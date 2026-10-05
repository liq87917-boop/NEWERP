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
/// ERP-314 Stage 2 单节报表预设参数绑定单元测试：覆盖有限参数元数据、类型化物化请求、
/// 无参数端点兼容、customer/date/status 合取筛选绑定、日期倒置 / 未声明参数 / 撤销字段在创建草稿前的显式失败。
/// <para>全部使用内存数据库（TestDbFactory）与有限夹具目录，不连接 SQL Server、不执行 SQL / seed。</para>
/// </summary>
public class ReportConfigurationPresetParameterTests
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

    private static IReportConfigurationCatalog BuildRealCatalog(ErpDbContext db)
        => new ReportConfigurationCatalog(new IReportConfigurationDatasetProvider[]
        {
            new SalesOrderReportConfigurationDatasetProvider(new DynamicSalesOrderReportQuery(db)),
            new ReceivableReportConfigurationDatasetProvider(new DynamicReceivableReportQuery(db)),
        });

    private static IReportConfigurationService BuildService(ErpDbContext db, IReportConfigurationCatalog catalog)
        => new ReportConfigurationService(db, catalog);

    private static IReportConfigurationPresetCatalog BuildPresets(
        ErpDbContext db,
        IReportConfigurationCatalog catalog,
        IReportConfigurationService service)
    {
        var migrationPresets = new ReportMigrationPresetCatalog();
        var registry = new ReportMigrationRegistry(catalog, db, migrationPresets);
        return new ReportConfigurationPresetCatalog(catalog, registry, service);
    }

    // ==================== 有限夹具：FakeCatalog / FakeRegistry ====================

    private static ReportConfigurationFieldDto Field(string key, string type, bool filterable)
        => new(key, key, type, null, filterable, false, Hidden: false,
            ReportConfigurationRules.GetOperatorsForType(type));

    private static ReportConfigurationDatasetDto Dataset(string key, string menuCode, IReadOnlyList<ReportConfigurationFieldDto>? fields = null)
        => new(
            key, key, key, "金额按原币呈现", menuCode, menuCode,
            (fields ?? new List<ReportConfigurationFieldDto>()).ToList(),
            Array.Empty<string>(),
            Array.Empty<string>(),
            Array.Empty<string>(),
            20, 100, string.Empty, string.Empty);

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

    private sealed class FakeRegistry : IReportMigrationRegistry
    {
        private readonly IReadOnlyDictionary<string, string> _parityByLegacy;

        public FakeRegistry(IReadOnlyDictionary<string, string> parityByLegacy) => _parityByLegacy = parityByLegacy;

        public Task<ReportMigrationRegistryDto> GetRegistryAsync(long? userId, CancellationToken cancellationToken = default)
        {
            var entries = _parityByLegacy
                .Select(kv => new ReportMigrationRegistryEntryDto(
                    kv.Key, kv.Key, "迁移", string.Empty, Array.Empty<string>(), string.Empty,
                    "金额按原币呈现", true, true, kv.Value))
                .ToList();
            return Task.FromResult(new ReportMigrationRegistryDto(ReportConfigurationRules.CurrentSchemaVersion, entries, false));
        }

        public Task<bool> CanRetireLegacyRoutesAsync(long? userId, CancellationToken cancellationToken = default)
            => Task.FromResult(false);
    }


    // ==================== 1. 有限参数元数据 ====================

    [Fact]
    public async Task GetPreset_销售订单_暴露客户日期状态参数()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "param-so-meta", "sales-order");
        var catalog = BuildRealCatalog(db);
        var presets = BuildPresets(db, catalog, BuildService(db, catalog));

        var preset = await presets.GetPresetAsync("sales-order", user);

        Assert.NotNull(preset);
        Assert.Equal(new[] { "customer", "date", "status" }, preset!.Parameters.Select(p => p.Key).ToArray());
        Assert.Equal(new[] { "number", "date", "text" }, preset.Parameters.Select(p => p.Type).ToArray());
        Assert.All(preset.Parameters, p => Assert.False(p.Required));
    }

    [Fact]
    public async Task GetPreset_应收账款_暴露客户日期状态参数()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "param-ar-meta", "customer");
        var catalog = BuildRealCatalog(db);
        var presets = BuildPresets(db, catalog, BuildService(db, catalog));

        var preset = await presets.GetPresetAsync("receivable", user);

        Assert.NotNull(preset);
        Assert.Equal(new[] { "customer", "date", "status" }, preset!.Parameters.Select(p => p.Key).ToArray());
    }

    // ==================== 2. 无参数端点兼容 + 参数化物化 ====================

    [Fact]
    public async Task Materialize_无参数_兼容原端点_创建空筛选私有草稿()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "param-so-noparam", "sales-order");
        var catalog = BuildRealCatalog(db);
        var service = BuildService(db, catalog);
        var presets = BuildPresets(db, catalog, service);

        var created = await presets.MaterializeAsync("sales-order", user);

        var entity = Assert.Single(db.ReportConfigurations);
        Assert.Equal(created.Id, entity.Id);
        Assert.Equal(user, entity.OwnerUserId);
        Assert.NotNull(created.Definition);
        Assert.Empty(created.Definition!.Filters);
    }

    [Fact]
    public async Task Materialize_销售订单_客户日期状态_合取筛选绑定()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "param-so-bind", "sales-order");
        var catalog = BuildRealCatalog(db);
        var service = BuildService(db, catalog);
        var presets = BuildPresets(db, catalog, service);

        var created = await presets.MaterializeAsync("sales-order",
            new ReportConfigurationPresetMaterializeRequest
            {
                CustomerId = 42,
                StartDate = new DateTime(2026, 9, 1),
                EndDate = new DateTime(2026, 9, 30),
                Status = "Approved",
            }, user);

        Assert.Equal(3, created.Definition!.Filters.Count);
        Assert.Equal("customerId", created.Definition.Filters[0].FieldKey);
        Assert.Equal(ReportConfigurationConstants.OperatorEq, created.Definition.Filters[0].Operator);
        Assert.Equal("orderDate", created.Definition.Filters[1].FieldKey);
        Assert.Equal(ReportConfigurationConstants.OperatorBetween, created.Definition.Filters[1].Operator);
        Assert.Equal("status", created.Definition.Filters[2].FieldKey);
        Assert.Equal(ReportConfigurationConstants.OperatorEq, created.Definition.Filters[2].Operator);

        var reloaded = await service.GetAsync(user, created.Id);
        Assert.Equal(3, reloaded.Definition!.Filters.Count);
        Assert.Equal("customerId", reloaded.Definition.Filters[0].FieldKey);
        Assert.Equal("orderDate", reloaded.Definition.Filters[1].FieldKey);
        Assert.Equal("status", reloaded.Definition.Filters[2].FieldKey);
    }

    [Fact]
    public async Task Materialize_应收账款_客户日期状态_绑定权威字段()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "param-ar-bind", "customer");
        var catalog = BuildRealCatalog(db);
        var service = BuildService(db, catalog);
        var presets = BuildPresets(db, catalog, service);

        var created = await presets.MaterializeAsync("receivable",
            new ReportConfigurationPresetMaterializeRequest
            {
                CustomerId = 7,
                StartDate = new DateTime(2026, 1, 1),
                EndDate = new DateTime(2026, 12, 31),
                Status = "allocated",
            }, user);

        Assert.Equal(3, created.Definition!.Filters.Count);
        Assert.Equal("customerId", created.Definition.Filters[0].FieldKey);
        Assert.Equal("invoiceDate", created.Definition.Filters[1].FieldKey);
        Assert.Equal("allocationState", created.Definition.Filters[2].FieldKey);
    }


    // ==================== 3. 无效输入：持久化前显式失败 ====================

    [Fact]
    public async Task Materialize_日期倒置_拒绝且不落库()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "param-inverted", "sales-order");
        var catalog = BuildRealCatalog(db);
        var presets = BuildPresets(db, catalog, BuildService(db, catalog));

        var ex = await Assert.ThrowsAsync<BusinessException>(() => presets.MaterializeAsync("sales-order",
            new ReportConfigurationPresetMaterializeRequest
            {
                StartDate = new DateTime(2026, 9, 30),
                EndDate = new DateTime(2026, 9, 1),
            }, user));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Empty(db.ReportConfigurations);
    }

    [Fact]
    public async Task Materialize_未声明参数_拒绝且不落库()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "param-undeclared", "inventory-movement");
        var catalog = new FakeCatalog(new Dictionary<string, ReportConfigurationDatasetDto?>
        {
            [ReportConfigurationConstants.DatasetInventoryMovement] = Dataset(
                ReportConfigurationConstants.DatasetInventoryMovement, "inventory-movement"),
        });
        var registry = new FakeRegistry(new Dictionary<string, string>
        {
            ["report:inventory-movement"] = ReportMigrationParityStatusText.PresetReady,
        });
        var presets = new ReportConfigurationPresetCatalog(catalog, registry, BuildService(db, catalog));

        var ex = await Assert.ThrowsAsync<BusinessException>(() => presets.MaterializeAsync("inventory-movement",
            new ReportConfigurationPresetMaterializeRequest { CustomerId = 1 }, user));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Empty(db.ReportConfigurations);
    }

    [Fact]
    public async Task Materialize_撤销字段_不可筛选_拒绝且不落库()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "param-revoked", "sales-order");
        var catalog = new FakeCatalog(new Dictionary<string, ReportConfigurationDatasetDto?>
        {
            ["sales-order"] = Dataset("sales-order", "sales-order", new List<ReportConfigurationFieldDto>
            {
                Field("customerId", ReportConfigurationConstants.TypeNumber, filterable: false),
                Field("orderDate", ReportConfigurationConstants.TypeDate, filterable: true),
                Field("status", ReportConfigurationConstants.TypeEnum, filterable: true),
            }),
        });
        var registry = new FakeRegistry(new Dictionary<string, string>
        {
            ["dynamic:sales-order"] = ReportMigrationParityStatusText.PresetReady,
        });
        var presets = new ReportConfigurationPresetCatalog(catalog, registry, BuildService(db, catalog));

        var ex = await Assert.ThrowsAsync<BusinessException>(() => presets.MaterializeAsync("sales-order",
            new ReportConfigurationPresetMaterializeRequest { CustomerId = 1 }, user));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Empty(db.ReportConfigurations);
    }

}
