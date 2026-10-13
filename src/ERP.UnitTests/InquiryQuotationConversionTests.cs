using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERP.UnitTests;

public class InquiryQuotationConversionTests
{
    [Fact]
    public async Task Direct_conversion_maps_source_and_recalculates_totals()
    {
        using var db = TestDbFactory.Create();
        var inquiry = Seed(db, DocumentStatus.Approved);

        var result = await Controller(db).ToQuotation(inquiry.Id);

        Assert.IsType<OkObjectResult>(result);
        var quotation = db.Quotations.Include(q => q.Details).Single();
        Assert.Equal(inquiry.Id, quotation.InquiryId);
        Assert.Equal(inquiry.InquiryNo, quotation.InquiryNo);
        Assert.Equal("Acme", quotation.CustomerName);
        Assert.Equal("FOB", quotation.TradeTerms);
        Assert.Equal("NINGBO", quotation.PortOfDestination);
        Assert.Equal(250m, quotation.TotalAmount);
        Assert.Equal(1750m, quotation.TotalAmountCny);
        Assert.Equal(250m, quotation.Details.Single().Amount);
        Assert.Equal(DocumentStatus.Completed, db.Inquiries.Single().Status);
    }

    [Fact]
    public async Task Prefill_does_not_persist_or_change_source_status()
    {
        using var db = TestDbFactory.Create();
        var inquiry = Seed(db, DocumentStatus.Approved);

        Assert.IsType<OkObjectResult>(await Controller(db).QuotationPrefill(inquiry.Id));

        Assert.Empty(db.Quotations);
        Assert.Equal(DocumentStatus.Approved, db.Inquiries.Single().Status);
    }

    [Theory]
    [InlineData(DocumentStatus.Pending)]
    [InlineData(DocumentStatus.Submitted)]
    [InlineData(DocumentStatus.Cancelled)]
    public async Task Invalid_source_state_is_rejected(DocumentStatus status)
    {
        using var db = TestDbFactory.Create();
        var inquiry = Seed(db, status);

        var error = await Assert.ThrowsAsync<BusinessException>(() => Controller(db).ToQuotation(inquiry.Id));

        Assert.Contains("已审核", error.Message);
        Assert.Empty(db.Quotations);
    }

    [Fact]
    public async Task Repeated_conversion_is_rejected_and_keeps_one_quotation()
    {
        using var db = TestDbFactory.Create();
        var inquiry = Seed(db, DocumentStatus.Approved);
        var controller = Controller(db);
        await controller.ToQuotation(inquiry.Id);

        var error = await Assert.ThrowsAsync<BusinessException>(() => controller.ToQuotation(inquiry.Id));

        Assert.Contains("不能重复转换", error.Message);
        Assert.Single(db.Quotations);
    }

    [Fact]
    public void Ui_exposes_prefill_and_direct_actions()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../"));
        var modules = File.ReadAllText(Path.Combine(root, "ERP.Api/wwwroot/js/modules-doc.js"));
        var script = File.ReadAllText(Path.Combine(root, "ERP.Api/wwwroot/js/sales-pi.js"));
        Assert.Contains("onclick: 'inquiryPrefillQuotation'", modules);
        Assert.Contains("onclick: 'inquiryToQuotation'", modules);
        Assert.Contains("/api/inquiries/${id}/quotation-prefill", script);
        Assert.Contains("/api/inquiries/${id}/to-quotation", script);
    }

    [Fact]
    public async Task Unauthenticated_conversion_and_prefill_are_rejected_without_mutation()
    {
        using var db = TestDbFactory.Create();
        var inquiry = Seed(db, DocumentStatus.Approved);
        var controller = Controller(db, null);   // 无身份：fail closed

        await Assert.ThrowsAsync<BusinessException>(() => controller.ToQuotation(inquiry.Id));
        await Assert.ThrowsAsync<BusinessException>(() => controller.QuotationPrefill(inquiry.Id));

        Assert.Empty(db.Quotations);
        Assert.Equal(DocumentStatus.Approved, db.Inquiries.AsNoTracking().Single().Status);
    }

    [Fact]
    public async Task Converted_source_is_frozen_for_cancel_edit_delete_and_reconvert()
    {
        using var db = TestDbFactory.Create();
        var inquiry = Seed(db, DocumentStatus.Approved);
        var controller = Controller(db);
        await controller.ToQuotation(inquiry.Id);

        await Assert.ThrowsAsync<BusinessException>(() => controller.Cancel(inquiry.Id));
        await Assert.ThrowsAsync<BusinessException>(() =>
            controller.Update(inquiry.Id, new Inquiry { CustomerId = inquiry.CustomerId }));
        await Assert.ThrowsAsync<BusinessException>(() => controller.Delete(inquiry.Id));
        await Assert.ThrowsAsync<BusinessException>(() => controller.QuotationPrefill(inquiry.Id));
        await Assert.ThrowsAsync<BusinessException>(() => controller.ToQuotation(inquiry.Id));

        var stored = db.Inquiries.AsNoTracking().Single();
        Assert.Equal(DocumentStatus.Completed, stored.Status);
        Assert.False(stored.IsDeleted);
        Assert.Single(db.Quotations.Where(q => !q.IsDeleted));
    }

    private static InquiryController Controller(ERP.Infrastructure.Data.ErpDbContext db)
    {
        var controller = new InquiryController(db, new DocumentNumberService(db));
        TestAuth.SetUser(controller, TestAuth.SeedPrivilegedUser(db));
        return controller;
    }

    private static InquiryController Controller(ERP.Infrastructure.Data.ErpDbContext db, long? userId)
    {
        var controller = new InquiryController(db, new DocumentNumberService(db));
        TestAuth.SetUser(controller, userId);
        return controller;
    }

    private static Inquiry Seed(ERP.Infrastructure.Data.ErpDbContext db, DocumentStatus status)
    {
        var customer = new BaseCustomer
        {
            CustomerCode = Guid.NewGuid().ToString("N"), CustomerName = "Acme",
            ContactPerson = "Customer Contact", Phone = "123", Email = "sales@acme.test",
            TradeTerms = "FOB", DestinationPort = "NINGBO", PaymentTerms = "T/T"
        };
        db.BaseCustomers.Add(customer);
        db.SaveChanges();
        var inquiry = new Inquiry
        {
            InquiryNo = "INQ-CONVERT", InquiryDate = DateTime.Today, CustomerId = customer.Id,
            ContactPerson = "Inquiry Contact", ContactPhone = "456", Currency = Currency.USD,
            ExchangeRate = 7m, ValidDays = 15, Status = status, Remark = "source remark",
            Details = new List<InquiryDetail>
            {
                new() { ProductId = 1, ProductName = "Widget", Spec = "A", Unit = "PCS",
                    Quantity = 2m, UnitPrice = 125m, Amount = 1m }
            }
        };
        db.Inquiries.Add(inquiry);
        db.SaveChanges();
        return inquiry;
    }

    // ==================== ERP-470 商业条款校验（询价 → 报价转换来源） ====================

    [Fact]
    public async Task 转换_来源条款非法_在占号与改写来源状态之前受控拒绝()
    {
        using var db = TestDbFactory.Create();
        var inquiry = Seed(db, DocumentStatus.Approved);
        var detail = db.InquiryDetails.Single();
        detail.Quantity = 0m;
        db.SaveChanges();

        var error = await Assert.ThrowsAsync<BusinessException>(() => Controller(db).ToQuotation(inquiry.Id));

        Assert.Equal(ErrorCodes.InvalidParameter, error.Code);
        Assert.Contains("数量必须大于 0", error.Message);
        Assert.Empty(db.Quotations);
        Assert.Equal(DocumentStatus.Approved, db.Inquiries.AsNoTracking().Single().Status);

        // 带入预填（只读）同样受控拒绝：不落库、不占号、不改写来源状态。
        var prefill = await Assert.ThrowsAsync<BusinessException>(() =>
            Controller(db).QuotationPrefill(inquiry.Id));
        Assert.Equal(ErrorCodes.InvalidParameter, prefill.Code);
        Assert.Empty(db.Quotations);
        Assert.Equal(DocumentStatus.Approved, db.Inquiries.AsNoTracking().Single().Status);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-7)]
    public async Task 转换_来源汇率非正_拒绝且绝不静默按一处理(decimal rate)
    {
        using var db = TestDbFactory.Create();
        var inquiry = Seed(db, DocumentStatus.Approved);
        inquiry.ExchangeRate = rate;
        db.SaveChanges();

        var error = await Assert.ThrowsAsync<BusinessException>(() => Controller(db).ToQuotation(inquiry.Id));

        Assert.Equal(ErrorCodes.InvalidParameter, error.Code);
        Assert.Equal(QuotationCommercialValidationRules.ExchangeRateText, error.Message);
        Assert.Empty(db.Quotations);
        Assert.Equal(DocumentStatus.Approved, db.Inquiries.AsNoTracking().Single().Status);
    }

    [Fact]
    public async Task 转换_有效来源_按币种精度派生金额且不采信来源行金额()
    {
        using var db = TestDbFactory.Create();
        var inquiry = Seed(db, DocumentStatus.Approved);
        var detail = db.InquiryDetails.Single();
        detail.Quantity = 3m;
        detail.UnitPrice = 10.5m;
        detail.Amount = 1m;                       // 来源行金额故意写错：服务端一律重算
        inquiry.Currency = Currency.JPY;
        inquiry.ExchangeRate = 0.05m;
        db.SaveChanges();

        Assert.IsType<OkObjectResult>(await Controller(db).ToQuotation(inquiry.Id));

        var quotation = db.Quotations.Include(q => q.Details).Single();
        Assert.Equal(Currency.JPY, quotation.Currency);
        Assert.Equal(32m, quotation.Details.Single().Amount);   // 31.5 → JPY 0 位小数 0.5 进位
        Assert.Equal(32m, quotation.TotalAmount);
        Assert.Equal(1.60m, quotation.TotalAmountCny);          // 折人民币按 CNY 2 位小数
        Assert.Equal(DocumentStatus.Completed, db.Inquiries.AsNoTracking().Single().Status);
    }


    // ==================== ERP-403 普通保存来源血缘回归 ====================

    [Fact]
    public void 普通保存与直接转换共用同一把来源行锁_锁序口径同源()
    {
        Assert.Equal(SalesDocumentSourceLineageRules.LockOrderText,
            InquiryQuotationConversion.SourceLineageLockOrderText);
        Assert.Equal(InquiryMutationRules.InquiryRowLockSql, SalesDocumentSourceLineageRules.InquiryRowLockSql);
        Assert.Contains("db_owner.Inquiries", SalesDocumentSourceLineageRules.InquiryRowLockSql);
    }

    [Fact]
    public async Task 带入预填草稿经服务端复核后保存_留痕权威来源且不改写来源状态()
    {
        using var db = TestDbFactory.Create();
        var inquiry = Seed(db, DocumentStatus.Approved);

        // 带入预填（只读草稿）→ 普通表单保存：显式来源 Id 仍被服务端权威复核 / 规范化。
        var draft = await InquiryQuotationConversion.BuildDraftAsync(db, inquiry.Id);
        var ctl = new QuotationController(db, new DocumentNumberService(db));
        TestAuth.SetUser(ctl, TestAuth.SeedPrivilegedUser(db));

        draft.InquiryNo = "FORGED-INQ";   // 调用方自由文本不是权威链接
        Assert.IsType<OkObjectResult>(await ctl.Create(draft));

        var saved = db.Quotations.AsNoTracking().Single();
        Assert.Equal(inquiry.Id, saved.InquiryId);
        Assert.Equal("INQ-CONVERT", saved.InquiryNo);
        // 普通保存不是直接转换：来源询价单状态保持已审核，绝不因保存而置「已完成」。
        Assert.Equal(DocumentStatus.Approved, db.Inquiries.AsNoTracking().Single().Status);

        // 直接转换在已有实时报价单时被既有重复规则拒绝（两种入口共用同一唯一目标口径）。
        var inquiryCtl = new InquiryController(db, new DocumentNumberService(db));
        TestAuth.SetUser(inquiryCtl, TestAuth.SeedPrivilegedUser(db));
        var ex = await Assert.ThrowsAsync<BusinessException>(() => inquiryCtl.ToQuotation(inquiry.Id));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Equal(1, db.Quotations.Count());
    }
}
