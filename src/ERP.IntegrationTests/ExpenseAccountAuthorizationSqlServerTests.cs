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
/// ERP-498 费用科目（<c>api/base/expense-accounts</c> 分页 / 全部 / 按主键读取 / 新增 / 修改 / 删除 / 批量删除）
/// 实时授权与有界字段校验的真实 SQL Server 集成测试（GUID 独占 <c>NEWERP_AUTOTEST</c> 目标）。
/// <list type="number">
/// <item><b>真实控制器 + 真实既有授权</b>：以既有「费用科目」（<c>expense-account</c>）菜单与既有
/// 「角色 → 菜单」口径驱动真实 <see cref="ExpenseAccountController"/>（注入真实 HTTP 身份）。</item>
/// <item><b>拒绝矩阵</b>：缺失 / 禁用 / 已删除 / 无菜单的身份在<b>全部路由</b> fail closed，
/// 且 <c>BaseExpenseAccounts</c> 行逐字节不变（拒绝既不读取也不改写任何行）。</item>
/// <item><b>授权身份</b>：具备既有费用科目菜单时既有读 / 写契约放行；请求之间撤销菜单立即收敛为拒绝。</item>
/// <item><b>有界字段 / 唯一性 / 父级校验</b>：编码 / 名称空值、文本长度越界、类型或状态越界、编码重复、
/// 父级不存在在授权后仍 fail closed 且零写入（不落任何新行、不改写任何既有行）。</item>
/// </list>
/// <para>安全口径：目标必须为专用 localdb 实例 <c>NEWERP_AutoAcceptance</c>、库名前缀 <c>NEWERP_AUTOTEST</c>
/// 且集成安全；每次运行只创建全新 GUID 后缀库，发现同名库已存在立即拒绝；绝不 drop / reset / 复用任何数据库，
/// 连接串只来自进程环境变量或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据。</para>
/// </summary>
public sealed class ExpenseAccountAuthorizationSqlServerTests
    : IClassFixture<ExpenseAccountAuthorizationSqlServerFixture>
{
    private readonly ExpenseAccountAuthorizationSqlServerFixture _fixture;

    public ExpenseAccountAuthorizationSqlServerTests(ExpenseAccountAuthorizationSqlServerFixture fixture)
        => _fixture = fixture;

    /// <summary>访问数据库前复核专用目标护栏（实例名 / 库名前缀 / 集成安全）。</summary>
    private void Guard()
    {
        var target = new SqlConnectionStringBuilder(_fixture.ConnectionString);
        Assert.Equal("(localdb)\\NEWERP_AutoAcceptance", target.DataSource, ignoreCase: true);
        Assert.StartsWith(ExpenseAccountAuthorizationSqlServerFixture.DatabasePrefix,
            target.InitialCatalog, StringComparison.OrdinalIgnoreCase);
        Assert.True(target.IntegratedSecurity);
    }

    private static ExpenseAccountController Controller(ErpDbContext db, long? userId)
        => new(new GenericService<BaseExpenseAccount>(db), db) { ControllerContext = ContextFor(userId) };

    /// <summary>注入真实 HTTP 身份（可空 = 无 <c>NameIdentifier</c>）；<c>Request.Path</c> 已赋值以标记真实请求。</summary>
    private static ControllerContext ContextFor(long? userId)
    {
        var claims = userId.HasValue
            ? new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }
            : Array.Empty<Claim>();
        var http = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) };
        http.Request.Path = "/api/base/expense-accounts";
        return new ControllerContext { HttpContext = http };
    }

    private static BaseExpenseAccount NewExpenseAccount(
        string code, string name, int type = 2, int status = 1, string description = "", long? parentId = null)
        => new()
        {
            AccountCode = code,
            AccountName = name,
            AccountType = type,
            Status = status,
            Description = description,
            ParentId = parentId
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
    public async Task Live_denies_every_route_without_reading_or_mutating_any_expense_row(string scenario)
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var account = await SeedAccountAsync(db);

        long? userId = scenario switch
        {
            "missing" => null,
            "disabled" => await SeedUserAsync(db, UserStatus.Disabled, deleted: false, expenseMenu: true),
            "deleted" => await SeedUserAsync(db, UserStatus.Enabled, deleted: true, expenseMenu: true),
            _ => await SeedUserAsync(db, UserStatus.Enabled, deleted: false, expenseMenu: false)
        };
        var expectedCode = scenario is "missing" or "deleted"
            ? ErrorCodes.Unauthorized
            : ErrorCodes.Forbidden;

        var ctl = Controller(db, userId);
        var before = await SnapshotAsync(db);

        await AssertCodeAsync(expectedCode, () => ctl.GetPaged(new PageQuery { Page = 1, PageSize = 10 }));
        await AssertCodeAsync(expectedCode, () => ctl.GetAll());
        await AssertCodeAsync(expectedCode, () => ctl.GetById(account.Id));
        await AssertCodeAsync(expectedCode, () => ctl.Create(NewExpenseAccount($"EA-DENY-{Tag()}", "被拒科目")));
        await AssertCodeAsync(expectedCode,
            () => ctl.Update(account.Id, NewExpenseAccount(account.AccountCode, "被拒改名")));
        await AssertCodeAsync(expectedCode, () => ctl.Delete(account.Id));
        await AssertCodeAsync(expectedCode, () => ctl.BatchDelete(new List<long> { account.Id }));

        Assert.Equal(before, await SnapshotAsync(db));
        var stored = await db.BaseExpenseAccounts.AsNoTracking().SingleAsync(x => x.Id == account.Id);
        Assert.Equal(account.AccountCode, stored.AccountCode);
        Assert.False(stored.IsDeleted);
    }

    // ==================== 2. 授权身份：既有读 / 写契约放行 ====================

    [Fact]
    public async Task Authorized_identity_allows_existing_contracts()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var userId = await SeedUserAsync(db, UserStatus.Enabled, deleted: false, expenseMenu: true);
        var ctl = Controller(db, userId);

        var created = Data<BaseExpenseAccount>(await ctl.Create(NewExpenseAccount($"EA-OK-{Tag()}", "授权科目")));
        Assert.True(created.Id > 0);
        var page = Data<PagedResult<BaseExpenseAccount>>(
            await ctl.GetPaged(new PageQuery { Page = 1, PageSize = 100 }));
        Assert.Contains(page.Items, x => x.Id == created.Id && x.AccountCode == created.AccountCode);
        Assert.Contains(Data<List<BaseExpenseAccount>>(await ctl.GetAll()), x => x.Id == created.Id);
        Assert.Equal("授权科目", Data<BaseExpenseAccount>(await ctl.GetById(created.Id)).AccountName);

        var updated = Data<BaseExpenseAccount>(await ctl.Update(created.Id,
            NewExpenseAccount(created.AccountCode, "授权科目改名", type: 1, status: 0)));
        Assert.Equal("授权科目改名", updated.AccountName);
        Assert.Equal(1, updated.AccountType);

        var second = Data<BaseExpenseAccount>(await ctl.Create(NewExpenseAccount($"EA-OK2-{Tag()}", "授权科目2")));
        await ctl.BatchDelete(new List<long> { second.Id });
        Assert.True((await db.BaseExpenseAccounts.AsNoTracking().SingleAsync(x => x.Id == second.Id)).IsDeleted);

        await ctl.Delete(created.Id);
        Assert.True((await db.BaseExpenseAccounts.AsNoTracking().SingleAsync(x => x.Id == created.Id)).IsDeleted);
    }

    /// <summary>
    /// 特权（种子管理员，既有种子已授予全部菜单含 <c>expense-account</c>）：既有只读契约同样放行
    /// —— 授权口径不因特权而跳过菜单检查，也不新增任何用户授权。
    /// </summary>
    [Fact]
    public async Task Privileged_seed_admin_with_expense_menu_is_permitted()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var adminId = await db.SysUsers.AsNoTracking()
            .Where(u => u.UserName == SeedData.AdminUserName && !u.IsDeleted)
            .Select(u => u.Id).FirstAsync();

        var page = Data<PagedResult<BaseExpenseAccount>>(
            await Controller(db, adminId).GetPaged(new PageQuery { Page = 1, PageSize = 10 }));
        Assert.NotNull(page.Items);
    }

    // ==================== 3. 请求之间撤销授权立即收敛 ====================

    [Fact]
    public async Task Revoked_menu_between_requests_converges_to_denial()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var account = await SeedAccountAsync(db);
        var userId = await SeedUserAsync(db, UserStatus.Enabled, deleted: false, expenseMenu: true);

        Data<PagedResult<BaseExpenseAccount>>(await Controller(db, userId)
            .GetPaged(new PageQuery { Page = 1, PageSize = 10 }));   // 授权读取成功

        await RevokeExpenseMenuForUserAsync(db, userId);             // 撤销该账号的既有「角色 → 菜单」授权

        await AssertCodeAsync(ErrorCodes.Forbidden, () =>
            Controller(db, userId).GetPaged(new PageQuery { Page = 1, PageSize = 10 }));
        await AssertCodeAsync(ErrorCodes.Forbidden, () => Controller(db, userId).Delete(account.Id));
        Assert.False((await db.BaseExpenseAccounts.AsNoTracking().SingleAsync(x => x.Id == account.Id)).IsDeleted);
    }

    // ==================== 4. 授权后有界字段 / 唯一性 / 父级校验仍 fail closed 且零写入 ====================

    [Fact]
    public async Task Authorized_identity_with_invalid_payloads_fails_closed_without_writes()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var existing = await SeedAccountAsync(db);
        var deletedParent = await SeedAccountAsync(db, deleted: true);
        var userId = await SeedUserAsync(db, UserStatus.Enabled, deleted: false, expenseMenu: true);
        var ctl = Controller(db, userId);
        var before = await SnapshotAsync(db);

        await AssertCodeAsync(ErrorCodes.InvalidParameter, () => ctl.Create(NewExpenseAccount("", "名称")));
        await AssertCodeAsync(ErrorCodes.InvalidParameter, () => ctl.Create(NewExpenseAccount($"EA-{Tag()}", "   ")));
        await AssertCodeAsync(ErrorCodes.InvalidParameter, () => ctl.Create(NewExpenseAccount(
            new string('C', ExpenseAccountAuthorizationRules.MaxAccountCodeLength + 1), "超长编码")));
        await AssertCodeAsync(ErrorCodes.InvalidParameter, () => ctl.Create(NewExpenseAccount(
            $"EA-{Tag()}", new string('N', ExpenseAccountAuthorizationRules.MaxAccountNameLength + 1))));
        await AssertCodeAsync(ErrorCodes.InvalidParameter, () => ctl.Create(NewExpenseAccount(
            $"EA-{Tag()}", "名称", description: new string('D', ExpenseAccountAuthorizationRules.MaxDescriptionLength + 1))));
        await AssertCodeAsync(ErrorCodes.InvalidParameter, () => ctl.Create(NewExpenseAccount(
            $"EA-{Tag()}", "名称", type: 3)));
        await AssertCodeAsync(ErrorCodes.InvalidParameter, () => ctl.Create(NewExpenseAccount(
            $"EA-{Tag()}", "名称", status: 9)));

        // 编码重复（未删除行内唯一）→ 1003；父级不存在 / 已删除 → 1001；均不落行、不改写既有行。
        await AssertCodeAsync(ErrorCodes.Duplicate,
            () => ctl.Create(NewExpenseAccount(existing.AccountCode, "重复编码")));
        await AssertCodeAsync(ErrorCodes.InvalidParameter,
            () => ctl.Create(NewExpenseAccount($"EA-{Tag()}", "无效父级", parentId: 987_654_321L)));
        await AssertCodeAsync(ErrorCodes.InvalidParameter,
            () => ctl.Create(NewExpenseAccount($"EA-{Tag()}", "已删除父级", parentId: deletedParent.Id)));
        await AssertCodeAsync(ErrorCodes.InvalidParameter, () => ctl.Update(existing.Id,
            NewExpenseAccount(existing.AccountCode, "非法改名", status: 2)));

        Assert.Equal(before, await SnapshotAsync(db));
    }

    // ==================== 5. 自包含播种（真实既有授权模型） ====================

    private static string Tag() => Guid.NewGuid().ToString("N")[..8];

    private static async Task<string> SnapshotAsync(ErpDbContext db) => string.Join("|",
        await db.BaseExpenseAccounts.AsNoTracking().OrderBy(x => x.Id)
            .Select(x => $"{x.Id}:{x.AccountCode}:{x.AccountName}:{x.AccountType}:{x.Status}:{x.ParentId}:{x.Description}:{x.IsDeleted}")
            .ToListAsync());

    private static async Task<BaseExpenseAccount> SeedAccountAsync(ErpDbContext db, bool deleted = false)
    {
        var account = new BaseExpenseAccount
        {
            AccountCode = $"EAIA-{Tag()}",
            AccountName = "费用科目授权集成测试科目",
            AccountType = 2,
            Status = 1,
            IsDeleted = deleted
        };
        db.BaseExpenseAccounts.Add(account);
        await db.SaveChangesAsync();
        return account;
    }

    private static async Task<long> SeedUserAsync(
        ErpDbContext db, UserStatus status, bool deleted, bool expenseMenu)
    {
        var user = new SysUser
        {
            UserName = $"eaia-{Tag()}",
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = "费用科目授权集成测试账号",
            Status = status,
            IsDeleted = deleted
        };
        db.SysUsers.Add(user);
        await db.SaveChangesAsync();

        var role = new SysRole
        {
            RoleName = "费用科目授权集成测试角色",
            RoleCode = $"EAIA-{Tag()}",
            IsSystem = false
        };
        db.SysRoles.Add(role);
        await db.SaveChangesAsync();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        await db.SaveChangesAsync();

        if (expenseMenu)
            await GrantMenuAsync(db, role.Id, ExpenseAccountAuthorizationRules.RequiredMenuCode,
                ExpenseAccountAuthorizationRules.RequiredMenuText);
        return user.Id;
    }

    /// <summary>授予既有功能菜单（菜单由种子数据提供；缺失时按既有种子口径补建，不新增生产菜单）。</summary>
    private static async Task GrantMenuAsync(ErpDbContext db, long roleId, string menuCode, string menuName)
    {
        var menu = await db.SysMenus.FirstOrDefaultAsync(m => m.MenuCode == menuCode && !m.IsDeleted);
        if (menu is null)
        {
            menu = new SysMenu { MenuCode = menuCode, MenuName = menuName, MenuType = MenuType.Menu };
            db.SysMenus.Add(menu);
            await db.SaveChangesAsync();
        }
        if (!await db.SysRoleMenus.AnyAsync(rm => rm.RoleId == roleId && rm.MenuId == menu.Id && !rm.IsDeleted))
        {
            db.SysRoleMenus.Add(new SysRoleMenu { RoleId = roleId, MenuId = menu.Id });
            await db.SaveChangesAsync();
        }
    }

    /// <summary>回收指定账号的既有「费用科目」菜单授权（模拟请求之间撤销权限，不影响其它账号 / 种子管理员）。</summary>
    private static async Task RevokeExpenseMenuForUserAsync(ErpDbContext db, long userId)
    {
        var roleIds = await db.SysUserRoles.AsNoTracking()
            .Where(ur => ur.UserId == userId && !ur.IsDeleted)
            .Select(ur => ur.RoleId)
            .ToListAsync();
        var menuIds = await db.SysMenus.AsNoTracking()
            .Where(m => m.MenuCode == ExpenseAccountAuthorizationRules.RequiredMenuCode)
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
/// ERP-498 专用 localdb 目标 Fixture：只创建一个全新 GUID 后缀库并初始化完整 NEWERP 结构 + 种子数据，
/// 供费用科目实时授权集成测试复用。
/// <para>安全口径：实例必须精确为 <c>(localdb)\NEWERP_AutoAcceptance</c>，库名前缀必须为 <c>NEWERP_AUTOTEST</c>
/// 且使用集成安全；发现同名库已存在立即拒绝；绝不 drop / reset / 复用任何数据库，绝不读取生产设置。</para>
/// </summary>
public sealed class ExpenseAccountAuthorizationSqlServerFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";

    /// <summary>本次运行新建的 GUID 独占库名（每次运行唯一，绝不复用既有库）。</summary>
    public static string DefaultDatabaseName { get; } =
        $"{DatabasePrefix}_EXPENSEACCOUNTAUTH_{Guid.NewGuid():N}";

    public string ConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = BuildDefaultConnectionString();

        // 访问数据库之前先复核目标护栏（错误目标 fail closed）。
        AssertDedicatedTarget(connectionString);
        ConnectionString = connectionString;

        Console.WriteLine($"[ERP-498] 目标库护栏放行（实例 {InstanceMarker}，库名前缀 {DatabasePrefix}，集成安全）。");

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

        Console.WriteLine("[ERP-498] 集成场景就绪：完整 NEWERP 结构 + 种子数据（含既有 expense-account 菜单）。");
    }
}

/// <summary>目标库护栏单元级校验：非专用目标必须在访问数据库之前被拒绝。</summary>
public sealed class ExpenseAccountAuthorizationTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;User Id=sa;Password=secret")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(
            () => ExpenseAccountAuthorizationSqlServerFixture.AssertDedicatedTarget(connection));
}
