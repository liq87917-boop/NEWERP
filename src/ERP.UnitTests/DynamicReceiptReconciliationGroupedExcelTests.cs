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
/// ERP-174 动态客户订单与收款核对报表「当前页计数分组 Excel 导出」单元测试。
/// 覆盖：none 模式保留原有两表、支持分组模式追加「订单计数分组」与「未关联收款计数分组」两个独立工作表（当前页标签 + 数值计数、
/// 不适用 / 空 / 截断状态显式保留、标签公式注入转义、无金额合计、绝不推断匹配）、未知分组键在源读取前拒绝、无销售订单菜单授权拒绝、
/// 以及只读不写库。
/// <para>全部使用内存数据库（TestDbFactory），不连接 SQL Server、不启动 API、不执行任何 SQL / seed。</para>
/// </summary>
public class DynamicReceiptReconciliationGroupedExcelTests
{
    private static readonly DateTime AsOf = new(2026, 9, 25);
    private const long CustomerA = 966001L;
    private const long CustomerB = 966002L;

    private const string OrderGroupSheetName = "订单计数分组";
    private const string ReceiptGroupSheetName = "未关联收款计数分组";
    private const string GroupLabelColumn = "分组标签";
    private const string OrderCountColumn = "订单张数";
    private const string ReceiptCountColumn = "收款张数";
    private const string TruncatedColumn = "截断";
    private const string OrderGroupEmptyNote = "无订单计数分组（不适用或当前页为空）";
    private const string ReceiptGroupEmptyNote = "无未关联收款计数分组（不适用或当前页为空）";

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

    // ==================== 1. none 模式保留原有两表 ====================

    [Fact]
    public async Task Export_none_retains_original_two_sheet_workbook()
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
            GroupBy = "none",
            PageSize = 200,
        }));

        using var workbook = OpenWorkbook(file.FileContents);
        Assert.Equal(2, workbook.NumberOfSheets);
        Assert.Equal(DynamicReceiptReconciliationReportRules.OrderSheetName, workbook.GetSheetAt(0).SheetName);
        Assert.Equal(DynamicReceiptReconciliationReportRules.ReceiptSheetName, workbook.GetSheetAt(1).SheetName);
    }

    // ==================== 2. 支持分组模式：两个独立计数工作表 ====================

    [Fact]
    public async Task Export_customer_group_adds_order_and_receipt_count_sheets_with_numeric_counts()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "甲客户");
        SeedCustomer(db, CustomerB, "乙客户");
        SeedOrder(db, "SO-A-1", CustomerA, Currency.USD, 100m);
        SeedOrder(db, "SO-A-2", CustomerA, Currency.USD, 200m);
        SeedOrder(db, "SO-B-1", CustomerB, Currency.CNY, 300m);
        SeedReceipt(db, "SK-A-1", CustomerA, 30m, Currency.USD, DocumentStatus.Approved);
        SeedReceipt(db, "SK-B-1", CustomerB, 40m, Currency.CNY, DocumentStatus.Approved);
        await db.SaveChangesAsync();

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var file = ExportOk(await ctl.Export(new DynamicReceiptReconciliationReportRequest
        {
            Fields = new() { "orderNo" },
            ReceiptFields = new() { "receiptNo" },
            GroupBy = "customer",
            PageSize = 200,
        }));

        using var workbook = OpenWorkbook(file.FileContents);
        Assert.Equal(4, workbook.NumberOfSheets);
        Assert.Equal(DynamicReceiptReconciliationReportRules.OrderSheetName, workbook.GetSheetAt(0).SheetName);
        Assert.Equal(DynamicReceiptReconciliationReportRules.ReceiptSheetName, workbook.GetSheetAt(1).SheetName);

        var orderGroupSheet = RequireSheet(workbook, OrderGroupSheetName);
        Assert.Equal(GroupLabelColumn, orderGroupSheet.GetRow(0).GetCell(0).StringCellValue);
        Assert.Equal(OrderCountColumn, orderGroupSheet.GetRow(0).GetCell(1).StringCellValue);
        Assert.Equal(2, orderGroupSheet.LastRowNum);
        Assert.Equal("甲客户", orderGroupSheet.GetRow(1).GetCell(0).StringCellValue);
        Assert.Equal(2d, orderGroupSheet.GetRow(1).GetCell(1).NumericCellValue);
        Assert.Equal("乙客户", orderGroupSheet.GetRow(2).GetCell(0).StringCellValue);
        Assert.Equal(1d, orderGroupSheet.GetRow(2).GetCell(1).NumericCellValue);

        var receiptGroupSheet = RequireSheet(workbook, ReceiptGroupSheetName);
        Assert.Equal(GroupLabelColumn, receiptGroupSheet.GetRow(0).GetCell(0).StringCellValue);
        Assert.Equal(ReceiptCountColumn, receiptGroupSheet.GetRow(0).GetCell(1).StringCellValue);
        Assert.Equal(TruncatedColumn, receiptGroupSheet.GetRow(0).GetCell(2).StringCellValue);
        Assert.Equal(2, receiptGroupSheet.LastRowNum);
        Assert.Equal("甲客户", receiptGroupSheet.GetRow(1).GetCell(0).StringCellValue);
        Assert.Equal(1d, receiptGroupSheet.GetRow(1).GetCell(1).NumericCellValue);
        Assert.Equal("否", receiptGroupSheet.GetRow(1).GetCell(2).StringCellValue);
        Assert.Equal("乙客户", receiptGroupSheet.GetRow(2).GetCell(0).StringCellValue);
        Assert.Equal(1d, receiptGroupSheet.GetRow(2).GetCell(1).NumericCellValue);
        Assert.Equal("否", receiptGroupSheet.GetRow(2).GetCell(2).StringCellValue);
    }


    // ==================== 3. 收款覆盖状态分组：订单侧适用、未关联收款侧不适用 ====================

    [Fact]
    public async Task Export_receiptCoverageStatus_group_orders_applicable_receipts_inapplicable()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "甲客户");
        SeedOrder(db, "SO-COV-1", CustomerA, Currency.USD, 100m);
        SeedOrder(db, "SO-COV-2", CustomerA, Currency.USD, 200m);
        await db.SaveChangesAsync();

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var file = ExportOk(await ctl.Export(new DynamicReceiptReconciliationReportRequest
        {
            Fields = new() { "orderNo" },
            ReceiptFields = new() { "receiptNo" },
            GroupBy = "receiptCoverageStatus",
            PageSize = 200,
        }));

        using var workbook = OpenWorkbook(file.FileContents);
        Assert.Equal(4, workbook.NumberOfSheets);

        var orderGroupSheet = RequireSheet(workbook, OrderGroupSheetName);
        Assert.Equal(GroupLabelColumn, orderGroupSheet.GetRow(0).GetCell(0).StringCellValue);
        Assert.Equal(OrderCountColumn, orderGroupSheet.GetRow(0).GetCell(1).StringCellValue);
        Assert.True(orderGroupSheet.LastRowNum >= 1, "收款覆盖状态分组下订单侧应产出分组行");

        var receiptGroupSheet = RequireSheet(workbook, ReceiptGroupSheetName);
        Assert.Equal(GroupLabelColumn, receiptGroupSheet.GetRow(0).GetCell(0).StringCellValue);
        Assert.Equal(ReceiptCountColumn, receiptGroupSheet.GetRow(0).GetCell(1).StringCellValue);
        Assert.Equal(TruncatedColumn, receiptGroupSheet.GetRow(0).GetCell(2).StringCellValue);
        Assert.Equal(ReceiptGroupEmptyNote, receiptGroupSheet.GetRow(1).GetCell(0).StringCellValue);
    }

    // ==================== 4. 收款证据状态分组：未关联收款侧适用、订单侧不适用 ====================

    [Fact]
    public async Task Export_receiptEvidenceStatus_group_receipts_applicable_orders_inapplicable()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "甲客户");
        SeedOrder(db, "SO-EV-1", CustomerA, Currency.USD, 100m);
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
            ReceiptStatus = "all",
            GroupBy = "receiptEvidenceStatus",
            PageSize = 200,
        }));

        using var workbook = OpenWorkbook(file.FileContents);
        Assert.Equal(4, workbook.NumberOfSheets);

        var orderGroupSheet = RequireSheet(workbook, OrderGroupSheetName);
        Assert.Equal(GroupLabelColumn, orderGroupSheet.GetRow(0).GetCell(0).StringCellValue);
        Assert.Equal(OrderCountColumn, orderGroupSheet.GetRow(0).GetCell(1).StringCellValue);
        Assert.Equal(OrderGroupEmptyNote, orderGroupSheet.GetRow(1).GetCell(0).StringCellValue);

        var receiptGroupSheet = RequireSheet(workbook, ReceiptGroupSheetName);
        Assert.Equal(GroupLabelColumn, receiptGroupSheet.GetRow(0).GetCell(0).StringCellValue);
        Assert.Equal(ReceiptCountColumn, receiptGroupSheet.GetRow(0).GetCell(1).StringCellValue);
        Assert.Equal(TruncatedColumn, receiptGroupSheet.GetRow(0).GetCell(2).StringCellValue);
        Assert.Equal(3, receiptGroupSheet.LastRowNum);
    }


    // ==================== 5. 空页：分组工作表显式提示 ====================

    [Fact]
    public async Task Export_empty_page_group_sheets_show_explicit_empty_note()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var file = ExportOk(await ctl.Export(new DynamicReceiptReconciliationReportRequest
        {
            Fields = new() { "orderNo" },
            ReceiptFields = new() { "receiptNo" },
            GroupBy = "customer",
            PageSize = 200,
        }));

        using var workbook = OpenWorkbook(file.FileContents);
        Assert.Equal(4, workbook.NumberOfSheets);

        var orderGroupSheet = RequireSheet(workbook, OrderGroupSheetName);
        Assert.Equal(OrderGroupEmptyNote, orderGroupSheet.GetRow(1).GetCell(0).StringCellValue);

        var receiptGroupSheet = RequireSheet(workbook, ReceiptGroupSheetName);
        Assert.Equal(ReceiptGroupEmptyNote, receiptGroupSheet.GetRow(1).GetCell(0).StringCellValue);
    }

    // ==================== 6. 未知分组键在源读取前拒绝 ====================

    [Fact]
    public async Task Export_unknown_group_rejected_before_source_reads()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => ctl.Export(new DynamicReceiptReconciliationReportRequest
            {
                GroupBy = "bogus",
                PageSize = 200,
            }));
    }

    // ==================== 7. 无销售订单菜单授权拒绝 ====================

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
                GroupBy = "customer",
                PageSize = 200,
            }));
    }


    // ==================== 8. 公式前导标签转义 ====================

    [Fact]
    public async Task Export_escapes_formula_leading_group_labels()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "=1+1");
        SeedOrder(db, "SO-FORMULA", CustomerA, Currency.USD, 100m);
        await db.SaveChangesAsync();

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var file = ExportOk(await ctl.Export(new DynamicReceiptReconciliationReportRequest
        {
            Fields = new() { "orderNo" },
            ReceiptFields = new() { "receiptNo" },
            GroupBy = "customer",
            PageSize = 200,
        }));

        using var workbook = OpenWorkbook(file.FileContents);
        var orderGroupSheet = RequireSheet(workbook, OrderGroupSheetName);
        Assert.Equal("'=1+1", orderGroupSheet.GetRow(1).GetCell(0).StringCellValue);
    }

    // ==================== 9. 未关联收款截断状态显式保留 ====================

    [Fact]
    public async Task Export_truncated_receipt_group_marks_truncated_explicitly()
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
            GroupBy = "customer",
            PageSize = 200,
        }));

        using var workbook = OpenWorkbook(file.FileContents);
        var receiptGroupSheet = RequireSheet(workbook, ReceiptGroupSheetName);
        Assert.Equal(1, receiptGroupSheet.LastRowNum);
        Assert.Equal("甲客户", receiptGroupSheet.GetRow(1).GetCell(0).StringCellValue);
        Assert.Equal((double)SalesOrderReceiptReconciliation.UnlinkedReceiptLimit,
            receiptGroupSheet.GetRow(1).GetCell(1).NumericCellValue);
        Assert.Equal("是", receiptGroupSheet.GetRow(1).GetCell(2).StringCellValue);
    }


    // ==================== 10. 只读不写库 ====================

    [Fact]
    public async Task Export_grouped_does_not_write_to_database()
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
            GroupBy = "customer",
            PageSize = 200,
        }));

        Assert.NotEmpty(file.FileContents);
        Assert.Equal(0, counting.WriteCalls);
    }

    // ==================== 11. 只读计数上下文（断言不写库） ====================

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

