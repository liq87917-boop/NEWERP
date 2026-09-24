using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using System.Reflection;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-031 供应商采购敞口报表（只读派生）单元测试：
/// 多供应商 / 多币种分组（绝不跨币种合并）、链接可用 / 不唯一 / 无引用三种状态下的金额暴露、
/// 未链接敞口单列、收货进度复用同一口径、筛选（供应商 / 币种 / 订单日期 / 链接状态 / 关键字）、
/// 分页有界与固定查询次数（无 N+1、只读不写库）、参数校验、非应付账款台账的字段与文案边界、接口与前端接线。
/// <para>说明：全部使用内存数据库，不连接 SQL Server、不启动 API、不执行任何 SQL 或 seed，不运行浏览器验收。</para>
/// </summary>
public class SupplierPurchaseExposureTests
{
    private static readonly DateTime AsOf = new(2026, 9, 24);
    private const long SupplierA = 940001L;
    private const long SupplierB = 940002L;
    private const long ProductA = 940101L;

    // ==================== 1. 分组：供应商 + 币种，不同币种绝不合并 ====================

    [Fact]
    public async Task Report_groups_exposure_by_supplier_and_currency_without_merging_currencies()
    {
        using var db = TestDbFactory.Create();
        SeedSupplier(db, SupplierA, "甲供应商");
        SeedSupplier(db, SupplierB, "乙供应商");
        var firstSalesOrder = SeedSalesOrder(db, "SO-EXP-1");
        var secondSalesOrder = SeedSalesOrder(db, "SO-EXP-1B");
        SeedOrder(db, "PO-EXP-1", SupplierA, Currency.CNY, 100m, firstSalesOrder.Id, (ProductA, 1m));
        SeedOrder(db, "PO-EXP-2", SupplierA, Currency.CNY, 50m, secondSalesOrder.Id, (ProductA, 1m));
        SeedOrder(db, "PO-EXP-3", SupplierA, Currency.USD, 200m, null, (ProductA, 1m));
        SeedOrder(db, "PO-EXP-4", SupplierB, Currency.CNY, 300m, null, (ProductA, 1m));
        await db.SaveChangesAsync();

        var report = await SupplierPurchaseExposure.ForQueryAsync(db, Query());

        Assert.Equal(4, report.Total);
        Assert.Equal(4, report.PageOrderCount);
        Assert.Equal(3, report.Groups.Count);

        var aCny = report.Groups.Single(g => g.SupplierId == SupplierA && g.Currency == "CNY");
        Assert.Equal("甲供应商", aCny.SupplierName);
        Assert.Equal(2, aCny.OrderCount);
        Assert.Equal(150m, aCny.OrderedAmount);          // 同供应商 + 同币种才汇总
        Assert.Equal(2, aCny.LinkedOrderCount);

        var aUsd = report.Groups.Single(g => g.SupplierId == SupplierA && g.Currency == "USD");
        Assert.Equal(200m, aUsd.OrderedAmount);
        Assert.Equal(1, aUsd.UnlinkedOrderCount);

        var bCny = report.Groups.Single(g => g.SupplierId == SupplierB && g.Currency == "CNY");
        Assert.Equal(300m, bCny.OrderedAmount);

        // 币种汇总分别成行：CNY = 100 + 50 + 300（同币种跨供应商），USD = 200（不与 CNY 相加）
        Assert.Equal(2, report.Currencies.Count);
        var cny = report.Currencies.Single(c => c.Currency == "CNY");
        Assert.Equal(450m, cny.OrderedAmount);
        Assert.Equal(2, cny.SupplierCount);
        Assert.Equal(3, cny.OrderCount);
        var usd = report.Currencies.Single(c => c.Currency == "USD");
        Assert.Equal(200m, usd.OrderedAmount);
        Assert.Equal(1, usd.SupplierCount);

        // 报表不存在任何「跨币种总额」字段：金额只能按币种分别查看
        var names = typeof(SupplierPurchaseExposureReport).GetProperties().Select(p => p.Name).ToList();
        Assert.DoesNotContain(names, n => n.Contains("TotalAmount", StringComparison.Ordinal));
        Assert.DoesNotContain(names, n => n.Contains("GrandTotal", StringComparison.Ordinal));
    }

    // ==================== 2. 链接可用：暴露已结算 / 未结算金额 ====================

    [Fact]
    public async Task Report_exposes_settled_and_outstanding_amount_only_when_linkage_is_available()
    {
        using var db = TestDbFactory.Create();
        SeedSupplier(db, SupplierA, "甲供应商");
        var salesOrder = SeedSalesOrder(db, "SO-EXP-2");
        SeedOrder(db, "PO-EXP-5", SupplierA, Currency.CNY, 100m, salesOrder.Id, (ProductA, 1m));
        var apply = SeedPaymentApply(db, "HK-EXP-1", salesOrder.Id);
        SeedPayment(db, "FK-EXP-1", apply.Id, SupplierA, 60m, Currency.CNY, DocumentStatus.Approved);
        SeedPayment(db, "FK-EXP-2", apply.Id, SupplierA, 20m, Currency.CNY, DocumentStatus.Submitted);   // 未审核不计入
        await db.SaveChangesAsync();

        var report = await SupplierPurchaseExposure.ForQueryAsync(db, Query());

        var group = Assert.Single(report.Groups);
        Assert.Equal(SupplierPurchaseExposureSemantics.LinkLinked, group.Orders[0].LinkStatus);
        Assert.Equal(60m, group.SettledAmount);
        Assert.Equal(40m, group.OutstandingAmount);
        Assert.Equal(20m, group.SubmittedAmount);
        Assert.Equal(0m, group.UnlinkedOrderedAmount);
        Assert.Equal(1, group.LinkedOrderCount);
        Assert.Equal(100m, group.LinkedOrderedAmount);
        Assert.Equal(0, group.OverSettledOrderCount);
        Assert.Contains("未结算金额 = 订单金额 − 已结算金额", group.Orders[0].Note);
    }

    // ==================== 3. 引用不唯一 / 无可用引用：未知且与链接可用金额分离 ====================

    [Fact]
    public async Task Report_keeps_ambiguous_and_unavailable_exposure_unknown_and_separate()
    {
        using var db = TestDbFactory.Create();
        SeedSupplier(db, SupplierA, "甲供应商");
        var salesOrder = SeedSalesOrder(db, "SO-EXP-3");
        SeedOrder(db, "PO-EXP-6", SupplierA, Currency.CNY, 100m, salesOrder.Id, (ProductA, 1m));
        SeedOrder(db, "PO-EXP-7", SupplierA, Currency.CNY, 200m, salesOrder.Id, (ProductA, 1m));   // 同销售订单两张 → 不唯一
        SeedOrder(db, "PO-EXP-8", SupplierA, Currency.CNY, 50m, null, (ProductA, 1m));            // 无归属销售订单
        var apply = SeedPaymentApply(db, "HK-EXP-2", salesOrder.Id);
        SeedPayment(db, "FK-EXP-3", apply.Id, SupplierA, 999m, Currency.CNY, DocumentStatus.Approved);
        await db.SaveChangesAsync();

        var report = await SupplierPurchaseExposure.ForQueryAsync(db, Query());

        var group = Assert.Single(report.Groups);
        Assert.Equal(350m, group.OrderedAmount);
        Assert.Equal(0, group.LinkedOrderCount);
        Assert.Equal(2, group.AmbiguousOrderCount);
        Assert.Equal(1, group.UnlinkedOrderCount);
        Assert.Null(group.SettledAmount);                     // 没有链接可用的订单 → 未知，不是 0
        Assert.Null(group.OutstandingAmount);                 // 未知，也不是「全部未结算」
        Assert.Equal(350m, group.UnlinkedOrderedAmount);      // 未链接敞口单列
        Assert.Equal(0m, group.LinkedOrderedAmount);

        var ambiguous = group.Orders.Single(o => o.OrderNo == "PO-EXP-6");
        Assert.Equal(SupplierPurchaseExposureSemantics.LinkAmbiguous, ambiguous.LinkStatus);
        Assert.Null(ambiguous.SettledAmount);
        Assert.Null(ambiguous.OutstandingAmount);
        Assert.Contains("2 张采购订单", ambiguous.LinkReason);   // 复用执行进度的同一套原因文案
        Assert.Contains("不作为应付余额", ambiguous.Note);

        var unavailable = group.Orders.Single(o => o.OrderNo == "PO-EXP-8");
        Assert.Equal(SupplierPurchaseExposureSemantics.LinkUnavailable, unavailable.LinkStatus);
        Assert.Contains("未关联归属销售订单", unavailable.LinkReason);
        Assert.Null(unavailable.SettledAmount);

        // 即使存在 999 的付款单，也不被当成「按供应商汇总」的结算依据
        Assert.Equal(350m, report.Currencies.Single().UnlinkedOrderedAmount);
        Assert.Null(report.Currencies.Single().SettledAmount);
        Assert.Contains("未链接敞口", report.PayableDisclaimer);
    }

    // ==================== 4. 同一分组内部分链接：链接可用部分与未链接敞口分列 ====================

    [Fact]
    public async Task Report_splits_partially_linked_group_into_linked_and_unlinked_exposure()
    {
        using var db = TestDbFactory.Create();
        SeedSupplier(db, SupplierA, "甲供应商");
        var salesOrder = SeedSalesOrder(db, "SO-EXP-4");
        SeedOrder(db, "PO-EXP-9", SupplierA, Currency.CNY, 100m, salesOrder.Id, (ProductA, 1m));
        SeedOrder(db, "PO-EXP-10", SupplierA, Currency.CNY, 300m, null, (ProductA, 1m));
        var apply = SeedPaymentApply(db, "HK-EXP-3", salesOrder.Id);
        SeedPayment(db, "FK-EXP-4", apply.Id, SupplierA, 40m, Currency.CNY, DocumentStatus.Approved);
        await db.SaveChangesAsync();

        var report = await SupplierPurchaseExposure.ForQueryAsync(db, Query());

        var group = Assert.Single(report.Groups);
        Assert.Equal(400m, group.OrderedAmount);
        Assert.Equal(1, group.LinkedOrderCount);
        Assert.Equal(1, group.UnlinkedOrderCount);
        Assert.Equal(100m, group.LinkedOrderedAmount);
        Assert.Equal(40m, group.SettledAmount);
        Assert.Equal(60m, group.OutstandingAmount);
        Assert.Equal(300m, group.UnlinkedOrderedAmount);
        Assert.Equal(1, report.LinkedOrderCount);
        Assert.Equal(1, report.UnlinkedOrderCount);
    }

    // ==================== 5. 收货进度：复用「执行进度」的已审核入库口径 ====================

    [Fact]
    public async Task Report_exposes_receipt_progress_through_the_same_authoritative_stock_in_rules()
    {
        using var db = TestDbFactory.Create();
        SeedSupplier(db, SupplierA, "甲供应商");
        var salesOrder = SeedSalesOrder(db, "SO-EXP-5");
        var order = SeedOrder(db, "PO-EXP-11", SupplierA, Currency.CNY, 100m, salesOrder.Id, (ProductA, 10m));
        SeedStockIn(db, "RK-EXP-1", order.Id, DocumentStatus.Approved, (ProductA, 4m));
        SeedStockIn(db, "RK-EXP-2", order.Id, DocumentStatus.Submitted, (ProductA, 2m));   // 未审核：只列待审，不计入已收
        SeedStockIn(db, "RK-EXP-3", order.Id, DocumentStatus.Rejected, (ProductA, 3m));    // 驳回：不计
        await db.SaveChangesAsync();

        var report = await SupplierPurchaseExposure.ForQueryAsync(db, Query());

        var item = Assert.Single(report.Groups).Orders[0];
        Assert.Equal(10m, item.OrderedQuantity);
        Assert.Equal(4m, item.ReceivedQuantity);
        Assert.Equal(6m, item.OutstandingQuantity);
        Assert.Equal(2m, item.PendingQuantity);
        Assert.Equal(PurchaseOrderProgress.ReceiptPartial, item.ReceiptStatus);
        Assert.Equal(0, report.ReceiptUnknownCount);
    }

    // ==================== 6. 空数据集：无行、无币种汇总，不臆造金额 ====================

    [Fact]
    public async Task Report_returns_empty_page_for_empty_dataset_without_inventing_amounts()
    {
        using var db = TestDbFactory.Create();

        var report = await SupplierPurchaseExposure.ForQueryAsync(db, Query());

        Assert.Equal(0, report.Total);
        Assert.Equal(0, report.PageOrderCount);
        Assert.Equal(0, report.TotalPages);
        Assert.Empty(report.Groups);
        Assert.Empty(report.Currencies);
        Assert.Equal(0, report.LinkedOrderCount);
        Assert.Equal(0, report.AmbiguousOrderCount);
        Assert.Equal(0, report.UnlinkedOrderCount);
        Assert.Equal(SupplierPurchaseExposureSemantics.RuleText, report.Rule);
        Assert.Equal(SupplierPurchaseExposureSemantics.ScopeNoteText, report.ScopeNote);
        Assert.Equal(SupplierPurchaseExposureSemantics.PayableDisclaimerText, report.PayableDisclaimer);
    }

    // ==================== 7. 筛选：供应商 / 币种 / 订单日期（含首尾当天） ====================

    [Fact]
    public async Task Report_filters_by_supplier_currency_and_order_date()
    {
        using var db = TestDbFactory.Create();
        SeedSupplier(db, SupplierA, "甲供应商");
        SeedSupplier(db, SupplierB, "乙供应商");
        SeedOrder(db, "PO-EXP-12", SupplierA, Currency.CNY, 10m, null, (ProductA, 1m), new DateTime(2026, 1, 10));
        SeedOrder(db, "PO-EXP-13", SupplierA, Currency.USD, 20m, null, (ProductA, 1m), new DateTime(2026, 3, 10));
        SeedOrder(db, "PO-EXP-14", SupplierB, Currency.CNY, 30m, null, (ProductA, 1m), new DateTime(2026, 5, 10));
        await db.SaveChangesAsync();

        var bySupplier = await SupplierPurchaseExposure.ForQueryAsync(db, Query(supplierId: SupplierA));
        Assert.Equal(2, bySupplier.Total);
        Assert.All(bySupplier.Groups.SelectMany(g => g.Orders), o => Assert.Equal(SupplierA, o.SupplierId));
        Assert.Equal(SupplierA, bySupplier.SupplierId);

        // 币种大小写不敏感，但只登记枚举名（不做任何模糊匹配）
        var byCurrency = await SupplierPurchaseExposure.ForQueryAsync(db, Query(currency: "usd"));
        Assert.Equal(1, byCurrency.Total);
        Assert.Equal("USD", byCurrency.Currency);
        Assert.Equal("PO-EXP-13", byCurrency.Groups.Single().Orders.Single().OrderNo);

        // 订单日期区间含首尾当天
        var byDate = await SupplierPurchaseExposure.ForQueryAsync(db,
            Query(from: new DateTime(2026, 1, 10), to: new DateTime(2026, 3, 10)));
        Assert.Equal(2, byDate.Total);
        Assert.Equal(new DateTime(2026, 1, 10), byDate.OrderDateFrom);
        Assert.Equal(new DateTime(2026, 3, 10), byDate.OrderDateTo);

        var combined = await SupplierPurchaseExposure.ForQueryAsync(db,
            Query(supplierId: SupplierA, currency: "CNY"));
        Assert.Equal(1, combined.Total);
        Assert.Equal("PO-EXP-12", combined.Groups.Single().Orders.Single().OrderNo);
    }

    // ==================== 8. 链接状态筛选与派生状态完全一致（数据库条件 ≡ 派生规则） ====================

    [Fact]
    public async Task Report_link_status_filter_matches_the_derived_link_status()
    {
        using var db = TestDbFactory.Create();
        SeedSupplier(db, SupplierA, "甲供应商");
        var linkedSalesOrder = SeedSalesOrder(db, "SO-EXP-6");
        SeedOrder(db, "PO-EXP-15", SupplierA, Currency.CNY, 10m, linkedSalesOrder.Id, (ProductA, 1m));
        var sharedSalesOrder = SeedSalesOrder(db, "SO-EXP-7");
        SeedOrder(db, "PO-EXP-16", SupplierA, Currency.CNY, 20m, sharedSalesOrder.Id, (ProductA, 1m));
        SeedOrder(db, "PO-EXP-17", SupplierA, Currency.CNY, 30m, sharedSalesOrder.Id, (ProductA, 1m));
        SeedOrder(db, "PO-EXP-18", SupplierA, Currency.CNY, 40m, null, (ProductA, 1m));
        SeedOrder(db, "PO-EXP-19", SupplierA, Currency.CNY, 50m, null, (ProductA, 1m));
        await db.SaveChangesAsync();

        var all = await SupplierPurchaseExposure.ForQueryAsync(db, Query());
        var derived = all.Groups.SelectMany(g => g.Orders)
            .GroupBy(o => o.LinkStatus)
            .ToDictionary(g => g.Key, g => g.Select(o => o.OrderNo).OrderBy(x => x).ToList());

        foreach (var status in new[]
                 {
                     SupplierPurchaseExposureSemantics.LinkLinked,
                     SupplierPurchaseExposureSemantics.LinkAmbiguous,
                     SupplierPurchaseExposureSemantics.LinkUnavailable
                 })
        {
            var filtered = await SupplierPurchaseExposure.ForQueryAsync(db, Query(linkStatus: status));
            var orders = filtered.Groups.SelectMany(g => g.Orders).ToList();
            Assert.All(orders, o => Assert.Equal(status, o.LinkStatus));
            Assert.Equal(derived[status], orders.Select(o => o.OrderNo).OrderBy(x => x).ToList());
            Assert.Equal(derived[status].Count, filtered.Total);   // 筛选在数据库侧完成，total 与筛选一致
        }

        Assert.Single(derived[SupplierPurchaseExposureSemantics.LinkLinked]);
        Assert.Equal(2, derived[SupplierPurchaseExposureSemantics.LinkAmbiguous].Count);
        Assert.Equal(2, derived[SupplierPurchaseExposureSemantics.LinkUnavailable].Count);
    }

    // ==================== 9. 关键字：采购单号 / 采购合同号 / 归属销售订单号 ====================

    [Fact]
    public async Task Report_filters_by_keyword_over_order_contract_and_owning_sales_order()
    {
        using var db = TestDbFactory.Create();
        SeedSupplier(db, SupplierA, "甲供应商");
        SeedOrder(db, "PO-KW-1", SupplierA, Currency.CNY, 10m, null, (ProductA, 1m));
        var contract = SeedOrder(db, "PO-KW-2", SupplierA, Currency.CNY, 20m, null, (ProductA, 1m));
        contract.ContractNo = "HT-2026-777";
        var owning = SeedSalesOrder(db, "SO-KW-9");
        var withOwning = SeedOrder(db, "PO-KW-3", SupplierA, Currency.CNY, 30m, owning.Id, (ProductA, 1m));
        withOwning.OwningSalesOrderNo = "SO-KW-9";
        await db.SaveChangesAsync();

        var byOrderNo = await SupplierPurchaseExposure.ForQueryAsync(db, Query(keyword: "PO-KW-2"));
        Assert.Equal("PO-KW-2", byOrderNo.Groups.Single().Orders.Single().OrderNo);
        Assert.Equal("PO-KW-2", byOrderNo.Keyword);

        var byContract = await SupplierPurchaseExposure.ForQueryAsync(db, Query(keyword: "HT-2026"));
        Assert.Equal("PO-KW-2", byContract.Groups.Single().Orders.Single().OrderNo);

        var byOwningSalesOrder = await SupplierPurchaseExposure.ForQueryAsync(db, Query(keyword: "SO-KW-9"));
        Assert.Equal("PO-KW-3", byOwningSalesOrder.Groups.Single().Orders.Single().OrderNo);

        var none = await SupplierPurchaseExposure.ForQueryAsync(db, Query(keyword: "不存在的关键字"));
        Assert.Equal(0, none.Total);
        Assert.Empty(none.Groups);
    }

    // ==================== 10. 参数校验：非法取值拒绝，分页钳制到有界范围 ====================

    [Fact]
    public void Query_normalization_validates_inputs_and_caps_paging()
    {
        var invalidCurrency = Query(currency: "XYZ");
        var currencyError = Assert.Throws<BusinessException>(() => invalidCurrency.Normalize());
        Assert.Contains("币种无效", currencyError.Message);

        var invalidLinkStatus = Query(linkStatus: "settled");
        var linkError = Assert.Throws<BusinessException>(() => invalidLinkStatus.Normalize());
        Assert.Contains("链接状态无效", linkError.Message);

        var inverted = Query(from: new DateTime(2026, 5, 1), to: new DateTime(2026, 4, 1));
        var dateError = Assert.Throws<BusinessException>(() => inverted.Normalize());
        Assert.Contains("不能晚于", dateError.Message);

        var clamped = Query(supplierId: 0, keyword: "   ", page: 0, pageSize: 9999);
        clamped.Normalize();
        Assert.Null(clamped.SupplierId);
        Assert.Null(clamped.Keyword);
        Assert.Equal(1, clamped.Page);
        Assert.Equal(SupplierPurchaseExposureQuery.MaxPageSize, clamped.PageSize);

        var defaults = new SupplierPurchaseExposureQuery();
        defaults.Normalize();
        Assert.Null(defaults.SupplierId);
        Assert.Null(defaults.Currency);
        Assert.Null(defaults.LinkStatus);
        Assert.Equal(1, defaults.Page);
        Assert.Equal(SupplierPurchaseExposureQuery.DefaultPageSize, defaults.PageSize);

        // 合法币种按枚举名归一化（大小写不敏感）
        var currency = Query(currency: " cny ");
        currency.Normalize();
        Assert.Equal("CNY", currency.Currency);
    }

    // ==================== 11. 分页：稳定排序、合计只统计本页 ====================

    [Fact]
    public async Task Report_pages_orders_stably_and_scopes_totals_to_the_returned_page()
    {
        using var db = TestDbFactory.Create();
        SeedSupplier(db, SupplierA, "甲供应商");
        for (var i = 1; i <= 5; i++)
        {
            SeedOrder(db, $"PO-PAGE-{i}", SupplierA, Currency.CNY, i * 10m, null, (ProductA, 1m),
                new DateTime(2026, 1, i));
        }

        await db.SaveChangesAsync();

        var first = await SupplierPurchaseExposure.ForQueryAsync(db, Query(page: 1, pageSize: 2));
        Assert.Equal(5, first.Total);
        Assert.Equal(3, first.TotalPages);
        Assert.Equal(2, first.PageOrderCount);
        Assert.Equal(30m, first.Groups.Single().OrderedAmount);      // 只统计本页：10 + 20

        var second = await SupplierPurchaseExposure.ForQueryAsync(db, Query(page: 2, pageSize: 2));
        Assert.Equal(2, second.PageOrderCount);
        Assert.Equal(70m, second.Groups.Single().OrderedAmount);     // 30 + 40

        var third = await SupplierPurchaseExposure.ForQueryAsync(db, Query(page: 3, pageSize: 2));
        Assert.Equal(1, third.PageOrderCount);
        Assert.Equal(50m, third.Groups.Single().OrderedAmount);

        // 稳定排序：三页合起来恰好覆盖全部订单，无重复、无遗漏
        var orderNos = first.Groups.SelectMany(g => g.Orders).Select(o => o.OrderNo)
            .Concat(second.Groups.SelectMany(g => g.Orders).Select(o => o.OrderNo))
            .Concat(third.Groups.SelectMany(g => g.Orders).Select(o => o.OrderNo))
            .ToList();
        Assert.Equal(new[] { "PO-PAGE-1", "PO-PAGE-2", "PO-PAGE-3", "PO-PAGE-4", "PO-PAGE-5" }, orderNos);
    }

    // ==================== 12. 有界查询与只读：数据集访问次数与行数无关，且全程不写库 ====================

    [Fact]
    public async Task Report_uses_a_bounded_number_of_dataset_reads_and_never_writes()
    {
        using var db = TestDbFactory.Create();
        SeedSupplier(db, SupplierA, "甲供应商");
        var salesOrder = SeedSalesOrder(db, "SO-EXP-9");
        var firstOrder = SeedOrder(db, "PO-BOUND-1", SupplierA, Currency.CNY, 10m, salesOrder.Id, (ProductA, 1m));
        SeedStockIn(db, "RK-BOUND-1", firstOrder.Id, DocumentStatus.Approved, (ProductA, 1m));
        var apply = SeedPaymentApply(db, "HK-EXP-5", salesOrder.Id);
        SeedPayment(db, "FK-EXP-6", apply.Id, SupplierA, 5m, Currency.CNY, DocumentStatus.Approved);
        await db.SaveChangesAsync();

        var counting = InventoryMovementReportTests.CountingDbContext.Wrap(db);
        var single = await SupplierPurchaseExposure.ForQueryAsync(counting.Proxy, Query(pageSize: 1));
        var singleReads = counting.DatasetReads;
        Assert.Equal(1, single.Total);
        // 常数级访问：订单筛选 + 本页订单（含明细）+ 供应商名 + 收货 2 次 + 结算 3 次
        Assert.Equal(8, singleReads);

        // 再补 300 张订单（跨多页）：同一报表的数据集访问次数必须保持不变（无逐行查库 / 无 N+1）
        for (var i = 2; i <= 301; i++)
        {
            var linked = i % 2 == 0;
            SeedOrder(db, $"PO-BOUND-{i}", SupplierA, Currency.CNY, i, linked ? salesOrder.Id : null,
                (ProductA, 1m));
        }

        await db.SaveChangesAsync();

        var large = await SupplierPurchaseExposure.ForQueryAsync(counting.Proxy, Query(pageSize: 200));
        var largeReads = counting.DatasetReads - singleReads;
        Assert.Equal(301, large.Total);
        Assert.Equal(SupplierPurchaseExposureQuery.MaxPageSize, large.PageOrderCount);   // 单页有界（上限 200）
        Assert.Equal(singleReads, largeReads);
        Assert.Equal(0, counting.WriteCalls);                                           // 只读报表：没有一次 SaveChanges
    }

    // ==================== 13. 接口端点 ====================

    [Fact]
    public async Task Controller_endpoint_returns_exposure_payload()
    {
        using var db = TestDbFactory.Create();
        SeedSupplier(db, SupplierA, "甲供应商");
        var salesOrder = SeedSalesOrder(db, "SO-EXP-10");
        SeedOrder(db, "PO-EXP-20", SupplierA, Currency.CNY, 100m, salesOrder.Id, (ProductA, 1m));
        var apply = SeedPaymentApply(db, "HK-EXP-6", salesOrder.Id);
        SeedPayment(db, "FK-EXP-7", apply.Id, SupplierA, 25m, Currency.CNY, DocumentStatus.Approved);
        await db.SaveChangesAsync();

        var controller = new PurchaseOrderController(db, new DocumentNumberService(db));
        var result = await controller.SupplierExposure(Query());

        var ok = Assert.IsType<OkObjectResult>(result);
        var payload = Assert.IsType<ApiResponse<SupplierPurchaseExposureReport>>(ok.Value);
        Assert.Equal(ErrorCodes.Success, payload.Code);
        var data = payload.Data!;
        Assert.Equal(1, data.Total);
        var group = Assert.Single(data.Groups);
        Assert.Equal(25m, group.SettledAmount);
        Assert.Equal(75m, group.OutstandingAmount);
        Assert.Equal(SupplierPurchaseExposureSemantics.PayableDisclaimerText, data.PayableDisclaimer);
    }

    // ==================== 14. 命中批量上限：收货数量记为未知，绝不给出不完整数量 ====================

    [Fact]
    public async Task Report_marks_receipt_quantities_unknown_when_batch_derivation_hits_its_cap()
    {
        using var db = TestDbFactory.Create();
        SeedSupplier(db, SupplierA, "甲供应商");
        var salesOrder = SeedSalesOrder(db, "SO-EXP-11");
        var first = SeedOrder(db, "PO-CAP-1", SupplierA, Currency.CNY, 10m, salesOrder.Id, (ProductA, 1m));
        var second = SeedOrder(db, "PO-CAP-2", SupplierA, Currency.CNY, 20m, salesOrder.Id, (ProductA, 1m));

        // 2000 张入库单（= 批量派生上限）分属两张订单：无法把截断归因到具体订单 → 整页收货数量按未知处理
        var stockIns = new List<StockIn>();
        for (var i = 0; i < 2000; i++)
        {
            stockIns.Add(new StockIn
            {
                StockInNo = $"RK-CAP-{i}", StockInDate = AsOf.AddDays(-5),
                PurchaseOrderId = i % 2 == 0 ? first.Id : second.Id,
                SupplierId = SupplierA, WarehouseId = 1, Status = DocumentStatus.Approved,
            });
        }

        db.StockIns.AddRange(stockIns);
        await db.SaveChangesAsync();

        var capped = await SupplierPurchaseExposure.ForQueryAsync(db, Query());
        Assert.Equal(2, capped.ReceiptUnknownCount);
        foreach (var item in capped.Groups.SelectMany(g => g.Orders))
        {
            Assert.Equal(SupplierPurchaseExposureSemantics.ReceiptUnknown, item.ReceiptStatus);
            Assert.Null(item.ReceivedQuantity);      // 不完整就不给出数字（未知 ≠ 0）
            Assert.Null(item.OrderedQuantity);
            Assert.Null(item.OutstandingQuantity);
            Assert.Contains("超过单次派生上限", item.Note);
        }

        // 少一张（1999 < 上限）即恢复正常口径：数量按同一套规则真实派生
        db.StockIns.Remove(stockIns[0]);
        await db.SaveChangesAsync();

        var restored = await SupplierPurchaseExposure.ForQueryAsync(db, Query());
        Assert.Equal(0, restored.ReceiptUnknownCount);
        foreach (var item in restored.Groups.SelectMany(g => g.Orders))
        {
            Assert.Equal(PurchaseOrderProgress.ReceiptNone, item.ReceiptStatus);
            Assert.Equal(0m, item.ReceivedQuantity);
            Assert.Equal(1m, item.OrderedQuantity);
        }
    }

    // ==================== 15. 命中批量上限：结算金额记为未知（不静默截断） ====================

    [Fact]
    public async Task Report_marks_settlement_unknown_when_payment_derivation_hits_its_cap()
    {
        using var db = TestDbFactory.Create();
        SeedSupplier(db, SupplierA, "甲供应商");
        var salesOrder = SeedSalesOrder(db, "SO-EXP-12");
        SeedOrder(db, "PO-CAP-3", SupplierA, Currency.CNY, 10m, salesOrder.Id, (ProductA, 1m));

        var applies = new List<FinancePaymentApply>();
        for (var i = 0; i < 2000; i++)
        {
            applies.Add(new FinancePaymentApply
            {
                ApplyNo = $"HK-CAP-{i}", ApplyDate = AsOf.AddDays(-3), SalesOrderId = salesOrder.Id,
                CustomerId = 1, Amount = 1m, Status = DocumentStatus.Approved,
            });
        }

        db.FinancePaymentApplies.AddRange(applies);
        await db.SaveChangesAsync();

        var report = await SupplierPurchaseExposure.ForQueryAsync(db, Query());

        var item = report.Groups.Single().Orders.Single();
        Assert.Equal(SupplierPurchaseExposureSemantics.LinkAmbiguous, item.LinkStatus);
        Assert.Null(item.SettledAmount);
        Assert.Contains("单次查询上限", item.LinkReason);
        Assert.Null(report.Groups.Single().SettledAmount);                     // 分组金额同为未知，不是 0
        Assert.Equal(10m, report.Groups.Single().UnlinkedOrderedAmount);       // 只作未链接敞口单列
        Assert.True(SupplierPurchaseExposureSemantics.IsUnlinked(item.LinkStatus));
        Assert.False(SupplierPurchaseExposureSemantics.IsUnlinked(SupplierPurchaseExposureSemantics.LinkLinked));
    }

    // ==================== 16. 与应付账款台账 / 账龄表的边界（字段、路由与文案） ====================

    [Fact]
    public void Report_exposes_no_payable_ledger_or_aging_fields_and_states_the_boundary()
    {
        foreach (var type in new[]
                 {
                     typeof(SupplierPurchaseExposureOrder), typeof(SupplierPurchaseExposureGroup),
                     typeof(SupplierPurchaseExposureCurrencySummary), typeof(SupplierPurchaseExposureReport)
                 })
        {
            var names = type.GetProperties().Select(p => p.Name).ToList();
            Assert.DoesNotContain(names, n => n.Contains("Invoice", StringComparison.Ordinal));
            Assert.DoesNotContain(names, n => n.Contains("DueDate", StringComparison.Ordinal));
            Assert.DoesNotContain(names, n => n.Contains("Aging", StringComparison.Ordinal));
        }

        Assert.Contains("不是应付账款台账", SupplierPurchaseExposureSemantics.PayableDisclaimerText);
        Assert.Contains("账龄", SupplierPurchaseExposureSemantics.PayableDisclaimerText);
        Assert.Contains("不得当作应付余额", SupplierPurchaseExposureSemantics.PayableDisclaimerText);
        Assert.Contains("绝不合并", SupplierPurchaseExposureSemantics.RuleText);
        Assert.Contains("未链接敞口", SupplierPurchaseExposureSemantics.RuleText);
        Assert.Contains("同一套权威引用规则", SupplierPurchaseExposureSemantics.RuleText);

        // 只读报表只暴露一个 GET 路由（无创建 / 修改 / 核销入口）
        var routes = typeof(PurchaseOrderController).GetMethods()
            .SelectMany(m => m.GetCustomAttributes<HttpGetAttribute>())
            .Select(a => a.Template ?? string.Empty)
            .ToList();
        Assert.Contains("supplier-exposure", routes);
        Assert.Single(routes.Where(r => r == "supplier-exposure"));
        Assert.DoesNotContain(typeof(PurchaseOrderController).GetMethods()
                .SelectMany(m => m.GetCustomAttributes<HttpPostAttribute>()),
            a => (a.Template ?? string.Empty).Contains("exposure", StringComparison.Ordinal));
    }

    // ==================== 17. 前端接线（离线校验，不启动浏览器） ====================

    [Fact]
    public void Frontend_entry_page_and_api_are_wired_without_browser()
    {
        var js = JsDirectory();
        var reportJs = File.ReadAllText(Path.Combine(js, "supplier-purchase-exposure.js"));

        Assert.Contains("function openSupplierPurchaseExposureReport()", reportJs);
        Assert.Contains("/api/purchase-orders/supplier-exposure?", reportJs);
        Assert.Contains("function speRenderCurrencyTable(data)", reportJs);
        Assert.Contains("function speRenderGroupTable(data)", reportJs);
        Assert.Contains("function speRenderOrderTable(data)", reportJs);
        Assert.Contains("function exportSpeCsv()", reportJs);
        Assert.Contains("SPE_LINK_LABELS", reportJs);
        Assert.Contains("SPE_RECEIPT_LABELS", reportJs);
        Assert.Contains("'未知'", reportJs);                       // 未知（null）显示「未知」，绝不回落为 0
        Assert.Contains("不是", reportJs);                          // 页面显式声明不是应付账款台账 / 账龄表
        Assert.Contains("CURRENCY_NAME_OPTS", reportJs);            // 币种按枚举名筛选，不做汇率换算

        // 五项筛选控件齐备：供应商 / 币种 / 订单日期（起止）/ 链接状态 / 关键字，以及每页条数
        foreach (var id in new[]
                 {
                     "spe-supplier", "spe-currency", "spe-date-from", "spe-date-to", "spe-link-status",
                     "spe-keyword", "spe-pagesize"
                 })
        {
            Assert.Contains(id, reportJs);
        }

        // 工具栏入口（采购订单页）与脚本注册
        var modulesDoc = File.ReadAllText(Path.Combine(js, "modules-doc.js"));
        Assert.Contains("openSupplierPurchaseExposureReport", modulesDoc);
        var index = File.ReadAllText(Path.Combine(js, "..", "index.html"));
        Assert.Contains("/js/supplier-purchase-exposure.js", index);
    }

    // ==================== 助手 ====================

    private static SupplierPurchaseExposureQuery Query(long? supplierId = null, string? currency = null,
        DateTime? from = null, DateTime? to = null, string? linkStatus = null, string? keyword = null,
        int page = 1, int pageSize = 50)
        => new()
        {
            SupplierId = supplierId,
            Currency = currency,
            OrderDateFrom = from,
            OrderDateTo = to,
            LinkStatus = linkStatus,
            Keyword = keyword,
            Page = page,
            PageSize = pageSize
        };

    private static string JsDirectory() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "..", "..", "..", "..", "..", "src", "ERP.Api", "wwwroot", "js"));

    private static void SeedSupplier(ErpDbContext db, long id, string name)
        => db.BaseSuppliers.Add(new BaseSupplier { Id = id, SupplierCode = $"S{id}", SupplierName = name });

    private static SalesOrder SeedSalesOrder(ErpDbContext db, string orderNo)
    {
        var salesOrder = new SalesOrder
        {
            OrderNo = orderNo,
            OrderDate = AsOf.AddDays(-30),
            CustomerId = 1,
            Currency = Currency.CNY,
            Status = DocumentStatus.Approved,
            CreatedAt = new DateTime(2026, 8, 25, 8, 0, 0),
        };
        db.SalesOrders.Add(salesOrder);
        db.SaveChanges();
        return salesOrder;
    }

    /// <summary>写入一张采购订单（含 1 行明细；明细数量用于收货派生的冲抵口径）</summary>
    private static PurchaseOrder SeedOrder(ErpDbContext db, string orderNo, long supplierId, Currency currency,
        decimal totalAmount, long? owningSalesOrderId, (long ProductId, decimal Quantity) line,
        DateTime? orderDate = null)
    {
        var order = new PurchaseOrder
        {
            OrderNo = orderNo,
            OrderDate = orderDate ?? AsOf.AddDays(-10),
            SupplierId = supplierId,
            Currency = currency,
            TotalAmount = totalAmount,
            OwningSalesOrderId = owningSalesOrderId,
            OwningSalesOrderNo = owningSalesOrderId.HasValue ? $"SO#{owningSalesOrderId.Value}" : string.Empty,
            Status = DocumentStatus.Approved,
            CreatedAt = new DateTime(2026, 9, 14, 8, 0, 0),
        };
        db.PurchaseOrders.Add(order);
        db.SaveChanges();
        db.PurchaseOrderDetails.Add(new PurchaseOrderDetail
        {
            PurchaseOrderId = order.Id,
            ProductId = line.ProductId,
            ProductName = $"商品{line.ProductId}",
            Spec = "规格A",
            Unit = "PCS",
            Quantity = line.Quantity,
            UnitPrice = line.Quantity == 0 ? 0m : totalAmount / line.Quantity,
            Amount = totalAmount,
        });
        db.SaveChanges();
        return order;
    }

    private static StockIn SeedStockIn(ErpDbContext db, string stockInNo, long? purchaseOrderId,
        DocumentStatus status, params (long ProductId, decimal Quantity)[] lines)
    {
        var stockIn = new StockIn
        {
            StockInNo = stockInNo,
            StockInDate = AsOf.AddDays(-5),
            PurchaseOrderId = purchaseOrderId,
            SupplierId = SupplierA,
            WarehouseId = 1,
            Status = status,
        };
        db.StockIns.Add(stockIn);
        db.SaveChanges();
        foreach (var (productId, quantity) in lines)
        {
            db.StockInDetails.Add(new StockInDetail
            {
                StockInId = stockIn.Id,
                ProductId = productId,
                ProductName = $"商品{productId}",
                Unit = "PCS",
                Quantity = quantity,
            });
        }

        db.SaveChanges();
        stockIn.TotalQuantity = db.StockInDetails.Where(d => d.StockInId == stockIn.Id).Sum(d => d.Quantity);
        db.SaveChanges();
        return stockIn;
    }

    private static FinancePaymentApply SeedPaymentApply(ErpDbContext db, string applyNo, long? salesOrderId)
    {
        var apply = new FinancePaymentApply
        {
            ApplyNo = applyNo,
            ApplyDate = AsOf.AddDays(-3),
            SalesOrderId = salesOrderId,
            CustomerId = 1,
            Amount = 0m,
            Status = DocumentStatus.Approved,
        };
        db.FinancePaymentApplies.Add(apply);
        db.SaveChanges();
        return apply;
    }

    private static FinancePayment SeedPayment(ErpDbContext db, string paymentNo, long? paymentApplyId, long supplierId,
        decimal amount, Currency currency, DocumentStatus status)
    {
        var payment = new FinancePayment
        {
            PaymentNo = paymentNo,
            PaymentDate = AsOf.AddDays(-2),
            SupplierId = supplierId,
            PaymentApplyId = paymentApplyId,
            Amount = amount,
            Currency = currency,
            Status = status,
        };
        db.FinancePayments.Add(payment);
        db.SaveChanges();
        return payment;
    }
}
