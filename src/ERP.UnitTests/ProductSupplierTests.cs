using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
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
/// 商品 / SKU 货源关系（多供应商货源指引）单元测试（ERP-038）。覆盖：
/// 商品级与规格级关系的新增 / 修改 / 停用 / 启用 / 删除、引用校验（缺少 / 已删除 / 已停用 / 归属不符）、
/// 重复关系与「同一范围唯一启用首选」判定、首选的显式切换（与列表顺序无关）、
/// 有界列表与上限、停用关系的历史可读与不可选用，以及
/// 「货源关系维护不改写采购报价 / 采购订单 / 库存 / 库存流水 / 历史单据与主数据本身」的边界与幂等结构契约。
/// 全部使用内存库（TestDbFactory），不连接 SQL Server、不执行任何 SQL / 部署脚本。
/// </summary>
public class ProductSupplierTests
{
    // ==================== 0. 测试脚手架 ====================

    private const long Warehouse1 = 910001L;

    private static ProductSupplierController SupplyController(ErpDbContext db) => new(db);

    private static SupplierSourcingController SourcingController(ErpDbContext db) => new(db);

    /// <summary>商品资料控制器（补充规格 / 货源关系计数标注），依赖与生产一致：内存库 + 测试用 OSS 配置 + 假宿主环境</summary>
    private static ProductController ProductControllerOf(ErpDbContext db) => new(
        new GenericService<BaseProduct>(db),
        new OssStorageService(BuildOssConfiguration()),
        new ProductExcelExporter(db),
        new FakeWebHostEnvironment(),
        db);

    /// <summary>供应商资料控制器（补充货源关系计数标注）</summary>
    private static SupplierController SupplierControllerOf(ErpDbContext db) => new(
        new GenericService<BaseSupplier>(db), db);

    private static IConfiguration BuildOssConfiguration() =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Oss:AccessKeyId"] = "test-key-id",
            ["Oss:AccessKeySecret"] = "test-secret",
            ["Oss:Bucket"] = "test-bucket",
            ["Oss:Endpoint"] = "oss-cn-hangzhou.aliyuncs.com"
        }).Build();

    private static BaseProduct SeedProduct(
        ErpDbContext db, string code = "P001", string name = "保温杯", int status = 1, bool deleted = false)
    {
        var product = new BaseProduct { ProductCode = code, ProductName = name, Status = status, IsDeleted = deleted };
        db.BaseProducts.Add(product);
        db.SaveChanges();
        return product;
    }

    private static BaseSupplier SeedSupplier(
        ErpDbContext db, string code = "S001", string name = "义乌工厂", int status = 1, bool deleted = false)
    {
        var supplier = new BaseSupplier { SupplierCode = code, SupplierName = name, Status = status, IsDeleted = deleted };
        db.BaseSuppliers.Add(supplier);
        db.SaveChanges();
        return supplier;
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

    /// <summary>直接落库一条货源关系（用于构造历史 / 停用 / 首选 / 上限等场景，绕过服务端校验）</summary>
    private static BaseProductSupplier SeedRelation(
        ErpDbContext db, long productId, long supplierId, long? variantId = null,
        string itemCode = "", string unit = "", decimal moq = 0m, int leadTime = 0,
        bool preferred = false, int status = 1, bool deleted = false)
    {
        var relation = new BaseProductSupplier
        {
            ProductId = productId,
            VariantId = variantId,
            ScopeKey = ProductSupplierRules.BuildScopeKey(variantId),
            SupplierId = supplierId,
            SupplierItemCode = itemCode,
            PurchaseUnit = unit,
            MinOrderQty = moq,
            LeadTimeDays = leadTime,
            IsPreferred = preferred,
            Status = status,
            IsDeleted = deleted
        };
        db.BaseProductSuppliers.Add(relation);
        db.SaveChanges();
        return relation;
    }

    private static ProductSupplierSaveDto Save(long supplierId, long? variantId = null, string itemCode = "",
        string unit = "", decimal? moq = null, int? leadTime = null, bool? preferred = null, int? status = null)
        => new()
        {
            SupplierId = supplierId,
            VariantId = variantId,
            SupplierItemCode = itemCode,
            PurchaseUnit = unit,
            MinOrderQty = moq,
            LeadTimeDays = leadTime,
            IsPreferred = preferred,
            Status = status
        };

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

    // ==================== 1. 新增：多供应商、商品级 / 规格级、规范化 ====================

    [Fact]
    public async Task Create_同一商品可关联多个供应商_商品级关系_服务端规范化并落库()
    {
        using var db = TestDbFactory.Create();
        var product = SeedProduct(db, "P001", "保温杯");
        var factory = SeedSupplier(db, "S001", "义乌工厂");
        var trader = SeedSupplier(db, "S002", "杭州贸易商");
        var ctl = SupplyController(db);

        var first = Data<ProductSupplierDto>(await ctl.Create(product.Id, Save(
            factory.Id, itemCode: "  F-8899  ", unit: " 箱 ", moq: 100m, leadTime: 15, preferred: true)));
        var second = Data<ProductSupplierDto>(await ctl.Create(product.Id, Save(
            trader.Id, itemCode: "T-001", unit: "个", moq: 50m, leadTime: 7)));

        Assert.Equal("P", first.ScopeKey);                                  // 商品级作用域键由服务端推导
        Assert.Equal(ProductSupplierRules.ProductScopeText, first.ScopeText);
        Assert.Null(first.VariantId);
        Assert.Equal("F-8899", first.SupplierItemCode);                      // 去首尾空白
        Assert.Equal("箱", first.PurchaseUnit);
        Assert.Equal(100m, first.MinOrderQty);
        Assert.Equal(15, first.LeadTimeDays);
        Assert.True(first.IsPreferred);
        Assert.True(first.Selectable);
        Assert.Equal("启用", first.StatusText);
        Assert.Equal("义乌工厂", first.SupplierName);
        Assert.True(first.SupplierAvailable);
        Assert.Equal("可选用", first.AvailabilityText);

        Assert.Equal("T-001", second.SupplierItemCode);
        Assert.False(second.IsPreferred);
        Assert.Equal(2, db.BaseProductSuppliers.Count());                     // 两个供应商各一条，互不影响
    }

    [Fact]
    public async Task Create_规格级关系_作用域键绑定规格且与商品级并存()
    {
        using var db = TestDbFactory.Create();
        var product = SeedProduct(db);
        var supplier = SeedSupplier(db);
        var variant = SeedVariant(db, product.Id, "RED-XL", "红色", "XL");
        var ctl = SupplyController(db);

        var byVariant = Data<ProductSupplierDto>(await ctl.Create(product.Id, Save(
            supplier.Id, variantId: variant.Id, itemCode: "SKU-RED-XL")));
        var byProduct = Data<ProductSupplierDto>(await ctl.Create(product.Id, Save(supplier.Id, itemCode: "P-ALL")));

        Assert.Equal($"V{variant.Id}", byVariant.ScopeKey);
        Assert.Equal(ProductSupplierRules.VariantScopeText, byVariant.ScopeText);
        Assert.Equal(variant.Id, byVariant.VariantId);
        Assert.Equal("RED-XL", byVariant.VariantCode);
        Assert.Equal("红色 / XL", byVariant.VariantName);
        Assert.Equal("P", byProduct.ScopeKey);                                // 同一供应商在商品级可另有一条
        Assert.Equal(2, db.BaseProductSuppliers.Count());
    }

    [Fact]
    public async Task Create_可选字段留空_按未指定处理()
    {
        using var db = TestDbFactory.Create();
        var product = SeedProduct(db);
        var supplier = SeedSupplier(db);

        var created = Data<ProductSupplierDto>(await SupplyController(db).Create(product.Id, Save(supplier.Id)));

        Assert.Equal(string.Empty, created.SupplierItemCode);
        Assert.Equal(string.Empty, created.PurchaseUnit);
        Assert.Equal(0m, created.MinOrderQty);
        Assert.Equal(0, created.LeadTimeDays);
        Assert.False(created.IsPreferred);
    }

    [Fact]
    public async Task Create_同一范围内同一供应商重复_拒绝且不落库()
    {
        using var db = TestDbFactory.Create();
        var product = SeedProduct(db);
        var supplier = SeedSupplier(db);
        var ctl = SupplyController(db);
        await ctl.Create(product.Id, Save(supplier.Id, itemCode: "F-1"));

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(product.Id, Save(supplier.Id, itemCode: "F-2")));

        Assert.Equal(ErrorCodes.Duplicate, ex.Code);
        Assert.Contains("已存在货源关系", ex.Message);
        Assert.Single(db.BaseProductSuppliers);
    }

    [Fact]
    public async Task Create_同一供应商但不同规格范围_允许()
    {
        using var db = TestDbFactory.Create();
        var product = SeedProduct(db);
        var supplier = SeedSupplier(db);
        var red = SeedVariant(db, product.Id, "RED-XL", "红色", "XL");
        var blue = SeedVariant(db, product.Id, "BLUE-XL", "蓝色", "XL");
        var ctl = SupplyController(db);

        await ctl.Create(product.Id, Save(supplier.Id, variantId: red.Id));
        await ctl.Create(product.Id, Save(supplier.Id, variantId: blue.Id));

        Assert.Equal(2, db.BaseProductSuppliers.Count());
        Assert.Equal(2, db.BaseProductSuppliers.Select(x => x.ScopeKey).Distinct().Count());
    }

    [Fact]
    public async Task Create_参数非法_拒绝且不落库()
    {
        using var db = TestDbFactory.Create();
        var product = SeedProduct(db);
        var supplier = SeedSupplier(db);
        var ctl = SupplyController(db);

        var negativeMoq = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(product.Id, Save(supplier.Id, moq: -1m)));
        Assert.Equal(ErrorCodes.InvalidParameter, negativeMoq.Code);
        Assert.Contains("不能为负数", negativeMoq.Message);

        var negativeLeadTime = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(product.Id, Save(supplier.Id, leadTime: -3)));
        Assert.Equal(ErrorCodes.InvalidParameter, negativeLeadTime.Code);

        var badStatus = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(product.Id, Save(supplier.Id, status: 7)));
        Assert.Equal(ErrorCodes.InvalidParameter, badStatus.Code);

        var tooLongUnit = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(product.Id, Save(supplier.Id, unit: new string('箱', 21))));
        Assert.Equal(ErrorCodes.InvalidParameter, tooLongUnit.Code);

        Assert.Empty(db.BaseProductSuppliers);
    }

    // ==================== 2. 首选：唯一性与显式切换 ====================

    [Fact]
    public async Task Create_该范围已有启用首选_再勾选首选被拒绝且原首选不变()
    {
        using var db = TestDbFactory.Create();
        var product = SeedProduct(db);
        var first = SeedSupplier(db, "S001", "工厂甲");
        var second = SeedSupplier(db, "S002", "工厂乙");
        var ctl = SupplyController(db);
        await ctl.Create(product.Id, Save(first.Id, preferred: true));

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(product.Id, Save(second.Id, preferred: true)));

        Assert.Equal(ErrorCodes.Duplicate, ex.Code);
        Assert.Contains("首选", ex.Message);
        // 原首选未被改动，也没有新增第二条
        var stored = Assert.Single(db.BaseProductSuppliers);
        Assert.Equal(first.Id, stored.SupplierId);
        Assert.True(stored.IsPreferred);
    }

    [Fact]
    public async Task Create_不同范围可各有一条首选_互不冲突()
    {
        using var db = TestDbFactory.Create();
        var product = SeedProduct(db);
        var supplierA = SeedSupplier(db, "S001", "工厂甲");
        var supplierB = SeedSupplier(db, "S002", "工厂乙");
        var variant = SeedVariant(db, product.Id, "RED-XL", "红色", "XL");
        var ctl = SupplyController(db);

        var productLevel = Data<ProductSupplierDto>(await ctl.Create(product.Id, Save(supplierA.Id, preferred: true)));
        var variantLevel = Data<ProductSupplierDto>(await ctl.Create(
            product.Id, Save(supplierB.Id, variantId: variant.Id, preferred: true)));

        Assert.True(productLevel.IsPreferred);
        Assert.True(variantLevel.IsPreferred);
        Assert.Equal(2, db.BaseProductSuppliers.Count());
    }

    [Fact]
    public async Task SetPreferred_显式切换_旧首选被释放且同范围内只剩一条首选()
    {
        using var db = TestDbFactory.Create();
        var product = SeedProduct(db);
        var supplierA = SeedSupplier(db, "S001", "工厂甲");
        var supplierB = SeedSupplier(db, "S002", "工厂乙");
        var ctl = SupplyController(db);
        var first = Data<ProductSupplierDto>(await ctl.Create(product.Id, Save(supplierA.Id, preferred: true)));
        var second = Data<ProductSupplierDto>(await ctl.Create(product.Id, Save(supplierB.Id)));

        var switched = Data<ProductSupplierDto>(await ctl.SetPreferred(product.Id, second.Id, true));
        Assert.True(switched.IsPreferred);

        var rows = Data<List<ProductSupplierDto>>(await ctl.List(product.Id));
        Assert.False(rows.Single(x => x.Id == first.Id).IsPreferred);       // 旧首选被显式释放
        Assert.True(rows.Single(x => x.Id == second.Id).IsPreferred);
        Assert.Single(rows.Where(x => x.IsPreferred));
    }

    [Fact]
    public async Task SetPreferred_取消首选只影响本行_其他范围的首选保持不变()
    {
        using var db = TestDbFactory.Create();
        var product = SeedProduct(db);
        var supplierA = SeedSupplier(db, "S001", "工厂甲");
        var supplierB = SeedSupplier(db, "S002", "工厂乙");
        var variant = SeedVariant(db, product.Id, "RED-XL", "红色", "XL");
        var ctl = SupplyController(db);
        var productLevel = Data<ProductSupplierDto>(await ctl.Create(product.Id, Save(supplierA.Id, preferred: true)));
        var variantLevel = Data<ProductSupplierDto>(await ctl.Create(
            product.Id, Save(supplierB.Id, variantId: variant.Id, preferred: true)));

        await ctl.SetPreferred(product.Id, productLevel.Id, false);

        var rows = Data<List<ProductSupplierDto>>(await ctl.List(product.Id));
        Assert.False(rows.Single(x => x.Id == productLevel.Id).IsPreferred);
        Assert.True(rows.Single(x => x.Id == variantLevel.Id).IsPreferred);  // 另一个范围不受影响
    }

    [Fact]
    public async Task SetPreferred_停用中的关系_拒绝设为首选()
    {
        using var db = TestDbFactory.Create();
        var product = SeedProduct(db);
        var supplier = SeedSupplier(db);
        var relation = SeedRelation(db, product.Id, supplier.Id, status: 0);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            SupplyController(db).SetPreferred(product.Id, relation.Id, true));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Contains("启用", ex.Message);
        Assert.False(relation.IsPreferred);
    }

    [Fact]
    public async Task Update_勾选首选但该范围已有其他启用首选_拒绝且不落库()
    {
        using var db = TestDbFactory.Create();
        var product = SeedProduct(db);
        var supplierA = SeedSupplier(db, "S001", "工厂甲");
        var supplierB = SeedSupplier(db, "S002", "工厂乙");
        var ctl = SupplyController(db);
        await ctl.Create(product.Id, Save(supplierA.Id, preferred: true));
        var second = Data<ProductSupplierDto>(await ctl.Create(product.Id, Save(supplierB.Id)));

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Update(product.Id, second.Id, Save(supplierB.Id, preferred: true)));

        Assert.Equal(ErrorCodes.Duplicate, ex.Code);
        var stored = db.BaseProductSuppliers.AsNoTracking().Single(x => x.Id == second.Id);
        Assert.False(stored.IsPreferred);                                   // 服务端拒绝后不留半成品状态
    }

    // ==================== 3. 引用校验：缺少 / 已删除 / 已停用 / 归属不符 ====================

    [Fact]
    public async Task Create_商品不存在或已删除_拒绝且不落库()
    {
        using var db = TestDbFactory.Create();
        var deleted = SeedProduct(db, "P-DEL", "已删除商品", deleted: true);
        var supplier = SeedSupplier(db);
        var ctl = SupplyController(db);

        var missing = await Assert.ThrowsAsync<BusinessException>(() => ctl.Create(999999L, Save(supplier.Id)));
        Assert.Equal(ErrorCodes.NotFound, missing.Code);

        var removed = await Assert.ThrowsAsync<BusinessException>(() => ctl.Create(deleted.Id, Save(supplier.Id)));
        Assert.Equal(ErrorCodes.NotFound, removed.Code);
        Assert.Empty(db.BaseProductSuppliers);
    }

    [Fact]
    public async Task Create_商品已停用_拒绝新增货源关系()
    {
        using var db = TestDbFactory.Create();
        var product = SeedProduct(db, "P-OFF", "停用商品", status: 0);
        var supplier = SeedSupplier(db);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            SupplyController(db).Create(product.Id, Save(supplier.Id)));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Contains("已停用", ex.Message);
        Assert.Empty(db.BaseProductSuppliers);
    }

    [Fact]
    public async Task Create_供应商不存在或已删除_拒绝且不落库()
    {
        using var db = TestDbFactory.Create();
        var product = SeedProduct(db);
        var deletedSupplier = SeedSupplier(db, "S-DEL", "已删除供应商", deleted: true);
        var ctl = SupplyController(db);

        var missing = await Assert.ThrowsAsync<BusinessException>(() => ctl.Create(product.Id, Save(888888L)));
        Assert.Equal(ErrorCodes.NotFound, missing.Code);

        var removed = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(product.Id, Save(deletedSupplier.Id)));
        Assert.Equal(ErrorCodes.NotFound, removed.Code);
        Assert.Empty(db.BaseProductSuppliers);
    }

    [Fact]
    public async Task Create_供应商已停用_拒绝新增货源关系()
    {
        using var db = TestDbFactory.Create();
        var product = SeedProduct(db);
        var supplier = SeedSupplier(db, "S-OFF", "停用供应商", status: 0);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            SupplyController(db).Create(product.Id, Save(supplier.Id)));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Contains("已停用", ex.Message);
        Assert.Empty(db.BaseProductSuppliers);
    }

    [Fact]
    public async Task Create_规格不存在已删除或已停用_拒绝()
    {
        using var db = TestDbFactory.Create();
        var product = SeedProduct(db);
        var supplier = SeedSupplier(db);
        var deletedVariant = SeedVariant(db, product.Id, "DEL", "红", "S", deleted: true);
        var inactiveVariant = SeedVariant(db, product.Id, "OFF", "红", "M", status: 0);
        var ctl = SupplyController(db);

        var missing = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(product.Id, Save(supplier.Id, variantId: 777777L)));
        Assert.Equal(ErrorCodes.NotFound, missing.Code);

        var removed = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(product.Id, Save(supplier.Id, variantId: deletedVariant.Id)));
        Assert.Equal(ErrorCodes.NotFound, removed.Code);

        var inactive = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(product.Id, Save(supplier.Id, variantId: inactiveVariant.Id)));
        Assert.Equal(ErrorCodes.InvalidParameter, inactive.Code);
        Assert.Contains("已停用", inactive.Message);

        Assert.Empty(db.BaseProductSuppliers);
    }

    [Fact]
    public async Task Create_规格不属于该商品_拒绝()
    {
        using var db = TestDbFactory.Create();
        var productA = SeedProduct(db, "P-A", "商品A");
        var productB = SeedProduct(db, "P-B", "商品B");
        var supplier = SeedSupplier(db);
        var foreignVariant = SeedVariant(db, productB.Id, "B-RED", "红色", "XL");

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            SupplyController(db).Create(productA.Id, Save(supplier.Id, variantId: foreignVariant.Id)));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Contains("不属于该商品", ex.Message);
        Assert.Empty(db.BaseProductSuppliers);
    }

    [Fact]
    public async Task Update_更换为已停用供应商_拒绝且保持原引用()
    {
        using var db = TestDbFactory.Create();
        var product = SeedProduct(db);
        var active = SeedSupplier(db, "S-ON", "启用供应商");
        var inactive = SeedSupplier(db, "S-OFF", "停用供应商", status: 0);
        var ctl = SupplyController(db);
        var created = Data<ProductSupplierDto>(await ctl.Create(product.Id, Save(active.Id, itemCode: "F-1")));

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Update(product.Id, created.Id, Save(inactive.Id, itemCode: "F-9")));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        var stored = db.BaseProductSuppliers.AsNoTracking().Single(x => x.Id == created.Id);
        Assert.Equal(active.Id, stored.SupplierId);                          // 供应商引用未被改写
        Assert.Equal("F-1", stored.SupplierItemCode);
    }

    [Fact]
    public async Task Update_未更换引用但供应商后来停用_仍可编辑其他字段()
    {
        using var db = TestDbFactory.Create();
        var product = SeedProduct(db);
        var supplier = SeedSupplier(db);
        var ctl = SupplyController(db);
        var created = Data<ProductSupplierDto>(await ctl.Create(product.Id, Save(supplier.Id, moq: 10m)));

        supplier.Status = 0;                                                 // 主数据后来停用（历史关系保持可读可维护）
        db.SaveChanges();

        var updated = Data<ProductSupplierDto>(await ctl.Update(
            product.Id, created.Id, Save(supplier.Id, moq: 20m, leadTime: 12, itemCode: "F-UPDATED")));

        Assert.Equal(20m, updated.MinOrderQty);
        Assert.Equal(12, updated.LeadTimeDays);
        Assert.False(updated.SupplierAvailable);                             // 读取时显式标注不可用
        Assert.Contains(ProductSupplierRules.UnavailableMark, updated.AvailabilityText);
    }

    [Fact]
    public async Task Update_规格更换到别的商品规格_拒绝()
    {
        using var db = TestDbFactory.Create();
        var productA = SeedProduct(db, "P-A", "商品A");
        var productB = SeedProduct(db, "P-B", "商品B");
        var supplier = SeedSupplier(db);
        var ownVariant = SeedVariant(db, productA.Id, "A-RED", "红色", "XL");
        var foreignVariant = SeedVariant(db, productB.Id, "B-RED", "红色", "XL");
        var ctl = SupplyController(db);
        var created = Data<ProductSupplierDto>(await ctl.Create(
            productA.Id, Save(supplier.Id, variantId: ownVariant.Id)));

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Update(productA.Id, created.Id, Save(supplier.Id, variantId: foreignVariant.Id)));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        var stored = db.BaseProductSuppliers.AsNoTracking().Single(x => x.Id == created.Id);
        Assert.Equal(ownVariant.Id, stored.VariantId);
    }

    [Fact]
    public async Task Update_商品级与规格级之间切换_作用域键同步重算()
    {
        using var db = TestDbFactory.Create();
        var product = SeedProduct(db);
        var supplier = SeedSupplier(db);
        var variant = SeedVariant(db, product.Id, "RED-XL", "红色", "XL");
        var ctl = SupplyController(db);
        var created = Data<ProductSupplierDto>(await ctl.Create(product.Id, Save(supplier.Id)));

        var toVariant = Data<ProductSupplierDto>(await ctl.Update(
            product.Id, created.Id, Save(supplier.Id, variantId: variant.Id)));
        Assert.Equal($"V{variant.Id}", toVariant.ScopeKey);

        var backToProduct = Data<ProductSupplierDto>(await ctl.Update(
            product.Id, created.Id, Save(supplier.Id, variantId: null)));
        Assert.Equal(ProductSupplierRules.ProductScopeKey, backToProduct.ScopeKey);
        Assert.Null(backToProduct.VariantId);
    }

    [Fact]
    public async Task Update_关系不存在或不属于该商品_按不存在处理()
    {
        using var db = TestDbFactory.Create();
        var productA = SeedProduct(db, "P-A", "商品A");
        var productB = SeedProduct(db, "P-B", "商品B");
        var supplier = SeedSupplier(db);
        var ctl = SupplyController(db);
        var created = Data<ProductSupplierDto>(await ctl.Create(productA.Id, Save(supplier.Id)));

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Update(productB.Id, created.Id, Save(supplier.Id)));

        Assert.Equal(ErrorCodes.NotFound, ex.Code);
    }

    // ==================== 4. 停用 / 启用 / 删除与历史可读 ====================

    [Fact]
    public async Task Disable_释放首选_列表仍可读且不再出现在可选用视图()
    {
        using var db = TestDbFactory.Create();
        var product = SeedProduct(db);
        var supplier = SeedSupplier(db);
        var ctl = SupplyController(db);
        var created = Data<ProductSupplierDto>(await ctl.Create(product.Id, Save(supplier.Id, preferred: true)));

        var disabled = Data<ProductSupplierDto>(await ctl.Disable(product.Id, created.Id));

        Assert.Equal(ProductSupplierRules.DisabledStatus, disabled.Status);
        Assert.False(disabled.IsPreferred);                                  // 停用即释放首选标记
        Assert.False(disabled.Selectable);
        Assert.Equal("停用", disabled.StatusText);
        Assert.Contains("已停用", disabled.AvailabilityText);

        var all = Data<List<ProductSupplierDto>>(await ctl.List(product.Id));
        Assert.Single(all);                                                  // 停用关系仍在明细中（历史可读）
        Assert.False(all[0].Selectable);

        var selectable = Data<List<ProductSupplierDto>>(await ctl.Options(product.Id));
        Assert.Empty(selectable);                                            // 可选用视图不含停用关系
    }

    [Fact]
    public async Task Enable_停用关系可恢复为可选用_首选需再次显式设置()
    {
        using var db = TestDbFactory.Create();
        var product = SeedProduct(db);
        var supplier = SeedSupplier(db);
        var ctl = SupplyController(db);
        var created = Data<ProductSupplierDto>(await ctl.Create(product.Id, Save(supplier.Id, preferred: true)));
        await ctl.Disable(product.Id, created.Id);

        var enabled = Data<ProductSupplierDto>(await ctl.Enable(product.Id, created.Id));

        Assert.Equal(ProductSupplierRules.ActiveStatus, enabled.Status);
        Assert.True(enabled.Selectable);
        Assert.False(enabled.IsPreferred);                                   // 首选不随启用自动恢复，需显式设置

        var nowPreferred = Data<ProductSupplierDto>(await ctl.SetPreferred(product.Id, created.Id, true));
        Assert.True(nowPreferred.IsPreferred);
        Assert.Single(Data<List<ProductSupplierDto>>(await ctl.Options(product.Id)));
    }

    [Fact]
    public async Task Enable_商品或供应商已停用_拒绝()
    {
        using var db = TestDbFactory.Create();
        var product = SeedProduct(db);
        var supplier = SeedSupplier(db);
        var ctl = SupplyController(db);
        var created = Data<ProductSupplierDto>(await ctl.Create(product.Id, Save(supplier.Id)));
        await ctl.Disable(product.Id, created.Id);

        supplier.Status = 0;
        db.SaveChanges();
        var supplierOff = await Assert.ThrowsAsync<BusinessException>(() => ctl.Enable(product.Id, created.Id));
        Assert.Equal(ErrorCodes.InvalidParameter, supplierOff.Code);
        Assert.Contains("已停用", supplierOff.Message);

        supplier.Status = 1;
        product.Status = 0;
        db.SaveChanges();
        var productOff = await Assert.ThrowsAsync<BusinessException>(() => ctl.Enable(product.Id, created.Id));
        Assert.Equal(ErrorCodes.InvalidParameter, productOff.Code);

        Assert.Equal(ProductSupplierRules.DisabledStatus,
            db.BaseProductSuppliers.AsNoTracking().Single(x => x.Id == created.Id).Status);
    }

    [Fact]
    public async Task Enable_会形成第二条启用首选_拒绝()
    {
        using var db = TestDbFactory.Create();
        var product = SeedProduct(db);
        var supplierA = SeedSupplier(db, "S001", "工厂甲");
        var supplierB = SeedSupplier(db, "S002", "工厂乙");
        var activePreferred = SeedRelation(db, product.Id, supplierA.Id, preferred: true);
        // 历史数据：停用行上仍保留首选标记（服务端停用时本会释放），启用时必须重新判定首选唯一
        var disabledPreferred = SeedRelation(db, product.Id, supplierB.Id, preferred: true, status: 0);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            SupplyController(db).Enable(product.Id, disabledPreferred.Id));

        Assert.Equal(ErrorCodes.Duplicate, ex.Code);
        Assert.Contains("首选", ex.Message);
        Assert.Equal(ProductSupplierRules.DisabledStatus,
            db.BaseProductSuppliers.AsNoTracking().Single(x => x.Id == disabledPreferred.Id).Status);
        Assert.True(db.BaseProductSuppliers.AsNoTracking().Single(x => x.Id == activePreferred.Id).IsPreferred);
    }

    [Fact]
    public async Task Delete_软删除_历史行保留且不再出现在明细与可选用视图()
    {
        using var db = TestDbFactory.Create();
        var product = SeedProduct(db);
        var supplier = SeedSupplier(db);
        var ctl = SupplyController(db);
        var created = Data<ProductSupplierDto>(await ctl.Create(product.Id, Save(supplier.Id, preferred: true)));

        await ctl.Delete(product.Id, created.Id);

        var stored = Assert.Single(db.BaseProductSuppliers);                  // 行仍在（软删除），不是物理删除
        Assert.True(stored.IsDeleted);
        Assert.False(stored.IsPreferred);
        Assert.Empty(Data<List<ProductSupplierDto>>(await ctl.List(product.Id)));
        Assert.Empty(Data<List<ProductSupplierDto>>(await ctl.Options(product.Id)));
    }

    [Fact]
    public async Task Delete_删除后可重新维护同一供应商()
    {
        using var db = TestDbFactory.Create();
        var product = SeedProduct(db);
        var supplier = SeedSupplier(db);
        var ctl = SupplyController(db);
        var created = Data<ProductSupplierDto>(await ctl.Create(product.Id, Save(supplier.Id, itemCode: "OLD")));

        await ctl.Delete(product.Id, created.Id);
        var reCreated = Data<ProductSupplierDto>(await ctl.Create(product.Id, Save(supplier.Id, itemCode: "AGAIN")));

        Assert.Equal("AGAIN", reCreated.SupplierItemCode);
        Assert.Equal(2, db.BaseProductSuppliers.Count());                    // 软删除行仍保留，新行另存
    }

    [Fact]
    public async Task List_按作用域与排序号稳定排序_商品级在前()
    {
        using var db = TestDbFactory.Create();
        var product = SeedProduct(db);
        var supplierA = SeedSupplier(db, "S001", "工厂甲");
        var supplierB = SeedSupplier(db, "S002", "工厂乙");
        var variant = SeedVariant(db, product.Id, "RED-XL", "红色", "XL");
        var variantLevel = SeedRelation(db, product.Id, supplierB.Id, variantId: variant.Id);
        var productLevel = SeedRelation(db, product.Id, supplierA.Id);

        var rows = Data<List<ProductSupplierDto>>(await SupplyController(db).List(product.Id));

        Assert.Equal(new[] { productLevel.Id, variantLevel.Id }, rows.Select(x => x.Id).ToArray());
        Assert.Equal(ProductSupplierRules.ProductScopeText, rows[0].ScopeText);
        Assert.Equal(ProductSupplierRules.VariantScopeText, rows[1].ScopeText);
    }

    [Fact]
    public async Task List_超过上限_只返回上限数量且不能再新增()
    {
        using var db = TestDbFactory.Create();
        var product = SeedProduct(db);
        for (var i = 0; i < ProductSupplierRules.MaxRelationshipsPerProduct + 5; i++)
        {
            db.BaseProductSuppliers.Add(new BaseProductSupplier
            {
                ProductId = product.Id,
                ScopeKey = ProductSupplierRules.ProductScopeKey,
                SupplierId = 800000L + i,
                SupplierItemCode = $"BULK-{i:D3}"
            });
        }
        db.SaveChanges();
        var ctl = SupplyController(db);

        var list = Data<List<ProductSupplierDto>>(await ctl.List(product.Id));
        Assert.Equal(ProductSupplierRules.MaxRelationshipsPerProduct, list.Count);       // 视图有界

        var supplier = SeedSupplier(db);
        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Create(product.Id, Save(supplier.Id)));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Contains("上限", ex.Message);
    }

    [Fact]
    public async Task Create_单供应商已达上限_拒绝()
    {
        using var db = TestDbFactory.Create();
        var product = SeedProduct(db);
        var supplier = SeedSupplier(db);
        for (var i = 0; i < ProductSupplierRules.MaxRelationshipsPerSupplier; i++)
        {
            db.BaseProductSuppliers.Add(new BaseProductSupplier
            {
                ProductId = 900000L + i,                                     // 其他商品的货源关系也计入该供应商上限
                ScopeKey = ProductSupplierRules.ProductScopeKey,
                SupplierId = supplier.Id
            });
        }
        db.SaveChanges();

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            SupplyController(db).Create(product.Id, Save(supplier.Id)));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Contains("上限", ex.Message);                                  // 单供应商上限（与商品侧上限区分）
        Assert.Contains("供应商", ex.Message);
        Assert.Equal(ProductSupplierRules.MaxRelationshipsPerSupplier, db.BaseProductSuppliers.Count());
    }

    [Fact]
    public async Task 供应商侧列表_有界只读_含停用关系与商品信息()
    {
        using var db = TestDbFactory.Create();
        var product = SeedProduct(db, "P001", "保温杯");
        var supplier = SeedSupplier(db, "S001", "义乌工厂");
        var variant = SeedVariant(db, product.Id, "RED-XL", "红色", "XL");
        SeedRelation(db, product.Id, supplier.Id, itemCode: "F-ACTIVE", unit: "箱", moq: 100m, leadTime: 15);
        SeedRelation(db, product.Id, supplier.Id, variantId: variant.Id, itemCode: "F-OFF", status: 0);
        var ctl = SourcingController(db);

        var rows = Data<List<ProductSupplierDto>>(await ctl.List(supplier.Id));
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r =>
        {
            Assert.Equal(product.Id, r.ProductId);
            Assert.Equal("P001", r.ProductCode);
            Assert.Equal("保温杯", r.ProductName);
        });
        var off = rows.Single(r => r.SupplierItemCode == "F-OFF");
        Assert.False(off.Selectable);
        Assert.Contains("已停用", off.AvailabilityText);

        var activeOnly = Data<List<ProductSupplierDto>>(await ctl.List(supplier.Id, activeOnly: true));
        Assert.Equal("F-ACTIVE", Assert.Single(activeOnly).SupplierItemCode);

        var missing = await Assert.ThrowsAsync<BusinessException>(() => ctl.List(424242L));
        Assert.Equal(ErrorCodes.NotFound, missing.Code);
    }

    // ==================== 5. 列表标注：商品与供应商两侧计数 ====================

    [Fact]
    public async Task 商品列表与详情_标注货源关系启用数与总数_且无关系的商品为0()
    {
        using var db = TestDbFactory.Create();
        var mixed = SeedProduct(db, "P001", "商品1");
        var noRelation = SeedProduct(db, "P002", "商品2");
        var variant = SeedVariant(db, mixed.Id, "RED-XL", "红色", "XL");
        var supplier = SeedSupplier(db);
        SeedRelation(db, mixed.Id, supplier.Id);                                        // 启用（商品级）
        SeedRelation(db, mixed.Id, supplier.Id, variantId: variant.Id, status: 0);      // 停用（规格级）
        SeedRelation(db, mixed.Id, supplier.Id, itemCode: "DELETED", deleted: true);    // 软删除不计入
        var ctl = ProductControllerOf(db);

        var detail = Data<BaseProduct>(await ctl.GetById(mixed.Id));
        Assert.Equal(1, detail.SourcingCount);
        Assert.Equal(2, detail.SourcingTotalCount);

        var page = Data<PagedResult<BaseProduct>>(await ctl.GetPaged(new PageQuery { Page = 1, PageSize = 50 }));
        var row = page.Items.Single(p => p.Id == mixed.Id);
        Assert.Equal(1, row.SourcingCount);
        Assert.Equal(2, row.SourcingTotalCount);
        var legacy = page.Items.Single(p => p.Id == noRelation.Id);
        Assert.Equal(0, legacy.SourcingCount);
        Assert.Equal(0, legacy.SourcingTotalCount);                                     // 无需任何回填
    }

    [Fact]
    public async Task 供应商列表与详情_标注供货商品启用数与总数()
    {
        using var db = TestDbFactory.Create();
        var product = SeedProduct(db);
        var supplier = SeedSupplier(db, "S001", "义乌工厂");
        var other = SeedSupplier(db, "S002", "杭州贸易商");
        var variant = SeedVariant(db, product.Id, "RED-XL", "红色", "XL");
        SeedRelation(db, product.Id, supplier.Id);
        SeedRelation(db, product.Id, supplier.Id, variantId: variant.Id, status: 0);
        var ctl = SupplierControllerOf(db);

        var detail = Data<BaseSupplier>(await ctl.GetById(supplier.Id));
        Assert.Equal(1, detail.SourcingCount);
        Assert.Equal(2, detail.SourcingTotalCount);

        var page = Data<PagedResult<BaseSupplier>>(await ctl.GetPaged(new PageQuery { Page = 1, PageSize = 50 }));
        Assert.Equal(1, page.Items.Single(s => s.Id == supplier.Id).SourcingCount);
        Assert.Equal(2, page.Items.Single(s => s.Id == supplier.Id).SourcingTotalCount);
        Assert.Equal(0, page.Items.Single(s => s.Id == other.Id).SourcingTotalCount);
    }

    // ==================== 6. 边界：货源关系维护不改写采购报价 / 采购订单 / 库存 / 历史单据 ====================

    [Fact]
    public async Task 货源关系维护_不改写采购报价_采购订单_库存_库存流水与主数据本身()
    {
        using var db = TestDbFactory.Create();
        var product = SeedProduct(db, "P001", "保温杯");
        product.UpdatedAt = new DateTime(2026, 9, 10, 9, 0, 0);
        var supplier = SeedSupplier(db, "S001", "义乌工厂");
        supplier.UpdatedAt = new DateTime(2026, 9, 11, 9, 0, 0);
        db.SaveChanges();

        db.PurchaseQuotes.Add(new PurchaseQuote
        {
            QuoteNo = "PQ-20260925-001", ProductId = product.Id, ProductName = "保温杯",
            SupplierId = supplier.Id, SupplierName = "义乌工厂", Quantity = 100m,
            QuotePrice = 12.5m, TotalAmount = 1250m, IsSelected = true, Status = "已选中"
        });
        db.PurchaseOrders.Add(new PurchaseOrder
        {
            OrderNo = "CG-20260925-001", SupplierId = supplier.Id, TotalAmount = 1250m,
            Status = DocumentStatus.Pending
        });
        db.PurchaseOrderDetails.Add(new PurchaseOrderDetail
        {
            PurchaseOrderId = 1L, ProductId = product.Id, ProductName = "保温杯", Spec = "500ml",
            Unit = "个", Quantity = 100m, UnitPrice = 12.5m, Amount = 1250m
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
        db.Stocks.Add(new Stock
        {
            WarehouseId = Warehouse1, ProductId = product.Id, Quantity = 10m, AvailableQuantity = 10m,
            TotalCost = 125m, AverageCost = 12.5m
        });
        db.StockMovements.Add(new StockMovement
        {
            MovementType = InventoryMovementType.PurchaseIn, SourceDocType = "StockIn", SourceDocId = 77L,
            SourceDocNo = "CGRK-1", WarehouseId = Warehouse1, ProductId = product.Id, Direction = 1,
            Quantity = 10m, UnitCost = 12.5m, Amount = 125m, BalanceQuantity = 10m, BalanceAmount = 125m,
            BalanceAverageCost = 12.5m
        });
        db.SaveChanges();

        var productUpdatedAt = db.BaseProducts.AsNoTracking().Single(p => p.Id == product.Id).UpdatedAt;
        var supplierUpdatedAt = db.BaseSuppliers.AsNoTracking().Single(s => s.Id == supplier.Id).UpdatedAt;

        var ctl = SupplyController(db);
        var created = Data<ProductSupplierDto>(await ctl.Create(
            product.Id, Save(supplier.Id, itemCode: "F-1", unit: "箱", moq: 100m, leadTime: 15, preferred: true)));
        await ctl.Update(product.Id, created.Id, Save(supplier.Id, itemCode: "F-1", moq: 120m, leadTime: 20));
        await ctl.SetPreferred(product.Id, created.Id, false);
        await ctl.SetPreferred(product.Id, created.Id, true);
        await ctl.Disable(product.Id, created.Id);
        await ctl.Enable(product.Id, created.Id);
        await ctl.Delete(product.Id, created.Id);

        // 采购报价：选中供应商、单价与状态一处未动
        var quote = Assert.Single(db.PurchaseQuotes);
        Assert.Equal(supplier.Id, quote.SupplierId);
        Assert.Equal(12.5m, quote.QuotePrice);
        Assert.True(quote.IsSelected);
        Assert.Equal("已选中", quote.Status);

        // 采购订单与明细：供应商、数量、单价、金额一处未动
        var order = Assert.Single(db.PurchaseOrders);
        Assert.Equal(supplier.Id, order.SupplierId);
        Assert.Equal(1250m, order.TotalAmount);
        var orderLine = Assert.Single(db.PurchaseOrderDetails);
        Assert.Equal(100m, orderLine.Quantity);
        Assert.Equal(12.5m, orderLine.UnitPrice);
        Assert.Equal(1250m, orderLine.Amount);

        // 销售订单行 / 报价行：未被拆分或改写（货源关系不对单据行做任何加工）
        Assert.Single(db.SalesOrderDetails);
        Assert.Equal(3m, db.SalesOrderDetails.Single().Quantity);
        Assert.Single(db.QuotationDetails);
        Assert.Equal(20m, db.QuotationDetails.Single().UnitPrice);

        // 库存与库存流水：数量、成本、余额均未变化
        var stock = Assert.Single(db.Stocks);
        Assert.Equal(10m, stock.Quantity);
        Assert.Equal(125m, stock.TotalCost);
        Assert.Equal(12.5m, stock.AverageCost);
        var movement = Assert.Single(db.StockMovements);
        Assert.Equal(10m, movement.Quantity);
        Assert.Equal(125m, movement.Amount);
        Assert.Equal(12.5m, movement.BalanceAverageCost);

        // 商品与供应商主数据本身（身份与字段）保持原样，时间戳不变
        var productAfter = db.BaseProducts.AsNoTracking().Single(p => p.Id == product.Id);
        Assert.Equal("P001", productAfter.ProductCode);
        Assert.Equal(productUpdatedAt, productAfter.UpdatedAt);
        var supplierAfter = db.BaseSuppliers.AsNoTracking().Single(s => s.Id == supplier.Id);
        Assert.Equal("义乌工厂", supplierAfter.SupplierName);
        Assert.Equal(supplierUpdatedAt, supplierAfter.UpdatedAt);

        // 只有货源关系子表发生变化（一行，软删除）
        Assert.True(Assert.Single(db.BaseProductSuppliers).IsDeleted);
    }

    // ==================== 7. 结构契约与前端接线（浏览器验收延后，此处做静态可达性核对） ====================

    [Fact]
    public void Model_声明两条过滤唯一索引与供应商索引_且不建外键()
    {
        using var db = TestDbFactory.Create();
        var entity = db.Model.FindEntityType(typeof(BaseProductSupplier));
        Assert.NotNull(entity);

        var scopeSupplier = Assert.Single(entity!.GetIndexes(), i =>
            i.Properties.Any(p => p.Name == nameof(BaseProductSupplier.ProductId))
            && i.Properties.Any(p => p.Name == nameof(BaseProductSupplier.ScopeKey))
            && i.Properties.Any(p => p.Name == nameof(BaseProductSupplier.SupplierId)));
        Assert.True(scopeSupplier.IsUnique);
        Assert.Equal("UX_BaseProductSuppliers_ScopeSupplier", scopeSupplier.GetDatabaseName());
        Assert.Contains("IsDeleted = 0", scopeSupplier.GetFilter());

        var scopePreferred = Assert.Single(entity.GetIndexes(), i =>
            i.IsUnique && i.Properties.Count == 2
            && i.Properties.Any(p => p.Name == nameof(BaseProductSupplier.ProductId))
            && i.Properties.Any(p => p.Name == nameof(BaseProductSupplier.ScopeKey)));
        Assert.Equal("UX_BaseProductSuppliers_ScopePreferred", scopePreferred.GetDatabaseName());
        Assert.Contains("Status = 1", scopePreferred.GetFilter());            // 停用行不占用首选位
        Assert.Contains("IsPreferred = 1", scopePreferred.GetFilter());

        var supplierIndex = Assert.Single(entity.GetIndexes(), i =>
            i.Properties.Count == 1 && i.Properties[0].Name == nameof(BaseProductSupplier.SupplierId));
        Assert.False(supplierIndex.IsUnique);
        Assert.Equal("IX_BaseProductSuppliers_SupplierId", supplierIndex.GetDatabaseName());

        // 表刻意不建外键：商品 / 规格 / 供应商软删除后历史货源关系仍可读
        Assert.Empty(entity.GetForeignKeys());

        // MOQ 使用 4 位小数口径（与建表脚本 DECIMAL(18,4) 一致），避免 EF 默认精度截断
        Assert.Equal(18, entity.FindProperty(nameof(BaseProductSupplier.MinOrderQty))!.GetPrecision());
        Assert.Equal(4, entity.FindProperty(nameof(BaseProductSupplier.MinOrderQty))!.GetScale());
    }

    [Fact]
    public void Schema_upgrade_幂等建表建索引且不做任何回填()
    {
        var script = File.ReadAllText(RepoFile("src", "ERP.Infrastructure", "Data", "SchemaUpgrader.cs"));

        Assert.Contains("IF OBJECT_ID('db_owner.BaseProductSuppliers') IS NULL", script);
        Assert.Contains("CREATE TABLE db_owner.BaseProductSuppliers", script);
        Assert.Contains("VariantId BIGINT NULL", script);
        Assert.Contains("ScopeKey NVARCHAR(30) NOT NULL DEFAULT N''", script);
        Assert.Contains("SupplierItemCode NVARCHAR(100) NOT NULL DEFAULT N''", script);
        Assert.Contains("PurchaseUnit NVARCHAR(20) NOT NULL DEFAULT N''", script);
        Assert.Contains("MinOrderQty DECIMAL(18,4) NOT NULL DEFAULT 0", script);
        Assert.Contains("LeadTimeDays INT NOT NULL DEFAULT 0", script);
        Assert.Contains("IsPreferred BIT NOT NULL DEFAULT 0", script);
        Assert.Contains("CREATE UNIQUE INDEX UX_BaseProductSuppliers_ScopeSupplier", script);
        Assert.Contains("ON db_owner.BaseProductSuppliers(ProductId, ScopeKey, SupplierId)", script);
        Assert.Contains("CREATE UNIQUE INDEX UX_BaseProductSuppliers_ScopePreferred", script);
        Assert.Contains("ON db_owner.BaseProductSuppliers(ProductId, ScopeKey)", script);
        Assert.Contains("WHERE IsDeleted = 0 AND Status = 1 AND IsPreferred = 1;", script);
        Assert.Contains("CREATE INDEX IX_BaseProductSuppliers_SupplierId", script);

        // 幂等结构：不得出现任何按货源关系回填 / 改写采购单据、库存或历史数据的语句
        Assert.DoesNotContain("UPDATE db_owner.BaseProductSuppliers", script);
        Assert.DoesNotContain("INSERT INTO db_owner.BaseProductSuppliers", script);
        Assert.DoesNotContain("UPDATE db_owner.PurchaseQuotes", script);
        Assert.DoesNotContain("UPDATE db_owner.PurchaseOrders", script);
        Assert.DoesNotContain("UPDATE db_owner.Stocks", script);
        Assert.DoesNotContain("UPDATE db_owner.StockMovements", script);
    }

    [Fact]
    public void 路由与前端接线_货源维护入口可静态核对()
    {
        // API 路由挂在既有商品 / 供应商资源下，既有接口本身不变
        var productRoute = typeof(ProductSupplierController).GetCustomAttributes(typeof(RouteAttribute), false)
            .Cast<RouteAttribute>().Single();
        Assert.Equal("api/base/products/{productId:long}/suppliers", productRoute.Template);

        var supplierRoute = typeof(SupplierSourcingController).GetCustomAttributes(typeof(RouteAttribute), false)
            .Cast<RouteAttribute>().Single();
        Assert.Equal("api/base/suppliers/{supplierId:long}/sourcing", supplierRoute.Template);

        // 前端：商品与供应商模块声明货源列与行操作，页面已加载货源关系脚本
        var modules = File.ReadAllText(RepoFile("src", "ERP.Api", "wwwroot", "js", "modules.js"));
        Assert.Contains("openProductSuppliers", modules);
        Assert.Contains("openSupplierSourcing", modules);
        Assert.Contains("sourcingCount", modules);

        var ui = File.ReadAllText(RepoFile("src", "ERP.Api", "wwwroot", "js", "product-suppliers.js"));
        Assert.Contains("/api/base/products/${productId}/suppliers", ui);
        Assert.Contains("/api/base/suppliers/${supplierId}/sourcing", ui);
        Assert.Contains("sourcingTotalCount", ui);
        Assert.Contains("PRODUCT_SUPPLIER_UNAVAILABLE_MARK", ui);
        Assert.Contains("preferred", ui);

        var index = File.ReadAllText(RepoFile("src", "ERP.Api", "wwwroot", "index.html"));
        Assert.Contains("/js/product-suppliers.js", index);
    }

    // ==================== 8. 纯规则：作用域键 / 规范化 / 状态与可用性文案 ====================

    [Fact]
    public void Rules_作用域键推导_商品级与规格级()
    {
        Assert.Equal(ProductSupplierRules.ProductScopeKey, ProductSupplierRules.BuildScopeKey(null));
        Assert.Equal(ProductSupplierRules.ProductScopeKey, ProductSupplierRules.BuildScopeKey(0));
        Assert.Equal("V7", ProductSupplierRules.BuildScopeKey(7));
        Assert.False(ProductSupplierRules.IsVariantScope("P"));
        Assert.True(ProductSupplierRules.IsVariantScope("V7"));
        Assert.Equal(ProductSupplierRules.ProductScopeText, ProductSupplierRules.ScopeText("P"));
        Assert.Equal(ProductSupplierRules.VariantScopeText, ProductSupplierRules.ScopeText("V9"));
    }

    [Fact]
    public void Rules_规范化_压缩空白并保留原大小写()
    {
        var normalized = ProductSupplierRules.Normalize("  f   8899 ", "  箱  ", 100.5m, 15, "  首选  工厂  ");

        Assert.Equal("f 8899", normalized.SupplierItemCode);
        Assert.Equal("箱", normalized.PurchaseUnit);
        Assert.Equal(100.5m, normalized.MinOrderQty);
        Assert.Equal(15, normalized.LeadTimeDays);
        Assert.Equal("首选 工厂", normalized.Remark);

        var empty = ProductSupplierRules.Normalize(null, null, null, null, null);
        Assert.Equal(string.Empty, empty.SupplierItemCode);
        Assert.Equal(0m, empty.MinOrderQty);
        Assert.Equal(0, empty.LeadTimeDays);
    }

    [Fact]
    public void Rules_状态与数值边界校验()
    {
        Assert.Equal(ProductSupplierRules.ActiveStatus, ProductSupplierRules.NormalizeStatus(null));
        Assert.Equal(ProductSupplierRules.ActiveStatus, ProductSupplierRules.NormalizeStatus(1));
        Assert.Equal(ProductSupplierRules.DisabledStatus, ProductSupplierRules.NormalizeStatus(0));
        Assert.Throws<BusinessException>(() => ProductSupplierRules.NormalizeStatus(2));

        Assert.Throws<BusinessException>(() =>
            ProductSupplierRules.Normalize("", "", ProductSupplierRules.MaxMinOrderQty + 1m, 0, ""));
        Assert.Throws<BusinessException>(() =>
            ProductSupplierRules.Normalize("", "", 0m, ProductSupplierRules.MaxLeadTimeDays + 1, ""));
        Assert.Throws<BusinessException>(() =>
            ProductSupplierRules.Normalize(new string('x', ProductSupplierRules.MaxSupplierItemCodeLength + 1), "", 0m, 0, ""));
        Assert.Throws<BusinessException>(() =>
            ProductSupplierRules.Normalize("", "", 0m, 0, new string('x', ProductSupplierRules.MaxRemarkLength + 1)));
    }

    [Fact]
    public void Rules_可用性与展示文案_停用或缺引用都显式标注()
    {
        var disabled = new BaseProductSupplier { Id = 1, Status = 0, IsPreferred = true };
        Assert.False(ProductSupplierRules.IsSelectable(disabled));
        Assert.False(ProductSupplierRules.IsPreferredActive(disabled));       // 停用行不占用首选位

        var active = new BaseProductSupplier { Id = 2, Status = 1 };
        Assert.True(ProductSupplierRules.IsSelectable(active));
        Assert.False(ProductSupplierRules.IsPreferredActive(active));
        active.IsPreferred = true;
        Assert.True(ProductSupplierRules.IsPreferredActive(active));

        var deleted = new BaseProductSupplier { Id = 3, Status = 1, IsPreferred = true, IsDeleted = true };
        Assert.False(ProductSupplierRules.IsSelectable(deleted));

        Assert.Equal(ProductSupplierRules.UnavailableMark, ProductSupplierRules.MarkUnavailable("  "));
        Assert.Equal($"义乌工厂{ProductSupplierRules.UnavailableMark}", ProductSupplierRules.MarkUnavailable(" 义乌工厂 "));

        Assert.Equal("可选用", ProductSupplierRules.AvailabilityText(true, true, true));
        Assert.Contains("已停用", ProductSupplierRules.AvailabilityText(false, true, true));
        Assert.Contains(ProductSupplierRules.UnavailableMark, ProductSupplierRules.AvailabilityText(true, false, true));
        Assert.Contains(ProductSupplierRules.UnavailableMark, ProductSupplierRules.AvailabilityText(true, true, false));
    }

    /// <summary>按仓库根目录拼接文件的绝对路径（与其它契约测试口径一致）</summary>
    private static string RepoFile(params string[] segments)
        => Path.GetFullPath(Path.Combine(new[] { AppContext.BaseDirectory, "..", "..", "..", "..", ".." }.Concat(segments).ToArray()));
}
