using ERP.Application.Common;

namespace ERP.Application.Interfaces;

/// <summary>
/// 库存库龄与成本估值报表口径常量（ERP-034）：后端派生、前端展示与测试断言共用同一套字符串口径，
/// 避免各处自行拼写导致「未知」被当成 0、库龄边界不一致或跨币种合并。
/// </summary>
public static class InventoryAgingSemantics
{
    /// <summary>库龄分层键：0-30 天（闭区间）</summary>
    public const string Bucket0To30 = "0-30";

    /// <summary>库龄分层键：31-60 天（闭区间）</summary>
    public const string Bucket31To60 = "31-60";

    /// <summary>库龄分层键：61-90 天（闭区间）</summary>
    public const string Bucket61To90 = "61-90";

    /// <summary>库龄分层键：91-180 天（闭区间）</summary>
    public const string Bucket91To180 = "91-180";

    /// <summary>库龄分层键：180 天以上（不设上限）</summary>
    public const string BucketOver180 = "over_180";

    /// <summary>分层键顺序（= 报表列顺序与 KPI 顺序；前后端与测试断言共用）</summary>
    public static readonly IReadOnlyList<string> BucketKeys =
        new[] { Bucket0To30, Bucket31To60, Bucket61To90, Bucket91To180, BucketOver180 };

    /// <summary>分层中文标签（后端返回、前端展示、测试断言共用）</summary>
    public static string BucketLabel(string key) => key switch
    {
        Bucket0To30 => "0-30 天",
        Bucket31To60 => "31-60 天",
        Bucket61To90 => "61-90 天",
        Bucket91To180 => "91-180 天",
        BucketOver180 => "180 天以上",
        _ => key
    };

    /// <summary>分层下界（天，闭区间）</summary>
    public static int BucketMinDays(string key) => key switch
    {
        Bucket0To30 => 0,
        Bucket31To60 => 31,
        Bucket61To90 => 61,
        Bucket91To180 => 91,
        BucketOver180 => 181,
        _ => 0
    };

    /// <summary>分层上界（天，闭区间）；null = 不设上限（180 天以上）</summary>
    public static int? BucketMaxDays(string key) => key switch
    {
        Bucket0To30 => 30,
        Bucket31To60 => 60,
        Bucket61To90 => 90,
        Bucket91To180 => 180,
        _ => null
    };

    /// <summary>
    /// 库龄天数 → 分层键（闭区间边界：30 天在 0-30、31 天在 31-60、90 天在 61-90、91 天在 91-180、180 天在 91-180、181 天在 180 天以上）
    /// </summary>
    public static string BucketOf(int ageDays) =>
        ageDays <= 30 ? Bucket0To30
        : ageDays <= 60 ? Bucket31To60
        : ageDays <= 90 ? Bucket61To90
        : ageDays <= 180 ? Bucket91To180
        : BucketOver180;

    /// <summary>库龄依据：全部现存量都有台账分层依据</summary>
    public const string EvidenceFull = "full";

    /// <summary>库龄依据：部分现存量没有台账分层依据（流水起点之前的历史库存，单列为库龄未知）</summary>
    public const string EvidencePartial = "partial";

    /// <summary>库龄依据：现存量完全没有台账分层依据（截止日期前没有任何流水）</summary>
    public const string EvidenceNone = "none";

    /// <summary>成本状态：有成本依据（Stocks.AverageCost 与 Stocks.TotalCost 均大于 0）</summary>
    public const string CostKnown = "known";

    /// <summary>成本状态：无成本依据（金额一律为未知 null，不回落为 0）</summary>
    public const string CostUnknown = "unknown";

    /// <summary>库存成本币种：库存行与库存流水没有币种列，持久化成本历来以人民币计价</summary>
    public const string CostCurrency = "CNY";

    /// <summary>金额保留位数（与库存金额口径一致：4 位小数）</summary>
    public const int AmountDecimals = 4;

    /// <summary>金额取整（与 <c>InventoryService</c> 的金额口径一致：4 位小数、AwayFromZero）</summary>
    public static decimal RoundAmount(decimal value)
        => Math.Round(value, AmountDecimals, MidpointRounding.AwayFromZero);

    /// <summary>数量 / 库龄口径文案（后端返回、前端展示、测试断言共用同一份文案）</summary>
    public const string RuleText =
        "口径：主表为库存行（Stocks），现存量一律取库存流水已落库的基础单位数量，不做事后单位换算；" +
        "库龄分层由库存流水（StockMovements）按 FIFO 分层派生——方向 +1 的入库流水按其移动日期与数量形成一层，" +
        "方向 -1 的出库流水按移动日期（同日按流水 Id）从最早的分层开始消耗，剩余分层即现存量的库龄依据；" +
        "分层按「截止日期 - 分层日期」的天数归入 0-30 / 31-60 / 61-90 / 91-180 / 180 天以上（均为闭区间）；" +
        "流水一律以「移动日期 <= 截止日期」截断，截止日期之后的流水不进入本次口径、也不被删除；" +
        "红字冲销按 ReversalOfMovementId 权威配对：冲销一张入库时扣回该入库自己的分层（该分层已被消耗的部分按 FIFO 扣减），" +
        "冲销一张出库时原出库并未发生，按原出库的移动日期与数量回补一层；被冲销的原流水不删除、不改写；" +
        "现存量大于台账分层合计的差额（流水起点之前的历史库存）单列为「库龄未知」，不臆造入库日期、也不放进任何分层；" +
        "台账分层合计大于现存量时按 FIFO 口径在最早分层扣减，并在行说明中留痕；" +
        "本报表只读：不落库、不改库存、不改单据状态、不新增或修改任何表列，也不重建或补齐历史流水。";

    /// <summary>估值与币种口径文案（后端返回、前端展示、测试断言共用同一份文案）</summary>
    public const string CostRuleText =
        "估值口径：分层金额 = 分层数量 × 库存行持久化的移动加权平均成本（Stocks.AverageCost，ERP-009），" +
        "按金额 4 位小数分摊并把取整残差并入数量最多的那一格，因此「5 个分层金额之和 + 库龄未知金额」恒等于该行的权威金额；" +
        "行的权威金额为持久化的 Stocks.TotalCost（= Σ 库存流水金额），本报表不重算、不重建历史成本、不计提跌价准备；" +
        "成本依据缺失（Stocks.AverageCost 或 Stocks.TotalCost 为 0）的行，其数量与金额一律记为「未知」（金额为 null），" +
        "单列计数并排除在任何权威合计之外，既不估算成本、也不回落为 0；" +
        "币种口径：库存行与库存流水没有币种列，持久化成本历来以人民币（CNY）计价" +
        "（ERP-009 / ERP-033：外币采购价只有订单持久化了非占位汇率换算后才进入库存成本），" +
        "因此本报表的金额合计全部是同一币种，不做跨币种合并，也不从数据字典（BaseOtherInfo / 系统参数）推断或套用汇率。";

    /// <summary>范围说明：合计、分层合计与计数只统计本次返回页的行（分页有界，不做无界全量统计）</summary>
    public const string PageScopeText =
        "以下合计、分层合计与计数只统计本次返回页的行；total 为符合筛选条件的库存行总数。";
}

/// <summary>
/// 库存库龄与成本估值报表 DTO（ERP-034，只读派生：不落库、不改单据状态、不新增或修改任何表列）
/// </summary>
public static partial class ReportDtos
{
    /// <summary>库存库龄与成本估值报表查询条件（全部为只读筛选参数）</summary>
    public sealed class InventoryAgingReportQuery
    {
        /// <summary>默认每页条数</summary>
        public const int DefaultPageSize = 50;

        /// <summary>每页条数上限（有界：单次请求最多返回这么多行）</summary>
        public const int MaxPageSize = 200;

        /// <summary>截止日期（报表时点，默认今天；截止日期之后的流水不进入库龄口径）</summary>
        public DateTime? AsOfDate { get; set; }

        /// <summary>仓库筛选（留空 = 全部仓库）</summary>
        public long? WarehouseId { get; set; }

        /// <summary>商品筛选（留空 = 全部商品）</summary>
        public long? ProductId { get; set; }

        /// <summary>关键字（匹配商品资料的编码 / 名称；留空 = 不过滤）</summary>
        public string? Keyword { get; set; }

        /// <summary>仅列出当前现存量 &gt; 0 的库存行（默认 true；false 时零数量行也会列出并标注）</summary>
        public bool OnlyPositiveQuantity { get; set; } = true;

        /// <summary>页码（从 1 开始）</summary>
        public int Page { get; set; } = 1;

        /// <summary>每页条数（1 ~ MaxPageSize，超出按上限截断）</summary>
        public int PageSize { get; set; } = DefaultPageSize;

        /// <summary>归一化并校验：补齐默认日期、钳制分页、去掉空白关键字（非法取值一律不臆造，只做有界钳制）</summary>
        public void Normalize()
        {
            AsOfDate = (AsOfDate ?? DateTime.Today).Date;
            if (Page < 1) Page = 1;
            if (PageSize < 1) PageSize = DefaultPageSize;
            if (PageSize > MaxPageSize) PageSize = MaxPageSize;

            if (Keyword is not null)
            {
                Keyword = Keyword.Trim();
                if (Keyword.Length == 0) Keyword = null;
            }
        }
    }

    /// <summary>库龄分层格（数量一律为基础单位；金额按库存行持久化加权平均成本分摊，成本未知时为 null）</summary>
    public sealed class InventoryAgeBucketItem
    {
        /// <summary>分层键（0-30 / 31-60 / 61-90 / 91-180 / over_180）</summary>
        public string Key { get; set; } = string.Empty;

        /// <summary>分层中文标签</summary>
        public string Label { get; set; } = string.Empty;

        /// <summary>分层下界（天，闭区间）</summary>
        public int MinDays { get; set; }

        /// <summary>分层上界（天，闭区间）；null = 不设上限</summary>
        public int? MaxDays { get; set; }

        /// <summary>该分层的基础单位数量（没有台账分层依据的数量不在任何分层内，单列为库龄未知）</summary>
        public decimal Quantity { get; set; }

        /// <summary>该分层金额（成本未知时为 null = 未知，绝不回落为 0）</summary>
        public decimal? Amount { get; set; }
    }

    /// <summary>库存库龄与成本估值报表行（一个仓库 + 一个商品；数量一律为基础单位）</summary>
    public sealed class InventoryAgingItem
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

        /// <summary>有台账分层依据的数量（= 5 个分层数量之和）</summary>
        public decimal KnownAgedQuantity { get; set; }

        /// <summary>库龄未知数量（现存量 - 台账分层合计；流水起点之前的历史库存，不放进任何分层）</summary>
        public decimal UnknownAgeQuantity { get; set; }

        /// <summary>库龄依据：full / partial / none</summary>
        public string EvidenceStatus { get; set; } = InventoryAgingSemantics.EvidenceNone;

        /// <summary>持久化移动加权平均成本（成本未知时为 0，含义由 CostStatus 表达）</summary>
        public decimal AverageCost { get; set; }

        /// <summary>成本状态：known / unknown</summary>
        public string CostStatus { get; set; } = InventoryAgingSemantics.CostUnknown;

        /// <summary>行权威金额（持久化 Stocks.TotalCost）；成本未知时为 null（未知，不回落为 0）</summary>
        public decimal? AuthoritativeAmount { get; set; }

        /// <summary>分层金额合计（按持久化加权平均成本分摊的派生值）；成本未知时为 null</summary>
        public decimal? AgedAmount { get; set; }

        /// <summary>库龄未知数量的金额（同一套持久化加权平均成本分摊）；成本未知时为 null</summary>
        public decimal? UnknownAgeAmount { get; set; }

        /// <summary>成本未知的数量（成本未知时 = 现存量；成本已知时 = 0），与权威合计严格分离</summary>
        public decimal UnknownCostQuantity { get; set; }

        /// <summary>台账分层合计大于现存量时按 FIFO 口径在最早分层扣减的差额（不新增或改写任何流水）</summary>
        public decimal LedgerDeficitQuantity { get; set; }

        /// <summary>没有可扣减分层的出库 / 红字腿数量（流水起点之前的入库），不臆造分层</summary>
        public decimal UnpairedQuantity { get; set; }

        /// <summary>截止日期（含）之前的红字冲销流水条数（用于核对配对口径）</summary>
        public int ReversalCount { get; set; }

        /// <summary>库龄分层（固定 5 格，顺序与 BucketKeys 一致；没有数量的分层为 0）</summary>
        public List<InventoryAgeBucketItem> Buckets { get; set; } = new();

        /// <summary>该行口径说明（缺什么、为什么不推断）</summary>
        public string Note { get; set; } = string.Empty;
    }

    /// <summary>库存库龄与成本估值报表结果（只读派生；合计、分层合计与计数均限定在本次返回页内）</summary>
    public sealed class InventoryAgingReport
    {
        /// <summary>截止日期（报表时点）</summary>
        public DateTime AsOfDate { get; set; }

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

        /// <summary>库存成本币种（库存行 / 库存流水没有币种列，持久化成本统一为本币）</summary>
        public string CostCurrency { get; set; } = InventoryAgingSemantics.CostCurrency;

        /// <summary>本页现存量合计（基础单位）</summary>
        public decimal PageCurrentQuantity { get; set; }

        /// <summary>本页有台账分层依据的数量合计</summary>
        public decimal PageKnownAgedQuantity { get; set; }

        /// <summary>本页库龄未知数量合计（不放进任何分层）</summary>
        public decimal PageUnknownAgeQuantity { get; set; }

        /// <summary>本页权威库存金额合计（Σ Stocks.TotalCost，仅成本已知行）；本页全部行都无成本依据时为 null（未知）</summary>
        public decimal? PageAuthoritativeAmount { get; set; }

        /// <summary>本页成本未知的数量合计（与权威合计严格分离）</summary>
        public decimal PageUnknownCostQuantity { get; set; }

        /// <summary>本页有成本依据的行数</summary>
        public int KnownCostCount { get; set; }

        /// <summary>本页无成本依据的行数</summary>
        public int UnknownCostCount { get; set; }

        /// <summary>本页库龄依据完整的行数（full）</summary>
        public int FullEvidenceCount { get; set; }

        /// <summary>本页库龄依据部分缺失的行数（partial）</summary>
        public int PartialEvidenceCount { get; set; }

        /// <summary>本页库龄依据完全缺失的行数（none）</summary>
        public int NoEvidenceCount { get; set; }

        /// <summary>本页各分层合计（数量与金额；金额在相关行都没有成本依据时为 null）</summary>
        public List<InventoryAgeBucketItem> PageBuckets { get; set; } = new();

        /// <summary>数量 / 库龄口径说明（与后端派生同源）</summary>
        public string Rule { get; set; } = InventoryAgingSemantics.RuleText;

        /// <summary>估值 / 币种口径说明（与后端派生同源）</summary>
        public string CostRule { get; set; } = InventoryAgingSemantics.CostRuleText;

        /// <summary>范围说明（合计仅统计本页）</summary>
        public string ScopeNote { get; set; } = InventoryAgingSemantics.PageScopeText;

        /// <summary>本页报表行（按仓库 + 商品排序，与分页顺序一致）</summary>
        public List<InventoryAgingItem> Items { get; set; } = new();
    }
}
