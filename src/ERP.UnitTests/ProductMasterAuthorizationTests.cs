using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using ERP.Infrastructure.Export;
using ERP.Infrastructure.Storage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using System.Security.Claims;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// 商品资料（<c>api/base/products</c>）实时授权与有界字段校验单元测试（ERP-452）。覆盖：
/// <list type="number">
/// <item><b>拒绝矩阵</b>：缺失 / 禁用 / 已删除 / 缺少既有「商品资料」菜单的身份在<b>全部 10 条路由</b>
/// （分页 / 全部 / 按主键 / 新增 / 修改 / 删除 / 批量删除 / 导出 / 单张上传 / 批量上传）fail closed，
/// 且 <c>BaseProducts</c> 行逐字节不变（拒绝既不读取也不改写任何行），导出与上传在授权之前即被拒绝，
/// 因此不产出任何可下载产物、也不写入任何 OSS 对象；</item>
/// <item><b>放行</b>：具备既有「商品资料」菜单的授权身份下，既有读 / 写契约、导出契约与上传文件校验契约保持；</item>
/// <item><b>收敛</b>：请求之间撤销菜单授权后下一次请求立即拒绝（每次请求重新解析，绝不缓存）；</item>
/// <item><b>有界字段校验</b>：编码 / 名称空值、文本长度越界、decimal 数值越界、整数负数、状态非 0 / 1
/// 一律按受控参数错误拒绝且不落任何行 / 不改写任何行；</item>
/// <item><b>源码契约</b>：控制器 10 条路由全部先授权再读写，且只复用既有 <c>product</c> 菜单，不新增菜单。</item>
/// </list>
/// 全部使用内存库（TestDbFactory），不连接 SQL Server、不上传 OSS（上传走既有文件校验前置拒绝路径）、
/// 不执行任何 SQL / 部署脚本。
/// </summary>
public class ProductMasterAuthorizationTests
{
    // ==================== 0. 测试脚手架 ====================

    private static ProductController Controller(ErpDbContext db, long? userId) =>
        new(new GenericService<BaseProduct>(db), new OssStorageService(BuildOssConfiguration()),
            new ProductExcelExporter(db), new FakeWebHostEnvironment(), db)
        { ControllerContext = ContextWithUser(userId) };

    /// <summary>
    /// 带（可空）<c>NameIdentifier</c> 的真实 HTTP 路由身份上下文（<c>Request.Path</c> 已赋值）：
    /// null = 无身份，真实请求因处于请求管线内一律 fail closed 拒绝。
    /// </summary>
    private static ControllerContext ContextWithUser(long? userId)
    {
        var claims = userId.HasValue
            ? new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }
            : Array.Empty<Claim>();
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"))
        };
        // 标记为真实 HTTP 路由（Request.Path 已赋值）：缺失身份也必须实时授权并 fail closed。
        http.Request.Path = "/api/base/products";
        return new ControllerContext { HttpContext = http };
    }

    /// <summary>播种一个独立授权身份（可选状态 / 删除 / 商品菜单），返回用户 Id（每个用例独立）</summary>
    private static long SeedUser(ErpDbContext db, UserStatus status = UserStatus.Enabled, bool deleted = false,
        bool grantProductMenu = true)
    {
        var user = new SysUser
        {
            UserName = $"product-auth-{Guid.NewGuid():N}",
            DisplayName = "商品授权用例账号",
            PasswordHash = "hash",
            PasswordSalt = "salt",
            Status = status,
            IsDeleted = deleted
        };
        db.SysUsers.Add(user);
        db.SaveChanges();

        var role = new SysRole
        {
            RoleName = "商品授权用例角色",
            RoleCode = $"ProductAuthCase-{Guid.NewGuid():N}",
            IsSystem = false
        };
        db.SysRoles.Add(role);
        db.SaveChanges();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        db.SaveChanges();

        if (grantProductMenu)
            GrantMenu(db, role.Id, ProductAuthorizationRules.RequiredMenuCode, ProductAuthorizationRules.RequiredMenuText);
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

    private static BaseProduct NewProduct(
        string code = "P-NEW", string name = "新商品", int status = 1,
        string remark = "", decimal salePrice = 0m, int unitsPerPackage = 0, int minOrderQty = 0)
        => new()
        {
            ProductCode = code,
            ProductName = name,
            Status = status,
            Remark = remark,
            SalePrice = salePrice,
            UnitsPerPackage = unitsPerPackage,
            MinOrderQty = minOrderQty
        };

    /// <summary>商品主表快照（授权 / 校验拒绝后必须逐字节不变）</summary>
    private static string Snapshot(ErpDbContext db) => string.Join("|",
        db.BaseProducts.AsNoTracking().OrderBy(x => x.Id)
            .Select(x => $"{x.Id}:{x.Status}:{x.IsDeleted}:{x.ProductCode}:{x.ProductName}:{x.Spec}:{x.Unit}:" +
                         $"{x.Category}:{x.HsCode}:{x.Barcode}:{x.PurchasePrice}:{x.SalePrice}:{x.CostPrice}:" +
                         $"{x.Weight}:{x.Volume}:{x.Length}:{x.Width}:{x.Height}:{x.Image1}:{x.PackageUnit}:" +
                         $"{x.UnitsPerPackage}:{x.UnitConversion}:{x.OuterLength}:{x.OuterWidth}:{x.OuterHeight}:" +
                         $"{x.OuterWeight}:{x.VolumeWeight}:{x.EnglishDeclareName}:{x.RefundRate}:{x.Brand}:" +
                         $"{x.Certification}:{x.CustomerItemNo}:{x.FactoryItemNo}:{x.MinOrderQty}:{x.TaxIncluded}:" +
                         $"{x.MinStock}:{x.MaxStock}:{x.Remark}")
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

    /// <summary>构造一个非空、格式合法的图片上传（授权护栏正确时不会触达 OSS）。</summary>
    private static IFormFile Image(string fileName = "p.png")
        => new FakeFormFile(fileName, "image/png",
            new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x01 });

    private static IConfiguration BuildOssConfiguration() =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Oss:AccessKeyId"] = "test-key-id",
            ["Oss:AccessKeySecret"] = "test-secret",
            ["Oss:Bucket"] = "test-bucket",
            ["Oss:Endpoint"] = "oss-cn-hangzhou.aliyuncs.com"
        }).Build();

    /// <summary>按仓库根目录拼接文件的绝对路径（与其它契约测试口径一致）</summary>
    private static string RepoFile(params string[] segments)
        => Path.GetFullPath(Path.Combine(
            new[] { AppContext.BaseDirectory, "..", "..", "..", "..", ".." }.Concat(segments).ToArray()));

    /// <summary>测试用宿主环境：内容根指向 ERP.Api（导出用例只读取既有模板，不写文件、不上传 OSS）</summary>
    private sealed class FakeWebHostEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "ERP.UnitTests";
        public string EnvironmentName { get; set; } = "Development";
        public string ContentRootPath { get; set; } =
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "ERP.Api"));
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = Directory.GetCurrentDirectory();
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }

    /// <summary>测试用内存 <see cref="IFormFile"/>（授权通过后仅用于触发既有文件校验，不触达 OSS）。</summary>
    private sealed class FakeFormFile : IFormFile
    {
        private readonly byte[] _content;

        public FakeFormFile(string fileName, string contentType, byte[] content)
        {
            FileName = fileName;
            ContentType = contentType;
            _content = content;
        }

        public string ContentType { get; }
        public string ContentDisposition => $"form-data; name=\"file\"; filename=\"{FileName}\"";
        public IHeaderDictionary Headers { get; set; } = new HeaderDictionary();
        public long Length => _content.Length;
        public string Name => "file";
        public string FileName { get; }
        public void CopyTo(Stream target) => target.Write(_content, 0, _content.Length);
        public Task CopyToAsync(Stream target, CancellationToken cancellationToken = default)
            => target.WriteAsync(_content, 0, _content.Length, cancellationToken);
        public Stream OpenReadStream() => new MemoryStream(_content);
    }

    // ==================== 1. 拒绝矩阵：身份 / 账号状态 / 既有商品菜单 ====================

    /// <summary>
    /// 缺失 / 禁用 / 已删除 / 缺少既有「商品资料」菜单的身份：全部 10 条路由在读取、写入或产出任何产物之前
    /// fail closed，且商品主表逐字节不变（拒绝既不读取也不改写任何行）；导出与上传在授权之前即被拒绝，
    /// 因此不产出任何可下载产物、也不写入任何 OSS 对象。
    /// </summary>
    [Theory]
    [InlineData("missing")]
    [InlineData("disabled")]
    [InlineData("deleted")]
    [InlineData("no-menu")]
    public async Task Auth_拒绝身份_所有路由先授权且不改写任何商品(string scenario)
    {
        using var db = TestDbFactory.Create();
        var product = SeedProduct(db);

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

        await AssertCode(expectedCode, () => ctl.GetPaged(new PageQuery { Page = 1, PageSize = 10 }));
        await AssertCode(expectedCode, () => ctl.GetAll());
        await AssertCode(expectedCode, () => ctl.GetById(product.Id));
        await AssertCode(expectedCode, () => ctl.Create(NewProduct($"P-DENY-{Guid.NewGuid():N}", "被拒商品")));
        await AssertCode(expectedCode, () => ctl.Update(product.Id, NewProduct(product.ProductCode, "被拒改名")));
        await AssertCode(expectedCode, () => ctl.Delete(product.Id));
        await AssertCode(expectedCode, () => ctl.BatchDelete(new List<long> { product.Id }));

        // 导出：授权先于产物生成 → 授权错误而非模板 / IO 错误，且没有可下载产物。
        await AssertCode(expectedCode, () => ctl.Export());
        // 上传：授权先于 OSS 写入 → 即便文件格式合法也返回授权错误（证明未触达 OSS）。
        await AssertCode(expectedCode, () => ctl.Upload(Image()));
        await AssertCode(expectedCode, () => ctl.UploadBatch(new List<IFormFile> { Image() }));

        Assert.Equal(before, Snapshot(db));
        var stored = await db.BaseProducts.AsNoTracking().SingleAsync(x => x.Id == product.Id);
        Assert.Equal("P001", stored.ProductCode);
        Assert.False(stored.IsDeleted);
    }

    // ==================== 2. 授权身份：既有读 / 写 / 导出 / 上传契约放行与撤销收敛 ====================

    /// <summary>具备既有「商品资料」菜单的授权身份：既有商品读 / 写契约与响应契约照常放行。</summary>
    [Fact]
    public async Task Auth_具备既有商品菜单_放行既有读写契约()
    {
        using var db = TestDbFactory.Create();
        var userId = SeedUser(db);
        var ctl = Controller(db, userId);

        var created = Data<BaseProduct>(await ctl.Create(NewProduct(code: "P-OK", name: "授权商品")));
        Assert.True(created.Id > 0);
        Assert.Equal("P-OK", created.ProductCode);

        var page = Data<PagedResult<BaseProduct>>(await ctl.GetPaged(new PageQuery { Page = 1, PageSize = 10 }));
        Assert.Equal(1, page.Total);
        Assert.Single(Data<List<BaseProduct>>(await ctl.GetAll()));
        Assert.Equal("授权商品", Data<BaseProduct>(await ctl.GetById(created.Id)).ProductName);

        var updated = Data<BaseProduct>(await ctl.Update(created.Id, NewProduct(code: "P-OK", name: "授权商品改名")));
        Assert.Equal("授权商品改名", updated.ProductName);

        var second = Data<BaseProduct>(await ctl.Create(NewProduct(code: "P-OK2", name: "授权商品2")));
        await ctl.BatchDelete(new List<long> { second.Id });
        Assert.True((await db.BaseProducts.AsNoTracking().SingleAsync(x => x.Id == second.Id)).IsDeleted);

        await ctl.Delete(created.Id);
        Assert.True((await db.BaseProducts.AsNoTracking().SingleAsync(x => x.Id == created.Id)).IsDeleted);
    }

    /// <summary>授权身份下导出与上传沿用既有契约：导出产出可下载文件，上传空 / 非法载荷仍按参数错误拒绝。</summary>
    [Fact]
    public async Task Auth_具备既有商品菜单_导出与上传沿用既有契约()
    {
        using var db = TestDbFactory.Create();
        var userId = SeedUser(db);
        SeedProduct(db, "P-EXP", "导出商品");
        var ctl = Controller(db, userId);

        var file = Assert.IsType<FileContentResult>(await ctl.Export());
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", file.ContentType);
        Assert.True(file.FileContents.Length > 0);

        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.Upload(null!));
        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.UploadBatch(new List<IFormFile>()));
    }

    /// <summary>请求之间撤销菜单授权：下一次请求立即收敛为拒绝（每次都重新解析，绝不缓存）。</summary>
    [Fact]
    public async Task Auth_撤销菜单后_下一次请求立即收敛为拒绝()
    {
        using var db = TestDbFactory.Create();
        var product = SeedProduct(db);
        var userId = SeedUser(db);
        var ctl = Controller(db, userId);

        Data<PagedResult<BaseProduct>>(await ctl.GetPaged(new PageQuery { Page = 1, PageSize = 10 })); // 授权读取成功

        foreach (var grant in db.SysRoleMenus.ToList()) grant.IsDeleted = true;
        db.SaveChanges();

        await AssertCode(ErrorCodes.Forbidden, () => ctl.GetPaged(new PageQuery { Page = 1, PageSize = 10 }));
        await AssertCode(ErrorCodes.Forbidden, () => ctl.Delete(product.Id));
        await AssertCode(ErrorCodes.Forbidden, () => ctl.Create(NewProduct(code: "P-REVOKED")));
        await AssertCode(ErrorCodes.Forbidden, () => ctl.Export());
        await AssertCode(ErrorCodes.Forbidden, () => ctl.Upload(Image()));
        Assert.False((await db.BaseProducts.AsNoTracking().SingleAsync(x => x.Id == product.Id)).IsDeleted);
        Assert.Equal(1, await db.BaseProducts.AsNoTracking().CountAsync());
    }

    // ==================== 3. 有界字段校验：拒绝且不落 / 不改写任何行 ====================

    /// <summary>
    /// 授权身份下的非法载荷（编码 / 名称空值、文本长度越界、decimal 数值越界、整数负数、状态非已知值）：
    /// 新增与修改都按受控参数错误拒绝，且不落任何新行、不改写任何既有行。
    /// </summary>
    [Theory]
    [InlineData("empty-code")]
    [InlineData("empty-name")]
    [InlineData("code-too-long")]
    [InlineData("name-too-long")]
    [InlineData("english-name-too-long")]
    [InlineData("spec-too-long")]
    [InlineData("unit-too-long")]
    [InlineData("category-too-long")]
    [InlineData("hs-code-too-long")]
    [InlineData("barcode-too-long")]
    [InlineData("image1-too-long")]
    [InlineData("package-unit-too-long")]
    [InlineData("unit-conversion-too-long")]
    [InlineData("english-declare-too-long")]
    [InlineData("brand-too-long")]
    [InlineData("certification-too-long")]
    [InlineData("customer-item-no-too-long")]
    [InlineData("factory-item-no-too-long")]
    [InlineData("remark-too-long")]
    [InlineData("decimal-too-large")]
    [InlineData("units-per-package-negative")]
    [InlineData("min-order-qty-negative")]
    [InlineData("status-unknown")]
    public async Task Validation_非法载荷_新增与修改均拒绝且零写入(string scenario)
    {
        using var db = TestDbFactory.Create();
        var existing = SeedProduct(db, "P-EXIST", "既有商品");
        var userId = SeedUser(db);
        var ctl = Controller(db, userId);
        var before = Snapshot(db);

        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.Create(InvalidPayload(scenario)));
        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.Update(existing.Id, InvalidPayload(scenario)));

        Assert.Equal(before, Snapshot(db));
        var stored = await db.BaseProducts.AsNoTracking().SingleAsync(x => x.Id == existing.Id);
        Assert.Equal("P-EXIST", stored.ProductCode);
        Assert.Equal("既有商品", stored.ProductName);
        Assert.Equal(1, stored.Status);
    }

    /// <summary>构造指定越界字段的非法商品载荷（其余字段均在界内）。</summary>
    private static BaseProduct InvalidPayload(string scenario) => scenario switch
    {
        "empty-code" => NewProduct(code: ""),
        "empty-name" => NewProduct(name: "   "),
        "code-too-long" => NewProduct(code: new string('C', ProductAuthorizationRules.MaxProductCodeLength + 1)),
        "name-too-long" => NewProduct(name: new string('N', ProductAuthorizationRules.MaxProductNameLength + 1)),
        "english-name-too-long" => With(NewProduct(),
            p => p.EnglishName = new string('E', ProductAuthorizationRules.MaxEnglishNameLength + 1)),
        "spec-too-long" => With(NewProduct(),
            p => p.Spec = new string('S', ProductAuthorizationRules.MaxSpecLength + 1)),
        "unit-too-long" => With(NewProduct(),
            p => p.Unit = new string('U', ProductAuthorizationRules.MaxUnitLength + 1)),
        "category-too-long" => With(NewProduct(),
            p => p.Category = new string('C', ProductAuthorizationRules.MaxCategoryLength + 1)),
        "hs-code-too-long" => With(NewProduct(),
            p => p.HsCode = new string('H', ProductAuthorizationRules.MaxHsCodeLength + 1)),
        "barcode-too-long" => With(NewProduct(),
            p => p.Barcode = new string('B', ProductAuthorizationRules.MaxBarcodeLength + 1)),
        "image1-too-long" => With(NewProduct(),
            p => p.Image1 = new string('I', ProductAuthorizationRules.MaxImageUrlLength + 1)),
        "package-unit-too-long" => With(NewProduct(),
            p => p.PackageUnit = new string('K', ProductAuthorizationRules.MaxPackageUnitLength + 1)),
        "unit-conversion-too-long" => With(NewProduct(),
            p => p.UnitConversion = new string('V', ProductAuthorizationRules.MaxUnitConversionLength + 1)),
        "english-declare-too-long" => With(NewProduct(),
            p => p.EnglishDeclareName = new string('D', ProductAuthorizationRules.MaxEnglishDeclareNameLength + 1)),
        "brand-too-long" => With(NewProduct(),
            p => p.Brand = new string('G', ProductAuthorizationRules.MaxBrandLength + 1)),
        "certification-too-long" => With(NewProduct(),
            p => p.Certification = new string('T', ProductAuthorizationRules.MaxCertificationLength + 1)),
        "customer-item-no-too-long" => With(NewProduct(),
            p => p.CustomerItemNo = new string('X', ProductAuthorizationRules.MaxCustomerItemNoLength + 1)),
        "factory-item-no-too-long" => With(NewProduct(),
            p => p.FactoryItemNo = new string('Y', ProductAuthorizationRules.MaxFactoryItemNoLength + 1)),
        "remark-too-long" => NewProduct(remark: new string('R', ProductAuthorizationRules.MaxRemarkLength + 1)),
        "decimal-too-large" => NewProduct(salePrice: ProductAuthorizationRules.MaxDecimalMagnitude + 1m),
        "units-per-package-negative" => NewProduct(unitsPerPackage: -1),
        "min-order-qty-negative" => NewProduct(minOrderQty: -1),
        _ => NewProduct(status: 2)
    };

    private static BaseProduct With(BaseProduct product, Action<BaseProduct> mutate)
    {
        mutate(product);
        return product;
    }

    /// <summary>边界值必须放行：编码 / 名称恰好等于持久化长度上限、备注等于上限、decimal 等于上限、状态 0 / 1 都接受。</summary>
    [Fact]
    public async Task Validation_边界值放行_超一位即拒绝()
    {
        using var db = TestDbFactory.Create();
        var userId = SeedUser(db);
        var ctl = Controller(db, userId);

        var codeAtLimit = new string('C', ProductAuthorizationRules.MaxProductCodeLength);
        var nameAtLimit = new string('N', ProductAuthorizationRules.MaxProductNameLength);
        var created = Data<BaseProduct>(await ctl.Create(NewProduct(
            code: codeAtLimit,
            name: nameAtLimit,
            status: ProductAuthorizationRules.DisabledStatus,
            remark: new string('R', ProductAuthorizationRules.MaxRemarkLength),
            salePrice: ProductAuthorizationRules.MaxDecimalMagnitude,
            unitsPerPackage: 0,
            minOrderQty: 0)));
        Assert.Equal(codeAtLimit, created.ProductCode);
        Assert.Equal(nameAtLimit, created.ProductName);
        Assert.Equal(ProductAuthorizationRules.DisabledStatus, created.Status);
        Assert.Equal(ProductAuthorizationRules.MaxDecimalMagnitude, created.SalePrice);

        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.Create(NewProduct(code: codeAtLimit + "C", name: "超长编码")));
        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.Create(NewProduct(code: "P-2", name: nameAtLimit + "N")));
        Assert.Equal(1, await db.BaseProducts.AsNoTracking().CountAsync());
    }

    // ==================== 4. 源码与菜单契约 ====================

    /// <summary>控制器源码契约：10 条路由全部先经实时授权再读写 / 产物生成，新增与修改另经有界字段校验。</summary>
    [Fact]
    public void Auth_控制器源码契约_所有路由先授权再读写()
    {
        var source = File.ReadAllText(RepoFile("src", "ERP.Api", "Controllers", "BaseDataControllers.cs"));
        Assert.Contains("ClaimTypes.NameIdentifier", source);
        Assert.Equal(10, System.Text.RegularExpressions.Regex.Matches(
            source, @"await EnsureProductAuthorizedAsync\(\);").Count);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(
            source, @"ProductAuthorizationRules\.Validate\(").Count);
        Assert.DoesNotContain("AllowAnonymous", source);
        Assert.DoesNotContain("[Authorize(Roles", source);
    }

    /// <summary>授权口径复用既有「商品资料」菜单（与 SeedData.Menus 同源），不新增任何菜单。</summary>
    [Fact]
    public void Auth_复用既有商品菜单常量_不新增菜单()
    {
        Assert.Equal("product", ProductAuthorizationRules.RequiredMenuCode);
        Assert.Equal("商品资料", ProductAuthorizationRules.RequiredMenuText);

        var menus = File.ReadAllText(RepoFile("src", "ERP.Infrastructure", "Data", "SeedData.Menus.cs"));
        Assert.Contains(
            $"(\"base\", \"{ProductAuthorizationRules.RequiredMenuCode}\", \"{ProductAuthorizationRules.RequiredMenuText}\"",
            menus);
    }

    /// <summary>
    /// ERP-461：商品资料页工具栏打开的<b>并列</b>只读工作台（图片库 / 出口字段完整度）复用<b>同一</b>既有
    /// 「商品资料」菜单授权，因此 <c>api/base/products</c> 的 ERP-452 契约保持不变，且不新增任何菜单。
    /// </summary>
    [Fact]
    public void Auth_并列只读工作台复用同一商品菜单_不新增菜单()
    {
        Assert.Equal(ProductAuthorizationRules.RequiredMenuCode, ProductReadWorkspaceAuthorizationRules.RequiredMenuCode);
        Assert.Equal(ProductAuthorizationRules.RequiredMenuText, ProductReadWorkspaceAuthorizationRules.RequiredMenuText);

        var menus = File.ReadAllText(RepoFile("src", "ERP.Infrastructure", "Data", "SeedData.Menus.cs"));
        Assert.Contains(
            $"(\"base\", \"{ProductReadWorkspaceAuthorizationRules.RequiredMenuCode}\", \"{ProductReadWorkspaceAuthorizationRules.RequiredMenuText}\"",
            menus);
    }
}




