using ERP.Api.Controllers;
using ERP.Application.Common;
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
/// ERP-420 规范销售订单普通写入（新增 / 修改 / 删除 / 提交 / 审核 / 取消）实时授权与权威客户范围护栏单元测试。
/// <para>覆盖：无来源（手工）与无法解析历史来源路径同样要求实时身份 + 既有「销售订单」菜单 + 权威客户范围；
/// 持久化订单客户先于读取明细 / 暴露状态复核（范围外 / 已删除 / 不存在返回同一非披露错误）；拟议客户先于
/// 新增 / 改派复核；缺失 / 非法 / 已删除 / 已禁用身份、缺菜单 / 已撤销菜单一律 fail closed；允许的写入仍可用；
/// 拒绝时零副作用且取消保留既有下游护栏。</para>
/// <para>全部使用内存数据库（<see cref="TestDbFactory"/>）+ 真实 <see cref="SalesOrderController"/>，
/// 不连接 SQL Server、不启动 API、不运行浏览器验收。</para>
/// </summary>
public class SalesOrderMutationAuthorizationTests
{
    // ==================== 新增（含手工 / 无法解析历史来源） ====================

    [Fact]
    public async Task 新增_手工无来源订单_本人客户且具备菜单_放行()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var userId = SeedOperator(db, new[] { customer.Id }, withMenu: true);
        var ctl = NewController(db, userId);

        Assert.IsType<OkObjectResult>(await ctl.Create(NewOrderBody(customer.Id)));

        var order = db.SalesOrders.Single();
        Assert.Equal(customer.Id, order.CustomerId);
        Assert.Null(order.SourcePiId);
        Assert.Null(order.SourceQuotationId);
    }

    [Fact]
    public async Task 新增_手工无来源订单_越界客户_拒绝且零写入()
    {
        using var db = TestDbFactory.Create();
        var own = SeedCustomer(db);
        var foreign = SeedCustomer(db);
        var userId = SeedOperator(db, new[] { own.Id }, withMenu: true);
        var ctl = NewController(db, userId);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Create(NewOrderBody(foreign.Id)));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
        Assert.Empty(db.SalesOrders);
        Assert.Empty(db.SalesOrderDetails);
    }

    [Fact]
    public async Task 新增_无法解析历史来源_越界客户_拒绝且零写入_修复无来源绕过()
    {
        using var db = TestDbFactory.Create();
        var own = SeedCustomer(db);
        var foreign = SeedCustomer(db);
        var userId = SeedOperator(db, new[] { own.Id }, withMenu: true);
        var ctl = NewController(db, userId);

        // 显式来源 Id 全部无法解析（历史值），此前该路径不解析任何授权：越界客户必须 fail closed。
        var body = NewOrderBody(foreign.Id, quotationId: 8_420_777L, piId: 8_420_888L,
            quotationNo: "QT-HIST", piNo: "PI-HIST");
        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Create(body));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
        Assert.Empty(db.SalesOrders);
    }

    [Fact]
    public async Task 新增_无法解析历史来源_本人客户_放行并原样保留显式历史值()
    {
        using var db = TestDbFactory.Create();
        var own = SeedCustomer(db);
        var userId = SeedOperator(db, new[] { own.Id }, withMenu: true);
        var ctl = NewController(db, userId);

        var body = NewOrderBody(own.Id, quotationId: 8_420_777L, piId: 8_420_888L,
            quotationNo: "QT-HIST", piNo: "PI-HIST");
        Assert.IsType<OkObjectResult>(await ctl.Create(body));

        var order = db.SalesOrders.Single();
        Assert.Equal(8_420_777L, order.SourceQuotationId);
        Assert.Equal("QT-HIST", order.SourceQuotationNo);
        Assert.Equal(8_420_888L, order.SourcePiId);
        Assert.Equal("PI-HIST", order.SourcePiNo);
    }

    // ==================== 身份 / 菜单拒绝矩阵 ====================

    [Fact]
    public async Task 新增_无身份_按未认证拒绝()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var ctl = NewController(db, userId: null);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Create(NewOrderBody(customer.Id)));
        Assert.Equal(ErrorCodes.Unauthorized, ex.Code);
        Assert.Empty(db.SalesOrders);
    }

    [Fact]
    public async Task 新增_非法身份_按未认证拒绝()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var ctl = ControllerWithRawIdentity(db, "not-a-number");

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Create(NewOrderBody(customer.Id)));
        Assert.Equal(ErrorCodes.Unauthorized, ex.Code);
        Assert.Empty(db.SalesOrders);
    }

    [Fact]
    public async Task 新增_已删除账号_按未认证拒绝()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var userId = SeedOperator(db, new[] { customer.Id }, withMenu: true);
        db.SysUsers.Single(u => u.Id == userId).IsDeleted = true;
        await db.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            NewController(db, userId).Create(NewOrderBody(customer.Id)));
        Assert.Equal(ErrorCodes.Unauthorized, ex.Code);
        Assert.Empty(db.SalesOrders);
    }

    [Fact]
    public async Task 新增_已禁用账号_按权限不足拒绝()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var userId = SeedOperator(db, new[] { customer.Id }, withMenu: true, status: UserStatus.Disabled);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            NewController(db, userId).Create(NewOrderBody(customer.Id)));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
        Assert.Empty(db.SalesOrders);
    }

    [Fact]
    public async Task 新增_缺菜单_按权限不足拒绝()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var userId = SeedOperator(db, new[] { customer.Id }, withMenu: false);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            NewController(db, userId).Create(NewOrderBody(customer.Id)));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
        Assert.Contains("销售订单", ex.Message);
        Assert.Empty(db.SalesOrders);
    }

    [Fact]
    public async Task 新增_已撤销菜单_按权限不足拒绝()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var userId = SeedOperator(db, new[] { customer.Id }, withMenu: true);
        foreach (var rm in db.SysRoleMenus.Where(m => !m.IsDeleted)) rm.IsDeleted = true;
        await db.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            NewController(db, userId).Create(NewOrderBody(customer.Id)));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
        Assert.Empty(db.SalesOrders);
    }

    // ==================== 修改 ====================

    [Fact]
    public async Task 修改_本人订单_放行且客户不变()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var userId = SeedOperator(db, new[] { customer.Id }, withMenu: true);
        var order = SeedOrder(db, customer.Id, DocumentStatus.Pending, total: 1000m);
        var ctl = NewController(db, userId);

        Assert.IsType<OkObjectResult>(await ctl.Update(order.Id, NewOrderBody(customer.Id, remark: "UPDATED")));

        var saved = db.SalesOrders.Single(o => o.Id == order.Id);
        Assert.Equal(customer.Id, saved.CustomerId);
        Assert.Equal("UPDATED", saved.Remark);
    }

    [Fact]
    public async Task 修改_持久化越界订单_非披露NotFound_且明细不变()
    {
        using var db = TestDbFactory.Create();
        var own = SeedCustomer(db);
        var foreign = SeedCustomer(db);
        var userId = SeedOperator(db, new[] { own.Id }, withMenu: true);
        var order = SeedOrder(db, foreign.Id, DocumentStatus.Pending, total: 1000m);
        var detailsBefore = await db.SalesOrderDetails.CountAsync(d => d.SalesOrderId == order.Id);
        var ctl = NewController(db, userId);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Update(order.Id, NewOrderBody(foreign.Id, remark: "HACK")));

        Assert.Equal(ErrorCodes.NotFound, ex.Code);
        Assert.Equal(SalesOrderMutationAuthorizationRules.OrderDeniedText, ex.Message);
        var saved = db.SalesOrders.Single(o => o.Id == order.Id);
        Assert.NotEqual("HACK", saved.Remark);
        Assert.Equal(detailsBefore, await db.SalesOrderDetails.CountAsync(d => d.SalesOrderId == order.Id));
    }

    [Fact]
    public async Task 修改_改派到越界客户_拒绝且客户不变()
    {
        using var db = TestDbFactory.Create();
        var own = SeedCustomer(db);
        var foreign = SeedCustomer(db);
        var userId = SeedOperator(db, new[] { own.Id }, withMenu: true);
        var order = SeedOrder(db, own.Id, DocumentStatus.Pending, total: 1000m);
        var ctl = NewController(db, userId);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Update(order.Id, NewOrderBody(foreign.Id, remark: "REASSIGN")));

        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
        var saved = db.SalesOrders.Single(o => o.Id == order.Id);
        Assert.Equal(own.Id, saved.CustomerId);
        Assert.NotEqual("REASSIGN", saved.Remark);
    }

    [Fact]
    public async Task 修改_已删除订单_非披露NotFound()
    {
        using var db = TestDbFactory.Create();
        var own = SeedCustomer(db);
        var userId = SeedOperator(db, new[] { own.Id }, withMenu: true);
        var order = SeedOrder(db, own.Id, DocumentStatus.Pending, total: 1000m, deleted: true);
        var ctl = NewController(db, userId);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Update(order.Id, NewOrderBody(own.Id)));
        Assert.Equal(ErrorCodes.NotFound, ex.Code);
    }

    [Fact]
    public async Task 修改_无身份_按未认证拒绝()
    {
        using var db = TestDbFactory.Create();
        var own = SeedCustomer(db);
        var order = SeedOrder(db, own.Id, DocumentStatus.Pending, total: 1000m);
        var ctl = NewController(db, userId: null);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Update(order.Id, NewOrderBody(own.Id)));
        Assert.Equal(ErrorCodes.Unauthorized, ex.Code);
    }

    // ==================== 提交 / 审核 ====================

    [Fact]
    public async Task 提交与审核_本人订单_放行至已审核()
    {
        using var db = TestDbFactory.Create();
        var own = SeedCustomer(db);
        var userId = SeedOperator(db, new[] { own.Id }, withMenu: true);
        var order = SeedOrder(db, own.Id, DocumentStatus.Pending, total: 1000m);
        var ctl = NewController(db, userId);

        Assert.IsType<OkObjectResult>(await ctl.Submit(order.Id));
        Assert.Equal(DocumentStatus.Submitted, db.SalesOrders.Single(o => o.Id == order.Id).Status);
        Assert.IsType<OkObjectResult>(await ctl.Approve(order.Id));
        Assert.Equal(DocumentStatus.Approved, db.SalesOrders.Single(o => o.Id == order.Id).Status);
    }

    [Fact]
    public async Task 提交与审核_越界订单_非披露NotFound_且状态不变()
    {
        using var db = TestDbFactory.Create();
        var own = SeedCustomer(db);
        var foreign = SeedCustomer(db);
        var userId = SeedOperator(db, new[] { own.Id }, withMenu: true);
        var submitted = SeedOrder(db, foreign.Id, DocumentStatus.Submitted, total: 1000m);
        var pending = SeedOrder(db, foreign.Id, DocumentStatus.Pending, total: 1000m);
        var ctl = NewController(db, userId);

        var submitEx = await Assert.ThrowsAsync<BusinessException>(() => ctl.Submit(pending.Id));
        var approveEx = await Assert.ThrowsAsync<BusinessException>(() => ctl.Approve(submitted.Id));
        Assert.Equal(ErrorCodes.NotFound, submitEx.Code);
        Assert.Equal(ErrorCodes.NotFound, approveEx.Code);
        Assert.Equal(DocumentStatus.Pending, db.SalesOrders.Single(o => o.Id == pending.Id).Status);
        Assert.Equal(DocumentStatus.Submitted, db.SalesOrders.Single(o => o.Id == submitted.Id).Status);
    }

    [Fact]
    public async Task 提交_非法身份_按未认证拒绝()
    {
        using var db = TestDbFactory.Create();
        var own = SeedCustomer(db);
        var order = SeedOrder(db, own.Id, DocumentStatus.Pending, total: 1000m);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ControllerWithRawIdentity(db, " ").Submit(order.Id));
        Assert.Equal(ErrorCodes.Unauthorized, ex.Code);
        Assert.Equal(DocumentStatus.Pending, db.SalesOrders.Single(o => o.Id == order.Id).Status);
    }

    [Fact]
    public async Task 审核_已禁用账号_按权限不足拒绝()
    {
        using var db = TestDbFactory.Create();
        var own = SeedCustomer(db);
        var userId = SeedOperator(db, new[] { own.Id }, withMenu: true, status: UserStatus.Disabled);
        var order = SeedOrder(db, own.Id, DocumentStatus.Submitted, total: 1000m);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => NewController(db, userId).Approve(order.Id));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
        Assert.Equal(DocumentStatus.Submitted, db.SalesOrders.Single(o => o.Id == order.Id).Status);
    }

    // ==================== 删除 ====================

    [Fact]
    public async Task 删除_本人待提交订单_放行软删除()
    {
        using var db = TestDbFactory.Create();
        var own = SeedCustomer(db);
        var userId = SeedOperator(db, new[] { own.Id }, withMenu: true);
        var order = SeedOrder(db, own.Id, DocumentStatus.Pending, total: 1000m);
        var ctl = NewController(db, userId);

        Assert.IsType<OkObjectResult>(await ctl.Delete(order.Id));
        Assert.True(db.SalesOrders.Single(o => o.Id == order.Id).IsDeleted);
    }

    [Fact]
    public async Task 删除_越界订单_非披露NotFound_且未软删()
    {
        using var db = TestDbFactory.Create();
        var own = SeedCustomer(db);
        var foreign = SeedCustomer(db);
        var userId = SeedOperator(db, new[] { own.Id }, withMenu: true);
        var order = SeedOrder(db, foreign.Id, DocumentStatus.Pending, total: 1000m);
        var ctl = NewController(db, userId);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Delete(order.Id));
        Assert.Equal(ErrorCodes.NotFound, ex.Code);
        Assert.False(db.SalesOrders.Single(o => o.Id == order.Id).IsDeleted);
    }

    [Fact]
    public async Task 删除_无身份_按未认证拒绝()
    {
        using var db = TestDbFactory.Create();
        var own = SeedCustomer(db);
        var order = SeedOrder(db, own.Id, DocumentStatus.Pending, total: 1000m);
        var ctl = NewController(db, userId: null);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Delete(order.Id));
        Assert.Equal(ErrorCodes.Unauthorized, ex.Code);
        Assert.False(db.SalesOrders.Single(o => o.Id == order.Id).IsDeleted);
    }

    // ==================== 取消（保留下游护栏） ====================

    [Fact]
    public async Task 取消_本人订单_放行且保留下游护栏()
    {
        using var db = TestDbFactory.Create();
        var own = SeedCustomer(db);
        var userId = SeedOperator(db, new[] { own.Id }, withMenu: true);
        var order = SeedOrder(db, own.Id, DocumentStatus.Approved, total: 1000m);
        var ctl = NewController(db, userId);

        Assert.IsType<OkObjectResult>(await ctl.Cancel(order.Id));
        Assert.Equal(DocumentStatus.Cancelled, db.SalesOrders.Single(o => o.Id == order.Id).Status);
    }

    [Fact]
    public async Task 取消_本人订单存在已审核出库_下游护栏仍拒绝()
    {
        using var db = TestDbFactory.Create();
        var own = SeedCustomer(db);
        var userId = SeedOperator(db, new[] { own.Id }, withMenu: true);
        var order = SeedOrder(db, own.Id, DocumentStatus.Approved, total: 1000m);
        db.StockOuts.Add(new StockOut
        {
            StockOutNo = "SO-OUT-ERP420", StockOutDate = DateTime.Today, SalesOrderId = order.Id,
            CustomerId = own.Id, WarehouseId = 942_001L, Status = DocumentStatus.Approved
        });
        await db.SaveChangesAsync();
        var ctl = NewController(db, userId);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Cancel(order.Id));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Equal(DocumentStatus.Approved, db.SalesOrders.Single(o => o.Id == order.Id).Status);
    }

    [Fact]
    public async Task 取消_越界订单_非披露NotFound_且状态不变()
    {
        using var db = TestDbFactory.Create();
        var own = SeedCustomer(db);
        var foreign = SeedCustomer(db);
        var userId = SeedOperator(db, new[] { own.Id }, withMenu: true);
        var order = SeedOrder(db, foreign.Id, DocumentStatus.Approved, total: 1000m);
        var ctl = NewController(db, userId);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Cancel(order.Id));
        Assert.Equal(ErrorCodes.NotFound, ex.Code);
        Assert.Equal(DocumentStatus.Approved, db.SalesOrders.Single(o => o.Id == order.Id).Status);
    }

    [Fact]
    public async Task 取消_无身份_按未认证拒绝()
    {
        using var db = TestDbFactory.Create();
        var own = SeedCustomer(db);
        var order = SeedOrder(db, own.Id, DocumentStatus.Approved, total: 1000m);
        var ctl = NewController(db, userId: null);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Cancel(order.Id));
        Assert.Equal(ErrorCodes.Unauthorized, ex.Code);
        Assert.Equal(DocumentStatus.Approved, db.SalesOrders.Single(o => o.Id == order.Id).Status);
    }

    // ==================== 工厂与种子数据 ====================

    private static SalesOrderController NewController(ErpDbContext db, long? userId)
    {
        var controller = new SalesOrderController(db, new DocumentNumberService(db));
        TestAuth.SetUser(controller, userId);
        return controller;
    }

    /// <summary>写入一个非数字身份声明（<c>CurrentUserId()</c> 解析失败 → 按未认证 fail closed）。</summary>
    private static SalesOrderController ControllerWithRawIdentity(ErpDbContext db, string raw)
    {
        var controller = new SalesOrderController(db, new DocumentNumberService(db));
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    new[] { new Claim(ClaimTypes.NameIdentifier, raw) }, "Test"))
            }
        };
        return controller;
    }

    private static BaseCustomer SeedCustomer(ErpDbContext db)
    {
        var customer = new BaseCustomer
        {
            CustomerCode = $"C-{Guid.NewGuid():N}",
            CustomerName = "ERP420 客户",
            Status = 1,
            DepositRatio = 30m
        };
        db.BaseCustomers.Add(customer);
        db.SaveChanges();
        // ERP-423：规范销售订单写入要求实时商品主数据；一并播种本用例使用的既有合法商品（只补缺失行）。
        if (!db.BaseProducts.Any(p => p.Id == 942_001L))
        {
            db.BaseProducts.Add(new BaseProduct
            {
                Id = 942_001L, ProductCode = "ERP420-P", ProductName = "ERP420 商品", Status = 1
            });
            db.SaveChanges();
        }

        return customer;
    }

    private static SalesOrder SeedOrder(ErpDbContext db, long customerId, DocumentStatus status,
        decimal total, bool deleted = false)
    {
        var order = new SalesOrder
        {
            OrderNo = $"SO-ERP420-{Guid.NewGuid():N}"[..20],
            OrderDate = DateTime.Today,
            CustomerId = customerId,
            Currency = Currency.USD,
            ExchangeRate = 7.2m,
            DepositRatio = 30m,
            TotalAmount = total,
            Status = status,
            IsDeleted = deleted
        };
        db.SalesOrders.Add(order);
        db.SaveChanges();
        db.SalesOrderDetails.Add(new SalesOrderDetail
        {
            SalesOrderId = order.Id, ProductId = 942_001L, ProductName = "ERP420 商品",
            Unit = "PCS", Quantity = 1m, UnitPrice = total, Amount = total
        });
        db.SaveChanges();
        return order;
    }

    private static SalesOrder NewOrderBody(long customerId, long? quotationId = null, long? piId = null,
        string quotationNo = "", string piNo = "", string remark = "")
        => new()
        {
            OrderDate = DateTime.Today,
            CustomerId = customerId,
            Currency = Currency.USD,
            ExchangeRate = 7.2m,
            DepositRatio = 30m,
            Remark = remark,
            SourceQuotationId = quotationId,
            SourceQuotationNo = quotationNo,
            SourcePiId = piId,
            SourcePiNo = piNo,
            Details = new List<SalesOrderDetail>
            {
                new() { ProductId = 942_001L, ProductName = "ERP420 商品", Unit = "PCS", Quantity = 2m, UnitPrice = 5m }
            }
        };

    /// <summary>
    /// 播种受限（非特权）业务员账号：登录账号 = 员工编码（ERP-097 权威映射）、可选既有「销售订单」菜单、
    /// 并把给定客户分配给该业务员（未给出的客户即范围外）。绝不新增任何生产权限模型。
    /// </summary>
    private static long SeedOperator(ErpDbContext db, IEnumerable<long> customerIds, bool withMenu,
        UserStatus status = UserStatus.Enabled)
    {
        var userName = $"erp420-{Guid.NewGuid():N}";
        var employee = new BaseEmployee
        {
            EmployeeCode = userName, EmployeeName = "ERP420 业务员", IsSalesman = true, Status = 1
        };
        db.BaseEmployees.Add(employee);
        db.SaveChanges();

        var user = new SysUser
        {
            UserName = userName, PasswordHash = "hash", PasswordSalt = "salt",
            DisplayName = "ERP420 业务员", Status = status
        };
        db.SysUsers.Add(user);
        db.SaveChanges();

        var role = new SysRole { RoleCode = $"ERP420-{Guid.NewGuid():N}", RoleName = "ERP420 角色" };
        db.SysRoles.Add(role);
        db.SaveChanges();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });

        if (withMenu)
        {
            var menu = new SysMenu
            {
                MenuCode = SalesOrderMutationAuthorizationRules.RequiredMenuCode,
                MenuName = SalesOrderMutationAuthorizationRules.RequiredMenuText,
                MenuType = MenuType.Menu
            };
            db.SysMenus.Add(menu);
            db.SaveChanges();
            db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
        }

        foreach (var customerId in customerIds.Distinct())
        {
            var customer = db.BaseCustomers.Single(c => c.Id == customerId);
            customer.EmpId = employee.Id;
        }

        db.SaveChanges();
        return user.Id;
    }
}
