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
/// ERP-273 通用报表配置有界一致只读快照单元测试（内存数据库，不连接 SQL Server / 不执行真实事务）：
/// 覆盖 1000 / 1001 边界、选中页与匹配计数 / 覆盖元数据、读取后数据库变更不影响已物化快照、
/// 快照生命周期（完成 / 释放幂等）、旧适配器默认不支持、取消令牌、应收账款空数据集与能力目录暴露。
/// <para>真实 SQL Server / 浏览器 / 字体环境不可用时显式 environment-blocked；本测试只验证离线路径。</para>
/// </summary>
public class ReportConfigurationSnapshotTests
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

    private static SysRole SeedRole(ErpDbContext db, string suffix, bool isSystem = false)
    {
        var role = new SysRole
        {
            RoleName = $"Role-{suffix}",
            RoleCode = $"Role-{suffix}-{Guid.NewGuid():N}",
            IsSystem = isSystem,
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

    private static long SeedPrivilegedUser(ErpDbContext db, string userName, string menuCode)
    {
        var user = SeedUser(db, userName);
        var role = SeedRole(db, menuCode, isSystem: true);
        SeedUserRole(db, user.Id, role.Id);
        SeedRoleMenu(db, role.Id, SeedMenu(db, menuCode).Id);
        return user.Id;
    }

    private static BaseCustomer SeedCustomer(ErpDbContext db, string code, string name)
    {
        var customer = new BaseCustomer { CustomerCode = code, CustomerName = name, Status = 1, CreditStatus = "正常" };
        db.BaseCustomers.Add(customer);
        db.SaveChanges();
        return customer;
    }

    private static void SeedOrders(ErpDbContext db, long customerId, int count, decimal startAmount)
    {
        var orders = new List<SalesOrder>();
        for (var i = 0; i < count; i++)
        {
            orders.Add(new SalesOrder
            {
                OrderNo = $"SO-{i:D4}",
                CustomerId = customerId,
                OrderDate = new DateTime(2026, 9, 1).AddDays(i % 28),
                Status = DocumentStatus.Pending,
                Currency = Currency.CNY,
                TotalAmount = startAmount + i,
            });
        }

        db.SalesOrders.AddRange(orders);
        db.SaveChanges();
    }

    private static IReadOnlyList<IReportConfigurationDatasetProvider> BuildProviders(ErpDbContext db)
        => new IReportConfigurationDatasetProvider[]
        {
            new SalesOrderReportConfigurationDatasetProvider(new DynamicSalesOrderReportQuery(db), db),
            new ReceivableReportConfigurationDatasetProvider(new DynamicReceivableReportQuery(db), db),
        };

    private static IReportConfigurationExecutionService BuildExecution(ErpDbContext db)
        => new ReportConfigurationExecutionService(db, BuildProviders(db));

    private static IReportConfigurationService BuildService(ErpDbContext db)
        => new ReportConfigurationService(db, new ReportConfigurationCatalog(BuildProviders(db)));

    private static ReportConfigurationDefinition MatchedSalesOrderDefinition(string[]? fields = null)
        => new()
        {
            SchemaVersion = ReportConfigurationRules.CurrentSchemaVersion,
            DatasetKey = ReportConfigurationConstants.DatasetSalesOrder,
            Fields = fields is { Length: > 0 }
                ? fields.ToList()
                : new List<string> { "orderNo", "totalAmount", "currency" },
            Filters = new List<ReportConfigurationFilter>(),
            Grouping = new List<string> { ReportConfigurationConstants.GroupNone },
            Aggregates = new List<ReportConfigurationAggregate>(),
            Capabilities = new List<string>(),
            Coverage = ReportConfigurationConstants.CoverageMatchedSet,
        };

    [Fact]
    public async Task 快照_1000条事实_返回匹配集覆盖与计数()
    {
        using var db = TestDbFactory.Create();
        var user = SeedPrivilegedUser(db, "snap-1000", "sales-order");
        var customer = SeedCustomer(db, "C1", "客户");
        SeedOrders(db, customer.Id, 1000, 100m);

        var service = BuildService(db);
        var created = await service.CreateAsync(user, new ReportConfigurationSaveDto
        {
            Name = "千条匹配集",
            Definition = MatchedSalesOrderDefinition(),
        });

        var execution = BuildExecution(db);
        var preview = await execution.PreviewAsync(user, new ReportConfigurationPreviewRequest
        {
            ConfigurationId = created.Id,
            PageSize = 50,
        });

        Assert.Equal(1000, preview.Total);
        Assert.Equal(1000, preview.MatchedCount);
        Assert.Equal(50, preview.Rows.Count);
        Assert.NotNull(preview.Evidence);
        Assert.Equal(ReportConfigurationConstants.CoverageMatchedSet, preview.Evidence.Coverage);
    }

    [Fact]
    public async Task 快照_1001条事实_超过上限failClosed()
    {
        using var db = TestDbFactory.Create();
        var user = SeedPrivilegedUser(db, "snap-1001", "sales-order");
        var customer = SeedCustomer(db, "C1", "客户");
        SeedOrders(db, customer.Id, 1001, 100m);

        var service = BuildService(db);
        var created = await service.CreateAsync(user, new ReportConfigurationSaveDto
        {
            Name = "千一条匹配集",
            Definition = MatchedSalesOrderDefinition(),
        });

        var execution = BuildExecution(db);
        var ex = await Assert.ThrowsAsync<BusinessException>(() => execution.PreviewAsync(
            user, new ReportConfigurationPreviewRequest { ConfigurationId = created.Id }));

        Assert.Equal(ReportConfigurationExecutionLimits.ErrorCodeResultTooLarge, ex.Code);
        Assert.Contains("1000", ex.Message);
    }

    [Fact]
    public async Task 快照_分页_选中页来自匹配集且携带覆盖元数据()
    {
        using var db = TestDbFactory.Create();
        var user = SeedPrivilegedUser(db, "snap-page", "sales-order");
        var customer = SeedCustomer(db, "C1", "客户");
        SeedOrders(db, customer.Id, 5, 100m);

        var service = BuildService(db);
        var created = await service.CreateAsync(user, new ReportConfigurationSaveDto
        {
            Name = "分页匹配集",
            Definition = MatchedSalesOrderDefinition(new[] { "orderNo" }),
        });

        var execution = BuildExecution(db);
        var preview = await execution.PreviewAsync(user, new ReportConfigurationPreviewRequest
        {
            ConfigurationId = created.Id,
            Page = 2,
            PageSize = 2,
        });

        Assert.Equal(5, preview.Total);
        Assert.Equal(5, preview.MatchedCount);
        Assert.Equal(3, preview.TotalPages);
        Assert.Equal(2, preview.Rows.Count);
        Assert.Equal(ReportConfigurationConstants.CoverageMatchedSet, preview.Evidence!.Coverage);
    }

    [Fact]
    public async Task 快照_读取后数据库变更_不改变已物化快照()
    {
        using var db = TestDbFactory.Create();
        var user = SeedPrivilegedUser(db, "snap-mutate", "sales-order");
        var customer = SeedCustomer(db, "C1", "客户");
        SeedOrders(db, customer.Id, 3, 100m);

        var provider = new SalesOrderReportConfigurationDatasetProvider(new DynamicSalesOrderReportQuery(db), db);
        var definition = MatchedSalesOrderDefinition(new[] { "orderNo" });
        var parameters = new ReportConfigurationPreviewParameters(1, 20, "none", null, null);

        await using (var snapshot = await provider.OpenReadSnapshotAsync(
            definition, parameters, user, Guid.NewGuid().ToString("N")))
        {
            var before = snapshot.MatchedCount;
            Assert.Equal(3, before);

            // 快照读取后新增订单：已物化的有界匹配集不受影响（请求作用域，绝不跨请求缓存 / 重读）
            SeedOrders(db, customer.Id, 2, 500m);
            Assert.Equal(before, snapshot.MatchedCount);
            Assert.Equal(before, snapshot.Rows.Count);
        }
    }

    [Fact]
    public async Task 快照_生命周期_完成与释放幂等()
    {
        var snapshot = new ReportConfigurationReadSnapshot(
            "corr", "sales-order", ReportConfigurationConstants.CoverageMatchedSet,
            0, Array.Empty<long>(), new List<Dictionary<string, object?>>(),
            new List<ReportConfigurationColumnDto>(),
            new ReportConfigurationEvidenceContextDto(
                "sales-order", "grain", "semantics", "readonly", "boundary", "disclaimer",
                ReportConfigurationConstants.CoverageMatchedSet),
            "privileged", 0, isConsistent: false, transaction: null);

        await snapshot.CompleteAsync();
        await snapshot.CompleteAsync();
        await snapshot.DisposeAsync();
        await snapshot.DisposeAsync();
    }

    [Fact]
    public async Task 快照_旧适配器默认不支持_受控环境错误()
    {
        IReportConfigurationDatasetProvider provider = new LegacyProvider();
        Assert.False(provider.SupportsReadSnapshot);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            provider.OpenReadSnapshotAsync(null!, null!, 1, "corr"));

        Assert.Equal(ReportConfigurationExecutionLimits.ErrorCodeEnvironmentUnsupported, ex.Code);
        Assert.Contains("environment-blocked", ex.Message);
    }

    [Fact]
    public async Task 快照_取消令牌_探针阶段抛取消()
    {
        using var db = TestDbFactory.Create();
        var user = SeedPrivilegedUser(db, "snap-cancel", "sales-order");
        var customer = SeedCustomer(db, "C1", "客户");
        SeedOrders(db, customer.Id, 5, 100m);

        var provider = new SalesOrderReportConfigurationDatasetProvider(new DynamicSalesOrderReportQuery(db), db);
        var definition = MatchedSalesOrderDefinition(new[] { "orderNo" });
        var parameters = new ReportConfigurationPreviewParameters(1, 20, "none", null, null);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            provider.OpenReadSnapshotAsync(definition, parameters, user, Guid.NewGuid().ToString("N"), cts.Token));
    }

    [Fact]
    public async Task 快照_应收账款空数据集_成功返回匹配集覆盖()
    {
        using var db = TestDbFactory.Create();
        var user = SeedPrivilegedUser(db, "snap-recv", DynamicReceivableReportRules.RequiredMenuCode);

        var definition = new ReportConfigurationDefinition
        {
            SchemaVersion = ReportConfigurationRules.CurrentSchemaVersion,
            DatasetKey = ReportConfigurationConstants.DatasetReceivable,
            Fields = new List<string> { "customerName", "grossAmount", "currency" },
            Filters = new List<ReportConfigurationFilter>(),
            Grouping = new List<string> { ReportConfigurationConstants.GroupNone },
            Aggregates = new List<ReportConfigurationAggregate>(),
            Capabilities = new List<string>(),
            Coverage = ReportConfigurationConstants.CoverageMatchedSet,
        };

        var service = BuildService(db);
        var created = await service.CreateAsync(user, new ReportConfigurationSaveDto
        {
            Name = "应收匹配集",
            Definition = definition,
        });

        var execution = BuildExecution(db);
        var preview = await execution.PreviewAsync(user, new ReportConfigurationPreviewRequest
        {
            ConfigurationId = created.Id,
        });

        Assert.Equal(0, preview.Total);
        Assert.Equal(0, preview.MatchedCount);
        Assert.Equal(ReportConfigurationConstants.CoverageMatchedSet, preview.Evidence!.Coverage);
    }

    [Fact]
    public async Task 快照_目录_暴露匹配集能力()
    {
        using var db = TestDbFactory.Create();
        var user = SeedPrivilegedUser(db, "snap-cap", "sales-order");

        var catalog = new ReportConfigurationCatalog(BuildProviders(db));
        var dto = await catalog.GetCatalogAsync(user);

        var dataset = Assert.Single(dto.Datasets, d => d.DatasetKey == ReportConfigurationConstants.DatasetSalesOrder);
        Assert.Contains(ReportConfigurationConstants.CapabilityMatchedSet, dataset.SupportedCapabilities);
    }

    /// <summary>不实现快照能力的旧数据集适配器（测试替身）：沿用默认不支持实现，保持编译。</summary>
    private sealed class LegacyProvider : IReportConfigurationDatasetProvider
    {
        public string DatasetKey => "legacy";

        public Task<ReportConfigurationDatasetDto?> GetDatasetAsync(long? userId, CancellationToken cancellationToken = default)
            => Task.FromResult<ReportConfigurationDatasetDto?>(null);

        public Task<ReportConfigurationPreviewDto> PreviewAsync(
            ReportConfigurationDefinition definition,
            ReportConfigurationPreviewParameters parameters,
            long? userId,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ReportConfigurationPreviewDto());
    }
}
