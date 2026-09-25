using ERP.Application.Common;
using ERP.Domain.Enums;

namespace ERP.Application.Services;

/// <summary>
/// 代理服务费对账单证据的纯规则（ERP-070，无数据库依赖，便于逐条单测）：
/// 对账单号与身份规范化、客户 / 协议资格、对账日期与**可选**到期日、服务期间、币种口径、
/// 显式来源类型 allowlist 与来源资格判定、行说明 / 计费基础 / 行金额校验、**服务端合计计算**、
/// 状态机（草稿 → 已登记 → 已作废）与筛选取值校验，以及接口 / 界面 / 文档同源的口径文案。
/// <para>关键口径：</para>
/// <list type="number">
/// <item>行只按 <b>来源类型 + 来源记录 Id（持久化标识符）</b>引用来源，<strong>绝不</strong>按单号文本 / 金额 /
/// 日期 / 相似度猜测链接；</item>
/// <item>行金额与计费基础数量**只来自授权用户显式提交**：本规则<strong>不</strong>读取协议费率、协议固定金额、
/// 客户账期 / 默认值、来源单据金额、自由文本或历史对账单来折算 / 补齐 / 猜测任何费用；</item>
/// <item>到期日留空 = <b>未知</b>：<strong>不</strong>按客户账期、协议文字、对账日期或历史单据推算；</item>
/// <item>合计由服务端按币种精度对**已校验行金额**求和，客户端提交的合计不被采信。</item>
/// </list>
/// <para>边界：本规则只做**校验与计算**，不写库、不开票、不记账、不生成凭证、不催收或联系客户、不收款或付款、
/// 也不改写协议、客户、订单、装柜清单、单证、发票、收款、库存、费用、退税与结算等任何既有记录。</para>
/// </summary>
public static class AgencyServiceFeeStatementRules
{
    // ==================== 0. 口径常量 ====================

    /// <summary>来源类型：销售订单（<c>SalesOrder</c>；来源自带客户与币种，两者都会被校验）</summary>
    public const string SourceTypeSalesOrder = "sales-order";

    /// <summary>来源类型：装柜清单（<c>ContainerLoadingList</c>；来源不携带币种列，币种校验不适用）</summary>
    public const string SourceTypeLoadingList = "loading-list";

    /// <summary>支持的来源类型（显式 allowlist；其它取值一律拒绝，不做隐式兜底、不猜测商业含义）</summary>
    public static readonly string[] SupportedSourceTypes = { SourceTypeSalesOrder, SourceTypeLoadingList };

    /// <summary>状态：草稿（可编辑，尚未形成登记证据；草稿行同样占用来源身份）</summary>
    public const int StatusDraft = 0;

    /// <summary>状态：已登记（证据冻结：不可编辑，保留可读；行随表头一起冻结）</summary>
    public const int StatusRecorded = 1;

    /// <summary>状态：已作废（保留原始行、来源与客户 / 协议快照、登记人与时间戳，不物理删除）</summary>
    public const int StatusVoided = 2;

    /// <summary>对账单号长度上限</summary>
    public const int MaxStatementNoLength = 50;

    /// <summary>行说明长度上限</summary>
    public const int MaxDescriptionLength = 200;

    /// <summary>计费基础说明长度上限</summary>
    public const int MaxBasisNoteLength = 200;

    /// <summary>备注长度上限</summary>
    public const int MaxRemarkLength = 500;

    /// <summary>作废原因长度上限</summary>
    public const int MaxVoidReasonLength = 500;

    /// <summary>登记人长度上限</summary>
    public const int MaxRecordedByLength = 100;

    /// <summary>关键字长度上限（超长直接拒绝，避免全表模糊扫描）</summary>
    public const int MaxKeywordLength = 100;

    /// <summary>单张对账单的行数上限（有界：一次提交 / 一次读取都不允许无界行数）</summary>
    public const int MaxLinesPerStatement = 200;

    /// <summary>来源候选单次返回上限（有界，避免一次拉全表）</summary>
    public const int MaxSourceOptions = 200;

    /// <summary>计费基础数量小数位（与 DECIMAL(18,4) 口径一致，0.5 进位）</summary>
    public const int BasisQuantityDecimals = 4;

    /// <summary>计费基础数量绝对值上限（有界；超限直接拒绝，不静默截断）</summary>
    public const decimal MaxBasisQuantity = 99999999999999.9999m;

    /// <summary>单行金额与合计上限（DECIMAL(18,2) 口径的上界；超限直接拒绝，不静默截断）</summary>
    public const decimal MaxAmount = 9999999999999999.99m;

    /// <summary>系统支持的币种（与协议 / 销售订单 / 收款单 / 发票币种枚举同源：CNY / USD / EUR / HKD / GBP / JPY）</summary>
    public static readonly string[] SupportedCurrencies = Enum.GetNames<Currency>();

    // ==================== 0.1 口径文案（接口、界面与文档同源） ====================

    /// <summary>服务来源链接口径文案</summary>
    public const string SourceLinkRuleText =
        "服务来源链接口径：每一行都必须用**来源类型 + 来源记录 Id（持久化标识符）**显式指向一条既有服务来源"
        + "（销售订单 / 装柜清单），单号 / 日期 / 状态 / 客户 / 币种快照由服务端按来源记录权威写入；"
        + "系统**绝不**按单号文本、金额、日期或相似度猜测链接，也不会替用户自动关联任何记录。";

    /// <summary>行金额口径文案</summary>
    public const string AmountRuleText =
        "行金额口径：说明、计费基础数量与金额都只来自授权用户**显式提交**（或按权威字段手工抄录）的持久化证据，"
        + "服务端只按币种精度取整并校验大于 0；**当前协议费率 / 协议固定金额 / 客户账期与默认值 / 来源单据金额 / "
        + "自由文本 / 相似度都不会被折算、分摊或转换成任何行金额**；合计由服务端按有效行金额求和，"
        + "客户端提交的合计一律不被采信。";

    /// <summary>到期日口径文案</summary>
    public const string DueDateRuleText =
        "到期日口径：到期日是**可选**的显式证据，留空即**未知**；系统不会按客户账期、协议文字、对账日期、"
        + "历史对账单或来源单据推算或补齐任何到期日，填写时只校验不得早于对账日期。";

    /// <summary>唯一性口径文案</summary>
    public const string UniquenessRuleText =
        "唯一性口径：同一「客户 + 规范化对账单号」在**未作废**对账单内唯一（草稿同样占用身份）；"
        + "同一服务来源（来源类型 + 来源记录 Id）在同一时间只能被**一条未作废行**引用（草稿行同样占用来源身份，"
        + "作废后释放）；重复被**拒绝**而不是静默合并或覆盖。";

    /// <summary>与发票 / 收款 / 记账 / 提成的分离文案</summary>
    public const string SeparationText =
        "与其它模型刻意分离：本对账单证据**不是**税务发票、**不是**具有法律效力的客户对账单确认、"
        + "**不是**收入确认、**不是**付款通知或催款函、**不是**结算 / 核销确认，也**不是**会计凭证或总账记账分录；"
        + "它与客户销项发票证据、收款单及其引用行、业务员提成报表（SalesCommissionRate）之间没有任何派生、"
        + "回写或合并关系，登记 / 作废对账单不会改变它们的任何结果。";

    /// <summary>模块边界文案</summary>
    public const string BoundaryText =
        "本模块只是客户代理服务费的**仓库内操作性费用证据册**：登记 / 修改 / 登记 / 作废都**不**开具或作废任何发票、"
        + "**不**报税、**不**记账或生成凭证 / 收款 / 付款 / 结算单、**不**收款或付款、**不**催收或联系客户、"
        + "**不**调用任何外部服务，也**不**改写 ERP-069 协议证据、客户主数据（含账期 / 信用）、销售订单、"
        + "装柜与装柜清单、单证、发票（销项 / 进项）、收款单及其引用行、库存与库存成本、库存流水、"
        + "费用与退税、结算与余额记录；更正走显式作废（原因必填），保留原始行与历史。";

    /// <summary>历史证据只读口径文案</summary>
    public const string HistoricalEvidenceText =
        "历史证据只读：来源记录（订单 / 装柜清单）被取消、软删除或改名，协议被作废，客户被停用 / 删除 / 改名后，"
        + "历史对账单行仍按登记当时的来源快照与客户 / 协议快照可读，只显式标注不可用；"
        + "系统不会把历史行改派到别的记录，也不提供硬删除与静默改写。";

    // ==================== 1. 对账单身份与客户 ====================

    /// <summary>对账单号规范化（必填 + 去首尾空白 + 长度校验；不自动发号、不做格式猜测）</summary>
    public static string NormalizeStatementNo(string? statementNo)
    {
        var value = (statementNo ?? string.Empty).Trim();
        if (value.Length == 0)
            throw BusinessException.InvalidParameter("请填写对账单号（按真实对账单填写，系统不自动发号）");
        if (value.Length > MaxStatementNoLength)
            throw BusinessException.InvalidParameter($"对账单号长度不能超过 {MaxStatementNoLength} 个字符");
        return value;
    }

    /// <summary>
    /// 身份要素规范化（唯一性判定用）：去掉所有空白与连字符 / 下划线后大写。
    /// 仅用于「同一客户 + 同一规范化对账单号」的**精确**判定，不做模糊匹配、不静默合并。
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
    public static string IdentityText(string? statementNo)
    {
        var value = (statementNo ?? string.Empty).Trim();
        return value.Length == 0 ? "(未填对账单号)" : value;
    }

    /// <summary>客户可用性文案（历史证据照常可读，不可用时显式说明，不静默改成 0 或空）</summary>
    public static string CustomerAvailabilityText(bool exists, bool deleted, int status)
        => CustomerAvailabilityText(exists ? status : (int?)null, deleted);

    /// <summary>客户可用性文案（历史证据照常可读，不可用时显式说明）</summary>
    public static string CustomerAvailabilityText(int? status, bool deleted)
    {
        if (status is null || deleted) return "客户已删除（历史快照仍可读，不能用于新对账单）";
        if (status != 1) return "客户已停用（历史快照仍可读，不能用于新对账单）";
        return "客户可用";
    }

    /// <summary>客户是否可用于新对账单 / 修改对账单（存在、未删除且启用）</summary>
    public static bool IsCustomerSelectable(bool exists, bool deleted, int status)
        => exists && !deleted && status == 1;

    /// <summary>协议可用性文案（已登记才可作为新对账单的关联协议；历史快照照常可读）</summary>
    public static string AgreementAvailabilityText(int? agreementStatus, bool deleted)
    {
        if (agreementStatus is null || deleted)
            return "关联协议已删除（历史快照仍可读，不能用于新对账单）";
        return agreementStatus switch
        {
            AgencyServiceFeeAgreementRules.StatusDraft =>
                "关联协议仍是草稿（未登记）：不能作为新对账单依据（历史快照仍可读）",
            AgencyServiceFeeAgreementRules.StatusRecorded => "关联协议可用",
            AgencyServiceFeeAgreementRules.StatusVoided =>
                "关联协议已作废（历史快照仍可读，不能用于新对账单）",
            _ => $"关联协议状态未知（{agreementStatus}）：不能用于新对账单（历史快照仍可读）"
        };
    }

    /// <summary>协议是否可用于新对账单 / 修改对账单（存在、未删除且已登记）</summary>
    public static bool IsAgreementSelectable(int? agreementStatus, bool deleted)
        => agreementStatus == AgencyServiceFeeAgreementRules.StatusRecorded && !deleted;

    // ==================== 2. 币种、对账日期、到期日与服务期间 ====================

    /// <summary>币种规范化 + 支持范围校验（币种必须来自系统币种口径，金额不做汇率换算）</summary>
    public static string NormalizeCurrencyStrict(string? currency)
        => AgencyServiceFeeAgreementRules.NormalizeCurrencyStrict(currency);

    /// <summary>对账日期校验（必填；由用户显式填写，服务端不按当天、单据日期或来源日期推算）</summary>
    public static DateTime ValidateStatementDate(DateTime? statementDate)
    {
        if (statementDate is null)
            throw BusinessException.InvalidParameter("请填写对账日期（系统不按当天、订单或来源单据日期推算）");
        return statementDate.Value.Date;
    }

    /// <summary>
    /// 到期日校验（**可选**）：留空 = 未知（照实保持 null，绝不推算 / 补齐）；
    /// 填写时不得早于对账日期（不做静默调整）。
    /// </summary>
    public static DateTime? ValidateDueDate(DateTime? dueDate, DateTime statementDate)
    {
        if (dueDate is null) return null;

        var value = dueDate.Value.Date;
        if (value < statementDate)
            throw BusinessException.InvalidParameter(
                $"到期日 {value:yyyy-MM-dd} 不能早于对账日期 {statementDate:yyyy-MM-dd}"
                + "（如尚未确定到期日请留空 = 未知，系统不会自动补一个日期）");
        return value;
    }

    /// <summary>到期日文案（留空时显式显示「未提供（到期日未知）」，绝不推算）</summary>
    public static string DueDateText(DateTime? dueDate)
        => dueDate is null
            ? "未提供（到期日未知：不由客户账期、协议或对账日期推算）"
            : dueDate.Value.ToString("yyyy-MM-dd");

    /// <summary>服务期间校验（起止均必填，且结束不得早于起始；不按对账日期或行来源日期推算）</summary>
    public static (DateTime From, DateTime To) ValidateServicePeriod(DateTime? from, DateTime? to)
    {
        if (from is null)
            throw BusinessException.InvalidParameter("请填写服务期间起始日期（系统不按对账日期或来源单据日期推算）");
        if (to is null)
            throw BusinessException.InvalidParameter("请填写服务期间结束日期（系统不按对账日期或来源单据日期推算）");

        var start = from.Value.Date;
        var end = to.Value.Date;
        if (end < start)
            throw BusinessException.InvalidParameter(
                $"服务期间结束日期 {end:yyyy-MM-dd} 不能早于起始日期 {start:yyyy-MM-dd}"
                + "（不做静默调整）");
        return (start, end);
    }

    /// <summary>服务期间文案（**非持久化列**）</summary>
    public static string ServicePeriodText(DateTime from, DateTime to) => $"{from:yyyy-MM-dd} ~ {to:yyyy-MM-dd}";

    /// <summary>币种兼容文案（显式说明对账单 / 协议 / 行来源各自的币种口径）</summary>
    public static string CurrencyCompatibilityText(string? currency)
        => $"客户与协议币种必须与对账单币种 {CurrencyAmountRules.NormalizeCurrency(currency)} 完全一致；"
        + "销售订单来源自带币种（不一致即拒绝），装柜清单来源不携带币种（币种校验对该来源不适用，"
        + "系统不会替它补一个币种）；金额一律原币记录，**不做汇率换算、不跨币种合并**。";

    // ==================== 3. 服务来源类型与资格 ====================

    /// <summary>来源类型文案（未知取值照实回显，不假定归属）</summary>
    public static string SourceTypeText(string? sourceType)
    {
        var value = (sourceType ?? string.Empty).Trim();
        return value switch
        {
            SourceTypeSalesOrder => "销售订单",
            SourceTypeLoadingList => "装柜清单",
            _ => $"未知来源类型（{value}）"
        };
    }

    /// <summary>来源类型规范化（必须显式且受支持；空值或未知取值一律拒绝，不静默兜底）</summary>
    public static string NormalizeSourceType(string? sourceType)
    {
        var value = (sourceType ?? string.Empty).Trim();
        if (!SupportedSourceTypes.Contains(value, StringComparer.Ordinal))
            throw BusinessException.InvalidParameter(
                $"服务来源类型「{value}」不受支持：只允许 "
                + $"{SourceTypeSalesOrder}（销售订单）/ {SourceTypeLoadingList}（装柜清单）"
                + "（系统不按单号文本、金额或日期猜测来源类型）");
        return value;
    }

    /// <summary>来源类型过滤规范化（为空 = 不过滤；未知取值一律拒绝，不静默忽略筛选条件）</summary>
    public static string? NormalizeSourceTypeFilter(string? sourceType)
    {
        var value = (sourceType ?? string.Empty).Trim();
        return value.Length == 0 ? null : NormalizeSourceType(value);
    }

    /// <summary>该来源类型是否自带币种（装柜清单没有币种列 ⇒ 币种校验对它不适用，而不是「币种未知可猜」）</summary>
    public static bool SourceCarriesCurrency(string? sourceType)
        => string.Equals((sourceType ?? string.Empty).Trim(), SourceTypeSalesOrder, StringComparison.Ordinal);

    /// <summary>来源身份文案（类型 + 单号，便于人工核对）</summary>
    public static string SourceIdentityText(string? sourceType, string? sourceNo)
        => $"{SourceTypeText(sourceType)}「{(sourceNo ?? string.Empty).Trim()}」";

    /// <summary>
    /// 来源币种兼容文案：销售订单来源**必须**与对账单币种一致（不一致即拒绝，不做汇率换算）；
    /// 装柜清单来源不携带币种，因此显式标注「币种校验不适用（来源不携带币种）」，绝不由其它字段补一个币种。
    /// </summary>
    public static string SourceCurrencyCompatibilityText(
        string? sourceType, string? sourceCurrency, string? statementCurrency)
    {
        var statement = CurrencyAmountRules.NormalizeCurrency(statementCurrency);
        if (!SourceCarriesCurrency(sourceType))
            return $"来源不携带币种：币种校验对该来源不适用（对账单币种 {statement}，不做汇率换算）";

        var source = CurrencyAmountRules.NormalizeCurrency(sourceCurrency);
        return string.Equals(source, statement, StringComparison.Ordinal)
            ? $"来源币种 {source} 与对账单币种 {statement} 一致（原币，不做汇率换算）"
            : $"来源币种 {source} 与对账单币种 {statement} 不一致（不做汇率换算，不能引用）";
    }

    /// <summary>
    /// 来源资格判定（**不抛异常**，返回原因文案供界面逐行说明）：
    /// 来源记录存在且未删除、未取消、客户与对账单客户一致；自带币种的来源还必须币种一致。
    /// 装柜清单的**参与方行不参与判定**（只按兼容主客户字段，绝不按参与方推断归属）。
    /// </summary>
    public static (bool Eligible, string Text) EvaluateSourceEligibility(
        string? sourceType, bool exists, bool deleted, int sourceStatus,
        long sourceCustomerId, string? sourceCurrency, long statementCustomerId, string? statementCurrency)
    {
        var typeText = SourceTypeText(sourceType);

        if (!exists) return (false, $"指定的{typeText}记录不存在，不能引用（历史行仍可读）");
        if (deleted) return (false, $"指定的{typeText}记录已删除，不能引用（历史行仍可读）");
        if (sourceStatus == (int)DocumentStatus.Cancelled)
            return (false, $"指定的{typeText}记录已取消，不能引用（已取消来源不参与服务费对账）");

        if (sourceCustomerId != statementCustomerId)
            return (false,
                $"{typeText}的客户（Id={sourceCustomerId}）与对账单客户（Id={statementCustomerId}）不一致，不能引用"
                + "（不做跨客户合并）");

        if (SourceCarriesCurrency(sourceType))
        {
            var source = CurrencyAmountRules.NormalizeCurrency(sourceCurrency);
            var statement = CurrencyAmountRules.NormalizeCurrency(statementCurrency);
            if (!string.Equals(source, statement, StringComparison.Ordinal))
                return (false,
                    $"{typeText}的币种 {source} 与对账单币种 {statement} 不一致，不能引用（不做汇率换算）");
        }

        return (true, $"{typeText}可引用（只读关联，不会改写该记录）");
    }

    // ==================== 4. 行证据：说明、计费基础与金额 ====================

    /// <summary>行说明规范化（必填 + 去首尾空白 + 长度校验；超长直接拒绝，不静默截断）</summary>
    public static string NormalizeDescription(string? description)
    {
        var value = (description ?? string.Empty).Trim();
        if (value.Length == 0)
            throw BusinessException.InvalidParameter(
                "请填写行说明（说明这一行对应什么服务；该说明只作为人工留痕，系统不据此计算金额）");
        if (value.Length > MaxDescriptionLength)
            throw BusinessException.InvalidParameter($"行说明长度不能超过 {MaxDescriptionLength} 个字符");
        return value;
    }

    /// <summary>计费基础说明规范化（必填 + 去首尾空白 + 长度校验；超长直接拒绝，不静默截断）</summary>
    public static string NormalizeBasisNote(string? basisNote)
    {
        var value = (basisNote ?? string.Empty).Trim();
        if (value.Length == 0)
            throw BusinessException.InvalidParameter(
                "请填写计费基础说明（例如按协议约定的期间 / 按订单数量 / 按月固定；"
                + "该说明只作为人工留痕，系统不据此计算金额）");
        if (value.Length > MaxBasisNoteLength)
            throw BusinessException.InvalidParameter($"计费基础说明长度不能超过 {MaxBasisNoteLength} 个字符");
        return value;
    }

    /// <summary>
    /// 计费基础数量校验（**可选**显式留痕）：留空 = 未知（保持 null，绝不从来源单据数量推算）；
    /// 填写时按 4 位小数取整并校验绝对值不超过上限。
    /// </summary>
    public static decimal? ValidateBasisQuantity(decimal? basisQuantity)
    {
        if (basisQuantity is null) return null;

        var rounded = Math.Round(basisQuantity.Value, BasisQuantityDecimals, MidpointRounding.AwayFromZero);
        if (Math.Abs(rounded) > MaxBasisQuantity)
            throw BusinessException.InvalidParameter(
                $"计费基础数量 {basisQuantity.Value} 超出允许范围（绝对值不得超过 {MaxBasisQuantity}，不静默截断）");
        return rounded;
    }

    /// <summary>
    /// 行金额校验（**服务端权威**）：按对账单币种精度取整（0.5 进位）后必须大于 0，且不得超过上限；
    /// 金额只来自用户显式提交，服务端不从协议费率 / 固定金额、客户默认值或来源金额折算。
    /// </summary>
    public static decimal ValidateAmount(decimal amount, string? currency)
    {
        var cur = CurrencyAmountRules.NormalizeCurrency(currency);
        var rounded = CurrencyAmountRules.RoundAmount(amount, cur);
        if (rounded <= 0)
            throw BusinessException.InvalidParameter(
                $"行金额必须大于 0（当前 {amount}，按 {cur} 精度取整后为 {rounded}）；"
                + "0 或负值不是有效费用证据，系统也不会用协议费率或客户默认值补一个金额");
        if (rounded > MaxAmount)
            throw BusinessException.InvalidParameter(
                $"行金额 {rounded} 超出允许上限 {MaxAmount}（不静默截断）");
        return rounded;
    }

    /// <summary>
    /// 合计计算（**服务端权威**）：对已校验的行金额按对账单币种精度求和；不读取客户端提交的合计，
    /// 也不按协议费率 / 客户默认值补差。
    /// </summary>
    public static decimal ComputeTotal(IEnumerable<decimal> lineAmounts, string? currency)
    {
        ArgumentNullException.ThrowIfNull(lineAmounts);
        var sum = lineAmounts.Aggregate(0m, (acc, value) => acc + value);
        var total = CurrencyAmountRules.RoundAmount(sum, currency);
        if (total > MaxAmount)
            throw BusinessException.InvalidParameter(
                $"对账单合计 {total} 超出允许上限 {MaxAmount}（请拆分对账单，系统不静默截断）");
        return total;
    }

    /// <summary>行金额文案（显式展示原币与币种精度，不做换算）</summary>
    public static string AmountText(decimal amount, string? currency)
    {
        var cur = CurrencyAmountRules.NormalizeCurrency(currency);
        var decimals = CurrencyAmountRules.PrecisionOf(cur);
        return $"{amount.ToString($"F{decimals}", System.Globalization.CultureInfo.InvariantCulture)} {cur}";
    }

    // ==================== 5. 有界文本、状态机与文案 ====================

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

    /// <summary>行数上限校验（**有界**：至少一行，且不得超过 MaxLinesPerStatement）</summary>
    public static void EnsureLineCountWithinLimit(int lineCount)
    {
        if (lineCount <= 0)
            throw BusinessException.InvalidParameter(
                "请至少添加一条**显式服务来源引用行**：对账单证据必须说明对应哪些服务来源");
        if (lineCount > MaxLinesPerStatement)
            throw BusinessException.InvalidParameter(
                $"单张对账单最多 {MaxLinesPerStatement} 行（当前 {lineCount} 行）：请拆分对账单，"
                + "系统不静默截断也不丢弃多余行");
    }

    /// <summary>状态文案</summary>
    public static string StatusText(int status) => status switch
    {
        StatusDraft => "草稿",
        StatusRecorded => "已登记",
        StatusVoided => "已作废",
        _ => throw BusinessException.InvalidParameter(
            $"对账单状态只能是 {StatusDraft}（草稿）/ {StatusRecorded}（已登记）/ {StatusVoided}（已作废），收到 {status}")
    };

    /// <summary>状态过滤规范化（为空 = 不过滤；未知取值一律拒绝，不静默忽略筛选条件）</summary>
    public static int? NormalizeStatusFilter(int? status)
    {
        if (status is null) return null;
        _ = StatusText(status.Value);
        return status;
    }

    /// <summary>对账单必须处于草稿状态才能编辑（已登记 / 已作废冻结，保留可读）</summary>
    public static void EnsureEditable(int status, string identity)
    {
        if (status == StatusVoided)
            throw BusinessException.RuleConflict(
                $"对账单「{identity}」已作废：作废证据不可修改（如需重新登记请新建一条，历史证据保持可读）");
        if (status == StatusRecorded)
            throw BusinessException.RuleConflict(
                $"对账单「{identity}」已登记：已登记证据不可修改（如需更正请先作废，再登记新对账单）");
    }

    /// <summary>对账单必须处于草稿状态才能登记（重复登记被拒绝）</summary>
    public static void EnsureRecordable(int status, string identity)
    {
        if (status == StatusRecorded)
            throw BusinessException.RuleConflict($"对账单「{identity}」已登记，不能重复登记");
        if (status == StatusVoided)
            throw BusinessException.RuleConflict(
                $"对账单「{identity}」已作废，不能登记（作废证据不可恢复为已登记）");
    }

    /// <summary>对账单可以作废（已作废拒绝重复作废；草稿与已登记都可作废一次）</summary>
    public static void EnsureVoidable(int status, string identity)
    {
        if (status == StatusVoided)
            throw BusinessException.RuleConflict($"对账单「{identity}」已是已作废状态，不能重复作废");
    }
}


