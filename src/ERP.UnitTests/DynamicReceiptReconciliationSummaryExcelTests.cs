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
/// ERP-176 动态客户订单与收款核对报表「当前页金额汇总 Excel 导出」单元测试。
/// 覆盖：none 保留既有订单证据 + 未关联收款证据工作表（及选定计数分组工作表）、customerCurrency 追加「订单金额汇总」与
/// 「未关联收款金额汇总」两个独立工作表（客户 + 原币 + 收款证据状态 + 已知数值合计 + 未知空单元格与已知 / 未知行数 + 空页 / 截断显式提示 +
/// 公式前导标签转义）、多币种隔离、null 金额绝不回落 0、pending / historical 收款证据状态显式保留、无效金额汇总模式在源读取前拒绝、
/// 无销售订单菜单授权拒绝、以及只读不写库。
/// <para>全部使用内存数据库（TestDbFactory），不连接 SQL Server、不启动 API、不执行任何 SQL / seed。</para>
/// </summary>
public class DynamicReceiptReconciliationSummaryExcelTests
{
    private static readonly DateTime AsOf = new(2026, 9, 25);
    private const long CustomerA = 965001L;
    private const long ProductA = 965101L;

    private const string OrderSummarySheetName = "订单金额汇总";
    private const string ReceiptSummarySheetName = "未关联收款金额汇总";
    private const string CustomerColumn = "客户";
    private const string CurrencyColumn = "币种";
    private const string OrderCountColumn = "订单张数";
    private const string OrderAmountColumn = "订单金额";
    private const string KnownLinkedReceiptAmountRowsColumn = "已关联收款金额已知行数";
    private const string UnknownLinkedReceiptAmountRowsColumn = "已关联收款金额未知行数";
    private const string LinkedReceiptAmountColumn = "已关联收款金额";
    private const string KnownUncoveredAmountRowsColumn = "未覆盖金额已知行数";
    private const string UnknownUncoveredAmountRowsColumn = "未覆盖金额未知行数";
    private const string UncoveredAmountColumn = "未覆盖金额";
    private const string EvidenceStatusColumn = "收款证据状态";
    private const string ReceiptCountColumn = "收款张数";
    private const string ReceiptAmountColumn = "金额";
    private const string TruncatedColumn = "截断";
    private const string OrderSummaryEmptyNote = "本页没有可汇总金额的订单证据（空页）";
    private const string ReceiptSummaryEmptyNote = "本页没有可汇总金额的未关联收款证据（空页）";

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
        SeedRoleMenu(db, role.Id, SeedMenu(db, DynamicReceiptReconciliationReportRules.RequiredMenuCode).Id);
        return user.Id;
    }

    private static BaseCustomer SeedCustomer(ErpDbContext db, long id, string name)
    {
        var customer = new BaseCustomer
        {
            Id = id,
            CustomerCode = $"C{id}",
            CustomerName = name,
            Status = 1,
            CreditStatus = "正常",
        };
        db.BaseCustomers.Add(customer);
        db.SaveChanges();
        return customer;
    }

    private static SalesOrder SeedOrder(ErpDbContext db, string orderNo, long customerId, Currency currency,
        decimal totalAmount, DocumentStatus status = DocumentStatus.Approved)
    {
        var order = new SalesOrder
        {
            OrderNo = orderNo,
            OrderDate = AsOf.AddDays(-10),
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

    private static void SeedDepositApply(ErpDbContext db, string applyNo, long salesOrderId, decimal amount,
        Currency currency, DocumentStatus status, long customerId)
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

    private static FinanceReceipt SeedReceipt(ErpDbContext db, string receiptNo, long customerId, decimal amount,
        Currency currency, DocumentStatus status)
    {
        var receipt = new FinanceReceipt
        {
            ReceiptNo = receiptNo,
            ReceiptDate = AsOf.AddDays(-1),
            CustomerId = customerId,
            Amount = amount,
            Currency = currency,
            Status = status,
        };
        db.FinanceReceipts.Add(receipt);
        db.SaveChanges();
        return receipt;
    }


    private static DynamicReceiptReconciliationReportController NewController(ErpDbContext db) => new(db);

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


    // ==================== 1. none 保留既有工作表（不追加金额汇总工作表） ====================

    [Fact]
    public async Task Export_none_retains_evidence_sheets_without_summary_sheets()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "甲客户");
        SeedOrder(db, "SO-NONE", CustomerA, Currency.USD, 100m);
        SeedReceipt(db, "SK-NONE", CustomerA, 30m, Currency.USD, DocumentStatus.Approved);
        await db.SaveChangesAsync();

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var file = ExportOk(await ctl.Export(new DynamicReceiptReconciliationReportRequest
        {
            Fields = new() { "orderNo" },
            ReceiptFields = new() { "receiptNo" },
            SummaryMode = "none",
            GroupBy = "none",
            PageSize = 200,
        }));

        using var workbook = OpenWorkbook(file.FileContents);
        Assert.Equal(2, workbook.NumberOfSheets);
        Assert.Equal(DynamicReceiptReconciliationReportRules.OrderSheetName, workbook.GetSheetAt(0).SheetName);
        Assert.Equal(DynamicReceiptReconciliationReportRules.ReceiptSheetName, workbook.GetSheetAt(1).SheetName);
        Assert.Null(workbook.GetSheet(OrderSummarySheetName));
        Assert.Null(workbook.GetSheet(ReceiptSummarySheetName));
    }

    [Fact]
    public async Task Export_none_with_group_keeps_group_sheets_and_no_summary_sheets()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "甲客户");
        SeedOrder(db, "SO-G", CustomerA, Currency.USD, 100m);
        await db.SaveChangesAsync();

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var file = ExportOk(await ctl.Export(new DynamicReceiptReconciliationReportRequest
        {
            Fields = new() { "orderNo" },
            ReceiptFields = new() { "receiptNo" },
            SummaryMode = "none",
            GroupBy = "customer",
            PageSize = 200,
        }));

        using var workbook = OpenWorkbook(file.FileContents);
        Assert.Equal(4, workbook.NumberOfSheets);
        Assert.Equal(DynamicReceiptReconciliationReportRules.OrderSheetName, workbook.GetSheetAt(0).SheetName);
        Assert.Equal(DynamicReceiptReconciliationReportRules.ReceiptSheetName, workbook.GetSheetAt(1).SheetName);
        Assert.Equal("订单计数分组", workbook.GetSheetAt(2).SheetName);
        Assert.Equal("未关联收款计数分组", workbook.GetSheetAt(3).SheetName);
        Assert.Null(workbook.GetSheet(OrderSummarySheetName));
        Assert.Null(workbook.GetSheet(ReceiptSummarySheetName));
    }

    // ==================== 2. customerCurrency 追加两个独立金额汇总工作表 ====================

    [Fact]
    public async Task Export_customerCurrency_adds_order_and_receipt_summary_sheets()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "甲客户");
        SeedOrder(db, "SO-SUM", CustomerA, Currency.USD, 100m);
        SeedReceipt(db, "SK-SUM", CustomerA, 30m, Currency.USD, DocumentStatus.Approved);
        await db.SaveChangesAsync();

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var file = ExportOk(await ctl.Export(new DynamicReceiptReconciliationReportRequest
        {
            Fields = new() { "orderNo" },
            ReceiptFields = new() { "receiptNo" },
            SummaryMode = "customerCurrency",
            GroupBy = "none",
            PageSize = 200,
        }));

        using var workbook = OpenWorkbook(file.FileContents);
        Assert.Equal(4, workbook.NumberOfSheets);
        Assert.Equal(DynamicReceiptReconciliationReportRules.OrderSheetName, workbook.GetSheetAt(0).SheetName);
        Assert.Equal(DynamicReceiptReconciliationReportRules.ReceiptSheetName, workbook.GetSheetAt(1).SheetName);
        Assert.Equal(OrderSummarySheetName, workbook.GetSheetAt(2).SheetName);
        Assert.Equal(ReceiptSummarySheetName, workbook.GetSheetAt(3).SheetName);

        var orderSummary = RequireSheet(workbook, OrderSummarySheetName);
        Assert.Equal(CustomerColumn, Cell(orderSummary, 0, 0));
        Assert.Equal(CurrencyColumn, Cell(orderSummary, 0, 1));
        Assert.Equal(OrderCountColumn, Cell(orderSummary, 0, 2));
        Assert.Equal(OrderAmountColumn, Cell(orderSummary, 0, 3));
        Assert.Equal(KnownLinkedReceiptAmountRowsColumn, Cell(orderSummary, 0, 4));
        Assert.Equal(UnknownLinkedReceiptAmountRowsColumn, Cell(orderSummary, 0, 5));
        Assert.Equal(LinkedReceiptAmountColumn, Cell(orderSummary, 0, 6));
        Assert.Equal(KnownUncoveredAmountRowsColumn, Cell(orderSummary, 0, 7));
        Assert.Equal(UnknownUncoveredAmountRowsColumn, Cell(orderSummary, 0, 8));
        Assert.Equal(UncoveredAmountColumn, Cell(orderSummary, 0, 9));

        var receiptSummary = RequireSheet(workbook, ReceiptSummarySheetName);
        Assert.Equal(CustomerColumn, Cell(receiptSummary, 0, 0));
        Assert.Equal(CurrencyColumn, Cell(receiptSummary, 0, 1));
        Assert.Equal(EvidenceStatusColumn, Cell(receiptSummary, 0, 2));
        Assert.Equal(ReceiptCountColumn, Cell(receiptSummary, 0, 3));
        Assert.Equal(ReceiptAmountColumn, Cell(receiptSummary, 0, 4));
        Assert.Equal(TruncatedColumn, Cell(receiptSummary, 0, 5));
    }


    // ==================== 3. 无效金额汇总模式在源读取前拒绝 ====================

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

        // 无菜单授权 + 无效汇总模式：若模式校验晚于授权，这里会抛 Forbidden；先抛 InvalidParameter 证明校验先于源读取。
        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => ctl.Export(new DynamicReceiptReconciliationReportRequest
            {
                SummaryMode = "bogus",
                PageSize = 200,
            }));
    }

    // ==================== 4. 无销售订单菜单授权拒绝 ====================

    [Fact]
    public async Task Export_denied_access_without_sales_order_menu()
    {
        using var db = TestDbFactory.Create();
        var user = SeedUser(db, "nomenu");
        var role = SeedRole(db, "NoMenu");
        SeedUserRole(db, user.Id, role.Id);
        await db.SaveChangesAsync();

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        await AssertBusinessAsync(ErrorCodes.Forbidden,
            () => ctl.Export(new DynamicReceiptReconciliationReportRequest
            {
                SummaryMode = "customerCurrency",
                PageSize = 200,
            }));
    }

    // ==================== 5. 多币种隔离 ====================

    [Fact]
    public async Task Export_customerCurrency_keeps_multiple_currencies_separate()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "甲客户");
        SeedOrder(db, "SO-USD-1", CustomerA, Currency.USD, 100m);
        SeedOrder(db, "SO-USD-2", CustomerA, Currency.USD, 100m);
        SeedOrder(db, "SO-CNY-1", CustomerA, Currency.CNY, 300m);
        SeedReceipt(db, "SK-USD", CustomerA, 30m, Currency.USD, DocumentStatus.Approved);
        SeedReceipt(db, "SK-CNY", CustomerA, 40m, Currency.CNY, DocumentStatus.Approved);
        await db.SaveChangesAsync();

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var file = ExportOk(await ctl.Export(new DynamicReceiptReconciliationReportRequest
        {
            Fields = new() { "orderNo" },
            ReceiptFields = new() { "receiptNo" },
            SummaryMode = "customerCurrency",
            GroupBy = "none",
            PageSize = 200,
        }));

        using var workbook = OpenWorkbook(file.FileContents);
        var orderSummary = RequireSheet(workbook, OrderSummarySheetName);
        Assert.Equal(2, orderSummary.LastRowNum);

        var rows = new List<(string Currency, double Amount, double Count)>();
        for (var r = 1; r <= orderSummary.LastRowNum; r++)
            rows.Add((Cell(orderSummary, r, 1), Num(orderSummary, r, 3), Num(orderSummary, r, 2)));

        Assert.Contains(rows, r => r.Currency == "USD" && r.Amount == 200d && r.Count == 2d);
        Assert.Contains(rows, r => r.Currency == "CNY" && r.Amount == 300d && r.Count == 1d);

        var receiptSummary = RequireSheet(workbook, ReceiptSummarySheetName);
        Assert.Equal(2, receiptSummary.LastRowNum);
        var receiptRows = new List<(string Currency, double Amount)>();
        for (var r = 1; r <= receiptSummary.LastRowNum; r++)
            receiptRows.Add((Cell(receiptSummary, r, 1), Num(receiptSummary, r, 4)));
        Assert.Contains(receiptRows, r => r.Currency == "USD" && r.Amount == 30d);
        Assert.Contains(receiptRows, r => r.Currency == "CNY" && r.Amount == 40d);
    }


    // ==================== 6. 未知金额 null 保留为空（绝不回落 0） ====================

    [Fact]
    public async Task Export_customerCurrency_unknown_amounts_blank_with_known_unknown_counts()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "甲客户");
        SeedOrder(db, "SO-NULL", CustomerA, Currency.USD, 100m);
        await db.SaveChangesAsync();

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var file = ExportOk(await ctl.Export(new DynamicReceiptReconciliationReportRequest
        {
            Fields = new() { "orderNo" },
            ReceiptFields = new() { "receiptNo" },
            SummaryMode = "customerCurrency",
            PageSize = 200,
        }));

        using var workbook = OpenWorkbook(file.FileContents);
        var orderSummary = RequireSheet(workbook, OrderSummarySheetName);
        Assert.Equal("甲客户", Cell(orderSummary, 1, 0));
        Assert.Equal("USD", Cell(orderSummary, 1, 1));
        Assert.Equal(1d, Num(orderSummary, 1, 2));
        Assert.Equal(100d, Num(orderSummary, 1, 3));
        Assert.Equal(0d, Num(orderSummary, 1, 4));
        Assert.Equal(1d, Num(orderSummary, 1, 5));
        Assert.True(CellIsBlank(orderSummary, 1, 6), "未知已关联收款金额必须为空，绝不回落 0");
        Assert.Equal(0d, Num(orderSummary, 1, 7));
        Assert.Equal(1d, Num(orderSummary, 1, 8));
        Assert.True(CellIsBlank(orderSummary, 1, 9), "未知未覆盖金额必须为空，绝不回落 0");
    }

    // ==================== 7. 已知金额合计为数值 ====================

    [Fact]
    public async Task Export_customerCurrency_known_totals_are_numeric()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "甲客户");
        var order = SeedOrder(db, "SO-KNOWN", CustomerA, Currency.USD, 100m);
        SeedDetail(db, order.Id, ProductA, 1m);
        SeedDepositApply(db, "DJ-1", order.Id, 30m, Currency.USD, DocumentStatus.Approved, CustomerA);
        await db.SaveChangesAsync();

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var file = ExportOk(await ctl.Export(new DynamicReceiptReconciliationReportRequest
        {
            Fields = new() { "orderNo" },
            ReceiptFields = new() { "receiptNo" },
            SummaryMode = "customerCurrency",
            PageSize = 200,
        }));

        using var workbook = OpenWorkbook(file.FileContents);
        var orderSummary = RequireSheet(workbook, OrderSummarySheetName);
        Assert.Equal(1d, Num(orderSummary, 1, 4));
        Assert.Equal(0d, Num(orderSummary, 1, 5));
        Assert.Equal(30d, Num(orderSummary, 1, 6));
        Assert.Equal(1d, Num(orderSummary, 1, 7));
        Assert.Equal(0d, Num(orderSummary, 1, 8));
        Assert.Equal(70d, Num(orderSummary, 1, 9));
    }

    // ==================== 8. pending / historical 收款证据状态显式保留 ====================

    [Fact]
    public async Task Export_customerCurrency_separates_pending_and_historical_receipts()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "甲客户");
        SeedOrder(db, "SO-EV", CustomerA, Currency.USD, 100m);
        SeedReceipt(db, "SK-ACT", CustomerA, 10m, Currency.USD, DocumentStatus.Approved);
        SeedReceipt(db, "SK-PEND", CustomerA, 20m, Currency.USD, DocumentStatus.Pending);
        SeedReceipt(db, "SK-HIST", CustomerA, 30m, Currency.USD, DocumentStatus.Cancelled);
        await db.SaveChangesAsync();

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var file = ExportOk(await ctl.Export(new DynamicReceiptReconciliationReportRequest
        {
            Fields = new() { "orderNo" },
            ReceiptFields = new() { "receiptNo" },
            SummaryMode = "customerCurrency",
            ReceiptStatus = "all",
            PageSize = 200,
        }));

        using var workbook = OpenWorkbook(file.FileContents);
        var receiptSummary = RequireSheet(workbook, ReceiptSummarySheetName);
        Assert.Equal(3, receiptSummary.LastRowNum);

        var rows = new List<(string Status, double Amount)>();
        for (var r = 1; r <= receiptSummary.LastRowNum; r++)
            rows.Add((Cell(receiptSummary, r, 2), Num(receiptSummary, r, 4)));

        Assert.Contains(rows, r => r.Status == "active" && r.Amount == 10d);
        Assert.Contains(rows, r => r.Status == "pending" && r.Amount == 20d);
        Assert.Contains(rows, r => r.Status == "historical" && r.Amount == 30d);
    }


    // ==================== 9. 空页显式提示 ====================

    [Fact]
    public async Task Export_customerCurrency_empty_page_shows_explicit_notes()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var file = ExportOk(await ctl.Export(new DynamicReceiptReconciliationReportRequest
        {
            Fields = new() { "orderNo" },
            ReceiptFields = new() { "receiptNo" },
            SummaryMode = "customerCurrency",
            PageSize = 200,
        }));

        using var workbook = OpenWorkbook(file.FileContents);
        var orderSummary = RequireSheet(workbook, OrderSummarySheetName);
        Assert.Equal(OrderSummaryEmptyNote, Cell(orderSummary, 1, 0));

        var receiptSummary = RequireSheet(workbook, ReceiptSummarySheetName);
        Assert.Equal(ReceiptSummaryEmptyNote, Cell(receiptSummary, 1, 0));
    }

    // ==================== 10. 截断显式保留 ====================

    [Fact]
    public async Task Export_customerCurrency_marks_truncated_receipt_summary()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "甲客户");
        SeedOrder(db, "SO-TRUNC", CustomerA, Currency.USD, 100m);
        for (var i = 0; i < SalesOrderReceiptReconciliation.UnlinkedReceiptLimit; i++)
        {
            db.FinanceReceipts.Add(new FinanceReceipt
            {
                ReceiptNo = $"SK-CAP-{i}",
                ReceiptDate = AsOf.AddDays(-1),
                CustomerId = CustomerA,
                Amount = 1m,
                Currency = Currency.USD,
                Status = DocumentStatus.Approved,
            });
        }
        await db.SaveChangesAsync();

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var file = ExportOk(await ctl.Export(new DynamicReceiptReconciliationReportRequest
        {
            Fields = new() { "orderNo" },
            ReceiptFields = new() { "receiptNo" },
            SummaryMode = "customerCurrency",
            PageSize = 200,
        }));

        using var workbook = OpenWorkbook(file.FileContents);
        var receiptSummary = RequireSheet(workbook, ReceiptSummarySheetName);
        Assert.Equal("active", Cell(receiptSummary, 1, 2));
        Assert.Equal((double)SalesOrderReceiptReconciliation.UnlinkedReceiptLimit, Num(receiptSummary, 1, 3));
        Assert.Equal("是", Cell(receiptSummary, 1, 5));
        Assert.Equal(DynamicReceiptReconciliationReportRules.ReceiptTruncationNote, Cell(receiptSummary, 2, 0));
    }

    // ==================== 11. 公式前导标签转义 ====================

    [Fact]
    public async Task Export_customerCurrency_escapes_formula_leading_labels()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "=1+1");
        SeedOrder(db, "SO-FORMULA", CustomerA, Currency.USD, 100m);
        SeedReceipt(db, "SK-FORMULA", CustomerA, 30m, Currency.USD, DocumentStatus.Approved);
        await db.SaveChangesAsync();

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var file = ExportOk(await ctl.Export(new DynamicReceiptReconciliationReportRequest
        {
            Fields = new() { "customerName" },
            ReceiptFields = new() { "receiptNo" },
            SummaryMode = "customerCurrency",
            PageSize = 200,
        }));

        using var workbook = OpenWorkbook(file.FileContents);
        var orderSummary = RequireSheet(workbook, OrderSummarySheetName);
        Assert.Equal("'=1+1", Cell(orderSummary, 1, 0));

        var receiptSummary = RequireSheet(workbook, ReceiptSummarySheetName);
        Assert.Equal("'=1+1", Cell(receiptSummary, 1, 0));
    }

    // ==================== 12. 只读不写库 ====================

    [Fact]
    public async Task Export_customerCurrency_does_not_write_to_database()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "只读客户");
        SeedOrder(db, "SO-RO", CustomerA, Currency.USD, 100m);
        SeedReceipt(db, "SK-RO", CustomerA, 30m, Currency.USD, DocumentStatus.Approved);
        await db.SaveChangesAsync();

        var counting = CountingDbContext.Wrap(db);
        var ctl = new DynamicReceiptReconciliationReportController(counting.Proxy);
        TestAuth.SetUser(ctl, uid);

        var file = ExportOk(await ctl.Export(new DynamicReceiptReconciliationReportRequest
        {
            Fields = new() { "orderNo" },
            ReceiptFields = new() { "receiptNo" },
            SummaryMode = "customerCurrency",
            PageSize = 200,
        }));

        Assert.NotEmpty(file.FileContents);
        Assert.Equal(0, counting.WriteCalls);
    }

    // ==================== 13. 只读计数上下文（断言不写库） ====================

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
