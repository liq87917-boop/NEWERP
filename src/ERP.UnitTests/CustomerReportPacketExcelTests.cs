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
/// ERP-123 客户报告包 Excel 导出（只读、有界、双授权、作用域化）单元测试。
/// 覆盖：两个独立工作表（销售订单 + 应收证据）、标识 / 原币 / 显式剩余证据状态、公式前导文本转义（保持字面、
/// 非公式单元格）、双菜单授权与越界客户（fail closed）、页大小上限与仅导出当前页、空页仅表头、以及只读不写库。
/// <para>全部使用内存数据库（TestDbFactory），不连接 SQL Server、不启动 API、不执行任何 SQL / seed。</para>
/// </summary>
public class CustomerReportPacketExcelTests
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
        Currency currency = Currency.USD, DateTime? orderDate = null)
    {
        var order = new SalesOrder
        {
            OrderNo = orderNo,
            CustomerId = customerId,
            OrderDate = orderDate ?? new DateTime(2026, 9, 1),
            Status = DocumentStatus.Pending,
            Currency = currency,
            TotalAmount = 100
        };
        db.SalesOrders.Add(order);
        db.SaveChanges();
        return order;
    }

    private static CustomerSalesInvoiceEvidence SeedInvoice(
        ErpDbContext db, string invoiceNumber, long customerId, decimal grossAmount,
        string currency = "USD", int status = CustomerSalesInvoiceEvidenceRules.StatusRecorded,
        DateTime? invoiceDate = null)
    {
        var invoice = new CustomerSalesInvoiceEvidence
        {
            InvoiceType = "普票",
            InvoiceCode = string.Empty,
            InvoiceNumber = invoiceNumber,
            NormalizedInvoiceNumber = invoiceNumber.Replace("-", "").ToUpperInvariant(),
            InvoiceDate = invoiceDate ?? new DateTime(2026, 8, 20),
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
        ErpDbContext db, string receiptNo, long customerId, decimal amount, Currency currency = Currency.USD)
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

    /// <summary>播种同时具备「销售订单 + 客户资料」两个菜单授权的特权账号（系统内置角色），返回用户 Id。</summary>
    private static long SeedPrivilegedBothMenus(ErpDbContext db)
    {
        var role = SeedRole(db, $"Priv-{Guid.NewGuid():N}", isSystem: true);
        var user = SeedUser(db, $"priv-{Guid.NewGuid():N}");
        SeedUserRole(db, user.Id, role.Id);
        SeedRoleMenu(db, role.Id, SeedMenu(db, CustomerReportPacketRules.RequiredOrderMenuCode).Id);
        SeedRoleMenu(db, role.Id, SeedMenu(db, CustomerReportPacketRules.RequiredReceivableMenuCode).Id);
        return user.Id;
    }

    /// <summary>播种只具备「客户资料」菜单授权的特权账号（缺少销售订单菜单）。</summary>
    private static long SeedPrivilegedCustomerMenuOnly(ErpDbContext db)
    {
        var role = SeedRole(db, $"Priv-{Guid.NewGuid():N}", isSystem: true);
        var user = SeedUser(db, $"priv-{Guid.NewGuid():N}");
        SeedUserRole(db, user.Id, role.Id);
        SeedRoleMenu(db, role.Id, SeedMenu(db, CustomerReportPacketRules.RequiredReceivableMenuCode).Id);
        return user.Id;
    }

    /// <summary>播种只具备「销售订单」菜单授权的特权账号（缺少客户资料菜单）。</summary>
    private static long SeedPrivilegedOrderMenuOnly(ErpDbContext db)
    {
        var role = SeedRole(db, $"Priv-{Guid.NewGuid():N}", isSystem: true);
        var user = SeedUser(db, $"priv-{Guid.NewGuid():N}");
        SeedUserRole(db, user.Id, role.Id);
        SeedRoleMenu(db, role.Id, SeedMenu(db, CustomerReportPacketRules.RequiredOrderMenuCode).Id);
        return user.Id;
    }

    /// <summary>播种同时具备两个菜单授权的受限业务员账号，返回（用户 Id、本人客户、他人客户）。</summary>
    private static (long UserId, long Mine, long Other) SeedRestrictedBothMenus(ErpDbContext db, string userName)
    {
        var role = SeedRole(db, $"Sales-{userName}");
        var user = SeedUser(db, userName);
        SeedUserRole(db, user.Id, role.Id);
        SeedRoleMenu(db, role.Id, SeedMenu(db, CustomerReportPacketRules.RequiredOrderMenuCode).Id);
        SeedRoleMenu(db, role.Id, SeedMenu(db, CustomerReportPacketRules.RequiredReceivableMenuCode).Id);

        var employee = SeedEmployee(db, userName);
        var mine = SeedCustomer(db, $"{userName}-C1", "我的客户", employee.Id);
        var other = SeedCustomer(db, $"{userName}-C2", "别人的客户", employee.Id + 1000);
        return (user.Id, mine.Id, other.Id);
    }

    private static CustomerReportPacketController BuildController(ErpDbContext db, long? userId)
    {
        var ctl = new CustomerReportPacketController(
            new DynamicSalesOrderReportQuery(db),
            new DynamicReceivableReportQuery(db),
            db);
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

    /// <summary>按表头中文标题返回列下标（未找到返回 -1）</summary>
    private static int ColOf(ISheet sheet, string label)
    {
        var header = sheet.GetRow(0);
        for (var c = 0; c < header.LastCellNum; c++)
        {
            var cell = header.GetCell(c);
            if (cell != null && cell.StringCellValue == label) return c;
        }
        return -1;
    }

    // ==================== 1. 两个独立工作表 / 标识 / 原币 / 显式剩余状态 ====================

    [Fact]
    public async Task Export_双菜单授权齐全_两个独立工作表_标识与原币与显式剩余状态()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedBothMenus(db);
        var customer = SeedCustomer(db, "C1", "客户一");
        SeedOrder(db, "SO-USD", customer.Id, Currency.USD);
        SeedOrder(db, "SO-CNY", customer.Id, Currency.CNY);

        var known = SeedInvoice(db, "INV-KNOWN", customer.Id, 1000m, "USD");
        SeedAllocation(db, known, SeedReceipt(db, "RC-KNOWN", customer.Id, 400m, Currency.USD), 400m);

        var over = SeedInvoice(db, "INV-OVER", customer.Id, 500m, "CNY");
        SeedAllocation(db, over, SeedReceipt(db, "RC-OVER", customer.Id, 600m, Currency.CNY), 600m);

        var ctl = BuildController(db, uid);
        var file = ExportOk(await ctl.Export(new CustomerReportPacketRequest { CustomerId = customer.Id, PageSize = 20 }));

        using var workbook = OpenWorkbook(file.FileContents);
        Assert.Equal(2, workbook.NumberOfSheets);

        var orderSheet = workbook.GetSheetAt(0);
        var receivableSheet = workbook.GetSheetAt(1);
        Assert.Equal(CustomerReportPacketRules.SalesOrderSheetName, orderSheet.SheetName);
        Assert.Equal(CustomerReportPacketRules.ReceivableEvidenceSheetName, receivableSheet.SheetName);

        var orderNoCol = ColOf(orderSheet, "订单号");
        var orderCurrencyCol = ColOf(orderSheet, "币种");
        Assert.True(orderNoCol >= 0 && orderCurrencyCol >= 0, "销售订单工作表缺少订单号 / 币种列");

        var orderNos = new HashSet<string>();
        var orderCurrencies = new HashSet<string>();
        for (var r = 1; r <= orderSheet.LastRowNum; r++)
        {
            var row = orderSheet.GetRow(r);
            orderNos.Add(row.GetCell(orderNoCol).StringCellValue);
            orderCurrencies.Add(row.GetCell(orderCurrencyCol).StringCellValue);
        }
        Assert.Contains("SO-USD", orderNos);
        Assert.Contains("SO-CNY", orderNos);
        Assert.Contains("USD", orderCurrencies);
        Assert.Contains("CNY", orderCurrencies);

        var invNoCol = ColOf(receivableSheet, "发票号码");
        var recCurrencyCol = ColOf(receivableSheet, "币种");
        var recRemainingCol = ColOf(receivableSheet, "算术剩余金额");
        var recStateCol = ColOf(receivableSheet, "剩余证据状态");
        Assert.True(invNoCol >= 0 && recCurrencyCol >= 0 && recRemainingCol >= 0 && recStateCol >= 0,
            "应收证据工作表缺少发票号码 / 币种 / 算术剩余金额 / 剩余证据状态列");

        var byInv = new Dictionary<string, IRow>();
        for (var r = 1; r <= receivableSheet.LastRowNum; r++)
        {
            var row = receivableSheet.GetRow(r);
            byInv[row.GetCell(invNoCol).StringCellValue] = row;
        }

        var knownRow = byInv["INV-KNOWN"];
        Assert.Equal("USD", knownRow.GetCell(recCurrencyCol).StringCellValue);
        Assert.Equal(CustomerReceivableReconciliationRules.RemainingKnown, knownRow.GetCell(recStateCol).StringCellValue);
        Assert.Equal(600d, knownRow.GetCell(recRemainingCol).NumericCellValue, 3);

        var overRow = byInv["INV-OVER"];
        Assert.Equal("CNY", overRow.GetCell(recCurrencyCol).StringCellValue);
        Assert.Equal(CustomerReceivableReconciliationRules.RemainingOverAllocated, overRow.GetCell(recStateCol).StringCellValue);
        Assert.Equal(string.Empty, overRow.GetCell(recRemainingCol).StringCellValue);
    }

    // ==================== 2. 公式前导文本转义（保持字面、非公式单元格） ====================

    [Fact]
    public async Task Export_公式前导文本_两个工作表都转义为字面文本()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedBothMenus(db);
        var customer = SeedCustomer(db, "C1", "客户一");
        SeedOrder(db, "=1+1", customer.Id, Currency.USD);
        SeedInvoice(db, "=1+1", customer.Id, 1000m, "USD");

        var ctl = BuildController(db, uid);
        var file = ExportOk(await ctl.Export(new CustomerReportPacketRequest { CustomerId = customer.Id, PageSize = 20 }));

        using var workbook = OpenWorkbook(file.FileContents);
        var orderSheet = workbook.GetSheetAt(0);
        var orderNoCell = orderSheet.GetRow(1).GetCell(ColOf(orderSheet, "订单号"));
        Assert.Equal(CellType.String, orderNoCell.CellType);
        Assert.NotEqual(CellType.Formula, orderNoCell.CellType);
        Assert.Equal("'=1+1", orderNoCell.StringCellValue);

        var receivableSheet = workbook.GetSheetAt(1);
        var invNoCell = receivableSheet.GetRow(1).GetCell(ColOf(receivableSheet, "发票号码"));
        Assert.Equal(CellType.String, invNoCell.CellType);
        Assert.NotEqual(CellType.Formula, invNoCell.CellType);
        Assert.Equal("'=1+1", invNoCell.StringCellValue);
    }

    // ==================== 3. 双菜单授权 / 身份 / 有界校验（fail closed，发送字节前拒绝） ====================

    [Fact]
    public async Task Export_缺少销售订单菜单_拒绝()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedCustomerMenuOnly(db);
        var customer = SeedCustomer(db, "C1", "客户一");
        SeedOrder(db, "SO-1", customer.Id);
        var ctl = BuildController(db, uid);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Export(new CustomerReportPacketRequest
        {
            CustomerId = customer.Id,
        }));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    [Fact]
    public async Task Export_缺少客户资料菜单_拒绝()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedOrderMenuOnly(db);
        var customer = SeedCustomer(db, "C1", "客户一");
        SeedInvoice(db, "INV-1", customer.Id, 1000m);
        var ctl = BuildController(db, uid);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Export(new CustomerReportPacketRequest
        {
            CustomerId = customer.Id,
        }));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    [Fact]
    public async Task Export_无身份_未认证拒绝()
    {
        using var db = TestDbFactory.Create();
        var ctl = BuildController(db, null);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Export(new CustomerReportPacketRequest
        {
            CustomerId = 1,
        }));
        Assert.Equal(ErrorCodes.Unauthorized, ex.Code);
    }

    [Fact]
    public async Task Export_客户Id非正整数_拒绝()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedBothMenus(db);
        var ctl = BuildController(db, uid);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Export(new CustomerReportPacketRequest
        {
            CustomerId = 0,
        }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public async Task Export_日期区间倒置_拒绝()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedBothMenus(db);
        var ctl = BuildController(db, uid);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Export(new CustomerReportPacketRequest
        {
            CustomerId = 1,
            StartDate = new DateTime(2026, 9, 30),
            EndDate = new DateTime(2026, 9, 1),
        }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public async Task Export_页大小超限_拒绝()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedBothMenus(db);
        var ctl = BuildController(db, uid);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Export(new CustomerReportPacketRequest
        {
            CustomerId = 1,
            PageSize = 101,
        }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    // ==================== 4. 业务员数据范围（fail closed，不泄露范围外数据） ====================

    [Fact]
    public async Task Export_越界客户_两个工作表都空_不泄露()
    {
        using var db = TestDbFactory.Create();
        var (uid, _, other) = SeedRestrictedBothMenus(db, "alice");
        SeedOrder(db, "SO-OTHER", other);
        SeedInvoice(db, "INV-OTHER", other, 1000m);
        var ctl = BuildController(db, uid);

        var file = ExportOk(await ctl.Export(new CustomerReportPacketRequest { CustomerId = other, PageSize = 20 }));

        using var workbook = OpenWorkbook(file.FileContents);
        Assert.Equal(2, workbook.NumberOfSheets);
        Assert.Equal(0, workbook.GetSheetAt(0).LastRowNum);
        Assert.Equal(0, workbook.GetSheetAt(1).LastRowNum);
    }

    // ==================== 5. 仅导出当前页（行数受页大小上限约束） ====================

    [Fact]
    public async Task Export_仅导出当前页_行数受页大小上限约束()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedBothMenus(db);
        var customer = SeedCustomer(db, "C1", "客户一");
        SeedOrder(db, "SO-1", customer.Id);
        SeedOrder(db, "SO-2", customer.Id);
        SeedOrder(db, "SO-3", customer.Id);
        SeedInvoice(db, "INV-1", customer.Id, 100m);
        SeedInvoice(db, "INV-2", customer.Id, 200m);
        SeedInvoice(db, "INV-3", customer.Id, 300m);

        var ctl = BuildController(db, uid);
        var file = ExportOk(await ctl.Export(new CustomerReportPacketRequest
        {
            CustomerId = customer.Id,
            Page = 1,
            PageSize = 2,
        }));

        using var workbook = OpenWorkbook(file.FileContents);
        Assert.Equal(2, workbook.GetSheetAt(0).LastRowNum);
        Assert.Equal(2, workbook.GetSheetAt(1).LastRowNum);
    }

    // ==================== 6. 只读不写库 ====================

    [Fact]
    public async Task Export_只读不写库()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedBothMenus(db);
        var customer = SeedCustomer(db, "C1", "客户一");
        SeedOrder(db, "SO-1", customer.Id);
        SeedInvoice(db, "INV-1", customer.Id, 1000m);

        var counting = CountingDbContext.Wrap(db);
        var ctl = new CustomerReportPacketController(
            new DynamicSalesOrderReportQuery(counting.Proxy),
            new DynamicReceivableReportQuery(counting.Proxy),
            counting.Proxy);
        var claims = new[] { new Claim(ClaimTypes.NameIdentifier, uid.ToString()) };
        ctl.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) }
        };

        var file = ExportOk(await ctl.Export(new CustomerReportPacketRequest { CustomerId = customer.Id, PageSize = 20 }));

        Assert.NotEmpty(file.FileContents);
        Assert.Equal(0, counting.WriteCalls);
    }

    // ==================== 7. 纯规则：公式注入转义 ====================

    [Theory]
    [InlineData("=1+1", true)]
    [InlineData("+SUM(A1)", true)]
    [InlineData("-1+2", true)]
    [InlineData("@cmd", true)]
    [InlineData("正常文本", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void EscapeFormulaLeading_危险字符转义_普通值原样(string? input, bool expectedEscaped)
    {
        var result = CustomerReportPacketRules.EscapeFormulaLeading(input);
        if (expectedEscaped)
            Assert.Equal("'" + input, result);
        else
            Assert.Equal(input, result);
    }

    [Fact]
    public void BuildExportRow_保留键并转义公式前导文本_数值原样()
    {
        var row = new Dictionary<string, object?> { ["orderNo"] = "=1+1", ["totalAmount"] = 100m };
        var exported = CustomerReportPacketRules.BuildExportRow(row);

        Assert.Equal("'=1+1", exported["orderNo"]);
        Assert.Equal(100m, exported["totalAmount"]);
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
