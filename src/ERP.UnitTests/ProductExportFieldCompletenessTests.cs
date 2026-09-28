using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-107 只读出口字段完整度工作台单元测试。覆盖：
/// 稀疏商品与完整商品、无效尺寸与退税率边界、软删除 / 停用排除、关键字与完整度分组筛选、
/// 稳定分页有界、只读不写库、未知分组拒绝，以及接口与前端接线契约。
/// <para>全部使用内存数据库（TestDbFactory），不连接 SQL Server、不启动 API、不执行任何 SQL / seed，不做浏览器验收。</para>
/// </summary>
public class ProductExportFieldCompletenessTests
{
    // ==================== 0. 测试脚手架 ====================

    private static ProductExportFieldCompletenessController BuildController(ErpDbContext db) => new(db);

    private static ProductExportFieldCompletenessDto GetData(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<ApiResponse<ProductExportFieldCompletenessDto>>(ok.Value);
        Assert.Equal(ErrorCodes.Success, resp.Code);
        return resp.Data!;
    }

    private static ProductExportFieldCompletenessFieldDto FieldOf(ProductExportFieldCompletenessRowDto row, string key)
        => Assert.Single(row.Fields, f => f.Key == key);

    private static BaseProduct SeedProduct(ErpDbContext db, long id, string code, string name,
        string englishDeclareName = "", string packageUnit = "", int unitsPerPackage = 0,
        decimal outerLength = 0m, decimal outerWidth = 0m, decimal outerHeight = 0m, decimal outerWeight = 0m,
        decimal refundRate = 0m, int status = 1, bool deleted = false)
        => db.BaseProducts.Add(new BaseProduct
        {
            Id = id,
            ProductCode = code,
            ProductName = name,
            Spec = "标准",
            Unit = "PCS",
            EnglishDeclareName = englishDeclareName,
            PackageUnit = packageUnit,
            UnitsPerPackage = unitsPerPackage,
            OuterLength = outerLength,
            OuterWidth = outerWidth,
            OuterHeight = outerHeight,
            OuterWeight = outerWeight,
            RefundRate = refundRate,
            Status = status,
            IsDeleted = deleted
        }).Entity;

    // ==================== 1. 稀疏与完整商品 ====================

    [Fact]
    public async Task Worksheet_reports_complete_product_with_no_gaps()
    {
        using var db = TestDbFactory.Create();
        SeedProduct(db, 1, "P-FULL", "保温杯",
            englishDeclareName: "Insulated Bottle", packageUnit: "箱", unitsPerPackage: 12,
            outerLength: 30m, outerWidth: 20m, outerHeight: 40m, outerWeight: 5m, refundRate: 13m);
        await db.SaveChangesAsync();

        var data = GetData(await BuildController(db).GetWorksheet(new ProductExportFieldCompletenessQuery()));

        var row = Assert.Single(data.Items);
        Assert.Equal(1L, row.ProductId);
        Assert.Equal(ProductExportFieldCompletenessRules.GroupComplete, row.Completeness);
        Assert.Equal(0, row.GapCount);
        Assert.Equal(8, row.FieldCount);
        Assert.All(row.Fields, f => Assert.True(f.Present));
    }

    [Fact]
    public async Task Worksheet_distinguishes_blank_zero_and_unknown_per_field()
    {
        using var db = TestDbFactory.Create();
        SeedProduct(db, 1, "P-SPARSE", "稀疏商品",
            englishDeclareName: "",        // blank
            packageUnit: "   ",            // blank
            unitsPerPackage: 0,            // zero
            outerLength: -1m,              // unknown
            outerWidth: 0m,                // zero
            outerHeight: 5m,               // present
            outerWeight: 0m,               // zero
            refundRate: 0m);               // zero
        await db.SaveChangesAsync();

        var data = GetData(await BuildController(db).GetWorksheet(new ProductExportFieldCompletenessQuery()));

        var row = Assert.Single(data.Items);
        Assert.Equal(ProductExportFieldCompletenessRules.GroupIncomplete, row.Completeness);
        Assert.Equal(7, row.GapCount);

        Assert.Equal(ProductExportFieldCompletenessRules.StateBlank, FieldOf(row, "englishDeclareName").State);
        Assert.Equal(ProductExportFieldCompletenessRules.StateBlank, FieldOf(row, "packageUnit").State);
        Assert.Equal(ProductExportFieldCompletenessRules.StateZero, FieldOf(row, "unitsPerPackage").State);
        Assert.Equal(ProductExportFieldCompletenessRules.StateUnknown, FieldOf(row, "outerLength").State);
        Assert.Equal(ProductExportFieldCompletenessRules.StateZero, FieldOf(row, "outerWidth").State);
        Assert.Equal(ProductExportFieldCompletenessRules.StatePresent, FieldOf(row, "outerHeight").State);
        Assert.Equal(ProductExportFieldCompletenessRules.StateZero, FieldOf(row, "outerWeight").State);
        Assert.Equal(ProductExportFieldCompletenessRules.StateZero, FieldOf(row, "refundRate").State);
    }

    [Fact]
    public async Task Worksheet_marks_negative_dimensions_as_unknown()
    {
        using var db = TestDbFactory.Create();
        SeedProduct(db, 1, "P-BAD-DIM", "尺寸异常商品",
            englishDeclareName: "Broken Box", packageUnit: "箱", unitsPerPackage: 10,
            outerLength: -1m, outerWidth: -2m, outerHeight: -3m, outerWeight: -4m, refundRate: 13m);
        await db.SaveChangesAsync();

        var data = GetData(await BuildController(db).GetWorksheet(new ProductExportFieldCompletenessQuery()));

        var row = Assert.Single(data.Items);
        Assert.Equal(ProductExportFieldCompletenessRules.StateUnknown, FieldOf(row, "outerLength").State);
        Assert.Equal(ProductExportFieldCompletenessRules.StateUnknown, FieldOf(row, "outerWidth").State);
        Assert.Equal(ProductExportFieldCompletenessRules.StateUnknown, FieldOf(row, "outerHeight").State);
        Assert.Equal(ProductExportFieldCompletenessRules.StateUnknown, FieldOf(row, "outerWeight").State);
        Assert.Equal(4, row.GapCount);
    }

    [Fact]
    public async Task Worksheet_treats_refund_rate_boundaries_without_legal_conclusion()
    {
        using var db = TestDbFactory.Create();
        SeedProduct(db, 1, "P-100", "退税率边界-100", refundRate: 100m);
        SeedProduct(db, 2, "P-OVER", "退税率越界", refundRate: 120m);
        SeedProduct(db, 3, "P-NEG", "退税率负数", refundRate: -1m);
        await db.SaveChangesAsync();

        var data = GetData(await BuildController(db).GetWorksheet(new ProductExportFieldCompletenessQuery()));
        var byCode = data.Items.ToDictionary(r => r.ProductCode);

        Assert.Equal(ProductExportFieldCompletenessRules.StatePresent, FieldOf(byCode["P-100"], "refundRate").State);
        Assert.Equal(ProductExportFieldCompletenessRules.StateUnknown, FieldOf(byCode["P-OVER"], "refundRate").State);
        Assert.Equal(ProductExportFieldCompletenessRules.StateUnknown, FieldOf(byCode["P-NEG"], "refundRate").State);
        // 只报告字段状态，绝不输出「可退税 / 不可退税」之类的结论文案
        Assert.DoesNotContain(data.Items, r => r.Fields.Any(f => f.Text.Contains("可退税") || f.Text.Contains("不可退税")));
    }

    // ==================== 2. 软删除 / 停用排除 ====================

    [Fact]
    public async Task Worksheet_excludes_soft_deleted_and_disabled_products()
    {
        using var db = TestDbFactory.Create();
        SeedProduct(db, 1, "P-ACTIVE", "启用商品", englishDeclareName: "Active");
        SeedProduct(db, 2, "P-DELETED", "已删除商品", englishDeclareName: "Deleted", deleted: true);
        SeedProduct(db, 3, "P-DISABLED", "已停用商品", englishDeclareName: "Disabled", status: 0);
        await db.SaveChangesAsync();

        var data = GetData(await BuildController(db).GetWorksheet(new ProductExportFieldCompletenessQuery()));

        var row = Assert.Single(data.Items);
        Assert.Equal("P-ACTIVE", row.ProductCode);
    }

    // ==================== 3. 关键字与完整度分组筛选 ====================

    [Fact]
    public async Task Worksheet_filters_active_products_by_code_or_name()
    {
        using var db = TestDbFactory.Create();
        SeedProduct(db, 1, "ABC-1", "保温杯", englishDeclareName: "Bottle");
        SeedProduct(db, 2, "XYZ-2", "文具套装", englishDeclareName: "Stationery");
        await db.SaveChangesAsync();

        var byCode = GetData(await BuildController(db).GetWorksheet(
            new ProductExportFieldCompletenessQuery { Keyword = "ABC" }));
        Assert.Single(byCode.Items);
        Assert.Equal("ABC-1", byCode.Items[0].ProductCode);

        var byName = GetData(await BuildController(db).GetWorksheet(
            new ProductExportFieldCompletenessQuery { Keyword = "文具" }));
        Assert.Single(byName.Items);
        Assert.Equal("XYZ-2", byName.Items[0].ProductCode);
    }

    [Fact]
    public async Task Worksheet_filters_by_completeness_group()
    {
        using var db = TestDbFactory.Create();
        SeedProduct(db, 1, "P-FULL", "完整商品", englishDeclareName: "Full", packageUnit: "箱",
            unitsPerPackage: 12, outerLength: 30m, outerWidth: 20m, outerHeight: 40m, outerWeight: 5m, refundRate: 13m);
        SeedProduct(db, 2, "P-NO-DECL", "缺报关品名", englishDeclareName: "", packageUnit: "箱",
            unitsPerPackage: 12, outerLength: 30m, outerWidth: 20m, outerHeight: 40m, outerWeight: 5m, refundRate: 13m);
        SeedProduct(db, 3, "P-NO-RATE", "缺退税率", englishDeclareName: "NoRate", packageUnit: "箱",
            unitsPerPackage: 12, outerLength: 30m, outerWidth: 20m, outerHeight: 40m, outerWeight: 5m, refundRate: 0m);
        await db.SaveChangesAsync();

        var complete = GetData(await BuildController(db).GetWorksheet(
            new ProductExportFieldCompletenessQuery { Group = ProductExportFieldCompletenessRules.GroupComplete }));
        Assert.Single(complete.Items);
        Assert.Equal("P-FULL", complete.Items[0].ProductCode);

        var incomplete = GetData(await BuildController(db).GetWorksheet(
            new ProductExportFieldCompletenessQuery { Group = ProductExportFieldCompletenessRules.GroupIncomplete }));
        Assert.Equal(2, incomplete.Items.Count);

        var declaration = GetData(await BuildController(db).GetWorksheet(
            new ProductExportFieldCompletenessQuery { Group = ProductExportFieldCompletenessRules.GroupDeclaration }));
        Assert.Single(declaration.Items);
        Assert.Equal("P-NO-DECL", declaration.Items[0].ProductCode);

        var refundRate = GetData(await BuildController(db).GetWorksheet(
            new ProductExportFieldCompletenessQuery { Group = ProductExportFieldCompletenessRules.GroupRefundRate }));
        Assert.Single(refundRate.Items);
        Assert.Equal("P-NO-RATE", refundRate.Items[0].ProductCode);
    }


    // ==================== 4. 分页与只读 ====================

    [Fact]
    public async Task Worksheet_pages_stably_by_product_id()
    {
        using var db = TestDbFactory.Create();
        SeedProduct(db, 1, "P-1", "商品一", englishDeclareName: "A");
        SeedProduct(db, 2, "P-2", "商品二", englishDeclareName: "B");
        SeedProduct(db, 3, "P-3", "商品三", englishDeclareName: "C");
        await db.SaveChangesAsync();

        var page1 = GetData(await BuildController(db).GetWorksheet(
            new ProductExportFieldCompletenessQuery { Page = 1, PageSize = 2 }));
        var page2 = GetData(await BuildController(db).GetWorksheet(
            new ProductExportFieldCompletenessQuery { Page = 2, PageSize = 2 }));

        Assert.Equal(3, page1.Total);
        Assert.Equal(2, page1.Items.Count);
        Assert.Single(page2.Items);
        Assert.Equal(1L, page1.Items[0].ProductId);
        Assert.Equal(2L, page1.Items[1].ProductId);
        Assert.Equal(3L, page2.Items[0].ProductId);
    }

    [Fact]
    public async Task Worksheet_is_read_only_no_write()
    {
        using var db = TestDbFactory.Create();
        SeedProduct(db, 1, "P-RO", "只读商品", englishDeclareName: "Readonly");
        await db.SaveChangesAsync();
        var productCount = db.BaseProducts.Count();

        var data = GetData(await BuildController(db).GetWorksheet(new ProductExportFieldCompletenessQuery()));

        Assert.Single(data.Items);
        Assert.Equal(productCount, db.BaseProducts.Count());
        Assert.DoesNotContain(db.ChangeTracker.Entries(), e => e.State != EntityState.Unchanged);
        Assert.False(string.IsNullOrWhiteSpace(data.ReadOnlyText));
        Assert.False(string.IsNullOrWhiteSpace(data.BoundaryText));
        Assert.False(string.IsNullOrWhiteSpace(data.DisclaimerText));
    }

    [Fact]
    public async Task Worksheet_rejects_unknown_group()
    {
        using var db = TestDbFactory.Create();
        await db.SaveChangesAsync();

        await Assert.ThrowsAsync<BusinessException>(() =>
            BuildController(db).GetWorksheet(new ProductExportFieldCompletenessQuery { Group = "not-a-group" }));
    }

    // ==================== 5. 接口与前端接线契约 ====================

    [Fact]
    public void Frontend_wiring_registers_worksheet_and_edit_flow()
    {
        var route = typeof(ProductExportFieldCompletenessController).GetCustomAttributes(typeof(RouteAttribute), false)
            .Cast<RouteAttribute>().Single();
        Assert.Equal("api/base/products/export-field-completeness", route.Template);

        var js = File.ReadAllText(RepoFile("src", "ERP.Api", "wwwroot", "js", "product-export-field-completeness.js"));
        Assert.Contains("/api/base/products/export-field-completeness", js);
        Assert.Contains("openProductExportFieldCompleteness", js);
        Assert.Contains("openProductEditor", js);

        var modules = File.ReadAllText(RepoFile("src", "ERP.Api", "wwwroot", "js", "modules.js"));
        Assert.Contains("onclick: 'openProductExportFieldCompleteness()'", modules);

        var index = File.ReadAllText(RepoFile("src", "ERP.Api", "wwwroot", "index.html"));
        Assert.Contains("/js/product-export-field-completeness.js", index);
    }

    /// <summary>按仓库根目录拼接文件的绝对路径（与其它契约测试口径一致）</summary>
    private static string RepoFile(params string[] segments)
        => Path.GetFullPath(Path.Combine(
            new[] { AppContext.BaseDirectory, "..", "..", "..", "..", ".." }.Concat(segments).ToArray()));
}

