using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-369 销售订单取消护栏的「已审核预装柜需求计划证据」单元测试。
/// <para>覆盖：未删除、已审核且明细显式链接本单明细的预装柜单拒绝取消（可执行解除要求 + 有界文案）；
/// 未链接（null 历史遗留）/ 待提交 / 已提交 / 已驳回 / 已删除 / 已取消 / 无关订单链接都不阻断；
/// 取消预装柜单即释放护栏且历史（链接与数量）原样保留；失败拒绝不改订单 / 明细 / 下游库存 / 采购 / 财务；
/// 既有 ERP-347 采购 / 出库护栏与重复取消行为不变；身份 / 菜单 / 客户范围 fail closed（真实授权，
/// 无匿名 / 管理员兜底）。</para>
/// <para>全部使用内存数据库（<see cref="TestDbFactory"/>），不连接 SQL Server、不启动 API、不运行浏览器验收。</para>
/// </summary>
public class SalesOrderPreLoadingCancellationTests
{
    private const long CustomerA = 969301L;
    private const long SupplierA = 969001L;
    private const long WarehouseA = 969101L;
    private const long ProductA = 969201L;

    // ==================== 已审核链接：拒绝取消 ====================

    [Fact]
    public async Task Cancel_ApprovedLinkedPreLoading_RefusedWithActionableRequirement()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, isSystem: true, withMenu: true);
        var (order, detail) = SeedOrderWithDetail(db, "SO-PL-1", CustomerA, DocumentStatus.Approved);
        var pre = SeedPreLoading(db, "YZ-PL-1", DocumentStatus.Approved, (ProductA, 6m, detail.Id));

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            SalesOrderCancellationRules.ValidateCancellationAsync(db, ReloadOrder(db, order.Id), user.Id));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("预装柜", ex.Message);
        Assert.Contains("YZ-PL-1", ex.Message);
        Assert.Contains(SalesOrderCancellationRules.PreLoadingReversalRequirementText, ex.Message);
        Assert.Contains("商品A", ex.Message);
        Assert.Equal(DocumentStatus.Approved, db.SalesOrders.Single(o => o.Id == order.Id).Status);
        Assert.Equal(DocumentStatus.Approved, db.ContainerPreLoadings.Single(p => p.Id == pre.Id).Status);
    }

    [Fact]
    public async Task Cancel_RefusalMessage_IsBoundedAndListsAtMostFivePreLoadings()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, isSystem: true, withMenu: true);
        var (order, detail) = SeedOrderWithDetail(db, "SO-PL-2", CustomerA, DocumentStatus.Approved);
        for (var i = 1; i <= 7; i++)
            SeedPreLoading(db, $"YZ-PL-2-{i}", DocumentStatus.Approved, (ProductA, 1m, detail.Id));

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            SalesOrderCancellationRules.ValidateCancellationAsync(db, ReloadOrder(db, order.Id), user.Id));

        Assert.Contains("YZ-PL-2-1", ex.Message);
        Assert.Contains("YZ-PL-2-5", ex.Message);
        Assert.DoesNotContain("YZ-PL-2-6", ex.Message);
        Assert.Contains("等共 7 张", ex.Message);
    }

    [Fact]
    public async Task Cancel_ThroughController_RefusedAndPreservesEverything()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, isSystem: true, withMenu: true);
        var (order, detail) = SeedOrderWithDetail(db, "SO-PL-3", CustomerA, DocumentStatus.Approved,
            quantity: 10m, unitPrice: 7.5m);
        var pre = SeedPreLoading(db, "YZ-PL-3", DocumentStatus.Approved, (ProductA, 4m, detail.Id));
        SeedPurchaseOrder(db, "PO-PL-3", order.Id, DocumentStatus.Cancelled);
        var ctl = NewSalesOrderController(db);
        TestAuth.SetUser(ctl, user.Id);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Cancel(order.Id));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);

        var stored = db.SalesOrders.AsNoTracking().Single(o => o.Id == order.Id);
        Assert.Equal(DocumentStatus.Approved, stored.Status);
        Assert.Equal(75m, stored.TotalAmount);
        Assert.Equal(1, await db.SalesOrderDetails.CountAsync(d => d.SalesOrderId == order.Id && !d.IsDeleted));

        // 失败拒绝绝不改动下游与历史证据：预装柜状态 / 数量 / 显式链接原样，且无任何库存流水。
        var history = db.ContainerPreLoadingDetails.AsNoTracking().Single(d => d.PreLoadingId == pre.Id);
        Assert.Equal(DocumentStatus.Approved, db.ContainerPreLoadings.AsNoTracking().Single(p => p.Id == pre.Id).Status);
        Assert.Equal(4m, history.Quantity);
        Assert.Equal(detail.Id, history.SourceSalesOrderDetailId);
        Assert.Equal(0, await db.StockMovements.CountAsync(m => m.SourceDocId == order.Id));
    }

    // ==================== 释放口径：不阻断 ====================

    [Fact]
    public async Task Cancel_NullLegacyLink_DoesNotBlock()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, isSystem: true, withMenu: true);
        var (order, _) = SeedOrderWithDetail(db, "SO-PL-4", CustomerA, DocumentStatus.Approved);
        SeedPreLoading(db, "YZ-PL-4", DocumentStatus.Approved, (ProductA, 6m, null));

        var ctl = NewSalesOrderController(db);
        TestAuth.SetUser(ctl, user.Id);
        Assert.IsType<OkObjectResult>(await ctl.Cancel(order.Id));
        Assert.Equal(DocumentStatus.Cancelled, db.SalesOrders.Single(o => o.Id == order.Id).Status);
    }

    [Fact]
    public async Task Cancel_PendingSubmittedAndRejectedLinks_DoNotBlock()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, isSystem: true, withMenu: true);
        var (order, detail) = SeedOrderWithDetail(db, "SO-PL-5", CustomerA, DocumentStatus.Approved);
        SeedPreLoading(db, "YZ-PL-5-P", DocumentStatus.Pending, (ProductA, 3m, detail.Id));
        SeedPreLoading(db, "YZ-PL-5-S", DocumentStatus.Submitted, (ProductA, 3m, detail.Id));
        SeedPreLoading(db, "YZ-PL-5-R", DocumentStatus.Rejected, (ProductA, 3m, detail.Id));

        await SalesOrderCancellationRules.ValidateCancellationAsync(db, ReloadOrder(db, order.Id), user.Id);
    }

    [Fact]
    public async Task Cancel_DeletedApprovedLink_DoesNotBlock()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, isSystem: true, withMenu: true);
        var (order, detail) = SeedOrderWithDetail(db, "SO-PL-6", CustomerA, DocumentStatus.Approved);
        SeedDeletedPreLoading(db, "YZ-PL-6", DocumentStatus.Approved, (ProductA, 3m, detail.Id));

        await SalesOrderCancellationRules.ValidateCancellationAsync(db, ReloadOrder(db, order.Id), user.Id);
    }

    [Fact]
    public async Task Cancel_CancelledLink_ReleasesGuardAndKeepsHistory()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, isSystem: true, withMenu: true);
        var (order, detail) = SeedOrderWithDetail(db, "SO-PL-7", CustomerA, DocumentStatus.Approved);
        var pre = SeedPreLoading(db, "YZ-PL-7", DocumentStatus.Cancelled, (ProductA, 8m, detail.Id));

        var ctl = NewSalesOrderController(db);
        TestAuth.SetUser(ctl, user.Id);
        Assert.IsType<OkObjectResult>(await ctl.Cancel(order.Id));

        // 取消预装柜单只改状态：显式来源链接与数量作为历史证据原样保留。
        Assert.Equal(DocumentStatus.Cancelled, db.SalesOrders.Single(o => o.Id == order.Id).Status);
        var history = db.ContainerPreLoadingDetails.AsNoTracking().Single(d => d.PreLoadingId == pre.Id);
        Assert.Equal(8m, history.Quantity);
        Assert.Equal(detail.Id, history.SourceSalesOrderDetailId);
        Assert.Equal(DocumentStatus.Cancelled, db.ContainerPreLoadings.AsNoTracking().Single(p => p.Id == pre.Id).Status);
    }

    [Fact]
    public async Task Cancel_UnrelatedOrderLink_DoesNotBlock()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, isSystem: true, withMenu: true);
        var (order, _) = SeedOrderWithDetail(db, "SO-PL-8", CustomerA, DocumentStatus.Approved);
        var (other, otherDetail) = SeedOrderWithDetail(db, "SO-PL-8-OTHER", CustomerA, DocumentStatus.Approved);
        SeedPreLoading(db, "YZ-PL-8", DocumentStatus.Approved, (ProductA, 5m, otherDetail.Id));

        var ctl = NewSalesOrderController(db);
        TestAuth.SetUser(ctl, user.Id);
        Assert.IsType<OkObjectResult>(await ctl.Cancel(order.Id));

        // 无关订单的链接与订单状态不受影响。
        Assert.Equal(DocumentStatus.Approved, db.SalesOrders.Single(o => o.Id == other.Id).Status);
        Assert.Equal(DocumentStatus.Approved,
            db.ContainerPreLoadings.Single(p => p.PreLoadingNo == "YZ-PL-8").Status);
    }

    [Fact]
    public async Task Cancel_ApprovedLinkToDeletedOrderDetail_DoesNotBlock()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, isSystem: true, withMenu: true);
        var (order, detail) = SeedOrderWithDetail(db, "SO-PL-9", CustomerA, DocumentStatus.Approved);
        detail.IsDeleted = true;
        await db.SaveChangesAsync();
        SeedPreLoading(db, "YZ-PL-9", DocumentStatus.Approved, (ProductA, 5m, detail.Id));

        await SalesOrderCancellationRules.ValidateCancellationAsync(db, ReloadOrder(db, order.Id), user.Id);
    }

    // ==================== 既有护栏与既有行为不变 ====================

    [Fact]
    public async Task Cancel_ExistingProcurementGuard_StillWinsAndIsUnchanged()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, isSystem: true, withMenu: true);
        var (order, detail) = SeedOrderWithDetail(db, "SO-PL-10", CustomerA, DocumentStatus.Approved);
        SeedPurchaseOrder(db, "PO-PL-10", order.Id, DocumentStatus.Approved);
        SeedPreLoading(db, "YZ-PL-10", DocumentStatus.Approved, (ProductA, 2m, detail.Id));

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            SalesOrderCancellationRules.ValidateCancellationAsync(db, ReloadOrder(db, order.Id), user.Id));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("采购订单", ex.Message);
        Assert.Equal(DocumentStatus.Approved, db.SalesOrders.Single(o => o.Id == order.Id).Status);
    }

    [Fact]
    public async Task Cancel_ActiveStockOut_StillRefused()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, isSystem: true, withMenu: true);
        var (order, detail) = SeedOrderWithDetail(db, "SO-PL-11", CustomerA, DocumentStatus.Approved);
        SeedStockOut(db, "SO-OUT-PL-11", order.Id, CustomerA, DocumentStatus.Approved);
        SeedPreLoading(db, "YZ-PL-11", DocumentStatus.Approved, (ProductA, 2m, detail.Id));

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            SalesOrderCancellationRules.ValidateCancellationAsync(db, ReloadOrder(db, order.Id), user.Id));

        Assert.Contains("已审核且未冲销", ex.Message);
    }

    [Fact]
    public async Task Cancel_ActiveReceiptAllocation_StillRefused()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, isSystem: true, withMenu: true);
        var (order, detail) = SeedOrderWithDetail(db, "SO-PL-12", CustomerA, DocumentStatus.Approved);
        SeedReceiptAllocation(db, order, CustomerReceiptAllocationRules.StatusActive);
        SeedPreLoading(db, "YZ-PL-12", DocumentStatus.Approved, (ProductA, 2m, detail.Id));

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            SalesOrderCancellationRules.ValidateCancellationAsync(db, ReloadOrder(db, order.Id), user.Id));

        Assert.Contains("收款引用", ex.Message);
    }

    [Fact]
    public async Task Cancel_AlreadyCancelled_KeepsExistingRepeatBehaviour()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, isSystem: true, withMenu: true);
        var (order, detail) = SeedOrderWithDetail(db, "SO-PL-13", CustomerA, DocumentStatus.Cancelled);
        SeedPreLoading(db, "YZ-PL-13", DocumentStatus.Approved, (ProductA, 2m, detail.Id));

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            SalesOrderCancellationRules.ValidateCancellationAsync(db, ReloadOrder(db, order.Id), user.Id));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("重复取消", ex.Message);
        Assert.Equal(DocumentStatus.Cancelled, db.SalesOrders.Single(o => o.Id == order.Id).Status);
    }

    // ==================== 授权（真实既有权限，fail closed） ====================

    [Fact]
    public async Task Cancel_NoIdentity_UnauthorizedEvenWithLinkedPreLoading()
    {
        using var db = TestDbFactory.Create();
        var (order, detail) = SeedOrderWithDetail(db, "SO-PL-14", CustomerA, DocumentStatus.Approved);
        SeedPreLoading(db, "YZ-PL-14", DocumentStatus.Approved, (ProductA, 2m, detail.Id));

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            SalesOrderCancellationRules.ValidateCancellationAsync(db, ReloadOrder(db, order.Id), null));

        Assert.Equal(ErrorCodes.Unauthorized, ex.Code);
    }

    [Fact]
    public async Task Cancel_RevokedMenu_ForbiddenEvenWithLinkedPreLoading()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, isSystem: false, withMenu: true);
        var (order, detail) = SeedOrderWithDetail(db, "SO-PL-15", CustomerA, DocumentStatus.Approved);
        SeedPreLoading(db, "YZ-PL-15", DocumentStatus.Approved, (ProductA, 2m, detail.Id));

        foreach (var rm in db.SysRoleMenus.Where(rm => !rm.IsDeleted)) rm.IsDeleted = true;
        await db.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            SalesOrderCancellationRules.ValidateCancellationAsync(db, ReloadOrder(db, order.Id), user.Id));

        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    [Fact]
    public async Task Cancel_OutOfCustomerScope_ForbiddenEvenWithLinkedPreLoading()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, isSystem: false, withMenu: true);
        var (order, detail) = SeedOrderWithDetail(db, "SO-PL-16", CustomerA, DocumentStatus.Approved);
        SeedPreLoading(db, "YZ-PL-16", DocumentStatus.Approved, (ProductA, 2m, detail.Id));

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            SalesOrderCancellationRules.ValidateCancellationAsync(db, ReloadOrder(db, order.Id), user.Id));

        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    [Fact]
    public async Task Cancel_LimitedUserWithExistingMenuAndScope_SucceedsAfterRelease()
    {
        using var db = TestDbFactory.Create();
        var (order, detail) = SeedOrderWithDetail(db, "SO-PL-17", CustomerA, DocumentStatus.Approved);
        var userId = SeedLimitedSalesmanUser(db, CustomerA, withMenu: true);
        SeedPreLoading(db, "YZ-PL-17", DocumentStatus.Cancelled, (ProductA, 2m, detail.Id));

        var ctl = NewSalesOrderController(db);
        TestAuth.SetUser(ctl, userId);

        Assert.IsType<OkObjectResult>(await ctl.Cancel(order.Id));
        Assert.Equal(DocumentStatus.Cancelled, db.SalesOrders.Single(o => o.Id == order.Id).Status);
    }

    [Fact]
    public async Task Cancel_LimitedUserWithoutMenu_Forbidden_NoPrivilegedFallback()
    {
        using var db = TestDbFactory.Create();
        var (order, detail) = SeedOrderWithDetail(db, "SO-PL-18", CustomerA, DocumentStatus.Approved);
        var userId = SeedLimitedSalesmanUser(db, CustomerA, withMenu: false);
        SeedPreLoading(db, "YZ-PL-18", DocumentStatus.Cancelled, (ProductA, 2m, detail.Id));

        var ctl = NewSalesOrderController(db);
        TestAuth.SetUser(ctl, userId);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Cancel(order.Id));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
        Assert.Equal(DocumentStatus.Approved, db.SalesOrders.Single(o => o.Id == order.Id).Status);
    }

    // ==================== 控制器授权继承（无权限扩展） ====================

    [Fact]
    public void Controller_Authorization_OnlyInheritedFromBase()
    {
        Assert.Empty(typeof(SalesOrderController).GetCustomAttributes(typeof(AuthorizeAttribute), false));
        Assert.NotEmpty(typeof(DocumentControllerBase<SalesOrder>).GetCustomAttributes(typeof(AuthorizeAttribute), false));
    }

    // ==================== 测试脚手架 ====================

    private static SalesOrderController NewSalesOrderController(ErpDbContext db)
        => new(db, new DocumentNumberService(db));

    private static SalesOrder ReloadOrder(ErpDbContext db, long id)
        => db.SalesOrders.Include(o => o.Details).Single(o => o.Id == id);

    private static (SalesOrder Order, SalesOrderDetail Detail) SeedOrderWithDetail(
        ErpDbContext db, string no, long customerId, DocumentStatus status,
        decimal quantity = 5m, decimal unitPrice = 10m)
    {
        var order = new SalesOrder
        {
            OrderNo = no,
            OrderDate = DateTime.Today.AddDays(-5),
            CustomerId = customerId,
            Currency = Currency.USD,
            ExchangeRate = 1m,
            Status = status
        };
        db.SalesOrders.Add(order);
        db.SaveChanges();

        var detail = new SalesOrderDetail
        {
            SalesOrderId = order.Id,
            ProductId = ProductA,
            ProductName = "商品A",
            Unit = "PCS",
            Quantity = quantity,
            UnitPrice = unitPrice,
            Amount = quantity * unitPrice
        };
        db.SalesOrderDetails.Add(detail);
        db.SaveChanges();

        order.TotalAmount = detail.Amount;
        db.SaveChanges();
        return (order, detail);
    }

    private static ContainerPreLoading SeedPreLoading(ErpDbContext db, string no, DocumentStatus status,
        params (long ProductId, decimal Quantity, long? SourceDetailId)[] lines)
        => SeedPreLoadingCore(db, no, status, deleted: false, lines);

    private static ContainerPreLoading SeedDeletedPreLoading(ErpDbContext db, string no, DocumentStatus status,
        params (long ProductId, decimal Quantity, long? SourceDetailId)[] lines)
        => SeedPreLoadingCore(db, no, status, deleted: true, lines);

    private static ContainerPreLoading SeedPreLoadingCore(ErpDbContext db, string no, DocumentStatus status,
        bool deleted, (long ProductId, decimal Quantity, long? SourceDetailId)[] lines)
    {
        var pre = new ContainerPreLoading
        {
            PreLoadingNo = no,
            LoadingDate = DateTime.Today,
            Status = status,
            IsDeleted = deleted
        };
        db.ContainerPreLoadings.Add(pre);
        db.SaveChanges();

        foreach (var (productId, quantity, sourceDetailId) in lines)
        {
            db.ContainerPreLoadingDetails.Add(new ContainerPreLoadingDetail
            {
                PreLoadingId = pre.Id,
                ProductId = productId,
                ProductName = productId == ProductA ? "商品A" : "商品B",
                Quantity = quantity,
                SourceSalesOrderDetailId = sourceDetailId
            });
        }
        db.SaveChanges();
        return pre;
    }

    private static void SeedStockOut(ErpDbContext db, string no, long orderId, long customerId,
        DocumentStatus status)
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
        DocumentStatus status)
    {
        db.PurchaseOrders.Add(new PurchaseOrder
        {
            OrderNo = no,
            OrderDate = DateTime.Today,
            SupplierId = SupplierA,
            Currency = Currency.CNY,
            OwningSalesOrderId = owningSalesOrderId,
            OwningSalesOrderNo = no,
            Status = status
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

    /// <summary>播种特权登录用户（系统内置角色）；非特权账号必须显式授予「销售订单」菜单。</summary>
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

        if (withMenu) GrantSalesOrderMenu(db, role.Id);
        return user;
    }

    /// <summary>
    /// 播种受限制业务员账号（既有权限口径：登录名 = 员工编码 + 业务员客户归属 + 显式菜单授权），
    /// 用于验证「真实认证 + 既有权限可取消 / 缺菜单即拒绝」，绝无匿名或管理员兜底。
    /// </summary>
    private static long SeedLimitedSalesmanUser(ErpDbContext db, long customerId, bool withMenu)
    {
        var code = $"so-pl-op-{Guid.NewGuid():N}";
        var employee = new BaseEmployee
        {
            EmployeeCode = code,
            EmployeeName = code,
            IsSalesman = true,
            Status = 1
        };
        db.BaseEmployees.Add(employee);
        db.SaveChanges();

        db.BaseCustomers.Add(new BaseCustomer
        {
            Id = customerId,
            CustomerCode = $"C-{customerId}",
            CustomerName = "客户A",
            EmpId = employee.Id,
            Status = 1
        });
        db.SaveChanges();

        var user = new SysUser
        {
            UserName = code,
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = code,
            Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        db.SaveChanges();

        var role = new SysRole
        {
            RoleName = "受限业务员",
            RoleCode = $"Limited-{Guid.NewGuid():N}",
            IsSystem = false
        };
        db.SysRoles.Add(role);
        db.SaveChanges();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        db.SaveChanges();

        if (withMenu) GrantSalesOrderMenu(db, role.Id);
        return user.Id;
    }

    private static void GrantSalesOrderMenu(ErpDbContext db, long roleId)
    {
        var menu = new SysMenu
        {
            MenuCode = SalesOrderCancellationRules.RequiredMenuCode,
            MenuName = SalesOrderCancellationRules.RequiredMenuText,
            MenuType = MenuType.Menu
        };
        db.SysMenus.Add(menu);
        db.SaveChanges();
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = roleId, MenuId = menu.Id });
        db.SaveChanges();
    }
}
