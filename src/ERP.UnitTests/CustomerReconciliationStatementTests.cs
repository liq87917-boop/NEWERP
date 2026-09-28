using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-086 客户对账证据导出（只读派生、有界、分币种）单元测试：无证据、部分 / 全额分摊、
/// 已作废历史、货币 / 收款单失效（未知证据不修复）、多客户多币种（绝不跨币种合并）、
/// 有界筛选（客户 / 发票身份 / 发票日期区间 / 币种 / 证据类 / 分配状态）、授权 fail closed、
/// 来源记录非变更边界、CSV / HTML 边界文案与 as-of / 生成时间、以及未知值不回落为 0 契约。
/// <para>说明：全部使用内存数据库（TestDbFactory），不连接 SQL Server、不执行任何 SQL 或部署脚本、
/// 不做浏览器 / UI 验收（浏览器验收按 browser_deferred 延后到 FINAL-UI-ACCEPTANCE）。</para>
/// </summary>
public class CustomerReconciliationStatementTests
{
    // ==================== 0. 测试脚手架 ====================

    private static readonly DateTime AsOf = new(2026, 9, 25);

    private const long AuthorizedUserId = 9801L;
    private const long NoMenuUserId = 9802L;
    private const long WrongMenuUserId = 9803L;

    private static BaseCustomer SeedCustomer(ErpDbContext db, string code, string name, bool deleted = false)
    {
        var customer = new BaseCustomer
        {
            CustomerCode = code,
            CustomerName = name,
            Status = 1,
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
        ErpDbContext db, string invoiceNumber, long customerId, decimal grossAmount,
        string currency = "USD", int status = CustomerSalesInvoiceEvidenceRules.StatusRecorded,
        bool deleted = false, DateTime? invoiceDate = null)
    {
        var invoice = new CustomerSalesInvoiceEvidence
        {
            InvoiceType = "普票",
            InvoiceCode = string.Empty,
            InvoiceNumber = invoiceNumber,
            NormalizedInvoiceNumber = invoiceNumber.Replace("-", "").ToUpperInvariant(),
            InvoiceDate = invoiceDate ?? new DateTime(2026, 8, 20),
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
        ErpDbContext db, string receiptNo, long customerId, decimal amount,
        Currency currency = Currency.USD, DocumentStatus status = DocumentStatus.Approved,
        bool deleted = false)
    {
        var receipt = new FinanceReceipt
        {
            ReceiptNo = receiptNo,
            ReceiptDate = new DateTime(2026, 9, 20),
            CustomerId = customerId,
            Amount = amount,
            Currency = currency,
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
        ErpDbContext db, CustomerSalesInvoiceEvidence invoice, FinanceReceipt receipt, decimal amount,
        int status = CustomerSalesInvoiceCollectionAllocationRules.StatusActive,
        string? currency = null, long? customerId = null)
    {
        var row = new CustomerSalesInvoiceCollectionAllocation
        {
            CustomerSalesInvoiceEvidenceId = invoice.Id,
            InvoiceType = invoice.InvoiceType,
            InvoiceCode = invoice.InvoiceCode,
            InvoiceNumber = invoice.InvoiceNumber,
            InvoiceDate = invoice.InvoiceDate,
            InvoiceStatus = invoice.Status,
            InvoiceStatusText = "已登记",
            InvoiceGrossAmount = invoice.GrossAmount,
            InvoiceCurrency = invoice.Currency,
            ReceiptId = receipt.Id,
            ReceiptNo = receipt.ReceiptNo,
            ReceiptDate = receipt.ReceiptDate,
            ReceiptStatus = (int)receipt.Status,
            ReceiptStatusText = "已审核",
            ReceiptAmount = receipt.Amount,
            CustomerId = customerId ?? invoice.CustomerId,
            CustomerCode = invoice.CustomerCode,
            CustomerName = invoice.CustomerName,
            AllocatedAmount = amount,
            Currency = currency ?? invoice.Currency,
            Remark = string.Empty,
            Status = status,
            AllocatedAt = new DateTime(2026, 9, 21),
            AllocatedBy = "张三"
        };
        db.CustomerSalesInvoiceCollectionAllocations.Add(row);
        db.SaveChanges();
        return row;
    }

    private static void SeedAuthorization(ErpDbContext db, long userId, params string[] menuCodes)
    {
        var role = new SysRole
        {
            RoleName = $"对账导出测试角色 {userId}",
            RoleCode = $"CSST-{userId}",
            Description = "ERP-086 授权测试",
            IsSystem = false
        };
        db.SysRoles.Add(role);
        db.SaveChanges();

        db.SysUserRoles.Add(new SysUserRole { UserId = userId, RoleId = role.Id });
        db.SaveChanges();

        foreach (var code in menuCodes)
        {
            var menu = new SysMenu
            {
                ParentId = 0,
                MenuName = $"测试菜单 {code}",
                MenuCode = code,
                Path = $"/{code}",
                Icon = "test",
                SortOrder = 1,
                MenuType = MenuType.Menu
            };
            db.SysMenus.Add(menu);
            db.SaveChanges();
            db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
            db.SaveChanges();
        }
    }

    private static CustomerReconciliationStatementController BuildController(ErpDbContext db, long? userId)
    {
        var controller = new CustomerReconciliationStatementController(db);
        var claims = userId.HasValue
            ? new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }
            : Array.Empty<Claim>();
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) }
        };
        return controller;
    }

    private static CustomerReconciliationStatementQuery Query(
        long? customerId = null, string? currency = null, long? invoiceId = null,
        string? evidenceClass = null, string? allocationState = null, string? invoiceStatus = null,
        DateTime? from = null, DateTime? to = null, DateTime? asOf = null, int maxRows = 2000)
        => new()
        {
            CustomerId = customerId,
            Currency = currency,
            InvoiceId = invoiceId,
            EvidenceClass = evidenceClass,
            AllocationState = allocationState,
            InvoiceStatus = invoiceStatus,
            InvoiceDateFrom = from,
            InvoiceDateTo = to,
            AsOfDate = asOf ?? AsOf,
            MaxRows = maxRows,
        };

    private static async Task<BusinessException> AssertBusinessAsync(int expectedCode, Func<Task> action)
    {
        var ex = await Assert.ThrowsAsync<BusinessException>(action);
        Assert.Equal(expectedCode, ex.Code);
        return ex;
    }

    private static CustomerReconciliationStatementRowDto Row(
        CustomerReconciliationStatementDto statement, string invoiceNumber)
        => statement.Rows.Single(r => r.InvoiceNumber == invoiceNumber);

    // ==================== 1. 无证据 / 部分 / 全额分摊与算术剩余 ====================

    [Fact]
    public async Task No_evidence_yields_empty_statement_with_boundary_text()
    {
        using var db = TestDbFactory.Create();
        SeedAuthorization(db, AuthorizedUserId, CustomerReceivableReconciliationRules.RequiredMenuCode);
        var customer = SeedCustomer(db, "C001", "义乌进出口");

        var statement = await CustomerReconciliationStatementService.ForStatementAsync(
            db, Query(customerId: customer.Id), AuthorizedUserId);

        Assert.Empty(statement.Rows);
        Assert.Empty(statement.Currencies);
        Assert.Equal(0, statement.Total);
        Assert.Equal(0, statement.RowCount);
        Assert.False(statement.Truncated);
        Assert.Contains("不是总账", statement.BoundaryText);
        Assert.Contains("不是经审计", statement.BoundaryText);
    }

    [Fact]
    public async Task Partial_allocation_keeps_gross_allocated_and_remainder_separate()
    {
        using var db = TestDbFactory.Create();
        SeedAuthorization(db, AuthorizedUserId, CustomerReceivableReconciliationRules.RequiredMenuCode);
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var invoice = SeedInvoice(db, "INV-P1", customer.Id, 1000m);
        var receipt = SeedReceipt(db, "RC-P1", customer.Id, 600m);
        SeedAllocation(db, invoice, receipt, 600m);

        var statement = await CustomerReconciliationStatementService.ForStatementAsync(
            db, Query(customerId: customer.Id), AuthorizedUserId);

        var row = Row(statement, "INV-P1");
        Assert.True(row.IsActiveEvidence);
        Assert.Equal(1000m, row.InvoiceGrossAmount);
        Assert.Equal(600m, row.EffectiveAllocatedAmount);
        Assert.Equal(400m, row.RemainingAmount);
        Assert.Equal(CustomerReceivableReconciliationRules.AllocationPartial, row.ReceiptAllocationState);
        Assert.Equal(1, row.EffectiveAllocationCount);
        Assert.Equal(1, row.EffectiveReceiptCount);
        Assert.Single(statement.Currencies);
        Assert.Equal(400m, statement.Currencies[0].RemainingAmount);
    }

    [Fact]
    public async Task Full_allocation_remainder_is_zero_not_settled()
    {
        using var db = TestDbFactory.Create();
        SeedAuthorization(db, AuthorizedUserId, CustomerReceivableReconciliationRules.RequiredMenuCode);
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var invoice = SeedInvoice(db, "INV-F1", customer.Id, 1000m);
        var receipt = SeedReceipt(db, "RC-F1", customer.Id, 1000m);
        SeedAllocation(db, invoice, receipt, 1000m);

        var statement = await CustomerReconciliationStatementService.ForStatementAsync(
            db, Query(customerId: customer.Id), AuthorizedUserId);

        var row = Row(statement, "INV-F1");
        Assert.Equal(CustomerReceivableReconciliationRules.AllocationFull, row.ReceiptAllocationState);
        Assert.Equal(0m, row.RemainingAmount);
    }

    [Fact]
    public async Task Voided_allocation_history_is_labeled_and_excluded_from_active_totals()
    {
        using var db = TestDbFactory.Create();
        SeedAuthorization(db, AuthorizedUserId, CustomerReceivableReconciliationRules.RequiredMenuCode);
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var invoice = SeedInvoice(db, "INV-V1", customer.Id, 1000m);
        var receipt = SeedReceipt(db, "RC-V1", customer.Id, 800m);
        SeedAllocation(db, invoice, receipt, 600m);
        SeedAllocation(db, invoice, receipt, 200m, status: CustomerSalesInvoiceCollectionAllocationRules.StatusVoided);

        var statement = await CustomerReconciliationStatementService.ForStatementAsync(
            db, Query(customerId: customer.Id), AuthorizedUserId);

        var row = Row(statement, "INV-V1");
        Assert.Equal(600m, row.EffectiveAllocatedAmount);
        Assert.Equal(1, row.EffectiveAllocationCount);
        Assert.Equal(2, row.ReceiptAllocationTotalRows);
        Assert.Equal(1, row.ReceiptVoidedRows);
        Assert.Equal(400m, row.RemainingAmount);
    }

    [Fact]
    public async Task Currency_mismatch_and_cancelled_receipt_are_not_repaired()
    {
        using var db = TestDbFactory.Create();
        SeedAuthorization(db, AuthorizedUserId, CustomerReceivableReconciliationRules.RequiredMenuCode);
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var invoice = SeedInvoice(db, "INV-X1", customer.Id, 1000m, currency: "USD");
        var mismatchedReceipt = SeedReceipt(db, "RC-X1", customer.Id, 300m, Currency.USD);
        SeedAllocation(db, invoice, mismatchedReceipt, 300m, currency: "CNY"); // 币种不一致 → 无效
        var cancelledReceipt = SeedReceipt(db, "RC-X2", customer.Id, 300m, Currency.USD, DocumentStatus.Cancelled);
        SeedAllocation(db, invoice, cancelledReceipt, 300m); // 收款单已取消 → 无效

        var statement = await CustomerReconciliationStatementService.ForStatementAsync(
            db, Query(customerId: customer.Id), AuthorizedUserId);

        var row = Row(statement, "INV-X1");
        Assert.Equal(0m, row.EffectiveAllocatedAmount);
        Assert.Equal(0, row.EffectiveAllocationCount);
        Assert.Equal(2, row.ReceiptAllocationTotalRows);
        Assert.Equal(CustomerReceivableReconciliationRules.AllocationHistoricalOnly, row.ReceiptAllocationState);
        Assert.Equal(1000m, row.RemainingAmount);
    }

    [Fact]
    public async Task Draft_and_voided_invoices_are_labeled_and_excluded_by_default()
    {
        using var db = TestDbFactory.Create();
        SeedAuthorization(db, AuthorizedUserId, CustomerReceivableReconciliationRules.RequiredMenuCode);
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        SeedInvoice(db, "INV-REC", customer.Id, 1000m);
        SeedInvoice(db, "INV-DRAFT", customer.Id, 500m, status: CustomerSalesInvoiceEvidenceRules.StatusDraft);
        SeedInvoice(db, "INV-VOID", customer.Id, 800m, status: CustomerSalesInvoiceEvidenceRules.StatusVoided);

        var recorded = await CustomerReconciliationStatementService.ForStatementAsync(
            db, Query(customerId: customer.Id), AuthorizedUserId);
        Assert.Single(recorded.Rows);
        Assert.Equal("INV-REC", recorded.Rows[0].InvoiceNumber);

        var all = await CustomerReconciliationStatementService.ForStatementAsync(
            db, Query(customerId: customer.Id, invoiceStatus: CustomerReceivableReconciliationRules.InvoiceStatusAll),
            AuthorizedUserId);
        Assert.Equal(3, all.Rows.Count);
        Assert.True(Row(all, "INV-DRAFT").IsDraft);
        Assert.True(Row(all, "INV-VOID").IsVoided);
        Assert.False(Row(all, "INV-VOID").IsActiveEvidence);
        Assert.Null(Row(all, "INV-VOID").RemainingAmount); // 非有效证据：剩余未知，绝不回落为 0
    }

    // ==================== 2. 多币种 / 有界筛选 / 授权 / 非变更 ====================

    private static string Snapshot(ErpDbContext db)
        => string.Join("|",
            db.CustomerSalesInvoiceEvidences.AsNoTracking().OrderBy(i => i.Id)
                .Select(i => $"{i.Id}:{i.Status}:{i.GrossAmount}:{i.IsDeleted}"))
        + "||" + string.Join("|",
            db.CustomerSalesInvoiceCollectionAllocations.AsNoTracking().OrderBy(a => a.Id)
                .Select(a => $"{a.Id}:{a.Status}:{a.AllocatedAmount}:{a.IsDeleted}"))
        + "||" + string.Join("|",
            db.FinanceReceipts.AsNoTracking().OrderBy(r => r.Id)
                .Select(r => $"{r.Id}:{r.Amount}:{r.Status}:{r.IsDeleted}"))
        + "||" + string.Join("|",
            db.BaseCustomers.AsNoTracking().OrderBy(c => c.Id)
                .Select(c => $"{c.Id}:{c.CustomerName}:{c.IsDeleted}"));

    [Fact]
    public async Task Multiple_currencies_are_grouped_separately_never_combined()
    {
        using var db = TestDbFactory.Create();
        SeedAuthorization(db, AuthorizedUserId, CustomerReceivableReconciliationRules.RequiredMenuCode);
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var usd = SeedInvoice(db, "INV-M1", customer.Id, 1000m, currency: "USD");
        var cny = SeedInvoice(db, "INV-M2", customer.Id, 500m, currency: "CNY");
        SeedAllocation(db, usd, SeedReceipt(db, "RC-M1", customer.Id, 300m, Currency.USD), 300m);
        SeedAllocation(db, cny, SeedReceipt(db, "RC-M2", customer.Id, 500m, Currency.CNY), 500m);

        var statement = await CustomerReconciliationStatementService.ForStatementAsync(
            db, Query(customerId: customer.Id), AuthorizedUserId);

        Assert.Equal(2, statement.Rows.Count);
        Assert.Equal(2, statement.Currencies.Count);
        Assert.Contains(statement.Currencies, c => c.Currency == "USD");
        Assert.Contains(statement.Currencies, c => c.Currency == "CNY");
        Assert.Equal(1000m, statement.Currencies.Single(c => c.Currency == "USD").InvoiceGrossAmount);
        Assert.Equal(500m, statement.Currencies.Single(c => c.Currency == "CNY").InvoiceGrossAmount);
    }

    [Fact]
    public async Task Bounded_filters_by_identity_currency_date_evidence_class_and_allocation()
    {
        using var db = TestDbFactory.Create();
        SeedAuthorization(db, AuthorizedUserId, CustomerReceivableReconciliationRules.RequiredMenuCode);
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var a1 = SeedInvoice(db, "INV-B1", customer.Id, 1000m, invoiceDate: new DateTime(2026, 8, 20));
        var a2 = SeedInvoice(db, "INV-B2", customer.Id, 500m, invoiceDate: new DateTime(2026, 9, 1));
        SeedAllocation(db, a1, SeedReceipt(db, "RC-B1", customer.Id, 400m), 400m);

        var byId = await CustomerReconciliationStatementService.ForStatementAsync(
            db, Query(customerId: customer.Id, invoiceId: a2.Id), AuthorizedUserId);
        Assert.Single(byId.Rows);
        Assert.Equal("INV-B2", byId.Rows[0].InvoiceNumber);

        var byClass = await CustomerReconciliationStatementService.ForStatementAsync(
            db, Query(customerId: customer.Id, evidenceClass: CustomerReconciliationStatementRules.ClassReceiptContext),
            AuthorizedUserId);
        Assert.Single(byClass.Rows);
        Assert.Equal("INV-B1", byClass.Rows[0].InvoiceNumber);

        var none = await CustomerReconciliationStatementService.ForStatementAsync(
            db, Query(customerId: customer.Id, allocationState: CustomerReceivableReconciliationRules.AllocationNone),
            AuthorizedUserId);
        Assert.Single(none.Rows);
        Assert.Equal("INV-B2", none.Rows[0].InvoiceNumber);

        var byDate = await CustomerReconciliationStatementService.ForStatementAsync(
            db, Query(customerId: customer.Id, from: new DateTime(2026, 8, 15), to: new DateTime(2026, 8, 31)),
            AuthorizedUserId);
        Assert.Single(byDate.Rows);
        Assert.Equal("INV-B1", byDate.Rows[0].InvoiceNumber);

        var shipment = await CustomerReconciliationStatementService.ForStatementAsync(
            db, Query(customerId: customer.Id, evidenceClass: CustomerReconciliationStatementRules.ClassShipmentLink),
            AuthorizedUserId);
        Assert.Empty(shipment.Rows); // 无权威持久化出货链接册 → 空结果（fail closed）
    }

    [Fact]
    public async Task Authorization_fails_closed_for_missing_identity_menu_and_deleted_customer()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var deleted = SeedCustomer(db, "C002", "已删除客户", deleted: true);

        // 未登录
        await AssertBusinessAsync(ErrorCodes.Unauthorized,
            () => CustomerReconciliationStatementService.ForStatementAsync(db, Query(), null));

        // 有角色但无菜单授权
        SeedAuthorization(db, NoMenuUserId);
        await AssertBusinessAsync(ErrorCodes.Forbidden,
            () => CustomerReconciliationStatementService.ForStatementAsync(db, Query(customerId: customer.Id), NoMenuUserId));

        // 只有其它模块授权
        SeedAuthorization(db, WrongMenuUserId, "finance-payment");
        await AssertBusinessAsync(ErrorCodes.Forbidden,
            () => CustomerReconciliationStatementService.ForStatementAsync(db, Query(customerId: customer.Id), WrongMenuUserId));

        // 来源客户已删除
        SeedAuthorization(db, AuthorizedUserId, CustomerReceivableReconciliationRules.RequiredMenuCode);
        await AssertBusinessAsync(ErrorCodes.NotFound,
            () => CustomerReconciliationStatementService.ForStatementAsync(db, Query(customerId: deleted.Id), AuthorizedUserId));
    }

    [Fact]
    public async Task Generation_is_read_only_and_does_not_mutate_source_records()
    {
        using var db = TestDbFactory.Create();
        SeedAuthorization(db, AuthorizedUserId, CustomerReceivableReconciliationRules.RequiredMenuCode);
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var invoice = SeedInvoice(db, "INV-N1", customer.Id, 1000m);
        SeedAllocation(db, invoice, SeedReceipt(db, "RC-N1", customer.Id, 600m), 600m);

        var before = Snapshot(db);
        await CustomerReconciliationStatementService.ForStatementAsync(db, Query(customerId: customer.Id), AuthorizedUserId);
        var controller = BuildController(db, AuthorizedUserId);
        var q = Query(customerId: customer.Id);
        q.Format = "csv";
        await controller.Export(q);
        Assert.Equal(before, Snapshot(db));
    }

    [Fact]
    public async Task Export_csv_and_html_include_boundary_asof_and_generation_timestamp()
    {
        using var db = TestDbFactory.Create();
        SeedAuthorization(db, AuthorizedUserId, CustomerReceivableReconciliationRules.RequiredMenuCode);
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var invoice = SeedInvoice(db, "INV-E1", customer.Id, 1000m);
        SeedAllocation(db, invoice, SeedReceipt(db, "RC-E1", customer.Id, 400m), 400m);

        var controller = BuildController(db, AuthorizedUserId);

        var csvQuery = Query(customerId: customer.Id);
        csvQuery.Format = "csv";
        var csvResult = Assert.IsType<FileContentResult>(await controller.Export(csvQuery));
        Assert.Equal("text/csv; charset=utf-8", csvResult.ContentType);
        var csv = System.Text.Encoding.UTF8.GetString(csvResult.FileContents);
        Assert.Contains("客户对账证据导出", csv);
        Assert.Contains("生成时间", csv);
        Assert.Contains("不是总账", csv);
        Assert.Contains("不是经审计", csv);
        Assert.Contains("出货链接证据(ERP-076)", csv);
        Assert.Contains("unknown", csv); // 出货链接证据 fail closed 未知

        var htmlQuery = Query(customerId: customer.Id);
        htmlQuery.Format = "html";
        var htmlResult = Assert.IsType<FileContentResult>(await controller.Export(htmlQuery));
        Assert.Equal("text/html; charset=utf-8", htmlResult.ContentType);
        var html = System.Text.Encoding.UTF8.GetString(htmlResult.FileContents);
        Assert.Contains("客户对账证据导出", html);
        Assert.Contains("不是总账", html);
        Assert.Contains("出货链接证据(ERP-076)", html);
    }
}





