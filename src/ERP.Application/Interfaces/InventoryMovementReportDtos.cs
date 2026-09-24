using ERP.Application.Common;

namespace ERP.Application.Interfaces;

/// <summary>
/// 库存移动与呆滞报表口径常量（ERP-029）：后端派生、前端展示与测试断言共用同一套字符串口径，
/// 避免各处自行拼写导致「未知」被当成 0、或呆滞判定与阈值不一致。
/// </summary>
public static class InventoryMovementSemantics
{
    /// <summary>台账状态：截止日期前存在库存流水，且移动窗口内也有移动</summary>
    public const string HistoryLedger = "ledger";

    /// <summary>台账状态：截止日期前有流水，但移动窗口内无任何移动（窗口内合计是真实的 0，不是未知）</summary>
    public const string HistoryWindowEmpty = "window_empty";

    /// <summary>台账状态：截止日期前完全没有流水（流水起点之前的历史库存），最后移动日期 / 停滞天数均未知</summary>
    public const string HistoryNoHistory = "no_history";

    /// <summary>分类：正常流动（停滞天数小于阈值）</summary>
    public const string ClassActive = "active";

    /// <summary>分类：呆滞（停滞天数大于等于阈值）</summary>
    public const string ClassStagnant = "stagnant";

    /// <summary>分类：无法判定（无台账，不臆造最后移动日期与停滞天数）</summary>
    public const string ClassUnknown = "unknown";

    /// <summary>口径说明（后端返回、前端展示、测试断言共用同一份文案）</summary>
    public const string RuleText =
        "口径：主表为库存行（Stocks），数量一律取库存流水已落库的基础单位数量，不做事后单位换算；" +
        "「最后移动日期 / 入库数量 / 出库数量 / 停滞天数」全部取自库存流水（StockMovements）台账，" +
        "并以「移动日期 <= 截止日期」截断（截止日期之后的移动不计入本次口径）；" +
        "窗口内出入库为台账毛额（方向 × 数量）：红字冲销流水以反方向记录，原流水与其红字成对净额为 0，" +
        "因此不做二次扣减、也不删除或改写任何一行；" +
        "截止日期前没有任何台账的库存行，「最后移动日期 / 停滞天数」记为未知（null），不臆造日期、比率或估价；" +
        "呆滞判定只依据「截止日期 - 最后移动日期 >= 阈值」；" +
        "本报表不估算库存成本或金额，库存金额口径仍以库存流水金额与库存行金额为准。";

    /// <summary>范围说明：合计与分类计数只统计本次返回页的行（分页有界，不做无界全量统计）</summary>
    public const string PageScopeText =
        "以下合计与分类计数只统计本次返回页的行；total 为符合筛选条件的库存行总数。";
}

/// <summary>
/// 库存移动与呆滞报表 DTO（ERP-029，只读派生：不落库、不改单据状态、不新增或修改任何表列）
/// </summary>
public static partial class ReportDtos
{
    /// <summary>库存移动与呆滞报表查询条件（全部为只读筛选参数）</summary>
    public sealed class InventoryMovementReportQuery
    {
        /// <summary>呆滞阈值（天）最小值：小于该值时无法表达「停滞」含义，直接拒绝</summary>
        public const int MinInactiveDays = 1;

        /// <summary>默认呆滞阈值（天）</summary>
        public const int DefaultInactiveDays = 90;

        /// <summary>默认移动窗口天数（含首尾；未显式传窗口时按截止日期往前推算）</summary>
        public const int DefaultWindowDays = 90;

        /// <summary>默认每页条数</summary>
        public const int DefaultPageSize = 50;

        /// <summary>每页条数上限（有界：单次请求最多返回这么多行）</summary>
        public const int MaxPageSize = 200;

        /// <summary>截止日期（报表时点，默认今天；截止日期之后的移动不计入）</summary>
        public DateTime? AsOfDate { get; set; }

        /// <summary>移动窗口开始日期（默认 = 截止日期往前 DefaultWindowDays 天，含当天）</summary>
        public DateTime? WindowStart { get; set; }

        /// <summary>移动窗口结束日期（默认 = 截止日期；晚于截止日期时按截止日期截断）</summary>
        public DateTime? WindowEnd { get; set; }

        /// <summary>仓库筛选（留空 = 全部仓库）</summary>
        public long? WarehouseId { get; set; }

        /// <summary>商品筛选（留空 = 全部商品）</summary>
        public long? ProductId { get; set; }

        /// <summary>关键字（匹配商品资料的编码 / 名称；留空 = 不过滤）</summary>
        public string? Keyword { get; set; }

        /// <summary>呆滞阈值（天）：停滞天数 >= 该值判为呆滞；必须 >= MinInactiveDays</summary>
        public int InactiveDays { get; set; } = DefaultInactiveDays;

        /// <summary>仅列出当前现存量 &gt; 0 的库存行（默认 true；false 时零数量行也会列出并标注）</summary>
        public bool OnlyPositiveQuantity { get; set; } = true;

        /// <summary>页码（从 1 开始）</summary>
        public int Page { get; set; } = 1;

        /// <summary>每页条数（1 ~ MaxPageSize，超出按上限截断）</summary>
        public int PageSize { get; set; } = DefaultPageSize;

        /// <summary>
        /// 归一化并校验：补齐默认值与钳制分页；非法阈值或倒置窗口直接抛业务异常（参数错误）。
        /// </summary>
        public void Normalize()
        {
            var asOf = (AsOfDate ?? DateTime.Today).Date;
            var windowEnd = (WindowEnd ?? asOf).Date;
            if (windowEnd > asOf) windowEnd = asOf;   // 窗口不得越过报表时点
            var windowStart = (WindowStart ?? windowEnd.AddDays(-(DefaultWindowDays - 1))).Date;
            if (windowStart > windowEnd)
            {
                throw BusinessException.InvalidParameter(
                    $"移动窗口开始日期 {windowStart:yyyy-MM-dd} 不能晚于结束日期 {windowEnd:yyyy-MM-dd}");
            }

            if (InactiveDays < MinInactiveDays)
            {
                throw BusinessException.InvalidParameter(
                    $"呆滞阈值（天）必须大于等于 {MinInactiveDays}，当前为 {InactiveDays}");
            }

            if (Page < 1) Page = 1;
            if (PageSize < 1) PageSize = DefaultPageSize;
            if (PageSize > MaxPageSize) PageSize = MaxPageSize;

            AsOfDate = asOf;
            WindowStart = windowStart;
            WindowEnd = windowEnd;

            if (Keyword is not null)
            {
                Keyword = Keyword.Trim();
                if (Keyword.Length == 0) Keyword = null;
            }
        }
    }

    /// <summary>库存移动与呆滞报表行（一个仓库 + 一个商品；数量一律为基础单位）</summary>
    public sealed class InventoryMovementItem
    {
        public long WarehouseId { get; set; }

        /// <summary>仓库名称（仓库资料缺失时为空串）</summary>
        public string WarehouseName { get; set; } = string.Empty;

        public long ProductId { get; set; }

        /// <summary>商品编码（商品资料缺失时为空串）</summary>
        public string ProductCode { get; set; } = string.Empty;

        /// <summary>商品名称（商品资料缺失时为空串）</summary>
        public string ProductName { get; set; } = string.Empty;

        /// <summary>规格</summary>
        public string Spec { get; set; } = string.Empty;

        /// <summary>基础单位（取商品资料单位；商品资料缺失时为空串，不臆造）</summary>
        public string Unit { get; set; } = string.Empty;

        /// <summary>当前现存量（基础单位，来自库存行，报表时点的权威库存数量）</summary>
        public decimal CurrentQuantity { get; set; }

        /// <summary>截止日期前最后一次台账移动日期；null = 未知（截止日期前无任何流水，不臆造）</summary>
        public DateTime? LastMovementDate { get; set; }

        /// <summary>窗口内入库数量（台账毛额：方向 +1 的流水数量合计，含红字冲销腿）</summary>
        public decimal InboundQuantity { get; set; }

        /// <summary>窗口内出库数量（台账毛额：方向 -1 的流水数量合计，含红字冲销腿）</summary>
        public decimal OutboundQuantity { get; set; }

        /// <summary>窗口内净变动（= 入库 - 出库；红字流水以反方向参与，成对净额为 0）</summary>
        public decimal NetQuantity { get; set; }

        /// <summary>窗口内台账行数（含红字冲销行）</summary>
        public int MovementCount { get; set; }

        /// <summary>窗口内红字冲销行数（用于核对毛额与净额差异）</summary>
        public int ReversalCount { get; set; }

        /// <summary>停滞天数（截止日期 - 最后移动日期）；null = 未知（无台账）</summary>
        public int? InactivityDays { get; set; }

        /// <summary>台账状态：ledger / window_empty / no_history</summary>
        public string HistoryStatus { get; set; } = InventoryMovementSemantics.HistoryNoHistory;

        /// <summary>分类：active / stagnant / unknown</summary>
        public string Classification { get; set; } = InventoryMovementSemantics.ClassUnknown;

        /// <summary>该行口径说明（缺什么、为什么不推断）</summary>
        public string Note { get; set; } = string.Empty;
    }

    /// <summary>库存移动与呆滞报表结果（只读派生；合计与计数均限定在本次返回页内）</summary>
    public sealed class InventoryMovementReport
    {
        /// <summary>截止日期（报表时点）</summary>
        public DateTime AsOfDate { get; set; }

        /// <summary>移动窗口开始日期（含）</summary>
        public DateTime WindowStart { get; set; }

        /// <summary>移动窗口结束日期（含）</summary>
        public DateTime WindowEnd { get; set; }

        /// <summary>本次使用的呆滞阈值（天）</summary>
        public int InactiveDays { get; set; }

        /// <summary>是否只列出当前现存量 &gt; 0 的库存行</summary>
        public bool OnlyPositiveQuantity { get; set; }

        /// <summary>仓库筛选回显</summary>
        public long? WarehouseId { get; set; }

        /// <summary>商品筛选回显</summary>
        public long? ProductId { get; set; }

        /// <summary>关键字筛选回显</summary>
        public string Keyword { get; set; } = string.Empty;

        /// <summary>符合筛选条件的库存行总数</summary>
        public int Total { get; set; }

        /// <summary>当前页码</summary>
        public int Page { get; set; }

        /// <summary>每页条数（已按上限截断）</summary>
        public int PageSize { get; set; }

        /// <summary>总页数</summary>
        public int TotalPages { get; set; }

        /// <summary>本页正常流动行数（active）</summary>
        public int ActiveCount { get; set; }

        /// <summary>本页呆滞行数（stagnant：停滞天数 >= 阈值）</summary>
        public int StagnantCount { get; set; }

        /// <summary>本页无台账行数（no_history：最后移动日期与停滞天数未知）</summary>
        public int InsufficientHistoryCount { get; set; }

        /// <summary>本页台账存在但窗口内无移动的行数（window_empty）</summary>
        public int WindowEmptyCount { get; set; }

        /// <summary>本页现存量合计（基础单位）</summary>
        public decimal PageCurrentQuantity { get; set; }

        /// <summary>本页窗口内入库合计（基础单位毛额）</summary>
        public decimal PageInboundQuantity { get; set; }

        /// <summary>本页窗口内出库合计（基础单位毛额）</summary>
        public decimal PageOutboundQuantity { get; set; }

        /// <summary>本页窗口内净变动合计（基础单位）</summary>
        public decimal PageNetQuantity { get; set; }

        /// <summary>口径说明（与后端派生同源）</summary>
        public string Rule { get; set; } = InventoryMovementSemantics.RuleText;

        /// <summary>范围说明（合计仅统计本页）</summary>
        public string ScopeNote { get; set; } = InventoryMovementSemantics.PageScopeText;

        /// <summary>本页报表行（按仓库 + 商品排序，与分页顺序一致）</summary>
        public List<InventoryMovementItem> Items { get; set; } = new();
    }
}
