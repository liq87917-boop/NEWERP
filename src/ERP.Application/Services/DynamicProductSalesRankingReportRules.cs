using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;

namespace ERP.Application.Services;

/// <summary>
/// 动态商品销量排名报表（ERP-213）的纯规则：有限、有序发货数量证据字段白名单、字段 / 日期 / Top 校验、
/// 行投影与只读 / 边界 / 免责 / 单位 / 发货证据文案，以及 Excel 导出所需的公式注入转义。无数据库依赖，便于逐条单测。
/// <para>复用既有「商品销量排名榜」菜单授权与 <see cref="SalespersonDataScopeService"/>（ERP-097）业务员数据范围；
/// 发货数量口径与既有 <see cref="ReportService.GetProductSalesRankingAsync"/>（ERP-212）完全一致（已审核销售出库的发货数量），
/// 明确排除既有「当前价估算金额」口径，绝不跨单位合计数量。</para>
/// </summary>
public static class DynamicProductSalesRankingReportRules
{
    // ==================== 0. 常量 ====================

    /// <summary>预览所需的既有菜单编码（复用商品销量排名榜菜单；与 SeedData / SchemaUpgrader 同源）</summary>
    public const string RequiredMenuCode = "product-sales-ranking";

    /// <summary>预览所需菜单的中文文案（与既有菜单名一致）</summary>
    public const string RequiredMenuText = "商品销量排名榜";

    /// <summary>默认 Top</summary>
    public const int DefaultTop = 10;

    /// <summary>Top 上限（有界：单次请求最多返回这么多排名行）</summary>
    public const int MaxTop = 200;

    /// <summary>允许的日期区间最大跨度（含首尾日历日）：366 天，与既有商品销量排名口径一致</summary>
    public const int MaxDateRangeDays = 366;

    /// <summary>单位筛选最大长度（有界文本；超长直接拒绝，绝不静默截断）</summary>
    public const int MaxUnitFilterLength = 30;

    // ==================== 0.1 分组键（ERP-216） ====================

    /// <summary>不分组（默认）</summary>
    public const string GroupNone = "none";

    /// <summary>按精确单位分组（区分大小写 / 空白，绝不合并或换算单位）</summary>
    public const string GroupUnit = "unit";

    /// <summary>空白 / 未知单位的显式桶标签</summary>
    public const string UnknownUnitLabel = "未知单位";

    // ==================== 1. 文案 ====================

    /// <summary>只读声明（接口与文档统一声明）</summary>
    public const string ReadOnlyText =
        "只读商品销量排名数量证据预览：仅按选定白名单字段与有界日期 / Top 读取当前账号数据范围内的已审核发货证据，不新增 / 修改 / 删除任何记录";

    /// <summary>边界口径文案</summary>
    public const string BoundaryText =
        "口径：字段仅限商品销量排名发货数量证据白名单（排名 / 商品Id / 编码 / 名称 / 规格 / 单位 / 发货数量），排除金额估算；" +
        "日期范围含首尾最多 366 天、Top 1~200；结果限定在当前账号业务员数据范围（特权账号不受限）；不执行任意 SQL、不做写入";

    /// <summary>免责文案</summary>
    public const string DisclaimerText =
        "本预览为只读发货数量证据：仅展示 Top 限定数量的排名行，绝不声称 Top 之外完整，也绝不跨单位合计数量；不含任何金额估算";

    /// <summary>空结果显式说明</summary>
    public const string EmptyText = "没有符合日期范围与数据范围的已审核发货证据（或记录已被软删除）";

    /// <summary>单位不兼容口径文案</summary>
    public const string UnitContextText =
        "单位不兼容：不同单位的发货数量保持独立，绝不跨单位合计数量";

    /// <summary>已审核发货证据口径文案</summary>
    public const string ApprovedShipmentText =
        "已审核销售出库（发货）证据：仅统计已审核、未删除、当前账号数据范围内的销售出库明细；发货数量按商品 / 规格 / 单位独立分桶";

    /// <summary>
    /// 按单位分组口径文案（ERP-216）：仅针对当前 Top 结果（绝非完整日期范围）；每单位独立呈现排名桶数
    /// （同一商品/规格/单位各算一桶，非唯一商品或单据数）与同单位签名数量小计；绝不跨单位合计数量、也不含金额；
    /// 空白 / 未知单位仅呈现桶数（数量为 null）。
    /// </summary>
    public const string GroupContextText =
        "分组仅针对当前 Top 结果（绝不声称完整日期范围）：每单位独立呈现排名桶数（同一商品/规格/单位各算一桶，非唯一商品或单据数）"
        + "与同单位签名数量小计；绝不跨单位合计数量、也不含金额；空白 / 未知单位仅呈现桶数（数量为空）";

    // ==================== 2. 字段白名单（有限、有序） ====================

    private sealed record FieldDef(
        string Key, string Label, string DataType, bool Filterable,
        Func<ReportDtos.ProductSalesRankItem, object?> Selector);

    private static readonly IReadOnlyList<FieldDef> Fields = new List<FieldDef>
    {
        new("rank", "排名", "number", false, r => r.Rank),
        new("productId", "商品Id", "number", false, r => r.ProductId),
        new("productCode", "商品编码", "text", false, r => r.ProductCode),
        new("productName", "商品名称", "text", false, r => r.ProductName),
        new("spec", "规格", "text", false, r => r.Spec),
        new("unit", "单位", "text", false, r => r.Unit),
        new("totalQuantity", "发货数量", "number", false, r => r.TotalQuantity),
    };

    private static readonly Dictionary<string, FieldDef> FieldByKey =
        Fields.ToDictionary(f => f.Key, StringComparer.OrdinalIgnoreCase);

    /// <summary>全部白名单字段键（目录顺序）</summary>
    public static readonly IReadOnlyList<string> AllFieldKeys =
        Fields.Select(f => f.Key).ToList();

    // ==================== 3. 目录与字段 ====================

    /// <summary>有限、有序的发货数量证据字段白名单目录。</summary>
    public static List<DynamicProductSalesRankingReportFieldDto> GetCatalog()
        => Fields.Select(f => new DynamicProductSalesRankingReportFieldDto(
            f.Key, f.Label, f.DataType, f.Filterable)).ToList();

    /// <summary>完整目录（含所需菜单与有界额度口径）。</summary>
    public static DynamicProductSalesRankingReportCatalogDto GetCatalogDto()
        => new(
            GetCatalog(),
            RequiredMenuCode,
            RequiredMenuText,
            MaxTop,
            ReadOnlyText,
            BoundaryText);

    /// <summary>按键取字段目录项（未知键返回 null）。</summary>
    public static DynamicProductSalesRankingReportFieldDto? GetField(string key)
        => FieldByKey.TryGetValue(key.Trim(), out var def)
            ? new DynamicProductSalesRankingReportFieldDto(def.Key, def.Label, def.DataType, def.Filterable)
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
                throw BusinessException.InvalidParameter("字段键不能为空：仅允许商品销量排名发货数量证据字段白名单");

            if (!FieldByKey.TryGetValue(key, out var def))
                throw BusinessException.InvalidParameter($"未知字段「{key}」：仅允许商品销量排名发货数量证据字段白名单");

            if (ordered.Contains(def.Key, StringComparer.OrdinalIgnoreCase))
                throw BusinessException.InvalidParameter($"重复字段「{key}」：同一字段只能选择一次");

            ordered.Add(def.Key);
        }

        return ordered;
    }

    /// <summary>
    /// 校验并规范化日期窗口（fail closed）：留空 = 今天；只取日期部分；结束不得早于开始；
    /// 含首尾日历日最多 <see cref="MaxDateRangeDays"/> 天，超出直接拒绝（先于任何发货数据读取）。
    /// </summary>
    public static (DateTime Start, DateTime End) ValidateDateRange(DateTime? start, DateTime? end)
    {
        var startDate = (start ?? DateTime.Today).Date;
        var endDate = (end ?? DateTime.Today).Date;

        if (endDate < startDate)
            throw BusinessException.InvalidParameter("商品销量排名动态报表的结束日期不能早于开始日期");

        var inclusiveDays = (endDate - startDate).Days + 1;
        if (inclusiveDays > MaxDateRangeDays)
            throw BusinessException.InvalidParameter($"商品销量排名动态报表的日期范围最多 {MaxDateRangeDays} 天（含首尾）");

        return (startDate, endDate);
    }

    /// <summary>Top 边界校验（fail closed）：必须在 1 ~ <see cref="MaxTop"/> 之间。</summary>
    public static int ValidateTop(int top)
    {
        if (top is < 1 or > MaxTop)
            throw BusinessException.InvalidParameter($"商品销量排名动态报表的 Top 必须在 1 到 {MaxTop} 之间");
        return top;
    }

    // ==================== 4.1 应用筛选校验与规范化（ERP-215，fail closed） ====================

    /// <summary>
    /// 规范化可选应用筛选（fail closed）：客户 Id / 商品 Id 必须为正整数、单位文本去首尾空白后最多
    /// <see cref="MaxUnitFilterLength"/> 字符且不含控制字符；非法取值直接拒绝，绝不静默丢弃或做单位换算。
    /// 三项全部留空时返回 null（表示不过滤，保持既有排名行为与稳定顺序）。
    /// </summary>
    public static ProductSalesRankingFilterDto? NormalizeFilter(ProductSalesRankingFilterDto? filter)
    {
        if (filter is null)
            return null;

        var customerId = ValidateFilterCustomerId(filter.CustomerId);
        var productId = ValidateFilterProductId(filter.ProductId);
        var unit = NormalizeUnitFilter(filter.Unit);

        if (customerId is null && productId is null && unit is null)
            return null;

        return new ProductSalesRankingFilterDto
        {
            CustomerId = customerId,
            ProductId = productId,
            Unit = unit,
        };
    }

    /// <summary>校验客户 Id 筛选（可选）：提供时必须是正整数（&gt;0），否则 fail closed 拒绝；留空 = 不过滤。</summary>
    public static long? ValidateFilterCustomerId(long? customerId)
    {
        if (customerId is <= 0)
            throw BusinessException.InvalidParameter("客户 Id 筛选必须是正整数（大于 0）");
        return customerId;
    }

    /// <summary>校验商品 Id 筛选（可选）：提供时必须是正整数（&gt;0），否则 fail closed 拒绝；留空 = 不过滤。</summary>
    public static long? ValidateFilterProductId(long? productId)
    {
        if (productId is <= 0)
            throw BusinessException.InvalidParameter("商品 Id 筛选必须是正整数（大于 0）");
        return productId;
    }

    /// <summary>规范化单位筛选（可选）：留空 / 全空白 = 不过滤；否则去首尾空白、长度有界且不含控制字符（精确匹配，绝不做单位换算）。</summary>
    public static string? NormalizeUnitFilter(string? unit)
    {
        if (string.IsNullOrWhiteSpace(unit))
            return null;

        var trimmed = unit.Trim();
        if (trimmed.Length > MaxUnitFilterLength)
            throw BusinessException.InvalidParameter($"单位筛选最多 {MaxUnitFilterLength} 个字符（收到 {trimmed.Length} 个字符）");

        if (trimmed.Any(ch => char.IsControl(ch)))
            throw BusinessException.InvalidParameter("单位筛选不能包含控制字符");

        return trimmed;
    }

    /// <summary>把已规范化的应用筛选渲染为上下文文案（客户 Id / 商品 Id / 单位）；无筛选时返回空串。</summary>
    public static string BuildFilterContext(ProductSalesRankingFilterDto? filter)
    {
        if (filter is null)
            return string.Empty;

        var parts = new List<string>();
        if (filter.CustomerId.HasValue)
            parts.Add($"客户 Id {filter.CustomerId.Value}");
        if (filter.ProductId.HasValue)
            parts.Add($"商品 Id {filter.ProductId.Value}");
        if (!string.IsNullOrEmpty(filter.Unit))
            parts.Add($"单位 {filter.Unit}");

        return parts.Count == 0 ? string.Empty : string.Join("；", parts);
    }

    // ==================== 5. 行投影与结果页（纯规则） ====================

    /// <summary>把一条排名行映射为「选定字段 → 值」的只读行（仅含选定字段，键保持请求顺序）</summary>
    public static Dictionary<string, object?> BuildRow(
        ReportDtos.ProductSalesRankItem item, IReadOnlyList<string> fieldKeys)
    {
        var row = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var key in fieldKeys)
            row[key] = Select(item, key);
        return row;
    }

    /// <summary>读取指定字段的值（未知字段 fail closed）</summary>
    private static object? Select(ReportDtos.ProductSalesRankItem item, string key)
        => FieldByKey.TryGetValue(key.Trim(), out var def)
            ? def.Selector(item)
            : throw BusinessException.InvalidParameter($"未知字段: {key}");

    // ==================== 5.1 分组汇总（ERP-216） ====================

    /// <summary>
    /// 规范化分组键（fail closed）：空 / 留空 = 不分组（none）；仅接受 none / unit（大小写不敏感）；未知取值显式拒绝（先于任何源读取）。
    /// </summary>
    public static string NormalizeGroupBy(string? groupBy)
    {
        if (string.IsNullOrWhiteSpace(groupBy))
            return GroupNone;

        var normalized = groupBy.Trim();
        if (string.Equals(normalized, GroupNone, StringComparison.OrdinalIgnoreCase)) return GroupNone;
        if (string.Equals(normalized, GroupUnit, StringComparison.OrdinalIgnoreCase)) return GroupUnit;

        throw BusinessException.InvalidParameter(
            $"无效的分组键: {groupBy}（可选：none / unit）");
    }

    /// <summary>
    /// 按精确单位分组汇总（ERP-216）：仅针对当前 Top 结果（绝非完整日期范围）。空白 / 未知单位归入独立「未知单位」桶
    /// （数量恒为 null，仅呈现排名桶数）；已知单位按精确文本（区分大小写 / 空白）独立成组，绝不合并或换算单位、
    /// 绝不跨单位合计数量、绝不包含金额。排名桶数 = 该单位内的排名行数（同一商品/规格/单位各算一桶，非唯一商品或单据数）。
    /// <para>确定排序：已知单位按签名数量小计降序、再按单位文本升序；未知单位桶恒在末尾（存在时才出现）。none / 空页返回空列表。</para>
    /// </summary>
    public static List<DynamicProductSalesRankingReportGroupDto> BuildGroups(
        IReadOnlyList<ReportDtos.ProductSalesRankItem> items, string groupBy)
    {
        var list = items ?? Array.Empty<ReportDtos.ProductSalesRankItem>();
        var normalized = NormalizeGroupBy(groupBy);
        if (normalized != GroupUnit)
            return new List<DynamicProductSalesRankingReportGroupDto>();

        var byUnit = new Dictionary<string, (int Count, decimal Total)>(StringComparer.Ordinal);
        var unknownCount = 0;
        foreach (var item in list)
        {
            if (string.IsNullOrWhiteSpace(item.Unit))
            {
                unknownCount++;
                continue;
            }

            byUnit.TryGetValue(item.Unit, out var acc);
            byUnit[item.Unit] = (acc.Count + 1, acc.Total + item.TotalQuantity);
        }

        var groups = byUnit
            .OrderByDescending(kv => kv.Value.Total)
            .ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => new DynamicProductSalesRankingReportGroupDto(
                kv.Key,
                kv.Key,
                kv.Value.Count,
                kv.Value.Total,
                false))
            .ToList();

        if (unknownCount > 0)
        {
            groups.Add(new DynamicProductSalesRankingReportGroupDto(
                string.Empty,
                UnknownUnitLabel,
                unknownCount,
                null,
                true));
        }

        return groups;
    }

    /// <summary>
    /// 把既有的商品销量排名结果投影为预览页（只读、纯映射）。稳定排序由既有
    /// <see cref="ReportService.GetProductSalesRankingAsync"/> 保证（发货数量降序 / 商品Id / 名称 / 规格 / 单位升序）。
    /// <see cref="DynamicProductSalesRankingReportPageDto.TopLimited"/> 为 true 表示返回行数已达 Top、可能存在 Top 之外更多排名。
    /// </summary>
    public static DynamicProductSalesRankingReportPageDto BuildPage(
        IReadOnlyList<ReportDtos.ProductSalesRankItem> items,
        IReadOnlyList<string> fieldKeys,
        int top,
        DateTime start,
        DateTime end,
        string filterText = "",
        string groupBy = GroupNone)
    {
        var all = items ?? Array.Empty<ReportDtos.ProductSalesRankItem>();
        var normalizedGroupBy = NormalizeGroupBy(groupBy);
        var columns = fieldKeys.Select(k => GetField(k)!).ToList();
        var rows = all.Select(i => BuildRow(i, fieldKeys)).ToList();
        var topLimited = rows.Count >= top;
        var groups = BuildGroups(all, normalizedGroupBy);

        return new DynamicProductSalesRankingReportPageDto(
            columns,
            rows,
            rows.Count,
            top,
            topLimited,
            rows.Count == 0 ? EmptyText : string.Empty,
            ReadOnlyText,
            BoundaryText,
            DisclaimerText,
            UnitContextText,
            ApprovedShipmentText,
            start,
            end,
            filterText,
            normalizedGroupBy,
            groups,
            groups.Count == 0 ? string.Empty : GroupContextText);
    }

    // ==================== 6. Excel 导出（ERP-213） ====================

    /// <summary>Excel「报表口径」上下文工作表名（标注日期窗口 / Top 限定 / 单位与发货证据口径，绝不追加金额合计）</summary>
    public const string ContextSheetName = "报表口径";

    /// <summary>上下文表「开始日期」行标签</summary>
    public const string ContextStartLabel = "开始日期";

    /// <summary>上下文表「结束日期」行标签</summary>
    public const string ContextEndLabel = "结束日期";

    /// <summary>上下文表「Top 限定」行标签</summary>
    public const string ContextTopLabel = "Top 限定";

    /// <summary>上下文表「发货证据口径」行标签</summary>
    public const string ContextApprovedShipmentLabel = "发货证据口径";

    /// <summary>上下文表「单位口径」行标签</summary>
    public const string ContextUnitLabel = "单位口径";

    /// <summary>上下文表「只读声明」行标签</summary>
    public const string ContextReadOnlyLabel = "只读声明";

    /// <summary>上下文表「筛选」行标签（ERP-215：客户 Id / 商品 Id / 单位规范化上下文）</summary>
    public const string ContextFilterLabel = "筛选";

    /// <summary>上下文表「空结果说明」行标签</summary>
    public const string ContextEmptyLabel = "空结果说明";

    /// <summary>把 Top 与是否截断渲染为上下文文案（绝不声称 Top 之外完整）</summary>
    public static string BuildTopContext(int top, bool topLimited)
        => topLimited
            ? $"Top {top} 限定结果：仅展示排名前 {top} 项；可能存在 Top 之外更多排名，绝不声称完整"
            : $"Top {top} 限定结果：本次返回 {top} 项以内，已覆盖全部匹配排名";

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
