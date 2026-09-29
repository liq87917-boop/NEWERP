using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Reflection;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-111 客户销项发票收款时效证据（只读派生）单元测试：
/// 部分 / 全额分摊、多张收款取最早与最晚有效收款、收款单取消 / 早于开票日期 / 币种不一致 / 已作废仅作异常列出且不计入、
/// 分页有界与稳定排序、批量取数（固定次数数据集访问、无逐单查库）、只读不写库、业务员数据范围、接口端点与前端接线契约。
/// <para>说明：全部使用内存数据库，不连接 SQL Server、不启动 API、不执行任何 SQL / seed，不运行浏览器验收。</para>
/// </summary>
public class CustomerInvoiceCollectionTimingTests
{
    private static readonly DateTime InvoiceDay = new(2026, 8, 20);

    // ==================== 1. 部分 / 全额分摊 ====================

    [Fact]
    public async Task 部分分摊_可比较已分摊与剩余正确()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var invoice = SeedInvoice(db, "INV-001", customer.Id, grossAmount: 1000m);
        var receipt = SeedReceipt(db, "SK-001", customer.Id, amount: 1000m);
        SeedAllocation(db, invoice, receipt, 300m);

        var report = await CustomerInvoiceCollectionTimingService.ForQueryAsync(db, Query());

        var item = Assert.Single(report.Items);
        Assert.Equal(300m, item.ComparableAllocatedAmount);
        Assert.Equal(700m, item.ComparableRemainingAmount);
        Assert.Equal(1, item.ComparableAllocationCount);
        Assert.Equal("SK-001", item.FirstReceiptNo);
        Assert.Equal("SK-001", item.LastReceiptNo);
        Assert.False(item.IsAnomalous);
    }

    [Fact]
    public async Task 全额分摊_可比较剩余为0()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var invoice = SeedInvoice(db, "INV-001", customer.Id, grossAmount: 1000m);
        var receipt = SeedReceipt(db, "SK-001", customer.Id, amount: 1000m);
        SeedAllocation(db, invoice, receipt, 1000m);

        var report = await CustomerInvoiceCollectionTimingService.ForQueryAsync(db, Query());

        var item = Assert.Single(report.Items);
        Assert.Equal(1000m, item.ComparableAllocatedAmount);
        Assert.Equal(0m, item.ComparableRemainingAmount);
        Assert.Equal(1, item.ComparableAllocationCount);
    }

    // ==================== 2. 多张收款：取最早与最晚有效收款 ====================

    [Fact]
    public async Task 多张收款_取最早与最晚有效收款日期与间隔()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var invoice = SeedInvoice(db, "INV-001", customer.Id, grossAmount: 1000m);
        var early = SeedReceipt(db, "SK-EARLY", customer.Id, amount: 500m, receiptDate: InvoiceDay.AddDays(6));
        var mid = SeedReceipt(db, "SK-MID", customer.Id, amount: 500m, receiptDate: InvoiceDay.AddDays(36));
        var late = SeedReceipt(db, "SK-LATE", customer.Id, amount: 500m, receiptDate: InvoiceDay.AddDays(41));
        SeedAllocation(db, invoice, early, 300m);
        SeedAllocation(db, invoice, mid, 300m);
        SeedAllocation(db, invoice, late, 400m);

        var report = await CustomerInvoiceCollectionTimingService.ForQueryAsync(db, Query());

        var item = Assert.Single(report.Items);
        Assert.Equal("SK-EARLY", item.FirstReceiptNo);
        Assert.Equal(InvoiceDay.AddDays(6), item.FirstReceiptDate);
        Assert.Equal(6, item.FirstCollectionDays);
        Assert.Equal("SK-LATE", item.LastReceiptNo);
        Assert.Equal(InvoiceDay.AddDays(41), item.LastReceiptDate);
        Assert.Equal(41, item.LastCollectionDays);
        Assert.Equal(1000m, item.ComparableAllocatedAmount);
        Assert.Equal(0m, item.ComparableRemainingAmount);
        Assert.Equal(3, item.ComparableAllocationCount);
    }

    // ==================== 3. 异常证据：取消 / 早于开票 / 币种不一致 / 已作废 ====================

    [Fact]
    public async Task 收款单取消_不计入且单列异常()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var invoice = SeedInvoice(db, "INV-001", customer.Id, grossAmount: 1000m);
        var cancelled = SeedReceipt(db, "SK-CANCEL", customer.Id, amount: 300m,
            status: DocumentStatus.Cancelled);
        SeedAllocation(db, invoice, cancelled, 300m);

        var report = await CustomerInvoiceCollectionTimingService.ForQueryAsync(db, Query());

        var item = Assert.Single(report.Items);
        Assert.Null(item.FirstReceiptNo);
        Assert.Null(item.LastReceiptNo);
        Assert.Equal(0m, item.ComparableAllocatedAmount);
        Assert.Equal(1000m, item.ComparableRemainingAmount);
        Assert.Equal(1, item.CancelledCount);
        Assert.True(item.IsAnomalous);
        Assert.Contains(CustomerInvoiceCollectionTimingRules.AnomalyCancelledReceipt, item.Anomalies);
    }

    [Fact]
    public async Task 早于开票日期的收款_不计入且单列异常()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var invoice = SeedInvoice(db, "INV-001", customer.Id, grossAmount: 1000m);
        var pre = SeedReceipt(db, "SK-PRE", customer.Id, amount: 200m, receiptDate: InvoiceDay.AddDays(-3));
        SeedAllocation(db, invoice, pre, 200m);

        var report = await CustomerInvoiceCollectionTimingService.ForQueryAsync(db, Query());

        var item = Assert.Single(report.Items);
        Assert.Null(item.FirstReceiptNo);
        Assert.Equal(1, item.PreInvoiceCount);
        Assert.True(item.IsAnomalous);
        Assert.Contains(CustomerInvoiceCollectionTimingRules.AnomalyPreInvoiceReceipt, item.Anomalies);
    }

    [Fact]
    public async Task 币种不一致的收款_不计入且单列异常()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var invoice = SeedInvoice(db, "INV-001", customer.Id, currency: "USD", grossAmount: 1000m);
        var cnyReceipt = SeedReceipt(db, "SK-CNY", customer.Id, currency: "CNY", amount: 400m);
        SeedAllocation(db, invoice, cnyReceipt, 400m);

        var report = await CustomerInvoiceCollectionTimingService.ForQueryAsync(db, Query());

        var item = Assert.Single(report.Items);
        Assert.Null(item.FirstReceiptNo);
        Assert.Equal(0m, item.ComparableAllocatedAmount);
        Assert.Equal(1, item.CurrencyConflictCount);
        Assert.True(item.IsAnomalous);
        Assert.Contains(CustomerInvoiceCollectionTimingRules.AnomalyCurrencyConflict, item.Anomalies);
    }

    [Fact]
    public async Task 已作废分摊行_不计入且单列异常()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var invoice = SeedInvoice(db, "INV-001", customer.Id, grossAmount: 1000m);
        var receipt = SeedReceipt(db, "SK-001", customer.Id, amount: 500m);
        SeedAllocation(db, invoice, receipt, 500m, status: CustomerSalesInvoiceCollectionAllocationRules.StatusVoided);

        var report = await CustomerInvoiceCollectionTimingService.ForQueryAsync(db, Query());

        var item = Assert.Single(report.Items);
        Assert.Null(item.FirstReceiptNo);
        Assert.Equal(1, item.VoidedCount);
        Assert.True(item.IsAnomalous);
        Assert.Contains(CustomerInvoiceCollectionTimingRules.AnomalyVoidedAllocation, item.Anomalies);
    }


    // ==================== 4. 分页有界与稳定排序 ====================

    [Fact]
    public async Task 分页有界_按客户与开票日期稳定排序()
    {
        using var db = TestDbFactory.Create();
        var customerA = SeedCustomer(db, "C001", "甲客户");
        var customerB = SeedCustomer(db, "C002", "乙客户");
        SeedInvoice(db, "INV-A1", customerA.Id, invoiceDate: InvoiceDay.AddDays(1));
        SeedInvoice(db, "INV-A2", customerA.Id, invoiceDate: InvoiceDay.AddDays(2));
        SeedInvoice(db, "INV-B1", customerB.Id, invoiceDate: InvoiceDay.AddDays(3));

        var page1 = await CustomerInvoiceCollectionTimingService.ForQueryAsync(db, Query(page: 1, pageSize: 2));

        Assert.Equal(3, page1.Total);
        Assert.Equal(2, page1.Items.Count);
        Assert.Equal("INV-A1", page1.Items[0].InvoiceNumber);
        Assert.Equal("INV-A2", page1.Items[1].InvoiceNumber);

        var page2 = await CustomerInvoiceCollectionTimingService.ForQueryAsync(db, Query(page: 2, pageSize: 2));
        var item = Assert.Single(page2.Items);
        Assert.Equal("INV-B1", item.InvoiceNumber);
    }

    // ==================== 5. 只读：不写库 ====================

    [Fact]
    public async Task 读取收款时效_不改写发票_收款单_分摊行与客户()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var invoice = SeedInvoice(db, "INV-001", customer.Id, grossAmount: 1000m);
        var receipt = SeedReceipt(db, "SK-001", customer.Id, amount: 1000m);
        var allocation = SeedAllocation(db, invoice, receipt, 300m);

        await CustomerInvoiceCollectionTimingService.ForQueryAsync(db, Query());

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

    // ==================== 6. 批量取数（固定次数数据集访问、无逐单查库） ====================

    [Fact]
    public async Task 批量取数_固定次数数据集访问_无逐单查库且不写库()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        for (var i = 1; i <= 5; i++)
        {
            var invoice = SeedInvoice(db, $"INV-00{i}", customer.Id, grossAmount: 1000m);
            var receipt = SeedReceipt(db, $"SK-00{i}", customer.Id, amount: 1000m, receiptDate: InvoiceDay.AddDays(i));
            SeedAllocation(db, invoice, receipt, 200m);
        }

        var counting = CountingDbContext.Wrap(db);

        var report = await CustomerInvoiceCollectionTimingService.ForQueryAsync(counting.Proxy, Query(pageSize: 20));

        Assert.Equal(5, report.Total);
        Assert.Equal(5, report.Items.Count);
        Assert.Equal(0, counting.WriteCalls);
        Assert.True(counting.DatasetReads <= 5, $"数据集访问次数应固定有界，实际 {counting.DatasetReads} 次");
    }

    // ==================== 7. 业务员数据范围 ====================

    [Fact]
    public async Task 业务员数据范围_只返回被分配客户发票()
    {
        using var db = TestDbFactory.Create();
        var employee = new BaseEmployee { EmployeeCode = "EMP1", EmployeeName = "业务员一", IsSalesman = true, Status = 1 };
        db.BaseEmployees.Add(employee);
        db.SaveChanges();

        var user = new SysUser { UserName = "EMP1", PasswordHash = "hash", PasswordSalt = "salt", DisplayName = "EMP1", Status = UserStatus.Enabled };
        db.SysUsers.Add(user);
        db.SaveChanges();
        var role = new SysRole { RoleName = "业务员", RoleCode = "Sales", IsSystem = false };
        db.SysRoles.Add(role);
        db.SaveChanges();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        db.SaveChanges();

        var allowed = SeedCustomer(db, "C-ALLOWED", "被分配客户", empId: employee.Id);
        var denied = SeedCustomer(db, "C-DENIED", "未分配客户", empId: null);
        SeedInvoice(db, "INV-ALLOWED", allowed.Id);
        SeedInvoice(db, "INV-DENIED", denied.Id);

        var controller = new CustomerSalesInvoiceEvidenceController(db);
        TestAuth.SetUser(controller, user.Id);

        var report = AssertOk<CustomerInvoiceCollectionTimingReport>(await controller.CollectionTiming(Query()));

        var item = Assert.Single(report.Items);
        Assert.Equal("INV-ALLOWED", item.InvoiceNumber);
    }

    // ==================== 8. 接口端点与前端接线契约 ====================

    [Fact]
    public void 接口端点与前端接线契约()
    {
        var method = typeof(CustomerSalesInvoiceEvidenceController).GetMethod(nameof(CustomerSalesInvoiceEvidenceController.CollectionTiming));
        Assert.NotNull(method);
        Assert.NotNull(method!.GetCustomAttribute<HttpGetAttribute>());

        var js = JsDirectory();
        var timingJs = File.ReadAllText(Path.Combine(js, "customer-invoice-collection-timing.js"));
        Assert.Contains("function openCustomerInvoiceCollectionTiming(", timingJs);
        Assert.Contains("/api/customer-sales-invoices/collection-timing", timingJs);

        var invoiceJs = File.ReadAllText(Path.Combine(js, "customer-sales-invoices.js"));
        Assert.Contains("openCustomerInvoiceCollectionTiming(", invoiceJs);

        var index = File.ReadAllText(Path.Combine(js, "..", "index.html"));
        Assert.Contains("/js/customer-invoice-collection-timing.js", index);
    }


    // ==================== 助手 ====================

    private static CustomerInvoiceCollectionTimingQuery Query(
        long? customerId = null, DateTime? from = null, DateTime? to = null,
        string? keyword = null, int page = 1, int pageSize = 50)
        => new()
        {
            CustomerId = customerId,
            InvoiceDateFrom = from,
            InvoiceDateTo = to,
            Keyword = keyword,
            Page = page,
            PageSize = pageSize,
        };

    private static string JsDirectory() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "..", "..", "..", "..", "..", "src", "ERP.Api", "wwwroot", "js"));

    private static T AssertOk<T>(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<ApiResponse<T>>(ok.Value);
        Assert.Equal(ErrorCodes.Success, resp.Code);
        Assert.NotNull(resp.Data);
        return resp.Data!;
    }

    private static BaseCustomer SeedCustomer(ErpDbContext db, string code, string name, long? empId = null)
    {
        var customer = new BaseCustomer
        {
            CustomerCode = code,
            CustomerName = name,
            Status = 1,
            CreditStatus = "正常",
            CreditLimit = 100000m,
            CreditDays = 30,
            EmpId = empId
        };
        db.BaseCustomers.Add(customer);
        db.SaveChanges();
        return customer;
    }

    private static CustomerSalesInvoiceEvidence SeedInvoice(
        ErpDbContext db, string invoiceNumber, long customerId, string currency = "USD",
        decimal grossAmount = 1000m, DateTime? invoiceDate = null,
        int status = CustomerSalesInvoiceEvidenceRules.StatusRecorded)
    {
        var invoice = new CustomerSalesInvoiceEvidence
        {
            InvoiceType = "普票",
            InvoiceCode = "",
            InvoiceNumber = invoiceNumber,
            NormalizedInvoiceNumber = invoiceNumber.Replace("-", "").Replace("_", "").ToUpperInvariant(),
            InvoiceDate = (invoiceDate ?? InvoiceDay).Date,
            CustomerId = customerId,
            CustomerCode = "C001",
            CustomerName = "义乌进出口",
            Currency = currency,
            NetAmount = grossAmount * 0.9m,
            TaxAmount = grossAmount * 0.1m,
            GrossAmount = grossAmount,
            Status = status
        };
        db.CustomerSalesInvoiceEvidences.Add(invoice);
        db.SaveChanges();
        return invoice;
    }

    private static FinanceReceipt SeedReceipt(
        ErpDbContext db, string receiptNo, long customerId, string currency = "USD",
        decimal amount = 1000m, DocumentStatus status = DocumentStatus.Approved,
        DateTime? receiptDate = null)
    {
        var receipt = new FinanceReceipt
        {
            ReceiptNo = receiptNo,
            ReceiptDate = (receiptDate ?? InvoiceDay.AddDays(36)).Date,
            CustomerId = customerId,
            Amount = amount,
            Currency = currency == "CNY" ? Currency.CNY : Currency.USD,
            PaymentMethod = PaymentMethod.BankTransfer,
            BankAccount = "TEST-ACCOUNT",
            Status = status
        };
        db.FinanceReceipts.Add(receipt);
        db.SaveChanges();
        return receipt;
    }

    private static CustomerSalesInvoiceCollectionAllocation SeedAllocation(
        ErpDbContext db, CustomerSalesInvoiceEvidence invoice, FinanceReceipt receipt, decimal amount,
        int status = CustomerSalesInvoiceCollectionAllocationRules.StatusActive)
    {
        var row = new CustomerSalesInvoiceCollectionAllocation
        {
            CustomerSalesInvoiceEvidenceId = invoice.Id,
            InvoiceType = invoice.InvoiceType,
            InvoiceCode = invoice.InvoiceCode,
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
            ReceiptStatusText = receipt.Status.ToString(),
            ReceiptAmount = receipt.Amount,
            CustomerId = invoice.CustomerId,
            CustomerCode = invoice.CustomerCode,
            CustomerName = invoice.CustomerName,
            AllocatedAmount = amount,
            Currency = invoice.Currency,
            Status = status,
            AllocatedAt = DateTime.Now,
            AllocatedBy = "tester"
        };
        db.CustomerSalesInvoiceCollectionAllocations.Add(row);
        db.SaveChanges();
        return row;
    }


    /// <summary>用于断言「分页 / 有界查询」「无逐单查库」与「只读不写库」；不改动生产代码。</summary>
    public class CountingDbContext : DispatchProxy
    {
        private IErpDbContext _inner = null!;
        public IErpDbContext Proxy { get; private set; } = null!;
        public int DatasetReads { get; private set; }
        public int WriteCalls { get; private set; }

        public static CountingDbContext Wrap(IErpDbContext inner)
        {
            var proxy = DispatchProxy.Create<IErpDbContext, CountingDbContext>();
            var counting = (CountingDbContext)(object)proxy;
            counting._inner = inner;
            counting.Proxy = proxy;
            return counting;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod is null) return null;
            if (targetMethod.Name == nameof(IErpDbContext.SaveChangesAsync))
            {
                WriteCalls++;
                return _inner.SaveChangesAsync(args is { Length: > 0 } ? (CancellationToken)args[0]! : default);
            }
            if (targetMethod.Name.StartsWith("get_", StringComparison.Ordinal)) DatasetReads++;
            return targetMethod.Invoke(_inner, args);
        }
    }
}

