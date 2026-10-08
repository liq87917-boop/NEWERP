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
/// ERP-346 采购订单显式归属销售订单链接规则单元测试。
/// 覆盖：授权链接并派生权威快照、未关联不臆造链接、来源非正 / 不存在 / 已删除 / 已取消 / 未审核拒绝、
/// 无身份 / 无菜单 / 客户数据范围外拒绝、归属客户冲突、商品 / 单位不兼容、伪造快照以权威为准、
/// 以及更新时来源在编辑与保存之间变更被重新解析拒绝。
/// <para>全部使用内存数据库，不连接 SQL Server、不启动 API、不运行浏览器验收。</para>
/// </summary>
public class PurchaseSalesOrderLinkTests
{
    private const long ProductA = 946201L;
    private const long ProductB = 946202L;
    private const long CustomerA = 946301L;
    private const long CustomerB = 946302L;

    // ==================== 脚手架 ====================

    private static PurchaseOrderController NewController(ErpDbContext db)
        => new(db, new DocumentNumberService(db));

    private static BaseProduct SeedProduct(ErpDbContext db, long id, string code, string name, string unit)
    {
        var product = new BaseProduct
        {
            Id = id,
            ProductCode = code,
            ProductName = name,
            Spec = "规格A",
            Unit = unit
        };
        db.BaseProducts.Add(product);
        db.SaveChanges();
        return product;
    }

    private static BaseCustomer SeedCustomer(ErpDbContext db, long id, string code, string name, long? empId = null)
    {
        var customer = new BaseCustomer { Id = id, CustomerCode = code, CustomerName = name, EmpId = empId };
        db.BaseCustomers.Add(customer);
        db.SaveChanges();
        return customer;
    }

    private static SalesOrder SeedApprovedSalesOrder(ErpDbContext db, string orderNo, long customerId,
        params (long ProductId, string Unit)[] lines)
    {
        var order = new SalesOrder
        {
            OrderNo = orderNo,
            OrderDate = DateTime.Today.AddDays(-5),
            CustomerId = customerId,
            Currency = Currency.USD,
            Status = DocumentStatus.Approved,
            Details = lines.Select(l => new SalesOrderDetail
            {
                ProductId = l.ProductId,
                ProductName = $"商品{l.ProductId}",
                Spec = "规格A",
                Unit = l.Unit,
                Quantity = 10m,
                UnitPrice = 20m,
                Amount = 200m
            }).ToList()
        };
        db.SalesOrders.Add(order);
        db.SaveChanges();
        return order;
    }

    private static SysMenu SeedMenu(ErpDbContext db, string code)
    {
        var menu = new SysMenu { MenuName = code, MenuCode = code, MenuType = MenuType.Menu };
        db.SysMenus.Add(menu);
        db.SaveChanges();
        return menu;
    }

    /// <summary>播种一个「系统内置角色 + 采购订单菜单授权」的特权用户。</summary>
    private static long SeedPrivilegedPurchaseUser(ErpDbContext db)
    {
        var user = new SysUser
        {
            UserName = $"priv-{Guid.NewGuid():N}",
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = "特权采购用户",
            Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        db.SaveChanges();

        var role = new SysRole { RoleName = "特权角色", RoleCode = $"Privileged-{Guid.NewGuid():N}", IsSystem = true };
        db.SysRoles.Add(role);
        db.SaveChanges();

        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        db.SaveChanges();

        var menu = SeedMenu(db, PurchaseSalesOrderLinkRules.RequiredMenuCode);
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
        db.SaveChanges();
        return user.Id;
    }

    /// <summary>播种一个「系统内置角色但无采购订单菜单授权」的用户。</summary>
    private static long SeedUserWithoutMenu(ErpDbContext db)
    {
        var user = new SysUser
        {
            UserName = $"nomenu-{Guid.NewGuid():N}",
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = "无菜单用户",
            Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        db.SaveChanges();

        var role = new SysRole { RoleName = "系统角色", RoleCode = $"System-{Guid.NewGuid():N}", IsSystem = true };
        db.SysRoles.Add(role);
        db.SaveChanges();

        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        db.SaveChanges();
        return user.Id;
    }

    /// <summary>播种一个受限制业务员账号（非系统角色 + 采购订单菜单，映射到指定员工编码）。</summary>
    private static long SeedRestrictedUser(ErpDbContext db, string code)
    {
        var employee = new BaseEmployee { EmployeeCode = code, EmployeeName = code, IsSalesman = true };
        db.BaseEmployees.Add(employee);
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

        var role = new SysRole { RoleName = code, RoleCode = $"Role-{code}", IsSystem = false };
        db.SysRoles.Add(role);
        db.SaveChanges();

        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        db.SaveChanges();

        var menu = SeedMenu(db, PurchaseSalesOrderLinkRules.RequiredMenuCode);
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
        db.SaveChanges();
        return user.Id;
    }

    private static PurchaseOrder NewPurchaseOrder(long? owningSalesOrderId,
        params (long ProductId, string Unit, decimal Quantity, decimal UnitPrice)[] lines)
        => new()
        {
            OrderDate = DateTime.Today,
            SupplierId = 1,
            OwningSalesOrderId = owningSalesOrderId,
            Details = lines.Select(l => new PurchaseOrderDetail
            {
                ProductId = l.ProductId,
                ProductName = $"商品{l.ProductId}",
                Spec = "规格A",
                Unit = l.Unit,
                Quantity = l.Quantity,
                UnitPrice = l.UnitPrice
            }).ToList()
        };

    // ==================== 链接解析 / 派生 ====================

    [Fact]
    public async Task Create_显式链接已审核销售订单_派生权威来源快照并保留原币与原价()
    {
        using var db = TestDbFactory.Create();
        SeedProduct(db, ProductA, "P-LINK-1", "商品A", "PCS");
        var customer = SeedCustomer(db, CustomerA, "C-LINK-1", "ACME IMPORT");
        var so = SeedApprovedSalesOrder(db, "SO-AUTH-1", customer.Id, (ProductA, "PCS"));
        var userId = SeedPrivilegedPurchaseUser(db);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, userId);

        var result = await ctl.Create(NewPurchaseOrder(so.Id, (ProductA, "PCS", 2m, 5m)));

        Assert.IsType<OkObjectResult>(result);
        var saved = db.PurchaseOrders.Include(o => o.Details).Single();
        Assert.Equal(so.Id, saved.OwningSalesOrderId);
        Assert.Equal("SO-AUTH-1", saved.OwningSalesOrderNo);
        Assert.Equal(customer.Id, saved.OwningCustomerId);
        Assert.Equal("ACME IMPORT", saved.OwningCustomerName);
        Assert.Equal(10m, saved.TotalAmount);
        Assert.Equal(Currency.CNY, saved.Currency);          // 采购原币不被销售订单 USD 覆盖
        Assert.Equal(5m, saved.Details.Single().UnitPrice);  // 采购原价保持不变
    }

    [Fact]
    public async Task Create_未关联采购_不臆造销售链接()
    {
        using var db = TestDbFactory.Create();
        var ctl = NewController(db);
        // 未关联路径不要求身份
        var result = await ctl.Create(NewPurchaseOrder(null, (ProductA, "PCS", 3m, 4m)));

        Assert.IsType<OkObjectResult>(result);
        var saved = db.PurchaseOrders.Single();
        Assert.Null(saved.OwningSalesOrderId);
        Assert.Null(saved.OwningCustomerId);
        Assert.Equal(string.Empty, saved.OwningSalesOrderNo);
        Assert.Equal(12m, saved.TotalAmount);
    }

    [Fact]
    public async Task Create_归属销售订单Id非正_拒绝()
    {
        using var db = TestDbFactory.Create();
        var userId = SeedPrivilegedPurchaseUser(db);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, userId);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Create(NewPurchaseOrder(0L)));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Empty(db.PurchaseOrders);
    }

    [Fact]
    public async Task Create_归属销售订单不存在_拒绝()
    {
        using var db = TestDbFactory.Create();
        var userId = SeedPrivilegedPurchaseUser(db);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, userId);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Create(NewPurchaseOrder(999999L)));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("不存在或已删除", ex.Message);
        Assert.Empty(db.PurchaseOrders);
    }

    [Fact]
    public async Task Create_归属销售订单已删除_拒绝()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, CustomerA, "C-DEL-1", "客户A");
        var so = SeedApprovedSalesOrder(db, "SO-DEL-1", customer.Id, (ProductA, "PCS"));
        so.IsDeleted = true;
        db.SaveChanges();
        var userId = SeedPrivilegedPurchaseUser(db);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, userId);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Create(NewPurchaseOrder(so.Id)));
        Assert.Contains("不存在或已删除", ex.Message);
        Assert.Empty(db.PurchaseOrders);
    }

    [Fact]
    public async Task Create_归属销售订单已取消_拒绝()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, CustomerA, "C-CANCEL-1", "客户A");
        var so = SeedApprovedSalesOrder(db, "SO-CANCEL-1", customer.Id, (ProductA, "PCS"));
        so.Status = DocumentStatus.Cancelled;
        db.SaveChanges();
        var userId = SeedPrivilegedPurchaseUser(db);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, userId);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Create(NewPurchaseOrder(so.Id)));
        Assert.Contains("已取消", ex.Message);
        Assert.Empty(db.PurchaseOrders);
    }

    [Fact]
    public async Task Create_归属销售订单未审核_拒绝()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, CustomerA, "C-PENDING-1", "客户A");
        var so = SeedApprovedSalesOrder(db, "SO-PENDING-1", customer.Id, (ProductA, "PCS"));
        so.Status = DocumentStatus.Pending;
        db.SaveChanges();
        var userId = SeedPrivilegedPurchaseUser(db);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, userId);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Create(NewPurchaseOrder(so.Id)));
        Assert.Contains("未审核", ex.Message);
        Assert.Empty(db.PurchaseOrders);
    }

    // ==================== 授权 / 范围 ====================

    [Fact]
    public async Task Create_无身份_拒绝()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, CustomerA, "C-NOID-1", "客户A");
        var so = SeedApprovedSalesOrder(db, "SO-NOID-1", customer.Id, (ProductA, "PCS"));
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, null);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Create(NewPurchaseOrder(so.Id)));
        Assert.Equal(ErrorCodes.Unauthorized, ex.Code);
        Assert.Empty(db.PurchaseOrders);
    }

    [Fact]
    public async Task Create_无采购订单菜单授权_拒绝()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, CustomerA, "C-NOMENU-1", "客户A");
        var so = SeedApprovedSalesOrder(db, "SO-NOMENU-1", customer.Id, (ProductA, "PCS"));
        var userId = SeedUserWithoutMenu(db);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, userId);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Create(NewPurchaseOrder(so.Id)));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
        Assert.Contains("模块授权", ex.Message);
        Assert.Empty(db.PurchaseOrders);
    }

    [Fact]
    public async Task Create_来源客户超出数据范围_拒绝()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, CustomerA, "C-SCOPE-1", "客户A", empId: null); // 未分配给受限业务员
        var so = SeedApprovedSalesOrder(db, "SO-SCOPE-1", customer.Id, (ProductA, "PCS"));
        var userId = SeedRestrictedUser(db, "scope-a");
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, userId);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Create(NewPurchaseOrder(so.Id)));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
        Assert.Contains("数据范围", ex.Message);
        Assert.Empty(db.PurchaseOrders);
    }

    // ==================== 兼容性 / 快照 ====================

    [Fact]
    public async Task Create_归属客户与来源客户不一致_拒绝()
    {
        using var db = TestDbFactory.Create();
        var customerA = SeedCustomer(db, CustomerA, "C-OWNA-1", "客户A");
        SeedCustomer(db, CustomerB, "C-OWNB-1", "客户B");
        var so = SeedApprovedSalesOrder(db, "SO-OWN-1", customerA.Id, (ProductA, "PCS"));
        var userId = SeedPrivilegedPurchaseUser(db);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, userId);

        var po = NewPurchaseOrder(so.Id, (ProductA, "PCS", 2m, 5m));
        po.OwningCustomerId = CustomerB;

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Create(po));
        Assert.Contains("归属客户", ex.Message);
        Assert.Empty(db.PurchaseOrders);
    }

    [Fact]
    public async Task Create_采购商品不在来源销售订单_拒绝()
    {
        using var db = TestDbFactory.Create();
        SeedProduct(db, ProductA, "P-PA-1", "商品A", "PCS");
        SeedProduct(db, ProductB, "P-PB-1", "商品B", "PCS");
        var customer = SeedCustomer(db, CustomerA, "C-PROD-1", "客户A");
        var so = SeedApprovedSalesOrder(db, "SO-PROD-1", customer.Id, (ProductA, "PCS"));
        var userId = SeedPrivilegedPurchaseUser(db);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, userId);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(NewPurchaseOrder(so.Id, (ProductB, "PCS", 2m, 5m))));
        Assert.Contains("不在归属销售订单明细中", ex.Message);
        Assert.Empty(db.PurchaseOrders);
    }

    [Fact]
    public async Task Create_采购单位与来源单位不兼容_拒绝()
    {
        using var db = TestDbFactory.Create();
        SeedProduct(db, ProductA, "P-UNIT-1", "商品A", "PCS");
        var customer = SeedCustomer(db, CustomerA, "C-UNIT-1", "客户A");
        var so = SeedApprovedSalesOrder(db, "SO-UNIT-1", customer.Id, (ProductA, "PCS"));
        var userId = SeedPrivilegedPurchaseUser(db);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, userId);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(NewPurchaseOrder(so.Id, (ProductA, "BOX", 2m, 5m))));
        Assert.Contains("单位", ex.Message);
        Assert.Empty(db.PurchaseOrders);
    }

    [Fact]
    public async Task Create_伪造来源快照_以权威来源为准()
    {
        using var db = TestDbFactory.Create();
        SeedProduct(db, ProductA, "P-FORGE-1", "商品A", "PCS");
        var customer = SeedCustomer(db, CustomerA, "C-FORGE-1", "权威客户名称");
        var so = SeedApprovedSalesOrder(db, "SO-REAL-1", customer.Id, (ProductA, "PCS"));
        var userId = SeedPrivilegedPurchaseUser(db);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, userId);

        var po = NewPurchaseOrder(so.Id, (ProductA, "PCS", 2m, 5m));
        po.OwningSalesOrderNo = "FORGED-NO";
        po.OwningCustomerName = "FORGED-NAME";

        var result = await ctl.Create(po);

        Assert.IsType<OkObjectResult>(result);
        var saved = db.PurchaseOrders.Single();
        Assert.Equal("SO-REAL-1", saved.OwningSalesOrderNo);
        Assert.Equal("权威客户名称", saved.OwningCustomerName);
    }

    [Fact]
    public async Task Update_来源在编辑与保存之间变更_拒绝且不改动现有单据()
    {
        using var db = TestDbFactory.Create();
        SeedProduct(db, ProductA, "P-UPD-1", "商品A", "PCS");
        var customer = SeedCustomer(db, CustomerA, "C-UPD-1", "客户A");
        var so = SeedApprovedSalesOrder(db, "SO-UPD-1", customer.Id, (ProductA, "PCS"));
        var userId = SeedPrivilegedPurchaseUser(db);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, userId);

        Assert.IsType<OkObjectResult>(await ctl.Create(NewPurchaseOrder(so.Id, (ProductA, "PCS", 2m, 5m))));
        var id = db.PurchaseOrders.Single().Id;

        // 编辑与保存之间：来源销售订单被取消
        so.Status = DocumentStatus.Cancelled;
        db.SaveChanges();

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Update(id, NewPurchaseOrder(so.Id, (ProductA, "PCS", 4m, 6m))));
        Assert.Contains("已取消", ex.Message);

        var saved = db.PurchaseOrders.Include(o => o.Details).Single();
        Assert.Equal(DocumentStatus.Pending, saved.Status);
        Assert.Equal("SO-UPD-1", saved.OwningSalesOrderNo);
        Assert.Equal(10m, saved.TotalAmount);
    }

    [Fact]
    public void RuleText_包含关键口径()
    {
        Assert.Contains("权威", PurchaseSalesOrderLinkRules.RuleText);
        Assert.Contains("绝不采信", PurchaseSalesOrderLinkRules.RuleText);
        Assert.Contains("不臆造", PurchaseSalesOrderLinkRules.RuleText);
        Assert.Contains("不跨币种", PurchaseSalesOrderLinkRules.RuleText);
        Assert.Contains("重新解析来源与权限", PurchaseSalesOrderLinkRules.RuleText);
    }
}


