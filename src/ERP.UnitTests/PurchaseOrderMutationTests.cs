using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-425 采购订单普通草稿写入（创建 / 修改）的确定性锁协议、合法状态约束与「锁内权威重读」单元测试。
/// <para>覆盖：统一事务协议（非关系型等价无事务、绝不凭请求空归属选择安全路径）、确定性锁序（来源销售订单行升序
/// → 采购订单行）与既有锁同源、合法状态（仅待提交可修改、已提交 / 已删除 / 已取消后过期编辑不复活）、
/// 明细整体替换的完整性、已链接单据「请求未给出来源」保留血缘（绝不静默清除）、显式改绑仍校验实时已审核来源、
/// 显式非法来源 Id 拒绝、失败整体回滚并清空半成品变更跟踪，以及控制器源码接线契约。</para>
/// <para>全部使用内存数据库，不连接 SQL Server、不启动 API、不运行浏览器验收；并发「两条独立连接」由
/// <c>PurchaseOrderMutationSqlServerTests</c> 在专用 localdb 上验证。</para>
/// </summary>
public class PurchaseOrderMutationTests
{
    private const long ProductA = 957201L;
    private const long CustomerA = 957301L;

    // ==================== 1. 规则口径（纯判定） ====================

    [Fact]
    public void 确定性锁序_来源销售订单行先于采购订单行_与既有锁同源()
    {
        // 采购订单行锁与状态流转 / 普通写入共用同一把锁。
        Assert.Contains("db_owner.PurchaseOrders", PurchaseOrderMutationRules.PurchaseOrderRowLockSql);
        Assert.Contains("UPDLOCK, HOLDLOCK", PurchaseOrderMutationRules.PurchaseOrderRowLockSql);
        // 归属销售订单行锁直接复用既有规范常量（锁身份同源，绝不另起一把锁）。
        Assert.Equal(PreLoadingSalesOrderLinkRules.LockSalesOrderRowSql,
            PurchaseOrderMutationRules.SalesOrderRowLockSql);
        Assert.Contains("db_owner.SalesOrders", PurchaseOrderMutationRules.SalesOrderRowLockSql);

        Assert.Contains("升序", PurchaseOrderMutationRules.LockOrderText);
        Assert.Contains("销售订单", PurchaseOrderMutationRules.LockOrderText);
        Assert.Contains("采购订单", PurchaseOrderMutationRules.LockOrderText);
        Assert.Contains("绝不反向获取", PurchaseOrderMutationRules.LockOrderText);
        Assert.Contains("请求空归属", PurchaseOrderMutationRules.LockOrderText);
    }

    [Fact]
    public void 合并加锁来源_去重_仅正整数_确定性升序()
    {
        var ids = PurchaseOrderMutationRules.MergeSalesOrderLockIds(30L, null, 10L, 30L, 0L, -5L, 20L);
        Assert.Equal(new[] { 10L, 20L, 30L }, ids);

        Assert.Empty(PurchaseOrderMutationRules.MergeSalesOrderLockIds((long?)null));
        Assert.Empty(PurchaseOrderMutationRules.MergeSalesOrderLockIds(0L, -1L));
    }

    [Fact]
    public void 来源归一化_仅正整数视为有效来源()
    {
        Assert.Equal(7L, PurchaseOrderMutationRules.NormalizeId(7L));
        Assert.Null(PurchaseOrderMutationRules.NormalizeId(null));
        Assert.Null(PurchaseOrderMutationRules.NormalizeId(0L));
        Assert.Null(PurchaseOrderMutationRules.NormalizeId(-3L));
    }

    [Fact]
    public void 仅待提交可修改_其它状态一律拒绝()
    {
        PurchaseOrderMutationRules.EnsureEditable(DocumentStatus.Pending);

        foreach (var status in new[]
                 {
                     DocumentStatus.Submitted, DocumentStatus.Approved, DocumentStatus.Rejected,
                     DocumentStatus.Completed, DocumentStatus.Cancelled
                 })
        {
            var ex = Assert.Throws<BusinessException>(() => PurchaseOrderMutationRules.EnsureEditable(status));
            Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
            Assert.Equal(PurchaseOrderMutationRules.NotPendingEditText, ex.Message);
        }
    }

    [Fact]
    public void 来源指针未变更_按归一化口径比较()
    {
        Assert.True(PurchaseOrderMutationRules.PersistedSourceUnchanged(null, null));
        Assert.True(PurchaseOrderMutationRules.PersistedSourceUnchanged(0L, null));
        Assert.True(PurchaseOrderMutationRules.PersistedSourceUnchanged(15L, 15L));
        Assert.True(PurchaseOrderMutationRules.PersistedSourceUnchanged(-1L, null));
        Assert.False(PurchaseOrderMutationRules.PersistedSourceUnchanged(15L, 16L));
        Assert.False(PurchaseOrderMutationRules.PersistedSourceUnchanged(null, 16L));
        Assert.False(PurchaseOrderMutationRules.PersistedSourceUnchanged(15L, null));
    }

    [Fact]
    public async Task 事务协议_非关系型提供程序等价无事务()
    {
        using var db = TestDbFactory.Create();
        var transaction = await PurchaseOrderMutationRules.BeginMutationTransactionAsync(db);
        Assert.Null(transaction);
    }

    [Fact]
    public void RuleText_包含关键口径()
    {
        Assert.Contains("原子事务", PurchaseOrderMutationRules.RuleText);
        Assert.Contains("升序", PurchaseOrderMutationRules.RuleText);
        Assert.Contains("待提交", PurchaseOrderMutationRules.RuleText);
        Assert.Contains("绝不静默清除血缘", PurchaseOrderMutationRules.RuleText);
        Assert.Contains("不新增表", PurchaseOrderMutationRules.BoundaryText);
    }

    // ==================== 2. 控制器行为（内存库 + 真实登录身份） ====================

    [Fact]
    public async Task 手工创建_进入统一协议_成功落库()
    {
        using var db = TestDbFactory.Create();
        var ctl = ControllerFor(db);

        var result = await ctl.Create(NewOrder(owningCustomerId: null,
            lines: new[] { (ProductA, "PCS", 3m, 10m) }));

        Assert.IsType<OkObjectResult>(result);
        var saved = db.PurchaseOrders.Single();
        Assert.Equal(DocumentStatus.Pending, saved.Status);
        Assert.StartsWith("PO", saved.OrderNo);
        Assert.Equal(30m, saved.TotalAmount);
        Assert.Null(saved.OwningSalesOrderId);
    }

    [Fact]
    public async Task 手工改单_明细整体替换_总额重算_集合完整()
    {
        using var db = TestDbFactory.Create();
        var ctl = ControllerFor(db);
        var order = SeedOrder(db, DocumentStatus.Pending, owningSalesOrderId: null);

        await ctl.Update(order.Id, NewOrder(owningCustomerId: null,
            lines: new[] { (ProductA, "PCS", 2m, 15m), (ProductA + 1, "BOX", 4m, 5m) }, remark: "改后"));

        var stored = db.PurchaseOrders.AsNoTracking().Include(o => o.Details).Single(o => o.Id == order.Id);
        Assert.Equal("改后", stored.Remark);
        Assert.Equal(50m, stored.TotalAmount);                     // 2×15 + 4×5
        var details = stored.Details.Where(d => !d.IsDeleted).ToList();
        Assert.Equal(2, details.Count);                            // 集合完整（旧的已删除 + 新的全建）
        Assert.Contains(details, d => d.Quantity == 2m && d.UnitPrice == 15m);
        Assert.Contains(details, d => d.Quantity == 4m && d.UnitPrice == 5m);
    }

    [Fact]
    public async Task 手工改单_提交后过期编辑被拒_状态与明细零改动()
    {
        using var db = TestDbFactory.Create();
        var ctl = ControllerFor(db);
        var order = SeedOrder(db, DocumentStatus.Pending, owningSalesOrderId: null);

        Assert.IsType<OkObjectResult>(await ctl.Submit(order.Id));

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Update(order.Id, NewOrder(null, new[] { (ProductA, "PCS", 99m, 99m) })));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);

        var stored = db.PurchaseOrders.AsNoTracking().Include(o => o.Details).Single(o => o.Id == order.Id);
        Assert.Equal(DocumentStatus.Submitted, stored.Status);      // 唯一合法终态，不复活为待提交
        Assert.Equal(1000m, stored.TotalAmount);
        Assert.Equal(10m, stored.Details.Single(d => !d.IsDeleted).Quantity);
    }

    [Fact]
    public async Task 手工改单_软删除后拒绝_不复活()
    {
        using var db = TestDbFactory.Create();
        var ctl = ControllerFor(db);
        var order = SeedOrder(db, DocumentStatus.Pending, owningSalesOrderId: null);

        Assert.IsType<OkObjectResult>(await ctl.Delete(order.Id));

        await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Update(order.Id, NewOrder(null, new[] { (ProductA, "PCS", 1m, 1m) })));

        var stored = db.PurchaseOrders.AsNoTracking().Single(o => o.Id == order.Id);
        Assert.True(stored.IsDeleted);                              // 软删除不可复活
        Assert.Equal(DocumentStatus.Pending, stored.Status);
        Assert.Equal(1000m, stored.TotalAmount);
    }

    [Fact]
    public async Task 手工改单_取消后拒绝_不复活()
    {
        using var db = TestDbFactory.Create();
        var ctl = ControllerFor(db);
        var order = SeedOrder(db, DocumentStatus.Pending, owningSalesOrderId: null);

        Assert.IsType<OkObjectResult>(await ctl.Cancel(order.Id));

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Update(order.Id, NewOrder(null, new[] { (ProductA, "PCS", 1m, 1m) })));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);

        var stored = db.PurchaseOrders.AsNoTracking().Single(o => o.Id == order.Id);
        Assert.Equal(DocumentStatus.Cancelled, stored.Status);
        Assert.False(stored.IsDeleted);
    }

    [Fact]
    public async Task 已链接单据_请求未给出来源_保留血缘且绝不清除()
    {
        using var db = TestDbFactory.Create();
        var ctl = ControllerFor(db);
        var (customer, salesOrder) = SeedSalesSource(db);
        var order = SeedOrder(db, DocumentStatus.Pending, salesOrder.Id, customer.Id);

        // 请求 OwningSalesOrderId = null：必须保留已存来源（绝不静默清除血缘），其余字段正常更新。
        await ctl.Update(order.Id, NewOrder(owningCustomerId: null,
            lines: new[] { (ProductA, "PCS", 5m, 20m) }, remark: "仅改备注"));

        var stored = db.PurchaseOrders.AsNoTracking().Single(o => o.Id == order.Id);
        Assert.Equal(salesOrder.Id, stored.OwningSalesOrderId);     // 血缘保留
        Assert.Equal(salesOrder.OrderNo, stored.OwningSalesOrderNo);
        Assert.Equal(customer.Id, stored.OwningCustomerId);
        Assert.Equal("仅改备注", stored.Remark);
    }

    [Fact]
    public async Task 已链接单据_来源已取消_请求未给出来源时拒绝且血缘保留()
    {
        using var db = TestDbFactory.Create();
        var ctl = ControllerFor(db);
        var (customer, salesOrder) = SeedSalesSource(db);
        var order = SeedOrder(db, DocumentStatus.Pending, salesOrder.Id, customer.Id);

        salesOrder.Status = DocumentStatus.Cancelled;
        db.SaveChanges();

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Update(order.Id, NewOrder(null, new[] { (ProductA, "PCS", 1m, 1m) })));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("已取消", ex.Message);

        var stored = db.PurchaseOrders.AsNoTracking().Include(o => o.Details).Single(o => o.Id == order.Id);
        Assert.Equal(salesOrder.Id, stored.OwningSalesOrderId);     // 绝不静默清除血缘
        Assert.Equal(1000m, stored.TotalAmount);
        Assert.Equal(10m, stored.Details.Single(d => !d.IsDeleted).Quantity);
    }

    [Fact]
    public async Task 已链接单据_显式改绑到另一已审核来源_权威快照更新()
    {
        using var db = TestDbFactory.Create();
        var ctl = ControllerFor(db);
        var (customer, first) = SeedSalesSource(db);
        var second = SeedApprovedSalesOrder(db, "SO-425-B", customer.Id);
        var order = SeedOrder(db, DocumentStatus.Pending, first.Id, customer.Id);

        await ctl.Update(order.Id, NewOrder(owningCustomerId: null,
            lines: new[] { (ProductA, "PCS", 5m, 20m) }, salesOrderId: second.Id));

        var stored = db.PurchaseOrders.AsNoTracking().Single(o => o.Id == order.Id);
        Assert.Equal(second.Id, stored.OwningSalesOrderId);
        Assert.Equal(second.OrderNo, stored.OwningSalesOrderNo);
        Assert.Equal(customer.Id, stored.OwningCustomerId);
    }

    [Fact]
    public async Task 显式非法来源Id_拒绝且不静默清除血缘()
    {
        using var db = TestDbFactory.Create();
        var ctl = ControllerFor(db);
        var (customer, salesOrder) = SeedSalesSource(db);
        var order = SeedOrder(db, DocumentStatus.Pending, salesOrder.Id, customer.Id);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Update(order.Id, NewOrder(null, new[] { (ProductA, "PCS", 1m, 1m) }, salesOrderId: 0L)));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);

        var stored = db.PurchaseOrders.AsNoTracking().Single(o => o.Id == order.Id);
        Assert.Equal(salesOrder.Id, stored.OwningSalesOrderId);     // 非法请求绝不静默清除血缘
    }

    [Fact]
    public async Task 改单校验失败_整体回滚并清空半成品变更跟踪()
    {
        using var db = TestDbFactory.Create();
        var ctl = ControllerFor(db);
        var order = SeedOrder(db, DocumentStatus.Pending, owningSalesOrderId: null);

        // 税率越界在字段赋值 / 明细替换之后校验失败：整单回滚并清空跟踪器。
        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Update(order.Id, NewOrder(null, new[] { (ProductA, "PCS", 99m, 99m) }, taxRate: 101m)));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);

        // 即便随后再次保存，也绝不残留「删除旧明细 + 新增新明细」的半成品变更。
        await db.SaveChangesAsync();

        var stored = db.PurchaseOrders.AsNoTracking().Include(o => o.Details).Single(o => o.Id == order.Id);
        Assert.Equal(DocumentStatus.Pending, stored.Status);
        Assert.Equal(1000m, stored.TotalAmount);
        var details = stored.Details.Where(d => !d.IsDeleted).ToList();
        Assert.Single(details);
        Assert.Equal(10m, details[0].Quantity);
        Assert.Equal(100m, details[0].UnitPrice);
    }

    [Fact]
    public void 控制器接线_创建与修改进入统一事务与锁协议()
    {
        var controller = ReadSource("src/ERP.Api/Controllers/PurchaseOrderController.cs");

        Assert.Contains("PurchaseOrderMutationRules.BeginMutationTransactionAsync", controller);
        Assert.Contains("MergeSalesOrderLockIds", controller);
        Assert.Contains("AcquireSalesOrderLinkLocksAsync", controller);
        Assert.Contains("PurchaseOrderMutationRules.EnsureEditable", controller);
        Assert.Contains("PersistedSourceUnchanged", controller);
        Assert.Contains("AcquireOrderStateLocksAsync", controller);
        Assert.Contains("AcquirePurchaseOrderLockAsync", controller);
        Assert.Contains("UPDLOCK, HOLDLOCK",
            ReadSource("src/ERP.Application/Services/PurchaseOrderMutationRules.cs"));
        // 旧的「无归属即不加锁 / 不开事务」旁路必须彻底移除。
        Assert.DoesNotContain("UpdateUnlinkedAsync", controller);
        Assert.DoesNotContain("CreateUnlinkedAsync", controller);
    }

    // ==================== 脚手架 ====================

    private static PurchaseOrderController ControllerFor(ErpDbContext db)
    {
        var userId = SeedPrivilegedPurchaseUser(db);
        var ctl = new PurchaseOrderController(db, new DocumentNumberService(db));
        TestAuth.SetUser(ctl, userId);
        return ctl;
    }

    private static PurchaseOrder SeedOrder(ErpDbContext db, DocumentStatus status, long? owningSalesOrderId,
        long? owningCustomerId = null)
    {
        var order = new PurchaseOrder
        {
            OrderNo = $"PO-425-{Guid.NewGuid():N}",
            OrderDate = DateTime.Today,
            SupplierId = 1,
            Currency = Currency.CNY,
            ExchangeRate = 1m,
            OwningSalesOrderId = owningSalesOrderId,
            OwningCustomerId = owningCustomerId,
            Status = status,
            TotalAmount = 1000m,
            Details = new List<PurchaseOrderDetail>
            {
                new()
                {
                    ProductId = ProductA, ProductName = "商品A", Spec = "规格A", Unit = "PCS",
                    Quantity = 10m, UnitPrice = 100m, Amount = 1000m
                }
            }
        };
        db.PurchaseOrders.Add(order);
        db.SaveChanges();
        return order;
    }

    private static PurchaseOrder NewOrder(long? owningCustomerId,
        (long ProductId, string Unit, decimal Quantity, decimal UnitPrice)[] lines,
        long? salesOrderId = null, string remark = "", decimal taxRate = 0m)
    {
        var order = new PurchaseOrder
        {
            OrderDate = DateTime.Today,
            SupplierId = 1,
            Currency = Currency.CNY,
            ExchangeRate = 1m,
            OwningCustomerId = owningCustomerId,
            OwningSalesOrderId = salesOrderId,
            Remark = remark,
            TaxRate = taxRate
        };
        foreach (var line in lines)
            order.Details.Add(new PurchaseOrderDetail
            {
                ProductId = line.ProductId, ProductName = "商品A", Spec = "规格A", Unit = line.Unit,
                Quantity = line.Quantity, UnitPrice = line.UnitPrice
            });
        return order;
    }

    private static (BaseCustomer Customer, SalesOrder SalesOrder) SeedSalesSource(ErpDbContext db)
    {
        var customer = new BaseCustomer
        {
            Id = CustomerA, CustomerCode = "C-425", CustomerName = "客户A", Status = 1
        };
        db.BaseCustomers.Add(customer);
        db.SaveChanges();
        return (customer, SeedApprovedSalesOrder(db, "SO-425-A", customer.Id));
    }

    private static SalesOrder SeedApprovedSalesOrder(ErpDbContext db, string orderNo, long customerId)
    {
        var order = new SalesOrder
        {
            OrderNo = orderNo,
            OrderDate = DateTime.Today.AddDays(-5),
            CustomerId = customerId,
            Currency = Currency.USD,
            Status = DocumentStatus.Approved,
            Details = new List<SalesOrderDetail>
            {
                new()
                {
                    ProductId = ProductA, ProductName = "商品A", Spec = "规格A", Unit = "PCS",
                    Quantity = 100m, UnitPrice = 10m, Amount = 1000m
                }
            }
        };
        db.SalesOrders.Add(order);
        db.SaveChanges();
        return order;
    }

    /// <summary>播种特权采购账号（系统内置角色 + 既有 purchase-order 菜单），不新增任何用户授权。</summary>
    private static long SeedPrivilegedPurchaseUser(ErpDbContext db)
    {
        var user = new SysUser
        {
            UserName = $"po-425-priv-{Guid.NewGuid():N}",
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = "采购测试账号",
            Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        db.SaveChanges();

        var role = new SysRole
        {
            RoleName = "采购测试特权角色", RoleCode = $"Po425-{Guid.NewGuid():N}", IsSystem = true
        };
        db.SysRoles.Add(role);
        db.SaveChanges();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        db.SaveChanges();

        var menu = new SysMenu
        {
            ParentId = 0,
            MenuCode = PurchaseOrderAuthorizationRules.RequiredMenuCode,
            MenuName = PurchaseOrderAuthorizationRules.RequiredMenuText,
            Path = "/purchase/purchase-order",
            MenuType = MenuType.Menu,
            CreatedAt = DateTime.Now
        };
        db.SysMenus.Add(menu);
        db.SaveChanges();
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
        db.SaveChanges();

        return user.Id;
    }

    private static string ReadSource(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "NEWERP.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine(directory!.FullName,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
    }
}
