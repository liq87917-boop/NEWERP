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
/// ERP-449 员工资料（<c>api/base/employees</c> 分页 / 全部 / 按主键读取 / 新增 / 修改 / 删除 / 批量删除，
/// 以及业务员下拉 <c>salesmen</c>）实时授权与有界字段校验的真实 SQL Server 集成测试
/// （GUID 独占 <c>NEWERP_AUTOTEST</c> 目标）。
/// <list type="number">
/// <item><b>真实控制器 + 真实既有授权</b>：以既有「员工资料」（<c>employee</c>）菜单与既有
/// 「角色 → 菜单」口径驱动真实 <see cref="EmployeeController"/>（注入真实 HTTP 身份）。</item>
/// <item><b>拒绝矩阵</b>：缺失 / 禁用 / 已删除 / 无菜单的身份在<b>全部路由</b> fail closed，
/// 且 <c>BaseEmployees</c> 行逐字节不变（拒绝既不读取也不改写任何行）。</item>
/// <item><b>授权身份</b>：具备既有员工菜单时既有读 / 写契约放行，业务员下拉仍只返回在职业务员；
/// 请求之间撤销菜单立即收敛为拒绝。</item>
/// <item><b>有界字段校验</b>：编码 / 姓名空值、文本长度越界、入职日期越界、状态越界在授权后仍 fail closed
/// 且零写入（不落任何新行、不改写任何既有行）。</item>
/// </list>
/// <para>安全口径：目标必须为专用 localdb 实例 <c>NEWERP_AutoAcceptance</c>、库名前缀 <c>NEWERP_AUTOTEST</c>
/// 且集成安全；每次运行只创建全新 GUID 后缀库，发现同名库已存在立即拒绝；绝不 drop / reset / 复用任何数据库，
/// 连接串只来自进程环境变量或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据。</para>
/// </summary>
public sealed class EmployeeMasterAuthorizationSqlServerTests
    : IClassFixture<EmployeeMasterAuthorizationSqlServerFixture>
{
    private readonly EmployeeMasterAuthorizationSqlServerFixture _fixture;

    public EmployeeMasterAuthorizationSqlServerTests(EmployeeMasterAuthorizationSqlServerFixture fixture)
        => _fixture = fixture;

    /// <summary>访问数据库前复核专用目标护栏（实例名 / 库名前缀 / 集成安全）。</summary>
    private void Guard()
    {
        var target = new SqlConnectionStringBuilder(_fixture.ConnectionString);
        Assert.Equal("(localdb)\\NEWERP_AutoAcceptance", target.DataSource, ignoreCase: true);
        Assert.StartsWith(EmployeeMasterAuthorizationSqlServerFixture.DatabasePrefix,
            target.InitialCatalog, StringComparison.OrdinalIgnoreCase);
        Assert.True(target.IntegratedSecurity);
    }

    private static EmployeeController Controller(ErpDbContext db, long? userId)
        => new(new GenericService<BaseEmployee>(db), db) { ControllerContext = ContextFor(userId) };

    /// <summary>
    /// 注入真实 HTTP 身份（可空 = 无 <c>NameIdentifier</c>）；<c>Request.Path</c> 已赋值以标记真实请求，
    /// 因此缺失身份也一律实时授权并 fail closed。
    /// </summary>
    private static ControllerContext ContextFor(long? userId)
    {
        var claims = userId.HasValue
            ? new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }
            : Array.Empty<Claim>();
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"))
        };
        http.Request.Path = "/api/base/employees";
        return new ControllerContext { HttpContext = http };
    }

    private static BaseEmployee NewEmployee(
        string code, string name, int status = 1, bool isSalesman = false,
        string department = "", string position = "", string phone = "", string email = "",
        DateTime? hireDate = null)
        => new()
        {
            EmployeeCode = code,
            EmployeeName = name,
            Status = status,
            IsSalesman = isSalesman,
            Department = department,
            Position = position,
            Phone = phone,
            Email = email,
            HireDate = hireDate
        };

    private static async Task AssertCodeAsync(int expected, Func<Task<IActionResult>> action)
    {
        var ex = await Assert.ThrowsAsync<BusinessException>(action);
        Assert.Equal(expected, ex.Code);
    }

    private static T Data<T>(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<ApiResponse<T>>(ok.Value);
        return response.Data!;
    }

    // ==================== 1. 身份 / 账号状态 / 菜单 fail closed ====================

    [Theory]
    [InlineData("missing")]
    [InlineData("disabled")]
    [InlineData("deleted")]
    [InlineData("no-menu")]
    public async Task Live_denies_every_route_without_reading_or_mutating_any_employee_row(string scenario)
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var employee = await SeedEmployeeAsync(db, isSalesman: true);

        long? userId = scenario switch
        {
            "missing" => null,
            "disabled" => await SeedUserAsync(db, UserStatus.Disabled, deleted: false, employeeMenu: true),
            "deleted" => await SeedUserAsync(db, UserStatus.Enabled, deleted: true, employeeMenu: true),
            _ => await SeedUserAsync(db, UserStatus.Enabled, deleted: false, employeeMenu: false)
        };
        var expectedCode = scenario is "missing" or "deleted"
            ? ErrorCodes.Unauthorized
            : ErrorCodes.Forbidden;

        var ctl = Controller(db, userId);
        var before = await SnapshotAsync(db);

        await AssertCodeAsync(expectedCode, () => ctl.GetPaged(new PageQuery { Page = 1, PageSize = 10 }));
        await AssertCodeAsync(expectedCode, () => ctl.GetAll());
        await AssertCodeAsync(expectedCode, () => ctl.GetById(employee.Id));
        await AssertCodeAsync(expectedCode, () => ctl.Create(NewEmployee($"E-DENY-{Tag()}", "被拒员工")));
        await AssertCodeAsync(expectedCode, () => ctl.Update(employee.Id, NewEmployee(employee.EmployeeCode, "被拒改名")));
        await AssertCodeAsync(expectedCode, () => ctl.Delete(employee.Id));
        await AssertCodeAsync(expectedCode, () => ctl.BatchDelete(new List<long> { employee.Id }));
        await AssertCodeAsync(expectedCode, () => ctl.GetSalesmen());

        Assert.Equal(before, await SnapshotAsync(db));
        var stored = await db.BaseEmployees.AsNoTracking().SingleAsync(x => x.Id == employee.Id);
        Assert.Equal(employee.EmployeeCode, stored.EmployeeCode);
        Assert.False(stored.IsDeleted);
    }

    // ==================== 2. 授权身份：既有读 / 写契约放行 ====================

    [Fact]
    public async Task Authorized_identity_allows_existing_contracts()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var userId = await SeedUserAsync(db, UserStatus.Enabled, deleted: false, employeeMenu: true);
        var ctl = Controller(db, userId);

        var created = Data<BaseEmployee>(await ctl.Create(NewEmployee($"E-OK-{Tag()}", "授权员工", isSalesman: true)));
        Assert.True(created.Id > 0);
        var page = Data<PagedResult<BaseEmployee>>(
            await ctl.GetPaged(new PageQuery { Page = 1, PageSize = 100 }));
        Assert.Contains(page.Items, e => e.Id == created.Id && e.EmployeeCode == created.EmployeeCode);
        Assert.Contains(Data<List<BaseEmployee>>(await ctl.GetAll()), e => e.Id == created.Id);
        Assert.Equal("授权员工", Data<BaseEmployee>(await ctl.GetById(created.Id)).EmployeeName);

        var updated = Data<BaseEmployee>(await ctl.Update(created.Id,
            NewEmployee(created.EmployeeCode, "授权员工改名", isSalesman: true)));
        Assert.Equal("授权员工改名", updated.EmployeeName);

        var second = Data<BaseEmployee>(await ctl.Create(NewEmployee($"E-OK2-{Tag()}", "授权员工2")));
        await ctl.BatchDelete(new List<long> { second.Id });
        Assert.True((await db.BaseEmployees.AsNoTracking().SingleAsync(x => x.Id == second.Id)).IsDeleted);

        await ctl.Delete(created.Id);
        Assert.True((await db.BaseEmployees.AsNoTracking().SingleAsync(x => x.Id == created.Id)).IsDeleted);
    }

    /// <summary>业务员下拉仍只返回在职（Status=1）、未删除且 IsSalesman 的员工。</summary>
    [Fact]
    public async Task Authorized_identity_lists_only_enabled_salesmen()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var onDuty = await SeedEmployeeAsync(db, status: 1, isSalesman: true);
        var offDuty = await SeedEmployeeAsync(db, status: 0, isSalesman: true);
        var nonSalesman = await SeedEmployeeAsync(db, status: 1, isSalesman: false);
        var userId = await SeedUserAsync(db, UserStatus.Enabled, deleted: false, employeeMenu: true);

        var items = Data<List<BaseEmployee>>(await Controller(db, userId).GetSalesmen());

        Assert.Contains(items, e => e.Id == onDuty.Id);
        Assert.DoesNotContain(items, e => e.Id == offDuty.Id);
        Assert.DoesNotContain(items, e => e.Id == nonSalesman.Id);
    }

    /// <summary>
    /// 特权（种子管理员，既有种子已授予全部菜单含 <c>employee</c>）：既有只读契约同样放行
    /// —— 授权口径不因特权而跳过菜单检查，也不新增任何用户授权。
    /// </summary>
    [Fact]
    public async Task Privileged_seed_admin_with_employee_menu_is_permitted()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var adminId = await db.SysUsers.AsNoTracking()
            .Where(u => u.UserName == SeedData.AdminUserName && !u.IsDeleted)
            .Select(u => u.Id).FirstAsync();

        var page = Data<PagedResult<BaseEmployee>>(
            await Controller(db, adminId).GetPaged(new PageQuery { Page = 1, PageSize = 10 }));
        Assert.NotNull(page.Items);
    }

    // ==================== 3. 请求之间撤销授权立即收敛 ====================

    [Fact]
    public async Task Revoked_menu_between_requests_converges_to_denial()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var employee = await SeedEmployeeAsync(db, isSalesman: true);
        var userId = await SeedUserAsync(db, UserStatus.Enabled, deleted: false, employeeMenu: true);

        Data<PagedResult<BaseEmployee>>(await Controller(db, userId)
            .GetPaged(new PageQuery { Page = 1, PageSize = 10 }));  // 授权读取成功

        await RevokeEmployeeMenuForUserAsync(db, userId);            // 撤销该账号的既有「角色 → 菜单」授权

        await AssertCodeAsync(ErrorCodes.Forbidden, () =>
            Controller(db, userId).GetPaged(new PageQuery { Page = 1, PageSize = 10 }));
        await AssertCodeAsync(ErrorCodes.Forbidden, () => Controller(db, userId).Delete(employee.Id));
        await AssertCodeAsync(ErrorCodes.Forbidden, () => Controller(db, userId).GetSalesmen());
        Assert.False((await db.BaseEmployees.AsNoTracking().SingleAsync(x => x.Id == employee.Id)).IsDeleted);
    }

    // ==================== 4. 授权后有界字段校验仍 fail closed 且零写入 ====================

    [Fact]
    public async Task Authorized_identity_with_invalid_payloads_fails_closed_without_writes()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var existing = await SeedEmployeeAsync(db);
        var userId = await SeedUserAsync(db, UserStatus.Enabled, deleted: false, employeeMenu: true);
        var ctl = Controller(db, userId);
        var before = await SnapshotAsync(db);

        await AssertCodeAsync(ErrorCodes.InvalidParameter, () => ctl.Create(NewEmployee("", "姓名")));
        await AssertCodeAsync(ErrorCodes.InvalidParameter, () => ctl.Create(NewEmployee($"E-{Tag()}", "   ")));
        await AssertCodeAsync(ErrorCodes.InvalidParameter, () => ctl.Create(NewEmployee(
            new string('C', EmployeeAuthorizationRules.MaxEmployeeCodeLength + 1), "超长编码")));
        await AssertCodeAsync(ErrorCodes.InvalidParameter, () => ctl.Create(NewEmployee(
            $"E-{Tag()}", new string('N', EmployeeAuthorizationRules.MaxEmployeeNameLength + 1))));
        await AssertCodeAsync(ErrorCodes.InvalidParameter, () => ctl.Create(NewEmployee(
            $"E-{Tag()}", "姓名", department: new string('D', EmployeeAuthorizationRules.MaxDepartmentLength + 1))));
        await AssertCodeAsync(ErrorCodes.InvalidParameter, () => ctl.Create(NewEmployee(
            $"E-{Tag()}", "姓名", hireDate: default(DateTime))));
        await AssertCodeAsync(ErrorCodes.InvalidParameter, () => ctl.Create(NewEmployee(
            $"E-{Tag()}", "姓名", status: 9)));
        await AssertCodeAsync(ErrorCodes.InvalidParameter, () => ctl.Update(existing.Id,
            NewEmployee(existing.EmployeeCode, "非法改名", status: 2)));

        Assert.Equal(before, await SnapshotAsync(db));
    }

    // ==================== 5. 自包含播种（真实既有授权模型） ====================

    private static string Tag() => Guid.NewGuid().ToString("N")[..8];

    private static async Task<string> SnapshotAsync(ErpDbContext db) => string.Join("|",
        await db.BaseEmployees.AsNoTracking().OrderBy(x => x.Id)
            .Select(x => $"{x.Id}:{x.Status}:{x.IsDeleted}:{x.EmployeeCode}:{x.EmployeeName}:{x.Department}:{x.IsSalesman}")
            .ToListAsync());

    private static async Task<BaseEmployee> SeedEmployeeAsync(
        ErpDbContext db, int status = 1, bool isSalesman = false)
    {
        var employee = new BaseEmployee
        {
            EmployeeCode = $"EMA-E-{Tag()}",
            EmployeeName = "员工授权集成测试员工",
            Status = status,
            IsSalesman = isSalesman
        };
        db.BaseEmployees.Add(employee);
        await db.SaveChangesAsync();
        return employee;
    }

    private static async Task<long> SeedUserAsync(
        ErpDbContext db, UserStatus status, bool deleted, bool employeeMenu)
    {
        var user = new SysUser
        {
            UserName = $"ema-{Tag()}",
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = "员工授权集成测试账号",
            Status = status,
            IsDeleted = deleted
        };
        db.SysUsers.Add(user);
        await db.SaveChangesAsync();

        var role = new SysRole
        {
            RoleName = "员工授权集成测试角色",
            RoleCode = $"EMA-{Tag()}",
            IsSystem = false
        };
        db.SysRoles.Add(role);
        await db.SaveChangesAsync();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        await db.SaveChangesAsync();

        if (employeeMenu)
            await GrantMenuAsync(db, role.Id, EmployeeAuthorizationRules.RequiredMenuCode);
        return user.Id;
    }

    /// <summary>授予既有功能菜单（菜单由种子数据提供；缺失时按既有种子口径补建）。</summary>
    private static async Task GrantMenuAsync(ErpDbContext db, long roleId, string menuCode)
    {
        var menu = await db.SysMenus.FirstOrDefaultAsync(m => m.MenuCode == menuCode && !m.IsDeleted);
        if (menu is null)
        {
            menu = new SysMenu { MenuCode = menuCode, MenuName = menuCode, MenuType = MenuType.Menu };
            db.SysMenus.Add(menu);
            await db.SaveChangesAsync();
        }
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = roleId, MenuId = menu.Id });
        await db.SaveChangesAsync();
    }

    /// <summary>回收指定账号的既有「员工资料」菜单授权（模拟请求之间撤销权限，不影响其它账号 / 种子管理员）。</summary>
    private static async Task RevokeEmployeeMenuForUserAsync(ErpDbContext db, long userId)
    {
        var roleIds = await db.SysUserRoles.AsNoTracking()
            .Where(ur => ur.UserId == userId && !ur.IsDeleted)
            .Select(ur => ur.RoleId)
            .ToListAsync();
        var menuIds = await db.SysMenus.AsNoTracking()
            .Where(m => m.MenuCode == EmployeeAuthorizationRules.RequiredMenuCode)
            .Select(m => m.Id)
            .ToListAsync();
        var grants = await db.SysRoleMenus
            .Where(rm => roleIds.Contains(rm.RoleId) && menuIds.Contains(rm.MenuId))
            .ToListAsync();
        foreach (var grant in grants) grant.IsDeleted = true;
        await db.SaveChangesAsync();
    }
}

/// <summary>
/// ERP-449 专用 localdb 目标 Fixture：只创建一个全新 GUID 后缀库并初始化完整 NEWERP 结构 + 种子数据，
/// 供员工资料实时授权集成测试复用。
/// <para>安全口径：实例必须精确为 <c>(localdb)\NEWERP_AutoAcceptance</c>，库名前缀必须为 <c>NEWERP_AUTOTEST</c>
/// 且使用集成安全；发现同名库已存在立即拒绝；绝不 drop / reset / 复用任何数据库，绝不读取生产设置。</para>
/// </summary>
public sealed class EmployeeMasterAuthorizationSqlServerFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";

    /// <summary>本次运行新建的 GUID 独占库名（每次运行唯一，绝不复用既有库）。</summary>
    public static string DefaultDatabaseName { get; } =
        $"{DatabasePrefix}_EMPLOYEEMASTERAUTH_{Guid.NewGuid():N}";

    public string ConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = BuildDefaultConnectionString();

        // 访问数据库之前先复核目标护栏（错误目标 fail closed）。
        AssertDedicatedTarget(connectionString);
        ConnectionString = connectionString;

        Console.WriteLine($"[ERP-449] 目标库护栏放行（实例 {InstanceMarker}，库名前缀 {DatabasePrefix}，集成安全）。");

        await InitialiseFreshDatabaseAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public ErpDbContext CreateDbContext() => new(BuildOptions());

    private DbContextOptions<ErpDbContext> BuildOptions()
        => new DbContextOptionsBuilder<ErpDbContext>().UseSqlServer(ConnectionString).Options;

    private static string BuildDefaultConnectionString()
        => $"Server=(localdb)\\{InstanceMarker};Initial Catalog={DefaultDatabaseName};" +
           "Integrated Security=true;TrustServerCertificate=true;";

    internal static void AssertDedicatedTarget(string connectionString)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        var server = builder.DataSource ?? string.Empty;
        var database = builder.InitialCatalog ?? string.Empty;

        Assert.Equal($"(localdb)\\{InstanceMarker}", server, ignoreCase: true);
        Assert.StartsWith(DatabasePrefix, database, StringComparison.OrdinalIgnoreCase);
        Assert.True(builder.IntegratedSecurity);
    }

    private async Task InitialiseFreshDatabaseAsync()
    {
        var builder = new SqlConnectionStringBuilder(ConnectionString);
        var database = builder.InitialCatalog;

        // 破坏性初始化前再次护栏：绝不使用生产回退。
        AssertDedicatedTarget(ConnectionString);

        var master = new SqlConnectionStringBuilder(ConnectionString) { InitialCatalog = "master" };
        await using (var conn = new SqlConnection(master.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            // 绝不销毁已存在的夹具库或其它调用方的数据库。
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

        Console.WriteLine("[ERP-449] 集成场景就绪：完整 NEWERP 结构 + 种子数据（含既有 employee 菜单）。");
    }
}

/// <summary>目标库护栏单元级校验：非专用目标必须在访问数据库之前被拒绝。</summary>
public sealed class EmployeeMasterAuthorizationTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;User Id=sa;Password=secret")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(
            () => EmployeeMasterAuthorizationSqlServerFixture.AssertDedicatedTarget(connection));
}

