using ERP.Application.Common;
using ERP.Domain.Entities;

namespace ERP.Application.Services;

/// <summary>
/// 客户「指定货代」的纯规则（ERP-036，无数据库依赖，便于逐条单测）：
/// 判定一个「其他资料」字典项能否作为指定货代（类型必须是 Forwarder、未删除、已启用），
/// 以及不可用时的显式展示文案。
/// <para>边界：本规则只做「引用是否合法」的判断，不产生任何单据、不写订舱 / 装柜 / 报关 / 费用数据，
/// 也不涉及外部货代系统。</para>
/// </summary>
public static class CustomerForwarderRules
{
    /// <summary>货代对应的数据字典资料类型（与 <c>other-info</c> 页面的 InfoType 选项一致）</summary>
    public const string ForwarderInfoType = "Forwarder";

    /// <summary>引用不可用（字典项已删除 / 已停用 / 类型不符）时的显式展示标注</summary>
    public const string UnavailableMark = "（已停用/不可用）";

    /// <summary>资料类型是否匹配（忽略大小写与首尾空白，避免因录入手误判为「类型不符」）</summary>
    public static bool IsType(string? infoType, string expectedType) =>
        string.Equals((infoType ?? string.Empty).Trim(), expectedType, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 字典项当前是否可被引用：必须存在、未删除、已启用，且类型与请求的类型一致。
    /// 任一不满足即不可选用（历史引用仍可显示，但不能新选中）。
    /// </summary>
    public static bool IsSelectable(BaseOtherInfo? entry, string infoType) =>
        entry is not null && !entry.IsDeleted && entry.Status == 1 && IsType(entry.InfoType, infoType);

    /// <summary>字典项当前是否可作为「指定货代」选用（<see cref="ForwarderInfoType"/> 类型专用口径）</summary>
    public static bool IsSelectableForwarder(BaseOtherInfo? entry) =>
        IsSelectable(entry, ForwarderInfoType);

    /// <summary>
    /// 校验被选中的字典项确实可作为指定货代，否则抛出可读的业务错误：
    /// 不存在 → 404「不存在」；已删除 / 类型不符 / 已停用 → 400 参数校验失败。
    /// 说明：服务端只在「新增或更换货代引用」时调用本方法；客户资料上原有的历史引用不会因此被清空或改写。
    /// </summary>
    public static void EnsureSelectableForwarder(BaseOtherInfo? entry, long requestedId)
    {
        if (entry is null)
            throw BusinessException.NotFound($"指定的货代（Id={requestedId}）不存在，请重新选择");

        if (entry.IsDeleted)
            throw BusinessException.InvalidParameter("指定的货代已被删除，不能新指定，请重新选择");

        if (!IsType(entry.InfoType, ForwarderInfoType))
            throw BusinessException.InvalidParameter(
                $"所选资料「{entry.InfoName}」不是货代（InfoType={ForwarderInfoType}）类字典项，不能作为指定货代");

        if (entry.Status != 1)
            throw BusinessException.InvalidParameter($"指定的货代「{entry.InfoName}」已停用，不能新指定，请重新选择或清空");
    }

    /// <summary>引用不可用时的显式展示文案：名称 + 标注（名称为空时只显示标注，不留空白）</summary>
    public static string MarkUnavailable(string? infoName) =>
        string.IsNullOrWhiteSpace(infoName) ? UnavailableMark : $"{infoName.Trim()}{UnavailableMark}";

    /// <summary>
    /// 可选用时按字典项写回名称快照（客户端提交的自由文本不被采信）；
    /// 字典项名称为空时退化为空串（不写入「未命名」这类伪造值）。
    /// </summary>
    public static string SnapshotName(BaseOtherInfo entry) => (entry.InfoName ?? string.Empty).Trim();
}
