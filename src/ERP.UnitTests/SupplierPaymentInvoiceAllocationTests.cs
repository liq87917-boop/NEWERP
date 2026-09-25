using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// 供应商付款 → 采购发票 付款引用（分摊）登记单元测试（ERP-066）。覆盖：付款单 / 发票权威资格
/// （存在、未删除、已登记未作废、供应商一致、币种一致）、金额精度与大于 0、付款单金额上限与发票未引用含税总额上限、
/// 同一发票同一付款单不得重复有效引用、全额 / 部分引用与两侧未引用金额、作废保留原始值 / 快照 / 登记人与历史
/// （重复作废拒绝、作废后可重新登记）、付款单 / 发票软删除后历史仍可读、付款单侧与发票侧汇总派生、
/// 证据维度分离（与 ERP-049 采购订单引用金额分别记录、绝不相加）、台账过滤与分页有界、候选派生金额、
/// 非变更边界（付款单、发票、发票关联行、采购订单、ERP-049 引用行、库存、退税、费用与供应商均不被改写），
/// 以及模型 / 幂等结构 / 路由 / 前端接线契约。
/// 全部使用内存库（TestDbFactory），不连接 SQL Server、不执行任何 SQL / 部署脚本。
/// </summary>
public class SupplierPaymentInvoiceAllocationTests
{
    // ==================== 0. 测试脚手架 ====================

    private static SupplierPaymentInvoiceAllocationController BuildController(
        ErpDbContext db, string? userName = null)
    {
        var controller = new SupplierPaymentInvoiceAllocationController(db);
        var identity = new ClaimsIdentity(
            userName is null ? Array.Empty<Claim>() : new[] { new Claim(ClaimTypes.Name, userName) },
            "Test");
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
        };
        return controller;
    }

    private static BaseSupplier SeedSupplier(
        ErpDbContext db, string code, string name, int status = 1, bool deleted = false)
    {
        var supplier = new BaseSupplier
        {
            SupplierCode = code,
            SupplierName = name,
            Status = status,
            IsDeleted = deleted
        };
        db.BaseSuppliers.Add(supplier);
        db.SaveChanges();
        return supplier;
    }

    private static FinancePayment SeedPayment(
        ErpDbContext db, string paymentNo, long supplierId, decimal amount = 1000m,
        Currency currency = Currency.CNY, DocumentStatus status = DocumentStatus.Approved, bool deleted = false)
    {
        var payment = new FinancePayment
        {
            PaymentNo = paymentNo,
            PaymentDate = new DateTime(2026, 9, 10),
            SupplierId = supplierId,
            Amount = amount,
            Currency = currency,
            PaymentMethod = PaymentMethod.BankTransfer,
            Status = status,
            IsDeleted = deleted
        };
        db.FinancePayments.Add(payment);
        db.SaveChanges();
        return payment;
    }

    private static PurchaseOrder SeedOrder(
        ErpDbContext db, string orderNo, long supplierId, Currency currency = Currency.CNY,
        DocumentStatus status = DocumentStatus.Approved, decimal totalAmount = 1000m, bool deleted = false)
    {
        var order = new PurchaseOrder
        {
            OrderNo = orderNo,
            OrderDate = new DateTime(2026, 9, 1),
            SupplierId = supplierId,
            Currency = currency,
            TotalAmount = totalAmount,
            Status = status,
            IsDeleted = deleted,
            ArrivalProgress = "未到货",
            SettlementProgress = "未结算"
        };
        db.PurchaseOrders.Add(order);
        db.SaveChanges();
        return order;
    }

    /// <summary>
    /// 播种一张采购发票证据（默认**已登记**：ERP-066 只允许对已登记未作废发票登记付款引用）；
    /// 含税总额 = 净额（税额 0，满足金额等式口径），供应商编码 / 名称按播种值冻结为快照。
    /// </summary>
    private static PurchaseInvoice SeedInvoice(
        ErpDbContext db, string number, long supplierId, decimal grossAmount = 1000m,
        string currency = "CNY", string invoiceType = "普票", string code = "",
        int status = PurchaseInvoiceRules.StatusRecorded, bool deleted = false, string supplierName = "")
    {
        var invoice = new PurchaseInvoice
        {
            InvoiceType = invoiceType,
            InvoiceCode = code,
            InvoiceNumber = number,
            NormalizedInvoiceCode = PurchaseInvoiceRules.NormalizeIdentityPart(code),
            NormalizedInvoiceNumber = PurchaseInvoiceRules.NormalizeIdentityPart(number),
            InvoiceDate = new DateTime(2026, 9, 20),
            SupplierId = supplierId,
            SupplierCode = "S" + supplierId,
            SupplierName = string.IsNullOrEmpty(supplierName) ? "供应商" + supplierId : supplierName,
            Currency = currency,
            NetAmount = grossAmount,
            TaxAmount = 0m,
            GrossAmount = grossAmount,
            Status = status,
            IsDeleted = deleted
        };
        db.PurchaseInvoices.Add(invoice);
        db.SaveChanges();
        return invoice;
    }

    private static SupplierPaymentInvoiceAllocationSaveDto AllocateDto(
        long paymentId, long invoiceId, decimal amount, string remark = "")
        => new()
        {
            PaymentId = paymentId,
            PurchaseInvoiceId = invoiceId,
            AllocatedAmount = amount,
            Remark = remark
        };

    /// <summary>断言成功响应并取出数据（业务码必须为 0）</summary>
    private static T AssertOk<T>(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<ApiResponse<T>>(ok.Value);
        Assert.Equal(ErrorCodes.Success, resp.Code);
        Assert.NotNull(resp.Data);
        return resp.Data!;
    }

    /// <summary>断言业务异常的错误码（避免只断言消息文案）</summary>
    private static async Task<BusinessException> AssertBusinessAsync(int expectedCode, Func<Task> action)
    {
        var ex = await Assert.ThrowsAsync<BusinessException>(action);
        Assert.Equal(expectedCode, ex.Code);
        return ex;
    }

    /// <summary>按仓库根目录拼接文件的绝对路径（与其它契约测试口径一致）</summary>
    private static string RepoFile(params string[] segments)
        => Path.GetFullPath(Path.Combine(
            new[] { AppContext.BaseDirectory, "..", "..", "..", "..", ".." }.Concat(segments).ToArray()));

    private static async Task<SupplierPaymentInvoiceAllocationDto> CreateAllocationAsync(
        SupplierPaymentInvoiceAllocationController controller, long paymentId, long invoiceId,
        decimal amount, string remark = "")
        => AssertOk<SupplierPaymentInvoiceAllocationDto>(
            await controller.Create(AllocateDto(paymentId, invoiceId, amount, remark)));

    // ==================== 1. 登记：部分 / 全额引用、快照与登记人 ====================

    [Fact]
    public async Task 部分引用两张发票_保留服务端快照与登记人且不改写付款单与发票()
    {
        using var db = TestDbFactory.Create();
        var supplier = SeedSupplier(db, "S001", "义乌档口");
        var payment = SeedPayment(db, "FK20260910", supplier.Id, 1000m);
        var invoiceA = SeedInvoice(db, "INV-001", supplier.Id, 400m, code: "045001");
        var invoiceB = SeedInvoice(db, "INV-002", supplier.Id, 600m, invoiceType: "专票", code: "045002");
        var controller = BuildController(db, "张三");

        var first = await CreateAllocationAsync(controller, payment.Id, invoiceA.Id, 300m, "第一笔");
        var second = await CreateAllocationAsync(controller, payment.Id, invoiceB.Id, 200m);

        // 服务端快照：付款单 / 供应商 / 发票全部来自持久化记录，登记人只取当前账号
        Assert.Equal(payment.Id, first.PaymentId);
        Assert.Equal("FK20260910", first.PaymentNo);
        Assert.Equal(1000m, first.PaymentAmount);
        Assert.Equal("已审核", first.PaymentStatusText);
        Assert.Equal(invoiceA.Id, first.PurchaseInvoiceId);
        Assert.Equal("普票", first.InvoiceType);
        Assert.Equal("045001", first.InvoiceCode);
        Assert.Equal("INV-001", first.InvoiceNumber);
        Assert.Equal("普票 045001-INV-001", first.InvoiceIdentityText);
        Assert.Equal(400m, first.InvoiceGrossAmount);
        Assert.Equal("已登记", first.InvoiceStatusText);
        Assert.Equal("S001", first.SupplierCode);
        Assert.Equal("义乌档口", first.SupplierName);
        Assert.Equal("CNY", first.Currency);
        Assert.Equal("张三", first.RecordedBy);
        Assert.True(first.IsActive);
        Assert.False(first.IsVoided);
        Assert.Equal("第一笔", first.Remark);
        Assert.Equal(SupplierPaymentInvoiceAllocationRules.StatusActive, first.Status);

        Assert.Equal(200m, second.AllocatedAmount);
        Assert.Equal("专票 045002-INV-002", second.InvoiceIdentityText);

        // 不改写付款单与发票
        var paymentAfter = await db.FinancePayments.AsNoTracking().FirstAsync(p => p.Id == payment.Id);
        Assert.Equal(1000m, paymentAfter.Amount);
        Assert.Equal(Currency.CNY, paymentAfter.Currency);
        Assert.Equal(DocumentStatus.Approved, paymentAfter.Status);
        Assert.False(paymentAfter.IsDeleted);
        var invoiceAfter = await db.PurchaseInvoices.AsNoTracking().FirstAsync(i => i.Id == invoiceA.Id);
        Assert.Equal(400m, invoiceAfter.GrossAmount);
        Assert.Equal(PurchaseInvoiceRules.StatusRecorded, invoiceAfter.Status);
        Assert.False(invoiceAfter.IsDeleted);
    }

    [Fact]
    public async Task 全额引用后付款单汇总显示未引用为0且状态为已全额引用()
    {
        using var db = TestDbFactory.Create();
        var supplier = SeedSupplier(db, "S001", "义乌档口");
        var payment = SeedPayment(db, "FK20260910", supplier.Id, 1000m);
        var invoiceA = SeedInvoice(db, "INV-001", supplier.Id, 400m);
        var invoiceB = SeedInvoice(db, "INV-002", supplier.Id, 600m);
        var controller = BuildController(db);

        await CreateAllocationAsync(controller, payment.Id, invoiceA.Id, 400m);
        await CreateAllocationAsync(controller, payment.Id, invoiceB.Id, 600m);

        var summary = AssertOk<SupplierPaymentInvoiceAllocationPaymentSummaryDto>(
            await controller.PaymentSummary(payment.Id));

        Assert.Equal(1000m, summary.InvoiceAllocatedAmount);
        Assert.Equal(0m, summary.UnallocatedAmount);
        Assert.Equal(2, summary.AllocationCount);
        Assert.Equal(0, summary.VoidedCount);
        Assert.Equal(SupplierPaymentInvoiceAllocationRules.LinkageFullyAllocated, summary.LinkageStatus);
        Assert.Contains("已全额引用", summary.LinkageText);
        Assert.Equal(2, summary.Allocations.Count);

        // 发票侧同样显示全额引用（未引用含税总额为 0）
        var invoiceSummary = AssertOk<SupplierPaymentInvoiceAllocationInvoiceSummaryDto>(
            await controller.InvoiceSummary(invoiceB.Id));
        Assert.Equal(600m, invoiceSummary.AllocatedAmount);
        Assert.Equal(0m, invoiceSummary.UnallocatedAmount);
        Assert.Equal(SupplierPaymentInvoiceAllocationRules.LinkageFullyAllocated, invoiceSummary.LinkageStatus);
        Assert.Contains("已全额引用", invoiceSummary.LinkageText);
    }

    [Fact]
    public async Task 未引用付款单汇总为未引用且采购订单维度独立为零()
    {
        using var db = TestDbFactory.Create();
        var supplier = SeedSupplier(db, "S001", "义乌档口");
        var payment = SeedPayment(db, "FK20260910", supplier.Id, 500m);
        var controller = BuildController(db);

        var summary = AssertOk<SupplierPaymentInvoiceAllocationPaymentSummaryDto>(
            await controller.PaymentSummary(payment.Id));

        Assert.Equal(0m, summary.InvoiceAllocatedAmount);
        Assert.Equal(500m, summary.UnallocatedAmount);
        Assert.Equal(0, summary.AllocationCount);
        Assert.Equal(SupplierPaymentInvoiceAllocationRules.LinkageUnallocated, summary.LinkageStatus);
        Assert.Contains("未被引用", summary.LinkageText);
        Assert.Empty(summary.Allocations);
        Assert.Equal(0m, summary.PurchaseOrderAllocatedAmount);
        Assert.Equal(0, summary.PurchaseOrderAllocationCount);
        Assert.Equal(SupplierPaymentInvoiceAllocationRules.SeparateEvidenceText, summary.SeparateEvidenceText);
    }

    [Fact]
    public async Task 供应商或币种不一致的发票被拒绝且绝不换算或改派()
    {
        using var db = TestDbFactory.Create();
        var supplier = SeedSupplier(db, "S001", "义乌档口");
        var otherSupplier = SeedSupplier(db, "S002", "另一家供应商");
        var payment = SeedPayment(db, "FK20260910", supplier.Id, 1000m);
        var otherInvoice = SeedInvoice(db, "INV-OTHER", otherSupplier.Id, 500m);
        var usdInvoice = SeedInvoice(db, "INV-USD", supplier.Id, 500m, currency: "USD");
        var controller = BuildController(db);

        var mismatchSupplier = await AssertBusinessAsync(ErrorCodes.RuleConflict,
            () => controller.Create(AllocateDto(payment.Id, otherInvoice.Id, 100m)));
        Assert.Contains("供应商", mismatchSupplier.Message);

        var mismatchCurrency = await AssertBusinessAsync(ErrorCodes.RuleConflict,
            () => controller.Create(AllocateDto(payment.Id, usdInvoice.Id, 100m)));
        Assert.Contains("币种", mismatchCurrency.Message);

        // 没有任何引用行被写入（拒绝即不落库）
        Assert.Empty(await db.SupplierPaymentInvoiceAllocations.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task 草稿已作废或已删除的发票被拒绝()
    {
        using var db = TestDbFactory.Create();
        var supplier = SeedSupplier(db, "S001", "义乌档口");
        var payment = SeedPayment(db, "FK20260910", supplier.Id, 1000m);
        var draft = SeedInvoice(db, "INV-DRAFT", supplier.Id, 500m, status: PurchaseInvoiceRules.StatusDraft);
        var voided = SeedInvoice(db, "INV-VOID", supplier.Id, 500m, status: PurchaseInvoiceRules.StatusVoided);
        var deleted = SeedInvoice(db, "INV-DEL", supplier.Id, 500m, deleted: true);
        var controller = BuildController(db);

        var draftError = await AssertBusinessAsync(ErrorCodes.RuleConflict,
            () => controller.Create(AllocateDto(payment.Id, draft.Id, 100m)));
        Assert.Contains("草稿", draftError.Message);

        var voidedError = await AssertBusinessAsync(ErrorCodes.RuleConflict,
            () => controller.Create(AllocateDto(payment.Id, voided.Id, 100m)));
        Assert.Contains("已作废", voidedError.Message);

        var deletedError = await AssertBusinessAsync(ErrorCodes.NotFound,
            () => controller.Create(AllocateDto(payment.Id, deleted.Id, 100m)));
        Assert.Contains("不存在或已删除", deletedError.Message);

        Assert.Empty(await db.SupplierPaymentInvoiceAllocations.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task 已删除或不存在的付款单被拒绝()
    {
        using var db = TestDbFactory.Create();
        var supplier = SeedSupplier(db, "S001", "义乌档口");
        var payment = SeedPayment(db, "FK20260910", supplier.Id, 1000m, deleted: true);
        var invoice = SeedInvoice(db, "INV-001", supplier.Id, 500m);
        var controller = BuildController(db);

        await AssertBusinessAsync(ErrorCodes.NotFound,
            () => controller.Create(AllocateDto(payment.Id, invoice.Id, 100m)));
        await AssertBusinessAsync(ErrorCodes.NotFound,
            () => controller.Create(AllocateDto(999_999, invoice.Id, 100m)));

        Assert.Empty(await db.SupplierPaymentInvoiceAllocations.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task 引用金额合计不得超过付款单金额并给出已引用与本次金额()
    {
        using var db = TestDbFactory.Create();
        var supplier = SeedSupplier(db, "S001", "义乌档口");
        var payment = SeedPayment(db, "FK20260910", supplier.Id, 1000m);
        var invoiceA = SeedInvoice(db, "INV-001", supplier.Id, 800m);
        var invoiceB = SeedInvoice(db, "INV-002", supplier.Id, 800m);
        var controller = BuildController(db);

        await CreateAllocationAsync(controller, payment.Id, invoiceA.Id, 700m);

        var error = await AssertBusinessAsync(ErrorCodes.RuleConflict,
            () => controller.Create(AllocateDto(payment.Id, invoiceB.Id, 400m)));
        Assert.Contains("超过付款单金额", error.Message);
        Assert.Contains("已引用 700", error.Message);

        // 上限内可以继续登记（部分引用允许，未被引用金额保留）
        var ok = await CreateAllocationAsync(controller, payment.Id, invoiceB.Id, 300m);
        Assert.Equal(300m, ok.AllocatedAmount);

        var summary = AssertOk<SupplierPaymentInvoiceAllocationPaymentSummaryDto>(
            await controller.PaymentSummary(payment.Id));
        Assert.Equal(1000m, summary.InvoiceAllocatedAmount);
        Assert.Equal(0m, summary.UnallocatedAmount);
    }

    [Fact]
    public async Task 引用金额合计不得超过发票未引用含税总额()
    {
        using var db = TestDbFactory.Create();
        var supplier = SeedSupplier(db, "S001", "义乌档口");
        var paymentA = SeedPayment(db, "FK-A", supplier.Id, 1000m);
        var paymentB = SeedPayment(db, "FK-B", supplier.Id, 1000m);
        var invoice = SeedInvoice(db, "INV-001", supplier.Id, 500m);
        var controller = BuildController(db);

        // 另一张付款单先占用 400（跨付款单共享同一发票上限）
        await CreateAllocationAsync(controller, paymentA.Id, invoice.Id, 400m);

        var error = await AssertBusinessAsync(ErrorCodes.RuleConflict,
            () => controller.Create(AllocateDto(paymentB.Id, invoice.Id, 200m)));
        Assert.Contains("超过含税总额", error.Message);
        Assert.Contains("已引用 400", error.Message);

        var ok = await CreateAllocationAsync(controller, paymentB.Id, invoice.Id, 100m);
        Assert.Equal(100m, ok.AllocatedAmount);

        var invoiceSummary = AssertOk<SupplierPaymentInvoiceAllocationInvoiceSummaryDto>(
            await controller.InvoiceSummary(invoice.Id));
        Assert.Equal(500m, invoiceSummary.AllocatedAmount);
        Assert.Equal(0m, invoiceSummary.UnallocatedAmount);
        Assert.Equal(2, invoiceSummary.AllocationCount);
    }

    [Fact]
    public async Task 同一发票同一付款单重复有效引用被拒绝_作废后可重新登记()
    {
        using var db = TestDbFactory.Create();
        var supplier = SeedSupplier(db, "S001", "义乌档口");
        var payment = SeedPayment(db, "FK20260910", supplier.Id, 1000m);
        var invoice = SeedInvoice(db, "INV-001", supplier.Id, 800m);
        var controller = BuildController(db);

        var first = await CreateAllocationAsync(controller, payment.Id, invoice.Id, 300m);

        var duplicate = await AssertBusinessAsync(ErrorCodes.Duplicate,
            () => controller.Create(AllocateDto(payment.Id, invoice.Id, 100m)));
        Assert.Contains("只能有一条有效引用行", duplicate.Message);

        var voided = AssertOk<SupplierPaymentInvoiceAllocationDto>(
            await controller.Void(first.Id, new SupplierPaymentInvoiceAllocationVoidRequest { Reason = "录错" }));
        Assert.True(voided.IsVoided);
        Assert.Equal("录错", voided.VoidReason);
        Assert.NotNull(voided.VoidedAt);

        // 作废后额度释放，可重新登记同一发票
        var again = await CreateAllocationAsync(controller, payment.Id, invoice.Id, 150m);
        Assert.NotEqual(first.Id, again.Id);

        var rows = await db.SupplierPaymentInvoiceAllocations.AsNoTracking()
            .Where(a => a.PaymentId == payment.Id && a.PurchaseInvoiceId == invoice.Id)
            .ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Single(rows, r => r.Status == SupplierPaymentInvoiceAllocationRules.StatusActive);
        Assert.Single(rows, r => r.Status == SupplierPaymentInvoiceAllocationRules.StatusVoided);

        // 已作废行不占用额度：付款单汇总只计有效行
        var summary = AssertOk<SupplierPaymentInvoiceAllocationPaymentSummaryDto>(
            await controller.PaymentSummary(payment.Id));
        Assert.Equal(150m, summary.InvoiceAllocatedAmount);
        Assert.Equal(1, summary.AllocationCount);
        Assert.Equal(1, summary.VoidedCount);
    }

    [Fact]
    public async Task 重复作废被拒绝且历史保留()
    {
        using var db = TestDbFactory.Create();
        var supplier = SeedSupplier(db, "S001", "义乌档口");
        var payment = SeedPayment(db, "FK20260910", supplier.Id, 1000m);
        var invoice = SeedInvoice(db, "INV-001", supplier.Id, 800m);
        var controller = BuildController(db);

        var row = await CreateAllocationAsync(controller, payment.Id, invoice.Id, 300m);
        await controller.Void(row.Id, new SupplierPaymentInvoiceAllocationVoidRequest { Reason = "第一次更正" });

        var error = await AssertBusinessAsync(ErrorCodes.RuleConflict,
            () => controller.Void(row.Id, new SupplierPaymentInvoiceAllocationVoidRequest { Reason = "第二次" }));
        Assert.Contains("重复作废", error.Message);

        // 原始值、快照与登记人保留可读
        var detail = AssertOk<SupplierPaymentInvoiceAllocationDto>(await controller.GetById(row.Id));
        Assert.Equal(300m, detail.AllocatedAmount);
        Assert.Equal("INV-001", detail.InvoiceNumber);
        Assert.Equal("第一次更正", detail.VoidReason);
    }

    [Fact]
    public async Task 引用金额必须为正且按币种精度取整_未知币种付款单拒绝登记()
    {
        using var db = TestDbFactory.Create();
        var supplier = SeedSupplier(db, "S001", "义乌档口");
        var payment = SeedPayment(db, "FK-CNY", supplier.Id, 1000m);
        var jpyPayment = SeedPayment(db, "FK-JPY", supplier.Id, 1000m, currency: Currency.JPY);
        var invalidPayment = SeedPayment(db, "FK-BAD", supplier.Id, 1000m, currency: (Currency)999);
        var invoice = SeedInvoice(db, "INV-001", supplier.Id, 800m);
        var jpyInvoice = SeedInvoice(db, "INV-JPY", supplier.Id, 800m, currency: "JPY");
        var controller = BuildController(db);

        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => controller.Create(AllocateDto(payment.Id, invoice.Id, 0m)));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => controller.Create(AllocateDto(payment.Id, invoice.Id, 0.004m)));

        // JPY 无小数：100.6 → 101（0.5 进位，与 CurrencyAmountRules 口径一致）
        var jpy = await CreateAllocationAsync(controller, jpyPayment.Id, jpyInvoice.Id, 100.6m);
        Assert.Equal(101m, jpy.AllocatedAmount);
        Assert.Equal(0, jpy.AmountDecimals);
        Assert.Equal("JPY", jpy.Currency);

        // 付款单币种不在系统口径内：拒绝登记（不做汇率换算）
        var error = await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => controller.Create(AllocateDto(invalidPayment.Id, invoice.Id, 10m)));
        Assert.Contains("不受支持", error.Message);
    }

    [Fact]
    public async Task 备注与作废原因有界且必填()
    {
        using var db = TestDbFactory.Create();
        var supplier = SeedSupplier(db, "S001", "义乌档口");
        var payment = SeedPayment(db, "FK20260910", supplier.Id, 1000m);
        var invoice = SeedInvoice(db, "INV-001", supplier.Id, 800m);
        var controller = BuildController(db);

        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => controller.Create(AllocateDto(payment.Id, invoice.Id, 10m, new string('备', 501))));

        var row = await CreateAllocationAsync(controller, payment.Id, invoice.Id, 10m, new string('备', 500));
        Assert.Equal(500, row.Remark.Length);

        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => controller.Void(row.Id, new SupplierPaymentInvoiceAllocationVoidRequest { Reason = "   " }));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => controller.Void(row.Id, new SupplierPaymentInvoiceAllocationVoidRequest
            {
                Reason = new string('因', 501)
            }));
    }

    [Fact]
    public async Task 客户端不可提交快照余额与登记人字段()
    {
        var names = typeof(SupplierPaymentInvoiceAllocationSaveDto)
            .GetProperties().Select(p => p.Name).ToList();

        // 只有「付款单 Id + 发票 Id + 引用金额 + 备注」四个字段
        Assert.Equal(4, names.Count);
        Assert.Contains(nameof(SupplierPaymentInvoiceAllocationSaveDto.PaymentId), names);
        Assert.Contains(nameof(SupplierPaymentInvoiceAllocationSaveDto.PurchaseInvoiceId), names);
        Assert.Contains(nameof(SupplierPaymentInvoiceAllocationSaveDto.AllocatedAmount), names);
        Assert.Contains(nameof(SupplierPaymentInvoiceAllocationSaveDto.Remark), names);
        Assert.DoesNotContain("RecordedBy", names);
        Assert.DoesNotContain("AllocatedAt", names);
        Assert.DoesNotContain("Status", names);
        Assert.DoesNotContain("PaymentAmount", names);
        Assert.DoesNotContain("InvoiceGrossAmount", names);
        Assert.DoesNotContain("SupplierId", names);
        Assert.DoesNotContain("Currency", names);

        using var db = TestDbFactory.Create();
        var supplier = SeedSupplier(db, "S001", "义乌档口");
        var payment = SeedPayment(db, "FK20260910", supplier.Id, 1000m, status: DocumentStatus.Pending);
        var invoice = SeedInvoice(db, "INV-001", supplier.Id, 800m);
        var controller = BuildController(db);

        // 未提供登录账号时登记人记为「未知用户」（绝不臆造账号）；付款单状态 / 金额同样是服务端快照
        var row = await CreateAllocationAsync(controller, payment.Id, invoice.Id, 10m);
        Assert.Equal(SupplierPaymentInvoiceAllocationRules.UnknownUserText, row.RecordedBy);
        Assert.Equal((int)DocumentStatus.Pending, row.PaymentStatus);
        Assert.Equal("待提交", row.PaymentStatusText);
        Assert.Equal(1000m, row.PaymentAmount);
        Assert.Equal(800m, row.InvoiceGrossAmount);

        // 登记人超长由服务端截断（不接受客户端提交，也不会因此失败）
        var invoice2 = SeedInvoice(db, "INV-002", supplier.Id, 800m);
        var longName = new string('人', 150);
        var withUser = await CreateAllocationAsync(BuildController(db, longName), payment.Id, invoice2.Id, 11m);
        Assert.Equal(SupplierPaymentInvoiceAllocationRules.MaxRecordedByLength, withUser.RecordedBy.Length);
    }

    [Fact]
    public async Task 付款单或发票软删除与作废后历史引用仍可读并显式标注()
    {
        using var db = TestDbFactory.Create();
        var supplier = SeedSupplier(db, "S001", "义乌档口");
        var payment = SeedPayment(db, "FK20260910", supplier.Id, 1000m);
        var deletedInvoice = SeedInvoice(db, "INV-001", supplier.Id, 800m);
        var voidedInvoice = SeedInvoice(db, "INV-002", supplier.Id, 500m);
        var controller = BuildController(db);

        var row = await CreateAllocationAsync(controller, payment.Id, deletedInvoice.Id, 300m);
        var row2 = await CreateAllocationAsync(controller, payment.Id, voidedInvoice.Id, 200m);

        // 软删除付款单与第一张发票；把第二张发票改为已作废（历史证据绝不回填或改派）
        (await db.FinancePayments.FirstAsync(p => p.Id == payment.Id)).IsDeleted = true;
        (await db.PurchaseInvoices.FirstAsync(i => i.Id == deletedInvoice.Id)).IsDeleted = true;
        (await db.PurchaseInvoices.FirstAsync(i => i.Id == voidedInvoice.Id)).Status =
            PurchaseInvoiceRules.StatusVoided;
        await db.SaveChangesAsync();

        var detail = AssertOk<SupplierPaymentInvoiceAllocationDto>(await controller.GetById(row.Id));
        Assert.False(detail.PaymentAvailable);
        Assert.False(detail.InvoiceAvailable);
        Assert.Contains("已删除", detail.PaymentAvailabilityText);
        Assert.Contains("已删除", detail.InvoiceAvailabilityText);
        Assert.Equal(300m, detail.AllocatedAmount);
        Assert.Equal("INV-001", detail.InvoiceNumber);

        var detail2 = AssertOk<SupplierPaymentInvoiceAllocationDto>(await controller.GetById(row2.Id));
        Assert.False(detail2.InvoiceAvailable);
        Assert.Contains("已作废", detail2.InvoiceAvailabilityText);
        Assert.Equal(200m, detail2.AllocatedAmount);

        // 台账默认仍包含这些历史行（证据保留可读）
        var page = AssertOk<PagedResult<SupplierPaymentInvoiceAllocationDto>>(
            await controller.GetPaged(new SupplierPaymentInvoiceAllocationQuery()));
        Assert.Equal(2, page.Total);
    }

    [Fact]
    public async Task 发票侧汇总显示已引用与未引用含税总额且作废历史单独计数()
    {
        using var db = TestDbFactory.Create();
        var supplier = SeedSupplier(db, "S001", "义乌档口");
        var paymentA = SeedPayment(db, "FK-A", supplier.Id, 1000m);
        var paymentB = SeedPayment(db, "FK-B", supplier.Id, 1000m);
        var invoice = SeedInvoice(db, "INV-001", supplier.Id, 1000m);
        var controller = BuildController(db);

        await CreateAllocationAsync(controller, paymentA.Id, invoice.Id, 300m);
        var voidedRow = await CreateAllocationAsync(controller, paymentB.Id, invoice.Id, 200m);
        await controller.Void(voidedRow.Id,
            new SupplierPaymentInvoiceAllocationVoidRequest { Reason = "改按另一张发票" });

        var summary = AssertOk<SupplierPaymentInvoiceAllocationInvoiceSummaryDto>(
            await controller.InvoiceSummary(invoice.Id));

        Assert.Equal(1000m, summary.InvoiceGrossAmount);
        Assert.Equal(300m, summary.AllocatedAmount);
        Assert.Equal(700m, summary.UnallocatedAmount);
        Assert.Equal(1, summary.AllocationCount);
        Assert.Equal(1, summary.VoidedCount);
        Assert.Equal(SupplierPaymentInvoiceAllocationRules.LinkagePartial, summary.LinkageStatus);
        Assert.Contains("未引用 700", summary.LinkageText);
        // 已作废历史单独列出但永不并入有效合计
        Assert.Equal(2, summary.Allocations.Count);
        Assert.Single(summary.Allocations, a => a.IsVoided);
        Assert.True(summary.InvoiceAvailable);
        Assert.Contains("已登记", summary.InvoiceAvailabilityText);
        Assert.Equal(SupplierPaymentInvoiceAllocationRules.RuleText, summary.RuleText);
        Assert.Equal(SupplierPaymentInvoiceAllocationRules.BoundaryText, summary.BoundaryText);

        // 发票并不存在 → 数据不存在
        await AssertBusinessAsync(ErrorCodes.NotFound, () => controller.InvoiceSummary(999_999));
    }

    [Fact]
    public async Task 台账按状态币种发票与关键字过滤_分页有界且默认分页上限截断()
    {
        using var db = TestDbFactory.Create();
        var supplier = SeedSupplier(db, "S001", "义乌档口");
        var payment = SeedPayment(db, "FK20260910", supplier.Id, 10_000m);
        var controller = BuildController(db);

        var invoices = new List<PurchaseInvoice>();
        for (var i = 1; i <= 5; i++)
            invoices.Add(SeedInvoice(db, $"INV-{i:0000}", supplier.Id, 100m, code: "045" + i));

        foreach (var invoice in invoices)
            await CreateAllocationAsync(controller, payment.Id, invoice.Id, 10m);

        var rowToVoid = (await db.SupplierPaymentInvoiceAllocations.AsNoTracking()
            .FirstAsync(a => a.PurchaseInvoiceId == invoices[0].Id)).Id;
        await controller.Void(rowToVoid, new SupplierPaymentInvoiceAllocationVoidRequest { Reason = "更正" });

        // 默认分页（默认 50 / 上限 200）；status 过滤
        var active = AssertOk<PagedResult<SupplierPaymentInvoiceAllocationDto>>(
            await controller.GetPaged(new SupplierPaymentInvoiceAllocationQuery
            {
                Status = SupplierPaymentInvoiceAllocationRules.StatusActive
            }));
        Assert.Equal(4, active.Total);
        Assert.All(active.Items, i => Assert.True(i.IsActive));

        var voided = AssertOk<PagedResult<SupplierPaymentInvoiceAllocationDto>>(
            await controller.GetPaged(new SupplierPaymentInvoiceAllocationQuery
            {
                Status = SupplierPaymentInvoiceAllocationRules.StatusVoided
            }));
        Assert.Equal(1, voided.Total);
        Assert.True(voided.Items[0].IsVoided);

        // 发票 / 供应商 / 币种 / 关键字过滤
        var byInvoice = AssertOk<PagedResult<SupplierPaymentInvoiceAllocationDto>>(
            await controller.GetPaged(new SupplierPaymentInvoiceAllocationQuery
            {
                PurchaseInvoiceId = invoices[1].Id
            }));
        Assert.Equal(1, byInvoice.Total);
        Assert.Equal("INV-0002", byInvoice.Items[0].InvoiceNumber);

        var bySupplier = AssertOk<PagedResult<SupplierPaymentInvoiceAllocationDto>>(
            await controller.GetPaged(new SupplierPaymentInvoiceAllocationQuery { SupplierId = supplier.Id }));
        Assert.Equal(5, bySupplier.Total);

        var byCurrency = AssertOk<PagedResult<SupplierPaymentInvoiceAllocationDto>>(
            await controller.GetPaged(new SupplierPaymentInvoiceAllocationQuery { Currency = "USD" }));
        Assert.Equal(0, byCurrency.Total);

        var byKeyword = AssertOk<PagedResult<SupplierPaymentInvoiceAllocationDto>>(
            await controller.GetPaged(new SupplierPaymentInvoiceAllocationQuery { Keyword = "INV-0003" }));
        Assert.Equal(1, byKeyword.Total);
        Assert.Equal("INV-0003", byKeyword.Items[0].InvoiceNumber);

        // 分页有界：每页 2 条；页大小超上限按上限截断
        var paged = AssertOk<PagedResult<SupplierPaymentInvoiceAllocationDto>>(
            await controller.GetPaged(new SupplierPaymentInvoiceAllocationQuery { PageSize = 2 }));
        Assert.Equal(2, paged.Items.Count);
        Assert.Equal(5, paged.Total);

        var clamped = AssertOk<PagedResult<SupplierPaymentInvoiceAllocationDto>>(
            await controller.GetPaged(new SupplierPaymentInvoiceAllocationQuery { PageSize = 9999 }));
        Assert.Equal(SupplierPaymentInvoiceAllocationQuery.MaxPageSize, clamped.PageSize);

        // 未知筛选值一律拒绝（不静默忽略筛选条件）
        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => controller.GetPaged(new SupplierPaymentInvoiceAllocationQuery { Keyword = new string('K', 101) }));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => controller.GetPaged(new SupplierPaymentInvoiceAllocationQuery { Status = 9 }));

        // 单付款单 / 单发票清单同样有界
        var forPayment = AssertOk<List<SupplierPaymentInvoiceAllocationDto>>(
            await controller.AllocationsForPayment(payment.Id, null, 2));
        Assert.Equal(2, forPayment.Count);
        var activeOnly = AssertOk<List<SupplierPaymentInvoiceAllocationDto>>(
            await controller.AllocationsForPayment(payment.Id, SupplierPaymentInvoiceAllocationRules.StatusActive, 50));
        Assert.Equal(4, activeOnly.Count);
        var forInvoice = AssertOk<List<SupplierPaymentInvoiceAllocationDto>>(
            await controller.AllocationsForInvoice(invoices[1].Id));
        Assert.Single(forInvoice);
        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => controller.AllocationsForPayment(0));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => controller.AllocationsForInvoice(-1));
    }

    [Fact]
    public async Task 付款单候选只列未删除付款单并派生已引用未引用与资格()
    {
        using var db = TestDbFactory.Create();
        var supplier = SeedSupplier(db, "S001", "义乌档口");
        var otherSupplier = SeedSupplier(db, "S002", "另一家");
        var paymentA = SeedPayment(db, "FK-A", supplier.Id, 1000m);
        var paymentB = SeedPayment(db, "FK-B", supplier.Id, 500m);
        var deletedPayment = SeedPayment(db, "FK-DEL", supplier.Id, 300m, deleted: true);
        var otherPayment = SeedPayment(db, "FK-OTHER", otherSupplier.Id, 800m);
        var seller = SeedSupplier(db, "S900", "停用供应商", status: 0);
        var stoppedPayment = SeedPayment(db, "FK-STOP", seller.Id, 400m);
        var invoice = SeedInvoice(db, "INV-001", supplier.Id, 800m);
        var controller = BuildController(db);

        await CreateAllocationAsync(controller, paymentA.Id, invoice.Id, 400m);
        await CreateAllocationAsync(controller, paymentB.Id, invoice.Id, 400m);

        var candidates = AssertOk<List<SupplierPaymentInvoiceAllocationPaymentCandidateDto>>(
            await controller.PaymentCandidates(supplier.Id, null));

        // 已删除付款单不出现
        Assert.DoesNotContain(candidates, c => c.PaymentId == deletedPayment.Id);
        Assert.DoesNotContain(candidates, c => c.PaymentId == otherPayment.Id);
        Assert.Equal(2, candidates.Count);

        var candidateA = candidates.Single(c => c.PaymentId == paymentA.Id);
        Assert.Equal(1000m, candidateA.PaymentAmount);
        Assert.Equal(400m, candidateA.AllocatedAmount);
        Assert.Equal(600m, candidateA.UnallocatedAmount);
        Assert.Equal(1, candidateA.AllocationCount);
        Assert.True(candidateA.Eligible);
        Assert.Contains("剩余可引用 600", candidateA.EligibilityText);
        Assert.Contains("供应商可用", candidateA.EligibilityText);

        // 部分引用的付款单仍可继续引用（未引用金额保留）
        var candidateB = candidates.Single(c => c.PaymentId == paymentB.Id);
        Assert.Equal(100m, candidateB.UnallocatedAmount);
        Assert.True(candidateB.Eligible);

        // 供应商停用只作只读说明，不影响付款单候选可得性
        var stopped = AssertOk<List<SupplierPaymentInvoiceAllocationPaymentCandidateDto>>(
            await controller.PaymentCandidates(seller.Id, null));
        Assert.Single(stopped);
        Assert.Contains("停用", stopped[0].EligibilityText);

        // 关键字按付款单号检索；无匹配返回空
        var byKeyword = AssertOk<List<SupplierPaymentInvoiceAllocationPaymentCandidateDto>>(
            await controller.PaymentCandidates(null, "FK-A"));
        Assert.Single(byKeyword);
        Assert.Equal(paymentA.Id, byKeyword[0].PaymentId);

        var empty = AssertOk<List<SupplierPaymentInvoiceAllocationPaymentCandidateDto>>(
            await controller.PaymentCandidates(null, "NOT-EXIST"));
        Assert.Empty(empty);
    }

    [Fact]
    public async Task 发票候选只列同供应商同币种并派生其他付款单已引用与剩余未引用()
    {
        using var db = TestDbFactory.Create();
        var supplier = SeedSupplier(db, "S001", "义乌档口");
        var otherSupplier = SeedSupplier(db, "S002", "另一家");
        var paymentA = SeedPayment(db, "FK-A", supplier.Id, 1000m);
        var paymentB = SeedPayment(db, "FK-B", supplier.Id, 1000m);
        var recorded = SeedInvoice(db, "INV-001", supplier.Id, 500m);
        var draft = SeedInvoice(db, "INV-DRAFT", supplier.Id, 500m, status: PurchaseInvoiceRules.StatusDraft);
        var voided = SeedInvoice(db, "INV-VOID", supplier.Id, 500m, status: PurchaseInvoiceRules.StatusVoided);
        SeedInvoice(db, "INV-USD", supplier.Id, 500m, currency: "USD");
        SeedInvoice(db, "INV-OTHER", otherSupplier.Id, 500m);
        SeedInvoice(db, "INV-DEL", supplier.Id, 500m, deleted: true);
        var controller = BuildController(db);

        // 本付款单已引用 200，另一张付款单已引用 100
        await CreateAllocationAsync(controller, paymentA.Id, recorded.Id, 200m);
        await CreateAllocationAsync(controller, paymentB.Id, recorded.Id, 100m);

        var candidates = AssertOk<List<SupplierPaymentInvoiceAllocationInvoiceCandidateDto>>(
            await controller.InvoiceCandidates(paymentA.Id, null));

        // 只列同供应商 + 同币种的未删除发票（含草稿 / 已作废并标注不可引用）
        Assert.Equal(3, candidates.Count);
        Assert.DoesNotContain(candidates, c => c.InvoiceNumber == "INV-USD");
        Assert.DoesNotContain(candidates, c => c.InvoiceNumber == "INV-OTHER");
        Assert.DoesNotContain(candidates, c => c.InvoiceNumber == "INV-DEL");

        var recordedCandidate = candidates.Single(c => c.PurchaseInvoiceId == recorded.Id);
        Assert.Equal(500m, recordedCandidate.InvoiceGrossAmount);
        Assert.Equal(200m, recordedCandidate.AllocatedByThisPayment);
        Assert.Equal(100m, recordedCandidate.AllocatedByOtherPayments);
        Assert.Equal(200m, recordedCandidate.RemainingUnallocatedAmount);
        Assert.True(recordedCandidate.Eligible);

        var draftCandidate = candidates.Single(c => c.PurchaseInvoiceId == draft.Id);
        Assert.False(draftCandidate.Eligible);
        Assert.Contains("草稿", draftCandidate.EligibilityText);
        Assert.False(draftCandidate.Voided);

        var voidedCandidate = candidates.Single(c => c.PurchaseInvoiceId == voided.Id);
        Assert.False(voidedCandidate.Eligible);
        Assert.True(voidedCandidate.Voided);
        Assert.Contains("已作废", voidedCandidate.EligibilityText);

        // 再经第三张付款单引用剩余 200（本付款单 200 + 其他付款单 300 = 含税总额 500）后候选不可引用
        var paymentC = SeedPayment(db, "FK-C", supplier.Id, 1000m);
        await CreateAllocationAsync(controller, paymentC.Id, recorded.Id, 200m);
        var occupied = AssertOk<List<SupplierPaymentInvoiceAllocationInvoiceCandidateDto>>(
            await controller.InvoiceCandidates(paymentA.Id, "INV-001"));
        Assert.Single(occupied);
        var filled = occupied[0];
        Assert.Equal(recorded.Id, filled.PurchaseInvoiceId);
        Assert.Equal(200m, filled.AllocatedByThisPayment);
        Assert.Equal(300m, filled.AllocatedByOtherPayments);
        Assert.Equal(0m, filled.RemainingUnallocatedAmount);
        Assert.False(filled.Eligible);
        Assert.Contains("未引用含税总额为 0", filled.EligibilityText);
    }

    [Fact]
    public async Task 登记与作废不改写付款单发票订单库存退税费用与供应商记录()
    {
        using var db = TestDbFactory.Create();
        var supplier = SeedSupplier(db, "S001", "义乌档口");
        var payment = SeedPayment(db, "FK20260910", supplier.Id, 1000m);
        var invoice = SeedInvoice(db, "INV-001", supplier.Id, 800m, code: "045001");
        var order = SeedOrder(db, "PO20260901", supplier.Id, totalAmount: 900m);
        db.PurchaseInvoiceAllocations.Add(new PurchaseInvoiceAllocation
        {
            PurchaseInvoiceId = invoice.Id,
            PurchaseOrderId = order.Id,
            OrderNo = order.OrderNo,
            OrderDate = order.OrderDate,
            OrderCurrency = "CNY",
            SupplierId = supplier.Id,
            SupplierCode = supplier.SupplierCode,
            SupplierName = supplier.SupplierName,
            AllocatedAmount = 500m,
            Currency = "CNY",
            SortOrder = 1
        });
        await db.SaveChangesAsync();

        var controller = BuildController(db);
        var row = await CreateAllocationAsync(controller, payment.Id, invoice.Id, 300m);
        await controller.Void(row.Id, new SupplierPaymentInvoiceAllocationVoidRequest { Reason = "更正" });

        // 付款单 / 供应商 / 发票 / 采购订单：字段与状态完全不变
        var paymentAfter = await db.FinancePayments.AsNoTracking().FirstAsync(p => p.Id == payment.Id);
        Assert.Equal(1000m, paymentAfter.Amount);
        Assert.Equal(Currency.CNY, paymentAfter.Currency);
        Assert.Equal(PaymentMethod.BankTransfer, paymentAfter.PaymentMethod);
        Assert.Equal(DocumentStatus.Approved, paymentAfter.Status);
        Assert.Equal("FK20260910", paymentAfter.PaymentNo);

        var supplierAfter = await db.BaseSuppliers.AsNoTracking().FirstAsync(s => s.Id == supplier.Id);
        Assert.Equal("S001", supplierAfter.SupplierCode);
        Assert.Equal("义乌档口", supplierAfter.SupplierName);
        Assert.Equal(1, supplierAfter.Status);
        Assert.False(supplierAfter.IsDeleted);

        var invoiceAfter = await db.PurchaseInvoices.AsNoTracking().FirstAsync(i => i.Id == invoice.Id);
        Assert.Equal(800m, invoiceAfter.GrossAmount);
        Assert.Equal("045001", invoiceAfter.InvoiceCode);
        Assert.Equal(PurchaseInvoiceRules.StatusRecorded, invoiceAfter.Status);
        Assert.Null(invoiceAfter.RecordedAt);
        Assert.Null(invoiceAfter.VoidedAt);
        Assert.False(invoiceAfter.IsDeleted);

        var orderAfter = await db.PurchaseOrders.AsNoTracking().FirstAsync(o => o.Id == order.Id);
        Assert.Equal(900m, orderAfter.TotalAmount);
        Assert.Equal(DocumentStatus.Approved, orderAfter.Status);
        Assert.Equal("未到货", orderAfter.ArrivalProgress);
        Assert.Equal("未结算", orderAfter.SettlementProgress);

        // ERP-043 发票 → 采购订单 关联行保持不变
        var invoiceLink = await db.PurchaseInvoiceAllocations.AsNoTracking()
            .SingleAsync(a => a.PurchaseInvoiceId == invoice.Id);
        Assert.Equal(500m, invoiceLink.AllocatedAmount);
        Assert.False(invoiceLink.IsDeleted);

        // 库存 / 库存流水 / 退税 / 费用 / ERP-049 采购订单引用行：本任务不产生任何行
        Assert.Equal(0, await db.Stocks.AsNoTracking().CountAsync());
        Assert.Equal(0, await db.StockMovements.AsNoTracking().CountAsync());
        Assert.Equal(0, await db.BaseTaxRefunds.AsNoTracking().CountAsync());
        Assert.Equal(0, await db.FinanceExpenses.AsNoTracking().CountAsync());
        Assert.Equal(0, await db.SupplierPaymentAllocations.AsNoTracking().CountAsync());

        // 本册只新增一行（作废保留），不做硬删除
        var rows = await db.SupplierPaymentInvoiceAllocations.AsNoTracking().ToListAsync();
        Assert.Single(rows);
        Assert.Equal(SupplierPaymentInvoiceAllocationRules.StatusVoided, rows[0].Status);
        Assert.False(rows[0].IsDeleted);
        Assert.Equal(300m, rows[0].AllocatedAmount);
    }

    [Fact]
    public async Task 付款发票引用与采购订单引用是独立证据维度_汇总不合并金额()
    {
        using var db = TestDbFactory.Create();
        var supplier = SeedSupplier(db, "S001", "义乌档口");
        var payment = SeedPayment(db, "FK20260910", supplier.Id, 1000m);
        var invoice = SeedInvoice(db, "INV-001", supplier.Id, 800m);
        var order = SeedOrder(db, "PO20260901", supplier.Id, totalAmount: 800m);
        var controller = BuildController(db);

        // ERP-049：付款单 → 采购订单 引用 500（与本册完全独立，本任务不触碰）
        db.SupplierPaymentAllocations.Add(new SupplierPaymentAllocation
        {
            PaymentId = payment.Id,
            PaymentNo = payment.PaymentNo,
            PaymentDate = payment.PaymentDate,
            PaymentStatus = (int)payment.Status,
            PaymentStatusText = "已审核",
            PaymentAmount = 1000m,
            PurchaseOrderId = order.Id,
            OrderNo = order.OrderNo,
            OrderDate = order.OrderDate,
            OrderStatus = (int)order.Status,
            OrderCurrency = "CNY",
            SupplierId = supplier.Id,
            SupplierCode = supplier.SupplierCode,
            SupplierName = supplier.SupplierName,
            AllocatedAmount = 500m,
            Currency = "CNY",
            Status = SupplierPaymentAllocationRules.StatusActive,
            AllocatedAt = DateTime.Now
        });
        await db.SaveChangesAsync();

        // 本册：付款单 → 采购发票 引用 400
        await CreateAllocationAsync(controller, payment.Id, invoice.Id, 400m);

        var summary = AssertOk<SupplierPaymentInvoiceAllocationPaymentSummaryDto>(
            await controller.PaymentSummary(payment.Id));

        // 两个维度分别报告：发票引用 400（未引用 600）、采购订单引用 500（独立对照值）
        Assert.Equal(400m, summary.InvoiceAllocatedAmount);
        Assert.Equal(600m, summary.UnallocatedAmount);
        Assert.Equal(1, summary.AllocationCount);
        Assert.Equal(500m, summary.PurchaseOrderAllocatedAmount);
        Assert.Equal(1, summary.PurchaseOrderAllocationCount);
        Assert.Contains("独立", summary.SeparateEvidenceText);
        Assert.Contains("绝不", summary.SeparateEvidenceText);
        // 绝不把两个维度的金额相加当作付款金额
        Assert.NotEqual(
            summary.InvoiceAllocatedAmount + summary.PurchaseOrderAllocatedAmount, summary.PaymentAmount);

        // ERP-049 引用行不被改写
        var poRow = await db.SupplierPaymentAllocations.AsNoTracking()
            .SingleAsync(a => a.PaymentId == payment.Id);
        Assert.Equal(500m, poRow.AllocatedAmount);
        Assert.Equal(SupplierPaymentAllocationRules.StatusActive, poRow.Status);

        // 作废本册行同样不影响 ERP-049 维度
        var row = await db.SupplierPaymentInvoiceAllocations.AsNoTracking()
            .SingleAsync(a => a.PaymentId == payment.Id);
        await controller.Void(row.Id, new SupplierPaymentInvoiceAllocationVoidRequest { Reason = "改按订单" });
        var afterVoid = AssertOk<SupplierPaymentInvoiceAllocationPaymentSummaryDto>(
            await controller.PaymentSummary(payment.Id));
        Assert.Equal(0m, afterVoid.InvoiceAllocatedAmount);
        Assert.Equal(500m, afterVoid.PurchaseOrderAllocatedAmount);
        Assert.Equal(1, afterVoid.VoidedCount);
    }

    // ==================== 7. 纯规则 / 模型 / 幂等结构 / 前端接线契约 ====================

    [Fact]
    public void 纯规则_状态币种金额精度与文案()
    {
        var rules = typeof(SupplierPaymentInvoiceAllocationRules);

        // 状态机与文案
        Assert.Equal("有效", SupplierPaymentInvoiceAllocationRules.StatusText(
            SupplierPaymentInvoiceAllocationRules.StatusActive));
        Assert.Equal("已作废", SupplierPaymentInvoiceAllocationRules.StatusText(
            SupplierPaymentInvoiceAllocationRules.StatusVoided));
        Assert.Throws<BusinessException>(() => SupplierPaymentInvoiceAllocationRules.StatusText(9));
        Assert.Null(SupplierPaymentInvoiceAllocationRules.NormalizeStatusFilter(null));
        Assert.Equal(2, SupplierPaymentInvoiceAllocationRules.NormalizeStatusFilter(2));
        Assert.Throws<BusinessException>(() => SupplierPaymentInvoiceAllocationRules.NormalizeStatusFilter(9));
        SupplierPaymentInvoiceAllocationRules.EnsureVoidable(
            SupplierPaymentInvoiceAllocationRules.StatusActive, "FK1", "普票 INV-1");
        Assert.Throws<BusinessException>(() => SupplierPaymentInvoiceAllocationRules.EnsureVoidable(
            SupplierPaymentInvoiceAllocationRules.StatusVoided, "FK1", "普票 INV-1"));

        // 币种与金额精度（与 CurrencyAmountRules 同源）
        Assert.Equal("CNY", SupplierPaymentInvoiceAllocationRules.NormalizeCurrencyStrict("cny"));
        Assert.Equal(
            new[] { "CNY", "USD", "EUR", "HKD", "GBP", "JPY" },
            SupplierPaymentInvoiceAllocationRules.SupportedCurrencies);
        Assert.Throws<BusinessException>(() =>
            SupplierPaymentInvoiceAllocationRules.NormalizeCurrencyStrict("KRW"));
        Assert.Equal(100.51m, SupplierPaymentInvoiceAllocationRules.NormalizeAllocationAmount(100.505m, "CNY"));
        Assert.Equal(101m, SupplierPaymentInvoiceAllocationRules.NormalizeAllocationAmount(100.6m, "JPY"));
        Assert.Throws<BusinessException>(() =>
            SupplierPaymentInvoiceAllocationRules.NormalizeAllocationAmount(0.004m, "CNY"));

        // 文本与登记人
        Assert.Equal(SupplierPaymentInvoiceAllocationRules.UnknownUserText,
            SupplierPaymentInvoiceAllocationRules.NormalizeRecordedBy("   "));
        Assert.Equal(new string('人', 100),
            SupplierPaymentInvoiceAllocationRules.NormalizeRecordedBy(new string('人', 150)));
        Assert.Throws<BusinessException>(() => SupplierPaymentInvoiceAllocationRules.NormalizeVoidReason(" "));
        Assert.Throws<BusinessException>(() =>
            SupplierPaymentInvoiceAllocationRules.NormalizeRemark(new string('x', 501)));
        Assert.Throws<BusinessException>(() =>
            SupplierPaymentInvoiceAllocationRules.NormalizeKeyword(new string('x', 101)));

        // 引用状态与文案
        Assert.Equal(SupplierPaymentInvoiceAllocationRules.LinkageUnallocated,
            SupplierPaymentInvoiceAllocationRules.LinkageStatusOf(100m, 0m));
        Assert.Equal(SupplierPaymentInvoiceAllocationRules.LinkagePartial,
            SupplierPaymentInvoiceAllocationRules.LinkageStatusOf(100m, 60m));
        Assert.Equal(SupplierPaymentInvoiceAllocationRules.LinkageFullyAllocated,
            SupplierPaymentInvoiceAllocationRules.LinkageStatusOf(100m, 100m));
        Assert.Contains("未引用 40",
            SupplierPaymentInvoiceAllocationRules.PaymentLinkageText(100m, 60m, 1, "CNY"));
        Assert.Contains("未被引用",
            SupplierPaymentInvoiceAllocationRules.InvoiceLinkageText(100m, 0m, 0, "USD"));
        Assert.Contains("已全额引用",
            SupplierPaymentInvoiceAllocationRules.InvoiceLinkageText(100m, 100m, 2, "USD"));

        // 发票身份（与 PurchaseInvoiceRules.IdentityText 同源）
        Assert.Equal("普票 045001-INV-1",
            SupplierPaymentInvoiceAllocationRules.InvoiceIdentityText("普票", "045001", "INV-1", null));
        Assert.Equal("登记当时快照",
            SupplierPaymentInvoiceAllocationRules.InvoiceIdentityText("普票", "045001", "INV-1", "登记当时快照"));
        Assert.Equal("专票 045002-INV-2",
            SupplierPaymentInvoiceAllocationRules.InvoiceIdentity(new PurchaseInvoice
            {
                InvoiceType = "专票",
                InvoiceCode = "045002",
                InvoiceNumber = "INV-2"
            }));

        // 可用性文案与可选择性
        Assert.Equal("付款单可用",
            SupplierPaymentInvoiceAllocationRules.PaymentAvailabilityText(new FinancePayment()));
        Assert.Contains("已删除", SupplierPaymentInvoiceAllocationRules.PaymentAvailabilityText(null));
        Assert.False(SupplierPaymentInvoiceAllocationRules.IsPaymentSelectable(
            new FinancePayment { IsDeleted = true }));
        Assert.Contains("草稿", SupplierPaymentInvoiceAllocationRules.InvoiceAvailabilityText(
            new PurchaseInvoice { Status = PurchaseInvoiceRules.StatusDraft }));
        Assert.Contains("已作废", SupplierPaymentInvoiceAllocationRules.InvoiceAvailabilityText(
            new PurchaseInvoice { Status = PurchaseInvoiceRules.StatusVoided }));
        Assert.True(SupplierPaymentInvoiceAllocationRules.IsInvoiceSelectable(
            new PurchaseInvoice { Status = PurchaseInvoiceRules.StatusRecorded }));
        Assert.False(SupplierPaymentInvoiceAllocationRules.IsInvoiceSelectable(
            new PurchaseInvoice { Status = PurchaseInvoiceRules.StatusDraft }));

        // 口径文案（接口 / 界面 / 文档同源）
        Assert.Contains("不是银行付款凭证", SupplierPaymentInvoiceAllocationRules.BoundaryText);
        Assert.Contains("引用口径", SupplierPaymentInvoiceAllocationRules.RuleText);
        Assert.Contains("独立", SupplierPaymentInvoiceAllocationRules.SeparateEvidenceText);
        Assert.NotNull(rules.GetField(nameof(SupplierPaymentInvoiceAllocationRules.MaxAllocationsPerInvoice)));
    }

    [Fact]
    public void 模型配置_精度长度过滤唯一索引且刻意不建外键()
    {
        using var db = TestDbFactory.Create();

        var allocation = db.Model.FindEntityType(typeof(SupplierPaymentInvoiceAllocation));
        Assert.NotNull(allocation);
        Assert.Equal(2, allocation!.FindProperty(
            nameof(SupplierPaymentInvoiceAllocation.AllocatedAmount))!.GetScale());
        Assert.Equal(2, allocation.FindProperty(
            nameof(SupplierPaymentInvoiceAllocation.PaymentAmount))!.GetScale());
        Assert.Equal(2, allocation.FindProperty(
            nameof(SupplierPaymentInvoiceAllocation.InvoiceGrossAmount))!.GetScale());
        Assert.Equal(50, allocation.FindProperty(
            nameof(SupplierPaymentInvoiceAllocation.PaymentNo))!.GetMaxLength());
        Assert.Equal(20, allocation.FindProperty(
            nameof(SupplierPaymentInvoiceAllocation.InvoiceType))!.GetMaxLength());
        Assert.Equal(50, allocation.FindProperty(
            nameof(SupplierPaymentInvoiceAllocation.InvoiceCode))!.GetMaxLength());
        Assert.Equal(50, allocation.FindProperty(
            nameof(SupplierPaymentInvoiceAllocation.InvoiceNumber))!.GetMaxLength());
        Assert.Equal(120, allocation.FindProperty(
            nameof(SupplierPaymentInvoiceAllocation.InvoiceIdentityText))!.GetMaxLength());
        Assert.Equal(20, allocation.FindProperty(
            nameof(SupplierPaymentInvoiceAllocation.Currency))!.GetMaxLength());
        Assert.Equal(50, allocation.FindProperty(
            nameof(SupplierPaymentInvoiceAllocation.SupplierCode))!.GetMaxLength());
        Assert.Equal(200, allocation.FindProperty(
            nameof(SupplierPaymentInvoiceAllocation.SupplierName))!.GetMaxLength());
        Assert.Equal(100, allocation.FindProperty(
            nameof(SupplierPaymentInvoiceAllocation.RecordedBy))!.GetMaxLength());
        Assert.Equal(500, allocation.FindProperty(
            nameof(SupplierPaymentInvoiceAllocation.Remark))!.GetMaxLength());
        Assert.Equal(500, allocation.FindProperty(
            nameof(SupplierPaymentInvoiceAllocation.VoidReason))!.GetMaxLength());

        var unique = Assert.Single(allocation.GetIndexes(), i => i.IsUnique);
        Assert.Equal("UX_SupplierPaymentInvoiceAllocations_PaymentInvoice", unique.GetDatabaseName());
        Assert.Equal(2, unique.Properties.Count);
        Assert.Contains("IsDeleted = 0", unique.GetFilter());
        Assert.Contains("Status <> 2", unique.GetFilter());
        Assert.Contains(allocation.GetIndexes(), i =>
            i.GetDatabaseName() == "IX_SupplierPaymentInvoiceAllocations_PaymentId_Status");
        Assert.Contains(allocation.GetIndexes(), i =>
            i.GetDatabaseName() == "IX_SupplierPaymentInvoiceAllocations_PurchaseInvoiceId");
        Assert.Contains(allocation.GetIndexes(), i =>
            i.GetDatabaseName() == "IX_SupplierPaymentInvoiceAllocations_Status_AllocatedAt");

        // 刻意不建任何外键（付款单 / 发票只做软删除；供应商可能被停用 / 改名）
        Assert.Empty(allocation.GetForeignKeys());

        // 付款单与采购发票侧不新增导航属性（既有单据结构与读取口径完全不变）
        var paymentType = db.Model.FindEntityType(typeof(FinancePayment));
        Assert.NotNull(paymentType);
        Assert.DoesNotContain(paymentType!.GetNavigations(),
            n => n.ClrType == typeof(SupplierPaymentInvoiceAllocation));
        var invoiceType = db.Model.FindEntityType(typeof(PurchaseInvoice));
        Assert.NotNull(invoiceType);
        Assert.DoesNotContain(invoiceType!.GetNavigations(),
            n => n.ClrType == typeof(SupplierPaymentInvoiceAllocation));
    }

    [Fact]
    public void Schema_upgrade_幂等建表建索引且不含任何回填或资金语句()
    {
        var script = File.ReadAllText(
            RepoFile("src", "ERP.Infrastructure", "Data", "SchemaUpgrader.cs"));

        Assert.Contains("IF OBJECT_ID('db_owner.SupplierPaymentInvoiceAllocations') IS NULL", script);
        Assert.Contains("CREATE TABLE db_owner.SupplierPaymentInvoiceAllocations", script);
        Assert.Contains("PaymentAmount DECIMAL(18,2) NOT NULL DEFAULT 0", script);
        Assert.Contains("InvoiceGrossAmount DECIMAL(18,2) NOT NULL DEFAULT 0", script);
        Assert.Contains("AllocatedAmount DECIMAL(18,2) NOT NULL DEFAULT 0", script);
        Assert.Contains("RecordedBy NVARCHAR(100) NOT NULL DEFAULT N''", script);
        Assert.Contains("CREATE UNIQUE INDEX UX_SupplierPaymentInvoiceAllocations_PaymentInvoice", script);
        Assert.Contains("WHERE IsDeleted = 0 AND Status <> 2;", script);
        Assert.Contains("CREATE INDEX IX_SupplierPaymentInvoiceAllocations_PurchaseInvoiceId", script);

        // 不建任何外键（付款单 / 发票 / 供应商侧都不被本段引用或改写）
        Assert.DoesNotContain("FK_SupplierPaymentInvoiceAllocations", script);

        var start = script.IndexOf("// 41. 供应商付款 → 采购发票 引用登记", StringComparison.Ordinal);
        Assert.True(start > 0);
        var segment = script[start..];

        // 本段只建本模块一张表：不改既有的付款单 / 采购发票表，也不含任何数据回填语句
        Assert.DoesNotContain("ALTER TABLE db_owner.PurchaseInvoices", segment);
        Assert.DoesNotContain("ALTER TABLE db_owner.FinancePayment", segment);
        Assert.DoesNotContain("UPDATE db_owner", segment);
        Assert.DoesNotContain("INSERT INTO db_owner", segment);
        Assert.DoesNotContain("DELETE FROM db_owner", segment);
    }

    [Fact]
    public void 前端与路由接线契约()
    {
        var index = File.ReadAllText(RepoFile("src", "ERP.Api", "wwwroot", "index.html"));
        Assert.Contains("/js/supplier-payment-invoice-allocations.js", index);

        var docModules = File.ReadAllText(
            RepoFile("src", "ERP.Api", "wwwroot", "js", "modules-doc.js"));
        Assert.Contains("openSupplierPaymentInvoiceAllocationRegister", docModules);

        var financeModules = File.ReadAllText(
            RepoFile("src", "ERP.Api", "wwwroot", "js", "modules-finance.js"));
        Assert.Contains("openSupplierPaymentInvoiceAllocationRegister()", financeModules);

        var js = File.ReadAllText(
            RepoFile("src", "ERP.Api", "wwwroot", "js", "supplier-payment-invoice-allocations.js"));
        Assert.Contains("async function openSupplierPaymentInvoiceAllocationRegister", js);
        Assert.Contains("'/api/supplier-payment-invoice-allocations'", js);
        Assert.Contains("'/api/supplier-payment-invoice-allocations?'", js);
        Assert.Contains("/summary", js);
        Assert.Contains("/invoice-candidates?", js);
        Assert.Contains("/void", js);
        Assert.Contains("spaCreateAllocation", js);
        Assert.Contains("不会执行付款", js);
        Assert.Contains("绝不相加", js);

        var controller = File.ReadAllText(
            RepoFile("src", "ERP.Api", "Controllers", "SupplierPaymentInvoiceAllocationController.cs"));
        Assert.Contains("[Route(\"api/supplier-payment-invoice-allocations\")]", controller);
        Assert.Contains("payments/{paymentId:long}/summary", controller);
        Assert.Contains("payments/{paymentId:long}/allocations", controller);
        Assert.Contains("payments/{paymentId:long}/invoice-candidates", controller);
        Assert.Contains("invoices/{purchaseInvoiceId:long}/summary", controller);
        Assert.Contains("invoices/{purchaseInvoiceId:long}/allocations", controller);
        Assert.Contains("{id:long}/void", controller);
        // 不提供改派与静默替换：控制器刻意没有 PUT / PATCH / DELETE
        Assert.DoesNotContain("HttpPut", controller);
        Assert.DoesNotContain("HttpPatch", controller);
        Assert.DoesNotContain("HttpDelete", controller);
    }

}
