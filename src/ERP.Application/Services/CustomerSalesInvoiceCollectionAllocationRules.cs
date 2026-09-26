using ERP.Application.Common;
using ERP.Domain.Entities;
using ERP.Domain.Enums;

namespace ERP.Application.Services;

/// <summary>
/// 客户收款单 → 客户销项发票证据 收款分摊证据的纯规则（ERP-073，无数据库依赖，便于逐条单测）：
/// 状态机（有效 → 已作废）、币种与金额精度、收款单 / 发票证据的**权威资格**判定（未删除 / 未取消 / 已登记）、
/// 客户与币种兼容性、双向可用金额（收款单可分摊余额 / 发票未分摊含税额）、关联状态与口径文案。
/// <para>关键口径：</para>
/// <list type="number">
/// <item>一行只按**收款单 Id + 发票证据 Id 的持久化标识符**建立分摊，<strong>绝不</strong>按单号文本、金额、
/// 日期或相似度匹配 / 猜测；</item>
/// <item>不读取、不修改收款单与发票证据的任何字段，只保存服务端权威写入的快照；</item>
/// <item>金额按币种精度取整后必须大于 0，且不得超过**两侧**可用金额（收款单金额 − 本维度有效分摊，
/// 发票含税总额 − 本维度有效分摊）；</item>
/// <item>币种与原币一致：<strong>不做汇率换算、不跨币种合并</strong>；</item>
/// <item>本维度与 ERP-053 销售订单收款引用、ERP-055 销项发票 → 销售订单分摊、ERP-071 代理服务费收款分摊
/// 是**互相独立的证据维度**，绝不相加。</item>
/// </list>
/// <para>边界：本规则只做**校验与计算**，不写库、不收款、不付款、不记账、不核销、不催收或联系客户，
/// 也不改写收款单、发票证据、客户、销售订单、装柜清单、单证、库存、费用与结算记录。</para>
/// </summary>
public static class CustomerSalesInvoiceCollectionAllocationRules
{
    // ==================== 0. 口径常量 ====================

    /// <summary>状态：有效（形成收款分摊证据，占用收款单与发票两侧额度）</summary>
    public const int StatusActive = 1;

    /// <summary>状态：已作废（保留原始金额 / 快照 / 作废原因与审计历史，不物理删除、不重写）</summary>
    public const int StatusVoided = 2;

    /// <summary>关联状态：收款单 / 发票在本维度尚未被任何有效分摊行指向</summary>
    public const string LinkageUnallocated = "unallocated";

    /// <summary>关联状态：部分分摊（本维度有效行合计小于基准金额）</summary>
    public const string LinkagePartial = "partial";

    /// <summary>关联状态：已全部分摊（本维度有效行合计等于基准金额）</summary>
    public const string LinkageFullyAllocated = "fully_allocated";

    /// <summary>备注长度上限</summary>
    public const int MaxRemarkLength = 500;

    /// <summary>作废原因长度上限</summary>
    public const int MaxVoidReasonLength = 500;

    /// <summary>登记人长度上限</summary>
    public const int MaxAllocatedByLength = 100;

    /// <summary>关键字长度上限（超长直接拒绝，避免全表模糊扫描）</summary>
    public const int MaxKeywordLength = 100;

    /// <summary>单条发票最多保留的有效分摊行数（保证视图与前端有界）</summary>
    public const int MaxAllocationsPerInvoice = 50;

    /// <summary>单张收款单最多保留的有效分摊行数（保证视图与前端有界）</summary>
    public const int MaxAllocationsPerReceipt = 50;

    /// <summary>可分摊收款单候选单次返回上限（有界，避免一次拉全表）</summary>
    public const int MaxReceiptCandidates = 200;

    /// <summary>可承接分摊的发票候选单次返回上限（有界，避免一次拉全表）</summary>
    public const int MaxInvoiceCandidates = 200;

    /// <summary>系统支持的币种（与收款单 / 发票 / 订单币种枚举同源：CNY / USD / EUR / HKD / GBP / JPY）</summary>
    public static readonly string[] SupportedCurrencies = Enum.GetNames<Currency>();

    // ==================== 0.1 口径文案（接口、界面与文档同源） ====================

    /// <summary>分摊口径文案</summary>
    public const string RuleText =
        "分摊口径：一行只能用**收款单 Id + 发票证据 Id（持久化标识符）**把一张既有、未删除且未取消的收款单的"
        + "一部分（或全部）金额显式分摊到一条**已登记**的客户销项发票证据上；发票类型 / 代码 / 号码 / 日期 / "
        + "状态 / 客户 / 币种 / 含税总额快照由服务端权威写入；系统**绝不**按单号文本、金额、日期或相似度猜测对应关系；"
        + "分摊金额按币种精度取整后必须大于 0，且不得超过收款单可分摊余额与发票未分摊含税额中的较小者。";

    /// <summary>双向金额口径文案</summary>
    public const string AmountRuleText =
        "金额口径：分摊金额只来自授权用户**显式提交**，服务端只做币种精度取整（0.5 进位）与上限校验；"
        + "收款单可分摊余额 = 收款单金额 − **本维度**有效分摊总额，发票未分摊含税额 = 发票含税总额 − "
        + "**本维度**有效分摊总额；两侧未分摊金额分别展示、**绝不被静默核销或改派**，"
        + "也不表示已付 / 已结清 / 逾期 / 已确认收入 / 已记账状态或应收余额。";

    /// <summary>唯一性口径文案</summary>
    public const string UniquenessRuleText =
        "唯一性口径：同一「发票证据 + 收款单」在**未作废**分摊行内唯一（重复提交一律拒绝，不合并、不覆盖）；"
        + "已作废行保留可读但不再占用额度，因此同一组合可以重新登记一条新的有效分摊行（新旧并存可查）。";

    /// <summary>证据维度分离文案</summary>
    public const string DimensionSeparationText =
        "证据维度分离：本登记册的分摊只属于「客户收款 → 客户销项发票」这一个证据维度；"
        + "ERP-053 的「收款单 → 销售订单」收款引用、ERP-055 的「销项发票 → 销售订单」分摊、"
        + "ERP-071 的「客户收款 → 代理服务费对账单」收款分摊是**另外三个独立维度**；"
        + "四个维度的金额**绝不相加**、也**不**被当作几张不同的收款单，任意一个维度的变动都不影响其它维度的数值。";

    /// <summary>历史证据只读口径文案</summary>
    public const string HistoricalEvidenceText =
        "历史证据只读：收款单被取消或软删除、发票证据被作废或软删除、客户被停用 / 删除 / 改名后，"
        + "历史分摊行仍按登记当时的收款单与发票快照可读，只显式标注不可用；"
        + "系统不会把历史行改派到别的收款单或发票，也不提供硬删除与静默金额替换。";

    /// <summary>模块边界文案（明确不是到账凭证 / 应收台账 / 核销 / 结算 / 记账 / 税务）</summary>
    public const string BoundaryText =
        "本登记册只是客户收款关联销项发票的分摊证据：不是银行入账 / 到账凭证、不是应收账款台账或余额、"
        + "不是货款核销、不是客户对账单、不是收入确认、不是税务（销项）判断、不是结算确认、"
        + "也不是会计凭证或总账记账分录；登记 / 作废分摊行都不会真的收款或付款、不会移动资金、"
        + "不会把发票或收款单标记为已收款或已结清，也不改写收款单与发票证据的任何既有字段。";

    // ==================== 1. 币种、金额与文本 ====================

    /// <summary>币种规范化 + 支持范围校验（收款单币种必须来自系统币种口径，才能与发票币种权威比对）</summary>
    public static string NormalizeCurrencyStrict(string? currency)
    {
        var value = CurrencyAmountRules.NormalizeCurrency(currency);
        if (!SupportedCurrencies.Contains(value, StringComparer.Ordinal))
            throw BusinessException.InvalidParameter(
                $"币种「{value}」不受支持：只允许 {string.Join(" / ", SupportedCurrencies)}"
                + "（收款单币种必须与发票币种一致，系统不做汇率换算）");
        return value;
    }

    /// <summary>收款单 / 发票金额上限口径（按币种精度取整；用于有效行分配合计的上限比对）</summary>
    public static decimal AuthoritativeAmount(decimal amount, string? currency)
        => CurrencyAmountRules.RoundAmount(amount, currency);

    /// <summary>
    /// 分摊金额校验（服务端权威）：按币种精度四舍五入（0.5 进位）后必须大于 0；
    /// 返回取整后的金额（写入即取整值，系统不自动调整差额、不做汇率换算）。
    /// </summary>
    public static decimal NormalizeAllocationAmount(decimal allocatedAmount, string? currency)
    {
        var rounded = CurrencyAmountRules.RoundAmount(allocatedAmount, currency);
        if (rounded <= 0)
            throw BusinessException.InvalidParameter(
                $"分摊金额必须大于 0：收到 {allocatedAmount}，按 {CurrencyAmountRules.NormalizeCurrency(currency)} "
                + $"精度取整后为 {rounded}");
        return rounded;
    }

    /// <summary>备注规范化（去首尾空白并校验长度；超长直接拒绝，不静默截断）</summary>
    public static string NormalizeRemark(string? remark)
    {
        var value = (remark ?? string.Empty).Trim();
        if (value.Length > MaxRemarkLength)
            throw BusinessException.InvalidParameter($"备注长度不能超过 {MaxRemarkLength} 个字符");
        return value;
    }

    /// <summary>作废原因规范化（必填 + 长度校验：作废保留原始值，必须记录更正原因）</summary>
    public static string NormalizeVoidReason(string? reason)
    {
        var value = (reason ?? string.Empty).Trim();
        if (value.Length == 0)
            throw BusinessException.InvalidParameter("请填写作废原因：作废会保留原始分摊证据，必须记录更正原因");
        if (value.Length > MaxVoidReasonLength)
            throw BusinessException.InvalidParameter($"作废原因长度不能超过 {MaxVoidReasonLength} 个字符");
        return value;
    }

    /// <summary>登记人规范化（服务端按已认证身份写入，不由客户端提交；缺失记「未知用户」）</summary>
    public static string NormalizeAllocatedBy(string? allocatedBy)
    {
        var value = (allocatedBy ?? string.Empty).Trim();
        if (value.Length == 0) return "未知用户";
        return value.Length > MaxAllocatedByLength ? value[..MaxAllocatedByLength] : value;
    }

    /// <summary>关键字规范化（去首尾空白 + 长度校验；超长直接拒绝，避免全表模糊扫描）</summary>
    public static string NormalizeKeyword(string? keyword)
    {
        var value = (keyword ?? string.Empty).Trim();
        if (value.Length > MaxKeywordLength)
            throw BusinessException.InvalidParameter($"关键字长度不能超过 {MaxKeywordLength} 个字符");
        return value;
    }

    /// <summary>金额文案（显式展示原币与币种精度；不做换算、不补齐币种）</summary>
    public static string AmountText(decimal amount, string? currency)
    {
        var cur = CurrencyAmountRules.NormalizeCurrency(currency);
        var decimals = CurrencyAmountRules.PrecisionOf(cur);
        return $"{amount.ToString($"F{decimals}", System.Globalization.CultureInfo.InvariantCulture)} {cur}";
    }

    // ==================== 2. 状态机与文案 ====================

    /// <summary>分摊行状态文案</summary>
    public static string StatusText(int status) => status switch
    {
        StatusActive => "有效",
        StatusVoided => "已作废",
        _ => throw BusinessException.InvalidParameter(
            $"分摊行状态只能是 {StatusActive}（有效）/ {StatusVoided}（已作废），收到 {status}")
    };

    /// <summary>状态过滤规范化（为空 = 不过滤；未知取值一律拒绝，不静默忽略筛选条件）</summary>
    public static int? NormalizeStatusFilter(int? status)
    {
        if (status is null) return null;
        _ = StatusText(status.Value);
        return status;
    }

    /// <summary>有效行可以作废（已作废拒绝重复作废：历史留痕不重写）</summary>
    public static void EnsureVoidable(int status, string identity)
    {
        if (status == StatusVoided)
            throw BusinessException.RuleConflict(
                $"分摊行「{identity}」已是已作废状态，不能重复作废"
                + "（已作废证据保留原始值，不提供重写或硬删除）");
    }

    /// <summary>收款单状态文案（快照与只读标注共用；未知取值照实回显，不假定为已到账或已审核）</summary>
    public static string ReceiptStatusText(int status) => status switch
    {
        (int)DocumentStatus.Pending => "待提交",
        (int)DocumentStatus.Submitted => "已提交",
        (int)DocumentStatus.Approved => "已审核",
        (int)DocumentStatus.Rejected => "已驳回",
        (int)DocumentStatus.Completed => "已完成",
        (int)DocumentStatus.Cancelled => "已取消",
        _ => $"未知（{status}）"
    };

    /// <summary>发票状态文案（与 ERP-055 口径一致；未知取值照实回显）</summary>
    public static string InvoiceStatusText(int status) => status switch
    {
        CustomerSalesInvoiceEvidenceRules.StatusDraft => "草稿",
        CustomerSalesInvoiceEvidenceRules.StatusRecorded => "已登记",
        CustomerSalesInvoiceEvidenceRules.StatusVoided => "已作废",
        _ => $"未知（{status}）"
    };

    // ==================== 3. 权威资格（收款单 / 发票证据） ====================

    /// <summary>
    /// 收款单能否用于新分摊行的判定（**不抛异常**，返回原因文案供界面逐条说明）：
    /// 存在、未删除、未取消；金额按币种精度取整后必须大于 0；币种必须在系统币种口径内；
    /// 且本维度有效分摊合计尚未占满收款单金额。
    /// </summary>
    public static (bool Eligible, string Text) EvaluateReceiptEligibility(
        FinanceReceipt? receipt, decimal allocatedAmount)
    {
        if (receipt is null || receipt.IsDeleted)
            return (false, "收款单不存在或已删除，不能登记新分摊（历史分摊仍可读）");

        if (receipt.Status == DocumentStatus.Cancelled)
            return (false,
                $"收款单「{receipt.ReceiptNo}」已取消，不能登记新分摊（已取消收款单不参与收款分摊证据，历史分摊仍可读）");

        var currency = CurrencyAmountRules.NormalizeCurrency(receipt.Currency.ToString());
        if (!SupportedCurrencies.Contains(currency, StringComparer.Ordinal))
            return (false, $"收款单币种「{currency}」不在系统币种口径内，不能登记分摊（不做汇率换算）");

        var amount = AuthoritativeAmount(receipt.Amount, currency);
        if (amount <= 0)
            return (false, $"收款单金额为 {amount} {currency}，没有可分摊的收款金额");

        var remaining = amount - allocatedAmount;
        if (remaining <= 0)
            return (false,
                $"收款单在本维度已被有效分摊行占满（已分摊 {allocatedAmount} / 收款金额 {amount} {currency}），"
                + "不能再登记分摊");

        return (true,
            $"可分摊：收款单状态 {ReceiptStatusText((int)receipt.Status)}，本维度剩余可分摊 {remaining} {currency}");
    }

    /// <summary>校验收款单可用于新分摊（不存在 / 已删除 → 数据不存在；其余不可用原因 → 业务规则冲突）</summary>
    public static void EnsureReceiptAllocatable(FinanceReceipt? receipt, decimal allocatedAmount, string identity)
    {
        var (eligible, text) = EvaluateReceiptEligibility(receipt, allocatedAmount);
        if (eligible) return;
        if (receipt is null || receipt.IsDeleted) throw BusinessException.NotFound(text);
        throw BusinessException.RuleConflict($"{text}（{identity}）");
    }

    /// <summary>
    /// 发票证据能否承接新分摊行的判定（**不抛异常**，返回原因文案供界面逐条说明）：
    /// 存在、未删除、**已登记**（草稿 / 已作废拒绝）；含税总额必须大于 0；币种必须在系统币种口径内；
    /// 且本维度有效分摊合计尚未占满含税总额。
    /// </summary>
    public static (bool Eligible, string Text) EvaluateInvoiceEligibility(
        CustomerSalesInvoiceEvidence? invoice, decimal allocatedAmount)
    {
        if (invoice is null || invoice.IsDeleted)
            return (false, "客户销项发票证据不存在或已删除，不能登记分摊（历史分摊仍可读）");

        if (invoice.Status != CustomerSalesInvoiceEvidenceRules.StatusRecorded)
            return (false,
                $"发票「{IdentityText(invoice.InvoiceType, invoice.InvoiceCode, invoice.InvoiceNumber)}」"
                + $"当前状态为 {InvoiceStatusText(invoice.Status)}，"
                + "只有**已登记**的发票证据可以承接收款分摊（草稿尚未构成证据、已作废证据不可再分摊）");

        var currency = CurrencyAmountRules.NormalizeCurrency(invoice.Currency);
        if (!SupportedCurrencies.Contains(currency, StringComparer.Ordinal))
            return (false, $"发票币种「{currency}」不在系统币种口径内，不能登记分摊（不做汇率换算）");

        var total = AuthoritativeAmount(invoice.GrossAmount, currency);
        if (total <= 0)
            return (false, $"发票含税总额为 {total} {currency}，没有可分摊的含税金额");

        var remaining = total - allocatedAmount;
        if (remaining <= 0)
            return (false,
                $"发票已被本维度有效分摊行占满（已分摊 {allocatedAmount} / 含税总额 {total} {currency}），"
                + "不能再登记分摊");

        return (true,
            $"可分摊：发票身份 {IdentityText(invoice.InvoiceType, invoice.InvoiceCode, invoice.InvoiceNumber)}，"
            + $"本维度未分摊 {remaining} {currency}");
    }

    /// <summary>校验发票可承接新分摊（不存在 / 已删除 → 数据不存在；其余不可用原因 → 业务规则冲突）</summary>
    public static void EnsureInvoiceAllocatable(
        CustomerSalesInvoiceEvidence? invoice, decimal allocatedAmount, string identity)
    {
        var (eligible, text) = EvaluateInvoiceEligibility(invoice, allocatedAmount);
        if (eligible) return;
        if (invoice is null || invoice.IsDeleted) throw BusinessException.NotFound(text);
        throw BusinessException.RuleConflict($"{text}（{identity}）");
    }

    // ==================== 3.1 客户 / 币种兼容与可用性文案 ====================

    /// <summary>客户与币种兼容校验（收款单客户 / 币种必须与发票客户 / 币种**权威一致**；不做汇率换算、不跨客户合并）</summary>
    public static void EnsureCompatible(
        FinanceReceipt receipt, string receiptCurrency, CustomerSalesInvoiceEvidence invoice)
    {
        if (receipt.CustomerId != invoice.CustomerId)
            throw BusinessException.RuleConflict(
                $"收款单「{receipt.ReceiptNo}」的客户（Id={receipt.CustomerId}）与发票「"
                + $"{IdentityText(invoice.InvoiceType, invoice.InvoiceCode, invoice.InvoiceNumber)}」"
                + $"的客户（Id={invoice.CustomerId}）不一致，不能登记分摊（不做跨客户合并）");

        var invoiceCurrency = NormalizeCurrencyStrict(invoice.Currency);
        if (!string.Equals(receiptCurrency, invoiceCurrency, StringComparison.Ordinal))
            throw BusinessException.RuleConflict(
                $"收款单「{receipt.ReceiptNo}」的币种 {receiptCurrency} 与发票「"
                + $"{IdentityText(invoice.InvoiceType, invoice.InvoiceCode, invoice.InvoiceNumber)}」的币种 "
                + $"{invoiceCurrency} 不一致，不能登记分摊（不做汇率换算、不跨币种合并）");
    }

    /// <summary>收款单可用性文案（历史证据照常可读，不可用时显式说明）</summary>
    public static string ReceiptAvailabilityText(FinanceReceipt? receipt)
    {
        if (receipt is null || receipt.IsDeleted) return "收款单已删除（历史快照仍可读，不能再分摊）";
        if (receipt.Status == DocumentStatus.Cancelled) return "收款单已取消（历史快照仍可读，不能再分摊）";
        return "收款单可读（只读引用，未被本模块改写）";
    }

    /// <summary>发票可用性文案（历史证据照常可读，不可用时显式说明）</summary>
    public static string InvoiceAvailabilityText(CustomerSalesInvoiceEvidence? invoice)
    {
        if (invoice is null || invoice.IsDeleted)
            return "发票证据已删除（历史快照仍可读，不能再分摊）";
        if (invoice.Status == CustomerSalesInvoiceEvidenceRules.StatusVoided)
            return "发票证据已作废（历史快照仍可读，不能再分摊）";
        if (invoice.Status == CustomerSalesInvoiceEvidenceRules.StatusDraft)
            return "发票证据仍为草稿（历史快照仍可读，尚不能承接分摊）";
        return "发票证据可读（只读引用，未被本模块改写）";
    }

    /// <summary>发票对外身份文案（与 ERP-055 口径一致；不按文本模糊匹配）</summary>
    public static string IdentityText(string? invoiceType, string? invoiceCode, string? invoiceNumber)
        => CustomerSalesInvoiceEvidenceRules.IdentityText(invoiceType, invoiceCode, invoiceNumber);

    /// <summary>分摊行对外身份文案（「发票 ← 收款单」）</summary>
    public static string AllocationIdentityText(
        string? invoiceType, string? invoiceCode, string? invoiceNumber, string? receiptNo)
    {
        var invoice = IdentityText(invoiceType, invoiceCode, invoiceNumber);
        var receipt = (receiptNo ?? string.Empty).Trim();
        return receipt.Length == 0 ? $"{invoice} ← (未填收款单号)" : $"{invoice} ← {receipt}";
    }

    // ==================== 4. 关联状态（只读派生） ====================

    /// <summary>关联状态判定（依据有效持久化行合计；等于基准金额时才是已全部分摊）</summary>
    public static string LinkageStatusOf(decimal baseAmount, decimal allocatedAmount)
        => allocatedAmount <= 0 ? LinkageUnallocated
            : allocatedAmount < baseAmount ? LinkagePartial
            : LinkageFullyAllocated;

    /// <summary>
    /// 关联状态文案（显式展示已分摊 / 未分摊金额；未分摊部分<strong>绝不</strong>被猜测到任何收款单或发票，
    /// 也<strong>不</strong>被解释为已付 / 已结清 / 逾期 / 已确认收入 / 已记账状态或应收余额）。
    /// </summary>
    public static string LinkageText(
        decimal baseAmount, decimal allocatedAmount, int allocationCount, string? currency, string subject)
    {
        var cur = CurrencyAmountRules.NormalizeCurrency(currency);
        var unallocated = baseAmount - allocatedAmount;
        if (unallocated < 0) unallocated = 0;

        return LinkageStatusOf(baseAmount, allocatedAmount) switch
        {
            LinkageFullyAllocated =>
                $"{subject}已全部分摊：{allocatedAmount} {cur} 全部指向 {allocationCount} 条记录",
            LinkagePartial =>
                $"{subject}部分分摊：已分摊 {allocatedAmount} {cur}，未分摊 {unallocated} {cur}"
                + "（未分摊部分不会被系统猜测到任何记录，也不代表已付 / 已结清 / 逾期 / 收入确认 / 记账状态或应收余额）",
            _ =>
                $"{subject}在本维度尚未分摊：{baseAmount} {cur} 还没有指向任何记录"
                + "（系统不按单号 / 金额 / 日期相似度猜测对应关系）"
        };
    }
}
