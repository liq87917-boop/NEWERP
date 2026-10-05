using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using ERP.Infrastructure.Export;
using ERP.Infrastructure.Reports;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using System.Text;
using Xunit;

namespace ERP.IntegrationTests;

/// <summary>
/// ERP-314 Stage 2 单节报表预设参数绑定 SQL Server 集成测试。
/// <para>在专用 localdb 目标（实例含 NEWERP_AutoAcceptance、库名前缀 NEWERP_AUTOTEST、IntegratedSecurity）
/// 上复用 ERP-287 既有夹具的销售订单 + 特权账号，并自包含播种应收账款发票，对「销售订单」与「应收账款」
/// 两个家族物化参数化定义并断言非空行、日期边界、当前用户范围、无参数兼容、无效输入不落库与 Excel / PDF 非空。</para>
/// </summary>
public sealed class ReportConfigurationPresetParameterSqlServerTests : IClassFixture<ReportConfigurationExecutionSqlServerFixture>
{
    private readonly ReportConfigurationExecutionSqlServerFixture _fixture;

    public ReportConfigurationPresetParameterSqlServerTests(ReportConfigurationExecutionSqlServerFixture fixture) => _fixture = fixture;

    private static void Guard()
    {
        var conn = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default")
            ?? $"Server=(localdb)\\{ReportConfigurationExecutionSqlServerFixture.InstanceMarker};Initial Catalog={ReportConfigurationExecutionSqlServerFixture.DefaultDatabaseName};Integrated Security=true;TrustServerCertificate=true;";
        var t = new SqlConnectionStringBuilder(conn);
        Assert.Equal($"(localdb)\\{ReportConfigurationExecutionSqlServerFixture.InstanceMarker}", t.DataSource, ignoreCase: true);
        Assert.StartsWith(ReportConfigurationExecutionSqlServerFixture.DatabasePrefix, t.InitialCatalog);
        Assert.True(t.IntegratedSecurity);
    }

    private static IReportConfigurationDatasetProvider[] BuildProviders(ErpDbContext db)
        => new IReportConfigurationDatasetProvider[]
        {
            new SalesOrderReportConfigurationDatasetProvider(new DynamicSalesOrderReportQuery(db)),
            new ReceivableReportConfigurationDatasetProvider(new DynamicReceivableReportQuery(db), db),
        };

    private static ReportConfigurationPresetCatalog BuildPresetCatalog(
        ErpDbContext db, IReportConfigurationCatalog catalog, IReportConfigurationService service)
    {
        var migrationPresets = new ReportMigrationPresetCatalog();
        var registry = new ReportMigrationRegistry(catalog, db, migrationPresets);
        return new ReportConfigurationPresetCatalog(catalog, registry, service);
    }

    private static async Task EnsureCustomerMenuAsync(ErpDbContext db, ReportConfigurationExecutionSqlServerFixture fixture)
    {
        var customerMenu = await db.SysMenus.FirstOrDefaultAsync(m => m.MenuCode == "customer" && !m.IsDeleted);
        if (customerMenu is null)
        {
            Guard();
            customerMenu = new SysMenu { MenuCode = "customer", MenuName = "Isolated preset customer", MenuType = MenuType.Menu };
            db.SysMenus.Add(customerMenu);
            await db.SaveChangesAsync();
        }

        var roleId = await db.SysUserRoles.Where(r => r.UserId == fixture.PrivilegedUserId).Select(r => r.RoleId).FirstAsync();
        if (!await db.SysRoleMenus.AnyAsync(rm => rm.RoleId == roleId && rm.MenuId == customerMenu.Id))
        {
            Guard();
            db.SysRoleMenus.Add(new SysRoleMenu { RoleId = roleId, MenuId = customerMenu.Id });
            await db.SaveChangesAsync();
        }
    }

    private static async Task SeedReceivableInvoicesAsync(ErpDbContext db, ReportConfigurationExecutionSqlServerFixture fixture)
    {
        foreach (var pair in new[] { ("ERP314-INV-SEP", new DateTime(2026, 9, 15)), ("ERP314-INV-OCT", new DateTime(2026, 10, 4)) })
        {
            if (await db.CustomerSalesInvoiceEvidences.AnyAsync(i => i.InvoiceNumber == pair.Item1))
                continue;

            Guard();
            db.CustomerSalesInvoiceEvidences.Add(new CustomerSalesInvoiceEvidence
            {
                InvoiceType = CustomerSalesInvoiceEvidenceRules.InvoiceTypeOrdinary,
                InvoiceNumber = pair.Item1,
                NormalizedInvoiceNumber = pair.Item1.Replace("-", string.Empty),
                CustomerId = fixture.CustomerAId,
                CustomerCode = ReportConfigurationExecutionSqlServerFixture.CustomerACode,
                CustomerName = "Isolated preset customer",
                InvoiceDate = pair.Item2,
                Currency = "USD",
                NetAmount = 100m,
                TaxAmount = 0m,
                GrossAmount = 100m,
                Status = CustomerSalesInvoiceEvidenceRules.StatusRecorded,
            });
            await db.SaveChangesAsync();
        }
    }

    [Fact]
    public async Task 两个家族_参数化物化_日期边界与当前用户范围_预览非空()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();

        await EnsureCustomerMenuAsync(db, _fixture);
        await SeedReceivableInvoicesAsync(db, _fixture);

        var providers = BuildProviders(db);
        var catalog = new ReportConfigurationCatalog(providers);
        var service = new ReportConfigurationService(db, catalog);
        var execution = new ReportConfigurationExecutionService(db, providers);
        var presets = BuildPresetCatalog(db, catalog, service);

        // 家族 1：销售订单（customer + date），日期边界 2026-09-01 ~ 2026-09-30，仅客户甲。
        Guard();
        var salesOrder = await presets.MaterializeAsync("sales-order",
            new ReportConfigurationPresetMaterializeRequest
            {
                CustomerId = _fixture.CustomerAId,
                StartDate = new DateTime(2026, 9, 1),
                EndDate = new DateTime(2026, 9, 30),
            }, _fixture.PrivilegedUserId);

        Assert.Equal(2, salesOrder.Definition!.Filters.Count);

        var salesPreview = await execution.PreviewAsync(_fixture.PrivilegedUserId,
            new ReportConfigurationPreviewRequest { ConfigurationId = salesOrder.Id, PageSize = 100 });
        Assert.Equal(ReportConfigurationConstants.DatasetSalesOrder, salesPreview.DatasetKey);
        Assert.True(salesPreview.Rows.Count > 0);
        Assert.All(salesPreview.Rows, r => Assert.Equal(_fixture.CustomerAId, Convert.ToInt64(r["customerId"]!)));
        Assert.All(salesPreview.Rows, r =>
        {
            var d = Convert.ToDateTime(r["orderDate"]!);
            Assert.InRange(d, new DateTime(2026, 9, 1), new DateTime(2026, 9, 30));
        });

        // 家族 2：应收账款（customer + date），日期边界 2026-10-01 ~ 2026-10-31。
        var receivable = await presets.MaterializeAsync("receivable",
            new ReportConfigurationPresetMaterializeRequest
            {
                CustomerId = _fixture.CustomerAId,
                StartDate = new DateTime(2026, 10, 1),
                EndDate = new DateTime(2026, 10, 31),
            }, _fixture.PrivilegedUserId);

        Assert.Equal(2, receivable.Definition!.Filters.Count);

        var receivablePreview = await execution.PreviewAsync(_fixture.PrivilegedUserId,
            new ReportConfigurationPreviewRequest { ConfigurationId = receivable.Id, PageSize = 100 });
        Assert.Equal(ReportConfigurationConstants.DatasetReceivable, receivablePreview.DatasetKey);
        Assert.True(receivablePreview.Rows.Count > 0);
        Assert.Contains(receivablePreview.Rows, r => Convert.ToString(r["invoiceNumber"]) == "ERP314-INV-OCT");
        Assert.DoesNotContain(receivablePreview.Rows, r => Convert.ToString(r["invoiceNumber"]) == "ERP314-INV-SEP");
    }


    [Fact]
    public async Task 参数化定义_ExcelPDF非空_无参数兼容_无效输入不落库()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();

        var providers = BuildProviders(db);
        var catalog = new ReportConfigurationCatalog(providers);
        var service = new ReportConfigurationService(db, catalog);
        var execution = new ReportConfigurationExecutionService(db, providers);
        var presets = BuildPresetCatalog(db, catalog, service);

        // 参数化定义导出 Excel / PDF 非空。
        Guard();
        var parameterized = await presets.MaterializeAsync("sales-order",
            new ReportConfigurationPresetMaterializeRequest
            {
                CustomerId = _fixture.CustomerAId,
                StartDate = new DateTime(2026, 9, 1),
                EndDate = new DateTime(2026, 9, 30),
                Status = "Approved",
            }, _fixture.PrivilegedUserId);

        Assert.Equal(3, parameterized.Definition!.Filters.Count);

        using (var lease = new ReportConfigurationExecutionBudget().Acquire(_fixture.PrivilegedUserId))
        {
            var export = await execution.BuildExportResultAsync(_fixture.PrivilegedUserId,
                new ReportConfigurationPreviewRequest { ConfigurationId = parameterized.Id, PageSize = 100 }, lease);

            var excel = new ReportConfigurationExcelExporter().Build(export);
            Assert.True(excel.Length > 0);
            Assert.Equal((byte)'P', excel[0]);
            Assert.Equal((byte)'K', excel[1]);

            var fontPath = SimHeiPdfFontResolver.FindFontPath();
            if (fontPath is not null)
            {
                var pdf = ReportConfigurationPdfExporter.Export(export, fontPath);
                Assert.StartsWith("%PDF-", Encoding.ASCII.GetString(pdf));
            }
        }

        // 无参数端点兼容：原模板定义物化，空筛选且预览非空。
        Guard();
        var before = await db.ReportConfigurations.CountAsync();
        var noParam = await presets.MaterializeAsync("sales-order", _fixture.PrivilegedUserId);
        Assert.Empty(noParam.Definition!.Filters);

        var noParamPreview = await execution.PreviewAsync(_fixture.PrivilegedUserId,
            new ReportConfigurationPreviewRequest { ConfigurationId = noParam.Id, PageSize = 100 });
        Assert.True(noParamPreview.Rows.Count > 0);

        // 无效输入（日期倒置）：显式失败且绝不新增草稿。
        var after = await db.ReportConfigurations.CountAsync();
        var ex = await Assert.ThrowsAsync<BusinessException>(() => presets.MaterializeAsync("sales-order",
            new ReportConfigurationPresetMaterializeRequest
            {
                StartDate = new DateTime(2026, 9, 30),
                EndDate = new DateTime(2026, 9, 1),
            }, _fixture.PrivilegedUserId));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Equal(after, await db.ReportConfigurations.CountAsync());
    }

}
