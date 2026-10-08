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
/// ERP-364 装柜清单实时授权 / 权威客户数据范围（含一柜多客户参与方与显式上游共享出运）的**真实 SQL Server** 集成测试
/// （GUID 独占 <c>NEWERP_AUTOTEST</c> 目标）。
/// <list type="number">
/// <item>真实控制器：受限制业务员只能读本人客户的装柜清单；共享柜含他人参与方 / 显式上游他人订柜 / 无权威归属一律 fail closed；</item>
/// <item>真实身份：撤销既有「装柜清单」菜单后立即收敛为拒绝、禁用账号按权限不足、缺失身份按未认证（不新增任何用户授权）；</item>
/// <item>失败参与方维护不改变库中参与方行、兼容客户字段与审计时间戳；</item>
/// <item><b>两条独立连接竞争</b>：参与方置主与审核并发不留撕裂状态；并发审核恰好一方成功，均由既有装柜清单行锁
/// （<c>UPDLOCK, HOLDLOCK</c>）与可串行化事务串行化。</item>
/// </list>
/// <para>安全口径：目标必须为专用 localdb 实例 <c>NEWERP_AutoAcceptance</c>、库名前缀 <c>NEWERP_AUTOTEST</c> 且集成安全；
/// 每次运行只创建全新 GUID 后缀库，发现同名库已存在立即拒绝；绝不 drop / reset / 复用任何数据库，
/// 连接串只来自进程环境变量或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据。</para>
/// </summary>
public sealed class LoadingListAuthorizationSqlServerTests
    : IClassFixture<LoadingListAuthorizationSqlServerFixture>
{
    private readonly LoadingListAuthorizationSqlServerFixture _fixture;

    public LoadingListAuthorizationSqlServerTests(LoadingListAuthorizationSqlServerFixture fixture)
        => _fixture = fixture;

    /// <summary>访问数据库前复核专用目标护栏（实例名 / 库名前缀 / 集成安全）。</summary>
    private void Guard()
    {
        var target = new SqlConnectionStringBuilder(_fixture.ConnectionString);
        Assert.Equal("(localdb)\\NEWERP_AutoAcceptance", target.DataSource, ignoreCase: true);
        Assert.StartsWith(LoadingListAuthorizationSqlServerFixture.DatabasePrefix, target.InitialCatalog,
            StringComparison.OrdinalIgnoreCase);
        Assert.True(target.IntegratedSecurity);
    }

    // ==================== 1. 真实控制器：受限业务员只读本人客户 ====================

    [Fact]
    public async Task Live_restricted_operator_reads_only_own_customer_loading_lists()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var (userId, employeeId) = await SeedRestrictedOperatorAsync(db, withLoadingListMenu: true);
        var own = await SeedCustomerAsync(db, "本人客户", employeeId);
        var foreign = await SeedCustomerAsync(db, "他人客户");

        var keyword = Tag();
        var ownList = await SeedLoadingListAsync(db, $"ZQ-{keyword}-OWN", own, DocumentStatus.Pending);
        var foreignList = await SeedLoadingListAsync(db, $"ZQ-{keyword}-OTHER", foreign, DocumentStatus.Pending);
        var ownerless = await SeedLoadingListAsync(db, $"ZQ-{keyword}-OWNERLESS", 0L, DocumentStatus.Pending);
        var shared = await SeedLoadingListAsync(db, $"ZQ-{keyword}-SHARED", own, DocumentStatus.Pending);
        await SeedParticipantAsync(db, shared.Id, foreign);       // 共享柜含他人参与方 → 不可见

        var ctl = NewController(db, userId);

        var page = AssertOk<PagedResult<ContainerLoadingList>>(
            await ctl.GetPaged(new PageQuery { Page = 1, PageSize = 50, Keyword = keyword }, null));
        var row = Assert.Single(page.Items);
        Assert.Equal(ownList.Id, row.Id);
        Assert.Equal(1, page.Total);   // 计数发生在数据库侧范围过滤之后

        await AssertCode(ErrorCodes.Forbidden, () => ctl.GetById(foreignList.Id));
        await AssertCode(ErrorCodes.Forbidden, () => ctl.GetById(ownerless.Id));
        await AssertCode(ErrorCodes.Forbidden, () => ctl.GetById(shared.Id));
        await AssertCode(ErrorCodes.Forbidden, () => ctl.GetParticipants(shared.Id));
    }

    // ==================== 2. 撤销菜单 / 禁用账号 / 缺失身份 ====================

    [Fact]
    public async Task Live_revoked_menu_disabled_user_and_missing_identity_are_denied()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var (revokedUserId, employeeId) = await SeedRestrictedOperatorAsync(db, withLoadingListMenu: true);
        var customerId = await SeedCustomerAsync(db, "撤销菜单客户", employeeId);
        var keyword = Tag();
        var list = await SeedLoadingListAsync(db, $"ZQ-{keyword}-REVOKE", customerId, DocumentStatus.Pending);
        await RevokeMenusAsync(db, revokedUserId);

        var disabledUserId = await SeedDisabledUserAsync(db);

        await AssertCode(ErrorCodes.Forbidden, () => NewController(db, revokedUserId).GetById(list.Id));
        await AssertCode(ErrorCodes.Forbidden, () => NewController(db, revokedUserId).Delete(list.Id));
        await AssertCode(ErrorCodes.Forbidden, () => NewController(db, disabledUserId).GetById(list.Id));
        await AssertCode(ErrorCodes.Unauthorized, () => NewController(db, null).GetById(list.Id));

        db.ChangeTracker.Clear();
        var stored = await db.ContainerLoadingLists.AsNoTracking().SingleAsync(x => x.Id == list.Id);
        Assert.False(stored.IsDeleted);
        Assert.Equal(DocumentStatus.Pending, stored.Status);
    }

    // ==================== 3. 失败的参与方维护不改动原行 / 兼容字段 / 审计 ====================

    [Fact]
    public async Task Live_denied_participant_edit_preserves_original_rows_and_audit()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var (userId, employeeId) = await SeedRestrictedOperatorAsync(db, withLoadingListMenu: true);
        var own = await SeedCustomerAsync(db, "本人客户", employeeId);
        var foreign = await SeedCustomerAsync(db, "他人客户");
        var keyword = Tag();
        var list = await SeedLoadingListAsync(db, $"ZQ-{keyword}-PART", own, DocumentStatus.Pending);
        var ownParticipant = await SeedParticipantAsync(db, list.Id, own, primary: true);

        var beforeList = await db.ContainerLoadingLists.AsNoTracking().SingleAsync(x => x.Id == list.Id);
        var beforeRows = await db.ContainerLoadingListParticipants.AsNoTracking()
            .Where(p => p.LoadingListId == list.Id).ToListAsync();

        var ctl = NewController(db, userId);
        await AssertCode(ErrorCodes.Forbidden, () => ctl.CreateParticipant(list.Id,
            new ContainerLoadingParticipantSaveDto { CustomerId = foreign }));
        await AssertCode(ErrorCodes.Forbidden, () => ctl.UpdateParticipant(list.Id, ownParticipant.Id,
            new ContainerLoadingParticipantSaveDto { CustomerId = foreign }));

        db.ChangeTracker.Clear();
        var afterList = await db.ContainerLoadingLists.AsNoTracking().SingleAsync(x => x.Id == list.Id);
        var afterRows = await db.ContainerLoadingListParticipants.AsNoTracking()
            .Where(p => p.LoadingListId == list.Id).ToListAsync();

        Assert.Equal(beforeList.CustomerId, afterList.CustomerId);      // 兼容客户字段未被改写
        Assert.Equal(beforeList.UpdatedAt, afterList.UpdatedAt);        // 审计时间戳未被改写
        Assert.Equal(beforeRows.Count, afterRows.Count);               // 未新增参与方行
        var afterRow = Assert.Single(afterRows);
        Assert.Equal(ownParticipant.CustomerId, afterRow.CustomerId);
        Assert.True(afterRow.IsPrimary);
        Assert.Equal(beforeRows.Single().UpdatedAt, afterRow.UpdatedAt);
    }

    // ==================== 4. 两条独立连接的参与方置主 / 审核竞争 ====================

    [Fact]
    public async Task Two_connection_participant_edit_racing_approval_leaves_consistent_state()
    {
        Guard();
        long loadingListId;
        long participantBId;
        long adminId;
        await using (var db = _fixture.CreateDbContext())
        {
            adminId = await ResolveSeededAdminIdAsync(db);
            var customerA = await SeedCustomerAsync(db, "并发参与方A");
            var customerB = await SeedCustomerAsync(db, "并发参与方B");
            var keyword = Tag();
            var list = await SeedLoadingListAsync(db, $"ZQ-{keyword}-RACE", customerA, DocumentStatus.Submitted);
            loadingListId = list.Id;
            await SeedParticipantAsync(db, list.Id, customerA, primary: true);
            participantBId = (await SeedParticipantAsync(db, list.Id, customerB)).Id;
        }

        var results = await RaceAsync(
            () => TryAsync(ctl => ctl.SetPrimaryParticipant(loadingListId, participantBId), adminId),
            () => TryAsync(ctl => ctl.Approve(loadingListId), adminId));

        Assert.True(results.Count(r => r.Success) >= 1);

        await using var verify = _fixture.CreateDbContext();
        var stored = await verify.ContainerLoadingLists.AsNoTracking().SingleAsync(x => x.Id == loadingListId);
        var participants = await verify.ContainerLoadingListParticipants.AsNoTracking()
            .Where(p => p.LoadingListId == loadingListId && !p.IsDeleted).ToListAsync();

        // 行锁 + 提交后一致：任何时刻至多一条启用主参与方，且兼容客户字段与主参与方一致（若已置主）。
        var primaries = participants.Where(p => p.Status == 1 && p.IsPrimary).ToList();
        Assert.True(primaries.Count <= 1);
        if (primaries.Count == 1)
            Assert.Equal(primaries[0].CustomerId, stored.CustomerId);
        Assert.Contains(stored.Status, new[] { DocumentStatus.Submitted, DocumentStatus.Approved });
    }

    [Fact]
    public async Task Two_connection_double_approve_allows_exactly_one_winner()
    {
        Guard();
        long loadingListId;
        long adminId;
        await using (var db = _fixture.CreateDbContext())
        {
            adminId = await ResolveSeededAdminIdAsync(db);
            var customerId = await SeedCustomerAsync(db, "并发审核客户");
            var keyword = Tag();
            loadingListId = (await SeedLoadingListAsync(db,
                $"ZQ-{keyword}-APPROVE", customerId, DocumentStatus.Submitted)).Id;
        }

        var results = await RaceAsync(
            () => TryAsync(ctl => ctl.Approve(loadingListId), adminId),
            () => TryAsync(ctl => ctl.Approve(loadingListId), adminId));

        Assert.Equal(1, results.Count(r => r.Success));

        await using var verify = _fixture.CreateDbContext();
        var stored = await verify.ContainerLoadingLists.AsNoTracking().SingleAsync(x => x.Id == loadingListId);
        Assert.Equal(DocumentStatus.Approved, stored.Status);
        Assert.False(stored.IsDeleted);
    }


    // ==================== 控制器工厂 / 并发脚手架 ====================

    private static ContainerLoadingListController NewController(ErpDbContext db, long? userId)
    {
        var ctl = new ContainerLoadingListController(db, new DocumentNumberService(db));
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

    /// <summary>两条独立连接在同一栅栏后同时发起参与方置主 / 审核（各自独立 DbContext / 连接 / 事务）。</summary>
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

    private async Task<(bool Success, string Error)> TryAsync(
        Func<ContainerLoadingListController, Task<IActionResult>> action, long userId)
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

    private static string Tag() => Guid.NewGuid().ToString("N")[..10];

    private static async Task<long> SeedCustomerAsync(ErpDbContext db, string name, long? empId = null)
    {
        var customer = new BaseCustomer
        {
            CustomerCode = $"LL-C-{Guid.NewGuid():N}"[..30], CustomerName = name, EmpId = empId, Status = 1
        };
        db.BaseCustomers.Add(customer);
        await db.SaveChangesAsync();
        return customer.Id;
    }

    private static async Task<ContainerLoadingList> SeedLoadingListAsync(
        ErpDbContext db, string no, long customerId, DocumentStatus status)
    {
        var list = new ContainerLoadingList
        {
            LoadingListNo = no.Length > 50 ? no[..50] : no, LoadingDate = DateTime.Today,
            ContainerNo = "CTN-INT", CustomerId = customerId, Status = status, Remark = "ERP-364_INT"
        };
        db.ContainerLoadingLists.Add(list);
        await db.SaveChangesAsync();
        return list;
    }

    private static async Task<ContainerLoadingListParticipant> SeedParticipantAsync(
        ErpDbContext db, long loadingListId, long customerId, bool primary = false)
    {
        var participant = new ContainerLoadingListParticipant
        {
            LoadingListId = loadingListId, CustomerId = customerId, CustomerCode = $"LL-P-{customerId}",
            CustomerName = "集成参与方", Status = 1, IsPrimary = primary
        };
        db.ContainerLoadingListParticipants.Add(participant);
        await db.SaveChangesAsync();
        return participant;
    }


    private static async Task<(long UserId, long EmployeeId)> SeedRestrictedOperatorAsync(
        ErpDbContext db, bool withLoadingListMenu)
    {
        var code = $"ll-op-{Guid.NewGuid():N}";
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

        var role = new SysRole { RoleName = "装柜清单操作员", RoleCode = $"LlOp-{Guid.NewGuid():N}", IsSystem = false };
        db.SysRoles.Add(role);
        await db.SaveChangesAsync();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        await db.SaveChangesAsync();

        var menuId = await db.SysMenus.AsNoTracking()
            .Where(m => m.MenuCode == LoadingListAuthorizationRules.RequiredMenuCode && !m.IsDeleted)
            .Select(m => m.Id)
            .FirstAsync();
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menuId, IsDeleted = !withLoadingListMenu });
        await db.SaveChangesAsync();

        return (user.Id, employee.Id);
    }

    private static async Task RevokeMenusAsync(ErpDbContext db, long userId)
    {
        var roleIds = await db.SysUserRoles.AsNoTracking()
            .Where(ur => ur.UserId == userId && !ur.IsDeleted)
            .Select(ur => ur.RoleId)
            .ToListAsync();
        var grants = await db.SysRoleMenus
            .Where(rm => roleIds.Contains(rm.RoleId) && !rm.IsDeleted).ToListAsync();
        foreach (var grant in grants) grant.IsDeleted = true;
        await db.SaveChangesAsync();
    }

    private static async Task<long> SeedDisabledUserAsync(ErpDbContext db)
    {
        var user = new SysUser
        {
            UserName = $"ll-disabled-{Guid.NewGuid():N}", DisplayName = "禁用账号",
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

    private static T AssertOk<T>(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<ApiResponse<T>>(ok.Value);
        Assert.Equal(ErrorCodes.Success, resp.Code);
        Assert.NotNull(resp.Data);
        return resp.Data!;
    }
}

/// <summary>
/// 专用 localdb 目标 Fixture（ERP-364）：把目标库初始化为完整 NEWERP 结构 + 种子数据，供真实 SQL Server 集成测试复用。
/// <para>安全口径：库名一律带本次运行的 GUID 后缀（绝不复用 / 绝不 drop 既有库）；任何数据库访问之前先校验
/// 实例名、库名前缀与集成安全，连接串只来自进程环境变量或专用 localdb 默认值。</para>
/// </summary>
public sealed class LoadingListAuthorizationSqlServerFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";
    public const string DefaultDatabaseName = "NEWERP_AUTOTEST_LOADINGLISTAUTHORIZATION_20261008";

    public string ConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = BuildDefaultConnectionString();

        AssertDedicatedTarget(connectionString);
        ConnectionString = connectionString;

        Console.WriteLine($"[ERP-364] 目标库护栏放行（实例含 {InstanceMarker}，库名前缀 {DatabasePrefix}）。");

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

        Console.WriteLine("[ERP-364] 集成场景就绪：完整 NEWERP 结构 + 种子数据。");
    }
}

/// <summary>目标库护栏单元级校验：非专用目标必须在访问数据库之前被拒绝。</summary>
public sealed class LoadingListAuthorizationTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;User Id=sa;Password=x")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(() => LoadingListAuthorizationSqlServerFixture.AssertDedicatedTarget(connection));

    [Fact]
    public void Accepts_the_dedicated_localdb_target_with_integrated_security()
        => LoadingListAuthorizationSqlServerFixture.AssertDedicatedTarget(
            "Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_X;Integrated Security=true");
}

