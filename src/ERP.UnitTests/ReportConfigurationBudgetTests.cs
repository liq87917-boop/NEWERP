using ERP.Application.Common;
using ERP.Application.Services;
using System.Collections;
using System.Reflection;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-269 服务端执行预算单元测试（纯离线，无数据库 / SQL / 浏览器）：
/// 覆盖平台常量、每用户并发上限、无全局拒绝、槽位释放、成功 / 异常 / 调用方取消 / 超时（含非合作工作保留槽位）、
/// 以及可注入假时钟驱动的空闲用户条目驱逐。
/// </summary>
public class ReportConfigurationBudgetTests
{
    private const long UserA = 7001;
    private const long UserB = 7002;

    [Fact]
    public void 平台常量_固定边界与失败码()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), ReportConfigurationExecutionLimits.OperationTimeout);
        Assert.Equal(2, ReportConfigurationExecutionLimits.MaxConcurrentPerUser);
        Assert.Equal(200, ReportConfigurationExecutionLimits.MaxPreviewRows);
        Assert.Equal(32, ReportConfigurationExecutionLimits.MaxPreviewColumns);
        Assert.Equal(1024 * 1024, ReportConfigurationExecutionLimits.MaxSerializedPreviewBytes);
        Assert.Equal(10 * 1024 * 1024, ReportConfigurationExecutionLimits.MaxGeneratedFileBytes);
    }

    [Fact]
    public void Acquire_每用户最多两个_第三个繁忙()
    {
        var budget = new ReportConfigurationExecutionBudget();

        using var first = budget.Acquire(UserA);
        using var second = budget.Acquire(UserA);

        var ex = Assert.Throws<BusinessException>(() => budget.Acquire(UserA));
        Assert.Equal(ReportConfigurationExecutionLimits.ErrorCodeBusy, ex.Code);
        Assert.Contains("关联ID", ex.Message);
    }

    [Fact]
    public void Acquire_不同用户互不影响_无全局拒绝()
    {
        var budget = new ReportConfigurationExecutionBudget();

        using var a1 = budget.Acquire(UserA);
        using var a2 = budget.Acquire(UserA);
        using var b1 = budget.Acquire(UserB);
        using var b2 = budget.Acquire(UserB);
    }

    [Fact]
    public void Acquire_释放后槽位可复用()
    {
        var budget = new ReportConfigurationExecutionBudget();

        var first = budget.Acquire(UserA);
        using var second = budget.Acquire(UserA);

        first.Dispose();          // 释放一个槽位
        using var third = budget.Acquire(UserA);   // 现在可再次获取
    }

    [Fact]
    public async Task ExecuteAsync_成功_透传结果并释放槽位()
    {
        var budget = new ReportConfigurationExecutionBudget();
        var result = await budget.ExecuteAsync(UserA, CancellationToken.None, _ => Task.FromResult(42));
        Assert.Equal(42, result);

        using var first = budget.Acquire(UserA);
        using var second = budget.Acquire(UserA);
    }

    [Fact]
    public async Task ExecuteAsync_工作抛业务异常_透传并释放槽位()
    {
        var budget = new ReportConfigurationExecutionBudget();

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            budget.ExecuteAsync<int>(UserA, CancellationToken.None,
                _ => Task.FromException<int>(BusinessException.NotFound("不存在"))));
        Assert.Equal(ErrorCodes.NotFound, ex.Code);

        using var first = budget.Acquire(UserA);
        using var second = budget.Acquire(UserA);
    }

    [Fact]
    public async Task ExecuteAsync_非合作工作超时_返回超时且保留槽位直到结束()
    {
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var budget = new ReportConfigurationExecutionBudget(operationTimeout: TimeSpan.FromMilliseconds(50));

        var pending = budget.ExecuteAsync(UserA, CancellationToken.None, async _ =>
        {
            await gate.Task;   // 非合作：忽略取消令牌，一直阻塞
            return 42;
        });

        var ex = await Assert.ThrowsAsync<BusinessException>(() => pending);
        Assert.Equal(ReportConfigurationExecutionLimits.ErrorCodeTimeout, ex.Code);
        Assert.Contains("关联ID", ex.Message);

        // 底层工作仍未结束：槽位必须保留（只能再取 1 个，第 3 个仍繁忙）
        using var second = budget.Acquire(UserA);
        Assert.Throws<BusinessException>(() => budget.Acquire(UserA));

        // 真正结束后释放保留槽位
        gate.TrySetResult(true);
        await Task.Delay(100);

        second.Dispose();
        using var firstAgain = budget.Acquire(UserA);
        using var secondAgain = budget.Acquire(UserA);
    }

    [Fact]
    public async Task ExecuteAsync_调用方取消_返回取消()
    {
        using var caller = new CancellationTokenSource();
        var budget = new ReportConfigurationExecutionBudget(operationTimeout: TimeSpan.FromSeconds(30));

        var pending = budget.ExecuteAsync(UserA, caller.Token, async lease =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, lease.Token);   // 合作：观察联动令牌
            return 42;
        });

        caller.Cancel();
        var ex = await Assert.ThrowsAsync<BusinessException>(() => pending);
        Assert.Equal(ReportConfigurationExecutionLimits.ErrorCodeCancelled, ex.Code);
        Assert.Contains("关联ID", ex.Message);
    }

    [Fact]
    public void 空闲用户条目_过期后被驱逐()
    {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var budget = new ReportConfigurationExecutionBudget(
            idleEvictionAfter: TimeSpan.FromMinutes(5),
            clock: () => now);

        using (var lease = budget.Acquire(UserA)) { }   // 释放后空闲
        Assert.Equal(1, UserCount(budget));

        now = now.AddMinutes(6);                         // 超过空闲阈值

        using var other = budget.Acquire(UserB);         // 触发惰性驱逐
        Assert.Equal(1, UserCount(budget));              // UserA 已被驱逐，仅剩 UserB
    }

    private static int UserCount(ReportConfigurationExecutionBudget budget)
    {
        var field = typeof(ReportConfigurationExecutionBudget)
            .GetField("_users", BindingFlags.NonPublic | BindingFlags.Instance);
        var dictionary = (IDictionary)field!.GetValue(budget)!;
        return dictionary.Count;
    }
}
