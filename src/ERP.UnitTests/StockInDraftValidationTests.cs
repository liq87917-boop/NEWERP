using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-504 采购入库草稿写入校验单元测试（内存库 + 真实 HTTP 身份）：
/// <list type="number">
/// <item>创建与修改在单号预约 / 明细整体替换之前拒绝「商品 Id 缺失 / 不存在 / 已删除 / 已停用」；</item>
/// <item>拒绝「数量非正或不可表示 / 溢出、毛重与体积为负或不可表示、表头合计不可表示 / 溢出」（与 EF 实际精度同源）；</item>
/// <item>客户端 <c>IsDeleted=true</c> 不能隐藏坏行（服务端会把每一行都恢复为正常行），有效隐藏行照常持久化为正常行；</item>
/// <item>保留空草稿头（0 行）与重复商品行（累计数量语义），提交 / 审核要求至少一行有效明细且复核当前在用商品；</item>
/// <item>单位折算先于护栏（包装单位行先折算为基础单位再校验，折算后不可表示同样拒绝）；</item>
/// <item>历史取消 / 冲销路径不新增任何校验闸门（台账与退货引用护栏保持既有口径）；</item>
/// <item>每个拒绝场景都断言零单据、零明细、零库存、零流水、零单号消耗，且既有单据与明细逐字段不变。</item>
/// </list>
/// 说明：全部使用内存数据库；只有「实际 EF 精度」用例以 SQL Server 提供程序<b>仅构建模型</b>（不连接数据库）。
/// </summary>
public class StockInDraftValidationTests
{
    private const long SupplierA = 985001L;
    private const long WarehouseA = 985101L;
    private const long ProductA = 985201L;
    private const long ProductB = 985202L;
    private const long PackageProduct = 985203L;
    private const long MissingProduct = 985999L;

    // ==================== 1. 创建：非法明细（零写入 / 零单号） ====================

    [Theory]
    [InlineData("missing-product-id", ErrorCodes.InvalidParameter)]
    [InlineData("missing-product", ErrorCodes.InvalidParameter)]
    [InlineData("deleted-product", ErrorCodes.InvalidParameter)]
    [InlineData("disabled-product", ErrorCodes.RuleConflict)]
    [InlineData("zero-quantity", ErrorCodes.InvalidParameter)]
    [InlineData("negative-quantity", ErrorCodes.InvalidParameter)]
    [InlineData("unrepresentable-quantity", ErrorCodes.InvalidParameter)]
    [InlineData("overflow-quantity", ErrorCodes.InvalidParameter)]
    [InlineData("negative-weight", ErrorCodes.InvalidParameter)]
    [InlineData("unrepresentable-weight", ErrorCodes.InvalidParameter)]
    [InlineData("negative-volume", ErrorCodes.InvalidParameter)]
    [InlineData("unrepresentable-volume", ErrorCodes.InvalidParameter)]
    public async Task 创建_非法明细_拒绝且零单据零明细零库存零流水零单号(string scenario, int expectedCode)
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        SeedNumberRule(db);
        if (scenario == "deleted-product") SoftDeleteProduct(db, ProductA);
        if (scenario == "disabled-product") DisableProduct(db, ProductA);

        var line = scenario switch
        {
            "missing-product-id" => Line(0L, 1m),
            "missing-product" => Line(MissingProduct, 1m),
            "zero-quantity" => Line(ProductA, 0m),
            "negative-quantity" => Line(ProductA, -1m),
            "unrepresentable-quantity" => Line(ProductA, 1.005m),
            "overflow-quantity" => Line(ProductA, 10_000_000_000_000_000m),
            "negative-weight" => Line(ProductA, 1m, weight: -0.5m),
            "unrepresentable-weight" => Line(ProductA, 1m, weight: 0.005m),
            "negative-volume" => Line(ProductA, 1m, volume: -0.5m),
            "unrepresentable-volume" => Line(ProductA, 1m, volume: 0.005m),
            _ => Line(ProductA, 1m)
        };

        var controller = Controller(db);
        var error = await Assert.ThrowsAsync<BusinessException>(() => controller.Create(Draft(line)));

        Assert.Equal(expectedCode, error.Code);
        AssertRejectedWithNoSideEffects(db);
    }

    // ==================== 2. 创建：客户端删除态不能掩盖坏行 ====================

    [Theory]
    [InlineData("hidden-zero-quantity", ProductA, ErrorCodes.InvalidParameter)]
    [InlineData("hidden-negative-quantity", ProductA, ErrorCodes.InvalidParameter)]
    [InlineData("hidden-missing-product", MissingProduct, ErrorCodes.InvalidParameter)]
    [InlineData("hidden-unrepresentable-quantity", ProductA, ErrorCodes.InvalidParameter)]
    public async Task 创建_客户端删除态隐藏坏行_仍然拒绝且零副作用(string scenario, long productId, int expectedCode)
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        SeedNumberRule(db);

        var hidden = scenario switch
        {
            "hidden-zero-quantity" => Line(productId, 0m, isDeleted: true),
            "hidden-negative-quantity" => Line(productId, -2m, isDeleted: true),
            "hidden-unrepresentable-quantity" => Line(productId, 0.0001m, isDeleted: true),
            _ => Line(productId, 1m, isDeleted: true)
        };

        var controller = Controller(db);
        var error = await Assert.ThrowsAsync<BusinessException>(() =>
            controller.Create(Draft(Line(ProductA, 1m), hidden)));

        Assert.Equal(expectedCode, error.Code);
        AssertRejectedWithNoSideEffects(db);
    }

    [Fact]
    public async Task 创建_客户端删除态有效行_服务端恢复为正常行并计入合计()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        SeedNumberRule(db);

        var controller = Controller(db);
        var hidden = Line(ProductB, 3m, isDeleted: true);
        hidden.Id = 424242L;
        hidden.StockInId = 777777L;

        var result = await controller.Create(Draft(Line(ProductA, 2m), hidden));

        Assert.IsType<OkObjectResult>(result);
        var saved = db.StockIns.Include(o => o.Details).Single();
        Assert.Equal(5m, saved.TotalQuantity);
        Assert.Equal(2, saved.Details.Count);
        Assert.All(saved.Details, d => Assert.False(d.IsDeleted));
        Assert.All(saved.Details, d => Assert.NotEqual(424242L, d.Id));
        Assert.All(saved.Details, d => Assert.Equal(saved.Id, d.StockInId));
    }

    [Fact]
    public async Task 创建_包装单位先折算后校验_基础单位数量落库()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        SeedNumberRule(db);

        var controller = Controller(db);
        var result = await controller.Create(Draft(Line(PackageProduct, 2m, unit: "BOX")));

        Assert.IsType<OkObjectResult>(result);
        var saved = db.StockIns.Include(o => o.Details).Single();
        var detail = Assert.Single(saved.Details);
        Assert.Equal("PCS", detail.Unit);
        Assert.Equal(24m, detail.Quantity);
        Assert.Equal(24m, saved.TotalQuantity);
    }

    [Fact]
    public async Task 创建_折算后数量不可表示_拒绝且零副作用()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        SeedNumberRule(db);

        // 0.0001 箱 × 12 = 0.0012 —— 折算结果在 DECIMAL(18,2) 内不可表示，必须在落库前拒绝。
        var controller = Controller(db);
        var error = await Assert.ThrowsAsync<BusinessException>(() =>
            controller.Create(Draft(Line(PackageProduct, 0.0001m, unit: "BOX"))));

        Assert.Equal(ErrorCodes.InvalidParameter, error.Code);
        Assert.Contains("超出存储精度", error.Message);
        AssertRejectedWithNoSideEffects(db);
    }

    // ==================== 3. 提交 / 审核：空明细、失效主数据与失效已存数值 ====================

    [Fact]
    public async Task 创建_空草稿头_保存成功且只消耗一个单号()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        SeedNumberRule(db);

        var controller = Controller(db);
        var result = await controller.Create(Draft());

        Assert.IsType<OkObjectResult>(result);
        var saved = db.StockIns.Include(o => o.Details).Single();
        Assert.Empty(saved.Details);
        Assert.Equal(0m, saved.TotalQuantity);
        Assert.Equal(0m, saved.TotalWeight);
        Assert.Equal(0m, saved.TotalVolume);
        Assert.Equal(DocumentStatus.Pending, saved.Status);
        Assert.Equal(1L, NumberSequence(db));
    }

    [Fact]
    public async Task 提交_空草稿头_拒绝且状态保持待提交()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        SeedNumberRule(db);

        var controller = Controller(db);
        var id = CreatedId(await controller.Create(Draft()));

        var error = await Assert.ThrowsAsync<BusinessException>(() => controller.Submit(id));

        Assert.Equal(ErrorCodes.RuleConflict, error.Code);
        Assert.Contains("入库单无明细", error.Message);
        Assert.Equal(DocumentStatus.Pending, db.StockIns.AsNoTracking().Single(o => o.Id == id).Status);
        Assert.Empty(db.StockMovements);
        Assert.Empty(db.Stocks);
    }

    [Fact]
    public async Task 审核_历史已提交空草稿头_拒绝且零库存零流水零状态变化()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        SeedNumberRule(db);

        // 历史形状：直接落一张「已提交、无明细」的单据（不经过 Create），审核必须拒绝。
        var legacy = new StockIn
        {
            StockInNo = "RK-504-LEGACY-EMPTY",
            StockInDate = DateTime.Today,
            SupplierId = SupplierA,
            WarehouseId = WarehouseA,
            Status = DocumentStatus.Submitted
        };
        db.StockIns.Add(legacy);
        db.SaveChanges();

        var controller = Controller(db);
        var error = await Assert.ThrowsAsync<BusinessException>(() => controller.Approve(legacy.Id));

        Assert.Equal(ErrorCodes.RuleConflict, error.Code);
        Assert.Contains("入库单无明细", error.Message);
        Assert.Equal(DocumentStatus.Submitted, db.StockIns.AsNoTracking().Single(o => o.Id == legacy.Id).Status);
        Assert.Empty(db.StockMovements);
        Assert.Empty(db.Stocks);
    }

    [Theory]
    [InlineData("zero-quantity", ErrorCodes.InvalidParameter)]
    [InlineData("negative-quantity", ErrorCodes.InvalidParameter)]
    [InlineData("deleted-product", ErrorCodes.InvalidParameter)]
    [InlineData("disabled-product", ErrorCodes.RuleConflict)]
    public async Task 审核_已存数值或主数据失效_拒绝且零库存零流水状态保持已提交(string scenario, int expectedCode)
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        SeedNumberRule(db);

        var controller = Controller(db);
        var id = CreatedId(await controller.Create(Draft(Line(ProductA, 2m))));
        await controller.Submit(id);

        if (scenario == "deleted-product") SoftDeleteProduct(db, ProductA);
        if (scenario == "disabled-product") DisableProduct(db, ProductA);
        if (scenario is "zero-quantity" or "negative-quantity")
        {
            var stored = db.StockInDetails.Single(d => d.StockInId == id);
            stored.Quantity = scenario == "zero-quantity" ? 0m : -2m;
            db.SaveChanges();
        }

        var error = await Assert.ThrowsAsync<BusinessException>(() => controller.Approve(id));

        Assert.Equal(expectedCode, error.Code);
        Assert.Equal(DocumentStatus.Submitted, db.StockIns.AsNoTracking().Single(o => o.Id == id).Status);
        Assert.Empty(db.StockMovements);
        Assert.Empty(db.Stocks);
    }

    [Theory]
    [InlineData("deleted-product", ErrorCodes.InvalidParameter)]
    [InlineData("disabled-product", ErrorCodes.RuleConflict)]
    public async Task 提交_商品在保存后失效_拒绝且状态保持待提交(string scenario, int expectedCode)
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        SeedNumberRule(db);

        var controller = Controller(db);
        var id = CreatedId(await controller.Create(Draft(Line(ProductA, 2m))));

        if (scenario == "deleted-product") SoftDeleteProduct(db, ProductA);
        if (scenario == "disabled-product") DisableProduct(db, ProductA);

        var error = await Assert.ThrowsAsync<BusinessException>(() => controller.Submit(id));

        Assert.Equal(expectedCode, error.Code);
        Assert.Equal(DocumentStatus.Pending, db.StockIns.AsNoTracking().Single(o => o.Id == id).Status);
        Assert.Empty(db.StockMovements);
        Assert.Empty(db.Stocks);
    }

    // ==================== 4. 修改：非法输入零改动，合法输入保留服务端身份 ====================

    [Theory]
    [InlineData("zero-quantity", ErrorCodes.InvalidParameter)]
    [InlineData("negative-quantity", ErrorCodes.InvalidParameter)]
    [InlineData("missing-product", ErrorCodes.InvalidParameter)]
    [InlineData("deleted-product", ErrorCodes.InvalidParameter)]
    [InlineData("disabled-product", ErrorCodes.RuleConflict)]
    [InlineData("unrepresentable-weight", ErrorCodes.InvalidParameter)]
    [InlineData("hidden-bad-row", ErrorCodes.InvalidParameter)]
    public async Task 修改_非法输入_拒绝且既有单据明细逐字段不变(string scenario, int expectedCode)
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        SeedNumberRule(db);

        var controller = Controller(db);
        var id = CreatedId(await controller.Create(Draft(Line(ProductA, 4m, weight: 1m))));
        var before = db.StockIns.AsNoTracking().Single(o => o.Id == id);
        var beforeDetail = db.StockInDetails.AsNoTracking().Single(d => d.StockInId == id);
        var beforeSequence = NumberSequence(db);

        if (scenario == "deleted-product") SoftDeleteProduct(db, ProductA);
        if (scenario == "disabled-product") DisableProduct(db, ProductA);

        var edit = scenario switch
        {
            "zero-quantity" => Draft(Line(ProductA, 0m)),
            "negative-quantity" => Draft(Line(ProductA, -3m)),
            "missing-product" => Draft(Line(MissingProduct, 3m)),
            "unrepresentable-weight" => Draft(Line(ProductA, 3m, weight: 0.005m)),
            "hidden-bad-row" => Draft(Line(ProductA, 3m), Line(ProductA, 0m, isDeleted: true)),
            _ => Draft(Line(ProductA, 3m))
        };

        var error = await Assert.ThrowsAsync<BusinessException>(() => controller.Update(id, edit));

        Assert.Equal(expectedCode, error.Code);

        var stored = db.StockIns.AsNoTracking().Single(o => o.Id == id);
        Assert.Equal(before.StockInNo, stored.StockInNo);
        Assert.Equal(before.Status, stored.Status);
        Assert.False(stored.IsDeleted);
        Assert.Equal(before.SupplierId, stored.SupplierId);
        Assert.Equal(before.WarehouseId, stored.WarehouseId);
        Assert.Equal(before.PurchaseOrderId, stored.PurchaseOrderId);
        Assert.Equal(before.Remark, stored.Remark);
        Assert.Equal(before.TotalQuantity, stored.TotalQuantity);
        Assert.Equal(before.TotalWeight, stored.TotalWeight);
        Assert.Equal(before.TotalVolume, stored.TotalVolume);
        Assert.Equal(beforeSequence, NumberSequence(db));

        var detail = db.StockInDetails.AsNoTracking().Single(d => d.StockInId == id);
        Assert.Equal(beforeDetail.Id, detail.Id);
        Assert.Equal(beforeDetail.ProductId, detail.ProductId);
        Assert.Equal(beforeDetail.Quantity, detail.Quantity);
        Assert.Equal(beforeDetail.Unit, detail.Unit);

        Assert.Empty(db.StockMovements);
        Assert.Empty(db.Stocks);
    }

    [Fact]
    public async Task 修改_合法输入_成功且不消耗新单号()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        SeedNumberRule(db);

        var controller = Controller(db);
        var id = CreatedId(await controller.Create(Draft(Line(ProductA, 4m, weight: 1m))));
        var number = db.StockIns.AsNoTracking().Single(o => o.Id == id).StockInNo;
        var sequence = NumberSequence(db);

        var result = await controller.Update(id, Draft(Line(ProductA, 7m, weight: 2.5m, volume: 1m)));

        Assert.IsType<OkObjectResult>(result);
        var stored = db.StockIns.AsNoTracking().Single(o => o.Id == id);
        Assert.Equal(number, stored.StockInNo);
        Assert.Equal(DocumentStatus.Pending, stored.Status);
        Assert.Equal(7m, stored.TotalQuantity);
        Assert.Equal(2.5m, stored.TotalWeight);
        Assert.Equal(1m, stored.TotalVolume);
        Assert.Equal(sequence, NumberSequence(db));

        var detail = db.StockInDetails.AsNoTracking().Single(d => d.StockInId == id);
        Assert.Equal(id, detail.StockInId);
        Assert.Equal(7m, detail.Quantity);
        Assert.Empty(db.StockMovements);
    }

    // ==================== 5. 审核：合法草稿过账且累计容量 / 取消释放口径不变 ====================

    [Fact]
    public async Task 审核_重复商品行_按累计数量过账且来源容量按基础单位累计()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        SeedNumberRule(db);
        var order = SeedApprovedOrder(db, "PO-504-ROWS", capacity: 10m);

        var controller = Controller(db);
        var id = CreatedId(await controller.Create(LinkedDraft(order.Id, Line(ProductA, 4m), Line(ProductA, 2m))));

        await controller.Submit(id);
        Assert.IsType<OkObjectResult>(await controller.Approve(id));

        Assert.Equal(6m, db.Stocks.Single(s => s.ProductId == ProductA).Quantity);
        Assert.Equal(2, db.StockMovements.Count(m => m.SourceDocId == id
            && m.MovementType == InventoryMovementType.PurchaseIn));
        Assert.Equal(DocumentStatus.Approved, db.StockIns.AsNoTracking().Single(o => o.Id == id).Status);

        // 来源剩余容量按基础单位累计：再来 5 个（6 + 5 > 10）必须被拒绝且零写入。
        var over = CreatedId(await controller.Create(LinkedDraft(order.Id, Line(ProductA, 5m))));
        await controller.Submit(over);

        var error = await Assert.ThrowsAsync<BusinessException>(() => controller.Approve(over));
        Assert.Equal(ErrorCodes.RuleConflict, error.Code);
        Assert.Contains("超过来源采购订单授权数量", error.Message);
        Assert.Equal(6m, db.Stocks.Single(s => s.ProductId == ProductA).Quantity);
        Assert.Empty(db.StockMovements.Where(m => m.SourceDocId == over));

        // 取消第一张释放额度：第二张可审核过账（既有 ERP-342 口径不变）。
        Assert.IsType<OkObjectResult>(await controller.Cancel(id));
        Assert.IsType<OkObjectResult>(await controller.Approve(over));
        Assert.Equal(5m, db.Stocks.Single(s => s.ProductId == ProductA).Quantity);
    }

    // ==================== 6. 历史取消 / 冲销路径不新增校验闸门 ====================

    [Fact]
    public async Task 取消_历史无流水已审核单据且商品已失效_仍可原路冲回()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        db.Stocks.Add(new Stock
        {
            WarehouseId = WarehouseA,
            ProductId = ProductA,
            Quantity = 10m,
            AvailableQuantity = 10m
        });
        db.SaveChanges();

        var legacy = new StockIn
        {
            StockInNo = "RK-504-LEGACY-CANCEL",
            StockInDate = DateTime.Today,
            SupplierId = SupplierA,
            WarehouseId = WarehouseA,
            TotalQuantity = 10m,
            Status = DocumentStatus.Approved,
            Details = new List<StockInDetail>
            {
                new() { ProductId = ProductA, ProductName = "商品A", Spec = "规格A", Unit = "PCS", Quantity = 10m }
            }
        };
        db.StockIns.Add(legacy);
        db.SaveChanges();

        // 历史取消 / 冲销路径不因商品已失效新增闸门（既有取消口径保持可用）。
        SoftDeleteProduct(db, ProductA);

        var controller = Controller(db);
        Assert.IsType<OkObjectResult>(await controller.Cancel(legacy.Id));

        Assert.Equal(0m, db.Stocks.Single().Quantity);
        Assert.Equal(DocumentStatus.Cancelled, db.StockIns.AsNoTracking().Single(o => o.Id == legacy.Id).Status);
        Assert.Empty(db.StockMovements);
    }

    // ==================== 7. 实际 EF 精度、规则文案与源码接线契约 ====================

    [Fact]
    public void EF实际精度_数量毛重体积与表头合计_与护栏常量同源()
    {
        var options = new DbContextOptionsBuilder<ErpDbContext>()
            .UseSqlServer(
                "Server=(localdb)\\NEWERP_AutoAcceptance;Database=NEWERP_AUTOTEST_MODEL_ONLY;Integrated Security=true")
            .Options;
        using var db = new ErpDbContext(options);
        var model = db.Model;

        Assert.Equal("decimal(18,2)",
            ColumnType(model, typeof(StockInDetail), nameof(StockInDetail.Quantity)));
        Assert.Equal("decimal(18,2)",
            ColumnType(model, typeof(StockInDetail), nameof(StockInDetail.Weight)));
        Assert.Equal("decimal(18,2)",
            ColumnType(model, typeof(StockInDetail), nameof(StockInDetail.Volume)));
        Assert.Equal("decimal(18,2)",
            ColumnType(model, typeof(StockIn), nameof(StockIn.TotalQuantity)));
        Assert.Equal("decimal(18,2)",
            ColumnType(model, typeof(StockIn), nameof(StockIn.TotalWeight)));
        Assert.Equal("decimal(18,2)",
            ColumnType(model, typeof(StockIn), nameof(StockIn.TotalVolume)));

        Assert.Equal(18, StockInAuthorizationRules.QuantityPrecision);
        Assert.Equal(2, StockInAuthorizationRules.QuantityScale);
        Assert.Equal(9_999_999_999_999_999.99m, StockInAuthorizationRules.MaxDecimal18Scale2);
        Assert.True(StockInAuthorizationRules.IsQuantityRepresentable(9_999_999_999_999_999.99m));
        Assert.True(StockInAuthorizationRules.IsQuantityRepresentable(1.05m));
        Assert.False(StockInAuthorizationRules.IsQuantityRepresentable(10_000_000_000_000_000m));
        Assert.False(StockInAuthorizationRules.IsQuantityRepresentable(1.005m));
    }

    [Fact]
    public void 规则文案_明确拒绝口径与价格边界()
    {
        var text = StockInAuthorizationRules.DraftGuardRuleText;
        Assert.False(string.IsNullOrWhiteSpace(text));
        Assert.Contains("DECIMAL(18,2)", text);
        Assert.Contains("IsDeleted=true", text);
        Assert.Contains("提交 / 审核要求至少一行有效明细", text);
        Assert.Contains("绝不臆造来源或改写已审核采购价", text);
    }

    // ==================== 辅助 ====================

    private static void SeedMaster(ErpDbContext db)
    {
        StockInTestAuthorization.SeedSupplier(db, SupplierA, "草稿校验供应商");
        StockInTestAuthorization.SeedWarehouse(db, WarehouseA, "草稿校验仓");
        StockInTestAuthorization.SeedProduct(db, ProductA, name: "商品A");
        StockInTestAuthorization.SeedProduct(db, ProductB, name: "商品B");
        StockInTestAuthorization.SeedProduct(db, PackageProduct, packageUnit: "BOX",
            unitsPerPackage: 12, name: "箱装商品");
    }

    private static void SeedNumberRule(ErpDbContext db)
    {
        db.SysDocumentNumberRules.Add(new SysDocumentNumberRule
        {
            DocumentType = DocumentType.StockIn,
            RuleCode = "RK-504",
            RuleName = "采购入库单号",
            Prefix = "RK",
            DateFormat = "yyyyMMdd",
            SerialLength = 4,
            CurrentSequence = 0
        });
        db.SaveChanges();
    }

    private static PurchaseOrder SeedApprovedOrder(ErpDbContext db, string orderNo, decimal capacity)
    {
        var order = new PurchaseOrder
        {
            OrderNo = orderNo,
            OrderDate = DateTime.Today.AddDays(-2),
            SupplierId = SupplierA,
            Currency = Currency.CNY,
            ExchangeRate = 1m,
            Status = DocumentStatus.Approved
        };
        db.PurchaseOrders.Add(order);
        db.SaveChanges();

        db.PurchaseOrderDetails.Add(new PurchaseOrderDetail
        {
            PurchaseOrderId = order.Id,
            ProductId = ProductA,
            ProductName = "商品A",
            Spec = "规格A",
            Unit = "PCS",
            Quantity = capacity,
            UnitPrice = 7.5m,
            Amount = capacity * 7.5m
        });
        db.SaveChanges();
        return order;
    }

    private static void SoftDeleteProduct(ErpDbContext db, long id)
    {
        var product = db.BaseProducts.Single(p => p.Id == id);
        product.IsDeleted = true;
        db.SaveChanges();
    }

    private static void DisableProduct(ErpDbContext db, long id)
    {
        var product = db.BaseProducts.Single(p => p.Id == id);
        product.Status = 0;
        db.SaveChanges();
    }

    private static StockInController Controller(ErpDbContext db)
        => StockInTestAuthorization.ForUser(db, StockInTestAuthorization.SeedPrivilegedInboundOperator(db));

    private static StockIn Draft(params StockInDetail[] lines)
        => new()
        {
            StockInDate = DateTime.Today,
            SupplierId = SupplierA,
            WarehouseId = WarehouseA,
            Remark = "ERP-504_TEST",
            Details = lines.ToList()
        };

    private static StockIn LinkedDraft(long purchaseOrderId, params StockInDetail[] lines)
        => new()
        {
            StockInDate = DateTime.Today,
            PurchaseOrderId = purchaseOrderId,
            SupplierId = SupplierA,
            WarehouseId = WarehouseA,
            Remark = "ERP-504_TEST_LINKED",
            Details = lines.ToList()
        };

    private static StockInDetail Line(long productId, decimal quantity, decimal weight = 0m,
        decimal volume = 0m, string unit = "PCS", bool isDeleted = false)
        => new()
        {
            ProductId = productId,
            ProductName = $"商品{productId}",
            Spec = "规格A",
            Unit = unit,
            Quantity = quantity,
            Weight = weight,
            Volume = volume,
            IsDeleted = isDeleted
        };

    private static long NumberSequence(ErpDbContext db)
        => db.SysDocumentNumberRules.AsNoTracking()
            .Where(r => r.DocumentType == DocumentType.StockIn)
            .Select(r => r.CurrentSequence)
            .FirstOrDefault();

    /// <summary>拒绝场景的统一断言：零单据、零明细、零库存、零流水、零单号消耗。</summary>
    private static void AssertRejectedWithNoSideEffects(ErpDbContext db)
    {
        Assert.Empty(db.StockIns);
        Assert.Empty(db.StockInDetails);
        Assert.Empty(db.Stocks);
        Assert.Empty(db.StockMovements);
        Assert.Equal(0L, NumberSequence(db));
    }

    private static long CreatedId(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var payload = Assert.IsType<ApiResponse<object>>(ok.Value).Data!;
        return (long)payload.GetType().GetProperty("Id")!.GetValue(payload)!;
    }

    private static string ColumnType(IModel model, Type entityType, string propertyName)
        => model.FindEntityType(entityType)!.FindProperty(propertyName)!.GetColumnType()!;

    private static string ReadSource(params string[] segments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "NEWERP.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine(directory!.FullName,
            Path.Combine(segments).Replace('/', Path.DirectorySeparatorChar)));
    }

    /// <summary>截取控制器源码中某个路由方法（签名 → 下一个成员文档注释之前）的正文。</summary>
    private static string MethodBody(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"未找到路由方法：{signature}");
        var end = source.IndexOf("\n    /// <summary>", start, StringComparison.Ordinal);
        return end < 0 ? source[start..] : source[start..end];
    }
}
