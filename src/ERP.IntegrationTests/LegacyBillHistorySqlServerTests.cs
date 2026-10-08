using ERP.Api.Controllers;
using ERP.Api.Services;
using ERP.Application.Common;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using System.Security.Claims;
using System.Text.Json;
using Xunit;

namespace ERP.IntegrationTests;

/// <summary>
/// ERP-406 旧单据操作历史（<c>GET api/v2/bills/{billType}/{oid}/logs</c>）的真实 SQL Server 集成测试
/// （GUID 独占 <c>NEWERP_AUTOTEST</c> 目标）。
/// <list type="number">
/// <item><b>真实规则 + 真实控制器 + 真实既有授权</b>：以新播种的既有功能菜单授权与业务员客户数据范围驱动真实
/// <see cref="BillProcController"/> 与真实 <see cref="ERP.Infrastructure.Reports.LegacyBillReadService"/>；
/// 不新增 / 不修改任何既有菜单 / 角色 / 用户授权，无匿名 / 管理员降级。</item>
/// <item><b>精确文档身份</b>：两位客户、同族跨客户与跨族同单号、精确路径 vs 前缀碰撞 / 非有限动作段 / 无记录标识，
/// 只有精确命中「模块标题 + 权威单号 + 有限精确路径」的历史行才返回。</item>
/// <item><b>拒绝侧</b>：零 / 负数 Oid、缺失 / 越权 Oid、撤销 / 禁用 / 删除 / 无菜单 / 仅导出菜单身份一律零单号 / 零历史；缺结构族显式 environment-blocked。</item>
/// <item><b>零写入证据</b>：每次读取 / 拒绝后 <c>SysOperationLogs</c> / <c>SysDingTalkLogs</c> / 规范业务表
/// （<c>SalesOrders</c> / <c>StockIns</c> / <c>StockMovements</c>，保留库存来源单据审计）与旧库行数全部不变。</item>
/// <item><b>两个独立连接竞态</b>：每条竞态用例各自新建两个独立 DbContext / 连接 / 控制器并门闩对齐并发，结果一致且零写入。</item>
/// <item><b>专用目标护栏</b>：必须在访问数据库之前精确命中 <c>(localdb)\NEWERP_AutoAcceptance</c> + <c>NEWERP_AUTOTEST</c>
/// 前缀 + <c>Integrated Security</c>；每次运行只创建一个全新 GUID 库，发现同名库已存在立即拒绝，
/// 绝不 drop / reset / 复用任何库，也绝不读取 appsettings / .env / 生产凭据。</item>
/// </list>
/// <para>保留既有库存来源单据审计与原始失败日志：本测试只读取计数与只读快照，不删除 / 不清理任何既有行
/// （被拒绝的请求本就零写入）。构建完成不等于阶段验收：只有本文件在受控 localdb 上真实执行通过才算验收证据。</para>
/// </summary>
public sealed class LegacyBillHistorySqlServerTests : IClassFixture<LegacyBillHistorySqlServerFixture>
{
    private readonly LegacyBillHistorySqlServerFixture _fixture;

    public LegacyBillHistorySqlServerTests(LegacyBillHistorySqlServerFixture fixture) => _fixture = fixture;

    private static readonly string[] SnapshotTables =
    {
        "SysOperationLogs", "SysDingTalkLogs", "SalesOrders", "StockIns", "StockMovements",
        "SalesOrder",
    };

    /// <summary>专用目标护栏（任何数据库访问之前）。</summary>
    private void Guard() => LegacyBillHistorySqlServerFixture.AssertDedicatedTarget(_fixture.ConnectionString);

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private BillProcController NewController(ErpDbContext db, long? userId)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = _fixture.ConnectionString
            })
            .Build();

        var controller = new BillProcController(
            new StoredProcedureService(configuration),
            db,
            new DingTalkService(db, new StubHttpClientFactory(), NullLogger<DingTalkService>.Instance));

        var http = new DefaultHttpContext();
        if (userId.HasValue)
        {
            http.User = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }, "Test"));
        }

        controller.ControllerContext = new ControllerContext { HttpContext = http };
        return controller;
    }

    private static ApiResponse<object> Envelope(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        return Assert.IsType<ApiResponse<object>>(ok.Value);
    }

    private static JsonElement Payload(ApiResponse<object> envelope)
        => JsonDocument.Parse(JsonSerializer.Serialize(envelope.Data)).RootElement.Clone();

    private static (int Total, int Page, int PageSize, List<string> Paths, List<long> Ids) PageOf(ApiResponse<object> envelope)
    {
        var root = Payload(envelope);
        var paths = new List<string>();
        var ids = new List<long>();
        foreach (var item in root.GetProperty("items").EnumerateArray())
        {
            paths.Add(item.GetProperty("Path").GetString() ?? string.Empty);
            ids.Add(item.GetProperty("Id").GetInt64());
        }

        return (root.GetProperty("total").GetInt32(), root.GetProperty("page").GetInt32(),
            root.GetProperty("pageSize").GetInt32(), paths, ids);
    }

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

    // ==================== 1. 受限业务员：精确模块 + 有限精确路径 ====================

    [Fact]
    public async Task 受限业务员可见单据历史只返回精确路径与模块的行()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var controller = NewController(db, _fixture.RestrictedUserId);

        var envelope = Envelope(await controller.GetBillLogs("sales-order",
            LegacyBillHistorySqlServerFixture.VisibleOids[0], 1, 200));
        Assert.Equal(ErrorCodes.Success, envelope.Code);

        var (total, page, pageSize, paths, _) = PageOf(envelope);
        Assert.Equal(LegacyBillHistorySqlServerFixture.ExpectedVisibleHistoryRows, total);
        Assert.Equal(1, page);
        Assert.Equal(200, pageSize);
        Assert.Equal(LegacyBillHistorySqlServerFixture.ExpectedVisibleHistoryRows, paths.Count);

        Assert.All(paths, p => Assert.Contains(p, LegacyBillHistorySqlServerFixture.VisibleHistoryPaths));
        Assert.DoesNotContain(LegacyBillHistorySqlServerFixture.CollisionPath, paths);
        Assert.DoesNotContain(LegacyBillHistorySqlServerFixture.NonFiniteActionPath, paths);
        Assert.DoesNotContain(LegacyBillHistorySqlServerFixture.LegacyNoIdentifierPath, paths);
        Assert.DoesNotContain(LegacyBillHistorySqlServerFixture.OtherFamilyPath, paths);
        Assert.DoesNotContain(LegacyBillHistorySqlServerFixture.HiddenDocumentPath, paths);

        foreach (var item in Payload(envelope).GetProperty("items").EnumerateArray())
        {
            Assert.Equal(BillProcController.BillTitles["sales-order"], item.GetProperty("Module").GetString());
            Assert.Equal(LegacyBillHistorySqlServerFixture.DuplicateBillNo, item.GetProperty("BillNo").GetString());
            Assert.False(item.TryGetProperty("RequestBody", out _));
        }
    }

    // ==================== 2. 跨族 / 跨客户同单号、碰撞路径、越权历史 ====================

    [Fact]
    public async Task 同单号跨族跨客户与碰撞路径不得混入_越权历史不泄露()
    {
        Guard();
        await using var restricted = _fixture.CreateDbContext();
        var restrictedController = NewController(restricted, _fixture.RestrictedUserId);

        // 越权（其它客户）Oid：与「不存在」返回同一结果，且不返回任何单号 / 历史。
        var foreign = Envelope(await restrictedController.GetBillLogs("sales-order",
            LegacyBillHistorySqlServerFixture.HiddenOids[0]));
        Assert.Equal(ErrorCodes.NotFound, foreign.Code);
        Assert.Null(foreign.Data);
        Assert.DoesNotContain(LegacyBillHistorySqlServerFixture.DuplicateBillNo, JsonSerializer.Serialize(foreign));

        // 跨族同单号历史：stock-out 族在隔离库没有旧表 → 显式 environment-blocked（绝不返回规范表 / 静默空集）。
        var otherFamily = Envelope(await restrictedController.GetBillLogs("stock-out",
            LegacyBillHistorySqlServerFixture.OtherFamilyOid));
        Assert.Equal(ReportConfigurationExecutionLimits.ErrorCodeEnvironmentUnsupported, otherFamily.Code);
        Assert.Contains("environment-blocked", otherFamily.Message, StringComparison.Ordinal);
        Assert.Null(otherFamily.Data);

        // 特权账号沿用既有全量口径：可读取隐藏客户文档的历史（同单号，但精确命中其自身路径）。
        await using var privileged = _fixture.CreateDbContext();
        var privilegedEnvelope = Envelope(await NewController(privileged, _fixture.PrivilegedUserId)
            .GetBillLogs("sales-order", LegacyBillHistorySqlServerFixture.HiddenOids[0], 1, 200));
        Assert.Equal(ErrorCodes.Success, privilegedEnvelope.Code);
        var (hiddenTotal, _, _, hiddenPaths, _) = PageOf(privilegedEnvelope);
        Assert.Equal(LegacyBillHistorySqlServerFixture.ExpectedHiddenHistoryRows, hiddenTotal);
        Assert.All(hiddenPaths, p => Assert.Equal(LegacyBillHistorySqlServerFixture.HiddenDocumentPath, p));
    }

    // ==================== 3. 零 / 负数 / 缺失 Oid 与权限撤销 / 禁用 / 删除身份 ====================

    [Fact]
    public async Task 零负数与缺失Oid及权限撤销禁用身份一律零历史()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();

        foreach (var badOid in new long[] { 0, -1 })
        {
            var denied = Envelope(await NewController(db, _fixture.RestrictedUserId)
                .GetBillLogs("sales-order", badOid));
            Assert.Equal(ErrorCodes.InvalidParameter, denied.Code);
            Assert.Null(denied.Data);
        }

        var missing = Envelope(await NewController(db, _fixture.RestrictedUserId)
            .GetBillLogs("sales-order", LegacyBillHistorySqlServerFixture.MissingOid));
        Assert.Equal(ErrorCodes.NotFound, missing.Code);
        Assert.Null(missing.Data);

        // 缺失身份 / 已删除身份按未认证拒绝；已禁用 / 无菜单 / 仅导出菜单 / 撤销授权按权限不足拒绝。
        var deniedCases = new (long? UserId, int Expected)[]
        {
            (null, ErrorCodes.Unauthorized),
            (_fixture.DeletedUserId, ErrorCodes.Unauthorized),
            (_fixture.DisabledUserId, ErrorCodes.Forbidden),
            (_fixture.MenuLessUserId, ErrorCodes.Forbidden),
            (_fixture.ExportOnlyUserId, ErrorCodes.Forbidden),
            (_fixture.RevokedUserId, ErrorCodes.Forbidden),
        };

        foreach (var (userId, expected) in deniedCases)
        {
            var envelope = Envelope(await NewController(db, userId)
                .GetBillLogs("sales-order", LegacyBillHistorySqlServerFixture.VisibleOids[0]));
            Assert.Equal(expected, envelope.Code);
            Assert.Null(envelope.Data);
        }
    }

    // ==================== 4. 有界分页与计数 ====================

    [Fact]
    public async Task 分页与计数稳定且页大小有界()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var controller = NewController(db, _fixture.RestrictedUserId);
        var oid = LegacyBillHistorySqlServerFixture.VisibleOids[0];

        var first = PageOf(Envelope(await controller.GetBillLogs("sales-order", oid, 1, 2)));
        var second = PageOf(Envelope(await controller.GetBillLogs("sales-order", oid, 2, 2)));
        var third = PageOf(Envelope(await controller.GetBillLogs("sales-order", oid, 3, 2)));

        Assert.Equal(LegacyBillHistorySqlServerFixture.ExpectedVisibleHistoryRows, first.Total);
        Assert.Equal(LegacyBillHistorySqlServerFixture.ExpectedVisibleHistoryRows, second.Total);
        Assert.Equal(2, first.Paths.Count);
        Assert.Equal(2, second.Paths.Count);
        Assert.Equal(2, third.Paths.Count);
        Assert.Empty(first.Ids.Intersect(second.Ids));
        Assert.Empty(first.Ids.Intersect(third.Ids));
        Assert.Empty(second.Ids.Intersect(third.Ids));

        var bounded = PageOf(Envelope(await controller.GetBillLogs("sales-order", oid, 1, 100000)));
        Assert.Equal(LegacyBillHistoryRules.MaxPageSize, bounded.PageSize);

        var overflow = Envelope(await controller.GetBillLogs("sales-order", oid, int.MaxValue,
            LegacyBillHistoryRules.MaxPageSize));
        Assert.Equal(ErrorCodes.InvalidParameter, overflow.Code);
        Assert.Null(overflow.Data);
    }

    // ==================== 5. 不可用历史源 + 拒绝路径零写入零通知 ====================

    [Fact]
    public async Task 不可用历史源显式environment_blocked且拒绝路径零写入零通知()
    {
        Guard();
        var before = await SnapshotAsync();

        await using var db = _fixture.CreateDbContext();
        var controller = NewController(db, _fixture.RestrictedUserId);

        // sales-order 旧表存在但 payment 旧表刻意不存在 → 显式 environment-blocked，绝不静默返回规范表 / 空集。
        var blocked = Envelope(await NewController(db, _fixture.PrivilegedUserId).GetBillLogs("payment", 1));
        Assert.Equal(ReportConfigurationExecutionLimits.ErrorCodeEnvironmentUnsupported, blocked.Code);
        Assert.Contains("environment-blocked", blocked.Message, StringComparison.Ordinal);
        Assert.Null(blocked.Data);

        // 一轮完整读取 / 拒绝之后，操作日志、通知、规范业务表与旧库行数全部不变
        // （保留既有库存来源单据审计与原始失败日志）。
        Envelope(await controller.GetBillLogs("sales-order", LegacyBillHistorySqlServerFixture.HiddenOids[0]));
        Envelope(await controller.GetBillLogs("sales-order", 0));
        Envelope(await controller.GetBillLogs("sales-order", LegacyBillHistorySqlServerFixture.VisibleOids[0], 1, 200));
        Envelope(await controller.GetBillLogs("stock-out", LegacyBillHistorySqlServerFixture.OtherFamilyOid));

        AssertUnchanged(before, await SnapshotAsync());
        Assert.False(db.ChangeTracker.HasChanges());
    }

    // ==================== 6. 两个独立连接竞态 ====================

    [Fact]
    public async Task 独立连接竞态_同一可见单据历史一致且零写入()
    {
        Guard();
        var before = await SnapshotAsync();

        await using var dbA = _fixture.CreateDbContext();
        await using var dbB = _fixture.CreateDbContext();
        var controllerA = NewController(dbA, _fixture.RestrictedUserId);
        var controllerB = NewController(dbB, _fixture.RestrictedUserId);
        var oid = LegacyBillHistorySqlServerFixture.VisibleOids[0];

        using var gate = new SemaphoreSlim(0, 2);
        async Task<(int Total, List<long> Ids)> ReadAsync(BillProcController controller)
        {
            await gate.WaitAsync();
            var page = PageOf(Envelope(await controller.GetBillLogs("sales-order", oid, 1, 200)));
            return (page.Total, page.Ids);
        }

        var runA = ReadAsync(controllerA);
        var runB = ReadAsync(controllerB);
        gate.Release(2);

        var resultA = await runA;
        var resultB = await runB;

        Assert.Equal(LegacyBillHistorySqlServerFixture.ExpectedVisibleHistoryRows, resultA.Total);
        Assert.Equal(resultA.Total, resultB.Total);
        Assert.Equal(resultA.Ids.OrderBy(x => x).ToArray(), resultB.Ids.OrderBy(x => x).ToArray());

        AssertUnchanged(before, await SnapshotAsync());
    }

    [Fact]
    public async Task 独立连接竞态_越权历史与无菜单账号同时收敛且零写入()
    {
        Guard();
        var before = await SnapshotAsync();

        await using var dbA = _fixture.CreateDbContext();
        await using var dbB = _fixture.CreateDbContext();
        var scopedController = NewController(dbA, _fixture.RestrictedUserId);
        var menuLessController = NewController(dbB, _fixture.MenuLessUserId);

        using var gate = new SemaphoreSlim(0, 2);
        async Task<int> ForeignAsync(BillProcController controller)
        {
            await gate.WaitAsync();
            var envelope = Envelope(await controller.GetBillLogs("sales-order",
                LegacyBillHistorySqlServerFixture.HiddenOids[0]));
            return envelope.Data is null ? envelope.Code : 0;
        }

        async Task<int> MenuLessAsync(BillProcController controller)
        {
            await gate.WaitAsync();
            var envelope = Envelope(await controller.GetBillLogs("sales-order",
                LegacyBillHistorySqlServerFixture.VisibleOids[0]));
            return envelope.Data is null ? envelope.Code : 0;
        }

        var foreignRun = ForeignAsync(scopedController);
        var menuLessRun = MenuLessAsync(menuLessController);
        gate.Release(2);

        Assert.Equal(ErrorCodes.NotFound, await foreignRun);
        Assert.Equal(ErrorCodes.Forbidden, await menuLessRun);

        AssertUnchanged(before, await SnapshotAsync());
    }
}

/// <summary>
/// ERP-406 旧单据操作历史真实 SQL 集成测试夹具：GUID 独占 <c>NEWERP_AUTOTEST</c> 目标，任何库访问之前拒绝非专用目标，
/// 发现同名库已存在立即拒绝（绝不 drop / reset / 复用）；建库后补齐完整 NEWERP 结构与种子数据，
/// 再以受控旧库结构 / 旧库行与既有授权（既有功能菜单 + 业务员客户数据范围）驱动真实控制器与真实受控只读服务。
/// </summary>
public sealed class LegacyBillHistorySqlServerFixture : IAsyncLifetime
{
    /// <summary>专用实例（精确匹配）。</summary>
    public const string InstanceTarget = @"(localdb)\NEWERP_AutoAcceptance";

    /// <summary>库名前缀（必须为 NEWERP_AUTOTEST）。</summary>
    public const string DatabasePrefix = "NEWERP_AUTOTEST";

    private const string DefaultDatabaseName = DatabasePrefix + "_LBHIST";

    /// <summary>同族跨客户 + 跨族同单号（用于证明精确路径 / 模块边界）。</summary>
    public const string DuplicateBillNo = "SO-DUP";

    /// <summary>可见客户（受限业务员名下）旧库行 Oid。</summary>
    public static readonly long[] VisibleOids = { 8001L, 8002L };

    /// <summary>隐藏客户（不在受限业务员名下）旧库行 Oid。</summary>
    public static readonly long[] HiddenOids = { 8101L, 8102L };

    /// <summary>跨族同单号旧单据的 Oid（stock-out 在隔离库刻意无旧表）。</summary>
    public const long OtherFamilyOid = 7001L;

    /// <summary>不存在于旧库的 Oid。</summary>
    public const long MissingOid = 999999L;

    /// <summary>可见单据的精确历史行数（5 条基础路径 + 1 条有限动作段）。</summary>
    public const int ExpectedVisibleHistoryRows = 6;

    /// <summary>隐藏单据（仅特权可见）的精确历史行数。</summary>
    public const int ExpectedHiddenHistoryRows = 1;

    /// <summary>可见单据的有限允许历史路径（精确匹配集合）。</summary>
    public static readonly string[] VisibleHistoryPaths =
    {
        $"/api/v2/bills/sales-order/{VisibleOids[0]}",
        $"/api/v2/bills/sales-order/{VisibleOids[0]}/audit",
    };

    /// <summary>前缀碰撞路径（<c>…/8001</c> 的下一数字位）→ 绝不匹配。</summary>
    public static readonly string CollisionPath = $"/api/v2/bills/sales-order/{VisibleOids[0]}0";

    /// <summary>非有限动作段 → 绝不匹配。</summary>
    public static readonly string NonFiniteActionPath = $"/api/v2/bills/sales-order/{VisibleOids[0]}/audit-extra";

    /// <summary>缺少权威记录标识的旧日志路径 → 省略。</summary>
    public const string LegacyNoIdentifierPath = "/api/v2/bills/sales-order/save";

    /// <summary>跨族同单号历史路径 → 绝不匹配。</summary>
    public static readonly string OtherFamilyPath = $"/api/v2/bills/stock-out/{OtherFamilyOid}";

    /// <summary>跨客户同单号历史路径 → 受限账号绝不匹配。</summary>
    public static readonly string HiddenDocumentPath = $"/api/v2/bills/sales-order/{HiddenOids[0]}";

    public string ConnectionString { get; private set; } = string.Empty;

    public long PrivilegedUserId { get; private set; }
    public long RestrictedUserId { get; private set; }
    public long MenuLessUserId { get; private set; }
    public long ExportOnlyUserId { get; private set; }
    public long DisabledUserId { get; private set; }
    public long DeletedUserId { get; private set; }
    public long RevokedUserId { get; private set; }
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

        Console.WriteLine($"[ERP-406] 目标库护栏放行（实例 {InstanceTarget}，库名前缀 {DatabasePrefix}）。");
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

        Console.WriteLine("[ERP-406] 集成场景就绪：全新 GUID 库 + 完整 NEWERP 结构 + 种子数据（不清理既有行）。");
    }

    // ==================== 既有授权（不新增权限模型）+ 受控旧库夹具 ====================

    private async Task SeedAsync()
    {
        await using var db = CreateDbContext();

        // 1) 特权账号（系统内置角色，沿用既有全部访问口径）。
        var privilegedRole = new SysRole
        {
            RoleName = "ERP406 特权角色",
            RoleCode = $"ERP406-P-{Guid.NewGuid():N}",
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

        // 2) 受限业务员：既有功能菜单（sales-order / stock-out）+ 业务员客户数据范围。
        var restricted = NewUser(UserStatus.Enabled);
        db.SysUsers.Add(restricted);
        await db.SaveChangesAsync();
        var restrictedRoleId = await AddRoleAsync(db, new[] { "sales-order", "stock-out" });
        db.SysUserRoles.Add(new SysUserRole { UserId = restricted.Id, RoleId = restrictedRoleId });
        await db.SaveChangesAsync();
        RestrictedUserId = restricted.Id;

        var employee = new BaseEmployee
        {
            EmployeeCode = restricted.UserName,
            EmployeeName = "ERP406 受限业务员",
            IsSalesman = true,
            Status = 1
        };
        db.BaseEmployees.Add(employee);
        await db.SaveChangesAsync();
        SalesmanId = employee.Id;

        var customerA = new BaseCustomer
        {
            CustomerCode = $"C-A-{Guid.NewGuid():N}",
            CustomerName = "ERP406 可见客户",
            EmpId = employee.Id,
            Status = 1,
            CreditStatus = "正常"
        };
        var customerB = new BaseCustomer
        {
            CustomerCode = $"C-B-{Guid.NewGuid():N}",
            CustomerName = "ERP406 隐藏客户",
            EmpId = null,
            Status = 1,
            CreditStatus = "正常"
        };
        db.BaseCustomers.AddRange(customerA, customerB);
        await db.SaveChangesAsync();
        CustomerAId = customerA.Id;
        CustomerBId = customerB.Id;

        // 3) 撤销授权账号：先具备 sales-order 菜单，再物理移除角色菜单关联（下一次请求立即收敛）。
        var revoked = NewUser(UserStatus.Enabled);
        db.SysUsers.Add(revoked);
        await db.SaveChangesAsync();
        var revokedRoleId = await AddRoleAsync(db, new[] { "sales-order" });
        db.SysUserRoles.Add(new SysUserRole { UserId = revoked.Id, RoleId = revokedRoleId });
        await db.SaveChangesAsync();
        RevokedUserId = revoked.Id;
        db.SysRoleMenus.RemoveRange(db.SysRoleMenus.Where(rm => rm.RoleId == revokedRoleId));
        await db.SaveChangesAsync();

        // 4) 拒绝侧账号：无菜单 / 仅导出菜单 / 已禁用 / 已删除。
        var menuLess = NewUser(UserStatus.Enabled);
        db.SysUsers.Add(menuLess);
        await db.SaveChangesAsync();
        db.SysUserRoles.Add(new SysUserRole
        {
            UserId = menuLess.Id,
            RoleId = await AddRoleAsync(db, Array.Empty<string>())
        });
        await db.SaveChangesAsync();
        MenuLessUserId = menuLess.Id;

        var exportOnly = NewUser(UserStatus.Enabled);
        db.SysUsers.Add(exportOnly);
        await db.SaveChangesAsync();
        db.SysUserRoles.Add(new SysUserRole
        {
            UserId = exportOnly.Id,
            RoleId = await AddRoleAsync(db, new[] { "sales-order-export" })
        });
        await db.SaveChangesAsync();
        ExportOnlyUserId = exportOnly.Id;

        var disabled = NewUser(UserStatus.Disabled);
        db.SysUsers.Add(disabled);
        await db.SaveChangesAsync();
        db.SysUserRoles.Add(new SysUserRole
        {
            UserId = disabled.Id,
            RoleId = await AddRoleAsync(db, new[] { "sales-order" })
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
            RoleId = await AddRoleAsync(db, new[] { "sales-order" })
        });
        await db.SaveChangesAsync();
        DeletedUserId = deleted.Id;

        // 5) 受控旧库结构 + 两位客户的旧库行（仅在本隔离 GUID 库内创建，绝不触碰既有行）。
        await db.Database.ExecuteSqlRawAsync(LegacySchemaSql);
        await db.Database.ExecuteSqlRawAsync(LegacyRowsSql, CustomerAId, SalesmanId, CustomerBId);

        // 6) 受控历史行：精确命中可见单据的行计入，其余（跨族 / 跨客户 / 碰撞 / 非有限动作段 / 无记录标识 / 单号不符 / 已删除）排除。
        var title = BillProcController.BillTitles["sales-order"];
        var otherFamilyTitle = BillProcController.BillTitles["stock-out"];
        var basePath = $"/api/v2/bills/sales-order/{VisibleOids[0]}";

        for (var i = 0; i < 5; i++)
            AddLog(db, title, basePath, DuplicateBillNo);
        AddLog(db, title, basePath + "/audit", DuplicateBillNo);
        AddLog(db, title, CollisionPath, DuplicateBillNo);
        AddLog(db, title, NonFiniteActionPath, DuplicateBillNo);
        AddLog(db, title, LegacyNoIdentifierPath, DuplicateBillNo);
        AddLog(db, title, basePath, "SO-OTHER");
        AddLog(db, otherFamilyTitle, OtherFamilyPath, DuplicateBillNo);
        AddLog(db, title, HiddenDocumentPath, DuplicateBillNo);
        AddLog(db, title, basePath, DuplicateBillNo, deleted: true);
        AddLog(db, title, $"/api/v2/bills/sales-order/{VisibleOids[1]}", "SO-A2");
        await db.SaveChangesAsync();

        Console.WriteLine("[ERP-406] 既有授权 + 受控旧库 / 历史夹具就绪（受控只读，不新增权限模型）。");
    }

    private static SysUser NewUser(UserStatus status) => new()
    {
        UserName = $"erp406-{Guid.NewGuid():N}",
        PasswordHash = "hash",
        PasswordSalt = "salt",
        DisplayName = "ERP406 隔离账号",
        Status = status
    };

    private static void AddLog(ErpDbContext db, string module, string path, string billNo, bool deleted = false)
        => db.SysOperationLogs.Add(new SysOperationLog
        {
            UserName = "ERP406 操作员",
            Module = module,
            Action = "保存",
            Method = "POST",
            Path = path,
            BillNo = billNo,
            IpAddress = "127.0.0.1",
            StatusCode = 200,
            DurationMs = 1,
            CreatedAt = DateTime.Now,
            IsDeleted = deleted,
        });

    private static async Task<long> AddRoleAsync(ErpDbContext db, string[] menuCodes)
    {
        var role = new SysRole
        {
            RoleName = $"ERP406-{Guid.NewGuid():N}",
            RoleCode = $"ERP406-{Guid.NewGuid():N}",
            IsSystem = false
        };
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
    /// 受控旧库结构（仅本隔离 GUID 库）：与读侧授权白名单一致的列（<c>Oid</c> + 授权表头列），
    /// 不含任何未授权列；<c>payment</c> / <c>stock-out</c> 等族刻意不建表以验证 environment-blocked。
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

    /// <summary>受控旧库行：2 行可见客户 + 2 行隐藏客户；可见 8001 与隐藏 8101 刻意使用相同单号（跨客户同单号）。</summary>
    private const string LegacyRowsSql = @"
INSERT INTO db_owner.SalesOrder
    (Oid, BillNo, OrderDate, CustId, EmpId, Currency, ExchangeRate, TotalAmount, DepositAmount, DepositRatio, DeliveryDate, Status, Remark)
VALUES
    (8001, N'SO-DUP', SYSDATETIME(), {0}, {1}, 2, 1, 100.5, 10.5, 10, NULL, 1, N'可见一'),
    (8002, N'SO-A2',  SYSDATETIME(), {0}, {1}, 2, 1, 200.5, 20.5, 10, NULL, 1, N'可见二'),
    (8101, N'SO-DUP', SYSDATETIME(), {2}, {1}, 2, 1, 300.5, 30.5, 10, NULL, 1, N'隐藏一'),
    (8102, N'SO-B2',  SYSDATETIME(), {2}, {1}, 2, 1, 400.5, 40.5, 10, NULL, 1, N'隐藏二');";
}

/// <summary>专用目标护栏的 fail-closed 覆盖：错误实例 / 错误库名 / 非集成安全必须在访问数据库之前被拒绝。</summary>
public sealed class LegacyBillHistoryTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;User Id=sa;Password=secret")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(
            () => LegacyBillHistorySqlServerFixture.AssertDedicatedTarget(connection));
}
