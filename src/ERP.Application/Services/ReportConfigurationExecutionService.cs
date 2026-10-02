using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace ERP.Application.Services;

/// <summary>
/// 通用报表配置执行服务（ERP-261 Stage 1）实现：加载选定的草稿定义或固定发布修订，按当前账号的
/// 已授权数据集目录重新校验有界定义，并把执行分发到对应 <see cref="IReportConfigurationDatasetProvider"/>。
/// <para>安全口径：所有者只来自服务端认证身份；跨所有者 / 已删除配置一律按不存在处理（fail closed）；
/// 已保存定义本身绝不授予权限；每次预览都重新校验数据集菜单授权与数据范围（目录 + 查询双层重检）。</para>
/// <para>预览参数：页码 / 每页条数 / 分组键可由当前请求覆盖，但都受数据集上限与支持分组键约束；
/// 分组键不支持组合（Stage 1 只支持单一分组），不支持时显式拒绝而非假装执行。</para>
/// </summary>
public sealed class ReportConfigurationExecutionService : IReportConfigurationExecutionService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly IErpDbContext _db;
    private readonly IReadOnlyList<IReportConfigurationDatasetProvider> _providers;
    private readonly IReportConfigurationRelationResolver? _relationResolver;
    private readonly IReportConfigurationExecutionBudget _budget;
    private readonly ILogger<ReportConfigurationExecutionService>? _logger;

    public ReportConfigurationExecutionService(
        IErpDbContext db,
        IEnumerable<IReportConfigurationDatasetProvider> providers,
        IReportConfigurationRelationResolver? relationResolver = null,
        IReportConfigurationExecutionBudget? budget = null,
        ILogger<ReportConfigurationExecutionService>? logger = null)
    {
        _db = db;
        _providers = (providers ?? Array.Empty<IReportConfigurationDatasetProvider>())
            .OrderBy(p => p.DatasetKey, StringComparer.Ordinal)
            .ToList();
        _relationResolver = relationResolver;
        _budget = budget ?? new ReportConfigurationExecutionBudget();
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<ReportConfigurationPreviewDto> PreviewAsync(
        long ownerUserId,
        ReportConfigurationPreviewRequest request,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        ReportConfigurationPreviewDto? preview = null;
        try
        {
            preview = await _budget.ExecuteAsync(
                ownerUserId,
                cancellationToken,
                lease => PreviewWithinLeaseCoreAsync(ownerUserId, request, lease));

            LogExecution(ownerUserId, request, preview, ReportConfigurationExecutionOutcomes.Success, stopwatch.ElapsedMilliseconds);
            return preview;
        }
        catch (BusinessException ex)
        {
            LogExecution(ownerUserId, request, preview, ReportConfigurationExecutionOutcomes.For(ex.Code), stopwatch.ElapsedMilliseconds);
            throw;
        }
        catch (OperationCanceledException)
        {
            LogExecution(ownerUserId, request, preview, ReportConfigurationExecutionOutcomes.Cancelled, stopwatch.ElapsedMilliseconds);
            throw;
        }
        catch (Exception)
        {
            LogExecution(ownerUserId, request, preview, ReportConfigurationExecutionOutcomes.Error, stopwatch.ElapsedMilliseconds);
            throw;
        }
    }

    /// <summary>在已获取的执行租约内预览（导出复用同一租约，共享截止时间，绝不二次获取租约）。</summary>
    public async Task<ReportConfigurationPreviewDto> PreviewAsync(
        long ownerUserId,
        ReportConfigurationPreviewRequest request,
        IReportConfigurationExecutionLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        return await PreviewWithinLeaseCoreAsync(ownerUserId, request, lease);
    }

    private async Task<ReportConfigurationPreviewDto> PreviewWithinLeaseCoreAsync(
        long ownerUserId,
        ReportConfigurationPreviewRequest request,
        IReportConfigurationExecutionLease lease)
    {
        var preview = await PreviewResolvedAsync(ownerUserId, request, lease.Token, lease.CorrelationId);
        CheckPreviewBounds(preview, lease.CorrelationId);
        return preview;
    }

    private async Task<ReportConfigurationPreviewDto> PreviewResolvedAsync(
        long ownerUserId,
        ReportConfigurationPreviewRequest request,
        CancellationToken cancellationToken,
        string correlationId)
    {
        EnsureAuthenticated(ownerUserId);
        ArgumentNullException.ThrowIfNull(request);
        if (request.ConfigurationId <= 0)
            throw BusinessException.InvalidParameter("请选择要预览的报表配置");

        // 1) 优先按所有者解析（跨所有者不影响共享判定；无授权仍 fail closed）
        var config = await _db.ReportConfigurations
            .FirstOrDefaultAsync(c => c.Id == request.ConfigurationId && !c.IsDeleted && c.OwnerUserId == ownerUserId,
                cancellationToken);

        if (config is not null)
            return await PreviewOwnedAsync(ownerUserId, request, config, cancellationToken, correlationId);

        // 2) 否则按被授权人共享解析（每次重新校验授权与固定修订，绝不暴露草稿 / 其它修订 / 历史）
        return await PreviewSharedAsync(ownerUserId, request, cancellationToken, correlationId);
    }

    private static void CheckPreviewBounds(ReportConfigurationPreviewDto preview, string correlationId)
    {
        if (preview is null)
            return;

        if (preview.Columns is { Count: > ReportConfigurationExecutionLimits.MaxPreviewColumns })
            throw new BusinessException(
                $"报表结果列数超出上限（{preview.Columns.Count} > {ReportConfigurationExecutionLimits.MaxPreviewColumns}），请减少所选字段（关联ID：{correlationId}）",
                ReportConfigurationExecutionLimits.ErrorCodeResultTooLarge);

        if (preview.Rows is { Count: > ReportConfigurationExecutionLimits.MaxPreviewRows })
            throw new BusinessException(
                $"报表结果行数超出上限（{preview.Rows.Count} > {ReportConfigurationExecutionLimits.MaxPreviewRows}），请缩小筛选范围（关联ID：{correlationId}）",
                ReportConfigurationExecutionLimits.ErrorCodeResultTooLarge);

        if (SerializePreviewBytes(preview) > ReportConfigurationExecutionLimits.MaxSerializedPreviewBytes)
            throw new BusinessException(
                "报表结果过大，已拒绝返回，请减少所选字段或缩小筛选范围（关联ID：" + correlationId + "）",
                ReportConfigurationExecutionLimits.ErrorCodeResultTooLarge);
    }

    private static long SerializePreviewBytes(ReportConfigurationPreviewDto preview)
        => JsonSerializer.SerializeToUtf8Bytes(preview, JsonOptions).LongLength;

    private void LogExecution(
        long userId,
        ReportConfigurationPreviewRequest request,
        ReportConfigurationPreviewDto? preview,
        string outcome,
        long durationMs)
    {
        if (_logger is null)
            return;

        try
        {
            _logger.LogInformation(
                "ReportConfigurationExecution {@Execution}",
                new
                {
                    UserId = userId,
                    ConfigurationId = request.ConfigurationId,
                    PinnedRevision = preview?.PinnedRevisionVersion,
                    DatasetKey = preview?.DatasetKey,
                    Operation = "preview",
                    Outcome = outcome,
                    DurationMs = durationMs,
                    RowCount = preview?.Rows?.Count ?? 0,
                    ColumnCount = preview?.Columns?.Count ?? 0,
                    RelationKeys = RelationKeysOf(preview),
                });
        }
        catch
        {
            // 日志失败不影响主流程
        }
    }

    private static string[] RelationKeysOf(ReportConfigurationPreviewDto? preview)
    {
        if (preview?.RelationEvidence is null)
            return Array.Empty<string>();

        return preview.RelationEvidence
            .Select(r => r.RelationKey)
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }


    private async Task<ReportConfigurationPreviewDto> PreviewOwnedAsync(
        long ownerUserId, ReportConfigurationPreviewRequest request, ReportConfiguration config,
        CancellationToken cancellationToken, string correlationId)
    {
        // 选定定义：草稿 or 固定发布修订（修订必须属于该配置且未被软删除）
        ReportConfigurationDefinition definition;
        string datasetKey;
        string name;
        int? pinnedRevision = null;

        if (request.RevisionVersion.HasValue)
        {
            var revision = await _db.ReportConfigurationRevisions
                .FirstOrDefaultAsync(r => r.ReportConfigurationId == config.Id
                    && !r.IsDeleted && r.Version == request.RevisionVersion.Value, cancellationToken)
                ?? throw BusinessException.NotFound("指定的发布版本不存在");

            definition = Deserialize(revision.DefinitionJson)
                ?? throw BusinessException.RuleConflict("发布修订定义缺失，无法预览");
            datasetKey = revision.DatasetKey;
            name = revision.Name;
            pinnedRevision = revision.Version;
        }
        else
        {
            definition = Deserialize(config.DefinitionJson)
                ?? throw BusinessException.RuleConflict("报表配置定义缺失，无法预览");
            datasetKey = config.DatasetKey;
            name = config.Name;
        }

        return await ExecutePreviewAsync(ownerUserId, config.Id, datasetKey, definition, name,
            pinnedRevision, pinnedRevision ?? config.Version, request, cancellationToken, correlationId, isShared: false);
    }

    private async Task<ReportConfigurationPreviewDto> PreviewSharedAsync(
        long recipientUserId, ReportConfigurationPreviewRequest request,
        CancellationToken cancellationToken, string correlationId)
    {
        // 被授权人必须为现有激活用户（fail closed）
        var recipient = await _db.SysUsers.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == recipientUserId && !u.IsDeleted, cancellationToken)
            ?? throw BusinessException.NotFound("报表配置不存在或无权访问");
        if (recipient.Status != UserStatus.Enabled)
            throw BusinessException.NotFound("报表配置不存在或无权访问");

        var grant = await _db.ReportConfigurationGrants
            .FirstOrDefaultAsync(g => g.RecipientUserId == recipientUserId
                && g.ReportConfigurationId == request.ConfigurationId && !g.IsDeleted, cancellationToken)
            ?? throw BusinessException.NotFound("报表配置不存在或无权访问");

        var config = await _db.ReportConfigurations
            .FirstOrDefaultAsync(c => c.Id == grant.ReportConfigurationId && !c.IsDeleted, cancellationToken)
            ?? throw BusinessException.NotFound("报表配置不存在或无权访问");

        var revision = await _db.ReportConfigurationRevisions
            .FirstOrDefaultAsync(r => r.ReportConfigurationId == config.Id
                && !r.IsDeleted && r.Version == grant.RevisionVersion, cancellationToken)
            ?? throw BusinessException.NotFound("共享的发布版本不存在");

        var definition = Deserialize(revision.DefinitionJson)
            ?? throw BusinessException.RuleConflict("共享发布修订定义缺失，无法预览");

        // 共享预览始终使用固定修订（忽略客户端 RevisionVersion），绝不暴露其它修订 / 草稿
        return await ExecutePreviewAsync(recipientUserId, config.Id, revision.DatasetKey, definition,
            revision.Name, revision.Version, revision.Version, request, cancellationToken, correlationId, isShared: true);
    }

    private async Task<ReportConfigurationPreviewDto> ExecutePreviewAsync(
        long userId, long configurationId, string datasetKey, ReportConfigurationDefinition definition,
        string name, int? pinnedRevision, int version, ReportConfigurationPreviewRequest request,
        CancellationToken cancellationToken, string correlationId, bool isShared)
    {
        // 定位对应数据集适配器（未知数据集 fail closed）
        var provider = _providers.FirstOrDefault(p =>
            string.Equals(p.DatasetKey, datasetKey, StringComparison.OrdinalIgnoreCase))
            ?? throw BusinessException.InvalidParameter($"未知数据集: {datasetKey}");

        // 每次预览重新校验数据集菜单授权（撤销后立即收敛；未授权 → 拒绝，不返回任何数据）
        var dataset = await provider.GetDatasetAsync(userId, cancellationToken)
            ?? throw new BusinessException($"当前账号没有「{datasetKey}」数据集授权：拒绝预览（fail closed）",
                ErrorCodes.Forbidden);

        // 按「当前」目录重新校验有界定义（字段撤销 / 陈旧 schema / 不支持能力都会在此被拒绝）
        ReportConfigurationRules.Validate(definition, dataset);

        // 解析 / 校验当前预览参数（页码 / 每页条数 / 分组键）
        var parameters = ResolveParameters(request, definition, dataset);

        // ERP-273：客户显式选择有界匹配集覆盖时走一致快照管线；否则维持既有当前页行为（默认）。
        if (string.Equals(definition.Coverage?.Trim(), ReportConfigurationConstants.CoverageMatchedSet,
                StringComparison.OrdinalIgnoreCase))
        {
            return await ExecuteMatchedSetPreviewAsync(
                userId, configurationId, datasetKey, definition, name, pinnedRevision, version,
                request, parameters, dataset, provider, correlationId, isShared, cancellationToken);
        }

        // 分发执行（适配器内部再次走既有查询的菜单授权 + 数据范围，并保留币种 / 单位口径）
        var preview = await provider.PreviewAsync(definition, parameters, userId, cancellationToken);
        preview.Groupings = parameters.Groupings.ToList();

        // 透视（ERP-272）：在指标汇总剥离隐藏依赖前，从当前页事实行构建有界透视结果（与普通行分离存储）。
        if (definition.Pivot is not null)
            preview.Pivot = ReportConfigurationPivotRules.Build(definition, dataset, preview.Rows, cancellationToken);

        ApplyMetrics(definition, dataset, parameters.Groupings, preview);
        if (parameters.Groupings.Count >= 2 || definition.Pivot is not null)
            StripCompositeDependencies(definition, preview);
        await ApplyRelationsAsync(definition, preview, userId, cancellationToken);
        preview.ConfigurationId = configurationId;
        preview.PinnedRevisionVersion = pinnedRevision;
        preview.IsPinnedRevision = pinnedRevision.HasValue;
        preview.Name = name;
        preview.Version = version;
        preview.NormalizedFiltersText = BuildNormalizedFiltersText(definition, dataset);
        preview.DateRangeText = BuildNormalizedDateRangeText(definition, dataset);
        preview.SortFieldKey = parameters.SortFieldKey;
        preview.SortDirection = parameters.SortDirection;
        preview.SortEvidence = BuildNormalizedSortText(parameters, dataset);
        preview.MatchedCount = preview.Total;
        return preview;
    }

    private async Task<ReportConfigurationPreviewDto> ExecuteMatchedSetPreviewAsync(
        long userId, long configurationId, string datasetKey, ReportConfigurationDefinition definition,
        string name, int? pinnedRevision, int version, ReportConfigurationPreviewRequest request,
        ReportConfigurationPreviewParameters parameters, ReportConfigurationDatasetDto dataset,
        IReportConfigurationDatasetProvider provider, string correlationId, bool isShared,
        CancellationToken cancellationToken)
    {
        if (!provider.SupportsReadSnapshot)
        {
            throw new BusinessException(
                $"数据集 {datasetKey} 不支持有界一致只读快照（environment-blocked）",
                ReportConfigurationExecutionLimits.ErrorCodeEnvironmentUnsupported);
        }

        IReportConfigurationReadSnapshot snapshot;
        try
        {
            snapshot = await provider.OpenReadSnapshotAsync(
                definition, parameters, userId, correlationId, cancellationToken);
        }
        catch (BusinessException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new BusinessException(
                "当前环境无法提供一致只读快照（environment-blocked）",
                ReportConfigurationExecutionLimits.ErrorCodeEnvironmentUnsupported);
        }

        await using (snapshot)
        {
            var preview = provider.RenderMatchedPage(snapshot, definition, parameters);
            preview.Groupings = parameters.Groupings.ToList();

            if (definition.Pivot is not null)
                preview.Pivot = ReportConfigurationPivotRules.Build(definition, dataset, preview.Rows, cancellationToken);

            ApplyMetrics(definition, dataset, parameters.Groupings, preview);
            if (parameters.Groupings.Count >= 2 || definition.Pivot is not null)
                StripCompositeDependencies(definition, preview);
            await ApplyRelationsAsync(definition, preview, userId, cancellationToken);

            preview.ConfigurationId = configurationId;
            preview.PinnedRevisionVersion = pinnedRevision;
            preview.IsPinnedRevision = pinnedRevision.HasValue;
            preview.Name = name;
            preview.Version = version;
            preview.NormalizedFiltersText = BuildNormalizedFiltersText(definition, dataset);
            preview.DateRangeText = BuildNormalizedDateRangeText(definition, dataset);
            preview.SortFieldKey = parameters.SortFieldKey;
            preview.SortDirection = parameters.SortDirection;
            preview.SortEvidence = BuildNormalizedSortText(parameters, dataset);
            preview.MatchedCount = snapshot.MatchedCount;

            // 完成事务后再做最终新鲜授权复核（授权 / 菜单 / 数据范围变更即拒绝整个响应）
            await snapshot.CompleteAsync(cancellationToken);
            await EnsureFinalAuthorizationAsync(
                userId, configurationId, datasetKey, snapshot.ScopeFingerprint, isShared, cancellationToken);

            return preview;
        }
    }

    private async Task EnsureFinalAuthorizationAsync(
        long userId, long configurationId, string datasetKey, string scopeFingerprint, bool isShared,
        CancellationToken cancellationToken)
    {
        // 1) 新鲜菜单复核（fail closed）
        var provider = _providers.FirstOrDefault(p =>
            string.Equals(p.DatasetKey, datasetKey, StringComparison.OrdinalIgnoreCase))
            ?? throw new BusinessException($"未知数据集: {datasetKey}");
        if (await provider.GetDatasetAsync(userId, cancellationToken) is null)
            throw new BusinessException($"当前账号没有「{datasetKey}」数据集授权：拒绝预览（fail closed）", ErrorCodes.Forbidden);

        // 2) 新鲜数据范围复核（变更即拒绝）
        var scope = await SalespersonDataScopeService.ResolveAsync(_db, userId);
        if (!string.Equals(ComputeScopeFingerprint(scope), scopeFingerprint, StringComparison.Ordinal))
            throw new BusinessException("数据范围已变更，本次预览被拒绝（fail closed）", ErrorCodes.Forbidden);

        // 3) 共享授权复核（仅共享预览）
        if (isShared)
        {
            var grant = await _db.ReportConfigurationGrants
                .FirstOrDefaultAsync(g => g.RecipientUserId == userId
                    && g.ReportConfigurationId == configurationId && !g.IsDeleted, cancellationToken)
                ?? throw BusinessException.NotFound("报表配置不存在或无权访问");
        }
    }

    private static string ComputeScopeFingerprint(SalespersonDataScope scope)
        => scope.IsPrivileged
            ? "privileged"
            : (scope.SalesmanId?.ToString(CultureInfo.InvariantCulture) ?? "none")
              + "|" + string.Join(",", (scope.AllowedCustomerIds ?? new HashSet<long>()).OrderBy(x => x));

    // ==================== 内部辅助 ====================

    /// <summary>执行用户已选中的指标汇总，并从行 / 列中剥离未选择展示的指标原始依赖值（绝不返回隐藏依赖）。</summary>
    private static void ApplyMetrics(
        ReportConfigurationDefinition definition,
        ReportConfigurationDatasetDto dataset,
        IReadOnlyList<string> groupings,
        ReportConfigurationPreviewDto preview)
    {
        var aggregates = definition.Aggregates ?? new List<ReportConfigurationAggregate>();
        if (aggregates.Count == 0)
            return;

        preview.Metrics = ReportConfigurationMetricRules.Compute(definition, dataset, preview.Rows, groupings);

        var selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in definition.Fields ?? new List<string>())
        {
            if (!string.IsNullOrWhiteSpace(field))
                selected.Add(field.Trim());
        }

        var metricKeys = aggregates
            .Where(a => a is not null && !string.IsNullOrWhiteSpace(a.FieldKey))
            .Select(a => a.FieldKey.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var hidden = metricKeys.Where(k => !selected.Contains(k)).ToList();
        if (hidden.Count == 0)
            return;

        preview.Columns = preview.Columns
            .Where(c => !hidden.Contains(c.Key, StringComparer.OrdinalIgnoreCase))
            .ToList();

        foreach (var row in preview.Rows)
        {
            if (row is null)
                continue;
            var remove = row.Keys.Where(k => hidden.Contains(k, StringComparer.OrdinalIgnoreCase)).ToList();
            foreach (var key in remove)
                row.Remove(key);
        }
    }

    /// <summary>
    /// 复合分组（ERP-271）：通用引擎读取未分组源页时补取的维度 / 币种 / 金额授权依赖仅在分组汇总中消费，
    /// 数据表只投影用户选定字段（保留选择顺序），绝不泄露隐藏依赖、绝不复制事实行。
    /// </summary>
    private static void StripCompositeDependencies(ReportConfigurationDefinition definition, ReportConfigurationPreviewDto preview)
    {
        var selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in definition.Fields ?? new List<string>())
        {
            if (!string.IsNullOrWhiteSpace(field))
                selected.Add(field.Trim());
        }
        foreach (var column in definition.ComputedColumns ?? new List<ReportConfigurationComputedColumn>())
        {
            if (column is not null && !string.IsNullOrWhiteSpace(column.Key))
                selected.Add(column.Key.Trim());
        }

        preview.Columns = preview.Columns
            .Where(c => selected.Contains(c.Key))
            .ToList();

        foreach (var row in preview.Rows)
        {
            if (row is null)
                continue;
            var remove = row.Keys.Where(k => !selected.Contains(k)).ToList();
            foreach (var key in remove)
                row.Remove(key);
        }
    }

    /// <summary>补全受控关系维度（ERP-268）：定义含关系选择时调用关系解析器，批量只读解析当前页客户 Id。</summary>
    private async Task ApplyRelationsAsync(
        ReportConfigurationDefinition definition,
        ReportConfigurationPreviewDto preview,
        long userId,
        CancellationToken cancellationToken)
    {
        if (_relationResolver is null)
            return;
        if (definition.Relations is not { Count: > 0 })
            return;
        await _relationResolver.EnrichAsync(preview, definition, userId, cancellationToken);
    }

    private static void EnsureAuthenticated(long ownerUserId)
    {
        if (ownerUserId <= 0)
            throw new BusinessException("请先登录后再预览报表配置", ErrorCodes.Unauthorized);
    }

    private static ReportConfigurationDefinition? Deserialize(string? json)
        => string.IsNullOrWhiteSpace(json)
            ? null
            : JsonSerializer.Deserialize<ReportConfigurationDefinition>(json, JsonOptions);

    private static ReportConfigurationPreviewParameters ResolveParameters(
        ReportConfigurationPreviewRequest request,
        ReportConfigurationDefinition definition,
        ReportConfigurationDatasetDto dataset)
    {
        var page = request.Page ?? definition.Presentation?.Page ?? 1;
        var pageSize = request.PageSize ?? definition.Presentation?.PageSize ?? dataset.DefaultPageSize;

        if (page < 1)
            throw BusinessException.InvalidParameter("页码必须从 1 开始");
        if (pageSize < 1 || pageSize > dataset.MaxPageSize)
            throw BusinessException.InvalidParameter($"每页条数必须在 1 ~ {dataset.MaxPageSize} 之间");

        var groupings = ResolveGroupings(request, definition, dataset);
        var groupBy = groupings.Count switch
        {
            1 => groupings[0],
            _ => ReportConfigurationConstants.GroupNone,
        };

        var (sortFieldKey, sortDirection) = ResolveSort(definition);
        return new ReportConfigurationPreviewParameters(page, pageSize, groupBy, sortFieldKey, sortDirection)
        {
            Groupings = groupings,
        };
    }

    /// <summary>解析保存的排序（排序在纯校验器中已按目录校验为有限可排序字段；无排序时返回空）。</summary>
    private static (string? FieldKey, string? Direction) ResolveSort(ReportConfigurationDefinition definition)
    {
        var presentation = definition.Presentation;
        var fieldKey = string.IsNullOrWhiteSpace(presentation?.SortFieldKey)
            ? null
            : presentation.SortFieldKey.Trim();
        if (fieldKey is null)
            return (null, null);

        var direction = string.IsNullOrWhiteSpace(presentation?.SortDirection)
            ? "asc"
            : presentation.SortDirection.Trim();
        return (fieldKey, direction);
    }

    private static IReadOnlyList<string> ResolveGroupings(
        ReportConfigurationPreviewRequest request,
        ReportConfigurationDefinition definition,
        ReportConfigurationDatasetDto dataset)
    {
        var overrideList = request.Groupings;
        var overrideSingle = request.GroupBy;

        // ERP-271：旧单 GroupBy 覆盖与新有序 Groupings 覆盖冲突时显式拒绝（绝不猜测优先级）。
        if (overrideList is { Count: > 0 } && !string.IsNullOrWhiteSpace(overrideSingle))
            throw BusinessException.InvalidParameter("分组覆盖冲突：请只使用 groupBy（单一）或 groupings（有序复合）之一");

        IReadOnlyList<string> resolved;
        if (overrideList is { Count: > 0 })
            resolved = ReportConfigurationGroupingRules.NormalizeGroupingKeys(overrideList);
        else if (!string.IsNullOrWhiteSpace(overrideSingle))
            resolved = new[] { overrideSingle.Trim() };
        else
            resolved = ReportConfigurationGroupingRules.NormalizeGroupingKeys(definition.Grouping);

        return ReportConfigurationGroupingRules.ValidateGrouping(resolved, dataset);
    }

    // ==================== 导出证据规范化（纯文本、无 DB、无数据集特化分派） ====================

    private static string BuildNormalizedFiltersText(
        ReportConfigurationDefinition definition, ReportConfigurationDatasetDto dataset)
    {
        if (definition.Filters is null || definition.Filters.Count == 0)
            return string.Empty;

        var parts = new List<string>();
        foreach (var filter in definition.Filters)
        {
            if (filter is null || string.IsNullOrWhiteSpace(filter.FieldKey) || string.IsNullOrWhiteSpace(filter.Operator))
                continue;

            var text = BuildFilterText(filter, dataset);
            if (!string.IsNullOrWhiteSpace(text))
                parts.Add(text);
        }

        return string.Join("；", parts);
    }

    private static string BuildNormalizedDateRangeText(
        ReportConfigurationDefinition definition, ReportConfigurationDatasetDto dataset)
    {
        DateTime? start = null;
        DateTime? end = null;
        var hasDateFilter = false;

        foreach (var filter in definition.Filters ?? new List<ReportConfigurationFilter>())
        {
            if (filter is null || string.IsNullOrWhiteSpace(filter.FieldKey) || string.IsNullOrWhiteSpace(filter.Operator))
                continue;

            if (!TryGetField(dataset, filter.FieldKey, out var field)
                || !string.Equals(field.Type, ReportConfigurationConstants.TypeDate, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!TryApplyDateBound(filter, ref start, ref end))
                continue;

            hasDateFilter = true;
        }

        if (!hasDateFilter)
            return string.Empty;

        if (start.HasValue && end.HasValue && start.Value.Date == end.Value.Date)
            return start.Value.Date.ToString("yyyy-MM-dd");
        if (start.HasValue && end.HasValue)
            return $"{start.Value.Date:yyyy-MM-dd} ~ {end.Value.Date:yyyy-MM-dd}";
        if (start.HasValue)
            return $"{start.Value.Date:yyyy-MM-dd} 起";
        return $"截至 {end!.Value.Date:yyyy-MM-dd}";
    }

    private static string BuildNormalizedSortText(
        ReportConfigurationPreviewParameters parameters, ReportConfigurationDatasetDto dataset)
    {
        var isReceivable = string.Equals(
            dataset.DatasetKey, ReportConfigurationConstants.DatasetReceivable, StringComparison.OrdinalIgnoreCase);
        var identityLabel = isReceivable ? "发票 Id" : "订单 Id";

        var fieldKey = parameters.SortFieldKey;
        if (string.IsNullOrWhiteSpace(fieldKey))
        {
            return isReceivable
                ? "默认排序：按客户 / 币种 / 开票日期降序 / 发票 Id 降序（既有默认口径）"
                : "默认排序：按订单 Id 升序（稳定分页）";
        }

        var label = TryGetField(dataset, fieldKey, out var field) ? field.Label : fieldKey;
        var descending = string.Equals(parameters.SortDirection, "desc", StringComparison.OrdinalIgnoreCase);
        return $"排序：{label} {(descending ? "降序" : "升序")}；并列时按 {identityLabel} 升序稳定分页";
    }

    private static string? BuildFilterText(ReportConfigurationFilter filter, ReportConfigurationDatasetDto dataset)
    {
        if (!TryGetField(dataset, filter.FieldKey, out var field))
            return null;

        var op = filter.Operator.Trim();
        return $"{field.Label} {OperatorLabel(op)} {BuildValueText(filter)}";
    }

    private static string OperatorLabel(string op)
    {
        return op switch
        {
            ReportConfigurationConstants.OperatorEq => "=",
            ReportConfigurationConstants.OperatorNe => "≠",
            ReportConfigurationConstants.OperatorIn => "属于",
            ReportConfigurationConstants.OperatorGt => ">",
            ReportConfigurationConstants.OperatorGte => "≥",
            ReportConfigurationConstants.OperatorLt => "<",
            ReportConfigurationConstants.OperatorLte => "≤",
            ReportConfigurationConstants.OperatorBetween => "介于",
            _ => op,
        };
    }

    private static string BuildValueText(ReportConfigurationFilter filter)
    {
        var op = filter.Operator.Trim();
        var value = ToClrValue(filter.Value);

        if (string.Equals(op, ReportConfigurationConstants.OperatorBetween, StringComparison.OrdinalIgnoreCase))
        {
            var upper = ToClrValue(filter.Value2);
            return $"{DisplayScalar(value)} ~ {DisplayScalar(upper)}";
        }

        if (string.Equals(op, ReportConfigurationConstants.OperatorIn, StringComparison.OrdinalIgnoreCase)
            && value is IEnumerable enumerable && value is not string)
        {
            return string.Join("、", enumerable.Cast<object?>().Select(DisplayScalar));
        }

        return DisplayScalar(value);
    }

    private static string DisplayScalar(object? value)
    {
        if (value is null)
            return "空";
        if (value is DateTime dt)
            return dt.ToString("yyyy-MM-dd");
        if (value is DateTimeOffset dto)
            return dto.DateTime.ToString("yyyy-MM-dd");
        if (value is bool b)
            return b ? "是" : "否";
        return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static bool TryApplyDateBound(ReportConfigurationFilter filter, ref DateTime? start, ref DateTime? end)
    {
        switch (filter.Operator.Trim())
        {
            case ReportConfigurationConstants.OperatorEq:
                if (TryGetDate(filter.Value, out var eq)) { start = eq; end = eq; return true; }
                return false;
            case ReportConfigurationConstants.OperatorGte:
                if (TryGetDate(filter.Value, out var gte)) { start = gte; return true; }
                return false;
            case ReportConfigurationConstants.OperatorLte:
                if (TryGetDate(filter.Value, out var lte)) { end = lte; return true; }
                return false;
            case ReportConfigurationConstants.OperatorGt:
                if (TryGetDate(filter.Value, out var gt)) { start = gt.AddDays(1); return true; }
                return false;
            case ReportConfigurationConstants.OperatorLt:
                if (TryGetDate(filter.Value, out var lt)) { end = lt.AddDays(-1); return true; }
                return false;
            case ReportConfigurationConstants.OperatorBetween:
                if (TryGetDate(filter.Value, out var b1) && TryGetDate(filter.Value2, out var b2)) { start = b1; end = b2; return true; }
                return false;
            default:
                return false;
        }
    }

    private static bool TryGetDate(object? value, out DateTime date)
    {
        var v = ToClrValue(value);
        switch (v)
        {
            case DateTime dt:
                date = dt.Date;
                return true;
            case DateTimeOffset dto:
                date = dto.DateTime.Date;
                return true;
            case string s when TryParseDate(s, out var parsed):
                date = parsed.Date;
                return true;
            default:
                date = default;
                return false;
        }
    }

    private static bool TryGetField(
        ReportConfigurationDatasetDto dataset, string key, out ReportConfigurationFieldDto field)
    {
        foreach (var candidate in dataset.Fields)
        {
            if (string.Equals(candidate.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                field = candidate;
                return true;
            }
        }

        field = null!;
        return false;
    }

    private static bool TryParseDate(string text, out DateTime date)
        => DateTime.TryParse(text, CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.RoundtripKind, out date);

    private static object? ToClrValue(object? value)
        => value is JsonElement element ? JsonElementToClr(element) : value;

    private static object? JsonElementToClr(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                return element.GetString();
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            case JsonValueKind.Number:
                return element.TryGetInt64(out var l) ? l : element.GetDecimal();
            case JsonValueKind.Array:
                return element.EnumerateArray().Select(JsonElementToClr).ToList();
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
            case JsonValueKind.Object:
            default:
                return null;
        }
    }
}

