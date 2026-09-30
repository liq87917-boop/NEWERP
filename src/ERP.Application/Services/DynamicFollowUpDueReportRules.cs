using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Domain.Entities;

namespace ERP.Application.Services;

/// <summary>
/// 动态跟进提醒报表（ERP-193）的纯规则：字段白名单（有限、有序）、字段 / 到期状态 / 提前天数 / 分页校验、
/// 行投影与只读 / 边界 / 免责文案。无数据库依赖，便于逐条单测。
/// <para>复用 ERP-192「跟进提醒」菜单授权与 <see cref="SalespersonDataScopeService"/>（ERP-097）业务员数据范围；
/// 到期状态口径与既有 <see cref="ReportService.GetFollowUpDueAsync"/> 完全一致（&gt;0 已逾期、=0 今日到期、&lt;0 即将到期）。</para>
/// </summary>
public static class DynamicFollowUpDueReportRules
{
    // ==================== 0. 常量 ====================

    /// <summary>预览所需的既有菜单编码（复用 ERP-192 跟进提醒菜单；与 SeedData / SchemaUpgrader 同源）</summary>
    public const string RequiredMenuCode = "follow-up-due";

    /// <summary>预览所需菜单的中文文案（与既有菜单名一致）</summary>
    public const string RequiredMenuText = "跟进提醒";

    /// <summary>默认每页条数</summary>
    public const int DefaultPageSize = 20;

    /// <summary>每页条数上限（有界：单次请求最多返回这么多跟进记录）</summary>
    public const int MaxPageSize = 200;

    /// <summary>提前天数上限（有界）</summary>
    public const int MaxAheadDays = 365;

    // ==================== 0.1 到期状态（与 ERP-192 同源） ====================

    /// <summary>已逾期（下次跟进日期早于 as-of 日期）</summary>
    public const string DueOverdue = "overdue";

    /// <summary>今日到期（下次跟进日期 == as-of 日期）</summary>
    public const string DueToday = "today";

    /// <summary>即将到期（下次跟进日期晚于 as-of 日期）</summary>
    public const string DueUpcoming = "upcoming";

    /// <summary>已逾期文案（与 ERP-192 同源）</summary>
    public const string DueOverdueText = "已逾期";

    /// <summary>今日到期文案（与 ERP-192 同源）</summary>
    public const string DueTodayText = "今日到期";

    /// <summary>即将到期文案（与 ERP-192 同源）</summary>
    public const string DueUpcomingText = "即将到期";

    // ==================== 1. 文案 ====================

    /// <summary>只读声明（接口与文档统一声明）</summary>
    public const string ReadOnlyText =
        "只读跟进提醒证据预览：仅按选定白名单字段与有界筛选读取当前账号数据范围内的客户跟进记录，不新增 / 修改 / 删除任何记录";

    /// <summary>边界口径文案</summary>
    public const string BoundaryText =
        "口径：字段仅限客户跟进记录持久化字段 + 由下次跟进日期 / as-of 日期派生的到期天数与到期状态；" +
        "筛选仅限 as-of 日期、提前天数与到期状态；结果限定在当前账号业务员数据范围（特权账号不受限）；不执行任意 SQL、不做写入";

    /// <summary>免责文案</summary>
    public const string DisclaimerText =
        "本预览为只读跟进提醒证据：到期天数 / 到期状态仅由下次跟进日期与 as-of 日期派生（已逾期 / 今日到期 / 即将到期），" +
        "不构成催收、账龄或任何会计 / 结算结论";

    /// <summary>空页显式说明</summary>
    public const string EmptyText = "没有符合筛选条件的跟进提醒证据（或记录已被软删除）";

    // ==================== 2. 字段白名单（有限、有序） ====================

    private sealed record FieldDef(
        string Key, string Label, string DataType, bool Filterable,
        Func<CustomerFollowUp, DateTime, object?> Selector);

    private static readonly IReadOnlyList<FieldDef> Fields = new List<FieldDef>
    {
        new("id", "跟进记录Id", "number", false, (f, _) => f.Id),
        new("followNo", "跟进编号", "text", false, (f, _) => f.FollowNo),
        new("followDate", "跟进日期", "date", false, (f, _) => f.FollowDate),
        new("customerId", "客户Id", "number", false, (f, _) => f.CustomerId),
        new("customerName", "客户名称", "text", false, (f, _) => f.CustomerName),
        new("followType", "跟进方式", "text", false, (f, _) => f.FollowType),
        new("contactPerson", "对接人", "text", false, (f, _) => f.ContactPerson),
        new("salesmanId", "跟进人Id", "number", false, (f, _) => f.SalesmanId),
        new("salesmanName", "跟进人姓名", "text", false, (f, _) => f.SalesmanName),
        new("subject", "跟进主题", "text", false, (f, _) => f.Subject),
        new("content", "跟进内容", "text", false, (f, _) => f.Content),
        new("result", "跟进结果", "text", false, (f, _) => f.Result),
        new("nextFollowDate", "下次跟进日期", "date", false, (f, _) => f.NextFollowDate),
        new("dueDays", "到期天数", "number", false, (f, asOf) => DueDays(f, asOf)),
        new("dueStatus", "到期状态", "text", true, (f, asOf) => DueStatusText(DueDays(f, asOf))),
        new("remark", "备注", "text", false, (f, _) => f.Remark),
    };

    private static readonly Dictionary<string, FieldDef> FieldByKey =
        Fields.ToDictionary(f => f.Key, StringComparer.OrdinalIgnoreCase);

    /// <summary>全部白名单字段键（目录顺序）</summary>
    public static readonly IReadOnlyList<string> AllFieldKeys =
        Fields.Select(f => f.Key).ToList();

    // ==================== 3. 目录与字段 ====================

    /// <summary>有限、有序的字段白名单目录。</summary>
    public static List<DynamicFollowUpDueReportFieldDto> GetCatalog()
        => Fields.Select(f => new DynamicFollowUpDueReportFieldDto(
            f.Key, f.Label, f.DataType, f.Filterable)).ToList();

    /// <summary>完整目录（含所需菜单与有界额度口径）。</summary>
    public static DynamicFollowUpDueReportCatalogDto GetCatalogDto()
        => new(
            GetCatalog(),
            RequiredMenuCode,
            RequiredMenuText,
            MaxPageSize,
            ReadOnlyText,
            BoundaryText);

    /// <summary>按键取字段目录项（未知键返回 null）。</summary>
    public static DynamicFollowUpDueReportFieldDto? GetField(string key)
        => FieldByKey.TryGetValue(key.Trim(), out var def)
            ? new DynamicFollowUpDueReportFieldDto(def.Key, def.Label, def.DataType, def.Filterable)
            : null;

    // ==================== 4. 校验与规范化（fail closed） ====================

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
                throw BusinessException.InvalidParameter($"未知字段「{key}」：仅允许跟进提醒证据字段白名单");
            if (ordered.Contains(def.Key, StringComparer.OrdinalIgnoreCase))
                throw BusinessException.InvalidParameter($"重复字段「{key}」：同一字段只能选择一次");
            ordered.Add(def.Key);
        }

        return ordered;
    }

    /// <summary>规范化到期状态筛选（空 = 不过滤；未知取值显式拒绝；大小写不敏感）</summary>
    public static string? NormalizeDueStatus(string? dueStatus)
    {
        if (string.IsNullOrWhiteSpace(dueStatus))
            return null;

        var normalized = dueStatus.Trim();
        if (string.Equals(normalized, DueOverdue, StringComparison.OrdinalIgnoreCase)) return DueOverdue;
        if (string.Equals(normalized, DueToday, StringComparison.OrdinalIgnoreCase)) return DueToday;
        if (string.Equals(normalized, DueUpcoming, StringComparison.OrdinalIgnoreCase)) return DueUpcoming;

        throw BusinessException.InvalidParameter(
            $"无效的到期状态筛选值: {dueStatus}（可选：overdue / today / upcoming）");
    }

    /// <summary>规范化 as-of 日期（留空 = 今天；只取日期部分）</summary>
    public static DateTime NormalizeAsOfDate(DateTime? asOfDate)
        => (asOfDate ?? DateTime.Today).Date;

    /// <summary>校验提前天数（0 ~ 365，超出直接拒绝）</summary>
    public static void ValidateAheadDays(int aheadDays)
    {
        if (aheadDays < 0 || aheadDays > MaxAheadDays)
            throw BusinessException.InvalidParameter($"提前天数必须在 0 到 {MaxAheadDays} 之间");
    }

    /// <summary>分页边界校验（fail closed）：页码必须 ≥ 1，每页条数必须在 1 ~ <see cref="MaxPageSize"/> 之间。</summary>
    public static void ValidatePageBounds(int page, int pageSize)
    {
        if (page < 1)
            throw BusinessException.InvalidParameter("页码必须从 1 开始");
        if (pageSize < 1 || pageSize > MaxPageSize)
            throw BusinessException.InvalidParameter($"每页条数必须在 1 ~ {MaxPageSize} 之间（收到 {pageSize}）");
    }

    // ==================== 5. 到期派生与行映射 ====================

    /// <summary>到期天数（与 ERP-192 同源：&gt;0 已逾期、=0 今日到期、&lt;0 还有几天）</summary>
    public static int DueDays(CustomerFollowUp item, DateTime asOfDate)
    {
        var next = (item.NextFollowDate ?? asOfDate).Date;
        return (asOfDate.Date - next).Days;
    }

    /// <summary>到期状态文案（与 ERP-192 同源）</summary>
    public static string DueStatusText(int dueDays)
        => dueDays > 0 ? DueOverdueText : (dueDays == 0 ? DueTodayText : DueUpcomingText);

    /// <summary>读取指定字段的值（未知字段 fail closed）</summary>
    public static object? Select(CustomerFollowUp item, string key, DateTime asOfDate)
        => FieldByKey.TryGetValue(key.Trim(), out var def)
            ? def.Selector(item, asOfDate)
            : throw BusinessException.InvalidParameter($"未知字段: {key}");

    /// <summary>把一条跟进记录映射为「选定字段 → 值」的只读行（仅含选定字段，键保持请求顺序）</summary>
    public static Dictionary<string, object?> BuildRow(
        CustomerFollowUp item, IReadOnlyList<string> fieldKeys, DateTime asOfDate)
    {
        var row = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var key in fieldKeys)
            row[key] = Select(item, key, asOfDate);
        return row;
    }

    // ==================== 6. Excel 导出（ERP-195） ====================

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
