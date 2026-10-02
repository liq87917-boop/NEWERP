namespace ERP.Application.Interfaces;

/// <summary>
/// 报表数据传输对象
/// </summary>
public static partial class ReportDtos
{
    /// <summary>商品销量排名项</summary>
    public class ProductSalesRankItem
    {
        public long ProductId { get; set; }
        public string ProductCode { get; set; } = string.Empty;
        public string ProductName { get; set; } = string.Empty;
        public string Spec { get; set; } = string.Empty;
        public string Unit { get; set; } = string.Empty;
        public decimal TotalQuantity { get; set; }
        /// <summary>数量 × 商品当前售价的估算金额（币种未知，仅估算，非实际发货收入）</summary>
        public decimal TotalAmount { get; set; }
        /// <summary>金额口径证据标签：明确「当前价估算、币种未知、非实际发货收入」</summary>
        public string AmountLabel { get; set; } = string.Empty;
        public int Rank { get; set; }
    }

    /// <summary>
    /// 订单利润暂估项（ERP-219 / ERP-220，只读派生）：
    /// 销售额保留订单原币 <c>TotalAmount</c>，绝不换算汇率、不改动金额；
    /// 成本 / 利润 / 利润率为未知（null，绝不减去币种未知的当前成本价、绝不回落为 0）；
    /// 「当前价估算」仅为数量 × 商品当前 CostPrice 的独立口径（币种未知，仅估算），
    /// 明细 / 商品缺失或已删除时为 null 并给出显式原因。
    /// </summary>
    public class OrderProfitItem
    {
        /// <summary>订单 Id（订单身份）</summary>
        public long OrderId { get; set; }

        /// <summary>客户 Id（客户身份）</summary>
        public long CustomerId { get; set; }

        /// <summary>订单号</summary>
        public string OrderNo { get; set; } = string.Empty;

        /// <summary>订单日期</summary>
        public DateTime OrderDate { get; set; }

        /// <summary>客户名称</summary>
        public string CustomerName { get; set; } = string.Empty;

        /// <summary>原币币种编码（如 CNY / USD；未知取值归入「未知币种」）</summary>
        public string Currency { get; set; } = string.Empty;

        /// <summary>原币币种标签（如「USD 美元」；未知取值为「未知币种」）</summary>
        public string CurrencyLabel { get; set; } = string.Empty;

        /// <summary>销售额 = 订单 <c>TotalAmount</c>（原币，未做任何汇率换算）</summary>
        public decimal SalesAmount { get; set; }

        /// <summary>销售额口径证据标签</summary>
        public string SalesAmountLabel { get; set; } = string.Empty;

        /// <summary>成本金额（未知：当前无可信、可比较的历史成本依据；绝不回落为 0）</summary>
        public decimal? CostAmount { get; set; }

        /// <summary>利润（未知：绝不减去币种未知的当前成本价）</summary>
        public decimal? Profit { get; set; }

        /// <summary>利润率 %（未知）</summary>
        public decimal? ProfitRate { get; set; }

        /// <summary>成本证据标签（未知原因）</summary>
        public string CostEvidence { get; set; } = string.Empty;

        /// <summary>利润证据标签（未知原因）</summary>
        public string ProfitEvidence { get; set; } = string.Empty;

        /// <summary>当前价估算金额（数量 × 商品当前 CostPrice；币种未知，仅估算；明细 / 商品缺失或已删除时为 null）</summary>
        public decimal? CurrentPriceEstimate { get; set; }

        /// <summary>当前价估算口径标签（币种未知，仅估算，非历史成本）</summary>
        public string CurrentPriceEstimateLabel { get; set; } = string.Empty;

        /// <summary>当前价估算不可用的显式原因（可估算时为空字符串）</summary>
        public string CurrentPriceEstimateReason { get; set; } = string.Empty;
    }

    /// <summary>客户出货量统计项（证据口径：已审核、未删除销售订单的数量与金额，非实际出库/装柜/收款）</summary>
    public class CustomerShipmentItem
    {
        public long CustomerId { get; set; }
        public string CustomerName { get; set; } = string.Empty;
        public int OrderCount { get; set; }
        public decimal TotalQuantity { get; set; }
        public decimal TotalAmount { get; set; }

        /// <summary>数量口径证据标签：已审核订单明细数量合计，非实际出库 / 装柜数量</summary>
        public string QuantityLabel { get; set; } = string.Empty;

        /// <summary>金额口径证据标签：已审核订单金额合计（原币），非实际收款金额</summary>
        public string AmountLabel { get; set; } = string.Empty;
    }

    /// <summary>业务员产值项</summary>
    public class SalesmanOutputItem
    {
        public long SalesmanId { get; set; }
        public string SalesmanName { get; set; } = string.Empty;
        public int OrderCount { get; set; }
        public decimal TotalAmount { get; set; }
        public decimal TotalProfit { get; set; }
    }

    /// <summary>财务报表（通用：资产/负债/损益/现金流）</summary>
    public class FinancialStatement
    {
        public string Title { get; set; } = string.Empty;
        public DateTime PeriodStart { get; set; }
        public DateTime PeriodEnd { get; set; }
        public List<StatementLine> Lines { get; set; } = new();
        public decimal Total { get; set; }
    }

    /// <summary>财务报表行</summary>
    public class StatementLine
    {
        public string Name { get; set; } = string.Empty;
        public decimal Amount { get; set; }
    }

    /// <summary>应收账龄分析项（按销售订单逐笔，仅列出未收完的）</summary>
    public class ArAgingItem
    {
        public string CustomerName { get; set; } = string.Empty;
        public string OrderNo { get; set; } = string.Empty;
        public DateTime OrderDate { get; set; }
        public string Currency { get; set; } = string.Empty;
        public decimal OrderAmount { get; set; }
        public decimal ReceivedAmount { get; set; }
        public decimal Balance { get; set; }
        public int AgingDays { get; set; }
        public int CreditDays { get; set; }
        public string Bucket { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
    }

    /// <summary>柜量与装柜利用率统计项（按柜号聚合装柜清单）</summary>
    public class ContainerStatsItem
    {
        public string ContainerNo { get; set; } = string.Empty;
        public DateTime LoadingDate { get; set; }
        public int CustomerCount { get; set; }
        public decimal TotalCartons { get; set; }
        public decimal TotalWeight { get; set; }
        public decimal TotalVolume { get; set; }
        /// <summary>装载率（按 40HQ 68 m³ 基准估算，%）</summary>
        public decimal Utilization { get; set; }
        /// <summary>柜型判定（拼柜 = 多客户）</summary>
        public string TypeText { get; set; } = string.Empty;
    }

    /// <summary>采购成本分析项（按供应商聚合采购订单）</summary>
    public class PurchaseCostItem
    {
        public string SupplierName { get; set; } = string.Empty;
        public string SupplierType { get; set; } = string.Empty;
        public int OrderCount { get; set; }
        public decimal TotalAmount { get; set; }
        public decimal AvgAmount { get; set; }
        public DateTime? LastOrderDate { get; set; }
    }

    /// <summary>退税汇总项（按退税期间聚合）</summary>
    public class TaxRefundSummaryItem
    {
        public string RefundPeriod { get; set; } = string.Empty;
        public int RecordCount { get; set; }
        public int DeclaredCount { get; set; }
        public int RefundedCount { get; set; }
        public decimal ExportAmount { get; set; }
        public decimal RefundableAmount { get; set; }
        public decimal RefundedAmount { get; set; }
        public decimal UnrefundedAmount { get; set; }
    }

    /// <summary>库存预警项（低于安全库存或高于上限）</summary>
    public class StockAlertItem
    {
        public string ProductName { get; set; } = string.Empty;
        public string Spec { get; set; } = string.Empty;
        public string Unit { get; set; } = string.Empty;
        public string WarehouseName { get; set; } = string.Empty;
        public decimal Quantity { get; set; }
        public decimal MinStock { get; set; }
        public decimal MaxStock { get; set; }
        public decimal Diff { get; set; }
        public string AlertLevel { get; set; } = string.Empty;
    }

    /// <summary>业务员提成项（销售额 / 毛利 / 提成额）</summary>
    public class SalesCommissionItem
    {
        public string SalesmanName { get; set; } = string.Empty;
        public int OrderCount { get; set; }
        public decimal SalesAmount { get; set; }
        public decimal Profit { get; set; }
        public decimal ProfitRate { get; set; }
        /// <summary>提成比例（%，取自系统参数 SalesCommissionRate）</summary>
        public decimal CommissionRate { get; set; }
        /// <summary>提成额（= 毛利 × 提成比例）</summary>
        public decimal CommissionAmount { get; set; }
    }

    /// <summary>
    /// 报价成交率分析项（ERP-018，按业务员 × 原币聚合；计算口径见 <c>docs/报价单与PI设计方案.md</c> §10.3）
    /// </summary>
    public class QuotationConversionItem
    {
        /// <summary>业务员（报价单未填业务员时归入「未指定业务员」）</summary>
        public string SalesmanName { get; set; } = string.Empty;

        /// <summary>原币币种（规范化大写；空值 / 未知取值归入「未知币种」，绝不默认币种或折算汇率）</summary>
        public string Currency { get; set; } = string.Empty;

        /// <summary>有效报价单数（分母：期间内未删除、未作废的报价单数）</summary>
        public int QuotationCount { get; set; }

        /// <summary>已转出报价单数（分子：已转 PI / 已转销售订单 / 状态已完成的报价单数）</summary>
        public int ConvertedCount { get; set; }

        /// <summary>成交率（%）= 已转出数 ÷ 有效报价数 × 100，保留 2 位；分母为 0 时为 0</summary>
        public decimal ConversionRate { get; set; }

        /// <summary>已过期且未转出的报价单数（以报表期间结束日为判定基准日）</summary>
        public int ExpiredCount { get; set; }

        /// <summary>已作废报价单数（不计入分母，单列便于对账）</summary>
        public int CancelledCount { get; set; }

        /// <summary>有效报价金额合计（原币，仅分母口径报价单）</summary>
        public decimal TotalAmount { get; set; }

        /// <summary>已转出报价金额合计（原币，仅分子口径报价单）</summary>
        public decimal ConvertedAmount { get; set; }

        /// <summary>单笔成交平均报价金额（原币）= 已转出金额 ÷ 已转出数，保留 2 位；无成交时为 0</summary>
        public decimal AvgConvertedAmount { get; set; }
    }

    /// <summary>跟进提醒项（按下次跟进日期到期或即将到期）</summary>
    public class FollowUpDueItem
    {
        public string CustomerName { get; set; } = string.Empty;
        public string SalesmanName { get; set; } = string.Empty;
        public DateTime FollowDate { get; set; }
        public string Result { get; set; } = string.Empty;
        public DateTime NextFollowDate { get; set; }
        /// <summary>逾期天数（&gt;0 已逾期、=0 今日到期、&lt;0 还有几天）</summary>
        public int DueDays { get; set; }
        public string DueStatus { get; set; } = string.Empty;
        public string Subject { get; set; } = string.Empty;
    }
}
