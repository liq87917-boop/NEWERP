using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Domain.Enums;

namespace ERP.Application.Services;

/// <summary>
/// 动态客户订单与收款核对报表（ERP-164）的纯规则：字段白名单（有限、只读）、字段 / 客户 / 币种 / 订单日期 / 出货状态 /
/// 收款链接状态 / 收款证据状态 / 订单状态 / 关键字 / 页大小校验、行投影与只读 / 边界 / 免责文案。无数据库依赖，便于逐条单测。
/// <para>复用既有「角色 → 菜单」模块授权（<c>sales-order</c> 销售订单菜单，见 <c>SeedData.Menus</c>）与
/// <see cref="SalespersonDataScopeService"/>（ERP-097）数据范围，并复用 ERP-046 客户订单与收款核对报表的权威派生；
/// 不新增任何表 / 列 / 权限模型，也不执行任何 SQL。</para>
/// <para>证据边界：本预览只回显 ERP-046 订单证据行的原始持久化 / 派生字段，收款申请链接证据、收款引用登记证据（ERP-054）、
/// 销项发票登记证据（ERP-056）与客户级未关联收款证据各自独立、绝不合并；不计算应收余额、不做收款授权、不做结算；
/// 金额按原币呈现、不做跨币种换算或汇总，未知金额与未知数量照实保留（null），绝不推算或修复。</para>
/// </summary>
public static class DynamicReceiptReconciliationReportRules
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

    /// <summary>关键字长度上限（超长直接拒绝，避免全表模糊扫描）</summary>
    public const int MaxKeywordLength = 50;

    // ==================== 0.1 筛选取值（与 ERP-046 口径同源） ====================

    /// <summary>出货状态筛选：无「以本单为来源、未删除、已审核」的销售出库单</summary>
    public const string ShipmentStatusNone = "none";

    /// <summary>出货状态筛选：存在「以本单为来源、未删除、已审核」的销售出库单</summary>
    public const string ShipmentStatusShipped = "shipped";

    /// <summary>支持的出货状态筛选取值（超出范围一律拒绝，不静默兜底）</summary>
    public static readonly string[] SupportedShipmentStatuses = { ShipmentStatusNone, ShipmentStatusShipped };

    /// <summary>收款链接状态筛选：权威引用完整</summary>
    public const string ReceiptLinkStatusLinked = "linked";

    /// <summary>收款链接状态筛选：部分可归属（他币种 / 未审核记录仅列出）</summary>
    public const string ReceiptLinkStatusPartial = "partial";

    /// <summary>收款链接状态筛选：无可用权威引用（金额未知，不推断）</summary>
    public const string ReceiptLinkStatusUnlinked = "unlinked";

    /// <summary>支持的收款链接状态筛选取值（超出范围一律拒绝，不静默兜底）</summary>
    public static readonly string[] SupportedReceiptLinkStatuses =
        { ReceiptLinkStatusLinked, ReceiptLinkStatusPartial, ReceiptLinkStatusUnlinked };

    /// <summary>收款证据状态：有效（已审核，默认）</summary>
    public const string ReceiptStatusActive = "active";

    /// <summary>收款证据状态：未审核（待提交 / 已提交，仅列出）</summary>
    public const string ReceiptStatusPending = "pending";

    /// <summary>收款证据状态：历史（已驳回 / 已取消 / 已完成，仅显式选择时可见）</summary>
    public const string ReceiptStatusHistorical = "historical";

    /// <summary>收款证据状态：全部（三类证据各自单列）</summary>
    public const string ReceiptStatusAll = "all";

    /// <summary>支持的收款证据状态筛选取值（超出范围一律拒绝，不静默兜底）</summary>
    public static readonly string[] SupportedReceiptStatuses =
        { ReceiptStatusActive, ReceiptStatusPending, ReceiptStatusHistorical, ReceiptStatusAll };

    /// <summary>订单状态：有效（默认，排除已取消）</summary>
    public const string OrderStatusActive = "active";

    /// <summary>订单状态：仅已取消</summary>
    public const string OrderStatusCancelled = "cancelled";

    /// <summary>订单状态：全部未删除</summary>
    public const string OrderStatusAll = "all";

    /// <summary>支持的订单状态筛选取值（超出范围一律拒绝，不静默兜底）</summary>
    public static readonly string[] SupportedOrderStatuses = { OrderStatusActive, OrderStatusCancelled, OrderStatusAll };

    // ==================== 1. 文案 ====================

    /// <summary>只读声明（界面与接口统一声明）</summary>
    public const string ReadOnlyText =
        "只读客户订单与收款核对报表预览：仅按选定白名单字段与有界筛选读取当前账号数据范围内的销售订单与收款证据，不新增 / 修改 / 删除任何记录";

    /// <summary>边界口径文案</summary>
    public const string BoundaryText =
        "口径：字段仅限 ERP-046 订单证据字段白名单；筛选仅限客户 / 币种 / 订单日期 / 出货状态 / 收款链接状态 / 收款证据状态 / 订单状态 / 关键字；"
        + "结果限定在当前账号业务员数据范围（特权账号不受限）；收款申请链接证据、收款引用登记证据、销项发票登记证据与未关联收款证据各自独立、绝不合并；"
        + "不执行任意 SQL、不做写入";

    /// <summary>免责文案</summary>
    public const string DisclaimerText =
        "本预览为只读快照：不替代应收账款台账 / 客户对账单 / 收款授权 / 结算结果 / 账龄表；金额按订单与收款单原币呈现、"
        + "不做跨币种换算或汇总，未知金额与未知数量为 null（不是 0），绝不推断收款单与订单的匹配关系";

    // ==================== 2. 字段白名单（有限、有序） ====================

    private sealed record FieldDef(string Key, string Label, string DataType, bool Filterable);

    private static readonly IReadOnlyList<FieldDef> Fields = new List<FieldDef>
    {
        // ---- 订单身份 ----
        new("orderId", "订单Id", "number", false),
        new("orderNo", "订单号", "text", false),
        new("orderDate", "订单日期", "date", true),
        new("status", "订单状态", "text", false),
        // ---- 客户与币种 ----
        new("customerId", "客户Id", "number", true),
        new("customerName", "客户名", "text", false),
        new("currency", "币种", "enum", true),
        new("amountDecimals", "金额小数位", "number", false),
        // ---- 订单金额（原币） ----
        new("orderAmount", "订单金额", "number", false),
        new("recordedDepositAmount", "已登记定金金额", "number", false),
        // ---- 出货数量证据 ----
        new("orderedQuantity", "已订数量", "number", false),
        new("shippedQuantity", "已出货数量", "number", false),
        new("pendingShipmentQuantity", "待审核出货数量", "number", false),
        new("outstandingQuantity", "未出货数量", "number", false),
        new("overShippedQuantity", "超发数量", "number", false),
        new("shipmentStatus", "出货状态", "text", true),
        new("hasApprovedShipment", "是否存在已审核出库单", "boolean", false),
        new("shipmentDocumentCount", "出库单张数", "number", false),
        new("approvedShipmentCount", "已审核出库单张数", "number", false),
        // ---- 收款申请链接证据（定金 / 货款申请单的 SalesOrderId 权威引用） ----
        new("receiptCoverageStatus", "收款覆盖状态", "text", false),
        new("receiptCoverageText", "收款覆盖文案", "text", false),
        new("receiptCoverageKnown", "收款覆盖是否已知", "boolean", false),
        new("linkedReceiptAmount", "已关联收款金额", "number", false),
        new("pendingReceiptAmount", "未审核收款金额", "number", false),
        new("uncoveredAmount", "未覆盖金额", "number", false),
        new("otherCurrencyReceiptCount", "他币种收款申请数", "number", false),
        new("unapprovedReceiptCount", "非已审核收款申请数", "number", false),
        new("unattributedReceiptCount", "无法归属收款记录数", "number", false),
        new("unattributedReceiptsTruncated", "无法归属收款是否截断", "boolean", false),
        new("overReceived", "是否超收", "boolean", false),
        // ---- 收款引用登记证据（ERP-054：ERP-053 持久化引用行） ----
        new("receiptAllocationStatus", "收款引用登记状态", "text", false),
        new("receiptAllocationEvidenceLabel", "收款引用登记标签", "text", false),
        new("receiptAllocationCount", "收款引用登记条数", "number", false),
        new("recordedReceiptAllocationAmount", "已登记收款引用金额", "number", false),
        new("recordedReceiptCount", "参与引用收款单张数", "number", false),
        new("voidedReceiptAllocationCount", "已作废引用条数", "number", false),
        new("invalidReceiptAllocationCount", "无效引用条数", "number", false),
        new("unavailableReceiptAllocationCount", "无法确认引用条数", "number", false),
        new("receiptAllocationTruncated", "收款引用是否截断", "boolean", false),
        new("unreferencedOrderAmount", "无引用证据订单金额", "number", false),
        new("receiptAllocationNote", "收款引用登记说明", "text", false),
        // ---- 销项发票登记证据（ERP-056：ERP-055 持久化发票证据行 + 分摊行） ----
        new("invoiceEvidenceStatus", "销项发票证据状态", "text", false),
        new("invoiceEvidenceLabel", "销项发票证据标签", "text", false),
        new("invoiceAllocationCount", "销项发票分摊行条数", "number", false),
        new("recordedInvoicedAmount", "已登记销项发票分摊金额", "number", false),
        new("recordedInvoiceCount", "参与发票证据张数", "number", false),
        new("recordedInvoiceGrossAmount", "发票含税总额快照合计", "number", false),
        new("unreferencedInvoiceAmount", "发票未指向本订单金额", "number", false),
        new("invoiceUnreferencedOrderAmount", "无发票证据订单金额", "number", false),
        new("draftInvoiceAllocationCount", "草稿发票分摊行条数", "number", false),
        new("voidedInvoiceAllocationCount", "已作废发票分摊行条数", "number", false),
        new("invalidInvoiceAllocationCount", "无效发票分摊行条数", "number", false),
        new("unavailableInvoiceAllocationCount", "无法确认发票分摊行条数", "number", false),
        new("invoiceEvidenceTruncated", "销项发票证据是否截断", "boolean", false),
        new("invoiceEvidenceNote", "销项发票证据说明", "text", false),
        // ---- 行级说明 ----
        new("note", "说明", "text", false),
    };

    private static readonly IReadOnlyDictionary<string, FieldDef> FieldByKey =
        Fields.ToDictionary(f => f.Key, StringComparer.OrdinalIgnoreCase);

    private static readonly string[] AllFieldKeys = Fields.Select(f => f.Key).ToArray();

    // ==================== 2.1 未关联收款证据字段目录（独立、有限、只读） ====================

    /// <summary>
    /// 未关联收款证据字段白名单：与订单证据字段目录完全独立，只覆盖 <see cref="DynamicReceiptReconciliationReportReceiptDto"/>
    /// 的有限字段，绝不混入订单侧合计字段；收款单只有客户级引用，因此链接状态恒为 unlinked，金额按收款单原币原样列出。
    /// </summary>
    private static readonly IReadOnlyList<FieldDef> ReceiptFields = new List<FieldDef>
    {
        new("receiptId", "收款单Id", "number", false),
        new("receiptNo", "收款单号", "text", false),
        new("receiptDate", "收款日期", "date", false),
        new("customerId", "客户Id", "number", false),
        new("customerName", "客户名", "text", false),
        new("currency", "币种", "enum", false),
        new("amount", "收款金额", "number", false),
        new("paymentMethod", "付款方式", "text", false),
        new("status", "单据状态", "text", false),
        new("evidenceStatus", "收款证据状态", "text", false),
        new("evidenceText", "收款证据文案", "text", false),
        new("receiptLinkageStatus", "收款链接状态", "text", false),
        new("receiptLinkageText", "收款链接文案", "text", false),
        new("referenceField", "建立引用字段", "text", false),
        new("note", "说明", "text", false),
    };

    private static readonly IReadOnlyDictionary<string, FieldDef> ReceiptFieldByKey =
        ReceiptFields.ToDictionary(f => f.Key, StringComparer.OrdinalIgnoreCase);

    private static readonly string[] AllReceiptFieldKeys = ReceiptFields.Select(f => f.Key).ToArray();

    // ==================== 3. 字段目录 ====================

    /// <summary>完整字段目录（仅白名单，有序）</summary>
    public static List<DynamicReceiptReconciliationReportFieldDto> GetCatalog()
        => Fields.Select(f => new DynamicReceiptReconciliationReportFieldDto(f.Key, f.Label, f.DataType, f.Filterable)).ToList();

    /// <summary>完整目录（含所需菜单与有界额度口径），并额外暴露独立的未关联收款证据字段目录</summary>
    public static DynamicReceiptReconciliationReportCatalogDto GetCatalogDto() => new(
        GetCatalog(),
        GetReceiptCatalog(),
        RequiredMenuCode,
        RequiredMenuText,
        MaxPageSize,
        ReadOnlyText,
        BoundaryText);

    /// <summary>按字段键查找目录项（大小写不敏感；未知返回 null）</summary>
    public static DynamicReceiptReconciliationReportFieldDto? GetField(string key)
        => FieldByKey.TryGetValue(key.Trim(), out var def)
            ? new DynamicReceiptReconciliationReportFieldDto(def.Key, def.Label, def.DataType, def.Filterable)
            : null;

    /// <summary>完整未关联收款证据字段目录（仅白名单，有序，独立于订单字段目录）</summary>
    public static List<DynamicReceiptReconciliationReportFieldDto> GetReceiptCatalog()
        => ReceiptFields.Select(f => new DynamicReceiptReconciliationReportFieldDto(f.Key, f.Label, f.DataType, f.Filterable)).ToList();

    /// <summary>按字段键查找未关联收款证据目录项（大小写不敏感；未知返回 null）</summary>
    public static DynamicReceiptReconciliationReportFieldDto? GetReceiptField(string key)
        => ReceiptFieldByKey.TryGetValue(key.Trim(), out var def)
            ? new DynamicReceiptReconciliationReportFieldDto(def.Key, def.Label, def.DataType, def.Filterable)
            : null;

    // ==================== 4. 校验与规范化（全部在源读取之前完成） ====================

    /// <summary>
    /// 规范化选定字段（fail closed）：未知字段显式拒绝；空 / 留空 = 返回全部白名单字段（目录顺序）；去重并保持请求顺序。
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

    /// <summary>
    /// 规范化选定的未关联收款证据字段（fail closed）：未知收款字段显式拒绝；空 / 留空 = 返回全部收款证据白名单字段（目录顺序）；
    /// 去重并保持请求顺序。与订单字段目录相互独立，绝不把订单侧合计字段当成收款证据字段接受。
    /// </summary>
    public static IReadOnlyList<string> NormalizeReceiptFields(IEnumerable<string>? fields)
    {
        var requested = (fields ?? Array.Empty<string>())
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Select(f => f.Trim())
            .ToList();
        if (requested.Count == 0)
            return AllReceiptFieldKeys;

        var ordered = new List<string>();
        foreach (var key in requested)
        {
            if (!ReceiptFieldByKey.TryGetValue(key, out var def))
                throw BusinessException.InvalidParameter($"未知收款证据字段: {key}");
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

    /// <summary>规范化币种筛选（空 = 不过滤；非法取值显式拒绝，复用 ERP-046 的严格币种口径）</summary>
    public static string? NormalizeCurrency(string? currency)
    {
        if (string.IsNullOrWhiteSpace(currency))
            return null;
        if (!Enum.TryParse<Currency>(currency.Trim(), true, out var parsed) || !Enum.IsDefined(parsed))
            throw BusinessException.InvalidParameter(
                $"币种无效：{currency}（应为 CNY / USD / EUR / HKD / GBP / JPY）");
        return parsed.ToString();
    }

    /// <summary>校验订单日期区间（开始晚于结束 = 无效）</summary>
    public static void ValidateDateRange(DateTime? from, DateTime? to)
    {
        if (from.HasValue && to.HasValue && from.Value.Date > to.Value.Date)
            throw BusinessException.InvalidParameter(
                $"订单日期开始 {from:yyyy-MM-dd} 不能晚于结束 {to:yyyy-MM-dd}");
    }

    /// <summary>规范化出货状态筛选（空 = 不过滤；非法取值直接拒绝，复用 ERP-046 的 none / shipped 口径）</summary>
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

    /// <summary>规范化收款链接状态筛选（空 = 不过滤；非法取值直接拒绝，复用 ERP-046 的 linked / partial / unlinked 口径）</summary>
    public static string? NormalizeReceiptLinkStatus(string? receiptLinkStatus)
    {
        if (string.IsNullOrWhiteSpace(receiptLinkStatus))
            return null;
        var value = receiptLinkStatus.Trim().ToLowerInvariant();
        if (!SupportedReceiptLinkStatuses.Contains(value, StringComparer.Ordinal))
            throw BusinessException.InvalidParameter(
                $"收款链接状态无效：{receiptLinkStatus}（应为 linked / partial / unlinked）");
        return value;
    }

    /// <summary>规范化收款证据状态筛选（空 = 默认 active；非法取值直接拒绝，复用 ERP-046 口径）</summary>
    public static string? NormalizeReceiptStatus(string? receiptStatus)
    {
        if (string.IsNullOrWhiteSpace(receiptStatus))
            return null;
        var value = receiptStatus.Trim().ToLowerInvariant();
        if (!SupportedReceiptStatuses.Contains(value, StringComparer.Ordinal))
            throw BusinessException.InvalidParameter(
                $"收款证据状态无效：{receiptStatus}（应为 active / pending / historical / all）");
        return value;
    }

    /// <summary>规范化订单状态筛选（空 = 默认 active；非法取值直接拒绝，复用 ERP-046 口径）</summary>
    public static string? NormalizeOrderStatus(string? orderStatus)
    {
        if (string.IsNullOrWhiteSpace(orderStatus))
            return null;
        var value = orderStatus.Trim().ToLowerInvariant();
        if (!SupportedOrderStatuses.Contains(value, StringComparer.Ordinal))
            throw BusinessException.InvalidParameter(
                $"订单状态无效：{orderStatus}（应为 active / cancelled / all）");
        return value;
    }

    /// <summary>规范化关键字（空 = 不过滤；超长直接拒绝，避免全表模糊扫描）</summary>
    public static string? NormalizeKeyword(string? keyword)
    {
        if (string.IsNullOrWhiteSpace(keyword))
            return null;
        var value = keyword.Trim();
        if (value.Length > MaxKeywordLength)
            throw BusinessException.InvalidParameter($"关键字长度不能超过 {MaxKeywordLength} 个字符");
        return value;
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

    // ==================== 6. Excel 导出（ERP-167，只读） ====================

    /// <summary>订单证据工作表名称（与预览分区同源，清晰标注、互不混淆）</summary>
    public const string OrderSheetName = "订单证据";

    /// <summary>未关联收款证据工作表名称（与预览分区同源，清晰标注、互不混淆）</summary>
    public const string ReceiptSheetName = "未关联收款证据";

    /// <summary>未关联收款证据命中读取上限时的截断警告（写入收款证据工作表尾行，显式保留、绝不静默截断）</summary>
    public const string ReceiptTruncationNote = "⚠️ 未关联收款证据命中读取上限，本页收款证据被截断（不完整，请缩小筛选范围后重试）";

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
    /// <para>仅对字符串生效；数值 / 日期 / 布尔等类型原样返回（由导出端按其类型写入对应单元格）。</para>
    /// </summary>
    public static object? EscapeFormulaLeading(object? value)
    {
        if (value is string s && IsFormulaLeading(s))
            return "'" + s;
        return value;
    }

    /// <summary>把一页预览行转成导出行：对每个单元格做公式注入转义，键保持不变（订单证据与未关联收款证据工作表共用）</summary>
    public static Dictionary<string, object?> BuildExportRow(Dictionary<string, object?> row)
    {
        var export = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var kv in row)
            export[kv.Key] = EscapeFormulaLeading(kv.Value);
        return export;
    }
}
