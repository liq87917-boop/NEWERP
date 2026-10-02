using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-224 动态订单利润暂估报表「分币种汇总」聚焦单元测试（纯规则 + 经授权预览端点）。
/// 覆盖：按规范化原币键合并（仅相等币种键、绝不跨币种合计）、未知币种显式保留（有订单数、金额 null、绝不回落为 0）、
/// 签名销售额小计（可为负）、汇总列固定（原币 / 订单数 / 销售额，与明细页选定列无关）、
/// 空结果、汇总与页码 / 页大小 / 选定列无关，以及来源超限 fail closed（不返回部分汇总）。
/// <para>全部使用内存数据库（TestDbFactory），不连接 SQL Server、不启动 API、不执行任何 SQL / seed、不运行浏览器验收。</para>
/// </summary>
public class OrderProfitCurrencySummaryTests
{
    private const string OrderProfitMenuCode = "order-profit";
    private static readonly DateTime Start = new(2026, 9, 1);
    private static readonly DateTime End = new(2026, 9, 30);

    // ==================== 纯规则：分币种汇总 ====================

    private static ReportDtos.OrderProfitItem Item(string currency, decimal salesAmount, long orderId = 0)
        => new()
        {
            OrderId = orderId,
            CustomerId = 1,
            OrderNo = "SO-" + orderId,
            OrderDate = new DateTime(2026, 9, 10),
            CustomerName = "客户",
            Currency = currency,
            CurrencyLabel = currency,
            SalesAmount = salesAmount,
        };

    private static DynamicOrderProfitEstimateSummaryDto Build(params ReportDtos.OrderProfitItem[] items)
        => DynamicOrderProfitEstimateReportRules.BuildSummary(items);

    [Fact]
    public void 汇总_按规范化币种合并_稳定升序_不跨币种合计()
    {
        var summary = Build(
            Item("usd", 100m, 1),
            Item("USD", 200m, 2),
            Item("EUR", 500m, 3));

        Assert.Equal(2, summary.CurrencyCount);
        Assert.Equal(new[] { "EUR", "USD" }, summary.Rows.Select(r => r.Currency).ToArray());

        var usd = summary.Rows.Single(r => r.Currency == "USD");
        Assert.Equal(2, usd.OrderCount);
        Assert.Equal(300m, usd.SalesAmount);

        var eur = summary.Rows.Single(r => r.Currency == "EUR");
        Assert.Equal(1, eur.OrderCount);
        Assert.Equal(500m, eur.SalesAmount);

        // 绝不出现混合币种合计行
        Assert.DoesNotContain(summary.Rows, r => string.IsNullOrWhiteSpace(r.Currency));
        Assert.DoesNotContain(summary.Rows, r => r.SalesAmount == 800m);
    }

    [Fact]
    public void 汇总_未知币种_显式保留独立桶_金额null_绝不0()
    {
        var summary = Build(
            Item("", 10m, 1),            // 空值 → 未知币种
            Item("未知币种", 20m, 2),    // 显式未知桶 → 未知币种
            Item("BTC", 30m, 3),         // 非法取值 → 未知币种
            Item("USD", 40m, 4));

        Assert.Equal(2, summary.CurrencyCount);
        Assert.Equal(new[] { "USD", "未知币种" }, summary.Rows.Select(r => r.Currency).ToArray());

        var unknown = summary.Rows.Single(r => r.Currency == "未知币种");
        Assert.Equal(3, unknown.OrderCount);
        Assert.Null(unknown.SalesAmount);   // 未知币种金额为 null，绝不回落为 0

        var usd = summary.Rows.Single(r => r.Currency == "USD");
        Assert.Equal(1, usd.OrderCount);
        Assert.Equal(40m, usd.SalesAmount);
    }

    [Fact]
    public void 汇总_签名销售额小计_可为负_仅同币种求和()
    {
        var summary = Build(
            Item("USD", 100m, 1),
            Item("USD", -30m, 2),
            Item("EUR", 500m, 3));

        var usd = summary.Rows.Single(r => r.Currency == "USD");
        Assert.Equal(70m, usd.SalesAmount);

        var eur = summary.Rows.Single(r => r.Currency == "EUR");
        Assert.Equal(500m, eur.SalesAmount);

        // 绝不产生成本 / 利润 / 当前价估算或跨币种总额列
        Assert.Equal(
            new[] { "currency", "orderCount", "salesAmount" },
            summary.Columns.Select(c => c.Key).ToArray());
    }

    [Fact]
    public void 汇总_空结果_空行且覆盖文案与列头仍存在()
    {
        var summary = Build();

        Assert.Equal(0, summary.CurrencyCount);
        Assert.Empty(summary.Rows);
        Assert.Equal(DynamicOrderProfitEstimateReportRules.CurrencySummaryCoverageText, summary.CoverageText);
        Assert.Equal(
            new[] { "currency", "orderCount", "salesAmount" },
            summary.Columns.Select(c => c.Key).ToArray());
    }

    [Fact]
    public void 汇总_与页码页大小选定列无关()
    {
        var items = new[]
        {
            Item("USD", 100m, 1),
            Item("USD", 200m, 2),
            Item("EUR", 500m, 3),
        };

        var page1 = DynamicOrderProfitEstimateReportRules.BuildPage(
            items, new[] { "orderNo" }, 1, 1, Start, End);
        var page2 = DynamicOrderProfitEstimateReportRules.BuildPage(
            items, new[] { "orderId", "currency", "salesAmount" }, 2, 1, Start, End);

        Assert.NotNull(page1.Summary);
        Assert.NotNull(page2.Summary);
        Assert.Equal(page1.Summary!.CurrencyCount, page2.Summary!.CurrencyCount);
        Assert.Equal(page1.Summary!.Rows.Count, page2.Summary!.Rows.Count);

        foreach (var expected in page1.Summary!.Rows)
        {
            var actual = page2.Summary!.Rows.Single(r => r.Currency == expected.Currency);
            Assert.Equal(expected.OrderCount, actual.OrderCount);
            Assert.Equal(expected.SalesAmount, actual.SalesAmount);
        }
    }

    // ==================== 经授权预览端点：汇总覆盖全部匹配订单 ====================

    private static BaseCustomer SeedCustomer(ErpDbContext db, string code, string name)
    {
        var customer = new BaseCustomer { CustomerCode = code, CustomerName = name, Status = 1 };
        db.BaseCustomers.Add(customer);
        db.SaveChanges();
        return customer;
    }

    private static SalesOrder SeedOrder(ErpDbContext db, string orderNo, long customerId, Currency currency, decimal totalAmount)
    {
        var order = new SalesOrder
        {
            OrderNo = orderNo,
            OrderDate = new DateTime(2026, 9, 10),
            CustomerId = customerId,
            Status = DocumentStatus.Approved,
            Currency = currency,
            TotalAmount = totalAmount,
        };
        db.SalesOrders.Add(order);
        db.SaveChanges();
        return order;
    }

    private static SysUser SeedAuthorizedUser(ErpDbContext db, string userName)
    {
        var role = new SysRole { RoleName = "Priv", RoleCode = "Priv", IsSystem = true };
        db.SysRoles.Add(role);
        db.SaveChanges();
        var user = new SysUser
        {
            UserName = userName,
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = userName,
            Status = UserStatus.Enabled,
        };
        db.SysUsers.Add(user);
        db.SaveChanges();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        db.SaveChanges();
        var menu = new SysMenu { MenuName = "订单利润暂估表", MenuCode = OrderProfitMenuCode, MenuType = MenuType.Menu };
        db.SysMenus.Add(menu);
        db.SaveChanges();
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
        db.SaveChanges();
        return user;
    }

    private static DynamicOrderProfitEstimateReportController BuildController(ErpDbContext db, long? userId)
    {
        var ctl = new DynamicOrderProfitEstimateReportController(db, new ReportService(db));
        TestAuth.SetUser(ctl, userId);
        return ctl;
    }

    private static DynamicOrderProfitEstimateReportPageDto OkPage(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<ApiResponse<DynamicOrderProfitEstimateReportPageDto>>(ok.Value);
        return resp.Data!;
    }

    [Fact]
    public async Task 预览端点_汇总覆盖全部匹配订单_与分页无关()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "opd-summary");
        var customer = SeedCustomer(db, "C001", "客户");
        SeedOrder(db, "SO-USD-1", customer.Id, Currency.USD, 100m);
        SeedOrder(db, "SO-USD-2", customer.Id, Currency.USD, 200m);
        SeedOrder(db, "SO-EUR-1", customer.Id, Currency.EUR, 500m);

        var ctl = BuildController(db, user.Id);
        var page = OkPage(await ctl.Preview(new DynamicOrderProfitEstimateReportRequest
        {
            Fields = new List<string> { "orderNo" },
            Page = 1,
            PageSize = 1,
            Start = Start,
            End = End,
        }));

        Assert.Equal(3, page.Total);
        Assert.Single(page.Rows);   // 当前页仅 1 行
        Assert.NotNull(page.Summary);
        Assert.Equal(2, page.Summary!.CurrencyCount);
        Assert.Equal(2, page.Summary.Rows.Single(r => r.Currency == "USD").OrderCount);
        Assert.Equal(300m, page.Summary.Rows.Single(r => r.Currency == "USD").SalesAmount);
        Assert.Equal(1, page.Summary.Rows.Single(r => r.Currency == "EUR").OrderCount);
        Assert.Equal(500m, page.Summary.Rows.Single(r => r.Currency == "EUR").SalesAmount);
    }

    [Fact]
    public async Task 预览端点_未知币种_汇总金额null_绝不0()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "opd-summary-unknown");
        var customer = SeedCustomer(db, "C001", "客户");
        SeedOrder(db, "SO-UNK", customer.Id, (Currency)99, 999m);
        SeedOrder(db, "SO-USD", customer.Id, Currency.USD, 100m);

        var ctl = BuildController(db, user.Id);
        var page = OkPage(await ctl.Preview(new DynamicOrderProfitEstimateReportRequest
        {
            Fields = new List<string> { "orderNo" },
            Start = Start,
            End = End,
        }));

        Assert.NotNull(page.Summary);
        var unknown = page.Summary!.Rows.Single(r => r.Currency == "未知币种");
        Assert.Equal(1, unknown.OrderCount);
        Assert.Null(unknown.SalesAmount);

        var usd = page.Summary.Rows.Single(r => r.Currency == "USD");
        Assert.Equal(1, usd.OrderCount);
        Assert.Equal(100m, usd.SalesAmount);
    }

    [Fact]
    public async Task 预览端点_超出500订单_拒绝且不返回部分汇总()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "opd-summary-overflow");
        var customer = SeedCustomer(db, "C001", "客户");

        var many = new List<SalesOrder>();
        for (var i = 1; i <= 501; i++)
        {
            many.Add(new SalesOrder
            {
                OrderNo = $"SO-{i:0000}",
                OrderDate = new DateTime(2026, 9, 10),
                CustomerId = customer.Id,
                Status = DocumentStatus.Approved,
                Currency = Currency.USD,
                TotalAmount = 100m,
            });
        }
        db.SalesOrders.AddRange(many);
        db.SaveChanges();

        var ctl = BuildController(db, user.Id);
        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Preview(
            new DynamicOrderProfitEstimateReportRequest { Start = Start, End = End }));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
    }
}
