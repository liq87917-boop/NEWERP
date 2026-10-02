using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using Microsoft.EntityFrameworkCore;
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

    public ReportConfigurationExecutionService(
        IErpDbContext db,
        IEnumerable<IReportConfigurationDatasetProvider> providers)
    {
        _db = db;
        _providers = (providers ?? Array.Empty<IReportConfigurationDatasetProvider>())
            .OrderBy(p => p.DatasetKey, StringComparer.Ordinal)
            .ToList();
    }

    /// <inheritdoc />
    public async Task<ReportConfigurationPreviewDto> PreviewAsync(
        long ownerUserId,
        ReportConfigurationPreviewRequest request,
        CancellationToken cancellationToken = default)
    {
        EnsureAuthenticated(ownerUserId);
        ArgumentNullException.ThrowIfNull(request);
        if (request.ConfigurationId <= 0)
            throw BusinessException.InvalidParameter("请选择要预览的报表配置");

        // 1) 仅加载当前用户自己、未删除的配置（跨所有者 / 已删除 → 一律 NotFound，不泄露存在性）
        var config = await _db.ReportConfigurations
            .FirstOrDefaultAsync(c => c.Id == request.ConfigurationId && !c.IsDeleted && c.OwnerUserId == ownerUserId,
                cancellationToken)
            ?? throw BusinessException.NotFound("报表配置不存在或无权访问");

        // 2) 选定定义：草稿 or 固定发布修订（修订必须属于该配置且未被软删除）
        ReportConfigurationDefinition definition;
        string datasetKey;
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
            pinnedRevision = revision.Version;
        }
        else
        {
            definition = Deserialize(config.DefinitionJson)
                ?? throw BusinessException.RuleConflict("报表配置定义缺失，无法预览");
            datasetKey = config.DatasetKey;
        }

        // 3) 定位对应数据集适配器（未知数据集 fail closed）
        var provider = _providers.FirstOrDefault(p =>
            string.Equals(p.DatasetKey, datasetKey, StringComparison.OrdinalIgnoreCase))
            ?? throw BusinessException.InvalidParameter($"未知数据集: {datasetKey}");

        // 4) 每次预览重新校验数据集菜单授权（撤销后立即收敛；未授权 → 拒绝，不返回任何数据）
        var dataset = await provider.GetDatasetAsync(ownerUserId, cancellationToken)
            ?? throw new BusinessException($"当前账号没有「{datasetKey}」数据集授权：拒绝预览（fail closed）",
                ErrorCodes.Forbidden);

        // 5) 按「当前」目录重新校验有界定义（字段撤销 / 陈旧 schema / 不支持能力都会在此被拒绝）
        ReportConfigurationRules.Validate(definition, dataset);

        // 6) 解析 / 校验当前预览参数（页码 / 每页条数 / 分组键）
        var parameters = ResolveParameters(request, definition, dataset);

        // 7) 分发执行（适配器内部再次走既有查询的菜单授权 + 数据范围，并保留币种 / 单位口径）
        var preview = await provider.PreviewAsync(definition, parameters, ownerUserId, cancellationToken);
        preview.ConfigurationId = config.Id;
        preview.PinnedRevisionVersion = pinnedRevision;
        preview.IsPinnedRevision = pinnedRevision.HasValue;
        return preview;
    }

    // ==================== 内部辅助 ====================

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

        var groupBy = ResolveGroupBy(request.GroupBy, definition);
        if (!dataset.GroupingKeys.Contains(groupBy, StringComparer.OrdinalIgnoreCase))
        {
            throw BusinessException.InvalidParameter(
                $"分组键 {groupBy} 不是数据集 {dataset.DatasetKey} 支持的分组（仅支持 {string.Join(" / ", dataset.GroupingKeys)}）");
        }

        return new ReportConfigurationPreviewParameters(page, pageSize, groupBy);
    }

    private static string ResolveGroupBy(string? overrideGroupBy, ReportConfigurationDefinition definition)
    {
        if (!string.IsNullOrWhiteSpace(overrideGroupBy))
            return overrideGroupBy.Trim();

        var grouping = (definition.Grouping ?? new List<string>())
            .Where(g => !string.IsNullOrWhiteSpace(g))
            .Select(g => g.Trim())
            .Where(g => !string.Equals(g, ReportConfigurationConstants.GroupNone, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (grouping.Count == 0)
            return ReportConfigurationConstants.GroupNone;

        if (grouping.Count == 1)
            return grouping[0];

        throw BusinessException.InvalidParameter(
            $"当前阶段仅支持单一分组键（收到 {grouping.Count} 个），请选择 none / customer / month 之一");
    }
}

