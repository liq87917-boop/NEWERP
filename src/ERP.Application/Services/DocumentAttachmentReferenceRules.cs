using ERP.Application.Common;

namespace ERP.Application.Services;

/// <summary>
/// 业务单据附件引用登记的纯规则（ERP-045，无数据库依赖，便于逐条单测）：
/// 父单据类型与分类白名单、有界元数据校验（显示名 / 引用标识 / 内容类型 / 字节数 / 校验和 / 备注）、
/// 不透明引用标识判定（拒绝链接、路径穿越、标记字符与空白）、来源授权确认校验、状态机与文案。
/// <para>边界：本规则只做**校验与文案**，不写库、不访问对象存储、不发起任何服务端请求，
/// 也不改写父单据（订单 / 装柜清单 / 单证）与库存 / 财务 / 出运数据。</para>
/// </summary>
public static class DocumentAttachmentReferenceRules
{
    // ==================== 0. 白名单：父单据类型 ====================

    /// <summary>父单据类型：销售订单</summary>
    public const string ParentTypeSalesOrder = "SalesOrder";

    /// <summary>父单据类型：采购订单</summary>
    public const string ParentTypePurchaseOrder = "PurchaseOrder";

    /// <summary>父单据类型：装柜清单</summary>
    public const string ParentTypeContainerLoadingList = "ContainerLoadingList";

    /// <summary>父单据类型：出口单证（单证中心）</summary>
    public const string ParentTypeTradeDocument = "TradeDocument";

    /// <summary>支持的父单据类型（超出范围一律拒绝，不做隐式兜底、不猜测父单据归属）</summary>
    public static readonly string[] SupportedParentTypes =
    {
        ParentTypeSalesOrder,
        ParentTypePurchaseOrder,
        ParentTypeContainerLoadingList,
        ParentTypeTradeDocument
    };

    // ==================== 1. 白名单：附件分类 ====================

    /// <summary>分类：合同 / 协议</summary>
    public const string CategoryContract = "Contract";

    /// <summary>分类：发票</summary>
    public const string CategoryInvoice = "Invoice";

    /// <summary>分类：装箱单 / 重量单</summary>
    public const string CategoryPackingList = "PackingList";

    /// <summary>分类：报关资料</summary>
    public const string CategoryCustoms = "Customs";

    /// <summary>分类：证件 / 证书（产地证、检测报告等）</summary>
    public const string CategoryCertificate = "Certificate";

    /// <summary>分类：运输单据（提单、订舱确认等）</summary>
    public const string CategoryTransport = "Transport";

    /// <summary>分类：图片 / 影印件</summary>
    public const string CategoryPhoto = "Photo";

    /// <summary>分类：其他</summary>
    public const string CategoryOther = "Other";

    /// <summary>支持的附件分类（超出范围一律拒绝）</summary>
    public static readonly string[] SupportedCategories =
    {
        CategoryContract,
        CategoryInvoice,
        CategoryPackingList,
        CategoryCustoms,
        CategoryCertificate,
        CategoryTransport,
        CategoryPhoto,
        CategoryOther
    };

    // ==================== 2. 状态 ====================

    /// <summary>状态：有效（登记完成、可读；作废后不再有效）</summary>
    public const int StatusActive = 0;

    /// <summary>状态：已作废（保留原始元数据 / 授权留痕 / 审计历史，不物理删除）</summary>
    public const int StatusVoided = 1;

    // ==================== 3. 有界上限 ====================

    /// <summary>显示名长度上限</summary>
    public const int MaxDisplayNameLength = 200;

    /// <summary>不透明引用标识长度上限</summary>
    public const int MaxReferenceIdLength = 200;

    /// <summary>内容类型长度上限</summary>
    public const int MaxContentTypeLength = 120;

    /// <summary>校验和长度上限（十六进制摘要）</summary>
    public const int MaxChecksumLength = 128;

    /// <summary>校验和长度下限</summary>
    public const int MinChecksumLength = 16;

    /// <summary>备注长度上限</summary>
    public const int MaxNotesLength = 500;

    /// <summary>来源授权确认说明长度上限</summary>
    public const int MaxAuthorizationNoteLength = 300;

    /// <summary>确认人 / 确认来源长度上限</summary>
    public const int MaxAuthorizedByLength = 100;

    /// <summary>作废原因长度上限</summary>
    public const int MaxVoidReasonLength = 500;

    /// <summary>父单据号码快照长度上限（与实体 [MaxLength(50)] 一致）</summary>
    public const int MaxParentNoLength = 50;

    /// <summary>父单据类型文案长度上限</summary>
    public const int MaxParentTypeTextLength = 30;

    /// <summary>字节大小上限（10 GiB）：0 = 未提供 / 未知，负数与超上限一律拒绝</summary>
    public const long MaxSizeBytes = 10L * 1024 * 1024 * 1024;

    /// <summary>单据详情工作流中单次最多返回的附件引用条数（保证视图有界）</summary>
    public const int MaxPerParent = 200;

    // ==================== 4. 口径文案（接口 / 界面 / 文档同源） ====================

    /// <summary>引用标识与显示名口径文案</summary>
    public const string ReferencePolicyText =
        "引用标识口径（服务端权威校验）：只接受 1~200 位由字母 / 数字 / 点 / 下划线 / 加号 / 连字符组成的"
        + "不透明令牌（首字符必须是字母或数字，且不得包含连续的「..」）；链接（含 :// 或 data: / javascript: 方案）、"
        + "绝对 / 相对路径、反斜杠、空白、百分号编码、查询串与 HTML / 脚本标记字符（< > \" '）一律拒绝。"
        + "显示名只作为纯文本标签：拒绝控制字符、< > 标记字符与链接形态；"
        + "历史或外部写入的不安全引用值在读取时只显示「不可用」文本，系统绝不发起服务端抓取、不嵌入不可信标记。";

    /// <summary>大小口径文案</summary>
    public const string SizePolicyText =
        "大小口径：字节数取值 0~10737418240（10 GiB），0 表示未提供 / 未知；负数或超出上限一律拒绝"
        + "（系统不校验文件是否真实存在，也不读取文件内容）。";

    /// <summary>来源授权口径文案</summary>
    public const string AuthorizationPolicyText =
        "来源授权口径：创建引用必须显式确认来源授权（SourceAuthorizationAcknowledged = true）并填写确认说明；"
        + "该确认只表示登记人声明其有权引用该来源，不授予对象存储访问权，也不表示系统已认定生产数据合规。";

    /// <summary>唯一口径文案（同一父单据 + 分类 + 引用标识的有效记录唯一）</summary>
    public const string UniquenessPolicyText =
        "唯一口径：同一父单据（类型 + Id）+ 分类 + 引用标识在**有效**记录内唯一，重复登记一律拒绝"
        + "（不静默合并、不覆盖）；已作废记录保留可读但不占用身份，因此作废后可重新登记同一引用标识。";

    /// <summary>只登记元数据的提示文案</summary>
    public const string MetadataOnlyNoticeText =
        "附件引用只登记元数据：系统不上传 / 不下载 / 不预览 / 不抓取任何对象，"
        + "也不校验文件是否存在、内容是否安全、是否真实或是否已获下载授权；"
        + "列表中的条目是**元数据引用**，不是文件可用的证明。";

    /// <summary>模块边界文案</summary>
    public const string BoundaryText =
        "本登记册只记录不透明引用标识与有界元数据：不做对象存储读写、不做服务端抓取、不嵌入不可信标记，"
        + "不提供硬删除、远端对象删除或静默替换，也不改写父单据（销售订单 / 采购订单 / 装柜清单 / 出口单证）的"
        + "状态、金额、明细、库存与库存成本、财务、出运与审批数据。";

    /// <summary>历史兼容文案（既有 FileNote 与商品图片位保持原样）</summary>
    public const string LegacyNoticeText =
        "历史兼容：既有单证「附件说明 / 存放位置」（FileNote）与商品图片位（Image1~3）保持原样，"
        + "系统不会自动导入、改写或把它们当作已授权附件；新增附件引用只影响本登记册。";

    /// <summary>作废口径文案</summary>
    public const string VoidPolicyText =
        "作废口径：作废必须填写原因，保留原始元数据、授权留痕与审计历史；"
        + "系统不提供硬删除、远端对象删除或静默替换。";

    // ==================== 5. 父单据类型与分类 ====================

    /// <summary>父单据类型是否受支持（大小写不敏感）</summary>
    public static bool IsSupportedParentType(string? parentType)
    {
        var value = (parentType ?? string.Empty).Trim();
        return SupportedParentTypes.Contains(value, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>父单据类型规范化（必须显式且受支持；空值 / 未知类型一律拒绝，并统一为规范写法）</summary>
    public static string NormalizeParentType(string? parentType)
    {
        var value = (parentType ?? string.Empty).Trim();
        foreach (var supported in SupportedParentTypes)
        {
            if (string.Equals(supported, value, StringComparison.OrdinalIgnoreCase)) return supported;
        }

        throw BusinessException.InvalidParameter(
            $"父单据类型「{Truncate(value)}」不受支持：只允许 {string.Join(" / ", SupportedParentTypes)}"
            + "（销售订单 / 采购订单 / 装柜清单 / 出口单证以外的单据不能登记附件引用）");
    }

    /// <summary>父单据类型文案（用于快照与界面显示；未知类型返回空串，不猜测）</summary>
    public static string ParentTypeText(string? parentType)
    {
        var value = (parentType ?? string.Empty).Trim();
        if (string.Equals(ParentTypeSalesOrder, value, StringComparison.OrdinalIgnoreCase)) return "销售订单";
        if (string.Equals(ParentTypePurchaseOrder, value, StringComparison.OrdinalIgnoreCase)) return "采购订单";
        if (string.Equals(ParentTypeContainerLoadingList, value, StringComparison.OrdinalIgnoreCase)) return "装柜清单";
        if (string.Equals(ParentTypeTradeDocument, value, StringComparison.OrdinalIgnoreCase)) return "出口单证";
        return string.Empty;
    }

    /// <summary>分类是否受支持（大小写不敏感）</summary>
    public static bool IsSupportedCategory(string? category)
    {
        var value = (category ?? string.Empty).Trim();
        return SupportedCategories.Contains(value, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>分类规范化（必须显式且受支持；空值 / 未知分类一律拒绝）</summary>
    public static string NormalizeCategory(string? category)
    {
        var value = (category ?? string.Empty).Trim();
        foreach (var supported in SupportedCategories)
        {
            if (string.Equals(supported, value, StringComparison.OrdinalIgnoreCase)) return supported;
        }

        throw BusinessException.InvalidParameter(
            $"附件分类「{Truncate(value)}」不受支持：只允许 {string.Join(" / ", SupportedCategories)}");
    }

    /// <summary>分类文案（未知分类返回原值，读取侧不静默改写）</summary>
    public static string CategoryText(string? category)
    {
        var value = (category ?? string.Empty).Trim();
        if (string.Equals(CategoryContract, value, StringComparison.OrdinalIgnoreCase)) return "合同 / 协议";
        if (string.Equals(CategoryInvoice, value, StringComparison.OrdinalIgnoreCase)) return "发票";
        if (string.Equals(CategoryPackingList, value, StringComparison.OrdinalIgnoreCase)) return "装箱单 / 重量单";
        if (string.Equals(CategoryCustoms, value, StringComparison.OrdinalIgnoreCase)) return "报关资料";
        if (string.Equals(CategoryCertificate, value, StringComparison.OrdinalIgnoreCase)) return "证件 / 证书";
        if (string.Equals(CategoryTransport, value, StringComparison.OrdinalIgnoreCase)) return "运输单据";
        if (string.Equals(CategoryPhoto, value, StringComparison.OrdinalIgnoreCase)) return "图片 / 影印件";
        if (string.Equals(CategoryOther, value, StringComparison.OrdinalIgnoreCase)) return "其他";
        return value;
    }

    // ==================== 6. 有界元数据校验 ====================

    /// <summary>显示名规范化（必填、有界；拒绝控制字符、&lt; &gt; 标记字符与链接形态）</summary>
    public static string NormalizeDisplayName(string? displayName)
    {
        var value = (displayName ?? string.Empty).Trim();
        if (value.Length == 0)
            throw BusinessException.InvalidParameter("请填写附件显示名（只作为纯文本标签，不代表文件可用）");
        if (value.Length > MaxDisplayNameLength)
            throw BusinessException.InvalidParameter($"附件显示名长度不能超过 {MaxDisplayNameLength} 个字符");

        EnsurePlainText(value, "附件显示名");
        return value;
    }

    /// <summary>
    /// 不透明引用标识规范化（必填、有界）：只接受不透明令牌，拒绝链接 / 路径 / 空白 / 标记字符 / <c>..</c> 穿越。
    /// </summary>
    public static string NormalizeReferenceId(string? referenceId)
    {
        var value = (referenceId ?? string.Empty).Trim();
        if (value.Length == 0)
            throw BusinessException.InvalidParameter(
                "请填写不透明引用标识（只接受字母 / 数字 / 点 / 下划线 / 加号 / 连字符组成的不透明令牌，"
                + "不接受链接、路径或文件内容）");
        if (value.Length > MaxReferenceIdLength)
            throw BusinessException.InvalidParameter($"引用标识长度不能超过 {MaxReferenceIdLength} 个字符");
        if (!IsOpaqueToken(value))
            throw BusinessException.InvalidParameter(
                $"引用标识「{Truncate(value)}」不是合法的不透明标识：只接受字母 / 数字 / 点 / 下划线 / 加号 / 连字符"
                + "（首字符必须是字母或数字，且不得包含连续的「..」）；链接（:// 、data: 、javascript: 等）、"
                + "路径、反斜杠、空白、百分号编码与 HTML / 脚本标记字符一律拒绝");

        return value;
    }

    /// <summary>内容类型规范化（可选；只接受 <c>type/subtype</c> 形态，不接受参数、标记与自由文本）</summary>
    public static string NormalizeContentType(string? contentType)
    {
        var value = (contentType ?? string.Empty).Trim();
        if (value.Length == 0) return string.Empty;
        if (value.Length > MaxContentTypeLength)
            throw BusinessException.InvalidParameter($"内容类型长度不能超过 {MaxContentTypeLength} 个字符");
        if (!IsMediaType(value))
            throw BusinessException.InvalidParameter(
                $"内容类型「{Truncate(value)}」不合法：只接受 type/subtype 形态（例如 application/pdf），"
                + "不接受参数、链接与 HTML / 脚本标记");

        return value;
    }

    /// <summary>字节大小校验（0 = 未提供 / 未知；负数与超上限一律拒绝）</summary>
    public static long NormalizeSizeBytes(long? sizeBytes)
    {
        var value = sizeBytes ?? 0;
        if (value < 0)
            throw BusinessException.InvalidParameter("字节大小不能为负数（0 表示未提供 / 未知）");
        if (value > MaxSizeBytes)
            throw BusinessException.InvalidParameter(
                $"字节大小超出上限（最大 {MaxSizeBytes} 字节 = 10 GiB；0 表示未提供 / 未知）");

        return value;
    }

    /// <summary>校验和规范化（可选；只接受 16~128 位十六进制摘要，不接受任意文本）</summary>
    public static string NormalizeChecksum(string? checksum)
    {
        var value = (checksum ?? string.Empty).Trim();
        if (value.Length == 0) return string.Empty;
        if (value.Length > MaxChecksumLength)
            throw BusinessException.InvalidParameter($"校验和长度不能超过 {MaxChecksumLength} 个字符");
        if (!IsHexDigest(value))
            throw BusinessException.InvalidParameter(
                $"校验和「{Truncate(value)}」不合法：只接受 {MinChecksumLength}~{MaxChecksumLength} 位十六进制摘要"
                + "（不接受链接、自由文本或 HTML / 脚本标记）");

        return value;
    }

    /// <summary>备注规范化（可选、有界；拒绝控制字符）</summary>
    public static string NormalizeNotes(string? notes)
    {
        var value = (notes ?? string.Empty).Trim();
        if (value.Length > MaxNotesLength)
            throw BusinessException.InvalidParameter($"备注长度不能超过 {MaxNotesLength} 个字符");

        EnsureNoControlCharacters(value, "备注");
        return value;
    }

    /// <summary>确认人 / 确认来源规范化（可选、有界；拒绝控制字符）</summary>
    public static string NormalizeAuthorizedBy(string? authorizedBy)
    {
        var value = (authorizedBy ?? string.Empty).Trim();
        if (value.Length > MaxAuthorizedByLength)
            throw BusinessException.InvalidParameter($"确认人 / 确认来源长度不能超过 {MaxAuthorizedByLength} 个字符");

        EnsureNoControlCharacters(value, "确认人 / 确认来源");
        return value;
    }

    /// <summary>
    /// 来源授权确认校验（创建引用**必须**显式确认为真并填写确认说明）：
    /// 返回规范化后的确认说明；缺少确认或说明一律拒绝。
    /// </summary>
    public static string EnsureSourceAuthorized(bool acknowledged, string? note)
    {
        if (!acknowledged)
            throw BusinessException.InvalidParameter(
                "登记附件引用必须显式确认来源授权（SourceAuthorizationAcknowledged = true）："
                + "确认只表示登记人声明其有权引用该来源，不授予系统存储访问权");

        var value = (note ?? string.Empty).Trim();
        if (value.Length == 0)
            throw BusinessException.InvalidParameter("请填写来源授权确认说明（授权依据 / 范围），与确认标记一起落库留痕");
        if (value.Length > MaxAuthorizationNoteLength)
            throw BusinessException.InvalidParameter($"来源授权确认说明长度不能超过 {MaxAuthorizationNoteLength} 个字符");

        EnsurePlainText(value, "来源授权确认说明");
        return value;
    }

    /// <summary>作废原因规范化（必填、有界；拒绝控制字符）</summary>
    public static string NormalizeVoidReason(string? reason)
    {
        var value = (reason ?? string.Empty).Trim();
        if (value.Length == 0)
            throw BusinessException.InvalidParameter("请填写作废原因（作废保留原始元数据，必须记录更正原因）");
        if (value.Length > MaxVoidReasonLength)
            throw BusinessException.InvalidParameter($"作废原因长度不能超过 {MaxVoidReasonLength} 个字符");

        EnsureNoControlCharacters(value, "作废原因");
        return value;
    }

    /// <summary>父单据号码快照校验（服务端权威写入；有界且不得包含控制字符）</summary>
    public static string NormalizeParentNo(string? parentNo)
    {
        var value = (parentNo ?? string.Empty).Trim();
        if (value.Length > MaxParentNoLength)
            throw BusinessException.InvalidParameter(
                $"父单据号码长度不能超过 {MaxParentNoLength} 个字符（无法形成有界快照）");

        EnsureNoControlCharacters(value, "父单据号码");
        return value;
    }

    // ==================== 7. 不透明引用判定（读取侧安全标注复用） ====================

    /// <summary>
    /// 判断引用标识是否符合**不透明标识**口径（不抛异常，供读取侧标注历史 / 外部写入值）。
    /// 不满足时界面只显示不可用文本，绝不把原值当链接或标记渲染。
    /// </summary>
    public static bool IsSafeReferenceId(string? referenceId)
    {
        var value = (referenceId ?? string.Empty).Trim();
        return value.Length is > 0 and <= MaxReferenceIdLength && IsOpaqueToken(value);
    }

    /// <summary>引用标识不可用时的说明文案（安全值返回空串，界面据此只显示文本提示）</summary>
    public static string ReferenceUnavailableText(string? referenceId)
    {
        if (IsSafeReferenceId(referenceId)) return string.Empty;
        return "引用不可用：登记值不符合不透明标识口径（可能包含链接、路径穿越、空白或 HTML / 脚本标记），"
            + "只作为文本提示显示，系统不会访问该引用。";
    }

    // ==================== 8. 状态机 ====================

    /// <summary>状态文案（未知状态返回「未知状态」而不是猜测）</summary>
    public static string StatusText(int status) => status switch
    {
        StatusActive => "有效",
        StatusVoided => "已作废",
        _ => "未知状态"
    };

    /// <summary>附件引用是否可作废（已作废不可重复作废）</summary>
    public static void EnsureVoidable(int status, string label)
    {
        if (status == StatusVoided)
            throw BusinessException.RuleConflict(
                $"附件引用「{label}」已作废，不能重复作废（历史与原始元数据保留可读）");
    }

    // ==================== 9. 显示文案 ====================

    /// <summary>字节大小文案（0 显式说明「未提供 / 未知」，不猜测为 0 字节文件）</summary>
    public static string SizeText(long sizeBytes)
    {
        if (sizeBytes <= 0) return "未提供大小（未知）";
        if (sizeBytes < 1024) return $"{sizeBytes} B";
        if (sizeBytes < 1024L * 1024) return $"{sizeBytes / 1024d:0.##} KB";
        if (sizeBytes < 1024L * 1024 * 1024) return $"{sizeBytes / (1024d * 1024):0.##} MB";
        return $"{sizeBytes / (1024d * 1024 * 1024):0.##} GB";
    }

    /// <summary>父单据快照文案（类型文案 + 号码；缺一即照实说明，不猜测父单据）</summary>
    public static string ParentSnapshotText(string? parentTypeText, string? parentNo)
    {
        var typeText = (parentTypeText ?? string.Empty).Trim();
        var no = (parentNo ?? string.Empty).Trim();
        if (typeText.Length == 0 && no.Length == 0) return "父单据快照缺失";
        if (no.Length == 0) return $"{typeText}（号码缺失，历史快照照实显示）";
        return typeText.Length == 0 ? no : $"{typeText} {no}";
    }

    /// <summary>父单据可用性文案（不存在 / 已删除时照实说明，历史引用照常可读）</summary>
    public static string ParentAvailabilityText(bool available)
        => available ? "父单据可用" : "父单据已不存在或已删除（历史引用元数据仍可读，不能用于新登记）";

    /// <summary>引用条目标签文案（固定声明这是元数据引用，不是文件可用的证明）</summary>
    public static string ReferenceLabelText(bool referenceAvailable)
        => referenceAvailable ? "元数据引用（不代表文件可用）" : "元数据引用不可用（不透明标识口径不满足）";

    // ==================== 10. 内部工具 ====================

    /// <summary>不透明令牌判定：字母 / 数字 / 点 / 下划线 / 加号 / 连字符，首字符为字母或数字，无连续「..」</summary>
    private static bool IsOpaqueToken(string value)
    {
        if (value.Length == 0) return false;
        if (!char.IsAsciiLetterOrDigit(value[0])) return false;
        if (value.Contains("..", StringComparison.Ordinal)) return false;

        foreach (var ch in value)
        {
            if (char.IsAsciiLetterOrDigit(ch)) continue;
            if (ch is '.' or '_' or '+' or '-') continue;
            return false;
        }

        return true;
    }

    /// <summary>媒体类型判定：恰好一段 <c>type/subtype</c>，两段均为字母 / 数字 / 点 / 加号 / 连字符 / 下划线</summary>
    private static bool IsMediaType(string value)
    {
        var parts = value.Split('/');
        if (parts.Length != 2) return false;
        foreach (var part in parts)
        {
            if (part.Length == 0) return false;
            foreach (var ch in part)
            {
                if (char.IsAsciiLetterOrDigit(ch)) continue;
                if (ch is '.' or '+' or '-' or '_') continue;
                return false;
            }
        }

        return true;
    }

    /// <summary>十六进制摘要判定（16~128 位 [0-9a-fA-F]）</summary>
    private static bool IsHexDigest(string value)
    {
        if (value.Length is < MinChecksumLength or > MaxChecksumLength) return false;
        foreach (var ch in value)
        {
            if (!char.IsAsciiHexDigit(ch)) return false;
        }

        return true;
    }

    /// <summary>纯文本校验：拒绝控制字符、HTML 标记字符与链接 / 脚本 / 数据方案形态</summary>
    private static void EnsurePlainText(string value, string fieldName)
    {
        EnsureNoControlCharacters(value, fieldName);

        if (value.Contains('<', StringComparison.Ordinal) || value.Contains('>', StringComparison.Ordinal))
            throw BusinessException.InvalidParameter($"{fieldName}不能包含 HTML / 脚本标记字符（< 或 >）");

        if (value.Contains("://", StringComparison.Ordinal)
            || value.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("vbscript:", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            throw BusinessException.InvalidParameter(
                $"{fieldName}不能是链接或脚本 / 数据方案（系统只登记不透明标识与纯文本标签）");
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

    /// <summary>截断显示（错误提示中有界回显，避免把超长 / 不安全原值整段抛出）</summary>
    private static string Truncate(string value, int max = 60)
        => value.Length <= max ? value : value[..max] + "…";
}
