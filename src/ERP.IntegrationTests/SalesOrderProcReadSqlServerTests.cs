using System.Text.Json;
using ERP.Api.Controllers;
using ERP.Api.Services;
using ERP.Application.Common;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using ERP.Infrastructure.Reports;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using System.Security.Claims;
using Xunit;

namespace ERP.IntegrationTests;

/// <summary>
/// ERP-411 旧销售订单专用路由（<c>api/v2/sales-orders</c> 查询 / 翻页导航）的真实 SQL Server 集成测试
/// （GUID 独占 <c>NEWERP_AUTOTEST</c> 目标）。
/// <list type="number">
/// <item><b>真实规则 + 真实控制器 + 真实既有授权</b>：以新播种的既有「销售订单」功能菜单授权与业务员客户数据范围
/// 驱动真实 <see cref="SalesOrderProcController"/> 与真实 <see cref="LegacyBillReadService"/>；不新增 / 不修改任何
/// 既有菜单 / 角色 / 用户授权，无匿名 / 管理员降级。</item>
/// <item><b>范围证明</b>：两位客户的旧库行在受限业务员下只返回授权客户的行 / 计数 / 首末前后；越权或不存在锚点与
/// 「没有更多」返回同一结果（不泄露）；特权账号保留既有全量口径；专用 vs 通用旧读侧对同一授权记录一致。</item>
/// <item><b>零写入证据</b>：每次拒绝 / 读取后 <c>SysOperationLogs</c> / <c>SysDingTalkLogs</c> / 规范业务表
/// （<c>SalesOrders</c> / <c>StockIns</c> / <c>StockMovements</c>，保留库存来源单据审计）与旧库行数全部不变。</item>
/// <item><b>两个独立连接竞态</b>：每条竞态用例各自新建两个独立 DbContext / 连接 / 控制器并门闩对齐并发；
/// 两条连接都返回一致的范围内结果（拒绝侧都 fail closed），零写入。</item>
/// <item><b>专用目标护栏</b>：必须在访问数据库之前精确命中 <c>(localdb)\NEWERP_AutoAcceptance</c> + <c>NEWERP_AUTOTEST</c>
/// 前缀 + <c>Integrated Security</c>；每次运行只创建一个全新 GUID 库，发现同名库已存在立即拒绝，
/// 绝不 drop / reset / 复用任何库，也绝不读取 appsettings / .env / 生产凭据。</item>
/// </list>
/// <para>保留既有库存来源单据审计与原始失败日志：本测试只读取计数与只读快照，不删除 / 不清理任何既有行
/// （被拒绝的请求本就零写入）。构建完成不等于阶段验收：只有本文件在受控 localdb 上真实执行通过才算验收证据。</para>
/// </summary>
public sealed class SalesOrderProcReadSqlServerTests : IClassFixture<SalesOrderProcReadSqlServerFixture>
{
    private readonly SalesOrderProcReadSqlServerFixture _fixture;

    public SalesOrderProcReadSqlServerTests(SalesOrderProcReadSqlServerFixture fixture) => _fixture = fixture;

    private static readonly string[] SnapshotTables =
    {
        "SysOperationLogs", "SysDingTalkLogs", "SalesOrders", "StockIns", "StockMovements", "SalesOrder",
    };

    /// <summary>专用目标护栏（任何数据库访问之前）。</summary>
    private void Guard() => SalesOrderProcReadSqlServerFixture.AssertDedicatedTarget(_fixture.ConnectionString);

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private SalesOrderProcController NewDedicatedController(ErpDbContext db, long? userId)
    {
        var controller = new SalesOrderProcController(
            new StoredProcedureService(Configuration()), db,
            new LegacyBillReadService(new StoredProcedureService(Configuration())));
        TestUser(controller, userId);
        return controller;
    }

    private BillProcController NewGenericController(ErpDbContext db, long? userId)
    {
        var controller = new BillProcController(
            new StoredProcedureService(Configuration()), db,
            new DingTalkService(db, new StubHttpClientFactory(), NullLogger<DingTalkService>.Instance),
            new LegacyBillReadService(new StoredProcedureService(Configuration())));
        TestUser(controller, userId);
        return controller;
    }

    private IConfiguration Configuration()
        => new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = _fixture.ConnectionString
            })
            .Build();

    private static void TestUser(ControllerBase controller, long? userId)
    {
        var http = new DefaultHttpContext();
        if (userId.HasValue)
            http.User = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }, "Test"));

        controller.ControllerContext = new ControllerContext { HttpContext = http };
    }

    private static ApiResponse<object> Envelope(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        return Assert.IsType<ApiResponse<object>>(ok.Value);
    }

    private static JsonElement Payload(ApiResponse<object> envelope)
        => JsonDocument.Parse(JsonSerializer.Serialize(envelope.Data)).RootElement.Clone();

    private static (int Total, List<long> Oids, List<long> CustIds) PageOf(ApiResponse<object> envelope)
    {
        var root = Payload(envelope);
        var oids = new List<long>();
        var customers = new List<long>();
        foreach (var item in root.GetProperty("items").EnumerateArray())
        {
            oids.Add(item.GetProperty("Oid").GetInt64());
            customers.Add(item.GetProperty("CustId").GetInt64());
        }

        return (root.GetProperty("total").GetInt32(), oids, customers);
    }

    private static long RowOid(ApiResponse<object> envelope) => Payload(envelope).GetProperty("Oid").GetInt64();

    private async Task<Dictionary<string, long>> SnapshotAsync()
    {
        var snapshot = new Dictionary<string, long>(StringComparer.Ordinal);
        await using var conn = new SqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        foreach (var table in SnapshotTables)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT COUNT(*) FROM db_owner.[{table}]";
            snapshot[table] = Convert.ToInt64(await cmd.ExecuteScalarAsync() ?? 0L);
        }

        return snapshot;
    }

    private static void AssertUnchanged(Dictionary<string, long> before, Dictionary<string, long> after)
    {
        foreach (var (table, count) in before)
        {
            Assert.True(after.TryGetValue(table, out var current), $"快照缺少 {table}");
            Assert.Equal(count, current);
        }
    }

    // ==================== 1. 范围受限的分页 / 计数（两位客户） ====================

    [Fact]
    public async Task 受限业务员分页只含授权客户的行与计数()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var controller = NewDedicatedController(db, _fixture.RestrictedUserId);

        var page = Envelope(await controller.GetPaged(new PageQuery { Page = 1, PageSize = 200 }, null));
        Assert.Equal(ErrorCodes.Success, page.Code);

        var (total, oids, customers) = PageOf(page);
        Assert.Equal(SalesOrderProcReadSqlServerFixture.VisibleOids.Length, total);
        Assert.Equal(
            SalesOrderProcReadSqlServerFixture.VisibleOids.OrderBy(o => o).ToArray(),
            oids.OrderBy(o => o).ToArray());
        Assert.All(customers, c => Assert.Equal(_fixture.CustomerAId, c));
        Assert.DoesNotContain(SalesOrderProcReadSqlServerFixture.HiddenOids[0], oids);
    }

    [Fact]
    public async Task 特权账号分页保留既有全量口径()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var controller = NewDedicatedController(db, _fixture.PrivilegedUserId);

        var page = Envelope(await controller.GetPaged(new PageQuery { Page = 1, PageSize = 200 }, null));
        Assert.Equal(ErrorCodes.Success, page.Code);

        var (total, oids, _) = PageOf(page);
        var expected = SalesOrderProcReadSqlServerFixture.VisibleOids
            .Concat(SalesOrderProcReadSqlServerFixture.HiddenOids).OrderBy(o => o).ToArray();
        Assert.Equal(expected.Length, total);
        Assert.Equal(expected, oids.OrderBy(o => o).ToArray());
    }

    // ==================== 2. 翻页导航与锚点 ====================

    [Fact]
    public async Task 翻页导航各方向均受范围约束()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var controller = NewDedicatedController(db, _fixture.RestrictedUserId);

        var first = Envelope(await controller.Navigate(SalesOrderProcReadSqlServerFixture.VisibleOids[1], "first"));
        Assert.Equal(ErrorCodes.Success, first.Code);
        Assert.Equal(SalesOrderProcReadSqlServerFixture.VisibleOids[0], RowOid(first));

        var last = Envelope(await controller.Navigate(SalesOrderProcReadSqlServerFixture.VisibleOids[1], "last"));
        Assert.Equal(ErrorCodes.Success, last.Code);
        Assert.Equal(SalesOrderProcReadSqlServerFixture.VisibleOids[2], RowOid(last));

        var prev = Envelope(await controller.Navigate(SalesOrderProcReadSqlServerFixture.VisibleOids[1], "prev"));
        Assert.Equal(ErrorCodes.Success, prev.Code);
        Assert.Equal(SalesOrderProcReadSqlServerFixture.VisibleOids[0], RowOid(prev));

        var next = Envelope(await controller.Navigate(SalesOrderProcReadSqlServerFixture.VisibleOids[1], "next"));
        Assert.Equal(ErrorCodes.Success, next.Code);
        Assert.Equal(SalesOrderProcReadSqlServerFixture.VisibleOids[2], RowOid(next));

        // 范围内边界与「没有更多」返回同一结果。
        Assert.Equal(ErrorCodes.NotFound,
            Envelope(await controller.Navigate(SalesOrderProcReadSqlServerFixture.VisibleOids[0], "prev")).Code);
        Assert.Equal(ErrorCodes.NotFound,
            Envelope(await controller.Navigate(SalesOrderProcReadSqlServerFixture.VisibleOids[2], "next")).Code);
    }

    [Fact]
    public async Task 越权与不存在锚点与没有更多返回同一结果且零写入()
    {
        Guard();
        var before = await SnapshotAsync();
        await using var db = _fixture.CreateDbContext();
        var controller = NewDedicatedController(db, _fixture.RestrictedUserId);

        var foreign = Envelope(await controller.Navigate(
            SalesOrderProcReadSqlServerFixture.HiddenOids[0], "next"));
        Assert.Equal(ErrorCodes.NotFound, foreign.Code);
        Assert.Null(foreign.Data);

        var absent = Envelope(await controller.Navigate(987654321L, "next"));
        Assert.Equal(ErrorCodes.NotFound, absent.Code);
        Assert.Null(absent.Data);

        AssertUnchanged(before, await SnapshotAsync());
    }

    // ==================== 3. 专用 vs 通用旧读侧一致（同一授权记录） ====================

    [Fact]
    public async Task 专用与通用旧读侧对同一授权记录一致()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();

        var dedicated = PageOf(Envelope(await NewDedicatedController(db, _fixture.RestrictedUserId)
            .GetPaged(new PageQuery { Page = 1, PageSize = 200 }, null)));
        var generic = PageOf(Envelope(await NewGenericController(db, _fixture.RestrictedUserId)
            .GetPaged(SalesOrderProcController.LegacyFamilyKey, new PageQuery { Page = 1, PageSize = 200 }, null)));

        Assert.Equal(generic.Total, dedicated.Total);
        Assert.Equal(generic.Oids.OrderBy(o => o).ToArray(), dedicated.Oids.OrderBy(o => o).ToArray());
        Assert.Equal(generic.CustIds.OrderBy(c => c).ToArray(), dedicated.CustIds.OrderBy(c => c).ToArray());
    }

    // ==================== 4. 拒绝矩阵（身份 / 菜单 / 撤销） ====================

    [Fact]
    public async Task 禁用撤销脱敏身份与无菜单仅导出菜单被拒绝且零写入()
    {
        Guard();
        var before = await SnapshotAsync();
        await using var db = _fixture.CreateDbContext();

        Assert.Equal(ErrorCodes.Unauthorized,
            Envelope(await NewDedicatedController(db, null).GetPaged(new PageQuery(), null)).Code);
        Assert.Equal(ErrorCodes.Unauthorized,
            Envelope(await NewDedicatedController(db, _fixture.DeletedUserId).GetPaged(new PageQuery(), null)).Code);
        Assert.Equal(ErrorCodes.Forbidden,
            Envelope(await NewDedicatedController(db, _fixture.DisabledUserId).GetPaged(new PageQuery(), null)).Code);
        Assert.Equal(ErrorCodes.Forbidden,
            Envelope(await NewDedicatedController(db, _fixture.MenuLessUserId).GetPaged(new PageQuery(), null)).Code);
        Assert.Equal(ErrorCodes.Forbidden,
            Envelope(await NewDedicatedController(db, _fixture.ExportOnlyUserId).GetPaged(new PageQuery(), null)).Code);
        Assert.Equal(ErrorCodes.Forbidden,
            Envelope(await NewDedicatedController(db, _fixture.RevokedMenuUserId).GetPaged(new PageQuery(), null)).Code);
        Assert.Equal(ErrorCodes.Forbidden,
            Envelope(await NewDedicatedController(db, _fixture.ExportOnlyUserId)
                .Navigate(SalesOrderProcReadSqlServerFixture.VisibleOids[0], "first")).Code);

        AssertUnchanged(before, await SnapshotAsync());
    }

    // ==================== 5. 恶意 / 溢出输入与零写入 ====================

    [Fact]
    public async Task 恶意与溢出输入被拒绝且零写入()
    {
        Guard();
        var before = await SnapshotAsync();
        await using var db = _fixture.CreateDbContext();
        var controller = NewDedicatedController(db, _fixture.RestrictedUserId);

        Assert.Equal(ErrorCodes.InvalidParameter,
            Envelope(await controller.GetPaged(new PageQuery { Page = 1, PageSize = 100000 }, null)).Code);
        Assert.Equal(ErrorCodes.InvalidParameter,
            Envelope(await controller.GetPaged(new PageQuery { Page = int.MaxValue, PageSize = 200 }, null)).Code);
        Assert.Equal(ErrorCodes.InvalidParameter,
            Envelope(await controller.Navigate(SalesOrderProcReadSqlServerFixture.VisibleOids[1],
                "first;drop table db_owner.SalesOrder")).Code);
        Assert.Equal(ErrorCodes.InvalidParameter,
            Envelope(await controller.Navigate(0, "next")).Code);

        // 注入式关键字按参数化处理（不拼接），返回 0 行且旧库结构完好。
        var inject = Envelope(await controller.GetPaged(
            new PageQuery { Keyword = "'; DROP TABLE db_owner.SalesOrder; --" }, null));
        Assert.Equal(ErrorCodes.Success, inject.Code);
        Assert.Equal(0, PageOf(inject).Total);

        AssertUnchanged(before, await SnapshotAsync());
    }

    // ==================== 6. 两个独立连接竞态（范围内一致 + 零写入） ====================

    [Fact]
    public async Task 两个独立连接竞态_受限分页一致且零写入()
    {
        Guard();
        var before = await SnapshotAsync();

        await using var dbA = _fixture.CreateDbContext();
        await using var dbB = _fixture.CreateDbContext();
        var controllerA = NewDedicatedController(dbA, _fixture.RestrictedUserId);
        var controllerB = NewDedicatedController(dbB, _fixture.RestrictedUserId);

        using var gate = new SemaphoreSlim(0, 2);
        async Task<(int Total, List<long> Oids)> ReadAsync(SalesOrderProcController controller)
        {
            await gate.WaitAsync();
            var page = Envelope(await controller.GetPaged(new PageQuery { Page = 1, PageSize = 200 }, null));
            var (total, oids, _) = PageOf(page);
            return (total, oids);
        }

        var firstRun = ReadAsync(controllerA);
        var secondRun = ReadAsync(controllerB);
        gate.Release(2);
        var results = new[] { await firstRun, await secondRun };

        Assert.All(results, r => Assert.Equal(SalesOrderProcReadSqlServerFixture.VisibleOids.Length, r.Total));
        Assert.Equal(results[0].Oids.OrderBy(o => o).ToArray(), results[1].Oids.OrderBy(o => o).ToArray());
        Assert.All(results, r => Assert.DoesNotContain(SalesOrderProcReadSqlServerFixture.HiddenOids[0], r.Oids));

        AssertUnchanged(before, await SnapshotAsync());
    }

    [Fact]
    public async Task 两个独立连接竞态_越权导航与仅导出菜单一致拒绝且零写入()
    {
        Guard();
        var before = await SnapshotAsync();

        await using var dbA = _fixture.CreateDbContext();
        await using var dbB = _fixture.CreateDbContext();
        var scopedController = NewDedicatedController(dbA, _fixture.RestrictedUserId);
        var exportOnlyController = NewDedicatedController(dbB, _fixture.ExportOnlyUserId);
        var hidden = SalesOrderProcReadSqlServerFixture.HiddenOids[0];

        using var gate = new SemaphoreSlim(0, 2);
        async Task<int?> NavigateDeniedAsync(SalesOrderProcController controller)
        {
            await gate.WaitAsync();
            var envelope = Envelope(await controller.Navigate(hidden, "next"));
            return envelope.Data is null ? envelope.Code : null;
        }

        async Task<int> MenuDeniedAsync(SalesOrderProcController controller)
        {
            await gate.WaitAsync();
            return Envelope(await controller.GetPaged(new PageQuery(), null)).Code;
        }

        var navigateRun = NavigateDeniedAsync(scopedController);
        var menuRun = MenuDeniedAsync(exportOnlyController);
        gate.Release(2);

        Assert.Equal(ErrorCodes.NotFound, await navigateRun);
        Assert.Equal(ErrorCodes.Forbidden, await menuRun);

        AssertUnchanged(before, await SnapshotAsync());
    }
}

/// <summary>
/// ERP-411 旧销售订单专用读侧真实 SQL 集成测试夹具：GUID 独占 <c>NEWERP_AUTOTEST</c> 目标，任何库访问之前拒绝非专用目标，
/// 发现同名库已存在立即拒绝（绝不 drop / reset / 复用）；建库后补齐完整 NEWERP 结构与种子数据，再以受控旧库结构
/// （<c>db_owner.SalesOrder</c>）与既有授权（既有「销售订单」功能菜单 + 业务员客户数据范围）驱动真实控制器与真实受控只读服务。
/// </summary>
public sealed class SalesOrderProcReadSqlServerFixture : IAsyncLifetime
{
    /// <summary>专用实例（精确匹配）。</summary>
    public const string InstanceTarget = @"(localdb)\NEWERP_AutoAcceptance";

    /// <summary>库名前缀（必须为 NEWERP_AUTOTEST）。</summary>
    public const string DatabasePrefix = "NEWERP_AUTOTEST";

    private const string DefaultDatabaseName = DatabasePrefix + "_SOPRD";

    /// <summary>可见客户（受限业务员名下）旧库行 Oid。</summary>
    public static readonly long[] VisibleOids = { 9301L, 9302L, 9303L };

    /// <summary>隐藏客户（不在受限业务员名下）旧库行 Oid。</summary>
    public static readonly long[] HiddenOids = { 9401L, 9402L };

    public string ConnectionString { get; private set; } = string.Empty;

    public long PrivilegedUserId { get; private set; }
    public long RestrictedUserId { get; private set; }
    public long MenuLessUserId { get; private set; }
    public long ExportOnlyUserId { get; private set; }
    public long RevokedMenuUserId { get; private set; }
    public long DisabledUserId { get; private set; }
    public long DeletedUserId { get; private set; }
    public long CustomerAId { get; private set; }
    public long CustomerBId { get; private set; }
    public long SalesmanId { get; private set; }

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = BuildDefaultConnectionString();

        AssertDedicatedTarget(connectionString);
        ConnectionString = connectionString;

        Console.WriteLine($"[ERP-411] 目标库护栏放行（实例 {InstanceTarget}，库名前缀 {DatabasePrefix}）。");
        await CreateFreshDatabaseAsync();
        await SeedAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public ErpDbContext CreateDbContext() => new(BuildOptions());

    private DbContextOptions<ErpDbContext> BuildOptions()
        => new DbContextOptionsBuilder<ErpDbContext>().UseSqlServer(ConnectionString).Options;

    private static string BuildDefaultConnectionString()
        => $"Server={InstanceTarget};Initial Catalog={DefaultDatabaseName}_{Guid.NewGuid():N};"
           + "Integrated Security=true;TrustServerCertificate=true;";

    /// <summary>专用目标护栏：错误实例 / 错误库名 / 非集成安全必须在访问数据库之前被拒绝。</summary>
    public static void AssertDedicatedTarget(string connectionString)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        Assert.Equal(InstanceTarget, builder.DataSource ?? string.Empty, ignoreCase: true);
        Assert.StartsWith(DatabasePrefix, builder.InitialCatalog ?? string.Empty, StringComparison.OrdinalIgnoreCase);
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

        Console.WriteLine("[ERP-411] 集成场景就绪：全新 GUID 库 + 完整 NEWERP 结构 + 种子数据（不清理既有行）。");
    }

    // ==================== 既有授权（不新增权限模型）+ 受控旧库夹具 ====================

    private async Task SeedAsync()
    {
        await using var db = CreateDbContext();

        // 1) 特权账号（系统内置角色，沿用既有全部访问口径）。
        var privilegedRole = new SysRole
        {
            RoleName = "ERP411 特权角色",
            RoleCode = $"ERP411-P-{Guid.NewGuid():N}",
            IsSystem = true
        };
        db.SysRoles.Add(privilegedRole);
        await db.SaveChangesAsync();

        var privileged = NewUser(UserStatus.Enabled);
        db.SysUsers.Add(privileged);
        await db.SaveChangesAsync();
        db.SysUserRoles.Add(new SysUserRole { UserId = privileged.Id, RoleId = privilegedRole.Id });
        await db.SaveChangesAsync();
        PrivilegedUserId = privileged.Id;

        // 2) 受限业务员：既有「销售订单」功能菜单 + 客户数据范围。
        var restricted = NewUser(UserStatus.Enabled);
        db.SysUsers.Add(restricted);
        await db.SaveChangesAsync();
        var restrictedRoleId = await AddRoleAsync(db, menuCodes: new[] { "sales-order" });
        db.SysUserRoles.Add(new SysUserRole { UserId = restricted.Id, RoleId = restrictedRoleId });
        await db.SaveChangesAsync();
        RestrictedUserId = restricted.Id;

        var employee = new BaseEmployee
        {
            EmployeeCode = restricted.UserName,
            EmployeeName = "ERP411 受限业务员",
            IsSalesman = true,
            Status = 1
        };
        db.BaseEmployees.Add(employee);
        await db.SaveChangesAsync();
        SalesmanId = employee.Id;

        var customerA = new BaseCustomer
        {
            CustomerCode = $"C-A-{Guid.NewGuid():N}",
            CustomerName = "ERP411 可见客户",
            EmpId = employee.Id,
            Status = 1,
            CreditStatus = "正常"
        };
        var customerB = new BaseCustomer
        {
            CustomerCode = $"C-B-{Guid.NewGuid():N}",
            CustomerName = "ERP411 隐藏客户",
            EmpId = null,
            Status = 1,
            CreditStatus = "正常"
        };
        db.BaseCustomers.AddRange(customerA, customerB);
        await db.SaveChangesAsync();
        CustomerAId = customerA.Id;
        CustomerBId = customerB.Id;

        // 3) 拒绝侧账号：无菜单 / 仅导出菜单 / 已禁用 / 已删除。
        var menuLess = NewUser(UserStatus.Enabled);
        db.SysUsers.Add(menuLess);
        await db.SaveChangesAsync();
        db.SysUserRoles.Add(new SysUserRole
        {
            UserId = menuLess.Id,
            RoleId = await AddRoleAsync(db, menuCodes: Array.Empty<string>())
        });
        await db.SaveChangesAsync();
        MenuLessUserId = menuLess.Id;

        var exportOnly = NewUser(UserStatus.Enabled);
        db.SysUsers.Add(exportOnly);
        await db.SaveChangesAsync();
        db.SysUserRoles.Add(new SysUserRole
        {
            UserId = exportOnly.Id,
            RoleId = await AddRoleAsync(db, menuCodes: new[] { "sales-order-export" })
        });
        await db.SaveChangesAsync();
        ExportOnlyUserId = exportOnly.Id;

        // 授予后又撤销既有「销售订单」菜单（角色仍在，授权已回收 → 下一次请求立即收敛）。
        var revoked = NewUser(UserStatus.Enabled);
        db.SysUsers.Add(revoked);
        await db.SaveChangesAsync();
        var revokedRoleId = await AddRoleAsync(db, menuCodes: new[] { "sales-order" });
        db.SysUserRoles.Add(new SysUserRole { UserId = revoked.Id, RoleId = revokedRoleId });
        await db.SaveChangesAsync();
        db.SysRoleMenus.RemoveRange(db.SysRoleMenus.Where(rm => rm.RoleId == revokedRoleId));
        await db.SaveChangesAsync();
        RevokedMenuUserId = revoked.Id;

        var disabled = NewUser(UserStatus.Disabled);
        db.SysUsers.Add(disabled);
        await db.SaveChangesAsync();
        db.SysUserRoles.Add(new SysUserRole
        {
            UserId = disabled.Id,
            RoleId = await AddRoleAsync(db, menuCodes: new[] { "sales-order" })
        });
        await db.SaveChangesAsync();
        DisabledUserId = disabled.Id;

        var deleted = NewUser(UserStatus.Enabled);
        deleted.IsDeleted = true;
        db.SysUsers.Add(deleted);
        await db.SaveChangesAsync();
        db.SysUserRoles.Add(new SysUserRole
        {
            UserId = deleted.Id,
            RoleId = await AddRoleAsync(db, menuCodes: new[] { "sales-order" })
        });
        await db.SaveChangesAsync();
        DeletedUserId = deleted.Id;

        // 4) 受控旧库结构 + 两位客户的旧库行（仅在本隔离 GUID 库内创建，绝不触碰既有行）。
        await db.Database.ExecuteSqlRawAsync(LegacySchemaSql);
        await db.Database.ExecuteSqlRawAsync(LegacyRowsSql,
            VisibleOids[0], CustomerAId, SalesmanId, VisibleOids[1], VisibleOids[2],
            HiddenOids[0], CustomerBId, HiddenOids[1]);

        Console.WriteLine("[ERP-411] 既有授权 + 受控旧库夹具就绪（受控只读，不新增权限模型）。");
    }

    private static SysUser NewUser(UserStatus status) => new()
    {
        UserName = $"erp411-{Guid.NewGuid():N}",
        PasswordHash = "hash",
        PasswordSalt = "salt",
        DisplayName = "ERP411 隔离账号",
        Status = status
    };

    private static async Task<long> AddRoleAsync(ErpDbContext db, string[] menuCodes)
    {
        var role = new SysRole { RoleName = $"ERP411-{Guid.NewGuid():N}", RoleCode = $"ERP411-{Guid.NewGuid():N}", IsSystem = false };
        db.SysRoles.Add(role);
        await db.SaveChangesAsync();

        foreach (var menuCode in menuCodes)
        {
            var menu = await db.SysMenus.FirstOrDefaultAsync(m => m.MenuCode == menuCode && !m.IsDeleted);
            if (menu is null)
            {
                menu = new SysMenu { MenuName = menuCode, MenuCode = menuCode, MenuType = MenuType.Menu };
                db.SysMenus.Add(menu);
                await db.SaveChangesAsync();
            }

            db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
            await db.SaveChangesAsync();
        }

        return role.Id;
    }

    /// <summary>
    /// 受控旧库结构（仅本隔离 GUID 库）：与读侧授权白名单一致的列（<c>Oid</c> + 授权表头列），不含任何未授权列。
    /// </summary>
    private const string LegacySchemaSql = @"
IF OBJECT_ID('db_owner.SalesOrder') IS NULL
BEGIN
    CREATE TABLE db_owner.SalesOrder (
        Oid BIGINT NOT NULL PRIMARY KEY,
        BillNo NVARCHAR(50) NOT NULL,
        OrderDate DATETIME2 NOT NULL,
        CustId BIGINT NOT NULL,
        EmpId BIGINT NULL,
        Currency INT NOT NULL,
        ExchangeRate DECIMAL(18,6) NOT NULL,
        TotalAmount DECIMAL(18,4) NOT NULL,
        DepositAmount DECIMAL(18,4) NOT NULL,
        DepositRatio DECIMAL(18,4) NOT NULL,
        DeliveryDate DATETIME2 NULL,
        Status INT NOT NULL,
        Remark NVARCHAR(500) NULL
    );
END";

    /// <summary>受控旧库行：3 行可见客户 + 2 行隐藏客户（Oid 稳定、顺序稳定）。</summary>
    private const string LegacyRowsSql = @"
INSERT INTO db_owner.SalesOrder
    (Oid, BillNo, OrderDate, CustId, EmpId, Currency, ExchangeRate, TotalAmount, DepositAmount, DepositRatio, DeliveryDate, Status, Remark)
VALUES
    ({0}, N'SO-A1', SYSDATETIME(), {1}, {2}, 2, 1, 100.5, 10.5, 10, NULL, 1, N'可见一'),
    ({3}, N'SO-A2', SYSDATETIME(), {1}, {2}, 2, 1, 200.5, 20.5, 10, NULL, 1, N'可见二'),
    ({4}, N'SO-A3', SYSDATETIME(), {1}, {2}, 2, 1, 300.5, 30.5, 10, NULL, 1, N'可见三'),
    ({5}, N'SO-B1', SYSDATETIME(), {6}, {2}, 2, 1, 400.5, 40.5, 10, NULL, 1, N'隐藏一'),
    ({7}, N'SO-B2', SYSDATETIME(), {6}, {2}, 2, 1, 500.5, 50.5, 10, NULL, 1, N'隐藏二');";
}

/// <summary>专用目标护栏的 fail-closed 覆盖：错误实例 / 错误库名 / 非集成安全必须在访问数据库之前被拒绝。</summary>
public sealed class SalesOrderProcReadTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;User Id=sa;Password=secret")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(
            () => SalesOrderProcReadSqlServerFixture.AssertDedicatedTarget(connection));
}
