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

    public static TheoryData<string> DriftedLegacyKeys => new()
    {
        "report:product-sales-ranking",
        "dynamic:product-sales-ranking",
        "report:order-profit",
        "dynamic:order-profit",
        "report:salesman-output",
        "dynamic:salesman-output",
        "report:sales-commission",
        "dynamic:sales-commission",
        "report:follow-up-due",
        "dynamic:follow-up-due",
        "report:quotation-conversion",
        "dynamic:quotation-conversion",
        "dynamic:receivable",
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

    private static IReportConfigurationDatasetProvider BuildDriftedProvider(ErpDbContext db, string datasetKey)
    {
        var reportService = new ReportService(db);
        return datasetKey switch
        {
            ReportConfigurationConstants.DatasetProductSalesRanking => new ProductSalesRankingReportConfigurationDatasetProvider(reportService, db),
            ReportConfigurationConstants.DatasetOrderProfit => new OrderProfitEstimateReportConfigurationDatasetProvider(reportService, db),
            ReportConfigurationConstants.DatasetSalesmanOutput => new SalesmanOutputReportConfigurationDatasetProvider(reportService, db),
            ReportConfigurationConstants.DatasetSalesCommission => new SalesCommissionReportConfigurationDatasetProvider(reportService, db),
            ReportConfigurationConstants.DatasetFollowUpDue => new FollowUpDueReportConfigurationDatasetProvider(reportService, db),
            ReportConfigurationConstants.DatasetQuotationConversion => new QuotationConversionReportConfigurationDatasetProvider(reportService, db),
            ReportConfigurationConstants.DatasetReceivable => new ReceivableReportConfigurationDatasetProvider(new DynamicReceivableReportQuery(db)),
            _ => throw new ArgumentOutOfRangeException(nameof(datasetKey)),
        };
    }

    private static ReportMigrationRegistry BuildRegistry(
        ErpDbContext db, IReportConfigurationCatalog catalog, IReportMigrationPresetCatalog? presets = null,
        IReportMigrationParityEvidenceProvider? evidence = null,
        IReportConfigurationBundlePresetCatalog? bundlePresets = null)
        => new(catalog, db, presets ?? new EmptyReportMigrationPresetCatalog(), evidence, bundlePresets);

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

    private sealed class FakeBundlePresetCatalog : IReportConfigurationBundlePresetCatalog
    {
        private readonly IReadOnlyList<ReportConfigurationBundlePresetDto> _presets;

        public FakeBundlePresetCatalog(params ReportConfigurationBundlePresetDto[] presets) => _presets = presets;

        public Task<List<ReportConfigurationBundlePresetDto>> ListPresetsAsync(long? userId, CancellationToken cancellationToken = default)
            => Task.FromResult(_presets.ToList());

        public Task<ReportConfigurationBundlePresetDto?> GetPresetAsync(string presetKey, long? userId, CancellationToken cancellationToken = default)
            => Task.FromResult<ReportConfigurationBundlePresetDto?>(null);

        public Task<ReportConfigurationBundlePresetMaterializationDto> MaterializeAsync(
            string presetKey,
            ReportConfigurationBundlePresetMaterializeRequest request,
            long? userId,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private static ReportConfigurationBundlePresetDto BundlePreset(
        string legacyKey, string readiness, params string[] sectionDatasetKeys)
        => new()
        {
            PresetKey = "customer-report-packet",
            LegacyKey = legacyKey,
            Name = "客户报告包",
            Readiness = readiness,
            Sections = sectionDatasetKeys.Select(k => new ReportConfigurationBundlePresetSectionDto
            {
                SectionKey = k,
                Title = k,
                DatasetKey = k,
            }).ToList(),
        };

    // ==================== 1. 完整清单覆盖 ====================

    [Fact]
    public void Manifest_完整覆盖全部旧报表条目_无遗漏无重复()
    {
        var entries = ReportMigrationRegistryManifest.Entries;

        Assert.Equal(39, entries.Count);
        Assert.Equal(entries.Count, entries.Select(e => e.LegacyKey).Distinct(StringComparer.Ordinal).Count());

        Assert.Equal(10, entries.Count(e => e.Category == ReportMigrationRegistryCategories.FixedReport));
        Assert.Equal(18, entries.Count(e => e.Category == ReportMigrationRegistryCategories.DynamicReport));
        Assert.Equal(1, entries.Count(e => e.Category == ReportMigrationRegistryCategories.Export));
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

    [Fact]
    public void Manifest_不含被取代的聚合单据导出占位()
    {
        Assert.DoesNotContain(ReportMigrationRegistryManifest.Entries,
            e => e.LegacyKey == "export:bill-proc");
        Assert.DoesNotContain(ReportMigrationRegistryManifest.Entries,
            e => e.DatasetKey == "bill-export");
    }

    [Fact]
    public void LegacyBillExportCatalog_16个族条目是旧单据导出的唯一表示()
    {
        var entries = LegacyBillExportCatalog.RegistryEntries;

        Assert.Equal(16, entries.Count);
        Assert.Equal(entries.Count, entries.Select(e => e.LegacyKey).Distinct(StringComparer.Ordinal).Count());

        Assert.All(entries, e =>
        {
            Assert.StartsWith("export:bill-proc:", e.LegacyKey);
            Assert.StartsWith("bill-export:", e.DatasetKey);
            Assert.NotEmpty(e.RequiredMenuCodes);
            Assert.True(e.ExcelCompatible);
        });

        // 被取代的单一聚合键不得再出现在清单里（16 个族条目是旧单据导出的唯一表示）。
        Assert.DoesNotContain(ReportMigrationRegistryManifest.Entries,
            e => e.LegacyKey == "export:bill-proc");
    }

    // ==================== 1.5 币种/单位语义对齐（ERP-326） ====================

    [Theory]
    [MemberData(nameof(DriftedLegacyKeys))]
    public async Task Manifest_13条漂移条目_币种单位语义与数据集提供者完全一致(string legacyKey)
    {
        using var db = TestDbFactory.Create();
        var entry = Assert.Single(ReportMigrationRegistryManifest.Entries, e => e.LegacyKey == legacyKey);
        var menuCode = Assert.Single(entry.RequiredMenuCodes);
        var user = SeedUserWithMenus(db, "sem-" + legacyKey.Replace(':', '-'), menuCode);
        var provider = BuildDriftedProvider(db, entry.DatasetKey);

        var dataset = await provider.GetDatasetAsync(user.Id);

        Assert.NotNull(dataset);
        Assert.Equal(entry.CurrencyUnitSemantics, dataset!.CurrencyUnitSemantics);
    }

    [Fact]
    public void Manifest_已对齐条目的币种单位语义保持不变()
    {
        var expected = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["report:customer-shipment"] = "金额按原币呈现；数量按基础单位；不跨币种换算或合并",
            ["dynamic:customer-shipment"] = "金额按原币呈现；数量按基础单位；不跨币种换算或合并",
            ["report:container-stats"] = "箱数/毛重/体积按原始单位；装载率与柜型未知；不跨币种换算",
            ["dynamic:container-stats"] = "箱数/毛重/体积按原始单位；装载率与柜型未知；不跨币种换算",
            ["report:inventory-movement"] = "数量按基础单位；成本按移动加权平均；不跨币种合并",
            ["dynamic:inventory-movement"] = "数量按基础单位；成本按移动加权平均；不跨币种合并",
            ["report:inventory-aging"] = "数量按基础单位；成本/金额按移动加权平均；不跨币种合并",
            ["dynamic:inventory-aging"] = "数量按基础单位；成本/金额按移动加权平均；不跨币种合并",
            ["report:ar-aging"] = "金额按原币呈现；账龄按自然日；不跨币种换算或合并",
            ["report:purchase-cost"] = "金额按原币呈现；按供应商聚合；不跨币种换算或合并",
            ["report:tax-refund-summary"] = "金额按原币呈现；按退税期间聚合；不跨币种换算或合并",
            ["report:balance-sheet"] = "金额按单据金额直接汇总（资产/负债/权益）；不跨币种换算",
            ["report:income-statement"] = "金额按单据金额直接汇总（收入-成本-费用）；不跨币种换算",
            ["report:cash-flow"] = "金额按单据金额直接汇总（流入-流出）；不跨币种换算",
            ["report:stock-alert"] = "数量按基础单位；无金额/币种",
            ["dynamic:agency-service-fee-monthly"] = "金额按原币呈现；按对账月份/客户分组；不跨币种换算或合并",
            ["dynamic:receipt-reconciliation"] = "金额按原币呈现；订单与收款核对；不跨币种换算或合并",
            ["dynamic:purchase-order"] = "金额按订单原币呈现，不跨币种换算或合并",
            ["dynamic:supplier-aging"] = "金额按原币呈现；账龄按自然日；不跨币种换算或合并",
            ["dynamic:supplier-exposure"] = "金额按原币呈现；不跨币种换算或合并",
            ["dynamic:shipment-finance"] = "数量按基础单位；金额按原币呈现；不跨币种换算或合并",
            ["dynamic:sales-order"] = "金额按订单原币呈现，不跨币种换算或合并",
            ["export:product-export-field-completeness"] = "只读字段完整度（无金额/币种）",
            ["document:trade-document-print"] = "金额按原币呈现；数量按基础单位；不跨币种换算或合并",
            ["document:trade-document-export-excel"] = "金额按原币呈现；数量按基础单位；不跨币种换算或合并",
        };

        Assert.Equal(25, expected.Count);
        foreach (var (key, semantics) in expected)
        {
            var entry = Assert.Single(ReportMigrationRegistryManifest.Entries, e => e.LegacyKey == key);
            Assert.Equal(semantics, entry.CurrencyUnitSemantics);
        }
    }

    [Fact]
    public async Task Parity_漂移条目币种单位仍不匹配_完整证据仍preset_ready()
    {
        using var db = TestDbFactory.Create();
        var user = SeedUserWithMenus(db, "drift-mismatch", "customer");

        var catalog = new FakeCatalog(new Dictionary<string, ReportConfigurationDatasetDto?>
        {
            ["receivable"] = Dataset("receivable", "customer", "金额按原币呈现（故意不同口径）"),
        });
        var evidence = new FakeEvidenceProvider(key => key == "dynamic:receivable"
            ? new ReportMigrationParityEvidenceDto(true, true, true, true)
            : null);
        var registry = BuildRegistry(db, catalog,
            new FakePresetCatalog(k => k == "dynamic:receivable"), evidence);

        var result = await registry.GetRegistryAsync(user.Id);

        var entry = Assert.Single(result.Entries, e => e.LegacyKey == "dynamic:receivable");
        Assert.Equal(ReportMigrationParityStatusText.PresetReady, entry.ParityStatus);
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

    [Fact]
    public async Task Parity_固定报表无旧ExcelPdf_完整证据_parity_passed()
    {
        using var db = TestDbFactory.Create();
        var user = SeedUserWithMenus(db, "fixed-passed", "product-sales-ranking");

        var catalog = new FakeCatalog(new Dictionary<string, ReportConfigurationDatasetDto?>
        {
            ["product-sales-ranking"] = Dataset("product-sales-ranking", "product-sales-ranking",
                "发货数量按基础单位独立（绝不跨单位合计）；金额为数量 × 商品当前售价的估算（币种未知，仅估算）"),
        });
        var evidence = new FakeEvidenceProvider(key => key == "report:product-sales-ranking"
            ? new ReportMigrationParityEvidenceDto(true, true, true, true)
            : null);
        var registry = BuildRegistry(db, catalog,
            new FakePresetCatalog(k => k == "report:product-sales-ranking"), evidence);

        var result = await registry.GetRegistryAsync(user.Id);

        var entry = Assert.Single(result.Entries, e => e.LegacyKey == "report:product-sales-ranking");
        Assert.Equal(ReportMigrationParityStatusText.ParityPassed, entry.ParityStatus);
    }

    [Fact]
    public async Task Parity_财务报表无旧ExcelPdf_完整证据_parity_passed()
    {
        using var db = TestDbFactory.Create();
        var user = SeedUserWithMenus(db, "financial-passed", "balance-sheet");

        var catalog = new FakeCatalog(new Dictionary<string, ReportConfigurationDatasetDto?>
        {
            ["balance-sheet"] = Dataset("balance-sheet", "balance-sheet",
                "金额按单据金额直接汇总（资产/负债/权益）；不跨币种换算"),
        });
        var evidence = new FakeEvidenceProvider(key => key == "report:balance-sheet"
            ? new ReportMigrationParityEvidenceDto(true, true, true, true)
            : null);
        var registry = BuildRegistry(db, catalog,
            new FakePresetCatalog(k => k == "report:balance-sheet"), evidence);

        var result = await registry.GetRegistryAsync(user.Id);

        var entry = Assert.Single(result.Entries, e => e.LegacyKey == "report:balance-sheet");
        Assert.Equal(ReportMigrationParityStatusText.ParityPassed, entry.ParityStatus);
    }

    [Fact]
    public async Task Parity_无旧ExcelPdf_无证据_仍preset_ready()
    {
        using var db = TestDbFactory.Create();
        var user = SeedUserWithMenus(db, "fixed-no-evidence", "product-sales-ranking");

        var catalog = new FakeCatalog(new Dictionary<string, ReportConfigurationDatasetDto?>
        {
            ["product-sales-ranking"] = Dataset("product-sales-ranking", "product-sales-ranking",
                "发货数量按基础单位独立（绝不跨单位合计）；金额为数量 × 商品当前售价的估算（币种未知，仅估算）"),
        });
        // 无旧 Excel/PDF（false,false）是有效（真空）兼容声明，但无四维比对证据仍绝不 parity-passed（fail closed）。
        var registry = BuildRegistry(db, catalog,
            new FakePresetCatalog(k => k == "report:product-sales-ranking"));

        var result = await registry.GetRegistryAsync(user.Id);

        var entry = Assert.Single(result.Entries, e => e.LegacyKey == "report:product-sales-ranking");
        Assert.Equal(ReportMigrationParityStatusText.PresetReady, entry.ParityStatus);
    }

    [Fact]
    public async Task Parity_多菜单导出族_完整菜单集合_parity_passed()
    {
        using var db = TestDbFactory.Create();
        var user = SeedUserWithMenus(db, "multi-passed", "sales-order", "sales-order-export");

        var catalog = new FakeCatalog(new Dictionary<string, ReportConfigurationDatasetDto?>
        {
            ["bill-export:sales-order"] = Dataset("bill-export:sales-order", "sales-order+sales-order-export",
                "金额按原币呈现；数量按基础单位；不跨币种换算或合并"),
        });
        var evidence = new FakeEvidenceProvider(key => key == "export:bill-proc:sales-order"
            ? new ReportMigrationParityEvidenceDto(true, true, true, true)
            : null);
        var registry = BuildRegistry(db, catalog,
            new FakePresetCatalog(k => k == "export:bill-proc:sales-order"), evidence);

        var result = await registry.GetRegistryAsync(user.Id);

        var entry = Assert.Single(result.Entries, e => e.LegacyKey == "export:bill-proc:sales-order");
        Assert.Equal(ReportMigrationParityStatusText.ParityPassed, entry.ParityStatus);
    }

    [Fact]
    public async Task Parity_多菜单导出族_缺失基础菜单_语义不匹配_preset_ready()
    {
        using var db = TestDbFactory.Create();
        var user = SeedUserWithMenus(db, "multi-missing-base", "sales-order", "sales-order-export");

        var catalog = new FakeCatalog(new Dictionary<string, ReportConfigurationDatasetDto?>
        {
            // 数据集只声明专用导出菜单（缺失基础菜单），语义匹配必须失败（fail closed）。
            ["bill-export:sales-order"] = Dataset("bill-export:sales-order", "sales-order-export",
                "金额按原币呈现；数量按基础单位；不跨币种换算或合并"),
        });
        var evidence = new FakeEvidenceProvider(key => key == "export:bill-proc:sales-order"
            ? new ReportMigrationParityEvidenceDto(true, true, true, true)
            : null);
        var registry = BuildRegistry(db, catalog,
            new FakePresetCatalog(k => k == "export:bill-proc:sales-order"), evidence);

        var result = await registry.GetRegistryAsync(user.Id);

        var entry = Assert.Single(result.Entries, e => e.LegacyKey == "export:bill-proc:sales-order");
        Assert.Equal(ReportMigrationParityStatusText.PresetReady, entry.ParityStatus);
    }

    [Fact]
    public async Task Parity_多菜单导出族_缺失专用导出菜单_语义不匹配_preset_ready()
    {
        using var db = TestDbFactory.Create();
        var user = SeedUserWithMenus(db, "multi-missing-export", "sales-order", "sales-order-export");

        var catalog = new FakeCatalog(new Dictionary<string, ReportConfigurationDatasetDto?>
        {
            // 数据集只声明基础菜单（缺失专用导出菜单），语义匹配必须失败（fail closed）。
            ["bill-export:sales-order"] = Dataset("bill-export:sales-order", "sales-order",
                "金额按原币呈现；数量按基础单位；不跨币种换算或合并"),
        });
        var evidence = new FakeEvidenceProvider(key => key == "export:bill-proc:sales-order"
            ? new ReportMigrationParityEvidenceDto(true, true, true, true)
            : null);
        var registry = BuildRegistry(db, catalog,
            new FakePresetCatalog(k => k == "export:bill-proc:sales-order"), evidence);

        var result = await registry.GetRegistryAsync(user.Id);

        var entry = Assert.Single(result.Entries, e => e.LegacyKey == "export:bill-proc:sales-order");
        Assert.Equal(ReportMigrationParityStatusText.PresetReady, entry.ParityStatus);
    }

    [Fact]
    public async Task Parity_16个单据导出族_完整数据预设证据_全部parity_passed()
    {
        using var db = TestDbFactory.Create();

        var billEntries = LegacyBillExportCatalog.RegistryEntries;
        var menuCodes = billEntries
            .SelectMany(e => e.RequiredMenuCodes)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var user = SeedUserWithMenus(db, "all-bill-families", menuCodes);

        var datasets = new Dictionary<string, ReportConfigurationDatasetDto?>();
        foreach (var entry in billEntries)
        {
            datasets[entry.DatasetKey] = Dataset(
                entry.DatasetKey,
                string.Join('+', entry.RequiredMenuCodes),
                entry.CurrencyUnitSemantics);
        }

        var billLegacyKeys = new HashSet<string>(
            billEntries.Select(e => e.LegacyKey),
            StringComparer.Ordinal);

        var registry = BuildRegistry(
            db,
            new FakeCatalog(datasets),
            new FakePresetCatalog(billLegacyKeys.Contains),
            new FakeEvidenceProvider(key => billLegacyKeys.Contains(key)
                ? new ReportMigrationParityEvidenceDto(true, true, true, true)
                : null));

        var result = await registry.GetRegistryAsync(user.Id);

        var passed = result.Entries
            .Where(e => e.LegacyKey.StartsWith("export:bill-proc:", StringComparison.Ordinal))
            .ToList();
        Assert.Equal(16, passed.Count);
        Assert.All(passed, e => Assert.Equal(ReportMigrationParityStatusText.ParityPassed, e.ParityStatus));
    }

    // ==================== 3.5 捆绑组合条目 parity 派生（ERP-322） ====================

    private static ReportConfigurationBundlePresetDto PacketBundlePreset(
        string readiness, params string[] sectionDatasetKeys)
        => BundlePreset("packet:customer-report-packet", readiness, sectionDatasetKeys);

    [Fact]
    public async Task Packet_单数据集缺失但捆绑预设各节就绪_preset_ready()
    {
        using var db = TestDbFactory.Create();
        var user = SeedUserWithMenus(db, "packet-ready", "sales-order", "customer");

        var catalog = new FakeCatalog(new Dictionary<string, ReportConfigurationDatasetDto?>
        {
            ["sales-order"] = Dataset("sales-order", "sales-order", "金额按订单原币呈现，不跨币种换算或合并"),
            ["receivable"] = Dataset("receivable", "customer", "金额按原币呈现"),
        });
        var bundlePresets = new FakeBundlePresetCatalog(
            PacketBundlePreset(ReportConfigurationBundlePresetConstants.ReadinessPresetReady, "sales-order", "receivable"));
        var registry = BuildRegistry(db, catalog, bundlePresets: bundlePresets);

        var result = await registry.GetRegistryAsync(user.Id);

        var entry = Assert.Single(result.Entries, e => e.LegacyKey == "packet:customer-report-packet");
        Assert.Equal(ReportMigrationParityStatusText.PresetReady, entry.ParityStatus);
    }

    [Fact]
    public async Task Packet_单数据集缺失_任一节数据集缺失_pending()
    {
        using var db = TestDbFactory.Create();
        var user = SeedUserWithMenus(db, "packet-missing", "sales-order", "customer");

        var catalog = new FakeCatalog(new Dictionary<string, ReportConfigurationDatasetDto?>
        {
            ["sales-order"] = Dataset("sales-order", "sales-order", "金额按订单原币呈现，不跨币种换算或合并"),
        });
        var bundlePresets = new FakeBundlePresetCatalog(
            PacketBundlePreset(ReportConfigurationBundlePresetConstants.ReadinessPresetReady, "sales-order", "receivable"));
        var registry = BuildRegistry(db, catalog, bundlePresets: bundlePresets);

        var result = await registry.GetRegistryAsync(user.Id);

        var entry = Assert.Single(result.Entries, e => e.LegacyKey == "packet:customer-report-packet");
        Assert.Equal(ReportMigrationParityStatusText.Pending, entry.ParityStatus);
    }

    [Fact]
    public async Task Packet_单数据集缺失_捆绑预设未列出_pending()
    {
        using var db = TestDbFactory.Create();
        var user = SeedUserWithMenus(db, "packet-no-preset", "sales-order", "customer");

        var catalog = new FakeCatalog(new Dictionary<string, ReportConfigurationDatasetDto?>
        {
            ["sales-order"] = Dataset("sales-order", "sales-order", "金额按订单原币呈现，不跨币种换算或合并"),
            ["receivable"] = Dataset("receivable", "customer", "金额按原币呈现"),
        });
        var registry = BuildRegistry(db, catalog, bundlePresets: new FakeBundlePresetCatalog());

        var result = await registry.GetRegistryAsync(user.Id);

        var entry = Assert.Single(result.Entries, e => e.LegacyKey == "packet:customer-report-packet");
        Assert.Equal(ReportMigrationParityStatusText.Pending, entry.ParityStatus);
    }

    [Fact]
    public async Task Packet_捆绑预设各节就绪_无证据_拒绝parity_passed()
    {
        using var db = TestDbFactory.Create();
        var user = SeedUserWithMenus(db, "packet-no-evidence", "sales-order", "customer");

        var catalog = new FakeCatalog(new Dictionary<string, ReportConfigurationDatasetDto?>
        {
            ["sales-order"] = Dataset("sales-order", "sales-order", "金额按订单原币呈现，不跨币种换算或合并"),
            ["receivable"] = Dataset("receivable", "customer", "金额按原币呈现"),
        });
        var bundlePresets = new FakeBundlePresetCatalog(
            PacketBundlePreset(ReportConfigurationBundlePresetConstants.ReadinessPresetReady, "sales-order", "receivable"));
        var registry = BuildRegistry(db, catalog, bundlePresets: bundlePresets);

        var result = await registry.GetRegistryAsync(user.Id);

        var entry = Assert.Single(result.Entries, e => e.LegacyKey == "packet:customer-report-packet");
        Assert.Equal(ReportMigrationParityStatusText.PresetReady, entry.ParityStatus);
        Assert.False(await registry.CanRetireLegacyRoutesAsync(user.Id));
    }

    [Fact]
    public async Task Packet_捆绑预设各节就绪_有完整证据_parity_passed()
    {
        using var db = TestDbFactory.Create();
        var user = SeedUserWithMenus(db, "packet-evidence", "sales-order", "customer");

        var catalog = new FakeCatalog(new Dictionary<string, ReportConfigurationDatasetDto?>
        {
            ["sales-order"] = Dataset("sales-order", "sales-order", "金额按订单原币呈现，不跨币种换算或合并"),
            ["receivable"] = Dataset("receivable", "customer", "金额按原币呈现"),
        });
        var bundlePresets = new FakeBundlePresetCatalog(
            PacketBundlePreset(ReportConfigurationBundlePresetConstants.ReadinessPresetReady, "sales-order", "receivable"));
        var evidence = new FakeEvidenceProvider(key => key == "packet:customer-report-packet"
            ? new ReportMigrationParityEvidenceDto(true, true, true, true)
            : null);
        var registry = BuildRegistry(db, catalog, evidence: evidence, bundlePresets: bundlePresets);

        var result = await registry.GetRegistryAsync(user.Id);

        var entry = Assert.Single(result.Entries, e => e.LegacyKey == "packet:customer-report-packet");
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

    [Fact]
    public void CanRetireLegacyRoutes_门控清单不含被取代的聚合占位()
    {
        var gateDefinitions = ReportMigrationRegistryManifest.Entries
            .Concat(LegacyBillExportCatalog.RegistryEntries)
            .Concat(ReportPrintTemplateFamilies.RegistryEntries)
            .ToList();

        Assert.DoesNotContain(gateDefinitions, d => d.LegacyKey == "export:bill-proc");
        Assert.DoesNotContain(gateDefinitions, d => d.DatasetKey == "bill-export");
    }
}


