using ERP.Domain.Common;
using ERP.Domain.Enums;
using System.ComponentModel.DataAnnotations;

namespace ERP.Domain.Entities;

/// <summary>
/// 装柜结算单
/// </summary>
public class FinanceContainerSettlement : BaseEntity
{
    /// <summary>
    /// 结算单号（服务端权威生成：新增入口一律在实时身份 / 装柜结算单（<c>container-settlement</c>）菜单 / 客户范围、
    /// 真实可用客户、正总金额与非负费用、来源装柜清单精确资格等全部校验通过之后，由 <c>IDocumentNumberService</c>
    /// 预约覆盖，调用方提交值不参与持久化）。
    /// <para>ERP-481：这里刻意<strong>不加</strong> <c>[Required]</c>（与 ERP-479 收款单号 · 付款单号 /
    /// ERP-480 定金申请单号 · 货款申请单号同源口径）。手工录入表单不提交结算单号，MVC 对非空引用类型属性本就会
    /// 推断一个 <c>AllowEmptyStrings = true</c> 的隐式必填（允许空串）；而显式 <c>[Required]</c>（默认
    /// <c>AllowEmptyStrings = false</c>）会把「省略结算单号」在模型绑定 / 模型校验阶段直接拒绝为 HTTP 400
    /// <c>{"errors":{"SettlementNo":[...]}}</c>，服务端权威单号根本没有机会生成。移除显式特性后：省略 / 空串
    /// 照常绑定并进入已授权的 <c>FinanceContainerSettlementController.Create</c>，服务端仍按唯一权威口径覆盖结算单号、
    /// 主键、状态、删除态与时间戳；数据库列仍是 NOT NULL（非空 CLR 引用类型），长度仍受
    /// <see cref="MaxLengthAttribute"/>(50) 约束，非法 / 越权请求的校验顺序不变
    /// （授权与来源校验先于单号预约与任何写入）。</para>
    /// </summary>
    [MaxLength(50)]
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
    /// <summary>
    /// 结算单号（服务端权威生成：新增入口一律在实时身份 / 散货结算单（<c>bulk-settlement</c>）菜单 / 客户范围、
    /// 真实可用客户、正总金额与非负海运费等全部校验通过之后，由 <c>IDocumentNumberService</c> 预约覆盖，
    /// 调用方提交值不参与持久化）。
    /// <para>ERP-481：这里刻意<strong>不加</strong> <c>[Required]</c>（与 ERP-479 / ERP-480 同源口径）。手工录入表单
    /// 不提交结算单号，MVC 对非空引用类型属性本就会推断一个 <c>AllowEmptyStrings = true</c> 的隐式必填（允许空串）；
    /// 而显式 <c>[Required]</c>（默认 <c>AllowEmptyStrings = false</c>）会把「省略结算单号」在模型绑定 / 模型校验
    /// 阶段直接拒绝为 HTTP 400 <c>{"errors":{"SettlementNo":[...]}}</c>，服务端权威单号根本没有机会生成。移除显式特性后：
    /// 省略 / 空串照常绑定并进入已授权的 <c>FinanceBulkSettlementController.Create</c>，服务端仍按唯一权威口径覆盖
    /// 结算单号、主键、状态、删除态与时间戳；数据库列仍是 NOT NULL（非空 CLR 引用类型），长度仍受
    /// <see cref="MaxLengthAttribute"/>(50) 约束，非法 / 越权请求的校验顺序不变
    /// （授权与客户 / 金额校验先于单号预约与任何写入）。</para>
    /// </summary>
    [MaxLength(50)]
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
    /// <summary>
    /// 收款单号（服务端权威生成：新增入口一律在实时身份 / 收款单（receipt）菜单 / 客户范围、真实可用客户、
    /// 受支持币种与正金额等全部校验通过之后，由 <c>IDocumentNumberService</c> 预约覆盖，调用方提交值不参与持久化）。
    /// <para>ERP-479：这里刻意<strong>不加</strong> <c>[Required]</c>（与 ERP-477 入库单号 / ERP-478 出库单号同源口径）。
    /// 手工录入表单不提交收款单号，MVC 对非空引用类型属性本就会推断一个 <c>AllowEmptyStrings = true</c> 的隐式必填
    /// （允许空串）；而显式 <c>[Required]</c>（默认 <c>AllowEmptyStrings = false</c>）会把「省略收款单号」在模型绑定 /
    /// 模型校验阶段直接拒绝为 HTTP 400 <c>{"errors":{"ReceiptNo":[...]}}</c>，服务端权威单号根本没有机会生成。
    /// 移除显式特性后：省略 / 空串照常绑定并进入已授权的 <c>FinanceReceiptController.Create</c>，服务端仍按唯一权威口径
    /// 覆盖收款单号、状态、删除态与时间戳；数据库列仍是 NOT NULL（非空 CLR 引用类型），长度仍受
    /// <see cref="MaxLengthAttribute"/>(50) 约束，非法 / 越权请求的校验顺序不变（授权与来源校验先于单号预约与任何写入）。</para>
    /// </summary>
    [MaxLength(50)]
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
    /// <summary>
    /// 客诉单号（服务端权威生成：新增入口一律在实时身份 / 客诉单（<c>complaint</c>）菜单 / 客户范围、
    /// 真实可用客户、可选来源销售订单精确资格等全部校验通过之后，由 <c>IDocumentNumberService</c> 预约覆盖，
    /// 调用方提交值不参与持久化）。
    /// <para>ERP-483：这里刻意<strong>不加</strong> <c>[Required]</c>（与 ERP-477 入库单号 / ERP-478 出库单号 /
    /// ERP-479 收款单号 · 付款单号 / ERP-480 申请单号 / ERP-481 结算单号 / ERP-482 计划单号同源口径）。
    /// 手工录入表单不提交客诉单号，MVC 对非空引用类型属性本就会推断一个 <c>AllowEmptyStrings = true</c> 的隐式必填
    /// （允许空串）；而显式 <c>[Required]</c>（默认 <c>AllowEmptyStrings = false</c>）会把「省略客诉单号」在模型绑定 /
    /// 模型校验阶段直接拒绝为 HTTP 400 <c>{"errors":{"ComplaintNo":[...]}}</c>，服务端权威单号根本没有机会生成。
    /// 移除显式特性后：省略 / 空串照常绑定并进入已授权的 <c>FinanceComplaintController.Create</c>，服务端仍按唯一
    /// 权威口径覆盖客诉单号、主键、状态、删除态与时间戳；数据库列仍是 NOT NULL（非空 CLR 引用类型），长度仍受
    /// <see cref="MaxLengthAttribute"/>(50) 约束，非法 / 越权请求的校验顺序不变
    /// （授权、客户与来源校验先于单号预约与任何写入）。</para>
    /// </summary>
    [MaxLength(50)]
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
