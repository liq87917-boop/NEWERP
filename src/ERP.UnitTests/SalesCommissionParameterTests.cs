using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// 业务员提成表（ERP-243）SalesCommissionRate 系统参数解析单元测试：
/// 恰好一条「不变量普通十进制 0..100」（含显式 0）为可空的当前参考比例；缺失 / 重复 / 非法 / 负数 / 超范围
/// 一律未知并给出显式原因，绝不回退为 0、绝不改写参数；服务层至多读取 2 条未删除记录。
/// <para>全部使用内存数据库（TestDbFactory），不连接 SQL Server、不启动 API、不执行任何 SQL / seed、不运行浏览器验收。</para>
/// </summary>
public class SalesCommissionParameterTests
{
    private static readonly DateTime Start = new(2026, 9, 1);
    private static readonly DateTime End = new(2026, 9, 30);

    private static readonly SalespersonDataScope PrivilegedScope = new() { IsPrivileged = true, AllowedCustomerIds = null };

    private static SalesOrder SeedOrder(ErpDbContext db)
    {
        var customer = new BaseCustomer { CustomerCode = "C001", CustomerName = "客户", Status = 1 };
        var employee = new BaseEmployee { EmployeeCode = "S001", EmployeeName = "业务员甲", IsSalesman = true, Status = 1 };
        db.BaseCustomers.Add(customer);
        db.BaseEmployees.Add(employee);
        db.SaveChanges();

        var order = new SalesOrder
        {
            OrderNo = "SO-1",
            OrderDate = new DateTime(2026, 9, 10),
            CustomerId = customer.Id,
            SalesmanId = employee.Id,
            Currency = Currency.USD,
            TotalAmount = 100m,
            Status = DocumentStatus.Approved
        };
        db.SalesOrders.Add(order);
        db.SaveChanges();
        return order;
    }

    private static void SeedRate(ErpDbContext db, params string[] values)
    {
        foreach (var value in values)
        {
            db.SysParameters.Add(new SysParameter
            {
                ParamKey = SalesCommissionEvidenceRules.SalesCommissionRateKey,
                ParamValue = value,
                ParamName = "业务员提成比例(%)",
                IsSystem = true
            });
        }
        db.SaveChanges();
    }

    // ==================== 纯规则：参数解析 ====================

    [Fact]
    public void 缺失_未知()
    {
        var (rate, reason) = SalesCommissionEvidenceRules.ParseRate(Array.Empty<string>());
        Assert.Null(rate);
        Assert.Equal(SalesCommissionEvidenceRules.RateMissingReason, reason);
    }

    [Fact]
    public void 重复_未知()
    {
        var (rate, reason) = SalesCommissionEvidenceRules.ParseRate(new[] { "5", "6" });
        Assert.Null(rate);
        Assert.Equal(SalesCommissionEvidenceRules.RateDuplicateReason, reason);
    }

    [Fact]
    public void 显式零_当前参考比例0()
    {
        var (rate, reason) = SalesCommissionEvidenceRules.ParseRate(new[] { "0" });
        Assert.Equal(0m, rate);
        Assert.Equal(string.Empty, reason);
    }

    [Fact]
    public void 有效小数_可知()
    {
        var (rate, reason) = SalesCommissionEvidenceRules.ParseRate(new[] { "12.5" });
        Assert.Equal(12.5m, rate);
        Assert.Equal(string.Empty, reason);
    }

    [Fact]
    public void 上限100_可知()
    {
        var (rate, reason) = SalesCommissionEvidenceRules.ParseRate(new[] { "100" });
        Assert.Equal(100m, rate);
        Assert.Equal(string.Empty, reason);
    }

    [Fact]
    public void 非法文本_未知()
    {
        var (rate, reason) = SalesCommissionEvidenceRules.ParseRate(new[] { "abc" });
        Assert.Null(rate);
        Assert.Equal(SalesCommissionEvidenceRules.RateMalformedReason, reason);
    }

    [Fact]
    public void 负数_未知()
    {
        var (rate, reason) = SalesCommissionEvidenceRules.ParseRate(new[] { "-1" });
        Assert.Null(rate);
        Assert.Equal(SalesCommissionEvidenceRules.RateNegativeReason, reason);
    }

    [Fact]
    public void 超范围_未知()
    {
        var (rate, reason) = SalesCommissionEvidenceRules.ParseRate(new[] { "101" });
        Assert.Null(rate);
        Assert.Equal(SalesCommissionEvidenceRules.RateExcessiveReason, reason);
    }

    [Theory]
    [InlineData("1,000")]
    [InlineData("1e2")]
    [InlineData("50%")]
    public void 非普通十进制_未知(string value)
    {
        var (rate, reason) = SalesCommissionEvidenceRules.ParseRate(new[] { value });
        Assert.Null(rate);
        Assert.Equal(SalesCommissionEvidenceRules.RateMalformedReason, reason);
    }

    // ==================== 服务层：有界参数读取 ====================

    [Fact]
    public async Task 恰好一条有效参数_CommissionRate可知()
    {
        using var db = TestDbFactory.Create();
        SeedOrder(db);
        SeedRate(db, "7.5");

        var service = new ReportService(db);
        var result = await service.GetSalesCommissionAsync(Start, End, PrivilegedScope);

        var row = Assert.Single(result);
        Assert.Equal(7.5m, row.CommissionRate);
        Assert.Equal(SalesCommissionEvidenceRules.CommissionRateEvidence, row.CommissionRateEvidence);
    }

    [Fact]
    public async Task 两条参数_CommissionRate未知重复原因()
    {
        using var db = TestDbFactory.Create();
        SeedOrder(db);
        SeedRate(db, "7", "8");

        var service = new ReportService(db);
        var result = await service.GetSalesCommissionAsync(Start, End, PrivilegedScope);

        var row = Assert.Single(result);
        Assert.Null(row.CommissionRate);
        Assert.Equal(SalesCommissionEvidenceRules.RateDuplicateReason, row.CommissionRateEvidence);
    }

    [Fact]
    public async Task 参数缺失_CommissionRate未知缺失原因()
    {
        using var db = TestDbFactory.Create();
        SeedOrder(db);

        var service = new ReportService(db);
        var result = await service.GetSalesCommissionAsync(Start, End, PrivilegedScope);

        var row = Assert.Single(result);
        Assert.Null(row.CommissionRate);
        Assert.Equal(SalesCommissionEvidenceRules.RateMissingReason, row.CommissionRateEvidence);
    }

    [Fact]
    public async Task 参数为显式0_CommissionRate为0非null()
    {
        using var db = TestDbFactory.Create();
        SeedOrder(db);
        SeedRate(db, "0");

        var service = new ReportService(db);
        var result = await service.GetSalesCommissionAsync(Start, End, PrivilegedScope);

        var row = Assert.Single(result);
        Assert.Equal(0m, row.CommissionRate);
        Assert.Equal(SalesCommissionEvidenceRules.CommissionRateEvidence, row.CommissionRateEvidence);
    }
}
