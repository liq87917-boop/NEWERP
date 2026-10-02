using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Domain.Enums;
using System.Globalization;

namespace ERP.Application.Services;

/// <summary>
/// 动态客户应收账款证据报表（ERP-117）的纯规则：字段白名单（有限、只读）、字段 / 币种 / 分配状态 /
/// 日期区间 / 页大小校验、行映射与只读 / 边界 / 免责文案。无数据库依赖，便于逐条单测。
/// <para>复用 ERP-074 客户应收账款对账口径（<see cref="CustomerReceivableReconciliationRules"/>）与既有「客户资料」菜单授权，
/// 以及 <see cref="SalespersonDataScopeService"/>（ERP-097）数据范围；不新增任何表 / 列 / 权限模型，也不执行任何 SQL。</para>
/// <para>口径边界：本报表只呈现 ①发票含税总额证据、②显式收款分摊证据、③算术剩余证据，并保留 known / unknown /
/// over_allocated 三种剩余证据状态；<strong>不主张</strong>权威应收账款余额、收款 / 核销结算、到期日账龄或催收状态。</para>
/// </summary>
public static class DynamicReceivableReportRules
{
    // ==================== 0. 常量 ====================

    /// <summary>预览所需的既有菜单编码（复用 ERP-074 客户资料菜单；与 SeedData.Menus 同源）</summary>
    public const string RequiredMenuCode = CustomerReceivableReconciliationRules.RequiredMenuCode;

    /// <summary>预览所需菜单的中文文案（与既有菜单名一致）</summary>
    public const string RequiredMenuText = CustomerReceivableReconciliationRules.RequiredMenuText;

    /// <summary>默认每页条数</summary>
    public const int DefaultPageSize = 20;

    /// <summary>每页条数上限（有界：单次请求最多返回这么多发票证据）</summary>
    public const int MaxPageSize = 100;

    // ==================== 0.1 分组键（ERP-120） ====================

    /// <summary>不分组（默认）</summary>
    public const string GroupNone = "none";

    /// <summary>按客户分组</summary>
    public const string GroupCustomer = "customer";

    /// <summary>按发票日期月份分组</summary>
    public const string GroupMonth = "month";

    // ==================== 1. 文案 ====================

    /// <summary>只读声明（接口与文档统一声明）</summary>
    public const string ReadOnlyText =
        "只读客户应收账款证据报表预览：仅按选定白名单字段与有界筛选读取当前账号数据范围内的客户销项发票证据与收款分摊证据，不新增 / 修改 / 删除任何记录";

    /// <summary>边界口径文案</summary>
    public const string BoundaryText =
        "口径：字段仅限发票含税总额 / 显式收款分摊 / 算术剩余证据白名单；筛选仅限客户 / 发票日期 / 币种 / 分配状态；" +
        "结果限定在当前账号业务员数据范围（特权账号不受限）；不做汇率换算、不跨币种合并、不执行任意 SQL、不做写入";

    /// <summary>免责文案</summary>
    public const string DisclaimerText =
        "本预览为只读派生证据：不构成应收账款余额、收款 / 核销结算、到期日账龄或催收结论；" +
        "算术剩余证据 = 发票含税总额 − 有效已分摊金额，仅在证据可确认时给出";

    // ==================== 2. 字段白名单（有限、有序；不含账龄 / 到期日字段） ====================

    private sealed record FieldDef(
        string Key, string Label, string DataType, bool Filterable,
        Func<CustomerReceivableReconciliationInvoiceRow, object?> Selector);

    private static readonly IReadOnlyList<FieldDef> Fields = new List<FieldDef>
    {
        new("invoiceId", "发票Id", "number", false, r => r.InvoiceId),
        new("invoiceType", "发票类型", "text", false, r => r.InvoiceType),
        new("invoiceCode", "发票代码", "text", false, r => r.InvoiceCode),
        new("invoiceNumber", "发票号码", "text", false, r => r.InvoiceNumber),
        new("identityText", "发票身份", "text", false, r => r.IdentityText),
        new("invoiceDate", "开票日期", "date", true, r => r.InvoiceDate),
        new("customerId", "客户Id", "number", true, r => r.CustomerId),
        new("customerCode", "客户编码", "text", false, r => r.CustomerCode),
        new("customerName", "客户名称", "text", false, r => r.CustomerName),
        new("currency", "币种", "text", true, r => r.Currency),
        new("amountDecimals", "币种精度", "number", false, r => r.AmountDecimals),
        new("grossAmount", "发票含税总额", "number", false, r => r.GrossAmount),
        new("status", "发票状态", "number", false, r => r.Status),
        new("statusText", "发票状态文案", "text", false, r => r.StatusText),
        new("isDraft", "是否草稿", "boolean", false, r => r.IsDraft),
        new("isVoided", "是否已作废", "boolean", false, r => r.IsVoided),
        new("isActiveEvidence", "是否有效证据", "boolean", false, r => r.IsActiveEvidence),
        new("totalRowCount", "收款分摊行总条数", "number", false, r => r.TotalRowCount),
        new("activeRowCount", "有效分摊行条数", "number", false, r => r.ActiveRowCount),
        new("activeAmount", "有效分摊行金额", "number", false, r => r.ActiveAmount),
        new("voidedRowCount", "已作废分摊行条数", "number", false, r => r.VoidedRowCount),
        new("effectiveCount", "有效分摊条数", "number", false, r => r.EffectiveCount),
        new("effectiveAmount", "有效已分摊金额", "number", false, r => r.EffectiveAmount),
        new("effectiveReceiptCount", "有效分摊收款单数", "number", false, r => r.EffectiveReceiptCount),
        new("allocationState", "分配状态", "text", true, r => r.AllocationState),
        new("allocationStateText", "分配状态文案", "text", false, r => r.AllocationStateText),
        new("remainingAmount", "算术剩余金额", "number", false, r => r.RemainingAmount),
        new("remainingState", "剩余证据状态", "text", false, r => r.RemainingState),
        new("remainingStateText", "剩余证据状态文案", "text", false, r => r.RemainingStateText),
    };

    private static readonly Dictionary<string, FieldDef> FieldByKey =
        new(StringComparer.OrdinalIgnoreCase);

    static DynamicReceivableReportRules()
    {
        foreach (var field in Fields)
            FieldByKey[field.Key] = field;
    }

    /// <summary>全部白名单字段键（目录顺序）</summary>
    public static IReadOnlyList<string> AllFieldKeys { get; } = Fields.Select(f => f.Key).ToList();

    // ==================== 3. 目录与字段 ====================

    /// <summary>有限白名单字段目录（按目录顺序）</summary>
    public static List<DynamicReceivableReportFieldDto> GetCatalog() => Fields.Select(ToDto).ToList();

    /// <summary>完整目录（含所需菜单与有界额度口径）</summary>
    public static DynamicReceivableReportCatalogDto GetCatalogDto() => new(
        GetCatalog(),
        RequiredMenuCode,
        RequiredMenuText,
        MaxPageSize,
        ReadOnlyText,
        BoundaryText);

    /// <summary>按字段键查找目录项（大小写不敏感；未知返回 null）</summary>
    public static DynamicReceivableReportFieldDto? GetField(string key)
        => FieldByKey.TryGetValue(key.Trim(), out var def) ? ToDto(def) : null;

    private static DynamicReceivableReportFieldDto ToDto(FieldDef def)
        => new(def.Key, def.Label, def.DataType, def.Filterable);

    // ==================== 4. 校验与规范化（fail closed） ====================

    /// <summary>
    /// 规范化选定字段（fail closed）：未知字段显式拒绝；空 / 留空 = 返回全部白名单字段（目录顺序）；
    /// 去重并保持请求顺序。
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
                throw BusinessException.InvalidParameter($"未知字段: {key}（仅限应收账款证据白名单字段）");
            if (!ordered.Contains(def.Key, StringComparer.OrdinalIgnoreCase))
                ordered.Add(def.Key);
        }
        return ordered;
    }

    /// <summary>校验客户 Id 筛选（非正整数 = 无效，fail closed）</summary>
    public static void ValidateCustomerId(long? customerId)
    {
        if (customerId is <= 0)
            throw BusinessException.InvalidParameter($"客户 Id 必须为正整数：{customerId}");
    }

    /// <summary>规范化币种筛选（空 = 不过滤；非法币种直接拒绝，复用 ERP-074 系统币种白名单）</summary>
    public static string? NormalizeCurrency(string? currency)
        => string.IsNullOrWhiteSpace(currency)
            ? null
            : CustomerReceivableReconciliationRules.NormalizeCurrencyStrict(currency);

    /// <summary>规范化分配状态筛选（空 = 不过滤；非法取值直接拒绝，复用 ERP-074 口径）</summary>
    public static string? NormalizeAllocationState(string? allocationState)
        => CustomerReceivableReconciliationRules.NormalizeAllocationFilter(allocationState);

    /// <summary>规范化发票状态筛选（空 = 默认 recorded；非法取值直接拒绝，复用 ERP-074 口径）</summary>
    public static string? NormalizeInvoiceStatus(string? invoiceStatus)
        => CustomerReceivableReconciliationRules.NormalizeInvoiceStatusFilter(invoiceStatus);

    /// <summary>校验发票日期区间（开始晚于结束 = 无效）</summary>
    public static void ValidateDateRange(DateTime? start, DateTime? end)
    {
        if (start.HasValue && end.HasValue && start.Value.Date > end.Value.Date)
            throw BusinessException.InvalidParameter("开始日期不能晚于结束日期");
    }

    /// <summary>校验每页条数（1 ~ 100，超出直接拒绝）</summary>
    public static void ValidatePageSize(int pageSize)
    {
        if (pageSize < 1 || pageSize > MaxPageSize)
            throw BusinessException.InvalidParameter($"每页条数必须在 1~{MaxPageSize} 之间");
    }

    // ==================== 4.1 排序（ERP-270：有限持久化排序，身份并列决断） ====================

    /// <summary>有限可排序字段键（仅原生持久化键：发票 Id / 开票日期 / 客户 Id）</summary>
    public static IReadOnlyList<string> SortableFieldKeys { get; } = new[] { "invoiceId", "invoiceDate", "customerId" };

    /// <summary>
    /// 规范化保存的排序（fail closed）：空 = 沿用既有默认口径；仅接受可排序字段与 asc / desc，非法取值显式拒绝。
    /// </summary>
    public static (string FieldKey, bool Descending) NormalizeSort(string? fieldKey, string? direction)
    {
        if (string.IsNullOrWhiteSpace(fieldKey))
            return ("invoiceId", false);

        var key = fieldKey.Trim();
        foreach (var candidate in SortableFieldKeys)
        {
            if (string.Equals(candidate, key, StringComparison.OrdinalIgnoreCase))
                return (candidate, string.Equals(direction?.Trim(), "desc", StringComparison.OrdinalIgnoreCase));
        }

        throw BusinessException.InvalidParameter($"数据集 {ReportConfigurationConstants.DatasetReceivable} 不支持的排序字段: {fieldKey}");
    }

    // ==================== 5. 行映射 ====================

    /// <summary>读取指定字段的值（未知字段 fail closed）</summary>
    public static object? Select(CustomerReceivableReconciliationInvoiceRow row, string key)
        => FieldByKey.TryGetValue(key.Trim(), out var def)
            ? def.Selector(row)
            : throw BusinessException.InvalidParameter($"未知字段: {key}");

    /// <summary>把一张发票证据行映射为「选定字段 → 值」的只读行（仅含选定字段）</summary>
    public static Dictionary<string, object?> BuildRow(
        CustomerReceivableReconciliationInvoiceRow row, IReadOnlyList<string> fieldKeys)
    {
        var mapped = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var key in fieldKeys)
            mapped[key] = Select(row, key);
        return mapped;
    }

    // ==================== 6. 分组与小计（ERP-120） ====================

    /// <summary>
    /// 规范化分组键（fail closed）：空 / 留空 = 不分组（none）；仅接受 none / customer / month（大小写不敏感）；
    /// 未知取值显式拒绝。
    /// </summary>
    public static string NormalizeGroupBy(string? groupBy)
    {
        if (string.IsNullOrWhiteSpace(groupBy))
            return GroupNone;

        var normalized = groupBy.Trim();
        if (string.Equals(normalized, GroupNone, StringComparison.OrdinalIgnoreCase)) return GroupNone;
        if (string.Equals(normalized, GroupCustomer, StringComparison.OrdinalIgnoreCase)) return GroupCustomer;
        if (string.Equals(normalized, GroupMonth, StringComparison.OrdinalIgnoreCase)) return GroupMonth;

        throw BusinessException.InvalidParameter($"无效的分组键: {groupBy}（可选：none / customer / month）");
    }

    /// <summary>
    /// 分组时补齐计算页面小计所需的字段（currency / grossAmount / effectiveAmount / remainingAmount /
    /// remainingState + 分组键字段 customerId 或 invoiceDate）。
    /// <para>空 / 未指定字段 = 返回 null（由查询层按「全部白名单字段」处理，已含所需字段）；不分组时返回 null 不改动。</para>
    /// </summary>
    public static List<string>? EnsureGroupingFields(IEnumerable<string>? fields, string groupBy)
    {
        if (groupBy == GroupNone)
            return null;

        var requested = (fields ?? Array.Empty<string>())
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Select(f => f.Trim())
            .ToList();
        if (requested.Count == 0)
            return null;

        AppendIfMissing(requested, "currency");
        AppendIfMissing(requested, "grossAmount");
        AppendIfMissing(requested, "effectiveAmount");
        AppendIfMissing(requested, "remainingAmount");
        AppendIfMissing(requested, "remainingState");
        AppendIfMissing(requested, groupBy == GroupCustomer ? "customerId" : "invoiceDate");
        return requested;
    }

    /// <summary>
    /// 从「当前预览页的只读行」（同一批有界、已授权行）计算分组页面小计（ERP-120）：
    /// 按分组键聚合，组内再按币种分开统计条数、发票含税总额与有效已分摊金额；
    /// 剩余证据仅当组内全部行都「可确认」时给出金额，任一行为 unknown / over_allocated 时该币种小计的
    /// 剩余证据标注为 unknown / over_allocated（金额为 null，绝不轧为假余额）；金额只对同币种求和，绝不跨币种相加。
    /// <para>分组键与组序均为确定性（客户按 Id 升序、月份按年月升序、币种按枚举顺序）；不分组或空页返回空列表。</para>
    /// </summary>
    public static List<DynamicReceivableReportGroupDto> BuildGroupSubtotals(
        IReadOnlyList<Dictionary<string, object?>> rows, string groupBy)
    {
        var normalized = NormalizeGroupBy(groupBy);
        if (normalized == GroupNone || rows is null || rows.Count == 0)
            return new List<DynamicReceivableReportGroupDto>();

        var buckets = new Dictionary<string, GroupBucket>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var (key, label, sortKey) = GroupBucketOf(row, normalized);
            if (!buckets.TryGetValue(key, out var bucket))
            {
                bucket = new GroupBucket { Key = key, Label = label, SortKey = sortKey };
                buckets[key] = bucket;
            }

            var currency = ReadCurrency(row);
            var gross = ReadAmount(row, "grossAmount");
            var effective = ReadAmount(row, "effectiveAmount");
            var remainingState = ReadRemainingState(row);

            if (!bucket.Currencies.TryGetValue(currency, out var acc))
                acc = new CurrencyAccumulator();
            acc.Count++;
            acc.GrossAmount += gross;
            acc.EffectiveAllocatedAmount += effective;
            acc.AbsorbRemaining(remainingState, ReadNullableAmount(row, "remainingAmount"));
            bucket.Currencies[currency] = acc;
        }

        return buckets.Values
            .OrderBy(b => b.SortKey, StringComparer.Ordinal)
            .Select(b => new DynamicReceivableReportGroupDto(
                b.Key,
                b.Label,
                b.Currencies
                    .OrderBy(kv => CurrencyOrder(kv.Key))
                    .ThenBy(kv => kv.Key, StringComparer.Ordinal)
                    .Select(kv => new DynamicReceivableReportCurrencySubtotalDto(
                        kv.Key,
                        kv.Value.Count,
                        kv.Value.GrossAmount,
                        kv.Value.EffectiveAllocatedAmount,
                        kv.Value.RemainingAmount,
                        kv.Value.RemainingState))
                    .ToList()))
            .ToList();
    }

    private sealed class GroupBucket
    {
        public string Key = string.Empty;
        public string Label = string.Empty;
        public string SortKey = string.Empty;
        public Dictionary<string, CurrencyAccumulator> Currencies = new(StringComparer.Ordinal);
    }

    /// <summary>单个分组 × 币种的聚合器：金额只对同币种求和；剩余证据采用「矛盾 / 缺口绝不回落为可确认」的确定性合并。</summary>
    private sealed class CurrencyAccumulator
    {
        public int Count;
        public decimal GrossAmount;
        public decimal EffectiveAllocatedAmount;
        public decimal? RemainingAmount;
        public string RemainingState = CustomerReceivableReconciliationRules.RemainingKnown;

        /// <summary>
        /// 合并一行剩余证据（确定性、fail closed）：over_allocated &gt; unknown &gt; known 的优先级，
        /// 一旦出现非 known 行，该币种小计的剩余证据即降级为 unknown / over_allocated 且金额为 null（绝不轧为假余额）。
        /// </summary>
        public void AbsorbRemaining(string state, decimal? amount)
        {
            if (state == CustomerReceivableReconciliationRules.RemainingOverAllocated)
            {
                RemainingState = CustomerReceivableReconciliationRules.RemainingOverAllocated;
            }
            else if (state != CustomerReceivableReconciliationRules.RemainingKnown
                     && RemainingState == CustomerReceivableReconciliationRules.RemainingKnown)
            {
                RemainingState = CustomerReceivableReconciliationRules.RemainingUnknown;
            }

            if (RemainingState == CustomerReceivableReconciliationRules.RemainingKnown)
                RemainingAmount = (RemainingAmount ?? 0m) + (amount ?? 0m);
            else
                RemainingAmount = null;
        }
    }

    private static (string Key, string Label, string SortKey) GroupBucketOf(
        Dictionary<string, object?> row, string groupBy)
    {
        if (groupBy == GroupCustomer)
        {
            var customerId = ReadLong(row, "customerId");
            return ($"customer:{customerId}", $"客户 #{customerId}",
                customerId.ToString("D19", CultureInfo.InvariantCulture));
        }

        var date = ReadDate(row, "invoiceDate");
        var yyyyMM = date.ToString("yyyy-MM", CultureInfo.InvariantCulture);
        return ($"month:{yyyyMM}", $"{date.Year}年{date.Month}月",
            date.ToString("yyyyMM", CultureInfo.InvariantCulture));
    }

    private static long ReadLong(Dictionary<string, object?> row, string key)
    {
        if (row.TryGetValue(key, out var v) && v != null)
            return Convert.ToInt64(v, CultureInfo.InvariantCulture);
        return 0L;
    }

    private static DateTime ReadDate(Dictionary<string, object?> row, string key)
    {
        if (row.TryGetValue(key, out var v) && v != null)
        {
            if (v is DateTime dt) return dt;
            return Convert.ToDateTime(v, CultureInfo.InvariantCulture);
        }
        return DateTime.MinValue;
    }

    private static string ReadCurrency(Dictionary<string, object?> row)
    {
        if (row.TryGetValue("currency", out var v) && v != null)
            return Convert.ToString(v, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
        return string.Empty;
    }

    private static decimal ReadAmount(Dictionary<string, object?> row, string key)
    {
        if (row.TryGetValue(key, out var v) && v != null)
            return Convert.ToDecimal(v, CultureInfo.InvariantCulture);
        return 0m;
    }

    private static decimal? ReadNullableAmount(Dictionary<string, object?> row, string key)
    {
        if (row.TryGetValue(key, out var v) && v != null)
            return Convert.ToDecimal(v, CultureInfo.InvariantCulture);
        return null;
    }

    private static string ReadRemainingState(Dictionary<string, object?> row)
    {
        if (row.TryGetValue("remainingState", out var v) && v != null)
            return Convert.ToString(v, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
        return CustomerReceivableReconciliationRules.RemainingUnknown;
    }

    private static int CurrencyOrder(string currency)
    {
        // 未知币种仍单独小计（绝不并入其它币种），仅排到最后。
        return Enum.TryParse<Currency>(currency, true, out var c) ? (int)c : int.MaxValue;
    }

    private static void AppendIfMissing(List<string> list, string key)
    {
        if (!list.Contains(key, StringComparer.OrdinalIgnoreCase))
            list.Add(key);
    }

    // ==================== 7. Excel 导出（ERP-119） ====================

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
    /// <para>仅对字符串生效；数值 / 日期 / 布尔等类型原样返回（由 ExcelExporter 按其类型写入数值单元格）。</para>
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
