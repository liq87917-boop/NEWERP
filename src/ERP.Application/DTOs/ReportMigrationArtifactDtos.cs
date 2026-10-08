using System.Collections.Generic;

namespace ERP.Application.DTOs;

/// <summary>
/// 迁移 parity 实际产物比对（ERP-333 Stage 2）的数据契约：把真实旧路由与通用平台的 Excel/PDF 字节
/// 解码为同一个有界、类型化产物模型，用于确定性只读比对。
/// <para>绝不携带任意 SQL / 连接串 / 凭据；只描述「有序列头 + 类型化 / null 单元格 + 公式安全字面标签 +
/// 原始币种 / 单位标签 + 支持的布局语义」，供比较器得出输出语义结论。</para>
/// </summary>

/// <summary>产物格式（有限、稳定）。</summary>
public enum ReportMigrationArtifactFormat
{
    /// <summary>Excel 工作簿（xlsx）。</summary>
    Excel = 1,

    /// <summary>PDF 文档。</summary>
    Pdf = 2,
}

/// <summary>
/// 解码后的单元格：区分原始 null / 空白（<see cref="IsNull"/>）与零 / 文本（绝不把 null 回落为 0），
/// 并保留按声明类型可归一化的类型化值（数值 decimal / 布尔 bool / 日期 DateTime / 文本 string，公式安全字面标签保留前导单引号）。
/// </summary>
public sealed record ReportMigrationArtifactCellDto(
    object? Value,
    bool IsNull,
    string RawType);

/// <summary>
/// 解码后的有界产物（从实际 Excel/PDF 字节解码）：有序列头 + 有序类型化单元格行 + 支持的结构 / 布局语义。
/// <para>Excel 携带 <see cref="Headers"/>（有序、公式安全标签）与 <see cref="Rows"/>（类型化 / null 单元格）；
/// PDF 只提取结构语义（页数 / 内嵌中文字体 / 非空内容），列头与单元格语义由比较器结合已知列定义做确定性比对。</para>
/// </summary>
public sealed record ReportMigrationArtifactDto(
    ReportMigrationArtifactFormat Format,
    IReadOnlyList<string> Headers,
    IReadOnlyList<IReadOnlyList<ReportMigrationArtifactCellDto>> Rows,
    int PageCount,
    bool EmbeddedFont,
    bool HasContent);

/// <summary>
/// 旧路由实际产物字节集（由 <c>ILegacyReportArtifactSource</c> 产出，未经解码的原始字节）。
/// <para>声明不兼容的格式其对应字节恒为 null；声明兼容但旧导出不可用 / 缺失时该格式字节为 null（fail closed）。</para>
/// </summary>
public sealed record LegacyReportArtifactBytesDto(
    byte[]? ExcelBytes,
    byte[]? PdfBytes);

/// <summary>
/// 从实际 PDF 字节解码出的单个单元格（ERP-336）：实际抽取的文本 + 原始 null 表示（空单元格绝不回落为 0 / 文本）。
/// <para>PDF 中 null 与空字符串均渲染为空单元格（无文本），统一表示为 <see cref="IsNull"/> = true。</para>
/// </summary>
public sealed record ReportMigrationPdfCellDto(
    string Text,
    bool IsNull);

/// <summary>
/// 从实际 PDF 字节解码出的有界产物（ERP-336）：有序列头 + 有序数据行 + 空单元格 / 文本 + 页 / 列带 / 行延续语义。
/// <para>只描述从实际内容流 + ToUnicode CMap 抽取出的结构化表格（绝不携带任意 SQL / 凭据 / 原始压缩字节）。</para>
/// </summary>
public sealed record ReportMigrationPdfArtifactDto(
    int PageCount,
    bool EmbeddedFont,
    bool HasContent,
    IReadOnlyList<string> Headers,
    IReadOnlyList<IReadOnlyList<ReportMigrationPdfCellDto>> Rows);
