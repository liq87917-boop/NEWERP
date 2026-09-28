using ERP.Domain.Common;
using System.ComponentModel.DataAnnotations;

namespace ERP.Domain.Entities;

/// <summary>
/// 供应商比价价格审批与供应商选择历史（ERP-095）。
/// 用途：记录某比价行「批准选中该供应商 / 拒绝该供应商报价」的决定，含决定人、决定时间、决定依据
///       与选中供应商快照，供比价批次暴露 pending / approved / rejected 决定。
/// 口径：append-only（只追加、不修改、不删除）；与比价行 <see cref="PurchaseQuote"/> 是
///       「一比价行 → 至多一条有效决定」的权威链接（唯一索引 + 服务端重复守卫双重兜底）。
/// </summary>
public class PurchaseQuoteDecision : BaseEntity
{
    /// <summary>被审批的比价行 Id（供应商报价行）</summary>
    public long QuoteId { get; set; }

    /// <summary>比价批次号（冗余快照，便于按批次检索与跨批次校验）</summary>
    [Required, MaxLength(50)]
    public string QuoteNo { get; set; } = string.Empty;

    /// <summary>决定类型：Approved（批准选中）/ Rejected（拒绝）；「待审批」为派生态（无决定记录）</summary>
    [Required, MaxLength(20)]
    public string Decision { get; set; } = string.Empty;

    /// <summary>选中的供应商 Id（批准时写入，拒绝时为空）</summary>
    public long? SelectedSupplierId { get; set; }

    /// <summary>选中的供应商名称快照（批准时写入）</summary>
    [MaxLength(200)]
    public string SelectedSupplierName { get; set; } = string.Empty;

    /// <summary>决定依据 / 理由</summary>
    [MaxLength(500)]
    public string DecisionBasis { get; set; } = string.Empty;

    /// <summary>决定人 Id</summary>
    public long? DecidedBy { get; set; }

    /// <summary>决定人名称快照</summary>
    [MaxLength(100)]
    public string DecidedByName { get; set; } = string.Empty;

    /// <summary>决定时间</summary>
    public DateTime DecidedAt { get; set; } = DateTime.Now;

    /// <summary>审批参考号（写入采购订单备注以保留审批引用；稳定唯一）</summary>
    [MaxLength(50)]
    public string DecisionRef { get; set; } = string.Empty;
}
