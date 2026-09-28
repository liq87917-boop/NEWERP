using ERP.Domain.Entities;
using ERP.Domain.Enums;

namespace ERP.Application.Services;

/// <summary>
/// 供应商比价 → 采购订单价格差异的纯规则（ERP-105）。
/// 用途：只读核对比价行与已生成采购订单的价格差异，仅在相同比价口径
/// （商品 + 规格 + 单位 + 币种 + 是否含税）完全一致时计算单价差与金额差；
/// 歧义 / 口径不一致 / 陈旧链接 / 未链接一律返回未解决原因，绝不跨口径比较、
/// 绝不做汇率换算、绝不合并不同币种金额。
/// <para>边界（重要）：本规则只做<strong>判定与文案</strong>，不读写数据库、不访问存储、
/// 不发起网络请求，也不改写报价 / 采购订单 / 价格 / 审批。</para>
/// </summary>
public static class PurchaseQuoteOrderPriceVarianceRules
{
    /// <summary>口径与边界说明（界面与文档同源）</summary>
    public const string RuleText =
        "本视图是供应商比价与已生成采购订单的只读价格差异核对（不写库、不重定价、不改审批）：" +
        "仅统计「已转采购订单」的比价行，按报价日期 + 行 Id 稳定排序并分页；" +
        "通过比价行 RefOrderNo 持久化链接与采购订单备注来源标记定位订单，链接缺失 / 订单删除 / 单号歧义 / 标记缺失均显式标注为未解决；" +
        "仅当「商品 + 规格 + 单位 + 币种 + 含税」完全一致时计算单价差（采购单价 − 报价单价）与金额差（采购金额 − 报价总额）；" +
        "口径不一致、明细重复歧义或明细缺失一律显示未解决原因，绝不跨口径比较、绝不做汇率换算、绝不合并不同币种金额。";

    /// <summary>
    /// 对一张已确认链接的采购订单解析该比价行对应的明细并计算价格差异。
    /// <paramref name="sourceMarker"/> 为比价行写入采购订单备注的来源标记（由调用方按转换口径生成，避免本规则反向依赖 API 层）。
    /// </summary>
    public static PurchaseQuoteOrderPriceVarianceResolution Resolve(PurchaseQuote quote, PurchaseOrder order,
        IReadOnlyList<PurchaseOrderDetail> details, string sourceMarker)
    {
        ArgumentNullException.ThrowIfNull(quote);
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(details);
        var marker = sourceMarker ?? string.Empty;

        // ① 陈旧链接：订单备注必须包含来源比价标记（与转换留痕同源），否则视为陈旧链接
        if (marker.Length == 0 || !order.Remark.Contains(marker, StringComparison.Ordinal))
            return Unresolved("陈旧链接：采购订单备注未包含来源比价标记，无法确认该单由本比价行生成");

        // ② 表头口径：币种（文本 → 枚举名，不区分大小写）与是否含税
        var quoteCurrency = NormalizeCurrency(quote.Currency);
        var orderCurrency = order.Currency.ToString();
        if (!string.Equals(quoteCurrency, orderCurrency, StringComparison.OrdinalIgnoreCase))
            return Unresolved($"币种不一致：报价 {Display(quote.Currency)}，采购订单 {orderCurrency}");
        if (quote.TaxIncluded != order.TaxIncluded)
            return Unresolved($"含税口径不一致：报价{(quote.TaxIncluded ? "含税" : "不含税")}，采购订单{(order.TaxIncluded ? "含税" : "不含税")}");

        // ③ 明细定位：优先按来源标记（批次转换逐行留痕），其次按「商品 + 规格 + 单位」口径
        var active = details.Where(d => !d.IsDeleted).ToList();
        var markerMatches = active.Where(d => d.Remark.Contains(marker, StringComparison.Ordinal)).ToList();

        PurchaseOrderDetail? matched;
        if (markerMatches.Count == 1)
        {
            matched = markerMatches[0];
        }
        else if (markerMatches.Count > 1)
        {
            return Unresolved("明细歧义：采购订单内多条明细都带该比价行来源标记，无法唯一定位");
        }
        else
        {
            var basisMatches = active.Where(d => DetailBasisMatches(quote, d)).ToList();
            if (basisMatches.Count == 0)
                return Unresolved("无匹配明细：商品 / 规格 / 单位与采购订单明细不一致");
            if (basisMatches.Count > 1)
                return Unresolved("明细歧义：采购订单内存在多条相同商品 / 规格 / 单位明细，无法唯一定位");
            matched = basisMatches[0];
        }

        // ④ 即使来源标记已定位，也复核口径（防止订单明细事后被编辑成不同口径）
        if (!DetailBasisMatches(quote, matched))
            return Unresolved("明细口径不一致：商品 / 规格 / 单位与报价不一致");

        return new PurchaseQuoteOrderPriceVarianceResolution
        {
            Resolved = true,
            Detail = matched,
            UnitPriceDelta = matched.UnitPrice - quote.QuotePrice,
            AmountDelta = matched.Amount - quote.TotalAmount,
            Reason = string.Empty
        };
    }

    /// <summary>明细口径：商品 Id（未引用按 0）+ 规格 + 单位（去首尾空白、区分大小写）</summary>
    public static bool DetailBasisMatches(PurchaseQuote quote, PurchaseOrderDetail detail)
        => (quote.ProductId ?? 0) == detail.ProductId
            && string.Equals(Normalize(quote.Spec), Normalize(detail.Spec), StringComparison.Ordinal)
            && string.Equals(Normalize(quote.Unit), Normalize(detail.Unit), StringComparison.Ordinal);

    /// <summary>表头口径：币种（文本 → 枚举名）+ 是否含税</summary>
    public static bool HeaderBasisMatches(PurchaseQuote quote, PurchaseOrder order)
        => string.Equals(NormalizeCurrency(quote.Currency), order.Currency.ToString(), StringComparison.OrdinalIgnoreCase)
            && quote.TaxIncluded == order.TaxIncluded;

    /// <summary>比价行币种文本 → 枚举名（无法识别按原文大写；未填写按 CNY），用于与采购订单币种枚举比对</summary>
    public static string NormalizeCurrency(string? currency)
    {
        var text = (currency ?? string.Empty).Trim();
        if (text.Length == 0) return nameof(Currency.CNY);
        return Enum.TryParse<Currency>(text, ignoreCase: true, out var parsed) ? parsed.ToString() : text.ToUpperInvariant();
    }

    private static PurchaseQuoteOrderPriceVarianceResolution Unresolved(string reason)
        => new() { Resolved = false, Reason = reason, Detail = null };

    private static string Normalize(string? value) => (value ?? string.Empty).Trim();

    private static string Display(string? value) => string.IsNullOrWhiteSpace(value) ? "（未填）" : value!.Trim();
}

/// <summary>价格差异解析结果：已定位明细 + 差异，或未解决原因（无数值差异）</summary>
public sealed class PurchaseQuoteOrderPriceVarianceResolution
{
    public bool Resolved { get; init; }

    /// <summary>未解决原因（resolved 时为空）</summary>
    public string Reason { get; init; } = string.Empty;

    /// <summary>已定位的采购订单明细（未解决时为 null）</summary>
    public PurchaseOrderDetail? Detail { get; init; }

    /// <summary>单价差 = 采购单价 − 报价单价（仅 resolved 时有值）</summary>
    public decimal? UnitPriceDelta { get; init; }

    /// <summary>金额差 = 采购金额 − 报价总额（仅 resolved 时有值）</summary>
    public decimal? AmountDelta { get; init; }
}
