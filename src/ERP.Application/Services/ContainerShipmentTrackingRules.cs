using ERP.Application.Common;
using ERP.Domain.Entities;

namespace ERP.Application.Services;

/// <summary>
/// 装柜外贸与物流跟踪的纯规则（ERP-040，无数据库依赖，便于逐条单测）：
/// 出运方式取值域（拼箱 LCL / 整箱 FCL / 未指定）、跟踪字段长度上限、查验要求三态口径，
/// 以及「其他资料」中 <c>InfoType = CustomsBroker</c> 的报关行字典项能否被引用。
/// <para>边界：规则只判断「录入值是否合法」，不做任何数据推断 —— 不按自由文本猜日期 / 港口 / 报关行，
/// 不按柜号匹配订柜记录，也不产生或改写费用、单证、库存与外部系统数据。</para>
/// </summary>
public static class ContainerShipmentTrackingRules
{
    // ==================== 出运方式 ====================

    /// <summary>整箱</summary>
    public const string Fcl = "FCL";

    /// <summary>拼箱</summary>
    public const string Lcl = "LCL";

    /// <summary>可选用的出运方式（空串 = 未指定，不在此列）</summary>
    public static readonly IReadOnlyList<string> ShipmentModes = new[] { Lcl, Fcl };

    // ==================== 报关行字典项 ====================

    /// <summary>报关行对应的数据字典资料类型（与 <c>other-info</c> 页面的 InfoType 选项一致）</summary>
    public const string CustomsBrokerInfoType = "CustomsBroker";

    /// <summary>引用不可用（字典项已删除 / 已停用 / 类型不符）时的显式展示标注</summary>
    public const string UnavailableMark = "（已停用/不可用）";

    // ==================== 字段长度上限（与实体 [MaxLength] 一致） ====================

    /// <summary>出运方式长度上限</summary>
    public const int ShipmentModeMaxLength = 10;

    /// <summary>提单号长度上限</summary>
    public const int BillOfLadingNoMaxLength = 50;

    /// <summary>订舱号 / 托运单号长度上限</summary>
    public const int ShippingOrderNoMaxLength = 50;

    /// <summary>港口名称长度上限（起运港 / 目的港 / 中转港）</summary>
    public const int PortMaxLength = 100;

    /// <summary>拖车 / 集卡公司名称长度上限</summary>
    public const int TruckerNameMaxLength = 200;

    /// <summary>报关行名称快照长度上限</summary>
    public const int CustomsBrokerNameMaxLength = 100;

    // ==================== 展示文案 ====================

    /// <summary>未知 / 未填写文案（前端与报表共用口径：未知不回落为 0 或空）</summary>
    public const string UnknownText = "未知";

    /// <summary>出运方式文案（拼箱 / 整箱 / 未知）</summary>
    public static string ShipmentModeText(string? mode) => NormalizeText(mode).ToUpperInvariant() switch
    {
        Lcl => "拼箱 LCL",
        Fcl => "整箱 FCL",
        _ => UnknownText
    };

    /// <summary>查验要求文案（需要查验 / 不需要查验 / 未知）—— 三态互不混淆</summary>
    public static string InspectionRequiredText(bool? inspectionRequired) => inspectionRequired switch
    {
        true => "需要查验",
        false => "不需要查验",
        _ => UnknownText
    };

    // ==================== 规范化与校验 ====================

    /// <summary>
    /// 出运方式规范化：忽略首尾空白、统一大写；空串 / null = 未指定（未知）；
    /// 其他取值一律拒绝（不接受「海运」「拼柜」这类自由文本当作权威出运方式）。
    /// </summary>
    public static string NormalizeShipmentMode(string? raw)
    {
        var value = NormalizeText(raw).ToUpperInvariant();
        if (value.Length == 0) return string.Empty;
        if (!ShipmentModes.Contains(value))
            throw BusinessException.InvalidParameter(
                $"出运方式只能是 {Lcl}（拼箱）或 {Fcl}（整箱），收到「{raw}」");
        return value;
    }

    /// <summary>文本规范化：忽略首尾空白（空 = 未填写；不写「无」「待定」这类占位值）</summary>
    public static string NormalizeText(string? raw) => (raw ?? string.Empty).Trim();

    /// <summary>文本长度校验：超长直接拒绝，不静默截断（避免数据库截断把跟踪信息写坏）</summary>
    public static void EnsureLength(string value, int maxLength, string fieldLabel)
    {
        if (value.Length > maxLength)
            throw BusinessException.InvalidParameter(
                $"{fieldLabel}最长 {maxLength} 个字符，当前 {value.Length} 个字符");
    }

    /// <summary>
    /// 查验要求三态校验：<c>null</c> = 未知、<c>false</c> = 不需要查验、<c>true</c> = 需要查验。
    /// <para>明确「不需要查验」（false）时不允许填写查验日期 —— 否则记录自相矛盾；
    /// 反过来，「需要查验」或「未知」都不要求必须有查验日期（尚未查验时日期本就可以留空），
    /// 任何一态都不会由自由文本解析或推断得出。</para>
    /// </summary>
    public static void EnsureInspectionConsistency(bool? inspectionRequired, DateTime? inspectionDate)
    {
        if (inspectionRequired == false && inspectionDate.HasValue)
            throw BusinessException.InvalidParameter(
                "查验要求为「不需要查验」时不能填写查验日期，请改为「需要查验」或清空查验日期");
    }

    // ==================== 报关行字典项引用 ====================

    /// <summary>资料类型是否匹配（忽略大小写与首尾空白，避免因录入手误判为「类型不符」）</summary>
    public static bool IsType(string? infoType, string expectedType) =>
        string.Equals(NormalizeText(infoType), expectedType, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 字典项当前是否可被引用：必须存在、未删除、已启用，且类型与请求的类型一致。
    /// 任一不满足即不可选用（历史引用仍可显示，但不能新选中）。
    /// </summary>
    public static bool IsSelectable(BaseOtherInfo? entry, string infoType) =>
        entry is not null && !entry.IsDeleted && entry.Status == 1 && IsType(entry.InfoType, infoType);

    /// <summary>字典项当前是否可作为报关行选用</summary>
    public static bool IsSelectableCustomsBroker(BaseOtherInfo? entry) =>
        IsSelectable(entry, CustomsBrokerInfoType);

    /// <summary>
    /// 校验被选中的字典项确实可作为报关行，否则抛出可读的业务错误：
    /// 不存在 → 404「不存在」；已删除 / 类型不符 / 已停用 → 400 参数校验失败。
    /// 说明：只在「新增或更换报关行引用」时调用；订柜记录上原有的历史引用不会因此被清空或改写。
    /// </summary>
    public static void EnsureSelectableCustomsBroker(BaseOtherInfo? entry, long requestedId)
    {
        if (entry is null)
            throw BusinessException.NotFound($"指定的报关行（Id={requestedId}）不存在，请重新选择");

        if (entry.IsDeleted)
            throw BusinessException.InvalidParameter("指定的报关行已被删除，不能新指定，请重新选择");

        if (!IsType(entry.InfoType, CustomsBrokerInfoType))
            throw BusinessException.InvalidParameter(
                $"所选资料「{entry.InfoName}」不是报关行（InfoType={CustomsBrokerInfoType}）类字典项，不能作为报关行");

        if (entry.Status != 1)
            throw BusinessException.InvalidParameter($"指定的报关行「{entry.InfoName}」已停用，不能新指定，请重新选择或清空");
    }

    /// <summary>引用不可用时的显式展示文案：名称 + 标注（名称为空时只显示标注，不留空白）</summary>
    public static string MarkUnavailable(string? infoName) =>
        string.IsNullOrWhiteSpace(infoName) ? UnavailableMark : $"{NormalizeText(infoName)}{UnavailableMark}";

    /// <summary>
    /// 可选用时按字典项写回名称快照（客户端提交的自由文本不被采信）；
    /// 字典项名称为空时退化为空串（不写入「未命名」这类伪造值）。
    /// </summary>
    public static string SnapshotName(BaseOtherInfo entry) => NormalizeText(entry.InfoName);
}
