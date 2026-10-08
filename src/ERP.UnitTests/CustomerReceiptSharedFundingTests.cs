using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-350 收款单唯一分摊额度单元测试（内存库）：验证同一张收款单的「收款单 → 销售订单」（ERP-053）
/// 与「收款单 → 代理服务费对账单」（ERP-071）两套有效分摊行，在真实服务下共同占用同一张收款单的
/// 同一币种权威金额额度，拒绝合计超额、保持超额时两表不变、显式作废释放额度，并把两套证据的
/// 剩余额度准确反映到既有收款单汇总与候选数据里。全部使用内存库，不连接 SQL Server、不做浏览器 / UI 验收。
/// <para>原始 ERP-349 生命周期测试（CustomerReceiptLifecycleTests）保持不变；本类只补 ERP-350 缺失的
/// 跨消费者额度不变量。</para>
/// </summary>
public class CustomerReceiptSharedFundingTests
{
    // ==================== 0. 测试脚手架 ====================

    private static BaseCustomer SeedCustomer(ErpDbContext db, string code, string name)
    {
        var customer = new BaseCustomer
        {
            CustomerCode = code,
            CustomerName = name,
            Status = 1,
            CreditStatus = "正常",
            CreditLimit = 100000m,
            CreditDays = 30
        };
        db.BaseCustomers.Add(customer);
        db.SaveChanges();
        return customer;
    }

    private static FinanceReceipt SeedReceipt(
        ErpDbContext db, string receiptNo, long customerId, decimal amount = 100m,
        Currency currency = Currency.USD, DocumentStatus status = DocumentStatus.Approved)
    {
        var receipt = new FinanceReceipt
        {
            ReceiptNo = receiptNo,
            ReceiptDate = new DateTime(2026, 9, 10),
            CustomerId = customerId,
            Amount = amount,
            Currency = currency,
            PaymentMethod = PaymentMethod.BankTransfer,
            Status = status
        };
        db.FinanceReceipts.Add(receipt);
        db.SaveChanges();
        return receipt;
    }

    private static SalesOrder SeedOrder(ErpDbContext db, string orderNo, long customerId, Currency currency = Currency.USD)
    {
        var order = new SalesOrder
        {
            OrderNo = orderNo,
            OrderDate = new DateTime(2026, 9, 1),
            CustomerId = customerId,
            Currency = currency,
            Status = DocumentStatus.Approved
        };
        db.SalesOrders.Add(order);
        db.SaveChanges();
        return order;
    }

    private static AgencyServiceFeeStatement SeedStatement(
        ErpDbContext db, string statementNo, long customerId, string currency = "USD", decimal totalAmount = 1000m)
    {
        var statement = new AgencyServiceFeeStatement
        {
            StatementNo = statementNo,
            NormalizedStatementNo = AgencyServiceFeeStatementRules.NormalizeIdentityPart(statementNo),
            CustomerId = customerId,
            CustomerCode = "C001",
            CustomerName = "义乌进出口",
            Currency = currency,
            StatementDate = new DateTime(2026, 9, 20),
            ServicePeriodFrom = new DateTime(2026, 8, 1),
            ServicePeriodTo = new DateTime(2026, 8, 31),
            AgreementId = 1,
            AgreementNo = "ASF-2026-001",
            AgreementCurrency = currency,
            AgreementCustomerId = customerId,
            AgreementFeeMethod = "比例费率",
            AgreementTermsText = "比例费率",
            TotalAmount = totalAmount,
            Status = AgencyServiceFeeStatementRules.StatusRecorded,
            RecordedAt = new DateTime(2026, 9, 21),
            RecordedBy = "张三"
        };
        db.AgencyServiceFeeStatements.Add(statement);
        db.SaveChanges();
        return statement;
    }

    private static async Task<CustomerReceiptAllocationDto> CreateCustomerOrderAsync(
        ErpDbContext db, FinanceReceipt receipt, SalesOrder order, decimal amount)
        => await CustomerReceiptAllocationService.CreateAsync(db, new CustomerReceiptAllocationSaveDto
        {
            ReceiptId = receipt.Id,
            SalesOrderId = order.Id,
            AllocatedAmount = amount
        });

    private static async Task<AgencyServiceFeeCollectionAllocationDto> CreateAgencyAsync(
        ErpDbContext db, AgencyServiceFeeStatement statement, FinanceReceipt receipt, decimal amount)
        => await AgencyServiceFeeCollectionAllocationService.CreateAsync(db,
            new AgencyServiceFeeCollectionAllocationSaveDto
            {
                StatementId = statement.Id,
                ReceiptId = receipt.Id,
                AllocatedAmount = amount
            }, "tester");

    private static async Task<BusinessException> AssertBusinessAsync(int expectedCode, Func<Task> action)
    {
        var ex = await Assert.ThrowsAsync<BusinessException>(action);
        Assert.Equal(expectedCode, ex.Code);
        return ex;
    }

    // ==================== 1. 跨消费者额度：先客户、后代办（超额拒绝） ====================

    [Fact]
    public async Task 客户先80_代理再30_合计110_拒绝代理并保持两表不变()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "客户A");
        var receipt = SeedReceipt(db, "RC-1", customer.Id, amount: 100m, Currency.USD);
        var order = SeedOrder(db, "SO-1", customer.Id, Currency.USD);
        var statement = SeedStatement(db, "ASF-1", customer.Id, "USD", 1000m);

        var customerRow = await CreateCustomerOrderAsync(db, receipt, order, 80m);
        Assert.Equal(80m, customerRow.AllocatedAmount);

        await AssertBusinessAsync(ErrorCodes.RuleConflict, () => CreateAgencyAsync(db, statement, receipt, 30m));

        var customerRows = db.CustomerReceiptAllocations.AsNoTracking()
            .Where(a => a.ReceiptId == receipt.Id && !a.IsDeleted && a.Status == CustomerReceiptAllocationRules.StatusActive)
            .ToList();
        var agencyRows = db.AgencyServiceFeeCollectionAllocations.AsNoTracking()
            .Where(a => a.ReceiptId == receipt.Id && !a.IsDeleted && a.Status == AgencyServiceFeeCollectionAllocationRules.StatusActive)
            .ToList();
        Assert.Single(customerRows);
        Assert.Equal(80m, customerRows.Sum(a => a.AllocatedAmount));
        Assert.Empty(agencyRows);
    }

    [Fact]
    public async Task 代理先80_客户再30_合计110_拒绝客户并保持两表不变()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C002", "客户B");
        var receipt = SeedReceipt(db, "RC-2", customer.Id, amount: 100m, Currency.USD);
        var order = SeedOrder(db, "SO-2", customer.Id, Currency.USD);
        var statement = SeedStatement(db, "ASF-2", customer.Id, "USD", 1000m);

        var agencyRow = await CreateAgencyAsync(db, statement, receipt, 80m);
        Assert.Equal(80m, agencyRow.AllocatedAmount);

        await AssertBusinessAsync(ErrorCodes.RuleConflict, () => CreateCustomerOrderAsync(db, receipt, order, 30m));

        var customerRows = db.CustomerReceiptAllocations.AsNoTracking()
            .Where(a => a.ReceiptId == receipt.Id && !a.IsDeleted && a.Status == CustomerReceiptAllocationRules.StatusActive)
            .ToList();
        var agencyRows = db.AgencyServiceFeeCollectionAllocations.AsNoTracking()
            .Where(a => a.ReceiptId == receipt.Id && !a.IsDeleted && a.Status == AgencyServiceFeeCollectionAllocationRules.StatusActive)
            .ToList();
        Assert.Empty(customerRows);
        Assert.Single(agencyRows);
        Assert.Equal(80m, agencyRows.Sum(a => a.AllocatedAmount));
    }

    [Fact]
    public async Task 客户80_代理20_合计精确100_双写成功且互不改写对方表()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C003", "客户C");
        var receipt = SeedReceipt(db, "RC-3", customer.Id, amount: 100m, Currency.USD);
        var order = SeedOrder(db, "SO-3", customer.Id, Currency.USD);
        var statement = SeedStatement(db, "ASF-3", customer.Id, "USD", 1000m);

        await CreateCustomerOrderAsync(db, receipt, order, 80m);
        await CreateAgencyAsync(db, statement, receipt, 20m);

        var customerAllocated = db.CustomerReceiptAllocations.AsNoTracking()
            .Where(a => a.ReceiptId == receipt.Id && !a.IsDeleted && a.Status == CustomerReceiptAllocationRules.StatusActive)
            .Sum(a => a.AllocatedAmount);
        var agencyAllocated = db.AgencyServiceFeeCollectionAllocations.AsNoTracking()
            .Where(a => a.ReceiptId == receipt.Id && !a.IsDeleted && a.Status == AgencyServiceFeeCollectionAllocationRules.StatusActive)
            .Sum(a => a.AllocatedAmount);

        Assert.Equal(80m, customerAllocated);
        Assert.Equal(20m, agencyAllocated);
        Assert.Equal(100m, customerAllocated + agencyAllocated);

        Assert.Single(db.CustomerReceiptAllocations.AsNoTracking().Where(a => a.ReceiptId == receipt.Id));
        Assert.Single(db.AgencyServiceFeeCollectionAllocations.AsNoTracking().Where(a => a.ReceiptId == receipt.Id));
    }

    // ==================== 2. 显式作废释放额度 ====================

    [Fact]
    public async Task 占满后作废客户维度_释放额度后代理可再分摊()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C004", "客户D");
        var receipt = SeedReceipt(db, "RC-4", customer.Id, amount: 100m, Currency.USD);
        var order = SeedOrder(db, "SO-4", customer.Id, Currency.USD);
        var statement = SeedStatement(db, "ASF-4", customer.Id, "USD", 1000m);
        var statement2 = SeedStatement(db, "ASF-4B", customer.Id, "USD", 1000m);

        await CreateCustomerOrderAsync(db, receipt, order, 80m);
        await CreateAgencyAsync(db, statement, receipt, 20m);

        await AssertBusinessAsync(ErrorCodes.RuleConflict, () => CreateAgencyAsync(db, statement2, receipt, 10m));

        var customerRow = db.CustomerReceiptAllocations.Single(a => a.ReceiptId == receipt.Id && !a.IsDeleted);
        await CustomerReceiptAllocationService.VoidAsync(db, customerRow.Id, "录错");

        var created = await CreateAgencyAsync(db, statement2, receipt, 10m);
        Assert.Equal(10m, created.AllocatedAmount);

        var customerAllocated = db.CustomerReceiptAllocations.AsNoTracking()
            .Where(a => a.ReceiptId == receipt.Id && !a.IsDeleted && a.Status == CustomerReceiptAllocationRules.StatusActive)
            .Sum(a => a.AllocatedAmount);
        var agencyAllocated = db.AgencyServiceFeeCollectionAllocations.AsNoTracking()
            .Where(a => a.ReceiptId == receipt.Id && !a.IsDeleted && a.Status == AgencyServiceFeeCollectionAllocationRules.StatusActive)
            .Sum(a => a.AllocatedAmount);
        Assert.Equal(0m, customerAllocated);
        Assert.Equal(30m, agencyAllocated);
    }

    // ==================== 3. 币种 / 客户护栏在共享额度下仍失败关闭 ====================

    [Fact]
    public async Task 币种不一致_代理分摊失败关闭且不改写任何证据()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C005", "客户E");
        var receipt = SeedReceipt(db, "RC-5", customer.Id, amount: 100m, Currency.USD);
        var statementCny = SeedStatement(db, "ASF-5", customer.Id, "CNY", 1000m);

        await AssertBusinessAsync(ErrorCodes.RuleConflict,
            () => CreateAgencyAsync(db, statementCny, receipt, 20m));

        Assert.Empty(db.AgencyServiceFeeCollectionAllocations.AsNoTracking()
            .Where(a => a.ReceiptId == receipt.Id && !a.IsDeleted));
        Assert.Empty(db.CustomerReceiptAllocations.AsNoTracking()
            .Where(a => a.ReceiptId == receipt.Id && !a.IsDeleted));
    }

    [Fact]
    public async Task 客户不一致_客户引用失败关闭()
    {
        using var db = TestDbFactory.Create();
        var customerA = SeedCustomer(db, "C006", "客户F");
        var customerB = SeedCustomer(db, "C007", "客户G");
        var receipt = SeedReceipt(db, "RC-6", customerA.Id, amount: 100m, Currency.USD);
        var orderB = SeedOrder(db, "SO-6", customerB.Id, Currency.USD);

        await AssertBusinessAsync(ErrorCodes.RuleConflict,
            () => CreateCustomerOrderAsync(db, receipt, orderB, 20m));

        Assert.Empty(db.CustomerReceiptAllocations.AsNoTracking()
            .Where(a => a.ReceiptId == receipt.Id && !a.IsDeleted));
    }

    // ==================== 4. 汇总与候选暴露两套证据的剩余额度 ====================

    [Fact]
    public async Task 汇总与候选_反映跨维度已占用与剩余额度()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C008", "客户H");
        var receipt = SeedReceipt(db, "RC-7", customer.Id, amount: 100m, Currency.USD);
        var order = SeedOrder(db, "SO-7", customer.Id, Currency.USD);
        var statement = SeedStatement(db, "ASF-7", customer.Id, "USD", 1000m);

        await CreateCustomerOrderAsync(db, receipt, order, 60m);
        await CreateAgencyAsync(db, statement, receipt, 30m);

        var customerSummary = await CustomerReceiptAllocationService.GetReceiptSummaryAsync(db, receipt.Id);
        Assert.Equal(90m, customerSummary.AllocatedAmount);
        Assert.Equal(10m, customerSummary.UnallocatedAmount);

        var agencySummary = await AgencyServiceFeeCollectionAllocationService.GetReceiptSummaryAsync(db, receipt.Id);
        Assert.Equal(90m, agencySummary.AllocatedAmount);
        Assert.Equal(10m, agencySummary.UnallocatedAmount);

        var customerCandidates = await CustomerReceiptAllocationService.ListReceiptCandidatesAsync(db, customer.Id, null, 200);
        var customerCandidate = Assert.Single(customerCandidates, c => c.ReceiptId == receipt.Id);
        Assert.Equal(90m, customerCandidate.AllocatedAmount);
        Assert.Equal(10m, customerCandidate.UnallocatedAmount);
        Assert.True(customerCandidate.Eligible);

        var agencyCandidates = await AgencyServiceFeeCollectionAllocationService.ListReceiptCandidatesAsync(
            db, customer.Id, "USD", null, 200);
        var agencyCandidate = Assert.Single(agencyCandidates, c => c.ReceiptId == receipt.Id);
        Assert.Equal(90m, agencyCandidate.AllocatedAmount);
        Assert.Equal(10m, agencyCandidate.UnallocatedAmount);
        Assert.True(agencyCandidate.Eligible);
    }
}
