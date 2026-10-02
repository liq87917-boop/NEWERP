using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using Microsoft.EntityFrameworkCore.Storage;

namespace ERP.Infrastructure.Reports;

/// <summary>
/// 通用报表配置平台（ERP-273 Stage 1）的关系型有界一致只读快照实现：
/// 持有同一只读 Serializable 事务内的有界匹配身份键与证据行，事务通过 <see cref="CompleteAsync"/>
/// 完成、通过 <see cref="DisposeAsync"/> 释放（幂等）；快照只在单次请求内有效，绝不跨请求 / 用户缓存。
/// </summary>
public sealed class ReportConfigurationReadSnapshot : IReportConfigurationReadSnapshot
{
    private readonly IDbContextTransaction? _transaction;
    private int _completed;
    private int _disposed;

    public ReportConfigurationReadSnapshot(
        string correlationId,
        string datasetKey,
        string coverage,
        int matchedCount,
        IReadOnlyList<long> factIds,
        IReadOnlyList<Dictionary<string, object?>> rows,
        IReadOnlyList<ReportConfigurationColumnDto> columns,
        ReportConfigurationEvidenceContextDto evidence,
        string scopeFingerprint,
        long estimatedBytes,
        bool isConsistent,
        IDbContextTransaction? transaction)
    {
        CorrelationId = correlationId;
        DatasetKey = datasetKey;
        Coverage = coverage;
        MatchedCount = matchedCount;
        FactIds = factIds;
        Rows = rows;
        Columns = columns;
        Evidence = evidence;
        ScopeFingerprint = scopeFingerprint;
        EstimatedBytes = estimatedBytes;
        IsConsistent = isConsistent;
        _transaction = transaction;
    }

    public string CorrelationId { get; }

    public string DatasetKey { get; }

    public string Coverage { get; }

    public int MatchedCount { get; }

    public long EstimatedBytes { get; }

    public bool IsConsistent { get; }

    public IReadOnlyList<long> FactIds { get; }

    public IReadOnlyList<Dictionary<string, object?>> Rows { get; }

    public IReadOnlyList<ReportConfigurationColumnDto> Columns { get; }

    public ReportConfigurationEvidenceContextDto Evidence { get; }

    public string ScopeFingerprint { get; }

    /// <summary>完成只读事务（幂等；只读快照无写入，提交即释放一致视图）。</summary>
    public async Task CompleteAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _completed, 1) != 0)
            return;

        if (_transaction is not null)
            await _transaction.CommitAsync(cancellationToken);
    }

    /// <summary>释放事务（幂等；未完成时回滚，只读无写入故等价于丢弃一致视图）。</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        if (_transaction is null)
            return;

        try
        {
            if (Volatile.Read(ref _completed) == 0)
                await _transaction.RollbackAsync(CancellationToken.None);
        }
        catch
        {
            // 回滚尽力而为；只读快照无写入，绝不因此影响主流程。
        }
        finally
        {
            await _transaction.DisposeAsync();
        }
    }
}
