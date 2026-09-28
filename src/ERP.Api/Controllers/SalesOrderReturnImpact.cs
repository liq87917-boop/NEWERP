using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ERP.Api.Controllers;

/// <summary>
/// 销售退货订单影响口径常量（ERP-101）：后端派生、前端展示与测试断言共用同一套字符串口径，
/// 避免「未知」被当成 0、或「退货」被当成「减少已出货」的静默钳制。
/// </summary>
public static class SalesOrderReturnImpactSemantics
{
    /// <summary>退货分类：有效退货（来源出库单未删除、已审核、客户一致；计入净额）</summary>
    public const string ReturnValid = "valid";

    /// <summary>退货分类：未审核退货（来源出库单存在但退货本身未审核；不计入）</summary>
    public const string ReturnNonApproved = "non_approved";

    /// <summary>退货分类：客户不一致（退货客户与本单客户不符；异常，不计入）</summary>
    public const string ReturnWrongCustomer = "wrong_customer";

    /// <summary>退货分类：来源出库单已删除（链接悬空；异常，不计入）</summary>
    public const string ReturnMissingSource = "missing_source";

    /// <summary>退货分类：来源出库单不属于本订单（链接指向其它订单；异常，不计入）</summary>
    public const string ReturnUnmatched = "unmatched";

    /// <summary>退货分类：未关联来源出库单（无法按显式链接归属；异常，不计入）</summary>
    public const string ReturnUnlinked = "unlinked";

    /// <summary>行异常：无异常</summary>
    public const string AnomalyNone = "none";

    /// <summary>行异常：超退（有效退货超过毛出货，净额可为负，不静默钳制）</summary>
    public const string AnomalyOverReturn = "over_return";

    /// <summary>行异常：未知（命中批量派生上限，数量不完整）</summary>
    public const string AnomalyUnknown = "unknown";

    /// <summary>口径说明（界面与文档同源）</summary>
    public static readonly string RuleText =
        "本视图是销售订单的退货影响视图（只读派生）：毛出货只统计「以本单为来源、未删除、已审核」的销售出库明细数量；"
        + "有效退货只统计「来源出库单属于本单且未删除、退货已审核、客户与本单一致」的销售退货明细数量；"
        + "净出货 = 毛出货 − 有效退货，绝不静默钳制超退（退货超过出货时净额为负并标记超退异常）；"
        + "未审核、客户不一致、来源出库单已删除、来源不属于本单、未关联来源出库单的退货一律作为异常单列，绝不推断为对某个商品/单据的扣减；"
        + "命中批量派生上限时数量为未知（null），绝不当作 0 或正常；不改写订单任何已登记进度，不执行迁移 / 生产 SQL / 真实数据库操作 / 部署。";

    /// <summary>范围说明（界面与文档同源）</summary>
    public static readonly string ScopeNoteText =
        "本视图只统计本销售订单一张单据的派生证据；毛出货 / 有效退货 / 净出货按商品汇总，"
        + "退货证据与异常退货分别列出、互不叠加；所有集合按固定上限有界读取。";
}

/// <summary>单张销售订单的退货影响行（只读派生，不落库）</summary>
public sealed class SalesOrderReturnImpactProductLine
{
    public long ProductId { get; init; }
    public string ProductName { get; init; } = string.Empty;
    public string Spec { get; init; } = string.Empty;
    public string Unit { get; init; } = string.Empty;

    /// <summary>订单数量（订单外商品为 0）</summary>
    public decimal OrderedQuantity { get; init; }

    /// <summary>毛出货数量（仅已审核未删除出库；证据不完整时为 null = 未知）</summary>
    public decimal? GrossShipped { get; init; }

    /// <summary>有效退货数量（证据不完整时为 null = 未知）</summary>
    public decimal? ValidReturned { get; init; }

    /// <summary>净出货数量 = 毛出货 − 有效退货（可为负 = 超退；证据不完整时为 null = 未知）</summary>
    public decimal? NetShipped { get; init; }

    /// <summary>是否超退（有效退货 &gt; 毛出货）</summary>
    public bool OverReturned { get; init; }

    /// <summary>行异常：none / over_return / unknown</summary>
    public string Anomaly { get; init; } = SalesOrderReturnImpactSemantics.AnomalyNone;

    /// <summary>异常说明（超退 / 未知原因等）</summary>
    public string Note { get; init; } = string.Empty;
}

/// <summary>本单的出库单证据行（只读派生）</summary>
public sealed class SalesOrderReturnImpactStockOut
{
    public long StockOutId { get; init; }
    public string StockOutNo { get; init; } = string.Empty;
    public DateTime StockOutDate { get; init; }
    public string Status { get; init; } = string.Empty;
    public decimal TotalQuantity { get; init; }

    /// <summary>是否计入毛出货（= 已审核且未删除）</summary>
    public bool Counted { get; init; }

    /// <summary>指向该出库单的有效退货数量</summary>
    public decimal ReturnedQuantity { get; init; }
}

/// <summary>退货证据行（只读派生，含分类与是否计入）</summary>
public sealed class SalesOrderReturnImpactReturnItem
{
    public long ReturnId { get; init; }
    public string ReturnNo { get; init; } = string.Empty;
    public DateTime ReturnDate { get; init; }
    public long? CustomerId { get; init; }
    public string CustomerName { get; init; } = string.Empty;
    public long? SourceStockOutId { get; init; }
    public string SourceStockOutNo { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;

    /// <summary>分类：valid / non_approved / wrong_customer / missing_source / unmatched / unlinked</summary>
    public string Classification { get; init; } = SalesOrderReturnImpactSemantics.ReturnUnlinked;

    /// <summary>是否计入净额（仅 valid 为 true）</summary>
    public bool Applied { get; init; }

    /// <summary>分类说明</summary>
    public string Note { get; init; } = string.Empty;
}

/// <summary>销售订单退货影响视图（只读派生）</summary>
public sealed class SalesOrderReturnImpactView
{
    public long OrderId { get; init; }
    public string OrderNo { get; init; } = string.Empty;
    public long CustomerId { get; init; }
    public string CustomerName { get; init; } = string.Empty;
    public string Currency { get; init; } = string.Empty;

    /// <summary>毛出货合计（证据不完整时为 null = 未知）</summary>
    public decimal? GrossShippedTotal { get; init; }

    /// <summary>有效退货合计（证据不完整时为 null = 未知）</summary>
    public decimal? ValidReturnedTotal { get; init; }

    /// <summary>净出货合计 = 毛出货 − 有效退货（可为负；证据不完整时为 null = 未知）</summary>
    public decimal? NetShippedTotal { get; init; }

    /// <summary>是否命中批量派生上限（true = 数量不完整，只能按「未知」呈现）</summary>
    public bool Truncated { get; init; }

    /// <summary>口径说明（界面与文档同源）</summary>
    public string Rule { get; init; } = SalesOrderReturnImpactSemantics.RuleText;

    /// <summary>范围说明（界面与文档同源）</summary>
    public string ScopeNote { get; init; } = SalesOrderReturnImpactSemantics.ScopeNoteText;

    public List<SalesOrderReturnImpactProductLine> Products { get; init; } = new();
    public List<SalesOrderReturnImpactStockOut> Shipments { get; init; } = new();
    public List<SalesOrderReturnImpactReturnItem> Returns { get; init; } = new();
    public List<SalesOrderReturnImpactReturnItem> Exceptions { get; init; } = new();
}

/// <summary>
/// 销售订单退货影响派生（ERP-101，只读）。按显式链接链
/// 「销售退货 → 来源销售出库单（<see cref="SalesReturn.SourceStockOutId"/>）」与
/// 「销售出库单 → 销售订单（<see cref="StockOut.SalesOrderId"/>）」派生毛出货 / 有效退货 / 净出货证据。
/// <para>毛出货复用 ERP-032 的同一套口径：只统计「以本单为来源、未删除、已审核」的销售出库明细数量，
/// 不引入第二套出货算法；有效退货只统计来源出库单属于本单且未删除、已审核、客户与本单一致的退货；</para>
/// <para>未审核、客户不一致、来源出库单已删除、来源不属于本单、未关联来源出库单的退货一律作为异常单列，
/// 绝不推断为对某个商品 / 单据的扣减；超退（退货 &gt; 出货）净额为负并标记，绝不静默钳制；</para>
/// <para>只读：不写任何表、不执行迁移 / 生产 SQL / 真实数据库操作 / 部署，不改写订单任何已登记进度。</para>
/// <para>查询有界：固定 7 次数据集访问（订单 / 出库单 / 出库明细 / 关联退货 / 异常退货 / 退货明细 / 客户名称），
/// 无逐单、逐行查库。</para>
/// </summary>
public static class SalesOrderReturnImpact
{
    /// <summary>单个订单参与派生的集合上限（避免大单据无界加载）</summary>
    private const int DocumentLimit = 200;

    /// <summary>明细行的单次查询上限（命中上限即数量未知）</summary>
    private const int DetailCeiling = 2000;

    /// <summary>派生指定销售订单的只读退货影响视图；订单不存在时抛业务异常。</summary>
    public static async Task<SalesOrderReturnImpactView> ForOrderAsync(IErpDbContext db, long id)
    {
        ArgumentNullException.ThrowIfNull(db);

        // 1) 订单 + 未删除明细
        var order = await db.SalesOrders.AsNoTracking().Include(o => o.Details)
            .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
            ?? throw BusinessException.NotFound("销售订单不存在");

        // 2) 本单全部出库单（含已删除，用于识别「来源已删除」的退货）
        var stockOutRows = await db.StockOuts.AsNoTracking()
            .Where(s => s.SalesOrderId == id)
            .OrderBy(s => s.Id)
            .Take(DocumentLimit)
            .Select(s => new StockOutRow(s.Id, s.StockOutNo, s.StockOutDate, s.Status, s.TotalQuantity, s.IsDeleted))
            .ToListAsync();
        var stockOutsTruncated = stockOutRows.Count == DocumentLimit;

        var allStockOutIds = stockOutRows.Select(s => s.Id).ToHashSet();
        var deletedStockOutIds = stockOutRows.Where(s => s.IsDeleted).Select(s => s.Id).ToHashSet();
        var nondeletedStockOutIds = stockOutRows.Where(s => !s.IsDeleted).Select(s => s.Id).ToHashSet();
        var approvedStockOutIds = stockOutRows.Where(s => !s.IsDeleted && s.Status == DocumentStatus.Approved)
            .Select(s => s.Id).ToHashSet();

        // 3) 出库明细（仅未删除出库单）
        var stockOutDetails = nondeletedStockOutIds.Count == 0
            ? new List<StockOutDetailRow>()
            : await db.StockOutDetails.AsNoTracking()
                .Where(d => nondeletedStockOutIds.Contains(d.StockOutId))
                .Take(DetailCeiling)
                .Select(d => new StockOutDetailRow(d.StockOutId, d.ProductId, d.ProductName, d.Quantity))
                .ToListAsync();
        var stockOutDetailsTruncated = stockOutDetails.Count == DetailCeiling;

        // 4) 显式关联到本单出库单的退货（含指向已删除出库单的，均计入 Returns，但仅 valid 计入净额）
        var linkedReturns = allStockOutIds.Count == 0
            ? new List<SalesReturn>()
            : await db.SalesReturns.AsNoTracking()
                .Where(r => !r.IsDeleted && r.SourceStockOutId != null && allStockOutIds.Contains(r.SourceStockOutId.Value))
                .OrderBy(r => r.Id)
                .Take(DocumentLimit)
                .ToListAsync();
        var linkedReturnsTruncated = linkedReturns.Count == DocumentLimit;

        // 5) 本单客户下的「未关联 / 来源不属于本单」退货（异常，不计入净额）
        var exceptionReturns = await db.SalesReturns.AsNoTracking()
            .Where(r => !r.IsDeleted && r.CustomerId == order.CustomerId
                        && (r.SourceStockOutId == null || !allStockOutIds.Contains(r.SourceStockOutId.Value)))
            .OrderBy(r => r.Id)
            .Take(DocumentLimit)
            .ToListAsync();
        var exceptionReturnsTruncated = exceptionReturns.Count == DocumentLimit;

        // 6) 退货明细（关联退货 + 异常退货一起，有界批量）
        var allReturnIds = linkedReturns.Select(r => r.Id).Concat(exceptionReturns.Select(r => r.Id)).ToHashSet();
        var returnDetails = allReturnIds.Count == 0
            ? new List<ReturnDetailRow>()
            : await db.SalesReturnDetails.AsNoTracking()
                .Where(d => allReturnIds.Contains(d.SalesReturnId))
                .Take(DetailCeiling)
                .Select(d => new ReturnDetailRow(d.SalesReturnId, d.ProductId ?? 0, d.ProductName, d.Quantity))
                .ToListAsync();
        var returnDetailsTruncated = returnDetails.Count == DetailCeiling;

        // 7) 客户名称
        var customerName = await db.BaseCustomers.AsNoTracking()
            .Where(c => c.Id == order.CustomerId)
            .Select(c => c.CustomerName)
            .FirstOrDefaultAsync() ?? string.Empty;

        var truncated = stockOutsTruncated || stockOutDetailsTruncated
            || linkedReturnsTruncated || exceptionReturnsTruncated || returnDetailsTruncated;

        // 分类退货
        var validReturnIds = new HashSet<long>();
        var returnItems = new List<SalesOrderReturnImpactReturnItem>();
        foreach (var r in linkedReturns)
        {
            var classification = ClassifyLinkedReturn(r, order.CustomerId, deletedStockOutIds);
            if (classification == SalesOrderReturnImpactSemantics.ReturnValid)
                validReturnIds.Add(r.Id);
            returnItems.Add(ToReturnItem(r, classification));
        }

        var exceptionItems = new List<SalesOrderReturnImpactReturnItem>();
        foreach (var r in exceptionReturns)
        {
            var classification = r.SourceStockOutId == null
                ? SalesOrderReturnImpactSemantics.ReturnUnlinked
                : SalesOrderReturnImpactSemantics.ReturnUnmatched;
            exceptionItems.Add(ToReturnItem(r, classification));
        }

        // 毛出货 / 有效退货 按商品汇总
        var grossByProduct = stockOutDetails
            .Where(d => approvedStockOutIds.Contains(d.StockOutId))
            .GroupBy(d => d.ProductId)
            .ToDictionary(g => g.Key, g => g.Sum(x => x.Quantity));

        var validReturnDetails = returnDetails.Where(d => validReturnIds.Contains(d.SalesReturnId)).ToList();
        var returnedByProduct = validReturnDetails
            .GroupBy(d => d.ProductId)
            .ToDictionary(g => g.Key, g => g.Sum(x => x.Quantity));

        // 有效退货数量按来源出库单汇总（用于出库单证据行的「指向退货数量」）
        var returnedByStockOut = validReturnDetails
            .Join(linkedReturns, d => d.SalesReturnId, r => r.Id, (d, r) => new { r.SourceStockOutId, d.Quantity })
            .Where(x => x.SourceStockOutId != null)
            .GroupBy(x => x.SourceStockOutId!.Value)
            .ToDictionary(g => g.Key, g => g.Sum(x => x.Quantity));

        // 商品口径：订单明细商品（按首次出现顺序）+ 订单外商品（出货/退货中出现但订单没有）
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
        foreach (var d in stockOutDetails)
            if (!productMeta.ContainsKey(d.ProductId)) productMeta[d.ProductId] = (d.ProductName, string.Empty, string.Empty);
        foreach (var d in returnDetails)
            if (!productMeta.ContainsKey(d.ProductId)) productMeta[d.ProductId] = (d.ProductName, string.Empty, string.Empty);

        var orderedQuantityByProduct = order.Details.Where(d => !d.IsDeleted)
            .GroupBy(d => d.ProductId)
            .ToDictionary(g => g.Key, g => g.Sum(x => x.Quantity));

        var productIds = orderedProductIds
            .Concat(productMeta.Keys.Where(p => !orderedProductIds.Contains(p)).OrderBy(p => p))
            .ToList();

        var lines = new List<SalesOrderReturnImpactProductLine>(productIds.Count);
        foreach (var pid in productIds)
        {
            var gross = grossByProduct.GetValueOrDefault(pid);
            var returned = returnedByProduct.GetValueOrDefault(pid);
            var meta = productMeta[pid];
            var overReturned = returned > gross;
            lines.Add(new SalesOrderReturnImpactProductLine
            {
                ProductId = pid,
                ProductName = meta.Name,
                Spec = meta.Spec,
                Unit = meta.Unit,
                OrderedQuantity = orderedQuantityByProduct.GetValueOrDefault(pid),
                GrossShipped = truncated ? null : gross,
                ValidReturned = truncated ? null : returned,
                NetShipped = truncated ? null : gross - returned,
                OverReturned = !truncated && overReturned,
                Anomaly = truncated
                    ? SalesOrderReturnImpactSemantics.AnomalyUnknown
                    : overReturned
                        ? SalesOrderReturnImpactSemantics.AnomalyOverReturn
                        : SalesOrderReturnImpactSemantics.AnomalyNone,
                Note = truncated
                    ? "退货影响证据超过单次派生上限，数量未知"
                    : overReturned
                        ? "有效退货超过毛出货（超退），净额为负、不静默钳制"
                        : string.Empty,
            });
        }

        var shipments = stockOutRows.Where(s => !s.IsDeleted).OrderBy(s => s.Id).Select(s =>
            new SalesOrderReturnImpactStockOut
            {
                StockOutId = s.Id,
                StockOutNo = s.StockOutNo,
                StockOutDate = s.StockOutDate,
                Status = s.Status.ToString(),
                TotalQuantity = s.TotalQuantity,
                Counted = s.Status == DocumentStatus.Approved,
                ReturnedQuantity = returnedByStockOut.GetValueOrDefault(s.Id),
            }).ToList();

        var grossTotal = grossByProduct.Values.Sum();
        var returnedTotal = returnedByProduct.Values.Sum();

        return new SalesOrderReturnImpactView
        {
            OrderId = order.Id,
            OrderNo = order.OrderNo,
            CustomerId = order.CustomerId,
            CustomerName = customerName,
            Currency = order.Currency.ToString(),
            GrossShippedTotal = truncated ? null : grossTotal,
            ValidReturnedTotal = truncated ? null : returnedTotal,
            NetShippedTotal = truncated ? null : grossTotal - returnedTotal,
            Truncated = truncated,
            Products = lines,
            Shipments = shipments,
            Returns = returnItems,
            Exceptions = exceptionItems,
        };
    }

    /// <summary>对「来源出库单属于本单」的退货分类（来源已删除 → 悬空；未审核 / 客户不一致 → 异常；否则有效）。</summary>
    private static string ClassifyLinkedReturn(SalesReturn r, long orderCustomerId, HashSet<long> deletedStockOutIds)
    {
        if (r.SourceStockOutId is null || deletedStockOutIds.Contains(r.SourceStockOutId.Value))
            return SalesOrderReturnImpactSemantics.ReturnMissingSource;
        if (r.Status != DocumentStatus.Approved)
            return SalesOrderReturnImpactSemantics.ReturnNonApproved;
        if ((r.CustomerId ?? 0) != orderCustomerId)
            return SalesOrderReturnImpactSemantics.ReturnWrongCustomer;
        return SalesOrderReturnImpactSemantics.ReturnValid;
    }

    private static SalesOrderReturnImpactReturnItem ToReturnItem(SalesReturn r, string classification)
    {
        var applied = classification == SalesOrderReturnImpactSemantics.ReturnValid;
        return new SalesOrderReturnImpactReturnItem
        {
            ReturnId = r.Id,
            ReturnNo = r.ReturnNo,
            ReturnDate = r.ReturnDate,
            CustomerId = r.CustomerId,
            CustomerName = r.CustomerName,
            SourceStockOutId = r.SourceStockOutId,
            SourceStockOutNo = r.SourceStockOutNo,
            Status = r.Status.ToString(),
            Classification = classification,
            Applied = applied,
            Note = classification switch
            {
                SalesOrderReturnImpactSemantics.ReturnValid => "来源出库单未删除、退货已审核、客户与本单一致，计入净额",
                SalesOrderReturnImpactSemantics.ReturnNonApproved => "退货未审核（待提交 / 已提交 / 已驳回 / 已取消），不计入净额",
                SalesOrderReturnImpactSemantics.ReturnWrongCustomer => "退货客户与本单客户不一致，作为异常单列、不计入净额",
                SalesOrderReturnImpactSemantics.ReturnMissingSource => "来源出库单已删除（链接悬空），作为异常单列、不计入净额",
                SalesOrderReturnImpactSemantics.ReturnUnmatched => "来源出库单不属于本销售订单，作为异常单列、不计入净额",
                SalesOrderReturnImpactSemantics.ReturnUnlinked => "未关联来源出库单，无法按显式链接归属，作为异常单列、不计入净额",
                _ => string.Empty,
            },
        };
    }

    /// <summary>出库单行的内存投影（一次取全本单出库单，含已删除）</summary>
    private sealed record StockOutRow(long Id, string StockOutNo, DateTime StockOutDate,
        DocumentStatus Status, decimal TotalQuantity, bool IsDeleted);

    /// <summary>出库明细行的内存投影（不聚合，保证一次取全）</summary>
    private sealed record StockOutDetailRow(long StockOutId, long ProductId, string ProductName, decimal Quantity);

    /// <summary>退货明细行的内存投影（不聚合，保证一次取全）</summary>
    private sealed record ReturnDetailRow(long SalesReturnId, long ProductId, string ProductName, decimal Quantity);
}
