using ERP.Application.Common;
using ERP.Domain.Entities;
using ERP.Domain.Enums;

namespace ERP.Application.Services;

/// <summary>
/// 客户收款单 → 销售订单 收款引用（分摊）证据登记的纯规则（ERP-053，无数据库依赖，便于逐条单测）：
/// 状态机（有效 → 已作废）、金额与币种精度、收款单 / 客户 / 销售订单的权威资格判定与文案。
/// <para>边界：本规则只做**校验与计算**，不写库、不改写收款单与销售订单、不改写客户信用状态与信用额度、
/// 发票、库存与库存成本、库存流水、装柜与单证、佣金 / 回佣、费用或退税记录，也不执行任何收款、记账、
/// 核销、结算或催收动作。</para>
/// </summary>
public static class CustomerReceiptAllocationRules
{
    // ==================== 0. 口径常量 ====================

    /// <summary>状态：有效（形成收款引用证据，占用收款单金额额度）</summary>
    public const int StatusActive = 1;

    /// <summary>状态：已作废（保留原始值 / 客户 / 订单快照与作废原因，不物理删除、不重写）</summary>
    public const int StatusVoided = 2;

    /// <summary>关联状态：收款单金额尚未被任何有效引用行指向</summary>
    public const string LinkageUnallocated = "unallocated";

    /// <summary>关联状态：部分引用（有效行合计小于收款单金额）</summary>
    public const string LinkagePartial = "partial";

    /// <summary>关联状态：已全额引用（有效行合计等于收款单金额）</summary>
    public const string LinkageFullyAllocated = "fully_allocated";

    /// <summary>备注长度上限</summary>
    public const int MaxRemarkLength = 500;

    /// <summary>作废原因长度上限</summary>
    public const int MaxVoidReasonLength = 500;

    /// <summary>单张收款单最多保留的有效引用行数（保证视图与前端有界）</summary>
    public const int MaxAllocationsPerReceipt = 50;

    /// <summary>可引用收款单候选单次返回上限（有界，避免一次拉全表）</summary>
    public const int MaxReceiptCandidates = 200;

    /// <summary>可引用销售订单候选单次返回上限（有界，避免一次拉全表）</summary>
    public const int MaxOrderCandidates = 200;

    /// <summary>系统支持的币种（与销售订单 / 收款单币种枚举同源：CNY / USD / EUR / HKD / GBP / JPY）</summary>
    public static readonly string[] SupportedCurrencies = Enum.GetNames<Currency>();

    /// <summary>引用口径文案（接口、界面与文档同源）</summary>
    public const string RuleText =
        "引用口径：一行只能指向既有、未删除、未取消的销售订单，且订单客户与币种都必须与收款单一致；"
        + "引用金额必须大于 0（按币种精度取整）且同一收款单内有效行合计不得超过收款单金额；"
        + "同一张销售订单在同一张收款单内只能有一条有效引用行（重复提交一律拒绝）；"
        + "收款单可以部分或全部未被引用，系统不会按单号相似度、金额相近或日期接近猜测订单。";

    /// <summary>模块边界文案（明确不是到账凭证 / 应收台账 / 核销 / 税务 / 债务清偿）</summary>
    public const string BoundaryText =
        "本登记册只是客户收款单的引用证据：不是银行入账 / 到账凭证、不是应收账款台账或余额、不是货款核销、"
        + "不是客户对账单、不是税务（销项）判断，也不构成法律上的债务清偿或客户信用结论；"
        + "登记 / 作废引用行都不会真的收款、不会移动资金、不会把销售订单标记为已收款或已结清，"
        + "也不改写收款单与销售订单的任何既有字段。";

    // ==================== 1. 币种、金额与文本 ====================

    /// <summary>币种规范化 + 支持范围校验（收款单币种必须来自系统币种口径，才能与销售订单权威比对）</summary>
    public static string NormalizeCurrencyStrict(string? currency)
    {
        var value = CurrencyAmountRules.NormalizeCurrency(currency);
        if (!SupportedCurrencies.Contains(value, StringComparer.Ordinal))
            throw BusinessException.InvalidParameter(
                $"币种「{value}」不受支持：只允许 {string.Join(" / ", SupportedCurrencies)}"
                + "（收款单币种必须与所引用销售订单的币种一致，系统不做汇率换算）");
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

    /// <summary>收款单金额上限口径（按币种精度取整；用于有效行合计的上限比对）</summary>
    public static decimal AuthoritativeReceiptAmount(decimal receiptAmount, string? currency)
        => CurrencyAmountRules.RoundAmount(receiptAmount, currency);

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
    public static void EnsureVoidable(int status, string receiptNo, string orderNo)
    {
        if (status == StatusVoided)
            throw BusinessException.RuleConflict(
                $"收款单「{receiptNo}」→ 销售订单「{orderNo}」的引用行已是已作废状态，不能重复作废"
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

    /// <summary>销售订单状态文案（只读标注；订单不存在时照实说明「不存在或已删除」，不假定为可用）</summary>
    public static string OrderStatusText(int status) => status switch
    {
        (int)DocumentStatus.Pending => "待提交",
        (int)DocumentStatus.Submitted => "已提交",
        (int)DocumentStatus.Approved => "已审核",
        (int)DocumentStatus.Rejected => "已驳回",
        (int)DocumentStatus.Completed => "已完成",
        (int)DocumentStatus.Cancelled => "已取消",
        _ => $"未知（{status}）"
    };

    /// <summary>收款单可用性文案（历史证据照常可读，不可用时显式说明，不静默改成 0 或空）</summary>
    public static string ReceiptAvailabilityText(FinanceReceipt? receipt)
    {
        if (receipt is null || receipt.IsDeleted)
            return "收款单已删除（历史快照仍可读，不能用于新引用）";
        return "收款单可用";
    }

    /// <summary>收款单是否可用于新引用行（存在且未删除）</summary>
    public static bool IsReceiptSelectable(FinanceReceipt? receipt)
        => receipt is not null && !receipt.IsDeleted;

    /// <summary>客户可用性文案（历史证据照常可读，不可用时显式说明）</summary>
    public static string CustomerAvailabilityText(BaseCustomer? customer)
    {
        if (customer is null || customer.IsDeleted) return "客户已删除（历史快照仍可读）";
        if (customer.Status != 1) return "客户已停用（历史快照仍可读）";
        return "客户可用";
    }

    // ==================== 3. 销售订单资格与关联状态 ====================

    /// <summary>
    /// 销售订单能否被本收款单引用的判定（**不抛异常**，返回原因文案供界面逐行说明）：
    /// 订单存在且未删除、未取消、客户一致、币种一致（不做汇率换算）。
    /// </summary>
    public static (bool Eligible, string Text) EvaluateOrderEligibility(
        long receiptCustomerId, string? receiptCurrency, SalesOrder? order)
    {
        if (order is null || order.IsDeleted)
            return (false, "销售订单不存在或已删除，不能引用（历史引用仍可读）");

        if (order.Status == DocumentStatus.Cancelled)
            return (false, $"销售订单「{order.OrderNo}」已取消，不能引用（已取消订单不参与收款引用证据）");

        if (order.CustomerId != receiptCustomerId)
            return (false,
                $"销售订单「{order.OrderNo}」的客户（Id={order.CustomerId}）与收款单客户（Id={receiptCustomerId}）不一致，不能引用");

        var receiptCurrencyText = CurrencyAmountRules.NormalizeCurrency(receiptCurrency);
        var orderCurrencyText = CurrencyAmountRules.NormalizeCurrency(order.Currency.ToString());
        if (!string.Equals(orderCurrencyText, receiptCurrencyText, StringComparison.Ordinal))
            return (false,
                $"销售订单「{order.OrderNo}」的币种 {orderCurrencyText} 与收款单币种 {receiptCurrencyText} 不一致，不能引用（不做汇率换算）");

        return (true, $"可引用：客户与币种（{receiptCurrencyText}）均与收款单一致");
    }

    /// <summary>校验销售订单可引用（不存在 / 已删除 → 数据不存在；其余不可用原因 → 业务规则冲突）</summary>
    public static void EnsureOrderLinkable(long receiptCustomerId, string? receiptCurrency, SalesOrder? order)
    {
        var (eligible, text) = EvaluateOrderEligibility(receiptCustomerId, receiptCurrency, order);
        if (eligible) return;
        if (order is null || order.IsDeleted) throw BusinessException.NotFound(text);
        throw BusinessException.RuleConflict(text);
    }

    /// <summary>关联状态判定（依据有效持久化行合计；等于收款单金额时才是已全额引用）</summary>
    public static string LinkageStatusOf(decimal receiptAmount, decimal allocatedAmount)
        => allocatedAmount <= 0 ? LinkageUnallocated
            : allocatedAmount < receiptAmount ? LinkagePartial
            : LinkageFullyAllocated;

    /// <summary>关联状态文案（显式展示已引用 / 未引用金额；未引用部分绝不猜测到任何订单）</summary>
    public static string LinkageText(
        decimal receiptAmount, decimal allocatedAmount, int allocationCount, string? currency)
    {
        var cur = CurrencyAmountRules.NormalizeCurrency(currency);
        var unallocated = receiptAmount - allocatedAmount;
        if (unallocated < 0) unallocated = 0;

        return LinkageStatusOf(receiptAmount, allocatedAmount) switch
        {
            LinkageFullyAllocated => $"已全额引用：{allocatedAmount} {cur} 全部指向 {allocationCount} 张销售订单",
            LinkagePartial => $"部分引用：已引用 {allocatedAmount} {cur}，未引用 {unallocated} {cur}"
                + "（未引用部分不会被系统猜测到任何订单，也不代表应收未收余额）",
            _ => $"未被引用：收款单金额 {receiptAmount} {cur} 尚未指向任何销售订单"
                + "（系统不按单号 / 金额 / 日期相似度猜测订单）"
        };
    }

    /// <summary>收款单候选资格文案（收款单必须未删除且仍有可引用余额）</summary>
    public static (bool Eligible, string Text) EvaluateReceiptEligibility(
        FinanceReceipt? receipt, decimal allocatedAmount)
    {
        if (receipt is null || receipt.IsDeleted)
            return (false, "收款单不存在或已删除，不能登记新引用");

        var currency = CurrencyAmountRules.NormalizeCurrency(receipt.Currency.ToString());
        if (!SupportedCurrencies.Contains(currency, StringComparer.Ordinal))
            return (false, $"收款单币种「{currency}」不在系统币种口径内，不能登记引用（不做汇率换算）");

        var amount = AuthoritativeReceiptAmount(receipt.Amount, currency);
        if (amount <= 0)
            return (false, $"收款单金额为 {amount} {currency}，没有可引用的收款金额");

        var remaining = amount - allocatedAmount;
        if (remaining <= 0)
            return (false,
                $"收款单已被有效引用行占满（已引用 {allocatedAmount} / 收款金额 {amount} {currency}），不能再登记引用");

        return (true, $"可引用：收款单状态 {ReceiptStatusText((int)receipt.Status)}，剩余可引用 {remaining} {currency}");
    }
}
