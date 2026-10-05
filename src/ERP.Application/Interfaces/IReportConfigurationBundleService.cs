using ERP.Application.DTOs;
using ERP.Application.Services;

namespace ERP.Application.Interfaces;

/// <summary>
/// 通用报表配置捆绑服务（ERP-307 Stage 2）：把有界有序的既有私有 / 共享定义与固定发布版本引用
/// 编排成一次多节预览或一次多节导出。捆绑本身无状态、不新增实体 / 数据库结构，绝不执行任意 SQL /
/// 联接 / 跨数据集运算，也不新增报表专用查询逻辑。
/// <para>安全口径：每个节都复用既有 <see cref="IReportConfigurationExecutionService"/> 在单一执行租约内
/// 重新校验归属 / 共享授权 / 固定修订 / 数据集菜单授权与数据范围（fail closed）；全部节都加载并校验通过
/// 后才产出结果，任何一节被拒绝 / 撤销 / 失效即整个捆绑失败，绝不产出部分数据或部分下载。</para>
/// </summary>
public interface IReportConfigurationBundleService
{
    /// <summary>预览捆绑：逐节复用既有执行服务的有界、已授权预览（自动获取单一执行租约）。</summary>
    Task<ReportConfigurationBundlePreviewDto> PreviewAsync(
        long ownerUserId,
        ReportConfigurationBundleRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>在已获取的执行租约内预览捆绑（导出复用同一租约，共享截止时间，绝不二次获取租约）。</summary>
    Task<ReportConfigurationBundlePreviewDto> PreviewAsync(
        long ownerUserId,
        ReportConfigurationBundleRequest request,
        IReportConfigurationExecutionLease lease);

    /// <summary>在已获取的执行租约内构建捆绑导出结果（逐节复用既有服务端内部导出结果，绝不二次获取租约）。</summary>
    Task<ReportConfigurationBundleExportResultDto> BuildExportResultAsync(
        long ownerUserId,
        ReportConfigurationBundleRequest request,
        IReportConfigurationExecutionLease lease);

    /// <summary>组合预览：按服务端声明场景组合表头 / 明细两节（自动获取单一执行租约）。</summary>
    Task<ReportConfigurationBundleCompositionPreviewDto> ComposePreviewAsync(
        long ownerUserId,
        ReportConfigurationBundleCompositionRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>在已获取的执行租约内组合预览（导出复用同一租约，共享截止时间，绝不二次获取租约）。</summary>
    Task<ReportConfigurationBundleCompositionPreviewDto> ComposePreviewAsync(
        long ownerUserId,
        ReportConfigurationBundleCompositionRequest request,
        IReportConfigurationExecutionLease lease);

    /// <summary>在已获取的执行租约内构建组合导出结果（复用同一租约，绝不二次获取租约）。</summary>
    Task<ReportConfigurationBundleCompositionExportDto> BuildCompositionExportResultAsync(
        long ownerUserId,
        ReportConfigurationBundleCompositionRequest request,
        IReportConfigurationExecutionLease lease);
}
