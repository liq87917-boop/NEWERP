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
/// ERP-295 报表迁移登记册单元测试：覆盖完整清单（不只是 18 个动态报表）、菜单 fail-closed 行为
/// （未授权 / 未声明菜单条目隐藏、菜单撤销立即收敛）与 parity 派生边界（pending → dataset-ready →
/// preset-ready → parity-passed），以及旧路由退役门消费。
/// <para>全部使用内存数据库（TestDbFactory），不连接 SQL Server、不执行 SQL / seed。</para>
/// </summary>
public class ReportMigrationRegistryTests
{
    // ==================== 0. 测试脚手架 ====================

    private static readonly string[] ExpectedDynamicLegacyKeys =
    {
        "dynamic:agency-service-fee-monthly",
        "dynamic:container-stats",
        "dynamic:customer-shipment",
        "dynamic:follow-up-due",
        "dynamic:inventory-aging",
        "dynamic:inventory-movement",
        "dynamic:order-profit",
        "dynamic:product-sales-ranking",
        "dynamic:purchase-order",
        "dynamic:quotation-conversion",
        "dynamic:receipt-reconciliation",
        "dynamic:receivable",
        "dynamic:sales-commission",
        "dynamic:salesman-output",
        "dynamic:sales-order",
        "dynamic:shipment-finance",
        "dynamic:supplier-aging",
        "dynamic:supplier-exposure",
    };

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

    private static SysRole SeedRole(ErpDbContext db, string code)
    {
        var role = new SysRole { RoleName = code, RoleCode = code, IsSystem = false };
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

    private static SysRoleMenu SeedRoleMenu(ErpDbContext db, long roleId, long menuId)
    {
        var link = new SysRoleMenu { RoleId = roleId, MenuId = menuId };
        db.SysRoleMenus.Add(link);
        db.SaveChanges();
        return link;
    }

    private static IReportConfigurationCatalog BuildRealCatalog(ErpDbContext db)
        => new ReportConfigurationCatalog(new IReportConfigurationDatasetProvider[]
        {
            new SalesOrderReportConfigurationDatasetProvider(new DynamicSalesOrderReportQuery(db)),
            new ReceivableReportConfigurationDatasetProvider(new DynamicReceivableReportQuery(db)),
        });

    private static ReportMigrationRegistry BuildRegistry(
        ErpDbContext db, IReportConfigurationCatalog catalog, IReportMigrationPresetCatalog? presets = null,
        IReportMigrationParityEvidenceProvider? evidence = null)
        => new(catalog, db, presets ?? new EmptyReportMigrationPresetCatalog(), evidence);

    private static ReportConfigurationDatasetDto Dataset(string key, string menuCode, string semantics)
        => new(key, key, key, semantics, menuCode, menuCode,
            new List<ReportConfigurationFieldDto>(),
            Array.Empty<string>(),
            Array.Empty<string>(),
            Array.Empty<string>(),
            20, 100, string.Empty, string.Empty);

    private sealed class FakeCatalog : IReportConfigurationCatalog
    {
        private readonly IReadOnlyDictionary<string, ReportConfigurationDatasetDto?> _datasets;

        public FakeCatalog(IReadOnlyDictionary<string, ReportConfigurationDatasetDto?> datasets) => _datasets = datasets;

        public Task<ReportConfigurationCatalogDto> GetCatalogAsync(long? userId, CancellationToken cancellationToken = default)
            => Task.FromResult(new ReportConfigurationCatalogDto(
                ReportConfigurationRules.CurrentSchemaVersion,
                _datasets.Values.Where(v => v is not null).Select(v => v!).ToList()));

        public Task<ReportConfigurationDatasetDto?> GetDatasetAsync(string datasetKey, long? userId, CancellationToken cancellationToken = default)
            => Task.FromResult(_datasets.TryGetValue(datasetKey, out var dataset) ? dataset : null);
    }

    private sealed class FakePresetCatalog : IReportMigrationPresetCatalog
    {
        private readonly Func<string, bool> _hasPreset;

        public FakePresetCatalog(Func<string, bool> hasPreset) => _hasPreset = hasPreset;

        public Task<bool> HasPresetAsync(string legacyKey, long? userId, CancellationToken cancellationToken = default)
            => Task.FromResult(_hasPreset(legacyKey));
    }

    private sealed class FakeEvidenceProvider : IReportMigrationParityEvidenceProvider
    {
        private readonly Func<string, ReportMigrationParityEvidenceDto?> _evidence;

        public FakeEvidenceProvider(Func<string, ReportMigrationParityEvidenceDto?> evidence) => _evidence = evidence;

        public ReportMigrationParityEvidenceDto? GetEvidence(string legacyKey) => _evidence(legacyKey);
    }

    // ==================== 1. 完整清单覆盖 ====================

    [Fact]
    public void Manifest_完整覆盖全部旧报表条目_无遗漏无重复()
    {
        var entries = ReportMigrationRegistryManifest.Entries;

        Assert.Equal(40, entries.Count);
        Assert.Equal(entries.Count, entries.Select(e => e.LegacyKey).Distinct(StringComparer.Ordinal).Count());

        Assert.Equal(10, entries.Count(e => e.Category == ReportMigrationRegistryCategories.FixedReport));
        Assert.Equal(18, entries.Count(e => e.Category == ReportMigrationRegistryCategories.DynamicReport));
        Assert.Equal(2, entries.Count(e => e.Category == ReportMigrationRegistryCategories.Export));
        Assert.Equal(1, entries.Count(e => e.Category == ReportMigrationRegistryCategories.Packet));
        Assert.Equal(2, entries.Count(e => e.Category == ReportMigrationRegistryCategories.Document));
        Assert.Equal(7, entries.Count(e => e.Category == ReportMigrationRegistryCategories.FinancialStatement));

        foreach (var key in ExpectedDynamicLegacyKeys)
            Assert.Contains(entries, e => e.LegacyKey == key);

        Assert.All(entries, e =>
        {
            Assert.False(string.IsNullOrWhiteSpace(e.LegacyKey));
            Assert.False(string.IsNullOrWhiteSpace(e.Title));
            Assert.False(string.IsNullOrWhiteSpace(e.Category));
            Assert.False(string.IsNullOrWhiteSpace(e.DatasetKey));
        });
    }

    // ==================== 2. 菜单 fail-closed ====================

    [Fact]
    public async Task GetRegistry_无身份_未认证拒绝()
    {
        using var db = TestDbFactory.Create();
        var registry = BuildRegistry(db, BuildRealCatalog(db));

        var ex = await Assert.ThrowsAsync<BusinessException>(() => registry.GetRegistryAsync(null));
        Assert.Equal(ErrorCodes.Unauthorized, ex.Code);
    }

    [Fact]
    public async Task GetRegistry_仅销售订单菜单_只返回销售订单相关授权条目()
    {
        using var db = TestDbFactory.Create();
        var user = SeedUser(db, "so-only");
        var role = SeedRole(db, "Role-so-only");
        SeedUserRole(db, user.Id, role.Id);
        SeedRoleMenu(db, role.Id, SeedMenu(db, "sales-order").Id);

        var registry = BuildRegistry(db, BuildRealCatalog(db));

        var result = await registry.GetRegistryAsync(user.Id);

        var keys = result.Entries.Select(e => e.LegacyKey).OrderBy(k => k, StringComparer.Ordinal).ToArray();
        Assert.Equal(
            new[] { "dynamic:receipt-reconciliation", "dynamic:sales-order", "dynamic:shipment-finance" },
            keys);

        var salesOrder = Assert.Single(result.Entries, e => e.LegacyKey == "dynamic:sales-order");
        Assert.Equal(ReportMigrationParityStatusText.DatasetReady, salesOrder.ParityStatus);

        Assert.All(result.Entries, e => Assert.Contains("sales-order", e.RequiredMenuCodes));
        Assert.DoesNotContain(result.Entries, e => e.LegacyKey == "dynamic:receivable");
        Assert.DoesNotContain(result.Entries, e => e.LegacyKey == "export:bill-proc");
    }

    [Fact]
    public async Task GetRegistry_菜单撤销后_条目立即隐藏()
    {
        using var db = TestDbFactory.Create();
        var user = SeedUser(db, "revoke");
        var role = SeedRole(db, "Role-revoke");
        SeedUserRole(db, user.Id, role.Id);
        var salesMenu = SeedMenu(db, "sales-order");
        SeedRoleMenu(db, role.Id, salesMenu.Id);

        var registry = BuildRegistry(db, BuildRealCatalog(db));

        var before = await registry.GetRegistryAsync(user.Id);
        Assert.Contains(before.Entries, e => e.LegacyKey == "dynamic:sales-order");

        var link = db.SysRoleMenus.First(rm => rm.RoleId == role.Id && rm.MenuId == salesMenu.Id);
        link.IsDeleted = true;
        db.SaveChanges();

        var after = await registry.GetRegistryAsync(user.Id);
        Assert.Empty(after.Entries);
    }

    [Fact]
    public async Task GetRegistry_未声明菜单条目_始终隐藏()
    {
        using var db = TestDbFactory.Create();
        var user = SeedUser(db, "product-only");
        var role = SeedRole(db, "Role-product-only");
        SeedUserRole(db, user.Id, role.Id);
        SeedRoleMenu(db, role.Id, SeedMenu(db, "product").Id);

        var registry = BuildRegistry(db, BuildRealCatalog(db));

        var result = await registry.GetRegistryAsync(user.Id);

        Assert.Contains(result.Entries, e => e.LegacyKey == "export:product-export-field-completeness");
        Assert.DoesNotContain(result.Entries, e => e.LegacyKey == "export:bill-proc");
    }

    // ==================== 3. parity 派生边界 ====================

    private static SysUser SeedUserWithMenus(ErpDbContext db, string userName, params string[] menuCodes)
    {
        var user = SeedUser(db, userName);
        var role = SeedRole(db, "Role-" + userName);
        SeedUserRole(db, user.Id, role.Id);
        foreach (var code in menuCodes)
            SeedRoleMenu(db, role.Id, SeedMenu(db, code).Id);
        return user;
    }

    [Fact]
    public async Task Parity_未注册数据集适配器_pending()
    {
        using var db = TestDbFactory.Create();
        var user = SeedUserWithMenus(db, "pending", "product-sales-ranking");
        var registry = BuildRegistry(db, BuildRealCatalog(db));

        var result = await registry.GetRegistryAsync(user.Id);

        var entry = Assert.Single(result.Entries, e => e.LegacyKey == "dynamic:product-sales-ranking");
        Assert.Equal(ReportMigrationParityStatusText.Pending, entry.ParityStatus);
    }

    [Fact]
    public async Task Parity_数据集就绪但预设缺失_dataset_ready()
    {
        using var db = TestDbFactory.Create();
        var user = SeedUserWithMenus(db, "dataset-ready", "sales-order");

        var catalog = new FakeCatalog(new Dictionary<string, ReportConfigurationDatasetDto?>
        {
            ["sales-order"] = Dataset("sales-order", "sales-order", "金额按订单原币呈现，不跨币种换算或合并"),
        });
        var registry = BuildRegistry(db, catalog, new FakePresetCatalog(_ => false));

        var result = await registry.GetRegistryAsync(user.Id);

        var entry = Assert.Single(result.Entries, e => e.LegacyKey == "dynamic:sales-order");
        Assert.Equal(ReportMigrationParityStatusText.DatasetReady, entry.ParityStatus);
    }

    [Fact]
    public async Task Parity_预设就绪但语义不匹配_preset_ready()
    {
        using var db = TestDbFactory.Create();
        var user = SeedUserWithMenus(db, "preset-ready", "sales-order");

        var catalog = new FakeCatalog(new Dictionary<string, ReportConfigurationDatasetDto?>
        {
            ["sales-order"] = Dataset("sales-order", "sales-order", "不同的币种口径"),
        });
        var registry = BuildRegistry(db, catalog, new FakePresetCatalog(k => k == "dynamic:sales-order"));

        var result = await registry.GetRegistryAsync(user.Id);

        var entry = Assert.Single(result.Entries, e => e.LegacyKey == "dynamic:sales-order");
        Assert.Equal(ReportMigrationParityStatusText.PresetReady, entry.ParityStatus);
    }

    [Fact]
    public async Task Parity_适配器预设兼容性声明匹配_但无证据_拒绝parity_passed()
    {
        using var db = TestDbFactory.Create();
        var user = SeedUserWithMenus(db, "declared-only", "sales-order");

        var catalog = new FakeCatalog(new Dictionary<string, ReportConfigurationDatasetDto?>
        {
            ["sales-order"] = Dataset("sales-order", "sales-order", "金额按订单原币呈现，不跨币种换算或合并"),
        });
        var registry = BuildRegistry(db, catalog, new FakePresetCatalog(k => k == "dynamic:sales-order"));

        var result = await registry.GetRegistryAsync(user.Id);

        var entry = Assert.Single(result.Entries, e => e.LegacyKey == "dynamic:sales-order");
        Assert.Equal(ReportMigrationParityStatusText.PresetReady, entry.ParityStatus);
    }

    [Fact]
    public async Task Parity_完整比对证据_parity_passed()
    {
        using var db = TestDbFactory.Create();
        var user = SeedUserWithMenus(db, "parity-passed", "sales-order");

        var catalog = new FakeCatalog(new Dictionary<string, ReportConfigurationDatasetDto?>
        {
            ["sales-order"] = Dataset("sales-order", "sales-order", "金额按订单原币呈现，不跨币种换算或合并"),
        });
        var evidence = new FakeEvidenceProvider(key => key == "dynamic:sales-order"
            ? new ReportMigrationParityEvidenceDto(true, true, true, true)
            : null);
        var registry = BuildRegistry(db, catalog, new FakePresetCatalog(k => k == "dynamic:sales-order"), evidence);

        var result = await registry.GetRegistryAsync(user.Id);

        var entry = Assert.Single(result.Entries, e => e.LegacyKey == "dynamic:sales-order");
        Assert.Equal(ReportMigrationParityStatusText.ParityPassed, entry.ParityStatus);
    }

    // ==================== 4. 旧路由退役门消费 ====================

    [Fact]
    public async Task CanRetireLegacyRoutes_预设缺失_恒false()
    {
        using var db = TestDbFactory.Create();
        var user = SeedUserWithMenus(db, "gate", "sales-order", "customer");
        var registry = BuildRegistry(db, BuildRealCatalog(db));

        Assert.False(await registry.CanRetireLegacyRoutesAsync(user.Id));
    }

    [Fact]
    public async Task CanRetireLegacyRoutes_任一条目未parity_passed_仍false()
    {
        using var db = TestDbFactory.Create();
        var user = SeedUserWithMenus(db, "partial", "sales-order");

        var catalog = new FakeCatalog(new Dictionary<string, ReportConfigurationDatasetDto?>
        {
            ["sales-order"] = Dataset("sales-order", "sales-order", "金额按订单原币呈现，不跨币种换算或合并"),
        });
        var registry = BuildRegistry(db, catalog, new FakePresetCatalog(k => k == "dynamic:sales-order"));

        Assert.False(await registry.CanRetireLegacyRoutesAsync(user.Id));
    }
}


