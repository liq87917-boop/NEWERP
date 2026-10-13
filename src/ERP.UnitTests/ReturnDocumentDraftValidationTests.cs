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
/// ERP-503 销售退货 / 采购退货草稿写入校验单元测试（内存库 + 真实 HTTP 身份）：
/// <list type="number">
/// <item>创建与修改在单号预约 / 明细整体替换之前，<b>无论是否显式链接来源</b>，拒绝「仓库 Id 非正 / 不存在 /
/// 已软删除 / 已停用」与「数量非正或不可表示、单价为负或不可表示、成本为负或不可表示、
/// 商品 Id 缺失 / 不存在 / 已删除 / 已停用」以及「行金额 / 合计数量 / 合计金额不可表示」；</item>
/// <item>空草稿头（0 行有效明细）仍可保存（提交 / 审核要求至少一行有效明细），拒绝时不消耗单据号、
/// 不改动既有单据与明细逐字段、不写任何库存流水；</item>
/// <item>重复商品行按累计数量语义保留（不引入任意唯一性规则）；</item>
/// <item>合法草稿（链接 / 未链接）仍可创建、修改、提交与审核，库存与流水恰好一次；</item>
/// <item>实际 EF 精度与源码契约：护栏常量与 <c>ErpDbContext</c> 的列类型同源，护栏先于单号生成与明细替换。</item>
/// </list>
/// 说明：全部使用内存数据库；只有「实际 EF 精度」用例以 SQL Server 提供程序<b>仅构建模型</b>（不连接数据库）。
/// </summary>
public class ReturnDocumentDraftValidationTests
{
    private const long CustomerA = 976001L;
    private const long SupplierA = 976002L;
    private const long WarehouseA = 976101L;
    private const long WarehouseAbsent = 976102L;      // 刻意不播种：不存在
    private const long ProductA = 976201L;
    private const long ProductB = 976202L;
    private const long ProductAbsent = 976999L;
    private const long SourceStockOutId = 976301L;
    private const long SourceStockInId = 976302L;

    // ==================== 1. 销售退货：创建：仓库非法（零副作用，链接 / 未链接同口径） ====================

    [Theory]
    [InlineData("zero", false, ErrorCodes.InvalidParameter)]
    [InlineData("negative", false, ErrorCodes.InvalidParameter)]
    [InlineData("missing", false, ErrorCodes.InvalidParameter)]
    [InlineData("deleted", false, ErrorCodes.InvalidParameter)]
    [InlineData("disabled", false, ErrorCodes.RuleConflict)]
    [InlineData("zero", true, ErrorCodes.InvalidParameter)]
    [InlineData("negative", true, ErrorCodes.InvalidParameter)]
    [InlineData("missing", true, ErrorCodes.InvalidParameter)]
    [InlineData("deleted", true, ErrorCodes.InvalidParameter)]
    [InlineData("disabled", true, ErrorCodes.RuleConflict)]
    public async Task 销售退货_创建_仓库非法_拒绝且零副作用(string scenario, bool linked, int expectedCode)
    {
        using var db = TestDbFactory.Create();
        SeedSalesMaster(db);
        if (scenario == "deleted") SoftDeleteWarehouse(db, WarehouseA);
        if (scenario == "disabled") DisableWarehouse(db, WarehouseA);

        var warehouseId = scenario switch
        {
            "zero" => 0L,
            "negative" => -9L,
            "missing" => WarehouseAbsent,
            _ => WarehouseA
        };
        if (linked) SeedApprovedShipment(db);

        var ctl = SalesOperator(db);
        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Create(SalesDraft(
            linked ? SourceStockOutId : null, warehouseId, SalesLine(ProductA, 1m))));

        Assert.Equal(expectedCode, ex.Code);
        AssertSalesRejectedWithNoSideEffects(db);
    }

    // ==================== 2. 销售退货：创建：明细非法（零副作用，链接 / 未链接同口径） ====================

    [Theory]
    [InlineData("missing-id", false, ErrorCodes.InvalidParameter)]
    [InlineData("missing-product", false, ErrorCodes.InvalidParameter)]
    [InlineData("deleted-product", false, ErrorCodes.InvalidParameter)]
    [InlineData("disabled-product", false, ErrorCodes.RuleConflict)]
    [InlineData("zero-quantity", false, ErrorCodes.InvalidParameter)]
    [InlineData("negative-quantity", false, ErrorCodes.InvalidParameter)]
    [InlineData("unrepresentable-quantity", false, ErrorCodes.InvalidParameter)]
    [InlineData("negative-price", false, ErrorCodes.InvalidParameter)]
    [InlineData("unrepresentable-price", false, ErrorCodes.InvalidParameter)]
    [InlineData("unrepresentable-cost", false, ErrorCodes.InvalidParameter)]
    [InlineData("missing-product", true, ErrorCodes.InvalidParameter)]
    [InlineData("deleted-product", true, ErrorCodes.InvalidParameter)]
    [InlineData("disabled-product", true, ErrorCodes.RuleConflict)]
    [InlineData("zero-quantity", true, ErrorCodes.InvalidParameter)]
    [InlineData("negative-quantity", true, ErrorCodes.InvalidParameter)]
    [InlineData("negative-price", true, ErrorCodes.InvalidParameter)]
    public async Task 销售退货_创建_明细非法_拒绝且零副作用(string scenario, bool linked, int expectedCode)
    {
        using var db = TestDbFactory.Create();
        SeedSalesMaster(db);
        if (scenario == "deleted-product") SoftDeleteProduct(db, ProductA);
        if (scenario == "disabled-product") DisableProduct(db, ProductA);

        var (productId, quantity, unitPrice, unitCost) = scenario switch
        {
            "missing-id" => (0L, 1m, 10m, 5m),
            "missing-product" => (ProductAbsent, 1m, 10m, 5m),
            "zero-quantity" => (ProductA, 0m, 10m, 5m),
            "negative-quantity" => (ProductA, -1m, 10m, 5m),
            "unrepresentable-quantity" => (ProductA, 1.00001m, 10m, 5m),
            "negative-price" => (ProductA, 1m, -1m, 5m),
            "unrepresentable-price" => (ProductA, 1m, 1.00001m, 5m),
            "unrepresentable-cost" => (ProductA, 1m, 10m, 0.0000001m),
            _ => (ProductA, 1m, 10m, 5m)
        };
        if (linked) SeedApprovedShipment(db);

        var ctl = SalesOperator(db);
        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Create(SalesDraft(
            linked ? SourceStockOutId : null, WarehouseA,
            SalesLine(productId, quantity, unitPrice, unitCost))));

        Assert.Equal(expectedCode, ex.Code);
        AssertSalesRejectedWithNoSideEffects(db);
    }

    // ==================== 3. 销售退货：创建：行金额 / 合计不可表示（零副作用） ====================

    [Theory]
    [InlineData("total-quantity")]
    [InlineData("line-amount")]
    [InlineData("total-amount")]
    public async Task 销售退货_创建_行金额或合计不可表示_拒绝且零副作用(string scenario)
    {
        using var db = TestDbFactory.Create();
        SeedSalesMaster(db);

        var lines = scenario switch
        {
            // 两行各自可表示（6e13），合计数量 1.2e14 超出 DECIMAL(18,4)
            "total-quantity" => new[]
            {
                SalesLine(ProductA, 60_000_000_000_000m),
                SalesLine(ProductB, 60_000_000_000_000m)
            },
            // 行金额 1.2e14 超出 DECIMAL(18,4)
            "line-amount" => new[] { SalesLine(ProductA, 60_000_000_000_000m, unitPrice: 2m) },
            // 行金额各自可表示（6e13），合计金额 1.2e14 超出 DECIMAL(18,4)
            _ => new[]
            {
                SalesLine(ProductA, 1m, unitPrice: 60_000_000_000_000m),
                SalesLine(ProductB, 1m, unitPrice: 60_000_000_000_000m)
            }
        };

        var ctl = SalesOperator(db);
        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(SalesDraft(null, WarehouseA, lines)));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);

        AssertSalesRejectedWithNoSideEffects(db);
    }

    // ==================== 4. 销售退货：空草稿头保留，提交 / 审核要求有效明细 ====================

    [Fact]
    public async Task 销售退货_空草稿头_可保存但不可提交与审核()
    {
        using var db = TestDbFactory.Create();
        SeedSalesMaster(db);
        var ctl = SalesOperator(db);

        var id = SalesId(await ctl.Create(SalesDraft(null, WarehouseA)));

        var stored = db.SalesReturns.AsNoTracking().Single(r => r.Id == id);
        Assert.Equal(DocumentStatus.Pending, stored.Status);
        Assert.Equal(0m, stored.TotalQuantity);                // 空草稿头合计为 0
        Assert.Equal(0m, stored.TotalAmount);
        Assert.Empty(db.SalesReturnDetails);                   // 零明细写入
        Assert.Equal(1L, SalesSequence(db));                   // 空草稿头照常占用一次服务端单号

        var submitEx = await Assert.ThrowsAsync<BusinessException>(() => ctl.Submit(id));
        Assert.Equal(ErrorCodes.RuleConflict, submitEx.Code);
        Assert.Contains("无明细", submitEx.Message);
        Assert.Equal(DocumentStatus.Pending, db.SalesReturns.AsNoTracking().Single(r => r.Id == id).Status);
        Assert.Equal(1L, SalesSequence(db));                   // 提交被拒绝不消耗单号

        // 历史遗留「已提交但无明细」直写单据：审核必须拒绝且零库存 / 流水
        var legacyId = SeedSubmittedSalesReturn(db, "XTH-503-EMPTY");
        var approveEx = await Assert.ThrowsAsync<BusinessException>(() => ctl.Approve(legacyId));
        Assert.Equal(ErrorCodes.RuleConflict, approveEx.Code);
        Assert.Contains("无明细", approveEx.Message);
        Assert.Equal(DocumentStatus.Submitted, db.SalesReturns.AsNoTracking().Single(r => r.Id == legacyId).Status);
        Assert.Empty(db.Stocks);
        Assert.Empty(db.StockMovements);
    }

    // ==================== 5. 销售退货：重复商品行与合法流程（链接 / 未链接） ====================

    [Fact]
    public async Task 销售退货_重复商品行_按累计数量保留且不引入唯一性规则()
    {
        using var db = TestDbFactory.Create();
        SeedSalesMaster(db);
        var ctl = SalesOperator(db);

        var id = SalesId(await ctl.Create(SalesDraft(null, WarehouseA,
            SalesLine(ProductA, 2m), SalesLine(ProductA, 3m))));

        Assert.Equal(2, db.SalesReturnDetails.Count(d => d.SalesReturnId == id && !d.IsDeleted));
        Assert.Equal(5m, db.SalesReturns.AsNoTracking().Single(r => r.Id == id).TotalQuantity);

        await ctl.Submit(id);
        Assert.IsType<OkObjectResult>(await ctl.Approve(id));
        Assert.Equal(5m, db.Stocks.Single(s => s.WarehouseId == WarehouseA && s.ProductId == ProductA).Quantity);
        Assert.Equal(2, db.StockMovements.Count());            // 两行各产生一条入库流水
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 销售退货_合法草稿_链接与未链接均可提交审核入库(bool linked)
    {
        using var db = TestDbFactory.Create();
        SeedSalesMaster(db);
        if (linked) SeedApprovedShipment(db);
        var ctl = SalesOperator(db);

        var id = SalesId(await ctl.Create(SalesDraft(linked ? SourceStockOutId : null, WarehouseA,
            SalesLine(ProductA, 4m, unitPrice: 10m, unitCost: 5m))));
        await ctl.Submit(id);
        Assert.IsType<OkObjectResult>(await ctl.Approve(id));

        var stored = db.SalesReturns.AsNoTracking().Single(r => r.Id == id);
        Assert.Equal(DocumentStatus.Approved, stored.Status);
        Assert.Equal(4m, stored.TotalQuantity);
        Assert.Equal(40m, stored.TotalAmount);                 // 4 × 10
        Assert.Equal(linked, stored.SourceStockOutId.HasValue);
        Assert.Single(db.StockMovements);
    }

    // ==================== 6. 销售退货：修改：非法输入拒绝且既有单据逐字段不变 ====================

    [Theory]
    [InlineData("warehouse-zero", false, ErrorCodes.InvalidParameter)]
    [InlineData("warehouse-missing", false, ErrorCodes.InvalidParameter)]
    [InlineData("warehouse-deleted", true, ErrorCodes.InvalidParameter)]
    [InlineData("warehouse-disabled", false, ErrorCodes.RuleConflict)]
    [InlineData("zero-quantity", false, ErrorCodes.InvalidParameter)]
    [InlineData("negative-quantity", true, ErrorCodes.InvalidParameter)]
    [InlineData("unrepresentable-quantity", false, ErrorCodes.InvalidParameter)]
    [InlineData("negative-price", true, ErrorCodes.InvalidParameter)]
    [InlineData("unrepresentable-price", false, ErrorCodes.InvalidParameter)]
    [InlineData("missing-id", false, ErrorCodes.InvalidParameter)]
    [InlineData("missing-product", false, ErrorCodes.InvalidParameter)]
    [InlineData("deleted-product", true, ErrorCodes.InvalidParameter)]
    [InlineData("disabled-product", false, ErrorCodes.RuleConflict)]
    public async Task 销售退货_修改_非法输入_拒绝且既有单据与明细不变(string scenario, bool linked, int expectedCode)
    {
        using var db = TestDbFactory.Create();
        SeedSalesMaster(db);
        if (linked) SeedApprovedShipment(db);
        var ctl = SalesOperator(db);

        var id = SalesId(await ctl.Create(SalesDraft(linked ? SourceStockOutId : null, WarehouseA,
            SalesLine(ProductA, 4m, unitPrice: 10m, unitCost: 5m))));
        var before = db.SalesReturns.AsNoTracking().Single(r => r.Id == id);
        var beforeDetail = db.SalesReturnDetails.AsNoTracking().Single(d => d.SalesReturnId == id);
        var sequence = SalesSequence(db);

        if (scenario == "warehouse-deleted") SoftDeleteWarehouse(db, WarehouseA);
        if (scenario == "warehouse-disabled") DisableWarehouse(db, WarehouseA);
        if (scenario == "deleted-product") SoftDeleteProduct(db, ProductA);
        if (scenario == "disabled-product") DisableProduct(db, ProductA);

        var warehouseId = scenario switch
        {
            "warehouse-zero" => 0L,
            "warehouse-missing" => WarehouseAbsent,
            _ => WarehouseA
        };
        var (productId, quantity, unitPrice) = scenario switch
        {
            "zero-quantity" => (ProductA, 0m, 10m),
            "negative-quantity" => (ProductA, -3m, 10m),
            "unrepresentable-quantity" => (ProductA, 1.00001m, 10m),
            "negative-price" => (ProductA, 2m, -10m),
            "unrepresentable-price" => (ProductA, 2m, 1.00001m),
            "missing-id" => (0L, 2m, 10m),
            "missing-product" => (ProductAbsent, 2m, 10m),
            _ => (ProductA, 2m, 10m)
        };

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Update(id,
            SalesDraft(linked ? SourceStockOutId : null, warehouseId, SalesLine(productId, quantity, unitPrice))));
        Assert.Equal(expectedCode, ex.Code);

        var after = db.SalesReturns.AsNoTracking().Single(r => r.Id == id);
        Assert.Equal(before.ReturnNo, after.ReturnNo);
        Assert.Equal(before.Status, after.Status);
        Assert.Equal(before.WarehouseId, after.WarehouseId);
        Assert.Equal(before.TotalQuantity, after.TotalQuantity);
        Assert.Equal(before.TotalAmount, after.TotalAmount);
        Assert.Equal(before.UpdatedAt, after.UpdatedAt);

        var afterDetail = db.SalesReturnDetails.AsNoTracking().Single(d => d.SalesReturnId == id);
        Assert.Equal(beforeDetail.Id, afterDetail.Id);
        Assert.Equal(beforeDetail.ProductId, afterDetail.ProductId);
        Assert.Equal(beforeDetail.Quantity, afterDetail.Quantity);
        Assert.Equal(beforeDetail.UnitPrice, afterDetail.UnitPrice);
        Assert.Equal(sequence, SalesSequence(db));             // 修改被拒绝不消耗新单号
        Assert.Empty(db.StockMovements);
    }

    [Fact]
    public async Task 销售退货_修改_清空明细_保留空草稿头但不可提交()
    {
        using var db = TestDbFactory.Create();
        SeedSalesMaster(db);
        var ctl = SalesOperator(db);
        var id = SalesId(await ctl.Create(SalesDraft(null, WarehouseA, SalesLine(ProductA, 4m))));
        var sequence = SalesSequence(db);

        Assert.IsType<OkObjectResult>(await ctl.Update(id, SalesDraft(null, WarehouseA)));

        Assert.Empty(db.SalesReturnDetails.Where(d => d.SalesReturnId == id && !d.IsDeleted));
        Assert.Equal(0m, db.SalesReturns.AsNoTracking().Single(r => r.Id == id).TotalQuantity);
        Assert.Equal(sequence, SalesSequence(db));             // 修改不消耗新单号

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Submit(id));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("无明细", ex.Message);
        Assert.Equal(DocumentStatus.Pending, db.SalesReturns.AsNoTracking().Single(r => r.Id == id).Status);
        Assert.Empty(db.StockMovements);
    }

    [Fact]
    public async Task 销售退货_修改_合法输入_保留单号状态并重算合计()
    {
        using var db = TestDbFactory.Create();
        SeedSalesMaster(db);
        var ctl = SalesOperator(db);
        var id = SalesId(await ctl.Create(SalesDraft(null, WarehouseA, SalesLine(ProductA, 4m))));
        var number = db.SalesReturns.AsNoTracking().Single(r => r.Id == id).ReturnNo;
        var sequence = SalesSequence(db);

        Assert.IsType<OkObjectResult>(await ctl.Update(id,
            SalesDraft(null, WarehouseA, SalesLine(ProductA, 2m), SalesLine(ProductB, 3m))));

        var stored = db.SalesReturns.AsNoTracking().Single(r => r.Id == id);
        Assert.Equal(number, stored.ReturnNo);
        Assert.Equal(DocumentStatus.Pending, stored.Status);
        Assert.Equal(5m, stored.TotalQuantity);
        Assert.Equal(50m, stored.TotalAmount);                 // 2 × 10 + 3 × 10
        Assert.Equal(sequence, SalesSequence(db));
        Assert.Empty(db.StockMovements);
    }

    // ==================== 7. 采购退货：创建：仓库非法（零副作用，链接 / 未链接同口径） ====================

    [Theory]
    [InlineData("zero", false, ErrorCodes.InvalidParameter)]
    [InlineData("negative", false, ErrorCodes.InvalidParameter)]
    [InlineData("missing", false, ErrorCodes.InvalidParameter)]
    [InlineData("deleted", false, ErrorCodes.InvalidParameter)]
    [InlineData("disabled", false, ErrorCodes.RuleConflict)]
    [InlineData("zero", true, ErrorCodes.InvalidParameter)]
    [InlineData("missing", true, ErrorCodes.InvalidParameter)]
    [InlineData("deleted", true, ErrorCodes.InvalidParameter)]
    [InlineData("disabled", true, ErrorCodes.RuleConflict)]
    public async Task 采购退货_创建_仓库非法_拒绝且零副作用(string scenario, bool linked, int expectedCode)
    {
        using var db = TestDbFactory.Create();
        SeedPurchaseMaster(db);
        if (scenario == "deleted") SoftDeleteWarehouse(db, WarehouseA);
        if (scenario == "disabled") DisableWarehouse(db, WarehouseA);

        var warehouseId = scenario switch
        {
            "zero" => 0L,
            "negative" => -9L,
            "missing" => WarehouseAbsent,
            _ => WarehouseA
        };
        if (linked) SeedApprovedReceipt(db);

        var ctl = PurchaseOperator(db);
        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Create(PurchaseDraft(
            linked ? SourceStockInId : null, warehouseId, PurchaseLine(ProductA, 1m))));

        Assert.Equal(expectedCode, ex.Code);
        AssertPurchaseRejectedWithNoSideEffects(db);
    }

    // ==================== 8. 采购退货：创建：明细非法（零副作用，链接 / 未链接同口径） ====================

    [Theory]
    [InlineData("missing-id", false, ErrorCodes.InvalidParameter)]
    [InlineData("missing-product", false, ErrorCodes.InvalidParameter)]
    [InlineData("deleted-product", false, ErrorCodes.InvalidParameter)]
    [InlineData("disabled-product", false, ErrorCodes.RuleConflict)]
    [InlineData("zero-quantity", false, ErrorCodes.InvalidParameter)]
    [InlineData("negative-quantity", false, ErrorCodes.InvalidParameter)]
    [InlineData("unrepresentable-quantity", false, ErrorCodes.InvalidParameter)]
    [InlineData("negative-price", false, ErrorCodes.InvalidParameter)]
    [InlineData("unrepresentable-price", false, ErrorCodes.InvalidParameter)]
    [InlineData("negative-cost", false, ErrorCodes.InvalidParameter)]
    [InlineData("unrepresentable-cost", false, ErrorCodes.InvalidParameter)]
    [InlineData("missing-product", true, ErrorCodes.InvalidParameter)]
    [InlineData("deleted-product", true, ErrorCodes.InvalidParameter)]
    [InlineData("disabled-product", true, ErrorCodes.RuleConflict)]
    [InlineData("zero-quantity", true, ErrorCodes.InvalidParameter)]
    [InlineData("negative-quantity", true, ErrorCodes.InvalidParameter)]
    [InlineData("negative-price", true, ErrorCodes.InvalidParameter)]
    [InlineData("unrepresentable-price", true, ErrorCodes.InvalidParameter)]
    public async Task 采购退货_创建_明细非法_拒绝且零副作用(string scenario, bool linked, int expectedCode)
    {
        using var db = TestDbFactory.Create();
        SeedPurchaseMaster(db);
        if (scenario == "deleted-product") SoftDeleteProduct(db, ProductA);
        if (scenario == "disabled-product") DisableProduct(db, ProductA);

        var (productId, quantity, unitPrice, unitCost) = scenario switch
        {
            "missing-id" => (0L, 1m, 10m, 5m),
            "missing-product" => (ProductAbsent, 1m, 10m, 5m),
            "zero-quantity" => (ProductA, 0m, 10m, 5m),
            "negative-quantity" => (ProductA, -1m, 10m, 5m),
            "unrepresentable-quantity" => (ProductA, 1.00001m, 10m, 5m),
            "negative-price" => (ProductA, 1m, -1m, 5m),
            "unrepresentable-price" => (ProductA, 1m, 1.00001m, 5m),
            "negative-cost" => (ProductA, 1m, 10m, -5m),
            "unrepresentable-cost" => (ProductA, 1m, 10m, 0.0000001m),
            _ => (ProductA, 1m, 10m, 5m)
        };
        if (linked) SeedApprovedReceipt(db);

        var ctl = PurchaseOperator(db);
        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Create(PurchaseDraft(
            linked ? SourceStockInId : null, WarehouseA,
            PurchaseLine(productId, quantity, unitPrice, unitCost))));

        Assert.Equal(expectedCode, ex.Code);
        AssertPurchaseRejectedWithNoSideEffects(db);
    }

    // ==================== 9. 采购退货：行金额 / 合计不可表示（零副作用） ====================

    [Theory]
    [InlineData("total-quantity")]
    [InlineData("line-amount")]
    [InlineData("total-amount")]
    public async Task 采购退货_创建_行金额或合计不可表示_拒绝且零副作用(string scenario)
    {
        using var db = TestDbFactory.Create();
        SeedPurchaseMaster(db);

        var lines = scenario switch
        {
            "total-quantity" => new[]
            {
                PurchaseLine(ProductA, 60_000_000_000_000m),
                PurchaseLine(ProductB, 60_000_000_000_000m)
            },
            "line-amount" => new[] { PurchaseLine(ProductA, 60_000_000_000_000m, unitPrice: 2m) },
            _ => new[]
            {
                PurchaseLine(ProductA, 1m, unitPrice: 60_000_000_000_000m),
                PurchaseLine(ProductB, 1m, unitPrice: 60_000_000_000_000m)
            }
        };

        var ctl = PurchaseOperator(db);
        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(PurchaseDraft(null, WarehouseA, lines)));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);

        AssertPurchaseRejectedWithNoSideEffects(db);
    }

    // ==================== 10. 采购退货：空草稿头保留，提交 / 审核要求有效明细 ====================

    [Fact]
    public async Task 采购退货_空草稿头_可保存但不可提交与审核()
    {
        using var db = TestDbFactory.Create();
        SeedPurchaseMaster(db);
        var ctl = PurchaseOperator(db);

        var id = PurchaseId(await ctl.Create(PurchaseDraft(null, WarehouseA)));

        var stored = db.PurchaseReturns.AsNoTracking().Single(r => r.Id == id);
        Assert.Equal(DocumentStatus.Pending, stored.Status);
        Assert.Equal(0m, stored.TotalQuantity);
        Assert.Equal(0m, stored.TotalAmount);
        Assert.Empty(db.PurchaseReturnDetails);
        Assert.Equal(1L, PurchaseSequence(db));

        var submitEx = await Assert.ThrowsAsync<BusinessException>(() => ctl.Submit(id));
        Assert.Equal(ErrorCodes.RuleConflict, submitEx.Code);
        Assert.Contains("无明细", submitEx.Message);
        Assert.Equal(DocumentStatus.Pending, db.PurchaseReturns.AsNoTracking().Single(r => r.Id == id).Status);
        Assert.Equal(1L, PurchaseSequence(db));

        var legacyId = SeedSubmittedPurchaseReturn(db, "CTH-503-EMPTY");
        var approveEx = await Assert.ThrowsAsync<BusinessException>(() => ctl.Approve(legacyId));
        Assert.Equal(ErrorCodes.RuleConflict, approveEx.Code);
        Assert.Contains("无明细", approveEx.Message);
        Assert.Equal(DocumentStatus.Submitted,
            db.PurchaseReturns.AsNoTracking().Single(r => r.Id == legacyId).Status);
        Assert.Empty(db.Stocks);
        Assert.Empty(db.StockMovements);
    }

    // ==================== 11. 采购退货：重复商品行与合法流程（链接 / 未链接） ====================

    [Fact]
    public async Task 采购退货_重复商品行_按累计数量保留且不引入唯一性规则()
    {
        using var db = TestDbFactory.Create();
        SeedPurchaseMaster(db);
        SeedStock(db, ProductA, quantity: 100m, totalCost: 600m);
        var ctl = PurchaseOperator(db);

        var id = PurchaseId(await ctl.Create(PurchaseDraft(null, WarehouseA,
            PurchaseLine(ProductA, 2m), PurchaseLine(ProductA, 3m))));

        Assert.Equal(2, db.PurchaseReturnDetails.Count(d => d.PurchaseReturnId == id && !d.IsDeleted));
        Assert.Equal(5m, db.PurchaseReturns.AsNoTracking().Single(r => r.Id == id).TotalQuantity);

        await ctl.Submit(id);
        Assert.IsType<OkObjectResult>(await ctl.Approve(id));
        Assert.Equal(95m, db.Stocks.Single(s => s.WarehouseId == WarehouseA && s.ProductId == ProductA).Quantity);
        Assert.Equal(2, db.StockMovements.Count());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 采购退货_合法草稿_链接与未链接均可提交审核出库(bool linked)
    {
        using var db = TestDbFactory.Create();
        SeedPurchaseMaster(db);
        SeedStock(db, ProductA, quantity: 100m, totalCost: 600m);
        if (linked) SeedApprovedReceipt(db);
        var ctl = PurchaseOperator(db);

        var id = PurchaseId(await ctl.Create(PurchaseDraft(linked ? SourceStockInId : null, WarehouseA,
            PurchaseLine(ProductA, 4m, unitPrice: 7m, unitCost: 5m))));
        await ctl.Submit(id);
        Assert.IsType<OkObjectResult>(await ctl.Approve(id));

        var stored = db.PurchaseReturns.AsNoTracking().Single(r => r.Id == id);
        Assert.Equal(DocumentStatus.Approved, stored.Status);
        Assert.Equal(4m, stored.TotalQuantity);
        Assert.Equal(28m, stored.TotalAmount);                 // 4 × 7
        Assert.Equal(linked, stored.SourceStockInId.HasValue);
        Assert.Equal(96m, db.Stocks.Single(s => s.WarehouseId == WarehouseA && s.ProductId == ProductA).Quantity);
        Assert.Single(db.StockMovements);
    }

    // ==================== 12. 采购退货：修改：非法输入拒绝且既有单据逐字段不变 ====================

    [Theory]
    [InlineData("warehouse-zero", false, ErrorCodes.InvalidParameter)]
    [InlineData("warehouse-missing", false, ErrorCodes.InvalidParameter)]
    [InlineData("warehouse-deleted", true, ErrorCodes.InvalidParameter)]
    [InlineData("warehouse-disabled", false, ErrorCodes.RuleConflict)]
    [InlineData("zero-quantity", false, ErrorCodes.InvalidParameter)]
    [InlineData("negative-quantity", true, ErrorCodes.InvalidParameter)]
    [InlineData("unrepresentable-quantity", false, ErrorCodes.InvalidParameter)]
    [InlineData("negative-price", true, ErrorCodes.InvalidParameter)]
    [InlineData("unrepresentable-price", false, ErrorCodes.InvalidParameter)]
    [InlineData("missing-id", false, ErrorCodes.InvalidParameter)]
    [InlineData("missing-product", false, ErrorCodes.InvalidParameter)]
    [InlineData("deleted-product", true, ErrorCodes.InvalidParameter)]
    [InlineData("disabled-product", false, ErrorCodes.RuleConflict)]
    public async Task 采购退货_修改_非法输入_拒绝且既有单据与明细不变(string scenario, bool linked, int expectedCode)
    {
        using var db = TestDbFactory.Create();
        SeedPurchaseMaster(db);
        if (linked) SeedApprovedReceipt(db);
        var ctl = PurchaseOperator(db);

        var id = PurchaseId(await ctl.Create(PurchaseDraft(linked ? SourceStockInId : null, WarehouseA,
            PurchaseLine(ProductA, 4m, unitPrice: 7m, unitCost: 5m))));
        var before = db.PurchaseReturns.AsNoTracking().Single(r => r.Id == id);
        var beforeDetail = db.PurchaseReturnDetails.AsNoTracking().Single(d => d.PurchaseReturnId == id);
        var sequence = PurchaseSequence(db);

        if (scenario == "warehouse-deleted") SoftDeleteWarehouse(db, WarehouseA);
        if (scenario == "warehouse-disabled") DisableWarehouse(db, WarehouseA);
        if (scenario == "deleted-product") SoftDeleteProduct(db, ProductA);
        if (scenario == "disabled-product") DisableProduct(db, ProductA);

        var warehouseId = scenario switch
        {
            "warehouse-zero" => 0L,
            "warehouse-missing" => WarehouseAbsent,
            _ => WarehouseA
        };
        var (productId, quantity, unitPrice) = scenario switch
        {
            "zero-quantity" => (ProductA, 0m, 7m),
            "negative-quantity" => (ProductA, -3m, 7m),
            "unrepresentable-quantity" => (ProductA, 1.00001m, 7m),
            "negative-price" => (ProductA, 2m, -7m),
            "unrepresentable-price" => (ProductA, 2m, 1.00001m),
            "missing-id" => (0L, 2m, 7m),
            "missing-product" => (ProductAbsent, 2m, 7m),
            _ => (ProductA, 2m, 7m)
        };

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Update(id,
            PurchaseDraft(linked ? SourceStockInId : null, warehouseId,
                PurchaseLine(productId, quantity, unitPrice))));
        Assert.Equal(expectedCode, ex.Code);

        var after = db.PurchaseReturns.AsNoTracking().Single(r => r.Id == id);
        Assert.Equal(before.ReturnNo, after.ReturnNo);
        Assert.Equal(before.Status, after.Status);
        Assert.Equal(before.WarehouseId, after.WarehouseId);
        Assert.Equal(before.TotalQuantity, after.TotalQuantity);
        Assert.Equal(before.TotalAmount, after.TotalAmount);
        Assert.Equal(before.UpdatedAt, after.UpdatedAt);

        var afterDetail = db.PurchaseReturnDetails.AsNoTracking().Single(d => d.PurchaseReturnId == id);
        Assert.Equal(beforeDetail.Id, afterDetail.Id);
        Assert.Equal(beforeDetail.ProductId, afterDetail.ProductId);
        Assert.Equal(beforeDetail.Quantity, afterDetail.Quantity);
        Assert.Equal(beforeDetail.UnitPrice, afterDetail.UnitPrice);
        Assert.Equal(sequence, PurchaseSequence(db));          // 修改被拒绝不消耗新单号
        Assert.Empty(db.StockMovements);
    }

    [Fact]
    public async Task 采购退货_修改_清空明细_保留空草稿头但不可提交()
    {
        using var db = TestDbFactory.Create();
        SeedPurchaseMaster(db);
        var ctl = PurchaseOperator(db);
        var id = PurchaseId(await ctl.Create(PurchaseDraft(null, WarehouseA, PurchaseLine(ProductA, 4m))));
        var sequence = PurchaseSequence(db);

        Assert.IsType<OkObjectResult>(await ctl.Update(id, PurchaseDraft(null, WarehouseA)));

        Assert.Empty(db.PurchaseReturnDetails.Where(d => d.PurchaseReturnId == id && !d.IsDeleted));
        Assert.Equal(0m, db.PurchaseReturns.AsNoTracking().Single(r => r.Id == id).TotalQuantity);
        Assert.Equal(sequence, PurchaseSequence(db));

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Submit(id));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("无明细", ex.Message);
        Assert.Equal(DocumentStatus.Pending, db.PurchaseReturns.AsNoTracking().Single(r => r.Id == id).Status);
        Assert.Empty(db.StockMovements);
    }

    // ==================== 13. 实际 EF 精度与源码契约 ====================

    [Fact]
    public void EF实际精度_数量单价成本与表头合计_与护栏常量同源()
    {
        var options = new DbContextOptionsBuilder<ErpDbContext>()
            .UseSqlServer(
                "Server=(localdb)\\NEWERP_AutoAcceptance;Database=NEWERP_AUTOTEST_MODEL_ONLY;Integrated Security=true")
            .Options;
        using var db = new ErpDbContext(options);
        var model = db.Model;

        Assert.Equal("decimal(18,4)", ColumnType(model, typeof(SalesReturn), nameof(SalesReturn.TotalQuantity)));
        Assert.Equal("decimal(18,4)", ColumnType(model, typeof(SalesReturn), nameof(SalesReturn.TotalAmount)));
        Assert.Equal("decimal(18,4)", ColumnType(model, typeof(SalesReturnDetail), nameof(SalesReturnDetail.Quantity)));
        Assert.Equal("decimal(18,4)", ColumnType(model, typeof(SalesReturnDetail), nameof(SalesReturnDetail.UnitPrice)));
        Assert.Equal("decimal(18,4)", ColumnType(model, typeof(SalesReturnDetail), nameof(SalesReturnDetail.Amount)));
        Assert.Equal("decimal(18,6)", ColumnType(model, typeof(SalesReturnDetail), nameof(SalesReturnDetail.UnitCost)));

        Assert.Equal("decimal(18,4)", ColumnType(model, typeof(PurchaseReturn), nameof(PurchaseReturn.TotalQuantity)));
        Assert.Equal("decimal(18,4)", ColumnType(model, typeof(PurchaseReturn), nameof(PurchaseReturn.TotalAmount)));
        Assert.Equal("decimal(18,4)",
            ColumnType(model, typeof(PurchaseReturnDetail), nameof(PurchaseReturnDetail.Quantity)));
        Assert.Equal("decimal(18,4)",
            ColumnType(model, typeof(PurchaseReturnDetail), nameof(PurchaseReturnDetail.UnitPrice)));
        Assert.Equal("decimal(18,4)",
            ColumnType(model, typeof(PurchaseReturnDetail), nameof(PurchaseReturnDetail.Amount)));
        Assert.Equal("decimal(18,6)",
            ColumnType(model, typeof(PurchaseReturnDetail), nameof(PurchaseReturnDetail.UnitCost)));

        Assert.Equal(18, ReturnDocumentDraftRules.QuantityPrecision);
        Assert.Equal(4, ReturnDocumentDraftRules.QuantityScale);
        Assert.Equal(18, ReturnDocumentDraftRules.CostPrecision);
        Assert.Equal(6, ReturnDocumentDraftRules.CostScale);
        Assert.True(ReturnDocumentDraftRules.IsQuantityRepresentable(99_999_999_999_999.9999m));
        Assert.True(ReturnDocumentDraftRules.IsQuantityRepresentable(1.0001m));
        Assert.False(ReturnDocumentDraftRules.IsQuantityRepresentable(100_000_000_000_000m));
        Assert.False(ReturnDocumentDraftRules.IsQuantityRepresentable(1.00001m));
        Assert.True(ReturnDocumentDraftRules.IsCostRepresentable(999_999_999_999.999999m));
        Assert.False(ReturnDocumentDraftRules.IsCostRepresentable(0.0000001m));
    }

    [Fact]
    public void 草稿护栏源码契约_先折算再校验且先于单号与明细替换()
    {
        var sales = ReadSource("src/ERP.Api/Controllers/SalesReturnController.cs");
        var purchase = ReadSource("src/ERP.Api/Controllers/PurchaseReturnController.cs");

        Assert.Contains("SalesReturnSourceRules.EnsureDraftValidAsync", sales);
        Assert.Contains("PurchaseReturnSourceRules.EnsureDraftValidAsync", purchase);
        Assert.Contains("EnsureHasEffectiveLine(entity.Details, \"提交\")", sales);
        Assert.Contains("EnsureHasEffectiveLine(entity.Details, \"提交\")", purchase);
        Assert.Contains("EnsureHasEffectiveLine(entity.Details, \"审核\")", sales);
        Assert.Contains("EnsureHasEffectiveLine(entity.Details, \"审核\")", purchase);

        foreach (var source in new[] { sales, purchase })
        {
            var createStart = source.IndexOf("public async Task<IActionResult> Create", StringComparison.Ordinal);
            var updateStart = source.IndexOf("public async Task<IActionResult> Update", StringComparison.Ordinal);
            var approveStart = source.IndexOf(
                "public override async Task<IActionResult> Approve", StringComparison.Ordinal);
            Assert.True(createStart >= 0 && updateStart > createStart && approveStart > updateStart);

            var create = source[createStart..updateStart];
            Assert.True(create.IndexOf("StockUnitConversion.NormalizeAsync", StringComparison.Ordinal)
                        < create.IndexOf("EnsureDraftValidAsync", StringComparison.Ordinal));   // 折算先于护栏
            Assert.True(create.IndexOf("EnsureDraftValidAsync", StringComparison.Ordinal)
                        < create.IndexOf("_noService.GenerateAsync", StringComparison.Ordinal)); // 不消耗单号

            var update = source[updateStart..approveStart];
            Assert.True(update.IndexOf("StockUnitConversion.NormalizeAsync", StringComparison.Ordinal)
                        < update.IndexOf("EnsureDraftValidAsync", StringComparison.Ordinal));
            Assert.True(update.IndexOf("EnsureDraftValidAsync", StringComparison.Ordinal)
                        < update.LastIndexOf("RemoveRange", StringComparison.Ordinal));          // 校验先于明细替换
        }

        var rules = ReadSource("src/ERP.Application/Services/SalesReturnSourceRules.cs");
        Assert.Contains("class ReturnDocumentDraftRules", rules);
        Assert.Contains("EnsureWarehouseAvailableAsync", rules);
        Assert.Contains("EnsureProductsAvailableAsync", rules);
        Assert.Contains("AsNoTracking", rules);
        Assert.DoesNotContain("SaveChanges", rules);              // 纯判定 + 只读查询，不落库
        Assert.Contains("DECIMAL(18,4)", ReturnDocumentDraftRules.RuleText);
        Assert.Contains("绝不引入任意唯一性规则", ReturnDocumentDraftRules.RuleText);
        Assert.Equal("sales-return", SalesReturnSourceRules.RequiredMenuCode);       // 不新增菜单 / 权限
        Assert.Equal("purchase-return", PurchaseReturnSourceRules.RequiredMenuCode);
    }

    // ==================== 测试辅助 ====================

    /// <summary>播种真实在用的销售退货主数据与单据号规则（存在、未删除、启用）。</summary>
    private static void SeedSalesMaster(ErpDbContext db)
    {
        SalesReturnTestAuthorization.SeedWarehouse(db, WarehouseA, "退货仓A");
        SalesReturnTestAuthorization.SeedProduct(db, ProductA, "ERP503-A", "PCS");
        SalesReturnTestAuthorization.SeedProduct(db, ProductB, "ERP503-B", "PCS");
        SeedNumberRules(db);
    }

    /// <summary>播种真实在用的采购退货主数据与单据号规则（存在、未删除、启用）。</summary>
    private static void SeedPurchaseMaster(ErpDbContext db)
    {
        PurchaseReturnTestAuthorization.SeedWarehouse(db, WarehouseA, "退货仓A");
        PurchaseReturnTestAuthorization.SeedProduct(db, ProductA, "ERP503-A", "PCS");
        PurchaseReturnTestAuthorization.SeedProduct(db, ProductB, "ERP503-B", "PCS");
        PurchaseReturnTestAuthorization.SeedSupplier(db, SupplierA, "供应商A");
        SeedNumberRules(db);
    }

    private static void SeedNumberRules(ErpDbContext db)
    {
        db.SysDocumentNumberRules.Add(new SysDocumentNumberRule
        {
            DocumentType = DocumentType.SalesReturn,
            RuleCode = "SR-503",
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
            RuleCode = "PR-503",
            RuleName = "采购退货单",
            Prefix = "CTH",
            DateFormat = "yyyyMMdd",
            SerialLength = 4,
            Separator = string.Empty,
            CurrentSequence = 0
        });
        db.SaveChanges();
    }

    /// <summary>真实已授权非特权操作员身份（既有 sales-return 菜单 + 客户数据范围），不绕过鉴权。</summary>
    private static SalesReturnController SalesOperator(ErpDbContext db)
        => SalesReturnTestAuthorization.ForUser(db,
            SalesReturnTestAuthorization.SeedAuthorizedSalesman(db, CustomerA));

    /// <summary>真实已授权非特权操作员身份（既有 purchase-return 菜单），不绕过鉴权。</summary>
    private static PurchaseReturnController PurchaseOperator(ErpDbContext db)
        => PurchaseReturnTestAuthorization.ForUser(db,
            PurchaseReturnTestAuthorization.SeedAuthorizedOperator(db));

    /// <summary>播种权威来源销售出库单（已审核、同客户同仓库、商品基础单位证据充足）。</summary>
    private static void SeedApprovedShipment(ErpDbContext db)
        => SalesReturnTestAuthorization.SeedApprovedStockOut(db, SourceStockOutId, "CK-SR-0503", CustomerA,
            WarehouseA, (ProductA, "PCS", 100m));

    /// <summary>播种权威来源采购入库单（已审核、同供应商同仓库、商品基础单位证据充足）。</summary>
    private static void SeedApprovedReceipt(ErpDbContext db)
        => PurchaseReturnTestAuthorization.SeedApprovedStockIn(db, SourceStockInId, "RK-PR-0503", SupplierA,
            WarehouseA, (ProductA, "PCS", 100m));

    private static SalesReturnDetail SalesLine(long productId, decimal quantity, decimal unitPrice = 10m,
        decimal unitCost = 5m)
        => new()
        {
            ProductId = productId,
            ProductName = $"商品{productId}",
            Spec = "规格A",
            Unit = "PCS",
            Quantity = quantity,
            UnitPrice = unitPrice,
            UnitCost = unitCost
        };

    private static PurchaseReturnDetail PurchaseLine(long productId, decimal quantity, decimal unitPrice = 10m,
        decimal unitCost = 5m)
        => new()
        {
            ProductId = productId,
            ProductName = $"商品{productId}",
            Spec = "规格A",
            Unit = "PCS",
            Quantity = quantity,
            UnitPrice = unitPrice,
            UnitCost = unitCost
        };

    private static SalesReturn SalesDraft(long? sourceStockOutId, long warehouseId,
        params SalesReturnDetail[] lines)
        => new()
        {
            ReturnDate = DateTime.Today,
            CustomerId = CustomerA,
            CustomerName = "客户A",
            WarehouseId = warehouseId,
            SourceStockOutId = sourceStockOutId,
            SourceStockOutNo = string.Empty,
            ReturnReason = "质量",
            Remark = "ERP-503_TEST",
            Details = lines.ToList()
        };

    private static PurchaseReturn PurchaseDraft(long? sourceStockInId, long warehouseId,
        params PurchaseReturnDetail[] lines)
        => new()
        {
            ReturnDate = DateTime.Today,
            SupplierId = SupplierA,
            SupplierName = "供应商A",
            WarehouseId = warehouseId,
            SourceStockInId = sourceStockInId,
            SourceStockInNo = string.Empty,
            ReturnReason = "规格不符",
            Remark = "ERP-503_TEST",
            Details = lines.ToList()
        };

    /// <summary>历史遗留「已提交但无明细」直写单据（不经过 Create、不消耗单号）。</summary>
    private static long SeedSubmittedSalesReturn(ErpDbContext db, string no)
    {
        var document = new SalesReturn
        {
            ReturnNo = no,
            ReturnDate = DateTime.Today,
            CustomerId = CustomerA,
            CustomerName = "客户A",
            WarehouseId = WarehouseA,
            Status = DocumentStatus.Submitted
        };
        db.SalesReturns.Add(document);
        db.SaveChanges();
        return document.Id;
    }

    /// <summary>历史遗留「已提交但无明细」直写采购退货单（不经过 Create、不消耗单号）。</summary>
    private static long SeedSubmittedPurchaseReturn(ErpDbContext db, string no)
    {
        var document = new PurchaseReturn
        {
            ReturnNo = no,
            ReturnDate = DateTime.Today,
            SupplierId = SupplierA,
            SupplierName = "供应商A",
            WarehouseId = WarehouseA,
            Status = DocumentStatus.Submitted
        };
        db.PurchaseReturns.Add(document);
        db.SaveChanges();
        return document.Id;
    }

    private static void SeedStock(ErpDbContext db, long productId, decimal quantity, decimal totalCost)
    {
        db.Stocks.Add(new Stock
        {
            WarehouseId = WarehouseA,
            ProductId = productId,
            Quantity = quantity,
            AvailableQuantity = quantity,
            TotalCost = totalCost,
            AverageCost = quantity > 0 ? Math.Round(totalCost / quantity, 6) : 0m
        });
        db.SaveChanges();
    }

    private static void SoftDeleteWarehouse(ErpDbContext db, long id)
    {
        var warehouse = db.BaseWarehouses.Single(w => w.Id == id);
        warehouse.IsDeleted = true;
        db.SaveChanges();
    }

    private static void DisableWarehouse(ErpDbContext db, long id)
    {
        var warehouse = db.BaseWarehouses.Single(w => w.Id == id);
        warehouse.Status = 0;
        db.SaveChanges();
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

    private static long SalesId(IActionResult result) => PayloadId(result);

    private static long PurchaseId(IActionResult result) => PayloadId(result);

    private static long PayloadId(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var payload = Assert.IsType<ApiResponse<object>>(ok.Value).Data!;
        return (long)payload.GetType().GetProperty("Id")!.GetValue(payload)!;
    }

    private static string ColumnType(IModel model, Type entityType, string propertyName)
        => model.FindEntityType(entityType)!.FindProperty(propertyName)!.GetColumnType()!;

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
