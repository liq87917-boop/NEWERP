using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// 柜量与装柜利用率统计（ERP-251）纯分组规则单元测试：
/// 按「装柜日历日 × 精确原始非空白柜号」分桶；空白 / 纯空白柜号按装柜清单 Id 独立成桶，
/// 绝不并入一个伪造的共享柜；签名持久化箱数 / 毛重 / 体积分别求和；装载率与柜型恒为未知。
/// 全部为纯函数，不连接数据库、不执行 SQL。
/// </summary>
public class ContainerStatsEvidenceTests
{
    private static ContainerLoadingList List(long id, DateTime date, string? containerNo, long customerId,
        decimal cartons = 0m, decimal weight = 0m, decimal volume = 0m)
        => new()
        {
            Id = id,
            LoadingDate = date,
            ContainerNo = containerNo ?? string.Empty,
            CustomerId = customerId,
            TotalCartons = cartons,
            TotalWeight = weight,
            TotalVolume = volume
        };

    // ==================== 柜号空白判定 ====================

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void 柜号空白_判定为空白(string? containerNo)
        => Assert.True(ContainerStatsEvidenceRules.IsContainerNoBlank(containerNo));

    [Theory]
    [InlineData("TCLU-001")]
    [InlineData(" TCLU-001 ")]
    public void 柜号非空白_判定为非空白(string containerNo)
        => Assert.False(ContainerStatsEvidenceRules.IsContainerNoBlank(containerNo));

    // ==================== 分组键与标签 ====================

    [Fact]
    public void 分组键_非空白柜号_使用原始柜号()
    {
        var key = ContainerStatsEvidenceRules.BucketKey(new DateTime(2026, 9, 10), " TCLU-001 ", 7);
        Assert.Equal("2026-09-10|R| TCLU-001 ", key);
    }

    [Fact]
    public void 分组键_空白柜号_按装柜清单Id独立()
    {
        var key = ContainerStatsEvidenceRules.BucketKey(new DateTime(2026, 9, 10), "   ", 7);
        Assert.Equal("2026-09-10|B|7", key);
    }

    [Fact]
    public void 分组键_原始井号柜号与空白清单Id_不碰撞()
    {
        var raw = ContainerStatsEvidenceRules.BucketKey(new DateTime(2026, 9, 10), "#1", 99);
        var blank = ContainerStatsEvidenceRules.BucketKey(new DateTime(2026, 9, 10), "   ", 1);

        Assert.Equal("2026-09-10|R|#1", raw);
        Assert.Equal("2026-09-10|B|1", blank);
        Assert.NotEqual(raw, blank);
    }

    [Fact]
    public void 构建桶_原始井号柜号与空白清单Id_独立成桶不碰撞()
    {
        var lists = new[]
        {
            List(2, new DateTime(2026, 9, 10), "#1", 101),
            List(1, new DateTime(2026, 9, 10), "   ", 102),
        };

        var buckets = ContainerStatsEvidenceRules.BuildBuckets(lists);

        Assert.Equal(2, buckets.Count);
        var raw = Assert.Single(buckets, b => !b.ContainerNoBlank);
        Assert.Equal("#1", raw.ContainerNo);
        var blank = Assert.Single(buckets, b => b.ContainerNoBlank);
        Assert.Equal("未填柜号（装柜清单 #1）", blank.ContainerNo);
        Assert.Equal(1, blank.LoadingListCount);
    }

    [Fact]
    public void 展示柜号_非空白_原样保留()
        => Assert.Equal(" TCLU-001 ", ContainerStatsEvidenceRules.ContainerNoLabel(" TCLU-001 ", 7));

    [Fact]
    public void 展示柜号_空白_按装柜清单独立标签()
        => Assert.Equal("未填柜号（装柜清单 #7）", ContainerStatsEvidenceRules.ContainerNoLabel("  ", 7));

    // ==================== 证据桶 ====================

    [Fact]
    public void 相同柜号与日期_合并为一个桶_清单数累加()
    {
        var lists = new[]
        {
            List(1, new DateTime(2026, 9, 10), "TCLU-001", 101, 10m, 100m, 5m),
            List(2, new DateTime(2026, 9, 10), "TCLU-001", 102, 20m, 200m, 8m),
        };

        var buckets = ContainerStatsEvidenceRules.BuildBuckets(lists);

        var bucket = Assert.Single(buckets);
        Assert.Equal("TCLU-001", bucket.ContainerNo);
        Assert.False(bucket.ContainerNoBlank);
        Assert.Equal(2, bucket.LoadingListCount);
        Assert.Equal(30m, bucket.TotalCartons);
        Assert.Equal(300m, bucket.TotalWeight);
        Assert.Equal(13m, bucket.TotalVolume);
    }

    [Fact]
    public void 空白柜号_同日多清单_各自独立成桶_绝不合并()
    {
        var lists = new[]
        {
            List(1, new DateTime(2026, 9, 10), "   ", 101),
            List(2, new DateTime(2026, 9, 10), "", 102),
        };

        var buckets = ContainerStatsEvidenceRules.BuildBuckets(lists);

        Assert.Equal(2, buckets.Count);
        Assert.All(buckets, b => Assert.True(b.ContainerNoBlank));
        Assert.Contains(buckets, b => b.ContainerNo == "未填柜号（装柜清单 #1）");
        Assert.Contains(buckets, b => b.ContainerNo == "未填柜号（装柜清单 #2）");
        Assert.All(buckets, b => Assert.Equal(1, b.LoadingListCount));
    }

    [Fact]
    public void 签名度量_保留符号求和_不取绝对值()
    {
        var lists = new[]
        {
            List(1, new DateTime(2026, 9, 10), "TCLU-001", 101, cartons: 10m, weight: -5m, volume: 3m),
            List(2, new DateTime(2026, 9, 10), "TCLU-001", 101, cartons: -2m, weight: 8m, volume: -1m),
        };

        var bucket = Assert.Single(ContainerStatsEvidenceRules.BuildBuckets(lists));

        Assert.Equal(8m, bucket.TotalCartons);
        Assert.Equal(3m, bucket.TotalWeight);
        Assert.Equal(2m, bucket.TotalVolume);
    }

    [Fact]
    public void 客户数_去重正数_无效身份单独计数并给出原因()
    {
        var lists = new[]
        {
            List(1, new DateTime(2026, 9, 10), "TCLU-001", 101),
            List(2, new DateTime(2026, 9, 10), "TCLU-001", 101),
            List(3, new DateTime(2026, 9, 10), "TCLU-001", 102),
            List(4, new DateTime(2026, 9, 10), "TCLU-001", 0),
            List(5, new DateTime(2026, 9, 10), "TCLU-001", -3),
        };

        var bucket = Assert.Single(ContainerStatsEvidenceRules.BuildBuckets(lists));

        Assert.Equal(2, bucket.AuthorizedCustomerCount);
        Assert.Equal(2, bucket.InvalidCustomerCount);
        Assert.Equal(ContainerStatsEvidenceRules.InvalidCustomerReason, bucket.InvalidCustomerReason);
    }

    [Fact]
    public void 无无效身份_客户无效原因为空()
    {
        var bucket = Assert.Single(ContainerStatsEvidenceRules.BuildBuckets(
            new[] { List(1, new DateTime(2026, 9, 10), "TCLU-001", 101) }));

        Assert.Equal(0, bucket.InvalidCustomerCount);
        Assert.Equal(string.Empty, bucket.InvalidCustomerReason);
    }

    [Fact]
    public void 装载率与柜型恒为未知_且附依据标签()
    {
        var bucket = Assert.Single(ContainerStatsEvidenceRules.BuildBuckets(
            new[] { List(1, new DateTime(2026, 9, 10), "TCLU-001", 101, cartons: 100m) }));

        Assert.Null(bucket.Utilization);
        Assert.Equal(ContainerStatsEvidenceRules.UtilizationUnknownReason, bucket.UtilizationReason);
        Assert.Equal(ContainerStatsEvidenceRules.UnknownType, bucket.TypeText);
        Assert.Equal(ContainerStatsEvidenceRules.TypeUnknownReason, bucket.TypeReason);
        Assert.Equal(ContainerStatsEvidenceRules.SourceLabel, bucket.SourceLabel);
        Assert.Equal(ContainerStatsEvidenceRules.CoverageLabel, bucket.CoverageLabel);
        Assert.Equal(ContainerStatsEvidenceRules.GroupingLabel, bucket.GroupingLabel);
        Assert.Equal(ContainerStatsEvidenceRules.CapacityLabel, bucket.CapacityLabel);
        Assert.Equal(ContainerStatsEvidenceRules.TypeLabel, bucket.TypeLabel);
        Assert.Equal(ContainerStatsEvidenceRules.QuantityLabel, bucket.QuantityLabel);
    }

    [Fact]
    public void 桶顺序_按装柜日期再按分组键稳定排序()
    {
        var lists = new[]
        {
            List(2, new DateTime(2026, 9, 11), "AAA", 101),
            List(1, new DateTime(2026, 9, 10), "ZZZ", 101),
            List(3, new DateTime(2026, 9, 10), "AAA", 101),
        };

        var buckets = ContainerStatsEvidenceRules.BuildBuckets(lists);

        Assert.Equal(3, buckets.Count);
        Assert.Equal(new DateTime(2026, 9, 10), buckets[0].LoadingDate);
        Assert.Equal("AAA", buckets[0].ContainerNo);
        Assert.Equal("ZZZ", buckets[1].ContainerNo);
        Assert.Equal(new DateTime(2026, 9, 11), buckets[2].LoadingDate);
    }

}
