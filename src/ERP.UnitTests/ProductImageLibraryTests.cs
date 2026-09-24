using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Reflection;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// 商品图片库（只读，图片位 1~3）单元测试（ERP-039）。覆盖：
/// 引用安全分类（本站相对路径 / HTTP(S) / 不安全协议 / 可疑标记 / 无法安全渲染 / 超长）、
/// 全部填充 / 部分填充 / 无图片 / 坏引用 / 不可用引用的区分与文案、按编码或名称的关键字筛选、
/// 图片填充状态筛选与状态 / 商品筛选、软删除排除、分页有界与排序、有界数据集访问（无逐行查库）、
/// 「只读不写库、不改写商品图片字段与其它任何状态」的边界、只读端点契约（仅 GET、不依赖存储服务）与前端接线。
/// <para>说明：全部使用内存数据库（TestDbFactory），不连接 SQL Server、不启动 API、不访问 OSS、
/// 不执行任何 SQL / 部署脚本、不做任何浏览器验收。</para>
/// </summary>
public class ProductImageLibraryTests
{
    private const string LocalReference = "/oss/NEWERP/20260925/p001-main.png";
    private const string HttpReference = "https://bucket.oss-cn-hangzhou.aliyuncs.com/oss/NEWERP/p001-side.png";

    // ==================== 0. 测试脚手架 ====================

    private static ProductImageLibraryController Controller(IErpDbContext db) => new(db);

    private static Task<ProductImageLibraryPage> QueryAsync(
        ErpDbContext db, string? keyword = null, long? productId = null, int? status = null,
        string? imageState = null, int page = 1, int pageSize = ProductImageLibraryQuery.DefaultPageSize)
        => ProductImageLibraryService.QueryAsync(db, new ProductImageLibraryQuery
        {
            Keyword = keyword,
            ProductId = productId,
            Status = status,
            ImageState = imageState,
            Page = page,
            PageSize = pageSize
        });

    /// <summary>写入一个商品（图片位按需给定；null / 未给定 = 空串，等价于历史数据里的 NULL）</summary>
    private static BaseProduct SeedProduct(
        ErpDbContext db, string code, string name, string spec = "标准",
        string? image1 = null, string? image2 = null, string? image3 = null,
        int status = 1, bool deleted = false)
    {
        var product = new BaseProduct
        {
            ProductCode = code,
            ProductName = name,
            Spec = spec,
            Image1 = image1 ?? string.Empty,
            Image2 = image2 ?? string.Empty,
            Image3 = image3 ?? string.Empty,
            Status = status,
            IsDeleted = deleted
        };
        db.BaseProducts.Add(product);
        return product;
    }

    private static ProductImageLibraryItem Row(ProductImageLibraryPage page, string productCode)
        => page.Items.Single(i => i.ProductCode == productCode);

    /// <summary>按仓库根目录拼接文件的绝对路径（与其它契约测试口径一致）</summary>
    private static string RepoFile(params string[] segments)
        => Path.GetFullPath(Path.Combine(
            new[] { AppContext.BaseDirectory, "..", "..", "..", "..", ".." }.Concat(segments).ToArray()));
    // ==================== 1. 引用安全分类（纯规则） ====================

    [Theory]
    [InlineData(null, ProductImageRules.RefEmpty)]
    [InlineData("", ProductImageRules.RefEmpty)]
    [InlineData("   ", ProductImageRules.RefEmpty)]
    [InlineData(LocalReference, ProductImageRules.RefLocal)]
    [InlineData("/uploads/a.jpg", ProductImageRules.RefLocal)]
    [InlineData(HttpReference, ProductImageRules.RefHttp)]
    [InlineData("http://example.com/a.png", ProductImageRules.RefHttp)]
    [InlineData("HTTPS://EXAMPLE.COM/A.PNG", ProductImageRules.RefHttp)]
    [InlineData("javascript:alert(1)", ProductImageRules.RefUnsafeScheme)]
    [InlineData("JaVaScRiPt:alert(1)", ProductImageRules.RefUnsafeScheme)]
    [InlineData("data:image/png;base64,AAAA", ProductImageRules.RefUnsafeScheme)]
    [InlineData("file:///c:/windows/a.png", ProductImageRules.RefUnsafeScheme)]
    [InlineData("vbscript:msgbox(1)", ProductImageRules.RefUnsafeScheme)]
    [InlineData("C:/images/a.png", ProductImageRules.RefUnsafeScheme)]
    [InlineData("//evil.example.com/a.png", ProductImageRules.RefUnsupported)]
    [InlineData("oss/NEWERP/a.png", ProductImageRules.RefUnsupported)]
    [InlineData("../secret.png", ProductImageRules.RefUnsupported)]
    [InlineData("/oss/../secret.png", ProductImageRules.RefUnsupported)]
    [InlineData("http://", ProductImageRules.RefUnsupported)]
    [InlineData("/", ProductImageRules.RefUnsupported)]
    [InlineData("/oss/a b.png", ProductImageRules.RefUnsupported)]
    [InlineData("\"/><script>alert(1)</script>", ProductImageRules.RefUnsafeMarkup)]
    [InlineData("<img src=x onerror=alert(1)>", ProductImageRules.RefUnsafeMarkup)]
    [InlineData("/oss/a'b.png", ProductImageRules.RefUnsafeMarkup)]
    [InlineData("C:\\images\\a.png", ProductImageRules.RefUnsafeMarkup)]
    [InlineData("/oss/a\nb.png", ProductImageRules.RefUnsafeMarkup)]
    public void Rule_classifies_persisted_reference_safely(string? reference, string expected)
    {
        var state = ProductImageRules.ClassifyReference(reference);

        Assert.Equal(expected, state);
        // 只有本站相对路径与 HTTP(S) 绝对地址可渲染，且可渲染判定只认这两个键
        Assert.Equal(state is ProductImageRules.RefLocal or ProductImageRules.RefHttp,
            ProductImageRules.IsRenderable(state));
        Assert.Equal(ProductImageRules.RenderableStateKeys.Contains(state), ProductImageRules.IsRenderable(state));
    }

    [Fact]
    public void Rule_rejects_references_longer_than_the_persisted_column()
    {
        // 恰好等于字段长度且形态合法 → 仍可渲染
        var atLimit = "/" + new string('a', ProductImageRules.MaxReferenceLength - 1);
        Assert.Equal(ProductImageRules.MaxReferenceLength, atLimit.Length);
        Assert.Equal(ProductImageRules.RefLocal, ProductImageRules.ClassifyReference(atLimit));

        // 超出字段长度的历史值 → 不渲染
        var tooLong = "/" + new string('a', ProductImageRules.MaxReferenceLength);
        Assert.True(tooLong.Length > ProductImageRules.MaxReferenceLength);
        Assert.Equal(ProductImageRules.RefTooLong, ProductImageRules.ClassifyReference(tooLong));
        Assert.False(ProductImageRules.IsRenderable(ProductImageRules.ClassifyReference(tooLong)));
    }

    [Fact]
    public void Rule_maps_slot_counts_and_shared_texts()
    {
        // 填充状态：0 = 无图片、1~2 = 部分填充、3 = 全部填充
        Assert.Equal(ProductImageRules.FillNone, ProductImageRules.FillStateOf(0));
        Assert.Equal(ProductImageRules.FillPartial, ProductImageRules.FillStateOf(1));
        Assert.Equal(ProductImageRules.FillPartial, ProductImageRules.FillStateOf(2));
        Assert.Equal(ProductImageRules.FillFull, ProductImageRules.FillStateOf(3));
        Assert.Equal("无图片", ProductImageRules.FillStateText(ProductImageRules.FillStateOf(0)));
        Assert.Equal("部分填充", ProductImageRules.FillStateText(ProductImageRules.FillStateOf(2)));
        Assert.Equal("全部填充", ProductImageRules.FillStateText(ProductImageRules.FillStateOf(3)));

        // 未维护与不可用必须能区分（占位文案与状态文案都要区分）
        Assert.Equal("未维护图片", ProductImageRules.ReferenceStateText(ProductImageRules.RefEmpty));
        Assert.Equal("未维护图片", ProductImageRules.PlaceholderText(ProductImageRules.RefEmpty));
        Assert.Equal("图片引用不可用", ProductImageRules.PlaceholderText(ProductImageRules.RefUnsafeScheme));
        Assert.Equal("不可用（不安全的协议）", ProductImageRules.ReferenceStateText(ProductImageRules.RefUnsafeScheme));
        Assert.Equal("图片 3", ProductImageRules.SlotLabel(3));

        // 筛选键：已知键可识别，未知键一律按「全部」处理（有界钳制、不报错）
        Assert.True(ProductImageRules.IsKnownFilter(ProductImageRules.FilterAll));
        Assert.False(ProductImageRules.IsKnownFilter("unsafe"));
        Assert.False(ProductImageRules.IsKnownFilter(null));

        // 商品状态文案与字段口径一致
        Assert.Equal("启用", ProductImageRules.StatusText(ProductImageRules.ActiveStatus));
        Assert.Equal("停用", ProductImageRules.StatusText(ProductImageRules.DisabledStatus));

        // 口径文案必须显式声明只读边界与安全渲染边界
        Assert.Contains("不上传 / 覆盖 / 删除 OSS 对象", ProductImageRules.ReadOnlyRuleText);
        Assert.Contains("不请求任何图片地址", ProductImageRules.ReadOnlyRuleText);
        Assert.Contains("javascript / data / file / vbscript", ProductImageRules.RenderRuleText);
        Assert.Contains("只统计本次返回页", ProductImageRules.PageScopeText);
    }
    // ==================== 2. 全部填充：三个图片位 + 商品上下文 ====================

    [Fact]
    public async Task Library_returns_three_slots_with_product_context_for_a_fully_populated_product()
    {
        using var db = TestDbFactory.Create();
        SeedProduct(db, "P001", "保温杯", "500ml", LocalReference, HttpReference, LocalReference);
        await db.SaveChangesAsync();

        var page = await QueryAsync(db);

        var row = Assert.Single(page.Items);
        Assert.Equal("P001", row.ProductCode);
        Assert.Equal("保温杯", row.ProductName);
        Assert.Equal("500ml", row.Spec);
        Assert.Equal("启用", row.StatusText);

        Assert.Equal(ProductImageRules.SlotCount, row.Images.Count);
        Assert.Equal(new[] { 1, 2, 3 }, row.Images.Select(s => s.Slot));
        Assert.Equal(new[] { "图片 1", "图片 2", "图片 3" }, row.Images.Select(s => s.SlotLabel));
        Assert.Equal(ProductImageRules.RefLocal, row.Images[0].State);
        Assert.Equal(ProductImageRules.RefHttp, row.Images[1].State);
        Assert.Equal(LocalReference, row.Images[0].Source);
        Assert.Equal(HttpReference, row.Images[1].Source);
        Assert.All(row.Images, slot => Assert.True(slot.Renderable));

        Assert.Equal(ProductImageRules.FillFull, row.FillState);
        Assert.Equal("全部填充", row.FillStateText);
        Assert.Equal(3, row.PopulatedSlotCount);
        Assert.Equal(3, row.RenderableSlotCount);
        Assert.Equal(0, row.UnusableReferenceCount);
        Assert.Equal(string.Empty, row.Note);

        // 本页计数与口径回显
        Assert.Equal(1, page.Total);
        Assert.Equal(1, page.FullFillCount);
        Assert.Equal(0, page.PartialFillCount);
        Assert.Equal(0, page.NoImageCount);
        Assert.Equal(0, page.UnusableProductCount);
        Assert.Equal(3, page.PopulatedSlotCount);
        Assert.Equal(3, page.RenderableSlotCount);
        Assert.Equal(0, page.UnusableReferenceCount);
        Assert.Equal(ProductImageRules.FilterAll, page.ImageState);
        Assert.Equal(ProductImageRules.FilterText(ProductImageRules.FilterAll), page.ImageStateText);
        Assert.Equal(ProductImageRules.RenderRuleText, page.Rule);
        Assert.Equal(ProductImageRules.ReadOnlyRuleText, page.ReadOnlyRule);
        Assert.Equal(ProductImageRules.PageScopeText, page.ScopeNote);
    }

    // ==================== 3. 部分填充 / 无图片的区分 ====================

    [Fact]
    public async Task Library_distinguishes_partial_fill_and_products_without_images()
    {
        using var db = TestDbFactory.Create();
        SeedProduct(db, "P001", "部分商品", "标准", image2: LocalReference);
        SeedProduct(db, "P002", "无图商品");
        await db.SaveChangesAsync();

        var page = await QueryAsync(db);

        var partial = Row(page, "P001");
        Assert.Equal(ProductImageRules.FillPartial, partial.FillState);
        Assert.Equal("部分填充", partial.FillStateText);
        Assert.Equal(1, partial.PopulatedSlotCount);
        Assert.Equal(1, partial.RenderableSlotCount);
        Assert.Equal(ProductImageRules.SlotCount, partial.Images.Count);
        Assert.Equal(ProductImageRules.RefEmpty, partial.Images[0].State);
        Assert.Equal("未维护图片", partial.Images[0].PlaceholderText);
        Assert.Equal(string.Empty, partial.Images[0].Source);          // 未维护不给地址
        Assert.Equal(LocalReference, partial.Images[1].Source);

        var none = Row(page, "P002");
        Assert.Equal(ProductImageRules.FillNone, none.FillState);
        Assert.Equal("无图片", none.FillStateText);
        Assert.Equal(0, none.PopulatedSlotCount);
        Assert.All(none.Images, slot => Assert.Equal(ProductImageRules.RefEmpty, slot.State));
        Assert.Contains("未维护图片引用", none.Note);

        Assert.Equal(1, page.PartialFillCount);
        Assert.Equal(1, page.NoImageCount);
        Assert.Equal(0, page.FullFillCount);
    }

    // ==================== 4. 坏引用 / 不安全引用：不渲染、保留原值文本 ====================

    [Fact]
    public async Task Library_keeps_broken_and_unsafe_references_as_text_without_rendering_them()
    {
        const string unsafeScheme = "javascript:alert(1)";
        const string unsafeMarkup = "\"/><img src=x onerror=alert(1)>";

        using var db = TestDbFactory.Create();
        // 图片 1 = 不安全协议；图片 2 = 形态合法但对象可能缺失（服务端不探测、界面加载失败时占位）；图片 3 = 可疑标记
        SeedProduct(db, "P001", "混合引用", "标准", unsafeScheme, LocalReference, unsafeMarkup);
        await db.SaveChangesAsync();

        var page = await QueryAsync(db);
        var row = Assert.Single(page.Items);

        Assert.Equal(3, row.PopulatedSlotCount);
        Assert.Equal(1, row.RenderableSlotCount);
        Assert.Equal(2, row.UnusableReferenceCount);
        Assert.Equal(ProductImageRules.FillFull, row.FillState);       // 填充状态只按「是否有引用」判定
        Assert.Contains("2 个不可用图片引用", row.Note);

        // 不安全协议：不给图片地址、原值原样返回（界面只作文本展示，绝不执行）
        Assert.Equal(ProductImageRules.RefUnsafeScheme, row.Images[0].State);
        Assert.False(row.Images[0].Renderable);
        Assert.Equal(string.Empty, row.Images[0].Source);
        Assert.Equal(unsafeScheme, row.Images[0].Reference);
        Assert.Equal("图片引用不可用", row.Images[0].PlaceholderText);

        // 形态合法（本站相对路径）：照常给出地址，是否存在由浏览器加载失败占位，服务端不探测
        Assert.Equal(ProductImageRules.RefLocal, row.Images[1].State);
        Assert.True(row.Images[1].Renderable);
        Assert.Equal(LocalReference, row.Images[1].Source);

        // 可疑标记：不渲染、不给地址、原值保留
        Assert.Equal(ProductImageRules.RefUnsafeMarkup, row.Images[2].State);
        Assert.False(row.Images[2].Renderable);
        Assert.Equal(string.Empty, row.Images[2].Source);
        Assert.Equal(unsafeMarkup, row.Images[2].Reference);

        Assert.Equal(1, page.UnusableProductCount);
        Assert.Equal(2, page.UnusableReferenceCount);
        Assert.Equal(1, page.RenderableSlotCount);
    }
    // ==================== 5. 关键字筛选（商品编码 / 名称） ====================

    [Fact]
    public async Task Library_filters_by_product_code_or_name_keyword()
    {
        using var db = TestDbFactory.Create();
        SeedProduct(db, "P001", "保温杯");
        SeedProduct(db, "P002", "玻璃杯");
        SeedProduct(db, "X900", "玩具车");
        await db.SaveChangesAsync();

        var byCode = await QueryAsync(db, keyword: "P00");
        Assert.Equal(2, byCode.Total);
        Assert.Equal("P00", byCode.Keyword);
        Assert.All(byCode.Items, item => Assert.StartsWith("P00", item.ProductCode));

        var byName = await QueryAsync(db, keyword: "保温");
        Assert.Single(byName.Items);
        Assert.Equal("P001", byName.Items[0].ProductCode);

        // 名称关键字命中多个商品时全部返回（「杯」同时命中保温杯与玻璃杯）
        var sharedName = await QueryAsync(db, keyword: "杯");
        Assert.Equal(new[] { "P001", "P002" }, sharedName.Items.Select(i => i.ProductCode));

        var trimmed = await QueryAsync(db, keyword: "  玩具  ");
        Assert.Equal("玩具", trimmed.Keyword);                       // 首尾空白被归一化
        Assert.Single(trimmed.Items);
        Assert.Equal("X900", trimmed.Items[0].ProductCode);

        var blank = await QueryAsync(db, keyword: "   ");
        Assert.Equal(string.Empty, blank.Keyword);                    // 只有空白 = 不过滤（回显为空串）
        Assert.Equal(3, blank.Total);

        var none = await QueryAsync(db, keyword: "不存在");
        Assert.Equal(0, none.Total);
        Assert.Empty(none.Items);
    }

    // ==================== 6. 图片填充状态筛选 ====================

    [Fact]
    public async Task Library_image_state_filter_separates_full_partial_none_and_has()
    {
        using var db = TestDbFactory.Create();
        SeedProduct(db, "P001", "全部填充", "标准", LocalReference, HttpReference, LocalReference);
        SeedProduct(db, "P002", "一个图片位", "标准", image1: LocalReference);
        SeedProduct(db, "P003", "两个图片位", "标准", image1: LocalReference, image3: HttpReference);
        SeedProduct(db, "P004", "无图片");
        await db.SaveChangesAsync();

        var all = await QueryAsync(db);
        Assert.Equal(4, all.Total);

        var has = await QueryAsync(db, imageState: ProductImageRules.FilterHas);
        Assert.Equal(3, has.Total);
        Assert.DoesNotContain(has.Items, item => item.ProductCode == "P004");

        var fullOnly = await QueryAsync(db, imageState: ProductImageRules.FilterFull);
        Assert.Equal(new[] { "P001" }, fullOnly.Items.Select(i => i.ProductCode));

        var partialOnly = await QueryAsync(db, imageState: ProductImageRules.FilterPartial);
        Assert.Equal(new[] { "P002", "P003" }, partialOnly.Items.Select(i => i.ProductCode));

        var noneOnly = await QueryAsync(db, imageState: ProductImageRules.FilterNone);
        Assert.Equal(new[] { "P004" }, noneOnly.Items.Select(i => i.ProductCode));

        // 大小写 / 空白归一化；未知取值按「全部」处理（有界钳制，不报错）
        var normalized = await QueryAsync(db, imageState: " PARTIAL ");
        Assert.Equal(ProductImageRules.FilterPartial, normalized.ImageState);
        Assert.Equal(2, normalized.Total);
        Assert.Equal(ProductImageRules.FilterText(ProductImageRules.FilterPartial), normalized.ImageStateText);

        var unknown = await QueryAsync(db, imageState: "unsafe");
        Assert.Equal(ProductImageRules.FilterAll, unknown.ImageState);
        Assert.Equal(4, unknown.Total);
    }

    // ==================== 7. 商品 / 状态筛选与软删除排除 ====================

    [Fact]
    public async Task Library_applies_product_status_and_soft_delete_filters()
    {
        using var db = TestDbFactory.Create();
        SeedProduct(db, "P001", "启用商品");
        var disabled = SeedProduct(db, "P002", "停用商品", status: ProductImageRules.DisabledStatus);
        SeedProduct(db, "P003", "已删除商品", deleted: true);
        await db.SaveChangesAsync();

        var all = await QueryAsync(db);
        Assert.Equal(2, all.Total);                                    // 软删除商品一律不出现
        Assert.DoesNotContain(all.Items, item => item.ProductCode == "P003");

        var onlyDisabled = await QueryAsync(db, status: ProductImageRules.DisabledStatus);
        var row = Assert.Single(onlyDisabled.Items);
        Assert.Equal("停用商品", row.ProductName);
        Assert.Equal("停用", row.StatusText);

        // 非法状态取值按「不限」处理，不臆造也不报错
        var invalidStatus = await QueryAsync(db, status: 9);
        Assert.Null(invalidStatus.Status);
        Assert.Equal(2, invalidStatus.Total);

        var byId = await QueryAsync(db, productId: disabled.Id);
        Assert.Equal(disabled.Id, byId.ProductId);
        Assert.Equal(new[] { "P002" }, byId.Items.Select(i => i.ProductCode));

        // 非法商品 Id 按「不限」处理
        var invalidId = await QueryAsync(db, productId: -1);
        Assert.Null(invalidId.ProductId);
        Assert.Equal(2, invalidId.Total);
    }

    // ==================== 8. 分页有界与排序 ====================

    [Fact]
    public async Task Library_paging_is_ordered_and_bounded()
    {
        using var db = TestDbFactory.Create();
        for (var i = 1; i <= 5; i++) SeedProduct(db, $"P{i:000}", $"商品{i}");
        await db.SaveChangesAsync();

        var first = await QueryAsync(db, page: 1, pageSize: 2);
        Assert.Equal(5, first.Total);
        Assert.Equal(2, first.PageSize);
        Assert.Equal(3, first.TotalPages);
        Assert.Equal(new[] { "P001", "P002" }, first.Items.Select(i => i.ProductCode));

        var last = await QueryAsync(db, page: 3, pageSize: 2);
        Assert.Single(last.Items);
        Assert.Equal("P005", last.Items[0].ProductCode);

        // 非法分页按有界默认值钳制
        var clamped = await QueryAsync(db, page: 0, pageSize: 0);
        Assert.Equal(1, clamped.Page);
        Assert.Equal(ProductImageLibraryQuery.DefaultPageSize, clamped.PageSize);

        // 超大每页条数按上限截断（单次请求有界）
        var huge = await QueryAsync(db, pageSize: 100000);
        Assert.Equal(ProductImageLibraryQuery.MaxPageSize, huge.PageSize);
        Assert.Equal(5, huge.Items.Count);
    }
    // ==================== 9. 有界数据集访问（无逐行查库）与只读不写库 ====================

    [Fact]
    public async Task Library_uses_a_bounded_number_of_dataset_reads_and_never_writes()
    {
        using var db = TestDbFactory.Create();
        SeedProduct(db, "P001", "商品1", "标准", LocalReference, HttpReference, LocalReference);
        await db.SaveChangesAsync();

        var counting = CountingDbContext.Wrap(db);
        var single = await ProductImageLibraryService.QueryAsync(counting.Proxy, new ProductImageLibraryQuery { PageSize = 1 });
        var singleReads = counting.DatasetReads;

        Assert.Equal(1, single.Total);
        // 常数级访问：整次查询只取一次商品资料数据集（计数 + 本页分页各一次查询）
        Assert.Equal(1, singleReads);
        // 只访问商品资料数据集：不触碰任何其它数据集、也不读数据字典
        Assert.Equal(new[] { nameof(IErpDbContext.BaseProducts) }, counting.ReadProperties.Distinct().ToArray());

        // 再补 299 个商品（跨多页）：数据集访问次数必须保持不变（无逐行查库）
        for (var i = 2; i <= 300; i++)
            SeedProduct(db, $"P{i:0000}", $"商品{i}", "标准", LocalReference, HttpReference, LocalReference);
        await db.SaveChangesAsync();

        var large = await ProductImageLibraryService.QueryAsync(counting.Proxy,
            new ProductImageLibraryQuery { PageSize = 200 });
        var largeReads = counting.DatasetReads - singleReads;

        Assert.Equal(300, large.Total);
        Assert.Equal(ProductImageLibraryQuery.MaxPageSize, large.Items.Count);   // 单页有界（上限 200）
        Assert.Equal(singleReads, largeReads);                                   // 行数 / 页大小变化不改变访问次数
        Assert.Equal(200 * ProductImageRules.SlotCount, large.RenderableSlotCount);
        Assert.Equal(0, large.UnusableReferenceCount);
        Assert.Equal(0, counting.WriteCalls);                                    // 只读：不落库
    }

    // ==================== 10. 不改写商品图片字段与任何其它状态 ====================

    [Fact]
    public async Task Library_never_mutates_image_fields_or_any_other_state()
    {
        using var db = TestDbFactory.Create();
        var product = SeedProduct(db, "P001", "混合引用", "标准", "javascript:alert(1)", LocalReference, string.Empty);
        await db.SaveChangesAsync();

        var beforeImage1 = product.Image1;
        var beforeImage2 = product.Image2;
        var beforeImage3 = product.Image3;
        var beforeStatus = product.Status;
        var beforeUpdatedAt = product.UpdatedAt;
        var beforeProductName = product.ProductName;

        var counting = CountingDbContext.Wrap(db);
        await ProductImageLibraryService.QueryAsync(db, new ProductImageLibraryQuery());
        await ProductImageLibraryService.QueryAsync(db,
            new ProductImageLibraryQuery { ImageState = ProductImageRules.FilterHas, Keyword = "P001" });
        await Controller(counting.Proxy).List(new ProductImageLibraryQuery { ProductId = product.Id }, CancellationToken.None);

        var stored = await db.BaseProducts.AsNoTracking().SingleAsync(p => p.Id == product.Id);
        Assert.Equal(beforeImage1, stored.Image1);
        Assert.Equal(beforeImage2, stored.Image2);
        Assert.Equal(beforeImage3, stored.Image3);
        Assert.Equal(beforeStatus, stored.Status);
        Assert.Equal(beforeProductName, stored.ProductName);
        Assert.Equal(beforeUpdatedAt, stored.UpdatedAt);

        // 商品资料（以及任何其它实体）都没有被改写：没有任何非 Unchanged 的跟踪实体，写入调用为 0
        Assert.DoesNotContain(db.ChangeTracker.Entries(), e => e.State != EntityState.Unchanged);
        Assert.Equal(0, counting.WriteCalls);
    }
    // ==================== 11. 只读端点契约与载荷 ====================

    [Fact]
    public void Controller_exposes_only_read_only_get_endpoints_without_storage_dependencies()
    {
        // 路由挂在既有基础资料前缀下，且与商品接口（/api/base/products）互不覆盖
        var route = typeof(ProductImageLibraryController)
            .GetCustomAttributes(typeof(RouteAttribute), false).Cast<RouteAttribute>().Single();
        Assert.Equal("api/base/product-images", route.Template);

        // 控制器只有 GET 端点：没有任何 POST / PUT / PATCH / DELETE（没有上传、删除或改写路径）
        var methods = typeof(ProductImageLibraryController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName)
            .ToList();
        Assert.NotEmpty(methods);
        foreach (var method in methods)
        {
            var attributes = method.GetCustomAttributes(true);
            Assert.Contains(attributes, a => a is HttpGetAttribute);
            Assert.DoesNotContain(attributes, a => a is HttpPostAttribute or HttpPutAttribute or HttpPatchAttribute or HttpDeleteAttribute);
        }

        // 只依赖数据上下文：不注入任何存储 / OSS 服务（不读凭据、不做服务端抓取）
        var parameterTypes = typeof(ProductImageLibraryController).GetConstructors().Single()
            .GetParameters().Select(p => p.ParameterType).ToArray();
        Assert.Equal(new[] { typeof(IErpDbContext) }, parameterTypes);

        // 服务层是静态只读 API：公开静态方法只有 QueryAsync，且不接收任何存储 / OSS 依赖
        Assert.True(typeof(ProductImageLibraryService).IsAbstract && typeof(ProductImageLibraryService).IsSealed);
        var publicMethods = typeof(ProductImageLibraryService)
            .GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Select(m => m.Name).Distinct().ToArray();
        Assert.Equal(new[] { nameof(ProductImageLibraryService.QueryAsync) }, publicMethods);

        var queryMethod = typeof(ProductImageLibraryService).GetMethod(nameof(ProductImageLibraryService.QueryAsync))!;
        Assert.Equal(
            new[] { typeof(IErpDbContext), typeof(ProductImageLibraryQuery), typeof(CancellationToken) },
            queryMethod.GetParameters().Select(p => p.ParameterType));
    }

    [Fact]
    public async Task Controller_endpoint_returns_the_library_payload()
    {
        using var db = TestDbFactory.Create();
        SeedProduct(db, "P001", "保温杯", "500ml", LocalReference, HttpReference, "javascript:alert(1)");
        await db.SaveChangesAsync();

        var result = await Controller(db).List(
            new ProductImageLibraryQuery { Keyword = "P" }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var payload = Assert.IsType<ApiResponse<ProductImageLibraryPage>>(ok.Value);
        Assert.Equal(ErrorCodes.Success, payload.Code);

        var row = Assert.Single(payload.Data!.Items);
        Assert.Equal(3, row.Images.Count);
        Assert.Equal(ProductImageRules.RefUnsafeScheme, row.Images[2].State);
        Assert.Equal(string.Empty, row.Images[2].Source);
        Assert.Equal(2, payload.Data!.RenderableSlotCount);
        Assert.Equal(1, payload.Data!.UnusableReferenceCount);
    }
    // ==================== 12. 路由与前端接线（静态核对） ====================

    [Fact]
    public void 路由与前端接线_图片库入口可静态核对()
    {
        // 商品模块：工具栏「图片库」入口 + 行操作「图片库」（只读查看单个商品）
        var modules = File.ReadAllText(RepoFile("src", "ERP.Api", "wwwroot", "js", "modules.js"));
        Assert.Contains("openProductImageLibrary()", modules);
        Assert.Contains("openProductImageLibrary'", modules);

        // 前端脚本：只调只读端点；不可用引用只作文本；加载失败转安全占位；无任何上传 / 删除调用
        var ui = File.ReadAllText(RepoFile("src", "ERP.Api", "wwwroot", "js", "product-image-library.js"));
        Assert.Contains("/api/base/product-images", ui);
        Assert.Contains("escapeHtml(slot.reference", ui);
        Assert.Contains("pilImageError", ui);
        Assert.Contains("图片引用不可用", ui);
        Assert.DoesNotContain("/upload", ui);
        Assert.DoesNotContain("method: 'POST'", ui);
        Assert.DoesNotContain("method: 'DELETE'", ui);

        // 页面已加载图片库脚本
        var index = File.ReadAllText(RepoFile("src", "ERP.Api", "wwwroot", "index.html"));
        Assert.Contains("/js/product-image-library.js", index);
    }

    /// <summary>
    /// 只读计数上下文代理（<see cref="DispatchProxy"/>）：记录访问的数据集（<c>DbSet</c> 属性）名称与写入次数，
    /// 用于断言「分页 / 有界查询」「无逐行查库」与「只读不写库」；不改动生产代码。
    /// </summary>
    public class CountingDbContext : DispatchProxy
    {
        private IErpDbContext _inner = null!;

        /// <summary>包装后的上下文（服务 / 控制器按 <see cref="IErpDbContext"/> 使用）</summary>
        public IErpDbContext Proxy { get; private set; } = null!;

        /// <summary>数据集（<c>DbSet</c> 属性）访问次数：即本次查询实际发起的数据集访问次数</summary>
        public int DatasetReads => ReadProperties.Count;

        /// <summary>被访问的数据集属性名（本库预期只有 BaseProducts）</summary>
        public List<string> ReadProperties { get; } = new();

        /// <summary><c>SaveChangesAsync</c> 调用次数：只读库恒为 0</summary>
        public int WriteCalls { get; private set; }

        /// <summary>包装一个真实上下文（计数从返回对象上读取）</summary>
        public static CountingDbContext Wrap(IErpDbContext inner)
        {
            var proxy = DispatchProxy.Create<IErpDbContext, CountingDbContext>();
            var counting = (CountingDbContext)(object)proxy;
            counting._inner = inner;
            counting.Proxy = proxy;
            return counting;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod is null) return null;
            if (targetMethod.Name == nameof(IErpDbContext.SaveChangesAsync))
            {
                WriteCalls++;
                return _inner.SaveChangesAsync(args is { Length: > 0 } ? (CancellationToken)args[0]! : default);
            }

            if (targetMethod.Name.StartsWith("get_", StringComparison.Ordinal))
            {
                ReadProperties.Add(targetMethod.Name[4..]);
            }

            return targetMethod.Invoke(_inner, args);
        }
    }
}






