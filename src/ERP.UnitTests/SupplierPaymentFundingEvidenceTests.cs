using ERP.Application.Common;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Xunit;
namespace ERP.UnitTests;
public class SupplierPaymentFundingEvidenceTests
{
    [Theory]
    [InlineData(false, "USD", 1, 10)]
    [InlineData(true, "USD", 1, 10)]
    [InlineData(false, "CNY", 2, 10)]
    [InlineData(true, "CNY", 2, 10)]
    [InlineData(false, "CNY", 1, -10)]
    [InlineData(true, "CNY", 1, -10)]
    public async Task Invalid_active_funding_evidence_fails_closed_without_mutation(bool invoice, string currency, long supplier, int amount)
    {
        using var db=TestDbFactory.Create();
        var payment=new FinancePayment { PaymentNo="BAD-EVIDENCE", SupplierId=1, Amount=100m, Currency=Currency.CNY, Status=DocumentStatus.Approved };
        db.FinancePayments.Add(payment); await db.SaveChangesAsync();
        if(invoice) db.SupplierPaymentInvoiceAllocations.Add(new SupplierPaymentInvoiceAllocation { PaymentId=payment.Id, SupplierId=supplier, Currency=currency, AllocatedAmount=amount, Status=SupplierPaymentInvoiceAllocationRules.StatusActive });
        else db.SupplierPaymentAllocations.Add(new SupplierPaymentAllocation { PaymentId=payment.Id, SupplierId=supplier, Currency=currency, AllocatedAmount=amount, Status=SupplierPaymentAllocationRules.StatusActive });
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<BusinessException>(()=>SupplierPaymentLifecycleRules.LoadPaymentFundingAsync(db,payment.Id));
        Assert.Equal(100m,payment.Amount);
        Assert.Equal(1,invoice ? db.SupplierPaymentInvoiceAllocations.Count() : db.SupplierPaymentAllocations.Count());
    }
}
