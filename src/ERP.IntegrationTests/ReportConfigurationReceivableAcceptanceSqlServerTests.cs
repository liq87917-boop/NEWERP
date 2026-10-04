using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Reports;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERP.IntegrationTests;

public sealed class ReportConfigurationReceivableAcceptanceSqlServerTests
    : IClassFixture<ReportConfigurationExecutionSqlServerFixture>
{
    private readonly ReportConfigurationExecutionSqlServerFixture _fixture;
    public ReportConfigurationReceivableAcceptanceSqlServerTests(ReportConfigurationExecutionSqlServerFixture fixture) => _fixture = fixture;
    private void Guard()
    {
        var t = new SqlConnectionStringBuilder(_fixture.ConnectionString);
        Assert.Equal("(localdb)\\NEWERP_AutoAcceptance", t.DataSource, ignoreCase: true);
        Assert.StartsWith("NEWERP_AUTOTEST", t.InitialCatalog);
        Assert.True(t.IntegratedSecurity);
    }
    [Fact]
    public async Task NonemptyReceivable_MatchedSnapshot_PreservesCurrencyTotalsAcrossPages()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var menu = await db.SysMenus.FirstOrDefaultAsync(m => m.MenuCode == "customer" && !m.IsDeleted);
        if (menu is null)
        {
            Guard(); menu = new SysMenu { MenuCode = "customer", MenuName = "Isolated acceptance customer", MenuType = MenuType.Menu };
            db.SysMenus.Add(menu); await db.SaveChangesAsync();
        }
        var role = await db.SysUserRoles.Where(r => r.UserId == _fixture.PrivilegedUserId).Select(r => r.RoleId).FirstAsync();
        if (!await db.SysRoleMenus.AnyAsync(r => r.RoleId == role && r.MenuId == menu.Id))
        {
            Guard(); db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role, MenuId = menu.Id }); await db.SaveChangesAsync();
        }
        var customer = await db.BaseCustomers.FirstOrDefaultAsync(c => c.CustomerCode == "ACCEPT-RECV-C");
        if (customer is null)
        {
            Guard(); customer = new BaseCustomer { CustomerCode = "ACCEPT-RECV-C", CustomerName = "Isolated receivable acceptance", Status = 1 };
            db.BaseCustomers.Add(customer); await db.SaveChangesAsync();
        }
        foreach (var pair in new[] { ("ACCEPT-RECV-USD1", "USD", 100m), ("ACCEPT-RECV-USD2", "USD", 200m), ("ACCEPT-RECV-EUR1", "EUR", 50m) })
        {
            if (await db.CustomerSalesInvoiceEvidences.AnyAsync(i => i.InvoiceNumber == pair.Item1)) continue;
            Guard(); db.CustomerSalesInvoiceEvidences.Add(new CustomerSalesInvoiceEvidence
            {
                InvoiceType = "Invoice", InvoiceNumber = pair.Item1, NormalizedInvoiceNumber = pair.Item1.Replace("-", ""),
                CustomerId = customer.Id, CustomerCode = customer.CustomerCode, CustomerName = customer.CustomerName,
                InvoiceDate = new DateTime(2026, 10, 4), Currency = pair.Item2,
                NetAmount = pair.Item3, GrossAmount = pair.Item3, Status = CustomerSalesInvoiceEvidenceRules.StatusRecorded
            }); await db.SaveChangesAsync();
        }
        IReadOnlyList<IReportConfigurationDatasetProvider> providers = new IReportConfigurationDatasetProvider[]
        { new ReceivableReportConfigurationDatasetProvider(new DynamicReceivableReportQuery(db), db) };
        var definitions = new ReportConfigurationService(db, new ReportConfigurationCatalog(providers));
        var execution = new ReportConfigurationExecutionService(db, providers);
        Guard();
        var saved = await definitions.CreateAsync(_fixture.PrivilegedUserId, new ReportConfigurationSaveDto
        {
            Name = "Receivable matched SQL " + Guid.NewGuid().ToString("N"),
            Definition = new ReportConfigurationDefinition
            {
                SchemaVersion = 1, DatasetKey = "receivable", Fields = new() { "invoiceNumber", "currency", "grossAmount", "effectiveAmount", "remainingAmount" },
                Filters = new() { new() { FieldKey = "customerId", Operator = "eq", Value = customer.Id } },
                Grouping = new() { "customer" }, Capabilities = new(), Coverage = "matched-set",
                Aggregates = new() { new() { FieldKey = "grossAmount", Function = "sum" } }
            }
        });
        foreach (var page in new[] { 1, 2, 3 })
        {
            var preview = await execution.PreviewAsync(_fixture.PrivilegedUserId, new ReportConfigurationPreviewRequest
            { ConfigurationId = saved.Id, Page = page, PageSize = 1 });
            Assert.Single(preview.Rows);
            Assert.Equal(3, preview.MatchedCount);
            var metric = Assert.Single(preview.Metrics);
            Assert.Equal(300m, Assert.Single(metric.Cells.Where(c => c.Currency == "USD")).Value);
            Assert.Equal(50m, Assert.Single(metric.Cells.Where(c => c.Currency == "EUR")).Value);
            var group = Assert.Single(preview.Groups!);
            var usd = Assert.Single(group.Partitions.Where(p => p.Currency == "USD"));
            var eur = Assert.Single(group.Partitions.Where(p => p.Currency == "EUR"));
            Assert.Equal(2, usd.Count); Assert.Equal(300m, usd.GrossAmount);
            Assert.Equal(1, eur.Count); Assert.Equal(50m, eur.GrossAmount);
            Assert.Equal(0m, usd.EffectiveAllocatedAmount); Assert.Equal(300m, usd.RemainingAmount);
            Assert.Equal(0m, eur.EffectiveAllocatedAmount); Assert.Equal(50m, eur.RemainingAmount);
        }
    }
}
