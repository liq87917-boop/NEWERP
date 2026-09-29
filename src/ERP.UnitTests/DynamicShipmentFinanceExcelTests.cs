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
using System.Reflection;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-158 动态销售订单出货 / 财务进度报表 Excel 导出（只读、有界）单元测试。
/// 覆盖：选定列顺序与行值、未知金额 / 数量保持 null（空单元格，绝不回落 0）、币种分别成行、
/// 公式前导文本转义（保持字面、非公式单元格）、无身份 / 无菜单授权 / 越界业务员 / 页大小超限拒绝、
/// 空页仅表头（文档下载限制），以及只读不写库。
/// <para>全部使用内存数据库（TestDbFactory），不连接 SQL Server、不启动 API、不执行任何 SQL / seed。</para>
/// </summary>
public class DynamicShipmentFinanceExcelTests
{
    private static readonly DateTime AsOf = new(2026, 9, 24);
    private const long CustomerA = 955001L;
    private const long CustomerB = 955002L;
    private const long ProductA = 955101L;

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

    /// <summary>播种一个「系统内置角色 + 销售订单菜单授权」用户</summary>
    private static long SeedPrivilegedUser(ErpDbContext db, string userName = "priv")
    {
        var user = SeedUser(db, userName);
        var role = SeedRole(db, $"Role-{userName}", isSystem: true);
        SeedUserRole(db, user.Id, role.Id);
        SeedRoleMenu(db, role.Id, SeedMenu(db, DynamicShipmentFinanceReportRules.RequiredMenuCode).Id);
        return user.Id;
    }

    private static (long UserId, long MineCustomerId, long OtherCustomerId) SeedRestrictedUser(
        ErpDbContext db, string userName)
    {
        var user = SeedUser(db, userName);
        var role = SeedRole(db, $"Role-{userName}", isSystem: false);
        SeedUserRole(db, user.Id, role.Id);
        SeedRoleMenu(db, role.Id, SeedMenu(db, DynamicShipmentFinanceReportRules.RequiredMenuCode).Id);
        var emp = SeedEmployee(db, userName);
        var mine = SeedCustomer(db, CustomerA, "我的客户", emp.Id);
        var other = SeedCustomer(db, CustomerB, "别人的客户", emp.Id + 1000);
        return (user.Id, mine.Id, other.Id);
    }

    private static BaseEmployee SeedEmployee(ErpDbContext db, string code, bool isSalesman = true)
    {
        var employee = new BaseEmployee { EmployeeCode = code, EmployeeName = code, IsSalesman = isSalesman, Status = 1 };
        db.BaseEmployees.Add(employee);
        db.SaveChanges();
        return employee;
    }

    private static BaseCustomer SeedCustomer(ErpDbContext db, long id, string name, long? empId = null)
    {
        var customer = new BaseCustomer
        {
            Id = id,
            CustomerCode = $"C{id}",
            CustomerName = name,
            Status = 1,
            CreditStatus = "正常",
            EmpId = empId
        };
        db.BaseCustomers.Add(customer);
        db.SaveChanges();
        return customer;
    }

    private static SalesOrder SeedOrder(ErpDbContext db, string orderNo, long customerId, Currency currency,
        decimal totalAmount, DateTime? orderDate = null, DocumentStatus status = DocumentStatus.Approved)
    {
        var order = new SalesOrder
        {
            OrderNo = orderNo,
            OrderDate = orderDate ?? AsOf.AddDays(-10),
            CustomerId = customerId,
            Currency = currency,
            TotalAmount = totalAmount,
            Status = status,
            CreatedAt = new DateTime(2026, 9, 14, 8, 0, 0),
        };
        db.SalesOrders.Add(order);
        db.SaveChanges();
        return order;
    }

    private static void SeedDetail(ErpDbContext db, long salesOrderId, long productId, decimal quantity)
    {
        db.SalesOrderDetails.Add(new SalesOrderDetail
        {
            SalesOrderId = salesOrderId,
            ProductId = productId,
            ProductName = $"商品{productId}",
            Spec = "规格A",
            Unit = "PCS",
            Quantity = quantity,
            UnitPrice = 10m,
            Amount = quantity * 10m,
        });
        db.SaveChanges();
    }

    private static void SeedStockOut(ErpDbContext db, string stockOutNo, long? salesOrderId,
        DocumentStatus status, params (long ProductId, decimal Quantity)[] lines)
    {
        var stockOut = new StockOut
        {
            StockOutNo = stockOutNo,
            StockOutDate = AsOf.AddDays(-5),
            SalesOrderId = salesOrderId,
            CustomerId = CustomerA,
            WarehouseId = 1,
            Status = status,
        };
        db.StockOuts.Add(stockOut);
        db.SaveChanges();
        foreach (var (productId, quantity) in lines)
        {
            db.StockOutDetails.Add(new StockOutDetail
            {
                StockOutId = stockOut.Id,
                ProductId = productId,
                ProductName = $"商品{productId}",
                Unit = "PCS",
                Quantity = quantity,
            });
        }

        db.SaveChanges();
    }

    private static void SeedDepositApply(ErpDbContext db, string applyNo, long salesOrderId, decimal amount,
        Currency currency, DocumentStatus status)
    {
        db.FinanceDepositApplies.Add(new FinanceDepositApply
        {
            ApplyNo = applyNo,
            ApplyDate = AsOf.AddDays(-3),
            SalesOrderId = salesOrderId,
            CustomerId = CustomerA,
            Amount = amount,
            Currency = currency,
            Status = status,
        });
        db.SaveChanges();
    }

    private static DynamicShipmentFinanceReportController NewController(ErpDbContext db) => new(db);

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

    // ==================== 1. 选定列顺序与行值 ====================

    [Fact]
    public async Task Export_导出当前页_列顺序与行值符合选定字段()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "甲客户");
        SeedOrder(db, "SO-XLS-1", CustomerA, Currency.USD, 100m);
        SeedOrder(db, "SO-XLS-2", CustomerA, Currency.USD, 200m);
        await db.SaveChangesAsync();

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var file = ExportOk(await ctl.Export(new DynamicShipmentFinanceReportRequest
        {
            Fields = new() { "currency", "orderNo", "orderAmount" },
            Page = 1,
            PageSize = 10
        }));

        var sheet = OpenWorkbook(file.FileContents).GetSheetAt(0);
        Assert.Equal("币种", sheet.GetRow(0).GetCell(0).StringCellValue);
        Assert.Equal("订单号", sheet.GetRow(0).GetCell(1).StringCellValue);
        Assert.Equal("订单金额", sheet.GetRow(0).GetCell(2).StringCellValue);
        Assert.Equal(2, sheet.LastRowNum);

        Assert.Equal("USD", sheet.GetRow(1).GetCell(0).StringCellValue);
        Assert.Equal("SO-XLS-1", sheet.GetRow(1).GetCell(1).StringCellValue);
        Assert.Equal(100d, sheet.GetRow(1).GetCell(2).NumericCellValue);
        Assert.Equal("SO-XLS-2", sheet.GetRow(2).GetCell(1).StringCellValue);
        Assert.Equal(200d, sheet.GetRow(2).GetCell(2).NumericCellValue);
    }

    // ==================== 2. 未知金额保持 null（空单元格，绝不回落 0） ====================

    [Fact]
    public async Task Export_无权威引用_金额未知_导出为空单元格_绝不回落为0()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "甲客户");
        var order = SeedOrder(db, "SO-XLS-U", CustomerA, Currency.USD, 1000m);
        SeedDetail(db, order.Id, ProductA, 10m);
        await db.SaveChangesAsync();

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var file = ExportOk(await ctl.Export(new DynamicShipmentFinanceReportRequest
        {
            Fields = new() { "orderNo", "financeLinkStatus", "linkedAmount", "uncoveredAmount", "submittedAmount" },
            PageSize = 10
        }));

        var sheet = OpenWorkbook(file.FileContents).GetSheetAt(0);
        var row = sheet.GetRow(1);
        Assert.Equal("SO-XLS-U", row.GetCell(0).StringCellValue);
        Assert.Equal(DynamicShipmentFinanceReportRules.FinanceStatusUnlinked, row.GetCell(1).StringCellValue);
        Assert.Equal(string.Empty, row.GetCell(2).StringCellValue);
        Assert.Equal(string.Empty, row.GetCell(3).StringCellValue);
        Assert.Equal(string.Empty, row.GetCell(4).StringCellValue);
    }

    // ==================== 3. 币种分别成行，绝不合并 / 换算 ====================

    [Fact]
    public async Task Export_币种分别成行_绝不合并或换算()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "甲客户");
        SeedCustomer(db, CustomerB, "乙客户");
        SeedOrder(db, "SO-XLS-C1", CustomerA, Currency.USD, 200m);
        SeedOrder(db, "SO-XLS-C2", CustomerB, Currency.CNY, 300m);
        await db.SaveChangesAsync();

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var file = ExportOk(await ctl.Export(new DynamicShipmentFinanceReportRequest
        {
            Fields = new() { "orderNo", "currency", "orderAmount" },
            PageSize = 10
        }));

        var sheet = OpenWorkbook(file.FileContents).GetSheetAt(0);
        Assert.Equal(2, sheet.LastRowNum);

        var byCurrency = new Dictionary<string, double>();
        for (var r = 1; r <= sheet.LastRowNum; r++)
            byCurrency[sheet.GetRow(r).GetCell(1).StringCellValue] = sheet.GetRow(r).GetCell(2).NumericCellValue;

        Assert.Equal(200d, byCurrency["USD"]);
        Assert.Equal(300d, byCurrency["CNY"]);
    }

    // ==================== 4. 公式注入防护 ====================

    [Fact]
    public async Task Export_公式前导文本_转义为字面文本非公式单元格()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "甲客户");
        SeedOrder(db, "=1+1", CustomerA, Currency.USD, 100m);
        await db.SaveChangesAsync();

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var file = ExportOk(await ctl.Export(new DynamicShipmentFinanceReportRequest
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
        Assert.False(DynamicShipmentFinanceReportRules.IsFormulaLeading(null));
        Assert.False(DynamicShipmentFinanceReportRules.IsFormulaLeading(""));
        Assert.False(DynamicShipmentFinanceReportRules.IsFormulaLeading("ABC"));
        Assert.True(DynamicShipmentFinanceReportRules.IsFormulaLeading("=1+1"));
        Assert.True(DynamicShipmentFinanceReportRules.IsFormulaLeading("+123"));
        Assert.True(DynamicShipmentFinanceReportRules.IsFormulaLeading("-5"));
        Assert.True(DynamicShipmentFinanceReportRules.IsFormulaLeading("@SUM(A1)"));

        Assert.Equal("'=1+1", DynamicShipmentFinanceReportRules.EscapeFormulaLeading("=1+1"));
        Assert.Equal("'@cmd", DynamicShipmentFinanceReportRules.EscapeFormulaLeading("@cmd"));
        Assert.Equal("ABC", DynamicShipmentFinanceReportRules.EscapeFormulaLeading("ABC"));
        Assert.Null(DynamicShipmentFinanceReportRules.EscapeFormulaLeading(null));
        Assert.Equal(123, DynamicShipmentFinanceReportRules.EscapeFormulaLeading(123));

        var row = new Dictionary<string, object?> { ["orderNo"] = "=1+1", ["orderAmount"] = 100m };
        var export = DynamicShipmentFinanceReportRules.BuildExportRow(row);
        Assert.Equal("'=1+1", export["orderNo"]);
        Assert.Equal(100m, export["orderAmount"]);
    }

    // ==================== 5. 授权（fail closed） ====================

    [Fact]
    public async Task Export_无身份_拒绝()
    {
        using var db = TestDbFactory.Create();
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, null);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Export(new DynamicShipmentFinanceReportRequest()));
        Assert.Equal(ErrorCodes.Unauthorized, ex.Code);
    }

    [Fact]
    public async Task Export_无菜单授权_拒绝()
    {
        using var db = TestDbFactory.Create();
        var denied = SeedUser(db, "no-menu");
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, denied.Id);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Export(new DynamicShipmentFinanceReportRequest()));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    // ==================== 6. 数据范围（越界业务员） ====================

    [Fact]
    public async Task Export_越界业务员_只导出范围内行()
    {
        using var db = TestDbFactory.Create();
        var (uid, mine, other) = SeedRestrictedUser(db, "alice");
        var a = SeedOrder(db, "SO-MINE", mine, Currency.USD, 100m);
        SeedDetail(db, a.Id, ProductA, 10m);
        SeedOrder(db, "SO-OTHER", other, Currency.USD, 300m);
        await db.SaveChangesAsync();

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var file = ExportOk(await ctl.Export(new DynamicShipmentFinanceReportRequest
        {
            Fields = new() { "orderNo", "customerId" },
            PageSize = 100
        }));

        var sheet = OpenWorkbook(file.FileContents).GetSheetAt(0);
        Assert.Equal(1, sheet.LastRowNum);
        Assert.Equal("SO-MINE", sheet.GetRow(1).GetCell(0).StringCellValue);
    }

    // ==================== 7. 无效输入（读取前拒绝） ====================

    [Fact]
    public async Task Export_页大小超限_拒绝()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Export(new DynamicShipmentFinanceReportRequest { PageSize = 201 }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public async Task Export_未知字段_拒绝()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Export(new DynamicShipmentFinanceReportRequest { Fields = new() { "unknownField" } }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    // ==================== 8. 空页（文档下载限制：仅表头） ====================

    [Fact]
    public async Task Export_空页_返回仅表头工作簿()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var file = ExportOk(await ctl.Export(new DynamicShipmentFinanceReportRequest
        {
            Fields = new() { "orderNo", "currency" },
            PageSize = 10
        }));

        var sheet = OpenWorkbook(file.FileContents).GetSheetAt(0);
        Assert.Equal("订单号", sheet.GetRow(0).GetCell(0).StringCellValue);
        Assert.Equal("币种", sheet.GetRow(0).GetCell(1).StringCellValue);
        Assert.Equal(0, sheet.LastRowNum);
    }

    // ==================== 9. 只读不写库 ====================

    [Fact]
    public async Task Export_只读不写库()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "甲客户");
        var order = SeedOrder(db, "SO-R", CustomerA, Currency.USD, 100m);
        SeedDetail(db, order.Id, ProductA, 5m);
        await db.SaveChangesAsync();

        var counting = CountingDbContext.Wrap(db);
        var ctl = new DynamicShipmentFinanceReportController(counting.Proxy);
        TestAuth.SetUser(ctl, uid);

        var file = ExportOk(await ctl.Export(new DynamicShipmentFinanceReportRequest
        {
            Fields = new() { "orderNo" },
            PageSize = 10
        }));

        Assert.NotEmpty(file.FileContents);
        Assert.Equal(0, counting.WriteCalls);
    }

    // ==================== 10. 只读计数上下文（断言不写库） ====================

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




