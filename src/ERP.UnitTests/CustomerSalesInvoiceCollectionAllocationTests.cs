using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Reflection;
using System.Security.Claims;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// 客户收款关联销项发票分摊证据单元测试（ERP-073）。覆盖：显式分摊（只按发票证据 Id + 收款单 Id 的持久化标识符）、
/// 发票已登记与收款单未取消的权威资格、客户与币种一致性、部分 / 全额与一对多 / 多对一、重复与双向超额拒绝、
/// 金额精度与正数、作废原因与历史保留、作废后额度释放、两侧汇总、两侧候选、证据维度分离、
/// 有界查询（无逐行查库）、来源记录非变更边界，以及模型 / 幂等结构 / 请求契约 / 前端与路由接线契约。
/// 全部使用内存库（TestDbFactory），不连接 SQL Server、不执行任何 SQL / 部署脚本、不做浏览器 / UI 验收。
/// </summary>
public class CustomerSalesInvoiceCollectionAllocationTests
{
    // ==================== 0. 测试脚手架 ====================

    /// <summary>播种具备既有「收款单」（receipt）菜单的特权账号（数据范围不受限，聚焦分摊证据语义）</summary>
    private static SysMenu EnsureReceiptMenu(ErpDbContext db)
    {
        var menu = db.SysMenus.FirstOrDefault(m => !m.IsDeleted
            && m.MenuCode == CustomerReceiptLifecycleRules.RequiredMenuCode);
        if (menu is not null) return menu;

        menu = new SysMenu
        {
            ParentId = 0,
            MenuCode = CustomerReceiptLifecycleRules.RequiredMenuCode,
            MenuName = CustomerReceiptLifecycleRules.RequiredMenuText,
            Path = "/finance/receipt",
            MenuType = MenuType.Menu
        };
        db.SysMenus.Add(menu);
        db.SaveChanges();
        return menu;
    }

    private static long SeedPrivilegedUser(ErpDbContext db)
    {
        var role = new SysRole
        {
            RoleName = "收款分摊特权角色",
            RoleCode = $"CollAllocPriv-{Guid.NewGuid():N}",
            IsSystem = true
        };
        db.SysRoles.Add(role);
        db.SaveChanges();

        var user = new SysUser
        {
            UserName = $"coll-alloc-priv-{Guid.NewGuid():N}",
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = "收款分摊特权用户",
            Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        db.SaveChanges();

        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        var menu = EnsureReceiptMenu(db);
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
        db.SaveChanges();
        return user.Id;
    }

    /// <summary>绑定特权身份（既有收款单菜单 + 不受限客户范围）的真实 HTTP 请求控制器。</summary>
    private static CustomerSalesInvoiceCollectionAllocationController BuildController(ErpDbContext db, string? userName = null)
    {
        var userId = SeedPrivilegedUser(db);
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId.ToString()) };
        if (userName is not null) claims.Add(new Claim(ClaimTypes.Name, userName));
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"))
        };
        http.Request.Path = "/api/customer-sales-invoice-collection-allocations";
        return new CustomerSalesInvoiceCollectionAllocationController(db)
        {
            ControllerContext = new ControllerContext { HttpContext = http }
        };
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

    private static FinanceReceipt SeedReceipt(
        ErpDbContext db, string receiptNo, long customerId, decimal amount = 1000m,
        Currency currency = Currency.USD, DocumentStatus status = DocumentStatus.Approved, bool deleted = false)
    {
        var receipt = new FinanceReceipt
        {
            ReceiptNo = receiptNo,
            ReceiptDate = new DateTime(2026, 9, 25),
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

    private static CustomerSalesInvoiceEvidence SeedInvoice(
        ErpDbContext db, string invoiceNumber, long customerId, decimal grossAmount = 1000m,
        string currency = "USD", int status = CustomerSalesInvoiceEvidenceRules.StatusRecorded,
        bool deleted = false, string invoiceType = "普票", string invoiceCode = "")
    {
        var invoice = new CustomerSalesInvoiceEvidence
        {
            InvoiceType = invoiceType,
            InvoiceCode = invoiceCode,
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

    private static CustomerSalesInvoiceCollectionAllocationSaveDto SaveDto(
        long invoiceId, long receiptId, decimal amount, string remark = "")
        => new()
        {
            CustomerSalesInvoiceEvidenceId = invoiceId,
            ReceiptId = receiptId,
            AllocatedAmount = amount,
            Remark = remark
        };

    private static T AssertOk<T>(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<ApiResponse<T>>(ok.Value);
        Assert.Equal(ErrorCodes.Success, resp.Code);
        Assert.NotNull(resp.Data);
        return resp.Data!;
    }

    private static async Task<BusinessException> AssertBusinessAsync(int expectedCode, Func<Task> action)
    {
        var ex = await Assert.ThrowsAsync<BusinessException>(action);
        Assert.Equal(expectedCode, ex.Code);
        return ex;
    }

    private static string RepoFile(params string[] segments)
        => Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            Path.Combine(segments)));

    private static async Task<CustomerSalesInvoiceCollectionAllocationDto> CreateAllocationAsync(
        CustomerSalesInvoiceCollectionAllocationController controller, long invoiceId, long receiptId,
        decimal amount, string remark = "")
        => AssertOk<CustomerSalesInvoiceCollectionAllocationDto>(
            await controller.Create(SaveDto(invoiceId, receiptId, amount, remark)));

    // ==================== 1. 登记分摊行：快照与权威资格 ====================

    [Fact]
    public async Task 登记分摊_写入两侧与客户快照_登记人与时间由服务端写入()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var invoice = SeedInvoice(db, "INV-2026-001", customer.Id, grossAmount: 1000m);
        var receipt = SeedReceipt(db, "SK20260925", customer.Id, amount: 1000m);
        var controller = BuildController(db, "张三");

        var first = await CreateAllocationAsync(controller, invoice.Id, receipt.Id, 300m, "首款");

        Assert.Equal(invoice.Id, first.CustomerSalesInvoiceEvidenceId);
        Assert.Equal("INV-2026-001", first.InvoiceNumber);
        Assert.Equal("普票", first.InvoiceType);
        Assert.Equal(1000m, first.InvoiceGrossAmount);
        Assert.Equal("USD", first.InvoiceCurrency);
        Assert.Equal(receipt.Id, first.ReceiptId);
        Assert.Equal("SK20260925", first.ReceiptNo);
        Assert.Equal((int)DocumentStatus.Approved, first.ReceiptStatus);
        Assert.Equal("已审核", first.ReceiptStatusText);
        Assert.Equal(1000m, first.ReceiptAmount);
        Assert.Equal(customer.Id, first.CustomerId);
        Assert.Equal("C001", first.CustomerCode);
        Assert.Equal("义乌进出口", first.CustomerName);
        Assert.Equal(300m, first.AllocatedAmount);
        Assert.Equal("USD", first.Currency);
        Assert.Equal(CustomerSalesInvoiceCollectionAllocationRules.StatusActive, first.Status);
        Assert.True(first.IsActive);
        Assert.False(first.IsVoided);
        Assert.Equal("张三", first.AllocatedBy);
        Assert.True(first.ReceiptAvailable);
        Assert.True(first.InvoiceAvailable);
    }

    [Fact]
    public async Task 无身份时登记人记未知用户()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var invoice = SeedInvoice(db, "INV-2026-001", customer.Id);
        var receipt = SeedReceipt(db, "SK20260925", customer.Id);
        // 进程内直调（无 HTTP 请求管线、无登录身份）：沿用既有语义，登记人记「未知用户」。
        var controller = new CustomerSalesInvoiceCollectionAllocationController(db);

        var created = await CreateAllocationAsync(controller, invoice.Id, receipt.Id, 100m);

        Assert.Equal("未知用户", created.AllocatedBy);
        Assert.Equal("C001", created.CustomerCode);
        Assert.Equal("义乌进出口", created.CustomerName);
    }

    [Fact]
    public async Task 未显式选择发票或收款单被拒绝()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var invoice = SeedInvoice(db, "INV-2026-001", customer.Id);
        var receipt = SeedReceipt(db, "SK20260925", customer.Id);
        var controller = BuildController(db);

        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => controller.Create(SaveDto(0, receipt.Id, 100m)));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => controller.Create(SaveDto(invoice.Id, 0, 100m)));
    }

    [Fact]
    public async Task 发票不存在或已删除被拒绝()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var receipt = SeedReceipt(db, "SK20260925", customer.Id);
        var deletedInvoice = SeedInvoice(db, "INV-DEL", customer.Id, deleted: true);
        var controller = BuildController(db);

        await AssertBusinessAsync(ErrorCodes.NotFound,
            () => controller.Create(SaveDto(999999, receipt.Id, 100m)));
        await AssertBusinessAsync(ErrorCodes.NotFound,
            () => controller.Create(SaveDto(deletedInvoice.Id, receipt.Id, 100m)));
    }

    [Fact]
    public async Task 草稿或已作废的发票不能承接分摊()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var receipt = SeedReceipt(db, "SK20260925", customer.Id);
        var draft = SeedInvoice(db, "INV-DRAFT", customer.Id,
            status: CustomerSalesInvoiceEvidenceRules.StatusDraft);
        var voided = SeedInvoice(db, "INV-VOIDED", customer.Id,
            status: CustomerSalesInvoiceEvidenceRules.StatusVoided);
        var controller = BuildController(db);

        var ex = await AssertBusinessAsync(ErrorCodes.RuleConflict,
            () => controller.Create(SaveDto(draft.Id, receipt.Id, 100m)));
        Assert.Contains("已登记", ex.Message);

        await AssertBusinessAsync(ErrorCodes.RuleConflict,
            () => controller.Create(SaveDto(voided.Id, receipt.Id, 100m)));
    }

    [Fact]
    public async Task 收款单不存在或已删除被拒绝()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var invoice = SeedInvoice(db, "INV-2026-001", customer.Id);
        var deletedReceipt = SeedReceipt(db, "SK-DEL", customer.Id, deleted: true);
        var controller = BuildController(db);

        await AssertBusinessAsync(ErrorCodes.NotFound,
            () => controller.Create(SaveDto(invoice.Id, 999999, 100m)));
        await AssertBusinessAsync(ErrorCodes.NotFound,
            () => controller.Create(SaveDto(invoice.Id, deletedReceipt.Id, 100m)));
    }

    [Fact]
    public async Task 已取消的收款单不能登记新分摊()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var invoice = SeedInvoice(db, "INV-2026-001", customer.Id);
        var cancelled = SeedReceipt(db, "SK-CANCEL", customer.Id, status: DocumentStatus.Cancelled);
        var controller = BuildController(db);

        var ex = await AssertBusinessAsync(ErrorCodes.RuleConflict,
            () => controller.Create(SaveDto(invoice.Id, cancelled.Id, 100m)));
        Assert.Contains("已取消", ex.Message);
    }

    [Fact]
    public async Task 客户不一致被拒绝_不做跨客户合并()
    {
        using var db = TestDbFactory.Create();
        var customerA = SeedCustomer(db, "C001", "义乌进出口");
        var customerB = SeedCustomer(db, "C002", "深圳贸易");
        var invoice = SeedInvoice(db, "INV-2026-001", customerA.Id);
        var receipt = SeedReceipt(db, "SK20260925", customerB.Id);
        var controller = BuildController(db);

        var ex = await AssertBusinessAsync(ErrorCodes.RuleConflict,
            () => controller.Create(SaveDto(invoice.Id, receipt.Id, 100m)));
        Assert.Contains("不一致", ex.Message);
    }

    [Fact]
    public async Task 币种不一致被拒绝且不做汇率换算()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var invoice = SeedInvoice(db, "INV-2026-001", customer.Id, currency: "USD");
        var receipt = SeedReceipt(db, "SK20260925", customer.Id, currency: Currency.CNY);
        var controller = BuildController(db);

        var ex = await AssertBusinessAsync(ErrorCodes.RuleConflict,
            () => controller.Create(SaveDto(invoice.Id, receipt.Id, 100m)));
        Assert.Contains("币种", ex.Message);
    }

    [Fact]
    public async Task 客户已停用时不能登记新分摊()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口", status: 0);
        var invoice = SeedInvoice(db, "INV-2026-001", customer.Id);
        var receipt = SeedReceipt(db, "SK20260925", customer.Id);
        var controller = BuildController(db);

        var ex = await AssertBusinessAsync(ErrorCodes.RuleConflict,
            () => controller.Create(SaveDto(invoice.Id, receipt.Id, 100m)));
        Assert.Contains("已停用", ex.Message);
    }

    [Fact]
    public async Task 分摊金额按币种精度取整且必须大于0()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var invoice = SeedInvoice(db, "INV-2026-001", customer.Id, grossAmount: 1000m);
        var receipt = SeedReceipt(db, "SK20260925", customer.Id, amount: 1000m);
        var controller = BuildController(db);

        var rounded = await CreateAllocationAsync(controller, invoice.Id, receipt.Id, 200.005m);
        Assert.Equal(200.01m, rounded.AllocatedAmount);

        var zero = await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => controller.Create(SaveDto(invoice.Id, receipt.Id, 0m)));
        Assert.Contains("大于 0", zero.Message);

        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => controller.Create(SaveDto(invoice.Id, receipt.Id, -5m)));
    }

    [Fact]
    public async Task 无小数币种按整数取整_备注超长被拒绝()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var invoice = SeedInvoice(db, "INV-JPY", customer.Id, grossAmount: 1000m, currency: "JPY");
        var receipt = SeedReceipt(db, "SK-JPY", customer.Id, amount: 1000m, currency: Currency.JPY);
        var controller = BuildController(db);

        var rounded = await CreateAllocationAsync(controller, invoice.Id, receipt.Id, 100.5m);
        Assert.Equal(101m, rounded.AllocatedAmount);

        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => controller.Create(SaveDto(invoice.Id, receipt.Id, 50m, new string('注', 501))));
    }

    [Fact]
    public async Task 同一发票与收款单重复分摊被拒绝而不是合并()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var invoice = SeedInvoice(db, "INV-2026-001", customer.Id, grossAmount: 1000m);
        var receipt = SeedReceipt(db, "SK20260925", customer.Id, amount: 1000m);
        var controller = BuildController(db);

        await CreateAllocationAsync(controller, invoice.Id, receipt.Id, 300m);
        var ex = await AssertBusinessAsync(ErrorCodes.Duplicate,
            () => controller.Create(SaveDto(invoice.Id, receipt.Id, 200m)));
        Assert.Contains("只允许一条有效分摊行", ex.Message);
    }

    [Fact]
    public async Task 超过收款单可分摊余额被拒绝()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var invoice = SeedInvoice(db, "INV-2026-001", customer.Id, grossAmount: 1000m);
        var receipt = SeedReceipt(db, "SK20260925", customer.Id, amount: 300m);
        var controller = BuildController(db);

        var ex = await AssertBusinessAsync(ErrorCodes.RuleConflict,
            () => controller.Create(SaveDto(invoice.Id, receipt.Id, 301m)));
        Assert.Contains("可分摊余额", ex.Message);
    }

    [Fact]
    public async Task 超过发票未分摊含税额被拒绝()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var invoice = SeedInvoice(db, "INV-2026-001", customer.Id, grossAmount: 300m);
        var receipt = SeedReceipt(db, "SK20260925", customer.Id, amount: 1000m);
        var controller = BuildController(db);

        var ex = await AssertBusinessAsync(ErrorCodes.RuleConflict,
            () => controller.Create(SaveDto(invoice.Id, receipt.Id, 301m)));
        Assert.Contains("未分摊含税额", ex.Message);
    }

    [Fact]
    public async Task 部分分摊_两侧未分摊金额分别可见且不被核销()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var invoice = SeedInvoice(db, "INV-2026-001", customer.Id, grossAmount: 1000m);
        var receipt = SeedReceipt(db, "SK20260925", customer.Id, amount: 1000m);
        var controller = BuildController(db);

        await CreateAllocationAsync(controller, invoice.Id, receipt.Id, 400m, "部分分摊");

        var invoiceSummary = AssertOk<CustomerSalesInvoiceCollectionAllocationInvoiceSummaryDto>(
            await controller.InvoiceSummary(invoice.Id));
        Assert.Equal(400m, invoiceSummary.AllocatedAmount);
        Assert.Equal(600m, invoiceSummary.UnallocatedAmount);
        Assert.Equal("partial", invoiceSummary.LinkageStatus);

        var receiptSummary = AssertOk<CustomerSalesInvoiceCollectionAllocationReceiptSummaryDto>(
            await controller.ReceiptSummary(receipt.Id));
        Assert.Equal(400m, receiptSummary.AllocatedAmount);
        Assert.Equal(600m, receiptSummary.UnallocatedAmount);
    }

    [Fact]
    public async Task 一张收款单分摊到两张发票_一对多()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var invoiceA = SeedInvoice(db, "INV-A", customer.Id, grossAmount: 1000m);
        var invoiceB = SeedInvoice(db, "INV-B", customer.Id, grossAmount: 1000m);
        var receipt = SeedReceipt(db, "SK20260925", customer.Id, amount: 1000m);
        var controller = BuildController(db);

        await CreateAllocationAsync(controller, invoiceA.Id, receipt.Id, 400m);
        await CreateAllocationAsync(controller, invoiceB.Id, receipt.Id, 600m);

        var receiptSummary = AssertOk<CustomerSalesInvoiceCollectionAllocationReceiptSummaryDto>(
            await controller.ReceiptSummary(receipt.Id));
        Assert.Equal(1000m, receiptSummary.AllocatedAmount);
        Assert.Equal(0m, receiptSummary.UnallocatedAmount);
        Assert.Equal(2, receiptSummary.AllocationCount);
        Assert.Equal("fully_allocated", receiptSummary.LinkageStatus);
    }

    [Fact]
    public async Task 两张收款单分摊到同一发票_多对一()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var invoice = SeedInvoice(db, "INV-2026-001", customer.Id, grossAmount: 1000m);
        var receiptA = SeedReceipt(db, "SK-A", customer.Id, amount: 1000m);
        var receiptB = SeedReceipt(db, "SK-B", customer.Id, amount: 1000m);
        var controller = BuildController(db);

        await CreateAllocationAsync(controller, invoice.Id, receiptA.Id, 400m);
        await CreateAllocationAsync(controller, invoice.Id, receiptB.Id, 600m);

        var invoiceSummary = AssertOk<CustomerSalesInvoiceCollectionAllocationInvoiceSummaryDto>(
            await controller.InvoiceSummary(invoice.Id));
        Assert.Equal(1000m, invoiceSummary.AllocatedAmount);
        Assert.Equal(0m, invoiceSummary.UnallocatedAmount);
        Assert.Equal(2, invoiceSummary.AllocationCount);

        var summaryA = AssertOk<CustomerSalesInvoiceCollectionAllocationReceiptSummaryDto>(
            await controller.ReceiptSummary(receiptA.Id));
        Assert.Equal(400m, summaryA.AllocatedAmount);
        Assert.Equal(600m, summaryA.UnallocatedAmount);
    }

    [Fact]
    public async Task 单侧有效行数有界_超限被拒绝()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var receipt = SeedReceipt(db, "SK-MANY-001", customer.Id, amount: 100000m);
        var controller = BuildController(db);

        for (var i = 0; i < CustomerSalesInvoiceCollectionAllocationRules.MaxAllocationsPerReceipt; i++)
        {
            var invoice = SeedInvoice(db, $"INV-BULK-{i:d4}", customer.Id, grossAmount: 10m);
            await CreateAllocationAsync(controller, invoice.Id, receipt.Id, 1m);
        }

        var overflowInvoice = SeedInvoice(db, "INV-OVERFLOW", customer.Id, grossAmount: 10m);
        var ex = await AssertBusinessAsync(ErrorCodes.RuleConflict,
            () => controller.Create(SaveDto(overflowInvoice.Id, receipt.Id, 1m)));
        Assert.Contains("已达上限", ex.Message);
    }

    // ==================== 2. 作废与历史保留 ====================

    [Fact]
    public async Task 作废原因必填且有界_重复作废被拒绝()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var invoice = SeedInvoice(db, "INV-2026-001", customer.Id);
        var receipt = SeedReceipt(db, "SK-001", customer.Id);
        var controller = BuildController(db, "张三");

        var created = await CreateAllocationAsync(controller, invoice.Id, receipt.Id, 100m);

        var empty = await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => controller.Void(created.Id,
                new CustomerSalesInvoiceCollectionAllocationVoidRequest { Reason = "   " }));
        Assert.Contains("请填写作废原因", empty.Message);

        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => controller.Void(created.Id,
                new CustomerSalesInvoiceCollectionAllocationVoidRequest { Reason = new string('原', 501) }));

        AssertOk<CustomerSalesInvoiceCollectionAllocationDto>(
            await controller.Void(created.Id,
                new CustomerSalesInvoiceCollectionAllocationVoidRequest { Reason = "分摊口径更正" }));

        var again = await AssertBusinessAsync(ErrorCodes.RuleConflict,
            () => controller.Void(created.Id,
                new CustomerSalesInvoiceCollectionAllocationVoidRequest { Reason = "再次作废" }));
        Assert.Contains("不能重复作废", again.Message);
    }

    [Fact]
    public async Task 作废保留原始金额快照与登记人_历史仍可读()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var invoice = SeedInvoice(db, "INV-2026-001", customer.Id, grossAmount: 1000m);
        var receipt = SeedReceipt(db, "SK-001", customer.Id, amount: 900m);
        var controller = BuildController(db, "李四");

        var created = await CreateAllocationAsync(controller, invoice.Id, receipt.Id, 288.5m, "首次分摊");
        var allocatedAt = created.AllocatedAt;

        var voided = AssertOk<CustomerSalesInvoiceCollectionAllocationDto>(
            await controller.Void(created.Id,
                new CustomerSalesInvoiceCollectionAllocationVoidRequest { Reason = "收款单选择错误" }));

        Assert.True(voided.IsVoided);
        Assert.False(voided.IsActive);
        Assert.Equal("已作废", voided.StatusText);
        Assert.Equal(288.5m, voided.AllocatedAmount);
        Assert.Equal("288.50 USD", voided.AllocatedAmountText);
        Assert.Equal("首次分摊", voided.Remark);
        Assert.Equal("李四", voided.AllocatedBy);
        Assert.Equal(allocatedAt, voided.AllocatedAt);
        Assert.NotNull(voided.VoidedAt);
        Assert.Equal("收款单选择错误", voided.VoidReason);

        var detail = AssertOk<CustomerSalesInvoiceCollectionAllocationDto>(await controller.GetById(created.Id));
        Assert.Equal(288.5m, detail.AllocatedAmount);
        Assert.Equal("收款单选择错误", detail.VoidReason);

        var invoiceSummary = AssertOk<CustomerSalesInvoiceCollectionAllocationInvoiceSummaryDto>(
            await controller.InvoiceSummary(invoice.Id));
        Assert.Equal(0m, invoiceSummary.AllocatedAmount);
        Assert.Equal(1000m, invoiceSummary.UnallocatedAmount);
        Assert.Equal(1, invoiceSummary.VoidedCount);
    }

    [Fact]
    public async Task 作废后同一组合可重新登记_新旧并存可查且额度已释放()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var invoice = SeedInvoice(db, "INV-2026-001", customer.Id, grossAmount: 1000m);
        var receipt = SeedReceipt(db, "SK-001", customer.Id, amount: 1000m);
        var controller = BuildController(db, "张三");

        var first = await CreateAllocationAsync(controller, invoice.Id, receipt.Id, 1000m);
        AssertOk<CustomerSalesInvoiceCollectionAllocationDto>(
            await controller.Void(first.Id,
                new CustomerSalesInvoiceCollectionAllocationVoidRequest { Reason = "金额口径更正" }));

        var second = await CreateAllocationAsync(controller, invoice.Id, receipt.Id, 600m);

        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(600m, second.AllocatedAmount);

        var rows = await db.CustomerSalesInvoiceCollectionAllocations
            .Where(a => !a.IsDeleted).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Single(rows, a => a.Status == CustomerSalesInvoiceCollectionAllocationRules.StatusActive);
        Assert.Single(rows, a => a.Status == CustomerSalesInvoiceCollectionAllocationRules.StatusVoided);
    }

    [Fact]
    public async Task 分摊行不存在或非法Id时详情与作废被拒绝()
    {
        using var db = TestDbFactory.Create();
        var controller = BuildController(db);

        await AssertBusinessAsync(ErrorCodes.NotFound, () => controller.GetById(999999));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => controller.GetById(0));
        await AssertBusinessAsync(ErrorCodes.NotFound,
            () => controller.Void(999999, new CustomerSalesInvoiceCollectionAllocationVoidRequest { Reason = "x" }));
    }

    // ==================== 3. 台账 / 候选 / 元数据（只读、有界） ====================

    [Fact]
    public async Task 台账支持发票_收款单_客户_状态_币种与关键字过滤()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var invoice = SeedInvoice(db, "INV-2026-001", customer.Id, grossAmount: 1000m);
        var receipt = SeedReceipt(db, "SK-001", customer.Id, amount: 1000m);
        var controller = BuildController(db, "张三");

        await CreateAllocationAsync(controller, invoice.Id, receipt.Id, 300m, "首款");

        var byInvoice = AssertOk<PagedResult<CustomerSalesInvoiceCollectionAllocationDto>>(
            await controller.GetPaged(new CustomerSalesInvoiceCollectionAllocationQuery
            { CustomerSalesInvoiceEvidenceId = invoice.Id }));
        Assert.Single(byInvoice.Items);

        var byReceipt = AssertOk<PagedResult<CustomerSalesInvoiceCollectionAllocationDto>>(
            await controller.GetPaged(new CustomerSalesInvoiceCollectionAllocationQuery
            { ReceiptId = receipt.Id }));
        Assert.Single(byReceipt.Items);

        var byCustomer = AssertOk<PagedResult<CustomerSalesInvoiceCollectionAllocationDto>>(
            await controller.GetPaged(new CustomerSalesInvoiceCollectionAllocationQuery
            { CustomerId = customer.Id }));
        Assert.Single(byCustomer.Items);

        var byKeyword = AssertOk<PagedResult<CustomerSalesInvoiceCollectionAllocationDto>>(
            await controller.GetPaged(new CustomerSalesInvoiceCollectionAllocationQuery
            { Keyword = "INV-2026" }));
        Assert.Single(byKeyword.Items);

        var byMissingKeyword = AssertOk<PagedResult<CustomerSalesInvoiceCollectionAllocationDto>>(
            await controller.GetPaged(new CustomerSalesInvoiceCollectionAllocationQuery
            { Keyword = "不存在" }));
        Assert.Empty(byMissingKeyword.Items);
    }

    [Fact]
    public async Task 收款单候选_必须给出客户与币种_只返回未删除未取消()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var receipt = SeedReceipt(db, "SK-001", customer.Id, amount: 1000m);
        SeedReceipt(db, "SK-CANCEL", customer.Id, status: DocumentStatus.Cancelled);
        var controller = BuildController(db);

        var noCustomer = await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => controller.ReceiptCandidates(0, "USD", null, 200));
        Assert.Contains("客户", noCustomer.Message);

        var options = AssertOk<List<CustomerSalesInvoiceCollectionAllocationReceiptCandidateDto>>(
            await controller.ReceiptCandidates(customer.Id, "USD", null, 200));
        Assert.Single(options);
        Assert.Equal(receipt.Id, options[0].ReceiptId);
        Assert.True(options[0].Eligible);
        Assert.Equal(1000m, options[0].UnallocatedAmount);
    }

    [Fact]
    public async Task 发票候选_只列已登记发票_草稿与已作废不出现()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var recorded = SeedInvoice(db, "INV-REC", customer.Id, grossAmount: 1000m);
        SeedInvoice(db, "INV-DRAFT", customer.Id,
            status: CustomerSalesInvoiceEvidenceRules.StatusDraft);
        SeedInvoice(db, "INV-VOID", customer.Id,
            status: CustomerSalesInvoiceEvidenceRules.StatusVoided);
        var controller = BuildController(db);

        var options = AssertOk<List<CustomerSalesInvoiceCollectionAllocationInvoiceCandidateDto>>(
            await controller.InvoiceCandidates(customer.Id, "USD", null, 200));
        Assert.Single(options);
        Assert.Equal(recorded.Id, options[0].CustomerSalesInvoiceEvidenceId);
        Assert.True(options[0].Eligible);
    }

    [Fact]
    public async Task 元数据_有界额度与口径文案与文档同源()
    {
        using var db = TestDbFactory.Create();
        var controller = BuildController(db);

        var metadata = AssertOk<CustomerSalesInvoiceCollectionAllocationMetadataDto>(
            await controller.Metadata());

        Assert.Equal(CustomerSalesInvoiceCollectionAllocationRules.MaxAllocationsPerInvoice,
            metadata.MaxAllocationsPerInvoice);
        Assert.Equal(CustomerSalesInvoiceCollectionAllocationRules.MaxAllocationsPerReceipt,
            metadata.MaxAllocationsPerReceipt);
        Assert.Equal(CustomerSalesInvoiceCollectionAllocationService.MaxReceiptCandidates,
            metadata.MaxReceiptCandidates);
        Assert.Equal(CustomerSalesInvoiceCollectionAllocationService.MaxInvoiceCandidates,
            metadata.MaxInvoiceCandidates);
        Assert.Equal(CustomerSalesInvoiceCollectionAllocationQuery.MaxPageSize, metadata.MaxPageSize);
        Assert.Equal(CustomerSalesInvoiceCollectionAllocationRules.RuleText, metadata.RuleText);
        Assert.Equal(CustomerSalesInvoiceCollectionAllocationRules.BoundaryText, metadata.BoundaryText);
    }

    // ==================== 4. 非变更边界与证据维度分离 ====================

    [Fact]
    public async Task 登记与作废不改写收款单_发票_客户_订单与收款引用记录()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var invoice = SeedInvoice(db, "INV-2026-001", customer.Id, grossAmount: 1000m);
        var receipt = SeedReceipt(db, "SK-001", customer.Id, amount: 1000m);
        var order = new SalesOrder
        {
            OrderNo = "SO-001",
            OrderDate = new DateTime(2026, 8, 15),
            CustomerId = customer.Id,
            Currency = Currency.USD,
            TotalAmount = 1500m,
            Status = DocumentStatus.Approved
        };
        db.SalesOrders.Add(order);
        db.CustomerReceiptAllocations.Add(new CustomerReceiptAllocation
        {
            ReceiptId = receipt.Id,
            ReceiptNo = receipt.ReceiptNo,
            ReceiptDate = receipt.ReceiptDate,
            ReceiptStatus = (int)receipt.Status,
            ReceiptStatusText = "已审核",
            ReceiptAmount = receipt.Amount,
            SalesOrderId = order.Id,
            OrderNo = order.OrderNo,
            OrderDate = order.OrderDate,
            OrderStatus = (int)order.Status,
            OrderCurrency = "USD",
            CustomerId = customer.Id,
            CustomerCode = "C001",
            CustomerName = "义乌进出口",
            AllocatedAmount = 700m,
            Currency = "USD",
            Status = CustomerReceiptAllocationRules.StatusActive,
            AllocatedAt = DateTime.Now.AddDays(-1)
        });
        await db.SaveChangesAsync();

        var receiptBefore = await db.FinanceReceipts.AsNoTracking().SingleAsync();
        var invoiceBefore = await db.CustomerSalesInvoiceEvidences.AsNoTracking().SingleAsync();
        var customerBefore = await db.BaseCustomers.AsNoTracking().SingleAsync();
        var orderBefore = await db.SalesOrders.AsNoTracking().SingleAsync();
        var receiptAllocationBefore = await db.CustomerReceiptAllocations.AsNoTracking().SingleAsync();

        var controller = BuildController(db, "张三");
        var created = await CreateAllocationAsync(controller, invoice.Id, receipt.Id, 300m, "登记不改写来源");
        AssertOk<CustomerSalesInvoiceCollectionAllocationDto>(
            await controller.Void(created.Id,
                new CustomerSalesInvoiceCollectionAllocationVoidRequest { Reason = "更正" }));

        var receiptAfter = await db.FinanceReceipts.AsNoTracking().SingleAsync();
        var invoiceAfter = await db.CustomerSalesInvoiceEvidences.AsNoTracking().SingleAsync();
        var customerAfter = await db.BaseCustomers.AsNoTracking().SingleAsync();
        var orderAfter = await db.SalesOrders.AsNoTracking().SingleAsync();
        var receiptAllocationAfter = await db.CustomerReceiptAllocations.AsNoTracking().SingleAsync();

        Assert.Equal(receiptBefore.Amount, receiptAfter.Amount);
        Assert.Equal(receiptBefore.Status, receiptAfter.Status);
        Assert.Equal(invoiceBefore.GrossAmount, invoiceAfter.GrossAmount);
        Assert.Equal(invoiceBefore.Status, invoiceAfter.Status);
        Assert.Equal(customerBefore.CreditLimit, customerAfter.CreditLimit);
        Assert.Equal(orderBefore.TotalAmount, orderAfter.TotalAmount);
        Assert.Equal(receiptAllocationBefore.AllocatedAmount, receiptAllocationAfter.AllocatedAmount);
    }

    [Fact]
    public async Task 证据维度分离_销售订单收款引用不占用本维度可分摊余额且绝不相加()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var invoice = SeedInvoice(db, "INV-2026-001", customer.Id, grossAmount: 1000m);
        var receipt = SeedReceipt(db, "SK-001", customer.Id, amount: 1000m);
        var order = new SalesOrder
        {
            OrderNo = "SO-001",
            OrderDate = new DateTime(2026, 8, 15),
            CustomerId = customer.Id,
            Currency = Currency.USD,
            TotalAmount = 1500m,
            Status = DocumentStatus.Approved
        };
        db.SalesOrders.Add(order);
        // ERP-053 维度：同一收款单已引用 700 到销售订单（另一个证据维度）
        db.CustomerReceiptAllocations.Add(new CustomerReceiptAllocation
        {
            ReceiptId = receipt.Id,
            ReceiptNo = receipt.ReceiptNo,
            ReceiptDate = receipt.ReceiptDate,
            ReceiptStatus = (int)receipt.Status,
            ReceiptStatusText = "已审核",
            ReceiptAmount = receipt.Amount,
            SalesOrderId = order.Id,
            OrderNo = order.OrderNo,
            OrderDate = order.OrderDate,
            OrderStatus = (int)order.Status,
            OrderCurrency = "USD",
            CustomerId = customer.Id,
            CustomerCode = "C001",
            CustomerName = "义乌进出口",
            AllocatedAmount = 700m,
            Currency = "USD",
            Status = CustomerReceiptAllocationRules.StatusActive,
            AllocatedAt = DateTime.Now.AddDays(-1)
        });
        await db.SaveChangesAsync();

        var controller = BuildController(db, "张三");
        // 本维度仍可分摊收款单全部 1000（ERP-053 的 700 不占用本维度额度）
        await CreateAllocationAsync(controller, invoice.Id, receipt.Id, 1000m);

        var receiptSummary = AssertOk<CustomerSalesInvoiceCollectionAllocationReceiptSummaryDto>(
            await controller.ReceiptSummary(receipt.Id));
        Assert.Equal(1000m, receiptSummary.AllocatedAmount);
        Assert.Equal(0m, receiptSummary.UnallocatedAmount);
    }

    // ==================== 5. 模型 / 幂等结构 / 请求 / 前端契约 ====================

    [Fact]
    public void 模型结构_长度精度唯一约束无外键且读取标注不落库()
    {
        using var db = TestDbFactory.Create();

        var entityType = db.Model.FindEntityType(typeof(CustomerSalesInvoiceCollectionAllocation));
        Assert.NotNull(entityType);

        Assert.Equal(20, entityType!.FindProperty(nameof(CustomerSalesInvoiceCollectionAllocation.InvoiceType))!
            .GetMaxLength());
        Assert.Equal(50, entityType.FindProperty(nameof(CustomerSalesInvoiceCollectionAllocation.InvoiceNumber))!
            .GetMaxLength());
        Assert.Equal(30, entityType.FindProperty(nameof(CustomerSalesInvoiceCollectionAllocation.InvoiceStatusText))!
            .GetMaxLength());
        Assert.Equal(50, entityType.FindProperty(nameof(CustomerSalesInvoiceCollectionAllocation.ReceiptNo))!
            .GetMaxLength());
        Assert.Equal(200, entityType.FindProperty(nameof(CustomerSalesInvoiceCollectionAllocation.CustomerName))!
            .GetMaxLength());
        Assert.Equal(18, entityType.FindProperty(nameof(CustomerSalesInvoiceCollectionAllocation.InvoiceGrossAmount))!
            .GetPrecision());
        Assert.Equal(2, entityType.FindProperty(nameof(CustomerSalesInvoiceCollectionAllocation.AllocatedAmount))!
            .GetScale());

        var unique = entityType.GetIndexes().Single(i =>
            i.GetDatabaseName() == "UX_CustomerSalesInvoiceCollectionAllocations_InvoiceReceipt");
        Assert.True(unique.IsUnique);
        Assert.Equal("IsDeleted = 0 AND Status <> 2", unique.GetFilter());

        Assert.DoesNotContain(entityType.GetProperties(),
            p => p.Name is nameof(CustomerSalesInvoiceCollectionAllocation.ReceiptAvailable)
                or nameof(CustomerSalesInvoiceCollectionAllocation.InvoiceAvailable)
                or nameof(CustomerSalesInvoiceCollectionAllocation.AmountDecimals)
                or nameof(CustomerSalesInvoiceCollectionAllocation.AmountText)
                or nameof(CustomerSalesInvoiceCollectionAllocation.StatusText));

        var receiptType = db.Model.FindEntityType(typeof(FinanceReceipt));
        Assert.NotNull(receiptType);
        Assert.DoesNotContain(receiptType!.GetNavigations(),
            n => n.ClrType == typeof(CustomerSalesInvoiceCollectionAllocation));
        var invoiceType = db.Model.FindEntityType(typeof(CustomerSalesInvoiceEvidence));
        Assert.NotNull(invoiceType);
        Assert.DoesNotContain(invoiceType!.GetNavigations(),
            n => n.ClrType == typeof(CustomerSalesInvoiceCollectionAllocation));
    }

    [Fact]
    public void 命名契约_收款引用模型仍只有ERP053一套_本模型用Collection命名不破坏审计()
    {
        var receiptAllocationSets = typeof(IErpDbContext).GetProperties()
            .Where(p => p.Name.Contains("Receipt", StringComparison.Ordinal)
                        && p.Name.Contains("Allocation", StringComparison.Ordinal))
            .ToList();
        Assert.Single(receiptAllocationSets);
        Assert.Equal("CustomerReceiptAllocations", receiptAllocationSets[0].Name);
        Assert.Equal(typeof(DbSet<CustomerReceiptAllocation>), receiptAllocationSets[0].PropertyType);

        var collectionSets = typeof(IErpDbContext).GetProperties()
            .Where(p => p.Name == "CustomerSalesInvoiceCollectionAllocations")
            .ToList();
        Assert.Single(collectionSets);
        Assert.Equal(typeof(DbSet<CustomerSalesInvoiceCollectionAllocation>), collectionSets[0].PropertyType);
    }

    [Fact]
    public void Schema_第45段幂等建表建索引且不含任何回填或账务语句()
    {
        var script = File.ReadAllText(
            RepoFile("src", "ERP.Infrastructure", "Data", "SchemaUpgrader.cs"));

        Assert.Contains("IF OBJECT_ID('db_owner.CustomerSalesInvoiceCollectionAllocations') IS NULL", script);
        Assert.Contains("CREATE TABLE db_owner.CustomerSalesInvoiceCollectionAllocations", script);
        Assert.Contains("InvoiceGrossAmount DECIMAL(18,2) NOT NULL DEFAULT 0", script);
        Assert.Contains("AllocatedAmount DECIMAL(18,2) NOT NULL DEFAULT 0", script);
        Assert.Contains("CREATE UNIQUE INDEX UX_CustomerSalesInvoiceCollectionAllocations_InvoiceReceipt", script);
        Assert.Contains("WHERE IsDeleted = 0 AND Status <> 2;", script);

        Assert.DoesNotContain("FK_CustomerSalesInvoiceCollectionAllocations", script);
        Assert.DoesNotContain("ALTER TABLE db_owner.FinanceReceipt", script);
        Assert.DoesNotContain("ALTER TABLE db_owner.CustomerSalesInvoiceEvidences", script);

        var start = script.IndexOf("// 45. 客户收款 → 客户销项发票证据 分摊登记", StringComparison.Ordinal);
        Assert.True(start > 0);
        var segment = script[start..];
        Assert.DoesNotContain("ALTER TABLE", segment);
        Assert.DoesNotContain("UPDATE db_owner", segment);
        Assert.DoesNotContain("INSERT INTO db_owner", segment);
        Assert.DoesNotContain("DELETE FROM db_owner", segment);
    }

    [Fact]
    public void 请求契约_不提供快照或账务字段()
    {
        var props = typeof(CustomerSalesInvoiceCollectionAllocationSaveDto).GetProperties()
            .Select(p => p.Name).ToHashSet();
        Assert.Equal(4, props.Count);
        Assert.Contains(nameof(CustomerSalesInvoiceCollectionAllocationSaveDto.CustomerSalesInvoiceEvidenceId), props);
        Assert.Contains(nameof(CustomerSalesInvoiceCollectionAllocationSaveDto.ReceiptId), props);
        Assert.Contains(nameof(CustomerSalesInvoiceCollectionAllocationSaveDto.AllocatedAmount), props);
        Assert.Contains(nameof(CustomerSalesInvoiceCollectionAllocationSaveDto.Remark), props);
        Assert.DoesNotContain("CustomerCode", props);
        Assert.DoesNotContain("Currency", props);
        Assert.DoesNotContain("Status", props);
        Assert.DoesNotContain("AllocatedBy", props);
    }

    [Fact]
    public void 前端与路由接线契约()
    {
        var index = File.ReadAllText(RepoFile("src", "ERP.Api", "wwwroot", "index.html"));
        Assert.Contains("/js/customer-sales-invoice-collection-allocations.js", index);

        var js = File.ReadAllText(RepoFile(
            "src", "ERP.Api", "wwwroot", "js", "customer-sales-invoice-collection-allocations.js"));
        Assert.Contains("async function openCustomerSalesInvoiceCollectionAllocationRegister", js);
        Assert.Contains("'/api/customer-sales-invoice-collection-allocations'", js);
        Assert.Contains("'/api/customer-sales-invoice-collection-allocations?'", js);
        Assert.Contains("/receipts?", js);
        Assert.Contains("/invoices/", js);
        Assert.Contains("/summary", js);
        Assert.Contains("/void", js);
        Assert.Contains("csicaCreate", js);
        Assert.Contains("csicaConfirmVoid", js);
        Assert.Contains("不是到账凭证", js);
        Assert.Contains("绝不相加", js);
        Assert.Contains("不会被静默核销或改派", js);

        var invoiceJs = File.ReadAllText(RepoFile(
            "src", "ERP.Api", "wwwroot", "js", "customer-sales-invoices.js"));
        Assert.Contains("openCustomerSalesInvoiceCollectionAllocationRegister(${inv.id})", invoiceJs);

        var controller = File.ReadAllText(RepoFile(
            "src", "ERP.Api", "Controllers", "CustomerSalesInvoiceCollectionAllocationController.cs"));
        Assert.Contains("[Route(\"api/customer-sales-invoice-collection-allocations\")]", controller);
        Assert.Contains("[HttpGet(\"metadata\")]", controller);
        Assert.Contains("[HttpGet(\"receipts\")]", controller);
        Assert.Contains("[HttpGet(\"invoices\")]", controller);
        Assert.Contains("[HttpGet(\"receipts/{receiptId:long}/summary\")]", controller);
        Assert.Contains("[HttpGet(\"invoices/{customerSalesInvoiceEvidenceId:long}/summary\")]", controller);
        Assert.Contains("{id:long}/void", controller);
        Assert.Contains("CurrentUserName()", controller);
    }

    // ==================== 9. ERP-434 实时授权与客户数据范围 ====================

    private static long SeedRestrictedSalesman(ErpDbContext db, string userName, bool grantMenu)
    {
        var role = new SysRole
        {
            RoleName = "收款分摊业务员角色",
            RoleCode = $"CollAllocSales-{Guid.NewGuid():N}",
            IsSystem = false
        };
        db.SysRoles.Add(role);
        db.SaveChanges();

        var user = new SysUser
        {
            UserName = userName,
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = userName,
            Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        db.SaveChanges();

        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        if (grantMenu)
        {
            var menu = EnsureReceiptMenu(db);
            db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
        }
        db.BaseEmployees.Add(new BaseEmployee
        {
            EmployeeCode = userName,
            EmployeeName = userName,
            IsSalesman = true,
            Status = 1
        });
        db.SaveChanges();
        return user.Id;
    }

    private static void RevokeMenus(ErpDbContext db, long userId)
    {
        var roleIds = db.SysUserRoles.AsNoTracking()
            .Where(ur => ur.UserId == userId && !ur.IsDeleted).Select(ur => ur.RoleId).ToList();
        foreach (var grant in db.SysRoleMenus.Where(rm => !rm.IsDeleted && roleIds.Contains(rm.RoleId)).ToList())
            grant.IsDeleted = true;
        db.SaveChanges();
    }

    /// <summary>绑定真实 HTTP 请求管线（Request.Path 已赋值）+ 指定身份（可空 = 匿名请求）。</summary>
    private static CustomerSalesInvoiceCollectionAllocationController BuildControllerWithUser(
        ErpDbContext db, long? userId)
    {
        var claims = userId.HasValue
            ? new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }
            : Array.Empty<Claim>();
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"))
        };
        http.Request.Path = "/api/customer-sales-invoice-collection-allocations";
        return new CustomerSalesInvoiceCollectionAllocationController(db)
        {
            ControllerContext = new ControllerContext { HttpContext = http }
        };
    }

    [Fact]
    public async Task 认证_缺失停用删除撤权身份与无菜单一律拒绝且不落行()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var invoice = SeedInvoice(db, "INV-001", customer.Id, grossAmount: 1000m);
        var receipt = SeedReceipt(db, "SK-001", customer.Id, amount: 1000m);

        var disabledId = SeedPrivilegedUser(db);
        (await db.SysUsers.SingleAsync(u => u.Id == disabledId)).Status = UserStatus.Disabled;

        var deletedId = SeedPrivilegedUser(db);
        (await db.SysUsers.SingleAsync(u => u.Id == deletedId)).IsDeleted = true;

        var revokedId = SeedPrivilegedUser(db);
        RevokeMenus(db, revokedId);
        await db.SaveChangesAsync();

        var noMenuId = SeedRestrictedSalesman(db, $"erp434n-nomenu-{Guid.NewGuid():N}", grantMenu: false);

        // 缺失 / 非法 / 已删除身份 → 未认证（每一条路由都在任何读取 / 写入之前拒绝）
        foreach (long? userId in new long?[] { null, 0, deletedId })
        {
            var controller = BuildControllerWithUser(db, userId);
            await AssertBusinessAsync(ErrorCodes.Unauthorized,
                () => controller.GetPaged(new CustomerSalesInvoiceCollectionAllocationQuery()));
            await AssertBusinessAsync(ErrorCodes.Unauthorized, () => controller.Metadata());
            await AssertBusinessAsync(ErrorCodes.Unauthorized,
                () => controller.ReceiptCandidates(customer.Id, "USD", null, 50));
            await AssertBusinessAsync(ErrorCodes.Unauthorized,
                () => controller.InvoiceCandidates(customer.Id, "USD", null, 50));
            await AssertBusinessAsync(ErrorCodes.Unauthorized, () => controller.ReceiptSummary(receipt.Id));
            await AssertBusinessAsync(ErrorCodes.Unauthorized, () => controller.AllocationsForReceipt(receipt.Id));
            await AssertBusinessAsync(ErrorCodes.Unauthorized, () => controller.InvoiceSummary(invoice.Id));
            await AssertBusinessAsync(ErrorCodes.Unauthorized, () => controller.AllocationsForInvoice(invoice.Id));
            await AssertBusinessAsync(ErrorCodes.Unauthorized, () => controller.GetById(1));
            await AssertBusinessAsync(ErrorCodes.Unauthorized,
                () => controller.Create(SaveDto(invoice.Id, receipt.Id, 10m)));
            await AssertBusinessAsync(ErrorCodes.Unauthorized,
                () => controller.Void(1, new CustomerSalesInvoiceCollectionAllocationVoidRequest { Reason = "作废" }));
        }

        // 禁用 / 撤销菜单 / 无菜单 → 权限不足
        foreach (var userId in new[] { disabledId, revokedId, noMenuId })
        {
            var controller = BuildControllerWithUser(db, userId);
            await AssertBusinessAsync(ErrorCodes.Forbidden,
                () => controller.GetPaged(new CustomerSalesInvoiceCollectionAllocationQuery()));
            await AssertBusinessAsync(ErrorCodes.Forbidden,
                () => controller.Create(SaveDto(invoice.Id, receipt.Id, 10m)));
            await AssertBusinessAsync(ErrorCodes.Forbidden,
                () => controller.Void(1, new CustomerSalesInvoiceCollectionAllocationVoidRequest { Reason = "作废" }));
        }

        Assert.Equal(0, await db.CustomerSalesInvoiceCollectionAllocations.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task 认证_自有范围登记与作废成功_越范围与已删除收款单发票同一条错误且零变更()
    {
        using var db = TestDbFactory.Create();
        var ownName = $"erp434n-own-{Guid.NewGuid():N}";
        var salesmanId = SeedRestrictedSalesman(db, ownName, grantMenu: true);
        var employeeId = (await db.BaseEmployees.SingleAsync(e => e.EmployeeCode == ownName)).Id;

        var ownCustomer = SeedCustomer(db, "C-OWN", "自有客户");
        ownCustomer.EmpId = employeeId;
        var foreignCustomer = SeedCustomer(db, "C-FGN", "他人客户");
        await db.SaveChangesAsync();

        var ownReceipt = SeedReceipt(db, "SK-OWN", ownCustomer.Id, amount: 1000m);
        var ownReceipt2 = SeedReceipt(db, "SK-OWN2", ownCustomer.Id, amount: 1000m);
        var foreignReceipt = SeedReceipt(db, "SK-FGN", foreignCustomer.Id, amount: 1000m);
        var deletionReceipt = SeedReceipt(db, "SK-DEL", ownCustomer.Id, amount: 1000m);
        var ownInvoice = SeedInvoice(db, "INV-OWN", ownCustomer.Id, grossAmount: 1000m);
        var ownInvoice2 = SeedInvoice(db, "INV-OWN2", ownCustomer.Id, grossAmount: 1000m);
        var foreignInvoice = SeedInvoice(db, "INV-FGN", foreignCustomer.Id, grossAmount: 1000m);
        var deletionInvoice = SeedInvoice(db, "INV-DEL", ownCustomer.Id, grossAmount: 1000m);

        // 特权账号先建立越范围与即将软删除收款单/发票的有效分摊行
        var privileged = BuildController(db);
        var ownRow = AssertOk<CustomerSalesInvoiceCollectionAllocationDto>(
            await privileged.Create(SaveDto(ownInvoice.Id, ownReceipt.Id, 50m)));
        var foreignRow = AssertOk<CustomerSalesInvoiceCollectionAllocationDto>(
            await privileged.Create(SaveDto(foreignInvoice.Id, foreignReceipt.Id, 50m)));
        var deletionRow = AssertOk<CustomerSalesInvoiceCollectionAllocationDto>(
            await privileged.Create(SaveDto(deletionInvoice.Id, deletionReceipt.Id, 50m)));

        deletionReceipt.IsDeleted = true;
        deletionInvoice.IsDeleted = true;
        await db.SaveChangesAsync();

        var controller = BuildControllerWithUser(db, salesmanId);

        // 自有范围内：登记与作废均被许可（被许可的生命周期）
        var ownNew = AssertOk<CustomerSalesInvoiceCollectionAllocationDto>(
            await controller.Create(SaveDto(ownInvoice2.Id, ownReceipt2.Id, 20m)));
        Assert.True(ownNew.IsActive);
        var voided = AssertOk<CustomerSalesInvoiceCollectionAllocationDto>(
            await controller.Void(ownRow.Id, new CustomerSalesInvoiceCollectionAllocationVoidRequest { Reason = "录错" }));
        Assert.True(voided.IsVoided);

        // 越范围收款单：与不存在收款单同一条不披露错误（不泄露范围外 Id / 金额 / 计数）
        var foreignDenied = await AssertBusinessAsync(ErrorCodes.NotFound,
            () => controller.ReceiptSummary(foreignReceipt.Id));
        var missingDenied = await AssertBusinessAsync(ErrorCodes.NotFound,
            () => controller.ReceiptSummary(987_654_321L));
        Assert.Equal(missingDenied.Message, foreignDenied.Message);
        Assert.Equal(CustomerReceiptLifecycleRules.AllocationNotFoundText, foreignDenied.Message);
        await AssertBusinessAsync(ErrorCodes.NotFound, () => controller.AllocationsForReceipt(foreignReceipt.Id));
        await AssertBusinessAsync(ErrorCodes.NotFound,
            () => controller.Create(SaveDto(ownInvoice2.Id, foreignReceipt.Id, 10m)));
        await AssertBusinessAsync(ErrorCodes.NotFound,
            () => controller.Void(foreignRow.Id,
                new CustomerSalesInvoiceCollectionAllocationVoidRequest { Reason = "越权作废" }));

        // 越范围发票：与不存在发票同一条不披露错误
        var foreignInvoiceDenied = await AssertBusinessAsync(ErrorCodes.NotFound,
            () => controller.InvoiceSummary(foreignInvoice.Id));
        var missingInvoiceDenied = await AssertBusinessAsync(ErrorCodes.NotFound,
            () => controller.InvoiceSummary(987_654_321L));
        Assert.Equal(missingInvoiceDenied.Message, foreignInvoiceDenied.Message);
        await AssertBusinessAsync(ErrorCodes.NotFound, () => controller.AllocationsForInvoice(foreignInvoice.Id));
        await AssertBusinessAsync(ErrorCodes.NotFound, () => controller.InvoiceCandidates(foreignCustomer.Id, "USD", null, 50));

        // 已删除收款单与发票：登记一律按不存在拒绝
        await AssertBusinessAsync(ErrorCodes.NotFound,
            () => controller.Create(SaveDto(deletionInvoice.Id, deletionReceipt.Id, 10m)));
        await AssertBusinessAsync(ErrorCodes.NotFound,
            () => controller.Void(deletionRow.Id,
                new CustomerSalesInvoiceCollectionAllocationVoidRequest { Reason = "已删除作废" }));

        // 证据零变更：被拒行仍为有效、无作废时间
        var storedForeign = await db.CustomerSalesInvoiceCollectionAllocations.AsNoTracking()
            .SingleAsync(a => a.Id == foreignRow.Id);
        Assert.Equal(CustomerSalesInvoiceCollectionAllocationRules.StatusActive, storedForeign.Status);
        Assert.Null(storedForeign.VoidedAt);
        var storedDeletion = await db.CustomerSalesInvoiceCollectionAllocations.AsNoTracking()
            .SingleAsync(a => a.Id == deletionRow.Id);
        Assert.Equal(CustomerSalesInvoiceCollectionAllocationRules.StatusActive, storedDeletion.Status);
        Assert.Null(storedDeletion.VoidedAt);

        // 台账按客户范围过滤：受限业务员只看到自有客户的分摊行
        var ledger = AssertOk<PagedResult<CustomerSalesInvoiceCollectionAllocationDto>>(
            await controller.GetPaged(new CustomerSalesInvoiceCollectionAllocationQuery()));
        Assert.NotEmpty(ledger.Items);
        Assert.All(ledger.Items, i => Assert.Equal(ownCustomer.Id, i.CustomerId));

        // 发票侧详情：越范围分摊行与不存在分摊行按同一条不披露错误拒绝
        var foreignDetail = await AssertBusinessAsync(ErrorCodes.NotFound, () => controller.GetById(foreignRow.Id));
        Assert.Equal(CustomerReceiptLifecycleRules.AllocationNotFoundText, foreignDetail.Message);
    }
}
