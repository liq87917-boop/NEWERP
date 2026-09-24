using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace ERP.Application.Services;

/// <summary>
/// 商品图片库服务（ERP-039，<b>只读</b>）：在既有商品资料的三个图片位（<c>Image1</c> / <c>Image2</c> / <c>Image3</c>）之上
/// 提供可分页、可筛选的浏览视图，并逐位标注引用是否可安全渲染。
/// <para>查询约束：基础行在数据库内排序 + 分页（有界），整次调用只访问商品资料数据集一次（计数 + 本页各一次查询），
/// 图片位分类完全是内存中的纯规则判定，因此不存在逐行查库。</para>
/// <para>边界（重要）：本服务不写任何数据 —— 不上传 / 覆盖 / 删除 OSS 对象、不引入或读取任何存储凭据、
/// 不请求任何图片地址（不做服务端抓取）、不改写商品图片字段，也不推断对象归属或访问授权；
/// 只读取已持久化的引用，缺失 / 已删除对象由浏览器加载失败时的占位提示呈现。</para>
/// </summary>
public static class ProductImageLibraryService
{
    /// <summary>
    /// 查询商品图片库（只读、分页有界）。整次调用只读取商品资料数据集：
    /// 一次计数 + 一次本页投影查询，图片位分类在内存完成，不产生逐行查询、也不产生任何写操作。
    /// </summary>
    public static async Task<ProductImageLibraryPage> QueryAsync(
        IErpDbContext db, ProductImageLibraryQuery? query = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);

        var request = query ?? new ProductImageLibraryQuery();
        request.Normalize();

        // 1) 基础行：商品资料（软删除行一律排除）+ 只读筛选；排序与分页全部在数据库内完成（有界）
        var products = db.BaseProducts.AsNoTracking().Where(p => !p.IsDeleted);
        if (request.ProductId.HasValue) products = products.Where(p => p.Id == request.ProductId.Value);
        if (request.Status.HasValue) products = products.Where(p => p.Status == request.Status.Value);
        if (request.Keyword is not null)
        {
            var keyword = request.Keyword;
            products = products.Where(p => p.ProductCode.Contains(keyword) || p.ProductName.Contains(keyword));
        }
        products = ApplyImageStateFilter(products, request.ImageState!);

        var total = await products.CountAsync(cancellationToken);
        var rows = await products
            .OrderBy(p => p.ProductCode).ThenBy(p => p.Id)
            .Skip((request.Page - 1) * request.PageSize)
            .Take(request.PageSize)
            .Select(p => new
            {
                p.Id,
                p.ProductCode,
                p.ProductName,
                p.Spec,
                p.Category,
                p.Unit,
                p.Status,
                p.Image1,
                p.Image2,
                p.Image3
            })
            .ToListAsync(cancellationToken);

        // 2) 图片位分类：纯规则 + 内存映射（不逐行查库、不访问 OSS、不请求任何地址）
        var items = rows
            .Select(r => BuildItem(r.Id, r.ProductCode, r.ProductName, r.Spec, r.Category, r.Unit, r.Status,
                r.Image1, r.Image2, r.Image3))
            .ToList();

        // 3) 本页计数与口径回显（合计只统计本页，不做无界全量统计）
        return new ProductImageLibraryPage
        {
            Keyword = request.Keyword ?? string.Empty,
            ProductId = request.ProductId,
            Status = request.Status,
            ImageState = request.ImageState!,
            ImageStateText = ProductImageRules.FilterText(request.ImageState),
            Total = total,
            Page = request.Page,
            PageSize = request.PageSize,
            TotalPages = (int)Math.Ceiling(total / (double)request.PageSize),
            FullFillCount = items.Count(i => i.FillState == ProductImageRules.FillFull),
            PartialFillCount = items.Count(i => i.FillState == ProductImageRules.FillPartial),
            NoImageCount = items.Count(i => i.FillState == ProductImageRules.FillNone),
            UnusableProductCount = items.Count(i => i.UnusableReferenceCount > 0),
            PopulatedSlotCount = items.Sum(i => i.PopulatedSlotCount),
            RenderableSlotCount = items.Sum(i => i.RenderableSlotCount),
            UnusableReferenceCount = items.Sum(i => i.UnusableReferenceCount),
            Items = items
        };
    }
    /// <summary>
    /// 图片填充状态筛选（全部在数据库内完成，仍是单条 SQL）：
    /// <c>has</c> = 至少一个图片位有引用；<c>full</c> = 三个图片位都有引用；
    /// <c>partial</c> = 有引用且至少一个图片位为空（即 1~2 个）；<c>none</c> = 三个图片位都为空。
    /// <para>判定一律按「是否有持久化引用」进行，与引用是否可渲染无关（不可渲染的引用在行内标注）；
    /// NULL 与空串对历史数据同义，因此统一使用 <c>string.IsNullOrEmpty</c> 语义。</para>
    /// </summary>
    private static IQueryable<BaseProduct> ApplyImageStateFilter(IQueryable<BaseProduct> source, string imageState)
        => imageState switch
        {
            ProductImageRules.FilterHas => source.Where(p =>
                !string.IsNullOrEmpty(p.Image1) || !string.IsNullOrEmpty(p.Image2) || !string.IsNullOrEmpty(p.Image3)),

            ProductImageRules.FilterFull => source.Where(p =>
                !string.IsNullOrEmpty(p.Image1) && !string.IsNullOrEmpty(p.Image2) && !string.IsNullOrEmpty(p.Image3)),

            ProductImageRules.FilterPartial => source.Where(p =>
                (!string.IsNullOrEmpty(p.Image1) || !string.IsNullOrEmpty(p.Image2) || !string.IsNullOrEmpty(p.Image3))
                && (string.IsNullOrEmpty(p.Image1) || string.IsNullOrEmpty(p.Image2) || string.IsNullOrEmpty(p.Image3))),

            ProductImageRules.FilterNone => source.Where(p =>
                string.IsNullOrEmpty(p.Image1) && string.IsNullOrEmpty(p.Image2) && string.IsNullOrEmpty(p.Image3)),

            _ => source
        };

    /// <summary>
    /// 组装一行：商品身份 + 规格信息 + 三个图片位的分类结果与计数。
    /// <para>引用原值原样返回（不做任何改写），只按纯规则标注状态；不可渲染的引用不给出图片地址。</para>
    /// </summary>
    private static ProductImageLibraryItem BuildItem(
        long productId, string? productCode, string? productName, string? spec, string? category, string? unit,
        int status, string? image1, string? image2, string? image3)
    {
        var references = new[] { image1, image2, image3 };
        var images = new List<ProductImageSlotDto>(ProductImageRules.SlotCount);

        for (var index = 0; index < references.Length; index++)
        {
            var slot = index + 1;
            var raw = references[index] ?? string.Empty;                     // 持久化原值（含空白，原样返回）
            var state = ProductImageRules.ClassifyReference(raw);
            var renderable = ProductImageRules.IsRenderable(state);

            images.Add(new ProductImageSlotDto
            {
                Slot = slot,
                SlotLabel = ProductImageRules.SlotLabel(slot),
                Reference = raw,
                State = state,
                StateText = ProductImageRules.ReferenceStateText(state),
                Renderable = renderable,
                Source = renderable ? ProductImageRules.NormalizeReference(raw) : string.Empty,
                PlaceholderText = ProductImageRules.PlaceholderText(state)
            });
        }

        var populated = images.Count(i => i.State != ProductImageRules.RefEmpty);
        var renderableCount = images.Count(i => i.Renderable);
        var unusable = populated - renderableCount;
        var fillState = ProductImageRules.FillStateOf(populated);

        return new ProductImageLibraryItem
        {
            ProductId = productId,
            ProductCode = productCode ?? string.Empty,
            ProductName = productName ?? string.Empty,
            Spec = spec ?? string.Empty,
            Category = category ?? string.Empty,
            Unit = unit ?? string.Empty,
            Status = status,
            StatusText = ProductImageRules.StatusText(status),
            FillState = fillState,
            FillStateText = ProductImageRules.FillStateText(fillState),
            PopulatedSlotCount = populated,
            RenderableSlotCount = renderableCount,
            UnusableReferenceCount = unusable,
            Note = BuildNote(populated, unusable),
            Images = images
        };
    }

    /// <summary>行说明：优先提示不可用引用（不能渲染、只作文本展示），其次提示未维护图片位。</summary>
    private static string BuildNote(int populatedSlots, int unusableReferences)
    {
        if (unusableReferences > 0)
            return $"存在 {unusableReferences} 个不可用图片引用（不渲染，仅在界面上作文本展示）";
        if (populatedSlots == 0)
            return "未维护图片引用（三个图片位均为空）";
        return string.Empty;
    }
}
