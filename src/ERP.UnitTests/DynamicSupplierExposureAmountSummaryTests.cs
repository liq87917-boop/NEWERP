using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using System.Reflection;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-154 动态供应商采购敞口预览「当前授权预览页按供应商 + 原币（可选链接状态）的已知金额汇总」单元测试。
/// <para>金额汇总语义：<see cref="DynamicSupplierExposureReportRules.BuildAmountSummaries"/> 只汇总「当前授权预览页」的采购订单敞口证据：
/// 订单金额来自采购订单已落库总额（恒可确认，直接求和）；已结算 / 未结算 / 已提交付款金额只汇总「链接可用且金额已知」的订单，
/// 没有链接可用订单或任一行金额未知即整组合计为 null（绝不轧为 0）；链接不唯一（ambiguous）与无可用链接（unavailable）的
/// 订单金额保持独立，绝不并入权威已结算合计、绝不推断为应付余额；供应商 + 原币严格隔离（绝不跨币种合并或换算）。</para>
/// 覆盖：汇总模式规范化、多币种多供应商隔离、链接可用与不唯一 / 无引用分离、未知金额保持 null、链接状态拆分、
/// 空页 / 分页边界、无效汇总模式（fail closed）、无采购订单菜单授权（权限不足）、无身份（未认证）与只读不写库。
/// <para>全部使用内存数据库（TestDbFactory），不连接 SQL Server、不启动 API、不执行任何 SQL / seed。</para>
/// </summary>
public class DynamicSupplierExposureAmountSummaryTests
{
    private static readonly DateTime AsOf = new(2026, 9, 24);
    private const long SupplierA = 969001L;
    private const long SupplierB = 969002L;
    private const long ProductA = 969101L;

    // ==================== 0. 测试脚手架 ====================

    private static SysUser SeedUser(ErpDbContext db, string userName)
    {
        var user = new SysUser
        {
            UserName = userName,
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = userName,
            Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        db.SaveChanges();
        return user;
    }

    private static SysRole SeedRole(ErpDbContext db, string code, bool isSystem = false)
    {
        var role = new SysRole { RoleName = code, RoleCode = code, IsSystem = isSystem };
        db.SysRoles.Add(role);
        db.SaveChanges();
        return role;
    }

    private static void SeedUserRole(ErpDbContext db, long userId, long roleId)
    {
        db.SysUserRoles.Add(new SysUserRole { UserId = userId, RoleId = roleId });
        db.SaveChanges();
    }

    private static SysMenu SeedMenu(ErpDbContext db, string code)
    {
        var menu = new SysMenu { MenuName = code, MenuCode = code, MenuType = MenuType.Menu };
        db.SysMenus.Add(menu);
        db.SaveChanges();
        return menu;
    }

    private static void SeedRoleMenu(ErpDbContext db, long roleId, long menuId)
    {
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = roleId, MenuId = menuId });
        db.SaveChanges();
    }

    /// <summary>播种一个「系统内置角色 + 采购订单菜单授权」用户</summary>
    private static long SeedPrivilegedUser(ErpDbContext db, string userName = "priv")
    {
        var user = SeedUser(db, userName);
        var role = SeedRole(db, $"Role-{userName}", isSystem: true);
        SeedUserRole(db, user.Id, role.Id);
        SeedRoleMenu(db, role.Id, SeedMenu(db, DynamicSupplierExposureReportRules.RequiredMenuCode).Id);
        return user.Id;
    }

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

    private static DynamicSupplierExposureReportController NewController(ErpDbContext db)
        => new(db);

    private static DynamicSupplierExposureReportPageDto PreviewOk(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<ApiResponse<DynamicSupplierExposureReportPageDto>>(ok.Value);
        Assert.NotNull(resp.Data);
        return resp.Data!;
    }

    // ==================== 1. 汇总模式规范化（fail closed） ====================

    [Theory]
    [InlineData(null, "none")]
    [InlineData("", "none")]
    [InlineData("   ", "none")]
    [InlineData("none", "none")]
    [InlineData("NONE", "none")]
    [InlineData("supplierCurrency", "supplierCurrency")]
    [InlineData("SUPPLIERCURRENCY", "supplierCurrency")]
    [InlineData("supplierCurrencyLink", "supplierCurrencyLink")]
    [InlineData("SupplierCurrencyLink", "supplierCurrencyLink")]
    public void NormalizeSummaryMode_accepts_only_known_monetary_modes(string? raw, string expected)
        => Assert.Equal(expected, DynamicSupplierExposureReportRules.NormalizeSummaryMode(raw));

    [Theory]
    [InlineData("supplier")]
    [InlineData("currency")]
    [InlineData("linkStatus")]
    [InlineData("supplierCurrencyLinkX")]
    [InlineData("total")]
    public void NormalizeSummaryMode_rejects_invalid_modes(string raw)
    {
        var ex = Assert.Throws<BusinessException>(
            () => DynamicSupplierExposureReportRules.NormalizeSummaryMode(raw));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    // ==================== 2. 供应商 + 币种隔离（绝不跨币种合并） ====================

    [Fact]
    public async Task Preview_summary_separates_suppliers_and_currencies()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedSupplier(db, SupplierA, "甲供应商");
        SeedSupplier(db, SupplierB, "乙供应商");
        SeedOrder(db, "PO-S-1", SupplierA, Currency.CNY, 100m, null, (ProductA, 1m));
        SeedOrder(db, "PO-S-2", SupplierA, Currency.CNY, 200m, null, (ProductA, 1m));
        SeedOrder(db, "PO-S-3", SupplierA, Currency.USD, 300m, null, (ProductA, 1m));
        SeedOrder(db, "PO-S-4", SupplierB, Currency.CNY, 400m, null, (ProductA, 1m));
        await db.SaveChangesAsync();

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var page = PreviewOk(await ctl.Preview(new DynamicSupplierExposureReportRequest
        { SummaryMode = "supplierCurrency", PageSize = 10 }));

        Assert.Equal("supplierCurrency", page.SummaryMode);
        Assert.Equal(3, page.Summaries!.Count);
        var byKey = page.Summaries.ToDictionary(s => $"{s.SupplierId}:{s.Currency}", StringComparer.Ordinal);
        Assert.Equal(300m, byKey[$"{SupplierA}:CNY"].OrderedAmount);
        Assert.Equal(2, byKey[$"{SupplierA}:CNY"].OrderCount);
        Assert.Equal("甲供应商", byKey[$"{SupplierA}:CNY"].SupplierName);
        Assert.Equal(300m, byKey[$"{SupplierA}:USD"].OrderedAmount);
        Assert.Equal(400m, byKey[$"{SupplierB}:CNY"].OrderedAmount);
        Assert.Equal("乙供应商", byKey[$"{SupplierB}:CNY"].SupplierName);
    }

    // ==================== 3. 链接可用与不唯一 / 无引用分离（未知不并入权威已结算合计） ====================

    [Fact]
    public async Task Preview_summary_keeps_linked_settlement_and_ambiguous_unavailable_amounts_separate()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedSupplier(db, SupplierA, "甲供应商");
        var linkedSo = SeedSalesOrder(db, "SO-SUM-1");
        var ambiguousSo = SeedSalesOrder(db, "SO-SUM-2");
        SeedOrder(db, "PO-SUM-1", SupplierA, Currency.CNY, 100m, linkedSo.Id, (ProductA, 1m));
        var apply = SeedPaymentApply(db, "HK-SUM-1", linkedSo.Id);
        SeedPayment(db, "FK-SUM-1", apply.Id, SupplierA, 60m, Currency.CNY, DocumentStatus.Approved);
        SeedPayment(db, "FK-SUM-2", apply.Id, SupplierA, 20m, Currency.CNY, DocumentStatus.Submitted);
        SeedOrder(db, "PO-SUM-2", SupplierA, Currency.CNY, 200m, ambiguousSo.Id, (ProductA, 1m));
        SeedOrder(db, "PO-SUM-3", SupplierA, Currency.CNY, 50m, ambiguousSo.Id, (ProductA, 1m));
        SeedOrder(db, "PO-SUM-4", SupplierA, Currency.CNY, 40m, null, (ProductA, 1m));
        await db.SaveChangesAsync();

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var page = PreviewOk(await ctl.Preview(new DynamicSupplierExposureReportRequest
        { SummaryMode = "supplierCurrency", PageSize = 10 }));

        var summary = Assert.Single(page.Summaries!);
        Assert.Null(summary.LinkStatus);
        Assert.Equal(4, summary.OrderCount);
        Assert.Equal(390m, summary.OrderedAmount);
        Assert.Equal(1, summary.LinkedOrderCount);
        Assert.Equal(100m, summary.LinkedOrderedAmount);
        Assert.Equal(2, summary.AmbiguousOrderCount);
        Assert.Equal(250m, summary.AmbiguousOrderedAmount);
        Assert.Equal(1, summary.UnavailableOrderCount);
        Assert.Equal(40m, summary.UnavailableOrderedAmount);
        Assert.Equal(60m, summary.SettledAmount);
        Assert.Equal(40m, summary.OutstandingAmount);
        Assert.Equal(20m, summary.SubmittedAmount);
        Assert.Equal(0, summary.UnknownSettlementOrderCount);
    }

    [Fact]
    public async Task Preview_summary_no_linked_orders_keeps_settlement_unknown()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedSupplier(db, SupplierA, "甲供应商");
        var ambiguousSo = SeedSalesOrder(db, "SO-SUM-3");
        SeedOrder(db, "PO-SUM-5", SupplierA, Currency.CNY, 100m, ambiguousSo.Id, (ProductA, 1m));
        SeedOrder(db, "PO-SUM-6", SupplierA, Currency.CNY, 50m, ambiguousSo.Id, (ProductA, 1m));
        SeedOrder(db, "PO-SUM-7", SupplierA, Currency.CNY, 40m, null, (ProductA, 1m));
        await db.SaveChangesAsync();

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var page = PreviewOk(await ctl.Preview(new DynamicSupplierExposureReportRequest
        { SummaryMode = "supplierCurrency", PageSize = 10 }));

        var summary = Assert.Single(page.Summaries!);
        Assert.Equal(190m, summary.OrderedAmount);
        Assert.Equal(0, summary.LinkedOrderCount);
        Assert.Equal(2, summary.AmbiguousOrderCount);
        Assert.Equal(150m, summary.AmbiguousOrderedAmount);
        Assert.Equal(1, summary.UnavailableOrderCount);
        Assert.Equal(40m, summary.UnavailableOrderedAmount);
        Assert.Null(summary.SettledAmount);
        Assert.Null(summary.OutstandingAmount);
        Assert.Null(summary.SubmittedAmount);
    }

    // ==================== 4. 链接状态拆分（supplierCurrencyLink） ====================

    [Fact]
    public async Task Preview_summary_splits_by_link_status_when_requested()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedSupplier(db, SupplierA, "甲供应商");
        var linkedSo = SeedSalesOrder(db, "SO-SPLIT-1");
        var ambiguousSo = SeedSalesOrder(db, "SO-SPLIT-2");
        SeedOrder(db, "PO-SPLIT-1", SupplierA, Currency.CNY, 100m, linkedSo.Id, (ProductA, 1m));
        var apply = SeedPaymentApply(db, "HK-SPLIT-1", linkedSo.Id);
        SeedPayment(db, "FK-SPLIT-1", apply.Id, SupplierA, 30m, Currency.CNY, DocumentStatus.Approved);
        SeedOrder(db, "PO-SPLIT-2", SupplierA, Currency.CNY, 200m, ambiguousSo.Id, (ProductA, 1m));
        SeedOrder(db, "PO-SPLIT-3", SupplierA, Currency.CNY, 50m, ambiguousSo.Id, (ProductA, 1m));
        SeedOrder(db, "PO-SPLIT-4", SupplierA, Currency.CNY, 40m, null, (ProductA, 1m));
        await db.SaveChangesAsync();

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var page = PreviewOk(await ctl.Preview(new DynamicSupplierExposureReportRequest
        { SummaryMode = "supplierCurrencyLink", PageSize = 10 }));

        Assert.Equal("supplierCurrencyLink", page.SummaryMode);
        Assert.Equal(3, page.Summaries!.Count);

        var linked = page.Summaries[0];
        Assert.Equal(DynamicSupplierExposureReportRules.LinkLinked, linked.LinkStatus);
        Assert.Equal("链接可用", linked.LinkStatusText);
        Assert.Equal(100m, linked.OrderedAmount);
        Assert.Equal(30m, linked.SettledAmount);
        Assert.Equal(70m, linked.OutstandingAmount);

        var ambiguous = page.Summaries[1];
        Assert.Equal(DynamicSupplierExposureReportRules.LinkAmbiguous, ambiguous.LinkStatus);
        Assert.Equal(250m, ambiguous.OrderedAmount);
        Assert.Equal(0, ambiguous.LinkedOrderCount);
        Assert.Null(ambiguous.SettledAmount);

        var unavailable = page.Summaries[2];
        Assert.Equal(DynamicSupplierExposureReportRules.LinkUnavailable, unavailable.LinkStatus);
        Assert.Equal(40m, unavailable.OrderedAmount);
        Assert.Null(unavailable.SettledAmount);
    }

    // ==================== 5. 未知金额保持 null（绝不轧为 0） ====================

    [Fact]
    public void BuildAmountSummaries_linked_row_with_unknown_settlement_keeps_totals_null()
    {
        var rows = new List<IReadOnlyDictionary<string, object?>>
        {
            new Dictionary<string, object?>
            {
                ["supplierId"] = 1L,
                ["supplierName"] = "甲供应商",
                ["currency"] = "CNY",
                ["linkStatus"] = DynamicSupplierExposureReportRules.LinkLinked,
                ["orderedAmount"] = 100m,
                ["settledAmount"] = null,
                ["outstandingAmount"] = null,
                ["submittedAmount"] = null,
            }
        };

        var summaries = DynamicSupplierExposureReportRules.BuildAmountSummaries(rows, "supplierCurrency");

        var summary = Assert.Single(summaries);
        Assert.Equal(100m, summary.OrderedAmount);
        Assert.Equal(1, summary.LinkedOrderCount);
        Assert.Null(summary.SettledAmount);
        Assert.Null(summary.OutstandingAmount);
        Assert.Null(summary.SubmittedAmount);
        Assert.Equal(1, summary.UnknownSettlementOrderCount);
    }

    // ==================== 6. 空页 / 默认模式 ====================

    [Fact]
    public async Task Preview_summary_default_mode_is_none_with_empty_summaries()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedSupplier(db, SupplierA, "甲供应商");
        SeedOrder(db, "PO-DEF-1", SupplierA, Currency.CNY, 100m, null, (ProductA, 1m));
        await db.SaveChangesAsync();

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var page = PreviewOk(await ctl.Preview(new DynamicSupplierExposureReportRequest { PageSize = 10 }));

        Assert.Equal("none", page.SummaryMode);
        Assert.Empty(page.Summaries!);
    }

    [Fact]
    public async Task Preview_summary_empty_page_returns_no_summaries()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var page = PreviewOk(await ctl.Preview(new DynamicSupplierExposureReportRequest
        { SummaryMode = "supplierCurrency", PageSize = 50 }));

        Assert.Equal("supplierCurrency", page.SummaryMode);
        Assert.Empty(page.Summaries!);
    }

    // ==================== 7. 分页边界（汇总只统计当前页） ====================

    [Fact]
    public async Task Preview_summary_scopes_amounts_to_the_current_page()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedSupplier(db, SupplierA, "甲供应商");
        for (var i = 1; i <= 3; i++)
            SeedOrder(db, $"PO-PAGE-{i}", SupplierA, Currency.CNY, i * 10m, null, (ProductA, 1m),
                new DateTime(2026, 1, i));
        await db.SaveChangesAsync();

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var page1 = PreviewOk(await ctl.Preview(new DynamicSupplierExposureReportRequest
        { SummaryMode = "supplierCurrency", PageSize = 2, Page = 1 }));
        var page2 = PreviewOk(await ctl.Preview(new DynamicSupplierExposureReportRequest
        { SummaryMode = "supplierCurrency", PageSize = 2, Page = 2 }));

        var s1 = Assert.Single(page1.Summaries!);
        Assert.Equal(2, s1.OrderCount);
        Assert.Equal(30m, s1.OrderedAmount);   // 10 + 20
        var s2 = Assert.Single(page2.Summaries!);
        Assert.Equal(1, s2.OrderCount);
        Assert.Equal(30m, s2.OrderedAmount);   // 30
        Assert.Equal(3, page1.Total);
    }

    // ==================== 8. 无效汇总模式（fail closed，先于源读取） ====================

    [Fact]
    public async Task Preview_summary_invalid_mode_rejected_before_reads()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Preview(
            new DynamicSupplierExposureReportRequest { SummaryMode = "supplierCurrencyLinkX" }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    // ==================== 9. 授权（fail closed） ====================

    [Fact]
    public async Task Preview_summary_denied_without_purchase_order_menu()
    {
        using var db = TestDbFactory.Create();
        var user = SeedUser(db, "nomenu");
        var role = SeedRole(db, "Role-nomenu");
        SeedUserRole(db, user.Id, role.Id);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Preview(
            new DynamicSupplierExposureReportRequest { SummaryMode = "supplierCurrency" }));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    [Fact]
    public async Task Preview_summary_denied_without_identity()
    {
        using var db = TestDbFactory.Create();
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, null);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Preview(
            new DynamicSupplierExposureReportRequest { SummaryMode = "supplierCurrency" }));
        Assert.Equal(ErrorCodes.Unauthorized, ex.Code);
    }

    // ==================== 10. 只读不写库 ====================

    [Fact]
    public async Task Preview_summary_is_read_only_and_writes_nothing()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedSupplier(db, SupplierA, "甲供应商");
        var salesOrder = SeedSalesOrder(db, "SO-RW-1");
        SeedOrder(db, "PO-RW-1", SupplierA, Currency.CNY, 100m, salesOrder.Id, (ProductA, 1m));
        await db.SaveChangesAsync();

        var counting = DynamicSupplierExposureGroupingTests.CountingDbContext.Wrap(db);
        var ctl = new DynamicSupplierExposureReportController(counting.Proxy);
        TestAuth.SetUser(ctl, uid);

        var page = PreviewOk(await ctl.Preview(new DynamicSupplierExposureReportRequest
        { SummaryMode = "supplierCurrency", PageSize = 10 }));

        Assert.Single(page.Summaries!);
        Assert.Equal(0, counting.WriteCalls);
    }

    // ==================== 11. 证据边界（结构上杜绝应付余额 / 付款授权推断） ====================

    [Fact]
    public void SummaryDto_exposes_no_payable_ledger_or_payment_authority_fields()
    {
        var names = typeof(DynamicSupplierExposureReportSummaryDto).GetProperties().Select(p => p.Name).ToList();
        Assert.Contains("SupplierId", names);
        Assert.Contains("Currency", names);
        Assert.Contains("OrderedAmount", names);
        Assert.Contains("SettledAmount", names);
        Assert.DoesNotContain(names, n => n.Contains("Payable", StringComparison.Ordinal));
        Assert.DoesNotContain(names, n => n.Contains("Ledger", StringComparison.Ordinal));
        Assert.DoesNotContain(names, n => n.Contains("Invoice", StringComparison.Ordinal));
        Assert.DoesNotContain(names, n => n.Contains("DueDate", StringComparison.Ordinal));
        Assert.DoesNotContain(names, n => n.Contains("Aging", StringComparison.Ordinal));
        Assert.DoesNotContain(names, n => n.Contains("Authorization", StringComparison.Ordinal));
    }
}
