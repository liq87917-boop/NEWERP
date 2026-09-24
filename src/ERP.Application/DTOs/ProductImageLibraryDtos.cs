using ERP.Application.Services;

namespace ERP.Application.DTOs;

/// <summary>
/// 商品图片库的单个图片位（ERP-039）。
/// <para><see cref="Reference"/> 是 <c>BaseProduct.Image1~3</c> 持久化原值（原样返回，仅供文本展示）；
/// <see cref="State"/> 由服务端按 <see cref="ProductImageRules.ClassifyReference"/> 判定，
/// 只有 <c>local</c> / <c>http</c> 两种状态可渲染，其余状态一律不渲染、只作不可用文本。</para>
/// </summary>
public sealed class ProductImageSlotDto
{
    /// <summary>图片位序号（1 / 2 / 3）</summary>
    public int Slot { get; set; }

    /// <summary>图片位标签（图片 1 / 图片 2 / 图片 3）</summary>
    public string SlotLabel { get; set; } = string.Empty;

    /// <summary>持久化原值（未做任何改写；不可用时界面以纯文本展示，绝不当作标记或脚本执行）</summary>
    public string Reference { get; set; } = string.Empty;

    /// <summary>引用状态键（empty / local / http / unsafe_scheme / unsafe_markup / unsupported / too_long）</summary>
    public string State { get; set; } = ProductImageRules.RefEmpty;

    /// <summary>引用状态文案（与 <see cref="ProductImageRules.ReferenceStateText"/> 同源）</summary>
    public string StateText { get; set; } = string.Empty;

    /// <summary>是否可作为图片地址渲染（仅本站相对路径与 HTTP(S) 绝对地址为 true）</summary>
    public bool Renderable { get; set; }

    /// <summary>可渲染时的图片地址（= 持久化原值）；不可渲染时为空串，界面只显示占位文案</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>不可渲染时的占位文案（未维护图片 / 图片引用不可用）</summary>
    public string PlaceholderText { get; set; } = string.Empty;
}

/// <summary>
/// 商品图片库的一行（一个商品及其三个已持久化的图片位）。
/// <para>商品身份与字段完全不变：本库只读取商品资料，不写回、不改写图片位。</para>
/// </summary>
public sealed class ProductImageLibraryItem
{
    /// <summary>商品 Id</summary>
    public long ProductId { get; set; }

    /// <summary>商品编码</summary>
    public string ProductCode { get; set; } = string.Empty;

    /// <summary>商品名称</summary>
    public string ProductName { get; set; } = string.Empty;

    /// <summary>规格型号</summary>
    public string Spec { get; set; } = string.Empty;

    /// <summary>商品分类</summary>
    public string Category { get; set; } = string.Empty;

    /// <summary>计量单位</summary>
    public string Unit { get; set; } = string.Empty;

    /// <summary>商品状态（1=启用 / 0=停用）</summary>
    public int Status { get; set; }

    /// <summary>商品状态文案（启用 / 停用）</summary>
    public string StatusText { get; set; } = string.Empty;

    /// <summary>填充状态键（none 无图片 / partial 部分填充 / full 全部填充）</summary>
    public string FillState { get; set; } = ProductImageRules.FillNone;

    /// <summary>填充状态文案（无图片 / 部分填充 / 全部填充）</summary>
    public string FillStateText { get; set; } = string.Empty;

    /// <summary>有持久化引用的图片位数（0 ~ 3）</summary>
    public int PopulatedSlotCount { get; set; }

    /// <summary>可安全渲染的图片位数（0 ~ 3）</summary>
    public int RenderableSlotCount { get; set; }

    /// <summary>有引用但不可安全渲染的图片位数（不安全协议 / 可疑标记 / 无法安全渲染 / 超长）</summary>
    public int UnusableReferenceCount { get; set; }

    /// <summary>行说明（未维护 / 存在不可用引用时的显式提示；无特殊情况为空串）</summary>
    public string Note { get; set; } = string.Empty;

    /// <summary>三个图片位（固定 3 个，顺序 = 图片 1 / 2 / 3）</summary>
    public List<ProductImageSlotDto> Images { get; set; } = new();
}
/// <summary>商品图片库查询条件（全部为只读筛选参数；不做任何写操作）</summary>
public sealed class ProductImageLibraryQuery
{
    /// <summary>默认每页条数</summary>
    public const int DefaultPageSize = 24;

    /// <summary>每页条数上限（有界：单次请求最多返回这么多行）</summary>
    public const int MaxPageSize = 200;

    /// <summary>关键字（匹配商品资料的编码 / 名称；留空 = 不过滤）</summary>
    public string? Keyword { get; set; }

    /// <summary>商品筛选（留空 = 全部商品）</summary>
    public long? ProductId { get; set; }

    /// <summary>商品状态筛选（1=仅启用 / 0=仅停用；留空或非法值 = 不限）</summary>
    public int? Status { get; set; }

    /// <summary>
    /// 图片填充状态筛选（all / has / full / partial / none；留空或未知取值一律按 all 处理）。
    /// <para>只按「是否有持久化引用」筛选，不按引用是否可渲染筛选：不可渲染的引用数与明细在行内标注。</para>
    /// </summary>
    public string? ImageState { get; set; }

    /// <summary>页码（从 1 开始）</summary>
    public int Page { get; set; } = 1;

    /// <summary>每页条数（1 ~ <see cref="MaxPageSize"/>，超出按上限截断）</summary>
    public int PageSize { get; set; } = DefaultPageSize;

    /// <summary>
    /// 归一化（有界钳制，不臆造取值也不报错）：页码 &lt; 1 按 1、每页条数超出上限按上限、
    /// 关键字去首尾空白后为空按「不过滤」、非 1 / 0 的状态按「不限」、未知填充状态按 all。
    /// </summary>
    public void Normalize()
    {
        if (Page < 1) Page = 1;
        if (PageSize < 1) PageSize = DefaultPageSize;
        if (PageSize > MaxPageSize) PageSize = MaxPageSize;

        if (ProductId is <= 0) ProductId = null;
        if (Status.HasValue
            && Status.Value != ProductImageRules.ActiveStatus
            && Status.Value != ProductImageRules.DisabledStatus) Status = null;

        if (Keyword is not null)
        {
            Keyword = Keyword.Trim();
            if (Keyword.Length == 0) Keyword = null;
        }

        var imageState = ImageState?.Trim().ToLowerInvariant();
        ImageState = ProductImageRules.IsKnownFilter(imageState) ? imageState : ProductImageRules.FilterAll;
    }
}
/// <summary>
/// 商品图片库结果（只读派生）：本页三个图片位的明细 + 本页计数 + 口径文案。
/// <para>不落库、不新增或修改任何表列、不请求任何图片地址、不读取任何存储凭据。</para>
/// </summary>
public sealed class ProductImageLibraryPage
{
    /// <summary>关键字筛选回显（未过滤时为空串）</summary>
    public string Keyword { get; set; } = string.Empty;

    /// <summary>商品筛选回显（未过滤时为 null）</summary>
    public long? ProductId { get; set; }

    /// <summary>状态筛选回显（未过滤时为 null）</summary>
    public int? Status { get; set; }

    /// <summary>图片填充状态筛选键（已归一化：all / has / full / partial / none）</summary>
    public string ImageState { get; set; } = ProductImageRules.FilterAll;

    /// <summary>图片填充状态筛选文案（与 <see cref="ProductImageRules.FilterText"/> 同源）</summary>
    public string ImageStateText { get; set; } = string.Empty;

    /// <summary>符合筛选条件的商品总数</summary>
    public int Total { get; set; }

    /// <summary>当前页码</summary>
    public int Page { get; set; }

    /// <summary>每页条数（已按上限截断）</summary>
    public int PageSize { get; set; }

    /// <summary>总页数</summary>
    public int TotalPages { get; set; }

    /// <summary>本页三个图片位都已填充的商品数</summary>
    public int FullFillCount { get; set; }

    /// <summary>本页部分填充（1~2 个图片位）的商品数</summary>
    public int PartialFillCount { get; set; }

    /// <summary>本页三个图片位都没有引用的商品数</summary>
    public int NoImageCount { get; set; }

    /// <summary>本页存在不可安全渲染引用的商品数</summary>
    public int UnusableProductCount { get; set; }

    /// <summary>本页有持久化引用的图片位总数</summary>
    public int PopulatedSlotCount { get; set; }

    /// <summary>本页可安全渲染的图片位总数</summary>
    public int RenderableSlotCount { get; set; }

    /// <summary>本页有引用但不可安全渲染的图片位总数</summary>
    public int UnusableReferenceCount { get; set; }

    /// <summary>渲染安全口径说明（与后端判定同源）</summary>
    public string Rule { get; set; } = ProductImageRules.RenderRuleText;

    /// <summary>只读口径说明（不上传 / 不删除 / 不读凭据 / 不改写图片字段）</summary>
    public string ReadOnlyRule { get; set; } = ProductImageRules.ReadOnlyRuleText;

    /// <summary>范围说明（合计与计数只统计本页）</summary>
    public string ScopeNote { get; set; } = ProductImageRules.PageScopeText;

    /// <summary>本页商品行（按商品编码 + Id 排序，与分页顺序一致）</summary>
    public List<ProductImageLibraryItem> Items { get; set; } = new();
}
