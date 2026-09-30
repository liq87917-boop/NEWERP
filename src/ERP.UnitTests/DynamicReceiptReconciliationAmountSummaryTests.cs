using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-172 动态客户订单与收款核对报表「当前授权预览页按客户 + 原币的订单金额与未关联收款金额汇总」单元测试。
/// 覆盖：汇总模式规范化（none / customerCurrency）、多客户多币种隔离、未知金额按未知、收款证据状态拆分、
/// 空页 / 分页边界、无效汇总模式（源读取前拒绝）、无菜单授权拒绝、受限制业务员范围与只读不写库。
/// <para>全部使用内存数据库（TestDbFactory），不连接 SQL Server、不启动 API、不执行任何 SQL / seed。</para>
/// </summary>
public class DynamicReceiptReconciliationAmountSummaryTests
{
    private static readonly DateTime AsOf = new(2026, 9, 25);
    private const long CustomerA = 967001L;
    private const long CustomerB = 967002L;
    private const long ProductA = 967101L;

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

    private static void SeedDepositApply(ErpDbContext db, string applyNo, long salesOrderId, decimal amount,
        Currency currency, DocumentStatus status, long customerId = CustomerA)
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

    private static DynamicReceiptReconciliationReportPageDto PreviewOk(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<ApiResponse<DynamicReceiptReconciliationReportPageDto>>(ok.Value);
        Assert.NotNull(resp.Data);
        return resp.Data!;
    }

    private static async Task<BusinessException> AssertBusinessAsync(int expectedCode, Func<Task> action)
    {
        var ex = await Assert.ThrowsAsync<BusinessException>(action);
        Assert.Equal(expectedCode, ex.Code);
        return ex;
    }

    // ==================== 1. 汇总模式规范化（fail closed） ====================

    [Theory]
    [InlineData(null, "none")]
    [InlineData("", "none")]
    [InlineData("   ", "none")]
    [InlineData("none", "none")]
    [InlineData("None", "none")]
    [InlineData("customerCurrency", "customerCurrency")]
    [InlineData("CustomerCurrency", "customerCurrency")]
    [InlineData("CUSTOMERCURRENCY", "customerCurrency")]
    public void NormalizeSummaryMode_接受有限汇总模式(string? input, string expected)
        => Assert.Equal(expected, DynamicReceiptReconciliationReportRules.NormalizeSummaryMode(input));

    [Theory]
    [InlineData("supplierCurrency")]
    [InlineData("currency")]
    [InlineData("customer")]
    [InlineData("total")]
    [InlineData("customerCurrencyShipment")]
    [InlineData("CUSTOMERCURRENCYTOTAL")]
    public void NormalizeSummaryMode_拒绝未知汇总模式(string input)
    {
        var ex = Assert.Throws<BusinessException>(() => DynamicReceiptReconciliationReportRules.NormalizeSummaryMode(input));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    // ==================== 2. 订单金额汇总（纯规则） ====================

    [Fact]
    public void BuildOrderSummaries_none模式返回空()
        => Assert.Empty(DynamicReceiptReconciliationReportRules.BuildOrderSummaries(
            new List<Dictionary<string, object?>> { new() { ["orderAmount"] = 1m } }, "none"));

    [Fact]
    public void BuildOrderSummaries_币种不合并()
    {
        var rows = new List<Dictionary<string, object?>>
        {
            new() { ["customerId"] = CustomerA, ["customerName"] = "甲客户", ["currency"] = "USD", ["orderAmount"] = 100m, ["linkedReceiptAmount"] = 0m, ["uncoveredAmount"] = 100m },
            new() { ["customerId"] = CustomerA, ["customerName"] = "甲客户", ["currency"] = "CNY", ["orderAmount"] = 200m, ["linkedReceiptAmount"] = 0m, ["uncoveredAmount"] = 200m },
        };

        var summaries = DynamicReceiptReconciliationReportRules.BuildOrderSummaries(rows, "customerCurrency");

        Assert.Equal(2, summaries.Count);
        Assert.Equal(100m, summaries.Single(s => s.Currency == "USD").OrderAmount);
        Assert.Equal(200m, summaries.Single(s => s.Currency == "CNY").OrderAmount);
    }

    [Fact]
    public void BuildOrderSummaries_已知链接与未覆盖金额求和()
    {
        var rows = new List<Dictionary<string, object?>>
        {
            new() { ["customerId"] = CustomerA, ["customerName"] = "甲客户", ["currency"] = "USD", ["orderAmount"] = 100m, ["linkedReceiptAmount"] = 30m, ["uncoveredAmount"] = 70m },
            new() { ["customerId"] = CustomerA, ["customerName"] = "甲客户", ["currency"] = "USD", ["orderAmount"] = 200m, ["linkedReceiptAmount"] = 50m, ["uncoveredAmount"] = 150m },
        };

        var summary = Assert.Single(DynamicReceiptReconciliationReportRules.BuildOrderSummaries(rows, "customerCurrency"));

        Assert.Equal(300m, summary.OrderAmount);
        Assert.Equal(80m, summary.LinkedReceiptAmount);
        Assert.Equal(220m, summary.UncoveredAmount);
        Assert.Equal(2, summary.KnownLinkedReceiptAmountRows);
        Assert.Equal(0, summary.UnknownLinkedReceiptAmountRows);
        Assert.Equal(2, summary.KnownUncoveredAmountRows);
        Assert.Equal(0, summary.UnknownUncoveredAmountRows);
    }

    [Fact]
    public void BuildOrderSummaries_未知链接与未覆盖金额按未知()
    {
        var rows = new List<Dictionary<string, object?>>
        {
            new() { ["customerId"] = CustomerA, ["customerName"] = "甲客户", ["currency"] = "USD", ["orderAmount"] = 100m, ["linkedReceiptAmount"] = null, ["uncoveredAmount"] = null },
            new() { ["customerId"] = CustomerA, ["customerName"] = "甲客户", ["currency"] = "USD", ["orderAmount"] = 200m, ["linkedReceiptAmount"] = 50m, ["uncoveredAmount"] = 150m },
        };

        var summary = Assert.Single(DynamicReceiptReconciliationReportRules.BuildOrderSummaries(rows, "customerCurrency"));

        Assert.Equal(2, summary.OrderCount);
        Assert.Equal(300m, summary.OrderAmount);
        Assert.Equal(1, summary.KnownLinkedReceiptAmountRows);
        Assert.Equal(1, summary.UnknownLinkedReceiptAmountRows);
        Assert.Null(summary.LinkedReceiptAmount);
        Assert.Equal(1, summary.KnownUncoveredAmountRows);
        Assert.Equal(1, summary.UnknownUncoveredAmountRows);
        Assert.Null(summary.UncoveredAmount);
    }

    // ==================== 3. 未关联收款金额汇总（纯规则） ====================

    [Fact]
    public void BuildReceiptSummaries_none模式返回空()
        => Assert.Empty(DynamicReceiptReconciliationReportRules.BuildReceiptSummaries(
            new List<Dictionary<string, object?>> { new() { ["amount"] = 1m } }, "none", false));

    [Fact]
    public void BuildReceiptSummaries_证据状态拆分与截断显式保留()
    {
        var rows = new List<Dictionary<string, object?>>
        {
            new() { ["customerId"] = CustomerA, ["customerName"] = "甲客户", ["currency"] = "USD", ["evidenceStatus"] = "active", ["amount"] = 10m },
            new() { ["customerId"] = CustomerA, ["customerName"] = "甲客户", ["currency"] = "USD", ["evidenceStatus"] = "active", ["amount"] = 20m },
            new() { ["customerId"] = CustomerA, ["customerName"] = "甲客户", ["currency"] = "USD", ["evidenceStatus"] = "pending", ["amount"] = 5m },
            new() { ["customerId"] = CustomerA, ["customerName"] = "甲客户", ["currency"] = "USD", ["evidenceStatus"] = "historical", ["amount"] = 3m },
        };

        var summaries = DynamicReceiptReconciliationReportRules.BuildReceiptSummaries(rows, "customerCurrency", truncated: true);

        Assert.Equal(3, summaries.Count);
        Assert.All(summaries, s => Assert.True(s.Truncated));
        Assert.Equal(30m, summaries.Single(s => s.EvidenceStatus == "active").Amount);
        Assert.Equal(5m, summaries.Single(s => s.EvidenceStatus == "pending").Amount);
        Assert.Equal(3m, summaries.Single(s => s.EvidenceStatus == "historical").Amount);
    }

    // ==================== 4. 控制器集成：无效模式 / 授权 / 范围 ====================

    [Fact]
    public async Task Preview_无效汇总模式在源读取前拒绝()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "甲客户");
        SeedOrder(db, "SO-1", CustomerA, Currency.USD, 100m);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => ctl.Preview(new DynamicReceiptReconciliationReportRequest { SummaryMode = "supplierCurrency" }));
    }

    [Fact]
    public async Task Preview_无销售订单菜单授权_拒绝()
    {
        using var db = TestDbFactory.Create();
        var user = SeedUser(db, "denied");
        var role = SeedRole(db, "NoMenu");
        SeedUserRole(db, user.Id, role.Id);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        await AssertBusinessAsync(ErrorCodes.Forbidden,
            () => ctl.Preview(new DynamicReceiptReconciliationReportRequest { SummaryMode = "customerCurrency" }));
    }

    [Fact]
    public async Task Preview_受限制业务员_仅本人客户进入汇总()
    {
        using var db = TestDbFactory.Create();
        var user = SeedUser(db, "alice");
        var role = SeedRole(db, "Sales");
        SeedUserRole(db, user.Id, role.Id);
        SeedRoleMenu(db, role.Id, SeedMenu(db, DynamicReceiptReconciliationReportRules.RequiredMenuCode).Id);
        var alice = SeedEmployee(db, "alice");
        var mine = SeedCustomer(db, CustomerA, "我的客户", alice.Id);
        SeedCustomer(db, CustomerB, "别人的客户", alice.Id + 1000);

        SeedOrder(db, "SO-MINE", mine.Id, Currency.USD, 100m);
        SeedOrder(db, "SO-OTHER", CustomerB, Currency.USD, 300m);
        SeedReceipt(db, "SK-MINE", mine.Id, 40m, Currency.USD, DocumentStatus.Approved);
        SeedReceipt(db, "SK-OTHER", CustomerB, 50m, Currency.USD, DocumentStatus.Approved);
        await db.SaveChangesAsync();

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var page = PreviewOk(await ctl.Preview(new DynamicReceiptReconciliationReportRequest
        { SummaryMode = "customerCurrency" }));

        Assert.Equal(1, page.Total);
        var orderSummary = Assert.Single(page.OrderSummaries!);
        Assert.Equal(mine.Id, orderSummary.CustomerId);
        Assert.Equal(100m, orderSummary.OrderAmount);
        var receiptSummary = Assert.Single(page.ReceiptSummaries!);
        Assert.Equal(mine.Id, receiptSummary.CustomerId);
        Assert.Equal(40m, receiptSummary.Amount);
    }

    // ==================== 5. 控制器集成：订单与未关联收款金额各自独立 ====================

    [Fact]
    public async Task Preview_customerCurrency_订单与未关联收款金额各自独立()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "甲客户");
        SeedOrder(db, "SO-1", CustomerA, Currency.USD, 100m);
        SeedOrder(db, "SO-2", CustomerA, Currency.USD, 200m);
        SeedReceipt(db, "SK-ACT", CustomerA, 10m, Currency.USD, DocumentStatus.Approved);
        SeedReceipt(db, "SK-PEND", CustomerA, 20m, Currency.USD, DocumentStatus.Pending);
        await db.SaveChangesAsync();

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var page = PreviewOk(await ctl.Preview(new DynamicReceiptReconciliationReportRequest
        {
            SummaryMode = "customerCurrency",
            ReceiptStatus = "all",
        }));

        Assert.Equal("customerCurrency", page.SummaryMode);

        var orderSummary = Assert.Single(page.OrderSummaries!);
        Assert.Equal(CustomerA, orderSummary.CustomerId);
        Assert.Equal("USD", orderSummary.Currency);
        Assert.Equal(2, orderSummary.OrderCount);
        Assert.Equal(300m, orderSummary.OrderAmount);

        Assert.Equal(2, page.ReceiptSummaries!.Count);
        Assert.Equal(10m, page.ReceiptSummaries.Single(s => s.EvidenceStatus == "active").Amount);
        Assert.Equal(20m, page.ReceiptSummaries.Single(s => s.EvidenceStatus == "pending").Amount);
        Assert.All(page.ReceiptSummaries, s => Assert.False(s.Truncated));
    }

    [Fact]
    public async Task Preview_订单无权威收款引用_链接与未覆盖金额按未知()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "甲客户");
        SeedOrder(db, "SO-1", CustomerA, Currency.USD, 100m);
        await db.SaveChangesAsync();

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var page = PreviewOk(await ctl.Preview(new DynamicReceiptReconciliationReportRequest
        { SummaryMode = "customerCurrency" }));

        var summary = Assert.Single(page.OrderSummaries!);
        Assert.Equal(100m, summary.OrderAmount);
        Assert.Equal(0, summary.KnownLinkedReceiptAmountRows);
        Assert.Equal(1, summary.UnknownLinkedReceiptAmountRows);
        Assert.Null(summary.LinkedReceiptAmount);
        Assert.Equal(0, summary.KnownUncoveredAmountRows);
        Assert.Equal(1, summary.UnknownUncoveredAmountRows);
        Assert.Null(summary.UncoveredAmount);
    }

    [Fact]
    public async Task Preview_已知链接与未覆盖金额求和()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "甲客户");
        var order = SeedOrder(db, "SO-1", CustomerA, Currency.USD, 100m);
        SeedDetail(db, order.Id, ProductA, 1m);
        SeedDepositApply(db, "DJ-1", order.Id, 30m, Currency.USD, DocumentStatus.Approved, CustomerA);
        await db.SaveChangesAsync();

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var page = PreviewOk(await ctl.Preview(new DynamicReceiptReconciliationReportRequest
        { SummaryMode = "customerCurrency" }));

        var summary = Assert.Single(page.OrderSummaries!);
        Assert.Equal(30m, summary.LinkedReceiptAmount);
        Assert.Equal(70m, summary.UncoveredAmount);
        Assert.Equal(1, summary.KnownLinkedReceiptAmountRows);
        Assert.Equal(0, summary.UnknownLinkedReceiptAmountRows);
        Assert.Equal(1, summary.KnownUncoveredAmountRows);
        Assert.Equal(0, summary.UnknownUncoveredAmountRows);
    }

    // ==================== 6. 空页 / 分页 / 只读 ====================

    [Fact]
    public async Task Preview_空页_汇总为空()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var page = PreviewOk(await ctl.Preview(new DynamicReceiptReconciliationReportRequest
        { SummaryMode = "customerCurrency" }));

        Assert.Equal(0, page.Total);
        Assert.Empty(page.OrderSummaries!);
        Assert.Empty(page.ReceiptSummaries!);
    }

    [Fact]
    public async Task Preview_金额汇总只统计当前页()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "甲客户");
        SeedOrder(db, "SO-1", CustomerA, Currency.USD, 100m);
        SeedOrder(db, "SO-2", CustomerA, Currency.USD, 100m);
        SeedOrder(db, "SO-3", CustomerA, Currency.USD, 100m);
        await db.SaveChangesAsync();

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var page = PreviewOk(await ctl.Preview(new DynamicReceiptReconciliationReportRequest
        {
            SummaryMode = "customerCurrency",
            PageSize = 2,
        }));

        Assert.Equal(3, page.Total);
        Assert.Equal(2, page.Rows.Count);
        var summary = Assert.Single(page.OrderSummaries!);
        Assert.Equal(2, summary.OrderCount);
        Assert.Equal(200m, summary.OrderAmount);
    }

    [Fact]
    public async Task Preview_汇总只读不写库()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "甲客户");
        SeedOrder(db, "SO-1", CustomerA, Currency.USD, 100m);
        SeedReceipt(db, "SK-1", CustomerA, 30m, Currency.USD, DocumentStatus.Approved);
        await db.SaveChangesAsync();

        var ordersBefore = db.SalesOrders.Count();
        var receiptsBefore = db.FinanceReceipts.Count();
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        await ctl.Preview(new DynamicReceiptReconciliationReportRequest { SummaryMode = "customerCurrency" });

        Assert.Equal(ordersBefore, db.SalesOrders.Count());
        Assert.Equal(receiptsBefore, db.FinanceReceipts.Count());
        Assert.DoesNotContain(db.ChangeTracker.Entries(), e => e.State != EntityState.Unchanged);
    }

    // ==================== 7. 汇总 DTO 结构边界（绝不呈现收款分配 / 应收余额 / 跨币种合计） ====================

    [Fact]
    public void SummaryDto_exposes_no_payment_allocation_or_receivable_balance_fields()
    {
        var orderNames = typeof(DynamicReceiptReconciliationReportOrderSummaryDto).GetProperties().Select(p => p.Name).ToList();
        Assert.Contains("CustomerId", orderNames);
        Assert.Contains("Currency", orderNames);
        Assert.Contains("OrderAmount", orderNames);
        Assert.Contains("LinkedReceiptAmount", orderNames);
        Assert.Contains("UncoveredAmount", orderNames);
        Assert.DoesNotContain(orderNames, n => n.Contains("Receivable", StringComparison.Ordinal));
        Assert.DoesNotContain(orderNames, n => n.Contains("Balance", StringComparison.Ordinal));
        Assert.DoesNotContain(orderNames, n => n.Contains("Allocation", StringComparison.Ordinal));
        Assert.DoesNotContain(orderNames, n => n.Contains("Paid", StringComparison.Ordinal));

        var receiptNames = typeof(DynamicReceiptReconciliationReportReceiptSummaryDto).GetProperties().Select(p => p.Name).ToList();
        Assert.Contains("CustomerId", receiptNames);
        Assert.Contains("Currency", receiptNames);
        Assert.Contains("EvidenceStatus", receiptNames);
        Assert.Contains("Amount", receiptNames);
        Assert.Contains("Truncated", receiptNames);
        Assert.DoesNotContain(receiptNames, n => n.Contains("Receivable", StringComparison.Ordinal));
        Assert.DoesNotContain(receiptNames, n => n.Contains("Balance", StringComparison.Ordinal));
    }
}
