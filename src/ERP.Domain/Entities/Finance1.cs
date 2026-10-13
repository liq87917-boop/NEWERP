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
    /// <summary>
    /// 付款单号（服务端权威生成：新增入口一律在实时身份 / 付款单（payment）菜单 / 客户范围、真实可用供应商、
    /// 受支持币种与正金额、来源货款申请单解析等全部校验通过之后，由 <c>IDocumentNumberService</c> 预约覆盖，
    /// 调用方提交值不参与持久化）。
    /// <para>ERP-479：这里刻意<strong>不加</strong> <c>[Required]</c>（与 ERP-477 入库单号 / ERP-478 出库单号同源口径）。
    /// 手工录入表单不提交付款单号，MVC 对非空引用类型属性本就会推断一个 <c>AllowEmptyStrings = true</c> 的隐式必填
    /// （允许空串）；而显式 <c>[Required]</c>（默认 <c>AllowEmptyStrings = false</c>）会把「省略付款单号」在模型绑定 /
    /// 模型校验阶段直接拒绝为 HTTP 400 <c>{"errors":{"PaymentNo":[...]}}</c>，服务端权威单号根本没有机会生成。
    /// 移除显式特性后：省略 / 空串照常绑定并进入已授权的 <c>FinancePaymentController.Create</c>，服务端仍按唯一权威口径
    /// 覆盖付款单号、状态、删除态与时间戳；数据库列仍是 NOT NULL（非空 CLR 引用类型），长度仍受
    /// <see cref="MaxLengthAttribute"/>(50) 约束，非法 / 越权请求的校验顺序不变（授权与来源校验先于单号预约与任何写入）。</para>
    /// </summary>
    [MaxLength(50)]
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
