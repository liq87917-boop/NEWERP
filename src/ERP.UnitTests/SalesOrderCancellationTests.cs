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
/// ERP-347 销售订单取消护栏单元测试。
/// <para>覆盖：无履约依赖取消成功且保留原明细 / 金额、已审核未冲销出库拒绝、已取消出库释放、
/// 未删除未取消采购履约拒绝、已取消 / 已删除采购释放、有效收款引用证据拒绝、作废收款引用释放、
/// 无身份 / 无菜单 / 已回收菜单 / 越客户范围拒绝、重复取消、终止态取消拒绝，以及控制器授权仅继承基类。</para>
/// <para>全部使用内存数据库，不连接 SQL Server、不启动 API、不运行浏览器验收。</para>
/// </summary>
public class SalesOrderCancellationTests
{
    private const long CustomerA = 943301L;
    private const long SupplierA = 943001L;
    private const long WarehouseA = 943101L;
    private const long ProductA = 943201L;

    // ==================== 无履约依赖取消 ====================

    [Fact]
    public async Task Cancel_NoDependent_SucceedsAndPreservesDetailsAndAmount()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, isSystem: true, withMenu: true);
        var order = SeedOrder(db, "SO-CANCEL-1", CustomerA, DocumentStatus.Approved, totalAmount: 1500m, lineAmount: 1500m);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var result = await ctl.Cancel(order.Id);

        Assert.IsType<OkObjectResult>(result);
        var saved = db.SalesOrders.Single(o => o.Id == order.Id);
        Assert.Equal(DocumentStatus.Cancelled, saved.Status);
        Assert.Equal(1500m, saved.TotalAmount);
        Assert.Single(db.SalesOrderDetails.Where(d => d.SalesOrderId == order.Id && !d.IsDeleted));
    }

    // ==================== 销售出库履约证据 ====================

    [Fact]
    public async Task Cancel_ActiveApprovedStockOut_Refused()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, isSystem: true, withMenu: true);
        var order = SeedOrder(db, "SO-CANCEL-2", CustomerA, DocumentStatus.Approved);
        SeedStockOut(db, "SO-OUT-ACTIVE", order.Id, CustomerA, DocumentStatus.Approved);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            SalesOrderCancellationRules.ValidateCancellationAsync(db, ReloadOrder(db, order.Id), user.Id));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("已审核且未冲销", ex.Message);
        Assert.Equal(DocumentStatus.Approved, db.SalesOrders.Single(o => o.Id == order.Id).Status);
    }

    [Fact]
    public async Task Cancel_ReversedStockOut_Released()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, isSystem: true, withMenu: true);
        var order = SeedOrder(db, "SO-CANCEL-3", CustomerA, DocumentStatus.Approved);
        SeedStockOut(db, "SO-OUT-CANCELLED", order.Id, CustomerA, DocumentStatus.Cancelled);

        await SalesOrderCancellationRules.ValidateCancellationAsync(db, ReloadOrder(db, order.Id), user.Id);
    }

    // ==================== 采购履约证据 ====================

    [Fact]
    public async Task Cancel_ActiveProcurement_Refused()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, isSystem: true, withMenu: true);
        var order = SeedOrder(db, "SO-CANCEL-4", CustomerA, DocumentStatus.Approved);
        SeedPurchaseOrder(db, "PO-LINKED-ACTIVE", order.Id, DocumentStatus.Approved);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            SalesOrderCancellationRules.ValidateCancellationAsync(db, ReloadOrder(db, order.Id), user.Id));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("采购订单", ex.Message);
        Assert.Equal(DocumentStatus.Approved, db.SalesOrders.Single(o => o.Id == order.Id).Status);
    }

    [Fact]
    public async Task Cancel_CancelledProcurement_Released()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, isSystem: true, withMenu: true);
        var order = SeedOrder(db, "SO-CANCEL-5", CustomerA, DocumentStatus.Approved);
        SeedPurchaseOrder(db, "PO-LINKED-CANCELLED", order.Id, DocumentStatus.Cancelled);

        await SalesOrderCancellationRules.ValidateCancellationAsync(db, ReloadOrder(db, order.Id), user.Id);
    }

    [Fact]
    public async Task Cancel_DeletedProcurement_Released()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, isSystem: true, withMenu: true);
        var order = SeedOrder(db, "SO-CANCEL-6", CustomerA, DocumentStatus.Approved);
        SeedPurchaseOrder(db, "PO-LINKED-DELETED", order.Id, DocumentStatus.Approved, deleted: true);

        await SalesOrderCancellationRules.ValidateCancellationAsync(db, ReloadOrder(db, order.Id), user.Id);
    }

    // ==================== 客户收款引用证据 ====================

    [Fact]
    public async Task Cancel_ActiveReceiptAllocation_Refused()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, isSystem: true, withMenu: true);
        var order = SeedOrder(db, "SO-CANCEL-7", CustomerA, DocumentStatus.Approved);
        SeedReceiptAllocation(db, order, CustomerReceiptAllocationRules.StatusActive);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            SalesOrderCancellationRules.ValidateCancellationAsync(db, ReloadOrder(db, order.Id), user.Id));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("收款引用", ex.Message);
        Assert.Equal(DocumentStatus.Approved, db.SalesOrders.Single(o => o.Id == order.Id).Status);
    }

    [Fact]
    public async Task Cancel_VoidedReceiptAllocation_Released()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, isSystem: true, withMenu: true);
        var order = SeedOrder(db, "SO-CANCEL-8", CustomerA, DocumentStatus.Approved);
        SeedReceiptAllocation(db, order, CustomerReceiptAllocationRules.StatusVoided);

        await SalesOrderCancellationRules.ValidateCancellationAsync(db, ReloadOrder(db, order.Id), user.Id);
    }

    // ==================== 控制器路径的依赖拒绝 ====================

    [Fact]
    public async Task Cancel_ActiveApprovedStockOut_Refused_ThroughController()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, isSystem: true, withMenu: true);
        var order = SeedOrder(db, "SO-CANCEL-9", CustomerA, DocumentStatus.Approved);
        SeedStockOut(db, "SO-OUT-CTL", order.Id, CustomerA, DocumentStatus.Approved);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Cancel(order.Id));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Equal(DocumentStatus.Approved, db.SalesOrders.Single(o => o.Id == order.Id).Status);
    }

    [Fact]
    public async Task Cancel_ActiveProcurement_Refused_ThroughController()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, isSystem: true, withMenu: true);
        var order = SeedOrder(db, "SO-CANCEL-10", CustomerA, DocumentStatus.Approved);
        SeedPurchaseOrder(db, "PO-LINKED-CTL", order.Id, DocumentStatus.Approved);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Cancel(order.Id));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Equal(DocumentStatus.Approved, db.SalesOrders.Single(o => o.Id == order.Id).Status);
    }

    [Fact]
    public async Task Cancel_ActiveReceiptAllocation_Refused_ThroughController()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, isSystem: true, withMenu: true);
        var order = SeedOrder(db, "SO-CANCEL-11", CustomerA, DocumentStatus.Approved);
        SeedReceiptAllocation(db, order, CustomerReceiptAllocationRules.StatusActive);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Cancel(order.Id));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Equal(DocumentStatus.Approved, db.SalesOrders.Single(o => o.Id == order.Id).Status);
    }

    // ==================== 授权与状态 ====================

    [Fact]
    public async Task Cancel_NoIdentity_Unauthorized()
    {
        using var db = TestDbFactory.Create();
        var order = SeedOrder(db, "SO-CANCEL-12", CustomerA, DocumentStatus.Approved);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            SalesOrderCancellationRules.ValidateCancellationAsync(db, ReloadOrder(db, order.Id), null));

        Assert.Equal(ErrorCodes.Unauthorized, ex.Code);
    }

    [Fact]
    public async Task Cancel_NoMenu_Forbidden()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, isSystem: false, withMenu: false);
        var order = SeedOrder(db, "SO-CANCEL-13", CustomerA, DocumentStatus.Approved);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            SalesOrderCancellationRules.ValidateCancellationAsync(db, ReloadOrder(db, order.Id), user.Id));

        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    [Fact]
    public async Task Cancel_RevokedMenu_Forbidden()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, isSystem: false, withMenu: true);
        var order = SeedOrder(db, "SO-CANCEL-14", CustomerA, DocumentStatus.Approved);

        // 授权被回收后必须立即 fail closed（每次请求都重新查询授权，不依赖缓存）
        foreach (var rm in db.SysRoleMenus.Where(rm => !rm.IsDeleted)) rm.IsDeleted = true;
        await db.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            SalesOrderCancellationRules.ValidateCancellationAsync(db, ReloadOrder(db, order.Id), user.Id));

        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    [Fact]
    public async Task Cancel_OutOfCustomerScope_Forbidden()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, isSystem: false, withMenu: true);
        var order = SeedOrder(db, "SO-CANCEL-15", CustomerA, DocumentStatus.Approved);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            SalesOrderCancellationRules.ValidateCancellationAsync(db, ReloadOrder(db, order.Id), user.Id));

        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    [Fact]
    public async Task Cancel_AlreadyCancelled_Conflict()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, isSystem: true, withMenu: true);
        var order = SeedOrder(db, "SO-CANCEL-16", CustomerA, DocumentStatus.Cancelled);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            SalesOrderCancellationRules.ValidateCancellationAsync(db, ReloadOrder(db, order.Id), user.Id));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("重复取消", ex.Message);
    }

    [Fact]
    public async Task Cancel_TerminalState_Conflict()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, isSystem: true, withMenu: true);
        var order = SeedOrder(db, "SO-CANCEL-17", CustomerA, DocumentStatus.Rejected);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            SalesOrderCancellationRules.ValidateCancellationAsync(db, ReloadOrder(db, order.Id), user.Id));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("当前状态不允许取消", ex.Message);
    }

    // ==================== 控制器授权继承（无权限扩展） ====================

    [Fact]
    public void Controller_Authorization_OnlyInheritedFromBase()
    {
        Assert.Empty(typeof(SalesOrderController).GetCustomAttributes(typeof(AuthorizeAttribute), false));
        Assert.NotEmpty(typeof(DocumentControllerBase<SalesOrder>).GetCustomAttributes(typeof(AuthorizeAttribute), false));
    }

    // ==================== 测试脚手架 ====================

    private static SalesOrderController NewController(ErpDbContext db)
        => new SalesOrderController(db, new DocumentNumberService(db));

    private static SalesOrder ReloadOrder(ErpDbContext db, long id)
        => db.SalesOrders.Single(o => o.Id == id);

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
                MenuCode = SalesOrderCancellationRules.RequiredMenuCode,
                MenuName = SalesOrderCancellationRules.RequiredMenuText,
                MenuType = MenuType.Menu
            };
            db.SysMenus.Add(menu);
            db.SaveChanges();
            db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
            db.SaveChanges();
        }

        return user;
    }

    private static SalesOrder SeedOrder(ErpDbContext db, string no, long customerId, DocumentStatus status,
        decimal totalAmount = 1000m, decimal lineAmount = 1000m)
    {
        var order = new SalesOrder
        {
            OrderNo = no,
            OrderDate = DateTime.Today.AddDays(-5),
            CustomerId = customerId,
            Currency = Currency.USD,
            ExchangeRate = 1m,
            TotalAmount = totalAmount,
            Status = status
        };
        db.SalesOrders.Add(order);
        db.SaveChanges();

        db.SalesOrderDetails.Add(new SalesOrderDetail
        {
            SalesOrderId = order.Id,
            ProductId = ProductA,
            ProductName = "商品A",
            Quantity = 1m,
            Unit = "PCS",
            UnitPrice = lineAmount,
            Amount = lineAmount
        });
        db.SaveChanges();
        return order;
    }

    private static void SeedStockOut(ErpDbContext db, string no, long orderId, long customerId, DocumentStatus status)
    {
        db.StockOuts.Add(new StockOut
        {
            StockOutNo = no,
            StockOutDate = DateTime.Today,
            SalesOrderId = orderId,
            CustomerId = customerId,
            WarehouseId = WarehouseA,
            Status = status
        });
        db.SaveChanges();
    }

    private static void SeedPurchaseOrder(ErpDbContext db, string no, long owningSalesOrderId,
        DocumentStatus status, bool deleted = false)
    {
        db.PurchaseOrders.Add(new PurchaseOrder
        {
            OrderNo = no,
            OrderDate = DateTime.Today,
            SupplierId = SupplierA,
            Currency = Currency.CNY,
            OwningSalesOrderId = owningSalesOrderId,
            OwningSalesOrderNo = no,
            Status = status,
            IsDeleted = deleted
        });
        db.SaveChanges();
    }

    private static void SeedReceiptAllocation(ErpDbContext db, SalesOrder order, int status)
    {
        db.CustomerReceiptAllocations.Add(new CustomerReceiptAllocation
        {
            ReceiptId = 1,
            ReceiptNo = "REC-1",
            ReceiptDate = DateTime.Today,
            ReceiptStatus = (int)DocumentStatus.Approved,
            ReceiptStatusText = "已审核",
            ReceiptAmount = 1000m,
            SalesOrderId = order.Id,
            OrderNo = order.OrderNo,
            OrderDate = order.OrderDate,
            OrderStatus = (int)order.Status,
            OrderCurrency = order.Currency.ToString(),
            CustomerId = order.CustomerId,
            CustomerCode = "CUST",
            CustomerName = "客户",
            AllocatedAmount = 100m,
            Currency = order.Currency.ToString(),
            Status = status,
            AllocatedAt = DateTime.Now,
            VoidedAt = status == CustomerReceiptAllocationRules.StatusVoided ? DateTime.Now : null,
            VoidReason = status == CustomerReceiptAllocationRules.StatusVoided ? "录错" : string.Empty
        });
        db.SaveChanges();
    }
}
