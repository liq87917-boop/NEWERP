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
/// ERP-444 商品规格变体（<c>/api/base/products/{productId}/variants</c>）实时授权的真实 SQL Server 集成测试
/// （GUID 独占 <c>NEWERP_AUTOTEST</c> 目标）。
/// <list type="number">
/// <item><b>真实控制器 + 真实既有授权</b>：以既有「商品资料」（<c>product</c>）菜单与既有「角色 → 菜单」口径
/// 驱动真实 <see cref="ProductVariantController"/>（注入真实 HTTP 身份）。</item>
/// <item><b>拒绝矩阵</b>：缺失 / 禁用 / 已删除 / 缺少商品菜单的身份在<b>全部路由</b> fail closed，
/// 且 <c>BaseProductVariants</c> 行逐字节不变（拒绝不落任何规格或状态变更）。</item>
/// <item><b>授权身份</b>：具备既有商品菜单时既有读 / 写契约放行；请求之间撤销菜单立即收敛为拒绝。</item>
/// <item><b>权威引用</b>：外部 / 已删除 / 已停用商品与非法载荷在授权后仍 fail closed 且零写入。</item>
/// </list>
/// <para>安全口径：目标必须为专用 localdb 实例 <c>NEWERP_AutoAcceptance</c>、库名前缀 <c>NEWERP_AUTOTEST</c>
/// 且集成安全；每次运行只创建全新 GUID 后缀库，发现同名库已存在立即拒绝；绝不 drop / reset / 复用任何数据库，
/// 连接串只来自进程环境变量或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据。</para>
/// </summary>
public sealed class ProductVariantAuthorizationSqlServerTests
    : IClassFixture<ProductVariantAuthorizationSqlServerFixture>
{
    private readonly ProductVariantAuthorizationSqlServerFixture _fixture;

    public ProductVariantAuthorizationSqlServerTests(ProductVariantAuthorizationSqlServerFixture fixture)
        => _fixture = fixture;

    /// <summary>访问数据库前复核专用目标护栏（实例名 / 库名前缀 / 集成安全）。</summary>
    private void Guard()
    {
        var target = new SqlConnectionStringBuilder(_fixture.ConnectionString);
        Assert.Equal("(localdb)\\NEWERP_AutoAcceptance", target.DataSource, ignoreCase: true);
        Assert.StartsWith(ProductVariantAuthorizationSqlServerFixture.DatabasePrefix,
            target.InitialCatalog, StringComparison.OrdinalIgnoreCase);
        Assert.True(target.IntegratedSecurity);
    }

    private static ProductVariantController Variant(ErpDbContext db, long? userId)
        => new(db) { ControllerContext = ContextFor(userId) };

    /// <summary>注入真实 HTTP 身份（可空 = 无 <c>NameIdentifier</c>，由授权护栏 fail closed 拒绝）。</summary>
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

    private static ProductVariantSaveDto Save(string code = "PVA-RED-XL", string color = "红色", string size = "XL",
        int? status = null)
        => new() { VariantCode = code, Color = color, Size = size, Status = status };

    private static ProductVariantDto VariantData(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<ApiResponse<ProductVariantDto>>(ok.Value);
        Assert.Equal(ErrorCodes.Success, resp.Code);
        return resp.Data!;
    }

    private static List<ProductVariantDto> VariantList(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<ApiResponse<List<ProductVariantDto>>>(ok.Value);
        Assert.Equal(ErrorCodes.Success, resp.Code);
        return resp.Data!;
    }

    private static async Task AssertCodeAsync(int expected, Func<Task<IActionResult>> action)
    {
        var ex = await Assert.ThrowsAsync<BusinessException>(action);
        Assert.Equal(expected, ex.Code);
    }

    /// <summary>规格子表快照（授权 / 引用拒绝后必须逐字节不变）。</summary>
    private static async Task<string> SnapshotAsync(ErpDbContext db) => string.Join("|",
        await db.BaseProductVariants.AsNoTracking().OrderBy(x => x.Id)
            .Select(x => $"{x.Id}:{x.Status}:{x.IsDeleted}:{x.ProductId}:{x.VariantCode}:{x.ColorSizeKey}")
            .ToListAsync());

    // ==================== 1. 拒绝矩阵：fail closed 且零写入 ====================

    [Theory]
    [InlineData("missing")]
    [InlineData("disabled")]
    [InlineData("deleted")]
    [InlineData("no-product-menu")]
    public async Task Denied_identities_denied_on_every_route_and_mutate_no_row(string scenario)
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var product = await SeedProductAsync(db);
        var variant = await SeedVariantAsync(db, product.Id);

        long? userId = scenario switch
        {
            "missing" => null,
            "disabled" => await SeedUserAsync(db, UserStatus.Disabled, deleted: false, productMenu: true),
            "deleted" => await SeedUserAsync(db, UserStatus.Enabled, deleted: true, productMenu: true),
            _ => await SeedUserAsync(db, UserStatus.Enabled, deleted: false, productMenu: false)
        };
        var expected = scenario is "missing" or "deleted" ? ErrorCodes.Unauthorized : ErrorCodes.Forbidden;

        var ctl = Variant(db, userId);
        var before = await SnapshotAsync(db);

        await AssertCodeAsync(expected, () => ctl.List(product.Id));
        await AssertCodeAsync(expected, () => ctl.Options(product.Id));
        await AssertCodeAsync(expected, () => ctl.Create(product.Id, Save(code: $"PVA-{Tag()}", color: "蓝色", size: "L")));
        await AssertCodeAsync(expected, () => ctl.Update(product.Id, variant.Id, Save(code: variant.VariantCode, color: "蓝色", size: "L")));
        await AssertCodeAsync(expected, () => ctl.Disable(product.Id, variant.Id));
        await AssertCodeAsync(expected, () => ctl.Enable(product.Id, variant.Id));
        await AssertCodeAsync(expected, () => ctl.Delete(product.Id, variant.Id));

        Assert.Equal(before, await SnapshotAsync(db));
        var stored = await db.BaseProductVariants.AsNoTracking().SingleAsync(x => x.Id == variant.Id);
        Assert.Equal(ProductVariantRules.ActiveStatus, stored.Status);
        Assert.False(stored.IsDeleted);
    }

    // ==================== 2. 授权身份：既有契约放行且生命周期可维护 ====================

    [Fact]
    public async Task Permitted_identity_reads_and_writes_with_existing_contract()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var product = await SeedProductAsync(db);
        var userId = await SeedUserAsync(db, UserStatus.Enabled, deleted: false, productMenu: true);
        var ctl = Variant(db, userId);

        var created = VariantData(await ctl.Create(product.Id, Save(code: "PVA-RED-XL", color: "红色", size: "XL")));
        Assert.Equal("PVA-RED-XL", created.VariantCode);
        Assert.Equal("红色|XL", created.ColorSizeKey);
        Assert.Single(VariantList(await ctl.List(product.Id)));
        Assert.Single(VariantList(await ctl.Options(product.Id)));

        var updated = VariantData(await ctl.Update(product.Id, created.Id, Save(code: "PVA-RED-L", color: "红色", size: "L")));
        Assert.Equal("PVA-RED-L", updated.VariantCode);

        Assert.Equal(ProductVariantRules.DisabledStatus, VariantData(await ctl.Disable(product.Id, created.Id)).Status);
        Assert.Equal(ProductVariantRules.ActiveStatus, VariantData(await ctl.Enable(product.Id, created.Id)).Status);

        await ctl.Delete(product.Id, created.Id);
        Assert.True((await db.BaseProductVariants.AsNoTracking().SingleAsync(x => x.Id == created.Id)).IsDeleted);
    }

    // ==================== 3. 请求之间撤销授权立即收敛 ====================

    [Fact]
    public async Task Revoked_menu_between_requests_converges_to_denial()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var product = await SeedProductAsync(db);
        var variant = await SeedVariantAsync(db, product.Id);
        var userId = await SeedUserAsync(db, UserStatus.Enabled, deleted: false, productMenu: true);

        VariantList(await Variant(db, userId).List(product.Id));            // 授权读取成功

        await RevokeProductMenuAsync(db);                                   // 撤销既有「角色 → 菜单」授权

        await AssertCodeAsync(ErrorCodes.Forbidden, () => Variant(db, userId).List(product.Id));
        await AssertCodeAsync(ErrorCodes.Forbidden, () => Variant(db, userId).Delete(product.Id, variant.Id));
        Assert.False((await db.BaseProductVariants.AsNoTracking()
            .SingleAsync(x => x.Id == variant.Id)).IsDeleted);
    }

    // ==================== 4. 授权后权威引用 / 载荷仍 fail closed 且零写入 ====================

    [Fact]
    public async Task Authorized_but_foreign_deleted_disabled_product_and_invalid_payload_fail_closed()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var product = await SeedProductAsync(db);
        var deletedProduct = await SeedProductAsync(db, deleted: true);
        var disabledProduct = await SeedProductAsync(db, status: 0);
        var variant = await SeedVariantAsync(db, product.Id);
        var userId = await SeedUserAsync(db, UserStatus.Enabled, deleted: false, productMenu: true);
        var ctl = Variant(db, userId);
        var before = await SnapshotAsync(db);

        await AssertCodeAsync(ErrorCodes.NotFound, () => ctl.List(987654321L));
        await AssertCodeAsync(ErrorCodes.NotFound, () => ctl.Create(987654321L, Save(code: $"PVA-{Tag()}")));
        await AssertCodeAsync(ErrorCodes.NotFound, () => ctl.Delete(deletedProduct.Id, variant.Id));
        await AssertCodeAsync(ErrorCodes.InvalidParameter, () => ctl.List(disabledProduct.Id));
        await AssertCodeAsync(ErrorCodes.InvalidParameter, () => ctl.Create(disabledProduct.Id, Save(code: $"PVA-{Tag()}")));
        await AssertCodeAsync(ErrorCodes.InvalidParameter, () => ctl.Create(product.Id, Save(code: "BAD;CODE")));
        await AssertCodeAsync(ErrorCodes.InvalidParameter, () => ctl.Create(product.Id, Save(code: $"PVA-{Tag()}", color: " ", size: "")));
        await AssertCodeAsync(ErrorCodes.InvalidParameter, () =>
            ctl.Update(product.Id, variant.Id, Save(code: variant.VariantCode, color: "红色", size: "XL", status: 9)));

        Assert.Equal(before, await SnapshotAsync(db));
    }

    // ==================== 5. 自包含播种（真实既有授权模型） ====================

    private static string Tag() => Guid.NewGuid().ToString("N")[..8];

    private static async Task<BaseProduct> SeedProductAsync(
        ErpDbContext db, int status = 1, bool deleted = false)
    {
        var product = new BaseProduct
        {
            ProductCode = $"PVA-P-{Tag()}",
            ProductName = "规格授权测试商品",
            Unit = "PCS",
            Status = status,
            IsDeleted = deleted
        };
        db.BaseProducts.Add(product);
        await db.SaveChangesAsync();
        return product;
    }

    private static async Task<BaseProductVariant> SeedVariantAsync(
        ErpDbContext db, long productId, int status = 1, bool deleted = false)
    {
        var size = Tag();
        var variant = new BaseProductVariant
        {
            ProductId = productId,
            VariantCode = ProductVariantRules.NormalizeCode($"PVA-V-{Tag()}"),
            Color = "红色",
            Size = size,
            ColorSizeKey = ProductVariantRules.BuildColorSizeKey("红色", size),
            Status = status,
            IsDeleted = deleted
        };
        db.BaseProductVariants.Add(variant);
        await db.SaveChangesAsync();
        return variant;
    }

    private static async Task<long> SeedUserAsync(ErpDbContext db, UserStatus status, bool deleted, bool productMenu)
    {
        var user = new SysUser
        {
            UserName = $"pva-{Tag()}",
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = "规格变体授权集成测试账号",
            Status = status,
            IsDeleted = deleted
        };
        db.SysUsers.Add(user);
        await db.SaveChangesAsync();

        var role = new SysRole
        {
            RoleName = "规格变体授权集成测试角色",
            RoleCode = $"PVA-{Tag()}",
            IsSystem = false
        };
        db.SysRoles.Add(role);
        await db.SaveChangesAsync();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        await db.SaveChangesAsync();

        if (productMenu) await GrantMenuAsync(db, role.Id, ProductVariantAuthorizationRules.ProductMenuCode);
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

    /// <summary>回收既有「商品资料」菜单授权（模拟请求之间撤销权限）。</summary>
    private static async Task RevokeProductMenuAsync(ErpDbContext db)
    {
        var menuIds = await db.SysMenus.AsNoTracking()
            .Where(m => m.MenuCode == ProductVariantAuthorizationRules.ProductMenuCode)
            .Select(m => m.Id)
            .ToListAsync();
        var grants = await db.SysRoleMenus.Where(rm => menuIds.Contains(rm.MenuId)).ToListAsync();
        foreach (var grant in grants) grant.IsDeleted = true;
        await db.SaveChangesAsync();
    }
}

/// <summary>
/// ERP-444 专用 localdb 目标 Fixture：只创建一个全新 GUID 后缀库并初始化完整 NEWERP 结构 + 种子数据，
/// 供商品规格变体实时授权集成测试复用。
/// <para>安全口径：实例必须精确为 <c>(localdb)\NEWERP_AutoAcceptance</c>，库名前缀必须为 <c>NEWERP_AUTOTEST</c>
/// 且使用集成安全；发现同名库已存在立即拒绝；绝不 drop / reset / 复用任何数据库，绝不读取生产设置。</para>
/// </summary>
public sealed class ProductVariantAuthorizationSqlServerFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";

    /// <summary>本次运行新建的 GUID 独占库名（每次运行唯一，绝不复用既有库）。</summary>
    public static string DefaultDatabaseName { get; } =
        $"{DatabasePrefix}_PVAUTHORIZATION_{Guid.NewGuid():N}";

    public string ConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = BuildDefaultConnectionString();

        // 访问数据库之前先复核目标护栏（错误目标 fail closed）。
        AssertDedicatedTarget(connectionString);
        ConnectionString = connectionString;

        Console.WriteLine($"[ERP-444] 目标库护栏放行（实例 {InstanceMarker}，库名前缀 {DatabasePrefix}，集成安全）。");

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

        Console.WriteLine("[ERP-444] 集成场景就绪：完整 NEWERP 结构 + 种子数据（含既有 product 菜单）。");
    }
}

/// <summary>目标库护栏单元级校验：非专用目标必须在访问数据库之前被拒绝。</summary>
public sealed class ProductVariantAuthorizationTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(() =>
            ProductVariantAuthorizationSqlServerFixture.AssertDedicatedTarget(connection));
}
