using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// 柜量与装柜利用率证据报表（ERP-255）「全匹配」汇总聚焦单元测试：
/// 纯规则在分页 / 选定列投影之前对同一份完整有界作用域化证据桶派生每日与期间汇总，
/// 每日按装柜日历日升序、证据桶数 / 已审核装柜清单数 / 缺柜号清单数与签名独立单位合计；期间合计使用相同事实；
/// 无证据时显式无证据上下文、已记录零与无证据区分；绝不合计授权范围客户数或推断实体柜容量；
/// 原始「#1」柜号与空白装柜清单 Id 1 的身份碰撞被类型化 / 消歧身份拆分为不相交桶；越界页与隐藏字段下汇总仍覆盖全部匹配桶。
/// <para>全部为纯规则或内存数据库（TestDbFactory），不连接 SQL Server、不启动 API、不执行任何 SQL / seed、不运行浏览器验收。</para>
/// </summary>
public class DynamicContainerStatsSummaryTests
{
    private static ReportDtos.ContainerStatsItem Item(
        DateTime date, string containerNo, int loadingListCount = 1, bool blank = false,
        decimal cartons = 0m, decimal weight = 0m, decimal volume = 0m)
        => new()
        {
            BucketKey = $"{date:yyyyMMdd}|{containerNo}",
            LoadingDate = date,
            ContainerNo = containerNo,
            ContainerNoBlank = blank,
            LoadingListCount = loadingListCount,
            TotalCartons = cartons,
            TotalWeight = weight,
            TotalVolume = volume,
        };

    private static ContainerLoadingList List(long id, DateTime date, string? containerNo,
        decimal cartons = 0m, decimal weight = 0m, decimal volume = 0m)
        => new()
        {
            Id = id,
            LoadingDate = date,
            ContainerNo = containerNo ?? string.Empty,
            CustomerId = 101,
            TotalCartons = cartons,
            TotalWeight = weight,
            TotalVolume = volume,
        };

    // ==================== 1. 空来源 / 无证据与已记录零 ====================

    [Fact]
    public void 空来源_无每日行且显式无证据上下文()
    {
        var summary = DynamicContainerStatsSummaryRules.BuildSummary(
            Array.Empty<ReportDtos.ContainerStatsItem>());

        Assert.Empty(summary.DailyRows);
        Assert.Equal(0, summary.Period.BucketCount);
        Assert.Equal(0, summary.Period.ApprovedLists);
        Assert.Equal(0, summary.Period.MissingContainerNoCount);
        Assert.Equal(0m, summary.Period.TotalCartons);
        Assert.Equal(0m, summary.Period.TotalWeight);
        Assert.Equal(0m, summary.Period.TotalVolume);
        Assert.Equal(DynamicContainerStatsSummaryRules.NoEvidenceContext, summary.NoEvidenceContext);
        Assert.NotEmpty(summary.CoverageText);
        Assert.NotEmpty(summary.DailyColumns);
    }

    [Fact]
    public void 已记录零_区分无证据()
    {
        var summary = DynamicContainerStatsSummaryRules.BuildSummary(new[]
        {
            Item(new DateTime(2026, 9, 10), "TCLU-001", cartons: 0m, weight: 0m, volume: 0m),
        });

        var day = Assert.Single(summary.DailyRows);
        Assert.Equal(0m, day.TotalCartons);
        Assert.Equal(0m, day.TotalWeight);
        Assert.Equal(0m, day.TotalVolume);
        Assert.Equal(string.Empty, summary.NoEvidenceContext);   // 有证据（已记录零）≠ 无证据
    }

    // ==================== 2. 每日 / 期间汇总 ====================

    [Fact]
    public void 每日_按日期升序_证据桶数与已审核清单数与缺柜号清单数()
    {
        var summary = DynamicContainerStatsSummaryRules.BuildSummary(new[]
        {
            Item(new DateTime(2026, 9, 11), "AAA"),
            Item(new DateTime(2026, 9, 10), "BBB", loadingListCount: 2),
            Item(new DateTime(2026, 9, 10), "未填柜号（装柜清单 #1）", blank: true),
            Item(new DateTime(2026, 9, 10), "未填柜号（装柜清单 #2）", blank: true),
        });

        Assert.Equal(2, summary.DailyRows.Count);
        Assert.Equal(new DateTime(2026, 9, 10), summary.DailyRows[0].Date);
        Assert.Equal(3, summary.DailyRows[0].BucketCount);
        Assert.Equal(4, summary.DailyRows[0].ApprovedLists);           // 2 + 1 + 1
        Assert.Equal(2, summary.DailyRows[0].MissingContainerNoCount); // 两个空白柜号清单
        Assert.Equal(new DateTime(2026, 9, 11), summary.DailyRows[1].Date);
        Assert.Equal(1, summary.DailyRows[1].BucketCount);
        Assert.Equal(1, summary.DailyRows[1].ApprovedLists);
        Assert.Equal(0, summary.DailyRows[1].MissingContainerNoCount);
    }

    [Fact]
    public void 签名度量_独立单位有符号合计_不取绝对值()
    {
        var summary = DynamicContainerStatsSummaryRules.BuildSummary(new[]
        {
            Item(new DateTime(2026, 9, 10), "A", cartons: 10m, weight: -5m, volume: 3m),
            Item(new DateTime(2026, 9, 10), "B", cartons: -2m, weight: 8m, volume: -1m),
        });

        var day = Assert.Single(summary.DailyRows);
        Assert.Equal(8m, day.TotalCartons);
        Assert.Equal(3m, day.TotalWeight);
        Assert.Equal(2m, day.TotalVolume);
    }

    [Fact]
    public void 期间合计_使用相同事实_与每日合计一致()
    {
        var items = new[]
        {
            Item(new DateTime(2026, 9, 10), "A", cartons: 1m, weight: 2m, volume: 3m),
            Item(new DateTime(2026, 9, 10), "B", cartons: 4m, weight: 5m, volume: 6m),
            Item(new DateTime(2026, 9, 11), "C", cartons: 7m, weight: 8m, volume: 9m),
        };
        var summary = DynamicContainerStatsSummaryRules.BuildSummary(items);

        Assert.Equal(items.Length, summary.Period.BucketCount);
        Assert.Equal(items.Sum(x => x.LoadingListCount), summary.Period.ApprovedLists);
        Assert.Equal(12m, summary.Period.TotalCartons);
        Assert.Equal(15m, summary.Period.TotalWeight);
        Assert.Equal(18m, summary.Period.TotalVolume);
    }

    // ==================== 3. 桶身份碰撞：原始「#1」柜号 vs 空白清单 Id 1 ====================

    [Fact]
    public void 原始井号柜号与空白清单Id_汇总缺柜号计数真实()
    {
        var buckets = ContainerStatsEvidenceRules.BuildBuckets(new[]
        {
            List(2, new DateTime(2026, 9, 10), "#1", cartons: 10m),
            List(1, new DateTime(2026, 9, 10), "   "),
        });

        Assert.Equal(2, buckets.Count);

        var summary = DynamicContainerStatsSummaryRules.BuildSummary(buckets);
        var day = Assert.Single(summary.DailyRows);
        Assert.Equal(2, day.BucketCount);
        Assert.Equal(2, day.ApprovedLists);
        Assert.Equal(1, day.MissingContainerNoCount);   // 仅空白清单 1，绝不把原始「#1」误计为缺柜号
        Assert.Equal(10m, day.TotalCartons);
    }

    // ==================== 4. 越界页 / 隐藏字段下汇总仍覆盖全部匹配桶 ====================

    [Fact]
    public void BuildPage_越界页与隐藏字段_汇总仍覆盖全部匹配桶()
    {
        var items = new[]
        {
            Item(new DateTime(2026, 9, 10), "A"),
            Item(new DateTime(2026, 9, 11), "B"),
            Item(new DateTime(2026, 9, 12), "C"),
        };

        var page = DynamicContainerStatsReportRules.BuildPage(
            items, new List<string>(), page: 99, pageSize: 20,
            start: new DateTime(2026, 9, 1), end: new DateTime(2026, 9, 30));

        Assert.NotNull(page.Summary);
        Assert.Empty(page.Columns);
        Assert.Empty(page.Rows);
        Assert.Equal(3, page.Summary.DailyRows.Count);
        Assert.Equal(3, page.Summary.Period.BucketCount);
    }

    [Fact]
    public void BuildPage_当前页仅一行_汇总仍覆盖全部匹配桶()
    {
        var items = new[]
        {
            Item(new DateTime(2026, 9, 10), "A"),
            Item(new DateTime(2026, 9, 11), "B"),
            Item(new DateTime(2026, 9, 12), "C"),
        };

        var page = DynamicContainerStatsReportRules.BuildPage(
            items, new List<string> { "containerNo" }, page: 1, pageSize: 1,
            start: new DateTime(2026, 9, 1), end: new DateTime(2026, 9, 30));

        Assert.NotNull(page.Summary);
        Assert.Single(page.Rows);                         // 当前页仅 1 行
        Assert.Equal(3, page.Summary.DailyRows.Count);    // 汇总仍覆盖全部匹配桶
        Assert.Equal(3, page.Summary.Period.BucketCount);
    }
}

