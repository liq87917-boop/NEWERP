using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// 商品规格变体（颜色 / 尺码 SKU，ERP-037）实时授权与权威引用守卫单元测试（ERP-444）。覆盖：
/// <list type="number">
/// <item><b>拒绝矩阵</b>：缺失 / 禁用 / 已删除 / 缺少既有「商品资料」菜单 / 无菜单的身份在
/// <b>全部 7 条路由</b>（list / options / create / update / disable / enable / delete）fail closed，
/// 且 <c>BaseProductVariants</c> 子表逐字节不变（拒绝不落任何规格或状态变更）；</item>
/// <item><b>放行</b>：具备既有「商品资料」菜单的授权身份下，既有读 / 写契约保持；</item>
/// <item><b>收敛</b>：请求之间撤销菜单授权后下一次请求立即拒绝（每次请求重新解析，绝不缓存）；</item>
/// <item><b>权威引用 / 载荷</b>：授权通过后，外部 / 已删除商品、已停用商品与非法载荷仍 fail closed 且零写入；</item>
/// <item><b>源码契约</b>：控制器 7 条路由全部先授权再读写，且只复用既有 <c>product</c> 菜单，不新增菜单。</item>
/// </list>
/// 全部使用内存库（TestDbFactory），不连接 SQL Server、不执行任何 SQL / 部署脚本。
/// </summary>
public class ProductVariantAuthorizationTests
{
    // ==================== 0. 测试脚手架 ====================

    private static ProductVariantController Controller(ErpDbContext db, long? userId) =>
        new(db) { ControllerContext = ContextWithUser(userId) };

    /// <summary>带（可空）<c>NameIdentifier</c> 的 HTTP 身份上下文；null = 无身份，由授权护栏 fail closed 拒绝</summary>
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
            UserName = $"variant-{Guid.NewGuid():N}",
            DisplayName = "规格变体授权用例账号",
            PasswordHash = "hash",
            PasswordSalt = "salt",
            Status = status,
            IsDeleted = deleted
        };
        db.SysUsers.Add(user);
        db.SaveChanges();

        var role = new SysRole
        {
            RoleName = "规格变体授权用例角色",
            RoleCode = $"VariantCase-{Guid.NewGuid():N}",
            IsSystem = false
        };
        db.SysRoles.Add(role);
        db.SaveChanges();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        db.SaveChanges();

        if (grantProductMenu)
            GrantMenu(db, role.Id, ProductVariantAuthorizationRules.ProductMenuCode, ProductVariantAuthorizationRules.ProductMenuText);
        return user.Id;
    }

    /// <summary>按既有菜单编码授予角色访问权限（幂等；菜单缺失时按既有种子口径补建一条功能菜单）</summary>
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

    private static BaseProduct SeedProduct(
        ErpDbContext db, string code = "P001", string name = "保温杯", int status = 1, bool deleted = false)
    {
        var product = new BaseProduct { ProductCode = code, ProductName = name, Status = status, IsDeleted = deleted };
        db.BaseProducts.Add(product);
        db.SaveChanges();
        return product;
    }

    private static BaseProductVariant SeedVariant(
        ErpDbContext db, long productId, string code, string color = "", string size = "",
        int status = 1, bool deleted = false)
    {
        var variant = new BaseProductVariant
        {
            ProductId = productId,
            VariantCode = ProductVariantRules.NormalizeCode(code),
            Color = ProductVariantRules.DisplayValue(color),
            Size = ProductVariantRules.DisplayValue(size),
            ColorSizeKey = ProductVariantRules.BuildColorSizeKey(color, size),
            Status = status,
            IsDeleted = deleted
        };
        db.BaseProductVariants.Add(variant);
        db.SaveChanges();
        return variant;
    }

    /// <summary>规格子表快照（授权 / 引用拒绝后必须逐字节不变）</summary>
    private static string Snapshot(ErpDbContext db) => string.Join("|",
        db.BaseProductVariants.AsNoTracking().OrderBy(x => x.Id)
            .Select(x => $"{x.Id}:{x.Status}:{x.IsDeleted}:{x.ProductId}:{x.VariantCode}:{x.ColorSizeKey}")
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

    private static ProductVariantSaveDto Save(string code = "RED-XL", string color = "红色", string size = "XL",
        int? status = null)
        => new() { VariantCode = code, Color = color, Size = size, Status = status };

    // ==================== 1. 拒绝矩阵：身份 / 账号状态 / 既有商品菜单 ====================

    /// <summary>
    /// 缺失 / 禁用 / 已删除 / 缺少既有「商品资料」菜单 / 无任何菜单的身份：全部 7 条路由
    /// 在读取或写入任何规格之前 fail closed，且规格子表逐字节不变（拒绝不落任何规格或状态变更）。
    /// </summary>
    [Theory]
    [InlineData("missing")]
    [InlineData("disabled")]
    [InlineData("deleted")]
    [InlineData("no-product-menu")]
    [InlineData("no-menu")]
    public async Task Auth_拒绝身份_所有路由先授权且不改写任何规格(string scenario)
    {
        using var db = TestDbFactory.Create();
        var product = SeedProduct(db);
        var variant = SeedVariant(db, product.Id, "RED-XL", "红色", "XL");

        long? userId = scenario switch
        {
            "missing" => null,
            "disabled" => SeedUser(db, UserStatus.Disabled),
            "deleted" => SeedUser(db, deleted: true),
            _ => SeedUser(db, grantProductMenu: false)
        };
        var expectedCode = scenario switch
        {
            "missing" or "deleted" => ErrorCodes.Unauthorized,
            _ => ErrorCodes.Forbidden
        };

        var ctl = Controller(db, userId);
        var before = Snapshot(db);

        await AssertCode(expectedCode, () => ctl.List(product.Id));
        await AssertCode(expectedCode, () => ctl.List(product.Id, activeOnly: true));
        await AssertCode(expectedCode, () => ctl.Options(product.Id));
        await AssertCode(expectedCode, () => ctl.Create(product.Id, Save(color: "蓝色", size: "L")));
        await AssertCode(expectedCode, () => ctl.Update(product.Id, variant.Id, Save(color: "蓝色", size: "L")));
        await AssertCode(expectedCode, () => ctl.Disable(product.Id, variant.Id));
        await AssertCode(expectedCode, () => ctl.Enable(product.Id, variant.Id));
        await AssertCode(expectedCode, () => ctl.Delete(product.Id, variant.Id));

        Assert.Equal(before, Snapshot(db));
        var stored = db.BaseProductVariants.AsNoTracking().Single();
        Assert.Equal(ProductVariantRules.ActiveStatus, stored.Status);
        Assert.False(stored.IsDeleted);
        Assert.Equal("RED-XL", stored.VariantCode);
    }

    // ==================== 2. 授权身份：既有读 / 写契约放行与撤销收敛 ====================

    /// <summary>具备既有「商品资料」菜单的授权身份：既有规格读 / 写契约照常放行。</summary>
    [Fact]
    public async Task Auth_具备既有商品菜单_放行既有读写契约()
    {
        using var db = TestDbFactory.Create();
        var product = SeedProduct(db);
        var userId = SeedUser(db);
        var ctl = Controller(db, userId);

        var created = Data<ProductVariantDto>(await ctl.Create(product.Id, Save(code: "RED-XL", color: "红色", size: "XL")));
        Assert.Equal("RED-XL", created.VariantCode);
        Assert.Single(Data<List<ProductVariantDto>>(await ctl.List(product.Id)));
        Assert.Single(Data<List<ProductVariantDto>>(await ctl.Options(product.Id)));

        var updated = Data<ProductVariantDto>(await ctl.Update(product.Id, created.Id, Save(code: "RED-L", color: "红色", size: "L")));
        Assert.Equal("RED-L", updated.VariantCode);

        Assert.Equal(ProductVariantRules.DisabledStatus, Data<ProductVariantDto>(await ctl.Disable(product.Id, created.Id)).Status);
        Assert.Equal(ProductVariantRules.ActiveStatus, Data<ProductVariantDto>(await ctl.Enable(product.Id, created.Id)).Status);

        await ctl.Delete(product.Id, created.Id);
        Assert.True(db.BaseProductVariants.AsNoTracking().Single(x => x.Id == created.Id).IsDeleted);
    }

    /// <summary>请求之间撤销菜单授权：下一次请求立即收敛为拒绝（每次都重新解析，绝不缓存）。</summary>
    [Fact]
    public async Task Auth_撤销菜单后_下一次请求立即收敛为拒绝()
    {
        using var db = TestDbFactory.Create();
        var product = SeedProduct(db);
        var variant = SeedVariant(db, product.Id, "RED-XL", "红色", "XL");
        var userId = SeedUser(db);
        var ctl = Controller(db, userId);

        Data<List<ProductVariantDto>>(await ctl.List(product.Id));       // 授权读取成功

        foreach (var grant in db.SysRoleMenus.ToList()) grant.IsDeleted = true;
        db.SaveChanges();

        await AssertCode(ErrorCodes.Forbidden, () => ctl.List(product.Id));
        await AssertCode(ErrorCodes.Forbidden, () => ctl.Delete(product.Id, variant.Id));
        Assert.False(db.BaseProductVariants.AsNoTracking().Single(x => x.Id == variant.Id).IsDeleted);
    }

    // ==================== 3. 授权后权威引用与载荷仍 fail closed 且零写入 ====================

    /// <summary>
    /// 授权通过后，权威引用 / 载荷校验仍然 fail closed：外部商品、已删除商品、已停用商品与
    /// 非法编码 / 颜色尺码 / 状态一律拒绝且不落任何规格行或状态变更（ERP-037 语义保持不变）。
    /// </summary>
    [Fact]
    public async Task Auth_授权身份下_外部已删除停用商品与非法载荷仍拒绝且零写入()
    {
        using var db = TestDbFactory.Create();
        var product = SeedProduct(db, "P001", "启用商品");
        var deletedProduct = SeedProduct(db, "P002", "已删除商品", deleted: true);
        var disabledProduct = SeedProduct(db, "P003", "停用商品", status: 0);
        var variant = SeedVariant(db, product.Id, "RED-XL", "红色", "XL");
        var userId = SeedUser(db);
        var ctl = Controller(db, userId);
        var before = Snapshot(db);

        const long foreignProductId = 987654321L;

        // 外部 / 已删除商品：读取与写入都按「不存在」拒绝
        await AssertCode(ErrorCodes.NotFound, () => ctl.List(foreignProductId));
        await AssertCode(ErrorCodes.NotFound, () => ctl.Options(foreignProductId));
        await AssertCode(ErrorCodes.NotFound, () => ctl.Create(foreignProductId, Save()));
        await AssertCode(ErrorCodes.NotFound, () => ctl.Delete(deletedProduct.Id, variant.Id));

        // 已停用商品：fail closed（读写一律拒绝）
        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.List(disabledProduct.Id));
        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.Create(disabledProduct.Id, Save()));

        // 非法载荷：编码含非法字符 / 颜色与尺码都为空 / 状态越界
        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.Create(product.Id, Save(code: "RED;DROP")));
        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.Create(product.Id, Save(code: "EMPTY", color: " ", size: "")));
        await AssertCode(ErrorCodes.InvalidParameter, () =>
            ctl.Update(product.Id, variant.Id, Save(code: "RED-XL", color: "红色", size: "XL", status: 9)));

        Assert.Equal(before, Snapshot(db));
        Assert.Single(db.BaseProductVariants);
    }

    // ==================== 4. 源码与菜单契约 ====================

    /// <summary>控制器源码契约：7 条路由全部先经实时授权再读写。</summary>
    [Fact]
    public void Auth_控制器源码契约_所有路由先授权再读写()
    {
        var source = File.ReadAllText(RepoFile("src", "ERP.Api", "Controllers", "ProductVariantController.cs"));
        Assert.Contains("ClaimTypes.NameIdentifier", source);
        Assert.Equal(7, System.Text.RegularExpressions.Regex.Matches(
            source, @"ProductVariantAuthorizationRules\.EnsureAuthorizedAsync\(_db, CurrentUserId\(\)\)").Count);
    }

    /// <summary>授权口径复用既有「商品资料」菜单（与 SeedData.Menus 同源），不新增任何菜单。</summary>
    [Fact]
    public void Auth_复用既有商品菜单常量_不新增菜单()
    {
        Assert.Equal("product", ProductVariantAuthorizationRules.ProductMenuCode);
        Assert.Equal("商品资料", ProductVariantAuthorizationRules.ProductMenuText);

        var menus = File.ReadAllText(RepoFile("src", "ERP.Infrastructure", "Data", "SeedData.Menus.cs"));
        Assert.Contains(
            $"(\"base\", \"{ProductVariantAuthorizationRules.ProductMenuCode}\", \"{ProductVariantAuthorizationRules.ProductMenuText}\"",
            menus);
    }

    /// <summary>按仓库根目录拼接文件的绝对路径（与其它契约测试口径一致）</summary>
    private static string RepoFile(params string[] segments)
        => Path.GetFullPath(Path.Combine(new[] { AppContext.BaseDirectory, "..", "..", "..", "..", ".." }.Concat(segments).ToArray()));
}
