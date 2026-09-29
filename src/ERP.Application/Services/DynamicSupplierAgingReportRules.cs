using ERP.Application.Common;
using ERP.Application.DTOs;

namespace ERP.Application.Services;

/// <summary>
/// 动态供应商对账与账龄报表（ERP-140）的纯规则：字段白名单（有限、只读）、字段 / 供应商 / 币种 / 发票状态 /
/// 分配状态 / 日期区间 / as-of / 关键字 / 页大小校验、行投影与只读 / 边界 / 免责文案。无数据库依赖，便于逐条单测。
/// <para>复用既有「角色 → 菜单」模块授权（<c>purchase-order</c> 采购订单菜单，见 <c>SeedData.Menus</c>）与
/// ERP-068 供应商对账与账龄工作台的权威派生（<c>SupplierReconciliationAging.ForQueryAsync</c>）；
/// 不新增任何表 / 列 / 权限模型，也不执行任何 SQL。</para>
/// <para>财务证据边界：本报表只回显发票证据行的原始持久化 / 派生字段，不计算应付余额、不做付款授权、不做结算；
/// 金额按原币呈现、不做跨币种换算或汇总，未知到期日与未知 / 无效分配证据照实保留，绝不推算或修复。</para>
/// </summary>
public static class DynamicSupplierAgingReportRules
{
    // ==================== 0. 常量 ====================

    /// <summary>预览所需的既有菜单编码（复用采购订单模块菜单；与 SeedData.Menus 同源）</summary>
    public const string RequiredMenuCode = "purchase-order";

    /// <summary>预览所需菜单的中文文案（与既有菜单名一致）</summary>
    public const string RequiredMenuText = "采购订单";

    /// <summary>默认每页条数</summary>
    public const int DefaultPageSize = 50;

    /// <summary>每页条数上限（有界：单次请求最多返回这么多发票证据）</summary>
    public const int MaxPageSize = 200;

    /// <summary>关键字长度上限（超长直接拒绝，避免全表模糊扫描）</summary>
    public const int MaxKeywordLength = 50;

    /// <summary>早于该年份的 as-of 日期视为「无效」（绝不静默兜底为当天）</summary>
    public const int MinDateYear = 1900;

    // ==================== 0.1 筛选取值（与 ERP-068 口径同源；仅在应用层重复常量，不新增第二套语义） ====================

    /// <summary>发票状态筛选：仅已登记且未作废（默认）</summary>
    public const string InvoiceStatusRecorded = "recorded";

    /// <summary>发票状态筛选：仅草稿</summary>
    public const string InvoiceStatusDraft = "draft";

    /// <summary>发票状态筛选：仅已作废</summary>
    public const string InvoiceStatusVoided = "voided";

    /// <summary>发票状态筛选：全部（草稿 + 已登记 + 已作废）</summary>
    public const string InvoiceStatusAll = "all";

    /// <summary>支持的发票状态筛选取值（超出范围一律拒绝，不静默兜底）</summary>
    public static readonly string[] SupportedInvoiceStatuses =
        { InvoiceStatusRecorded, InvoiceStatusDraft, InvoiceStatusVoided, InvoiceStatusAll };

    /// <summary>分配状态筛选：无任何持久化付款引用行</summary>
    public const string AllocationNone = "none";

    /// <summary>分配状态筛选：仅有历史 / 无效引用行</summary>
    public const string AllocationHistoricalOnly = "historical_only";

    /// <summary>分配状态筛选：部分分配</summary>
    public const string AllocationPartial = "partial";

    /// <summary>分配状态筛选：整笔分配</summary>
    public const string AllocationFull = "full";

    /// <summary>支持的分配状态筛选取值（over_allocated / unknown 只作为读取时派生状态，不提供为筛选）</summary>
    public static readonly string[] SupportedAllocationStates =
        { AllocationNone, AllocationHistoricalOnly, AllocationPartial, AllocationFull };

    // ==================== 1. 文案 ====================

    /// <summary>只读声明（接口与文档统一声明）</summary>
    public const string ReadOnlyText =
        "只读供应商对账与账龄报表预览：仅按选定白名单发票证据字段与有界筛选读取当前账号可见的供应商发票证据，不新增 / 修改 / 删除任何记录";

    /// <summary>边界口径文案</summary>
    public const string BoundaryText =
        "口径：字段仅限 ERP-068 供应商对账与账龄发票证据字段白名单；筛选仅限供应商 / 币种 / 发票状态 / 分配状态 / 开票日期 / 到期日 / as-of；"
        + "金额一律按原币分别成行、绝不跨币种合并或换算；未知到期日不计算账龄并单独成组；草稿 / 已作废与无效 / 无法确认证据保持可见且绝不并入有效合计；不执行任意 SQL、不做写入";

    /// <summary>免责文案</summary>
    public const string DisclaimerText =
        "本预览为只读派生证据：不是应付账款余额、不是法定供应商对账单、不是付款授权、不是税务申报，也不构成结算确认；"
        + "算术剩余证据 = 含税总额 − 有效已分配金额，仅在证据可确认时给出";

    // ==================== 2. 字段白名单（有限、有序；全部来自 ERP-068 发票证据行） ====================

    private sealed record FieldDef(string Key, string Label, string DataType, bool Filterable);

    private static readonly IReadOnlyList<FieldDef> Fields = new List<FieldDef>
    {
        // ---- 发票身份 ----
        new("invoiceId", "发票Id", "number", false),
        new("invoiceType", "发票类型", "text", false),
        new("invoiceTypeText", "发票类型文案", "text", false),
        new("invoiceCode", "发票代码", "text", false),
        new("invoiceNumber", "发票号码", "text", false),
        new("invoiceIdentityText", "发票身份", "text", false),
        new("invoiceDate", "开票日期", "date", true),
        // ---- 供应商 ----
        new("supplierId", "供应商Id", "number", true),
        new("supplierCode", "供应商编码", "text", false),
        new("supplierName", "供应商名称", "text", false),
        new("supplierAvailable", "供应商可用", "boolean", false),
        new("supplierAvailabilityText", "供应商可用性", "text", false),
        // ---- 币种与金额证据（原币；绝不换算、绝不跨币种合并） ----
        new("currency", "币种", "text", true),
        new("amountDecimals", "币种精度", "number", false),
        new("netAmount", "不含税金额", "number", false),
        new("taxAmount", "税额", "number", false),
        new("grossAmount", "含税总额", "number", false),
        // ---- 发票状态（草稿 / 已作废单独标注，排除在有效合计外） ----
        new("invoiceStatus", "发票状态码", "number", true),
        new("invoiceStatusText", "发票状态", "text", false),
        new("isActiveEvidence", "是否有效证据", "boolean", false),
        new("isDraft", "是否草稿", "boolean", false),
        new("isVoided", "是否已作废", "boolean", false),
        // ---- 显式到期日与账龄（未知 = 不计算、单独成组） ----
        new("dueDate", "显式到期日", "date", true),
        new("dueDateKnown", "到期日已知", "boolean", false),
        new("dueDateText", "到期日文案", "text", false),
        new("paymentTerms", "付款条件", "text", false),
        new("paymentTermsText", "付款条件文案", "text", false),
        new("agingBucket", "账龄分桶", "text", false),
        new("agingBucketText", "账龄分桶文案", "text", false),
        new("overdueDays", "逾期天数", "number", false),
        new("agingText", "账龄说明", "text", false),
        // ---- 分配证据（ERP-066 / ERP-067 复用；有效与历史严格分列） ----
        new("allocationState", "分配状态", "text", false),
        new("allocationStateText", "分配状态文案", "text", false),
        new("activeAllocatedAmount", "有效已分配金额", "number", false),
        new("activeAllocationCount", "有效引用行数", "number", false),
        new("activePaymentCount", "有效付款单数", "number", false),
        new("remainingAmount", "算术剩余证据", "number", false),
        new("remainingState", "剩余证据状态", "text", false),
        new("remainingStateText", "剩余证据状态文案", "text", false),
        new("voidedAllocationCount", "已作废引用行数", "number", false),
        new("voidedAllocationAmount", "已作废引用行金额", "number", false),
        new("invoiceInactiveAllocationCount", "发票失效引用行数", "number", false),
        new("invoiceInactiveAllocationAmount", "发票失效引用行金额", "number", false),
        new("invalidAllocationCount", "无效证据条数", "number", false),
        new("invalidAllocationAmount", "无效证据金额", "number", false),
        new("unavailableAllocationCount", "无法确认证据条数", "number", false),
        new("unavailableAllocationAmount", "无法确认证据金额", "number", false),
        new("hasAllocationHistory", "是否存在历史证据", "boolean", false),
        new("hasInvalidOrUnavailableEvidence", "是否存在无效/无法确认证据", "boolean", false),
        new("historicalEvidenceText", "历史证据文案", "text", false),
        new("note", "行级说明", "text", false),
    };

    private static readonly Dictionary<string, FieldDef> FieldByKey =
        new(StringComparer.OrdinalIgnoreCase);

    static DynamicSupplierAgingReportRules()
    {
        foreach (var field in Fields)
            FieldByKey[field.Key] = field;
    }

    /// <summary>全部白名单字段键（目录顺序）</summary>
    public static IReadOnlyList<string> AllFieldKeys { get; } = Fields.Select(f => f.Key).ToList();

    // ==================== 3. 目录与字段 ====================

    /// <summary>有限白名单字段目录（按目录顺序）</summary>
    public static List<DynamicSupplierAgingReportFieldDto> GetCatalog() => Fields.Select(ToDto).ToList();

    /// <summary>完整目录（含所需菜单与有界额度口径）</summary>
    public static DynamicSupplierAgingReportCatalogDto GetCatalogDto() => new(
        GetCatalog(),
        RequiredMenuCode,
        RequiredMenuText,
        MaxPageSize,
        ReadOnlyText,
        BoundaryText);

    /// <summary>按字段键查找目录项（大小写不敏感；未知返回 null）</summary>
    public static DynamicSupplierAgingReportFieldDto? GetField(string key)
        => FieldByKey.TryGetValue(key.Trim(), out var def) ? ToDto(def) : null;

    private static DynamicSupplierAgingReportFieldDto ToDto(FieldDef def)
        => new(def.Key, def.Label, def.DataType, def.Filterable);

    // ==================== 4. 校验与规范化（全部在源读取之前完成） ====================

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
                throw BusinessException.InvalidParameter($"未知字段: {key}");
            if (!ordered.Contains(def.Key, StringComparer.OrdinalIgnoreCase))
                ordered.Add(def.Key);
        }
        return ordered;
    }

    /// <summary>规范化供应商 Id 筛选（留空 = 不限；非正数显式拒绝，fail closed）</summary>
    public static long? NormalizeSupplierId(long? supplierId)
    {
        if (supplierId is <= 0)
            throw BusinessException.InvalidParameter($"供应商 Id 必须为正整数：{supplierId}");
        return supplierId;
    }

    /// <summary>规范化币种筛选（空 = 不过滤；非法取值显式拒绝，复用发票币种严格口径）</summary>
    public static string? NormalizeCurrency(string? currency)
        => string.IsNullOrWhiteSpace(currency)
            ? null
            : PurchaseInvoiceRules.NormalizeCurrencyStrict(currency);

    /// <summary>规范化发票状态筛选（空 = 默认 recorded；非法取值直接拒绝）</summary>
    public static string NormalizeInvoiceStatus(string? status)
    {
        var value = (status ?? string.Empty).Trim().ToLowerInvariant();
        if (value.Length == 0) return InvoiceStatusRecorded;
        if (!SupportedInvoiceStatuses.Contains(value, StringComparer.Ordinal))
        {
            throw BusinessException.InvalidParameter(
                $"发票状态无效：{status}（应为 recorded / draft / voided / all）");
        }
        return value;
    }

    /// <summary>规范化分配状态筛选（空 = 不过滤；非法取值直接拒绝）</summary>
    public static string? NormalizeAllocationState(string? state)
    {
        var value = (state ?? string.Empty).Trim().ToLowerInvariant();
        if (value.Length == 0) return null;
        if (!SupportedAllocationStates.Contains(value, StringComparer.Ordinal))
        {
            throw BusinessException.InvalidParameter(
                $"分配状态无效：{state}（应为 none / historical_only / partial / full）");
        }
        return value;
    }

    /// <summary>规范化关键字（空 = 不过滤；超长直接拒绝）</summary>
    public static string? NormalizeKeyword(string? keyword)
    {
        if (keyword is null) return null;
        var value = keyword.Trim();
        if (value.Length == 0) return null;
        if (value.Length > MaxKeywordLength)
            throw BusinessException.InvalidParameter($"关键字长度不能超过 {MaxKeywordLength} 个字符");
        return value;
    }

    /// <summary>校验日期区间（开始晚于结束 = 无效）</summary>
    public static void ValidateDateRange(DateTime? from, DateTime? to, string fieldName)
    {
        if (from.HasValue && to.HasValue && from.Value.Date > to.Value.Date)
            throw BusinessException.InvalidParameter(
                $"{fieldName}开始 {from:yyyy-MM-dd} 不能晚于结束 {to:yyyy-MM-dd}");
    }

    /// <summary>校验 as-of 日期（早于 <see cref="MinDateYear"/> = 无效，绝不静默兜底为当天）</summary>
    public static void ValidateAsOfDate(DateTime? asOfDate)
    {
        if (asOfDate is { } d && d.Date.Year < MinDateYear)
            throw BusinessException.InvalidParameter($"as-of 日期无效：{d:yyyy-MM-dd}");
    }

    /// <summary>校验每页条数（1 ~ 上限，超出直接拒绝）</summary>
    public static void ValidatePageSize(int pageSize)
    {
        if (pageSize < 1 || pageSize > MaxPageSize)
            throw BusinessException.InvalidParameter($"每页条数必须在 1~{MaxPageSize} 之间");
    }

    // ==================== 5. 行投影 ====================

    /// <summary>从整行证据字典中仅投影选定的白名单字段（保持请求顺序；缺失键按 null）</summary>
    public static Dictionary<string, object?> BuildRow(
        IReadOnlyDictionary<string, object?> source, IReadOnlyList<string> fieldKeys)
    {
        var row = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var key in fieldKeys)
            row[key] = source.TryGetValue(key, out var v) ? v : null;
        return row;
    }

    // ==================== 6. Excel 导出（ERP-142） ====================

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


