using ERP.Application.Common;
using System.Text;

namespace ERP.Application.Services;

/// <summary>
/// 业务单据附件内容证据的纯规则（ERP-061，无数据库与存储依赖，便于逐条单测）：
/// 归属单据白名单、内容格式判定（扩展名 / 声明 Content-Type / 文件签名三者一致）、
/// 可执行与标记类格式拒绝、文件名净化、有界校验（大小 / 说明 / 作废原因）与全部文案。
/// <para>边界：本规则只做**校验与文案**，不读写数据库、不访问任何存储提供程序、不发起任何网络请求，
/// 也不改写父单据与库存 / 财务 / 出运 / 单证数据。</para>
/// </summary>
public static class AttachmentEvidenceRules
{
    // ==================== 0. 白名单：归属单据类型 ====================

    /// <summary>归属单据类型：销售订单</summary>
    public const string OwnerTypeSalesOrder = "SalesOrder";

    /// <summary>归属单据类型：采购订单</summary>
    public const string OwnerTypePurchaseOrder = "PurchaseOrder";

    /// <summary>
    /// 本任务支持的归属单据类型（超出范围一律拒绝，不做隐式兜底、不猜测归属）；
    /// 单证（TradeDocument）等其它类型由后续任务在同一模型上扩展。
    /// </summary>
    public static readonly string[] SupportedOwnerTypes =
    {
        OwnerTypeSalesOrder,
        OwnerTypePurchaseOrder
    };

    // ==================== 1. 内容存储提供程序（唯一接缝） ====================

    /// <summary>隔离的非生产本地存储（开发 / 测试唯一允许的提供程序）</summary>
    public const string ProviderIsolatedLocal = "local-isolated";

    /// <summary>生产对象存储（既有 <c>OssStorageService</c>）：本阶段**未实现 / 未注册 / 未激活**</summary>
    public const string ProviderOss = "oss";

    /// <summary>提供程序配置键（缺省 = 隔离的非生产本地存储）</summary>
    public const string ProviderConfigurationKey = "Attachments:Provider";

    /// <summary>隔离本地存储根目录配置键（缺省 = 应用目录之外的隔离目录）</summary>
    public const string LocalRootConfigurationKey = "Attachments:LocalRootPath";

    // ==================== 2. 状态 ====================

    /// <summary>状态：有效（内容可下载）</summary>
    public const int StatusActive = 0;

    /// <summary>状态：已作废（保留原始元数据与内容，但不再提供下载）</summary>
    public const int StatusVoided = 1;

    // ==================== 3. 允许的内容格式 ====================

    /// <summary>媒体类型：PDF</summary>
    public const string MediaPdf = "application/pdf";

    /// <summary>媒体类型：PNG</summary>
    public const string MediaPng = "image/png";

    /// <summary>媒体类型：JPEG</summary>
    public const string MediaJpeg = "image/jpeg";

    /// <summary>允许的媒体类型（超出范围一律拒绝）</summary>
    public static readonly string[] SupportedMediaTypes = { MediaPdf, MediaPng, MediaJpeg };

    /// <summary>允许的扩展名（小写，含点）</summary>
    public static readonly string[] SupportedExtensions = { ".pdf", ".png", ".jpg", ".jpeg" };

    /// <summary>格式口径文案（接口 / 界面同源）</summary>
    public const string SupportedFormatsText = "PDF / PNG / JPEG";

    /// <summary>
    /// 明确拒绝的可执行 / 脚本 / 标记类扩展名：真正的内容判定始终以文件签名为准，
    /// 此表只用于给出更清楚的拒绝提示（不允许用改名伪装成合法图片 / PDF）。
    /// </summary>
    public static readonly string[] ActiveOrExecutableExtensions =
    {
        ".html", ".htm", ".xhtml", ".svg", ".xml", ".js", ".mjs", ".vbs", ".wsf", ".hta",
        ".exe", ".dll", ".com", ".scr", ".msi", ".bat", ".cmd", ".ps1", ".psm1", ".sh",
        ".jar", ".class", ".php", ".asp", ".aspx", ".jsp", ".py", ".rb", ".pl",
        ".zip", ".rar", ".7z", ".gz", ".tar", ".docm", ".xlsm", ".pptm"
    };

    // ==================== 4. 有界上限 ====================

    /// <summary>单个附件内容大小上限（20 MiB；有界，服务端实测）</summary>
    public const long MaxSizeBytes = 20L * 1024 * 1024;

    /// <summary>单次上传请求上限（内容上限 + 1 MiB 表单开销；由控制器请求大小限制兜底）</summary>
    public const long MaxRequestBytes = MaxSizeBytes + (1024 * 1024);

    /// <summary>原始文件名快照长度上限</summary>
    public const int MaxOriginalFileNameLength = 255;

    /// <summary>说明长度上限</summary>
    public const int MaxDescriptionLength = 500;

    /// <summary>作废原因长度上限</summary>
    public const int MaxVoidReasonLength = 500;

    /// <summary>归属单据号码快照长度上限</summary>
    public const int MaxOwnerNoLength = 50;

    /// <summary>上传人长度上限</summary>
    public const int MaxUploadedByLength = 100;

    /// <summary>存储键长度上限</summary>
    public const int MaxStorageKeyLength = 200;

    /// <summary>关键字筛选长度上限</summary>
    public const int MaxKeywordLength = 100;

    /// <summary>文件签名探测字节数（最长签名 8 字节）</summary>
    public const int SignatureProbeLength = 8;

    /// <summary>摘要长度（SHA-256 十六进制）</summary>
    public const int Sha256Length = 64;

    /// <summary>摘要筛选最小长度（允许按前缀比对，便于人工核对重复上传）</summary>
    public const int MinSha256FilterLength = 8;

    /// <summary>未知值统一文案（缺失一律记为「未知」，绝不推断）</summary>
    public const string UnknownText = "未知";

    // ==================== 5. 归属单据类型 ====================

    /// <summary>归属单据类型是否在白名单内（大小写不敏感，服务端统一输出规范写法）</summary>
    public static bool IsSupportedOwnerType(string? value)
        => value is not null
           && SupportedOwnerTypes.Contains(value.Trim(), StringComparer.OrdinalIgnoreCase);

    /// <summary>规范化归属单据类型（未知 / 留空一律拒绝，并回显白名单）</summary>
    public static string NormalizeOwnerType(string? value)
    {
        if (!IsSupportedOwnerType(value))
            throw BusinessException.InvalidParameter(
                $"不支持的归属单据类型「{Truncate(value)}」：本模块只接受 {string.Join(" / ", SupportedOwnerTypes)}"
                + "（其它单据类型尚未接入；系统不会按号码或名称猜测归属）");

        return value!.Trim();
    }

    /// <summary>归属单据类型文案（未知类型返回「未知单据类型」而不是猜测）</summary>
    public static string OwnerTypeText(string? ownerType)
        => ownerType?.Trim() switch
        {
            OwnerTypeSalesOrder => "销售订单",
            OwnerTypePurchaseOrder => "采购订单",
            _ => "未知单据类型"
        };

    // ==================== 6. 有界文本 ====================

    /// <summary>说明（可空 → 空串；有界：超长拒绝，不做静默截断）</summary>
    public static string NormalizeDescription(string? value)
        => BoundedOptionalText(value, "证据说明", MaxDescriptionLength);

    /// <summary>上传人（空 → 「未知用户」，绝不猜测当前身份；有界）</summary>
    public static string NormalizeUploadedBy(string? value)
    {
        var text = BoundedOptionalText(value, "上传人", MaxUploadedByLength);
        return text.Length == 0 ? "未知用户" : text;
    }

    /// <summary>作废原因（必填且有界：更正必须写明原因，不允许静默覆盖或删除）</summary>
    public static string NormalizeVoidReason(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        if (text.Length == 0)
            throw BusinessException.InvalidParameter(
                "请填写作废原因（作废保留原始文件名、摘要与登记历史，必须记录更正原因）");
        if (text.Length > MaxVoidReasonLength)
            throw BusinessException.InvalidParameter(
                $"作废原因不能超过 {MaxVoidReasonLength} 个字符（当前 {text.Length}）");

        EnsureNoControlCharacters(text, "作废原因");
        EnsureNoMarkupOrLink(text, "作废原因");
        return text;
    }

    /// <summary>关键字筛选（可空；有界，超长拒绝而不是无界扫描）</summary>
    public static string? NormalizeKeyword(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = value.Trim();
        if (text.Length > MaxKeywordLength)
            throw BusinessException.InvalidParameter(
                $"关键字不能超过 {MaxKeywordLength} 个字符（当前 {text.Length}）");
        return text;
    }

    /// <summary>摘要筛选（可空；只接受 8~64 位十六进制，按前缀比对，**不**作为证据身份）</summary>
    public static string? NormalizeSha256Filter(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = value.Trim().ToLowerInvariant();
        if (text.Length is < MinSha256FilterLength or > Sha256Length || !IsHex(text))
            throw BusinessException.InvalidParameter(
                $"摘要筛选只接受 {MinSha256FilterLength}~{Sha256Length} 位十六进制（SHA-256 前缀 / 全值）；"
                + "摘要只用于内容比对，不是证据身份，系统也不会按摘要去重");

        return text;
    }

    /// <summary>输出侧摘要（历史 / 外部写入的异常值照实可读，不做修正）</summary>
    public static string DigestText(string? sha256)
    {
        var text = (sha256 ?? string.Empty).Trim();
        return text.Length == 0 ? UnknownText : text.ToLowerInvariant();
    }

    /// <summary>状态筛选（可空；只接受 0 / 1）</summary>
    public static int? NormalizeStatusFilter(int? status)
    {
        if (status is null) return null;
        if (status is not (StatusActive or StatusVoided))
            throw BusinessException.InvalidParameter(
                $"未知的证据状态筛选值「{status}」：只接受 {StatusActive}（有效）或 {StatusVoided}（已作废）");
        return status;
    }

    // ==================== 7. 文件名净化（客户端路径一律忽略） ====================

    /// <summary>
    /// 净化客户端文件名，得到**只作展示的原始文件名快照**：
    /// 路径（Windows / Unix 分隔符）一律丢弃，控制字符与文件系统 / 标记类不安全字符替换为 <c>_</c>，
    /// 首尾空格与点去除，长度有界（超长截断但保留扩展名）。
    /// <para>净化结果只用于展示与下载时的文件名提示，绝不用于拼接任何存储路径。</para>
    /// </summary>
    public static string SanitizeFileName(string? clientFileName)
    {
        var raw = clientFileName ?? string.Empty;

        // 1) 客户端路径一律忽略：只取最后一段（同时处理 / 与 \，且不接受盘符形态）
        var cut = raw.LastIndexOfAny(new[] { '/', '\\' });
        var last = cut >= 0 ? raw[(cut + 1)..] : raw;

        // 2) 控制字符丢弃；文件系统 / 标记类不安全字符替换
        var builder = new StringBuilder(last.Length);
        foreach (var ch in last)
        {
            if (char.IsControl(ch)) continue;
            builder.Append(UnsafeFileNameCharacters.Contains(ch) ? '_' : ch);
        }

        var name = builder.ToString().Trim(' ', '.');
        if (name.Length == 0)
            throw BusinessException.InvalidParameter("请选择要上传的附件文件（文件名不能为空）");

        // 3) 超长截断：保留扩展名，避免把证据文件名整段丢弃
        if (name.Length > MaxOriginalFileNameLength)
        {
            var extension = FileExtension(name);
            var stemKeep = MaxOriginalFileNameLength - extension.Length;
            name = stemKeep > 0 ? name[..stemKeep] + extension : name[..MaxOriginalFileNameLength];
        }

        return name;
    }

    /// <summary>取小写扩展名（含点）；无扩展名返回空串</summary>
    public static string FileExtension(string? fileName)
    {
        var name = fileName ?? string.Empty;
        var dot = name.LastIndexOf('.');
        return dot <= 0 || dot == name.Length - 1 ? string.Empty : name[dot..].ToLowerInvariant();
    }

    // ==================== 8. 内容格式判定（签名 / 扩展名 / 声明类型三者一致） ====================

    /// <summary>服务端判定的内容身份（存储扩展名 + 媒体类型；均由文件签名决定）</summary>
    public sealed record AttachmentContentIdentity(string Extension, string MediaType);

    /// <summary>
    /// 按文件签名判定媒体类型（无法识别返回 null）：PDF <c>%PDF-</c>、PNG 8 字节魔数、JPEG <c>FF D8 FF</c>。
    /// <para>判定只依赖内容本身，因此改名伪装（<c>.png</c> 里放 HTML / 脚本）一律无法通过。</para>
    /// </summary>
    public static string? DetectMediaType(ReadOnlySpan<byte> head)
    {
        if (head.Length >= 5 && head[0] == 0x25 && head[1] == 0x50 && head[2] == 0x44 && head[3] == 0x46 && head[4] == 0x2D)
            return MediaPdf;

        if (head.Length >= 8 && head[0] == 0x89 && head[1] == 0x50 && head[2] == 0x4E && head[3] == 0x47
            && head[4] == 0x0D && head[5] == 0x0A && head[6] == 0x1A && head[7] == 0x0A)
            return MediaPng;

        if (head.Length >= 3 && head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF)
            return MediaJpeg;

        return null;
    }

    /// <summary>媒体类型对应的存储扩展名（未知类型返回空串）</summary>
    public static string MediaTypeExtension(string? mediaType)
        => mediaType switch
        {
            MediaPdf => ".pdf",
            MediaPng => ".png",
            MediaJpeg => ".jpg",
            _ => string.Empty
        };

    /// <summary>媒体类型文案（未知类型返回「未知类型」而不是猜测）</summary>
    public static string MediaTypeText(string? mediaType)
        => mediaType switch
        {
            MediaPdf => "PDF 文档",
            MediaPng => "PNG 图片",
            MediaJpeg => "JPEG 图片",
            _ => "未知类型"
        };

    /// <summary>
    /// 完整的内容校验（上传路径唯一入口）：文件名净化 → 扩展名白名单与可执行 / 标记类拒绝 →
    /// 声明 Content-Type 白名单 → 文件签名判定 → 三者一致性。
    /// <para>任何一步失败都抛 <see cref="BusinessException.InvalidParameter"/>，调用方据此**不保存任何内容**。</para>
    /// </summary>
    public static AttachmentContentIdentity ValidateUpload(
        string? clientFileName, string? declaredContentType, ReadOnlySpan<byte> head)
    {
        var fileName = SanitizeFileName(clientFileName);
        var extension = FileExtension(fileName);
        if (extension.Length == 0)
            throw BusinessException.InvalidParameter(
                "附件文件名缺少扩展名：只接受 .pdf / .png / .jpg / .jpeg（系统按文件签名复核内容）");

        if (ActiveOrExecutableExtensions.Contains(extension))
            throw BusinessException.InvalidParameter(
                $"拒绝上传可执行 / 脚本 / 标记类文件（{extension}）：附件证据只允许 {SupportedFormatsText}，"
                + "且上传内容一律按不可信文件处理，绝不在浏览器中渲染");

        if (!SupportedExtensions.Contains(extension))
            throw BusinessException.InvalidParameter(
                $"仅支持 {SupportedFormatsText} 证据（扩展名 .pdf / .png / .jpg / .jpeg）；"
                + $"当前扩展名「{extension}」不受支持，系统不保存无法识别的文件");

        var declared = NormalizeDeclaredContentType(declaredContentType);

        var detected = DetectMediaType(head)
            ?? throw BusinessException.InvalidParameter(
                $"附件内容不是受支持的 {SupportedFormatsText}（按文件签名判定）：已拒绝上传，"
                + "系统不保存无法识别的文件，也不按扩展名猜测类型");

        if (!IsExtensionForMediaType(extension, detected))
            throw BusinessException.InvalidParameter(
                $"附件扩展名（{extension}）与文件签名判定的类型（{MediaTypeText(detected)}）不一致："
                + "已拒绝上传（可能是改名伪装或内容被替换）");

        if (!string.Equals(declared, detected, StringComparison.Ordinal))
            throw BusinessException.InvalidParameter(
                $"声明的 Content-Type（{declared}）与文件签名判定的类型（{MediaTypeText(detected)}）不一致：已拒绝上传");

        return new AttachmentContentIdentity(MediaTypeExtension(detected), detected);
    }

    /// <summary>规范化声明的 Content-Type：允许 <c>type/subtype; charset=...</c> 形态，只取参数前的部分</summary>
    public static string NormalizeDeclaredContentType(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        var semi = text.IndexOf(';');
        if (semi >= 0) text = text[..semi].Trim();
        text = text.ToLowerInvariant();
        if (text == "image/jpg") text = MediaJpeg;   // 兼容常见别名，仍以文件签名判定结果为准

        if (!SupportedMediaTypes.Contains(text))
            throw BusinessException.InvalidParameter(
                $"声明的 Content-Type「{Truncate(value)}」不受支持：只接受 application/pdf、image/png、image/jpeg；"
                + "HTML / SVG / 脚本 / 未知二进制流等类型一律拒绝");
        return text;
    }

    /// <summary>扩展名是否与签名判定的媒体类型相容（.jpg 与 .jpeg 视为同一类型）</summary>
    public static bool IsExtensionForMediaType(string? extension, string? mediaType)
        => (mediaType, extension) switch
        {
            (MediaPdf, ".pdf") => true,
            (MediaPng, ".png") => true,
            (MediaJpeg, ".jpg") => true,
            (MediaJpeg, ".jpeg") => true,
            _ => false
        };

    // ==================== 9. 读取侧文案 ====================

    /// <summary>状态文案（未知状态照实返回枚举值而不是猜测）</summary>
    public static string StatusText(int status) => status switch
    {
        StatusActive => "有效",
        StatusVoided => "已作废",
        _ => $"未知状态（{status}）"
    };

    /// <summary>字节长度文案（B / KB / MB；有界展示）</summary>
    public static string SizeText(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024L * 1024) return $"{bytes / 1024.0:0.#} KB";
        return $"{bytes / (1024.0 * 1024.0):0.##} MB";
    }

    /// <summary>归属单据可用性文案（不可用时照实说明，历史证据照常可读）</summary>
    public static string OwnerAvailabilityText(bool available)
        => available
            ? "归属单据可用"
            : "归属单据已不存在或已删除（历史证据仍可只读查看，但不提供下载，也不能用于新的上挂）";

    /// <summary>内容可下载性文案（只有「有效 + 归属单据可用 + 内容未被外力改动」才可下载）</summary>
    public static string DownloadAvailabilityText(bool downloadable, bool isVoided, bool ownerAvailable)
    {
        if (downloadable) return "可下载（以附件方式流式返回，浏览器不内联渲染）";
        if (isVoided) return "已作废：不提供下载（原始文件名 / 摘要 / 登记历史仍保留可读）";
        if (!ownerAvailable) return "归属单据不可用：不提供下载（需要先恢复或重新确认归属单据）";
        return "不可下载：请核对证据状态与归属单据";
    }

    /// <summary>归属单据快照文案（类型 + 号码；号码缺失时只显示类型，绝不猜测）</summary>
    public static string OwnerSnapshotText(string? ownerTypeText, string? ownerNo)
    {
        var typeText = (ownerTypeText ?? string.Empty).Trim();
        var no = (ownerNo ?? string.Empty).Trim();
        return no.Length == 0 ? typeText : $"{typeText} {no}";
    }

    /// <summary>内容存储提供程序文案（与存储实现同源；未知提供程序照实标注而不是伪装成本地存储）</summary>
    public static string ProviderText(string? providerCode)
        => providerCode?.Trim() switch
        {
            ProviderIsolatedLocal => "隔离非生产本地存储（开发 / 测试，内容在 Web 根目录之外）",
            ProviderOss => "阿里云 OSS 生产对象存储（**未激活**：属生产 OSS Human Gate）",
            _ => UnknownText
        };

    /// <summary>内容下载接口相对地址（**只给 API 路径**，绝不给出存储键或文件系统 / 对象存储路径）</summary>
    public static string ContentApiPath(long id) => $"/api/attachment-evidences/{id}/content";

    /// <summary>证据性质与边界声明（接口与界面同源）</summary>
    public const string BoundaryText =
        "附件证据是用户提供的仓库文件证据：不是报关 / 报税 / 银行 / 承运人 / 客户或任何第三方的确认或回执，"
        + "不构成批准、付款、出运、清关或结算依据；系统只按文件签名保存 PDF / PNG / JPEG 内容元数据并提供"
        + "安全的附件下载，绝不改写任何单据状态、金额、明细与库存 / 财务记录。";

    // ==================== 10. 内部工具 ====================

    /// <summary>文件名中必须替换的不安全字符（文件系统保留字符 + 标记 / 引号类字符）</summary>
    private static readonly char[] UnsafeFileNameCharacters =
    {
        '<', '>', ':', '"', '|', '?', '*', ';', '%', '\'', '`', '\t'
    };

    /// <summary>可空文本：去首尾空白、拒绝控制字符与标记 / 链接形态、有界</summary>
    private static string BoundedOptionalText(string? value, string fieldName, int maxLength)
    {
        var text = (value ?? string.Empty).Trim();
        if (text.Length == 0) return string.Empty;
        EnsureNoControlCharacters(text, fieldName);
        EnsureNoMarkupOrLink(text, fieldName);
        if (text.Length > maxLength)
            throw BusinessException.InvalidParameter($"{fieldName}不能超过 {maxLength} 个字符（当前 {text.Length}）");
        return text;
    }

    /// <summary>控制字符校验（制表 / 换行 / 回车以外的控制字符一律拒绝）</summary>
    private static void EnsureNoControlCharacters(string value, string fieldName)
    {
        foreach (var ch in value)
        {
            if (char.IsControl(ch) && ch is not '\r' and not '\n' and not '\t')
                throw BusinessException.InvalidParameter($"{fieldName}不能包含控制字符");
        }
    }

    /// <summary>纯文本校验：拒绝 HTML / 脚本标记字符与链接 / 数据方案形态</summary>
    private static void EnsureNoMarkupOrLink(string value, string fieldName)
    {
        if (value.Contains('<', StringComparison.Ordinal) || value.Contains('>', StringComparison.Ordinal))
            throw BusinessException.InvalidParameter($"{fieldName}不能包含 HTML / 脚本标记字符（< 或 >）");

        if (value.Contains("://", StringComparison.Ordinal)
            || value.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("vbscript:", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            throw BusinessException.InvalidParameter(
                $"{fieldName}不能是链接或脚本 / 数据方案（系统只记录纯文本说明，不做任何抓取）");
    }

    /// <summary>十六进制判定</summary>
    private static bool IsHex(string value)
    {
        if (value.Length == 0) return false;
        foreach (var ch in value)
        {
            if (!char.IsAsciiHexDigit(ch)) return false;
        }

        return true;
    }

    /// <summary>截断回显（错误提示中有界回显，避免把超长 / 不安全原值整段抛出）</summary>
    private static string Truncate(string? value, int max = 60)
    {
        var text = value ?? string.Empty;
        return text.Length <= max ? text : text[..max] + "…";
    }
}
