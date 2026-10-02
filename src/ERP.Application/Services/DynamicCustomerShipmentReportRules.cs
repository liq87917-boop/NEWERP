using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;

namespace ERP.Application.Services;

/// <summary>
/// 动态客户出货量证据报表（ERP-229）的纯规则：有限、有序字段白名单、字段 / 日期 / 分页校验、
/// 行投影与只读 / 边界 / 免责 / 原币 / 单位 / 未知 / 来源 / 来源上限文案，以及 Excel 导出所需的公式注入转义。
/// 无数据库依赖，便于逐条单测。
/// <para>复用既有「客户出货量统计表」（customer-shipment）菜单授权与 <see cref="SalespersonDataScopeService"/>（ERP-097）业务员数据范围；
/// 行口径与既有 <see cref="ReportService.GetCustomerShipmentStatsAsync"/>（ERP-227 / ERP-228）完全一致：
/// 客户 × 原币证据行、金额仅在已知币种下签名小计、未知币种金额为 null、精确单位分组独立呈现、绝不跨币种 / 跨单位合计。</para>
/// </summary>
public static class DynamicCustomerShipmentReportRules
{
    // ==================== 0. 常量 ====================

    /// <summary>预览所需的既有菜单编码（复用客户出货量统计表菜单；与 SeedData / SchemaUpgrader 同源）</summary>
    public const string RequiredMenuCode = "customer-shipment";

    /// <summary>预览所需菜单的中文文案（与既有菜单名一致）</summary>
    public const string RequiredMenuText = "客户出货量统计表";

    /// <summary>默认每页条数</summary>
    public const int DefaultPageSize = 20;

    /// <summary>每页条数上限（有界：单次请求最多返回这么多客户 × 原币证据行）</summary>
    public const int MaxPageSize = 200;

    /// <summary>允许的订单日期区间最大跨度（含首尾日历日）：366 天，与既有客户出货量统计口径一致</summary>
    public const int MaxDateRangeDays = 366;

    /// <summary>客户 × 原币证据行上下文标签</summary>
    public const string ContextLabel = "客户 × 原币证据行";

    /// <summary>证据依据口径：已审核销售订单证据，非实际出库 / 装柜 / 收款</summary>
    public const string SourceEvidenceBasis = "已审核销售订单证据（非实际出库/装柜/收款）";

    /// <summary>未知 / null 金额与数量的显式展示文本（未知，而非数值 0）</summary>
    public const string UnknownValueText = "未知";

    /// <summary>无单位分组证据时的显式文本</summary>
    public const string NoUnitGroupsText = "无单位分组证据";

    // ==================== 1. 文案 ====================

    /// <summary>只读声明（接口与文档统一声明）</summary>
    public const string ReadOnlyText =
        "只读客户出货量证据预览：仅按选定白名单字段与有界日期 / 分页读取当前账号数据范围内的已审核销售订单证据，不新增 / 修改 / 删除任何记录";

    /// <summary>边界口径文案</summary>
    public const string BoundaryText =
        "口径：字段仅限客户出货量证据字段白名单（客户 / 原币 / 已审核订单数 / 原币金额小计 / 已知单一单位数量 / 显式数量与来源证据）；" +
        "筛选仅限开始 / 结束日期（含首尾最多 366 天）、客户 Id（正整数）与原币币种（CNY / USD / EUR / HKD / GBP / JPY），分页页码 ≥ 1、每页 1~200；" +
        "结果限定在当前账号业务员数据范围（特权账号不受限）；金额按客户 × 原币独立小计、数量按原始精确单位分组，绝不跨币种 / 跨单位合计；不执行任意 SQL、不做写入";

    /// <summary>免责文案</summary>
    public const string DisclaimerText =
        "本预览为只读客户出货量证据：金额与数量均取自已审核、未删除销售订单头 / 明细，非实际出库 / 非实际装柜数量，非实际收款金额";

    /// <summary>空页显式说明</summary>
    public const string EmptyText = "没有符合所选日期范围与数据范围的已审核销售订单（或记录已被软删除）";

    /// <summary>页面覆盖口径：仅当前页，绝不声称一次性返回全部行</summary>
    public const string PageOnlyText =
        "页面覆盖：本页仅展示当前分页内的客户 × 原币证据行；Total 为范围内证据行总数（分页前，服务端派生），本页之外的行不在此展示";

    /// <summary>原币口径：金额按客户 × 原币独立小计，绝不跨币种合计</summary>
    public const string CurrencyContextText =
        "原币口径：金额按客户 × 原币独立小计，绝不跨币种合计、不折算、不默认币种、不推断汇率";

    /// <summary>单位口径：数量按明细原始精确单位分组，绝不归一化 / 换算 / 合并不兼容单位</summary>
    public const string UnitContextText =
        "单位口径：数量按明细原始精确单位分组，绝不归一化 / 换算 / 合并不兼容单位；旧口径数量仅在单一非空单位且明细证据完整时可知";

    /// <summary>未知口径：未知 / 无效币种与空白 / 未知单位数量以未知呈现，绝不回落为 0</summary>
    public const string UnknownContextText =
        "未知口径：未知 / 无效币种金额为未知（仅订单头计数证据）；空白 / 未知单位数量为未知（仅明细计数证据）；绝不回落为 0";

    /// <summary>来源口径：已审核销售订单证据，非实际出库 / 装柜 / 收款</summary>
    public const string SourceContextText =
        "来源证据：已审核、未删除销售订单（当前账号数据范围），非实际出库 / 装柜 / 收款证据";

    /// <summary>来源上限口径：有界读取，超出即 fail closed</summary>
    public const string SourceLimitText =
        "来源上限：仅已审核、未删除、当前账号数据范围内的销售订单头；单次来源上限 500 张订单、10000 条明细，超出即 fail closed";

    // ==================== 2. 字段白名单（有限、有序） ====================

    private sealed record FieldDef(
        string Key, string Label, string DataType, bool Filterable,
        Func<ReportDtos.CustomerShipmentItem, object?> Selector);

    private static readonly IReadOnlyList<FieldDef> Fields = new List<FieldDef>
    {
        new("customerName", "客户", "string", false, r => r.CustomerName),
        new("currency", "原币币种", "string", false, r => r.Currency),
        new("currencyLabel", "原币币种标签", "string", false, r => r.CurrencyLabel),
        new("orderCount", "已审核订单数", "number", false, r => r.OrderCount),
        new("totalAmount", "原币金额小计", "money", false, r => r.TotalAmount),
        new("currencyEvidence", "币种证据", "string", false, r => r.CurrencyEvidence),
        new("amountLabel", "金额口径", "string", false, r => r.AmountLabel),
        new("unitGroups", "单位分组", "string", false, r => FormatUnitGroups(r.UnitGroups)),
        new("totalQuantity", "已知单一单位数量", "number", false, r => r.TotalQuantity),
        new("quantityCompletenessReason", "数量完整度", "string", false, r => r.QuantityCompletenessReason),
        new("quantityLabel", "数量口径", "string", false, r => r.QuantityLabel),
    };

    // ==================== 3. 校验 / 规范化 ====================

    /// <summary>
    /// 规范化选定字段键：仅限白名单；未知 / 重复 / 空键由服务端 fail closed 拒绝。
    /// 留空返回全部白名单字段（保持目录顺序）。
    /// </summary>
    public static IReadOnlyList<string> NormalizeFields(List<string>? fields)
    {
        if (fields is null || fields.Count == 0)
            return Fields.Select(f => f.Key).ToList();

        var valid = new HashSet<string>(Fields.Select(f => f.Key), StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>(fields.Count);
        foreach (var raw in fields)
        {
            var key = raw?.Trim() ?? string.Empty;
            if (key.Length == 0 || !valid.Contains(key))
                throw BusinessException.InvalidParameter($"未知字段键：{raw ?? string.Empty}");
            if (!seen.Add(key))
                throw BusinessException.InvalidParameter($"字段键重复：{key}");
            result.Add(key);
        }

        return result;
    }

    /// <summary>
    /// 规范化日期窗口（fail closed）：只取日期部分；结束不得早于开始；含首尾最多 <see cref="MaxDateRangeDays"/> 天。
    /// </summary>
    public static (DateTime Start, DateTime End) ValidateDateRange(DateTime? start, DateTime? end)
    {
        var startDate = (start ?? DateTime.Today).Date;
        var endDate = (end ?? DateTime.Today).Date;

        if (endDate < startDate)
            throw BusinessException.InvalidParameter("客户出货量证据报表的结束日期不能早于开始日期");

        var inclusiveDays = (endDate - startDate).Days + 1;
        if (inclusiveDays > MaxDateRangeDays)
            throw BusinessException.InvalidParameter(
                $"客户出货量证据报表的日期范围最多 {MaxDateRangeDays} 天（含首尾）");

        return (startDate, endDate);
    }

    /// <summary>校验分页边界（fail closed）：页码 ≥ 1，每页 1 ~ 200。</summary>
    public static void ValidatePageBounds(int page, int pageSize)
    {
        if (page < 1)
            throw BusinessException.InvalidParameter("页码必须 ≥ 1");
        if (pageSize < 1 || pageSize > MaxPageSize)
            throw BusinessException.InvalidParameter($"每页条数必须在 1 ~ {MaxPageSize} 之间");
    }

    // ==================== 4. 目录 / 字段 ====================

    /// <summary>返回有限字段白名单目录 DTO。</summary>
    public static DynamicCustomerShipmentReportCatalogDto GetCatalogDto() => new(
        Fields.Select(f => new DynamicCustomerShipmentReportFieldDto(f.Key, f.Label, f.DataType, f.Filterable)).ToList(),
        RequiredMenuCode,
        RequiredMenuText,
        MaxPageSize,
        DefaultPageSize,
        ReadOnlyText,
        BoundaryText,
        CustomerShipmentReportFilterRules.SupportedFilterText);

    /// <summary>按键取字段定义（键不存在返回 null）。</summary>
    public static DynamicCustomerShipmentReportFieldDto? GetField(string key)
    {
        var def = Fields.FirstOrDefault(f => string.Equals(f.Key, key, StringComparison.Ordinal));
        return def is null ? null : new DynamicCustomerShipmentReportFieldDto(def.Key, def.Label, def.DataType, def.Filterable);
    }

    /// <summary>按选定字段顺序投影单行（键为字段键、值为白名单字段值；未知金额 / 数量保持 null）。</summary>
    public static Dictionary<string, object?> BuildRow(
        ReportDtos.CustomerShipmentItem item, IReadOnlyList<string> fieldKeys)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(fieldKeys);

        var row = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var key in fieldKeys)
        {
            var def = Fields.FirstOrDefault(f => string.Equals(f.Key, key, StringComparison.Ordinal))
                ?? throw BusinessException.InvalidParameter($"未知字段键：{key}");
            row[key] = def.Selector(item);
        }

        return row;
    }

    /// <summary>
    /// 把精确单位分组格式化为安全证据文本（绝不重复任何货币金额、绝不跨单位合计）：
    /// 已知单位为「单位=数量(明细条数)」，空白 / 未知单位为「未知单位=未知(明细条数)」。
    /// </summary>
    public static string FormatUnitGroups(IReadOnlyList<ReportDtos.CustomerShipmentUnitGroup>? groups)
    {
        if (groups is null || groups.Count == 0)
            return NoUnitGroupsText;

        return string.Join("; ", groups.Select(g =>
            $"{g.Unit}={g.Quantity?.ToString() ?? UnknownValueText}({g.DetailCount}条)"));
    }

    // ==================== 5. 页面装配 ====================

    /// <summary>
    /// 把有界、作用域化的客户 × 原币证据行装配为预览结果页（纯函数）：
    /// 稳定分页（来源已稳定排序）、投影选定列、派生 Total / TotalPages / PageOnly 与去重客户 / 订单上下文。
    /// </summary>
    public static DynamicCustomerShipmentReportPageDto BuildPage(
        IReadOnlyList<ReportDtos.CustomerShipmentItem> items,
        IReadOnlyList<string> fieldKeys,
        int page,
        int pageSize,
        DateTime start,
        DateTime end,
        string filterText = "")
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(fieldKeys);

        var total = items.Count;
        var totalPages = total == 0 ? 0 : (int)Math.Ceiling(total / (double)pageSize);
        var skip = (page - 1) * pageSize;
        var pageItems = items.Skip(skip).Take(pageSize).ToList();
        var truncated = skip + pageItems.Count < total;
        var pageOnly = skip > 0 || truncated;

        var columns = fieldKeys.Select(k => GetField(k)!).ToList();
        var rows = pageItems.Select(x => BuildRow(x, fieldKeys)).ToList();

        var context = new DynamicCustomerShipmentReportContextDto(
            ContextLabel,
            total,
            items.Select(x => x.CustomerId).Distinct().Count(),
            items.Sum(x => x.OrderCount),
            SourceEvidenceBasis);

        // ERP-232：在分页 / 选定列投影之前，对同一份完整有界、作用域化、已筛选列表纯派生「全匹配」汇总
        var summary = DynamicCustomerShipmentSummaryRules.BuildSummary(items);

        return new DynamicCustomerShipmentReportPageDto(
            columns,
            rows,
            total,
            page,
            pageSize,
            totalPages,
            truncated,
            pageOnly,
            PageOnlyText,
            rows.Count == 0 ? EmptyText : string.Empty,
            ReadOnlyText,
            BoundaryText,
            DisclaimerText,
            start,
            end,
            CurrencyContextText,
            UnitContextText,
            UnknownContextText,
            SourceContextText,
            SourceLimitText,
            filterText,
            context,
            summary);
    }

    // ==================== 6. Excel 导出 ====================

    /// <summary>报表口径上下文工作表名</summary>
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

    /// <summary>上下文表「单位口径」行标签</summary>
    public const string ContextUnitLabel = "单位口径";

    /// <summary>上下文表「未知口径」行标签</summary>
    public const string ContextUnknownLabel = "未知口径";

    /// <summary>上下文表「来源证据」行标签</summary>
    public const string ContextSourceLabel = "来源证据";

    /// <summary>上下文表「页面覆盖」行标签</summary>
    public const string ContextPageOnlyLabel = "页面覆盖";

    /// <summary>上下文表「只读声明」行标签</summary>
    public const string ContextReadOnlyLabel = "只读声明";

    /// <summary>上下文表「应用筛选」行标签</summary>
    public const string ContextFilterLabel = "应用筛选";

    /// <summary>上下文表「空页说明」行标签</summary>
    public const string ContextEmptyLabel = "空页说明";

    /// <summary>把预览页分页信息格式化为上下文文本（日期 / 页 / 条数 / 行总数 / 总页数）。</summary>
    public static string BuildPageContext(DynamicCustomerShipmentReportPageDto page)
    {
        ArgumentNullException.ThrowIfNull(page);
        return $"第 {page.Page} 页 · 每页 {page.PageSize} 条 · 客户×原币行总数 {page.Total} · 共 {page.TotalPages} 页";
    }

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
    /// 把一页预览行转成导出行：null 金额 / 数量显式转为「未知」（绝不写成数值 0、绝不跨币种 / 跨单位求和），
    /// 其余字符串做公式注入转义；数值 / 日期原样保留，由 ExcelExporter 按其类型写入对应单元格。
    /// </summary>
    public static Dictionary<string, object?> BuildExportRow(Dictionary<string, object?> row)
    {
        var export = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var kv in row)
            export[kv.Key] = kv.Value is null ? UnknownValueText : EscapeFormulaLeading(kv.Value);
        return export;
    }
}
