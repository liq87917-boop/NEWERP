using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-075：把 ERP-073「客户收款单 → 客户销项发票」收款分摊证据暴露到客户销项发票详情 / 台账与收款单工作流的单元测试。
/// 覆盖：无证据（未分摊=含税总额、绝不当作已付 / 已结清）、部分 / 全额分摊、已作废行不计入有效合计但历史可读、
/// 多币种不合并（绝不换算或改派）、台账有界汇总（不逐行查库）、来源记录非变更、以及收款单侧发票分摊与剩余收款证据。
/// 全部使用内存库（TestDbFactory），不连接 SQL Server、不执行任何 SQL / 部署脚本、不做浏览器 / UI 验收。
/// </summary>
public class CustomerSalesInvoiceReceiptAllocationExposureTests
{
    private static CustomerSalesInvoiceEvidenceController InvoiceController(ErpDbContext db)
    {
        var controller = new CustomerSalesInvoiceEvidenceController(db);
        TestAuth.SetUser(controller, TestAuth.SeedPrivilegedUser(db));
        return controller;
    }

    private static BaseCustomer SeedCustomer(
        ErpDbContext db, string code, string name, int status = 1, bool deleted = false)
    {
        var customer = new BaseCustomer
        {
            CustomerCode = code,
            CustomerName = name,
            Status = status,
            CreditStatus = "正常",
            CreditLimit = 100000m,
            CreditDays = 30,
            IsDeleted = deleted
        };
        db.BaseCustomers.Add(customer);
        db.SaveChanges();
        return customer;
    }

    private static CustomerSalesInvoiceEvidence SeedInvoice(
        ErpDbContext db, string invoiceNumber, long customerId, string currency = "USD",
        decimal grossAmount = 1000m, int status = CustomerSalesInvoiceEvidenceRules.StatusRecorded,
        bool deleted = false)
    {
        var invoice = new CustomerSalesInvoiceEvidence
        {
            InvoiceType = "普票",
            InvoiceCode = "",
            InvoiceNumber = invoiceNumber,
            NormalizedInvoiceNumber = invoiceNumber.Replace("-", "").Replace("_", "").ToUpperInvariant(),
            InvoiceDate = new DateTime(2026, 8, 20),
            CustomerId = customerId,
            CustomerCode = "C001",
            CustomerName = "义乌进出口",
            Currency = currency,
            NetAmount = grossAmount * 0.9m,
            TaxAmount = grossAmount * 0.1m,
            GrossAmount = grossAmount,
            Status = status,
            IsDeleted = deleted
        };
        db.CustomerSalesInvoiceEvidences.Add(invoice);
        db.SaveChanges();
        return invoice;
    }

    private static FinanceReceipt SeedReceipt(
        ErpDbContext db, string receiptNo, long customerId, string currency = "USD",
        decimal amount = 1000m, DocumentStatus status = DocumentStatus.Approved, bool deleted = false)
    {
        var receipt = new FinanceReceipt
        {
            ReceiptNo = receiptNo,
            ReceiptDate = new DateTime(2026, 9, 25),
            CustomerId = customerId,
            Amount = amount,
            Currency = currency == "CNY" ? Currency.CNY : Currency.USD,
            PaymentMethod = PaymentMethod.BankTransfer,
            BankAccount = "TEST-ACCOUNT",
            Status = status,
            IsDeleted = deleted
        };
        db.FinanceReceipts.Add(receipt);
        db.SaveChanges();
        return receipt;
    }

    private static CustomerSalesInvoiceCollectionAllocation SeedAllocation(
        ErpDbContext db, CustomerSalesInvoiceEvidence invoice, FinanceReceipt receipt,
        decimal allocatedAmount, int status = CustomerSalesInvoiceCollectionAllocationRules.StatusActive,
        string? voidReason = null)
    {
        var row = new CustomerSalesInvoiceCollectionAllocation
        {
            CustomerSalesInvoiceEvidenceId = invoice.Id,
            InvoiceType = invoice.InvoiceType,
            InvoiceCode = invoice.InvoiceCode ?? string.Empty,
            InvoiceNumber = invoice.InvoiceNumber,
            InvoiceDate = invoice.InvoiceDate,
            InvoiceStatus = invoice.Status,
            InvoiceStatusText = CustomerSalesInvoiceEvidenceRules.StatusText(invoice.Status),
            InvoiceGrossAmount = invoice.GrossAmount,
            InvoiceCurrency = invoice.Currency,
            ReceiptId = receipt.Id,
            ReceiptNo = receipt.ReceiptNo,
            ReceiptDate = receipt.ReceiptDate,
            ReceiptStatus = (int)receipt.Status,
            ReceiptStatusText = "已审核",
            ReceiptAmount = receipt.Amount,
            CustomerId = invoice.CustomerId,
            CustomerCode = invoice.CustomerCode ?? string.Empty,
            CustomerName = invoice.CustomerName ?? string.Empty,
            AllocatedAmount = allocatedAmount,
            Currency = invoice.Currency,
            Remark = "测试分摊",
            Status = status,
            AllocatedAt = new DateTime(2026, 9, 26),
            AllocatedBy = "测试员",
            VoidedAt = status == CustomerSalesInvoiceCollectionAllocationRules.StatusVoided
                ? new DateTime(2026, 9, 27)
                : null,
            VoidReason = voidReason ?? string.Empty
        };
        db.CustomerSalesInvoiceCollectionAllocations.Add(row);
        db.SaveChanges();
        return row;
    }

    private static T AssertOk<T>(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<ApiResponse<T>>(ok.Value);
        Assert.Equal(ErrorCodes.Success, resp.Code);
        Assert.NotNull(resp.Data);
        return resp.Data!;
    }

    // ==================== 1. 发票详情：无 / 部分 / 全额 / 作废 ====================

    [Fact]
    public async Task 发票详情_无收款分摊证据_未分摊等于含税总额且绝不当作已付()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var invoice = SeedInvoice(db, "INV-001", customer.Id, grossAmount: 1000m);
        var controller = InvoiceController(db);

        var dto = AssertOk<CustomerSalesInvoiceEvidenceDto>(await controller.GetById(invoice.Id));

        Assert.Equal(0m, dto.ReceiptAllocatedAmount);
        Assert.Equal(1000m, dto.ReceiptUnallocatedAmount);
        Assert.Equal(0, dto.ReceiptAllocationCount);
        Assert.Equal(0, dto.ReceiptVoidedAllocationCount);
        Assert.Equal(CustomerSalesInvoiceCollectionAllocationRules.LinkageUnallocated, dto.ReceiptAllocationStatus);
        Assert.Empty(dto.ReceiptAllocations);
        Assert.DoesNotContain("已付", dto.ReceiptAllocationText);
        Assert.DoesNotContain("已结清", dto.ReceiptAllocationText);
    }

    [Fact]
    public async Task 发票详情_部分分摊_已分摊与未分摊分别标注()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var invoice = SeedInvoice(db, "INV-001", customer.Id, grossAmount: 1000m);
        var receipt = SeedReceipt(db, "SK-001", customer.Id, amount: 1000m);
        SeedAllocation(db, invoice, receipt, 300m);
        var controller = InvoiceController(db);

        var dto = AssertOk<CustomerSalesInvoiceEvidenceDto>(await controller.GetById(invoice.Id));

        Assert.Equal(300m, dto.ReceiptAllocatedAmount);
        Assert.Equal(700m, dto.ReceiptUnallocatedAmount);
        Assert.Equal(1, dto.ReceiptAllocationCount);
        Assert.Equal(0, dto.ReceiptVoidedAllocationCount);
        Assert.Equal(CustomerSalesInvoiceCollectionAllocationRules.LinkagePartial, dto.ReceiptAllocationStatus);
        var row = Assert.Single(dto.ReceiptAllocations);
        Assert.True(row.IsActive);
        Assert.Equal("SK-001", row.ReceiptNo);
        Assert.Equal(300m, row.AllocatedAmount);
    }

    [Fact]
    public async Task 发票详情_全额分摊_未分摊为零()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var invoice = SeedInvoice(db, "INV-001", customer.Id, grossAmount: 1000m);
        var receipt = SeedReceipt(db, "SK-001", customer.Id, amount: 1000m);
        SeedAllocation(db, invoice, receipt, 1000m);
        var controller = InvoiceController(db);

        var dto = AssertOk<CustomerSalesInvoiceEvidenceDto>(await controller.GetById(invoice.Id));

        Assert.Equal(1000m, dto.ReceiptAllocatedAmount);
        Assert.Equal(0m, dto.ReceiptUnallocatedAmount);
        Assert.Equal(CustomerSalesInvoiceCollectionAllocationRules.LinkageFullyAllocated, dto.ReceiptAllocationStatus);
    }

    [Fact]
    public async Task 发票详情_已作废分摊行不计入有效合计但历史可读()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var invoice = SeedInvoice(db, "INV-001", customer.Id, grossAmount: 1000m);
        var receipt = SeedReceipt(db, "SK-001", customer.Id, amount: 1000m);
        SeedAllocation(db, invoice, receipt, 300m, CustomerSalesInvoiceCollectionAllocationRules.StatusVoided, "登记错误");
        var controller = InvoiceController(db);

        var dto = AssertOk<CustomerSalesInvoiceEvidenceDto>(await controller.GetById(invoice.Id));

        Assert.Equal(0m, dto.ReceiptAllocatedAmount);
        Assert.Equal(1000m, dto.ReceiptUnallocatedAmount);
        Assert.Equal(0, dto.ReceiptAllocationCount);
        Assert.Equal(1, dto.ReceiptVoidedAllocationCount);
        Assert.Equal(CustomerSalesInvoiceCollectionAllocationRules.LinkageUnallocated, dto.ReceiptAllocationStatus);
        var row = Assert.Single(dto.ReceiptAllocations);
        Assert.True(row.IsVoided);
        Assert.False(row.IsActive);
        Assert.Contains("登记错误", row.VoidReason);
    }

    // ==================== 2. 币种分组（不合并、不换算、不改派） ====================

    [Fact]
    public async Task 多币种_各自原币汇总_绝不合并换算()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var usdInvoice = SeedInvoice(db, "INV-USD", customer.Id, currency: "USD", grossAmount: 1000m);
        var cnyInvoice = SeedInvoice(db, "INV-CNY", customer.Id, currency: "CNY", grossAmount: 6000m);
        var usdReceipt = SeedReceipt(db, "SK-USD", customer.Id, currency: "USD", amount: 1000m);
        var cnyReceipt = SeedReceipt(db, "SK-CNY", customer.Id, currency: "CNY", amount: 6000m);
        SeedAllocation(db, usdInvoice, usdReceipt, 400m);
        SeedAllocation(db, cnyInvoice, cnyReceipt, 2000m);
        var controller = InvoiceController(db);

        var usd = AssertOk<CustomerSalesInvoiceEvidenceDto>(await controller.GetById(usdInvoice.Id));
        var cny = AssertOk<CustomerSalesInvoiceEvidenceDto>(await controller.GetById(cnyInvoice.Id));

        Assert.Equal(400m, usd.ReceiptAllocatedAmount);
        Assert.Equal(600m, usd.ReceiptUnallocatedAmount);
        Assert.Equal(2000m, cny.ReceiptAllocatedAmount);
        Assert.Equal(4000m, cny.ReceiptUnallocatedAmount);
    }

    // ==================== 3. 台账有界汇总（不逐行查库、不逐行装载明细） ====================

    [Fact]
    public async Task 台账_有界收款分摊汇总_不逐行装载明细()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var inv1 = SeedInvoice(db, "INV-001", customer.Id, grossAmount: 1000m);
        var inv2 = SeedInvoice(db, "INV-002", customer.Id, grossAmount: 2000m);
        var receipt = SeedReceipt(db, "SK-001", customer.Id, amount: 3000m);
        SeedAllocation(db, inv1, receipt, 400m);
        SeedAllocation(db, inv2, receipt, 1500m);
        var controller = InvoiceController(db);

        var page = AssertOk<PagedResult<CustomerSalesInvoiceEvidenceDto>>(
            await controller.GetPaged(new CustomerSalesInvoiceEvidenceQuery()));

        Assert.Equal(2, page.Total);
        var item1 = page.Items.Single(x => x.Id == inv1.Id);
        var item2 = page.Items.Single(x => x.Id == inv2.Id);

        Assert.Equal(400m, item1.ReceiptAllocatedAmount);
        Assert.Equal(600m, item1.ReceiptUnallocatedAmount);
        Assert.Equal(1, item1.ReceiptAllocationCount);
        Assert.Empty(item1.ReceiptAllocations); // 台账不逐行装载明细，仅聚合汇总

        Assert.Equal(1500m, item2.ReceiptAllocatedAmount);
        Assert.Equal(500m, item2.ReceiptUnallocatedAmount);
        Assert.Equal(1, item2.ReceiptAllocationCount);
        Assert.Empty(item2.ReceiptAllocations);
    }

    // ==================== 4. 来源记录非变更 ====================

    [Fact]
    public async Task 读取发票详情不改写发票_收款单_分摊行与客户()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var invoice = SeedInvoice(db, "INV-001", customer.Id, grossAmount: 1000m);
        var receipt = SeedReceipt(db, "SK-001", customer.Id, amount: 1000m);
        var allocation = SeedAllocation(db, invoice, receipt, 300m);
        var controller = InvoiceController(db);

        await controller.GetById(invoice.Id);

        var invoiceAfter = await db.CustomerSalesInvoiceEvidences.AsNoTracking().SingleAsync();
        var receiptAfter = await db.FinanceReceipts.AsNoTracking().SingleAsync();
        var allocAfter = await db.CustomerSalesInvoiceCollectionAllocations.AsNoTracking().SingleAsync();
        var customerAfter = await db.BaseCustomers.AsNoTracking().SingleAsync();

        Assert.Equal(1000m, invoiceAfter.GrossAmount);
        Assert.Equal(CustomerSalesInvoiceEvidenceRules.StatusRecorded, invoiceAfter.Status);
        Assert.Equal(1000m, receiptAfter.Amount);
        Assert.Equal(DocumentStatus.Approved, receiptAfter.Status);
        Assert.Equal(300m, allocAfter.AllocatedAmount);
        Assert.Equal(CustomerSalesInvoiceCollectionAllocationRules.StatusActive, allocAfter.Status);
        Assert.Equal(100000m, customerAfter.CreditLimit);
    }

    // ==================== 5. 收款单工作流：发票分摊证据与剩余收款证据（只读，复用既有接口） ====================

    [Fact]
    public async Task 收款单汇总_暴露发票分摊与剩余收款证据且不改写收款单()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var invoice = SeedInvoice(db, "INV-001", customer.Id, grossAmount: 1000m);
        var receipt = SeedReceipt(db, "SK-001", customer.Id, amount: 1000m);
        SeedAllocation(db, invoice, receipt, 300m);

        var summary = await CustomerSalesInvoiceCollectionAllocationService.GetReceiptSummaryAsync(db, receipt.Id);

        Assert.Equal("SK-001", summary.ReceiptNo);
        Assert.Equal(1000m, summary.ReceiptAmount);
        Assert.Equal(300m, summary.AllocatedAmount);
        Assert.Equal(700m, summary.UnallocatedAmount);
        Assert.Equal(1, summary.AllocationCount);
        var row = Assert.Single(summary.Allocations);
        Assert.Equal(invoice.Id, row.CustomerSalesInvoiceEvidenceId);
        Assert.Equal(300m, row.AllocatedAmount);

        var receiptAfter = await db.FinanceReceipts.AsNoTracking().SingleAsync();
        Assert.Equal(1000m, receiptAfter.Amount);
        Assert.Equal(DocumentStatus.Approved, receiptAfter.Status);
    }
}
