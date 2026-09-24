using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Reflection;
using System.Text.Json;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-046 客户订单与收款核对报表（只读派生）单元测试：
/// 分组与币种（客户 + 币种，绝不跨币种合并）、已订 / 已出 / 未出数量复用 ERP-032 权威口径、
/// 权威收款引用（定金 / 货款申请单 SalesOrderId，已审核 + 同币种才计入）、
/// 收款单无订单级引用 → 未关联证据单独列出且绝不被匹配（即使客户 / 金额 / 日期相同）、
/// 未知一律 null（不当作 0 / 已收 / 未收 / 逾期）、已取消与软删除记录默认不并入有效合计（历史需显式状态筛选）、
/// 筛选（客户 / 币种 / 订单日期 / 出货状态 / 收款链接状态 / 收款证据状态 / 订单状态 / 关键字）与派生结果一致、
/// 分页稳定、固定数据集访问次数（无 N+1、只读不写库）、参数校验、非应收账款台账的字段与文案边界、接口与前端接线。
/// <para>说明：全部使用内存数据库，不连接 SQL Server、不启动 API、不执行任何 SQL 或 seed，不运行浏览器验收。</para>
/// </summary>
public class SalesOrderReceiptReconciliationTests
{
    private static readonly DateTime AsOf = new(2026, 9, 25);
    private const long CustomerA = 946001L;
    private const long CustomerB = 946002L;
    private const long ProductA = 946101L;
    private const long ProductB = 946102L;

    // ==================== 1. 分组：客户 + 币种，绝不跨币种合并 ====================

    [Fact]
    public async Task Report_groups_orders_by_customer_and_currency_without_merging_currencies()
    {
        using var db = TestDbFactory.Create();
        SeedCustomer(db, CustomerA, "甲客户");
        SeedCustomer(db, CustomerB, "乙客户");
        SeedOrder(db, "SO-R1", CustomerA, Currency.USD, 100m);
        SeedOrder(db, "SO-R2", CustomerA, Currency.CNY, 200m);
        SeedOrder(db, "SO-R3", CustomerB, Currency.USD, 300m);

        var report = await SalesOrderReceiptReconciliation.ForQueryAsync(db, Query());

        Assert.Equal(3, report.Total);
        Assert.Equal(3, report.PageOrderCount);
        Assert.Equal(3, report.Groups.Count);

        var aUsd = report.Groups.Single(g => g.CustomerId == CustomerA && g.Currency == "USD");
        Assert.Equal("甲客户", aUsd.CustomerName);
        Assert.Equal(100m, aUsd.OrderAmount);
        Assert.Single(aUsd.Orders);

        var aCny = report.Groups.Single(g => g.CustomerId == CustomerA && g.Currency == "CNY");
        Assert.Equal(200m, aCny.OrderAmount);

        // 币种汇总分别成行：USD = 100 + 300（同币种跨客户），CNY = 200（不与 USD 相加）
        Assert.Equal(2, report.Currencies.Count);
        var usd = report.Currencies.Single(c => c.Currency == "USD");
        Assert.Equal(400m, usd.OrderAmount);
        Assert.Equal(2, usd.CustomerCount);
        var cny = report.Currencies.Single(c => c.Currency == "CNY");
        Assert.Equal(200m, cny.OrderAmount);
        Assert.Equal(1, cny.CustomerCount);

        // 未关联收款证据的币种汇总与订单侧汇总相互独立：没有收款单时为空表（不会与订单金额相加）
        Assert.Empty(report.UnlinkedReceipts);
        Assert.Empty(report.UnlinkedReceiptCurrencies);

        // 报表不存在任何「跨币种总额」字段：金额只能按币种分别查看
        var names = typeof(SalesOrderReceiptReconciliationReport).GetProperties().Select(p => p.Name).ToList();
        Assert.DoesNotContain(names, n => n.Contains("TotalAmount", StringComparison.Ordinal));
        Assert.DoesNotContain(names, n => n.Contains("GrandTotal", StringComparison.Ordinal));
        Assert.DoesNotContain(names, n => n.Contains("CrossCurrency", StringComparison.Ordinal));
    }

    // ==================== 2. 无订单级引用的收款单：单独列出，绝不猜测匹配 ====================

    [Fact]
    public async Task Report_lists_receipts_without_an_order_reference_as_unlinked_evidence()
    {
        using var db = TestDbFactory.Create();
        SeedCustomer(db, CustomerA, "甲客户");
        var order = SeedOrder(db, "SO-R5", CustomerA, Currency.USD, 1000m, AsOf.AddDays(-1));
        SeedDetail(db, order.Id, ProductA, 10m);
        // 客户相同、金额相同、日期相同：仍然绝不自动关联到订单
        SeedReceipt(db, "SK-R1", CustomerA, 1000m, Currency.USD, DocumentStatus.Approved);

        var report = await SalesOrderReceiptReconciliation.ForQueryAsync(db, Query());
        var row = Assert.Single(Assert.Single(report.Groups).Orders);

        // 订单侧：没有任何权威收款引用 → 已关联金额与未覆盖金额未知（null，不是 0）
        Assert.Equal(SalesOrderReceiptReconciliationSemantics.CoverageUnlinked, row.ReceiptCoverageStatus);
        Assert.True(row.ReceiptCoverageKnown);
        Assert.Null(row.LinkedReceiptAmount);
        Assert.Null(row.UncoveredAmount);
        Assert.Equal(1, row.UnattributedReceiptCount);   // 收款单只到客户级：仅列出、不计入
        Assert.Contains("不是 0", row.Note);
        Assert.Contains("绝不按客户名", row.Note);

        // 收款证据侧：单独列出，链接状态恒为未关联，引用字段只到客户级
        var receipt = Assert.Single(report.UnlinkedReceipts);
        Assert.Equal("SK-R1", receipt.ReceiptNo);
        Assert.Equal(1000m, receipt.Amount);
        Assert.Equal("USD", receipt.Currency);
        Assert.Equal(SalesOrderReceiptReconciliationSemantics.ReceiptLinkageUnlinked, receipt.ReceiptLinkageStatus);
        Assert.Equal("FinanceReceipt.CustomerId", receipt.ReferenceField);
        Assert.Equal(SalesOrderReceiptReconciliationSemantics.ReceiptStatusActive, receipt.EvidenceStatus);
        Assert.Contains("绝不", receipt.ReceiptLinkageText);
        Assert.Contains("未被计入任何订单", receipt.Note);

        // 未关联证据金额只按自己的币种单列，绝不并入订单侧（两表相互独立、不得相加）
        var receiptCurrency = Assert.Single(report.UnlinkedReceiptCurrencies);
        Assert.Equal("USD", receiptCurrency.Currency);
        Assert.Equal(1, receiptCurrency.ActiveReceiptCount);
        Assert.Equal(1000m, receiptCurrency.ActiveReceiptAmount);
        Assert.Equal(0, receiptCurrency.PendingReceiptCount);
        Assert.Equal(0, receiptCurrency.HistoricalReceiptCount);
        Assert.Null(row.LinkedReceiptAmount);
        Assert.Equal(1, report.PageUnlinkedReceiptCount);
        Assert.False(report.PageUnlinkedReceiptTruncated);
    }

    // ==================== 3. 部分可归属与未知：不当作 0 / 已收 / 未收 / 逾期 ====================

    [Fact]
    public async Task Report_keeps_partial_and_unknown_receipt_coverage_null_instead_of_zero()
    {
        using var db = TestDbFactory.Create();
        SeedCustomer(db, CustomerA, "甲客户");

        // 部分可归属：他币种 + 未审核收款申请只列出、不计入
        var partialOrder = SeedOrder(db, "SO-R6", CustomerA, Currency.USD, 1000m);
        SeedDetail(db, partialOrder.Id, ProductA, 10m);
        SeedDepositApply(db, "DJ-R2", partialOrder.Id, 120m, Currency.CNY, DocumentStatus.Approved);   // 他币种
        SeedDepositApply(db, "DJ-R3", partialOrder.Id, 80m, Currency.USD, DocumentStatus.Submitted);  // 未审核
        SeedDepositApply(db, "DJ-R4", partialOrder.Id, 70m, Currency.USD, DocumentStatus.Rejected);   // 非已审核

        // 未关联：没有指向本单的收款申请
        var unlinkedOrder = SeedOrder(db, "SO-R7", CustomerA, Currency.USD, 700m);
        SeedDetail(db, unlinkedOrder.Id, ProductA, 7m);

        var report = await SalesOrderReceiptReconciliation.ForQueryAsync(db, Query());
        var rows = Assert.Single(report.Groups).Orders;

        var partial = rows.Single(r => r.OrderNo == "SO-R6");
        Assert.Equal(SalesOrderReceiptReconciliationSemantics.CoveragePartial, partial.ReceiptCoverageStatus);
        Assert.Equal(0m, partial.LinkedReceiptAmount!.Value);     // 有依据的 0（没有任何「已审核 + 同币种」记录）
        Assert.Equal(80m, partial.PendingReceiptAmount!.Value);   // 未审核只单列
        Assert.Equal(1000m, partial.UncoveredAmount!.Value);      // 不等于未收款金额
        Assert.Equal(1, partial.OtherCurrencyReceiptCount);
        Assert.Equal(1, partial.UnapprovedReceiptCount);
        Assert.Contains("不是未收款金额", partial.Note);

        var unlinked = rows.Single(r => r.OrderNo == "SO-R7");
        Assert.Equal(SalesOrderReceiptReconciliationSemantics.CoverageUnlinked, unlinked.ReceiptCoverageStatus);
        Assert.Null(unlinked.LinkedReceiptAmount);                // 未知：不是 0
        Assert.Null(unlinked.UncoveredAmount);
        Assert.Contains("不得当作未收款、逾期或已结清", unlinked.Note);

        // 本页计数：部分 / 未关联各一；未链接金额合计只统计金额已知的行（本页 = 0，来自部分可归属行）
        Assert.Equal(1, report.PartialOrderCount);
        Assert.Equal(1, report.UnlinkedOrderCount);
        Assert.Equal(0, report.LinkedOrderCount);
        Assert.Equal(0, report.UnknownCoverageOrderCount);
        var currency = Assert.Single(report.Currencies);
        Assert.Equal(0m, currency.LinkedReceiptAmount!.Value);
        Assert.Equal(1000m, currency.UncoveredAmount!.Value);     // 只汇总金额已知的行：1000

        // 报表行不暴露任何「已收 / 应收 / 逾期 / 账龄」字段
        var names = typeof(SalesOrderReceiptReconciliationOrderRow).GetProperties().Select(p => p.Name).ToList();
        Assert.DoesNotContain(names, n => n.Contains("Paid", StringComparison.Ordinal));
        Assert.DoesNotContain(names, n => n.Contains("Overdue", StringComparison.Ordinal));
    }

    // ==================== 4. 已取消 / 软删除记录：默认不并入有效合计，历史需显式筛选 ====================

    [Fact]
    public async Task Report_excludes_cancelled_and_deleted_records_from_active_totals_and_exposes_history_by_filter()
    {
        using var db = TestDbFactory.Create();
        SeedCustomer(db, CustomerA, "甲客户");
        var active = SeedOrder(db, "SO-R8", CustomerA, Currency.USD, 100m);
        SeedDetail(db, active.Id, ProductA, 1m);
        var cancelled = SeedOrder(db, "SO-R9", CustomerA, Currency.USD, 200m, status: DocumentStatus.Cancelled);
        SeedDetail(db, cancelled.Id, ProductA, 2m);
        var deleted = SeedOrder(db, "SO-R10", CustomerA, Currency.USD, 300m);
        SeedDetail(db, deleted.Id, ProductA, 3m);
        deleted.IsDeleted = true;

        SeedReceipt(db, "SK-ACTIVE", CustomerA, 100m, Currency.USD, DocumentStatus.Approved);
        SeedReceipt(db, "SK-PENDING", CustomerA, 30m, Currency.USD, DocumentStatus.Submitted);
        SeedReceipt(db, "SK-CANCELLED", CustomerA, 500m, Currency.USD, DocumentStatus.Cancelled);
        SeedReceipt(db, "SK-REJECTED", CustomerA, 50m, Currency.USD, DocumentStatus.Rejected);
        SeedReceipt(db, "SK-DELETED", CustomerA, 700m, Currency.USD, DocumentStatus.Approved);
        var deletedReceipt = db.FinanceReceipts.Local.Single(r => r.ReceiptNo == "SK-DELETED");
        deletedReceipt.IsDeleted = true;
        await db.SaveChangesAsync();

        // 默认：订单状态 = 有效（排除已取消 / 软删除），收款证据 = 有效（已审核）
        var report = await SalesOrderReceiptReconciliation.ForQueryAsync(db, Query());
        Assert.Equal(1, report.Total);
        Assert.Equal("SO-R8", Assert.Single(Assert.Single(report.Groups).Orders).OrderNo);
        Assert.Equal(0, report.CancelledOrderCount);
        Assert.Equal("active", report.OrderStatus);

        var receipt = Assert.Single(report.UnlinkedReceipts);
        Assert.Equal("SK-ACTIVE", receipt.ReceiptNo);
        var currency = Assert.Single(report.UnlinkedReceiptCurrencies);
        Assert.Equal(100m, currency.ActiveReceiptAmount);
        Assert.Equal(0m, currency.PendingReceiptAmount);
        Assert.Equal(0m, currency.HistoricalReceiptAmount);
        Assert.Equal(1, currency.ActiveReceiptCount);
        Assert.DoesNotContain(report.UnlinkedReceipts, r => r.ReceiptNo == "SK-DELETED");

        // 订单状态：仅已取消 → 历史订单可见，且单列计数（金额仅作历史参考）
        var cancelledOnly = await SalesOrderReceiptReconciliation.ForQueryAsync(db, Query(orderStatus: "cancelled"));
        Assert.Equal(1, cancelledOnly.Total);
        Assert.Equal("SO-R9", Assert.Single(Assert.Single(cancelledOnly.Groups).Orders).OrderNo);
        Assert.Equal(1, cancelledOnly.CancelledOrderCount);
        Assert.Contains("已取消", Assert.Single(cancelledOnly.Groups).Note);
        Assert.Equal(SalesOrderReceiptReconciliationSemantics.OrderStatusText("cancelled"), cancelledOnly.OrderStatusText);

        // 订单状态：全部未删除 → 有效 + 已取消（软删除订单仍在排除之外）
        var allOrders = await SalesOrderReceiptReconciliation.ForQueryAsync(db, Query(orderStatus: "all"));
        Assert.Equal(2, allOrders.Total);
        Assert.Equal(1, allOrders.CancelledOrderCount);
        Assert.DoesNotContain(allOrders.Groups.SelectMany(g => g.Orders), o => o.OrderNo == "SO-R10");

        // 收款证据：未审核（仅列出、不计入有效合计）
        var pending = await SalesOrderReceiptReconciliation.ForQueryAsync(db, Query(receiptStatus: "pending"));
        var pendingReceipt = Assert.Single(pending.UnlinkedReceipts);
        Assert.Equal("SK-PENDING", pendingReceipt.ReceiptNo);
        Assert.Equal(SalesOrderReceiptReconciliationSemantics.ReceiptStatusPending, pendingReceipt.EvidenceStatus);
        Assert.Contains("不计入有效合计", pendingReceipt.EvidenceText);
        var pendingCurrency = Assert.Single(pending.UnlinkedReceiptCurrencies);
        Assert.Equal(0m, pendingCurrency.ActiveReceiptAmount);      // 有效合计里没有这一笔
        Assert.Equal(30m, pendingCurrency.PendingReceiptAmount);

        // 收款证据：历史（已取消 / 已驳回仅在显式筛选时可见，金额绝不并入有效合计）
        var historical = await SalesOrderReceiptReconciliation.ForQueryAsync(db, Query(receiptStatus: "historical"));
        Assert.Equal(2, historical.UnlinkedReceipts.Count);
        var historicalCurrency = Assert.Single(historical.UnlinkedReceiptCurrencies);
        Assert.Equal(550m, historicalCurrency.HistoricalReceiptAmount);
        Assert.Equal(0m, historicalCurrency.ActiveReceiptAmount);
        Assert.All(historical.UnlinkedReceipts, r =>
            Assert.Equal(SalesOrderReceiptReconciliationSemantics.ReceiptStatusHistorical, r.EvidenceStatus));

        // 全部状态：三类证据同时列出、各自单列；软删除收款单一律不出现在任何筛选下
        var all = await SalesOrderReceiptReconciliation.ForQueryAsync(db, Query(receiptStatus: "all"));
        Assert.Equal(4, all.UnlinkedReceipts.Count);
        Assert.DoesNotContain(all.UnlinkedReceipts, r => r.ReceiptNo == "SK-DELETED");
        var allCurrency = Assert.Single(all.UnlinkedReceiptCurrencies);
        Assert.Equal(100m, allCurrency.ActiveReceiptAmount);
        Assert.Equal(30m, allCurrency.PendingReceiptAmount);
        Assert.Equal(550m, allCurrency.HistoricalReceiptAmount);
        Assert.Equal(SalesOrderReceiptReconciliationSemantics.ReceiptStatusText("all"), all.ReceiptStatusText);
    }

    // ==================== 5. 未知覆盖：命中派生上限 / 未关联证据不完整时绝不静默给出数字 ====================

    [Fact]
    public async Task Report_marks_unknown_coverage_and_incomplete_receipt_evidence_instead_of_zero()
    {
        using var db = TestDbFactory.Create();
        SeedCustomer(db, CustomerA, "甲客户");
        var order = SeedOrder(db, "SO-R11", CustomerA, Currency.USD, 1000m);
        SeedDetail(db, order.Id, ProductA, 10m);

        // 单张订单页面的权威引用上限 = SalesOrderProgress.SingleOrderDocumentLimit（200）
        for (var i = 0; i < SalesOrderProgress.SingleOrderDocumentLimit; i++)
        {
            db.FinanceDepositApplies.Add(new FinanceDepositApply
            {
                ApplyNo = $"DJ-CAP-{i}",
                ApplyDate = AsOf.AddDays(-3),
                SalesOrderId = order.Id,
                CustomerId = CustomerA,
                Amount = 1m,
                Currency = Currency.USD,
                Status = DocumentStatus.Approved,
            });
        }

        // 收款（未关联证据）单次查询上限 = SalesOrderReceiptReconciliation.UnlinkedReceiptLimit
        for (var i = 0; i < SalesOrderReceiptReconciliation.UnlinkedReceiptLimit; i++)
        {
            db.FinanceReceipts.Add(new FinanceReceipt
            {
                ReceiptNo = $"SK-CAP-{i}",
                ReceiptDate = AsOf.AddDays(-1),
                CustomerId = CustomerA,
                Amount = 1m,
                Currency = Currency.USD,
                Status = DocumentStatus.Approved,
            });
        }
        await db.SaveChangesAsync();

        var report = await SalesOrderReceiptReconciliation.ForQueryAsync(db, Query());
        var row = Assert.Single(Assert.Single(report.Groups).Orders);

        // 命中权威引用上限：覆盖状态未知、金额一律 null（不静默给出不完整金额）
        Assert.Equal(SalesOrderReceiptReconciliationSemantics.CoverageUnknown, row.ReceiptCoverageStatus);
        Assert.False(row.ReceiptCoverageKnown);
        Assert.Null(row.LinkedReceiptAmount);
        Assert.Null(row.UncoveredAmount);
        Assert.Contains("未知", row.ReceiptCoverageText);
        Assert.Equal(1, report.UnknownCoverageOrderCount);
        Assert.Null(Assert.Single(report.Groups).LinkedReceiptAmount);
        Assert.Null(Assert.Single(report.Currencies).UncoveredAmount);

        // 命中收款单上限：显式标注「不完整」，张数与金额不静默截断
        Assert.True(report.PageUnlinkedReceiptTruncated);
        Assert.Equal(SalesOrderReceiptReconciliation.UnlinkedReceiptLimit, report.PageUnlinkedReceiptCount);
        var receiptCurrency = Assert.Single(report.UnlinkedReceiptCurrencies);
        Assert.True(receiptCurrency.Truncated);
        Assert.Null(receiptCurrency.ActiveReceiptAmount);
        Assert.Contains("不完整", receiptCurrency.Note);

        // 少一条（199 < 上限）即恢复正常口径：覆盖状态为已关联（199m），未覆盖 801m
        db.FinanceDepositApplies.Remove(db.FinanceDepositApplies.Local.First(a => a.ApplyNo == "DJ-CAP-0"));
        db.FinanceReceipts.Remove(db.FinanceReceipts.Local.First(r => r.ReceiptNo == "SK-CAP-0"));
        await db.SaveChangesAsync();

        var restored = await SalesOrderReceiptReconciliation.ForQueryAsync(db, Query());
        var restoredRow = Assert.Single(Assert.Single(restored.Groups).Orders);
        Assert.Equal(SalesOrderReceiptReconciliationSemantics.CoverageLinked, restoredRow.ReceiptCoverageStatus);
        Assert.Equal(199m, restoredRow.LinkedReceiptAmount!.Value);
        Assert.Equal(801m, restoredRow.UncoveredAmount!.Value);
        Assert.False(restored.PageUnlinkedReceiptTruncated);
        Assert.Equal(SalesOrderReceiptReconciliation.UnlinkedReceiptLimit - 1, restored.PageUnlinkedReceiptCount);
        Assert.Equal(SalesOrderReceiptReconciliation.UnlinkedReceiptLimit - 1,
            Assert.Single(restored.UnlinkedReceiptCurrencies).ActiveReceiptAmount);
    }

    // ==================== 6. 数量与权威收款引用：完全复用 ERP-032 派生 ====================

    [Fact]
    public async Task Report_reuses_erp032_quantities_and_authoritative_receipt_applications()
    {
        using var db = TestDbFactory.Create();
        SeedCustomer(db, CustomerA, "甲客户");
        var order = SeedOrder(db, "SO-R4", CustomerA, Currency.USD, 1000m);
        SeedDetail(db, order.Id, ProductA, 10m);
        SeedStockOut(db, "CK-R1", order.Id, DocumentStatus.Approved, (ProductA, 4m));
        SeedStockOut(db, "CK-R2", order.Id, DocumentStatus.Submitted, (ProductA, 2m));   // 未审核：只单列
        SeedDepositApply(db, "DJ-R1", order.Id, 300m, Currency.USD, DocumentStatus.Approved);
        SeedPaymentApply(db, "HK-R1", order.Id, 200m, Currency.USD, DocumentStatus.Approved);

        var report = await SalesOrderReceiptReconciliation.ForQueryAsync(db, Query());
        var row = Assert.Single(Assert.Single(report.Groups).Orders);

        Assert.Equal(10m, row.OrderedQuantity!.Value);
        Assert.Equal(4m, row.ShippedQuantity!.Value);            // 只有已审核出库单计入
        Assert.Equal(2m, row.PendingShipmentQuantity!.Value);    // 已提交只单列
        Assert.Equal(6m, row.OutstandingQuantity!.Value);
        Assert.Equal(0m, row.OverShippedQuantity!.Value);
        Assert.Equal(SalesOrderProgress.ShipmentPartial, row.ShipmentStatus);
        Assert.True(row.HasApprovedShipment);
        Assert.Equal(2, row.ShipmentDocumentCount);
        Assert.Equal(1, row.ApprovedShipmentCount);
        Assert.Equal(1000m, row.OrderAmount);

        // 收款覆盖：两条收款申请均为「权威引用 + 已审核 + 同币种」→ linked，金额 500，未覆盖 500
        Assert.Equal(SalesOrderReceiptReconciliationSemantics.CoverageLinked, row.ReceiptCoverageStatus);
        Assert.True(row.ReceiptCoverageKnown);
        Assert.Equal(500m, row.LinkedReceiptAmount!.Value);
        Assert.Equal(0m, row.PendingReceiptAmount!.Value);
        Assert.Equal(500m, row.UncoveredAmount!.Value);
        Assert.Equal(0, row.OtherCurrencyReceiptCount);
        Assert.Equal(0, row.UnapprovedReceiptCount);

        // 与「同一套派生」的逐单出货 / 收款进度口径完全一致（不引入第二套匹配算法）
        var progress = await SalesOrderProgress.ForSalesOrderAsync(db, order.Id);
        Assert.Equal(progress.Shipment.ShippedQuantity, row.ShippedQuantity);
        Assert.Equal(progress.Shipment.OutstandingQuantity, row.OutstandingQuantity);
        Assert.Equal(progress.Shipment.ShipmentStatus, row.ShipmentStatus);
        Assert.Equal(progress.Finance.LinkStatus, row.ReceiptCoverageStatus);
        Assert.Equal(progress.Finance.LinkedAmount, row.LinkedReceiptAmount);
        Assert.Equal(progress.Finance.UncoveredAmount, row.UncoveredAmount);
        Assert.Equal(progress.Finance.SubmittedAmount, row.PendingReceiptAmount);

        // 已关联金额只来自持久化的权威引用行（SalesOrderId 指向本单的定金 / 货款申请单）
        var counted = progress.Finance.Records.Where(r => r.Counted).ToList();
        Assert.Equal(2, counted.Count);
        Assert.All(counted, r => Assert.Contains("SalesOrderId", r.ReferenceField));
        Assert.Equal(500m, counted.Sum(r => r.Amount));

        var group = Assert.Single(report.Groups);
        Assert.Equal(500m, group.LinkedReceiptAmount!.Value);
        Assert.Equal(500m, group.UncoveredAmount!.Value);
        Assert.Equal(1, group.LinkedOrderCount);
        Assert.Equal(500m, Assert.Single(report.Currencies).LinkedReceiptAmount!.Value);
    }

    // ==================== 7. 筛选：客户 / 币种 / 订单日期 / 关键字 / 出货状态 / 收款链接状态 ====================

    [Fact]
    public async Task Report_filters_by_customer_currency_dates_shipment_state_and_receipt_linkage()
    {
        using var db = TestDbFactory.Create();
        SeedCustomer(db, CustomerA, "甲客户");
        SeedCustomer(db, CustomerB, "乙客户");
        // F1：已审核出库单 + 已审核同币种收款申请 → shipped + linked
        var f1 = SeedOrder(db, "SO-F1", CustomerA, Currency.USD, 10m, new DateTime(2026, 1, 10), customerPoNo: "PO-F1");
        SeedDetail(db, f1.Id, ProductA, 1m);
        SeedStockOut(db, "CK-F1", f1.Id, DocumentStatus.Approved, (ProductA, 1m));
        SeedDepositApply(db, "DJ-F1", f1.Id, 5m, Currency.USD, DocumentStatus.Approved);
        // F2：无出库单 + 他币种收款申请 → none + partial
        var f2 = SeedOrder(db, "SO-F2", CustomerA, Currency.CNY, 20m, new DateTime(2026, 3, 10), contractNo: "HT-F2");
        SeedDetail(db, f2.Id, ProductA, 2m);
        SeedDepositApply(db, "DJ-F2", f2.Id, 5m, Currency.USD, DocumentStatus.Approved);
        // F3：无出库单 + 无收款申请 → none + unlinked
        var f3 = SeedOrder(db, "SO-F3", CustomerB, Currency.USD, 30m, new DateTime(2026, 5, 10));
        SeedDetail(db, f3.Id, ProductA, 3m);

        var byCustomer = await SalesOrderReceiptReconciliation.ForQueryAsync(db, Query(customerId: CustomerA));
        Assert.Equal(2, byCustomer.Total);
        Assert.All(byCustomer.Groups, g => Assert.Equal(CustomerA, g.CustomerId));

        // 币种大小写不敏感，但只接受枚举名（不做模糊匹配）
        var byCurrency = await SalesOrderReceiptReconciliation.ForQueryAsync(db, Query(currency: "usd"));
        Assert.Equal(2, byCurrency.Total);
        Assert.Equal("USD", byCurrency.Currency);

        // 订单日期区间含首尾当天
        var byDate = await SalesOrderReceiptReconciliation.ForQueryAsync(db,
            Query(from: new DateTime(2026, 1, 10), to: new DateTime(2026, 3, 10)));
        Assert.Equal(2, byDate.Total);

        // 关键字只匹配既有单号列（订单号 / 合同号 / 客户 PO 号）
        var byKeyword = await SalesOrderReceiptReconciliation.ForQueryAsync(db, Query(keyword: "HT-F2"));
        Assert.Equal(1, byKeyword.Total);
        Assert.Equal("SO-F2", Assert.Single(byKeyword.Groups.SelectMany(g => g.Orders)).OrderNo);

        // 出货状态筛选：与派生 HasApprovedShipment 完全一致
        var unshipped = await SalesOrderReceiptReconciliation.ForQueryAsync(db, Query(shipmentStatus: "none"));
        Assert.Equal(2, unshipped.Total);
        Assert.All(unshipped.Groups.SelectMany(g => g.Orders), o => Assert.False(o.HasApprovedShipment));
        var shipped = await SalesOrderReceiptReconciliation.ForQueryAsync(db, Query(shipmentStatus: "shipped"));
        Assert.Equal(1, shipped.Total);
        Assert.Equal("SO-F1", Assert.Single(shipped.Groups.SelectMany(g => g.Orders)).OrderNo);

        // 收款链接状态筛选：与派生的覆盖状态完全一致（linked / partial / unlinked 三档并集 = 全量）
        var linked = await SalesOrderReceiptReconciliation.ForQueryAsync(db, Query(receiptLinkStatus: "linked"));
        Assert.Equal(1, linked.Total);
        Assert.Equal(SalesOrderReceiptReconciliationSemantics.CoverageLinked,
            Assert.Single(linked.Groups.SelectMany(g => g.Orders)).ReceiptCoverageStatus);
        var partial = await SalesOrderReceiptReconciliation.ForQueryAsync(db, Query(receiptLinkStatus: "partial"));
        Assert.Equal(1, partial.Total);
        Assert.Equal("SO-F2", Assert.Single(partial.Groups.SelectMany(g => g.Orders)).OrderNo);
        var unlinked = await SalesOrderReceiptReconciliation.ForQueryAsync(db, Query(receiptLinkStatus: "unlinked"));
        Assert.Equal(1, unlinked.Total);
        Assert.Equal("SO-F3", Assert.Single(unlinked.Groups.SelectMany(g => g.Orders)).OrderNo);
        Assert.Equal(unshipped.Total, partial.Total + unlinked.Total);

        var combined = await SalesOrderReceiptReconciliation.ForQueryAsync(db,
            Query(customerId: CustomerA, currency: "CNY", keyword: "SO-F2", receiptLinkStatus: "partial"));
        Assert.Equal(1, combined.Total);
        Assert.Equal("partial", combined.ReceiptLinkStatus);
        Assert.Equal("SO-F2", combined.Keyword);
    }

    // ==================== 8. 分页：稳定排序、只统计本页、翻页不重不漏 ====================

    [Fact]
    public async Task Report_pages_stably_and_scopes_totals_to_the_returned_page()
    {
        using var db = TestDbFactory.Create();
        SeedCustomer(db, CustomerA, "甲客户");
        for (var i = 1; i <= 5; i++)
        {
            var order = SeedOrder(db, $"SO-PAGE-{i}", CustomerA, Currency.USD, i * 10m, new DateTime(2026, 1, i));
            SeedDetail(db, order.Id, ProductA, i);
        }

        var first = await SalesOrderReceiptReconciliation.ForQueryAsync(db, Query(page: 1, pageSize: 2));
        Assert.Equal(5, first.Total);
        Assert.Equal(3, first.TotalPages);
        Assert.Equal(2, first.PageOrderCount);
        Assert.Equal(30m, Assert.Single(first.Groups).OrderAmount);     // 只统计本页：10 + 20

        var second = await SalesOrderReceiptReconciliation.ForQueryAsync(db, Query(page: 2, pageSize: 2));
        Assert.Equal(2, second.PageOrderCount);
        Assert.Equal(70m, Assert.Single(second.Groups).OrderAmount);    // 30 + 40

        var third = await SalesOrderReceiptReconciliation.ForQueryAsync(db, Query(page: 3, pageSize: 2));
        Assert.Equal(1, third.PageOrderCount);
        Assert.Equal(50m, Assert.Single(third.Groups).OrderAmount);

        var orderNos = first.Groups.SelectMany(g => g.Orders).Select(o => o.OrderNo)
            .Concat(second.Groups.SelectMany(g => g.Orders).Select(o => o.OrderNo))
            .Concat(third.Groups.SelectMany(g => g.Orders).Select(o => o.OrderNo))
            .ToList();
        Assert.Equal(new[] { "SO-PAGE-1", "SO-PAGE-2", "SO-PAGE-3", "SO-PAGE-4", "SO-PAGE-5" }, orderNos);
    }

    // ==================== 9. 有界查询与只读：数据集访问次数与行数无关，全程不写库 ====================

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
        var single = await SalesOrderReceiptReconciliation.ForQueryAsync(counting.Proxy, Query(pageSize: 1));
        var singleReads = counting.DatasetReads;
        Assert.Equal(1, single.Total);
        // 常数级访问：订单集合（筛选 + 计数 + 分页共用同一查询）+ 本页订单 + 客户名 + 逐单派生 8 次
        // （订单明细 / 出库主表 / 出库明细 / 定金申请 / 货款申请 / 收款单 / 装柜结算 / 散货结算）+ 未关联收款证据 1 次
        // + ERP-054 收款引用证据聚合 4 次（订单 + 持久化引用行 + 收款单 + 收款单侧有效引用合计）
        Assert.Equal(16, singleReads);

        // 再补 300 张订单（跨多页）：同一报表的数据集访问次数必须保持不变（无逐行查库 / 无 N+1）
        for (var i = 2; i <= 301; i++)
        {
            var extra = SeedOrder(db, $"SO-BOUND-{i}", CustomerA, Currency.USD, i);
            SeedDetail(db, extra.Id, ProductA, 1m);
        }

        var large = await SalesOrderReceiptReconciliation.ForQueryAsync(counting.Proxy, Query(pageSize: 200));
        var largeReads = counting.DatasetReads - singleReads;
        Assert.Equal(301, large.Total);
        Assert.Equal(SalesOrderReceiptReconciliationQuery.MaxPageSize, large.PageOrderCount);   // 单页有界（上限 200）
        Assert.Equal(singleReads, largeReads);
        Assert.Equal(0, counting.WriteCalls);                                                   // 只读报表：没有一次 SaveChanges
    }

    // ==================== 10. 参数归一化与校验（非法取值不静默忽略） ====================

    [Fact]
    public async Task Query_normalizes_and_validates_parameters()
    {
        using var db = TestDbFactory.Create();
        SeedCustomer(db, CustomerA, "甲客户");
        var order = SeedOrder(db, "SO-N1", CustomerA, Currency.USD, 100m);
        SeedDetail(db, order.Id, ProductA, 1m);
        SeedStockOut(db, "CK-N1", order.Id, DocumentStatus.Approved, (ProductA, 1m));
        SeedDepositApply(db, "DJ-N1", order.Id, 10m, Currency.USD, DocumentStatus.Approved);

        Assert.Throws<BusinessException>(() => Query(currency: "XXX").Normalize());
        Assert.Throws<BusinessException>(() => Query(shipmentStatus: "partial").Normalize());
        Assert.Throws<BusinessException>(() => Query(receiptLinkStatus: "unknown").Normalize());
        Assert.Throws<BusinessException>(() => Query(receiptStatus: "draft").Normalize());
        Assert.Throws<BusinessException>(() => Query(orderStatus: "completed").Normalize());
        Assert.Throws<BusinessException>(() => Query(keyword: new string('K', 51)).Normalize());
        Assert.Throws<BusinessException>(() =>
            Query(from: new DateTime(2026, 5, 1), to: new DateTime(2026, 1, 1)).Normalize());

        // 合法取值归一化后可正常使用（大小写与空白不敏感），并回显归一化后的实际取值
        var normalized = await SalesOrderReceiptReconciliation.ForQueryAsync(db,
            Query(customerId: -1, currency: " usd ", shipmentStatus: " SHIPPED ", receiptLinkStatus: "Linked",
                receiptStatus: " All ", orderStatus: " Active ", keyword: "  SO-N1  ", page: 0, pageSize: 999));

        Assert.Null(normalized.CustomerId);                       // ≤ 0 视为不筛选
        Assert.Equal("USD", normalized.Currency);
        Assert.Equal(SalesOrderShipmentFinanceSemantics.ShipmentFilterShipped, normalized.ShipmentStatus);
        Assert.Equal(SalesOrderReceiptReconciliationSemantics.CoverageLinked, normalized.ReceiptLinkStatus);
        Assert.Equal(SalesOrderReceiptReconciliationSemantics.ReceiptStatusAll, normalized.ReceiptStatus);
        Assert.Equal(SalesOrderReceiptReconciliationSemantics.OrderStatusActive, normalized.OrderStatus);
        Assert.Equal("SO-N1", normalized.Keyword);
        Assert.Equal(1, normalized.Page);                          // page < 1 归一到 1
        Assert.Equal(SalesOrderReceiptReconciliationQuery.MaxPageSize, normalized.PageSize);
        Assert.Equal(1, normalized.Total);

        // 空白筛选等于不筛选（非法非空值已在上方抛参数错误，绝不静默忽略）
        var blank = await SalesOrderReceiptReconciliation.ForQueryAsync(db,
            Query(currency: "  ", shipmentStatus: "", receiptLinkStatus: null, receiptStatus: null, orderStatus: null));
        Assert.Equal(string.Empty, blank.Currency);
        Assert.Equal(string.Empty, blank.ShipmentStatus);
        Assert.Equal(string.Empty, blank.ReceiptLinkStatus);
        Assert.Equal(SalesOrderReceiptReconciliationSemantics.ReceiptStatusActive, blank.ReceiptStatus);   // 默认仅有效证据
        Assert.Equal(SalesOrderReceiptReconciliationSemantics.OrderStatusActive, blank.OrderStatus);       // 默认排除已取消
        Assert.Equal(SalesOrderReceiptReconciliationQuery.DefaultPageSize, blank.PageSize);
    }

    // ==================== 11. 只读：不改写任何源记录 ====================

    [Fact]
    public async Task Report_does_not_mutate_orders_stock_outs_receipts_or_links()
    {
        using var db = TestDbFactory.Create();
        SeedCustomer(db, CustomerA, "甲客户");
        var order = SeedOrder(db, "SO-M1", CustomerA, Currency.USD, 500m);
        SeedDetail(db, order.Id, ProductA, 5m);
        var stockOut = SeedStockOut(db, "CK-M1", order.Id, DocumentStatus.Approved, (ProductA, 2m));
        SeedDepositApply(db, "DJ-M1", order.Id, 100m, Currency.USD, DocumentStatus.Approved);
        SeedReceipt(db, "SK-M1", CustomerA, 60m, Currency.USD, DocumentStatus.Approved);
        SeedReceipt(db, "SK-M2", CustomerA, 40m, Currency.USD, DocumentStatus.Cancelled);

        var orderBefore = (order.TotalAmount, order.Status, order.IsDeleted, order.OrderNo, order.DepositAmount);
        var stockOutBefore = (stockOut.StockOutNo, stockOut.Status, stockOut.IsDeleted, stockOut.TotalQuantity);
        var receiptBefore = db.FinanceReceipts.AsNoTracking()
            .ToDictionary(r => r.Id, r => (r.ReceiptNo, r.Amount, r.Status, r.IsDeleted, r.Currency));
        var applyBefore = db.FinanceDepositApplies.AsNoTracking()
            .ToDictionary(a => a.Id, a => (a.ApplyNo, a.Amount, a.Status, a.IsDeleted, a.SalesOrderId));

        await SalesOrderReceiptReconciliation.ForQueryAsync(db, Query(receiptStatus: "all"));
        await SalesOrderReceiptReconciliation.ForQueryAsync(db, Query(orderStatus: "all"));

        var orderAfter = db.SalesOrders.AsNoTracking().Single(o => o.Id == order.Id);
        var stockOutAfter = db.StockOuts.AsNoTracking().Single(s => s.Id == stockOut.Id);
        Assert.Equal(orderBefore, (orderAfter.TotalAmount, orderAfter.Status, orderAfter.IsDeleted,
            orderAfter.OrderNo, orderAfter.DepositAmount));
        Assert.Equal(stockOutBefore, (stockOutAfter.StockOutNo, stockOutAfter.Status, stockOutAfter.IsDeleted,
            stockOutAfter.TotalQuantity));
        Assert.Equal(receiptBefore, db.FinanceReceipts.AsNoTracking()
            .ToDictionary(r => r.Id, r => (r.ReceiptNo, r.Amount, r.Status, r.IsDeleted, r.Currency)));
        Assert.Equal(applyBefore, db.FinanceDepositApplies.AsNoTracking()
            .ToDictionary(a => a.Id, a => (a.ApplyNo, a.Amount, a.Status, a.IsDeleted, a.SalesOrderId)));
        Assert.Equal(1, db.SalesOrderDetails.Count(d => d.SalesOrderId == order.Id));
    }

    // ==================== 12. 空数据集：不臆造任何金额或状态 ====================

    [Fact]
    public async Task Report_returns_empty_page_for_empty_dataset_without_inventing_amounts()
    {
        using var db = TestDbFactory.Create();
        var report = await SalesOrderReceiptReconciliation.ForQueryAsync(db, Query());

        Assert.Equal(0, report.Total);
        Assert.Equal(0, report.TotalPages);
        Assert.Equal(0, report.PageOrderCount);
        Assert.Empty(report.Groups);
        Assert.Empty(report.Currencies);
        Assert.Empty(report.UnlinkedReceipts);
        Assert.Empty(report.UnlinkedReceiptCurrencies);
        Assert.Equal(0, report.PageUnlinkedReceiptCount);
        Assert.False(report.PageUnlinkedReceiptTruncated);
        Assert.Equal(0, report.LinkedOrderCount);
        Assert.Equal(0, report.UnlinkedOrderCount);
        Assert.Equal(0, report.UnknownCoverageOrderCount);
        Assert.Equal(0, report.CancelledOrderCount);
    }



    // ==================== 13. 与应收账款台账 / 对账单 / 账龄表的边界（字段、路由与文案） ====================

    [Fact]
    public void Report_exposes_no_receivable_ledger_statement_or_aging_fields_and_states_the_boundary()
    {
        foreach (var type in new[]
                 {
                     typeof(SalesOrderReceiptReconciliationOrderRow), typeof(SalesOrderReceiptReconciliationReceipt),
                     typeof(SalesOrderReceiptReconciliationGroup), typeof(SalesOrderReceiptReconciliationCurrencySummary),
                     typeof(SalesOrderReceiptReconciliationReceiptCurrencySummary),
                     typeof(SalesOrderReceiptReconciliationReport),
                 })
        {
            var names = type.GetProperties().Select(p => p.Name).ToList();
            Assert.DoesNotContain(names, n => n.Contains("Invoice", StringComparison.Ordinal));
            Assert.DoesNotContain(names, n => n.Contains("DueDate", StringComparison.Ordinal));
            Assert.DoesNotContain(names, n => n.Contains("Aging", StringComparison.Ordinal));
            Assert.DoesNotContain(names, n => n.Contains("Overdue", StringComparison.Ordinal));
            Assert.DoesNotContain(names, n => n.Contains("Receivable", StringComparison.Ordinal));
            Assert.DoesNotContain(names, n => n.Contains("Statement", StringComparison.Ordinal));
        }

        Assert.Contains("不是应收账款台账", SalesOrderReceiptReconciliationSemantics.LedgerBoundaryText);
        Assert.Contains("不是具有法律效力的客户对账单", SalesOrderReceiptReconciliationSemantics.LedgerBoundaryText);
        Assert.Contains("不是收款授权或结算结果", SalesOrderReceiptReconciliationSemantics.LedgerBoundaryText);
        Assert.Contains("不是账龄表", SalesOrderReceiptReconciliationSemantics.LedgerBoundaryText);
        Assert.Contains("不得当作应收余额", SalesOrderReceiptReconciliationSemantics.LedgerBoundaryText);
        Assert.Contains("绝不合并", SalesOrderReceiptReconciliationSemantics.RuleText);
        Assert.Contains("未关联证据", SalesOrderReceiptReconciliationSemantics.RuleText);
        Assert.Contains("未知", SalesOrderReceiptReconciliationSemantics.RuleText);
        Assert.Contains("同一套权威派生", SalesOrderReceiptReconciliationSemantics.RuleText);

        // 只读：报表只有一个 GET 路由，没有任何创建 / 修改 / 核销入口
        var getRoutes = typeof(SalesOrderController).GetMethods()
            .SelectMany(m => m.GetCustomAttributes<HttpGetAttribute>())
            .Select(a => a.Template ?? string.Empty)
            .ToList();
        Assert.Contains("receipt-reconciliation-report", getRoutes);
        Assert.Single(getRoutes.Where(r => r == "receipt-reconciliation-report"));
        Assert.DoesNotContain(typeof(SalesOrderController).GetMethods()
                .SelectMany(m => m.GetCustomAttributes<HttpPostAttribute>()),
            a => (a.Template ?? string.Empty).Contains("reconciliation", StringComparison.Ordinal));
        Assert.DoesNotContain(typeof(SalesOrderController).GetMethods()
                .SelectMany(m => m.GetCustomAttributes<HttpPutAttribute>()),
            a => (a.Template ?? string.Empty).Contains("reconciliation", StringComparison.Ordinal));
        Assert.DoesNotContain(typeof(SalesOrderController).GetMethods()
                .SelectMany(m => m.GetCustomAttributes<HttpDeleteAttribute>()),
            a => (a.Template ?? string.Empty).Contains("reconciliation", StringComparison.Ordinal));
    }

    // ==================== 14. 接口端点：报表已接线并返回完整契约 ====================

    [Fact]
    public async Task Controller_endpoint_returns_receipt_reconciliation_payload()
    {
        using var db = TestDbFactory.Create();
        SeedCustomer(db, CustomerA, "甲客户");
        var order = SeedOrder(db, "SO-EP-1", CustomerA, Currency.USD, 1000m);
        SeedDetail(db, order.Id, ProductA, 10m);
        SeedStockOut(db, "CK-EP-1", order.Id, DocumentStatus.Approved, (ProductA, 4m));
        SeedDepositApply(db, "DJ-EP-1", order.Id, 250m, Currency.USD, DocumentStatus.Approved);
        SeedReceipt(db, "SK-EP-1", CustomerA, 30m, Currency.USD, DocumentStatus.Approved);

        var controller = new SalesOrderController(db, new DocumentNumberService(db));
        var result = await controller.ReceiptReconciliationReport(Query());
        var ok = Assert.IsType<OkObjectResult>(result);
        var report = Assert.IsType<ApiResponse<SalesOrderReceiptReconciliationReport>>(ok.Value).Data!;

        Assert.Equal(1, report.Total);
        var row = Assert.Single(Assert.Single(report.Groups).Orders);
        Assert.Equal("SO-EP-1", row.OrderNo);
        Assert.Equal(250m, row.LinkedReceiptAmount!.Value);
        Assert.Equal(750m, row.UncoveredAmount!.Value);
        Assert.Equal(SalesOrderReceiptReconciliationSemantics.LedgerBoundaryText, report.LedgerBoundary);
        Assert.Equal(SalesOrderReceiptReconciliationSemantics.RuleText, report.Rule);
        Assert.Equal("SK-EP-1", Assert.Single(report.UnlinkedReceipts).ReceiptNo);
    }

    // ==================== 15. 报文契约：camelCase + 状态为字符串 + 未知为 null ====================

    [Fact]
    public async Task Report_serializes_with_camel_case_statuses_and_nulls_for_unknown()
    {
        using var db = TestDbFactory.Create();
        SeedCustomer(db, CustomerA, "甲客户");
        var order = SeedOrder(db, "SO-JSON-1", CustomerA, Currency.USD, 1000m);
        SeedDetail(db, order.Id, ProductA, 10m);
        SeedReceipt(db, "SK-JSON-1", CustomerA, 40m, Currency.USD, DocumentStatus.Approved);

        var report = await SalesOrderReceiptReconciliation.ForQueryAsync(db, Query());
        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Contains("\"orderStatus\":\"active\"", json);
        Assert.Contains("\"receiptStatus\":\"active\"", json);
        Assert.Contains("\"receiptCoverageStatus\":\"unlinked\"", json);       // 无权威引用：未关联（不是 0）
        Assert.Contains("\"linkedReceiptAmount\":null", json);                // 未知一律 null
        Assert.Contains("\"uncoveredAmount\":null", json);
        Assert.Contains("\"unlinkedReceipts\":[", json);
        Assert.Contains("\"receiptLinkageStatus\":\"unlinked\"", json);
        Assert.Contains("\"referenceField\":\"FinanceReceipt.CustomerId\"", json);
        Assert.Contains("\"activeReceiptAmount\":40", json);
        Assert.Contains("\"ledgerBoundary\"", json);
        Assert.Contains("\"scopeNote\"", json);
    }

    // ==================== 16. 前端接线（离线校验，不启动浏览器） ====================

    [Fact]
    public void Frontend_entry_page_and_api_are_wired_without_browser()
    {
        var js = JsDirectory();
        var page = File.ReadAllText(Path.Combine(js, "sales-order-receipt-reconciliation.js"));

        Assert.Contains("function openSalesOrderReceiptReconciliationReport()", page);
        Assert.Contains("/api/sales-orders/receipt-reconciliation-report?", page);
        Assert.Contains("function loadSalesOrderReceiptReconciliation(page)", page);
        Assert.Contains("function sorRenderCurrencyTable(data)", page);
        Assert.Contains("function sorRenderGroupTable(data)", page);
        Assert.Contains("function sorRenderOrderTable(data)", page);
        Assert.Contains("function sorRenderReceiptCurrencyTable(data)", page);
        Assert.Contains("function sorRenderReceiptTable(data)", page);
        Assert.Contains("function exportSorrcsv()", page);
        Assert.Contains("SORR_COVERAGE_LABELS", page);
        Assert.Contains("SORR_RECEIPT_STATUS_LABELS", page);
        Assert.Contains("SORR_EVIDENCE_LABELS", page);
        Assert.Contains("'未知'", page);                    // 未知（null）显示「未知」，绝不回落为 0
        Assert.Contains("不是", page);                       // 页面显式声明不是应收账款台账 / 对账单 / 收款授权 / 账龄表
        Assert.Contains("CURRENCY_NAME_OPTS", page);         // 币种按枚举名筛选，不做汇率换算

        // 八项筛选控件齐备：客户 / 币种 / 订单日期（起止）/ 出货状态 / 收款链接状态 / 收款证据状态 / 订单状态 / 关键字
        foreach (var id in new[]
                 {
                     "sorr-customer", "sorr-currency", "sorr-date-from", "sorr-date-to", "sorr-shipment-status",
                     "sorr-link-status", "sorr-receipt-status", "sorr-order-status", "sorr-keyword", "sorr-pagesize",
                 })
        {
            Assert.Contains(id, page);
        }

        // 工具栏入口（销售订单页）与脚本注册
        var modulesDoc = File.ReadAllText(Path.Combine(js, "modules-doc.js"));
        Assert.Contains("openSalesOrderReceiptReconciliationReport", modulesDoc);
        var index = File.ReadAllText(Path.Combine(js, "..", "index.html"));
        Assert.Contains("/js/sales-order-receipt-reconciliation.js", index);
    }


    // ==================== 17. 完整出货与部分出货并存：数量有据、金额未知不猜 ====================

    [Fact]
    public async Task Report_reports_full_and_partial_shipment_side_by_side()
    {
        using var db = TestDbFactory.Create();
        SeedCustomer(db, CustomerA, "甲客户");
        var complete = SeedOrder(db, "SO-S1", CustomerA, Currency.USD, 500m);
        SeedDetail(db, complete.Id, ProductA, 5m);
        SeedStockOut(db, "CK-S1", complete.Id, DocumentStatus.Approved, (ProductA, 5m));
        var partial = SeedOrder(db, "SO-S2", CustomerA, Currency.USD, 300m);
        SeedDetail(db, partial.Id, ProductA, 5m);
        SeedStockOut(db, "CK-S2", partial.Id, DocumentStatus.Approved, (ProductA, 2m));

        var report = await SalesOrderReceiptReconciliation.ForQueryAsync(db, Query());
        var rows = Assert.Single(report.Groups).Orders;

        var completeRow = rows.Single(r => r.OrderNo == "SO-S1");
        Assert.Equal(SalesOrderProgress.ShipmentComplete, completeRow.ShipmentStatus);
        Assert.Equal(5m, completeRow.ShippedQuantity!.Value);
        Assert.Equal(0m, completeRow.OutstandingQuantity!.Value);
        Assert.True(completeRow.HasApprovedShipment);

        var partialRow = rows.Single(r => r.OrderNo == "SO-S2");
        Assert.Equal(SalesOrderProgress.ShipmentPartial, partialRow.ShipmentStatus);
        Assert.Equal(2m, partialRow.ShippedQuantity!.Value);
        Assert.Equal(3m, partialRow.OutstandingQuantity!.Value);

        // 数量有据（出库单派生），收款证据却不存在：金额一律未知，绝不当作 0 或已收讫
        Assert.All(rows, r =>
        {
            Assert.Equal(SalesOrderReceiptReconciliationSemantics.CoverageUnlinked, r.ReceiptCoverageStatus);
            Assert.Null(r.LinkedReceiptAmount);
            Assert.Null(r.UncoveredAmount);
        });
        Assert.Equal(2, report.ShippedOrderCount);
        Assert.Equal(0, report.UnshippedOrderCount);
        Assert.Equal(2, report.UnlinkedOrderCount);

        var group = Assert.Single(report.Groups);
        Assert.Equal(10m, group.OrderedQuantity);
        Assert.Equal(7m, group.ShippedQuantity!.Value);
        Assert.Equal(3m, group.OutstandingQuantity!.Value);
        Assert.Equal(800m, group.OrderAmount);
        Assert.Null(group.LinkedReceiptAmount);      // 全部未知：不是 0
        var currency = Assert.Single(report.Currencies);
        Assert.Equal(7m, currency.ShippedQuantity!.Value);
        Assert.Null(currency.LinkedReceiptAmount);
    }

    // ==================== 助手 ====================

    private static SalesOrderReceiptReconciliationQuery Query(long? customerId = null, string? currency = null,
        DateTime? from = null, DateTime? to = null, string? shipmentStatus = null, string? receiptLinkStatus = null,
        string? receiptStatus = null, string? orderStatus = null, string? keyword = null, int page = 1,
        int pageSize = 50)
        => new()
        {
            CustomerId = customerId,
            Currency = currency,
            OrderDateFrom = from,
            OrderDateTo = to,
            ShipmentStatus = shipmentStatus,
            ReceiptLinkStatus = receiptLinkStatus,
            ReceiptStatus = receiptStatus,
            OrderStatus = orderStatus,
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
            CreatedAt = new DateTime(2026, 9, 20, 8, 0, 0),
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
        return stockOut;
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

    private static FinanceReceipt SeedReceipt(ErpDbContext db, string receiptNo, long customerId, decimal amount,
        Currency currency, DocumentStatus status)
    {
        var receipt = new FinanceReceipt
        {
            ReceiptNo = receiptNo,
            ReceiptDate = AsOf.AddDays(-1),
            CustomerId = customerId,
            Amount = amount,
            Currency = currency,
            Status = status,
        };
        db.FinanceReceipts.Add(receipt);
        db.SaveChanges();
        return receipt;
    }


}

