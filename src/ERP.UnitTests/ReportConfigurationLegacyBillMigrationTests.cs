using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using ERP.Infrastructure.Reports;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-308 旧单据导出族迁移的单元测试：覆盖 16 个族目录的精确列 / 日期 / 菜单映射、未知 / 畸形 / 注入标识与未知列拒绝、
/// 数据集适配器的菜单 + 启用账号 + 特权全量数据范围（fail closed）、预览把日期 / 状态 / 关键字筛选映射到受控读取查询并原样透传行 / null，
/// 以及迁移登记册 16 个显式族键均 preset-ready（绝不因目录存在而 parity-passed）。
/// <para>全部使用内存数据库（TestDbFactory），受控读取使用测试替身，不连接 SQL Server、不执行 SQL / seed。</para>
/// </summary>
public class ReportConfigurationLegacyBillMigrationTests
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

    private static LegacyBillExportReportConfigurationDatasetProvider BuildProvider(
        ErpDbContext db, string familyKey, ILegacyBillExportReadService? reader = null)
        => new(db, reader ?? new FakeBillExportReader(), LegacyBillExportCatalog.Resolve(familyKey).DatasetKey);

    private static ReportConfigurationCatalog BuildCatalog(ErpDbContext db, ILegacyBillExportReadService? reader = null)
    {
        var providers = LegacyBillExportCatalog.Families
            .Select(f => (IReportConfigurationDatasetProvider)new LegacyBillExportReportConfigurationDatasetProvider(
                db, reader ?? new FakeBillExportReader(), f.DatasetKey))
            .ToList();
        return new ReportConfigurationCatalog(providers);
    }

    private static ReportConfigurationDefinition Definition(string datasetKey, params string[] fields)
        => new()
        {
            SchemaVersion = ReportConfigurationRules.CurrentSchemaVersion,
            DatasetKey = datasetKey,
            Fields = fields.ToList(),
        };

    private static ReportConfigurationPreviewParameters Params()
        => new(1, 100, ReportConfigurationConstants.GroupNone, null, null);

    [Fact]
    public void 目录_恰好16族且每族首列单据号末列备注含状态()
    {
        Assert.Equal(16, LegacyBillExportCatalog.Families.Count);
        foreach (var family in LegacyBillExportCatalog.Families)
        {
            Assert.Equal("BillNo", family.Columns[0].Key);
            Assert.Equal("Remark", family.Columns[^1].Key);
            Assert.Contains(family.Columns, c => c.Key == "Status");
            Assert.All(family.Columns, c => Assert.False(string.IsNullOrWhiteSpace(c.Key)));
            Assert.False(string.IsNullOrWhiteSpace(family.TableName));
            Assert.False(string.IsNullOrWhiteSpace(family.DateField));
        }
    }

    [Fact]
    public void 目录_销售订单族_精确映射与双菜单()
    {
        var family = LegacyBillExportCatalog.Resolve("sales-order");
        Assert.Equal("SalesOrder", family.TableName);
        Assert.Equal("OrderDate", family.DateField);
        Assert.Equal(new[] { "sales-order", "sales-order-export" }, family.RequiredMenuCodes);
        Assert.Equal(12, family.Columns.Count);
        Assert.Equal("bill-export:sales-order", family.DatasetKey);
    }

    [Fact]
    public void 目录_收款单族_精确映射与单菜单()
    {
        var family = LegacyBillExportCatalog.Resolve("receipt");
        Assert.Equal("FinanceReceipt", family.TableName);
        Assert.Equal("ReceiptDate", family.DateField);
        Assert.Equal(new[] { "receipt" }, family.RequiredMenuCodes);
        Assert.Equal(9, family.Columns.Count);
        Assert.Equal("bill-export:receipt", family.DatasetKey);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("sales-order; DROP TABLE SalesOrder")]
    [InlineData("sales-order' OR 1=1--")]
    [InlineData("sales order")]
    [InlineData("bill-export:sales-order")]
    public void 目录_未知或畸形族标识_打开查询前拒绝(string key)
    {
        var ex = Assert.Throws<BusinessException>(() => LegacyBillExportCatalog.Resolve(key));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public void 目录_未知列_拒绝()
    {
        var family = LegacyBillExportCatalog.Resolve("receipt");
        var ex = Assert.Throws<BusinessException>(() =>
            LegacyBillExportCatalog.ResolveColumns(family, new[] { "BillNo", "NotAColumn" }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public void 目录_请求列去重且保序()
    {
        var family = LegacyBillExportCatalog.Resolve("receipt");
        var columns = LegacyBillExportCatalog.ResolveColumns(family, new[] { "Remark", "BillNo", "BillNo", "Status" });
        Assert.Equal(new[] { "Remark", "BillNo", "Status" }, columns.Select(c => c.Key).ToArray());
    }

    [Fact]
    public async Task 特权用户_收款单数据集暴露且字段顺序与旧导出一致()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "receipt-admin", "receipt");
        var provider = BuildProvider(db, "receipt");

        var dataset = await provider.GetDatasetAsync(user.Id);

        Assert.NotNull(dataset);
        var keys = dataset!.Fields.Select(f => f.Key).ToArray();
        Assert.Equal(LegacyBillExportCatalog.Resolve("receipt").Columns.Select(c => c.Key).ToArray(), keys);
    }

    [Fact]
    public async Task 受限制用户_有菜单_数据集不暴露且预览拒绝()
    {
        using var db = TestDbFactory.Create();
        var user = SeedRestrictedAuthorizedUser(db, "receipt-restricted", "receipt");
        var provider = BuildProvider(db, "receipt");

        Assert.Null(await provider.GetDatasetAsync(user.Id));

        var ex = await Assert.ThrowsAsync<BusinessException>(() => provider.PreviewAsync(
            Definition(provider.DatasetKey, "BillNo", "ReceiptDate", "Amount"), Params(), user.Id));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    [Fact]
    public async Task 菜单撤销后_数据集不暴露且预览拒绝()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "receipt-revoke", "receipt");
        var provider = BuildProvider(db, "receipt");

        Assert.NotNull(await provider.GetDatasetAsync(user.Id));

        var link = Assert.Single(db.SysRoleMenus.ToList());
        db.SysRoleMenus.Remove(link);
        db.SaveChanges();

        Assert.Null(await provider.GetDatasetAsync(user.Id));

        var ex = await Assert.ThrowsAsync<BusinessException>(() => provider.PreviewAsync(
            Definition(provider.DatasetKey, "BillNo", "ReceiptDate", "Amount"), Params(), user.Id));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    [Fact]
    public async Task 无身份_预览未认证拒绝()
    {
        using var db = TestDbFactory.Create();
        var provider = BuildProvider(db, "receipt");

        var ex = await Assert.ThrowsAsync<BusinessException>(() => provider.PreviewAsync(
            Definition(provider.DatasetKey, "BillNo"), Params(), null));
        Assert.Equal(ErrorCodes.Unauthorized, ex.Code);
    }

    [Fact]
    public async Task 预览_日期状态关键字映射到受控读取并透传行与null()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "receipt-preview", "receipt");
        var fake = new FakeBillExportReader
        {
            Result = new LegacyBillExportPage
            {
                Rows = new List<Dictionary<string, object?>>
                {
                    new(StringComparer.Ordinal)
                    {
                        ["BillNo"] = "R-1",
                        ["ReceiptDate"] = new DateTime(2026, 9, 1),
                        ["Amount"] = 100.5m,
                        ["Remark"] = null,
                    },
                },
                Total = 1,
                Page = 1,
                PageSize = 100,
                TotalPages = 1,
            },
        };

        var provider = BuildProvider(db, "receipt", fake);
        var definition = Definition(provider.DatasetKey, "BillNo", "ReceiptDate", "Amount", "Remark");
        definition.Filters = new List<ReportConfigurationFilter>
        {
            new() { FieldKey = "BillNo", Operator = ReportConfigurationConstants.OperatorEq, Value = "R-" },
            new() { FieldKey = "Status", Operator = ReportConfigurationConstants.OperatorEq, Value = 2 },
            new() { FieldKey = "ReceiptDate", Operator = ReportConfigurationConstants.OperatorGte, Value = new DateTime(2026, 9, 1) },
            new() { FieldKey = "ReceiptDate", Operator = ReportConfigurationConstants.OperatorLte, Value = new DateTime(2026, 9, 30) },
        };

        var preview = await provider.PreviewAsync(definition, Params(), user.Id);

        Assert.Equal(1, preview.Total);
        Assert.Equal("R-1", (string)preview.Rows[0]["BillNo"]!);
        Assert.Equal(100.5m, (decimal)preview.Rows[0]["Amount"]!);
        Assert.Null(preview.Rows[0]["Remark"]);

        Assert.Equal("receipt", fake.LastQuery!.FamilyKey);
        Assert.Equal("R-", fake.LastQuery.Keyword);
        Assert.Equal(2, fake.LastQuery.Status);
        Assert.Equal(new DateTime(2026, 9, 1), fake.LastQuery.StartDate);
        Assert.Equal(new DateTime(2026, 9, 30), fake.LastQuery.EndDate);
        Assert.Equal(new[] { "BillNo", "ReceiptDate", "Amount", "Remark" }, fake.LastQuery.Fields.ToArray());
    }

    [Fact]
    public async Task 预览_未知字段_拒绝()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "receipt-field", "receipt");
        var provider = BuildProvider(db, "receipt");

        var ex = await Assert.ThrowsAsync<BusinessException>(() => provider.PreviewAsync(
            Definition(provider.DatasetKey, "BillNo", "NotAColumn"), Params(), user.Id));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public async Task 迁移登记册_16族显式且均preset就绪非parity通过()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "registry-bill",
            "stock-in", "stock-out", "receipt", "payment", "deposit-apply", "payment-apply",
            "container-settlement", "bulk-settlement", "complaint", "receiving-plan", "booking",
            "pre-loading", "loading-list", "sales-order", "sales-order-export",
            "purchase-order", "purchase-order-export", "inquiry", "inquiry-export");

        var registry = new ReportMigrationRegistry(
            BuildCatalog(db), db, new ReportMigrationPresetCatalog(), new EmptyReportMigrationParityEvidenceProvider());

        var result = await registry.GetRegistryAsync(user.Id);
        var billEntries = result.Entries.Where(e => e.LegacyKey.StartsWith("export:bill-proc:", StringComparison.Ordinal)).ToList();

        Assert.Equal(16, billEntries.Count);
        Assert.All(billEntries, e => Assert.Equal(ReportMigrationParityStatusText.PresetReady, e.ParityStatus));
    }

    [Fact]
    public async Task 迁移登记册_多菜单导出族_完整证据_parity_passed()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "registry-multi-parity",
            "sales-order", "sales-order-export",
            "purchase-order", "purchase-order-export",
            "inquiry", "inquiry-export");

        var evidence = new FakeParityEvidenceProvider(key =>
            key is "export:bill-proc:sales-order" or "export:bill-proc:purchase-order" or "export:bill-proc:inquiry"
                ? new ReportMigrationParityEvidenceDto(true, true, true, true)
                : null);

        var registry = new ReportMigrationRegistry(
            BuildCatalog(db), db, new ReportMigrationPresetCatalog(), evidence);

        var result = await registry.GetRegistryAsync(user.Id);

        Assert.Equal(ReportMigrationParityStatusText.ParityPassed,
            result.Entries.Single(e => e.LegacyKey == "export:bill-proc:sales-order").ParityStatus);
        Assert.Equal(ReportMigrationParityStatusText.ParityPassed,
            result.Entries.Single(e => e.LegacyKey == "export:bill-proc:purchase-order").ParityStatus);
        Assert.Equal(ReportMigrationParityStatusText.ParityPassed,
            result.Entries.Single(e => e.LegacyKey == "export:bill-proc:inquiry").ParityStatus);
    }

    [Theory]
    [InlineData("registry-missing-base", "sales-order-export")]
    [InlineData("registry-missing-export", "sales-order")]
    public async Task 迁移登记册_多菜单导出族_缺失任一必需菜单_隐藏(string name, string menuCode)
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, name, menuCode);

        var registry = new ReportMigrationRegistry(
            BuildCatalog(db), db, new ReportMigrationPresetCatalog(), new EmptyReportMigrationParityEvidenceProvider());

        var result = await registry.GetRegistryAsync(user.Id);

        Assert.DoesNotContain(result.Entries, e => e.LegacyKey == "export:bill-proc:sales-order");
    }

    private sealed class FakeParityEvidenceProvider : IReportMigrationParityEvidenceProvider
    {
        private readonly Func<string, ReportMigrationParityEvidenceDto?> _evidence;

        public FakeParityEvidenceProvider(Func<string, ReportMigrationParityEvidenceDto?> evidence) => _evidence = evidence;

        public ReportMigrationParityEvidenceDto? GetEvidence(string legacyKey) => _evidence(legacyKey);
    }

    private sealed class FakeBillExportReader : ILegacyBillExportReadService
    {
        public LegacyBillExportQuery? LastQuery { get; private set; }
        public LegacyBillExportPage Result { get; set; } = new();

        public Task<LegacyBillExportPage> ReadPageAsync(LegacyBillExportQuery query, CancellationToken cancellationToken = default)
        {
            LastQuery = query;
            return Task.FromResult(Result);
        }
    }
}
