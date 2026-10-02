using ERP.Application.DTOs;

namespace ERP.Application.Interfaces;

/// <summary>
/// 通用报表配置平台（ERP-268）的受控关系解析接缝：把已授权当前预览页行中持久化的来源事实键
/// （客户 Id）批量解析为受控客户维度字段，并写回预览结果。
/// <para>只读、批量、无 N+1；每次调用都重新校验目标维度既有「客户资料」菜单授权与业务员数据范围
/// （fail closed）；缺失 / 已删除 / 越权维度以 null + 有界原因呈现，绝不泄露其它客户。</para>
/// </summary>
public interface IReportConfigurationRelationResolver
{
    /// <summary>
    /// 对已通过目录校验、且定义含关系选择的预览结果做客户维度补全（批量 AsNoTracking、当前页去重、
    /// 至多页大小条数、零 N+1）。无关系选择时为空操作。
    /// </summary>
    Task EnrichAsync(
        ReportConfigurationPreviewDto preview,
        ReportConfigurationDefinition definition,
        long? userId,
        CancellationToken cancellationToken = default);
}
