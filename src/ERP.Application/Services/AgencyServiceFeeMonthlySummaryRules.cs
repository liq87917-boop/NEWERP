using ERP.Application.Common;

namespace ERP.Application.Services;

/// <summary>
/// 代理服务费对账单**月度汇总**的纯规则（ERP-110，无数据库依赖，便于逐条单测）：
/// 分组键（对账日期所属年月 + 客户 + 原币）、金额与状态口径（仅**未删除且已登记**的对账单计入原币合计；
/// 草稿与已作废**单独计数、金额单独列示**、绝不并入合计）、服务期间跨月**不按期间分摊**、
/// 分页与筛选取值归一化，以及接口 / 界面 / 文档同源的口径文案。
/// <para>关键口径：</para>
/// <list type="number">
/// <item>只读：本规则只做判定、计算与文案，不写库、不开票、不记账、不收款或付款，也不改写对账单证据、
/// 协议、客户、销售订单、装柜清单、单证、发票、收款、库存、费用与结算记录；</item>
/// <item>分组只按持久化字段：对账日期所属年月（<c>StatementDate.Year</c> / <c>StatementDate.Month</c>）、
/// 客户 Id、币种（原币）；不同客户 / 币种绝不合并；</item>
/// <item>原币合计只来自未删除且已登记对账单的持久化 <c>TotalAmount</c>（服务端已计算的合计证据），
/// 不重算、不换算、不把草稿或已作废金额并入；</item>
/// <item>服务期间跨越多个月的对账单，<strong>全额</strong>计入其**对账日期**所属月份，
/// 不按天数 / 月份拆分、不重算、不跨月分摊。</item>
/// </list>
/// </summary>
public static class AgencyServiceFeeMonthlySummaryRules
{
    /// <summary>默认每页条数</summary>
    public const int DefaultPageSize = 50;

    /// <summary>每页条数上限（有界：单次请求最多返回这么多「年月 + 客户 + 币种」分组）</summary>
    public const int MaxPageSize = 200;

    /// <summary>分组与金额口径（接口 / 界面 / 文档同源）</summary>
    public const string RuleText =
        "口径：把**未删除**的代理服务费对账单证据按「对账日期所属年月 + 客户 + 原币」分组；"
        + "仅**未删除且已登记**的对账单计入原币合计（合计直接来自服务端计算的持久化 TotalAmount，不重算、不换算）；"
        + "草稿与已作废对账单单独计数、金额单独列示，绝不并入原币合计；"
        + "服务期间跨越多个月的对账单全额计入其对账日期所属月份，不按期间分摊。";

    /// <summary>与收入确认 / 应收账款 / 付款通知 / 税务申报 / 结算确认的边界说明（界面与文档同源）</summary>
    public const string BoundaryText =
        "本页是仓库内对账单证据的**只读月度汇总**：不是收入确认、不是应收账款或应收余额、"
        + "不是法定客户对账单、不是付款通知或催款函、不是税务申报或开票依据、不是结算或核销确认；"
        + "所有金额与计数只是**证据数字**，绝不称为「已确认收入 / 应收 / 已收款 / 欠款」。";

    /// <summary>只读边界说明（不写库、不改写任何来源记录）</summary>
    public const string ReadOnlyText =
        "全程只读：不新增 / 不修改任何表与列，不写库、不迁移、不回填；"
        + "不改写对账单证据（含合计 / 状态 / 服务期间 / 行清单）、协议、客户、销售订单、装柜清单、"
        + "单证、发票、收款、库存、费用与结算记录；不开票、不记账、不收款或付款、不催收、不调用外部服务。";

    /// <summary>服务期间跨月不按期间分摊的说明</summary>
    public const string NoProrationText =
        "服务期间跨月**不按期间分摊**：一条对账单证据无论其服务期间跨越多个月，"
        + "都**全额**计入其**对账日期**所属月份，不按天数 / 月份拆分、不重算、不跨月分摊。";

    /// <summary>币种隔离说明（不同币种绝不合并、绝无跨币种总额）</summary>
    public const string CurrencyIsolationText =
        "币种隔离：所有金额一律按**原币**分别成组，不同币种**绝不**合并、绝不换算，本页没有任何跨币种总额字段。";

    /// <summary>证据口径说明（金额与计数只是证据数字，不代表资金 / 收入结论）</summary>
    public const string EvidenceOnlyText =
        "证据口径：金额与计数只反映已登记的代理服务费对账单证据本身，不代表已收款、已结清、欠款或收入已确认。";

    /// <summary>年月文案（接口 / 界面同源：<c>yyyy-MM</c>）</summary>
    public static string MonthText(int year, int month) => $"{year:0000}-{month:00}";

    /// <summary>客户筛选归一化（留空或非正数 = 不过滤）</summary>
    public static long? NormalizeCustomerFilter(long? customerId)
        => customerId is <= 0 ? null : customerId;

    /// <summary>币种筛选归一化（留空 = 不过滤；非法取值直接拒绝，复用 ERP-070 币种白名单）</summary>
    public static string? NormalizeCurrencyFilter(string? currency)
        => string.IsNullOrWhiteSpace(currency)
            ? null
            : AgencyServiceFeeStatementRules.NormalizeCurrencyStrict(currency);

    /// <summary>对账日期区间归一化（只取日期部分，含当天；开始晚于结束抛参数错误）</summary>
    public static (DateTime? from, DateTime? to) NormalizeDateRange(DateTime? from, DateTime? to)
    {
        var start = from?.Date;
        var end = to?.Date;
        if (start.HasValue && end.HasValue && start > end)
        {
            throw BusinessException.InvalidParameter(
                $"对账日期开始 {start:yyyy-MM-dd} 不能晚于结束 {end:yyyy-MM-dd}");
        }

        return (start, end);
    }

    /// <summary>分页归一化：页码最小为 1，每页条数 1 ~ <see cref="MaxPageSize"/>（越界按上限截断）</summary>
    public static (int page, int pageSize) NormalizePaging(int page, int pageSize)
    {
        var p = page < 1 ? 1 : page;
        var s = pageSize < 1 ? DefaultPageSize : Math.Min(pageSize, MaxPageSize);
        return (p, s);
    }
}
