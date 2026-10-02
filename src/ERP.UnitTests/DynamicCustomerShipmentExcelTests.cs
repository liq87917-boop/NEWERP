using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using NPOI.SS.UserModel;
using NPOI.XSSF.UserModel;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-229 动态客户出货量证据报表 Excel 导出（只读、有界）单元测试。
/// 覆盖：选定列顺序与类型化值（已知金额 / 订单数 / 数量为数值、未知显式「未知」文本，绝不写成 0）、
/// 公式前导文本转义、仅导出当前页（单页上限 200）、上下文工作表标注日期 / 分页 / 来源上限 / 币种 / 单位 / 未知 / 来源 /
/// 页面覆盖、数据范围（受限制业务员看不到他人）、未知字段 / 页大小超限拒绝、空页仅表头，以及只读不写库。
/// <para>全部使用内存数据库（TestDbFactory），不连接 SQL Server、不启动 API、不执行任何 SQL / seed。</para>
/// </summary>
public class DynamicCustomerShipmentExcelTests
{
    private const string MenuCode = "customer-shipment";

    private static readonly DateTime Start = new(2026, 9, 1);
    private static readonly DateTime End = new(2026, 9, 30);

    // ==================== 脚手架 ====================

    private static BaseCustomer SeedCustomer(ErpDbContext db, string code, string name, long? empId = null)
    {
        var customer = new BaseCustomer
        {
            CustomerCode = code,
            CustomerName = name,
            Status = 1,
            CreditStatus = "正常",
            EmpId = empId,
            IsDeleted = false
        };
        db.BaseCustomers.Add(customer);
        db.SaveChanges();
        return customer;
    }

    private static BaseEmployee SeedEmployee(ErpDbContext db, string code, bool isSalesman = true)
    {
        var employee = new BaseEmployee
        {
            EmployeeCode = code,
            EmployeeName = code,
            IsSalesman = isSalesman,
            Status = 1
        };
        db.BaseEmployees.Add(employee);
        db.SaveChanges();
        return employee;
    }

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

    private static SysUser SeedAuthorizedUser(ErpDbContext db, string userName, string roleCode, bool isSystemRole = false)
    {
        var role = SeedRole(db, roleCode, isSystemRole);
        var user = SeedUser(db, userName);
        SeedUserRole(db, user.Id, role.Id);
        SeedRoleMenu(db, role.Id, SeedMenu(db, MenuCode).Id);
        return user;
    }

    private static SalesOrder SeedOrder(
        ErpDbContext db, string orderNo, long customerId, Currency currency, decimal totalAmount)
    {
        var order = new SalesOrder
        {
            OrderNo = orderNo,
            OrderDate = new DateTime(2026, 9, 10),
            CustomerId = customerId,
            Status = DocumentStatus.Approved,
            Currency = currency,
            TotalAmount = totalAmount
        };
        db.SalesOrders.Add(order);
        db.SaveChanges();
        return order;
    }

    private static SalesOrderDetail SeedDetail(
        ErpDbContext db, long orderId, string unit, decimal quantity, bool deleted = false)
    {
        var detail = new SalesOrderDetail
        {
            SalesOrderId = orderId,
            ProductId = 1,
            ProductName = "商品",
            Quantity = quantity,
            Unit = unit,
            IsDeleted = deleted
        };
        db.SalesOrderDetails.Add(detail);
        db.SaveChanges();
        return detail;
    }

    private static DynamicCustomerShipmentReportController NewController(ErpDbContext db)
        => new(db, new ReportService(db));

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

    // ==================== 1. 选定列顺序与类型化值 ====================

    [Fact]
    public async Task Export_选定列顺序与类型化值_已知金额订单数数量为数值()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv-user", "Priv", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "客户");
        var order = SeedOrder(db, "SO-1", customer.Id, Currency.USD, 100m);
        SeedDetail(db, order.Id, "PCS", 10m);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var file = ExportOk(await ctl.Export(new DynamicCustomerShipmentReportRequest
        {
            Fields = new List<string> { "customerName", "currency", "orderCount", "totalAmount", "totalQuantity" },
            Start = Start,
            End = End,
            Page = 1,
            PageSize = 20
        }));

        var sheet = OpenWorkbook(file.FileContents).GetSheetAt(0);
        Assert.Equal("客户出货量统计表", sheet.SheetName);
        Assert.Equal("客户", sheet.GetRow(0).GetCell(0).StringCellValue);
        Assert.Equal("原币币种", sheet.GetRow(0).GetCell(1).StringCellValue);
        Assert.Equal("已审核订单数", sheet.GetRow(0).GetCell(2).StringCellValue);
        Assert.Equal("原币金额小计", sheet.GetRow(0).GetCell(3).StringCellValue);
        Assert.Equal("已知单一单位数量", sheet.GetRow(0).GetCell(4).StringCellValue);

        Assert.Equal("客户", sheet.GetRow(1).GetCell(0).StringCellValue);
        Assert.Equal("USD", sheet.GetRow(1).GetCell(1).StringCellValue);
        Assert.Equal(CellType.Numeric, sheet.GetRow(1).GetCell(2).CellType);
        Assert.Equal(1d, sheet.GetRow(1).GetCell(2).NumericCellValue);
        Assert.Equal(CellType.Numeric, sheet.GetRow(1).GetCell(3).CellType);
        Assert.Equal(100d, sheet.GetRow(1).GetCell(3).NumericCellValue);
        Assert.Equal(CellType.Numeric, sheet.GetRow(1).GetCell(4).CellType);
        Assert.Equal(10d, sheet.GetRow(1).GetCell(4).NumericCellValue);
    }

    [Fact]
    public async Task Export_未知币种与未知单位_显式未知文本_绝不写成0()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv-user", "Priv", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "客户");
        SeedOrder(db, "SO-BAD", customer.Id, (Currency)999, 777m);   // 未知币种 + 无明细

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var file = ExportOk(await ctl.Export(new DynamicCustomerShipmentReportRequest
        {
            Fields = new List<string> { "currency", "orderCount", "totalAmount", "totalQuantity" },
            Start = Start,
            End = End,
            Page = 1,
            PageSize = 20
        }));

        var sheet = OpenWorkbook(file.FileContents).GetSheetAt(0);
        Assert.Equal("未知币种", sheet.GetRow(1).GetCell(0).StringCellValue);
        Assert.Equal(CellType.Numeric, sheet.GetRow(1).GetCell(1).CellType);
        Assert.Equal(1d, sheet.GetRow(1).GetCell(1).NumericCellValue);
        Assert.Equal("未知", sheet.GetRow(1).GetCell(2).StringCellValue);   // null 金额 → 文本「未知」
        Assert.Equal("未知", sheet.GetRow(1).GetCell(3).StringCellValue);   // null 数量 → 文本「未知」
    }

    // ==================== 2. 公式注入防护 ====================

    [Fact]
    public async Task Export_公式前导客户名_转义为字面文本非公式单元()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv-user", "Priv", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "=SUM(A1)");
        SeedOrder(db, "SO-1", customer.Id, Currency.USD, 100m);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var file = ExportOk(await ctl.Export(new DynamicCustomerShipmentReportRequest
        {
            Fields = new List<string> { "customerName" },
            Start = Start,
            End = End,
            Page = 1,
            PageSize = 20
        }));

        var sheet = OpenWorkbook(file.FileContents).GetSheetAt(0);
        var cell = sheet.GetRow(1).GetCell(0);
        Assert.Equal(CellType.String, cell.CellType);
        Assert.Equal("'=SUM(A1)", cell.StringCellValue);
    }

    [Fact]
    public void 公式转义纯规则_文本前导转义_数值原样()
    {
        Assert.True(DynamicCustomerShipmentReportRules.IsFormulaLeading("=1+1"));
        Assert.True(DynamicCustomerShipmentReportRules.IsFormulaLeading("-5"));
        Assert.True(DynamicCustomerShipmentReportRules.IsFormulaLeading("+5"));
        Assert.True(DynamicCustomerShipmentReportRules.IsFormulaLeading("@SUM(A1)"));
        Assert.False(DynamicCustomerShipmentReportRules.IsFormulaLeading("ABC"));
        Assert.False(DynamicCustomerShipmentReportRules.IsFormulaLeading(""));

        Assert.Equal("'=1+1", DynamicCustomerShipmentReportRules.EscapeFormulaLeading("=1+1"));
        Assert.Equal(123, DynamicCustomerShipmentReportRules.EscapeFormulaLeading(123));

        var exportRow = DynamicCustomerShipmentReportRules.BuildExportRow(
            new Dictionary<string, object?> { ["customerName"] = "=1+1", ["orderCount"] = 3, ["totalAmount"] = null });
        Assert.Equal("'=1+1", exportRow["customerName"]);
        Assert.Equal(3, exportRow["orderCount"]);
        Assert.Equal("未知", exportRow["totalAmount"]);
    }

    // ==================== 3. 上下文工作表 ====================

    [Fact]
    public async Task Export_上下文工作表_含日期分页来源上限币种单位未知来源()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv-user", "Priv", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "客户");
        SeedOrder(db, "SO-1", customer.Id, Currency.USD, 100m);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var file = ExportOk(await ctl.Export(new DynamicCustomerShipmentReportRequest
        {
            Fields = new List<string> { "customerName" },
            Start = Start,
            End = End,
            Page = 1,
            PageSize = 20
        }));

        using var workbook = OpenWorkbook(file.FileContents);
        Assert.Equal(2, workbook.NumberOfSheets);
        var ctx = workbook.GetSheetAt(1);
        Assert.Equal("报表口径", ctx.SheetName);

        var labels = new List<string>();
        for (var r = 0; r <= ctx.LastRowNum; r++)
            labels.Add(ctx.GetRow(r).GetCell(0).StringCellValue);

        Assert.Contains("开始日期", labels);
        Assert.Contains("结束日期", labels);
        Assert.Contains("分页", labels);
        Assert.Contains("来源上限", labels);
        Assert.Contains("币种口径", labels);
        Assert.Contains("单位口径", labels);
        Assert.Contains("未知口径", labels);
        Assert.Contains("来源证据", labels);
        Assert.Contains("页面覆盖", labels);
        Assert.Contains("只读声明", labels);
    }

    // ==================== 4. 仅导出当前页 / 空页 ====================

    [Fact]
    public async Task Export_仅导出当前页_受页大小上限约束()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv-user", "Priv", isSystemRole: true);
        var a = SeedCustomer(db, "C001", "客户A");
        var b = SeedCustomer(db, "C002", "客户B");
        var c = SeedCustomer(db, "C003", "客户C");

        SeedOrder(db, "SO-A", a.Id, Currency.USD, 1m);
        SeedOrder(db, "SO-B", b.Id, Currency.USD, 1m);
        SeedOrder(db, "SO-C", c.Id, Currency.USD, 1m);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var file = ExportOk(await ctl.Export(new DynamicCustomerShipmentReportRequest
        {
            Fields = new List<string> { "customerName" },
            Start = Start,
            End = End,
            Page = 1,
            PageSize = 1
        }));

        var sheet = OpenWorkbook(file.FileContents).GetSheetAt(0);
        Assert.Equal(1, sheet.LastRowNum);   // 表头 + 仅 1 条当前页数据
    }

    [Fact]
    public async Task Export_空页_仅表头且上下文含空页说明()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv-user", "Priv", isSystemRole: true);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var file = ExportOk(await ctl.Export(new DynamicCustomerShipmentReportRequest
        {
            Fields = new List<string> { "customerName" },
            Start = Start,
            End = End,
            Page = 1,
            PageSize = 20
        }));

        using var workbook = OpenWorkbook(file.FileContents);
        var sheet = workbook.GetSheetAt(0);
        Assert.Equal(0, sheet.LastRowNum);

        var ctx = workbook.GetSheetAt(1);
        var labels = new List<string>();
        for (var r = 0; r <= ctx.LastRowNum; r++)
            labels.Add(ctx.GetRow(r).GetCell(0).StringCellValue);
        Assert.Contains("空页说明", labels);
    }

    // ==================== 5. 数据范围 / 拒绝 ====================

    [Fact]
    public async Task Export_受限制业务员_只导出被分配客户()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "alice", "Sales");
        var employee = SeedEmployee(db, "alice");
        var mine = SeedCustomer(db, "C001", "我的客户", employee.Id);
        var other = SeedCustomer(db, "C002", "别人的客户", employee.Id + 1000);

        SeedOrder(db, "SO-MINE", mine.Id, Currency.USD, 100m);
        SeedOrder(db, "SO-OTHER", other.Id, Currency.USD, 999m);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var file = ExportOk(await ctl.Export(new DynamicCustomerShipmentReportRequest
        {
            Fields = new List<string> { "customerName" },
            Start = Start,
            End = End,
            Page = 1,
            PageSize = 20
        }));

        var sheet = OpenWorkbook(file.FileContents).GetSheetAt(0);
        Assert.Equal(1, sheet.LastRowNum);
        Assert.Equal("我的客户", sheet.GetRow(1).GetCell(0).StringCellValue);
    }

    [Fact]
    public async Task Export_未知字段拒绝()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv-user", "Priv", isSystemRole: true);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Export(
            new DynamicCustomerShipmentReportRequest
            {
                Fields = new List<string> { "customerName", "notAField" },
                Start = Start,
                End = End
            }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public async Task Export_页大小超限拒绝()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv-user", "Priv", isSystemRole: true);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Export(
            new DynamicCustomerShipmentReportRequest
            {
                Start = Start,
                End = End,
                PageSize = 201
            }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public async Task Export_无菜单授权拒绝()
    {
        using var db = TestDbFactory.Create();
        var role = SeedRole(db, "NoMenu");
        var user = SeedUser(db, "nommenu-user");
        SeedUserRole(db, user.Id, role.Id);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Export(
            new DynamicCustomerShipmentReportRequest { Start = Start, End = End }));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    // ==================== 6. ERP-231 应用筛选上下文 ====================

    [Fact]
    public async Task Export_应用筛选_上下文工作表可见规范化筛选_即使列被取消选择()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv-user", "Priv", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "客户");
        SeedOrder(db, "SO-1", customer.Id, Currency.USD, 100m);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var file = ExportOk(await ctl.Export(new DynamicCustomerShipmentReportRequest
        {
            // 不选择任何身份列，仍应保留服务端筛选上下文
            Fields = new List<string> { "totalAmount" },
            Start = Start,
            End = End,
            Page = 1,
            PageSize = 20,
            Filter = new CustomerShipmentFilterDto { CustomerId = customer.Id, Currency = "usd" }
        }));

        using var workbook = OpenWorkbook(file.FileContents);
        var ctx = workbook.GetSheetAt(1);
        var labels = new List<string>();
        for (var r = 0; r <= ctx.LastRowNum; r++)
        {
            var row = ctx.GetRow(r);
            if (row is null) continue;
            labels.Add(row.GetCell(0).StringCellValue);
            if (row.GetCell(0).StringCellValue == DynamicCustomerShipmentReportRules.ContextFilterLabel)
            {
                var value = row.GetCell(1).StringCellValue;
                Assert.Contains("客户 Id " + customer.Id, value);
                Assert.Contains("原币币种 USD", value);
            }
        }
        Assert.Contains(DynamicCustomerShipmentReportRules.ContextFilterLabel, labels);
    }

    // ==================== 7. ERP-232 全匹配汇总不改变明细导出 ====================

    [Fact]
    public void BuildPage_装配全匹配汇总_明细导出仍保持数据与口径两张表()
    {
        var items = new List<ReportDtos.CustomerShipmentItem>
        {
            new() { CustomerId = 1, CustomerName = "客户", Currency = "USD", TotalAmount = 100m, OrderCount = 1 },
        };

        var page = DynamicCustomerShipmentReportRules.BuildPage(
            items,
            new List<string> { "customerName", "currency", "orderCount", "totalAmount" },
            1, 20, Start, End);

        Assert.NotNull(page.Summary);                      // 明细导出复用的页面仍携带全匹配汇总
        Assert.Single(page.Summary.CurrencyRows);
        Assert.Contains(DynamicCustomerShipmentSummaryRules.CurrencySummaryColumns, c => c.Key == "totalAmount");
    }

    [Fact]
    public async Task Export_全匹配汇总在场_明细导出仍为数据与口径两张表_不新增汇总表()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv-user", "Priv", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "客户");
        SeedOrder(db, "SO-1", customer.Id, Currency.USD, 100m);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var file = ExportOk(await ctl.Export(new DynamicCustomerShipmentReportRequest
        {
            Fields = new List<string> { "customerName", "totalAmount" },
            Start = Start,
            End = End,
            Page = 1,
            PageSize = 20
        }));

        using var workbook = OpenWorkbook(file.FileContents);
        Assert.Equal(2, workbook.NumberOfSheets);          // 数据表 + 报表口径表，绝不新增汇总表
        Assert.Equal("客户出货量统计表", workbook.GetSheetAt(0).SheetName);
        Assert.Equal(DynamicCustomerShipmentReportRules.ContextSheetName, workbook.GetSheetAt(1).SheetName);
    }

}
