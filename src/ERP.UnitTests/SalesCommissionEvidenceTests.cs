using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// 业务员提成表（ERP-243）证据分组单元测试：
/// 按「持久化业务员桶 × 原始原币」分组；已知币种按签名订单头金额小计、未知 / 无效币种保留原始键且金额为 null 仅计数；
/// 未分配业务员与缺失 / 已删除员工身份为不同桶；利润 / 利润率 / 提成额恒为未知（null），即使系统参数为显式 0
/// 也不从当前商品成本派生或推断为 0；来源 / 币种 / 利润 / 提成 / 比率标签齐备。
/// <para>全部使用内存数据库（TestDbFactory），不连接 SQL Server、不启动 API、不执行任何 SQL / seed、不运行浏览器验收。</para>
/// </summary>
public class SalesCommissionEvidenceTests
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
        ErpDbContext db, string orderNo, long customerId, long? salesmanId,
        Currency currency = Currency.USD, decimal totalAmount = 100m)
    {
        var order = new SalesOrder
        {
            OrderNo = orderNo,
            OrderDate = new DateTime(2026, 9, 10),
            CustomerId = customerId,
            SalesmanId = salesmanId,
            Currency = currency,
            TotalAmount = totalAmount,
            Status = DocumentStatus.Approved
        };
        db.SalesOrders.Add(order);
        db.SaveChanges();
        return order;
    }

    private static void SeedRate(ErpDbContext db, string value)
    {
        db.SysParameters.Add(new SysParameter
        {
            ParamKey = SalesCommissionEvidenceRules.SalesCommissionRateKey,
            ParamValue = value,
            ParamName = "业务员提成比例(%)",
            IsSystem = true
        });
        db.SaveChanges();
    }

    [Fact]
    public async Task 已知币种按原币签名小计_负数保留签名()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "客户");
        var emp = SeedEmployee(db, "S001", "业务员甲");

        SeedOrder(db, "SO-1", customer.Id, emp.Id, Currency.USD, 100m);
        SeedOrder(db, "SO-2", customer.Id, emp.Id, Currency.USD, -30m);
        SeedOrder(db, "SO-3", customer.Id, emp.Id, Currency.EUR, 50m);

        var service = new ReportService(db);
        var result = await service.GetSalesCommissionAsync(Start, End, PrivilegedScope);

        Assert.Equal(2, result.Count);
        var usd = Assert.Single(result, x => x.Currency == "USD");
        Assert.Equal(70m, usd.SalesAmount); // 签名保留：100 + (-30)
        var eur = Assert.Single(result, x => x.Currency == "EUR");
        Assert.Equal(50m, eur.SalesAmount);
    }

    [Fact]
    public async Task 未知无效币种保留原始键_金额null仅计数()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "客户");
        var emp = SeedEmployee(db, "S001", "业务员甲");

        SeedOrder(db, "SO-USD", customer.Id, emp.Id, Currency.USD, 100m);
        SeedOrder(db, "SO-BAD", customer.Id, emp.Id, (Currency)999, 100m);

        var service = new ReportService(db);
        var result = await service.GetSalesCommissionAsync(Start, End, PrivilegedScope);

        var bad = Assert.Single(result, x => x.Currency == "999");
        Assert.Equal("未知币种", bad.CurrencyLabel);
        Assert.Null(bad.SalesAmount);
        Assert.Equal(1, bad.OrderCount);
        Assert.Equal(SalesCommissionEvidenceRules.UnknownCurrencyEvidence, bad.CurrencyEvidence);
    }

    [Fact]
    public async Task 未分配业务员与缺失已删除员工身份为不同桶()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "客户");
        var employee = SeedEmployee(db, "S001", "业务员甲");
        employee.IsDeleted = true;
        db.SaveChanges();

        SeedOrder(db, "SO-NONE", customer.Id, null);
        SeedOrder(db, "SO-GONE", customer.Id, employee.Id);

        var service = new ReportService(db);
        var result = await service.GetSalesCommissionAsync(Start, End, PrivilegedScope);

        Assert.Equal(2, result.Count);
        var unassigned = Assert.Single(result, x => x.SalesmanId == null);
        Assert.Equal(SalesCommissionEvidenceRules.UnassignedSalesmanName, unassigned.SalesmanName);
        Assert.Equal(SalesCommissionEvidenceRules.UnassignedSalesmanIdentityEvidence, unassigned.SalesmanIdentityEvidence);

        var unknown = Assert.Single(result, x => x.SalesmanId == employee.Id);
        Assert.Equal(SalesCommissionEvidenceRules.UnknownSalesmanName, unknown.SalesmanName);
        Assert.Equal(SalesCommissionEvidenceRules.UnknownSalesmanIdentityEvidence, unknown.SalesmanIdentityEvidence);
    }

    [Fact]
    public async Task 利润利润率提成额恒为null_即使参数为显式0_且有明细与当前成本()
    {
        using var db = TestDbFactory.Create();
        SeedRate(db, "0");
        var customer = SeedCustomer(db, "C001", "客户");
        var emp = SeedEmployee(db, "S001", "业务员甲");
        var product = new BaseProduct { ProductCode = "P001", ProductName = "商品", CostPrice = 60m };
        db.BaseProducts.Add(product);
        db.SaveChanges();

        var order = SeedOrder(db, "SO-1", customer.Id, emp.Id, Currency.USD, 2000m);
        db.SalesOrderDetails.Add(new SalesOrderDetail
        {
            SalesOrderId = order.Id,
            ProductId = product.Id,
            ProductName = product.ProductName,
            Quantity = 20m,
            UnitPrice = 100m
        });
        db.SaveChanges();

        var service = new ReportService(db);
        var result = await service.GetSalesCommissionAsync(Start, End, PrivilegedScope);

        var row = Assert.Single(result);
        Assert.Equal(0m, row.CommissionRate); // 显式 0 为可知的当前参考比例
        Assert.Null(row.Profit);
        Assert.Null(row.ProfitRate);
        Assert.Null(row.CommissionAmount); // 绝不从当前成本派生提成、绝不推断为 0
    }

    [Fact]
    public async Task 来源币种利润提成比率标签齐备()
    {
        using var db = TestDbFactory.Create();
        SeedRate(db, "12.5");
        var customer = SeedCustomer(db, "C001", "客户");
        var emp = SeedEmployee(db, "S001", "业务员甲");
        SeedOrder(db, "SO-1", customer.Id, emp.Id, Currency.USD, 100m);

        var service = new ReportService(db);
        var result = await service.GetSalesCommissionAsync(Start, End, PrivilegedScope);

        var row = Assert.Single(result);
        Assert.Equal(SalesCommissionEvidenceRules.SourceLabel, row.SourceLabel);
        Assert.Equal(SalesCommissionEvidenceRules.AmountLabel, row.AmountLabel);
        Assert.Equal(SalesCommissionEvidenceRules.KnownCurrencyEvidence, row.CurrencyEvidence);
        Assert.Equal(SalesCommissionEvidenceRules.ProfitEvidence, row.ProfitEvidence);
        Assert.Equal(SalesCommissionEvidenceRules.ProfitRateEvidence, row.ProfitRateEvidence);
        Assert.Equal(SalesCommissionEvidenceRules.CommissionEvidence, row.CommissionEvidence);
        Assert.Equal(12.5m, row.CommissionRate);
        Assert.Equal(SalesCommissionEvidenceRules.CommissionRateEvidence, row.CommissionRateEvidence);
    }
}
