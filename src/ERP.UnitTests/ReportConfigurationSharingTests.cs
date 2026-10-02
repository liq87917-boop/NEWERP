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
/// ERP-265 只读共享授权单元测试（Stage 1）：覆盖 owner grant/revoke/list 的固定修订与乐观并发、
/// 被授权人 shared list/detail/copy 只暴露固定快照、撤销 / 删除 / 停用 / 失去权限 fail closed、
/// 共享预览按被授权人作用域执行，以及授权表的精确 EF 关系型模型映射（离线建模，不连接 SQL Server）。
/// </summary>
public class ReportConfigurationSharingTests
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

    private static IReportConfigurationSharingService BuildSharing(ErpDbContext db)
        => new ReportConfigurationSharingService(db, BuildCatalog(db));

    private static IReportConfigurationExecutionService BuildExecution(ErpDbContext db)
        => new ReportConfigurationExecutionService(db, BuildProviders(db));

    private static IReadOnlyList<IReportConfigurationDatasetProvider> BuildProviders(ErpDbContext db)
        => new IReportConfigurationDatasetProvider[]
        {
            new SalesOrderReportConfigurationDatasetProvider(new DynamicSalesOrderReportQuery(db)),
            new ReceivableReportConfigurationDatasetProvider(new DynamicReceivableReportQuery(db)),
        };

    private static IReportConfigurationCatalog BuildCatalog(ErpDbContext db)
        => new ReportConfigurationCatalog(BuildProviders(db));

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

    // ==================== 1. owner grant/revoke ====================

    [Fact]
    public async Task GrantAsync_创建授权_固定修订并记录审计与并发版本()
    {
        using var db = TestDbFactory.Create();
        var ownerId = SeedAuthorizedUser(db, "owner", "sales-order");
        var recipientId = SeedAuthorizedUser(db, "recipient", "sales-order");
        var service = BuildService(db);
        var sharing = BuildSharing(db);

        var created = await service.CreateAsync(ownerId, SaveDto("报表", Definition(DefinitionA)));
        await service.PublishAsync(ownerId, created.Id, created.Version);

        var grant = await sharing.GrantAsync(ownerId, created.Id,
            new ReportConfigurationGrantRequestDto { RecipientUserId = recipientId, RevisionVersion = 1 });

        Assert.Equal(recipientId, grant.RecipientUserId);
        Assert.Equal(created.Id, grant.ReportConfigurationId);
        Assert.Equal(1, grant.RevisionVersion);
        Assert.Equal(ownerId, grant.GrantedByUserId);
        Assert.Equal(1, grant.Version);

        var entity = Assert.Single(db.ReportConfigurationGrants);
        Assert.False(entity.IsDeleted);
        Assert.Equal(ownerId, entity.CreatedBy);
        Assert.Equal(ownerId, entity.UpdatedBy);
        Assert.Equal(ownerId, entity.GrantedByUserId);
        Assert.True(entity.CreatedAt != default);
        Assert.NotNull(entity.UpdatedAt);
    }

    [Fact]
    public async Task GrantAsync_未发布或不存在修订_拒绝且不落授权()
    {
        using var db = TestDbFactory.Create();
        var ownerId = SeedAuthorizedUser(db, "owner", "sales-order");
        var recipientId = SeedAuthorizedUser(db, "recipient", "sales-order");
        var service = BuildService(db);
        var sharing = BuildSharing(db);

        var created = await service.CreateAsync(ownerId, SaveDto("报表", Definition(DefinitionA)));

        var ex = await Assert.ThrowsAsync<BusinessException>(() => sharing.GrantAsync(ownerId, created.Id,
            new ReportConfigurationGrantRequestDto { RecipientUserId = recipientId, RevisionVersion = 1 }));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Empty(db.ReportConfigurationGrants);
    }

    [Fact]
    public async Task GrantAsync_被授权用户不存在_拒绝()
    {
        using var db = TestDbFactory.Create();
        var ownerId = SeedAuthorizedUser(db, "owner", "sales-order");
        var service = BuildService(db);
        var sharing = BuildSharing(db);

        var created = await service.CreateAsync(ownerId, SaveDto("报表", Definition(DefinitionA)));
        await service.PublishAsync(ownerId, created.Id, created.Version);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => sharing.GrantAsync(ownerId, created.Id,
            new ReportConfigurationGrantRequestDto { RecipientUserId = 99999, RevisionVersion = 1 }));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Empty(db.ReportConfigurationGrants);
    }

    [Fact]
    public async Task GrantAsync_被授权用户已停用_拒绝()
    {
        using var db = TestDbFactory.Create();
        var ownerId = SeedAuthorizedUser(db, "owner", "sales-order");
        var recipient = SeedUser(db, "disabled", UserStatus.Disabled);
        var service = BuildService(db);
        var sharing = BuildSharing(db);

        var created = await service.CreateAsync(ownerId, SaveDto("报表", Definition(DefinitionA)));
        await service.PublishAsync(ownerId, created.Id, created.Version);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => sharing.GrantAsync(ownerId, created.Id,
            new ReportConfigurationGrantRequestDto { RecipientUserId = recipient.Id, RevisionVersion = 1 }));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Empty(db.ReportConfigurationGrants);
    }

    [Fact]
    public async Task GrantAsync_所有者再次发布_固定修订不变()
    {
        using var db = TestDbFactory.Create();
        var ownerId = SeedAuthorizedUser(db, "owner", "sales-order");
        var recipientId = SeedAuthorizedUser(db, "recipient", "sales-order");
        var service = BuildService(db);
        var sharing = BuildSharing(db);

        var created = await service.CreateAsync(ownerId, SaveDto("报表", Definition(DefinitionA)));
        await service.PublishAsync(ownerId, created.Id, created.Version);
        await sharing.GrantAsync(ownerId, created.Id,
            new ReportConfigurationGrantRequestDto { RecipientUserId = recipientId, RevisionVersion = 1 });

        await service.UpdateAsync(ownerId, created.Id, 2, SaveDto("报表", Definition(DefinitionB)));
        await service.PublishAsync(ownerId, created.Id, 3);

        var entity = Assert.Single(db.ReportConfigurationGrants);
        Assert.Equal(1, entity.RevisionVersion); // pin 不随再次发布漂移
        Assert.Equal(2, db.ReportConfigurationRevisions.Count(r => !r.IsDeleted));
    }

    [Fact]
    public async Task GrantAsync_变更固定修订_无预期版本冲突_带预期版本成功()
    {
        using var db = TestDbFactory.Create();
        var ownerId = SeedAuthorizedUser(db, "owner", "sales-order");
        var recipientId = SeedAuthorizedUser(db, "recipient", "sales-order");
        var service = BuildService(db);
        var sharing = BuildSharing(db);

        var created = await service.CreateAsync(ownerId, SaveDto("报表", Definition(DefinitionA)));
        await service.PublishAsync(ownerId, created.Id, created.Version);
        await service.UpdateAsync(ownerId, created.Id, 2, SaveDto("报表", Definition(DefinitionB)));
        await service.PublishAsync(ownerId, created.Id, 3);

        await sharing.GrantAsync(ownerId, created.Id,
            new ReportConfigurationGrantRequestDto { RecipientUserId = recipientId, RevisionVersion = 1 });

        var conflict = await Assert.ThrowsAsync<BusinessException>(() => sharing.GrantAsync(ownerId, created.Id,
            new ReportConfigurationGrantRequestDto { RecipientUserId = recipientId, RevisionVersion = 2 }));
        Assert.Equal(ErrorCodes.RuleConflict, conflict.Code);

        var updated = await sharing.GrantAsync(ownerId, created.Id,
            new ReportConfigurationGrantRequestDto { RecipientUserId = recipientId, RevisionVersion = 2, ExpectedVersion = 1 });
        Assert.Equal(2, updated.RevisionVersion);
        Assert.Equal(2, updated.Version);
        Assert.Equal(2, db.ReportConfigurationGrants.Single(g => !g.IsDeleted).RevisionVersion);
    }

    [Fact]
    public async Task RevokeAsync_撤销后软删除且列表不可见()
    {
        using var db = TestDbFactory.Create();
        var ownerId = SeedAuthorizedUser(db, "owner", "sales-order");
        var recipientId = SeedAuthorizedUser(db, "recipient", "sales-order");
        var service = BuildService(db);
        var sharing = BuildSharing(db);

        var created = await service.CreateAsync(ownerId, SaveDto("报表", Definition(DefinitionA)));
        await service.PublishAsync(ownerId, created.Id, created.Version);
        var grant = await sharing.GrantAsync(ownerId, created.Id,
            new ReportConfigurationGrantRequestDto { RecipientUserId = recipientId, RevisionVersion = 1 });

        await sharing.RevokeAsync(ownerId, created.Id, recipientId, grant.Version);

        Assert.Empty(await sharing.ListGrantsAsync(ownerId, created.Id));
        Assert.True(db.ReportConfigurationGrants.Single().IsDeleted);
        Assert.Empty(await sharing.ListSharedAsync(recipientId));
    }

    [Fact]
    public async Task RevokeAsync_陈旧预期版本_拒绝且授权仍有效()
    {
        using var db = TestDbFactory.Create();
        var ownerId = SeedAuthorizedUser(db, "owner", "sales-order");
        var recipientId = SeedAuthorizedUser(db, "recipient", "sales-order");
        var service = BuildService(db);
        var sharing = BuildSharing(db);

        var created = await service.CreateAsync(ownerId, SaveDto("报表", Definition(DefinitionA)));
        await service.PublishAsync(ownerId, created.Id, created.Version);
        await sharing.GrantAsync(ownerId, created.Id,
            new ReportConfigurationGrantRequestDto { RecipientUserId = recipientId, RevisionVersion = 1 });

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => sharing.RevokeAsync(ownerId, created.Id, recipientId, expectedVersion: 999));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.False(db.ReportConfigurationGrants.Single().IsDeleted);
        Assert.Single(await sharing.ListGrantsAsync(ownerId, created.Id));
    }

    [Fact]
    public async Task ListGrantsAsync_跨所有者_拒绝()
    {
        using var db = TestDbFactory.Create();
        var ownerA = SeedAuthorizedUser(db, "owner-a", "sales-order");
        var ownerB = SeedAuthorizedUser(db, "owner-b", "sales-order");
        var recipientId = SeedAuthorizedUser(db, "recipient", "sales-order");
        var service = BuildService(db);
        var sharing = BuildSharing(db);

        var created = await service.CreateAsync(ownerA, SaveDto("报表", Definition(DefinitionA)));
        await service.PublishAsync(ownerA, created.Id, created.Version);
        await sharing.GrantAsync(ownerA, created.Id,
            new ReportConfigurationGrantRequestDto { RecipientUserId = recipientId, RevisionVersion = 1 });

        var ex = await Assert.ThrowsAsync<BusinessException>(() => sharing.ListGrantsAsync(ownerB, created.Id));
        Assert.Equal(ErrorCodes.NotFound, ex.Code);
    }

    // ==================== 2. recipient 只读 / 复制 ====================

    [Fact]
    public async Task ListSharedAsync_只暴露固定快照_不含草稿与其它修订()
    {
        using var db = TestDbFactory.Create();
        var ownerId = SeedAuthorizedUser(db, "owner", "sales-order");
        var recipientId = SeedAuthorizedUser(db, "recipient", "sales-order");
        var service = BuildService(db);
        var sharing = BuildSharing(db);

        var created = await service.CreateAsync(ownerId, SaveDto("报表", Definition(DefinitionA)));
        await service.PublishAsync(ownerId, created.Id, created.Version);
        await sharing.GrantAsync(ownerId, created.Id,
            new ReportConfigurationGrantRequestDto { RecipientUserId = recipientId, RevisionVersion = 1 });

        await service.UpdateAsync(ownerId, created.Id, 2, SaveDto("改名", Definition(DefinitionB)));
        await service.PublishAsync(ownerId, created.Id, 3);

        var shared = Assert.Single(await sharing.ListSharedAsync(recipientId));
        Assert.Equal(created.Id, shared.ReportConfigurationId);
        Assert.Equal("报表", shared.Name);           // 固定修订快照名称，不是当前草稿名
        Assert.Equal(1, shared.RevisionVersion);     // 仍固定 v1，不是 v2
        Assert.Equal(ownerId, shared.OwnerUserId);
    }

    [Fact]
    public async Task GetSharedAsync_撤销后_拒绝()
    {
        using var db = TestDbFactory.Create();
        var ownerId = SeedAuthorizedUser(db, "owner", "sales-order");
        var recipientId = SeedAuthorizedUser(db, "recipient", "sales-order");
        var service = BuildService(db);
        var sharing = BuildSharing(db);

        var created = await service.CreateAsync(ownerId, SaveDto("报表", Definition(DefinitionA)));
        await service.PublishAsync(ownerId, created.Id, created.Version);
        var grant = await sharing.GrantAsync(ownerId, created.Id,
            new ReportConfigurationGrantRequestDto { RecipientUserId = recipientId, RevisionVersion = 1 });
        await sharing.RevokeAsync(ownerId, created.Id, recipientId, grant.Version);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => sharing.GetSharedAsync(recipientId, created.Id));
        Assert.Equal(ErrorCodes.NotFound, ex.Code);
    }

    [Fact]
    public async Task GetSharedAsync_配置删除后_拒绝()
    {
        using var db = TestDbFactory.Create();
        var ownerId = SeedAuthorizedUser(db, "owner", "sales-order");
        var recipientId = SeedAuthorizedUser(db, "recipient", "sales-order");
        var service = BuildService(db);
        var sharing = BuildSharing(db);

        var created = await service.CreateAsync(ownerId, SaveDto("报表", Definition(DefinitionA)));
        await service.PublishAsync(ownerId, created.Id, created.Version);
        await sharing.GrantAsync(ownerId, created.Id,
            new ReportConfigurationGrantRequestDto { RecipientUserId = recipientId, RevisionVersion = 1 });
        await service.DeleteAsync(ownerId, created.Id, 2);

        Assert.Empty(await sharing.ListSharedAsync(recipientId));
        var ex = await Assert.ThrowsAsync<BusinessException>(() => sharing.GetSharedAsync(recipientId, created.Id));
        Assert.Equal(ErrorCodes.NotFound, ex.Code);
    }

    [Fact]
    public async Task CopySharedAsync_创建受赠人自有草稿_不改写原配置()
    {
        using var db = TestDbFactory.Create();
        var ownerId = SeedAuthorizedUser(db, "owner", "sales-order");
        var recipientId = SeedAuthorizedUser(db, "recipient", "sales-order");
        var service = BuildService(db);
        var sharing = BuildSharing(db);

        var created = await service.CreateAsync(ownerId, SaveDto("报表", Definition(DefinitionA)));
        await service.PublishAsync(ownerId, created.Id, created.Version);
        await sharing.GrantAsync(ownerId, created.Id,
            new ReportConfigurationGrantRequestDto { RecipientUserId = recipientId, RevisionVersion = 1 });

        var copy = await sharing.CopySharedAsync(recipientId, created.Id);

        Assert.NotEqual(created.Id, copy.Id);
        Assert.Equal(recipientId, copy.OwnerUserId);
        Assert.Equal(ReportConfigurationStatus.Draft, copy.Status);
        Assert.EndsWith("副本", copy.Name);
        Assert.Equal(DefinitionA, copy.Definition!.Fields);

        // 原配置与固定修订未被改写
        var original = await service.GetAsync(ownerId, created.Id);
        Assert.Equal(ReportConfigurationStatus.Published, original.Status);
        Assert.Equal(2, original.Version);
        var revisions = await service.ListRevisionsAsync(ownerId, created.Id);
        Assert.Single(revisions);
        Assert.Equal(DefinitionA, revisions[0].Definition!.Fields);
    }

    [Fact]
    public async Task CopySharedAsync_失去数据集授权后_拒绝()
    {
        using var db = TestDbFactory.Create();
        var ownerId = SeedAuthorizedUser(db, "owner", "sales-order");
        var recipientId = SeedAuthorizedUser(db, "recipient", "sales-order");
        var service = BuildService(db);
        var sharing = BuildSharing(db);

        var created = await service.CreateAsync(ownerId, SaveDto("报表", Definition(DefinitionA)));
        await service.PublishAsync(ownerId, created.Id, created.Version);
        await sharing.GrantAsync(ownerId, created.Id,
            new ReportConfigurationGrantRequestDto { RecipientUserId = recipientId, RevisionVersion = 1 });

        // 撤销被授权人的销售订单菜单授权（lost underlying ERP permission）
        foreach (var menu in db.SysMenus.Where(m => m.MenuCode == "sales-order").ToList())
            menu.IsDeleted = true;
        db.SaveChanges();

        var ex = await Assert.ThrowsAsync<BusinessException>(() => sharing.CopySharedAsync(recipientId, created.Id));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
        Assert.Single(db.ReportConfigurations); // 原配置未被新增 / 复制
    }

    [Fact]
    public async Task CopySharedAsync_被授权人停用后_拒绝()
    {
        using var db = TestDbFactory.Create();
        var ownerId = SeedAuthorizedUser(db, "owner", "sales-order");
        var recipientId = SeedAuthorizedUser(db, "recipient", "sales-order");
        var service = BuildService(db);
        var sharing = BuildSharing(db);

        var created = await service.CreateAsync(ownerId, SaveDto("报表", Definition(DefinitionA)));
        await service.PublishAsync(ownerId, created.Id, created.Version);
        await sharing.GrantAsync(ownerId, created.Id,
            new ReportConfigurationGrantRequestDto { RecipientUserId = recipientId, RevisionVersion = 1 });

        db.SysUsers.Single(u => u.Id == recipientId).Status = UserStatus.Disabled;
        db.SaveChanges();

        Assert.Empty(await sharing.ListSharedAsync(recipientId));
        var ex = await Assert.ThrowsAsync<BusinessException>(() => sharing.CopySharedAsync(recipientId, created.Id));
        Assert.Equal(ErrorCodes.NotFound, ex.Code);
    }

    // ==================== 3. 共享预览（按被授权人作用域） ====================

    [Fact]
    public async Task 共享预览_按被授权人作用域成功_撤销后失败关闭()
    {
        using var db = TestDbFactory.Create();
        var ownerId = SeedAuthorizedUser(db, "owner", "sales-order");
        var recipientId = SeedAuthorizedUser(db, "recipient", "sales-order");
        var service = BuildService(db);
        var sharing = BuildSharing(db);
        var execution = BuildExecution(db);

        var created = await service.CreateAsync(ownerId, SaveDto("报表", Definition(DefinitionA)));
        await service.PublishAsync(ownerId, created.Id, created.Version);
        var grant = await sharing.GrantAsync(ownerId, created.Id,
            new ReportConfigurationGrantRequestDto { RecipientUserId = recipientId, RevisionVersion = 1 });

        var preview = await execution.PreviewAsync(recipientId,
            new ReportConfigurationPreviewRequest { ConfigurationId = created.Id, RevisionVersion = 2 });

        Assert.True(preview.IsPinnedRevision);
        Assert.Equal(1, preview.PinnedRevisionVersion); // 忽略客户端 RevisionVersion，使用固定修订
        Assert.Equal("报表", preview.Name);
        Assert.Equal(created.Id, preview.ConfigurationId);

        await sharing.RevokeAsync(ownerId, created.Id, recipientId, grant.Version);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => execution.PreviewAsync(recipientId,
            new ReportConfigurationPreviewRequest { ConfigurationId = created.Id }));
        Assert.Equal(ErrorCodes.NotFound, ex.Code);
    }

    [Fact]
    public async Task 共享预览_被授权人失去菜单授权_失败关闭()
    {
        using var db = TestDbFactory.Create();
        var ownerId = SeedAuthorizedUser(db, "owner", "sales-order");
        var recipientId = SeedAuthorizedUser(db, "recipient", "sales-order");
        var service = BuildService(db);
        var sharing = BuildSharing(db);
        var execution = BuildExecution(db);

        var created = await service.CreateAsync(ownerId, SaveDto("报表", Definition(DefinitionA)));
        await service.PublishAsync(ownerId, created.Id, created.Version);
        await sharing.GrantAsync(ownerId, created.Id,
            new ReportConfigurationGrantRequestDto { RecipientUserId = recipientId, RevisionVersion = 1 });

        foreach (var menu in db.SysMenus.Where(m => m.MenuCode == "sales-order").ToList())
            menu.IsDeleted = true;
        db.SaveChanges();

        var ex = await Assert.ThrowsAsync<BusinessException>(() => execution.PreviewAsync(recipientId,
            new ReportConfigurationPreviewRequest { ConfigurationId = created.Id }));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    [Fact]
    public async Task 共享预览_选中指标_按被授权人作用域计算并剥离隐藏依赖()
    {
        using var db = TestDbFactory.Create();
        var ownerId = SeedAuthorizedUser(db, "owner", "sales-order");
        var recipientId = SeedAuthorizedUser(db, "recipient", "sales-order");
        var service = BuildService(db);
        var sharing = BuildSharing(db);
        var execution = BuildExecution(db);

        var def = Definition("orderNo");
        def.Aggregates = new List<ReportConfigurationAggregate>
        {
            new() { Function = ReportConfigurationConstants.AggregateSum, FieldKey = "totalAmount" },
        };

        var created = await service.CreateAsync(ownerId, SaveDto("指标报表", def));
        await service.PublishAsync(ownerId, created.Id, created.Version);
        await sharing.GrantAsync(ownerId, created.Id,
            new ReportConfigurationGrantRequestDto { RecipientUserId = recipientId, RevisionVersion = 1 });

        var preview = await execution.PreviewAsync(recipientId,
            new ReportConfigurationPreviewRequest { ConfigurationId = created.Id });

        Assert.True(preview.IsPinnedRevision);
        var metric = Assert.Single(preview.Metrics);
        Assert.Equal("totalAmount", metric.Key);
        Assert.Equal(ReportConfigurationConstants.AggregateSum, metric.Function);
        Assert.DoesNotContain(preview.Columns, c => c.Key == "totalAmount");
        Assert.All(preview.Rows, r => Assert.False(r.ContainsKey("totalAmount")));
    }

    // ==================== 4. 精确 EF 关系型模型映射（离线建模，不连接 SQL Server） ====================

    [Fact]
    public void 模型配置契约_授权表表名并发令牌索引与级联删除()
    {
        var options = new DbContextOptionsBuilder<ErpDbContext>()
            .UseSqlServer("Server=localhost;Database=__offline_model__;Trusted_Connection=True;Encrypt=False;")
            .Options;
        using var db = new ErpDbContext(options);

        var grantType = db.Model.FindEntityType(typeof(ReportConfigurationGrant));
        Assert.NotNull(grantType);
        Assert.Equal("ReportConfigurationGrants", grantType!.GetTableName());
        Assert.Equal("db_owner", grantType.GetSchema());
        Assert.True(grantType.FindProperty(nameof(ReportConfigurationGrant.Version))!.IsConcurrencyToken);

        Assert.Contains(grantType.GetIndexes(),
            i => i.IsUnique
                && i.Properties.Select(p => p.Name).SequenceEqual(new[] { "RecipientUserId", "ReportConfigurationId" }));

        var fk = Assert.Single(grantType.GetForeignKeys());
        Assert.Equal(typeof(ReportConfiguration), fk.PrincipalEntityType.ClrType);
        Assert.Equal(DeleteBehavior.Cascade, fk.DeleteBehavior);
    }
}
