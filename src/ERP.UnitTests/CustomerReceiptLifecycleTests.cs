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
/// 客户收款单生命周期护栏（ERP-349）单元测试：覆盖身份 / 菜单 / 客户数据范围（无身份、无菜单、越界客户、
/// 未映射业务员）、真实可用客户 / 受支持币种 / 正金额精度校验、有效收款分摊证据（ERP-053 与 ERP-071）对
/// 取消 / 删除 / 修改客户 / 币种 / 金额的拒绝、显式作废释放限制、允许无害元数据修改、重复操作拒绝，
/// 以及提交 / 审核 / 列表 / 详情的范围复核。全部使用内存库（TestDbFactory），不连接 SQL Server、
/// 不执行任何 SQL / 部署脚本、不做任何浏览器 / UI 验收。
/// </summary>
public class CustomerReceiptLifecycleTests
{
    // ==================== 0. 测试脚手架 ====================

    private static FinanceReceiptController BuildController(ErpDbContext db)
        => new(db, new FakeDocumentNumberService());

    private sealed class FakeDocumentNumberService : IDocumentNumberService
    {
        private int _seq;
        public Task<string> GenerateAsync(DocumentType documentType, DateTime? date = null)
            => Task.FromResult($"RC{DateTime.Now:yyyyMMdd}{++_seq:D4}");
    }

    private static BaseCustomer SeedCustomer(
        ErpDbContext db, string code, string name, int status = 1, long? empId = null, bool deleted = false)
    {
        var customer = new BaseCustomer
        {
            CustomerCode = code,
            CustomerName = name,
            Status = status,
            CreditStatus = "正常",
            EmpId = empId,
            IsDeleted = deleted
        };
        db.BaseCustomers.Add(customer);
        db.SaveChanges();
        return customer;
    }

    private static FinanceReceipt SeedReceipt(
        ErpDbContext db, string receiptNo, long customerId, decimal amount = 1000m,
        Currency currency = Currency.USD, DocumentStatus status = DocumentStatus.Pending)
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

    private static SalesOrder SeedOrder(
        ErpDbContext db, string orderNo, long customerId, Currency currency = Currency.USD,
        DocumentStatus status = DocumentStatus.Approved)
    {
        var order = new SalesOrder
        {
            OrderNo = orderNo,
            OrderDate = new DateTime(2026, 9, 1),
            CustomerId = customerId,
            Currency = currency,
            Status = status
        };
        db.SalesOrders.Add(order);
        db.SaveChanges();
        return order;
    }

    private static CustomerReceiptAllocation SeedCustomerOrderAllocation(
        ErpDbContext db, FinanceReceipt receipt, SalesOrder order, decimal amount = 100m, int status = 1)
    {
        var row = new CustomerReceiptAllocation
        {
            ReceiptId = receipt.Id,
            ReceiptNo = receipt.ReceiptNo,
            ReceiptDate = receipt.ReceiptDate,
            ReceiptStatus = (int)receipt.Status,
            ReceiptStatusText = receipt.Status.ToString(),
            ReceiptAmount = receipt.Amount,
            SalesOrderId = order.Id,
            OrderNo = order.OrderNo,
            OrderDate = order.OrderDate,
            OrderStatus = (int)order.Status,
            OrderCurrency = order.Currency.ToString(),
            CustomerId = receipt.CustomerId,
            CustomerCode = "CUST",
            CustomerName = "客户",
            AllocatedAmount = amount,
            Currency = order.Currency.ToString(),
            Status = status,
            AllocatedAt = DateTime.Now,
            VoidedAt = status == CustomerReceiptAllocationRules.StatusVoided ? DateTime.Now : null,
            VoidReason = status == CustomerReceiptAllocationRules.StatusVoided ? "录错" : string.Empty
        };
        db.CustomerReceiptAllocations.Add(row);
        db.SaveChanges();
        return row;
    }

    private static AgencyServiceFeeCollectionAllocation SeedAgencyFeeAllocation(
        ErpDbContext db, FinanceReceipt receipt, decimal amount = 100m, int status = 1)
    {
        var row = new AgencyServiceFeeCollectionAllocation
        {
            StatementId = 990001L,
            StatementNo = "STMT-990001",
            StatementDate = new DateTime(2026, 9, 5),
            StatementStatus = 1,
            StatementStatusText = "已登记",
            StatementTotalAmount = 5000m,
            StatementCurrency = receipt.Currency.ToString(),
            StatementAgreementId = 1L,
            StatementAgreementNo = "AG-1",
            ReceiptId = receipt.Id,
            ReceiptNo = receipt.ReceiptNo,
            ReceiptDate = receipt.ReceiptDate,
            ReceiptStatus = (int)receipt.Status,
            ReceiptStatusText = receipt.Status.ToString(),
            ReceiptAmount = receipt.Amount,
            CustomerId = receipt.CustomerId,
            CustomerCode = "CUST",
            CustomerName = "客户",
            AllocatedAmount = amount,
            Currency = receipt.Currency.ToString(),
            Status = status,
            AllocatedAt = DateTime.Now,
            AllocatedBy = "tester",
            VoidedAt = status == AgencyServiceFeeCollectionAllocationRules.StatusVoided ? DateTime.Now : null,
            VoidReason = status == AgencyServiceFeeCollectionAllocationRules.StatusVoided ? "录错" : string.Empty
        };
        db.AgencyServiceFeeCollectionAllocations.Add(row);
        db.SaveChanges();
        return row;
    }

    private static SysMenu EnsureReceiptMenu(ErpDbContext db)
    {
        var menu = new SysMenu
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

    /// <summary>播种一个具备收款单菜单授权的特权用户（系统内置角色），返回用户 Id。</summary>
    private static long SeedPrivilegedUser(ErpDbContext db)
    {
        var role = new SysRole { RoleName = "收款特权角色", RoleCode = $"ReceiptPrivileged-{Guid.NewGuid():N}", IsSystem = true };
        db.SysRoles.Add(role);
        db.SaveChanges();

        var user = new SysUser
        {
            UserName = $"receipt-priv-{Guid.NewGuid():N}",
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = "收款特权用户",
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

    /// <summary>播种一个受限制业务员（非系统角色 + 收款单菜单 + 员工映射），返回 (用户 Id, 员工 Id)。</summary>
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
        var menu = EnsureReceiptMenu(db);
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

    // ==================== 1. 身份 / 菜单 / 客户数据范围 ====================

    [Fact]
    public async Task Create_无身份_未认证拒绝()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "客户A");
        var controller = BuildController(db);
        TestAuth.SetUser(controller, null);

        await AssertBusinessCodeAsync(ErrorCodes.Unauthorized,
            () => controller.Create(new FinanceReceipt { CustomerId = customer.Id, Amount = 100m, Currency = Currency.USD }));
    }

    [Fact]
    public async Task Create_无收款单菜单授权_权限不足()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "客户A");
        var userId = TestAuth.SeedPrivilegedUser(db);
        var controller = BuildController(db);
        TestAuth.SetUser(controller, userId);

        await AssertBusinessCodeAsync(ErrorCodes.Forbidden,
            () => controller.Create(new FinanceReceipt { CustomerId = customer.Id, Amount = 100m, Currency = Currency.USD }));
    }

    [Fact]
    public async Task Create_客户不存在_拒绝()
    {
        using var db = TestDbFactory.Create();
        var userId = SeedPrivilegedUser(db);
        var controller = BuildController(db);
        TestAuth.SetUser(controller, userId);

        await AssertBusinessCodeAsync(ErrorCodes.NotFound,
            () => controller.Create(new FinanceReceipt { CustomerId = 999999L, Amount = 100m, Currency = Currency.USD }));
    }

    [Fact]
    public async Task Create_客户已停用_拒绝()
    {
        using var db = TestDbFactory.Create();
        var userId = SeedPrivilegedUser(db);
        var customer = SeedCustomer(db, "C001", "客户A", status: 0);
        var controller = BuildController(db);
        TestAuth.SetUser(controller, userId);

        await AssertBusinessCodeAsync(ErrorCodes.RuleConflict,
            () => controller.Create(new FinanceReceipt { CustomerId = customer.Id, Amount = 100m, Currency = Currency.USD }));
    }

    [Fact]
    public async Task Create_金额非正_拒绝()
    {
        using var db = TestDbFactory.Create();
        var userId = SeedPrivilegedUser(db);
        var customer = SeedCustomer(db, "C001", "客户A");
        var controller = BuildController(db);
        TestAuth.SetUser(controller, userId);

        await AssertBusinessCodeAsync(ErrorCodes.InvalidParameter,
            () => controller.Create(new FinanceReceipt { CustomerId = customer.Id, Amount = 0m, Currency = Currency.USD }));
    }

    [Fact]
    public async Task Create_币种不受支持_拒绝()
    {
        using var db = TestDbFactory.Create();
        var userId = SeedPrivilegedUser(db);
        var customer = SeedCustomer(db, "C001", "客户A");
        var controller = BuildController(db);
        TestAuth.SetUser(controller, userId);

        await AssertBusinessCodeAsync(ErrorCodes.InvalidParameter,
            () => controller.Create(new FinanceReceipt { CustomerId = customer.Id, Amount = 100m, Currency = (Currency)999 }));
    }

    [Fact]
    public async Task Create_受限制业务员_越界客户_权限不足()
    {
        using var db = TestDbFactory.Create();
        var (userId, employeeId) = SeedRestrictedSalesman(db, "sales01");
        var inScope = SeedCustomer(db, "C-IN", "范围内客户", empId: employeeId);
        var outOfScope = SeedCustomer(db, "C-OUT", "范围外客户", empId: 9999L);
        Assert.NotEqual(inScope.Id, outOfScope.Id);

        var controller = BuildController(db);
        TestAuth.SetUser(controller, userId);

        await AssertBusinessCodeAsync(ErrorCodes.Forbidden,
            () => controller.Create(new FinanceReceipt { CustomerId = outOfScope.Id, Amount = 100m, Currency = Currency.USD }));
    }

    [Fact]
    public async Task Create_成功_金额按币种精度取整()
    {
        using var db = TestDbFactory.Create();
        var userId = SeedPrivilegedUser(db);
        var customer = SeedCustomer(db, "C001", "客户A");
        var controller = BuildController(db);
        TestAuth.SetUser(controller, userId);

        await controller.Create(new FinanceReceipt
        {
            CustomerId = customer.Id,
            Amount = 100.005m,
            Currency = Currency.USD
        });

        var saved = db.FinanceReceipts.AsNoTracking().Single();
        Assert.Equal(100.01m, saved.Amount);
        Assert.Equal(Currency.USD, saved.Currency);
        Assert.Equal(DocumentStatus.Pending, saved.Status);
    }

    [Fact]
    public async Task GetPaged_受限制业务员_只返回范围内客户()
    {
        using var db = TestDbFactory.Create();
        var (userId, employeeId) = SeedRestrictedSalesman(db, "sales01");
        var inScope = SeedCustomer(db, "C-IN", "范围内客户", empId: employeeId);
        var outOfScope = SeedCustomer(db, "C-OUT", "范围外客户", empId: 9999L);
        SeedReceipt(db, "RC-IN", inScope.Id);
        SeedReceipt(db, "RC-OUT", outOfScope.Id);

        var controller = BuildController(db);
        TestAuth.SetUser(controller, userId);

        var result = await controller.GetPaged(new PageQuery(), null);

        var paged = AssertOk<PagedResult<FinanceReceipt>>(result);
        Assert.Single(paged.Items);
        Assert.Equal("RC-IN", paged.Items[0].ReceiptNo);
    }

    [Fact]
    public async Task GetById_越界客户_权限不足()
    {
        using var db = TestDbFactory.Create();
        var (userId, employeeId) = SeedRestrictedSalesman(db, "sales01");
        var inScope = SeedCustomer(db, "C-IN", "范围内客户", empId: employeeId);
        var outOfScope = SeedCustomer(db, "C-OUT", "范围外客户", empId: 9999L);
        var receipt = SeedReceipt(db, "RC-OUT", outOfScope.Id);

        var controller = BuildController(db);
        TestAuth.SetUser(controller, userId);

        await AssertBusinessCodeAsync(ErrorCodes.Forbidden, () => controller.GetById(receipt.Id));
        Assert.NotNull(inScope);
    }

    // ==================== 2. 有效收款分摊证据保护（修改 / 取消 / 删除） ====================

    [Fact]
    public async Task Update_非待提交状态_冻结拒绝()
    {
        using var db = TestDbFactory.Create();
        var userId = SeedPrivilegedUser(db);
        var customer = SeedCustomer(db, "C001", "客户A");
        var receipt = SeedReceipt(db, "RC-SUBMITTED", customer.Id, status: DocumentStatus.Submitted);
        var controller = BuildController(db);
        TestAuth.SetUser(controller, userId);

        await AssertBusinessCodeAsync(ErrorCodes.RuleConflict,
            () => controller.Update(receipt.Id, new FinanceReceipt
            {
                CustomerId = customer.Id,
                Amount = 500m,
                Currency = Currency.USD,
                Remark = "改备注"
            }));
    }

    [Fact]
    public async Task Update_有效收款引用证据_改金额拒绝()
    {
        using var db = TestDbFactory.Create();
        var userId = SeedPrivilegedUser(db);
        var customer = SeedCustomer(db, "C001", "客户A");
        var receipt = SeedReceipt(db, "RC-1", customer.Id, amount: 1000m);
        var order = SeedOrder(db, "SO-1", customer.Id);
        SeedCustomerOrderAllocation(db, receipt, order, amount: 300m);

        var controller = BuildController(db);
        TestAuth.SetUser(controller, userId);

        await AssertBusinessCodeAsync(ErrorCodes.RuleConflict,
            () => controller.Update(receipt.Id, new FinanceReceipt
            {
                CustomerId = customer.Id,
                Amount = 500m,
                Currency = Currency.USD
            }));
    }

    [Fact]
    public async Task Update_有效收款引用证据_改客户拒绝()
    {
        using var db = TestDbFactory.Create();
        var userId = SeedPrivilegedUser(db);
        var customerA = SeedCustomer(db, "C-A", "客户A");
        var customerB = SeedCustomer(db, "C-B", "客户B");
        var receipt = SeedReceipt(db, "RC-1", customerA.Id, amount: 1000m);
        var order = SeedOrder(db, "SO-1", customerA.Id);
        SeedCustomerOrderAllocation(db, receipt, order, amount: 300m);

        var controller = BuildController(db);
        TestAuth.SetUser(controller, userId);

        await AssertBusinessCodeAsync(ErrorCodes.RuleConflict,
            () => controller.Update(receipt.Id, new FinanceReceipt
            {
                CustomerId = customerB.Id,
                Amount = 1000m,
                Currency = Currency.USD
            }));
    }

    [Fact]
    public async Task Update_有效收款引用证据_改币种拒绝()
    {
        using var db = TestDbFactory.Create();
        var userId = SeedPrivilegedUser(db);
        var customer = SeedCustomer(db, "C001", "客户A");
        var receipt = SeedReceipt(db, "RC-1", customer.Id, amount: 1000m, currency: Currency.USD);
        var order = SeedOrder(db, "SO-1", customer.Id, currency: Currency.USD);
        SeedCustomerOrderAllocation(db, receipt, order, amount: 300m);

        var controller = BuildController(db);
        TestAuth.SetUser(controller, userId);

        await AssertBusinessCodeAsync(ErrorCodes.RuleConflict,
            () => controller.Update(receipt.Id, new FinanceReceipt
            {
                CustomerId = customer.Id,
                Amount = 1000m,
                Currency = Currency.CNY
            }));
    }

    [Fact]
    public async Task Update_有效代理服务费分摊证据_改金额拒绝()
    {
        using var db = TestDbFactory.Create();
        var userId = SeedPrivilegedUser(db);
        var customer = SeedCustomer(db, "C001", "客户A");
        var receipt = SeedReceipt(db, "RC-1", customer.Id, amount: 1000m);
        SeedAgencyFeeAllocation(db, receipt, amount: 300m);

        var controller = BuildController(db);
        TestAuth.SetUser(controller, userId);

        await AssertBusinessCodeAsync(ErrorCodes.RuleConflict,
            () => controller.Update(receipt.Id, new FinanceReceipt
            {
                CustomerId = customer.Id,
                Amount = 500m,
                Currency = Currency.USD
            }));
    }

    [Fact]
    public async Task Update_有效证据_作废后_改金额放行()
    {
        using var db = TestDbFactory.Create();
        var userId = SeedPrivilegedUser(db);
        var customer = SeedCustomer(db, "C001", "客户A");
        var receipt = SeedReceipt(db, "RC-1", customer.Id, amount: 1000m);
        var order = SeedOrder(db, "SO-1", customer.Id);
        SeedCustomerOrderAllocation(db, receipt, order, amount: 300m, status: CustomerReceiptAllocationRules.StatusVoided);

        var controller = BuildController(db);
        TestAuth.SetUser(controller, userId);

        await controller.Update(receipt.Id, new FinanceReceipt
        {
            CustomerId = customer.Id,
            Amount = 500m,
            Currency = Currency.USD
        });

        var saved = db.FinanceReceipts.AsNoTracking().Single(r => r.Id == receipt.Id);
        Assert.Equal(500m, saved.Amount);
    }

    [Fact]
    public async Task Update_有效证据_允许无害元数据修改()
    {
        using var db = TestDbFactory.Create();
        var userId = SeedPrivilegedUser(db);
        var customer = SeedCustomer(db, "C001", "客户A");
        var receipt = SeedReceipt(db, "RC-1", customer.Id, amount: 1000m);
        var order = SeedOrder(db, "SO-1", customer.Id);
        SeedCustomerOrderAllocation(db, receipt, order, amount: 300m);

        var controller = BuildController(db);
        TestAuth.SetUser(controller, userId);

        await controller.Update(receipt.Id, new FinanceReceipt
        {
            CustomerId = customer.Id,
            Amount = 1000m,
            Currency = Currency.USD,
            PaymentMethod = PaymentMethod.TelegraphicTransfer,
            BankAccount = "BANK-01",
            Remark = "仅改付款方式 / 账户 / 备注"
        });

        var saved = db.FinanceReceipts.AsNoTracking().Single(r => r.Id == receipt.Id);
        Assert.Equal(PaymentMethod.TelegraphicTransfer, saved.PaymentMethod);
        Assert.Equal("BANK-01", saved.BankAccount);
        Assert.Equal("仅改付款方式 / 账户 / 备注", saved.Remark);
        Assert.Equal(1000m, saved.Amount);
        Assert.Equal(Currency.USD, saved.Currency);
        Assert.Equal(customer.Id, saved.CustomerId);
    }

    // ==================== 3. 取消 / 删除生命周期保护 ====================

    [Fact]
    public async Task Cancel_有效收款引用证据_拒绝()
    {
        using var db = TestDbFactory.Create();
        var userId = SeedPrivilegedUser(db);
        var customer = SeedCustomer(db, "C001", "客户A");
        var receipt = SeedReceipt(db, "RC-1", customer.Id, amount: 1000m);
        var order = SeedOrder(db, "SO-1", customer.Id);
        SeedCustomerOrderAllocation(db, receipt, order, amount: 300m);

        var controller = BuildController(db);
        TestAuth.SetUser(controller, userId);

        await AssertBusinessCodeAsync(ErrorCodes.RuleConflict, () => controller.Cancel(receipt.Id));
        Assert.Equal(DocumentStatus.Pending, db.FinanceReceipts.AsNoTracking().Single(r => r.Id == receipt.Id).Status);
    }

    [Fact]
    public async Task Cancel_有效代理服务费分摊证据_拒绝()
    {
        using var db = TestDbFactory.Create();
        var userId = SeedPrivilegedUser(db);
        var customer = SeedCustomer(db, "C001", "客户A");
        var receipt = SeedReceipt(db, "RC-1", customer.Id, amount: 1000m);
        SeedAgencyFeeAllocation(db, receipt, amount: 300m);

        var controller = BuildController(db);
        TestAuth.SetUser(controller, userId);

        await AssertBusinessCodeAsync(ErrorCodes.RuleConflict, () => controller.Cancel(receipt.Id));
    }

    [Fact]
    public async Task Cancel_作废证据_放行()
    {
        using var db = TestDbFactory.Create();
        var userId = SeedPrivilegedUser(db);
        var customer = SeedCustomer(db, "C001", "客户A");
        var receipt = SeedReceipt(db, "RC-1", customer.Id, amount: 1000m);
        var order = SeedOrder(db, "SO-1", customer.Id);
        SeedCustomerOrderAllocation(db, receipt, order, amount: 300m, status: CustomerReceiptAllocationRules.StatusVoided);

        var controller = BuildController(db);
        TestAuth.SetUser(controller, userId);

        await controller.Cancel(receipt.Id);
        Assert.Equal(DocumentStatus.Cancelled, db.FinanceReceipts.AsNoTracking().Single(r => r.Id == receipt.Id).Status);
    }

    [Fact]
    public async Task Cancel_重复取消_拒绝()
    {
        using var db = TestDbFactory.Create();
        var userId = SeedPrivilegedUser(db);
        var customer = SeedCustomer(db, "C001", "客户A");
        var receipt = SeedReceipt(db, "RC-1", customer.Id, amount: 1000m);

        var controller = BuildController(db);
        TestAuth.SetUser(controller, userId);

        await controller.Cancel(receipt.Id);
        await AssertBusinessCodeAsync(ErrorCodes.RuleConflict, () => controller.Cancel(receipt.Id));
    }

    [Fact]
    public async Task Delete_有效证据_拒绝()
    {
        using var db = TestDbFactory.Create();
        var userId = SeedPrivilegedUser(db);
        var customer = SeedCustomer(db, "C001", "客户A");
        var receipt = SeedReceipt(db, "RC-1", customer.Id, amount: 1000m);
        var order = SeedOrder(db, "SO-1", customer.Id);
        SeedCustomerOrderAllocation(db, receipt, order, amount: 300m);

        var controller = BuildController(db);
        TestAuth.SetUser(controller, userId);

        await AssertBusinessCodeAsync(ErrorCodes.RuleConflict, () => controller.Delete(receipt.Id));
        Assert.False(db.FinanceReceipts.AsNoTracking().Single(r => r.Id == receipt.Id).IsDeleted);
    }

    [Fact]
    public async Task Delete_无证据待提交_放行()
    {
        using var db = TestDbFactory.Create();
        var userId = SeedPrivilegedUser(db);
        var customer = SeedCustomer(db, "C001", "客户A");
        var receipt = SeedReceipt(db, "RC-1", customer.Id, amount: 1000m);

        var controller = BuildController(db);
        TestAuth.SetUser(controller, userId);

        await controller.Delete(receipt.Id);
        Assert.True(db.FinanceReceipts.AsNoTracking().Single(r => r.Id == receipt.Id).IsDeleted);
    }

    // ==================== 4. 提交 / 审核范围复核 ====================

    [Fact]
    public async Task Submit_越界客户_权限不足()
    {
        using var db = TestDbFactory.Create();
        var (userId, employeeId) = SeedRestrictedSalesman(db, "sales01");
        var inScope = SeedCustomer(db, "C-IN", "范围内客户", empId: employeeId);
        var outOfScope = SeedCustomer(db, "C-OUT", "范围外客户", empId: 9999L);
        var receipt = SeedReceipt(db, "RC-OUT", outOfScope.Id);

        var controller = BuildController(db);
        TestAuth.SetUser(controller, userId);

        await AssertBusinessCodeAsync(ErrorCodes.Forbidden, () => controller.Submit(receipt.Id));
        Assert.NotNull(inScope);
    }

    [Fact]
    public async Task Approve_无收款单菜单授权_权限不足()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "客户A");
        var receipt = SeedReceipt(db, "RC-1", customer.Id, amount: 1000m, status: DocumentStatus.Submitted);
        var userId = TestAuth.SeedPrivilegedUser(db);

        var controller = BuildController(db);
        TestAuth.SetUser(controller, userId);

        await AssertBusinessCodeAsync(ErrorCodes.Forbidden, () => controller.Approve(receipt.Id));
    }
}
