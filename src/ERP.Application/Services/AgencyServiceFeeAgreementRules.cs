using ERP.Application.Common;
using ERP.Domain.Entities;

namespace ERP.Application.Services;

/// <summary>
/// 代理服务费协议证据登记的纯规则（ERP-069，无数据库依赖，便于逐条单测）：
/// 协议号与身份规范化、客户可用性、生效日期区间、币种口径、披露的计费方式（比例费率 / 固定金额）与
/// 必填条款校验、状态机（草稿 → 已登记 → 已作废）、状态与筛选取值校验，以及接口 / 界面 / 文档同源的口径文案。
/// <para>关键口径：费用条款**只来自用户显式提交的值**；本规则<strong>不</strong>读取也不推断业务员提成设置
/// （系统参数 <c>SalesCommissionRate</c>）、客户 / 供应商主数据比例、历史订单、自由文本、客户默认值或金额相似度，
/// 也不做任何汇率换算、不做跨币种合并、不做静默归一化。</para>
/// <para>边界：本规则只做**校验与计算**，不写库、不开发票、不记账、不授权付款、不提供法律意见，
/// 也不改写客户 / 销售订单 / 单证 / 收款 / 发票 / 库存 / 费用 / 退税 / 结算等任何既有记录。</para>
/// </summary>
public static class AgencyServiceFeeAgreementRules
{
    // ==================== 0. 口径常量 ====================

    /// <summary>计费方式：比例费率（按协议约定的有界计费依据，按费率百分比计费）</summary>
    public const string FeeMethodRate = "比例费率";

    /// <summary>计费方式：固定金额（按协议约定的有界计费依据，收取固定金额）</summary>
    public const string FeeMethodFixed = "固定金额";

    /// <summary>支持的计费方式（超出范围一律拒绝，不做隐式兜底、不猜测商业含义）</summary>
    public static readonly string[] SupportedFeeMethods = { FeeMethodRate, FeeMethodFixed };

    /// <summary>状态：草稿（可编辑，尚未形成登记证据）</summary>
    public const int StatusDraft = 0;

    /// <summary>状态：已登记（证据冻结：不可编辑，保留可读）</summary>
    public const int StatusRecorded = 1;

    /// <summary>状态：已作废（保留原始条款 / 客户快照 / 登记人与时间戳，不物理删除）</summary>
    public const int StatusVoided = 2;

    /// <summary>协议号长度上限</summary>
    public const int MaxAgreementNoLength = 50;

    /// <summary>计费依据说明长度上限（有界人工文本）</summary>
    public const int MaxFeeBasisLength = 200;

    /// <summary>备注长度上限</summary>
    public const int MaxRemarkLength = 500;

    /// <summary>作废原因长度上限</summary>
    public const int MaxVoidReasonLength = 500;

    /// <summary>登记人长度上限</summary>
    public const int MaxRecordedByLength = 100;

    /// <summary>关键字长度上限（超长直接拒绝，避免全表模糊扫描）</summary>
    public const int MaxKeywordLength = 100;

    /// <summary>费率百分比上限（含）</summary>
    public const decimal MaxRatePercent = 100m;

    /// <summary>费率小数位（与 <c>ErpDbContext</c> 的 DECIMAL(9,4) 口径一致，0.5 进位）</summary>
    public const int RateDecimals = 4;

    /// <summary>系统支持的币种（与销售订单 / 收款单 / 发票币种枚举同源：CNY / USD / EUR / HKD / GBP / JPY）</summary>
    public static readonly string[] SupportedCurrencies = Enum.GetNames<Domain.Enums.Currency>();

    // ==================== 0.1 口径文案（接口、界面与文档同源） ====================

    /// <summary>费用条款口径文案</summary>
    public const string FeeTermsRuleText =
        "费用条款口径（服务端权威校验）：费率 / 固定金额 / 计费依据说明都只来自授权用户**显式提交**的值 —— "
        + "比例费率必须填写大于 0 且不超过 100 的费率（保留 4 位小数，0.5 进位），固定金额必须按币种精度取整后大于 0；"
        + "两种口径不得同时填写，不兼容组合一律拒绝且不做静默归一化；系统不会从业务员提成设置（SalesCommissionRate）、"
        + "客户 / 供应商主数据比例、历史订单、自由文本、客户默认值或金额相似度推断任何条款，也不做汇率换算。";

    /// <summary>与业务员提成报表的分离口径文案</summary>
    public const string CommissionSeparationText =
        "与业务员提成报表刻意分离：业务员提成表（/api/reports/sales-commission，比例取自系统参数 SalesCommissionRate、"
        + "按毛利计算）与客户佣金 / 回佣比例（BaseCustomer.CommissionRatio）、订单佣金比例快照（SalesOrder.CommissionRatio）、"
        + "供应商返点比例（BaseSupplier.RebateRatio）都是**各自独立的模型**；本协议证据既不读取、也不改写它们，"
        + "更不会被它们派生或覆盖（登记 / 作废协议不会改变任何提成报表结果）。";

    /// <summary>模块边界文案（明确不是发票 / 记账 / 付款授权 / 法律意见 / 交付或收付款证明）</summary>
    public const string BoundaryText =
        "本登记册只是客户代理服务费的**仓库内商业条款证据**：不是税务发票、不是会计凭证或记账分录、"
        + "不是付款授权或资金指令、不是法律意见，也不是服务已交付或已收付款的证明；"
        + "登记 / 记录 / 作废都不会开具或作废任何发票、不会记账或生成凭证 / 收款 / 付款 / 结算单、"
        + "不会发起任何付款，也不改写客户主数据（含佣金比例与信用状态）、销售订单（含佣金比例与金额）、"
        + "装柜与单证、收款单及其引用行、销项发票证据、库存与库存成本、费用与退税记录；"
        + "系统不提供任何「不披露 / 账外 / 隐匿佣金」的字段或流程。";

    /// <summary>历史证据只读口径文案</summary>
    public const string HistoricalEvidenceText =
        "历史证据只读：客户停用 / 删除 / 改名后，历史协议证据仍按登记当时的客户编码与名称快照可读；"
        + "更正走显式作废（原因必填），保留原始条款、客户快照、登记人与时间戳，不提供硬删除与静默改写。";

    // ==================== 1. 协议身份与客户 ====================

    /// <summary>协议号规范化（必填 + 去首尾空白 + 长度校验；不自动发号、不做格式猜测）</summary>
    public static string NormalizeAgreementNo(string? agreementNo)
    {
        var value = (agreementNo ?? string.Empty).Trim();
        if (value.Length == 0)
            throw BusinessException.InvalidParameter("请填写协议号（按真实协议填写，系统不自动发号）");
        if (value.Length > MaxAgreementNoLength)
            throw BusinessException.InvalidParameter($"协议号长度不能超过 {MaxAgreementNoLength} 个字符");
        return value;
    }

    /// <summary>
    /// 身份要素规范化（唯一性判定用）：去掉所有空白与连字符 / 下划线后大写。
    /// 仅用于「同一客户 + 同一规范化协议号」的**精确**判定，不做模糊匹配、不静默合并。
    /// </summary>
    public static string NormalizeIdentityPart(string? value)
    {
        var raw = (value ?? string.Empty).Trim();
        if (raw.Length == 0) return string.Empty;

        var builder = new System.Text.StringBuilder(raw.Length);
        foreach (var ch in raw)
        {
            if (char.IsWhiteSpace(ch) || ch is '-' or '－' or '—' or '_') continue;
            builder.Append(char.ToUpperInvariant(ch));
        }
        return builder.ToString();
    }

    /// <summary>对外身份文案（用于提示与台账显示；与唯一性判定口径一致）</summary>
    public static string IdentityText(string? agreementNo)
    {
        var value = (agreementNo ?? string.Empty).Trim();
        return value.Length == 0 ? "(未填协议号)" : value;
    }

    /// <summary>客户可用性文案（历史证据照常可读，不可用时显式说明，不静默改成 0 或空）</summary>
    public static string CustomerAvailabilityText(BaseCustomer? customer)
    {
        if (customer is null || customer.IsDeleted) return "客户已删除（历史快照仍可读，不能用于新协议）";
        if (customer.Status != 1) return "客户已停用（历史快照仍可读，不能用于新协议）";
        return "客户可用";
    }

    /// <summary>客户是否可用于新协议 / 修改协议（存在、未删除且启用）</summary>
    public static bool IsCustomerSelectable(BaseCustomer? customer)
        => customer is not null && !customer.IsDeleted && customer.Status == 1;

    // ==================== 2. 生效区间、币种与计费方式 ====================

    /// <summary>
    /// 生效日期区间校验：起始日期必填（由用户显式填写），结束日期可空 = **无固定结束日**；
    /// 填写了结束日期时不得早于起始日期（不做静默调整，也不猜测续签日期）。
    /// </summary>
    public static (DateTime From, DateTime? To) ValidateEffectiveRange(DateTime? effectiveFrom, DateTime? effectiveTo)
    {
        if (effectiveFrom is null)
            throw BusinessException.InvalidParameter("请填写生效起始日期（系统不按当天、订单或参数推算协议生效日）");

        var from = effectiveFrom.Value.Date;
        if (effectiveTo is null) return (from, null);

        var to = effectiveTo.Value.Date;
        if (to < from)
            throw BusinessException.InvalidParameter(
                $"生效结束日期 {to:yyyy-MM-dd} 不能早于生效起始日期 {from:yyyy-MM-dd}"
                + "（如协议无固定结束日请留空，系统不会自动补一个日期）");
        return (from, to);
    }

    /// <summary>生效区间文案（无结束日时显式说明「无固定结束日」，不猜测日期）</summary>
    public static string EffectiveRangeText(DateTime from, DateTime? to)
        => to is null
            ? $"{from:yyyy-MM-dd} 起，无固定结束日（留空 = 未提供结束日期）"
            : $"{from:yyyy-MM-dd} ~ {to.Value:yyyy-MM-dd}";

    /// <summary>币种规范化 + 支持范围校验（币种必须来自系统币种口径，金额不做汇率换算）</summary>
    public static string NormalizeCurrencyStrict(string? currency)
    {
        var value = CurrencyAmountRules.NormalizeCurrency(currency);
        if (!SupportedCurrencies.Contains(value, StringComparer.Ordinal))
            throw BusinessException.InvalidParameter(
                $"币种「{value}」不受支持：只允许 {string.Join(" / ", SupportedCurrencies)}"
                + "（金额一律以原币记录，系统不做汇率换算、不跨币种合并）");
        return value;
    }

    /// <summary>计费方式规范化（必须显式且受支持；空值或未知取值一律拒绝）</summary>
    public static string NormalizeFeeMethod(string? feeMethod)
    {
        var value = (feeMethod ?? string.Empty).Trim();
        if (!SupportedFeeMethods.Contains(value, StringComparer.Ordinal))
            throw BusinessException.InvalidParameter(
                $"计费方式「{value}」不受支持：只允许 {string.Join(" / ", SupportedFeeMethods)}"
                + "（系统不按费率值、金额或客户默认比例猜测计费方式）");
        return value;
    }

    /// <summary>计费方式过滤规范化（为空 = 不过滤；未知取值一律拒绝，不静默忽略筛选条件）</summary>
    public static string? NormalizeFeeMethodFilter(string? feeMethod)
    {
        var value = (feeMethod ?? string.Empty).Trim();
        return value.Length == 0 ? null : NormalizeFeeMethod(value);
    }

    /// <summary>是否为比例费率方式</summary>
    public static bool IsRateMethod(string? feeMethod)
        => string.Equals((feeMethod ?? string.Empty).Trim(), FeeMethodRate, StringComparison.Ordinal);

    /// <summary>
    /// 费用条款校验（服务端权威）：
    /// 比例费率 → 费率必填且 (0, 100]，不得同时填写固定金额；
    /// 固定金额 → 金额必填且按币种精度取整后大于 0，不得同时填写费率。
    /// 返回规范化后的 (费率, 固定金额) 二元组（写入即取整值），不兼容组合一律拒绝、不静默归一化。
    /// </summary>
    public static (decimal RatePercent, decimal FixedAmount) ValidateFeeTerms(
        string? feeMethod, decimal? ratePercent, decimal? fixedAmount, string? currency)
    {
        var method = NormalizeFeeMethod(feeMethod);
        var cur = CurrencyAmountRules.NormalizeCurrency(currency);
        var rate = ratePercent ?? 0m;
        var amount = fixedAmount ?? 0m;

        if (rate < 0)
            throw BusinessException.InvalidParameter($"费率不能为负数（当前 {rate}）");
        if (amount < 0)
            throw BusinessException.InvalidParameter($"固定金额不能为负数（当前 {amount}）");

        if (IsRateMethod(method))
        {
            if (amount != 0)
                throw BusinessException.InvalidParameter(
                    "计费方式为比例费率时不能同时填写固定金额：请只保留一种显式费用口径"
                    + "（系统不会自动取舍，也不会把金额折算成费率）");
            if (rate <= 0)
                throw BusinessException.InvalidParameter(
                    "比例费率必须填写大于 0 的费率（%）"
                    + "（系统不会从业务员提成设置、客户默认比例或历史订单推断费率）");
            if (rate > MaxRatePercent)
                throw BusinessException.InvalidParameter(
                    $"费率 {rate}% 超过上限 {MaxRatePercent}%：请按真实协议填写（系统不自动截断或归一化）");

            var roundedRate = Math.Round(rate, RateDecimals, MidpointRounding.AwayFromZero);
            if (roundedRate <= 0)
                throw BusinessException.InvalidParameter(
                    $"费率 {rate}% 按 {RateDecimals} 位小数取整后为 {roundedRate}，必须大于 0");

            return (roundedRate, 0m);
        }

        if (rate != 0)
            throw BusinessException.InvalidParameter(
                "计费方式为固定金额时不能同时填写费率：请只保留一种显式费用口径"
                + "（系统不会自动取舍，也不会把费率折算成金额）");

        var roundedAmount = CurrencyAmountRules.RoundAmount(amount, cur);
        if (roundedAmount <= 0)
            throw BusinessException.InvalidParameter(
                $"固定金额必须大于 0（当前 {amount}，按 {cur} 精度取整后为 {roundedAmount}）");

        return (0m, roundedAmount);
    }

    /// <summary>费用条款文案（只读展示：显式展示计费方式、条款值与币种口径）</summary>
    public static string FeeTermsText(string? feeMethod, decimal ratePercent, decimal fixedAmount, string? currency)
    {
        var cur = CurrencyAmountRules.NormalizeCurrency(currency);
        return IsRateMethod(feeMethod)
            ? $"比例费率 {ratePercent}%（按协议约定的计费依据计费，币种 {cur}；费率为用户显式提供，系统不推断）"
            : $"固定金额 {fixedAmount} {cur}（按协议约定的计费依据收取；金额为用户显式提供，系统不推断）";
    }

    // ==================== 3. 有界文本、状态机与文案 ====================

    /// <summary>计费依据说明规范化（必填 + 去首尾空白 + 长度校验；超长直接拒绝，不静默截断）</summary>
    public static string NormalizeFeeBasis(string? feeBasis)
    {
        var value = (feeBasis ?? string.Empty).Trim();
        if (value.Length == 0)
            throw BusinessException.InvalidParameter(
                "请填写计费依据说明（例如按出口发票金额 / 按订单 FOB 金额 / 按月固定；"
                + "该说明只作为人工留痕，系统不据此计算金额）");
        if (value.Length > MaxFeeBasisLength)
            throw BusinessException.InvalidParameter($"计费依据说明长度不能超过 {MaxFeeBasisLength} 个字符");
        return value;
    }

    /// <summary>备注规范化（去首尾空白并校验长度；超长直接拒绝，不静默截断）</summary>
    public static string NormalizeRemark(string? remark)
    {
        var value = (remark ?? string.Empty).Trim();
        if (value.Length > MaxRemarkLength)
            throw BusinessException.InvalidParameter($"备注长度不能超过 {MaxRemarkLength} 个字符");
        return value;
    }

    /// <summary>作废原因规范化（必填 + 长度校验：作废保留历史，必须记录更正原因）</summary>
    public static string NormalizeVoidReason(string? reason)
    {
        var value = (reason ?? string.Empty).Trim();
        if (value.Length == 0)
            throw BusinessException.InvalidParameter("请填写作废原因：作废会保留历史证据，必须记录更正原因");
        if (value.Length > MaxVoidReasonLength)
            throw BusinessException.InvalidParameter($"作废原因长度不能超过 {MaxVoidReasonLength} 个字符");
        return value;
    }

    /// <summary>登记人规范化（服务端按已认证身份写入，不由客户端提交；缺失记「未知用户」）</summary>
    public static string NormalizeRecordedBy(string? recordedBy)
    {
        var value = (recordedBy ?? string.Empty).Trim();
        if (value.Length == 0) return "未知用户";
        return value.Length > MaxRecordedByLength ? value[..MaxRecordedByLength] : value;
    }

    /// <summary>关键字规范化（去首尾空白 + 长度校验；超长直接拒绝，避免全表模糊扫描）</summary>
    public static string NormalizeKeyword(string? keyword)
    {
        var value = (keyword ?? string.Empty).Trim();
        if (value.Length > MaxKeywordLength)
            throw BusinessException.InvalidParameter($"关键字长度不能超过 {MaxKeywordLength} 个字符");
        return value;
    }

    /// <summary>状态文案</summary>
    public static string StatusText(int status) => status switch
    {
        StatusDraft => "草稿",
        StatusRecorded => "已登记",
        StatusVoided => "已作废",
        _ => throw BusinessException.InvalidParameter(
            $"协议状态只能是 {StatusDraft}（草稿）/ {StatusRecorded}（已登记）/ {StatusVoided}（已作废），收到 {status}")
    };

    /// <summary>状态过滤规范化（为空 = 不过滤；未知取值一律拒绝，不静默忽略筛选条件）</summary>
    public static int? NormalizeStatusFilter(int? status)
    {
        if (status is null) return null;
        _ = StatusText(status.Value);
        return status;
    }

    /// <summary>协议必须处于草稿状态才能编辑（已登记 / 已作废冻结，保留可读）</summary>
    public static void EnsureEditable(int status, string identity)
    {
        if (status == StatusVoided)
            throw BusinessException.RuleConflict(
                $"协议「{identity}」已作废：作废证据不可修改（如需重新登记请新建一条，历史证据保持可读）");
        if (status == StatusRecorded)
            throw BusinessException.RuleConflict(
                $"协议「{identity}」已登记：已登记证据不可修改（如需更正请先作废，再登记新协议）");
    }

    /// <summary>协议必须处于草稿状态才能登记（重复登记被拒绝）</summary>
    public static void EnsureRecordable(int status, string identity)
    {
        if (status == StatusRecorded)
            throw BusinessException.RuleConflict($"协议「{identity}」已登记，不能重复登记");
        if (status == StatusVoided)
            throw BusinessException.RuleConflict(
                $"协议「{identity}」已作废，不能登记（作废证据不可恢复为已登记）");
    }

    /// <summary>协议可以作废（已作废拒绝重复作废；草稿与已登记都可作废一次）</summary>
    public static void EnsureVoidable(int status, string identity)
    {
        if (status == StatusVoided)
            throw BusinessException.RuleConflict($"协议「{identity}」已是已作废状态，不能重复作废");
    }
}
