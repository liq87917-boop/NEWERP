using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore.Storage;

namespace ERP.Infrastructure.Reports;

/// <summary>
/// 通用报表配置平台（ERP-273 Stage 1）的关系型有界一致只读快照实现：
/// 持有同一只读 Serializable 事务内的有界匹配身份键与证据行，事务通过 <see cref="CompleteAsync"/>
/// 完成、通过 <see cref="DisposeAsync"/> 释放（幂等）；快照只在单次请求内有效，绝不跨请求 / 用户缓存。
/// <para>ERP-319：除 EF Core 的 <see cref="IDbContextTransaction"/> 外，还支持旧单据导出读取接缝的
/// 原生 <see cref="SqlConnection"/> + <see cref="SqlTransaction"/>（Serializable）只读事务。</para>
/// </summary>
public sealed class ReportConfigurationReadSnapshot : IReportConfigurationReadSnapshot
{
    private readonly IReadSnapshotTransaction? _transaction;
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
        : this(
            correlationId, datasetKey, coverage, matchedCount, factIds, rows, columns, evidence,
            scopeFingerprint, estimatedBytes, isConsistent,
            transaction is null ? null : new EfReadSnapshotTransaction(transaction))
    {
    }

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
        SqlConnection? connection,
        SqlTransaction? transaction)
        : this(
            correlationId, datasetKey, coverage, matchedCount, factIds, rows, columns, evidence,
            scopeFingerprint, estimatedBytes, isConsistent,
            connection is null || transaction is null ? null : new SqlReadSnapshotTransaction(connection, transaction))
    {
    }

    private ReportConfigurationReadSnapshot(
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
        IReadSnapshotTransaction? transaction)
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

    private interface IReadSnapshotTransaction
    {
        Task CommitAsync(CancellationToken cancellationToken);
        Task RollbackAsync(CancellationToken cancellationToken);
        ValueTask DisposeAsync();
    }

    private sealed class EfReadSnapshotTransaction : IReadSnapshotTransaction
    {
        private readonly IDbContextTransaction _transaction;

        public EfReadSnapshotTransaction(IDbContextTransaction transaction) => _transaction = transaction;

        public Task CommitAsync(CancellationToken cancellationToken) => _transaction.CommitAsync(cancellationToken);
        public Task RollbackAsync(CancellationToken cancellationToken) => _transaction.RollbackAsync(cancellationToken);
        public ValueTask DisposeAsync() => _transaction.DisposeAsync();
    }

    private sealed class SqlReadSnapshotTransaction : IReadSnapshotTransaction
    {
        private readonly SqlConnection _connection;
        private readonly SqlTransaction _transaction;

        public SqlReadSnapshotTransaction(SqlConnection connection, SqlTransaction transaction)
        {
            _connection = connection;
            _transaction = transaction;
        }

        public Task CommitAsync(CancellationToken cancellationToken) => _transaction.CommitAsync(cancellationToken);
        public Task RollbackAsync(CancellationToken cancellationToken) => _transaction.RollbackAsync(cancellationToken);

        public async ValueTask DisposeAsync()
        {
            try
            {
                await _transaction.DisposeAsync();
            }
            catch
            {
                // 尽力释放；只读快照无写入，绝不因此影响主流程。
            }

            await _connection.DisposeAsync();
        }
    }
}
