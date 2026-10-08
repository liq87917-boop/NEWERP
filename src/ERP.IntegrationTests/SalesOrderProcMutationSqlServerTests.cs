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
using Microsoft.Extensions.Configuration;
using System.Security.Claims;
using Xunit;

namespace ERP.IntegrationTests;

/// <summary>
/// ERP-410 旧销售订单专用路由（<c>api/v2/sales-orders</c>）有限变更策略的真实 SQL Server 集成测试
/// （GUID 独占 <c>NEWERP_AUTOTEST</c> 目标）。
/// <list type="number">
/// <item><b>真实规则 + 真实控制器 + 真实既有授权</b>：以新播种的既有「销售订单」功能菜单授权驱动真实
/// <see cref="SalesOrderProcController"/>；不新增 / 不修改任何既有菜单 / 角色 / 用户授权，无匿名 / 管理员降级。</item>
/// <item><b>零变更证据</b>：五个写动作被拒后，业务订单 / 明细 / 库存 / 库存流水（来源单据审计）/
/// 单号流水（<c>SysDocumentNumberRules.CurrentSequence</c>）/ 操作日志 / 钉钉通知全部零变更。</item>
/// <item><b>两个独立连接竞态</b>：为 save / audit 各建一条竞态用例，各自新建两个独立 DbContext / 连接 / 控制器并门闩对齐并发；
/// 两条连接都必须 fail closed（无赢家、零写入）。<b>规范适配器竞态不适用</b>：今天不存在任何已验证适配器，
/// 旧写路径整体 fail closed，因此不存在「规范 vs 旧」双写竞争。</item>
/// <item><b>专用目标护栏</b>：必须在访问数据库之前精确命中 <c>(localdb)\NEWERP_AutoAcceptance</c> + <c>NEWERP_AUTOTEST</c>
/// 前缀 + <c>Integrated Security</c>；每次运行只创建一个全新 GUID 库，发现同名库已存在立即拒绝，
/// 绝不 drop / reset / 复用任何库，也绝不读取 appsettings / .env / 生产凭据。</item>
/// </list>
/// <para>保留既有库存来源单据审计与原始失败日志：本测试只读取计数与只读快照，不删除 / 不清理任何既有行
/// （被拒绝的请求本就零写入）。构建完成不等于阶段验收：只有本文件在受控 localdb 上真实执行通过才算验收证据。</para>
/// </summary>
public sealed class SalesOrderProcMutationSqlServerTests : IClassFixture<SalesOrderProcMutationSqlServerFixture>
{
    private readonly SalesOrderProcMutationSqlServerFixture _fixture;

    public SalesOrderProcMutationSqlServerTests(SalesOrderProcMutationSqlServerFixture fixture)
        => _fixture = fixture;

    /// <summary>伪造的旧库主键（绝不允许被推断为规范 Id）。</summary>
    private const long ForgedOid = 987654321L;

    // ==================== 专用目标护栏（任何数据库访问之前） ====================

    private void Guard()
    {
        var target = new SqlConnectionStringBuilder(_fixture.ConnectionString);
        Assert.Equal(SalesOrderProcMutationSqlServerFixture.InstanceTarget, target.DataSource, ignoreCase: true);
        Assert.StartsWith(SalesOrderProcMutationSqlServerFixture.DatabasePrefix, target.InitialCatalog,
            StringComparison.OrdinalIgnoreCase);
        Assert.True(target.IntegratedSecurity);
    }

    // ==================== 脚手架 ====================

    private SalesOrderProcController NewController(ErpDbContext db, long? userId)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = _fixture.ConnectionString
            })
            .Build();

        var controller = new SalesOrderProcController(new StoredProcedureService(configuration), db);

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

    /// <summary>播种一位**受限制**操作员（非系统内置角色 → 非特权），并按需授予既有功能菜单。</summary>
    private static async Task<(SysUser User, SysRole Role)> SeedOperatorAsync(ErpDbContext db, params string[] menuCodes)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var user = new SysUser
        {
            UserName = $"SOPMUT_{suffix}",
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = "集成受限操作员",
            Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        await db.SaveChangesAsync();

        var role = new SysRole { RoleName = $"SOPMUT_{suffix}", RoleCode = $"SOPMUT_{suffix}", IsSystem = false };
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

    /// <summary>伪造的旧标识 + 请求头金额载荷（绝不允许被信任 / 转换）。</summary>
    private static SalesOrderSaveRequest ForgedSaveRequest() => new()
    {
        Oid = ForgedOid,
        OrderDate = DateTime.Today,
        CustId = 4321,
        SalesmanId = 8765,
        Currency = 2,
        ExchangeRate = 7.1234m,
        TotalAmount = 1234567.89m,
        DepositRatio = 99m,
        DepositAmount = 1222222.22m,
        PaymentTerms = "FORGED",
        DeliveryDate = DateTime.Today.AddDays(3),
        ShippingMethod = "FORGED",
        PortId = 777,
        Remark = "forged legacy oid + header totals"
    };

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

    private async Task<(bool GotEnvelope, int Code, string Message)> AttemptAsync(long? userId, string action)
    {
        await using var db = _fixture.CreateDbContext();
        try
        {
            var controller = NewController(db, userId);
            IActionResult result = action switch
            {
                "save" => await controller.Save(ForgedSaveRequest()),
                "delete" => await controller.Delete(ForgedOid),
                "audit" => await controller.Audit(ForgedOid),
                "void" => await controller.Void(ForgedOid),
                "restore" => await controller.Restore(ForgedOid),
                _ => throw new ArgumentOutOfRangeException(nameof(action), action, "未知写动作")
            };
            return Envelope(result);
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
    // ==================== 1. 五个写动作一律 fail closed 且零变更 ====================

    [Theory]
    [InlineData("save")]
    [InlineData("delete")]
    [InlineData("audit")]
    [InlineData("void")]
    [InlineData("restore")]
    public async Task Legacy_dedicated_sales_order_actions_are_denied_and_write_nothing(string action)
    {
        Guard();

        long userId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var (user, _) = await SeedOperatorAsync(seed, SalesOrderProcController.LegacyFamilyKey);
            userId = user.Id;
        }

        var before = await SnapshotAsync();
        var result = await AttemptAsync(userId, action);

        Assert.True(result.GotEnvelope, result.Message);
        Assert.Equal(ErrorCodes.RuleConflict, result.Code);
        Assert.Contains("sp_Biz_SalesOrder", result.Message, StringComparison.Ordinal);
        Assert.Contains("/api/sales-orders", result.Message, StringComparison.Ordinal);
        await AssertNoAdditionalWritesAsync(before);
    }

    [Fact]
    public void Dedicated_route_reuses_erp404_sales_order_policy()
    {
        Guard();

        var policy = LegacyBillMutationRules.Resolve(SalesOrderProcController.LegacyFamilyKey);
        Assert.Equal("sales-order", policy.FamilyKey);
        Assert.Equal("SalesOrder", policy.TableName);
        Assert.Equal("db_owner.sp_Biz_SalesOrder", policy.ProcedureName);
        Assert.Equal("/api/sales-orders", policy.CanonicalRoute);
        Assert.False(policy.HasValidatedAdapter);
    }

    // ==================== 2. 既有授权 / 身份拒绝矩阵 ====================

    [Fact]
    public async Task Dedicated_save_without_granted_module_menu_is_forbidden_and_writes_nothing()
    {
        Guard();

        long userId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var (user, _) = await SeedOperatorAsync(seed); // 无任何既有菜单授权
            userId = user.Id;
        }

        var before = await SnapshotAsync();
        var result = await AttemptAsync(userId, "save");

        Assert.True(result.GotEnvelope, result.Message);
        Assert.Equal(ErrorCodes.Forbidden, result.Code);
        await AssertNoAdditionalWritesAsync(before);
    }

    [Fact]
    public async Task Dedicated_save_with_only_export_menu_is_forbidden_and_writes_nothing()
    {
        Guard();

        long userId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var (user, _) = await SeedOperatorAsync(seed, "sales-order-export");
            userId = user.Id;
        }

        var before = await SnapshotAsync();
        var result = await AttemptAsync(userId, "save");

        Assert.True(result.GotEnvelope, result.Message);
        Assert.Equal(ErrorCodes.Forbidden, result.Code);
        await AssertNoAdditionalWritesAsync(before);
    }

    [Fact]
    public async Task Dedicated_save_with_foreign_module_menu_is_forbidden_and_writes_nothing()
    {
        Guard();

        long userId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var (user, _) = await SeedOperatorAsync(seed, "purchase-order");
            userId = user.Id;
        }

        var before = await SnapshotAsync();
        var result = await AttemptAsync(userId, "audit");

        Assert.True(result.GotEnvelope, result.Message);
        Assert.Equal(ErrorCodes.Forbidden, result.Code);
        await AssertNoAdditionalWritesAsync(before);
    }

    [Fact]
    public async Task Dedicated_save_after_menu_revoked_is_forbidden_and_writes_nothing()
    {
        Guard();

        long userId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var (user, role) = await SeedOperatorAsync(seed, SalesOrderProcController.LegacyFamilyKey);
            userId = user.Id;

            foreach (var link in await seed.SysRoleMenus.Where(rm => rm.RoleId == role.Id).ToListAsync())
                seed.SysRoleMenus.Remove(link);
            await seed.SaveChangesAsync();
        }

        var before = await SnapshotAsync();
        var result = await AttemptAsync(userId, "save");

        Assert.True(result.GotEnvelope, result.Message);
        Assert.Equal(ErrorCodes.Forbidden, result.Code);
        await AssertNoAdditionalWritesAsync(before);
    }

    [Fact]
    public async Task Dedicated_save_without_identity_is_unauthorized_and_writes_nothing()
    {
        Guard();

        var before = await SnapshotAsync();
        var result = await AttemptAsync(null, "save");

        Assert.True(result.GotEnvelope, result.Message);
        Assert.Equal(ErrorCodes.Unauthorized, result.Code);
        await AssertNoAdditionalWritesAsync(before);
    }


    // ==================== 3. 伪造旧标识 / 金额载荷 ====================

    [Fact]
    public async Task Dedicated_save_with_forged_legacy_oid_and_monetary_payload_is_denied_and_writes_nothing()
    {
        Guard();

        long userId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var (user, _) = await SeedOperatorAsync(seed, SalesOrderProcController.LegacyFamilyKey);
            userId = user.Id;
        }

        var before = await SnapshotAsync();
        foreach (var action in new[] { "save", "delete", "audit", "void", "restore" })
        {
            var result = await AttemptAsync(userId, action);
            Assert.True(result.GotEnvelope, $"{action}: {result.Message}");
            Assert.Equal(ErrorCodes.RuleConflict, result.Code);
        }

        await AssertNoAdditionalWritesAsync(before);

        // 伪造的旧主键绝不被推断为规范 Id：没有任何规范销售订单命中该 Id。
        await using var verify = _fixture.CreateDbContext();
        Assert.Null(await verify.SalesOrders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == ForgedOid));
    }

    // ==================== 4. 两个独立连接竞态（无适配器 → 两条连接都必须 fail closed） ====================

    [Fact]
    public async Task Two_independent_connections_racing_dedicated_save_are_both_denied_with_zero_writes()
    {
        Guard();

        long userId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var (user, _) = await SeedOperatorAsync(seed, SalesOrderProcController.LegacyFamilyKey);
            userId = user.Id;
        }

        var before = await SnapshotAsync();
        var results = await RaceAsync(
            () => AttemptAsync(userId, "save"),
            () => AttemptAsync(userId, "save"));

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.True(r.GotEnvelope, r.Message));
        Assert.All(results, r => Assert.Equal(ErrorCodes.RuleConflict, r.Code));
        await AssertNoAdditionalWritesAsync(before);
    }

    [Fact]
    public async Task Two_independent_connections_racing_dedicated_save_and_audit_are_both_denied_with_zero_writes()
    {
        Guard();

        long userId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var (user, _) = await SeedOperatorAsync(seed, SalesOrderProcController.LegacyFamilyKey);
            userId = user.Id;
        }

        var before = await SnapshotAsync();
        var results = await RaceAsync(
            () => AttemptAsync(userId, "save"),
            () => AttemptAsync(userId, "audit"));

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.True(r.GotEnvelope, r.Message));
        Assert.All(results, r => Assert.Equal(ErrorCodes.RuleConflict, r.Code));
        await AssertNoAdditionalWritesAsync(before);
    }

}
/// <summary>
/// ERP-410 专用 localdb 夹具：目标必须是专用实例 <c>(localdb)\NEWERP_AutoAcceptance</c>、库名前缀
/// <c>NEWERP_AUTOTEST</c> 且 <c>Integrated Security=true</c>；每次运行只创建一个<strong>全新 GUID 后缀库</strong>，
/// 发现同名库已存在立即拒绝，绝不 drop / reset / 复用任何数据库，也绝不读取 appsettings / .env / 生产凭据。
/// </summary>
public sealed class SalesOrderProcMutationSqlServerFixture : IAsyncLifetime
{
    /// <summary>专用 localdb 实例标记（与 ERP-398 / ERP-399 / ERP-400 / ERP-402 / ERP-404 集成测试同源）。</summary>
    public const string InstanceMarker = "NEWERP_AutoAcceptance";

    /// <summary>专用 localdb 目标标记（精确匹配）。</summary>
    public const string InstanceTarget = "(localdb)\\NEWERP_AutoAcceptance";

    /// <summary>新库名前缀（GUID 独占；绝不复用既有库）。</summary>
    public const string DatabasePrefix = "NEWERP_AUTOTEST";

    /// <summary>默认库名（未提供环境变量时使用；每次运行再拼 GUID 后缀）。</summary>
    public const string DefaultDatabaseName = "NEWERP_AUTOTEST_SOPMUT_20261009";

    public string ConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = BuildDefaultConnectionString();

        AssertDedicatedTarget(connectionString);
        ConnectionString = connectionString;

        Console.WriteLine($"[ERP-410] 目标库护栏放行（实例 {InstanceTarget}，库名前缀 {DatabasePrefix}）。");
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

        Console.WriteLine("[ERP-410] 集成场景就绪：全新 GUID 库 + 完整 NEWERP 结构 + 种子数据（不清理既有行）。");
    }
}

/// <summary>专用目标护栏的 fail-closed 覆盖：错误实例 / 错误库名 / 非集成安全必须在访问数据库之前被拒绝。</summary>
public sealed class SalesOrderProcMutationTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;User Id=sa;Password=secret")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(
            () => SalesOrderProcMutationSqlServerFixture.AssertDedicatedTarget(connection));
}


