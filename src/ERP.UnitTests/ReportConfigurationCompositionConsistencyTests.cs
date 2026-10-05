using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using ERP.Infrastructure.Reports;
using Microsoft.EntityFrameworkCore;
using System.Globalization;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-335 Stage 2 表头 / 明细组合一致读取作用域单元测试（内存数据库，不连接 SQL Server / 不执行 SQL）：
/// 用内存版作用域工厂验证 <see cref="ReportConfigurationBundleService.ComposePreviewAsync"/> 在一致读取作用域内的
/// 两次读取 + 最终授权复核编排，覆盖匹配 / 分歧、空明细证据、原始 null 与零不混写、原币 / 原单位分区隔离，
/// 以及菜单 / 数据范围 / 权限撤销的 fail closed 与作用域生命周期幂等。
/// </summary>
public class ReportConfigurationCompositionConsistencyTests
{
    // ==================== 脚手架 ====================

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

    private static void SeedDocument(ErpDbContext db, long id, string docNo, decimal amount, string currency,
        string docType = "商业发票")
        => db.TradeDocuments.Add(new TradeDocument
        {
            Id = id,
            DocNo = docNo,
            DocType = docType,
            CustomerName = "客户甲",
            Amount = amount,
            Currency = currency,
            IssueDate = DateTime.Today,
            Status = "待制作",
            Copies = 1,
        });

    private static void SeedItem(ErpDbContext db, long id, long docId, int lineNo,
        decimal quantity, string unit, decimal lineAmount = 0m, string currency = "USD")
        => db.TradeDocumentItems.Add(new TradeDocumentItem
        {
            Id = id,
            TradeDocumentId = docId,
            LineNo = lineNo,
            ProductCode = "P" + lineNo,
            ProductNameCn = "P" + lineNo,
            Quantity = quantity,
            Unit = unit,
            UnitPrice = 0m,
            LineAmount = lineAmount,
            Currency = currency,
        });

    private static ReportConfigurationDefinition Definition(string datasetKey, params string[] fields)
        => new()
        {
            SchemaVersion = ReportConfigurationRules.CurrentSchemaVersion,
            DatasetKey = datasetKey,
            Fields = fields.ToList(),
            Grouping = new List<string> { ReportConfigurationConstants.GroupNone },
        };

    private static IReportConfigurationDatasetProvider[] BuildProviders(ErpDbContext db)
        => new IReportConfigurationDatasetProvider[]
        {
            new TradeDocumentReportConfigurationDatasetProvider(db),
        };

    private static (
        IReportConfigurationExecutionService Execution,
        IReportConfigurationService Configs,
        ReportConfigurationBundleService Bundle,
        RecordingCompositionReadScopeFactory ScopeFactory) BuildScopedServices(ErpDbContext db)
    {
        var providers = BuildProviders(db);
        var catalog = new ReportConfigurationCatalog(providers);
        var execution = new ReportConfigurationExecutionService(db, providers);
        var configs = new ReportConfigurationService(db, catalog);
        var factory = new RecordingCompositionReadScopeFactory(db, providers);
        var bundle = new ReportConfigurationBundleService(execution, compositionReadScopeFactory: factory);
        return (execution, configs, bundle, factory);
    }

    private static async Task<(long HeaderId, long DetailId)> CreateCompositionPairAsync(
        IReportConfigurationService configs, long userId)
    {
        var header = await configs.CreateAsync(userId, new ReportConfigurationSaveDto
        {
            Name = "组合表头",
            Definition = Definition(ReportConfigurationConstants.DatasetTradeDocument, "id", "docNo", "docType", "amount", "currency"),
        });
        var detail = await configs.CreateAsync(userId, new ReportConfigurationSaveDto
        {
            Name = "组合明细",
            Definition = Definition(ReportConfigurationConstants.DatasetTradeDocument, "id", "lineNo", "productCode", "quantity", "unit", "lineAmount", "lineCurrency"),
        });
        return (header.Id, detail.Id);
    }

    private static ReportConfigurationBundleCompositionRequest Request(long headerId, long detailId)
        => new()
        {
            CompositionKey = ReportConfigurationBundleCompositionManifest.TradeDocumentHeaderDetail,
            HeaderConfigurationId = headerId,
            DetailConfigurationId = detailId,
        };

    private static string ComputeScopeFingerprint(SalespersonDataScope scope)
        => scope.IsPrivileged
            ? "privileged"
            : (scope.SalesmanId?.ToString(CultureInfo.InvariantCulture) ?? "none")
              + "|" + string.Join(",", (scope.AllowedCustomerIds ?? new HashSet<long>()).OrderBy(x => x));

    // ==================== 内存版作用域工厂 ====================

    private sealed class RecordingCompositionReadScopeFactory : IReportConfigurationCompositionReadScopeFactory
    {
        private readonly IErpDbContext _db;
        private readonly IReadOnlyList<IReportConfigurationDatasetProvider> _providers;
        public int OpenCount { get; private set; }

        public RecordingCompositionReadScopeFactory(
            IErpDbContext db, IReadOnlyList<IReportConfigurationDatasetProvider> providers)
        {
            _db = db;
            _providers = providers;
        }

        public async Task<IReportConfigurationCompositionReadScope> OpenAsync(
            long ownerUserId, string correlationId, CancellationToken cancellationToken = default)
        {
            OpenCount++;
            var fingerprint = ComputeScopeFingerprint(
                await SalespersonDataScopeService.ResolveAsync(_db, ownerUserId));
            return new MemoryCompositionReadScope(correlationId, _db, _providers, fingerprint);
        }
    }

    private sealed class MemoryCompositionReadScope : IReportConfigurationCompositionReadScope
    {
        private readonly IErpDbContext _db;
        private readonly IReadOnlyList<IReportConfigurationDatasetProvider> _providers;
        private readonly string _scopeFingerprint;
        private int _completed;
        private int _disposed;

        public MemoryCompositionReadScope(
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

        public async Task RecheckAsync(
            long ownerUserId,
            IReadOnlyList<ReportConfigurationCompositionReadTarget> targets,
            CancellationToken cancellationToken = default)
        {
            foreach (var target in targets)
            {
                var provider = _providers.FirstOrDefault(p =>
                    string.Equals(p.DatasetKey, target.DatasetKey, StringComparison.OrdinalIgnoreCase))
                    ?? throw new BusinessException($"未知数据集: {target.DatasetKey}", ErrorCodes.InvalidParameter);

                if (await provider.GetDatasetAsync(ownerUserId, cancellationToken) is null)
                    throw new BusinessException(
                        $"当前账号没有「{target.DatasetKey}」数据集授权：拒绝组合（fail closed）",
                        ErrorCodes.Forbidden);
            }

            var scope = await SalespersonDataScopeService.ResolveAsync(_db, ownerUserId);
            if (!string.Equals(ComputeScopeFingerprint(scope), _scopeFingerprint, StringComparison.Ordinal))
                throw new BusinessException("数据范围已变更，本次组合被拒绝（fail closed）", ErrorCodes.Forbidden);

            foreach (var target in targets)
            {
                var owned = await _db.ReportConfigurations.AnyAsync(
                    c => c.Id == target.ConfigurationId && !c.IsDeleted && c.OwnerUserId == ownerUserId,
                    cancellationToken);
                if (owned)
                    continue;

                var granted = await _db.ReportConfigurationGrants.AnyAsync(
                    g => g.ReportConfigurationId == target.ConfigurationId
                        && g.RecipientUserId == ownerUserId && !g.IsDeleted,
                    cancellationToken);
                if (!granted)
                    throw BusinessException.NotFound("报表配置不存在或无权访问");
            }
        }

        public Task CompleteAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _completed);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposed);
            return ValueTask.CompletedTask;
        }
    }

    // ==================== 测试 ====================

    [Fact]
    public async Task 组合_一致作用域_匹配表头明细_合计与原币单位正确()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "consistency-matched", "doc-center");
        SeedDocument(db, 1, "TD-CONS-1", 100m, "USD");
        SeedItem(db, 101, 1, 1, 2m, "箱", lineAmount: 30m, currency: "USD");
        SeedItem(db, 102, 1, 2, 3m, "箱", lineAmount: 20m, currency: "USD");
        await db.SaveChangesAsync();

        var (_, configs, bundle, factory) = BuildScopedServices(db);
        var (headerId, detailId) = await CreateCompositionPairAsync(configs, user.Id);

        var result = await bundle.ComposePreviewAsync(user.Id, Request(headerId, detailId));

        Assert.Equal(1, factory.OpenCount);
        Assert.Equal(1, result.ParentCount);
        Assert.Equal(2, result.DetailCount);
        var parent = Assert.Single(result.Parents);
        Assert.Equal(100m, parent.Totals.HeaderAmount);
        Assert.Equal("USD", parent.Totals.HeaderCurrency);
        var amount = Assert.Single(parent.Totals.DetailAmounts);
        Assert.Equal("USD", amount.Currency);
        Assert.Equal(50m, amount.Amount);
        var quantity = Assert.Single(parent.Totals.DetailQuantities);
        Assert.Equal("箱", quantity.Unit);
        Assert.Equal(5m, quantity.Quantity);
    }

    [Fact]
    public async Task 组合_一致作用域_空明细保留显式证据()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "consistency-empty", "doc-center");
        SeedDocument(db, 1, "TD-EMPTY", 0m, "USD");
        await db.SaveChangesAsync();

        var (_, configs, bundle, _) = BuildScopedServices(db);
        var (headerId, detailId) = await CreateCompositionPairAsync(configs, user.Id);

        var result = await bundle.ComposePreviewAsync(user.Id, Request(headerId, detailId));

        var parent = Assert.Single(result.Parents);
        Assert.False(parent.HasDetails);
        Assert.NotNull(parent.EmptyDetailsEvidence);
        Assert.Empty(parent.Details);
        Assert.Equal(0m, parent.Totals.HeaderAmount);
    }

    [Fact]
    public async Task 组合_一致作用域_原始null与零_不混写()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "consistency-null-zero", "doc-center");
        // 装箱单：表头金额登记为 0（零），明细行不含价格口径（原始 null，绝不回落为 0）。
        SeedDocument(db, 1, "TD-PL", 0m, "USD", docType: "装箱单");
        SeedItem(db, 101, 1, 1, 2m, "箱", lineAmount: 0m, currency: "USD");
        await db.SaveChangesAsync();

        var (_, configs, bundle, _) = BuildScopedServices(db);
        var (headerId, detailId) = await CreateCompositionPairAsync(configs, user.Id);

        var result = await bundle.ComposePreviewAsync(user.Id, Request(headerId, detailId));

        var parent = Assert.Single(result.Parents);
        Assert.Equal(0m, parent.Totals.HeaderAmount);
        // 装箱单行不含价格口径：lineAmount 为 null，明细金额分区应为空（不把 null 当成 0）。
        Assert.Empty(parent.Totals.DetailAmounts);
        var quantity = Assert.Single(parent.Totals.DetailQuantities);
        Assert.Equal("箱", quantity.Unit);
        Assert.Equal(2m, quantity.Quantity);
    }

    [Fact]
    public async Task 组合_一致作用域_不同币种单位_分区隔离()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "consistency-partition", "doc-center");
        SeedDocument(db, 1, "TD-P1", 100m, "USD");
        SeedDocument(db, 2, "TD-P2", 200m, "EUR");
        SeedItem(db, 101, 1, 1, 1m, "箱", lineAmount: 20m, currency: "USD");
        SeedItem(db, 102, 2, 1, 1m, "个", lineAmount: 40m, currency: "EUR");
        await db.SaveChangesAsync();

        var (_, configs, bundle, _) = BuildScopedServices(db);
        var (headerId, detailId) = await CreateCompositionPairAsync(configs, user.Id);

        var result = await bundle.ComposePreviewAsync(user.Id, Request(headerId, detailId));

        Assert.Equal(2, result.ParentCount);
        var usd = Assert.Single(result.Parents, p => (string)p.Header["docNo"]! == "TD-P1");
        Assert.Equal("USD", Assert.Single(usd.Totals.DetailAmounts).Currency);
        Assert.Equal(20m, Assert.Single(usd.Totals.DetailAmounts).Amount);
        Assert.Equal("箱", Assert.Single(usd.Totals.DetailQuantities).Unit);

        var eur = Assert.Single(result.Parents, p => (string)p.Header["docNo"]! == "TD-P2");
        Assert.Equal("EUR", Assert.Single(eur.Totals.DetailAmounts).Currency);
        Assert.Equal(40m, Assert.Single(eur.Totals.DetailAmounts).Amount);
        Assert.Equal("个", Assert.Single(eur.Totals.DetailQuantities).Unit);
    }

    [Fact]
    public async Task 组合_一致作用域_孤儿明细_拒绝()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "consistency-orphan", "doc-center");
        SeedDocument(db, 1, "TD-ORPHAN", 100m, "USD");
        SeedDocument(db, 2, "TD-ORPHAN-2", 20m, "USD");
        SeedItem(db, 201, 2, 1, 1m, "箱", lineAmount: 20m);
        await db.SaveChangesAsync();

        var (_, configs, bundle, _) = BuildScopedServices(db);

        // 表头节只选单证 1，明细节不筛选 → 单证 2 的明细成为孤儿行（分歧结果）。
        var header = await configs.CreateAsync(user.Id, new ReportConfigurationSaveDto
        {
            Name = "组合表头-仅单证1",
            Definition = new ReportConfigurationDefinition
            {
                SchemaVersion = ReportConfigurationRules.CurrentSchemaVersion,
                DatasetKey = ReportConfigurationConstants.DatasetTradeDocument,
                Fields = new List<string> { "id", "docNo", "amount", "currency" },
                Filters = new List<ReportConfigurationFilter>
                {
                    new() { FieldKey = "id", Operator = ReportConfigurationConstants.OperatorEq, Value = 1 },
                },
                Grouping = new List<string> { ReportConfigurationConstants.GroupNone },
            },
        });
        var detail = await configs.CreateAsync(user.Id, new ReportConfigurationSaveDto
        {
            Name = "组合明细-全量",
            Definition = Definition(ReportConfigurationConstants.DatasetTradeDocument, "id", "lineNo", "productCode", "quantity", "unit", "lineAmount", "lineCurrency"),
        });

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => bundle.ComposePreviewAsync(user.Id, Request(header.Id, detail.Id)));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("孤儿行", ex.Message);
    }


    [Fact]
    public async Task 组合_一致作用域_菜单撤销_整体失败()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "consistency-menu", "doc-center");
        SeedDocument(db, 1, "TD-MENU", 100m, "USD");
        SeedItem(db, 101, 1, 1, 1m, "箱", lineAmount: 10m);
        await db.SaveChangesAsync();

        var (_, configs, bundle, _) = BuildScopedServices(db);
        var (headerId, detailId) = await CreateCompositionPairAsync(configs, user.Id);

        var roleId = db.SysUserRoles.Single(r => r.UserId == user.Id).RoleId;
        var menuId = db.SysMenus.Single(m => m.MenuCode == "doc-center").Id;
        var link = db.SysRoleMenus.Single(rm => rm.RoleId == roleId && rm.MenuId == menuId);
        db.SysRoleMenus.Remove(link);
        await db.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => bundle.ComposePreviewAsync(user.Id, Request(headerId, detailId)));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    [Fact]
    public async Task 组合_一致作用域_数据范围撤销_整体失败()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "consistency-scope", "doc-center");
        SeedDocument(db, 1, "TD-SCOPE", 100m, "USD");
        SeedItem(db, 101, 1, 1, 1m, "箱", lineAmount: 10m);
        await db.SaveChangesAsync();

        var (_, configs, bundle, _) = BuildScopedServices(db);
        var (headerId, detailId) = await CreateCompositionPairAsync(configs, user.Id);

        var role = db.SysRoles.Single(r => db.SysUserRoles.Any(ur => ur.UserId == user.Id && ur.RoleId == r.Id));
        role.IsSystem = false;
        await db.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => bundle.ComposePreviewAsync(user.Id, Request(headerId, detailId)));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    [Fact]
    public async Task 组合_一致作用域_权限撤销_配置已删除_拒绝()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "consistency-grant", "doc-center");
        SeedDocument(db, 1, "TD-GRANT", 100m, "USD");
        await db.SaveChangesAsync();

        var (_, configs, bundle, factory) = BuildScopedServices(db);
        var (headerId, detailId) = await CreateCompositionPairAsync(configs, user.Id);

        var config = await db.ReportConfigurations.FindAsync(headerId);
        config!.IsDeleted = true;
        await db.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => bundle.ComposePreviewAsync(user.Id, Request(headerId, detailId)));
        Assert.Equal(ErrorCodes.NotFound, ex.Code);
        Assert.Equal(1, factory.OpenCount);
    }

    [Fact]
    public async Task 组合_一致作用域_完成与释放_幂等()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "consistency-lifecycle", "doc-center");

        var (_, _, _, factory) = BuildScopedServices(db);
        await using var scope = await factory.OpenAsync(user.Id, "corr");

        await scope.CompleteAsync();
        await scope.CompleteAsync();
        await scope.DisposeAsync();
        await scope.DisposeAsync();

        Assert.True(scope.IsConsistent);
        Assert.Equal("corr", scope.CorrelationId);
    }

    [Fact]
    public async Task 组合_关系型工厂_不支持后端_显式失败()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "consistency-unsupported", "doc-center");
        var providers = BuildProviders(db);

        // 关系型工厂在内存库（不支持 Snapshot 事务）上必须显式 environment-blocked，绝不静默回退。
        var factory = new ReportConfigurationCompositionReadScopeFactory(db, providers);
        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => factory.OpenAsync(user.Id, "corr-unsupported"));
        Assert.Equal(ReportConfigurationExecutionLimits.ErrorCodeEnvironmentUnsupported, ex.Code);
    }
}

