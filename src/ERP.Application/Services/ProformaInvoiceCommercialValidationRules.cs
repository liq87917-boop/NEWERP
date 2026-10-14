using ERP.Application.Common;
using ERP.Domain.Entities;
using ERP.Domain.Enums;

namespace ERP.Application.Services;

/// <summary>
/// 形式发票 PI 商业条款（币种 / 汇率 / 数量 / 单价 / 金额 / 合计 / 定金比例 · 定金金额）的
/// **唯一权威校验与整理契约**（ERP-471）。PI 的普通新增 / 修改、报价单转 PI 与提交 / 审核 /
/// 转销售订单（下游承诺）全部复用本契约，系统内不允许出现第二套 PI 金额 / 定金规则：
/// <list type="number">
/// <item><b>币种</b>：必须是 <see cref="Currency"/> 已定义值（绝不跨币种合计，也绝不把未定义值当作某币种）；</item>
/// <item><b>汇率</b>：必须 &gt; 0 且在汇率列实际精度 <c>DECIMAL(18,6)</c> 内可表示 —— <b>绝不</b>再把 0 / 负数静默替换为 1；</item>
/// <item><b>逐行</b>：数量 &gt; 0、单价 ≥ 0 且均在数量 / 单价列实际精度 <c>DECIMAL(18,4)</c> 内可表示（绝不把正数量静默取整为 0）；</item>
/// <item><b>金额 / 合计</b>：一律由服务端按**已批准的币种精度与取整口径**
/// （<see cref="CurrencyAmountRules"/>：JPY / KRW / VND / IDR 为 0 位小数、其余 2 位、0.5 进位）
/// 从有效明细派生，绝不采信客户端行金额 / 合计；折人民币合计按 CNY 精度取整；</item>
/// <item><b>定金比例</b>：必须 0~100（含端点）且在定金比例列实际精度 <c>DECIMAL(18,4)</c> 内可表示；</item>
/// <item><b>定金金额</b>：显式 <c>0</c> 保留既有「自动按比例计算」语义；显式**负数一律拒绝**
/// （绝不静默按比例重算，也绝不当作「未填写」）；显式正数必须在币种精度内可表示且不得超过权威总额。</item>
/// </list>
/// <para>三种调用口径：<see cref="ValidateIncomingDraft"/>（新增 / 修改 / 带入预填后保存：允许显式支持的
/// 空草稿，但任何被提交的行都必须是合法商业行，且拒绝软删除明细行注入）、
/// <see cref="ValidatePersisted"/>（已存记录复核：按有效明细校验，历史软删除行不是拒绝原因）与
/// <see cref="ValidateOperational"/>（提交 / 审核 / 转销售订单等下游承诺：至少一行有效明细）。</para>
/// <para><b>边界</b>：本类只做纯计算与校验，不访问数据库、不写库、不改变单据状态、不消耗单据号、
/// 不改写任何既有记录；历史读取 / 打印路径不调用本类，既有历史单据仍可读且不被自动修正。</para>
/// </summary>
public static class ProformaInvoiceCommercialValidationRules
{
    /// <summary>
    /// PI 主表与明细（<c>ProformaInvoices</c> / <c>ProformaInvoiceDetails</c>）的整数位数（18）。
    /// 汇率列的小数位见 <see cref="ExchangeRateScale"/>（<c>DECIMAL(18,6)</c>），
    /// 数量 / 单价 / 行金额 / 合计 / 折人民币 / 定金比例 / 定金金额列的小数位见 <see cref="AmountScale"/>
    /// （<c>DECIMAL(18,4)</c>），与 SchemaUpgrader 第 21 段建表脚本、<c>ErpDbContext.Config</c> 的显式
    /// <c>HasPrecision</c> 三处同源（ERP-534）。校验必须使用**同一份**精度口径，否则超精度值会被落库静默取整。
    /// </summary>
    public const int DecimalPrecision = 18;

    /// <summary>
    /// 汇率列（<c>db_owner.ProformaInvoices.ExchangeRate</c>）的实际小数位：<c>DECIMAL(18,6)</c>
    /// （EF 模型已显式 <c>HasPrecision(18, 6)</c>，与 SchemaUpgrader 第 21 段建表脚本一致）。
    /// </summary>
    public const int ExchangeRateScale = 6;

    /// <summary>
    /// 数量 / 单价 / 行金额 / 合计 / 折人民币 / 定金比例 / 定金金额列（<c>db_owner.ProformaInvoices</c> 与
    /// <c>db_owner.ProformaInvoiceDetails</c>）的实际小数位：<c>DECIMAL(18,4)</c>
    /// （EF 模型已显式 <c>HasPrecision(18, 4)</c>，与 SchemaUpgrader 第 21 段建表脚本一致）。
    /// </summary>
    public const int AmountScale = 4;

    /// <summary><c>DECIMAL(18,6)</c>（汇率列）的最大可表示值。</summary>
    public const decimal MaxExchangeRate = 999_999_999_999.999999m;

    /// <summary><c>DECIMAL(18,6)</c>（汇率列）的最小可表示值。</summary>
    public const decimal MinExchangeRate = -999_999_999_999.999999m;

    /// <summary><c>DECIMAL(18,4)</c>（数量 / 单价 / 行金额 / 合计 / 定金列）的最大可表示值。</summary>
    public const decimal MaxAmount = 99_999_999_999_999.9999m;

    /// <summary><c>DECIMAL(18,4)</c>（数量 / 单价 / 行金额 / 合计 / 定金列）的最小可表示值。</summary>
    public const decimal MinAmount = -99_999_999_999_999.9999m;

    /// <summary>定金比例下限（%，含端点）。</summary>
    public const decimal MinDepositRatio = 0m;

    /// <summary>定金比例上限（%，含端点）。</summary>
    public const decimal MaxDepositRatio = 100m;

    /// <summary>币种不是 <see cref="Currency"/> 已定义值时的拒绝文案。</summary>
    public const string CurrencyText = "PI 币种不是系统受支持的币种（Currency 枚举未定义值）";

    /// <summary>汇率非正 / 超精度时的拒绝文案（绝不静默按 1 处理）。</summary>
    public const string ExchangeRateText = "PI 汇率必须大于 0 且在存储精度（DECIMAL(18,6)）内可表示";

    /// <summary>客户端注入软删除明细行时的拒绝文案。</summary>
    public const string SoftDeletedDetailText =
        "PI 明细不接受软删除行（IsDeleted = true）注入：请在提交前移除该行，服务端不会写入软删除明细";

    /// <summary>提交 / 审核 / 转销售订单要求至少一行有效明细时的拒绝文案。</summary>
    public const string EmptyOperationalDetailsText =
        "PI 提交 / 审核 / 转销售订单前至少需要一行有效明细（未删除且数量大于 0、单价非负）";

    /// <summary>金额 / 合计超出持久化精度时的拒绝文案（绝不静默截断或取整）。</summary>
    public const string AmountOverflowText =
        "PI 金额超出可存储范围（DECIMAL(18,4)）：已拒绝，绝不静默截断或取整改变明细合计";

    /// <summary>定金比例越界 / 超精度时的拒绝文案。</summary>
    public const string DepositRatioText =
        "PI 定金比例必须在 0~100 之间且在存储精度（DECIMAL(18,4)）内可表示";

    /// <summary>显式负定金时的拒绝文案（负数不是「未填写」，绝不静默按比例重算）。</summary>
    public const string DepositNegativeText =
        "PI 定金金额不能为负：负数不是「未填写」，服务端不会静默按比例重算，请在提交前修正或置 0（0 = 按比例自动计算）";

    /// <summary>定金金额超出持久化 / 币种精度时的拒绝文案（绝不静默取整）。</summary>
    public const string DepositOverflowText =
        "PI 定金金额超出存储精度（DECIMAL(18,4) 与币种精度）：已拒绝，绝不静默取整";

    /// <summary>显式定金金额超过权威总额时的拒绝文案。</summary>
    public const string DepositExceedsTotalText = "PI 定金金额不能大于 PI 总额";

    /// <summary>契约口径文案（接口 / 文档同源）。</summary>
    public const string ContractText =
        "ERP-471：PI 商业条款只有一份权威契约（ProformaInvoiceCommercialValidationRules）："
        + "币种必须是 Currency 已定义值、汇率必须大于 0 且在 DECIMAL(18,6)（汇率列实际精度）内可表示、"
        + "逐行数量大于 0、单价非负且数量 / 单价 / 金额 / 合计 / 定金均在 DECIMAL(18,4)（声明列实际精度）内可表示，"
        + "行金额 / 合计 / 折人民币与自动定金一律由服务端按 CurrencyAmountRules 的币种精度"
        + "（JPY 等 0 位、其余 2 位、0.5 进位）从有效明细派生，绝不采信客户端行金额 / 合计，也不跨币种合计；"
        + "定金比例 0~100，显式定金 0 = 按比例自动计算，显式负数一律拒绝（绝不静默重算），显式正数必须"
        + "在币种精度内可表示且不得超过权威总额。新增 / 修改 / 带入预填 / 报价转 PI 与提交 / 审核 / 转销售订单"
        + "共用本契约，被拒绝的请求在单号预约与任何写入之前整体回滚，表头 / 明细 / 来源单据 / 状态 / 单号预约均不变。";

    /// <summary>币种代码（<see cref="CurrencyAmountRules"/> 的币种精度契约按代码字符串匹配）。</summary>
    public static string CurrencyCode(Currency currency) => currency.ToString();

    /// <summary>币种金额小数位（复用 <see cref="CurrencyAmountRules.PrecisionOf"/>：JPY / KRW / VND / IDR 为 0 位，其余 2 位）。</summary>
    public static int AmountDecimals(Currency currency)
        => CurrencyAmountRules.PrecisionOf(CurrencyCode(currency));

    /// <summary>按币种精度取整（复用 <see cref="CurrencyAmountRules.RoundAmount"/>：0.5 进位，确定性）。</summary>
    public static decimal RoundAmount(decimal amount, Currency currency)
        => CurrencyAmountRules.RoundAmount(amount, CurrencyCode(currency));

    /// <summary>值是否在 <c>DECIMAL(18,4)</c> 精度内可表示（最多 4 位小数且绝对值有界）。</summary>
    public static bool IsRepresentable(decimal value)
        => decimal.Round(value, AmountScale) == value
           && value >= MinAmount && value <= MaxAmount;

    /// <summary>汇率是否在 <c>DECIMAL(18,6)</c> 精度内可表示（最多 6 位小数且绝对值有界）。</summary>
    public static bool IsExchangeRateRepresentable(decimal value)
        => decimal.Round(value, ExchangeRateScale) == value
           && value >= MinExchangeRate && value <= MaxExchangeRate;

    /// <summary>单行「数量 × 单价」是否在持久化精度内可表示（乘法溢出按不可表示处理）。</summary>
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

    /// <summary>有效（未删除）明细行：提交 / 审核 / 转换一律以有效行为准。</summary>
    public static List<ProformaInvoiceDetail> ActiveDetails(ProformaInvoice pi)
    {
        ArgumentNullException.ThrowIfNull(pi);
        return (pi.Details ?? new List<ProformaInvoiceDetail>())
            .Where(d => !d.IsDeleted)
            .ToList();
    }

    /// <summary>
    /// 客户端写入（新增 / 修改 / 带入预填后保存）的校验：允许显式支持的**空草稿**（无明细行），
    /// 但任何被提交的明细行都必须是合法商业行；注入软删除明细行一律拒绝。
    /// </summary>
    public static void ValidateIncomingDraft(ProformaInvoice pi)
    {
        ArgumentNullException.ThrowIfNull(pi);

        var injected = (pi.Details ?? new List<ProformaInvoiceDetail>()).FirstOrDefault(d => d.IsDeleted);
        if (injected is not null)
            throw BusinessException.InvalidParameter(SoftDeletedDetailText);

        ValidateCommercialTerms(pi, requireOperationalLines: false);
    }

    /// <summary>
    /// 已持久化记录的条款校验（按**有效明细**校验，不含「软删除行注入」检查 —— 历史记录本就可能带有软删除明细行）：
    /// 提交 / 审核 / 转换在锁内重读后复核既有商业条款，非法历史数量绝不成为实时履约。
    /// 本方法只校验，绝不回填、重置或改写既有记录的任何字段。
    /// </summary>
    public static void ValidatePersisted(ProformaInvoice pi)
    {
        ArgumentNullException.ThrowIfNull(pi);
        ValidateCommercialTerms(pi, requireOperationalLines: false);
    }

    /// <summary>
    /// 提交 / 审核 / 转销售订单等**下游承诺**的校验：在条款校验之上要求至少一行有效明细，
    /// 保证非法历史数量不可能成为实时履约（本方法只校验，不改写任何字段，也不回填历史快照）。
    /// </summary>
    public static void ValidateOperational(ProformaInvoice pi)
    {
        ArgumentNullException.ThrowIfNull(pi);
        ValidateCommercialTerms(pi, requireOperationalLines: true);
    }

    /// <summary>
    /// 客户端写入的完整整理入口：先 <see cref="ValidateIncomingDraft"/>（失败即原样抛出，不做任何字段改写），
    /// 再重排行号、按币种精度派生行金额 / 合计 / 折人民币，并按比例自动补齐定金（仅在定金为 0 时）。
    /// </summary>
    public static void NormalizeIncomingDraft(ProformaInvoice pi)
    {
        ValidateIncomingDraft(pi);
        NormalizeValidated(pi);
    }

    /// <summary>
    /// 有效明细条款校验（币种 / 汇率 / 逐行数量 · 单价 / 派生金额与合计的精度与溢出 / 定金比例与金额）：
    /// 任一项不满足即返回受控业务错误（<see cref="ErrorCodes.InvalidParameter"/>），绝不静默取整、
    /// 绝不把正数量舍入为 0，也绝不写入与明细合计不一致的金额。
    /// </summary>
    private static void ValidateCommercialTerms(ProformaInvoice pi, bool requireOperationalLines)
    {
        if (!Enum.IsDefined(typeof(Currency), pi.Currency))
            throw BusinessException.InvalidParameter(CurrencyText);

        if (pi.ExchangeRate <= 0m || !IsExchangeRateRepresentable(pi.ExchangeRate))
            throw BusinessException.InvalidParameter(ExchangeRateText);

        var details = ActiveDetails(pi);
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
                        $"PI 明细第 {line} 行数量必须大于 0 且在存储精度（DECIMAL(18,4)）内可表示：当前值 {detail.Quantity}");

                if (detail.UnitPrice < 0m || !IsRepresentable(detail.UnitPrice))
                    throw BusinessException.InvalidParameter(
                        $"PI 明细第 {line} 行单价不能为负且必须在存储精度（DECIMAL(18,4)）内可表示：当前值 {detail.UnitPrice}");

                var amount = RoundAmount(detail.Quantity * detail.UnitPrice, pi.Currency);
                if (!IsRepresentable(amount))
                    throw BusinessException.InvalidParameter(
                        $"PI 明细第 {line} 行金额 {amount} 超出存储精度（DECIMAL(18,4)）：已拒绝，绝不静默取整改变明细合计");

                total += amount;
            }

            var roundedTotal = RoundAmount(total, pi.Currency);
            if (!IsRepresentable(roundedTotal))
                throw BusinessException.InvalidParameter(AmountOverflowText);

            var cny = CurrencyAmountRules.RoundAmount(roundedTotal * pi.ExchangeRate,
                CurrencyAmountRules.DefaultCurrency);
            if (!IsRepresentable(cny))
                throw BusinessException.InvalidParameter(AmountOverflowText);

            ValidateDeposit(pi, roundedTotal);
        }
        catch (OverflowException)
        {
            // decimal 乘法 / 加法溢出：转换为受控业务错误，绝不落库为截断值。
            throw BusinessException.InvalidParameter(AmountOverflowText);
        }
    }

    /// <summary>
    /// 定金口径校验（与整理口径**同一份**实现）：
    /// 比例 0~100 且可表示；显式 0 = 自动按比例计算；显式负数拒绝；显式正数必须在币种精度内
    /// 可表示且不得超过权威总额（自动计算的定金同样按币种精度取整）。
    /// </summary>
    private static void ValidateDeposit(ProformaInvoice pi, decimal totalAmount)
    {
        if (pi.DepositRatio < MinDepositRatio || pi.DepositRatio > MaxDepositRatio
            || !IsRepresentable(pi.DepositRatio))
            throw BusinessException.InvalidParameter(DepositRatioText);

        // 自动定金也按币种精度派生（JPY 等无小数币种取整到整数），并固化可表示性。
        var depositByRatio = RoundAmount(totalAmount * pi.DepositRatio / 100m, pi.Currency);
        if (!IsRepresentable(depositByRatio))
            throw BusinessException.InvalidParameter(DepositOverflowText);

        if (pi.DepositAmount < 0m)
            throw BusinessException.InvalidParameter(DepositNegativeText);

        if (pi.DepositAmount == 0m) return;   // 显式 0 = 按比例自动计算（既有语义保留）

        if (!IsRepresentable(pi.DepositAmount) || RoundAmount(pi.DepositAmount, pi.Currency) != pi.DepositAmount)
            throw BusinessException.InvalidParameter(DepositOverflowText);

        if (pi.DepositAmount > totalAmount)
            throw BusinessException.InvalidParameter(DepositExceedsTotalText);
    }

    /// <summary>
    /// 已经过校验的 PI 整理：行号按数组顺序重排为 1..n、主键 / 外键由服务端重置、行金额按币种精度派生、
    /// 合计与折人民币由服务端复核、定金仅在显式 0 时按比例自动补齐；客户端提交的行号 / 金额 / 合计 /
    /// 自动定金一律不被信任。
    /// </summary>
    private static void NormalizeValidated(ProformaInvoice pi)
    {
        var line = 0;
        var total = 0m;
        // 允许「未提供明细集合」的显式空草稿（与校验口径一致：空集合与空引用等价）。
        var details = pi.Details ?? new List<ProformaInvoiceDetail>();
        foreach (var detail in details)
        {
            detail.Id = 0;
            detail.PiId = pi.Id;
            detail.PiNo = pi.PiNo;
            detail.SortNo = ++line;
            detail.Amount = RoundAmount(detail.Quantity * detail.UnitPrice, pi.Currency);
            detail.CreatedAt = DateTime.Now;
            total += detail.Amount;
        }

        pi.TotalAmount = RoundAmount(total, pi.Currency);
        pi.TotalAmountCny = CurrencyAmountRules.RoundAmount(
            pi.TotalAmount * pi.ExchangeRate, CurrencyAmountRules.DefaultCurrency);
        if (pi.PiDate == default) pi.PiDate = DateTime.Today;

        // 定金：显式 0（或未填写 = 0）按比例自动计算；显式正数保留（已在校验阶段确认 <= 总额且符合精度）。
        if (pi.DepositAmount == 0m)
            pi.DepositAmount = RoundAmount(pi.TotalAmount * pi.DepositRatio / 100m, pi.Currency);
    }
}
