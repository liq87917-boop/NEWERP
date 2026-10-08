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
using System.Globalization;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-338 Stage 2 三方一致读取作用域单元测试（内存数据库，不连接 SQL Server / 不执行 SQL）：
/// 验证关系型作用域工厂在非 SQL Server 后端显式 environment-blocked，以及证据服务在一致读取作用域内
/// 只打开一次作用域、记录证据前做最终菜单 / 数据范围复核、撤销 / 整页截断 fail closed。
/// </summary>
public class ReportMigrationParityConsistencyTests
{
    private const string Text = ReportConfigurationConstants.TypeText;

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

    private static string ComputeScopeFingerprint(SalespersonDataScope scope)
        => scope.IsPrivileged
            ? "privileged"
            : (scope.SalesmanId?.ToString(CultureInfo.InvariantCulture) ?? "none")
              + "|" + string.Join(",", (scope.AllowedCustomerIds ?? new HashSet<long>()).OrderBy(x => x));

    private static ReportMigrationParitySnapshotDto Snapshot(int rowCount)
    {
        var columns = new[] { new ReportMigrationParityColumnDto("orderNo", Text, null, null) };
        var rows = Enumerable.Range(1, rowCount)
            .Select(i => new ReportMigrationParityRowDto(
                new[] { "S" + i },
                null,
                null,
                new object?[] { "S" + i }))
            .ToList();
        return new ReportMigrationParitySnapshotDto(
            columns, rows, new ReportMigrationParityPermissionsDto(Array.Empty<long>(), "privileged"));
    }

    private static ReportConfigurationDatasetDto Dataset()
        => new(
            "product-sales-ranking",
            "product-sales-ranking",
            "product-sales-ranking",
            "只读",
            "product-sales-ranking",
            "商品销量排名榜",
            new List<ReportConfigurationFieldDto>
            {
                new("orderNo", "订单号", Text, null, true, false, false, Array.Empty<string>()),
            },
            Array.Empty<string>(),
            Array.Empty<string>(),
            Array.Empty<string>(),
            20,
            100,
            string.Empty,
            string.Empty);

    private static ReportConfigurationPreviewDto Preview(int rowCount, int? total = null)
        => new()
        {
            DatasetKey = "product-sales-ranking",
            Columns = new List<ReportConfigurationColumnDto>
            {
                new("orderNo", "订单号", Text, null),
            },
            Rows = Enumerable.Range(1, rowCount)
                .Select(i => new Dictionary<string, object?> { ["orderNo"] = "S" + i })
                .ToList(),
            Total = total ?? rowCount,
            Page = 1,
            PageSize = rowCount,
            TotalPages = total is not null && total > rowCount ? 2 : 1,
            GroupBy = ReportConfigurationConstants.GroupNone,
        };

    private static IReportConfigurationDatasetProvider[] Providers(
        ReportConfigurationDatasetDto dataset, ReportConfigurationPreviewDto preview, Action? onPreview = null)
        => new IReportConfigurationDatasetProvider[] { new FakeProvider(dataset, preview, onPreview) };

    private sealed class FakeProvider : IReportConfigurationDatasetProvider
    {
        private readonly ReportConfigurationDatasetDto _dataset;
        private readonly ReportConfigurationPreviewDto _preview;
        private readonly Action? _onPreview;

        public FakeProvider(
            ReportConfigurationDatasetDto dataset, ReportConfigurationPreviewDto preview, Action? onPreview = null)
        {
            _dataset = dataset;
            _preview = preview;
            _onPreview = onPreview;
        }

        public string DatasetKey => "product-sales-ranking";

        public Task<ReportConfigurationDatasetDto?> GetDatasetAsync(long? userId, CancellationToken cancellationToken = default)
            => Task.FromResult<ReportConfigurationDatasetDto?>(_dataset);

        public Task<ReportConfigurationPreviewDto> PreviewAsync(
            ReportConfigurationDefinition definition,
            ReportConfigurationPreviewParameters parameters,
            long? userId,
            CancellationToken cancellationToken = default)
        {
            _onPreview?.Invoke();
            return Task.FromResult(_preview);
        }
    }

    private sealed class FakeLegacySource : ILegacyReportSource
    {
        private readonly ReportMigrationParitySnapshotDto _snapshot;
        public int ReadCount { get; private set; }

        public FakeLegacySource(ReportMigrationParitySnapshotDto snapshot) => _snapshot = snapshot;

        public Task<LegacyReportSourceResult> ReadAsync(
            LegacyReportSourceRequest request, CancellationToken cancellationToken = default)
        {
            ReadCount++;
            return Task.FromResult(new LegacyReportSourceResult(
                request.LegacyKey,
                ReportMigrationRegistryCategories.FixedReport,
                LegacyReportSourceStatus.Success,
                null,
                null,
                _snapshot));
        }
    }

    // ==================== 内存版作用域工厂 ====================

    private sealed class RecordingReadScopeFactory : IReportMigrationParityReadScopeFactory
    {
        private readonly IErpDbContext _db;
        private readonly IReadOnlyList<IReportConfigurationDatasetProvider> _providers;

        public RecordingReadScopeFactory(
            IErpDbContext db, IReadOnlyList<IReportConfigurationDatasetProvider> providers)
        {
            _db = db;
            _providers = providers;
        }

        public int OpenCount { get; private set; }
        public MemoryReadScope? LastScope { get; private set; }

        public async Task<IReportMigrationParityReadScope> OpenAsync(
            long ownerUserId, string correlationId, CancellationToken cancellationToken = default)
        {
            OpenCount++;
            var fingerprint = ComputeScopeFingerprint(
                await SalespersonDataScopeService.ResolveAsync(_db, ownerUserId));
            var scope = new MemoryReadScope(correlationId, _db, _providers, fingerprint);
            LastScope = scope;
            return scope;
        }
    }

    private sealed class MemoryReadScope : IReportMigrationParityReadScope
    {
        private readonly IErpDbContext _db;
        private readonly IReadOnlyList<IReportConfigurationDatasetProvider> _providers;
        private readonly string _scopeFingerprint;

        public MemoryReadScope(
            string correlationId,
            IErpDbContext db,
            IReadOnlyList<IReportConfigurationDatasetProvider> providers,
            string scopeFingerprint)
        {
            CorrelationId = correlationId;
            _db = db;
            _providers = providers;
            _scopeFingerprint = scopeFingerprint;
        }

        public string CorrelationId { get; }
        public bool IsConsistent => true;
        public int RecheckCount { get; private set; }

        public async Task RecheckAsync(
            long ownerUserId, ReportMigrationParityReadTarget target, CancellationToken cancellationToken = default)
        {
            RecheckCount++;

            var authorizedMenus = await CustomerReceivableReconciliationService.LoadAuthorizedMenuCodesAsync(
                _db, ownerUserId);
            if (target.RequiredMenuCodes.Count == 0 || !target.RequiredMenuCodes.All(authorizedMenus.Contains))
                throw new BusinessException("菜单授权已撤销", ErrorCodes.Forbidden);

            var provider = _providers.FirstOrDefault(p =>
                string.Equals(p.DatasetKey, target.DatasetKey, StringComparison.OrdinalIgnoreCase))
                ?? throw new BusinessException("未知数据集", ErrorCodes.InvalidParameter);
            if (await provider.GetDatasetAsync(ownerUserId, cancellationToken) is null)
                throw new BusinessException("数据集授权已撤销", ErrorCodes.Forbidden);

            var scope = await SalespersonDataScopeService.ResolveAsync(_db, ownerUserId);
            if (!string.Equals(ComputeScopeFingerprint(scope), _scopeFingerprint, StringComparison.Ordinal))
                throw new BusinessException("数据范围已变更", ErrorCodes.Forbidden);
        }

        public Task CompleteAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    // ==================== 服务装配与测试 ====================

    private static (
        ReportMigrationParityEvidenceService Service,
        RecordingReadScopeFactory Factory,
        ReportMigrationParityEvidenceProvider Store) BuildService(
        ErpDbContext db,
        ReportMigrationParitySnapshotDto snapshot,
        ReportConfigurationPreviewDto preview,
        Action? onPreview = null)
    {
        var providers = Providers(Dataset(), preview, onPreview);
        var factory = new RecordingReadScopeFactory(db, providers);
        var store = new ReportMigrationParityEvidenceProvider();
        var service = new ReportMigrationParityEvidenceService(
            new FakeLegacySource(snapshot),
            providers,
            new ReportMigrationParityComparator(),
            new ReportMigrationOutputSemanticsComparator(),
            new ReportConfigurationExecutionBudget(),
            store,
            artifactSource: null,
            readScopeFactory: factory);
        return (service, factory, store);
    }

    [Fact]
    public async Task 关系型工厂_不支持后端_显式失败()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "relational-unsupported", "product-sales-ranking");
        var factory = new ReportMigrationParityReadScopeFactory(db, Providers(Dataset(), Preview(1)));

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => factory.OpenAsync(user.Id, "corr-unsupported"));

        Assert.Equal(ReportConfigurationExecutionLimits.ErrorCodeEnvironmentUnsupported, ex.Code);
    }

    [Fact]
    public async Task 证据服务_三方一致作用域_打开一次并复核后记录()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "consistency-ok", "product-sales-ranking");
        var (service, factory, store) = BuildService(db, Snapshot(1), Preview(1));

        var evidence = await service.GetEvidenceAsync("report:product-sales-ranking", user.Id);

        Assert.NotNull(evidence);
        Assert.True(evidence!.Complete);
        Assert.Equal(1, factory.OpenCount);
        Assert.NotNull(factory.LastScope);
        Assert.Equal(1, factory.LastScope!.RecheckCount);
        Assert.NotNull(store.GetEvidence("report:product-sales-ranking"));
    }

    [Fact]
    public async Task 证据服务_菜单撤销_复核失败_无证据()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "consistency-menu-revoke", "product-sales-ranking");
        var (service, factory, store) = BuildService(db, Snapshot(1), Preview(1));

        var roleId = db.SysUserRoles.Single(r => r.UserId == user.Id).RoleId;
        var menuId = db.SysMenus.Single(m => m.MenuCode == "product-sales-ranking").Id;
        var link = db.SysRoleMenus.Single(rm => rm.RoleId == roleId && rm.MenuId == menuId);
        db.SysRoleMenus.Remove(link);
        await db.SaveChangesAsync();

        var evidence = await service.GetEvidenceAsync("report:product-sales-ranking", user.Id);

        Assert.Null(evidence);
        Assert.Equal(1, factory.OpenCount);
        Assert.Equal(1, factory.LastScope!.RecheckCount);
        Assert.Null(store.GetEvidence("report:product-sales-ranking"));
    }

    [Fact]
    public async Task 证据服务_数据范围撤销_复核失败_无证据()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "consistency-scope-revoke", "product-sales-ranking");

        var role = db.SysRoles.Single(r => db.SysUserRoles.Any(ur => ur.UserId == user.Id && ur.RoleId == r.Id));
        // 在通用预览读取期间撤销数据范围（特权 → 受限），模拟三方读取之间源权限变更。
        var (service, factory, store) = BuildService(db, Snapshot(1), Preview(1), onPreview: () =>
        {
            role.IsSystem = false;
            db.SaveChanges();
        });

        var evidence = await service.GetEvidenceAsync("report:product-sales-ranking", user.Id);

        Assert.Null(evidence);
        Assert.Equal(1, factory.OpenCount);
        Assert.Equal(1, factory.LastScope!.RecheckCount);
        Assert.Null(store.GetEvidence("report:product-sales-ranking"));
    }

    [Fact]
    public async Task 证据服务_通用预览全量超过本页_拒绝完整比对()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "consistency-truncated-preview", "product-sales-ranking");
        var (service, factory, store) = BuildService(db, Snapshot(1), Preview(1, total: 2));

        var evidence = await service.GetEvidenceAsync("report:product-sales-ranking", user.Id);

        Assert.Null(evidence);
        Assert.Equal(1, factory.OpenCount);
        Assert.Equal(0, factory.LastScope!.RecheckCount);
        Assert.Null(store.GetEvidence("report:product-sales-ranking"));
    }

    [Fact]
    public async Task 证据服务_旧来源满页_拒绝完整比对()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "consistency-full-page", "product-sales-ranking");
        var (service, factory, store) = BuildService(db, Snapshot(200), Preview(200));

        var evidence = await service.GetEvidenceAsync("report:product-sales-ranking", user.Id);

        Assert.Null(evidence);
        Assert.Equal(1, factory.OpenCount);
        Assert.Null(store.GetEvidence("report:product-sales-ranking"));
    }

    [Fact]
    public async Task 证据服务_已取消令牌_抛出取消()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "consistency-cancel", "product-sales-ranking");
        var (service, _, _) = BuildService(db, Snapshot(1), Preview(1));

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.GetEvidenceAsync("report:product-sales-ranking", user.Id, cts.Token));
    }
}
