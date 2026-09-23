using ERP.Domain.Common;
using ERP.Domain.Enums;
using System.ComponentModel.DataAnnotations;

namespace ERP.Domain.Entities;

/// <summary>
/// 形式发票 PI 主表（外贸业务链：询价单 → 报价单 → **形式发票 PI** → 销售订单）
/// 说明：与报价单同构，同样使用 EF 主子表实现（不走存储过程），额外承载银行信息 /
///       收货人 / 通知人 / 唛头 / 定金比例与定金金额（PI 是收定金与开信用证的凭据）。
/// </summary>
public class ProformaInvoice : BaseEntity
{
    /// <summary>PI 号（单据字轨生成，如 PI2609180001）</summary>
    /// <remarks>
    /// 允许空串：PI 号由服务端字轨生成（表单提示「留空自动生成」），若此处禁止空串，
    /// 新增 PI 会在到达控制器之前被 [ApiController] 的模型校验拦成 400，导致服务端自动编号永远无法生效。
    /// </remarks>
    [Required(AllowEmptyStrings = true), MaxLength(50)]
    public string PiNo { get; set; } = string.Empty;

    /// <summary>PI 日期</summary>
    public DateTime PiDate { get; set; } = DateTime.Today;

    /// <summary>来源报价单 Id（由报价单转 PI 时回填）</summary>
    public long? QuotationId { get; set; }

    /// <summary>来源报价单号（冗余，报表与追溯免关联）</summary>
    [MaxLength(50)]
    public string QuotationNo { get; set; } = string.Empty;

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

    /// <summary>联系邮箱</summary>
    [MaxLength(100)]
    public string ContactEmail { get; set; } = string.Empty;

    /// <summary>收货人（PI 必备，随提单显示）</summary>
    [MaxLength(300)]
    public string Consignee { get; set; } = string.Empty;

    /// <summary>通知人（PI 必备，随提单显示）</summary>
    [MaxLength(300)]
    public string NotifyParty { get; set; } = string.Empty;

    /// <summary>唛头（Shipping Marks，多行文本）</summary>
    [MaxLength(500)]
    public string ShippingMarks { get; set; } = string.Empty;

    /// <summary>银行信息（多行文本：Beneficiary / Bank / Account / SWIFT；系统参数存默认，PI 可覆盖）</summary>
    [MaxLength(1000)]
    public string BankInfo { get; set; } = string.Empty;

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

    /// <summary>运输方式与条款（如 By sea / FCL / LCL；客户的 shipping terms）</summary>
    [MaxLength(200)]
    public string ShippingTerms { get; set; } = string.Empty;

    /// <summary>交货期（如 35 days after receipt of deposit）</summary>
    [MaxLength(100)]
    public string LeadTime { get; set; } = string.Empty;

    /// <summary>币种</summary>
    public Currency Currency { get; set; } = Currency.USD;

    /// <summary>汇率（折人民币用）</summary>
    public decimal ExchangeRate { get; set; } = 1;

    /// <summary>PI 总额（原币）</summary>
    public decimal TotalAmount { get; set; }

    /// <summary>PI 总额（折人民币，报表用）</summary>
    public decimal TotalAmountCny { get; set; }

    /// <summary>定金比例（%，0~100；由报价/系统默认带出，允许手改）</summary>
    public decimal DepositRatio { get; set; }

    /// <summary>定金金额（留 0 时由前端/后端按比例自动计算，可手工覆盖）</summary>
    public decimal DepositAmount { get; set; }

    /// <summary>业务员 Id（员工）</summary>
    public long? SalesmanId { get; set; }

    /// <summary>业务员姓名（冗余）</summary>
    [MaxLength(50)]
    public string SalesmanName { get; set; } = string.Empty;

    /// <summary>单据状态（草稿 → 已审核 → 已转销售订单 / 已作废）</summary>
    public DocumentStatus Status { get; set; } = DocumentStatus.Pending;

    /// <summary>备注</summary>
    [MaxLength(500)]
    public string Remark { get; set; } = string.Empty;

    /// <summary>PI 明细</summary>
    public List<ProformaInvoiceDetail> Details { get; set; } = new();
}

/// <summary>
/// 形式发票 PI 明细（一行一个商品：数量 × 单价 = 金额）
/// </summary>
public class ProformaInvoiceDetail : BaseEntity
{
    /// <summary>PI 主表 Id</summary>
    public long PiId { get; set; }

    /// <summary>PI 号（冗余，报表免关联）</summary>
    [MaxLength(50)]
    public string PiNo { get; set; } = string.Empty;

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
