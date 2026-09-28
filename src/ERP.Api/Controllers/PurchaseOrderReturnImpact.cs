using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ERP.Api.Controllers;

/// <summary>
/// 采购退货订单影响口径常量（ERP-100）：后端派生、前端展示与测试断言共用同一套字符串口径，
/// 避免「未知」被当成 0、或「退货」被当成「减少已收」的静默钳制。
/// </summary>
public static class PurchaseOrderReturnImpactSemantics
{
    /// <summary>退货分类：有效退货（来源入库单未删除、已审核、供应商一致；计入净额）</summary>
    public const string ReturnValid = "valid";

    /// <summary>退货分类：未审核退货（来源入库单存在但退货本身未审核；不计入）</summary>
    public const string ReturnNonApproved = "non_approved";

    /// <summary>退货分类：供应商不一致（退货供应商与本单供应商不符；异常，不计入）</summary>
    public const string ReturnWrongSupplier = "wrong_supplier";

    /// <summary>退货分类：来源入库单已删除（链接悬空；异常，不计入）</summary>
    public const string ReturnMissingSource = "missing_source";

    /// <summary>退货分类：来源入库单不属于本订单（链接指向其它订单；异常，不计入）</summary>
    public const string ReturnUnmatched = "unmatched";

    /// <summary>退货分类：未关联来源入库单（无法按显式链接归属；异常，不计入）</summary>
    public const string ReturnUnlinked = "unlinked";

    /// <summary>行异常：无异常</summary>
    public const string AnomalyNone = "none";

    /// <summary>行异常：超退（有效退货超过毛收货，净额可为负，不静默钳制）</summary>
    public const string AnomalyOverReturn = "over_return";

    /// <summary>行异常：未知（命中批量派生上限，数量不完整）</summary>
    public const string AnomalyUnknown = "unknown";

    /// <summary>口径说明（界面与文档同源）</summary>
    public static readonly string RuleText =
        "本视图是采购订单的退货影响视图（只读派生）：毛收货只统计「以本单为来源、未删除、已审核」的采购入库明细数量；"
        + "有效退货只统计「来源入库单属于本单且未删除、退货已审核、供应商与本单一致」的采购退货明细数量；"
        + "净收货 = 毛收货 − 有效退货，绝不静默钳制超退（退货超过收货时净额为负并标记超退异常）；"
        + "未审核、供应商不一致、来源入库单已删除、来源不属于本单、未关联来源入库单的退货一律作为异常单列，绝不推断为对某个商品/单据的扣减；"
        + "命中批量派生上限时数量为未知（null），绝不当作 0 或正常；不改写订单任何已登记进度，不执行迁移 / 生产 SQL / 真实数据库操作 / 部署。";

    /// <summary>范围说明（界面与文档同源）</summary>
    public static readonly string ScopeNoteText =
        "本视图只统计本采购订单一张单据的派生证据；毛收货 / 有效退货 / 净收货按商品汇总，"
        + "退货证据与异常退货分别列出、互不叠加；所有集合按固定上限有界读取。";
}

/// <summary>单张采购订单的退货影响行（只读派生，不落库）</summary>
public sealed class PurchaseOrderReturnImpactProductLine
{
    public long ProductId { get; init; }
    public string ProductName { get; init; } = string.Empty;
    public string Spec { get; init; } = string.Empty;
    public string Unit { get; init; } = string.Empty;

    /// <summary>订单数量（订单外商品为 0）</summary>
    public decimal OrderedQuantity { get; init; }

    /// <summary>毛收货数量（仅已审核未删除入库；证据不完整时为 null = 未知）</summary>
    public decimal? GrossReceived { get; init; }

    /// <summary>有效退货数量（证据不完整时为 null = 未知）</summary>
    public decimal? ValidReturned { get; init; }

    /// <summary>净收货数量 = 毛收货 − 有效退货（可为负 = 超退；证据不完整时为 null = 未知）</summary>
    public decimal? NetReceived { get; init; }

    /// <summary>是否超退（有效退货 &gt; 毛收货）</summary>
    public bool OverReturned { get; init; }

    /// <summary>行异常：none / over_return / unknown</summary>
    public string Anomaly { get; init; } = PurchaseOrderReturnImpactSemantics.AnomalyNone;

    /// <summary>异常说明（超退 / 未知原因等）</summary>
    public string Note { get; init; } = string.Empty;
}

/// <summary>本单的入库单证据行（只读派生）</summary>
public sealed class PurchaseOrderReturnImpactStockIn
{
    public long StockInId { get; init; }
    public string StockInNo { get; init; } = string.Empty;
    public DateTime StockInDate { get; init; }
    public string Status { get; init; } = string.Empty;
    public decimal TotalQuantity { get; init; }

    /// <summary>是否计入毛收货（= 已审核且未删除）</summary>
    public bool Counted { get; init; }

    /// <summary>指向该入库单的有效退货数量</summary>
    public decimal ReturnedQuantity { get; init; }
}


/// <summary>退货证据行（只读派生，含分类与是否计入）</summary>
public sealed class PurchaseOrderReturnImpactReturnItem
{
    public long ReturnId { get; init; }
    public string ReturnNo { get; init; } = string.Empty;
    public DateTime ReturnDate { get; init; }
    public long? SupplierId { get; init; }
    public string SupplierName { get; init; } = string.Empty;
    public long? SourceStockInId { get; init; }
    public string SourceStockInNo { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;

    /// <summary>分类：valid / non_approved / wrong_supplier / missing_source / unmatched / unlinked</summary>
    public string Classification { get; init; } = PurchaseOrderReturnImpactSemantics.ReturnUnlinked;

    /// <summary>是否计入净额（仅 valid 为 true）</summary>
    public bool Applied { get; init; }

    /// <summary>分类说明</summary>
    public string Note { get; init; } = string.Empty;
}

/// <summary>采购订单退货影响视图（只读派生）</summary>
public sealed class PurchaseOrderReturnImpactView
{
    public long OrderId { get; init; }
    public string OrderNo { get; init; } = string.Empty;
    public long SupplierId { get; init; }
    public string SupplierName { get; init; } = string.Empty;
    public string Currency { get; init; } = string.Empty;

    /// <summary>毛收货合计（证据不完整时为 null = 未知）</summary>
    public decimal? GrossReceivedTotal { get; init; }

    /// <summary>有效退货合计（证据不完整时为 null = 未知）</summary>
    public decimal? ValidReturnedTotal { get; init; }

    /// <summary>净收货合计 = 毛收货 − 有效退货（可为负；证据不完整时为 null = 未知）</summary>
    public decimal? NetReceivedTotal { get; init; }

    /// <summary>是否命中批量派生上限（true = 数量不完整，只能按「未知」呈现）</summary>
    public bool Truncated { get; init; }

    /// <summary>口径说明（界面与文档同源）</summary>
    public string Rule { get; init; } = PurchaseOrderReturnImpactSemantics.RuleText;

    /// <summary>范围说明（界面与文档同源）</summary>
    public string ScopeNote { get; init; } = PurchaseOrderReturnImpactSemantics.ScopeNoteText;

    public List<PurchaseOrderReturnImpactProductLine> Products { get; init; } = new();
    public List<PurchaseOrderReturnImpactStockIn> Receipts { get; init; } = new();
    public List<PurchaseOrderReturnImpactReturnItem> Returns { get; init; } = new();
    public List<PurchaseOrderReturnImpactReturnItem> Exceptions { get; init; } = new();
}

/// <summary>
/// 采购订单退货影响派生（ERP-100，只读）。按显式链接链
/// 「采购退货 → 来源采购入库单（<see cref="PurchaseReturn.SourceStockInId"/>）」与
/// 「采购入库单 → 采购订单（<see cref="StockIn.PurchaseOrderId"/>）」派生毛收货 / 有效退货 / 净收货证据。
/// <para>毛收货复用 ERP-026 的同一套口径：只统计「以本单为来源、未删除、已审核」的采购入库明细数量，
/// 不引入第二套收货算法；有效退货只统计来源入库单属于本单且未删除、已审核、供应商与本单一致的退货；</para>
/// <para>未审核、供应商不一致、来源入库单已删除、来源不属于本单、未关联来源入库单的退货一律作为异常单列，
/// 绝不推断为对某个商品 / 单据的扣减；超退（退货 &gt; 收货）净额为负并标记，绝不静默钳制；</para>
/// <para>只读：不写任何表、不执行迁移 / 生产 SQL / 真实数据库操作 / 部署，不改写订单任何已登记进度。</para>
/// <para>查询有界：固定 7 次数据集访问（订单 / 入库单 / 入库明细 / 关联退货 / 异常退货 / 退货明细 / 供应商名称），
/// 无逐单、逐行查库。</para>
/// </summary>
public static class PurchaseOrderReturnImpact
{
    /// <summary>单个订单参与派生的集合上限（避免大单据无界加载）</summary>
    private const int DocumentLimit = 200;

    /// <summary>明细行的单次查询上限（命中上限即数量未知）</summary>
    private const int DetailCeiling = 2000;

    /// <summary>派生指定采购订单的只读退货影响视图；订单不存在时抛业务异常。</summary>
    public static async Task<PurchaseOrderReturnImpactView> ForOrderAsync(IErpDbContext db, long id)
    {
        ArgumentNullException.ThrowIfNull(db);

        // 1) 订单 + 未删除明细
        var order = await db.PurchaseOrders.AsNoTracking().Include(o => o.Details)
            .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
            ?? throw BusinessException.NotFound("采购订单不存在");

        // 2) 本单全部入库单（含已删除，用于识别「来源已删除」的退货）
        var stockInRows = await db.StockIns.AsNoTracking()
            .Where(s => s.PurchaseOrderId == id)
            .OrderBy(s => s.Id)
            .Take(DocumentLimit)
            .Select(s => new StockInRow(s.Id, s.StockInNo, s.StockInDate, s.Status, s.TotalQuantity, s.IsDeleted))
            .ToListAsync();
        var stockInsTruncated = stockInRows.Count == DocumentLimit;

        var allStockInIds = stockInRows.Select(s => s.Id).ToHashSet();
        var deletedStockInIds = stockInRows.Where(s => s.IsDeleted).Select(s => s.Id).ToHashSet();
        var nondeletedStockInIds = stockInRows.Where(s => !s.IsDeleted).Select(s => s.Id).ToHashSet();
        var approvedStockInIds = stockInRows.Where(s => !s.IsDeleted && s.Status == DocumentStatus.Approved)
            .Select(s => s.Id).ToHashSet();

        // 3) 入库明细（仅未删除入库单）
        var stockInDetails = nondeletedStockInIds.Count == 0
            ? new List<StockInDetailRow>()
            : await db.StockInDetails.AsNoTracking()
                .Where(d => nondeletedStockInIds.Contains(d.StockInId))
                .Take(DetailCeiling)
                .Select(d => new StockInDetailRow(d.StockInId, d.ProductId, d.ProductName, d.Quantity))
                .ToListAsync();
        var stockInDetailsTruncated = stockInDetails.Count == DetailCeiling;

        // 4) 显式关联到本单入库单的退货（含指向已删除入库单的，均计入 Returns，但仅 valid 计入净额）
        var linkedReturns = allStockInIds.Count == 0
            ? new List<PurchaseReturn>()
            : await db.PurchaseReturns.AsNoTracking()
                .Where(r => !r.IsDeleted && r.SourceStockInId != null && allStockInIds.Contains(r.SourceStockInId.Value))
                .OrderBy(r => r.Id)
                .Take(DocumentLimit)
                .ToListAsync();
        var linkedReturnsTruncated = linkedReturns.Count == DocumentLimit;

        // 5) 本单供应商下的「未关联 / 来源不属于本单」退货（异常，不计入净额）
        var exceptionReturns = await db.PurchaseReturns.AsNoTracking()
            .Where(r => !r.IsDeleted && r.SupplierId == order.SupplierId
                        && (r.SourceStockInId == null || !allStockInIds.Contains(r.SourceStockInId.Value)))
            .OrderBy(r => r.Id)
            .Take(DocumentLimit)
            .ToListAsync();
        var exceptionReturnsTruncated = exceptionReturns.Count == DocumentLimit;

        // 6) 退货明细（关联退货 + 异常退货一起，有界批量）
        var allReturnIds = linkedReturns.Select(r => r.Id).Concat(exceptionReturns.Select(r => r.Id)).ToHashSet();
        var returnDetails = allReturnIds.Count == 0
            ? new List<ReturnDetailRow>()
            : await db.PurchaseReturnDetails.AsNoTracking()
                .Where(d => allReturnIds.Contains(d.PurchaseReturnId))
                .Take(DetailCeiling)
                .Select(d => new ReturnDetailRow(d.PurchaseReturnId, d.ProductId ?? 0, d.ProductName, d.Quantity))
                .ToListAsync();
        var returnDetailsTruncated = returnDetails.Count == DetailCeiling;

        // 7) 供应商名称
        var supplierName = await db.BaseSuppliers.AsNoTracking()
            .Where(s => s.Id == order.SupplierId)
            .Select(s => s.SupplierName)
            .FirstOrDefaultAsync() ?? string.Empty;

        var truncated = stockInsTruncated || stockInDetailsTruncated
            || linkedReturnsTruncated || exceptionReturnsTruncated || returnDetailsTruncated;

        // 分类退货
        var validReturnIds = new HashSet<long>();
        var returnItems = new List<PurchaseOrderReturnImpactReturnItem>();
        foreach (var r in linkedReturns)
        {
            var classification = ClassifyLinkedReturn(r, order.SupplierId, deletedStockInIds);
            if (classification == PurchaseOrderReturnImpactSemantics.ReturnValid)
                validReturnIds.Add(r.Id);
            returnItems.Add(ToReturnItem(r, classification));
        }

        var exceptionItems = new List<PurchaseOrderReturnImpactReturnItem>();
        foreach (var r in exceptionReturns)
        {
            var classification = r.SourceStockInId == null
                ? PurchaseOrderReturnImpactSemantics.ReturnUnlinked
                : PurchaseOrderReturnImpactSemantics.ReturnUnmatched;
            exceptionItems.Add(ToReturnItem(r, classification));
        }

        // 毛收货 / 有效退货 按商品汇总
        var grossByProduct = stockInDetails
            .Where(d => approvedStockInIds.Contains(d.StockInId))
            .GroupBy(d => d.ProductId)
            .ToDictionary(g => g.Key, g => g.Sum(x => x.Quantity));

        var validReturnDetails = returnDetails.Where(d => validReturnIds.Contains(d.PurchaseReturnId)).ToList();
        var returnedByProduct = validReturnDetails
            .GroupBy(d => d.ProductId)
            .ToDictionary(g => g.Key, g => g.Sum(x => x.Quantity));

        // 有效退货数量按来源入库单汇总（用于入库单证据行的「指向退货数量」）
        var returnedByStockIn = validReturnDetails
            .Join(linkedReturns, d => d.PurchaseReturnId, r => r.Id, (d, r) => new { r.SourceStockInId, d.Quantity })
            .Where(x => x.SourceStockInId != null)
            .GroupBy(x => x.SourceStockInId!.Value)
            .ToDictionary(g => g.Key, g => g.Sum(x => x.Quantity));

        // 商品口径：订单明细商品（按首次出现顺序）+ 订单外商品（收货/退货中出现但订单没有）
        var productMeta = new Dictionary<long, (string Name, string Spec, string Unit)>();
        var orderedProductIds = new List<long>();
        foreach (var d in order.Details.Where(d => !d.IsDeleted).OrderBy(d => d.Id))
        {
            if (!productMeta.ContainsKey(d.ProductId))
            {
                productMeta[d.ProductId] = (d.ProductName, d.Spec, d.Unit);
                orderedProductIds.Add(d.ProductId);
            }
        }
        foreach (var d in stockInDetails)
            if (!productMeta.ContainsKey(d.ProductId)) productMeta[d.ProductId] = (d.ProductName, string.Empty, string.Empty);
        foreach (var d in returnDetails)
            if (!productMeta.ContainsKey(d.ProductId)) productMeta[d.ProductId] = (d.ProductName, string.Empty, string.Empty);

        var orderedQuantityByProduct = order.Details.Where(d => !d.IsDeleted)
            .GroupBy(d => d.ProductId)
            .ToDictionary(g => g.Key, g => g.Sum(x => x.Quantity));

        var productIds = orderedProductIds
            .Concat(productMeta.Keys.Where(p => !orderedProductIds.Contains(p)).OrderBy(p => p))
            .ToList();


        var lines = new List<PurchaseOrderReturnImpactProductLine>(productIds.Count);
        foreach (var pid in productIds)
        {
            var gross = grossByProduct.GetValueOrDefault(pid);
            var returned = returnedByProduct.GetValueOrDefault(pid);
            var meta = productMeta[pid];
            var overReturned = returned > gross;
            lines.Add(new PurchaseOrderReturnImpactProductLine
            {
                ProductId = pid,
                ProductName = meta.Name,
                Spec = meta.Spec,
                Unit = meta.Unit,
                OrderedQuantity = orderedQuantityByProduct.GetValueOrDefault(pid),
                GrossReceived = truncated ? null : gross,
                ValidReturned = truncated ? null : returned,
                NetReceived = truncated ? null : gross - returned,
                OverReturned = !truncated && overReturned,
                Anomaly = truncated
                    ? PurchaseOrderReturnImpactSemantics.AnomalyUnknown
                    : overReturned
                        ? PurchaseOrderReturnImpactSemantics.AnomalyOverReturn
                        : PurchaseOrderReturnImpactSemantics.AnomalyNone,
                Note = truncated
                    ? "退货影响证据超过单次派生上限，数量未知"
                    : overReturned
                        ? "有效退货超过毛收货（超退），净额为负、不静默钳制"
                        : string.Empty,
            });
        }

        var receipts = stockInRows.Where(s => !s.IsDeleted).OrderBy(s => s.Id).Select(s =>
            new PurchaseOrderReturnImpactStockIn
            {
                StockInId = s.Id,
                StockInNo = s.StockInNo,
                StockInDate = s.StockInDate,
                Status = s.Status.ToString(),
                TotalQuantity = s.TotalQuantity,
                Counted = s.Status == DocumentStatus.Approved,
                ReturnedQuantity = returnedByStockIn.GetValueOrDefault(s.Id),
            }).ToList();

        var grossTotal = grossByProduct.Values.Sum();
        var returnedTotal = returnedByProduct.Values.Sum();

        return new PurchaseOrderReturnImpactView
        {
            OrderId = order.Id,
            OrderNo = order.OrderNo,
            SupplierId = order.SupplierId,
            SupplierName = supplierName,
            Currency = order.Currency.ToString(),
            GrossReceivedTotal = truncated ? null : grossTotal,
            ValidReturnedTotal = truncated ? null : returnedTotal,
            NetReceivedTotal = truncated ? null : grossTotal - returnedTotal,
            Truncated = truncated,
            Products = lines,
            Receipts = receipts,
            Returns = returnItems,
            Exceptions = exceptionItems,
        };
    }

    /// <summary>对「来源入库单属于本单」的退货分类（来源已删除 → 悬空；未审核 / 供应商不一致 → 异常；否则有效）。</summary>
    private static string ClassifyLinkedReturn(PurchaseReturn r, long orderSupplierId, HashSet<long> deletedStockInIds)
    {
        if (r.SourceStockInId is null || deletedStockInIds.Contains(r.SourceStockInId.Value))
            return PurchaseOrderReturnImpactSemantics.ReturnMissingSource;
        if (r.Status != DocumentStatus.Approved)
            return PurchaseOrderReturnImpactSemantics.ReturnNonApproved;
        if (r.SupplierId != orderSupplierId)
            return PurchaseOrderReturnImpactSemantics.ReturnWrongSupplier;
        return PurchaseOrderReturnImpactSemantics.ReturnValid;
    }

    private static PurchaseOrderReturnImpactReturnItem ToReturnItem(PurchaseReturn r, string classification)
    {
        var applied = classification == PurchaseOrderReturnImpactSemantics.ReturnValid;
        return new PurchaseOrderReturnImpactReturnItem
        {
            ReturnId = r.Id,
            ReturnNo = r.ReturnNo,
            ReturnDate = r.ReturnDate,
            SupplierId = r.SupplierId,
            SupplierName = r.SupplierName,
            SourceStockInId = r.SourceStockInId,
            SourceStockInNo = r.SourceStockInNo,
            Status = r.Status.ToString(),
            Classification = classification,
            Applied = applied,
            Note = classification switch
            {
                PurchaseOrderReturnImpactSemantics.ReturnValid => "来源入库单未删除、退货已审核、供应商与本单一致，计入净额",
                PurchaseOrderReturnImpactSemantics.ReturnNonApproved => "退货未审核（待提交 / 已提交 / 已驳回 / 已取消），不计入净额",
                PurchaseOrderReturnImpactSemantics.ReturnWrongSupplier => "退货供应商与本单供应商不一致，作为异常单列、不计入净额",
                PurchaseOrderReturnImpactSemantics.ReturnMissingSource => "来源入库单已删除（链接悬空），作为异常单列、不计入净额",
                PurchaseOrderReturnImpactSemantics.ReturnUnmatched => "来源入库单不属于本采购订单，作为异常单列、不计入净额",
                PurchaseOrderReturnImpactSemantics.ReturnUnlinked => "未关联来源入库单，无法按显式链接归属，作为异常单列、不计入净额",
                _ => string.Empty,
            },
        };
    }

    /// <summary>入库单行的内存投影（一次取全本单入库单，含已删除）</summary>
    private sealed record StockInRow(long Id, string StockInNo, DateTime StockInDate,
        DocumentStatus Status, decimal TotalQuantity, bool IsDeleted);

    /// <summary>入库明细行的内存投影（不聚合，保证一次取全）</summary>
    private sealed record StockInDetailRow(long StockInId, long ProductId, string ProductName, decimal Quantity);

    /// <summary>退货明细行的内存投影（不聚合，保证一次取全）</summary>
    private sealed record ReturnDetailRow(long PurchaseReturnId, long ProductId, string ProductName, decimal Quantity);
}

