using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using Xunit;

namespace ERP.IntegrationTests;

/// <summary>
/// ERP-421 规范销售订单普通写入（修改 / 删除 / 提交 / 审核）的**确定性锁协议**真实 SQL Server 集成测试
/// （GUID 独占 <c>NEWERP_AUTOTEST</c> 目标）。
/// <para>直接执行<b>真实业务代码</b>（<see cref="SalesOrderController"/> + <see cref="SalesOrderMutationRules"/> +
/// <see cref="SalesOrderSourceLineageRules"/>），不复制测试专用实现：</para>
/// <list type="number">
/// <item><b>两条独立连接竞态</b>：手工订单「编辑 vs 提交」「编辑 vs 删除」、手工订单「提交 vs 删除」、
/// 「审核 vs 取消」，均只有一致结果（受控失败方 + 唯一合法终态 + 完整明细集 + 无孤儿 / 不复活）；</item>
/// <item><b>关联来源与无法解析历史来源</b>的状态竞态复用同一把订单行锁，来源证据不被静默改写；</item>
/// <item><b>强制明细替换保存失败</b>：在同一原子事务内「删除旧明细 + 插入新明细」的插入被数据库约束拒绝后，
/// 表头 / 明细 / 状态整体回滚，绝不残留半成品变更（并清除变更跟踪器）。</item>
/// </list>
/// <para>安全口径：目标必须为专用 localdb 实例 <c>(localdb)\NEWERP_AutoAcceptance</c>、库名前缀 <c>NEWERP_AUTOTEST</c>
/// 且集成安全；每次运行只创建<b>全新 GUID 后缀库</b>，发现同名库已存在立即拒绝；绝不 drop / reset / 复用任何数据库，
/// 连接串只来自进程环境变量或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据。
/// <b>构建完成不等于阶段验收</b>：只有以下场景在专用 localdb 上真实执行通过才算验收证据。</para>
/// </summary>
public sealed class SalesOrderMutationSqlServerTests : IClassFixture<SalesOrderMutationSqlServerFixture>
{
    private readonly SalesOrderMutationSqlServerFixture _fixture;

    public SalesOrderMutationSqlServerTests(SalesOrderMutationSqlServerFixture fixture)
        => _fixture = fixture;

    /// <summary>访问数据库前复核专用目标护栏（实例名 / 库名前缀 / 集成安全）。</summary>
    private void Guard()
    {
        var target = new SqlConnectionStringBuilder(_fixture.ConnectionString);
        Assert.Equal("(localdb)\\NEWERP_AutoAcceptance", target.DataSource, ignoreCase: true);
        Assert.StartsWith(SalesOrderMutationSqlServerFixture.DatabasePrefix, target.InitialCatalog,
            StringComparison.OrdinalIgnoreCase);
        Assert.True(target.IntegratedSecurity);
    }

    // ==================== 控制器 / 双连接脚手架 ====================

    private static string Tag() => Guid.NewGuid().ToString("N")[..8];

    private static SalesOrderController NewSalesOrderController(ErpDbContext db, long? userId)
        => new(db, new DocumentNumberService(db))
        {
            ControllerContext = new ControllerContext { HttpContext = HttpFor(userId) }
        };

    private static DefaultHttpContext HttpFor(long? userId)
    {
        var http = new DefaultHttpContext();
        if (userId.HasValue)
            http.User = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }, "Test"));
        return http;
    }

    private async Task<(bool Success, string Error)> TryUpdateAsync(long? userId, long orderId, SalesOrder body)
    {
        await using var db = _fixture.CreateDbContext();
        try
        {
            var result = await NewSalesOrderController(db, userId).Update(orderId, body);
            return (result is OkObjectResult, string.Empty);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private async Task<(bool Success, string Error)> TrySubmitAsync(long? userId, long orderId)
    {
        await using var db = _fixture.CreateDbContext();
        try
        {
            var result = await NewSalesOrderController(db, userId).Submit(orderId);
            return (result is OkObjectResult, string.Empty);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private async Task<(bool Success, string Error)> TryApproveAsync(long? userId, long orderId)
    {
        await using var db = _fixture.CreateDbContext();
        try
        {
            var result = await NewSalesOrderController(db, userId).Approve(orderId);
            return (result is OkObjectResult, string.Empty);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private async Task<(bool Success, string Error)> TryDeleteAsync(long? userId, long orderId)
    {
        await using var db = _fixture.CreateDbContext();
        try
        {
            var result = await NewSalesOrderController(db, userId).Delete(orderId);
            return (result is OkObjectResult, string.Empty);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private async Task<(bool Success, string Error)> TryCancelAsync(long? userId, long orderId)
    {
        await using var db = _fixture.CreateDbContext();
        try
        {
            var result = await NewSalesOrderController(db, userId).Cancel(orderId);
            return (result is OkObjectResult, string.Empty);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>两条独立连接以同一起跑线并发执行（门闩对齐），返回两侧结果。</summary>
    private static async Task<List<(bool Success, string Error)>> RaceAsync(
        Func<Task<(bool Success, string Error)>> first, Func<Task<(bool Success, string Error)>> second)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<(bool Success, string Error)> Run(Func<Task<(bool Success, string Error)>> action)
        {
            await gate.Task;
            return await action();
        }

        var left = Run(first);
        var right = Run(second);
        gate.SetResult();
        return (await Task.WhenAll(left, right)).ToList();
    }

    private async Task ExecuteRawAsync(string sql)
    {
        await using var conn = new SqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    // ==================== 1. 两条独立连接：手工订单 编辑 vs 提交 ====================

    [Fact]
    public async Task 两条独立连接_手工订单编辑与提交_受控失败_唯一合法终态_完整明细()
    {
        Guard();
        var tag = Tag();
        long userId, customerId, orderId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var (user, employee) = await SeedOperatorAsync(seed);
            userId = user.Id;
            var customer = await SeedCustomerAsync(seed, $"INT_E421_A_{tag}", employee.Id);
            customerId = customer.Id;
            orderId = (await SeedOrderAsync(seed, customer.Id, DocumentStatus.Pending)).Id;
        }

        var results = await RaceAsync(
            () => TryUpdateAsync(userId, orderId, NewBody(customerId)),
            () => TrySubmitAsync(userId, orderId));

        // 提交必然成功（受控失败方只可能是编辑：提交先行后编辑读到已提交状态并原子拒绝）。
        Assert.True(results[1].Success);

        await using var verify = _fixture.CreateDbContext();
        var order = await verify.SalesOrders.AsNoTracking().SingleAsync(o => o.Id == orderId);
        Assert.Equal(DocumentStatus.Submitted, order.Status);   // 唯一合法终态
        Assert.False(order.IsDeleted);

        var details = await verify.SalesOrderDetails.AsNoTracking()
            .Where(d => d.SalesOrderId == orderId).ToListAsync();
        Assert.Single(details);                                  // 完整明细集（绝无孤儿 / 撕裂行）
        Assert.Equal(orderId, details[0].SalesOrderId);
        if (results[0].Success)
        {
            Assert.Equal("改后商品", details[0].ProductName);
            Assert.Equal(1200m, order.TotalAmount);
        }
        else
        {
            Assert.Equal("集成商品", details[0].ProductName);
            Assert.Equal(1000m, order.TotalAmount);
        }
    }

    // ==================== 2. 两条独立连接：手工订单 编辑 vs 删除 ====================

    [Fact]
    public async Task 两条独立连接_手工订单编辑与删除_删除恒成功_编辑受控失败()
    {
        Guard();
        var tag = Tag();
        long userId, customerId, orderId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var (user, employee) = await SeedOperatorAsync(seed);
            userId = user.Id;
            var customer = await SeedCustomerAsync(seed, $"INT_E421_B_{tag}", employee.Id);
            customerId = customer.Id;
            orderId = (await SeedOrderAsync(seed, customer.Id, DocumentStatus.Pending)).Id;
        }

        var results = await RaceAsync(
            () => TryUpdateAsync(userId, orderId, NewBody(customerId)),
            () => TryDeleteAsync(userId, orderId));

        Assert.True(results[1].Success);   // 删除恒成功；编辑只有在先取得锁时才成功

        await using var verify = _fixture.CreateDbContext();
        var order = await verify.SalesOrders.AsNoTracking().SingleAsync(o => o.Id == orderId);
        Assert.True(order.IsDeleted);      // 软删除不可复活
        Assert.Equal(DocumentStatus.Pending, order.Status);

        var details = await verify.SalesOrderDetails.AsNoTracking()
            .Where(d => d.SalesOrderId == orderId).ToListAsync();
        Assert.Single(details);            // 明细集完整（编辑先行则替换，否则保持原样）
        Assert.Equal(results[0].Success ? "改后商品" : "集成商品", details[0].ProductName);
    }

    // ==================== 3. 两条独立连接：手工订单 提交 vs 删除（互斥） ====================

    [Fact]
    public async Task 两条独立连接_手工订单提交与删除_恰好一方成功()
    {
        Guard();
        var tag = Tag();
        long userId, customerId, orderId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var (user, employee) = await SeedOperatorAsync(seed);
            userId = user.Id;
            var customer = await SeedCustomerAsync(seed, $"INT_E421_C_{tag}", employee.Id);
            customerId = customer.Id;
            orderId = (await SeedOrderAsync(seed, customer.Id, DocumentStatus.Pending)).Id;
        }

        var results = await RaceAsync(
            () => TrySubmitAsync(userId, orderId),
            () => TryDeleteAsync(userId, orderId));

        Assert.Equal(1, results.Count(r => r.Success));   // 互斥：恰好一方成功

        await using var verify = _fixture.CreateDbContext();
        var order = await verify.SalesOrders.AsNoTracking().SingleAsync(o => o.Id == orderId);
        if (results[0].Success)
        {
            Assert.Equal(DocumentStatus.Submitted, order.Status);
            Assert.False(order.IsDeleted);
        }
        else
        {
            Assert.Equal(DocumentStatus.Pending, order.Status);
            Assert.True(order.IsDeleted);
        }

        Assert.Equal(1, await verify.SalesOrderDetails.AsNoTracking()
            .CountAsync(d => d.SalesOrderId == orderId));
    }

    // ==================== 4. 两条独立连接：审核 vs 取消 ====================

    [Fact]
    public async Task 两条独立连接_审核与取消_取消恒成功_唯一合法终态()
    {
        Guard();
        var tag = Tag();
        long userId, customerId, orderId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var (user, employee) = await SeedOperatorAsync(seed);
            userId = user.Id;
            var customer = await SeedCustomerAsync(seed, $"INT_E421_D_{tag}", employee.Id);
            customerId = customer.Id;
            orderId = (await SeedOrderAsync(seed, customer.Id, DocumentStatus.Submitted)).Id;
        }

        var results = await RaceAsync(
            () => TryApproveAsync(userId, orderId),
            () => TryCancelAsync(userId, orderId));

        Assert.True(results[1].Success);   // 取消恒成功；审核只有在先取得锁并提交后才可能成功

        await using var verify = _fixture.CreateDbContext();
        var order = await verify.SalesOrders.AsNoTracking().SingleAsync(o => o.Id == orderId);
        Assert.Equal(DocumentStatus.Cancelled, order.Status);   // 唯一合法终态
        Assert.False(order.IsDeleted);
        Assert.Equal(1, await verify.SalesOrderDetails.AsNoTracking()
            .CountAsync(d => d.SalesOrderId == orderId));
    }

    // ==================== 5. 两条独立连接：关联来源订单的状态竞态 ====================

    [Fact]
    public async Task 两条独立连接_关联来源订单提交与取消_来源绑定不被静默改写()
    {
        Guard();
        var tag = Tag();
        long userId, customerId, orderId, piId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var (user, employee) = await SeedOperatorAsync(seed);
            userId = user.Id;
            var customer = await SeedCustomerAsync(seed, $"INT_E421_H_{tag}", employee.Id);
            customerId = customer.Id;
            var pi = await SeedPiAsync(seed, $"INT_E421_PI_{tag}", customer.Id, DocumentStatus.Approved);
            piId = pi.Id;
            orderId = (await SeedOrderAsync(seed, customer.Id, DocumentStatus.Pending, pi.Id, pi.PiNo)).Id;
        }

        var results = await RaceAsync(
            () => TrySubmitAsync(userId, orderId),
            () => TryCancelAsync(userId, orderId));

        Assert.True(results[1].Success);

        await using var verify = _fixture.CreateDbContext();
        var order = await verify.SalesOrders.AsNoTracking().SingleAsync(o => o.Id == orderId);
        Assert.Equal(DocumentStatus.Cancelled, order.Status);
        Assert.Equal(piId, order.SourcePiId);   // 来源绑定原样保留，绝不静默清除

        var storedPi = await verify.ProformaInvoices.AsNoTracking().SingleAsync(p => p.Id == piId);
        Assert.Equal(DocumentStatus.Approved, storedPi.Status);   // 来源状态不被伪造
        Assert.False(storedPi.IsDeleted);
    }

    // ==================== 6. 两条独立连接：无法解析历史来源的状态竞态 ====================

    [Fact]
    public async Task 两条独立连接_无法解析历史来源订单提交与取消_历史值原样保留()
    {
        Guard();
        const long missingPiId = 9_999_999L;
        var tag = Tag();
        long userId, customerId, orderId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var (user, employee) = await SeedOperatorAsync(seed);
            userId = user.Id;
            var customer = await SeedCustomerAsync(seed, $"INT_E421_I_{tag}", employee.Id);
            customerId = customer.Id;
            orderId = (await SeedOrderAsync(seed, customer.Id, DocumentStatus.Pending, missingPiId,
                "INT_E421_LEGACY")).Id;
        }

        var results = await RaceAsync(
            () => TrySubmitAsync(userId, orderId),
            () => TryCancelAsync(userId, orderId));

        Assert.True(results[1].Success);

        await using var verify = _fixture.CreateDbContext();
        var order = await verify.SalesOrders.AsNoTracking().SingleAsync(o => o.Id == orderId);
        Assert.Equal(DocumentStatus.Cancelled, order.Status);
        Assert.Equal(missingPiId, order.SourcePiId);          // 无法解析的历史来源原样保留
        Assert.Equal("INT_E421_LEGACY", order.SourcePiNo);
        Assert.False(await verify.ProformaInvoices.AsNoTracking().AnyAsync(p => p.Id == missingPiId));
    }

    // ==================== 7. 强制明细替换保存失败：整体回滚 ====================

    [Fact]
    public async Task 明细替换保存失败_头与明细整体回滚且不留半成品()
    {
        Guard();
        var tag = Tag();
        long userId, customerId, orderId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var (user, employee) = await SeedOperatorAsync(seed);
            userId = user.Id;
            var customer = await SeedCustomerAsync(seed, $"INT_E421_J_{tag}", employee.Id);
            customerId = customer.Id;
            orderId = (await SeedOrderAsync(seed, customer.Id, DocumentStatus.Pending)).Id;
        }

        // 在专用 GUID 测试库上强制「删除旧明细 + 插入新明细」的插入被数据库 CHECK 约束拒绝。
        await ExecuteRawAsync(
            "ALTER TABLE db_owner.SalesOrderDetails WITH NOCHECK ADD CONSTRAINT "
            + $"CK_ERP421_FAIL_{tag} CHECK (ProductName <> N'ERP421_FORCE_FAIL');");

        var result = await TryUpdateAsync(userId, orderId,
            NewBody(customerId, productName: "ERP421_FORCE_FAIL"));
        Assert.False(result.Success);

        await using var verify = _fixture.CreateDbContext();
        var order = await verify.SalesOrders.AsNoTracking().SingleAsync(o => o.Id == orderId);
        Assert.Equal(DocumentStatus.Pending, order.Status);   // 表头 / 状态零改动
        Assert.False(order.IsDeleted);
        Assert.Equal(1000m, order.TotalAmount);

        var details = await verify.SalesOrderDetails.AsNoTracking()
            .Where(d => d.SalesOrderId == orderId).ToListAsync();
        Assert.Single(details);                               // 旧明细仍在，绝不残留「删除 + 插入」的半成品
        Assert.Equal("集成商品", details[0].ProductName);
        Assert.Equal(1000m, details[0].Amount);
    }

    // ==================== 种子（既有菜单 / 既有业务员数据范围，不新增权限模型） ====================

    private static async Task<(SysUser User, BaseEmployee Employee)> SeedOperatorAsync(ErpDbContext db)
    {
        var code = $"INT_E421_{Guid.NewGuid():N}";
        var employee = new BaseEmployee
        {
            EmployeeCode = code, EmployeeName = code, IsSalesman = true, Status = 1
        };
        db.BaseEmployees.Add(employee);
        await db.SaveChangesAsync();

        var user = new SysUser
        {
            UserName = code, PasswordHash = "hash", PasswordSalt = "salt", DisplayName = code,
            Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        await db.SaveChangesAsync();

        var role = new SysRole
        {
            RoleName = $"E421R_{code}", RoleCode = $"INT_E421_{Guid.NewGuid():N}", IsSystem = false
        };
        db.SysRoles.Add(role);
        await db.SaveChangesAsync();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });

        // 复用既有「销售订单」菜单授权（绝不新增菜单 / 角色）。
        var menu = await db.SysMenus.FirstAsync(m => !m.IsDeleted
            && m.MenuCode == SalesOrderSourceLineageRules.SalesOrderMenuCode);
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
        await db.SaveChangesAsync();
        return (user, employee);
    }

    private static async Task<BaseCustomer> SeedCustomerAsync(ErpDbContext db, string code, long employeeId)
    {
        var customer = new BaseCustomer
        {
            CustomerCode = code, CustomerName = code, DepositRatio = 30m, EmpId = employeeId
        };
        db.BaseCustomers.Add(customer);
        await db.SaveChangesAsync();
        return customer;
    }

    private static async Task<ProformaInvoice> SeedPiAsync(ErpDbContext db, string no, long customerId,
        DocumentStatus status)
    {
        var pi = new ProformaInvoice
        {
            PiNo = no, PiDate = DateTime.Today, CustomerId = customerId, CustomerName = "集成客户",
            Currency = Currency.USD, ExchangeRate = 7.2m, DepositRatio = 30m,
            TotalAmount = 1000m, TotalAmountCny = 7200m, DepositAmount = 300m, Status = status
        };
        db.ProformaInvoices.Add(pi);
        await db.SaveChangesAsync();
        return pi;
    }

    private static async Task<SalesOrder> SeedOrderAsync(ErpDbContext db, long customerId,
        DocumentStatus status, long? piId = null, string piNo = "")
    {
        var order = new SalesOrder
        {
            OrderNo = $"INT_E421_{Guid.NewGuid():N}", OrderDate = DateTime.Today,
            CustomerId = customerId, SalesmanId = 1L, Currency = Currency.USD, ExchangeRate = 7.2m,
            DepositRatio = 30m, DepositAmount = 300m, TotalAmount = 1000m, Status = status,
            SourcePiId = piId, SourcePiNo = piNo,
            Details = new List<SalesOrderDetail>
            {
                new()
                {
                    ProductId = 21, ProductName = "集成商品", Spec = "标准", Unit = "PCS",
                    Quantity = 100m, UnitPrice = 10m, Amount = 1000m
                }
            }
        };
        db.SalesOrders.Add(order);
        await db.SaveChangesAsync();
        return order;
    }

    private static SalesOrder NewBody(long customerId, decimal quantity = 20m, decimal unitPrice = 60m,
        string productName = "改后商品")
        => new()
        {
            OrderDate = DateTime.Today, CustomerId = customerId, SalesmanId = 1L, Currency = Currency.USD,
            ExchangeRate = 7.2m, DepositRatio = 30m,
            Details = new List<SalesOrderDetail>
            {
                new()
                {
                    ProductId = 100, ProductName = productName, Spec = "标准", Unit = "PCS",
                    Quantity = quantity, UnitPrice = unitPrice
                }
            }
        };
}

/// <summary>
/// ERP-421 专用 localdb 夹具：目标必须是专用实例 <c>(localdb)\NEWERP_AutoAcceptance</c>、库名前缀
/// <c>NEWERP_AUTOTEST</c> 且 <c>Integrated Security=true</c>；每次运行只创建一个<strong>全新 GUID 后缀库</strong>，
/// 发现同名库已存在立即拒绝；绝不 drop / reset / 复用任何数据库；连接串只来自进程环境变量
/// <c>ERP_ConnectionStrings__Default</c> 或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据。
/// </summary>
public sealed class SalesOrderMutationSqlServerFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";
    public const string DefaultDatabaseName = "NEWERP_AUTOTEST_SOMUTATION_20261009";

    public string ConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = BuildDefaultConnectionString();

        AssertDedicatedTarget(connectionString);
        ConnectionString = connectionString;

        Console.WriteLine($"[ERP-421] 目标库护栏放行（实例 {InstanceMarker}，库名前缀 {DatabasePrefix}）。");

        await CreateFreshDatabaseAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public ErpDbContext CreateDbContext() => new(BuildOptions());

    private DbContextOptions<ErpDbContext> BuildOptions()
        => new DbContextOptionsBuilder<ErpDbContext>().UseSqlServer(ConnectionString).Options;

    private static string BuildDefaultConnectionString()
        => $"Server=(localdb)\\{InstanceMarker};Initial Catalog={DefaultDatabaseName}_{Guid.NewGuid():N};"
           + "Integrated Security=true;TrustServerCertificate=true;";

    internal static void AssertDedicatedTarget(string connectionString)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        var server = builder.DataSource ?? string.Empty;
        var database = builder.InitialCatalog ?? string.Empty;

        Assert.Equal($"(localdb)\\{InstanceMarker}", server, ignoreCase: true);
        Assert.StartsWith(DatabasePrefix, database, StringComparison.OrdinalIgnoreCase);
        Assert.True(builder.IntegratedSecurity);
    }

    private async Task CreateFreshDatabaseAsync()
    {
        var database = new SqlConnectionStringBuilder(ConnectionString).InitialCatalog;

        // 任何库访问 / 建库之前再次护栏：绝不使用生产或非专用目标。
        AssertDedicatedTarget(ConnectionString);

        var master = new SqlConnectionStringBuilder(ConnectionString) { InitialCatalog = "master" };
        await using (var conn = new SqlConnection(master.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            // 绝不销毁已存在的夹具库或其它调用方的数据库。
            cmd.CommandText = "SELECT DB_ID(@database)";
            cmd.Parameters.AddWithValue("@database", database);
            var existing = await cmd.ExecuteScalarAsync();
            if (existing is not null && existing != DBNull.Value)
                throw new InvalidOperationException(
                    "The isolated fixture database already exists; choose a fresh NEWERP_AUTOTEST database.");
        }

        await using (var db = CreateDbContext())
        {
            await db.Database.EnsureCreatedAsync();
            await SchemaUpgrader.EnsureUpgradedAsync(db);
            await SeedData.InitializeAsync(db);
            await SchemaUpgrader.EnsureUpgradedAsync(db);
        }

        Console.WriteLine("[ERP-421] 集成场景就绪：全新 GUID 库 + 完整 NEWERP 结构 + 种子数据（不清理既有行）。");
    }
}

/// <summary>专用目标护栏的 fail-closed 覆盖：错误实例 / 错误库名 / 非集成安全必须在访问数据库之前被拒绝。</summary>
public sealed class SalesOrderMutationTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;User Id=sa;Password=secret")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(
            () => SalesOrderMutationSqlServerFixture.AssertDedicatedTarget(connection));
}