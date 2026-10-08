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
/// ERP-399 形式发票 PI 生命周期与「PI → 销售订单」转换的确定性来源行锁 / 原子事务 / 锁内权威复核
/// 真实 SQL Server 集成测试（GUID 独占 <c>NEWERP_AUTOTEST</c> 目标）。
/// <list type="number">
/// <item><b>真实规则 + 真实控制器 + 真实既有授权</b>：以既有「形式发票 PI」（<c>proforma-invoice</c>）与
/// 「销售订单」（<c>sales-order</c>）菜单授权 + 既有业务员数据范围驱动真实 <see cref="ProformaInvoiceController"/>；
/// 不新增任何菜单 / 角色 / 用户授权，无匿名 / 管理员降级。</item>
/// <item><b>两个独立连接竞态</b>（每条用例两个独立 DbContext / 连接 / 事务）：
/// 转换 / 转换、转换 / 作废、转换 / 销审、编辑 / 审核、混合批量删除 —— 恰好一个合法赢家且失败侧整体回滚，
/// 来源状态与目标订单绝不撕裂。</item>
/// <item><b>失败原子回滚</b>：销售订单编号生成失败时整体回滚（不落半成品订单、不改来源状态、不占号）。</item>
/// <item><b>权威一致性</b>：落库订单的数量 / 币种 / 汇率 / 合计 / 定金 / 显式 <c>SourcePiId</c> 与来源 PI 完全一致，
/// 且不产生任何财务 / 库存过账。</item>
/// </list>
/// <para>安全口径：目标必须为专用 localdb 实例 <c>(localdb)\NEWERP_AutoAcceptance</c>、库名前缀 <c>NEWERP_AUTOTEST</c>
/// 且 <c>Integrated Security</c>；每次运行只创建一个全新 GUID 后缀库，发现同名库已存在立即拒绝，
/// 绝不 drop / reset / 复用任何数据库；连接串只来自进程环境变量或专用 localdb 默认值，绝不读取
/// <c>appsettings*.json</c> / <c>.env</c> / 生产凭据。</para>
/// <para>保留既有库存来源单据审计与原始失败日志：本测试只新增自己的证据行，不清理 / 不删除任何既有行
/// （被拒绝的请求只回滚自己的写入）。构建完成不等于阶段验收：本文件只有在受控 localdb 上真实执行通过才算验收证据。</para>
/// </summary>
public sealed class ProformaInvoiceMutationSqlServerTests
    : IClassFixture<ProformaInvoiceMutationSqlServerFixture>
{
    private readonly ProformaInvoiceMutationSqlServerFixture _fixture;

    public ProformaInvoiceMutationSqlServerTests(ProformaInvoiceMutationSqlServerFixture fixture)
        => _fixture = fixture;

    /// <summary>访问数据库前复核专用目标护栏（实例名 / 库名前缀 / 集成安全）。</summary>
    private void Guard()
    {
        var target = new SqlConnectionStringBuilder(_fixture.ConnectionString);
        Assert.Equal("(localdb)\\NEWERP_AutoAcceptance", target.DataSource, ignoreCase: true);
        Assert.StartsWith(ProformaInvoiceMutationSqlServerFixture.DatabasePrefix, target.InitialCatalog,
            StringComparison.OrdinalIgnoreCase);
        Assert.True(target.IntegratedSecurity);
    }

    // ==================== 控制器 / 双连接脚手架 ====================

    private static string Tag() => Guid.NewGuid().ToString("N")[..8];

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

    /// <summary>用一条独立连接执行控制器动作（各自 DbContext / 连接 / 事务），返回成功标志与错误。</summary>
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

    // ==================== 种子（既有菜单 / 既有业务员数据范围，不新增权限模型） ====================

    /// <summary>播种受限操作员：登录账号 = 员工编码（ERP-097 权威映射），并按需授予既有 PI / 销售订单菜单。</summary>
    private static async Task<(SysUser User, BaseEmployee Employee, SysRole Role)> SeedOperatorAsync(
        ErpDbContext db, bool piMenu = true, bool soMenu = true, UserStatus status = UserStatus.Enabled)
    {
        var code = $"INT_E399_{Guid.NewGuid():N}";
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
            RoleName = $"E399R_{code}", RoleCode = $"INT_E399_{Guid.NewGuid():N}", IsSystem = false
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

    private static async Task<BaseCustomer> SeedCustomerAsync(ErpDbContext db, string code, long? empId = null)
    {
        var customer = new BaseCustomer
        {
            CustomerCode = code, CustomerName = "集成客户", Status = 1, CreditStatus = "正常", EmpId = empId
        };
        db.BaseCustomers.Add(customer);
        await db.SaveChangesAsync();
        return customer;
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
            SortNo = 1, ProductCode = "INT-E399-P1", ProductName = "集成商品", Unit = "PCS",
            Quantity = 10m, UnitPrice = amount / 10m, Amount = amount
        });
        db.ProformaInvoices.Add(pi);
        await db.SaveChangesAsync();
        return pi;
    }

    // ==================== 1. 真实既有授权下的合法转换 ====================

    [Fact]
    public async Task 真实既有授权_无身份与缺菜单与越界一律拒绝_双菜单本人才可转换且口径一致()
    {
        Guard();
        var tag = Tag();
        long bothUserId, piOnlyUserId, ownPiId, foreignPiId, ownCustomerId;
        int stockBefore, receiptBefore;

        await using (var seed = _fixture.CreateDbContext())
        {
            var (both, bothEmp, _) = await SeedOperatorAsync(seed);                        // PI + 销售订单菜单
            var (piOnly, _, _) = await SeedOperatorAsync(seed, piMenu: true, soMenu: false);
            bothUserId = both.Id;
            piOnlyUserId = piOnly.Id;

            var own = await SeedCustomerAsync(seed, $"INT_E399_OWN_{tag}", bothEmp.Id);
            var foreign = await SeedCustomerAsync(seed, $"INT_E399_FR_{tag}");
            ownCustomerId = own.Id;
            ownPiId = (await SeedPiAsync(seed, $"INT_E399_PI_OWN_{tag}", own.Id, DocumentStatus.Approved)).Id;
            foreignPiId = (await SeedPiAsync(seed, $"INT_E399_PI_FR_{tag}", foreign.Id, DocumentStatus.Approved)).Id;

            stockBefore = await seed.StockOuts.CountAsync();
            receiptBefore = await seed.FinanceReceipts.CountAsync();
        }

        // 无身份 / 缺既有「销售订单」菜单 / 越界（他人客户）一律 fail closed。
        Assert.False((await TryAsync(null, c => c.ToSalesOrder(ownPiId))).Success);
        Assert.False((await TryAsync(piOnlyUserId, c => c.ToSalesOrder(ownPiId))).Success);
        Assert.False((await TryAsync(bothUserId, c => c.ToSalesOrder(foreignPiId))).Success);

        await using (var verify = _fixture.CreateDbContext())
        {
            Assert.False(await verify.SalesOrders.AnyAsync(o => o.SourcePiId == ownPiId || o.SourcePiId == foreignPiId));
            Assert.Equal(DocumentStatus.Approved,
                (await verify.ProformaInvoices.AsNoTracking().SingleAsync(p => p.Id == ownPiId)).Status);
            Assert.Equal(DocumentStatus.Approved,
                (await verify.ProformaInvoices.AsNoTracking().SingleAsync(p => p.Id == foreignPiId)).Status);
        }

        // 双菜单 + 本人客户：放行；落库订单与来源权威口径逐项一致，且无财务 / 库存过账。
        Assert.True((await TryAsync(bothUserId, c => c.ToSalesOrder(ownPiId))).Success);

        await using (var verify = _fixture.CreateDbContext())
        {
            var order = await verify.SalesOrders.AsNoTracking().Include(o => o.Details)
                .SingleAsync(o => o.SourcePiId == ownPiId);
            var pi = await verify.ProformaInvoices.AsNoTracking().Include(o => o.Details)
                .SingleAsync(p => p.Id == ownPiId);

            Assert.Equal(DocumentStatus.Completed, pi.Status);
            Assert.Equal(ownCustomerId, order.CustomerId);
            Assert.Equal(pi.Id, order.SourcePiId);
            Assert.Equal(pi.PiNo, order.SourcePiNo);
            Assert.Equal(pi.Currency, order.Currency);
            Assert.Equal(pi.ExchangeRate, order.ExchangeRate);
            Assert.Equal(pi.TotalAmount, order.TotalAmount);
            Assert.Equal(pi.DepositAmount, order.DepositAmount);
            Assert.Equal(pi.Details.Where(d => !d.IsDeleted).Sum(d => d.Quantity), order.Details.Sum(d => d.Quantity));

            Assert.Equal(stockBefore, await verify.StockOuts.CountAsync());
            Assert.Equal(receiptBefore, await verify.FinanceReceipts.CountAsync());
        }
    }

    // ==================== 2. 双连接竞态（恰好一个合法赢家 + 整体回滚） ====================

    /// <summary>播种一位双菜单受限操作员 + 其本人客户 + 一张 PI（返回用户 / 客户 / PI）。</summary>
    private async Task<(long UserId, long CustomerId, long PiId)> SeedOwnedPiAsync(string tag,
        DocumentStatus status = DocumentStatus.Approved)
    {
        await using var seed = _fixture.CreateDbContext();
        var (user, employee, _) = await SeedOperatorAsync(seed);
        var customer = await SeedCustomerAsync(seed, $"INT_E399_C_{tag}", employee.Id);
        var pi = await SeedPiAsync(seed, $"INT_E399_PI_{tag}", customer.Id, status);
        return (user.Id, customer.Id, pi.Id);
    }

    [Fact]
    public async Task 转换并发_两条独立连接只生成一张完整销售订单_另一侧整体回滚()
    {
        Guard();
        var (userId, _, piId) = await SeedOwnedPiAsync($"RACE_CC_{Tag()}");

        var results = await RaceAsync(
            () => TryAsync(userId, c => c.ToSalesOrder(piId)),
            () => TryAsync(userId, c => c.ToSalesOrder(piId)));

        Assert.Equal(1, results.Count(r => r.Success));

        await using var verify = _fixture.CreateDbContext();
        var orders = await verify.SalesOrders.AsNoTracking().Include(o => o.Details)
            .Where(o => o.SourcePiId == piId).ToListAsync();
        Assert.Single(orders);
        Assert.Single(orders[0].Details);
        Assert.Equal(1000m, orders[0].TotalAmount);
        Assert.Equal(300m, orders[0].DepositAmount);
        Assert.Equal(Currency.USD, orders[0].Currency);
        Assert.Equal(piId, orders[0].SourcePiId);

        var pi = await verify.ProformaInvoices.AsNoTracking().SingleAsync(p => p.Id == piId);
        Assert.Equal(DocumentStatus.Completed, pi.Status);
    }

    [Fact]
    public async Task 转换与作废并发_恰好一个合法赢家_来源状态与目标订单绝不撕裂()
    {
        Guard();
        var (userId, _, piId) = await SeedOwnedPiAsync($"RACE_CV_{Tag()}");

        var results = await RaceAsync(
            () => TryAsync(userId, c => c.ToSalesOrder(piId)),
            () => TryAsync(userId, c => c.Void(piId)));

        Assert.Equal(1, results.Count(r => r.Success));

        await using var verify = _fixture.CreateDbContext();
        var pi = await verify.ProformaInvoices.AsNoTracking().SingleAsync(p => p.Id == piId);
        var orders = await verify.SalesOrders.AsNoTracking().Where(o => o.SourcePiId == piId).ToListAsync();

        if (pi.Status == DocumentStatus.Completed)
        {
            Assert.Single(orders);            // 转换赢：订单已生成，作废被拒
        }
        else
        {
            Assert.Equal(DocumentStatus.Cancelled, pi.Status);   // 作废赢：零订单，来源已成作废
            Assert.Empty(orders);
        }
    }

    [Fact]
    public async Task 转换与销审并发_恰好一个合法赢家_绝不出现已完成却零订单()
    {
        Guard();
        var (userId, _, piId) = await SeedOwnedPiAsync($"RACE_CU_{Tag()}");

        var results = await RaceAsync(
            () => TryAsync(userId, c => c.ToSalesOrder(piId)),
            () => TryAsync(userId, c => c.Unaudit(piId)));

        Assert.Equal(1, results.Count(r => r.Success));

        await using var verify = _fixture.CreateDbContext();
        var pi = await verify.ProformaInvoices.AsNoTracking().SingleAsync(p => p.Id == piId);
        var orders = await verify.SalesOrders.AsNoTracking().Where(o => o.SourcePiId == piId).ToListAsync();

        if (pi.Status == DocumentStatus.Completed)
        {
            Assert.Single(orders);            // 转换赢：销审被拒（已完成 + 已链接）
        }
        else
        {
            Assert.Equal(DocumentStatus.Pending, pi.Status);     // 销审赢：回到草稿，零订单
            Assert.Empty(orders);
        }
    }

    [Fact]
    public async Task 编辑与审核并发_审核必然生效_编辑只在审核之前落库_无撕裂()
    {
        Guard();
        var tag = Tag();
        var (userId, customerId, piId) = await SeedOwnedPiAsync($"RACE_EA_{tag}", DocumentStatus.Submitted);

        var results = await RaceAsync(
            () => TryAsync(userId, c => c.Update(piId, new ProformaInvoice
            {
                PiDate = DateTime.Today,
                CustomerId = customerId,
                CustomerName = "集成客户",
                Currency = Currency.USD,
                ExchangeRate = 7.2m,
                DepositRatio = 30m,
                Details = new List<ProformaInvoiceDetail>
                {
                    new()
                    {
                        ProductCode = "INT-E399-EDIT", ProductName = "集成商品", Unit = "PCS",
                        Quantity = 20m, UnitPrice = 5m
                    }
                }
            })),
            () => TryAsync(userId, c => c.Approve(piId)));

        Assert.True(results[1].Success, $"approve={results[1].Error}");

        await using var verify = _fixture.CreateDbContext();
        var pi = await verify.ProformaInvoices.AsNoTracking().Include(o => o.Details)
            .SingleAsync(p => p.Id == piId);
        Assert.Equal(DocumentStatus.Approved, pi.Status);

        var quantity = pi.Details.Where(d => !d.IsDeleted).Sum(d => d.Quantity);
        if (results[0].Success) Assert.Equal(20m, quantity);   // 编辑先落库：审核在编辑后的权威状态上生效
        else Assert.Equal(10m, quantity);                      // 审核先赢：编辑在锁内被拒（绝不丢更新）
    }

    [Fact]
    public async Task 混合批量删除并发_恰好一个合法赢家_失败侧整体回滚不部分删除()
    {
        Guard();
        var tag = Tag();
        long userId, a, b, c, d;
        await using (var seed = _fixture.CreateDbContext())
        {
            var (user, employee, _) = await SeedOperatorAsync(seed);
            userId = user.Id;
            var customer = await SeedCustomerAsync(seed, $"INT_E399_BD_{tag}", employee.Id);
            a = (await SeedPiAsync(seed, $"INT_E399_BDA_{tag}", customer.Id)).Id;
            b = (await SeedPiAsync(seed, $"INT_E399_BDB_{tag}", customer.Id)).Id;
            c = (await SeedPiAsync(seed, $"INT_E399_BDC_{tag}", customer.Id)).Id;
            d = (await SeedPiAsync(seed, $"INT_E399_BDD_{tag}", customer.Id)).Id;
        }

        var results = await RaceAsync(
            () => TryAsync(userId, ctl => ctl.BatchDelete(new List<long> { a, b, c })),
            () => TryAsync(userId, ctl => ctl.BatchDelete(new List<long> { b, c, d })));

        Assert.Equal(1, results.Count(r => r.Success));

        await using var verify = _fixture.CreateDbContext();
        var deleted = await verify.ProformaInvoices.AsNoTracking()
            .Where(p => p.Id == a || p.Id == b || p.Id == c || p.Id == d)
            .ToDictionaryAsync(p => p.Id, p => p.IsDeleted);

        Assert.Equal(3, deleted.Values.Count(value => value));   // 恰好一个完整批次生效
        var winner = results[0].Success ? new[] { a, b, c } : new[] { b, c, d };
        var loserOnly = results[0].Success ? d : a;
        foreach (var id in winner) Assert.True(deleted[id]);
        Assert.False(deleted[loserOnly]);                        // 失败侧的独占行绝不被部分删除
    }

    // ==================== 3. 编号生成失败 → 整体回滚 ====================

    [Fact]
    public async Task 编号生成失败_整体回滚_不落半成品订单_不占号_不改来源状态与审计()
    {
        Guard();
        var tag = Tag();
        long userId, piId;
        DateTime? updatedAtBefore;

        await using (var seed = _fixture.CreateDbContext())
        {
            var (user, employee, _) = await SeedOperatorAsync(seed);
            userId = user.Id;
            var customer = await SeedCustomerAsync(seed, $"INT_E399_NUM_{tag}", employee.Id);
            var pi = await SeedPiAsync(seed, $"INT_E399_PI_NUM_{tag}", customer.Id, DocumentStatus.Approved);
            piId = pi.Id;
            updatedAtBefore = pi.UpdatedAt;
        }

        try
        {
            // 既有「销售订单」单据号规则在**本测试独占的 GUID 库内**被改成非法日期格式：
            // 编号生成在引用事务内真实失败（不伪造异常）；测试结束立即还原（本类用例串行执行）。
            await using (var mutate = _fixture.CreateDbContext())
            {
                var rule = await mutate.SysDocumentNumberRules
                    .FirstAsync(r => r.DocumentType == DocumentType.SalesOrder && !r.IsDeleted);
                rule.DateFormat = "q";
                await mutate.SaveChangesAsync();
            }

            var result = await TryAsync(userId, c => c.ToSalesOrder(piId));
            Assert.False(result.Success);

            await using (var verify = _fixture.CreateDbContext())
            {
                Assert.False(await verify.SalesOrders.AnyAsync(o => o.SourcePiId == piId));

                var pi = await verify.ProformaInvoices.AsNoTracking().SingleAsync(p => p.Id == piId);
                Assert.Equal(DocumentStatus.Approved, pi.Status);
                Assert.Equal(1000m, pi.TotalAmount);
                Assert.Equal(updatedAtBefore, pi.UpdatedAt);   // 行锁的审计时间戳刷新也在同一事务内回滚
            }
        }
        finally
        {
            await using var restore = _fixture.CreateDbContext();
            var rule = await restore.SysDocumentNumberRules
                .FirstAsync(r => r.DocumentType == DocumentType.SalesOrder && !r.IsDeleted);
            rule.DateFormat = "yyyyMMdd";
            await restore.SaveChangesAsync();
        }
    }
}

/// <summary>
/// ERP-399 专用 localdb 夹具：目标必须是专用实例 <c>(localdb)\NEWERP_AutoAcceptance</c>、库名前缀
/// <c>NEWERP_AUTOTEST</c> 且 <c>Integrated Security=true</c>；每次运行只创建一个<strong>全新 GUID 后缀库</strong>，
/// 发现同名库已存在立即拒绝，绝不 drop / reset / 复用任何数据库，也绝不读取 appsettings / .env / 生产凭据。
/// </summary>
public sealed class ProformaInvoiceMutationSqlServerFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";
    public const string DefaultDatabaseName = "NEWERP_AUTOTEST_PIMUT_20261009";

    public string ConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = BuildDefaultConnectionString();

        AssertDedicatedTarget(connectionString);
        ConnectionString = connectionString;

        Console.WriteLine($"[ERP-399] 目标库护栏放行（实例 {InstanceMarker}，库名前缀 {DatabasePrefix}）。");

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

        Console.WriteLine("[ERP-399] 集成场景就绪：全新 GUID 库 + 完整 NEWERP 结构 + 种子数据（不清理既有行）。");
    }
}

/// <summary>专用目标护栏的 fail-closed 覆盖：错误实例 / 错误库名 / 非集成安全必须在访问数据库之前被拒绝。</summary>
public sealed class ProformaInvoiceMutationTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;User Id=sa;Password=secret")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(
            () => ProformaInvoiceMutationSqlServerFixture.AssertDedicatedTarget(connection));
}
