using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NPOI.SS.UserModel;
using NPOI.XSSF.UserModel;
using System.Reflection;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-178 动态销售订单出货 / 财务进度报表「当前页金额汇总 Excel 导出」单元测试。
/// 覆盖：none 保留既有选定列数据工作表；customerCurrency / customerCurrencyShipment / customerCurrencyFinance 追加独立金额汇总工作表
/// （客户 + 原币 + 可选出货状态 / 收款链接状态 + 数值已知合计 + 未知空单元格与已知 / 未知行数 + 空页显式提示 + 公式前导标签转义）、
/// 多币种隔离、null 金额绝不回落 0、无效金额汇总模式在源读取前拒绝、无销售订单菜单授权拒绝、受限制业务员范围过滤、以及只读不写库。
/// <para>全部使用内存数据库（TestDbFactory），不连接 SQL Server、不启动 API、不执行任何 SQL / seed。</para>
/// </summary>
public class DynamicShipmentFinanceSummaryExcelTests
{
    private static readonly DateTime AsOf = new(2026, 9, 24);
    private const long CustomerA = 958001L;
    private const long CustomerB = 958002L;
    private const long ProductA = 958101L;

    private const string DataSheetName = "销售订单出货财务进度";
    private const string AmountSummarySheetName = "金额汇总";
    private const string CustomerColumn = "客户";
    private const string CurrencyColumn = "币种";
    private const string ShipmentStatusColumn = "出货状态";
    private const string FinanceLinkStatusColumn = "收款链接状态";
    private const string OrderCountColumn = "订单张数";
    private const string OrderAmountColumn = "订单金额";
    private const string KnownLinkedAmountRowsColumn = "已关联金额已知行数";
    private const string UnknownLinkedAmountRowsColumn = "已关联金额未知行数";
    private const string LinkedAmountColumn = "已关联金额";
    private const string KnownUncoveredAmountRowsColumn = "未覆盖金额已知行数";
    private const string UnknownUncoveredAmountRowsColumn = "未覆盖金额未知行数";
    private const string UncoveredAmountColumn = "未覆盖金额";
    private const string KnownSubmittedAmountRowsColumn = "已提交金额已知行数";
    private const string UnknownSubmittedAmountRowsColumn = "已提交金额未知行数";
    private const string SubmittedAmountColumn = "已提交金额";
    private const string AmountSummaryEmptyNote = "本页没有可汇总金额的订单出货 / 财务证据（空页）";

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

    private static long SeedPrivilegedUser(ErpDbContext db, string userName = "priv")
    {
        var user = SeedUser(db, userName);
        var role = SeedRole(db, $"Role-{userName}", isSystem: true);
        SeedUserRole(db, user.Id, role.Id);
        SeedRoleMenu(db, role.Id, SeedMenu(db, DynamicShipmentFinanceReportRules.RequiredMenuCode).Id);
        return user.Id;
    }

    private static BaseEmployee SeedEmployee(ErpDbContext db, string code)
    {
        var employee = new BaseEmployee { EmployeeCode = code, EmployeeName = code, IsSalesman = true, Status = 1 };
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

    private static void SeedStockOut(ErpDbContext db, string stockOutNo, long? salesOrderId, long customerId,
        DocumentStatus status, params (long ProductId, decimal Quantity)[] lines)
    {
        var stockOut = new StockOut
        {
            StockOutNo = stockOutNo,
            StockOutDate = AsOf.AddDays(-5),
            SalesOrderId = salesOrderId,
            CustomerId = customerId,
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

    private static void SeedDepositApply(ErpDbContext db, string applyNo, long salesOrderId, long customerId,
        decimal amount, Currency currency, DocumentStatus status)
    {
        db.FinanceDepositApplies.Add(new FinanceDepositApply
        {
            ApplyNo = applyNo,
            ApplyDate = AsOf.AddDays(-3),
            SalesOrderId = salesOrderId,
            CustomerId = customerId,
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
        using var stream = new MemoryStream(bytes);
        return new XSSFWorkbook(stream);
    }

    private static ISheet RequireSheet(XSSFWorkbook workbook, string name)
    {
        var sheet = workbook.GetSheet(name);
        Assert.NotNull(sheet);
        return sheet;
    }

    private static async Task<BusinessException> AssertBusinessAsync(int expectedCode, Func<Task> action)
    {
        var ex = await Assert.ThrowsAsync<BusinessException>(action);
        Assert.Equal(expectedCode, ex.Code);
        return ex;
    }

    private static string Cell(ISheet sheet, int row, int col)
        => sheet.GetRow(row)?.GetCell(col)?.StringCellValue ?? string.Empty;

    private static double Num(ISheet sheet, int row, int col)
        => sheet.GetRow(row).GetCell(col).NumericCellValue;

    private static bool CellIsBlank(ISheet sheet, int row, int col)
    {
        var cell = sheet.GetRow(row)?.GetCell(col);
        return cell is null || cell.CellType == CellType.Blank || string.IsNullOrEmpty(cell.StringCellValue);
    }

    // ==================== 1. none 保留既有选定列数据工作表（不追加金额汇总工作表） ====================

    [Fact]
    public async Task Export_none_retains_existing_data_sheet_without_summary_sheet()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "甲客户");
        SeedOrder(db, "SO-NONE", CustomerA, Currency.USD, 100m);
        await db.SaveChangesAsync();

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var file = ExportOk(await ctl.Export(new DynamicShipmentFinanceReportRequest
        {
            Fields = new() { "orderNo" },
            SummaryMode = "none",
            PageSize = 200,
        }));

        using var workbook = OpenWorkbook(file.FileContents);
        Assert.Equal(1, workbook.NumberOfSheets);
        Assert.Equal(DataSheetName, workbook.GetSheetAt(0).SheetName);
        Assert.Null(workbook.GetSheet(AmountSummarySheetName));

        var data = workbook.GetSheetAt(0);
        Assert.Equal("订单号", Cell(data, 0, 0));
        Assert.Equal("SO-NONE", Cell(data, 1, 0));
    }

    // ==================== 2. customerCurrency 追加独立金额汇总工作表 ====================

    [Fact]
    public async Task Export_customerCurrency_adds_summary_sheet_with_numeric_known_totals()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "甲客户");
        var order = SeedOrder(db, "SO-SUM", CustomerA, Currency.USD, 1000m);
        SeedDepositApply(db, "DEP-SUM", order.Id, CustomerA, 300m, Currency.USD, DocumentStatus.Approved);
        await db.SaveChangesAsync();

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var file = ExportOk(await ctl.Export(new DynamicShipmentFinanceReportRequest
        {
            Fields = new() { "orderNo" },
            SummaryMode = "customerCurrency",
            PageSize = 200,
        }));

        using var workbook = OpenWorkbook(file.FileContents);
        Assert.Equal(2, workbook.NumberOfSheets);
        Assert.Equal(DataSheetName, workbook.GetSheetAt(0).SheetName);
        Assert.Equal(AmountSummarySheetName, workbook.GetSheetAt(1).SheetName);

        var summary = RequireSheet(workbook, AmountSummarySheetName);
        Assert.Equal(CustomerColumn, Cell(summary, 0, 0));
        Assert.Equal(CurrencyColumn, Cell(summary, 0, 1));
        Assert.Equal(OrderCountColumn, Cell(summary, 0, 2));
        Assert.Equal(OrderAmountColumn, Cell(summary, 0, 3));
        Assert.Equal(KnownLinkedAmountRowsColumn, Cell(summary, 0, 4));
        Assert.Equal(UnknownLinkedAmountRowsColumn, Cell(summary, 0, 5));
        Assert.Equal(LinkedAmountColumn, Cell(summary, 0, 6));
        Assert.Equal(KnownUncoveredAmountRowsColumn, Cell(summary, 0, 7));
        Assert.Equal(UnknownUncoveredAmountRowsColumn, Cell(summary, 0, 8));
        Assert.Equal(UncoveredAmountColumn, Cell(summary, 0, 9));
        Assert.Equal(KnownSubmittedAmountRowsColumn, Cell(summary, 0, 10));
        Assert.Equal(UnknownSubmittedAmountRowsColumn, Cell(summary, 0, 11));
        Assert.Equal(SubmittedAmountColumn, Cell(summary, 0, 12));

        Assert.Equal("甲客户", Cell(summary, 1, 0));
        Assert.Equal("USD", Cell(summary, 1, 1));
        Assert.Equal(1d, Num(summary, 1, 2));
        Assert.Equal(1000d, Num(summary, 1, 3));
        Assert.Equal(1d, Num(summary, 1, 4));
        Assert.Equal(0d, Num(summary, 1, 5));
        Assert.Equal(300d, Num(summary, 1, 6));
        Assert.Equal(1d, Num(summary, 1, 7));
        Assert.Equal(0d, Num(summary, 1, 8));
        Assert.Equal(700d, Num(summary, 1, 9));
        Assert.Equal(1d, Num(summary, 1, 10));
        Assert.Equal(0d, Num(summary, 1, 11));
        Assert.Equal(0d, Num(summary, 1, 12));
    }

    // ==================== 3. customerCurrencyShipment 追加出货状态列 ====================

    [Fact]
    public async Task Export_customerCurrencyShipment_adds_shipment_status_column()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "甲客户");

        var unshipped = SeedOrder(db, "SO-SH-1", CustomerA, Currency.USD, 1000m);
        SeedDepositApply(db, "DEP-SH-1", unshipped.Id, CustomerA, 100m, Currency.USD, DocumentStatus.Approved);

        var shipped = SeedOrder(db, "SO-SH-2", CustomerA, Currency.USD, 500m);
        SeedDepositApply(db, "DEP-SH-2", shipped.Id, CustomerA, 50m, Currency.USD, DocumentStatus.Approved);
        SeedDetail(db, shipped.Id, ProductA, 5m);
        SeedStockOut(db, "CK-SH-1", shipped.Id, CustomerA, DocumentStatus.Approved, (ProductA, 5m));
        await db.SaveChangesAsync();

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var file = ExportOk(await ctl.Export(new DynamicShipmentFinanceReportRequest
        {
            Fields = new() { "orderNo" },
            SummaryMode = "customerCurrencyShipment",
            PageSize = 200,
        }));

        using var workbook = OpenWorkbook(file.FileContents);
        var summary = RequireSheet(workbook, AmountSummarySheetName);
        Assert.Equal(ShipmentStatusColumn, Cell(summary, 0, 2));
        Assert.Equal(2, summary.LastRowNum);

        var rows = new List<(string Status, double Amount)>();
        for (var r = 1; r <= summary.LastRowNum; r++)
            rows.Add((Cell(summary, r, 2), Num(summary, r, 4)));

        Assert.Contains(rows, x => x.Status == "未出货" && x.Amount == 1000d);
        Assert.Contains(rows, x => x.Status == "已出齐" && x.Amount == 500d);
    }

    // ==================== 4. customerCurrencyFinance 追加收款链接状态列 ====================

    [Fact]
    public async Task Export_customerCurrencyFinance_adds_finance_link_status_column()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "甲客户");

        var linked = SeedOrder(db, "SO-FN-1", CustomerA, Currency.USD, 1000m);
        SeedDepositApply(db, "DEP-FN-1", linked.Id, CustomerA, 300m, Currency.USD, DocumentStatus.Approved);
        SeedOrder(db, "SO-FN-2", CustomerA, Currency.USD, 500m); // 未链接
        await db.SaveChangesAsync();

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var file = ExportOk(await ctl.Export(new DynamicShipmentFinanceReportRequest
        {
            Fields = new() { "orderNo" },
            SummaryMode = "customerCurrencyFinance",
            PageSize = 200,
        }));

        using var workbook = OpenWorkbook(file.FileContents);
        var summary = RequireSheet(workbook, AmountSummarySheetName);
        Assert.Equal(FinanceLinkStatusColumn, Cell(summary, 0, 2));
        Assert.Equal(2, summary.LastRowNum);

        var linkedRowIdx = -1;
        var unlinkedRowIdx = -1;
        for (var r = 1; r <= summary.LastRowNum; r++)
        {
            var status = Cell(summary, r, 2);
            if (status == "收款引用完整") linkedRowIdx = r;
            if (status == "未链接（金额未知）") unlinkedRowIdx = r;
        }

        Assert.True(linkedRowIdx > 0);
        Assert.True(unlinkedRowIdx > 0);

        Assert.Equal(300d, Num(summary, linkedRowIdx, 7));
        Assert.Equal(1d, Num(summary, linkedRowIdx, 5));
        Assert.Equal(0d, Num(summary, linkedRowIdx, 6));

        Assert.Equal(0d, Num(summary, unlinkedRowIdx, 5));
        Assert.Equal(1d, Num(summary, unlinkedRowIdx, 6));
        Assert.True(CellIsBlank(summary, unlinkedRowIdx, 7));
    }

    // ==================== 5. 无效金额汇总模式在源读取前拒绝 ====================

    [Fact]
    public async Task Export_invalid_summary_mode_rejected_before_source_reads()
    {
        using var db = TestDbFactory.Create();
        var user = SeedUser(db, "nomenu-invalid");
        var role = SeedRole(db, "NoMenuInvalid");
        SeedUserRole(db, user.Id, role.Id);
        await db.SaveChangesAsync();

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        // 无菜单授权 + 无效汇总模式：若模式校验晚于源读取会抛 Forbidden，先抛 InvalidParameter 证明校验先于源读取（且先于授权）。
        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => ctl.Export(new DynamicShipmentFinanceReportRequest
            {
                SummaryMode = "grandTotal",
                PageSize = 200,
            }));
    }

    // ==================== 6. 无销售订单菜单授权拒绝 ====================

    [Fact]
    public async Task Export_denied_without_sales_order_menu()
    {
        using var db = TestDbFactory.Create();
        var user = SeedUser(db, "nomenu");
        var role = SeedRole(db, "NoMenu");
        SeedUserRole(db, user.Id, role.Id);
        await db.SaveChangesAsync();

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        await AssertBusinessAsync(ErrorCodes.Forbidden,
            () => ctl.Export(new DynamicShipmentFinanceReportRequest
            {
                SummaryMode = "customerCurrency",
                PageSize = 200,
            }));
    }

    [Fact]
    public async Task Export_denied_without_identity()
    {
        using var db = TestDbFactory.Create();
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, null);

        await AssertBusinessAsync(ErrorCodes.Unauthorized,
            () => ctl.Export(new DynamicShipmentFinanceReportRequest
            {
                SummaryMode = "customerCurrency",
                PageSize = 200,
            }));
    }

    // ==================== 7. 受限制业务员范围过滤 ====================

    [Fact]
    public async Task Export_scopes_restricted_salesperson_to_assigned_customers()
    {
        using var db = TestDbFactory.Create();
        var user = SeedUser(db, "alice");
        var role = SeedRole(db, "Sales");
        SeedUserRole(db, user.Id, role.Id);
        SeedRoleMenu(db, role.Id, SeedMenu(db, DynamicShipmentFinanceReportRules.RequiredMenuCode).Id);
        var alice = SeedEmployee(db, "alice");
        var mine = SeedCustomer(db, CustomerA, "我的客户", alice.Id);
        SeedCustomer(db, CustomerB, "别人的客户", alice.Id + 1000);

        var mineOrder = SeedOrder(db, "SO-SC-1", mine.Id, Currency.USD, 100m);
        SeedDepositApply(db, "DEP-SC-1", mineOrder.Id, mine.Id, 100m, Currency.USD, DocumentStatus.Approved);
        SeedOrder(db, "SO-SC-2", CustomerB, Currency.USD, 300m);
        await db.SaveChangesAsync();

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var file = ExportOk(await ctl.Export(new DynamicShipmentFinanceReportRequest
        {
            Fields = new() { "orderNo" },
            SummaryMode = "customerCurrency",
            PageSize = 200,
        }));

        using var workbook = OpenWorkbook(file.FileContents);
        var summary = RequireSheet(workbook, AmountSummarySheetName);
        Assert.Equal(1, summary.LastRowNum);
        Assert.Equal("我的客户", Cell(summary, 1, 0));
        Assert.Equal(100d, Num(summary, 1, 3));
    }

    // ==================== 8. 多币种隔离（绝不跨币种合并 / 换算，无应收 / 合计） ====================

    [Fact]
    public async Task Export_customerCurrency_keeps_multiple_currencies_separate_without_totals()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "甲客户");
        SeedOrder(db, "SO-USD", CustomerA, Currency.USD, 100m);
        SeedOrder(db, "SO-CNY", CustomerA, Currency.CNY, 300m);
        await db.SaveChangesAsync();

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var file = ExportOk(await ctl.Export(new DynamicShipmentFinanceReportRequest
        {
            Fields = new() { "orderNo" },
            SummaryMode = "customerCurrency",
            PageSize = 200,
        }));

        using var workbook = OpenWorkbook(file.FileContents);
        var summary = RequireSheet(workbook, AmountSummarySheetName);

        // 表头不包含应收 / 余额 / 账龄 / 合计 / 换算等跨币种或应收口径列。
        for (var c = 0; c <= summary.GetRow(0).LastCellNum; c++)
        {
            var header = Cell(summary, 0, c);
            Assert.DoesNotContain("应收", header);
            Assert.DoesNotContain("余额", header);
            Assert.DoesNotContain("账龄", header);
            Assert.DoesNotContain("合计", header);
            Assert.DoesNotContain("换算", header);
        }

        Assert.Equal(2, summary.LastRowNum);

        var rows = new List<(string Currency, double Amount, double Count)>();
        for (var r = 1; r <= summary.LastRowNum; r++)
            rows.Add((Cell(summary, r, 1), Num(summary, r, 3), Num(summary, r, 2)));

        Assert.Contains(rows, x => x.Currency == "USD" && x.Amount == 100d && x.Count == 1d);
        Assert.Contains(rows, x => x.Currency == "CNY" && x.Amount == 300d && x.Count == 1d);
    }

    // ==================== 9. 未知金额 null 保留为空（绝不回落 0） ====================

    [Fact]
    public async Task Export_unknown_amounts_blank_with_known_unknown_counts()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "甲客户");
        SeedOrder(db, "SO-NULL", CustomerA, Currency.USD, 1000m); // 未链接：金额未知
        await db.SaveChangesAsync();

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var file = ExportOk(await ctl.Export(new DynamicShipmentFinanceReportRequest
        {
            Fields = new() { "orderNo" },
            SummaryMode = "customerCurrency",
            PageSize = 200,
        }));

        using var workbook = OpenWorkbook(file.FileContents);
        var summary = RequireSheet(workbook, AmountSummarySheetName);
        Assert.Equal(1, summary.LastRowNum);

        Assert.Equal(1000d, Num(summary, 1, 3));     // 订单金额已知
        Assert.Equal(0d, Num(summary, 1, 4));        // 已关联金额已知行数
        Assert.Equal(1d, Num(summary, 1, 5));        // 已关联金额未知行数
        Assert.True(CellIsBlank(summary, 1, 6));     // 已关联金额未知 → 空
        Assert.Equal(0d, Num(summary, 1, 7));
        Assert.Equal(1d, Num(summary, 1, 8));
        Assert.True(CellIsBlank(summary, 1, 9));     // 未覆盖金额未知 → 空
        Assert.Equal(0d, Num(summary, 1, 10));
        Assert.Equal(1d, Num(summary, 1, 11));
        Assert.True(CellIsBlank(summary, 1, 12));    // 已提交金额未知 → 空
    }

    // ==================== 10. 空页显式提示 ====================

    [Fact]
    public async Task Export_empty_page_shows_explicit_note()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var file = ExportOk(await ctl.Export(new DynamicShipmentFinanceReportRequest
        {
            Fields = new() { "orderNo" },
            SummaryMode = "customerCurrency",
            PageSize = 200,
        }));

        using var workbook = OpenWorkbook(file.FileContents);
        Assert.Equal(2, workbook.NumberOfSheets);
        var summary = RequireSheet(workbook, AmountSummarySheetName);
        Assert.Equal(AmountSummaryEmptyNote, Cell(summary, 1, 0));
    }

    // ==================== 11. 分页只汇总当前页 ====================

    [Fact]
    public async Task Export_paged_results_only_count_current_page()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "甲客户");
        for (var i = 1; i <= 3; i++)
        {
            var order = SeedOrder(db, $"SO-PG-{i}", CustomerA, Currency.USD, 100m);
            SeedDepositApply(db, $"DEP-PG-{i}", order.Id, CustomerA, 100m, Currency.USD, DocumentStatus.Approved);
        }

        await db.SaveChangesAsync();

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var page1 = ExportOk(await ctl.Export(new DynamicShipmentFinanceReportRequest
        {
            Fields = new() { "orderNo" },
            SummaryMode = "customerCurrency",
            Page = 1,
            PageSize = 2,
        }));
        var page2 = ExportOk(await ctl.Export(new DynamicShipmentFinanceReportRequest
        {
            Fields = new() { "orderNo" },
            SummaryMode = "customerCurrency",
            Page = 2,
            PageSize = 2,
        }));

        using var wb1 = OpenWorkbook(page1.FileContents);
        using var wb2 = OpenWorkbook(page2.FileContents);
        var s1 = RequireSheet(wb1, AmountSummarySheetName);
        var s2 = RequireSheet(wb2, AmountSummarySheetName);

        Assert.Equal(1, s1.LastRowNum);
        Assert.Equal(2d, Num(s1, 1, 2));
        Assert.Equal(200d, Num(s1, 1, 3));

        Assert.Equal(1, s2.LastRowNum);
        Assert.Equal(1d, Num(s2, 1, 2));
        Assert.Equal(100d, Num(s2, 1, 3));
    }

    // ==================== 12. 公式前导标签转义 ====================

    [Fact]
    public async Task Export_escapes_formula_leading_labels()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "=1+1");
        SeedOrder(db, "SO-FORMULA", CustomerA, Currency.USD, 100m);
        await db.SaveChangesAsync();

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var file = ExportOk(await ctl.Export(new DynamicShipmentFinanceReportRequest
        {
            Fields = new() { "customerName" },
            SummaryMode = "customerCurrency",
            PageSize = 200,
        }));

        using var workbook = OpenWorkbook(file.FileContents);
        var summary = RequireSheet(workbook, AmountSummarySheetName);
        Assert.Equal("'=1+1", Cell(summary, 1, 0));
    }

    // ==================== 13. 只读不写库 ====================

    [Fact]
    public async Task Export_customerCurrency_does_not_write_to_database()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "只读客户");
        var order = SeedOrder(db, "SO-RO", CustomerA, Currency.USD, 100m);
        SeedDepositApply(db, "DEP-RO", order.Id, CustomerA, 30m, Currency.USD, DocumentStatus.Approved);
        await db.SaveChangesAsync();

        var counting = CountingDbContext.Wrap(db);
        var ctl = new DynamicShipmentFinanceReportController(counting.Proxy);
        TestAuth.SetUser(ctl, uid);

        var file = ExportOk(await ctl.Export(new DynamicShipmentFinanceReportRequest
        {
            Fields = new() { "orderNo" },
            SummaryMode = "customerCurrency",
            PageSize = 200,
        }));

        Assert.NotEmpty(file.FileContents);
        Assert.Equal(0, counting.WriteCalls);
    }

    // ==================== 14. 只读计数上下文（断言不写库） ====================

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
