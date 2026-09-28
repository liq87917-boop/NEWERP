using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Domain.Entities;
using System.Linq.Expressions;

namespace ERP.Application.Services;

/// <summary>
/// 只读出口字段完整度工作台（ERP-107）的纯规则（无数据库依赖，便于逐条单测）：
/// 字段「已填写 / 空白 / 为 0 / 无效值」的判定、完整度分组与过滤表达式、以及只读 / 边界 / 免责文案。
/// <para>边界：本规则只做读取口径的判定——不读取图片、不改写商品 / 单证 / 报关单，
/// 不做报关合规、退税资格或税率结论，也不调用任何外部服务。</para>
/// </summary>
public static class ProductExportFieldCompletenessRules
{
    // ==================== 0. 常量 ====================

    /// <summary>启用状态（与商品资料口径一致）</summary>
    public const int ActiveStatus = 1;

    /// <summary>字段状态：已填写</summary>
    public const string StatePresent = "present";

    /// <summary>字段状态：空白（文本字段为空 / 纯空白）</summary>
    public const string StateBlank = "blank";

    /// <summary>字段状态：为 0（数值字段等于 0，未填写）</summary>
    public const string StateZero = "zero";

    /// <summary>字段状态：无效值（如负数尺寸、负数或超出 0–100 的退税率）</summary>
    public const string StateUnknown = "unknown";

    /// <summary>完整度分组：全部</summary>
    public const string GroupAll = "all";

    /// <summary>完整度分组：完整（所有字段均已填写）</summary>
    public const string GroupComplete = "complete";

    /// <summary>完整度分组：有缺口（至少一个字段未填写 / 为 0 / 无效）</summary>
    public const string GroupIncomplete = "incomplete";

    /// <summary>完整度分组：英文报关品名缺失</summary>
    public const string GroupDeclaration = "declaration";

    /// <summary>完整度分组：装箱信息缺失</summary>
    public const string GroupPacking = "packing";

    /// <summary>完整度分组：外箱尺寸 / 重量缺失</summary>
    public const string GroupDimensions = "dimensions";

    /// <summary>完整度分组：退税率缺失</summary>
    public const string GroupRefundRate = "refund-rate";

    // ==================== 1. 文案 ====================

    /// <summary>只读声明（界面与接口统一声明）</summary>
    public const string ReadOnlyText =
        "只读出口字段完整度工作台：仅读取启用商品资料的英文报关品名、装箱单位与每箱数量、外箱尺寸 / 毛重与出口退税率字段的填写与缺口情况，不新增 / 修改 / 删除任何记录";

    /// <summary>边界口径文案</summary>
    public const string BoundaryText =
        "口径：只报告字段是否已填写（区分空白 / 为 0 / 无效值），不做报关合规、退税资格或税率结论；不读取图片、不改写商品 / 单证 / 报关单、不调用外部服务";

    /// <summary>免责文案（哪些结论不能从字段完整度推出）</summary>
    public const string DisclaimerText =
        "本工作台不是报关合规或退税资格判定：字段齐全不等于可以报关或退税，字段缺失也不代表不可报关或退税；请以海关 / 税务官方口径为准，并通过既有商品编辑流程补齐字段";

    // ==================== 2. 分组校验 ====================

    /// <summary>规范化完整度分组（空值 = 全部；未知取值显式拒绝）</summary>
    public static string NormalizeGroup(string? group)
    {
        if (string.IsNullOrWhiteSpace(group)) return GroupAll;
        var normalized = group.Trim().ToLowerInvariant();
        return normalized switch
        {
            GroupAll or GroupComplete or GroupIncomplete
                or GroupDeclaration or GroupPacking or GroupDimensions or GroupRefundRate => normalized,
            _ => throw BusinessException.InvalidParameter(
                "完整度分组无效（可选：all / complete / incomplete / declaration / packing / dimensions / refund-rate）"),
        };
    }

    // ==================== 3. 字段判定（纯函数） ====================

    /// <summary>文本字段：非空白 = 已填写；空白 = 未填写</summary>
    public static ProductExportFieldCompletenessFieldDto TextField(string key, string label, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return new(key, label, StateBlank, "空白未填写", false);
        return new(key, label, StatePresent, "已填写", true);
    }

    /// <summary>正整数字段（如每箱数量）：&gt; 0 已填写；= 0 未填写；负数无效</summary>
    public static ProductExportFieldCompletenessFieldDto PositiveIntField(string key, string label, int value)
    {
        if (value > 0) return new(key, label, StatePresent, "已填写（> 0）", true);
        if (value == 0) return new(key, label, StateZero, "为 0（未填写）", false);
        return new(key, label, StateUnknown, "无效（负数）", false);
    }

    /// <summary>正小数字段（如外箱尺寸 / 毛重）：&gt; 0 已填写；= 0 未填写；负数无效</summary>
    public static ProductExportFieldCompletenessFieldDto PositiveDecimalField(string key, string label, decimal value)
    {
        if (value > 0m) return new(key, label, StatePresent, "已填写（> 0）", true);
        if (value == 0m) return new(key, label, StateZero, "为 0（未填写）", false);
        return new(key, label, StateUnknown, "无效（负数）", false);
    }

    /// <summary>退税率字段（%）：0 &lt; 值 ≤ 100 已填写；= 0 未填写；负数或超过 100 无效</summary>
    public static ProductExportFieldCompletenessFieldDto RateField(string key, string label, decimal value)
    {
        if (value > 0m && value <= 100m) return new(key, label, StatePresent, "已填写", true);
        if (value == 0m) return new(key, label, StateZero, "为 0（未填写）", false);
        return new(key, label, StateUnknown, "无效（超出 0–100%）", false);
    }

    /// <summary>构建单个商品的完整字段列表（与导出文档使用的既有商品字段一一对应）</summary>
    public static List<ProductExportFieldCompletenessFieldDto> BuildFields(BaseProduct p)
    {
        return new List<ProductExportFieldCompletenessFieldDto>
        {
            TextField("englishDeclareName", "英文报关品名", p.EnglishDeclareName),
            TextField("packageUnit", "装箱单位", p.PackageUnit),
            PositiveIntField("unitsPerPackage", "每箱数量", p.UnitsPerPackage),
            PositiveDecimalField("outerLength", "外箱长(cm)", p.OuterLength),
            PositiveDecimalField("outerWidth", "外箱宽(cm)", p.OuterWidth),
            PositiveDecimalField("outerHeight", "外箱高(cm)", p.OuterHeight),
            PositiveDecimalField("outerWeight", "外箱毛重(kg)", p.OuterWeight),
            RateField("refundRate", "出口退税率(%)", p.RefundRate),
        };
    }

    /// <summary>构建单个商品的工作台行（完整度 = 是否所有字段均已填写）</summary>
    public static ProductExportFieldCompletenessRowDto BuildRow(BaseProduct p)
    {
        var fields = BuildFields(p);
        var gaps = fields.Count(f => !f.Present);
        return new ProductExportFieldCompletenessRowDto(
            p.Id,
            p.ProductCode,
            string.IsNullOrWhiteSpace(p.ProductName) ? $"商品#{p.Id}" : p.ProductName,
            p.Spec,
            p.Unit,
            gaps == 0 ? GroupComplete : GroupIncomplete,
            gaps,
            fields.Count,
            fields);
    }


    // ==================== 4. 完整度分组过滤表达式 ====================

    /// <summary>把完整度分组翻译为数据库可直接执行的商品过滤表达式（null = 不过滤）</summary>
    public static Expression<Func<BaseProduct, bool>>? BuildGroupPredicate(string group)
    {
        switch (group)
        {
            case GroupAll:
                return null;
            case GroupComplete:
                return p =>
                    (p.EnglishDeclareName != null && p.EnglishDeclareName.Trim() != "")
                    && (p.PackageUnit != null && p.PackageUnit.Trim() != "")
                    && p.UnitsPerPackage > 0
                    && p.OuterLength > 0m && p.OuterWidth > 0m && p.OuterHeight > 0m && p.OuterWeight > 0m
                    && p.RefundRate > 0m && p.RefundRate <= 100m;
            case GroupIncomplete:
                return p =>
                    (p.EnglishDeclareName == null || p.EnglishDeclareName.Trim() == "")
                    || (p.PackageUnit == null || p.PackageUnit.Trim() == "")
                    || p.UnitsPerPackage <= 0
                    || p.OuterLength <= 0m || p.OuterWidth <= 0m || p.OuterHeight <= 0m || p.OuterWeight <= 0m
                    || p.RefundRate <= 0m || p.RefundRate > 100m;
            case GroupDeclaration:
                return p => (p.EnglishDeclareName == null || p.EnglishDeclareName.Trim() == "");
            case GroupPacking:
                return p =>
                    (p.PackageUnit == null || p.PackageUnit.Trim() == "") || p.UnitsPerPackage <= 0;
            case GroupDimensions:
                return p =>
                    p.OuterLength <= 0m || p.OuterWidth <= 0m || p.OuterHeight <= 0m || p.OuterWeight <= 0m;
            case GroupRefundRate:
                return p => p.RefundRate <= 0m || p.RefundRate > 100m;
            default:
                throw BusinessException.InvalidParameter("完整度分组无效");
        }
    }
}

