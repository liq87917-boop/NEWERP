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
/// ERP-319 旧单据导出族有界一致只读快照的单元测试（内存数据库，不连接 SQL Server、不执行真实事务）：
/// 覆盖 16 族能力声明（matched-set 支持 / all-match-total 不支持）、快照打开时的菜单 + 特权全量数据范围校验、
/// 字段 / 关键字 / 状态 / 日期映射、匹配计数与显示页区分、越界 / 零行、取消传播与未知字段拒绝。
/// <para>真实 SQL Server 快照（Serializable 事务 / 缺表缺列 / 并发写入 / 真实工件行数）由集成测试覆盖。</para>
/// </summary>
public class ReportConfigurationLegacyBillSnapshotTests
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
        ErpDbContext db, string familyKey, FakeBillExportReader reader)
        => new(db, reader, LegacyBillExportCatalog.Resolve(familyKey).DatasetKey);

    private static ReportConfigurationDefinition Definition(string datasetKey, params string[] fields)
        => new()
        {
            SchemaVersion = ReportConfigurationRules.CurrentSchemaVersion,
            DatasetKey = datasetKey,
            Fields = fields.ToList(),
        };

    private static ReportConfigurationPreviewParameters Params()
        => new(1, 100, ReportConfigurationConstants.GroupNone, null, null);

    private static IReportConfigurationReadSnapshot BuildSnapshot(
        string correlationId,
        string datasetKey,
        IReadOnlyList<Dictionary<string, object?>> rows,
        IReadOnlyList<ReportConfigurationColumnDto> columns,
        ReportConfigurationEvidenceContextDto evidence)
        => new ReportConfigurationReadSnapshot(
            correlationId,
            datasetKey,
            evidence.Coverage,
            rows.Count,
            Enumerable.Range(1, rows.Count).Select(i => (long)i).ToList(),
            rows,
            columns,
            evidence,
            "privileged",
            0,
            isConsistent: true,
            transaction: null);

    [Fact]
    public async Task 快照能力_16族均声明支持匹配集且不支持全匹配合计()
    {
        using var db = TestDbFactory.Create();
        var menuCodes = LegacyBillExportCatalog.Families
            .SelectMany(f => f.RequiredMenuCodes)
            .Distinct()
            .ToArray();
        var user = SeedAuthorizedUser(db, "snap-cap", menuCodes);

        foreach (var family in LegacyBillExportCatalog.Families)
        {
            var provider = new LegacyBillExportReportConfigurationDatasetProvider(
                db, new FakeBillExportReader(), family.DatasetKey);

            Assert.True(provider.SupportsReadSnapshot);
            Assert.IsAssignableFrom<IReportConfigurationSnapshotDatasetProvider>(provider);

            var dataset = await provider.GetDatasetAsync(user.Id);
            Assert.NotNull(dataset);
            Assert.Contains(ReportConfigurationConstants.CapabilityMatchedSet, dataset!.SupportedCapabilities);
            Assert.Contains(ReportConfigurationConstants.CapabilityAllMatchTotal, dataset.UnsupportedCapabilities);
        }
    }

    [Fact]
    public async Task 打开快照_映射筛选字段并返回一致快照与匹配集证据()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "snap-open", "receipt");
        var fake = new FakeBillExportReader();
        var provider = BuildProvider(db, "receipt", fake);

        var definition = Definition(provider.DatasetKey, "BillNo", "Amount", "Remark");
        definition.Coverage = ReportConfigurationConstants.CoverageMatchedSet;
        definition.Filters = new List<ReportConfigurationFilter>
        {
            new() { FieldKey = "BillNo", Operator = ReportConfigurationConstants.OperatorEq, Value = "R-" },
            new() { FieldKey = "Status", Operator = ReportConfigurationConstants.OperatorEq, Value = 2 },
            new() { FieldKey = "ReceiptDate", Operator = ReportConfigurationConstants.OperatorGte, Value = new DateTime(2026, 9, 1) },
            new() { FieldKey = "ReceiptDate", Operator = ReportConfigurationConstants.OperatorLte, Value = new DateTime(2026, 9, 30) },
        };

        var snapshot = await provider.OpenReadSnapshotAsync(definition, Params(), user.Id, "corr-1");

        Assert.NotNull(snapshot);
        Assert.Equal("corr-1", snapshot.CorrelationId);
        Assert.Equal(provider.DatasetKey, snapshot.DatasetKey);
        Assert.True(snapshot.IsConsistent);
        Assert.Equal("privileged", snapshot.ScopeFingerprint);
        Assert.Equal(ReportConfigurationConstants.CoverageMatchedSet, snapshot.Evidence.Coverage);

        var query = fake.LastSnapshotQuery;
        Assert.NotNull(query);
        Assert.Equal("receipt", query!.FamilyKey);
        Assert.Equal(new[] { "BillNo", "Amount", "Remark" }, query.Fields.ToArray());
        Assert.Equal("R-", query.Keyword);
        Assert.Equal(2, query.Status);
        Assert.Equal(new DateTime(2026, 9, 1), query.StartDate);
        Assert.Equal(new DateTime(2026, 9, 30), query.EndDate);
    }

    [Fact]
    public void 渲染匹配页_区分匹配计数与显示页且保留列顺序()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "snap-render", "receipt");
        var provider = BuildProvider(db, "receipt", new FakeBillExportReader());

        var columns = new List<ReportConfigurationColumnDto>
        {
            new("BillNo", "单据号", "text", null),
            new("Amount", "金额", "number", "原币金额"),
        };
        var rows = Enumerable.Range(1, 5).Select(i => new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["BillNo"] = $"R-{i}",
            ["Amount"] = 100m + i,
        }).ToList();
        var evidence = new ReportConfigurationEvidenceContextDto(
            provider.DatasetKey, "一行一条单据", "金额按原币呈现", "只读", "边界", "免责",
            ReportConfigurationConstants.CoverageMatchedSet);

        var snapshot = BuildSnapshot("corr-2", provider.DatasetKey, rows, columns, evidence);

        var preview = provider.RenderMatchedPage(
            snapshot, Definition(provider.DatasetKey, "BillNo", "Amount"),
            new ReportConfigurationPreviewParameters(2, 2, ReportConfigurationConstants.GroupNone, null, null));

        Assert.Equal(5, preview.MatchedCount);
        Assert.Equal(5, preview.Total);
        Assert.Equal(2, preview.Page);
        Assert.Equal(2, preview.PageSize);
        Assert.Equal(3, preview.TotalPages);
        Assert.Equal(2, preview.Rows.Count);
        Assert.Equal("R-3", (string)preview.Rows[0]["BillNo"]!);
        Assert.Equal("R-4", (string)preview.Rows[1]["BillNo"]!);
        Assert.Equal(new[] { "BillNo", "Amount" }, preview.Columns.Select(c => c.Key).ToArray());
    }

    [Fact]
    public void 渲染匹配页_零行与越界页()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "snap-empty", "receipt");
        var provider = BuildProvider(db, "receipt", new FakeBillExportReader());

        var columns = new List<ReportConfigurationColumnDto> { new("BillNo", "单据号", "text", null) };
        var evidence = new ReportConfigurationEvidenceContextDto(
            provider.DatasetKey, "一行一条单据", "金额按原币呈现", "只读", "边界", "免责",
            ReportConfigurationConstants.CoverageMatchedSet);

        var empty = BuildSnapshot("corr-3", provider.DatasetKey, new List<Dictionary<string, object?>>(), columns, evidence);
        var preview = provider.RenderMatchedPage(
            empty, Definition(provider.DatasetKey, "BillNo"),
            new ReportConfigurationPreviewParameters(1, 20, ReportConfigurationConstants.GroupNone, null, null));

        Assert.Equal(0, preview.MatchedCount);
        Assert.Equal(0, preview.Total);
        Assert.Equal(0, preview.TotalPages);
        Assert.Empty(preview.Rows);

        var rows = Enumerable.Range(1, 3).Select(i => new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["BillNo"] = $"R-{i}",
        }).ToList();
        var snapshot = BuildSnapshot("corr-4", provider.DatasetKey, rows, columns, evidence);
        var beyond = provider.RenderMatchedPage(
            snapshot, Definition(provider.DatasetKey, "BillNo"),
            new ReportConfigurationPreviewParameters(5, 20, ReportConfigurationConstants.GroupNone, null, null));

        Assert.Equal(3, beyond.MatchedCount);
        Assert.Equal(3, beyond.Total);
        Assert.Empty(beyond.Rows);
    }

    [Fact]
    public async Task 打开快照_受限账号拒绝()
    {
        using var db = TestDbFactory.Create();
        var user = SeedRestrictedAuthorizedUser(db, "snap-restricted", "receipt");
        var provider = BuildProvider(db, "receipt", new FakeBillExportReader());

        var ex = await Assert.ThrowsAsync<BusinessException>(() => provider.OpenReadSnapshotAsync(
            Definition(provider.DatasetKey, "BillNo"), Params(), user.Id, "corr-5"));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    [Fact]
    public async Task 打开快照_无身份拒绝()
    {
        using var db = TestDbFactory.Create();
        var provider = BuildProvider(db, "receipt", new FakeBillExportReader());

        var ex = await Assert.ThrowsAsync<BusinessException>(() => provider.OpenReadSnapshotAsync(
            Definition(provider.DatasetKey, "BillNo"), Params(), null, "corr-6"));
        Assert.Equal(ErrorCodes.Unauthorized, ex.Code);
    }

    [Fact]
    public async Task 打开快照_未知字段拒绝()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "snap-field", "receipt");
        var provider = BuildProvider(db, "receipt", new FakeBillExportReader());

        var ex = await Assert.ThrowsAsync<BusinessException>(() => provider.OpenReadSnapshotAsync(
            Definition(provider.DatasetKey, "BillNo", "NotAColumn"), Params(), user.Id, "corr-7"));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public async Task 打开快照_取消传播不降级()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "snap-cancel", "receipt");
        var provider = BuildProvider(db, "receipt", new FakeBillExportReader
        {
            SnapshotError = new OperationCanceledException("已取消"),
        });

        await Assert.ThrowsAsync<OperationCanceledException>(() => provider.OpenReadSnapshotAsync(
            Definition(provider.DatasetKey, "BillNo"), Params(), user.Id, "corr-8"));
    }

    private sealed class FakeBillExportReader : ILegacyBillExportReadService
    {
        public LegacyBillExportQuery? LastSnapshotQuery { get; private set; }
        public Exception? SnapshotError { get; set; }

        public Task<LegacyBillExportPage> ReadPageAsync(LegacyBillExportQuery query, CancellationToken cancellationToken = default)
            => Task.FromResult(new LegacyBillExportPage());

        public Task<IReportConfigurationReadSnapshot> ReadSnapshotAsync(
            LegacyBillExportQuery query,
            string correlationId,
            string scopeFingerprint,
            IReadOnlyList<ReportConfigurationColumnDto> columns,
            ReportConfigurationEvidenceContextDto evidence,
            CancellationToken cancellationToken = default)
        {
            LastSnapshotQuery = query;
            if (SnapshotError is not null)
                throw SnapshotError;

            return Task.FromResult<IReportConfigurationReadSnapshot>(new ReportConfigurationReadSnapshot(
                correlationId,
                evidence.DatasetKey,
                evidence.Coverage,
                0,
                Array.Empty<long>(),
                new List<Dictionary<string, object?>>(),
                columns,
                evidence,
                scopeFingerprint,
                0,
                isConsistent: true,
                transaction: null));
        }
    }
}
