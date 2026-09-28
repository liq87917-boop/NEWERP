using ERP.Domain.Entities;

namespace ERP.Api.Controllers;

/// <summary>审批决定请求：比价行 Id（必填）+ 可选批次号（跨批次校验）+ 决定类型 / 依据 / 决定人</summary>
public sealed class PurchaseQuoteDecisionRequest
{
    /// <summary>被审批的比价行 Id</summary>
    public long QuoteId { get; set; }

    /// <summary>比价批次号（可选；给定时必须与比价行所属批次一致，否则按跨批次拒绝）</summary>
    public string? QuoteNo { get; set; }

    /// <summary>决定类型：Approved / Rejected</summary>
    public string? Decision { get; set; }

    /// <summary>决定依据 / 理由</summary>
    public string? DecisionBasis { get; set; }

    /// <summary>决定人 Id</summary>
    public long? DecidedBy { get; set; }

    /// <summary>决定人名称</summary>
    public string? DecidedByName { get; set; }
}

/// <summary>批次审批状态：逐行 pending / approved / rejected + 全量决定历史</summary>
public sealed class PurchaseQuoteDecisionBatch
{
    public string QuoteNo { get; set; } = string.Empty;

    /// <summary>批次内比价行数</summary>
    public int LineCount { get; set; }

    /// <summary>待审批行数（无决定记录）</summary>
    public int PendingCount { get; set; }

    /// <summary>已批准行数</summary>
    public int ApprovedCount { get; set; }

    /// <summary>已拒绝行数</summary>
    public int RejectedCount { get; set; }

    public List<PurchaseQuoteDecisionLine> Lines { get; set; } = new();

    /// <summary>该批次的全部决定历史（append-only，按决定顺序）</summary>
    public List<PurchaseQuoteDecision> History { get; set; } = new();
}

/// <summary>批次内一条比价行及其审批状态</summary>
public sealed class PurchaseQuoteDecisionLine
{
    public long QuoteId { get; set; }
    public string QuoteNo { get; set; } = string.Empty;
    public string ProductName { get; set; } = string.Empty;
    public long? SupplierId { get; set; }
    public string SupplierName { get; set; } = string.Empty;
    public decimal QuotePrice { get; set; }

    /// <summary>Pending / Approved / Rejected</summary>
    public string State { get; set; } = string.Empty;

    /// <summary>该行的决定（待审批时为 null）</summary>
    public PurchaseQuoteDecision? Decision { get; set; }
}
