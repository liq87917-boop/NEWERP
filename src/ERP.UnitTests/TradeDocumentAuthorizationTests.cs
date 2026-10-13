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
/// 单证中心实时授权、权威客户范围与写入护栏单元测试（ERP-394 / ERP-497，内存库）。覆盖：
/// 身份 / 账号状态 / 既有「单证中心」（doc-center）菜单授权 / 未映射业务员 fail closed 与撤销收敛；
/// 受限 / 特权数据范围下推（列表 / 导出 / 明细 / 打印同口径，无主与越界一律不泄露）；
/// 完整 CRUD 覆盖（详情 / 新增 / 修改 / 删除 / 批量删除的范围与真实性校验，混合批次整体拒绝、无部分删除）；
/// 明细行读写的父单证归属复核；由销售订单 / 装柜清单「带入预填 + 直接生成」的来源与目标授权（不可绕过）。
/// <para><b>ERP-497 夹具口径</b>：身份一律是隔离测试数据里的<b>真实启用账号</b>（缺失 / 为零 / 未知 / 已删除 /
/// 禁用 / 未映射业务员 / 缺少或已撤销既有「单证中心」菜单各按其真实口径拒绝），受限操作员按其 ERP-097
/// 业务员映射（登录名 = 员工编码）持有既有菜单授权与其本人客户；特权账号使用既有系统内置角色。
/// 不再向 <c>HttpContext.Items</c> 注入范围，也不再依赖任何「未解析身份即不受限」的旁路：
/// 控制器与四个生成入口的每一个路由动作都自行解析实时授权（见
/// <see cref="TradeDocumentAuthorizationRules.PathIndependenceText"/>）。</para>
/// </summary>
public class TradeDocumentAuthorizationTests
{
    // ==================== 0. 测试脚手架 ====================

    /// <summary>绑定真实登录身份（可空 = 无身份）的单证中心控制器；默认不设置 <c>Request.Path</c>。</summary>
    private static TradeDocumentController Controller(ErpDbContext db, long? userId)
        => Controller(db, userId, path: null);

    /// <summary>
    /// 绑定真实登录身份与（可选）请求路径的单证中心控制器；<paramref name="bindContext"/>=false 表示完全不绑定
    /// <c>HttpContext</c>（纯进程内直调），用于证明授权与请求形状 / 是否绑定请求上下文完全无关。
    /// </summary>
    private static TradeDocumentController Controller(
        ErpDbContext db, long? userId, string? path, bool bindContext = true)
    {
        var controller = new TradeDocumentController(new GenericService<TradeDocument>(db), db);
        if (!bindContext) return controller;

        var httpContext = new DefaultHttpContext();
        if (userId.HasValue)
            httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }, "Test"));
        if (path is not null) httpContext.Request.Path = path;
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        return controller;
    }

    /// <summary>隔离测试数据里的业务员账号（真实启用身份 + ERP-097 业务员映射 + 既有菜单授权）。</summary>
    private sealed record OperatorFixture(SysUser User, BaseEmployee Employee, SysRole Role);

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
    private static async Task<OperatorFixture> SeedOperatorAsync(
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
        return new OperatorFixture(user, employee, role);
    }

    /// <summary>纯规则夹具：受限范围（仅用于直接测试规则方法，绝不作为路由身份注入）。</summary>
    private static SalespersonDataScope RestrictedScope(long salesmanId, params long[] allowedCustomerIds) => new()
    {
        IsPrivileged = false,
        SalesmanId = salesmanId,
        AllowedCustomerIds = allowedCustomerIds.ToHashSet()
    };

    /// <summary>纯规则夹具：特权范围（仅用于直接测试规则方法，绝不作为路由身份注入）。</summary>
    private static SalespersonDataScope PrivilegedScope() =>
        new() { IsPrivileged = true, SalesmanId = null, AllowedCustomerIds = null };

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
    public void 范围过滤_受限排除无主与越界_特权不过滤且空范围旁路已移除()
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

        // ERP-497：原来的「未解析身份（null 范围）= 匹配全部」旁路已移除：规则层不接受 null 范围。
        Assert.Throws<ArgumentNullException>(
            () => TradeDocumentAuthorizationRules.ScopeFilter(null!));
    }

    // ==================== 1.1 路由层身份形状（与请求路径 / 是否绑定 HttpContext 无关） ====================

    /// <summary>对一个代表性子集的路由动作执行并捕获业务异常（身份形状判定用）。</summary>
    private static async Task<BusinessException?> CaptureAsync(Func<Task<IActionResult>> action)
    {
        try
        {
            await action();
            return null;
        }
        catch (BusinessException ex)
        {
            return ex;
        }
    }

    [Fact]
    public async Task 路由_缺失零未知已删除身份_按未认证拒绝且零读写()
    {
        using var db = TestDbFactory.Create();
        var document = SeedDocument(db, "DOC-IDENT", 10);
        var deleted = await SeedOperatorAsync(db);
        deleted.User.IsDeleted = true;
        await db.SaveChangesAsync();

        var identities = new long?[] { null, 0, 987654321, deleted.User.Id };

        foreach (var userId in identities)
        {
            var ex = await CaptureAsync(() => Controller(db, userId).GetPaged(new PageQuery()));
            Assert.Equal(ErrorCodes.Unauthorized, ex?.Code);

            ex = await CaptureAsync(() => Controller(db, userId).GetById(document.Id));
            Assert.Equal(ErrorCodes.Unauthorized, ex?.Code);

            ex = await CaptureAsync(() => Controller(db, userId).GetItems(document.Id));
            Assert.Equal(ErrorCodes.Unauthorized, ex?.Code);

            ex = await CaptureAsync(() => Controller(db, userId).Create(
                new TradeDocument { DocNo = "DOC-IDENT-CREATE", DocType = "商业发票", CustomerId = 10 }));
            Assert.Equal(ErrorCodes.Unauthorized, ex?.Code);
        }

        Assert.Single(db.TradeDocuments);
        Assert.Empty(db.TradeDocumentItems);
    }

    [Fact]
    public async Task 路由_禁用身份_按权限不足拒绝且零读写()
    {
        using var db = TestDbFactory.Create();
        var document = SeedDocument(db, "DOC-DISABLED-ID", 10);
        var op = await SeedOperatorAsync(db, status: UserStatus.Disabled);
        var own = SeedCustomer(db, "DOC-DISABLED-OWN", op.Employee.Id);

        var ex = await CaptureAsync(() => Controller(db, op.User.Id).GetPaged(new PageQuery()));
        Assert.Equal(ErrorCodes.Forbidden, ex?.Code);

        ex = await CaptureAsync(() => Controller(db, op.User.Id).Create(
            new TradeDocument { DocNo = "DOC-DISABLED-NEW", DocType = "商业发票", CustomerId = own.Id }));
        Assert.Equal(ErrorCodes.Forbidden, ex?.Code);

        Assert.Single(db.TradeDocuments);
        Assert.Equal(own.Id, db.BaseCustomers.Single(c => c.Id == own.Id).Id);
    }

    [Fact]
    public async Task 路由_撤销既有菜单_按权限不足拒绝且零读写()
    {
        using var db = TestDbFactory.Create();
        var op = await SeedOperatorAsync(db);
        var own = SeedCustomer(db, "DOC-REVOKE-OWN", op.Employee.Id);
        var document = SeedDocument(db, "DOC-REVOKE", own.Id);

        // 撤销前：本人单证可读（真实身份 + 既有菜单 + ERP-097 范围）。
        Assert.Equal(document.Id, DataOf<TradeDocument>(await Controller(db, op.User.Id).GetById(document.Id)).Id);

        var grant = db.SysRoleMenus.Single(rm => rm.RoleId == op.Role.Id);
        db.SysRoleMenus.Remove(grant);
        db.SaveChanges();

        var ex = await CaptureAsync(() => Controller(db, op.User.Id).GetById(document.Id));
        Assert.Equal(ErrorCodes.Forbidden, ex?.Code);
        Assert.Contains(TradeDocumentAuthorizationRules.RequiredMenuCode, ex!.Message);

        ex = await CaptureAsync(() => Controller(db, op.User.Id).GetPrint(document.Id));
        Assert.Equal(ErrorCodes.Forbidden, ex?.Code);

        Assert.False(db.TradeDocuments.Single(d => d.Id == document.Id).IsDeleted);
    }

    [Fact]
    public async Task 路由_未绑定HttpContext_空路径与已赋值路径_同一身份判定一致()
    {
        using var db = TestDbFactory.Create();
        var op = await SeedOperatorAsync(db);
        var own = SeedCustomer(db, "DOC-PATH-OWN", op.Employee.Id);
        var foreign = SeedCustomer(db, "DOC-PATH-FOREIGN");
        var ownDoc = SeedDocument(db, "DOC-PATH-OWN-DOC", own.Id);
        var foreignDoc = SeedDocument(db, "DOC-PATH-FOREIGN-DOC", foreign.Id);

        // ① 完全未绑定 HttpContext 的纯进程内直调：无身份 → 未认证（绝不旁路）。
        Assert.Equal(ErrorCodes.Unauthorized,
            (await CaptureAsync(() => Controller(db, null, path: null, bindContext: false)
                .GetById(ownDoc.Id)))?.Code);

        // ② 已绑定身份但空路径 / 已赋值路径：同一身份给出完全一致的成功与拒绝口径。
        var emptyPath = Controller(db, op.User.Id, path: null);
        var populatedPath = Controller(db, op.User.Id, path: "/api/trade/documents/999999/print");

        Assert.Equal(ownDoc.Id, DataOf<TradeDocument>(await emptyPath.GetById(ownDoc.Id)).Id);
        Assert.Equal(ownDoc.Id, DataOf<TradeDocument>(await populatedPath.GetById(ownDoc.Id)).Id);

        Assert.Equal(ErrorCodes.Forbidden,
            (await CaptureAsync(() => emptyPath.GetById(foreignDoc.Id)))?.Code);
        Assert.Equal(ErrorCodes.Forbidden,
            (await CaptureAsync(() => populatedPath.GetById(foreignDoc.Id)))?.Code);

        // ③ 未绑定 HttpContext 的纯进程内直调即使带真实身份也不能绕过：没有请求上下文即没有身份。
        Assert.Equal(ErrorCodes.Unauthorized,
            (await CaptureAsync(() => Controller(db, op.User.Id, path: null, bindContext: false)
                .GetById(ownDoc.Id)))?.Code);
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
        var op = await SeedOperatorAsync(db);
        var ownCustomer = SeedCustomer(db, "DOC-A-OWN-C", op.Employee.Id);
        var foreignCustomer = SeedCustomer(db, "DOC-A-FOREIGN-C");
        var own = SeedDocument(db, "DOC-A-OWN", ownCustomer.Id);
        SeedDocument(db, "DOC-A-FOREIGN", foreignCustomer.Id);
        SeedDocument(db, "DOC-A-UNLINKED", null);

        var page = DataOf<PagedResult<TradeDocument>>(
            await Controller(db, op.User.Id).GetPaged(new PageQuery()));
        Assert.Equal(1, page.Total);
        Assert.Equal(own.Id, page.Items.Single().Id);

        var all = DataOf<PagedResult<TradeDocument>>(
            await Controller(db, TestAuth.SeedPrivilegedUser(db)).GetPaged(new PageQuery()));
        Assert.Equal(3, all.Total);
    }

    [Fact]
    public async Task 详情_越界与无主fail_closed且不泄露单证号()
    {
        using var db = TestDbFactory.Create();
        var op = await SeedOperatorAsync(db);
        var ownCustomer = SeedCustomer(db, "DOC-B-OWN-C", op.Employee.Id);
        var foreignCustomer = SeedCustomer(db, "DOC-B-FOREIGN-C");
        var own = SeedDocument(db, "DOC-B-OWN", ownCustomer.Id);
        var foreign = SeedDocument(db, "DOC-B-FOREIGN-SECRET", foreignCustomer.Id);
        var unlinked = SeedDocument(db, "DOC-B-UNLINKED", null);
        var controller = Controller(db, op.User.Id);

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
        var op = await SeedOperatorAsync(db);
        var ownCustomer = SeedCustomer(db, "DOC-C-OWN-C", op.Employee.Id);
        var foreignCustomer = SeedCustomer(db, "DOC-C-FOREIGN-C");
        var own = SeedDocument(db, "DOC-C-OWN", ownCustomer.Id);
        var foreign = SeedDocument(db, "DOC-C-FOREIGN", foreignCustomer.Id);
        var controller = Controller(db, op.User.Id);

        Assert.NotNull(DataOf<TradeDocumentPrintModel>(await controller.GetPrint(own.Id)));
        await CodeOfAsync(ErrorCodes.Forbidden, () => controller.GetPrint(foreign.Id));
    }

    [Fact]
    public async Task 导出_受限导出不含越界与无主单证()
    {
        using var db = TestDbFactory.Create();
        var op = await SeedOperatorAsync(db);
        var ownCustomer = SeedCustomer(db, "DOC-D-OWN-C", op.Employee.Id);
        var foreignCustomer = SeedCustomer(db, "DOC-D-FOREIGN-C");
        SeedDocument(db, "DOC-D-OWN", ownCustomer.Id);
        SeedDocument(db, "DOC-D-FOREIGN", foreignCustomer.Id);
        SeedDocument(db, "DOC-D-UNLINKED", null);

        var file = Assert.IsType<FileContentResult>(
            await Controller(db, op.User.Id).ExportExcel(null, null, null, null, null, null));
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
        var op = await SeedOperatorAsync(db);
        SeedCustomer(db, "DOC-E-OWN-C", op.Employee.Id);
        var foreign = SeedCustomer(db, "DOC-E-FOREIGN");
        var forged = new TradeDocument { DocNo = "DOC-E-FORGED", DocType = "商业发票", CustomerId = foreign.Id };

        var ex = await CodeOfAsync(ErrorCodes.Forbidden,
            () => Controller(db, op.User.Id).Create(forged));
        Assert.DoesNotContain("DOC-E-FORGED", ex.Message);
        Assert.Empty(db.TradeDocuments);
    }

    [Fact]
    public async Task 新增_受限账号无权威客户归属_整体拒绝()
    {
        using var db = TestDbFactory.Create();
        var op = await SeedOperatorAsync(db);
        SeedCustomer(db, "DOC-F-OWN-C", op.Employee.Id);
        var unlinked = new TradeDocument { DocNo = "DOC-F-UNLINKED", DocType = "商业发票" };

        await CodeOfAsync(ErrorCodes.Forbidden,
            () => Controller(db, op.User.Id).Create(unlinked));
        Assert.Empty(db.TradeDocuments);
    }

    [Fact]
    public async Task 新增_拟议客户真实启用的服务端校验()
    {
        using var db = TestDbFactory.Create();
        var op = await SeedOperatorAsync(db);

        // ① 范围外（无业务员映射）：不可写，绝不落库（不泄露存在性）。
        var foreign = SeedCustomer(db, "DOC-G-FOREIGN");
        await CodeOfAsync(ErrorCodes.Forbidden,
            () => Controller(db, op.User.Id).Create(
                new TradeDocument { DocNo = "DOC-G-FOREIGN", CustomerId = foreign.Id }));
        Assert.Empty(db.TradeDocuments);

        // ② 范围内但已停用：范围放行但真实性校验拒绝（必须真实启用）。
        var disabled = SeedCustomer(db, "DOC-G-DISABLED", op.Employee.Id, status: 0);
        await CodeOfAsync(ErrorCodes.InvalidParameter,
            () => Controller(db, op.User.Id).Create(
                new TradeDocument { DocNo = "DOC-G-DISABLED", CustomerId = disabled.Id }));
        Assert.Empty(db.TradeDocuments);

        // ③ 范围内且真实启用：允许写入。
        var active = SeedCustomer(db, "DOC-G-ACTIVE", op.Employee.Id);
        var created = DataOf<TradeDocument>(await Controller(db, op.User.Id)
            .Create(new TradeDocument { DocNo = "DOC-G-ACTIVE", DocType = "商业发票", CustomerId = active.Id }));
        Assert.Equal(active.Id, created.CustomerId);
        Assert.Equal(active.Id, db.TradeDocuments.Single().CustomerId);
    }

    [Fact]
    public async Task 修改_已存越界拒绝_且原值不变()
    {
        using var db = TestDbFactory.Create();
        var op = await SeedOperatorAsync(db);
        SeedCustomer(db, "DOC-H-OWN-C", op.Employee.Id);
        var foreignCustomer = SeedCustomer(db, "DOC-H-FOREIGN-C");
        var foreign = SeedDocument(db, "DOC-H-FOREIGN", foreignCustomer.Id);
        var edit = new TradeDocument
        {
            DocNo = "DOC-H-EDITED", DocType = "商业发票", CustomerId = foreignCustomer.Id
        };

        await CodeOfAsync(ErrorCodes.Forbidden,
            () => Controller(db, op.User.Id).Update(foreign.Id, edit));
        Assert.Equal("DOC-H-FOREIGN", db.TradeDocuments.Single(d => d.Id == foreign.Id).DocNo);
        Assert.Equal(foreignCustomer.Id, db.TradeDocuments.Single(d => d.Id == foreign.Id).CustomerId);
    }

    [Fact]
    public async Task 修改_已存允许但改写为越界客户_整体拒绝()
    {
        using var db = TestDbFactory.Create();
        var op = await SeedOperatorAsync(db);
        var ownCustomer = SeedCustomer(db, "DOC-I-OWN-C", op.Employee.Id);
        var foreign = SeedCustomer(db, "DOC-I-FOREIGN");
        var own = SeedDocument(db, "DOC-I-OWN", ownCustomer.Id);
        var forged = new TradeDocument
        {
            DocNo = "DOC-I-OWN", DocType = "商业发票", CustomerId = foreign.Id
        };

        await CodeOfAsync(ErrorCodes.Forbidden,
            () => Controller(db, op.User.Id).Update(own.Id, forged));
        Assert.Equal(ownCustomer.Id, db.TradeDocuments.Single(d => d.Id == own.Id).CustomerId);
    }

    [Fact]
    public async Task 删除_越界拒绝且行保留()
    {
        using var db = TestDbFactory.Create();
        var op = await SeedOperatorAsync(db);
        var foreignCustomer = SeedCustomer(db, "DOC-J-FOREIGN-C");
        var foreign = SeedDocument(db, "DOC-J-FOREIGN", foreignCustomer.Id);

        await CodeOfAsync(ErrorCodes.Forbidden,
            () => Controller(db, op.User.Id).Delete(foreign.Id));
        Assert.False(db.TradeDocuments.Single(d => d.Id == foreign.Id).IsDeleted);
    }

    [Fact]
    public async Task 批量删除_混合允许与越界_整体拒绝且无部分删除()
    {
        using var db = TestDbFactory.Create();
        var op = await SeedOperatorAsync(db);
        var ownCustomer = SeedCustomer(db, "DOC-K-OWN-C", op.Employee.Id);
        var foreignCustomer = SeedCustomer(db, "DOC-K-FOREIGN-C");
        var own = SeedDocument(db, "DOC-K-OWN", ownCustomer.Id);
        var foreign = SeedDocument(db, "DOC-K-FOREIGN", foreignCustomer.Id);
        var unlinked = SeedDocument(db, "DOC-K-UNLINKED", null);

        await CodeOfAsync(ErrorCodes.Forbidden, () => Controller(db, op.User.Id)
            .BatchDelete(new List<long> { own.Id, foreign.Id, unlinked.Id }));

        Assert.Empty(db.TradeDocuments.Where(d => d.IsDeleted));
    }

    [Fact]
    public async Task 批量删除_全部允许成功()
    {
        using var db = TestDbFactory.Create();
        var op = await SeedOperatorAsync(db);
        var ownCustomer = SeedCustomer(db, "DOC-L-OWN-C", op.Employee.Id);
        var first = SeedDocument(db, "DOC-L-1", ownCustomer.Id);
        var second = SeedDocument(db, "DOC-L-2", ownCustomer.Id);

        var ok = Assert.IsType<OkObjectResult>(await Controller(db, op.User.Id)
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
        var op = await SeedOperatorAsync(db);
        var ownCustomer = SeedCustomer(db, "DOC-M-OWN-C", op.Employee.Id);
        var foreignCustomer = SeedCustomer(db, "DOC-M-FOREIGN-C");
        var own = SeedDocument(db, "DOC-M-OWN", ownCustomer.Id);
        var foreign = SeedDocument(db, "DOC-M-FOREIGN", foreignCustomer.Id);

        var controller = Controller(db, op.User.Id);
        await controller.CreateItem(own.Id, ItemDto());                 // 允许流程：本人单证写入一行
        Assert.Single(DataOf<TradeDocumentItemListDto>(await controller.GetItems(own.Id)).Items);

        // 越界单证：读取与写入都必须 fail closed，且不产生任何行。
        await CodeOfAsync(ErrorCodes.Forbidden, () => controller.GetItems(foreign.Id));
        await CodeOfAsync(ErrorCodes.Forbidden, () => controller.CreateItem(foreign.Id, ItemDto()));
        Assert.Empty(db.TradeDocumentItems.Where(i => i.TradeDocumentId == foreign.Id));
    }

    [Fact]
    public async Task 明细新增修改删除_越界fail_closed且不改写()
    {
        using var db = TestDbFactory.Create();
        var op = await SeedOperatorAsync(db);
        var ownCustomer = SeedCustomer(db, "DOC-N-OWN-C", op.Employee.Id);
        var foreignCustomer = SeedCustomer(db, "DOC-N-FOREIGN-C");
        SeedDocument(db, "DOC-N-OWN", ownCustomer.Id);
        var foreign = SeedDocument(db, "DOC-N-FOREIGN", foreignCustomer.Id);

        // 越界单证的既有行由**特权真实身份**（既有系统内置角色）播种，不使用任何空范围旁路。
        var privileged = Controller(db, TestAuth.SeedPrivilegedUser(db));
        var seeded = DataOf<TradeDocumentItemDto>(await privileged.CreateItem(foreign.Id, ItemDto()));

        var controller = Controller(db, op.User.Id);
        await CodeOfAsync(ErrorCodes.Forbidden, () => controller.CreateItem(foreign.Id, ItemDto()));
        await CodeOfAsync(ErrorCodes.Forbidden, () => controller.UpdateItem(seeded.Id, ItemDto()));
        await CodeOfAsync(ErrorCodes.Forbidden, () => controller.DeleteItem(seeded.Id));

        Assert.Equal(1, db.TradeDocumentItems.Count(i => !i.IsDeleted && i.TradeDocumentId == foreign.Id));
        Assert.Equal(2m, db.TradeDocumentItems.Single(i => i.Id == seeded.Id).Quantity);
    }

    // ==================== 5. 来源生成 / 预填（目标授权不可绕过） ====================

    /// <summary>绑定真实登录身份的销售订单控制器（单证生成入口的实时授权由动作内部无条件执行）。</summary>
    private static SalesOrderController SalesOrderCtl(ErpDbContext db, long? userId)
    {
        var controller = new SalesOrderController(db, new DocumentNumberService(db));
        TestAuth.SetUser(controller, userId);
        return controller;
    }

    /// <summary>绑定真实登录身份的装柜清单控制器（同上）。</summary>
    private static ContainerLoadingListController LoadingCtl(ErpDbContext db, long? userId)
    {
        var controller = new ContainerLoadingListController(db, new DocumentNumberService(db));
        TestAuth.SetUser(controller, userId);
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

    private static ContainerLoadingList SeedLoadingList(ErpDbContext db, string no, long customerId)
    {
        var list = new ContainerLoadingList
        {
            LoadingListNo = no, LoadingDate = DateTime.Today, CustomerId = customerId,
            Status = DocumentStatus.Approved
        };
        db.ContainerLoadingLists.Add(list);
        db.SaveChanges();
        return list;
    }

    [Fact]
    public async Task 销售订单生成_越界来源拒绝且不落库()
    {
        using var db = TestDbFactory.Create();
        var op = await SeedOperatorAsync(db);
        SeedCustomer(db, "DOC-O-OWN-C", op.Employee.Id);
        var foreign = SeedCustomer(db, "DOC-O-FOREIGN");
        var order = SeedOrder(db, "SO-DOC-O-FOREIGN", foreign.Id);

        await CodeOfAsync(ErrorCodes.Forbidden,
            () => SalesOrderCtl(db, op.User.Id).GenerateTradeDocuments(order.Id, null));
        Assert.Empty(db.TradeDocuments);
        Assert.Empty(db.TradeDocumentItems);
    }

    [Fact]
    public async Task 销售订单生成_本人来源成功_目标归属权威客户()
    {
        using var db = TestDbFactory.Create();
        var op = await SeedOperatorAsync(db);
        var own = SeedCustomer(db, "DOC-P-OWN", op.Employee.Id);
        var order = SeedOrder(db, "SO-DOC-P-OWN", own.Id);

        var ok = Assert.IsType<OkObjectResult>(await SalesOrderCtl(db, op.User.Id)
            .GenerateTradeDocuments(order.Id, null));
        Assert.Equal(ErrorCodes.Success, Assert.IsType<ApiResponse<TradeDocGenerateResult>>(ok.Value).Code);
        Assert.NotEmpty(db.TradeDocuments);
        Assert.All(db.TradeDocuments.ToList(), d => Assert.Equal(own.Id, d.CustomerId));
    }

    [Fact]
    public async Task 销售订单预填_本人成功_越界拒绝且不泄露客户()
    {
        using var db = TestDbFactory.Create();
        var op = await SeedOperatorAsync(db);
        var own = SeedCustomer(db, "DOC-Q-OWN", op.Employee.Id);
        var ownOrder = SeedOrder(db, "SO-DOC-Q-OWN", own.Id);
        var foreign = SeedCustomer(db, "DOC-Q-FOREIGN-SECRET");
        var foreignOrder = SeedOrder(db, "SO-DOC-Q-FOREIGN", foreign.Id);

        var ok = Assert.IsType<OkObjectResult>(await SalesOrderCtl(db, op.User.Id)
            .TradeDocumentPrefill(ownOrder.Id));
        Assert.Equal(ErrorCodes.Success, Assert.IsType<ApiResponse<TradeDocPrefillResult>>(ok.Value).Code);

        var ex = await CodeOfAsync(ErrorCodes.Forbidden,
            () => SalesOrderCtl(db, op.User.Id).TradeDocumentPrefill(foreignOrder.Id));
        Assert.DoesNotContain("DOC-Q-FOREIGN-SECRET", ex.Message);
        Assert.Empty(db.TradeDocuments);        // 预填不落库
    }

    [Fact]
    public async Task 装柜清单生成与预填_越界拒绝_本人成功()
    {
        using var db = TestDbFactory.Create();
        var op = await SeedOperatorAsync(db);
        var own = SeedCustomer(db, "DOC-R-OWN", op.Employee.Id);
        var foreignCustomer = SeedCustomer(db, "DOC-R-FOREIGN");
        var ownList = SeedLoadingList(db, "LL-DOC-R-OWN", own.Id);
        var foreignList = SeedLoadingList(db, "LL-DOC-R-FOREIGN", foreignCustomer.Id);

        await CodeOfAsync(ErrorCodes.Forbidden,
            () => LoadingCtl(db, op.User.Id).TradeDocumentPrefill(foreignList.Id));
        await CodeOfAsync(ErrorCodes.Forbidden,
            () => LoadingCtl(db, op.User.Id).GenerateTradeDocuments(foreignList.Id, null));
        Assert.Empty(db.TradeDocuments);

        var prefill = Assert.IsType<OkObjectResult>(
            await LoadingCtl(db, op.User.Id).TradeDocumentPrefill(ownList.Id));
        Assert.Equal(ErrorCodes.Success, Assert.IsType<ApiResponse<TradeDocPrefillResult>>(prefill.Value).Code);

        var generated = Assert.IsType<OkObjectResult>(
            await LoadingCtl(db, op.User.Id).GenerateTradeDocuments(ownList.Id, null));
        Assert.Equal(ErrorCodes.Success, Assert.IsType<ApiResponse<TradeDocGenerateResult>>(generated.Value).Code);
        Assert.All(db.TradeDocuments.ToList(), d => Assert.Equal(own.Id, d.CustomerId));
    }

    [Fact]
    public async Task 四个生成入口_缺失身份或未绑定请求上下文_按未认证拒绝且零落库()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "DOC-NOIDENT");
        var order = SeedOrder(db, "SO-DOC-NOIDENT", customer.Id);
        var list = SeedLoadingList(db, "LL-DOC-NOIDENT", customer.Id);

        Assert.Equal(ErrorCodes.Unauthorized, (await CaptureAsync(
            () => SalesOrderCtl(db, null).TradeDocumentPrefill(order.Id)))?.Code);
        Assert.Equal(ErrorCodes.Unauthorized, (await CaptureAsync(
            () => SalesOrderCtl(db, null).GenerateTradeDocuments(order.Id, null)))?.Code);
        Assert.Equal(ErrorCodes.Unauthorized, (await CaptureAsync(
            () => LoadingCtl(db, null).TradeDocumentPrefill(list.Id)))?.Code);
        Assert.Equal(ErrorCodes.Unauthorized, (await CaptureAsync(
            () => LoadingCtl(db, null).GenerateTradeDocuments(list.Id, null)))?.Code);

        // 完全未绑定 HttpContext 的纯进程内直调同样按未认证拒绝（没有请求上下文即没有身份）。
        var sales = new SalesOrderController(db, new DocumentNumberService(db));
        var loading = new ContainerLoadingListController(db, new DocumentNumberService(db));
        Assert.Equal(ErrorCodes.Unauthorized, (await CaptureAsync(
            () => sales.GenerateTradeDocuments(order.Id, null)))?.Code);
        Assert.Equal(ErrorCodes.Unauthorized, (await CaptureAsync(
            () => loading.GenerateTradeDocuments(list.Id, null)))?.Code);

        Assert.Empty(db.TradeDocuments);
        Assert.Empty(db.TradeDocumentItems);
        Assert.Empty(db.SysDocumentNumberRules);
    }

    [Fact]
    public void 生成目标守卫_受限账号无权威归属_拒绝_特权与既有派生调用保持口径()
    {
        var target = new TradeDocument { DocNo = "DOC-S-TARGET", DocType = "商业发票" };
        var ex = Assert.Throws<BusinessException>(
            () => TradeDocumentGeneration.EnsureGeneratedTargetAuthorized(RestrictedScope(1, 10), target));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);

        TradeDocumentGeneration.EnsureGeneratedTargetAuthorized(PrivilegedScope(), target);
        TradeDocumentGeneration.EnsureGeneratedTargetAuthorized(null, target);
    }

    // ==================== 6. 接线契约（完整覆盖 + 移除请求管线强制门） ====================

    [Fact]
    public void 控制器_完整覆盖基类CRUD_且不再应用请求管线过滤器()
    {
        var type = typeof(TradeDocumentController);
        foreach (var name in new[] { "GetPaged", "GetAll", "GetById", "Create", "Update", "Delete", "BatchDelete" })
        {
            var method = type.GetMethod(name, BindingFlags.Public | BindingFlags.Instance);
            Assert.NotNull(method);
            Assert.Equal(type, method!.DeclaringType);   // 必须由 TradeDocumentController 自身覆盖，而非继承基类
        }

        // ERP-497：请求管线过滤器（及其 HttpContext.Items 范围通道）不再应用，授权改为每个动作内无条件解析。
        Assert.DoesNotContain(type.GetCustomAttributes(inherit: true),
            a => a.GetType().Name == nameof(TradeDocumentRequestAuthorizationFilter));
    }

    [Fact]
    public void 控制器源码_每个路由动作都无条件解析实时授权()
    {
        var source = File.ReadAllText(RepoFile("src", "ERP.Api", "Controllers", "TradeDocumentController.cs"));

        Assert.Equal(13, CountOccurrences(source, "await EnsureTradeDocumentAuthorizedAsync();"));
        Assert.Contains("TradeDocumentAuthorizationRules.EnsureMenuAuthorizedAsync(_db, CurrentUserId())", source);
        Assert.DoesNotContain(".ScopeFrom(", source);            // 范围读取通道已移除
        Assert.DoesNotContain("Items[", source);                 // 不再经请求上下文缓存范围
        Assert.DoesNotContain("IAsyncActionFilter", source);     // 不再实现任何动作过滤器
    }

    [Fact]
    public void 生成入口源码与方法_四个路由都无条件解析实时授权且不再应用过滤器()
    {
        foreach (var (path, count) in new[]
                 {
                     ("SalesOrderController.cs", 2),
                     ("ContainerLoadingListController.cs", 2)
                 })
        {
            var source = File.ReadAllText(RepoFile("src", "ERP.Api", "Controllers", path));
            Assert.Equal(count, CountOccurrences(
                source, "await TradeDocumentAuthorizationRules.EnsureMenuAuthorizedAsync(Db, CurrentUserId());"));
            Assert.DoesNotContain(".ScopeFrom(", source);
            Assert.DoesNotContain("TradeDocumentRequestAuthorizationFilter", source);
        }

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
            Assert.DoesNotContain(method!.GetCustomAttributes(inherit: true),
                a => a.GetType().Name == nameof(TradeDocumentRequestAuthorizationFilter));
        }
    }

    private static int CountOccurrences(string text, string token)
        => text.Split(token, StringSplitOptions.None).Length - 1;

    /// <summary>仓库根相对路径的文件（沿测试程序集输出目录上溯到仓库根，与既有契约测试同一约定）。</summary>
    private static string RepoFile(params string[] parts)
        => Path.GetFullPath(Path.Combine(
            new[] { AppContext.BaseDirectory, "..", "..", "..", "..", ".." }.Concat(parts).ToArray()));
}
