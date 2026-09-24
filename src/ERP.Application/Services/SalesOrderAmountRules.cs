using ERP.Application.Common;
using ERP.Domain.Entities;

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
}
