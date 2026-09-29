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

    // ==================== 0.2 分组键（ERP-160） ====================

    /// <summary>不分组（默认）</summary>
    public const string GroupNone = "none";

    /// <summary>按客户分组</summary>
    public const string GroupCustomer = "customer";

    /// <summary>按币种分组（原币，绝不跨币种合并或换算）</summary>
    public const string GroupCurrency = "currency";

    /// <summary>按出货状态分组（none / partial / complete / over_shipped / unknown，五类始终保留）</summary>
    public const string GroupShipmentStatus = "shipmentStatus";

    /// <summary>按收款链接状态分组（linked / partial / unlinked / unknown，四类始终保留）</summary>
    public const string GroupFinanceLinkStatus = "financeLinkStatus";

    /// <summary>出货状态派生类别：部分出货（与 ERP-032 派生口径同源）</summary>
    public const string ShipmentStatusPartial = "partial";

    /// <summary>出货状态派生类别：已出齐</summary>
    public const string ShipmentStatusComplete = "complete";

    /// <summary>出货状态派生类别：超发</summary>
    public const string ShipmentStatusOver = "over_shipped";

    /// <summary>出货状态派生类别：未知（命中派生上限，数量未知）</summary>
    public const string ShipmentStatusUnknown = "unknown";

    /// <summary>出货状态分组用的全部取值（确定性顺序；unknown 保持可见）</summary>
    public static readonly string[] GroupShipmentStatuses =
        { ShipmentStatusNone, ShipmentStatusPartial, ShipmentStatusComplete, ShipmentStatusOver, ShipmentStatusUnknown };

    /// <summary>收款链接状态派生类别：未知（命中派生上限，金额未知）</summary>
    public const string FinanceStatusUnknown = "unknown";

    /// <summary>收款链接状态分组用的全部取值（确定性顺序；unknown 保持可见）</summary>
    public static readonly string[] GroupFinanceLinkStatuses =
        { FinanceStatusLinked, FinanceStatusPartial, FinanceStatusUnlinked, FinanceStatusUnknown };

    // ==================== 0.3 金额汇总模式（ERP-162） ====================

    /// <summary>不汇总（默认）</summary>
    public const string SummaryNone = "none";

    /// <summary>按客户 + 币种汇总（客户与币种为强制分组边界，原币绝不跨币种合并或换算）</summary>
    public const string SummaryCustomerCurrency = "customerCurrency";

    /// <summary>按客户 + 币种 + 出货状态汇总</summary>
    public const string SummaryCustomerCurrencyShipment = "customerCurrencyShipment";

    /// <summary>按客户 + 币种 + 收款链接状态汇总</summary>
    public const string SummaryCustomerCurrencyFinance = "customerCurrencyFinance";

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

    // ==================== 4.1 分组键校验（ERP-160，源读取之前完成） ====================

    /// <summary>
    /// 规范化分组键（fail closed）：空 / 留空 = 不分组（none）；仅接受 none / customer / currency / shipmentStatus / financeLinkStatus（大小写不敏感）；
    /// 未知取值显式拒绝（在读取任何源数据之前完成）。
    /// </summary>
    public static string NormalizeGroupBy(string? groupBy)
    {
        if (string.IsNullOrWhiteSpace(groupBy))
            return GroupNone;

        var normalized = groupBy.Trim();
        if (string.Equals(normalized, GroupNone, StringComparison.OrdinalIgnoreCase)) return GroupNone;
        if (string.Equals(normalized, GroupCustomer, StringComparison.OrdinalIgnoreCase)) return GroupCustomer;
        if (string.Equals(normalized, GroupCurrency, StringComparison.OrdinalIgnoreCase)) return GroupCurrency;
        if (string.Equals(normalized, GroupShipmentStatus, StringComparison.OrdinalIgnoreCase)) return GroupShipmentStatus;
        if (string.Equals(normalized, GroupFinanceLinkStatus, StringComparison.OrdinalIgnoreCase)) return GroupFinanceLinkStatus;

        throw BusinessException.InvalidParameter(
            $"无效的分组键: {groupBy}（可选：none / customer / currency / shipmentStatus / financeLinkStatus）");
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

    // ==================== 5.1 分组计数（ERP-160） ====================

    /// <summary>
    /// 分组销售订单张数分布（ERP-160）：从「当前授权预览页」的销售订单出货 / 财务进度证据行计算张数分布，只统计张数、绝不求和任何金额或数量、绝不跨币种合并或换算。
    /// <para>customer / currency 为动态分组（只出现本页存在的取值，按客户 Id / 币种升序）；shipmentStatus 与 financeLinkStatus 为固定证据分类，
    /// 空分类始终保留（计数可为 0），其中 unknown 出货 / 收款链接类别保持可见；none / 空页返回空列表。</para>
    /// </summary>
    public static List<DynamicShipmentFinanceReportGroupDto> BuildGroupCounts(
        IEnumerable<IReadOnlyDictionary<string, object?>> rows, string groupBy)
    {
        var list = (rows ?? Array.Empty<IReadOnlyDictionary<string, object?>>()).ToList();
        return NormalizeGroupBy(groupBy) switch
        {
            GroupCustomer => BuildCustomerCounts(list),
            GroupCurrency => BuildCurrencyCounts(list),
            GroupShipmentStatus => BuildShipmentStatusCounts(list),
            GroupFinanceLinkStatus => BuildFinanceLinkStatusCounts(list),
            _ => new List<DynamicShipmentFinanceReportGroupDto>(),
        };
    }

    private static List<DynamicShipmentFinanceReportGroupDto> BuildCustomerCounts(
        List<IReadOnlyDictionary<string, object?>> rows)
    {
        return rows
            .GroupBy(ReadCustomerId)
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                var name = ReadText(g.First(), "customerName");
                return new DynamicShipmentFinanceReportGroupDto(
                    $"customer:{g.Key}",
                    string.IsNullOrWhiteSpace(name) ? $"客户 #{g.Key}" : name,
                    g.Count());
            })
            .ToList();
    }

    private static List<DynamicShipmentFinanceReportGroupDto> BuildCurrencyCounts(
        List<IReadOnlyDictionary<string, object?>> rows)
    {
        return rows
            .GroupBy(r => ReadText(r, "currency"))
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new DynamicShipmentFinanceReportGroupDto(
                $"currency:{g.Key}",
                string.IsNullOrWhiteSpace(g.Key) ? "未知币种" : g.Key,
                g.Count()))
            .ToList();
    }

    private static List<DynamicShipmentFinanceReportGroupDto> BuildShipmentStatusCounts(
        List<IReadOnlyDictionary<string, object?>> rows)
    {
        return GroupShipmentStatuses
            .Select(status => new DynamicShipmentFinanceReportGroupDto(
                $"shipmentStatus:{status}",
                GroupShipmentStatusLabel(status),
                rows.Count(r => ReadText(r, "shipmentStatus") == status)))
            .ToList();
    }

    private static List<DynamicShipmentFinanceReportGroupDto> BuildFinanceLinkStatusCounts(
        List<IReadOnlyDictionary<string, object?>> rows)
    {
        return GroupFinanceLinkStatuses
            .Select(status => new DynamicShipmentFinanceReportGroupDto(
                $"financeLinkStatus:{status}",
                GroupFinanceLinkStatusLabel(status),
                rows.Count(r => ReadText(r, "financeLinkStatus") == status)))
            .ToList();
    }

    private static long ReadCustomerId(IReadOnlyDictionary<string, object?> row)
        => row.TryGetValue("customerId", out var v) && v is long id ? id : 0L;

    private static string ReadText(IReadOnlyDictionary<string, object?> row, string key)
        => row.TryGetValue(key, out var v) && v is string s ? s : string.Empty;

    /// <summary>出货状态中文文案（与 ERP-032 / 执行进度同源；unknown 保持可见）</summary>
    public static string GroupShipmentStatusLabel(string status) => status switch
    {
        ShipmentStatusNone => "未出货",
        ShipmentStatusPartial => "部分出货",
        ShipmentStatusComplete => "已出齐",
        ShipmentStatusOver => "超发",
        ShipmentStatusUnknown => "未知（超出派生上限）",
        _ => "未知出货状态",
    };

    /// <summary>收款链接状态中文文案（与 ERP-032 / 执行进度同源；unknown 保持可见）</summary>
    public static string GroupFinanceLinkStatusLabel(string status) => status switch
    {
        FinanceStatusLinked => "收款引用完整",
        FinanceStatusPartial => "部分可归属（其余未知）",
        FinanceStatusUnlinked => "未链接（金额未知）",
        FinanceStatusUnknown => "未知（超出派生上限）",
        _ => "未知收款链接状态",
    };

    // ==================== 5.2 当前页金额汇总（ERP-162） ====================

    /// <summary>
    /// 规范化金额汇总模式（fail closed）：空 / 留空 = 不汇总（none）；仅接受 none / customerCurrency / customerCurrencyShipment /
    /// customerCurrencyFinance（大小写不敏感）；未知取值显式拒绝（在读取任何源数据之前完成）。
    /// </summary>
    public static string NormalizeSummaryMode(string? summaryMode)
    {
        if (string.IsNullOrWhiteSpace(summaryMode))
            return SummaryNone;

        var normalized = summaryMode.Trim();
        if (string.Equals(normalized, SummaryNone, StringComparison.OrdinalIgnoreCase)) return SummaryNone;
        if (string.Equals(normalized, SummaryCustomerCurrency, StringComparison.OrdinalIgnoreCase)) return SummaryCustomerCurrency;
        if (string.Equals(normalized, SummaryCustomerCurrencyShipment, StringComparison.OrdinalIgnoreCase)) return SummaryCustomerCurrencyShipment;
        if (string.Equals(normalized, SummaryCustomerCurrencyFinance, StringComparison.OrdinalIgnoreCase)) return SummaryCustomerCurrencyFinance;

        throw BusinessException.InvalidParameter(
            $"无效的金额汇总模式: {summaryMode}（可选：none / customerCurrency / customerCurrencyShipment / customerCurrencyFinance）");
    }

    /// <summary>
    /// 当前授权预览页的金额汇总（ERP-162）：只汇总「当前授权预览页」的销售订单出货 / 财务进度证据行（非全量合计），
    /// 客户与币种为强制分组边界（可选再按出货状态 / 收款链接状态拆分）；订单金额保持原币证据（直接求和，绝不跨币种合并或换算）；
    /// linked / uncovered / submitted 合计只要任一行金额未知（null）即整体按「未知」（null）返回，绝不轧为 0 或给部分合计，
    /// 并显式给出已知 / 未知行数。空页 / none 返回空列表。
    /// </summary>
    public static List<DynamicShipmentFinanceReportSummaryDto> BuildAmountSummaries(
        IEnumerable<IReadOnlyDictionary<string, object?>> rows, string summaryMode)
    {
        var list = (rows ?? Array.Empty<IReadOnlyDictionary<string, object?>>()).ToList();
        var mode = NormalizeSummaryMode(summaryMode);
        if (mode == SummaryNone)
            return new List<DynamicShipmentFinanceReportSummaryDto>();

        var withShipment = mode == SummaryCustomerCurrencyShipment;
        var withFinance = mode == SummaryCustomerCurrencyFinance;

        var summaries = new List<DynamicShipmentFinanceReportSummaryDto>();
        foreach (var group in list
            .GroupBy(r => new
            {
                CustomerId = ReadCustomerId(r),
                Currency = ReadText(r, "currency"),
                Shipment = withShipment ? ReadText(r, "shipmentStatus") : string.Empty,
                Finance = withFinance ? ReadText(r, "financeLinkStatus") : string.Empty,
            })
            .OrderBy(g => g.Key.CustomerId)
            .ThenBy(g => g.Key.Currency, StringComparer.Ordinal)
            .ThenBy(g => g.Key.Shipment, StringComparer.Ordinal)
            .ThenBy(g => g.Key.Finance, StringComparer.Ordinal))
        {
            var groupRows = group.ToList();
            var first = groupRows[0];

            var knownLinked = CountKnownRows(groupRows, "linkedAmount");
            var knownUncovered = CountKnownRows(groupRows, "uncoveredAmount");
            var knownSubmitted = CountKnownRows(groupRows, "submittedAmount");

            summaries.Add(new DynamicShipmentFinanceReportSummaryDto(
                group.Key.CustomerId,
                ReadText(first, "customerName"),
                group.Key.Currency,
                withShipment ? group.Key.Shipment : null,
                withFinance ? group.Key.Finance : null,
                groupRows.Count,
                groupRows.Sum(r => ReadDecimal(r, "orderAmount")),
                knownLinked,
                groupRows.Count - knownLinked,
                SumKnownNullable(groupRows, "linkedAmount"),
                knownUncovered,
                groupRows.Count - knownUncovered,
                SumKnownNullable(groupRows, "uncoveredAmount"),
                knownSubmitted,
                groupRows.Count - knownSubmitted,
                SumKnownNullable(groupRows, "submittedAmount")));
        }

        return summaries;
    }

    private static decimal ReadDecimal(IReadOnlyDictionary<string, object?> row, string key)
        => row.TryGetValue(key, out var v) && v is decimal d ? d : 0m;

    private static decimal? ReadDecimalNullable(IReadOnlyDictionary<string, object?> row, string key)
        => row.TryGetValue(key, out var v) && v is decimal d ? d : null;

    private static int CountKnownRows(IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, string key)
        => rows.Count(r => ReadDecimalNullable(r, key).HasValue);

    /// <summary>可未知金额求和：只要有一行金额未知（null），整体按「未知」（null）返回，绝不用 0 顶替或给部分合计</summary>
    private static decimal? SumKnownNullable(
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, string key)
    {
        decimal sum = 0m;
        foreach (var row in rows)
        {
            var value = ReadDecimalNullable(row, key);
            if (value is null) return null;
            sum += value.Value;
        }

        return sum;
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
