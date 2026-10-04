using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Infrastructure.Data;
using ERP.Infrastructure.Reports;
using ERP.Infrastructure.Export;
using Microsoft.Data.SqlClient;
using NPOI.XSSF.UserModel;
using Xunit;

namespace ERP.IntegrationTests;

// Direct stage acceptance, not a functional queue increment. Reuses guarded nonempty fixtures.
public sealed class ReportConfigurationRecipientAcceptanceSqlServerTests
    : IClassFixture<ReportConfigurationExecutionSqlServerFixture>
{
    private readonly ReportConfigurationExecutionSqlServerFixture _fixture;
    public ReportConfigurationRecipientAcceptanceSqlServerTests(ReportConfigurationExecutionSqlServerFixture fixture)
        => _fixture = fixture;

    private void Guard()
    {
        var target = new SqlConnectionStringBuilder(_fixture.ConnectionString);
        Assert.Equal("(localdb)\\NEWERP_AutoAcceptance", target.DataSource, ignoreCase: true);
        Assert.StartsWith("NEWERP_AUTOTEST", target.InitialCatalog);
        Assert.True(target.IntegratedSecurity);
    }

    private static ReportConfigurationDefinition Definition(params string[] fields) => new()
    {
        SchemaVersion = 1, DatasetKey = "sales-order", Fields = fields.ToList(),
        Filters = new(), Grouping = new() { "none" }, Aggregates = new(),
        Capabilities = new() { "preview" }
    };

    [Fact]
    public async Task SharedSnapshot_UsesRecipientScopeAndPinnedColumns_ExportsScopedRows_RevocationClosesAccess()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        IReadOnlyList<IReportConfigurationDatasetProvider> providers = new IReportConfigurationDatasetProvider[]
        {
            new SalesOrderReportConfigurationDatasetProvider(new DynamicSalesOrderReportQuery(db)),
            new ReceivableReportConfigurationDatasetProvider(new DynamicReceivableReportQuery(db))
        };
        var catalog = new ReportConfigurationCatalog(providers);
        var definitions = new ReportConfigurationService(db, catalog);
        var sharing = new ReportConfigurationSharingService(db, catalog);
        var execution = new ReportConfigurationExecutionService(db, providers);
        var owner = _fixture.PrivilegedUserId;
        var recipient = _fixture.SalespersonUserId;
        Guard();
        var saved = await definitions.CreateAsync(owner, new ReportConfigurationSaveDto
        {
            Name = "SQL recipient acceptance " + Guid.NewGuid().ToString("N"),
            Definition = Definition("orderNo", "customerId", "currency", "totalAmount")
        });
        Guard();
        var published = await definitions.PublishAsync(owner, saved.Id, saved.Version);
        Guard();
        var grant = await sharing.GrantAsync(owner, saved.Id, new ReportConfigurationGrantRequestDto
        { RecipientUserId = recipient, RevisionVersion = 1 });
        Guard();
        await definitions.UpdateAsync(owner, saved.Id, published.Version, new ReportConfigurationSaveDto
        { Name = "Changed private draft", Definition = Definition("orderNo") });
        var preview = await execution.PreviewAsync(recipient, new ReportConfigurationPreviewRequest
        { ConfigurationId = saved.Id, RevisionVersion = 999, PageSize = 200 });
        Assert.True(preview.IsPinnedRevision);
        Assert.Equal(1, preview.PinnedRevisionVersion);
        Assert.NotEmpty(preview.Rows);
        Assert.Equal(new[] { "orderNo", "customerId", "currency", "totalAmount" }, preview.Columns.Select(c => c.Key));
        Assert.All(preview.Rows, row => Assert.Equal(_fixture.CustomerAId, Convert.ToInt64(row["customerId"])));
        Assert.DoesNotContain(preview.Rows, row => Convert.ToString(row["orderNo"])!.StartsWith("ERP287-SO-B"));
        var excel = new ReportConfigurationExcelExporter().Build(preview);
        using var workbook = new XSSFWorkbook(new MemoryStream(excel));
        var sheet = workbook.GetSheet(ReportConfigurationExcelExporter.DataSheetName);
        Assert.Equal(preview.Rows.Count, sheet.LastRowNum);
        for (var i = 0; i < preview.Rows.Count; i++)
        {
            Assert.Equal(Convert.ToString(preview.Rows[i]["orderNo"]), sheet.GetRow(i + 1).GetCell(0).StringCellValue);
            Assert.Equal(Convert.ToDouble(preview.Rows[i]["totalAmount"]), sheet.GetRow(i + 1).GetCell(3).NumericCellValue);
        }
        var pdf = ReportConfigurationPdfExporter.Export(preview);
        Assert.StartsWith("%PDF", System.Text.Encoding.ASCII.GetString(pdf, 0, 8));
        Guard();
        await sharing.RevokeAsync(owner, saved.Id, recipient, grant.Version);
        var denied = await Assert.ThrowsAsync<BusinessException>(() => execution.PreviewAsync(recipient,
            new ReportConfigurationPreviewRequest { ConfigurationId = saved.Id }));
        Assert.Equal(ErrorCodes.NotFound, denied.Code);
    }
}
