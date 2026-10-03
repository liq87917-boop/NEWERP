using ERP.Application.DTOs;
using ERP.Application.Services;

namespace ERP.Application.Interfaces;

/// <summary>
/// 通用报表配置执行服务（ERP-261 Stage 1）：加载选定的草稿定义或固定发布修订，按当前账号的
/// 已授权数据集目录重新校验有界定义，并把执行分发到对应 <see cref="IReportConfigurationDatasetProvider"/>。
/// <para>安全口径：所有者只来自服务端认证身份；跨所有者 / 已删除配置 fail closed（按不存在处理）；
/// 已保存定义本身绝不授予权限；每次预览都重新校验数据集菜单授权与数据范围。</para>
/// </summary>
public interface IReportConfigurationExecutionService
{
    /// <summary>
    /// 预览私有报表配置：草稿（默认）或指定发布修订；解析 / 校验当前预览参数（页码 / 每页条数 / 分组键），
    /// 并返回统一类型化的列 / 行 / 分页 / 分组页面小计 / 证据上下文。
    /// </summary>
    Task<ReportConfigurationPreviewDto> PreviewAsync(
        long ownerUserId,
        ReportConfigurationPreviewRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 在已获取的执行租约内预览（导出复用同一租约，共享截止时间，绝不二次获取租约）。
    /// </summary>
    Task<ReportConfigurationPreviewDto> PreviewAsync(
        long ownerUserId,
        ReportConfigurationPreviewRequest request,
        IReportConfigurationExecutionLease lease);

    /// <summary>
    /// 在已获取的执行租约内构建服务端内部导出结果（ERP-275）：复用同一租约与截止时间，绝不二次获取租约。
    /// <para>全匹配覆盖时获取一次有界一致快照，由同一快照派生完整事实（≤1000）与全部选中指标 / 分组 / 透视汇总；
    /// 普通当前页覆盖维持 ≤200 行且不改变预览 / 默认上限。返回前重新校验已保存 / 固定 / 授权定义、接收人
    /// 字段 / 菜单 / 数据范围与最终撤销（fail closed），绝不把页面子集冒充全量合计。</para>
    /// </summary>
    Task<ReportConfigurationExportResultDto> BuildExportResultAsync(
        long ownerUserId,
        ReportConfigurationPreviewRequest request,
        IReportConfigurationExecutionLease lease);
}
