using ERP.Application.DTOs;

namespace ERP.Application.Interfaces;

/// <summary>
/// 迁移 parity 输出语义比对接缝（ERP-331）：把通用平台 Excel/PDF 导出与旧导出的渲染语义
/// （列头、有序行单元格值、币种 / 单位标签）做确定性只读比对，得出 <c>OutputSemanticsMatched</c>。
/// <para>绝不触碰数据库、绝不扩权、绝不新增实体或 API；字体缺失或旧导出不可用时 fail closed（不匹配），
/// 绝不猜测。</para>
/// </summary>
public interface IReportMigrationOutputComparator
{
    /// <summary>
    /// 比较通用预览渲染输出与旧导出语义快照。
    /// <para><paramref name="genericPreview"/> 为通用数据集的同一有界预览（通用导出器据此渲染 Excel/PDF）；
    /// <paramref name="legacySnapshot"/> 为旧来源接缝归一化的旧导出语义快照（列头 / 单元格 / 币种单位）。</para>
    /// <para><paramref name="excelCompatible"/> / <paramref name="pdfCompatible"/> 为该旧报表条目的声明兼容性：
    /// 声明为不兼容的格式绝不强制比对；声明为兼容的格式必须渲染成功且列头 / 单元格 / 币种单位标签一致，
    /// 任一分歧即该维度失败；两者均不兼容表示旧路由本就不产出 Excel/PDF，输出语义为真空匹配。</para>
    /// <para><paramref name="fontPath"/> 供测试显式注入 PDF 中文字体路径（null 时按 Windows 黑体 SimHei 查找）；字体缺失显式失败。</para>
    /// </summary>
    ReportMigrationOutputComparisonResultDto Compare(
        ReportConfigurationPreviewDto genericPreview,
        ReportMigrationParitySnapshotDto? legacySnapshot,
        bool excelCompatible,
        bool pdfCompatible,
        string? fontPath = null);
}

/// <summary>
/// 输出语义比对结果（ERP-331）：<see cref="OutputSemanticsMatched"/> 为 Excel/PDF 输出语义维度结论，
/// <see cref="Evidence"/> 携带确定性证据明细（空列表 = 匹配；任一元素即人类可读的不一致 / 失败原因）。
/// </summary>
public sealed record ReportMigrationOutputComparisonResultDto(
    bool OutputSemanticsMatched,
    IReadOnlyList<string> Evidence);
