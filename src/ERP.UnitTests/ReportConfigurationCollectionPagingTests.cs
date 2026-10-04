using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using ERP.Infrastructure.Reports;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-279 Stage 1：通用报表配置「自有列表 / 修订历史 / 所有者授权 / 被共享」集合的有界 keyset 分页单元测试。
/// <para>离线内存库夹具，超过 200 条候选；覆盖稳定唯一排序、无重复无遗漏导航、共享扫描经被删除 / 被撤销候选推进、
/// 空页续读、有界扫描与取消、无效 limit / cursor 显式失败、遗留小列表兼容包装显式拒绝溢出，
/// 以及 API 端点使用分页契约。不连接 SQL Server、不执行浏览器 / UI 验收。</para>
/// </summary>
public class ReportConfigurationCollectionPagingTests
{
    // ==================== 0. 脚手架 ====================

    private static SysUser SeedUser(ErpDbContext db, string userName, UserStatus status = UserStatus.Enabled)
    {
        var user = new SysUser
        {
            UserName = userName,
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = userName,
            Status = status,
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

    private static IReportConfigurationService BuildService(ErpDbContext db)
        => new ReportConfigurationService(db, BuildCatalog(db));

    private static IReportConfigurationSharingService BuildSharing(ErpDbContext db)
        => new ReportConfigurationSharingService(db, BuildCatalog(db));

    private static IReadOnlyList<IReportConfigurationDatasetProvider> BuildProviders(ErpDbContext db)
        => new IReportConfigurationDatasetProvider[]
        {
            new SalesOrderReportConfigurationDatasetProvider(new DynamicSalesOrderReportQuery(db)),
            new ReceivableReportConfigurationDatasetProvider(new DynamicReceivableReportQuery(db)),
        };

    private static IReportConfigurationCatalog BuildCatalog(ErpDbContext db)
        => new ReportConfigurationCatalog(BuildProviders(db));

    private static ReportConfigurationsController BuildController(ErpDbContext db)
        => new(
            new ReportConfigurationCatalog(BuildProviders(db)),
            new ReportConfigurationService(db, new ReportConfigurationCatalog(BuildProviders(db))),
            new ReportConfigurationExecutionService(db, BuildProviders(db)),
            new ReportConfigurationSharingService(db, new ReportConfigurationCatalog(BuildProviders(db))));

    private static ReportConfigurationSaveDto SaveDto(string name, ReportConfigurationDefinition definition)
        => new() { Name = name, Definition = definition };

    private static ReportConfigurationDefinition Definition(params string[] fields) => new()
    {
        SchemaVersion = 1,
        DatasetKey = ReportConfigurationConstants.DatasetSalesOrder,
        Fields = fields.ToList(),
        Filters = new List<ReportConfigurationFilter>(),
        Grouping = new List<string> { ReportConfigurationConstants.GroupNone },
        Aggregates = new List<ReportConfigurationAggregate>(),
        Capabilities = new List<string> { ReportConfigurationConstants.CapabilityPreview },
        Presentation = new ReportConfigurationPresentation { Page = 1, PageSize = 20 },
    };

    private static string DefinitionJson(string datasetKey) => JsonSerializer.Serialize(
        new ReportConfigurationDefinition
        {
            SchemaVersion = 1,
            DatasetKey = datasetKey,
            Fields = new List<string> { "orderNo" },
            Filters = new List<ReportConfigurationFilter>(),
            Grouping = new List<string> { ReportConfigurationConstants.GroupNone },
            Aggregates = new List<ReportConfigurationAggregate>(),
            Capabilities = new List<string> { ReportConfigurationConstants.CapabilityPreview },
            Presentation = new ReportConfigurationPresentation { Page = 1, PageSize = 20 },
        },
        new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

    private static void SeedConfig(ErpDbContext db, long ownerId, int index)
    {
        db.ReportConfigurations.Add(new ReportConfiguration
        {
            OwnerUserId = ownerId,
            Name = $"配置-{index}",
            DatasetKey = ReportConfigurationConstants.DatasetSalesOrder,
            DefinitionJson = "{}",
            SchemaVersion = 1,
            Status = ReportConfigurationStatus.Draft,
            Version = 1,
            CurrentPublishedVersion = 0,
        });
    }

    private static void SeedRevision(ErpDbContext db, long configId, long ownerId, int version)
    {
        db.ReportConfigurationRevisions.Add(new ReportConfigurationRevision
        {
            ReportConfigurationId = configId,
            OwnerUserId = ownerId,
            Version = version,
            Name = "报表",
            DatasetKey = ReportConfigurationConstants.DatasetSalesOrder,
            DefinitionJson = DefinitionJson(ReportConfigurationConstants.DatasetSalesOrder),
            SchemaVersion = 1,
            PublishedAt = DateTime.Now,
            PublishedBy = ownerId,
        });
    }

    private static void SeedGrant(ErpDbContext db, long configId, long recipientId, long grantedBy, bool deleted = false)
    {
        db.ReportConfigurationGrants.Add(new ReportConfigurationGrant
        {
            RecipientUserId = recipientId,
            ReportConfigurationId = configId,
            RevisionVersion = 1,
            GrantedByUserId = grantedBy,
            Version = 1,
            IsDeleted = deleted,
        });
    }

    private static async Task<List<T>> DrainPageAsync<T>(Func<string?, Task<ReportConfigurationPage<T>>> fetch)
    {
        var all = new List<T>();
        string? cursor = null;
        var guard = 0;
        do
        {
            var page = await fetch(cursor);
            all.AddRange(page.Items);
            Assert.True(guard++ < 1000, "分页续读出现死循环");
            cursor = page.HasMore ? page.NextCursor : null;
        } while (cursor is not null);

        return all;
    }

    // ==================== 1. 自有列表分页 ====================

    [Fact]
    public async Task ListPageAsync_超过200条_默认25最大100_稳定无重复无遗漏()
    {
        using var db = TestDbFactory.Create();
        var ownerId = SeedAuthorizedUser(db, "owner", "sales-order");
        for (var i = 1; i <= 230; i++)
            SeedConfig(db, ownerId, i);
        db.SaveChanges();

        var service = BuildService(db);

        var first = await service.ListPageAsync(ownerId);
        Assert.Equal(ReportConfigurationPaging.DefaultPageSize, first.Items.Count);
        Assert.True(first.HasMore);
        Assert.NotNull(first.NextCursor);

        var all = await DrainPageAsync<ReportConfigurationSummaryDto>(
            cursor => service.ListPageAsync(ownerId, 25, cursor));

        Assert.Equal(230, all.Count);
        Assert.Equal(230, all.Select(x => x.Id).Distinct().Count());                 // 无重复
        Assert.Equal(all.OrderByDescending(x => x.Id).Select(x => x.Id), all.Select(x => x.Id)); // 稳定唯一排序（Id 降序，最新在前）

        var maxPage = await service.ListPageAsync(ownerId, 100);
        Assert.True(maxPage.Items.Count <= ReportConfigurationPaging.MaxPageSize);
    }

    [Fact]
    public async Task ListPageAsync_仅返回当前用户_跨所有者为空()
    {
        using var db = TestDbFactory.Create();
        var ownerA = SeedAuthorizedUser(db, "owner-a", "sales-order");
        var ownerB = SeedAuthorizedUser(db, "owner-b", "sales-order");
        for (var i = 1; i <= 5; i++)
            SeedConfig(db, ownerA, i);
        db.SaveChanges();

        var service = BuildService(db);

        var forA = await service.ListPageAsync(ownerA);
        Assert.Equal(5, forA.Items.Count);

        var forB = await service.ListPageAsync(ownerB);
        Assert.Empty(forB.Items);
        Assert.False(forB.HasMore);
        Assert.Null(forB.NextCursor);
    }

    // ==================== 2. 修订历史分页 ====================

    [Fact]
    public async Task ListRevisionsPageAsync_按版本号稳定分页_无重复无遗漏()
    {
        using var db = TestDbFactory.Create();
        var ownerId = SeedAuthorizedUser(db, "owner", "sales-order");
        var config = new ReportConfiguration
        {
            OwnerUserId = ownerId,
            Name = "报表",
            DatasetKey = ReportConfigurationConstants.DatasetSalesOrder,
            DefinitionJson = DefinitionJson(ReportConfigurationConstants.DatasetSalesOrder),
            SchemaVersion = 1,
            Status = ReportConfigurationStatus.Published,
            Version = 121,
            CurrentPublishedVersion = 120,
        };
        db.ReportConfigurations.Add(config);
        db.SaveChanges();

        for (var v = 1; v <= 120; v++)
            SeedRevision(db, config.Id, ownerId, v);
        db.SaveChanges();

        var service = BuildService(db);
        var all = await DrainPageAsync<ReportConfigurationRevisionDto>(
            cursor => service.ListRevisionsPageAsync(ownerId, config.Id, 25, cursor));

        Assert.Equal(120, all.Count);
        Assert.Equal(Enumerable.Range(1, 120), all.Select(r => r.Version));
        Assert.Equal(120, all.Select(r => r.Id).Distinct().Count());
    }

    // ==================== 3. 所有者授权分页 ====================

    [Fact]
    public async Task ListGrantsPageAsync_按Id稳定分页_无重复无遗漏()
    {
        using var db = TestDbFactory.Create();
        var ownerId = SeedAuthorizedUser(db, "owner", "sales-order");
        var recipientId = SeedAuthorizedUser(db, "recipient", "sales-order");
        var config = new ReportConfiguration
        {
            OwnerUserId = ownerId,
            Name = "报表",
            DatasetKey = ReportConfigurationConstants.DatasetSalesOrder,
            DefinitionJson = "{}",
            SchemaVersion = 1,
            Status = ReportConfigurationStatus.Draft,
            Version = 1,
            CurrentPublishedVersion = 0,
        };
        db.ReportConfigurations.Add(config);
        db.SaveChanges();

        for (var i = 1; i <= 130; i++)
            SeedGrant(db, config.Id, recipientId, ownerId);
        db.SaveChanges();

        var sharing = BuildSharing(db);
        var all = await DrainPageAsync<ReportConfigurationGrantDto>(
            cursor => sharing.ListGrantsPageAsync(ownerId, config.Id, 25, cursor));

        Assert.Equal(130, all.Count);
        Assert.Equal(130, all.Select(g => g.Id).Distinct().Count());
        Assert.Equal(all.OrderBy(g => g.Id).Select(g => g.Id), all.Select(g => g.Id));
    }

    // ==================== 4. 被共享列表分页（有界扫描 + 续读推进） ====================

    [Fact]
    public async Task ListSharedPageAsync_超过200候选_推进经过被删除配置候选_无重复无遗漏()
    {
        using var db = TestDbFactory.Create();
        var ownerId = SeedAuthorizedUser(db, "owner", "sales-order");
        var recipientId = SeedAuthorizedUser(db, "recipient", "sales-order");

        var hiddenConfig = new ReportConfiguration
        {
            OwnerUserId = ownerId,
            Name = "已删除配置",
            DatasetKey = ReportConfigurationConstants.DatasetSalesOrder,
            DefinitionJson = DefinitionJson(ReportConfigurationConstants.DatasetSalesOrder),
            SchemaVersion = 1,
            Status = ReportConfigurationStatus.Published,
            Version = 2,
            CurrentPublishedVersion = 1,
        };
        var visibleConfig = new ReportConfiguration
        {
            OwnerUserId = ownerId,
            Name = "可见配置",
            DatasetKey = ReportConfigurationConstants.DatasetSalesOrder,
            DefinitionJson = DefinitionJson(ReportConfigurationConstants.DatasetSalesOrder),
            SchemaVersion = 1,
            Status = ReportConfigurationStatus.Published,
            Version = 2,
            CurrentPublishedVersion = 1,
        };
        db.ReportConfigurations.AddRange(hiddenConfig, visibleConfig);
        db.SaveChanges();
        SeedRevision(db, hiddenConfig.Id, ownerId, 1);
        SeedRevision(db, visibleConfig.Id, ownerId, 1);

        // 前 40 个候选指向已软删除的配置（解析阶段隐藏），其余 220 个可见
        for (var i = 1; i <= 40; i++)
            SeedGrant(db, hiddenConfig.Id, recipientId, ownerId);
        for (var i = 1; i <= 220; i++)
            SeedGrant(db, visibleConfig.Id, recipientId, ownerId);
        hiddenConfig.IsDeleted = true;
        db.SaveChanges();

        var sharing = BuildSharing(db);
        var all = await DrainPageAsync<ReportConfigurationSharedSummaryDto>(
            cursor => sharing.ListSharedPageAsync(recipientId, 25, cursor));

        Assert.Equal(220, all.Count);
        Assert.All(all, s => Assert.Equal(visibleConfig.Id, s.ReportConfigurationId));
        Assert.DoesNotContain(all, s => s.ReportConfigurationId == hiddenConfig.Id);
    }

    [Fact]
    public async Task ListSharedPageAsync_空页续读_前200候选全部被删除配置隐藏_仍有后续可见()
    {
        using var db = TestDbFactory.Create();
        var ownerId = SeedAuthorizedUser(db, "owner", "sales-order");
        var recipientId = SeedAuthorizedUser(db, "recipient", "sales-order");

        var hiddenConfig = new ReportConfiguration
        {
            OwnerUserId = ownerId,
            Name = "已删除配置",
            DatasetKey = ReportConfigurationConstants.DatasetSalesOrder,
            DefinitionJson = DefinitionJson(ReportConfigurationConstants.DatasetSalesOrder),
            SchemaVersion = 1,
            Status = ReportConfigurationStatus.Published,
            Version = 2,
            CurrentPublishedVersion = 1,
        };
        var visibleConfig = new ReportConfiguration
        {
            OwnerUserId = ownerId,
            Name = "可见配置",
            DatasetKey = ReportConfigurationConstants.DatasetSalesOrder,
            DefinitionJson = DefinitionJson(ReportConfigurationConstants.DatasetSalesOrder),
            SchemaVersion = 1,
            Status = ReportConfigurationStatus.Published,
            Version = 2,
            CurrentPublishedVersion = 1,
        };
        db.ReportConfigurations.AddRange(hiddenConfig, visibleConfig);
        db.SaveChanges();
        SeedRevision(db, hiddenConfig.Id, ownerId, 1);
        SeedRevision(db, visibleConfig.Id, ownerId, 1);

        // 前 200 个候选全部指向已删除配置（首页空但 HasMore），最后 10 个可见
        for (var i = 1; i <= 200; i++)
            SeedGrant(db, hiddenConfig.Id, recipientId, ownerId);
        for (var i = 1; i <= 10; i++)
            SeedGrant(db, visibleConfig.Id, recipientId, ownerId);
        hiddenConfig.IsDeleted = true;
        db.SaveChanges();

        var sharing = BuildSharing(db);

        var first = await sharing.ListSharedPageAsync(recipientId, 25, null);
        Assert.Empty(first.Items);
        Assert.True(first.HasMore);
        Assert.NotNull(first.NextCursor);

        var all = await DrainPageAsync<ReportConfigurationSharedSummaryDto>(
            cursor => sharing.ListSharedPageAsync(recipientId, 25, cursor));
        Assert.Equal(10, all.Count);
        Assert.All(all, s => Assert.Equal(visibleConfig.Id, s.ReportConfigurationId));
    }

    [Fact]
    public async Task ListSharedPageAsync_数据集授权撤销_隐藏共享_绝不泄露()
    {
        using var db = TestDbFactory.Create();
        var ownerId = SeedAuthorizedUser(db, "owner", "sales-order");
        var recipientId = SeedAuthorizedUser(db, "recipient", "sales-order");

        var config = new ReportConfiguration
        {
            OwnerUserId = ownerId,
            Name = "报表",
            DatasetKey = ReportConfigurationConstants.DatasetSalesOrder,
            DefinitionJson = DefinitionJson(ReportConfigurationConstants.DatasetSalesOrder),
            SchemaVersion = 1,
            Status = ReportConfigurationStatus.Published,
            Version = 2,
            CurrentPublishedVersion = 1,
        };
        db.ReportConfigurations.Add(config);
        db.SaveChanges();
        SeedRevision(db, config.Id, ownerId, 1);
        SeedGrant(db, config.Id, recipientId, ownerId);
        db.SaveChanges();

        foreach (var menu in db.SysMenus.Where(m => m.MenuCode == "sales-order").ToList())
            menu.IsDeleted = true;
        db.SaveChanges();

        var sharing = BuildSharing(db);
        var page = await sharing.ListSharedPageAsync(recipientId, 25, null);
        Assert.Empty(page.Items);
        Assert.False(page.HasMore);
    }

    [Fact]
    public async Task ListSharedPageAsync_被授权人停用_空页()
    {
        using var db = TestDbFactory.Create();
        var ownerId = SeedAuthorizedUser(db, "owner", "sales-order");
        var recipientId = SeedAuthorizedUser(db, "recipient", "sales-order");

        var config = new ReportConfiguration
        {
            OwnerUserId = ownerId,
            Name = "报表",
            DatasetKey = ReportConfigurationConstants.DatasetSalesOrder,
            DefinitionJson = DefinitionJson(ReportConfigurationConstants.DatasetSalesOrder),
            SchemaVersion = 1,
            Status = ReportConfigurationStatus.Published,
            Version = 2,
            CurrentPublishedVersion = 1,
        };
        db.ReportConfigurations.Add(config);
        db.SaveChanges();
        SeedRevision(db, config.Id, ownerId, 1);
        SeedGrant(db, config.Id, recipientId, ownerId);
        db.SysUsers.Single(u => u.Id == recipientId).Status = UserStatus.Disabled;
        db.SaveChanges();

        var sharing = BuildSharing(db);
        var page = await sharing.ListSharedPageAsync(recipientId, 25, null);
        Assert.Empty(page.Items);
        Assert.False(page.HasMore);
        Assert.Null(page.NextCursor);
    }

    // ==================== 5. 有界扫描与取消 ====================

    [Fact]
    public async Task ListSharedPageAsync_单次请求可见不超过100_且存在后续()
    {
        using var db = TestDbFactory.Create();
        var ownerId = SeedAuthorizedUser(db, "owner", "sales-order");
        var recipientId = SeedAuthorizedUser(db, "recipient", "sales-order");

        var config = new ReportConfiguration
        {
            OwnerUserId = ownerId,
            Name = "报表",
            DatasetKey = ReportConfigurationConstants.DatasetSalesOrder,
            DefinitionJson = DefinitionJson(ReportConfigurationConstants.DatasetSalesOrder),
            SchemaVersion = 1,
            Status = ReportConfigurationStatus.Published,
            Version = 2,
            CurrentPublishedVersion = 1,
        };
        db.ReportConfigurations.Add(config);
        db.SaveChanges();
        SeedRevision(db, config.Id, ownerId, 1);
        for (var i = 1; i <= 205; i++)
            SeedGrant(db, config.Id, recipientId, ownerId);
        db.SaveChanges();

        var sharing = BuildSharing(db);
        var page = await sharing.ListSharedPageAsync(recipientId, 100, null);

        Assert.True(page.Items.Count <= ReportConfigurationPaging.MaxPageSize);
        Assert.True(page.HasMore);
    }

    [Fact]
    public async Task ListSharedPageAsync_取消令牌_有界扫描取消()
    {
        using var db = TestDbFactory.Create();
        var ownerId = SeedAuthorizedUser(db, "owner", "sales-order");
        var recipientId = SeedAuthorizedUser(db, "recipient", "sales-order");

        var config = new ReportConfiguration
        {
            OwnerUserId = ownerId,
            Name = "报表",
            DatasetKey = ReportConfigurationConstants.DatasetSalesOrder,
            DefinitionJson = DefinitionJson(ReportConfigurationConstants.DatasetSalesOrder),
            SchemaVersion = 1,
            Status = ReportConfigurationStatus.Published,
            Version = 2,
            CurrentPublishedVersion = 1,
        };
        db.ReportConfigurations.Add(config);
        db.SaveChanges();
        SeedRevision(db, config.Id, ownerId, 1);
        SeedGrant(db, config.Id, recipientId, ownerId);
        db.SaveChanges();

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var sharing = BuildSharing(db);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => sharing.ListSharedPageAsync(recipientId, 25, null, cts.Token));
    }

    // ==================== 6. 无效 limit / cursor 显式失败 ====================

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(101)]
    public async Task 无效limit_显式失败(int limit)
    {
        using var db = TestDbFactory.Create();
        var ownerId = SeedAuthorizedUser(db, "owner", "sales-order");
        var service = BuildService(db);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => service.ListPageAsync(ownerId, limit));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public async Task 无效或负游标_显式失败()
    {
        using var db = TestDbFactory.Create();
        var ownerId = SeedAuthorizedUser(db, "owner", "sales-order");
        var service = BuildService(db);

        var invalid = await Assert.ThrowsAsync<BusinessException>(() => service.ListPageAsync(ownerId, null, "not-base64!"));
        Assert.Equal(ErrorCodes.InvalidParameter, invalid.Code);

        var negative = ReportConfigurationPaging.EncodeCursor(-5);
        var neg = await Assert.ThrowsAsync<BusinessException>(() => service.ListPageAsync(ownerId, null, negative));
        Assert.Equal(ErrorCodes.InvalidParameter, neg.Code);
    }

    // ==================== 7. 遗留小列表兼容包装：超过上限显式拒绝 ====================

    [Fact]
    public async Task ListAsync_遗留包装_超过上限_显式拒绝而非截断()
    {
        using var db = TestDbFactory.Create();
        var ownerId = SeedAuthorizedUser(db, "owner", "sales-order");
        for (var i = 1; i <= 101; i++)
            SeedConfig(db, ownerId, i);
        db.SaveChanges();

        var service = BuildService(db);
        var ex = await Assert.ThrowsAsync<BusinessException>(() => service.ListAsync(ownerId));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
    }

    [Fact]
    public async Task ListGrantsAsync_遗留包装_超过上限_显式拒绝()
    {
        using var db = TestDbFactory.Create();
        var ownerId = SeedAuthorizedUser(db, "owner", "sales-order");
        var recipientId = SeedAuthorizedUser(db, "recipient", "sales-order");
        var config = new ReportConfiguration
        {
            OwnerUserId = ownerId,
            Name = "报表",
            DatasetKey = ReportConfigurationConstants.DatasetSalesOrder,
            DefinitionJson = "{}",
            SchemaVersion = 1,
            Status = ReportConfigurationStatus.Draft,
            Version = 1,
            CurrentPublishedVersion = 0,
        };
        db.ReportConfigurations.Add(config);
        db.SaveChanges();
        for (var i = 1; i <= 101; i++)
            SeedGrant(db, config.Id, recipientId, ownerId);
        db.SaveChanges();

        var sharing = BuildSharing(db);
        var ex = await Assert.ThrowsAsync<BusinessException>(() => sharing.ListGrantsAsync(ownerId, config.Id));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
    }

    [Fact]
    public async Task ListSharedAsync_遗留包装_可见超过上限_显式拒绝()
    {
        using var db = TestDbFactory.Create();
        var ownerId = SeedAuthorizedUser(db, "owner", "sales-order");
        var recipientId = SeedAuthorizedUser(db, "recipient", "sales-order");
        var config = new ReportConfiguration
        {
            OwnerUserId = ownerId,
            Name = "报表",
            DatasetKey = ReportConfigurationConstants.DatasetSalesOrder,
            DefinitionJson = DefinitionJson(ReportConfigurationConstants.DatasetSalesOrder),
            SchemaVersion = 1,
            Status = ReportConfigurationStatus.Published,
            Version = 2,
            CurrentPublishedVersion = 1,
        };
        db.ReportConfigurations.Add(config);
        db.SaveChanges();
        SeedRevision(db, config.Id, ownerId, 1);
        for (var i = 1; i <= 101; i++)
            SeedGrant(db, config.Id, recipientId, ownerId);
        db.SaveChanges();

        var sharing = BuildSharing(db);
        var ex = await Assert.ThrowsAsync<BusinessException>(() => sharing.ListSharedAsync(recipientId));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
    }

    // ==================== 8. API 使用分页契约 ====================

    [Fact]
    public async Task API_自有列表与共享列表_返回分页契约()
    {
        using var db = TestDbFactory.Create();
        var owner = SeedAuthorizedUser(db, "owner", "sales-order");
        var recipient = SeedAuthorizedUser(db, "recipient", "sales-order");

        var ctlOwner = BuildController(db);
        TestAuth.SetUser(ctlOwner, owner);
        var created = await ctlOwner.Create(SaveDto("报表", Definition("orderNo")));
        var id = Assert.IsType<ApiResponse<ReportConfigurationDto>>(Assert.IsType<OkObjectResult>(created).Value).Data!.Id;
        await ctlOwner.Publish(id, 1);
        await ctlOwner.Grant(id, new ReportConfigurationGrantRequestDto { RecipientUserId = recipient, RevisionVersion = 1 });

        var own = await ctlOwner.List(null, null);
        var ownPage = Assert.IsType<ApiResponse<ReportConfigurationPage<ReportConfigurationSummaryDto>>>(
            Assert.IsType<OkObjectResult>(own).Value).Data!;
        Assert.Single(ownPage.Items);

        var ctlRecipient = BuildController(db);
        TestAuth.SetUser(ctlRecipient, recipient);
        var shared = await ctlRecipient.Shared(null, null);
        var sharedPage = Assert.IsType<ApiResponse<ReportConfigurationPage<ReportConfigurationSharedSummaryDto>>>(
            Assert.IsType<OkObjectResult>(shared).Value).Data!;
        Assert.Single(sharedPage.Items);
    }

    [Fact]
    public async Task API_无效limit_显式失败()
    {
        using var db = TestDbFactory.Create();
        var owner = SeedAuthorizedUser(db, "owner", "sales-order");
        var ctl = BuildController(db);
        TestAuth.SetUser(ctl, owner);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.List(-1, null));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }
}
