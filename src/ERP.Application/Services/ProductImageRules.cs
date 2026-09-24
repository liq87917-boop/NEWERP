namespace ERP.Application.Services;

/// <summary>
/// 商品图片库的纯规则（ERP-039，无数据库依赖，便于逐条单测）：
/// 把商品资料已持久化的图片位引用（<c>Image1</c> / <c>Image2</c> / <c>Image3</c>）分类为
/// 「未维护 / 本站相对路径 / HTTP(S) 绝对地址 / 不安全协议 / 可疑标记 / 无法安全渲染 / 超出字段长度」，
/// 并给出填充状态（无图片 / 部分填充 / 全部填充）、筛选键与前后端共用的展示文案。
/// <para>边界：本规则只判断「一个已持久化的引用能否当作图片地址渲染」——
/// 不探测对象是否真实存在、不访问 OSS、不读取任何存储凭据、不推断对象归属与访问授权，
/// 也不上传 / 覆盖 / 删除对象或改写商品图片字段。</para>
/// </summary>
public static class ProductImageRules
{
    /// <summary>商品资料上既有的图片位数量（图片 1 / 2 / 3），本库只读这三列。</summary>
    public const int SlotCount = 3;

    /// <summary>引用最大长度（与 <c>BaseProduct.Image1~3</c> 的 NVARCHAR(500) 一致）：超长的历史值一律不渲染。</summary>
    public const int MaxReferenceLength = 500;

    /// <summary>启用状态（商品 <c>Status = 1</c>）</summary>
    public const int ActiveStatus = 1;

    /// <summary>停用状态（商品 <c>Status = 0</c>）</summary>
    public const int DisabledStatus = 0;

    // ==================== 引用状态键（后端返回、前端展示、测试断言共用） ====================

    /// <summary>图片位未维护（持久化为空串 / NULL）</summary>
    public const string RefEmpty = "empty";

    /// <summary>本站相对路径（以单个 <c>/</c> 开头），例如 <c>/oss/NEWERP/20260925/xxx.png</c></summary>
    public const string RefLocal = "local";

    /// <summary>HTTP(S) 绝对地址（协议 + 主机均合法），例如 OSS 外链</summary>
    public const string RefHttp = "http";

    /// <summary>不安全协议（javascript / data / file / vbscript / 本地盘符等），绝不渲染</summary>
    public const string RefUnsafeScheme = "unsafe_scheme";

    /// <summary>含可疑标记或控制字符（<c>&lt; &gt; " ' ` \</c> 与换行等），绝不渲染</summary>
    public const string RefUnsafeMarkup = "unsafe_markup";

    /// <summary>无法安全渲染的地址（协议相对 <c>//host</c>、缺少前导斜杠的相对路径、含空白、含 <c>..</c>、非法 http 地址等）</summary>
    public const string RefUnsupported = "unsupported";

    /// <summary>超过字段长度的历史值（&gt; <see cref="MaxReferenceLength"/> 字符），不渲染</summary>
    public const string RefTooLong = "too_long";

    /// <summary>全部引用状态键（顺序 = 展示与断言顺序）</summary>
    public static readonly IReadOnlyList<string> ReferenceStateKeys =
        new[] { RefEmpty, RefLocal, RefHttp, RefUnsafeScheme, RefUnsafeMarkup, RefUnsupported, RefTooLong };
    // ==================== 填充状态键 ====================

    /// <summary>三个图片位都没有引用</summary>
    public const string FillNone = "none";

    /// <summary>三个图片位有一到两个引用（部分填充）</summary>
    public const string FillPartial = "partial";

    /// <summary>三个图片位都有引用</summary>
    public const string FillFull = "full";

    // ==================== 筛选项 ====================

    /// <summary>不按图片填充状态筛选（默认）</summary>
    public const string FilterAll = "all";

    /// <summary>至少一个图片位有引用（全部填充 + 部分填充）</summary>
    public const string FilterHas = "has";

    /// <summary>三个图片位都已填充</summary>
    public const string FilterFull = "full";

    /// <summary>部分填充（一到两个图片位有引用）</summary>
    public const string FilterPartial = "partial";

    /// <summary>三个图片位都没有引用</summary>
    public const string FilterNone = "none";

    /// <summary>全部图片填充状态筛选键（顺序 = 界面下拉顺序）</summary>
    public static readonly IReadOnlyList<string> FilterKeys =
        new[] { FilterAll, FilterHas, FilterFull, FilterPartial, FilterNone };

    /// <summary>可渲染的引用状态（仅这两种：本站相对路径与 HTTP(S) 绝对地址）</summary>
    public static readonly IReadOnlyList<string> RenderableStateKeys = new[] { RefLocal, RefHttp };

    /// <summary>标记 / 引号 / 反斜杠等会破坏 HTML 属性或表示标记的字符：命中即不渲染，只作不可用文本</summary>
    private static readonly char[] MarkupChars = { '<', '>', '"', '\'', '`', '\\' };

    /// <summary>去掉首尾空白后的引用（仅用于判定；持久化原值原样返回给界面展示）</summary>
    public static string NormalizeReference(string? raw) => raw?.Trim() ?? string.Empty;
    /// <summary>
    /// 引用安全分类（服务端权威口径，客户端提交值一律不被采信）：
    /// <list type="number">
    /// <item>空 / 空白 → <see cref="RefEmpty"/>；</item>
    /// <item>含控制字符或标记字符（<c>&lt; &gt; " ' ` \</c>）→ <see cref="RefUnsafeMarkup"/>；</item>
    /// <item>含空白 → <see cref="RefUnsupported"/>；</item>
    /// <item>超长 → <see cref="RefTooLong"/>；</item>
    /// <item>有协议前缀：只允许 http / https 且主机非空（否则 <see cref="RefUnsafeScheme"/> / <see cref="RefUnsupported"/>）；</item>
    /// <item>无协议：只允许以单个 <c>/</c> 开头的本站相对路径且不含 <c>..</c>（否则 <see cref="RefUnsupported"/>）。</item>
    /// </list>
    /// </summary>
    public static string ClassifyReference(string? raw)
    {
        var value = NormalizeReference(raw);
        if (value.Length == 0) return RefEmpty;
        if (value.Length > MaxReferenceLength) return RefTooLong;

        // 控制字符（换行 / 制表 / NUL 等）与标记字符：可能破坏属性或试图注入标记，一律不渲染
        foreach (var ch in value)
        {
            if (char.IsControl(ch)) return RefUnsafeMarkup;
            if (Array.IndexOf(MarkupChars, ch) >= 0) return RefUnsafeMarkup;
        }

        // 地址里的空白（含全角空格）不是可安全渲染的引用形态
        foreach (var ch in value)
        {
            if (char.IsWhiteSpace(ch)) return RefUnsupported;
        }

        var colon = value.IndexOf(':');
        var firstSlash = value.IndexOf('/');
        if (colon >= 0 && (firstSlash < 0 || colon < firstSlash))
        {
            // 有协议前缀：只认 http / https（javascript / data / file / vbscript / 盘符等一律拒绝）
            var scheme = value[..colon].ToLowerInvariant();
            if (scheme is not ("http" or "https")) return RefUnsafeScheme;
            return IsAbsoluteHttpUrl(value) ? RefHttp : RefUnsupported;
        }

        // 无协议：只接受本站相对路径 /xxx；//host 是协议相对地址（可指向任意主机），不接受
        if (!value.StartsWith('/') || value.StartsWith("//", StringComparison.Ordinal)) return RefUnsupported;
        if (value.Length < 2) return RefUnsupported;
        if (value.Contains("..", StringComparison.Ordinal)) return RefUnsupported;
        return RefLocal;
    }

    /// <summary>该引用状态是否可作为图片地址渲染（只有本站相对路径与 HTTP(S) 绝对地址）</summary>
    public static bool IsRenderable(string? state) => state is RefLocal or RefHttp;

    /// <summary>是否为合法的 http / https 绝对地址（协议 + 主机都必须存在）</summary>
    private static bool IsAbsoluteHttpUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
        && !string.IsNullOrEmpty(uri.Host);
    /// <summary>引用状态文案（后端返回、前端展示、测试断言共用同一份文案）</summary>
    public static string ReferenceStateText(string? state) => state switch
    {
        RefLocal => "本站相对路径（可渲染）",
        RefHttp => "HTTP(S) 绝对地址（可渲染）",
        RefUnsafeScheme => "不可用（不安全的协议）",
        RefUnsafeMarkup => "不可用（可疑标记或控制字符）",
        RefUnsupported => "不可用（无法安全渲染的地址）",
        RefTooLong => "不可用（超出字段长度）",
        _ => "未维护图片"
    };

    /// <summary>不可渲染时的占位文案（未维护与不可用必须能区分出来）</summary>
    public static string PlaceholderText(string? state) => state switch
    {
        RefEmpty => "未维护图片",
        _ => "图片引用不可用"
    };

    /// <summary>图片位标签（图片 1 / 2 / 3）</summary>
    public static string SlotLabel(int slot) => $"图片 {slot}";

    /// <summary>填充状态判定：0 个引用 = 无图片，3 个引用 = 全部填充，其余 = 部分填充</summary>
    public static string FillStateOf(int populatedSlots) =>
        populatedSlots <= 0 ? FillNone : populatedSlots >= SlotCount ? FillFull : FillPartial;

    /// <summary>填充状态文案（无图片 / 部分填充 / 全部填充）</summary>
    public static string FillStateText(string? state) => state switch
    {
        FillFull => "全部填充",
        FillPartial => "部分填充",
        _ => "无图片"
    };

    /// <summary>是否为已知的图片填充状态筛选键（未知取值一律按「全部」处理，只做有界钳制、不报错）</summary>
    public static bool IsKnownFilter(string? key) =>
        key is not null && FilterKeys.Contains(key, StringComparer.Ordinal);

    /// <summary>筛选键文案（界面下拉与回显共用）</summary>
    public static string FilterText(string? key) => key switch
    {
        FilterHas => "有图片引用（全部填充 + 部分填充）",
        FilterFull => "三个图片位都已填充",
        FilterPartial => "部分填充（1~2 个图片位）",
        FilterNone => "无图片引用",
        _ => "全部商品（不限图片填充）"
    };

    /// <summary>商品状态文案（1=启用 / 其余按停用展示）</summary>
    public static string StatusText(int status) => status == ActiveStatus ? "启用" : "停用";

    /// <summary>渲染安全口径（后端返回、前端展示、测试断言共用同一份文案）</summary>
    public const string RenderRuleText =
        "渲染口径：只把两种已持久化的引用当作图片地址渲染——(1) 以单个「/」开头的本站相对路径；" +
        "(2) http / https 绝对地址（主机非空）。" +
        "其他协议（javascript / data / file / vbscript / 本地盘符等）、协议相对地址（//host）、" +
        "含标记或控制字符（< > \" ' ` \\ 与换行）的引用、含空白或「..」的地址以及超出 500 字符的历史值" +
        "一律不渲染，只在界面上作为不可用文本原样显示，既不执行也不做任何服务端抓取。";

    /// <summary>只读口径（不上传 / 不删除 / 不读凭据 / 不改写图片字段）</summary>
    public const string ReadOnlyRuleText =
        "只读口径：本库只读取商品资料已持久化的图片位 1~3，不新增或改写任何数据——" +
        "不上传 / 覆盖 / 删除 OSS 对象、不引入或读取任何存储凭据、不请求任何图片地址（不做服务端抓取）、" +
        "不改写商品图片字段，也不推断对象归属或访问授权；" +
        "引用对象缺失 / 已删除（404）由浏览器加载失败时显示占位提示，服务端不探测对象是否存在。";

    /// <summary>范围说明：合计与计数只统计本次返回页（分页有界，不做无界全量统计）</summary>
    public const string PageScopeText =
        "以下合计与计数只统计本次返回页；total 为符合筛选条件的商品总数。";
}
