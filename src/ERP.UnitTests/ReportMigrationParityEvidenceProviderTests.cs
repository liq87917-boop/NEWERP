using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-332 迁移 parity 证据源单元测试：证据源 fail-closed（无证据恒 null）、只写完整证据、
/// 不覆盖既有证据，以及接入 <see cref="ReportMigrationRegistry"/> 后代表条目可真正到达 parity-passed。
/// <para>全部使用内存数据库（TestDbFactory），不连接 SQL Server、不执行 SQL / seed。</para>
/// </summary>
public class ReportMigrationParityEvidenceProviderTests
{
    [Fact]
    public void GetEvidence_无任何证据_返回null()
    {
        var provider = new ReportMigrationParityEvidenceProvider();

        Assert.Null(provider.GetEvidence("report:product-sales-ranking"));
        Assert.Null(provider.GetEvidence(null!));
        Assert.Null(provider.GetEvidence("   "));
        Assert.Null(provider.GetEvidence("unknown:key"));
    }

    [Fact]
    public void Record_完整四维证据_按旧报表键返回()
    {
        var provider = new ReportMigrationParityEvidenceProvider();
        var evidence = new ReportMigrationParityEvidenceDto(true, true, true, true);

        provider.Record("dynamic:sales-order", evidence);

        Assert.True(evidence.Complete);
        var served = provider.GetEvidence("dynamic:sales-order");
        Assert.NotNull(served);
        Assert.True(served!.Complete);
        Assert.True(served.DataGrainMatched);
        Assert.True(served.CurrencyUnitMatched);
        Assert.True(served.PermissionsMatched);
        Assert.True(served.OutputSemanticsMatched);
    }

    [Fact]
    public void Record_空证据与空白键_不写入()
    {
        var provider = new ReportMigrationParityEvidenceProvider();

        provider.Record("dynamic:sales-order", null!);
        provider.Record("", new ReportMigrationParityEvidenceDto(true, true, true, true));
        provider.Record("   ", new ReportMigrationParityEvidenceDto(true, true, true, true));

        Assert.Null(provider.GetEvidence("dynamic:sales-order"));
        Assert.Null(provider.GetEvidence(""));
    }

    [Theory]
    [InlineData("report:product-sales-ranking", "product-sales-ranking")]
    [InlineData("dynamic:sales-order", "sales-order")]
    [InlineData("report:balance-sheet", "balance-sheet")]
    public async Task Registry_接入真实证据源_有完整证据_代表条目parity_passed(string legacyKey, string menuCode)
    {
        using var db = TestDbFactory.Create();
        var user = SeedUserWithMenus(db, "p-" + legacyKey.Replace(':', '-'), menuCode);
        var definition = Resolve(legacyKey);

        var provider = new ReportMigrationParityEvidenceProvider();
        provider.Record(legacyKey, new ReportMigrationParityEvidenceDto(true, true, true, true));

        var registry = BuildRegistry(db, definition, provider);

        var result = await registry.GetRegistryAsync(user.Id);

        var entry = Assert.Single(result.Entries, e => e.LegacyKey == legacyKey);
        Assert.Equal(ReportMigrationParityStatusText.ParityPassed, entry.ParityStatus);
    }

    [Theory]
    [InlineData("export:bill-proc:sales-order", "sales-order", "sales-order-export")]
    [InlineData("document:trade-document-export-excel", "doc-center")]
    [InlineData("print-template:sales-order", "sales-order", "sales-order-export")]
    public async Task Registry_多菜单与单证条目_有完整证据_parity_passed(string legacyKey, params string[] menuCodes)
    {
        using var db = TestDbFactory.Create();
        var user = SeedUserWithMenus(db, "m-" + legacyKey.Replace(':', '-'), menuCodes);
        var definition = Resolve(legacyKey);

        var provider = new ReportMigrationParityEvidenceProvider();
        provider.Record(legacyKey, new ReportMigrationParityEvidenceDto(true, true, true, true));

        var registry = BuildRegistry(db, definition, provider);

        var result = await registry.GetRegistryAsync(user.Id);

        var entry = Assert.Single(result.Entries, e => e.LegacyKey == legacyKey);
        Assert.Equal(ReportMigrationParityStatusText.ParityPassed, entry.ParityStatus);
    }

    [Fact]
    public async Task Registry_证据源无证据_仍preset_ready_绝不parity_passed()
    {
        using var db = TestDbFactory.Create();
        var user = SeedUserWithMenus(db, "no-evidence", "sales-order");
        var definition = Resolve("dynamic:sales-order");

        var provider = new ReportMigrationParityEvidenceProvider();
        var registry = BuildRegistry(db, definition, provider);

        var result = await registry.GetRegistryAsync(user.Id);

        var entry = Assert.Single(result.Entries, e => e.LegacyKey == "dynamic:sales-order");
        Assert.Equal(ReportMigrationParityStatusText.PresetReady, entry.ParityStatus);
        Assert.False(await registry.CanRetireLegacyRoutesAsync(user.Id));
    }


    [Theory]
    [InlineData(false, true, true, true)]
    [InlineData(true, false, true, true)]
    [InlineData(true, true, false, true)]
    [InlineData(true, true, true, false)]
    public void Incomplete_evidence_invalidates_previous_complete_evidence(
        bool grain, bool currency, bool permissions, bool output)
    {
        var provider = new ReportMigrationParityEvidenceProvider();
        provider.Record("dynamic:sales-order", new(true, true, true, true));
        provider.Record("dynamic:sales-order", new(grain, currency, permissions, output));
        Assert.Null(provider.GetEvidence("dynamic:sales-order"));
    }

    [Fact]
    public void Unknown_legacy_key_cannot_publish_complete_evidence()
    {
        var provider = new ReportMigrationParityEvidenceProvider();
        provider.Record("invented:outside-manifest", new(true, true, true, true));
        Assert.Null(provider.GetEvidence("invented:outside-manifest"));
    }

    // ==================== 脚手架 ====================

    private static ReportMigrationRegistryEntryDefinition Resolve(string legacyKey)
        => ReportMigrationRegistryManifest.Entries
            .Concat(LegacyBillExportCatalog.RegistryEntries)
            .Concat(ReportPrintTemplateFamilies.RegistryEntries)
            .Single(e => e.LegacyKey == legacyKey);

    private static ReportMigrationRegistry BuildRegistry(
        ErpDbContext db, ReportMigrationRegistryEntryDefinition definition,
        IReportMigrationParityEvidenceProvider evidence)
        => new(
            new FakeCatalog(definition),
            db,
            new FakePresetCatalog(k => k == definition.LegacyKey),
            evidence);

    private static SysUser SeedUserWithMenus(ErpDbContext db, string userName, params string[] menuCodes)
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

        var role = new SysRole { RoleName = "Role-" + userName, RoleCode = "Role-" + userName, IsSystem = false };
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

    private sealed class FakeCatalog : IReportConfigurationCatalog
    {
        private readonly ReportConfigurationDatasetDto _dataset;

        public FakeCatalog(ReportMigrationRegistryEntryDefinition definition)
            => _dataset = Dataset(definition);

        public Task<ReportConfigurationCatalogDto> GetCatalogAsync(long? userId, CancellationToken cancellationToken = default)
            => Task.FromResult(new ReportConfigurationCatalogDto(
                ReportConfigurationRules.CurrentSchemaVersion,
                new List<ReportConfigurationDatasetDto> { _dataset }));

        public Task<ReportConfigurationDatasetDto?> GetDatasetAsync(string datasetKey, long? userId, CancellationToken cancellationToken = default)
            => Task.FromResult(string.Equals(datasetKey, _dataset.DatasetKey, StringComparison.OrdinalIgnoreCase)
                ? _dataset
                : null);
    }

    private static ReportConfigurationDatasetDto Dataset(ReportMigrationRegistryEntryDefinition definition)
        => new(
            definition.DatasetKey,
            definition.DatasetKey,
            definition.DatasetKey,
            definition.CurrencyUnitSemantics,
            string.Join("+", definition.RequiredMenuCodes),
            definition.RequiredMenuText,
            new List<ReportConfigurationFieldDto>(),
            Array.Empty<string>(),
            Array.Empty<string>(),
            Array.Empty<string>(),
            20,
            100,
            string.Empty,
            string.Empty);

    private sealed class FakePresetCatalog : IReportMigrationPresetCatalog
    {
        private readonly Func<string, bool> _hasPreset;

        public FakePresetCatalog(Func<string, bool> hasPreset) => _hasPreset = hasPreset;

        public Task<bool> HasPresetAsync(string legacyKey, long? userId, CancellationToken cancellationToken = default)
            => Task.FromResult(_hasPreset(legacyKey));
    }
}

