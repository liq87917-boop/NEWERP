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
/// ERP-398 形式发票 PI 实时授权、权威客户范围与转换护栏的真实 SQL Server 集成测试（GUID 独占 NEWERP_AUTOTEST 目标）。
/// <list type="number">
/// <item><b>真实规则 + 真实控制器</b>：以既有「形式发票 PI」（<c>proforma-invoice</c>）与「销售订单」
/// （<c>sales-order</c>）菜单授权、既有业务员数据范围与真实 <see cref="ProformaInvoiceController"/> 验证
/// 本人 / 他人 / 无主 / 撤销菜单 / 禁用账号；伪造新增 / 修改 / 批量删除与转换一律 fail closed，被拒绝的请求不改写任何行。</item>
/// <item><b>两个独立连接竞态</b>：① 两个独立连接并发伪造新增越界客户 PI → 二者都 fail closed 且零落库；
/// ② 同一 PI 上「改写到越界客户」与「合法删除」并发 → 改写照旧拒绝、删除成功，归属与状态一致无撕裂。</item>
/// </list>
/// <para>安全口径：目标必须为专用 localdb 实例 <c>(localdb)\NEWERP_AutoAcceptance</c>、库名前缀 <c>NEWERP_AUTOTEST</c> 且
/// <c>Integrated Security</c>；每次运行只创建一个全新 GUID 后缀库，发现同名库已存在立即拒绝，绝不 drop / reset /
/// 复用任何数据库；连接串只来自进程环境变量或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据。</para>
/// <para>保留既有库存来源单据审计与原始失败日志：本测试只新增自己的证据行，不清理 / 不删除任何既有行。
/// 构建完成不等于阶段验收：本文件只有在受控 localdb 上真实执行通过才算验收证据。</para>
/// </summary>
public sealed class ProformaInvoiceAuthorizationSqlServerTests
    : IClassFixture<ProformaInvoiceAuthorizationSqlServerFixture>
{
    private readonly ProformaInvoiceAuthorizationSqlServerFixture _fixture;

    public ProformaInvoiceAuthorizationSqlServerTests(ProformaInvoiceAuthorizationSqlServerFixture fixture)
        => _fixture = fixture;

    /// <summary>访问数据库前复核专用目标护栏（实例名 / 库名前缀 / 集成安全）。</summary>
    private void Guard()
    {
        var target = new SqlConnectionStringBuilder(_fixture.ConnectionString);
        Assert.Equal("(localdb)\\NEWERP_AutoAcceptance", target.DataSource, ignoreCase: true);
        Assert.StartsWith(ProformaInvoiceAuthorizationSqlServerFixture.DatabasePrefix, target.InitialCatalog,
            StringComparison.OrdinalIgnoreCase);
        Assert.True(target.IntegratedSecurity);
    }

    // ==================== 控制器 / 双连接脚手架 ====================

    /// <summary>绑定真实 HttpContext 身份（可空 = 无身份）的真实 PI 控制器。</summary>
    private static ProformaInvoiceController NewController(ErpDbContext db, long? userId)
    {
        var http = new DefaultHttpContext();
        if (userId.HasValue)
            http.User = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }, "Test"));

        var controller = new ProformaInvoiceController(db, new DocumentNumberService(db));
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        return controller;
    }

    /// <summary>用一条独立连接执行控制器动作（每次调用各自 DbContext / 连接 / 事务），返回成功标志与错误。</summary>
    private async Task<(bool Success, string Error)> TryAsync(
        long? userId, Func<ProformaInvoiceController, Task<IActionResult>> action)
    {
        await using var db = _fixture.CreateDbContext();
        try
        {
            var result = await action(NewController(db, userId));
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

    private static string Tag() => Guid.NewGuid().ToString("N")[..8];

    // ==================== 种子（既有菜单 / 既有业务员数据范围，不新增权限模型） ====================

    private static async Task<BaseCustomer> SeedCustomerAsync(ErpDbContext db, string code, long? empId = null)
    {
        var customer = new BaseCustomer
        {
            CustomerCode = code, CustomerName = code, Status = 1, CreditStatus = "正常", EmpId = empId
        };
        db.BaseCustomers.Add(customer);
        await db.SaveChangesAsync();
        return customer;
    }

    /// <summary>播种受限业务员账号：登录名 = 员工编码（ERP-097 权威映射），并按需授予既有 PI / 销售订单菜单。</summary>
    private static async Task<(SysUser User, BaseEmployee Employee, SysRole Role)> SeedOperatorAsync(
        ErpDbContext db, bool piMenu = true, bool soMenu = true, UserStatus status = UserStatus.Enabled)
    {
        var code = $"INT_E398_EMP_{Tag()}";
        var employee = new BaseEmployee
        {
            EmployeeCode = code, EmployeeName = code, IsSalesman = true, Status = 1
        };
        db.BaseEmployees.Add(employee);
        await db.SaveChangesAsync();

        var user = new SysUser
        {
            UserName = code, PasswordHash = "hash", PasswordSalt = "salt", DisplayName = code, Status = status
        };
        db.SysUsers.Add(user);
        await db.SaveChangesAsync();

        var role = new SysRole
        {
            RoleName = $"INT_E398_ROLE_{code}", RoleCode = $"INT_E398_{Guid.NewGuid():N}", IsSystem = false
        };
        db.SysRoles.Add(role);
        await db.SaveChangesAsync();

        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        if (piMenu) await GrantMenuAsync(db, role.Id, ProformaInvoiceAuthorizationRules.RequiredMenuCode);
        if (soMenu) await GrantMenuAsync(db, role.Id, ProformaInvoiceAuthorizationRules.SalesOrderMenuCode);
        await db.SaveChangesAsync();
        return (user, employee, role);
    }

    private static async Task GrantMenuAsync(ErpDbContext db, long roleId, string menuCode)
    {
        var menu = await db.SysMenus.FirstAsync(m => !m.IsDeleted && m.MenuCode == menuCode);
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = roleId, MenuId = menu.Id });
    }

    private static async Task RevokeMenusAsync(ErpDbContext db, long roleId)
    {
        var grants = await db.SysRoleMenus.Where(rm => rm.RoleId == roleId && !rm.IsDeleted).ToListAsync();
        foreach (var grant in grants) grant.IsDeleted = true;
        await db.SaveChangesAsync();
    }

    private static async Task<ProformaInvoice> SeedPiAsync(ErpDbContext db, string no, long? customerId,
        DocumentStatus status = DocumentStatus.Pending, decimal amount = 1000m)
    {
        var pi = new ProformaInvoice
        {
            PiNo = no, PiDate = DateTime.Today, CustomerId = customerId, CustomerName = "集成客户",
            Currency = Currency.USD, ExchangeRate = 7.2m, DepositRatio = 30m,
            TotalAmount = amount, TotalAmountCny = amount * 7.2m, DepositAmount = amount * 0.3m,
            Status = status
        };
        pi.Details.Add(new ProformaInvoiceDetail
        {
            SortNo = 1, ProductCode = "INT-P1", ProductName = "集成商品", Unit = "PCS",
            Quantity = 10m, UnitPrice = amount / 10m, Amount = amount
        });
        db.ProformaInvoices.Add(pi);
        await db.SaveChangesAsync();
        return pi;
    }

    // ==================== 1. 真实身份 / 菜单授权（规则层） ====================

    [Fact]
    public async Task 真实身份_本人可访问_撤销菜单与禁用账号立即收敛()
    {
        Guard();
        var tag = Tag();
        long userId, roleId, customerId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var (user, employee, role) = await SeedOperatorAsync(seed);
            userId = user.Id;
            roleId = role.Id;
            customerId = (await SeedCustomerAsync(seed, $"INT_E398_C_{tag}", employee.Id)).Id;
        }

        await using (var db = _fixture.CreateDbContext())
        {
            var scope = await ProformaInvoiceAuthorizationRules.EnsureAuthorizedAsync(db, userId);
            Assert.False(scope.IsPrivileged);
            Assert.True(scope.AllowsCustomer(customerId));
        }

        await using (var revoke = _fixture.CreateDbContext())
            await RevokeMenusAsync(revoke, roleId);

        await using (var db = _fixture.CreateDbContext())
        {
            var ex = await Assert.ThrowsAsync<BusinessException>(
                () => ProformaInvoiceAuthorizationRules.EnsureAuthorizedAsync(db, userId));
            Assert.Equal(ErrorCodes.Forbidden, ex.Code);
        }
    }

    [Fact]
    public async Task 真实身份_禁用账号按权限不足拒绝()
    {
        Guard();
        long userId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var (user, _, _) = await SeedOperatorAsync(seed, status: UserStatus.Disabled);
            userId = user.Id;
        }

        await using var db = _fixture.CreateDbContext();
        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => ProformaInvoiceAuthorizationRules.EnsureAuthorizedAsync(db, userId));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    // ==================== 2. 真实控制器：读 / 写 / 批量越界 fail closed ====================

    [Fact]
    public async Task 列表详情打印_受限范围_越界与无主fail_closed()
    {
        Guard();
        var tag = Tag();
        long userId, ownId, foreignId, unlinkedId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var (user, employee, _) = await SeedOperatorAsync(seed);
            userId = user.Id;
            var own = await SeedCustomerAsync(seed, $"INT_E398_C_{tag}", employee.Id);
            var foreign = await SeedCustomerAsync(seed, $"INT_E398_F_{tag}");
            ownId = (await SeedPiAsync(seed, $"INT_E398_PI_OWN_{tag}", own.Id)).Id;
            foreignId = (await SeedPiAsync(seed, $"INT_E398_PI_FOREIGN_{tag}", foreign.Id)).Id;
            unlinkedId = (await SeedPiAsync(seed, $"INT_E398_PI_UNLINKED_{tag}", null)).Id;
        }

        Assert.True((await TryAsync(userId, c => c.GetById(ownId))).Success);
        Assert.True((await TryAsync(userId, c => c.GetPrint(ownId))).Success);
        Assert.False((await TryAsync(userId, c => c.GetById(foreignId))).Success);
        Assert.False((await TryAsync(userId, c => c.GetPrint(foreignId))).Success);
        Assert.False((await TryAsync(userId, c => c.GetById(unlinkedId))).Success);
    }

    [Fact]
    public async Task 伪造新增与修改_越界客户拒绝且不改写()
    {
        Guard();
        var tag = Tag();
        long userId, ownId, ownCustomerId, foreignCustomerId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var (user, employee, _) = await SeedOperatorAsync(seed);
            userId = user.Id;
            var own = await SeedCustomerAsync(seed, $"INT_E398_C_{tag}", employee.Id);
            var foreign = await SeedCustomerAsync(seed, $"INT_E398_F_{tag}");
            ownCustomerId = own.Id;
            foreignCustomerId = foreign.Id;
            ownId = (await SeedPiAsync(seed, $"INT_E398_PI_{tag}", own.Id)).Id;
        }

        var forged = new ProformaInvoice
        {
            PiNo = $"INT_E398_FORGED_{tag}", CustomerId = foreignCustomerId, CustomerName = "伪造客户",
            Details = new List<ProformaInvoiceDetail> { new() { Quantity = 1m, UnitPrice = 1m } }
        };
        Assert.False((await TryAsync(userId, c => c.Create(forged))).Success);

        var edit = new ProformaInvoice
        {
            CustomerId = foreignCustomerId, CustomerName = "改写客户",
            Details = new List<ProformaInvoiceDetail> { new() { Quantity = 10m, UnitPrice = 100m } }
        };
        Assert.False((await TryAsync(userId, c => c.Update(ownId, edit))).Success);

        await using (var verify = _fixture.CreateDbContext())
        {
            Assert.False(await verify.ProformaInvoices.AnyAsync(p => p.PiNo == $"INT_E398_FORGED_{tag}"));
            var stored = await verify.ProformaInvoices.AsNoTracking().SingleAsync(p => p.Id == ownId);
            Assert.Equal(ownCustomerId, stored.CustomerId);
            Assert.Equal("集成客户", stored.CustomerName);
            Assert.Equal(1000m, stored.TotalAmount);
            Assert.Equal(DocumentStatus.Pending, stored.Status);
        }
    }

    [Fact]
    public async Task 批量删除_混合越界_整体拒绝且不部分删除()
    {
        Guard();
        var tag = Tag();
        long userId, ownId, foreignId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var (user, employee, _) = await SeedOperatorAsync(seed);
            userId = user.Id;
            var own = await SeedCustomerAsync(seed, $"INT_E398_C_{tag}", employee.Id);
            var foreign = await SeedCustomerAsync(seed, $"INT_E398_F_{tag}");
            ownId = (await SeedPiAsync(seed, $"INT_E398_PI_OWN_{tag}", own.Id)).Id;
            foreignId = (await SeedPiAsync(seed, $"INT_E398_PI_FOREIGN_{tag}", foreign.Id)).Id;
        }

        Assert.False((await TryAsync(userId, c => c.BatchDelete(new List<long> { ownId, foreignId }))).Success);

        await using (var verify = _fixture.CreateDbContext())
        {
            Assert.False((await verify.ProformaInvoices.AsNoTracking().SingleAsync(p => p.Id == ownId)).IsDeleted);
            Assert.False((await verify.ProformaInvoices.AsNoTracking().SingleAsync(p => p.Id == foreignId)).IsDeleted);
        }
    }

    // ==================== 3. 转换护栏：来源-only / 目标-only 拒绝与合法放行 ====================

    [Fact]
    public async Task 转换_来源only与目标only拒绝_双权限合法放行且保留金额()
    {
        Guard();
        var tag = Tag();
        long soOnlyUserId, piOnlyUserId, bothUserId, soOnlyPiId, piOnlyPiId, okPiId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var (soOnly, soOnlyEmp, _) = await SeedOperatorAsync(seed, piMenu: false, soMenu: true);
            var (piOnly, piOnlyEmp, _) = await SeedOperatorAsync(seed, piMenu: true, soMenu: false);
            var (both, bothEmp, _) = await SeedOperatorAsync(seed);
            soOnlyUserId = soOnly.Id;
            piOnlyUserId = piOnly.Id;
            bothUserId = both.Id;

            var soOnlyCustomer = await SeedCustomerAsync(seed, $"INT_E398_SO_{tag}", soOnlyEmp.Id);
            var piOnlyCustomer = await SeedCustomerAsync(seed, $"INT_E398_PI_{tag}", piOnlyEmp.Id);
            var okCustomer = await SeedCustomerAsync(seed, $"INT_E398_OK_{tag}", bothEmp.Id);
            soOnlyPiId = (await SeedPiAsync(seed, $"INT_E398_CONV_SO_{tag}", soOnlyCustomer.Id,
                DocumentStatus.Approved)).Id;
            piOnlyPiId = (await SeedPiAsync(seed, $"INT_E398_CONV_PI_{tag}", piOnlyCustomer.Id,
                DocumentStatus.Approved)).Id;
            okPiId = (await SeedPiAsync(seed, $"INT_E398_CONV_OK_{tag}", okCustomer.Id,
                DocumentStatus.Approved)).Id;
        }

        // 仅有销售订单菜单 → 来源端（PI 菜单）拒绝，来源 PI 保持已审核、零订单。
        Assert.False((await TryAsync(soOnlyUserId, c => c.ToSalesOrder(soOnlyPiId))).Success);
        // 仅有 PI 菜单 → 目标端（销售订单菜单）拒绝，来源 PI 保持已审核、零订单。
        Assert.False((await TryAsync(piOnlyUserId, c => c.ToSalesOrder(piOnlyPiId))).Success);
        Assert.False((await TryAsync(piOnlyUserId, c => c.OrderPrefill(piOnlyPiId))).Success);

        // 双权限 + 本人客户 → 合法生成，保留原计算与来源留痕。
        Assert.True((await TryAsync(bothUserId, c => c.ToSalesOrder(okPiId))).Success);

        await using (var verify = _fixture.CreateDbContext())
        {
            Assert.Equal(DocumentStatus.Approved,
                (await verify.ProformaInvoices.AsNoTracking().SingleAsync(p => p.Id == soOnlyPiId)).Status);
            Assert.Equal(DocumentStatus.Approved,
                (await verify.ProformaInvoices.AsNoTracking().SingleAsync(p => p.Id == piOnlyPiId)).Status);
            var converted = await verify.ProformaInvoices.AsNoTracking().SingleAsync(p => p.Id == okPiId);
            Assert.Equal(DocumentStatus.Completed, converted.Status);

            var order = await verify.SalesOrders.AsNoTracking().SingleAsync(o => o.SourcePiId == okPiId);
            Assert.Equal(1000m, order.TotalAmount);
            Assert.Equal(300m, order.DepositAmount);
            Assert.Equal(Currency.USD, order.Currency);

            // 被拒绝的两次转换绝不落任何订单。
            Assert.Equal(0, await verify.SalesOrders.AsNoTracking()
                .CountAsync(o => o.SourcePiId == soOnlyPiId || o.SourcePiId == piOnlyPiId));
        }
    }

    // ==================== 4. 两个独立连接竞态 ====================

    [Fact]
    public async Task 竞态一_两连接并发伪造新增越界客户_双方拒绝且零落库()
    {
        Guard();
        var tag = Tag();
        long userId, foreignCustomerId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var (user, _, _) = await SeedOperatorAsync(seed);
            userId = user.Id;
            foreignCustomerId = (await SeedCustomerAsync(seed, $"INT_E398_F_{tag}")).Id;
        }

        var results = await RaceAsync(
            () => TryAsync(userId, c => c.Create(new ProformaInvoice
            {
                PiNo = $"INT_E398_RACE1_A_{tag}", CustomerId = foreignCustomerId, CustomerName = "越界 A",
                Details = new List<ProformaInvoiceDetail> { new() { Quantity = 1m, UnitPrice = 1m } }
            })),
            () => TryAsync(userId, c => c.Create(new ProformaInvoice
            {
                PiNo = $"INT_E398_RACE1_B_{tag}", CustomerId = foreignCustomerId, CustomerName = "越界 B",
                Details = new List<ProformaInvoiceDetail> { new() { Quantity = 1m, UnitPrice = 1m } }
            })));

        Assert.All(results, r => Assert.False(r.Success));
        await using (var verify = _fixture.CreateDbContext())
        {
            Assert.False(await verify.ProformaInvoices.AnyAsync(
                p => p.PiNo == $"INT_E398_RACE1_A_{tag}" || p.PiNo == $"INT_E398_RACE1_B_{tag}"));
        }
    }

    [Fact]
    public async Task 竞态二_改写到越界客户与合法删除并发_改写拒绝删除生效且无撕裂()
    {
        Guard();
        var tag = Tag();
        long userId, ownId, ownCustomerId, foreignCustomerId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var (user, employee, _) = await SeedOperatorAsync(seed);
            userId = user.Id;
            var own = await SeedCustomerAsync(seed, $"INT_E398_C_{tag}", employee.Id);
            var foreign = await SeedCustomerAsync(seed, $"INT_E398_F_{tag}");
            ownCustomerId = own.Id;
            foreignCustomerId = foreign.Id;
            ownId = (await SeedPiAsync(seed, $"INT_E398_RACE2_{tag}", own.Id)).Id;
        }

        var edit = new ProformaInvoice
        {
            CustomerId = foreignCustomerId, CustomerName = "越界改写",
            Details = new List<ProformaInvoiceDetail> { new() { Quantity = 10m, UnitPrice = 100m } }
        };
        var results = await RaceAsync(
            () => TryAsync(userId, c => c.Update(ownId, edit)),
            () => TryAsync(userId, c => c.Delete(ownId)));

        Assert.False(results[0].Success);   // 改写越界客户始终被拒
        Assert.True(results[1].Success);    // 合法删除在范围内 PI 上生效

        await using (var verify = _fixture.CreateDbContext())
        {
            var stored = await verify.ProformaInvoices.AsNoTracking().SingleAsync(p => p.Id == ownId);
            Assert.True(stored.IsDeleted);                     // 删除生效且未回滚
            Assert.Equal(ownCustomerId, stored.CustomerId);    // 归属从未被改写为越界客户（无撕裂）
            Assert.Equal("集成客户", stored.CustomerName);
        }
    }
}

/// <summary>
/// ERP-398 专用 localdb 夹具：目标必须是专用实例 <c>(localdb)\NEWERP_AutoAcceptance</c>、库名前缀
/// <c>NEWERP_AUTOTEST</c> 与 <c>Integrated Security=true</c>；每次运行只创建一个 <strong>全新 GUID 后缀库</strong>；
/// 发现同名库已存在立即拒绝，绝不 drop / reset / 复用任何数据库，也绝不读取 appsettings / .env / 生产凭据。
/// </summary>
public sealed class ProformaInvoiceAuthorizationSqlServerFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";
    public const string DefaultDatabaseName = "NEWERP_AUTOTEST_PIAUTHORIZATION_20261009";

    public string ConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = BuildDefaultConnectionString();

        AssertDedicatedTarget(connectionString);
        ConnectionString = connectionString;

        Console.WriteLine($"[ERP-398] 目标库护栏放行（实例 {InstanceMarker}，库名前缀 {DatabasePrefix}）。");

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
            // Never destroy a pre-existing fixture or another caller's database.
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

        Console.WriteLine("[ERP-398] 集成场景就绪：全新 GUID 库 + 完整 NEWERP 结构 + 种子数据（不清理既有行）。");
    }
}

/// <summary>专用目标护栏的 fail-closed 覆盖：错误实例 / 错误库名 / 非集成安全必须在访问数据库之前被拒绝。</summary>
public sealed class ProformaInvoiceAuthorizationTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;User Id=sa;Password=secret")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(
            () => ProformaInvoiceAuthorizationSqlServerFixture.AssertDedicatedTarget(connection));
}
