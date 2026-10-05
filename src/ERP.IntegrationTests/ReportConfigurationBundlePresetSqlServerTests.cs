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
/// ERP-310 Stage 2 捆绑预设 SQL Server 集成测试。
/// <para>在专用 localdb 目标（实例含 NEWERP_AutoAcceptance、库名前缀 NEWERP_AUTOTEST、IntegratedSecurity）
/// 上复用 ERP-287 既有夹具的销售订单 + 特权账号，并自包含播种应收账款发票，物化「客户报告包」捆绑预设
/// （销售订单 + 应收证据）并断言预览 / 多工作表 Excel / 多节 PDF 非空。</para>
/// <para>说明：本测试项目不引用 ERP.Api，因此「客户订单与收款核对」族的订单证据 / 未关联收款证据运行时适配器
/// （Api 层）由 ERP.UnitTests（内存数据库，引用 Api）覆盖；本文件覆盖可直达基础设施层的客户报告包两节。</para>
/// </summary>
public sealed class ReportConfigurationBundlePresetSqlServerTests : IClassFixture<ReportConfigurationExecutionSqlServerFixture>
{
    private readonly ReportConfigurationExecutionSqlServerFixture _fixture;

    public ReportConfigurationBundlePresetSqlServerTests(ReportConfigurationExecutionSqlServerFixture fixture) => _fixture = fixture;

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

    private static async Task EnsureCustomerMenuAsync(ErpDbContext db, ReportConfigurationExecutionSqlServerFixture fixture)
    {
        var customerMenu = await db.SysMenus.FirstOrDefaultAsync(m => m.MenuCode == "customer" && !m.IsDeleted);
        if (customerMenu is null)
        {
            Guard();
            customerMenu = new SysMenu { MenuCode = "customer", MenuName = "Isolated bundle customer", MenuType = MenuType.Menu };
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
        foreach (var pair in new[] { ("ERP310-INV-USD1", "USD", 100m), ("ERP310-INV-EUR1", "EUR", 50m) })
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
                CustomerName = "Isolated bundle customer",
                InvoiceDate = new DateTime(2026, 10, 4),
                Currency = pair.Item2,
                NetAmount = pair.Item3,
                TaxAmount = 0m,
                GrossAmount = pair.Item3,
                Status = CustomerSalesInvoiceEvidenceRules.StatusRecorded,
            });
            await db.SaveChangesAsync();
        }
    }

    [Fact]
    public async Task 捆绑预设_客户报告包_物化预览与ExcelPDF_非空()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();

        await EnsureCustomerMenuAsync(db, _fixture);
        await SeedReceivableInvoicesAsync(db, _fixture);

        var providers = BuildProviders(db);
        var catalog = new ReportConfigurationCatalog(providers);
        var service = new ReportConfigurationService(db, catalog);
        var execution = new ReportConfigurationExecutionService(db, providers);
        var bundle = new ReportConfigurationBundleService(execution);
        var presets = new ReportConfigurationBundlePresetCatalog(catalog, service, db);

        Guard();
        var materialized = await presets.MaterializeAsync("customer-report-packet",
            new ReportConfigurationBundlePresetMaterializeRequest { CustomerId = _fixture.CustomerAId },
            _fixture.PrivilegedUserId);

        Assert.Equal(2, materialized.Bundle.Sections.Count);

        var preview = await bundle.PreviewAsync(_fixture.PrivilegedUserId, materialized.Bundle);
        Assert.Equal(2, preview.SectionCount);
        Assert.Equal(ReportConfigurationConstants.DatasetSalesOrder, preview.Sections[0].Preview.DatasetKey);
        Assert.Equal(ReportConfigurationConstants.DatasetReceivable, preview.Sections[1].Preview.DatasetKey);
        Assert.True(preview.Sections[0].Preview.Rows.Count > 0);
        Assert.True(preview.Sections[1].Preview.Rows.Count > 0);

        using var lease = new ReportConfigurationExecutionBudget().Acquire(_fixture.PrivilegedUserId);
        var export = await bundle.BuildExportResultAsync(_fixture.PrivilegedUserId, materialized.Bundle, lease);

        var excel = new ReportConfigurationBundleExcelExporter().Build(export);
        Assert.True(excel.Length > 0);
        Assert.Equal((byte)'P', excel[0]);
        Assert.Equal((byte)'K', excel[1]);

        var fontPath = SimHeiPdfFontResolver.FindFontPath();
        if (fontPath is not null)
        {
            var pdf = ReportConfigurationBundlePdfExporter.Export(export, fontPath);
            Assert.StartsWith("%PDF-", Encoding.ASCII.GetString(pdf));
        }
    }
}
