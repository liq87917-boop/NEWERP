namespace ERP.Application.Services;

/// <summary>
/// 报价单有效期治理规则（ERP-018）：有效期状态分类与提醒窗口的唯一事实来源。
/// </summary>
/// <remarks>
/// 设计口径（与 <c>Quotation.ValidUntil</c> 现有字段配合，**不新增任何数据库结构**）：
/// <list type="bullet">
/// <item>剩余天数 = <c>ValidUntil</c>（取日期部分） − 判定基准日（取日期部分）；未填有效期返回 <c>null</c>。</item>
/// <item>状态文案：未设置有效期 / 已过期（剩余 &lt; 0）/ 今日到期（剩余 = 0）/ 即将到期（0 &lt; 剩余 ≤ 提醒窗口）/ 有效（剩余 &gt; 提醒窗口）。</item>
/// <item>提醒窗口默认 7 天（<see cref="DefaultAheadDays"/>），入参 &lt; 0 时回落默认值，避免出现"永不提醒"的隐式行为。</item>
/// <item>状态级别（<c>danger</c> / <c>warning</c> / <c>success</c> / <c>neutral</c>）与前端徽标样式（<c>status-*</c>）一一对应。</item>
/// </list>
/// 价格有效期已过期的报价单不允许再直接对外发送，业务员应在有效期到期前跟进（报价单页「⏰ 有效期提醒」与列表「有效期状态」列）。
/// </remarks>
public static class QuotationValidityRules
{
    /// <summary>默认提醒窗口（天）：有效期在未来 7 天内到期即进入提醒清单</summary>
    public const int DefaultAheadDays = 7;

    /// <summary>状态文案：未设置有效期</summary>
    public const string NoneText = "未设置有效期";

    /// <summary>状态文案：已过期</summary>
    public const string ExpiredText = "已过期";

    /// <summary>状态文案：今日到期</summary>
    public const string DueTodayText = "今日到期";

    /// <summary>状态文案：即将到期</summary>
    public const string DueSoonText = "即将到期";

    /// <summary>状态文案：有效</summary>
    public const string ValidText = "有效";

    /// <summary>状态级别：已过期（红色）</summary>
    public const string DangerLevel = "danger";

    /// <summary>状态级别：临近到期（橙色）</summary>
    public const string WarningLevel = "warning";

    /// <summary>状态级别：仍然有效（绿色）</summary>
    public const string SuccessLevel = "success";

    /// <summary>状态级别：无有效期（灰色）</summary>
    public const string NeutralLevel = "neutral";

    /// <summary>提醒窗口归一化：负数按默认 7 天处理</summary>
    public static int NormalizeAheadDays(int aheadDays) => aheadDays < 0 ? DefaultAheadDays : aheadDays;

    /// <summary>剩余有效天数（负数 = 已过期、0 = 今日到期；未设置有效期返回 null）</summary>
    public static int? DaysRemaining(DateTime? validUntil, DateTime asOfDate)
        => validUntil is null ? null : (validUntil.Value.Date - asOfDate.Date).Days;

    /// <summary>有效期状态文案（未设置 / 已过期 / 今日到期 / 即将到期 / 有效）</summary>
    public static string StatusOf(DateTime? validUntil, DateTime asOfDate, int aheadDays = DefaultAheadDays)
    {
        var days = DaysRemaining(validUntil, asOfDate);
        if (days is null) return NoneText;
        if (days < 0) return ExpiredText;
        if (days == 0) return DueTodayText;
        return days <= NormalizeAheadDays(aheadDays) ? DueSoonText : ValidText;
    }

    /// <summary>有效期状态级别（danger / warning / success / neutral，前端直接拼 <c>status-</c> 样式类）</summary>
    public static string LevelOf(DateTime? validUntil, DateTime asOfDate, int aheadDays = DefaultAheadDays)
    {
        var days = DaysRemaining(validUntil, asOfDate);
        if (days is null) return NeutralLevel;
        if (days < 0) return DangerLevel;
        return days <= NormalizeAheadDays(aheadDays) ? WarningLevel : SuccessLevel;
    }

    /// <summary>是否进入提醒范围（已过期或提醒窗口内到期；未设置有效期的报价单不提醒）</summary>
    public static bool IsDue(DateTime? validUntil, DateTime asOfDate, int aheadDays = DefaultAheadDays)
        => validUntil is not null && DaysRemaining(validUntil, asOfDate) <= NormalizeAheadDays(aheadDays);

    /// <summary>提醒紧急度（越大越紧急）：已过期 &gt; 今日到期 &gt; 即将到期</summary>
    public static int UrgencyOf(DateTime? validUntil, DateTime asOfDate, int aheadDays = DefaultAheadDays)
    {
        var days = DaysRemaining(validUntil, asOfDate);
        if (days is null) return 0;
        if (days < 0) return 3;
        if (days == 0) return 2;
        return days <= NormalizeAheadDays(aheadDays) ? 1 : 0;
    }
}
