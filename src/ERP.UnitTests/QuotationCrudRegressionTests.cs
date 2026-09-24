using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERP.UnitTests;

/// <summary>ERP-024：报价单通用 CRUD、查询与服务端合计复算的聚焦回归覆盖。</summary>
public class QuotationCrudRegressionTests
{
    [Fact]
    public async Task Create_ignores_client_totals_and_recalculates_lines_and_cny_total()
    {
        using var db = TestDbFactory.Create();
        var entity = Draft(string.Empty, DocumentStatus.Approved);
        entity.Id = 999;
        entity.TotalAmount = 999999;
        entity.TotalAmountCny = 1;
        entity.ExchangeRate = 7.2m;
        entity.Details = new()
        {
            Line(2, 10, amount: 999, sortNo: 99),
            Line(3, 4, amount: 888, sortNo: 88)
        };

        Assert.IsType<OkObjectResult>(await Controller(db).Create(entity));

        var saved = db.Quotations.Include(q => q.Details).Single();
        Assert.NotEqual(999, saved.Id);
        Assert.StartsWith("QT", saved.QuotationNo);
        Assert.Equal(DocumentStatus.Pending, saved.Status);
        Assert.Equal(32m, saved.TotalAmount);
        Assert.Equal(230.4m, saved.TotalAmountCny);
        Assert.Equal(new[] { 1, 2 }, saved.Details.OrderBy(d => d.SortNo).Select(d => d.SortNo));
        Assert.Equal(new[] { 20m, 12m }, saved.Details.OrderBy(d => d.SortNo).Select(d => d.Amount));
    }

    [Fact]
    public async Task Create_defaults_dates_and_zero_exchange_rate_without_trusting_client_values()
    {
        using var db = TestDbFactory.Create();
        var entity = Draft("QT-DEFAULTS", DocumentStatus.Pending);
        entity.QuotationDate = default;
        entity.ValidUntil = null;
        entity.ExchangeRate = 0;
        entity.Details = new() { Line(5, 6, 1, 1) };

        await Controller(db).Create(entity);

        var saved = db.Quotations.Single();
        Assert.Equal(DateTime.Today, saved.QuotationDate.Date);
        Assert.Equal(DateTime.Today.AddDays(30), saved.ValidUntil);
        Assert.Equal(30m, saved.TotalAmount);
        Assert.Equal(30m, saved.TotalAmountCny);
    }

    [Fact]
    public async Task Update_replaces_details_preserves_number_and_recalculates_totals()
    {
        using var db = TestDbFactory.Create();
        var original = Seed(db, "QT-KEEP", DocumentStatus.Pending, Line(1, 10, 10, 1));
        var oldDetailId = db.QuotationDetails.Single().Id;
        var update = Draft("CLIENT-CANNOT-RENAME", DocumentStatus.Approved);
        update.ExchangeRate = 2m;
        update.TotalAmount = 999;
        update.Details = new() { Line(4, 3, 999, 50), Line(1.5m, 2m, 999, 60) };

        Assert.IsType<OkObjectResult>(await Controller(db).Update(original.Id, update));

        var saved = db.Quotations.Include(q => q.Details).Single();
        Assert.Equal("QT-KEEP", saved.QuotationNo);
        Assert.Equal(15m, saved.TotalAmount);
        Assert.Equal(30m, saved.TotalAmountCny);
        Assert.DoesNotContain(saved.Details, d => d.Id == oldDetailId);
        Assert.Equal(new[] { 1, 2 }, saved.Details.OrderBy(d => d.SortNo).Select(d => d.SortNo));
    }

    [Theory]
    [InlineData(DocumentStatus.Approved)]
    [InlineData(DocumentStatus.Cancelled)]
    public async Task Update_rejects_locked_status_and_preserves_data(DocumentStatus status)
    {
        using var db = TestDbFactory.Create();
        var original = Seed(db, "QT-LOCKED", status, Line(2, 5, 10, 1));

        await Assert.ThrowsAsync<BusinessException>(() => Controller(db).Update(original.Id,
            Draft("IGNORED", DocumentStatus.Pending)));

        Assert.Equal(10m, db.Quotations.Single().TotalAmount);
        Assert.Single(db.QuotationDetails);
    }

    [Fact]
    public async Task Detail_returns_only_active_lines_in_sort_order()
    {
        using var db = TestDbFactory.Create();
        var quotation = Seed(db, "QT-DETAIL", DocumentStatus.Pending,
            Line(1, 1, 1, 2), Line(1, 2, 2, 1), Line(1, 3, 3, 3));
        db.QuotationDetails.Single(d => d.SortNo == 3).IsDeleted = true;
        db.SaveChanges();

        var result = GetData<Quotation>(await Controller(db).GetById(quotation.Id));

        Assert.Equal(new[] { 1, 2 }, result.Details.Select(d => d.SortNo));
        Assert.DoesNotContain(result.Details, d => d.IsDeleted);
    }

    [Fact]
    public async Task Detail_rejects_missing_and_soft_deleted_records()
    {
        using var db = TestDbFactory.Create();
        var quotation = Seed(db, "QT-DELETED", DocumentStatus.Pending, Line(1, 1, 1, 1));
        quotation.IsDeleted = true;
        db.SaveChanges();

        await Assert.ThrowsAsync<BusinessException>(() => Controller(db).GetById(quotation.Id));
        await Assert.ThrowsAsync<BusinessException>(() => Controller(db).GetById(999999));
    }

    [Fact]
    public async Task List_applies_keyword_status_date_and_paging_filters()
    {
        using var db = TestDbFactory.Create();
        var first = Seed(db, "QT-ALPHA", DocumentStatus.Pending, Line(1, 1, 1, 1));
        first.CustomerName = "Target Customer";
        first.QuotationDate = new DateTime(2026, 9, 1);
        var second = Seed(db, "QT-BETA", DocumentStatus.Approved, Line(1, 1, 1, 1));
        second.InquiryNo = "TARGET-INQUIRY";
        second.QuotationDate = new DateTime(2026, 9, 2);
        var excluded = Seed(db, "QT-OLD", DocumentStatus.Approved, Line(1, 1, 1, 1));
        excluded.CustomerName = "Target Customer";
        excluded.QuotationDate = new DateTime(2026, 8, 1);
        db.SaveChanges();

        var result = GetData<PagedResult<Quotation>>(await Controller(db).GetPaged(
            new PageQuery { Page = 0, PageSize = 0, Keyword = "TARGET" }, DocumentStatus.Approved,
            new DateTime(2026, 9, 1), new DateTime(2026, 9, 30)));

        Assert.Equal(1, result.Total);
        Assert.Equal(1, result.Page);
        Assert.True(result.PageSize > 0);
        Assert.Equal("QT-BETA", Assert.Single(result.Items).QuotationNo);
    }

    private static QuotationController Controller(ERP.Infrastructure.Data.ErpDbContext db)
        => new(db, new DocumentNumberService(db));

    private static T GetData<T>(IActionResult action)
    {
        var response = Assert.IsType<ApiResponse<T>>(Assert.IsType<OkObjectResult>(action).Value);
        return response.Data!;
    }

    private static Quotation Seed(ERP.Infrastructure.Data.ErpDbContext db, string no,
        DocumentStatus status, params QuotationDetail[] details)
    {
        var quotation = Draft(no, status);
        quotation.Status = status;
        quotation.Details = details.ToList();
        quotation.TotalAmount = details.Sum(d => d.Amount);
        quotation.TotalAmountCny = quotation.TotalAmount * quotation.ExchangeRate;
        db.Quotations.Add(quotation);
        db.SaveChanges();
        return quotation;
    }

    private static Quotation Draft(string no, DocumentStatus status) => new()
    {
        QuotationNo = no, QuotationDate = new DateTime(2026, 9, 1), CustomerName = "Customer",
        InquiryNo = "INQ-1", SalesmanName = "Sales", Currency = Currency.USD, ExchangeRate = 1m,
        Status = status, Details = new() { Line(1, 1, 999, 99) }
    };

    private static QuotationDetail Line(decimal quantity, decimal price, decimal amount, int sortNo) => new()
    {
        ProductId = sortNo, ProductName = "P" + sortNo, Unit = "PCS", Quantity = quantity,
        UnitPrice = price, Amount = amount, SortNo = sortNo
    };
}
