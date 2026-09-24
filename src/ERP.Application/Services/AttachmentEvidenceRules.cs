using ERP.Application.Common;
using System.Text;

namespace ERP.Application.Services;

/// <summary>
/// 业务单据附件内容证据的纯规则（ERP-061 建立；ERP-062 在同一模型上接入出口单证；
/// ERP-063 在同一模型上接入既有验货记录与样品记录）：
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
    /// 归属单据类型：出口单证（单证中心台账，ERP-062 把既有单证接入**同一**附件证据模型；
    /// 类型编码与 ERP-045 附件引用册的 <c>ParentTypeTradeDocument</c> 完全一致）。
    /// </summary>
    public const string OwnerTypeTradeDocument = "TradeDocument";

    /// <summary>
    /// 归属单据类型：验货记录（ERP-063）。
    /// <para>**审计结论**：本仓库**没有**独立的验货 / 质检实体与表（`docs/菜单与业务流程优化建议-20260918.md` 明确
    /// 「验货单 QC ❌ 未实现，未立项：采购订单仅有 `QcStatus` 字段，无独立验货单与照片」，`SchemaUpgrader` 也只加过
    /// `PurchaseOrders.QcStatus`）。因此**权威的验货记录**就是既有采购订单上的 QC 执行字段
    /// （<c>QcStatus</c> 验货状态 + <c>ArrivalProgress</c> 到货进度）：本类型以其**既有持久化 Id** 归属，
    /// **不**新建验货主数据、**不**新建第二张表，也绝不按订单号 / 商品文本 / 文件名猜测归属。</para>
    /// </summary>
    public const string OwnerTypeQualityInspection = "QualityInspection";

    /// <summary>
    /// 归属单据类型：样品记录（ERP-063，既有 <c>Sample</c> 实体 / <c>api/crm/samples</c> 工作流）：
    /// 同一附件证据模型、同一套格式 / 大小 / 下载 / 作废口径，**不**新建样品主数据。
    /// </summary>
    public const string OwnerTypeSample = "Sample";

    /// <summary>
    /// 已接入的归属单据类型（超出范围一律拒绝，不做隐式兜底、不猜测归属）：
    /// 销售订单 / 采购订单（ERP-061）、出口单证（ERP-062）、
    /// 验货记录（既有采购订单 QC 记录）与样品记录（既有 <c>Sample</c>，ERP-063）。
    /// 其它单据类型（装柜清单等）仍未接入：一律拒绝，绝不按号码 / 名称 / 文件名猜测归属。
    /// </summary>
    public static readonly string[] SupportedOwnerTypes =
    {
        OwnerTypeSalesOrder,
        OwnerTypePurchaseOrder,
        OwnerTypeTradeDocument,
        OwnerTypeQualityInspection,
        OwnerTypeSample
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

    /// <summary>
    /// 单次「归属单据附件摘要」请求的归属 Id 上限（ERP-062：列表页按**本页** Id 一次批量取回摘要，
    /// 有界、无逐行查库；超出上限一律拒绝而不是静默截断）。
    /// </summary>
    public const int MaxSummaryOwnerIds = 200;

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

    /// <summary>
    /// 规范化归属单据类型：按白名单**大小写不敏感**匹配，并统一输出**规范写法**
    /// （例如 <c>tradedocument</c> → <c>TradeDocument</c>），避免同一类型因大小写不同被存成两个值；
    /// 未知 / 留空一律拒绝，并回显白名单。
    /// </summary>
    public static string NormalizeOwnerType(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        foreach (var supported in SupportedOwnerTypes)
        {
            if (string.Equals(supported, text, StringComparison.OrdinalIgnoreCase)) return supported;
        }

        throw BusinessException.InvalidParameter(
            $"不支持的归属单据类型「{Truncate(value)}」：本模块只接受 {string.Join(" / ", SupportedOwnerTypes)}"
            + "（其它单据类型尚未接入；系统不会按号码或名称猜测归属）");
    }

    /// <summary>归属单据类型文案（未知类型返回「未知单据类型」而不是猜测；与 ERP-045 引用册口径一致）</summary>
    public static string OwnerTypeText(string? ownerType)
        => ownerType?.Trim() switch
        {
            OwnerTypeSalesOrder => "销售订单",
            OwnerTypePurchaseOrder => "采购订单",
            OwnerTypeTradeDocument => "出口单证",
            OwnerTypeQualityInspection => "验货记录",
            OwnerTypeSample => "样品记录",
            _ => "未知单据类型"
        };

    /// <summary>
    /// 验货状态文案（ERP-063）：<c>PurchaseOrders.QcStatus</c> 是**业务人工维护的自由文本**
    /// （未验货 / 验货中 / 合格 / 不合格 / 免验）：只做有界修剪与空值标注，**不**推断、
    /// **不**映射成合格 / 不合格裁定，**不**改写采购订单。
    /// </summary>
    public static string QualityInspectionStatusText(string? qcStatus)
    {
        var text = (qcStatus ?? string.Empty).Trim();
        return text.Length == 0 ? "未登记验货状态" : Truncate(text, 20);
    }

    /// <summary>
    /// 到货进度文案（ERP-063）：<c>PurchaseOrders.ArrivalProgress</c> 同样是自由文本，只做有界修剪与空值标注。
    /// </summary>
    public static string ArrivalProgressText(string? arrivalProgress)
    {
        var text = (arrivalProgress ?? string.Empty).Trim();
        return text.Length == 0 ? "未登记到货进度" : Truncate(text, 20);
    }

    /// <summary>
    /// 样品类型文案（ERP-063）：<c>Samples.SampleType</c> 自由文本，只做有界修剪与空值标注。
    /// </summary>
    public static string SampleTypeText(string? sampleType)
    {
        var text = (sampleType ?? string.Empty).Trim();
        return text.Length == 0 ? "未登记样品类型" : Truncate(text, 20);
    }

    /// <summary>
    /// 客户反馈结果文案（ERP-063）：<c>Samples.Result</c> 自由文本，只做有界修剪与空值标注，
    /// **不**推断样品是否获批准或是否已成交。
    /// </summary>
    public static string SampleResultText(string? result)
    {
        var text = (result ?? string.Empty).Trim();
        return text.Length == 0 ? "未登记客户反馈" : Truncate(text, 20);
    }

    /// <summary>
    /// 出口单证状态文案（TradeDocument.Status 是台账里的自由文本，不是枚举）：
    /// 只做有界修剪与空值标注，**不**推断、不映射、不改写台账状态。
    /// </summary>
    public static string TradeDocumentStatusText(string? status)
    {
        var text = (status ?? string.Empty).Trim();
        return text.Length == 0 ? "未登记状态" : Truncate(text, 20);
    }

    /// <summary>
    /// 规范化一批归属单据 Id（ERP-062 摘要接口）：只接受正整数，按升序去重，长度有界；
    /// 非法值（≤0）与超量请求一律拒绝，**不**静默丢弃或静默截断。
    /// </summary>
    public static List<long> NormalizeOwnerIds(IEnumerable<long>? ownerIds)
    {
        var raw = ownerIds?.ToList() ?? new List<long>();
        if (raw.Count == 0) return new List<long>();

        if (raw.Count > MaxSummaryOwnerIds)
            throw BusinessException.InvalidParameter(
                $"单次最多查询 {MaxSummaryOwnerIds} 个归属单据的附件证据摘要（当前 {raw.Count}）："
                + "请按当前页分批查询（系统不做静默截断）");

        foreach (var id in raw)
        {
            if (id <= 0)
                throw BusinessException.InvalidParameter(
                    $"归属单据 Id「{id}」无效：必须为正整数（系统不按 0 / 负数猜测归属）");
        }

        return raw.Distinct().OrderBy(id => id).ToList();
    }

    /// <summary>
    /// 解析查询串里的归属单据 Id 列表（<c>ids=1,2,3</c> 或重复参数）：分隔符 <c>,</c> / <c>;</c> /
    /// 空白，非法项照实拒绝；空串返回空列表（由服务端返回空摘要而不是查询整表）。
    /// </summary>
    public static List<long> ParseOwnerIds(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return new List<long>();

        var parts = raw.Split(new[] { ',', ';', ' ', '\t', '\r', '\n' },
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var ids = new List<long>(parts.Length);
        foreach (var part in parts)
        {
            if (!long.TryParse(part, out var id))
                throw BusinessException.InvalidParameter(
                    $"归属单据 Id「{Truncate(part)}」不是有效整数：请使用 ids=1,2,3 形式");
            ids.Add(id);
        }

        return NormalizeOwnerIds(ids);
    }

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

    /// <summary>
    /// 归属单据附件摘要文案（ERP-062：列表页只显示**有界计数**，不做逐行查库、不访问任何存储）；
    /// 计数只表示已登记的仓库证据条数，绝不表示已报关 / 已报税 / 已交承运人 / 已交付客户。
    /// <para>ERP-063 起推荐使用按归属类型给出对应边界口径的重载
    /// （<see cref="OwnerSummaryText(string?, string?, string?, int, int, int)"/>）；
    /// 本重载保持 ERP-062 的对外文案（出口单证口径）不变。</para>
    /// </summary>
    public static string OwnerSummaryText(
        string? ownerTypeText, string? ownerNo, int total, int active, int voided)
        => OwnerSummaryTextCore(
            OwnerSnapshotText(ownerTypeText, ownerNo), SummaryBoundaryClause(OwnerTypeTradeDocument),
            total, active, voided);

    /// <summary>
    /// 归属单据附件摘要文案（ERP-063，按归属类型给出对应边界口径）：验货记录**不**表述为验货结论、
    /// 样品记录**不**表述为样品批准，出口单证仍保持报关 / 报税 / 承运人口径，其余单据保持通用口径。
    /// </summary>
    public static string OwnerSummaryText(
        string? ownerType, string? ownerTypeText, string? ownerNo, int total, int active, int voided)
        => OwnerSummaryTextCore(
            OwnerSnapshotText(ownerTypeText, ownerNo), SummaryBoundaryClause(ownerType),
            total, active, voided);

    /// <summary>摘要文案核心（计数为 0 时只说明「暂无仓库附件证据」，绝不把缺失解释成「无缺陷 / 已通过」）</summary>
    private static string OwnerSummaryTextCore(
        string snapshot, string boundaryClause, int total, int active, int voided)
    {
        if (total <= 0)
            return $"{snapshot}：暂无仓库附件证据（用户提供的扫描件证据需显式上传；系统不导入既有附件说明文本）";

        return $"{snapshot}：仓库附件证据 {total} 条（有效 {active} / 已作废 {voided}）；"
               + $"仅为用户上传的仓库证据，{boundaryClause}";
    }

    /// <summary>
    /// 摘要文案的边界从句（按归属类型区分口径；未知 / 历史类型使用通用口径，不借用别的单据类型的话术）。
    /// </summary>
    private static string SummaryBoundaryClause(string? ownerType)
        => ownerType?.Trim() switch
        {
            OwnerTypeTradeDocument => "不代表已向海关 / 税务 / 承运人提交或获其确认",
            OwnerTypeQualityInspection => "不代表验货合格 / 不合格判定、质量认证或供应商绩效结论",
            OwnerTypeSample => "不代表样品已获批准、客户确认或打样报告已完成",
            _ => "不代表已获批准、已付款、已出运或已结算"
        };

    /// <summary>
    /// 历史自由文本（<c>TradeDocuments.FileNote</c>，即「附件说明 / 存放位置」）的口径声明（ERP-062）：
    /// 保持原样可读，但**不**解析成路径、**不**当作 URL 抓取、**不**转成附件证据、**不**在请求时回填。
    /// </summary>
    public const string LegacyFileNotePolicyText =
        "单证台账既有的「附件说明 / 存放位置」是历史自由文本：系统保持其原样可读，"
        + "绝不把它解析成文件路径、绝不按其中的 URL 抓取任何内容、绝不转成附件证据，"
        + "也不在读取时按它回填附件行（附件证据只能由用户在单证中心显式上传）。";

    /// <summary>
    /// 出口单证附件证据的边界声明（ERP-062）：仓库证据与报关 / 报税 / 承运人提交明确区分。
    /// </summary>
    public const string TradeDocumentEvidenceBoundaryText =
        "出口单证附件证据只表示「用户把一份 PDF / PNG / JPEG 仓库文件挂到了该单证台账记录上」："
        + "它不是报关单回执、不是税务备案或退税资料受理结果、不是承运人或客户确认，"
        + "也不代表单证已提交、已放行、已收汇或允许出运；单证状态与明细行一律由单证中心自己的流程维护。";

    /// <summary>
    /// 验货记录附件证据的边界声明（ERP-063）：仓库证据与验货结论 / 质量认证 / 出运许可明确区分。
    /// </summary>
    public const string QualityInspectionEvidenceBoundaryText =
        "验货记录附件证据只表示「用户把一份 PDF / PNG / JPEG 仓库文件挂到了该采购订单的验货记录上」："
        + "它不是验货合格 / 不合格判定、不是质量认证或第三方检验结论，不代表供应商绩效结论、"
        + "不代表允许出运或已获客户接受；验货状态与执行进度一律由采购订单自己的工作流维护，"
        + "系统绝不因为有一条附件就推断验货结论。";

    /// <summary>
    /// 样品记录附件证据的边界声明（ERP-063）：仓库证据与样品批准 / 客户确认明确区分。
    /// </summary>
    public const string SampleEvidenceBoundaryText =
        "样品记录附件证据只表示「用户把一份 PDF / PNG / JPEG 仓库文件挂到了该样品记录上」："
        + "不代表样品已获批准、不代表客户确认或订单承诺、不代表打样完成报告或检测报告结论，"
        + "也不代表样品费已结算；样品状态（客户反馈结果）一律由样品管理自己的工作流维护，"
        + "系统绝不因为有一条附件就推断样品结论。";

    /// <summary>
    /// 按归属单据类型取对应的边界声明（接口与界面同源；大小写不敏感，与归属可用性复核口径一致）：
    /// 未知 / 历史类型回落到通用边界文案，**不**猜测归属、也**不**借用别的单据类型的口径。
    /// </summary>
    public static string BoundaryTextOf(string? ownerType)
    {
        var text = (ownerType ?? string.Empty).Trim();
        if (string.Equals(text, OwnerTypeTradeDocument, StringComparison.OrdinalIgnoreCase))
            return TradeDocumentEvidenceBoundaryText;
        if (string.Equals(text, OwnerTypeQualityInspection, StringComparison.OrdinalIgnoreCase))
            return QualityInspectionEvidenceBoundaryText;
        if (string.Equals(text, OwnerTypeSample, StringComparison.OrdinalIgnoreCase))
            return SampleEvidenceBoundaryText;
        return BoundaryText;
    }

    /// <summary>证据性质与边界声明（接口与界面同源）</summary>
    public const string BoundaryText =
        "附件证据是用户提供的仓库文件证据：不是报关 / 报税 / 银行 / 承运人 / 客户或任何第三方的确认或回执，"
        + "不构成批准、付款、出运、清关或结算依据；系统只按文件签名保存 PDF / PNG / JPEG 内容元数据并提供"
        + "安全的附件下载，绝不改写任何单据状态、金额、明细与库存 / 财务记录。";

    // ==================== 9.1 附件中心工作台（ERP-064：按既有「角色 → 菜单」授权收敛） ====================

    /// <summary>归属类型「销售订单」所需的既有菜单编码</summary>
    public const string MenuCodeSalesOrder = "sales-order";

    /// <summary>归属类型「采购订单」所需的既有菜单编码</summary>
    public const string MenuCodePurchaseOrder = "purchase-order";

    /// <summary>归属类型「出口单证」所需的既有菜单编码（单证中心）</summary>
    public const string MenuCodeTradeDocument = "doc-center";

    /// <summary>归属类型「样品记录」所需的既有菜单编码（样品管理）</summary>
    public const string MenuCodeSample = "sample";

    /// <summary>
    /// 归属单据类型 → 访问该类型附件证据所需的**既有菜单编码**（与 <c>SysMenus.MenuCode</c> 同名）。
    /// <para>附件中心工作台<strong>不</strong>引入新的权限模型：它只复用既有的「角色 → 菜单」授权
    /// （<c>SysUserRoles</c> → <c>SysRoleMenus</c> → <c>SysMenus.MenuCode</c>）。因此「当前用户被允许访问的
    /// 权威父单据」＝「当前用户已获对应模块菜单授权的归属类型」；未授权类型既不显示记录也不显示计数。</para>
    /// <para>验货记录的权威记录是**既有采购订单上的 QC 记录**，因此与采购订单共用同一菜单授权；
    /// 未知 / 历史类型返回空串（一律不授权，绝不借用别的单据类型的权限）。</para>
    /// </summary>
    public static string RequiredMenuCodeOf(string? ownerType) => ownerType?.Trim() switch
    {
        OwnerTypeSalesOrder => MenuCodeSalesOrder,
        OwnerTypePurchaseOrder => MenuCodePurchaseOrder,
        OwnerTypeTradeDocument => MenuCodeTradeDocument,
        OwnerTypeQualityInspection => MenuCodePurchaseOrder,
        OwnerTypeSample => MenuCodeSample,
        _ => string.Empty
    };

    /// <summary>归属类型所需的既有菜单文案（与界面同源；未知类型返回「未知来源菜单」而不是猜测）</summary>
    public static string RequiredMenuTextOf(string? ownerType) => ownerType?.Trim() switch
    {
        OwnerTypeSalesOrder => "销售订单菜单",
        OwnerTypePurchaseOrder => "采购订单菜单",
        OwnerTypeTradeDocument => "单证中心菜单",
        OwnerTypeQualityInspection => "采购订单菜单（验货记录的权威记录就是既有采购订单上的 QC 记录）",
        OwnerTypeSample => "样品管理菜单",
        _ => "未知来源菜单"
    };

    /// <summary>
    /// 授权状态文案：未授权时明确说明「记录 / 计数 / 文件名 / 摘要一律不显示」，
    /// 避免界面把「看不到」误解成「没有」。
    /// </summary>
    public static string CenterAuthorizationText(string? ownerType, bool authorized)
    {
        var menu = RequiredMenuTextOf(ownerType);
        return authorized
            ? $"已获「{menu}」授权：可查看该归属类型的仓库附件证据元数据"
            : $"未获「{menu}」授权：该归属类型的记录、计数、文件名与摘要一律不显示（系统不披露不可访问记录的存在性）";
    }

    /// <summary>工作台归属号码快照筛选（可空；有界，超长拒绝而不是无界扫描）</summary>
    public static string? NormalizeCenterOwnerNoFilter(string? value)
        => BoundedFilter(value, "归属单据号码筛选", MaxOwnerNoLength);

    /// <summary>工作台文件名快照筛选（可空；只匹配已持久化的净化文件名，长度上限 255）</summary>
    public static string? NormalizeCenterFileNameFilter(string? value)
        => BoundedFilter(value, "文件名筛选", MaxOriginalFileNameLength);

    /// <summary>工作台上传人筛选（可空；有界）</summary>
    public static string? NormalizeCenterUploadedByFilter(string? value)
        => BoundedFilter(value, "上传人筛选", MaxUploadedByLength);

    /// <summary>工作台媒体类型筛选（可空；只接受白名单取值，大小写不敏感，输出规范写法）</summary>
    public static string? NormalizeMediaTypeFilter(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = value.Trim();
        foreach (var supported in SupportedMediaTypes)
        {
            if (string.Equals(supported, text, StringComparison.OrdinalIgnoreCase)) return supported;
        }

        throw BusinessException.InvalidParameter(
            $"不支持的媒体类型筛选值「{Truncate(value)}」：本模块只保存 {string.Join(" / ", SupportedMediaTypes)}");
    }

    /// <summary>
    /// 工作台登记日期区间（可空；只做区间合法性校验，不做任何时间推断 / 时区转换）：
    /// 起点晚于终点一律拒绝，避免出现「看似无结果」的静默空区间；区间终点按**含**处理
    /// （由查询侧对终点加一天作为上界）。
    /// </summary>
    public static (DateTime? From, DateTime? To) NormalizeRecordedRange(DateTime? from, DateTime? to)
    {
        if (from is not null && to is not null && from.Value.Date > to.Value.Date)
            throw BusinessException.InvalidParameter(
                $"登记日期区间无效（起点 {from:yyyy-MM-dd} 晚于终点 {to:yyyy-MM-dd}）：请调整筛选条件后再查询");

        return (from?.Date, to?.Date);
    }

    /// <summary>工作台筛选字段：可空文本（去首尾空白、拒绝控制字符、有界，超长拒绝而不是静默截断）</summary>
    private static string? BoundedFilter(string? value, string fieldName, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = value.Trim();
        if (text.Length > maxLength)
            throw BusinessException.InvalidParameter($"{fieldName}不能超过 {maxLength} 个字符（当前 {text.Length}）");
        EnsureNoControlCharacters(text, fieldName);
        return text;
    }

    /// <summary>工作台只读声明（界面与接口同源）</summary>
    public const string CenterReadOnlyNoticeText =
        "附件中心工作台是只读视图：只按已持久化的显式元数据列出 / 筛选既有附件证据，"
        + "不新增、不改写、不作废任何证据与父单据，也不改动任何业务状态、金额、明细与库存 / 财务记录。";

    /// <summary>
    /// 工作台证据性质声明（界面与接口同源）：附件是不可信文件证据，
    /// 不是批准 / 验货结论 / 报关或税务提交 / 付款授权 / 结算确认 / 出运许可。
    /// </summary>
    public const string CenterUntrustedEvidenceNoticeText =
        "附件是仓库内的不可信文件证据：它只是用户上传并登记在仓库里的一份 PDF / PNG / JPEG。"
        + "它不是批准或审核结论、不是验货合格或不合格判定、不是质量认证或第三方检验结论、"
        + "不是报关或税务提交 / 受理结果、不是银行付款凭证或付款授权、不是结算 / 对账确认，"
        + "也不构成出运许可；系统只显示服务端权威保存的内容元数据，绝不按内容推断任何业务结论。";

    /// <summary>工作台未授权类型的披露口径（界面与接口同源）</summary>
    public const string CenterUnauthorizedNoticeText =
        "工作台按当前账号的既有「角色 → 菜单」授权收敛：未获菜单授权的归属类型不返回任何记录、计数、"
        + "文件名或摘要（连计数行都不返回），系统也不披露不可访问记录的存在性；被撤销授权后，"
        + "此前可见的记录会立即从列表与计数中消失，直接打开元数据、历史或下载都会 fail closed。";

    /// <summary>工作台筛选口径声明（界面与接口同源）</summary>
    public const string CenterFilterPolicyText =
        "筛选只使用已持久化的显式元数据（归属类型 / 归属 Id / 归属号码快照 / 文件名快照 / 媒体类型 / 上传人 / "
        + "登记日期区间 / 状态）：不扫描文件内容、不抓取任何远端地址、不做模糊跨记录匹配，"
        + "也绝不因为文件名或摘要相同而合并或改派记录。";

    /// <summary>工作台边界声明（只读 + 明确「不是什么」）</summary>
    public const string CenterBoundaryText =
        "附件是仓库内用户上传的不可信文件证据：不是批准、不是验货合格 / 不合格判定或质量认证、"
        + "不是报关或税务提交 / 受理结果、不是付款授权或银行凭证、不是结算 / 对账确认，"
        + "也不是出运许可或承运人 / 客户确认；工作台只读，不改写附件、父单据、工作流状态、库存、"
        + "出运、单证、发票、税务、财务、审批与结算记录。";

    /// <summary>工作台可见范围文案（授权类型为空时明确 fail closed，绝不把「看不到」写成「没有」）</summary>
    public static string CenterScopeText(IReadOnlyList<string> authorizedTypeTexts, int unauthorizedTypeCount)
    {
        var scope = authorizedTypeTexts.Count == 0
            ? "当前账号没有任何已授权的归属类型：工作台不显示记录与计数（fail closed）"
            : $"当前账号可查看的归属类型：{string.Join(" / ", authorizedTypeTexts)}";

        return unauthorizedTypeCount <= 0
            ? scope
            : $"{scope}；另有 {unauthorizedTypeCount} 类归属未授权"
              + "（只显示「未授权」这一事实，不显示其记录、计数、文件名与摘要）";
    }

    /// <summary>工作台摘要文案（计数只在授权范围内统计，绝不把未授权类型计入合计）</summary>
    public static string CenterSummaryText(int total, int active, int voided, int authorizedTypeCount)
        => $"授权范围内共 {total} 条仓库附件证据（有效 {active} / 已作废 {voided}），覆盖 {authorizedTypeCount} 类归属；"
           + "计数只统计当前账号已授权的归属类型，未授权类型既不计入合计也不单独显示。";

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
