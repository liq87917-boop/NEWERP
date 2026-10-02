using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-231 动态客户出货量证据报表「客户 Id / 原币币种」筛选的聚焦单元测试：
/// 筛选规范化（fail closed：客户 Id 正整数、币种仅已知枚举码、数字 / 未知取值拒绝）、规范化筛选上下文文案、
/// 空筛选兼容（全部留空 = null = 不过滤）、服务层筛选与业务员数据范围相交后先于 500 订单头上限探测、
/// 省略筛选保留全部已知 / 未知币种证据，以及范围外客户 Id 不返回任何名称 / 金额。
/// <para>纯规则测试无数据库依赖；服务层测试全部使用内存数据库（TestDbFactory），不连接 SQL Server、不启动 API、不执行任何 SQL / seed、不运行浏览器验收。</para>
/// </summary>
public class CustomerShipmentReportFilterTests
{
    private static readonly DateTime Start = new(2026, 9, 1);
    private static readonly DateTime End = new(2026, 9, 30);

    private static readonly SalespersonDataScope PrivilegedScope = new() { IsPrivileged = true, AllowedCustomerIds = null };

    // ==================== 1. 纯规则：规范化 ====================

    [Fact]
    public void NormalizeFilter_留空或null_返回null表示不过滤()
    {
        Assert.Null(CustomerShipmentReportFilterRules.NormalizeFilter(null));
        Assert.Null(CustomerShipmentReportFilterRules.NormalizeFilter(new CustomerShipmentFilterDto()));
        Assert.Null(CustomerShipmentReportFilterRules.NormalizeFilter(
            new CustomerShipmentFilterDto { CustomerId = null, Currency = "  " }));
    }

    [Fact]
    public void NormalizeFilter_客户Id保留_币种归一化为枚举名()
    {
        var filter = CustomerShipmentReportFilterRules.NormalizeFilter(
            new CustomerShipmentFilterDto { CustomerId = 3L, Currency = " usd " });

        Assert.Equal(3L, filter!.CustomerId);
        Assert.Equal("USD", filter.Currency);
    }

    [Fact]
    public void NormalizeFilter_客户Id非正整数_拒绝()
    {
        Assert.Throws<BusinessException>(() =>
            CustomerShipmentReportFilterRules.NormalizeFilter(new CustomerShipmentFilterDto { CustomerId = 0 }));
        Assert.Throws<BusinessException>(() =>
            CustomerShipmentReportFilterRules.NormalizeFilter(new CustomerShipmentFilterDto { CustomerId = -1 }));
    }

    [Theory]
    [InlineData("1")]
    [InlineData("0")]
    [InlineData("99")]
    [InlineData("BTC")]
    [InlineData("人民币")]
    [InlineData("USDT")]
    public void NormalizeFilter_数字或未知币种_拒绝(string currency)
    {
        var ex = Assert.Throws<BusinessException>(() =>
            CustomerShipmentReportFilterRules.NormalizeFilter(new CustomerShipmentFilterDto { Currency = currency }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Theory]
    [InlineData(" cny ", "CNY")]
    [InlineData("usd", "USD")]
    [InlineData("EUR", "EUR")]
    [InlineData("HKD", "HKD")]
    [InlineData("gbp", "GBP")]
    [InlineData("jpy", "JPY")]
    public void NormalizeCurrencyFilter_已知枚举码_去空白大写归一化(string raw, string expected)
    {
        Assert.Equal(expected, CustomerShipmentReportFilterRules.NormalizeCurrencyFilter(raw));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NormalizeCurrencyFilter_留空_返回null(string? raw)
    {
        Assert.Null(CustomerShipmentReportFilterRules.NormalizeCurrencyFilter(raw));
    }

    [Fact]
    public void 目录口径_说明支持的筛选_不泄露任何数据()
    {
        Assert.Contains("CNY", CustomerShipmentReportFilterRules.SupportedFilterText);
        Assert.Contains("JPY", CustomerShipmentReportFilterRules.SupportedFilterText);
        Assert.DoesNotContain("未知币种", CustomerShipmentReportFilterRules.SupportedFilterText);
    }

    [Fact]
    public void BuildFilterContext_无筛选_返回空串()
    {
        Assert.Equal(string.Empty, CustomerShipmentReportFilterRules.BuildFilterContext(null));
        Assert.Equal(string.Empty, CustomerShipmentReportFilterRules.BuildFilterContext(new CustomerShipmentFilterDto()));
    }

    // ==================== 2. 服务层：筛选 / 范围 / 上限 ====================

    private static BaseCustomer SeedCustomer(ErpDbContext db, string code, string name)
    {
        var customer = new BaseCustomer
        {
            CustomerCode = code,
            CustomerName = name,
            Status = 1,
            CreditStatus = "正常",
            IsDeleted = false
        };
        db.BaseCustomers.Add(customer);
        db.SaveChanges();
        return customer;
    }

    private static SalesOrder SeedOrder(
        ErpDbContext db, string orderNo, long customerId, Currency currency, decimal totalAmount = 100m)
    {
        var order = new SalesOrder
        {
            OrderNo = orderNo,
            OrderDate = new DateTime(2026, 9, 10),
            CustomerId = customerId,
            Status = DocumentStatus.Approved,
            Currency = currency,
            TotalAmount = totalAmount
        };
        db.SalesOrders.Add(order);
        db.SaveChanges();
        return order;
    }

    [Fact]
    public async Task 服务_省略筛选_保留全部已知与未知币种证据()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "客户");

        SeedOrder(db, "SO-USD", customer.Id, Currency.USD, 100m);
        SeedOrder(db, "SO-BAD", customer.Id, (Currency)999, 777m);

        var service = new ReportService(db);
        var result = await service.GetCustomerShipmentStatsAsync(Start, End, PrivilegedScope, null);

        Assert.Equal(2, result.Count);
        Assert.Contains(result, x => x.Currency == "USD");
        Assert.Contains(result, x => x.Currency == CustomerShipmentEvidenceRules.UnknownCurrencyGroup);
    }

    [Fact]
    public async Task 服务_客户Id筛选_仅返回该客户()
    {
        using var db = TestDbFactory.Create();
        var a = SeedCustomer(db, "C-A", "客户A");
        var b = SeedCustomer(db, "C-B", "客户B");

        SeedOrder(db, "SO-A", a.Id, Currency.USD);
        SeedOrder(db, "SO-B", b.Id, Currency.USD);

        var service = new ReportService(db);
        var result = await service.GetCustomerShipmentStatsAsync(
            Start, End, PrivilegedScope, new CustomerShipmentFilterDto { CustomerId = a.Id });

        var row = Assert.Single(result);
        Assert.Equal("客户A", row.CustomerName);
    }

    [Fact]
    public async Task 服务_原币币种筛选_仅返回该币种_不含未知币种()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "客户");

        SeedOrder(db, "SO-USD", customer.Id, Currency.USD, 100m);
        SeedOrder(db, "SO-CNY", customer.Id, Currency.CNY, 500m);
        SeedOrder(db, "SO-BAD", customer.Id, (Currency)999, 777m);

        var service = new ReportService(db);
        var result = await service.GetCustomerShipmentStatsAsync(
            Start, End, PrivilegedScope, new CustomerShipmentFilterDto { Currency = "usd" });

        var row = Assert.Single(result);
        Assert.Equal("USD", row.Currency);
        Assert.Equal(100m, row.TotalAmount);
    }


    [Fact]
    public async Task 服务_筛选先于500订单上限_过滤后不再溢出()
    {
        using var db = TestDbFactory.Create();
        var a = SeedCustomer(db, "C-A", "客户A");
        var b = SeedCustomer(db, "C-B", "客户B");

        var many = new List<SalesOrder>();
        for (var i = 1; i <= 501; i++)
        {
            many.Add(new SalesOrder
            {
                OrderNo = $"SO-A-{i:0000}",
                OrderDate = new DateTime(2026, 9, 10),
                CustomerId = a.Id,
                Status = DocumentStatus.Approved,
                Currency = Currency.USD,
                TotalAmount = 100m
            });
        }
        many.Add(new SalesOrder
        {
            OrderNo = "SO-B-1",
            OrderDate = new DateTime(2026, 9, 10),
            CustomerId = b.Id,
            Status = DocumentStatus.Approved,
            Currency = Currency.USD,
            TotalAmount = 100m
        });
        db.SalesOrders.AddRange(many);
        db.SaveChanges();

        var service = new ReportService(db);
        var result = await service.GetCustomerShipmentStatsAsync(
            Start, End, PrivilegedScope, new CustomerShipmentFilterDto { CustomerId = b.Id });

        var row = Assert.Single(result);
        Assert.Equal("客户B", row.CustomerName);
    }

    [Fact]
    public async Task 服务_受限制范围_筛选范围外客户Id_不返回任何名称或金额()
    {
        using var db = TestDbFactory.Create();
        var mine = SeedCustomer(db, "C001", "我的客户");
        var other = SeedCustomer(db, "C002", "别人的客户");

        SeedOrder(db, "SO-MINE", mine.Id, Currency.USD, 100m);
        SeedOrder(db, "SO-OTHER", other.Id, Currency.USD, 999m);

        var scope = new SalespersonDataScope
        {
            IsPrivileged = false,
            SalesmanId = 1,
            AllowedCustomerIds = new HashSet<long> { mine.Id }
        };
        var service = new ReportService(db);

        var empty = await service.GetCustomerShipmentStatsAsync(
            Start, End, scope, new CustomerShipmentFilterDto { CustomerId = other.Id });
        Assert.Empty(empty);

        var single = await service.GetCustomerShipmentStatsAsync(
            Start, End, scope, new CustomerShipmentFilterDto { CustomerId = mine.Id });
        var row = Assert.Single(single);
        Assert.Equal("我的客户", row.CustomerName);
    }

    [Fact]
    public void BuildFilterContext_客户与币种_准确标注规范化筛选()
    {
        var filter = CustomerShipmentReportFilterRules.NormalizeFilter(
            new CustomerShipmentFilterDto { CustomerId = 7L, Currency = "eur" });

        var text = CustomerShipmentReportFilterRules.BuildFilterContext(filter);
        Assert.Contains("客户 Id 7", text);
        Assert.Contains("原币币种 EUR", text);
    }
}
