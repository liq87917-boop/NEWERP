using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using ERP.Infrastructure.Reports;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NPOI.SS.UserModel;
using NPOI.XSSF.UserModel;
using System.Reflection;
using System.Security.Claims;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-119 动态客户应收账款证据报表 Excel 导出（只读、有界、作用域化）单元测试。
/// 覆盖：选定列顺序与行值、公式前导文本转义（保持字面、非公式单元格）、原币保留与 known / unknown /
/// over_allocated 三种剩余证据状态、越界业务员（数据范围 fail closed）、页大小上限与仅导出当前页、
/// 空页仅表头、以及只读不写库。
/// <para>全部使用内存数据库（TestDbFactory），不连接 SQL Server、不启动 API、不执行任何 SQL / seed。</para>
/// </summary>
public class DynamicReceivableExcelTests
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
        var employee = new BaseEmployee
        {
            EmployeeCode = code,
            EmployeeName = code,
            IsSalesman = true,
            Status = 1
        };
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

    private static CustomerSalesInvoiceEvidence SeedInvoice(
        ErpDbContext db, string invoiceNumber, long customerId, decimal grossAmount,
        string currency = "USD", int status = CustomerSalesInvoiceEvidenceRules.StatusRecorded)
    {
        var invoice = new CustomerSalesInvoiceEvidence
        {
            InvoiceType = "普票",
            InvoiceCode = string.Empty,
            InvoiceNumber = invoiceNumber,
            NormalizedInvoiceNumber = invoiceNumber.Replace("-", "").ToUpperInvariant(),
            InvoiceDate = new DateTime(2026, 8, 20),
            CustomerId = customerId,
            CustomerCode = "C001",
            CustomerName = "义乌进出口",
            Currency = currency,
            NetAmount = grossAmount * 0.9m,
            TaxAmount = grossAmount * 0.1m,
            GrossAmount = grossAmount,
            Status = status,
            IsDeleted = false
        };
        db.CustomerSalesInvoiceEvidences.Add(invoice);
        db.SaveChanges();
        return invoice;
    }

    private static FinanceReceipt SeedReceipt(
        ErpDbContext db, string receiptNo, long customerId, decimal amount,
        Currency currency = Currency.USD)
    {
        var receipt = new FinanceReceipt
        {
            ReceiptNo = receiptNo,
            ReceiptDate = new DateTime(2026, 9, 20),
            CustomerId = customerId,
            Amount = amount,
            Currency = currency,
            PaymentMethod = PaymentMethod.BankTransfer,
            BankAccount = "TEST-ACCOUNT",
            Status = DocumentStatus.Approved,
            IsDeleted = false
        };
        db.FinanceReceipts.Add(receipt);
        db.SaveChanges();
        return receipt;
    }

    private static CustomerSalesInvoiceCollectionAllocation SeedAllocation(
        ErpDbContext db, CustomerSalesInvoiceEvidence invoice, FinanceReceipt receipt, decimal amount)
    {
        var row = new CustomerSalesInvoiceCollectionAllocation
        {
            CustomerSalesInvoiceEvidenceId = invoice.Id,
            InvoiceType = invoice.InvoiceType,
            InvoiceCode = invoice.InvoiceCode,
            InvoiceNumber = invoice.InvoiceNumber,
            InvoiceDate = invoice.InvoiceDate,
            InvoiceStatus = invoice.Status,
            InvoiceStatusText = "已登记",
            InvoiceGrossAmount = invoice.GrossAmount,
            InvoiceCurrency = invoice.Currency,
            ReceiptId = receipt.Id,
            ReceiptNo = receipt.ReceiptNo,
            ReceiptDate = receipt.ReceiptDate,
            ReceiptStatus = (int)receipt.Status,
            ReceiptStatusText = "已审核",
            ReceiptAmount = receipt.Amount,
            CustomerId = invoice.CustomerId,
            CustomerCode = invoice.CustomerCode,
            CustomerName = invoice.CustomerName,
            AllocatedAmount = amount,
            Currency = invoice.Currency,
            Remark = string.Empty,
            Status = CustomerSalesInvoiceCollectionAllocationRules.StatusActive,
            AllocatedAt = new DateTime(2026, 9, 21),
            AllocatedBy = "张三"
        };
        db.CustomerSalesInvoiceCollectionAllocations.Add(row);
        db.SaveChanges();
        return row;
    }

    /// <summary>播种一个具备「客户资料」菜单授权的特权账号（系统内置角色），返回其用户 Id。</summary>
    private static long SeedPrivilegedAuthorized(ErpDbContext db)
    {
        var role = SeedRole(db, $"Priv-{Guid.NewGuid():N}", isSystem: true);
        var user = SeedUser(db, $"priv-{Guid.NewGuid():N}");
        SeedUserRole(db, user.Id, role.Id);
        SeedRoleMenu(db, role.Id, SeedMenu(db, DynamicReceivableReportRules.RequiredMenuCode).Id);
        return user.Id;
    }

    /// <summary>播种一个具备「客户资料」菜单授权的受限业务员账号，返回（用户 Id、本人客户、他人客户）。</summary>
    private static (long UserId, long Mine, long Other) SeedRestrictedAuthorized(ErpDbContext db, string userName)
    {
        var role = SeedRole(db, $"Sales-{userName}");
        var user = SeedUser(db, userName);
        SeedUserRole(db, user.Id, role.Id);
        SeedRoleMenu(db, role.Id, SeedMenu(db, DynamicReceivableReportRules.RequiredMenuCode).Id);

        var employee = SeedEmployee(db, userName);
        var mine = SeedCustomer(db, $"{userName}-C1", "我的客户", employee.Id);
        var other = SeedCustomer(db, $"{userName}-C2", "别人的客户", employee.Id + 1000);
        return (user.Id, mine.Id, other.Id);
    }

    private static DynamicReceivableReportController BuildController(ErpDbContext db, long? userId)
    {
        var ctl = new DynamicReceivableReportController(new DynamicReceivableReportQuery(db));
        var claims = userId.HasValue
            ? new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }
            : Array.Empty<Claim>();
        ctl.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) }
        };
        return ctl;
    }

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
        var uid = SeedPrivilegedAuthorized(db);
        var customer = SeedCustomer(db, "C1", "客户一");
        SeedInvoice(db, "INV-1", customer.Id, 100m, currency: "USD");
        SeedInvoice(db, "INV-2", customer.Id, 200m, currency: "CNY");

        var ctl = BuildController(db, uid);

        var file = ExportOk(await ctl.Export(new DynamicReceivableReportRequest
        {
            Fields = new() { "invoiceNumber", "currency", "grossAmount" },
            Page = 1,
            PageSize = 10
        }));

        var sheet = OpenWorkbook(file.FileContents).GetSheetAt(0);
        Assert.Equal("发票号码", sheet.GetRow(0).GetCell(0).StringCellValue);
        Assert.Equal("币种", sheet.GetRow(0).GetCell(1).StringCellValue);
        Assert.Equal("发票含税总额", sheet.GetRow(0).GetCell(2).StringCellValue);
        Assert.Equal(2, sheet.LastRowNum);

        var rows = new Dictionary<string, IRow>();
        for (var r = 1; r <= sheet.LastRowNum; r++)
            rows[sheet.GetRow(r).GetCell(0).StringCellValue] = sheet.GetRow(r);

        Assert.Equal("USD", rows["INV-1"].GetCell(1).StringCellValue);
        Assert.Equal(100d, rows["INV-1"].GetCell(2).NumericCellValue);

        Assert.Equal("CNY", rows["INV-2"].GetCell(1).StringCellValue);
        Assert.Equal(200d, rows["INV-2"].GetCell(2).NumericCellValue);
    }

    // ==================== 2. 公式注入防护 ====================

    [Fact]
    public async Task Export_公式前导文本_转义为字面文本_非公式单元格()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedAuthorized(db);
        var customer = SeedCustomer(db, "C1", "客户一");
        SeedInvoice(db, "=1+1", customer.Id, 100m);

        var ctl = BuildController(db, uid);

        var file = ExportOk(await ctl.Export(new DynamicReceivableReportRequest
        {
            Fields = new() { "invoiceNumber" },
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
        Assert.False(DynamicReceivableReportRules.IsFormulaLeading(null));
        Assert.False(DynamicReceivableReportRules.IsFormulaLeading(""));
        Assert.False(DynamicReceivableReportRules.IsFormulaLeading("ABC"));
        Assert.True(DynamicReceivableReportRules.IsFormulaLeading("=1+1"));
        Assert.True(DynamicReceivableReportRules.IsFormulaLeading("+123"));
        Assert.True(DynamicReceivableReportRules.IsFormulaLeading("-5"));
        Assert.True(DynamicReceivableReportRules.IsFormulaLeading("@SUM(A1)"));

        Assert.Equal("'=1+1", DynamicReceivableReportRules.EscapeFormulaLeading("=1+1"));
        Assert.Equal("'@cmd", DynamicReceivableReportRules.EscapeFormulaLeading("@cmd"));
        Assert.Equal("ABC", DynamicReceivableReportRules.EscapeFormulaLeading("ABC"));
        Assert.Null(DynamicReceivableReportRules.EscapeFormulaLeading(null));
        Assert.Equal(123, DynamicReceivableReportRules.EscapeFormulaLeading(123));
    }

    // ==================== 3. 原币保留与剩余证据状态 ====================

    [Fact]
    public async Task Export_剩余证据状态与金额_保留原币与三种状态_不跨币种()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedAuthorized(db);
        var customer = SeedCustomer(db, "C-KO", "对账客户");

        var known = SeedInvoice(db, "INV-KNOWN", customer.Id, 1000m, currency: "USD");
        var over = SeedInvoice(db, "INV-OVER", customer.Id, 500m, currency: "CNY");
        var draft = SeedInvoice(db, "INV-DRAFT", customer.Id, 700m, currency: "EUR",
            status: CustomerSalesInvoiceEvidenceRules.StatusDraft);

        var receipt1 = SeedReceipt(db, "RCP-KNOWN", customer.Id, 400m, currency: Currency.USD);
        SeedAllocation(db, known, receipt1, 400m);

        // 直接播种与源规则矛盾的超额分摊行（有效金额 600 > 发票 500），读取侧按 over_allocated 派生
        var receipt2 = SeedReceipt(db, "RCP-OVER", customer.Id, 600m, currency: Currency.CNY);
        SeedAllocation(db, over, receipt2, 600m);

        var ctl = BuildController(db, uid);
        var file = ExportOk(await ctl.Export(new DynamicReceivableReportRequest
        {
            Fields = new() { "invoiceNumber", "currency", "grossAmount", "remainingAmount", "remainingState" },
            InvoiceStatus = "all",
            PageSize = 100,
        }));

        var sheet = OpenWorkbook(file.FileContents).GetSheetAt(0);
        Assert.Equal("发票号码", sheet.GetRow(0).GetCell(0).StringCellValue);
        Assert.Equal("币种", sheet.GetRow(0).GetCell(1).StringCellValue);
        Assert.Equal("发票含税总额", sheet.GetRow(0).GetCell(2).StringCellValue);
        Assert.Equal("算术剩余金额", sheet.GetRow(0).GetCell(3).StringCellValue);
        Assert.Equal("剩余证据状态", sheet.GetRow(0).GetCell(4).StringCellValue);
        Assert.Equal(3, sheet.LastRowNum);

        var rows = new Dictionary<string, IRow>();
        for (var r = 1; r <= sheet.LastRowNum; r++)
            rows[sheet.GetRow(r).GetCell(0).StringCellValue] = sheet.GetRow(r);

        // known：USD 原币保留，剩余金额可确认
        Assert.Equal("USD", rows["INV-KNOWN"].GetCell(1).StringCellValue);
        Assert.Equal(1000d, rows["INV-KNOWN"].GetCell(2).NumericCellValue);
        Assert.Equal(600d, rows["INV-KNOWN"].GetCell(3).NumericCellValue);
        Assert.Equal(CustomerReceivableReconciliationRules.RemainingKnown, rows["INV-KNOWN"].GetCell(4).StringCellValue);

        // over_allocated：CNY 原币保留，剩余金额为 null（不轧为 0 或负数）
        Assert.Equal("CNY", rows["INV-OVER"].GetCell(1).StringCellValue);
        Assert.Equal(500d, rows["INV-OVER"].GetCell(2).NumericCellValue);
        Assert.Equal(string.Empty, rows["INV-OVER"].GetCell(3).StringCellValue);
        Assert.Equal(CustomerReceivableReconciliationRules.RemainingOverAllocated, rows["INV-OVER"].GetCell(4).StringCellValue);

        // unknown（草稿）：EUR 原币保留，剩余金额为 null（不回落 0）
        Assert.Equal("EUR", rows["INV-DRAFT"].GetCell(1).StringCellValue);
        Assert.Equal(700d, rows["INV-DRAFT"].GetCell(2).NumericCellValue);
        Assert.Equal(string.Empty, rows["INV-DRAFT"].GetCell(3).StringCellValue);
        Assert.Equal(CustomerReceivableReconciliationRules.RemainingUnknown, rows["INV-DRAFT"].GetCell(4).StringCellValue);
    }

    // ==================== 4. 授权 / 校验拒绝 ====================

    [Fact]
    public async Task Export_无客户资料菜单授权_拒绝()
    {
        using var db = TestDbFactory.Create();
        var role = SeedRole(db, "NoMenu");
        var user = SeedUser(db, "nomenu-user");
        SeedUserRole(db, user.Id, role.Id);
        var ctl = BuildController(db, user.Id);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Export(new DynamicReceivableReportRequest()));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    [Fact]
    public async Task Export_无身份_未认证拒绝()
    {
        using var db = TestDbFactory.Create();
        var ctl = BuildController(db, null);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Export(new DynamicReceivableReportRequest()));
        Assert.Equal(ErrorCodes.Unauthorized, ex.Code);
    }

    [Fact]
    public async Task Export_页大小超过100_拒绝_100本身通过()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedAuthorized(db);
        var ctl = BuildController(db, uid);

        var tooBig = await Assert.ThrowsAsync<BusinessException>(() => ctl.Export(
            new DynamicReceivableReportRequest { PageSize = 101 }));
        Assert.Equal(ErrorCodes.InvalidParameter, tooBig.Code);

        var ok = ExportOk(await ctl.Export(new DynamicReceivableReportRequest { PageSize = 100 }));
        Assert.EndsWith(".xlsx", ok.FileDownloadName);
    }

    // ==================== 5. 数据范围（越界） ====================

    [Fact]
    public async Task Export_越界业务员_只导出范围内行_越界客户导出空页()
    {
        using var db = TestDbFactory.Create();
        var (uid, mine, other) = SeedRestrictedAuthorized(db, "alice");
        SeedInvoice(db, "INV-MINE", mine, 1000m);
        SeedInvoice(db, "INV-OTHER", other, 2000m);
        var ctl = BuildController(db, uid);

        var inScope = ExportOk(await ctl.Export(new DynamicReceivableReportRequest
        {
            Fields = new() { "invoiceNumber" },
            PageSize = 100
        }));
        var sheet = OpenWorkbook(inScope.FileContents).GetSheetAt(0);
        Assert.Equal(1, sheet.LastRowNum);
        Assert.Equal("INV-MINE", sheet.GetRow(1).GetCell(0).StringCellValue);

        var outOfScope = ExportOk(await ctl.Export(new DynamicReceivableReportRequest
        {
            Fields = new() { "invoiceNumber" },
            CustomerId = other,
            PageSize = 100
        }));
        var emptySheet = OpenWorkbook(outOfScope.FileContents).GetSheetAt(0);
        Assert.Equal("发票号码", emptySheet.GetRow(0).GetCell(0).StringCellValue);
        Assert.Equal(0, emptySheet.LastRowNum);
    }

    // ==================== 6. 空页 / 仅当前页 ====================

    [Fact]
    public async Task Export_空页_返回仅表头工作簿()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedAuthorized(db);
        var ctl = BuildController(db, uid);

        var file = ExportOk(await ctl.Export(new DynamicReceivableReportRequest
        {
            Fields = new() { "invoiceNumber" },
            PageSize = 10
        }));

        var sheet = OpenWorkbook(file.FileContents).GetSheetAt(0);
        Assert.Equal("发票号码", sheet.GetRow(0).GetCell(0).StringCellValue);
        Assert.Equal(0, sheet.LastRowNum);
    }

    [Fact]
    public async Task Export_仅导出当前页_受页大小上限约束()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedAuthorized(db);
        var customer = SeedCustomer(db, "C1", "客户一");
        SeedInvoice(db, "INV-1", customer.Id, 100m);
        SeedInvoice(db, "INV-2", customer.Id, 200m);
        SeedInvoice(db, "INV-3", customer.Id, 300m);

        var ctl = BuildController(db, uid);
        var file = ExportOk(await ctl.Export(new DynamicReceivableReportRequest
        {
            Fields = new() { "invoiceNumber" },
            Page = 1,
            PageSize = 2
        }));

        var sheet = OpenWorkbook(file.FileContents).GetSheetAt(0);
        Assert.Equal(2, sheet.LastRowNum);
    }

    // ==================== 7. 只读不写库 ====================

    [Fact]
    public async Task Export_只读不写库()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedAuthorized(db);
        var customer = SeedCustomer(db, "C-RO", "只读客户");
        SeedInvoice(db, "INV-RO", customer.Id, 1000m);

        var counting = CountingDbContext.Wrap(db);
        var ctl = new DynamicReceivableReportController(new DynamicReceivableReportQuery(counting.Proxy));
        var claims = new[] { new Claim(ClaimTypes.NameIdentifier, uid.ToString()) };
        ctl.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) }
        };

        var file = ExportOk(await ctl.Export(new DynamicReceivableReportRequest
        {
            Fields = new() { "invoiceNumber" },
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
