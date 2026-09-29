using ERP.Application.Common;
using ERP.Application.DTOs;

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

    // ==================== 6. Excel 导出（ERP-119） ====================

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
