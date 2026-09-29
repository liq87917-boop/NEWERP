namespace ERP.Application.Services;

/// <summary>收款时效分摊行内存投影（批量派生：一次取全本页分摊行与收款单，供首末收款派生与异常标注使用，不落库）</summary>
public sealed record CustomerInvoiceCollectionTimingReceiptRow(
    long AllocationId,
    int AllocationStatus,
    long ReceiptId,
    string ReceiptNo,
    DateTime ReceiptDate,
    decimal AllocatedAmount,
    string AllocationCurrency,
    bool ReceiptMissing,
    bool ReceiptCancelled,
    string? ReceiptCurrency);

/// <summary>收款时效派生结果（纯函数返回，供服务装配成行）</summary>
public sealed record CustomerInvoiceCollectionTimingDerivation(
    string? FirstReceiptNo,
    DateTime? FirstReceiptDate,
    string? LastReceiptNo,
    DateTime? LastReceiptDate,
    int? FirstCollectionDays,
    int? LastCollectionDays,
    decimal ComparableAllocatedAmount,
    decimal ComparableRemainingAmount,
    int ComparableAllocationCount,
    int MissingCount,
    int VoidedCount,
    int CancelledCount,
    int PreInvoiceCount,
    int CurrencyConflictCount,
    bool IsAnomalous,
    List<string> Anomalies,
    string Note);

/// <summary>
/// 客户销项发票收款时效纯规则（ERP-111，无数据库依赖，便于逐条单测）：
/// 用显式 ERP-073「收款单 → 发票」分摊行派生每张已登记发票「开票日期 → 首张 / 末张有效收款日期」的间隔天数，
/// 以及可比较的已分摊 / 剩余证据；缺链接、已作废、收款单取消、早于开票日期、币种不一致的分摊行
/// 仅作异常证据列出、不计入首末收款与可比较金额。
/// <para>边界：本规则只做**只读派生**，不写库、不改写发票 / 收款单 / 分摊行 / 客户，也不执行迁移 / 生产 SQL / 部署；
/// 分摊行<strong>绝不</strong>被当作银行到账、法定账龄或催收 SLA。</para>
/// </summary>
public static class CustomerInvoiceCollectionTimingRules
{
    // ==================== 0. 异常证据文案 ====================

    /// <summary>异常证据：存在收款单缺失或已删除的分摊行（不计入收款时效）</summary>
    public const string AnomalyMissingReceipt = "存在收款单缺失或已删除的分摊行（不计入收款时效）";

    /// <summary>异常证据：存在已作废分摊行（不计入收款时效）</summary>
    public const string AnomalyVoidedAllocation = "存在已作废分摊行（不计入收款时效）";

    /// <summary>异常证据：存在收款单已取消的分摊行（不计入收款时效）</summary>
    public const string AnomalyCancelledReceipt = "存在收款单已取消的分摊行（不计入收款时效）";

    /// <summary>异常证据：存在早于开票日期的收款（不计入首末收款）</summary>
    public const string AnomalyPreInvoiceReceipt = "存在早于开票日期的收款（不计入首末收款）";

    /// <summary>异常证据：存在币种不一致的收款（不做汇率换算、不计入）</summary>
    public const string AnomalyCurrencyConflict = "存在币种不一致的收款（不做汇率换算、不计入）";

    /// <summary>异常证据：本页分摊行超过单次派生上限，收款时效未知</summary>
    public const string AnomalyAllocationOverCeiling = "本页分摊行超过单次派生上限，收款时效未知";

    // ==================== 1. 有界上限 ====================

    /// <summary>本页关联分摊行的单次派生上限（超过即整页收款时效按未知，绝不报出不可靠的首末收款日期）</summary>
    public const int AllocationEvidenceCeiling = 2000;

    // ==================== 2. 口径 / 范围 / 边界文案（接口、界面与文档同源） ====================

    /// <summary>收款时效口径说明（界面与文档同源）</summary>
    public const string RuleText =
        "本报表按已登记发票的显式 ERP-073「收款单 → 发票」分摊行统计「开票日期到首张 / 末张有效收款日期」的间隔天数（只读派生）；"
        + "有效收款 = 未作废分摊行 + 收款单存在、未删除且未取消 + 币种与发票一致 + 收款日期不早于开票日期；"
        + "首收 / 末收间隔天数 = 收款日期 − 开票日期（整数天，只对有效收款计算）；"
        + "可比较已分摊 = 有效收款分摊金额合计，可比较剩余 = 含税总额 − 可比较已分摊（下限 0，只作算术证据）；"
        + "缺链接 / 已作废 / 收款单取消 / 早于开票日期 / 币种不一致的分摊行仅作异常证据列出、不计入；"
        + "分摊行绝不表示银行到账、法定账龄或催收 SLA，也不表示已付 / 已结清 / 逾期 / 已确认收入 / 已记账状态或应收余额。";

    /// <summary>范围说明：合计与计数只统计本次返回页的已登记发票（分页有界）</summary>
    public const string ScopeNoteText =
        "以下汇总与计数只统计本次返回页的未删除已登记发票；total 为符合筛选条件的未删除已登记发票总数；"
        + "分页按客户 + 开票日期 + 单据 Id 稳定排序；已应用业务员数据范围。";

    /// <summary>边界说明：不是银行到账 / 法定账龄 / 催收 SLA / 收入确认 / 结算确认</summary>
    public const string BoundaryText =
        "本报表不是银行到账凭证、不是法定账龄、不是催收 SLA、不是收入确认或结算确认：只统计显式收款分摊行的首末收款日期与间隔天数，"
        + "不推断缺失或不一致的日期，不据此改写任何已登记证据，也不做任何收款、付款、记账或核销动作。";

    // ==================== 3. 收款时效派生（纯函数，便于单测） ====================

    /// <summary>
    /// 派生收款时效（纯函数）。有效收款只取「未作废分摊行 + 收款单存在、未删除且未取消 + 币种与发票一致 +
    /// 收款日期不早于开票日期」的分摊行；缺链接 / 已作废 / 收款单取消 / 早于开票日期 / 币种不一致的分摊行
    /// 仅作为异常证据列出、不计入首末收款与可比较金额。
    /// </summary>
    public static CustomerInvoiceCollectionTimingDerivation Derive(
        DateTime invoiceDate, string? invoiceCurrency, decimal grossAmount,
        IReadOnlyList<CustomerInvoiceCollectionTimingReceiptRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var invoiceCur = CurrencyAmountRules.NormalizeCurrency(invoiceCurrency);

        var comparables = new List<CustomerInvoiceCollectionTimingReceiptRow>();
        int missing = 0, voided = 0, cancelled = 0, preInvoice = 0, currencyConflict = 0;

        foreach (var r in rows)
        {
            if (r.AllocationStatus == CustomerSalesInvoiceCollectionAllocationRules.StatusVoided)
            {
                voided++;
                continue;
            }
            if (r.ReceiptMissing)
            {
                missing++;
                continue;
            }
            if (r.ReceiptCancelled)
            {
                cancelled++;
                continue;
            }
            var receiptCur = CurrencyAmountRules.NormalizeCurrency(r.ReceiptCurrency);
            if (!string.Equals(receiptCur, invoiceCur, StringComparison.Ordinal))
            {
                currencyConflict++;
                continue;
            }
            if (r.ReceiptDate.Date < invoiceDate.Date)
            {
                preInvoice++;
                continue;
            }
            comparables.Add(r);
        }

        var anomalies = new List<string>();
        if (voided > 0) anomalies.Add(AnomalyVoidedAllocation);
        if (missing > 0) anomalies.Add(AnomalyMissingReceipt);
        if (cancelled > 0) anomalies.Add(AnomalyCancelledReceipt);
        if (currencyConflict > 0) anomalies.Add(AnomalyCurrencyConflict);
        if (preInvoice > 0) anomalies.Add(AnomalyPreInvoiceReceipt);

        var ordered = comparables
            .OrderBy(r => r.ReceiptDate)
            .ThenBy(r => r.ReceiptNo, StringComparer.Ordinal)
            .ThenBy(r => r.AllocationId)
            .ToList();

        var first = ordered.FirstOrDefault();
        var last = ordered.LastOrDefault();
        var allocated = comparables.Sum(r => r.AllocatedAmount);
        var remaining = grossAmount - allocated;
        if (remaining < 0) remaining = 0;

        var firstDate = first?.ReceiptDate.Date;
        var lastDate = last?.ReceiptDate.Date;
        int? firstDays = firstDate.HasValue ? (firstDate.Value - invoiceDate.Date).Days : null;
        int? lastDays = lastDate.HasValue ? (lastDate.Value - invoiceDate.Date).Days : null;

        var anomalous = anomalies.Count > 0;
        var note = first is not null
            ? (anomalous
                ? "已派生收款时效（首末收款来自有效且币种一致、不早于开票日期的分摊收款；存在其它异常分摊行，不计入）"
                : "已派生收款时效（首末收款来自有效且币种一致、不早于开票日期的分摊收款）")
            : (anomalous
                ? "无可比较收款证据（存在异常分摊行，不计入收款时效）"
                : "无收款分摊证据（尚未登记，绝不代表未收款 / 已收款 / 已结清 / 逾期）");

        return new CustomerInvoiceCollectionTimingDerivation(
            first?.ReceiptNo, firstDate, last?.ReceiptNo, lastDate, firstDays, lastDays,
            allocated, remaining, comparables.Count,
            missing, voided, cancelled, preInvoice, currencyConflict,
            anomalous, anomalies, note);
    }
}
