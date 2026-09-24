using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// 供应商比价「批次多行 → 采购订单」（ERP-027）单元测试：
/// 兼容行合并与不兼容行独立 / 显式跳过、组内与明细交期、归属销售订单分组、服务端重算合计、
/// 重复转换守卫（RefOrderNo 链接 + 备注来源标记，与单行路径同口径）、批次内单号唯一、只读计划、
/// 按行定位批次、LineIds 限定与参数校验、接口路由与前端接线静态断言、报文契约。
/// 说明：全部使用内存数据库，不连接 SQL Server、不启动 API、不触发浏览器验收
/// （按项目策略浏览器验收在开发阶段记为 `browser_deferred`）。
/// </summary>
public class PurchaseQuoteConversionBatchTests
{
    private const string BatchNo = "PQ-BATCH-1";
    private const string ProductName = "批次商品";

    // ==================== 兼容行合并 ====================

    [Fact]
    public async Task 同供应商同币种的多行_合并为一张订单_明细逐行铺开且合计重算()
    {
        using var db = TestDbFactory.Create();
        var line1 = AddLine(db, BatchNo, 88L, 5000m, 2.6m, customerId: 7L, customerName: "义乌客户", deliveryDays: 25);
        var line2 = AddLine(db, BatchNo, 88L, 1000m, 3.5m, customerId: 7L, customerName: "义乌客户", deliveryDays: 15);

        var result = await ConvertAsync(db, BatchNo);

        Assert.Equal(1, result.OrderCount);
        Assert.Equal(2, result.ConvertedLineCount);
        Assert.Equal(16500m, result.TotalAmount);                       // 报价总额故意写成 1，服务端按数量 × 单价重算
        Assert.Empty(result.Skipped);

        var group = Assert.Single(result.Orders);
        Assert.Equal(new[] { line1.Id, line2.Id }, group.LineIds);
        Assert.Equal(2, group.LineCount);
        Assert.Equal(88L, group.SupplierId);
        Assert.Equal("供应商88", group.SupplierName);
        Assert.Equal("USD", group.Currency);
        Assert.Equal(16500m, group.TotalAmount);

        var order = db.PurchaseOrders.Single();
        Assert.StartsWith("PO", order.OrderNo);
        Assert.Equal(DocumentStatus.Pending, order.Status);
        Assert.Equal(DateTime.Today, order.OrderDate);
        Assert.Equal(88L, order.SupplierId);
        Assert.Equal(Currency.USD, order.Currency);
        Assert.Equal(7L, order.OwningCustomerId);
        Assert.Equal(16500m, order.TotalAmount);
        Assert.Equal(DateTime.Today.AddDays(25), order.DeliveryDate);          // 组内最晚交期（一单覆盖所有行）
        Assert.Equal(DateTime.Today.AddDays(25), order.SupplierConfirmedDate);

        // 备注逐行留痕（父备注 + 每行来源标记）
        Assert.Contains("BATCH_TEST", order.Remark);
        Assert.Contains($"来源比价 {BatchNo}（比价行 #{line1.Id}）", order.Remark);
        Assert.Contains($"来源比价 {BatchNo}（比价行 #{line2.Id}）", order.Remark);

        // 明细与来源行一一对应：金额服务端重算、交期保留各自口径、明细备注行级留痕
        var details = db.PurchaseOrderDetails.Where(d => d.PurchaseOrderId == order.Id).OrderBy(d => d.Id).ToList();
        Assert.Equal(2, details.Count);
        Assert.Equal(13000m, details[0].Amount);
        Assert.Equal(3500m, details[1].Amount);
        Assert.Equal(5000m, details[0].Quantity);
        Assert.Equal(2.6m, details[0].UnitPrice);
        Assert.Equal(DateTime.Today.AddDays(25), details[0].DeliveryDate);
        Assert.Equal(DateTime.Today.AddDays(15), details[1].DeliveryDate);
        Assert.Contains($"来源比价 {BatchNo}（比价行 #{line1.Id}）", details[0].Remark);
        Assert.Contains($"来源比价 {BatchNo}（比价行 #{line2.Id}）", details[1].Remark);

        // 来源行逐行留痕：状态「已转采购订单」+ RefOrderNo 回写同一张采购单号
        var saved = db.PurchaseQuotes.OrderBy(q => q.Id).ToList();
        Assert.All(saved, q => Assert.Equal(PurchaseQuoteConversion.ConvertedStatus, q.Status));
        Assert.All(saved, q => Assert.Equal(order.OrderNo, q.RefOrderNo));
        Assert.All(saved, q => Assert.NotNull(q.UpdatedAt));
    }

    [Fact]
    public async Task 不同供应商或币种_保持独立成单_不跨组合并()
    {
        using var db = TestDbFactory.Create();
        var a1 = AddLine(db, BatchNo, 88L, 100m, 2m, customerId: 7L);
        var a2 = AddLine(db, BatchNo, 88L, 200m, 2m, customerId: 7L);
        var b1 = AddLine(db, BatchNo, 99L, 300m, 1m, customerId: 7L);
        var c1 = AddLine(db, BatchNo, 88L, 400m, 5m, currency: "CNY", customerId: 7L);   // 同供应商不同币种

        var result = await ConvertAsync(db, BatchNo);

        Assert.Equal(3, result.OrderCount);
        Assert.Equal(4, result.ConvertedLineCount);
        Assert.Equal(600m + 300m + 2000m, result.TotalAmount);
        Assert.Equal(3, db.PurchaseOrders.Count());
        Assert.Equal(3, db.PurchaseOrders.Select(o => o.OrderNo).ToList().Distinct(StringComparer.OrdinalIgnoreCase).Count());

        var merged = result.Orders.Single(o => o.LineCount == 2);
        Assert.Equal(new[] { a1.Id, a2.Id }, merged.LineIds);
        Assert.Equal(2, db.PurchaseOrderDetails.Count(d => d.PurchaseOrderId == merged.Id));
        Assert.Equal(600m, db.PurchaseOrders.Single(o => o.Id == merged.Id).TotalAmount);

        var otherSupplier = result.Orders.Single(o => o.LineIds.Count == 1 && o.LineIds[0] == b1.Id);
        Assert.Equal(99L, otherSupplier.SupplierId);
        Assert.Equal("USD", otherSupplier.Currency);
        Assert.Equal(300m, otherSupplier.TotalAmount);

        var cny = result.Orders.Single(o => o.Currency == "CNY");
        Assert.Equal(new[] { c1.Id }, cny.LineIds);
        Assert.Equal(2000m, cny.TotalAmount);
    }

    [Fact]
    public async Task 供应商币种相同但付款条件或含税或归属客户不同_保持独立_付款条件去空格比较()
    {
        using var db = TestDbFactory.Create();
        var cash = AddLine(db, BatchNo, 88L, 100m, 2m, paymentTerms: "现结");
        var monthly = AddLine(db, BatchNo, 88L, 100m, 2m, paymentTerms: "月结30天");
        var taxed = AddLine(db, BatchNo, 88L, 100m, 2m, paymentTerms: "现结", taxIncluded: true);
        var customerA = AddLine(db, BatchNo, 88L, 100m, 2m, paymentTerms: "现结", customerId: 7L, customerName: "客户A");
        var customerAWithSpaces = AddLine(db, BatchNo, 88L, 100m, 2m, paymentTerms: " 现结 ", customerId: 7L, customerName: "客户A");

        var result = await ConvertAsync(db, BatchNo);

        Assert.Equal(4, result.OrderCount);
        Assert.Equal(5, result.ConvertedLineCount);
        Assert.Equal(new[] { customerA.Id, customerAWithSpaces.Id },
            result.Orders.Single(o => o.LineCount == 2).LineIds);          // 仅空格差异视为同一组
        Assert.Equal(new[] { cash.Id }, result.Orders.Single(o => o.LineIds.Contains(cash.Id)).LineIds);
        Assert.Equal(new[] { monthly.Id }, result.Orders.Single(o => o.LineIds.Contains(monthly.Id)).LineIds);
        Assert.Equal(new[] { taxed.Id }, result.Orders.Single(o => o.LineIds.Contains(taxed.Id)).LineIds);
    }

    [Fact]
    public async Task 归属销售订单号不同_分到不同订单_同号合并()
    {
        using var db = TestDbFactory.Create();
        db.SalesOrders.Add(new SalesOrder { OrderNo = "SO-B1", OrderDate = DateTime.Today, Status = DocumentStatus.Approved });
        db.SalesOrders.Add(new SalesOrder { OrderNo = "SO-B2", OrderDate = DateTime.Today, Status = DocumentStatus.Approved });
        db.SaveChanges();
        var so1 = db.SalesOrders.Single(o => o.OrderNo == "SO-B1").Id;
        var so2 = db.SalesOrders.Single(o => o.OrderNo == "SO-B2").Id;

        var l1 = AddLine(db, BatchNo, 88L, 100m, 2m, refOrderNo: "SO-B1");
        var l2 = AddLine(db, BatchNo, 88L, 100m, 2m, refOrderNo: "SO-B2");
        var l3 = AddLine(db, BatchNo, 88L, 100m, 2m, refOrderNo: "SO-B1");

        var result = await ConvertAsync(db, BatchNo);

        Assert.Equal(2, result.OrderCount);
        var merged = result.Orders.Single(o => o.LineCount == 2);
        Assert.Equal(new[] { l1.Id, l3.Id }, merged.LineIds);
        var mergedOrder = db.PurchaseOrders.Single(o => o.Id == merged.Id);
        Assert.Equal(so1, mergedOrder.OwningSalesOrderId);
        Assert.Equal("SO-B1", mergedOrder.OwningSalesOrderNo);

        var singleOrder = db.PurchaseOrders.Single(o => o.Id != merged.Id);
        Assert.Equal(new[] { l2.Id }, result.Orders.Single(o => o.Id == singleOrder.Id).LineIds);
        Assert.Equal(so2, singleOrder.OwningSalesOrderId);
        Assert.Equal("SO-B2", singleOrder.OwningSalesOrderNo);
    }

    [Fact]
    public async Task 组内交期不同_订单取最晚_零交期行明细留空_全零时订单交期留空()
    {
        using var db = TestDbFactory.Create();
        AddLine(db, BatchNo, 88L, 100m, 2m, deliveryDays: 0, productName: "现货商品", customerId: 7L);
        AddLine(db, BatchNo, 88L, 200m, 2m, deliveryDays: 20, productName: "定制商品", customerId: 7L);

        await ConvertAsync(db, BatchNo);

        var order = db.PurchaseOrders.Single();
        Assert.Equal(DateTime.Today.AddDays(20), order.DeliveryDate);
        var details = db.PurchaseOrderDetails.Where(d => d.PurchaseOrderId == order.Id).ToList();
        Assert.Equal(2, details.Count);
        Assert.Null(details.Single(d => d.Quantity == 100m).DeliveryDate);                    // 零交期行不臆造交期
        Assert.Equal(DateTime.Today.AddDays(20), details.Single(d => d.Quantity == 200m).DeliveryDate);

        // 组内全部零交期：订单交期留空
        const string zeroBatch = "PQ-BATCH-ZERO";
        AddLine(db, zeroBatch, 88L, 100m, 2m, deliveryDays: 0, customerId: 7L);
        AddLine(db, zeroBatch, 88L, 200m, 2m, deliveryDays: 0, customerId: 7L);
        await ConvertAsync(db, zeroBatch);

        var zeroOrder = db.PurchaseOrders.Single(o => o.Remark.Contains(zeroBatch));
        Assert.Null(zeroOrder.DeliveryDate);
        Assert.Null(zeroOrder.SupplierConfirmedDate);
        Assert.All(db.PurchaseOrderDetails.Where(d => d.PurchaseOrderId == zeroOrder.Id).ToList(),
            d => Assert.Null(d.DeliveryDate));
    }

    // ==================== 不合格行显式跳过 ====================

    [Fact]
    public async Task 批次内不合格行_显式跳过并保留原状态_合格行照常生成()
    {
        using var db = TestDbFactory.Create();
        var ok = AddLine(db, BatchNo, 88L, 100m, 2m);
        var unselected = AddLine(db, BatchNo, 88L, 100m, 2m, selected: false, status: "待比较");
        var discarded = AddLine(db, BatchNo, 88L, 100m, 2m, status: PurchaseQuoteConversion.DiscardedStatus);
        var noSupplier = AddLine(db, BatchNo, 0L, 100m, 2m);
        var alreadyConverted = AddLine(db, BatchNo, 88L, 100m, 2m,
            status: PurchaseQuoteConversion.ConvertedStatus, refOrderNo: "PO-OLD");
        var zeroQuantity = AddLine(db, BatchNo, 88L, 0m, 2m);

        var result = await ConvertAsync(db, BatchNo);

        Assert.Equal(1, result.OrderCount);
        Assert.Equal(1, result.ConvertedLineCount);
        Assert.Equal(5, result.Skipped.Count);
        Assert.Contains(result.Skipped, s => s.LineId == unselected.Id && s.Reason.Contains("未选中供应商"));
        Assert.Contains(result.Skipped, s => s.LineId == discarded.Id && s.Reason.Contains("已放弃"));
        Assert.Contains(result.Skipped, s => s.LineId == noSupplier.Id && s.Reason.Contains("未维护供应商"));
        Assert.Contains(result.Skipped, s => s.LineId == alreadyConverted.Id && s.Reason.Contains("已转为采购订单"));
        Assert.Contains(result.Skipped, s => s.LineId == zeroQuantity.Id && s.Reason.Contains("数量必须大于 0"));
        Assert.All(result.Skipped, s => Assert.Equal(BatchNo, s.QuoteNo));

        Assert.Equal(new[] { ok.Id }, result.Orders.Single().LineIds);

        // 不合格行：状态与 RefOrderNo 均未被改动（不静默丢弃也不误改）
        Assert.Equal("待比较", db.PurchaseQuotes.Single(q => q.Id == unselected.Id).Status);
        Assert.Equal(string.Empty, db.PurchaseQuotes.Single(q => q.Id == unselected.Id).RefOrderNo);
        Assert.Equal(PurchaseQuoteConversion.DiscardedStatus, db.PurchaseQuotes.Single(q => q.Id == discarded.Id).Status);
        Assert.Equal("PO-OLD", db.PurchaseQuotes.Single(q => q.Id == alreadyConverted.Id).RefOrderNo);
        Assert.Single(db.PurchaseOrders);
    }

    [Fact]
    public async Task 批次内没有任何合格行_抛RuleConflict_不落库且计划给出跳过原因()
    {
        using var db = TestDbFactory.Create();
        var l1 = AddLine(db, BatchNo, 88L, 100m, 2m, selected: false, status: "待比较");
        var l2 = AddLine(db, BatchNo, 88L, 100m, 2m, selected: false, status: "待比较");

        var ex = await Assert.ThrowsAsync<BusinessException>(() => NewController(db)
            .BatchToOrder(new PurchaseQuoteBatchConversionRequest { QuoteNo = BatchNo }));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("没有可转换", ex.Message);
        Assert.Contains("未选中供应商", ex.Message);
        Assert.Empty(db.PurchaseOrders);

        var plan = await PlanAsync(db, BatchNo);
        Assert.Equal(0, plan.GroupCount);
        Assert.Equal(0, plan.EligibleLineCount);
        Assert.Equal(2, plan.Skipped.Count);
        Assert.Empty(plan.Groups);
        Assert.Empty(db.PurchaseOrders);
        Assert.Equal("待比较", db.PurchaseQuotes.Single(q => q.Id == l1.Id).Status);
        Assert.Equal(string.Empty, db.PurchaseQuotes.Single(q => q.Id == l2.Id).RefOrderNo);
    }

    // ==================== 重复转换守卫（与单行路径同口径） ====================

    [Fact]
    public async Task 重复批次转换_第二次被拒绝_订单与来源行都不再变化()
    {
        using var db = TestDbFactory.Create();
        var l1 = AddLine(db, BatchNo, 88L, 100m, 2m);
        var l2 = AddLine(db, BatchNo, 88L, 300m, 2m);

        var first = await ConvertAsync(db, BatchNo);
        Assert.Equal(1, first.OrderCount);
        var orderNo = db.PurchaseOrders.Single().OrderNo;
        var detailIds = db.PurchaseOrderDetails.Select(d => d.Id).OrderBy(id => id).ToList();
        Assert.Equal(2, detailIds.Count);                          // 两行合并为一张订单的两行明细

        var ex = await Assert.ThrowsAsync<BusinessException>(() => NewController(db)
            .BatchToOrder(new PurchaseQuoteBatchConversionRequest { QuoteNo = BatchNo }));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("没有可转换", ex.Message);
        Assert.Contains("已生成采购订单", ex.Message);
        Assert.Single(db.PurchaseOrders);                          // 不新增订单
        Assert.Equal(detailIds, db.PurchaseOrderDetails.Select(d => d.Id).OrderBy(id => id).ToList());   // 明细不重复
        Assert.All(db.PurchaseQuotes.ToList(), q => Assert.Equal(orderNo, q.RefOrderNo));

        // 单行接口（ERP-020）与批次路径共用同一守卫：同一来源行不会被再转一次
        var single = await Assert.ThrowsAsync<BusinessException>(() => NewController(db).ToPurchaseOrder(l1.Id));
        Assert.Equal(ErrorCodes.RuleConflict, single.Code);
        Assert.Contains("已生成采购订单", single.Message);
        Assert.Single(db.PurchaseOrders);

        // 只读预填路径同样被守卫拦截
        var prefill = await Assert.ThrowsAsync<BusinessException>(() => NewController(db).OrderPrefill(l2.Id));
        Assert.Equal(ErrorCodes.RuleConflict, prefill.Code);
    }

    [Fact]
    public async Task 批次内部分行已转换_其余行仍可转换且不多生成订单()
    {
        using var db = TestDbFactory.Create();
        var l1 = AddLine(db, BatchNo, 88L, 100m, 2m);
        var l2 = AddLine(db, BatchNo, 88L, 300m, 2m);

        var first = await ConvertAsync(db, BatchNo, lineIds: new List<long> { l1.Id });     // 先转掉一行
        Assert.Equal(new[] { l1.Id }, first.Orders.Single().LineIds);

        var result = await ConvertAsync(db, BatchNo);                                       // 再转批次内剩余行

        Assert.Equal(1, result.OrderCount);
        Assert.Equal(1, result.ConvertedLineCount);
        Assert.Equal(new[] { l2.Id }, result.Orders.Single().LineIds);
        Assert.Contains(result.Skipped, s => s.LineId == l1.Id && s.Reason.Contains("已生成采购订单"));
        Assert.Equal(2, db.PurchaseOrders.Count());
        Assert.Equal("已转采购订单", db.PurchaseQuotes.Single(q => q.Id == l1.Id).Status);
        Assert.Equal(2, db.PurchaseOrderDetails.Count());
    }

    [Fact]
    public async Task 批次生成的订单被软删除_来源行回退后可重新生成()
    {
        using var db = TestDbFactory.Create();
        var line = AddLine(db, BatchNo, 88L, 100m, 2m);
        await ConvertAsync(db, BatchNo);

        var generated = db.PurchaseOrders.Single();
        generated.IsDeleted = true;
        var saved = db.PurchaseQuotes.Single();
        saved.Status = PurchaseQuoteConversion.SelectedStatus;
        saved.RefOrderNo = string.Empty;
        db.SaveChanges();
        db.ChangeTracker.Clear();

        var again = await ConvertAsync(db, BatchNo);

        Assert.Equal(1, again.OrderCount);
        Assert.Equal(new[] { line.Id }, again.Orders.Single().LineIds);
        Assert.Equal(2, db.PurchaseOrders.Count());
        Assert.Single(db.PurchaseOrders.Where(o => !o.IsDeleted));
    }

    // ==================== 批次内单号唯一性 ====================

    [Fact]
    public async Task 批次转换_多张订单单号互不相同()
    {
        using var db = TestDbFactory.Create();
        AddLine(db, BatchNo, 88L, 100m, 2m);
        AddLine(db, BatchNo, 99L, 100m, 2m);
        AddLine(db, BatchNo, 97L, 100m, 2m);

        var result = await ConvertAsync(db, BatchNo);

        Assert.Equal(3, result.OrderCount);
        var numbers = db.PurchaseOrders.Select(o => o.OrderNo).ToList();
        Assert.Equal(3, numbers.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(result.Orders, o => Assert.Contains(o.OrderNo, numbers));
        Assert.All(result.Orders, o => Assert.True(o.Id > 0));
    }

    [Fact]
    public async Task 单号唯一守卫_库内重号与批次内重号追加后缀_软删除号可复用()
    {
        using var db = TestDbFactory.Create();
        db.PurchaseOrders.Add(new PurchaseOrder { OrderNo = "PO-DUP", OrderDate = DateTime.Today });
        db.SaveChanges();

        var assigned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Assert.Equal("PO-DUP-2", await PurchaseQuoteConversion.EnsureUniqueOrderNoAsync(db, "PO-DUP", assigned));
        Assert.Equal("PO-DUP-3", await PurchaseQuoteConversion.EnsureUniqueOrderNoAsync(db, "PO-DUP", assigned));
        Assert.Equal("PO-FRESH", await PurchaseQuoteConversion.EnsureUniqueOrderNoAsync(db, "PO-FRESH", assigned));
        Assert.Equal(3, assigned.Count);

        // 软删除的历史单号不阻断复用（与重复生成守卫同一口径）
        db.PurchaseOrders.Single(o => o.OrderNo == "PO-DUP").IsDeleted = true;
        db.SaveChanges();
        db.ChangeTracker.Clear();
        var reuse = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Assert.Equal("PO-DUP", await PurchaseQuoteConversion.EnsureUniqueOrderNoAsync(db, "PO-DUP", reuse));

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            PurchaseQuoteConversion.EnsureUniqueOrderNoAsync(db, "   ", reuse));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    // ==================== 只读计划 ====================

    [Fact]
    public async Task 批次计划_只读不落库不占号_给出分组重算合计与跳过原因()
    {
        using var db = TestDbFactory.Create();
        var l1 = AddLine(db, BatchNo, 88L, 5000m, 2.6m, customerId: 7L, customerName: "义乌客户");
        var l2 = AddLine(db, BatchNo, 88L, 1000m, 3.5m, customerId: 7L, customerName: "义乌客户");
        var l3 = AddLine(db, BatchNo, 99L, 2000m, 1.2m, customerId: 7L, customerName: "义乌客户");
        var skippedLine = AddLine(db, BatchNo, 99L, 100m, 1m, selected: false, status: "待比较", customerId: 7L);
        var seedUpdatedAt = db.PurchaseQuotes.Single(q => q.Id == l1.Id).UpdatedAt;

        var plan = await PlanAsync(db, BatchNo);

        Assert.Equal(PurchaseQuoteConversion.PurchaseQuoteSourceType, plan.SourceType);
        Assert.Equal(BatchNo, plan.SourceNo);
        Assert.Equal(4, plan.BatchLineCount);
        Assert.Equal(3, plan.EligibleLineCount);
        Assert.Equal(2, plan.GroupCount);
        Assert.Equal(13000m + 3500m + 2400m, plan.TotalAmount);
        Assert.Equal(skippedLine.Id, Assert.Single(plan.Skipped).LineId);

        var merged = plan.Groups.Single(g => g.LineCount == 2);
        Assert.Equal(new[] { l1.Id, l2.Id }, merged.LineIds);
        Assert.Equal(88L, merged.SupplierId);
        Assert.Equal("供应商88", merged.SupplierName);
        Assert.Equal("USD", merged.Currency);
        Assert.Equal(7L, merged.OwningCustomerId);
        Assert.Equal("义乌客户", merged.OwningCustomerName);
        Assert.Equal("T/T 30%", merged.PaymentTerms);
        Assert.False(merged.TaxIncluded);
        Assert.Equal(DateTime.Today.AddDays(10), merged.DeliveryDate);
        Assert.Equal(16500m, merged.TotalAmount);
        Assert.Equal(string.Empty, merged.Order.OrderNo);           // 计划不占用单据号
        Assert.Equal(16500m, merged.Order.TotalAmount);             // 草稿合计同样由服务端重算
        Assert.Equal(2, merged.Order.Details.Count);
        Assert.Equal(13000m, merged.Order.Details[0].Amount);
        Assert.Contains($"来源比价 {BatchNo}（比价行 #{l1.Id}）", merged.Order.Remark);

        var single = plan.Groups.Single(g => g.LineCount == 1);
        Assert.Equal(new[] { l3.Id }, single.LineIds);
        Assert.Equal(2400m, single.TotalAmount);
        Assert.Equal(99L, single.SupplierId);

        // 只读：不写库、不改来源状态、不刷新审计时间
        Assert.Empty(db.PurchaseOrders);
        Assert.Empty(db.PurchaseOrderDetails);
        var savedQuote = db.PurchaseQuotes.Single(q => q.Id == l1.Id);
        Assert.Equal(PurchaseQuoteConversion.SelectedStatus, savedQuote.Status);
        Assert.Equal(string.Empty, savedQuote.RefOrderNo);
        Assert.Equal(seedUpdatedAt, savedQuote.UpdatedAt);
    }

    [Fact]
    public async Task 按比价行Id定位批次_计划与转换结果一致()
    {
        using var db = TestDbFactory.Create();
        var l1 = AddLine(db, BatchNo, 88L, 100m, 2m);
        var l2 = AddLine(db, BatchNo, 88L, 200m, 2m);

        var plan = await PlanAsync(db, null, l1.Id);
        Assert.Equal(BatchNo, plan.SourceNo);
        Assert.Equal(2, plan.EligibleLineCount);
        Assert.Empty(db.PurchaseOrders);

        var result = await ConvertAsync(db, lineId: l1.Id);

        Assert.Equal(1, result.OrderCount);
        Assert.Equal(2, result.ConvertedLineCount);
        Assert.Equal(new[] { l1.Id, l2.Id }, result.Orders.Single().LineIds);
    }

    // ==================== 参数校验与限定转换 ====================

    [Fact]
    public async Task 指定LineIds_只转换这些行_不影响同批次其他行()
    {
        using var db = TestDbFactory.Create();
        var l1 = AddLine(db, BatchNo, 88L, 100m, 2m);
        var l2 = AddLine(db, BatchNo, 88L, 200m, 3m);

        var result = await ConvertAsync(db, BatchNo, lineIds: new List<long> { l2.Id });

        Assert.Equal(1, result.OrderCount);
        Assert.Equal(1, result.ConvertedLineCount);
        Assert.Equal(new[] { l2.Id }, result.Orders.Single().LineIds);
        Assert.Empty(result.Skipped);                     // 未列出的行不参与本次转换，也不计为跳过
        Assert.Equal(600m, result.TotalAmount);
        Assert.Equal(PurchaseQuoteConversion.SelectedStatus, db.PurchaseQuotes.Single(q => q.Id == l1.Id).Status);
        Assert.Equal(PurchaseQuoteConversion.ConvertedStatus, db.PurchaseQuotes.Single(q => q.Id == l2.Id).Status);
        Assert.Single(db.PurchaseOrderDetails);
    }

    [Fact]
    public async Task 指定行不属于该批次_抛InvalidParameter_且不落库()
    {
        using var db = TestDbFactory.Create();
        AddLine(db, BatchNo, 88L, 100m, 2m);
        var other = AddLine(db, "PQ-OTHER-BATCH", 88L, 100m, 2m);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => NewController(db).BatchToOrder(
            new PurchaseQuoteBatchConversionRequest { QuoteNo = BatchNo, LineIds = new List<long> { other.Id } }));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Contains("不属于比价批次", ex.Message);
        Assert.Empty(db.PurchaseOrders);
    }

    [Fact]
    public async Task 批次号与行Id都为空_抛InvalidParameter_批次或比价行不存在_抛NotFound()
    {
        using var db = TestDbFactory.Create();
        var ctl = NewController(db);

        var blank = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.BatchToOrder(new PurchaseQuoteBatchConversionRequest()));
        Assert.Equal(ErrorCodes.InvalidParameter, blank.Code);
        Assert.Contains("请提供比价批次号或比价行 Id", blank.Message);

        var nullBody = await Assert.ThrowsAsync<BusinessException>(() => ctl.BatchToOrder(null!));
        Assert.Equal(ErrorCodes.InvalidParameter, nullBody.Code);
        Assert.Contains("请求内容不能为空", nullBody.Message);

        var noBatch = await Assert.ThrowsAsync<BusinessException>(() => ctl.BatchOrderPlan("PQ-NOT-EXIST", null));
        Assert.Equal(ErrorCodes.NotFound, noBatch.Code);
        Assert.Contains("不存在", noBatch.Message);

        var noLine = await Assert.ThrowsAsync<BusinessException>(() => ctl.BatchOrderPlan(null, 987654L));
        Assert.Equal(ErrorCodes.NotFound, noLine.Code);

        var blankPlan = await Assert.ThrowsAsync<BusinessException>(() => ctl.BatchOrderPlan(null, null));
        Assert.Equal(ErrorCodes.InvalidParameter, blankPlan.Code);
    }

    // ==================== 备注截断与来源标记保留 ====================

    [Fact]
    public async Task 备注超长_父备注截断而来源标记完整保留_明细备注逐行标记()
    {
        using var db = TestDbFactory.Create();
        var l1 = AddLine(db, BatchNo, 88L, 1m, 1m, remark: new string('备', 400));
        var l2 = AddLine(db, BatchNo, 88L, 1m, 1m, remark: new string('注', 400));

        await ConvertAsync(db, BatchNo);

        var order = db.PurchaseOrders.Single();
        Assert.True(order.Remark.Length <= 500);
        Assert.Contains($"来源比价 {BatchNo}（比价行 #{l1.Id}）", order.Remark);    // 截断不丢来源标记（重复生成判据）
        Assert.Contains($"来源比价 {BatchNo}（比价行 #{l2.Id}）", order.Remark);

        var details = db.PurchaseOrderDetails.Where(d => d.PurchaseOrderId == order.Id).OrderBy(d => d.Id).ToList();
        Assert.All(details, d => Assert.True(d.Remark.Length <= 500));
        Assert.Contains($"来源比价 {BatchNo}（比价行 #{l1.Id}）", details[0].Remark);
        Assert.Contains($"来源比价 {BatchNo}（比价行 #{l2.Id}）", details[1].Remark);
    }

    // ==================== 接口路由与前端接线 ====================

    [Fact]
    public void 批次接口路由_与前端调用路径一致()
    {
        AssertRoute(typeof(PurchaseQuoteController), nameof(PurchaseQuoteController.BatchOrderPlan), "batch-order-plan");
        AssertRoute(typeof(PurchaseQuoteController), nameof(PurchaseQuoteController.BatchToOrder), "batch-to-order");
    }

    [Fact]
    public void 前端接线_批次行操作与工具栏入口_接口路径与脚本加载()
    {
        var modules = File.ReadAllText(Path.Combine(JsDirectory(), "modules.js"));
        var js = File.ReadAllText(Path.Combine(JsDirectory(), "purchase-order-conversion.js"));
        var indexHtml = File.ReadAllText(Path.Combine(JsDirectory(), "..", "index.html"));

        // 行操作（不带括号，行号由列表渲染器拼接）与工具栏入口（带括号，直接作为 onclick）
        Assert.Contains("onclick: 'purchaseQuoteBatchToOrder'", modules);
        Assert.Contains("onclick: 'purchaseQuoteBatchToOrderByNo()'", modules);
        Assert.Contains("function purchaseQuoteBatchToOrder(", js);
        Assert.Contains("function purchaseQuoteBatchToOrderByNo(", js);
        Assert.Contains("statuses: ['已选中']", modules);              // 入口只在「已选中」行显示

        // 函数 → 后端接口路径（拼写漂移会让按钮点了没反应）
        Assert.Contains("batch-order-plan", js);
        Assert.Contains("batch-to-order", js);
        Assert.Contains("`${cfg.api}/batch-order-plan?${query}`", js);
        Assert.Contains("api: '/api/purchase/quotes'", js);

        // 必须先取只读计划并经用户确认，才允许落库
        Assert.Contains("confirm(batchPlanText(plan, groups))", js);
        Assert.Contains("batchSkipText(plan.skipped)", js);
        Assert.Contains("if (CURRENT_LOADER) CURRENT_LOADER();", js);   // 落库后刷新列表，页面立即反映来源状态

        Assert.Contains("/js/purchase-order-conversion.js", indexHtml);
    }

    [Fact]
    public void 批次转换报文_JSON字段名符合前端约定()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());

        var payload = new PurchaseQuoteBatchConversionResult
        {
            SourceType = PurchaseQuoteConversion.PurchaseQuoteSourceType,
            SourceNo = "PQ-1",
            OrderCount = 2,
            ConvertedLineCount = 3,
            TotalAmount = 19700m,
            Orders = new List<PurchaseQuoteBatchOrderResult>
            {
                new()
                {
                    Id = 1, OrderNo = "PO-202609240001", SupplierId = 88L, SupplierName = "供应商88",
                    Currency = "USD", LineIds = new List<long> { 11L, 12L }, LineCount = 2, TotalAmount = 16500m
                }
            },
            Skipped = new List<PurchaseQuoteBatchSkip>
            {
                new()
                {
                    LineId = 13L, QuoteNo = "PQ-1", ProductName = "批次商品",
                    Reason = "该比价行未选中供应商，请先勾选「选中该供应商」并保存后再生成采购订单"
                }
            }
        };

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(payload, options));
        var root = doc.RootElement;
        Assert.Equal("PurchaseQuote", root.GetProperty("sourceType").GetString());
        Assert.Equal("PQ-1", root.GetProperty("sourceNo").GetString());
        Assert.Equal(2, root.GetProperty("orderCount").GetInt32());
        Assert.Equal(3, root.GetProperty("convertedLineCount").GetInt32());
        Assert.Equal(19700m, root.GetProperty("totalAmount").GetDecimal());

        var order = root.GetProperty("orders")[0];
        Assert.Equal("PO-202609240001", order.GetProperty("orderNo").GetString());
        Assert.Equal(88L, order.GetProperty("supplierId").GetInt64());
        Assert.Equal("供应商88", order.GetProperty("supplierName").GetString());
        Assert.Equal("USD", order.GetProperty("currency").GetString());
        Assert.Equal(2, order.GetProperty("lineIds").GetArrayLength());
        Assert.Equal(16500m, order.GetProperty("totalAmount").GetDecimal());

        var skipped = root.GetProperty("skipped")[0];
        Assert.Equal(13L, skipped.GetProperty("lineId").GetInt64());
        Assert.Contains("未选中供应商", skipped.GetProperty("reason").GetString());
    }

    [Fact]
    public void 批次计划报文_JSON字段名与枚举口径符合前端约定()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());

        var payload = new PurchaseQuoteBatchPlan
        {
            SourceType = PurchaseQuoteConversion.PurchaseQuoteSourceType,
            SourceNo = "PQ-1",
            BatchLineCount = 3,
            EligibleLineCount = 2,
            GroupCount = 1,
            TotalAmount = 16500m,
            Groups = new List<PurchaseQuoteBatchGroupPlan>
            {
                new()
                {
                    SupplierId = 88L, SupplierName = "供应商88", Currency = "USD",
                    OwningCustomerId = 7L, OwningCustomerName = "义乌客户", OwningSalesOrderNo = string.Empty,
                    PaymentTerms = "T/T 30%", TaxIncluded = true, DeliveryDate = new DateTime(2026, 10, 19),
                    LineIds = new List<long> { 11L, 12L }, LineCount = 2, TotalAmount = 16500m,
                    Order = new PurchaseOrder
                    {
                        OrderDate = new DateTime(2026, 9, 24), SupplierId = 88L, Currency = Currency.USD,
                        ExchangeRate = 1m, TaxIncluded = true, OwningCustomerId = 7L,
                        Details = new List<PurchaseOrderDetail>
                        {
                            new() { ProductId = 310L, ProductName = "批次商品", Quantity = 5000m, UnitPrice = 2.6m, Amount = 13000m }
                        }
                    }
                }
            }
        };

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(payload, options));
        var root = doc.RootElement;
        Assert.Equal(1, root.GetProperty("groupCount").GetInt32());
        Assert.Equal(2, root.GetProperty("eligibleLineCount").GetInt32());
        Assert.Equal(3, root.GetProperty("batchLineCount").GetInt32());
        Assert.Equal(0, root.GetProperty("skipped").GetArrayLength());

        var group = root.GetProperty("groups")[0];
        Assert.Equal(88L, group.GetProperty("supplierId").GetInt64());
        Assert.Equal("USD", group.GetProperty("currency").GetString());
        Assert.Equal("2026-10-19", group.GetProperty("deliveryDate").GetString()![..10]);
        Assert.Equal(2, group.GetProperty("lineIds").GetArrayLength());
        Assert.Equal(16500m, group.GetProperty("totalAmount").GetDecimal());
        Assert.Equal("USD", group.GetProperty("order").GetProperty("currency").GetString());
        Assert.Equal(2.6m, group.GetProperty("order").GetProperty("details")[0].GetProperty("unitPrice").GetDecimal());
    }

    // ==================== 工厂与种子数据 ====================

    private static PurchaseQuoteController NewController(ErpDbContext db)
        => new(new GenericService<PurchaseQuote>(db), db, new DocumentNumberService(db));

    /// <summary>走控制器批次转换端点（与前端同一入口），断言业务码为 0</summary>
    private static async Task<PurchaseQuoteBatchConversionResult> ConvertAsync(ErpDbContext db, string? quoteNo = null,
        long? lineId = null, List<long>? lineIds = null)
    {
        var ok = Assert.IsType<OkObjectResult>(await NewController(db).BatchToOrder(
            new PurchaseQuoteBatchConversionRequest { QuoteNo = quoteNo, LineId = lineId, LineIds = lineIds }));
        var response = Assert.IsType<ApiResponse<PurchaseQuoteBatchConversionResult>>(ok.Value);
        Assert.Equal(0, response.Code);
        return response.Data!;
    }

    /// <summary>走控制器批次计划端点（只读）</summary>
    private static async Task<PurchaseQuoteBatchPlan> PlanAsync(ErpDbContext db, string? quoteNo, long? lineId = null)
    {
        var ok = Assert.IsType<OkObjectResult>(await NewController(db).BatchOrderPlan(quoteNo, lineId));
        var response = Assert.IsType<ApiResponse<PurchaseQuoteBatchPlan>>(ok.Value);
        Assert.Equal(0, response.Code);
        return response.Data!;
    }

    /// <summary>断言控制器方法上存在指定路由模板（批次接口无 `{id:long}` 前缀，故按模板全等匹配）</summary>
    private static void AssertRoute(Type controller, string methodName, string template)
    {
        var method = controller.GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(method);
        var templates = method!.GetCustomAttributes<HttpMethodAttribute>(true)
            .Select(a => a.Template ?? string.Empty)
            .ToList();
        Assert.Contains(template, templates);
    }

    /// <summary>前端脚本目录（沿测试程序集输出目录上溯到仓库根，与既有转换测试同一约定）</summary>
    private static string JsDirectory() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "..", "..", "..", "..", "..", "src", "ERP.Api", "wwwroot", "js"));

    /// <summary>
    /// 比价行：报价总额故意写成 1（验证服务端按采购订单口径重算，不采信来源总额）；
    /// 默认「已选中」+ 状态已选中、交期 10 天、USD、付款条件 T/T 30%、无归属客户与关联销售订单。
    /// </summary>
    private static PurchaseQuote AddLine(ErpDbContext db, string quoteNo, long supplierId, decimal quantity, decimal price,
        string currency = "USD", bool selected = true, string status = PurchaseQuoteConversion.SelectedStatus,
        string productName = ProductName, int deliveryDays = 10, string refOrderNo = "", string paymentTerms = "T/T 30%",
        bool taxIncluded = false, long? customerId = null, string customerName = "", string remark = "BATCH_TEST")
    {
        var line = new PurchaseQuote
        {
            QuoteNo = quoteNo,
            QuoteDate = DateTime.Today,
            ProductId = 310L,
            ProductName = productName,
            Spec = "大号",
            Unit = "PCS",
            Quantity = quantity,
            SupplierId = supplierId,
            SupplierName = $"供应商{supplierId}",
            SupplierType = "档口",
            QuotePrice = price,
            TotalAmount = 1m,
            Currency = currency,
            TaxIncluded = taxIncluded,
            DeliveryDays = deliveryDays,
            MinOrderQty = 1000,
            PaymentTerms = paymentTerms,
            IsSelected = selected,
            Status = status,
            CustomerId = customerId,
            CustomerName = customerName,
            RefOrderNo = refOrderNo,
            Remark = remark
        };
        db.PurchaseQuotes.Add(line);
        db.SaveChanges();
        return line;
    }
}