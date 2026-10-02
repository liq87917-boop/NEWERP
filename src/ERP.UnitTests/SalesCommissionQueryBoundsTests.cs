using ERP.Application.Common;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// 业务员提成表（ERP-243）日期窗口 / 有界读取 / 固定批量查询单元测试：
/// 日期校验（含结束日溢出防护）先于任何源读取；结束日按排他上界（&lt; 结束日次日）比较；
/// 订单头在业务员数据范围之后按 SalesmanId / Id 稳定排序做 501 行探测（500 张上限），
/// 超出即 fail closed 且不返回任何行或金额；不再读取明细 / 商品、不设明细上限；
/// 业务员姓名按 Id 集合一次固定批量查询，系统参数 SalesCommissionRate 至多读取 2 条，只读不写库。
/// <para>全部使用内存数据库（TestDbFactory），不连接 SQL Server、不启动 API、不执行任何 SQL / seed、不运行浏览器验收。</para>
/// </summary>
public class SalesCommissionQueryBoundsTests
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

    private static BaseEmployee SeedEmployee(ErpDbContext db, string code, string name)
    {
        var e = new BaseEmployee { EmployeeCode = code, EmployeeName = name, IsSalesman = true, Status = 1 };
        db.BaseEmployees.Add(e);
        db.SaveChanges();
        return e;
    }

    private static SalesOrder SeedOrder(
        ErpDbContext db, string orderNo, long customerId, long? salesmanId, DateTime? orderDate = null,
        decimal totalAmount = 100m, DocumentStatus status = DocumentStatus.Approved)
    {
        var order = new SalesOrder
        {
            OrderNo = orderNo,
            OrderDate = orderDate ?? new DateTime(2026, 9, 10),
            CustomerId = customerId,
            SalesmanId = salesmanId,
            Status = status,
            TotalAmount = totalAmount
        };
        db.SalesOrders.Add(order);
        db.SaveChanges();
        return order;
    }

    [Fact]
    public async Task 结束日期早于开始日期_拒绝()
    {
        using var db = TestDbFactory.Create();
        var service = new ReportService(db);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            service.GetSalesCommissionAsync(new DateTime(2026, 9, 30), new DateTime(2026, 9, 1), PrivilegedScope));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public async Task 日期范围超过366天_拒绝()
    {
        using var db = TestDbFactory.Create();
        var service = new ReportService(db);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            service.GetSalesCommissionAsync(new DateTime(2026, 1, 1), new DateTime(2027, 1, 2), PrivilegedScope));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public async Task 结束日为最大日期_拒绝而非运行时溢出()
    {
        using var db = TestDbFactory.Create();
        var service = new ReportService(db);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            service.GetSalesCommissionAsync(DateTime.MaxValue.Date, DateTime.MaxValue.Date, PrivilegedScope));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public async Task 日期范围恰为366天_正常通过_且包含结束日()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "客户");
        var emp = SeedEmployee(db, "S001", "业务员甲");
        SeedOrder(db, "SO-366", customer.Id, emp.Id, new DateTime(2027, 1, 1), 100m);

        var service = new ReportService(db);
        var result = await service.GetSalesCommissionAsync(
            new DateTime(2026, 1, 1), new DateTime(2027, 1, 1), PrivilegedScope);

        var row = Assert.Single(result);
        Assert.Equal("业务员甲", row.SalesmanName);
    }

    [Fact]
    public async Task 结束日次日为排他上界_次日不计入()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "客户");
        var emp = SeedEmployee(db, "S001", "业务员甲");
        SeedOrder(db, "SO-IN", customer.Id, emp.Id, new DateTime(2026, 9, 30), 100m);
        SeedOrder(db, "SO-OUT", customer.Id, emp.Id, new DateTime(2026, 10, 1), 999m);

        var service = new ReportService(db);
        var result = await service.GetSalesCommissionAsync(Start, End, PrivilegedScope);

        var row = Assert.Single(result);
        Assert.Equal(100m, row.SalesAmount);
    }

    [Fact]
    public async Task 订单数超过500_报告上限错误()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "客户");
        var emp = SeedEmployee(db, "S001", "业务员甲");
        for (var i = 1; i <= 501; i++)
        {
            SeedOrder(db, $"SO-{i:0000}", customer.Id, emp.Id, new DateTime(2026, 9, 10), 100m);
        }

        var service = new ReportService(db);
        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            service.GetSalesCommissionAsync(Start, End, PrivilegedScope));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("500", ex.Message);
    }

    [Fact]
    public async Task 明细数量不参与上限_仅订单头500上限()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "客户");
        var emp = SeedEmployee(db, "S001", "业务员甲");
        var order = SeedOrder(db, "SO-BIG", customer.Id, emp.Id, new DateTime(2026, 9, 10), 100m);

        // 报表只读订单头：大量明细不触发任何明细上限（不再读取明细 / 商品）
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
        var result = await service.GetSalesCommissionAsync(Start, End, PrivilegedScope);

        var row = Assert.Single(result);
        Assert.Equal(100m, row.SalesAmount);
        Assert.Equal(1, row.OrderCount);
    }

    [Fact]
    public async Task 上限探测只针对范围内订单()
    {
        using var db = TestDbFactory.Create();
        var mine = SeedCustomer(db, "C001", "我的客户");
        var other = SeedCustomer(db, "C002", "别人的客户");
        var emp = SeedEmployee(db, "S001", "业务员甲");

        SeedOrder(db, "SO-MINE", mine.Id, emp.Id, new DateTime(2026, 9, 10), 100m);
        for (var i = 1; i <= 501; i++)
        {
            SeedOrder(db, $"SO-OUT-{i:0000}", other.Id, emp.Id, new DateTime(2026, 9, 10), 100m);
        }

        var scope = new SalespersonDataScope
        {
            IsPrivileged = false,
            SalesmanId = emp.Id,
            AllowedCustomerIds = new HashSet<long> { mine.Id }
        };
        var service = new ReportService(db);
        var result = await service.GetSalesCommissionAsync(Start, End, scope);

        var row = Assert.Single(result);
        Assert.Equal("业务员甲", row.SalesmanName);
        Assert.Equal(1, row.OrderCount);
    }

    [Fact]
    public async Task 固定批量查询_数据集读取次数恒定且只读()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "客户");
        var emp = SeedEmployee(db, "S001", "业务员甲");

        var order1 = new SalesOrder { OrderNo = "SO-1", OrderDate = new DateTime(2026, 9, 10), CustomerId = customer.Id, SalesmanId = emp.Id, Status = DocumentStatus.Approved, TotalAmount = 100m };
        var order2 = new SalesOrder { OrderNo = "SO-2", OrderDate = new DateTime(2026, 9, 11), CustomerId = customer.Id, SalesmanId = emp.Id, Status = DocumentStatus.Approved, TotalAmount = 200m };
        db.SalesOrders.AddRange(order1, order2);
        db.SaveChanges();

        var counting = InventoryMovementReportTests.CountingDbContext.Wrap(db);
        var service = new ReportService(counting.Proxy);
        var result = await service.GetSalesCommissionAsync(Start, End, PrivilegedScope);

        Assert.Single(result);
        // 固定 3 次数据集访问：SalesOrders + BaseEmployees + SysParameters；不再读取明细 / 商品、无逐单 / 逐行查库
        Assert.Equal(3, counting.DatasetReads);
        Assert.Equal(0, counting.WriteCalls);
    }
}
