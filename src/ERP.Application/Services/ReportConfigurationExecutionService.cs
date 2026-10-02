using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using System.Collections;
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

        // 1) 优先按所有者解析（跨所有者不影响共享判定；无授权仍 fail closed）
        var config = await _db.ReportConfigurations
            .FirstOrDefaultAsync(c => c.Id == request.ConfigurationId && !c.IsDeleted && c.OwnerUserId == ownerUserId,
                cancellationToken);

        if (config is not null)
            return await PreviewOwnedAsync(ownerUserId, request, config, cancellationToken);

        // 2) 否则按被授权人共享解析（每次重新校验授权与固定修订，绝不暴露草稿 / 其它修订 / 历史）
        return await PreviewSharedAsync(ownerUserId, request, cancellationToken);
    }

    private async Task<ReportConfigurationPreviewDto> PreviewOwnedAsync(
        long ownerUserId, ReportConfigurationPreviewRequest request, ReportConfiguration config,
        CancellationToken cancellationToken)
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
            pinnedRevision, pinnedRevision ?? config.Version, request, cancellationToken);
    }

    private async Task<ReportConfigurationPreviewDto> PreviewSharedAsync(
        long recipientUserId, ReportConfigurationPreviewRequest request, CancellationToken cancellationToken)
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
            revision.Name, revision.Version, revision.Version, request, cancellationToken);
    }

    private async Task<ReportConfigurationPreviewDto> ExecutePreviewAsync(
        long userId, long configurationId, string datasetKey, ReportConfigurationDefinition definition,
        string name, int? pinnedRevision, int version, ReportConfigurationPreviewRequest request,
        CancellationToken cancellationToken)
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

        // 分发执行（适配器内部再次走既有查询的菜单授权 + 数据范围，并保留币种 / 单位口径）
        var preview = await provider.PreviewAsync(definition, parameters, userId, cancellationToken);
        preview.ConfigurationId = configurationId;
        preview.PinnedRevisionVersion = pinnedRevision;
        preview.IsPinnedRevision = pinnedRevision.HasValue;
        preview.Name = name;
        preview.Version = version;
        preview.NormalizedFiltersText = BuildNormalizedFiltersText(definition, dataset);
        preview.DateRangeText = BuildNormalizedDateRangeText(definition, dataset);
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

