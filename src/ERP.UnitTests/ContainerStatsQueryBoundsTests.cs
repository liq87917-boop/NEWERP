using ERP.Application.Common;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// 柜量与装柜利用率统计（ERP-251）日期窗口 / 有界读取 / 稳定排序单元测试：
/// 日期校验（含溢出防护）先于任何源读取；结束日按排他上界（&lt; 结束日次日）比较；
/// 装柜清单头稳定排序后做 501 行来源探测（500 张上限），超出即 fail closed；
/// 只读取一次装柜清单头数据集，无逐行查库、只读不写库。
/// <para>全部使用内存数据库（TestDbFactory），不连接 SQL Server、不启动 API、不执行任何 SQL / seed。</para>
/// </summary>
public class ContainerStatsQueryBoundsTests
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

    private static ContainerLoadingList SeedList(
        ErpDbContext db, string loadingListNo, long customerId, string containerNo,
        DateTime? loadingDate = null, DocumentStatus status = DocumentStatus.Approved, bool deleted = false)
    {
        var list = new ContainerLoadingList
        {
            LoadingListNo = loadingListNo,
            LoadingDate = loadingDate ?? new DateTime(2026, 9, 10),
            ContainerNo = containerNo,
            CustomerId = customerId,
            Status = status,
            IsDeleted = deleted,
            TotalCartons = 1m,
            TotalWeight = 2m,
            TotalVolume = 3m
        };
        db.ContainerLoadingLists.Add(list);
        db.SaveChanges();
        return list;
    }

    // ==================== 日期校验（先于任何源读取） ====================

    [Fact]
    public async Task 结束日期早于开始日期_拒绝()
    {
        using var db = TestDbFactory.Create();
        var service = new ReportService(db);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            service.GetContainerStatsAsync(new DateTime(2026, 9, 30), new DateTime(2026, 9, 1), PrivilegedScope));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public async Task 日期范围超过366天_拒绝()
    {
        using var db = TestDbFactory.Create();
        var service = new ReportService(db);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            service.GetContainerStatsAsync(new DateTime(2026, 1, 1), new DateTime(2027, 1, 2), PrivilegedScope));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public async Task 日期溢出_结束日为最大日期_拒绝而非运行时溢出()
    {
        using var db = TestDbFactory.Create();
        var service = new ReportService(db);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            service.GetContainerStatsAsync(new DateTime(2026, 1, 1), DateTime.MaxValue, PrivilegedScope));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public async Task 日期范围恰为366天_正常通过()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "客户");
        SeedList(db, "LL-366", customer.Id, "TCLU-001", new DateTime(2026, 1, 1));

        var service = new ReportService(db);
        var result = await service.GetContainerStatsAsync(
            new DateTime(2026, 1, 1), new DateTime(2027, 1, 1), PrivilegedScope);

        var row = Assert.Single(result);
        Assert.Equal("TCLU-001", row.ContainerNo);
    }

    // ==================== 结束日排他边界 ====================

    [Fact]
    public async Task 结束日排他边界_整日包含_次日排除()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "客户");
        SeedList(db, "LL-END-0000", customer.Id, "TCLU-A", new DateTime(2026, 9, 30, 0, 0, 0));
        SeedList(db, "LL-END-2359", customer.Id, "TCLU-B", new DateTime(2026, 9, 30, 23, 59, 59));
        SeedList(db, "LL-NEXT", customer.Id, "TCLU-C", new DateTime(2026, 10, 1, 0, 0, 0));

        var service = new ReportService(db);
        var result = await service.GetContainerStatsAsync(Start, End, PrivilegedScope);

        Assert.Equal(2, result.Count);
        Assert.All(result, r => Assert.Equal(new DateTime(2026, 9, 30), r.LoadingDate));
        Assert.DoesNotContain(result, r => r.ContainerNo == "TCLU-C");
    }

    // ==================== 有界读取（501 行探测 500 上限） ====================

    [Fact]
    public async Task 清单数超过500_报告上限错误()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "客户");
        for (var i = 1; i <= 501; i++)
        {
            db.ContainerLoadingLists.Add(new ContainerLoadingList
            {
                LoadingListNo = $"LL-{i:0000}",
                LoadingDate = new DateTime(2026, 9, 10),
                ContainerNo = $"TCLU-{i:0000}",
                CustomerId = customer.Id,
                Status = DocumentStatus.Approved
            });
        }
        db.SaveChanges();

        var service = new ReportService(db);
        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            service.GetContainerStatsAsync(Start, End, PrivilegedScope));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("500", ex.Message);
    }

    // ==================== 固定批量查询 / 只读 ====================

    [Fact]
    public async Task 固定批量查询_数据集读取一次且只读()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db, "C001", "客户");
        SeedList(db, "LL-1", customer.Id, "TCLU-001", new DateTime(2026, 9, 10));
        SeedList(db, "LL-2", customer.Id, "TCLU-002", new DateTime(2026, 9, 11));

        var counting = InventoryMovementReportTests.CountingDbContext.Wrap(db);
        var service = new ReportService(counting.Proxy);
        var result = await service.GetContainerStatsAsync(Start, End, PrivilegedScope);

        Assert.Equal(2, result.Count);
        Assert.Equal(1, counting.DatasetReads);
        Assert.Equal(0, counting.WriteCalls);
    }

}
