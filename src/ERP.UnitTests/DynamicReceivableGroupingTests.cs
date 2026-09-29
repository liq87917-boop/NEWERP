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
/// ERP-120 动态客户应收账款证据报表「货币安全分组与页面小计」单元测试。
/// <para>页面小计语义：<see cref="DynamicReceivableReportRules.BuildGroupSubtotals"/> 只对「当前预览页」的已授权行聚合，
/// 分组内按币种分开统计条数、发票含税总额与有效已分摊金额（金额只对同币种求和、绝不跨币种相加）；
/// 剩余证据仅当组内全部行都「可确认」时给出金额，否则标注 unknown / over_allocated 且金额为 null（绝不轧为假余额）。</para>
/// 覆盖：混合币种不合并、月份边界、未选择字段（服务端补齐）、未知 / 超额分摊证据、空页、
/// 无效分组键（fail closed）与只读不写库。
/// <para>全部使用内存数据库（TestDbFactory），不连接 SQL Server、不启动 API、不执行任何 SQL / seed。</para>
/// </summary>
public class DynamicReceivableGroupingTests
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
        var employee = new BaseEmployee { EmployeeCode = code, EmployeeName = code, IsSalesman = true, Status = 1 };
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

    private static DynamicReceivableReportPageDto PreviewOk(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<ApiResponse<DynamicReceivableReportPageDto>>(ok.Value);
        return resp.Data!;
    }

    // ==================== 1. 分组键规范化（fail closed） ====================

    [Fact]
    public void NormalizeGroupBy_空与合法取值_规范化为小写()
    {
        Assert.Equal(DynamicReceivableReportRules.GroupNone, DynamicReceivableReportRules.NormalizeGroupBy(null));
        Assert.Equal(DynamicReceivableReportRules.GroupNone, DynamicReceivableReportRules.NormalizeGroupBy("  "));
        Assert.Equal(DynamicReceivableReportRules.GroupNone, DynamicReceivableReportRules.NormalizeGroupBy("none"));
        Assert.Equal(DynamicReceivableReportRules.GroupCustomer, DynamicReceivableReportRules.NormalizeGroupBy("Customer"));
        Assert.Equal(DynamicReceivableReportRules.GroupMonth, DynamicReceivableReportRules.NormalizeGroupBy("MONTH"));
    }

    [Fact]
    public async Task Preview_无效分组键_拒绝()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedAuthorized(db);
        var ctl = BuildController(db, uid);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Preview(
            new DynamicReceivableReportRequest { GroupBy = "quarter" }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    // ==================== 2. 客户分组（币种分开、混合币种不合并） ====================

    [Fact]
    public async Task GroupBy_客户_按币种分开小计_混合币种不合并()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedAuthorized(db);
        var customer = SeedCustomer(db, "C-MIX", "混合币种客户");
        SeedInvoice(db, "INV-USD", customer.Id, 1000m, currency: "USD");
        SeedInvoice(db, "INV-CNY", customer.Id, 500m, currency: "CNY");
        var ctl = BuildController(db, uid);

        var page = PreviewOk(await ctl.Preview(new DynamicReceivableReportRequest
        {
            GroupBy = "customer",
            PageSize = 100,
        }));

        Assert.Equal("customer", page.GroupBy);
        Assert.Equal(2, page.Total);
        var g = Assert.Single(page.Groups!);
        Assert.Equal($"customer:{customer.Id}", g.Key);
        Assert.Equal(2, g.Subtotals.Count);

        // 币种按枚举顺序：CNY 在 USD 之前，金额只对同币种求和、绝不跨币种相加
        var cny = g.Subtotals[0];
        Assert.Equal("CNY", cny.Currency);
        Assert.Equal(1, cny.Count);
        Assert.Equal(500m, cny.GrossAmount);
        Assert.Equal(0m, cny.EffectiveAllocatedAmount);
        Assert.Equal(CustomerReceivableReconciliationRules.RemainingKnown, cny.RemainingState);
        Assert.Equal(500m, cny.RemainingAmount);

        var usd = g.Subtotals[1];
        Assert.Equal("USD", usd.Currency);
        Assert.Equal(1, usd.Count);
        Assert.Equal(1000m, usd.GrossAmount);
        Assert.Equal(0m, usd.EffectiveAllocatedAmount);
        Assert.Equal(CustomerReceivableReconciliationRules.RemainingKnown, usd.RemainingState);
        Assert.Equal(1000m, usd.RemainingAmount);
    }

    // ==================== 3. 月份分组（月份边界不跨月） ====================

    [Fact]
    public async Task GroupBy_月份_月份边界_不跨月()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedAuthorized(db);
        var customer = SeedCustomer(db, "C-MONTH", "月份客户");
        SeedInvoice(db, "INV-AUG", customer.Id, 100m, invoiceDate: new DateTime(2026, 8, 31));
        SeedInvoice(db, "INV-SEP", customer.Id, 200m, invoiceDate: new DateTime(2026, 9, 1));
        var ctl = BuildController(db, uid);

        var page = PreviewOk(await ctl.Preview(new DynamicReceivableReportRequest
        {
            GroupBy = "month",
            PageSize = 100,
        }));

        Assert.Equal("month", page.GroupBy);
        Assert.Equal(2, page.Total);
        Assert.Equal(2, page.Groups!.Count);

        // 月份按年月升序、确定性排序
        Assert.Equal("month:2026-08", page.Groups[0].Key);
        Assert.Equal("2026年8月", page.Groups[0].Label);
        Assert.Equal("month:2026-09", page.Groups[1].Key);
        Assert.Equal("2026年9月", page.Groups[1].Label);

        Assert.Equal(100m, page.Groups[0].Subtotals[0].GrossAmount);
        Assert.Equal(200m, page.Groups[1].Subtotals[0].GrossAmount);
    }

    // ==================== 4. 未选择字段（服务端补齐分组所需字段） ====================

    [Fact]
    public async Task GroupBy_未选择字段_服务端补齐_仍可小计()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedAuthorized(db);
        var customer = SeedCustomer(db, "C-OMIT", "省略字段客户");
        SeedInvoice(db, "INV-OMIT", customer.Id, 1000m);
        var ctl = BuildController(db, uid);

        // 只选择 invoiceNumber，未选择 currency / grossAmount / customerId 等分组字段
        var page = PreviewOk(await ctl.Preview(new DynamicReceivableReportRequest
        {
            Fields = new() { "invoiceNumber" },
            GroupBy = "customer",
            PageSize = 100,
        }));

        Assert.Equal(1, page.Total);
        var g = Assert.Single(page.Groups!);
        Assert.Equal($"customer:{customer.Id}", g.Key);
        var s = Assert.Single(g.Subtotals);
        Assert.Equal("USD", s.Currency);
        Assert.Equal(1000m, s.GrossAmount);
        Assert.Equal(0m, s.EffectiveAllocatedAmount);
    }

    // ==================== 5. 未知 / 超额分摊证据（绝不轧为假余额） ====================

    [Fact]
    public async Task GroupBy_未知证据_组内剩余标注未知_金额为null()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedAuthorized(db);
        var customer = SeedCustomer(db, "C-UNKNOWN", "未知证据客户");
        SeedInvoice(db, "INV-KNOWN", customer.Id, 1000m);
        // 草稿发票：非有效证据，remainingState = unknown，remainingAmount = null
        SeedInvoice(db, "INV-DRAFT", customer.Id, 500m, status: CustomerSalesInvoiceEvidenceRules.StatusDraft);
        var ctl = BuildController(db, uid);

        var page = PreviewOk(await ctl.Preview(new DynamicReceivableReportRequest
        {
            GroupBy = "customer",
            InvoiceStatus = "all",
            PageSize = 100,
        }));

        Assert.Equal(2, page.Total);
        var g = Assert.Single(page.Groups!);
        var s = Assert.Single(g.Subtotals);
        Assert.Equal("USD", s.Currency);
        Assert.Equal(2, s.Count);
        Assert.Equal(1500m, s.GrossAmount);
        Assert.Equal(0m, s.EffectiveAllocatedAmount);
        // 任一行缺乏已知有效剩余证据 → 该币种小计的剩余证据标注未知，金额为 null（绝不轧为假余额）
        Assert.Equal(CustomerReceivableReconciliationRules.RemainingUnknown, s.RemainingState);
        Assert.Null(s.RemainingAmount);
    }

    [Fact]
    public async Task GroupBy_超额分摊证据_组内剩余标注超额分摊_金额为null()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedAuthorized(db);
        var customer = SeedCustomer(db, "C-OVER", "超额分摊客户");

        var known = SeedInvoice(db, "INV-KNOWN", customer.Id, 1000m);
        var over = SeedInvoice(db, "INV-OVER", customer.Id, 500m);

        var receiptKnown = SeedReceipt(db, "RCP-KNOWN", customer.Id, 400m);
        SeedAllocation(db, known, receiptKnown, 400m);

        // 与源规则矛盾的超额分摊（有效金额 600 > 发票 500）→ remainingState = over_allocated
        var receiptOver = SeedReceipt(db, "RCP-OVER", customer.Id, 600m);
        SeedAllocation(db, over, receiptOver, 600m);

        var ctl = BuildController(db, uid);
        var page = PreviewOk(await ctl.Preview(new DynamicReceivableReportRequest
        {
            GroupBy = "customer",
            PageSize = 100,
        }));

        Assert.Equal(2, page.Total);
        var g = Assert.Single(page.Groups!);
        var s = Assert.Single(g.Subtotals);
        Assert.Equal("USD", s.Currency);
        Assert.Equal(2, s.Count);
        Assert.Equal(1500m, s.GrossAmount);
        Assert.Equal(1000m, s.EffectiveAllocatedAmount);
        // 存在超额分摊矛盾 → 该币种小计的剩余证据标注超额分摊（无效），金额为 null（绝不轧为假余额）
        Assert.Equal(CustomerReceivableReconciliationRules.RemainingOverAllocated, s.RemainingState);
        Assert.Null(s.RemainingAmount);
    }

    // ==================== 6. 空页 ====================

    [Fact]
    public async Task GroupBy_空页_无分组小计()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedAuthorized(db);
        var ctl = BuildController(db, uid);

        var page = PreviewOk(await ctl.Preview(new DynamicReceivableReportRequest
        {
            GroupBy = "customer",
            PageSize = 100,
        }));

        Assert.Equal(0, page.Total);
        Assert.Empty(page.Rows);
        Assert.Equal("customer", page.GroupBy);
        Assert.NotNull(page.Groups);
        Assert.Empty(page.Groups);
    }

    // ==================== 7. 范围受限（小计只来自授权可见行） ====================

    [Fact]
    public async Task GroupBy_越界业务员_仅本人客户分组()
    {
        using var db = TestDbFactory.Create();
        var (uid, mine, other) = SeedRestrictedAuthorized(db, "grp-scope");
        SeedInvoice(db, "INV-MINE", mine, 1000m);
        SeedInvoice(db, "INV-OTHER", other, 2000m);
        var ctl = BuildController(db, uid);

        var page = PreviewOk(await ctl.Preview(new DynamicReceivableReportRequest
        {
            GroupBy = "customer",
            PageSize = 100,
        }));

        Assert.Equal(1, page.Total);
        var g = Assert.Single(page.Groups!);
        Assert.Equal($"customer:{mine}", g.Key);
        var s = Assert.Single(g.Subtotals);
        Assert.Equal(1000m, s.GrossAmount);
    }

    // ==================== 8. 只读不写库 ====================

    [Fact]
    public async Task GroupBy_只读不写库()
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
            GroupBy = "customer",
            PageSize = 10,
        }));

        Assert.Equal(1, page.Total);
        Assert.NotEmpty(page.Groups!);
        Assert.Equal(0, counting.WriteCalls);
    }

    // ==================== 9. 只读计数上下文（断言不写库） ====================

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
