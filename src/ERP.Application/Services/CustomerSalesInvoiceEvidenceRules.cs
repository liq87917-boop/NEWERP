using ERP.Application.Common;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using System.Text;

namespace ERP.Application.Services;

/// <summary>
/// 客户销项发票证据登记的纯规则（ERP-055，无数据库依赖，便于逐条单测）：
/// 发票类型（普票 / 专票 / 出口发票）与发票代码要求、发票号码 / 身份规范化、客户可用性、
/// 金额等式与币种精度、状态机（草稿 → 已登记 → 已作废）、分摊状态与文案、
/// 「销售订单能否被本发票分摊」的权威资格判定（客户 + 币种一致、非取消），
/// 以及与单证中心商业发票的**显式、有界**交叉引用资格判定。
/// <para>边界：本规则只做**校验与计算**，不写库、不调用任何开票 / 税务接口、不改写销售订单 / 客户 / 单证 /
/// 收款引用 / 库存与库存成本 / 退税 / 费用 / 财务数据，也不记账、不生成凭证或任何收付款 / 结算单。</para>
/// </summary>
public static class CustomerSalesInvoiceEvidenceRules
{
    // ==================== 0. 口径常量 ====================

    /// <summary>普通发票（销项）</summary>
    public const string InvoiceTypeOrdinary = "普票";

    /// <summary>增值税专用发票（销项；必须填写发票代码）</summary>
    public const string InvoiceTypeSpecial = "专票";

    /// <summary>出口发票（出口业务口径；发票代码可选）</summary>
    public const string InvoiceTypeExport = "出口发票";

    /// <summary>支持的发票类型（超出范围一律拒绝，不做隐式兜底）</summary>
    public static readonly string[] SupportedInvoiceTypes =
        { InvoiceTypeOrdinary, InvoiceTypeSpecial, InvoiceTypeExport };

    /// <summary>状态：草稿（可编辑、可维护分摊，未形成登记证据）</summary>
    public const int StatusDraft = 0;

    /// <summary>状态：已登记（证据冻结：不可编辑、不可再改分摊，保留可读）</summary>
    public const int StatusRecorded = 1;

    /// <summary>状态：已作废（保留身份 / 金额 / 分摊 / 审计历史，不物理删除）</summary>
    public const int StatusVoided = 2;

    /// <summary>分摊状态：已全额分摊</summary>
    public const string LinkageLinked = "linked";

    /// <summary>分摊状态：部分分摊</summary>
    public const string LinkagePartial = "partial";

    /// <summary>分摊状态：未分摊</summary>
    public const string LinkageUnlinked = "unlinked";

    /// <summary>发票代码长度上限</summary>
    public const int MaxInvoiceCodeLength = 50;

    /// <summary>发票号码长度上限</summary>
    public const int MaxInvoiceNumberLength = 50;

    /// <summary>备注长度上限</summary>
    public const int MaxRemarkLength = 500;

    /// <summary>作废原因长度上限</summary>
    public const int MaxVoidReasonLength = 500;

    /// <summary>商业发票交叉引用文本长度上限（有界人工留痕，绝不用于自动匹配）</summary>
    public const int MaxCommercialInvoiceReferenceLength = 100;

    /// <summary>关键字长度上限（超长直接拒绝，避免全表模糊扫描）</summary>
    public const int MaxKeywordLength = 100;

    /// <summary>单张发票最多分摊的销售订单条数（保证视图与前端有界）</summary>
    public const int MaxAllocationsPerInvoice = 50;

    /// <summary>可分摊销售订单候选单次返回上限（有界，避免一次拉全表）</summary>
    public const int MaxOrderCandidates = 200;

    /// <summary>可交叉引用商业发票候选单次返回上限（有界）</summary>
    public const int MaxTradeDocumentCandidates = 200;

    /// <summary>系统支持的币种（与销售订单 / 收款单币种枚举同源：CNY / USD / EUR / HKD / GBP / JPY）</summary>
    public static readonly string[] SupportedCurrencies = Enum.GetNames<Currency>();

    // ==================== 0.1 口径文案（接口、界面与文档同源） ====================

    /// <summary>金额等式口径文案</summary>
    public const string AmountEquationText =
        "金额等式（服务端权威校验）：含税总额（价税合计）= 不含税金额（净额）+ 税额；"
        + "三项均按币种精度四舍五入（JPY / KRW / VND / IDR 为 0 位小数，其余 2 位，0.5 进位）后必须严格相等，"
        + "且含税总额必须大于 0、税额不得为负；系统不按税率反推金额、不做汇率换算、不跨币种合并。";

    /// <summary>分摊口径文案</summary>
    public const string LinkageRuleText =
        "分摊口径：只能分摊到既有、未删除、未取消的销售订单，且订单客户与币种都必须与发票一致；"
        + "分摊金额必须大于 0（按币种精度取整）且合计不超过发票含税总额，同一张销售订单在同一张发票内只能分摊一次；"
        + "发票可以部分或全部分摊，未分摊部分保留为未分摊金额，系统不会按单号相似度、金额相近或日期接近猜测销售订单。";

    /// <summary>与单证中心商业发票的分离口径文案</summary>
    public const string TradeDocumentSeparationText =
        "单证中心商业发票与本登记册刻意分离：单证中心的商业发票是出口报关用的单证快照，"
        + "本登记册登记的是账务 / 税务口径的发票证据；两者不会互相转换、替换或自动链接，"
        + "只允许显式、有界的交叉引用留痕（不读取单证金额、不参与金额派生、不改写单证）。";

    /// <summary>模块边界文案（明确不是开票系统 / 税务申报 / 应收账款台账 / 收款核销）</summary>
    public const string BoundaryText =
        "本登记册只是客户销项发票的运营证据台账：不是发票开具系统（不连税务局、不调用任何开票服务）、"
        + "不是税务申报与销项税金计算、不是应收账款台账或余额、不是收款核销，也不构成开票、纳税或债权结论；"
        + "登记 / 记录 / 作废都不会开具或作废真实发票、不会改写销售订单状态 / 出货进度 / 金额与明细、"
        + "客户信用状态、收款单与其引用行、库存与库存成本、装柜与单证、佣金 / 回佣、费用与退税记录，"
        + "也不记账、不生成凭证 / 收款 / 付款 / 结算单。";

    // ==================== 1. 发票类型与身份 ====================

    /// <summary>发票类型规范化（必须显式且受支持；空值或未知类型一律拒绝）</summary>
    public static string NormalizeInvoiceType(string? invoiceType)
    {
        var value = (invoiceType ?? string.Empty).Trim();
        if (!SupportedInvoiceTypes.Contains(value, StringComparer.Ordinal))
            throw BusinessException.InvalidParameter(
                $"发票类型「{value}」不受支持：只允许 {string.Join(" / ", SupportedInvoiceTypes)}"
                + "（系统不按发票代码或号码猜测类型）");
        return value;
    }

    /// <summary>是否专票（不抛异常：用于「是否需要发票代码」等只读判定）</summary>
    public static bool IsSpecialInvoice(string? invoiceType) =>
        string.Equals((invoiceType ?? string.Empty).Trim(), InvoiceTypeSpecial, StringComparison.Ordinal);

    /// <summary>该类型是否必须填写发票代码（专票必填；普票与出口发票可不填）</summary>
    public static bool RequiresInvoiceCode(string? invoiceType) => IsSpecialInvoice(invoiceType);

    /// <summary>发票代码规范化（去空白 + 长度校验；专票必须非空，普票 / 出口发票可为空）</summary>
    public static string NormalizeInvoiceCode(string? invoiceCode, string invoiceType)
    {
        var value = (invoiceCode ?? string.Empty).Trim();
        if (value.Length > MaxInvoiceCodeLength)
            throw BusinessException.InvalidParameter($"发票代码长度不能超过 {MaxInvoiceCodeLength} 个字符");
        if (RequiresInvoiceCode(invoiceType) && value.Length == 0)
            throw BusinessException.InvalidParameter("专票必须填写发票代码（普票 / 出口发票可不填）");
        return value;
    }

    /// <summary>发票号码规范化（必填 + 长度校验；一张发票登记一条，不支持多号码拼接）</summary>
    public static string NormalizeInvoiceNumber(string? invoiceNumber)
    {
        var value = (invoiceNumber ?? string.Empty).Trim();
        if (value.Length == 0) throw BusinessException.InvalidParameter("请填写发票号码");
        if (value.Length > MaxInvoiceNumberLength)
            throw BusinessException.InvalidParameter($"发票号码长度不能超过 {MaxInvoiceNumberLength} 个字符");
        return value;
    }

    /// <summary>
    /// 身份要素规范化（唯一性判定用）：去掉所有空白与连字符后大写。
    /// 仅用于「同一客户 + 同一发票类型 + 同一代码 / 号码」的**精确**判定，不做模糊匹配、不静默合并。
    /// </summary>
    public static string NormalizeIdentityPart(string? value)
    {
        var raw = (value ?? string.Empty).Trim();
        if (raw.Length == 0) return string.Empty;

        var builder = new StringBuilder(raw.Length);
        foreach (var ch in raw)
        {
            if (char.IsWhiteSpace(ch) || ch is '-' or '－' or '—' or '_') continue;
            builder.Append(char.ToUpperInvariant(ch));
        }
        return builder.ToString();
    }

    /// <summary>对外身份文案（用于提示与台账显示；与唯一性判定口径一致）</summary>
    public static string IdentityText(string? invoiceType, string? invoiceCode, string? invoiceNumber)
    {
        var type = (invoiceType ?? string.Empty).Trim();
        var code = (invoiceCode ?? string.Empty).Trim();
        var number = (invoiceNumber ?? string.Empty).Trim();
        return string.IsNullOrEmpty(code) ? $"{type} {number}".Trim() : $"{type} {code}-{number}";
    }

    /// <summary>币种规范化 + 支持范围校验（发票币种必须来自系统币种口径，才能与销售订单权威比对）</summary>
    public static string NormalizeCurrencyStrict(string? currency)
    {
        var value = CurrencyAmountRules.NormalizeCurrency(currency);
        if (!SupportedCurrencies.Contains(value, StringComparer.Ordinal))
            throw BusinessException.InvalidParameter(
                $"币种「{value}」不受支持：只允许 {string.Join(" / ", SupportedCurrencies)}"
                + "（发票币种必须与所分摊销售订单的币种一致，系统不做汇率换算）");
        return value;
    }

    // ==================== 2. 金额等式与币种精度 ====================

    /// <summary>
    /// 金额等式校验（服务端权威）：三项均为原币且不得为负；按币种精度取整后必须满足
    /// <c>含税总额 = 不含税金额 + 税额</c> 且含税总额大于 0；返回取整后的三元组（写入即取整值）。
    /// </summary>
    public static (decimal Net, decimal Tax, decimal Gross) ValidateAmounts(
        decimal netAmount, decimal taxAmount, decimal grossAmount, string? currency)
    {
        if (netAmount < 0 || taxAmount < 0 || grossAmount < 0)
            throw BusinessException.InvalidParameter(
                $"金额不能为负数：不含税金额 {netAmount} / 税额 {taxAmount} / 含税总额 {grossAmount}");

        var net = CurrencyAmountRules.RoundAmount(netAmount, currency);
        var tax = CurrencyAmountRules.RoundAmount(taxAmount, currency);
        var gross = CurrencyAmountRules.RoundAmount(grossAmount, currency);
        var normalized = CurrencyAmountRules.NormalizeCurrency(currency);

        if (gross <= 0)
            throw BusinessException.InvalidParameter(
                $"含税总额必须大于 0（当前按 {normalized} 精度取整后为 {gross}）");

        if (gross != net + tax)
            throw BusinessException.InvalidParameter(
                $"金额等式不成立：含税总额 {gross} ≠ 不含税金额 {net} + 税额 {tax}"
                + $"（差额 {gross - net - tax} {normalized}）；请按实际发票金额填写（系统不按税率反推、不自动调整差额）");

        return (net, tax, gross);
    }

    /// <summary>单个分摊金额规范化（按币种精度取整；必须大于 0）</summary>
    public static decimal NormalizeAllocationAmount(decimal amount, string? currency)
    {
        if (amount < 0)
            throw BusinessException.InvalidParameter($"分摊金额不能为负数（当前 {amount}）");

        var rounded = CurrencyAmountRules.RoundAmount(amount, currency);
        if (rounded <= 0)
            throw BusinessException.InvalidParameter(
                $"分摊金额必须大于 0（当前 {amount}，按 {CurrencyAmountRules.NormalizeCurrency(currency)} 精度取整后为 {rounded}）");
        return rounded;
    }

    // ==================== 3. 文本、状态机与文案 ====================

    /// <summary>备注规范化（去首尾空白并校验长度；超长直接拒绝，不静默截断）</summary>
    public static string NormalizeRemark(string? remark)
    {
        var value = (remark ?? string.Empty).Trim();
        if (value.Length > MaxRemarkLength)
            throw BusinessException.InvalidParameter($"备注长度不能超过 {MaxRemarkLength} 个字符");
        return value;
    }

    /// <summary>商业发票交叉引用文本规范化（去首尾空白 + 长度校验；超长直接拒绝，不静默截断）</summary>
    public static string NormalizeCommercialInvoiceReference(string? reference)
    {
        var value = (reference ?? string.Empty).Trim();
        if (value.Length > MaxCommercialInvoiceReferenceLength)
            throw BusinessException.InvalidParameter(
                $"商业发票引用文本长度不能超过 {MaxCommercialInvoiceReferenceLength} 个字符"
                + "（该文本只作为人工留痕，系统不会据此自动匹配或链接单证）");
        return value;
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
            $"发票状态只能是 {StatusDraft}（草稿）/ {StatusRecorded}（已登记）/ {StatusVoided}（已作废），收到 {status}")
    };

    /// <summary>状态过滤规范化（为空 = 不过滤；未知取值一律拒绝，不静默忽略筛选条件）</summary>
    public static int? NormalizeStatusFilter(int? status)
    {
        if (status is null) return null;
        _ = StatusText(status.Value);
        return status;
    }

    /// <summary>是否为合法的分摊状态取值</summary>
    public static bool IsSupportedLinkage(string? linkageStatus)
        => linkageStatus is LinkageLinked or LinkagePartial or LinkageUnlinked;

    /// <summary>分摊状态过滤规范化（为空 = 不过滤；未知取值一律拒绝，不静默忽略筛选条件）</summary>
    public static string? NormalizeLinkageFilter(string? linkageStatus)
    {
        var value = (linkageStatus ?? string.Empty).Trim();
        if (value.Length == 0) return null;
        if (!IsSupportedLinkage(value))
            throw BusinessException.InvalidParameter(
                $"分摊状态「{value}」不受支持：只允许 {LinkageLinked} / {LinkagePartial} / {LinkageUnlinked}");
        return value;
    }

    /// <summary>发票类型过滤规范化（为空 = 不过滤）</summary>
    public static string? NormalizeInvoiceTypeFilter(string? invoiceType)
    {
        var value = (invoiceType ?? string.Empty).Trim();
        return value.Length == 0 ? null : NormalizeInvoiceType(value);
    }

    /// <summary>发票必须处于草稿状态才能编辑或维护分摊（已登记 / 已作废冻结，保留可读）</summary>
    public static void EnsureEditable(int status, string identity)
    {
        if (status == StatusVoided)
            throw BusinessException.RuleConflict(
                $"发票「{identity}」已作废：作废证据不可修改（如需重新登记请新建一条，历史证据保持可读）");
        if (status == StatusRecorded)
            throw BusinessException.RuleConflict(
                $"发票「{identity}」已登记：已登记证据不可修改或重新分摊（如需更正请先作废，再登记新发票）");
    }

    /// <summary>发票必须处于草稿状态才能登记（重复登记被拒绝）</summary>
    public static void EnsureRecordable(int status, string identity)
    {
        if (status == StatusRecorded)
            throw BusinessException.RuleConflict($"发票「{identity}」已登记，不能重复登记");
        if (status == StatusVoided)
            throw BusinessException.RuleConflict($"发票「{identity}」已作废，不能登记（作废证据不可恢复为已登记）");
    }

    /// <summary>发票可以作废（已作废拒绝重复作废；草稿与已登记都可作废一次）</summary>
    public static void EnsureVoidable(int status, string identity)
    {
        if (status == StatusVoided)
            throw BusinessException.RuleConflict($"发票「{identity}」已是已作废状态，不能重复作废");
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

    // ==================== 4. 分摊状态、客户可用性与销售订单资格 ====================

    /// <summary>分摊状态判定（依据持久化有效分摊行合计；等于含税总额时才是全额分摊）</summary>
    public static string LinkageStatusOf(decimal grossAmount, decimal linkedAmount)
        => linkedAmount <= 0 ? LinkageUnlinked
            : linkedAmount < grossAmount ? LinkagePartial
            : LinkageLinked;

    /// <summary>分摊状态文案（显式展示已分摊 / 未分摊金额；未分摊部分绝不猜测到任何订单）</summary>
    public static string LinkageText(decimal grossAmount, decimal linkedAmount, int allocationCount, string? currency)
    {
        var cur = CurrencyAmountRules.NormalizeCurrency(currency);
        var unlinked = grossAmount - linkedAmount;
        if (unlinked < 0) unlinked = 0;

        return LinkageStatusOf(grossAmount, linkedAmount) switch
        {
            LinkageLinked => $"已全额分摊：{linkedAmount} {cur} 全部分摊到 {allocationCount} 张销售订单",
            LinkagePartial => $"部分分摊：已分摊 {linkedAmount} {cur}，未分摊 {unlinked} {cur}"
                + "（未分摊部分不会被系统猜测到任何订单，也不代表应收未收余额或未开票税金）",
            _ => $"未分摊：含税总额 {grossAmount} {cur} 尚未分摊到任何销售订单"
                + "（系统不按单号 / 金额 / 日期相似度猜测订单）"
        };
    }

    /// <summary>客户可用性文案（历史证据照常可读，不可用时显式说明，不静默改成 0 或空）</summary>
    public static string CustomerAvailabilityText(BaseCustomer? customer)
    {
        if (customer is null || customer.IsDeleted) return "客户已删除（历史快照仍可读，不能用于新发票）";
        if (customer.Status != 1) return "客户已停用（历史快照仍可读，不能用于新发票）";
        return "客户可用";
    }

    /// <summary>客户是否可用于新发票 / 修改发票（存在、未删除且启用）</summary>
    public static bool IsCustomerSelectable(BaseCustomer? customer)
        => customer is not null && !customer.IsDeleted && customer.Status == 1;

    /// <summary>
    /// 销售订单能否被本发票分摊的判定（**不抛异常**，返回原因文案供界面逐行说明）：
    /// 订单存在且未删除、未取消、客户一致、币种一致（不做汇率换算）。
    /// </summary>
    public static (bool Eligible, string Text) EvaluateOrderEligibility(
        long customerId, string? currency, SalesOrder? order)
    {
        if (order is null || order.IsDeleted)
            return (false, "销售订单不存在或已删除，不能分摊（历史分摊仍可读）");

        if (order.Status == DocumentStatus.Cancelled)
            return (false, $"销售订单「{order.OrderNo}」已取消，不能分摊（已取消订单不参与发票证据分摊）");

        if (order.CustomerId != customerId)
            return (false,
                $"销售订单「{order.OrderNo}」的客户（Id={order.CustomerId}）与发票客户（Id={customerId}）不一致，不能分摊");

        var invoiceCurrency = CurrencyAmountRules.NormalizeCurrency(currency);
        var orderCurrency = CurrencyAmountRules.NormalizeCurrency(order.Currency.ToString());
        if (!string.Equals(orderCurrency, invoiceCurrency, StringComparison.Ordinal))
            return (false,
                $"销售订单「{order.OrderNo}」的币种 {orderCurrency} 与发票币种 {invoiceCurrency} 不一致，不能分摊（不做汇率换算）");

        return (true, $"可分摊：客户与币种（{invoiceCurrency}）均与发票一致");
    }

    /// <summary>校验销售订单可分摊（不存在 / 已删除 → 数据不存在；其余不可用原因 → 业务规则冲突）</summary>
    public static void EnsureOrderLinkable(long customerId, string? currency, SalesOrder? order)
    {
        var (eligible, text) = EvaluateOrderEligibility(customerId, currency, order);
        if (eligible) return;
        if (order is null || order.IsDeleted) throw BusinessException.NotFound(text);
        throw BusinessException.RuleConflict(text);
    }

    // ==================== 5. 单证中心商业发票的**显式**交叉引用资格（只作为证据） ====================

    /// <summary>
    /// 显式交叉引用的单证资格判定（**不抛异常**）：单证必须存在、未删除且类型为商业发票
    /// （权威常量 <see cref="TradeDocumentItemRules.CommercialInvoiceDocType"/>）。
    /// <para>判定<strong>不</strong>读取单证金额、<strong>不</strong>校验客户 / 币种一致（单证快照与账务发票本就可能不同口径），
    /// 引用仅作留痕；不满足时拒绝写入，绝不静默忽略或替换成其它单证。</para>
    /// </summary>
    public static (bool Eligible, string Text) EvaluateTradeDocumentEligibility(TradeDocument? document)
    {
        if (document is null || document.IsDeleted)
            return (false, "单证不存在或已删除，不能作为交叉引用来源（历史引用仍可读）");

        var docType = (document.DocType ?? string.Empty).Trim();
        if (!string.Equals(docType, TradeDocumentItemRules.CommercialInvoiceDocType, StringComparison.Ordinal))
            return (false,
                $"单证「{document.DocNo}」的类型是「{docType}」，不是商业发票，不能作为交叉引用来源"
                + "（本登记册只接受商业发票的显式交叉引用，且不转换、不替换任何单证）");

        return (true, $"可引用：单证中心商业发票「{document.DocNo}」只作为有界证据留痕（不读取金额、不参与金额派生）");
    }

    /// <summary>校验单证可作为显式交叉引用（不存在 / 已删除 → 数据不存在；类型不符 → 业务规则冲突）</summary>
    public static void EnsureTradeDocumentReferenceable(TradeDocument? document)
    {
        var (eligible, text) = EvaluateTradeDocumentEligibility(document);
        if (eligible) return;
        if (document is null || document.IsDeleted) throw BusinessException.NotFound(text);
        throw BusinessException.RuleConflict(text);
    }

    /// <summary>交叉引用可用性文案（未引用 / 单证被删除时都照实说明，历史证据照常可读）</summary>
    public static string TradeDocumentAvailabilityText(long? tradeDocumentId, TradeDocument? document)
    {
        if (tradeDocumentId is null) return "未引用单证中心单证（本登记册不建立自动链接）";
        if (document is null || document.IsDeleted) return "被引用单证已删除或不存在（历史引用快照仍可读）";
        return $"被引用单证可用（{document.DocType}）";
    }
}
