using ERP.Application.DTOs;
using ERP.Application.Services;

namespace ERP.Application.Interfaces;

/// <summary>
/// 受控打印渲染服务接缝（ERP-313 Stage 2）：把现有通用报表定义（Id + 可选发布修订）与已保存打印模板
/// （Id）绑定为有界渲染结果（standard 表格 / LayoutJson 网格）。渲染前复用既有执行租约与受控绑定目录
/// 重新校验所有权 / 共享 / 当前菜单 / 行 / 列范围（fail closed）；族 / 数据集不匹配、不支持别名、被拒绝 /
/// 撤销字段或模板一律整单失败，绝不产出部分输出。全程只读，不写库、不执行任意 SQL。
/// </summary>
public interface IReportConfigurationPrintRenderService
{
    /// <summary>自动获取执行租约并构建打印渲染结果（预览）。</summary>
    Task<ReportConfigurationPrintRenderDto> PreviewAsync(
        long userId,
        ReportConfigurationPrintRenderRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>在已获取的执行租约内构建打印渲染结果（PDF 导出复用同一租约，绝不二次获取租约）。</summary>
    Task<ReportConfigurationPrintRenderDto> BuildAsync(
        long userId,
        ReportConfigurationPrintRenderRequest request,
        IReportConfigurationExecutionLease lease);
}
