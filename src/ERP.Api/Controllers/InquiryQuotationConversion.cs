using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ERP.Api.Controllers;

/// <summary>询价单转报价单的唯一映射与资格守卫。</summary>
public static class InquiryQuotationConversion
{
    public static async Task<Quotation> BuildDraftAsync(IErpDbContext db, long inquiryId)
    {
        var inquiry = await db.Inquiries.AsNoTracking().Include(o => o.Details)
            .FirstOrDefaultAsync(o => o.Id == inquiryId && !o.IsDeleted)
            ?? throw BusinessException.NotFound("询价单不存在");

        if (await db.Quotations.AsNoTracking().AnyAsync(q => q.InquiryId == inquiryId && !q.IsDeleted))
            throw BusinessException.RuleConflict("该询价单已生成报价单，不能重复转换");
        if (inquiry.Status != DocumentStatus.Approved)
            throw BusinessException.RuleConflict("仅已审核的询价单可转为报价单");
        if (!inquiry.Details.Any(d => !d.IsDeleted))
            throw BusinessException.RuleConflict("询价单没有有效明细，不能生成报价单");

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
