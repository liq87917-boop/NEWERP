using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// 客户出货量统计表（ERP-227）日期窗口 / 有界读取 / 稳定排序单元测试：
/// 日期校验（含溢出防护）先于任何源读取；结束日按排他上界（&lt; 结束日次日）比较；
/// 订单头按 CustomerId / Id 稳定排序；订单 / 明细有界读取，超出上限 fail closed；
/// 客户名称按 Id 集合一次固定批量查询，无逐单查库、只读不写库。
/// <para>全部使用内存数据库（TestDbFactory），不连接 SQL Server、不启动 API、不执行任何 SQL / seed、不运行浏览器验收。</para>
/// </summary>
public class CustomerShipmentQueryBoundsTests
{
    private static readonly DateTime Start = new(2026, 9, 1);
    private static readonly DateTime End = new(2026, 9, 30);

    /// <summary>特权数据范围（不过滤客户），仅用于本文件聚焦查询边界</summary>
    private static readonly SalespersonDataScope PrivilegedScope = new() { IsPrivileged = true, AllowedCustomerIds = null };

    private static BaseCustomer SeedCustomer(ErpDbContext db, string code, string name)
    {
        var c = new BaseCustomer { CustomerCode = code, CustomerName = name, Status = 1 };
        db.BaseCustomers.Add(c);
        db.SaveChanges();
        return c;
    }

    // ==================== 日期校验（先于任何源读取） ====================

    [Fact]
    public async Task 结束日期早于开始日期_拒绝()
    {
        using var db = TestDbFactory.Create();
        var service = new ReportService(db);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            service.GetCustomerShipmentStatsAsync(new DateTime(2026, 9, 30), new DateTime(2026, 9, 1), PrivilegedScope));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public async Task 日期范围超过366天_拒绝()
    {
        using var db = TestDbFactory.Create();
        var service = new ReportService(db);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            service.GetCustomerShipmentStatsAsync(new DateTime(2026, 1, 1), new DateTime(2027, 1, 2), PrivilegedScope));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public async Task 日期溢出_结束日为最大日期_拒绝而非运行时溢出()
    {
        using var db = TestDbFactory.Create();
        var service = new ReportService(db);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            service.GetCustomerShipmentStatsAsync(new DateTime(2026, 1, 1), DateTime.MaxValue, PrivilegedScope));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public async Task 日期范围恰为366天_正常通过()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "客户");
        db.SalesOrders.Add(new SalesOrder
        {
            OrderNo = "SO-366",
            OrderDate = new DateTime(2026, 1, 1),
            CustomerId = customer.Id,
            Status = DocumentStatus.Approved,
            TotalAmount = 100m
        });
        db.SaveChanges();

        var service = new ReportService(db);
        var result = await service.GetCustomerShipmentStatsAsync(
            new DateTime(2026, 1, 1), new DateTime(2027, 1, 1), PrivilegedScope);

        var row = Assert.Single(result);
        Assert.Equal("客户", row.CustomerName);
    }

    // ==================== 有界读取 ====================

    [Fact]
    public async Task 订单数超过500_报告上限错误()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "客户");
        for (var i = 1; i <= 501; i++)
        {
            db.SalesOrders.Add(new SalesOrder
            {
                OrderNo = $"SO-{i:0000}",
                OrderDate = new DateTime(2026, 9, 10),
                CustomerId = customer.Id,
                Status = DocumentStatus.Approved,
                TotalAmount = 100m
            });
        }
        db.SaveChanges();

        var service = new ReportService(db);
        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            service.GetCustomerShipmentStatsAsync(Start, End, PrivilegedScope));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("500", ex.Message);
    }

    [Fact]
    public async Task 明细数超过10000_报告上限错误()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "客户");
        var order = new SalesOrder
        {
            OrderNo = "SO-BIG",
            OrderDate = new DateTime(2026, 9, 10),
            CustomerId = customer.Id,
            Status = DocumentStatus.Approved,
            TotalAmount = 100m
        };
        db.SalesOrders.Add(order);
        db.SaveChanges();

        for (var i = 1; i <= 10001; i++)
        {
            db.SalesOrderDetails.Add(new SalesOrderDetail
            {
                SalesOrderId = order.Id,
                ProductId = 1,
                ProductName = "商品",
                Quantity = 1m,
                Unit = "PCS"
            });
        }
        db.SaveChanges();

        var service = new ReportService(db);
        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            service.GetCustomerShipmentStatsAsync(Start, End, PrivilegedScope));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("10000", ex.Message);
    }

    // ==================== 固定批量查询 / 只读 ====================

    [Fact]
    public async Task 固定批量查询_数据集读取次数恒定且只读()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "客户");

        var order1 = new SalesOrder { OrderNo = "SO-1", OrderDate = new DateTime(2026, 9, 10), CustomerId = customer.Id, Status = DocumentStatus.Approved, TotalAmount = 100m };
        var order2 = new SalesOrder { OrderNo = "SO-2", OrderDate = new DateTime(2026, 9, 11), CustomerId = customer.Id, Status = DocumentStatus.Approved, TotalAmount = 200m };
        db.SalesOrders.AddRange(order1, order2);
        db.SaveChanges();

        db.SalesOrderDetails.AddRange(
            new SalesOrderDetail { SalesOrderId = order1.Id, ProductId = 1, ProductName = "热销商品", Quantity = 1m, Unit = "PCS" },
            new SalesOrderDetail { SalesOrderId = order2.Id, ProductId = 2, ProductName = "平销商品", Quantity = 1m, Unit = "PCS" });
        db.SaveChanges();

        var counting = InventoryMovementReportTests.CountingDbContext.Wrap(db);
        var service = new ReportService(counting.Proxy);
        var result = await service.GetCustomerShipmentStatsAsync(Start, End, PrivilegedScope);

        Assert.Single(result);
        // 固定 3 次数据集访问：SalesOrders + SalesOrderDetails + BaseCustomers；无逐单查库
        Assert.Equal(3, counting.DatasetReads);
        Assert.Equal(0, counting.WriteCalls);
    }

    // ==================== ERP-232 全匹配汇总与分页同源（有界列表派生） ====================

    [Fact]
    public void BuildPage_越界页_全匹配汇总仍覆盖完整有界列表()
    {
        var items = new List<ReportDtos.CustomerShipmentItem>
        {
            new() { CustomerId = 1, Currency = "USD", TotalAmount = 100m, OrderCount = 1 },
            new() { CustomerId = 2, Currency = "CNY", TotalAmount = 50m, OrderCount = 1 },
        };

        var page = DynamicCustomerShipmentReportRules.BuildPage(
            items,
            new List<string> { "currency", "totalAmount" },
            page: 99,
            pageSize: 20,
            start: new DateTime(2026, 9, 1),
            end: new DateTime(2026, 9, 30));

        Assert.NotNull(page.Summary);
        Assert.Empty(page.Rows);
        Assert.Equal(2, page.Summary.CurrencyRows.Count);
        Assert.Equal(2, page.Summary.CurrencyRows.Sum(r => r.CustomerCount));
    }
}
