using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-033 采购入库成本来源单元测试：审核时把「已审核采购订单的唯一兼容明细行单价」折算成
/// 库存基础单位成本带入库存流水，任何语义不明确都保持 ERP-025 的移动加权平均兜底。
/// <para>覆盖：唯一链接按订单价计价并参与加权平均、包装单位订单价按 UnitsPerPackage 折算（数量不变）、
/// 外币订单按持久化汇率换算、默认占位汇率不换算、无链接 / 订单不存在 / 未审核 / 已删除 / 订单行缺失 ·
/// 重复 · 单位不兼容 / 单价不可用 / 商品资料缺失一律回退、取消红字冲销与重复审核幂等、取消后重试。</para>
/// <para>说明：全部使用内存数据库，不连接 SQL Server、不启动 API、不执行任何 SQL 或 seed，不运行浏览器验收。</para>
/// </summary>
public class PurchaseStockInCostTests
{
    private const long WarehouseA = 930001L;
    private const long ProductA = 830001L;
    private const long SupplierA = 930010L;

    /// <summary>库存流水入库方向（<see cref="StockMovement.Direction"/> = 1）</summary>
    private const int MovementDirectionIn = 1;

    // ==================== 1. 唯一链接：按订单单价计价 ====================

    [Fact]
    public async Task 采购入库_唯一订单行_按订单单价计价并参与移动加权平均()
    {
        using var db = TestDbFactory.Create();
        SeedProduct(db, ProductA, "P-033-1", "商品A", "PCS", string.Empty, 0);
        var order = SeedOrder(db, "PO-033-1", Currency.CNY, 1m, DocumentStatus.Approved,
            (ProductA, "PCS", 7.5m));
        SeedStock(db, WarehouseA, ProductA, quantity: 10m, totalCost: 100m);
        var ctl = NewController(db);
        var id = await CreateStockInAsync(ctl, ProductA, 10m, "PCS", order.Id);

        await ctl.Submit(id);
        await ctl.Approve(id);

        var movement = db.StockMovements.Single();
        Assert.Equal(InventoryDocumentHelper.StockInType, movement.SourceDocType);
        Assert.Equal(id, movement.SourceDocId);
        Assert.Equal(MovementDirectionIn, movement.Direction);
        Assert.Equal(10m, movement.Quantity);
        Assert.Equal(7.5m, movement.UnitCost);                       // 订单单价直接作为基础单位成本
        Assert.Equal(75m, movement.Amount);
        Assert.Contains($"采购订单 Id {order.Id}", movement.Remark);
        Assert.Contains("成本来源：采购订单单价", movement.Remark);

        var stock = db.Stocks.Single(s => s.WarehouseId == WarehouseA && s.ProductId == ProductA);
        Assert.Equal(20m, stock.Quantity);
        Assert.Equal(175m, stock.TotalCost);                         // 100 + 10 × 7.5
        Assert.Equal(8.75m, stock.AverageCost);                      // 订单价参与加权平均

        // 成本规则只影响单价：入库数量（基础单位）不被改动
        Assert.Equal(10m, db.StockInDetails.Single().Quantity);
        Assert.Equal("PCS", db.StockInDetails.Single().Unit);

        // 只读判定结果可核对来源与原因（同一口径常量，供流水备注与后续界面复用）
        var source = await LoadSourceAsync(db, order.Id, ProductA);
        var cost = source.Resolve(ProductA);
        Assert.True(source.HasAuthoritativeOrder);
        Assert.Equal(order.OrderNo, source.PurchaseOrderNo);
        Assert.Equal(PurchaseStockInCost.SourceOrderPrice, cost.Source);
        Assert.Empty(cost.Reason);
        Assert.True(cost.UsedOrderPrice);
        Assert.False(cost.PackagePriceNormalized);
        Assert.False(cost.CurrencyConverted);
    }

    // ==================== 2. 包装单位订单价 → 基础单位成本 ====================

    [Fact]
    public async Task 采购入库_包装单位订单价_按装箱数折算基础单位成本且数量不变()
    {
        using var db = TestDbFactory.Create();
        SeedProduct(db, ProductA, "P-033-2", "商品A", "PCS", "CTN", 12);
        var order = SeedOrder(db, "PO-033-2", Currency.CNY, 1m, DocumentStatus.Approved,
            (ProductA, "CTN", 125m));                                 // 订单价按包装单位（箱）计价
        var ctl = NewController(db);
        var id = await CreateStockInAsync(ctl, ProductA, 2m, "CTN", order.Id);   // 2 CTN → 24 PCS

        await ctl.Submit(id);
        await ctl.Approve(id);

        Assert.Equal(24m, db.StockInDetails.Single().Quantity);       // ERP-023 基础单位数量不变
        var movement = db.StockMovements.Single();
        Assert.Equal(24m, movement.Quantity);
        Assert.Equal(10.416667m, movement.UnitCost);                  // 125 ÷ 12，保留 6 位小数
        Assert.Equal(250m, movement.Amount);
        Assert.Contains("成本来源：采购订单单价（按装箱数折算为基础单位）", movement.Remark);

        var stock = db.Stocks.Single(s => s.ProductId == ProductA);
        Assert.Equal(24m, stock.Quantity);
        Assert.Equal(250m, stock.TotalCost);
        Assert.Equal(10.416667m, stock.AverageCost);
    }

    // ==================== 3. 两次不同订单价：移动加权平均 ====================

    [Fact]
    public async Task 采购入库_两次不同订单价入库_按移动加权平均重算库存均价()
    {
        using var db = TestDbFactory.Create();
        SeedProduct(db, ProductA, "P-033-3", "商品A", "PCS", string.Empty, 0);
        var first = SeedOrder(db, "PO-033-3A", Currency.CNY, 1m, DocumentStatus.Approved, (ProductA, "PCS", 10m));
        var second = SeedOrder(db, "PO-033-3B", Currency.CNY, 1m, DocumentStatus.Approved, (ProductA, "PCS", 20m));
        var ctl = NewController(db);

        var firstId = await CreateStockInAsync(ctl, ProductA, 10m, "PCS", first.Id);
        await ctl.Submit(firstId);
        await ctl.Approve(firstId);

        var secondId = await CreateStockInAsync(ctl, ProductA, 10m, "PCS", second.Id);
        await ctl.Submit(secondId);
        await ctl.Approve(secondId);

        var stock = db.Stocks.Single(s => s.ProductId == ProductA);
        Assert.Equal(20m, stock.Quantity);
        Assert.Equal(300m, stock.TotalCost);                          // 100 + 200
        Assert.Equal(15m, stock.AverageCost);
        Assert.Equal(new[] { 10m, 20m }, db.StockMovements.OrderBy(m => m.Id).Select(m => m.UnitCost).ToArray());
    }

    // ==================== 4. 无链接 / 链接不可用：既有兜底 ====================

    [Fact]
    public async Task 采购入库_无采购订单链接_保持既有兜底口径与原文案()
    {
        using var db = TestDbFactory.Create();
        SeedProduct(db, ProductA, "P-033-4", "商品A", "PCS", string.Empty, 0);
        var ctl = NewController(db);
        var id = await CreateStockInAsync(ctl, ProductA, 6m, "PCS", purchaseOrderId: null);

        await ctl.Submit(id);
        await ctl.Approve(id);

        var movement = db.StockMovements.Single();
        Assert.Equal(0m, movement.UnitCost);                          // 首次入库无历史成本 → 0，不臆造
        Assert.Equal("采购入库单审核入库", movement.Remark);            // 未链接订单时保持 ERP-025 原文案

        var source = await LoadSourceAsync(db, null, ProductA);
        Assert.False(source.HasAuthoritativeOrder);
        Assert.Equal(PurchaseStockInCost.ReasonNoLinkage, source.OrderReason);
        var cost = source.Resolve(ProductA);
        Assert.False(cost.UsedOrderPrice);
        Assert.Equal(PurchaseStockInCost.ReasonNoLinkage, cost.Reason);
        Assert.Equal("既有移动加权平均兜底（入库单未链接采购订单）", cost.RemarkText);
    }

    [Fact]
    public async Task 采购入库_链接的采购订单不存在_回退既有兜底()
    {
        using var db = TestDbFactory.Create();
        SeedProduct(db, ProductA, "P-033-5", "商品A", "PCS", string.Empty, 0);
        SeedStock(db, WarehouseA, ProductA, quantity: 10m, totalCost: 100m);   // 当前均价 10
        var ctl = NewController(db);
        var id = await CreateStockInAsync(ctl, ProductA, 5m, "PCS", purchaseOrderId: 999999L);

        await ctl.Submit(id);
        await ctl.Approve(id);

        var movement = db.StockMovements.Single();
        Assert.Equal(10m, movement.UnitCost);                         // 取当前加权平均成本
        Assert.Equal(50m, movement.Amount);
        Assert.Contains("成本来源：既有移动加权平均兜底", movement.Remark);

        var source = await LoadSourceAsync(db, 999999L, ProductA);
        Assert.False(source.HasAuthoritativeOrder);
        Assert.Equal(PurchaseStockInCost.ReasonOrderNotAuthoritative, source.OrderReason);
    }

    [Theory]
    [InlineData(DocumentStatus.Pending)]
    [InlineData(DocumentStatus.Submitted)]
    [InlineData(DocumentStatus.Cancelled)]
    [InlineData(DocumentStatus.Rejected)]
    public async Task 采购入库_链接订单未审核_不以订单价格计价(DocumentStatus orderStatus)
    {
        using var db = TestDbFactory.Create();
        SeedProduct(db, ProductA, "P-033-6", "商品A", "PCS", string.Empty, 0);
        var order = SeedOrder(db, "PO-033-6", Currency.CNY, 1m, orderStatus, (ProductA, "PCS", 7.5m));
        SeedStock(db, WarehouseA, ProductA, quantity: 10m, totalCost: 100m);
        var ctl = NewController(db);
        var id = await CreateStockInAsync(ctl, ProductA, 4m, "PCS", order.Id);

        await ctl.Submit(id);
        await ctl.Approve(id);

        var movement = db.StockMovements.Single();
        Assert.Equal(10m, movement.UnitCost);                         // 未审核价格不构成成本依据
        Assert.Contains("采购订单不存在或未审核", movement.Remark);
        Assert.Equal(4m * 10m, movement.Amount);
    }

    [Fact]
    public async Task 采购入库_链接订单已软删除_回退既有兜底()
    {
        using var db = TestDbFactory.Create();
        SeedProduct(db, ProductA, "P-033-7", "商品A", "PCS", string.Empty, 0);
        var order = SeedOrder(db, "PO-033-7", Currency.CNY, 1m, DocumentStatus.Approved, (ProductA, "PCS", 7.5m));
        order.IsDeleted = true;
        await db.SaveChangesAsync();
        SeedStock(db, WarehouseA, ProductA, quantity: 10m, totalCost: 100m);
        var ctl = NewController(db);
        var id = await CreateStockInAsync(ctl, ProductA, 4m, "PCS", order.Id);

        await ctl.Submit(id);
        await ctl.Approve(id);

        Assert.Equal(10m, db.StockMovements.Single().UnitCost);
    }

    // ==================== 5. 订单行缺失 / 重复：不任选价格 ====================

    [Fact]
    public async Task 采购入库_订单无该商品明细行_回退既有兜底()
    {
        using var db = TestDbFactory.Create();
        SeedProduct(db, ProductA, "P-033-8", "商品A", "PCS", string.Empty, 0);
        SeedProduct(db, 830002L, "P-033-8B", "商品B", "PCS", string.Empty, 0);
        var order = SeedOrder(db, "PO-033-8", Currency.CNY, 1m, DocumentStatus.Approved, (830002L, "PCS", 9m));
        SeedStock(db, WarehouseA, ProductA, quantity: 10m, totalCost: 100m);
        var ctl = NewController(db);
        var id = await CreateStockInAsync(ctl, ProductA, 4m, "PCS", order.Id);

        await ctl.Submit(id);
        await ctl.Approve(id);

        var movement = db.StockMovements.Single();
        Assert.Equal(10m, movement.UnitCost);
        Assert.Contains("采购订单无该商品明细行", movement.Remark);

        var source = await LoadSourceAsync(db, order.Id, ProductA);
        Assert.Equal(PurchaseStockInCost.ReasonLineMissing, source.Resolve(ProductA).Reason);
    }

    [Fact]
    public async Task 采购入库_订单同商品多行明细_不任选价格回退既有兜底()
    {
        using var db = TestDbFactory.Create();
        SeedProduct(db, ProductA, "P-033-9", "商品A", "PCS", string.Empty, 0);
        var order = SeedOrder(db, "PO-033-9", Currency.CNY, 1m, DocumentStatus.Approved,
            (ProductA, "PCS", 7.5m), (ProductA, "PCS", 99m));
        SeedStock(db, WarehouseA, ProductA, quantity: 10m, totalCost: 100m);
        var ctl = NewController(db);
        var id = await CreateStockInAsync(ctl, ProductA, 4m, "PCS", order.Id);

        await ctl.Submit(id);
        await ctl.Approve(id);

        var movement = db.StockMovements.Single();
        Assert.Equal(10m, movement.UnitCost);                         // 既不是 7.5 也不是 99
        Assert.Contains("采购订单同商品多行明细，价格不唯一", movement.Remark);

        var source = await LoadSourceAsync(db, order.Id, ProductA);
        var cost = source.Resolve(ProductA);
        Assert.Equal(PurchaseStockInCost.ReasonLineAmbiguous, cost.Reason);
        Assert.Equal(0m, cost.UnitCost);
    }

    // ==================== 6. 订单行单位语义：无法折算到基础单位则回退 ====================

    [Theory]
    [InlineData("SET", 12)]        // 订单行单位既不是基础单位也不是装箱单位
    [InlineData("", 12)]           // 订单行单位为空
    [InlineData("CTN", 0)]         // 装箱单位但装箱数无效
    [InlineData("CTN", -3)]        // 装箱数为负
    public async Task 采购入库_订单行单位无法折算到基础单位_回退既有兜底(string lineUnit, int unitsPerPackage)
    {
        using var db = TestDbFactory.Create();
        SeedProduct(db, ProductA, "P-033-10", "商品A", "PCS", "CTN", unitsPerPackage);
        var order = SeedOrder(db, "PO-033-10", Currency.CNY, 1m, DocumentStatus.Approved, (ProductA, lineUnit, 7.5m));
        SeedStock(db, WarehouseA, ProductA, quantity: 10m, totalCost: 100m);
        var ctl = NewController(db);
        var id = await CreateStockInAsync(ctl, ProductA, 4m, "PCS", order.Id);

        await ctl.Submit(id);
        await ctl.Approve(id);

        Assert.Equal(10m, db.StockMovements.Single().UnitCost);

        var source = await LoadSourceAsync(db, order.Id, ProductA);
        Assert.Equal(PurchaseStockInCost.ReasonUnitIncompatible, source.Resolve(ProductA).Reason);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public async Task 采购入库_订单行单价不可用_回退既有兜底(int unitPrice)
    {
        using var db = TestDbFactory.Create();
        SeedProduct(db, ProductA, "P-033-11", "商品A", "PCS", string.Empty, 0);
        var order = SeedOrder(db, "PO-033-11", Currency.CNY, 1m, DocumentStatus.Approved,
            (ProductA, "PCS", (decimal)unitPrice));
        SeedStock(db, WarehouseA, ProductA, quantity: 10m, totalCost: 100m);
        var ctl = NewController(db);
        var id = await CreateStockInAsync(ctl, ProductA, 4m, "PCS", order.Id);

        await ctl.Submit(id);
        await ctl.Approve(id);

        var movement = db.StockMovements.Single();
        Assert.Equal(10m, movement.UnitCost);                         // 零 / 负单价不当作真实成本
        Assert.Contains("订单行单价不可用（空 / 零 / 负数）", movement.Remark);
    }

    [Fact]
    public async Task 采购入库_商品资料缺失_回退既有兜底()
    {
        using var db = TestDbFactory.Create();
        const long OrphanProduct = 830099L;                            // 订单行引用了不存在的商品资料
        var order = SeedOrder(db, "PO-033-12", Currency.CNY, 1m, DocumentStatus.Approved, (OrphanProduct, "PCS", 7.5m));
        var ctl = NewController(db);
        var id = await CreateStockInAsync(ctl, OrphanProduct, 4m, "PCS", order.Id);

        await ctl.Submit(id);
        await ctl.Approve(id);

        var source = await LoadSourceAsync(db, order.Id, OrphanProduct);
        var cost = source.Resolve(OrphanProduct);
        Assert.False(cost.UsedOrderPrice);
        Assert.Equal(PurchaseStockInCost.ReasonProductMissing, cost.Reason);
        Assert.Equal(0m, cost.UnitCost);

        // 无法确认基础单位时按既有兜底落账（新库存行首次入库为 0），不臆造单价
        Assert.Equal(0m, db.StockMovements.Single().UnitCost);
    }

    // ==================== 7. 币种：只接受既有权威换算 ====================

    [Fact]
    public async Task 采购入库_外币订单_按订单持久化汇率换算基础单位成本()
    {
        using var db = TestDbFactory.Create();
        SeedProduct(db, ProductA, "P-033-13", "商品A", "PCS", string.Empty, 0);
        var order = SeedOrder(db, "PO-033-13", Currency.USD, 7.2m, DocumentStatus.Approved, (ProductA, "PCS", 2m));
        var ctl = NewController(db);
        var id = await CreateStockInAsync(ctl, ProductA, 5m, "PCS", order.Id);

        await ctl.Submit(id);
        await ctl.Approve(id);

        var movement = db.StockMovements.Single();
        Assert.Equal(14.4m, movement.UnitCost);                       // 2 USD × 7.2
        Assert.Equal(72m, movement.Amount);
        Assert.Contains("成本来源：采购订单单价（按订单汇率换算）", movement.Remark);

        var source = await LoadSourceAsync(db, order.Id, ProductA);
        var cost = source.Resolve(ProductA);
        Assert.Equal(PurchaseStockInCost.SourceOrderPriceConverted, cost.Source);
        Assert.True(cost.CurrencyConverted);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(0)]
    [InlineData(-7)]
    public async Task 采购入库_外币订单_汇率不可用时不臆造换算并回退既有兜底(int exchangeRate)
    {
        using var db = TestDbFactory.Create();
        SeedProduct(db, ProductA, "P-033-14", "商品A", "PCS", string.Empty, 0);
        var order = SeedOrder(db, "PO-033-14", Currency.USD, exchangeRate, DocumentStatus.Approved,
            (ProductA, "PCS", 2m));
        SeedStock(db, WarehouseA, ProductA, quantity: 10m, totalCost: 100m);
        var ctl = NewController(db);
        var id = await CreateStockInAsync(ctl, ProductA, 5m, "PCS", order.Id);

        await ctl.Submit(id);
        await ctl.Approve(id);

        var movement = db.StockMovements.Single();
        Assert.Equal(10m, movement.UnitCost);                         // 不把 2 USD 当成 2 元，也不按 0 汇率换算
        Assert.Contains("外币订单无可用的权威汇率换算", movement.Remark);

        var source = await LoadSourceAsync(db, order.Id, ProductA);
        Assert.Equal(PurchaseStockInCost.ReasonCurrencyUnavailable, source.Resolve(ProductA).Reason);
        Assert.False(PurchaseStockInCost.IsAuthoritativeExchangeRate(Currency.USD, exchangeRate));
        Assert.True(PurchaseStockInCost.IsAuthoritativeExchangeRate(Currency.CNY, 1m));
    }

    // ==================== 8. 取消 / 重复审核 / 重试：冲销与幂等不被成本规则破坏 ====================

    [Fact]
    public async Task 采购入库_取消冲销_按订单价核减且不改写历史流水()
    {
        using var db = TestDbFactory.Create();
        SeedProduct(db, ProductA, "P-033-15", "商品A", "PCS", string.Empty, 0);
        var order = SeedOrder(db, "PO-033-15", Currency.CNY, 1m, DocumentStatus.Approved, (ProductA, "PCS", 7.5m));
        SeedStock(db, WarehouseA, ProductA, quantity: 10m, totalCost: 100m);
        var ctl = NewController(db);
        var id = await CreateStockInAsync(ctl, ProductA, 10m, "PCS", order.Id);
        await ctl.Submit(id);
        await ctl.Approve(id);
        var original = db.StockMovements.Single();
        Assert.Equal(7.5m, original.UnitCost);

        await ctl.Cancel(id);

        var movements = db.StockMovements.OrderBy(m => m.Id).ToList();
        Assert.Equal(2, movements.Count);
        Assert.True(movements[0].IsReversed);                    // 原流水标记已冲销，但自身不被改写
        Assert.Equal(10m, movements[0].Quantity);
        Assert.Equal(7.5m, movements[0].UnitCost);
        Assert.Equal(75m, movements[0].Amount);
        Assert.Contains("成本来源：采购订单单价", movements[0].Remark);

        var reversal = movements[1];
        Assert.True(reversal.IsReversal);
        Assert.Equal(original.Id, reversal.ReversalOfMovementId);
        Assert.Equal(-MovementDirectionIn, reversal.Direction);
        Assert.Equal(10m, reversal.Quantity);
        Assert.Equal(7.5m, reversal.UnitCost);                   // 红字沿用原成本
        Assert.Equal(-75m, reversal.Amount);

        var stock = db.Stocks.Single(s => s.ProductId == ProductA);
        Assert.Equal(10m, stock.Quantity);
        Assert.Equal(100m, stock.TotalCost);
        Assert.Equal(10m, stock.AverageCost);

        var again = await Assert.ThrowsAsync<BusinessException>(() => ctl.Cancel(id));
        Assert.Equal(ErrorCodes.RuleConflict, again.Code);
        Assert.Equal(2, db.StockMovements.Count());
    }

    [Fact]
    public async Task 采购入库_重复审核与取消后重试_幂等护栏与成本一致()
    {
        using var db = TestDbFactory.Create();
        SeedProduct(db, ProductA, "P-033-16", "商品A", "PCS", string.Empty, 0);
        var order = SeedOrder(db, "PO-033-16", Currency.CNY, 1m, DocumentStatus.Approved, (ProductA, "PCS", 7.5m));
        var ctl = NewController(db);
        var id = await CreateStockInAsync(ctl, ProductA, 10m, "PCS", order.Id);
        await ctl.Submit(id);
        await ctl.Approve(id);

        // 1) 状态护栏：已审核不可再审核
        var byStatus = await Assert.ThrowsAsync<BusinessException>(() => ctl.Approve(id));
        Assert.Equal(ErrorCodes.RuleConflict, byStatus.Code);

        // 2) 绕过状态护栏：流水幂等护栏兜住，成本不被二次写入
        var entity = db.StockIns.Single();
        entity.Status = DocumentStatus.Submitted;
        await db.SaveChangesAsync();
        var byMovement = await Assert.ThrowsAsync<BusinessException>(() => ctl.Approve(id));
        Assert.Contains("已产生库存流水", byMovement.Message);
        Assert.Single(db.StockMovements);
        Assert.Equal(7.5m, db.StockMovements.Single().UnitCost);
        Assert.Equal(10m, db.Stocks.Single(s => s.ProductId == ProductA).Quantity);

        // 3) 恢复真实状态（上面的状态回写只是为验证流水护栏），取消必须走已审核红字冲销路径
        entity.Status = DocumentStatus.Approved;
        await db.SaveChangesAsync();
        await ctl.Cancel(id);
        Assert.Equal(0m, db.Stocks.Single(s => s.ProductId == ProductA).Quantity);

        // 4) 重试：新单按同一权威订单价重新入库，历史流水不被改写
        var retryId = await CreateStockInAsync(ctl, ProductA, 10m, "PCS", order.Id);
        await ctl.Submit(retryId);
        await ctl.Approve(retryId);

        var movements = db.StockMovements.OrderBy(m => m.Id).ToList();
        Assert.Equal(3, movements.Count);
        Assert.Equal(7.5m, movements[2].UnitCost);
        Assert.Equal(75m, movements[2].Amount);
        Assert.True(movements[0].IsReversed);
        Assert.Equal(7.5m, movements[0].UnitCost);
        Assert.Equal(75m, movements[0].Amount);
        var stock = db.Stocks.Single(s => s.ProductId == ProductA);
        Assert.Equal(10m, stock.Quantity);
        Assert.Equal(75m, stock.TotalCost);
        Assert.Equal(7.5m, stock.AverageCost);
    }

    // ==================== 9. 接线静态断言 ====================

    [Fact]
    public void 采购入库控制器_已接线采购订单成本来源解析()
    {
        var controllers = ControllersDirectory();
        var stockIn = File.ReadAllText(Path.Combine(controllers, "StockInController.cs"));
        Assert.Contains("PurchaseStockInCostSource.LoadAsync", stockIn);
        Assert.Contains("costSource.Resolve(d.ProductId)", stockIn);
        Assert.Contains("cost.UnitCost", stockIn);
        Assert.Contains("成本来源：", stockIn);

        var resolver = File.ReadAllText(Path.Combine(controllers, "PurchaseStockInCost.cs"));
        Assert.Contains("UnitsPerPackage", resolver);              // 包装单位单价折算复用同一装箱数元数据
        Assert.Contains("SourceWeightedAverageFallback", resolver); // 未决语义一律回退既有兜底
        Assert.Contains("ReasonLineAmbiguous", resolver);
        Assert.Contains("IsAuthoritativeExchangeRate", resolver);
    }

    // ==================== 助手 ====================

    private static StockInController NewController(ErpDbContext db)
        => new(db, new DocumentNumberService(db), new InventoryService(db));

    private static Task<PurchaseStockInCostSource> LoadSourceAsync(ErpDbContext db, long? purchaseOrderId,
        params long[] productIds)
        => PurchaseStockInCostSource.LoadAsync(db, purchaseOrderId, productIds.Select(id => (long?)id));

    private static async Task<long> CreateStockInAsync(StockInController ctl, long productId, decimal quantity,
        string unit, long? purchaseOrderId)
    {
        var result = await ctl.Create(new StockIn
        {
            StockInDate = DateTime.Today,
            PurchaseOrderId = purchaseOrderId,
            SupplierId = SupplierA,
            WarehouseId = WarehouseA,
            Remark = "ERP-033_TEST",
            Details = new List<StockInDetail>
            {
                new()
                {
                    ProductId = productId,
                    ProductName = $"商品{productId}",
                    Spec = "规格A",
                    Unit = unit,
                    Quantity = quantity
                }
            }
        });
        Assert.IsType<OkObjectResult>(result);
        var data = Assert.IsType<ApiResponse<object>>(((OkObjectResult)result).Value).Data!;
        return (long)data.GetType().GetProperty("Id")!.GetValue(data)!;
    }

    private static void SeedProduct(ErpDbContext db, long id, string code, string name, string unit,
        string packageUnit, int unitsPerPackage)
    {
        db.BaseProducts.Add(new BaseProduct
        {
            Id = id,
            ProductCode = code,
            ProductName = name,
            Spec = "规格A",
            Unit = unit,
            PackageUnit = packageUnit,
            UnitsPerPackage = unitsPerPackage
        });
        db.SaveChanges();
    }

    private static PurchaseOrder SeedOrder(ErpDbContext db, string orderNo, Currency currency,
        decimal exchangeRate, DocumentStatus status, params (long ProductId, string Unit, decimal UnitPrice)[] lines)
    {
        var order = new PurchaseOrder
        {
            OrderNo = orderNo,
            OrderDate = DateTime.Today.AddDays(-5),
            SupplierId = SupplierA,
            Currency = currency,
            ExchangeRate = exchangeRate,
            Status = status
        };
        db.PurchaseOrders.Add(order);
        db.SaveChanges();
        foreach (var (productId, unit, unitPrice) in lines)
        {
            db.PurchaseOrderDetails.Add(new PurchaseOrderDetail
            {
                PurchaseOrderId = order.Id,
                ProductId = productId,
                ProductName = $"商品{productId}",
                Spec = "规格A",
                Unit = unit,
                Quantity = 10m,
                UnitPrice = unitPrice,
                Amount = unitPrice * 10m
            });
        }

        db.SaveChanges();
        return order;
    }

    private static void SeedStock(ErpDbContext db, long warehouseId, long productId, decimal quantity,
        decimal totalCost)
    {
        db.Stocks.Add(new Stock
        {
            WarehouseId = warehouseId,
            ProductId = productId,
            Quantity = quantity,
            AvailableQuantity = quantity,
            TotalCost = totalCost,
            AverageCost = quantity > 0 ? Math.Round(totalCost / quantity, 6) : 0m
        });
        db.SaveChanges();
    }

    private static string ControllersDirectory() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "..", "..", "..", "..", "..", "src", "ERP.Api", "Controllers"));
}
