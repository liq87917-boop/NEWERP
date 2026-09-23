using ERP.Domain.Enums;

namespace ERP.Application.DTOs;

/// <summary>
/// 报价单有效期提醒项（ERP-018）：报价单页「⏰ 有效期提醒」的数据契约。
/// </summary>
/// <remarks>
/// 全部字段取自现有 <c>Quotations</c> 表与来源关联表，**不新增数据库结构**；
/// 有效期状态与剩余天数由 <see cref="Services.QuotationValidityRules"/> 统一计算。
/// </remarks>
public class QuotationValidityItem
{
    /// <summary>报价单 Id（前端可直接跳转编辑）</summary>
    public long Id { get; set; }

    /// <summary>报价单号</summary>
    public string QuotationNo { get; set; } = string.Empty;

    /// <summary>客户名称</summary>
    public string CustomerName { get; set; } = string.Empty;

    /// <summary>业务员姓名</summary>
    public string SalesmanName { get; set; } = string.Empty;

    /// <summary>报价日期</summary>
    public DateTime QuotationDate { get; set; }

    /// <summary>有效期至（未设置时为 null）</summary>
    public DateTime? ValidUntil { get; set; }

    /// <summary>剩余有效天数（负数 = 已过期、0 = 今日到期；未设置有效期时为 null）</summary>
    public int? ValidDays { get; set; }

    /// <summary>有效期状态文案（未设置有效期 / 已过期 / 今日到期 / 即将到期 / 有效）</summary>
    public string ValidityStatus { get; set; } = string.Empty;

    /// <summary>有效期状态级别（danger / warning / success / neutral）</summary>
    public string ValidityLevel { get; set; } = string.Empty;

    /// <summary>报价总额（原币）</summary>
    public decimal TotalAmount { get; set; }

    /// <summary>报价总额（折人民币）</summary>
    public decimal TotalAmountCny { get; set; }

    /// <summary>币种</summary>
    public Currency Currency { get; set; }

    /// <summary>单据状态（草稿 / 已审核 / 已完成 …）</summary>
    public DocumentStatus Status { get; set; }

    /// <summary>是否已转出（已转形式发票 PI 或已转销售订单）</summary>
    public bool Converted { get; set; }
}
