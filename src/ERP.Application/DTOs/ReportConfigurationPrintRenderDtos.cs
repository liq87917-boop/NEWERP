using System.Collections.Generic;

namespace ERP.Application.DTOs;

/// <summary>打印渲染布局类型（有限）：standard = 受控表格布局；grid = 已保存 LayoutJson 网格布局。</summary>
public static class ReportPrintRenderLayoutText
{
    public const string Standard = "standard";
    public const string Grid = "grid";
}

/// <summary>打印渲染有界约束（平台常量，客户端不可递增）。</summary>
public static class ReportPrintRenderLimits
{
    /// <summary>单张网格最大列数。</summary>
    public const int MaxGridCols = 40;

    /// <summary>单张网格最大行数。</summary>
    public const int MaxGridRows = 400;

    /// <summary>单张网格最大单元格数。</summary>
    public const int MaxGridCells = 4000;

    /// <summary>单张网格最大合并区数。</summary>
    public const int MaxGridMerges = 200;

    /// <summary>一次渲染最多生成的网格块数（与当前预览行上限对齐）。</summary>
    public const int MaxGridBlocks = 200;

    /// <summary>一次渲染网格单元格总数上限（块 × 单元格）。</summary>
    public const int MaxGridTotalCells = 100_000;

    /// <summary>单个网格单元格文本最大长度（字符）。</summary>
    public const int MaxCellTextLength = 500;

    /// <summary>网格列宽有界（px）。</summary>
    public const double MinColWidthPx = 10;
    public const double MaxColWidthPx = 2000;

    /// <summary>单张网格总宽 / 总高有界（px）：拒绝超大网格。</summary>
    public const double MaxGridTotalWidthPx = 2000;
    public const double MaxGridTotalHeightPx = 4000;

    /// <summary>网格行高有界（px）。</summary>
    public const double MinRowHeightPx = 8;
    public const double MaxRowHeightPx = 400;

    /// <summary>网格单元格字号有界（px）。</summary>
    public const double MinGridFontSize = 6;
    public const double MaxGridFontSize = 48;
}

/// <summary>打印渲染请求：现有报表定义 Id + 可选发布修订 + 已保存模板 Id。</summary>
public sealed class ReportConfigurationPrintRenderRequest
{
    /// <summary>要打印的私有报表配置 Id（自有草稿 / 自有发布修订 / 被共享固定快照）。</summary>
    public long ConfigurationId { get; set; }

    /// <summary>固定发布修订版本号（可选；空 = 当前草稿）。</summary>
    public int? RevisionVersion { get; set; }

    /// <summary>已保存打印模板 Id。</summary>
    public long TemplateId { get; set; }

    /// <summary>页码覆盖（可选）。</summary>
    public int? Page { get; set; }

    /// <summary>每页条数覆盖（可选）。</summary>
    public int? PageSize { get; set; }
}

/// <summary>打印渲染列（模板字段顺序 → 受控列键）。</summary>
public sealed class ReportPrintRenderColumnDto
{
    public string LegacyKey { get; set; } = string.Empty;
    public string ColumnKey { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string? CurrencyUnit { get; set; }
}

/// <summary>网格单元格（已替换占位符的纯文本 + 有界样式；渲染层负责转义）。</summary>
public sealed class ReportPrintGridCellDto
{
    public int Row { get; set; }
    public int Col { get; set; }
    public int RowSpan { get; set; } = 1;
    public int ColSpan { get; set; } = 1;
    public string Text { get; set; } = string.Empty;
    public bool Bold { get; set; }
    public bool Italic { get; set; }
    public string Align { get; set; } = "left";
    public double FontSize { get; set; }
    public string? Color { get; set; }
    public string? Background { get; set; }
    public int Border { get; set; }
}

/// <summary>一个网格块（对应一条事实行：占位符已替换为纯文本）。</summary>
public sealed class ReportPrintGridBlockDto
{
    public string FontFamily { get; set; } = string.Empty;
    public IReadOnlyList<double> ColWidths { get; set; } = new List<double>();
    public IReadOnlyList<double> RowHeights { get; set; } = new List<double>();
    public IReadOnlyList<ReportPrintGridCellDto> Cells { get; set; } = new List<ReportPrintGridCellDto>();
    public string FooterText { get; set; } = string.Empty;
}

/// <summary>打印渲染结果（预览与 PDF 下载共用的有界渲染模型）。</summary>
public sealed class ReportConfigurationPrintRenderDto
{
    public long TemplateId { get; set; }
    public string FamilyKey { get; set; } = string.Empty;
    public string DatasetKey { get; set; } = string.Empty;
    public string TemplateName { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string CompanyName { get; set; } = string.Empty;
    public string CompanyAddress { get; set; } = string.Empty;
    public string CompanyPhone { get; set; } = string.Empty;
    public bool ShowCompanyHeader { get; set; }
    public string FooterText { get; set; } = string.Empty;
    public string PaperSize { get; set; } = "A4";
    public int FontSize { get; set; }
    public int TitleFontSize { get; set; }
    public int CompanyFontSize { get; set; }
    public string FontFamily { get; set; } = string.Empty;
    public int CellPadding { get; set; }
    public int RowHeight { get; set; }

    /// <summary>布局类型（standard / grid）。</summary>
    public string Layout { get; set; } = ReportPrintRenderLayoutText.Standard;

    public List<ReportPrintRenderColumnDto> Columns { get; set; } = new();
    public List<Dictionary<string, object?>> Rows { get; set; } = new();
    public List<ReportPrintGridBlockDto> GridBlocks { get; set; } = new();

    public ReportConfigurationEvidenceContextDto? Evidence { get; set; }
    public string Coverage { get; set; } = ReportConfigurationConstants.CoverageCurrentPage;
    public int MatchedCount { get; set; }
    public int SourceEvidenceCount { get; set; }
    public string CorrelationId { get; set; } = string.Empty;
    public string DefinitionName { get; set; } = string.Empty;
    public int DefinitionVersion { get; set; }
    public bool IsPinnedRevision { get; set; }
}
