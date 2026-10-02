using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Domain.Enums;

namespace ERP.Application.Services;

/// <summary>
/// 动态报价成交率报表（ERP-206）的纯规则：有限、有序字段白名单、字段 / 日期 / 分页校验、
/// 行投影与只读 / 边界 / 免责文案，以及 Excel 导出所需的公式注入转义。无数据库依赖，便于逐条单测。
/// <para>复用既有「报价单」（quotation）菜单授权与 <see cref="SalespersonDataScopeService"/>（ERP-097）业务员数据范围；
/// 分组口径（业务员 × 原币）与既有 <see cref="ReportService.GetQuotationConversionAsync"/>（ERP-205）完全一致，
/// 金额一律为报价单原币，绝不跨币种合计、绝不推断汇率或默认币种。</para>
/// </summary>
public static class DynamicQuotationConversionReportRules
{
    // ==================== 0. 常量 ====================

    /// <summary>预览所需的既有菜单编码（复用报价单菜单；与 SeedData / SchemaUpgrader 同源）</summary>
    public const string RequiredMenuCode = "quotation";

    /// <summary>预览所需菜单的中文文案（与既有菜单名一致）</summary>
    public const string RequiredMenuText = "报价单";

    /// <summary>默认每页条数</summary>
    public const int DefaultPageSize = 20;

    /// <summary>每页条数上限（有界：单次请求最多返回这么多分桶行）</summary>
    public const int MaxPageSize = 200;

    /// <summary>允许的报价日期区间最大跨度（含首尾日历日）：366 天，与既有报价成交率口径一致</summary>
    public const int MaxDateRangeDays = 366;

    /// <summary>筛选关键字最大长度（有界）</summary>
    public const int MaxFilterKeywordLength = 80;

    /// <summary>原币币种筛选「未知币种」分桶的规范 token（显式未知桶；绝不回退为 CNY）</summary>
    public const string UnknownCurrencyFilterToken = "unknown";

    /// <summary>原币币种筛选「未知币种」分桶的展示文案（与分组口径一致）</summary>
    public const string UnknownCurrencyFilterText = "未知币种";

    // ==================== 1. 文案 ====================

    /// <summary>只读声明（接口与文档统一声明）</summary>
    public const string ReadOnlyText =
        "只读报价成交率预览：仅按选定白名单字段与有界日期窗口读取当前账号数据范围内的报价成交率分桶（业务员 × 原币），不新增 / 修改 / 删除任何记录";

    /// <summary>边界口径文案</summary>
    public const string BoundaryText =
        "口径：字段仅限报价成交率分桶字段；筛选仅限开始 / 结束日期（含首尾最多 366 天）与分页；" +
        "结果限定在当前账号业务员数据范围（特权账号不受限）；金额均为报价单原币、按业务员 × 原币分列，绝不跨币种合计；不执行任意 SQL、不做写入";

    /// <summary>免责文案</summary>
    public const string DisclaimerText =
        "本预览为只读报价成交率证据：金额均为报价单原币，不折算、不默认币种、不推断汇率；本表不构成收入确认、应收或任何会计 / 结算结论";

    /// <summary>空页显式说明</summary>
    public const string EmptyText = "没有符合所选日期范围的报价成交率数据（业务员 × 原币分桶）";

    // ==================== 2. 字段白名单（有限、有序） ====================

    private sealed record FieldDef(
        string Key, string Label, string DataType, bool Filterable,
        Func<ReportDtos.QuotationConversionItem, object?> Selector);

    private static readonly IReadOnlyList<FieldDef> Fields = new List<FieldDef>
    {
        new("salesmanName", "业务员", "text", false, r => r.SalesmanName),
        new("currency", "原币币种", "text", false, r => r.Currency),
        new("quotationCount", "有效报价数", "number", false, r => r.QuotationCount),
        new("convertedCount", "已转出数", "number", false, r => r.ConvertedCount),
        new("conversionRate", "成交率%", "number", false, r => r.ConversionRate),
        new("expiredCount", "已过期未成交", "number", false, r => r.ExpiredCount),
        new("cancelledCount", "已作废", "number", false, r => r.CancelledCount),
        new("totalAmount", "有效报价金额(原币)", "number", false, r => r.TotalAmount),
        new("convertedAmount", "已转出金额(原币)", "number", false, r => r.ConvertedAmount),
        new("avgConvertedAmount", "单笔成交均价(原币)", "number", false, r => r.AvgConvertedAmount),
    };

    private static readonly Dictionary<string, FieldDef> FieldByKey =
        Fields.ToDictionary(f => f.Key, StringComparer.OrdinalIgnoreCase);

    /// <summary>全部白名单字段键（目录顺序）</summary>
    public static readonly IReadOnlyList<string> AllFieldKeys =
        Fields.Select(f => f.Key).ToList();

    // ==================== 3. 目录与字段 ====================

    /// <summary>有限、有序的字段白名单目录。</summary>
    public static List<DynamicQuotationConversionReportFieldDto> GetCatalog()
        => Fields.Select(f => new DynamicQuotationConversionReportFieldDto(
            f.Key, f.Label, f.DataType, f.Filterable)).ToList();

    /// <summary>完整目录（含所需菜单与有界额度口径）。</summary>
    public static DynamicQuotationConversionReportCatalogDto GetCatalogDto()
        => new(
            GetCatalog(),
            RequiredMenuCode,
            RequiredMenuText,
            MaxPageSize,
            ReadOnlyText,
            BoundaryText);

    /// <summary>按键取字段目录项（未知键返回 null）。</summary>
    public static DynamicQuotationConversionReportFieldDto? GetField(string key)
        => FieldByKey.TryGetValue(key.Trim(), out var def)
            ? new DynamicQuotationConversionReportFieldDto(def.Key, def.Label, def.DataType, def.Filterable)
            : null;

    // ==================== 4. 校验与规范化（fail closed） ====================

    /// <summary>
    /// 规范化选定字段（fail closed）：留空 / null 返回全部白名单字段（目录顺序）；
    /// 未知键、重复键（大小写不敏感）与空键一律拒绝，绝不静默丢弃或去重。
    /// </summary>
    public static IReadOnlyList<string> NormalizeFields(IEnumerable<string>? fields)
    {
        if (fields is null)
            return AllFieldKeys;

        var requested = fields.Select(f => f?.Trim() ?? string.Empty).ToList();
        if (requested.Count == 0)
            return AllFieldKeys;

        var ordered = new List<string>();
        foreach (var key in requested)
        {
            if (key.Length == 0)
                throw BusinessException.InvalidParameter("字段键不能为空：仅允许报价成交率字段白名单");

            if (!FieldByKey.TryGetValue(key, out var def))
                throw BusinessException.InvalidParameter($"未知字段「{key}」：仅允许报价成交率字段白名单");

            if (ordered.Contains(def.Key, StringComparer.OrdinalIgnoreCase))
                throw BusinessException.InvalidParameter($"重复字段「{key}」：同一字段只能选择一次");

            ordered.Add(def.Key);
        }

        return ordered;
    }

    /// <summary>
    /// 校验并规范化日期窗口（fail closed）：留空 = 今天；只取日期部分；结束不得早于开始；
    /// 含首尾日历日最多 <see cref="MaxDateRangeDays"/> 天，超出直接拒绝（先于任何报价单读取）。
    /// </summary>
    public static (DateTime Start, DateTime End) ValidateDateRange(DateTime? start, DateTime? end)
    {
        var startDate = (start ?? DateTime.Today).Date;
        var endDate = (end ?? DateTime.Today).Date;

        if (endDate < startDate)
            throw BusinessException.InvalidParameter("报价成交率动态报表的结束日期不能早于开始日期");

        var inclusiveDays = (endDate - startDate).Days + 1;
        if (inclusiveDays > MaxDateRangeDays)
            throw BusinessException.InvalidParameter($"报价成交率动态报表的日期范围最多 {MaxDateRangeDays} 天（含首尾）");

        return (startDate, endDate);
    }

    /// <summary>分页边界校验（fail closed）：页码必须 ≥ 1，每页条数必须在 1 ~ <see cref="MaxPageSize"/> 之间。</summary>
    public static void ValidatePageBounds(int page, int pageSize)
    {
        if (page < 1)
            throw BusinessException.InvalidParameter("页码必须从 1 开始");

        if (pageSize < 1 || pageSize > MaxPageSize)
            throw BusinessException.InvalidParameter($"每页条数必须在 1 ~ {MaxPageSize} 之间（收到 {pageSize}）");
    }

    // ==================== 4.1 筛选校验与规范化（ERP-208，fail closed） ====================

    /// <summary>
    /// 规范化可选应用筛选（fail closed）：客户 Id 必须为正整数、业务员姓名关键字去首尾空白后最多
    /// <see cref="MaxFilterKeywordLength"/> 字符、原币币种仅接受空白（全部）/ 已知 <see cref="Currency"/> 枚举码 /
    /// 显式 <see cref="UnknownCurrencyFilterToken"/> 未知桶；非法取值直接拒绝，绝不静默丢弃或回退币种。
    /// 三项全部留空时返回 null（表示不过滤）。
    /// </summary>
    public static QuotationConversionFilterDto? NormalizeFilter(QuotationConversionFilterDto? filter)
    {
        if (filter is null)
            return null;

        var customerId = ValidateFilterCustomerId(filter.CustomerId);
        var salespersonName = NormalizeSalespersonKeyword(filter.SalespersonName);
        var currency = NormalizeCurrencyFilter(filter.Currency);

        if (customerId is null && salespersonName is null && currency is null)
            return null;

        return new QuotationConversionFilterDto
        {
            CustomerId = customerId,
            SalespersonName = salespersonName,
            Currency = currency,
        };
    }

    /// <summary>校验客户 Id 筛选（可选）：提供时必须是正整数（&gt;0），否则 fail closed 拒绝；留空 = 不过滤。</summary>
    public static long? ValidateFilterCustomerId(long? customerId)
    {
        if (customerId is <= 0)
            throw BusinessException.InvalidParameter("客户 Id 筛选必须是正整数（大于 0）");
        return customerId;
    }

    /// <summary>
    /// 规范化业务员姓名关键字（fail closed）：留空 / 全空白 = 不过滤；否则去首尾空白，长度最多
    /// <see cref="MaxFilterKeywordLength"/> 字符，超出直接拒绝。
    /// </summary>
    public static string? NormalizeSalespersonKeyword(string? keyword)
    {
        if (string.IsNullOrWhiteSpace(keyword))
            return null;

        var trimmed = keyword.Trim();
        if (trimmed.Length > MaxFilterKeywordLength)
            throw BusinessException.InvalidParameter(
                $"业务员姓名关键字筛选最多 {MaxFilterKeywordLength} 个字符（收到 {trimmed.Length} 个字符）");
        return trimmed;
    }

    /// <summary>
    /// 规范化原币币种筛选（fail closed）：留空 = 全部；已知 <see cref="Currency"/> 枚举码（大小写不敏感）归一化为枚举名；
    /// 显式未知桶（unknown / 未知币种）归一化为 <see cref="UnknownCurrencyFilterToken"/>；其余取值直接拒绝。
    /// 绝不把未知币种回退为 CNY 或任何默认币种。
    /// </summary>
    public static string? NormalizeCurrencyFilter(string? currency)
    {
        if (string.IsNullOrWhiteSpace(currency))
            return null;

        var value = currency.Trim();
        if (string.Equals(value, UnknownCurrencyFilterToken, StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, UnknownCurrencyFilterText, StringComparison.OrdinalIgnoreCase))
        {
            return UnknownCurrencyFilterToken;
        }

        if (Enum.TryParse<Currency>(value, true, out var parsed) && Enum.IsDefined(parsed))
            return parsed.ToString();

        throw BusinessException.InvalidParameter(
            $"无效的原币币种筛选: {currency}（可选：CNY / USD / EUR / HKD / GBP / JPY 或 {UnknownCurrencyFilterText}）");
    }

    /// <summary>
    /// 把已规范化的应用筛选渲染为导出上下文文案（客户 Id / 业务员关键字 / 原币币种）；无筛选时返回空串。
    /// </summary>
    public static string BuildFilterContext(QuotationConversionFilterDto? filter)
    {
        if (filter is null)
            return string.Empty;

        var parts = new List<string>();
        if (filter.CustomerId.HasValue)
            parts.Add($"客户 Id {filter.CustomerId.Value}");
        if (!string.IsNullOrEmpty(filter.SalespersonName))
            parts.Add($"业务员关键字 {filter.SalespersonName}");
        if (!string.IsNullOrEmpty(filter.Currency))
        {
            var currencyText = string.Equals(filter.Currency, UnknownCurrencyFilterToken, StringComparison.OrdinalIgnoreCase)
                ? UnknownCurrencyFilterText
                : filter.Currency;
            parts.Add($"原币币种 {currencyText}");
        }

        return parts.Count == 0 ? string.Empty : string.Join("；", parts);
    }

    // ==================== 5. 行投影与分页（纯规则） ====================

    /// <summary>把一条报价成交率分桶行映射为「选定字段 → 值」的只读行（仅含选定字段，键保持请求顺序）</summary>
    public static Dictionary<string, object?> BuildRow(
        ReportDtos.QuotationConversionItem item, IReadOnlyList<string> fieldKeys)
    {
        var row = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var key in fieldKeys)
            row[key] = Select(item, key);
        return row;
    }

    /// <summary>读取指定字段的值（未知字段 fail closed）</summary>
    private static object? Select(ReportDtos.QuotationConversionItem item, string key)
        => FieldByKey.TryGetValue(key.Trim(), out var def)
            ? def.Selector(item)
            : throw BusinessException.InvalidParameter($"未知字段: {key}");

    /// <summary>
    /// 把既有的报价成交率分桶结果分页并投影为预览页（只读、纯映射）。稳定排序由既有
    /// <see cref="ReportService.GetQuotationConversionAsync"/> 保证（成交率降序 / 报价数降序 / 业务员升序 / 币种升序）。
    /// </summary>
    public static DynamicQuotationConversionReportPageDto BuildPage(
        IReadOnlyList<ReportDtos.QuotationConversionItem> items,
        IReadOnlyList<string> fieldKeys,
        int page,
        int pageSize,
        DateTime start,
        DateTime end,
        string filterText = "")
    {
        var all = items ?? Array.Empty<ReportDtos.QuotationConversionItem>();
        var total = all.Count;
        var skip = (page - 1) * pageSize;
        var pageItems = all.Skip(skip).Take(pageSize).ToList();

        var columns = fieldKeys.Select(k => GetField(k)!).ToList();
        var rows = pageItems.Select(i => BuildRow(i, fieldKeys)).ToList();

        var totalPages = total == 0 ? 0 : (int)Math.Ceiling(total / (double)pageSize);
        var truncated = skip + rows.Count < total;

        return new DynamicQuotationConversionReportPageDto(
            columns,
            rows,
            total,
            page,
            pageSize,
            totalPages,
            truncated,
            rows.Count == 0 ? EmptyText : string.Empty,
            ReadOnlyText,
            BoundaryText,
            DisclaimerText,
            start,
            end,
            filterText,
            BuildSummary(all, fieldKeys));
    }

    // ==================== 5.1 分币种汇总（ERP-209，纯规则） ====================

    /// <summary>分币种汇总覆盖文案：显式声明汇总覆盖本次有界来源内的全部匹配分桶</summary>
    public const string CurrencySummaryCoverageText =
        "分币种汇总覆盖本次预览的全部匹配报价成交率分桶（按原币合并业务员分桶，金额均为原币，绝不跨币种合计）";

    /// <summary>可纳入分币种汇总的指标字段键（除业务员与币种外的其余白名单字段）</summary>
    private static readonly HashSet<string> CurrencySummaryMetricKeys = new(
        new[] { "quotationCount", "convertedCount", "conversionRate", "expiredCount", "cancelledCount", "totalAmount", "convertedAmount", "avgConvertedAmount" },
        StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 分币种汇总（ERP-209，纯规则）：把全部匹配分桶（ERP-208 筛选后、分页前）按规范化原币键合并，
    /// 仅合并相等规范币种键、显式保留未知币种桶，绝不跨币种合计。
    /// 成交率% = 已转出总数 ÷ 有效报价总数 × 100；单笔成交均价 = 已转出金额合计 ÷ 已转出总数
    /// （两者都基于合计值计算，绝不按业务员成交率 / 均价求平均）。
    /// 仅填充选定字段对应的汇总指标（未选定的金额 / 计数指标省略为 null）。
    /// </summary>
    public static DynamicQuotationConversionSummaryDto BuildSummary(
        IReadOnlyList<ReportDtos.QuotationConversionItem> items,
        IReadOnlyList<string> fieldKeys)
    {
        var all = items ?? Array.Empty<ReportDtos.QuotationConversionItem>();
        var selectedMetrics = fieldKeys
            .Where(k => CurrencySummaryMetricKeys.Contains(k))
            .ToList();

        var columns = new List<DynamicQuotationConversionReportFieldDto> { GetField("currency")! };
        columns.AddRange(selectedMetrics.Select(k => GetField(k)!));

        var groups = new Dictionary<string, (
            int Effective, int Converted, int Expired, int Cancelled, decimal Total, decimal ConvertedAmount)>(
            StringComparer.Ordinal);

        foreach (var item in all)
        {
            var currency = ReportService.NormalizeCurrencyCode(item.Currency);
            groups.TryGetValue(currency, out var acc);
            groups[currency] = (
                acc.Effective + item.QuotationCount,
                acc.Converted + item.ConvertedCount,
                acc.Expired + item.ExpiredCount,
                acc.Cancelled + item.CancelledCount,
                acc.Total + item.TotalAmount,
                acc.ConvertedAmount + item.ConvertedAmount);
        }

        var rows = new List<DynamicQuotationConversionCurrencySummaryDto>();
        foreach (var currency in groups.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            var acc = groups[currency];
            var conversionRate = acc.Effective == 0 ? 0m : Math.Round(acc.Converted * 100m / acc.Effective, 2);
            var avgConvertedAmount = acc.Converted == 0 ? 0m : Math.Round(acc.ConvertedAmount / acc.Converted, 2);

            var row = new DynamicQuotationConversionCurrencySummaryDto { Currency = currency };
            if (selectedMetrics.Contains("quotationCount")) row.QuotationCount = acc.Effective;
            if (selectedMetrics.Contains("convertedCount")) row.ConvertedCount = acc.Converted;
            if (selectedMetrics.Contains("conversionRate")) row.ConversionRate = conversionRate;
            if (selectedMetrics.Contains("expiredCount")) row.ExpiredCount = acc.Expired;
            if (selectedMetrics.Contains("cancelledCount")) row.CancelledCount = acc.Cancelled;
            if (selectedMetrics.Contains("totalAmount")) row.TotalAmount = acc.Total;
            if (selectedMetrics.Contains("convertedAmount")) row.ConvertedAmount = acc.ConvertedAmount;
            if (selectedMetrics.Contains("avgConvertedAmount")) row.AvgConvertedAmount = avgConvertedAmount;
            rows.Add(row);
        }

        return new DynamicQuotationConversionSummaryDto(columns, rows, rows.Count, CurrencySummaryCoverageText);
    }

    // ==================== 6. Excel 导出（ERP-206） ====================

    /// <summary>Excel「报表口径」上下文工作表名（标注日期窗口与原币口径，绝不追加跨币种金额合计）</summary>
    public const string ContextSheetName = "报表口径";

    /// <summary>上下文表「开始日期」行标签</summary>
    public const string ContextStartLabel = "开始日期";

    /// <summary>上下文表「结束日期」行标签</summary>
    public const string ContextEndLabel = "结束日期";

    /// <summary>上下文表「币种口径」行标签</summary>
    public const string ContextCurrencyLabel = "币种口径";

    /// <summary>上下文表「筛选条件」行标签（ERP-208：准确标注已应用的应用筛选）</summary>
    public const string ContextFilterLabel = "筛选条件";

    /// <summary>上下文表币种口径文案：强调金额为报价单原币、按业务员 × 原币分列、绝不跨币种合计</summary>
    public const string ContextCurrencyText =
        "金额均为报价单原币（有效报价金额 / 已转出金额 / 单笔成交均价），按业务员 × 原币分列，绝不跨币种合计、不折算、不默认币种";

    /// <summary>上下文表「只读声明」行标签</summary>
    public const string ContextReadOnlyLabel = "只读声明";

    /// <summary>上下文表「空页说明」行标签</summary>
    public const string ContextEmptyLabel = "空页说明";

    /// <summary>电子表格公式注入风险首字符（OWASP：= / + / - / @ 及制表符 / 回车 / 换行）</summary>
    private static bool IsFormulaLeadingChar(char c)
        => c is '=' or '+' or '-' or '@' or '\t' or '\r' or '\n';

    /// <summary>文本是否以电子表格公式字符开头（会触发 Excel 公式注入）</summary>
    public static bool IsFormulaLeading(string? value)
    {
        if (string.IsNullOrEmpty(value)) return false;
        return IsFormulaLeadingChar(value[0]);
    }

    /// <summary>
    /// 转义 Excel 公式前导文本：以危险字符开头的文本前缀单引号，使单元格保持字面文本、不被当作公式执行。
    /// <para>仅对字符串生效；数值 / 日期 / 布尔等类型原样返回（由 ExcelExporter 按其类型写入对应单元格）。</para>
    /// </summary>
    public static object? EscapeFormulaLeading(object? value)
    {
        if (value is string s && IsFormulaLeading(s))
            return "'" + s;
        return value;
    }

    /// <summary>把一页预览行转成导出行：对每个单元格做公式注入转义，键保持不变</summary>
    public static Dictionary<string, object?> BuildExportRow(Dictionary<string, object?> row)
    {
        var export = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var kv in row)
            export[kv.Key] = EscapeFormulaLeading(kv.Value);
        return export;
    }
}


