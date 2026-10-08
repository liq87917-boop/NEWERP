using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ERP.Application.DTOs;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Content;
using PdfSharp.Pdf.Content.Objects;
using PdfSharp.Pdf.IO;

namespace ERP.Infrastructure.Export;

/// <summary>
/// 有界托管 PDF 解码器（ERP-336）：从实际 PDF 字节解码真实「有序列头 + 有序类型化 / null 单元格 + 币种 / 单位标签 +
/// 页 / 列带 / 行延续」语义，供 parity 输出语义比较器做确定性只读比对。
/// <list type="bullet">
/// <item><b>Unicode / CMap 文本</b>：解析内容流 Tf / Td / Tm / Tj / TJ 文本操作，结合各字体的 ToUnicode CMap
/// 把字形码映射回 Unicode；无法映射 / 无 CMap 且非 ASCII 的字形一律 fail closed（绝不产出乱码或猜测）。</item>
/// <item><b>有界</b>：字节 / 页数 / 令牌数 / 矩形数 / 行数全部有界；超过边界即 resource overflow，拒绝比对。</item>
/// <item><b>布局语义</b>：按网格矩形（re）重建表格行列，把空单元格表示为 null（绝不回落为 0 / 文本），
/// 按页 / 列带合并重复标识列与数据列，跨行页续接，绝不丢列。</item>
/// </list>
/// <para>纯只读：仅解析 PDF 对象与内容流，不写库、不执行任意 SQL、不扩权。</para>
/// </summary>
public sealed class ReportMigrationPdfArtifactDecoder
{
    private const int DefaultMaxArtifactBytes = 8 * 1024 * 1024;
    private const int MaxPages = 200;
    private const int MaxTextTokens = 40000;
    private const int MaxRects = 40000;
    private const int MaxDataRows = 200;

    private const double CellEpsilon = 3.0;
    private const double RowGroupTolerance = 4.0;

    private static readonly NumberStyles Hex = NumberStyles.HexNumber;

    private readonly int _maxArtifactBytes;

    public ReportMigrationPdfArtifactDecoder(int maxArtifactBytes = DefaultMaxArtifactBytes)
    {
        _maxArtifactBytes = maxArtifactBytes > 0 ? maxArtifactBytes : DefaultMaxArtifactBytes;
    }

    /// <summary>解码实际 PDF 字节；失败返回 null 并把精确原因写入 <paramref name="evidence"/>（fail closed）。</summary>
    public ReportMigrationPdfArtifactDto? Decode(byte[] bytes, string side, List<string> evidence)
    {
        if (bytes is null || bytes.Length == 0)
        {
            evidence.Add($"{side} 为空");
            return null;
        }

        if (bytes.Length > _maxArtifactBytes)
        {
            evidence.Add($"{side} 超限（oversized）：{bytes.Length} 字节，拒绝比对（fail closed）");
            return null;
        }

        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            using var document = PdfReader.Open(stream, PdfDocumentOpenMode.Import);

            if (document.SecuritySettings?.IsEncrypted == true)
            {
                evidence.Add($"{side} 加密（encrypted）：拒绝提取与比对（fail closed）");
                return null;
            }

            if (document.PageCount > MaxPages)
            {
                evidence.Add($"{side} 页数超限（resource overflow）：{document.PageCount} 页");
                return null;
            }

            var cmapCache = new Dictionary<string, Dictionary<int, string>?>(StringComparer.Ordinal);
            var grids = new List<Grid>();
            var embeddedFont = false;

            for (var pageIndex = 0; pageIndex < document.PageCount; pageIndex++)
            {
                var page = document.Pages[pageIndex];
                var grid = ExtractPage(page, pageIndex, side, cmapCache, evidence);
                if (grid is null)
                    return null;

                if (grid.HasUndecodableText)
                {
                    evidence.Add($"{side} 第 {pageIndex + 1} 页存在不可提取的字形映射（unsupported glyph mapping / unextractable）");
                    return null;
                }

                embeddedFont |= grid.EmbeddedFont;
                if (grid.Headers.Count > 0 || grid.Rows.Count > 0)
                    grids.Add(grid);
            }

            if (grids.Count == 0)
            {
                evidence.Add($"{side} 无可提取的表格内容（images-only / unextractable）");
                return null;
            }

            var table = BuildTable(grids, side, evidence);
            if (table is null)
                return null;

            return new ReportMigrationPdfArtifactDto(
                document.PageCount,
                embeddedFont,
                table.Headers.Count > 0 || table.Rows.Count > 0,
                table.Headers,
                table.Rows);
        }
        catch (Exception ex)
        {
            evidence.Add($"{side} 解码失败（malformed / unextractable）：{ex.Message}");
            return null;
        }
    }

    private Grid? ExtractPage(
        PdfPage page,
        int pageIndex,
        string side,
        Dictionary<string, Dictionary<int, string>?> cmapCache,
        List<string> evidence)
    {
        CSequence content;
        try
        {
            content = ContentReader.ReadContent(page);
        }
        catch (Exception ex)
        {
            evidence.Add($"{side} 第 {pageIndex + 1} 页内容流不可提取（unextractable）：{ex.Message}");
            return null;
        }

        var textTokens = new List<TextToken>();
        var rects = new List<CellRect>();

        var textX = 0d;
        var textY = 0d;
        string? currentFont = null;
        var hasUndecodable = false;

        foreach (var item in content)
        {
            if (item is not COperator op)
                continue;

            switch (op.OpCode.OpCodeName)
            {
                case OpCodeName.BT:
                    textX = 0;
                    textY = 0;
                    break;

                case OpCodeName.Tf:
                    if (op.Operands.Count >= 2 && op.Operands[0] is CName fontName)
                        currentFont = fontName.Name;
                    break;

                case OpCodeName.Td:
                case OpCodeName.TD:
                    if (op.Operands.Count >= 2)
                    {
                        textX += Number(op.Operands[0]);
                        textY += Number(op.Operands[1]);
                    }
                    break;

                case OpCodeName.Tm:
                    if (op.Operands.Count >= 6)
                    {
                        textX = Number(op.Operands[4]);
                        textY = Number(op.Operands[5]);
                    }
                    break;

                case OpCodeName.Tj:
                    if (op.Operands.Count >= 1)
                        hasUndecodable |= AddText(textTokens, op.Operands[0], textX, textY, currentFont, page, cmapCache);
                    break;

                case OpCodeName.TJ:
                    if (op.Operands.Count >= 1 && op.Operands[0] is CArray array)
                    {
                        var x = textX;
                        foreach (var element in array)
                        {
                            if (element is CString)
                            {
                                hasUndecodable |= AddText(textTokens, element, x, textY, currentFont, page, cmapCache);
                            }
                            else if (element is CInteger or CReal)
                            {
                                x -= Number(element) / 1000 * 8.0;
                            }
                        }
                    }
                    break;

                case OpCodeName.re:
                    if (op.Operands.Count >= 4)
                    {
                        rects.Add(new CellRect(
                            Number(op.Operands[0]),
                            Number(op.Operands[1]),
                            Number(op.Operands[2]),
                            Number(op.Operands[3])));
                    }
                    break;
            }

            if (textTokens.Count > MaxTextTokens || rects.Count > MaxRects)
            {
                evidence.Add($"{side} 第 {pageIndex + 1} 页令牌 / 矩形数超限（resource overflow）");
                return null;
            }
        }

        var grid = new Grid
        {
            EmbeddedFont = DetectEmbeddedFont(page),
            HasUndecodableText = hasUndecodable,
        };

        if (rects.Count == 0)
            return grid;

        var rows = GroupRectRows(rects);
        if (rows.Count == 0)
            return grid;

        var headerRow = rows[0];
        var dataRows = rows.Skip(1).ToList();

        if (dataRows.Count > MaxDataRows)
        {
            evidence.Add($"{side} 数据行数超限（resource overflow）：{dataRows.Count} 行");
            return null;
        }

        foreach (var rect in headerRow.OrderBy(r => r.X))
            grid.Headers.Add(CellText(rect, textTokens));

        foreach (var row in dataRows)
        {
            var cells = new List<ReportMigrationPdfCellDto>(row.Count);
            foreach (var rect in row.OrderBy(r => r.X))
                cells.Add(BuildCell(rect, textTokens));
            grid.Rows.Add(cells);
        }

        return grid;
    }


    private static bool AddText(
        List<TextToken> tokens,
        CObject operand,
        double x,
        double y,
        string? fontName,
        PdfPage page,
        Dictionary<string, Dictionary<int, string>?> cmapCache)
    {
        if (operand is not CString text)
            return false;

        var decoded = DecodeText(text, fontName, page, cmapCache);
        if (decoded is null)
            return true; // 不可解码 → unsupported glyph mapping（fail closed）

        if (decoded.Length == 0)
            return false;

        tokens.Add(new TextToken(x, y, decoded));
        return false;
    }

    private static string? DecodeText(
        CString text,
        string? fontName,
        PdfPage page,
        Dictionary<string, Dictionary<int, string>?> cmapCache)
    {
        var value = text.Value;
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        var raw = new byte[value.Length];
        for (var i = 0; i < value.Length; i++)
        {
            var ch = value[i];
            if (ch > 0xFF)
                return value; // 已是解码后的 Unicode（UTF-16BE 字面量）
            raw[i] = (byte)ch;
        }

        // UTF-16BE BOM 字面量
        if (raw.Length >= 2 && raw[0] == 0xFE && raw[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(raw, 2, raw.Length - 2);

        // 优先走 ToUnicode CMap（字形码 / CID → Unicode）
        var cmap = GetToUnicodeMap(fontName, page, cmapCache);
        if (cmap is not null)
        {
            if (raw.Length % 2 != 0)
                return null; // 奇数字节 → 无法按 2 字节字形码对齐

            var builder = new StringBuilder(raw.Length / 2);
            for (var i = 0; i < raw.Length; i += 2)
            {
                var code = (raw[i] << 8) | raw[i + 1];
                if (!cmap.TryGetValue(code, out var mapped))
                    return null; // 未映射字形 → unsupported glyph mapping

                builder.Append(mapped);
            }

            return builder.ToString();
        }

        // 无 CMap：纯 ASCII 才可安全直读，否则 fail closed。
        for (var i = 0; i < raw.Length; i++)
        {
            if (raw[i] >= 0x80)
                return null;
        }

        return Encoding.ASCII.GetString(raw);
    }

    private static Dictionary<int, string>? GetToUnicodeMap(
        string? fontName,
        PdfPage page,
        Dictionary<string, Dictionary<int, string>?> cmapCache)
    {
        if (string.IsNullOrEmpty(fontName))
            return null;

        if (cmapCache.TryGetValue(fontName, out var cached))
            return cached;

        Dictionary<int, string>? cmap = null;
        try
        {
            var key = !string.IsNullOrEmpty(fontName) && fontName[0] == '/'
                ? fontName
                : "/" + fontName;
            var fonts = page.Resources?.Elements.GetDictionary("/Font");
            var font = fonts?.Elements.GetDictionary(key);
            var toUnicode = font?.Elements.GetDictionary("/ToUnicode");
            var stream = toUnicode?.Stream;
            if (stream is not null)
            {
                byte[] bytes;
                try
                {
                    bytes = stream.UnfilteredValue;
                }
                catch
                {
                    bytes = stream.Value;
                }

                if (bytes is not null && bytes.Length > 0)
                    cmap = ParseToUnicodeCmap(Encoding.ASCII.GetString(bytes));
            }
        }
        catch
        {
            cmap = null;
        }

        cmapCache[fontName] = cmap;
        return cmap;
    }


    private static Dictionary<int, string>? ParseToUnicodeCmap(string text)
    {
        var map = new Dictionary<int, string>();

        ParseBfchar(text, map);
        ParseBfrange(text, map);

        return map.Count == 0 ? null : map;
    }

    private static void ParseBfchar(string text, Dictionary<int, string> map)
    {
        var start = 0;
        while (true)
        {
            var begin = text.IndexOf("beginbfchar", start, StringComparison.Ordinal);
            if (begin < 0)
                return;

            var end = text.IndexOf("endbfchar", begin, StringComparison.Ordinal);
            if (end < 0)
                return;

            var section = text.Substring(begin + "beginbfchar".Length, end - begin - "beginbfchar".Length);
            foreach (var groups in ExtractHexGroups(section))
            {
                if (groups.Count == 2)
                {
                    var code = int.Parse(groups[0], Hex);
                    map[code] = HexToUnicode(groups[1]);
                }
            }

            start = end + "endbfchar".Length;
        }
    }

    private static void ParseBfrange(string text, Dictionary<int, string> map)
    {
        var start = 0;
        while (true)
        {
            var begin = text.IndexOf("beginbfrange", start, StringComparison.Ordinal);
            if (begin < 0)
                return;

            var end = text.IndexOf("endbfrange", begin, StringComparison.Ordinal);
            if (end < 0)
                return;

            var section = text.Substring(begin + "beginbfrange".Length, end - begin - "beginbfrange".Length);
            foreach (var groups in ExtractHexGroups(section))
            {
                if (groups.Count == 3)
                {
                    var lo = int.Parse(groups[0], Hex);
                    var hi = int.Parse(groups[1], Hex);
                    var dst = int.Parse(groups[2], Hex);
                    for (var code = lo; code <= hi; code++)
                        map[code] = new string((char)(dst + (code - lo)), 1);
                }
            }

            start = end + "endbfrange".Length;
        }
    }

    private static List<List<string>> ExtractHexGroups(string section)
    {
        var result = new List<List<string>>();
        foreach (var rawLine in section.Split('\n'))
        {
            var line = rawLine.Trim();
            var groups = new List<string>();
            foreach (Match match in Regex.Matches(line, @"<([0-9A-Fa-f]+)>"))
                groups.Add(match.Groups[1].Value);

            if (groups.Count > 0)
                result.Add(groups);
        }

        return result;
    }

    private static string HexToUnicode(string hex)
    {
        var bytes = new byte[hex.Length / 2];
        for (var i = 0; i < bytes.Length; i++)
            bytes[i] = (byte)int.Parse(hex.Substring(i * 2, 2), Hex);

        return Encoding.BigEndianUnicode.GetString(bytes);
    }


    private static List<List<CellRect>> GroupRectRows(List<CellRect> rects)
    {
        var rows = new List<List<CellRect>>();
        foreach (var rect in rects.OrderByDescending(r => r.Y))
        {
            List<CellRect>? row = null;
            foreach (var existing in rows)
            {
                if (Math.Abs(existing[0].Y - rect.Y) <= RowGroupTolerance)
                {
                    row = existing;
                    break;
                }
            }

            if (row is null)
            {
                row = new List<CellRect>();
                rows.Add(row);
            }

            row.Add(rect);
        }

        return rows;
    }

    private static string CellText(CellRect rect, List<TextToken> tokens)
        => string.Concat(TokensIn(rect, tokens));

    private static ReportMigrationPdfCellDto BuildCell(CellRect rect, List<TextToken> tokens)
    {
        var texts = TokensIn(rect, tokens).ToList();
        return texts.Count == 0
            ? new ReportMigrationPdfCellDto(string.Empty, true)
            : new ReportMigrationPdfCellDto(string.Concat(texts), false);
    }

    private static IEnumerable<string> TokensIn(CellRect rect, List<TextToken> tokens)
        => tokens
            .Where(t => t.X >= rect.X - CellEpsilon
                        && t.X < rect.X + rect.Width
                        && t.Y >= rect.Y - CellEpsilon
                        && t.Y <= rect.Y + rect.Height + CellEpsilon)
            .OrderByDescending(t => t.Y)
            .ThenBy(t => t.X)
            .Select(t => t.Text);

    private static double Number(CObject obj) => obj switch
    {
        CReal real => real.Value,
        CInteger integer => integer.Value,
        _ => 0,
    };

    private static bool DetectEmbeddedFont(PdfPage page)
    {
        try
        {
            var fonts = page.Resources?.Elements.GetDictionary("/Font");
            if (fonts is null)
                return false;

            foreach (var key in fonts.Elements.Keys)
            {
                var font = fonts.Elements.GetDictionary(key);
                if (font is null)
                    continue;

                if (HasFontProgram(font))
                    return true;
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    private static bool HasFontProgram(PdfDictionary font)
    {
        var descriptor = font.Elements.GetDictionary("/FontDescriptor");
        if (descriptor is not null && HasFontFile(descriptor))
            return true;

        var descendants = font.Elements.GetArray("/DescendantFonts");
        if (descendants is not null)
        {
            foreach (var element in descendants.Elements)
            {
                if (element is PdfDictionary descendant)
                {
                    var child = descendant.Elements.GetDictionary("/FontDescriptor");
                    if (child is not null && HasFontFile(child))
                        return true;
                }
            }
        }

        return false;
    }

    private static bool HasFontFile(PdfDictionary descriptor)
        => descriptor.Elements.ContainsKey("/FontFile")
           || descriptor.Elements.ContainsKey("/FontFile2")
           || descriptor.Elements.ContainsKey("/FontFile3");


    private static Table? BuildTable(List<Grid> grids, string side, List<string> evidence)
    {
        var bands = new List<(List<string> Header, List<List<ReportMigrationPdfCellDto>> Rows)>();
        foreach (var grid in grids)
        {
            var signature = string.Join("\u0001", grid.Headers);
            var band = bands.FirstOrDefault(b => string.Join("\u0001", b.Header) == signature);
            if (band.Header is null)
            {
                band = (grid.Headers.ToList(), new List<List<ReportMigrationPdfCellDto>>());
                bands.Add(band);
            }

            band.Rows.AddRange(grid.Rows);
        }

        if (bands.Count == 0)
        {
            evidence.Add($"{side} 无可用列带（unextractable）");
            return null;
        }

        var firstHeader = bands[0].Header;
        var identityCount = 0;
        for (var c = 0; c < firstHeader.Count; c++)
        {
            var common = bands.All(b => b.Header.Count > c
                && string.Equals(b.Header[c], firstHeader[c], StringComparison.Ordinal));
            if (!common)
                break;

            identityCount = c + 1;
        }

        if (identityCount == 0)
        {
            evidence.Add($"{side} 列带无共享标识列，无法语义对齐（unextractable）");
            return null;
        }

        var headers = new List<string>(firstHeader.Take(identityCount));
        foreach (var band in bands)
            headers.AddRange(band.Header.Skip(identityCount));

        var rowCount = bands[0].Rows.Count;
        if (bands.Any(b => b.Rows.Count != rowCount))
        {
            evidence.Add($"{side} 列带 / 页行数不一致（band/page truncation），拒绝比对");
            return null;
        }

        var rows = new List<IReadOnlyList<ReportMigrationPdfCellDto>>();
        for (var r = 0; r < rowCount; r++)
        {
            var row = new List<ReportMigrationPdfCellDto>(headers.Count);

            for (var c = 0; c < identityCount; c++)
            {
                var reference = bands[0].Rows[r][c];
                for (var b = 1; b < bands.Count; b++)
                {
                    if (bands[b].Rows[r][c].IsNull != reference.IsNull
                        || !string.Equals(bands[b].Rows[r][c].Text, reference.Text, StringComparison.Ordinal))
                    {
                        evidence.Add($"{side} 第 {r} 行标识列在不同列带不一致（band/page truncation），拒绝比对");
                        return null;
                    }
                }

                row.Add(reference);
            }

            foreach (var band in bands)
                for (var c = identityCount; c < band.Rows[r].Count; c++)
                    row.Add(band.Rows[r][c]);

            rows.Add(row);
        }

        return new Table(headers, rows);
    }

    private sealed record TextToken(double X, double Y, string Text);

    private sealed record CellRect(double X, double Y, double Width, double Height);

    private sealed class Grid
    {
        public List<string> Headers { get; } = new();
        public List<List<ReportMigrationPdfCellDto>> Rows { get; } = new();
        public bool EmbeddedFont { get; set; }
        public bool HasUndecodableText { get; set; }
    }

    private sealed record Table(
        IReadOnlyList<string> Headers,
        IReadOnlyList<IReadOnlyList<ReportMigrationPdfCellDto>> Rows);
}

