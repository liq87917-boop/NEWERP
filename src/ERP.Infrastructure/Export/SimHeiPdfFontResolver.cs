using PdfSharp.Fonts;

namespace ERP.Infrastructure.Export;

/// <summary>
/// 共享的 PDFsharp 中文字体（Windows 黑体 SimHei）解析器（ERP-116 与 ERP-121 共用）。
/// 把「定位 simhei.ttf + 精确注册为 PDFsharp 字体解析器」抽成单一实现，避免销售订单与客户应收账款
/// 两种报表在同一个进程内各自注册解析器而互相覆盖；字体缺失时显式失败（不产出乱码或缺字 PDF）。
/// </summary>
public static class SimHeiPdfFontResolver
{
    /// <summary>固定使用的中文字体族（Windows 黑体）</summary>
    public const string FontFamily = "SimHei";

    /// <summary>黑体字体文件名（Windows 字体目录）</summary>
    public const string FontFileName = "simhei.ttf";

    /// <summary>
    /// 在 Windows 字体目录查找黑体 SimHei（simhei.ttf，大小写不敏感）；找不到返回 null。
    /// </summary>
    public static string? FindFontPath()
    {
        string? fonts = null;
        try
        {
            fonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        }
        catch
        {
            fonts = null;
        }

        if (!string.IsNullOrWhiteSpace(fonts) && Directory.Exists(fonts))
        {
            foreach (var file in Directory.EnumerateFiles(fonts))
            {
                if (string.Equals(Path.GetFileName(file), FontFileName, StringComparison.OrdinalIgnoreCase))
                    return file;
            }
        }

        // 兜底：%WINDIR%\Fonts\simhei.ttf
        var windows = Environment.GetEnvironmentVariable("WINDIR");
        if (!string.IsNullOrWhiteSpace(windows))
        {
            var alternative = Path.Combine(windows, "Fonts", FontFileName);
            if (File.Exists(alternative))
                return alternative;
        }

        return null;
    }

    /// <summary>
    /// 把指定字体路径精确注册为 PDFsharp 全局字体解析器（仅当尚未注册同一路径时才更新）。
    /// 同一进程内两种报表共用此解析器，不会互相替换为其它（缺字）字体。
    /// </summary>
    public static void Ensure(string path)
    {
        if (GlobalFontSettings.FontResolver is Resolver resolver
            && string.Equals(resolver.FontPath, path, StringComparison.OrdinalIgnoreCase))
            return;

        GlobalFontSettings.FontResolver = new Resolver(path);
    }

    /// <summary>把 Windows 黑体 SimHei 精确注册为 PDFsharp 字体（避免被替换为缺字字体）</summary>
    private sealed class Resolver : IFontResolver
    {
        public string FontPath { get; }

        public Resolver(string path)
        {
            FontPath = path;
        }

        public FontResolverInfo? ResolveTypeface(string familyName, bool bold, bool italic)
            => string.Equals(familyName, FontFamily, StringComparison.OrdinalIgnoreCase)
                ? new FontResolverInfo(FontFamily)
                : null;

        public byte[]? GetFont(string faceName)
            => string.Equals(faceName, FontFamily, StringComparison.OrdinalIgnoreCase)
                ? File.ReadAllBytes(FontPath)
                : null;
    }
}
