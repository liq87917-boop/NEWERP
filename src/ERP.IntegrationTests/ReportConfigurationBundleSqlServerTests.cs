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
/// ERP-307 Stage 2 通用报表配置捆绑 SQL Server 集成测试。
/// <para>在专用 localdb 目标（实例含 NEWERP_AutoAcceptance、库名前缀 NEWERP_AUTOTEST、IntegratedSecurity）
/// 上自包含播种 ERP307 前缀的应收账款发票，复用 ERP-287 既有夹具的销售订单 + 特权账号，构建两个不同授权
/// 数据集的捆绑，并断言预览 / 多工作表 Excel / 多节 PDF / 任一节失效整体失败。全程只读，不触碰生产凭据与数据。</para>
/// </summary>
public sealed class ReportConfigurationBundleSqlServerTests : IClassFixture<ReportConfigurationExecutionSqlServerFixture>
{
    private readonly ReportConfigurationExecutionSqlServerFixture _fixture;

    public ReportConfigurationBundleSqlServerTests(ReportConfigurationExecutionSqlServerFixture fixture) => _fixture = fixture;

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

    [Fact]
    public async Task 捆绑_两个授权数据集_预览与ExcelPDF()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();

        var customerMenu = await db.SysMenus.FirstOrDefaultAsync(m => m.MenuCode == "customer" && !m.IsDeleted);
        if (customerMenu is null)
        {
            Guard();
            customerMenu = new SysMenu { MenuCode = "customer", MenuName = "Isolated bundle customer", MenuType = MenuType.Menu };
            db.SysMenus.Add(customerMenu);
            await db.SaveChangesAsync();
        }

        var roleId = await db.SysUserRoles.Where(r => r.UserId == _fixture.PrivilegedUserId).Select(r => r.RoleId).FirstAsync();
        if (!await db.SysRoleMenus.AnyAsync(rm => rm.RoleId == roleId && rm.MenuId == customerMenu.Id))
        {
            Guard();
            db.SysRoleMenus.Add(new SysRoleMenu { RoleId = roleId, MenuId = customerMenu.Id });
            await db.SaveChangesAsync();
        }

        foreach (var pair in new[] { ("ERP307-INV-USD1", "USD", 100m), ("ERP307-INV-EUR1", "EUR", 50m) })
        {
            if (await db.CustomerSalesInvoiceEvidences.AnyAsync(i => i.InvoiceNumber == pair.Item1))
                continue;

            Guard();
            db.CustomerSalesInvoiceEvidences.Add(new CustomerSalesInvoiceEvidence
            {
                InvoiceType = CustomerSalesInvoiceEvidenceRules.InvoiceTypeOrdinary,
                InvoiceNumber = pair.Item1,
                NormalizedInvoiceNumber = pair.Item1.Replace("-", string.Empty),
                CustomerId = _fixture.CustomerAId,
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

        var providers = BuildProviders(db);
        var definitions = new ReportConfigurationService(db, new ReportConfigurationCatalog(providers));
        var execution = new ReportConfigurationExecutionService(db, providers);
        var bundle = new ReportConfigurationBundleService(execution);

        Guard();
        var so = await definitions.CreateAsync(_fixture.PrivilegedUserId, new ReportConfigurationSaveDto
        {
            Name = "Bundle sales " + Guid.NewGuid().ToString("N"),
            Definition = new ReportConfigurationDefinition
            {
                SchemaVersion = 1,
                DatasetKey = ReportConfigurationConstants.DatasetSalesOrder,
                Fields = new() { "orderNo", "currency", "totalAmount" },
                Filters = new(),
                Grouping = new() { ReportConfigurationConstants.GroupNone },
                Aggregates = new(),
                Capabilities = new(),
            },
        });
        var recv = await definitions.CreateAsync(_fixture.PrivilegedUserId, new ReportConfigurationSaveDto
        {
            Name = "Bundle receivable " + Guid.NewGuid().ToString("N"),
            Definition = new ReportConfigurationDefinition
            {
                SchemaVersion = 1,
                DatasetKey = ReportConfigurationConstants.DatasetReceivable,
                Fields = new() { "invoiceNumber", "currency", "grossAmount" },
                Filters = new() { new() { FieldKey = "customerId", Operator = "eq", Value = _fixture.CustomerAId } },
                Grouping = new() { ReportConfigurationConstants.GroupNone },
                Aggregates = new(),
                Capabilities = new(),
            },
        });

        var request = new ReportConfigurationBundleRequest
        {
            Sections = new List<ReportConfigurationBundleSectionRequest>
            {
                new() { ConfigurationId = so.Id, Title = "销售订单" },
                new() { ConfigurationId = recv.Id, Title = "应收账款" },
            },
        };

        Guard();
        var preview = await bundle.PreviewAsync(_fixture.PrivilegedUserId, request);
        Assert.Equal(2, preview.SectionCount);
        Assert.Equal(ReportConfigurationConstants.DatasetSalesOrder, preview.Sections[0].Preview.DatasetKey);
        Assert.Equal(ReportConfigurationConstants.DatasetReceivable, preview.Sections[1].Preview.DatasetKey);
        Assert.True(preview.Sections[0].Preview.Rows.Count > 0);
        Assert.Equal(2, preview.Sections[1].Preview.Rows.Count);

        using var lease = new ReportConfigurationExecutionBudget().Acquire(_fixture.PrivilegedUserId);
        var export = await bundle.BuildExportResultAsync(_fixture.PrivilegedUserId, request, lease);

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

    [Fact]
    public async Task 捆绑_任一节不存在_整体失败无部分()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var providers = BuildProviders(db);
        var execution = new ReportConfigurationExecutionService(db, providers);
        var bundle = new ReportConfigurationBundleService(execution);

        Guard();
        var ex = await Assert.ThrowsAsync<BusinessException>(() => bundle.PreviewAsync(_fixture.PrivilegedUserId,
            new ReportConfigurationBundleRequest
            {
                Sections = new List<ReportConfigurationBundleSectionRequest>
                {
                    new() { ConfigurationId = 1 },
                    new() { ConfigurationId = 999999999 },
                },
            }));

        Assert.Equal(ErrorCodes.NotFound, ex.Code);
    }
}
