using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using System.Text.Json;

namespace ERP.Application.Services;

/// <summary>
/// 通用报表配置捆绑服务（ERP-307 Stage 2）实现：把有界有序的既有定义 / 版本引用编排为一次多节预览或导出。
/// <para>捆绑本身无状态、不新增实体 / 数据库结构、不执行任意 SQL / 联接 / 跨数据集运算、不新增报表专用查询逻辑；
/// 每个节都复用既有 <see cref="IReportConfigurationExecutionService"/> 在单一执行租约内重新校验归属 / 共享授权 /
/// 固定修订 / 数据集菜单授权与数据范围（fail closed），并保留各节独立的币种 / 单位 / 粒度 / 证据与既有查询 / 审计边界。</para>
/// <para>安全口径：全部节都加载并校验通过后才产出结果，任何一节被拒绝 / 撤销 / 失效即整个捆绑失败，绝不产出部分数据或部分下载。</para>
/// </summary>
public sealed class ReportConfigurationBundleService : IReportConfigurationBundleService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly IReportConfigurationExecutionService _execution;
    private readonly IReportConfigurationExecutionBudget _budget;

    public ReportConfigurationBundleService(
        IReportConfigurationExecutionService execution,
        IReportConfigurationExecutionBudget? budget = null)
    {
        _execution = execution ?? throw new ArgumentNullException(nameof(execution));
        _budget = budget ?? new ReportConfigurationExecutionBudget();
    }

    /// <inheritdoc />
    public async Task<ReportConfigurationBundlePreviewDto> PreviewAsync(
        long ownerUserId,
        ReportConfigurationBundleRequest request,
        CancellationToken cancellationToken = default)
        => await _budget.ExecuteAsync(
            ownerUserId,
            cancellationToken,
            lease => PreviewWithinLeaseCoreAsync(ownerUserId, request, lease));

    /// <inheritdoc />
    public Task<ReportConfigurationBundlePreviewDto> PreviewAsync(
        long ownerUserId,
        ReportConfigurationBundleRequest request,
        IReportConfigurationExecutionLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        return PreviewWithinLeaseCoreAsync(ownerUserId, request, lease);
    }

    /// <inheritdoc />
    public async Task<ReportConfigurationBundleExportResultDto> BuildExportResultAsync(
        long ownerUserId,
        ReportConfigurationBundleRequest request,
        IReportConfigurationExecutionLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);

        var sections = ValidateRequest(request);
        var result = new ReportConfigurationBundleExportResultDto
        {
            Name = BundleName(),
            CorrelationId = lease.CorrelationId,
        };

        var list = new List<ReportConfigurationBundleSectionExportDto>(sections.Count);
        var totalRows = 0;
        var ordinal = 0;

        foreach (var section in sections)
        {
            // 逐节复用既有服务端内部导出结果（同一租约；matched-set ≤1000 / current-page ≤200，绝不二次获取租约）。
            var export = await _execution.BuildExportResultAsync(ownerUserId, ToPreviewRequest(section), lease);

            ordinal++;
            totalRows += export.Facts.Count;
            if (totalRows > ReportConfigurationBundleLimits.MaxTotalExportRows)
                throw new BusinessException(
                    $"捆绑导出跨节合计行数超出上限（{ReportConfigurationBundleLimits.MaxTotalExportRows}）",
                    ReportConfigurationExecutionLimits.ErrorCodeResultTooLarge);

            list.Add(new ReportConfigurationBundleSectionExportDto
            {
                Ordinal = ordinal,
                Title = ResolveTitle(section, export.Preview),
                ConfigurationId = section.ConfigurationId,
                RevisionVersion = section.RevisionVersion,
                Preview = export.Preview,
                Facts = export.Facts,
                Coverage = export.Coverage,
                MatchedCount = export.MatchedCount,
                SourceEvidenceCount = export.SourceEvidenceCount,
            });
        }

        result.Sections = list;
        result.SectionCount = list.Count;
        result.TotalRowCount = totalRows;
        return result;
    }

    private async Task<ReportConfigurationBundlePreviewDto> PreviewWithinLeaseCoreAsync(
        long ownerUserId,
        ReportConfigurationBundleRequest request,
        IReportConfigurationExecutionLease lease)
    {
        var sections = ValidateRequest(request);
        var result = new ReportConfigurationBundlePreviewDto
        {
            Name = BundleName(),
            CorrelationId = lease.CorrelationId,
        };

        var list = new List<ReportConfigurationBundleSectionDto>(sections.Count);
        var totalRows = 0;
        var ordinal = 0;

        foreach (var section in sections)
        {
            // 逐节复用既有有界、已授权预览（同一租约；每次重新校验归属 / 共享 / 菜单 / 数据范围）。
            var preview = await _execution.PreviewAsync(ownerUserId, ToPreviewRequest(section), lease);

            ordinal++;
            totalRows += preview.Rows?.Count ?? 0;
            if (totalRows > ReportConfigurationBundleLimits.MaxTotalRows)
                throw new BusinessException(
                    $"捆绑预览跨节合计行数超出上限（{ReportConfigurationBundleLimits.MaxTotalRows}）",
                    ReportConfigurationExecutionLimits.ErrorCodeResultTooLarge);

            list.Add(new ReportConfigurationBundleSectionDto
            {
                Ordinal = ordinal,
                Title = ResolveTitle(section, preview),
                ConfigurationId = section.ConfigurationId,
                RevisionVersion = section.RevisionVersion,
                Preview = preview,
            });
        }

        result.Sections = list;
        result.SectionCount = list.Count;
        result.TotalRowCount = totalRows;

        CheckSerializedPreviewBytes(result, lease.CorrelationId);
        return result;
    }

    private static IReadOnlyList<ReportConfigurationBundleSectionRequest> ValidateRequest(
        ReportConfigurationBundleRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Sections is null || request.Sections.Count < ReportConfigurationBundleLimits.MinSections)
            throw BusinessException.InvalidParameter("捆绑至少需要 1 个节");

        if (request.Sections.Count > ReportConfigurationBundleLimits.MaxSections)
            throw BusinessException.InvalidParameter(
                $"捆绑最多允许 {ReportConfigurationBundleLimits.MaxSections} 个节，当前 {request.Sections.Count} 个");

        var sections = new List<ReportConfigurationBundleSectionRequest>(request.Sections.Count);
        for (var i = 0; i < request.Sections.Count; i++)
        {
            var section = request.Sections[i]
                ?? throw BusinessException.InvalidParameter($"第 {i + 1} 个节为空");

            if (section.ConfigurationId <= 0)
                throw BusinessException.InvalidParameter($"第 {i + 1} 个节缺少有效的报表配置 Id");

            if (section.RevisionVersion is <= 0)
                throw BusinessException.InvalidParameter($"第 {i + 1} 个节的固定发布版本号必须为正整数");

            if (section.Page is < 1)
                throw BusinessException.InvalidParameter($"第 {i + 1} 个节的页码必须从 1 开始");

            if (section.PageSize is < 1)
                throw BusinessException.InvalidParameter($"第 {i + 1} 个节的每页条数必须从 1 开始");

            var title = section.Title ?? string.Empty;
            if (title.Length > ReportConfigurationBundleLimits.MaxSectionTitleLength)
                throw BusinessException.InvalidParameter(
                    $"第 {i + 1} 个节的标题最长 {ReportConfigurationBundleLimits.MaxSectionTitleLength} 个字符");

            sections.Add(section);
        }

        return sections;
    }

    private static ReportConfigurationPreviewRequest ToPreviewRequest(
        ReportConfigurationBundleSectionRequest section)
        => new()
        {
            ConfigurationId = section.ConfigurationId,
            RevisionVersion = section.RevisionVersion,
            Page = section.Page,
            PageSize = section.PageSize,
        };

    private static string ResolveTitle(
        ReportConfigurationBundleSectionRequest section,
        ReportConfigurationPreviewDto preview)
    {
        var title = (section.Title ?? string.Empty).Trim();
        if (title.Length == 0)
            title = preview?.Name ?? string.Empty;
        if (title.Length == 0)
            title = $"报表配置 #{section.ConfigurationId}";
        return title;
    }

    private static string BundleName() => "报表配置捆绑";

    private static void CheckSerializedPreviewBytes(ReportConfigurationBundlePreviewDto result, string correlationId)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(result, JsonOptions).LongLength;
        if (bytes > ReportConfigurationBundleLimits.MaxSerializedBundlePreviewBytes)
            throw new BusinessException(
                "捆绑预览结果过大，已拒绝返回，请减少节数或缩小筛选范围（关联ID：" + correlationId + "）",
                ReportConfigurationExecutionLimits.ErrorCodeResultTooLarge);
    }
}

