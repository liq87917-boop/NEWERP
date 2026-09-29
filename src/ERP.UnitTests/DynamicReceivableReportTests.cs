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
using System.Reflection;
using System.Security.Claims;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-117 动态客户应收账款证据报表预览（只读、有界、作用域化）单元测试。
/// 覆盖：字段白名单目录（不含账龄 / 到期日字段）、客户 / 发票日期 / 币种 / 分配状态筛选、稳定分页与 100 行上限、
/// 无身份、无客户资料菜单授权、越界业务员（数据范围 fail closed）、未映射业务员（空范围）、
/// 无效字段 / 无效客户 Id / 无效日期区间 / 无效币种与分配状态 / 页大小超限、
/// 已知 / 未知 / 超额分摊三种剩余证据状态，以及只读不写库。
/// <para>全部使用内存数据库（TestDbFactory），不连接 SQL Server、不启动 API、不执行任何 SQL / seed。</para>
/// </summary>
public class DynamicReceivableReportTests
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
        ErpDbContext db, string receiptNo, long customerId, decimal amount,
        Currency currency = Currency.USD, DocumentStatus status = DocumentStatus.Approved)
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
            Status = status,
            IsDeleted = false
        };
        db.FinanceReceipts.Add(receipt);
        db.SaveChanges();
        return receipt;
    }

    private static CustomerSalesInvoiceCollectionAllocation SeedAllocation(
        ErpDbContext db, CustomerSalesInvoiceEvidence invoice, FinanceReceipt receipt, decimal amount,
        int status = CustomerSalesInvoiceCollectionAllocationRules.StatusActive)
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
            Status = status,
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

    private static DynamicReceivableReportCatalogDto CatalogOk(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<ApiResponse<DynamicReceivableReportCatalogDto>>(ok.Value);
        return resp.Data!;
    }

    private static DynamicReceivableReportPageDto PreviewOk(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<ApiResponse<DynamicReceivableReportPageDto>>(ok.Value);
        return resp.Data!;
    }

    // ==================== 1. 目录 ====================

    [Fact]
    public async Task Catalog_特权用户_返回有限白名单且不含账龄字段()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedAuthorized(db);
        var ctl = BuildController(db, uid);

        var catalog = CatalogOk(await ctl.Catalog());

        Assert.Equal(DynamicReceivableReportRules.RequiredMenuCode, catalog.RequiredMenuCode);
        Assert.Equal(DynamicReceivableReportRules.MaxPageSize, catalog.MaxPageSize);
        Assert.Equal(DynamicReceivableReportRules.AllFieldKeys.Count, catalog.Fields.Count);

        var keys = catalog.Fields.Select(f => f.Key).ToList();
        Assert.Contains("grossAmount", keys);
        Assert.Contains("effectiveAmount", keys);
        Assert.Contains("remainingAmount", keys);
        Assert.Contains("allocationState", keys);
        // 不主张到期日账龄 / 催收状态：白名单不得暴露账龄字段
        Assert.DoesNotContain("agingBucket", keys);
        Assert.DoesNotContain("agingBucketText", keys);
        Assert.DoesNotContain("dueDateKnown", keys);
    }

    [Fact]
    public async Task Catalog_无身份_未认证拒绝()
    {
        using var db = TestDbFactory.Create();
        var ctl = BuildController(db, null);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Catalog());
        Assert.Equal(ErrorCodes.Unauthorized, ex.Code);
    }

    [Fact]
    public async Task Catalog_无客户菜单_权限不足拒绝()
    {
        using var db = TestDbFactory.Create();
        var role = SeedRole(db, "NoMenu");
        var user = SeedUser(db, "nommenu-user");
        SeedUserRole(db, user.Id, role.Id);
        var ctl = BuildController(db, user.Id);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Catalog());
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    // ==================== 2. 校验（先于读取，fail closed） ====================

    [Fact]
    public async Task Preview_未知字段_拒绝()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedAuthorized(db);
        var ctl = BuildController(db, uid);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Preview(
            new DynamicReceivableReportRequest { Fields = new() { "unknownField" } }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public async Task Preview_无效客户Id_拒绝()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedAuthorized(db);
        var ctl = BuildController(db, uid);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Preview(
            new DynamicReceivableReportRequest { CustomerId = 0 }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public async Task Preview_无效币种_拒绝()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedAuthorized(db);
        var ctl = BuildController(db, uid);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Preview(
            new DynamicReceivableReportRequest { Currency = "XXX" }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public async Task Preview_无效分配状态_拒绝()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedAuthorized(db);
        var ctl = BuildController(db, uid);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Preview(
            new DynamicReceivableReportRequest { AllocationState = "weird" }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public async Task Preview_无效发票状态_拒绝()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedAuthorized(db);
        var ctl = BuildController(db, uid);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Preview(
            new DynamicReceivableReportRequest { InvoiceStatus = "weird" }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public async Task Preview_日期区间颠倒_拒绝()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedAuthorized(db);
        var ctl = BuildController(db, uid);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Preview(
            new DynamicReceivableReportRequest
            {
                StartDate = new DateTime(2026, 9, 30),
                EndDate = new DateTime(2026, 9, 1),
            }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public async Task Preview_页大小超过100_拒绝_100本身通过()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedAuthorized(db);
        var ctl = BuildController(db, uid);

        var tooBig = await Assert.ThrowsAsync<BusinessException>(() => ctl.Preview(
            new DynamicReceivableReportRequest { PageSize = 101 }));
        Assert.Equal(ErrorCodes.InvalidParameter, tooBig.Code);

        var ok = PreviewOk(await ctl.Preview(new DynamicReceivableReportRequest { PageSize = 100 }));
        Assert.Equal(100, ok.PageSize);
    }

    // ==================== 3. 数据范围（每次请求重新解析，fail closed） ====================

    [Fact]
    public async Task Preview_越界业务员_只返回自己客户_越界筛选返回空()
    {
        using var db = TestDbFactory.Create();
        var (uid, mine, other) = SeedRestrictedAuthorized(db, "alice");
        SeedInvoice(db, "INV-MINE", mine, 1000m);
        SeedInvoice(db, "INV-OTHER", other, 2000m);
        var ctl = BuildController(db, uid);

        var page = PreviewOk(await ctl.Preview(new DynamicReceivableReportRequest
        {
            Fields = new() { "invoiceNumber" },
            PageSize = 100,
        }));
        Assert.Equal(1, page.Total);
        Assert.Equal("INV-MINE", page.Rows[0]["invoiceNumber"]);

        var outOfScope = PreviewOk(await ctl.Preview(new DynamicReceivableReportRequest
        {
            Fields = new() { "invoiceNumber" },
            CustomerId = other,
            PageSize = 100,
        }));
        Assert.Equal(0, outOfScope.Total);
        Assert.Empty(outOfScope.Rows);
    }

    [Fact]
    public async Task Preview_未映射业务员_空范围_不泄露任何客户()
    {
        using var db = TestDbFactory.Create();
        // 具备客户资料菜单授权，但登录账号没有对应员工（或非业务员）
        var role = SeedRole(db, "Sales-ghost");
        var user = SeedUser(db, "ghost");
        SeedUserRole(db, user.Id, role.Id);
        SeedRoleMenu(db, role.Id, SeedMenu(db, DynamicReceivableReportRules.RequiredMenuCode).Id);
        var customer = SeedCustomer(db, "G-C1", "幽灵客户", empId: 9999L);
        SeedInvoice(db, "INV-GHOST", customer.Id, 1000m);
        var ctl = BuildController(db, user.Id);

        var page = PreviewOk(await ctl.Preview(new DynamicReceivableReportRequest
        {
            Fields = new() { "invoiceNumber" },
            PageSize = 100,
        }));

        Assert.Equal(0, page.Total);
        Assert.Empty(page.Rows);
    }

    // ==================== 4. 剩余证据状态（known / unknown / over_allocated） ====================

    [Fact]
    public async Task Preview_已知与超额分摊_保留算术剩余证据状态()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedAuthorized(db);
        var customer = SeedCustomer(db, "C-KO", "对账客户");

        var known = SeedInvoice(db, "INV-KNOWN", customer.Id, 1000m);
        var over = SeedInvoice(db, "INV-OVER", customer.Id, 500m);

        var receipt1 = SeedReceipt(db, "RCP-KNOWN", customer.Id, 400m);
        SeedAllocation(db, known, receipt1, 400m);

        // 直接播种与源规则矛盾的超额分摊行（有效金额 600 > 发票 500），读取侧按 over_allocated 派生
        var receipt2 = SeedReceipt(db, "RCP-OVER", customer.Id, 600m);
        SeedAllocation(db, over, receipt2, 600m);

        var ctl = BuildController(db, uid);
        var page = PreviewOk(await ctl.Preview(new DynamicReceivableReportRequest
        {
            Fields = new() { "invoiceNumber", "grossAmount", "effectiveAmount", "remainingAmount", "remainingState", "allocationState" },
            PageSize = 100,
        }));

        var rows = page.Rows.ToDictionary(r => (string)r["invoiceNumber"]!);
        Assert.Equal(2, rows.Count);

        Assert.Equal(CustomerReceivableReconciliationRules.RemainingKnown, rows["INV-KNOWN"]["remainingState"]);
        Assert.Equal(1000m, rows["INV-KNOWN"]["grossAmount"]);
        Assert.Equal(400m, rows["INV-KNOWN"]["effectiveAmount"]);
        Assert.Equal(600m, rows["INV-KNOWN"]["remainingAmount"]);
        Assert.Equal(CustomerReceivableReconciliationRules.AllocationPartial, rows["INV-KNOWN"]["allocationState"]);

        Assert.Equal(CustomerReceivableReconciliationRules.RemainingOverAllocated, rows["INV-OVER"]["remainingState"]);
        Assert.Null(rows["INV-OVER"]["remainingAmount"]);
        Assert.Equal(CustomerReceivableReconciliationRules.AllocationOverAllocated, rows["INV-OVER"]["allocationState"]);
    }

    [Fact]
    public async Task Preview_草稿发票_剩余证据未知_不并入有效证据()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedAuthorized(db);
        var customer = SeedCustomer(db, "C-DRAFT", "草稿客户");
        SeedInvoice(db, "INV-DRAFT", customer.Id, 1000m, status: CustomerSalesInvoiceEvidenceRules.StatusDraft);
        var ctl = BuildController(db, uid);

        var page = PreviewOk(await ctl.Preview(new DynamicReceivableReportRequest
        {
            Fields = new() { "invoiceNumber", "isActiveEvidence", "remainingAmount", "remainingState", "allocationState" },
            InvoiceStatus = "draft",
            PageSize = 100,
        }));

        var row = Assert.Single(page.Rows);
        Assert.Equal(false, row["isActiveEvidence"]);
        Assert.Equal(CustomerReceivableReconciliationRules.RemainingUnknown, row["remainingState"]);
        Assert.Null(row["remainingAmount"]);
        Assert.Equal(CustomerReceivableReconciliationRules.AllocationNone, row["allocationState"]);
    }

    // ==================== 5. 只读不写库 ====================

    [Fact]
    public async Task Preview_只读不写库()
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

        var page = PreviewOk(await ctl.Preview(new DynamicReceivableReportRequest
        {
            Fields = new() { "invoiceNumber" },
            PageSize = 10,
        }));

        Assert.Equal(1, page.Total);
        Assert.Equal(0, counting.WriteCalls);
    }

    // ==================== 6. 只读计数上下文（断言不写库） ====================

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
