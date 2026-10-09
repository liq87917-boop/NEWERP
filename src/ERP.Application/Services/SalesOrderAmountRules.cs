using ERP.Application.Common;
using ERP.Domain.Entities;
using ERP.Domain.Enums;

namespace ERP.Application.Services;

/// <summary>
/// 销售订单金额与业务校验的**唯一权威口径**（ERP-047 从《销售订单控制器》原样抽出，供控制器与
/// 变更申请登记等复用方共用，系统内不允许出现第二套销售订单合计 / 校验规则）：
/// <list type="number">
/// <item>明细金额：<c>金额 = 数量 × 单价</c>（逐行，服务端重算，不信任客户端金额）；</item>
/// <item>订单总额：<c>总额 = Σ 明细数量 × 单价</c>；</item>
/// <item>定金金额：<c>定金金额 = 总额 × 定金比例 %</c>；</item>
/// <item>业务校验：佣金比例必须在 0~100 之间（历史单据不填时为 0，不受影响）。</item>
/// </list>
/// <para>边界：本类只做**纯计算与校验**，不访问数据库、不写库、不改变单据状态，也不涉及库存 /
/// 出运 / 财务口径。金额保留原始精度（不按币种取整），与销售订单既有页面录入路径完全一致。</para>
/// <para>ERP-422 新增<b>新写入校验</b>（<see cref="ValidateNewWrite"/>）：新增 / 修改 / 提交 / 审核前对
/// 「明细非空、数量为正、单价非负、币种受支持、汇率大于 0、定金 / 佣金比例 0~100、逐行金额与总额 /
/// 定金在 EF 实际精度内可表示」做一次权威校验，任一项不满足即返回受控业务错误（绝不静默取整、
/// 绝不把正数量舍入为 0、绝不写入与明细合计不一致的金额）。历史读取 / 打印路径不调用本校验，也不会
/// 被本类**改写**任何已持久化值。</para>
/// </summary>
public static class SalesOrderAmountRules
{
    /// <summary>定金比例下限（%）</summary>
    public const decimal MinDepositRatio = 0m;

    /// <summary>定金比例上限（%）</summary>
    public const decimal MaxDepositRatio = 100m;

    /// <summary>佣金比例下限（%）</summary>
    public const decimal MinCommissionRatio = 0m;

    /// <summary>佣金比例上限（%）</summary>
    public const decimal MaxCommissionRatio = 100m;

    /// <summary>
    /// EF / 数据库实际精度：<c>DECIMAL(18,2)</c>。
    /// <para>销售订单主表与明细（<c>SalesOrders</c> / <c>SalesOrderDetails</c>）在 EF 模型中**未**显式
    /// <c>HasPrecision</c>，因此数量 / 单价 / 行金额 / 合计 / 定金 / 汇率 / 比例一律按 EF 默认的
    /// <c>DECIMAL(18,2)</c> 落库。校验必须使用**同一份**精度口径，否则正数量会被舍入为 0、或写入与明细
    /// 合计不一致的金额。</para>
    /// </summary>
    public const int DecimalPrecision = 18;

    /// <summary>EF / 数据库实际小数位：2（与 <see cref="DecimalPrecision"/> 同源）。</summary>
    public const int DecimalScale = 2;

    /// <summary><c>DECIMAL(18,2)</c> 的最大可表示值（16 位整数 + 2 位小数）。</summary>
    public const decimal MaxDecimal18Scale2 = 9_999_999_999_999_999.99m;

    /// <summary><c>DECIMAL(18,2)</c> 的最小可表示值。</summary>
    public const decimal MinDecimal18Scale2 = -9_999_999_999_999_999.99m;

    /// <summary>有效明细为空时的拒绝文案（新增 / 修改 / 提交 / 审核共用）。</summary>
    public const string EmptyDetailsText = "销售订单至少需要一行有效明细（未删除且已填写数量与单价）";

    /// <summary>币种不受支持（<see cref="Currency"/> 未定义值）时的拒绝文案。</summary>
    public const string CurrencyText = "销售订单币种不是系统受支持的币种（Currency 枚举未定义值）";

    /// <summary>汇率非法（非正数或超出存储精度）时的拒绝文案。</summary>
    public const string ExchangeRateText = "销售订单汇率必须大于 0 且在存储精度（DECIMAL(18,2)）内可表示";

    /// <summary>金额超出存储精度 / 溢出时的拒绝文案（绝不静默截断或取整）。</summary>
    public const string AmountOverflowText =
        "销售订单金额超出可存储范围（DECIMAL(18,2)）：已拒绝，绝不静默截断或取整改变明细合计";

    /// <summary>定金金额超出存储精度时的拒绝文案。</summary>
    public const string DepositOverflowText =
        "销售订单定金金额超出存储精度（DECIMAL(18,2)）：已拒绝，绝不静默取整";

    /// <summary>值是否在 <c>DECIMAL(18,2)</c> 精度内可表示（最多 2 位小数、且绝对值有界）。</summary>
    public static bool IsRepresentable(decimal value)
        => decimal.Round(value, DecimalScale) == value
           && value >= MinDecimal18Scale2 && value <= MaxDecimal18Scale2;

    /// <summary>单行明细金额是否在存储精度内可表示（逐行「数量 × 单价」）。</summary>
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

    /// <summary>明细金额口径：逐行 <c>金额 = 数量 × 单价</c>（服务端重算，忽略客户端传入的金额）</summary>
    public static void ApplyDetailAmounts(SalesOrder order)
    {
        ArgumentNullException.ThrowIfNull(order);

        foreach (var detail in order.Details)
            detail.Amount = detail.Quantity * detail.UnitPrice;
    }

    /// <summary>合计口径：总额 = Σ 明细数量 × 单价；定金金额 = 总额 × 定金比例 %</summary>
    public static void Calculate(SalesOrder order)
    {
        ArgumentNullException.ThrowIfNull(order);

        order.TotalAmount = order.Details.Sum(d => d.Quantity * d.UnitPrice);
        order.DepositAmount = order.TotalAmount * order.DepositRatio / 100;
    }

    /// <summary>业务字段校验（佣金比例 0~100；历史单据不填时为 0，不受影响）</summary>
    public static void Validate(SalesOrder order)
    {
        ArgumentNullException.ThrowIfNull(order);

        if (order.CommissionRatio < MinCommissionRatio || order.CommissionRatio > MaxCommissionRatio)
            throw BusinessException.InvalidParameter(
                $"佣金比例必须在 {MinCommissionRatio:0}~{MaxCommissionRatio:0} 之间");
    }

    /// <summary>定金比例校验（0~100；用于「带入预填 / 变更申请」这类需要显式复核比例的服务端路径）</summary>
    public static void ValidateDepositRatio(decimal depositRatio)
    {
        if (depositRatio < MinDepositRatio || depositRatio > MaxDepositRatio)
            throw BusinessException.InvalidParameter(
                $"定金比例必须在 {MinDepositRatio:0}~{MaxDepositRatio:0} 之间");
    }

    /// <summary>明细取值校验（数量必须 &gt; 0、单价不得为负；与销售订单带入路径同口径）</summary>
    public static void ValidateDetailValues(IEnumerable<SalesOrderDetail> details)
    {
        ArgumentNullException.ThrowIfNull(details);

        foreach (var detail in details)
        {
            if (detail.Quantity <= 0)
                throw BusinessException.InvalidParameter("销售订单明细数量必须大于 0");
            if (detail.UnitPrice < 0)
                throw BusinessException.InvalidParameter("销售订单明细单价不能为负");
        }
    }

    /// <summary>有效明细（未软删除）集合；<c>null</c> 明细集合按空集合处理（与「必须至少一行」同口径）。</summary>
    public static IReadOnlyList<SalesOrderDetail> ActiveDetails(SalesOrder order)
    {
        ArgumentNullException.ThrowIfNull(order);
        return order.Details is null
            ? Array.Empty<SalesOrderDetail>()
            : order.Details.Where(d => !d.IsDeleted).ToList();
    }

    /// <summary>
    /// **新写入**（新增 / 修改 / 提交 / 审核）的唯一权威校验（ERP-422）：在单号预约与任何字段 / 明细改写
    /// **之前**调用，也用于提交 / 审核在既有行锁内复核**已持久化条款**。逐项要求：
    /// <list type="number">
    /// <item>有效明细非空（未软删除）；</item>
    /// <item>逐行数量 &gt; 0 且可在实际 EF 精度内表示（<b>绝不</b>把正数量静默舍入为 0）；</item>
    /// <item>逐行单价 ≥ 0 且可在实际 EF 精度内表示（保留既有「零单价」政策）；</item>
    /// <item>币种是 <see cref="Currency"/> 已定义值（不跨币种合计）；</item>
    /// <item>汇率 &gt; 0 且可在实际 EF 精度内表示；</item>
    /// <item>定金比例与佣金比例均在 0~100（含端点）；</item>
    /// <item>逐行「数量 × 单价」、总额与定金金额均可在实际 EF 精度内表示，且全程 checked：
    /// 溢出 / 不可表示一律返回受控业务错误（<see cref="ErrorCodes.InvalidParameter"/>），
    /// 绝不静默取整、绝不写入与明细合计不一致的金额。</item>
    /// </list>
    /// <para>本方法只**校验**，不改写任何字段；历史读取 / 打印路径不调用它，因此既有历史单据仍可读、
    /// 不被自动修正。金额合计只累加货币金额，绝不跨单位累加数量。</para>
    /// </summary>
    public static void ValidateNewWrite(SalesOrder order)
    {
        ArgumentNullException.ThrowIfNull(order);

        var details = ActiveDetails(order);
        if (details.Count == 0)
            throw BusinessException.InvalidParameter(EmptyDetailsText);

        if (!Enum.IsDefined(typeof(Currency), order.Currency))
            throw BusinessException.InvalidParameter(CurrencyText);

        if (order.ExchangeRate <= 0m || !IsRepresentable(order.ExchangeRate))
            throw BusinessException.InvalidParameter(ExchangeRateText);

        // 定金比例 0~100（含端点）；佣金比例 0~100（历史单据不填时为 0，保持既有政策）。
        ValidateDepositRatio(order.DepositRatio);
        Validate(order);

        for (var i = 0; i < details.Count; i++)
            ValidateDetailLine(details[i], i + 1);

        ValidateRepresentableAmounts(details, order.DepositRatio);
    }

    /// <summary>逐行数量 / 单价校验（带行号；数量为正且可表示、单价非负且可表示）。</summary>
    private static void ValidateDetailLine(SalesOrderDetail detail, int line)
    {
        if (detail.Quantity <= 0m || !IsRepresentable(detail.Quantity))
            throw BusinessException.InvalidParameter(
                $"销售订单明细第 {line} 行数量必须大于 0 且在存储精度（DECIMAL(18,2)）内可表示：当前值 {detail.Quantity}");

        if (detail.UnitPrice < 0m || !IsRepresentable(detail.UnitPrice))
            throw BusinessException.InvalidParameter(
                $"销售订单明细第 {line} 行单价不能为负且必须在存储精度（DECIMAL(18,2)）内可表示：当前值 {detail.UnitPrice}");
    }

    /// <summary>
    /// 逐行金额、总额与定金金额的 checked 精度校验：任一不可表示 / 溢出都返回受控业务错误。
    /// 通过本校验后，<see cref="ApplyDetailAmounts"/> 与 <see cref="Calculate"/> 的结果可被 EF **原样**持久化，
    /// 明细金额之和与主表总额逐分一致（不存在落库取整差）。
    /// </summary>
    private static void ValidateRepresentableAmounts(IReadOnlyList<SalesOrderDetail> details, decimal depositRatio)
    {
        try
        {
            var total = 0m;
            for (var i = 0; i < details.Count; i++)
            {
                var amount = details[i].Quantity * details[i].UnitPrice;
                if (!IsRepresentable(amount))
                    throw BusinessException.InvalidParameter(
                        $"销售订单明细第 {i + 1} 行金额 {amount} 超出存储精度（DECIMAL(18,2)）："
                        + "已拒绝，绝不静默取整改变明细合计");
                total += amount;
            }

            if (!IsRepresentable(total))
                throw BusinessException.InvalidParameter(AmountOverflowText);

            var deposit = total * depositRatio / 100m;
            if (!IsRepresentable(deposit))
                throw BusinessException.InvalidParameter(DepositOverflowText);
        }
        catch (OverflowException)
        {
            // decimal 乘法 / 加法溢出：转换为受控业务错误，绝不落库为截断值。
            throw BusinessException.InvalidParameter(AmountOverflowText);
        }
    }
}
