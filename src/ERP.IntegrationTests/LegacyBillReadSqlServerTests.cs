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
using System.Data;
using System.Security.Claims;
using System.Text.Json;
using Xunit;

namespace ERP.IntegrationTests;

/// <summary>
/// ERP-405 旧单据读侧（<c>api/v2/bills</c> 查询 / 翻页导航 / 详情）的真实 SQL Server 集成测试
/// （GUID 独占 <c>NEWERP_AUTOTEST</c> 目标）。
/// <list type="number">
/// <item><b>真实规则 + 真实控制器 + 真实既有授权</b>：以新播种的既有功能菜单授权与业务员客户数据范围驱动真实
/// <see cref="BillProcController"/> 与真实 <see cref="ERP.Infrastructure.Reports.LegacyBillReadService"/>；
/// 不新增 / 不修改任何既有菜单 / 角色 / 用户授权，无匿名 / 管理员降级。</item>
/// <item><b>范围证明</b>：两位客户的旧库行在受限业务员下只返回授权客户的行 / 计数 / 首末前后 / 详情，
/// 越权锚点与越权 Oid 与「没有更多」返回同一结果（不泄露）；特权账号保留既有全量口径。</item>
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
public sealed class LegacyBillReadSqlServerTests : IClassFixture<LegacyBillReadSqlServerFixture>
{
    private readonly LegacyBillReadSqlServerFixture _fixture;

    public LegacyBillReadSqlServerTests(LegacyBillReadSqlServerFixture fixture) => _fixture = fixture;

    private static readonly string[] SnapshotTables =
    {
        "SysOperationLogs", "SysDingTalkLogs", "SalesOrders", "StockIns", "StockMovements",
        "SalesOrder", "SalesOrderDetail",
    };

    /// <summary>专用目标护栏（任何数据库访问之前）。</summary>
    private void Guard() => LegacyBillReadSqlServerFixture.AssertDedicatedTarget(_fixture.ConnectionString);

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

    private static (long Oid, int DetailCount) DetailOf(ApiResponse<object> envelope)
    {
        var root = Payload(envelope);
        return (root.GetProperty("main").GetProperty("Oid").GetInt64(),
            root.GetProperty("details").GetArrayLength());
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

    // ==================== 1. 范围受限的分页 / 计数（两位客户） ====================

    [Fact]
    public async Task 受限业务员分页只含授权客户的行与计数()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var controller = NewController(db, _fixture.RestrictedUserId);

        var page = Envelope(await controller.GetPaged("sales-order", new PageQuery { Page = 1, PageSize = 200 }, null));
        Assert.Equal(ErrorCodes.Success, page.Code);

        var (total, oids, customers) = PageOf(page);
        Assert.Equal(LegacyBillReadSqlServerFixture.VisibleOids.Length, total);
        Assert.Equal(
            LegacyBillReadSqlServerFixture.VisibleOids.OrderBy(o => o).ToArray(),
            oids.OrderBy(o => o).ToArray());
        Assert.All(customers, c => Assert.Equal(_fixture.CustomerAId, c));
        Assert.DoesNotContain(LegacyBillReadSqlServerFixture.HiddenOids[0], oids);
    }

    [Fact]
    public async Task 特权账号分页保留既有全量口径()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var controller = NewController(db, _fixture.PrivilegedUserId);

        var page = Envelope(await controller.GetPaged("sales-order", new PageQuery { Page = 1, PageSize = 200 }, null));
        Assert.Equal(ErrorCodes.Success, page.Code);

        var (total, oids, _) = PageOf(page);
        Assert.Equal(LegacyBillReadSqlServerFixture.VisibleOids.Length + LegacyBillReadSqlServerFixture.HiddenOids.Length, total);
        foreach (var hidden in LegacyBillReadSqlServerFixture.HiddenOids)
            Assert.Contains(hidden, oids);
    }

    [Fact]
    public async Task 分页第二页稳定且不跨范围()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var controller = NewController(db, _fixture.RestrictedUserId);

        var first = PageOf(Envelope(await controller.GetPaged("sales-order", new PageQuery { Page = 1, PageSize = 2 }, null)));
        var second = PageOf(Envelope(await controller.GetPaged("sales-order", new PageQuery { Page = 2, PageSize = 2 }, null)));

        Assert.Equal(LegacyBillReadSqlServerFixture.VisibleOids.Length, first.Total);
        Assert.Equal(2, first.Oids.Count);
        Assert.Single(second.Oids);
        Assert.Empty(first.Oids.Intersect(second.Oids));
        Assert.All(second.CustIds, c => Assert.Equal(_fixture.CustomerAId, c));
        // 稳定按 Oid 降序。
        Assert.Equal(LegacyBillReadSqlServerFixture.VisibleOids.OrderByDescending(o => o).Take(2).ToArray(), first.Oids.ToArray());
    }

    // ==================== 2. 范围受限的翻页导航与详情 ====================

    [Fact]
    public async Task 受限业务员翻页导航首末前后均受限且越权锚点不泄露()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var controller = NewController(db, _fixture.RestrictedUserId);

        var visible = LegacyBillReadSqlServerFixture.VisibleOids.OrderBy(o => o).ToArray();
        var hiddenAnchor = LegacyBillReadSqlServerFixture.HiddenOids[0];

        Assert.Equal(visible.First(), RowOid(Envelope(await controller.Navigate("sales-order", 0, "first"))));
        Assert.Equal(visible.Last(), RowOid(Envelope(await controller.Navigate("sales-order", 0, "last"))));
        Assert.Equal(visible[1], RowOid(Envelope(await controller.Navigate("sales-order", visible[0], "next"))));
        Assert.Equal(visible[0], RowOid(Envelope(await controller.Navigate("sales-order", visible[1], "prev"))));

        // 越权锚点：与「范围内没有更多」同一结果（NotFound），不泄露锚点存在性。
        var denied = Envelope(await controller.Navigate("sales-order", hiddenAnchor, "next"));
        Assert.Equal(ErrorCodes.NotFound, denied.Code);
        Assert.Null(denied.Data);

        var deniedPrev = Envelope(await controller.Navigate("sales-order", hiddenAnchor, "prev"));
        Assert.Equal(ErrorCodes.NotFound, deniedPrev.Code);
    }

    [Fact]
    public async Task 受限业务员详情_范围内返回关联明细_越权Oid返回不存在()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var controller = NewController(db, _fixture.RestrictedUserId);

        var allowed = Envelope(await controller.GetDetail(
            "sales-order", LegacyBillReadSqlServerFixture.DetailParentOid));
        Assert.Equal(ErrorCodes.Success, allowed.Code);
        var (oid, detailCount) = DetailOf(allowed);
        Assert.Equal(LegacyBillReadSqlServerFixture.DetailParentOid, oid);
        Assert.Equal(1, detailCount);

        var denied = Envelope(await controller.GetDetail(
            "sales-order", LegacyBillReadSqlServerFixture.HiddenOids[0]));
        Assert.Equal(ErrorCodes.NotFound, denied.Code);
        Assert.Null(denied.Data);
    }

    [Fact]
    public async Task 特权账号详情读取范围内任意行()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var controller = NewController(db, _fixture.PrivilegedUserId);

        var hidden = Envelope(await controller.GetDetail(
            "sales-order", LegacyBillReadSqlServerFixture.HiddenOids[0]));
        Assert.Equal(ErrorCodes.Success, hidden.Code);
        Assert.Equal(LegacyBillReadSqlServerFixture.HiddenOids[0], DetailOf(hidden).Oid);
    }


    // ==================== 3. 无归属族 / 身份与授权拒绝 / 缺表环境阻塞 ====================

    [Fact]
    public async Task 受限业务员在无权威客户归属族上failclosed且零写入()
    {
        Guard();
        var before = await SnapshotAsync();
        await using var db = _fixture.CreateDbContext();
        var controller = NewController(db, _fixture.RestrictedUserId);

        foreach (var familyKey in new[] { "purchase-order", "stock-in", "payment", "receiving-plan", "pre-loading" })
        {
            var denied = Envelope(await controller.GetPaged(familyKey, new PageQuery(), null));
            Assert.Equal(ErrorCodes.Forbidden, denied.Code);
            Assert.Contains("归属", denied.Message, StringComparison.Ordinal);
            Assert.Null(denied.Data);

            var detail = Envelope(await controller.GetDetail(familyKey, 1));
            Assert.Equal(ErrorCodes.Forbidden, detail.Code);
        }

        AssertUnchanged(before, await SnapshotAsync());
    }

    [Fact]
    public async Task 撤销禁用脱敏身份与未知族被拒绝且零写入()
    {
        Guard();
        var before = await SnapshotAsync();
        await using var db = _fixture.CreateDbContext();

        Assert.Equal(ErrorCodes.Unauthorized,
            Envelope(await NewController(db, null).GetPaged("sales-order", new PageQuery(), null)).Code);
        Assert.Equal(ErrorCodes.Unauthorized,
            Envelope(await NewController(db, _fixture.DeletedUserId).GetPaged("sales-order", new PageQuery(), null)).Code);
        Assert.Equal(ErrorCodes.Forbidden,
            Envelope(await NewController(db, _fixture.DisabledUserId).GetPaged("sales-order", new PageQuery(), null)).Code);
        Assert.Equal(ErrorCodes.Forbidden,
            Envelope(await NewController(db, _fixture.MenuLessUserId).GetPaged("sales-order", new PageQuery(), null)).Code);
        Assert.Equal(ErrorCodes.Forbidden,
            Envelope(await NewController(db, _fixture.ExportOnlyUserId).GetPaged("sales-order", new PageQuery(), null)).Code);
        Assert.Equal(ErrorCodes.InvalidParameter,
            Envelope(await NewController(db, _fixture.PrivilegedUserId).GetPaged("no-such-bill", new PageQuery(), null)).Code);

        // 默认值同样先授权。
        Assert.Equal(ErrorCodes.Unauthorized,
            Envelope(await NewController(db, null).GetDefaults("sales-order")).Code);
        Assert.Equal(ErrorCodes.Forbidden,
            Envelope(await NewController(db, _fixture.ExportOnlyUserId).GetDefaults("sales-order")).Code);

        AssertUnchanged(before, await SnapshotAsync());
    }

    [Fact]
    public async Task 缺表族environmentBlocked且绝不回退到规范表()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var canonicalBefore = await db.SalesOrders.CountAsync();
        var controller = NewController(db, _fixture.PrivilegedUserId);

        // payment 在隔离库中**没有**旧库单体表：显式 environment-blocked，绝不静默返回规范表数据。
        var blocked = Envelope(await controller.GetPaged("payment", new PageQuery(), null));
        Assert.Equal(ReportConfigurationExecutionLimits.ErrorCodeEnvironmentUnsupported, blocked.Code);
        Assert.Contains("environment-blocked", blocked.Message, StringComparison.Ordinal);
        Assert.Null(blocked.Data);

        var blockedDetail = Envelope(await controller.GetDetail("payment", 1));
        Assert.Equal(ReportConfigurationExecutionLimits.ErrorCodeEnvironmentUnsupported, blockedDetail.Code);

        Assert.Equal(canonicalBefore, await db.SalesOrders.CountAsync());
    }

    // ==================== 4. 恶意 / 溢出输入与零写入 ====================

    [Fact]
    public async Task 恶意与溢出输入被拒绝且零写入()
    {
        Guard();
        var before = await SnapshotAsync();
        await using var db = _fixture.CreateDbContext();
        var controller = NewController(db, _fixture.RestrictedUserId);

        Assert.Equal(ErrorCodes.InvalidParameter,
            Envelope(await controller.GetPaged("sales-order", new PageQuery { Page = 1, PageSize = 100000 }, null)).Code);
        Assert.Equal(ErrorCodes.InvalidParameter,
            Envelope(await controller.GetPaged("sales-order", new PageQuery { Page = int.MaxValue, PageSize = 200 }, null)).Code);
        Assert.Equal(ErrorCodes.InvalidParameter,
            Envelope(await controller.Navigate("sales-order", 1, "first;drop table")).Code);
        Assert.Equal(ErrorCodes.InvalidParameter,
            Envelope(await controller.GetDetail("sales-order", 0)).Code);

        // 注入式关键字按参数化处理（不拼接），返回 0 行且旧库结构完好。
        var inject = Envelope(await controller.GetPaged(
            "sales-order", new PageQuery { Keyword = "'; DROP TABLE db_owner.SalesOrder; --" }, null));
        Assert.Equal(ErrorCodes.Success, inject.Code);
        Assert.Equal(0, PageOf(inject).Total);

        AssertUnchanged(before, await SnapshotAsync());
    }


    // ==================== 5. 两个独立连接竞态（范围内一致 + 零写入） ====================

    [Fact]
    public async Task 两个独立连接竞态_受限分页一致且零写入()
    {
        Guard();
        var before = await SnapshotAsync();

        await using var dbA = _fixture.CreateDbContext();
        await using var dbB = _fixture.CreateDbContext();
        var controllerA = NewController(dbA, _fixture.RestrictedUserId);
        var controllerB = NewController(dbB, _fixture.RestrictedUserId);

        using var gate = new SemaphoreSlim(0, 2);
        async Task<(int Total, List<long> Oids)> ReadAsync(BillProcController controller)
        {
            await gate.WaitAsync();
            var page = Envelope(await controller.GetPaged("sales-order", new PageQuery { Page = 1, PageSize = 200 }, null));
            var (total, oids, _) = PageOf(page);
            return (total, oids);
        }

        var firstRun = ReadAsync(controllerA);
        var secondRun = ReadAsync(controllerB);
        gate.Release(2);
        var results = new[] { await firstRun, await secondRun };

        Assert.All(results, r => Assert.Equal(LegacyBillReadSqlServerFixture.VisibleOids.Length, r.Total));
        Assert.Equal(results[0].Oids.OrderBy(o => o).ToArray(), results[1].Oids.OrderBy(o => o).ToArray());
        Assert.All(results, r => Assert.DoesNotContain(LegacyBillReadSqlServerFixture.HiddenOids[0], r.Oids));

        AssertUnchanged(before, await SnapshotAsync());
    }

    [Fact]
    public async Task 两个独立连接竞态_越权详情与导航一致拒绝且零写入()
    {
        Guard();
        var before = await SnapshotAsync();

        await using var dbA = _fixture.CreateDbContext();
        await using var dbB = _fixture.CreateDbContext();
        var scopedController = NewController(dbA, _fixture.RestrictedUserId);
        var menuLessController = NewController(dbB, _fixture.ExportOnlyUserId);
        var hidden = LegacyBillReadSqlServerFixture.HiddenOids[0];

        using var gate = new SemaphoreSlim(0, 2);
        async Task<int> DetailDeniedAsync(BillProcController controller)
        {
            await gate.WaitAsync();
            return Envelope(await controller.GetDetail("sales-order", hidden)).Code;
        }

        async Task<int?> NavigateDeniedAsync(BillProcController controller)
        {
            await gate.WaitAsync();
            var envelope = Envelope(await controller.Navigate("sales-order", hidden, "next"));
            return envelope.Data is null ? envelope.Code : null;
        }

        var detailRun = DetailDeniedAsync(scopedController);
        var navigateRun = NavigateDeniedAsync(menuLessController);
        gate.Release(2);

        Assert.Equal(ErrorCodes.NotFound, await detailRun);
        Assert.Equal(ErrorCodes.Forbidden, await navigateRun);

        AssertUnchanged(before, await SnapshotAsync());
    }
}

/// <summary>
/// ERP-405 旧单据读侧真实 SQL 集成测试夹具：GUID 独占 <c>NEWERP_AUTOTEST</c> 目标，任何库访问之前拒绝非专用目标，
/// 发现同名库已存在立即拒绝（绝不 drop / reset / 复用）；建库后补齐完整 NEWERP 结构与种子数据，
/// 再以受控旧库结构（<c>db_owner.SalesOrder</c> / <c>db_owner.SalesOrderDetail</c> / <c>db_owner.PurchaseOrder</c>）
/// 与既有授权（既有功能菜单 + 业务员客户数据范围）驱动真实控制器与真实受控只读服务。
/// </summary>
public sealed class LegacyBillReadSqlServerFixture : IAsyncLifetime
{
    /// <summary>专用实例（精确匹配）。</summary>
    public const string InstanceTarget = @"(localdb)\NEWERP_AutoAcceptance";

    /// <summary>库名前缀（必须为 NEWERP_AUTOTEST）。</summary>
    public const string DatabasePrefix = "NEWERP_AUTOTEST";

    private const string DefaultDatabaseName = DatabasePrefix + "_LBRD";

    /// <summary>可见客户（受限业务员名下）旧库行 Oid。</summary>
    public static readonly long[] VisibleOids = { 9001L, 9002L, 9003L };

    /// <summary>隐藏客户（不在受限业务员名下）旧库行 Oid。</summary>
    public static readonly long[] HiddenOids = { 9101L, 9102L };

    /// <summary>带关联副表明细的可见行 Oid。</summary>
    public const long DetailParentOid = 9002L;

    public string ConnectionString { get; private set; } = string.Empty;

    public long PrivilegedUserId { get; private set; }
    public long RestrictedUserId { get; private set; }
    public long MenuLessUserId { get; private set; }
    public long ExportOnlyUserId { get; private set; }
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

        Console.WriteLine($"[ERP-405] 目标库护栏放行（实例 {InstanceTarget}，库名前缀 {DatabasePrefix}）。");
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

        Console.WriteLine("[ERP-405] 集成场景就绪：全新 GUID 库 + 完整 NEWERP 结构 + 种子数据（不清理既有行）。");
    }


    // ==================== 既有授权（不新增权限模型）+ 受控旧库夹具 ====================

    private async Task SeedAsync()
    {
        await using var db = CreateDbContext();

        // 1) 特权账号（系统内置角色，沿用既有全部访问口径）。
        var privilegedRole = new SysRole
        {
            RoleName = "ERP405 特权角色",
            RoleCode = $"ERP405-P-{Guid.NewGuid():N}",
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

        // 2) 受限业务员：既有功能菜单（sales-order / purchase-order / receipt / stock-out）+ 客户数据范围。
        var restricted = NewUser(UserStatus.Enabled);
        db.SysUsers.Add(restricted);
        await db.SaveChangesAsync();
        var restrictedRoleId = await AddRoleAsync(db, menuCodes: new[]
        {
            "sales-order", "purchase-order", "stock-in", "payment", "receiving-plan", "pre-loading"
        });
        db.SysUserRoles.Add(new SysUserRole { UserId = restricted.Id, RoleId = restrictedRoleId });
        await db.SaveChangesAsync();
        RestrictedUserId = restricted.Id;

        var employee = new BaseEmployee
        {
            EmployeeCode = restricted.UserName,
            EmployeeName = "ERP405 受限业务员",
            IsSalesman = true,
            Status = 1
        };
        db.BaseEmployees.Add(employee);
        await db.SaveChangesAsync();
        SalesmanId = employee.Id;

        var customerA = new BaseCustomer
        {
            CustomerCode = $"C-A-{Guid.NewGuid():N}",
            CustomerName = "ERP405 可见客户",
            EmpId = employee.Id,
            Status = 1,
            CreditStatus = "正常"
        };
        var customerB = new BaseCustomer
        {
            CustomerCode = $"C-B-{Guid.NewGuid():N}",
            CustomerName = "ERP405 隐藏客户",
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
            HiddenOids[0], CustomerBId, HiddenOids[1], DetailParentOid);

        Console.WriteLine("[ERP-405] 既有授权 + 受控旧库夹具就绪（受控只读，不新增权限模型）。");
    }

    private static SysUser NewUser(UserStatus status) => new()
    {
        UserName = $"erp405-{Guid.NewGuid():N}",
        PasswordHash = "hash",
        PasswordSalt = "salt",
        DisplayName = "ERP405 隔离账号",
        Status = status
    };

    private static async Task<long> AddRoleAsync(ErpDbContext db, string[] menuCodes)
    {
        var role = new SysRole { RoleName = $"ERP405-{Guid.NewGuid():N}", RoleCode = $"ERP405-{Guid.NewGuid():N}", IsSystem = false };
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
    /// 不含任何未授权列；<c>payment</c> 等族刻意不建表以验证 environment-blocked。
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
END
IF OBJECT_ID('db_owner.SalesOrderDetail') IS NULL
BEGIN
    CREATE TABLE db_owner.SalesOrderDetail (
        Oid BIGINT NOT NULL PRIMARY KEY,
        SalesOrderId BIGINT NOT NULL,
        ProductId BIGINT NOT NULL,
        ProductName NVARCHAR(200) NOT NULL,
        Spec NVARCHAR(200) NULL,
        Quantity DECIMAL(18,4) NOT NULL,
        Unit NVARCHAR(20) NULL,
        UnitPrice DECIMAL(18,4) NOT NULL,
        Amount DECIMAL(18,4) NOT NULL,
        DeliveryDate DATETIME2 NULL
    );
END
IF OBJECT_ID('db_owner.PurchaseOrder') IS NULL
BEGIN
    CREATE TABLE db_owner.PurchaseOrder (
        Oid BIGINT NOT NULL PRIMARY KEY,
        BillNo NVARCHAR(50) NOT NULL,
        OrderDate DATETIME2 NOT NULL,
        SupplierId BIGINT NULL,
        EmpId BIGINT NULL,
        Currency INT NOT NULL,
        ExchangeRate DECIMAL(18,6) NOT NULL,
        TotalAmount DECIMAL(18,4) NOT NULL,
        PaymentTerms NVARCHAR(200) NULL,
        DeliveryDate DATETIME2 NULL,
        Status INT NOT NULL,
        Remark NVARCHAR(500) NULL
    );
END";

    /// <summary>受控旧库行：3 行可见客户 + 2 行隐藏客户 + 1 行可见客户的副表明细（Oid 稳定、顺序稳定）。</summary>
    private const string LegacyRowsSql = @"
INSERT INTO db_owner.SalesOrder
    (Oid, BillNo, OrderDate, CustId, EmpId, Currency, ExchangeRate, TotalAmount, DepositAmount, DepositRatio, DeliveryDate, Status, Remark)
VALUES
    ({0}, N'SO-A1', SYSDATETIME(), {1}, {2}, 2, 1, 100.5, 10.5, 10, NULL, 1, N'可见一'),
    ({3}, N'SO-A2', SYSDATETIME(), {1}, {2}, 2, 1, 200.5, 20.5, 10, NULL, 1, N'可见二'),
    ({4}, N'SO-A3', SYSDATETIME(), {1}, {2}, 2, 1, 300.5, 30.5, 10, NULL, 1, N'可见三'),
    ({5}, N'SO-B1', SYSDATETIME(), {6}, {2}, 2, 1, 400.5, 40.5, 10, NULL, 1, N'隐藏一'),
    ({7}, N'SO-B2', SYSDATETIME(), {6}, {2}, 2, 1, 500.5, 50.5, 10, NULL, 1, N'隐藏二');

INSERT INTO db_owner.SalesOrderDetail
    (Oid, SalesOrderId, ProductId, ProductName, Spec, Quantity, Unit, UnitPrice, Amount, DeliveryDate)
VALUES
    (9201, {8}, 501, N'商品A', N'标准', 5, N'PCS', 20, 100, NULL);";
}

/// <summary>专用目标护栏的 fail-closed 覆盖：错误实例 / 错误库名 / 非集成安全必须在访问数据库之前被拒绝。</summary>
public sealed class LegacyBillReadTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;User Id=sa;Password=secret")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(
            () => LegacyBillReadSqlServerFixture.AssertDedicatedTarget(connection));
}
