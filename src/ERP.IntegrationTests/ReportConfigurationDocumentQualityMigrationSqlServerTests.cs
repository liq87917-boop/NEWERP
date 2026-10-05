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
using Xunit;

namespace ERP.IntegrationTests;

/// <summary>
/// ERP-306 出口字段完整度 / 单证中心迁移的 SQL Server 集成测试：在专用 localdb 目标上自包含播种非空夹具
/// （商品 + 单证 + 明细行），通过受控数据集适配器预览并与既有规则 / 持久快照逐行比对，验证表头 / 明细行粒度、
/// 受限制账号 fail closed，以及通用 Excel / PDF 导出可执行。
/// <para>安全口径：每次写库前先做目标护栏，强制目标为专用 localdb 实例 <c>NEWERP_AutoAcceptance</c>
/// 且库名前缀 <c>NEWERP_AUTOTEST</c>；连接串只来自进程环境变量（<c>ERP_ConnectionStrings__Default</c>）
/// 或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据。</para>
/// </summary>
public sealed class ReportConfigurationDocumentQualityMigrationSqlServerTests
    : IClassFixture<ReportConfigurationDocumentQualityMigrationSqlServerFixture>
{
    private readonly ReportConfigurationDocumentQualityMigrationSqlServerFixture _fixture;

    public ReportConfigurationDocumentQualityMigrationSqlServerTests(ReportConfigurationDocumentQualityMigrationSqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    private void Guard()
    {
        var t = new SqlConnectionStringBuilder(_fixture.ConnectionString);
        Assert.Equal($"(localdb)\\{ReportConfigurationDocumentQualityMigrationSqlServerFixture.InstanceMarker}", t.DataSource, ignoreCase: true);
        Assert.StartsWith(ReportConfigurationDocumentQualityMigrationSqlServerFixture.DatabasePrefix, t.InitialCatalog);
        Assert.True(t.IntegratedSecurity);
    }

    [Fact]
    public async Task 商品完整度_SQLServer预览与既有规则一致()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var userId = _fixture.PrivilegedUserId;

        var provider = new ProductExportCompletenessReportConfigurationDatasetProvider(db);
        var preview = await provider.PreviewAsync(ProductDefinition(), Params(), userId);

        Assert.Equal(2, preview.Total);

        var full = preview.Rows.Single(r => (string)r["productCode"]! == "ERP306-P-FULL");
        Assert.Equal(ProductExportFieldCompletenessRules.GroupComplete, (string)full["completeness"]!);
        Assert.Equal(0, (int)full["gapCount"]!);
        Assert.Equal(ProductExportFieldCompletenessRules.StatePresent, (string)full["englishDeclareName"]!);

        var sparse = preview.Rows.Single(r => (string)r["productCode"]! == "ERP306-P-SPARSE");
        Assert.Equal(ProductExportFieldCompletenessRules.GroupIncomplete, (string)sparse["completeness"]!);
        Assert.Equal(ProductExportFieldCompletenessRules.StateBlank, (string)sparse["englishDeclareName"]!);
    }

    [Fact]
    public async Task 单证中心_SQLServer表头与明细行粒度()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var userId = _fixture.PrivilegedUserId;
        var provider = new TradeDocumentReportConfigurationDatasetProvider(db);

        var header = await provider.PreviewAsync(TradeHeaderDefinition(), Params(), userId);
        var h = Assert.Single(header.Rows);
        Assert.Equal("ERP306-TD-1", (string)h["docNo"]!);
        Assert.Equal(200m, (decimal)h["amount"]!);
        Assert.Equal("USD", (string)h["currency"]!);

        var lines = await provider.PreviewAsync(TradeLineDefinition(), Params(), userId);
        Assert.Equal(2, lines.Total);
        Assert.Equal(2, lines.Rows.Count);

        foreach (var line in lines.Rows)
        {
            Assert.Null(line["amount"]); // 表头金额绝不在明细行重复
            Assert.Equal("USD", (string)line["lineCurrency"]!);
        }

        var line1 = lines.Rows.Single(r => (int)r["lineNo"]! == 1);
        Assert.Equal("ERP306-P1", (string)line1["productCode"]!);
        Assert.Equal(20m, (decimal)line1["lineAmount"]!);

        var line2 = lines.Rows.Single(r => (int)r["lineNo"]! == 2);
        Assert.Equal(15m, (decimal)line2["lineAmount"]!);
    }

    [Fact]
    public async Task 受限制用户_SQLServer数据集不暴露且预览拒绝()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var userId = await SeedRestrictedUserAsync(db, "ERP306-RESTRICTED");

        var productProvider = new ProductExportCompletenessReportConfigurationDatasetProvider(db);
        Assert.Null(await productProvider.GetDatasetAsync(userId));
        var ex1 = await Assert.ThrowsAsync<BusinessException>(() => productProvider.PreviewAsync(
            ProductDefinition(), Params(), userId));
        Assert.Equal(ErrorCodes.Forbidden, ex1.Code);

        var tradeProvider = new TradeDocumentReportConfigurationDatasetProvider(db);
        Assert.Null(await tradeProvider.GetDatasetAsync(userId));
        var ex2 = await Assert.ThrowsAsync<BusinessException>(() => tradeProvider.PreviewAsync(
            TradeHeaderDefinition(), Params(), userId));
        Assert.Equal(ErrorCodes.Forbidden, ex2.Code);
    }

    [Fact]
    public async Task 通用Excel与PDF导出_可执行()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var userId = _fixture.PrivilegedUserId;

        var productProvider = new ProductExportCompletenessReportConfigurationDatasetProvider(db);
        var productPreview = await productProvider.PreviewAsync(ProductDefinition(), Params(), userId);
        var productExcel = new ReportConfigurationExcelExporter().Build(productPreview);
        Assert.NotEmpty(productExcel);
        Assert.Equal("PK", System.Text.Encoding.ASCII.GetString(productExcel, 0, 2));

        var tradeProvider = new TradeDocumentReportConfigurationDatasetProvider(db);
        var tradePreview = await tradeProvider.PreviewAsync(TradeHeaderDefinition(), Params(), userId);
        var tradeExcel = new ReportConfigurationExcelExporter().Build(tradePreview);
        Assert.NotEmpty(tradeExcel);

        var fontPath = SimHeiPdfFontResolver.FindFontPath();
        if (fontPath is not null)
        {
            var pdf = ReportConfigurationPdfExporter.Export(tradePreview, fontPath);
            Assert.NotEmpty(pdf);
        }
    }

    private static async Task<long> SeedRestrictedUserAsync(ErpDbContext db, string userName)
    {
        var existing = await db.SysUsers.FirstOrDefaultAsync(u => u.UserName == userName && !u.IsDeleted);
        if (existing is not null)
            return existing.Id;

        var user = new SysUser { UserName = userName, PasswordHash = "hash", PasswordSalt = "salt", DisplayName = userName, Status = UserStatus.Enabled };
        db.SysUsers.Add(user);
        await db.SaveChangesAsync();

        var role = new SysRole { RoleName = userName + "-role", RoleCode = userName + "-role", IsSystem = false };
        db.SysRoles.Add(role);
        await db.SaveChangesAsync();

        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        await db.SaveChangesAsync();

        foreach (var code in new[] { "product", "doc-center" })
        {
            var menu = await db.SysMenus.FirstOrDefaultAsync(m => m.MenuCode == code && !m.IsDeleted)
                ?? throw new InvalidOperationException($"缺少菜单种子 {code}");
            db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
            await db.SaveChangesAsync();
        }

        return user.Id;
    }

    private static ReportConfigurationDefinition ProductDefinition()
        => new()
        {
            SchemaVersion = ReportConfigurationRules.CurrentSchemaVersion,
            DatasetKey = ReportConfigurationConstants.DatasetProductExportFieldCompleteness,
            Fields = new List<string> { "productId", "productCode", "productName", "spec", "unit", "completeness", "gapCount", "fieldCount", "englishDeclareName", "packageUnit" },
        };

    private static ReportConfigurationDefinition TradeHeaderDefinition()
        => new()
        {
            SchemaVersion = ReportConfigurationRules.CurrentSchemaVersion,
            DatasetKey = ReportConfigurationConstants.DatasetTradeDocument,
            Fields = new List<string> { "docNo", "docType", "status", "issueDate", "customerName", "salesOrderNo", "refNo", "declareNo", "amount", "currency", "departurePort", "destinationPort", "issuedBy", "copies", "fileNote", "remark" },
        };

    private static ReportConfigurationDefinition TradeLineDefinition()
        => new()
        {
            SchemaVersion = ReportConfigurationRules.CurrentSchemaVersion,
            DatasetKey = ReportConfigurationConstants.DatasetTradeDocument,
            Fields = new List<string> { "docNo", "amount", "lineNo", "productCode", "quantity", "unit", "unitPrice", "lineCurrency", "lineAmount", "packageCount", "netWeight", "grossWeight" },
        };

    private static ReportConfigurationPreviewParameters Params()
        => new(1, 100, ReportConfigurationConstants.GroupNone, null, null);
}

/// <summary>
/// 专用 localdb 目标 Fixture：一次性启动序列（自动建表 + 结构升级 + 种子 + 再升级）+ 幂等非空夹具
/// （商品 + 单证 + 明细行）。写库前每次都断言目标身份（实例含 <c>NEWERP_AutoAcceptance</c>、
/// 库名前缀 <c>NEWERP_AUTOTEST</c>）。
/// </summary>
public sealed class ReportConfigurationDocumentQualityMigrationSqlServerFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";
    public const string DefaultDatabaseName = "NEWERP_AUTOTEST_ERP306";
    public static readonly DateTime UniformDate = new(2026, 9, 1);

    private const string UserName = "ERP306-ADMIN";
    private const string RoleCode = "ERP306-SYS";

    public string ConnectionString { get; private set; } = string.Empty;
    public long PrivilegedUserId { get; private set; }

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = BuildDefaultConnectionString();

        AssertDedicatedTarget(connectionString);
        ConnectionString = connectionString;

        await using (var db = CreateDbContext())
        {
            await db.Database.EnsureCreatedAsync();
            await SchemaUpgrader.EnsureUpgradedAsync(db);
            await SeedData.InitializeAsync(db);
            await SchemaUpgrader.EnsureUpgradedAsync(db);
        }

        await SeedFixtureAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public ErpDbContext CreateDbContext() => new(BuildOptions());

    private async Task SeedFixtureAsync()
    {
        AssertDedicatedTarget(ConnectionString);
        await using var db = CreateDbContext();

        var user = await EnsureUserAsync(db);
        var role = await EnsureRoleAsync(db);
        if (!await db.SysUserRoles.AnyAsync(ur => ur.UserId == user.Id && ur.RoleId == role.Id && !ur.IsDeleted))
        {
            db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
            await db.SaveChangesAsync();
        }

        foreach (var code in new[] { "product", "doc-center" })
        {
            var menu = await EnsureMenuAsync(db, code);
            if (!await db.SysRoleMenus.AnyAsync(rm => rm.RoleId == role.Id && rm.MenuId == menu.Id && !rm.IsDeleted))
            {
                db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
                await db.SaveChangesAsync();
            }
        }

        await SeedProductsAsync(db);
        await SeedTradeDocumentsAsync(db);

        PrivilegedUserId = user.Id;
    }

    private static async Task SeedProductsAsync(ErpDbContext db)
    {
        await EnsureProductAsync(db, "ERP306-P-FULL", "ERP306 保温杯", "Insulated Bottle", "箱", 12, 30m, 20m, 40m, 5m, 13m);
        await EnsureProductAsync(db, "ERP306-P-SPARSE", "ERP306 稀疏商品", "", "", 0, 0m, 0m, 0m, 0m, 0m);
    }

    private static async Task SeedTradeDocumentsAsync(ErpDbContext db)
    {
        var doc = await db.TradeDocuments.FirstOrDefaultAsync(d => d.DocNo == "ERP306-TD-1" && !d.IsDeleted);
        if (doc is null)
        {
            doc = new TradeDocument { DocNo = "ERP306-TD-1", DocType = "商业发票", CustomerName = "ERP306 客户甲", Amount = 200m, Currency = "USD", IssueDate = UniformDate, Status = "待制作", Copies = 1 };
            db.TradeDocuments.Add(doc);
            await db.SaveChangesAsync();
        }

        await EnsureTradeItemAsync(db, doc.Id, 1, "ERP306-P1", 2m, "箱", 10m, 20m, "USD", null, null, null);
        await EnsureTradeItemAsync(db, doc.Id, 2, "ERP306-P2", 3m, "箱", 5m, 15m, "USD", null, null, null);
    }

    private static async Task EnsureProductAsync(ErpDbContext db, string code, string name,
        string englishDeclareName, string packageUnit, int unitsPerPackage,
        decimal outerLength, decimal outerWidth, decimal outerHeight, decimal outerWeight, decimal refundRate)
    {
        if (await db.BaseProducts.AnyAsync(p => p.ProductCode == code && !p.IsDeleted))
            return;

        db.BaseProducts.Add(new BaseProduct
        {
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
        });
        await db.SaveChangesAsync();
    }

    private static async Task EnsureTradeItemAsync(ErpDbContext db, long docId, int lineNo, string productCode,
        decimal quantity, string unit, decimal unitPrice, decimal lineAmount, string currency,
        int? packageCount, decimal? netWeight, decimal? grossWeight)
    {
        if (await db.TradeDocumentItems.AnyAsync(i => i.TradeDocumentId == docId && i.LineNo == lineNo && !i.IsDeleted))
            return;

        db.TradeDocumentItems.Add(new TradeDocumentItem
        {
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
        await db.SaveChangesAsync();
    }

    private static async Task<SysMenu> EnsureMenuAsync(ErpDbContext db, string code)
    {
        var menu = await db.SysMenus.FirstOrDefaultAsync(m => m.MenuCode == code && !m.IsDeleted);
        if (menu is not null)
            return menu;

        menu = new SysMenu { MenuName = code, MenuCode = code, MenuType = MenuType.Menu };
        db.SysMenus.Add(menu);
        await db.SaveChangesAsync();
        return menu;
    }

    private static async Task<SysUser> EnsureUserAsync(ErpDbContext db)
    {
        var user = await db.SysUsers.FirstOrDefaultAsync(u => u.UserName == UserName && !u.IsDeleted);
        if (user is not null)
            return user;

        user = new SysUser { UserName = UserName, PasswordHash = "hash", PasswordSalt = "salt", DisplayName = "ERP306 隔离账号", Status = UserStatus.Enabled };
        db.SysUsers.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    private static async Task<SysRole> EnsureRoleAsync(ErpDbContext db)
    {
        var role = await db.SysRoles.FirstOrDefaultAsync(r => r.RoleCode == RoleCode && !r.IsDeleted);
        if (role is not null)
            return role;

        role = new SysRole { RoleName = RoleCode, RoleCode = RoleCode, IsSystem = true };
        db.SysRoles.Add(role);
        await db.SaveChangesAsync();
        return role;
    }

    private DbContextOptions<ErpDbContext> BuildOptions()
        => new DbContextOptionsBuilder<ErpDbContext>()
            .UseSqlServer(ConnectionString)
            .Options;

    private static string BuildDefaultConnectionString()
        => $"Server=(localdb)\\{InstanceMarker};Initial Catalog={DefaultDatabaseName};Integrated Security=true;TrustServerCertificate=true;";

    private static void AssertDedicatedTarget(string connectionString)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        var server = builder.DataSource ?? string.Empty;
        var database = builder.InitialCatalog ?? string.Empty;

        Assert.Equal($"(localdb)\\{InstanceMarker}", server, ignoreCase: true);
        Assert.True(builder.IntegratedSecurity);
        Assert.StartsWith(DatabasePrefix, database, StringComparison.OrdinalIgnoreCase);
    }
}

