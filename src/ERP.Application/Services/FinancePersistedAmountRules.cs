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
}
