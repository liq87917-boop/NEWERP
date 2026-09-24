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
        public decimal TotalAmount { get; set; }
        public int Rank { get; set; }
    }

    /// <summary>订单利润暂估项</summary>
    public class OrderProfitItem
    {
        public string OrderNo { get; set; } = string.Empty;
        public DateTime OrderDate { get; set; }
        public string CustomerName { get; set; } = string.Empty;
        public decimal SalesAmount { get; set; }
        public decimal CostAmount { get; set; }
        public decimal Profit { get; set; }
        public decimal ProfitRate { get; set; }
    }

    /// <summary>客户出货量统计项</summary>
    public class CustomerShipmentItem
    {
        public long CustomerId { get; set; }
        public string CustomerName { get; set; } = string.Empty;
        public int OrderCount { get; set; }
        public decimal TotalQuantity { get; set; }
        public decimal TotalAmount { get; set; }
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
    /// 报价成交率分析项（ERP-018，按业务员聚合；计算口径见 <c>docs/报价单与PI设计方案.md</c> §10.3）
    /// </summary>
    public class QuotationConversionItem
    {
        /// <summary>业务员（报价单未填业务员时归入「未指定业务员」）</summary>
        public string SalesmanName { get; set; } = string.Empty;

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
