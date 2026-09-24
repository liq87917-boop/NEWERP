using ERP.Application.Common;
using ERP.Domain.Entities;
using System.Text;

namespace ERP.Application.Services;

/// <summary>
/// 商品规格变体的纯规则（ERP-037，无数据库依赖，便于逐条单测）：
/// 规范化（编码 / 颜色 / 尺码）、字段校验（编码必填、颜色与尺码至少一个、长度与字符集）、
/// 以及「同商品内编码唯一」「启用状态下颜色 + 尺码组合唯一」的判定。
/// <para>边界：本规则只做主数据自身的判定，不产生 / 修改任何单据行，也不涉及库存数量与成本，
/// 因此规格的新增 / 修改 / 停用 / 删除不会改写历史询价、报价、订单、库存与库存流水，也不会拆分已有库存。</para>
/// </summary>
public static class ProductVariantRules
{
    /// <summary>规格编码最大长度（与实体 / 建表脚本一致）</summary>
    public const int MaxVariantCodeLength = 50;

    /// <summary>颜色最大长度</summary>
    public const int MaxColorLength = 50;

    /// <summary>尺码最大长度</summary>
    public const int MaxSizeLength = 50;

    /// <summary>归一化组合键最大长度</summary>
    public const int MaxColorSizeKeyLength = 120;

    /// <summary>备注最大长度</summary>
    public const int MaxRemarkLength = 500;

    /// <summary>
    /// 单个商品允许维护的规格条数上限（含停用行）：规格维护视图与列表读取都按此上限收敛，
    /// 保证「有界」的维护视图与列表响应，避免单商品无限膨胀。
    /// </summary>
    public const int MaxVariantsPerProduct = 200;

    /// <summary>启用状态</summary>
    public const int ActiveStatus = 1;

    /// <summary>停用状态</summary>
    public const int DisabledStatus = 0;

    /// <summary>停用规格的显式展示标注（历史仍可读，但不可再被新选中）</summary>
    public const string UnavailableMark = "（已停用，不可再被新选中）";

    /// <summary>归一化后的规格（服务端权威口径，客户端提交值一律不被采信）</summary>
    public readonly record struct NormalizedVariant(
        string VariantCode,
        string Color,
        string Size,
        string ColorSizeKey,
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

    /// <summary>规格编码归一化：压缩空白后转大写（「red-1」「 Red-1 」「RED-1」视为同一编码）</summary>
    public static string NormalizeCode(string? value) => CollapseWhitespace(value).ToUpperInvariant();

    /// <summary>唯一性比较用的键：压缩空白后转大写</summary>
    public static string NormalizeKey(string? value) => CollapseWhitespace(value).ToUpperInvariant();

    /// <summary>展示值：只压缩空白与去首尾空白，保留原大小写（颜色 / 尺码显示不受比较口径影响）</summary>
    public static string DisplayValue(string? value) => CollapseWhitespace(value);

    /// <summary>归一化「颜色 + 尺码」组合键（形如 <c>RED|XL</c>；两者都为空时为 <c>|</c>，由调用方拒绝）</summary>
    public static string BuildColorSizeKey(string? color, string? size) =>
        $"{NormalizeKey(color)}|{NormalizeKey(size)}";

    /// <summary>颜色与尺码是否至少填写了一个（规格必须有可区分的颜色或尺码）</summary>
    public static bool HasColorOrSize(string? color, string? size) =>
        NormalizeKey(color).Length > 0 || NormalizeKey(size).Length > 0;

    /// <summary>规格当前是否可被选用（未删除且启用）；停用规格只能作为历史读取</summary>
    public static bool IsSelectable(BaseProductVariant? variant) =>
        variant is not null && !variant.IsDeleted && variant.Status == ActiveStatus;

    /// <summary>规格展示名：颜色 / 尺码按「 / 」拼接，均空时退化为编码</summary>
    public static string DisplayName(string? variantCode, string? color, string? size)
    {
        var parts = new List<string>(2);
        var displayColor = DisplayValue(color);
        var displaySize = DisplayValue(size);
        if (displayColor.Length > 0) parts.Add(displayColor);
        if (displaySize.Length > 0) parts.Add(displaySize);
        return parts.Count > 0 ? string.Join(" / ", parts) : DisplayValue(variantCode);
    }

    /// <summary>停用规格的显式展示文案（名称为空时只显示标注，不留空白）</summary>
    public static string MarkUnavailable(string? variantName) =>
        string.IsNullOrWhiteSpace(variantName) ? UnavailableMark : $"{variantName.Trim()}{UnavailableMark}";

    /// <summary>校验并规范化客户端提交的规格字段；任一项不合法即抛出可读的参数校验错误</summary>
    public static NormalizedVariant Normalize(string? rawCode, string? rawColor, string? rawSize, string? rawRemark = null)
    {
        var code = NormalizeCode(rawCode);
        if (code.Length == 0)
            throw BusinessException.InvalidParameter("规格编码不能为空（编码用于在同商品内唯一标识该颜色 / 尺码组合）");
        if (code.Length > MaxVariantCodeLength)
            throw BusinessException.InvalidParameter($"规格编码长度不能超过 {MaxVariantCodeLength} 个字符");
        EnsureCodeCharactersAllowed(code);

        var color = DisplayValue(rawColor);
        var size = DisplayValue(rawSize);
        if (color.Length > MaxColorLength)
            throw BusinessException.InvalidParameter($"颜色长度不能超过 {MaxColorLength} 个字符");
        if (size.Length > MaxSizeLength)
            throw BusinessException.InvalidParameter($"尺码长度不能超过 {MaxSizeLength} 个字符");
        if (!HasColorOrSize(color, size))
            throw BusinessException.InvalidParameter("颜色与尺码至少填写一个（两者都为空无法区分规格）");

        var remark = DisplayValue(rawRemark);
        if (remark.Length > MaxRemarkLength)
            throw BusinessException.InvalidParameter($"备注长度不能超过 {MaxRemarkLength} 个字符");

        var key = BuildColorSizeKey(color, size);
        if (key.Length > MaxColorSizeKeyLength)
            throw BusinessException.InvalidParameter($"颜色与尺码组合长度不能超过 {MaxColorSizeKeyLength} 个字符");

        return new NormalizedVariant(code, color, size, key, remark);
    }

    /// <summary>规格编码字符集校验：只允许字母、数字、空格与 - _ . /（避免引号 / 分号等影响展示与导出）</summary>
    public static void EnsureCodeCharactersAllowed(string normalizedCode)
    {
        foreach (var ch in normalizedCode)
        {
            if (char.IsLetterOrDigit(ch) || ch == '-' || ch == '_' || ch == '.' || ch == '/' || ch == ' ')
                continue;
            throw BusinessException.InvalidParameter(
                $"规格编码含不支持的字符「{ch}」：只允许字母、数字、空格与 - _ . /");
        }
    }

    /// <summary>
    /// 同一商品内规格编码唯一（忽略大小写与首尾 / 连续空白；软删除行不占用编码）。
    /// 说明：本判定给用户可读的错误，数据库层还用过滤唯一索引 <c>UX_BaseProductVariants_ProductCode</c> 兜底并发写入。
    /// </summary>
    public static void EnsureCodeUnique(
        IEnumerable<BaseProductVariant> siblings, string normalizedCode, long? excludeVariantId = null)
    {
        var duplicated = siblings.Any(v =>
            !v.IsDeleted && v.Id != (excludeVariantId ?? 0)
            && NormalizeCode(v.VariantCode) == normalizedCode);

        if (duplicated)
            throw BusinessException.Duplicate(
                $"规格编码「{normalizedCode}」在该商品下已存在（编码忽略大小写与首尾空白），请更换编码");
    }

    /// <summary>
    /// 同一商品内「启用中」规格的颜色 + 尺码组合唯一（停用与软删除行不占用组合）。
    /// 停用规格作为历史保留，不与新规格互相覆盖；重新启用时会再次做同样判定，因此不会出现两条同时启用
    /// 且颜色 / 尺码相同的规格。数据库层由过滤唯一索引 <c>UX_BaseProductVariants_ProductColorSize</c> 兜底。
    /// </summary>
    public static void EnsureColorSizeUnique(
        IEnumerable<BaseProductVariant> siblings, string colorSizeKey, long? excludeVariantId = null)
    {
        var duplicated = siblings.Any(v =>
            IsSelectable(v) && v.Id != (excludeVariantId ?? 0)
            && EffectiveColorSizeKey(v) == colorSizeKey);

        if (duplicated)
            throw BusinessException.Duplicate(
                "该商品下已存在启用中的相同「颜色 + 尺码」规格，不允许重复维护；如确需调整请先停用或删除原规格");
    }

    /// <summary>
    /// 行上生效的「颜色 + 尺码」组合键：优先用落库的组合键（与数据库过滤唯一索引 <c>UX_BaseProductVariants_ProductColorSize</c>
    /// 判定口径完全一致），仅在历史行未写入组合键时按颜色 / 尺码现算。
    /// </summary>
    public static string EffectiveColorSizeKey(BaseProductVariant variant) =>
        string.IsNullOrEmpty(variant.ColorSizeKey)
            ? BuildColorSizeKey(variant.Color, variant.Size)
            : variant.ColorSizeKey;

    /// <summary>规格条数上限校验（含停用行，保证维护视图有界）</summary>
    public static void EnsureWithinBound(int existingCount)
    {
        if (existingCount >= MaxVariantsPerProduct)
            throw BusinessException.InvalidParameter(
                $"单个商品的规格条数已达上限 {MaxVariantsPerProduct} 条，请先清理不再使用的规格");
    }
}
