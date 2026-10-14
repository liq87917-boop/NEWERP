using ERP.Application.Common;
using ERP.Domain.Entities;
using ERP.Domain.Enums;

namespace ERP.Application.Services;

/// <summary>
/// 报价单商业条款（币种 / 汇率 / 数量 / 单价 / 金额 / 合计）的**唯一权威校验与整理契约**（ERP-470）。
/// 报价单的新增 / 修改、询价转报价、版本复制、提交 / 审核与转 PI / 转销售订单（下游承诺）全部复用本契约，
/// 系统内不允许出现第二套报价单金额或校验规则：
/// <list type="number">
/// <item><b>币种</b>：必须是 <see cref="Currency"/> 已定义值（绝不跨币种合计，也绝不把未定义值当作某币种）；</item>
/// <item><b>汇率</b>：必须 &gt; 0 且在汇率列实际精度 <c>DECIMAL(18,6)</c> 内可表示（最多 6 位小数、量级有界）——
/// <b>绝不</b>再把 0 / 负数静默替换为 1，也绝不静默取整；</item>
/// <item><b>逐行</b>：数量 &gt; 0、单价 ≥ 0 且均在数量 / 单价列实际精度 <c>DECIMAL(18,4)</c> 内可表示
/// （最多 4 位小数、量级有界：绝不把正数量静默取整为 0，也绝不把 3 / 4 位小数单价静默取整）；</item>
/// <item><b>金额 / 合计</b>：一律由服务端按**已批准的币种精度与取整口径**
/// （<see cref="CurrencyAmountRules"/>：JPY / KRW / VND / IDR 为 0 位小数、其余 2 位、0.5 进位）
/// 从有效明细派生，绝不采信客户端行金额 / 合计；折人民币合计按 CNY 精度取整；</item>
/// <item><b>软删除行注入</b>：客户端提交的明细不接受 <c>IsDeleted = true</c> 的行（直接拒绝，绝不静默写入）。</item>
/// </list>
/// <para>两种调用口径：<see cref="ValidateIncomingDraft"/>（新增 / 修改 / 版本复制：允许显式支持的空草稿，
/// 但任何被提交的行都必须是合法商业行）与 <see cref="ValidateOperational"/>（提交 / 审核 / 转换等下游承诺：
/// 至少一行有效明细，非法历史数量绝不进入履约）。</para>
/// <para><b>边界</b>：本类只做纯计算与校验，不访问数据库、不写库、不改变单据状态、不消耗单据号、不改写任何既有记录；
/// 历史读取 / 打印路径不调用本类，既有历史单据仍可读且不被自动修正。</para>
/// </summary>
public static class QuotationCommercialValidationRules
{
    /// <summary>
    /// 报价单主表与明细（<c>Quotations</c> / <c>QuotationDetails</c>）的整数位数（18）；
    /// 汇率列的小数位见 <see cref="ExchangeRateScale"/>（<c>DECIMAL(18,6)</c>），
    /// 数量 / 单价 / 行金额 / 合计 / 折人民币列的小数位见 <see cref="AmountScale"/>（<c>DECIMAL(18,4)</c>），
    /// 与 SchemaUpgrader 第 20 段建表脚本、<c>ErpDbContext.Config</c> 的显式 <c>HasPrecision</c> 三处同源。
    /// </summary>
    public const int DecimalPrecision = 18;

    /// <summary>
    /// 汇率列（<c>db_owner.Quotations.ExchangeRate</c>）的实际小数位：<c>DECIMAL(18,6)</c>
    /// （EF 模型已显式 <c>HasPrecision(18, 6)</c>，与 SchemaUpgrader 第 20 段建表脚本一致）。
    /// </summary>
    public const int ExchangeRateScale = 6;

    /// <summary>
    /// 数量 / 单价 / 行金额 / 合计 / 折人民币列（<c>db_owner.Quotations</c> 与
    /// <c>db_owner.QuotationDetails</c>）的实际小数位：<c>DECIMAL(18,4)</c>
    /// （EF 模型已显式 <c>HasPrecision(18, 4)</c>，与 SchemaUpgrader 第 20 段建表脚本一致）。
    /// </summary>
    public const int AmountScale = 4;

    /// <summary><c>DECIMAL(18,6)</c>（汇率列）的最大可表示值。</summary>
    public const decimal MaxExchangeRate = 999_999_999_999.999999m;

    /// <summary><c>DECIMAL(18,6)</c>（汇率列）的最小可表示值。</summary>
    public const decimal MinExchangeRate = -999_999_999_999.999999m;

    /// <summary><c>DECIMAL(18,4)</c>（数量 / 单价 / 行金额 / 合计列）的最大可表示值。</summary>
    public const decimal MaxAmount = 99_999_999_999_999.9999m;

    /// <summary><c>DECIMAL(18,4)</c>（数量 / 单价 / 行金额 / 合计列）的最小可表示值。</summary>
    public const decimal MinAmount = -99_999_999_999_999.9999m;

    /// <summary>币种不是 <see cref="Currency"/> 已定义值时的拒绝文案。</summary>
    public const string CurrencyText = "报价单币种不是系统受支持的币种（Currency 枚举未定义值）";

    /// <summary>汇率非正 / 超精度时的拒绝文案（绝不静默按 1 处理，也绝不静默取整）。</summary>
    public const string ExchangeRateText = "报价单汇率必须大于 0 且在存储精度（DECIMAL(18,6)）内可表示";

    /// <summary>客户端注入软删除明细行时的拒绝文案。</summary>
    public const string SoftDeletedDetailText =
        "报价单明细不接受软删除行（IsDeleted = true）注入：请在提交前移除该行，服务端不会写入软删除明细";

    /// <summary>提交 / 审核 / 转换要求至少一行有效明细时的拒绝文案。</summary>
    public const string EmptyOperationalDetailsText =
        "报价单提交 / 审核 / 转换前至少需要一行有效明细（未删除且数量大于 0、单价非负）";

    /// <summary>金额 / 合计超出持久化精度时的拒绝文案（绝不静默截断或取整）。</summary>
    public const string AmountOverflowText =
        "报价单金额超出可存储范围（DECIMAL(18,4)）：已拒绝，绝不静默截断或取整改变明细合计";

    /// <summary>契约口径文案（接口 / 文档同源）。</summary>
    public const string ContractText =
        "ERP-470：报价单商业条款只有一份权威契约（QuotationCommercialValidationRules）："
        + "币种必须是 Currency 已定义值、汇率必须大于 0 且在 DECIMAL(18,6)（汇率列实际精度）内可表示、"
        + "逐行数量大于 0、单价非负且均在 DECIMAL(18,4)（数量 / 单价列实际精度）内可表示，"
        + "行金额与合计一律由服务端按 CurrencyAmountRules 的币种精度（JPY 等 0 位、其余 2 位、0.5 进位）从有效明细派生，"
        + "绝不采信客户端行金额 / 合计，也不跨币种合计；软删除明细行注入一律拒绝。"
        + "新增 / 修改 / 询价转报价 / 版本复制与提交 / 审核 / 转 PI / 转销售订单共用本契约，"
        + "被拒绝的请求在单号预约与任何写入之前整体回滚，表头 / 明细 / 来源单据 / 状态 / 单号预约均不变。";

    /// <summary>币种代码（<see cref="CurrencyAmountRules"/> 的币种精度契约按代码字符串匹配）。</summary>
    public static string CurrencyCode(Currency currency) => currency.ToString();

    /// <summary>币种金额小数位（复用 <see cref="CurrencyAmountRules.PrecisionOf"/>：JPY / KRW / VND / IDR 为 0 位，其余 2 位）。</summary>
    public static int AmountDecimals(Currency currency)
        => CurrencyAmountRules.PrecisionOf(CurrencyCode(currency));

    /// <summary>按币种精度取整（复用 <see cref="CurrencyAmountRules.RoundAmount"/>：0.5 进位，确定性）。</summary>
    public static decimal RoundAmount(decimal amount, Currency currency)
        => CurrencyAmountRules.RoundAmount(amount, CurrencyCode(currency));

    /// <summary>
    /// 值是否在报价单**数量 / 单价 / 行金额 / 合计**列（<c>DECIMAL(18,4)</c>）内可表示
    /// （最多 4 位小数且绝对值有界）；超精度 / 越界一律 <c>false</c>，绝不静默取整。
    /// </summary>
    public static bool IsRepresentable(decimal value)
        => value >= MinAmount && value <= MaxAmount
           && decimal.Round(value, AmountScale, MidpointRounding.AwayFromZero) == value;

    /// <summary>
    /// 值是否在报价单**汇率**列（<c>DECIMAL(18,6)</c>）内可表示
    /// （最多 6 位小数且绝对值有界）；超精度 / 越界一律 <c>false</c>，绝不静默取整。
    /// </summary>
    public static bool IsExchangeRateRepresentable(decimal value)
        => value >= MinExchangeRate && value <= MaxExchangeRate
           && decimal.Round(value, ExchangeRateScale, MidpointRounding.AwayFromZero) == value;

    /// <summary>单行「数量 × 单价」是否在持久化精度（<c>DECIMAL(18,4)</c>）内可表示（乘法溢出按不可表示处理）。</summary>
    public static bool IsLineAmountRepresentable(decimal quantity, decimal unitPrice)
    {
        try
        {
            return IsRepresentable(quantity * unitPrice);
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    /// <summary>有效（未删除）明细行：转换 / 提交 / 审核 / 版本复制一律以有效行为准。</summary>
    public static List<QuotationDetail> ActiveDetails(Quotation quotation)
    {
        ArgumentNullException.ThrowIfNull(quotation);
        return (quotation.Details ?? new List<QuotationDetail>())
            .Where(d => !d.IsDeleted)
            .ToList();
    }

    /// <summary>
    /// 客户端写入（新增 / 修改 / 带入预填后保存 / 版本复制结果）的校验：允许显式支持的**空草稿**
    /// （无明细行），但任何被提交的明细行都必须是合法商业行；注入软删除明细行一律拒绝。
    /// </summary>
    public static void ValidateIncomingDraft(Quotation quotation)
    {
        ArgumentNullException.ThrowIfNull(quotation);

        var injected = (quotation.Details ?? new List<QuotationDetail>()).FirstOrDefault(d => d.IsDeleted);
        if (injected is not null)
            throw BusinessException.InvalidParameter(SoftDeletedDetailText);

        ValidateActiveDetails(quotation, requireOperationalLines: false);
    }

    /// <summary>
    /// 已持久化记录的条款校验（按**有效明细**校验，不含「软删除行注入」检查 —— 历史记录本就可能带有软删除明细行）：
    /// 提交 / 审核 / 转换 / 版本复制在锁内重读后复核既有商业条款，非法历史数量绝不成为实时履约。
    /// 本方法只校验，绝不回填、重置或改写既有记录的任何字段。
    /// </summary>
    public static void ValidatePersisted(Quotation quotation)
    {
        ArgumentNullException.ThrowIfNull(quotation);
        ValidateActiveDetails(quotation, requireOperationalLines: false);
    }

    /// <summary>
    /// 提交 / 审核 / 转 PI / 转销售订单等**下游承诺**的校验：在条款校验之上要求至少一行有效明细，
    /// 保证非法历史数量不可能成为实时履约（本方法只校验，不改写任何字段，也不回填历史快照）。
    /// </summary>
    public static void ValidateOperational(Quotation quotation)
    {
        ArgumentNullException.ThrowIfNull(quotation);
        ValidateActiveDetails(quotation, requireOperationalLines: true);
    }

    /// <summary>
    /// 客户端写入的完整整理入口：先 <see cref="ValidateIncomingDraft"/>（失败即原样抛出，不做任何字段改写），
    /// 再重排行号、按币种精度派生行金额 / 合计 / 折人民币，并补齐报价日期与有效期默认值。
    /// </summary>
    public static void NormalizeIncomingDraft(Quotation quotation)
    {
        ValidateIncomingDraft(quotation);
        NormalizeValidated(quotation);
    }

    /// <summary>
    /// 由既有记录派生的草稿（版本复制结果）的整理入口：与 <see cref="NormalizeIncomingDraft"/> 同源
    /// （派生草稿的明细均为新行，因此不存在软删除注入），允许无有效明细的空草稿。
    /// </summary>
    public static void NormalizeDerivedDraft(Quotation quotation) => NormalizeIncomingDraft(quotation);

    /// <summary>
    /// 有效明细条款校验（币种 / 汇率 / 逐行数量 · 单价 / 派生金额与合计的精度与溢出）：任一项不满足即返回
    /// 受控业务错误（<see cref="ErrorCodes.InvalidParameter"/>），绝不静默取整、绝不把正数量舍入为 0，
    /// 也绝不写入与明细合计不一致的金额。
    /// </summary>
    private static void ValidateActiveDetails(Quotation quotation, bool requireOperationalLines)
    {
        if (!Enum.IsDefined(typeof(Currency), quotation.Currency))
            throw BusinessException.InvalidParameter(CurrencyText);

        if (quotation.ExchangeRate <= 0m || !IsExchangeRateRepresentable(quotation.ExchangeRate))
            throw BusinessException.InvalidParameter(ExchangeRateText);

        var details = ActiveDetails(quotation);
        if (requireOperationalLines && details.Count == 0)
            throw BusinessException.InvalidParameter(EmptyOperationalDetailsText);

        try
        {
            var total = 0m;
            for (var i = 0; i < details.Count; i++)
            {
                var line = i + 1;
                var detail = details[i];

                if (detail.Quantity <= 0m || !IsRepresentable(detail.Quantity))
                    throw BusinessException.InvalidParameter(
                        $"报价单明细第 {line} 行数量必须大于 0 且在存储精度（DECIMAL(18,4)）内可表示：当前值 {detail.Quantity}");

                if (detail.UnitPrice < 0m || !IsRepresentable(detail.UnitPrice))
                    throw BusinessException.InvalidParameter(
                        $"报价单明细第 {line} 行单价不能为负且必须在存储精度（DECIMAL(18,4)）内可表示：当前值 {detail.UnitPrice}");

                var amount = RoundAmount(detail.Quantity * detail.UnitPrice, quotation.Currency);
                if (!IsRepresentable(amount))
                    throw BusinessException.InvalidParameter(
                        $"报价单明细第 {line} 行金额 {amount} 超出存储精度（DECIMAL(18,4)）：已拒绝，绝不静默取整改变明细合计");

                total += amount;
            }

            var roundedTotal = RoundAmount(total, quotation.Currency);
            if (!IsRepresentable(roundedTotal))
                throw BusinessException.InvalidParameter(AmountOverflowText);

            var cny = CurrencyAmountRules.RoundAmount(roundedTotal * quotation.ExchangeRate,
                CurrencyAmountRules.DefaultCurrency);
            if (!IsRepresentable(cny))
                throw BusinessException.InvalidParameter(AmountOverflowText);
        }
        catch (OverflowException)
        {
            // decimal 乘法 / 加法溢出：转换为受控业务错误，绝不落库为截断值。
            throw BusinessException.InvalidParameter(AmountOverflowText);
        }
    }

    /// <summary>
    /// 已经过校验的报价单整理：行号按数组顺序重排为 1..n、主键 / 外键由服务端重置、行金额按币种精度派生、
    /// 合计与折人民币由服务端复核；客户端提交的行号 / 金额 / 合计一律不被信任。
    /// </summary>
    private static void NormalizeValidated(Quotation quotation)
    {
        var line = 0;
        var total = 0m;
        // 允许「未提供明细集合」的显式空草稿（与校验口径一致：空集合与空引用等价）。
        var details = quotation.Details ?? new List<QuotationDetail>();
        foreach (var detail in details)
        {
            detail.Id = 0;
            detail.QuotationId = quotation.Id;
            detail.QuotationNo = quotation.QuotationNo;
            detail.SortNo = ++line;
            detail.Amount = RoundAmount(detail.Quantity * detail.UnitPrice, quotation.Currency);
            detail.CreatedAt = DateTime.Now;
            total += detail.Amount;
        }

        quotation.TotalAmount = RoundAmount(total, quotation.Currency);
        quotation.TotalAmountCny = CurrencyAmountRules.RoundAmount(
            quotation.TotalAmount * quotation.ExchangeRate, CurrencyAmountRules.DefaultCurrency);
        if (quotation.QuotationDate == default) quotation.QuotationDate = DateTime.Today;
        quotation.ValidUntil ??= quotation.QuotationDate.Date.AddDays(30);
    }
}
