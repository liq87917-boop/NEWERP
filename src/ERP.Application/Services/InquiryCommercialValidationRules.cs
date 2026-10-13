using ERP.Application.Common;
using ERP.Domain.Entities;
using ERP.Domain.Enums;

namespace ERP.Application.Services;

/// <summary>
/// 询价单商业条款（币种 / 汇率 / 有效期天数 / 逐行数量 · 单价 / 行金额）的**唯一权威校验与整理契约**（ERP-474）。
/// 询价单的手工新增 / 修改与运营承诺（提交 / 审核 / 转报价单）全部复用本契约，系统内不允许出现第二套询价单条款校验规则：
/// <list type="number">
/// <item><b>币种</b>：必须是 <see cref="Currency"/> 已定义值（绝不把未定义值当作某币种）；</item>
/// <item><b>汇率</b>：必须 &gt; 0 且在持久化精度内可表示 —— <b>绝不</b>再把 0 / 负数静默替换为 1；</item>
/// <item><b>有效期（天）</b>：必须是 <see cref="MinValidDays"/> ~ <see cref="MaxValidDays"/> 的<b>有界整数天</b>，
/// 拒绝 0 / 负数 / 超范围（转换时 <c>ValidUntil = 报价日期 + ValidDays</c> 因此不可能静默回退默认值）；</item>
/// <item><b>逐行</b>：数量 &gt; 0、单价 ≥ 0 且均在实际持久化精度内可表示（绝不把正数量静默取整为 0）；</item>
/// <item><b>行金额</b>：一律由服务端按**已批准的币种精度与取整口径**（<see cref="CurrencyAmountRules"/>：
/// JPY / KRW / VND / IDR 为 0 位小数、其余 2 位、0.5 进位）从有效明细派生，绝不采信客户端行金额；</item>
/// <item><b>软删除行注入</b>：客户端提交的明细不接受 <c>IsDeleted = true</c> 的行（直接拒绝，绝不静默写入）。</item>
/// </list>
/// <para>两种调用口径：<see cref="NormalizeIncomingDraft"/>（新增 / 修改：允许显式支持的空草稿，但任何被提交的行都必须是合法商业行，
/// 并通过服务端派生行金额）与 <see cref="ValidateOperational"/>（提交 / 审核 / 转报价单等下游承诺：至少一行有效明细，
/// 非法历史条款绝不进入履约）。</para>
/// <para><b>边界</b>：本类只做纯计算与校验，不访问数据库、不写库、不改变单据状态、不消耗单据号、不改写任何既有记录；
/// 历史读取 / 打印路径不调用本类，既有历史单据仍可读且不被自动修正。</para>
/// </summary>
public static class InquiryCommercialValidationRules
{
    /// <summary>
    /// EF / 数据库实际精度：询价单主表与明细（<c>Inquiries</c> / <c>InquiryDetails</c>）在 EF 模型中
    /// **未**显式 <c>HasPrecision</c>，数量 / 单价 / 行金额 / 汇率因此按 EF 默认的 <c>DECIMAL(18,2)</c> 落库。
    /// 校验必须使用**同一份**精度口径，否则超精度值会被落库静默取整。
    /// </summary>
    public const int DecimalPrecision = 18;

    /// <summary>EF / 数据库实际小数位（与 <see cref="DecimalPrecision"/> 同源）。</summary>
    public const int DecimalScale = 2;

    /// <summary><c>DECIMAL(18,2)</c> 的最大可表示值。</summary>
    public const decimal MaxDecimal = 9_999_999_999_999_999.99m;

    /// <summary><c>DECIMAL(18,2)</c> 的最小可表示值。</summary>
    public const decimal MinDecimal = -9_999_999_999_999_999.99m;

    /// <summary>询价单报价有效期的最少天数（0 天意味着报价当场失效，非法）。</summary>
    public const int MinValidDays = 1;

    /// <summary>
    /// 询价单报价有效期的最多天数（有界整数天：约束 <c>ValidUntil = 报价日期 + ValidDays</c> 不会溢出 / 产生异常有效期，
    /// 10 年对询价报价场景已足够）。
    /// </summary>
    public const int MaxValidDays = 3650;

    /// <summary>币种不是 <see cref="Currency"/> 已定义值时的拒绝文案。</summary>
    public const string CurrencyText = "询价单币种不是系统受支持的币种（Currency 枚举未定义值）";

    /// <summary>汇率非正 / 超精度时的拒绝文案（绝不静默按 1 处理）。</summary>
    public const string ExchangeRateText = "询价单汇率必须大于 0 且在存储精度（DECIMAL(18,2)）内可表示";

    /// <summary>有效期天数非有界整数天时的拒绝文案。</summary>
    public const string ValidDaysText =
        "询价单有效期（天）必须是 1 ~ 3650 之间的整数天（拒绝 0 / 负数 / 超范围，绝不静默回退默认值）";

    /// <summary>客户端注入软删除明细行时的拒绝文案。</summary>
    public const string SoftDeletedDetailText =
        "询价单明细不接受软删除行（IsDeleted = true）注入：请在提交前移除该行，服务端不会写入软删除明细";

    /// <summary>提交 / 审核 / 转报价要求至少一行有效明细时的拒绝文案。</summary>
    public const string EmptyOperationalDetailsText =
        "询价单提交 / 审核 / 转报价前至少需要一行有效明细（未删除且数量大于 0、单价非负）";

    /// <summary>行金额超出持久化精度时的拒绝文案（绝不静默截断或取整）。</summary>
    public const string AmountOverflowText =
        "询价单行金额超出可存储范围（DECIMAL(18,2)）：已拒绝，绝不静默截断或取整改变明细金额";

    /// <summary>契约口径文案（接口 / 文档同源）。</summary>
    public const string ContractText =
        "ERP-474：询价单商业条款只有一份权威契约（InquiryCommercialValidationRules）："
        + "币种必须是 Currency 已定义值、汇率必须大于 0 且在 DECIMAL(18,2) 内可表示、有效期（天）必须是 1 ~ 3650 的整数天、"
        + "逐行数量大于 0、单价非负，行金额一律由服务端按 CurrencyAmountRules 的币种精度"
        + "（JPY 等 0 位、其余 2 位、0.5 进位）从有效明细派生，绝不采信客户端行金额；软删除明细行注入一律拒绝。"
        + "手工新增 / 修改与运营承诺（提交 / 审核 / 转报价单）共用本契约，"
        + "被拒绝的请求在单号预约与任何写入之前整体拒绝，表头 / 明细 / 来源单据 / 状态 / 单号预约均不变。";

    /// <summary>币种代码（<see cref="CurrencyAmountRules"/> 的币种精度契约按代码字符串匹配）。</summary>
    public static string CurrencyCode(Currency currency) => currency.ToString();

    /// <summary>币种金额小数位（复用 <see cref="CurrencyAmountRules.PrecisionOf"/>：JPY / KRW / VND / IDR 为 0 位，其余 2 位）。</summary>
    public static int AmountDecimals(Currency currency)
        => CurrencyAmountRules.PrecisionOf(CurrencyCode(currency));

    /// <summary>按币种精度取整（复用 <see cref="CurrencyAmountRules.RoundAmount"/>：0.5 进位，确定性）。</summary>
    public static decimal RoundAmount(decimal amount, Currency currency)
        => CurrencyAmountRules.RoundAmount(amount, CurrencyCode(currency));

    /// <summary>值是否在 <c>DECIMAL(18,2)</c> 精度内可表示（最多 2 位小数且绝对值有界）。</summary>
    public static bool IsRepresentable(decimal value)
        => decimal.Round(value, DecimalScale) == value
           && value >= MinDecimal && value <= MaxDecimal;

    /// <summary>有效（未删除）明细行：提交 / 审核 / 转报价一律以有效行为准。</summary>
    public static List<InquiryDetail> ActiveDetails(Inquiry inquiry)
    {
        ArgumentNullException.ThrowIfNull(inquiry);
        return (inquiry.Details ?? new List<InquiryDetail>())
            .Where(d => !d.IsDeleted)
            .ToList();
    }

    /// <summary>
    /// 客户端写入（新增 / 修改）的校验：允许显式支持的**空草稿**（无明细行），但任何被提交的明细行都必须是合法商业行；
    /// 注入软删除明细行一律拒绝。
    /// </summary>
    public static void ValidateIncomingDraft(Inquiry inquiry)
    {
        ArgumentNullException.ThrowIfNull(inquiry);

        var injected = (inquiry.Details ?? new List<InquiryDetail>()).FirstOrDefault(d => d.IsDeleted);
        if (injected is not null)
            throw BusinessException.InvalidParameter(SoftDeletedDetailText);

        ValidateActiveDetails(inquiry, requireOperationalLines: false);
    }

    /// <summary>
    /// 已持久化记录的条款校验（按**有效明细**校验，不含「软删除行注入」检查 —— 历史记录本就可能带有软删除明细行）：
    /// 提交 / 审核 / 转换在锁内重读后复核既有商业条款，非法历史条款绝不成为实时履约。
    /// 本方法只校验，绝不回填、重置或改写既有记录的任何字段。
    /// </summary>
    public static void ValidatePersisted(Inquiry inquiry)
    {
        ArgumentNullException.ThrowIfNull(inquiry);
        ValidateActiveDetails(inquiry, requireOperationalLines: false);
    }

    /// <summary>
    /// 提交 / 审核 / 转报价单等**下游承诺**的校验：在条款校验之上要求至少一行有效明细，保证非法历史条款
    /// （含空草稿、数量 &lt;= 0、未定义币种、非正 / 超精度汇率、越界有效期天数）不可能成为实时履约
    /// （本方法只校验，不改写任何字段）。
    /// </summary>
    public static void ValidateOperational(Inquiry inquiry)
    {
        ArgumentNullException.ThrowIfNull(inquiry);
        ValidateActiveDetails(inquiry, requireOperationalLines: true);
    }

    /// <summary>
    /// 客户端写入的完整整理入口：先 <see cref="ValidateIncomingDraft"/>（失败即原样抛出，不做任何字段改写），
    /// 再重置明细身份（<c>Id</c> 由服务端生成）、按币种精度派生行金额，并补齐询价日期默认值。
    /// 单号 / 状态 / 身份映射仍由控制器在授权与校验之后按既有口径覆盖（本方法绝不触碰）。
    /// </summary>
    public static void NormalizeIncomingDraft(Inquiry inquiry)
    {
        ValidateIncomingDraft(inquiry);
        NormalizeValidated(inquiry);
    }

    /// <summary>
    /// 有效明细条款校验（币种 / 汇率 / 有效期天数 / 逐行数量 · 单价 / 派生行金额的精度与溢出）：任一项不满足即返回
    /// 受控业务错误（<see cref="ErrorCodes.InvalidParameter"/>），绝不静默取整、绝不把正数量舍入为 0，
    /// 也绝不写入与明细不一致的金额。
    /// </summary>
    private static void ValidateActiveDetails(Inquiry inquiry, bool requireOperationalLines)
    {
        if (!Enum.IsDefined(typeof(Currency), inquiry.Currency))
            throw BusinessException.InvalidParameter(CurrencyText);

        if (inquiry.ExchangeRate <= 0m || !IsRepresentable(inquiry.ExchangeRate))
            throw BusinessException.InvalidParameter(ExchangeRateText);

        if (inquiry.ValidDays < MinValidDays || inquiry.ValidDays > MaxValidDays)
            throw BusinessException.InvalidParameter(ValidDaysText);

        var details = ActiveDetails(inquiry);
        if (requireOperationalLines && details.Count == 0)
            throw BusinessException.InvalidParameter(EmptyOperationalDetailsText);

        try
        {
            for (var i = 0; i < details.Count; i++)
            {
                var line = i + 1;
                var detail = details[i];

                if (detail.Quantity <= 0m || !IsRepresentable(detail.Quantity))
                    throw BusinessException.InvalidParameter(
                        $"询价单明细第 {line} 行数量必须大于 0 且在存储精度（DECIMAL(18,2)）内可表示：当前值 {detail.Quantity}");

                if (detail.UnitPrice < 0m || !IsRepresentable(detail.UnitPrice))
                    throw BusinessException.InvalidParameter(
                        $"询价单明细第 {line} 行单价不能为负且必须在存储精度（DECIMAL(18,2)）内可表示：当前值 {detail.UnitPrice}");

                var amount = RoundAmount(detail.Quantity * detail.UnitPrice, inquiry.Currency);
                if (!IsRepresentable(amount))
                    throw BusinessException.InvalidParameter(
                        $"询价单明细第 {line} 行金额 {amount} 超出存储精度（DECIMAL(18,2)）：已拒绝，绝不静默取整改变明细金额");
            }
        }
        catch (OverflowException)
        {
            // decimal 乘法 / 加法溢出：转换为受控业务错误，绝不落库为截断值。
            throw BusinessException.InvalidParameter(AmountOverflowText);
        }
    }

    /// <summary>
    /// 已经过校验的询价单整理：明细主键由服务端重置、行金额按币种精度派生；客户端提交的行身份 / 行金额一律不被信任。
    /// </summary>
    private static void NormalizeValidated(Inquiry inquiry)
    {
        var details = inquiry.Details ?? new List<InquiryDetail>();
        inquiry.Details = details;
        foreach (var detail in details)
        {
            detail.Id = 0;
            detail.Amount = RoundAmount(detail.Quantity * detail.UnitPrice, inquiry.Currency);
            detail.CreatedAt = DateTime.Now;
        }
        if (inquiry.InquiryDate == default) inquiry.InquiryDate = DateTime.Today;
    }
}

