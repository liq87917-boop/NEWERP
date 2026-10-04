using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using ERP.Infrastructure.Reports;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Update;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-284 Stage 1：报表配置生命周期并发冲突回归测试（离线）。
/// 用「可控抛出的内存 DbContext」在 SaveChangesAsync 处注入 <see cref="DbUpdateConcurrencyException"/>，
/// 验证私有报表更新 / 重命名 / 删除 / 发布 / 恢复以及既有授权 pin / 撤销写入都被统一映射为
/// <see cref="BusinessException.RuleConflict"/>，绝不自动重试、绝不覆盖赢家状态，
/// 且失败的发布 / 恢复不落任何幽灵修订、失败的授权 / 撤销不改变赢家行。
/// </summary>
public class ReportConfigurationConcurrencyTests
{
    // ==================== 0. 脚手架 ====================

    private static (ConcurrencyThrowingDbContext Db, ErpDbContext Verify) CreateSharedContexts()
    {
        var name = Guid.NewGuid().ToString();
        var options = new DbContextOptionsBuilder<ErpDbContext>()
            .UseInMemoryDatabase(name)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return (new ConcurrencyThrowingDbContext(options), new ErpDbContext(options));
    }

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

    private sealed class ConcurrencyThrowingDbContext : ErpDbContext
    {
        public bool ThrowConcurrency { get; set; }
        public bool ThrowGenericDbUpdate { get; set; }

        public ConcurrencyThrowingDbContext(DbContextOptions<ErpDbContext> options) : base(options)
        {
        }

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            if (ThrowConcurrency)
                throw new DbUpdateConcurrencyException("simulated concurrency conflict", new List<IUpdateEntry>());
            if (ThrowGenericDbUpdate)
                throw new DbUpdateException("simulated generic db fault", new List<IUpdateEntry>());
            return base.SaveChangesAsync(cancellationToken);
        }
    }

    // ==================== 1. 私有报表生命周期写冲突 → RuleConflict ====================

    [Fact]
    public async Task UpdateAsync_数据库并发冲突_映射为规则冲突且不覆盖赢家()
    {
        var ctx = CreateSharedContexts();
        using (ctx.Db)
        using (ctx.Verify)
        {
            var db = ctx.Db;
            var verify = ctx.Verify;
            var ownerId = SeedAuthorizedUser(db, "owner", "sales-order");
            var service = BuildService(db);
            var created = await service.CreateAsync(ownerId, SaveDto("原始名称"));

            db.ThrowConcurrency = true;
            var ex = await Assert.ThrowsAsync<BusinessException>(
                () => service.UpdateAsync(ownerId, created.Id, created.Version, SaveDto("冲突更新")));

            Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
            Assert.Contains("请刷新后重试", ex.Message);

            var persisted = verify.ReportConfigurations.Single(c => c.Id == created.Id);
            Assert.Equal("原始名称", persisted.Name);
            Assert.Equal(created.Version, persisted.Version);
        }
    }

    [Fact]
    public async Task RenameAsync_数据库并发冲突_映射为规则冲突且不覆盖赢家()
    {
        var ctx = CreateSharedContexts();
        using (ctx.Db)
        using (ctx.Verify)
        {
            var db = ctx.Db;
            var verify = ctx.Verify;
            var ownerId = SeedAuthorizedUser(db, "owner", "sales-order");
            var service = BuildService(db);
            var created = await service.CreateAsync(ownerId, SaveDto("原始名称"));

            db.ThrowConcurrency = true;
            var ex = await Assert.ThrowsAsync<BusinessException>(
                () => service.RenameAsync(ownerId, created.Id, created.Version, "冲突重命名"));

            Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
            Assert.Contains("请刷新后重试", ex.Message);

            var persisted = verify.ReportConfigurations.Single(c => c.Id == created.Id);
            Assert.Equal("原始名称", persisted.Name);
            Assert.Equal(created.Version, persisted.Version);
        }
    }

    [Fact]
    public async Task DeleteAsync_数据库并发冲突_映射为规则冲突且不软删除()
    {
        var ctx = CreateSharedContexts();
        using (ctx.Db)
        using (ctx.Verify)
        {
            var db = ctx.Db;
            var verify = ctx.Verify;
            var ownerId = SeedAuthorizedUser(db, "owner", "sales-order");
            var service = BuildService(db);
            var created = await service.CreateAsync(ownerId, SaveDto("要删除的报表"));

            db.ThrowConcurrency = true;
            var ex = await Assert.ThrowsAsync<BusinessException>(
                () => service.DeleteAsync(ownerId, created.Id, created.Version));

            Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
            Assert.Contains("请刷新后重试", ex.Message);

            var persisted = verify.ReportConfigurations.Single(c => c.Id == created.Id);
            Assert.False(persisted.IsDeleted);
            Assert.Equal(created.Version, persisted.Version);
        }
    }

    [Fact]
    public async Task PublishAsync_数据库并发冲突_映射为规则冲突且无幽灵修订()
    {
        var ctx = CreateSharedContexts();
        using (ctx.Db)
        using (ctx.Verify)
        {
            var db = ctx.Db;
            var verify = ctx.Verify;
            var ownerId = SeedAuthorizedUser(db, "owner", "sales-order");
            var service = BuildService(db);
            var created = await service.CreateAsync(ownerId, SaveDto("发布并发"));

            db.ThrowConcurrency = true;
            var ex = await Assert.ThrowsAsync<BusinessException>(
                () => service.PublishAsync(ownerId, created.Id, created.Version));

            Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
            Assert.Contains("请刷新后重试", ex.Message);

            Assert.Empty(verify.ReportConfigurationRevisions.Where(r => r.ReportConfigurationId == created.Id));
            var persisted = verify.ReportConfigurations.Single(c => c.Id == created.Id);
            Assert.Equal(ReportConfigurationStatus.Draft, persisted.Status);
            Assert.Equal(0, persisted.CurrentPublishedVersion);
            Assert.Equal(created.Version, persisted.Version);
        }
    }

    [Fact]
    public async Task RestoreAsync_数据库并发冲突_映射为规则冲突且无幽灵修订()
    {
        var ctx = CreateSharedContexts();
        using (ctx.Db)
        using (ctx.Verify)
        {
            var db = ctx.Db;
            var verify = ctx.Verify;
            var ownerId = SeedAuthorizedUser(db, "owner", "sales-order");
            var service = BuildService(db);
            var created = await service.CreateAsync(ownerId, SaveDto("恢复并发"));
            var published = await service.PublishAsync(ownerId, created.Id, created.Version);

            db.ThrowConcurrency = true;
            var ex = await Assert.ThrowsAsync<BusinessException>(
                () => service.RestoreAsync(ownerId, created.Id, published.Version, 1));

            Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
            Assert.Contains("请刷新后重试", ex.Message);

            Assert.Single(verify.ReportConfigurationRevisions.Where(r => r.ReportConfigurationId == created.Id));
            var persisted = verify.ReportConfigurations.Single(c => c.Id == created.Id);
            Assert.Equal(1, persisted.CurrentPublishedVersion);
            Assert.Equal(published.Version, persisted.Version);
        }
    }

    // ==================== 2. 既有授权 pin / 撤销写冲突 → RuleConflict ====================

    [Fact]
    public async Task GrantAsync_变更固定修订pin_数据库并发冲突_映射为规则冲突且pin不变()
    {
        var ctx = CreateSharedContexts();
        using (ctx.Db)
        using (ctx.Verify)
        {
            var db = ctx.Db;
            var verify = ctx.Verify;
            var ownerId = SeedAuthorizedUser(db, "owner", "sales-order");
            var recipientId = SeedAuthorizedUser(db, "recipient", "sales-order");
            var service = BuildService(db);
            var sharing = BuildSharing(db);

            var created = await service.CreateAsync(ownerId, SaveDto("共享pin并发"));
            var published = await service.PublishAsync(ownerId, created.Id, created.Version);
            await service.PublishAsync(ownerId, created.Id, published.Version);
            var grant = await sharing.GrantAsync(ownerId, created.Id,
                new ReportConfigurationGrantRequestDto { RecipientUserId = recipientId, RevisionVersion = 1 });

            db.ThrowConcurrency = true;
            var ex = await Assert.ThrowsAsync<BusinessException>(
                () => sharing.GrantAsync(ownerId, created.Id,
                    new ReportConfigurationGrantRequestDto
                    {
                        RecipientUserId = recipientId,
                        RevisionVersion = 2,
                        ExpectedVersion = grant.Version,
                    }));

            Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
            Assert.Contains("请刷新后重试", ex.Message);

            var persisted = verify.ReportConfigurationGrants.Single(g => g.Id == grant.Id);
            Assert.Equal(1, persisted.RevisionVersion);
            Assert.Equal(grant.Version, persisted.Version);
        }
    }

    [Fact]
    public async Task RevokeAsync_数据库并发冲突_映射为规则冲突且授权仍有效()
    {
        var ctx = CreateSharedContexts();
        using (ctx.Db)
        using (ctx.Verify)
        {
            var db = ctx.Db;
            var verify = ctx.Verify;
            var ownerId = SeedAuthorizedUser(db, "owner", "sales-order");
            var recipientId = SeedAuthorizedUser(db, "recipient", "sales-order");
            var service = BuildService(db);
            var sharing = BuildSharing(db);

            var created = await service.CreateAsync(ownerId, SaveDto("撤销并发"));
            await service.PublishAsync(ownerId, created.Id, created.Version);
            var grant = await sharing.GrantAsync(ownerId, created.Id,
                new ReportConfigurationGrantRequestDto { RecipientUserId = recipientId, RevisionVersion = 1 });

            db.ThrowConcurrency = true;
            var ex = await Assert.ThrowsAsync<BusinessException>(
                () => sharing.RevokeAsync(ownerId, created.Id, recipientId, grant.Version));

            Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
            Assert.Contains("请刷新后重试", ex.Message);

            var persisted = verify.ReportConfigurationGrants.Single(g => g.Id == grant.Id);
            Assert.False(persisted.IsDeleted);
            Assert.Equal(grant.Version, persisted.Version);
        }
    }

    // ==================== 3. 其他数据库故障保留既有失败语义 ====================

    [Fact]
    public async Task UpdateAsync_非并发数据库异常_保留既有失败语义()
    {
        var ctx = CreateSharedContexts();
        using (ctx.Db)
        using (ctx.Verify)
        {
            var db = ctx.Db;
            var ownerId = SeedAuthorizedUser(db, "owner", "sales-order");
            var service = BuildService(db);
            var created = await service.CreateAsync(ownerId, SaveDto("原始名称"));

            db.ThrowGenericDbUpdate = true;
            await Assert.ThrowsAsync<DbUpdateException>(
                () => service.UpdateAsync(ownerId, created.Id, created.Version, SaveDto("冲突更新")));
        }
    }
}




