using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Domain.Enums;

namespace ERP.Application.Services;

/// <summary>
/// 动态供应商采购敞口预览（ERP-148）的纯规则：字段白名单（有限、只读）、字段 / 供应商 / 币种 / 日期 /
/// 链接状态 / 关键字 / 页大小校验、行投影与只读 / 边界 / 免责文案。无数据库依赖，便于逐条单测。
/// <para>复用既有「角色 → 菜单」模块授权（<c>purchase-order</c> 采购订单菜单，见 <c>SeedData.Menus</c>）与
/// ERP-031 供应商采购敞口报表的权威派生（<c>SupplierPurchaseExposure.ForQueryAsync</c>）；
/// 不新增任何表 / 列 / 权限模型，也不执行任何 SQL。</para>
/// <para>财务证据边界：本预览只回显采购订单敞口证据行的原始持久化 / 派生字段，不计算应付余额、不做付款授权、不做结算；
/// 金额按原币呈现、不做跨币种换算或汇总，未知结算金额与未知收货数量照实保留，绝不推算或修复。</para>
/// </summary>
public static class DynamicSupplierExposureReportRules
{
    // ==================== 0. 常量 ====================

    /// <summary>预览所需的既有菜单编码（复用采购订单模块菜单；与 SeedData.Menus 同源）</summary>
    public const string RequiredMenuCode = "purchase-order";

    /// <summary>预览所需菜单的中文文案（与既有菜单名一致）</summary>
    public const string RequiredMenuText = "采购订单";

    /// <summary>默认每页条数</summary>
    public const int DefaultPageSize = 50;

    /// <summary>每页条数上限（有界：单次请求最多返回这么多采购订单）</summary>
    public const int MaxPageSize = 200;

    /// <summary>关键字长度上限（超长直接拒绝，避免全表模糊扫描）</summary>
    public const int MaxKeywordLength = 50;

    // ==================== 0.1 筛选取值（与 ERP-031 口径同源） ====================

    /// <summary>链接状态筛选：既有引用可用（唯一归属）</summary>
    public const string LinkLinked = "linked";

    /// <summary>链接状态筛选：既有引用无法唯一归属（金额未知，不推断）</summary>
    public const string LinkAmbiguous = "ambiguous";

    /// <summary>链接状态筛选：不存在可用的既有引用（金额未知，不推断）</summary>
    public const string LinkUnavailable = "unavailable";

    /// <summary>支持的链接状态筛选取值（超出范围一律拒绝，不静默兜底）</summary>
    public static readonly string[] SupportedLinkStatuses =
        { LinkLinked, LinkAmbiguous, LinkUnavailable };

    // ==================== 1. 文案 ====================

    /// <summary>只读声明（接口与文档统一声明）</summary>
    public const string ReadOnlyText =
        "只读供应商采购敞口预览：仅按选定白名单采购订单敞口证据字段与有界筛选读取当前账号可见的采购订单，不新增 / 修改 / 删除任何记录";

    /// <summary>边界口径文案</summary>
    public const string BoundaryText =
        "口径：字段仅限 ERP-031 供应商采购敞口订单证据字段白名单；筛选仅限供应商 / 币种 / 订单日期 / 链接状态 / 关键字；" +
        "金额一律按原币分别成行、绝不跨币种合并或换算；链接不唯一或缺失的结算金额与收货数量未知时照实保留（null），" +
        "绝不推算或修复；不执行任意 SQL、不做写入";

    /// <summary>免责文案</summary>
    public const string DisclaimerText =
        "本预览为只读派生证据：不是应付账款台账、不是账龄表、不是付款授权、不是税务申报，也不构成结算确认；" +
        "「未链接敞口」只是尚未按既有引用归属到订单的订单金额，不得当作应付余额或据以付款";

    // ==================== 2. 字段白名单（有限、有序；全部来自 ERP-031 订单证据行） ====================

    private sealed record FieldDef(string Key, string Label, string DataType, bool Filterable);

    private static readonly IReadOnlyList<FieldDef> Fields = new List<FieldDef>
    {
        // ---- 订单身份 ----
        new("orderId", "订单Id", "number", false),
        new("orderNo", "采购单号", "text", false),
        new("orderDate", "订单日期", "date", true),
        new("status", "单据状态", "text", false),
        // ---- 供应商 ----
        new("supplierId", "供应商Id", "number", true),
        new("supplierName", "供应商名称", "text", false),
        // ---- 币种（原币；绝不换算、绝不跨币种合并） ----
        new("currency", "币种", "text", true),
        // ---- 结算引用链 ----
        new("owningSalesOrderNo", "归属销售订单号", "text", false),
        new("recordedSettlementProgress", "已登记结算进度", "text", false),
        // ---- 订单金额 ----
        new("orderedAmount", "订单金额", "number", false),
        // ---- 链接状态与结算金额（未知用 null，绝不回落为 0） ----
        new("linkStatus", "链接状态", "text", true),
        new("linkReason", "链接状态说明", "text", false),
        new("settledAmount", "已结算金额", "number", false),
        new("outstandingAmount", "未结算金额", "number", false),
        new("submittedAmount", "已提交/待提交付款金额", "number", false),
        new("overSettled", "是否超付", "boolean", false),
        // ---- 收货状态与数量（未知用 null，绝不回落为 0） ----
        new("receiptStatus", "收货状态", "text", false),
        new("orderedQuantity", "订单数量", "number", false),
        new("receivedQuantity", "已收数量", "number", false),
        new("outstandingQuantity", "未收数量", "number", false),
        new("pendingQuantity", "待审核数量", "number", false),
        // ---- 行级说明 ----
        new("note", "说明", "text", false),
    };

    private static readonly IReadOnlyDictionary<string, FieldDef> FieldByKey =
        Fields.ToDictionary(f => f.Key, StringComparer.OrdinalIgnoreCase);

    private static readonly string[] AllFieldKeys = Fields.Select(f => f.Key).ToArray();

    // ==================== 3. 字段目录 ====================

    /// <summary>完整字段目录（仅白名单，有序）</summary>
    public static List<DynamicSupplierExposureReportFieldDto> GetCatalog()
        => Fields.Select(f => new DynamicSupplierExposureReportFieldDto(f.Key, f.Label, f.DataType, f.Filterable)).ToList();

    /// <summary>完整目录（含所需菜单与有界额度口径）</summary>
    public static DynamicSupplierExposureReportCatalogDto GetCatalogDto() => new(
        GetCatalog(),
        RequiredMenuCode,
        RequiredMenuText,
        MaxPageSize,
        ReadOnlyText,
        BoundaryText);

    /// <summary>按字段键查找目录项（大小写不敏感；未知返回 null）</summary>
    public static DynamicSupplierExposureReportFieldDto? GetField(string key)
        => FieldByKey.TryGetValue(key.Trim(), out var def)
            ? new DynamicSupplierExposureReportFieldDto(def.Key, def.Label, def.DataType, def.Filterable)
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

    /// <summary>规范化供应商 Id 筛选（留空 = 不限；非正数显式拒绝，fail closed）</summary>
    public static long? NormalizeSupplierId(long? supplierId)
    {
        if (supplierId is <= 0)
            throw BusinessException.InvalidParameter($"供应商 Id 必须为正整数：{supplierId}");
        return supplierId;
    }

    /// <summary>规范化币种筛选（空 = 不过滤；非法取值显式拒绝，复用 ERP-031 的严格币种口径）</summary>
    public static string? NormalizeCurrency(string? currency)
    {
        if (string.IsNullOrWhiteSpace(currency))
            return null;
        if (!Enum.TryParse<Currency>(currency.Trim(), true, out var parsed) || !Enum.IsDefined(parsed))
            throw BusinessException.InvalidParameter(
                $"币种无效：{currency}（应为 CNY / USD / EUR / HKD / GBP / JPY）");
        return parsed.ToString();
    }

    /// <summary>规范化链接状态筛选（空 = 不过滤；非法取值直接拒绝）</summary>
    public static string? NormalizeLinkStatus(string? linkStatus)
    {
        if (string.IsNullOrWhiteSpace(linkStatus))
            return null;
        var value = linkStatus.Trim().ToLowerInvariant();
        if (!SupportedLinkStatuses.Contains(value, StringComparer.Ordinal))
            throw BusinessException.InvalidParameter(
                $"链接状态无效：{linkStatus}（应为 linked / ambiguous / unavailable）");
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
}
