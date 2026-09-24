using ERP.Domain.Common;
using ERP.Domain.Enums;
using System.ComponentModel.DataAnnotations;

namespace ERP.Domain.Entities;

/// <summary>
/// 报价单主表（外贸业务链：询价单 → 报价单 → 形式发票 PI → 销售订单）
/// </summary>
public class Quotation : BaseEntity
{
    /// <summary>报价单号（单据字轨生成，如 QT2609180001）</summary>
    /// <remarks>
    /// 允许空串：报价单号由服务端字轨生成（表单提示「留空自动生成」），若此处禁止空串，
    /// 新增报价单会在到达控制器之前被 [ApiController] 的模型校验拦成 400，导致服务端自动编号永远无法生效。
    /// </remarks>
    [Required(AllowEmptyStrings = true), MaxLength(50)]
    public string QuotationNo { get; set; } = string.Empty;

    /// <summary>报价日期</summary>
    public DateTime QuotationDate { get; set; } = DateTime.Today;

    /// <summary>报价有效期至（客户砍价 / 催价时核对价格是否仍有效）</summary>
    public DateTime? ValidUntil { get; set; }

    /// <summary>客户 Id</summary>
    public long? CustomerId { get; set; }

    /// <summary>客户名称（冗余，便于列表与打印）</summary>
    [MaxLength(200)]
    public string CustomerName { get; set; } = string.Empty;

    /// <summary>客户方对接人</summary>
    [MaxLength(50)]
    public string ContactPerson { get; set; } = string.Empty;

    /// <summary>联系电话</summary>
    [MaxLength(50)]
    public string ContactPhone { get; set; } = string.Empty;

    /// <summary>联系邮箱（发送报价 / 转 PI 用）</summary>
    [MaxLength(100)]
    public string ContactEmail { get; set; } = string.Empty;

    /// <summary>来源询价单 Id（可空，支持无询价单直接报价）</summary>
    public long? InquiryId { get; set; }

    /// <summary>来源询价单号</summary>
    [MaxLength(50)]
    public string InquiryNo { get; set; } = string.Empty;

    /// <summary>贸易术语（FOB / CIF / EXW / DDP…）</summary>
    [MaxLength(50)]
    public string TradeTerms { get; set; } = string.Empty;

    /// <summary>起运港（如 NINGBO / SHANGHAI）</summary>
    [MaxLength(100)]
    public string PortOfLoading { get; set; } = string.Empty;

    /// <summary>目的港</summary>
    [MaxLength(100)]
    public string PortOfDestination { get; set; } = string.Empty;

    /// <summary>付款方式（如 T/T 30% deposit, balance against B/L copy）</summary>
    [MaxLength(200)]
    public string PaymentTerms { get; set; } = string.Empty;

    /// <summary>交货期（如 35 days after receipt of deposit）</summary>
    [MaxLength(100)]
    public string LeadTime { get; set; } = string.Empty;

    /// <summary>币种</summary>
    public Currency Currency { get; set; } = Currency.USD;

    /// <summary>汇率（折人民币用）</summary>
    public decimal ExchangeRate { get; set; } = 1;

    /// <summary>报价总额（原币）</summary>
    public decimal TotalAmount { get; set; }

    /// <summary>报价总额（折人民币，报表用）</summary>
    public decimal TotalAmountCny { get; set; }

    /// <summary>业务员 Id（员工）</summary>
    public long? SalesmanId { get; set; }

    /// <summary>业务员姓名（冗余）</summary>
    [MaxLength(50)]
    public string SalesmanName { get; set; } = string.Empty;

    /// <summary>单据状态</summary>
    public DocumentStatus Status { get; set; } = DocumentStatus.Pending;

    /// <summary>备注</summary>
    [MaxLength(500)]
    public string Remark { get; set; } = string.Empty;

    /* ============ 版本链（ERP-035：多轮议价版本留痕，源版本作为不可变历史） ============ */

    /// <summary>初始版本号：首个版本与「无版本元数据」的历史报价单一律按 V1 处理</summary>
    public const int InitialRevisionNumber = 1;

    /// <summary>
    /// 版本链根单 Id：初始版本为 <c>null</c>（自身即根），后续版本指向根单主键；
    /// 链内查询口径 = <c>Id == 根单Id || RootQuotationId == 根单Id</c>。
    /// </summary>
    public long? RootQuotationId { get; set; }

    /// <summary>版本链根单号（冗余：列表 / 详情 / 打印免关联；初始版本为空串，按自身单号显示）</summary>
    [MaxLength(50)]
    public string RootQuotationNo { get; set; } = string.Empty;

    /// <summary>
    /// 版本号（服务端分配，链内单调递增）：初始版本为 1；
    /// 数据库默认值为 1，因此新增列之前创建的历史报价单读取时即为初始版本，
    /// 既不回填也不被改写（见 <see cref="InitialRevisionNumber"/>）。
    /// </summary>
    public int RevisionNumber { get; set; } = InitialRevisionNumber;

    /// <summary>上一版本 Id（创建版本时指向被复制的源版本；初始版本为 null）</summary>
    public long? PreviousRevisionId { get; set; }

    /// <summary>上一版本号（冗余，便于列表 / 详情直接展示版本来源；初始版本为空串）</summary>
    [MaxLength(50)]
    public string PreviousRevisionNo { get; set; } = string.Empty;

    /// <summary>报价明细</summary>
    public List<QuotationDetail> Details { get; set; } = new();
}

/// <summary>
/// 报价单明细（一行一个商品：数量 × 单价 = 金额）
/// </summary>
public class QuotationDetail : BaseEntity
{
    /// <summary>报价单 Id</summary>
    public long QuotationId { get; set; }

    /// <summary>报价单号（冗余，报表免关联）</summary>
    [MaxLength(50)]
    public string QuotationNo { get; set; } = string.Empty;

    /// <summary>行号（LineNo 在部分 SQL Server 实例上为保留字，改用 SortNo）</summary>
    public int SortNo { get; set; }

    /// <summary>商品 Id</summary>
    public long? ProductId { get; set; }

    /// <summary>商品编码（冗余）</summary>
    [MaxLength(50)]
    public string ProductCode { get; set; } = string.Empty;

    /// <summary>商品名称（冗余）</summary>
    [MaxLength(200)]
    public string ProductName { get; set; } = string.Empty;

    /// <summary>规格</summary>
    [MaxLength(200)]
    public string Spec { get; set; } = string.Empty;

    /// <summary>单位</summary>
    [MaxLength(20)]
    public string Unit { get; set; } = string.Empty;

    /// <summary>数量</summary>
    public decimal Quantity { get; set; }

    /// <summary>单价（原币）</summary>
    public decimal UnitPrice { get; set; }

    /// <summary>金额 = 数量 × 单价</summary>
    public decimal Amount { get; set; }

    /// <summary>起订量说明（如 500 pcs/款）</summary>
    [MaxLength(100)]
    public string Moq { get; set; } = string.Empty;

    /// <summary>备注（色号、包装、外箱尺寸等）</summary>
    [MaxLength(500)]
    public string Remark { get; set; } = string.Empty;
}
