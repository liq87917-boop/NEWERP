using ERP.Application.Common;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// 订单利润暂估表（ERP-220）原币与「当前价估算」口径单元测试：
/// 销售额保留订单原币 TotalAmount（绝不换算汇率、不改动金额）；成本 / 利润 / 利润率为未知（null，绝不回落为 0）；
/// 「当前价估算」仅为数量 × 商品当前 CostPrice 的独立口径（币种未知，仅估算），
/// 明细缺失 / 明细缺少商品 / 商品缺失或已删除时为 null 并给出显式原因；已知 0 成本与未知成本严格区分。
/// <para>全部使用内存数据库（TestDbFactory），不连接 SQL Server、不启动 API、不执行任何 SQL / seed、不运行浏览器验收。</para>
/// </summary>
public class OrderProfitEstimateCurrencyTests
{
    private static readonly DateTime Start = new(2026, 9, 1);
    private static readonly DateTime End = new(2026, 9, 30);

    private static readonly SalespersonDataScope PrivilegedScope = new() { IsPrivileged = true, AllowedCustomerIds = null };

    private static BaseCustomer SeedCustomer(ErpDbContext db, string code, string name)
    {
        var c = new BaseCustomer { CustomerCode = code, CustomerName = name, Status = 1 };
        db.BaseCustomers.Add(c);
        db.SaveChanges();
        return c;
    }

    private static BaseProduct SeedProduct(ErpDbContext db, string code, decimal costPrice, bool deleted = false)
    {
        var p = new BaseProduct
        {
            ProductCode = code,
            ProductName = code,
            SalePrice = 100m,
            CostPrice = costPrice,
            Status = 1,
            IsDeleted = deleted
        };
        db.BaseProducts.Add(p);
        db.SaveChanges();
        return p;
    }

    private static SalesOrder SeedOrder(ErpDbContext db, string orderNo, long customerId, Currency currency, decimal totalAmount)
    {
        var o = new SalesOrder
        {
            OrderNo = orderNo,
            OrderDate = new DateTime(2026, 9, 10),
            CustomerId = customerId,
            Status = DocumentStatus.Approved,
            Currency = currency,
            TotalAmount = totalAmount
        };
        db.SalesOrders.Add(o);
        db.SaveChanges();
        return o;
    }

    private static SalesOrderDetail SeedDetail(ErpDbContext db, long orderId, long productId, decimal quantity)
    {
        var d = new SalesOrderDetail
        {
            SalesOrderId = orderId,
            ProductId = productId,
            ProductName = "商品",
            Quantity = quantity,
            Unit = "PCS"
        };
        db.SalesOrderDetails.Add(d);
        db.SaveChanges();
        return d;
    }

    // ==================== 原币身份与销售额 ====================

    [Fact]
    public async Task 混合币种_每行保留订单原币与身份_销售额不改动()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "测试客户");
        var usd = SeedOrder(db, "SO-USD", customer.Id, Currency.USD, 1234.56m);
        var cny = SeedOrder(db, "SO-CNY", customer.Id, Currency.CNY, 8765.43m);

        var service = new ReportService(db);
        var result = await service.GetOrderProfitEstimateAsync(Start, End, PrivilegedScope);

        Assert.Equal(2, result.Count);

        var usdRow = Assert.Single(result, r => r.OrderNo == "SO-USD");
        Assert.Equal(usd.Id, usdRow.OrderId);
        Assert.Equal(customer.Id, usdRow.CustomerId);
        Assert.Equal("USD", usdRow.Currency);
        Assert.Equal("USD 美元", usdRow.CurrencyLabel);
        Assert.Equal(1234.56m, usdRow.SalesAmount);
        Assert.Equal(ReportService.OrderProfitSalesAmountLabel, usdRow.SalesAmountLabel);

        var cnyRow = Assert.Single(result, r => r.OrderNo == "SO-CNY");
        Assert.Equal(cny.Id, cnyRow.OrderId);
        Assert.Equal("CNY", cnyRow.Currency);
        Assert.Equal("CNY 人民币", cnyRow.CurrencyLabel);
        Assert.Equal(8765.43m, cnyRow.SalesAmount);

        Assert.All(result, r =>
        {
            Assert.Null(r.CostAmount);
            Assert.Null(r.Profit);
            Assert.Null(r.ProfitRate);
            Assert.Equal(ReportService.OrderProfitCostEvidence, r.CostEvidence);
            Assert.Equal(ReportService.OrderProfitProfitEvidence, r.ProfitEvidence);
        });
    }

    [Fact]
    public async Task 未知枚举币种_归入未知币种_绝不默认币种()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "测试客户");
        SeedOrder(db, "SO-UNKNOWN", customer.Id, (Currency)0, 500m);

        var service = new ReportService(db);
        var result = await service.GetOrderProfitEstimateAsync(Start, End, PrivilegedScope);

        var row = Assert.Single(result);
        Assert.Equal("未知币种", row.Currency);
        Assert.Equal("未知币种", row.CurrencyLabel);
        Assert.Equal(500m, row.SalesAmount);
    }

    // ==================== 当前价估算：缺失 / 已删除 / 不完整 ====================

    [Fact]
    public async Task 明细为空_当前价估算未知_并给出显式原因()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "测试客户");
        SeedOrder(db, "SO-EMPTY", customer.Id, Currency.USD, 1000m);

        var service = new ReportService(db);
        var result = await service.GetOrderProfitEstimateAsync(Start, End, PrivilegedScope);

        var row = Assert.Single(result);
        Assert.Null(row.CurrentPriceEstimate);
        Assert.Equal(ReportService.OrderProfitCurrentPriceEstimateLabel, row.CurrentPriceEstimateLabel);
        Assert.Equal(ReportService.OrderProfitEstimateMissingDetailReason, row.CurrentPriceEstimateReason);
    }

    [Fact]
    public async Task 明细缺少商品_当前价估算未知_并给出显式原因()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "测试客户");
        var order = SeedOrder(db, "SO-INCOMPLETE", customer.Id, Currency.USD, 1000m);
        SeedDetail(db, order.Id, productId: 0, quantity: 10m);

        var service = new ReportService(db);
        var result = await service.GetOrderProfitEstimateAsync(Start, End, PrivilegedScope);

        var row = Assert.Single(result);
        Assert.Null(row.CurrentPriceEstimate);
        Assert.Equal(ReportService.OrderProfitEstimateIncompleteDetailReason, row.CurrentPriceEstimateReason);
    }

    [Fact]
    public async Task 商品已软删除_当前价估算未知_并给出显式原因()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "测试客户");
        var product = SeedProduct(db, "P-DEL", costPrice: 60m, deleted: true);
        var order = SeedOrder(db, "SO-DEL", customer.Id, Currency.USD, 1000m);
        SeedDetail(db, order.Id, product.Id, quantity: 10m);

        var service = new ReportService(db);
        var result = await service.GetOrderProfitEstimateAsync(Start, End, PrivilegedScope);

        var row = Assert.Single(result);
        Assert.Null(row.CurrentPriceEstimate);
        Assert.Equal(ReportService.OrderProfitEstimateMissingProductReason, row.CurrentPriceEstimateReason);
    }

    [Fact]
    public async Task 商品不存在_当前价估算未知_并给出显式原因()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "测试客户");
        var order = SeedOrder(db, "SO-MISSING", customer.Id, Currency.USD, 1000m);
        SeedDetail(db, order.Id, productId: 999, quantity: 10m);

        var service = new ReportService(db);
        var result = await service.GetOrderProfitEstimateAsync(Start, End, PrivilegedScope);

        var row = Assert.Single(result);
        Assert.Null(row.CurrentPriceEstimate);
        Assert.Equal(ReportService.OrderProfitEstimateMissingProductReason, row.CurrentPriceEstimateReason);
    }

    // ==================== 已知 0 成本与未知成本严格区分 ====================

    [Fact]
    public async Task 已知零成本与未知成本_严格区分_不混为未知或0()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "测试客户");
        var zeroCost = SeedProduct(db, "P-ZERO", costPrice: 0m);
        var knownZeroOrder = SeedOrder(db, "SO-ZERO-COST", customer.Id, Currency.USD, 1000m);
        SeedDetail(db, knownZeroOrder.Id, zeroCost.Id, quantity: 10m);

        var unknownOrder = SeedOrder(db, "SO-UNKNOWN-COST", customer.Id, Currency.USD, 1000m);
        SeedDetail(db, unknownOrder.Id, productId: 999, quantity: 10m);

        var service = new ReportService(db);
        var result = await service.GetOrderProfitEstimateAsync(Start, End, PrivilegedScope);

        var knownZero = Assert.Single(result, r => r.OrderNo == "SO-ZERO-COST");
        Assert.Equal(0m, knownZero.CurrentPriceEstimate);        // 已知 0 成本：估算 = 0（不是未知）
        Assert.Equal(string.Empty, knownZero.CurrentPriceEstimateReason);

        var unknown = Assert.Single(result, r => r.OrderNo == "SO-UNKNOWN-COST");
        Assert.Null(unknown.CurrentPriceEstimate);                // 未知成本：null（绝不回落为 0）
        Assert.Equal(ReportService.OrderProfitEstimateMissingProductReason, unknown.CurrentPriceEstimateReason);
    }

    [Fact]
    public async Task 汇率存在_销售额仍为订单原币_绝不换算()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "测试客户");
        var product = SeedProduct(db, "P001", costPrice: 60m);
        var order = new SalesOrder
        {
            OrderNo = "SO-FX",
            OrderDate = new DateTime(2026, 9, 10),
            CustomerId = customer.Id,
            Status = DocumentStatus.Approved,
            Currency = Currency.USD,
            ExchangeRate = 7.2m,          // 汇率仅记录，不参与换算
            TotalAmount = 1000m
        };
        db.SalesOrders.Add(order);
        db.SaveChanges();
        SeedDetail(db, order.Id, product.Id, quantity: 1m);

        var service = new ReportService(db);
        var result = await service.GetOrderProfitEstimateAsync(Start, End, PrivilegedScope);

        var row = Assert.Single(result);
        Assert.Equal(1000m, row.SalesAmount);              // 仍为订单原币，绝不乘以汇率
        Assert.Equal(60m, row.CurrentPriceEstimate);        // 当前价估算也不做汇率换算
    }
}
