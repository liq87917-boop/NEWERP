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
/// ERP-530 商品资料（<c>api/base/products</c>）价格 / 尺寸 / 重量 / 体积 / 退税率 / 安全库存等**全部**持久化
/// decimal 列的**实际存储精度**（<c>DECIMAL(18,4)</c>）边界单元测试（内存库 + 真实控制器 + 真实已认证身份）：
/// <list type="number">
/// <item>实际 EF 列精度与规则常量同源：16 个 decimal 列显式 <c>decimal(18,4)</c>
/// （仅以 SQL Server 提供程序<b>只构建模型</b>，不连接数据库）；</item>
/// <item>纯规则：四位小数与量级边界的逐位原样可表示判定（恰好上限放行、第 5 位小数与量级溢出拒绝、
/// 「半进位取整后恰好落到上限」的过小数位值仍拒绝、「当前已接受的边界值」继续放行）；</item>
/// <item><see cref="ProductAuthorizationRules.Validate"/>：<b>每一个</b> decimal 列的第 5 位小数 / 量级溢出都以既有
/// 受控参数错误拒绝，且逐字段不变（绝不取整、截断或改写）；</item>
/// <item>真实控制器新增 / 修改：不可表示的值零 <c>BaseProducts</c> 行、零改写，合法 4 位小数原样往返；</item>
/// <item>源码契约：EF 映射为全部 16 列声明 <c>HasPrecision(18,4)</c>，且不新增任何索引 / DDL。</item>
/// </list>
/// <para>内存库不施加列精度，因此「真实列上原样往返 / 越界被真实列拒绝」由
/// <c>ERP.IntegrationTests/ProductMasterPersistedNumericBoundsSqlServerTests.cs</c> 在专用 localdb 上验证。</para>
/// </summary>
public class ProductMasterPersistedNumericBoundsTests
{
    /// <summary>商品数值列的有效位数（<c>DECIMAL(18,4)</c>，与建表脚本同源）。</summary>
    private const int DecimalPrecision = 18;

    /// <summary><c>DECIMAL(18,4)</c> 的最大可表示值（与规则常量同源）。</summary>
    private static readonly decimal Max4 = ProductAuthorizationRules.MaxDecimalMagnitude;

    /// <summary>商品全部持久化 decimal 列（属性名 + 中文标签，与实体 / 建表脚本逐一对应）。</summary>
    private static readonly (string Property, string Label)[] DecimalColumns =
    {
        (nameof(BaseProduct.PurchasePrice), "采购价"),
        (nameof(BaseProduct.SalePrice), "销售价"),
        (nameof(BaseProduct.CostPrice), "成本价"),
        (nameof(BaseProduct.Weight), "单件毛重"),
        (nameof(BaseProduct.Volume), "单件体积"),
        (nameof(BaseProduct.Length), "长"),
        (nameof(BaseProduct.Width), "宽"),
        (nameof(BaseProduct.Height), "高"),
        (nameof(BaseProduct.OuterLength), "外箱长"),
        (nameof(BaseProduct.OuterWidth), "外箱宽"),
        (nameof(BaseProduct.OuterHeight), "外箱高"),
        (nameof(BaseProduct.OuterWeight), "外箱毛重"),
        (nameof(BaseProduct.VolumeWeight), "体积重"),
        (nameof(BaseProduct.RefundRate), "出口退税率"),
        (nameof(BaseProduct.MinStock), "安全库存"),
        (nameof(BaseProduct.MaxStock), "库存上限"),
    };

    /// <summary>16 个 decimal 列名（供逐列 <c>[Theory]</c> 使用）。</summary>
    public static IEnumerable<object[]> DecimalColumnNames()
        => DecimalColumns.Select(c => new object[] { c.Property });

    // ==================== 1. 实际 EF 列精度与规则常量同源（仅构建模型，不连接数据库） ====================

    [Fact]
    public void 实际EF列精度_全部商品decimal列均为18_4()
    {
        var options = new DbContextOptionsBuilder<ErpDbContext>()
            .UseSqlServer(
                "Server=(localdb)\\NEWERP_AutoAcceptance;"
                + "Database=NEWERP_AUTOTEST_ERP530_MODEL_ONLY;Integrated Security=true;TrustServerCertificate=true")
            .Options;
        using var db = new ErpDbContext(options);

        Assert.Equal(DecimalPrecision, 18);
        Assert.Equal(4, ProductAuthorizationRules.PersistedDecimalScale);
        Assert.Equal(99_999_999_999_999.9999m, Max4);

        var entityType = db.Model.FindEntityType(typeof(BaseProduct));
        Assert.NotNull(entityType);
        foreach (var (property, _) in DecimalColumns)
        {
            var mapped = entityType!.FindProperty(property);
            Assert.NotNull(mapped);
            Assert.Equal(DecimalPrecision, mapped!.GetPrecision());
            Assert.Equal(ProductAuthorizationRules.PersistedDecimalScale, mapped.GetScale());
            Assert.Equal("decimal(18,4)", mapped.GetColumnType());
        }
    }

    /// <summary>源码契约：EF 映射为全部 16 个商品 decimal 列声明 <c>HasPrecision(18, 4)</c>，且既有唯一索引不变。</summary>
    [Fact]
    public void 源码契约_EF映射为全部商品decimal列声明18_4()
    {
        var source = File.ReadAllText(RepoFile("src", "ERP.Infrastructure", "Data", "ErpDbContext.Config.cs"));

        foreach (var (property, _) in DecimalColumns)
            Assert.Contains(
                $"modelBuilder.Entity<BaseProduct>().Property(x => x.{property}).HasPrecision(18, 4);", source);

        Assert.Contains("modelBuilder.Entity<BaseProduct>().HasIndex(p => p.ProductCode).IsUnique();", source);
    }

    // ==================== 2. 可表示性边界（纯规则） ====================

    [Theory]
    [InlineData("0")]
    [InlineData("1.5")]
    [InlineData("13.0555")]
    [InlineData("0.0125")]
    [InlineData("-0.0125")]
    [InlineData("0.0001")]
    [InlineData("99999999999999.9999")]
    [InlineData("-99999999999999.9999")]
    public void 可表示_四位小数与量级边界内放行(string raw)
        => Assert.True(ProductAuthorizationRules.IsPersistedDecimalRepresentable(
            decimal.Parse(raw, System.Globalization.CultureInfo.InvariantCulture)));

    [Theory]
    [InlineData("1.00005")]                 // 第 5 位小数
    [InlineData("0.00001")]                 // 第 5 位小数
    [InlineData("13.05555")]                // 第 5 位小数
    [InlineData("99999999999999.99985")]    // 第 5 位小数：半进位取整后恰好落到上限，仍拒绝
    [InlineData("99999999999999.99995")]    // 第 5 位小数：半进位取整后越过上限，仍拒绝
    [InlineData("100000000000000")]         // 量级越界（1e14）
    [InlineData("100000000000000.0000")]    // 量级越界
    [InlineData("-100000000000000.0000")]   // 量级越界（负向）
    public void 不可表示_过小数位或量级越界(string raw)
        => Assert.False(ProductAuthorizationRules.IsPersistedDecimalRepresentable(
            decimal.Parse(raw, System.Globalization.CultureInfo.InvariantCulture)));

    // ==================== 3. Validate：逐列第 5 位小数 / 量级越界受控拒绝，字段不被改写 ====================

    [Theory]
    [MemberData(nameof(DecimalColumnNames))]
    public void 校验_每个decimal列第五位小数_受控拒绝且值不变(string property)
    {
        var product = NewProduct();

        SetDecimal(product, property, 1.00005m);
        var ex = Assert.Throws<BusinessException>(() => ProductAuthorizationRules.Validate(product));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Contains(property, ex.Message);
        Assert.Contains("DECIMAL(18,4)", ex.Message);
        Assert.Equal(1.00005m, GetDecimal(product, property));
    }

    [Theory]
    [MemberData(nameof(DecimalColumnNames))]
    public void 校验_每个decimal列量级越界_受控拒绝(string property)
    {
        var product = NewProduct();

        SetDecimal(product, property, Max4 + 0.0001m);
        var ex = Assert.Throws<BusinessException>(() => ProductAuthorizationRules.Validate(product));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Contains(property, ex.Message);
    }

    [Theory]
    [MemberData(nameof(DecimalColumnNames))]
    public void 校验_每个decimal列四位小数与上限_原样放行(string property)
    {
        var atFourDecimals = NewProduct();
        SetDecimal(atFourDecimals, property, 0.0125m);
        ProductAuthorizationRules.Validate(atFourDecimals);
        Assert.Equal(0.0125m, GetDecimal(atFourDecimals, property));

        var atLimit = NewProduct();
        SetDecimal(atLimit, property, Max4);
        ProductAuthorizationRules.Validate(atLimit);
        Assert.Equal(Max4, GetDecimal(atLimit, property));
    }

    /// <summary>当前已接受的边界值（0 / 1 位小数 / 恰好上限）继续放行，且逐字段原样保留。</summary>
    [Fact]
    public void 校验_既有可接受边界值_继续放行且不改写字段()
    {
        var product = NewProduct();
        product.PurchasePrice = 0m;
        product.SalePrice = Max4;
        product.CostPrice = 1.5m;
        product.Weight = 0.0125m;
        product.Volume = 13.0555m;
        product.Length = 120m;
        product.Width = 80.5m;
        product.Height = 60.25m;
        product.OuterLength = 0.0001m;
        product.OuterWidth = Max4;
        product.OuterHeight = 1m;
        product.OuterWeight = 2.3456m;
        product.VolumeWeight = 3.4567m;
        product.RefundRate = 100m;
        product.MinStock = 0.0001m;
        product.MaxStock = Max4;

        ProductAuthorizationRules.Validate(product);

        Assert.Equal(0m, product.PurchasePrice);
        Assert.Equal(Max4, product.SalePrice);
        Assert.Equal(0.0125m, product.Weight);
        Assert.Equal(13.0555m, product.Volume);
        Assert.Equal(0.0001m, product.OuterLength);
        Assert.Equal(Max4, product.MaxStock);
    }

    // ==================== 4. 真实控制器：不可表示值零写入 / 合法 4 位小数原样往返 ====================

    [Fact]
    public async Task 控制器_不可表示值_零写入且不改写既有行()
    {
        using var db = TestDbFactory.Create();
        var existing = SeedProduct(db, "P-PREC-EXIST", "既有商品");
        var userId = SeedUser(db);
        var ctl = Controller(db, userId);
        var before = Snapshot(db);

        // 新增：体积第 5 位小数 / 销售价量级越界 → 受控拒绝，零新行。
        var fifthPlace = NewProduct(code: "P-PREC-BAD");
        fifthPlace.Volume = 0.01255m;
        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.Create(fifthPlace));

        var overflow = NewProduct(code: "P-PREC-OVF");
        overflow.SalePrice = Max4 + 0.0001m;
        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.Create(overflow));

        // 修改：退税率第 5 位小数 → 受控拒绝，既有行逐字节不变。
        var badUpdate = NewProduct(code: "P-PREC-EXIST", name: "既有商品");
        badUpdate.RefundRate = 13.05555m;
        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.Update(existing.Id, badUpdate));

        Assert.Equal(before, Snapshot(db));
        Assert.Equal(1, await db.BaseProducts.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task 控制器_合法四位小数_原样落库往返()
    {
        using var db = TestDbFactory.Create();
        var userId = SeedUser(db);
        var ctl = Controller(db, userId);

        var payload = NewProduct(code: "P-PREC-OK", name: "精度商品");
        payload.Volume = 0.0125m;
        payload.RefundRate = 13.0555m;
        payload.SalePrice = Max4;
        payload.OuterWeight = 1.2345m;
        payload.MinStock = 0.0001m;

        var created = Data<BaseProduct>(await ctl.Create(payload));
        Assert.True(created.Id > 0);

        var stored = await db.BaseProducts.AsNoTracking().SingleAsync(x => x.Id == created.Id);
        Assert.Equal(0.0125m, stored.Volume);
        Assert.Equal(13.0555m, stored.RefundRate);
        Assert.Equal(Max4, stored.SalePrice);
        Assert.Equal(1.2345m, stored.OuterWeight);
        Assert.Equal(0.0001m, stored.MinStock);
    }

    // ==================== 5. 逐列读写辅助 ====================

    private static void SetDecimal(BaseProduct product, string property, decimal value)
    {
        switch (property)
        {
            case nameof(BaseProduct.PurchasePrice): product.PurchasePrice = value; break;
            case nameof(BaseProduct.SalePrice): product.SalePrice = value; break;
            case nameof(BaseProduct.CostPrice): product.CostPrice = value; break;
            case nameof(BaseProduct.Weight): product.Weight = value; break;
            case nameof(BaseProduct.Volume): product.Volume = value; break;
            case nameof(BaseProduct.Length): product.Length = value; break;
            case nameof(BaseProduct.Width): product.Width = value; break;
            case nameof(BaseProduct.Height): product.Height = value; break;
            case nameof(BaseProduct.OuterLength): product.OuterLength = value; break;
            case nameof(BaseProduct.OuterWidth): product.OuterWidth = value; break;
            case nameof(BaseProduct.OuterHeight): product.OuterHeight = value; break;
            case nameof(BaseProduct.OuterWeight): product.OuterWeight = value; break;
            case nameof(BaseProduct.VolumeWeight): product.VolumeWeight = value; break;
            case nameof(BaseProduct.RefundRate): product.RefundRate = value; break;
            case nameof(BaseProduct.MinStock): product.MinStock = value; break;
            case nameof(BaseProduct.MaxStock): product.MaxStock = value; break;
            default: throw new ArgumentOutOfRangeException(nameof(property), property, "未知商品 decimal 列");
        }
    }

    private static decimal GetDecimal(BaseProduct product, string property) => property switch
    {
        nameof(BaseProduct.PurchasePrice) => product.PurchasePrice,
        nameof(BaseProduct.SalePrice) => product.SalePrice,
        nameof(BaseProduct.CostPrice) => product.CostPrice,
        nameof(BaseProduct.Weight) => product.Weight,
        nameof(BaseProduct.Volume) => product.Volume,
        nameof(BaseProduct.Length) => product.Length,
        nameof(BaseProduct.Width) => product.Width,
        nameof(BaseProduct.Height) => product.Height,
        nameof(BaseProduct.OuterLength) => product.OuterLength,
        nameof(BaseProduct.OuterWidth) => product.OuterWidth,
        nameof(BaseProduct.OuterHeight) => product.OuterHeight,
        nameof(BaseProduct.OuterWeight) => product.OuterWeight,
        nameof(BaseProduct.VolumeWeight) => product.VolumeWeight,
        nameof(BaseProduct.RefundRate) => product.RefundRate,
        nameof(BaseProduct.MinStock) => product.MinStock,
        nameof(BaseProduct.MaxStock) => product.MaxStock,
        _ => throw new ArgumentOutOfRangeException(nameof(property), property, "未知商品 decimal 列")
    };

    // ==================== 6. 测试脚手架（真实控制器 + 真实已认证身份） ====================

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
        http.Request.Path = "/api/base/products";
        return new ControllerContext { HttpContext = http };
    }

    /// <summary>播种一个具备既有「商品资料」菜单授权的独立启用身份，返回用户 Id（每个用例独立）</summary>
    private static long SeedUser(ErpDbContext db)
    {
        var user = new SysUser
        {
            UserName = $"product-prec-{Guid.NewGuid():N}",
            DisplayName = "商品精度用例账号",
            PasswordHash = "hash",
            PasswordSalt = "salt",
            Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        db.SaveChanges();

        var role = new SysRole
        {
            RoleName = "商品精度用例角色",
            RoleCode = $"ProductPrecCase-{Guid.NewGuid():N}",
            IsSystem = false
        };
        db.SysRoles.Add(role);
        db.SaveChanges();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        db.SaveChanges();

        var menu = db.SysMenus.FirstOrDefault(
            m => m.MenuCode == ProductAuthorizationRules.RequiredMenuCode && !m.IsDeleted);
        if (menu is null)
        {
            menu = new SysMenu
            {
                MenuCode = ProductAuthorizationRules.RequiredMenuCode,
                MenuName = ProductAuthorizationRules.RequiredMenuText,
                MenuType = MenuType.Menu
            };
            db.SysMenus.Add(menu);
            db.SaveChanges();
        }
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
        db.SaveChanges();
        return user.Id;
    }

    private static BaseProduct SeedProduct(ErpDbContext db, string code, string name)
    {
        var product = new BaseProduct { ProductCode = code, ProductName = name, Status = 1 };
        db.BaseProducts.Add(product);
        db.SaveChanges();
        return product;
    }

    private static BaseProduct NewProduct(string code = "P-NEW", string name = "新商品", int status = 1)
        => new() { ProductCode = code, ProductName = name, Status = status };

    /// <summary>商品主表快照（含全部 decimal 列；拒绝后必须逐字节不变）</summary>
    private static string Snapshot(ErpDbContext db) => string.Join("|",
        db.BaseProducts.AsNoTracking().OrderBy(x => x.Id)
            .Select(x => $"{x.Id}:{x.Status}:{x.IsDeleted}:{x.ProductCode}:{x.ProductName}:" +
                         $"{x.PurchasePrice}:{x.SalePrice}:{x.CostPrice}:{x.Weight}:{x.Volume}:{x.Length}:" +
                         $"{x.Width}:{x.Height}:{x.OuterLength}:{x.OuterWidth}:{x.OuterHeight}:{x.OuterWeight}:" +
                         $"{x.VolumeWeight}:{x.RefundRate}:{x.MinStock}:{x.MaxStock}:{x.Remark}")
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

    /// <summary>测试用宿主环境：内容根指向 ERP.Api（本用例集不触达导出模板与 OSS）。</summary>
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
}



