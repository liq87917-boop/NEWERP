using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using System.Data;
using System.Globalization;

namespace ERP.Infrastructure.Reports;

/// <summary>
/// 通用报表配置平台（ERP-335 Stage 2）的关系型组合一致读取作用域工厂实现：
/// 在既有作用域 DbContext 上获取一个 <c>Snapshot</c> 隔离的只读事务，把表头 / 明细两次有界读取与最终授权复核
/// 包在同一个一致快照内，避免两次独立读取之间源数据发生变化而产出混合表头 / 明细结果。
/// <para>后端 / 隔离级别不支持时显式抛 environment-blocked（fail closed），绝不静默回退为无保护多读；
/// 不新增表 / 列、不执行任意 SQL / 联接、不扩大写入或业务权限。</para>
/// </summary>
public sealed class ReportConfigurationCompositionReadScopeFactory : IReportConfigurationCompositionReadScopeFactory
{
    private readonly IErpDbContext _db;
    private readonly IReadOnlyList<IReportConfigurationDatasetProvider> _providers;

    public ReportConfigurationCompositionReadScopeFactory(
        IErpDbContext db,
        IEnumerable<IReportConfigurationDatasetProvider> providers)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _providers = (providers ?? Array.Empty<IReportConfigurationDatasetProvider>())
            .OrderBy(p => p.DatasetKey, StringComparer.Ordinal)
            .ToList();
    }

    /// <inheritdoc />
    public async Task<IReportConfigurationCompositionReadScope> OpenAsync(
        long ownerUserId,
        string correlationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(correlationId);
        if (ownerUserId <= 0)
            throw new BusinessException("请先登录后再执行组合读取", ErrorCodes.Unauthorized);

        // 非关系型后端（如内存库）无法提供一致只读事务：显式 environment-blocked，绝不静默回退。
        if (!string.Equals(_db.Database.ProviderName, "Microsoft.EntityFrameworkCore.SqlServer",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new BusinessException(
                "当前环境不支持组合一致读取所需的 SQL Server 后端（environment-blocked）",
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
                "当前环境不支持组合一致读取所需的快照隔离（environment-blocked）",
                ReportConfigurationExecutionLimits.ErrorCodeEnvironmentUnsupported);
        }

        // 在一致快照内捕获数据范围指纹；后续读取与最终复核都在同一快照内，绝不混入跨快照来源。
        var scopeFingerprint = ComputeScopeFingerprint(
            await SalespersonDataScopeService.ResolveAsync(_db, ownerUserId));

        return new RelationalCompositionReadScope(correlationId, transaction, _db, _providers, scopeFingerprint);
    }

    private static string ComputeScopeFingerprint(SalespersonDataScope scope)
        => scope.IsPrivileged
            ? "privileged"
            : (scope.SalesmanId?.ToString(CultureInfo.InvariantCulture) ?? "none")
              + "|" + string.Join(",", (scope.AllowedCustomerIds ?? new HashSet<long>()).OrderBy(x => x));

    private sealed class RelationalCompositionReadScope : IReportConfigurationCompositionReadScope
    {
        private readonly IDbContextTransaction _transaction;
        private readonly IErpDbContext _db;
        private readonly IReadOnlyList<IReportConfigurationDatasetProvider> _providers;
        private readonly string _scopeFingerprint;
        private int _completed;
        private int _disposed;

        public RelationalCompositionReadScope(
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
            IReadOnlyList<ReportConfigurationCompositionReadTarget> targets,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(targets);
            if (targets.Count == 0)
                throw BusinessException.InvalidParameter("组合读取目标不能为空");

            // 1) 新鲜菜单复核（fail closed）
            foreach (var target in targets)
            {
                var provider = _providers.FirstOrDefault(p =>
                    string.Equals(p.DatasetKey, target.DatasetKey, StringComparison.OrdinalIgnoreCase))
                    ?? throw new BusinessException($"未知数据集: {target.DatasetKey}", ErrorCodes.InvalidParameter);

                if (await provider.GetDatasetAsync(ownerUserId, cancellationToken) is null)
                    throw new BusinessException(
                        $"当前账号没有「{target.DatasetKey}」数据集授权：拒绝组合（fail closed）",
                        ErrorCodes.Forbidden);
            }

            // 2) 新鲜数据范围复核（变更即拒绝）
            var scope = await SalespersonDataScopeService.ResolveAsync(_db, ownerUserId);
            if (!string.Equals(ComputeScopeFingerprint(scope), _scopeFingerprint, StringComparison.Ordinal))
                throw new BusinessException("数据范围已变更，本次组合被拒绝（fail closed）", ErrorCodes.Forbidden);

            // 3) 归属 / 共享授权复核（撤销即拒绝）
            foreach (var target in targets)
            {
                var owned = await _db.ReportConfigurations.AnyAsync(
                    c => c.Id == target.ConfigurationId && !c.IsDeleted && c.OwnerUserId == ownerUserId,
                    cancellationToken);
                if (owned)
                    continue;

                var granted = await _db.ReportConfigurationGrants.AnyAsync(
                    g => g.ReportConfigurationId == target.ConfigurationId
                        && g.RecipientUserId == ownerUserId && !g.IsDeleted,
                    cancellationToken);
                if (!granted)
                    throw BusinessException.NotFound("报表配置不存在或无权访问");
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
    }
}
