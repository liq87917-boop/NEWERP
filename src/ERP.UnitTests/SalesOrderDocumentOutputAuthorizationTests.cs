using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-424 规范销售订单（<c>api/sales-orders</c>）**文档输出入口**（打印 / JSON 单据导出 / Excel 导出）
/// 实时授权与权威客户范围护栏单元测试。
/// <para>覆盖：受限业务员（既有「销售订单」功能菜单 + 既有「销售订单导出」导出菜单 + 客户数据范围）在本人 /
/// 他人 / 无主（CustomerId 缺省）/ 已删除 / 不存在订单上的三个输出入口；无身份 / 畸形身份 / 已删除 / 已禁用 /
/// 无菜单 / 仅导出菜单 / 仅功能菜单 / 已撤销菜单一律 fail closed；特权账号保留既有全量口径；
/// JSON 导出只返回范围内父订单，Excel 导出解码真实工作簿核对越界订单号 / 金额绝不出现，允许打印保留未删除明细；
/// 直接控制器调用（无请求路径）同样无条件授权；拒绝与允许前后零写入。</para>
/// <para>全部使用内存数据库（<see cref="TestDbFactory"/>），不连接 SQL Server、不启动 API、不执行真实 SQL / seed。</para>
/// </summary>
public class SalesOrderDocumentOutputAuthorizationTests
{
    private static readonly DateTime AsOf = new(2026, 9, 20);

    // ==================== 0. 测试脚手架 ====================

    private static DefaultHttpContext HttpFor(long? userId, string? path = "/api/sales-orders")
    {
        var http = new DefaultHttpContext();
        if (path is not null) http.Request.Path = path;
        http.User = userId.HasValue
            ? new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }, "Test"))
            : new ClaimsPrincipal(new ClaimsIdentity());
        return http;
    }

    private static SalesOrderController NewController(ErpDbContext db, long? userId, string? path = "/api/sales-orders")
    {
        var controller = new SalesOrderController(db, new DocumentNumberService(db));
        controller.ControllerContext = new ControllerContext { HttpContext = HttpFor(userId, path) };
        return controller;
    }

    /// <summary>绑定原始 <c>NameIdentifier</c> 文本（覆盖畸形 / 非正身份）。</summary>
    private static SalesOrderController NewControllerWithRawClaim(ErpDbContext db, string? raw, string? path = "/api/sales-orders")
    {
        var http = new DefaultHttpContext();
        if (path is not null) http.Request.Path = path;
        http.User = raw is null
            ? new ClaimsPrincipal(new ClaimsIdentity())
            : new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.NameIdentifier, raw) }, "Test"));
        var controller = new SalesOrderController(db, new DocumentNumberService(db));
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        return controller;
    }

    /// <summary>
    /// 播种受限业务员（登录账号 == 员工编码，ERP-097 权威映射）：默认同时具备既有「销售订单」功能菜单与
    /// 既有「销售订单导出」导出菜单。
    /// </summary>
    private static (long UserId, long EmployeeId, SysRole Role) SeedOperator(ErpDbContext db,
        bool functionalMenu = true, bool exportMenu = true, UserStatus status = UserStatus.Enabled)
    {
        var code = $"erp424-{Guid.NewGuid():N}";
        var employee = new BaseEmployee
        {
            EmployeeCode = code, EmployeeName = code, IsSalesman = true, Status = 1
        };
        db.BaseEmployees.Add(employee);
        db.SaveChanges();

        var user = new SysUser
        {
            UserName = code, PasswordHash = "hash", PasswordSalt = "salt", DisplayName = code, Status = status
        };
        db.SysUsers.Add(user);
        db.SaveChanges();

        var role = new SysRole { RoleName = code, RoleCode = code, IsSystem = false };
        db.SysRoles.Add(role);
        db.SaveChanges();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        if (functionalMenu) GrantMenu(db, role.Id, SalesOrderDocumentOutputAuthorizationRules.FunctionalMenuCode);
        if (exportMenu) GrantMenu(db, role.Id, SalesOrderDocumentOutputAuthorizationRules.ExportMenuCode);
        db.SaveChanges();
        return (user.Id, employee.Id, role);
    }

    private static SysMenu GrantMenu(ErpDbContext db, long roleId, string menuCode)
    {
        var menu = db.SysMenus.FirstOrDefault(m => m.MenuCode == menuCode && !m.IsDeleted);
        if (menu is null)
        {
            menu = new SysMenu { MenuName = menuCode, MenuCode = menuCode, MenuType = MenuType.Menu };
            db.SysMenus.Add(menu);
            db.SaveChanges();
        }

        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = roleId, MenuId = menu.Id });
        return menu;
    }

    private static BaseCustomer SeedCustomer(ErpDbContext db, string code, long? empId)
    {
        var customer = new BaseCustomer
        {
            CustomerCode = code, CustomerName = code, Status = 1, CreditStatus = "正常", EmpId = empId
        };
        db.BaseCustomers.Add(customer);
        db.SaveChanges();
        return customer;
    }

    /// <summary>播种订单（可选已删除、可选附带一行已删除明细）。</summary>
    private static SalesOrder SeedOrder(ErpDbContext db, string orderNo, long customerId, decimal totalAmount,
        bool deleted = false, bool withDeletedDetail = false)
    {
        var order = new SalesOrder
        {
            OrderNo = orderNo,
            OrderDate = AsOf.AddDays(-10),
            CustomerId = customerId,
            Currency = Currency.USD,
            ExchangeRate = 7.1m,
            TotalAmount = totalAmount,
            DepositRatio = 30m,
            DepositAmount = decimal.Round(totalAmount * 0.3m, 2),
            Status = DocumentStatus.Approved,
            IsDeleted = deleted
        };
        db.SalesOrders.Add(order);
        db.SaveChanges();

        db.SalesOrderDetails.Add(new SalesOrderDetail
        {
            SalesOrderId = order.Id, ProductId = 1, ProductName = $"{orderNo}-行1", Spec = "大", Unit = "PCS",
            Quantity = 10m, UnitPrice = totalAmount / 10m, Amount = totalAmount
        });
        db.SaveChanges();

        if (withDeletedDetail)
        {
            var hidden = new SalesOrderDetail
            {
                SalesOrderId = order.Id, ProductId = 2, ProductName = $"{orderNo}-已删除行", Unit = "PCS",
                Quantity = 1m, UnitPrice = 1m, Amount = 1m
            };
            db.SalesOrderDetails.Add(hidden);
            db.SaveChanges();
            hidden.IsDeleted = true;
            db.SaveChanges();
        }

        return order;
    }

    private static async Task<T> OkDataAsync<T>(Func<Task<IActionResult>> action)
        => Assert.IsType<ApiResponse<T>>(Assert.IsType<OkObjectResult>(await action()).Value).Data!;

    /// <summary>断言拒绝：指定错误码 + 错误文案（不返回任何订单 / 明细 / 导出字节）。</summary>
    private static async Task AssertDeniedAsync(int code, string text, Func<Task<IActionResult>> action)
    {
        var ex = await Assert.ThrowsAsync<BusinessException>(action);
        Assert.Equal(code, ex.Code);
        Assert.Equal(text, ex.Message);
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

    private static Dictionary<string, int> Snapshot(ErpDbContext db) => new(StringComparer.Ordinal)
    {
        ["SalesOrders"] = db.SalesOrders.Count(),
        ["SalesOrderDetails"] = db.SalesOrderDetails.Count(),
        ["BaseCustomers"] = db.BaseCustomers.Count(),
        ["SysMenus"] = db.SysMenus.Count(),
        ["SysRoleMenus"] = db.SysRoleMenus.Count(),
    };

    private static void AssertUnchanged(Dictionary<string, int> before, ErpDbContext db)
    {
        var after = Snapshot(db);
        foreach (var (table, count) in before) Assert.Equal(count, after[table]);
    }

    // ==================== 1. 复用既有受控导出族菜单口径 ====================

    [Fact]
    public void 护栏菜单口径_与既有受控导出族目录逐字一致()
    {
        Assert.Equal("sales-order", SalesOrderDocumentOutputAuthorizationRules.FunctionalMenuCode);
        Assert.Equal("sales-order-export", SalesOrderDocumentOutputAuthorizationRules.ExportMenuCode);
        Assert.Equal("销售订单", SalesOrderDocumentOutputAuthorizationRules.FunctionalMenuText);
        Assert.Equal("销售订单导出", SalesOrderDocumentOutputAuthorizationRules.ExportMenuText);

        // 直接复用既有受控导出族目录的 sales-order 族菜单授权（功能菜单 + 导出菜单），不新增任何菜单。
        var required = LegacyBillExportCatalog.Resolve("sales-order").RequiredMenuCodes;
        Assert.Equal(
            new[] { SalesOrderDocumentOutputAuthorizationRules.FunctionalMenuCode,
                    SalesOrderDocumentOutputAuthorizationRules.ExportMenuCode },
            required);
    }

    // ==================== 2. 受限业务员：本人订单三个输出入口可读（打印保留未删除明细） ====================

    [Fact]
    public async Task 受限业务员_本人订单_三个文档输出入口可读且打印保留未删除明细()
    {
        using var db = TestDbFactory.Create();
        var (userId, employeeId, _) = SeedOperator(db);
        var own = SeedCustomer(db, "C424-OWN", employeeId);
        var order = SeedOrder(db, "SO424-OWN", own.Id, 1234.56m, withDeletedDetail: true);
        var before = Snapshot(db);

        var ctl = NewController(db, userId);

        var print = await OkDataAsync<SalesOrder>(() => ctl.GetPrint(order.Id));
        Assert.Equal(order.Id, print.Id);
        Assert.Equal(own.Id, print.CustomerId);
        Assert.Equal(1234.56m, print.TotalAmount);
        Assert.Equal(Currency.USD, print.Currency);
        Assert.Equal("PCS", Assert.Single(print.Details).Unit);
        Assert.DoesNotContain(print.Details, d => d.ProductName.Contains("已删除行", StringComparison.Ordinal));

        var exported = await OkDataAsync<List<SalesOrder>>(() => ctl.Export(null, null));
        var only = Assert.Single(exported);
        Assert.Equal(order.Id, only.Id);
        Assert.Equal("PCS", Assert.Single(only.Details).Unit);

        var excel = Assert.IsType<FileContentResult>(await ctl.ExportExcel(null, null, null, null));
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", excel.ContentType);
        Assert.EndsWith(".xlsx", excel.FileDownloadName);
        Assert.Equal("PK", System.Text.Encoding.ASCII.GetString(excel.FileContents, 0, 2));

        AssertUnchanged(before, db);
    }

    // ==================== 3. 受限业务员：他人 / 已删除 / 不存在订单统一非披露错误 ====================

    [Fact]
    public async Task 受限业务员_他人已删除不存在订单_打印统一非披露错误()
    {
        using var db = TestDbFactory.Create();
        var (userId, employeeId, _) = SeedOperator(db);
        var own = SeedCustomer(db, "C424-A", employeeId);
        var foreign = SeedCustomer(db, "C424-B", null);
        var ownOrder = SeedOrder(db, "SO424-A", own.Id, 100m);
        var foreignOrder = SeedOrder(db, "SO424-B", foreign.Id, 9999.99m);
        var deletedOrder = SeedOrder(db, "SO424-D", own.Id, 300m, deleted: true);
        var before = Snapshot(db);

        var ctl = NewController(db, userId);

        // 本人放行（对照），他人 / 已删除 / 不存在 / 非正 Id 返回同一非披露错误。
        Assert.Equal(ownOrder.Id, (await OkDataAsync<SalesOrder>(() => ctl.GetPrint(ownOrder.Id))).Id);
        await AssertDeniedAsync(ErrorCodes.NotFound, SalesOrderDocumentOutputAuthorizationRules.NotFoundText,
            () => ctl.GetPrint(foreignOrder.Id));
        await AssertDeniedAsync(ErrorCodes.NotFound, SalesOrderDocumentOutputAuthorizationRules.NotFoundText,
            () => ctl.GetPrint(deletedOrder.Id));
        await AssertDeniedAsync(ErrorCodes.NotFound, SalesOrderDocumentOutputAuthorizationRules.NotFoundText,
            () => ctl.GetPrint(9_424_000L));
        await AssertDeniedAsync(ErrorCodes.NotFound, SalesOrderDocumentOutputAuthorizationRules.NotFoundText,
            () => ctl.GetPrint(0));

        AssertUnchanged(before, db);
    }


    // ==================== 4. 受限业务员：无主（CustomerId 缺省）订单 fail closed ====================

    [Fact]
    public async Task 受限业务员_无主客户订单_打印fail_closed()
    {
        using var db = TestDbFactory.Create();
        var (userId, _, _) = SeedOperator(db);
        var unlinked = SeedOrder(db, "SO424-UNLINKED", 0L, 55555.55m);
        var ctl = NewController(db, userId);

        await AssertDeniedAsync(ErrorCodes.NotFound, SalesOrderDocumentOutputAuthorizationRules.NotFoundText,
            () => ctl.GetPrint(unlinked.Id));
    }

    // ==================== 5. 受限业务员：JSON / Excel 导出仅含范围内父订单 ====================

    [Fact]
    public async Task 受限业务员_JSON与Excel导出_仅含范围内订单且越界订单号金额绝不出现()
    {
        using var db = TestDbFactory.Create();
        var (userId, employeeId, _) = SeedOperator(db);
        var own = SeedCustomer(db, "C424-EXP-OWN", employeeId);
        var foreign = SeedCustomer(db, "C424-EXP-FOREIGN", null);
        var ownOrder = SeedOrder(db, "SO424-EXP-OWN", own.Id, 1234.56m);
        SeedOrder(db, "SO424-EXP-FOREIGN", foreign.Id, 98765.43m);
        SeedOrder(db, "SO424-EXP-NULLOWNER", 0L, 55555.55m);
        var before = Snapshot(db);

        var ctl = NewController(db, userId);

        var exported = await OkDataAsync<List<SalesOrder>>(() => ctl.Export(null, null));
        var only = Assert.Single(exported);
        Assert.Equal(ownOrder.Id, only.Id);
        Assert.Equal(own.Id, only.CustomerId);

        var file = Assert.IsType<FileContentResult>(await ctl.ExportExcel(null, null, null, null));
        var orderNos = ReadExcelColumn(file.FileContents, "订单号");
        Assert.Contains("SO424-EXP-OWN", orderNos);
        Assert.DoesNotContain("SO424-EXP-FOREIGN", orderNos);
        Assert.DoesNotContain("SO424-EXP-NULLOWNER", orderNos);

        var amounts = ReadExcelColumn(file.FileContents, "订单总额");
        Assert.Single(amounts);
        Assert.Equal("1234.56", amounts[0]);
        Assert.DoesNotContain("98765.43", amounts);
        Assert.DoesNotContain("55555.55", amounts);

        AssertUnchanged(before, db);
    }

    // ==================== 6. 特权账号：保留既有全量口径（含他人与无主订单） ====================

    [Fact]
    public async Task 特权账号_保留既有全量口径_可读他人与无主订单()
    {
        using var db = TestDbFactory.Create();
        var foreign = SeedCustomer(db, "C424-P-FOREIGN", null);
        var foreignOrder = SeedOrder(db, "SO424-P-FOREIGN", foreign.Id, 200m);
        var unlinked = SeedOrder(db, "SO424-P-NULLOWNER", 0L, 300m);
        var privilegedUserId = TestAuth.SeedPrivilegedUser(db);

        var ctl = NewController(db, privilegedUserId);

        Assert.Equal(foreignOrder.Id, (await OkDataAsync<SalesOrder>(() => ctl.GetPrint(foreignOrder.Id))).Id);
        Assert.Equal(unlinked.Id, (await OkDataAsync<SalesOrder>(() => ctl.GetPrint(unlinked.Id))).Id);

        var exported = await OkDataAsync<List<SalesOrder>>(() => ctl.Export(null, null));
        Assert.Contains(exported, o => o.Id == foreignOrder.Id);
        Assert.Contains(exported, o => o.Id == unlinked.Id);

        var file = Assert.IsType<FileContentResult>(await ctl.ExportExcel(null, null, null, null));
        var orderNos = ReadExcelColumn(file.FileContents, "订单号");
        Assert.Contains("SO424-P-FOREIGN", orderNos);
        Assert.Contains("SO424-P-NULLOWNER", orderNos);
    }

    // ==================== 7. 身份 / 菜单拒绝矩阵：三个输出入口一律 fail closed 且零写入 ====================

    [Fact]
    public async Task 身份与菜单拒绝矩阵_三个输出入口fail_closed且零写入()
    {
        using var db = TestDbFactory.Create();
        var (ownerUserId, employeeId, _) = SeedOperator(db);
        var own = SeedCustomer(db, "C424-MATRIX", employeeId);
        var ownOrder = SeedOrder(db, "SO424-MATRIX", own.Id, 100m);

        var (deletedUserId, _, _) = SeedOperator(db, status: UserStatus.Enabled);
        db.SysUsers.Single(u => u.Id == deletedUserId).IsDeleted = true;
        db.SaveChanges();

        var (disabledUserId, _, _) = SeedOperator(db, status: UserStatus.Disabled);
        var (noMenuUserId, _, _) = SeedOperator(db, functionalMenu: false, exportMenu: false);
        var (exportOnlyUserId, _, _) = SeedOperator(db, functionalMenu: false, exportMenu: true);
        var (functionalOnlyUserId, _, _) = SeedOperator(db, functionalMenu: true, exportMenu: false);

        // 已撤销：先授予两菜单，再回收该角色的全部菜单授权（角色仍在）。
        var (revokedUserId, _, revokedRole) = SeedOperator(db);
        db.SysRoleMenus.RemoveRange(db.SysRoleMenus.Where(rm => rm.RoleId == revokedRole.Id));
        db.SaveChanges();

        var before = Snapshot(db);

        (long? UserId, int Code, string Text)[] cases =
        {
            (null, ErrorCodes.Unauthorized, SalesOrderDocumentOutputAuthorizationRules.UnauthorizedText),
            (deletedUserId, ErrorCodes.Unauthorized, SalesOrderDocumentOutputAuthorizationRules.UserDeletedText),
            (disabledUserId, ErrorCodes.Forbidden, SalesOrderDocumentOutputAuthorizationRules.UserDisabledText),
            (noMenuUserId, ErrorCodes.Forbidden, SalesOrderDocumentOutputAuthorizationRules.FunctionalMenuDeniedText),
            (exportOnlyUserId, ErrorCodes.Forbidden, SalesOrderDocumentOutputAuthorizationRules.FunctionalMenuDeniedText),
            (functionalOnlyUserId, ErrorCodes.Forbidden, SalesOrderDocumentOutputAuthorizationRules.ExportMenuDeniedText),
            (revokedUserId, ErrorCodes.Forbidden, SalesOrderDocumentOutputAuthorizationRules.FunctionalMenuDeniedText),
        };

        foreach (var (userId, code, text) in cases)
        {
            var ctl = NewController(db, userId);
            await AssertDeniedAsync(code, text, () => ctl.GetPrint(ownOrder.Id));
            await AssertDeniedAsync(code, text, () => ctl.Export(null, null));
            await AssertDeniedAsync(code, text, () => ctl.ExportExcel(null, null, null, null));
        }

        // 所有者本人仍可读（对照），且全部拒绝 / 读取前后零写入。
        Assert.Equal(ownOrder.Id, (await OkDataAsync<SalesOrder>(
            () => NewController(db, ownerUserId).GetPrint(ownOrder.Id))).Id);
        AssertUnchanged(before, db);
    }

    // ==================== 8. 畸形 / 非正身份：三个输出入口按未认证拒绝 ====================

    [Fact]
    public async Task 畸形与非正身份_三个输出入口按未认证拒绝()
    {
        using var db = TestDbFactory.Create();
        var (_, employeeId, _) = SeedOperator(db);
        var own = SeedCustomer(db, "C424-MALFORMED", employeeId);
        var ownOrder = SeedOrder(db, "SO424-MALFORMED", own.Id, 100m);

        foreach (var raw in new[] { "abc", "-5", "0", " ", null })
        {
            var ctl = NewControllerWithRawClaim(db, raw);
            await AssertDeniedAsync(ErrorCodes.Unauthorized, SalesOrderDocumentOutputAuthorizationRules.UnauthorizedText,
                () => ctl.GetPrint(ownOrder.Id));
            await AssertDeniedAsync(ErrorCodes.Unauthorized, SalesOrderDocumentOutputAuthorizationRules.UnauthorizedText,
                () => ctl.Export(null, null));
            await AssertDeniedAsync(ErrorCodes.Unauthorized, SalesOrderDocumentOutputAuthorizationRules.UnauthorizedText,
                () => ctl.ExportExcel(null, null, null, null));
        }
    }

    // ==================== 9. 直接控制器调用（无请求路径）同样无条件授权 ====================

    [Fact]
    public async Task 直接控制器调用_无请求路径_仍无条件授权fail_closed()
    {
        using var db = TestDbFactory.Create();
        var (_, employeeId, _) = SeedOperator(db);
        var own = SeedCustomer(db, "C424-NOPATH", employeeId);
        var ownOrder = SeedOrder(db, "SO424-NOPATH", own.Id, 100m);
        var (noMenuUserId, _, _) = SeedOperator(db, functionalMenu: false, exportMenu: false);

        // 无请求路径 + 无身份：绝不豁免（未认证）。
        var anonymous = NewController(db, null, path: null);
        await AssertDeniedAsync(ErrorCodes.Unauthorized, SalesOrderDocumentOutputAuthorizationRules.UnauthorizedText,
            () => anonymous.GetPrint(ownOrder.Id));
        await AssertDeniedAsync(ErrorCodes.Unauthorized, SalesOrderDocumentOutputAuthorizationRules.UnauthorizedText,
            () => anonymous.Export(null, null));
        await AssertDeniedAsync(ErrorCodes.Unauthorized, SalesOrderDocumentOutputAuthorizationRules.UnauthorizedText,
            () => anonymous.ExportExcel(null, null, null, null));

        // 无请求路径 + 已认证但缺菜单：仍然 fail closed（无 Request.Path 身份豁免）。
        var noMenu = NewController(db, noMenuUserId, path: null);
        await AssertDeniedAsync(ErrorCodes.Forbidden, SalesOrderDocumentOutputAuthorizationRules.FunctionalMenuDeniedText,
            () => noMenu.GetPrint(ownOrder.Id));
        await AssertDeniedAsync(ErrorCodes.Forbidden, SalesOrderDocumentOutputAuthorizationRules.FunctionalMenuDeniedText,
            () => noMenu.Export(null, null));
        await AssertDeniedAsync(ErrorCodes.Forbidden, SalesOrderDocumentOutputAuthorizationRules.FunctionalMenuDeniedText,
            () => noMenu.ExportExcel(null, null, null, null));
    }

    // ==================== 10. 撤销授权后下一次请求立即收敛 ====================

    [Fact]
    public async Task 撤销授权后下一次请求立即收敛_三个输出入口Forbidden()
    {
        using var db = TestDbFactory.Create();
        var (userId, employeeId, role) = SeedOperator(db);
        var own = SeedCustomer(db, "C424-REVOKE", employeeId);
        var ownOrder = SeedOrder(db, "SO424-REVOKE", own.Id, 100m);

        // 授权期间三个入口可读。
        Assert.Equal(ownOrder.Id, (await OkDataAsync<SalesOrder>(
            () => NewController(db, userId).GetPrint(ownOrder.Id))).Id);
        Assert.Single(await OkDataAsync<List<SalesOrder>>(
            () => NewController(db, userId).Export(null, null)));

        // 回收全部菜单授权（角色仍在）→ 下一次请求立即收敛（不缓存授权）。
        db.SysRoleMenus.RemoveRange(db.SysRoleMenus.Where(rm => rm.RoleId == role.Id));
        db.SaveChanges();

        var ctl = NewController(db, userId);
        await AssertDeniedAsync(ErrorCodes.Forbidden, SalesOrderDocumentOutputAuthorizationRules.FunctionalMenuDeniedText,
            () => ctl.GetPrint(ownOrder.Id));
        await AssertDeniedAsync(ErrorCodes.Forbidden, SalesOrderDocumentOutputAuthorizationRules.FunctionalMenuDeniedText,
            () => ctl.Export(null, null));
        await AssertDeniedAsync(ErrorCodes.Forbidden, SalesOrderDocumentOutputAuthorizationRules.FunctionalMenuDeniedText,
            () => ctl.ExportExcel(null, null, null, null));
    }

    }
