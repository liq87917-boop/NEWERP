using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// 客户出货量统计表（ERP-228）币种 / 单位分组单元测试：
/// 金额仅在已知币种下按签名原币小计、未知 / 无效币种金额为 null 仅保留订单头计数；
/// 精确单位分组独立呈现签名数量、空白 / 未知单位仅计数证据（数量 null），不归一化 / 换算 / 合并不兼容单位；
/// 旧口径数量合计可空，仅当明细证据完整且单一非空单位时可知。
/// <para>全部使用内存数据库（TestDbFactory）或纯规则函数，不连接 SQL Server、不启动 API、不执行任何 SQL / seed、不运行浏览器验收。</para>
/// </summary>
public class CustomerShipmentCurrencyUnitTests
{
    private static readonly DateTime Start = new(2026, 9, 1);
    private static readonly DateTime End = new(2026, 9, 30);

    private static readonly SalespersonDataScope PrivilegedScope = new() { IsPrivileged = true, AllowedCustomerIds = null };

    // ==================== 纯规则：币种 ====================

    [Fact]
    public void 币种分组键_已知枚举返回编码_未定义枚举归入未知币种()
    {
        Assert.Equal("USD", CustomerShipmentEvidenceRules.CurrencyGroupKey(Currency.USD));
        Assert.Equal("CNY", CustomerShipmentEvidenceRules.CurrencyGroupKey(Currency.CNY));
        Assert.Equal(CustomerShipmentEvidenceRules.UnknownCurrencyGroup,
            CustomerShipmentEvidenceRules.CurrencyGroupKey((Currency)999));
        Assert.Equal("USD 美元", CustomerShipmentEvidenceRules.CurrencyGroupLabel("USD"));
        Assert.Equal("未知币种", CustomerShipmentEvidenceRules.CurrencyGroupLabel("未知币种"));
        Assert.True(CustomerShipmentEvidenceRules.IsKnownCurrency(Currency.USD));
        Assert.False(CustomerShipmentEvidenceRules.IsKnownCurrency((Currency)999));
    }

    // ==================== 纯规则：精确单位分组 ====================

    private static SalesOrderDetail Detail(long orderId, string unit, decimal quantity)
        => new() { SalesOrderId = orderId, Unit = unit, Quantity = quantity };

    [Fact]
    public void 精确单位分组_已知单位签名合计_空白未知单位数量为null且计数()
    {
        var groups = CustomerShipmentEvidenceRules.BuildUnitGroups(new[]
        {
            Detail(1, "PCS", 10m),
            Detail(1, "PCS", -2m),
            Detail(1, "", 5m),
            Detail(1, "   ", 3m),
        });

        var pcs = Assert.Single(groups.Where(g => g.Unit == "PCS"));
        Assert.Equal(8m, pcs.Quantity);
        Assert.Equal(2, pcs.DetailCount);
        Assert.Equal(CustomerShipmentEvidenceRules.KnownUnitQuantityLabel, pcs.QuantityLabel);

        var unknown = Assert.Single(groups.Where(g => g.Unit == CustomerShipmentEvidenceRules.UnknownUnitGroup));
        Assert.Null(unknown.Quantity);
        Assert.Equal(2, unknown.DetailCount);
        Assert.Equal(CustomerShipmentEvidenceRules.UnknownUnitQuantityLabel, unknown.QuantityLabel);
    }

    [Fact]
    public void 精确单位分组_不归一化_大小写与前后空白保持不同单位()
    {
        var groups = CustomerShipmentEvidenceRules.BuildUnitGroups(new[]
        {
            Detail(1, "PCS", 1m),
            Detail(1, "pcs", 1m),
            Detail(1, "PCS ", 1m),
        });

        Assert.Equal(3, groups.Count);
        Assert.Contains(groups, g => g.Unit == "PCS");
        Assert.Contains(groups, g => g.Unit == "pcs");
        Assert.Contains(groups, g => g.Unit == "PCS ");
    }

    // ==================== 纯规则：旧口径数量合计 ====================

    [Fact]
    public void 旧口径数量_单一非空单位且证据完整_已知()
    {
        var orders = new List<SalesOrder> { new() { Id = 1 } };
        var details = new Dictionary<long, List<SalesOrderDetail>>
        {
            [1] = new() { Detail(1, "PCS", 10m), Detail(1, "PCS", 5m) }
        };

        var (quantity, reason) = CustomerShipmentEvidenceRules.BuildLegacyTotalQuantity(orders, details);

        Assert.Equal(15m, quantity);
        Assert.Equal(string.Empty, reason);
    }

    [Fact]
    public void 旧口径数量_混合单位_未知()
    {
        var orders = new List<SalesOrder> { new() { Id = 1 } };
        var details = new Dictionary<long, List<SalesOrderDetail>>
        {
            [1] = new() { Detail(1, "PCS", 10m), Detail(1, "BOX", 5m) }
        };

        var (quantity, reason) = CustomerShipmentEvidenceRules.BuildLegacyTotalQuantity(orders, details);

        Assert.Null(quantity);
        Assert.Equal(CustomerShipmentEvidenceRules.MixedUnitReason, reason);
    }

    [Fact]
    public void 旧口径数量_存在空白单位_未知()
    {
        var orders = new List<SalesOrder> { new() { Id = 1 } };
        var details = new Dictionary<long, List<SalesOrderDetail>>
        {
            [1] = new() { Detail(1, "PCS", 10m), Detail(1, "", 5m) }
        };

        var (quantity, reason) = CustomerShipmentEvidenceRules.BuildLegacyTotalQuantity(orders, details);

        Assert.Null(quantity);
        Assert.Equal(CustomerShipmentEvidenceRules.UnknownUnitReason, reason);
    }

    [Fact]
    public void 旧口径数量_存在无明细订单_证据不完整_未知()
    {
        var orders = new List<SalesOrder> { new() { Id = 1 }, new() { Id = 2 } };
        var details = new Dictionary<long, List<SalesOrderDetail>>
        {
            [1] = new() { Detail(1, "PCS", 10m) }
        };

        var (quantity, reason) = CustomerShipmentEvidenceRules.BuildLegacyTotalQuantity(orders, details);

        Assert.Null(quantity);
        Assert.Equal(CustomerShipmentEvidenceRules.IncompleteDetailReason, reason);
    }

    [Fact]
    public void 旧口径数量_空订单集合_缺失明细_未知()
    {
        var (quantity, reason) = CustomerShipmentEvidenceRules.BuildLegacyTotalQuantity(
            new List<SalesOrder>(), new Dictionary<long, List<SalesOrderDetail>>());

        Assert.Null(quantity);
        Assert.Equal(CustomerShipmentEvidenceRules.MissingDetailReason, reason);
    }


    // ==================== 服务层：客户 × 原币分组 ====================

    private static BaseCustomer SeedCustomer(ErpDbContext db, string code, string name)
    {
        var c = new BaseCustomer { CustomerCode = code, CustomerName = name, Status = 1 };
        db.BaseCustomers.Add(c);
        db.SaveChanges();
        return c;
    }

    private static SalesOrder SeedOrder(
        ErpDbContext db, string orderNo, long customerId, Currency currency, decimal totalAmount)
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

    private static SalesOrderDetail SeedDetail(
        ErpDbContext db, long orderId, string unit, decimal quantity, bool deleted = false)
    {
        var detail = new SalesOrderDetail
        {
            SalesOrderId = orderId,
            ProductId = 1,
            ProductName = "商品",
            Quantity = quantity,
            Unit = unit,
            IsDeleted = deleted
        };
        db.SalesOrderDetails.Add(detail);
        db.SaveChanges();
        return detail;
    }

    [Fact]
    public async Task 服务层_按客户原币分组_已知币种签名小计_未知币种金额为null()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "客户");

        SeedOrder(db, "SO-USD1", customer.Id, Currency.USD, 1000m);
        SeedOrder(db, "SO-USD2", customer.Id, Currency.USD, -200m);
        SeedOrder(db, "SO-CNY", customer.Id, Currency.CNY, 500m);
        SeedOrder(db, "SO-BAD", customer.Id, (Currency)999, 777m);

        var service = new ReportService(db);
        var result = await service.GetCustomerShipmentStatsAsync(Start, End, PrivilegedScope);

        Assert.Equal(3, result.Count);

        var usd = Assert.Single(result.Where(x => x.Currency == "USD"));
        Assert.Equal(2, usd.OrderCount);
        Assert.Equal(800m, usd.TotalAmount);   // 1000 + (-200)，签名原币小计
        Assert.Equal("USD 美元", usd.CurrencyLabel);
        Assert.Equal(CustomerShipmentEvidenceRules.KnownCurrencyEvidence, usd.CurrencyEvidence);

        var cny = Assert.Single(result.Where(x => x.Currency == "CNY"));
        Assert.Equal(1, cny.OrderCount);
        Assert.Equal(500m, cny.TotalAmount);

        var unknown = Assert.Single(result.Where(x => x.Currency == CustomerShipmentEvidenceRules.UnknownCurrencyGroup));
        Assert.Equal(1, unknown.OrderCount);
        Assert.Null(unknown.TotalAmount);       // 未知币种：金额未知，绝不推断币种
        Assert.Equal(CustomerShipmentEvidenceRules.UnknownCurrencyEvidence, unknown.CurrencyEvidence);
    }

    [Fact]
    public async Task 服务层_同客户多币种_分别成行_不跨币种合计()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "客户");

        SeedOrder(db, "SO-USD", customer.Id, Currency.USD, 1000m);
        SeedOrder(db, "SO-CNY", customer.Id, Currency.CNY, 500m);

        var service = new ReportService(db);
        var result = await service.GetCustomerShipmentStatsAsync(Start, End, PrivilegedScope);

        Assert.Equal(2, result.Count);
        Assert.DoesNotContain(result, x => x.TotalAmount == 1500m); // 绝不出现跨币种合计
    }

    [Fact]
    public async Task 服务层_精确单位分组_空白单位数量为null且计数_旧口径数量未知()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "客户");
        var order = SeedOrder(db, "SO-1", customer.Id, Currency.USD, 100m);

        SeedDetail(db, order.Id, "PCS", 10m);
        SeedDetail(db, order.Id, "PCS", 5m);
        SeedDetail(db, order.Id, "", 2m);

        var service = new ReportService(db);
        var result = await service.GetCustomerShipmentStatsAsync(Start, End, PrivilegedScope);

        var row = Assert.Single(result);
        var pcs = Assert.Single(row.UnitGroups.Where(g => g.Unit == "PCS"));
        Assert.Equal(15m, pcs.Quantity);

        var unknown = Assert.Single(row.UnitGroups.Where(g => g.Unit == CustomerShipmentEvidenceRules.UnknownUnitGroup));
        Assert.Null(unknown.Quantity);
        Assert.Equal(1, unknown.DetailCount);

        Assert.Null(row.TotalQuantity);   // 存在空白单位 → 旧口径数量未知，绝不回落为 0
        Assert.Equal(CustomerShipmentEvidenceRules.UnknownUnitReason, row.QuantityCompletenessReason);
    }

    [Fact]
    public async Task 服务层_旧口径数量_单一非空单位且完整_已知()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "客户");
        var order = SeedOrder(db, "SO-1", customer.Id, Currency.USD, 100m);

        SeedDetail(db, order.Id, "PCS", 10m);
        SeedDetail(db, order.Id, "PCS", -2m);

        var service = new ReportService(db);
        var result = await service.GetCustomerShipmentStatsAsync(Start, End, PrivilegedScope);

        var row = Assert.Single(result);
        Assert.Equal(8m, row.TotalQuantity);   // 10 + (-2)，签名合计
        Assert.Equal(string.Empty, row.QuantityCompletenessReason);
    }

    // ==================== ERP-232 全匹配汇总（与既有精确单位分组同源） ====================

    [Fact]
    public void 全匹配汇总_同源精确单位分组_未知单位null且金额只属原币面板()
    {
        var item = new ReportDtos.CustomerShipmentItem
        {
            CustomerId = 1,
            Currency = "USD",
            TotalAmount = 100m,
            OrderCount = 1,
            UnitGroups = CustomerShipmentEvidenceRules.BuildUnitGroups(new[]
            {
                Detail(1, "PCS", 10m),
                Detail(1, "PCS", -2m),
                Detail(1, "", 5m),
            }).ToList(),
        };

        var summary = DynamicCustomerShipmentSummaryRules.BuildSummary(new[] { item });

        var pcs = Assert.Single(summary.UnitRows.Where(r => r.Unit == "PCS"));
        Assert.Equal(8m, pcs.Quantity);
        Assert.Equal(2, pcs.DetailCount);

        var unknown = Assert.Single(summary.UnitRows.Where(r => r.Unit == CustomerShipmentEvidenceRules.UnknownUnitGroup));
        Assert.Null(unknown.Quantity);
        Assert.Equal(1, unknown.DetailCount);

        // 金额只属于原币面板，绝不复制到单位行
        Assert.Equal(100m, Assert.Single(summary.CurrencyRows).TotalAmount);
        Assert.DoesNotContain(DynamicCustomerShipmentSummaryRules.UnitSummaryColumns, c => c.Key == "totalAmount");
    }
}

