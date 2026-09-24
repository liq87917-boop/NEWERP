using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using ERP.Infrastructure.Export;
using ERP.Infrastructure.Storage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// 商品规格（颜色 / 尺码 SKU 变体）单元测试（ERP-037）。覆盖：
/// 新增 / 修改 / 停用 / 启用 / 删除、编码与「颜色 + 尺码」组合的重复保护、颜色或尺码至少一项、
/// 有界维护视图与条数上限、停用规格的可读性与「不可再被新选中」、无规格历史商品的兼容与计数标注，
/// 以及「规格维护不改写历史单据行 / 库存 / 库存流水 / 商品本身」的边界与幂等结构契约。
/// 全部使用内存库（TestDbFactory），不连接 SQL Server、不执行任何 SQL / 部署脚本。
/// </summary>
public class ProductVariantTests
{
    // ==================== 0. 测试脚手架 ====================

    private const long Warehouse1 = 910001L;

    private static ProductVariantController VariantController(ErpDbContext db) => new(db);

    /// <summary>商品资料控制器（补充规格计数标注），依赖与生产一致：内存库 + 测试用 OSS 配置 + 假宿主环境</summary>
    private static ProductController ProductControllerOf(ErpDbContext db) => new(
        new GenericService<BaseProduct>(db),
        new OssStorageService(BuildOssConfiguration()),
        new ProductExcelExporter(db),
        new FakeWebHostEnvironment(),
        db);

    private static IConfiguration BuildOssConfiguration() =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Oss:AccessKeyId"] = "test-key-id",
            ["Oss:AccessKeySecret"] = "test-secret",
            ["Oss:Bucket"] = "test-bucket",
            ["Oss:Endpoint"] = "oss-cn-hangzhou.aliyuncs.com"
        }).Build();

    private static BaseProduct SeedProduct(ErpDbContext db, string code = "P001", string name = "保温杯", bool deleted = false)
    {
        var product = new BaseProduct { ProductCode = code, ProductName = name, IsDeleted = deleted };
        db.BaseProducts.Add(product);
        db.SaveChanges();
        return product;
    }

    private static BaseProductVariant SeedVariant(
        ErpDbContext db, long productId, string code, string color = "", string size = "",
        int status = 1, bool deleted = false, int sortOrder = 0)
    {
        var variant = new BaseProductVariant
        {
            ProductId = productId,
            VariantCode = ProductVariantRules.NormalizeCode(code),
            Color = ProductVariantRules.DisplayValue(color),
            Size = ProductVariantRules.DisplayValue(size),
            ColorSizeKey = ProductVariantRules.BuildColorSizeKey(color, size),
            Status = status,
            SortOrder = sortOrder,
            IsDeleted = deleted
        };
        db.BaseProductVariants.Add(variant);
        db.SaveChanges();
        return variant;
    }

    private static T Data<T>(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<ApiResponse<T>>(ok.Value);
        return response.Data!;
    }

    /// <summary>测试用宿主环境：内容根指向仓库目录，仅用于构造 ProductController（不会读写文件）</summary>
    private sealed class FakeWebHostEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "ERP.UnitTests";
        public string EnvironmentName { get; set; } = "Development";
        public string ContentRootPath { get; set; } = Directory.GetCurrentDirectory();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = Directory.GetCurrentDirectory();
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }

    // ==================== 1. 新增与规范化 / 字段校验 ====================

    [Fact]
    public async Task Create_颜色与尺码_服务端规范化后落库()
    {
        using var db = TestDbFactory.Create();
        var product = SeedProduct(db, "P001", "保温杯");
        var ctl = VariantController(db);

        var data = Data<ProductVariantDto>(await ctl.Create(product.Id, new ProductVariantSaveDto
        {
            VariantCode = "  red-xl  ",      // 客户端随手前后带空白 / 小写
            Color = " 红色 ",
            Size = "XL"
        }));

        Assert.Equal("RED-XL", data.VariantCode);           // 编码规范化：去首尾空白 + 大写
        Assert.Equal("红色", data.Color);                    // 展示值只去首尾空白，保留原大小写
        Assert.Equal("XL", data.Size);
        Assert.Equal("红色|XL", data.ColorSizeKey);          // 归一化组合键由服务端推导
        Assert.Equal("红色 / XL", data.VariantName);
        Assert.Equal(1, data.Status);
        Assert.True(data.Selectable);
        Assert.Equal("启用", data.StatusText);

        var stored = Assert.Single(db.BaseProductVariants);
        Assert.Equal(product.Id, stored.ProductId);
        Assert.Equal("RED-XL", stored.VariantCode);
        Assert.Equal("红色|XL", stored.ColorSizeKey);
        Assert.False(stored.IsDeleted);
    }

    [Fact]
    public async Task Create_只填颜色或只填尺码_均允许()
    {
        using var db = TestDbFactory.Create();
        var product = SeedProduct(db);
        var ctl = VariantController(db);

        var byColor = Data<ProductVariantDto>(await ctl.Create(product.Id, new ProductVariantSaveDto
        {
            VariantCode = "C-RED", Color = "红色"
        }));
        var bySize = Data<ProductVariantDto>(await ctl.Create(product.Id, new ProductVariantSaveDto
        {
            VariantCode = "C-XL", Size = "XL"
        }));

        Assert.Equal("红色|", byColor.ColorSizeKey);
        Assert.Equal("红色", byColor.VariantName);
        Assert.Equal("|XL", bySize.ColorSizeKey);
        Assert.Equal("XL", bySize.VariantName);
        Assert.Equal(2, db.BaseProductVariants.Count());
    }

    [Fact]
    public async Task Create_颜色与尺码都为空_拒绝且不落库()
    {
        using var db = TestDbFactory.Create();
        var product = SeedProduct(db);
        var ctl = VariantController(db);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Create(product.Id, new ProductVariantSaveDto
        {
            VariantCode = "ONLY-CODE", Color = "   ", Size = ""
        }));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Contains("至少填写一个", ex.Message);
        Assert.Empty(db.BaseProductVariants);
    }

    [Fact]
    public async Task Create_编码为空_拒绝()
    {
        using var db = TestDbFactory.Create();
        var product = SeedProduct(db);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            VariantController(db).Create(product.Id, new ProductVariantSaveDto { VariantCode = "  ", Color = "红色" }));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Contains("不能为空", ex.Message);
        Assert.Empty(db.BaseProductVariants);
    }

    [Fact]
    public async Task Create_编码含非法字符或超长_拒绝()
    {
        using var db = TestDbFactory.Create();
        var product = SeedProduct(db);
        var ctl = VariantController(db);

        var illegal = await Assert.ThrowsAsync<BusinessException>(() => ctl.Create(product.Id,
            new ProductVariantSaveDto { VariantCode = "RED;DROP", Color = "红色" }));
        Assert.Equal(ErrorCodes.InvalidParameter, illegal.Code);
        Assert.Contains("不支持的字符", illegal.Message);

        var tooLong = await Assert.ThrowsAsync<BusinessException>(() => ctl.Create(product.Id,
            new ProductVariantSaveDto { VariantCode = new string('A', 51), Color = "红色" }));
        Assert.Equal(ErrorCodes.InvalidParameter, tooLong.Code);
        Assert.Contains("长度不能超过", tooLong.Message);

        Assert.Empty(db.BaseProductVariants);
    }

    [Fact]
    public async Task Create_状态值非法_拒绝()
    {
        using var db = TestDbFactory.Create();
        var product = SeedProduct(db);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => VariantController(db).Create(product.Id,
            new ProductVariantSaveDto { VariantCode = "RED-XL", Color = "红色", Size = "XL", Status = 7 }));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Contains("状态", ex.Message);
        Assert.Empty(db.BaseProductVariants);
    }

    [Fact]
    public async Task Create_商品不存在或已删除_抛NotFound()
    {
        using var db = TestDbFactory.Create();
        var ctl = VariantController(db);

        var missing = await Assert.ThrowsAsync<BusinessException>(() => ctl.Create(999999L,
            new ProductVariantSaveDto { VariantCode = "RED-XL", Color = "红色" }));
        Assert.Equal(ErrorCodes.NotFound, missing.Code);

        var deleted = SeedProduct(db, "P-DEL", "已删除商品", deleted: true);
        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Create(deleted.Id,
            new ProductVariantSaveDto { VariantCode = "RED-XL", Color = "红色" }));
        Assert.Equal(ErrorCodes.NotFound, ex.Code);
        Assert.Empty(db.BaseProductVariants);
    }

    [Fact]
    public async Task Create_可直接新增停用规格_且不占用启用组合()
    {
        using var db = TestDbFactory.Create();
        var product = SeedProduct(db);
        var ctl = VariantController(db);

        var disabled = Data<ProductVariantDto>(await ctl.Create(product.Id, new ProductVariantSaveDto
        {
            VariantCode = "RED-XL", Color = "红色", Size = "XL", Status = 0
        }));

        Assert.Equal(0, disabled.Status);
        Assert.False(disabled.Selectable);
        Assert.Equal("停用", disabled.StatusText);

        // 停用行不占用组合：可以再新增一条同组合的启用规格（历史停用规格保留可读）
        var active = Data<ProductVariantDto>(await ctl.Create(product.Id, new ProductVariantSaveDto
        {
            VariantCode = "RED-XL-2", Color = "红色", Size = "XL"
        }));
        Assert.Equal(1, active.Status);
        Assert.Equal(2, db.BaseProductVariants.Count());
    }

    // ==================== 2. 重复保护（编码 / 颜色 + 尺码组合） ====================

    [Fact]
    public async Task Create_同商品内编码重复_忽略大小写与空白_拒绝()
    {
        using var db = TestDbFactory.Create();
        var product = SeedProduct(db);
        SeedVariant(db, product.Id, "RED-XL", "红色", "XL");
        var ctl = VariantController(db);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Create(product.Id, new ProductVariantSaveDto
        {
            VariantCode = "  red-xl ", Color = "蓝色", Size = "L"     // 只有编码重复
        }));

        Assert.Equal(ErrorCodes.Duplicate, ex.Code);
        Assert.Contains("已存在", ex.Message);
        Assert.Single(db.BaseProductVariants);
    }

    [Fact]
    public async Task Create_不同商品可使用相同编码_允许()
    {
        using var db = TestDbFactory.Create();
        var first = SeedProduct(db, "P001", "商品1");
        var second = SeedProduct(db, "P002", "商品2");
        SeedVariant(db, first.Id, "RED-XL", "红色", "XL");

        var data = Data<ProductVariantDto>(await VariantController(db).Create(second.Id, new ProductVariantSaveDto
        {
            VariantCode = "red-xl", Color = "红色", Size = "XL"
        }));

        Assert.Equal(second.Id, data.ProductId);
        Assert.Equal("RED-XL", data.VariantCode);
        Assert.Equal(2, db.BaseProductVariants.Count());
    }

    [Fact]
    public async Task Create_启用中颜色尺码组合重复_拒绝()
    {
        using var db = TestDbFactory.Create();
        var product = SeedProduct(db);
        SeedVariant(db, product.Id, "RED-XL", "红色", "XL");

        var ex = await Assert.ThrowsAsync<BusinessException>(() => VariantController(db).Create(product.Id,
            new ProductVariantSaveDto { VariantCode = "RED-XL-2", Color = "红色", Size = "XL" }));

        Assert.Equal(ErrorCodes.Duplicate, ex.Code);
        Assert.Contains("颜色 + 尺码", ex.Message);
        Assert.Single(db.BaseProductVariants);
    }

    [Fact]
    public async Task Create_颜色尺码仅大小写或空白不同_视为重复并拒绝()
    {
        using var db = TestDbFactory.Create();
        var product = SeedProduct(db);
        SeedVariant(db, product.Id, "RED-XL", "Red", "xl");

        var ex = await Assert.ThrowsAsync<BusinessException>(() => VariantController(db).Create(product.Id,
            new ProductVariantSaveDto { VariantCode = "RED-XL-2", Color = " RED ", Size = " XL " }));

        Assert.Equal(ErrorCodes.Duplicate, ex.Code);
        Assert.Single(db.BaseProductVariants);
    }

    [Fact]
    public async Task Create_颜色相同但尺码不同_允许()
    {
        using var db = TestDbFactory.Create();
        var product = SeedProduct(db);
        SeedVariant(db, product.Id, "RED-XL", "红色", "XL");

        var data = Data<ProductVariantDto>(await VariantController(db).Create(product.Id,
            new ProductVariantSaveDto { VariantCode = "RED-L", Color = "红色", Size = "L" }));

        Assert.Equal("红色|L", data.ColorSizeKey);
        Assert.Equal(2, db.BaseProductVariants.Count());
    }

    // ==================== 3. 修改规格 ====================

    [Fact]
    public async Task Update_修改编码颜色尺码_重新规范化并落库()
    {
        using var db = TestDbFactory.Create();
        var product = SeedProduct(db);
        var variant = SeedVariant(db, product.Id, "RED-XL", "红色", "XL");
        var ctl = VariantController(db);

        var data = Data<ProductVariantDto>(await ctl.Update(product.Id, variant.Id, new ProductVariantSaveDto
        {
            VariantCode = " blue-l ", Color = " 蓝色 ", Size = " l ", Remark = " 换色 ", SortOrder = 5
        }));

        Assert.Equal("BLUE-L", data.VariantCode);
        Assert.Equal("蓝色", data.Color);
        Assert.Equal("l", data.Size);                        // 展示值保留原大小写
        Assert.Equal("蓝色|L", data.ColorSizeKey);           // 组合键按比较口径归一化（大写）
        Assert.Equal("换色", data.Remark);
        Assert.Equal(5, data.SortOrder);
        Assert.Equal(1, data.Status);        // 未提交状态时保持原状态
        Assert.Equal(variant.Id, data.Id);   // 同一行被更新，没有新增行
        Assert.Single(db.BaseProductVariants);
    }

    [Fact]
    public async Task Update_自身编码不必视为重复_允许原样保存()
    {
        using var db = TestDbFactory.Create();
        var product = SeedProduct(db);
        var variant = SeedVariant(db, product.Id, "RED-XL", "红色", "XL");

        var data = Data<ProductVariantDto>(await VariantController(db).Update(product.Id, variant.Id,
            new ProductVariantSaveDto { VariantCode = "red-xl", Color = "红色", Size = "XL" }));

        Assert.Equal("RED-XL", data.VariantCode);
        Assert.Single(db.BaseProductVariants);
    }

    [Fact]
    public async Task Update_改成同商品其它规格的编码_拒绝且不改动数据()
    {
        using var db = TestDbFactory.Create();
        var product = SeedProduct(db);
        SeedVariant(db, product.Id, "RED-XL", "红色", "XL");
        var target = SeedVariant(db, product.Id, "BLUE-L", "蓝色", "L");

        var ex = await Assert.ThrowsAsync<BusinessException>(() => VariantController(db).Update(product.Id, target.Id,
            new ProductVariantSaveDto { VariantCode = " red-xl ", Color = "蓝色", Size = "L" }));

        Assert.Equal(ErrorCodes.Duplicate, ex.Code);
        var stored = db.BaseProductVariants.Single(v => v.Id == target.Id);
        Assert.Equal("BLUE-L", stored.VariantCode);      // 拒绝后原值不变
        Assert.Equal("蓝色|L", stored.ColorSizeKey);
    }

    [Fact]
    public async Task Update_改成与其它启用规格相同的颜色尺码_拒绝()
    {
        using var db = TestDbFactory.Create();
        var product = SeedProduct(db);
        SeedVariant(db, product.Id, "RED-XL", "红色", "XL");
        var target = SeedVariant(db, product.Id, "BLUE-L", "蓝色", "L");

        var ex = await Assert.ThrowsAsync<BusinessException>(() => VariantController(db).Update(product.Id, target.Id,
            new ProductVariantSaveDto { VariantCode = "BLUE-L", Color = "红色", Size = "XL" }));

        Assert.Equal(ErrorCodes.Duplicate, ex.Code);
        Assert.Equal("蓝色|L", db.BaseProductVariants.Single(v => v.Id == target.Id).ColorSizeKey);
    }

    [Fact]
    public async Task Update_提交停用状态_生效且不再可选()
    {
        using var db = TestDbFactory.Create();
        var product = SeedProduct(db);
        var variant = SeedVariant(db, product.Id, "RED-XL", "红色", "XL");

        var data = Data<ProductVariantDto>(await VariantController(db).Update(product.Id, variant.Id,
            new ProductVariantSaveDto { VariantCode = "RED-XL", Color = "红色", Size = "XL", Status = 0 }));

        Assert.Equal(0, data.Status);
        Assert.False(data.Selectable);
    }

    [Fact]
    public async Task Update_规格不属于该商品_抛NotFound()
    {
        using var db = TestDbFactory.Create();
        var first = SeedProduct(db, "P001", "商品1");
        var second = SeedProduct(db, "P002", "商品2");
        var variant = SeedVariant(db, first.Id, "RED-XL", "红色", "XL");

        var ex = await Assert.ThrowsAsync<BusinessException>(() => VariantController(db).Update(second.Id, variant.Id,
            new ProductVariantSaveDto { VariantCode = "RED-XL", Color = "红色", Size = "XL" }));

        Assert.Equal(ErrorCodes.NotFound, ex.Code);
    }

    // ==================== 4. 停用 / 启用 / 删除 与「不可再被新选中」 ====================

    [Fact]
    public async Task Disable_停用后明细仍可读但标注不可选_且不再出现在可选用视图中()
    {
        using var db = TestDbFactory.Create();
        var product = SeedProduct(db);
        var variant = SeedVariant(db, product.Id, "RED-XL", "红色", "XL");
        SeedVariant(db, product.Id, "BLUE-L", "蓝色", "L");
        var ctl = VariantController(db);

        var disabled = Data<ProductVariantDto>(await ctl.Disable(product.Id, variant.Id));
        Assert.Equal(0, disabled.Status);
        Assert.False(disabled.Selectable);

        // 明细视图：停用规格照常返回（历史可读），并带有可识别标注
        var all = Data<List<ProductVariantDto>>(await ctl.List(product.Id));
        Assert.Equal(2, all.Count);
        var stillReadable = all.Single(v => v.Id == variant.Id);
        Assert.Equal("RED-XL", stillReadable.VariantCode);
        Assert.Equal("红色", stillReadable.Color);
        Assert.False(stillReadable.Selectable);
        Assert.Equal("红色 / XL" + ProductVariantRules.UnavailableMark,
            ProductVariantRules.MarkUnavailable(stillReadable.VariantName));
        Assert.Contains("不可再被新选中", ProductVariantRules.UnavailableMark);

        // 可选用视图：停用规格不出现，无法被新选用
        var selectable = Data<List<ProductVariantDto>>(await ctl.Options(product.Id));
        Assert.Single(selectable);
        Assert.Equal("BLUE-L", selectable[0].VariantCode);

        // 带 activeOnly 的明细视图口径一致
        var activeOnly = Data<List<ProductVariantDto>>(await ctl.List(product.Id, activeOnly: true));
        Assert.Single(activeOnly);
        Assert.Equal("BLUE-L", activeOnly[0].VariantCode);
    }

    [Fact]
    public async Task Disable_只改状态_不删除行也不改动其它规格()
    {
        using var db = TestDbFactory.Create();
        var product = SeedProduct(db);
        var variant = SeedVariant(db, product.Id, "RED-XL", "红色", "XL");
        var other = SeedVariant(db, product.Id, "BLUE-L", "蓝色", "L");

        await VariantController(db).Disable(product.Id, variant.Id);

        var stored = db.BaseProductVariants.Single(v => v.Id == variant.Id);
        Assert.Equal(0, stored.Status);
        Assert.False(stored.IsDeleted);                    // 停用不等于删除：行保留
        Assert.Equal("RED-XL", stored.VariantCode);
        Assert.Equal(1, db.BaseProductVariants.Single(v => v.Id == other.Id).Status);
    }

    [Fact]
    public async Task Enable_无组合冲突时_恢复可选用()
    {
        using var db = TestDbFactory.Create();
        var product = SeedProduct(db);
        var variant = SeedVariant(db, product.Id, "RED-XL", "红色", "XL", status: 0);
        var ctl = VariantController(db);

        var enabled = Data<ProductVariantDto>(await ctl.Enable(product.Id, variant.Id));

        Assert.Equal(1, enabled.Status);
        Assert.True(enabled.Selectable);
        Assert.Single(Data<List<ProductVariantDto>>(await ctl.Options(product.Id)));
    }

    [Fact]
    public async Task Enable_已存在同组合的启用规格_拒绝()
    {
        using var db = TestDbFactory.Create();
        var product = SeedProduct(db);
        SeedVariant(db, product.Id, "RED-XL", "红色", "XL");                    // 启用中
        var disabled = SeedVariant(db, product.Id, "RED-XL-OLD", "红色", "XL", status: 0);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            VariantController(db).Enable(product.Id, disabled.Id));

        Assert.Equal(ErrorCodes.Duplicate, ex.Code);
        Assert.Equal(0, db.BaseProductVariants.Single(v => v.Id == disabled.Id).Status);   // 拒绝后仍为停用
    }

    [Fact]
    public async Task Delete_软删除_行保留在库中且明细不再返回()
    {
        using var db = TestDbFactory.Create();
        var product = SeedProduct(db);
        var variant = SeedVariant(db, product.Id, "RED-XL", "红色", "XL");
        var ctl = VariantController(db);

        await ctl.Delete(product.Id, variant.Id);

        var stored = Assert.Single(db.BaseProductVariants);
        Assert.True(stored.IsDeleted);                       // 软删除：历史行保留可读
        Assert.Equal("RED-XL", stored.VariantCode);
        Assert.Empty(Data<List<ProductVariantDto>>(await ctl.List(product.Id)));

        // 已删除的编码不占用唯一键：可以重新新增同编码规格（历史行仍在库中）
        var recreated = Data<ProductVariantDto>(await ctl.Create(product.Id, new ProductVariantSaveDto
        {
            VariantCode = "red-xl", Color = "红色", Size = "XL"
        }));
        Assert.Equal("RED-XL", recreated.VariantCode);
        Assert.Equal(2, db.BaseProductVariants.Count());
    }

    [Fact]
    public async Task Delete_规格不存在或已删除_抛NotFound()
    {
        using var db = TestDbFactory.Create();
        var product = SeedProduct(db);
        var variant = SeedVariant(db, product.Id, "RED-XL", "红色", "XL", deleted: true);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            VariantController(db).Delete(product.Id, variant.Id));

        Assert.Equal(ErrorCodes.NotFound, ex.Code);
    }

    // ==================== 5. 历史商品兼容 / 列表与详情标注 / 有界视图 ====================

    [Fact]
    public async Task GetById_无规格的历史商品_计数为0且商品本身未被改写()
    {
        using var db = TestDbFactory.Create();
        var product = SeedProduct(db, "P-LEGACY", "老商品（单规格）");
        product.UpdatedAt = new DateTime(2026, 9, 1, 8, 0, 0);
        db.SaveChanges();
        var updatedAt = product.UpdatedAt;

        var read = Data<BaseProduct>(await ProductControllerOf(db).GetById(product.Id));

        Assert.Equal("P-LEGACY", read.ProductCode);
        Assert.Equal("老商品（单规格）", read.ProductName);
        Assert.Equal(0, read.VariantCount);
        Assert.Equal(0, read.VariantTotalCount);

        // 读取只做标注，不写库：商品行与时间戳原样，且不产生任何规格行（无需回填）
        var stored = db.BaseProducts.AsNoTracking().Single(p => p.Id == product.Id);
        Assert.Equal(updatedAt, stored.UpdatedAt);
        Assert.Empty(db.BaseProductVariants);
    }

    [Fact]
    public async Task GetById与GetPaged_按行标注启用数与规格总数()
    {
        using var db = TestDbFactory.Create();
        var mixed = SeedProduct(db, "P001", "商品1");          // 2 启用 + 1 停用
        SeedVariant(db, mixed.Id, "P1-A", "红色", "XL");
        SeedVariant(db, mixed.Id, "P1-B", "蓝色", "XL");
        SeedVariant(db, mixed.Id, "P1-C", "绿色", "XL", status: 0);

        var single = SeedProduct(db, "P002", "商品2");          // 无规格
        var deletedOnly = SeedProduct(db, "P003", "商品3");     // 仅有一条已删除规格
        SeedVariant(db, deletedOnly.Id, "P3-A", "黑色", "L", deleted: true);
        var ctl = ProductControllerOf(db);

        var detail = Data<BaseProduct>(await ctl.GetById(mixed.Id));
        Assert.Equal(2, detail.VariantCount);
        Assert.Equal(3, detail.VariantTotalCount);

        var page = Data<PagedResult<BaseProduct>>(await ctl.GetPaged(new PageQuery { Page = 1, PageSize = 50 }));
        Assert.Equal(3, page.Items.Count);
        Assert.Equal(2, page.Items.Single(p => p.Id == mixed.Id).VariantCount);
        Assert.Equal(3, page.Items.Single(p => p.Id == mixed.Id).VariantTotalCount);
        Assert.Equal(0, page.Items.Single(p => p.Id == single.Id).VariantCount);
        Assert.Equal(0, page.Items.Single(p => p.Id == deletedOnly.Id).VariantTotalCount);   // 软删除行不计入
    }

    [Fact]
    public async Task List_按排序号与Id稳定排序()
    {
        using var db = TestDbFactory.Create();
        var product = SeedProduct(db);
        var third = SeedVariant(db, product.Id, "V3", "红", "S", sortOrder: 3);
        var first = SeedVariant(db, product.Id, "V1", "红", "M", sortOrder: 1);
        var second = SeedVariant(db, product.Id, "V2", "红", "L", sortOrder: 2);
        var ctl = VariantController(db);

        var list = Data<List<ProductVariantDto>>(await ctl.List(product.Id));

        Assert.Equal(new[] { first.Id, second.Id, third.Id }, list.Select(v => v.Id).ToArray());
    }

    [Fact]
    public async Task List_超过上限_只返回上限数量且不能再新增()
    {
        using var db = TestDbFactory.Create();
        var product = SeedProduct(db);
        for (var i = 0; i < ProductVariantRules.MaxVariantsPerProduct + 5; i++)
        {
            db.BaseProductVariants.Add(new BaseProductVariant
            {
                ProductId = product.Id,
                VariantCode = $"BULK-{i:D3}",
                Color = "批量",
                Size = $"S{i}",
                ColorSizeKey = ProductVariantRules.BuildColorSizeKey("批量", $"S{i}")
            });
        }
        db.SaveChanges();
        var ctl = VariantController(db);

        var list = Data<List<ProductVariantDto>>(await ctl.List(product.Id));
        Assert.Equal(ProductVariantRules.MaxVariantsPerProduct, list.Count);          // 视图有界

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Create(product.Id,
            new ProductVariantSaveDto { VariantCode = "OVER-BOUND", Color = "批量", Size = "SX" }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Contains("上限", ex.Message);
    }

    // ==================== 6. 边界：规格维护不改写历史单据 / 库存 / 库存流水与商品本身 ====================

    [Fact]
    public async Task 规格维护_不改写历史单据行_库存与库存流水_也不改写商品本身()
    {
        using var db = TestDbFactory.Create();
        var product = SeedProduct(db, "P001", "保温杯");
        product.UpdatedAt = new DateTime(2026, 9, 10, 9, 0, 0);
        db.Stocks.Add(new Stock
        {
            WarehouseId = Warehouse1, ProductId = product.Id, Quantity = 10m, AvailableQuantity = 10m,
            TotalCost = 100m, AverageCost = 10m
        });
        db.StockMovements.Add(new StockMovement
        {
            MovementType = InventoryMovementType.PurchaseIn, SourceDocType = "StockIn", SourceDocId = 77L,
            SourceDocNo = "CGRK-1", WarehouseId = Warehouse1, ProductId = product.Id, Direction = 1,
            Quantity = 10m, UnitCost = 10m, Amount = 100m, BalanceQuantity = 10m, BalanceAmount = 100m,
            BalanceAverageCost = 10m
        });
        db.SalesOrderDetails.Add(new SalesOrderDetail
        {
            SalesOrderId = 55L, ProductId = product.Id, ProductName = "保温杯", Spec = "500ml",
            Quantity = 3m, UnitPrice = 20m, Amount = 60m
        });
        db.QuotationDetails.Add(new QuotationDetail
        {
            QuotationId = 66L, ProductId = product.Id, ProductName = "保温杯", Spec = "500ml",
            Quantity = 3m, UnitPrice = 20m
        });
        db.SaveChanges();
        var productUpdatedAt = db.BaseProducts.AsNoTracking().Single(p => p.Id == product.Id).UpdatedAt;

        var ctl = VariantController(db);
        var created = Data<ProductVariantDto>(await ctl.Create(product.Id, new ProductVariantSaveDto
        {
            VariantCode = "RED-XL", Color = "红色", Size = "XL"
        }));
        await ctl.Update(product.Id, created.Id, new ProductVariantSaveDto
        {
            VariantCode = "RED-XL", Color = "红色", Size = "XXL"
        });
        await ctl.Disable(product.Id, created.Id);
        await ctl.Delete(product.Id, created.Id);

        // 库存 / 库存流水 / 销售订单行 / 报价行：一处未动，也没有被拆分成按规格的多行
        var stockAfter = Assert.Single(db.Stocks);
        Assert.Equal(10m, stockAfter.Quantity);
        Assert.Equal(10m, stockAfter.AvailableQuantity);
        Assert.Equal(100m, stockAfter.TotalCost);
        Assert.Equal(10m, stockAfter.AverageCost);

        var movementAfter = Assert.Single(db.StockMovements);
        Assert.Equal(10m, movementAfter.Quantity);
        Assert.Equal(100m, movementAfter.Amount);
        Assert.Equal("CGRK-1", movementAfter.SourceDocNo);

        var orderLineAfter = Assert.Single(db.SalesOrderDetails);
        Assert.Equal(3m, orderLineAfter.Quantity);
        Assert.Equal(60m, orderLineAfter.Amount);
        Assert.Equal("500ml", orderLineAfter.Spec);

        var quotationLineAfter = Assert.Single(db.QuotationDetails);
        Assert.Equal(3m, quotationLineAfter.Quantity);
        Assert.Equal(20m, quotationLineAfter.UnitPrice);

        // 商品本身（身份与字段）保持原样，时间戳不变
        var productAfter = db.BaseProducts.AsNoTracking().Single(p => p.Id == product.Id);
        Assert.Equal("P001", productAfter.ProductCode);
        Assert.Equal("保温杯", productAfter.ProductName);
        Assert.Equal(productUpdatedAt, productAfter.UpdatedAt);

        // 只有规格子表发生变化（一行，软删除）
        Assert.True(Assert.Single(db.BaseProductVariants).IsDeleted);
    }

    // ==================== 7. 结构契约与前端接线（浏览器验收延后，此处做静态可达性核对） ====================

    [Fact]
    public void Model_声明两条过滤唯一索引_编码与启用颜色尺码组合()
    {
        using var db = TestDbFactory.Create();
        var entity = db.Model.FindEntityType(typeof(BaseProductVariant));
        Assert.NotNull(entity);

        var codeIndex = Assert.Single(entity!.GetIndexes(), i =>
            i.Properties.Any(p => p.Name == nameof(BaseProductVariant.ProductId))
            && i.Properties.Any(p => p.Name == nameof(BaseProductVariant.VariantCode)));
        Assert.True(codeIndex.IsUnique);
        Assert.Equal("UX_BaseProductVariants_ProductCode", codeIndex.GetDatabaseName());
        Assert.Contains("IsDeleted = 0", codeIndex.GetFilter());

        var comboIndex = Assert.Single(entity.GetIndexes(), i =>
            i.Properties.Any(p => p.Name == nameof(BaseProductVariant.ProductId))
            && i.Properties.Any(p => p.Name == nameof(BaseProductVariant.ColorSizeKey)));
        Assert.True(comboIndex.IsUnique);
        Assert.Equal("UX_BaseProductVariants_ProductColorSize", comboIndex.GetDatabaseName());
        Assert.Contains("IsDeleted = 0", comboIndex.GetFilter());
        Assert.Contains("Status = 1", comboIndex.GetFilter());          // 停用行不占用颜色/尺码组合

        // 规格表刻意不建外键：商品软删除 / 历史引用不受影响
        Assert.Empty(entity.GetForeignKeys());
    }

    [Fact]
    public void Schema_upgrade_幂等建表建索引且不做任何回填()
    {
        var script = File.ReadAllText(RepoFile("src", "ERP.Infrastructure", "Data", "SchemaUpgrader.cs"));

        Assert.Contains("IF OBJECT_ID('db_owner.BaseProductVariants') IS NULL", script);
        Assert.Contains("CREATE TABLE db_owner.BaseProductVariants", script);
        Assert.Contains("ProductId BIGINT NOT NULL", script);
        Assert.Contains("VariantCode NVARCHAR(50) NOT NULL DEFAULT N''", script);
        Assert.Contains("ColorSizeKey NVARCHAR(120) NOT NULL DEFAULT N''", script);
        Assert.Contains("Status INT NOT NULL DEFAULT 1", script);
        Assert.Contains("CREATE UNIQUE INDEX UX_BaseProductVariants_ProductCode", script);
        Assert.Contains("ON db_owner.BaseProductVariants(ProductId, VariantCode)", script);
        Assert.Contains("CREATE UNIQUE INDEX UX_BaseProductVariants_ProductColorSize", script);
        Assert.Contains("ON db_owner.BaseProductVariants(ProductId, ColorSizeKey)", script);
        Assert.Contains("WHERE IsDeleted = 0;", script);
        Assert.Contains("WHERE IsDeleted = 0 AND Status = 1;", script);

        // 幂等结构：不得出现任何按规格回填 / 改写历史数据或库存的语句
        Assert.DoesNotContain("UPDATE db_owner.BaseProductVariants", script);
        Assert.DoesNotContain("INSERT INTO db_owner.BaseProductVariants", script);
        Assert.DoesNotContain("UPDATE db_owner.Stocks", script);
        Assert.DoesNotContain("UPDATE db_owner.StockMovements", script);
    }

    [Fact]
    public void 路由与前端接线_规格维护入口可静态核对()
    {
        // API 路由挂在既有商品资源下，商品接口本身不变
        var route = typeof(ProductVariantController).GetCustomAttributes(typeof(RouteAttribute), false)
            .Cast<RouteAttribute>().Single();
        Assert.Equal("api/base/products/{productId:long}/variants", route.Template);

        // 前端：商品模块声明「规格数」列与「颜色/尺码规格」行操作，页面已加载规格维护脚本
        var modules = File.ReadAllText(RepoFile("src", "ERP.Api", "wwwroot", "js", "modules.js"));
        Assert.Contains("openProductVariants", modules);
        Assert.Contains("variantCount", modules);

        var ui = File.ReadAllText(RepoFile("src", "ERP.Api", "wwwroot", "js", "product-variants.js"));
        Assert.Contains("/api/base/products/${productId}/variants", ui);
        Assert.Contains("variantTotalCount", ui);
        Assert.Contains("PRODUCT_VARIANT_UNAVAILABLE_MARK", ui);

        var index = File.ReadAllText(RepoFile("src", "ERP.Api", "wwwroot", "index.html"));
        Assert.Contains("/js/product-variants.js", index);
    }

    /// <summary>按仓库根目录拼接文件的绝对路径（与其它契约测试口径一致）</summary>
    private static string RepoFile(params string[] segments)
        => Path.GetFullPath(Path.Combine(new[] { AppContext.BaseDirectory, "..", "..", "..", "..", ".." }.Concat(segments).ToArray()));
}
