using ERP.Application.DTOs;

namespace ERP.Application.Interfaces;

/// <summary>
/// 迁移 parity 输出语义比对接缝（ERP-331 / ERP-333）：把通用平台 Excel/PDF 导出与旧导出的实际产物字节
/// （列头、有序类型化 / null 单元格、公式安全字面标签、原始币种 / 单位标签与支持的布局语义）做确定性只读比对，
/// 得出 <c>OutputSemanticsMatched</c>。
/// <para>绝不触碰数据库、绝不扩权、绝不新增实体或 API；缺失旧产物 / 旧导出 / 字体、畸形或超大输出、不可提取格式
/// 一律 fail closed（不匹配），绝不猜测，绝不以调用方布尔作为通过凭据。</para>
/// </summary>
public interface IReportMigrationOutputComparator
{
    /// <summary>
    /// 比较通用预览渲染输出与旧导出实际产物。
    /// <para><paramref name="genericPreview"/> 为通用数据集的同一有界预览（通用导出器据此渲染 Excel/PDF）；
    /// <paramref name="legacySnapshot"/> 为旧来源接缝归一化的旧导出语义快照（列头 / 单元格 / 币种单位）。</para>
    /// <para><paramref name="legacyArtifacts"/> 为旧路由实际产物字节（由 <see cref="ILegacyReportArtifactSource"/> 产出）；
    /// 传入非 null 时按真实字节解码比对（ERP-333）；为 null 时回退为仅旧语义快照比对（ERP-331，供未接线旧部署保留旧行为）。</para>
    /// <para><paramref name="excelCompatible"/> / <paramref name="pdfCompatible"/> 为该旧报表条目的声明兼容性：
    /// 声明为不兼容的格式绝不强制比对；声明为兼容的格式必须存在真实旧产物且列头 / 单元格 / 币种单位标签 / 布局语义一致，
    /// 任一分歧即该维度失败；两者均不兼容表示旧路由本就不产出 Excel/PDF，输出语义为真空匹配。</para>
    /// <para><paramref name="fontPath"/> 供测试显式注入 PDF 中文字体路径（null 时按 Windows 黑体 SimHei 查找）；字体缺失显式失败。</para>
    /// </summary>
    ReportMigrationOutputComparisonResultDto Compare(
        ReportConfigurationPreviewDto genericPreview,
        ReportMigrationParitySnapshotDto? legacySnapshot,
        bool excelCompatible,
        bool pdfCompatible,
        string? fontPath = null,
        LegacyReportArtifactBytesDto? legacyArtifacts = null);
}

/// <summary>
/// 输出语义比对结果（ERP-331）：<see cref="OutputSemanticsMatched"/> 为 Excel/PDF 输出语义维度结论，
/// <see cref="Evidence"/> 携带确定性证据明细（空列表 = 匹配；任一元素即人类可读的不一致 / 失败原因）。
/// </summary>
public sealed record ReportMigrationOutputComparisonResultDto(
    bool OutputSemanticsMatched,
    IReadOnlyList<string> Evidence);
