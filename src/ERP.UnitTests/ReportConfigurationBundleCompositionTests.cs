using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using ERP.Infrastructure.Export;
using ERP.Infrastructure.Reports;
using Microsoft.EntityFrameworkCore;
using System.Text;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-316 Stage 2 表头 / 明细组合单元测试（内存数据库，不连接 SQL Server / 不执行 SQL）：
/// 覆盖服务端声明场景组合、父项一次呈现与有序明细、独立表头金额与明细按币种 / 单位分区、空明细证据、
/// 孤儿行 / 缺失外键 / 菜单撤销 / 未知场景 / 缺失组合字段的显式失败，以及组合 Excel / PDF 导出。
/// </summary>
public class ReportConfigurationBundleCompositionTests
{
    // ==================== 脚手架 ====================

    private static SysUser SeedAuthorizedUser(ErpDbContext db, string name, params string[] menuCodes)
    {
        var user = new SysUser
        {
            UserName = name,
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = name,
            Status = UserStatus.Enabled,
        };
        db.SysUsers.Add(user);
        db.SaveChanges();

        var role = new SysRole
        {
            RoleName = name + "-role",
            RoleCode = name + "-role-" + Guid.NewGuid().ToString("N"),
            IsSystem = true,
        };
        db.SysRoles.Add(role);
        db.SaveChanges();

        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        db.SaveChanges();

        foreach (var code in menuCodes)
        {
            var menu = new SysMenu { MenuName = code, MenuCode = code, MenuType = MenuType.Menu };
            db.SysMenus.Add(menu);
            db.SaveChanges();
            db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
            db.SaveChanges();
        }

        return user;
    }

    private static void SeedDocument(ErpDbContext db, long id, string docNo, string docType, decimal amount, string currency)
        => db.TradeDocuments.Add(new TradeDocument
        {
            Id = id,
            DocNo = docNo,
            DocType = docType,
            CustomerName = "客户甲",
            Amount = amount,
            Currency = currency,
            IssueDate = DateTime.Today,
            Status = "待制作",
            Copies = 1,
        });

    private static void SeedItem(ErpDbContext db, long id, long docId, int lineNo, string productCode,
        decimal quantity, string unit, decimal unitPrice = 0m, decimal lineAmount = 0m, string currency = "USD")
        => db.TradeDocumentItems.Add(new TradeDocumentItem
        {
            Id = id,
            TradeDocumentId = docId,
            LineNo = lineNo,
            ProductCode = productCode,
            ProductNameCn = productCode,
            Quantity = quantity,
            Unit = unit,
            UnitPrice = unitPrice,
            LineAmount = lineAmount,
            Currency = currency,
        });

    private static ReportConfigurationDefinition Definition(string datasetKey, params string[] fields)
        => new()
        {
            SchemaVersion = ReportConfigurationRules.CurrentSchemaVersion,
            DatasetKey = datasetKey,
            Fields = fields.ToList(),
            Grouping = new List<string> { ReportConfigurationConstants.GroupNone },
        };

    private static (
        IReportConfigurationExecutionService Execution,
        IReportConfigurationService Configs,
        ReportConfigurationBundleService Bundle) BuildServices(ErpDbContext db)
    {
        var providers = new IReportConfigurationDatasetProvider[]
        {
            new TradeDocumentReportConfigurationDatasetProvider(db),
        };
        var catalog = new ReportConfigurationCatalog(providers);
        var execution = new ReportConfigurationExecutionService(db, providers);
        var configs = new ReportConfigurationService(db, catalog);
        var bundle = new ReportConfigurationBundleService(execution);
        return (execution, configs, bundle);
    }

    private static async Task<(long HeaderId, long DetailId)> CreateCompositionPairAsync(
        IReportConfigurationService configs, long userId)
    {
        var header = await configs.CreateAsync(userId, new ReportConfigurationSaveDto
        {
            Name = "组合表头",
            Definition = Definition(ReportConfigurationConstants.DatasetTradeDocument, "id", "docNo", "docType", "amount", "currency"),
        });
        var detail = await configs.CreateAsync(userId, new ReportConfigurationSaveDto
        {
            Name = "组合明细",
            Definition = Definition(ReportConfigurationConstants.DatasetTradeDocument, "id", "lineNo", "productCode", "quantity", "unit", "lineAmount", "lineCurrency"),
        });
        return (header.Id, detail.Id);
    }

    [Theory]
    [InlineData(51, 0)]
    [InlineData(1, 101)]
    public async Task Composition_rejects_truncated_header_or_detail_page(int parentCount, int detailCount)
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "compose-truncated", "doc-center");
        for (var i = 1; i <= parentCount; i++)
            SeedDocument(db, i, "TD-BOUND-" + i, "invoice", 200m, "USD");
        for (var i = 1; i <= detailCount; i++)
            SeedItem(db, 1000 + i, 1, i, "P" + i, 1m, "PCS", lineAmount: 1m);
        await db.SaveChangesAsync();
        var (_, configs, bundle) = BuildServices(db);
        var (headerId, detailId) = await CreateCompositionPairAsync(configs, user.Id);
        var error = await Assert.ThrowsAsync<BusinessException>(() => bundle.ComposePreviewAsync(user.Id,
            new ReportConfigurationBundleCompositionRequest
            {
                CompositionKey = ReportConfigurationBundleCompositionManifest.TradeDocumentHeaderDetail,
                HeaderConfigurationId = headerId,
                DetailConfigurationId = detailId,
            }));
        Assert.Equal(ReportConfigurationExecutionLimits.ErrorCodeResultTooLarge, error.Code);
    }

    // ==================== 1. 组合与独立合计 ====================

    [Fact]
    public async Task 组合_两个父项不同币种单位多行_表头一次明细有序且合计分区()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "compose", "doc-center");
        SeedDocument(db, 1, "TD-USD", "商业发票", 200m, "USD");
        SeedItem(db, 101, 1, 1, "P1", 2m, "箱", unitPrice: 10m, lineAmount: 20m);
        SeedItem(db, 102, 1, 2, "P2", 3m, "箱", unitPrice: 5m, lineAmount: 15m);
        SeedDocument(db, 2, "TD-EUR", "商业发票", 50m, "EUR");
        SeedItem(db, 201, 2, 1, "P3", 4m, "个", unitPrice: 10m, lineAmount: 40m, currency: "EUR");
        await db.SaveChangesAsync();

        var (_, configs, bundle) = BuildServices(db);
        var (headerId, detailId) = await CreateCompositionPairAsync(configs, user.Id);

        var result = await bundle.ComposePreviewAsync(user.Id, new ReportConfigurationBundleCompositionRequest
        {
            CompositionKey = ReportConfigurationBundleCompositionManifest.TradeDocumentHeaderDetail,
            HeaderConfigurationId = headerId,
            DetailConfigurationId = detailId,
        });

        Assert.Equal(2, result.ParentCount);
        Assert.Equal(3, result.DetailCount);

        var usd = Assert.Single(result.Parents, p => (string)p.Header["docNo"]! == "TD-USD");
        Assert.Equal(2, usd.Details.Count);
        Assert.Equal(1, (int)usd.Details[0]["lineNo"]!);
        Assert.Equal(2, (int)usd.Details[1]["lineNo"]!);
        Assert.Equal(200m, usd.Totals.HeaderAmount);
        Assert.Equal("USD", usd.Totals.HeaderCurrency);
        var usdAmount = Assert.Single(usd.Totals.DetailAmounts);
        Assert.Equal("USD", usdAmount.Currency);
        Assert.Equal(35m, usdAmount.Amount);
        Assert.Equal(2, usdAmount.Count);
        var usdQuantity = Assert.Single(usd.Totals.DetailQuantities);
        Assert.Equal("箱", usdQuantity.Unit);
        Assert.Equal(5m, usdQuantity.Quantity);
        Assert.Equal(2, usdQuantity.Count);

        var eur = Assert.Single(result.Parents, p => (string)p.Header["docNo"]! == "TD-EUR");
        Assert.Single(eur.Details);
        Assert.Equal(50m, eur.Totals.HeaderAmount);
        Assert.Equal("EUR", eur.Totals.HeaderCurrency);
        var eurAmount = Assert.Single(eur.Totals.DetailAmounts);
        Assert.Equal("EUR", eurAmount.Currency);
        Assert.Equal(40m, eurAmount.Amount);
        var eurQuantity = Assert.Single(eur.Totals.DetailQuantities);
        Assert.Equal("个", eurQuantity.Unit);
        Assert.Equal(4m, eurQuantity.Quantity);
    }

    [Fact]
    public async Task 组合_空明细_显式证据且不失败()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "compose-empty", "doc-center");
        SeedDocument(db, 3, "TD-EMPTY", "装箱单", 0m, "USD");
        await db.SaveChangesAsync();

        var (_, configs, bundle) = BuildServices(db);
        var (headerId, detailId) = await CreateCompositionPairAsync(configs, user.Id);

        var result = await bundle.ComposePreviewAsync(user.Id, new ReportConfigurationBundleCompositionRequest
        {
            CompositionKey = ReportConfigurationBundleCompositionManifest.TradeDocumentHeaderDetail,
            HeaderConfigurationId = headerId,
            DetailConfigurationId = detailId,
        });

        var empty = Assert.Single(result.Parents);
        Assert.False(empty.HasDetails);
        Assert.NotNull(empty.EmptyDetailsEvidence);
        Assert.Empty(empty.Details);
        Assert.Empty(empty.Totals.DetailAmounts);
        Assert.Empty(empty.Totals.DetailQuantities);
    }


    // ==================== 2. 授权 / 不兼容 / 孤儿行 fail closed ====================

    [Fact]
    public async Task 组合_菜单撤销后_整体失败()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "compose-revoke", "doc-center");
        SeedDocument(db, 1, "TD-R", "商业发票", 10m, "USD");
        await db.SaveChangesAsync();

        var (_, configs, bundle) = BuildServices(db);
        var (headerId, detailId) = await CreateCompositionPairAsync(configs, user.Id);

        var link = Assert.Single(db.SysRoleMenus.ToList());
        db.SysRoleMenus.Remove(link);
        db.SaveChanges();

        var ex = await Assert.ThrowsAsync<BusinessException>(() => bundle.ComposePreviewAsync(user.Id,
            new ReportConfigurationBundleCompositionRequest
            {
                CompositionKey = ReportConfigurationBundleCompositionManifest.TradeDocumentHeaderDetail,
                HeaderConfigurationId = headerId,
                DetailConfigurationId = detailId,
            }));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    [Fact]
    public async Task 组合_明细定义缺外键字段_不兼容拒绝()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "compose-missing-fk", "doc-center");
        SeedDocument(db, 1, "TD-FK", "商业发票", 10m, "USD");
        SeedItem(db, 101, 1, 1, "P1", 1m, "箱", lineAmount: 5m);
        await db.SaveChangesAsync();

        var (_, configs, bundle) = BuildServices(db);
        var header = await configs.CreateAsync(user.Id, new ReportConfigurationSaveDto
        {
            Name = "组合表头",
            Definition = Definition(ReportConfigurationConstants.DatasetTradeDocument, "id", "docNo", "amount", "currency"),
        });
        var detail = await configs.CreateAsync(user.Id, new ReportConfigurationSaveDto
        {
            Name = "组合明细缺外键",
            Definition = Definition(ReportConfigurationConstants.DatasetTradeDocument, "lineNo", "productCode", "quantity", "unit", "lineAmount", "lineCurrency"),
        });

        var ex = await Assert.ThrowsAsync<BusinessException>(() => bundle.ComposePreviewAsync(user.Id,
            new ReportConfigurationBundleCompositionRequest
            {
                CompositionKey = ReportConfigurationBundleCompositionManifest.TradeDocumentHeaderDetail,
                HeaderConfigurationId = header.Id,
                DetailConfigurationId = detail.Id,
            }));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
    }

    [Fact]
    public async Task 组合_未知场景键_拒绝()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "compose-unknown", "doc-center");

        var (_, _, bundle) = BuildServices(db);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => bundle.ComposePreviewAsync(user.Id,
            new ReportConfigurationBundleCompositionRequest
            {
                CompositionKey = "not-a-scenario",
                HeaderConfigurationId = 1,
                DetailConfigurationId = 2,
            }));
        Assert.Equal(ErrorCodes.NotFound, ex.Code);
    }


    [Fact]
    public async Task 组合_孤儿行_显式失败()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "compose-orphan", "doc-center");
        SeedDocument(db, 1, "TD-1", "商业发票", 100m, "USD");
        SeedItem(db, 101, 1, 1, "P1", 1m, "箱", lineAmount: 10m);
        SeedDocument(db, 2, "TD-2", "商业发票", 20m, "USD");
        SeedItem(db, 201, 2, 1, "P2", 2m, "箱", lineAmount: 20m);
        await db.SaveChangesAsync();

        var (_, configs, bundle) = BuildServices(db);

        // 表头节只选单证 1，明细节不筛选 → 单证 2 的明细成为孤儿行。
        var header = await configs.CreateAsync(user.Id, new ReportConfigurationSaveDto
        {
            Name = "组合表头-仅单证1",
            Definition = new ReportConfigurationDefinition
            {
                SchemaVersion = ReportConfigurationRules.CurrentSchemaVersion,
                DatasetKey = ReportConfigurationConstants.DatasetTradeDocument,
                Fields = new List<string> { "id", "docNo", "amount", "currency" },
                Filters = new List<ReportConfigurationFilter>
                {
                    new() { FieldKey = "id", Operator = ReportConfigurationConstants.OperatorEq, Value = 1 },
                },
                Grouping = new List<string> { ReportConfigurationConstants.GroupNone },
            },
        });
        var detail = await configs.CreateAsync(user.Id, new ReportConfigurationSaveDto
        {
            Name = "组合明细-全量",
            Definition = Definition(ReportConfigurationConstants.DatasetTradeDocument, "id", "lineNo", "productCode", "quantity", "unit", "lineAmount", "lineCurrency"),
        });

        var ex = await Assert.ThrowsAsync<BusinessException>(() => bundle.ComposePreviewAsync(user.Id,
            new ReportConfigurationBundleCompositionRequest
            {
                CompositionKey = ReportConfigurationBundleCompositionManifest.TradeDocumentHeaderDetail,
                HeaderConfigurationId = header.Id,
                DetailConfigurationId = detail.Id,
            }));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("孤儿行", ex.Message);
    }


    // ==================== 3. 场景清单与导出 ====================

    [Fact]
    public void 场景清单_出口单证中心_字段键与数据集声明正确()
    {
        var scenario = ReportConfigurationBundleCompositionManifest.Find(
            ReportConfigurationBundleCompositionManifest.TradeDocumentHeaderDetail);
        Assert.NotNull(scenario);
        Assert.Equal(ReportConfigurationConstants.DatasetTradeDocument, scenario.HeaderDatasetKey);
        Assert.Equal(ReportConfigurationConstants.DatasetTradeDocument, scenario.DetailDatasetKey);
        Assert.Equal("id", scenario.ParentIdentityFieldKey);
        Assert.Equal("id", scenario.DetailForeignKeyFieldKey);
        Assert.Equal("lineNo", scenario.DetailOrderFieldKey);
        Assert.Contains("doc-center", scenario.RequiredMenuCodes);
    }

    [Fact]
    public async Task 导出_组合Excel与PDF_可生成字节流()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "compose-export", "doc-center");
        SeedDocument(db, 1, "TD-X", "商业发票", 100m, "USD");
        SeedItem(db, 101, 1, 1, "P1", 1m, "箱", lineAmount: 10m);
        await db.SaveChangesAsync();

        var (_, configs, bundle) = BuildServices(db);
        var (headerId, detailId) = await CreateCompositionPairAsync(configs, user.Id);

        using var lease = new ReportConfigurationExecutionBudget().Acquire(user.Id);
        var export = await bundle.BuildCompositionExportResultAsync(user.Id,
            new ReportConfigurationBundleCompositionRequest
            {
                CompositionKey = ReportConfigurationBundleCompositionManifest.TradeDocumentHeaderDetail,
                HeaderConfigurationId = headerId,
                DetailConfigurationId = detailId,
            }, lease);

        var excel = new ReportConfigurationBundleExcelExporter().BuildComposed(export.Preview);
        Assert.True(excel.Length > 0);
        Assert.Equal((byte)'P', excel[0]);
        Assert.Equal((byte)'K', excel[1]);

        var fontPath = SimHeiPdfFontResolver.FindFontPath();
        if (fontPath is not null)
        {
            var pdf = ReportConfigurationBundlePdfExporter.ExportComposed(export.Preview, fontPath);
            Assert.StartsWith("%PDF-", Encoding.ASCII.GetString(pdf));
        }
    }
}

