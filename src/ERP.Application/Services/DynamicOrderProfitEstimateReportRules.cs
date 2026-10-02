using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;

namespace ERP.Application.Services;

/// <summary>
/// 动态订单利润暂估报表（ERP-221）的纯规则：有限、有序字段白名单、字段 / 日期 / 分页校验、
/// 行投影与只读 / 边界 / 免责 / 页面覆盖 / 原币 / 未知依据 / 来源上限文案，以及 Excel 导出所需的公式注入转义。
/// 无数据库依赖，便于逐条单测。
/// <para>复用既有「订单利润暂估表」（order-profit）菜单授权与 <see cref="SalespersonDataScopeService"/>（ERP-097）业务员数据范围；
/// 行口径与既有 <see cref="ReportService.GetOrderProfitEstimateAsync"/>（ERP-219 / ERP-220）完全一致：
/// 销售额保留订单原币、成本 / 利润 / 利润率为未知（null，绝不回落为 0）、「当前价估算」仅作独立口径（币种未知，仅估算）。</para>
/// </summary>
public static class DynamicOrderProfitEstimateReportRules
{
    // ==================== 0. 常量 ====================

    /// <summary>预览所需的既有菜单编码（复用订单利润暂估表菜单；与 SeedData / SchemaUpgrader 同源）</summary>
    public const string RequiredMenuCode = "order-profit";

    /// <summary>预览所需菜单的中文文案（与既有菜单名一致）</summary>
    public const string RequiredMenuText = "订单利润暂估表";

    /// <summary>默认每页条数</summary>
    public const int DefaultPageSize = 20;

    /// <summary>每页条数上限（有界：单次请求最多返回这么多订单行）</summary>
    public const int MaxPageSize = 200;

    /// <summary>允许的订单日期区间最大跨度（含首尾日历日）：366 天，与既有订单利润暂估口径一致</summary>
    public const int MaxDateRangeDays = 366;

    // ==================== 1. 文案 ====================

    /// <summary>只读声明（接口与文档统一声明）</summary>
    public const string ReadOnlyText =
        "只读订单利润暂估预览：仅按选定白名单字段与有界日期 / 分页读取当前账号数据范围内的已审核销售订单证据，不新增 / 修改 / 删除任何记录";

    /// <summary>边界口径文案</summary>
    public const string BoundaryText =
        "口径：字段仅限订单利润暂估字段白名单（身份 / 日期 / 客户 / 原币 / 销售额 / 显式未知成本利润证据 / 独立当前价估算）；" +
        "日期范围含首尾最多 366 天、页码 ≥ 1、每页 1~200；结果限定在当前账号业务员数据范围（特权账号不受限）；" +
        "金额均为订单原币，绝不跨币种合计；不执行任意 SQL、不做写入";

    /// <summary>免责文案</summary>
    public const string DisclaimerText =
        "本预览为只读订单利润暂估证据：销售额保留订单原币；成本 / 利润 / 利润率为未知（无历史成本依据）；" +
        "当前价估算仅作独立口径（币种未知，仅估算），绝不推断实际利润或利润率";

    /// <summary>空页显式说明</summary>
    public const string EmptyText = "没有符合所选日期范围与数据范围的已审核销售订单（或记录已被软删除）";

    /// <summary>页面覆盖口径：仅当前页，绝不声称一次性返回全部行</summary>
    public const string PageOnlyText =
        "页面覆盖：本页仅展示当前分页内的订单证据行；Total 为范围内订单行总数（分页前，服务端派生），本页之外的行不在此展示";

    /// <summary>原币口径：金额均为订单原币，绝不跨币种合计</summary>
    public const string CurrencyContextText =
        "原币口径：金额均为订单原币，绝不跨币种合计、不折算、不默认币种、不推断汇率";

    /// <summary>未知依据口径：成本 / 利润 / 利润率为未知，当前价估算为独立口径</summary>
    public const string UnknownBasisText =
        "未知依据：成本 / 利润 / 利润率为未知（无可信历史成本依据），绝不回落为 0，也绝不减去币种未知的当前成本价；" +
        "当前价估算为独立口径（币种未知，仅估算，非历史成本）";

    /// <summary>来源上限口径：有界读取，超出即 fail closed</summary>
    public const string SourceLimitText =
        "来源上限：仅已审核、未删除、当前账号数据范围内的销售订单头；单次来源上限 500 张订单、10000 条明细，超出即 fail closed";

    /// <summary>缺失成本依据口径：null 金额显式呈现为「未知」，绝不写成数值 0，也绝不跨币种求和</summary>
    public const string MissingCostBasisText =
        "缺失成本依据：成本金额 / 利润 / 利润率为 null 时显式呈现为「未知」，绝不写成数值 0，也绝不跨币种求和";

    /// <summary>null 金额的显式展示文本（未知，而非数值 0）</summary>
    public const string UnknownAmountText = "未知";

    // ==================== 2. 字段白名单（有限、有序） ====================

    private sealed record FieldDef(
        string Key, string Label, string DataType, bool Filterable,
        Func<ReportDtos.OrderProfitItem, object?> Selector);

    private static readonly IReadOnlyList<FieldDef> Fields = new List<FieldDef>
    {
        new("orderId", "订单Id", "number", false, r => r.OrderId),
        new("customerId", "客户Id", "number", false, r => r.CustomerId),
        new("orderNo", "订单号", "text", false, r => r.OrderNo),
        new("orderDate", "订单日期", "date", false, r => r.OrderDate),
        new("customerName", "客户名称", "text", false, r => r.CustomerName),
        new("currency", "原币币种编码", "text", false, r => r.Currency),
        new("currencyLabel", "原币币种", "text", false, r => r.CurrencyLabel),
        new("salesAmount", "销售额(原币)", "number", false, r => r.SalesAmount),
        new("salesAmountLabel", "销售额口径", "text", false, r => r.SalesAmountLabel),
        new("costAmount", "成本金额", "number", false, r => r.CostAmount),
        new("profit", "利润", "number", false, r => r.Profit),
        new("profitRate", "利润率%", "number", false, r => r.ProfitRate),
        new("costEvidence", "成本证据", "text", false, r => r.CostEvidence),
        new("profitEvidence", "利润证据", "text", false, r => r.ProfitEvidence),
        new("currentPriceEstimate", "当前价估算(币种未知)", "number", false, r => r.CurrentPriceEstimate),
        new("currentPriceEstimateLabel", "当前价估算口径", "text", false, r => r.CurrentPriceEstimateLabel),
        new("currentPriceEstimateReason", "估算说明", "text", false, r => r.CurrentPriceEstimateReason),
    };

    private static readonly Dictionary<string, FieldDef> FieldByKey =
        Fields.ToDictionary(f => f.Key, StringComparer.OrdinalIgnoreCase);

    /// <summary>全部白名单字段键（目录顺序）</summary>
    public static readonly IReadOnlyList<string> AllFieldKeys =
        Fields.Select(f => f.Key).ToList();

    // ==================== 3. 目录与字段 ====================

    /// <summary>有限、有序的字段白名单目录。</summary>
    public static List<DynamicOrderProfitEstimateReportFieldDto> GetCatalog()
        => Fields.Select(f => new DynamicOrderProfitEstimateReportFieldDto(
            f.Key, f.Label, f.DataType, f.Filterable)).ToList();

    /// <summary>完整目录（含所需菜单与有界额度口径）。</summary>
    public static DynamicOrderProfitEstimateReportCatalogDto GetCatalogDto()
        => new(
            GetCatalog(),
            RequiredMenuCode,
            RequiredMenuText,
            MaxPageSize,
            ReadOnlyText,
            BoundaryText);

    /// <summary>按键取字段目录项（未知键返回 null）。</summary>
    public static DynamicOrderProfitEstimateReportFieldDto? GetField(string key)
        => FieldByKey.TryGetValue(key.Trim(), out var def)
            ? new DynamicOrderProfitEstimateReportFieldDto(def.Key, def.Label, def.DataType, def.Filterable)
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
                throw BusinessException.InvalidParameter("字段键不能为空：仅允许订单利润暂估字段白名单");

            if (!FieldByKey.TryGetValue(key, out var def))
                throw BusinessException.InvalidParameter($"未知字段「{key}」：仅允许订单利润暂估字段白名单");

            if (ordered.Contains(def.Key, StringComparer.OrdinalIgnoreCase))
                throw BusinessException.InvalidParameter($"重复字段「{key}」：同一字段只能选择一次");

            ordered.Add(def.Key);
        }

        return ordered;
    }

    /// <summary>
    /// 校验并规范化日期窗口（fail closed）：留空 = 今天；只取日期部分；结束不得早于开始；
    /// 含首尾日历日最多 <see cref="MaxDateRangeDays"/> 天，超出直接拒绝（先于任何订单读取）。
    /// </summary>
    public static (DateTime Start, DateTime End) ValidateDateRange(DateTime? start, DateTime? end)
    {
        var startDate = (start ?? DateTime.Today).Date;
        var endDate = (end ?? DateTime.Today).Date;

        if (endDate < startDate)
            throw BusinessException.InvalidParameter("订单利润暂估动态报表的结束日期不能早于开始日期");

        var inclusiveDays = (endDate - startDate).Days + 1;
        if (inclusiveDays > MaxDateRangeDays)
            throw BusinessException.InvalidParameter($"订单利润暂估动态报表的日期范围最多 {MaxDateRangeDays} 天（含首尾）");

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

    // ==================== 5. 行投影与分页（纯规则） ====================

    /// <summary>把一条订单利润暂估行映射为「选定字段 → 值」的只读行（仅含选定字段，键保持请求顺序）</summary>
    public static Dictionary<string, object?> BuildRow(
        ReportDtos.OrderProfitItem item, IReadOnlyList<string> fieldKeys)
    {
        var row = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var key in fieldKeys)
            row[key] = Select(item, key);
        return row;
    }

    /// <summary>读取指定字段的值（未知字段 fail closed）</summary>
    private static object? Select(ReportDtos.OrderProfitItem item, string key)
        => FieldByKey.TryGetValue(key.Trim(), out var def)
            ? def.Selector(item)
            : throw BusinessException.InvalidParameter($"未知字段: {key}");

    /// <summary>
    /// 把既有的订单利润暂估结果分页并投影为预览页（只读、纯映射）。稳定排序由既有
    /// <see cref="ReportService.GetOrderProfitEstimateAsync"/> 保证（OrderDate 降序、Id 降序）。
    /// <see cref="DynamicOrderProfitEstimateReportPageDto.Total"/> 为范围内订单行总数（分页前，服务端派生）；
    /// 页面覆盖 / 原币 / 未知依据 / 来源上限上下文始终返回（即使对应列被取消选择）。
    /// </summary>
    public static DynamicOrderProfitEstimateReportPageDto BuildPage(
        IReadOnlyList<ReportDtos.OrderProfitItem> items,
        IReadOnlyList<string> fieldKeys,
        int page,
        int pageSize,
        DateTime start,
        DateTime end)
    {
        var all = items ?? Array.Empty<ReportDtos.OrderProfitItem>();
        var total = all.Count;
        var skip = (page - 1) * pageSize;
        var pageItems = all.Skip(skip).Take(pageSize).ToList();

        var columns = fieldKeys.Select(k => GetField(k)!).ToList();
        var rows = pageItems.Select(i => BuildRow(i, fieldKeys)).ToList();

        var totalPages = total == 0 ? 0 : (int)Math.Ceiling(total / (double)pageSize);
        var truncated = skip + rows.Count < total;

        return new DynamicOrderProfitEstimateReportPageDto(
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
            PageOnlyText,
            CurrencyContextText,
            UnknownBasisText,
            SourceLimitText);
    }

    /// <summary>把分页上下文渲染为 Excel 口径文案（页面 / 每页条数 / 总数 / 是否截断）</summary>
    public static string BuildPageContext(int page, int pageSize, int total, int totalPages, bool truncated)
        => $"第 {page} 页 / 共 {totalPages} 页，每页 {pageSize} 条，范围内共 {total} 条"
           + (truncated ? "（本页之后仍有更多订单行）" : "（已覆盖全部匹配订单行）");

    // ==================== 6. Excel 导出（ERP-221） ====================

    /// <summary>Excel「报表口径」上下文工作表名（标注日期 / 分页 / 来源上限 / 原币 / 依据 / 页面覆盖，绝不追加跨币种金额合计）</summary>
    public const string ContextSheetName = "报表口径";

    /// <summary>上下文表「开始日期」行标签</summary>
    public const string ContextStartLabel = "开始日期";

    /// <summary>上下文表「结束日期」行标签</summary>
    public const string ContextEndLabel = "结束日期";

    /// <summary>上下文表「分页」行标签</summary>
    public const string ContextPageLabel = "分页";

    /// <summary>上下文表「来源上限」行标签</summary>
    public const string ContextSourceLimitLabel = "来源上限";

    /// <summary>上下文表「币种口径」行标签</summary>
    public const string ContextCurrencyLabel = "币种口径";

    /// <summary>上下文表「成本依据」行标签</summary>
    public const string ContextBasisLabel = "成本依据";

    /// <summary>上下文表「缺失成本依据」行标签</summary>
    public const string ContextMissingCostBasisLabel = "缺失成本依据";

    /// <summary>上下文表「页面覆盖」行标签</summary>
    public const string ContextCoverageLabel = "页面覆盖";

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

    /// <summary>
    /// 把一页预览行转成导出行：null 金额显式转为「未知」（绝不写成数值 0、绝不跨币种求和），
    /// 其余字符串做公式注入转义；数值 / 日期原样保留，由 ExcelExporter 按其类型写入对应单元格。
    /// </summary>
    public static Dictionary<string, object?> BuildExportRow(Dictionary<string, object?> row)
    {
        var export = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var kv in row)
            export[kv.Key] = kv.Value is null ? UnknownAmountText : EscapeFormulaLeading(kv.Value);
        return export;
    }
}
