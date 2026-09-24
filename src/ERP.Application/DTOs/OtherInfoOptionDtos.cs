namespace ERP.Application.DTOs;

/// <summary>
/// 数据字典「引用下拉 / 历史回显」项（ERP-036）：
/// 字典项本身 + 当前是否仍可被引用；不可引用时 <see cref="InfoName"/> 已带显式标注
/// （如「XX 货代（已停用/不可用）」），保证历史引用在界面上被明确指出而不是静默消失。
/// </summary>
/// <param name="Id">字典项 Id</param>
/// <param name="InfoType">资料类型（如 Forwarder）</param>
/// <param name="InfoCode">资料编码</param>
/// <param name="InfoName">展示名称（不可引用时带标注）</param>
/// <param name="Selectable">当前是否可被引用下拉框选用（未删除、已启用、类型匹配时为 true）</param>
public sealed record OtherInfoOptionDto(long Id, string InfoType, string InfoCode, string InfoName, bool Selectable);
