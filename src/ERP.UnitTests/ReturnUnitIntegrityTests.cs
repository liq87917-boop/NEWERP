using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-524 销售退货 / 采购退货单位完整性单元测试（内存库 + 真实 HTTP 身份 + 真实控制器）：
/// <list type="number">
/// <item>手工（未链接）与显式链接来源的退货草稿在<b>单号预约 / 明细整体替换之前</b>拒绝「显式非空且不在商品当前基础 / 装箱单位内」的单位；</item>
/// <item>保留空 / 空白单位回退与合法基础 / 装箱单位（大小写、首尾空白不敏感），装箱单位按当前正数装箱数<b>恰好折算一次</b>；</item>
/// <item>商品未维护可用基础 / 装箱单位（缺失 / 非法主数据）时受控拒绝，绝不臆造新单位；</item>
/// <item>提交 / 审核在既有单据 / 来源锁内复核仍然生效明细的单位资格，主数据被改写即整体拒绝；</item>
/// <item>链接来源按折算后的基础单位累计容量、不重复折算，超退在审核前拒绝且状态 / 库存 / 来源容量不变；</item>
/// <item>每个拒绝场景断言零单据 / 零明细 / 零库存流水 / 零单号消耗。</item>
/// </list>
/// </summary>
public class ReturnUnitIntegrityTests
{
    private const long CustomerA = 988001L;
    private const long SupplierA = 988002L;
    private const long WarehouseA = 988101L;
    private const long ProductA = 988201L;            // 基础 PCS / 装箱 CTN(12)
    private const long PlainProduct = 988202L;        // 仅基础 PCS
    private const long NoUnitProduct = 988203L;       // 历史商品：基础 / 装箱单位均未维护
    private const long NoBaseWithPackage = 988204L;   // 只维护装箱单位、缺基础单位
    private const long BadFactorProduct = 988205L;    // 装箱数非法（0）
    private const long SourceStockOutId = 988301L;
    private const long SourceStockInId = 988302L;
    private const string SourceStockOutNo = "CK-524-LINK";
    private const string SourceStockInNo = "RK-524-LINK";

    // ==================== 1. 纯判定：共享口径与退货文案 ====================

    [Theory]
    [InlineData("", "PCS", "CTN", true)]          // 空单位：既有回退
    [InlineData("   ", "PCS", "CTN", true)]       // 空白单位：既有回退
    [InlineData("PCS", "PCS", "CTN", true)]       // 基础单位
    [InlineData(" pcs ", "PCS", "CTN", true)]     // 首尾空白 + 大小写
    [InlineData("CTN", "PCS", "CTN", true)]       // 装箱单位
    [InlineData(" Ctn ", "PCS", "CTN", true)]
    [InlineData("KG", "PCS", "CTN", false)]       // 任意显式非空单位：拒绝
    [InlineData("SET", "PCS", "CTN", false)]
    [InlineData("工程量", "PCS", "CTN", false)]
    [InlineData("PCS", "", "", false)]            // 主数据未维护任何单位：拒绝，绝不臆造
    [InlineData("CTN", "", "CTN", true)]
    public void 单位资格纯判定_退货与入库出库同口径(string? lineUnit, string? baseUnit, string? packageUnit,
        bool expected)
        => Assert.Equal(expected, StockUnitConversion.IsUnitEligible(lineUnit, baseUnit, packageUnit));

    [Fact]
    public void 退货单位资格文案_与既有共享口径同源()
    {
        Assert.Contains("销售退货", StockUnitConversion.ReturnUnitEligibilityRuleText);
        Assert.Contains("采购退货", StockUnitConversion.ReturnUnitEligibilityRuleText);
        Assert.Contains("UnitsPerPackage", StockUnitConversion.ReturnUnitEligibilityRuleText);
        Assert.Contains("空单位", StockUnitConversion.ReturnUnitEligibilityRuleText);
        Assert.Contains("任意", StockUnitConversion.ReturnUnitEligibilityRuleText);
        // 采购入库 / 销售出库口径保持 ERP-522 / ERP-523 原样，不被改写。
        Assert.Contains("采购入库", StockUnitConversion.UnitEligibilityRuleText);
        Assert.Contains("销售出库", StockUnitConversion.StockOutUnitEligibilityRuleText);
    }

    [Fact]
    public void 共享批量复核_退货明细越界单位受控拒绝()
    {
        var products = new Dictionary<long, BaseProduct>
        {
            [ProductA] = new() { Id = ProductA, ProductName = "箱装商品", Unit = "PCS", PackageUnit = "CTN" }
        };

        StockUnitConversion.EnsureUnitsEligible(products,
            new[] { new SalesReturnDetail { ProductId = ProductA, Unit = " PCS ", Quantity = 1m } });
        StockUnitConversion.EnsureUnitsEligible(products,
            new[] { new PurchaseReturnDetail { ProductId = ProductA, Unit = "CTN", Quantity = 1m } });

        var salesError = Assert.Throws<BusinessException>(() => StockUnitConversion.EnsureUnitsEligible(products,
            new[] { new SalesReturnDetail { ProductId = ProductA, Unit = "KG", Quantity = 1m } }));
        Assert.Equal(ErrorCodes.RuleConflict, salesError.Code);
        Assert.Contains("不在当前有效单位口径内", salesError.Message);

        var purchaseError = Assert.Throws<BusinessException>(() => StockUnitConversion.EnsureUnitsEligible(products,
            new[] { new PurchaseReturnDetail { ProductId = ProductA, Unit = "SET", Quantity = 1m } }));
        Assert.Equal(ErrorCodes.RuleConflict, purchaseError.Code);
        Assert.Contains("不在当前有效单位口径内", purchaseError.Message);
    }

    // ==================== 2. 销售退货：显式越界单位在创建即拒绝（零副作用） ====================

    [Theory]
    [InlineData("KG")]
    [InlineData("kg")]
    [InlineData(" SET ")]
    [InlineData("工程量")]
    public async Task 销售退货_创建_未链接_显式越界单位_拒绝且零副作用(string unit)
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);

        var error = await Assert.ThrowsAsync<BusinessException>(
            () => SalesOperator(db).Create(SalesDraft(null, SalesLine(ProductA, 1m, unit))));

        Assert.Equal(ErrorCodes.RuleConflict, error.Code);
        Assert.Contains("不在当前有效单位口径内", error.Message);
        AssertSalesRejectedWithNoSideEffects(db);
    }

    [Fact]
    public async Task 销售退货_创建_链接来源_显式越界单位_在来源校验前拒绝且来源不变()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        var shipment = SalesReturnTestAuthorization.SeedApprovedStockOut(db, SourceStockOutId, SourceStockOutNo,
            CustomerA, WarehouseA, (ProductA, "PCS", 24m));

        var error = await Assert.ThrowsAsync<BusinessException>(
            () => SalesOperator(db).Create(SalesDraft(shipment.Id, SalesLine(ProductA, 1m, "KG"))));

        Assert.Equal(ErrorCodes.RuleConflict, error.Code);
        Assert.Contains("不在当前有效单位口径内", error.Message);
        AssertSalesRejectedWithNoSideEffects(db);

        var storedShipment = db.StockOuts.AsNoTracking().Single(o => o.Id == shipment.Id);
        Assert.Equal(DocumentStatus.Approved, storedShipment.Status);
        Assert.Equal(24m, db.StockOutDetails.AsNoTracking().Single(d => d.StockOutId == shipment.Id).Quantity);
    }

    [Fact]
    public async Task 销售退货_创建_合法行与越界行混合_整体拒绝且零副作用()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);

        var error = await Assert.ThrowsAsync<BusinessException>(() => SalesOperator(db).Create(
            SalesDraft(null, SalesLine(ProductA, 2m, "PCS"), SalesLine(PlainProduct, 1m, "KG"))));

        Assert.Equal(ErrorCodes.RuleConflict, error.Code);
        Assert.Contains("不在当前有效单位口径内", error.Message);
        AssertSalesRejectedWithNoSideEffects(db);
    }

    // ==================== 3. 采购退货：显式越界单位在创建即拒绝（零副作用） ====================

    [Theory]
    [InlineData("KG", false)]
    [InlineData("kg", false)]
    [InlineData("KG", true)]
    [InlineData("工程量", true)]
    public async Task 采购退货_创建_显式越界单位_拒绝且零副作用(string unit, bool linked)
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        if (linked)
            PurchaseReturnTestAuthorization.SeedApprovedStockIn(db, SourceStockInId, SourceStockInNo, SupplierA,
                WarehouseA, (ProductA, "PCS", 24m));

        var error = await Assert.ThrowsAsync<BusinessException>(() => PurchaseOperator(db).Create(
            PurchaseDraft(linked ? SourceStockInId : null, PurchaseLine(ProductA, 1m, unit))));

        Assert.Equal(ErrorCodes.RuleConflict, error.Code);
        Assert.Contains("不在当前有效单位口径内", error.Message);
        AssertPurchaseRejectedWithNoSideEffects(db);
    }

    // ==================== 4. 非法 / 缺失主数据：受控拒绝，绝不臆造单位 ====================

    [Fact]
    public async Task 销售退货_创建_商品未维护任何单位_显式非空单位_受控拒绝不臆造单位()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);

        var error = await Assert.ThrowsAsync<BusinessException>(() => SalesOperator(db).Create(
            SalesDraft(null, SalesLine(NoUnitProduct, 1m, "PCS"))));

        Assert.Equal(ErrorCodes.RuleConflict, error.Code);
        Assert.Contains("不在当前有效单位口径内", error.Message);
        Assert.Contains("未设置", error.Message);
        AssertSalesRejectedWithNoSideEffects(db);
    }

    [Theory]
    [InlineData(true, "PCS", "不在当前有效单位口径内")]
    [InlineData(true, "CTN", "未设置基础单位")]
    [InlineData(false, "PCS", "不在当前有效单位口径内")]
    public async Task 退货_创建_只维护装箱单位缺基础单位_受控拒绝(bool sales, string unit, string expected)
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);

        var error = await Assert.ThrowsAsync<BusinessException>(() => sales
            ? SalesOperator(db).Create(SalesDraft(null, SalesLine(NoBaseWithPackage, 1m, unit)))
            : PurchaseOperator(db).Create(PurchaseDraft(null, PurchaseLine(NoBaseWithPackage, 1m, unit))));

        Assert.Equal(ErrorCodes.RuleConflict, error.Code);
        Assert.Contains(expected, error.Message);
        if (sales) AssertSalesRejectedWithNoSideEffects(db);
        else AssertPurchaseRejectedWithNoSideEffects(db);
    }

    [Theory]
    [InlineData(true, "CTN", "必须大于 0")]
    [InlineData(true, "KG", "不在当前有效单位口径内")]
    [InlineData(false, "CTN", "必须大于 0")]
    public async Task 退货_创建_装箱数非法_受控拒绝(bool sales, string unit, string expected)
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);

        var error = await Assert.ThrowsAsync<BusinessException>(() => sales
            ? SalesOperator(db).Create(SalesDraft(null, SalesLine(BadFactorProduct, 1m, unit)))
            : PurchaseOperator(db).Create(PurchaseDraft(null, PurchaseLine(BadFactorProduct, 1m, unit))));

        Assert.Equal(ErrorCodes.RuleConflict, error.Code);
        Assert.Contains(expected, error.Message);
        if (sales) AssertSalesRejectedWithNoSideEffects(db);
        else AssertPurchaseRejectedWithNoSideEffects(db);
    }

    // ==================== 5. 支持口径：空白回退 / 基础单位 / 装箱折算恰好一次 ====================

    [Theory]
    [InlineData(true, "CTN", 24)]
    [InlineData(true, "ctn", 24)]
    [InlineData(false, " Ctn ", 24)]
    public async Task 退货_创建_装箱单位大小写与空白_折算一次为基础单位(bool sales, string unit,
        decimal expectedQuantity)
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);

        if (sales)
        {
            await SalesOperator(db).Create(SalesDraft(null, SalesLine(ProductA, 2m, unit)));
            var detail = db.SalesReturnDetails.AsNoTracking().Single();
            Assert.Equal("PCS", detail.Unit);
            Assert.Equal(expectedQuantity, detail.Quantity);
            Assert.Equal(expectedQuantity, db.SalesReturns.Single().TotalQuantity);
        }
        else
        {
            await PurchaseOperator(db).Create(PurchaseDraft(null, PurchaseLine(ProductA, 2m, unit)));
            var detail = db.PurchaseReturnDetails.AsNoTracking().Single();
            Assert.Equal("PCS", detail.Unit);
            Assert.Equal(expectedQuantity, detail.Quantity);
            Assert.Equal(expectedQuantity, db.PurchaseReturns.Single().TotalQuantity);
        }
    }

    [Theory]
    [InlineData(true, "PCS", 2)]
    [InlineData(true, " pcs ", 2)]
    [InlineData(true, "", 3.5)]
    [InlineData(true, "   ", 2)]
    [InlineData(false, "PCS", 2)]
    [InlineData(false, " pcs ", 2)]
    [InlineData(false, "", 3.5)]
    public async Task 退货_创建_基础单位与空白单位_保留既有回退不折算(bool sales, string unit, decimal quantity)
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);

        if (sales)
        {
            await SalesOperator(db).Create(SalesDraft(null, SalesLine(ProductA, quantity, unit)));
            var detail = db.SalesReturnDetails.AsNoTracking().Single();
            Assert.Equal(unit, detail.Unit);
            Assert.Equal(quantity, detail.Quantity);
            Assert.Equal(quantity, db.SalesReturns.Single().TotalQuantity);
        }
        else
        {
            await PurchaseOperator(db).Create(PurchaseDraft(null, PurchaseLine(ProductA, quantity, unit)));
            var detail = db.PurchaseReturnDetails.AsNoTracking().Single();
            Assert.Equal(unit, detail.Unit);
            Assert.Equal(quantity, detail.Quantity);
            Assert.Equal(quantity, db.PurchaseReturns.Single().TotalQuantity);
        }
    }

    [Fact]
    public async Task 销售退货_创建_重复商品行_按行折算且累计语义保留()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);

        await SalesOperator(db).Create(SalesDraft(null,
            SalesLine(ProductA, 2m, "CTN"),     // 24 PCS
            SalesLine(ProductA, 3m, "PCS")));   // 3 PCS

        var saved = db.SalesReturns.Include(o => o.Details).Single();
        Assert.Equal(2, saved.Details.Count);
        Assert.Equal(24m, saved.Details.Single(d => d.Quantity == 24m).Quantity);
        Assert.Equal(3m, saved.Details.Single(d => d.Quantity == 3m).Quantity);
        Assert.Equal(27m, saved.TotalQuantity);
        Assert.All(saved.Details, d => Assert.Equal("PCS", d.Unit));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task 退货_创建_装箱换算溢出_受控拒绝不静默截断(bool sales)
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        const decimal huge = 10_000_000_000_000_000_000_000_000_000m;

        var error = await Assert.ThrowsAsync<BusinessException>(() => sales
            ? SalesOperator(db).Create(SalesDraft(null, SalesLine(ProductA, huge, "CTN")))
            : PurchaseOperator(db).Create(PurchaseDraft(null, PurchaseLine(ProductA, huge, "CTN"))));

        Assert.Equal(ErrorCodes.InvalidParameter, error.Code);
        Assert.Contains("超出可表示范围", error.Message);
        if (sales) AssertSalesRejectedWithNoSideEffects(db);
        else AssertPurchaseRejectedWithNoSideEffects(db);
    }

    // ==================== 6. 修改：越界单位在明细整体替换前拒绝 ====================

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task 退货_修改_显式越界单位_在明细整体替换前拒绝且既有单据逐字段不变(bool sales)
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);

        if (sales)
        {
            var ctl = SalesOperator(db);
            var id = CreatedId(await ctl.Create(SalesDraft(null, SalesLine(ProductA, 2m, "PCS"))));
            var originalDetailId = db.SalesReturnDetails.AsNoTracking().Single().Id;

            var error = await Assert.ThrowsAsync<BusinessException>(
                () => ctl.Update(id, SalesDraft(null, SalesLine(ProductA, 1m, "KG"))));

            Assert.Equal(ErrorCodes.RuleConflict, error.Code);
            Assert.Contains("不在当前有效单位口径内", error.Message);

            var reloaded = db.SalesReturns.Include(o => o.Details).AsNoTracking().Single();
            Assert.Equal(2m, reloaded.TotalQuantity);
            Assert.Equal(2m, reloaded.Details.Single().Quantity);
            Assert.Equal("PCS", reloaded.Details.Single().Unit);
            Assert.Equal(originalDetailId, reloaded.Details.Single().Id);
            Assert.Equal(DocumentStatus.Pending, reloaded.Status);
            Assert.Empty(db.StockMovements);
        }
        else
        {
            var ctl = PurchaseOperator(db);
            var id = CreatedId(await ctl.Create(PurchaseDraft(null, PurchaseLine(ProductA, 2m, "PCS"))));
            var originalDetailId = db.PurchaseReturnDetails.AsNoTracking().Single().Id;

            var error = await Assert.ThrowsAsync<BusinessException>(
                () => ctl.Update(id, PurchaseDraft(null, PurchaseLine(ProductA, 1m, "KG"))));

            Assert.Equal(ErrorCodes.RuleConflict, error.Code);
            Assert.Contains("不在当前有效单位口径内", error.Message);

            var reloaded = db.PurchaseReturns.Include(o => o.Details).AsNoTracking().Single();
            Assert.Equal(2m, reloaded.TotalQuantity);
            Assert.Equal(2m, reloaded.Details.Single().Quantity);
            Assert.Equal("PCS", reloaded.Details.Single().Unit);
            Assert.Equal(originalDetailId, reloaded.Details.Single().Id);
            Assert.Equal(DocumentStatus.Pending, reloaded.Status);
            Assert.Empty(db.StockMovements);
        }
    }

    // ==================== 7. 提交 / 审核承诺边界：复核仍然生效的单位资格 ====================

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task 退货_提交_已存显式越界单位_拒绝且状态与库存流水不变(bool sales)
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        var id = sales
            ? SeedSalesReturn(db, "XTH-524-SUB-KG", DocumentStatus.Pending, ProductA, "KG", 1m)
            : SeedPurchaseReturn(db, "CTH-524-SUB-KG", DocumentStatus.Pending, ProductA, "KG", 1m);

        var error = sales
            ? await Assert.ThrowsAsync<BusinessException>(() => SalesOperator(db).Submit(id))
            : await Assert.ThrowsAsync<BusinessException>(() => PurchaseOperator(db).Submit(id));

        Assert.Equal(ErrorCodes.RuleConflict, error.Code);
        Assert.Contains("不在当前有效单位口径内", error.Message);
        Assert.Equal(DocumentStatus.Pending, sales
            ? db.SalesReturns.Single(o => o.Id == id).Status
            : db.PurchaseReturns.Single(o => o.Id == id).Status);
        Assert.Empty(db.StockMovements);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task 退货_提交_商品单位主数据被改写_已存单位失效_受控拒绝(bool sales, bool clearUnits)
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        var id = sales
            ? CreatedId(await SalesOperator(db).Create(SalesDraft(null, SalesLine(ProductA, 2m, "PCS"))))
            : CreatedId(await PurchaseOperator(db).Create(PurchaseDraft(null, PurchaseLine(ProductA, 2m, "PCS"))));

        var product = db.BaseProducts.Single(p => p.Id == ProductA);
        product.Unit = clearUnits ? string.Empty : "BOX";
        product.PackageUnit = clearUnits ? string.Empty : "CTN";
        db.SaveChanges();

        var error = sales
            ? await Assert.ThrowsAsync<BusinessException>(() => SalesOperator(db).Submit(id))
            : await Assert.ThrowsAsync<BusinessException>(() => PurchaseOperator(db).Submit(id));

        Assert.Equal(ErrorCodes.RuleConflict, error.Code);
        Assert.Contains("不在当前有效单位口径内", error.Message);
        Assert.Equal(DocumentStatus.Pending, sales
            ? db.SalesReturns.Single(o => o.Id == id).Status
            : db.PurchaseReturns.Single(o => o.Id == id).Status);
        Assert.Empty(db.StockMovements);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task 退货_审核_已存显式越界单位_锁内拒绝且零库存流水状态不变(bool sales)
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        if (!sales) SeedStock(db, ProductA, 50m);
        var id = sales
            ? SeedSalesReturn(db, "XTH-524-APP-KG", DocumentStatus.Submitted, ProductA, "KG", 1m)
            : SeedPurchaseReturn(db, "CTH-524-APP-KG", DocumentStatus.Submitted, ProductA, "KG", 1m);

        var error = sales
            ? await Assert.ThrowsAsync<BusinessException>(() => SalesOperator(db).Approve(id))
            : await Assert.ThrowsAsync<BusinessException>(() => PurchaseOperator(db).Approve(id));

        Assert.Equal(ErrorCodes.RuleConflict, error.Code);
        Assert.Contains("不在当前有效单位口径内", error.Message);
        Assert.Equal(DocumentStatus.Submitted, sales
            ? db.SalesReturns.Single(o => o.Id == id).Status
            : db.PurchaseReturns.Single(o => o.Id == id).Status);
        Assert.Empty(db.StockMovements);
        if (sales) Assert.Empty(db.Stocks);
        else Assert.Equal(50m, db.Stocks.Single(s => s.ProductId == ProductA).Quantity);
    }

    // ==================== 8. 链接来源：折算后按基础单位累计容量且不重复折算 ====================

    [Fact]
    public async Task 销售退货_链接来源_折算一次且按基础单位累计容量()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        SalesReturnTestAuthorization.SeedApprovedStockOut(db, SourceStockOutId, SourceStockOutNo, CustomerA,
            WarehouseA, (ProductA, "PCS", 24m));
        var ctl = SalesOperator(db);

        var firstId = CreatedId(await ctl.Create(SalesDraft(SourceStockOutId, SalesLine(ProductA, 2m, "CTN"))));
        await ctl.Submit(firstId);
        await ctl.Approve(firstId);

        var firstDetail = db.SalesReturnDetails.AsNoTracking().Single(d => d.SalesReturnId == firstId);
        Assert.Equal(24m, firstDetail.Quantity);            // 2 CTN → 24 PCS，恰好一次
        Assert.Equal("PCS", firstDetail.Unit);
        Assert.Equal(24m, db.StockMovements.AsNoTracking()
            .Single(m => m.SourceDocId == firstId).Quantity);   // 承诺边界不重复折算（不是 288）
        Assert.Equal(24m, db.Stocks.Single(s => s.ProductId == ProductA).Quantity);

        // 第二张：来源可退容量 24 已被占满，再退 1 CTN（12 PCS）在审核时按累计容量拒绝。
        var secondId = CreatedId(await ctl.Create(SalesDraft(SourceStockOutId, SalesLine(ProductA, 1m, "CTN"))));
        await ctl.Submit(secondId);
        var over = await Assert.ThrowsAsync<BusinessException>(() => ctl.Approve(secondId));
        Assert.Equal(ErrorCodes.RuleConflict, over.Code);
        Assert.Equal(24m, db.Stocks.Single(s => s.ProductId == ProductA).Quantity);
        Assert.Equal(DocumentStatus.Submitted, db.SalesReturns.Single(o => o.Id == secondId).Status);

        await ctl.Unaudit(firstId);                             // 销审按红字流水冲销，库存回到基线并释放可退容量
        Assert.Equal(0m, db.Stocks.Single(s => s.ProductId == ProductA).Quantity);
        await ctl.Approve(secondId);                            // 1 CTN = 12 PCS
        Assert.Equal(12m, db.Stocks.Single(s => s.ProductId == ProductA).Quantity);
    }

    [Fact]
    public async Task 采购退货_链接来源_折算一次且按基础单位累计容量()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        PurchaseReturnTestAuthorization.SeedApprovedStockIn(db, SourceStockInId, SourceStockInNo, SupplierA,
            WarehouseA, (ProductA, "PCS", 24m));
        SeedStock(db, ProductA, 100m);
        var ctl = PurchaseOperator(db);

        var firstId = CreatedId(await ctl.Create(PurchaseDraft(SourceStockInId, PurchaseLine(ProductA, 2m, "CTN"))));
        await ctl.Submit(firstId);
        await ctl.Approve(firstId);

        var firstDetail = db.PurchaseReturnDetails.AsNoTracking().Single(d => d.PurchaseReturnId == firstId);
        Assert.Equal(24m, firstDetail.Quantity);
        Assert.Equal("PCS", firstDetail.Unit);
        Assert.Equal(24m, db.StockMovements.AsNoTracking().Single(m => m.SourceDocId == firstId).Quantity);
        Assert.Equal(76m, db.Stocks.Single(s => s.ProductId == ProductA).Quantity);

        var secondId = CreatedId(await ctl.Create(PurchaseDraft(SourceStockInId, PurchaseLine(ProductA, 1m, "CTN"))));
        await ctl.Submit(secondId);
        var over = await Assert.ThrowsAsync<BusinessException>(() => ctl.Approve(secondId));
        Assert.Equal(ErrorCodes.RuleConflict, over.Code);
        Assert.Equal(76m, db.Stocks.Single(s => s.ProductId == ProductA).Quantity);
        Assert.Equal(DocumentStatus.Submitted, db.PurchaseReturns.Single(o => o.Id == secondId).Status);

        await ctl.Unaudit(firstId);
        await ctl.Approve(secondId);
        Assert.Equal(88m, db.Stocks.Single(s => s.ProductId == ProductA).Quantity);
    }

    // ==================== 9. 接线契约 ====================

    [Fact]
    public void 接线契约_退货草稿与承诺边界启用单位资格且不改变入库出库策略()
    {
        foreach (var path in new[]
                 {
                     "src/ERP.Api/Controllers/SalesReturnController.cs",
                     "src/ERP.Api/Controllers/PurchaseReturnController.cs"
                 })
        {
            var controller = ReadSource(path);
            Assert.Equal(2, Occurrences(controller, "NormalizeAsync(Db, entity.Details, ensureUnitEligible: true)"));
            Assert.Equal(2, Occurrences(controller,
                "EnsureUnitsEligibleAsync(Db, entity.Details.Where(d => !d.IsDeleted))"));

            var createStart = controller.IndexOf("public async Task<IActionResult> Create", StringComparison.Ordinal);
            var updateStart = controller.IndexOf("public async Task<IActionResult> Update", StringComparison.Ordinal);
            Assert.True(createStart >= 0 && updateStart > createStart);
            var create = controller[createStart..updateStart];
            Assert.True(create.IndexOf("NormalizeAsync(Db, entity.Details, ensureUnitEligible: true)",
                            StringComparison.Ordinal)
                        < create.IndexOf("EnsureDraftValidAsync", StringComparison.Ordinal));      // 资格判定 + 折算先于草稿护栏
            Assert.True(create.IndexOf("EnsureDraftValidAsync", StringComparison.Ordinal)
                        < create.IndexOf("_noService.GenerateAsync", StringComparison.Ordinal));  // 再先于单号预约与写入
        }

        var conversion = ReadSource("src/ERP.Api/Controllers/StockUnitConversion.cs");
        Assert.Contains("ReturnUnitEligibilityRuleText", conversion);
        // 未启用单位资格的重载（旧 2 参入库 / 出库 / 退货重载）保持 4 处；ERP-524 仅让退货控制器改用显式启用重载。
        Assert.Equal(4, Occurrences(conversion, "ensureUnitEligible: false"));
        // 采购入库 / 销售出库策略保持 ERP-522 / ERP-523 原样。
        Assert.Contains("采购入库", StockUnitConversion.UnitEligibilityRuleText);
        Assert.Contains("销售出库", StockUnitConversion.StockOutUnitEligibilityRuleText);
    }

    // ==================== 辅助 ====================

    private static SalesReturnController SalesOperator(ErpDbContext db)
        => SalesReturnTestAuthorization.ForUser(db,
            SalesReturnTestAuthorization.SeedAuthorizedSalesman(db, CustomerA));

    private static PurchaseReturnController PurchaseOperator(ErpDbContext db)
        => PurchaseReturnTestAuthorization.ForUser(db,
            PurchaseReturnTestAuthorization.SeedAuthorizedOperator(db));

    private static void SeedMaster(ErpDbContext db)
    {
        SalesReturnTestAuthorization.SeedWarehouse(db, WarehouseA, "退货仓A");
        PurchaseReturnTestAuthorization.SeedSupplier(db, SupplierA, "供应商A");
        SalesReturnTestAuthorization.SeedProduct(db, ProductA, "RET-524-A", "PCS", "CTN", 12);
        SalesReturnTestAuthorization.SeedProduct(db, PlainProduct, "RET-524-P", "PCS");
        SalesReturnTestAuthorization.SeedProduct(db, NoUnitProduct, "RET-524-N", string.Empty);
        SalesReturnTestAuthorization.SeedProduct(db, NoBaseWithPackage, "RET-524-B", string.Empty, "CTN", 12);
        SalesReturnTestAuthorization.SeedProduct(db, BadFactorProduct, "RET-524-F", "PCS", "CTN", 0);
        SeedNumberRules(db);
    }

    private static void SeedNumberRules(ErpDbContext db)
    {
        db.SysDocumentNumberRules.Add(new SysDocumentNumberRule
        {
            DocumentType = DocumentType.SalesReturn,
            RuleCode = "SR-524",
            RuleName = "销售退货单",
            Prefix = "XTH",
            DateFormat = "yyyyMMdd",
            SerialLength = 4,
            Separator = string.Empty,
            CurrentSequence = 0
        });
        db.SysDocumentNumberRules.Add(new SysDocumentNumberRule
        {
            DocumentType = DocumentType.PurchaseReturn,
            RuleCode = "PR-524",
            RuleName = "采购退货单",
            Prefix = "CTH",
            DateFormat = "yyyyMMdd",
            SerialLength = 4,
            Separator = string.Empty,
            CurrentSequence = 0
        });
        db.SaveChanges();
    }

    private static SalesReturn SalesDraft(long? sourceStockOutId, params SalesReturnDetail[] lines)
        => new()
        {
            ReturnDate = DateTime.Today,
            CustomerId = CustomerA,
            CustomerName = "客户A",
            WarehouseId = WarehouseA,
            SourceStockOutId = sourceStockOutId,
            SourceStockOutNo = string.Empty,
            ReturnReason = "质量",
            Remark = "ERP-524_TEST",
            Details = lines.ToList()
        };

    private static SalesReturnDetail SalesLine(long productId, decimal quantity, string unit,
        decimal unitPrice = 10m, decimal unitCost = 5m)
        => new()
        {
            ProductId = productId,
            ProductName = $"商品{productId}",
            Spec = "规格A",
            Unit = unit,
            Quantity = quantity,
            UnitPrice = unitPrice,
            UnitCost = unitCost
        };

    private static PurchaseReturn PurchaseDraft(long? sourceStockInId, params PurchaseReturnDetail[] lines)
        => new()
        {
            ReturnDate = DateTime.Today,
            SupplierId = SupplierA,
            SupplierName = "供应商A",
            WarehouseId = WarehouseA,
            SourceStockInId = sourceStockInId,
            SourceStockInNo = string.Empty,
            ReturnReason = "规格不符",
            Remark = "ERP-524_TEST",
            Details = lines.ToList()
        };

    private static PurchaseReturnDetail PurchaseLine(long productId, decimal quantity, string unit,
        decimal unitPrice = 10m, decimal unitCost = 5m)
        => new()
        {
            ProductId = productId,
            ProductName = $"商品{productId}",
            Spec = "规格A",
            Unit = unit,
            Quantity = quantity,
            UnitPrice = unitPrice,
            UnitCost = unitCost
        };

    /// <summary>直接播种一张退货单（绕过 Create），用于提交 / 审核承诺边界复核场景。</summary>
    private static long SeedSalesReturn(ErpDbContext db, string no, DocumentStatus status, long productId,
        string unit, decimal quantity)
    {
        var document = new SalesReturn
        {
            ReturnNo = no,
            ReturnDate = DateTime.Today,
            CustomerId = CustomerA,
            CustomerName = "客户A",
            WarehouseId = WarehouseA,
            Status = status,
            TotalQuantity = quantity
        };
        db.SalesReturns.Add(document);
        db.SaveChanges();

        db.SalesReturnDetails.Add(new SalesReturnDetail
        {
            SalesReturnId = document.Id,
            ReturnNo = document.ReturnNo,
            SortNo = 1,
            ProductId = productId,
            ProductName = "商品",
            Spec = "规格A",
            Unit = unit,
            Quantity = quantity,
            UnitPrice = 10m,
            Amount = quantity * 10m,
            UnitCost = 5m
        });
        db.SaveChanges();
        return document.Id;
    }

    private static long SeedPurchaseReturn(ErpDbContext db, string no, DocumentStatus status, long productId,
        string unit, decimal quantity)
    {
        var document = new PurchaseReturn
        {
            ReturnNo = no,
            ReturnDate = DateTime.Today,
            SupplierId = SupplierA,
            SupplierName = "供应商A",
            WarehouseId = WarehouseA,
            Status = status,
            TotalQuantity = quantity
        };
        db.PurchaseReturns.Add(document);
        db.SaveChanges();

        db.PurchaseReturnDetails.Add(new PurchaseReturnDetail
        {
            PurchaseReturnId = document.Id,
            ReturnNo = document.ReturnNo,
            SortNo = 1,
            ProductId = productId,
            ProductName = "商品",
            Spec = "规格A",
            Unit = unit,
            Quantity = quantity,
            UnitPrice = 10m,
            Amount = quantity * 10m,
            UnitCost = 5m
        });
        db.SaveChanges();
        return document.Id;
    }

    private static void SeedStock(ErpDbContext db, long productId, decimal quantity)
    {
        db.Stocks.Add(new Stock
        {
            WarehouseId = WarehouseA,
            ProductId = productId,
            Quantity = quantity,
            AvailableQuantity = quantity,
            TotalCost = quantity * 6m,
            AverageCost = 6m
        });
        db.SaveChanges();
    }

    private static long CreatedId(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var payload = Assert.IsType<ApiResponse<object>>(ok.Value).Data!;
        return (long)payload.GetType().GetProperty("Id")!.GetValue(payload)!;
    }

    private static long SalesSequence(ErpDbContext db)
        => db.SysDocumentNumberRules.AsNoTracking()
            .Where(r => r.DocumentType == DocumentType.SalesReturn)
            .Select(r => r.CurrentSequence)
            .FirstOrDefault();

    private static long PurchaseSequence(ErpDbContext db)
        => db.SysDocumentNumberRules.AsNoTracking()
            .Where(r => r.DocumentType == DocumentType.PurchaseReturn)
            .Select(r => r.CurrentSequence)
            .FirstOrDefault();

    /// <summary>销售退货拒绝场景的统一断言：零单据、零明细、零库存流水、零单号消耗。</summary>
    private static void AssertSalesRejectedWithNoSideEffects(ErpDbContext db)
    {
        Assert.Empty(db.SalesReturns);
        Assert.Empty(db.SalesReturnDetails);
        Assert.Empty(db.StockMovements);
        Assert.Equal(0L, SalesSequence(db));
    }

    /// <summary>采购退货拒绝场景的统一断言：零单据、零明细、零库存流水、零单号消耗。</summary>
    private static void AssertPurchaseRejectedWithNoSideEffects(ErpDbContext db)
    {
        Assert.Empty(db.PurchaseReturns);
        Assert.Empty(db.PurchaseReturnDetails);
        Assert.Empty(db.StockMovements);
        Assert.Equal(0L, PurchaseSequence(db));
    }

    private static int Occurrences(string text, string value)
    {
        var count = 0;
        var index = text.IndexOf(value, StringComparison.Ordinal);
        while (index >= 0)
        {
            count++;
            index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal);
        }
        return count;
    }

    private static string ReadSource(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "NEWERP.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine(directory!.FullName,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
    }
}
