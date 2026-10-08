using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ERP.Api.Controllers;

/// <summary>询价单转报价单的唯一映射与资格守卫。</summary>
public static class InquiryQuotationConversion
{
    /// <summary>
    /// 只读构造（带入预填 / 直接转换共用的资格守卫入口）：按 <paramref name="inquiryId"/> 读取**权威**询价单
    /// （含明细），复核既有转换资格（重复生成 / 已完成 / 未审核 / 无有效明细），再映射为未落库的报价单草稿。
    /// 本方法**不落库、不占号、不改写来源状态**；写路径（转报价单）应在询价单来源行锁内改用
    /// <see cref="BuildDraftAsync(IErpDbContext, Inquiry)"/> 以锁内权威重读为准。
    /// </summary>
    public static async Task<Quotation> BuildDraftAsync(IErpDbContext db, long inquiryId)
    {
        var inquiry = await db.Inquiries.AsNoTracking().Include(o => o.Details)
            .FirstOrDefaultAsync(o => o.Id == inquiryId && !o.IsDeleted)
            ?? throw BusinessException.NotFound("询价单不存在");

        var existing = await InquiryMutationRules.FindLiveQuotationAsync(db, inquiryId);
        InquiryMutationRules.EnsureQuotationConversionEligible(inquiry, existing,
            InquiryMutationRules.ActiveDetailCount(inquiry));

        return await BuildDraftAsync(db, inquiry);
    }

    /// <summary>
    /// 按**锁内权威重读**的询价单映射报价单草稿（不查资格、不落库、不占号、不改写来源）：
    /// 资格由调用方在询价单来源行锁内以 <see cref="InquiryMutationRules.EnsureQuotationConversionEligible"/>
    /// 复核后调用，保证「重复检测 / 单号生成 / 草稿构造」全部发生在同一原子事务内。
    /// </summary>
    public static async Task<Quotation> BuildDraftAsync(IErpDbContext db, Inquiry inquiry)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(inquiry);

        var customer = inquiry.CustomerId > 0
            ? await db.BaseCustomers.AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == inquiry.CustomerId && !c.IsDeleted)
            : null;
        if (customer == null)
            throw BusinessException.RuleConflict("询价单客户无效，不能生成报价单");

        var salesmanName = inquiry.SalesmanId.HasValue
            ? await db.BaseEmployees.AsNoTracking()
                .Where(e => e.Id == inquiry.SalesmanId.Value && !e.IsDeleted)
                .Select(e => e.EmployeeName)
                .FirstOrDefaultAsync() ?? string.Empty
            : string.Empty;

        var quotationDate = DateTime.Today;
        var quotation = new Quotation
        {
            QuotationDate = quotationDate,
            ValidUntil = quotationDate.AddDays(inquiry.ValidDays > 0 ? inquiry.ValidDays : 30),
            CustomerId = inquiry.CustomerId,
            CustomerName = customer.CustomerName,
            ContactPerson = string.IsNullOrWhiteSpace(inquiry.ContactPerson) ? customer.ContactPerson : inquiry.ContactPerson,
            ContactPhone = string.IsNullOrWhiteSpace(inquiry.ContactPhone) ? customer.Phone : inquiry.ContactPhone,
            ContactEmail = customer.Email,
            InquiryId = inquiry.Id,
            InquiryNo = inquiry.InquiryNo,
            TradeTerms = customer.TradeTerms,
            PortOfDestination = customer.DestinationPort,
            PaymentTerms = customer.PaymentTerms,
            Currency = inquiry.Currency,
            ExchangeRate = inquiry.ExchangeRate > 0 ? inquiry.ExchangeRate : 1m,
            SalesmanId = inquiry.SalesmanId,
            SalesmanName = salesmanName,
            Remark = inquiry.Remark,
            Status = DocumentStatus.Pending,
            Details = inquiry.Details.Where(d => !d.IsDeleted).OrderBy(d => d.Id)
                .Select((d, index) => new QuotationDetail
                {
                    SortNo = index + 1,
                    ProductId = d.ProductId,
                    ProductName = d.ProductName,
                    Spec = d.Spec,
                    Unit = d.Unit,
                    Quantity = d.Quantity,
                    UnitPrice = d.UnitPrice,
                    Amount = Math.Round(d.Quantity * d.UnitPrice, 2),
                    Remark = d.Remark
                }).ToList()
        };
        Recalculate(quotation);
        return quotation;
    }

    public static void Recalculate(Quotation quotation)
    {
        decimal total = 0;
        var line = 0;
        foreach (var detail in quotation.Details)
        {
            detail.SortNo = ++line;
            detail.Amount = Math.Round(detail.Quantity * detail.UnitPrice, 2);
            total += detail.Amount;
        }
        quotation.TotalAmount = Math.Round(total, 2);
        quotation.TotalAmountCny = Math.Round(quotation.TotalAmount *
            (quotation.ExchangeRate > 0 ? quotation.ExchangeRate : 1m), 2);
    }
}
