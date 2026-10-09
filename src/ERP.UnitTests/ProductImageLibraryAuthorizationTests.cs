using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// 商品只读工作台（商品图片库 <c>api/base/product-images</c> 与出口字段完整度
/// <c>api/base/products/export-field-completeness</c>）实时授权单元测试（ERP-461）。覆盖：
/// <list type="number">
/// <item><b>拒绝矩阵</b>：缺失 / 禁用 / 已删除 / 缺少既有「商品资料」菜单的身份在两条并列路由上
/// fail closed（<c>2000</c> / <c>2002</c>），拒绝发生在任何 <c>BaseProducts</c> 读取之前，且商品主表逐字节不变；</item>
/// <item><b>放行</b>：具备既有「商品资料」菜单的授权身份下，两个只读工作台的既有读契约照常放行；</item>
/// <item><b>收敛</b>：请求之间撤销菜单授权后下一次请求立即拒绝（每次请求重新解析，绝不缓存）；</item>
/// <item><b>只读</b>：被拒绝的请求不返回任何商品编码 / 名称 / 图片引用 / 字段完整度状态、零 <c>BaseProducts</c> 读取、零写入；</item>
/// <item><b>源码契约</b>：两个控制器每条路由都<b>无条件</b>先经实时授权复用既有 <c>product</c> 菜单，
/// 不依赖 <c>Request.Path</c> / 环境 / 假身份，不新增菜单，也不改变 ERP-452 的 <c>api/base/products</c> 契约。</item>
/// </list>
/// 全部使用内存库（TestDbFactory），不连接 SQL Server、不访问 OSS、不执行任何 SQL / 部署脚本。
/// </summary>
public class ProductImageLibraryAuthorizationTests
{
    private const string LocalReference = "/oss/NEWERP/20260925/p001-main.png";

    // ==================== 0. 测试脚手架 ====================

    private static ProductImageLibraryController ImageLibrary(IErpDbContext db, long? userId)
        => new(db) { ControllerContext = ContextWithUser(userId) };

    private static ProductExportFieldCompletenessController ExportWorksheet(IErpDbContext db, long? userId)
        => new(db) { ControllerContext = ContextWithUser(userId) };

    /// <summary>
    /// 带（可空）<c>NameIdentifier</c> 的 HTTP 身份上下文；<c>null</c> = 无身份。
    /// 故意<b>不</b>设置 <c>Request.Path</c>：授权是无条件的，绝不依赖请求路径放行或绕过。
    /// </summary>
    private static ControllerContext ContextWithUser(long? userId)
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

    /// <summary>播种一个独立授权身份（可选状态 / 删除 / 商品菜单），返回用户 Id（每个用例独立）</summary>
    private static long SeedUser(ErpDbContext db, UserStatus status = UserStatus.Enabled, bool deleted = false,
        bool grantProductMenu = true)
    {
        var user = new SysUser
        {
            UserName = $"read-workspace-{Guid.NewGuid():N}",
            DisplayName = "只读工作台授权用例账号",
            PasswordHash = "hash",
            PasswordSalt = "salt",
            Status = status,
            IsDeleted = deleted
        };
        db.SysUsers.Add(user);
        db.SaveChanges();

        var role = new SysRole
        {
            RoleName = "只读工作台授权用例角色",
            RoleCode = $"ReadWorkspaceCase-{Guid.NewGuid():N}",
            IsSystem = false
        };
        db.SysRoles.Add(role);
        db.SaveChanges();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        db.SaveChanges();

        if (grantProductMenu)
            GrantMenu(db, role.Id, ProductReadWorkspaceAuthorizationRules.RequiredMenuCode,
                ProductReadWorkspaceAuthorizationRules.RequiredMenuText);
        return user.Id;
    }

    /// <summary>按既有菜单编码授予角色访问权限（与生产「角色 → 菜单」口径同源）</summary>
    private static void GrantMenu(ErpDbContext db, long roleId, string menuCode, string menuName)
    {
        var menu = db.SysMenus.FirstOrDefault(m => m.MenuCode == menuCode && !m.IsDeleted);
        if (menu is null)
        {
            menu = new SysMenu { MenuCode = menuCode, MenuName = menuName, MenuType = MenuType.Menu };
            db.SysMenus.Add(menu);
            db.SaveChanges();
        }
        if (!db.SysRoleMenus.Any(rm => rm.RoleId == roleId && rm.MenuId == menu.Id && !rm.IsDeleted))
        {
            db.SysRoleMenus.Add(new SysRoleMenu { RoleId = roleId, MenuId = menu.Id });
            db.SaveChanges();
        }
    }

    private static BaseProduct SeedProduct(ErpDbContext db, string code, string name, string image1 = "")
    {
        var product = new BaseProduct
        {
            ProductCode = code,
            ProductName = name,
            Spec = "标准",
            Unit = "PCS",
            Image1 = image1,
            Status = 1
        };
        db.BaseProducts.Add(product);
        db.SaveChanges();
        return product;
    }

    /// <summary>商品主表快照（授权拒绝后必须逐字节不变）：包含编码 / 名称 / 图片位与出口字段完整度输入。</summary>
    private static string Snapshot(ErpDbContext db) => string.Join("|",
        db.BaseProducts.AsNoTracking().OrderBy(x => x.Id)
            .Select(x => $"{x.Id}:{x.Status}:{x.IsDeleted}:{x.ProductCode}:{x.ProductName}:" +
                         $"{x.Image1}:{x.Image2}:{x.Image3}:{x.EnglishDeclareName}:{x.PackageUnit}:" +
                         $"{x.UnitsPerPackage}:{x.OuterLength}:{x.OuterWidth}:{x.OuterHeight}:{x.OuterWeight}:{x.RefundRate}")
            .ToList());

    private static async Task AssertCode(int expected, Func<Task<IActionResult>> action)
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

    private static string RepoFile(params string[] segments)
        => Path.GetFullPath(Path.Combine(
            new[] { AppContext.BaseDirectory, "..", "..", "..", "..", ".." }.Concat(segments).ToArray()));

    // ==================== 1. 缺失 / 禁用 / 已删除 / 无菜单 fail closed ====================

    [Theory]
    [InlineData("missing")]
    [InlineData("disabled")]
    [InlineData("deleted")]
    [InlineData("no-menu")]
    public async Task Denies_both_read_workspaces_without_mutating_any_product_row(string scenario)
    {
        using var db = TestDbFactory.Create();
        SeedProduct(db, "P001", "保温杯", LocalReference);
        long? userId = scenario switch
        {
            "missing" => null,
            "disabled" => SeedUser(db, UserStatus.Disabled, deleted: false, grantProductMenu: true),
            "deleted" => SeedUser(db, UserStatus.Enabled, deleted: true, grantProductMenu: true),
            _ => SeedUser(db, UserStatus.Enabled, deleted: false, grantProductMenu: false)
        };
        var expectedCode = scenario is "missing" or "deleted"
            ? ErrorCodes.Unauthorized
            : ErrorCodes.Forbidden;
        var before = Snapshot(db);

        await AssertCode(expectedCode, () => ImageLibrary(db, userId)
            .List(new ProductImageLibraryQuery(), CancellationToken.None));
        await AssertCode(expectedCode, () => ExportWorksheet(db, userId)
            .GetWorksheet(new ProductExportFieldCompletenessQuery()));

        Assert.Equal(before, Snapshot(db));
    }

    /// <summary>
    /// 被拒绝的只读请求在读取任何 <c>BaseProducts</c> 行之前即 fail closed：数据集访问记录里不含
    /// <c>BaseProducts</c>，且零写入（因此不返回任何商品编码 / 名称 / 图片引用 / 字段完整度状态）。
    /// </summary>
    [Fact]
    public async Task Denied_request_reads_no_BaseProducts_and_writes_nothing()
    {
        using var db = TestDbFactory.Create();
        SeedProduct(db, "P001", "保温杯", LocalReference);
        var counting = ProductImageLibraryTests.CountingDbContext.Wrap(db);

        await AssertCode(ErrorCodes.Unauthorized, () => ImageLibrary(counting.Proxy, null)
            .List(new ProductImageLibraryQuery(), CancellationToken.None));
        await AssertCode(ErrorCodes.Unauthorized, () => ExportWorksheet(counting.Proxy, null)
            .GetWorksheet(new ProductExportFieldCompletenessQuery()));

        Assert.DoesNotContain("BaseProducts", counting.ReadProperties);
        Assert.Empty(counting.ReadProperties);
        Assert.Equal(0, counting.WriteCalls);
    }

    // ==================== 2. 授权身份：既有只读契约放行与撤销收敛 ====================

    [Fact]
    public async Task Authorized_identity_reads_both_workspaces()
    {
        using var db = TestDbFactory.Create();
        SeedProduct(db, "P001", "保温杯", LocalReference);
        var userId = SeedUser(db);

        var page = Data<ProductImageLibraryPage>(await ImageLibrary(db, userId)
            .List(new ProductImageLibraryQuery(), CancellationToken.None));
        var row = Assert.Single(page.Items);
        Assert.Equal("P001", row.ProductCode);
        Assert.Equal("保温杯", row.ProductName);

        var worksheet = Data<ProductExportFieldCompletenessDto>(await ExportWorksheet(db, userId)
            .GetWorksheet(new ProductExportFieldCompletenessQuery()));
        var worksheetRow = Assert.Single(worksheet.Items);
        Assert.Equal("P001", worksheetRow.ProductCode);
    }

    /// <summary>请求之间撤销菜单授权：下一次请求立即收敛为拒绝（每次都重新解析，绝不缓存）。</summary>
    [Fact]
    public async Task Revoked_menu_converges_to_denial_on_the_next_request()
    {
        using var db = TestDbFactory.Create();
        SeedProduct(db, "P001", "保温杯", LocalReference);
        var userId = SeedUser(db);

        Data<ProductImageLibraryPage>(await ImageLibrary(db, userId)
            .List(new ProductImageLibraryQuery(), CancellationToken.None));  // 授权读取成功
        Data<ProductExportFieldCompletenessDto>(await ExportWorksheet(db, userId)
            .GetWorksheet(new ProductExportFieldCompletenessQuery()));         // 授权读取成功

        foreach (var grant in db.SysRoleMenus.ToList()) grant.IsDeleted = true;   // 撤销该账号的菜单授权
        db.SaveChanges();

        await AssertCode(ErrorCodes.Forbidden, () => ImageLibrary(db, userId)
            .List(new ProductImageLibraryQuery(), CancellationToken.None));
        await AssertCode(ErrorCodes.Forbidden, () => ExportWorksheet(db, userId)
            .GetWorksheet(new ProductExportFieldCompletenessQuery()));
    }


    // ==================== 3. 源码与菜单契约 ====================

    /// <summary>
    /// 两个控制器每条路由都无条件先经实时授权，复用既有 <c>product</c> 菜单，且不依赖
    /// <c>Request.Path</c> / 环境放行；ERP-452 的 <c>api/base/products</c> 契约保持不变。
    /// </summary>
    [Fact]
    public void Both_controllers_enforce_live_authorization_and_reuse_the_existing_product_menu()
    {
        // 复用既有「商品资料」菜单常量（与 ProductAuthorizationRules / SeedData.Menus 同源），不新增菜单。
        Assert.Equal("product", ProductReadWorkspaceAuthorizationRules.RequiredMenuCode);
        Assert.Equal("商品资料", ProductReadWorkspaceAuthorizationRules.RequiredMenuText);
        Assert.Equal(ProductAuthorizationRules.RequiredMenuCode, ProductReadWorkspaceAuthorizationRules.RequiredMenuCode);
        Assert.Equal(ProductAuthorizationRules.RequiredMenuText, ProductReadWorkspaceAuthorizationRules.RequiredMenuText);

        var rulesSource = File.ReadAllText(RepoFile(
            "src", "ERP.Application", "Services", "ProductReadWorkspaceAuthorizationRules.cs"));
        Assert.Contains("ProductAuthorizationRules.EnsureAuthorizedAsync", rulesSource);
        Assert.DoesNotContain("SysMenus", rulesSource);      // 不新增 / 不写任何菜单
        Assert.DoesNotContain("SysRoleMenus", rulesSource);  // 不新增 / 不写任何角色授权

        var imageSource = File.ReadAllText(RepoFile(
            "src", "ERP.Api", "Controllers", "ProductImageLibraryController.cs"));
        var exportSource = File.ReadAllText(RepoFile(
            "src", "ERP.Api", "Controllers", "ProductExportFieldCompleteness.cs"));

        Assert.Single(Regex.Matches(imageSource,
            @"ProductReadWorkspaceAuthorizationRules\.EnsureAuthorizedAsync\("));
        Assert.Single(Regex.Matches(exportSource,
            @"ProductReadWorkspaceAuthorizationRules\.EnsureAuthorizedAsync\("));

        foreach (var source in new[] { imageSource, exportSource })
        {
            Assert.Contains("ClaimTypes.NameIdentifier", source);
            Assert.Contains("ProductReadWorkspaceAuthorizationRules.EnsureAuthorizedAsync", source);
            Assert.DoesNotContain("AllowAnonymous", source);
            Assert.DoesNotContain("[Authorize(Roles", source);
            Assert.DoesNotContain("RequiresLiveAuthorization", source);
            Assert.DoesNotContain("FromSql", source);
            Assert.DoesNotContain("OssStorageService", source);

            // 可执行代码（去掉 XML 文档注释行）：既不写库，也绝不引用请求路径 / 环境作为放行条件。
            var code = string.Join("\n", source.Split('\n')
                .Where(line => !line.TrimStart().StartsWith("///")));
            Assert.DoesNotContain("SaveChanges", code);
            Assert.DoesNotContain("Request.Path", code);
            Assert.DoesNotContain("IsDevelopment", code);
            Assert.DoesNotContain("Environment.", code);
        }

        // 路由模板保持既有契约不变。
        Assert.Equal("api/base/product-images", typeof(ProductImageLibraryController)
            .GetCustomAttributes(typeof(RouteAttribute), false).Cast<RouteAttribute>().Single().Template);
        Assert.Equal("api/base/products/export-field-completeness",
            typeof(ProductExportFieldCompletenessController)
                .GetCustomAttributes(typeof(RouteAttribute), false).Cast<RouteAttribute>().Single().Template);

        // ERP-452 的商品资料契约不变：仍然复用 ProductAuthorizationRules。
        var masterSource = File.ReadAllText(RepoFile(
            "src", "ERP.Api", "Controllers", "BaseDataControllers.cs"));
        Assert.Contains("ProductAuthorizationRules.EnsureAuthorizedAsync", masterSource);
    }
}

