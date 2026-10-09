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
/// ERP-451 客户资料（<c>api/base/customers</c> 分页 / 全部 / 按主键读取 / 指定货代下拉 / 新增 / 修改 / 删除 /
/// 批量删除）实时授权与有界字段校验的真实 SQL Server 集成测试（GUID 独占 <c>NEWERP_AUTOTEST</c> 目标）。
/// <list type="number">
/// <item><b>真实控制器 + 真实既有授权</b>：以既有「客户资料」（<c>customer</c>）菜单与既有
/// 「角色 → 菜单」口径驱动真实 <see cref="CustomerController"/>（注入真实 HTTP 身份）。</item>
/// <item><b>拒绝矩阵</b>：缺失 / 禁用 / 已删除 / 无菜单的身份在<b>全部路由</b> fail closed，
/// 且 <c>BaseCustomers</c> 行逐字节不变（拒绝既不读取也不改写任何行）。</item>
/// <item><b>授权身份</b>：具备既有客户菜单时既有读 / 写契约放行；请求之间撤销菜单立即收敛为拒绝。</item>
/// <item><b>有界字段校验</b>：编码 / 名称空值、文本长度越界、数值超 <c>DECIMAL(18,4)</c>、
/// 负数业务员 Id / 账期天数、状态越界在授权后仍 fail closed 且零写入（不落任何新行、不改写任何既有行）。</item>
/// </list>
/// <para>安全口径：目标必须为专用 localdb 实例 <c>NEWERP_AutoAcceptance</c>、库名前缀 <c>NEWERP_AUTOTEST</c>
/// 且集成安全；每次运行只创建全新 GUID 后缀库，发现同名库已存在立即拒绝；绝不 drop / reset / 复用任何数据库，
/// 连接串只来自进程环境变量或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据。</para>
/// </summary>
public sealed class CustomerMasterAuthorizationSqlServerTests
    : IClassFixture<CustomerMasterAuthorizationSqlServerFixture>
{
    private readonly CustomerMasterAuthorizationSqlServerFixture _fixture;

    public CustomerMasterAuthorizationSqlServerTests(CustomerMasterAuthorizationSqlServerFixture fixture)
        => _fixture = fixture;

    /// <summary>访问数据库前复核专用目标护栏（实例名 / 库名前缀 / 集成安全）。</summary>
    private void Guard()
    {
        var target = new SqlConnectionStringBuilder(_fixture.ConnectionString);
        Assert.Equal("(localdb)\\NEWERP_AutoAcceptance", target.DataSource, ignoreCase: true);
        Assert.StartsWith(CustomerMasterAuthorizationSqlServerFixture.DatabasePrefix,
            target.InitialCatalog, StringComparison.OrdinalIgnoreCase);
        Assert.True(target.IntegratedSecurity);
    }

    private static CustomerController Controller(ErpDbContext db, long? userId)
        => new(new GenericService<BaseCustomer>(db), db) { ControllerContext = ContextFor(userId) };

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
        http.Request.Path = "/api/base/customers";
        return new ControllerContext { HttpContext = http };
    }

    private static BaseCustomer NewCustomer(
        string code, string name, int status = 1, string remark = "",
        decimal creditLimit = 0m, decimal depositRatio = 0m, decimal commissionRatio = 0m,
        long? empId = null, int? creditDays = null)
        => new()
        {
            CustomerCode = code,
            CustomerName = name,
            Status = status,
            CreditStatus = "正常",
            Remark = remark,
            CreditLimit = creditLimit,
            DepositRatio = depositRatio,
            CommissionRatio = commissionRatio,
            EmpId = empId,
            CreditDays = creditDays
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
    public async Task Live_denies_every_route_without_reading_or_mutating_any_customer_row(string scenario)
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var customer = await SeedCustomerAsync(db);

        long? userId = scenario switch
        {
            "missing" => null,
            "disabled" => await SeedUserAsync(db, UserStatus.Disabled, deleted: false, customerMenu: true),
            "deleted" => await SeedUserAsync(db, UserStatus.Enabled, deleted: true, customerMenu: true),
            _ => await SeedUserAsync(db, UserStatus.Enabled, deleted: false, customerMenu: false)
        };
        var expectedCode = scenario is "missing" or "deleted"
            ? ErrorCodes.Unauthorized
            : ErrorCodes.Forbidden;

        var ctl = Controller(db, userId);
        var before = await SnapshotAsync(db);

        await AssertCodeAsync(expectedCode, () => ctl.GetPaged(new PageQuery { Page = 1, PageSize = 10 }));
        await AssertCodeAsync(expectedCode, () => ctl.GetAll());
        await AssertCodeAsync(expectedCode, () => ctl.GetById(customer.Id));
        await AssertCodeAsync(expectedCode, () => ctl.GetForwarderOptions());
        await AssertCodeAsync(expectedCode, () => ctl.Create(NewCustomer($"C-DENY-{Tag()}", "被拒客户")));
        await AssertCodeAsync(expectedCode, () => ctl.Update(customer.Id, NewCustomer(customer.CustomerCode, "被拒改名")));
        await AssertCodeAsync(expectedCode, () => ctl.Delete(customer.Id));
        await AssertCodeAsync(expectedCode, () => ctl.BatchDelete(new List<long> { customer.Id }));

        Assert.Equal(before, await SnapshotAsync(db));
        var stored = await db.BaseCustomers.AsNoTracking().SingleAsync(x => x.Id == customer.Id);
        Assert.Equal(customer.CustomerCode, stored.CustomerCode);
        Assert.False(stored.IsDeleted);
    }

    // ==================== 2. 授权身份：既有读 / 写契约放行 ====================

    [Fact]
    public async Task Authorized_identity_allows_existing_contracts()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var userId = await SeedUserAsync(db, UserStatus.Enabled, deleted: false, customerMenu: true, privileged: true);
        var ctl = Controller(db, userId);

        var created = Data<BaseCustomer>(await ctl.Create(NewCustomer($"C-OK-{Tag()}", "授权客户")));
        Assert.True(created.Id > 0);
        var page = Data<PagedResult<BaseCustomer>>(
            await ctl.GetPaged(new PageQuery { Page = 1, PageSize = 100 }));
        Assert.Contains(page.Items, c => c.Id == created.Id && c.CustomerCode == created.CustomerCode);
        Assert.Contains(Data<List<BaseCustomer>>(await ctl.GetAll()), c => c.Id == created.Id);
        Assert.Equal("授权客户", Data<BaseCustomer>(await ctl.GetById(created.Id)).CustomerName);

        // 指定货代下拉放行（至少返回既有启用货代；空集合也可，取决于种子数据）
        Data<List<ERP.Application.DTOs.OtherInfoOptionDto>>(await ctl.GetForwarderOptions());

        var updated = Data<BaseCustomer>(await ctl.Update(created.Id,
            NewCustomer(created.CustomerCode, "授权客户改名")));
        Assert.Equal("授权客户改名", updated.CustomerName);

        var second = Data<BaseCustomer>(await ctl.Create(NewCustomer($"C-OK2-{Tag()}", "授权客户2")));
        await ctl.BatchDelete(new List<long> { second.Id });
        Assert.True((await db.BaseCustomers.AsNoTracking().SingleAsync(x => x.Id == second.Id)).IsDeleted);

        await ctl.Delete(created.Id);
        Assert.True((await db.BaseCustomers.AsNoTracking().SingleAsync(x => x.Id == created.Id)).IsDeleted);
    }

    // ==================== 3. 请求之间撤销菜单：立即收敛 ====================

    [Fact]
    public async Task Revoking_customer_menu_between_requests_immediately_denies()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var customer = await SeedCustomerAsync(db);
        var userId = await SeedUserAsync(db, UserStatus.Enabled, deleted: false, customerMenu: true, privileged: true);
        var ctl = Controller(db, userId);

        Data<PagedResult<BaseCustomer>>(await ctl.GetPaged(new PageQuery { Page = 1, PageSize = 10 }));

        await RevokeCustomerMenuForUserAsync(db, userId);

        await AssertCodeAsync(ErrorCodes.Forbidden, () => ctl.GetPaged(new PageQuery { Page = 1, PageSize = 10 }));
        await AssertCodeAsync(ErrorCodes.Forbidden, () => ctl.GetForwarderOptions());
        await AssertCodeAsync(ErrorCodes.Forbidden, () => ctl.Delete(customer.Id));
        await AssertCodeAsync(ErrorCodes.Forbidden, () => ctl.Create(NewCustomer($"C-REV-{Tag()}", "撤销后客户")));
        Assert.False((await db.BaseCustomers.AsNoTracking().SingleAsync(x => x.Id == customer.Id)).IsDeleted);
    }


    // ==================== 4. 授权后非法载荷：零写入 ====================

    [Theory]
    [InlineData("empty-code")]
    [InlineData("blank-name")]
    [InlineData("code-too-long")]
    [InlineData("name-too-long")]
    [InlineData("text-too-long")]
    [InlineData("credit-overflow")]
    [InlineData("deposit-overflow")]
    [InlineData("commission-overflow")]
    [InlineData("negative-emp")]
    [InlineData("negative-creditdays")]
    [InlineData("status-unknown")]
    public async Task Invalid_payloads_after_authorization_persist_nothing(string scenario)
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var existing = await SeedCustomerAsync(db);
        var userId = await SeedUserAsync(db, UserStatus.Enabled, deleted: false, customerMenu: true);
        var ctl = Controller(db, userId);
        var before = await SnapshotAsync(db);

        BaseCustomer Payload() => scenario switch
        {
            "empty-code" => NewCustomer("", "名称"),
            "blank-name" => NewCustomer($"C-{Tag()}", "   "),
            "code-too-long" => NewCustomer(new string('C', CustomerAuthorizationRules.MaxCustomerCodeLength + 1), "超长编码"),
            "name-too-long" => NewCustomer($"C-{Tag()}", new string('N', CustomerAuthorizationRules.MaxCustomerNameLength + 1)),
            "text-too-long" => NewCustomer($"C-{Tag()}", "名称",
                remark: new string('R', CustomerAuthorizationRules.MaxRemarkLength + 1)),
            "credit-overflow" => NewCustomer($"C-{Tag()}", "名称", creditLimit: 100000000000000m),
            "deposit-overflow" => NewCustomer($"C-{Tag()}", "名称", depositRatio: -100000000000000m),
            "commission-overflow" => NewCustomer($"C-{Tag()}", "名称", commissionRatio: 100000000000000m),
            "negative-emp" => NewCustomer($"C-{Tag()}", "名称", empId: -1),
            "negative-creditdays" => NewCustomer($"C-{Tag()}", "名称", creditDays: -1),
            _ => NewCustomer($"C-{Tag()}", "名称", status: 9)
        };

        await AssertCodeAsync(ErrorCodes.InvalidParameter, () => ctl.Create(Payload()));
        await AssertCodeAsync(ErrorCodes.InvalidParameter, () => ctl.Update(existing.Id, Payload()));

        Assert.Equal(before, await SnapshotAsync(db));
        var stored = await db.BaseCustomers.AsNoTracking().SingleAsync(x => x.Id == existing.Id);
        Assert.Equal(existing.CustomerCode, stored.CustomerCode);
        Assert.Equal("客户授权集成测试客户", stored.CustomerName);
    }

    // ==================== 5. 自包含播种（真实既有授权模型） ====================

    private static string Tag() => Guid.NewGuid().ToString("N")[..8];

    private static async Task<string> SnapshotAsync(ErpDbContext db) => string.Join("|",
        await db.BaseCustomers.AsNoTracking().OrderBy(x => x.Id)
            .Select(x => $"{x.Id}:{x.Status}:{x.IsDeleted}:{x.CustomerCode}:{x.CustomerName}:{x.Remark}")
            .ToListAsync());

    private static async Task<BaseCustomer> SeedCustomerAsync(ErpDbContext db, int status = 1)
    {
        var customer = new BaseCustomer
        {
            CustomerCode = $"CMA-C-{Tag()}",
            CustomerName = "客户授权集成测试客户",
            Status = status,
            CreditStatus = "正常"
        };
        db.BaseCustomers.Add(customer);
        await db.SaveChangesAsync();
        return customer;
    }

    private static async Task<long> SeedUserAsync(
        ErpDbContext db, UserStatus status, bool deleted, bool customerMenu, bool privileged = false)
    {
        var user = new SysUser
        {
            UserName = $"cma-{Tag()}",
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = "客户授权集成测试账号",
            Status = status,
            IsDeleted = deleted
        };
        db.SysUsers.Add(user);
        await db.SaveChangesAsync();

        var role = new SysRole
        {
            RoleName = "客户授权集成测试角色",
            RoleCode = $"CMA-{Tag()}",
            IsSystem = privileged
        };
        db.SysRoles.Add(role);
        await db.SaveChangesAsync();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        await db.SaveChangesAsync();

        if (customerMenu)
            await GrantMenuAsync(db, role.Id, CustomerAuthorizationRules.RequiredMenuCode);
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

    /// <summary>回收指定账号的既有「客户资料」菜单授权（模拟请求之间撤销权限，不影响其它账号 / 种子管理员）。</summary>
    private static async Task RevokeCustomerMenuForUserAsync(ErpDbContext db, long userId)
    {
        var roleIds = await db.SysUserRoles.AsNoTracking()
            .Where(ur => ur.UserId == userId && !ur.IsDeleted)
            .Select(ur => ur.RoleId)
            .ToListAsync();
        var menuIds = await db.SysMenus.AsNoTracking()
            .Where(m => m.MenuCode == CustomerAuthorizationRules.RequiredMenuCode)
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
/// ERP-451 专用 localdb 目标 Fixture：只创建一个全新 GUID 后缀库并初始化完整 NEWERP 结构 + 种子数据，
/// 供客户资料实时授权集成测试复用。
/// <para>安全口径：实例必须精确为 <c>(localdb)\NEWERP_AutoAcceptance</c>，库名前缀必须为 <c>NEWERP_AUTOTEST</c>
/// 且使用集成安全；发现同名库已存在立即拒绝；绝不 drop / reset / 复用任何数据库，绝不读取生产设置。</para>
/// </summary>
public sealed class CustomerMasterAuthorizationSqlServerFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";

    /// <summary>本次运行新建的 GUID 独占库名（每次运行唯一，绝不复用既有库）。</summary>
    public static string DefaultDatabaseName { get; } =
        $"{DatabasePrefix}_CUSTOMERMASTERAUTH_{Guid.NewGuid():N}";

    public string ConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = BuildDefaultConnectionString();

        // 访问数据库之前先复核目标护栏（错误目标 fail closed）。
        AssertDedicatedTarget(connectionString);
        ConnectionString = connectionString;

        Console.WriteLine($"[ERP-451] 目标库护栏放行（实例 {InstanceMarker}，库名前缀 {DatabasePrefix}，集成安全）。");

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

        Console.WriteLine("[ERP-451] 集成场景就绪：完整 NEWERP 结构 + 种子数据（含既有 customer 菜单）。");
    }
}

/// <summary>目标库护栏单元级校验：非专用目标必须在访问数据库之前被拒绝。</summary>
public sealed class CustomerMasterAuthorizationTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;User Id=sa;Password=secret")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(
            () => CustomerMasterAuthorizationSqlServerFixture.AssertDedicatedTarget(connection));
}

