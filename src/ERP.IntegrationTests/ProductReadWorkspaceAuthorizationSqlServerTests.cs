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
/// ERP-461 商品只读工作台（商品图片库 <c>api/base/product-images</c> 与出口字段完整度
/// <c>api/base/products/export-field-completeness</c>）实时授权的真实 SQL Server 集成测试
/// （GUID 独占 <c>NEWERP_AUTOTEST</c> 目标）。
/// <list type="number">
/// <item><b>真实控制器 + 真实既有授权</b>：以既有「商品资料」（<c>product</c>）菜单与既有
/// 「角色 → 菜单」口径驱动两个只读控制器（注入真实 HTTP 身份），验证与 ERP-452 <c>api/base/products</c>
/// 完全同源的判定。</item>
/// <item><b>拒绝矩阵</b>：缺失 / 禁用 / 已删除 / 无菜单的身份在两条并列路由上 fail closed，
/// 且 <c>BaseProducts</c> 行逐字节不变（拒绝既不读取也不改写任何行）。</item>
/// <item><b>授权身份</b>：具备既有商品菜单（含特权种子管理员）时两个只读工作台的既有读契约放行；
/// 请求之间撤销菜单立即收敛为拒绝。</item>
/// </list>
/// <para>安全口径：目标必须为专用 localdb 实例 <c>NEWERP_AutoAcceptance</c>、库名前缀 <c>NEWERP_AUTOTEST</c>
/// 且集成安全；每次运行只创建全新 GUID 后缀库，发现同名库已存在立即拒绝；绝不 drop / reset / 复用任何数据库，
/// 连接串只来自进程环境变量或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据。</para>
/// </summary>
public sealed class ProductReadWorkspaceAuthorizationSqlServerTests
    : IClassFixture<ProductReadWorkspaceAuthorizationSqlServerFixture>
{
    private readonly ProductReadWorkspaceAuthorizationSqlServerFixture _fixture;

    public ProductReadWorkspaceAuthorizationSqlServerTests(ProductReadWorkspaceAuthorizationSqlServerFixture fixture)
        => _fixture = fixture;

    /// <summary>访问数据库前复核专用目标护栏（实例名 / 库名前缀 / 集成安全）。</summary>
    private void Guard()
    {
        var target = new SqlConnectionStringBuilder(_fixture.ConnectionString);
        Assert.Equal("(localdb)\\NEWERP_AutoAcceptance", target.DataSource, ignoreCase: true);
        Assert.StartsWith(ProductReadWorkspaceAuthorizationSqlServerFixture.DatabasePrefix,
            target.InitialCatalog, StringComparison.OrdinalIgnoreCase);
        Assert.True(target.IntegratedSecurity);
    }

    private static ProductImageLibraryController ImageLibrary(ErpDbContext db, long? userId)
        => new(db) { ControllerContext = ContextFor(userId) };

    private static ProductExportFieldCompletenessController ExportWorksheet(ErpDbContext db, long? userId)
        => new(db) { ControllerContext = ContextFor(userId) };

    /// <summary>
    /// 注入真实 HTTP 身份（可空 = 无 <c>NameIdentifier</c>）。故意<b>不</b>设置 <c>Request.Path</c>：
    /// 授权是无条件的，绝不依赖请求路径放行或绕过。
    /// </summary>
    private static ControllerContext ContextFor(long? userId)
    {
        var claims = userId.HasValue
            ? new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }
            : Array.Empty<Claim>();
        return new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"))
            }
        };
    }

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

    private static string Tag() => Guid.NewGuid().ToString("N")[..8];

    // ==================== 1. 身份 / 账号状态 / 菜单 fail closed ====================

    [Theory]
    [InlineData("missing")]
    [InlineData("disabled")]
    [InlineData("deleted")]
    [InlineData("no-menu")]
    public async Task Live_denies_both_read_workspaces_without_mutating_any_product_row(string scenario)
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var product = await SeedProductAsync(db);
        long? userId = scenario switch
        {
            "missing" => null,
            "disabled" => await SeedUserAsync(db, UserStatus.Disabled, deleted: false, productMenu: true),
            "deleted" => await SeedUserAsync(db, UserStatus.Enabled, deleted: true, productMenu: true),
            _ => await SeedUserAsync(db, UserStatus.Enabled, deleted: false, productMenu: false)
        };
        var expectedCode = scenario is "missing" or "deleted"
            ? ErrorCodes.Unauthorized
            : ErrorCodes.Forbidden;
        var before = await SnapshotAsync(db);

        await AssertCodeAsync(expectedCode, () => ImageLibrary(db, userId)
            .List(new ProductImageLibraryQuery(), CancellationToken.None));
        await AssertCodeAsync(expectedCode, () => ExportWorksheet(db, userId)
            .GetWorksheet(new ProductExportFieldCompletenessQuery()));

        Assert.Equal(before, await SnapshotAsync(db));
        var stored = await db.BaseProducts.AsNoTracking().SingleAsync(x => x.Id == product.Id);
        Assert.Equal(product.ProductCode, stored.ProductCode);
        Assert.Equal(product.ProductName, stored.ProductName);
    }

    // ==================== 2. 授权身份：既有只读契约放行 ====================

    [Fact]
    public async Task Authorized_identity_reads_both_workspaces()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var product = await SeedProductAsync(db);
        var userId = await SeedUserAsync(db, UserStatus.Enabled, deleted: false, productMenu: true);

        var page = Data<ProductImageLibraryPage>(await ImageLibrary(db, userId)
            .List(new ProductImageLibraryQuery { ProductId = product.Id }, CancellationToken.None));
        var row = Assert.Single(page.Items);
        Assert.Equal(product.Id, row.ProductId);
        Assert.Equal(product.ProductCode, row.ProductCode);
        Assert.Equal(3, row.Images.Count);

        var worksheet = Data<ProductExportFieldCompletenessDto>(await ExportWorksheet(db, userId)
            .GetWorksheet(new ProductExportFieldCompletenessQuery { Keyword = product.ProductCode }));
        var worksheetRow = Assert.Single(worksheet.Items);
        Assert.Equal(product.Id, worksheetRow.ProductId);
        Assert.Equal(product.ProductCode, worksheetRow.ProductCode);
    }

    /// <summary>
    /// 特权（种子管理员，既有种子已授予全部菜单含 <c>product</c>）：两个只读工作台的既有读契约同样放行
    /// —— 授权口径不因特权而跳过菜单检查，也不新增任何用户授权。
    /// </summary>
    [Fact]
    public async Task Privileged_seed_admin_with_product_menu_is_permitted()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var adminId = await db.SysUsers.AsNoTracking()
            .Where(u => u.UserName == SeedData.AdminUserName && !u.IsDeleted)
            .Select(u => u.Id).FirstAsync();

        var page = Data<ProductImageLibraryPage>(await ImageLibrary(db, adminId)
            .List(new ProductImageLibraryQuery(), CancellationToken.None));
        Assert.NotNull(page.Items);

        var worksheet = Data<ProductExportFieldCompletenessDto>(await ExportWorksheet(db, adminId)
            .GetWorksheet(new ProductExportFieldCompletenessQuery()));
        Assert.NotNull(worksheet.Items);
    }

    // ==================== 3. 请求之间撤销授权立即收敛 ====================

    [Fact]
    public async Task Revoked_menu_between_requests_converges_to_denial()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var product = await SeedProductAsync(db);
        var userId = await SeedUserAsync(db, UserStatus.Enabled, deleted: false, productMenu: true);

        Data<ProductImageLibraryPage>(await ImageLibrary(db, userId)
            .List(new ProductImageLibraryQuery { ProductId = product.Id }, CancellationToken.None));
        Data<ProductExportFieldCompletenessDto>(await ExportWorksheet(db, userId)
            .GetWorksheet(new ProductExportFieldCompletenessQuery()));

        await RevokeProductMenuForUserAsync(db, userId);   // 撤销该账号的既有「角色 → 菜单」授权

        await AssertCodeAsync(ErrorCodes.Forbidden, () => ImageLibrary(db, userId)
            .List(new ProductImageLibraryQuery(), CancellationToken.None));
        await AssertCodeAsync(ErrorCodes.Forbidden, () => ExportWorksheet(db, userId)
            .GetWorksheet(new ProductExportFieldCompletenessQuery()));

        var stored = await db.BaseProducts.AsNoTracking().SingleAsync(x => x.Id == product.Id);
        Assert.Equal(product.ProductCode, stored.ProductCode);
        Assert.False(stored.IsDeleted);
    }


    // ==================== 4. 自包含播种（真实既有授权模型） ====================

    private static async Task<string> SnapshotAsync(ErpDbContext db) => string.Join("|",
        await db.BaseProducts.AsNoTracking().OrderBy(x => x.Id)
            .Select(x => $"{x.Id}:{x.Status}:{x.IsDeleted}:{x.ProductCode}:{x.ProductName}:" +
                         $"{x.Image1}:{x.Image2}:{x.Image3}:{x.EnglishDeclareName}:{x.PackageUnit}:" +
                         $"{x.UnitsPerPackage}:{x.RefundRate}")
            .ToListAsync());

    private static async Task<BaseProduct> SeedProductAsync(ErpDbContext db)
    {
        var product = new BaseProduct
        {
            ProductCode = $"P-READ-{Tag()}",
            ProductName = "只读工作台商品",
            Spec = "标准",
            Unit = "PCS",
            Image1 = $"/oss/NEWERP/{Tag()}/main.png",
            Status = 1
        };
        db.BaseProducts.Add(product);
        await db.SaveChangesAsync();
        return product;
    }

    private static async Task<long> SeedUserAsync(
        ErpDbContext db, UserStatus status, bool deleted, bool productMenu)
    {
        var user = new SysUser
        {
            UserName = $"read-workspace-{Tag()}",
            DisplayName = "只读工作台集成用例账号",
            PasswordHash = "hash",
            PasswordSalt = "salt",
            Status = status,
            IsDeleted = deleted
        };
        db.SysUsers.Add(user);
        await db.SaveChangesAsync();

        var role = new SysRole
        {
            RoleName = "只读工作台集成用例角色",
            RoleCode = $"ReadWorkspaceSql-{Tag()}",
            IsSystem = false
        };
        db.SysRoles.Add(role);
        await db.SaveChangesAsync();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        await db.SaveChangesAsync();

        if (productMenu)
            await GrantMenuAsync(db, role.Id, ProductReadWorkspaceAuthorizationRules.RequiredMenuCode,
                ProductReadWorkspaceAuthorizationRules.RequiredMenuText);
        return user.Id;
    }

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

    /// <summary>回收指定账号的既有「商品资料」菜单授权（模拟请求之间撤销权限，不影响其它账号 / 种子管理员）。</summary>
    private static async Task RevokeProductMenuForUserAsync(ErpDbContext db, long userId)
    {
        var roleIds = await db.SysUserRoles.AsNoTracking()
            .Where(ur => ur.UserId == userId && !ur.IsDeleted)
            .Select(ur => ur.RoleId)
            .ToListAsync();
        var menuIds = await db.SysMenus.AsNoTracking()
            .Where(m => m.MenuCode == ProductReadWorkspaceAuthorizationRules.RequiredMenuCode)
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
/// ERP-461 专用 localdb 目标 Fixture：只创建一个全新 GUID 后缀库并初始化完整 NEWERP 结构 + 种子数据，
/// 供商品只读工作台实时授权集成测试复用。
/// <para>安全口径：实例必须精确为 <c>(localdb)\NEWERP_AutoAcceptance</c>，库名前缀必须为 <c>NEWERP_AUTOTEST</c>
/// 且使用集成安全；发现同名库已存在立即拒绝；绝不 drop / reset / 复用任何数据库，绝不读取生产设置。</para>
/// </summary>
public sealed class ProductReadWorkspaceAuthorizationSqlServerFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";

    /// <summary>本次运行新建的 GUID 独占库名（每次运行唯一，绝不复用既有库）。</summary>
    public static string DefaultDatabaseName { get; } =
        $"{DatabasePrefix}_PRODUCTREADWORKSPACE_{Guid.NewGuid():N}";

    public string ConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = BuildDefaultConnectionString();

        // 访问数据库之前先复核目标护栏（错误目标 fail closed）。
        AssertDedicatedTarget(connectionString);
        ConnectionString = connectionString;

        Console.WriteLine($"[ERP-461] 目标库护栏放行（实例 {InstanceMarker}，库名前缀 {DatabasePrefix}，集成安全）。");

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

        Console.WriteLine("[ERP-461] 集成场景就绪：完整 NEWERP 结构 + 种子数据（含既有 product 菜单）。");
    }
}

/// <summary>目标库护栏单元级校验：非专用目标必须在访问数据库之前被拒绝。</summary>
public sealed class ProductReadWorkspaceAuthorizationTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;User Id=sa;Password=secret")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(
            () => ProductReadWorkspaceAuthorizationSqlServerFixture.AssertDedicatedTarget(connection));
}

