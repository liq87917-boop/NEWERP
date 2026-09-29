using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Domain.Enums;

namespace ERP.Application.Services;

/// <summary>
/// 动态销售订单出货 / 财务进度报表（ERP-156）的纯规则：字段白名单（有限、只读）、字段 / 客户 / 币种 / 订单日期 /
/// 出货状态 / 收款链接状态 / 页大小校验、行投影与只读 / 边界 / 免责文案。无数据库依赖，便于逐条单测。
/// <para>复用既有「角色 → 菜单」模块授权（<c>sales-order</c> 销售订单菜单，见 <c>SeedData.Menus</c>）与
/// <see cref="SalespersonDataScopeService"/>（ERP-097）数据范围，并复用 ERP-032 销售订单出货 / 财务进度报表的权威派生；
/// 不新增任何表 / 列 / 权限模型，也不执行任何 SQL。</para>
/// <para>财务证据边界：本预览只回显 ERP-032 订单证据行的原始持久化 / 派生字段，不计算应收余额、不做收款授权、不做结算；
/// 金额按原币呈现、不做跨币种换算或汇总，未知金额与未知数量照实保留（null），绝不推算或修复。</para>
/// </summary>
public static class DynamicShipmentFinanceReportRules
{
    // ==================== 0. 常量 ====================

    /// <summary>预览所需的既有菜单编码（复用销售订单模块菜单；与 SeedData.Menus 同源）</summary>
    public const string RequiredMenuCode = "sales-order";

    /// <summary>预览所需菜单的中文文案（与既有菜单名一致）</summary>
    public const string RequiredMenuText = "销售订单";

    /// <summary>默认每页条数</summary>
    public const int DefaultPageSize = 50;

    /// <summary>每页条数上限（有界：单次请求最多返回这么多订单）</summary>
    public const int MaxPageSize = 200;

    // ==================== 0.1 筛选取值（与 ERP-032 口径同源） ====================

    /// <summary>出货状态筛选：无「以本单为来源、未删除、已审核」的销售出库单</summary>
    public const string ShipmentStatusNone = "none";

    /// <summary>出货状态筛选：存在「以本单为来源、未删除、已审核」的销售出库单</summary>
    public const string ShipmentStatusShipped = "shipped";

    /// <summary>支持的出货状态筛选取值（超出范围一律拒绝，不静默兜底）</summary>
    public static readonly string[] SupportedShipmentStatuses = { ShipmentStatusNone, ShipmentStatusShipped };

    /// <summary>收款链接状态筛选：权威引用完整</summary>
    public const string FinanceStatusLinked = "linked";

    /// <summary>收款链接状态筛选：部分可归属（他币种 / 未审核 / 非已审核记录仅列出）</summary>
    public const string FinanceStatusPartial = "partial";

    /// <summary>收款链接状态筛选：无可用权威引用（金额未知，不推断）</summary>
    public const string FinanceStatusUnlinked = "unlinked";

    /// <summary>支持的收款链接状态筛选取值（超出范围一律拒绝，不静默兜底）</summary>
    public static readonly string[] SupportedFinanceLinkStatuses =
        { FinanceStatusLinked, FinanceStatusPartial, FinanceStatusUnlinked };

    // ==================== 1. 文案 ====================

    /// <summary>只读声明（接口与文档统一声明）</summary>
    public const string ReadOnlyText =
        "只读销售订单出货 / 财务进度报表预览：仅按选定白名单字段与有界筛选读取当前账号数据范围内的销售订单出货与收款链接证据，不新增 / 修改 / 删除任何记录";

    /// <summary>边界口径文案</summary>
    public const string BoundaryText =
        "口径：字段仅限 ERP-032 销售订单出货 / 财务进度证据字段白名单；筛选仅限客户 / 币种 / 订单日期 / 出货状态 / 收款链接状态；" +
        "结果限定在当前账号业务员数据范围（特权账号不受限）；金额按原币分别成行、绝不跨币种合并或换算；未知金额与未知数量照实保留（null）；" +
        "不执行任意 SQL、不做写入";

    /// <summary>免责文案</summary>
    public const string DisclaimerText =
        "本预览为只读派生证据：不是应收账款台账、不是账龄表、不是收款授权或结算结果；" +
        "「未覆盖金额」只是订单金额与权威计入金额之差，不得当作应收余额或据以催收";

    // ==================== 2. 字段白名单（有限、有序；全部来自 ERP-032 订单证据行） ====================

    private sealed record FieldDef(string Key, string Label, string DataType, bool Filterable);

    private static readonly IReadOnlyList<FieldDef> Fields = new List<FieldDef>
    {
        // ---- 订单身份 ----
        new("orderId", "订单Id", "number", false),
        new("orderNo", "订单号", "text", false),
        new("orderDate", "订单日期", "date", true),
        new("status", "单据状态", "text", false),
        // ---- 客户 ----
        new("customerId", "客户Id", "number", true),
        new("customerName", "客户名称", "text", false),
        // ---- 币种（原币；绝不换算、绝不跨币种合并） ----
        new("currency", "币种", "text", true),
        // ---- 订单金额（已落库，报表不重算） ----
        new("orderAmount", "订单金额", "number", false),
        new("recordedDepositAmount", "已登记定金金额", "number", false),
        // ---- 出货数量与状态（未知用 null，绝不回落为 0） ----
        new("orderedQuantity", "订单数量", "number", false),
        new("shippedQuantity", "已出货数量", "number", false),
        new("pendingShipmentQuantity", "待审核出库数量", "number", false),
        new("outstandingQuantity", "未出货数量", "number", false),
        new("shipmentStatus", "出货状态", "text", true),
        new("hasApprovedShipment", "存在已审核出库单", "boolean", false),
        new("shipmentDocumentCount", "出库单张数", "number", false),
        new("approvedShipmentCount", "已审核出库单张数", "number", false),
        // ---- 收款链接状态与金额（未知用 null，绝不回落为 0） ----
        new("financeLinkStatus", "收款链接状态", "text", true),
        new("financeLinkReason", "收款链接状态说明", "text", false),
        new("linkedAmount", "已关联金额", "number", false),
        new("uncoveredAmount", "未覆盖金额", "number", false),
        new("submittedAmount", "已提交 / 待提交金额", "number", false),
        new("otherCurrencyRecordCount", "他币种记录数", "number", false),
        new("unapprovedRecordCount", "非已审核记录数", "number", false),
        new("unattributedRecordCount", "无法归属记录数", "number", false),
        new("overReceived", "是否超收", "boolean", false),
        // ---- 行级说明 ----
        new("note", "说明", "text", false),
    };

    private static readonly IReadOnlyDictionary<string, FieldDef> FieldByKey =
        Fields.ToDictionary(f => f.Key, StringComparer.OrdinalIgnoreCase);

    private static readonly string[] AllFieldKeys = Fields.Select(f => f.Key).ToArray();

    // ==================== 3. 字段目录 ====================

    /// <summary>完整字段目录（仅白名单，有序）</summary>
    public static List<DynamicShipmentFinanceReportFieldDto> GetCatalog()
        => Fields.Select(f => new DynamicShipmentFinanceReportFieldDto(f.Key, f.Label, f.DataType, f.Filterable)).ToList();

    /// <summary>完整目录（含所需菜单与有界额度口径）</summary>
    public static DynamicShipmentFinanceReportCatalogDto GetCatalogDto() => new(
        GetCatalog(),
        RequiredMenuCode,
        RequiredMenuText,
        MaxPageSize,
        ReadOnlyText,
        BoundaryText);

    /// <summary>按字段键查找目录项（大小写不敏感；未知返回 null）</summary>
    public static DynamicShipmentFinanceReportFieldDto? GetField(string key)
        => FieldByKey.TryGetValue(key.Trim(), out var def)
            ? new DynamicShipmentFinanceReportFieldDto(def.Key, def.Label, def.DataType, def.Filterable)
            : null;

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

    /// <summary>规范化客户 Id 筛选（留空 = 不限；非正数显式拒绝，fail closed）</summary>
    public static long? NormalizeCustomerId(long? customerId)
    {
        if (customerId is <= 0)
            throw BusinessException.InvalidParameter($"客户 Id 必须为正整数：{customerId}");
        return customerId;
    }

    /// <summary>规范化币种筛选（空 = 不过滤；非法取值显式拒绝，复用 ERP-032 的严格币种口径）</summary>
    public static string? NormalizeCurrency(string? currency)
    {
        if (string.IsNullOrWhiteSpace(currency))
            return null;
        if (!Enum.TryParse<Currency>(currency.Trim(), true, out var parsed) || !Enum.IsDefined(parsed))
            throw BusinessException.InvalidParameter(
                $"币种无效：{currency}（应为 CNY / USD / EUR / HKD / GBP / JPY）");
        return parsed.ToString();
    }

    /// <summary>规范化出货状态筛选（空 = 不过滤；非法取值直接拒绝，复用 ERP-032 的 none / shipped 口径）</summary>
    public static string? NormalizeShipmentStatus(string? shipmentStatus)
    {
        if (string.IsNullOrWhiteSpace(shipmentStatus))
            return null;
        var value = shipmentStatus.Trim().ToLowerInvariant();
        if (!SupportedShipmentStatuses.Contains(value, StringComparer.Ordinal))
            throw BusinessException.InvalidParameter(
                $"出货状态无效：{shipmentStatus}（应为 none / shipped）");
        return value;
    }

    /// <summary>规范化收款链接状态筛选（空 = 不过滤；非法取值直接拒绝，复用 ERP-032 的 linked / partial / unlinked 口径）</summary>
    public static string? NormalizeFinanceLinkStatus(string? financeLinkStatus)
    {
        if (string.IsNullOrWhiteSpace(financeLinkStatus))
            return null;
        var value = financeLinkStatus.Trim().ToLowerInvariant();
        if (!SupportedFinanceLinkStatuses.Contains(value, StringComparer.Ordinal))
            throw BusinessException.InvalidParameter(
                $"收款链接状态无效：{financeLinkStatus}（应为 linked / partial / unlinked）");
        return value;
    }

    /// <summary>校验订单日期区间（开始晚于结束 = 无效）</summary>
    public static void ValidateDateRange(DateTime? from, DateTime? to)
    {
        if (from.HasValue && to.HasValue && from.Value.Date > to.Value.Date)
            throw BusinessException.InvalidParameter(
                $"订单日期开始 {from:yyyy-MM-dd} 不能晚于结束 {to:yyyy-MM-dd}");
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

    // ==================== 6. Excel 导出（ERP-158） ====================

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

    /// <summary>把一页预览行转成导出行：对每个单元格做公式注入转义，键保持不变（未知金额 / 数量仍为 null，不回落为 0）</summary>
    public static Dictionary<string, object?> BuildExportRow(Dictionary<string, object?> row)
    {
        var export = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var kv in row)
            export[kv.Key] = EscapeFormulaLeading(kv.Value);
        return export;
    }
}
