using ERP.Application.DTOs;

namespace ERP.Infrastructure.Export;

/// <summary>
/// 通用报表配置 PDF 导出的共享「有限宽度、行标识感知」水平列带布局（ERP-311 Stage 2）。
/// 单一导出器与捆绑导出器共用同一份列宽估计 / 身份列解析 / 列带拆分 / 带宽计算逻辑，保证：
/// 所有已选字段按保存顺序只出现一次、每个逻辑列带恒重复一个已授权的行标识列（无标识列则回退稳定行序号）、
/// 列宽有界（最小保证中文标签可读、最大避免单列独占整页、数值列更宽避免裁切符号），绝不查询隐藏标识字段、
/// 绝不丢弃或截断列。该类型只做纯布局数学，不持有 PDF 图形状态，渲染仍由各导出器负责。
/// </summary>
public static class ReportConfigurationPdfColumnLayout
{
    /// <summary>无允许标识列时回退的「当前页行序号」列键（合成列，不映射任何数据集字段）</summary>
    public const string RowOrdinalColumnKey = "__rowOrdinal__";

    /// <summary>无允许标识列时回退的「当前页行序号」列标签</summary>
    public const string RowOrdinalLabel = "本页序号";

    /// <summary>列宽有界（毫米）：最小保证中文标签可读；文本最大避免单列独占整页，超宽靠「列带」拆分而非挤压。</summary>
    public const double MinColumnWidthMm = 20;
    public const double MaxColumnWidthMm = 45;
    public const double MaxNumberColumnWidthMm = 70;

    /// <summary>单元格左右内边距（点）</summary>
    public const double CellPaddingPoints = 3;

    /// <summary>毫米到 PDF 点的换算系数</summary>
    public const double PointsPerMillimeter = 72.0 / 25.4;

    private const double HeaderFontSize = 8;
    private const double CellFontSize = 8;

    private static readonly HashSet<string> IdentityColumnKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "id", "orderNo", "orderNumber", "invoiceId", "invoiceNumber", "invoiceCode",
        "identityText", "receiptNo", "statementNo",
    };

    /// <summary>字段键是否为允许的「行标识」列（用于跨列带重复，仅限已授权选定列）。</summary>
    public static bool IsIdentityColumn(string? key)
        => !string.IsNullOrWhiteSpace(key) && IdentityColumnKeys.Contains(key);

    /// <summary>合成「当前页行序号」列（无允许标识列时回退使用）。</summary>
    public static ReportConfigurationColumnDto RowOrdinalColumn()
        => new(RowOrdinalColumnKey, RowOrdinalLabel, ReportConfigurationConstants.TypeNumber, null);

    /// <summary>解析要跨列带重复的允许标识列；没有任何已选标识列时回退为合成「本页序号」列。</summary>
    public static IReadOnlyList<ReportConfigurationColumnDto> ResolveIdentity(IReadOnlyList<ReportConfigurationColumnDto> columns)
    {
        var identity = columns.Where(c => IsIdentityColumn(c.Key)).ToList();
        if (identity.Count == 0)
            identity.Add(RowOrdinalColumn());
        return identity;
    }

    private static bool IsIdentityKey(string key)
        => IsIdentityColumn(key) || string.Equals(key, RowOrdinalColumnKey, StringComparison.Ordinal);

    /// <summary>按可用页宽把选定列拆成多个逻辑列带（仅返回列集合，不计算带宽），每个列带恒以允许标识列开头。</summary>
    public static IReadOnlyList<IReadOnlyList<ReportConfigurationColumnDto>> SplitColumnBands(
        IReadOnlyList<ReportConfigurationColumnDto> columns,
        IReadOnlyList<Dictionary<string, object?>> rows,
        double usableWidthPoints)
    {
        columns ??= Array.Empty<ReportConfigurationColumnDto>();
        rows ??= Array.Empty<Dictionary<string, object?>>();

        var identity = ResolveIdentity(columns);
        return SplitBands(columns, identity, rows, usableWidthPoints);
    }

    /// <summary>计算完整布局：每个逻辑列带包含列集合、列宽与身份列个数（列带内列顺序 = 身份列 + 数据列）。</summary>
    public static IReadOnlyList<ReportConfigurationPdfColumnBand> Layout(
        IReadOnlyList<ReportConfigurationColumnDto> columns,
        IReadOnlyList<Dictionary<string, object?>> rows,
        double usableWidthPoints)
    {
        columns ??= Array.Empty<ReportConfigurationColumnDto>();
        rows ??= Array.Empty<Dictionary<string, object?>>();

        var identity = ResolveIdentity(columns);
        var bands = SplitBands(columns, identity, rows, usableWidthPoints);

        var result = new List<ReportConfigurationPdfColumnBand>(bands.Count);
        foreach (var band in bands)
        {
            var widths = ComputeBandWidths(band, identity, rows, usableWidthPoints);
            result.Add(new ReportConfigurationPdfColumnBand(band, widths, identity.Count));
        }

        return result;
    }

    private static List<List<ReportConfigurationColumnDto>> SplitBands(
        IReadOnlyList<ReportConfigurationColumnDto> columns,
        IReadOnlyList<ReportConfigurationColumnDto> identity,
        IReadOnlyList<Dictionary<string, object?>> rows,
        double usableWidth)
    {
        var data = columns.Where(c => !IsIdentityColumn(c.Key)).ToList();

        var identityWidth = identity.Sum(c => ColumnWidthPoints(c, rows, usableWidth));
        var dataUsable = Math.Max(Mm(MinColumnWidthMm), usableWidth - identityWidth);

        var pages = new List<List<ReportConfigurationColumnDto>>();
        var current = new List<ReportConfigurationColumnDto>();
        var used = 0d;

        foreach (var column in data)
        {
            var width = ColumnWidthPoints(column, rows, dataUsable);
            if (current.Count > 0 && used + width > dataUsable)
            {
                pages.Add(new List<ReportConfigurationColumnDto>(identity).Concat(current).ToList());
                current = new List<ReportConfigurationColumnDto>();
                used = 0d;
            }

            current.Add(column);
            used += width;
        }

        if (current.Count > 0 || pages.Count == 0)
            pages.Add(new List<ReportConfigurationColumnDto>(identity).Concat(current).ToList());

        return pages;
    }

    private static double[] ComputeBandWidths(
        IReadOnlyList<ReportConfigurationColumnDto> band,
        IReadOnlyList<ReportConfigurationColumnDto> identity,
        IReadOnlyList<Dictionary<string, object?>> rows,
        double usableWidth)
    {
        var identityWidth = identity.Sum(c => ColumnWidthPoints(c, rows, usableWidth));
        var dataUsable = Math.Max(Mm(MinColumnWidthMm), usableWidth - identityWidth);

        return band.Select(c =>
            IsIdentityKey(c.Key)
                ? ColumnWidthPoints(c, rows, usableWidth)
                : ColumnWidthPoints(c, rows, dataUsable)).ToArray();
    }

    private static double ColumnWidthPoints(
        ReportConfigurationColumnDto column,
        IReadOnlyList<Dictionary<string, object?>> rows,
        double maxWidthPoints)
    {
        var max = ReportConfigurationPdfExporter.EstimateWidthPoints(
            ReportConfigurationPdfExporter.HeaderText(column), HeaderFontSize) + CellPaddingPoints * 2;
        foreach (var row in rows)
        {
            var width = ReportConfigurationPdfExporter.EstimateWidthPoints(
                ReportConfigurationPdfExporter.FormatCellValue(column, row), CellFontSize) + CellPaddingPoints * 2;
            if (width > max)
                max = width;
        }

        var isNumber = string.Equals(column.Type, ReportConfigurationConstants.TypeNumber, StringComparison.OrdinalIgnoreCase);
        var maxMm = isNumber ? MaxNumberColumnWidthMm : MaxColumnWidthMm;
        var capMm = Math.Min(maxMm, maxWidthPoints / PointsPerMillimeter);
        var mm = Math.Clamp(max / PointsPerMillimeter, MinColumnWidthMm, Math.Max(MinColumnWidthMm, capMm));
        return Mm(mm);
    }

    private static double Mm(double mm) => mm * PointsPerMillimeter;
}

/// <summary>一个逻辑列带：列顺序 = 允许标识列（重复） + 该列带独占的数据列；附带已计算的列宽与身份列个数。</summary>
public sealed class ReportConfigurationPdfColumnBand
{
    /// <summary>列带内的列（身份列在前，其余为按保存顺序独占的数据列）</summary>
    public IReadOnlyList<ReportConfigurationColumnDto> Columns { get; }

    /// <summary>与 <see cref="Columns"/> 一一对应的列宽（点）</summary>
    public double[] Widths { get; }

    /// <summary>列带开头重复的身份列个数（≥1：允许标识列或合成「本页序号」）</summary>
    public int IdentityColumnCount { get; }

    public ReportConfigurationPdfColumnBand(
        IReadOnlyList<ReportConfigurationColumnDto> columns,
        double[] widths,
        int identityColumnCount)
    {
        Columns = columns ?? throw new ArgumentNullException(nameof(columns));
        Widths = widths ?? throw new ArgumentNullException(nameof(widths));
        IdentityColumnCount = identityColumnCount;
    }
}
