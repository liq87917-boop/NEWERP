using ERP.Domain.Common;
using ERP.Domain.Enums;
using System.ComponentModel.DataAnnotations;

namespace ERP.Domain.Entities;

/// <summary>
/// 装柜结算单
/// </summary>
public class FinanceContainerSettlement : BaseEntity
{
    /// <summary>结算单号</summary>
    [Required, MaxLength(50)]
    public string SettlementNo { get; set; } = string.Empty;

    /// <summary>结算日期</summary>
    public DateTime SettlementDate { get; set; } = DateTime.Today;

    /// <summary>装柜清单 Id</summary>
    public long? LoadingListId { get; set; }

    /// <summary>客户 Id</summary>
    public long CustomerId { get; set; }

    /// <summary>结算总金额</summary>
    public decimal TotalAmount { get; set; }

    /// <summary>海运费</summary>
    public decimal FreightCost { get; set; }

    /// <summary>其他费用</summary>
    public decimal OtherCost { get; set; }

    /// <summary>单据状态</summary>
    public DocumentStatus Status { get; set; } = DocumentStatus.Pending;

    /// <summary>备注</summary>
    [MaxLength(500)]
    public string Remark { get; set; } = string.Empty;
}

/// <summary>
/// 散货结算单
/// </summary>
public class FinanceBulkSettlement : BaseEntity
{
    /// <summary>结算单号</summary>
    [Required, MaxLength(50)]
    public string SettlementNo { get; set; } = string.Empty;

    /// <summary>结算日期</summary>
    public DateTime SettlementDate { get; set; } = DateTime.Today;

    /// <summary>客户 Id</summary>
    public long CustomerId { get; set; }

    /// <summary>结算总金额</summary>
    public decimal TotalAmount { get; set; }

    /// <summary>海运费</summary>
    public decimal FreightCost { get; set; }

    /// <summary>单据状态</summary>
    public DocumentStatus Status { get; set; } = DocumentStatus.Pending;

    /// <summary>备注</summary>
    [MaxLength(500)]
    public string Remark { get; set; } = string.Empty;
}

/// <summary>
/// 收款单
/// </summary>
public class FinanceReceipt : BaseEntity
{
    /// <summary>收款单号</summary>
    [Required, MaxLength(50)]
    public string ReceiptNo { get; set; } = string.Empty;

    /// <summary>收款日期</summary>
    public DateTime ReceiptDate { get; set; } = DateTime.Today;

    /// <summary>客户 Id</summary>
    public long CustomerId { get; set; }

    /// <summary>收款金额</summary>
    public decimal Amount { get; set; }

    /// <summary>币种</summary>
    public Currency Currency { get; set; } = Currency.USD;

    /// <summary>付款方式</summary>
    public PaymentMethod PaymentMethod { get; set; } = PaymentMethod.BankTransfer;

    /// <summary>收款银行账户</summary>
    [MaxLength(100)]
    public string BankAccount { get; set; } = string.Empty;

    /// <summary>单据状态</summary>
    public DocumentStatus Status { get; set; } = DocumentStatus.Pending;

    /// <summary>备注</summary>
    [MaxLength(500)]
    public string Remark { get; set; } = string.Empty;
}

/// <summary>
/// 客诉单
/// </summary>
public class FinanceComplaint : BaseEntity
{
    /// <summary>客诉单号</summary>
    [Required, MaxLength(50)]
    public string ComplaintNo { get; set; } = string.Empty;

    /// <summary>客诉日期</summary>
    public DateTime ComplaintDate { get; set; } = DateTime.Today;

    /// <summary>客户 Id</summary>
    public long CustomerId { get; set; }

    /// <summary>销售订单 Id</summary>
    public long? SalesOrderId { get; set; }

    /// <summary>客诉类型</summary>
    [MaxLength(100)]
    public string ComplaintType { get; set; } = string.Empty;

    /// <summary>客诉描述</summary>
    [MaxLength(1000)]
    public string Description { get; set; } = string.Empty;

    /// <summary>责任部门</summary>
    [MaxLength(100)]
    public string ResponsibleDept { get; set; } = string.Empty;

    /// <summary>处理结果</summary>
    [MaxLength(1000)]
    public string HandleResult { get; set; } = string.Empty;

    /// <summary>单据状态</summary>
    public DocumentStatus Status { get; set; } = DocumentStatus.Pending;

    /// <summary>备注</summary>
    [MaxLength(500)]
    public string Remark { get; set; } = string.Empty;
}
