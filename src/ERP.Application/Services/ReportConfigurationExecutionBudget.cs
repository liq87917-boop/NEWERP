using ERP.Application.Common;

namespace ERP.Application.Services;

/// <summary>
/// 通用报表配置平台（ERP-269 Stage 1）的服务端执行预算平台常量。
/// <para>这些都是平台常量，客户端不可递增，也不受环境 / 配置影响：每次操作的截止时间（含已保存 / 授权解析、
/// 授权、提供程序查找与渲染）、每用户最大并发执行数、当前预览页行 / 列上限、序列化预览与生成文件体积上限。</para>
/// </summary>
public static class ReportConfigurationExecutionLimits
{
    /// <summary>每次执行操作的固定截止时间（30 秒，含解析 / 授权 / 渲染）</summary>
    public static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(30);

    /// <summary>每个已认证用户的最大同时执行操作数（至少 2，平台常量）</summary>
    public const int MaxConcurrentPerUser = 2;

    /// <summary>当前预览页最大行数</summary>
    public const int MaxPreviewRows = 200;

    /// <summary>当前预览最大可见列数（基础列 + 计算列同属可见上限）</summary>
    public const int MaxPreviewColumns = 32;

    /// <summary>序列化预览结果体积上限（1 MiB）</summary>
    public const int MaxSerializedPreviewBytes = 1024 * 1024;

    /// <summary>生成文件（Excel / PDF）体积上限（10 MiB）</summary>
    public const int MaxGeneratedFileBytes = 10 * 1024 * 1024;

    /// <summary>有界一致只读快照最大事实数（ERP-273，1000；超过即 fail closed，绝不截断冒充足量）</summary>
    public const int MaxSnapshotFacts = 1000;

    /// <summary>有界一致只读快照内部字节上限（8 MiB）</summary>
    public const int MaxSnapshotBytes = 8 * 1024 * 1024;

    // ==================== ERP-269 有界执行失败码（平台常量，客户端不可递增） ====================

    /// <summary>服务繁忙（每用户并发执行达到上限）</summary>
    public const int ErrorCodeBusy = 1005;

    /// <summary>操作超时（服务端截止时间到）</summary>
    public const int ErrorCodeTimeout = 1006;

    /// <summary>调用方取消（请求中断 / 断开）</summary>
    public const int ErrorCodeCancelled = 1007;

    /// <summary>结果过大（行 / 列 / 字节超限）</summary>
    public const int ErrorCodeResultTooLarge = 1008;

    /// <summary>文件渲染失败（Excel / PDF 生成异常，区别于持久化环境不可用）</summary>
    public const int ErrorCodeRenderingFailed = 5001;

    /// <summary>环境不支持（如一致快照事务 / SQL Server 不可用；显式 environment-blocked，绝不静默降级）</summary>
    public const int ErrorCodeEnvironmentUnsupported = 5002;
}

/// <summary>有界执行结果分类（用于结构化日志 outcome 键，仅记录受控取值）。</summary>
public static class ReportConfigurationExecutionOutcomes
{
    public const string Success = "success";
    public const string Timeout = "timeout";
    public const string Busy = "busy";
    public const string Cancelled = "cancelled";
    public const string TooLarge = "too_large";
    public const string Forbidden = "forbidden";
    public const string Unauthorized = "unauthorized";
    public const string NotFound = "not_found";
    public const string Invalid = "invalid";
    public const string Conflict = "conflict";
    public const string Environment = "environment";
    public const string RenderingFailed = "rendering_failed";
    public const string Error = "error";

    /// <summary>把业务错误码映射为受控 outcome 取值（未知码一律 error，绝不泄露细节）。</summary>
    public static string For(int code) => code switch
    {
        ErrorCodes.Success => Success,
        ErrorCodes.InvalidParameter => Invalid,
        ErrorCodes.NotFound => NotFound,
        ErrorCodes.RuleConflict => Conflict,
        ErrorCodes.Forbidden => Forbidden,
        ErrorCodes.Unauthorized => Unauthorized,
        ErrorCodes.TokenExpired => Unauthorized,
        ErrorCodes.AccountDisabled => Unauthorized,
        ErrorCodes.InternalError => Environment,
        ReportConfigurationExecutionLimits.ErrorCodeBusy => Busy,
        ReportConfigurationExecutionLimits.ErrorCodeTimeout => Timeout,
        ReportConfigurationExecutionLimits.ErrorCodeCancelled => Cancelled,
        ReportConfigurationExecutionLimits.ErrorCodeResultTooLarge => TooLarge,
        ReportConfigurationExecutionLimits.ErrorCodeRenderingFailed => RenderingFailed,
        ReportConfigurationExecutionLimits.ErrorCodeEnvironmentUnsupported => Environment,
        _ => Error,
    };
}

/// <summary>一次已获取的执行租约：持有服务端截止时间 + 调用方取消的联动令牌与关联 ID。</summary>
public interface IReportConfigurationExecutionLease : IDisposable
{
    /// <summary>本次执行的关联 ID（用于受控错误响应与追踪，绝不泄露 SQL / 栈 / 私有值）。</summary>
    string CorrelationId { get; }

    /// <summary>联动取消令牌（调用方取消或截止时间到都会触发）。</summary>
    CancellationToken Token { get; }
}

/// <summary>
/// 服务端执行预算（ERP-269 Stage 1）：为每个已认证用户提供有界执行租约（默认最多同时 2 个），
/// 并为每个租约施加固定截止时间；忙时显式拒绝（绝不全局拒绝、绝不无限缓存用户条目）。
/// </summary>
public interface IReportConfigurationExecutionBudget
{
    /// <summary>固定操作截止时间（平台常量）。</summary>
    TimeSpan OperationTimeout { get; }

    /// <summary>
    /// 为指定用户获取一个执行租约；超过每用户并发上限时抛「繁忙」业务异常。
    /// </summary>
    IReportConfigurationExecutionLease Acquire(long userId, CancellationToken callerToken = default);

    /// <summary>
    /// 在单一租约内执行 <paramref name="work"/>（预览 / 导出只获取一次租约，绝不二次获取），
    /// 与调用方取消 / 截止时间竞争；非合作（不观察取消令牌）的操作会保留槽位直到真正结束。
    /// </summary>
    Task<TResult> ExecuteAsync<TResult>(
        long userId,
        CancellationToken callerToken,
        Func<IReportConfigurationExecutionLease, Task<TResult>> work);
}

/// <summary>
/// 服务端执行预算实现：<see cref="IReportConfigurationExecutionBudget"/>。
/// <para>并发计数按用户键控、锁内完成；空闲用户条目按可注入时钟惰性回收（不占用租约时安全驱逐）；
/// 无全局信号量；释放槽位幂等；未完成的非合作操作保留槽位直至底层任务真正结束。</para>
/// </summary>
public sealed class ReportConfigurationExecutionBudget : IReportConfigurationExecutionBudget
{
    /// <summary>空闲用户条目驱逐阈值（平台常量；仅测试通过可注入时钟做确定性断言）。</summary>
    public static readonly TimeSpan IdleEvictionAfter = TimeSpan.FromMinutes(5);

    private readonly object _gate = new();
    private readonly Dictionary<long, UserEntry> _users = new();
    private readonly TimeSpan _operationTimeout;
    private readonly TimeSpan _idleEvictionAfter;
    private readonly Func<DateTimeOffset> _clock;

    public ReportConfigurationExecutionBudget(
        TimeSpan? operationTimeout = null,
        TimeSpan? idleEvictionAfter = null,
        Func<DateTimeOffset>? clock = null)
    {
        _operationTimeout = operationTimeout ?? ReportConfigurationExecutionLimits.OperationTimeout;
        _idleEvictionAfter = idleEvictionAfter ?? IdleEvictionAfter;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <inheritdoc />
    public TimeSpan OperationTimeout => _operationTimeout;

    /// <inheritdoc />
    public IReportConfigurationExecutionLease Acquire(long userId, CancellationToken callerToken = default)
        => AcquireLease(userId, callerToken);

    /// <inheritdoc />
    public async Task<TResult> ExecuteAsync<TResult>(
        long userId,
        CancellationToken callerToken,
        Func<IReportConfigurationExecutionLease, Task<TResult>> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        callerToken.ThrowIfCancellationRequested();

        var lease = AcquireLease(userId, callerToken);
        Task<TResult> workTask;
        try
        {
            workTask = work(lease);
        }
        catch (Exception)
        {
            lease.Dispose();
            throw;
        }

        var callerSignal = CreateCancelledSignal(callerToken);
        var completed = await Task.WhenAny(workTask, callerSignal, lease.DeadlineSignal);

        if (ReferenceEquals(completed, workTask))
        {
            lease.Dispose();
            try
            {
                return await workTask;
            }
            catch (OperationCanceledException)
            {
                throw MapCancellation(callerToken, lease);
            }
        }

        // 截止时间 / 调用方取消先于底层工作完成：底层工作可能仍不合作地在运行，
        // 因此保留槽位直到其真正结束（绝不声称取消已停止底层工作）。
        _ = workTask.ContinueWith(
            static (task, state) =>
            {
                _ = task.Exception; // 观察异常，避免未观察异常
                ((Lease)state!).Dispose();
            },
            lease,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        if (ReferenceEquals(completed, callerSignal))
            throw new BusinessException(
                $"报表执行已取消（关联ID：{lease.CorrelationId}）",
                ReportConfigurationExecutionLimits.ErrorCodeCancelled);

        throw new BusinessException(
            $"报表执行超时，请稍后重试（关联ID：{lease.CorrelationId}）",
            ReportConfigurationExecutionLimits.ErrorCodeTimeout);
    }

    private Lease AcquireLease(long userId, CancellationToken callerToken)
    {
        if (userId <= 0)
            throw new BusinessException("无法获取当前用户信息", ErrorCodes.Unauthorized);

        var now = _clock();
        lock (_gate)
        {
            EvictIdleLocked(now);

            if (!_users.TryGetValue(userId, out var entry))
            {
                entry = new UserEntry { LastActivityUtc = now };
                _users.Add(userId, entry);
            }

            entry.LastActivityUtc = now;

            if (entry.Active.Count >= ReportConfigurationExecutionLimits.MaxConcurrentPerUser)
            {
                throw new BusinessException(
                    "报表执行繁忙，请稍后重试（关联ID：" + Guid.NewGuid().ToString("N") + "）",
                    ReportConfigurationExecutionLimits.ErrorCodeBusy);
            }

            var lease = new Lease(this, userId, callerToken, _operationTimeout);
            entry.Active.Add(lease);
            return lease;
        }
    }

    private void Release(long userId, Lease lease)
    {
        lock (_gate)
        {
            if (_users.TryGetValue(userId, out var entry))
            {
                entry.Active.Remove(lease);
                entry.LastActivityUtc = _clock();
            }
        }
    }

    private void EvictIdleLocked(DateTimeOffset now)
    {
        if (_idleEvictionAfter <= TimeSpan.Zero)
            return;

        var threshold = now - _idleEvictionAfter;
        var idleKeys = new List<long>();
        foreach (var kv in _users)
        {
            if (kv.Value.Active.Count == 0 && kv.Value.LastActivityUtc < threshold)
                idleKeys.Add(kv.Key);
        }

        foreach (var key in idleKeys)
            _users.Remove(key);
    }

    private static Task CreateCancelledSignal(CancellationToken token)
    {
        if (token.IsCancellationRequested)
            return Task.CompletedTask;

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        token.Register(static state => ((TaskCompletionSource<bool>)state!).TrySetResult(true), tcs);
        return tcs.Task;
    }

    private static BusinessException MapCancellation(CancellationToken callerToken, Lease lease)
    {
        if (callerToken.IsCancellationRequested)
            return new BusinessException(
                $"报表执行已取消（关联ID：{lease.CorrelationId}）",
                ReportConfigurationExecutionLimits.ErrorCodeCancelled);

        return new BusinessException(
            $"报表执行超时，请稍后重试（关联ID：{lease.CorrelationId}）",
            ReportConfigurationExecutionLimits.ErrorCodeTimeout);
    }

    private sealed class UserEntry
    {
        public DateTimeOffset LastActivityUtc;
        public readonly List<Lease> Active = new();
    }

    private sealed class Lease : IReportConfigurationExecutionLease
    {
        private readonly ReportConfigurationExecutionBudget _owner;
        private readonly long _userId;
        private readonly CancellationTokenSource _deadlineCts;
        private readonly CancellationTokenSource _linkedCts;
        private readonly TaskCompletionSource<bool> _deadlineSignal =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _disposed;

        public string CorrelationId { get; } = Guid.NewGuid().ToString("N");
        public CancellationToken Token => _linkedCts.Token;
        public Task DeadlineSignal => _deadlineSignal.Task;

        public Lease(
            ReportConfigurationExecutionBudget owner,
            long userId,
            CancellationToken callerToken,
            TimeSpan timeout)
        {
            _owner = owner;
            _userId = userId;
            _deadlineCts = new CancellationTokenSource(timeout);
            _linkedCts = CancellationTokenSource.CreateLinkedTokenSource(callerToken, _deadlineCts.Token);
            _deadlineCts.Token.Register(
                static state => ((TaskCompletionSource<bool>)state!).TrySetResult(true),
                _deadlineSignal);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            try
            {
                _linkedCts.Dispose();
            }
            finally
            {
                _deadlineCts.Dispose();
            }

            _owner.Release(_userId, this);
        }
    }
}
