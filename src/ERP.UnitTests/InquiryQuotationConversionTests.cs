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

    private static InquiryController Controller(ERP.Infrastructure.Data.ErpDbContext db)
        => new(db, new DocumentNumberService(db));

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
}
