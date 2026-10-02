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
/// ERP-260 私有报表配置持久化单元测试（Stage 1）：覆盖 owner-only CRUD / 复制 / 软删除、
/// 有界定义校验、跨所有者 fail closed、陈旧预期版本拒绝、授权撤销后立即收敛、审计元数据与
/// 精确 EF 关系型模型映射（离线建模，不连接 SQL Server）。
/// </summary>
public class ReportConfigurationPersistenceTests
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

    private static SysUserRole SeedUserRole(ErpDbContext db, long userId, long roleId)
    {
        var link = new SysUserRole { UserId = userId, RoleId = roleId };
        db.SysUserRoles.Add(link);
        db.SaveChanges();
        return link;
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

    private static ReportConfigurationSaveDto SaveDto(string name, ReportConfigurationDefinition? definition = null)
        => new() { Name = name, Definition = definition ?? SalesOrderDefinition() };

    private static ReportConfigurationDefinition SalesOrderDefinition() => new()
    {
        SchemaVersion = 1,
        DatasetKey = ReportConfigurationConstants.DatasetSalesOrder,
        Fields = new List<string> { "orderNo", "currency", "totalAmount" },
        Filters = new List<ReportConfigurationFilter>(),
        Grouping = new List<string> { ReportConfigurationConstants.GroupNone },
        Aggregates = new List<ReportConfigurationAggregate>(),
        Capabilities = new List<string> { ReportConfigurationConstants.CapabilityPreview },
        Presentation = new ReportConfigurationPresentation { Page = 1, PageSize = 20 },
    };

    // ==================== 1. 身份与所有权 ====================

    [Fact]
    public async Task CreateAsync_无身份_未认证拒绝()
    {
        using var db = TestDbFactory.Create();
        var service = BuildService(db);

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => service.CreateAsync(0, SaveDto("无身份")));

        Assert.Equal(ErrorCodes.Unauthorized, ex.Code);
        Assert.Empty(db.ReportConfigurations);
    }

    [Fact]
    public async Task CreateAsync_保存私有配置_落库且所有权与审计元数据正确()
    {
        using var db = TestDbFactory.Create();
        var ownerId = SeedAuthorizedUser(db, "owner", "sales-order");
        var service = BuildService(db);

        var dto = await service.CreateAsync(ownerId, SaveDto("我的销售订单报表"));

        var entity = Assert.Single(db.ReportConfigurations);
        Assert.Equal(dto.Id, entity.Id);
        Assert.Equal(ownerId, entity.OwnerUserId);
        Assert.Equal("我的销售订单报表", entity.Name);
        Assert.Equal(ReportConfigurationConstants.DatasetSalesOrder, entity.DatasetKey);
        Assert.Equal(ReportConfigurationStatus.Draft, entity.Status);
        Assert.Equal(1, entity.Version);
        Assert.Equal(0, entity.CurrentPublishedVersion);
        Assert.False(string.IsNullOrWhiteSpace(entity.DefinitionJson));

        Assert.Equal(ownerId, entity.CreatedBy);
        Assert.Equal(ownerId, entity.UpdatedBy);
        Assert.True(entity.CreatedAt != default);
        Assert.NotNull(entity.UpdatedAt);
    }

    [Fact]
    public async Task GetAsync_跨所有者访问_按不存在处理且不泄露()
    {
        using var db = TestDbFactory.Create();
        var ownerA = SeedAuthorizedUser(db, "owner-a", "sales-order");
        var ownerB = SeedAuthorizedUser(db, "owner-b", "sales-order");
        var service = BuildService(db);

        var created = await service.CreateAsync(ownerA, SaveDto("A 的私有报表"));

        var ex = await Assert.ThrowsAsync<BusinessException>(() => service.GetAsync(ownerB, created.Id));
        Assert.Equal(ErrorCodes.NotFound, ex.Code);
    }

    [Fact]
    public async Task ListAsync_仅返回当前用户配置()
    {
        using var db = TestDbFactory.Create();
        var ownerA = SeedAuthorizedUser(db, "owner-a", "sales-order");
        var ownerB = SeedAuthorizedUser(db, "owner-b", "sales-order");
        var service = BuildService(db);

        await service.CreateAsync(ownerA, SaveDto("A 的报表"));
        await service.CreateAsync(ownerB, SaveDto("B 的报表"));

        var listA = await service.ListAsync(ownerA);
        var listB = await service.ListAsync(ownerB);

        Assert.Single(listA);
        Assert.Equal("A 的报表", listA[0].Name);
        Assert.Single(listB);
        Assert.Equal("B 的报表", listB[0].Name);
    }

    // ==================== 2. 有界定义与授权 ====================

    [Fact]
    public async Task CreateAsync_未授权数据集_拒绝()
    {
        using var db = TestDbFactory.Create();
        var noAuthUserId = SeedUser(db, "no-auth").Id;
        var service = BuildService(db);

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => service.CreateAsync(noAuthUserId, SaveDto("越权报表")));

        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
        Assert.Empty(db.ReportConfigurations);
    }

    [Fact]
    public async Task CreateAsync_未知字段_有界校验拒绝且不落库()
    {
        using var db = TestDbFactory.Create();
        var ownerId = SeedAuthorizedUser(db, "owner", "sales-order");
        var service = BuildService(db);

        var bad = SalesOrderDefinition();
        bad.Fields = new List<string> { "orderNo", "notARealField" };

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => service.CreateAsync(ownerId, SaveDto("坏定义", bad)));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Empty(db.ReportConfigurations);
    }

    [Fact]
    public async Task GetAsync_授权撤销后_立即收敛拒绝()
    {
        using var db = TestDbFactory.Create();
        var user = SeedUser(db, "revoke");
        var role = SeedRole(db, "revoke");
        SeedUserRole(db, user.Id, role.Id);
        var menu = SeedMenu(db, "sales-order");
        SeedRoleMenu(db, role.Id, menu.Id);
        var service = BuildService(db);

        var created = await service.CreateAsync(user.Id, SaveDto("即将被撤销"));

        menu.IsDeleted = true;
        db.SaveChanges();

        var ex = await Assert.ThrowsAsync<BusinessException>(() => service.GetAsync(user.Id, created.Id));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }


    // ==================== 3. 更新 / 复制 / 重命名 / 软删除 ====================

    [Fact]
    public async Task UpdateAsync_合法更新_定义生效且版本递增()
    {
        using var db = TestDbFactory.Create();
        var ownerId = SeedAuthorizedUser(db, "owner", "sales-order");
        var service = BuildService(db);

        var created = await service.CreateAsync(ownerId, SaveDto("原始名称"));

        var next = SalesOrderDefinition();
        next.Fields = new List<string> { "orderNo", "currency", "totalAmount", "paymentTerms" };
        var updated = await service.UpdateAsync(ownerId, created.Id, created.Version, SaveDto("更新名称", next));

        Assert.Equal("更新名称", updated.Name);
        Assert.Equal(2, updated.Version);
        Assert.Equal(ReportConfigurationStatus.Draft, updated.Status);
        Assert.Equal(4, updated.Definition!.Fields.Count);
        Assert.Contains("paymentTerms", updated.Definition.Fields);
    }

    [Fact]
    public async Task UpdateAsync_陈旧预期版本_拒绝且不落库()
    {
        using var db = TestDbFactory.Create();
        var ownerId = SeedAuthorizedUser(db, "owner", "sales-order");
        var service = BuildService(db);

        var created = await service.CreateAsync(ownerId, SaveDto("原始名称"));

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => service.UpdateAsync(ownerId, created.Id, created.Version - 1, SaveDto("篡改", SalesOrderDefinition())));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);

        var persisted = await service.GetAsync(ownerId, created.Id);
        Assert.Equal("原始名称", persisted.Name);
        Assert.Equal(3, persisted.Definition!.Fields.Count);
    }

    [Fact]
    public async Task CopyAsync_复制为新的私有草稿_同定义不同Id()
    {
        using var db = TestDbFactory.Create();
        var ownerId = SeedAuthorizedUser(db, "owner", "sales-order");
        var service = BuildService(db);

        var source = await service.CreateAsync(ownerId, SaveDto("源报表"));

        var copy = await service.CopyAsync(ownerId, source.Id);

        Assert.NotEqual(source.Id, copy.Id);
        Assert.Equal(ownerId, copy.OwnerUserId);
        Assert.Equal("源报表 副本", copy.Name);
        Assert.Equal(ReportConfigurationStatus.Draft, copy.Status);
        Assert.Equal(1, copy.Version);
        Assert.Equal(0, copy.CurrentPublishedVersion);
        Assert.Equal(source.DatasetKey, copy.DatasetKey);
        Assert.Equal(source.Definition!.Fields, copy.Definition!.Fields);

        Assert.Equal(2, db.ReportConfigurations.Count());
    }

    [Fact]
    public async Task RenameAsync_合法重命名_版本递增()
    {
        using var db = TestDbFactory.Create();
        var ownerId = SeedAuthorizedUser(db, "owner", "sales-order");
        var service = BuildService(db);

        var created = await service.CreateAsync(ownerId, SaveDto("旧名称"));

        var renamed = await service.RenameAsync(ownerId, created.Id, created.Version, " 新名称 ");

        Assert.Equal("新名称", renamed.Name);
        Assert.Equal(2, renamed.Version);
    }

    [Fact]
    public async Task DeleteAsync_软删除_之后加载与列表不可见()
    {
        using var db = TestDbFactory.Create();
        var ownerId = SeedAuthorizedUser(db, "owner", "sales-order");
        var service = BuildService(db);

        var created = await service.CreateAsync(ownerId, SaveDto("要删除的报表"));

        await service.DeleteAsync(ownerId, created.Id, created.Version);

        Assert.Empty(await service.ListAsync(ownerId));
        var ex = await Assert.ThrowsAsync<BusinessException>(() => service.GetAsync(ownerId, created.Id));
        Assert.Equal(ErrorCodes.NotFound, ex.Code);

        var entity = Assert.Single(db.ReportConfigurations);
        Assert.True(entity.IsDeleted);
    }

    [Fact]
    public async Task DeleteAsync_陈旧预期版本_拒绝()
    {
        using var db = TestDbFactory.Create();
        var ownerId = SeedAuthorizedUser(db, "owner", "sales-order");
        var service = BuildService(db);

        var created = await service.CreateAsync(ownerId, SaveDto("要删除的报表"));

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => service.DeleteAsync(ownerId, created.Id, created.Version + 1));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.False(Assert.Single(db.ReportConfigurations).IsDeleted);
    }


    // ==================== 4. 精确 EF 关系型模型映射（离线建模，不连接 SQL Server） ====================

    [Fact]
    public void 模型配置契约_表名列长JSON并发令牌索引与级联删除()
    {
        var options = new DbContextOptionsBuilder<ErpDbContext>()
            .UseSqlServer("Server=localhost;Database=__offline_model__;Trusted_Connection=True;Encrypt=False;")
            .Options;
        using var db = new ErpDbContext(options);

        var configType = db.Model.FindEntityType(typeof(ReportConfiguration));
        Assert.NotNull(configType);
        Assert.Equal("ReportConfigurations", configType!.GetTableName());
        Assert.Equal("db_owner", configType.GetSchema());

        Assert.Equal(200, configType.FindProperty(nameof(ReportConfiguration.Name))!.GetMaxLength());
        Assert.Equal(50, configType.FindProperty(nameof(ReportConfiguration.DatasetKey))!.GetMaxLength());
        Assert.Equal("nvarchar(max)", configType.FindProperty(nameof(ReportConfiguration.DefinitionJson))!.GetColumnType());
        Assert.True(configType.FindProperty(nameof(ReportConfiguration.Version))!.IsConcurrencyToken);

        Assert.Contains(configType.GetIndexes(),
            i => i.Properties.Select(p => p.Name).SequenceEqual(new[] { "OwnerUserId", "IsDeleted" }));

        var revisionType = db.Model.FindEntityType(typeof(ReportConfigurationRevision));
        Assert.NotNull(revisionType);
        Assert.Equal("ReportConfigurationRevisions", revisionType!.GetTableName());
        Assert.Equal("db_owner", revisionType.GetSchema());

        Assert.Equal(200, revisionType.FindProperty(nameof(ReportConfigurationRevision.Name))!.GetMaxLength());
        Assert.Equal(50, revisionType.FindProperty(nameof(ReportConfigurationRevision.DatasetKey))!.GetMaxLength());

        // 唯一修订元组（同配置内版本号唯一）
        Assert.Contains(revisionType.GetIndexes(),
            i => i.IsUnique
                && i.Properties.Select(p => p.Name).SequenceEqual(new[] { "ReportConfigurationId", "Version" }));

        // 修订 → 配置：必填外键 + 级联删除
        var fk = Assert.Single(revisionType.GetForeignKeys());
        Assert.Equal(typeof(ReportConfiguration), fk.PrincipalEntityType.ClrType);
        Assert.Equal(DeleteBehavior.Cascade, fk.DeleteBehavior);
    }
}

