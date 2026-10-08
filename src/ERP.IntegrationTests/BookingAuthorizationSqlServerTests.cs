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
/// ERP-360 订柜信息实时授权 / 客户数据范围 / 主数据护栏的真实 SQL Server 集成测试（GUID 独占 <c>NEWERP_AUTOTEST</c> 目标）。
/// <list type="number">
/// <item>真实控制器：受限制业务员只能读 / 写本人客户的订柜信息，范围外详情 / 时间线 / 修改 / 状态变更 fail closed 且库中保持不变；</item>
/// <item>真实身份：禁用账号按权限不足、撤销既有「订柜信息」菜单后立即收敛为拒绝（不新增任何用户授权）；</item>
/// <item>真实主数据：停用客户阻止新增 / 状态变更但历史读取照常并带显式不可用证据；失败的修改不改变客户 / 状态 / 备注；</item>
/// <item><b>两条独立连接竞争</b>：并发「提交 vs 删除」与并发「双重审核」经同一把订柜行锁（ERP-353 <c>UPDLOCK, HOLDLOCK</c>）
/// 串行化后恰好一方成功。</item>
/// </list>
/// <para>安全口径：目标必须为专用 localdb 实例 <c>NEWERP_AutoAcceptance</c>、库名前缀 <c>NEWERP_AUTOTEST</c> 且集成安全；
/// 每次运行只创建全新 GUID 后缀库，发现同名库已存在立即拒绝；绝不 drop / reset / 复用任何数据库，
/// 连接串只来自进程环境变量或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据。</para>
/// </summary>
public sealed class BookingAuthorizationSqlServerTests
    : IClassFixture<BookingAuthorizationSqlServerFixture>
{
    private readonly BookingAuthorizationSqlServerFixture _fixture;

    public BookingAuthorizationSqlServerTests(BookingAuthorizationSqlServerFixture fixture)
        => _fixture = fixture;

    /// <summary>访问数据库前复核专用目标护栏（实例名 / 库名前缀 / 集成安全）。</summary>
    private void Guard()
    {
        var target = new SqlConnectionStringBuilder(_fixture.ConnectionString);
        Assert.Equal("(localdb)\\NEWERP_AutoAcceptance", target.DataSource, ignoreCase: true);
        Assert.StartsWith(BookingAuthorizationSqlServerFixture.DatabasePrefix, target.InitialCatalog,
            StringComparison.OrdinalIgnoreCase);
        Assert.True(target.IntegratedSecurity);
    }

    // ==================== 1. 真实控制器的本人 / 他人身份 ====================

    [Fact]
    public async Task Live_restricted_operator_reads_only_own_customer_booking()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var (userId, employeeId) = await SeedRestrictedOperatorAsync(db, withBookingMenu: true);
        var ownCustomer = await SeedCustomerAsync(db, "本人客户", employeeId);
        var foreignCustomer = await SeedCustomerAsync(db, "他人客户", empId: null);
        var own = await SeedBookingAsync(db, ownCustomer, DocumentStatus.Pending);
        var foreign = await SeedBookingAsync(db, foreignCustomer, DocumentStatus.Pending);

        var ctl = NewController(db, userId);

        var page = AssertOk<PagedResult<ContainerBooking>>(
            await ctl.GetPaged(new PageQuery { Page = 1, PageSize = 50 }, null));
        var row = Assert.Single(page.Items);
        Assert.Equal(own.Id, row.Id);
        Assert.Equal(1, page.Total);                       // 计数发生在数据库侧范围过滤之后

        Assert.Equal(own.Id, AssertOk<ContainerBooking>(await ctl.GetById(own.Id)).Id);
        await AssertCode(ErrorCodes.Forbidden, () => ctl.GetById(foreign.Id));
        await AssertCode(ErrorCodes.Forbidden, () => ctl.GetShipmentTimeline(foreign.Id));
    }

    [Fact]
    public async Task Live_restricted_operator_cannot_write_foreign_customer_booking()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var (userId, employeeId) = await SeedRestrictedOperatorAsync(db, withBookingMenu: true);
        await SeedCustomerAsync(db, "本人客户", employeeId);
        var foreignCustomer = await SeedCustomerAsync(db, "他人客户", empId: null);
        var pending = await SeedBookingAsync(db, foreignCustomer, DocumentStatus.Pending);
        var submitted = await SeedBookingAsync(db, foreignCustomer, DocumentStatus.Submitted);
        var ctl = NewController(db, userId);

        await AssertCode(ErrorCodes.Forbidden, () => ctl.Update(pending.Id,
            new ContainerBooking { BookingDate = DateTime.Today, CustomerId = foreignCustomer }));
        await AssertCode(ErrorCodes.Forbidden, () => ctl.Submit(pending.Id));
        await AssertCode(ErrorCodes.Forbidden, () => ctl.Approve(submitted.Id));
        await AssertCode(ErrorCodes.Forbidden, () => ctl.Cancel(pending.Id));
        await AssertCode(ErrorCodes.Forbidden, () => ctl.Delete(pending.Id));

        db.ChangeTracker.Clear();
        var storedPending = await db.ContainerBookings.AsNoTracking().SingleAsync(b => b.Id == pending.Id);
        var storedSubmitted = await db.ContainerBookings.AsNoTracking().SingleAsync(b => b.Id == submitted.Id);
        Assert.Equal(DocumentStatus.Pending, storedPending.Status);
        Assert.False(storedPending.IsDeleted);
        Assert.Equal(foreignCustomer, storedPending.CustomerId);
        Assert.Equal(DocumentStatus.Submitted, storedSubmitted.Status);
    }

    // ==================== 2. 禁用身份 / 撤销菜单 ====================

    [Fact]
    public async Task Live_disabled_identity_is_denied_without_any_mutation()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var customerId = await SeedCustomerAsync(db, "禁用身份客户");
        var booking = await SeedBookingAsync(db, customerId, DocumentStatus.Pending);
        var disabledUserId = await SeedDisabledUserAsync(db);

        var ctl = NewController(db, disabledUserId);
        await AssertCode(ErrorCodes.Forbidden, () => ctl.GetPaged(new PageQuery(), null));
        await AssertCode(ErrorCodes.Forbidden, () => ctl.Delete(booking.Id));
        await AssertCode(ErrorCodes.Forbidden, () => ctl.Cancel(booking.Id));

        db.ChangeTracker.Clear();
        var stored = await db.ContainerBookings.AsNoTracking().SingleAsync(b => b.Id == booking.Id);
        Assert.Equal(DocumentStatus.Pending, stored.Status);
        Assert.False(stored.IsDeleted);
    }

    [Fact]
    public async Task Live_revoked_booking_menu_is_denied_and_reads_stop_immediately()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var (userId, employeeId, roleId) = await SeedRestrictedOperatorWithRoleAsync(db);
        var customerId = await SeedCustomerAsync(db, "撤销菜单客户", employeeId);
        await SeedBookingAsync(db, customerId, DocumentStatus.Pending);

        var grants = await db.SysRoleMenus.Where(rm => rm.RoleId == roleId && !rm.IsDeleted).ToListAsync();
        Assert.NotEmpty(grants);
        foreach (var grant in grants) grant.IsDeleted = true;
        await db.SaveChangesAsync();

        await AssertCode(ErrorCodes.Forbidden, () => NewController(db, userId).GetPaged(new PageQuery(), null));
        await AssertCode(ErrorCodes.Forbidden, () => NewController(db, userId).GetCustomsBrokerOptions());
    }

    // ==================== 3. 真实主数据：失败修改不改动 / 历史读取证据 ====================

    [Fact]
    public async Task Live_failed_edit_leaves_booking_unchanged()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var (userId, employeeId) = await SeedRestrictedOperatorAsync(db, withBookingMenu: true);
        var ownCustomer = await SeedCustomerAsync(db, "本人客户", employeeId);
        var foreignCustomer = await SeedCustomerAsync(db, "他人客户", empId: null);
        var booking = await SeedBookingAsync(db, ownCustomer, DocumentStatus.Pending);
        var ctl = NewController(db, userId);

        await AssertCode(ErrorCodes.Forbidden, () => ctl.Update(booking.Id, new ContainerBooking
        {
            BookingDate = DateTime.Today, CustomerId = foreignCustomer, Remark = "试图移出范围"
        }));

        db.ChangeTracker.Clear();
        var stored = await db.ContainerBookings.AsNoTracking().SingleAsync(b => b.Id == booking.Id);
        Assert.Equal(ownCustomer, stored.CustomerId);
        Assert.Equal(DocumentStatus.Pending, stored.Status);
        Assert.NotEqual("试图移出范围", stored.Remark);
    }

    [Fact]
    public async Task Live_disabled_customer_blocks_state_change_but_history_stays_readable()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var adminId = await ResolveSeededAdminIdAsync(db);
        var customerId = await SeedCustomerAsync(db, "停用客户", status: 0);
        var booking = await SeedBookingAsync(db, customerId, DocumentStatus.Pending);
        var ctl = NewController(db, adminId);

        await AssertCode(ErrorCodes.RuleConflict, () => ctl.Submit(booking.Id));

        // 历史读取照常，并给出显式不可用证据
        var response = AssertOkResponse<ContainerBooking>(await ctl.GetById(booking.Id));
        Assert.Equal(booking.Id, response.Data!.Id);
        Assert.Contains(BookingAuthorizationRules.UnavailableEvidencePrefix, response.Message);
        Assert.Contains("已停用", response.Message);

        db.ChangeTracker.Clear();
        Assert.Equal(DocumentStatus.Pending,
            (await db.ContainerBookings.AsNoTracking().SingleAsync(b => b.Id == booking.Id)).Status);
    }

    [Fact]
    public async Task Live_create_requires_enabled_customer_on_real_sql()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var adminId = await ResolveSeededAdminIdAsync(db);
        var enabledCustomer = await SeedCustomerAsync(db, "新增可用客户");
        var disabledCustomer = await SeedCustomerAsync(db, "新增停用客户", status: 0);
        var ctl = NewController(db, adminId);

        await AssertCode(ErrorCodes.RuleConflict, () => ctl.Create(new ContainerBooking
        {
            BookingDate = DateTime.Today, CustomerId = disabledCustomer
        }));

        var created = await db.ContainerBookings.AsNoTracking()
            .CountAsync(b => b.CustomerId == enabledCustomer);
        var result = AssertOkObject(await ctl.Create(new ContainerBooking
        {
            BookingDate = DateTime.Today, CustomerId = enabledCustomer
        }));
        Assert.NotNull(result);
        db.ChangeTracker.Clear();
        Assert.Equal(created + 1, await db.ContainerBookings.AsNoTracking()
            .CountAsync(b => b.CustomerId == enabledCustomer));
    }

    // ==================== 4. 两条独立连接的并发状态变更 ====================

    [Fact]
    public async Task Concurrent_submit_and_delete_allow_exactly_one_winner_on_two_connections()
    {
        Guard();
        long bookingId;
        long adminId;
        await using (var db = _fixture.CreateDbContext())
        {
            adminId = await ResolveSeededAdminIdAsync(db);
            var customerId = await SeedCustomerAsync(db, "并发提交删除客户");
            bookingId = (await SeedBookingAsync(db, customerId, DocumentStatus.Pending)).Id;
        }

        var results = await RaceAsync(
            () => TrySubmitAsync(bookingId, adminId),
            () => TryDeleteAsync(bookingId, adminId));

        Assert.Equal(1, results.Count(r => r.Success));

        await using var verify = _fixture.CreateDbContext();
        var stored = await verify.ContainerBookings.AsNoTracking().SingleAsync(b => b.Id == bookingId);
        Assert.Equal(stored.IsDeleted, stored.Status != DocumentStatus.Submitted);
        if (stored.IsDeleted) Assert.Equal(DocumentStatus.Pending, stored.Status);
    }

    [Fact]
    public async Task Concurrent_double_approve_allows_exactly_one_winner_on_two_connections()
    {
        Guard();
        long bookingId;
        long adminId;
        await using (var db = _fixture.CreateDbContext())
        {
            adminId = await ResolveSeededAdminIdAsync(db);
            var customerId = await SeedCustomerAsync(db, "并发双重审核客户");
            bookingId = (await SeedBookingAsync(db, customerId, DocumentStatus.Submitted)).Id;
        }

        var results = await RaceAsync(
            () => TryApproveAsync(bookingId, adminId),
            () => TryApproveAsync(bookingId, adminId));

        Assert.Equal(1, results.Count(r => r.Success));

        await using var verify = _fixture.CreateDbContext();
        var stored = await verify.ContainerBookings.AsNoTracking().SingleAsync(b => b.Id == bookingId);
        Assert.Equal(DocumentStatus.Approved, stored.Status);
        Assert.False(stored.IsDeleted);
    }

    // ==================== 控制器工厂 / 并发脚手架 ====================

    private static ContainerBookingController NewController(ErpDbContext db, long? userId)
    {
        var ctl = new ContainerBookingController(db, new DocumentNumberService(db));
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

    private async Task<(bool Success, string Error)> TrySubmitAsync(long bookingId, long userId)
        => await TryAsync(async ctl => await ctl.Submit(bookingId), userId);

    private async Task<(bool Success, string Error)> TryApproveAsync(long bookingId, long userId)
        => await TryAsync(async ctl => await ctl.Approve(bookingId), userId);

    private async Task<(bool Success, string Error)> TryDeleteAsync(long bookingId, long userId)
        => await TryAsync(async ctl => await ctl.Delete(bookingId), userId);

    private async Task<(bool Success, string Error)> TryAsync(
        Func<ContainerBookingController, Task<IActionResult>> action, long userId)
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

    private static async Task<long> SeedCustomerAsync(ErpDbContext db, string name,
        long? empId = null, int status = 1)
    {
        var customer = new BaseCustomer
        {
            CustomerCode = $"BA-C-{Guid.NewGuid():N}"[..30],
            CustomerName = name,
            EmpId = empId,
            Status = status
        };
        db.BaseCustomers.Add(customer);
        await db.SaveChangesAsync();
        return customer.Id;
    }

    private static async Task<ContainerBooking> SeedBookingAsync(ErpDbContext db, long customerId,
        DocumentStatus status)
    {
        var booking = new ContainerBooking
        {
            BookingNo = $"BA-B-{Guid.NewGuid():N}"[..30],
            BookingDate = DateTime.Today,
            CustomerId = customerId,
            Status = status,
            Remark = "ERP-360_INT"
        };
        db.ContainerBookings.Add(booking);
        await db.SaveChangesAsync();
        return booking;
    }

    private static async Task<(long UserId, long EmployeeId)> SeedRestrictedOperatorAsync(
        ErpDbContext db, bool withBookingMenu)
    {
        var (userId, employeeId, roleId) = await SeedRestrictedOperatorWithRoleAsync(db);
        if (!withBookingMenu)
        {
            var grants = await db.SysRoleMenus.Where(rm => rm.RoleId == roleId && !rm.IsDeleted).ToListAsync();
            foreach (var grant in grants) grant.IsDeleted = true;
            await db.SaveChangesAsync();
        }
        return (userId, employeeId);
    }

    /// <summary>播种受限制的订柜操作员：业务员映射 + 既有「订柜信息」菜单（复用 SeedData 菜单，不新增权限模型）。</summary>
    private static async Task<(long UserId, long EmployeeId, long RoleId)> SeedRestrictedOperatorWithRoleAsync(
        ErpDbContext db)
    {
        var code = $"ba-op-{Guid.NewGuid():N}";
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

        var role = new SysRole { RoleName = "订柜操作员", RoleCode = $"BaOp-{Guid.NewGuid():N}", IsSystem = false };
        db.SysRoles.Add(role);
        await db.SaveChangesAsync();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        await db.SaveChangesAsync();

        var menuId = await db.SysMenus.AsNoTracking()
            .Where(m => m.MenuCode == BookingAuthorizationRules.RequiredMenuCode && !m.IsDeleted)
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
            UserName = $"ba-disabled-{Guid.NewGuid():N}", DisplayName = "禁用账号",
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

    private static ApiResponse<object> AssertOkObject(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<ApiResponse<object>>(ok.Value);
        Assert.Equal(ErrorCodes.Success, resp.Code);
        return resp;
    }
}

/// <summary>
/// 专用 localdb 目标 Fixture（ERP-360）：把目标库初始化为完整 NEWERP 结构 + 种子数据，供真实 SQL Server 集成测试复用。
/// <para>安全口径：库名一律带本次运行的 GUID 后缀（绝不复用 / 绝不 drop 既有库）；任何数据库访问之前先校验
/// 实例名、库名前缀与集成安全，连接串只来自进程环境变量或专用 localdb 默认值。</para>
/// </summary>
public sealed class BookingAuthorizationSqlServerFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";
    public const string DefaultDatabaseName = "NEWERP_AUTOTEST_BOOKINGAUTHORIZATION_20261008";

    public string ConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = BuildDefaultConnectionString();

        AssertDedicatedTarget(connectionString);
        ConnectionString = connectionString;

        Console.WriteLine($"[ERP-360] 目标库护栏放行（实例含 {InstanceMarker}，库名前缀 {DatabasePrefix}）。");

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

        Console.WriteLine("[ERP-360] 集成场景就绪：完整 NEWERP 结构 + 种子数据。");
    }
}

/// <summary>目标库护栏单元级校验：非专用目标必须在访问数据库之前被拒绝。</summary>
public sealed class BookingAuthorizationTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;User Id=sa;Password=x")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(() => BookingAuthorizationSqlServerFixture.AssertDedicatedTarget(connection));

    [Fact]
    public void Accepts_the_dedicated_localdb_target_with_integrated_security()
        => BookingAuthorizationSqlServerFixture.AssertDedicatedTarget(
            "Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_X;Integrated Security=true");
}

