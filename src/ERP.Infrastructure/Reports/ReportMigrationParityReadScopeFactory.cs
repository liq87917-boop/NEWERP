using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using System.Data;
using System.Globalization;

namespace ERP.Infrastructure.Reports;

/// <summary>
/// 迁移 parity 三方一致读取作用域工厂实现（ERP-338 Stage 2）：
/// 在既有作用域 DbContext 上获取一个 <c>Snapshot</c> 隔离的只读事务，把旧来源快照 / 通用预览 / 实际旧产物
/// 三次有界读取与最终授权复核包在同一个一致快照内，避免三次独立读取之间源数据发生变化而产出混合证据。
/// <para>后端 / 隔离级别不支持或存在嵌套事务时显式抛 environment-blocked（fail closed），绝不静默回退为无保护多读；
/// 不新增表 / 列、不执行任意 SQL / 联接、不扩大写入或业务权限。</para>
/// </summary>
public sealed class ReportMigrationParityReadScopeFactory : IReportMigrationParityReadScopeFactory
{
    private readonly IErpDbContext _db;
    private readonly IReadOnlyList<IReportConfigurationDatasetProvider> _providers;

    public ReportMigrationParityReadScopeFactory(
        IErpDbContext db,
        IEnumerable<IReportConfigurationDatasetProvider> providers)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _providers = (providers ?? Array.Empty<IReportConfigurationDatasetProvider>())
            .OrderBy(p => p.DatasetKey, StringComparer.Ordinal)
            .ToList();
    }

    /// <inheritdoc />
    public async Task<IReportMigrationParityReadScope> OpenAsync(
        long ownerUserId,
        string correlationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(correlationId);
        if (ownerUserId <= 0)
            throw new BusinessException("请先登录后再执行三方一致读取", ErrorCodes.Unauthorized);

        // 非关系型后端（如内存库）无法提供一致只读事务：显式 environment-blocked，绝不静默回退。
        if (!string.Equals(_db.Database.ProviderName, "Microsoft.EntityFrameworkCore.SqlServer",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new BusinessException(
                "当前环境不支持三方一致读取所需的 SQL Server 后端（environment-blocked）",
                ReportConfigurationExecutionLimits.ErrorCodeEnvironmentUnsupported);
        }

        // 已存在环境事务（嵌套事务）无法再开启一层一致快照：显式 environment-blocked。
        if (_db.Database.CurrentTransaction is not null)
        {
            throw new BusinessException(
                "当前环境不支持嵌套三方一致读取事务（environment-blocked）",
                ReportConfigurationExecutionLimits.ErrorCodeEnvironmentUnsupported);
        }

        IDbContextTransaction transaction;
        try
        {
            transaction = await _db.Database.BeginTransactionAsync(IsolationLevel.Snapshot, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new BusinessException(
                "当前环境不支持三方一致读取所需的快照隔离（environment-blocked）",
                ReportConfigurationExecutionLimits.ErrorCodeEnvironmentUnsupported);
        }

        // 在一致快照内捕获数据范围指纹；后续读取与最终复核都在同一快照内，绝不混入跨快照来源。
        var scopeFingerprint = ComputeScopeFingerprint(
            await SalespersonDataScopeService.ResolveAsync(_db, ownerUserId));

        return new RelationalParityReadScope(correlationId, transaction, _db, _providers, scopeFingerprint);
    }

    private static string ComputeScopeFingerprint(SalespersonDataScope scope)
        => scope.IsPrivileged
            ? "privileged"
            : (scope.SalesmanId?.ToString(CultureInfo.InvariantCulture) ?? "none")
              + "|" + string.Join(",", (scope.AllowedCustomerIds ?? new HashSet<long>()).OrderBy(x => x));

    private sealed class RelationalParityReadScope : IReportMigrationParityReadScope
    {
        private readonly IDbContextTransaction _transaction;
        private readonly IErpDbContext _db;
        private readonly IReadOnlyList<IReportConfigurationDatasetProvider> _providers;
        private readonly string _scopeFingerprint;
        private int _completed;
        private int _disposed;

        public RelationalParityReadScope(
            string correlationId,
            IDbContextTransaction transaction,
            IErpDbContext db,
            IReadOnlyList<IReportConfigurationDatasetProvider> providers,
            string scopeFingerprint)
        {
            CorrelationId = correlationId;
            _transaction = transaction;
            _db = db;
            _providers = providers;
            _scopeFingerprint = scopeFingerprint;
        }

        public string CorrelationId { get; }

        public bool IsConsistent => true;

        public async Task RecheckAsync(
            long ownerUserId,
            ReportMigrationParityReadTarget target,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(target);
            if (string.IsNullOrWhiteSpace(target.LegacyKey) || string.IsNullOrWhiteSpace(target.DatasetKey))
                throw BusinessException.InvalidParameter("三方一致读取目标不完整");

            // 已经物化的三方读取结果来自同一个快照。结束该只读事务后再做授权查询，
            // 使读取期间提交的撤销可见；在 Snapshot 内复核会复用陈旧授权。
            cancellationToken.ThrowIfCancellationRequested();
            await CompleteAsync(cancellationToken);
            await DisposeAsync();

            // 1) 新鲜菜单复核（旧报表所需既有菜单编码，fail closed）
            var authorizedMenus = await CustomerReceivableReconciliationService.LoadAuthorizedMenuCodesAsync(
                _db, ownerUserId);
            if (!IsMenuAuthorized(target.RequiredMenuCodes, authorizedMenus))
            {
                throw new BusinessException(
                    $"当前账号已不再具备「{target.LegacyKey}」的菜单授权：拒绝三方一致读取（fail closed）",
                    ErrorCodes.Forbidden);
            }

            // 2) 新鲜数据集授权复核（无对应菜单授权即拒绝）
            var provider = _providers.FirstOrDefault(p =>
                string.Equals(p.DatasetKey, target.DatasetKey, StringComparison.OrdinalIgnoreCase))
                ?? throw new BusinessException($"未知数据集: {target.DatasetKey}", ErrorCodes.InvalidParameter);

            if (await provider.GetDatasetAsync(ownerUserId, cancellationToken) is null)
            {
                throw new BusinessException(
                    $"当前账号没有「{target.DatasetKey}」数据集授权：拒绝三方一致读取（fail closed）",
                    ErrorCodes.Forbidden);
            }

            // 3) 新鲜数据范围复核（变更即拒绝）
            var scope = await SalespersonDataScopeService.ResolveAsync(_db, ownerUserId);
            if (!string.Equals(ComputeScopeFingerprint(scope), _scopeFingerprint, StringComparison.Ordinal))
            {
                throw new BusinessException(
                    "数据范围已变更，本次三方一致读取被拒绝（fail closed）",
                    ErrorCodes.Forbidden);
            }
        }

        public async Task CompleteAsync(CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _completed, 1) != 0)
                return;

            await _transaction.CommitAsync(cancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            try
            {
                if (Volatile.Read(ref _completed) == 0)
                    await _transaction.RollbackAsync(CancellationToken.None);
            }
            catch
            {
                // 回滚尽力而为；只读事务无写入，绝不因此影响主流程。
            }
            finally
            {
                await _transaction.DisposeAsync();
            }
        }

        private static bool IsMenuAuthorized(
            IReadOnlyList<string> requiredMenuCodes, HashSet<string> authorizedMenuCodes)
        {
            if (requiredMenuCodes.Count == 0)
                return false;

            foreach (var code in requiredMenuCodes)
            {
                if (!authorizedMenuCodes.Contains(code))
                    return false;
            }

            return true;
        }

        private static string ComputeScopeFingerprint(SalespersonDataScope scope)
            => scope.IsPrivileged
                ? "privileged"
                : (scope.SalesmanId?.ToString(CultureInfo.InvariantCulture) ?? "none")
                  + "|" + string.Join(",", (scope.AllowedCustomerIds ?? new HashSet<long>()).OrderBy(x => x));
    }
}
