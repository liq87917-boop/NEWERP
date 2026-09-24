using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using System.Reflection;
using System.Text.Json;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-032 销售订单出货 / 财务进度报表（只读派生）单元测试：
/// 出货数量口径（只计已审核销售出库单、待提交单列、驳回 / 取消 / 软删除不计、库存流水不叠加、销售退货不冲减）、
/// 收款链接（复用 ERP-028 的既有引用规则、他币种 / 未审核仅列出、无引用为未知而非 0）、
/// 客户 / 币种 / 日期 / 出货状态 / 收款链接状态筛选（并断言「筛选结果 ≡ 派生结果」）、
/// 分组与币种汇总（绝不跨币种合并）、分页有界与固定查询次数（无 N+1、只读不写库）、
/// 命中批量上限时的诚实处理（数量 / 金额记为未知）、参数校验、非应收账款台账的字段与文案边界、接口与前端接线。
/// <para>说明：全部使用内存数据库，不连接 SQL Server、不启动 API、不执行任何 SQL 或 seed，不运行浏览器验收。</para>
/// </summary>
public class SalesOrderShipmentFinanceReportTests
{
    private static readonly DateTime AsOf = new(2026, 9, 24);
    private const long CustomerA = 932001L;
    private const long CustomerB = 932002L;
    private const long ProductA = 932101L;
    private const long ProductB = 932102L;

    // ==================== 1. 部分出货：已订 / 已出 / 待审 / 未出 ====================

    [Fact]
    public async Task Report_exposes_ordered_shipped_and_outstanding_quantities_for_a_partially_shipped_order()
    {
        using var db = TestDbFactory.Create();
        SeedCustomer(db, CustomerA, "甲客户");
        var order = SeedOrder(db, "SO-032-1", CustomerA, Currency.USD, 1000m);
        SeedDetail(db, order.Id, ProductA, 10m);
        SeedStockOut(db, "CK-032-1", order.Id, DocumentStatus.Approved, (ProductA, 4m));
        SeedStockOut(db, "CK-032-2", order.Id, DocumentStatus.Submitted, (ProductA, 2m));
        SeedStockOut(db, "CK-032-3", order.Id, DocumentStatus.Rejected, (ProductA, 3m));
        await db.SaveChangesAsync();

        var report = await SalesOrderShipmentFinanceReport.ForQueryAsync(db, Query());

        var row = Assert.Single(Assert.Single(report.Groups).Orders);
        Assert.Equal(10m, row.OrderedQuantity!.Value);
        Assert.Equal(4m, row.ShippedQuantity!.Value);          // 只有「已审核」出库单计入已出货
        Assert.Equal(2m, row.PendingShipmentQuantity!.Value);  // 已提交只单列，不计入
        Assert.Equal(6m, row.OutstandingQuantity!.Value);      // 已提交 / 已驳回都不冲抵订单数量
        Assert.Equal(SalesOrderProgress.ShipmentPartial, row.ShipmentStatus);
        Assert.True(row.HasApprovedShipment);
        Assert.Equal(3, row.ShipmentDocumentCount);
        Assert.Equal(1, row.ApprovedShipmentCount);
        Assert.Equal(1, report.ShippedOrderCount);
        Assert.Equal(0, report.UnshippedOrderCount);
        Assert.Equal(0, report.UnknownShipmentOrderCount);
    }

    // ==================== 2. 未出货：0 有依据，而不是未知 ====================

    [Fact]
    public async Task Report_reports_unshipped_order_as_evidence_based_zero()
    {
        using var db = TestDbFactory.Create();
        SeedCustomer(db, CustomerA, "甲客户");
        var order = SeedOrder(db, "SO-032-2", CustomerA, Currency.USD, 500m);
        SeedDetail(db, order.Id, ProductA, 5m);

        var report = await SalesOrderShipmentFinanceReport.ForQueryAsync(db, Query());

        var row = Assert.Single(Assert.Single(report.Groups).Orders);
        Assert.False(row.HasApprovedShipment);
        Assert.Equal(SalesOrderProgress.ShipmentNone, row.ShipmentStatus);
        Assert.Equal(0m, row.ShippedQuantity!.Value);          // 有依据的 0（确实没有已审核出库单）
        Assert.Equal(5m, row.OutstandingQuantity!.Value);
        Assert.Contains("未出货（0 有依据）", row.Note);
        Assert.Equal(1, report.UnshippedOrderCount);
    }

    // ==================== 3. 出齐与超发 ====================

    [Fact]
    public async Task Report_marks_fully_shipped_and_over_shipped_orders()
    {
        using var db = TestDbFactory.Create();
        SeedCustomer(db, CustomerA, "甲客户");
        var exact = SeedOrder(db, "SO-032-3", CustomerA, Currency.USD, 100m);
        SeedDetail(db, exact.Id, ProductA, 10m);
        SeedStockOut(db, "CK-032-4", exact.Id, DocumentStatus.Approved, (ProductA, 10m));
        var over = SeedOrder(db, "SO-032-4", CustomerA, Currency.USD, 200m);
        SeedDetail(db, over.Id, ProductA, 10m);
        SeedStockOut(db, "CK-032-5", over.Id, DocumentStatus.Approved, (ProductA, 12m));

        var report = await SalesOrderShipmentFinanceReport.ForQueryAsync(db, Query());

        var rows = Assert.Single(report.Groups).Orders;
        var exactRow = rows.Single(r => r.OrderNo == "SO-032-3");
        Assert.Equal(SalesOrderProgress.ShipmentComplete, exactRow.ShipmentStatus);
        Assert.Equal(0m, exactRow.OutstandingQuantity!.Value);

        var overRow = rows.Single(r => r.OrderNo == "SO-032-4");
        Assert.Equal(SalesOrderProgress.ShipmentOver, overRow.ShipmentStatus);
        Assert.Equal(12m, overRow.ShippedQuantity!.Value);     // 超发数量体现在订单行上
        Assert.Equal(0m, overRow.OutstandingQuantity!.Value);
    }

    // ==================== 4. 订单外商品：显式单列，不并入订单行 ====================

    [Fact]
    public async Task Report_keeps_products_outside_the_order_separately()
    {
        using var db = TestDbFactory.Create();
        SeedCustomer(db, CustomerA, "甲客户");
        var order = SeedOrder(db, "SO-032-5", CustomerA, Currency.USD, 100m);
        SeedDetail(db, order.Id, ProductA, 10m);
        SeedStockOut(db, "CK-032-6", order.Id, DocumentStatus.Approved, (ProductB, 3m));

        var report = await SalesOrderShipmentFinanceReport.ForQueryAsync(db, Query());
        var row = Assert.Single(Assert.Single(report.Groups).Orders);
        Assert.Equal(3m, row.ShippedQuantity!.Value);           // 单据口径含订单外商品
        Assert.Equal(10m, row.OutstandingQuantity!.Value);      // 订单行未出货仍是 10

        // 逐单视图把订单外商品显式单列
        var view = await SalesOrderProgress.ForSalesOrderAsync(db, order.Id);
        var unmatched = Assert.Single(view.UnmatchedShipments);
        Assert.Equal(ProductB, unmatched.ProductId);
        Assert.Equal(3m, unmatched.ShippedQuantity);
        Assert.Equal(3m, view.Shipment.UnmatchedShippedQuantity);
        Assert.Equal(0m, view.Shipment.MatchedShippedQuantity);
        Assert.Equal(0m, Assert.Single(view.Lines).ShippedQuantity);
    }

    // ==================== 5. 同商品多行：按明细顺序依次冲抵 ====================

    [Fact]
    public async Task Report_allocates_the_same_product_across_order_lines_in_detail_order()
    {
        using var db = TestDbFactory.Create();
        SeedCustomer(db, CustomerA, "甲客户");
        var order = SeedOrder(db, "SO-032-6", CustomerA, Currency.USD, 80m);
        SeedDetail(db, order.Id, ProductA, 3m);
        SeedDetail(db, order.Id, ProductA, 5m);
        SeedStockOut(db, "CK-032-7", order.Id, DocumentStatus.Approved, (ProductA, 4m));

        var view = await SalesOrderProgress.ForSalesOrderAsync(db, order.Id);

        Assert.Equal(2, view.Lines.Count);
        Assert.Equal(3m, view.Lines[0].ShippedQuantity);        // 第一行先冲抵
        Assert.Equal(1m, view.Lines[1].ShippedQuantity);
        Assert.Equal(4m, view.Shipment.ShippedQuantity);
        Assert.Equal(4m, view.Shipment.OutstandingQuantity);    // 8 − 4
        Assert.Equal(SalesOrderProgress.ShipmentPartial, view.Shipment.ShipmentStatus);
    }

    // ==================== 6. 软删除 / 其他订单的出库单不计入 ====================

    [Fact]
    public async Task Report_ignores_deleted_and_foreign_stock_outs()
    {
        using var db = TestDbFactory.Create();
        SeedCustomer(db, CustomerA, "甲客户");
        var order = SeedOrder(db, "SO-032-7", CustomerA, Currency.USD, 100m);
        SeedDetail(db, order.Id, ProductA, 10m);
        var deleted = SeedStockOut(db, "CK-032-8", order.Id, DocumentStatus.Approved, (ProductA, 6m));
        deleted.IsDeleted = true;
        var otherOrder = SeedOrder(db, "SO-032-8", CustomerA, Currency.USD, 100m);
        SeedDetail(db, otherOrder.Id, ProductA, 10m);
        SeedStockOut(db, "CK-032-9", otherOrder.Id, DocumentStatus.Approved, (ProductA, 7m));
        var unlinked = SeedStockOut(db, "CK-032-10", null, DocumentStatus.Approved, (ProductA, 8m));   // 无销售订单来源

        var report = await SalesOrderShipmentFinanceReport.ForQueryAsync(db, Query());

        var firstRow = report.Groups.Single().Orders.Single(r => r.OrderNo == "SO-032-7");
        Assert.Equal(0m, firstRow.ShippedQuantity!.Value);
        Assert.False(firstRow.HasApprovedShipment);
        var secondRow = report.Groups.Single().Orders.Single(r => r.OrderNo == "SO-032-8");
        Assert.Equal(7m, secondRow.ShippedQuantity!.Value);
        Assert.True(unlinked.Id > 0);
    }

    // ==================== 7. 库存流水不叠加：流水是同一批出库凭证的账簿记录 ====================

    [Fact]
    public async Task Report_does_not_double_count_the_stock_movement_ledger()
    {
        using var db = TestDbFactory.Create();
        SeedCustomer(db, CustomerA, "甲客户");
        var order = SeedOrder(db, "SO-032-9", CustomerA, Currency.USD, 100m);
        SeedDetail(db, order.Id, ProductA, 10m);
        var stockOut = SeedStockOut(db, "CK-032-11", order.Id, DocumentStatus.Approved, (ProductA, 4m));
        SeedMovement(db, stockOut, ProductA, 4m);      // ERP-025：审核出库已写库存流水（基础单位）

        var report = await SalesOrderShipmentFinanceReport.ForQueryAsync(db, Query());

        var row = Assert.Single(Assert.Single(report.Groups).Orders);
        Assert.Equal(4m, row.ShippedQuantity!.Value);  // 仍为 4，不因流水再加 4
        Assert.Contains("流水是同一批出库凭证的账簿记录", SalesOrderProgress.ShipmentRuleText);
    }

    // ==================== 8. 销售退货不冲减已出货数量（写入边界说明） ====================

    [Fact]
    public async Task Report_does_not_offset_shipped_quantity_with_sales_returns()
    {
        using var db = TestDbFactory.Create();
        SeedCustomer(db, CustomerA, "甲客户");
        var order = SeedOrder(db, "SO-032-10", CustomerA, Currency.USD, 100m);
        SeedDetail(db, order.Id, ProductA, 10m);
        var stockOut = SeedStockOut(db, "CK-032-12", order.Id, DocumentStatus.Approved, (ProductA, 10m));
        SeedSalesReturn(db, "TH-032-1", CustomerA, stockOut.Id, ProductA, 4m);

        var view = await SalesOrderProgress.ForSalesOrderAsync(db, order.Id);

        Assert.Equal(10m, view.Shipment.ShippedQuantity);          // 退货不回冲（避免凭据不完整时低估出货）
        Assert.Equal(0m, view.Shipment.OutstandingQuantity);
        Assert.Equal(SalesOrderProgress.ShipmentComplete, view.Shipment.ShipmentStatus);
        Assert.Contains("销售退货与库存调整不冲减已出货数量", view.ShipmentRule);
    }

    // ==================== 9. 收款链接完整：金额与 ERP-028 财务核对一致 ====================

    [Fact]
    public async Task Report_reuses_the_reconciliation_rules_for_linked_finance_amounts()
    {
        using var db = TestDbFactory.Create();
        SeedCustomer(db, CustomerA, "甲客户");
        var order = SeedOrder(db, "SO-032-11", CustomerA, Currency.USD, 1000m);
        SeedDetail(db, order.Id, ProductA, 10m);
        SeedDepositApply(db, "DJ-032-1", order.Id, 100m, Currency.USD, DocumentStatus.Approved);
        SeedPaymentApply(db, "HK-032-1", order.Id, 200m, Currency.USD, DocumentStatus.Approved);

        var report = await SalesOrderShipmentFinanceReport.ForQueryAsync(db, Query());
        var row = Assert.Single(Assert.Single(report.Groups).Orders);
        Assert.Equal(SalesOrderProgress.LinkLinked, row.FinanceLinkStatus);
        Assert.Equal(300m, row.LinkedAmount!.Value);
        Assert.Equal(700m, row.UncoveredAmount!.Value);
        Assert.Equal(0m, row.SubmittedAmount!.Value);
        Assert.False(row.OverReceived);

        // 同一套既有引用规则：与 ERP-028 财务核对的金额与状态完全一致
        var reconciliation = await OrderFinanceReconciliation.ForSalesOrderAsync(db, order.Id);
        Assert.Equal(OrderFinanceReconciliation.AmountLinked, reconciliation.AmountStatus);
        Assert.Equal(reconciliation.LinkedAmount, row.LinkedAmount);
        Assert.Equal(reconciliation.SubmittedAmount, row.SubmittedAmount);
        Assert.Equal(1, report.LinkedOrderCount);

        // 记录逐条带单号 / 日期 / 状态 / 引用字段（引用字段就是既有列）
        var view = await SalesOrderProgress.ForSalesOrderAsync(db, order.Id);
        var deposit = view.Finance.Records.Single(r => r.Source == "FinanceDepositApply");
        Assert.Equal("DJ-032-1", deposit.DocumentNo);
        Assert.Equal(AsOf.AddDays(-3), deposit.DocumentDate);
        Assert.Equal("USD", deposit.Currency);
        Assert.Equal(nameof(DocumentStatus.Approved), deposit.Status);
        Assert.True(deposit.Counted);
        Assert.All(view.Finance.Records, r => Assert.Contains("SalesOrderId", r.ReferenceField));
    }

    // ==================== 10. 部分可归属：他币种 / 未审核 / 非已审核仅列出 ====================

    [Fact]
    public async Task Report_marks_partial_finance_and_never_sums_unqualified_records()
    {
        using var db = TestDbFactory.Create();
        SeedCustomer(db, CustomerA, "甲客户");
        var order = SeedOrder(db, "SO-032-12", CustomerA, Currency.USD, 1000m);
        SeedDetail(db, order.Id, ProductA, 10m);
        SeedDepositApply(db, "DJ-032-2", order.Id, 100m, Currency.USD, DocumentStatus.Approved);
        SeedPaymentApply(db, "HK-032-2", order.Id, 50m, Currency.USD, DocumentStatus.Submitted);
        SeedPaymentApply(db, "HK-032-3", order.Id, 40m, Currency.CNY, DocumentStatus.Approved);   // 他币种：不汇总、不换算
        SeedDepositApply(db, "DJ-032-3", order.Id, 10m, Currency.USD, DocumentStatus.Rejected);   // 非已审核：仅列出

        var report = await SalesOrderShipmentFinanceReport.ForQueryAsync(db, Query());
        var row = Assert.Single(Assert.Single(report.Groups).Orders);
        Assert.Equal(SalesOrderProgress.LinkPartial, row.FinanceLinkStatus);
        Assert.Equal(100m, row.LinkedAmount!.Value);           // 绝不是 100 + 50 + 40 + 10
        Assert.Equal(50m, row.SubmittedAmount!.Value);         // 未审核单列
        Assert.Equal(900m, row.UncoveredAmount!.Value);
        Assert.Equal(1, row.OtherCurrencyRecordCount);
        Assert.Equal(1, row.UnapprovedRecordCount);
        Assert.Contains("不汇总、不做汇率换算", row.FinanceLinkReason);

        // 已关联金额与 ERP-028 一致（同一套计入规则：权威引用 + 已审核 + 币种一致）
        var reconciliation = await OrderFinanceReconciliation.ForSalesOrderAsync(db, order.Id);
        Assert.Equal(reconciliation.LinkedAmount, row.LinkedAmount);
        Assert.Equal(reconciliation.SubmittedAmount, row.SubmittedAmount);
        Assert.Equal(1, report.PartialOrderCount);

        // 逐单记录：只有「已审核 + 币种一致」的那条计入，他币种记录显式标注不计入
        var view = await SalesOrderProgress.ForSalesOrderAsync(db, order.Id);
        var countedRecords = view.Finance.Records.Where(r => r.Counted).ToList();
        Assert.Equal(100m, countedRecords.Sum(r => r.Amount));
        Assert.Single(countedRecords);
        var otherCurrencyRecord = view.Finance.Records.Single(r => r.Currency == "CNY");
        Assert.False(otherCurrencyRecord.Counted);
        Assert.Contains("币种", otherCurrencyRecord.Note);
    }

    // ==================== 11. 无可用引用：金额未知（不是 0），客户级记录单列 ====================

    [Fact]
    public async Task Report_keeps_unlinked_finance_unknown_and_separates_customer_level_money()
    {
        using var db = TestDbFactory.Create();
        SeedCustomer(db, CustomerA, "甲客户");
        var order = SeedOrder(db, "SO-032-13", CustomerA, Currency.USD, 1000m);
        SeedDetail(db, order.Id, ProductA, 10m);
        SeedReceipt(db, "SK-032-1", CustomerA, 500m, Currency.USD, DocumentStatus.Approved);
        SeedContainerSettlement(db, "ZJ-032-1", CustomerA, 120m);
        SeedBulkSettlement(db, "SJ-032-1", CustomerA, 80m);

        var report = await SalesOrderShipmentFinanceReport.ForQueryAsync(db, Query());
        var row = Assert.Single(Assert.Single(report.Groups).Orders);
        Assert.Equal(SalesOrderProgress.LinkUnlinked, row.FinanceLinkStatus);
        Assert.Null(row.LinkedAmount);                          // 未知，绝不回落为 0
        Assert.Null(row.UncoveredAmount);
        Assert.Null(row.SubmittedAmount);
        Assert.Equal(3, row.UnattributedRecordCount);           // 收款单 + 装柜结算 + 散货结算：仅列出、不计入
        Assert.Contains("未知（null）", row.FinanceLinkReason);
        Assert.Contains("客户级记录", row.Note);
        Assert.Equal(1, report.UnlinkedOrderCount);
        Assert.Null(Assert.Single(report.Currencies).LinkedAmount);
        Assert.True(SalesOrderShipmentFinanceSemantics.IsUnlinked(row.FinanceLinkStatus));

        // 逐单视图：客户级记录只记录客户、无订单级引用，一律不计入
        var view = await SalesOrderProgress.ForSalesOrderAsync(db, order.Id);
        Assert.Equal(3, view.Finance.Records.Count);
        Assert.All(view.Finance.Records, r => Assert.False(r.Counted));
        Assert.All(view.Finance.Records, r => Assert.Contains("没有订单级引用", r.Note));

        // 与 ERP-028 的口径差异（有意为之，已写入口径说明）：ERP-028 对「引用完整但空集」按 0 呈现（partial），
        // 本报表按验收口径取更保守的未知（null），避免把「本单没有收款申请」误读为「客户没有付过款」。
        var reconciliation = await OrderFinanceReconciliation.ForSalesOrderAsync(db, order.Id);
        Assert.Equal(0m, reconciliation.LinkedAmount);
        Assert.Equal(OrderFinanceReconciliation.AmountPartial, reconciliation.AmountStatus);
        Assert.Contains("层次不同", SalesOrderProgress.FinanceRuleText);
    }

    // ==================== 12. 命中批量上限：收款金额记为未知（不静默截断） ====================

    [Fact]
    public async Task Report_marks_finance_unknown_when_the_authoritative_derivation_hits_its_cap()
    {
        using var db = TestDbFactory.Create();
        SeedCustomer(db, CustomerA, "甲客户");
        var order = SeedOrder(db, "SO-032-14", CustomerA, Currency.USD, 1000m);
        SeedDetail(db, order.Id, ProductA, 10m);

        var applies = new List<FinancePaymentApply>();
        for (var i = 0; i < SalesOrderProgress.SingleOrderDocumentLimit; i++)
        {
            applies.Add(new FinancePaymentApply
            {
                ApplyNo = $"HK-CAP-{i}", ApplyDate = AsOf.AddDays(-3), SalesOrderId = order.Id,
                CustomerId = CustomerA, Amount = 1m, Currency = Currency.USD, Status = DocumentStatus.Approved,
            });
        }

        db.FinancePaymentApplies.AddRange(applies);
        await db.SaveChangesAsync();

        var capped = await SalesOrderShipmentFinanceReport.ForQueryAsync(db, Query());
        var cappedRow = Assert.Single(Assert.Single(capped.Groups).Orders);
        Assert.Equal(SalesOrderProgress.LinkUnknown, cappedRow.FinanceLinkStatus);
        Assert.Null(cappedRow.LinkedAmount);                    // 不完整就不给数字（未知 ≠ 0）
        Assert.Null(cappedRow.UncoveredAmount);
        Assert.Contains("超过单次派生上限", cappedRow.FinanceLinkReason);
        Assert.Null(Assert.Single(capped.Groups).LinkedAmount);
        Assert.Equal(1, capped.UnknownFinanceOrderCount);

        // 少一张（199 < 上限 200）即恢复正常口径
        db.FinancePaymentApplies.Remove(applies[0]);
        await db.SaveChangesAsync();

        var restored = await SalesOrderShipmentFinanceReport.ForQueryAsync(db, Query());
        var restoredRow = Assert.Single(Assert.Single(restored.Groups).Orders);
        Assert.Equal(SalesOrderProgress.LinkLinked, restoredRow.FinanceLinkStatus);
        Assert.Equal(SalesOrderProgress.SingleOrderDocumentLimit - 1, restoredRow.LinkedAmount!.Value);
        Assert.Equal(0, restored.UnknownFinanceOrderCount);
    }

    // ==================== 13. 命中批量上限：出货数量记为未知（不静默截断） ====================

    [Fact]
    public async Task Report_marks_shipment_quantities_unknown_when_the_derivation_hits_its_cap()
    {
        using var db = TestDbFactory.Create();
        SeedCustomer(db, CustomerA, "甲客户");
        var first = SeedOrder(db, "SO-CAP-1", CustomerA, Currency.USD, 10m);
        SeedDetail(db, first.Id, ProductA, 1m);
        var second = SeedOrder(db, "SO-CAP-2", CustomerA, Currency.USD, 20m);
        SeedDetail(db, second.Id, ProductA, 1m);

        // 2000 张出库单（= 批量派生上限）分属两张订单：无法把截断归因到具体订单 → 整页数量按未知处理
        var stockOuts = new List<StockOut>();
        for (var i = 0; i < SalesOrderProgress.BatchDocumentCeiling; i++)
        {
            stockOuts.Add(new StockOut
            {
                StockOutNo = $"CK-CAP-{i}", StockOutDate = AsOf.AddDays(-5),
                SalesOrderId = i % 2 == 0 ? first.Id : second.Id,
                CustomerId = CustomerA, WarehouseId = 1, Status = DocumentStatus.Approved,
            });
        }

        db.StockOuts.AddRange(stockOuts);
        await db.SaveChangesAsync();

        var capped = await SalesOrderShipmentFinanceReport.ForQueryAsync(db, Query());
        Assert.Equal(2, capped.UnknownShipmentOrderCount);
        foreach (var item in capped.Groups.SelectMany(g => g.Orders))
        {
            Assert.Equal(SalesOrderProgress.ShipmentUnknown, item.ShipmentStatus);
            Assert.Null(item.OrderedQuantity);      // 不完整就不给出数字（未知 ≠ 0）
            Assert.Null(item.ShippedQuantity);
            Assert.Null(item.OutstandingQuantity);
            Assert.Contains("超过单次派生上限", item.Note);
        }

        Assert.Null(Assert.Single(capped.Groups).ShippedQuantity);
        Assert.Null(Assert.Single(capped.Currencies).ShippedQuantity);

        // 少一张（1999 < 上限）即恢复正常口径
        db.StockOuts.Remove(stockOuts[0]);
        await db.SaveChangesAsync();

        var restored = await SalesOrderShipmentFinanceReport.ForQueryAsync(db, Query());
        Assert.Equal(0, restored.UnknownShipmentOrderCount);
        foreach (var item in restored.Groups.SelectMany(g => g.Orders))
        {
            Assert.Equal(SalesOrderProgress.ShipmentNone, item.ShipmentStatus);
            Assert.Equal(0m, item.ShippedQuantity!.Value);      // 出库单没有明细：已出货数量按 0 派生（有依据）
            Assert.Equal(1m, item.OrderedQuantity!.Value);
        }
    }

    // ==================== 14. 筛选：客户 / 币种 / 订单日期（含首尾当天）/ 关键字 ====================

    [Fact]
    public async Task Report_filters_by_customer_currency_order_date_and_keyword()
    {
        using var db = TestDbFactory.Create();
        SeedCustomer(db, CustomerA, "甲客户");
        SeedCustomer(db, CustomerB, "乙客户");
        SeedOrder(db, "SO-F1", CustomerA, Currency.USD, 10m, new DateTime(2026, 1, 10), customerPoNo: "PO-F1");
        SeedOrder(db, "SO-F2", CustomerA, Currency.CNY, 20m, new DateTime(2026, 3, 10), contractNo: "HT-F2");
        SeedOrder(db, "SO-F3", CustomerB, Currency.USD, 30m, new DateTime(2026, 5, 10));

        var byCustomer = await SalesOrderShipmentFinanceReport.ForQueryAsync(db, Query(customerId: CustomerA));
        Assert.Equal(2, byCustomer.Total);
        Assert.Equal(CustomerA, byCustomer.CustomerId);
        Assert.All(byCustomer.Groups, g => Assert.Equal(CustomerA, g.CustomerId));

        // 币种大小写不敏感，但只登记枚举名（不做任何模糊匹配）
        var byCurrency = await SalesOrderShipmentFinanceReport.ForQueryAsync(db, Query(currency: "usd"));
        Assert.Equal(2, byCurrency.Total);
        Assert.Equal("USD", byCurrency.Currency);

        // 订单日期区间含首尾当天
        var byDate = await SalesOrderShipmentFinanceReport.ForQueryAsync(db,
            Query(from: new DateTime(2026, 1, 10), to: new DateTime(2026, 3, 10)));
        Assert.Equal(2, byDate.Total);
        Assert.Equal(new DateTime(2026, 1, 10), byDate.OrderDateFrom);
        Assert.Equal(new DateTime(2026, 3, 10), byDate.OrderDateTo);

        // 关键字只匹配既有单号列（订单号 / 合同号 / 客户 PO 号）
        var byKeyword = await SalesOrderShipmentFinanceReport.ForQueryAsync(db, Query(keyword: "HT-F2"));
        Assert.Equal(1, byKeyword.Total);
        Assert.Equal("HT-F2", byKeyword.Keyword);
        Assert.Single(byKeyword.Groups.SelectMany(g => g.Orders));

        var combined = await SalesOrderShipmentFinanceReport.ForQueryAsync(db,
            Query(customerId: CustomerA, currency: "CNY", keyword: "SO-F2"));
        Assert.Equal(1, combined.Total);
    }

    // ==================== 15. 出货状态筛选与派生结果完全一致 ====================

    [Fact]
    public async Task Report_shipment_filter_matches_the_derived_state()
    {
        using var db = TestDbFactory.Create();
        SeedCustomer(db, CustomerA, "甲客户");
        var shipped = SeedOrder(db, "SO-S1", CustomerA, Currency.USD, 10m);
        SeedDetail(db, shipped.Id, ProductA, 1m);
        SeedStockOut(db, "CK-S1", shipped.Id, DocumentStatus.Approved, (ProductA, 1m));
        var pendingOnly = SeedOrder(db, "SO-S2", CustomerA, Currency.USD, 10m);
        SeedDetail(db, pendingOnly.Id, ProductA, 1m);
        SeedStockOut(db, "CK-S2", pendingOnly.Id, DocumentStatus.Submitted, (ProductA, 1m));   // 未审核不算已出货
        var noneOrder = SeedOrder(db, "SO-S3", CustomerA, Currency.USD, 10m);
        SeedDetail(db, noneOrder.Id, ProductA, 1m);

        var none = await SalesOrderShipmentFinanceReport.ForQueryAsync(db,
            Query(shipmentStatus: SalesOrderShipmentFinanceSemantics.ShipmentFilterNone));
        Assert.Equal(2, none.Total);
        Assert.All(none.Groups.SelectMany(g => g.Orders), row => Assert.False(row.HasApprovedShipment));

        var shippedOnly = await SalesOrderShipmentFinanceReport.ForQueryAsync(db,
            Query(shipmentStatus: SalesOrderShipmentFinanceSemantics.ShipmentFilterShipped));
        Assert.Equal(1, shippedOnly.Total);
        var shippedRow = Assert.Single(shippedOnly.Groups.SelectMany(g => g.Orders));
        Assert.True(shippedRow.HasApprovedShipment);
        Assert.Equal("SO-S1", shippedRow.OrderNo);

        // 两档筛选的并集 = 全量（数据库条件 ≡ 派生规则，不存在两套口径）
        var all = await SalesOrderShipmentFinanceReport.ForQueryAsync(db, Query());
        Assert.Equal(all.Total, none.Total + shippedOnly.Total);
        Assert.Equal(1, all.ShippedOrderCount);
        Assert.Equal(2, all.UnshippedOrderCount);
    }

    // ==================== 16. 收款链接状态筛选与派生结果完全一致 ====================

    [Fact]
    public async Task Report_finance_filter_matches_the_derived_link_status()
    {
        using var db = TestDbFactory.Create();
        SeedCustomer(db, CustomerA, "甲客户");
        var linked = SeedOrder(db, "SO-L1", CustomerA, Currency.USD, 100m);
        SeedDetail(db, linked.Id, ProductA, 1m);
        SeedDepositApply(db, "DJ-L1", linked.Id, 10m, Currency.USD, DocumentStatus.Approved);
        var partial = SeedOrder(db, "SO-L2", CustomerA, Currency.USD, 100m);
        SeedDetail(db, partial.Id, ProductA, 1m);
        SeedDepositApply(db, "DJ-L2", partial.Id, 10m, Currency.USD, DocumentStatus.Submitted);
        var unlinked = SeedOrder(db, "SO-L3", CustomerA, Currency.USD, 100m);
        SeedDetail(db, unlinked.Id, ProductA, 1m);

        var byLinked = await SalesOrderShipmentFinanceReport.ForQueryAsync(db,
            Query(financeLinkStatus: SalesOrderShipmentFinanceSemantics.FinanceLinked));
        Assert.Equal(1, byLinked.Total);
        Assert.All(byLinked.Groups.SelectMany(g => g.Orders),
            row => Assert.Equal(SalesOrderProgress.LinkLinked, row.FinanceLinkStatus));
        Assert.Equal("SO-L1", byLinked.Groups.SelectMany(g => g.Orders).Single().OrderNo);

        var byPartial = await SalesOrderShipmentFinanceReport.ForQueryAsync(db,
            Query(financeLinkStatus: SalesOrderShipmentFinanceSemantics.FinancePartial));
        Assert.Equal(1, byPartial.Total);
        Assert.All(byPartial.Groups.SelectMany(g => g.Orders),
            row => Assert.Equal(SalesOrderProgress.LinkPartial, row.FinanceLinkStatus));
        Assert.Equal("SO-L2", byPartial.Groups.SelectMany(g => g.Orders).Single().OrderNo);

        var byUnlinked = await SalesOrderShipmentFinanceReport.ForQueryAsync(db,
            Query(financeLinkStatus: SalesOrderShipmentFinanceSemantics.FinanceUnlinked));
        Assert.Equal(1, byUnlinked.Total);
        Assert.All(byUnlinked.Groups.SelectMany(g => g.Orders),
            row => Assert.Equal(SalesOrderProgress.LinkUnlinked, row.FinanceLinkStatus));
        Assert.Equal("SO-L3", byUnlinked.Groups.SelectMany(g => g.Orders).Single().OrderNo);

        // 三档筛选的并集 = 全量
        var all = await SalesOrderShipmentFinanceReport.ForQueryAsync(db, Query());
        Assert.Equal(all.Total, byLinked.Total + byPartial.Total + byUnlinked.Total);
        Assert.Equal(1, all.LinkedOrderCount);
        Assert.Equal(1, all.PartialOrderCount);
        Assert.Equal(1, all.UnlinkedOrderCount);
        Assert.Equal(0, all.UnknownFinanceOrderCount);
    }

    // ==================== 17. 多客户 / 多币种：分组与币种汇总绝不跨币种合并 ====================

    [Fact]
    public async Task Report_groups_by_customer_and_currency_without_merging_currencies()
    {
        using var db = TestDbFactory.Create();
        SeedCustomer(db, CustomerA, "甲客户");
        SeedCustomer(db, CustomerB, "乙客户");
        SeedOrder(db, "SO-G1", CustomerA, Currency.USD, 100m);
        SeedOrder(db, "SO-G2", CustomerA, Currency.CNY, 200m);
        SeedOrder(db, "SO-G3", CustomerB, Currency.USD, 300m);

        var report = await SalesOrderShipmentFinanceReport.ForQueryAsync(db, Query());

        Assert.Equal(3, report.Total);
        Assert.Equal(3, report.PageOrderCount);
        Assert.Equal(3, report.Groups.Count);

        var aUsd = report.Groups.Single(g => g.CustomerId == CustomerA && g.Currency == "USD");
        Assert.Equal("甲客户", aUsd.CustomerName);
        Assert.Equal(100m, aUsd.OrderAmount);

        // 币种汇总分别成行：USD = 100 + 300（同币种跨客户），CNY = 200（不与 USD 相加）
        Assert.Equal(2, report.Currencies.Count);
        var usd = report.Currencies.Single(c => c.Currency == "USD");
        Assert.Equal(400m, usd.OrderAmount);
        Assert.Equal(2, usd.CustomerCount);
        Assert.Equal(2, usd.OrderCount);
        var cny = report.Currencies.Single(c => c.Currency == "CNY");
        Assert.Equal(200m, cny.OrderAmount);
        Assert.Equal(1, cny.CustomerCount);

        // 报表不存在任何「跨币种总额」字段：金额只能按币种分别查看
        var names = typeof(SalesOrderShipmentFinanceReportView).GetProperties().Select(p => p.Name).ToList();
        Assert.DoesNotContain(names, n => n.Contains("TotalAmount", StringComparison.Ordinal));
        Assert.DoesNotContain(names, n => n.Contains("GrandTotal", StringComparison.Ordinal));
    }

    // ==================== 18. 分页有界：稳定排序与只统计本页 ====================

    [Fact]
    public async Task Report_pages_with_stable_order_and_page_scoped_totals()
    {
        using var db = TestDbFactory.Create();
        SeedCustomer(db, CustomerA, "甲客户");
        for (var i = 1; i <= 5; i++)
        {
            var order = SeedOrder(db, $"SO-PAGE-{i}", CustomerA, Currency.USD, i * 10m, new DateTime(2026, 1, i));
            SeedDetail(db, order.Id, ProductA, i);
        }

        var first = await SalesOrderShipmentFinanceReport.ForQueryAsync(db, Query(page: 1, pageSize: 2));
        Assert.Equal(5, first.Total);
        Assert.Equal(3, first.TotalPages);
        Assert.Equal(2, first.PageOrderCount);
        Assert.Equal(30m, Assert.Single(first.Groups).OrderAmount);        // 只统计本页：10 + 20

        var second = await SalesOrderShipmentFinanceReport.ForQueryAsync(db, Query(page: 2, pageSize: 2));
        Assert.Equal(2, second.PageOrderCount);
        Assert.Equal(70m, Assert.Single(second.Groups).OrderAmount);       // 30 + 40

        var third = await SalesOrderShipmentFinanceReport.ForQueryAsync(db, Query(page: 3, pageSize: 2));
        Assert.Equal(1, third.PageOrderCount);
        Assert.Equal(50m, Assert.Single(third.Groups).OrderAmount);

        // 稳定排序：三页合起来恰好覆盖全部订单，无重复、无遗漏
        var orderNos = first.Groups.SelectMany(g => g.Orders).Select(o => o.OrderNo)
            .Concat(second.Groups.SelectMany(g => g.Orders).Select(o => o.OrderNo))
            .Concat(third.Groups.SelectMany(g => g.Orders).Select(o => o.OrderNo))
            .ToList();
        Assert.Equal(new[] { "SO-PAGE-1", "SO-PAGE-2", "SO-PAGE-3", "SO-PAGE-4", "SO-PAGE-5" }, orderNos);
    }

    // ==================== 19. 有界查询与只读：数据集访问次数与行数无关，全程不写库 ====================

    [Fact]
    public async Task Report_uses_a_bounded_number_of_dataset_reads_and_never_writes()
    {
        using var db = TestDbFactory.Create();
        SeedCustomer(db, CustomerA, "甲客户");
        var order = SeedOrder(db, "SO-BOUND-1", CustomerA, Currency.USD, 100m);
        SeedDetail(db, order.Id, ProductA, 10m);
        SeedStockOut(db, "CK-BOUND-1", order.Id, DocumentStatus.Approved, (ProductA, 4m));
        SeedDepositApply(db, "DJ-BOUND-1", order.Id, 30m, Currency.USD, DocumentStatus.Approved);
        SeedReceipt(db, "SK-BOUND-1", CustomerA, 5m, Currency.USD, DocumentStatus.Approved);

        var counting = InventoryMovementReportTests.CountingDbContext.Wrap(db);
        var single = await SalesOrderShipmentFinanceReport.ForQueryAsync(counting.Proxy, Query(pageSize: 1));
        var singleReads = counting.DatasetReads;
        Assert.Equal(1, single.Total);
        // 常数级访问：订单集合（筛选 + 计数 + 分页共用同一查询）+ 本页订单 + 客户名 + 派生 8 次
        // （订单明细 / 出库主表 / 出库明细 / 定金申请 / 货款申请 / 收款单 / 装柜结算 / 散货结算）
        Assert.Equal(11, singleReads);

        // 再补 300 张订单（跨多页）：同一报表的数据集访问次数必须保持不变（无逐行查库 / 无 N+1）
        for (var i = 2; i <= 301; i++)
        {
            var extra = SeedOrder(db, $"SO-BOUND-{i}", CustomerA, Currency.USD, i);
            SeedDetail(db, extra.Id, ProductA, 1m);
        }

        var large = await SalesOrderShipmentFinanceReport.ForQueryAsync(counting.Proxy, Query(pageSize: 200));
        var largeReads = counting.DatasetReads - singleReads;
        Assert.Equal(301, large.Total);
        Assert.Equal(SalesOrderShipmentFinanceQuery.MaxPageSize, large.PageOrderCount);   // 单页有界（上限 200）
        Assert.Equal(singleReads, largeReads);
        Assert.Equal(0, counting.WriteCalls);                                            // 只读报表：没有一次 SaveChanges
    }

    // ==================== 20. 参数归一化与校验 ====================

    [Fact]
    public async Task Query_normalizes_and_validates_parameters()
    {
        using var db = TestDbFactory.Create();
        SeedCustomer(db, CustomerA, "甲客户");
        var order = SeedOrder(db, "SO-1", CustomerA, Currency.USD, 100m);
        SeedDetail(db, order.Id, ProductA, 1m);
        SeedDepositApply(db, "DJ-N1", order.Id, 10m, Currency.USD, DocumentStatus.Approved);
        SeedStockOut(db, "CK-N1", order.Id, DocumentStatus.Approved, (ProductA, 1m));

        Assert.Throws<BusinessException>(() => Query(currency: "XXX").Normalize());
        Assert.Throws<BusinessException>(() => Query(shipmentStatus: "partial").Normalize());
        Assert.Throws<BusinessException>(() => Query(financeLinkStatus: "unknown").Normalize());
        Assert.Throws<BusinessException>(() =>
            Query(from: new DateTime(2026, 5, 1), to: new DateTime(2026, 1, 1)).Normalize());

        // 合法取值归一化后可正常使用（大小写与空白不敏感），并回显归一化后的实际取值
        var normalized = await SalesOrderShipmentFinanceReport.ForQueryAsync(db,
            Query(customerId: -1, currency: " usd ", shipmentStatus: " SHIPPED ", financeLinkStatus: "Linked",
                keyword: "  SO-1  ", page: 0, pageSize: 999));

        Assert.Null(normalized.CustomerId);                                          // ≤ 0 视为不筛选
        Assert.Equal("USD", normalized.Currency);
        Assert.Equal(SalesOrderShipmentFinanceSemantics.ShipmentFilterShipped, normalized.ShipmentStatus);
        Assert.Equal(SalesOrderShipmentFinanceSemantics.FinanceLinked, normalized.FinanceLinkStatus);
        Assert.Equal("SO-1", normalized.Keyword);
        Assert.Equal(1, normalized.Page);                                            // page < 1 归一到 1
        Assert.Equal(SalesOrderShipmentFinanceQuery.MaxPageSize, normalized.PageSize);   // 分页钳制到有界范围
        Assert.Equal(1, normalized.Total);

        // 空白筛选等于不筛选（不静默忽略非空非法值：非法值已在上方抛参数错误）
        var blank = await SalesOrderShipmentFinanceReport.ForQueryAsync(db,
            Query(currency: "  ", shipmentStatus: "", financeLinkStatus: null));
        Assert.Equal(string.Empty, blank.Currency);
        Assert.Equal(string.Empty, blank.ShipmentStatus);
        Assert.Equal(string.Empty, blank.FinanceLinkStatus);
        Assert.Equal(SalesOrderShipmentFinanceQuery.DefaultPageSize, blank.PageSize);
    }

    // ==================== 21. 与应收账款台账 / 账龄表的边界（字段、路由与文案） ====================

    [Fact]
    public void Report_exposes_no_receivable_ledger_or_aging_fields_and_states_the_boundary()
    {
        foreach (var type in new[]
                 {
                     typeof(SalesOrderShipmentFinanceOrder), typeof(SalesOrderShipmentFinanceGroup),
                     typeof(SalesOrderShipmentFinanceCurrencySummary), typeof(SalesOrderShipmentFinanceReportView),
                 })
        {
            var names = type.GetProperties().Select(p => p.Name).ToList();
            Assert.DoesNotContain(names, n => n.Contains("Invoice", StringComparison.Ordinal));
            Assert.DoesNotContain(names, n => n.Contains("DueDate", StringComparison.Ordinal));
            Assert.DoesNotContain(names, n => n.Contains("Aging", StringComparison.Ordinal));
        }

        Assert.Contains("不是应收账款台账", SalesOrderShipmentFinanceSemantics.ReceivableDisclaimerText);
        Assert.Contains("账龄", SalesOrderShipmentFinanceSemantics.ReceivableDisclaimerText);
        Assert.Contains("不得当作应收余额", SalesOrderShipmentFinanceSemantics.ReceivableDisclaimerText);
        Assert.Contains("绝不合并", SalesOrderShipmentFinanceSemantics.RuleText);
        Assert.Contains("同一套既有引用规则", SalesOrderShipmentFinanceSemantics.RuleText);
        Assert.Contains("未知", SalesOrderShipmentFinanceSemantics.RuleText);

        // 只读：报表与进度各一个 GET 路由，没有任何创建 / 修改 / 核销入口
        var getRoutes = typeof(SalesOrderController).GetMethods()
            .SelectMany(m => m.GetCustomAttributes<HttpGetAttribute>())
            .Select(a => a.Template ?? string.Empty)
            .ToList();
        Assert.Contains("shipment-finance-report", getRoutes);
        Assert.Single(getRoutes.Where(r => r == "shipment-finance-report"));
        Assert.Contains("{id:long}/progress", getRoutes);
        Assert.DoesNotContain(typeof(SalesOrderController).GetMethods()
                .SelectMany(m => m.GetCustomAttributes<HttpPostAttribute>()),
            a => (a.Template ?? string.Empty).Contains("progress", StringComparison.Ordinal)
                || (a.Template ?? string.Empty).Contains("report", StringComparison.Ordinal));
    }

    // ==================== 22. 接口端点：进度视图与报表都已接线 ====================

    [Fact]
    public async Task Controller_endpoints_return_progress_and_report_payloads()
    {
        using var db = TestDbFactory.Create();
        SeedCustomer(db, CustomerA, "甲客户");
        var order = SeedOrder(db, "SO-EP-1", CustomerA, Currency.USD, 1000m);
        SeedDetail(db, order.Id, ProductA, 10m);
        SeedStockOut(db, "CK-EP-1", order.Id, DocumentStatus.Approved, (ProductA, 4m));
        SeedDepositApply(db, "DJ-EP-1", order.Id, 250m, Currency.USD, DocumentStatus.Approved);

        var controller = new SalesOrderController(db, new DocumentNumberService(db));

        var progressResult = await controller.Progress(order.Id);
        var progressOk = Assert.IsType<OkObjectResult>(progressResult);
        var progress = Assert.IsType<ApiResponse<SalesOrderProgressView>>(progressOk.Value).Data!;
        Assert.Equal("SO-EP-1", progress.OrderNo);
        Assert.Equal(4m, progress.Shipment.ShippedQuantity);
        Assert.Equal(6m, progress.Shipment.OutstandingQuantity);
        Assert.Equal(250m, progress.Finance.LinkedAmount!.Value);
        Assert.Equal(SalesOrderProgress.ShipmentRuleText, progress.ShipmentRule);
        Assert.Equal(SalesOrderProgress.FinanceRuleText, progress.FinanceRule);

        var reportResult = await controller.ShipmentFinanceReport(Query());
        var reportOk = Assert.IsType<OkObjectResult>(reportResult);
        var report = Assert.IsType<ApiResponse<SalesOrderShipmentFinanceReportView>>(reportOk.Value).Data!;
        Assert.Equal(1, report.Total);
        Assert.Equal(250m, Assert.Single(report.Groups).LinkedAmount!.Value);
        Assert.Equal(SalesOrderShipmentFinanceSemantics.ReceivableDisclaimerText, report.ReceivableDisclaimer);

        // 不存在的订单：明确业务错误，不返回空壳数据
        await Assert.ThrowsAsync<BusinessException>(() => SalesOrderProgress.ForSalesOrderAsync(db, 987654L));
    }

    // ==================== 23. 前端接线（离线校验，不启动浏览器） ====================

    [Fact]
    public void Frontend_entry_page_and_api_are_wired_without_browser()
    {
        var js = JsDirectory();
        var progressJs = File.ReadAllText(Path.Combine(js, "sales-order-progress.js"));

        Assert.Contains("function openSalesOrderShipmentFinanceReport()", progressJs);
        Assert.Contains("/api/sales-orders/shipment-finance-report?", progressJs);
        Assert.Contains("function showSalesOrderProgress(id)", progressJs);
        Assert.Contains("/api/sales-orders/${id}/progress", progressJs);
        Assert.Contains("function sopRenderCurrencyTable(data)", progressJs);
        Assert.Contains("function sopRenderGroupTable(data)", progressJs);
        Assert.Contains("function sopRenderOrderTable(data)", progressJs);
        Assert.Contains("function exportSopCsv()", progressJs);
        Assert.Contains("SOP_SHIPMENT_LABELS", progressJs);
        Assert.Contains("SOP_FINANCE_LABELS", progressJs);
        Assert.Contains("'未知'", progressJs);                 // 未知（null）显示「未知」，绝不回落为 0
        Assert.Contains("不是", progressJs);                    // 页面显式声明不是应收账款台账 / 账龄表
        Assert.Contains("CURRENCY_NAME_OPTS", progressJs);      // 币种按枚举名筛选，不做汇率换算

        // 六项筛选控件齐备：客户 / 币种 / 订单日期（起止）/ 出货状态 / 收款链接状态 / 关键字，以及每页条数
        foreach (var id in new[]
                 {
                     "sop-customer", "sop-currency", "sop-date-from", "sop-date-to", "sop-shipment-status",
                     "sop-finance-status", "sop-keyword", "sop-pagesize",
                 })
        {
            Assert.Contains(id, progressJs);
        }

        // 工具栏入口与行操作（销售订单页）以及脚本注册
        var modulesDoc = File.ReadAllText(Path.Combine(js, "modules-doc.js"));
        Assert.Contains("openSalesOrderShipmentFinanceReport", modulesDoc);
        Assert.Contains("showSalesOrderProgress", modulesDoc);
        var index = File.ReadAllText(Path.Combine(js, "..", "index.html"));
        Assert.Contains("/js/sales-order-progress.js", index);
    }

    // ==================== 24. 报文契约：camelCase + 状态为字符串 + 未知为 null ====================

    [Fact]
    public async Task Report_serializes_with_camel_case_statuses_and_nulls_for_unknown()
    {
        using var db = TestDbFactory.Create();
        SeedCustomer(db, CustomerA, "甲客户");
        var order = SeedOrder(db, "SO-JSON-1", CustomerA, Currency.USD, 1000m);
        SeedDetail(db, order.Id, ProductA, 10m);
        SeedStockOut(db, "CK-JSON-1", order.Id, DocumentStatus.Approved, (ProductA, 4m));

        var report = await SalesOrderShipmentFinanceReport.ForQueryAsync(db, Query());
        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Contains("\"pageOrderCount\":1", json);
        Assert.Contains("\"shipmentStatus\":\"partial\"", json);
        Assert.Contains("\"financeLinkStatus\":\"unlinked\"", json);    // 无权威引用：未链接（不是 0）
        Assert.Contains("\"shippedQuantity\":4", json);
        Assert.Contains("\"uncoveredAmount\":null", json);             // 未知一律 null
        Assert.Contains("\"receivableDisclaimer\"", json);
    }
    // ==================== 助手 ====================

    private static SalesOrderShipmentFinanceQuery Query(long? customerId = null, string? currency = null,
        DateTime? from = null, DateTime? to = null, string? shipmentStatus = null, string? financeLinkStatus = null,
        string? keyword = null, int page = 1, int pageSize = 50)
        => new()
        {
            CustomerId = customerId,
            Currency = currency,
            OrderDateFrom = from,
            OrderDateTo = to,
            ShipmentStatus = shipmentStatus,
            FinanceLinkStatus = financeLinkStatus,
            Keyword = keyword,
            Page = page,
            PageSize = pageSize,
        };

    private static string JsDirectory() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "..", "..", "..", "..", "..", "src", "ERP.Api", "wwwroot", "js"));

    private static void SeedCustomer(ErpDbContext db, long id, string name)
        => db.BaseCustomers.Add(new BaseCustomer { Id = id, CustomerCode = $"C{id}", CustomerName = name });

    private static SalesOrder SeedOrder(ErpDbContext db, string orderNo, long customerId, Currency currency,
        decimal totalAmount, DateTime? orderDate = null, DocumentStatus status = DocumentStatus.Approved,
        string contractNo = "", string customerPoNo = "")
    {
        var order = new SalesOrder
        {
            OrderNo = orderNo,
            OrderDate = orderDate ?? AsOf.AddDays(-10),
            CustomerId = customerId,
            Currency = currency,
            TotalAmount = totalAmount,
            Status = status,
            ContractNo = contractNo,
            CustomerPoNo = customerPoNo,
            CreatedAt = new DateTime(2026, 9, 14, 8, 0, 0),
        };
        db.SalesOrders.Add(order);
        db.SaveChanges();
        return order;
    }

    private static void SeedDetail(ErpDbContext db, long salesOrderId, long productId, decimal quantity)
    {
        db.SalesOrderDetails.Add(new SalesOrderDetail
        {
            SalesOrderId = salesOrderId,
            ProductId = productId,
            ProductName = $"商品{productId}",
            Spec = "规格A",
            Unit = "PCS",
            Quantity = quantity,
            UnitPrice = 10m,
            Amount = quantity * 10m,
        });
        db.SaveChanges();
    }

    private static StockOut SeedStockOut(ErpDbContext db, string stockOutNo, long? salesOrderId,
        DocumentStatus status, params (long ProductId, decimal Quantity)[] lines)
    {
        var stockOut = new StockOut
        {
            StockOutNo = stockOutNo,
            StockOutDate = AsOf.AddDays(-5),
            SalesOrderId = salesOrderId,
            CustomerId = CustomerA,
            WarehouseId = 1,
            Status = status,
        };
        db.StockOuts.Add(stockOut);
        db.SaveChanges();
        foreach (var (productId, quantity) in lines)
        {
            db.StockOutDetails.Add(new StockOutDetail
            {
                StockOutId = stockOut.Id,
                ProductId = productId,
                ProductName = $"商品{productId}",
                Unit = "PCS",
                Quantity = quantity,
            });
        }

        db.SaveChanges();
        stockOut.TotalQuantity = db.StockOutDetails.Where(d => d.StockOutId == stockOut.Id).Sum(d => d.Quantity);
        db.SaveChanges();
        return stockOut;
    }

    private static void SeedMovement(ErpDbContext db, StockOut stockOut, long productId, decimal quantity)
    {
        db.StockMovements.Add(new StockMovement
        {
            MovementDate = stockOut.StockOutDate,
            MovementType = InventoryMovementType.SalesOut,
            SourceDocType = nameof(StockOut),
            SourceDocId = stockOut.Id,
            SourceDocNo = stockOut.StockOutNo,
            WarehouseId = stockOut.WarehouseId,
            ProductId = productId,
            ProductName = $"商品{productId}",
            Direction = -1,
            Quantity = quantity,
        });
        db.SaveChanges();
    }

    private static void SeedSalesReturn(ErpDbContext db, string returnNo, long customerId, long sourceStockOutId,
        long productId, decimal quantity)
    {
        var salesReturn = new SalesReturn
        {
            ReturnNo = returnNo,
            ReturnDate = AsOf.AddDays(-2),
            CustomerId = customerId,
            CustomerName = "甲客户",
            WarehouseId = 1,
            SourceStockOutId = sourceStockOutId,
            TotalQuantity = quantity,
            Status = DocumentStatus.Approved,
        };
        db.SalesReturns.Add(salesReturn);
        db.SaveChanges();
        db.SalesReturnDetails.Add(new SalesReturnDetail
        {
            SalesReturnId = salesReturn.Id,
            ReturnNo = returnNo,
            ProductId = productId,
            ProductName = $"商品{productId}",
            Unit = "PCS",
            Quantity = quantity,
        });
        db.SaveChanges();
    }

    private static void SeedDepositApply(ErpDbContext db, string applyNo, long salesOrderId, decimal amount,
        Currency currency, DocumentStatus status)
    {
        db.FinanceDepositApplies.Add(new FinanceDepositApply
        {
            ApplyNo = applyNo,
            ApplyDate = AsOf.AddDays(-3),
            SalesOrderId = salesOrderId,
            CustomerId = CustomerA,
            Amount = amount,
            Currency = currency,
            Status = status,
        });
        db.SaveChanges();
    }

    private static void SeedPaymentApply(ErpDbContext db, string applyNo, long salesOrderId, decimal amount,
        Currency currency, DocumentStatus status)
    {
        db.FinancePaymentApplies.Add(new FinancePaymentApply
        {
            ApplyNo = applyNo,
            ApplyDate = AsOf.AddDays(-3),
            SalesOrderId = salesOrderId,
            CustomerId = CustomerA,
            Amount = amount,
            Currency = currency,
            Status = status,
        });
        db.SaveChanges();
    }

    private static void SeedReceipt(ErpDbContext db, string receiptNo, long customerId, decimal amount,
        Currency currency, DocumentStatus status)
    {
        db.FinanceReceipts.Add(new FinanceReceipt
        {
            ReceiptNo = receiptNo,
            ReceiptDate = AsOf.AddDays(-1),
            CustomerId = customerId,
            Amount = amount,
            Currency = currency,
            Status = status,
        });
        db.SaveChanges();
    }

    private static void SeedContainerSettlement(ErpDbContext db, string settlementNo, long customerId,
        decimal amount)
    {
        db.FinanceContainerSettlements.Add(new FinanceContainerSettlement
        {
            SettlementNo = settlementNo,
            SettlementDate = AsOf.AddDays(-1),
            CustomerId = customerId,
            TotalAmount = amount,
            Status = DocumentStatus.Approved,
        });
        db.SaveChanges();
    }

    private static void SeedBulkSettlement(ErpDbContext db, string settlementNo, long customerId, decimal amount)
    {
        db.FinanceBulkSettlements.Add(new FinanceBulkSettlement
        {
            SettlementNo = settlementNo,
            SettlementDate = AsOf.AddDays(-1),
            CustomerId = customerId,
            TotalAmount = amount,
            Status = DocumentStatus.Approved,
        });
        db.SaveChanges();
    }
}






