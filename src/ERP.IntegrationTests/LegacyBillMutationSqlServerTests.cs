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
using Xunit;

namespace ERP.IntegrationTests;

/// <summary>
/// ERP-404 旧单据（<c>api/v2/bills</c>）有限变更策略的真实 SQL Server 集成测试（GUID 独占 <c>NEWERP_AUTOTEST</c> 目标）。
/// <list type="number">
/// <item><b>真实规则 + 真实控制器 + 真实既有授权</b>：以新播种的既有功能菜单授权驱动真实
/// <see cref="BillProcController"/>；不新增 / 不修改任何既有菜单 / 角色 / 用户授权，无匿名 / 管理员降级。</item>
/// <item><b>零变更证据</b>：Save / 全部状态流转 / Excel 导入被拒后，业务表、明细、库存与库存流水（来源单据审计）、
/// 单号流水（<c>SysDocumentNumberRules.CurrentSequence</c>）、操作日志与钉钉通知全部零变更。</item>
/// <item><b>两个独立连接竞态</b>：每条竞态用例各自新建两个独立 DbContext / 连接 / 控制器并门闩对齐并发；
/// 两条连接都必须 fail closed（无赢家、零写入）。<b>规范适配器竞态不适用</b>：今天不存在任何已验证适配器，
/// 旧写路径整体 fail closed，因此不存在「规范 vs 旧」双写竞争。</item>
/// <item><b>专用目标护栏</b>：必须在访问数据库之前精确命中 <c>(localdb)\NEWERP_AutoAcceptance</c> + <c>NEWERP_AUTOTEST</c>
/// 前缀 + <c>Integrated Security</c>；每次运行只创建一个全新 GUID 库，发现同名库已存在立即拒绝，
/// 绝不 drop / reset / 复用任何库，也绝不读取 appsettings / .env / 生产凭据。</item>
/// </list>
/// <para>保留既有库存来源单据审计与原始失败日志：本测试只读取计数与只读快照，不删除 / 不清理任何既有行
/// （被拒绝的请求本就零写入）。构建完成不等于阶段验收：只有本文件在受控 localdb 上真实执行通过才算验收证据。</para>
/// </summary>
public sealed class LegacyBillMutationSqlServerTests : IClassFixture<LegacyBillMutationSqlServerFixture>
{
    private readonly LegacyBillMutationSqlServerFixture _fixture;

    public LegacyBillMutationSqlServerTests(LegacyBillMutationSqlServerFixture fixture)
        => _fixture = fixture;

    // ==================== 专用目标护栏（任何数据库访问之前） ====================

    private void Guard()
    {
        var target = new SqlConnectionStringBuilder(_fixture.ConnectionString);
        Assert.Equal(LegacyBillMutationSqlServerFixture.InstanceTarget, target.DataSource, ignoreCase: true);
        Assert.StartsWith(LegacyBillMutationSqlServerFixture.DatabasePrefix, target.InitialCatalog,
            StringComparison.OrdinalIgnoreCase);
        Assert.True(target.IntegratedSecurity);
    }

    // ==================== 脚手架 ====================

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
            http.User = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }, "Test"));

        controller.ControllerContext = new ControllerContext { HttpContext = http };
        return controller;
    }

    private static (bool GotEnvelope, int Code, string Message) Envelope(IActionResult result)
        => result is OkObjectResult ok && ok.Value is ApiResponse<object> envelope
            ? (true, envelope.Code, envelope.Message)
            : (false, 0, "非预期的控制器响应类型");

    private static BillSaveRequest SaveRequest(params (string Key, object? Value)[] fields)
    {
        var request = new BillSaveRequest { Oid = 0 };
        foreach (var (key, value) in fields)
            request.Fields[key] = value;
        return request;
    }

    /// <summary>播种一位**受限制**操作员（非系统内置角色 → 非特权），并按需授予既有功能菜单。</summary>
    private static async Task<(SysUser User, SysRole Role)> SeedOperatorAsync(ErpDbContext db, params string[] menuCodes)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var user = new SysUser
        {
            UserName = $"LBMUT_{suffix}",
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = "集成受限操作员",
            Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        await db.SaveChangesAsync();

        var role = new SysRole { RoleName = $"LBMUT_{suffix}", RoleCode = $"LBMUT_{suffix}", IsSystem = false };
        db.SysRoles.Add(role);
        await db.SaveChangesAsync();

        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        await db.SaveChangesAsync();

        foreach (var menuCode in menuCodes.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var menu = await db.SysMenus.FirstAsync(m => !m.IsDeleted && m.MenuCode == menuCode);
            db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
        }

        await db.SaveChangesAsync();
        return (user, role);
    }

    // ==================== 只读快照（零变更证据） ====================

    private sealed record WriteSnapshot(
        int SalesOrders,
        int SalesOrderDetails,
        int StockIns,
        int StockInDetails,
        int StockOuts,
        int Stocks,
        int StockMovements,
        int OperationLogs,
        int DingTalkLogs,
        string NumberRules);

    private static async Task<WriteSnapshot> ReadSnapshotAsync(ErpDbContext db) => new(
        await db.SalesOrders.CountAsync(),
        await db.SalesOrderDetails.CountAsync(),
        await db.StockIns.CountAsync(),
        await db.StockInDetails.CountAsync(),
        await db.StockOuts.CountAsync(),
        await db.Stocks.CountAsync(),
        await db.StockMovements.CountAsync(),
        await db.SysOperationLogs.CountAsync(),
        await db.SysDingTalkLogs.CountAsync(),
        string.Join(";", await db.SysDocumentNumberRules.AsNoTracking()
            .OrderBy(r => r.RuleCode)
            .Select(r => r.RuleCode + "=" + r.CurrentSequence)
            .ToListAsync()));

    private async Task<WriteSnapshot> SnapshotAsync()
    {
        await using var db = _fixture.CreateDbContext();
        return await ReadSnapshotAsync(db);
    }

    private async Task AssertNoAdditionalWritesAsync(WriteSnapshot before)
        => Assert.Equal(before, await SnapshotAsync());

    // ==================== 单连接尝试（不抛断言，供竞态复用） ====================

    private async Task<(bool GotEnvelope, int Code, string Message)> AttemptSaveAsync(
        long? userId, string billType, BillSaveRequest request)
    {
        await using var db = _fixture.CreateDbContext();
        try
        {
            return Envelope(await NewController(db, userId).Save(billType, request));
        }
        catch (Exception ex)
        {
            return (false, 0, ex.Message);
        }
    }

    private async Task<(bool GotEnvelope, int Code, string Message)> AttemptActionAsync(
        long? userId, string billType, long oid, string operation)
    {
        await using var db = _fixture.CreateDbContext();
        try
        {
            return Envelope(await NewController(db, userId).Action(billType, oid, operation));
        }
        catch (Exception ex)
        {
            return (false, 0, ex.Message);
        }
    }

    private async Task<(bool GotEnvelope, int Code, string Message)> AttemptImportAsync(
        long? userId, string billType)
    {
        await using var db = _fixture.CreateDbContext();
        try
        {
            return Envelope(await NewController(db, userId).Import(billType, null));
        }
        catch (Exception ex)
        {
            return (false, 0, ex.Message);
        }
    }

    /// <summary>两条独立连接以同一起跑线并发执行（门闩对齐）。</summary>
    private static async Task<List<T>> RaceAsync<T>(Func<Task<T>> first, Func<Task<T>> second)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<T> Run(Func<Task<T>> action)
        {
            await gate.Task;
            return await action();
        }

        var left = Run(first);
        var right = Run(second);
        gate.SetResult();
        return (await Task.WhenAll(left, right)).ToList();
    }

    // ==================== 1. Save / 状态流转 / Excel 导入一律 fail closed 且零变更 ====================

    [Fact]
    public async Task Legacy_save_is_denied_and_writes_nothing()
    {
        Guard();

        long userId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var (user, _) = await SeedOperatorAsync(seed, "sales-order");
            userId = user.Id;
        }

        var before = await SnapshotAsync();
        var result = await AttemptSaveAsync(userId, "sales-order", SaveRequest(("CustId", 1), ("TotalAmount", 100m)));

        Assert.True(result.GotEnvelope, result.Message);
        Assert.Equal(ErrorCodes.RuleConflict, result.Code);
        Assert.Contains("/api/sales-orders", result.Message, StringComparison.Ordinal);
        Assert.Contains("sp_Biz_", result.Message, StringComparison.Ordinal);
        await AssertNoAdditionalWritesAsync(before);
    }

    [Theory]
    [InlineData("delete")]
    [InlineData("audit")]
    [InlineData("unaudit")]
    [InlineData("void")]
    [InlineData("restore")]
    public async Task Legacy_action_transitions_are_denied_and_status_unchanged(string operation)
    {
        Guard();

        long userId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var (user, _) = await SeedOperatorAsync(seed, "stock-out");
            userId = user.Id;
        }

        var before = await SnapshotAsync();
        var result = await AttemptActionAsync(userId, "stock-out", 12, operation);

        Assert.True(result.GotEnvelope, result.Message);
        Assert.Equal(ErrorCodes.RuleConflict, result.Code);
        Assert.Contains("/api/stock-outs", result.Message, StringComparison.Ordinal);
        await AssertNoAdditionalWritesAsync(before);
    }

    [Fact]
    public async Task Legacy_import_is_denied_and_writes_nothing()
    {
        Guard();

        long userId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var (user, _) = await SeedOperatorAsync(seed, "stock-in");
            userId = user.Id;
        }

        var before = await SnapshotAsync();
        var result = await AttemptImportAsync(userId, "stock-in");

        Assert.True(result.GotEnvelope, result.Message);
        Assert.Equal(ErrorCodes.RuleConflict, result.Code);
        Assert.Contains("/api/stock-ins", result.Message, StringComparison.Ordinal);
        await AssertNoAdditionalWritesAsync(before);
    }

    [Fact]
    public async Task Legacy_stock_in_denial_preserves_source_audit_and_number_rules()
    {
        Guard();

        long userId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var (user, _) = await SeedOperatorAsync(seed, "stock-in");
            userId = user.Id;
        }

        var before = await SnapshotAsync();

        // 保存 + 审核（库存来源单据审计与单号流水必须在被拒时完全不变）。
        Assert.Equal(ErrorCodes.RuleConflict,
            (await AttemptSaveAsync(userId, "stock-in", SaveRequest(("SupplierId", 1), ("WarehouseId", 1)))).Code);
        Assert.Equal(ErrorCodes.RuleConflict, (await AttemptActionAsync(userId, "stock-in", 5, "audit")).Code);

        await AssertNoAdditionalWritesAsync(before);
    }

    // ==================== 2. 既有授权 / 身份拒绝矩阵 ====================

    [Fact]
    public async Task Legacy_save_without_granted_module_menu_is_forbidden_and_writes_nothing()
    {
        Guard();

        long userId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var (user, _) = await SeedOperatorAsync(seed); // 无任何既有菜单授权
            userId = user.Id;
        }

        var before = await SnapshotAsync();
        var result = await AttemptSaveAsync(userId, "sales-order", SaveRequest(("CustId", 1)));

        Assert.True(result.GotEnvelope, result.Message);
        Assert.Equal(ErrorCodes.Forbidden, result.Code);
        await AssertNoAdditionalWritesAsync(before);
    }

    [Fact]
    public async Task Legacy_save_with_only_export_menu_is_forbidden_and_writes_nothing()
    {
        Guard();

        long userId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var (user, _) = await SeedOperatorAsync(seed, "sales-order-export");
            userId = user.Id;
        }

        var before = await SnapshotAsync();
        var result = await AttemptSaveAsync(userId, "sales-order", SaveRequest(("CustId", 1)));

        Assert.True(result.GotEnvelope, result.Message);
        Assert.Equal(ErrorCodes.Forbidden, result.Code);
        await AssertNoAdditionalWritesAsync(before);
    }

    [Fact]
    public async Task Legacy_save_without_identity_is_unauthorized_and_writes_nothing()
    {
        Guard();

        var before = await SnapshotAsync();
        var result = await AttemptSaveAsync(null, "sales-order", SaveRequest(("CustId", 1)));

        Assert.True(result.GotEnvelope, result.Message);
        Assert.Equal(ErrorCodes.Unauthorized, result.Code);
        await AssertNoAdditionalWritesAsync(before);
    }

    // ==================== 3. 两个独立连接竞态（无适配器 → 两条连接都必须 fail closed） ====================

    [Fact]
    public async Task Two_independent_connections_racing_legacy_save_are_both_denied_with_zero_writes()
    {
        Guard();

        long userId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var (user, _) = await SeedOperatorAsync(seed, "sales-order");
            userId = user.Id;
        }

        var before = await SnapshotAsync();
        var results = await RaceAsync(
            () => AttemptSaveAsync(userId, "sales-order", SaveRequest(("CustId", 1), ("TotalAmount", 10m))),
            () => AttemptSaveAsync(userId, "sales-order", SaveRequest(("CustId", 1), ("TotalAmount", 10m))));

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.True(r.GotEnvelope, r.Message));
        Assert.All(results, r => Assert.Equal(ErrorCodes.RuleConflict, r.Code));
        await AssertNoAdditionalWritesAsync(before);
    }

    [Fact]
    public async Task Two_independent_connections_racing_legacy_audit_and_import_are_both_denied_with_zero_changes()
    {
        Guard();

        long userId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var (user, _) = await SeedOperatorAsync(seed, "stock-in");
            userId = user.Id;
        }

        var before = await SnapshotAsync();
        var results = await RaceAsync(
            () => AttemptActionAsync(userId, "stock-in", 7, "audit"),
            () => AttemptImportAsync(userId, "stock-in"));

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.True(r.GotEnvelope, r.Message));
        Assert.All(results, r => Assert.Equal(ErrorCodes.RuleConflict, r.Code));
        await AssertNoAdditionalWritesAsync(before);
    }

    [Fact]
    public async Task Legacy_save_is_denied_for_every_family_with_zero_writes()
    {
        Guard();
        var before = await SnapshotAsync();

        foreach (var policy in LegacyBillMutationRules.Families)
        {
            long userId;
            await using (var seed = _fixture.CreateDbContext())
            {
                var (user, _) = await SeedOperatorAsync(seed, policy.ModuleMenuCode);
                userId = user.Id;
            }

            var result = await AttemptSaveAsync(userId, policy.FamilyKey, SaveRequest(("Remark", "x")));

            Assert.True(result.GotEnvelope, $"{policy.FamilyKey}: {result.Message}");
            Assert.Equal(ErrorCodes.RuleConflict, result.Code);
            Assert.Contains(policy.CanonicalRoute, result.Message, StringComparison.Ordinal);
        }

        await AssertNoAdditionalWritesAsync(before);
    }

}

/// <summary>
/// ERP-404 专用 localdb 夹具：目标必须是专用实例 <c>(localdb)\NEWERP_AutoAcceptance</c>、库名前缀
/// <c>NEWERP_AUTOTEST</c> 且 <c>Integrated Security=true</c>；每次运行只创建一个<strong>全新 GUID 后缀库</strong>，
/// 发现同名库已存在立即拒绝，绝不 drop / reset / 复用任何数据库，也绝不读取 appsettings / .env / 生产凭据。
/// </summary>
public sealed class LegacyBillMutationSqlServerFixture : IAsyncLifetime
{
    /// <summary>专用 localdb 实例标记（与 ERP-398 / ERP-399 / ERP-400 / ERP-402 集成测试同源）。</summary>
    public const string InstanceMarker = "NEWERP_AutoAcceptance";

    /// <summary>专用 localdb 目标标记（精确匹配）。</summary>
    public const string InstanceTarget = "(localdb)\\NEWERP_AutoAcceptance";

    /// <summary>新库名前缀（GUID 独占；绝不复用既有库）。</summary>
    public const string DatabasePrefix = "NEWERP_AUTOTEST";

    /// <summary>默认库名（未提供环境变量时使用；每次运行再拼 GUID 后缀）。</summary>
    public const string DefaultDatabaseName = "NEWERP_AUTOTEST_LBMUT_20261009";

    public string ConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = BuildDefaultConnectionString();

        AssertDedicatedTarget(connectionString);
        ConnectionString = connectionString;

        Console.WriteLine($"[ERP-404] 目标库护栏放行（实例 {InstanceTarget}，库名前缀 {DatabasePrefix}）。");
        await CreateFreshDatabaseAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public ErpDbContext CreateDbContext() => new(BuildOptions());

    private DbContextOptions<ErpDbContext> BuildOptions()
        => new DbContextOptionsBuilder<ErpDbContext>().UseSqlServer(ConnectionString).Options;

    private static string BuildDefaultConnectionString()
        => $"Server={InstanceTarget};Initial Catalog={DefaultDatabaseName}_{Guid.NewGuid():N};"
           + "Integrated Security=true;TrustServerCertificate=true;";

    internal static void AssertDedicatedTarget(string connectionString)
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

        Console.WriteLine("[ERP-404] 集成场景就绪：全新 GUID 库 + 完整 NEWERP 结构 + 种子数据（不清理既有行）。");
    }
}

/// <summary>专用目标护栏的 fail-closed 覆盖：错误实例 / 错误库名 / 非集成安全必须在访问数据库之前被拒绝。</summary>
public sealed class LegacyBillMutationTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;User Id=sa;Password=secret")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(
            () => LegacyBillMutationSqlServerFixture.AssertDedicatedTarget(connection));
}

