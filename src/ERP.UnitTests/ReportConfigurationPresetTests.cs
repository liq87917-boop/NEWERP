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
/// ERP-296 报表预设模板编排单元测试：覆盖预设只读列出的授权收敛（未授权 / 未 ready 隐藏）、
/// 物化的私有副本语义（经既有 CreateAsync 落库）与 fail-closed 边界（缺失适配器 / 未知预设 / 撤销菜单），
/// 以及迁移登记册 parity 证据门（声明兼容但不提供证据绝不 parity-passed）。
/// <para>全部使用内存数据库（TestDbFactory），不连接 SQL Server、不执行 SQL / seed。</para>
/// </summary>
public class ReportConfigurationPresetTests
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

    private static SysRole SeedRole(ErpDbContext db, string suffix)
    {
        var role = new SysRole
        {
            RoleName = $"Role-{suffix}",
            RoleCode = $"Role-{suffix}-{Guid.NewGuid():N}",
            IsSystem = false,
        };
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

    private static long SeedAuthorizedUser(ErpDbContext db, string userName, params string[] menuCodes)
    {
        var user = SeedUser(db, userName);
        var role = SeedRole(db, userName);
        SeedUserRole(db, user.Id, role.Id);
        foreach (var code in menuCodes)
            SeedRoleMenu(db, role.Id, SeedMenu(db, code).Id);
        return user.Id;
    }

    private static void RevokeMenu(ErpDbContext db, long userId, string menuCode)
    {
        var menu = db.SysMenus.First(m => m.MenuCode == menuCode);
        var roleIds = db.SysUserRoles.Where(ur => ur.UserId == userId && !ur.IsDeleted)
            .Select(ur => ur.RoleId).ToList();
        foreach (var link in db.SysRoleMenus.Where(rm => roleIds.Contains(rm.RoleId) && rm.MenuId == menu.Id))
            link.IsDeleted = true;
        db.SaveChanges();
    }

    private static IReportConfigurationCatalog BuildRealCatalog(ErpDbContext db)
        => new ReportConfigurationCatalog(new IReportConfigurationDatasetProvider[]
        {
            new SalesOrderReportConfigurationDatasetProvider(new DynamicSalesOrderReportQuery(db)),
            new ReceivableReportConfigurationDatasetProvider(new DynamicReceivableReportQuery(db)),
        });

    private static IReportConfigurationService BuildService(ErpDbContext db)
        => new ReportConfigurationService(db, BuildRealCatalog(db));

    private static IReportConfigurationPresetCatalog BuildPresets(
        ErpDbContext db,
        IReportConfigurationCatalog catalog,
        IReportConfigurationService service,
        IReportMigrationParityEvidenceProvider? evidence = null)
    {
        var migrationPresets = new ReportMigrationPresetCatalog();
        var registry = new ReportMigrationRegistry(catalog, db, migrationPresets, evidence);
        return new ReportConfigurationPresetCatalog(catalog, registry, service);
    }

    private sealed class FakeCatalog : IReportConfigurationCatalog
    {
        private readonly IReadOnlyDictionary<string, ReportConfigurationDatasetDto?> _datasets;

        public FakeCatalog(IReadOnlyDictionary<string, ReportConfigurationDatasetDto?> datasets) => _datasets = datasets;

        public Task<ReportConfigurationCatalogDto> GetCatalogAsync(long? userId, CancellationToken cancellationToken = default)
        {
            var datasets = _datasets.Values.Where(d => d is not null).Select(d => d!).ToList();
            return Task.FromResult(new ReportConfigurationCatalogDto(ReportConfigurationRules.CurrentSchemaVersion, datasets));
        }

        public Task<ReportConfigurationDatasetDto?> GetDatasetAsync(string datasetKey, long? userId, CancellationToken cancellationToken = default)
            => Task.FromResult(_datasets.TryGetValue(datasetKey, out var dataset) ? dataset : null);
    }

    private static ReportConfigurationDatasetDto Dataset(string key, string menuCode)
        => new(
            key, key, key, "金额按原币呈现", menuCode, menuCode,
            new List<ReportConfigurationFieldDto>(),
            Array.Empty<string>(),
            Array.Empty<string>(),
            Array.Empty<string>(),
            20, 100, string.Empty, string.Empty);

    // ==================== 1. 只读列出：授权收敛 ====================

    [Fact]
    public async Task ListPresets_无身份_未认证拒绝()
    {
        using var db = TestDbFactory.Create();
        var service = BuildService(db);
        var presets = BuildPresets(db, BuildRealCatalog(db), service);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => presets.ListPresetsAsync(null));

        Assert.Equal(ErrorCodes.Unauthorized, ex.Code);
    }

    [Fact]
    public async Task ListPresets_仅销售订单菜单_只返回销售订单预设()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "sales-only", "sales-order");
        var service = BuildService(db);
        var presets = BuildPresets(db, BuildRealCatalog(db), service);

        var result = await presets.ListPresetsAsync(user);

        var preset = Assert.Single(result);
        Assert.Equal("sales-order", preset.PresetKey);
        Assert.Equal("dynamic:sales-order", preset.LegacyKey);
        Assert.Equal(ReportConfigurationConstants.DatasetSalesOrder, preset.DatasetKey);
    }

    [Fact]
    public async Task ListPresets_仅客户菜单_只返回应收预设()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "customer-only", "customer");
        var service = BuildService(db);
        var presets = BuildPresets(db, BuildRealCatalog(db), service);

        var result = await presets.ListPresetsAsync(user);

        var preset = Assert.Single(result);
        Assert.Equal("receivable", preset.PresetKey);
    }

    [Fact]
    public async Task ListPresets_缺失数据集适配器_未ready预设被隐藏()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "both-menus", "sales-order", "customer");
        var catalog = new FakeCatalog(new Dictionary<string, ReportConfigurationDatasetDto?>
        {
            ["sales-order"] = Dataset("sales-order", "sales-order"),
        });
        var service = new ReportConfigurationService(db, catalog);
        var presets = BuildPresets(db, catalog, service);

        var result = await presets.ListPresetsAsync(user);

        var preset = Assert.Single(result);
        Assert.Equal("sales-order", preset.PresetKey);
    }

    // ==================== 2. 物化：私有副本语义与 fail closed ====================

    [Fact]
    public async Task Materialize_就绪预设_经既有服务创建私有副本()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "materialize-ok", "sales-order");
        var service = BuildService(db);
        var presets = BuildPresets(db, BuildRealCatalog(db), service);

        var created = await presets.MaterializeAsync("sales-order", user);

        var entity = Assert.Single(db.ReportConfigurations);
        Assert.Equal(created.Id, entity.Id);
        Assert.Equal(user, entity.OwnerUserId);
        Assert.Equal("销售订单（迁移预设）", entity.Name);
        Assert.Equal(ReportConfigurationConstants.DatasetSalesOrder, entity.DatasetKey);
        Assert.Equal(ReportConfigurationStatus.Draft, entity.Status);
        Assert.Equal(1, entity.Version);
        Assert.NotNull(created.Definition);
        Assert.Contains("orderNo", created.Definition!.Fields);
    }

    [Fact]
    public async Task Materialize_未知预设_fail_closed()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "unknown-preset", "sales-order");
        var service = BuildService(db);
        var presets = BuildPresets(db, BuildRealCatalog(db), service);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => presets.MaterializeAsync("does-not-exist", user));

        Assert.Equal(ErrorCodes.NotFound, ex.Code);
        Assert.Empty(db.ReportConfigurations);
    }

    [Fact]
    public async Task Materialize_缺失适配器_fail_closed()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "missing-adapter", "customer");
        var catalog = new FakeCatalog(new Dictionary<string, ReportConfigurationDatasetDto?>
        {
            ["sales-order"] = Dataset("sales-order", "sales-order"),
        });
        var service = new ReportConfigurationService(db, catalog);
        var presets = BuildPresets(db, catalog, service);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => presets.MaterializeAsync("receivable", user));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Empty(db.ReportConfigurations);
    }

    [Fact]
    public async Task Materialize_菜单撤销_fail_closed()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "revoked", "sales-order");
        var service = BuildService(db);
        var presets = BuildPresets(db, BuildRealCatalog(db), service);

        await presets.MaterializeAsync("sales-order", user);
        Assert.Single(db.ReportConfigurations);

        RevokeMenu(db, user, "sales-order");

        var ex = await Assert.ThrowsAsync<BusinessException>(() => presets.MaterializeAsync("sales-order", user));

        Assert.Equal(ErrorCodes.NotFound, ex.Code);
        Assert.Single(db.ReportConfigurations); // 绝不新增第二条
    }

    // ==================== 3. parity 证据门（声明兼容但无证据绝不 parity-passed） ====================

    private sealed class FakeEvidenceProvider : IReportMigrationParityEvidenceProvider
    {
        private readonly Func<string, ReportMigrationParityEvidenceDto?> _evidence;

        public FakeEvidenceProvider(Func<string, ReportMigrationParityEvidenceDto?> evidence) => _evidence = evidence;

        public ReportMigrationParityEvidenceDto? GetEvidence(string legacyKey) => _evidence(legacyKey);
    }

    [Fact]
    public async Task Registry_声明兼容但无证据_仍preset_ready()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "declared-only", "sales-order");
        var presets = BuildPresets(db, BuildRealCatalog(db), BuildService(db));

        var preset = await presets.GetPresetAsync("sales-order", user);

        Assert.NotNull(preset);
        Assert.Equal(ReportMigrationParityStatusText.PresetReady, preset!.ParityStatus);
    }

    [Fact]
    public async Task Registry_完整比对证据_parity_passed()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "evidence-full", "sales-order");
        var evidence = new FakeEvidenceProvider(key => key == "dynamic:sales-order"
            ? new ReportMigrationParityEvidenceDto(true, true, true, true)
            : null);
        var service = BuildService(db);
        var presets = BuildPresets(db, BuildRealCatalog(db), service, evidence);

        var preset = await presets.GetPresetAsync("sales-order", user);

        Assert.NotNull(preset);
        Assert.Equal(ReportMigrationParityStatusText.ParityPassed, preset!.ParityStatus);
    }


}
