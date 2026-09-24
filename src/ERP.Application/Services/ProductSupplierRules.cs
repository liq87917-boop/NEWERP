using ERP.Application.Common;
using ERP.Domain.Entities;
using System.Text;

namespace ERP.Application.Services;

/// <summary>
/// 商品 / SKU 货源关系的纯规则（ERP-038，无数据库依赖，便于逐条单测）：
/// 文本与数值规范化（供应商货号 / 采购单位 / MOQ / 交期 / 备注）、作用域键推导、
/// 重复关系判定与「同一范围最多一条启用首选」判定，以及不可用引用的显式文案。
/// <para>边界：本规则只做主数据关系自身的判定——不选供应商、不定价、不生成采购报价或采购订单、
/// 不改写任何历史单据与库存，也不涉及任何外部系统。</para>
/// </summary>
public static class ProductSupplierRules
{
    /// <summary>供应商货号最大长度（与实体 / 建表脚本一致）</summary>
    public const int MaxSupplierItemCodeLength = 100;

    /// <summary>采购单位最大长度</summary>
    public const int MaxPurchaseUnitLength = 20;

    /// <summary>备注最大长度</summary>
    public const int MaxRemarkLength = 500;

    /// <summary>作用域键最大长度</summary>
    public const int MaxScopeKeyLength = 30;

    /// <summary>交期天数上限（约 10 年；0 = 未指定，不允许负数）</summary>
    public const int MaxLeadTimeDays = 3650;

    /// <summary>最小起订量上限（0 = 未指定，不允许负数）</summary>
    public const decimal MaxMinOrderQty = 999999999m;

    /// <summary>
    /// 单个商品（含其全部规格）允许维护的货源关系条数上限（含停用行）：
    /// 商品侧读取与维护视图按此上限收敛，保证列表**有界**。
    /// </summary>
    public const int MaxRelationshipsPerProduct = 200;

    /// <summary>单个供应商允许维护的货源关系条数上限（含停用行）：供应商侧读取与维护同样有界。</summary>
    public const int MaxRelationshipsPerSupplier = 500;

    /// <summary>启用状态</summary>
    public const int ActiveStatus = 1;

    /// <summary>停用状态</summary>
    public const int DisabledStatus = 0;

    /// <summary>商品级货源关系的作用域键（整品通用，不绑定具体规格）</summary>
    public const string ProductScopeKey = "P";

    /// <summary>商品级作用域文案</summary>
    public const string ProductScopeText = "商品级（整品通用）";

    /// <summary>规格级作用域文案</summary>
    public const string VariantScopeText = "规格级（SKU 专用）";

    /// <summary>引用不可用（供应商 / 规格已停用或已删除）时的显式展示标注</summary>
    public const string UnavailableMark = "（已停用/不可用）";

    /// <summary>归一化后的货源关系主数据字段（服务端权威口径，客户端提交值一律不被采信）</summary>
    public readonly record struct NormalizedSourcing(
        string SupplierItemCode,
        string PurchaseUnit,
        decimal MinOrderQty,
        int LeadTimeDays,
        string Remark);

    /// <summary>把空白字符压缩为单个空格并去掉首尾空白（中文全角空格同样按空白处理）</summary>
    private static string CollapseWhitespace(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        var builder = new StringBuilder(value.Length);
        var pendingSpace = false;
        foreach (var ch in value.Trim())
        {
            if (char.IsWhiteSpace(ch) || ch == '\u3000')
            {
                if (builder.Length > 0) pendingSpace = true;
                continue;
            }
            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }
            builder.Append(ch);
        }
        return builder.ToString();
    }

    /// <summary>展示值：压缩空白并去首尾空白，保留原大小写（货号 / 单位不做大小写改写）</summary>
    public static string DisplayValue(string? value) => CollapseWhitespace(value);

    /// <summary>
    /// 作用域键推导：规格 Id 有值且大于 0 时为 <c>V{规格Id}</c>，否则为商品级 <c>P</c>。
    /// 客户端提交的作用域键一律不被采信，全部由本方法按规格 Id 推导。
    /// </summary>
    public static string BuildScopeKey(long? variantId) =>
        variantId.HasValue && variantId.Value > 0 ? $"V{variantId.Value}" : ProductScopeKey;

    /// <summary>作用域键是否为规格级</summary>
    public static bool IsVariantScope(string? scopeKey) =>
        !string.Equals(scopeKey ?? string.Empty, ProductScopeKey, StringComparison.Ordinal);

    /// <summary>作用域文案（清晰区分商品级与规格级两类关系）</summary>
    public static string ScopeText(string? scopeKey) =>
        IsVariantScope(scopeKey) ? VariantScopeText : ProductScopeText;

    /// <summary>状态口径：只接受 0（停用）与 1（启用），其余按参数错误拒绝</summary>
    public static int NormalizeStatus(int? status) => status switch
    {
        null => ActiveStatus,
        ActiveStatus => ActiveStatus,
        DisabledStatus => DisabledStatus,
        _ => throw BusinessException.InvalidParameter("货源关系状态只能是 1（启用）或 0（停用）")
    };

    /// <summary>
    /// 规范化并校验货源关系主数据字段：货号 / 单位 / 备注按长度收敛，
    /// MOQ 与交期不允许负数且不超过上限（未填按 0 = 未指定）。
    /// </summary>
    public static NormalizedSourcing Normalize(
        string? supplierItemCode, string? purchaseUnit, decimal? minOrderQty, int? leadTimeDays, string? remark)
    {
        var itemCode = DisplayValue(supplierItemCode);
        if (itemCode.Length > MaxSupplierItemCodeLength)
            throw BusinessException.InvalidParameter($"供应商货号长度不能超过 {MaxSupplierItemCodeLength} 个字符");

        var unit = DisplayValue(purchaseUnit);
        if (unit.Length > MaxPurchaseUnitLength)
            throw BusinessException.InvalidParameter($"采购单位长度不能超过 {MaxPurchaseUnitLength} 个字符");

        var moq = minOrderQty ?? 0m;
        if (moq < 0)
            throw BusinessException.InvalidParameter("最小起订量 MOQ 不能为负数（未指定请留空或填 0）");
        if (moq > MaxMinOrderQty)
            throw BusinessException.InvalidParameter($"最小起订量 MOQ 不能超过 {MaxMinOrderQty}");

        var leadTime = leadTimeDays ?? 0;
        if (leadTime < 0)
            throw BusinessException.InvalidParameter("交期天数不能为负数（未指定请留空或填 0）");
        if (leadTime > MaxLeadTimeDays)
            throw BusinessException.InvalidParameter($"交期天数不能超过 {MaxLeadTimeDays} 天");

        var normalizedRemark = DisplayValue(remark);
        if (normalizedRemark.Length > MaxRemarkLength)
            throw BusinessException.InvalidParameter($"备注长度不能超过 {MaxRemarkLength} 个字符");

        return new NormalizedSourcing(itemCode, unit, moq, leadTime, normalizedRemark);
    }

    /// <summary>
    /// 商品本身必须可新增货源关系：未删除且启用。
    /// 停用商品不能新增 / 重新启用货源关系（历史关系仍照常可读），避免把停用商品变成可用货源指引。
    /// </summary>
    public static void EnsureProductSelectable(BaseProduct? product)
    {
        if (product is null)
            throw BusinessException.NotFound("商品不存在或已删除，不能维护货源关系");
        if (product.Status != ActiveStatus)
            throw BusinessException.InvalidParameter(
                $"商品「{product.ProductName}」已停用，不能新增或启用货源关系（历史货源关系仍可读取）");
    }

    /// <summary>关系当前是否可作为「启用货源」被参考（未删除且启用）；停用 / 已删除只能作为历史读取</summary>
    public static bool IsSelectable(BaseProductSupplier? relation) =>
        relation is not null && !relation.IsDeleted && relation.Status == ActiveStatus;

    /// <summary>该行是否属于「启用中的首选」（首选唯一性只针对启用中的关系判定）</summary>
    public static bool IsPreferredActive(BaseProductSupplier? relation) =>
        IsSelectable(relation) && relation!.IsPreferred;

    /// <summary>
    /// 同一「商品 + 规格作用域 + 供应商」不允许重复维护（含停用行，软删除行不占用）：
    /// 重复关系会被拒绝，已停用的历史关系照常保留可读，恢复供货请直接启用原关系而不是另建一条。
    /// <para>数据库层由过滤唯一索引 <c>UX_BaseProductSuppliers_ScopeSupplier</c> 兜底并发写入。</para>
    /// </summary>
    public static void EnsureScopeSupplierUnique(
        IEnumerable<BaseProductSupplier> siblings, string scopeKey, long supplierId, long? excludeId = null)
    {
        var duplicated = siblings.Any(x =>
            !x.IsDeleted && x.Id != (excludeId ?? 0)
            && x.ScopeKey == scopeKey && x.SupplierId == supplierId);

        if (duplicated)
            throw BusinessException.Duplicate(
                "该供应商在相同商品 / 规格范围内已存在货源关系（含停用记录），不允许重复维护；"
                + "如需恢复供货请直接启用原有货源关系");
    }

    /// <summary>
    /// 同一「商品 + 规格作用域」最多一条**启用中的**首选货源关系。
    /// 停用 / 已删除行不占用首选位；更换首选必须走显式的「设为首选」操作（先释放旧首选再置新首选），
    /// 不允许通过「并列勾选」或依赖列表顺序来隐式取胜。
    /// <para>数据库层由过滤唯一索引 <c>UX_BaseProductSuppliers_ScopePreferred</c> 兜底。</para>
    /// </summary>
    public static void EnsurePreferredUnique(
        IEnumerable<BaseProductSupplier> siblings, string scopeKey, bool requirePreferred, long? excludeId = null)
    {
        if (!requirePreferred) return;

        var existing = siblings.Any(x =>
            x.Id != (excludeId ?? 0) && x.ScopeKey == scopeKey && IsPreferredActive(x));

        if (existing)
            throw BusinessException.Duplicate(
                "该商品 / 规格范围内已存在启用中的首选货源关系："
                + "更换首选请对目标关系执行「设为首选」（服务端会先释放旧首选再设置新首选），不支持并列填写");
    }

    /// <summary>单商品货源关系条数上限校验（含停用行，保证维护视图有界）</summary>
    public static void EnsureProductBound(int existingCount)
    {
        if (existingCount >= MaxRelationshipsPerProduct)
            throw BusinessException.InvalidParameter(
                $"单个商品的货源关系条数已达上限 {MaxRelationshipsPerProduct} 条，请先清理不再使用的货源关系");
    }

    /// <summary>单供应商货源关系条数上限校验（含停用行，保证供应商侧列表有界）</summary>
    public static void EnsureSupplierBound(int existingCount)
    {
        if (existingCount >= MaxRelationshipsPerSupplier)
            throw BusinessException.InvalidParameter(
                $"单个供应商的货源关系条数已达上限 {MaxRelationshipsPerSupplier} 条，请先清理不再使用的货源关系");
    }

    /// <summary>引用不可用时的显式展示文案：名称 + 标注（名称为空时只显示标注，不留空白）</summary>
    public static string MarkUnavailable(string? name) =>
        string.IsNullOrWhiteSpace(name) ? UnavailableMark : $"{name.Trim()}{UnavailableMark}";

    /// <summary>
    /// 可用性文案：显式说明该关系当前能否作为启用货源参考，避免停用 / 已删除的供应商或规格
    /// 被静默当成可用货源（历史引用照常显示，但界面上必须能看出不可用）。
    /// </summary>
    public static string AvailabilityText(bool selectable, bool supplierAvailable, bool variantAvailable)
    {
        if (!selectable) return "已停用（历史保留，不再作为启用货源）";
        if (!supplierAvailable) return $"供应商已停用 / 已删除{UnavailableMark}，仅历史可读";
        if (!variantAvailable) return $"规格已停用 / 已删除{UnavailableMark}，仅历史可读";
        return "可选用";
    }
}
