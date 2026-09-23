using ERP.Domain.Common;
using System.ComponentModel.DataAnnotations;

namespace ERP.Domain.Entities;

/// <summary>
/// 出口退税台账
/// 用途：按「报关单/出口发票」记录每笔出口业务的退税申报与到账情况（工贸一体自营出口与代理出口均适用）
/// 说明：非单据流转（不需要存储过程），使用通用 CRUD 维护；金额为人民币，出口金额为原币
/// </summary>
public class BaseTaxRefund : BaseEntity
{
    /// <summary>台账编号（唯一）</summary>
    [Required, MaxLength(50)]
    public string RefundNo { get; set; } = string.Empty;

    /// <summary>退税所属期间（如 2026-08）</summary>
    [MaxLength(20)]
    public string RefundPeriod { get; set; } = string.Empty;

    /// <summary>申报日期</summary>
    public DateTime? DeclareDate { get; set; }

    /// <summary>报关单号</summary>
    [MaxLength(50)]
    public string DeclareNo { get; set; } = string.Empty;

    /// <summary>出口发票号</summary>
    [MaxLength(50)]
    public string InvoiceNo { get; set; } = string.Empty;

    /// <summary>关联销售订单号（按单号记录，避免强关联）</summary>
    [MaxLength(50)]
    public string SalesOrderNo { get; set; } = string.Empty;

    /// <summary>客户 Id</summary>
    public long? CustomerId { get; set; }

    /// <summary>客户名称（冗余，便于查询与导出）</summary>
    [MaxLength(200)]
    public string CustomerName { get; set; } = string.Empty;

    /// <summary>出口金额（原币）</summary>
    public decimal ExportAmount { get; set; }

    /// <summary>币种（USD / EUR / CNY 等）</summary>
    [MaxLength(20)]
    public string Currency { get; set; } = "USD";

    /// <summary>汇率（记账用）</summary>
    public decimal ExchangeRate { get; set; } = 1;

    /// <summary>退税率（%）</summary>
    public decimal RefundRate { get; set; }

    /// <summary>可退税额（人民币）</summary>
    public decimal RefundableAmount { get; set; }

    /// <summary>已退税额（人民币）</summary>
    public decimal RefundedAmount { get; set; }

    /// <summary>退税到账日期</summary>
    public DateTime? RefundDate { get; set; }

    /// <summary>状态（待申报 / 已申报 / 已退税 / 异常）</summary>
    [MaxLength(20)]
    public string Status { get; set; } = "待申报";

    /// <summary>备注</summary>
    [MaxLength(500)]
    public string Remark { get; set; } = string.Empty;
}
