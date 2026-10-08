using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.DTOs;
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
/// ERP-361 收货计划实时授权 / 主数据 / 生命周期护栏的真实 SQL Server 集成测试（GUID 独占 <c>NEWERP_AUTOTEST</c> 目标）。
/// <list type="number">
/// <item>真实控制器与真实身份：已映射业务员 + 既有「收货计划」菜单可读；撤销菜单后立即收敛为拒绝；禁用账号不产生任何变更；</item>
/// <item>真实主数据：停用供应商 / 停用目的港 / 非港口字典项阻止新增与状态变更，失败的新增不改变计数；</item>
/// <item>失败编辑不改动库中收货计划；零数量阻止提交 / 审核且状态不变；</item>
/// <item><b>两条独立连接竞争</b>：并发「提交 vs 删除」与并发「双重审核」经同一把收货计划行锁（<c>UPDLOCK, HOLDLOCK</c>）
/// 串行化后恰好一方成功。</item>
/// </list>
/// <para>安全口径：目标必须为专用 localdb 实例 <c>NEWERP_AutoAcceptance</c>、库名前缀 <c>NEWERP_AUTOTEST</c> 且集成安全；
/// 每次运行只创建全新 GUID 后缀库，发现同名库已存在立即拒绝；绝不 drop / reset / 复用任何数据库，
/// 连接串只来自进程环境变量或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据。</para>
/// </summary>
public sealed class ReceivingPlanLifecycleSqlServerTests
    : IClassFixture<ReceivingPlanLifecycleSqlServerFixture>
{
    private readonly ReceivingPlanLifecycleSqlServerFixture _fixture;

    public ReceivingPlanLifecycleSqlServerTests(ReceivingPlanLifecycleSqlServerFixture fixture)
        => _fixture = fixture;

    /// <summary>访问数据库前复核专用目标护栏（实例名 / 库名前缀 / 集成安全）。</summary>
    private void Guard()
    {
        var target = new SqlConnectionStringBuilder(_fixture.ConnectionString);
        Assert.Equal("(localdb)\\NEWERP_AutoAcceptance", target.DataSource, ignoreCase: true);
        Assert.StartsWith(ReceivingPlanLifecycleSqlServerFixture.DatabasePrefix, target.InitialCatalog,
            StringComparison.OrdinalIgnoreCase);
        Assert.True(target.IntegratedSecurity);
    }

    // ==================== 1. 真实身份 / 真实菜单 ====================

    [Fact]
    public async Task Live_mapped_operator_with_receiving_plan_menu_can_list_and_read()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var (userId, _) = await SeedRestrictedOperatorAsync(db, withReceivingPlanMenu: true);
        var supplierId = await SeedSupplierAsync(db, "授权操作员供应商");
        var plan = await SeedPlanAsync(db, supplierId, DocumentStatus.Pending, totalQuantity: 5m);

        var ctl = NewController(db, userId);
        var page = AssertOk<PagedResult<ContainerReceivingPlan>>(
            await ctl.GetPaged(new PageQuery { Page = 1, PageSize = 50 }, null));
        Assert.Contains(page.Items, p => p.Id == plan.Id);
        Assert.Equal(plan.Id, AssertOk<ContainerReceivingPlan>(await ctl.GetById(plan.Id)).Id);
    }

    [Fact]
    public async Task Live_revoked_receiving_plan_menu_is_denied_and_reads_stop_immediately()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var (userId, _, roleId) = await SeedRestrictedOperatorWithRoleAsync(db);
        var supplierId = await SeedSupplierAsync(db, "撤销菜单供应商");
        await SeedPlanAsync(db, supplierId, DocumentStatus.Pending, totalQuantity: 5m);

        var grants = await db.SysRoleMenus.Where(rm => rm.RoleId == roleId && !rm.IsDeleted).ToListAsync();
        Assert.NotEmpty(grants);
        foreach (var grant in grants) grant.IsDeleted = true;
        await db.SaveChangesAsync();

        await AssertCode(ErrorCodes.Forbidden, () => NewController(db, userId).GetPaged(new PageQuery(), null));
    }

    [Fact]
    public async Task Live_disabled_identity_is_denied_without_any_mutation()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var supplierId = await SeedSupplierAsync(db, "禁用身份供应商");
        var plan = await SeedPlanAsync(db, supplierId, DocumentStatus.Pending, totalQuantity: 5m);
        var disabledUserId = await SeedDisabledUserAsync(db);

        var ctl = NewController(db, disabledUserId);
        await AssertCode(ErrorCodes.Forbidden, () => ctl.GetPaged(new PageQuery(), null));
        await AssertCode(ErrorCodes.Forbidden, () => ctl.Submit(plan.Id));
        await AssertCode(ErrorCodes.Forbidden, () => ctl.Delete(plan.Id));

        db.ChangeTracker.Clear();
        var stored = await db.ContainerReceivingPlans.AsNoTracking().SingleAsync(p => p.Id == plan.Id);
        Assert.Equal(DocumentStatus.Pending, stored.Status);
        Assert.False(stored.IsDeleted);
    }

    // ==================== 2. 真实主数据：失败不落库 / 失败编辑不改动 ====================

    [Fact]
    public async Task Live_create_requires_enabled_supplier_and_valid_port()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var adminId = await ResolveSeededAdminIdAsync(db);
        var enabledSupplier = await SeedSupplierAsync(db, "新增可用供应商");
        var disabledSupplier = await SeedSupplierAsync(db, "新增停用供应商", status: 0);
        var enabledPort = await SeedOtherInfoAsync(db, "Port", "SHANGHAI");
        var disabledPort = await SeedOtherInfoAsync(db, "Port", "NINGBO", status: 0);
        var wrongTypePort = await SeedOtherInfoAsync(db, "Currency", "USD");
        var ctl = NewController(db, adminId);

        await AssertCode(ErrorCodes.RuleConflict, () => ctl.Create(NewPlan(disabledSupplier)));
        await AssertCode(ErrorCodes.RuleConflict, () => ctl.Create(NewPlan(enabledSupplier, portId: disabledPort)));
        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.Create(NewPlan(enabledSupplier, portId: wrongTypePort)));

        var before = await db.ContainerReceivingPlans.AsNoTracking()
            .CountAsync(p => p.SupplierId == enabledSupplier);
        AssertOkObject(await ctl.Create(NewPlan(enabledSupplier, portId: enabledPort, totalQuantity: 8m)));

        db.ChangeTracker.Clear();
        Assert.Equal(before + 1, await db.ContainerReceivingPlans.AsNoTracking()
            .CountAsync(p => p.SupplierId == enabledSupplier));
    }

    [Fact]
    public async Task Live_failed_edit_leaves_plan_unchanged()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var (userId, _) = await SeedRestrictedOperatorAsync(db, withReceivingPlanMenu: true);
        var supplierId = await SeedSupplierAsync(db, "失败编辑供应商");
        var disabledSupplier = await SeedSupplierAsync(db, "失败编辑停用供应商", status: 0);
        var plan = await SeedPlanAsync(db, supplierId, DocumentStatus.Pending,
            totalQuantity: 4m, destination: "OLD-DEST");
        var ctl = NewController(db, userId);

        await AssertCode(ErrorCodes.RuleConflict, () => ctl.Update(plan.Id,
            NewPlan(disabledSupplier, totalQuantity: 999m, destination: "NEW-DEST")));

        db.ChangeTracker.Clear();
        var stored = await db.ContainerReceivingPlans.AsNoTracking().SingleAsync(p => p.Id == plan.Id);
        Assert.Equal(supplierId, stored.SupplierId);
        Assert.Equal(4m, stored.TotalQuantity);
        Assert.Equal("OLD-DEST", stored.Destination);
        Assert.Equal(DocumentStatus.Pending, stored.Status);
    }

    [Fact]
    public async Task Live_zero_quantity_blocks_submit_and_approve()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var adminId = await ResolveSeededAdminIdAsync(db);
        var supplierId = await SeedSupplierAsync(db, "零数量供应商");
        var pending = await SeedPlanAsync(db, supplierId, DocumentStatus.Pending, totalQuantity: 0m);
        var submitted = await SeedPlanAsync(db, supplierId, DocumentStatus.Submitted, totalQuantity: 0m);
        var ctl = NewController(db, adminId);

        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.Submit(pending.Id));
        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.Approve(submitted.Id));

        db.ChangeTracker.Clear();
        Assert.Equal(DocumentStatus.Pending,
            (await db.ContainerReceivingPlans.AsNoTracking().SingleAsync(p => p.Id == pending.Id)).Status);
        Assert.Equal(DocumentStatus.Submitted,
            (await db.ContainerReceivingPlans.AsNoTracking().SingleAsync(p => p.Id == submitted.Id)).Status);
    }

    // ==================== 3. 两条独立连接的并发状态变更 ====================

    [Fact]
    public async Task Concurrent_submit_and_delete_allow_exactly_one_winner_on_two_connections()
    {
        Guard();
        long planId;
        long adminId;
        await using (var db = _fixture.CreateDbContext())
        {
            adminId = await ResolveSeededAdminIdAsync(db);
            var supplierId = await SeedSupplierAsync(db, "并发提交删除供应商");
            planId = (await SeedPlanAsync(db, supplierId, DocumentStatus.Pending, totalQuantity: 6m)).Id;
        }

        var results = await RaceAsync(
            () => TrySubmitAsync(planId, adminId),
            () => TryDeleteAsync(planId, adminId));

        Assert.Equal(1, results.Count(r => r.Success));

        await using var verify = _fixture.CreateDbContext();
        var stored = await verify.ContainerReceivingPlans.AsNoTracking().SingleAsync(p => p.Id == planId);
        Assert.Equal(stored.IsDeleted, stored.Status != DocumentStatus.Submitted);
        if (stored.IsDeleted) Assert.Equal(DocumentStatus.Pending, stored.Status);
    }

    [Fact]
    public async Task Concurrent_double_approve_allows_exactly_one_winner_on_two_connections()
    {
        Guard();
        long planId;
        long adminId;
        await using (var db = _fixture.CreateDbContext())
        {
            adminId = await ResolveSeededAdminIdAsync(db);
            var supplierId = await SeedSupplierAsync(db, "并发双重审核供应商");
            planId = (await SeedPlanAsync(db, supplierId, DocumentStatus.Submitted, totalQuantity: 6m)).Id;
        }

        var results = await RaceAsync(
            () => TryApproveAsync(planId, adminId),
            () => TryApproveAsync(planId, adminId));

        Assert.Equal(1, results.Count(r => r.Success));

        await using var verify = _fixture.CreateDbContext();
        var stored = await verify.ContainerReceivingPlans.AsNoTracking().SingleAsync(p => p.Id == planId);
        Assert.Equal(DocumentStatus.Approved, stored.Status);
        Assert.False(stored.IsDeleted);
    }

    // ==================== 控制器工厂 / 并发脚手架 ====================

    private static ContainerReceivingPlanController NewController(ErpDbContext db, long? userId)
    {
        var ctl = new ContainerReceivingPlanController(db, new DocumentNumberService(db));
        SetUser(ctl, userId);
        return ctl;
    }

    private static void SetUser(ControllerBase controller, long? userId)
    {
        var claims = userId.HasValue
            ? new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }
            : Array.Empty<Claim>();
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) }
        };
    }

    /// <summary>两条独立连接在同一栅栏后同时发起状态变更（各自独立 DbContext / 连接 / 事务）。</summary>
    private static async Task<List<(bool Success, string Error)>> RaceAsync(
        Func<Task<(bool Success, string Error)>> first,
        Func<Task<(bool Success, string Error)>> second)
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

    private Task<(bool Success, string Error)> TrySubmitAsync(long planId, long userId)
        => TryAsync(async ctl => await ctl.Submit(planId), userId);

    private Task<(bool Success, string Error)> TryApproveAsync(long planId, long userId)
        => TryAsync(async ctl => await ctl.Approve(planId), userId);

    private Task<(bool Success, string Error)> TryDeleteAsync(long planId, long userId)
        => TryAsync(async ctl => await ctl.Delete(planId), userId);

    private async Task<(bool Success, string Error)> TryAsync(
        Func<ContainerReceivingPlanController, Task<IActionResult>> action, long userId)
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

    // ==================== 种子数据（SQL 自增主键，不显式指定 Id） ====================

    private static ContainerReceivingPlan NewPlan(long supplierId, long? portId = null,
        decimal totalQuantity = 1m, string destination = "", string remark = "ERP-361_INT")
        => new()
        {
            PlanDate = DateTime.Today,
            SupplierId = supplierId,
            PortId = portId,
            TotalQuantity = totalQuantity,
            Destination = destination,
            Remark = remark
        };

    private static async Task<long> SeedSupplierAsync(ErpDbContext db, string name, int status = 1)
    {
        var supplier = new BaseSupplier
        {
            SupplierCode = $"RP-S-{Guid.NewGuid():N}"[..30],
            SupplierName = name,
            Status = status
        };
        db.BaseSuppliers.Add(supplier);
        await db.SaveChangesAsync();
        return supplier.Id;
    }

    private static async Task<long> SeedOtherInfoAsync(ErpDbContext db, string infoType, string code, int status = 1)
    {
        var other = new BaseOtherInfo
        {
            InfoType = infoType,
            InfoCode = $"{code}-{Guid.NewGuid():N}"[..30],
            InfoName = $"{infoType}-{code}",
            Status = status
        };
        db.BaseOtherInfos.Add(other);
        await db.SaveChangesAsync();
        return other.Id;
    }

    private static async Task<ContainerReceivingPlan> SeedPlanAsync(ErpDbContext db, long supplierId,
        DocumentStatus status, decimal totalQuantity = 1m, long? portId = null, string destination = "")
    {
        var plan = new ContainerReceivingPlan
        {
            PlanNo = $"RP-P-{Guid.NewGuid():N}"[..30],
            PlanDate = DateTime.Today,
            SupplierId = supplierId,
            PortId = portId,
            TotalQuantity = totalQuantity,
            Destination = destination,
            Status = status,
            Remark = "ERP-361_INT"
        };
        db.ContainerReceivingPlans.Add(plan);
        await db.SaveChangesAsync();
        return plan;
    }

    private static async Task<(long UserId, long EmployeeId)> SeedRestrictedOperatorAsync(
        ErpDbContext db, bool withReceivingPlanMenu)
    {
        var (userId, employeeId, roleId) = await SeedRestrictedOperatorWithRoleAsync(db);
        if (!withReceivingPlanMenu)
        {
            var grants = await db.SysRoleMenus.Where(rm => rm.RoleId == roleId && !rm.IsDeleted).ToListAsync();
            foreach (var grant in grants) grant.IsDeleted = true;
            await db.SaveChangesAsync();
        }
        return (userId, employeeId);
    }

    /// <summary>播种受限制的收货计划操作员：业务员映射 + 既有「收货计划」菜单（复用 SeedData 菜单，不新增权限模型）。</summary>
    private static async Task<(long UserId, long EmployeeId, long RoleId)> SeedRestrictedOperatorWithRoleAsync(
        ErpDbContext db)
    {
        var code = $"rp-op-{Guid.NewGuid():N}";
        var employee = new BaseEmployee { EmployeeCode = code, EmployeeName = code, IsSalesman = true, Status = 1 };
        db.BaseEmployees.Add(employee);
        await db.SaveChangesAsync();

        var user = new SysUser
        {
            UserName = code, DisplayName = code, PasswordHash = "hash", PasswordSalt = "salt",
            Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        await db.SaveChangesAsync();

        var role = new SysRole { RoleName = "收货计划操作员", RoleCode = $"RpOp-{Guid.NewGuid():N}", IsSystem = false };
        db.SysRoles.Add(role);
        await db.SaveChangesAsync();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        await db.SaveChangesAsync();

        var menuId = await db.SysMenus.AsNoTracking()
            .Where(m => m.MenuCode == ReceivingPlanLifecycleRules.RequiredMenuCode && !m.IsDeleted)
            .Select(m => m.Id)
            .FirstAsync();
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menuId });
        await db.SaveChangesAsync();

        return (user.Id, employee.Id, role.Id);
    }

    private static async Task<long> SeedDisabledUserAsync(ErpDbContext db)
    {
        var user = new SysUser
        {
            UserName = $"rp-disabled-{Guid.NewGuid():N}", DisplayName = "禁用账号",
            PasswordHash = "hash", PasswordSalt = "salt", Status = UserStatus.Disabled
        };
        db.SysUsers.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    private static Task<long> ResolveSeededAdminIdAsync(ErpDbContext db)
        => db.SysUsers.AsNoTracking()
            .Where(u => u.UserName == SeedData.AdminUserName)
            .Select(u => u.Id)
            .FirstAsync();

    // ==================== 断言脚手架 ====================

    private static async Task AssertCode(int expected, Func<Task<IActionResult>> action)
    {
        var ex = await Assert.ThrowsAsync<BusinessException>(async () => await action());
        Assert.Equal(expected, ex.Code);
    }

    private static ApiResponse<T> AssertOkResponse<T>(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<ApiResponse<T>>(ok.Value);
        Assert.Equal(ErrorCodes.Success, resp.Code);
        return resp;
    }

    private static T AssertOk<T>(IActionResult result)
    {
        var resp = AssertOkResponse<T>(result);
        Assert.NotNull(resp.Data);
        return resp.Data!;
    }

    private static void AssertOkObject(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<ApiResponse<object>>(ok.Value);
        Assert.Equal(ErrorCodes.Success, resp.Code);
    }
}

/// <summary>
/// 专用 localdb 目标 Fixture（ERP-361）：把目标库初始化为完整 NEWERP 结构 + 种子数据，供真实 SQL Server 集成测试复用。
/// <para>安全口径：库名一律带本次运行的 GUID 后缀（绝不复用 / 绝不 drop 既有库）；任何数据库访问之前先校验
/// 实例名、库名前缀与集成安全，连接串只来自进程环境变量或专用 localdb 默认值。</para>
/// </summary>
public sealed class ReceivingPlanLifecycleSqlServerFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";
    public const string DefaultDatabaseName = "NEWERP_AUTOTEST_RECEIVINGPLAN_20261008";

    public string ConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = BuildDefaultConnectionString();

        AssertDedicatedTarget(connectionString);
        ConnectionString = connectionString;

        Console.WriteLine($"[ERP-361] 目标库护栏放行（实例含 {InstanceMarker}，库名前缀 {DatabasePrefix}）。");

        await EnsureFreshDatabaseAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public ErpDbContext CreateDbContext() => new(BuildOptions());

    private DbContextOptions<ErpDbContext> BuildOptions()
        => new DbContextOptionsBuilder<ErpDbContext>().UseSqlServer(ConnectionString).Options;

    private static string BuildDefaultConnectionString()
        => $"Server=(localdb)\\{InstanceMarker};Initial Catalog={DefaultDatabaseName}_{Guid.NewGuid():N};Integrated Security=true;TrustServerCertificate=true;";

    internal static void AssertDedicatedTarget(string connectionString)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        var server = builder.DataSource ?? string.Empty;
        var database = builder.InitialCatalog ?? string.Empty;

        Assert.Equal($"(localdb)\\{InstanceMarker}", server, ignoreCase: true);
        Assert.StartsWith(DatabasePrefix, database, StringComparison.OrdinalIgnoreCase);
        Assert.True(builder.IntegratedSecurity);
    }

    private async Task EnsureFreshDatabaseAsync()
    {
        var database = new SqlConnectionStringBuilder(ConnectionString).InitialCatalog;

        // 任何数据库访问之前再次护栏：绝不使用生产 / 非专用回退。
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

        Console.WriteLine("[ERP-361] 集成场景就绪：完整 NEWERP 结构 + 种子数据。");
    }
}

/// <summary>目标库护栏单元级校验：非专用目标必须在访问数据库之前被拒绝。</summary>
public sealed class ReceivingPlanTargetGuardTests
{
    [Theory]
    [InlineData("Server=production;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;User Id=sa;Password=x")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(() => ReceivingPlanLifecycleSqlServerFixture.AssertDedicatedTarget(connection));

    [Fact]
    public void Accepts_the_dedicated_localdb_target_with_integrated_security()
        => ReceivingPlanLifecycleSqlServerFixture.AssertDedicatedTarget(
            "Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_X;Integrated Security=true");
}





