using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using ERP.Infrastructure.Reports;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-306 出口字段完整度 / 单证中心迁移的单元测试：覆盖两个受控数据集的目录暴露（有限字段 / 币种口径 / 菜单码）、
/// 商品完整度预览复用既有 BuildRow / 分组规则、单证中心表头 / 明细行粒度区分与表头金额不在明细聚合中重复、
/// 菜单撤销 / 受限制数据范围 fail closed，以及迁移登记册 parity 边界（三预设就绪且非 parity-passed）。
/// <para>全部使用内存数据库（TestDbFactory），不连接 SQL Server、不执行 SQL / seed。</para>
/// </summary>
public class ReportConfigurationDocumentQualityMigrationTests
{
    private static SysUser SeedUser(ErpDbContext db, string userName)
    {
        var user = new SysUser
        {
            UserName = userName,
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = userName,
            Status = UserStatus.Enabled,
        };
        db.SysUsers.Add(user);
        db.SaveChanges();
        return user;
    }

    private static SysRole SeedRole(ErpDbContext db, string code, bool isSystem = false)
    {
        var role = new SysRole { RoleName = code, RoleCode = code, IsSystem = isSystem };
        db.SysRoles.Add(role);
        db.SaveChanges();
        return role;
    }

    private static void SeedUserRole(ErpDbContext db, long userId, long roleId)
    {
        db.SysUserRoles.Add(new SysUserRole { UserId = userId, RoleId = roleId });
        db.SaveChanges();
    }

    private static SysMenu SeedMenu(ErpDbContext db, string code)
    {
        var menu = new SysMenu { MenuName = code, MenuCode = code, MenuType = MenuType.Menu };
        db.SysMenus.Add(menu);
        db.SaveChanges();
        return menu;
    }

    private static void SeedRoleMenu(ErpDbContext db, long roleId, long menuId)
    {
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = roleId, MenuId = menuId });
        db.SaveChanges();
    }

    private static SysUser SeedAuthorizedUser(ErpDbContext db, string name, params string[] menuCodes)
    {
        var user = SeedUser(db, name);
        var role = SeedRole(db, name + "-role", isSystem: true);
        SeedUserRole(db, user.Id, role.Id);
        foreach (var code in menuCodes)
            SeedRoleMenu(db, role.Id, SeedMenu(db, code).Id);
        return user;
    }

    private static SysUser SeedRestrictedAuthorizedUser(ErpDbContext db, string name, params string[] menuCodes)
    {
        var user = SeedUser(db, name);
        var role = SeedRole(db, name + "-restricted", isSystem: false);
        SeedUserRole(db, user.Id, role.Id);
        foreach (var code in menuCodes)
            SeedRoleMenu(db, role.Id, SeedMenu(db, code).Id);
        return user;
    }

    private static IReportConfigurationDatasetProvider BuildProvider(ErpDbContext db, string datasetKey)
        => datasetKey switch
        {
            ReportConfigurationConstants.DatasetProductExportFieldCompleteness => new ProductExportCompletenessReportConfigurationDatasetProvider(db),
            ReportConfigurationConstants.DatasetTradeDocument => new TradeDocumentReportConfigurationDatasetProvider(db),
            _ => throw new ArgumentOutOfRangeException(nameof(datasetKey)),
        };

    private static ReportConfigurationCatalog BuildCatalog(ErpDbContext db)
        => new(new IReportConfigurationDatasetProvider[]
        {
            new ProductExportCompletenessReportConfigurationDatasetProvider(db),
            new TradeDocumentReportConfigurationDatasetProvider(db),
        });

    private static ReportConfigurationDefinition Definition(string datasetKey, params string[] fields)
        => new()
        {
            SchemaVersion = ReportConfigurationRules.CurrentSchemaVersion,
            DatasetKey = datasetKey,
            Fields = fields.ToList(),
        };

    private static ReportConfigurationPreviewParameters Params()
        => new(1, 100, ReportConfigurationConstants.GroupNone, null, null);

    private static List<string> ProductFields()
        => new() { "productId", "productCode", "productName", "spec", "unit", "completeness", "gapCount", "fieldCount", "englishDeclareName", "packageUnit" };

    private static List<string> TradeHeaderFields()
        => new() { "docNo", "docType", "status", "issueDate", "customerName", "salesOrderNo", "refNo", "declareNo", "amount", "currency", "departurePort", "destinationPort", "issuedBy", "copies", "fileNote", "remark" };

    private static List<string> TradeLineFields()
        => new() { "docNo", "amount", "lineNo", "productCode", "quantity", "unit", "unitPrice", "lineCurrency", "lineAmount", "packageCount", "netWeight", "grossWeight" };

    private static BaseProduct SeedProduct(ErpDbContext db, long id, string code, string name,
        string englishDeclareName = "", string packageUnit = "", int unitsPerPackage = 0,
        decimal outerLength = 0m, decimal outerWidth = 0m, decimal outerHeight = 0m, decimal outerWeight = 0m,
        decimal refundRate = 0m)
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
            Status = 1,
        }).Entity;

    private static TradeDocument SeedDocument(ErpDbContext db, long id, string docNo, string docType,
        decimal amount, string currency = "USD", DateTime? issueDate = null)
        => db.TradeDocuments.Add(new TradeDocument
        {
            Id = id,
            DocNo = docNo,
            DocType = docType,
            CustomerName = "客户甲",
            Amount = amount,
            Currency = currency,
            IssueDate = issueDate ?? DateTime.Today,
            Status = "待制作",
            Copies = 1,
        }).Entity;

    private static void SeedItem(ErpDbContext db, long id, long docId, int lineNo, string productCode,
        decimal quantity, string unit, decimal unitPrice = 0m, decimal lineAmount = 0m, string currency = "USD",
        int? packageCount = null, decimal? netWeight = null, decimal? grossWeight = null)
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
            PackageCount = packageCount,
            NetWeight = netWeight,
            GrossWeight = grossWeight,
        });

    // ==================== 1. 目录暴露与字段 / 币种口径 ====================

    [Fact]
    public async Task 目录_两个数据集全部暴露且字段白名单与币种口径真实()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "doc-quality", "product", "doc-center");
        var catalog = BuildCatalog(db);

        var result = await catalog.GetCatalogAsync(user.Id);
        Assert.Equal(2, result.Datasets.Count);

        var product = Assert.Single(result.Datasets, d => d.DatasetKey == ReportConfigurationConstants.DatasetProductExportFieldCompleteness);
        Assert.Equal("只读字段完整度（无金额/币种）", product.CurrencyUnitSemantics);
        Assert.Equal("product", product.RequiredMenuCode);
        Assert.Equal(17, product.Fields.Count);
        Assert.Contains(product.Fields, f => f.Key == "englishDeclareName" && f.Type == ReportConfigurationConstants.TypeEnum);
        Assert.DoesNotContain(product.Fields, f => f.Key == "amount");

        var trade = Assert.Single(result.Datasets, d => d.DatasetKey == ReportConfigurationConstants.DatasetTradeDocument);
        Assert.Equal("金额按原币呈现；数量按基础单位；不跨币种换算或合并", trade.CurrencyUnitSemantics);
        Assert.Equal("doc-center", trade.RequiredMenuCode);
        Assert.Equal(31, trade.Fields.Count);
        var amount = Assert.Single(trade.Fields, f => f.Key == "amount");
        Assert.Equal("原币金额", amount.CurrencyUnit);
        Assert.True(amount.Aggregatable);
        Assert.Contains(trade.Fields, f => f.Key == "lineAmount" && f.Type == ReportConfigurationConstants.TypeNumber);
    }

    [Fact]
    public async Task 目录_未授权菜单_对应数据集不暴露()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "product-only", "product");
        var catalog = BuildCatalog(db);

        var result = await catalog.GetCatalogAsync(user.Id);
        var dataset = Assert.Single(result.Datasets);
        Assert.Equal(ReportConfigurationConstants.DatasetProductExportFieldCompleteness, dataset.DatasetKey);
    }

    // ==================== 2. 商品完整度：复用 BuildRow / 分组规则 ====================

    [Fact]
    public async Task 商品完整度_预览与既有规则逐行一致()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "product-preview", "product");
        SeedProduct(db, 1, "P-FULL", "保温杯",
            englishDeclareName: "Insulated Bottle", packageUnit: "箱", unitsPerPackage: 12,
            outerLength: 30m, outerWidth: 20m, outerHeight: 40m, outerWeight: 5m, refundRate: 13m);
        SeedProduct(db, 2, "P-SPARSE", "稀疏商品");
        await db.SaveChangesAsync();

        var provider = BuildProvider(db, ReportConfigurationConstants.DatasetProductExportFieldCompleteness);
        var preview = await provider.PreviewAsync(
            Definition(ReportConfigurationConstants.DatasetProductExportFieldCompleteness, ProductFields().ToArray()),
            Params(), user.Id);

        Assert.Equal(2, preview.Total);
        Assert.Equal(2, preview.Rows.Count);

        var full = preview.Rows.Single(r => (long)r["productId"]! == 1L);
        Assert.Equal(ProductExportFieldCompletenessRules.GroupComplete, (string)full["completeness"]!);
        Assert.Equal(0, (int)full["gapCount"]!);
        Assert.Equal(ProductExportFieldCompletenessRules.StatePresent, (string)full["englishDeclareName"]!);

        var sparse = preview.Rows.Single(r => (long)r["productId"]! == 2L);
        Assert.Equal(ProductExportFieldCompletenessRules.GroupIncomplete, (string)sparse["completeness"]!);
        Assert.Equal(ProductExportFieldCompletenessRules.StateBlank, (string)sparse["englishDeclareName"]!);
    }

    [Fact]
    public async Task 商品完整度_分组筛选复用既有分组规则()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "product-group", "product");
        SeedProduct(db, 1, "P-FULL", "保温杯",
            englishDeclareName: "Insulated Bottle", packageUnit: "箱", unitsPerPackage: 12,
            outerLength: 30m, outerWidth: 20m, outerHeight: 40m, outerWeight: 5m, refundRate: 13m);
        SeedProduct(db, 2, "P-NO-EN", "无英文名", packageUnit: "箱", unitsPerPackage: 12);
        await db.SaveChangesAsync();

        var definition = Definition(ReportConfigurationConstants.DatasetProductExportFieldCompleteness, ProductFields().ToArray());
        definition.Filters = new List<ReportConfigurationFilter>
        {
            new() { FieldKey = "group", Operator = ReportConfigurationConstants.OperatorEq, Value = ProductExportFieldCompletenessRules.GroupDeclaration },
        };

        var preview = await BuildProvider(db, ReportConfigurationConstants.DatasetProductExportFieldCompleteness)
            .PreviewAsync(definition, Params(), user.Id);

        var row = Assert.Single(preview.Rows);
        Assert.Equal(2L, (long)row["productId"]!);
    }

    // ==================== 3. 单证中心：表头 / 明细行粒度与 null 快照 ====================

    [Fact]
    public async Task 单证中心_表头粒度预览与持久快照一致()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "trade-header", "doc-center");
        SeedDocument(db, 1, "TD-1", "商业发票", 200m, "USD");
        SeedItem(db, 101, 1, 1, "P1", 2m, "箱", unitPrice: 10m, lineAmount: 20m);
        SeedItem(db, 102, 1, 2, "P2", 3m, "箱", unitPrice: 5m, lineAmount: 15m);
        await db.SaveChangesAsync();

        var preview = await BuildProvider(db, ReportConfigurationConstants.DatasetTradeDocument)
            .PreviewAsync(Definition(ReportConfigurationConstants.DatasetTradeDocument, TradeHeaderFields().ToArray()),
                Params(), user.Id);

        Assert.Equal(1, preview.Total);
        var row = Assert.Single(preview.Rows);
        Assert.Equal("TD-1", (string)row["docNo"]!);
        Assert.Equal("商业发票", (string)row["docType"]!);
        Assert.Equal(200m, (decimal)row["amount"]!);
        Assert.Equal("USD", (string)row["currency"]!);
        Assert.Equal(DateTime.Today, (DateTime)row["issueDate"]!);
        Assert.Equal("客户甲", (string)row["customerName"]!);
    }

    [Fact]
    public async Task 单证中心_明细行粒度_表头金额不重复且行金额正确()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "trade-line", "doc-center");
        SeedDocument(db, 1, "TD-1", "商业发票", 200m, "USD");
        SeedItem(db, 101, 1, 1, "P1", 2m, "箱", unitPrice: 10m, lineAmount: 20m);
        SeedItem(db, 102, 1, 2, "P2", 3m, "箱", unitPrice: 5m, lineAmount: 15m);
        await db.SaveChangesAsync();

        var preview = await BuildProvider(db, ReportConfigurationConstants.DatasetTradeDocument)
            .PreviewAsync(Definition(ReportConfigurationConstants.DatasetTradeDocument, TradeLineFields().ToArray()),
                Params(), user.Id);

        Assert.Equal(2, preview.Total);
        Assert.Equal(2, preview.Rows.Count);

        foreach (var line in preview.Rows)
        {
            Assert.Null(line["amount"]); // 表头金额绝不在明细行重复
            Assert.Equal("USD", (string)line["lineCurrency"]!);
            Assert.Null(line["packageCount"]);
        }

        var line1 = preview.Rows.Single(r => (int)r["lineNo"]! == 1);
        Assert.Equal("P1", (string)line1["productCode"]!);
        Assert.Equal(10m, (decimal)line1["unitPrice"]!);
        Assert.Equal(20m, (decimal)line1["lineAmount"]!);

        var line2 = preview.Rows.Single(r => (int)r["lineNo"]! == 2);
        Assert.Equal(15m, (decimal)line2["lineAmount"]!);
    }

    [Fact]
    public async Task 单证中心_装箱单明细行null快照保留且无价格口径()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "trade-null", "doc-center");
        SeedDocument(db, 2, "TD-2", "装箱单", 0m, "USD");
        SeedItem(db, 201, 2, 1, "P1", 10m, "箱", packageCount: null, netWeight: null, grossWeight: null);
        SeedItem(db, 202, 2, 2, "P2", 5m, "箱", packageCount: 10, netWeight: 100m, grossWeight: 110m);
        await db.SaveChangesAsync();

        var preview = await BuildProvider(db, ReportConfigurationConstants.DatasetTradeDocument)
            .PreviewAsync(Definition(ReportConfigurationConstants.DatasetTradeDocument, TradeLineFields().ToArray()),
                Params(), user.Id);

        var line1 = preview.Rows.Single(r => (int)r["lineNo"]! == 1);
        Assert.Null(line1["packageCount"]);
        Assert.Null(line1["netWeight"]);
        Assert.Null(line1["grossWeight"]);
        Assert.Null(line1["unitPrice"]);
        Assert.Null(line1["lineAmount"]);

        var line2 = preview.Rows.Single(r => (int)r["lineNo"]! == 2);
        Assert.Equal(10, (int)line2["packageCount"]!);
        Assert.Equal(100m, (decimal)line2["netWeight"]!);
        Assert.Equal(110m, (decimal)line2["grossWeight"]!);
    }

    // ==================== 4. 授权 / 数据范围 fail closed ====================

    [Theory]
    [InlineData(ReportConfigurationConstants.DatasetProductExportFieldCompleteness, "product")]
    [InlineData(ReportConfigurationConstants.DatasetTradeDocument, "doc-center")]
    public async Task 受限制用户_有菜单_数据集不暴露且预览拒绝(string datasetKey, string menuCode)
    {
        using var db = TestDbFactory.Create();
        var user = SeedRestrictedAuthorizedUser(db, "restricted-" + datasetKey, menuCode);
        var provider = BuildProvider(db, datasetKey);

        Assert.Null(await provider.GetDatasetAsync(user.Id));

        var ex = await Assert.ThrowsAsync<BusinessException>(() => provider.PreviewAsync(
            Definition(datasetKey, DefaultFields(datasetKey).ToArray()), Params(), user.Id));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    [Theory]
    [InlineData(ReportConfigurationConstants.DatasetProductExportFieldCompleteness, "product")]
    [InlineData(ReportConfigurationConstants.DatasetTradeDocument, "doc-center")]
    public async Task 菜单撤销后_数据集不暴露且预览拒绝(string datasetKey, string menuCode)
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "revoke-" + datasetKey, menuCode);
        var provider = BuildProvider(db, datasetKey);

        Assert.NotNull(await provider.GetDatasetAsync(user.Id));

        var link = Assert.Single(db.SysRoleMenus.ToList());
        db.SysRoleMenus.Remove(link);
        db.SaveChanges();

        Assert.Null(await provider.GetDatasetAsync(user.Id));

        var ex = await Assert.ThrowsAsync<BusinessException>(() => provider.PreviewAsync(
            Definition(datasetKey, DefaultFields(datasetKey).ToArray()), Params(), user.Id));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    [Fact]
    public async Task 无身份_预览未认证拒绝()
    {
        using var db = TestDbFactory.Create();
        var provider = BuildProvider(db, ReportConfigurationConstants.DatasetTradeDocument);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => provider.PreviewAsync(
            Definition(ReportConfigurationConstants.DatasetTradeDocument, TradeHeaderFields().ToArray()),
            Params(), null));
        Assert.Equal(ErrorCodes.Unauthorized, ex.Code);
    }

    // ==================== 5. 迁移登记册 parity 边界 ====================

    [Fact]
    public async Task 迁移登记册_三预设就绪且非parity通过()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "registry", "product", "doc-center");
        var registry = new ReportMigrationRegistry(
            BuildCatalog(db), db, new ReportMigrationPresetCatalog(), new EmptyReportMigrationParityEvidenceProvider());

        var result = await registry.GetRegistryAsync(user.Id);

        var product = Assert.Single(result.Entries, e => e.LegacyKey == "export:product-export-field-completeness");
        Assert.Equal(ReportMigrationParityStatusText.PresetReady, product.ParityStatus);

        var print = Assert.Single(result.Entries, e => e.LegacyKey == "document:trade-document-print");
        Assert.Equal(ReportMigrationParityStatusText.PresetReady, print.ParityStatus);

        var excel = Assert.Single(result.Entries, e => e.LegacyKey == "document:trade-document-export-excel");
        Assert.Equal(ReportMigrationParityStatusText.PresetReady, excel.ParityStatus);
    }

    private static List<string> DefaultFields(string datasetKey)
        => datasetKey == ReportConfigurationConstants.DatasetProductExportFieldCompleteness
            ? ProductFields()
            : TradeHeaderFields();
}
