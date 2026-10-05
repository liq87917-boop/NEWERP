using System.Text;
using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Infrastructure.Data;
using ERP.Infrastructure.Export;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-313 受控打印渲染单元测试：网格布局有界解析 / 校验 / 占位符替换、standard / grid PDF 导出（只读）、
/// 以及渲染服务复用受控绑定目录与执行服务的整单失败（族 / 数据集不匹配、被拒绝字段、模板不存在、未认证）。
/// <para>全部使用内存数据库（TestDbFactory）或纯离线对象，不连接 SQL Server、不启动 API、不执行任何 SQL / seed。</para>
/// </summary>
public class ReportConfigurationPrintRenderTests
{
    private static ReportPrintTemplateFieldAlias Alias(string key, string title, string type)
        => new(key, key, title, type);

    private static SysPrintTemplate SeedTemplate(ErpDbContext db, string billType = "sales-order", string? layoutJson = null)
    {
        var template = new SysPrintTemplate
        {
            BillType = billType,
            TemplateName = "测试打印模板",
            Title = "测试标题",
            CompanyName = "测试公司",
            CompanyAddress = "测试地址",
            CompanyPhone = "123456",
            ShowCompanyHeader = true,
            FooterText = "页脚",
            PaperSize = "A4",
            FontSize = 12,
            TitleFontSize = 16,
            CompanyFontSize = 18,
            FontFamily = "Microsoft YaHei",
            CellPadding = 6,
            RowHeight = 0,
            FieldKeys = "[\"BillNo\",\"Amount\"]",
            LayoutJson = layoutJson,
        };
        db.SysPrintTemplates.Add(template);
        db.SaveChanges();
        return template;
    }

    private static ReportConfigurationPreviewDto BuildPreview(string datasetKey, params string[] columnKeys)
    {
        var columns = columnKeys
            .Select((key, index) => new ReportConfigurationColumnDto(
                key,
                key == "Amount" ? "金额" : key,
                key == "Amount" ? ReportConfigurationConstants.TypeNumber : ReportConfigurationConstants.TypeText,
                null))
            .ToList();

        var row = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["BillNo"] = "SO-0001",
            ["OrderDate"] = new DateTime(2026, 9, 1),
            ["Amount"] = 1234.5m,
            ["CustomerName"] = "客户甲",
        };

        return new ReportConfigurationPreviewDto
        {
            ConfigurationId = 1,
            DatasetKey = datasetKey,
            Name = "测试报表",
            Version = 1,
            Columns = columns,
            Rows = new List<Dictionary<string, object?>> { row },
            Total = 1,
            MatchedCount = 1,
            SourceEvidenceCount = 1,
            Evidence = new ReportConfigurationEvidenceContextDto(
                datasetKey, "一行一条单据", "金额按原币呈现；数量按基础单位", "只读", "边界", "免责",
                ReportConfigurationConstants.CoverageCurrentPage),
        };
    }

    private sealed class FakeTemplateCatalog : IReportConfigurationPrintTemplateCatalog
    {
        public ReportPrintTemplateBindingDto Binding = new()
        {
            TemplateId = 0,
            FamilyKey = "sales-order",
            DatasetKey = "bill-export:sales-order",
            BoundColumns = new List<ReportPrintTemplateFieldAlias>(),
        };

        public Task<ReportPrintTemplateCatalogDto> GetCatalogAsync(long? userId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<ReportPrintTemplateBindingDto> BindAsync(
            ReportPrintTemplateBindingRequest request, long? userId, CancellationToken cancellationToken = default)
            => Task.FromResult(Binding);
    }

    private sealed class FakeExecution : IReportConfigurationExecutionService
    {
        public ReportConfigurationExportResultDto Export = new() { Preview = new ReportConfigurationPreviewDto() };

        public Task<ReportConfigurationPreviewDto> PreviewAsync(long ownerUserId, ReportConfigurationPreviewRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<ReportConfigurationPreviewDto> PreviewAsync(long ownerUserId, ReportConfigurationPreviewRequest request, IReportConfigurationExecutionLease lease)
            => throw new NotSupportedException();

        public Task<ReportConfigurationExportResultDto> BuildExportResultAsync(long ownerUserId, ReportConfigurationPreviewRequest request, IReportConfigurationExecutionLease lease)
            => Task.FromResult(Export);
    }

    private static ReportConfigurationPrintRenderService BuildService(
        ErpDbContext db, FakeTemplateCatalog catalog, FakeExecution execution)
        => new(db, catalog, execution, new ReportConfigurationExecutionBudget());

    private static ReportPrintGridBlockDto GridBlock(params ReportPrintGridCellDto[] cells)
        => new() { ColWidths = new double[] { 100, 100 }, RowHeights = new double[] { 30, 30 }, Cells = cells.ToList() };

    private static ReportPrintGridCellDto Cell(int row, int col, string text)
        => new() { Row = row, Col = col, Text = text };

    private static readonly IReadOnlyList<ReportPrintTemplateFieldAlias> BoundAliases =
        new List<ReportPrintTemplateFieldAlias>
        {
            Alias("BillNo", "单据号", ReportConfigurationConstants.TypeText),
            Alias("OrderDate", "日期", ReportConfigurationConstants.TypeDate),
            Alias("Amount", "金额", ReportConfigurationConstants.TypeNumber),
        };

    private static List<Dictionary<string, object?>> GridRows() => new()
    {
        new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["BillNo"] = "SO-0001",
            ["OrderDate"] = new DateTime(2026, 9, 1),
            ["Amount"] = 1234.5m,
        },
    };

    private static SysPrintTemplate CompanyTemplate() => new()
    {
        CompanyName = "测试公司",
        CompanyAddress = "测试地址",
        CompanyPhone = "123456",
        FooterText = "页脚",
    };

    private static void AssertInvalidGrid(string? json, string? contains = null)
    {
        var ex = Assert.Throws<BusinessException>(() =>
            ReportPrintGridLayoutRules.Build(json, BoundAliases, GridRows(), CompanyTemplate()));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        if (contains is not null)
            Assert.Contains(contains, ex.Message);
    }

    [Fact]
    public void Grid_合法布局_替换封闭占位符并保留字面文本()
    {
        const string json = "{\"cols\":[100,100],\"rows\":[30,30],\"cells\":{\"0_0\":{\"v\":\"单号：{BillNo}\",\"bold\":true},\"1_0\":{\"v\":\"金额：{Amount}\",\"align\":\"right\"}},\"font\":\"Microsoft YaHei\"}";

        var blocks = ReportPrintGridLayoutRules.Build(json, BoundAliases, GridRows(), CompanyTemplate());

        var block = Assert.Single(blocks);
        Assert.Equal(2, block.Cells.Count);
        Assert.Contains(block.Cells, c => c.Text == "单号：SO-0001" && c.Bold);
        Assert.Contains(block.Cells, c => c.Text == "金额：1234.5" && c.Align == "right");
    }

    [Fact]
    public void Grid_公司占位符_替换模板公司字段()
    {
        const string json = "{\"cols\":[200],\"rows\":[30],\"cells\":{\"0_0\":{\"v\":\"{CompanyName} {CompanyAddress} {CompanyPhone}\"}}}";

        var block = Assert.Single(ReportPrintGridLayoutRules.Build(json, BoundAliases, GridRows(), CompanyTemplate()));

        Assert.Contains(block.Cells, c => c.Text == "测试公司 测试地址 123456");
    }

    [Fact]
    public void Grid_合并区_应用跨度且被覆盖单元格不重复输出()
    {
        const string json = "{\"cols\":[100,100],\"rows\":[30,30],\"cells\":{\"0_0\":{\"v\":\"标题\"},\"1_1\":{\"v\":\"x\"}},\"merges\":[{\"r\":0,\"c\":0,\"rs\":2,\"cs\":2}]}";

        var block = Assert.Single(ReportPrintGridLayoutRules.Build(json, BoundAliases, GridRows(), CompanyTemplate()));

        var title = Assert.Single(block.Cells, c => c.Text == "标题");
        Assert.Equal(2, title.RowSpan);
        Assert.Equal(2, title.ColSpan);
        Assert.DoesNotContain(block.Cells, c => c.Row == 1 && c.Col == 1 && c.Text == "x");
    }

    [Fact]
    public void Grid_可执行公式_拒绝()
        => AssertInvalidGrid("{\"cols\":[100],\"rows\":[30],\"cells\":{\"0_0\":{\"v\":\"=SUM(1)\"}}}", "公式");

    [Fact]
    public void Grid_HTML脚本_拒绝()
        => AssertInvalidGrid("{\"cols\":[100],\"rows\":[30],\"cells\":{\"0_0\":{\"v\":\"<b>标题</b>\"}}}", "HTML");

    [Fact]
    public void Grid_任意属性路径_拒绝()
        => AssertInvalidGrid("{\"cols\":[100],\"rows\":[30],\"cells\":{\"0_0\":{\"v\":\"{BillNo.x}\"}}}", "占位符");

    [Fact]
    public void Grid_未支持别名_拒绝()
        => AssertInvalidGrid("{\"cols\":[100],\"rows\":[30],\"cells\":{\"0_0\":{\"v\":\"{Unknown}\"}}}", "不支持");

    [Fact]
    public void Grid_非字符串单元格文本_拒绝()
        => AssertInvalidGrid("{\"cols\":[100],\"rows\":[30],\"cells\":{\"0_0\":{\"v\":123}}}", "字符串");

    [Fact]
    public void Grid_畸形坐标_拒绝()
        => AssertInvalidGrid("{\"cols\":[100],\"rows\":[30],\"cells\":{\"x_y\":{\"v\":\"a\"}}}", "坐标");

    [Fact]
    public void Grid_合并区重叠_拒绝()
        => AssertInvalidGrid("{\"cols\":[100,100],\"rows\":[30,30],\"merges\":[{\"r\":0,\"c\":0,\"rs\":1,\"cs\":2},{\"r\":0,\"c\":1,\"rs\":1,\"cs\":1}]}", "重叠");

    [Fact]
    public void Grid_缺少行数组_拒绝()
        => AssertInvalidGrid("{\"cols\":[100]}", "行");

    [Fact]
    public void Grid_超大总宽度_拒绝()
        => AssertInvalidGrid("{\"cols\":[1500,1500],\"rows\":[30]}", "总宽度");

    [Fact]
    public void Grid_明细区启用_显式不支持()
        => AssertInvalidGrid("{\"cols\":[100],\"rows\":[30],\"detail\":{\"enabled\":true}}", "明细");

    [Fact]
    public void Grid_水印启用_显式不支持()
        => AssertInvalidGrid("{\"cols\":[100],\"rows\":[30],\"watermark\":{\"enabled\":true}}", "水印");

    [Fact]
    public void Grid_非法JSON_拒绝()
        => AssertInvalidGrid("not-json", "JSON");

    private static ReportConfigurationPrintRenderDto BuildStandardRender(string layout = "standard")
        => new()
        {
            TemplateId = 1,
            FamilyKey = "sales-order",
            DatasetKey = "bill-export:sales-order",
            TemplateName = "测试打印模板",
            Title = "测试标题",
            CompanyName = "测试公司",
            ShowCompanyHeader = true,
            FooterText = "页脚",
            PaperSize = "A4",
            FontSize = 12,
            TitleFontSize = 16,
            CompanyFontSize = 18,
            FontFamily = "Microsoft YaHei",
            CellPadding = 6,
            RowHeight = 0,
            Layout = layout,
            Columns = new List<ReportPrintRenderColumnDto>
            {
                new() { LegacyKey = "BillNo", ColumnKey = "BillNo", Title = "单据号", Type = "text" },
                new() { LegacyKey = "Amount", ColumnKey = "Amount", Title = "金额", Type = "number" },
            },
            Rows = new List<Dictionary<string, object?>>
            {
                new(StringComparer.Ordinal) { ["BillNo"] = "SO-0001", ["Amount"] = 1234.5m },
            },
            Evidence = new ReportConfigurationEvidenceContextDto(
                "bill-export:sales-order", "一行一条单据", "金额按原币呈现", "只读", "边界", "免责",
                ReportConfigurationConstants.CoverageCurrentPage),
            Coverage = ReportConfigurationConstants.CoverageCurrentPage,
            MatchedCount = 1,
            SourceEvidenceCount = 1,
            CorrelationId = "corr",
            DefinitionName = "测试报表",
            DefinitionVersion = 1,
        };

    [Fact]
    public void Pdf_标准布局_返回PDF签名()
    {
        var font = SimHeiPdfFontResolver.FindFontPath();
        if (font is null)
            return;

        var bytes = ReportConfigurationPrintPdfExporter.Export(BuildStandardRender(), font);
        Assert.StartsWith("%PDF-", Encoding.ASCII.GetString(bytes));
    }

    [Fact]
    public void Pdf_网格布局_返回PDF签名()
    {
        var font = SimHeiPdfFontResolver.FindFontPath();
        if (font is null)
            return;

        var render = BuildStandardRender(ReportPrintRenderLayoutText.Grid);
        render.GridBlocks = new List<ReportPrintGridBlockDto>
        {
            GridBlock(Cell(0, 0, "单号：SO-0001"), Cell(1, 0, "金额：1234.5")),
        };

        var bytes = ReportConfigurationPrintPdfExporter.Export(render, font);
        Assert.StartsWith("%PDF-", Encoding.ASCII.GetString(bytes));
    }

    [Fact]
    public void Pdf_字体缺失_显式失败()
    {
        var ex = Assert.Throws<BusinessException>(() =>
            ReportConfigurationPrintPdfExporter.Export(BuildStandardRender(), fontPath: null));

        Assert.Equal(ErrorCodes.InternalError, ex.Code);
        Assert.Contains("SimHei", ex.Message);
    }

    [Fact]
    public void Pdf_已取消_抛出取消()
    {
        var font = SimHeiPdfFontResolver.FindFontPath();
        if (font is null)
            return;

        Assert.Throws<OperationCanceledException>(() =>
            ReportConfigurationPrintPdfExporter.Export(BuildStandardRender(), font, new CancellationToken(true)));
    }

    private static ReportConfigurationExportResultDto ExportOf(
        string datasetKey, params string[] columnKeys)
    {
        var preview = BuildPreview(datasetKey, columnKeys);
        return new ReportConfigurationExportResultDto
        {
            Preview = preview,
            Facts = preview.Rows,
            Coverage = ReportConfigurationConstants.CoverageCurrentPage,
            MatchedCount = 1,
            SourceEvidenceCount = 1,
        };
    }

    private static ReportPrintTemplateBindingDto BindingFor(
        long templateId, string datasetKey, params ReportPrintTemplateFieldAlias[] columns)
        => new()
        {
            TemplateId = templateId,
            FamilyKey = "sales-order",
            DatasetKey = datasetKey,
            BoundColumns = columns.ToList(),
        };

    [Fact]
    public async Task Render_标准布局_成功投影模板字段顺序()
    {
        using var db = TestDbFactory.Create();
        var template = SeedTemplate(db);
        var catalog = new FakeTemplateCatalog
        {
            Binding = BindingFor(template.Id, "bill-export:sales-order",
                Alias("BillNo", "单据号", ReportConfigurationConstants.TypeText),
                Alias("Amount", "金额", ReportConfigurationConstants.TypeNumber)),
        };
        var execution = new FakeExecution { Export = ExportOf("bill-export:sales-order", "BillNo", "Amount") };

        var service = BuildService(db, catalog, execution);
        var result = await service.PreviewAsync(1,
            new ReportConfigurationPrintRenderRequest { ConfigurationId = 1, TemplateId = template.Id });

        Assert.Equal(ReportPrintRenderLayoutText.Standard, result.Layout);
        Assert.Equal(new[] { "BillNo", "Amount" }, result.Columns.Select(c => c.ColumnKey).ToArray());
        var row = Assert.Single(result.Rows);
        Assert.Equal("SO-0001", row["BillNo"]);
        Assert.Equal(1234.5m, row["Amount"]);
    }

    [Fact]
    public async Task Render_网格布局_生成与事实行等量网格块()
    {
        using var db = TestDbFactory.Create();
        var template = SeedTemplate(db,
            layoutJson: "{\"cols\":[100,100],\"rows\":[30,30],\"cells\":{\"0_0\":{\"v\":\"单号：{BillNo}\"},\"1_0\":{\"v\":\"金额：{Amount}\"}}}");
        var catalog = new FakeTemplateCatalog
        {
            Binding = BindingFor(template.Id, "bill-export:sales-order",
                Alias("BillNo", "单据号", ReportConfigurationConstants.TypeText),
                Alias("Amount", "金额", ReportConfigurationConstants.TypeNumber)),
        };
        var execution = new FakeExecution { Export = ExportOf("bill-export:sales-order", "BillNo", "Amount") };

        var service = BuildService(db, catalog, execution);
        var result = await service.PreviewAsync(1,
            new ReportConfigurationPrintRenderRequest { ConfigurationId = 1, TemplateId = template.Id });

        Assert.Equal(ReportPrintRenderLayoutText.Grid, result.Layout);
        var block = Assert.Single(result.GridBlocks);
        Assert.Contains(block.Cells, c => c.Text == "单号：SO-0001");
        Assert.Contains(block.Cells, c => c.Text == "金额：1234.5");
    }

    [Fact]
    public async Task Render_族数据集不匹配_整单失败()
    {
        using var db = TestDbFactory.Create();
        var template = SeedTemplate(db);
        var catalog = new FakeTemplateCatalog
        {
            Binding = BindingFor(template.Id, "bill-export:purchase-order",
                Alias("BillNo", "单据号", "text")),
        };
        var execution = new FakeExecution { Export = ExportOf("bill-export:sales-order", "BillNo") };

        var service = BuildService(db, catalog, execution);
        var ex = await Assert.ThrowsAsync<BusinessException>(() => service.PreviewAsync(1,
            new ReportConfigurationPrintRenderRequest { ConfigurationId = 1, TemplateId = template.Id }));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Contains("不匹配", ex.Message);
    }

    [Fact]
    public async Task Render_被拒绝字段_整单失败()
    {
        using var db = TestDbFactory.Create();
        var template = SeedTemplate(db);
        var catalog = new FakeTemplateCatalog
        {
            Binding = BindingFor(template.Id, "bill-export:sales-order",
                Alias("BillNo", "单据号", "text"),
                Alias("OrderDate", "日期", "date")),
        };
        var execution = new FakeExecution { Export = ExportOf("bill-export:sales-order", "BillNo") };

        var service = BuildService(db, catalog, execution);
        var ex = await Assert.ThrowsAsync<BusinessException>(() => service.PreviewAsync(1,
            new ReportConfigurationPrintRenderRequest { ConfigurationId = 1, TemplateId = template.Id }));

        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
        Assert.Contains("OrderDate", ex.Message);
    }

    [Fact]
    public async Task Render_模板不存在_NotFound()
    {
        using var db = TestDbFactory.Create();
        var service = BuildService(db, new FakeTemplateCatalog(), new FakeExecution());

        var ex = await Assert.ThrowsAsync<BusinessException>(() => service.PreviewAsync(1,
            new ReportConfigurationPrintRenderRequest { ConfigurationId = 1, TemplateId = 999 }));

        Assert.Equal(ErrorCodes.NotFound, ex.Code);
    }

    [Fact]
    public async Task Render_未认证_Unauthorized()
    {
        using var db = TestDbFactory.Create();
        SeedTemplate(db);
        var service = BuildService(db, new FakeTemplateCatalog(), new FakeExecution());

        var ex = await Assert.ThrowsAsync<BusinessException>(() => service.PreviewAsync(0,
            new ReportConfigurationPrintRenderRequest { ConfigurationId = 1, TemplateId = 1 }));

        Assert.Equal(ErrorCodes.Unauthorized, ex.Code);
    }
}



