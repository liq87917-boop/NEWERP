using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using ERP.Infrastructure.Reports;
using Microsoft.AspNetCore.Mvc;
using NPOI.SS.UserModel;
using NPOI.XSSF.UserModel;
using System.Reflection;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-115 动态销售订单报表 Excel 导出（只读、有界）单元测试。
/// 覆盖：选定列顺序与行值、公式前导文本转义（保持字面、非公式单元格）、分组页面小计的币种分开、
/// 无身份 / 无菜单授权 / 越界业务员 / 页大小超限拒绝、空页仅表头、以及只读不写库。
/// <para>全部使用内存数据库（TestDbFactory），不连接 SQL Server、不启动 API、不执行任何 SQL / seed。</para>
/// </summary>
public class DynamicSalesOrderExcelTests
{
    // ==================== 0. 测试脚手架 ====================

    private static SysUser SeedUser(ErpDbContext db, string userName)
    {
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
        return user;
    }

    private static SysRole SeedRole(ErpDbContext db, string code, bool isSystem = false)
    {
        var role = new SysRole { RoleName = code, RoleCode = code, IsSystem = isSystem };
        db.SysRoles.Add(role);
        db.SaveChanges();
        return role;
    }

    private static void SeedUserRole(ErpDbContext db, long userId, long roleId)
    {
        db.SysUserRoles.Add(new SysUserRole { UserId = userId, RoleId = roleId });
        db.SaveChanges();
    }

    private static SysMenu SeedMenu(ErpDbContext db, string code)
    {
        var menu = new SysMenu { MenuName = code, MenuCode = code, MenuType = MenuType.Menu };
        db.SysMenus.Add(menu);
        db.SaveChanges();
        return menu;
    }

    private static void SeedRoleMenu(ErpDbContext db, long roleId, long menuId)
    {
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = roleId, MenuId = menuId });
        db.SaveChanges();
    }

    private static BaseEmployee SeedEmployee(ErpDbContext db, string code)
    {
        var employee = new BaseEmployee { EmployeeCode = code, EmployeeName = code, IsSalesman = true, Status = 1 };
        db.BaseEmployees.Add(employee);
        db.SaveChanges();
        return employee;
    }

    private static BaseCustomer SeedCustomer(ErpDbContext db, string code, string name, long? empId = null)
    {
        var customer = new BaseCustomer
        {
            CustomerCode = code,
            CustomerName = name,
            Status = 1,
            CreditStatus = "正常",
            EmpId = empId
        };
        db.BaseCustomers.Add(customer);
        db.SaveChanges();
        return customer;
    }

    private static SalesOrder SeedOrder(ErpDbContext db, string orderNo, long customerId,
        DateTime? orderDate = null, DocumentStatus status = DocumentStatus.Pending,
        Currency currency = Currency.USD, decimal totalAmount = 100m)
    {
        var order = new SalesOrder
        {
            OrderNo = orderNo,
            CustomerId = customerId,
            OrderDate = orderDate ?? new DateTime(2026, 9, 1),
            Status = status,
            Currency = currency,
            TotalAmount = totalAmount
        };
        db.SalesOrders.Add(order);
        db.SaveChanges();
        return order;
    }

    private static long SeedPrivilegedUser(ErpDbContext db, string userName = "priv")
    {
        var user = SeedUser(db, userName);
        var role = SeedRole(db, $"Role-{userName}", isSystem: true);
        SeedUserRole(db, user.Id, role.Id);
        SeedRoleMenu(db, role.Id, SeedMenu(db, "sales-order").Id);
        return user.Id;
    }

    private static (long UserId, long MineCustomerId, long OtherCustomerId) SeedRestrictedUser(
        ErpDbContext db, string userName)
    {
        var user = SeedUser(db, userName);
        var role = SeedRole(db, $"Role-{userName}", isSystem: false);
        SeedUserRole(db, user.Id, role.Id);
        SeedRoleMenu(db, role.Id, SeedMenu(db, "sales-order").Id);
        var emp = SeedEmployee(db, userName);
        var mine = SeedCustomer(db, $"C-{userName}-mine", "我的客户", emp.Id);
        var other = SeedCustomer(db, $"C-{userName}-other", "别人的客户", emp.Id + 1000);
        return (user.Id, mine.Id, other.Id);
    }

    private static DynamicSalesOrderReportController NewController(IErpDbContext db)
        => new(new DynamicSalesOrderReportQuery(db));

    private static FileContentResult ExportOk(IActionResult result)
    {
        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", file.ContentType);
        Assert.EndsWith(".xlsx", file.FileDownloadName);
        return file;
    }

    private static XSSFWorkbook OpenWorkbook(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        return new XSSFWorkbook(ms);
    }

    // ==================== 1. 列顺序与行值 ====================

    [Fact]
    public async Task Export_导出当前页_列顺序与行值符合选定字段()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        var c1 = SeedCustomer(db, "C1", "客户一").Id;
        SeedOrder(db, "SO-1", c1, totalAmount: 100m);
        SeedOrder(db, "SO-2", c1, totalAmount: 200m);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var file = ExportOk(await ctl.Export(new DynamicSalesOrderReportRequest
        {
            Fields = new() { "orderNo", "totalAmount" },
            Page = 1,
            PageSize = 10
        }));

        var sheet = OpenWorkbook(file.FileContents).GetSheetAt(0);
        Assert.Equal("订单号", sheet.GetRow(0).GetCell(0).StringCellValue);
        Assert.Equal("订单总额", sheet.GetRow(0).GetCell(1).StringCellValue);
        Assert.Equal(2, sheet.LastRowNum);
        Assert.Equal("SO-1", sheet.GetRow(1).GetCell(0).StringCellValue);
        Assert.Equal("SO-2", sheet.GetRow(2).GetCell(0).StringCellValue);
        Assert.Equal(100d, sheet.GetRow(1).GetCell(1).NumericCellValue);
        Assert.Equal(200d, sheet.GetRow(2).GetCell(1).NumericCellValue);
    }

    // ==================== 2. 公式注入防护 ====================

    [Fact]
    public async Task Export_公式前导文本_转义为字面文本_非公式单元格()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        var c1 = SeedCustomer(db, "C1", "客户一").Id;
        SeedOrder(db, "=1+1", c1);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var file = ExportOk(await ctl.Export(new DynamicSalesOrderReportRequest
        {
            Fields = new() { "orderNo" },
            PageSize = 10
        }));

        var sheet = OpenWorkbook(file.FileContents).GetSheetAt(0);
        var cell = sheet.GetRow(1).GetCell(0);
        Assert.NotEqual(CellType.Formula, cell.CellType);
        Assert.Equal(CellType.String, cell.CellType);
        Assert.Equal("'=1+1", cell.StringCellValue);
    }

    [Fact]
    public void EscapeFormulaLeading_危险字符转义_普通值原样()
    {
        Assert.False(DynamicSalesOrderReportRules.IsFormulaLeading(null));
        Assert.False(DynamicSalesOrderReportRules.IsFormulaLeading(""));
        Assert.False(DynamicSalesOrderReportRules.IsFormulaLeading("ABC"));
        Assert.True(DynamicSalesOrderReportRules.IsFormulaLeading("=1+1"));
        Assert.True(DynamicSalesOrderReportRules.IsFormulaLeading("+123"));
        Assert.True(DynamicSalesOrderReportRules.IsFormulaLeading("-5"));
        Assert.True(DynamicSalesOrderReportRules.IsFormulaLeading("@SUM(A1)"));

        Assert.Equal("'=1+1", DynamicSalesOrderReportRules.EscapeFormulaLeading("=1+1"));
        Assert.Equal("'@cmd", DynamicSalesOrderReportRules.EscapeFormulaLeading("@cmd"));
        Assert.Equal("ABC", DynamicSalesOrderReportRules.EscapeFormulaLeading("ABC"));
        Assert.Null(DynamicSalesOrderReportRules.EscapeFormulaLeading(null));
        Assert.Equal(123, DynamicSalesOrderReportRules.EscapeFormulaLeading(123));
    }

    // ==================== 3. 分组页面小计（币种分开） ====================

    [Fact]
    public async Task Export_分组_追加本页小计_币种分开()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        var c1 = SeedCustomer(db, "C1", "客户一").Id;

        SeedOrder(db, "SO-1", c1, new DateTime(2026, 9, 1), currency: Currency.USD, totalAmount: 100m);
        SeedOrder(db, "SO-2", c1, new DateTime(2026, 9, 2), currency: Currency.USD, totalAmount: 50m);
        SeedOrder(db, "SO-3", c1, new DateTime(2026, 9, 3), currency: Currency.CNY, totalAmount: 200m);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var file = ExportOk(await ctl.Export(new DynamicSalesOrderReportRequest
        {
            GroupBy = "customer",
            PageSize = 10
        }));

        var wb = OpenWorkbook(file.FileContents);
        Assert.Equal(2, wb.NumberOfSheets);
        var subtotal = wb.GetSheet("本页小计");
        Assert.NotNull(subtotal);

        Assert.Equal("分组", subtotal!.GetRow(0).GetCell(0).StringCellValue);
        Assert.Equal("币种", subtotal.GetRow(0).GetCell(1).StringCellValue);
        Assert.Equal("条数", subtotal.GetRow(0).GetCell(2).StringCellValue);
        Assert.Equal("金额", subtotal.GetRow(0).GetCell(3).StringCellValue);
        Assert.Equal(2, subtotal.LastRowNum);

        Assert.Equal($"客户 #{c1}", subtotal.GetRow(1).GetCell(0).StringCellValue);
        Assert.Equal("CNY", subtotal.GetRow(1).GetCell(1).StringCellValue);
        Assert.Equal(1d, subtotal.GetRow(1).GetCell(2).NumericCellValue);
        Assert.Equal(200d, subtotal.GetRow(1).GetCell(3).NumericCellValue);

        Assert.Equal("USD", subtotal.GetRow(2).GetCell(1).StringCellValue);
        Assert.Equal(2d, subtotal.GetRow(2).GetCell(2).NumericCellValue);
        Assert.Equal(150d, subtotal.GetRow(2).GetCell(3).NumericCellValue);
    }

    // ==================== 4. 授权 / 校验拒绝 ====================

    [Fact]
    public async Task Export_无销售订单菜单授权_拒绝()
    {
        using var db = TestDbFactory.Create();
        var user = SeedUser(db, "nomenu");
        var role = SeedRole(db, "Role-nomenu", isSystem: false);
        SeedUserRole(db, user.Id, role.Id);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Export(
            new DynamicSalesOrderReportRequest()));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    [Fact]
    public async Task Export_无身份_未认证拒绝()
    {
        using var db = TestDbFactory.Create();
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, null);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Export(
            new DynamicSalesOrderReportRequest()));
        Assert.Equal(ErrorCodes.Unauthorized, ex.Code);
    }

    [Fact]
    public async Task Export_页大小超限_拒绝()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Export(
            new DynamicSalesOrderReportRequest { PageSize = 201 }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    // ==================== 5. 数据范围（越界） ====================

    [Fact]
    public async Task Export_越界业务员_只导出范围内行()
    {
        using var db = TestDbFactory.Create();
        var (uid, mine, other) = SeedRestrictedUser(db, "alice");
        SeedOrder(db, "SO-MINE", mine);
        SeedOrder(db, "SO-OTHER", other);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var file = ExportOk(await ctl.Export(new DynamicSalesOrderReportRequest
        {
            Fields = new() { "orderNo" },
            PageSize = 100
        }));

        var sheet = OpenWorkbook(file.FileContents).GetSheetAt(0);
        Assert.Equal(1, sheet.LastRowNum);
        Assert.Equal("SO-MINE", sheet.GetRow(1).GetCell(0).StringCellValue);
    }

    // ==================== 6. 空页 ====================

    [Fact]
    public async Task Export_空页_返回仅表头工作簿()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var file = ExportOk(await ctl.Export(new DynamicSalesOrderReportRequest
        {
            Fields = new() { "orderNo" },
            PageSize = 10
        }));

        var sheet = OpenWorkbook(file.FileContents).GetSheetAt(0);
        Assert.Equal("订单号", sheet.GetRow(0).GetCell(0).StringCellValue);
        Assert.Equal(0, sheet.LastRowNum);
    }

    // ==================== 7. 只读不写库 ====================

    [Fact]
    public async Task Export_只读不写库()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        var c1 = SeedCustomer(db, "C1", "客户一").Id;
        SeedOrder(db, "SO-R", c1);

        var counting = CountingDbContext.Wrap(db);
        var ctl = new DynamicSalesOrderReportController(new DynamicSalesOrderReportQuery(counting.Proxy));
        TestAuth.SetUser(ctl, uid);

        var file = ExportOk(await ctl.Export(new DynamicSalesOrderReportRequest
        {
            Fields = new() { "orderNo" },
            PageSize = 10
        }));

        Assert.NotEmpty(file.FileContents);
        Assert.Equal(0, counting.WriteCalls);
    }

    // ==================== 8. 只读计数上下文（断言不写库） ====================

    public class CountingDbContext : DispatchProxy
    {
        private IErpDbContext _inner = null!;
        public IErpDbContext Proxy { get; private set; } = null!;
        public int WriteCalls { get; private set; }

        public static CountingDbContext Wrap(IErpDbContext inner)
        {
            var proxy = DispatchProxy.Create<IErpDbContext, CountingDbContext>();
            var counting = (CountingDbContext)(object)proxy;
            counting._inner = inner;
            counting.Proxy = proxy;
            return counting;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod is null) return null;
            if (targetMethod.Name == nameof(IErpDbContext.SaveChangesAsync))
            {
                WriteCalls++;
                return _inner.SaveChangesAsync(args is { Length: > 0 } ? (CancellationToken)args[0]! : default);
            }
            return targetMethod.Invoke(_inner, args);
        }
    }
}


