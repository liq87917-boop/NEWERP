using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// SysUserController 单元测试：用户名唯一性、admin 保护、重置密码、Update/ToggleStatus/Delete、分页关键字搜索
/// </summary>
public class SysUserControllerTests
{
    // ==================== Create ====================

    [Fact]
    public async Task Create_用户名重复_抛Duplicate()
    {
        using var db = TestDbFactory.Create();
        SeedUser(db, "alice", "old", UserStatus.Enabled);
        var ctl = new SysUserController(db);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(new SysUserCreateRequest { UserName = "alice", Password = "NewPass123" }));

        Assert.Equal(ErrorCodes.Duplicate, ex.Code);
        // 数据库里仍然只有一个 alice
        Assert.Single(db.SysUsers);
    }

    [Fact]
    public async Task Create_正常创建_返回Ok_初始密码强制改密_且密码哈希非明文()
    {
        using var db = TestDbFactory.Create();
        var ctl = new SysUserController(db);

        var result = await ctl.Create(new SysUserCreateRequest
        {
            UserName = "alice",
            Password = "NewPass123",
            DisplayName = "Alice"
        });

        Assert.IsType<OkObjectResult>(result);
        var u = db.SysUsers.Single();
        Assert.Equal("alice", u.UserName);
        Assert.Equal("Alice", u.DisplayName);
        Assert.Equal(UserStatus.Enabled, u.Status);
        Assert.True(u.MustChangePassword);
        Assert.NotEqual("NewPass123", u.PasswordHash);    // 已哈希
        Assert.False(string.IsNullOrEmpty(u.PasswordSalt));
        // 新密码应当被验证可解
        Assert.True(PasswordHasher.VerifyPassword("NewPass123", u.PasswordSalt, u.PasswordHash));
    }

    [Fact]
    public async Task Create_含角色_创建后SysUserRoles有对应关联_重复RoleId去重()
    {
        using var db = TestDbFactory.Create();
        var r1 = SeedRole(db, "admin");
        var r2 = SeedRole(db, "sales");
        var ctl = new SysUserController(db);

        await ctl.Create(new SysUserCreateRequest
        {
            UserName = "alice",
            Password = "NewPass123",
            RoleIds = new List<long> { r1.Id, r2.Id, r1.Id }   // r1 重复
        });

        var u = db.SysUsers.Single();
        var links = db.SysUserRoles.Where(x => x.UserId == u.Id).ToList();
        Assert.Equal(2, links.Count);                          // r1 被去重
        Assert.Contains(links, x => x.RoleId == r1.Id);
        Assert.Contains(links, x => x.RoleId == r2.Id);
    }

    // ==================== Update ====================

    [Fact]
    public async Task Update_用户不存在_抛NotFound()
    {
        using var db = TestDbFactory.Create();
        var ctl = new SysUserController(db);

        await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Update(999, new SysUserUpdateRequest { DisplayName = "X" }));
    }

    [Fact]
    public async Task Update_正常更新_所有字段更新_角色重新分配_旧角色关联被清()
    {
        using var db = TestDbFactory.Create();
        var alice = SeedUser(db, "alice", "old", UserStatus.Enabled);
        var r1 = SeedRole(db, "admin");
        var r2 = SeedRole(db, "sales");
        db.SysUserRoles.Add(new SysUserRole { UserId = alice.Id, RoleId = r1.Id });
        await db.SaveChangesAsync();

        var ctl = new SysUserController(db);
        await ctl.Update(alice.Id, new SysUserUpdateRequest
        {
            DisplayName = "新Alice",
            Email = "alice@x.com",
            Phone = "13800138000",
            Status = UserStatus.Disabled,
            RoleIds = new List<long> { r2.Id }    // 移除 r1，只留 r2
        });

        var dbUser = db.SysUsers.Single();
        Assert.Equal("新Alice", dbUser.DisplayName);
        Assert.Equal("alice@x.com", dbUser.Email);
        Assert.Equal("13800138000", dbUser.Phone);
        Assert.Equal(UserStatus.Disabled, dbUser.Status);

        var links = db.SysUserRoles.Where(x => x.UserId == alice.Id).ToList();
        Assert.Single(links);
        Assert.Equal(r2.Id, links[0].RoleId);
    }

    // ==================== ToggleStatus ====================

    [Fact]
    public async Task ToggleStatus_admin账号_抛RuleConflict_状态不变()
    {
        using var db = TestDbFactory.Create();
        var admin = SeedUser(db, "admin", "x", UserStatus.Enabled);
        var ctl = new SysUserController(db);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.ToggleStatus(admin.Id));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Equal(UserStatus.Enabled, db.SysUsers.Single().Status);    // 未变
    }

    [Fact]
    public async Task ToggleStatus_普通用户_Enabled切换为Disabled_再次调用切回()
    {
        using var db = TestDbFactory.Create();
        var u = SeedUser(db, "alice", "x", UserStatus.Enabled);
        var ctl = new SysUserController(db);

        await ctl.ToggleStatus(u.Id);
        Assert.Equal(UserStatus.Disabled, db.SysUsers.Single().Status);

        await ctl.ToggleStatus(u.Id);
        Assert.Equal(UserStatus.Enabled, db.SysUsers.Single().Status);
    }

    // ==================== ResetPassword ====================

    [Fact]
    public async Task ResetPassword_用户不存在_抛NotFound()
    {
        using var db = TestDbFactory.Create();
        var ctl = new SysUserController(db);

        await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.ResetPassword(999, new ResetPasswordRequest { NewPassword = "NewPass123" }));
    }

    [Fact]
    public async Task ResetPassword_重置成功_新密码可用_旧密码失效_MustChangePassword置true()
    {
        using var db = TestDbFactory.Create();
        var u = SeedUser(db, "alice", "OldPass123", UserStatus.Enabled);
        u.MustChangePassword = false;
        await db.SaveChangesAsync();

        var ctl = new SysUserController(db);
        await ctl.ResetPassword(u.Id, new ResetPasswordRequest { NewPassword = "NewPass123" });

        var dbUser = db.SysUsers.Single();
        Assert.True(dbUser.MustChangePassword);
        Assert.True(PasswordHasher.VerifyPassword("NewPass123", dbUser.PasswordSalt, dbUser.PasswordHash));
        Assert.False(PasswordHasher.VerifyPassword("OldPass123", dbUser.PasswordSalt, dbUser.PasswordHash));
    }

    // ==================== Delete ====================

    [Fact]
    public async Task Delete_admin账号_抛RuleConflict_未软删()
    {
        using var db = TestDbFactory.Create();
        var admin = SeedUser(db, "admin", "x", UserStatus.Enabled);
        var ctl = new SysUserController(db);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Delete(admin.Id));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.False(db.SysUsers.Single().IsDeleted);
    }

    [Fact]
    public async Task Delete_普通用户_软删除_查询不到()
    {
        using var db = TestDbFactory.Create();
        var u = SeedUser(db, "alice", "x", UserStatus.Enabled);
        var ctl = new SysUserController(db);

        await ctl.Delete(u.Id);
        Assert.True(db.SysUsers.Single().IsDeleted);

        // GetById 看不到
        await Assert.ThrowsAsync<BusinessException>(() => ctl.GetById(u.Id));
    }

    // ==================== GetPaged 关键字 ====================

    [Fact]
    public async Task GetPaged_关键字_UserName或DisplayName匹配_分页正确()
    {
        using var db = TestDbFactory.Create();
        SeedUser(db, "alice", "x", UserStatus.Enabled, displayName: "艾丽斯");
        SeedUser(db, "bob", "x", UserStatus.Enabled, displayName: "鲍勃");
        SeedUser(db, "charlie", "x", UserStatus.Enabled, displayName: "查理");
        var ctl = new SysUserController(db);

        // 关键字 alice → 命中 alice
        var r1 = await ctl.GetPaged(new PageQuery { Page = 1, PageSize = 10, Keyword = "alice" });
        var ok1 = Assert.IsType<OkObjectResult>(r1);
        var resp1 = Assert.IsType<ApiResponse<PagedResult<SysUserView>>>(ok1.Value);
        Assert.Equal(1, resp1.Data!.Total);

        // 关键字 鲍 → 命中 bob（中文 DisplayName）
        var r2 = await ctl.GetPaged(new PageQuery { Page = 1, PageSize = 10, Keyword = "鲍" });
        var ok2 = Assert.IsType<OkObjectResult>(r2);
        var resp2 = Assert.IsType<ApiResponse<PagedResult<SysUserView>>>(ok2.Value);
        Assert.Equal(1, resp2.Data!.Total);
    }

    // ==================== 种子辅助 ====================

    private static SysUser SeedUser(ErpDbContext db, string userName, string password, UserStatus status,
        string displayName = "DisplayName")
    {
        var salt = PasswordHasher.GenerateSalt();
        var hash = PasswordHasher.HashPassword(password, salt);
        var u = new SysUser
        {
            UserName = userName,
            DisplayName = displayName,
            PasswordSalt = salt,
            PasswordHash = hash,
            Status = status,
            MustChangePassword = false
        };
        db.SysUsers.Add(u);
        db.SaveChanges();
        return u;
    }

    private static SysRole SeedRole(ErpDbContext db, string code)
    {
        var r = new SysRole { RoleCode = code, RoleName = code, IsSystem = false };
        db.SysRoles.Add(r);
        db.SaveChanges();
        return r;
    }
}