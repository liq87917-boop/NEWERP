using ERP.Application.Common;
using ERP.Application.DTOs;

namespace ERP.Application.Services;

/// <summary>
/// 动态代理服务费月度汇总报表（ERP-181）的纯规则：有限字段白名单、字段 / 筛选 / 分页校验、
/// 行投影与只读 / 边界 / 证据口径文案。无数据库依赖，便于逐条单测。
/// <para>复用 ERP-180 的 <see cref="AgencyServiceFeeMonthlySummaryService"/> 只读服务与
/// <see cref="AgencyServiceFeeMonthlySummaryRules"/> 的分组 / 金额口径；预览只做授权与字段投影，不写库。</para>
/// </summary>
public static class DynamicAgencyServiceFeeMonthlyReportRules
{
    // ==================== 0. 常量 ====================

    /// <summary>预览所需的既有菜单编码（复用 ERP-180 客户资料菜单口径）</summary>
    public const string RequiredMenuCode = AgencyServiceFeeReconciliationRules.RequiredMenuCode;

    /// <summary>预览所需菜单的中文文案（与既有菜单名一致）</summary>
    public const string RequiredMenuText = AgencyServiceFeeReconciliationRules.RequiredMenuText;

    /// <summary>默认每页条数</summary>
    public const int DefaultPageSize = AgencyServiceFeeMonthlySummaryRules.DefaultPageSize;

    /// <summary>每页条数上限（有界：单次请求最多返回这么多「年月 + 客户 + 币种」分组）</summary>
    public const int MaxPageSize = AgencyServiceFeeMonthlySummaryRules.MaxPageSize;

    // ==================== 1. 文案（与 ERP-180 同源，保证界面 / 接口证据口径一致） ====================

    /// <summary>只读声明（与 ERP-180 同源）</summary>
    public const string ReadOnlyText = AgencyServiceFeeMonthlySummaryRules.ReadOnlyText;

    /// <summary>模块边界（与收入确认 / 应收 / 付款通知 / 税务 / 结算的分离）</summary>
    public const string BoundaryText = AgencyServiceFeeMonthlySummaryRules.BoundaryText;

    /// <summary>证据口径说明（金额与计数只是证据数字）</summary>
    public const string EvidenceOnlyText = AgencyServiceFeeMonthlySummaryRules.EvidenceOnlyText;

    /// <summary>币种隔离说明（不同币种绝不合并、绝无跨币种总额）</summary>
    public const string CurrencyIsolationText = AgencyServiceFeeMonthlySummaryRules.CurrencyIsolationText;

    /// <summary>服务期间跨月不按期间分摊说明</summary>
    public const string NoProrationText = AgencyServiceFeeMonthlySummaryRules.NoProrationText;

    // ==================== 2. 字段白名单（有限、有序） ====================

    /// <summary>字段定义：键 / 文案 / 数据类型 / 是否对应既有筛选 / 从 ERP-180 月度汇总行取值</summary>
    private sealed record FieldDef(
        string Key, string Label, string DataType, bool Filterable,
        Func<AgencyServiceFeeMonthlySummaryRow, object?> Selector);

    private static readonly IReadOnlyList<FieldDef> Fields = new List<FieldDef>
    {
        new("statementYear", "年份", "number", false, r => r.StatementYear),
        new("statementMonth", "月份", "number", false, r => r.StatementMonth),
        new("statementMonthText", "对账日期所属月份", "text", false, r => r.StatementMonthText),
        new("customerId", "客户Id", "number", true, r => r.CustomerId),
        new("customerCode", "客户编码", "text", false, r => r.CustomerCode),
        new("customerName", "客户名称", "text", false, r => r.CustomerName),
        new("currency", "币种", "text", true, r => r.Currency),
        new("amountDecimals", "币种小数位", "number", false, r => r.AmountDecimals),
        new("registeredCount", "已登记张数", "number", false, r => r.RegisteredCount),
        new("registeredTotalAmount", "已登记原币合计", "number", false, r => r.RegisteredTotalAmount),
        new("registeredTotalAmountText", "已登记原币合计（文案）", "text", false, r => r.RegisteredTotalAmountText),
        new("draftCount", "草稿张数", "number", false, r => r.DraftCount),
        new("draftTotalAmount", "草稿金额（不计入合计）", "number", false, r => r.DraftTotalAmount),
        new("draftTotalAmountText", "草稿金额文案（不计入合计）", "text", false, r => r.DraftTotalAmountText),
        new("voidedCount", "已作废张数", "number", false, r => r.VoidedCount),
        new("voidedTotalAmount", "已作废金额（不计入合计）", "number", false, r => r.VoidedTotalAmount),
        new("voidedTotalAmountText", "已作废金额文案（不计入合计）", "text", false, r => r.VoidedTotalAmountText),
        new("statementCount", "对账单总张数", "number", false, r => r.StatementCount),
    };

    private static readonly Dictionary<string, FieldDef> FieldByKey =
        Fields.ToDictionary(f => f.Key, StringComparer.OrdinalIgnoreCase);

    /// <summary>全部字段键（目录顺序）</summary>
    public static readonly IReadOnlyList<string> AllFieldKeys =
        Fields.Select(f => f.Key).ToList();

    // ==================== 3. 目录 ====================

    /// <summary>返回有限、有序的字段白名单目录。</summary>
    public static List<DynamicAgencyServiceFeeMonthlyReportFieldDto> GetCatalog()
        => Fields.Select(f => new DynamicAgencyServiceFeeMonthlyReportFieldDto(
            f.Key, f.Label, f.DataType, f.Filterable)).ToList();

    /// <summary>返回预览接口所需的完整字段目录 DTO（字段 + 菜单授权 + 有界额度 + 证据口径文案）。</summary>
    public static DynamicAgencyServiceFeeMonthlyReportCatalogDto GetCatalogDto()
        => new(
            GetCatalog(),
            RequiredMenuCode,
            RequiredMenuText,
            MaxPageSize,
            ReadOnlyText,
            BoundaryText,
            EvidenceOnlyText);

    /// <summary>按键取字段定义（未知键返回 null）。</summary>
    public static DynamicAgencyServiceFeeMonthlyReportFieldDto? GetField(string key)
        => FieldByKey.TryGetValue(key.Trim(), out var def)
            ? new DynamicAgencyServiceFeeMonthlyReportFieldDto(def.Key, def.Label, def.DataType, def.Filterable)
            : null;

    // ==================== 4. 校验与投影 ====================

    /// <summary>
    /// 规范化选定字段（fail closed）：留空返回全部白名单字段（目录顺序）；
    /// 未知键与重复键（大小写不敏感）一律拒绝，绝不静默丢弃或去重。
    /// </summary>
    public static IReadOnlyList<string> NormalizeFields(IEnumerable<string>? fields)
    {
        var requested = (fields ?? Array.Empty<string>())
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Select(f => f.Trim())
            .ToList();

        if (requested.Count == 0)
            return AllFieldKeys;

        var ordered = new List<string>();
        foreach (var key in requested)
        {
            if (!FieldByKey.TryGetValue(key, out var def))
                throw BusinessException.InvalidParameter($"未知字段「{key}」：仅允许代理服务费月度汇总字段白名单");
            if (ordered.Contains(def.Key, StringComparer.OrdinalIgnoreCase))
                throw BusinessException.InvalidParameter($"重复字段「{key}」：同一字段只能选择一次");
            ordered.Add(def.Key);
        }

        return ordered;
    }

    /// <summary>分页边界校验（fail closed）：页码必须 ≥ 1，每页条数必须在 1 ~ <see cref="MaxPageSize"/> 之间。</summary>
    public static void ValidatePageBounds(int page, int pageSize)
    {
        if (page < 1)
            throw BusinessException.InvalidParameter("页码必须从 1 开始");
        if (pageSize < 1 || pageSize > MaxPageSize)
            throw BusinessException.InvalidParameter($"每页条数必须在 1 ~ {MaxPageSize} 之间（收到 {pageSize}）");
    }

    /// <summary>
    /// 组装 ERP-180 月度汇总查询（复用既有客户 / 币种 / 对账日期筛选口径；币种非法与日期倒置由既有规则 fail closed 拒绝）。
    /// 分页已在 <see cref="ValidatePageBounds"/> 校验，此处直接透传。
    /// </summary>
    public static AgencyServiceFeeMonthlySummaryQuery BuildQuery(
        DynamicAgencyServiceFeeMonthlyReportRequest request)
    {
        var (from, to) = AgencyServiceFeeMonthlySummaryRules.NormalizeDateRange(
            request.StatementDateFrom, request.StatementDateTo);
        var customerId = AgencyServiceFeeMonthlySummaryRules.NormalizeCustomerFilter(request.CustomerId);
        var currency = AgencyServiceFeeMonthlySummaryRules.NormalizeCurrencyFilter(request.Currency);

        return new AgencyServiceFeeMonthlySummaryQuery
        {
            StatementDateFrom = from,
            StatementDateTo = to,
            CustomerId = customerId,
            Currency = currency,
            Page = request.Page,
            PageSize = request.PageSize,
        };
    }

    /// <summary>把一条 ERP-180 月度汇总行投影为「仅选定字段」的字典（键保持请求顺序）。</summary>
    public static Dictionary<string, object?> BuildRow(
        AgencyServiceFeeMonthlySummaryRow row, IReadOnlyList<string> fieldKeys)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var key in fieldKeys)
        {
            if (FieldByKey.TryGetValue(key, out var def))
                result[def.Key] = def.Selector(row);
        }

        return result;
    }
}
