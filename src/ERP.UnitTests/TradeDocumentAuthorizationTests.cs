using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Reflection;
using System.Security.Claims;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// 单证中心实时授权、权威客户范围与写入护栏单元测试（ERP-394，内存库）。覆盖：
/// 身份 / 账号状态 / 既有「单证中心」（doc-center）菜单授权 / 未映射业务员 fail closed 与撤销收敛；
/// 受限 / 特权数据范围下推（列表 / 导出 / 明细 / 打印同口径，无主与越界一律不泄露）；
/// 完整 CRUD 覆盖（详情 / 新增 / 修改 / 删除 / 批量删除的范围与真实性校验，混合批次整体拒绝、无部分删除）；
/// 明细行读写的父单证归属复核；由销售订单 / 装柜清单「带入预填 + 直接生成」的来源与目标授权（不可绕过）。
/// <para>进程内直接调用控制器动作（未经过 MVC 过滤器）范围取 <c>null</c>，等价既有内部口径；
/// 受限场景通过显式注入范围到 <c>HttpContext.Items</c> 覆盖，绝不把空身份当作管理员。</para>
/// </summary>
public class TradeDocumentAuthorizationTests
{
    // ==================== 0. 测试脚手架 ====================

    /// <summary>绑定 HttpContext 与（可空）已解析范围的单证中心控制器；<paramref name="scope"/> 为 null 表示进程内调用。</summary>
    private static TradeDocumentController Controller(ErpDbContext db, SalespersonDataScope? scope, long? userId = null)
    {
        var httpContext = new DefaultHttpContext();
        if (userId.HasValue)
            httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }, "Test"));
        if (scope is not null)
            httpContext.Items[TradeDocumentRequestAuthorizationFilter.ScopeItemKey] = scope;

        var controller = new TradeDocumentController(new GenericService<TradeDocument>(db), db);
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        return controller;
    }

    private static SalespersonDataScope RestrictedScope(long salesmanId, params long[] allowedCustomerIds) => new()
    {
        IsPrivileged = false,
        SalesmanId = salesmanId,
        AllowedCustomerIds = allowedCustomerIds.ToHashSet()
    };

    private static SalespersonDataScope PrivilegedScope() =>
        new() { IsPrivileged = true, SalesmanId = null, AllowedCustomerIds = null };

    private static BaseCustomer SeedCustomer(ErpDbContext db, string code, long? empId = null, int status = 1)
    {
        var customer = new BaseCustomer
        {
            CustomerCode = code,
            CustomerName = code,
            Status = status,
            CreditStatus = "正常",
            Currency = "USD",
            EmpId = empId
        };
        db.BaseCustomers.Add(customer);
        db.SaveChanges();
        return customer;
    }

    private static TradeDocument SeedDocument(ErpDbContext db, string docNo, long? customerId,
        string docType = "商业发票", string status = "待制作")
    {
        var document = new TradeDocument
        {
            DocNo = docNo,
            DocType = docType,
            Status = status,
            Currency = "USD",
            IssueDate = DateTime.Today,
            CustomerId = customerId,
            CustomerName = customerId is > 0 ? $"客户{customerId}" : string.Empty
        };
        db.TradeDocuments.Add(document);
        db.SaveChanges();
        return document;
    }

    /// <summary>播种既有「单证中心」菜单（与生产 SchemaUpgrader 同编码，不为测试新增任何授权模型）。</summary>
    private static SysMenu EnsureDocCenterMenu(ErpDbContext db)
    {
        var existing = db.SysMenus.FirstOrDefault(m => m.MenuCode == TradeDocumentAuthorizationRules.RequiredMenuCode);
        if (existing is not null) return existing;

        var menu = new SysMenu
        {
            MenuName = TradeDocumentAuthorizationRules.RequiredMenuText,
            MenuCode = TradeDocumentAuthorizationRules.RequiredMenuCode,
            Path = "/container/doc-center",
            MenuType = MenuType.Menu
        };
        db.SysMenus.Add(menu);
        db.SaveChanges();
        return menu;
    }

    /// <summary>播种受限业务员账号：登录名 = 员工编码（ERP-097 权威映射），可选既有菜单授权。</summary>
    private static async Task<(SysUser User, BaseEmployee Employee, SysRole Role)> SeedOperatorAsync(
        ErpDbContext db, bool grantMenu = true, UserStatus status = UserStatus.Enabled)
    {
        var code = $"DOC394-OP-{Guid.NewGuid():N}";
        var employee = new BaseEmployee
        {
            EmployeeCode = code, EmployeeName = code, IsSalesman = true, Status = 1
        };
        db.BaseEmployees.Add(employee);
        await db.SaveChangesAsync();

        var user = new SysUser
        {
            UserName = code, PasswordHash = "hash", PasswordSalt = "salt", DisplayName = code, Status = status
        };
        db.SysUsers.Add(user);
        await db.SaveChangesAsync();

        var role = new SysRole
        {
            RoleName = $"单证操作角色-{code}", RoleCode = $"DOC394-{Guid.NewGuid():N}", IsSystem = false
        };
        db.SysRoles.Add(role);
        await db.SaveChangesAsync();

        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        if (grantMenu)
        {
            var menu = EnsureDocCenterMenu(db);
            db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
        }
        await db.SaveChangesAsync();
        return (user, employee, role);
    }

    // ==================== 1. 身份 / 账号状态 / 菜单授权 ====================

    [Fact]
    public async Task 规则层_无身份_按未认证拒绝()
    {
        using var db = TestDbFactory.Create();
        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => TradeDocumentAuthorizationRules.EnsureMenuAuthorizedAsync(db, null));
        Assert.Equal(ErrorCodes.Unauthorized, ex.Code);
    }

    [Fact]
    public async Task 规则层_账号不存在_按未认证拒绝()
    {
        using var db = TestDbFactory.Create();
        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => TradeDocumentAuthorizationRules.EnsureMenuAuthorizedAsync(db, 987654));
        Assert.Equal(ErrorCodes.Unauthorized, ex.Code);
    }

    [Fact]
    public async Task 规则层_账号已禁用_按权限不足拒绝()
    {
        using var db = TestDbFactory.Create();
        var (user, _, _) = await SeedOperatorAsync(db, status: UserStatus.Disabled);
        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => TradeDocumentAuthorizationRules.EnsureMenuAuthorizedAsync(db, user.Id));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
        Assert.Contains("已禁用", ex.Message);
    }

    [Fact]
    public async Task 规则层_无既有菜单授权_按权限不足拒绝()
    {
        using var db = TestDbFactory.Create();
        var (user, _, _) = await SeedOperatorAsync(db, grantMenu: false);
        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => TradeDocumentAuthorizationRules.EnsureMenuAuthorizedAsync(db, user.Id));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
        Assert.Contains(TradeDocumentAuthorizationRules.RequiredMenuCode, ex.Message);
    }

    [Fact]
    public async Task 规则层_未映射业务员_按权限不足拒绝()
    {
        using var db = TestDbFactory.Create();
        var menu = EnsureDocCenterMenu(db);
        var user = new SysUser
        {
            UserName = "not-a-salesman", PasswordHash = "hash", PasswordSalt = "salt",
            DisplayName = "未映射", Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        db.SaveChanges();
        var role = new SysRole { RoleName = "角色", RoleCode = $"R-{Guid.NewGuid():N}", IsSystem = false };
        db.SysRoles.Add(role);
        db.SaveChanges();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
        db.SaveChanges();

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => TradeDocumentAuthorizationRules.EnsureMenuAuthorizedAsync(db, user.Id));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
        Assert.Contains("未映射", ex.Message);
    }

    [Fact]
    public async Task 规则层_既有菜单加业务员映射_返回受限实时范围()
    {
        using var db = TestDbFactory.Create();
        var (user, employee, _) = await SeedOperatorAsync(db);
        var own = SeedCustomer(db, "DOC394-OWN", employee.Id);
        SeedCustomer(db, "DOC394-FOREIGN", empId: null);

        var scope = await TradeDocumentAuthorizationRules.EnsureMenuAuthorizedAsync(db, user.Id);
        Assert.False(scope.IsPrivileged);
        Assert.Equal(employee.Id, scope.SalesmanId);
        Assert.True(scope.AllowsCustomer(own.Id));
    }

    [Fact]
    public async Task 规则层_撤销菜单后_下一次请求立即收敛()
    {
        using var db = TestDbFactory.Create();
        var (user, _, role) = await SeedOperatorAsync(db);
        Assert.NotNull(await TradeDocumentAuthorizationRules.EnsureMenuAuthorizedAsync(db, user.Id));

        var grant = db.SysRoleMenus.Single(rm => rm.RoleId == role.Id);
        db.SysRoleMenus.Remove(grant);
        db.SaveChanges();

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => TradeDocumentAuthorizationRules.EnsureMenuAuthorizedAsync(db, user.Id));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    [Fact]
    public async Task 规则层_特权账号_无菜单授权仍保留既有访问()
    {
        using var db = TestDbFactory.Create();
        var userId = TestAuth.SeedPrivilegedUser(db);
        var scope = await TradeDocumentAuthorizationRules.EnsureMenuAuthorizedAsync(db, userId);
        Assert.True(scope.IsPrivileged);
        Assert.Null(scope.AllowedCustomerIds);
    }

    [Fact]
    public void 范围过滤_受限排除无主与越界_特权与进程内不过滤()
    {
        using var db = TestDbFactory.Create();
        var own = SeedDocument(db, "DOC-OWN", 10);
        SeedDocument(db, "DOC-FOREIGN", 20);
        SeedDocument(db, "DOC-UNLINKED", null);

        var visible = db.TradeDocuments.AsNoTracking()
            .Where(TradeDocumentAuthorizationRules.ScopeFilter(RestrictedScope(1, 10))).ToList();
        Assert.Single(visible);
        Assert.Equal(own.Id, visible[0].Id);

        Assert.Equal(3, db.TradeDocuments.AsNoTracking()
            .Where(TradeDocumentAuthorizationRules.ScopeFilter(PrivilegedScope())).Count());
        Assert.Equal(3, db.TradeDocuments.AsNoTracking()
            .Where(TradeDocumentAuthorizationRules.ScopeFilter(null)).Count());
    }

    [Fact]
    public void HTTP_身份解析_缺失或非法返回null_未过滤返回null范围()
    {
        Assert.Null(TradeDocumentRequestAuthorizationFilter.ResolveUserId(null));
        Assert.Null(TradeDocumentRequestAuthorizationFilter.ScopeFrom(null));
        Assert.Null(TradeDocumentRequestAuthorizationFilter.ResolveUserId(new ClaimsPrincipal(new ClaimsIdentity())));
        Assert.Equal(7, TradeDocumentRequestAuthorizationFilter.ResolveUserId(
            new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, "7") }))));
    }

    // ==================== 2. 列表 / 详情 / 打印 / 导出（范围下推，不泄露） ====================

    private static T DataOf<T>(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<ApiResponse<T>>(ok.Value);
        Assert.Equal(ErrorCodes.Success, response.Code);
        Assert.NotNull(response.Data);
        return response.Data!;
    }

    private static async Task<BusinessException> CodeOfAsync(int expected, Func<Task> action)
    {
        var ex = await Assert.ThrowsAsync<BusinessException>(action);
        Assert.Equal(expected, ex.Code);
        return ex;
    }

    [Fact]
    public async Task 列表_受限只统计范围内单证_越界与无主不计入()
    {
        using var db = TestDbFactory.Create();
        var own = SeedDocument(db, "DOC-A-OWN", 10);
        SeedDocument(db, "DOC-A-FOREIGN", 20);
        SeedDocument(db, "DOC-A-UNLINKED", null);

        var page = DataOf<PagedResult<TradeDocument>>(
            await Controller(db, RestrictedScope(1, 10)).GetPaged(new PageQuery()));
        Assert.Equal(1, page.Total);
        Assert.Equal(own.Id, page.Items.Single().Id);

        var all = DataOf<PagedResult<TradeDocument>>(await Controller(db, null).GetPaged(new PageQuery()));
        Assert.Equal(3, all.Total);
    }

    [Fact]
    public async Task 详情_越界与无主fail_closed且不泄露单证号()
    {
        using var db = TestDbFactory.Create();
        var own = SeedDocument(db, "DOC-B-OWN", 10);
        var foreign = SeedDocument(db, "DOC-B-FOREIGN-SECRET", 20);
        var unlinked = SeedDocument(db, "DOC-B-UNLINKED", null);
        var controller = Controller(db, RestrictedScope(1, 10));

        Assert.Equal(own.Id, DataOf<TradeDocument>(await controller.GetById(own.Id)).Id);
        var foreignEx = await CodeOfAsync(ErrorCodes.Forbidden, () => controller.GetById(foreign.Id));
        Assert.DoesNotContain("DOC-B-FOREIGN-SECRET", foreignEx.Message);
        var unlinkedEx = await CodeOfAsync(ErrorCodes.Forbidden, () => controller.GetById(unlinked.Id));
        Assert.DoesNotContain("DOC-B-UNLINKED", unlinkedEx.Message);
    }

    [Fact]
    public async Task 打印_越界与无主fail_closed()
    {
        using var db = TestDbFactory.Create();
        var own = SeedDocument(db, "DOC-C-OWN", 10);
        var foreign = SeedDocument(db, "DOC-C-FOREIGN", 20);
        var controller = Controller(db, RestrictedScope(1, 10));

        Assert.NotNull(DataOf<TradeDocumentPrintModel>(await controller.GetPrint(own.Id)));
        await CodeOfAsync(ErrorCodes.Forbidden, () => controller.GetPrint(foreign.Id));
    }

    [Fact]
    public async Task 导出_受限导出不含越界与无主单证()
    {
        using var db = TestDbFactory.Create();
        SeedDocument(db, "DOC-D-OWN", 10);
        SeedDocument(db, "DOC-D-FOREIGN", 20);
        SeedDocument(db, "DOC-D-UNLINKED", null);

        var file = Assert.IsType<FileContentResult>(
            await Controller(db, RestrictedScope(1, 10)).ExportExcel(null, null, null, null, null, null));
        var docNos = ReadExcelColumn(file.FileContents, "单证编号");
        Assert.Contains("DOC-D-OWN", docNos);
        Assert.DoesNotContain("DOC-D-FOREIGN", docNos);
        Assert.DoesNotContain("DOC-D-UNLINKED", docNos);
    }

    /// <summary>读取 xlsx 首个工作表指定中文列头的所有取值（导出内容断言用）。</summary>
    private static List<string> ReadExcelColumn(byte[] bytes, string title)
    {
        using var stream = new MemoryStream(bytes);
        using var workbook = new NPOI.XSSF.UserModel.XSSFWorkbook(stream);
        var sheet = workbook.GetSheetAt(0);
        var header = sheet.GetRow(0);
        var index = -1;
        for (var c = 0; c < header.LastCellNum; c++)
        {
            if (string.Equals(header.GetCell(c)?.ToString(), title, StringComparison.Ordinal)) { index = c; break; }
        }
        Assert.True(index >= 0, $"导出缺少列「{title}」");

        var values = new List<string>();
        for (var r = 1; r <= sheet.LastRowNum; r++)
        {
            var value = sheet.GetRow(r)?.GetCell(index)?.ToString();
            if (!string.IsNullOrWhiteSpace(value)) values.Add(value);
        }
        return values;
    }

    // ==================== 3. 新增 / 修改 / 删除 / 批量删除 ====================

    [Fact]
    public async Task 新增_伪造越界客户_整体拒绝且不落库()
    {
        using var db = TestDbFactory.Create();
        var foreign = SeedCustomer(db, "DOC-E-FOREIGN");
        var forged = new TradeDocument { DocNo = "DOC-E-FORGED", DocType = "商业发票", CustomerId = foreign.Id };

        var ex = await CodeOfAsync(ErrorCodes.Forbidden,
            () => Controller(db, RestrictedScope(1, 10)).Create(forged));
        Assert.DoesNotContain("DOC-E-FORGED", ex.Message);
        Assert.Empty(db.TradeDocuments);
    }

    [Fact]
    public async Task 新增_受限账号无权威客户归属_整体拒绝()
    {
        using var db = TestDbFactory.Create();
        var unlinked = new TradeDocument { DocNo = "DOC-F-UNLINKED", DocType = "商业发票" };
        await CodeOfAsync(ErrorCodes.Forbidden,
            () => Controller(db, RestrictedScope(1, 10)).Create(unlinked));
        Assert.Empty(db.TradeDocuments);
    }

    [Fact]
    public async Task 新增_范围内可写客户_必须真实启用()
    {
        using var db = TestDbFactory.Create();
        await CodeOfAsync(ErrorCodes.InvalidParameter,
            () => Controller(db, RestrictedScope(1, 999)).Create(
                new TradeDocument { DocNo = "DOC-G-MISSING", CustomerId = 999 }));

        var disabled = SeedCustomer(db, "DOC-G-DISABLED", status: 0);
        await CodeOfAsync(ErrorCodes.InvalidParameter,
            () => Controller(db, RestrictedScope(1, disabled.Id))
                .Create(new TradeDocument { DocNo = "DOC-G-DISABLED", CustomerId = disabled.Id }));

        var active = SeedCustomer(db, "DOC-G-ACTIVE");
        var created = DataOf<TradeDocument>(await Controller(db, RestrictedScope(1, active.Id))
            .Create(new TradeDocument { DocNo = "DOC-G-ACTIVE", DocType = "商业发票", CustomerId = active.Id }));
        Assert.Equal(active.Id, created.CustomerId);
        Assert.Equal(active.Id, db.TradeDocuments.Single().CustomerId);
    }

    [Fact]
    public async Task 修改_已存越界拒绝_且原值不变()
    {
        using var db = TestDbFactory.Create();
        var foreign = SeedDocument(db, "DOC-H-FOREIGN", 20);
        var edit = new TradeDocument { DocNo = "DOC-H-EDITED", DocType = "商业发票", CustomerId = 20 };

        await CodeOfAsync(ErrorCodes.Forbidden,
            () => Controller(db, RestrictedScope(1, 10)).Update(foreign.Id, edit));
        Assert.Equal("DOC-H-FOREIGN", db.TradeDocuments.Single(d => d.Id == foreign.Id).DocNo);
    }

    [Fact]
    public async Task 修改_已存允许但改写为越界客户_整体拒绝()
    {
        using var db = TestDbFactory.Create();
        var foreign = SeedCustomer(db, "DOC-I-FOREIGN");
        var own = SeedDocument(db, "DOC-I-OWN", 10);
        var forged = new TradeDocument { DocNo = "DOC-I-OWN", DocType = "商业发票", CustomerId = foreign.Id };

        await CodeOfAsync(ErrorCodes.Forbidden,
            () => Controller(db, RestrictedScope(1, 10)).Update(own.Id, forged));
        Assert.Equal(10, db.TradeDocuments.Single(d => d.Id == own.Id).CustomerId);
    }

    [Fact]
    public async Task 删除_越界拒绝且行保留()
    {
        using var db = TestDbFactory.Create();
        var foreign = SeedDocument(db, "DOC-J-FOREIGN", 20);
        await CodeOfAsync(ErrorCodes.Forbidden,
            () => Controller(db, RestrictedScope(1, 10)).Delete(foreign.Id));
        Assert.False(db.TradeDocuments.Single(d => d.Id == foreign.Id).IsDeleted);
    }

    [Fact]
    public async Task 批量删除_混合允许与越界_整体拒绝且无部分删除()
    {
        using var db = TestDbFactory.Create();
        var own = SeedDocument(db, "DOC-K-OWN", 10);
        var foreign = SeedDocument(db, "DOC-K-FOREIGN", 20);
        var unlinked = SeedDocument(db, "DOC-K-UNLINKED", null);

        await CodeOfAsync(ErrorCodes.Forbidden, () => Controller(db, RestrictedScope(1, 10))
            .BatchDelete(new List<long> { own.Id, foreign.Id, unlinked.Id }));

        Assert.Empty(db.TradeDocuments.Where(d => d.IsDeleted));
    }

    [Fact]
    public async Task 批量删除_全部允许成功()
    {
        using var db = TestDbFactory.Create();
        var first = SeedDocument(db, "DOC-L-1", 10);
        var second = SeedDocument(db, "DOC-L-2", 10);

        var ok = Assert.IsType<OkObjectResult>(await Controller(db, RestrictedScope(1, 10))
            .BatchDelete(new List<long> { first.Id, second.Id }));
        Assert.Equal(ErrorCodes.Success, Assert.IsType<ApiResponse<object>>(ok.Value).Code);
        Assert.All(db.TradeDocuments.ToList(), d => Assert.True(d.IsDeleted));
    }

    // ==================== 4. 明细行读写（父单证归属复核） ====================

    private static TradeDocumentItemSaveDto ItemDto() => new()
    {
        ProductCode = "P-1", ProductNameCn = "毛巾", Quantity = 2m, UnitPrice = 5m, Unit = "箱"
    };

    [Fact]
    public async Task 明细读取_越界与无主fail_closed()
    {
        using var db = TestDbFactory.Create();
        var own = SeedDocument(db, "DOC-M-OWN", 10);
        var foreign = SeedDocument(db, "DOC-M-FOREIGN", 20);
        await TradeDocumentItemService.CreateAsync(db, own.Id, ItemDto());
        await TradeDocumentItemService.CreateAsync(db, foreign.Id, ItemDto());

        var controller = Controller(db, RestrictedScope(1, 10));
        Assert.Single(DataOf<TradeDocumentItemListDto>(await controller.GetItems(own.Id)).Items);
        await CodeOfAsync(ErrorCodes.Forbidden, () => controller.GetItems(foreign.Id));
    }

    [Fact]
    public async Task 明细新增修改删除_越界fail_closed且不改写()
    {
        using var db = TestDbFactory.Create();
        var foreign = SeedDocument(db, "DOC-N-FOREIGN", 20);
        var seeded = await TradeDocumentItemService.CreateAsync(db, foreign.Id, ItemDto());
        var controller = Controller(db, RestrictedScope(1, 10));

        await CodeOfAsync(ErrorCodes.Forbidden, () => controller.CreateItem(foreign.Id, ItemDto()));
        await CodeOfAsync(ErrorCodes.Forbidden, () => controller.UpdateItem(seeded.Id, ItemDto()));
        await CodeOfAsync(ErrorCodes.Forbidden, () => controller.DeleteItem(seeded.Id));

        Assert.Equal(1, db.TradeDocumentItems.Count(i => !i.IsDeleted && i.TradeDocumentId == foreign.Id));
        Assert.Equal(2m, db.TradeDocumentItems.Single(i => i.Id == seeded.Id).Quantity);
    }

    // ==================== 5. 来源生成 / 预填（目标授权不可绕过） ====================

    private static SalesOrderController SalesOrderCtl(ErpDbContext db, SalespersonDataScope? scope)
    {
        var httpContext = new DefaultHttpContext();
        if (scope is not null)
            httpContext.Items[TradeDocumentRequestAuthorizationFilter.ScopeItemKey] = scope;
        var controller = new SalesOrderController(db, new DocumentNumberService(db));
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        return controller;
    }

    private static ContainerLoadingListController LoadingCtl(ErpDbContext db, SalespersonDataScope? scope)
    {
        var httpContext = new DefaultHttpContext();
        if (scope is not null)
            httpContext.Items[TradeDocumentRequestAuthorizationFilter.ScopeItemKey] = scope;
        var controller = new ContainerLoadingListController(db, new DocumentNumberService(db));
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        return controller;
    }

    private static SalesOrder SeedOrder(ErpDbContext db, string orderNo, long customerId)
    {
        var order = new SalesOrder
        {
            OrderNo = orderNo, OrderDate = DateTime.Today, CustomerId = customerId,
            Currency = Currency.USD, TotalAmount = 100m, Status = DocumentStatus.Approved
        };
        db.SalesOrders.Add(order);
        db.SaveChanges();
        return order;
    }

    [Fact]
    public async Task 销售订单生成_越界来源拒绝且不落库()
    {
        using var db = TestDbFactory.Create();
        var foreign = SeedCustomer(db, "DOC-O-FOREIGN");
        var order = SeedOrder(db, "SO-DOC-O-FOREIGN", foreign.Id);

        await CodeOfAsync(ErrorCodes.Forbidden,
            () => SalesOrderCtl(db, RestrictedScope(1, 10)).GenerateTradeDocuments(order.Id, null));
        Assert.Empty(db.TradeDocuments);
        Assert.Empty(db.TradeDocumentItems);
    }

    [Fact]
    public async Task 销售订单生成_本人来源成功_目标归属权威客户()
    {
        using var db = TestDbFactory.Create();
        var own = SeedCustomer(db, "DOC-P-OWN");
        var order = SeedOrder(db, "SO-DOC-P-OWN", own.Id);

        var ok = Assert.IsType<OkObjectResult>(await SalesOrderCtl(db, RestrictedScope(1, own.Id))
            .GenerateTradeDocuments(order.Id, null));
        Assert.Equal(ErrorCodes.Success, Assert.IsType<ApiResponse<TradeDocGenerateResult>>(ok.Value).Code);
        Assert.NotEmpty(db.TradeDocuments);
        Assert.All(db.TradeDocuments.ToList(), d => Assert.Equal(own.Id, d.CustomerId));
    }

    [Fact]
    public async Task 销售订单预填_越界来源拒绝且不泄露客户()
    {
        using var db = TestDbFactory.Create();
        var foreign = SeedCustomer(db, "DOC-Q-FOREIGN-SECRET");
        var order = SeedOrder(db, "SO-DOC-Q-FOREIGN", foreign.Id);

        var ex = await CodeOfAsync(ErrorCodes.Forbidden,
            () => SalesOrderCtl(db, RestrictedScope(1, 10)).TradeDocumentPrefill(order.Id));
        Assert.DoesNotContain("DOC-Q-FOREIGN-SECRET", ex.Message);
    }

    [Fact]
    public async Task 装柜清单预填_越界来源拒绝()
    {
        using var db = TestDbFactory.Create();
        var foreign = SeedCustomer(db, "DOC-R-FOREIGN");
        var list = new ContainerLoadingList
        {
            LoadingListNo = "LL-DOC-R", LoadingDate = DateTime.Today, CustomerId = foreign.Id,
            Status = DocumentStatus.Approved
        };
        db.ContainerLoadingLists.Add(list);
        db.SaveChanges();

        await CodeOfAsync(ErrorCodes.Forbidden,
            () => LoadingCtl(db, RestrictedScope(1, 10)).TradeDocumentPrefill(list.Id));
    }

    [Fact]
    public void 生成目标守卫_受限账号无权威归属_拒绝_特权与进程内保持口径()
    {
        var target = new TradeDocument { DocNo = "DOC-S-TARGET", DocType = "商业发票" };
        var ex = Assert.Throws<BusinessException>(
            () => TradeDocumentGeneration.EnsureGeneratedTargetAuthorized(RestrictedScope(1, 10), target));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);

        TradeDocumentGeneration.EnsureGeneratedTargetAuthorized(PrivilegedScope(), target);
        TradeDocumentGeneration.EnsureGeneratedTargetAuthorized(null, target);
    }

    // ==================== 6. 接线契约（完整覆盖 + 生成入口强制门） ====================

    [Fact]
    public void 控制器_完整覆盖基类CRUD_并声明HTTP强制门()
    {
        var type = typeof(TradeDocumentController);
        foreach (var name in new[] { "GetPaged", "GetAll", "GetById", "Create", "Update", "Delete", "BatchDelete" })
        {
            var method = type.GetMethod(name, BindingFlags.Public | BindingFlags.Instance);
            Assert.NotNull(method);
            Assert.Equal(type, method!.DeclaringType);   // 必须由 TradeDocumentController 自身覆盖，而非继承基类
        }

        Assert.Contains(type.GetCustomAttributes(inherit: true),
            a => a is TradeDocumentRequestAuthorizationFilter);
    }

    [Fact]
    public void 生成入口_销售订单与装柜清单路由均声明HTTP强制门()
    {
        foreach (var (controllerType, name) in new[]
                 {
                     (typeof(SalesOrderController), nameof(SalesOrderController.TradeDocumentPrefill)),
                     (typeof(SalesOrderController), nameof(SalesOrderController.GenerateTradeDocuments)),
                     (typeof(ContainerLoadingListController), nameof(ContainerLoadingListController.TradeDocumentPrefill)),
                     (typeof(ContainerLoadingListController), nameof(ContainerLoadingListController.GenerateTradeDocuments))
                 })
        {
            var method = controllerType.GetMethod(name, BindingFlags.Public | BindingFlags.Instance);
            Assert.NotNull(method);
            Assert.Contains(method!.GetCustomAttributes(inherit: true),
                a => a is TradeDocumentRequestAuthorizationFilter);
        }
    }
}
