using ERP.Domain.Common;
using ERP.Domain.Enums;
using System.ComponentModel.DataAnnotations;

namespace ERP.Domain.Entities;

/// <summary>
/// 定金申请单
/// </summary>
public class FinanceDepositApply : BaseEntity
{
    /// <summary>申请单号</summary>
    [Required, MaxLength(50)]
    public string ApplyNo { get; set; } = string.Empty;

    /// <summary>申请日期</summary>
    public DateTime ApplyDate { get; set; } = DateTime.Today;

    /// <summary>销售订单 Id</summary>
    public long? SalesOrderId { get; set; }

    /// <summary>客户 Id</summary>
    public long CustomerId { get; set; }

    /// <summary>申请金额</summary>
    public decimal Amount { get; set; }

    /// <summary>币种</summary>
    public Currency Currency { get; set; } = Currency.USD;

    /// <summary>汇率</summary>
    public decimal ExchangeRate { get; set; } = 1;

    /// <summary>收款银行账户</summary>
    [MaxLength(100)]
    public string BankAccount { get; set; } = string.Empty;

    /// <summary>收款方</summary>
    [MaxLength(200)]
    public string Payee { get; set; } = string.Empty;

    /// <summary>申请事由</summary>
    [MaxLength(500)]
    public string Reason { get; set; } = string.Empty;

    /// <summary>单据状态</summary>
    public DocumentStatus Status { get; set; } = DocumentStatus.Pending;

    /// <summary>备注</summary>
    [MaxLength(500)]
    public string Remark { get; set; } = string.Empty;
}

/// <summary>
/// 货款申请单
/// </summary>
public class FinancePaymentApply : BaseEntity
{
    /// <summary>申请单号</summary>
    [Required, MaxLength(50)]
    public string ApplyNo { get; set; } = string.Empty;

    /// <summary>申请日期</summary>
    public DateTime ApplyDate { get; set; } = DateTime.Today;

    /// <summary>销售订单 Id</summary>
    public long? SalesOrderId { get; set; }

    /// <summary>客户 Id</summary>
    public long CustomerId { get; set; }

    /// <summary>申请金额</summary>
    public decimal Amount { get; set; }

    /// <summary>币种</summary>
    public Currency Currency { get; set; } = Currency.USD;

    /// <summary>汇率</summary>
    public decimal ExchangeRate { get; set; } = 1;

    /// <summary>收款银行账户</summary>
    [MaxLength(100)]
    public string BankAccount { get; set; } = string.Empty;

    /// <summary>收款方</summary>
    [MaxLength(200)]
    public string Payee { get; set; } = string.Empty;

    /// <summary>申请事由</summary>
    [MaxLength(500)]
    public string Reason { get; set; } = string.Empty;

    /// <summary>单据状态</summary>
    public DocumentStatus Status { get; set; } = DocumentStatus.Pending;

    /// <summary>备注</summary>
    [MaxLength(500)]
    public string Remark { get; set; } = string.Empty;
}

/// <summary>
/// 付款单
/// </summary>
public class FinancePayment : BaseEntity
{
    /// <summary>付款单号</summary>
    [Required, MaxLength(50)]
    public string PaymentNo { get; set; } = string.Empty;

    /// <summary>付款日期</summary>
    public DateTime PaymentDate { get; set; } = DateTime.Today;

    /// <summary>供应商 Id</summary>
    public long SupplierId { get; set; }

    /// <summary>货款申请单 Id</summary>
    public long? PaymentApplyId { get; set; }

    /// <summary>付款金额</summary>
    public decimal Amount { get; set; }

    /// <summary>币种</summary>
    public Currency Currency { get; set; } = Currency.CNY;

    /// <summary>付款方式</summary>
    public PaymentMethod PaymentMethod { get; set; } = PaymentMethod.BankTransfer;

    /// <summary>付款银行账户</summary>
    [MaxLength(100)]
    public string BankAccount { get; set; } = string.Empty;

    /// <summary>单据状态</summary>
    public DocumentStatus Status { get; set; } = DocumentStatus.Pending;

    /// <summary>备注</summary>
    [MaxLength(500)]
    public string Remark { get; set; } = string.Empty;
}
