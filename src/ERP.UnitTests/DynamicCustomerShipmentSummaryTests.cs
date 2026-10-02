using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// 动态客户出货量证据报表（ERP-232）「全匹配」汇总聚焦单元测试：
/// 纯规则在分页 / 选定列投影之前对同一份完整有界作用域化列表派生原币金额与精确单位数量汇总，
/// 已知币种签名合计 / 去重客户数与不相交订单数、未知币种金额 null 且显式计数证据、绝不跨币种合计；
/// 每个原币内精确单位独立合并签名数量与明细条数、未知单位数量 null、明细计数非订单 / 客户去重数、
/// 缺失 / 不完整来源显式保留完整度原因与不完整桶数；金额只属于原币面板、绝不复制到单位行；
/// 空来源与越界页 / 隐藏字段下汇总仍可用。
/// <para>全部为纯规则或内存数据库（TestDbFactory），不连接 SQL Server、不启动 API、不执行任何 SQL / seed、不运行浏览器验收。</para>
/// </summary>
public class DynamicCustomerShipmentSummaryTests
{
    private static ReportDtos.CustomerShipmentItem Item(
        long customerId, string currency, decimal? totalAmount, int orderCount,
        string completenessReason = "",
        List<ReportDtos.CustomerShipmentUnitGroup>? unitGroups = null)
        => new()
        {
            CustomerId = customerId,
            CustomerName = "客户" + customerId,
            Currency = currency,
            TotalAmount = totalAmount,
            OrderCount = orderCount,
            QuantityCompletenessReason = completenessReason,
            UnitGroups = unitGroups ?? new()
        };

    private static ReportDtos.CustomerShipmentUnitGroup Unit(string unit, decimal? quantity, int detailCount)
        => new()
        {
            Unit = unit,
            Quantity = quantity,
            DetailCount = detailCount,
            QuantityLabel = unit == CustomerShipmentEvidenceRules.UnknownUnitGroup
                ? CustomerShipmentEvidenceRules.UnknownUnitQuantityLabel
                : CustomerShipmentEvidenceRules.KnownUnitQuantityLabel
        };

    // ==================== 1. 原币金额汇总 ====================

    [Fact]
    public void 原币汇总_已知币种签名合计_去重客户数与不相交订单数_无跨币种合计()
    {
        var summary = DynamicCustomerShipmentSummaryRules.BuildSummary(new List<ReportDtos.CustomerShipmentItem>
        {
            Item(1, "USD", 1000m, 2),
            Item(2, "USD", -200m, 3),
            Item(1, "CNY", 500m, 1),
        });

        Assert.Equal(2, summary.CurrencyRows.Count);

        var usd = Assert.Single(summary.CurrencyRows.Where(r => r.Currency == "USD"));
        Assert.Equal(800m, usd.TotalAmount);       // 1000 + (-200)，签名合计、不逐客户四舍五入
        Assert.Equal(2, usd.CustomerCount);         // 去重客户数
        Assert.Equal(5, usd.OrderCount);            // 不相交订单数合计
        Assert.Equal(CustomerShipmentEvidenceRules.KnownCurrencyEvidence, usd.Evidence);
        Assert.Equal("USD 美元", usd.CurrencyLabel);

        var cny = Assert.Single(summary.CurrencyRows.Where(r => r.Currency == "CNY"));
        Assert.Equal(500m, cny.TotalAmount);
        Assert.Equal(1, cny.CustomerCount);
        Assert.Equal(1, cny.OrderCount);

        // 绝不出现跨币种合计
        Assert.DoesNotContain(summary.CurrencyRows, r => r.TotalAmount == 1300m);
    }

    [Fact]
    public void 原币汇总_未知币种_金额null_显式未知计数证据()
    {
        var summary = DynamicCustomerShipmentSummaryRules.BuildSummary(new List<ReportDtos.CustomerShipmentItem>
        {
            Item(1, CustomerShipmentEvidenceRules.UnknownCurrencyGroup, null, 2),
            Item(2, CustomerShipmentEvidenceRules.UnknownCurrencyGroup, null, 1),
        });

        var row = Assert.Single(summary.CurrencyRows);
        Assert.Equal(CustomerShipmentEvidenceRules.UnknownCurrencyGroup, row.Currency);
        Assert.Null(row.TotalAmount);              // 未知币种金额未知，绝不回落为 0
        Assert.Equal(2, row.CustomerCount);
        Assert.Equal(3, row.OrderCount);
        Assert.Equal(CustomerShipmentEvidenceRules.UnknownCurrencyEvidence, row.Evidence);
    }

    [Fact]
    public void 原币汇总_零与负值_保留不丢弃()
    {
        var summary = DynamicCustomerShipmentSummaryRules.BuildSummary(new List<ReportDtos.CustomerShipmentItem>
        {
            Item(1, "USD", 0m, 1),
            Item(2, "USD", -5m, 1),
        });

        var usd = Assert.Single(summary.CurrencyRows);
        Assert.Equal(-5m, usd.TotalAmount);        // 0 + (-5)，零 / 负值保留
        Assert.Equal(2, usd.CustomerCount);
    }

    // ==================== 2. 精确单位数量汇总 ====================

    [Fact]
    public void 单位汇总_按原币内原始单位合并_签名数量与明细条数_未知单位null()
    {
        var summary = DynamicCustomerShipmentSummaryRules.BuildSummary(new List<ReportDtos.CustomerShipmentItem>
        {
            Item(1, "USD", 100m, 1, unitGroups: new()
            {
                Unit("PCS", 10m, 2),
                Unit("PCS", -2m, 1),
                Unit(CustomerShipmentEvidenceRules.UnknownUnitGroup, null, 2),
            }),
            Item(2, "USD", 50m, 1, unitGroups: new()
            {
                Unit("PCS", 5m, 1),
            }),
            Item(1, "CNY", 100m, 1, unitGroups: new()
            {
                Unit("PCS", 7m, 3),
            }),
        });

        var usdPcs = Assert.Single(summary.UnitRows.Where(r => r.Currency == "USD" && r.Unit == "PCS"));
        Assert.Equal(13m, usdPcs.Quantity);        // 10 + (-2) + 5
        Assert.Equal(4, usdPcs.DetailCount);       // 明细条数，非订单 / 客户去重数
        Assert.Equal(CustomerShipmentEvidenceRules.KnownUnitQuantityLabel, usdPcs.QuantityLabel);

        var usdUnknown = Assert.Single(summary.UnitRows.Where(r => r.Currency == "USD" && r.Unit == CustomerShipmentEvidenceRules.UnknownUnitGroup));
        Assert.Null(usdUnknown.Quantity);          // 未知单位数量未知，绝不回落为 0
        Assert.Equal(2, usdUnknown.DetailCount);

        var cnyPcs = Assert.Single(summary.UnitRows.Where(r => r.Currency == "CNY" && r.Unit == "PCS"));
        Assert.Equal(7m, cnyPcs.Quantity);
        Assert.Equal(3, cnyPcs.DetailCount);
    }

    [Fact]
    public void 单位汇总_不归一化_大小写保持不同单位()
    {
        var summary = DynamicCustomerShipmentSummaryRules.BuildSummary(new List<ReportDtos.CustomerShipmentItem>
        {
            Item(1, "USD", 100m, 1, unitGroups: new()
            {
                Unit("PCS", 1m, 1),
                Unit("pcs", 1m, 1),
            }),
        });

        Assert.Equal(2, summary.UnitRows.Count);
        Assert.Contains(summary.UnitRows, r => r.Unit == "PCS");
        Assert.Contains(summary.UnitRows, r => r.Unit == "pcs");
    }

    // ==================== 3. 完整度 / 金额不复制到单位行 ====================

    [Fact]
    public void 完整度_缺失原因显式保留_不完整桶数()
    {
        var summary = DynamicCustomerShipmentSummaryRules.BuildSummary(new List<ReportDtos.CustomerShipmentItem>
        {
            Item(1, "USD", 100m, 1, completenessReason: CustomerShipmentEvidenceRules.UnknownUnitReason),
            Item(2, "USD", 50m, 1, completenessReason: CustomerShipmentEvidenceRules.UnknownUnitReason),
            Item(3, "CNY", 100m, 1),
        });

        Assert.Equal(2, summary.IncompleteBucketCount);
        Assert.Single(summary.CompletenessReasons);
        Assert.Equal(CustomerShipmentEvidenceRules.UnknownUnitReason, summary.CompletenessReasons[0]);
    }

    [Fact]
    public void 单位汇总列_不含任何货币金额列_金额只属于原币面板()
    {
        Assert.Contains(DynamicCustomerShipmentSummaryRules.CurrencySummaryColumns, c => c.Key == "totalAmount");
        Assert.DoesNotContain(DynamicCustomerShipmentSummaryRules.UnitSummaryColumns, c => c.Key == "totalAmount");
        Assert.DoesNotContain(DynamicCustomerShipmentSummaryRules.UnitSummaryColumns,
            c => c.Key.Contains("Amount", StringComparison.OrdinalIgnoreCase));
    }

    // ==================== 4. 空来源 / 越界页 / 隐藏字段 ====================

    [Fact]
    public void 空来源_汇总仍存在_覆盖说明非空且含证据依据()
    {
        var summary = DynamicCustomerShipmentSummaryRules.BuildSummary(Array.Empty<ReportDtos.CustomerShipmentItem>());

        Assert.NotNull(summary);
        Assert.Empty(summary.CurrencyRows);
        Assert.Empty(summary.UnitRows);
        Assert.Equal(0, summary.IncompleteBucketCount);
        Assert.NotEmpty(summary.CoverageText);
        Assert.Contains("已审核", summary.CoverageText);
        Assert.Contains("非实际出库", summary.CoverageText);
    }

    [Fact]
    public void BuildPage_越界页与隐藏字段_汇总仍覆盖全部匹配行()
    {
        var items = new List<ReportDtos.CustomerShipmentItem>
        {
            Item(1, "USD", 1000m, 1),
            Item(2, "USD", 200m, 1),
            Item(3, "CNY", 500m, 1),
        };

        var page = DynamicCustomerShipmentReportRules.BuildPage(
            items,
            new List<string>(),
            page: 99,
            pageSize: 20,
            start: new DateTime(2026, 9, 1),
            end: new DateTime(2026, 9, 30));

        Assert.NotNull(page.Summary);
        Assert.Empty(page.Columns);
        Assert.Empty(page.Rows);
        Assert.Equal(2, page.Summary.CurrencyRows.Count);
        Assert.Equal(3, page.Summary.CurrencyRows.Sum(r => r.CustomerCount));
        Assert.Equal(3, page.Summary.CurrencyRows.Sum(r => r.OrderCount));
    }

    [Fact]
    public void BuildPage_当前页仅一行_汇总仍覆盖全部匹配行()
    {
        var items = new List<ReportDtos.CustomerShipmentItem>
        {
            Item(1, "USD", 1000m, 1),
            Item(2, "USD", 200m, 1),
            Item(3, "CNY", 500m, 1),
        };

        var page = DynamicCustomerShipmentReportRules.BuildPage(
            items,
            new List<string> { "currency" },
            page: 1,
            pageSize: 1,
            start: new DateTime(2026, 9, 1),
            end: new DateTime(2026, 9, 30));

        Assert.NotNull(page.Summary);
        Assert.Single(page.Rows);                  // 当前页仅 1 行
        Assert.Equal(2, page.Summary.CurrencyRows.Count);          // 汇总仍覆盖全部匹配行
        Assert.Equal(3, page.Summary.CurrencyRows.Sum(r => r.CustomerCount));
    }
}
