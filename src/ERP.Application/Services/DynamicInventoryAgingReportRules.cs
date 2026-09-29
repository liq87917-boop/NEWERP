using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;

namespace ERP.Application.Services;

/// <summary>
/// 动态库存库龄与成本估值报表（ERP-135）的纯规则：字段白名单、字段 / 日期 / 仓库 / 商品 / 页大小校验、行投影与只读 / 边界 / 免责文案。
/// 无数据库依赖，便于逐条单测。
/// <para>复用既有「角色 → 菜单」模块授权（<c>stock-query</c> 库存查询菜单，见 <c>SeedData.Menus</c>）与
/// ERP-034 库存库龄与成本估值报表服务（<see cref="IReportService.GetInventoryAgingReportAsync"/>）；
/// 不新增任何表 / 列 / 权限模型，也不执行任何 SQL。</para>
/// </summary>
public static class DynamicInventoryAgingReportRules
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

    /// <summary>早于该年份的截止日期视为「无效」（<c>DateTime</c> 未赋值的默认年份为 1，绝不静默兜底为当天）</summary>
    public const int MinDateYear = 1900;

    // ==================== 1. 文案 ====================

    /// <summary>只读声明（界面与接口统一声明）</summary>
    public const string ReadOnlyText =
        "只读库存库龄与成本估值报表预览：仅按选定白名单字段与有界筛选读取库存行与库存流水台账，不新增 / 修改 / 删除任何记录";

    /// <summary>边界口径文案（库龄 / 估值证据边界与币种口径）</summary>
    public const string BoundaryText =
        "口径：字段仅限 ERP-034 库存库龄与成本估值报表字段白名单（标量字段 + 固定 5 格库龄分层数量 / 金额）；" +
        "筛选仅限仓库 / 商品 / 截止日期；数量一律为基础单位（现存量取自库存行、库龄分层由库存流水按 FIFO 派生），" +
        "没有台账分层依据的数量单列为「库龄未知」，不臆造入库日期、也不放进任何分层；" +
        "金额只使用库存行持久化的移动加权平均成本与库存金额（Stocks.AverageCost / Stocks.TotalCost），" +
        "成本依据缺失时金额一律为「未知」（null），绝不回落为 0、不估算成本、不做跨币种合并或汇率换算；" +
        "金额币种为库存行持久化的库存成本币种（CNY）";

    /// <summary>免责文案</summary>
    public const string DisclaimerText =
        "本预览为只读快照：不替代库存库龄与成本估值报表主口径，不重算、不重建历史成本、不计提跌价准备";

    // ==================== 2. 字段白名单（有限、有序） ====================

    /// <summary>字段定义：键 / 文案 / 数据类型 / 是否可筛选 / 从 ERP-034 报表行取值</summary>
    private sealed record FieldDef(
        string Key, string Label, string DataType, bool Filterable,
        Func<ReportDtos.InventoryAgingItem, object?> Selector);

    private static readonly IReadOnlyList<FieldDef> Fields = new List<FieldDef>
    {
        // ---- 标量字段（全部来自 ERP-034 InventoryAgingItem 的既有字段） ----
        new("warehouseId", "仓库Id", "number", true, i => i.WarehouseId),
        new("warehouseName", "仓库名称", "text", false, i => i.WarehouseName),
        new("productId", "商品Id", "number", true, i => i.ProductId),
        new("productCode", "商品编码", "text", false, i => i.ProductCode),
        new("productName", "商品名称", "text", false, i => i.ProductName),
        new("spec", "规格", "text", false, i => i.Spec),
        new("unit", "基础单位", "text", false, i => i.Unit),
        new("currentQuantity", "当前现存量", "number", false, i => i.CurrentQuantity),
        new("knownAgedQuantity", "有台账分层依据数量", "number", false, i => i.KnownAgedQuantity),
        new("unknownAgeQuantity", "库龄未知数量", "number", false, i => i.UnknownAgeQuantity),
        new("evidenceStatus", "库龄依据", "enum", false, i => i.EvidenceStatus),
        new("averageCost", "移动加权平均成本", "number", false, i => i.AverageCost),
        new("costStatus", "成本状态", "enum", false, i => i.CostStatus),
        new("costCurrency", "成本币种", "text", false, _ => InventoryAgingSemantics.CostCurrency),
        new("authoritativeAmount", "权威库存金额", "number", false, i => i.AuthoritativeAmount),
        new("agedAmount", "分层金额合计", "number", false, i => i.AgedAmount),
        new("unknownAgeAmount", "库龄未知金额", "number", false, i => i.UnknownAgeAmount),
        new("unknownCostQuantity", "成本未知数量", "number", false, i => i.UnknownCostQuantity),
        new("ledgerDeficitQuantity", "台账分层超额扣减差额", "number", false, i => i.LedgerDeficitQuantity),
        new("unpairedQuantity", "无可扣减分层数量", "number", false, i => i.UnpairedQuantity),
        new("reversalCount", "红字冲销流水条数", "number", false, i => i.ReversalCount),
        new("note", "口径说明", "text", false, i => i.Note),

        // ---- 固定 5 格库龄分层字段（数量 + 金额；金额成本未知时为 null） ----
        new("bucket0To30Quantity", $"{InventoryAgingSemantics.BucketLabel(InventoryAgingSemantics.Bucket0To30)}数量", "number", false,
            i => BucketOf(i, InventoryAgingSemantics.Bucket0To30)?.Quantity),
        new("bucket0To30Amount", $"{InventoryAgingSemantics.BucketLabel(InventoryAgingSemantics.Bucket0To30)}金额", "number", false,
            i => BucketOf(i, InventoryAgingSemantics.Bucket0To30)?.Amount),
        new("bucket31To60Quantity", $"{InventoryAgingSemantics.BucketLabel(InventoryAgingSemantics.Bucket31To60)}数量", "number", false,
            i => BucketOf(i, InventoryAgingSemantics.Bucket31To60)?.Quantity),
        new("bucket31To60Amount", $"{InventoryAgingSemantics.BucketLabel(InventoryAgingSemantics.Bucket31To60)}金额", "number", false,
            i => BucketOf(i, InventoryAgingSemantics.Bucket31To60)?.Amount),
        new("bucket61To90Quantity", $"{InventoryAgingSemantics.BucketLabel(InventoryAgingSemantics.Bucket61To90)}数量", "number", false,
            i => BucketOf(i, InventoryAgingSemantics.Bucket61To90)?.Quantity),
        new("bucket61To90Amount", $"{InventoryAgingSemantics.BucketLabel(InventoryAgingSemantics.Bucket61To90)}金额", "number", false,
            i => BucketOf(i, InventoryAgingSemantics.Bucket61To90)?.Amount),
        new("bucket91To180Quantity", $"{InventoryAgingSemantics.BucketLabel(InventoryAgingSemantics.Bucket91To180)}数量", "number", false,
            i => BucketOf(i, InventoryAgingSemantics.Bucket91To180)?.Quantity),
        new("bucket91To180Amount", $"{InventoryAgingSemantics.BucketLabel(InventoryAgingSemantics.Bucket91To180)}金额", "number", false,
            i => BucketOf(i, InventoryAgingSemantics.Bucket91To180)?.Amount),
        new("bucketOver180Quantity", $"{InventoryAgingSemantics.BucketLabel(InventoryAgingSemantics.BucketOver180)}数量", "number", false,
            i => BucketOf(i, InventoryAgingSemantics.BucketOver180)?.Quantity),
        new("bucketOver180Amount", $"{InventoryAgingSemantics.BucketLabel(InventoryAgingSemantics.BucketOver180)}金额", "number", false,
            i => BucketOf(i, InventoryAgingSemantics.BucketOver180)?.Amount),
    };

    private static readonly Dictionary<string, FieldDef> FieldByKey =
        new(StringComparer.OrdinalIgnoreCase);

    static DynamicInventoryAgingReportRules()
    {
        foreach (var field in Fields)
            FieldByKey[field.Key] = field;
    }

    /// <summary>全部白名单字段键（目录顺序）</summary>
    public static IReadOnlyList<string> AllFieldKeys { get; } = Fields.Select(f => f.Key).ToList();

    /// <summary>按分层键取行内的库龄分层格（键来自同一套口径常量；行内固定 5 格）</summary>
    private static ReportDtos.InventoryAgeBucketItem? BucketOf(
        ReportDtos.InventoryAgingItem item, string key)
        => item.Buckets.FirstOrDefault(b => string.Equals(b.Key, key, StringComparison.Ordinal));

    // ==================== 3. 目录与字段 ====================

    /// <summary>有限白名单字段目录（按目录顺序）</summary>
    public static List<DynamicInventoryAgingReportFieldDto> GetCatalog() => Fields.Select(ToDto).ToList();

    /// <summary>完整目录（含所需菜单与有界额度口径）</summary>
    public static DynamicInventoryAgingReportCatalogDto GetCatalogDto() => new(
        GetCatalog(),
        RequiredMenuCode,
        RequiredMenuText,
        MaxPageSize,
        ReadOnlyText,
        BoundaryText);

    /// <summary>按字段键查找目录项（大小写不敏感；未知返回 null）</summary>
    public static DynamicInventoryAgingReportFieldDto? GetField(string key)
        => FieldByKey.TryGetValue(key.Trim(), out var def) ? ToDto(def) : null;

    private static DynamicInventoryAgingReportFieldDto ToDto(FieldDef def)
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

    /// <summary>校验截止日期（fail closed）：年份过小（<see cref="MinDateYear"/>）视为无效，绝不静默兜底为当天</summary>
    public static void ValidateAsOfDate(DateTime? asOfDate)
    {
        var date = (asOfDate ?? DateTime.Today).Date;
        if (date.Year < MinDateYear)
            throw BusinessException.InvalidParameter($"as-of 日期无效：{date:yyyy-MM-dd}");
    }

    /// <summary>校验仓库筛选（可空 = 全部；非空必须为正整数）</summary>
    public static void ValidateWarehouseId(long? warehouseId)
    {
        if (warehouseId.HasValue && warehouseId.Value <= 0)
            throw BusinessException.InvalidParameter("仓库筛选 Id 必须为正整数");
    }

    /// <summary>校验商品筛选（可空 = 全部；非空必须为正整数）</summary>
    public static void ValidateProductId(long? productId)
    {
        if (productId.HasValue && productId.Value <= 0)
            throw BusinessException.InvalidParameter("商品筛选 Id 必须为正整数");
    }

    /// <summary>校验每页条数（1 ~ 上限，超出直接拒绝）</summary>
    public static void ValidatePageSize(int pageSize)
    {
        if (pageSize < 1 || pageSize > MaxPageSize)
            throw BusinessException.InvalidParameter($"每页条数必须在 1~{MaxPageSize} 之间");
    }

    /// <summary>
    /// 把预览请求映射为 ERP-034 只读查询条件（仅仓库 / 商品 / 截止日期 / 分页）。
    /// 关键字与「仅正向数量」等额外口径不在此预览暴露，避免放宽数据可见性。
    /// </summary>
    public static ReportDtos.InventoryAgingReportQuery BuildQuery(
        DynamicInventoryAgingReportRequest request)
    {
        return new ReportDtos.InventoryAgingReportQuery
        {
            AsOfDate = request.AsOfDate,
            WarehouseId = request.WarehouseId,
            ProductId = request.ProductId,
            Page = request.Page,
            PageSize = request.PageSize,
        };
    }

    // ==================== 5. 行映射 ====================

    /// <summary>读取指定字段的值（未知字段 fail closed）</summary>
    public static object? Select(ReportDtos.InventoryAgingItem item, string key)
        => FieldByKey.TryGetValue(key.Trim(), out var def)
            ? def.Selector(item)
            : throw BusinessException.InvalidParameter($"未知字段: {key}");

    /// <summary>
    /// 把一行库存库龄报表映射为「选定字段 → 值」的只读行（仅含选定字段）。
    /// 未知库龄（库龄未知数量）与未知成本（成本状态 unknown、金额 null）语义保持不变。
    /// </summary>
    public static Dictionary<string, object?> BuildRow(
        ReportDtos.InventoryAgingItem item, IReadOnlyList<string> fieldKeys)
    {
        var row = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var key in fieldKeys)
            row[key] = Select(item, key);
        return row;
    }
}
