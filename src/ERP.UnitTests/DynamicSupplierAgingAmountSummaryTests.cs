using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-146 动态供应商对账与账龄报表「当前授权预览页按供应商 + 原币的已知有效金额汇总」单元测试。
/// <para>金额汇总语义：<see cref="DynamicSupplierAgingReportRules.BuildAmountSummaries"/> 只汇总「当前授权预览页」中计入有效应付证据合计
/// （已登记未作废）的发票证据：含税总额恒可确认（直接求和）；有效已分配与算术剩余证据只要任一行未知（命中有界上限）
/// 或无效（超过含税总额），对应合计即按「未知」（null）返回，绝不轧为 0 或给部分合计；草稿 / 已作废金额绝不并入；
/// 供应商 + 原币严格隔离（绝不跨币种合并或换算）；账龄汇总模式下未知到期日独立分组。</para>
/// 覆盖：汇总模式规范化、多币种多供应商隔离、草稿 / 已作废排除、未知与超额分配证据按未知、账龄分桶与未知到期日分离、
/// 空页 / 分页边界、无效汇总模式（fail closed）、无采购订单菜单授权（权限不足）、无身份（未认证）与只读不写库。
/// <para>全部使用内存数据库（TestDbFactory），不连接 SQL Server、不启动 API、不执行任何 SQL / seed。</para>
/// </summary>
public class DynamicSupplierAgingAmountSummaryTests
{
    private static readonly DateTime AsOf = new(2026, 9, 24);
    private const long SupplierA = 968001L;
    private const long SupplierB = 968002L;

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
        SeedRoleMenu(db, role.Id, SeedMenu(db, DynamicSupplierAgingReportRules.RequiredMenuCode).Id);
        return user.Id;
    }

    private static BaseSupplier SeedSupplier(ErpDbContext db, long id, string name, int status = 1, bool deleted = false)
    {
        var supplier = new BaseSupplier
        {
            Id = id,
            SupplierCode = $"S{id}",
            SupplierName = name,
            Status = status,
            IsDeleted = deleted
        };
        db.BaseSuppliers.Add(supplier);
        return supplier;
    }

    private static PurchaseInvoice SeedInvoice(
        ErpDbContext db, long id, long supplierId, string currency, string number,
        decimal net, decimal tax, decimal gross, int status, DateTime invoiceDate,
        DateTime? dueDate = null, string paymentTerms = "", bool deleted = false)
    {
        var supplier = db.BaseSuppliers.Local.FirstOrDefault(s => s.Id == supplierId);
        var invoice = new PurchaseInvoice
        {
            Id = id,
            InvoiceType = PurchaseInvoiceRules.InvoiceTypeOrdinary,
            InvoiceCode = string.Empty,
            InvoiceNumber = number,
            NormalizedInvoiceCode = string.Empty,
            NormalizedInvoiceNumber = PurchaseInvoiceRules.NormalizeIdentityPart(number),
            InvoiceDate = invoiceDate.Date,
            SupplierId = supplierId,
            SupplierCode = supplier?.SupplierCode ?? $"S{supplierId}",
            SupplierName = supplier?.SupplierName ?? $"供应商{supplierId}",
            Currency = currency,
            NetAmount = net,
            TaxAmount = tax,
            GrossAmount = gross,
            DueDate = dueDate?.Date,
            PaymentTerms = paymentTerms,
            Status = status,
            RecordedAt = status == PurchaseInvoiceRules.StatusDraft ? null : invoiceDate.Date.AddDays(1),
            VoidedAt = status == PurchaseInvoiceRules.StatusVoided ? invoiceDate.Date.AddDays(2) : null,
            VoidReason = status == PurchaseInvoiceRules.StatusVoided ? "作废测试" : string.Empty,
            IsDeleted = deleted
        };
        db.PurchaseInvoices.Add(invoice);
        return invoice;
    }

    private static DynamicSupplierAgingReportController NewController(IErpDbContext db)
        => new(db);

    private static DynamicSupplierAgingReportPageDto PreviewOk(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<ApiResponse<DynamicSupplierAgingReportPageDto>>(ok.Value);
        Assert.NotNull(resp.Data);
        return resp.Data!;
    }

    /// <summary>构造一行「字段 → 值」证据（仅金额汇总所需白名单字段；草稿 / 已作废用 active=false 表达排除语义）</summary>
    private static Dictionary<string, object?> Row(
        long supplierId, string supplierCode, string supplierName, string currency,
        decimal gross, bool active, string? agingBucket,
        decimal? allocated, decimal? remaining, string remainingState)
        => new(StringComparer.Ordinal)
        {
            ["supplierId"] = supplierId,
            ["supplierCode"] = supplierCode,
            ["supplierName"] = supplierName,
            ["currency"] = currency,
            ["grossAmount"] = gross,
            ["isActiveEvidence"] = active,
            ["agingBucket"] = agingBucket,
            ["activeAllocatedAmount"] = allocated,
            ["remainingAmount"] = remaining,
            ["remainingState"] = remainingState,
        };

    private static void SeedInvoicePaymentEvidence(
        ErpDbContext db, long id, PurchaseInvoice invoice, decimal allocatedAmount,
        decimal paymentAmount = 1000m,
        int status = SupplierPaymentInvoiceAllocationRules.StatusActive,
        string? rowCurrency = null, bool paymentDeleted = false)
    {
        var payment = BuildPayment(db, id + 5_000_000L, invoice, paymentAmount, paymentDeleted);
        db.SupplierPaymentInvoiceAllocations.Add(
            BuildInvoicePaymentRow(id, invoice, payment, allocatedAmount, rowCurrency, status));
    }

    private static FinancePayment BuildPayment(
        ErpDbContext db, long id, PurchaseInvoice invoice, decimal amount, bool deleted)
    {
        var payment = new FinancePayment
        {
            Id = id,
            PaymentNo = $"FK-146-{id}",
            PaymentDate = AsOf.AddDays(-3),
            SupplierId = invoice.SupplierId,
            Amount = amount,
            Currency = Enum.Parse<Currency>(invoice.Currency),
            Status = DocumentStatus.Approved,
            IsDeleted = deleted
        };
        db.FinancePayments.Add(payment);
        return payment;
    }

    private static SupplierPaymentInvoiceAllocation BuildInvoicePaymentRow(
        long id, PurchaseInvoice invoice, FinancePayment payment, decimal allocatedAmount,
        string? rowCurrency, int status)
        => new()
        {
            Id = id,
            PaymentId = payment.Id,
            PaymentNo = payment.PaymentNo,
            PaymentDate = payment.PaymentDate,
            PaymentStatus = (int)payment.Status,
            PaymentStatusText = SupplierPaymentInvoiceAllocationRules.PaymentStatusText((int)payment.Status),
            PaymentAmount = payment.Amount,
            PurchaseInvoiceId = invoice.Id,
            InvoiceType = invoice.InvoiceType,
            InvoiceCode = invoice.InvoiceCode,
            InvoiceNumber = invoice.InvoiceNumber,
            InvoiceIdentityText = SupplierPaymentInvoiceAllocationRules.InvoiceIdentity(invoice),
            InvoiceDate = invoice.InvoiceDate,
            InvoiceStatus = invoice.Status,
            InvoiceStatusText = SupplierPaymentInvoiceAllocationRules.InvoiceStatusText(invoice.Status),
            InvoiceGrossAmount = invoice.GrossAmount,
            SupplierId = invoice.SupplierId,
            SupplierCode = invoice.SupplierCode,
            SupplierName = invoice.SupplierName,
            AllocatedAmount = allocatedAmount,
            Currency = rowCurrency ?? invoice.Currency,
            Status = status,
            AllocatedAt = AsOf.AddDays(-2),
            RecordedBy = "tester",
            VoidedAt = status == SupplierPaymentInvoiceAllocationRules.StatusVoided ? AsOf.AddDays(-1) : null,
            VoidReason = status == SupplierPaymentInvoiceAllocationRules.StatusVoided ? "作废重登" : string.Empty
        };

    // ==================== 1. 汇总模式规范化 ====================

    [Theory]
    [InlineData(null, "none")]
    [InlineData("", "none")]
    [InlineData("  ", "none")]
    [InlineData("none", "none")]
    [InlineData("NONE", "none")]
    [InlineData("supplierCurrency", "supplierCurrency")]
    [InlineData("SUPPLIERCURRENCY", "supplierCurrency")]
    [InlineData("supplierCurrencyAging", "supplierCurrencyAging")]
    [InlineData("SUPPLIERCURRENCYAGING", "supplierCurrencyAging")]
    public void NormalizeSummaryMode_合法取值_规范化(string? input, string expected)
    {
        Assert.Equal(expected, DynamicSupplierAgingReportRules.NormalizeSummaryMode(input));
    }

    [Theory]
    [InlineData("supplier")]
    [InlineData("currency")]
    [InlineData("aging")]
    [InlineData("grandTotal")]
    [InlineData("supplier_currency")]
    [InlineData("supplier-currency")]
    [InlineData("unknown")]
    [InlineData("供应商币种")]
    public void NormalizeSummaryMode_非法取值_拒绝(string? input)
    {
        var ex = Assert.Throws<BusinessException>(() => DynamicSupplierAgingReportRules.NormalizeSummaryMode(input));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    // ==================== 2. 纯规则：多币种多供应商隔离 ====================

    [Fact]
    public void BuildAmountSummaries_多币种多供应商_按供应商币种隔离()
    {
        var rows = new List<Dictionary<string, object?>>
        {
            Row(SupplierA, "SA", "甲供应商", "CNY", 100m, true, "not_due", 0m, 100m, "known"),
            Row(SupplierA, "SA", "甲供应商", "CNY", 50m, true, "not_due", 0m, 50m, "known"),
            Row(SupplierA, "SA", "甲供应商", "USD", 200m, true, "not_due", 0m, 200m, "known"),
            Row(SupplierB, "SB", "乙供应商", "CNY", 300m, true, "not_due", 0m, 300m, "known"),
        };

        var summaries = DynamicSupplierAgingReportRules.BuildAmountSummaries(rows, "supplierCurrency");

        Assert.Equal(3, summaries.Count);
        Assert.Equal(new[] { (SupplierA, "CNY"), (SupplierA, "USD"), (SupplierB, "CNY") },
            summaries.Select(s => (s.SupplierId, s.Currency)));

        var aCny = summaries[0];
        Assert.Equal(2, aCny.InvoiceCount);
        Assert.Equal(150m, aCny.GrossAmount);
        Assert.Equal(0m, aCny.ActiveAllocatedAmount);
        Assert.Equal(150m, aCny.RemainingAmount);

        var aUsd = summaries[1];
        Assert.Equal("USD", aUsd.Currency);
        Assert.Equal(200m, aUsd.GrossAmount);

        var bCny = summaries[2];
        Assert.Equal(SupplierB, bCny.SupplierId);
        Assert.Equal(300m, bCny.GrossAmount);
    }

    // ==================== 3. 纯规则：草稿 / 已作废排除 ====================

    [Fact]
    public void BuildAmountSummaries_草稿与作废金额排除()
    {
        var rows = new List<Dictionary<string, object?>>
        {
            Row(SupplierA, "SA", "甲供应商", "CNY", 100m, true, "not_due", 0m, 100m, "known"),
            Row(SupplierA, "SA", "甲供应商", "CNY", 999m, false, "not_due", null, null, "unknown"), // 草稿
            Row(SupplierA, "SA", "甲供应商", "CNY", 888m, false, "not_due", null, null, "unknown"), // 已作废
        };

        var summary = Assert.Single(DynamicSupplierAgingReportRules.BuildAmountSummaries(rows, "supplierCurrency"));

        Assert.Equal(1, summary.InvoiceCount);
        Assert.Equal(100m, summary.GrossAmount);
        Assert.Equal(0m, summary.ActiveAllocatedAmount);
        Assert.Equal(100m, summary.RemainingAmount);
    }

    // ==================== 4. 纯规则：未知与超额分配证据 ====================

    [Fact]
    public void BuildAmountSummaries_未知与超额分配_对应合计为null()
    {
        var rows = new List<Dictionary<string, object?>>
        {
            Row(SupplierA, "SA", "甲供应商", "CNY", 100m, true, "not_due", 40m, 60m, "known"),
            Row(SupplierA, "SA", "甲供应商", "CNY", 100m, true, "not_due", null, null, "unknown"),   // 命中上限（未知）
            Row(SupplierA, "SA", "甲供应商", "CNY", 100m, true, "not_due", 120m, null, "over_allocated"), // 超额（无效）
        };

        var summary = Assert.Single(DynamicSupplierAgingReportRules.BuildAmountSummaries(rows, "supplierCurrency"));

        Assert.Equal(3, summary.InvoiceCount);
        Assert.Equal(300m, summary.GrossAmount);          // 含税总额恒可确认
        Assert.Null(summary.ActiveAllocatedAmount);       // 任一行未知 → 未知
        Assert.Null(summary.RemainingAmount);             // 任一行未知 / 无效 → 未知
        Assert.Equal(2, summary.UnknownRemainingInvoiceCount);
        Assert.Equal(1, summary.OverAllocatedInvoiceCount);
    }

    // ==================== 5. 纯规则：账龄分桶与未知到期日分离 ====================

    [Fact]
    public void BuildAmountSummaries_账龄分桶_未知到期日独立分组()
    {
        var rows = new List<Dictionary<string, object?>>
        {
            Row(SupplierA, "SA", "甲供应商", "CNY", 100m, true, "not_due", 0m, 100m, "known"),
            Row(SupplierA, "SA", "甲供应商", "CNY", 200m, true, "overdue_1_30", 0m, 200m, "known"),
            Row(SupplierA, "SA", "甲供应商", "CNY", 50m, true, null, 0m, 50m, "known"), // 未知到期日
        };

        var summaries = DynamicSupplierAgingReportRules.BuildAmountSummaries(rows, "supplierCurrencyAging");

        Assert.Equal(3, summaries.Count);
        Assert.Equal(
            new[] { "not_due", "overdue_1_30", "unknown_due_date" },
            summaries.Select(s => s.AgingBucket));

        var unknown = summaries[2];
        Assert.Equal(DynamicSupplierAgingReportRules.UnknownDueDateBucket, unknown.AgingBucket);
        Assert.Equal("未知到期日", unknown.AgingBucketText);
        Assert.Equal(50m, unknown.GrossAmount);
    }

    [Fact]
    public void BuildAmountSummaries_空页与none_空列表()
    {
        Assert.Empty(DynamicSupplierAgingReportRules.BuildAmountSummaries(Array.Empty<Dictionary<string, object?>>(), "supplierCurrency"));
        Assert.Empty(DynamicSupplierAgingReportRules.BuildAmountSummaries(Array.Empty<Dictionary<string, object?>>(), "supplierCurrencyAging"));

        var rows = new List<Dictionary<string, object?>>
        {
            Row(SupplierA, "SA", "甲供应商", "CNY", 100m, true, "not_due", 0m, 100m, "known"),
        };
        Assert.Empty(DynamicSupplierAgingReportRules.BuildAmountSummaries(rows, "none"));
    }

    // ==================== 6. 控制器：金额汇总（复用 ERP-068 权威派生） ====================

    [Fact]
    public async Task Preview_金额汇总_已知有效金额正确()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedSupplier(db, SupplierA, "甲供应商");
        var partial = SeedInvoice(db, 968101L, SupplierA, "CNY", "A001", 100m, 13m, 113m,
            PurchaseInvoiceRules.StatusRecorded, AsOf.AddDays(-20), dueDate: AsOf.AddDays(-20));
        SeedInvoice(db, 968102L, SupplierA, "CNY", "A002", 100m, 0m, 100m,
            PurchaseInvoiceRules.StatusRecorded, AsOf.AddDays(-20), dueDate: AsOf.AddDays(-20));
        await db.SaveChangesAsync();

        SeedInvoicePaymentEvidence(db, 968931L, partial, 40m, paymentAmount: 200m);
        await db.SaveChangesAsync();

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var page = PreviewOk(await ctl.Preview(new DynamicSupplierAgingReportRequest
        { SummaryMode = "supplierCurrency", AsOfDate = AsOf, PageSize = 50 }));

        Assert.Equal("supplierCurrency", page.SummaryMode);
        var summary = Assert.Single(page.Summaries!);
        Assert.Equal(SupplierA, summary.SupplierId);
        Assert.Equal("CNY", summary.Currency);
        Assert.Equal(2, summary.InvoiceCount);
        Assert.Equal(213m, summary.GrossAmount);      // 113 + 100
        Assert.Equal(40m, summary.ActiveAllocatedAmount);
        Assert.Equal(173m, summary.RemainingAmount);  // (113-40) + (100-0)
        Assert.Equal(0, summary.UnknownRemainingInvoiceCount);
        Assert.Equal(0, summary.OverAllocatedInvoiceCount);
    }

    [Fact]
    public async Task Preview_超额分配_剩余合计为null()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedSupplier(db, SupplierA, "甲供应商");
        var over = SeedInvoice(db, 968201L, SupplierA, "CNY", "B001", 100m, 0m, 100m,
            PurchaseInvoiceRules.StatusRecorded, AsOf.AddDays(-20), dueDate: AsOf.AddDays(-20));
        await db.SaveChangesAsync();

        SeedInvoicePaymentEvidence(db, 968941L, over, 60m, paymentAmount: 200m);
        SeedInvoicePaymentEvidence(db, 968942L, over, 60m, paymentAmount: 200m);
        await db.SaveChangesAsync();

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var page = PreviewOk(await ctl.Preview(new DynamicSupplierAgingReportRequest
        { SummaryMode = "supplierCurrency", AsOfDate = AsOf, PageSize = 50 }));

        var summary = Assert.Single(page.Summaries!);
        Assert.Equal(100m, summary.GrossAmount);
        Assert.Equal(120m, summary.ActiveAllocatedAmount); // 有效已分配金额本身已知（超过含税总额 → 无效证据）
        Assert.Null(summary.RemainingAmount);               // 无效证据 → 剩余按未知，绝不轧为 0 或负数
        Assert.Equal(1, summary.UnknownRemainingInvoiceCount);
        Assert.Equal(1, summary.OverAllocatedInvoiceCount);
    }

    [Fact]
    public async Task Preview_账龄汇总_未知到期日独立_草稿作废排除()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedSupplier(db, SupplierA, "甲供应商");
        SeedInvoice(db, 968301L, SupplierA, "CNY", "C001", 100m, 0m, 100m,
            PurchaseInvoiceRules.StatusRecorded, AsOf.AddDays(-40), dueDate: AsOf.AddDays(5));   // not_due
        SeedInvoice(db, 968302L, SupplierA, "CNY", "C002", 200m, 0m, 200m,
            PurchaseInvoiceRules.StatusRecorded, AsOf.AddDays(-40));                              // 未知到期日
        SeedInvoice(db, 968303L, SupplierA, "CNY", "C003", 900m, 0m, 900m,
            PurchaseInvoiceRules.StatusDraft, AsOf.AddDays(-40), dueDate: AsOf.AddDays(5));      // 草稿
        SeedInvoice(db, 968304L, SupplierA, "CNY", "C004", 800m, 0m, 800m,
            PurchaseInvoiceRules.StatusVoided, AsOf.AddDays(-40), dueDate: AsOf.AddDays(5));     // 已作废
        await db.SaveChangesAsync();

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var page = PreviewOk(await ctl.Preview(new DynamicSupplierAgingReportRequest
        { SummaryMode = "supplierCurrencyAging", AsOfDate = AsOf, PageSize = 50 }));

        Assert.Equal("supplierCurrencyAging", page.SummaryMode);
        Assert.Equal(2, page.Summaries!.Count);
        Assert.Equal(
            new[] { "not_due", "unknown_due_date" },
            page.Summaries.Select(s => s.AgingBucket));

        var notDue = page.Summaries[0];
        Assert.Equal(100m, notDue.GrossAmount);
        Assert.Equal(1, notDue.InvoiceCount);

        var unknown = page.Summaries[1];
        Assert.Equal(DynamicSupplierAgingReportRules.UnknownDueDateBucket, unknown.AgingBucket);
        Assert.Equal(200m, unknown.GrossAmount);
        Assert.Equal(1, unknown.InvoiceCount);
    }

    // ==================== 7. 控制器：fail closed 与分页边界 ====================

    [Fact]
    public async Task Preview_无效汇总模式_源读取前拒绝()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Preview(
            new DynamicSupplierAgingReportRequest { SummaryMode = "grandTotal" }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public async Task Preview_无采购订单菜单授权_拒绝()
    {
        using var db = TestDbFactory.Create();
        var user = SeedUser(db, "nomenu");
        var role = SeedRole(db, "Role-nomenu");
        SeedUserRole(db, user.Id, role.Id);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Preview(
            new DynamicSupplierAgingReportRequest { SummaryMode = "supplierCurrency" }));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    [Fact]
    public async Task Preview_无身份_未认证拒绝()
    {
        using var db = TestDbFactory.Create();
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, null);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Preview(
            new DynamicSupplierAgingReportRequest { SummaryMode = "supplierCurrency" }));
        Assert.Equal(ErrorCodes.Unauthorized, ex.Code);
    }

    [Fact]
    public async Task Preview_空页_汇总为空()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var page = PreviewOk(await ctl.Preview(new DynamicSupplierAgingReportRequest
        { SummaryMode = "supplierCurrency", AsOfDate = AsOf, PageSize = 50 }));

        Assert.Equal("supplierCurrency", page.SummaryMode);
        Assert.Empty(page.Summaries!);
    }

    [Fact]
    public async Task Preview_分页边界_汇总只统计当前页()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedSupplier(db, SupplierA, "甲供应商");
        for (var i = 1; i <= 3; i++)
        {
            SeedInvoice(db, 968400L + i, SupplierA, "CNY", $"D{i:D3}", 100m, 0m, 100m,
                PurchaseInvoiceRules.StatusRecorded, AsOf.AddDays(-40), dueDate: AsOf.AddDays(5));
        }
        await db.SaveChangesAsync();

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var page1 = PreviewOk(await ctl.Preview(new DynamicSupplierAgingReportRequest
        { SummaryMode = "supplierCurrency", AsOfDate = AsOf, PageSize = 2, Page = 1 }));
        var page2 = PreviewOk(await ctl.Preview(new DynamicSupplierAgingReportRequest
        { SummaryMode = "supplierCurrency", AsOfDate = AsOf, PageSize = 2, Page = 2 }));

        var s1 = Assert.Single(page1.Summaries!);
        var s2 = Assert.Single(page2.Summaries!);
        Assert.Equal(2, s1.InvoiceCount);
        Assert.Equal(200m, s1.GrossAmount);
        Assert.Equal(1, s2.InvoiceCount);
        Assert.Equal(100m, s2.GrossAmount);
        Assert.Equal(3, page1.Total);
    }

    // ==================== 8. 只读不写库 ====================

    [Fact]
    public async Task Preview_只读不写库()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedSupplier(db, SupplierA, "甲供应商");
        SeedInvoice(db, 968501L, SupplierA, "CNY", "E001", 100m, 0m, 100m,
            PurchaseInvoiceRules.StatusRecorded, AsOf.AddDays(-40), dueDate: AsOf.AddDays(5));
        await db.SaveChangesAsync();

        var counting = DynamicSupplierAgingReportTests.CountingDbContext.Wrap(db);
        var ctl = new DynamicSupplierAgingReportController(counting.Proxy);
        TestAuth.SetUser(ctl, uid);

        var page = PreviewOk(await ctl.Preview(new DynamicSupplierAgingReportRequest
        { SummaryMode = "supplierCurrency", AsOfDate = AsOf, PageSize = 50 }));

        Assert.Single(page.Summaries!);
        Assert.Equal(0, counting.WriteCalls);
    }
}
