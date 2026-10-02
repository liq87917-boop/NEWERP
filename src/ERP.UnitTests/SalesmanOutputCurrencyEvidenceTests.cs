using ERP.Application.Common;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// 业务员产值报表（ERP-236）币种分组单元测试：
/// 金额仅已知枚举币种按签名原币小计；未知 / 无效币种保留各自原始键（如 "999"）、金额为 null 仅保留订单头计数；
/// 同一业务员多币种分别成行、绝不跨币种合计；稳定业务员 Id / 币种排序取代跨币种金额排名；利润恒为未知（null）。
/// <para>全部使用内存数据库（TestDbFactory）或纯规则函数，不连接 SQL Server、不启动 API、不执行任何 SQL / seed、不运行浏览器验收。</para>
/// </summary>
public class SalesmanOutputCurrencyEvidenceTests
{
    private static readonly DateTime Start = new(2026, 9, 1);
    private static readonly DateTime End = new(2026, 9, 30);

    private static readonly SalespersonDataScope PrivilegedScope = new() { IsPrivileged = true, AllowedCustomerIds = null };

    // ==================== 纯规则：币种 ====================

    [Fact]
    public void 币种分组键_已知枚举返回名称_未知无效保留原始键()
    {
        Assert.Equal("USD", SalesmanOutputEvidenceRules.CurrencyGroupKey(Currency.USD));
        Assert.Equal("CNY", SalesmanOutputEvidenceRules.CurrencyGroupKey(Currency.CNY));
        Assert.Equal("999", SalesmanOutputEvidenceRules.CurrencyGroupKey((Currency)999));
        Assert.Equal("USD 美元", SalesmanOutputEvidenceRules.CurrencyGroupLabel("USD"));
        Assert.Equal("未知币种", SalesmanOutputEvidenceRules.CurrencyGroupLabel("999"));
        Assert.True(SalesmanOutputEvidenceRules.IsKnownCurrency(Currency.USD));
        Assert.False(SalesmanOutputEvidenceRules.IsKnownCurrency((Currency)999));
    }

    // ==================== 服务层：币种 ====================

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
        ErpDbContext db, string orderNo, long customerId, long? salesmanId, Currency currency, decimal totalAmount)
    {
        var order = new SalesOrder
        {
            OrderNo = orderNo,
            OrderDate = new DateTime(2026, 9, 10),
            CustomerId = customerId,
            SalesmanId = salesmanId,
            Currency = currency,
            Status = DocumentStatus.Approved,
            TotalAmount = totalAmount
        };
        db.SalesOrders.Add(order);
        db.SaveChanges();
        return order;
    }

    [Fact]
    public async Task 服务层_同一业务员多币种分别成行_绝不跨币种合计()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "客户");
        var emp = SeedEmployee(db, "S001", "业务员甲");

        SeedOrder(db, "SO-USD", customer.Id, emp.Id, Currency.USD, 1000m);
        SeedOrder(db, "SO-CNY", customer.Id, emp.Id, Currency.CNY, 500m);

        var service = new ReportService(db);
        var result = await service.GetSalesmanOutputAsync(Start, End, PrivilegedScope);

        Assert.Equal(2, result.Count);
        Assert.DoesNotContain(result, x => x.TotalAmount == 1500m);   // 绝不出现跨币种合计

        var usd = Assert.Single(result.Where(r => r.Currency == "USD"));
        Assert.Equal(1000m, usd.TotalAmount);
        Assert.Equal(1, usd.OrderCount);

        var cny = Assert.Single(result.Where(r => r.Currency == "CNY"));
        Assert.Equal(500m, cny.TotalAmount);
        Assert.Equal(1, cny.OrderCount);
    }

    [Fact]
    public async Task 服务层_已知币种按签名原币小计_可为负()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "客户");
        var emp = SeedEmployee(db, "S001", "业务员甲");

        SeedOrder(db, "SO-POS", customer.Id, emp.Id, Currency.USD, 1000m);
        SeedOrder(db, "SO-NEG", customer.Id, emp.Id, Currency.USD, -200m);

        var service = new ReportService(db);
        var result = await service.GetSalesmanOutputAsync(Start, End, PrivilegedScope);

        var row = Assert.Single(result);
        Assert.Equal("USD", row.Currency);
        Assert.Equal(800m, row.TotalAmount);   // 1000 + (-200)
        Assert.Equal(2, row.OrderCount);
    }

    [Fact]
    public async Task 服务层_未知币种金额为null_保留原始键与计数()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "客户");
        var emp = SeedEmployee(db, "S001", "业务员甲");

        SeedOrder(db, "SO-UNKNOWN", customer.Id, emp.Id, (Currency)999, 100m);
        SeedOrder(db, "SO-UNKNOWN2", customer.Id, emp.Id, (Currency)999, 200m);

        var service = new ReportService(db);
        var result = await service.GetSalesmanOutputAsync(Start, End, PrivilegedScope);

        var row = Assert.Single(result);
        Assert.Equal("999", row.Currency);            // 保留原始键，绝不折叠为默认币种
        Assert.Equal("未知币种", row.CurrencyLabel);
        Assert.Null(row.TotalAmount);                 // 未知金额绝不回落为 0
        Assert.Equal(2, row.OrderCount);              // 仅保留计数证据
        Assert.Equal(SalesmanOutputEvidenceRules.UnknownCurrencyEvidence, row.CurrencyEvidence);
    }

    [Fact]
    public async Task 服务层_未知币种不同原始键分别成行()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "客户");
        var emp = SeedEmployee(db, "S001", "业务员甲");

        SeedOrder(db, "SO-U999", customer.Id, emp.Id, (Currency)999, 100m);
        SeedOrder(db, "SO-U1000", customer.Id, emp.Id, (Currency)1000, 200m);

        var service = new ReportService(db);
        var result = await service.GetSalesmanOutputAsync(Start, End, PrivilegedScope);

        Assert.Equal(2, result.Count);
        Assert.Contains(result, r => r.Currency == "999");
        Assert.Contains(result, r => r.Currency == "1000");
        Assert.All(result, r => Assert.Null(r.TotalAmount));
    }

    [Fact]
    public async Task 服务层_稳定业务员Id与币种排序_不按金额排名()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "客户");
        var emp1 = SeedEmployee(db, "S001", "业务员甲");
        var emp2 = SeedEmployee(db, "S002", "业务员乙");

        // 业务员乙金额更大，但稳定排序应业务员 Id 升序：甲（Id 较小）在前、乙在后
        SeedOrder(db, "SO-BIG", customer.Id, emp2.Id, Currency.USD, 9000m);
        SeedOrder(db, "SO-SMALL", customer.Id, emp1.Id, Currency.USD, 100m);

        var service = new ReportService(db);
        var result = await service.GetSalesmanOutputAsync(Start, End, PrivilegedScope);

        Assert.Equal(2, result.Count);
        Assert.Equal(emp1.Id, result[0].SalesmanId);
        Assert.Equal(100m, result[0].TotalAmount);
        Assert.Equal(emp2.Id, result[1].SalesmanId);
        Assert.Equal(9000m, result[1].TotalAmount);
    }

    [Fact]
    public async Task 服务层_员工姓名快照缺失_显式未知身份_不改派其他业务员()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "客户");
        SeedOrder(db, "SO-GHOST", customer.Id, 999L, Currency.USD, 100m);

        var service = new ReportService(db);
        var result = await service.GetSalesmanOutputAsync(Start, End, PrivilegedScope);

        var row = Assert.Single(result);
        Assert.Equal(999L, row.SalesmanId);
        Assert.Equal(SalesmanOutputEvidenceRules.UnknownSalesmanName, row.SalesmanName);
        Assert.Equal(SalesmanOutputEvidenceRules.UnknownSalesmanIdentityEvidence, row.SalesmanIdentityEvidence);
    }

    // ==================== 纯规则：利润 / 利润率证据（ERP-239） ====================

    [Fact]
    public void 利润与利润率证据_恒未知_不回落为0_不做当前价推算()
    {
        Assert.Contains("利润未知", SalesmanOutputEvidenceRules.ProfitEvidence);
        Assert.Contains("历史", SalesmanOutputEvidenceRules.ProfitEvidence);
        Assert.Contains("不回落为0", SalesmanOutputEvidenceRules.ProfitEvidence);

        Assert.Contains("利润率未知", SalesmanOutputEvidenceRules.ProfitRateEvidence);
        Assert.Contains("与利润同源", SalesmanOutputEvidenceRules.ProfitRateEvidence);
        Assert.Contains("历史", SalesmanOutputEvidenceRules.ProfitRateEvidence);
        Assert.Contains("不回落为0", SalesmanOutputEvidenceRules.ProfitRateEvidence);
    }
}
