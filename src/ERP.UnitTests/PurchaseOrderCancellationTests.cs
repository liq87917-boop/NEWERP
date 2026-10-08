using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-345 采购订单取消护栏单元测试。
/// <para>覆盖：无履约依赖取消成功且保留原明细 / 金额、已审核未冲销入库拒绝、已取消入库释放、有效付款引用拒绝、
/// 作废 / 删除 / 发票失效证据释放、发票维度付款引用拒绝、无身份 / 无菜单 / 越客户范围拒绝、重复取消、
/// 终止态取消拒绝，以及控制器授权仅继承基类（无权限扩展）。</para>
/// <para>全部使用内存数据库，不连接 SQL Server、不启动 API、不运行浏览器验收。</para>
/// </summary>
public class PurchaseOrderCancellationTests
{
    private const long SupplierA = 943001L;
    private const long SupplierB = 943002L;
    private const long WarehouseA = 943101L;
    private const long ProductA = 943201L;
    private const long CustomerA = 943301L;

    // ==================== 无履约依赖取消 ====================

    [Fact]
    public async Task Cancel_NoDependent_SucceedsAndPreservesDetailsAndAmount()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, isSystem: true, withMenu: true);
        var order = SeedOrder(db, "PO-CANCEL-1", SupplierA, DocumentStatus.Approved, totalAmount: 1500m, lineAmount: 1500m);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var result = await ctl.Cancel(order.Id);

        Assert.IsType<OkObjectResult>(result);
        var saved = db.PurchaseOrders.Single(o => o.Id == order.Id);
        Assert.Equal(DocumentStatus.Cancelled, saved.Status);
        Assert.Equal(1500m, saved.TotalAmount);
        Assert.Single(db.PurchaseOrderDetails.Where(d => d.PurchaseOrderId == order.Id && !d.IsDeleted));
    }

    // ==================== 入库履约证据 ====================

    [Fact]
    public async Task Cancel_ActiveApprovedStockIn_Refused()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, isSystem: true, withMenu: true);
        var order = SeedOrder(db, "PO-CANCEL-2", SupplierA, DocumentStatus.Approved);
        SeedStockIn(db, "SI-ACTIVE", order.Id, SupplierA, DocumentStatus.Approved);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            PurchaseOrderCancellationRules.ValidateCancellationAsync(db, ReloadOrder(db, order.Id), user.Id));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("已审核且未冲销", ex.Message);
        Assert.Equal(DocumentStatus.Approved, db.PurchaseOrders.Single(o => o.Id == order.Id).Status);
    }

    [Fact]
    public async Task Cancel_ReversedStockIn_Released()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, isSystem: true, withMenu: true);
        var order = SeedOrder(db, "PO-CANCEL-3", SupplierA, DocumentStatus.Approved);
        SeedStockIn(db, "SI-CANCELLED", order.Id, SupplierA, DocumentStatus.Cancelled);

        await PurchaseOrderCancellationRules.ValidateCancellationAsync(db, ReloadOrder(db, order.Id), user.Id);
    }

    // ==================== 付款 → 采购订单 引用证据 ====================

    [Fact]
    public async Task Cancel_ActivePaymentAllocation_Refused()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, isSystem: true, withMenu: true);
        var order = SeedOrder(db, "PO-CANCEL-4", SupplierA, DocumentStatus.Approved);
        var payment = SeedPayment(db, "PAY-ACTIVE", SupplierA);
        SeedPaymentAllocation(db, payment, order, SupplierPaymentAllocationRules.StatusActive, 300m);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            PurchaseOrderCancellationRules.ValidateCancellationAsync(db, ReloadOrder(db, order.Id), user.Id));

        Assert.Contains("付款单 → 采购订单", ex.Message);
        Assert.Equal(DocumentStatus.Approved, db.PurchaseOrders.Single(o => o.Id == order.Id).Status);
    }

    [Fact]
    public async Task Cancel_VoidedPaymentAllocation_Released()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, isSystem: true, withMenu: true);
        var order = SeedOrder(db, "PO-CANCEL-5", SupplierA, DocumentStatus.Approved);
        var payment = SeedPayment(db, "PAY-VOIDED", SupplierA);
        SeedPaymentAllocation(db, payment, order, SupplierPaymentAllocationRules.StatusVoided, 300m, voidReason: "录错");

        await PurchaseOrderCancellationRules.ValidateCancellationAsync(db, ReloadOrder(db, order.Id), user.Id);
    }

    [Fact]
    public async Task Cancel_DeletedPayment_Released()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, isSystem: true, withMenu: true);
        var order = SeedOrder(db, "PO-CANCEL-6", SupplierA, DocumentStatus.Approved);
        var payment = SeedPayment(db, "PAY-DELETED", SupplierA, deleted: true);
        SeedPaymentAllocation(db, payment, order, SupplierPaymentAllocationRules.StatusActive, 300m);

        await PurchaseOrderCancellationRules.ValidateCancellationAsync(db, ReloadOrder(db, order.Id), user.Id);
    }

    [Fact]
    public async Task Cancel_MismatchedSupplierPaymentAllocation_Released()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, isSystem: true, withMenu: true);
        var order = SeedOrder(db, "PO-CANCEL-7", SupplierA, DocumentStatus.Approved);
        var payment = SeedPayment(db, "PAY-SUPB", SupplierB);
        SeedPaymentAllocation(db, payment, order, SupplierPaymentAllocationRules.StatusActive, 300m, supplierId: SupplierB);

        await PurchaseOrderCancellationRules.ValidateCancellationAsync(db, ReloadOrder(db, order.Id), user.Id);
    }

    // ==================== 付款 → 采购发票（关联本订单） 引用证据 ====================

    [Fact]
    public async Task Cancel_ActiveInvoicePaymentAllocation_Refused()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, isSystem: true, withMenu: true);
        var order = SeedOrder(db, "PO-CANCEL-8", SupplierA, DocumentStatus.Approved);
        var invoice = SeedInvoice(db, "INV-ACTIVE", SupplierA, PurchaseInvoiceRules.StatusRecorded, 300m);
        SeedInvoiceAllocation(db, invoice, order);
        var payment = SeedPayment(db, "PAY-INV-ACTIVE", SupplierA);
        SeedInvoicePaymentAllocation(db, payment, invoice, SupplierPaymentInvoiceAllocationRules.StatusActive, 300m);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            PurchaseOrderCancellationRules.ValidateCancellationAsync(db, ReloadOrder(db, order.Id), user.Id));

        Assert.Contains("付款单 → 采购发票", ex.Message);
        Assert.Equal(DocumentStatus.Approved, db.PurchaseOrders.Single(o => o.Id == order.Id).Status);
    }

    [Fact]
    public async Task Cancel_VoidedInvoicePaymentAllocation_Released()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, isSystem: true, withMenu: true);
        var order = SeedOrder(db, "PO-CANCEL-9", SupplierA, DocumentStatus.Approved);
        var invoice = SeedInvoice(db, "INV-VOIDROW", SupplierA, PurchaseInvoiceRules.StatusRecorded, 300m);
        SeedInvoiceAllocation(db, invoice, order);
        var payment = SeedPayment(db, "PAY-INV-VOID", SupplierA);
        SeedInvoicePaymentAllocation(db, payment, invoice, SupplierPaymentInvoiceAllocationRules.StatusVoided, 300m);

        await PurchaseOrderCancellationRules.ValidateCancellationAsync(db, ReloadOrder(db, order.Id), user.Id);
    }

    [Fact]
    public async Task Cancel_DraftInvoice_Released()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, isSystem: true, withMenu: true);
        var order = SeedOrder(db, "PO-CANCEL-10", SupplierA, DocumentStatus.Approved);
        var invoice = SeedInvoice(db, "INV-DRAFT", SupplierA, PurchaseInvoiceRules.StatusDraft, 300m);
        SeedInvoiceAllocation(db, invoice, order);
        var payment = SeedPayment(db, "PAY-INV-DRAFT", SupplierA);
        SeedInvoicePaymentAllocation(db, payment, invoice, SupplierPaymentInvoiceAllocationRules.StatusActive, 300m);

        await PurchaseOrderCancellationRules.ValidateCancellationAsync(db, ReloadOrder(db, order.Id), user.Id);
    }

    // ==================== 授权与状态 ====================

    [Fact]
    public async Task Cancel_NoIdentity_Unauthorized()
    {
        using var db = TestDbFactory.Create();
        var order = SeedOrder(db, "PO-CANCEL-11", SupplierA, DocumentStatus.Approved);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            PurchaseOrderCancellationRules.ValidateCancellationAsync(db, ReloadOrder(db, order.Id), null));

        Assert.Equal(ErrorCodes.Unauthorized, ex.Code);
    }

    [Fact]
    public async Task Cancel_NoMenu_Forbidden()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, isSystem: true, withMenu: false);
        var order = SeedOrder(db, "PO-CANCEL-12", SupplierA, DocumentStatus.Approved);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            PurchaseOrderCancellationRules.ValidateCancellationAsync(db, ReloadOrder(db, order.Id), user.Id));

        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    [Fact]
    public async Task Cancel_OutOfCustomerScope_Forbidden()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, isSystem: false, withMenu: true);
        var order = SeedOrder(db, "PO-CANCEL-13", SupplierA, DocumentStatus.Approved, owningCustomerId: CustomerA);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            PurchaseOrderCancellationRules.ValidateCancellationAsync(db, ReloadOrder(db, order.Id), user.Id));

        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    [Fact]
    public async Task Cancel_AlreadyCancelled_Conflict()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, isSystem: true, withMenu: true);
        var order = SeedOrder(db, "PO-CANCEL-14", SupplierA, DocumentStatus.Cancelled);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            PurchaseOrderCancellationRules.ValidateCancellationAsync(db, ReloadOrder(db, order.Id), user.Id));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("重复取消", ex.Message);
    }

    [Fact]
    public async Task Cancel_TerminalState_Conflict()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, isSystem: true, withMenu: true);
        var order = SeedOrder(db, "PO-CANCEL-15", SupplierA, DocumentStatus.Rejected);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            PurchaseOrderCancellationRules.ValidateCancellationAsync(db, ReloadOrder(db, order.Id), user.Id));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("当前状态不允许取消", ex.Message);
    }

    // ==================== 控制器授权继承（无权限扩展） ====================

    [Fact]
    public void Controller_Authorization_OnlyInheritedFromBase()
    {
        Assert.Empty(typeof(PurchaseOrderController).GetCustomAttributes(typeof(AuthorizeAttribute), false));
        Assert.NotEmpty(typeof(DocumentControllerBase<PurchaseOrder>).GetCustomAttributes(typeof(AuthorizeAttribute), false));
    }

    // ==================== 测试脚手架 ====================

    private static PurchaseOrderController NewController(ErpDbContext db)
        => new PurchaseOrderController(db, new DocumentNumberService(db));

    private static PurchaseOrder ReloadOrder(ErpDbContext db, long id)
        => db.PurchaseOrders.Single(o => o.Id == id);

    private static SysUser SeedAuthorizedUser(ErpDbContext db, bool isSystem, bool withMenu)
    {
        var role = new SysRole
        {
            RoleName = "测试角色",
            RoleCode = $"Role-{Guid.NewGuid():N}",
            IsSystem = isSystem
        };
        db.SysRoles.Add(role);
        db.SaveChanges();

        var user = new SysUser
        {
            UserName = $"u-{Guid.NewGuid():N}",
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = "测试用户",
            Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        db.SaveChanges();

        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        db.SaveChanges();

        if (withMenu)
        {
            var menu = new SysMenu
            {
                MenuCode = PurchaseOrderCancellationRules.RequiredMenuCode,
                MenuName = PurchaseOrderCancellationRules.RequiredMenuText,
                MenuType = MenuType.Menu
            };
            db.SysMenus.Add(menu);
            db.SaveChanges();
            db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
            db.SaveChanges();
        }

        return user;
    }

    private static PurchaseOrder SeedOrder(ErpDbContext db, string no, long supplierId, DocumentStatus status,
        long? owningCustomerId = null, decimal totalAmount = 1000m, decimal lineAmount = 1000m)
    {
        var order = new PurchaseOrder
        {
            OrderNo = no,
            OrderDate = DateTime.Today.AddDays(-5),
            SupplierId = supplierId,
            Currency = Currency.CNY,
            ExchangeRate = 1m,
            TotalAmount = totalAmount,
            OwningCustomerId = owningCustomerId,
            Status = status
        };
        db.PurchaseOrders.Add(order);
        db.SaveChanges();

        db.PurchaseOrderDetails.Add(new PurchaseOrderDetail
        {
            PurchaseOrderId = order.Id,
            ProductId = ProductA,
            ProductName = "商品A",
            Quantity = 1m,
            UnitPrice = lineAmount,
            Amount = lineAmount
        });
        db.SaveChanges();
        return order;
    }

    private static void SeedStockIn(ErpDbContext db, string no, long orderId, long supplierId, DocumentStatus status)
    {
        db.StockIns.Add(new StockIn
        {
            StockInNo = no,
            StockInDate = DateTime.Today,
            PurchaseOrderId = orderId,
            SupplierId = supplierId,
            WarehouseId = WarehouseA,
            Status = status
        });
        db.SaveChanges();
    }

    private static FinancePayment SeedPayment(ErpDbContext db, string no, long supplierId, bool deleted = false)
    {
        var payment = new FinancePayment
        {
            PaymentNo = no,
            PaymentDate = DateTime.Today,
            SupplierId = supplierId,
            Amount = 1000m,
            Currency = Currency.CNY,
            Status = DocumentStatus.Approved,
            IsDeleted = deleted
        };
        db.FinancePayments.Add(payment);
        db.SaveChanges();
        return payment;
    }


    private static void SeedPaymentAllocation(ErpDbContext db, FinancePayment payment, PurchaseOrder order,
        int status, decimal amount, long? supplierId = null, string? voidReason = null)
    {
        db.SupplierPaymentAllocations.Add(new SupplierPaymentAllocation
        {
            PaymentId = payment.Id,
            PaymentNo = payment.PaymentNo,
            PaymentDate = payment.PaymentDate,
            PaymentStatus = (int)payment.Status,
            PaymentStatusText = payment.Status.ToString(),
            PaymentAmount = payment.Amount,
            PurchaseOrderId = order.Id,
            OrderNo = order.OrderNo,
            OrderDate = order.OrderDate,
            OrderStatus = (int)order.Status,
            OrderCurrency = order.Currency.ToString(),
            SupplierId = supplierId ?? payment.SupplierId,
            SupplierCode = "SUP",
            SupplierName = "供应商",
            AllocatedAmount = amount,
            Currency = payment.Currency.ToString(),
            Status = status,
            AllocatedAt = DateTime.Now,
            VoidedAt = status == SupplierPaymentAllocationRules.StatusVoided ? DateTime.Now : null,
            VoidReason = voidReason ?? string.Empty
        });
        db.SaveChanges();
    }

    private static PurchaseInvoice SeedInvoice(ErpDbContext db, string no, long supplierId, int status, decimal grossAmount)
    {
        var invoice = new PurchaseInvoice
        {
            InvoiceType = PurchaseInvoiceRules.InvoiceTypeOrdinary,
            InvoiceNumber = no,
            NormalizedInvoiceNumber = no,
            InvoiceDate = DateTime.Today,
            SupplierId = supplierId,
            SupplierCode = "SUP",
            SupplierName = "供应商",
            Currency = "CNY",
            NetAmount = grossAmount,
            TaxAmount = 0m,
            GrossAmount = grossAmount,
            Status = status
        };
        db.PurchaseInvoices.Add(invoice);
        db.SaveChanges();
        return invoice;
    }

    private static void SeedInvoiceAllocation(ErpDbContext db, PurchaseInvoice invoice, PurchaseOrder order)
    {
        db.PurchaseInvoiceAllocations.Add(new PurchaseInvoiceAllocation
        {
            PurchaseInvoiceId = invoice.Id,
            PurchaseOrderId = order.Id,
            OrderNo = order.OrderNo,
            OrderDate = order.OrderDate,
            OrderCurrency = order.Currency.ToString(),
            SupplierId = order.SupplierId,
            SupplierCode = "SUP",
            SupplierName = "供应商",
            AllocatedAmount = invoice.GrossAmount,
            Currency = invoice.Currency
        });
        db.SaveChanges();
    }

    private static void SeedInvoicePaymentAllocation(ErpDbContext db, FinancePayment payment, PurchaseInvoice invoice,
        int status, decimal amount)
    {
        db.SupplierPaymentInvoiceAllocations.Add(new SupplierPaymentInvoiceAllocation
        {
            PaymentId = payment.Id,
            PaymentNo = payment.PaymentNo,
            PaymentDate = payment.PaymentDate,
            PaymentStatus = (int)payment.Status,
            PaymentStatusText = payment.Status.ToString(),
            PaymentAmount = payment.Amount,
            PurchaseInvoiceId = invoice.Id,
            InvoiceType = invoice.InvoiceType,
            InvoiceCode = invoice.InvoiceCode,
            InvoiceNumber = invoice.InvoiceNumber,
            InvoiceIdentityText = invoice.InvoiceNumber,
            InvoiceDate = invoice.InvoiceDate,
            InvoiceStatus = invoice.Status,
            InvoiceStatusText = invoice.Status.ToString(),
            InvoiceGrossAmount = invoice.GrossAmount,
            SupplierId = invoice.SupplierId,
            SupplierCode = "SUP",
            SupplierName = "供应商",
            AllocatedAmount = amount,
            Currency = invoice.Currency,
            Status = status,
            AllocatedAt = DateTime.Now,
            RecordedBy = "测试"
        });
        db.SaveChanges();
    }

}

