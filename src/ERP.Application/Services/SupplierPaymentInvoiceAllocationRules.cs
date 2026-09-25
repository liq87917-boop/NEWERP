using ERP.Application.Common;
using ERP.Domain.Entities;
using ERP.Domain.Enums;

namespace ERP.Application.Services;

/// <summary>
/// 供应商付款单 → 供应商采购发票 付款引用（分摊）证据登记的纯规则（ERP-066，无数据库依赖，便于逐条单测）：
/// 状态机（有效 → 已作废）、金额与币种精度、付款单 / 供应商 / 发票的权威资格判定、证据维度分离声明与文案。
/// <para>边界：本规则只做**校验与计算**，不写库、不改写付款单与发票、不改写采购订单与到货进度、库存与库存成本、
/// 退税、费用或供应商余额，也不执行任何付款、记账、核销或结算动作。</para>
/// </summary>
public static class SupplierPaymentInvoiceAllocationRules
{
    // ==================== 0. 口径常量 ====================

    /// <summary>状态：有效（形成付款引用证据，同时占用付款单金额额度与发票未引用含税总额额度）</summary>
    public const int StatusActive = 1;

    /// <summary>状态：已作废（保留原始值 / 供应商 / 发票快照与作废原因，不物理删除、不重写）</summary>
    public const int StatusVoided = 2;

    /// <summary>引用状态：付款单金额尚未被任何有效发票引用行指向</summary>
    public const string LinkageUnallocated = "unallocated";

    /// <summary>引用状态：部分引用（有效行合计小于比较基准）</summary>
    public const string LinkagePartial = "partial";

    /// <summary>引用状态：已全额引用（有效行合计等于比较基准）</summary>
    public const string LinkageFullyAllocated = "fully_allocated";

    /// <summary>备注长度上限</summary>
    public const int MaxRemarkLength = 500;

    /// <summary>作废原因长度上限</summary>
    public const int MaxVoidReasonLength = 500;

    /// <summary>登记人长度上限（登记人只取当前登录账号，客户端不可提交）</summary>
    public const int MaxRecordedByLength = 100;

    /// <summary>登记人缺失时的展示文案（绝不臆造账号）</summary>
    public const string UnknownUserText = "未知用户";

    /// <summary>单张付款单最多保留的有效引用行数（保证视图与前端有界）</summary>
    public const int MaxAllocationsPerPayment = 50;

    /// <summary>单张发票最多保留的有效引用行数（保证视图与前端有界）</summary>
    public const int MaxAllocationsPerInvoice = 50;

    /// <summary>可引用付款单候选单次返回上限（有界，避免一次拉全表）</summary>
    public const int MaxPaymentCandidates = 200;

    /// <summary>可引用发票候选单次返回上限（有界，避免一次拉全表）</summary>
    public const int MaxInvoiceCandidates = 200;

    /// <summary>系统支持的币种（与付款单 / 发票币种枚举同源：CNY / USD / EUR / HKD / GBP / JPY）</summary>
    public static readonly string[] SupportedCurrencies = Enum.GetNames<Currency>();

    /// <summary>引用口径文案（接口、界面与文档同源）</summary>
    public const string RuleText =
        "引用口径：一行只能指向既有、未删除且**已登记（未作废）**的供应商采购发票，"
        + "且发票供应商与币种都必须与付款单一致；引用金额必须大于 0（按币种精度取整），"
        + "同一付款单内有效行合计不得超过付款单金额，同一发票内有效行合计不得超过发票含税总额；"
        + "同一张发票在同一张付款单内只能有一条有效引用行（重复提交一律拒绝）；"
        + "付款单与发票都可以部分或全部未被引用，系统不会按号码相似度、金额相近或日期接近猜测发票。";

    /// <summary>证据维度分离文案（明确付款 → 采购订单 / 付款 → 采购发票 / 发票 → 采购订单 三个维度互不合并）</summary>
    public const string SeparateEvidenceText =
        "证据维度分离：本册的「付款单 → 供应商采购发票」引用行与 ERP-049 的「付款单 → 采购订单」引用行、"
        + "ERP-043 的「发票 → 采购订单」关联行是三个彼此独立的证据维度：本册金额绝不与采购订单引用金额相加，"
        + "也不改写付款单、发票与采购订单；三个维度都只作为只读派生展示，均不代表已付款、已核销或应付余额。";

    /// <summary>模块边界文案（明确不是付款凭证 / 核销 / 税务 / 余额）</summary>
    public const string BoundaryText =
        "本登记册只是供应商付款单的发票引用证据：不是银行付款凭证、不是应付账款核销、不是发票认证 / 抵扣、"
        + "不是税务申报，也不是供应商余额；登记 / 作废引用行都不会真的付款、不会移动资金、"
        + "不会把发票或采购订单标记为已结清，也不改写付款单、发票与采购订单的任何既有字段。";

    // ==================== 1. 币种、金额与文本 ====================

    /// <summary>币种规范化 + 支持范围校验（付款单币种必须来自系统币种口径，才能与发票权威比对）</summary>
    public static string NormalizeCurrencyStrict(string? currency)
    {
        var value = CurrencyAmountRules.NormalizeCurrency(currency);
        if (!SupportedCurrencies.Contains(value, StringComparer.Ordinal))
            throw BusinessException.InvalidParameter(
                $"币种「{value}」不受支持：只允许 {string.Join(" / ", SupportedCurrencies)}"
                + "（付款单币种必须与所引用发票的币种一致，系统不做汇率换算）");
        return value;
    }

    /// <summary>
    /// 引用金额校验（服务端权威）：按币种精度四舍五入（0.5 进位）后必须大于 0；
    /// 返回取整后的金额（写入即取整值，系统不自动调整差额、不做汇率换算）。
    /// </summary>
    public static decimal NormalizeAllocationAmount(decimal allocatedAmount, string? currency)
    {
        var rounded = CurrencyAmountRules.RoundAmount(allocatedAmount, currency);
        if (rounded <= 0)
            throw BusinessException.InvalidParameter(
                $"引用金额必须大于 0：收到 {allocatedAmount}，按 {CurrencyAmountRules.NormalizeCurrency(currency)} "
                + $"精度取整后为 {rounded}");
        return rounded;
    }

    /// <summary>权威金额口径（按币种精度取整；用于付款单金额与发票含税总额的比较基准）</summary>
    public static decimal AuthoritativeAmount(decimal amount, string? currency)
        => CurrencyAmountRules.RoundAmount(amount, currency);

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
            throw BusinessException.InvalidParameter("请填写作废原因：作废会保留原始引用证据，必须记录更正原因");
        if (value.Length > MaxVoidReasonLength)
            throw BusinessException.InvalidParameter($"作废原因长度不能超过 {MaxVoidReasonLength} 个字符");
        return value;
    }

    /// <summary>登记人规范化（只接受服务端从当前账号取得的文本；缺失记「未知用户」，绝不臆造）</summary>
    public static string NormalizeRecordedBy(string? recordedBy)
    {
        var value = (recordedBy ?? string.Empty).Trim();
        if (value.Length == 0) return UnknownUserText;
        return value.Length <= MaxRecordedByLength ? value : value[..MaxRecordedByLength];
    }

    /// <summary>关键字规范化（去首尾空白 + 长度校验；超长直接拒绝，避免全表模糊扫描）</summary>
    public static string NormalizeKeyword(string? keyword)
    {
        var value = (keyword ?? string.Empty).Trim();
        if (value.Length > 100) throw BusinessException.InvalidParameter("关键字长度不能超过 100 个字符");
        return value;
    }

    // ==================== 2. 状态机与文案 ====================

    /// <summary>引用行状态文案</summary>
    public static string StatusText(int status) => status switch
    {
        StatusActive => "有效",
        StatusVoided => "已作废",
        _ => throw BusinessException.InvalidParameter(
            $"引用行状态只能是 {StatusActive}（有效）/ {StatusVoided}（已作废），收到 {status}")
    };

    /// <summary>状态过滤规范化（为空 = 不过滤；未知取值一律拒绝，不静默忽略筛选条件）</summary>
    public static int? NormalizeStatusFilter(int? status)
    {
        if (status is null) return null;
        _ = StatusText(status.Value);
        return status;
    }

    /// <summary>有效行可以作废（已作废拒绝重复作废：历史留痕不重写）</summary>
    public static void EnsureVoidable(int status, string paymentNo, string invoiceIdentity)
    {
        if (status == StatusVoided)
            throw BusinessException.RuleConflict(
                $"付款单「{paymentNo}」→ 发票「{invoiceIdentity}」的引用行已是已作废状态，不能重复作废"
                + "（已作废证据保留原始值，不提供重写、改派或硬删除）");
    }

    /// <summary>付款单状态文案（快照与只读标注共用；未知取值照实回显，不假定为已审核 / 已付款）</summary>
    public static string PaymentStatusText(int status) => status switch
    {
        (int)DocumentStatus.Pending => "待提交",
        (int)DocumentStatus.Submitted => "已提交",
        (int)DocumentStatus.Approved => "已审核",
        (int)DocumentStatus.Rejected => "已驳回",
        (int)DocumentStatus.Completed => "已完成",
        (int)DocumentStatus.Cancelled => "已取消",
        _ => $"未知（{status}）"
    };

    /// <summary>发票状态文案（只读标注；取值与 <c>PurchaseInvoiceRules</c> 同源，未知取值照实回显）</summary>
    public static string InvoiceStatusText(int status) => status switch
    {
        PurchaseInvoiceRules.StatusDraft => "草稿",
        PurchaseInvoiceRules.StatusRecorded => "已登记",
        PurchaseInvoiceRules.StatusVoided => "已作废",
        _ => $"未知（{status}）"
    };

    /// <summary>付款单可用性文案（历史证据照常可读，不可用时显式说明，不静默改成 0 或空）</summary>
    public static string PaymentAvailabilityText(FinancePayment? payment)
    {
        if (payment is null || payment.IsDeleted)
            return "付款单已删除（历史快照仍可读，不能用于新引用）";
        return "付款单可用";
    }

    /// <summary>付款单是否可用于新引用行（存在且未删除）</summary>
    public static bool IsPaymentSelectable(FinancePayment? payment)
        => payment is not null && !payment.IsDeleted;

    /// <summary>
    /// 发票可用性文案（历史证据照常可读，不可用时显式说明）：
    /// 已删除 / 仍为草稿 / 已作废都照实标注，**不**把不可用发票当成可引用发票。
    /// </summary>
    public static string InvoiceAvailabilityText(PurchaseInvoice? invoice)
    {
        if (invoice is null || invoice.IsDeleted)
            return "发票已删除或不存在（历史快照仍可读，不能用于新引用）";
        if (invoice.Status == PurchaseInvoiceRules.StatusVoided)
            return "发票已作废（历史快照仍可读，不能用于新引用）";
        if (invoice.Status == PurchaseInvoiceRules.StatusDraft)
            return "发票仍为草稿（未登记，历史快照仍可读，不能用于新引用）";
        return "发票可用（已登记）";
    }

    /// <summary>发票是否可用于新引用行（存在、未删除且已登记）</summary>
    public static bool IsInvoiceSelectable(PurchaseInvoice? invoice)
        => invoice is not null && !invoice.IsDeleted
           && invoice.Status == PurchaseInvoiceRules.StatusRecorded;

    /// <summary>供应商可用性文案（历史证据照常可读，不可用时显式说明）</summary>
    public static string SupplierAvailabilityText(BaseSupplier? supplier)
    {
        if (supplier is null || supplier.IsDeleted) return "供应商已删除（历史快照仍可读）";
        if (supplier.Status != 1) return "供应商已停用（历史快照仍可读）";
        return "供应商可用";
    }

    // ==================== 3. 权威资格与金额上限 ====================

    /// <summary>
    /// 发票能否被本付款单引用的判定（**不抛异常**，返回原因文案供界面逐行说明）：
    /// 发票存在且未删除、**已登记（未作废）**、供应商一致、币种一致（不做汇率换算）。
    /// </summary>
    public static (bool Eligible, string Text) EvaluateInvoiceEligibility(
        FinancePayment payment, PurchaseInvoice? invoice)
    {
        ArgumentNullException.ThrowIfNull(payment);

        if (invoice is null || invoice.IsDeleted)
            return (false, "发票不存在或已删除，不能引用（历史引用仍可读）");

        if (invoice.Status == PurchaseInvoiceRules.StatusVoided)
            return (false, $"发票「{InvoiceIdentity(invoice)}」已作废，不能引用（作废发票不参与付款引用证据）");

        if (invoice.Status != PurchaseInvoiceRules.StatusRecorded)
            return (false,
                $"发票「{InvoiceIdentity(invoice)}」仍为{InvoiceStatusText(invoice.Status)}，"
                + "不能引用（付款引用只建立在本册已登记（未作废）的发票证据之上）");

        if (invoice.SupplierId != payment.SupplierId)
            return (false,
                $"发票「{InvoiceIdentity(invoice)}」的供应商（Id={invoice.SupplierId}）与付款单供应商"
                + $"（Id={payment.SupplierId}）不一致，不能引用");

        var paymentCurrencyText = CurrencyAmountRules.NormalizeCurrency(payment.Currency.ToString());
        var invoiceCurrencyText = CurrencyAmountRules.NormalizeCurrency(invoice.Currency);
        if (!string.Equals(invoiceCurrencyText, paymentCurrencyText, StringComparison.Ordinal))
            return (false,
                $"发票「{InvoiceIdentity(invoice)}」的币种 {invoiceCurrencyText} 与付款单币种 {paymentCurrencyText} "
                + "不一致，不能引用（不做汇率换算、不跨币种合并）");

        if (AuthoritativeAmount(invoice.GrossAmount, invoiceCurrencyText) <= 0)
            return (false, $"发票「{InvoiceIdentity(invoice)}」的含税总额不是正数，没有可引用的发票金额");

        return (true, $"可引用：供应商与币种（{paymentCurrencyText}）均与付款单一致");
    }

    /// <summary>校验发票可引用（不存在 / 已删除 → 数据不存在；其余不可用原因 → 业务规则冲突）</summary>
    public static void EnsureInvoiceLinkable(FinancePayment payment, PurchaseInvoice? invoice)
    {
        var (eligible, text) = EvaluateInvoiceEligibility(payment, invoice);
        if (eligible) return;
        if (invoice is null || invoice.IsDeleted) throw BusinessException.NotFound(text);
        throw BusinessException.RuleConflict(text);
    }

    /// <summary>
    /// 付款单能否登记新引用行的判定（**不抛异常**）：付款单存在且未删除、币种在系统口径内，
    /// 且仍有可引用余额（有效发票引用行合计 &lt; 付款单金额）。
    /// </summary>
    public static (bool Eligible, string Text) EvaluatePaymentEligibility(
        FinancePayment? payment, decimal allocatedAmount)
    {
        if (payment is null || payment.IsDeleted)
            return (false, "付款单不存在或已删除，不能登记新引用");

        var currency = CurrencyAmountRules.NormalizeCurrency(payment.Currency.ToString());
        if (!SupportedCurrencies.Contains(currency, StringComparer.Ordinal))
            return (false, $"付款单币种「{currency}」不在系统币种口径内，不能登记引用（不做汇率换算）");

        var amount = AuthoritativeAmount(payment.Amount, currency);
        if (amount <= 0)
            return (false, $"付款单金额为 {amount} {currency}，没有可引用的付款金额");

        var remaining = amount - allocatedAmount;
        if (remaining <= 0)
            return (false,
                $"付款单已被有效发票引用行占满（已引用 {allocatedAmount} / 付款金额 {amount} {currency}），不能再登记引用");

        return (true, $"可引用：付款单状态 {PaymentStatusText((int)payment.Status)}，剩余可引用 {remaining} {currency}");
    }

    // ==================== 4. 身份与引用状态文案 ====================

    /// <summary>发票身份文案（读取侧回退用；与 <c>PurchaseInvoiceRules.IdentityText</c> 同源口径，绝不猜测）</summary>
    public static string InvoiceIdentity(PurchaseInvoice invoice)
    {
        ArgumentNullException.ThrowIfNull(invoice);
        return PurchaseInvoiceRules.IdentityText(invoice.InvoiceType, invoice.InvoiceCode, invoice.InvoiceNumber);
    }

    /// <summary>发票快照身份文案（历史证据只按持久化快照回显；快照缺失时按既有身份口径回退，不臆造号码）</summary>
    public static string InvoiceIdentityText(
        string? invoiceType, string? invoiceCode, string? invoiceNumber, string? snapshotIdentity)
    {
        if (!string.IsNullOrWhiteSpace(snapshotIdentity)) return snapshotIdentity.Trim();
        return PurchaseInvoiceRules.IdentityText(invoiceType, invoiceCode, invoiceNumber);
    }

    /// <summary>引用状态判定（依据有效持久化行合计；等于比较基准时才是已全额引用）</summary>
    public static string LinkageStatusOf(decimal baselineAmount, decimal allocatedAmount)
        => allocatedAmount <= 0 ? LinkageUnallocated
            : allocatedAmount < baselineAmount ? LinkagePartial
            : LinkageFullyAllocated;

    /// <summary>付款单侧引用状态文案（显式展示已引用 / 未引用金额；未引用部分绝不猜测到任何发票）</summary>
    public static string PaymentLinkageText(
        decimal paymentAmount, decimal allocatedAmount, int allocationCount, string? currency)
    {
        var cur = CurrencyAmountRules.NormalizeCurrency(currency);
        var unallocated = paymentAmount - allocatedAmount;
        if (unallocated < 0) unallocated = 0;

        return LinkageStatusOf(paymentAmount, allocatedAmount) switch
        {
            LinkageFullyAllocated => $"已全额引用：{allocatedAmount} {cur} 全部指向 {allocationCount} 张采购发票",
            LinkagePartial => $"部分引用：已引用 {allocatedAmount} {cur}，未引用 {unallocated} {cur}"
                + "（未引用部分不会被系统猜测到任何发票）",
            _ => $"未被引用：付款单金额 {paymentAmount} {cur} 尚未指向任何采购发票"
                + "（系统不按号码 / 金额 / 日期相似度猜测发票）"
        };
    }

    /// <summary>发票侧引用状态文案（显式展示已引用 / 未引用含税总额；未引用部分绝不猜测到任何付款单）</summary>
    public static string InvoiceLinkageText(
        decimal grossAmount, decimal allocatedAmount, int allocationCount, string? currency)
    {
        var cur = CurrencyAmountRules.NormalizeCurrency(currency);
        var unallocated = grossAmount - allocatedAmount;
        if (unallocated < 0) unallocated = 0;

        return LinkageStatusOf(grossAmount, allocatedAmount) switch
        {
            LinkageFullyAllocated => $"已全额引用：{allocatedAmount} {cur} 全部由 {allocationCount} 张付款单引用",
            LinkagePartial => $"部分引用：已引用 {allocatedAmount} {cur}，未引用 {unallocated} {cur}"
                + "（未引用部分不会被系统猜测到任何付款单）",
            _ => $"未被引用：含税总额 {grossAmount} {cur} 尚未被任何付款单引用"
                + "（系统不按号码 / 金额 / 日期相似度猜测付款单）"
        };
    }
}
