using ERP.Application.Common;
using ERP.Application.DTOs;

namespace ERP.Application.Services;

/// <summary>
/// 只读补货工作台（ERP-106）的纯规则（无数据库依赖，便于逐条单测）：
/// 补货建议（低于最低库存 + 有效上限目标）的判定、阈值缺失 / 无效的显式标注、货源可用性的显式文案，
/// 以及只读 / 边界 / 免责声明。
/// <para>边界：本规则只做读取口径的判定——不自动选择供应商、不生成采购报价或采购订单、
/// 不改写任何库存、单据与主数据，也不做跨仓汇总与单位换算。</para>
/// </summary>
public static class StockReplenishmentRules
{
    // ==================== 0. 建议状态常量 ====================

    /// <summary>建议补货（低于最低库存且存在有效上限目标，给出补货至上限的数量）</summary>
    public const string StateReplenish = "replenish";

    /// <summary>低于最低库存但无有效上限目标（不给补货数量）</summary>
    public const string StateBelowMinNoTarget = "below-min-no-target";

    /// <summary>库存充足（不低于最低库存）</summary>
    public const string StateAdequate = "adequate";

    /// <summary>阈值缺失（最低库存缺失，无法判定是否低于最低）</summary>
    public const string StateMissingThreshold = "missing-threshold";

    /// <summary>阈值无效（上限低于最低库存）</summary>
    public const string StateInvalidThreshold = "invalid-threshold";

    /// <summary>启用状态（与实体 / 货源关系口径一致）</summary>
    public const int ActiveStatus = 1;

    // ==================== 1. 文案 ====================

    /// <summary>只读声明（界面与接口统一声明）</summary>
    public const string ReadOnlyText =
        "只读补货工作台：仅读取现有库存、商品最低 / 上限库存与启用中的货源关系，"
        + "不创建订单、不自动选择供应商、不改写库存，也不新增 / 修改 / 删除任何记录";

    /// <summary>边界口径文案</summary>
    public const string BoundaryText =
        "口径：按仓库逐行展示（仓库 + 商品），不跨仓汇总、不做单位换算；"
        + "建议补货量 = 库存上限 − 现有库存，仅在「低于最低库存且存在有效上限目标」时给出，其余情况显式标注不推断";

    /// <summary>免责文案（哪些结论不能从补货建议推出）</summary>
    public const string DisclaimerText =
        "货源信息仅作参考：不自动选择供应商、不生成采购订单、不改变库存；"
        + "补货建议只是阈值算术证据，不是采购指令，下单仍需人工在采购模块显式完成";

    // ==================== 2. 纯计算与分类 ====================

    /// <summary>补货建议结果（状态 + 建议补货量；无法建议时为 null）</summary>
    public readonly record struct ReplenishmentDecision(string State, decimal? SuggestedTopUp);

    /// <summary>最低库存阈值是否有效（0 或负数 = 未设置 / 无效）</summary>
    public static bool IsValidMinThreshold(decimal minStock) => minStock > 0m;

    /// <summary>上限目标是否有效（大于 0 且不低于最低库存，否则补货至上限仍低于最低，不成立）</summary>
    public static bool IsValidMaxTarget(decimal minStock, decimal maxStock) =>
        maxStock > 0m && maxStock >= minStock;

    /// <summary>现有库存是否低于最低库存（最低阈值无效时恒为 false，不误报）</summary>
    public static bool IsBelowMinimum(decimal quantity, decimal minStock) =>
        IsValidMinThreshold(minStock) && quantity < minStock;

    /// <summary>
    /// 判定单行的补货建议（纯函数）：低于最低库存且存在有效上限目标时给出补货至上限的数量，
    /// 否则按「低于最低但无目标 / 阈值缺失 / 阈值无效 / 库存充足」显式分类，绝不推断。
    /// </summary>
    public static ReplenishmentDecision Evaluate(decimal quantity, decimal minStock, decimal maxStock)
    {
        var hasMin = IsValidMinThreshold(minStock);
        var hasMax = maxStock > 0m;
        var belowMin = hasMin && quantity < minStock;

        if (!hasMin && !hasMax)
            return new ReplenishmentDecision(StateMissingThreshold, null);

        if (hasMin && !hasMax)
            return belowMin
                ? new ReplenishmentDecision(StateBelowMinNoTarget, null)
                : new ReplenishmentDecision(StateAdequate, null);

        if (!hasMin && hasMax)
            return new ReplenishmentDecision(StateMissingThreshold, null);

        if (maxStock < minStock)
            return new ReplenishmentDecision(StateInvalidThreshold, null);

        return belowMin
            ? new ReplenishmentDecision(StateReplenish, maxStock - quantity)
            : new ReplenishmentDecision(StateAdequate, null);
    }

    /// <summary>建议状态的中文文案（与 <see cref="Evaluate"/> 一一对应）</summary>
    public static string RecommendationText(ReplenishmentDecision decision, decimal minStock, decimal maxStock)
    {
        return decision.State switch
        {
            StateReplenish => $"建议补货 {FormatQty(decision.SuggestedTopUp)} 至上限 {FormatQty(maxStock)}",
            StateBelowMinNoTarget => "低于最低库存，但未设置有效库存上限，暂不给出补货数量",
            StateAdequate => "库存充足（不低于最低库存）",
            StateMissingThreshold => "未设置有效库存阈值（最低库存缺失）",
            StateInvalidThreshold => "库存阈值无效（上限低于最低库存）",
            _ => "无法判定"
        };
    }

    /// <summary>货源可用性文案（显式说明能否作为启用货源参考，避免停用 / 已删除供应商被静默当成可用）</summary>
    public static string SourcingText(int availableCount, int totalCount)
    {
        if (totalCount == 0) return "无可用货源（该商品无启用中的货源关系）";
        if (availableCount == 0) return "货源供应商已停用 / 已删除，仅作历史参考";
        return $"启用货源 {availableCount} 条（仅作参考，不自动选供应商 / 下单）";
    }

    /// <summary>数值文案：去掉多余小数位，便于展示补货量 / 上限</summary>
    private static string FormatQty(decimal? value) =>
        value.HasValue ? value.Value.ToString("0.####") : "—";

    // ==================== 3. 参数校验 ====================

    /// <summary>仓库筛选必填（补货工作台按仓库逐行查看，不跨仓汇总）</summary>
    public static long RequireWarehouse(long? warehouseId)
    {
        if (!warehouseId.HasValue || warehouseId.Value <= 0)
            throw BusinessException.InvalidParameter("请先选择仓库（补货工作台按仓库筛选）");
        return warehouseId.Value;
    }

    /// <summary>商品筛选校验（显式正整数 Id；空值 = 不过滤）</summary>
    public static long? NormalizeProductFilter(long? productId)
    {
        if (productId is null) return null;
        if (productId <= 0)
            throw BusinessException.InvalidParameter("商品 Id 必须为正整数（按商品筛选时不接受 0 或负数）");
        return productId;
    }
}
