using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace ERP.Application.Services;

/// <summary>
/// 受控打印渲染服务（ERP-313 Stage 2）实现：把现有通用报表定义与已保存打印模板绑定为有界渲染结果。
/// <para>每次渲染都在单一执行租约内复用受控绑定目录与既有执行服务重新校验；族 / 数据集不匹配、不支持别名、
/// 被拒绝 / 撤销字段或模板一律整单失败，绝不产出部分输出；网格布局仅接受字面文本与封闭占位符别名。</para>
/// </summary>
public sealed class ReportConfigurationPrintRenderService : IReportConfigurationPrintRenderService
{
    private readonly IErpDbContext _db;
    private readonly IReportConfigurationPrintTemplateCatalog _templateCatalog;
    private readonly IReportConfigurationExecutionService _execution;
    private readonly IReportConfigurationExecutionBudget _budget;

    public ReportConfigurationPrintRenderService(
        IErpDbContext db,
        IReportConfigurationPrintTemplateCatalog templateCatalog,
        IReportConfigurationExecutionService execution,
        IReportConfigurationExecutionBudget? budget = null)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _templateCatalog = templateCatalog ?? throw new ArgumentNullException(nameof(templateCatalog));
        _execution = execution ?? throw new ArgumentNullException(nameof(execution));
        _budget = budget ?? new ReportConfigurationExecutionBudget();
    }

    /// <inheritdoc />
    public async Task<ReportConfigurationPrintRenderDto> PreviewAsync(
        long userId,
        ReportConfigurationPrintRenderRequest request,
        CancellationToken cancellationToken = default)
        => await _budget.ExecuteAsync(userId, cancellationToken,
            lease => BuildAsync(userId, request, lease));

    /// <inheritdoc />
    public async Task<ReportConfigurationPrintRenderDto> BuildAsync(
        long userId,
        ReportConfigurationPrintRenderRequest request,
        IReportConfigurationExecutionLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        EnsureAuthenticated(userId);
        ArgumentNullException.ThrowIfNull(request);
        if (request.ConfigurationId <= 0)
            throw BusinessException.InvalidParameter("请选择要打印的报表配置");
        if (request.TemplateId <= 0)
            throw BusinessException.InvalidParameter("请选择要使用的打印模板");

        var template = await _db.SysPrintTemplates.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == request.TemplateId && !t.IsDeleted, lease.Token)
            ?? throw BusinessException.NotFound("打印模板不存在");

        var family = ReportPrintTemplateFamilies.Resolve(template.BillType);

        var binding = await _templateCatalog.BindAsync(
            new ReportPrintTemplateBindingRequest
            {
                FamilyKey = family.FamilyKey,
                TemplateId = template.Id,
                FieldKeys = new List<string>(),
            },
            userId,
            lease.Token);

        if (binding.BoundColumns.Count == 0)
            throw BusinessException.InvalidParameter("打印模板没有可渲染的受控字段");

        var export = await _execution.BuildExportResultAsync(
            userId,
            new ReportConfigurationPreviewRequest
            {
                ConfigurationId = request.ConfigurationId,
                RevisionVersion = request.RevisionVersion,
                Page = request.Page,
                PageSize = request.PageSize,
            },
            lease);

        var preview = export.Preview;

        if (!string.Equals(binding.DatasetKey, preview.DatasetKey, StringComparison.OrdinalIgnoreCase))
            throw BusinessException.InvalidParameter($"打印模板族 {family.Title} 与报表数据集不匹配，无法打印");

        var previewColumnByKey = preview.Columns.ToDictionary(c => c.Key, StringComparer.OrdinalIgnoreCase);
        foreach (var bound in binding.BoundColumns)
        {
            if (!previewColumnByKey.TryGetValue(bound.ColumnKey, out _))
                throw new BusinessException(
                    $"打印模板字段 {bound.LegacyKey} 不在当前报表已授权选定字段内，拒绝打印（不产出部分输出）",
                    ErrorCodes.Forbidden);
        }

        var columns = binding.BoundColumns
            .Select(b => new ReportPrintRenderColumnDto
            {
                LegacyKey = b.LegacyKey,
                ColumnKey = b.ColumnKey,
                Title = b.Title,
                Type = b.Type,
                CurrencyUnit = previewColumnByKey[b.ColumnKey].CurrencyUnit,
            })
            .ToList();

        var rows = preview.Rows
            .Select(row => ProjectRow(row, binding.BoundColumns, previewColumnByKey))
            .ToList();

        var result = new ReportConfigurationPrintRenderDto
        {
            TemplateId = template.Id,
            FamilyKey = family.FamilyKey,
            DatasetKey = binding.DatasetKey,
            TemplateName = template.TemplateName,
            Title = string.IsNullOrWhiteSpace(template.Title) ? family.Title : template.Title,
            CompanyName = template.CompanyName,
            CompanyAddress = template.CompanyAddress,
            CompanyPhone = template.CompanyPhone,
            ShowCompanyHeader = template.ShowCompanyHeader,
            FooterText = template.FooterText,
            PaperSize = string.IsNullOrWhiteSpace(template.PaperSize) ? "A4" : template.PaperSize,
            FontSize = template.FontSize,
            TitleFontSize = template.TitleFontSize,
            CompanyFontSize = template.CompanyFontSize,
            FontFamily = string.IsNullOrWhiteSpace(template.FontFamily) ? "Microsoft YaHei" : template.FontFamily,
            CellPadding = template.CellPadding,
            RowHeight = template.RowHeight,
            Layout = string.IsNullOrWhiteSpace(template.LayoutJson)
                ? ReportPrintRenderLayoutText.Standard
                : ReportPrintRenderLayoutText.Grid,
            Columns = columns,
            Rows = rows,
            Evidence = preview.Evidence,
            Coverage = preview.Evidence?.Coverage ?? ReportConfigurationConstants.CoverageCurrentPage,
            MatchedCount = preview.MatchedCount,
            SourceEvidenceCount = preview.SourceEvidenceCount,
            CorrelationId = lease.CorrelationId,
            DefinitionName = preview.Name,
            DefinitionVersion = preview.Version,
            IsPinnedRevision = preview.IsPinnedRevision,
        };

        if (string.Equals(result.Layout, ReportPrintRenderLayoutText.Grid, StringComparison.OrdinalIgnoreCase))
        {
            result.GridBlocks = ReportPrintGridLayoutRules.Build(
                template.LayoutJson,
                binding.BoundColumns,
                rows,
                template);
        }

        return result;
    }

    private static Dictionary<string, object?> ProjectRow(
        Dictionary<string, object?> row,
        IReadOnlyList<ReportPrintTemplateFieldAlias> boundColumns,
        IReadOnlyDictionary<string, ReportConfigurationColumnDto> previewColumnByKey)
    {
        var projected = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var bound in boundColumns)
        {
            var actualKey = previewColumnByKey.TryGetValue(bound.ColumnKey, out var col) ? col.Key : bound.ColumnKey;
            projected[bound.ColumnKey] = TryGetValue(row, actualKey);
        }
        return projected;
    }

    private static object? TryGetValue(Dictionary<string, object?> row, string key)
    {
        if (row.TryGetValue(key, out var value))
            return value;

        foreach (var kv in row)
        {
            if (string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase))
                return kv.Value;
        }

        return null;
    }

    private static void EnsureAuthenticated(long userId)
    {
        if (userId <= 0)
            throw new BusinessException("请先登录后再打印报表", ErrorCodes.Unauthorized);
    }
}

/// <summary>
/// 已保存 LayoutJson 网格布局的有界解析 / 校验 / 占位符替换规则（纯内存、无数据库依赖）。
/// <para>只接受字面文本 + 封闭占位符别名（{CompanyName}/{CompanyAddress}/{CompanyPhone} + 模板族有限字段别名），
/// 拒绝可执行公式 / HTML / 脚本 / 任意属性路径 / 畸形跨行跨列与坐标 / 超大网格；明细区 / 明细模板行 / 水印为
/// 显式不支持（fail closed，绝不静默忽略）。</para>
/// </summary>
public static class ReportPrintGridLayoutRules
{
    private static readonly HashSet<string> CompanyPlaceholders = new(StringComparer.Ordinal)
    {
        "CompanyName", "CompanyAddress", "CompanyPhone",
    };

    private static readonly Regex CellKeyPattern = new(@"^\d+_\d+$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ColorPattern = new(@"^#([0-9a-fA-F]{3}|[0-9a-fA-F]{6})$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex FontPattern = new(@"^[A-Za-z0-9][A-Za-z0-9 ,\-_]{0,49}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>解析、校验并把网格布局替换为一条事实行对应一个网格块（占位符已替换为纯文本）。</summary>
    public static List<ReportPrintGridBlockDto> Build(
        string? layoutJson,
        IReadOnlyList<ReportPrintTemplateFieldAlias> boundColumns,
        IReadOnlyList<Dictionary<string, object?>> rows,
        SysPrintTemplate template)
    {
        var spec = Parse(layoutJson);

        if (rows.Count > ReportPrintRenderLimits.MaxGridBlocks)
            throw new BusinessException(
                $"打印网格渲染行数超出上限（{rows.Count} > {ReportPrintRenderLimits.MaxGridBlocks}），请缩小筛选范围",
                ReportConfigurationExecutionLimits.ErrorCodeResultTooLarge);

        var aliasByLegacyKey = new Dictionary<string, ReportPrintTemplateFieldAlias>(StringComparer.OrdinalIgnoreCase);
        foreach (var bound in boundColumns)
            aliasByLegacyKey[bound.LegacyKey] = bound;

        var blocks = new List<ReportPrintGridBlockDto>(rows.Count);
        var totalCells = 0;
        foreach (var row in rows)
        {
            var cells = new List<ReportPrintGridCellDto>(spec.Cells.Count);
            foreach (var cell in spec.Cells)
            {
                if (spec.CoveredByMerge.Contains((cell.Row, cell.Col)))
                    continue;

                var text = Substitute(cell.Text, aliasByLegacyKey, row, template);
                cells.Add(new ReportPrintGridCellDto
                {
                    Row = cell.Row,
                    Col = cell.Col,
                    Text = text,
                    Bold = cell.Bold,
                    Italic = cell.Italic,
                    Align = cell.Align,
                    FontSize = cell.FontSize,
                    Color = cell.Color,
                    Background = cell.Background,
                    Border = cell.Border,
                });
            }

            foreach (var merge in spec.Merges)
            {
                var topLeft = cells.FirstOrDefault(x => x.Row == merge.Row && x.Col == merge.Col);
                if (topLeft is null)
                {
                    cells.Add(new ReportPrintGridCellDto
                    {
                        Row = merge.Row,
                        Col = merge.Col,
                        RowSpan = merge.RowSpan,
                        ColSpan = merge.ColSpan,
                    });
                }
                else
                {
                    topLeft.RowSpan = merge.RowSpan;
                    topLeft.ColSpan = merge.ColSpan;
                }
            }

            totalCells += cells.Count;
            if (totalCells > ReportPrintRenderLimits.MaxGridTotalCells)
                throw new BusinessException(
                    "打印网格渲染单元格总数超出上限，请精简模板或缩小筛选范围",
                    ReportConfigurationExecutionLimits.ErrorCodeResultTooLarge);

            var footer = string.IsNullOrWhiteSpace(spec.FooterText)
                ? template.FooterText
                : Substitute(spec.FooterText, aliasByLegacyKey, row, template);

            blocks.Add(new ReportPrintGridBlockDto
            {
                FontFamily = spec.FontFamily,
                ColWidths = spec.ColWidths,
                RowHeights = spec.RowHeights,
                Cells = cells,
                FooterText = footer,
            });
        }

        return blocks;
    }

    private static GridSpec Parse(string? layoutJson)
    {
        if (string.IsNullOrWhiteSpace(layoutJson))
            throw BusinessException.InvalidParameter("打印模板网格布局为空");

        if (Encoding.UTF8.GetByteCount(layoutJson) > ReportConfigurationRules.MaxSerializedBytes)
            throw BusinessException.InvalidParameter("打印模板网格布局超过大小上限");

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(layoutJson);
        }
        catch (JsonException)
        {
            throw BusinessException.InvalidParameter("打印模板网格布局不是合法 JSON");
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw BusinessException.InvalidParameter("打印模板网格布局必须是 JSON 对象");

            if (IsEnabled(root, "detail"))
                throw BusinessException.InvalidParameter("打印模板网格明细表区域在本阶段不支持（显式拒绝）");
            if (IsEnabled(root, "detailRow"))
                throw BusinessException.InvalidParameter("打印模板网格明细模板行在本阶段不支持（显式拒绝）");
            if (IsEnabled(root, "watermark"))
                throw BusinessException.InvalidParameter("打印模板网格水印在本阶段不支持（显式拒绝）");

            var cols = ParseDoubleArray(root, "cols", ReportPrintRenderLimits.MaxGridCols,
                ReportPrintRenderLimits.MinColWidthPx, ReportPrintRenderLimits.MaxColWidthPx, "列");
            var rows = ParseDoubleArray(root, "rows", ReportPrintRenderLimits.MaxGridRows,
                ReportPrintRenderLimits.MinRowHeightPx, ReportPrintRenderLimits.MaxRowHeightPx, "行");
            if (cols.Sum() > ReportPrintRenderLimits.MaxGridTotalWidthPx)
                throw BusinessException.InvalidParameter("打印模板网格总宽度超出上限（超大网格）");
            if (rows.Sum() > ReportPrintRenderLimits.MaxGridTotalHeightPx)
                throw BusinessException.InvalidParameter("打印模板网格总高度超出上限（超大网格）");
            var fontFamily = ParseFont(root);
            var merges = ParseMerges(root, cols.Length, rows.Length);
            var cells = ParseCells(root, cols.Length, rows.Length);
            var footerText = ParseFooterText(root);

            var covered = new HashSet<(int Row, int Col)>();
            foreach (var merge in merges)
            {
                for (var r = merge.Row; r < merge.Row + merge.RowSpan; r++)
                {
                    for (var c = merge.Col; c < merge.Col + merge.ColSpan; c++)
                    {
                        if (r == merge.Row && c == merge.Col)
                            continue;
                        covered.Add((r, c));
                    }
                }
            }

            return new GridSpec(cols, rows, cells, merges, fontFamily, footerText, covered);
        }
    }

    private static bool IsEnabled(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var element) || element.ValueKind != JsonValueKind.Object)
            return false;

        return element.TryGetProperty("enabled", out var enabled) && enabled.ValueKind == JsonValueKind.True;
    }

    private static double[] ParseDoubleArray(
        JsonElement root, string propertyName, int maxCount, double min, double max, string label)
    {
        if (!root.TryGetProperty(propertyName, out var element) || element.ValueKind != JsonValueKind.Array)
            throw BusinessException.InvalidParameter($"打印模板网格布局缺少合法的 {label} 数组");

        var items = element.EnumerateArray().ToArray();
        if (items.Length == 0 || items.Length > maxCount)
            throw BusinessException.InvalidParameter($"打印模板网格布局 {label} 数量超出边界（1 ~ {maxCount}）");

        var result = new double[items.Length];
        for (var i = 0; i < items.Length; i++)
        {
            if (items[i].ValueKind != JsonValueKind.Number || !items[i].TryGetDouble(out var value)
                || double.IsNaN(value) || double.IsInfinity(value) || value < min || value > max)
                throw BusinessException.InvalidParameter($"打印模板网格布局 {label} 值非法（须在 {min} ~ {max} 之间）");

            result[i] = value;
        }

        return result;
    }

    private static string ParseFont(JsonElement root)
    {
        if (!root.TryGetProperty("font", out var element) || element.ValueKind == JsonValueKind.Null)
            return "Microsoft YaHei";

        if (element.ValueKind != JsonValueKind.String)
            throw BusinessException.InvalidParameter("打印模板网格布局字体必须是字符串");

        var value = element.GetString() ?? string.Empty;
        if (value.Length == 0)
            return "Microsoft YaHei";
        if (!FontPattern.IsMatch(value))
            throw BusinessException.InvalidParameter("打印模板网格布局字体非法");

        return value;
    }

    private static IReadOnlyList<MergeSpec> ParseMerges(JsonElement root, int colCount, int rowCount)
    {
        if (!root.TryGetProperty("merges", out var element)
            || element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return Array.Empty<MergeSpec>();

        if (element.ValueKind != JsonValueKind.Array)
            throw BusinessException.InvalidParameter("打印模板网格合并区必须是数组");

        var items = element.EnumerateArray().ToArray();
        if (items.Length > ReportPrintRenderLimits.MaxGridMerges)
            throw BusinessException.InvalidParameter($"打印模板网格合并区数量超过上限 {ReportPrintRenderLimits.MaxGridMerges}");

        var result = new List<MergeSpec>(items.Length);
        var occupied = new bool[rowCount, colCount];
        foreach (var item in items)
        {
            if (item.ValueKind != JsonValueKind.Object)
                throw BusinessException.InvalidParameter("打印模板网格合并区必须是对象");

            var row = GetInt(item, "r", 0, rowCount - 1, "合并区行坐标");
            var col = GetInt(item, "c", 0, colCount - 1, "合并区列坐标");
            var rowSpan = GetInt(item, "rs", 1, rowCount - row, "合并区行跨度");
            var colSpan = GetInt(item, "cs", 1, colCount - col, "合并区列跨度");

            for (var r = row; r < row + rowSpan; r++)
            {
                for (var c = col; c < col + colSpan; c++)
                {
                    if (occupied[r, c])
                        throw BusinessException.InvalidParameter("打印模板网格合并区重叠");
                    occupied[r, c] = true;
                }
            }

            result.Add(new MergeSpec(row, col, rowSpan, colSpan));
        }

        return result;
    }

    private static int GetInt(JsonElement item, string propertyName, int min, int max, string label)
    {
        if (!item.TryGetProperty(propertyName, out var element) || element.ValueKind != JsonValueKind.Number
            || !element.TryGetInt32(out var value) || value < min || value > max)
            throw BusinessException.InvalidParameter($"打印模板网格{label}非法（须在 {min} ~ {max} 之间）");

        return value;
    }

    private static IReadOnlyList<CellSpec> ParseCells(JsonElement root, int colCount, int rowCount)
    {
        if (!root.TryGetProperty("cells", out var element) || element.ValueKind != JsonValueKind.Object)
            return Array.Empty<CellSpec>();

        var result = new List<CellSpec>();
        foreach (var property in element.EnumerateObject())
        {
            if (!CellKeyPattern.IsMatch(property.Name))
                throw BusinessException.InvalidParameter("打印模板网格单元格坐标非法");

            var parts = property.Name.Split('_');
            var row = int.Parse(parts[0], CultureInfo.InvariantCulture);
            var col = int.Parse(parts[1], CultureInfo.InvariantCulture);
            if (row < 0 || row >= rowCount || col < 0 || col >= colCount)
                throw BusinessException.InvalidParameter("打印模板网格单元格坐标越界");

            var cell = ParseCell(property.Value);
            cell.Row = row;
            cell.Col = col;
            result.Add(cell);
        }

        if (result.Count > ReportPrintRenderLimits.MaxGridCells)
            throw BusinessException.InvalidParameter($"打印模板网格单元格数量超过上限 {ReportPrintRenderLimits.MaxGridCells}");

        return result;
    }

    private static CellSpec ParseCell(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw BusinessException.InvalidParameter("打印模板网格单元格必须是对象");

        var spec = new CellSpec();

        if (element.TryGetProperty("v", out var value)
            && value.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
        {
            if (value.ValueKind != JsonValueKind.String)
                throw BusinessException.InvalidParameter("打印模板网格单元格文本必须是字符串");

            var text = value.GetString() ?? string.Empty;
            if (text.Length > ReportPrintRenderLimits.MaxCellTextLength)
                throw BusinessException.InvalidParameter($"打印模板网格单元格文本超过上限 {ReportPrintRenderLimits.MaxCellTextLength} 字符");

            ValidateCellText(text);
            spec.Text = text;
        }

        if (element.TryGetProperty("bold", out var bold) && bold.ValueKind == JsonValueKind.True)
            spec.Bold = true;
        if (element.TryGetProperty("italic", out var italic) && italic.ValueKind == JsonValueKind.True)
            spec.Italic = true;

        if (element.TryGetProperty("align", out var align) && align.ValueKind == JsonValueKind.String)
        {
            var alignValue = align.GetString() ?? "left";
            if (alignValue is not ("left" or "center" or "right"))
                throw BusinessException.InvalidParameter("打印模板网格单元格对齐方式非法（仅 left / center / right）");
            spec.Align = alignValue;
        }

        if (element.TryGetProperty("fs", out var fontSize)
            && fontSize.ValueKind == JsonValueKind.Number && fontSize.TryGetDouble(out var fsValue))
        {
            spec.FontSize = Math.Clamp(fsValue,
                ReportPrintRenderLimits.MinGridFontSize, ReportPrintRenderLimits.MaxGridFontSize);
        }

        spec.Color = ParseColor(element, "color");
        spec.Background = ParseColor(element, "bg");

        if (element.TryGetProperty("bd", out var border))
        {
            if (border.ValueKind == JsonValueKind.Number && border.TryGetInt32(out var bdValue) && bdValue is 0 or 1)
                spec.Border = bdValue;
            else if (border.ValueKind == JsonValueKind.True)
                spec.Border = 1;
            else if (border.ValueKind == JsonValueKind.False)
                spec.Border = 0;
            else
                throw BusinessException.InvalidParameter("打印模板网格单元格边框取值非法（仅 0 / 1）");
        }

        return spec;
    }

    private static string? ParseColor(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;

        if (value.ValueKind != JsonValueKind.String)
            throw BusinessException.InvalidParameter("打印模板网格单元格颜色必须是字符串");

        var text = value.GetString() ?? string.Empty;
        if (text.Length == 0)
            return null;
        if (!ColorPattern.IsMatch(text))
            throw BusinessException.InvalidParameter("打印模板网格单元格颜色非法（仅 #RGB / #RRGGBB）");

        return text;
    }

    private static string ParseFooterText(JsonElement root)
    {
        if (!root.TryGetProperty("footer", out var element) || element.ValueKind != JsonValueKind.Object)
            return string.Empty;

        if (!element.TryGetProperty("enabled", out var enabled) || enabled.ValueKind != JsonValueKind.True)
            return string.Empty;

        if (element.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
        {
            var value = text.GetString() ?? string.Empty;
            ValidateCellText(value);
            return value;
        }

        return string.Empty;
    }

    private static string Substitute(
        string text,
        IReadOnlyDictionary<string, ReportPrintTemplateFieldAlias> aliasByLegacyKey,
        Dictionary<string, object?> row,
        SysPrintTemplate template)
    {
        var builder = new StringBuilder(text.Length);
        var index = 0;
        while (index < text.Length)
        {
            var ch = text[index];
            if (ch == '{')
            {
                var close = text.IndexOf('}', index + 1);
                if (close < 0)
                    throw BusinessException.InvalidParameter("打印模板网格占位符缺少右花括号");

                var name = text.Substring(index + 1, close - index - 1);
                builder.Append(ResolvePlaceholder(name, aliasByLegacyKey, row, template));
                index = close + 1;
                continue;
            }

            if (ch == '}')
                throw BusinessException.InvalidParameter("打印模板网格占位符花括号不匹配");

            builder.Append(ch);
            index++;
        }

        return builder.ToString();
    }

    private static string ResolvePlaceholder(
        string name,
        IReadOnlyDictionary<string, ReportPrintTemplateFieldAlias> aliasByLegacyKey,
        Dictionary<string, object?> row,
        SysPrintTemplate template)
    {
        if (CompanyPlaceholders.Contains(name))
        {
            return name switch
            {
                "CompanyName" => template.CompanyName ?? string.Empty,
                "CompanyAddress" => template.CompanyAddress ?? string.Empty,
                "CompanyPhone" => template.CompanyPhone ?? string.Empty,
                _ => string.Empty,
            };
        }

        if (aliasByLegacyKey.TryGetValue(name, out var alias))
        {
            var value = TryGetValue(row, alias.ColumnKey);
            return FormatValue(value);
        }

        throw BusinessException.InvalidParameter($"打印模板网格使用了不支持的占位符：{{{name}}}");
    }

    private static object? TryGetValue(Dictionary<string, object?> row, string key)
    {
        if (row.TryGetValue(key, out var value))
            return value;

        foreach (var kv in row)
        {
            if (string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase))
                return kv.Value;
        }

        return null;
    }

    private static string FormatValue(object? value)
    {
        switch (value)
        {
            case null or DBNull:
                return string.Empty;
            case bool b:
                return b ? "是" : "否";
            case DateTime dt:
                return dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            case DateTimeOffset dto:
                return dto.DateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            case decimal m:
                return m.ToString("0.##", CultureInfo.InvariantCulture);
            case double d:
                return d.ToString("0.##", CultureInfo.InvariantCulture);
            case float f:
                return f.ToString("0.##", CultureInfo.InvariantCulture);
            default:
                return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        }
    }

    private static void ValidateCellText(string text)
    {
        if (text.Length == 0)
            return;

        if (text[0] is '=' or '+' or '-' or '@')
            throw BusinessException.InvalidParameter("打印模板网格单元格不允许可执行公式");

        if (text.IndexOf('<') >= 0 || text.IndexOf('>') >= 0)
            throw BusinessException.InvalidParameter("打印模板网格单元格不允许 HTML / 脚本字符");
    }

    private sealed class GridSpec
    {
        public double[] ColWidths { get; }
        public double[] RowHeights { get; }
        public IReadOnlyList<CellSpec> Cells { get; }
        public IReadOnlyList<MergeSpec> Merges { get; }
        public string FontFamily { get; }
        public string FooterText { get; }
        public HashSet<(int Row, int Col)> CoveredByMerge { get; }

        public GridSpec(
            double[] colWidths,
            double[] rowHeights,
            IReadOnlyList<CellSpec> cells,
            IReadOnlyList<MergeSpec> merges,
            string fontFamily,
            string footerText,
            HashSet<(int Row, int Col)> coveredByMerge)
        {
            ColWidths = colWidths;
            RowHeights = rowHeights;
            Cells = cells;
            Merges = merges;
            FontFamily = fontFamily;
            FooterText = footerText;
            CoveredByMerge = coveredByMerge;
        }
    }

    private sealed class CellSpec
    {
        public int Row { get; set; }
        public int Col { get; set; }
        public string Text { get; set; } = string.Empty;
        public bool Bold { get; set; }
        public bool Italic { get; set; }
        public string Align { get; set; } = "left";
        public double FontSize { get; set; } = 12;
        public string? Color { get; set; }
        public string? Background { get; set; }
        public int Border { get; set; }
    }

    private sealed class MergeSpec
    {
        public int Row { get; }
        public int Col { get; }
        public int RowSpan { get; }
        public int ColSpan { get; }

        public MergeSpec(int row, int col, int rowSpan, int colSpan)
        {
            Row = row;
            Col = col;
            RowSpan = rowSpan;
            ColSpan = colSpan;
        }
    }
}







