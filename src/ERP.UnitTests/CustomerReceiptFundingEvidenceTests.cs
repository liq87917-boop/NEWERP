using ERP.Application.Common;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Xunit;
namespace ERP.UnitTests;
public class CustomerReceiptFundingEvidenceTests
{
    [Theory]
    [InlineData(false, "USD", 1, 10)]
    [InlineData(true, "USD", 1, 10)]
    [InlineData(false, "CNY", 2, 10)]
    [InlineData(true, "CNY", 2, 10)]
    [InlineData(false, "CNY", 1, -10)]
    [InlineData(true, "CNY", 1, -10)]
    public async Task Invalid_active_evidence_blocks_single_and_candidate_funding(bool agency, string currency, long customer, int amount)
    {
        using var db = TestDbFactory.Create();
        var receipt = new FinanceReceipt { ReceiptNo="EVIDENCE", CustomerId=1, Amount=100m, Currency=Currency.CNY, Status=DocumentStatus.Approved };
        db.FinanceReceipts.Add(receipt); await db.SaveChangesAsync();
        if (agency) db.AgencyServiceFeeCollectionAllocations.Add(new AgencyServiceFeeCollectionAllocation { ReceiptId=receipt.Id, CustomerId=customer, Currency=currency, AllocatedAmount=amount, Status=AgencyServiceFeeCollectionAllocationRules.StatusActive });
        else db.CustomerReceiptAllocations.Add(new CustomerReceiptAllocation { ReceiptId=receipt.Id, CustomerId=customer, Currency=currency, AllocatedAmount=amount, Status=CustomerReceiptAllocationRules.StatusActive });
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<BusinessException>(() => CustomerReceiptLifecycleRules.LoadReceiptFundingAsync(db,receipt.Id));
        await Assert.ThrowsAsync<BusinessException>(() => CustomerReceiptLifecycleRules.LoadReceiptFundingForReceiptsAsync(db,new[]{receipt.Id}));
        Assert.Equal(100m, receipt.Amount);
        Assert.Equal(1, agency ? db.AgencyServiceFeeCollectionAllocations.Count() : db.CustomerReceiptAllocations.Count());
    }
}
