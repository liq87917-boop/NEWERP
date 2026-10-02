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
/// ERP-260 私有报表配置不可变版本链单元测试（Stage 1）：覆盖发布固定快照、草稿编辑不静默替换已发布修订、
/// 再次发布追加新修订、恢复历史版本通过新修订实现、陈旧预期版本拒绝、授权撤销后发布失败不产生修订，
/// 以及修订列表 owner-only 与排序。
/// </summary>
public class ReportConfigurationVersionTests
{
    // ==================== 0. 脚手架 ====================

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

    private static long SeedAuthorizedUser(ErpDbContext db, string userName, string menuCode)
    {
        var user = SeedUser(db, userName);
        var role = SeedRole(db, menuCode);
        SeedUserRole(db, user.Id, role.Id);
        SeedRoleMenu(db, role.Id, SeedMenu(db, menuCode).Id);
        return user.Id;
    }

    private static IReportConfigurationService BuildService(ErpDbContext db)
        => new ReportConfigurationService(db, BuildCatalog(db));

    private static IReportConfigurationCatalog BuildCatalog(ErpDbContext db)
        => new ReportConfigurationCatalog(new IReportConfigurationDatasetProvider[]
        {
            new SalesOrderReportConfigurationDatasetProvider(new DynamicSalesOrderReportQuery(db)),
            new ReceivableReportConfigurationDatasetProvider(new DynamicReceivableReportQuery(db)),
        });

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

    private static readonly string[] DefinitionA = { "orderNo", "currency", "totalAmount" };
    private static readonly string[] DefinitionB = { "orderNo", "currency", "totalAmount", "paymentTerms" };

    // ==================== 1. 发布 / 草稿编辑 ====================

    [Fact]
    public async Task PublishAsync_发布固定不可变快照_状态与版本递增()
    {
        using var db = TestDbFactory.Create();
        var ownerId = SeedAuthorizedUser(db, "owner", "sales-order");
        var service = BuildService(db);

        var created = await service.CreateAsync(ownerId, SaveDto("报表", Definition(DefinitionA)));
        var published = await service.PublishAsync(ownerId, created.Id, created.Version);

        Assert.Equal(ReportConfigurationStatus.Published, published.Status);
        Assert.Equal(1, published.CurrentPublishedVersion);
        Assert.Equal(2, published.Version);

        var revisions = await service.ListRevisionsAsync(ownerId, created.Id);
        var revision = Assert.Single(revisions);
        Assert.Equal(1, revision.Version);
        Assert.Equal(ownerId, revision.PublishedBy);
        Assert.Equal(DefinitionA, revision.Definition!.Fields);
    }

    [Fact]
    public async Task UpdateAsync_草稿编辑不替换已发布修订()
    {
        using var db = TestDbFactory.Create();
        var ownerId = SeedAuthorizedUser(db, "owner", "sales-order");
        var service = BuildService(db);

        var created = await service.CreateAsync(ownerId, SaveDto("报表", Definition(DefinitionA)));
        await service.PublishAsync(ownerId, created.Id, created.Version);

        var updated = await service.UpdateAsync(ownerId, created.Id, 2, SaveDto("报表", Definition(DefinitionB)));

        Assert.Equal(ReportConfigurationStatus.Draft, updated.Status);
        Assert.Equal(1, updated.CurrentPublishedVersion);

        var revisions = await service.ListRevisionsAsync(ownerId, created.Id);
        var revision = Assert.Single(revisions);
        Assert.Equal(1, revision.Version);
        Assert.Equal(DefinitionA, revision.Definition!.Fields);

        var current = await service.GetAsync(ownerId, created.Id);
        Assert.Equal(DefinitionB, current.Definition!.Fields);
    }

    [Fact]
    public async Task PublishAsync_再次发布_追加新修订且旧修订保留()
    {
        using var db = TestDbFactory.Create();
        var ownerId = SeedAuthorizedUser(db, "owner", "sales-order");
        var service = BuildService(db);

        var created = await service.CreateAsync(ownerId, SaveDto("报表", Definition(DefinitionA)));
        await service.PublishAsync(ownerId, created.Id, created.Version);
        await service.UpdateAsync(ownerId, created.Id, 2, SaveDto("报表", Definition(DefinitionB)));
        var published = await service.PublishAsync(ownerId, created.Id, 3);

        Assert.Equal(2, published.CurrentPublishedVersion);

        var revisions = await service.ListRevisionsAsync(ownerId, created.Id);
        Assert.Equal(2, revisions.Count);
        Assert.Equal(DefinitionA, revisions[0].Definition!.Fields);
        Assert.Equal(DefinitionB, revisions[1].Definition!.Fields);
        Assert.Equal(1, revisions[0].Version);
        Assert.Equal(2, revisions[1].Version);
    }


    [Fact]
    public async Task RestoreAsync_恢复历史版本_通过新修订实现且旧修订保留()
    {
        using var db = TestDbFactory.Create();
        var ownerId = SeedAuthorizedUser(db, "owner", "sales-order");
        var service = BuildService(db);

        var created = await service.CreateAsync(ownerId, SaveDto("报表", Definition(DefinitionA)));
        await service.PublishAsync(ownerId, created.Id, created.Version);
        await service.UpdateAsync(ownerId, created.Id, 2, SaveDto("报表", Definition(DefinitionB)));
        await service.PublishAsync(ownerId, created.Id, 3);

        var restored = await service.RestoreAsync(ownerId, created.Id, 4, versionNumber: 1);

        Assert.Equal(ReportConfigurationStatus.Published, restored.Status);
        Assert.Equal(3, restored.CurrentPublishedVersion);
        Assert.Equal(DefinitionA, restored.Definition!.Fields);

        var revisions = await service.ListRevisionsAsync(ownerId, created.Id);
        Assert.Equal(3, revisions.Count);
        Assert.Equal(DefinitionA, revisions[0].Definition!.Fields);
        Assert.Equal(DefinitionB, revisions[1].Definition!.Fields);
        Assert.Equal(DefinitionA, revisions[2].Definition!.Fields);
        Assert.Equal(new[] { 1, 2, 3 }, revisions.Select(r => r.Version));
    }

    // ==================== 2. 陈旧预期版本 / 无效版本 / 授权撤销 ====================

    [Fact]
    public async Task RestoreAsync_无效版本号_拒绝()
    {
        using var db = TestDbFactory.Create();
        var ownerId = SeedAuthorizedUser(db, "owner", "sales-order");
        var service = BuildService(db);

        var created = await service.CreateAsync(ownerId, SaveDto("报表", Definition(DefinitionA)));

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => service.RestoreAsync(ownerId, created.Id, created.Version, versionNumber: 5));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public async Task PublishAsync_陈旧预期版本_拒绝()
    {
        using var db = TestDbFactory.Create();
        var ownerId = SeedAuthorizedUser(db, "owner", "sales-order");
        var service = BuildService(db);

        var created = await service.CreateAsync(ownerId, SaveDto("报表", Definition(DefinitionA)));

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => service.PublishAsync(ownerId, created.Id, expectedVersion: 999));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Empty(db.ReportConfigurationRevisions);
    }

    [Fact]
    public async Task RestoreAsync_陈旧预期版本_拒绝()
    {
        using var db = TestDbFactory.Create();
        var ownerId = SeedAuthorizedUser(db, "owner", "sales-order");
        var service = BuildService(db);

        var created = await service.CreateAsync(ownerId, SaveDto("报表", Definition(DefinitionA)));
        await service.PublishAsync(ownerId, created.Id, created.Version);

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => service.RestoreAsync(ownerId, created.Id, expectedVersion: 999, versionNumber: 1));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Single(db.ReportConfigurationRevisions);
    }


    [Fact]
    public async Task PublishAsync_授权撤销后失败_不产生任何修订()
    {
        using var db = TestDbFactory.Create();
        var user = SeedUser(db, "revoke");
        var role = SeedRole(db, "revoke");
        SeedUserRole(db, user.Id, role.Id);
        var menu = SeedMenu(db, "sales-order");
        SeedRoleMenu(db, role.Id, menu.Id);
        var service = BuildService(db);

        var created = await service.CreateAsync(user.Id, SaveDto("报表", Definition(DefinitionA)));

        menu.IsDeleted = true;
        db.SaveChanges();

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => service.PublishAsync(user.Id, created.Id, created.Version));

        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
        Assert.Empty(db.ReportConfigurationRevisions);

        var config = Assert.Single(db.ReportConfigurations);
        Assert.Equal(ReportConfigurationStatus.Draft, config.Status);
        Assert.Equal(0, config.CurrentPublishedVersion);
    }

    // ==================== 3. 修订列表 ====================

    [Fact]
    public async Task ListRevisionsAsync_按版本号排序返回()
    {
        using var db = TestDbFactory.Create();
        var ownerId = SeedAuthorizedUser(db, "owner", "sales-order");
        var service = BuildService(db);

        var created = await service.CreateAsync(ownerId, SaveDto("报表", Definition(DefinitionA)));
        await service.PublishAsync(ownerId, created.Id, created.Version);
        await service.UpdateAsync(ownerId, created.Id, 2, SaveDto("报表", Definition(DefinitionB)));
        await service.PublishAsync(ownerId, created.Id, 3);

        var revisions = await service.ListRevisionsAsync(ownerId, created.Id);

        Assert.Equal(new[] { 1, 2 }, revisions.Select(r => r.Version));
    }

    [Fact]
    public async Task ListRevisionsAsync_跨所有者_拒绝()
    {
        using var db = TestDbFactory.Create();
        var ownerA = SeedAuthorizedUser(db, "owner-a", "sales-order");
        var ownerB = SeedAuthorizedUser(db, "owner-b", "sales-order");
        var service = BuildService(db);

        var created = await service.CreateAsync(ownerA, SaveDto("报表", Definition(DefinitionA)));

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => service.ListRevisionsAsync(ownerB, created.Id));

        Assert.Equal(ErrorCodes.NotFound, ex.Code);
    }
}

