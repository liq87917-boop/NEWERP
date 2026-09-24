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
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// 客户收款引用（分摊）登记单元测试（ERP-053）。覆盖：收款单 / 销售订单权威资格（存在、未删除、未取消、
/// 客户一致、币种一致）、金额精度与大于 0、有效行合计不得超过收款单金额、同一订单同一收款单不得重复有效引用、
/// 全额 / 部分引用与未引用金额、作废保留原始值与历史（重复作废拒绝、作废后可重新登记）、
/// 收款单 / 订单软删除后历史仍可读、台账过滤与分页有界、候选金额派生（其他收款单已引用 / 剩余未覆盖）、
/// 非变更边界（收款单、销售订单、客户信用状态、发票、库存、退税、费用均不被改写），
/// ERP-032 审计结论（收款单只有客户级引用 → 仓库中只有一套收款引用模型），
/// 以及模型 / 幂等结构 / 前端接线契约。全部使用内存库（TestDbFactory），不连接 SQL Server、
/// 不执行任何 SQL / 部署脚本、不做任何浏览器 / UI 验收。
/// </summary>
public class CustomerReceiptAllocationTests
{
    // ==================== 0. 测试脚手架 ====================

    private static CustomerReceiptAllocationController BuildController(ErpDbContext db) => new(db);

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
            IsDeleted = deleted
        };
        db.BaseCustomers.Add(customer);
        db.SaveChanges();
        return customer;
    }

    private static SalesOrder SeedOrder(
        ErpDbContext db, string orderNo, long customerId, Currency currency = Currency.USD,
        DocumentStatus status = DocumentStatus.Approved, decimal totalAmount = 1000m,
        string contractNo = "", bool deleted = false, int? rawCurrency = null)
    {
        var order = new SalesOrder
        {
            OrderNo = orderNo,
            OrderDate = new DateTime(2026, 9, 1),
            CustomerId = customerId,
            Currency = rawCurrency is null ? currency : (Currency)rawCurrency.Value,
            TotalAmount = totalAmount,
            ContractNo = contractNo,
            Status = status,
            IsDeleted = deleted
        };
        db.SalesOrders.Add(order);
        db.SaveChanges();
        return order;
    }

    private static FinanceReceipt SeedReceipt(
        ErpDbContext db, string receiptNo, long customerId, decimal amount = 1000m,
        Currency currency = Currency.USD, DocumentStatus status = DocumentStatus.Approved,
        int? rawCurrency = null, bool deleted = false)
    {
        var receipt = new FinanceReceipt
        {
            ReceiptNo = receiptNo,
            ReceiptDate = new DateTime(2026, 9, 10),
            CustomerId = customerId,
            Amount = amount,
            Currency = rawCurrency is null ? currency : (Currency)rawCurrency.Value,
            PaymentMethod = PaymentMethod.BankTransfer,
            Status = status,
            IsDeleted = deleted
        };
        db.FinanceReceipts.Add(receipt);
        db.SaveChanges();
        return receipt;
    }

    private static CustomerReceiptAllocationSaveDto AllocateDto(
        long receiptId, long orderId, decimal amount, string remark = "")
        => new()
        {
            ReceiptId = receiptId,
            SalesOrderId = orderId,
            AllocatedAmount = amount,
            Remark = remark
        };

    /// <summary>断言成功响应并取出数据（业务码必须为 0）</summary>
    private static T AssertOk<T>(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<ApiResponse<T>>(ok.Value);
        Assert.Equal(ErrorCodes.Success, resp.Code);
        Assert.NotNull(resp.Data);
        return resp.Data!;
    }

    /// <summary>断言业务异常的错误码（避免只断言消息文案）</summary>
    private static async Task<BusinessException> AssertBusinessAsync(int expectedCode, Func<Task> action)
    {
        var ex = await Assert.ThrowsAsync<BusinessException>(action);
        Assert.Equal(expectedCode, ex.Code);
        return ex;
    }

    /// <summary>按仓库根目录拼接文件的绝对路径（与其它契约测试口径一致）</summary>
    private static string RepoFile(params string[] segments)
        => Path.GetFullPath(Path.Combine(
            new[] { AppContext.BaseDirectory, "..", "..", "..", "..", ".." }.Concat(segments).ToArray()));

    private static async Task<CustomerReceiptAllocationDto> CreateAllocationAsync(
        CustomerReceiptAllocationController controller, long receiptId, long orderId,
        decimal amount, string remark = "")
        => AssertOk<CustomerReceiptAllocationDto>(
            await controller.Create(AllocateDto(receiptId, orderId, amount, remark)));

    // ==================== 1. 登记：部分 / 全额引用与快照 ====================

    [Fact]
    public async Task 部分引用两张订单_保留服务端快照且不改写收款单与销售订单()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var receipt = SeedReceipt(db, "SK20260910", customer.Id, 1000m);
        var orderA = SeedOrder(db, "SO-A", customer.Id, Currency.USD, DocumentStatus.Approved, 600m);
        var orderB = SeedOrder(db, "SO-B", customer.Id, Currency.USD, DocumentStatus.Approved, 500m);
        var controller = BuildController(db);

        var first = await CreateAllocationAsync(controller, receipt.Id, orderA.Id, 300m, "首款");
        var second = await CreateAllocationAsync(controller, receipt.Id, orderB.Id, 200.005m, "尾款");

        Assert.Equal(receipt.Id, first.ReceiptId);
        Assert.Equal("SK20260910", first.ReceiptNo);
        Assert.Equal(new DateTime(2026, 9, 10), first.ReceiptDate);
        Assert.Equal((int)DocumentStatus.Approved, first.ReceiptStatus);
        Assert.Equal("已审核", first.ReceiptStatusText);
        Assert.Equal(1000m, first.ReceiptAmount);
        Assert.Equal(orderA.Id, first.SalesOrderId);
        Assert.Equal("SO-A", first.OrderNo);
        Assert.Equal("USD", first.Currency);
        Assert.Equal("USD", first.OrderCurrency);
        Assert.Equal(2, first.AmountDecimals);
        Assert.Equal(300m, first.AllocatedAmount);
        Assert.Equal("首款", first.Remark);
        Assert.Equal(CustomerReceiptAllocationRules.StatusActive, first.Status);
        Assert.Equal("有效", first.StatusText);
        Assert.True(first.IsActive);
        Assert.False(first.IsVoided);
        Assert.Equal("C001", first.CustomerCode);
        Assert.Equal("义乌进出口", first.CustomerName);
        Assert.True(first.ReceiptAvailable);
        Assert.True(first.OrderAvailable);
        Assert.Contains("收款单的引用证据", first.BoundaryText);
        Assert.Contains("不是应收账款台账或余额", first.BoundaryText);

        // 金额按币种精度取整（0.5 进位，2 位小数）
        Assert.Equal(200.01m, second.AllocatedAmount);

        // 收款单与销售订单的既有字段完全不被改写
        var storedReceipt = await db.FinanceReceipts.AsNoTracking().FirstAsync(r => r.Id == receipt.Id);
        Assert.Equal(1000m, storedReceipt.Amount);
        Assert.Equal(Currency.USD, storedReceipt.Currency);
        Assert.Equal(DocumentStatus.Approved, storedReceipt.Status);
        Assert.Equal(PaymentMethod.BankTransfer, storedReceipt.PaymentMethod);
        Assert.Equal(receipt.UpdatedAt, storedReceipt.UpdatedAt);

        foreach (var order in await db.SalesOrders.AsNoTracking().ToListAsync())
        {
            Assert.Equal(order.Id == orderA.Id ? 600m : 500m, order.TotalAmount);
            Assert.Equal(DocumentStatus.Approved, order.Status);
        }
    }

    [Fact]
    public async Task 全额引用后收款单汇总显示未引用为0且状态为已全额引用()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var receipt = SeedReceipt(db, "SK-1", customer.Id, 500m);
        var orderA = SeedOrder(db, "SO-A", customer.Id, Currency.USD, DocumentStatus.Approved, 200m);
        var orderB = SeedOrder(db, "SO-B", customer.Id, Currency.USD, DocumentStatus.Approved, 300m);
        var controller = BuildController(db);

        await CreateAllocationAsync(controller, receipt.Id, orderA.Id, 200m);
        await CreateAllocationAsync(controller, receipt.Id, orderB.Id, 300m);

        var summary = AssertOk<CustomerReceiptAllocationReceiptSummaryDto>(
            await controller.ReceiptSummary(receipt.Id));

        Assert.Equal(500m, summary.ReceiptAmount);
        Assert.Equal(500m, summary.AllocatedAmount);
        Assert.Equal(0m, summary.UnallocatedAmount);
        Assert.Equal(2, summary.AllocationCount);
        Assert.Equal(0, summary.VoidedCount);
        Assert.Equal(CustomerReceiptAllocationRules.LinkageFullyAllocated, summary.LinkageStatus);
        Assert.Contains("已全额引用", summary.LinkageText);
        Assert.Contains("不是银行入账", summary.BoundaryText);
        Assert.Equal(2, summary.Allocations.Count);
        Assert.True(summary.ReceiptAvailable);
        Assert.Equal("C001", summary.CustomerCode);
    }

    [Fact]
    public async Task 未引用收款单汇总为未引用且列表为空()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var receipt = SeedReceipt(db, "SK-EMPTY", customer.Id, 120m);
        var controller = BuildController(db);

        var summary = AssertOk<CustomerReceiptAllocationReceiptSummaryDto>(
            await controller.ReceiptSummary(receipt.Id));

        Assert.Equal(0m, summary.AllocatedAmount);
        Assert.Equal(120m, summary.UnallocatedAmount);
        Assert.Equal(0, summary.AllocationCount);
        Assert.Equal(CustomerReceiptAllocationRules.LinkageUnallocated, summary.LinkageStatus);
        Assert.Contains("未被引用", summary.LinkageText);
        Assert.Empty(summary.Allocations);

        var list = AssertOk<List<CustomerReceiptAllocationDto>>(
            await controller.AllocationsForReceipt(receipt.Id));
        Assert.Empty(list);
    }

    // ==================== 2. 权威资格与金额校验 ====================

    [Fact]
    public async Task 客户或币种不一致的订单被拒绝且绝不换算或改派()
    {
        using var db = TestDbFactory.Create();
        var customerA = SeedCustomer(db, "C001", "义乌进出口");
        var customerB = SeedCustomer(db, "C002", "广州贸易");
        var receipt = SeedReceipt(db, "SK-1", customerA.Id, 1000m, Currency.USD);
        var otherCustomerOrder = SeedOrder(db, "SO-OTHER", customerB.Id, Currency.USD);
        var otherCurrencyOrder = SeedOrder(db, "SO-CNY", customerA.Id, Currency.CNY);
        var controller = BuildController(db);

        var customerConflict = await AssertBusinessAsync(ErrorCodes.RuleConflict,
            () => controller.Create(AllocateDto(receipt.Id, otherCustomerOrder.Id, 100m)));
        Assert.Contains("客户", customerConflict.Message);
        Assert.Contains("不一致", customerConflict.Message);

        var currencyConflict = await AssertBusinessAsync(ErrorCodes.RuleConflict,
            () => controller.Create(AllocateDto(receipt.Id, otherCurrencyOrder.Id, 100m)));
        Assert.Contains("币种", currencyConflict.Message);
        Assert.Contains("不做汇率换算", currencyConflict.Message);

        Assert.Empty(await db.CustomerReceiptAllocations.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task 已取消或已删除订单与已删除收款单被拒绝()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var receipt = SeedReceipt(db, "SK-1", customer.Id, 1000m);
        var deletedReceipt = SeedReceipt(db, "SK-DEL", customer.Id, 100m, Currency.USD,
            DocumentStatus.Approved, deleted: true);
        var cancelled = SeedOrder(db, "SO-CANCEL", customer.Id, Currency.USD, DocumentStatus.Cancelled);
        var deleted = SeedOrder(db, "SO-DEL", customer.Id, Currency.USD, DocumentStatus.Approved,
            totalAmount: 100m, deleted: true);
        var controller = BuildController(db);

        var cancelledConflict = await AssertBusinessAsync(ErrorCodes.RuleConflict,
            () => controller.Create(AllocateDto(receipt.Id, cancelled.Id, 100m)));
        Assert.Contains("已取消", cancelledConflict.Message);

        await AssertBusinessAsync(ErrorCodes.NotFound,
            () => controller.Create(AllocateDto(receipt.Id, deleted.Id, 100m)));

        await AssertBusinessAsync(ErrorCodes.NotFound,
            () => controller.Create(AllocateDto(receipt.Id, 999999L, 100m)));

        var deletedReceiptError = await AssertBusinessAsync(ErrorCodes.NotFound,
            () => controller.Create(AllocateDto(deletedReceipt.Id, cancelled.Id, 10m)));
        Assert.Contains("不存在或已删除", deletedReceiptError.Message);

        Assert.Empty(await db.CustomerReceiptAllocations.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task 引用金额合计不得超过收款单金额并给出已引用与本次金额()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var receipt = SeedReceipt(db, "SK-1", customer.Id, 400m, Currency.USD);
        var orderA = SeedOrder(db, "SO-A", customer.Id, Currency.USD, DocumentStatus.Approved, 900m);
        var orderB = SeedOrder(db, "SO-B", customer.Id, Currency.USD, DocumentStatus.Approved, 900m);
        var controller = BuildController(db);

        await CreateAllocationAsync(controller, receipt.Id, orderA.Id, 250m);

        var over = await AssertBusinessAsync(ErrorCodes.RuleConflict,
            () => controller.Create(AllocateDto(receipt.Id, orderB.Id, 150.01m)));
        Assert.Contains("超过收款单金额", over.Message);
        Assert.Contains("已引用 250", over.Message);

        // 恰好等于收款金额边界可以登记（250 + 150 = 400）
        var boundary = await CreateAllocationAsync(controller, receipt.Id, orderB.Id, 150m);
        Assert.Equal(150m, boundary.AllocatedAmount);
    }

    [Fact]
    public async Task 引用金额精度与零值拒绝_未知币种收款单拒绝登记()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var receipt = SeedReceipt(db, "SK-JPY", customer.Id, 1000m, rawCurrency: 6); // JPY：0 位精度
        var order = SeedOrder(db, "SO-JPY", customer.Id, rawCurrency: 6);
        var unsupportedReceipt = SeedReceipt(db, "SK-X", customer.Id, 100m, rawCurrency: 999);
        var controller = BuildController(db);

        // JPY 按币种精度取整（0.5 进位，0 位小数）
        var row = await CreateAllocationAsync(controller, receipt.Id, order.Id, 100.6m);
        Assert.Equal(101m, row.AllocatedAmount);
        Assert.Equal(0, row.AmountDecimals);

        // 金额取整后为 0 → 拒绝
        var zero = await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => controller.Create(AllocateDto(receipt.Id, order.Id, 0.004m)));
        Assert.Contains("必须大于 0", zero.Message);

        // 收款单币种不在系统币种口径内：不能登记（不做汇率换算、不猜测订单）
        var unsupported = await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => controller.Create(AllocateDto(unsupportedReceipt.Id, order.Id, 10m)));
        Assert.Contains("不受支持", unsupported.Message);
    }

    [Fact]
    public async Task 同一订单同一收款单重复有效引用被拒绝_作废后可重新登记()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var receipt = SeedReceipt(db, "SK-1", customer.Id, 1000m);
        var order = SeedOrder(db, "SO-A", customer.Id, Currency.USD, DocumentStatus.Approved, 1000m);
        var controller = BuildController(db);

        var first = await CreateAllocationAsync(controller, receipt.Id, order.Id, 100m);

        var duplicate = await AssertBusinessAsync(ErrorCodes.Duplicate,
            () => controller.Create(AllocateDto(receipt.Id, order.Id, 50m)));
        Assert.Contains("只能有一条有效引用行", duplicate.Message);

        // 作废原行后可以重新登记同一订单的有效引用（已作废行不占用有效额度）
        var voided = AssertOk<CustomerReceiptAllocationDto>(
            await controller.Void(first.Id, new CustomerReceiptAllocationVoidRequest { Reason = "金额录错" }));
        Assert.True(voided.IsVoided);

        var again = await CreateAllocationAsync(controller, receipt.Id, order.Id, 80m);
        Assert.True(again.IsActive);
        Assert.Equal(80m, again.AllocatedAmount);

        // 已作废历史仍然保留（不物理删除）
        var all = await db.CustomerReceiptAllocations.AsNoTracking().ToListAsync();
        Assert.Equal(2, all.Count);
        Assert.Single(all, a => a.Status == CustomerReceiptAllocationRules.StatusVoided);

        // 已作废后收款单汇总只计有效行
        var summary = AssertOk<CustomerReceiptAllocationReceiptSummaryDto>(
            await controller.ReceiptSummary(receipt.Id));
        Assert.Equal(80m, summary.AllocatedAmount);
        Assert.Equal(920m, summary.UnallocatedAmount);
        Assert.Equal(1, summary.AllocationCount);
        Assert.Equal(1, summary.VoidedCount);
        Assert.Equal(CustomerReceiptAllocationRules.LinkagePartial, summary.LinkageStatus);
    }

    // ==================== 3. 作废、历史保留与软删除后的可读性 ====================

    [Fact]
    public async Task 作废必须填原因且长度有界_重复作废被拒绝()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var receipt = SeedReceipt(db, "SK-1", customer.Id, 1000m);
        var order = SeedOrder(db, "SO-A", customer.Id, Currency.USD, DocumentStatus.Approved, 1000m);
        var controller = BuildController(db);

        var row = await CreateAllocationAsync(controller, receipt.Id, order.Id, 250m, "预收款引用");

        // 未填原因 / 全空白 → 拒绝
        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => controller.Void(row.Id, new CustomerReceiptAllocationVoidRequest { Reason = "   " }));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => controller.Void(row.Id, null));

        // 原因超长 → 拒绝（不静默截断）
        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => controller.Void(row.Id, new CustomerReceiptAllocationVoidRequest
            {
                Reason = new string('r', CustomerReceiptAllocationRules.MaxVoidReasonLength + 1)
            }));

        // 备注超长 → 拒绝
        var otherOrder = SeedOrder(db, "SO-B", customer.Id, Currency.USD, DocumentStatus.Approved, 500m);
        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => controller.Create(AllocateDto(receipt.Id, otherOrder.Id, 10m,
                new string('m', CustomerReceiptAllocationRules.MaxRemarkLength + 1))));

        var voided = AssertOk<CustomerReceiptAllocationDto>(
            await controller.Void(row.Id, new CustomerReceiptAllocationVoidRequest { Reason = "订单选错" }));
        Assert.True(voided.IsVoided);
        Assert.False(voided.IsActive);
        Assert.Equal("已作废", voided.StatusText);
        Assert.NotNull(voided.VoidedAt);
        Assert.Equal("订单选错", voided.VoidReason);

        // 原始值 / 快照保留（作废不是删除，也不是重写）
        Assert.Equal(250m, voided.AllocatedAmount);
        Assert.Equal("SK-1", voided.ReceiptNo);
        Assert.Equal("SO-A", voided.OrderNo);
        Assert.Equal("预收款引用", voided.Remark);
        Assert.Equal("C001", voided.CustomerCode);

        // 重复作废被拒绝
        var repeat = await AssertBusinessAsync(ErrorCodes.RuleConflict,
            () => controller.Void(row.Id, new CustomerReceiptAllocationVoidRequest { Reason = "再次作废" }));
        Assert.Contains("不能重复作废", repeat.Message);

        Assert.Single(await db.CustomerReceiptAllocations.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task 收款单或订单软删除取消后历史引用仍可读并标注不可用()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口", status: 0);
        var receipt = SeedReceipt(db, "SK-1", customer.Id, 1000m);
        var order = SeedOrder(db, "SO-A", customer.Id, Currency.USD, DocumentStatus.Approved, 1000m);
        var controller = BuildController(db);

        var row = await CreateAllocationAsync(controller, receipt.Id, order.Id, 100m);

        // 收款单与订单被软删除：历史引用照常可读，只显式标注不可用
        receipt.IsDeleted = true;
        order.IsDeleted = true;
        db.SaveChanges();

        var afterReceiptDelete = AssertOk<CustomerReceiptAllocationDto>(await controller.GetById(row.Id));
        Assert.False(afterReceiptDelete.ReceiptAvailable);
        Assert.Contains("收款单已删除", afterReceiptDelete.ReceiptAvailabilityText);
        Assert.False(afterReceiptDelete.OrderAvailable);
        Assert.Contains("订单已删除", afterReceiptDelete.OrderAvailabilityText);
        Assert.Contains("客户已停用", afterReceiptDelete.OrderAvailabilityText);

        order.IsDeleted = false;
        order.Status = DocumentStatus.Cancelled;
        db.SaveChanges();

        var afterCancel = AssertOk<CustomerReceiptAllocationDto>(await controller.GetById(row.Id));
        Assert.True(afterCancel.OrderAvailable);
        Assert.Contains("订单已取消", afterCancel.OrderAvailabilityText);

        // 台账仍然包含该历史行（默认含不可用来源的历史证据）
        var ledger = AssertOk<PagedResult<CustomerReceiptAllocationDto>>(
            await controller.GetPaged(new CustomerReceiptAllocationQuery()));
        Assert.Equal(1, ledger.Total);
        Assert.Equal(100m, ledger.Items[0].AllocatedAmount);
    }

    [Fact]
    public async Task 登记与作废不改写客户信用状态订单进度与其它单据且无请求期回填()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var receipt = SeedReceipt(db, "SK-1", customer.Id, 1000m);
        var order = SeedOrder(db, "SO-A", customer.Id, Currency.USD, DocumentStatus.Approved, 1000m);
        var controller = BuildController(db);

        var customerBefore = await db.BaseCustomers.AsNoTracking().SingleAsync();
        var receiptBefore = await db.FinanceReceipts.AsNoTracking().SingleAsync();
        var orderBefore = await db.SalesOrders.AsNoTracking().SingleAsync();
        var countsBefore = new[]
        {
            await db.BaseTaxRefunds.CountAsync(),
            await db.FinanceExpenses.CountAsync(),
            await db.PurchaseInvoices.CountAsync(),
            await db.StockMovements.CountAsync(),
            await db.SalesOrderChangeRequests.CountAsync(),
            await db.DocumentAttachmentReferences.CountAsync(),
            await db.SupplierPaymentAllocations.CountAsync()
        };

        var row = await CreateAllocationAsync(controller, receipt.Id, order.Id, 250m, "预收款引用");
        AssertOk<CustomerReceiptAllocationDto>(
            await controller.Void(row.Id, new CustomerReceiptAllocationVoidRequest { Reason = "订单选错" }));

        var customerAfter = await db.BaseCustomers.AsNoTracking().SingleAsync();
        Assert.Equal(customerBefore.CustomerName, customerAfter.CustomerName);
        Assert.Equal(customerBefore.Status, customerAfter.Status);
        Assert.Equal(customerBefore.CreditStatus, customerAfter.CreditStatus);
        Assert.Equal(customerBefore.CreditLimit, customerAfter.CreditLimit);
        Assert.Equal(customerBefore.CreditDays, customerAfter.CreditDays);
        Assert.Equal(customerBefore.Remark, customerAfter.Remark);

        var receiptAfter = await db.FinanceReceipts.AsNoTracking().SingleAsync();
        Assert.Equal(receiptBefore.ReceiptNo, receiptAfter.ReceiptNo);
        Assert.Equal(receiptBefore.ReceiptDate, receiptAfter.ReceiptDate);
        Assert.Equal(receiptBefore.CustomerId, receiptAfter.CustomerId);
        Assert.Equal(receiptBefore.Amount, receiptAfter.Amount);
        Assert.Equal(receiptBefore.Currency, receiptAfter.Currency);
        Assert.Equal(receiptBefore.PaymentMethod, receiptAfter.PaymentMethod);
        Assert.Equal(receiptBefore.BankAccount, receiptAfter.BankAccount);
        Assert.Equal(receiptBefore.Status, receiptAfter.Status);
        Assert.Equal(receiptBefore.Remark, receiptAfter.Remark);
        Assert.Equal(receiptBefore.IsDeleted, receiptAfter.IsDeleted);

        var orderAfter = await db.SalesOrders.AsNoTracking().SingleAsync();
        Assert.Equal(orderBefore.TotalAmount, orderAfter.TotalAmount);
        Assert.Equal(orderBefore.Status, orderAfter.Status);
        Assert.Equal(orderBefore.DepositAmount, orderAfter.DepositAmount);
        Assert.Equal(orderBefore.ContractNo, orderAfter.ContractNo);
        Assert.Equal(orderBefore.DeliveryDate, orderAfter.DeliveryDate);

        Assert.Equal(countsBefore, new[]
        {
            await db.BaseTaxRefunds.CountAsync(),
            await db.FinanceExpenses.CountAsync(),
            await db.PurchaseInvoices.CountAsync(),
            await db.StockMovements.CountAsync(),
            await db.SalesOrderChangeRequests.CountAsync(),
            await db.DocumentAttachmentReferences.CountAsync(),
            await db.SupplierPaymentAllocations.CountAsync()
        });

        // 引用行保留（作废不是删除），也没有为历史单据生成任何引用行
        Assert.Single(await db.CustomerReceiptAllocations.AsNoTracking().ToListAsync());
    }

    // ==================== 4. 台账过滤、分页有界与候选派生 ====================

    [Fact]
    public async Task 台账按状态币种订单与关键字过滤_分页有界且未知筛选取值拒绝()
    {
        using var db = TestDbFactory.Create();
        var customerA = SeedCustomer(db, "C001", "义乌进出口");
        var customerB = SeedCustomer(db, "C002", "广州贸易");
        var receiptCny = SeedReceipt(db, "SK-CNY", customerA.Id, 5000m, Currency.CNY);
        var receiptUsd = SeedReceipt(db, "SK-USD", customerB.Id, 5000m, Currency.USD);
        var orderCny = SeedOrder(db, "SO-CNY", customerA.Id, Currency.CNY, DocumentStatus.Approved, 5000m);
        var orderUsd = SeedOrder(db, "SO-USD", customerB.Id, Currency.USD, DocumentStatus.Approved, 5000m);
        var controller = BuildController(db);

        await CreateAllocationAsync(controller, receiptCny.Id, orderCny.Id, 100m);
        var usdRow = await CreateAllocationAsync(controller, receiptUsd.Id, orderUsd.Id, 200m);
        AssertOk<CustomerReceiptAllocationDto>(
            await controller.Void(usdRow.Id, new CustomerReceiptAllocationVoidRequest { Reason = "作废测试" }));

        var all = AssertOk<PagedResult<CustomerReceiptAllocationDto>>(
            await controller.GetPaged(new CustomerReceiptAllocationQuery()));
        Assert.Equal(2, all.Total);
        Assert.Equal(1, all.Page);
        Assert.Equal(CustomerReceiptAllocationQuery.DefaultPageSize, all.PageSize);

        var activeOnly = AssertOk<PagedResult<CustomerReceiptAllocationDto>>(
            await controller.GetPaged(new CustomerReceiptAllocationQuery
            {
                Status = CustomerReceiptAllocationRules.StatusActive
            }));
        Assert.Equal(1, activeOnly.Total);
        Assert.Equal(100m, activeOnly.Items[0].AllocatedAmount);

        var voidedOnly = AssertOk<PagedResult<CustomerReceiptAllocationDto>>(
            await controller.GetPaged(new CustomerReceiptAllocationQuery
            {
                Status = CustomerReceiptAllocationRules.StatusVoided
            }));
        Assert.Single(voidedOnly.Items);
        Assert.True(voidedOnly.Items[0].IsVoided);

        var byOrder = AssertOk<PagedResult<CustomerReceiptAllocationDto>>(
            await controller.GetPaged(new CustomerReceiptAllocationQuery { SalesOrderId = orderUsd.Id }));
        Assert.Equal(1, byOrder.Total);
        Assert.Equal("SK-USD", byOrder.Items[0].ReceiptNo);

        var byReceipt = AssertOk<PagedResult<CustomerReceiptAllocationDto>>(
            await controller.GetPaged(new CustomerReceiptAllocationQuery { ReceiptId = receiptCny.Id }));
        Assert.Equal(1, byReceipt.Total);

        var byCustomerAndCurrency = AssertOk<PagedResult<CustomerReceiptAllocationDto>>(
            await controller.GetPaged(new CustomerReceiptAllocationQuery
            {
                CustomerId = customerA.Id,
                Currency = " cny "
            }));
        Assert.Equal(1, byCustomerAndCurrency.Total);
        Assert.Equal("CNY", byCustomerAndCurrency.Items[0].Currency);

        var byKeyword = AssertOk<PagedResult<CustomerReceiptAllocationDto>>(
            await controller.GetPaged(new CustomerReceiptAllocationQuery { Keyword = "SO-USD" }));
        Assert.Equal(1, byKeyword.Total);

        var paged = AssertOk<PagedResult<CustomerReceiptAllocationDto>>(
            await controller.GetPaged(new CustomerReceiptAllocationQuery { Page = 2, PageSize = 1 }));
        Assert.Equal(2, paged.Total);
        Assert.Equal(2, paged.Page);
        Assert.Single(paged.Items);

        // 未知状态 / 未知币种 / 超长关键字一律拒绝（不静默忽略筛选条件）
        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => controller.GetPaged(new CustomerReceiptAllocationQuery { Status = 9 }));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => controller.GetPaged(new CustomerReceiptAllocationQuery { Currency = "RUB" }));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => controller.GetPaged(new CustomerReceiptAllocationQuery { Keyword = new string('k', 101) }));
    }

    [Fact]
    public async Task 收款单候选只列未删除收款单并派生已引用未引用与资格文案()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var disabled = SeedCustomer(db, "C009", "停用客户", status: 0);
        var receipt = SeedReceipt(db, "SK-1", customer.Id, 500m, Currency.USD);
        var fullReceipt = SeedReceipt(db, "SK-FULL", customer.Id, 100m, Currency.USD);
        var disabledCustomerReceipt = SeedReceipt(db, "SK-DIS", disabled.Id, 300m, Currency.USD);
        SeedReceipt(db, "SK-DEL", customer.Id, 100m, Currency.USD,
            DocumentStatus.Approved, deleted: true);
        var orderA = SeedOrder(db, "SO-A", customer.Id, Currency.USD, DocumentStatus.Approved, 500m);
        var orderB = SeedOrder(db, "SO-B", customer.Id, Currency.USD, DocumentStatus.Approved, 500m);
        var disabledOrder = SeedOrder(db, "SO-DIS", disabled.Id, Currency.USD,
            DocumentStatus.Approved, 500m);
        var controller = BuildController(db);

        await CreateAllocationAsync(controller, receipt.Id, orderA.Id, 200m);
        await CreateAllocationAsync(controller, fullReceipt.Id, orderB.Id, 100m);
        await CreateAllocationAsync(controller, disabledCustomerReceipt.Id, disabledOrder.Id, 50m);

        var candidates = AssertOk<List<CustomerReceiptAllocationReceiptCandidateDto>>(
            await controller.ReceiptCandidates(null, null));

        // 已删除收款单不出现在候选里
        Assert.DoesNotContain(candidates, c => c.ReceiptNo == "SK-DEL");
        Assert.Equal(3, candidates.Count);

        var partial = candidates.Single(c => c.ReceiptNo == "SK-1");
        Assert.Equal(500m, partial.ReceiptAmount);
        Assert.Equal(200m, partial.AllocatedAmount);
        Assert.Equal(300m, partial.UnallocatedAmount);
        Assert.Equal(1, partial.AllocationCount);
        Assert.True(partial.Eligible);
        Assert.Contains("USD", partial.EligibilityText);
        Assert.Contains("剩余可引用 300", partial.EligibilityText);
        Assert.Equal("C001", partial.CustomerCode);

        var full = candidates.Single(c => c.ReceiptNo == "SK-FULL");
        Assert.Equal(0m, full.UnallocatedAmount);
        Assert.False(full.Eligible);
        Assert.Contains("已被有效引用行占满", full.EligibilityText);

        // 客户停用只作只读说明（历史快照照常可读），不阻断候选读取
        var disabledCustomer = candidates.Single(c => c.ReceiptNo == "SK-DIS");
        Assert.True(disabledCustomer.Eligible);
        Assert.Contains("客户已停用", disabledCustomer.EligibilityText);

        // 按客户 + 收款单号关键字筛选
        var filtered = AssertOk<List<CustomerReceiptAllocationReceiptCandidateDto>>(
            await controller.ReceiptCandidates(disabled.Id, "SK-DIS"));
        Assert.Single(filtered);
        Assert.Equal(disabled.Id, filtered[0].CustomerId);
    }

    [Fact]
    public async Task 销售订单候选只列同客户同币种_含已取消标注与派生金额()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "义乌进出口");
        var other = SeedCustomer(db, "C002", "广州贸易");
        var receipt = SeedReceipt(db, "SK-1", customer.Id, 1000m, Currency.USD);
        var otherReceipt = SeedReceipt(db, "SK-2", customer.Id, 1000m, Currency.USD);
        var orderA = SeedOrder(db, "SO-A", customer.Id, Currency.USD, DocumentStatus.Approved, 800m,
            contractNo: "HT-2026-001");
        var cancelled = SeedOrder(db, "SO-CANCEL", customer.Id, Currency.USD, DocumentStatus.Cancelled, 500m);
        SeedOrder(db, "SO-CNY", customer.Id, Currency.CNY, DocumentStatus.Approved, 500m);
        SeedOrder(db, "SO-OTHER", other.Id, Currency.USD, DocumentStatus.Approved, 500m);
        SeedOrder(db, "SO-DEL", customer.Id, Currency.USD, DocumentStatus.Approved, 500m, deleted: true);
        var controller = BuildController(db);

        await CreateAllocationAsync(controller, receipt.Id, orderA.Id, 300m);
        await CreateAllocationAsync(controller, otherReceipt.Id, orderA.Id, 100m);

        var candidates = AssertOk<List<CustomerReceiptAllocationOrderCandidateDto>>(
            await controller.OrderCandidates(receipt.Id, null));

        // 只列同客户 + 同币种且未删除的订单（含已取消并标注）
        Assert.Equal(2, candidates.Count);
        Assert.DoesNotContain(candidates, c => c.OrderNo is "SO-CNY" or "SO-OTHER" or "SO-DEL");

        var rowA = candidates.Single(c => c.OrderNo == "SO-A");
        Assert.True(rowA.Eligible);
        Assert.False(rowA.Cancelled);
        Assert.Equal("USD", rowA.Currency);
        Assert.Equal(customer.Id, rowA.CustomerId);
        Assert.Equal(800m, rowA.OrderedAmount);
        Assert.Equal(300m, rowA.AllocatedByThisReceipt);
        Assert.Equal(100m, rowA.AllocatedByOtherReceipts);
        Assert.Equal(400m, rowA.RemainingUnallocatedAmount);

        var cancelledRow = candidates.Single(c => c.OrderNo == "SO-CANCEL");
        Assert.True(cancelledRow.Cancelled);
        Assert.False(cancelledRow.Eligible);
        Assert.Contains("已取消", cancelledRow.EligibilityText);

        // 关键字按销售订单号 / 外销合同号检索
        var byContract = AssertOk<List<CustomerReceiptAllocationOrderCandidateDto>>(
            await controller.OrderCandidates(receipt.Id, "HT-2026"));
        Assert.Single(byContract);
        Assert.Equal("SO-A", byContract[0].OrderNo);

        // 订单侧剩余金额下限 0（订单总额不是引用金额的硬上限，也不改派）
        var thirdReceipt = SeedReceipt(db, "SK-3", customer.Id, 1000m, Currency.USD);
        await CreateAllocationAsync(controller, thirdReceipt.Id, orderA.Id, 700m);
        var again = AssertOk<List<CustomerReceiptAllocationOrderCandidateDto>>(
            await controller.OrderCandidates(receipt.Id, "SO-A"));
        Assert.Equal(0m, again[0].RemainingUnallocatedAmount);
    }

    // ==================== 5. 纯规则 ====================

    [Fact]
    public void 纯规则_状态币种金额精度与文案()
    {
        Assert.Equal("有效", CustomerReceiptAllocationRules.StatusText(
            CustomerReceiptAllocationRules.StatusActive));
        Assert.Equal("已作废", CustomerReceiptAllocationRules.StatusText(
            CustomerReceiptAllocationRules.StatusVoided));
        Assert.Throws<BusinessException>(() => CustomerReceiptAllocationRules.StatusText(9));
        Assert.Equal(CustomerReceiptAllocationRules.StatusActive,
            CustomerReceiptAllocationRules.NormalizeStatusFilter(CustomerReceiptAllocationRules.StatusActive));
        Assert.Null(CustomerReceiptAllocationRules.NormalizeStatusFilter(null));
        Assert.Throws<BusinessException>(() => CustomerReceiptAllocationRules.NormalizeStatusFilter(7));

        Assert.Equal("USD", CustomerReceiptAllocationRules.NormalizeCurrencyStrict(" usd "));
        Assert.Throws<BusinessException>(() => CustomerReceiptAllocationRules.NormalizeCurrencyStrict("RUB"));
        Assert.Equal(100.46m, CustomerReceiptAllocationRules.NormalizeAllocationAmount(100.455m, "CNY"));
        Assert.Throws<BusinessException>(
            () => CustomerReceiptAllocationRules.NormalizeAllocationAmount(0m, "CNY"));
        Assert.Throws<BusinessException>(
            () => CustomerReceiptAllocationRules.NormalizeAllocationAmount(-5m, "CNY"));
        Assert.Equal(101m, CustomerReceiptAllocationRules.NormalizeAllocationAmount(100.5m, "JPY"));
        Assert.Equal(100.01m, CustomerReceiptAllocationRules.AuthoritativeReceiptAmount(100.005m, "CNY"));

        Assert.Equal("备注", CustomerReceiptAllocationRules.NormalizeRemark("  备注  "));
        Assert.Throws<BusinessException>(() => CustomerReceiptAllocationRules.NormalizeRemark(
            new string('m', CustomerReceiptAllocationRules.MaxRemarkLength + 1)));
        Assert.Equal("更正", CustomerReceiptAllocationRules.NormalizeVoidReason(" 更正 "));
        Assert.Throws<BusinessException>(() => CustomerReceiptAllocationRules.NormalizeVoidReason("  "));
        Assert.Throws<BusinessException>(() => CustomerReceiptAllocationRules.NormalizeVoidReason(
            new string('v', CustomerReceiptAllocationRules.MaxVoidReasonLength + 1)));
        Assert.Equal("SO-1", CustomerReceiptAllocationRules.NormalizeKeyword(" SO-1 "));
        Assert.Throws<BusinessException>(() => CustomerReceiptAllocationRules.NormalizeKeyword(
            new string('k', 101)));

        Assert.Equal(CustomerReceiptAllocationRules.LinkageUnallocated,
            CustomerReceiptAllocationRules.LinkageStatusOf(100m, 0m));
        Assert.Equal(CustomerReceiptAllocationRules.LinkagePartial,
            CustomerReceiptAllocationRules.LinkageStatusOf(100m, 99.99m));
        Assert.Equal(CustomerReceiptAllocationRules.LinkageFullyAllocated,
            CustomerReceiptAllocationRules.LinkageStatusOf(100m, 100m));
        Assert.Contains("未被引用",
            CustomerReceiptAllocationRules.LinkageText(100m, 0m, 0, "USD"));
        Assert.Contains("部分引用",
            CustomerReceiptAllocationRules.LinkageText(100m, 40m, 1, "USD"));
        Assert.Contains("已全额引用",
            CustomerReceiptAllocationRules.LinkageText(100m, 100m, 2, "USD"));

        Assert.Equal("已审核", CustomerReceiptAllocationRules.ReceiptStatusText((int)DocumentStatus.Approved));
        Assert.Equal("已取消", CustomerReceiptAllocationRules.OrderStatusText((int)DocumentStatus.Cancelled));
        Assert.Contains("未知", CustomerReceiptAllocationRules.OrderStatusText(99));

        CustomerReceiptAllocationRules.EnsureVoidable(
            CustomerReceiptAllocationRules.StatusActive, "SK-1", "SO-1");
        Assert.Throws<BusinessException>(() => CustomerReceiptAllocationRules.EnsureVoidable(
            CustomerReceiptAllocationRules.StatusVoided, "SK-1", "SO-1"));

        Assert.Contains("JPY", CustomerReceiptAllocationRules.SupportedCurrencies);
        Assert.Contains("不得超过收款单金额", CustomerReceiptAllocationRules.RuleText);
        Assert.Contains("不会按单号相似度", CustomerReceiptAllocationRules.RuleText);
        Assert.Contains("不是银行入账", CustomerReceiptAllocationRules.BoundaryText);
        Assert.Contains("不是应收账款台账或余额", CustomerReceiptAllocationRules.BoundaryText);
        Assert.Contains("不构成法律上的债务清偿", CustomerReceiptAllocationRules.BoundaryText);
    }

    // ==================== 6. ERP-032 审计与唯一权威引用册 ====================

    [Fact]
    public void ERP032审计_收款单只有客户级引用_仓库只有一套收款引用模型()
    {
        // ERP-032 / ERP-046 权威口径：收款单只按客户级引用（FinanceReceipt.CustomerId），没有订单级引用
        var progress = File.ReadAllText(
            RepoFile("src", "ERP.Api", "Controllers", "SalesOrderProgress.cs"));
        Assert.Contains("public const string ReferenceReceipt = \"FinanceReceipt.CustomerId\"", progress);
        Assert.Contains("收款单 / 装柜结算单 / 散货结算单只记录客户，没有订单级引用", progress);

        // ERP-046 沿用同一权威字段，并把收款单作为「未关联证据」列出（绝不按相似度匹配订单）
        var reconciliation = File.ReadAllText(
            RepoFile("src", "ERP.Api", "Controllers", "SalesOrderReceiptReconciliation.cs"));
        Assert.Contains("UnlinkedReferenceField = SalesOrderProgress.ReferenceReceipt;", reconciliation);
        Assert.Contains("未关联证据：收款单只记录客户（FinanceReceipt.CustomerId），没有订单级引用", reconciliation);

        // 收款单与销售订单实体都没有订单级 / 收款级引用列：没有可复用的既有链接，也没有新增任何列
        Assert.DoesNotContain(typeof(FinanceReceipt).GetProperties(),
            p => p.Name.Contains("SalesOrderId", StringComparison.Ordinal)
                 || p.Name.Contains("OrderNo", StringComparison.Ordinal));
        Assert.DoesNotContain(typeof(SalesOrder).GetProperties(),
            p => p.Name.Contains("Receipt", StringComparison.Ordinal));

        // 幂等升级只建一张收款引用表；本模块段落内不含任何 ALTER TABLE（不改写收款单 / 销售订单表结构）
        var schema = File.ReadAllText(
            RepoFile("src", "ERP.Infrastructure", "Data", "SchemaUpgrader.cs"));
        Assert.Contains("CREATE TABLE db_owner.CustomerReceiptAllocations", schema);
        Assert.DoesNotContain("ALTER TABLE db_owner.FinanceReceipt", schema);

        var schemaStart = schema.IndexOf("// 36. 客户收款引用登记", StringComparison.Ordinal);
        Assert.True(schemaStart > 0);
        Assert.DoesNotContain("ALTER TABLE", schema[schemaStart..]);

        // 唯一权威引用册：接口中只有一套「收款引用」模型（不存在第二套收款 → 订单链接结构）
        var receiptAllocationSets = typeof(IErpDbContext).GetProperties()
            .Where(p => p.Name.Contains("Receipt", StringComparison.Ordinal)
                        && p.Name.Contains("Allocation", StringComparison.Ordinal))
            .ToList();
        Assert.Single(receiptAllocationSets);
        Assert.Equal("CustomerReceiptAllocations", receiptAllocationSets[0].Name);
        Assert.Equal(typeof(DbSet<CustomerReceiptAllocation>), receiptAllocationSets[0].PropertyType);
    }

    // ==================== 7. 模型、幂等结构与前端接线契约 ====================

    [Fact]
    public void 模型配置契约_精度长度过滤唯一索引与刻意不建外键()
    {
        using var db = TestDbFactory.Create();

        var entityType = db.Model.FindEntityType(typeof(CustomerReceiptAllocation));
        Assert.NotNull(entityType);

        Assert.Equal(50, entityType!.FindProperty(nameof(CustomerReceiptAllocation.ReceiptNo))!
            .GetMaxLength());
        Assert.Equal(30, entityType.FindProperty(nameof(CustomerReceiptAllocation.ReceiptStatusText))!
            .GetMaxLength());
        Assert.Equal(50, entityType.FindProperty(nameof(CustomerReceiptAllocation.OrderNo))!
            .GetMaxLength());
        Assert.Equal(20, entityType.FindProperty(nameof(CustomerReceiptAllocation.OrderCurrency))!
            .GetMaxLength());
        Assert.Equal(50, entityType.FindProperty(nameof(CustomerReceiptAllocation.CustomerCode))!
            .GetMaxLength());
        Assert.Equal(200, entityType.FindProperty(nameof(CustomerReceiptAllocation.CustomerName))!
            .GetMaxLength());
        Assert.Equal(20, entityType.FindProperty(nameof(CustomerReceiptAllocation.Currency))!
            .GetMaxLength());
        Assert.Equal(500, entityType.FindProperty(nameof(CustomerReceiptAllocation.Remark))!
            .GetMaxLength());
        Assert.Equal(500, entityType.FindProperty(nameof(CustomerReceiptAllocation.VoidReason))!
            .GetMaxLength());
        Assert.Equal(18, entityType.FindProperty(nameof(CustomerReceiptAllocation.ReceiptAmount))!
            .GetPrecision());
        Assert.Equal(2, entityType.FindProperty(nameof(CustomerReceiptAllocation.ReceiptAmount))!
            .GetScale());
        Assert.Equal(18, entityType.FindProperty(nameof(CustomerReceiptAllocation.AllocatedAmount))!
            .GetPrecision());

        var unique = entityType.GetIndexes().Single(i =>
            i.GetDatabaseName() == "UX_CustomerReceiptAllocations_ReceiptOrder");
        Assert.True(unique.IsUnique);
        Assert.Equal("IsDeleted = 0 AND Status <> 2", unique.GetFilter());
        Assert.Equal(2, unique.Properties.Count);

        Assert.Contains(entityType.GetIndexes(),
            i => i.GetDatabaseName() == "IX_CustomerReceiptAllocations_SalesOrderId");
        Assert.Contains(entityType.GetIndexes(),
            i => i.GetDatabaseName() == "IX_CustomerReceiptAllocations_ReceiptId_Status");
        Assert.Contains(entityType.GetIndexes(),
            i => i.GetDatabaseName() == "IX_CustomerReceiptAllocations_Status_AllocatedAt");

        // 刻意不建任何外键，也不在收款单 / 销售订单 / 客户上加导航属性
        Assert.Empty(entityType.GetForeignKeys());
        Assert.Empty(entityType.GetNavigations());

        var receiptType = db.Model.FindEntityType(typeof(FinanceReceipt));
        Assert.NotNull(receiptType);
        Assert.DoesNotContain(receiptType!.GetNavigations(),
            n => n.ClrType == typeof(CustomerReceiptAllocation));
        var orderType = db.Model.FindEntityType(typeof(SalesOrder));
        Assert.NotNull(orderType);
        Assert.DoesNotContain(orderType!.GetNavigations(),
            n => n.ClrType == typeof(CustomerReceiptAllocation));

        // 读取侧标注是非持久化列（不落库）
        Assert.DoesNotContain(entityType.GetProperties(),
            p => p.Name is nameof(CustomerReceiptAllocation.ReceiptAvailable)
                or nameof(CustomerReceiptAllocation.ReceiptAvailabilityText)
                or nameof(CustomerReceiptAllocation.OrderAvailable)
                or nameof(CustomerReceiptAllocation.OrderAvailabilityText));
    }

    [Fact]
    public void Schema_upgrade_幂等建表建索引且不含任何回填或资金语句()
    {
        var script = File.ReadAllText(
            RepoFile("src", "ERP.Infrastructure", "Data", "SchemaUpgrader.cs"));

        Assert.Contains("IF OBJECT_ID('db_owner.CustomerReceiptAllocations') IS NULL", script);
        Assert.Contains("CREATE TABLE db_owner.CustomerReceiptAllocations", script);
        Assert.Contains("ReceiptAmount DECIMAL(18,2) NOT NULL DEFAULT 0", script);
        Assert.Contains("AllocatedAmount DECIMAL(18,2) NOT NULL DEFAULT 0", script);
        Assert.Contains("Status INT NOT NULL DEFAULT 1", script);
        Assert.Contains("CREATE UNIQUE INDEX UX_CustomerReceiptAllocations_ReceiptOrder", script);
        Assert.Contains("WHERE IsDeleted = 0 AND Status <> 2;", script);
        Assert.Contains("CREATE INDEX IX_CustomerReceiptAllocations_SalesOrderId", script);

        // 不建任何外键（收款单 / 销售订单 / 客户侧都不被本段引用或改写）
        Assert.DoesNotContain("FK_CustomerReceiptAllocations", script);
        Assert.DoesNotContain("ALTER TABLE db_owner.FinanceReceipt", script);

        var start = script.IndexOf("// 36. 客户收款引用登记", StringComparison.Ordinal);
        Assert.True(start > 0);
        var segment = script[start..];
        // 本模块段落只建表 + 过滤索引：既不改写既有表结构，也不做任何回填或资金语句
        Assert.DoesNotContain("ALTER TABLE", segment);
        Assert.DoesNotContain("UPDATE db_owner", segment);
        Assert.DoesNotContain("INSERT INTO db_owner", segment);
        Assert.DoesNotContain("DELETE FROM db_owner", segment);
    }

    [Fact]
    public void 前端与路由接线契约()
    {
        var index = File.ReadAllText(RepoFile("src", "ERP.Api", "wwwroot", "index.html"));
        Assert.Contains("/js/customer-receipt-allocations.js", index);

        var financeModules = File.ReadAllText(
            RepoFile("src", "ERP.Api", "wwwroot", "js", "modules-finance.js"));
        Assert.Contains("openCustomerReceiptAllocationRegister()", financeModules);

        var docModules = File.ReadAllText(
            RepoFile("src", "ERP.Api", "wwwroot", "js", "modules-doc.js"));
        // 工具栏入口必须带括号（extraActions 直接注入 onclick 属性），行操作只写函数名（渲染时注入行 Id）
        Assert.Contains("onclick: 'openCustomerReceiptAllocationRegister()'", docModules);
        Assert.Contains("onclick: 'openCustomerReceiptAllocationRegister'", docModules);

        var js = File.ReadAllText(
            RepoFile("src", "ERP.Api", "wwwroot", "js", "customer-receipt-allocations.js"));
        Assert.Contains("async function openCustomerReceiptAllocationRegister", js);
        Assert.Contains("'/api/customer-receipt-allocations'", js);
        Assert.Contains("'/api/customer-receipt-allocations?'", js);
        Assert.Contains("/receipts?", js);
        Assert.Contains("/summary", js);
        Assert.Contains("/order-candidates?", js);
        Assert.Contains("/void", js);
        Assert.Contains("craCreateAllocation", js);
        Assert.Contains("不会真的收款", js);

        var controller = File.ReadAllText(
            RepoFile("src", "ERP.Api", "Controllers", "CustomerReceiptAllocationController.cs"));
        Assert.Contains("[Route(\"api/customer-receipt-allocations\")]", controller);
        Assert.Contains("receipts/{receiptId:long}/summary", controller);
        Assert.Contains("receipts/{receiptId:long}/allocations", controller);
        Assert.Contains("receipts/{receiptId:long}/order-candidates", controller);
        Assert.Contains("{id:long}/void", controller);
    }
}
