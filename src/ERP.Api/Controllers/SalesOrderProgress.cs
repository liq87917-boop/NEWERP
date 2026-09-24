using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ERP.Api.Controllers;

/// <summary>销售订单明细行出货进度（只读派生，不落库）</summary>
public sealed class SalesOrderProgressLine
{
    public long DetailId { get; init; }
    public long ProductId { get; init; }
    public string ProductName { get; init; } = string.Empty;
    public string Spec { get; init; } = string.Empty;
    public string Unit { get; init; } = string.Empty;

    /// <summary>订单数量（销售订单明细数量）</summary>
    public decimal OrderedQuantity { get; init; }

    /// <summary>已审核出库数量（同商品多行按明细顺序依次冲抵）</summary>
    public decimal ShippedQuantity { get; init; }

    /// <summary>已提交 / 待提交出库数量（未审核，不计入已出货）</summary>
    public decimal PendingQuantity { get; init; }

    /// <summary>未出货数量 = max(0, 订单数量 − 已出货数量)</summary>
    public decimal OutstandingQuantity { get; init; }

    /// <summary>超发数量 = max(0, 已出货数量 − 订单数量)</summary>
    public decimal OverShippedQuantity { get; init; }

    /// <summary>行出货状态：none / partial / complete / over_shipped</summary>
    public string ShipmentStatus { get; init; } = SalesOrderProgress.ShipmentNone;
}

/// <summary>
/// 销售订单出货进度汇总（只读派生；「出货与收款进度」与「销售订单出货 / 财务进度报表」共用同一出货口径）。
/// </summary>
public sealed class SalesOrderShipmentSummary
{
    /// <summary>订单数量合计（订单明细口径）</summary>
    public decimal OrderedQuantity { get; init; }

    /// <summary>已出货数量合计（已审核出库单据口径，含订单外商品）</summary>
    public decimal ShippedQuantity { get; init; }

    /// <summary>已冲抵订单明细的已出货数量（含超发部分）</summary>
    public decimal MatchedShippedQuantity { get; init; }

    /// <summary>订单外商品的已出货数量（显式单列，不并入订单行）</summary>
    public decimal UnmatchedShippedQuantity { get; init; }

    /// <summary>待审核出库数量合计（不计入已出货）</summary>
    public decimal PendingQuantity { get; init; }

    /// <summary>未出货数量合计 = Σ 订单行未出货数量</summary>
    public decimal OutstandingQuantity { get; init; }

    /// <summary>超发数量合计 = Σ 订单行超发数量</summary>
    public decimal OverShippedQuantity { get; init; }

    /// <summary>整体出货状态：none / partial / complete / over_shipped（命中上限时为 unknown）</summary>
    public string ShipmentStatus { get; init; } = SalesOrderProgress.ShipmentNone;

    /// <summary>是否存在「以本单为来源、未删除、已审核」的销售出库单（报表出货状态筛选与此条件完全一致）</summary>
    public bool HasApprovedShipment { get; init; }

    /// <summary>以本单为来源的出库单数（未删除，含未审核）</summary>
    public int ShipmentDocumentCount { get; init; }

    /// <summary>以本单为来源且已审核的出库单数</summary>
    public int ApprovedShipmentCount { get; init; }

    /// <summary>是否命中派生上限（true = 以上数量不完整，调用方必须按「未知」呈现，不得当作 0 或全量）</summary>
    public bool Truncated { get; init; }
}

/// <summary>订单外商品的出货数量（出库明细商品不在本销售订单明细中，显式单列而不并入订单行）</summary>
public sealed class SalesOrderUnmatchedShipment
{
    public long ProductId { get; init; }
    public string ProductName { get; init; } = string.Empty;
    public decimal ShippedQuantity { get; init; }
}

/// <summary>出货来源单据引用（以本单为来源的销售出库单）</summary>
public sealed class SalesOrderShipmentReference
{
    public string StockOutNo { get; init; } = string.Empty;
    public DateTime StockOutDate { get; init; }
    public string Status { get; init; } = string.Empty;
    public decimal TotalQuantity { get; init; }

    /// <summary>是否计入「已出货数量」（= 已审核；待提交 / 已提交 / 已驳回 / 已取消均不计入）</summary>
    public bool Counted { get; init; }
}

/// <summary>
/// 一条收款链接记录（只读派生）：单号 / 日期 / 金额 / 币种 / 状态 + 建立引用所用的既有字段 + 是否计入合计。
/// 引用字段口径与 ERP-028 订单财务核对完全一致，不引入第二套匹配算法。
/// </summary>
public sealed class SalesOrderFinanceRecord
{
    /// <summary>来源实体（FinanceDepositApply / FinancePaymentApply / FinanceReceipt / FinanceContainerSettlement / FinanceBulkSettlement）</summary>
    public string Source { get; init; } = string.Empty;

    /// <summary>单据号（申请单号 / 收款单号 / 结算单号）</summary>
    public string DocumentNo { get; init; } = string.Empty;

    /// <summary>单据日期</summary>
    public DateTime DocumentDate { get; init; }

    /// <summary>单据金额（原币，多币种绝不汇总、不做汇率换算）</summary>
    public decimal Amount { get; init; }

    /// <summary>币种（枚举名；无币种列的来源为空串——币种未知）</summary>
    public string Currency { get; init; } = string.Empty;

    /// <summary>单据状态（枚举名）</summary>
    public string Status { get; init; } = string.Empty;

    /// <summary>建立引用所用的既有字段</summary>
    public string ReferenceField { get; init; } = string.Empty;

    /// <summary>是否计入已关联金额（= 权威引用 + 已审核 + 币种与本单一致）</summary>
    public bool Counted { get; init; }

    /// <summary>说明（不计入原因 / 是否可归属本单）</summary>
    public string Note { get; init; } = string.Empty;
}

/// <summary>
/// 销售订单收款链接进度（只读派生，复用 ERP-028 的既有引用规则）。
/// 金额为 null 表示未知（无可用权威引用或命中派生上限），不等于 0，也不代表「全部未收」。
/// </summary>
public sealed class SalesOrderFinanceProgress
{
    /// <summary>引用状态：linked（权威引用完整）/ partial（部分可归属）/ unlinked（无可用引用）/ unknown（命中派生上限）</summary>
    public string LinkStatus { get; init; } = SalesOrderProgress.LinkUnlinked;

    /// <summary>引用状态说明（缺什么、为什么不推断；与派生逻辑同源）</summary>
    public string LinkReason { get; init; } = string.Empty;

    /// <summary>已关联金额（指向本单的已审核 + 同币种定金 / 货款申请）；null = 未知（不用 0 顶替）</summary>
    public decimal? LinkedAmount { get; init; }

    /// <summary>未覆盖金额 = 订单金额 − 已关联金额；null = 未知；负数表示超过订单金额（超收）</summary>
    public decimal? UncoveredAmount { get; init; }

    /// <summary>已提交 / 待提交（未审核、同币种）申请金额；null = 未知，仅单列不计入</summary>
    public decimal? SubmittedAmount { get; init; }

    /// <summary>他币种记录数（指向本单但币种不一致：仅列出、不计入、不做汇率换算）</summary>
    public int OtherCurrencyRecordCount { get; init; }

    /// <summary>非「已审核」记录数（已驳回 / 已取消 / 已完成等：仅列出、不计入）</summary>
    public int UnapprovedRecordCount { get; init; }

    /// <summary>无法归属本单的客户级记录数（收款单 / 装柜结算单 / 散货结算单只记录客户，没有订单级引用）</summary>
    public int UnattributedRecordCount { get; init; }

    /// <summary>客户级记录是否命中单次查询上限（true = 上述计数不完整）</summary>
    public bool UnattributedRecordsTruncated { get; init; }

    /// <summary>是否超收（已关联金额 &gt; 订单金额，按金额容差判定）</summary>
    public bool OverReceived { get; init; }

    /// <summary>引用记录（逐单视图逐条列出；报表页只给计数，避免整页报文膨胀）</summary>
    public List<SalesOrderFinanceRecord> Records { get; init; } = new();
}

/// <summary>一张销售订单的出货与收款派生结果（供逐单视图与报表共用）</summary>
public sealed class SalesOrderProgressResult
{
    public long OrderId { get; init; }
    public SalesOrderShipmentSummary Shipment { get; init; } = new();
    public SalesOrderFinanceProgress Finance { get; init; } = new();

    /// <summary>逐单视图的明细行（报表批量派生时为空，避免整页报文膨胀）</summary>
    public List<SalesOrderProgressLine> Lines { get; init; } = new();

    /// <summary>逐单视图的订单外商品出货（报表批量派生时为空）</summary>
    public List<SalesOrderUnmatchedShipment> UnmatchedShipments { get; init; } = new();

    /// <summary>逐单视图的出货来源单据（报表批量派生时为空）</summary>
    public List<SalesOrderShipmentReference> Shipments { get; init; } = new();
}

/// <summary>销售订单出货与收款进度明细视图（ERP-032，只读派生；不落库、不改单据状态、不新增或修改任何表列）</summary>
public sealed class SalesOrderProgressView
{
    public long OrderId { get; init; }
    public string OrderNo { get; init; } = string.Empty;

    /// <summary>外销合同号（既有列回显，便于对单）</summary>
    public string ContractNo { get; init; } = string.Empty;

    /// <summary>客户 PO 号（既有列回显）</summary>
    public string CustomerPoNo { get; init; } = string.Empty;

    public string Status { get; init; } = string.Empty;
    public string Currency { get; init; } = string.Empty;

    /// <summary>订单金额（订单主表已落库总额，报表不重算）</summary>
    public decimal OrderAmount { get; init; }

    /// <summary>已落库定金金额（订单字段原样回显，不是派生的收款金额）</summary>
    public decimal RecordedDepositAmount { get; init; }

    /// <summary>出货进度（数量口径；Truncated = true 时数量不完整，只能按「未知」呈现）</summary>
    public SalesOrderShipmentSummary Shipment { get; init; } = new();

    /// <summary>收款链接进度（金额未知一律用 null 表达）</summary>
    public SalesOrderFinanceProgress Finance { get; init; } = new();

    public List<SalesOrderProgressLine> Lines { get; init; } = new();
    public List<SalesOrderUnmatchedShipment> UnmatchedShipments { get; init; } = new();
    public List<SalesOrderShipmentReference> Shipments { get; init; } = new();

    /// <summary>出货口径说明（与派生逻辑同源，供界面原样展示）</summary>
    public string ShipmentRule { get; init; } = SalesOrderProgress.ShipmentRuleText;

    /// <summary>收款链接口径说明（复用 ERP-028 的既有引用规则）</summary>
    public string FinanceRule { get; init; } = SalesOrderProgress.FinanceRuleText;
}

/// <summary>
/// 销售订单出货与收款进度派生（ERP-032，只读）。
/// <para><b>出货</b>只按「以本单为来源（StockOut.SalesOrderId）、未删除」的销售出库单明细派生：只有已审核出库单计入已出货，
/// 待提交 / 已提交单列（不计入），已驳回 / 已取消 / 软删除一律不计；同商品多行按明细顺序依次冲抵，订单外商品显式单列，不并入订单行。
/// 销售退货与库存调整不冲减已出货数量；ERP-025 的库存流水是同一批出库凭证的账簿记录（按基础单位），因此不再叠加，避免重复计数。</para>
/// <para><b>收款链接</b>复用 ERP-028「订单财务核对」的同一套既有引用字段（定金申请单 / 货款申请单的 SalesOrderId）：
/// 只有「权威引用 + 已审核 + 币种与本单一致」的记录计入已关联金额；他币种与未审核记录仅列出、不汇总、不做汇率换算；
/// 收款单 / 装柜结算单 / 散货结算单只记录客户，无法归属到本单（仅列出、不计入）。</para>
/// <para><b>未知与 0 严格区分</b>：没有权威引用或命中派生上限时金额为 null（未知），不用 0 顶替；
/// 查询有界：固定查询次数 + 每类来源单次上限，无逐单查库（无 N+1）。</para>
/// </summary>
public static class SalesOrderProgress
{
    // ---------- 出货状态 ----------
    /// <summary>未出货</summary>
    public const string ShipmentNone = "none";

    /// <summary>部分出货</summary>
    public const string ShipmentPartial = "partial";

    /// <summary>已出齐</summary>
    public const string ShipmentComplete = "complete";

    /// <summary>超发（已出货 &gt; 订单数量）</summary>
    public const string ShipmentOver = "over_shipped";

    /// <summary>未知（命中派生上限：数量不完整）</summary>
    public const string ShipmentUnknown = "unknown";

    // ---------- 收款引用状态 ----------
    /// <summary>权威引用完整（指向本单的收款申请全部可计入）</summary>
    public const string LinkLinked = "linked";

    /// <summary>部分可归属（存在他币种 / 未审核 / 非已审核的收款申请）</summary>
    public const string LinkPartial = "partial";

    /// <summary>无可用权威引用（没有指向本单的收款申请；金额未知，不用 0 顶替）</summary>
    public const string LinkUnlinked = "unlinked";

    /// <summary>未知（命中派生上限，无法确认引用是否完整）</summary>
    public const string LinkUnknown = "unknown";

    // ---------- 既有引用字段（与 ERP-028 订单财务核对完全一致） ----------
    /// <summary>定金申请单以 SalesOrderId 指向本单</summary>
    public const string ReferenceDepositApply = "FinanceDepositApply.SalesOrderId";

    /// <summary>货款申请单以 SalesOrderId 指向本单</summary>
    public const string ReferencePaymentApply = "FinancePaymentApply.SalesOrderId";

    /// <summary>收款单只记录客户（无订单级引用）</summary>
    public const string ReferenceReceipt = "FinanceReceipt.CustomerId";

    /// <summary>装柜结算单只记录客户（无订单级引用）</summary>
    public const string ReferenceContainerSettlement = "FinanceContainerSettlement.CustomerId";

    /// <summary>散货结算单只记录客户（无订单级引用）</summary>
    public const string ReferenceBulkSettlement = "FinanceBulkSettlement.CustomerId";

    /// <summary>来源单据没有币种列时，记录中的币种按空串呈现（币种未知，绝不参与汇总）</summary>
    public const string CurrencyUnspecified = "";

    /// <summary>单张订单：每类来源单次查询上限（与既有执行进度口径一致）</summary>
    public const int SingleOrderDocumentLimit = 200;

    /// <summary>一页多张订单：批量派生单次查询上限（命中时按「未知」呈现，绝不静默截断）</summary>
    public const int BatchDocumentCeiling = 2000;

    /// <summary>金额容差（超收判定，避免小数尾差误报）</summary>
    public const decimal AmountTolerance = 0.005m;

    /// <summary>出货口径说明（与派生逻辑同源，供界面与文档原样引用）</summary>
    public const string ShipmentRuleText =
        "出货数量只按「以本单为来源（StockOut.SalesOrderId）、未删除」的销售出库单明细派生，出库明细已折算基础单位（ERP-023）；" +
        "只有「已审核」出库单计入已出货，待提交 / 已提交单列为待审（不计入），已驳回 / 已取消 / 软删除一律不计；" +
        "同商品多行按明细顺序依次冲抵（该商品最后一行吸收剩余数量，超发体现在订单行上），订单外商品的已出货数量显式单列、不并入订单行；" +
        "销售退货与库存调整不冲减已出货数量；ERP-025 的库存流水是同一批出库凭证的账簿记录（按基础单位），因此不再叠加，避免重复计数；" +
        "本次派生命中单次上限时已出货 / 未出货数量记为未知（不静默给出不完整数量）。";

    /// <summary>收款链接口径说明（复用 ERP-028 的既有引用规则，不引入第二套匹配算法）</summary>
    public const string FinanceRuleText =
        "收款链接复用「订单财务核对（ERP-028）」的同一套既有引用规则：只有定金申请单 / 货款申请单以 SalesOrderId 指向本单时才是权威引用；" +
        "只有「已审核 + 币种与本单一致」的记录计入已关联金额，他币种与未审核（待提交 / 已提交）记录仅列出、不计入、不做任何汇率换算（不同币种分别成行、绝不合并为一个金额）；" +
        "非「已审核」状态（已驳回 / 已取消 / 已完成）同样仅列出；收款单 / 装柜结算单 / 散货结算单只记录客户，没有订单级引用，一律无法归属到本单；" +
        "没有指向本单的权威引用时，已关联金额与未覆盖金额记为未知（null），不用 0 顶替（本单没有收款申请，不等于客户没有付过款）；" +
        "未覆盖金额 = 订单金额 − 已关联金额，负数表示超过订单金额（超收），它不等于未收款金额；" +
        "报表的收款链接状态按「权威引用是否完整可计入」分档：linked（全部计入）/ partial（部分可归属：他币种或未审核记录仅列出）/ unlinked（无可用引用，金额未知）——" +
        "该分档描述可计入程度，与 ERP-028 的金额状态（linked / partial / unknown）层次不同，但两处的引用字段与计入规则完全一致。";

    /// <summary>报表口径说明（出货 + 收款，供界面与文档原样引用）</summary>
    public const string RuleText = ShipmentRuleText + FinanceRuleText;

    /// <summary>逐单出货与收款进度（GET /api/sales-orders/{id}/progress，只读派生，不写库）</summary>
    public static async Task<SalesOrderProgressView> ForSalesOrderAsync(IErpDbContext db, long id)
    {
        ArgumentNullException.ThrowIfNull(db);
        var order = await db.SalesOrders.AsNoTracking()
            .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
            ?? throw BusinessException.NotFound("销售订单不存在");

        var result = (await BuildAsync(db, new[] { order }, withDetails: true)).Single();
        return new SalesOrderProgressView
        {
            OrderId = order.Id,
            OrderNo = order.OrderNo,
            ContractNo = order.ContractNo,
            CustomerPoNo = order.CustomerPoNo,
            Status = order.Status.ToString(),
            Currency = order.Currency.ToString(),
            OrderAmount = order.TotalAmount,
            RecordedDepositAmount = order.DepositAmount,
            Shipment = result.Shipment,
            Finance = result.Finance,
            Lines = result.Lines,
            UnmatchedShipments = result.UnmatchedShipments,
            Shipments = result.Shipments,
        };
    }

    /// <summary>
    /// 批量派生一页订单的出货与收款进度（供报表复用同一口径；固定 8 次数据集查询，无逐单查库）。
    /// <para>页内每张订单的结果与逐单派生完全一致；订单明细不随入参带入，由本方法自带一次有界查询，避免调用方漏加载导致订单数量被当成 0。</para>
    /// <para>命中 <see cref="BatchDocumentCeiling"/> 时无法把截断归因到具体订单，因此整页出货数量与收款金额记为未知，绝不静默给出不完整数字。</para>
    /// </summary>
    public static async Task<List<SalesOrderProgressResult>> ForOrdersAsync(IErpDbContext db,
        IReadOnlyList<SalesOrder> orders)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(orders);
        return await BuildAsync(db, orders, withDetails: false);
    }

    /// <summary>批量派生的单次查询上限（单张订单与整页订单分别使用不同上限）</summary>
    private static int QueryLimitFor(int orderCount) => orderCount > 1 ? BatchDocumentCeiling : SingleOrderDocumentLimit;

    /// <summary>
    /// 出货与收款派生的主体（固定 8 次有界查询：订单明细 / 出库主表 / 出库明细 / 定金申请单 / 货款申请单 / 收款单 / 装柜结算单 / 散货结算单，
    /// 加上由调用方提供的订单本身；查询次数与页大小、订单数、单据数无关，无逐单查库）。
    /// </summary>
    private static async Task<List<SalesOrderProgressResult>> BuildAsync(IErpDbContext db,
        IReadOnlyList<SalesOrder> orders, bool withDetails)
    {
        var results = new List<SalesOrderProgressResult>();
        if (orders.Count == 0) return results;
        var orderIds = orders.Select(o => o.Id).ToList();
        var limit = QueryLimitFor(orders.Count);

        // 订单明细（1 次）：数量口径的权威来源，不随入参带入，避免调用方漏加载时被当成 0
        var detailRows = (await db.SalesOrderDetails.AsNoTracking()
                .Where(d => !d.IsDeleted && orderIds.Contains(d.SalesOrderId))
                .OrderBy(d => d.Id).Take(limit)
                .Select(d => new { d.Id, d.SalesOrderId, d.ProductId, d.ProductName, d.Spec, d.Unit, d.Quantity })
                .ToListAsync())
            .Select(d => new OrderDetailRow(d.Id, d.SalesOrderId, d.ProductId, d.ProductName, d.Spec, d.Unit, d.Quantity))
            .ToList();
        var detailTruncated = detailRows.Count == limit;

        // 出库主表（1 次）：只有 StockOut.SalesOrderId 是既有的订单级来源引用
        var stockOuts = (await db.StockOuts.AsNoTracking()
                .Where(s => !s.IsDeleted && s.SalesOrderId != null && orderIds.Contains(s.SalesOrderId.Value))
                .OrderBy(s => s.StockOutDate).ThenBy(s => s.Id).Take(limit)
                .Select(s => new { s.Id, s.SalesOrderId, s.StockOutNo, s.StockOutDate, s.Status, s.TotalQuantity })
                .ToListAsync())
            .Select(s => new ShipmentDocumentRow(s.Id, s.SalesOrderId!.Value, s.StockOutNo, s.StockOutDate, s.Status,
                s.TotalQuantity))
            .ToList();
        var stockOutTruncated = stockOuts.Count == limit;

        // 出库明细（1 次）：按本页出库单一次取全（已折算基础单位，不再叠加库存流水）
        var stockOutIds = stockOuts.Select(s => s.Id).ToList();
        var shipmentDetailRows = stockOutIds.Count == 0
            ? new List<ShipmentDetailRow>()
            : (await db.StockOutDetails.AsNoTracking()
                    .Where(d => !d.IsDeleted && stockOutIds.Contains(d.StockOutId))
                    .Take(limit)
                    .Select(d => new { d.StockOutId, d.ProductId, d.ProductName, d.Quantity })
                    .ToListAsync())
                .Select(d => new ShipmentDetailRow(d.StockOutId, d.ProductId, d.ProductName, d.Quantity))
                .ToList();
        var shipmentDetailTruncated = shipmentDetailRows.Count == limit;

        // 权威收款引用（2 次）：定金申请单 + 货款申请单，均按既有 SalesOrderId 归属
        var deposits = (await db.FinanceDepositApplies.AsNoTracking()
                .Where(a => !a.IsDeleted && a.SalesOrderId != null && orderIds.Contains(a.SalesOrderId.Value))
                .OrderBy(a => a.ApplyDate).ThenBy(a => a.Id).Take(limit)
                .Select(a => new { a.SalesOrderId, a.ApplyNo, a.ApplyDate, a.Amount, a.Currency, a.Status })
                .ToListAsync())
            .Select(a => new FinanceApplyRow(a.SalesOrderId!.Value, "FinanceDepositApply", a.ApplyNo, a.ApplyDate,
                a.Amount, a.Currency.ToString(), a.Status, ReferenceDepositApply))
            .ToList();
        var depositTruncated = deposits.Count == limit;

        var paymentApplies = (await db.FinancePaymentApplies.AsNoTracking()
                .Where(a => !a.IsDeleted && a.SalesOrderId != null && orderIds.Contains(a.SalesOrderId.Value))
                .OrderBy(a => a.ApplyDate).ThenBy(a => a.Id).Take(limit)
                .Select(a => new { a.SalesOrderId, a.ApplyNo, a.ApplyDate, a.Amount, a.Currency, a.Status })
                .ToListAsync())
            .Select(a => new FinanceApplyRow(a.SalesOrderId!.Value, "FinancePaymentApply", a.ApplyNo, a.ApplyDate,
                a.Amount, a.Currency.ToString(), a.Status, ReferencePaymentApply))
            .ToList();
        var paymentApplyTruncated = paymentApplies.Count == limit;

        // 客户级记录（3 次）：只按客户归属，无法归属到订单；仅用于「不可归属」计数与逐单列出（不汇总、不换算）
        var customerIds = orders.Select(o => o.CustomerId).Where(c => c > 0).Distinct().ToList();
        var receipts = customerIds.Count == 0
            ? new List<CustomerLevelRow>()
            : (await db.FinanceReceipts.AsNoTracking()
                    .Where(r => !r.IsDeleted && customerIds.Contains(r.CustomerId))
                    .OrderBy(r => r.ReceiptDate).ThenBy(r => r.Id).Take(limit)
                    .Select(r => new { r.CustomerId, r.ReceiptNo, r.ReceiptDate, r.Amount, r.Currency, r.Status })
                    .ToListAsync())
                .Select(r => new CustomerLevelRow(r.CustomerId, "FinanceReceipt", r.ReceiptNo, r.ReceiptDate, r.Amount,
                    r.Currency.ToString(), r.Status.ToString(), ReferenceReceipt))
                .ToList();
        var receiptTruncated = receipts.Count == limit;

        var containerSettlements = customerIds.Count == 0
            ? new List<CustomerLevelRow>()
            : (await db.FinanceContainerSettlements.AsNoTracking()
                    .Where(s => !s.IsDeleted && customerIds.Contains(s.CustomerId))
                    .OrderBy(s => s.SettlementDate).ThenBy(s => s.Id).Take(limit)
                    .Select(s => new { s.CustomerId, s.SettlementNo, s.SettlementDate, s.TotalAmount, s.Status })
                    .ToListAsync())
                .Select(s => new CustomerLevelRow(s.CustomerId, "FinanceContainerSettlement", s.SettlementNo,
                    s.SettlementDate, s.TotalAmount, CurrencyUnspecified, s.Status.ToString(),
                    ReferenceContainerSettlement))
                .ToList();
        var containerTruncated = containerSettlements.Count == limit;

        var bulkSettlements = customerIds.Count == 0
            ? new List<CustomerLevelRow>()
            : (await db.FinanceBulkSettlements.AsNoTracking()
                    .Where(s => !s.IsDeleted && customerIds.Contains(s.CustomerId))
                    .OrderBy(s => s.SettlementDate).ThenBy(s => s.Id).Take(limit)
                    .Select(s => new { s.CustomerId, s.SettlementNo, s.SettlementDate, s.TotalAmount, s.Status })
                    .ToListAsync())
                .Select(s => new CustomerLevelRow(s.CustomerId, "FinanceBulkSettlement", s.SettlementNo,
                    s.SettlementDate, s.TotalAmount, CurrencyUnspecified, s.Status.ToString(),
                    ReferenceBulkSettlement))
                .ToList();
        var bulkTruncated = bulkSettlements.Count == limit;

        foreach (var order in orders.OrderBy(o => o.Id))
        {
            var ownDetails = detailRows.Where(d => d.SalesOrderId == order.Id).ToList();
            var ownStockOuts = stockOuts.Where(s => s.OrderId == order.Id).ToList();
            var ownStockOutIds = ownStockOuts.Select(s => s.Id).ToHashSet();
            var ownShipmentDetails = shipmentDetailRows.Where(d => ownStockOutIds.Contains(d.StockOutId)).ToList();
            var shipmentTruncated = detailTruncated || stockOutTruncated || shipmentDetailTruncated;

            var shipment = BuildShipmentResult(ownDetails, ownStockOuts, ownShipmentDetails, shipmentTruncated,
                withDetails);

            var ownApplies = deposits.Where(a => a.OrderId == order.Id)
                .Concat(paymentApplies.Where(a => a.OrderId == order.Id))
                .OrderBy(a => a.Date).ThenBy(a => a.No, StringComparer.Ordinal)
                .ToList();
            var customerLevel = receipts.Where(r => r.CustomerId == order.CustomerId)
                .Concat(containerSettlements.Where(s => s.CustomerId == order.CustomerId))
                .Concat(bulkSettlements.Where(s => s.CustomerId == order.CustomerId))
                .OrderBy(r => r.Date).ThenBy(r => r.Source, StringComparer.Ordinal)
                .ToList();

            var finance = BuildFinanceResult(order, ownApplies, customerLevel,
                depositTruncated || paymentApplyTruncated,
                receiptTruncated || containerTruncated || bulkTruncated, withDetails);

            results.Add(new SalesOrderProgressResult
            {
                OrderId = order.Id,
                Shipment = shipment.Summary,
                Finance = finance,
                Lines = shipment.Lines,
                UnmatchedShipments = shipment.Unmatched,
                Shipments = shipment.Documents,
            });
        }

        return results;
    }

    /// <summary>逐单出货结果（入参为已批量加载的出库单与明细；同一套「已审核出库」规则）</summary>
    private static ShipmentResult BuildShipmentResult(List<OrderDetailRow> details,
        List<ShipmentDocumentRow> stockOuts, List<ShipmentDetailRow> shipmentDetails, bool truncated, bool withDetails)
    {
        var statusById = stockOuts.ToDictionary(s => s.Id, s => s.Status);
        bool Counted(long stockOutId) => statusById.TryGetValue(stockOutId, out var status)
            && status == DocumentStatus.Approved;
        bool AwaitingAudit(long stockOutId) => statusById.TryGetValue(stockOutId, out var status)
            && status is DocumentStatus.Pending or DocumentStatus.Submitted;

        var shippedPool = shipmentDetails.Where(d => Counted(d.StockOutId))
            .GroupBy(d => d.ProductId).ToDictionary(g => g.Key, g => g.Sum(x => x.Quantity));
        var pendingPool = shipmentDetails.Where(d => AwaitingAudit(d.StockOutId))
            .GroupBy(d => d.ProductId).ToDictionary(g => g.Key, g => g.Sum(x => x.Quantity));

        var lines = new List<SalesOrderProgressLine>();
        var orderedDetails = details.OrderBy(d => d.Id).ToList();
        var lastDetailIdByProduct = orderedDetails.GroupBy(d => d.ProductId)
            .ToDictionary(g => g.Key, g => g.Max(d => d.Id));
        foreach (var detail in orderedDetails)
        {
            // 同商品多行按明细顺序依次冲抵；该商品最后一行吸收剩余数量（超发体现在订单行上，不列为订单外商品）
            var availableShipped = Take(shippedPool, detail.ProductId);
            var isLastLineOfProduct = detail.Id == lastDetailIdByProduct[detail.ProductId];
            var shipped = isLastLineOfProduct ? availableShipped : Math.Min(availableShipped, detail.Quantity);
            PutBack(shippedPool, detail.ProductId, availableShipped - shipped);

            var availablePending = Take(pendingPool, detail.ProductId);
            var pending = Math.Min(availablePending, Math.Max(0m, detail.Quantity - shipped));
            PutBack(pendingPool, detail.ProductId, availablePending - pending);

            lines.Add(new SalesOrderProgressLine
            {
                DetailId = detail.Id,
                ProductId = detail.ProductId,
                ProductName = detail.ProductName,
                Spec = detail.Spec,
                Unit = detail.Unit,
                OrderedQuantity = detail.Quantity,
                ShippedQuantity = shipped,
                PendingQuantity = pending,
                OutstandingQuantity = Math.Max(0m, detail.Quantity - shipped),
                OverShippedQuantity = Math.Max(0m, shipped - detail.Quantity),
                ShipmentStatus = LineShipmentStatusOf(detail.Quantity, shipped),
            });
        }

        var unmatched = shippedPool.Where(kv => kv.Value > 0)
            .Select(kv => new SalesOrderUnmatchedShipment
            {
                ProductId = kv.Key,
                ProductName = shipmentDetails.FirstOrDefault(d => d.ProductId == kv.Key)?.ProductName ?? string.Empty,
                ShippedQuantity = kv.Value,
            })
            .OrderByDescending(u => u.ShippedQuantity).ThenBy(u => u.ProductId)
            .ToList();

        var orderedQuantity = lines.Sum(l => l.OrderedQuantity);
        var shippedQuantity = shipmentDetails.Where(d => Counted(d.StockOutId)).Sum(d => d.Quantity);
        var outstandingQuantity = lines.Sum(l => l.OutstandingQuantity);

        return new ShipmentResult
        {
            Summary = new SalesOrderShipmentSummary
            {
                OrderedQuantity = orderedQuantity,
                ShippedQuantity = shippedQuantity,
                MatchedShippedQuantity = lines.Sum(l => l.ShippedQuantity),
                UnmatchedShippedQuantity = unmatched.Sum(u => u.ShippedQuantity),
                PendingQuantity = shipmentDetails.Where(d => AwaitingAudit(d.StockOutId)).Sum(d => d.Quantity),
                OutstandingQuantity = outstandingQuantity,
                OverShippedQuantity = lines.Sum(l => l.OverShippedQuantity),
                ShipmentStatus = truncated
                    ? ShipmentUnknown
                    : OrderShipmentStatusOf(orderedQuantity, shippedQuantity, outstandingQuantity),
                HasApprovedShipment = stockOuts.Any(s => s.Status == DocumentStatus.Approved),
                ShipmentDocumentCount = stockOuts.Count,
                ApprovedShipmentCount = stockOuts.Count(s => s.Status == DocumentStatus.Approved),
                Truncated = truncated,
            },
            Lines = withDetails ? lines : new List<SalesOrderProgressLine>(),
            Unmatched = withDetails ? unmatched : new List<SalesOrderUnmatchedShipment>(),
            Documents = withDetails
                ? stockOuts.Select(s => new SalesOrderShipmentReference
                {
                    StockOutNo = s.No,
                    StockOutDate = s.Date,
                    Status = s.Status.ToString(),
                    TotalQuantity = s.TotalQuantity,
                    Counted = s.Status == DocumentStatus.Approved,
                }).ToList()
                : new List<SalesOrderShipmentReference>(),
        };
    }

    /// <summary>
    /// 收款链接结果（复用 ERP-028 的既有引用规则）：只有「权威引用 + 已审核 + 币种一致」计入已关联金额；
    /// 无权威引用或命中派生上限时金额为 null（未知），不用 0 顶替。
    /// </summary>
    private static SalesOrderFinanceProgress BuildFinanceResult(SalesOrder order, List<FinanceApplyRow> own,
        List<CustomerLevelRow> customerLevel, bool authoritativeTruncated, bool customerLevelTruncated,
        bool withRecords)
    {
        var orderCurrency = order.Currency.ToString();
        // 计入 = 权威引用 + 已审核 + 币种一致；他币种一律只列出（不汇总、不换算）
        var counted = own.Where(r => r.Counted && SameCurrency(r.Currency, orderCurrency)).ToList();
        var awaiting = own.Where(r => !r.Counted && r.AwaitingAudit && SameCurrency(r.Currency, orderCurrency)).ToList();
        var otherCurrency = own.Where(r => !SameCurrency(r.Currency, orderCurrency)).ToList();
        var unapproved = own
            .Where(r => SameCurrency(r.Currency, orderCurrency) && !r.Counted && !r.AwaitingAudit).ToList();

        decimal? linked = null;
        decimal? submitted = null;
        decimal? uncovered = null;
        string status;
        string reason;
        if (authoritativeTruncated)
        {
            status = LinkUnknown;
            reason = "指向本单的定金 / 货款申请单超过单次派生上限：无法确认收款引用是否完整，" +
                "已关联金额与未覆盖金额记为未知（null，绝不静默给出不完整金额）。";
        }
        else if (own.Count == 0)
        {
            status = LinkUnlinked;
            reason = "没有任何定金申请单 / 货款申请单以 SalesOrderId 指向本单：不存在可用权威引用，" +
                "已关联金额与未覆盖金额为未知（null），不用 0 顶替（本单没有收款申请，不等于客户没有付过款）。";
        }
        else if (otherCurrency.Count == 0 && unapproved.Count == 0 && awaiting.Count == 0)
        {
            status = LinkLinked;
            linked = counted.Sum(r => r.Amount);
            submitted = 0m;
            uncovered = order.TotalAmount - linked.Value;
            reason = $"指向本单的 {own.Count} 条收款申请（定金 / 货款申请单）全部为「已审核 + 币种与本单一致" +
                $"（{orderCurrency}）」，已关联金额按同一套权威引用规则完整计入。";
        }
        else
        {
            status = LinkPartial;
            linked = counted.Sum(r => r.Amount);
            submitted = awaiting.Sum(r => r.Amount);
            uncovered = order.TotalAmount - linked.Value;
            var parts = new List<string>();
            if (awaiting.Count > 0) parts.Add($"{awaiting.Count} 条已提交 / 待提交（未审核，已单列）");
            if (otherCurrency.Count > 0) parts.Add($"{otherCurrency.Count} 条他币种（不汇总、不做汇率换算）");
            if (unapproved.Count > 0) parts.Add($"{unapproved.Count} 条非「已审核」状态（已驳回 / 已取消 / 已完成）");
            var countedNote = counted.Count == 0
                ? "本单当前没有「已审核 + 币种一致」的收款申请，因此已关联金额为 0（有依据的 0，不是未知）"
                : $"已关联金额 = 其中「已审核 + 币种一致」的 {counted.Count} 条合计";
            reason = $"部分可归属：指向本单的收款申请中有 {string.Join("、", parts)}，不计入已关联金额；" +
                $"{countedNote}；未覆盖金额 = 订单金额 − 已关联金额，不等于未收款金额。";
        }

        if (customerLevel.Count > 0)
        {
            reason += $" 另有 {customerLevel.Count} 条客户级记录（收款单 / 装柜结算单 / 散货结算单只记录客户，" +
                "没有订单级引用）无法按既有引用归属到本单：仅列出、不计入、不汇总。";
        }
        if (customerLevelTruncated)
        {
            reason += " 客户级记录超过单次查询上限：上述客户级计数不完整（不静默截断）。";
        }

        return new SalesOrderFinanceProgress
        {
            LinkStatus = status,
            LinkReason = reason,
            LinkedAmount = linked,
            UncoveredAmount = uncovered,
            SubmittedAmount = submitted,
            OtherCurrencyRecordCount = otherCurrency.Count,
            UnapprovedRecordCount = unapproved.Count,
            UnattributedRecordCount = customerLevel.Count,
            UnattributedRecordsTruncated = customerLevelTruncated,
            OverReceived = linked.HasValue && linked.Value - order.TotalAmount > AmountTolerance,
            Records = withRecords
                ? BuildFinanceRecords(own, customerLevel, orderCurrency)
                : new List<SalesOrderFinanceRecord>(),
        };
    }

    /// <summary>
    /// 逐条列出引用记录（逐单视图用）：权威引用逐条给出单号 / 日期 / 金额 / 币种 / 状态与引用字段，
    /// 客户级记录一律 marked counted = false 并说明无法归属本单（多币种不汇总、不换算）。
    /// </summary>
    private static List<SalesOrderFinanceRecord> BuildFinanceRecords(List<FinanceApplyRow> own,
        List<CustomerLevelRow> customerLevel, string orderCurrency)
    {
        var records = new List<SalesOrderFinanceRecord>();
        foreach (var row in own.OrderBy(r => r.Date).ThenBy(r => r.No, StringComparer.Ordinal))
        {
            var sameCurrency = SameCurrency(row.Currency, orderCurrency);
            var counted = row.Counted && sameCurrency;   // 计入 = 已审核 + 币种一致
            var note = counted
                ? "权威引用（SalesOrderId 指向本单）+ 已审核 + 币种一致：计入已关联金额。"
                : !sameCurrency
                    ? $"权威引用（SalesOrderId 指向本单）但币种为 {row.Currency}，与本单 {orderCurrency} 不一致：" +
                        "仅列出、不汇总、不做汇率换算。"
                    : row.AwaitingAudit
                        ? "权威引用（SalesOrderId 指向本单）但未审核：仅列入「已提交 / 待提交」，不计入已关联金额。"
                        : "权威引用（SalesOrderId 指向本单）但单据状态不是「已审核」：仅列出、不计入。";
            records.Add(new SalesOrderFinanceRecord
            {
                Source = row.Source,
                DocumentNo = row.No,
                DocumentDate = row.Date,
                Amount = row.Amount,
                Currency = row.Currency,
                Status = row.Status.ToString(),
                ReferenceField = row.ReferenceField,
                Counted = counted,
                Note = note,
            });
        }

        foreach (var row in customerLevel)
        {
            records.Add(new SalesOrderFinanceRecord
            {
                Source = row.Source,
                DocumentNo = row.DocumentNo,
                DocumentDate = row.Date,
                Amount = row.Amount,
                Currency = row.Currency,
                Status = row.Status,
                ReferenceField = row.ReferenceField,
                Counted = false,
                Note = "只记录客户，没有订单级引用：无法归属到本单，仅列出、不计入、不汇总（多币种不做汇率换算）。",
            });
        }

        return records;
    }

    /// <summary>行出货状态</summary>
    private static string LineShipmentStatusOf(decimal ordered, decimal shipped)
    {
        if (shipped <= 0) return ShipmentNone;
        if (shipped > ordered) return ShipmentOver;
        return shipped < ordered ? ShipmentPartial : ShipmentComplete;
    }

    /// <summary>整体出货状态（按单据口径的已出货数量与订单行未出货数量判定）</summary>
    private static string OrderShipmentStatusOf(decimal ordered, decimal shipped, decimal outstanding)
    {
        if (shipped <= 0) return ShipmentNone;
        if (ordered <= 0 || shipped > ordered) return ShipmentOver;
        return outstanding > 0 ? ShipmentPartial : ShipmentComplete;
    }

    /// <summary>币种比较：只比较枚举名（忽略大小写与空白），绝不做任何汇率换算</summary>
    private static bool SameCurrency(string left, string right)
        => string.Equals((left ?? string.Empty).Trim(), (right ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase);

    private static decimal Take(Dictionary<long, decimal> pool, long productId)
        => pool.TryGetValue(productId, out var value) ? value : 0m;

    private static void PutBack(Dictionary<long, decimal> pool, long productId, decimal remainder)
    {
        if (remainder > 0) pool[productId] = remainder;
        else pool.Remove(productId);
    }

    /// <summary>订单明细行的内存投影（查询中不聚合，保证一次取全）</summary>
    private sealed record OrderDetailRow(long Id, long SalesOrderId, long ProductId, string ProductName, string Spec,
        string Unit, decimal Quantity);

    /// <summary>出库单行的内存投影（批量派生：一次取全本页出库单）</summary>
    private sealed record ShipmentDocumentRow(long Id, long OrderId, string No, DateTime Date, DocumentStatus Status,
        decimal TotalQuantity);

    /// <summary>出库明细行的内存投影</summary>
    private sealed record ShipmentDetailRow(long StockOutId, long ProductId, string ProductName, decimal Quantity);

    /// <summary>定金 / 货款申请单行的内存投影（权威引用：SalesOrderId 指向本单）</summary>
    private sealed record FinanceApplyRow(long OrderId, string Source, string No, DateTime Date, decimal Amount,
        string Currency, DocumentStatus Status, string ReferenceField)
    {
        /// <summary>是否计入已关联金额（= 已审核）</summary>
        public bool Counted => Status == DocumentStatus.Approved;

        /// <summary>是否属于已提交 / 待提交（未审核，仅单列）</summary>
        public bool AwaitingAudit => Status is DocumentStatus.Pending or DocumentStatus.Submitted;
    }

    /// <summary>客户级记录行的内存投影（只记录客户，无订单级引用，仅列出不计入）</summary>
    private sealed record CustomerLevelRow(long CustomerId, string Source, string DocumentNo, DateTime Date,
        decimal Amount, string Currency, string Status, string ReferenceField);

    /// <summary>出货派生的中间结果</summary>
    private sealed class ShipmentResult
    {
        public SalesOrderShipmentSummary Summary { get; init; } = new();
        public List<SalesOrderProgressLine> Lines { get; init; } = new();
        public List<SalesOrderUnmatchedShipment> Unmatched { get; init; } = new();
        public List<SalesOrderShipmentReference> Documents { get; init; } = new();
    }
}

