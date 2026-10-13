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
/// 供应商付款单生命周期护栏（ERP-351）单元测试：覆盖身份 / 菜单 / 客户数据范围、真实可用供应商 / 受支持币种 /
/// 正金额精度、货款申请单来源按 Id 解析（dangling / 已取消 / 币种不一致 / 金额超限）、有效付款引用证据（ERP-049 与
/// ERP-066）对取消 / 删除 / 修改供应商 / 币种 / 金额的拒绝、显式作废释放限制、无害元数据修改放行、
/// 未关联付款场景，以及两套引用跨消费者唯一分摊额度。全部使用内存库（TestDbFactory），不连接 SQL Server、
/// 不执行任何 SQL / 部署脚本。
/// </summary>
public class SupplierPaymentLifecycleTests
{
    // ==================== 0. 测试脚手架 ====================

    private static FinancePaymentController BuildController(ErpDbContext db)
        => new(db, new FakeDocumentNumberService());

    private sealed class FakeDocumentNumberService : IDocumentNumberService
    {
        private int _seq;
        public Task<string> GenerateAsync(DocumentType documentType, DateTime? date = null)
            => Task.FromResult($"FK{DateTime.Now:yyyyMMdd}{++_seq:D4}");
    }

    private static BaseSupplier SeedSupplier(ErpDbContext db, string code, string name, int status = 1)
    {
        var supplier = new BaseSupplier { SupplierCode = code, SupplierName = name, Status = status };
        db.BaseSuppliers.Add(supplier);
        db.SaveChanges();
        return supplier;
    }

    private static BaseCustomer SeedCustomer(ErpDbContext db, string code, string name, long? empId = null)
    {
        var customer = new BaseCustomer { CustomerCode = code, CustomerName = name, Status = 1, CreditStatus = "正常", EmpId = empId };
        db.BaseCustomers.Add(customer);
        db.SaveChanges();
        return customer;
    }

    private static FinancePaymentApply SeedPaymentApply(
        ErpDbContext db, string applyNo, long customerId, decimal amount = 1000m,
        Currency currency = Currency.CNY, DocumentStatus status = DocumentStatus.Approved, bool deleted = false)
    {
        var apply = new FinancePaymentApply
        {
            ApplyNo = applyNo,
            ApplyDate = new DateTime(2026, 9, 1),
            CustomerId = customerId,
            Amount = amount,
            Currency = currency,
            Status = status,
            IsDeleted = deleted
        };
        db.FinancePaymentApplies.Add(apply);
        db.SaveChanges();
        return apply;
    }

    private static FinancePayment SeedPayment(
        ErpDbContext db, string paymentNo, long supplierId, decimal amount = 1000m,
        Currency currency = Currency.CNY, DocumentStatus status = DocumentStatus.Pending,
        long? paymentApplyId = null, bool deleted = false)
    {
        var payment = new FinancePayment
        {
            PaymentNo = paymentNo,
            PaymentDate = new DateTime(2026, 9, 10),
            SupplierId = supplierId,
            PaymentApplyId = paymentApplyId,
            Amount = amount,
            Currency = currency,
            PaymentMethod = PaymentMethod.BankTransfer,
            BankAccount = "6222...",
            Status = status,
            Remark = "原备注",
            IsDeleted = deleted
        };
        db.FinancePayments.Add(payment);
        db.SaveChanges();
        return payment;
    }

    private static PurchaseOrder SeedOrder(
        ErpDbContext db, string orderNo, long supplierId, Currency currency = Currency.CNY,
        DocumentStatus status = DocumentStatus.Approved, decimal totalAmount = 1000m)
    {
        var order = new PurchaseOrder
        {
            OrderNo = orderNo,
            OrderDate = new DateTime(2026, 9, 1),
            SupplierId = supplierId,
            Currency = currency,
            TotalAmount = totalAmount,
            Status = status,
            ArrivalProgress = "未到货",
            SettlementProgress = "未结算"
        };
        db.PurchaseOrders.Add(order);
        db.SaveChanges();
        return order;
    }

    private static PurchaseInvoice SeedInvoice(
        ErpDbContext db, string number, long supplierId, decimal grossAmount = 1000m,
        string currency = "CNY", int status = PurchaseInvoiceRules.StatusRecorded)
    {
        var invoice = new PurchaseInvoice
        {
            InvoiceType = "普票",
            InvoiceNumber = number,
            NormalizedInvoiceNumber = PurchaseInvoiceRules.NormalizeIdentityPart(number),
            InvoiceDate = new DateTime(2026, 9, 20),
            SupplierId = supplierId,
            SupplierCode = "S" + supplierId,
            SupplierName = "供应商" + supplierId,
            Currency = currency,
            NetAmount = grossAmount,
            TaxAmount = 0m,
            GrossAmount = grossAmount,
            Status = status
        };
        db.PurchaseInvoices.Add(invoice);
        db.SaveChanges();
        return invoice;
    }

    private static SupplierPaymentAllocation SeedOrderAllocation(
        ErpDbContext db, FinancePayment payment, PurchaseOrder order, decimal amount, int status = 1)
    {
        var row = new SupplierPaymentAllocation
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
            SupplierId = payment.SupplierId,
            SupplierCode = "S" + payment.SupplierId,
            SupplierName = "供应商" + payment.SupplierId,
            AllocatedAmount = amount,
            Currency = order.Currency.ToString(),
            Status = status,
            AllocatedAt = DateTime.Now,
            VoidedAt = status == SupplierPaymentAllocationRules.StatusVoided ? DateTime.Now : null,
            VoidReason = status == SupplierPaymentAllocationRules.StatusVoided ? "录错" : string.Empty
        };
        db.SupplierPaymentAllocations.Add(row);
        db.SaveChanges();
        return row;
    }

    private static SupplierPaymentInvoiceAllocation SeedInvoiceAllocation(
        ErpDbContext db, FinancePayment payment, PurchaseInvoice invoice, decimal amount, int status = 1)
    {
        var row = new SupplierPaymentInvoiceAllocation
        {
            PaymentId = payment.Id,
            PaymentNo = payment.PaymentNo,
            PaymentDate = payment.PaymentDate,
            PaymentStatus = (int)payment.Status,
            PaymentStatusText = payment.Status.ToString(),
            PaymentAmount = payment.Amount,
            PurchaseInvoiceId = invoice.Id,
            InvoiceType = invoice.InvoiceType,
            InvoiceNumber = invoice.InvoiceNumber,
            InvoiceIdentityText = invoice.InvoiceNumber,
            InvoiceDate = invoice.InvoiceDate,
            InvoiceStatus = invoice.Status,
            InvoiceStatusText = invoice.Status.ToString(),
            InvoiceGrossAmount = invoice.GrossAmount,
            SupplierId = payment.SupplierId,
            SupplierCode = "S" + payment.SupplierId,
            SupplierName = "供应商" + payment.SupplierId,
            AllocatedAmount = amount,
            Currency = invoice.Currency,
            Status = status,
            AllocatedAt = DateTime.Now,
            RecordedBy = "tester",
            VoidedAt = status == SupplierPaymentInvoiceAllocationRules.StatusVoided ? DateTime.Now : null,
            VoidReason = status == SupplierPaymentInvoiceAllocationRules.StatusVoided ? "录错" : string.Empty
        };
        db.SupplierPaymentInvoiceAllocations.Add(row);
        db.SaveChanges();
        return row;
    }

    private static SysMenu EnsurePaymentMenu(ErpDbContext db)
    {
        var menu = new SysMenu
        {
            ParentId = 0,
            MenuCode = SupplierPaymentLifecycleRules.RequiredMenuCode,
            MenuName = SupplierPaymentLifecycleRules.RequiredMenuText,
            Path = "/finance/payment",
            MenuType = MenuType.Menu
        };
        db.SysMenus.Add(menu);
        db.SaveChanges();
        return menu;
    }

    private static long SeedPrivilegedUser(ErpDbContext db)
    {
        var role = new SysRole { RoleName = "付款特权角色", RoleCode = $"PaymentPrivileged-{Guid.NewGuid():N}", IsSystem = true };
        db.SysRoles.Add(role);
        db.SaveChanges();

        var user = new SysUser
        {
            UserName = $"payment-priv-{Guid.NewGuid():N}",
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = "付款特权用户",
            Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        db.SaveChanges();

        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        var menu = EnsurePaymentMenu(db);
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
        db.SaveChanges();
        return user.Id;
    }

    private static (long UserId, long EmployeeId) SeedRestrictedSalesman(ErpDbContext db, string userName)
    {
        var role = new SysRole { RoleName = "业务员角色", RoleCode = $"Sales-{Guid.NewGuid():N}", IsSystem = false };
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
        var menu = EnsurePaymentMenu(db);
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
        db.SaveChanges();

        var employee = new BaseEmployee { EmployeeCode = userName, EmployeeName = userName, IsSalesman = true, Status = 1 };
        db.BaseEmployees.Add(employee);
        db.SaveChanges();
        return (user.Id, employee.Id);
    }

    private static T AssertOk<T>(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<ApiResponse<T>>(ok.Value);
        Assert.Equal(ErrorCodes.Success, resp.Code);
        Assert.NotNull(resp.Data);
        return resp.Data!;
    }

    private static async Task<BusinessException> AssertBusinessCodeAsync(int expectedCode, Func<Task> action)
    {
        var ex = await Assert.ThrowsAsync<BusinessException>(action);
        Assert.Equal(expectedCode, ex.Code);
        return ex;
    }

    // ==================== 1. 创建：身份 / 菜单 / 数据范围 / 供应商 / 币种 / 金额 / 来源 ====================

    [Fact]
    public async Task Create_关联申请单_成功并取整金额与币种()
    {
        using var db = TestDbFactory.Create();
        var userId = SeedPrivilegedUser(db);
        var customer = SeedCustomer(db, "C001", "客户A");
        var supplier = SeedSupplier(db, "S001", "义乌档口");
        var apply = SeedPaymentApply(db, "DJ-1", customer.Id, amount: 1000m, currency: Currency.USD);

        var controller = BuildController(db);
        TestAuth.SetUser(controller, userId);

        await controller.Create(new FinancePayment
        {
            SupplierId = supplier.Id,
            PaymentApplyId = apply.Id,
            Amount = 123.456m,
            Currency = Currency.USD,
            PaymentDate = new DateTime(2026, 9, 10),
            PaymentMethod = PaymentMethod.BankTransfer,
            BankAccount = "6222...",
            Remark = "首款"
        });

        var saved = await db.FinancePayments.AsNoTracking().SingleAsync();
        Assert.Equal(123.46m, saved.Amount);
        Assert.Equal(Currency.USD, saved.Currency);
        Assert.Equal(apply.Id, saved.PaymentApplyId);
        Assert.Equal(DocumentStatus.Pending, saved.Status);
    }

    [Fact]
    public async Task Create_未关联申请单_特权账号放行()
    {
        using var db = TestDbFactory.Create();
        var userId = SeedPrivilegedUser(db);
        var supplier = SeedSupplier(db, "S001", "义乌档口");

        var controller = BuildController(db);
        TestAuth.SetUser(controller, userId);

        await controller.Create(new FinancePayment
        {
            SupplierId = supplier.Id,
            PaymentApplyId = null,
            Amount = 500m,
            Currency = Currency.CNY
        });

        var saved = await db.FinancePayments.AsNoTracking().SingleAsync();
        Assert.Null(saved.PaymentApplyId);
        Assert.Equal(500m, saved.Amount);
    }

    [Fact]
    public async Task Create_无身份_未认证()
    {
        using var db = TestDbFactory.Create();
        var supplier = SeedSupplier(db, "S001", "义乌档口");
        var controller = BuildController(db);
        TestAuth.SetUser(controller, null);

        await AssertBusinessCodeAsync(ErrorCodes.Unauthorized, () => controller.Create(new FinancePayment
        {
            SupplierId = supplier.Id,
            Amount = 500m,
            Currency = Currency.CNY
        }));
    }

    [Fact]
    public async Task Create_无付款菜单_权限不足()
    {
        using var db = TestDbFactory.Create();
        var supplier = SeedSupplier(db, "S001", "义乌档口");
        var role = new SysRole { RoleName = "无菜单角色", RoleCode = $"NoMenu-{Guid.NewGuid():N}", IsSystem = false };
        db.SysRoles.Add(role);
        db.SaveChanges();
        var user = new SysUser { UserName = "nomenu", PasswordHash = "h", PasswordSalt = "s", DisplayName = "nomenu", Status = UserStatus.Enabled };
        db.SysUsers.Add(user);
        db.SaveChanges();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        db.SaveChanges();

        var controller = BuildController(db);
        TestAuth.SetUser(controller, user.Id);

        await AssertBusinessCodeAsync(ErrorCodes.Forbidden, () => controller.Create(new FinancePayment
        {
            SupplierId = supplier.Id,
            Amount = 500m,
            Currency = Currency.CNY
        }));
    }

    [Fact]
    public async Task Create_越界客户_权限不足()
    {
        using var db = TestDbFactory.Create();
        var (userId, employeeId) = SeedRestrictedSalesman(db, "sales01");
        var inScope = SeedCustomer(db, "C-IN", "范围内", empId: employeeId);
        var outOfScope = SeedCustomer(db, "C-OUT", "范围外", empId: 9999L);
        var supplier = SeedSupplier(db, "S001", "义乌档口");
        var apply = SeedPaymentApply(db, "DJ-OUT", outOfScope.Id, amount: 1000m, currency: Currency.CNY);

        var controller = BuildController(db);
        TestAuth.SetUser(controller, userId);

        await AssertBusinessCodeAsync(ErrorCodes.Forbidden, () => controller.Create(new FinancePayment
        {
            SupplierId = supplier.Id,
            PaymentApplyId = apply.Id,
            Amount = 500m,
            Currency = Currency.CNY
        }));
        Assert.NotNull(inScope);
    }

    [Fact]
    public async Task Create_停用供应商_规则冲突()
    {
        using var db = TestDbFactory.Create();
        var userId = SeedPrivilegedUser(db);
        var supplier = SeedSupplier(db, "S001", "义乌档口", status: 0);

        var controller = BuildController(db);
        TestAuth.SetUser(controller, userId);

        await AssertBusinessCodeAsync(ErrorCodes.RuleConflict, () => controller.Create(new FinancePayment
        {
            SupplierId = supplier.Id,
            Amount = 500m,
            Currency = Currency.CNY
        }));
    }

    [Fact]
    public async Task Create_悬空申请单_不存在()
    {
        using var db = TestDbFactory.Create();
        var userId = SeedPrivilegedUser(db);
        var supplier = SeedSupplier(db, "S001", "义乌档口");

        var controller = BuildController(db);
        TestAuth.SetUser(controller, userId);

        await AssertBusinessCodeAsync(ErrorCodes.NotFound, () => controller.Create(new FinancePayment
        {
            SupplierId = supplier.Id,
            PaymentApplyId = 999999L,
            Amount = 500m,
            Currency = Currency.CNY
        }));
    }

    [Fact]
    public async Task Create_已取消申请单_规则冲突()
    {
        using var db = TestDbFactory.Create();
        var userId = SeedPrivilegedUser(db);
        var customer = SeedCustomer(db, "C001", "客户A");
        var supplier = SeedSupplier(db, "S001", "义乌档口");
        var apply = SeedPaymentApply(db, "DJ-1", customer.Id, amount: 1000m, currency: Currency.CNY, status: DocumentStatus.Cancelled);

        var controller = BuildController(db);
        TestAuth.SetUser(controller, userId);

        await AssertBusinessCodeAsync(ErrorCodes.RuleConflict, () => controller.Create(new FinancePayment
        {
            SupplierId = supplier.Id,
            PaymentApplyId = apply.Id,
            Amount = 500m,
            Currency = Currency.CNY
        }));
    }

    [Fact]
    public async Task Create_申请单币种不一致_规则冲突()
    {
        using var db = TestDbFactory.Create();
        var userId = SeedPrivilegedUser(db);
        var customer = SeedCustomer(db, "C001", "客户A");
        var supplier = SeedSupplier(db, "S001", "义乌档口");
        var apply = SeedPaymentApply(db, "DJ-1", customer.Id, amount: 1000m, currency: Currency.USD);

        var controller = BuildController(db);
        TestAuth.SetUser(controller, userId);

        await AssertBusinessCodeAsync(ErrorCodes.RuleConflict, () => controller.Create(new FinancePayment
        {
            SupplierId = supplier.Id,
            PaymentApplyId = apply.Id,
            Amount = 500m,
            Currency = Currency.CNY
        }));
    }

    [Fact]
    public async Task Create_金额超申请单_规则冲突()
    {
        using var db = TestDbFactory.Create();
        var userId = SeedPrivilegedUser(db);
        var customer = SeedCustomer(db, "C001", "客户A");
        var supplier = SeedSupplier(db, "S001", "义乌档口");
        var apply = SeedPaymentApply(db, "DJ-1", customer.Id, amount: 300m, currency: Currency.CNY);

        var controller = BuildController(db);
        TestAuth.SetUser(controller, userId);

        await AssertBusinessCodeAsync(ErrorCodes.RuleConflict, () => controller.Create(new FinancePayment
        {
            SupplierId = supplier.Id,
            PaymentApplyId = apply.Id,
            Amount = 500m,
            Currency = Currency.CNY
        }));
    }

    [Fact]
    public async Task Create_金额非正_参数错误()
    {
        using var db = TestDbFactory.Create();
        var userId = SeedPrivilegedUser(db);
        var supplier = SeedSupplier(db, "S001", "义乌档口");

        var controller = BuildController(db);
        TestAuth.SetUser(controller, userId);

        await AssertBusinessCodeAsync(ErrorCodes.InvalidParameter, () => controller.Create(new FinancePayment
        {
            SupplierId = supplier.Id,
            Amount = 0m,
            Currency = Currency.CNY
        }));
    }

    // ==================== 2. 有效引用证据保护：修改 / 取消 / 删除 ====================

    [Fact]
    public async Task Update_有效订单引用_改金额_拒绝且原值不变()
    {
        using var db = TestDbFactory.Create();
        var userId = SeedPrivilegedUser(db);
        var supplier = SeedSupplier(db, "S001", "义乌档口");
        var payment = SeedPayment(db, "FK-1", supplier.Id, amount: 1000m);
        var order = SeedOrder(db, "PO-1", supplier.Id);
        SeedOrderAllocation(db, payment, order, amount: 300m);

        var controller = BuildController(db);
        TestAuth.SetUser(controller, userId);

        await AssertBusinessCodeAsync(ErrorCodes.RuleConflict, () => controller.Update(payment.Id, new FinancePayment
        {
            SupplierId = supplier.Id,
            PaymentApplyId = null,
            Amount = 800m,
            Currency = Currency.CNY
        }));

        var stored = await db.FinancePayments.AsNoTracking().SingleAsync();
        Assert.Equal(1000m, stored.Amount);
        Assert.Equal(300m, (await db.SupplierPaymentAllocations.AsNoTracking().SingleAsync()).AllocatedAmount);
    }

    [Fact]
    public async Task Update_有效发票引用_改供应商_拒绝()
    {
        using var db = TestDbFactory.Create();
        var userId = SeedPrivilegedUser(db);
        var supplierA = SeedSupplier(db, "S001", "义乌档口");
        var supplierB = SeedSupplier(db, "S002", "另一家");
        var payment = SeedPayment(db, "FK-1", supplierA.Id, amount: 1000m);
        var invoice = SeedInvoice(db, "INV-1", supplierA.Id);
        SeedInvoiceAllocation(db, payment, invoice, amount: 200m);

        var controller = BuildController(db);
        TestAuth.SetUser(controller, userId);

        await AssertBusinessCodeAsync(ErrorCodes.RuleConflict, () => controller.Update(payment.Id, new FinancePayment
        {
            SupplierId = supplierB.Id,
            PaymentApplyId = null,
            Amount = 1000m,
            Currency = Currency.CNY
        }));

        Assert.Equal(supplierA.Id, (await db.FinancePayments.AsNoTracking().SingleAsync()).SupplierId);
    }

    [Fact]
    public async Task Update_有效引用_改币种_拒绝()
    {
        using var db = TestDbFactory.Create();
        var userId = SeedPrivilegedUser(db);
        var supplier = SeedSupplier(db, "S001", "义乌档口");
        var payment = SeedPayment(db, "FK-1", supplier.Id, amount: 1000m, currency: Currency.CNY);
        var order = SeedOrder(db, "PO-1", supplier.Id, currency: Currency.CNY);
        SeedOrderAllocation(db, payment, order, amount: 300m);

        var controller = BuildController(db);
        TestAuth.SetUser(controller, userId);

        await AssertBusinessCodeAsync(ErrorCodes.RuleConflict, () => controller.Update(payment.Id, new FinancePayment
        {
            SupplierId = supplier.Id,
            PaymentApplyId = null,
            Amount = 1000m,
            Currency = Currency.USD
        }));

        Assert.Equal(Currency.CNY, (await db.FinancePayments.AsNoTracking().SingleAsync()).Currency);
    }

    [Fact]
    public async Task Update_有效引用_无害元数据修改_放行()
    {
        using var db = TestDbFactory.Create();
        var userId = SeedPrivilegedUser(db);
        var supplier = SeedSupplier(db, "S001", "义乌档口");
        var payment = SeedPayment(db, "FK-1", supplier.Id, amount: 1000m);
        var order = SeedOrder(db, "PO-1", supplier.Id);
        SeedOrderAllocation(db, payment, order, amount: 300m);

        var controller = BuildController(db);
        TestAuth.SetUser(controller, userId);

        await controller.Update(payment.Id, new FinancePayment
        {
            SupplierId = supplier.Id,
            PaymentApplyId = null,
            Amount = 1000m,
            Currency = Currency.CNY,
            PaymentDate = new DateTime(2026, 9, 11),
            PaymentMethod = PaymentMethod.Cash,
            BankAccount = "NEW-ACCOUNT",
            Remark = "新备注"
        });

        var stored = await db.FinancePayments.AsNoTracking().SingleAsync();
        Assert.Equal(new DateTime(2026, 9, 11), stored.PaymentDate);
        Assert.Equal(PaymentMethod.Cash, stored.PaymentMethod);
        Assert.Equal("NEW-ACCOUNT", stored.BankAccount);
        Assert.Equal("新备注", stored.Remark);
        Assert.Equal(1000m, stored.Amount);
    }

    [Fact]
    public async Task Cancel_有效引用_拒绝()
    {
        using var db = TestDbFactory.Create();
        var userId = SeedPrivilegedUser(db);
        var supplier = SeedSupplier(db, "S001", "义乌档口");
        var payment = SeedPayment(db, "FK-1", supplier.Id, amount: 1000m);
        var order = SeedOrder(db, "PO-1", supplier.Id);
        SeedOrderAllocation(db, payment, order, amount: 300m);

        var controller = BuildController(db);
        TestAuth.SetUser(controller, userId);

        await AssertBusinessCodeAsync(ErrorCodes.RuleConflict, () => controller.Cancel(payment.Id));
        Assert.Equal(DocumentStatus.Pending, (await db.FinancePayments.AsNoTracking().SingleAsync()).Status);
    }

    [Fact]
    public async Task Delete_有效引用_拒绝()
    {
        using var db = TestDbFactory.Create();
        var userId = SeedPrivilegedUser(db);
        var supplier = SeedSupplier(db, "S001", "义乌档口");
        var payment = SeedPayment(db, "FK-1", supplier.Id, amount: 1000m);
        var invoice = SeedInvoice(db, "INV-1", supplier.Id);
        SeedInvoiceAllocation(db, payment, invoice, amount: 200m);

        var controller = BuildController(db);
        TestAuth.SetUser(controller, userId);

        await AssertBusinessCodeAsync(ErrorCodes.RuleConflict, () => controller.Delete(payment.Id));
        Assert.False((await db.FinancePayments.AsNoTracking().SingleAsync()).IsDeleted);
    }

    [Fact]
    public async Task 作废释放后_改金额与取消_放行()
    {
        using var db = TestDbFactory.Create();
        var userId = SeedPrivilegedUser(db);
        var supplier = SeedSupplier(db, "S001", "义乌档口");
        var payment = SeedPayment(db, "FK-1", supplier.Id, amount: 1000m);
        var order = SeedOrder(db, "PO-1", supplier.Id);
        var row = SeedOrderAllocation(db, payment, order, amount: 300m, status: SupplierPaymentAllocationRules.StatusVoided);

        var controller = BuildController(db);
        TestAuth.SetUser(controller, userId);

        await controller.Update(payment.Id, new FinancePayment
        {
            SupplierId = supplier.Id,
            PaymentApplyId = null,
            Amount = 800m,
            Currency = Currency.CNY
        });
        Assert.Equal(800m, (await db.FinancePayments.AsNoTracking().SingleAsync()).Amount);

        await controller.Cancel(payment.Id);
        Assert.Equal(DocumentStatus.Cancelled, (await db.FinancePayments.AsNoTracking().SingleAsync()).Status);
        Assert.Equal(SupplierPaymentAllocationRules.StatusVoided, row.Status);
    }

    // ==================== 3. 提交 / 审核范围复核 ====================

    [Fact]
    public async Task Submit_无付款菜单_权限不足()
    {
        using var db = TestDbFactory.Create();
        var supplier = SeedSupplier(db, "S001", "义乌档口");
        var payment = SeedPayment(db, "FK-1", supplier.Id, amount: 1000m);

        var role = new SysRole { RoleName = "无菜单角色", RoleCode = $"NoMenu-{Guid.NewGuid():N}", IsSystem = false };
        db.SysRoles.Add(role);
        db.SaveChanges();
        var user = new SysUser { UserName = "nomenu2", PasswordHash = "h", PasswordSalt = "s", DisplayName = "nomenu2", Status = UserStatus.Enabled };
        db.SysUsers.Add(user);
        db.SaveChanges();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        db.SaveChanges();

        var controller = BuildController(db);
        TestAuth.SetUser(controller, user.Id);

        await AssertBusinessCodeAsync(ErrorCodes.Forbidden, () => controller.Submit(payment.Id));
    }

    [Fact]
    public async Task Approve_越界客户_权限不足()
    {
        using var db = TestDbFactory.Create();
        var (userId, employeeId) = SeedRestrictedSalesman(db, "sales02");
        var inScope = SeedCustomer(db, "C-IN", "范围内", empId: employeeId);
        var outOfScope = SeedCustomer(db, "C-OUT", "范围外", empId: 9999L);
        var supplier = SeedSupplier(db, "S001", "义乌档口");
        var apply = SeedPaymentApply(db, "DJ-OUT", outOfScope.Id, amount: 1000m, currency: Currency.CNY);
        var payment = SeedPayment(db, "FK-OUT", supplier.Id, amount: 500m, currency: Currency.CNY,
            status: DocumentStatus.Submitted, paymentApplyId: apply.Id);

        var controller = BuildController(db);
        TestAuth.SetUser(controller, userId);

        await AssertBusinessCodeAsync(ErrorCodes.Forbidden, () => controller.Approve(payment.Id));
        Assert.NotNull(inScope);
    }

    // ==================== 4. 跨消费者唯一分摊额度（ERP-049 + ERP-066） ====================

    private static SupplierPaymentAllocationSaveDto OrderDto(long paymentId, long orderId, decimal amount)
        => new() { PaymentId = paymentId, PurchaseOrderId = orderId, AllocatedAmount = amount, Remark = string.Empty };

    private static SupplierPaymentInvoiceAllocationSaveDto InvoiceDto(long paymentId, long invoiceId, decimal amount)
        => new() { PaymentId = paymentId, PurchaseInvoiceId = invoiceId, AllocatedAmount = amount, Remark = string.Empty };

    [Fact]
    public async Task 跨消费者_订单80_发票20_精确合计100_成功()
    {
        using var db = TestDbFactory.Create();
        var supplier = SeedSupplier(db, "S001", "义乌档口");
        var payment = SeedPayment(db, "FK-1", supplier.Id, amount: 100m, currency: Currency.CNY);
        var order = SeedOrder(db, "PO-1", supplier.Id, Currency.CNY, totalAmount: 500m);
        var invoice = SeedInvoice(db, "INV-1", supplier.Id, grossAmount: 500m, currency: "CNY");

        await SupplierPaymentAllocationService.CreateAsync(db, OrderDto(payment.Id, order.Id, 80m));
        await SupplierPaymentInvoiceAllocationService.CreateAsync(db, InvoiceDto(payment.Id, invoice.Id, 20m), "tester");

        var funding = await SupplierPaymentLifecycleRules.LoadPaymentFundingAsync(db, payment.Id);
        Assert.Equal(80m, funding.OrderAllocated);
        Assert.Equal(20m, funding.InvoiceAllocated);
        Assert.Equal(100m, funding.CombinedAllocated);
    }

    [Fact]
    public async Task 跨消费者_合计超额_拒绝且两表不变()
    {
        using var db = TestDbFactory.Create();
        var supplier = SeedSupplier(db, "S001", "义乌档口");
        var payment = SeedPayment(db, "FK-1", supplier.Id, amount: 100m, currency: Currency.CNY);
        var order = SeedOrder(db, "PO-1", supplier.Id, Currency.CNY, totalAmount: 500m);
        var invoice = SeedInvoice(db, "INV-1", supplier.Id, grossAmount: 500m, currency: "CNY");

        await SupplierPaymentAllocationService.CreateAsync(db, OrderDto(payment.Id, order.Id, 80m));
        await AssertBusinessCodeAsync(ErrorCodes.RuleConflict,
            () => SupplierPaymentInvoiceAllocationService.CreateAsync(db, InvoiceDto(payment.Id, invoice.Id, 21m), "tester"));

        Assert.Single(await db.SupplierPaymentAllocations.AsNoTracking().ToListAsync());
        Assert.Empty(await db.SupplierPaymentInvoiceAllocations.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task 跨消费者_作废释放后_可继续引用()
    {
        using var db = TestDbFactory.Create();
        var supplier = SeedSupplier(db, "S001", "义乌档口");
        var payment = SeedPayment(db, "FK-1", supplier.Id, amount: 100m, currency: Currency.CNY);
        var order = SeedOrder(db, "PO-1", supplier.Id, Currency.CNY, totalAmount: 500m);
        var invoice = SeedInvoice(db, "INV-1", supplier.Id, grossAmount: 500m, currency: "CNY");

        var orderRow = await SupplierPaymentAllocationService.CreateAsync(db, OrderDto(payment.Id, order.Id, 80m));
        await SupplierPaymentAllocationService.VoidAsync(db, orderRow.Id, "录错");
        await SupplierPaymentInvoiceAllocationService.CreateAsync(db, InvoiceDto(payment.Id, invoice.Id, 100m), "tester");

        var funding = await SupplierPaymentLifecycleRules.LoadPaymentFundingAsync(db, payment.Id);
        Assert.Equal(0m, funding.OrderAllocated);
        Assert.Equal(100m, funding.InvoiceAllocated);
    }

    [Fact]
    public async Task 跨消费者_币种不一致_发票引用失败关闭且不跨币种合计()
    {
        using var db = TestDbFactory.Create();
        var supplier = SeedSupplier(db, "S001", "义乌档口");
        var payment = SeedPayment(db, "FK-1", supplier.Id, amount: 100m, currency: Currency.CNY);
        var order = SeedOrder(db, "PO-1", supplier.Id, Currency.CNY, totalAmount: 500m);
        var invoiceUsd = SeedInvoice(db, "INV-USD", supplier.Id, grossAmount: 500m, currency: "USD");

        await SupplierPaymentAllocationService.CreateAsync(db, OrderDto(payment.Id, order.Id, 80m));
        await AssertBusinessCodeAsync(ErrorCodes.RuleConflict,
            () => SupplierPaymentInvoiceAllocationService.CreateAsync(db, InvoiceDto(payment.Id, invoiceUsd.Id, 20m), "tester"));

        var funding = await SupplierPaymentLifecycleRules.LoadPaymentFundingAsync(db, payment.Id);
        Assert.Equal(80m, funding.OrderAllocated);
        Assert.Equal(0m, funding.InvoiceAllocated);
    }

    // ==================== 6. 读取 / 生命周期闸门先于付款单查询与行锁（ERP-351 验收恢复） ====================

    /// <summary>播种一个启用但<strong>没有</strong>付款单（payment）菜单授权的账号（用于证明闸门先于读取）。</summary>
    private static long SeedEnabledUserWithoutPaymentMenu(ErpDbContext db, string userName)
    {
        var role = new SysRole { RoleName = "无付款菜单角色", RoleCode = $"NoPaymentMenu-{Guid.NewGuid():N}", IsSystem = false };
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
        db.SaveChanges();
        return user.Id;
    }

    [Fact]
    public async Task GetById_无身份_未知Id与存在Id一律未认证()
    {
        using var db = TestDbFactory.Create();
        var supplier = SeedSupplier(db, "S001", "义乌档口");
        var payment = SeedPayment(db, "FK-1", supplier.Id);

        var controller = BuildController(db);
        TestAuth.SetUser(controller, null);

        // 未知 Id 必须先走身份 / 菜单闸门，绝不先返回 NotFound 而差异化泄露存在性。
        await AssertBusinessCodeAsync(ErrorCodes.Unauthorized, () => controller.GetById(987_654_321L));
        await AssertBusinessCodeAsync(ErrorCodes.Unauthorized, () => controller.GetById(payment.Id));
    }

    [Fact]
    public async Task GetById_未绑定请求上下文_未认证而非异常()
    {
        using var db = TestDbFactory.Create();
        var supplier = SeedSupplier(db, "S001", "义乌档口");
        var payment = SeedPayment(db, "FK-1", supplier.Id);

        // 纯进程内直调、完全不绑定 HttpContext：缺失身份安全解析为 null → 受控未认证（绝不抛 NRE、绝不豁免）。
        var controller = BuildController(db);

        await AssertBusinessCodeAsync(ErrorCodes.Unauthorized, () => controller.GetById(payment.Id));
        await AssertBusinessCodeAsync(ErrorCodes.Unauthorized, () => controller.GetById(987_654_321L));
    }

    [Fact]
    public async Task GetById_无付款单菜单_未知Id与存在Id一律权限不足()
    {
        using var db = TestDbFactory.Create();
        var supplier = SeedSupplier(db, "S001", "义乌档口");
        var payment = SeedPayment(db, "FK-1", supplier.Id);
        var userId = SeedEnabledUserWithoutPaymentMenu(db, "nopaymenu");

        var controller = BuildController(db);
        TestAuth.SetUser(controller, userId);

        await AssertBusinessCodeAsync(ErrorCodes.Forbidden, () => controller.GetById(987_654_321L));
        await AssertBusinessCodeAsync(ErrorCodes.Forbidden, () => controller.GetById(payment.Id));
    }

    [Fact]
    public async Task GetById_账号禁用_未知Id与存在Id一律权限不足()
    {
        using var db = TestDbFactory.Create();
        var supplier = SeedSupplier(db, "S001", "义乌档口");
        var payment = SeedPayment(db, "FK-1", supplier.Id);
        var userId = SeedPrivilegedUser(db);
        db.SysUsers.Single(u => u.Id == userId).Status = UserStatus.Disabled;
        db.SaveChanges();

        var controller = BuildController(db);
        TestAuth.SetUser(controller, userId);

        await AssertBusinessCodeAsync(ErrorCodes.Forbidden, () => controller.GetById(987_654_321L));
        await AssertBusinessCodeAsync(ErrorCodes.Forbidden, () => controller.GetById(payment.Id));
    }

    [Fact]
    public async Task GetById_账号已删除_未知Id与存在Id一律未认证()
    {
        using var db = TestDbFactory.Create();
        var supplier = SeedSupplier(db, "S001", "义乌档口");
        var payment = SeedPayment(db, "FK-1", supplier.Id);
        var userId = SeedPrivilegedUser(db);
        db.SysUsers.Single(u => u.Id == userId).IsDeleted = true;
        db.SaveChanges();

        var controller = BuildController(db);
        TestAuth.SetUser(controller, userId);

        await AssertBusinessCodeAsync(ErrorCodes.Unauthorized, () => controller.GetById(987_654_321L));
        await AssertBusinessCodeAsync(ErrorCodes.Unauthorized, () => controller.GetById(payment.Id));
    }

    [Fact]
    public async Task GetById_越界账号_未知Id不存在_范围外Id权限不足()
    {
        using var db = TestDbFactory.Create();
        var (userId, employeeId) = SeedRestrictedSalesman(db, "sales351");
        var inScope = SeedCustomer(db, "C-IN", "范围内客户", empId: employeeId);
        var outOfScope = SeedCustomer(db, "C-OUT", "范围外客户", empId: 9999L);
        var supplier = SeedSupplier(db, "S001", "义乌档口");
        var apply = SeedPaymentApply(db, "DJ-OUT", outOfScope.Id, amount: 1000m, currency: Currency.CNY);
        var payment = SeedPayment(db, "FK-OUT", supplier.Id, amount: 1000m, paymentApplyId: apply.Id);

        var controller = BuildController(db);
        TestAuth.SetUser(controller, userId);

        // 越界账号：身份 / 菜单闸门先通过，再按权威读取判定不存在；范围外付款单一律权限不足，不泄露归属。
        await AssertBusinessCodeAsync(ErrorCodes.NotFound, () => controller.GetById(987_654_321L));
        await AssertBusinessCodeAsync(ErrorCodes.Forbidden, () => controller.GetById(payment.Id));
        Assert.NotNull(inScope);
    }

    [Fact]
    public async Task GetById_有权限_未知Id_不存在()
    {
        using var db = TestDbFactory.Create();
        var userId = SeedPrivilegedUser(db);

        var controller = BuildController(db);
        TestAuth.SetUser(controller, userId);

        // 授权通过后才按权威读取给出不存在：身份 / 菜单闸门先于不存在判定。
        await AssertBusinessCodeAsync(ErrorCodes.NotFound, () => controller.GetById(987_654_321L));
    }

    [Fact]
    public async Task 生命周期动作_未知Id_缺失禁用无菜单账号_一律受控拒绝且零状态变化()
    {
        using var db = TestDbFactory.Create();
        var supplier = SeedSupplier(db, "S001", "义乌档口");
        var payment = SeedPayment(db, "FK-1", supplier.Id, amount: 1000m);
        var noMenuId = SeedEnabledUserWithoutPaymentMenu(db, "nomenu351");
        var disabledId = SeedPrivilegedUser(db);
        db.SysUsers.Single(u => u.Id == disabledId).Status = UserStatus.Disabled;
        db.SaveChanges();

        var before = db.FinancePayments.AsNoTracking().Single(p => p.Id == payment.Id);
        const long unknownId = 987_654_321L;

        var anonymous = BuildController(db);
        TestAuth.SetUser(anonymous, null);
        await AssertBusinessCodeAsync(ErrorCodes.Unauthorized, () => anonymous.Submit(unknownId));
        await AssertBusinessCodeAsync(ErrorCodes.Unauthorized, () => anonymous.Cancel(payment.Id));

        var noMenu = BuildController(db);
        TestAuth.SetUser(noMenu, noMenuId);
        await AssertBusinessCodeAsync(ErrorCodes.Forbidden, () => noMenu.Delete(unknownId));
        await AssertBusinessCodeAsync(ErrorCodes.Forbidden, () => noMenu.Update(unknownId, new FinancePayment
        {
            SupplierId = supplier.Id,
            Amount = 500m,
            Currency = Currency.CNY
        }));

        var disabled = BuildController(db);
        TestAuth.SetUser(disabled, disabledId);
        await AssertBusinessCodeAsync(ErrorCodes.Forbidden, () => disabled.Approve(unknownId));
        await AssertBusinessCodeAsync(ErrorCodes.Forbidden, () => disabled.Cancel(payment.Id));

        // 被拒绝的生命周期动作不改变原始付款单（状态、字段与审计时间戳保持不变，也绝不生成引用行）。
        var stored = db.FinancePayments.AsNoTracking().Single(p => p.Id == payment.Id);
        Assert.Equal(DocumentStatus.Pending, stored.Status);
        Assert.Equal(1000m, stored.Amount);
        Assert.False(stored.IsDeleted);
        Assert.Equal(before.UpdatedAt, stored.UpdatedAt);
        Assert.Empty(db.SupplierPaymentAllocations.AsNoTracking());
        Assert.Empty(db.SupplierPaymentInvoiceAllocations.AsNoTracking());
    }

    [Fact]
    public void 读取闸门口径文案_与实现同源()
    {
        var text = SupplierPaymentLifecycleRules.ReadGateOrderText;
        Assert.Contains("先执行实时身份 + 既有「付款单」（payment）菜单闸门", text);
        Assert.Contains("安全解析为 null", text);
        Assert.Contains("完全相同的受控拒绝", text);
        Assert.Contains("绝不先读行 / 先加锁再判权", text);
        Assert.DoesNotContain("豁免为", text);
    }


}






