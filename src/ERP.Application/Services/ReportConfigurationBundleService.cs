using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using System.Globalization;
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

    // ==================== ERP-316 Stage 2：有界表头 / 明细组合 ====================

    /// <inheritdoc />
    public async Task<ReportConfigurationBundleCompositionPreviewDto> ComposePreviewAsync(
        long ownerUserId,
        ReportConfigurationBundleCompositionRequest request,
        CancellationToken cancellationToken = default)
        => await _budget.ExecuteAsync(
            ownerUserId,
            cancellationToken,
            lease => ComposePreviewCoreAsync(ownerUserId, request, lease));

    /// <inheritdoc />
    public Task<ReportConfigurationBundleCompositionPreviewDto> ComposePreviewAsync(
        long ownerUserId,
        ReportConfigurationBundleCompositionRequest request,
        IReportConfigurationExecutionLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        return ComposePreviewCoreAsync(ownerUserId, request, lease);
    }

    /// <inheritdoc />
    public async Task<ReportConfigurationBundleCompositionExportDto> BuildCompositionExportResultAsync(
        long ownerUserId,
        ReportConfigurationBundleCompositionRequest request,
        IReportConfigurationExecutionLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        var preview = await ComposePreviewCoreAsync(ownerUserId, request, lease);
        return new ReportConfigurationBundleCompositionExportDto { Preview = preview };
    }

    private async Task<ReportConfigurationBundleCompositionPreviewDto> ComposePreviewCoreAsync(
        long ownerUserId,
        ReportConfigurationBundleCompositionRequest request,
        IReportConfigurationExecutionLease lease)
    {
        var scenario = ResolveScenario(request);

        // 表头 / 明细两节复用既有执行服务在同一租约内重新校验归属 / 共享授权 / 固定修订 / 数据集菜单授权与数据范围（fail closed）。
        var headerPreview = await _execution.PreviewAsync(ownerUserId, new ReportConfigurationPreviewRequest
        {
            ConfigurationId = request.HeaderConfigurationId,
            RevisionVersion = request.HeaderRevisionVersion,
            Page = 1,
            PageSize = ReportConfigurationBundleCompositionLimits.MaxParents,
        }, lease);

        lease.Token.ThrowIfCancellationRequested();

        var detailPreview = await _execution.PreviewAsync(ownerUserId, new ReportConfigurationPreviewRequest
        {
            ConfigurationId = request.DetailConfigurationId,
            RevisionVersion = request.DetailRevisionVersion,
            Page = 1,
            PageSize = ReportConfigurationBundleCompositionLimits.MaxTotalChildren,
        }, lease);

        lease.Token.ThrowIfCancellationRequested();

        // 不兼容组合显式拒绝：两节数据集必须与场景声明一致，绝不静默跨数据集组合。
        if (!string.Equals(headerPreview?.DatasetKey, scenario.HeaderDatasetKey, StringComparison.OrdinalIgnoreCase))
            throw BusinessException.RuleConflict(
                $"组合不兼容：表头节数据集必须是 {scenario.HeaderDatasetKey}，实际为 {headerPreview?.DatasetKey}");
        if (!string.Equals(detailPreview?.DatasetKey, scenario.DetailDatasetKey, StringComparison.OrdinalIgnoreCase))
            throw BusinessException.RuleConflict(
                $"组合不兼容：明细节数据集必须是 {scenario.DetailDatasetKey}，实际为 {detailPreview?.DatasetKey}");

        RequireCompleteCompositionSection(headerPreview!, "表头");
        RequireCompleteCompositionSection(detailPreview!, "明细");
        return ComposeCore(scenario, request, headerPreview, detailPreview, lease);
    }

    private static void RequireCompleteCompositionSection(ReportConfigurationPreviewDto preview, string section)
    {
        // A page may fit the row limit while omitting further matches. Never compose partial totals.
        if (preview.Total != preview.Rows.Count || preview.MatchedCount != preview.Rows.Count)
            throw new BusinessException(
                $"组合{section}匹配行未完整返回；请收窄筛选范围后重试，禁止以分页子集生成组合合计",
                ReportConfigurationExecutionLimits.ErrorCodeResultTooLarge);
    }

    private static ReportConfigurationBundleCompositionScenario ResolveScenario(
        ReportConfigurationBundleCompositionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.CompositionKey))
            throw BusinessException.InvalidParameter("缺少组合场景键");

        if (request.HeaderConfigurationId <= 0)
            throw BusinessException.InvalidParameter("表头节缺少有效的报表配置 Id");

        if (request.DetailConfigurationId <= 0)
            throw BusinessException.InvalidParameter("明细节缺少有效的报表配置 Id");

        if (request.HeaderRevisionVersion is <= 0 || request.DetailRevisionVersion is <= 0)
            throw BusinessException.InvalidParameter("固定发布版本号必须为正整数");

        return ReportConfigurationBundleCompositionManifest.Find(request.CompositionKey)
            ?? throw BusinessException.NotFound("组合场景不存在");
    }


    private static ReportConfigurationBundleCompositionPreviewDto ComposeCore(
        ReportConfigurationBundleCompositionScenario scenario,
        ReportConfigurationBundleCompositionRequest request,
        ReportConfigurationPreviewDto? headerPreview,
        ReportConfigurationPreviewDto? detailPreview,
        IReportConfigurationExecutionLease lease)
    {
        var headerColumns = headerPreview?.Columns ?? new List<ReportConfigurationColumnDto>();
        var detailColumns = detailPreview?.Columns ?? new List<ReportConfigurationColumnDto>();

        // 组合所需的身份 / 外键 / 合计字段都必须是已授权选中列，缺失即不兼容（fail closed）。
        RequireField(headerColumns, scenario.ParentIdentityFieldKey, "表头节");
        RequireField(detailColumns, scenario.DetailForeignKeyFieldKey, "明细节");
        RequireField(headerColumns, scenario.HeaderAmountFieldKey, "表头节");
        RequireField(headerColumns, scenario.HeaderCurrencyFieldKey, "表头节");
        RequireField(detailColumns, scenario.DetailAmountFieldKey, "明细节");
        RequireField(detailColumns, scenario.DetailCurrencyFieldKey, "明细节");
        RequireField(detailColumns, scenario.DetailQuantityFieldKey, "明细节");
        RequireField(detailColumns, scenario.DetailUnitFieldKey, "明细节");
        if (!string.IsNullOrWhiteSpace(scenario.DetailOrderFieldKey))
            RequireField(detailColumns, scenario.DetailOrderFieldKey!, "明细节");

        var headerRows = headerPreview?.Rows ?? new List<Dictionary<string, object?>>();
        var detailRows = detailPreview?.Rows ?? new List<Dictionary<string, object?>>();

        if (headerRows.Count > ReportConfigurationBundleCompositionLimits.MaxParents)
            throw new BusinessException(
                $"组合父项数超出上限（{ReportConfigurationBundleCompositionLimits.MaxParents}）",
                ReportConfigurationExecutionLimits.ErrorCodeResultTooLarge);

        // 父表：表头一次呈现；缺失身份 / 重复身份显式失败。
        var parents = new List<ReportConfigurationBundleComposedHeaderDto>(headerRows.Count);
        var parentByKey = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < headerRows.Count; i++)
        {
            lease.Token.ThrowIfCancellationRequested();
            var key = IdentityKey(headerRows[i], scenario.ParentIdentityFieldKey);
            if (key is null)
                throw BusinessException.RuleConflict(
                    $"表头第 {i + 1} 行缺少父身份字段 {scenario.ParentIdentityFieldKey}，无法组合（身份不可用）");

            if (!parentByKey.TryAdd(key, parents.Count))
                throw BusinessException.RuleConflict(
                    $"表头父身份重复：{scenario.ParentIdentityFieldKey} = {key}（重复键，拒绝组合）");

            parents.Add(new ReportConfigurationBundleComposedHeaderDto
            {
                Ordinal = parents.Count + 1,
                ParentKey = key,
                Header = headerRows[i],
            });
        }


        // 明细归组：孤儿行 / 缺失外键 / 重复明细键（外键 + 顺序）显式失败。
        var detailsByParent = new List<List<Dictionary<string, object?>>>(parents.Count);
        for (var p = 0; p < parents.Count; p++)
            detailsByParent.Add(new List<Dictionary<string, object?>>());

        var seenDetailKeys = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < detailRows.Count; i++)
        {
            lease.Token.ThrowIfCancellationRequested();
            var row = detailRows[i];
            var fk = IdentityKey(row, scenario.DetailForeignKeyFieldKey);
            if (fk is null)
                throw BusinessException.RuleConflict(
                    $"明细第 {i + 1} 行缺少外键 {scenario.DetailForeignKeyFieldKey}，无法组合（孤儿行）");

            if (!parentByKey.TryGetValue(fk, out var parentIndex))
                throw BusinessException.RuleConflict(
                    $"明细第 {i + 1} 行外键 {fk} 没有匹配的表头（孤儿行，拒绝组合）");

            var orderPart = DetailOrderKey(row, scenario);
            var detailKey = fk + "|" + orderPart;
            if (!seenDetailKeys.Add(detailKey))
                throw BusinessException.RuleConflict(
                    $"明细键重复：外键 {fk} / 顺序 {orderPart}（重复键，拒绝组合）");

            detailsByParent[parentIndex].Add(row);
        }

        var detailCount = 0;
        var cellCount = 0;
        for (var p = 0; p < parents.Count; p++)
        {
            lease.Token.ThrowIfCancellationRequested();
            var details = detailsByParent[p];
            details.Sort((a, b) => CompareDetailOrder(a, b, scenario));

            if (details.Count > ReportConfigurationBundleCompositionLimits.MaxChildrenPerParent)
                throw new BusinessException(
                    $"单个父项明细行数超出上限（{ReportConfigurationBundleCompositionLimits.MaxChildrenPerParent}）",
                    ReportConfigurationExecutionLimits.ErrorCodeResultTooLarge);

            parents[p].Details = details;
            parents[p].HasDetails = details.Count > 0;
            parents[p].EmptyDetailsEvidence = details.Count == 0
                ? "该单证没有明细行：不输出明细表、不做任何明细合计"
                : null;
            parents[p].Totals = BuildTotals(scenario, parents[p].Header, details);

            detailCount += details.Count;
            cellCount += parents[p].Header.Count;
            foreach (var detail in details)
                cellCount += detail.Count;
        }

        if (detailCount > ReportConfigurationBundleCompositionLimits.MaxTotalChildren)
            throw new BusinessException(
                $"组合明细行总数超出上限（{ReportConfigurationBundleCompositionLimits.MaxTotalChildren}）",
                ReportConfigurationExecutionLimits.ErrorCodeResultTooLarge);

        if (cellCount > ReportConfigurationBundleCompositionLimits.MaxCells)
            throw new BusinessException(
                $"组合单元格总数超出上限（{ReportConfigurationBundleCompositionLimits.MaxCells}）",
                ReportConfigurationExecutionLimits.ErrorCodeResultTooLarge);

        var result = new ReportConfigurationBundleCompositionPreviewDto
        {
            Name = "报表配置组合",
            CorrelationId = lease.CorrelationId,
            CompositionKey = scenario.Key,
            CompositionName = scenario.Name,
            HeaderTitle = ResolveCompositionTitle(request.HeaderTitle, headerPreview, request.HeaderConfigurationId),
            DetailTitle = ResolveCompositionTitle(request.DetailTitle, detailPreview, request.DetailConfigurationId),
            HeaderConfigurationId = request.HeaderConfigurationId,
            HeaderRevisionVersion = request.HeaderRevisionVersion,
            DetailConfigurationId = request.DetailConfigurationId,
            DetailRevisionVersion = request.DetailRevisionVersion,
            HeaderColumns = headerColumns.ToList(),
            DetailColumns = detailColumns.ToList(),
            Parents = parents,
            ParentCount = parents.Count,
            DetailCount = detailCount,
            CellCount = cellCount,
            CurrencyUnitSemantics = scenario.CurrencyUnitSemantics,
            ReadOnlyText = scenario.ReadOnlyText,
            BoundaryText = scenario.BoundaryText,
            DisclaimerText = scenario.DisclaimerText,
        };

        CheckSerializedCompositionBytes(result, lease.CorrelationId);
        return result;
    }


    private static void RequireField(
        IReadOnlyList<ReportConfigurationColumnDto> columns,
        string fieldKey,
        string sectionLabel)
    {
        if (columns.All(c => !string.Equals(c.Key, fieldKey, StringComparison.OrdinalIgnoreCase)))
            throw BusinessException.RuleConflict(
                $"{sectionLabel}未选择组合所需字段 {fieldKey}（不兼容组合，拒绝执行）");
    }

    private static string? IdentityKey(Dictionary<string, object?> row, string fieldKey)
    {
        if (!row.TryGetValue(fieldKey, out var value) || value is null || value is DBNull)
            return null;

        return value switch
        {
            long l => "l:" + l.ToString(CultureInfo.InvariantCulture),
            int i => "i:" + i.ToString(CultureInfo.InvariantCulture),
            short s => "i:" + s.ToString(CultureInfo.InvariantCulture),
            decimal m => "d:" + m.ToString(CultureInfo.InvariantCulture),
            double d => "d:" + d.ToString(CultureInfo.InvariantCulture),
            float f => "d:" + f.ToString(CultureInfo.InvariantCulture),
            string str => "s:" + str,
            _ => "o:" + Convert.ToString(value, CultureInfo.InvariantCulture),
        };
    }

    private static string DetailOrderKey(
        Dictionary<string, object?> row,
        ReportConfigurationBundleCompositionScenario scenario)
    {
        if (string.IsNullOrWhiteSpace(scenario.DetailOrderFieldKey))
            return string.Empty;

        if (!row.TryGetValue(scenario.DetailOrderFieldKey, out var value) || value is null || value is DBNull)
            return "\u0000";

        return value switch
        {
            long l => l.ToString(CultureInfo.InvariantCulture),
            int i => i.ToString(CultureInfo.InvariantCulture),
            short s => s.ToString(CultureInfo.InvariantCulture),
            decimal m => m.ToString(CultureInfo.InvariantCulture),
            double d => d.ToString(CultureInfo.InvariantCulture),
            string str => str,
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
        };
    }

    private static int CompareDetailOrder(
        Dictionary<string, object?> a,
        Dictionary<string, object?> b,
        ReportConfigurationBundleCompositionScenario scenario)
    {
        if (string.IsNullOrWhiteSpace(scenario.DetailOrderFieldKey))
            return 0;

        a.TryGetValue(scenario.DetailOrderFieldKey, out var av);
        b.TryGetValue(scenario.DetailOrderFieldKey, out var bv);

        if (TryDecimal(av, out var da) && TryDecimal(bv, out var db))
            return da.CompareTo(db);

        var sa = Convert.ToString(av, CultureInfo.InvariantCulture) ?? string.Empty;
        var sb = Convert.ToString(bv, CultureInfo.InvariantCulture) ?? string.Empty;
        return string.CompareOrdinal(sa, sb);
    }

    private static bool TryDecimal(object? value, out decimal number)
    {
        switch (value)
        {
            case null:
            case DBNull:
                number = 0m;
                return false;
            case decimal m:
                number = m;
                return true;
            case long l:
                number = l;
                return true;
            case int i:
                number = i;
                return true;
            case short s:
                number = s;
                return true;
            case double d:
                number = (decimal)d;
                return true;
            case float f:
                number = (decimal)f;
                return true;
            default:
                return decimal.TryParse(
                    Convert.ToString(value, CultureInfo.InvariantCulture),
                    NumberStyles.Number,
                    CultureInfo.InvariantCulture,
                    out number);
        }
    }


    private static ReportConfigurationBundleComposedTotalsDto BuildTotals(
        ReportConfigurationBundleCompositionScenario scenario,
        Dictionary<string, object?> header,
        List<Dictionary<string, object?>> details)
    {
        var totals = new ReportConfigurationBundleComposedTotalsDto
        {
            HeaderAmount = ReadDecimal(header, scenario.HeaderAmountFieldKey),
            HeaderCurrency = ReadString(header, scenario.HeaderCurrencyFieldKey),
        };

        var amountByCurrency = new Dictionary<string, (decimal Amount, int Count)>(StringComparer.Ordinal);
        var quantityByUnit = new Dictionary<string, (decimal Quantity, int Count)>(StringComparer.Ordinal);

        foreach (var detail in details)
        {
            var amount = ReadDecimal(detail, scenario.DetailAmountFieldKey);
            if (amount.HasValue)
            {
                var currency = ReadString(detail, scenario.DetailCurrencyFieldKey);
                var key = string.IsNullOrWhiteSpace(currency) ? "未知" : currency;
                var current = amountByCurrency.TryGetValue(key, out var acc) ? acc : default;
                amountByCurrency[key] = (current.Amount + amount.Value, current.Count + 1);
            }

            var quantity = ReadDecimal(detail, scenario.DetailQuantityFieldKey);
            if (quantity.HasValue)
            {
                var unit = ReadString(detail, scenario.DetailUnitFieldKey);
                var key = string.IsNullOrWhiteSpace(unit) ? "未知" : unit;
                var current = quantityByUnit.TryGetValue(key, out var acc) ? acc : default;
                quantityByUnit[key] = (current.Quantity + quantity.Value, current.Count + 1);
            }
        }

        totals.DetailAmounts = amountByCurrency
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => new ReportConfigurationBundleComposedAmountDto
            {
                Currency = kv.Key,
                Amount = kv.Value.Amount,
                Count = kv.Value.Count,
                Reason = kv.Key == "未知" ? "未知币种（明细行币种缺失）" : null,
            })
            .ToList();

        totals.DetailQuantities = quantityByUnit
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => new ReportConfigurationBundleComposedQuantityDto
            {
                Unit = kv.Key,
                Quantity = kv.Value.Quantity,
                Count = kv.Value.Count,
                Reason = kv.Key == "未知" ? "未知单位（明细行单位缺失）" : null,
            })
            .ToList();

        return totals;
    }

    private static decimal? ReadDecimal(Dictionary<string, object?> row, string fieldKey)
    {
        if (!row.TryGetValue(fieldKey, out var value) || value is null || value is DBNull)
            return null;

        return value switch
        {
            decimal m => m,
            long l => l,
            int i => i,
            short s => s,
            double d => (decimal)d,
            float f => (decimal)f,
            _ => decimal.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Number,
                CultureInfo.InvariantCulture, out var parsed) ? parsed : null,
        };
    }

    private static string? ReadString(Dictionary<string, object?> row, string fieldKey)
    {
        if (!row.TryGetValue(fieldKey, out var value) || value is null || value is DBNull)
            return null;

        return Convert.ToString(value, CultureInfo.InvariantCulture);
    }

    private static string ResolveCompositionTitle(string? requested, ReportConfigurationPreviewDto? preview, long configurationId)
    {
        var title = (requested ?? string.Empty).Trim();
        if (title.Length == 0)
            title = preview?.Name ?? string.Empty;
        if (title.Length == 0)
            title = $"报表配置 #{configurationId}";
        return title;
    }

    private static void CheckSerializedCompositionBytes(
        ReportConfigurationBundleCompositionPreviewDto result,
        string correlationId)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(result, JsonOptions).LongLength;
        if (bytes > ReportConfigurationBundleCompositionLimits.MaxSerializedCompositionPreviewBytes)
            throw new BusinessException(
                "组合预览结果过大，已拒绝返回，请缩小文档选择范围（关联ID：" + correlationId + "）",
                ReportConfigurationExecutionLimits.ErrorCodeResultTooLarge);
    }

}

