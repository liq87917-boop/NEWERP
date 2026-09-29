using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;

namespace ERP.Application.Services;

/// <summary>
/// 动态库存移动报表（ERP-130）的纯规则：字段白名单、字段 / 日期 / 阈值 / 页大小校验、行投影与只读 / 边界 / 免责文案。
/// 无数据库依赖，便于逐条单测。
/// <para>复用既有「角色 → 菜单」模块授权（<c>stock-query</c> 库存查询菜单，见 <c>SeedData.Menus</c>）与
/// ERP-029 库存移动报表服务（<see cref="IReportService.GetInventoryMovementReportAsync"/>）；
/// 不新增任何表 / 列 / 权限模型，也不执行任何 SQL。</para>
/// </summary>
public static class DynamicInventoryMovementReportRules
{
    // ==================== 0. 常量 ====================

    /// <summary>预览所需的既有菜单编码（复用库存查询模块菜单；与 SeedData.Menus 同源）</summary>
    public const string RequiredMenuCode = "stock-query";

    /// <summary>预览所需菜单的中文文案（与既有菜单名一致）</summary>
    public const string RequiredMenuText = "库存查询";

    /// <summary>默认每页条数</summary>
    public const int DefaultPageSize = 50;

    /// <summary>每页条数上限（有界：单次请求最多返回这么多行）</summary>
    public const int MaxPageSize = 200;

    /// <summary>呆滞阈值（天）最小值：小于该值时无法表达「停滞」含义，直接拒绝</summary>
    public const int MinInactiveDays = 1;

    /// <summary>默认呆滞阈值（天）</summary>
    public const int DefaultInactiveDays = 90;

    // ==================== 0.1 分组键（ERP-132） ====================

    /// <summary>不分组（默认）</summary>
    public const string GroupNone = "none";

    /// <summary>按仓库分组</summary>
    public const string GroupWarehouse = "warehouse";

    /// <summary>按呆滞分类分组（active / stagnant / unknown）</summary>
    public const string GroupClassification = "classification";

    /// <summary>按台账状态分组（ledger / window_empty / no_history）</summary>
    public const string GroupHistory = "history";

    // ==================== 1. 文案 ====================

    /// <summary>只读声明（界面与接口统一声明）</summary>
    public const string ReadOnlyText =
        "只读库存移动报表预览：仅按选定白名单字段与有界筛选读取库存行与库存流水台账，不新增 / 修改 / 删除任何记录";

    /// <summary>边界口径文案</summary>
    public const string BoundaryText =
        "口径：字段仅限 ERP-029 库存移动报表字段白名单；筛选仅限仓库 / 商品 / 截止日期 / 移动窗口 / 呆滞阈值；"
        + "数量一律为基础单位（取自库存流水已落库数量），最后移动日期 / 停滞天数无台账时以「未知」（null）呈现，"
        + "不臆造日期、比率或成本；不执行任意 SQL、不做写入";

    /// <summary>免责文案</summary>
    public const string DisclaimerText =
        "本预览为只读快照：不替代库存移动与呆滞报表主口径，也不估算库存成本或金额";

    // ==================== 2. 字段白名单（有限、有序） ====================

    /// <summary>字段定义：键 / 文案 / 数据类型 / 是否可筛选 / 从 ERP-029 报表行取值</summary>
    private sealed record FieldDef(
        string Key, string Label, string DataType, bool Filterable,
        Func<ReportDtos.InventoryMovementItem, object?> Selector);

    private static readonly IReadOnlyList<FieldDef> Fields = new List<FieldDef>
    {
        new("warehouseId", "仓库Id", "number", true, i => i.WarehouseId),
        new("warehouseName", "仓库名称", "text", false, i => i.WarehouseName),
        new("productId", "商品Id", "number", true, i => i.ProductId),
        new("productCode", "商品编码", "text", false, i => i.ProductCode),
        new("productName", "商品名称", "text", false, i => i.ProductName),
        new("spec", "规格", "text", false, i => i.Spec),
        new("unit", "基础单位", "text", false, i => i.Unit),
        new("currentQuantity", "当前现存量", "number", false, i => i.CurrentQuantity),
        new("lastMovementDate", "最后移动日期", "date", false, i => i.LastMovementDate),
        new("inboundQuantity", "窗口入库数量", "number", false, i => i.InboundQuantity),
        new("outboundQuantity", "窗口出库数量", "number", false, i => i.OutboundQuantity),
        new("netQuantity", "窗口净变动", "number", false, i => i.NetQuantity),
        new("movementCount", "窗口台账行数", "number", false, i => i.MovementCount),
        new("reversalCount", "窗口红字冲销行数", "number", false, i => i.ReversalCount),
        new("inactivityDays", "停滞天数", "number", false, i => i.InactivityDays),
        new("historyStatus", "台账状态", "enum", false, i => i.HistoryStatus),
        new("classification", "分类", "enum", false, i => i.Classification),
        new("note", "口径说明", "text", false, i => i.Note),
    };

    private static readonly Dictionary<string, FieldDef> FieldByKey =
        new(StringComparer.OrdinalIgnoreCase);

    static DynamicInventoryMovementReportRules()
    {
        foreach (var field in Fields)
            FieldByKey[field.Key] = field;
    }

    /// <summary>全部白名单字段键（目录顺序）</summary>
    public static IReadOnlyList<string> AllFieldKeys { get; } = Fields.Select(f => f.Key).ToList();

    // ==================== 3. 目录与字段 ====================

    /// <summary>有限白名单字段目录（按目录顺序）</summary>
    public static List<DynamicInventoryMovementReportFieldDto> GetCatalog() => Fields.Select(ToDto).ToList();

    /// <summary>完整目录（含所需菜单与有界额度口径）</summary>
    public static DynamicInventoryMovementReportCatalogDto GetCatalogDto() => new(
        GetCatalog(),
        RequiredMenuCode,
        RequiredMenuText,
        MaxPageSize,
        ReadOnlyText,
        BoundaryText);

    /// <summary>按字段键查找目录项（大小写不敏感；未知返回 null）</summary>
    public static DynamicInventoryMovementReportFieldDto? GetField(string key)
        => FieldByKey.TryGetValue(key.Trim(), out var def) ? ToDto(def) : null;

    private static DynamicInventoryMovementReportFieldDto ToDto(FieldDef def)
        => new(def.Key, def.Label, def.DataType, def.Filterable);

    // ==================== 4. 校验与规范化（全部在报告读取之前完成） ====================

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

    /// <summary>校验移动窗口日期区间（开始晚于结束 = 倒置，直接拒绝）</summary>
    public static void ValidateDateRange(DateTime? start, DateTime? end)
    {
        if (start.HasValue && end.HasValue && start.Value.Date > end.Value.Date)
            throw BusinessException.InvalidParameter("移动窗口开始日期不能晚于结束日期");
    }

    /// <summary>校验呆滞阈值（必须 >= MinInactiveDays，否则无法表达「停滞」）</summary>
    public static void ValidateInactiveDays(int inactiveDays)
    {
        if (inactiveDays < MinInactiveDays)
            throw BusinessException.InvalidParameter(
                $"呆滞阈值（天）必须大于等于 {MinInactiveDays}，当前为 {inactiveDays}");
    }

    /// <summary>校验每页条数（1 ~ 上限，超出直接拒绝）</summary>
    public static void ValidatePageSize(int pageSize)
    {
        if (pageSize < 1 || pageSize > MaxPageSize)
            throw BusinessException.InvalidParameter($"每页条数必须在 1~{MaxPageSize} 之间");
    }

    /// <summary>
    /// 把预览请求映射为 ERP-029 只读查询条件（仅仓库 / 商品 / 日期 / 阈值 / 分页）。
    /// 关键字与「仅正向数量」等额外口径不在此预览暴露，避免放宽数据可见性。
    /// </summary>
    public static ReportDtos.InventoryMovementReportQuery BuildQuery(
        DynamicInventoryMovementReportRequest request)
    {
        return new ReportDtos.InventoryMovementReportQuery
        {
            AsOfDate = request.AsOfDate,
            WindowStart = request.WindowStart,
            WindowEnd = request.WindowEnd,
            WarehouseId = request.WarehouseId,
            ProductId = request.ProductId,
            InactiveDays = request.InactiveDays,
            Page = request.Page,
            PageSize = request.PageSize,
        };
    }

    // ==================== 5. 行映射 ====================

    /// <summary>读取指定字段的值（未知字段 fail closed）</summary>
    public static object? Select(ReportDtos.InventoryMovementItem item, string key)
        => FieldByKey.TryGetValue(key.Trim(), out var def)
            ? def.Selector(item)
            : throw BusinessException.InvalidParameter($"未知字段: {key}");

    /// <summary>
    /// 把一行库存移动报表映射为「选定字段 → 值」的只读行（仅含选定字段）。
    /// 未知历史（最后移动日期 / 停滞天数 null）与基础单位数量语义保持不变。
    /// </summary>
    public static Dictionary<string, object?> BuildRow(
        ReportDtos.InventoryMovementItem item, IReadOnlyList<string> fieldKeys)
    {
        var row = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var key in fieldKeys)
            row[key] = Select(item, key);
        return row;
    }

    // ==================== 6. 分组计数（ERP-132） ====================

    /// <summary>
    /// 规范化分组键（fail closed）：空 / 留空 = 不分组（none）；仅接受 none / warehouse / classification / history（大小写不敏感）；
    /// 未知取值显式拒绝。
    /// </summary>
    public static string NormalizeGroupBy(string? groupBy)
    {
        if (string.IsNullOrWhiteSpace(groupBy))
            return GroupNone;

        var normalized = groupBy.Trim();
        if (string.Equals(normalized, GroupNone, StringComparison.OrdinalIgnoreCase)) return GroupNone;
        if (string.Equals(normalized, GroupWarehouse, StringComparison.OrdinalIgnoreCase)) return GroupWarehouse;
        if (string.Equals(normalized, GroupClassification, StringComparison.OrdinalIgnoreCase)) return GroupClassification;
        if (string.Equals(normalized, GroupHistory, StringComparison.OrdinalIgnoreCase)) return GroupHistory;

        throw BusinessException.InvalidParameter(
            $"无效的分组键: {groupBy}（可选：none / warehouse / classification / history）");
    }

    /// <summary>
    /// 分组行数分布（ERP-132）：从「当前授权预览页」的库存行计算行数分布，只统计行数、绝不跨不同商品 / 基础单位求和任何数量。
    /// <para>warehouse 为动态分组（只出现本页存在的仓库，按仓库 Id 升序）；classification / history 为固定分类，
    /// 空分类与未知历史分类始终保留（计数可为 0），确定性排序。none / 空页（warehouse）返回空列表。</para>
    /// </summary>
    public static List<DynamicInventoryMovementReportGroupDto> BuildGroupCounts(
        IEnumerable<ReportDtos.InventoryMovementItem> items, string groupBy)
    {
        var list = (items ?? Array.Empty<ReportDtos.InventoryMovementItem>()).ToList();
        var normalized = NormalizeGroupBy(groupBy);

        if (normalized == GroupWarehouse)
            return BuildWarehouseCounts(list);
        if (normalized == GroupClassification)
            return BuildClassificationCounts(list);
        if (normalized == GroupHistory)
            return BuildHistoryCounts(list);
        return new List<DynamicInventoryMovementReportGroupDto>();
    }

    private static List<DynamicInventoryMovementReportGroupDto> BuildWarehouseCounts(
        List<ReportDtos.InventoryMovementItem> items)
    {
        return items
            .GroupBy(i => i.WarehouseId)
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                var name = g.First().WarehouseName;
                return new DynamicInventoryMovementReportGroupDto(
                    $"warehouse:{g.Key}",
                    string.IsNullOrWhiteSpace(name) ? $"仓库 #{g.Key}" : name,
                    g.Count());
            })
            .ToList();
    }

    private static List<DynamicInventoryMovementReportGroupDto> BuildClassificationCounts(
        List<ReportDtos.InventoryMovementItem> items)
    {
        var categories = new (string Value, string Label)[]
        {
            (InventoryMovementSemantics.ClassActive, "正常流动"),
            (InventoryMovementSemantics.ClassStagnant, "呆滞"),
            (InventoryMovementSemantics.ClassUnknown, "无法判定"),
        };
        return categories.Select(c => new DynamicInventoryMovementReportGroupDto(
            $"classification:{c.Value}",
            c.Label,
            items.Count(i => i.Classification == c.Value))).ToList();
    }

    private static List<DynamicInventoryMovementReportGroupDto> BuildHistoryCounts(
        List<ReportDtos.InventoryMovementItem> items)
    {
        var categories = new (string Value, string Label)[]
        {
            (InventoryMovementSemantics.HistoryLedger, "有台账（窗口内有移动）"),
            (InventoryMovementSemantics.HistoryWindowEmpty, "有台账（窗口内无移动）"),
            (InventoryMovementSemantics.HistoryNoHistory, "无台账（历史库存 · 未知）"),
        };
        return categories.Select(c => new DynamicInventoryMovementReportGroupDto(
            $"history:{c.Value}",
            c.Label,
            items.Count(i => i.HistoryStatus == c.Value))).ToList();
    }
}
