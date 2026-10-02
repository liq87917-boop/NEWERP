using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.DTOs;
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
/// ERP-221 动态订单利润暂估报表 Excel 导出（只读、有界）单元测试。
/// 覆盖：选定列顺序与类型化值（身份 / 销售额 / 当前价估算为数值、日期为安全文本）、
/// 公式前导文本转义、null 金额显式「未知」（绝不写成数值 0、绝不跨币种求和）、
/// 仅导出当前页、日期 / 分页 / 来源上限 / 原币 / 缺失成本依据上下文工作表、
/// 无身份 / 无菜单授权 / 授权撤销拒绝、空页仅表头 + 上下文空页说明，以及只读不写库。
/// <para>全部使用内存数据库（TestDbFactory），不连接 SQL Server、不启动 API、不执行任何 SQL / seed。</para>
/// </summary>
public class DynamicOrderProfitEstimateExcelTests
{
    private const string OrderProfitMenuCode = "order-profit";
    private static readonly DateTime Start = new(2026, 9, 1);
    private static readonly DateTime End = new(2026, 9, 30);

    // ==================== 脚手架 ====================

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

    private static BaseCustomer SeedCustomer(ErpDbContext db, string code, string name)
    {
        var customer = new BaseCustomer
        {
            CustomerCode = code,
            CustomerName = name,
            Status = 1,
            CreditStatus = "正常",
            IsDeleted = false
        };
        db.BaseCustomers.Add(customer);
        db.SaveChanges();
        return customer;
    }

    private static BaseProduct SeedProduct(ErpDbContext db, string code, decimal costPrice)
    {
        var product = new BaseProduct
        {
            ProductCode = code,
            ProductName = code,
            SalePrice = 100m,
            CostPrice = costPrice,
            Status = 1
        };
        db.BaseProducts.Add(product);
        db.SaveChanges();
        return product;
    }

    private static SalesOrder SeedOrder(ErpDbContext db, string orderNo, long customerId, Currency currency, decimal totalAmount)
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

    private static void SeedDetail(ErpDbContext db, long orderId, long productId, decimal quantity)
    {
        db.SalesOrderDetails.Add(new SalesOrderDetail
        {
            SalesOrderId = orderId,
            ProductId = productId,
            ProductName = "商品",
            Quantity = quantity,
            Unit = "PCS"
        });
        db.SaveChanges();
    }

    private static SysUser SeedAuthorizedUser(ErpDbContext db, string userName, string roleCode, bool isSystemRole = true)
    {
        var role = SeedRole(db, roleCode, isSystemRole);
        var user = SeedUser(db, userName);
        SeedUserRole(db, user.Id, role.Id);
        var menu = SeedMenu(db, OrderProfitMenuCode);
        SeedRoleMenu(db, role.Id, menu.Id);
        return user;
    }

    private static DynamicOrderProfitEstimateReportController NewController(ErpDbContext db)
        => new(db, new ReportService(db));

    private static FileContentResult ExportOk(IActionResult result)
        => Assert.IsType<FileContentResult>(result);

    private static XSSFWorkbook OpenWorkbook(byte[] bytes)
    {
        using var input = new MemoryStream(bytes);
        return new XSSFWorkbook(input);
    }

    // ==================== 导出：工作簿结构与类型化单元格 ====================

    [Fact]
    public async Task Export_返回xlsx附件_数据表与口径表齐备()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "opd-xlsx", "Priv");
        var customer = SeedCustomer(db, "C001", "客户");
        SeedOrder(db, "SO-1", customer.Id, Currency.USD, 1500m);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var file = ExportOk(await ctl.Export(new DynamicOrderProfitEstimateReportRequest
        {
            Fields = new List<string> { "orderNo", "currency", "salesAmount" },
            Start = Start,
            End = End
        }));

        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", file.ContentType);

        using var workbook = OpenWorkbook(file.FileContents);
        Assert.Equal(2, workbook.NumberOfSheets);
        Assert.Equal(DynamicOrderProfitEstimateReportRules.RequiredMenuText, workbook.GetSheetAt(0).SheetName);
        Assert.Equal(DynamicOrderProfitEstimateReportRules.ContextSheetName, workbook.GetSheetAt(1).SheetName);
    }

    [Fact]
    public async Task Export_选定列顺序与类型化数值单元格_日期为安全文本()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "opd-typed", "Priv");
        var customer = SeedCustomer(db, "C001", "客户");
        var product = SeedProduct(db, "P001", 60m);
        var order = SeedOrder(db, "SO-1", customer.Id, Currency.USD, 1500m);
        SeedDetail(db, order.Id, product.Id, 10m);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var file = ExportOk(await ctl.Export(new DynamicOrderProfitEstimateReportRequest
        {
            Fields = new List<string> { "orderNo", "orderDate", "salesAmount", "currentPriceEstimate" },
            Start = Start,
            End = End
        }));

        using var workbook = OpenWorkbook(file.FileContents);
        var sheet = workbook.GetSheetAt(0);

        Assert.Equal("订单号", sheet.GetRow(0).GetCell(0).StringCellValue);
        Assert.Equal("订单日期", sheet.GetRow(0).GetCell(1).StringCellValue);
        Assert.Equal("销售额(原币)", sheet.GetRow(0).GetCell(2).StringCellValue);
        Assert.Equal("当前价估算(币种未知)", sheet.GetRow(0).GetCell(3).StringCellValue);

        var row = sheet.GetRow(1);
        Assert.Equal("SO-1", row.GetCell(0).StringCellValue);
        Assert.Equal(CellType.String, row.GetCell(1).CellType);      // 日期为安全文本
        Assert.StartsWith("2026-09-10", row.GetCell(1).StringCellValue);
        Assert.Equal(CellType.Numeric, row.GetCell(2).CellType);     // 销售额为数值
        Assert.Equal(1500d, row.GetCell(2).NumericCellValue, 2);
        Assert.Equal(CellType.Numeric, row.GetCell(3).CellType);     // 当前价估算为数值
        Assert.Equal(600d, row.GetCell(3).NumericCellValue, 2);
    }

    [Fact]
    public async Task Export_公式前导文本转义为字面文本()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "opd-formula", "Priv");
        var customer = SeedCustomer(db, "C001", "=SUM(A1)");
        SeedOrder(db, "SO-1", customer.Id, Currency.USD, 1000m);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var file = ExportOk(await ctl.Export(new DynamicOrderProfitEstimateReportRequest
        {
            Fields = new List<string> { "customerName" },
            Start = Start,
            End = End
        }));

        using var workbook = OpenWorkbook(file.FileContents);
        var cell = workbook.GetSheetAt(0).GetRow(1).GetCell(0);
        Assert.Equal(CellType.String, cell.CellType);
        Assert.Equal("'=SUM(A1)", cell.StringCellValue);
    }

    [Fact]
    public async Task Export_null金额显式未知_绝不写成数值0()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "opd-null", "Priv");
        var customer = SeedCustomer(db, "C001", "客户");
        var product = SeedProduct(db, "P001", 60m);
        var order = SeedOrder(db, "SO-1", customer.Id, Currency.USD, 1500m);
        SeedDetail(db, order.Id, product.Id, 10m);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var file = ExportOk(await ctl.Export(new DynamicOrderProfitEstimateReportRequest
        {
            Fields = new List<string> { "orderNo", "costAmount", "profit", "profitRate" },
            Start = Start,
            End = End
        }));

        using var workbook = OpenWorkbook(file.FileContents);
        var row = workbook.GetSheetAt(0).GetRow(1);
        Assert.Equal(CellType.String, row.GetCell(1).CellType);
        Assert.Equal("未知", row.GetCell(1).StringCellValue);   // null 成本 → 未知，绝非数值 0
        Assert.Equal(CellType.String, row.GetCell(2).CellType);
        Assert.Equal("未知", row.GetCell(2).StringCellValue);   // null 利润 → 未知
        Assert.Equal(CellType.String, row.GetCell(3).CellType);
        Assert.Equal("未知", row.GetCell(3).StringCellValue);   // null 利润率 → 未知
    }

    [Fact]
    public async Task Export_日期分页来源上限与缺失成本依据上下文已标注()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "opd-ctx", "Priv");
        var customer = SeedCustomer(db, "C001", "客户");
        SeedOrder(db, "SO-1", customer.Id, Currency.USD, 1000m);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var file = ExportOk(await ctl.Export(new DynamicOrderProfitEstimateReportRequest
        {
            Fields = new List<string> { "orderNo" },
            Start = Start,
            End = End,
            Page = 1,
            PageSize = 200
        }));

        using var workbook = OpenWorkbook(file.FileContents);
        var ctx = workbook.GetSheetAt(1);

        Assert.Equal(DynamicOrderProfitEstimateReportRules.ContextStartLabel, ctx.GetRow(0).GetCell(0).StringCellValue);
        Assert.Equal("2026-09-01", ctx.GetRow(0).GetCell(1).StringCellValue);
        Assert.Equal(DynamicOrderProfitEstimateReportRules.ContextEndLabel, ctx.GetRow(1).GetCell(0).StringCellValue);
        Assert.Equal("2026-09-30", ctx.GetRow(1).GetCell(1).StringCellValue);
        Assert.Equal(DynamicOrderProfitEstimateReportRules.ContextPageLabel, ctx.GetRow(2).GetCell(0).StringCellValue);
        Assert.Contains("第 1 页", ctx.GetRow(2).GetCell(1).StringCellValue);
        Assert.Equal(DynamicOrderProfitEstimateReportRules.ContextSourceLimitLabel, ctx.GetRow(3).GetCell(0).StringCellValue);
        Assert.Equal(DynamicOrderProfitEstimateReportRules.ContextCurrencyLabel, ctx.GetRow(4).GetCell(0).StringCellValue);
        Assert.Contains("绝不跨币种合计", ctx.GetRow(4).GetCell(1).StringCellValue);
        Assert.Equal(DynamicOrderProfitEstimateReportRules.ContextMissingCostBasisLabel, ctx.GetRow(6).GetCell(0).StringCellValue);
        Assert.Contains("未知", ctx.GetRow(6).GetCell(1).StringCellValue);
        Assert.Equal(DynamicOrderProfitEstimateReportRules.ContextCoverageLabel, ctx.GetRow(7).GetCell(0).StringCellValue);
    }

    [Fact]
    public async Task Export_授权撤销_拒绝且不返回工作簿()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "opd-revoke", "Priv");
        var customer = SeedCustomer(db, "C001", "客户");
        SeedOrder(db, "SO-1", customer.Id, Currency.USD, 1000m);

        var roleMenu = db.SysRoleMenus.Single();
        roleMenu.IsDeleted = true;
        db.SaveChanges();

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => ctl.Export(new DynamicOrderProfitEstimateReportRequest { Start = Start, End = End }));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    [Fact]
    public async Task Export_无身份_拒绝()
    {
        using var db = TestDbFactory.Create();
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, null);

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => ctl.Export(new DynamicOrderProfitEstimateReportRequest { Start = Start, End = End }));
        Assert.Equal(ErrorCodes.Unauthorized, ex.Code);
    }

    [Fact]
    public async Task Export_空页_仅表头并在口径表标注空页说明()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "opd-empty-x", "Priv");
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var file = ExportOk(await ctl.Export(new DynamicOrderProfitEstimateReportRequest
        {
            Fields = new List<string> { "orderNo" },
            Start = Start,
            End = End
        }));

        using var workbook = OpenWorkbook(file.FileContents);
        var data = workbook.GetSheetAt(0);
        Assert.Equal(0, data.LastRowNum);   // 仅表头行

        var ctx = workbook.GetSheetAt(1);
        Assert.Equal(DynamicOrderProfitEstimateReportRules.ContextEmptyLabel, ctx.GetRow(9).GetCell(0).StringCellValue);
        Assert.Equal(DynamicOrderProfitEstimateReportRules.EmptyText, ctx.GetRow(9).GetCell(1).StringCellValue);
    }

    [Fact]
    public async Task Export_只读_不写库()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "opd-ro-x", "Priv");
        var customer = SeedCustomer(db, "C001", "客户");
        SeedOrder(db, "SO-1", customer.Id, Currency.USD, 1000m);

        var before = db.SalesOrders.Count();
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        _ = ExportOk(await ctl.Export(new DynamicOrderProfitEstimateReportRequest
        {
            Fields = new List<string> { "orderNo" },
            Start = Start,
            End = End
        }));

        Assert.Equal(before, db.SalesOrders.Count());
        Assert.False(db.ChangeTracker.HasChanges());
    }
}
