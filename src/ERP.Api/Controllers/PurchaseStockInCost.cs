using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ERP.Api.Controllers;

/// <summary>
/// 采购入库成本来源口径常量与判定助手（ERP-033）。
/// </summary>
/// <remarks>
/// 本类与 <see cref="PurchaseStockInCostSource"/> 一起构成「采购订单 → 库存基础单位成本」的唯一口径，
/// 与文档 <c>docs/库存单据与库存成本说明.md</c> §11 同源；任何一处语义不明确都必须回退既有的
/// 移动加权平均兜底（<see cref="SourceWeightedAverageFallback"/>），不臆造成本单价。
/// </remarks>
public static class PurchaseStockInCost
{
    /// <summary>成本来源：采购订单明细单价（同币种，直接作为基础单位成本）</summary>
    public const string SourceOrderPrice = "order_price";

    /// <summary>成本来源：采购订单明细单价（外币，按采购订单持久化汇率换算为本币基础单位成本）</summary>
    public const string SourceOrderPriceConverted = "order_price_converted";

    /// <summary>成本来源：既有兜底口径（当前移动加权平均成本，首次入库为 0）</summary>
    public const string SourceWeightedAverageFallback = "weighted_average_fallback";

    /// <summary>回退原因：入库单没有持久化采购订单链接</summary>
    public const string ReasonNoLinkage = "no_purchase_order_linkage";

    /// <summary>回退原因：采购订单不存在 / 已删除 / 未审核（价格不构成权威依据）</summary>
    public const string ReasonOrderNotAuthoritative = "purchase_order_not_authoritative";

    /// <summary>回退原因：商品资料缺失，无法确认基础单位与装箱数</summary>
    public const string ReasonProductMissing = "product_metadata_missing";

    /// <summary>回退原因：采购订单里没有该商品的明细行（缺证据）</summary>
    public const string ReasonLineMissing = "order_line_missing";

    /// <summary>回退原因：采购订单里同商品存在多行明细（语义不唯一，不任选一行）</summary>
    public const string ReasonLineAmbiguous = "order_line_ambiguous";

    /// <summary>回退原因：订单行单位既不是基础单位也不是装箱单位，或装箱数无效</summary>
    public const string ReasonUnitIncompatible = "unit_incompatible";

    /// <summary>回退原因：外币订单没有可用的权威汇率换算（不臆造汇率）</summary>
    public const string ReasonCurrencyUnavailable = "currency_conversion_unavailable";

    /// <summary>回退原因：订单行为空单价 / 零单价 / 负数单价，不构成可用成本</summary>
    public const string ReasonPriceUnavailable = "price_unavailable";

    /// <summary>
    /// 汇率未配置占位值：<c>deploy/init*.sql</c> 的 <c>SysParameters.ExchangeRate</c> 与
    /// <c>BillProcController.LoadCurrencyDefaultsAsync</c> 都以 1 作为「未配置」默认值，
    /// 因此非人民币订单只有在订单持久化了非 1 的正汇率时，才视为存在权威换算。
    /// </summary>
    private const decimal PlaceholderExchangeRate = 1m;

    /// <summary>库存计价币种：库存行与库存流水都没有币种列，历来以人民币（本币）计价</summary>
    private const Currency InventoryCostCurrency = Currency.CNY;

    /// <summary>是否存在「外币订单单价 → 库存计价币种」的既有权威换算</summary>
    public static bool IsAuthoritativeExchangeRate(Currency currency, decimal exchangeRate)
        => currency == InventoryCostCurrency
           || (exchangeRate > 0m && exchangeRate != PlaceholderExchangeRate);

    /// <summary>回退原因的中文说明（用于写入库存流水备注，界面 / 报表可直接展示）</summary>
    public static string ReasonText(string reason) => reason switch
    {
        ReasonNoLinkage => "入库单未链接采购订单",
        ReasonOrderNotAuthoritative => "采购订单不存在或未审核",
        ReasonProductMissing => "商品资料缺失，无法确认基础单位",
        ReasonLineMissing => "采购订单无该商品明细行",
        ReasonLineAmbiguous => "采购订单同商品多行明细，价格不唯一",
        ReasonUnitIncompatible => "订单行单位无法按装箱数折算到基础单位",
        ReasonCurrencyUnavailable => "外币订单无可用的权威汇率换算",
        ReasonPriceUnavailable => "订单行单价不可用（空 / 零 / 负数）",
        _ => "订单行价格不可作为成本依据",
    };
}


/// <summary>
/// 单个采购入库明细行的成本来源判定结果（ERP-033，纯内存判定，不落库、不改单据）。
/// </summary>
public sealed class PurchaseStockInCostResolution
{
    private PurchaseStockInCostResolution()
    {
    }

    /// <summary>基础单位成本单价（大于 0 时作为入库成本；0 = 交给 InventoryService 既有加权平均兜底）</summary>
    public decimal UnitCost { get; private init; }

    /// <summary>成本来源（<see cref="PurchaseStockInCost"/> 的 Source 常量之一）</summary>
    public string Source { get; private init; } = PurchaseStockInCost.SourceWeightedAverageFallback;

    /// <summary>回退原因（仅当 <see cref="Source"/> 为兜底时有值，取 <see cref="PurchaseStockInCost"/> 的 Reason 常量之一）</summary>
    public string Reason { get; private init; } = string.Empty;

    /// <summary>是否已按商品装箱数把订单价从包装单位折算到基础单位</summary>
    public bool PackagePriceNormalized { get; private init; }

    /// <summary>是否已按采购订单持久化汇率把外币单价换算为本币</summary>
    public bool CurrencyConverted { get; private init; }

    /// <summary>是否采用了采购订单单价（false = 既有加权平均兜底）</summary>
    public bool UsedOrderPrice => Source != PurchaseStockInCost.SourceWeightedAverageFallback;

    /// <summary>写入库存流水备注的成本来源文案（含兜底原因，便于按流水反查成本依据）</summary>
    public string RemarkText
    {
        get
        {
            if (!UsedOrderPrice)
                return string.IsNullOrEmpty(Reason)
                    ? "既有移动加权平均兜底"
                    : $"既有移动加权平均兜底（{PurchaseStockInCost.ReasonText(Reason)}）";

            var notes = new List<string>();
            if (PackagePriceNormalized) notes.Add("按装箱数折算为基础单位");
            if (CurrencyConverted) notes.Add("按订单汇率换算");
            return notes.Count == 0 ? "采购订单单价" : $"采购订单单价（{string.Join("，", notes)}）";
        }
    }

    internal static PurchaseStockInCostResolution OrderPrice(decimal unitCost, bool packagePriceNormalized,
        bool currencyConverted) => new()
    {
        UnitCost = unitCost,
        Source = currencyConverted
            ? PurchaseStockInCost.SourceOrderPriceConverted
            : PurchaseStockInCost.SourceOrderPrice,
        PackagePriceNormalized = packagePriceNormalized,
        CurrencyConverted = currencyConverted,
    };

    internal static PurchaseStockInCostResolution Fallback(string reason) => new()
    {
        Source = PurchaseStockInCost.SourceWeightedAverageFallback,
        Reason = reason,
    };
}

/// <summary>
/// 采购入库成本来源解析（ERP-033）：把「已审核采购订单的唯一兼容明细行单价」折算成库存基础单位成本
/// 带入库存流水；任何一处语义不明确时一律回退既有的移动加权平均兜底。
/// </summary>
/// <remarks>
/// 判定口径（与文档 <c>docs/库存单据与库存成本说明.md</c> §11 同源）：
/// <list type="number">
/// <item>权威链接只有一条：入库单自身持久化的 <see cref="StockIn.PurchaseOrderId"/>；不按商品名 / 供应商 / 备注文本猜测。</item>
/// <item>订单必须存在、未删除且已审核（未审核订单的价格不构成成本依据）。</item>
/// <item>该订单在本商品上必须有且仅有一行明细行（零行 = 缺证据、多行 = 语义不唯一，二者均回退）。</item>
/// <item>订单行单位必须能确定折算到商品基础单位：等于基础单位直接使用；等于装箱单位时按商品
/// <c>UnitsPerPackage</c> 折算（装箱数无效则回退）；其他单位回退。明细数量本身已是基础单位口径（ERP-023），
/// 本解析只折算单价，不改动任何已落库数量。</item>
/// <item>币种：库存行与流水没有币种列（历来以人民币计价），人民币订单直接使用单价；外币订单只有在订单持久化了
/// 非占位汇率时才按该汇率换算，否则回退，绝不臆造汇率。</item>
/// <item>折算后的基础单位成本必须大于 0（保留 6 位小数，与 <see cref="InventoryService.RoundCost"/> 同一取整口径），
/// 否则回退，不把零 / 负单价当成真实成本。</item>
/// </list>
/// </remarks>
public sealed class PurchaseStockInCostSource
{
    private readonly IReadOnlyList<PurchaseOrderDetail> _orderLines;
    private readonly IReadOnlyDictionary<long, BaseProduct> _products;
    private readonly Currency _currency;
    private readonly decimal _exchangeRate;
    private readonly string _orderReason;

    private PurchaseStockInCostSource(IReadOnlyDictionary<long, BaseProduct> products,
        IReadOnlyList<PurchaseOrderDetail> orderLines, Currency currency, decimal exchangeRate,
        long? purchaseOrderId, string purchaseOrderNo, string orderReason)
    {
        _products = products;
        _orderLines = orderLines;
        _currency = currency;
        _exchangeRate = exchangeRate;
        _orderReason = orderReason;
        HasAuthoritativeOrder = orderReason.Length == 0;
        PurchaseOrderId = purchaseOrderId;
        PurchaseOrderNo = purchaseOrderNo;
    }

    /// <summary>是否拿到了可用的权威采购订单（订单存在、未删除、已审核）</summary>
    public bool HasAuthoritativeOrder { get; }

    /// <summary>入库单持久化的采购订单 Id（无链接时为 null）</summary>
    public long? PurchaseOrderId { get; }

    /// <summary>采购订单号（订单不存在 / 未审核时为空串）</summary>
    public string PurchaseOrderNo { get; } = string.Empty;

    /// <summary>无法使用订单价格的原因（仅当 <see cref="HasAuthoritativeOrder"/> 为 false 时有值）</summary>
    public string OrderReason => _orderReason;

    /// <summary>
    /// 一次加载判定所需的最小证据集：本单涉及的商品元数据 + 链接的采购订单（含明细）。
    /// 固定 2 次查询（商品资料 + 订单），与明细行数无关，不做逐行查库。
    /// </summary>
    public static async Task<PurchaseStockInCostSource> LoadAsync(IErpDbContext db, long? purchaseOrderId,
        IEnumerable<long?> productIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(productIds);

        var wanted = productIds.Where(id => id is > 0).Select(id => id!.Value).Distinct().ToList();
        var products = wanted.Count == 0
            ? new Dictionary<long, BaseProduct>()
            : await db.BaseProducts.AsNoTracking()
                .Where(p => wanted.Contains(p.Id) && !p.IsDeleted)
                .ToDictionaryAsync(p => p.Id, cancellationToken);

        if (purchaseOrderId is not > 0)
            return new PurchaseStockInCostSource(products, Array.Empty<PurchaseOrderDetail>(), Currency.CNY, 0m,
                null, string.Empty, PurchaseStockInCost.ReasonNoLinkage);

        var order = await db.PurchaseOrders.AsNoTracking().Include(o => o.Details)
            .FirstOrDefaultAsync(o => o.Id == purchaseOrderId.Value && !o.IsDeleted, cancellationToken);
        if (order is null)
            return new PurchaseStockInCostSource(products, Array.Empty<PurchaseOrderDetail>(), Currency.CNY, 0m,
                purchaseOrderId, string.Empty, PurchaseStockInCost.ReasonOrderNotAuthoritative);

        if (order.Status != DocumentStatus.Approved)
            return new PurchaseStockInCostSource(products, Array.Empty<PurchaseOrderDetail>(), order.Currency,
                order.ExchangeRate, order.Id, order.OrderNo, PurchaseStockInCost.ReasonOrderNotAuthoritative);

        return new PurchaseStockInCostSource(products,
            order.Details.Where(l => !l.IsDeleted).ToList(), order.Currency, order.ExchangeRate,
            order.Id, order.OrderNo, string.Empty);
    }

    /// <summary>判定一行入库明细的成本来源（纯内存；不查库、不写库）</summary>
    public PurchaseStockInCostResolution Resolve(long? productId)
    {
        if (!HasAuthoritativeOrder) return PurchaseStockInCostResolution.Fallback(_orderReason);
        if (productId is not > 0) return PurchaseStockInCostResolution.Fallback(PurchaseStockInCost.ReasonLineMissing);
        if (!_products.TryGetValue(productId.Value, out var product))
            return PurchaseStockInCostResolution.Fallback(PurchaseStockInCost.ReasonProductMissing);

        var matches = _orderLines.Where(l => l.ProductId == productId.Value).ToList();
        if (matches.Count == 0)
            return PurchaseStockInCostResolution.Fallback(PurchaseStockInCost.ReasonLineMissing);
        if (matches.Count > 1)
            return PurchaseStockInCostResolution.Fallback(PurchaseStockInCost.ReasonLineAmbiguous);

        var line = matches[0];
        if (!TryBaseUnitPrice(product, line, out var basePrice, out var packagePriceNormalized))
            return PurchaseStockInCostResolution.Fallback(PurchaseStockInCost.ReasonUnitIncompatible);

        var currencyConverted = false;
        if (_currency != Currency.CNY)
        {
            if (!PurchaseStockInCost.IsAuthoritativeExchangeRate(_currency, _exchangeRate))
                return PurchaseStockInCostResolution.Fallback(PurchaseStockInCost.ReasonCurrencyUnavailable);
            basePrice *= _exchangeRate;
            currencyConverted = true;
        }

        var unitCost = InventoryService.RoundCost(basePrice);
        if (unitCost <= 0m)
            return PurchaseStockInCostResolution.Fallback(PurchaseStockInCost.ReasonPriceUnavailable);

        return PurchaseStockInCostResolution.OrderPrice(unitCost, packagePriceNormalized, currencyConverted);
    }

    /// <summary>
    /// 订单行单价 → 基础单位单价：等于基础单位直接使用；等于装箱单位时按 UnitsPerPackage 折算；
    /// 其他 / 空单位或无效装箱数返回 false（由调用方回退）。
    /// </summary>
    private static bool TryBaseUnitPrice(BaseProduct product, PurchaseOrderDetail line, out decimal basePrice,
        out bool packagePriceNormalized)
    {
        basePrice = 0m;
        packagePriceNormalized = false;

        var lineUnit = (line.Unit ?? string.Empty).Trim();
        var baseUnit = (product.Unit ?? string.Empty).Trim();
        var packageUnit = (product.PackageUnit ?? string.Empty).Trim();

        if (baseUnit.Length > 0 && lineUnit.Equals(baseUnit, StringComparison.OrdinalIgnoreCase))
        {
            basePrice = line.UnitPrice;
            return true;
        }

        if (packageUnit.Length == 0 || !lineUnit.Equals(packageUnit, StringComparison.OrdinalIgnoreCase))
            return false;

        if (product.UnitsPerPackage <= 0) return false;
        basePrice = line.UnitPrice / product.UnitsPerPackage;
        packagePriceNormalized = true;
        return true;
    }
}
