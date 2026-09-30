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
using NPOI.SS.UserModel;
using NPOI.XSSF.UserModel;
using System.Reflection;
using System.Security.Claims;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-167 动态客户订单与收款核对报表 Excel 导出（只读、有界、作用域化）单元测试。
/// 覆盖：两个独立工作表（订单证据 + 未关联收款证据）、选定列顺序、业务员数据范围（fail closed）、
/// 页大小上限与仅导出当前页、空页仅表头、未知金额 null 保留为空（绝不回落 0）、原币保留、收款证据状态与截断警告显式保留、
/// 公式前导文本转义（保持字面、非公式单元格）、以及只读不写库。
/// <para>全部使用内存数据库（TestDbFactory），不连接 SQL Server、不启动 API、不执行任何 SQL / seed。</para>
/// </summary>
public class DynamicReceiptReconciliationExcelTests
{
    private static readonly DateTime AsOf = new(2026, 9, 25);
    private const long CustomerA = 966001L;
    private const long CustomerB = 966002L;

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

    private static async Task<BusinessException> AssertBusinessAsync(int expectedCode, Func<Task> action)
    {
        var ex = await Assert.ThrowsAsync<BusinessException>(action);
        Assert.Equal(expectedCode, ex.Code);
        return ex;
    }

    // ==================== 1. 两个独立工作表 + 选定列顺序 + 原币 + 收款证据状态 ====================

    [Fact]
    public async Task Export_writes_two_sheets_with_requested_column_order_and_original_currency()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "甲客户");
        SeedCustomer(db, CustomerB, "乙客户");
        SeedOrder(db, "SO-EX-1", CustomerA, Currency.USD, 1000m);
        SeedOrder(db, "SO-EX-2", CustomerB, Currency.CNY, 500m);
        SeedReceipt(db, "SK-EX-1", CustomerA, 30m, Currency.USD, DocumentStatus.Approved);
        await db.SaveChangesAsync();

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var file = ExportOk(await ctl.Export(new DynamicReceiptReconciliationReportRequest
        {
            Fields = new() { "customerName", "orderNo", "currency", "orderAmount" },
            ReceiptFields = new() { "receiptNo", "customerName", "currency", "amount", "evidenceStatus", "receiptLinkageStatus" },
            PageSize = 200,
        }));

        using var workbook = OpenWorkbook(file.FileContents);
        Assert.Equal(2, workbook.NumberOfSheets);
        Assert.Equal(DynamicReceiptReconciliationReportRules.OrderSheetName, workbook.GetSheetAt(0).SheetName);
        Assert.Equal(DynamicReceiptReconciliationReportRules.ReceiptSheetName, workbook.GetSheetAt(1).SheetName);

        // 订单证据工作表：列顺序与请求一致，不同币种分别成行（绝不合并）
        var orderSheet = workbook.GetSheetAt(0);
        Assert.Equal(new[] { "客户名", "订单号", "币种", "订单金额" },
            Enumerable.Range(0, 4).Select(i => orderSheet.GetRow(0).GetCell(i).StringCellValue).ToArray());
        Assert.Equal(2, orderSheet.LastRowNum);
        Assert.Equal("SO-EX-1", orderSheet.GetRow(1).GetCell(1).StringCellValue);
        Assert.Equal("USD", orderSheet.GetRow(1).GetCell(2).StringCellValue);
        Assert.Equal(1000d, orderSheet.GetRow(1).GetCell(3).NumericCellValue);
        Assert.Equal("SO-EX-2", orderSheet.GetRow(2).GetCell(1).StringCellValue);
        Assert.Equal("CNY", orderSheet.GetRow(2).GetCell(2).StringCellValue);

        // 未关联收款证据工作表：列顺序、原币、收款证据状态与收款链接状态显式保留
        var receiptSheet = workbook.GetSheetAt(1);
        Assert.Equal(new[] { "收款单号", "客户名", "币种", "收款金额", "收款证据状态", "收款链接状态" },
            Enumerable.Range(0, 6).Select(i => receiptSheet.GetRow(0).GetCell(i).StringCellValue).ToArray());
        var receiptRow = receiptSheet.GetRow(1);
        Assert.Equal("SK-EX-1", receiptRow.GetCell(0).StringCellValue);
        Assert.Equal("USD", receiptRow.GetCell(2).StringCellValue);
        Assert.Equal(30d, receiptRow.GetCell(3).NumericCellValue);
        Assert.Equal("active", receiptRow.GetCell(4).StringCellValue);
        Assert.Equal("unlinked", receiptRow.GetCell(5).StringCellValue);
    }

    // ==================== 2. 业务员数据范围（fail closed） ====================

    [Fact]
    public async Task Export_scopes_to_assigned_customers_only()
    {
        using var db = TestDbFactory.Create();
        var user = SeedUser(db, "alice");
        var role = SeedRole(db, "Sales");
        SeedUserRole(db, user.Id, role.Id);
        SeedRoleMenu(db, role.Id, SeedMenu(db, DynamicReceiptReconciliationReportRules.RequiredMenuCode).Id);
        var alice = SeedEmployee(db, "alice");
        SeedCustomer(db, CustomerA, "我的客户", alice.Id);
        SeedCustomer(db, CustomerB, "别人的客户", alice.Id + 1000);

        SeedOrder(db, "SO-MINE", CustomerA, Currency.USD, 100m);
        SeedOrder(db, "SO-OTHER", CustomerB, Currency.USD, 300m);
        SeedReceipt(db, "SK-MINE", CustomerA, 40m, Currency.USD, DocumentStatus.Approved);
        SeedReceipt(db, "SK-OTHER", CustomerB, 50m, Currency.USD, DocumentStatus.Approved);
        await db.SaveChangesAsync();

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var file = ExportOk(await ctl.Export(new DynamicReceiptReconciliationReportRequest
        {
            Fields = new() { "orderNo" },
            ReceiptFields = new() { "receiptNo" },
            PageSize = 200,
        }));

        using var workbook = OpenWorkbook(file.FileContents);
        Assert.Equal(1, workbook.GetSheetAt(0).LastRowNum);
        Assert.Equal("SO-MINE", workbook.GetSheetAt(0).GetRow(1).GetCell(0).StringCellValue);
        Assert.Equal(1, workbook.GetSheetAt(1).LastRowNum);
        Assert.Equal("SK-MINE", workbook.GetSheetAt(1).GetRow(1).GetCell(0).StringCellValue);
    }

    // ==================== 3. 页大小上限（超限拒绝，先于字节）+ 仅导出当前页 ====================

    [Fact]
    public async Task Export_rejects_page_size_over_limit_before_bytes()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => ctl.Export(new DynamicReceiptReconciliationReportRequest { PageSize = 201 }));
    }

    [Fact]
    public async Task Export_exports_only_current_page()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "甲客户");
        for (var i = 0; i < 3; i++)
            SeedOrder(db, $"SO-PG-{i}", CustomerA, Currency.USD, 100m);
        await db.SaveChangesAsync();

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var file = ExportOk(await ctl.Export(new DynamicReceiptReconciliationReportRequest
        {
            Fields = new() { "orderNo" },
            Page = 1,
            PageSize = 2,
        }));

        using var workbook = OpenWorkbook(file.FileContents);
        Assert.Equal(2, workbook.GetSheetAt(0).LastRowNum);
    }

    // ==================== 4. 空页仅表头 ====================

    [Fact]
    public async Task Export_empty_page_returns_header_only_workbook()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var file = ExportOk(await ctl.Export(new DynamicReceiptReconciliationReportRequest
        {
            Fields = new() { "orderNo" },
            ReceiptFields = new() { "receiptNo" },
            PageSize = 200,
        }));

        using var workbook = OpenWorkbook(file.FileContents);
        Assert.Equal(2, workbook.NumberOfSheets);
        Assert.Equal("订单号", workbook.GetSheetAt(0).GetRow(0).GetCell(0).StringCellValue);
        Assert.Equal(0, workbook.GetSheetAt(0).LastRowNum);
        Assert.Equal("收款单号", workbook.GetSheetAt(1).GetRow(0).GetCell(0).StringCellValue);
        Assert.Equal(0, workbook.GetSheetAt(1).LastRowNum);
    }

    // ==================== 5. 未知金额 null 保留为空（绝不回落 0）+ 截断警告 ====================

    [Fact]
    public async Task Export_keeps_null_unknown_amount_empty_and_truncation_warning_explicit()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "甲客户");
        SeedOrder(db, "SO-NULL", CustomerA, Currency.USD, 100m);

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
            Fields = new() { "orderNo", "uncoveredAmount" },
            ReceiptFields = new() { "receiptNo", "amount" },
            PageSize = 200,
        }));

        using var workbook = OpenWorkbook(file.FileContents);

        // 订单证据工作表：未关联订单的未覆盖金额未知 → null → 空文本，绝不回落 0
        var orderRow = workbook.GetSheetAt(0).GetRow(1);
        Assert.Equal(string.Empty, orderRow.GetCell(1).StringCellValue);

        // 未关联收款证据工作表：截断警告写入尾行、显式保留
        var receiptSheet = workbook.GetSheetAt(1);
        var noteRow = receiptSheet.GetRow(receiptSheet.LastRowNum);
        Assert.Contains("截断", noteRow.GetCell(0).StringCellValue);
    }

    // ==================== 6. 公式注入防护 ====================

    [Fact]
    public async Task Export_escapes_formula_leading_text_in_cells()
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
            Fields = new() { "customerName" },
            PageSize = 200,
        }));

        using var workbook = OpenWorkbook(file.FileContents);
        Assert.Equal("'=1+1", workbook.GetSheetAt(0).GetRow(1).GetCell(0).StringCellValue);
    }

    [Theory]
    [InlineData("=1+1", true)]
    [InlineData("+SUM(A1)", true)]
    [InlineData("-1+2", true)]
    [InlineData("@cmd", true)]
    [InlineData("正常文本", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void EscapeFormulaLeading_escapes_dangerous_leading_chars_keeps_plain(string? input, bool expectedEscaped)
    {
        var result = DynamicReceiptReconciliationReportRules.EscapeFormulaLeading(input);
        if (expectedEscaped)
            Assert.Equal("'" + input, result);
        else
            Assert.Equal(input, result);
    }

    [Fact]
    public void BuildExportRow_keeps_keys_and_escapes_formula_leading_text()
    {
        var row = new Dictionary<string, object?> { ["receiptNo"] = "=1+1", ["amount"] = 30m };
        var exported = DynamicReceiptReconciliationReportRules.BuildExportRow(row);

        Assert.Equal("'=1+1", exported["receiptNo"]);
        Assert.Equal(30m, exported["amount"]);
    }

    // ==================== 7. 只读不写库 ====================

    [Fact]
    public async Task Export_does_not_write_to_database()
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
            PageSize = 200,
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
