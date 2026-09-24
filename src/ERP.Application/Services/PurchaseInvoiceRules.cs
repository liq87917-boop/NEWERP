using ERP.Application.Common;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using System.Text;

namespace ERP.Application.Services;

/// <summary>
/// 供应商采购发票登记的纯规则（ERP-043，无数据库依赖，便于逐条单测）：
/// 发票类型（普票 / 专票 / 进口）与发票代码要求、发票号码 / 身份规范化、供应商可用性、
/// 到期日与付款条件（ERP-065：可选、显式、缺失即未知）、金额等式与币种精度、
/// 状态机（草稿 → 已登记 → 已作废）、关联状态与文案，
/// 以及「采购订单能否被本发票关联」的权威资格判定（供应商 + 币种一致、非取消）。
/// <para>边界：本规则只做**校验与计算**，不写库、不改写采购订单 / 库存 / 退税 / 付款 / 供应商数据，
/// 也不记账、不生成凭证或任何收付款 / 结算单。</para>
/// </summary>
public static class PurchaseInvoiceRules
{
    // ==================== 0. 口径常量 ====================

    /// <summary>普通发票</summary>
    public const string InvoiceTypeOrdinary = "普票";

    /// <summary>增值税专用发票（必须填写发票代码）</summary>
    public const string InvoiceTypeSpecial = "专票";

    /// <summary>进口发票 / 海关进口增值税专用缴款书（票面通常没有发票代码，代码可留空）</summary>
    public const string InvoiceTypeImport = "进口";

    /// <summary>支持的发票类型（超出范围一律拒绝，不做隐式兜底；客户销项发票与单证商业发票的类型不在此列）</summary>
    public static readonly string[] SupportedInvoiceTypes =
        { InvoiceTypeOrdinary, InvoiceTypeSpecial, InvoiceTypeImport };

    /// <summary>状态：草稿（可编辑、可维护关联，未形成登记证据）</summary>
    public const int StatusDraft = 0;

    /// <summary>状态：已登记（证据冻结：不可编辑、不可再改关联，保留可读）</summary>
    public const int StatusRecorded = 1;

    /// <summary>状态：已作废（保留身份 / 金额 / 关联 / 审计历史，不物理删除）</summary>
    public const int StatusVoided = 2;

    /// <summary>关联状态：已全额关联</summary>
    public const string LinkageLinked = "linked";

    /// <summary>关联状态：部分关联</summary>
    public const string LinkagePartial = "partial";

    /// <summary>关联状态：未关联</summary>
    public const string LinkageUnlinked = "unlinked";

    /// <summary>发票代码长度上限</summary>
    public const int MaxInvoiceCodeLength = 50;

    /// <summary>发票号码长度上限</summary>
    public const int MaxInvoiceNumberLength = 50;

    /// <summary>备注长度上限</summary>
    public const int MaxRemarkLength = 500;

    /// <summary>作废原因长度上限</summary>
    public const int MaxVoidReasonLength = 500;

    /// <summary>付款条件长度上限（ERP-065：付款条件是**有界文本快照**，不做任何解析与推算）</summary>
    public const int MaxPaymentTermsLength = 200;

    /// <summary>单张发票最多关联的采购订单条数（保证视图有界）</summary>
    public const int MaxAllocationsPerInvoice = 50;

    /// <summary>可关联采购订单候选单次返回上限（有界，避免一次拉全表）</summary>
    public const int MaxOrderCandidates = 200;

    /// <summary>系统支持的币种（与采购订单币种枚举同源：CNY / USD / EUR / HKD / GBP / JPY）</summary>
    public static readonly string[] SupportedCurrencies = Enum.GetNames<Currency>();

    /// <summary>金额等式口径文案（接口、界面与文档同源）</summary>
    public const string AmountEquationText =
        "金额等式（服务端权威校验）：含税总额（价税合计）= 不含税金额（净额）+ 税额；"
        + "三项均按币种精度四舍五入（JPY / KRW / VND / IDR 为 0 位小数，其余 2 位，0.5 进位）后必须严格相等，"
        + "且含税总额必须大于 0、税额不得为负；系统不按税率反推金额、不做汇率换算、不跨币种合并。";

    /// <summary>关联口径文案（接口、界面与文档同源）</summary>
    public const string LinkageRuleText =
        "关联口径：只能关联到既有、未删除、未取消的采购订单，且订单供应商与币种都必须与发票一致；"
        + "关联金额必须大于 0（按币种精度取整）且合计不超过发票含税总额，同一张采购订单在同一张发票内只能关联一次；"
        + "发票可以部分或全部未关联，系统不会按单号相似度、金额相近或开票日期接近猜测采购订单。";

    /// <summary>模块边界文案（明确不是应付账款台账 / 税务申报 / 付款授权）</summary>
    public const string BoundaryText =
        "本登记册只是供应商采购发票的运营证据台账：不是应付账款台账、不是税务申报系统、也不是付款授权机制；"
        + "登记 / 记录 / 作废发票都不会改写采购订单状态、到货进度与已收数量、库存与库存成本、退税记录、"
        + "供应商余额与付款状态，也不记账、不生成凭证 / 收款 / 付款 / 结算单。";

    // ==================== 1. 发票类型与身份 ====================

    /// <summary>发票类型规范化（必须显式且受支持；空值或未知类型一律拒绝）</summary>
    public static string NormalizeInvoiceType(string? invoiceType)
    {
        var value = (invoiceType ?? string.Empty).Trim();
        if (!SupportedInvoiceTypes.Contains(value, StringComparer.Ordinal))
            throw BusinessException.InvalidParameter(
                $"发票类型「{value}」不受支持：只允许 {string.Join(" / ", SupportedInvoiceTypes)}");
        return value;
    }

    /// <summary>是否专票（不抛异常：用于「是否需要发票代码」等只读判定）</summary>
    public static bool IsSpecialInvoice(string? invoiceType) =>
        string.Equals((invoiceType ?? string.Empty).Trim(), InvoiceTypeSpecial, StringComparison.Ordinal);

    /// <summary>该类型是否必须填写发票代码（专票必填；普票与进口票票面无代码时可留空）</summary>
    public static bool RequiresInvoiceCode(string? invoiceType) => IsSpecialInvoice(invoiceType);

    /// <summary>发票类型说明文案（接口、界面与文档同源；未知类型照实回显，不猜测、不兜底）</summary>
    public static string InvoiceTypeText(string? invoiceType)
    {
        var value = (invoiceType ?? string.Empty).Trim();
        return value switch
        {
            InvoiceTypeOrdinary => $"{InvoiceTypeOrdinary}（增值税普通发票）",
            InvoiceTypeSpecial => $"{InvoiceTypeSpecial}（增值税专用发票）",
            InvoiceTypeImport => $"{InvoiceTypeImport}（进口发票 / 海关进口增值税专用缴款书）",
            _ => value
        };
    }

    /// <summary>发票代码要求文案（专票必填；普票 / 进口票「票面无代码」时留空，不臆造代码）</summary>
    public static string InvoiceCodeRequirementText(string? invoiceType)
        => RequiresInvoiceCode(invoiceType)
            ? "专票必须填写发票代码"
            : $"{(invoiceType ?? string.Empty).Trim()} 可不填发票代码（票面无发票代码时留空；系统不臆造代码）";

    /// <summary>发票代码规范化（去空白 + 长度校验；专票必须非空，普票 / 进口票可为空）</summary>
    public static string NormalizeInvoiceCode(string? invoiceCode, string invoiceType)
    {
        var value = (invoiceCode ?? string.Empty).Trim();
        if (value.Length > MaxInvoiceCodeLength)
            throw BusinessException.InvalidParameter($"发票代码长度不能超过 {MaxInvoiceCodeLength} 个字符");
        if (RequiresInvoiceCode(invoiceType) && value.Length == 0)
            throw BusinessException.InvalidParameter(
                "专票必须填写发票代码（普票 / 进口票票面无代码时可留空）");
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
    /// 仅用于「同一供应商 + 同一发票类型 + 同一代码 / 号码」的**精确**判定，不做模糊匹配、不静默合并。
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

    /// <summary>币种规范化 + 支持范围校验（发票币种必须来自系统币种口径，才能与采购订单权威比对）</summary>
    public static string NormalizeCurrencyStrict(string? currency)
    {
        var value = CurrencyAmountRules.NormalizeCurrency(currency);
        if (!SupportedCurrencies.Contains(value, StringComparer.Ordinal))
            throw BusinessException.InvalidParameter(
                $"币种「{value}」不受支持：只允许 {string.Join(" / ", SupportedCurrencies)}"
                + "（发票币种必须与所关联采购订单的币种一致，系统不做汇率换算）");
        return value;
    }

    // ==================== 1.1 到期日与付款条件（ERP-065：可选、显式、缺失即未知） ====================

    /// <summary>到期日与付款条件口径文案（接口、界面与文档同源）</summary>
    public const string EvidenceTermsRuleText =
        "到期日与付款条件是用户显式登记的运营证据：到期日留空 = 未知，付款条件留空 = 未提供；"
        + "到期日与账期一律不推算：系统不会按供应商默认账期、付款条件文本、发票备注、历史发票或采购订单推算到期日，"
        + "也不据此判断逾期、账龄、现金折扣、付款义务或结算状态。";

    /// <summary>到期日未知文案（缺省一律「未知」，绝不用开票日期或任何推算值顶替）</summary>
    public const string UnknownDueDateText =
        "未知（未提供到期日：系统不按供应商默认账期、付款条件或备注推算）";

    /// <summary>付款条件未提供文案（缺省一律「未提供」，绝不回填供应商默认账期）</summary>
    public const string UnknownPaymentTermsText =
        "未提供（未知：不回填、不按供应商默认账期或备注推算）";

    /// <summary>
    /// 到期日规范化（可选、显式证据）：留空保持 <c>null</c>（未知，不做任何推算）；
    /// 填写则只保留日期部分，且不得早于开票日期（明显录入错误的到期日直接拒绝，不静默改写）。
    /// </summary>
    public static DateTime? NormalizeDueDate(DateTime? dueDate, DateTime invoiceDate)
    {
        if (dueDate is null) return null;

        var value = dueDate.Value.Date;
        if (value < invoiceDate.Date)
            throw BusinessException.InvalidParameter(
                $"到期日（{value:yyyy-MM-dd}）不能早于开票日期（{invoiceDate.Date:yyyy-MM-dd}）："
                + "到期日是用户显式提供的证据，留空表示未知（系统不按默认账期推算）");
        return value;
    }

    /// <summary>
    /// 付款条件规范化（可选、显式证据）：去首尾空白，长度 ≤ <see cref="MaxPaymentTermsLength"/>；
    /// 留空 = 未提供（绝不回填供应商默认账期，也不解析文本）。
    /// </summary>
    public static string NormalizePaymentTerms(string? paymentTerms)
    {
        var value = (paymentTerms ?? string.Empty).Trim();
        if (value.Length > MaxPaymentTermsLength)
            throw BusinessException.InvalidParameter(
                $"付款条件长度不能超过 {MaxPaymentTermsLength} 个字符（付款条件只作为有界文本证据保存，不参与任何计算）");
        return value;
    }

    /// <summary>到期日展示文案（未填写一律「未知」，绝不显示推算值或开票日期）</summary>
    public static string DueDateText(DateTime? dueDate)
        => dueDate is null ? UnknownDueDateText : $"{dueDate.Value:yyyy-MM-dd}（用户显式提供）";

    /// <summary>付款条件展示文案（未填写一律「未提供」，绝不回填供应商默认账期）</summary>
    public static string PaymentTermsText(string? paymentTerms)
    {
        var value = (paymentTerms ?? string.Empty).Trim();
        return value.Length == 0 ? UnknownPaymentTermsText : value;
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

    /// <summary>单个关联金额规范化（按币种精度取整；必须大于 0）</summary>
    public static decimal NormalizeAllocationAmount(decimal amount, string? currency)
    {
        if (amount < 0)
            throw BusinessException.InvalidParameter($"关联金额不能为负数（当前 {amount}）");

        var rounded = CurrencyAmountRules.RoundAmount(amount, currency);
        if (rounded <= 0)
            throw BusinessException.InvalidParameter(
                $"关联金额必须大于 0（当前 {amount}，按 {CurrencyAmountRules.NormalizeCurrency(currency)} 精度取整后为 {rounded}）");
        return rounded;
    }

    // ==================== 3. 状态机与文案 ====================

    /// <summary>状态文案</summary>
    public static string StatusText(int status) => status switch
    {
        StatusDraft => "草稿",
        StatusRecorded => "已登记",
        StatusVoided => "已作废",
        _ => throw BusinessException.InvalidParameter(
            $"发票状态只能是 {StatusDraft}（草稿）/ {StatusRecorded}（已登记）/ {StatusVoided}（已作废），收到 {status}")
    };

    /// <summary>状态过滤规范化（为空 = 不过滤）</summary>
    public static int? NormalizeStatusFilter(int? status)
    {
        if (status is null) return null;
        _ = StatusText(status.Value);
        return status;
    }

    /// <summary>是否为合法的关联状态取值</summary>
    public static bool IsSupportedLinkage(string? linkageStatus)
        => linkageStatus is LinkageLinked or LinkagePartial or LinkageUnlinked;

    /// <summary>关联状态过滤规范化（为空 = 不过滤；未知取值一律拒绝，不静默忽略筛选条件）</summary>
    public static string? NormalizeLinkageFilter(string? linkageStatus)
    {
        var value = (linkageStatus ?? string.Empty).Trim();
        if (value.Length == 0) return null;
        if (!IsSupportedLinkage(value))
            throw BusinessException.InvalidParameter(
                $"关联状态「{value}」不受支持：只允许 {LinkageLinked} / {LinkagePartial} / {LinkageUnlinked}");
        return value;
    }

    /// <summary>发票类型过滤规范化（为空 = 不过滤）</summary>
    public static string? NormalizeInvoiceTypeFilter(string? invoiceType)
    {
        var value = (invoiceType ?? string.Empty).Trim();
        return value.Length == 0 ? null : NormalizeInvoiceType(value);
    }

    /// <summary>发票必须处于草稿状态才能编辑或维护关联（已登记 / 已作废冻结，保留可读）</summary>
    public static void EnsureEditable(int status, string identity)
    {
        if (status == StatusVoided)
            throw BusinessException.RuleConflict(
                $"发票「{identity}」已作废：作废证据不可修改（如需重新登记请新建一条，历史证据保持可读）");
        if (status == StatusRecorded)
            throw BusinessException.RuleConflict(
                $"发票「{identity}」已登记：已登记证据不可修改或重新关联（如需更正请先作废，再登记新发票）");
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

    // ==================== 4. 关联状态、供应商可用性与采购订单资格 ====================

    /// <summary>关联状态判定（依据持久化关联行合计；已关联等于含税总额时才是全额关联）</summary>
    public static string LinkageStatusOf(decimal grossAmount, decimal linkedAmount)
        => linkedAmount <= 0 ? LinkageUnlinked
            : linkedAmount < grossAmount ? LinkagePartial
            : LinkageLinked;

    /// <summary>关联状态文案（显式展示已关联 / 未关联金额；未关联部分绝不猜测到任何订单）</summary>
    public static string LinkageText(decimal grossAmount, decimal linkedAmount, int allocationCount, string? currency)
    {
        var cur = CurrencyAmountRules.NormalizeCurrency(currency);
        var unlinked = grossAmount - linkedAmount;
        if (unlinked < 0) unlinked = 0;

        return LinkageStatusOf(grossAmount, linkedAmount) switch
        {
            LinkageLinked => $"已全额关联：{linkedAmount} {cur} 全部关联到 {allocationCount} 张采购订单",
            LinkagePartial => $"部分关联：已关联 {linkedAmount} {cur}，未关联 {unlinked} {cur}"
                + "（未关联部分不会被系统猜测到任何订单）",
            _ => $"未关联：含税总额 {grossAmount} {cur} 尚未关联到任何采购订单"
                + "（系统不按单号 / 金额 / 日期相似度猜测订单）"
        };
    }

    /// <summary>供应商可用性文案（历史证据照常可读，不可用时显式说明，不静默改成 0 或空）</summary>
    public static string SupplierAvailabilityText(BaseSupplier? supplier)
    {
        if (supplier is null || supplier.IsDeleted) return "供应商已删除（历史快照仍可读，不能用于新发票）";
        if (supplier.Status != 1) return "供应商已停用（历史快照仍可读，不能用于新发票）";
        return "供应商可用";
    }

    /// <summary>供应商是否可用于新发票 / 修改发票（存在、未删除且启用）</summary>
    public static bool IsSupplierSelectable(BaseSupplier? supplier)
        => supplier is not null && !supplier.IsDeleted && supplier.Status == 1;

    /// <summary>
    /// 采购订单能否被本发票关联的判定（**不抛异常**，返回原因文案供界面逐行说明）：
    /// 订单存在且未删除、未取消、供应商一致、币种一致（不做汇率换算）。
    /// </summary>
    public static (bool Eligible, string Text) EvaluateOrderEligibility(PurchaseInvoice invoice, PurchaseOrder? order)
    {
        ArgumentNullException.ThrowIfNull(invoice);

        if (order is null || order.IsDeleted)
            return (false, "采购订单不存在或已删除，不能关联（历史关联仍可读）");

        if (order.Status == DocumentStatus.Cancelled)
            return (false, $"采购订单「{order.OrderNo}」已取消，不能关联（已取消订单不参与发票证据关联）");

        if (order.SupplierId != invoice.SupplierId)
            return (false,
                $"采购订单「{order.OrderNo}」的供应商（Id={order.SupplierId}）与发票供应商（Id={invoice.SupplierId}）不一致，不能关联");

        var invoiceCurrency = CurrencyAmountRules.NormalizeCurrency(invoice.Currency);
        var orderCurrency = CurrencyAmountRules.NormalizeCurrency(order.Currency.ToString());
        if (!string.Equals(orderCurrency, invoiceCurrency, StringComparison.Ordinal))
            return (false,
                $"采购订单「{order.OrderNo}」的币种 {orderCurrency} 与发票币种 {invoiceCurrency} 不一致，不能关联（不做汇率换算）");

        return (true, $"可关联：供应商与币种（{invoiceCurrency}）均与发票一致");
    }

    /// <summary>校验采购订单可关联（不存在 / 已删除 → 数据不存在；其余不可用原因 → 业务规则冲突）</summary>
    public static void EnsureOrderLinkable(PurchaseInvoice invoice, PurchaseOrder? order)
    {
        var (eligible, text) = EvaluateOrderEligibility(invoice, order);
        if (eligible) return;
        if (order is null || order.IsDeleted) throw BusinessException.NotFound(text);
        throw BusinessException.RuleConflict(text);
    }

    /// <summary>备注规范化（去首尾空白并校验长度；超长直接拒绝，不静默截断）</summary>
    public static string NormalizeRemark(string? remark)
    {
        var value = (remark ?? string.Empty).Trim();
        if (value.Length > MaxRemarkLength)
            throw BusinessException.InvalidParameter($"备注长度不能超过 {MaxRemarkLength} 个字符");
        return value;
    }


}
