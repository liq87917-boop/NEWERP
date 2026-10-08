using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using System.Reflection;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// 形式发票 PI 实时授权、权威客户范围与转换护栏单元测试（ERP-398，内存库）。
/// 覆盖：实时启用身份（缺失 / 已删除 / 禁用）、既有「形式发票 PI」（proforma-invoice）与「销售订单」
/// （sales-order）菜单授权（撤销立即收敛）、未映射业务员 fail closed；权威客户范围下推（本人 / 他人 / 无主）；
/// 新增 / 修改（已存与拟议两侧）/ 批量删除的越界拒绝与零副作用；PI → 销售订单转换的
/// <b>来源-only / 目标-only 拒绝</b>（来源可见绝不授予目标权限）与合法转换保留原计算 / 留痕；
/// 全部业务路由由控制器自身声明（完整覆盖）。
/// <para>全部使用内存库（<see cref="TestDbFactory"/>）与真实既有身份 / 菜单 / 业务员 / 客户数据，
/// 不连接 SQL Server、不启动 API、不执行任何 SQL / seed、不运行浏览器验收，也绝不新增任何用户授权。</para>
/// </summary>
public class ProformaInvoiceAuthorizationTests
{
    private static readonly string PiMenu = ProformaInvoiceAuthorizationRules.RequiredMenuCode;
    private static readonly string SoMenu = ProformaInvoiceAuthorizationRules.SalesOrderMenuCode;

    // ==================== 脚手架 ====================

    private static SysMenu Menu(ErpDbContext db, string code)
    {
        var existing = db.SysMenus.FirstOrDefault(m => m.MenuCode == code && !m.IsDeleted);
        if (existing is not null) return existing;
        var menu = new SysMenu { MenuName = code, MenuCode = code, MenuType = MenuType.Menu };
        db.SysMenus.Add(menu);
        db.SaveChanges();
        return menu;
    }

    /// <summary>播种受限操作员：登录账号 = 员工编码（ERP-097 权威映射），并按需授予既有 PI / 销售订单菜单。</summary>
    private static (SysUser User, BaseEmployee? Employee, SysRole Role) SeedOperator(
        ErpDbContext db, bool piMenu = true, bool soMenu = true, bool mapped = true,
        UserStatus status = UserStatus.Enabled, bool deleted = false)
    {
        var code = $"PI_AUTH_{Guid.NewGuid():N}";
        BaseEmployee? employee = null;
        if (mapped)
        {
            employee = new BaseEmployee
            {
                EmployeeCode = code, EmployeeName = code, IsSalesman = true, Status = 1
            };
            db.BaseEmployees.Add(employee);
        }

        var user = new SysUser
        {
            UserName = code, PasswordHash = "hash", PasswordSalt = "salt", DisplayName = code,
            Status = status, IsDeleted = deleted
        };
        db.SysUsers.Add(user);
        db.SaveChanges();

        var role = new SysRole
        {
            RoleName = $"PI_AUTH_ROLE_{code}", RoleCode = $"PI_AUTH_{Guid.NewGuid():N}", IsSystem = false
        };
        db.SysRoles.Add(role);
        db.SaveChanges();

        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        if (piMenu) db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = Menu(db, PiMenu).Id });
        if (soMenu) db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = Menu(db, SoMenu).Id });
        db.SaveChanges();
        return (user, employee, role);
    }

    private static BaseCustomer SeedCustomer(ErpDbContext db, long? empId)
    {
        var customer = new BaseCustomer
        {
            CustomerCode = $"PI_C_{Guid.NewGuid():N}", CustomerName = "客户", Status = 1,
            CreditStatus = "正常", EmpId = empId
        };
        db.BaseCustomers.Add(customer);
        db.SaveChanges();
        return customer;
    }

    private static ProformaInvoice SeedPi(ErpDbContext db, string no, long? customerId,
        DocumentStatus status = DocumentStatus.Pending, decimal amount = 1000m)
    {
        var pi = new ProformaInvoice
        {
            PiNo = no, PiDate = DateTime.Today, CustomerId = customerId, CustomerName = "客户",
            Currency = Currency.USD, ExchangeRate = 7.2m, DepositRatio = 30m,
            TotalAmount = amount, TotalAmountCny = amount * 7.2m, DepositAmount = amount * 0.3m,
            Status = status
        };
        pi.Details.Add(new ProformaInvoiceDetail
        {
            SortNo = 1, ProductCode = "P1", ProductName = "商品", Unit = "PCS",
            Quantity = 10m, UnitPrice = amount / 10m, Amount = amount
        });
        db.ProformaInvoices.Add(pi);
        db.SaveChanges();
        return pi;
    }

    private static ProformaInvoiceController Controller(ErpDbContext db, long? userId)
    {
        var controller = new ProformaInvoiceController(db, new DocumentNumberService(db));
        TestAuth.SetUser(controller, userId);
        return controller;
    }

    private static Task<BusinessException> Denied(Func<Task> action)
        => Assert.ThrowsAsync<BusinessException>(action);

    private static ProformaInvoice UpdateBody(long? customerId) => new()
    {
        CustomerId = customerId,
        CustomerName = "被篡改的客户名",
        Details = new List<ProformaInvoiceDetail> { new() { Quantity = 10m, UnitPrice = 100m } }
    };

    // ==================== 1. 实时身份 / 菜单授权（fail closed） ====================

    [Fact]
    public async Task 无身份_访问PI_按未认证拒绝()
    {
        using var db = TestDbFactory.Create();
        var ex = await Denied(() => Controller(db, null).GetPaged(new PageQuery(), null, null, null));
        Assert.Equal(ErrorCodes.Unauthorized, ex.Code);
    }

    [Fact]
    public async Task 已删除账号_按未认证拒绝()
    {
        using var db = TestDbFactory.Create();
        var (user, _, _) = SeedOperator(db, deleted: true);
        var ex = await Denied(() => Controller(db, user.Id).GetPaged(new PageQuery(), null, null, null));
        Assert.Equal(ErrorCodes.Unauthorized, ex.Code);
    }

    [Fact]
    public async Task 禁用账号_按权限不足拒绝()
    {
        using var db = TestDbFactory.Create();
        var (user, _, _) = SeedOperator(db, status: UserStatus.Disabled);
        var ex = await Denied(() => Controller(db, user.Id).GetPaged(new PageQuery(), null, null, null));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    [Fact]
    public async Task 缺少PI菜单_按权限不足拒绝_且不返回任何数据()
    {
        using var db = TestDbFactory.Create();
        var (user, employee, _) = SeedOperator(db, piMenu: false, soMenu: true);
        var own = SeedCustomer(db, employee!.Id);
        SeedPi(db, "PI-NOMENU", own.Id);

        var ex = await Denied(() => Controller(db, user.Id).GetPaged(new PageQuery(), null, null, null));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
        Assert.Contains(ProformaInvoiceAuthorizationRules.MenuDeniedText, ex.Message);
    }

    [Fact]
    public async Task 未映射业务员_按权限不足拒绝()
    {
        using var db = TestDbFactory.Create();
        var (user, _, _) = SeedOperator(db, piMenu: true, mapped: false);
        var ex = await Denied(() => Controller(db, user.Id).GetPaged(new PageQuery(), null, null, null));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
        Assert.Contains(ProformaInvoiceAuthorizationRules.UnmappedOperatorText, ex.Message);
    }

    [Fact]
    public async Task 撤销菜单_下一次请求立即收敛()
    {
        using var db = TestDbFactory.Create();
        var (user, employee, role) = SeedOperator(db);
        var own = SeedCustomer(db, employee!.Id);
        var pi = SeedPi(db, "PI-REVOKE", own.Id);

        Assert.IsType<OkObjectResult>(await Controller(db, user.Id).GetById(pi.Id));

        foreach (var grant in db.SysRoleMenus.Where(rm => rm.RoleId == role.Id).ToList())
            grant.IsDeleted = true;
        db.SaveChanges();

        var ex = await Denied(() => Controller(db, user.Id).GetById(pi.Id));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    // ==================== 2. 权威客户范围（下推 / 越界 / 无主 fail closed） ====================

    [Fact]
    public async Task 列表详情打印_仅见本人客户_越界与无主failClosed()
    {
        using var db = TestDbFactory.Create();
        var (user, employee, _) = SeedOperator(db);
        var own = SeedCustomer(db, employee!.Id);
        var foreign = SeedCustomer(db, null);
        var ownPi = SeedPi(db, "PI-SCOPE-OWN", own.Id);
        var foreignPi = SeedPi(db, "PI-SCOPE-FOREIGN", foreign.Id);
        var unlinkedPi = SeedPi(db, "PI-SCOPE-UNLINKED", null);

        var ctl = Controller(db, user.Id);

        // 列表 / 计数在范围下推之后：本人 1 条，越界与无主不计入。
        var ok = Assert.IsType<OkObjectResult>(
            await ctl.GetPaged(new PageQuery { Page = 1, PageSize = 10 }, null, null, null));
        var page = Assert.IsType<ApiResponse<PagedResult<ProformaInvoice>>>(ok.Value!).Data!;
        Assert.Equal(1, page.Total);
        Assert.Equal(ownPi.Id, Assert.Single(page.Items).Id);

        // 详情 / 打印同口径：越界与无主一律按「不存在」拒绝（不泄露范围外 PI）。
        Assert.IsType<OkObjectResult>(await ctl.GetById(ownPi.Id));
        Assert.Equal(ErrorCodes.NotFound, (await Denied(() => ctl.GetById(foreignPi.Id))).Code);
        Assert.Equal(ErrorCodes.NotFound, (await Denied(() => ctl.GetById(unlinkedPi.Id))).Code);
        Assert.Equal(ErrorCodes.NotFound, (await Denied(() => ctl.GetPrint(foreignPi.Id))).Code);
        Assert.Equal(ErrorCodes.NotFound, (await Denied(() => ctl.GetPrint(unlinkedPi.Id))).Code);
    }

    [Fact]
    public async Task 新增_拟议客户越界或未知_拒绝且不消耗单号不落库()
    {
        using var db = TestDbFactory.Create();
        var (user, employee, _) = SeedOperator(db);
        SeedCustomer(db, employee!.Id);
        var foreign = SeedCustomer(db, null);
        var ctl = Controller(db, user.Id);

        var foreignEx = await Denied(() => ctl.Create(new ProformaInvoice
        {
            CustomerId = foreign.Id, CustomerName = "越界客户",
            Details = new List<ProformaInvoiceDetail> { new() { Quantity = 1m, UnitPrice = 1m } }
        }));
        Assert.Equal(ErrorCodes.Forbidden, foreignEx.Code);

        var unknownEx = await Denied(() => ctl.Create(new ProformaInvoice
        {
            CustomerId = 987654321L, CustomerName = "未知客户",
            Details = new List<ProformaInvoiceDetail> { new() { Quantity = 1m, UnitPrice = 1m } }
        }));
        Assert.Equal(ErrorCodes.Forbidden, unknownEx.Code);

        Assert.Empty(db.ProformaInvoices);
    }

    [Fact]
    public async Task 修改_已存或拟议越界_拒绝且不改写金额状态单号明细()
    {
        using var db = TestDbFactory.Create();
        var (user, employee, _) = SeedOperator(db);
        var own = SeedCustomer(db, employee!.Id);
        var foreign = SeedCustomer(db, null);
        var pi = SeedPi(db, "PI-UPD-SCOPE", own.Id, DocumentStatus.Pending, amount: 1000m);
        var ctl = Controller(db, user.Id);

        // 已存本人、拟议越界 → 拒绝。
        var ex = await Denied(() => ctl.Update(pi.Id, UpdateBody(foreign.Id)));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);

        var stored = db.ProformaInvoices.Single();
        Assert.Equal(own.Id, stored.CustomerId);
        Assert.Equal("PI-UPD-SCOPE", stored.PiNo);
        Assert.Equal(1000m, stored.TotalAmount);
        Assert.Equal(300m, stored.DepositAmount);
        Assert.Equal(DocumentStatus.Pending, stored.Status);
        Assert.Equal("客户", stored.CustomerName);
        Assert.Single(db.ProformaInvoiceDetails);

        // 已存越界（他人 PI）→ 即使拟议本人也拒绝。
        var foreignPi = SeedPi(db, "PI-UPD-FOREIGN", foreign.Id);
        var ex2 = await Denied(() => ctl.Update(foreignPi.Id, UpdateBody(own.Id)));
        Assert.Equal(ErrorCodes.Forbidden, ex2.Code);
        Assert.Equal(foreign.Id, db.ProformaInvoices.Single(o => o.Id == foreignPi.Id).CustomerId);
    }

    [Fact]
    public async Task 批量删除_混合越界_整体拒绝且不部分删除()
    {
        using var db = TestDbFactory.Create();
        var (user, employee, _) = SeedOperator(db);
        var own = SeedCustomer(db, employee!.Id);
        var foreign = SeedCustomer(db, null);
        var ownPi = SeedPi(db, "PI-BATCH-OWN", own.Id);
        var foreignPi = SeedPi(db, "PI-BATCH-FOREIGN", foreign.Id);
        var ctl = Controller(db, user.Id);

        var ex = await Denied(() => ctl.BatchDelete(new List<long> { ownPi.Id, foreignPi.Id }));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
        Assert.False(db.ProformaInvoices.Single(o => o.Id == ownPi.Id).IsDeleted);
        Assert.False(db.ProformaInvoices.Single(o => o.Id == foreignPi.Id).IsDeleted);

        // 全部本人 → 允许。
        Assert.IsType<OkObjectResult>(await ctl.BatchDelete(new List<long> { ownPi.Id }));
        Assert.True(db.ProformaInvoices.Single(o => o.Id == ownPi.Id).IsDeleted);
    }

    [Fact]
    public async Task 状态流转_越界PI_拒绝且状态不变()
    {
        using var db = TestDbFactory.Create();
        var (user, employee, _) = SeedOperator(db);
        SeedCustomer(db, employee!.Id);
        var foreign = SeedCustomer(db, null);
        var foreignPi = SeedPi(db, "PI-FLOW-FOREIGN", foreign.Id, DocumentStatus.Pending);
        var ctl = Controller(db, user.Id);

        Assert.Equal(ErrorCodes.Forbidden, (await Denied(() => ctl.Submit(foreignPi.Id))).Code);
        Assert.Equal(ErrorCodes.Forbidden, (await Denied(() => ctl.Approve(foreignPi.Id))).Code);
        Assert.Equal(ErrorCodes.Forbidden, (await Denied(() => ctl.Unaudit(foreignPi.Id))).Code);
        Assert.Equal(ErrorCodes.Forbidden, (await Denied(() => ctl.Cancel(foreignPi.Id))).Code);
        Assert.Equal(ErrorCodes.Forbidden, (await Denied(() => ctl.Void(foreignPi.Id))).Code);
        Assert.Equal(ErrorCodes.Forbidden, (await Denied(() => ctl.Delete(foreignPi.Id))).Code);

        Assert.Equal(DocumentStatus.Pending, db.ProformaInvoices.Single().Status);
        Assert.False(db.ProformaInvoices.Single().IsDeleted);
    }

    // ==================== 3. PI → 销售订单转换护栏（来源-only / 目标-only） ====================

    [Fact]
    public async Task 转换_仅销售订单菜单_无PI菜单_来源端拒绝()
    {
        using var db = TestDbFactory.Create();
        var (user, employee, _) = SeedOperator(db, piMenu: false, soMenu: true);
        var own = SeedCustomer(db, employee!.Id);
        var pi = SeedPi(db, "PI-CONV-SOURCEONLY", own.Id, DocumentStatus.Approved);

        var ex = await Denied(() => Controller(db, user.Id).ToSalesOrder(pi.Id));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);

        Assert.Empty(db.SalesOrders);
        Assert.Equal(DocumentStatus.Approved, db.ProformaInvoices.Single().Status);
    }

    [Fact]
    public async Task 转换_仅PI菜单_无销售订单菜单_目标端拒绝且不改写来源()
    {
        using var db = TestDbFactory.Create();
        var (user, employee, _) = SeedOperator(db, piMenu: true, soMenu: false);
        var own = SeedCustomer(db, employee!.Id);
        var pi = SeedPi(db, "PI-CONV-TARGETONLY", own.Id, DocumentStatus.Approved);
        var ctl = Controller(db, user.Id);

        var toOrderEx = await Denied(() => ctl.ToSalesOrder(pi.Id));
        Assert.Equal(ErrorCodes.Forbidden, toOrderEx.Code);
        Assert.Contains(ProformaInvoiceAuthorizationRules.SalesOrderMenuDeniedText, toOrderEx.Message);

        var prefillEx = await Denied(() => ctl.OrderPrefill(pi.Id));
        Assert.Equal(ErrorCodes.Forbidden, prefillEx.Code);

        Assert.Empty(db.SalesOrders);
        Assert.Equal(DocumentStatus.Approved, db.ProformaInvoices.Single().Status);
    }

    [Fact]
    public void 转换守卫_来源可见不代表目标授权()
    {
        var scope = new SalespersonDataScope
        {
            IsPrivileged = false, SalesmanId = 1, AllowedCustomerIds = new HashSet<long> { 10 }
        };

        ProformaInvoiceAuthorizationRules.EnsureSourceCustomerInScope(scope, 10);   // 来源在范围 → 允许

        var targetEx = Assert.Throws<BusinessException>(
            () => ProformaInvoiceAuthorizationRules.EnsureTargetCustomerInScope(scope, 20));
        Assert.Equal(ErrorCodes.Forbidden, targetEx.Code);

        var conversionEx = Assert.Throws<BusinessException>(
            () => SalesOrderConversion.EnsureConversionScopeAuthorized(scope, 10, 20));
        Assert.Equal(ErrorCodes.Forbidden, conversionEx.Code);

        // 无主来源 / 目标一律 fail closed。
        Assert.Equal(ErrorCodes.Forbidden,
            Assert.Throws<BusinessException>(
                () => SalesOrderConversion.EnsureConversionScopeAuthorized(scope, null, 10)).Code);

        // 特权账号与进程内调用保持既有口径。
        var privileged = new SalespersonDataScope { IsPrivileged = true, AllowedCustomerIds = null };
        SalesOrderConversion.EnsureConversionScopeAuthorized(privileged, 10, 20);
        SalesOrderConversion.EnsureConversionScopeAuthorized(null, 10, 20);
    }

    [Fact]
    public async Task 转换_本人客户且双菜单_生成订单并保留原计算与留痕()
    {
        using var db = TestDbFactory.Create();
        var (user, employee, _) = SeedOperator(db);
        var own = SeedCustomer(db, employee!.Id);
        var pi = SeedPi(db, "PI-CONV-OK", own.Id, DocumentStatus.Approved, amount: 1000m);
        pi.QuotationId = 77L;
        pi.QuotationNo = "QT-UPSTREAM-77";
        db.SaveChanges();
        var ctl = Controller(db, user.Id);

        var ok = Assert.IsType<OkObjectResult>(await ctl.ToSalesOrder(pi.Id));
        var result = Assert.IsType<ApiResponse<SalesOrderConversionResult>>(ok.Value!).Data!;
        Assert.StartsWith("SO", result.OrderNo);

        var order = db.SalesOrders.Single();
        Assert.Equal(pi.Id, order.SourcePiId);
        Assert.Equal("QT-UPSTREAM-77", order.SourceQuotationNo);
        Assert.Equal(1000m, order.TotalAmount);        // 原计算保留
        Assert.Equal(300m, order.DepositAmount);       // 原定金保留
        Assert.Equal(Currency.USD, order.Currency);    // 原币种保留
        Assert.Equal(DocumentStatus.Completed, db.ProformaInvoices.Single().Status);

        // 带入预填：不落库、不占用单号，且同样要求双菜单与来源 / 目标范围。
        var prefillPi = SeedPi(db, "PI-CONV-PREFILL", own.Id, DocumentStatus.Approved);
        var prefillOk = Assert.IsType<OkObjectResult>(await ctl.OrderPrefill(prefillPi.Id));
        var prefill = Assert.IsType<ApiResponse<SalesOrderPrefillResult>>(prefillOk.Value!).Data!;
        Assert.Equal(prefillPi.Id, prefill.Order.SourcePiId);
        Assert.Equal(string.Empty, prefill.Order.OrderNo);
        Assert.Equal(DocumentStatus.Approved, db.ProformaInvoices.Single(o => o.Id == prefillPi.Id).Status);
    }

    // ==================== 4. 接线契约（完整覆盖 + 既有菜单编码） ====================

    [Theory]
    [InlineData(nameof(ProformaInvoiceController.GetPaged))]
    [InlineData(nameof(ProformaInvoiceController.GetById))]
    [InlineData(nameof(ProformaInvoiceController.Create))]
    [InlineData(nameof(ProformaInvoiceController.Update))]
    [InlineData(nameof(ProformaInvoiceController.Submit))]
    [InlineData(nameof(ProformaInvoiceController.Approve))]
    [InlineData(nameof(ProformaInvoiceController.Unaudit))]
    [InlineData(nameof(ProformaInvoiceController.Cancel))]
    [InlineData(nameof(ProformaInvoiceController.Void))]
    [InlineData(nameof(ProformaInvoiceController.Delete))]
    [InlineData(nameof(ProformaInvoiceController.BatchDelete))]
    [InlineData(nameof(ProformaInvoiceController.GetPrint))]
    [InlineData(nameof(ProformaInvoiceController.OrderPrefill))]
    [InlineData(nameof(ProformaInvoiceController.ToSalesOrder))]
    public void 控制器_全部业务路由由控制器自身声明(string name)
    {
        var method = typeof(ProformaInvoiceController)
            .GetMethod(name, BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(method);
        Assert.Equal(typeof(ProformaInvoiceController), method!.DeclaringType);
    }

    [Fact]
    public void 复用既有菜单编码_不新增权限模型()
    {
        Assert.Equal("proforma-invoice", ProformaInvoiceAuthorizationRules.RequiredMenuCode);
        Assert.Equal("sales-order", ProformaInvoiceAuthorizationRules.SalesOrderMenuCode);
        Assert.Contains("proforma-invoice", ProformaInvoiceAuthorizationRules.RuleText);
        Assert.Contains("sales-order", ProformaInvoiceAuthorizationRules.RuleText);
        Assert.Contains("不新增", ProformaInvoiceAuthorizationRules.BoundaryText);
    }
}
