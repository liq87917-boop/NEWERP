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

    // ==================== 0.2 分组键（ERP-152） ====================

    /// <summary>不分组（默认）</summary>
    public const string GroupNone = "none";

    /// <summary>按供应商分组</summary>
    public const string GroupSupplier = "supplier";

    /// <summary>按币种分组（原币，绝不跨币种合并或换算）</summary>
    public const string GroupCurrency = "currency";

    /// <summary>按链接状态分组（linked / ambiguous / unavailable，三类始终保留）</summary>
    public const string GroupLinkStatus = "linkStatus";

    /// <summary>按收货状态分组（none / partial / complete / over_received / unknown，五类始终保留）</summary>
    public const string GroupReceiptStatus = "receiptStatus";

    /// <summary>收货状态：无已收（含订单数量为 0）</summary>
    public const string ReceiptNone = "none";

    /// <summary>收货状态：部分收货（仍有未收数量）</summary>
    public const string ReceiptPartial = "partial";

    /// <summary>收货状态：已收齐</summary>
    public const string ReceiptComplete = "complete";

    /// <summary>收货状态：超收</summary>
    public const string ReceiptOverReceived = "over_received";

    /// <summary>收货状态：本次派生命中上限（数量不完整，未知，不等于 0）</summary>
    public const string ReceiptUnknown = "unknown";

    /// <summary>链接状态分组用的全部取值（确定性顺序；ambiguous / unavailable 保持可见）</summary>
    public static readonly string[] GroupLinkStatuses =
        { LinkLinked, LinkAmbiguous, LinkUnavailable };

    /// <summary>收货状态分组用的全部取值（确定性顺序；unknown 保持可见）</summary>
    public static readonly string[] GroupReceiptStatuses =
        { ReceiptNone, ReceiptPartial, ReceiptComplete, ReceiptOverReceived, ReceiptUnknown };

    // ==================== 0.3 金额汇总模式（ERP-154） ====================

    /// <summary>不输出金额汇总（默认）</summary>
    public const string SummaryNone = "none";

    /// <summary>按供应商 + 币种输出当前页金额汇总（原币，绝不跨币种合并或换算）</summary>
    public const string SummarySupplierCurrency = "supplierCurrency";

    /// <summary>按供应商 + 币种 + 链接状态输出当前页金额汇总（ambiguous / unavailable 订单金额保持独立）</summary>
    public const string SummarySupplierCurrencyLink = "supplierCurrencyLink";

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

    // ==================== 5.1 分组计数（ERP-152） ====================

    /// <summary>
    /// 规范化分组键（fail closed）：空 / 留空 = 不分组（none）；仅接受 none / supplier / currency / linkStatus / receiptStatus（大小写不敏感）；
    /// 未知取值显式拒绝（在读取任何源数据之前完成）。
    /// </summary>
    public static string NormalizeGroupBy(string? groupBy)
    {
        if (string.IsNullOrWhiteSpace(groupBy))
            return GroupNone;

        var normalized = groupBy.Trim();
        if (string.Equals(normalized, GroupNone, StringComparison.OrdinalIgnoreCase)) return GroupNone;
        if (string.Equals(normalized, GroupSupplier, StringComparison.OrdinalIgnoreCase)) return GroupSupplier;
        if (string.Equals(normalized, GroupCurrency, StringComparison.OrdinalIgnoreCase)) return GroupCurrency;
        if (string.Equals(normalized, GroupLinkStatus, StringComparison.OrdinalIgnoreCase)) return GroupLinkStatus;
        if (string.Equals(normalized, GroupReceiptStatus, StringComparison.OrdinalIgnoreCase)) return GroupReceiptStatus;

        throw BusinessException.InvalidParameter(
            $"无效的分组键: {groupBy}（可选：none / supplier / currency / linkStatus / receiptStatus）");
    }

    /// <summary>
    /// 分组采购订单张数分布（ERP-152）：从「当前授权预览页」的采购订单敞口证据行计算张数分布，只统计张数、绝不求和任何金额或数量、绝不跨币种合并或换算。
    /// <para>supplier / currency 为动态分组（只出现本页存在的取值，按供应商 Id / 币种升序）；linkStatus 与 receiptStatus 为固定证据分类，
    /// 空分类始终保留（计数可为 0），其中 ambiguous / unavailable 链接与 unknown 收货保持可见；none / 空页返回空列表。</para>
    /// </summary>
    public static List<DynamicSupplierExposureReportGroupDto> BuildGroupCounts(
        IEnumerable<IReadOnlyDictionary<string, object?>> rows, string groupBy)
    {
        var list = (rows ?? Array.Empty<IReadOnlyDictionary<string, object?>>()).ToList();
        return NormalizeGroupBy(groupBy) switch
        {
            GroupSupplier => BuildSupplierCounts(list),
            GroupCurrency => BuildCurrencyCounts(list),
            GroupLinkStatus => BuildLinkStatusCounts(list),
            GroupReceiptStatus => BuildReceiptStatusCounts(list),
            _ => new List<DynamicSupplierExposureReportGroupDto>(),
        };
    }

    private static List<DynamicSupplierExposureReportGroupDto> BuildSupplierCounts(
        List<IReadOnlyDictionary<string, object?>> rows)
    {
        return rows
            .GroupBy(ReadSupplierId)
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                var name = ReadText(g.First(), "supplierName");
                return new DynamicSupplierExposureReportGroupDto(
                    $"supplier:{g.Key}",
                    string.IsNullOrWhiteSpace(name) ? $"供应商 #{g.Key}" : name,
                    g.Count());
            })
            .ToList();
    }

    private static List<DynamicSupplierExposureReportGroupDto> BuildCurrencyCounts(
        List<IReadOnlyDictionary<string, object?>> rows)
    {
        return rows
            .GroupBy(r => ReadText(r, "currency"))
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new DynamicSupplierExposureReportGroupDto(
                $"currency:{g.Key}",
                string.IsNullOrWhiteSpace(g.Key) ? "未知币种" : g.Key,
                g.Count()))
            .ToList();
    }

    private static List<DynamicSupplierExposureReportGroupDto> BuildLinkStatusCounts(
        List<IReadOnlyDictionary<string, object?>> rows)
    {
        return GroupLinkStatuses
            .Select(status => new DynamicSupplierExposureReportGroupDto(
                $"linkStatus:{status}",
                GroupLinkStatusLabel(status),
                rows.Count(r => ReadText(r, "linkStatus") == status)))
            .ToList();
    }

    private static List<DynamicSupplierExposureReportGroupDto> BuildReceiptStatusCounts(
        List<IReadOnlyDictionary<string, object?>> rows)
    {
        return GroupReceiptStatuses
            .Select(status => new DynamicSupplierExposureReportGroupDto(
                $"receiptStatus:{status}",
                GroupReceiptStatusLabel(status),
                rows.Count(r => ReadText(r, "receiptStatus") == status)))
            .ToList();
    }

    private static long ReadSupplierId(IReadOnlyDictionary<string, object?> row)
        => row.TryGetValue("supplierId", out var v) && v is long id ? id : 0L;

    private static string ReadText(IReadOnlyDictionary<string, object?> row, string key)
        => row.TryGetValue(key, out var v) && v is string s ? s : string.Empty;

    /// <summary>链接状态中文文案（与 ERP-031 / 执行进度同源）</summary>
    public static string GroupLinkStatusLabel(string status) => status switch
    {
        LinkLinked => "链接可用",
        LinkAmbiguous => "链接不唯一（金额未知）",
        LinkUnavailable => "无可用链接（金额未知）",
        _ => "未知链接状态",
    };

    /// <summary>收货状态中文文案（与 ERP-031 / 执行进度同源）</summary>
    public static string GroupReceiptStatusLabel(string status) => status switch
    {
        ReceiptNone => "未收货",
        ReceiptPartial => "部分收货",
        ReceiptComplete => "已收齐",
        ReceiptOverReceived => "超收",
        ReceiptUnknown => "未知（超出派生上限）",
        _ => "未知收货状态",
    };

    // ==================== 5.2 当前页金额汇总（ERP-154） ====================

    /// <summary>
    /// 规范化金额汇总模式（fail closed）：空 / 留空 = 不汇总（none）；仅接受 none / supplierCurrency / supplierCurrencyLink（大小写不敏感）；
    /// 未知取值显式拒绝（在读取任何源数据之前完成）。
    /// </summary>
    public static string NormalizeSummaryMode(string? summaryMode)
    {
        if (string.IsNullOrWhiteSpace(summaryMode))
            return SummaryNone;

        var normalized = summaryMode.Trim();
        if (string.Equals(normalized, SummaryNone, StringComparison.OrdinalIgnoreCase)) return SummaryNone;
        if (string.Equals(normalized, SummarySupplierCurrency, StringComparison.OrdinalIgnoreCase)) return SummarySupplierCurrency;
        if (string.Equals(normalized, SummarySupplierCurrencyLink, StringComparison.OrdinalIgnoreCase)) return SummarySupplierCurrencyLink;

        throw BusinessException.InvalidParameter(
            $"无效的金额汇总模式: {summaryMode}（可选：none / supplierCurrency / supplierCurrencyLink）");
    }

    /// <summary>
    /// 当前授权预览页的金额汇总（ERP-154）：按供应商 + 原币分组（可选再按链接状态拆分），只汇总当前页采购订单敞口证据。
    /// <para>订单金额来自采购订单已落库总额（恒可确认，直接求和，只统计当前页）；已结算 / 未结算 / 已提交付款金额只汇总
    /// 「链接可用且金额已知」的订单（没有链接可用订单或任一行金额未知即整组合计为 null，绝不轧为 0、绝不给出部分合计）；
    /// 链接不唯一（ambiguous）与无可用链接（unavailable）的订单金额保持独立，绝不并入权威已结算合计、绝不推断为应付余额。</para>
    /// <para>空页 / none 返回空列表；汇总只统计当前页、非全量合计。</para>
    /// </summary>
    public static List<DynamicSupplierExposureReportSummaryDto> BuildAmountSummaries(
        IEnumerable<IReadOnlyDictionary<string, object?>> rows, string summaryMode)
    {
        var list = (rows ?? Array.Empty<IReadOnlyDictionary<string, object?>>()).ToList();
        var mode = NormalizeSummaryMode(summaryMode);
        if (mode == SummaryNone)
            return new List<DynamicSupplierExposureReportSummaryDto>();

        var summaries = new List<DynamicSupplierExposureReportSummaryDto>();
        foreach (var group in list
            .GroupBy(r => new
            {
                SupplierId = ReadSupplierId(r),
                Currency = ReadText(r, "currency"),
                LinkStatus = mode == SummarySupplierCurrencyLink ? ReadText(r, "linkStatus") : string.Empty,
            })
            .OrderBy(g => g.Key.SupplierId)
            .ThenBy(g => g.Key.Currency, StringComparer.Ordinal)
            .ThenBy(g => LinkStatusOrder(g.Key.LinkStatus)))
        {
            var groupRows = group.ToList();
            var first = groupRows[0];
            var withLink = mode == SummarySupplierCurrencyLink;

            var linked = groupRows.Where(r => ReadText(r, "linkStatus") == LinkLinked).ToList();
            var ambiguous = groupRows.Where(r => ReadText(r, "linkStatus") == LinkAmbiguous).ToList();
            var unavailable = groupRows.Where(r => ReadText(r, "linkStatus") == LinkUnavailable).ToList();

            summaries.Add(new DynamicSupplierExposureReportSummaryDto(
                group.Key.SupplierId,
                ReadText(first, "supplierName"),
                group.Key.Currency,
                withLink ? group.Key.LinkStatus : null,
                withLink ? GroupLinkStatusLabel(group.Key.LinkStatus) : null,
                groupRows.Count,
                groupRows.Sum(ReadOrderedAmount),
                linked.Count,
                linked.Sum(ReadOrderedAmount),
                ambiguous.Count,
                ambiguous.Sum(ReadOrderedAmount),
                unavailable.Count,
                unavailable.Sum(ReadOrderedAmount),
                SumKnownNullable(linked, "settledAmount"),
                SumKnownNullable(linked, "outstandingAmount"),
                SumKnownNullable(linked, "submittedAmount"),
                linked.Count(r => ReadDecimalNullable(r, "settledAmount") is null
                                  || ReadDecimalNullable(r, "outstandingAmount") is null
                                  || ReadDecimalNullable(r, "submittedAmount") is null)));
        }

        return summaries;
    }

    private static decimal ReadOrderedAmount(IReadOnlyDictionary<string, object?> row)
        => row.TryGetValue("orderedAmount", out var v) && v is decimal d ? d : 0m;

    private static decimal? ReadDecimalNullable(IReadOnlyDictionary<string, object?> row, string key)
        => row.TryGetValue(key, out var v) && v is decimal d ? d : null;

    /// <summary>链接可用订单的可未知金额求和：没有链接可用订单或任一行金额未知（null）即整体按「未知」（null）返回，绝不用 0 顶替或给部分合计</summary>
    private static decimal? SumKnownNullable(
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, string key)
    {
        if (rows.Count == 0) return null;
        decimal sum = 0m;
        foreach (var row in rows)
        {
            var value = ReadDecimalNullable(row, key);
            if (value is null) return null;
            sum += value.Value;
        }

        return sum;
    }

    private static int LinkStatusOrder(string linkStatus) => linkStatus switch
    {
        LinkLinked => 0,
        LinkAmbiguous => 1,
        LinkUnavailable => 2,
        _ => int.MaxValue,
    };

    // ==================== 6. Excel 导出（ERP-150） ====================

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
