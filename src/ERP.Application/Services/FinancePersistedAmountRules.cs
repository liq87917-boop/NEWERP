using ERP.Application.Common;

namespace ERP.Application.Services;

/// <summary>
/// 财务单据「单个标量金额列」的**实际存储边界**口径（ERP-508；收款单 / 定金申请单写入与提交 / 审核共用，
/// 后续付款类单据可直接复用，系统内不允许出现第二套标量金额边界规则）：
/// <list type="number">
/// <item>这些单据的金额列在 EF 模型中**未**显式 <c>HasPrecision</c>，因此按 EF 默认精度落库为
/// <c>DECIMAL(18,2)</c>（16 位整数 + 2 位小数）；超出该区间的正金额能通过「大于 0」检查，却会在
/// SQL 写入时截断 / 溢出，因此必须在落库之前拒绝（<b>绝不静默截断或取整</b>）；</item>
/// <item>本类只对**已按币种精度取整后的单个标量金额**做区间判定，绝不重新取整、绝不改写调用方的取整口径
/// （币种取整仍唯一由 <see cref="CurrencyAmountRules.RoundAmount"/> 按 <c>AwayFromZero</c> 完成，
/// 零小数币种 <c>JPY / KRW / VND / IDR</c> 语义原样保留）；</item>
/// <item>边界：本类只做**纯判定**，不访问数据库、不写库、不涉及任何单据状态；且**绝不**用于聚合报表合计、
/// 只读权威余额或权威历史金额比对 —— 那些求和 / 口径可能合法地超过单个存储列的容量。</item>
/// </list>
/// </summary>
public static class FinancePersistedAmountRules
{
    /// <summary>金额列实际精度（<c>DECIMAL(18,2)</c>，与 EF 默认值同源）。</summary>
    public const int DecimalPrecision = 18;

    /// <summary>金额列实际小数位（<c>DECIMAL(18,2)</c>，与 <see cref="DecimalPrecision"/> 同源）。</summary>
    public const int DecimalScale = 2;

    /// <summary><c>DECIMAL(18,2)</c> 的最大可表示值（16 位整数 + 2 位小数）。</summary>
    public const decimal MaxDecimal18Scale2 = 9_999_999_999_999_999.99m;

    /// <summary><c>DECIMAL(18,2)</c> 的最小可表示值。</summary>
    public const decimal MinDecimal18Scale2 = -9_999_999_999_999_999.99m;

    /// <summary>比例 / 基数等 4 位小数列的实际小数位（<c>DECIMAL(18,4)</c>，与 EF 模型显式 <c>HasPrecision(18,4)</c> 同源）。</summary>
    public const int DecimalScale4 = 4;

    /// <summary>汇率列的实际小数位（<c>DECIMAL(18,6)</c>，与 EF 模型显式 <c>HasPrecision(18,6)</c> 同源）。</summary>
    public const int DecimalScale6 = 6;

    /// <summary><c>DECIMAL(18,4)</c> 的最大可表示值（14 位整数 + 4 位小数；分摊比例 / 分摊基数值等列）。</summary>
    public const decimal MaxDecimal18Scale4 = 99_999_999_999_999.9999m;

    /// <summary><c>DECIMAL(18,4)</c> 的最小可表示值。</summary>
    public const decimal MinDecimal18Scale4 = -99_999_999_999_999.9999m;

    /// <summary><c>DECIMAL(18,6)</c> 的最大可表示值（12 位整数 + 6 位小数；汇率列）。</summary>
    public const decimal MaxDecimal18Scale6 = 999_999_999_999.999999m;

    /// <summary><c>DECIMAL(18,6)</c> 的最小可表示值。</summary>
    public const decimal MinDecimal18Scale6 = -999_999_999_999.999999m;

    /// <summary>标量金额超出实际存储精度时的拒绝文案（接口 / 文档同源）。</summary>
    public const string OverflowText =
        "金额超出可存储范围（DECIMAL(18,2)）：已拒绝，绝不静默截断或取整";

    /// <summary>
    /// 值是否可在 <c>DECIMAL(18,2)</c> 内**原样**表示（小数位不超过 2 且绝对值有界）。
    /// <para>调用方必须传入**已按币种精度取整**的金额：本方法不代替币种取整，也不做任何改写。</para>
    /// </summary>
    public static bool IsRepresentable(decimal value)
        => decimal.Round(value, DecimalScale) == value
           && value >= MinDecimal18Scale2 && value <= MaxDecimal18Scale2;

    /// <summary>
    /// 校验（并按原值返回）一个**已按币种精度取整**的标量金额可被数据库原样存储；超出 <c>DECIMAL(18,2)</c>
    /// 时抛受控业务错误（<c>InvalidParameter</c>）。
    /// <para>纯函数：不取整、不截断、不改写任何状态；<paramref name="amountLabel"/> 只用于错误文案
    /// （例如「收款金额按币种精度取整后」）。</para>
    /// </summary>
    public static decimal EnsureRepresentable(decimal currencyRoundedAmount, string amountLabel)
    {
        ArgumentNullException.ThrowIfNull(amountLabel);
        if (!IsRepresentable(currencyRoundedAmount))
            throw BusinessException.InvalidParameter(
                $"{amountLabel}{OverflowText}（当前值 {currencyRoundedAmount}）");
        return currencyRoundedAmount;
    }

    /// <summary>
    /// 值按 <paramref name="scale"/> 位小数（0.5 进位，与 SQL Server 写入时的取整同向）取整后是否仍落在
    /// <c>DECIMAL(18,scale)</c> 的可表示区间内。
    /// <para>调用方必须传入**已经按业务口径取整**或本来就是派生计算结果的值：本方法只做区间判定，
    /// 不代替币种取整、绝不改写任何值；系统内支持的实际列精度只有 2（金额）/ 4（比例 / 基数）/ 6（汇率）三种。</para>
    /// </summary>
    public static bool IsWithinDecimal18(decimal value, int scale)
    {
        var (min, max) = BoundsOfScale(scale);
        var rounded = decimal.Round(value, scale, MidpointRounding.AwayFromZero);
        return rounded >= min && rounded <= max;
    }

    /// <summary>
    /// 校验（并按原值返回）一个标量能在 <c>DECIMAL(18,scale)</c> 内表示；超出时抛受控业务错误
    /// （<c>InvalidParameter</c>）。
    /// <para>纯函数：不取整入参、不截断、不改写任何状态；<paramref name="amountLabel"/> 只用于错误文案。
    /// 判定顺序与 SQL Server 一致（先按列小数位取整，再判上界），因此「取整进位后越过上限」的值同样被拒绝。</para>
    /// </summary>
    public static decimal EnsureWithinDecimal18(decimal value, int scale, string amountLabel)
    {
        ArgumentNullException.ThrowIfNull(amountLabel);
        _ = BoundsOfScale(scale);
        if (!IsWithinDecimal18(value, scale))
            throw BusinessException.InvalidParameter(
                $"{amountLabel}超出可存储范围（DECIMAL(18,{scale})）：已拒绝，绝不静默截断或取整（当前值 {value}）");
        return value;
    }

    /// <summary>
    /// 受控乘积（例如「原币金额 × 汇率 = 折人民币金额」）：CLR 十进制乘法溢出（<see cref="OverflowException"/>）
    /// 一律转换为受控业务错误 —— 绝不以未处理异常崩溃，也绝不静默截断 / 取整。
    /// <para>纯计算：不访问数据库、不改写任何状态；调用方必须在单号预约与任何写入之前调用。</para>
    /// </summary>
    public static decimal SafeMultiply(decimal left, decimal right, string amountLabel)
    {
        ArgumentNullException.ThrowIfNull(amountLabel);
        try
        {
            return left * right;
        }
        catch (OverflowException)
        {
            throw BusinessException.InvalidParameter(
                $"{amountLabel}的乘法结果溢出（{left} × {right} 超出十进制可表示范围）：已拒绝，绝不静默截断或取整");
        }
    }

    /// <summary>
    /// 受控乘积 + 目标列精度判定：乘积先做溢出防护，再按 <paramref name="scale"/> 位小数取整（0.5 进位，与
    /// SQL Server 写入取整同向）并判定是否落在 <c>DECIMAL(18,scale)</c> 内（两者任一不满足即受控拒绝）；
    /// 返回**取整后的乘积**（调用方据此落库，绝不静默截断 / 溢出）。
    /// </summary>
    public static decimal MultiplyScalar(decimal left, decimal right, int scale, string amountLabel)
    {
        var product = SafeMultiply(left, right, amountLabel);
        return EnsureWithinDecimal18(
            decimal.Round(product, scale, MidpointRounding.AwayFromZero), scale, amountLabel);
    }

    /// <summary>按实际列精度返回 <c>DECIMAL(18,scale)</c> 的可表示区间（只支持 2 / 4 / 6）。</summary>
    private static (decimal Min, decimal Max) BoundsOfScale(int scale) => scale switch
    {
        DecimalScale => (MinDecimal18Scale2, MaxDecimal18Scale2),
        DecimalScale4 => (MinDecimal18Scale4, MaxDecimal18Scale4),
        DecimalScale6 => (MinDecimal18Scale6, MaxDecimal18Scale6),
        _ => throw new ArgumentOutOfRangeException(nameof(scale), scale,
            "系统内只定义了 DECIMAL(18,2) / DECIMAL(18,4) / DECIMAL(18,6) 三种实际金额列精度口径")
    };
}
