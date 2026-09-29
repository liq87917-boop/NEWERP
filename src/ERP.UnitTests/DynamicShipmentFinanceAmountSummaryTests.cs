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
/// ERP-162 动态销售订单出货 / 财务进度报表「当前授权预览页按客户 + 原币（可选出货状态 / 收款链接状态）的已知金额汇总」单元测试。
/// <para>金额汇总语义：<see cref="DynamicShipmentFinanceReportRules.BuildAmountSummaries"/> 只汇总「当前授权预览页」的销售订单出货 / 财务进度证据行：
/// 客户与币种是强制分组边界，订单金额保持原币证据（直接求和，绝不跨币种合并或换算）；linked / uncovered / submitted 合计只要任一行金额未知
/// （null，即未链接 / 命中派生上限）即整体为 null（绝不轧为 0 或给部分合计），并显式给出已知 / 未知行数；uncoveredAmount 只作「未覆盖金额」，
/// 绝不是应收余额或收款授权。</para>
/// 覆盖：汇总模式规范化（全模式 + 大小写不敏感 + 默认 none）、多客户多币种隔离、未知链接与未知金额按未知、出货状态 / 收款链接状态拆分、
/// 空页 / 分页边界、无效汇总模式（fail closed，源读取之前拒绝）、无销售订单菜单授权（权限不足）、无身份（未认证）、受限制业务员范围与只读不写库。
/// <para>全部使用内存数据库（TestDbFactory），不连接 SQL Server、不启动 API、不执行任何 SQL / seed。</para>
/// </summary>
public class DynamicShipmentFinanceAmountSummaryTests
{
    private static readonly DateTime AsOf = new(2026, 9, 24);
    private const long CustomerA = 957001L;
    private const long CustomerB = 957002L;
    private const long ProductA = 957101L;

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

    /// <summary>播种一个「系统内置角色 + 销售订单菜单授权」用户</summary>
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

    private static DynamicShipmentFinanceReportPageDto PreviewOk(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<ApiResponse<DynamicShipmentFinanceReportPageDto>>(ok.Value);
        Assert.NotNull(resp.Data);
        return resp.Data!;
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
    [InlineData("customerCurrencyShipment", "customerCurrencyShipment")]
    [InlineData("CUSTOMERCURRENCYSHIPMENT", "customerCurrencyShipment")]
    [InlineData("customerCurrencyFinance", "customerCurrencyFinance")]
    [InlineData("CustomerCurrencyFinance", "customerCurrencyFinance")]
    public void NormalizeSummaryMode_accepts_only_known_modes(string? input, string expected)
        => Assert.Equal(expected, DynamicShipmentFinanceReportRules.NormalizeSummaryMode(input));

    [Theory]
    [InlineData("grandTotal")]
    [InlineData("customerCurrencyShip")]
    [InlineData("customerCurrencyFin")]
    [InlineData("currencyCustomer")]
    [InlineData("receivableBalance")]
    public void NormalizeSummaryMode_rejects_unknown_modes(string input)
    {
        var ex = Assert.Throws<BusinessException>(() =>
            DynamicShipmentFinanceReportRules.NormalizeSummaryMode(input));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    // ==================== 2. 客户 + 币种强制分组边界（原币隔离） ====================

    [Fact]
    public async Task Preview_customerCurrency_groups_by_customer_and_currency_without_conversion()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "甲客户");
        SeedCustomer(db, CustomerB, "乙客户");

        var aUsd1 = SeedOrder(db, "SO-SM-1", CustomerA, Currency.USD, 1000m);
        SeedDepositApply(db, "DEP-1", aUsd1.Id, CustomerA, 300m, Currency.USD, DocumentStatus.Approved);
        var aUsd2 = SeedOrder(db, "SO-SM-2", CustomerA, Currency.USD, 500m);
        SeedDepositApply(db, "DEP-2", aUsd2.Id, CustomerA, 100m, Currency.USD, DocumentStatus.Approved);
        var aCny = SeedOrder(db, "SO-SM-3", CustomerA, Currency.CNY, 700m);
        SeedDepositApply(db, "DEP-3", aCny.Id, CustomerA, 700m, Currency.CNY, DocumentStatus.Approved);
        SeedOrder(db, "SO-SM-4", CustomerB, Currency.USD, 200m); // 未链接：金额未知
        await db.SaveChangesAsync();

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var page = PreviewOk(await ctl.Preview(new DynamicShipmentFinanceReportRequest
        { SummaryMode = "customerCurrency", PageSize = 50 }));

        Assert.Equal("customerCurrency", page.SummaryMode);
        Assert.Equal(3, page.Summaries!.Count);

        var byKey = page.Summaries.ToDictionary(s => (s.CustomerId, s.Currency));
        var aUsd = byKey[(CustomerA, "USD")];
        Assert.Equal("甲客户", aUsd.CustomerName);
        Assert.Equal(2, aUsd.OrderCount);
        Assert.Equal(1500m, aUsd.OrderAmount);       // 原币直接求和，绝不换算
        Assert.Equal(400m, aUsd.LinkedAmount);
        Assert.Equal(1100m, aUsd.UncoveredAmount);
        Assert.Equal(0m, aUsd.SubmittedAmount);

        var aCnySummary = byKey[(CustomerA, "CNY")];
        Assert.Equal(1, aCnySummary.OrderCount);
        Assert.Equal(700m, aCnySummary.OrderAmount);
        Assert.Equal(700m, aCnySummary.LinkedAmount);
        Assert.Equal(0m, aCnySummary.UncoveredAmount);

        var bUsd = byKey[(CustomerB, "USD")];
        Assert.Equal("乙客户", bUsd.CustomerName);
        Assert.Equal(1, bUsd.OrderCount);
        Assert.Equal(200m, bUsd.OrderAmount);
        Assert.Null(bUsd.LinkedAmount);              // 未链接 → 未知，绝不轧为 0
        Assert.Null(bUsd.UncoveredAmount);
        Assert.Null(bUsd.SubmittedAmount);
    }

    // ==================== 3. 未知链接 / 未知金额 → 合计 null + 已知 / 未知行数 ====================

    [Fact]
    public async Task Preview_unknown_contributing_amounts_make_sum_null_with_explicit_known_unknown_counts()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "甲客户");

        var linked = SeedOrder(db, "SO-UK-1", CustomerA, Currency.USD, 1000m);
        SeedDepositApply(db, "DEP-UK-1", linked.Id, CustomerA, 300m, Currency.USD, DocumentStatus.Approved);
        SeedOrder(db, "SO-UK-2", CustomerA, Currency.USD, 500m); // 未链接 → linked/uncovered/submitted 未知
        await db.SaveChangesAsync();

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var page = PreviewOk(await ctl.Preview(new DynamicShipmentFinanceReportRequest
        { SummaryMode = "customerCurrency", PageSize = 50 }));

        var summary = Assert.Single(page.Summaries!);
        Assert.Equal(1500m, summary.OrderAmount);
        Assert.Equal(1, summary.KnownLinkedAmountRows);
        Assert.Equal(1, summary.UnknownLinkedAmountRows);
        Assert.Null(summary.LinkedAmount);           // 任一行未知 → 整体未知
        Assert.Equal(1, summary.KnownUncoveredAmountRows);
        Assert.Equal(1, summary.UnknownUncoveredAmountRows);
        Assert.Null(summary.UncoveredAmount);
        Assert.Equal(1, summary.KnownSubmittedAmountRows);
        Assert.Equal(1, summary.UnknownSubmittedAmountRows);
        Assert.Null(summary.SubmittedAmount);
    }

    // ==================== 4. 出货状态拆分（customerCurrencyShipment） ====================

    [Fact]
    public async Task Preview_customerCurrencyShipment_splits_by_shipment_state()
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

        var page = PreviewOk(await ctl.Preview(new DynamicShipmentFinanceReportRequest
        { SummaryMode = "customerCurrencyShipment", PageSize = 50 }));

        Assert.Equal("customerCurrencyShipment", page.SummaryMode);
        Assert.Equal(2, page.Summaries!.Count);

        var byStatus = page.Summaries.ToDictionary(s => s.ShipmentStatus!);
        Assert.Equal(1, byStatus[SalesOrderProgress.ShipmentNone].OrderCount);
        Assert.Equal(1000m, byStatus[SalesOrderProgress.ShipmentNone].OrderAmount);
        Assert.Equal(1, byStatus[SalesOrderProgress.ShipmentComplete].OrderCount);
        Assert.Equal(500m, byStatus[SalesOrderProgress.ShipmentComplete].OrderAmount);
        Assert.All(page.Summaries, s => Assert.Null(s.FinanceLinkStatus)); // 该模式不引入收款链接维度
    }

    // ==================== 5. 收款链接状态拆分（customerCurrencyFinance） ====================

    [Fact]
    public async Task Preview_customerCurrencyFinance_splits_by_finance_link_state()
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

        var page = PreviewOk(await ctl.Preview(new DynamicShipmentFinanceReportRequest
        { SummaryMode = "customerCurrencyFinance", PageSize = 50 }));

        Assert.Equal("customerCurrencyFinance", page.SummaryMode);
        Assert.Equal(2, page.Summaries!.Count);

        var byStatus = page.Summaries.ToDictionary(s => s.FinanceLinkStatus!);
        Assert.Equal(300m, byStatus[SalesOrderProgress.LinkLinked].LinkedAmount);
        Assert.Equal(1, byStatus[SalesOrderProgress.LinkLinked].KnownLinkedAmountRows);
        Assert.Null(byStatus[SalesOrderProgress.LinkUnlinked].LinkedAmount); // 未链接金额保持未知
        Assert.Equal(1, byStatus[SalesOrderProgress.LinkUnlinked].UnknownLinkedAmountRows);
        Assert.All(page.Summaries, s => Assert.Null(s.ShipmentStatus));      // 该模式不引入出货维度
    }

    // ==================== 6. 空页 / 分页边界 ====================

    [Fact]
    public async Task Preview_empty_page_returns_empty_summaries()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var page = PreviewOk(await ctl.Preview(new DynamicShipmentFinanceReportRequest
        { SummaryMode = "customerCurrency", PageSize = 50 }));

        Assert.Equal("customerCurrency", page.SummaryMode);
        Assert.Empty(page.Summaries!);
    }

    [Fact]
    public async Task Preview_summaries_only_count_the_current_page()
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

        var page1 = PreviewOk(await ctl.Preview(new DynamicShipmentFinanceReportRequest
        { SummaryMode = "customerCurrency", PageSize = 2, Page = 1 }));
        var page2 = PreviewOk(await ctl.Preview(new DynamicShipmentFinanceReportRequest
        { SummaryMode = "customerCurrency", PageSize = 2, Page = 2 }));

        var s1 = Assert.Single(page1.Summaries!);
        var s2 = Assert.Single(page2.Summaries!);
        Assert.Equal(2, s1.OrderCount);
        Assert.Equal(200m, s1.OrderAmount);
        Assert.Equal(1, s2.OrderCount);
        Assert.Equal(100m, s2.OrderAmount);
        Assert.Equal(3, page1.Total);
    }

    // ==================== 7. 无效汇总模式（源读取之前拒绝） ====================

    [Fact]
    public async Task Preview_rejects_invalid_summary_mode_before_source_reads()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Preview(
            new DynamicShipmentFinanceReportRequest { SummaryMode = "grandTotal" }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    // ==================== 8. 授权（fail closed） ====================

    [Fact]
    public async Task Preview_summary_denied_without_menu()
    {
        using var db = TestDbFactory.Create();
        var user = SeedUser(db, "no-menu");
        var role = SeedRole(db, "NoMenu", isSystem: false);
        SeedUserRole(db, user.Id, role.Id);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Preview(
            new DynamicShipmentFinanceReportRequest { SummaryMode = "customerCurrency" }));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    [Fact]
    public async Task Preview_summary_denied_without_identity()
    {
        using var db = TestDbFactory.Create();
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, null);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Preview(
            new DynamicShipmentFinanceReportRequest { SummaryMode = "customerCurrency" }));
        Assert.Equal(ErrorCodes.Unauthorized, ex.Code);
    }

    [Fact]
    public async Task Preview_summary_scopes_restricted_salesperson_to_assigned_customers()
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

        var page = PreviewOk(await ctl.Preview(new DynamicShipmentFinanceReportRequest
        { SummaryMode = "customerCurrency", PageSize = 50 }));

        var summary = Assert.Single(page.Summaries!);
        Assert.Equal(mine.Id, summary.CustomerId);
        Assert.Equal(100m, summary.OrderAmount);
    }

    // ==================== 9. 只读不写库 ====================

    [Fact]
    public async Task Preview_summary_does_not_write_to_database()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "甲客户");
        var order = SeedOrder(db, "SO-NW-1", CustomerA, Currency.USD, 500m);
        SeedDetail(db, order.Id, ProductA, 5m);
        SeedStockOut(db, "CK-NW-1", order.Id, CustomerA, DocumentStatus.Approved, (ProductA, 2m));
        SeedDepositApply(db, "DEP-NW-1", order.Id, CustomerA, 100m, Currency.USD, DocumentStatus.Approved);
        await db.SaveChangesAsync();

        var salesOrdersBefore = db.SalesOrders.Count();
        var detailsBefore = db.SalesOrderDetails.Count();
        var stockOutsBefore = db.StockOuts.Count();
        var appliesBefore = db.FinanceDepositApplies.Count();

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);
        await ctl.Preview(new DynamicShipmentFinanceReportRequest { SummaryMode = "customerCurrencyShipment" });

        Assert.Equal(salesOrdersBefore, db.SalesOrders.Count());
        Assert.Equal(detailsBefore, db.SalesOrderDetails.Count());
        Assert.Equal(stockOutsBefore, db.StockOuts.Count());
        Assert.Equal(appliesBefore, db.FinanceDepositApplies.Count());
        Assert.DoesNotContain(db.ChangeTracker.Entries(), e => e.State != EntityState.Unchanged);
    }

    // ==================== 10. 汇总 DTO 结构边界（绝不呈现应收余额 / 收款授权） ====================

    [Fact]
    public void SummaryDto_exposes_no_receivable_balance_or_payment_authority_fields()
    {
        var names = typeof(DynamicShipmentFinanceReportSummaryDto).GetProperties().Select(p => p.Name).ToList();
        Assert.Contains("CustomerId", names);
        Assert.Contains("Currency", names);
        Assert.Contains("OrderAmount", names);
        Assert.Contains("LinkedAmount", names);
        Assert.Contains("UncoveredAmount", names);
        Assert.Contains("SubmittedAmount", names);
        Assert.Contains("KnownLinkedAmountRows", names);
        Assert.Contains("UnknownLinkedAmountRows", names);
        Assert.DoesNotContain(names, n => n.Contains("Receivable", StringComparison.Ordinal));
        Assert.DoesNotContain(names, n => n.Contains("Ledger", StringComparison.Ordinal));
        Assert.DoesNotContain(names, n => n.Contains("Balance", StringComparison.Ordinal));
        Assert.DoesNotContain(names, n => n.Contains("Authorization", StringComparison.Ordinal));
        Assert.DoesNotContain(names, n => n.Contains("Payable", StringComparison.Ordinal));
        Assert.DoesNotContain(names, n => n.Contains("DueDate", StringComparison.Ordinal));
        Assert.DoesNotContain(names, n => n.Contains("Aging", StringComparison.Ordinal));
    }
}
