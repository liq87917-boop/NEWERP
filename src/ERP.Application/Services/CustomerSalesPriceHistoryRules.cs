using ERP.Application.Common;
using ERP.Application.DTOs;

namespace ERP.Application.Services;

/// <summary>
/// 客户销售订单价格历史（ERP-108）的纯规则：口径键、口径文案与参数归一化。
/// 用途：只读展示已审核销售订单明细的持久化价格，仅在「客户 + 商品 + 规格 + 单位 + 币种」完全一致时
/// 视为同一口径归为一组，保留原始单价与贸易条款；口径不一致的证据分组成行单列。
/// <para>边界（重要）：本规则只做<strong>判定与文案</strong>，不读写数据库、不访问存储、
/// 不发起网络请求，也不改写销售订单 / 明细 / 价格 / 贸易条款。</para>
/// </summary>
public static class CustomerSalesPriceHistoryRules
{
    /// <summary>默认每页条数</summary>
    public const int DefaultPageSize = 50;

    /// <summary>每页条数上限（有界：单次请求最多返回这么多明细行）</summary>
    public const int MaxPageSize = 200;

    /// <summary>口径与边界说明（界面与文档同源）</summary>
    public const string RuleText =
        "本视图是已审核销售订单明细价格的只读历史（不写库、不重定价、不改贸易条款）：按商品聚合所有未删除、已审核订单的未删除明细，" +
        "仅当「客户 + 商品 + 规格 + 单位 + 币种」完全一致时归为同一口径组，组内保留原始单价与贸易条款；" +
        "口径不一致（单位 / 币种 / 规格 / 客户不同）的证据分组成行单列，绝不跨口径比较、绝不做汇率换算、绝不合并不同币种金额；" +
        "业务员数据范围是硬边界（只读受分配客户），分页有界，命中截断时显式标注。";

    /// <summary>商品 Id 必填，缺失或非法抛参数错误</summary>
    public static long NormalizeProductId(CustomerSalesPriceHistoryQuery query)
    {
        if (query.ProductId is null or <= 0)
            throw BusinessException.InvalidParameter("请提供商品 Id");
        return query.ProductId.Value;
    }

    /// <summary>分页归一化：页码最小为 1，每页条数 1 ~ <see cref="MaxPageSize"/>（越界按上限截断）</summary>
    public static (int page, int pageSize) NormalizePaging(CustomerSalesPriceHistoryQuery query)
    {
        var page = query.Page < 1 ? 1 : query.Page;
        var pageSize = query.PageSize < 1 ? DefaultPageSize : Math.Min(query.PageSize, MaxPageSize);
        return (page, pageSize);
    }

    /// <summary>订单日期区间归一化：结束日期含当天（上界开区间），开始晚于结束抛参数错误</summary>
    public static (DateTime? from, DateTime? to) NormalizeDateRange(CustomerSalesPriceHistoryQuery query)
    {
        var from = query.DateFrom?.Date;
        var to = query.DateTo?.Date.AddDays(1);
        if (from is not null && to is not null && from >= to)
            throw BusinessException.InvalidParameter("订单日期区间不合法（开始日期不能晚于结束日期）");
        return (from, to);
    }

    /// <summary>口径键：客户 + 商品 + 规格 + 单位 + 币种（规格 / 单位去首尾空白，币种不区分大小写）</summary>
    public static string ComparisonKey(long customerId, long productId, string? spec, string? unit, string? currency)
        => $"{customerId}\u0001{productId}\u0001{Normalize(spec)}\u0001{Normalize(unit)}\u0001{Normalize(currency).ToUpperInvariant()}";

    /// <summary>口径文案（界面与文档同源）</summary>
    public static string BasisText(string customerName, string productName, string spec, string unit, string currency)
        => $"客户：{Display(customerName)} · 商品：{Display(productName)} · 规格：{Display(spec)} · 单位：{Display(unit)} · 币种：{Display(currency)}";

    /// <summary>去首尾空白（未填写按空串归组）</summary>
    public static string Normalize(string? value) => (value ?? string.Empty).Trim();

    /// <summary>展示文案：空白显示「（未填）」</summary>
    public static string Display(string? value) => string.IsNullOrWhiteSpace(value) ? "（未填）" : value!.Trim();
}
