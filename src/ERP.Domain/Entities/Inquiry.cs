using ERP.Domain.Common;
using ERP.Domain.Enums;
using System.ComponentModel.DataAnnotations;

namespace ERP.Domain.Entities;

/// <summary>
/// 询价单主表
/// </summary>
public class Inquiry : BaseEntity
{
    /// <summary>
    /// 询价单号（服务端权威生成：新增入口一律在实时授权、客户范围与全部商业条款校验通过之后由
    /// <c>IDocumentNumberService</c> 预约覆盖，调用方提交值不参与持久化）。
    /// <para>ERP-474：这里刻意<strong>不加</strong> <c>[Required]</c>（与 ERP-428 的订单号同源口径）。手工录入表单不提交询价单号，
    /// MVC 对非空引用类型属性本就会推断一个 <c>AllowEmptyStrings = true</c> 的隐式必填（允许空串），
    /// 而显式 <c>[Required]</c>（默认 <c>AllowEmptyStrings = false</c>）会把「省略询价单号」在模型绑定 /
    /// 模型校验阶段直接拒绝为 HTTP 400，服务端权威单号根本没有机会生成。移除显式特性后：
    /// 省略 / 空串照常绑定并进入已授权的 <c>InquiryController.Create</c>，服务端仍按唯一权威口径覆盖询价单号；
    /// 数据库列仍是 NOT NULL（非空 CLR 引用类型 + 唯一索引），长度仍受 <see cref="MaxLengthAttribute"/>(50) 约束，
    /// 非法请求 / 越权请求的校验顺序不变（授权与校验先于单号预约与任何写入）。</para>
    /// </summary>
    [MaxLength(50)]
    public string InquiryNo { get; set; } = string.Empty;

    /// <summary>询价日期</summary>
    public DateTime InquiryDate { get; set; } = DateTime.Today;

    /// <summary>客户 Id</summary>
    public long CustomerId { get; set; }

    /// <summary>联系人</summary>
    [MaxLength(50)]
    public string ContactPerson { get; set; } = string.Empty;

    /// <summary>联系电话</summary>
    [MaxLength(50)]
    public string ContactPhone { get; set; } = string.Empty;

    /// <summary>业务员 Id（员工）</summary>
    public long? SalesmanId { get; set; }

    /// <summary>币种</summary>
    public Currency Currency { get; set; } = Currency.USD;

    /// <summary>汇率</summary>
    public decimal ExchangeRate { get; set; } = 1;

    /// <summary>报价有效期（天）</summary>
    public int ValidDays { get; set; } = 30;

    /// <summary>单据状态</summary>
    public DocumentStatus Status { get; set; } = DocumentStatus.Pending;

    /// <summary>备注</summary>
    [MaxLength(500)]
    public string Remark { get; set; } = string.Empty;

    /// <summary>明细集合</summary>
    public List<InquiryDetail> Details { get; set; } = new();
}

/// <summary>
/// 询价单明细
/// </summary>
public class InquiryDetail : BaseEntity
{
    /// <summary>询价单 Id</summary>
    public long InquiryId { get; set; }

    /// <summary>商品 Id</summary>
    public long ProductId { get; set; }

    /// <summary>商品名称（冗余）</summary>
    [MaxLength(200)]
    public string ProductName { get; set; } = string.Empty;

    /// <summary>规格</summary>
    [MaxLength(200)]
    public string Spec { get; set; } = string.Empty;

    /// <summary>数量</summary>
    public decimal Quantity { get; set; }

    /// <summary>单位</summary>
    [MaxLength(20)]
    public string Unit { get; set; } = string.Empty;

    /// <summary>单价</summary>
    public decimal UnitPrice { get; set; }

    /// <summary>金额</summary>
    public decimal Amount { get; set; }

    /// <summary>目标价</summary>
    public decimal TargetPrice { get; set; }

    /// <summary>期望交期</summary>
    public DateTime? ExpectedDeliveryDate { get; set; }

    /// <summary>备注</summary>
    [MaxLength(500)]
    public string Remark { get; set; } = string.Empty;
}
