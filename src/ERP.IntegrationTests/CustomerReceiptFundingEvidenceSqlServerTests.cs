using ERP.Application.Common;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Xunit;
namespace ERP.IntegrationTests;
// Reuses the guarded fresh-GUID dedicated LocalDB fixture; no existing or production database is accessed.
public sealed class CustomerReceiptFundingEvidenceSqlServerTests : IClassFixture<CustomerReceiptSharedFundingSqlServerFixture>
{
    private readonly CustomerReceiptSharedFundingSqlServerFixture fixture;
    public CustomerReceiptFundingEvidenceSqlServerTests(CustomerReceiptSharedFundingSqlServerFixture fixture) => this.fixture=fixture;
    [Theory]
    [InlineData(false, "USD", false, 10)]
    [InlineData(true, "USD", false, 10)]
    [InlineData(false, "CNY", true, 10)]
    [InlineData(true, "CNY", true, 10)]
    [InlineData(false, "CNY", false, -10)]
    [InlineData(true, "CNY", false, -10)]
    public async Task Persisted_invalid_active_evidence_blocks_both_funding_queries_without_mutation(bool agency, string currency, bool wrongCustomer, int amount)
    {
        await using var db=fixture.CreateDbContext();
        var first=new BaseCustomer { CustomerCode=Guid.NewGuid().ToString("N"), CustomerName="Funding evidence owner", Status=1 };
        var second=new BaseCustomer { CustomerCode=Guid.NewGuid().ToString("N"), CustomerName="Other funding owner", Status=1 };
        db.BaseCustomers.AddRange(first,second); await db.SaveChangesAsync();
        var receipt=new FinanceReceipt { ReceiptNo="EVID-"+Guid.NewGuid().ToString("N"), CustomerId=first.Id, Amount=100m, Currency=Currency.CNY, Status=DocumentStatus.Approved, ReceiptDate=DateTime.Today };
        db.FinanceReceipts.Add(receipt); await db.SaveChangesAsync();
        var customerId=wrongCustomer ? second.Id : first.Id;
        if(agency) db.AgencyServiceFeeCollectionAllocations.Add(new AgencyServiceFeeCollectionAllocation { ReceiptId=receipt.Id, CustomerId=customerId, Currency=currency, AllocatedAmount=amount, Status=AgencyServiceFeeCollectionAllocationRules.StatusActive });
        else db.CustomerReceiptAllocations.Add(new CustomerReceiptAllocation { ReceiptId=receipt.Id, CustomerId=customerId, Currency=currency, AllocatedAmount=amount, Status=CustomerReceiptAllocationRules.StatusActive });
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        await Assert.ThrowsAsync<BusinessException>(() => CustomerReceiptLifecycleRules.LoadReceiptFundingAsync(db,receipt.Id));
        await Assert.ThrowsAsync<BusinessException>(() => CustomerReceiptLifecycleRules.LoadReceiptFundingForReceiptsAsync(db,new[]{receipt.Id}));
        Assert.Equal(100m,(await db.FinanceReceipts.AsNoTracking().SingleAsync(r=>r.Id==receipt.Id)).Amount);
        if(agency)
        {
            var row=await db.AgencyServiceFeeCollectionAllocations.AsNoTracking().SingleAsync(a=>a.ReceiptId==receipt.Id);
            Assert.Equal(amount,row.AllocatedAmount); Assert.Equal(currency,row.Currency); Assert.Equal(customerId,row.CustomerId);
            Assert.Equal(AgencyServiceFeeCollectionAllocationRules.StatusActive,row.Status);
            Assert.False(await db.CustomerReceiptAllocations.AnyAsync(a=>a.ReceiptId==receipt.Id));
        }
        else
        {
            var row=await db.CustomerReceiptAllocations.AsNoTracking().SingleAsync(a=>a.ReceiptId==receipt.Id);
            Assert.Equal(amount,row.AllocatedAmount); Assert.Equal(currency,row.Currency); Assert.Equal(customerId,row.CustomerId);
            Assert.Equal(CustomerReceiptAllocationRules.StatusActive,row.Status);
            Assert.False(await db.AgencyServiceFeeCollectionAllocations.AnyAsync(a=>a.ReceiptId==receipt.Id));
        }
    }
}
