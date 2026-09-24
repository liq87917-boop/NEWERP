using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ERP.Api.Controllers;

/// <summary>采购订单明细行收货进度（只读派生，不落库）</summary>
public sealed class PurchaseOrderProgressLine
{
    public long DetailId { get; init; }
    public long ProductId { get; init; }
    public string ProductName { get; init; } = string.Empty;
    public string Spec { get; init; } = string.Empty;
    public string Unit { get; init; } = string.Empty;

    /// <summary>订单数量（采购订单明细数量）</summary>
    public decimal OrderedQuantity { get; init; }

    /// <summary>已审核入库数量（同商品多行按明细顺序依次冲抵）</summary>
    public decimal ReceivedQuantity { get; init; }

    /// <summary>已提交 / 待提交入库数量（未审核，不计入已收）</summary>
    public decimal PendingQuantity { get; init; }

    /// <summary>未收数量 = max(0, 订单数量 − 已收数量)</summary>
    public decimal OutstandingQuantity { get; init; }

    /// <summary>超收数量 = max(0, 已收数量 − 订单数量)</summary>
    public decimal OverReceivedQuantity { get; init; }

    /// <summary>行收货状态：none / partial / complete / over_received</summary>
    public string ReceiptStatus { get; init; } = PurchaseOrderProgress.ReceiptNone;
}

/// <summary>
/// 采购订单收货进度汇总（只读派生；「执行进度」与「供应商采购敞口报表」共用同一收货口径）。
/// </summary>
public sealed class PurchaseOrderReceiptSummary
{
    /// <summary>订单数量合计（订单明细口径）</summary>
    public decimal OrderedQuantity { get; init; }

    /// <summary>已收数量合计（已审核入库单据口径，含订单外商品）</summary>
    public decimal ReceivedQuantity { get; init; }

    /// <summary>已冲抵订单明细的已收数量（含超收部分）</summary>
    public decimal MatchedReceivedQuantity { get; init; }

    /// <summary>订单外商品的已收数量（显式单列）</summary>
    public decimal UnmatchedReceivedQuantity { get; init; }

    /// <summary>待审核入库数量合计（不计入已收）</summary>
    public decimal PendingQuantity { get; init; }

    /// <summary>未收数量合计 = Σ 订单行未收数量</summary>
    public decimal OutstandingQuantity { get; init; }

    /// <summary>整体收货状态：none / partial / complete / over_received</summary>
    public string ReceiptStatus { get; init; } = PurchaseOrderProgress.ReceiptNone;

    /// <summary>是否命中批量派生上限（true = 以上数量不完整，调用方必须按「未知」呈现，不得当作 0 或全量）</summary>
    public bool Truncated { get; init; }
}

/// <summary>订单外商品的入库数量（入库明细商品不在本采购订单明细中，显式单列而不并入订单行）</summary>
public sealed class PurchaseOrderUnmatchedReceipt
{
    public long ProductId { get; init; }
    public string ProductName { get; init; } = string.Empty;
    public decimal ReceivedQuantity { get; init; }
}

/// <summary>收货来源单据引用（以本单为来源的采购入库单）</summary>
public sealed class PurchaseOrderReceiptReference
{
    public string StockInNo { get; init; } = string.Empty;
    public DateTime StockInDate { get; init; }
    public string Status { get; init; } = string.Empty;
    public decimal TotalQuantity { get; init; }

    /// <summary>是否计入「已收数量」（= 已审核；待提交 / 已提交 / 已驳回 / 已取消均不计入）</summary>
    public bool Counted { get; init; }
}

/// <summary>结算来源单据引用（付款单，经货款申请单引用本单归属销售订单）</summary>
public sealed class PurchaseOrderSettlementReference
{
    public string PaymentNo { get; init; } = string.Empty;
    public DateTime PaymentDate { get; init; }
    public decimal Amount { get; init; }
    public string Currency { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public string PaymentApplyNo { get; init; } = string.Empty;

    /// <summary>是否计入结算合计（= 已审核且币种与本单一致；其他情况仅列出不合计）</summary>
    public bool Counted { get; init; }
}

/// <summary>采购订单结算进度（只读派生；无可用既有引用时金额为 null，不做任何推断）</summary>
public sealed class PurchaseOrderSettlementProgress
{
    /// <summary>订单已落库的结算进度文本（人工维护，非派生值）</summary>
    public string RecordedProgress { get; init; } = string.Empty;

    /// <summary>引用状态：linked / ambiguous / unavailable</summary>
    public string LinkStatus { get; init; } = PurchaseOrderProgress.LinkUnavailable;

    /// <summary>引用状态说明（无可用引用时说明缺什么，不按文本或供应商口径猜测）</summary>
    public string LinkReason { get; init; } = string.Empty;

    public long? OwningSalesOrderId { get; init; }
    public string OwningSalesOrderNo { get; init; } = string.Empty;

    /// <summary>已结算金额（仅已审核且币种一致）；无可用引用时为 null（未知，不等于 0）</summary>
    public decimal? SettledAmount { get; init; }

    /// <summary>未结算金额 = 订单总额 − 已结算金额；无可用引用时为 null（未知）</summary>
    public decimal? OutstandingAmount { get; init; }

    /// <summary>已提交 / 待提交付款单金额（同口径）；无可用引用时为 null</summary>
    public decimal? SubmittedAmount { get; init; }

    /// <summary>是否超付（已结算 &gt; 订单总额）</summary>
    public bool OverSettled { get; init; }

    public List<PurchaseOrderSettlementReference> Documents { get; init; } = new();
}

/// <summary>采购订单执行进度视图：收货数量按既有入库单派生，结算金额仅在既有引用可用时暴露。</summary>
public sealed class PurchaseOrderProgressView
{
    public long OrderId { get; init; }
    public string OrderNo { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public string Currency { get; init; } = string.Empty;
    public decimal OrderAmount { get; init; }

    /// <summary>人工登记的到货进度文本（原样回显，不与派生值混用）</summary>
    public string ArrivalProgressRecorded { get; init; } = string.Empty;

    /// <summary>人工登记的结算进度文本（原样回显，不与派生值混用）</summary>
    public string SettlementProgressRecorded { get; init; } = string.Empty;

    /// <summary>订单数量合计（订单明细口径）</summary>
    public decimal OrderedQuantity { get; init; }

    /// <summary>已收数量合计（已审核入库单据口径，含订单外商品）</summary>
    public decimal ReceivedQuantity { get; init; }

    /// <summary>已冲抵订单明细的已收数量（含超收部分）</summary>
    public decimal MatchedReceivedQuantity { get; init; }

    /// <summary>订单外商品的已收数量（显式单列）</summary>
    public decimal UnmatchedReceivedQuantity { get; init; }

    /// <summary>待审核入库数量合计（不计入已收）</summary>
    public decimal PendingQuantity { get; init; }

    /// <summary>未收数量合计 = Σ 订单行未收数量</summary>
    public decimal OutstandingQuantity { get; init; }

    /// <summary>整体收货状态：none / partial / complete / over_received</summary>
    public string ReceiptStatus { get; init; } = PurchaseOrderProgress.ReceiptNone;

    /// <summary>人工到货进度与派生收货状态是否一致；未登记或文本不在既定词表内时为 null（无法判定）</summary>
    public bool? ArrivalProgressConsistent { get; init; }

    public List<PurchaseOrderProgressLine> Lines { get; init; } = new();
    public List<PurchaseOrderUnmatchedReceipt> UnmatchedReceipts { get; init; } = new();
    public List<PurchaseOrderReceiptReference> Receipts { get; init; } = new();
    public PurchaseOrderSettlementProgress Settlement { get; init; } = new();

    /// <summary>收货口径说明（与派生逻辑同源，供界面原样展示）</summary>
    public string ReceiptRule { get; init; } = PurchaseOrderProgress.ReceiptRuleText;

    /// <summary>结算口径说明（与派生逻辑同源，供界面原样展示）</summary>
    public string SettlementRule { get; init; } = PurchaseOrderProgress.SettlementRuleText;
}

/// <summary>
/// 采购订单执行进度派生（ERP-026，只读）。
/// <para>收货：只按既有采购入库单派生——以 <see cref="StockIn.PurchaseOrderId"/> 指向本单、未删除、明细未删除的
/// 入库明细数量按商品汇总；仅「已审核」计入已收，「待提交 / 已提交」单列为待审数量，「已驳回 / 已取消」不计。
/// 入库明细数量在 ERP-023 起已折算为基础单位，故与采购订单明细数量同口径直接比较。
/// 采购退货、库存调整等其它库存动作不冲减已收数量（另行核算）。</para>
/// <para>结算：只有当本单归属销售订单存在且唯一、且付款单经 <see cref="FinancePayment.PaymentApplyId"/> →
/// <see cref="FinancePaymentApply.SalesOrderId"/> 指向该销售订单并属于同一供应商时，才暴露已结算 / 未结算金额；
/// 无可用引用、引用不唯一或币种不一致时一律不换算、不按文本或供应商口径推断（金额返回 null 表示未知）。</para>
/// <para>查询有界：无论单据多少，固定为「1 次订单 + 1 次入库主表 + 1 次入库明细 + 1 次归属销售订单计数 +
/// 1 次货款申请单 + 1 次付款单」，各集合按固定上限截断。</para>
/// </summary>
public static class PurchaseOrderProgress
{
    /// <summary>收货状态：无已收（含订单数量为 0）</summary>
    public const string ReceiptNone = "none";

    /// <summary>收货状态：部分收货（仍有未收数量）</summary>
    public const string ReceiptPartial = "partial";

    /// <summary>收货状态：已收齐</summary>
    public const string ReceiptComplete = "complete";

    /// <summary>收货状态：超收</summary>
    public const string ReceiptOverReceived = "over_received";

    /// <summary>结算引用状态：既有引用可用（唯一归属，金额可信）</summary>
    public const string LinkLinked = "linked";

    /// <summary>结算引用状态：既有引用无法唯一归属（不推断金额）</summary>
    public const string LinkAmbiguous = "ambiguous";

    /// <summary>结算引用状态：不存在可用的既有引用（不推断金额）</summary>
    public const string LinkUnavailable = "unavailable";

    /// <summary>收货口径说明（界面与文档同源）</summary>
    public const string ReceiptRuleText =
        "已收数量仅统计「以本采购订单为来源、未删除、已审核」的采购入库明细数量（入库明细已折算基础单位）；" +
        "待提交 / 已提交入库单列为待审数量，不计入已收；已驳回 / 已取消不计；同商品多行按明细顺序依次冲抵，" +
        "该商品最后一行的剩余入库数量体现为超收；只有订单外商品（订单明细中不存在该商品）的入库数量才单列；" +
        "采购退货与库存调整不冲减已收数量。";

    /// <summary>结算口径说明（界面与文档同源）</summary>
    public const string SettlementRuleText =
        "结算金额只在既有引用可用时暴露：付款单经「货款申请单」引用本单归属销售订单，且该销售订单下只有本单一张采购订单、付款单供应商与本单一致；" +
        "仅「已审核且币种与本单一致」的付款单计入已结算，已提交 / 待提交单列，其他币种仅列出不换算；" +
        "无归属销售订单、归属销售订单下存在多张采购订单或找不到引用时，金额为未知（null），按供应商汇总或备注文本匹配一律不作为本单结算依据。";

    /// <summary>单个订单参与派生的集合上限（避免大单据无界加载）</summary>
    private const int DocumentLimit = 200;

    /// <summary>
    /// 批量派生的单次查询上限（只对一页多张订单的批量路径生效）：命中上限时无法把「被截断的行」归因到具体订单，
    /// 因此不做静默截断，而是把该页相关订单记为未知（金额 null / 收货数量未知）并说明原因。
    /// </summary>
    private const int BatchDocumentCeiling = 2000;

    /// <summary>本次派生的单次查询上限：单张订单沿用既有执行进度的 200 行上限，一页多张订单用批量上限</summary>
    private static int QueryLimitFor(int orderCount) => orderCount <= 1 ? DocumentLimit : BatchDocumentCeiling;

    /// <summary>金额比较容差（与应收账龄同一口径，吸收两位小数舍入）</summary>
    private const decimal AmountTolerance = 0.005m;

    /// <summary>派生指定采购订单的只读执行进度；订单不存在时抛业务异常。</summary>
    public static async Task<PurchaseOrderProgressView> ForPurchaseOrderAsync(IErpDbContext db, long id)
    {
        var order = await db.PurchaseOrders.AsNoTracking().Include(o => o.Details)
            .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
            ?? throw BusinessException.NotFound("采购订单不存在");

        var receipts = await BuildReceiptsAsync(db, order);
        var settlement = await BuildSettlementAsync(db, order);

        return new PurchaseOrderProgressView
        {
            OrderId = order.Id,
            OrderNo = order.OrderNo,
            Status = order.Status.ToString(),
            Currency = order.Currency.ToString(),
            OrderAmount = order.TotalAmount,
            ArrivalProgressRecorded = order.ArrivalProgress,
            SettlementProgressRecorded = order.SettlementProgress,
            OrderedQuantity = receipts.OrderedQuantity,
            ReceivedQuantity = receipts.ReceivedQuantity,
            MatchedReceivedQuantity = receipts.MatchedReceivedQuantity,
            UnmatchedReceivedQuantity = receipts.UnmatchedReceivedQuantity,
            PendingQuantity = receipts.PendingQuantity,
            OutstandingQuantity = receipts.OutstandingQuantity,
            ReceiptStatus = receipts.ReceiptStatus,
            ArrivalProgressConsistent = ArrivalProgressConsistent(order.ArrivalProgress, receipts.ReceiptStatus),
            Lines = receipts.Lines,
            UnmatchedReceipts = receipts.UnmatchedReceipts,
            Receipts = receipts.Documents,
            Settlement = settlement,
        };
    }

    /// <summary>
    /// 复用同一权威结算引用链（ERP-028 财务核对）：由调用方传入已加载的采购订单，返回结算引用状态与金额。
    /// 与 <see cref="ForPurchaseOrderAsync"/> 使用同一派生逻辑，避免「订单执行进度」与「订单财务核对」两处口径分叉。
    /// </summary>
    public static Task<PurchaseOrderSettlementProgress> SettlementForOrderAsync(IErpDbContext db, PurchaseOrder order)
        => BuildSettlementAsync(db, order);

    /// <summary>单张订单的收货进度（转调批量派生，保证全项目只有一套收货规则）</summary>
    private static async Task<ReceiptResult> BuildReceiptsAsync(IErpDbContext db, PurchaseOrder order)
        => (await BuildReceiptsForOrdersAsync(db, new[] { order }))[order.Id];

    /// <summary>
    /// 批量收货进度汇总（供「供应商采购敞口报表」等只读报表复用同一收货口径；固定 2 次查询，无逐单查库）。
    /// </summary>
    public static async Task<Dictionary<long, PurchaseOrderReceiptSummary>> ReceiptSummariesForOrdersAsync(
        IErpDbContext db, IReadOnlyList<PurchaseOrder> orders)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(orders);
        var results = await BuildReceiptsForOrdersAsync(db, orders);
        return results.ToDictionary(kv => kv.Key, kv => new PurchaseOrderReceiptSummary
        {
            OrderedQuantity = kv.Value.OrderedQuantity,
            ReceivedQuantity = kv.Value.ReceivedQuantity,
            MatchedReceivedQuantity = kv.Value.MatchedReceivedQuantity,
            UnmatchedReceivedQuantity = kv.Value.UnmatchedReceivedQuantity,
            PendingQuantity = kv.Value.PendingQuantity,
            OutstandingQuantity = kv.Value.OutstandingQuantity,
            ReceiptStatus = kv.Value.ReceiptStatus,
            Truncated = kv.Value.Truncated,
        });
    }

    /// <summary>
    /// 收货进度批量派生（固定 2 次查询：入库主表 + 入库明细），一页多张订单只查库两次。
    /// <para>页内每张订单的结果与逐单派生完全一致；单张订单沿用 <see cref="DocumentLimit"/> 上限（与既有执行进度一致），
    /// 一页多张订单命中 <see cref="BatchDocumentCeiling"/> 上限时无法把截断归因到具体订单，
    /// 因此整页标记 <see cref="ReceiptResult.Truncated"/>，由调用方以「未知」呈现，绝不静默给出不完整数量。</para>
    /// </summary>
    private static async Task<Dictionary<long, ReceiptResult>> BuildReceiptsForOrdersAsync(
        IErpDbContext db, IReadOnlyList<PurchaseOrder> orders)
    {
        var results = new Dictionary<long, ReceiptResult>();
        if (orders.Count == 0) return results;

        var orderIds = orders.Select(o => o.Id).ToList();
        var limit = QueryLimitFor(orders.Count);
        var stockIns = (await db.StockIns.AsNoTracking()
                .Where(s => s.PurchaseOrderId != null && orderIds.Contains(s.PurchaseOrderId.Value) && !s.IsDeleted)
                .OrderBy(s => s.StockInDate).ThenBy(s => s.Id)
                .Take(limit)
                .Select(s => new { s.Id, s.PurchaseOrderId, s.StockInNo, s.StockInDate, s.Status, s.TotalQuantity })
                .ToListAsync())
            .Select(s => new ReceiptStockInRow(s.Id, s.PurchaseOrderId!.Value, s.StockInNo, s.StockInDate,
                s.Status, s.TotalQuantity))
            .ToList();
        var truncated = orders.Count > 1 && stockIns.Count == limit;

        var stockInIds = stockIns.Select(s => s.Id).ToList();
        var receiptDetails = stockInIds.Count == 0
            ? new List<ReceiptDetailRow>()
            : (await db.StockInDetails.AsNoTracking()
                    .Where(d => stockInIds.Contains(d.StockInId) && !d.IsDeleted)
                    .Select(d => new { d.StockInId, d.ProductId, d.ProductName, d.Quantity })
                    .ToListAsync())
                .Select(d => new ReceiptDetailRow(d.StockInId, d.ProductId, d.ProductName, d.Quantity))
                .ToList();

        foreach (var order in orders)
        {
            var ownStockInIds = stockIns.Where(s => s.PurchaseOrderId == order.Id).Select(s => s.Id).ToHashSet();
            results[order.Id] = BuildReceiptResult(order,
                stockIns.Where(s => s.PurchaseOrderId == order.Id).ToList(),
                receiptDetails.Where(d => ownStockInIds.Contains(d.StockInId)).ToList(),
                truncated);
        }

        return results;
    }

    /// <summary>逐单收货结果（入参为已批量加载的入库单与明细；规则与既有 ERP-026 完全一致）</summary>
    private static ReceiptResult BuildReceiptResult(PurchaseOrder order, List<ReceiptStockInRow> stockIns,
        List<ReceiptDetailRow> receiptDetails, bool truncated)
    {
        var statusById = stockIns.ToDictionary(s => s.Id, s => s.Status);
        bool Counted(long stockInId) => statusById.TryGetValue(stockInId, out var status)
            && status == DocumentStatus.Approved;
        bool AwaitingAudit(long stockInId) => statusById.TryGetValue(stockInId, out var status)
            && status is DocumentStatus.Pending or DocumentStatus.Submitted;

        var receivedPool = receiptDetails.Where(d => Counted(d.StockInId))
            .GroupBy(d => d.ProductId).ToDictionary(g => g.Key, g => g.Sum(x => x.Quantity));
        var pendingPool = receiptDetails.Where(d => AwaitingAudit(d.StockInId))
            .GroupBy(d => d.ProductId).ToDictionary(g => g.Key, g => g.Sum(x => x.Quantity));

        var lines = new List<PurchaseOrderProgressLine>();
        var orderedDetails = order.Details.Where(d => !d.IsDeleted).OrderBy(d => d.Id).ToList();
        var lastDetailIdByProduct = orderedDetails.GroupBy(d => d.ProductId)
            .ToDictionary(g => g.Key, g => g.Max(d => d.Id));
        foreach (var detail in orderedDetails)
        {
            // 同商品多行按明细顺序依次冲抵；该商品最后一行吸收剩余数量（超收体现在订单行上，不列为订单外商品）
            var availableReceived = Take(receivedPool, detail.ProductId);
            var isLastLineOfProduct = detail.Id == lastDetailIdByProduct[detail.ProductId];
            var received = isLastLineOfProduct ? availableReceived : Math.Min(availableReceived, detail.Quantity);
            PutBack(receivedPool, detail.ProductId, availableReceived - received);

            var availablePending = Take(pendingPool, detail.ProductId);
            var pending = Math.Min(availablePending, Math.Max(0m, detail.Quantity - received));
            PutBack(pendingPool, detail.ProductId, availablePending - pending);

            lines.Add(new PurchaseOrderProgressLine
            {
                DetailId = detail.Id,
                ProductId = detail.ProductId,
                ProductName = detail.ProductName,
                Spec = detail.Spec,
                Unit = detail.Unit,
                OrderedQuantity = detail.Quantity,
                ReceivedQuantity = received,
                PendingQuantity = pending,
                OutstandingQuantity = Math.Max(0m, detail.Quantity - received),
                OverReceivedQuantity = Math.Max(0m, received - detail.Quantity),
                ReceiptStatus = LineReceiptStatusOf(detail.Quantity, received),
            });
        }

        var unmatched = receivedPool.Where(kv => kv.Value > 0)
            .Select(kv => new PurchaseOrderUnmatchedReceipt
            {
                ProductId = kv.Key,
                ProductName = receiptDetails.FirstOrDefault(d => d.ProductId == kv.Key)?.ProductName ?? string.Empty,
                ReceivedQuantity = kv.Value,
            })
            .OrderByDescending(u => u.ReceivedQuantity).ThenBy(u => u.ProductId)
            .ToList();

        var orderedQuantity = lines.Sum(l => l.OrderedQuantity);
        var receivedQuantity = receiptDetails.Where(d => Counted(d.StockInId)).Sum(d => d.Quantity);
        var outstandingQuantity = lines.Sum(l => l.OutstandingQuantity);

        return new ReceiptResult
        {
            OrderedQuantity = orderedQuantity,
            ReceivedQuantity = receivedQuantity,
            MatchedReceivedQuantity = lines.Sum(l => l.ReceivedQuantity),
            UnmatchedReceivedQuantity = unmatched.Sum(u => u.ReceivedQuantity),
            PendingQuantity = receiptDetails.Where(d => AwaitingAudit(d.StockInId)).Sum(d => d.Quantity),
            OutstandingQuantity = outstandingQuantity,
            ReceiptStatus = OrderReceiptStatusOf(orderedQuantity, receivedQuantity, outstandingQuantity),
            Truncated = truncated,
            Lines = lines,
            UnmatchedReceipts = unmatched,
            Documents = stockIns.Select(s => new PurchaseOrderReceiptReference
            {
                StockInNo = s.StockInNo,
                StockInDate = s.StockInDate,
                Status = s.Status.ToString(),
                TotalQuantity = s.TotalQuantity,
                Counted = s.Status == DocumentStatus.Approved,
            }).ToList(),
        };
    }

    /// <summary>单张订单的结算进度（转调批量派生，保证「执行进度 / 财务核对 / 敞口报表」只有一套权威引用规则）</summary>
    private static async Task<PurchaseOrderSettlementProgress> BuildSettlementAsync(IErpDbContext db, PurchaseOrder order)
        => (await SettlementForOrdersAsync(db, new[] { order }))[order.Id];

    /// <summary>
    /// 结算进度批量派生（固定 3 次查询：归属销售订单下的采购订单计数 + 货款申请单 + 付款单），一页多张订单只查库三次。
    /// <para>与 <see cref="SettlementForOrderAsync"/>、<see cref="ForPurchaseOrderAsync"/> 共用同一套权威引用规则：
    /// 付款单经「货款申请单」引用本单归属销售订单，且该销售订单下只有本单一张采购订单、付款单供应商与本单一致；
    /// 只在既有引用完整、唯一且币种一致时暴露金额，其余情况一律返回 null（未知）并说明原因。</para>
    /// <para>页内每张订单的结果与逐单派生完全一致；命中批量查询上限（<see cref="BatchDocumentCeiling"/>）时不做静默截断，
    /// 而是把相关订单记为 <see cref="LinkAmbiguous"/>（金额未知）并在原因中说明。</para>
    /// </summary>
    public static async Task<Dictionary<long, PurchaseOrderSettlementProgress>> SettlementForOrdersAsync(
        IErpDbContext db, IReadOnlyList<PurchaseOrder> orders)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(orders);
        var results = new Dictionary<long, PurchaseOrderSettlementProgress>();
        if (orders.Count == 0) return results;
        var limit = QueryLimitFor(orders.Count);

        var eligible = new List<PurchaseOrder>();
        foreach (var order in orders)
        {
            var owningId = order.OwningSalesOrderId ?? 0;
            if (owningId <= 0 || order.SupplierId <= 0)
            {
                results[order.Id] = new PurchaseOrderSettlementProgress
                {
                    RecordedProgress = order.SettlementProgress,
                    LinkStatus = LinkUnavailable,
                    LinkReason = owningId <= 0
                        ? "本单未关联归属销售订单：付款单只记录供应商，货款申请单只引用销售订单，两者都不存在指向本单的既有引用，结算金额未知（不做推断）。"
                        : "本单未维护供应商：付款单无法按供应商归属到本单，结算金额未知（不做推断）。",
                    OwningSalesOrderId = order.OwningSalesOrderId,
                    OwningSalesOrderNo = order.OwningSalesOrderNo,
                };
                continue;
            }

            eligible.Add(order);
        }

        if (eligible.Count == 0) return results;

        var owningIds = eligible.Select(o => o.OwningSalesOrderId!.Value).Distinct().ToList();
        var siblingRows = await db.PurchaseOrders.AsNoTracking()
            .Where(o => !o.IsDeleted && o.OwningSalesOrderId != null && owningIds.Contains(o.OwningSalesOrderId.Value))
            .GroupBy(o => o.OwningSalesOrderId!.Value)
            .Select(g => new { SalesOrderId = g.Key, Count = g.Count() })
            .ToListAsync();
        var siblingCounts = siblingRows.ToDictionary(r => r.SalesOrderId, r => r.Count);


        var applies = (await db.FinancePaymentApplies.AsNoTracking()
                .Where(a => !a.IsDeleted && a.SalesOrderId != null && owningIds.Contains(a.SalesOrderId.Value))
                .OrderBy(a => a.Id)
                .Take(limit)
                .Select(a => new { a.Id, a.ApplyNo, a.SalesOrderId })
                .ToListAsync())
            .Select(a => new SettlementApplyRow(a.Id, a.ApplyNo, a.SalesOrderId!.Value))
            .ToList();
        var appliesTruncated = applies.Count == limit;

        var applyIds = applies.Select(a => a.Id).ToList();
        var supplierIds = eligible.Select(o => o.SupplierId).Distinct().ToList();
        var payments = applyIds.Count == 0
            ? new List<SettlementPaymentRow>()
            : (await db.FinancePayments.AsNoTracking()
                    .Where(p => !p.IsDeleted && supplierIds.Contains(p.SupplierId)
                                && p.PaymentApplyId != null && applyIds.Contains(p.PaymentApplyId.Value))
                    .OrderBy(p => p.PaymentDate).ThenBy(p => p.Id)
                    .Take(limit)
                    .Select(p => new { p.PaymentNo, p.PaymentDate, p.Amount, p.Currency, p.Status, p.PaymentApplyId, p.SupplierId })
                    .ToListAsync())
                .Select(p => new SettlementPaymentRow(p.PaymentNo, p.PaymentDate, p.Amount, p.Currency, p.Status,
                    p.PaymentApplyId!.Value, p.SupplierId))
                .ToList();
        var paymentsTruncated = payments.Count == limit;

        foreach (var order in eligible)
        {
            var owningId = order.OwningSalesOrderId!.Value;
            siblingCounts.TryGetValue(owningId, out var siblingCount);
            results[order.Id] = BuildSettlementResult(order, siblingCount, applies, payments,
                appliesTruncated || paymentsTruncated, limit);
        }

        return results;
    }

    /// <summary>逐单结算结果（入参为已批量加载的货款申请单与付款单；规则与既有 ERP-026 / ERP-028 完全一致）</summary>
    private static PurchaseOrderSettlementProgress BuildSettlementResult(PurchaseOrder order, int siblingCount,
        IReadOnlyList<SettlementApplyRow> applies, IReadOnlyList<SettlementPaymentRow> payments, bool truncated,
        int limit)
    {
        var owningId = order.OwningSalesOrderId ?? 0;
        if (siblingCount > 1)
        {
            return new PurchaseOrderSettlementProgress
            {
                RecordedProgress = order.SettlementProgress,
                LinkStatus = LinkAmbiguous,
                LinkReason = $"归属销售订单下存在 {siblingCount} 张采购订单：付款单只能引用到销售订单，无法在既有引用下唯一归属到本单，结算金额未知（不做推断）。",
                OwningSalesOrderId = owningId,
                OwningSalesOrderNo = order.OwningSalesOrderNo,
            };
        }

        if (truncated)
        {
            return new PurchaseOrderSettlementProgress
            {
                RecordedProgress = order.SettlementProgress,
                LinkStatus = LinkAmbiguous,
                LinkReason = $"本次派生范围内货款申请单 / 付款单数量达到单次查询上限（{limit} 行），无法确认引用完整性，" +
                             "结算金额未知（不做推断）；请收窄供应商 / 币种 / 日期筛选或减小每页条数。",
                OwningSalesOrderId = owningId,
                OwningSalesOrderNo = order.OwningSalesOrderNo,
            };
        }

        var orderApplies = applies.Where(a => a.SalesOrderId == owningId).OrderBy(a => a.Id).ToList();
        if (orderApplies.Count == 0)
        {
            return new PurchaseOrderSettlementProgress
            {
                RecordedProgress = order.SettlementProgress,
                LinkStatus = LinkLinked,
                LinkReason = "归属销售订单唯一，但未找到引用该销售订单的货款申请单：按既有引用本单暂无已登记付款（已结算 0，未结算 = 订单总额）。",
                OwningSalesOrderId = owningId,
                OwningSalesOrderNo = order.OwningSalesOrderNo,
                SettledAmount = 0m,
                OutstandingAmount = order.TotalAmount,
                SubmittedAmount = 0m,
            };
        }

        var orderApplyIds = orderApplies.Select(a => a.Id).ToHashSet();
        var applyNoById = orderApplies.ToDictionary(a => a.Id, a => a.ApplyNo);
        var orderPayments = payments
            .Where(p => p.SupplierId == order.SupplierId && orderApplyIds.Contains(p.PaymentApplyId))
            .ToList();

        var documents = orderPayments.Select(p => new PurchaseOrderSettlementReference
        {
            PaymentNo = p.PaymentNo,
            PaymentDate = p.PaymentDate,
            Amount = p.Amount,
            Currency = p.Currency.ToString(),
            Status = p.Status.ToString(),
            PaymentApplyNo = applyNoById.TryGetValue(p.PaymentApplyId, out var no) ? no : string.Empty,
            Counted = p.Currency == order.Currency && p.Status == DocumentStatus.Approved,
        }).ToList();

        var settled = documents.Where(d => d.Counted).Sum(d => d.Amount);
        var submitted = orderPayments
            .Where(p => p.Currency == order.Currency && p.Status is DocumentStatus.Pending or DocumentStatus.Submitted)
            .Sum(p => p.Amount);
        var otherCurrencyCount = orderPayments.Count(p => p.Currency != order.Currency);

        var reason = $"归属销售订单唯一，已按「货款申请单 → 本单归属销售订单」建立引用（同供应商付款单 {documents.Count} 张）；" +
                     "仅已审核且币种与本单一致的付款单计入已结算。";
        if (otherCurrencyCount > 0)
            reason += $"另有 {otherCurrencyCount} 张付款单币种与本单不一致，仅列出、不计入合计（不做汇率换算）。";

        return new PurchaseOrderSettlementProgress
        {
            RecordedProgress = order.SettlementProgress,
            LinkStatus = LinkLinked,
            LinkReason = reason,
            OwningSalesOrderId = owningId,
            OwningSalesOrderNo = order.OwningSalesOrderNo,
            SettledAmount = settled,
            OutstandingAmount = Math.Max(0m, order.TotalAmount - settled),
            SubmittedAmount = submitted,
            OverSettled = settled - order.TotalAmount > AmountTolerance,
            Documents = documents,
        };
    }

    /// <summary>行收货状态</summary>
    private static string LineReceiptStatusOf(decimal ordered, decimal received)
    {
        if (received <= 0) return ReceiptNone;
        if (received > ordered) return ReceiptOverReceived;
        return received < ordered ? ReceiptPartial : ReceiptComplete;
    }

    /// <summary>整体收货状态（按单据口径的已收数量与订单行未收数量判定）</summary>
    private static string OrderReceiptStatusOf(decimal ordered, decimal received, decimal outstanding)
    {
        if (received <= 0) return ReceiptNone;
        if (ordered <= 0 || received > ordered) return ReceiptOverReceived;
        return outstanding > 0 ? ReceiptPartial : ReceiptComplete;
    }

    /// <summary>人工到货进度与派生状态的比对：未登记 / 词表外文本无法判定，返回 null</summary>
    private static bool? ArrivalProgressConsistent(string recorded, string derived)
    {
        var expected = recorded?.Trim() switch
        {
            "未到货" => ReceiptNone,
            "部分到货" => ReceiptPartial,
            "已到货" => ReceiptComplete,
            _ => null,
        };
        if (expected is null) return null;
        if (expected == ReceiptComplete) return derived is ReceiptComplete or ReceiptOverReceived;
        return expected == derived;
    }

    private static decimal Take(Dictionary<long, decimal> pool, long productId)
        => pool.TryGetValue(productId, out var value) ? value : 0m;

    private static void PutBack(Dictionary<long, decimal> pool, long productId, decimal remainder)
    {
        if (remainder > 0) pool[productId] = remainder;
        else pool.Remove(productId);
    }

    /// <summary>入库明细行的内存投影（查询中不聚合，保证一次取全）</summary>
    private sealed record ReceiptDetailRow(long StockInId, long ProductId, string ProductName, decimal Quantity);

    /// <summary>入库单行的内存投影（批量派生：一次取全本页入库单）</summary>
    private sealed record ReceiptStockInRow(long Id, long PurchaseOrderId, string StockInNo, DateTime StockInDate,
        DocumentStatus Status, decimal TotalQuantity);

    /// <summary>货款申请单行的内存投影（批量派生：一次取全本页涉及的货款申请单；Id 用于付款单归属链）</summary>
    private sealed record SettlementApplyRow(long Id, string ApplyNo, long SalesOrderId);

    /// <summary>付款单行的内存投影（批量派生：一次取全本页涉及的付款单；PaymentApplyId = 货款申请单 Id）</summary>
    private sealed record SettlementPaymentRow(string PaymentNo, DateTime PaymentDate, decimal Amount, Currency Currency,
        DocumentStatus Status, long PaymentApplyId, long SupplierId);

    /// <summary>收货派生的中间结果</summary>
    private sealed class ReceiptResult
    {
        public decimal OrderedQuantity { get; init; }
        public decimal ReceivedQuantity { get; init; }
        public decimal MatchedReceivedQuantity { get; init; }
        public decimal UnmatchedReceivedQuantity { get; init; }
        public decimal PendingQuantity { get; init; }
        public decimal OutstandingQuantity { get; init; }
        public string ReceiptStatus { get; init; } = ReceiptNone;

        /// <summary>是否命中批量派生上限（一页多张订单时；数量不完整，只能按「未知」呈现）</summary>
        public bool Truncated { get; init; }

        public List<PurchaseOrderProgressLine> Lines { get; init; } = new();
        public List<PurchaseOrderUnmatchedReceipt> UnmatchedReceipts { get; init; } = new();
        public List<PurchaseOrderReceiptReference> Documents { get; init; } = new();
    }
}
