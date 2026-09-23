using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// 报价单转形式发票 PI（POST /api/sales/quotations/{id}/to-pi）单元测试：
/// 覆盖字段与明细复制、来源报价单回填、原单状态变更、重复转换拦截与非法状态拒绝。
/// </summary>
public class QuotationToPiTests
{
    [Fact]
    public async Task ToPi_已审核报价单_复制主表与明细_回填来源_原单变已转PI()
    {
        using var db = TestDbFactory.Create();
        var (quotation, detail) = SeedQuotation(db, "QT2609230001", DocumentStatus.Approved);
        var ctl = NewController(db);

        var ok = Assert.IsType<OkObjectResult>(await ctl.ToProformaInvoice(quotation.Id));
        Assert.NotNull(ok.Value);

        var pi = db.ProformaInvoices.Single();
        Assert.True(pi.Id > 0);
        Assert.StartsWith("PI", pi.PiNo);
        Assert.Equal(DocumentStatus.Pending, pi.Status);
        Assert.Equal(quotation.Id, pi.QuotationId);
        Assert.Equal("QT2609230001", pi.QuotationNo);
        Assert.Equal(quotation.CustomerName, pi.CustomerName);
        Assert.Equal(quotation.ContactPerson, pi.ContactPerson);
        Assert.Equal(quotation.ContactEmail, pi.ContactEmail);
        Assert.Equal(quotation.TradeTerms, pi.TradeTerms);
        Assert.Equal(quotation.PortOfLoading, pi.PortOfLoading);
        Assert.Equal(quotation.PortOfDestination, pi.PortOfDestination);
        Assert.Equal(quotation.PaymentTerms, pi.PaymentTerms);
        Assert.Equal(quotation.LeadTime, pi.LeadTime);
        Assert.Equal(quotation.Currency, pi.Currency);
        Assert.Equal(quotation.ExchangeRate, pi.ExchangeRate);
        Assert.Equal(quotation.SalesmanName, pi.SalesmanName);

        // 明细复制：数量 / 单价 / 金额 / 行号重新计算
        var piDetails = db.ProformaInvoiceDetails.Where(d => d.PiId == pi.Id).OrderBy(d => d.SortNo).ToList();
        Assert.Single(piDetails);
        Assert.Equal(detail.ProductCode, piDetails[0].ProductCode);
        Assert.Equal(1, piDetails[0].SortNo);
        Assert.Equal(detail.Quantity, piDetails[0].Quantity);
        Assert.Equal(detail.UnitPrice, piDetails[0].UnitPrice);
        Assert.Equal(detail.Amount, piDetails[0].Amount);
        Assert.Equal(pi.PiNo, piDetails[0].PiNo);

        // 合计与定金：30% × 1000 = 300（客户资料未维护定金比例时按 30%）
        Assert.Equal(1000m, pi.TotalAmount);
        Assert.Equal(30m, pi.DepositRatio);
        Assert.Equal(300m, pi.DepositAmount);

        // 原报价单转为「已转 PI」
        Assert.Equal(DocumentStatus.Completed, db.Quotations.Single().Status);
    }

    [Fact]
    public async Task ToPi_客户资料与系统参数_带入ConsigneeNotify唛头银行信息与定金比例()
    {
        using var db = TestDbFactory.Create();
        var (quotation, _) = SeedQuotation(db, "QT2609230002", DocumentStatus.Approved);
        db.BaseCustomers.Add(new BaseCustomer
        {
            CustomerCode = "C001",
            CustomerName = quotation.CustomerName,
            Consignee = "CONSIGNEE LINE",
            NotifyParty = "NOTIFY LINE",
            DefaultShippingMark = "N/M",
            DepositRatio = 40m
        });
        db.SysParameters.Add(new SysParameter
        {
            ParamKey = "PI_BankInfo",
            ParamValue = "Beneficiary: YIWU CO.\r\nSWIFT: BKCHCNBJ",
            ParamName = "PI 银行信息（默认）"
        });
        db.SaveChanges();
        quotation.CustomerId = db.BaseCustomers.Single().Id;
        db.SaveChanges();
        var ctl = NewController(db);

        await ctl.ToProformaInvoice(quotation.Id);

        var pi = db.ProformaInvoices.Single();
        Assert.Equal("CONSIGNEE LINE", pi.Consignee);
        Assert.Equal("NOTIFY LINE", pi.NotifyParty);
        Assert.Equal("N/M", pi.ShippingMarks);
        Assert.Contains("SWIFT", pi.BankInfo);
        Assert.Equal(40m, pi.DepositRatio);
        Assert.Equal(400m, pi.DepositAmount);   // 1000 × 40%
    }

    [Fact]
    public async Task ToPi_草稿或已提交报价单_拒绝并给出业务错误()
    {
        using var db = TestDbFactory.Create();
        var (draft, _) = SeedQuotation(db, "QT-DRAFT", DocumentStatus.Pending);
        var (submitted, _) = SeedQuotation(db, "QT-SUBMITTED", DocumentStatus.Submitted);
        var ctl = NewController(db);

        var exDraft = await Assert.ThrowsAsync<BusinessException>(() => ctl.ToProformaInvoice(draft.Id));
        Assert.Equal(ErrorCodes.RuleConflict, exDraft.Code);
        Assert.Contains("审核", exDraft.Message);

        var exSubmitted = await Assert.ThrowsAsync<BusinessException>(() => ctl.ToProformaInvoice(submitted.Id));
        Assert.Equal(ErrorCodes.RuleConflict, exSubmitted.Code);

        Assert.Empty(db.ProformaInvoices);
        Assert.Equal(DocumentStatus.Pending, db.Quotations.Single(q => q.Id == draft.Id).Status);
        Assert.Equal(DocumentStatus.Submitted, db.Quotations.Single(q => q.Id == submitted.Id).Status);
    }

    [Fact]
    public async Task ToPi_已作废报价单_拒绝转换()
    {
        using var db = TestDbFactory.Create();
        var (quotation, _) = SeedQuotation(db, "QT-CANCELLED", DocumentStatus.Cancelled);
        var ctl = NewController(db);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.ToProformaInvoice(quotation.Id));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Empty(db.ProformaInvoices);
    }

    [Fact]
    public async Task ToPi_已转PI报价单_拒绝重复转换()
    {
        using var db = TestDbFactory.Create();
        var (quotation, _) = SeedQuotation(db, "QT-DONE", DocumentStatus.Approved);
        var ctl = NewController(db);

        await ctl.ToProformaInvoice(quotation.Id);
        Assert.Single(db.ProformaInvoices);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.ToProformaInvoice(quotation.Id));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("不能重复转换", ex.Message);
        Assert.Single(db.ProformaInvoices);
    }

    [Fact]
    public async Task ToPi_已存在PI但原单状态被改回_仍拒绝重复转换()
    {
        using var db = TestDbFactory.Create();
        var (quotation, _) = SeedQuotation(db, "QT-REOPEN", DocumentStatus.Approved);
        var ctl = NewController(db);
        await ctl.ToProformaInvoice(quotation.Id);

        // 人为把报价单状态改回已审核（模拟误操作），幂等保护仍由 PI.QuotationId 兜底
        var entity = db.Quotations.Single();
        entity.Status = DocumentStatus.Approved;
        db.SaveChanges();

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.ToProformaInvoice(quotation.Id));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("已转为 PI", ex.Message);
        Assert.Single(db.ProformaInvoices);
    }

    [Fact]
    public async Task ToPi_报价单无明细_拒绝转换()
    {
        using var db = TestDbFactory.Create();
        var quotation = new Quotation
        {
            QuotationNo = "QT-EMPTY",
            QuotationDate = DateTime.Today,
            CustomerName = "客户 A",
            Currency = Currency.USD,
            ExchangeRate = 7.2m,
            Status = DocumentStatus.Approved
        };
        db.Quotations.Add(quotation);
        db.SaveChanges();
        var ctl = NewController(db);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.ToProformaInvoice(quotation.Id));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("明细", ex.Message);
    }

    [Fact]
    public async Task ToPi_报价单不存在_抛NotFound()
    {
        using var db = TestDbFactory.Create();
        var ctl = NewController(db);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.ToProformaInvoice(999999));
        Assert.Equal(ErrorCodes.NotFound, ex.Code);
    }

    // ==================== 种子与工厂 ====================

    private static QuotationController NewController(ErpDbContext db)
        => new(db, new DocumentNumberService(db));

    /// <summary>种一张报价单（含一行明细：1000 × 1 = 1000）</summary>
    private static (Quotation quotation, QuotationDetail detail) SeedQuotation(
        ErpDbContext db, string no, DocumentStatus status)
    {
        var quotation = new Quotation
        {
            QuotationNo = no,
            QuotationDate = DateTime.Today,
            ValidUntil = DateTime.Today.AddDays(30),
            CustomerId = 1,
            CustomerName = "义乌外贸客户 A",
            ContactPerson = "Mr. Smith",
            ContactEmail = "smith@example.com",
            TradeTerms = "FOB",
            PortOfLoading = "NINGBO",
            PortOfDestination = "HAMBURG",
            PaymentTerms = "T/T 30% deposit",
            LeadTime = "35 days after deposit",
            Currency = Currency.USD,
            ExchangeRate = 7.2m,
            TotalAmount = 1000m,
            TotalAmountCny = 7200m,
            SalesmanId = 1,
            SalesmanName = "业务员 A",
            Status = status,
            Remark = "PI_FLOW_TEST"
        };
        db.Quotations.Add(quotation);
        db.SaveChanges();

        var detail = new QuotationDetail
        {
            QuotationId = quotation.Id,
            QuotationNo = quotation.QuotationNo,
            SortNo = 1,
            ProductCode = "P001",
            ProductName = "饰品 A",
            Spec = "大",
            Unit = "PCS",
            Quantity = 1000m,
            UnitPrice = 1m,
            Amount = 1000m
        };
        db.QuotationDetails.Add(detail);
        db.SaveChanges();
        return (quotation, detail);
    }
}
