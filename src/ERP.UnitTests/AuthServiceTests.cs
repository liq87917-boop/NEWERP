using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// 认证服务单元测试：覆盖登录 / 用户信息 / 修改密码 / 密码校验共 11 个用例
/// </summary>
public class AuthServiceTests
{
    // ==================== LoginAsync ====================

    [Fact]
    public async Task LoginAsync_用户不存在_抛出LoginFailed()
    {
        using var db = TestDbFactory.Create();
        var svc = NewAuthService(db);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            svc.LoginAsync(new LoginRequest { UserName = "ghost", Password = "whatever" }, "127.0.0.1"));

        Assert.Equal(ErrorCodes.LoginFailed, ex.Code);
    }

    [Fact]
    public async Task LoginAsync_密码错误_抛出LoginFailed()
    {
        using var db = TestDbFactory.Create();
        SeedUser(db, "alice", "CorrectPass1", UserStatus.Enabled);
        var svc = NewAuthService(db);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            svc.LoginAsync(new LoginRequest { UserName = "alice", Password = "WrongPass1" }, "127.0.0.1"));

        Assert.Equal(ErrorCodes.LoginFailed, ex.Code);
    }

    [Fact]
    public async Task LoginAsync_账号被禁用_抛出AccountDisabled()
    {
        using var db = TestDbFactory.Create();
        SeedUser(db, "alice", "CorrectPass1", UserStatus.Disabled);
        var svc = NewAuthService(db);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            svc.LoginAsync(new LoginRequest { UserName = "alice", Password = "CorrectPass1" }, "127.0.0.1"));

        Assert.Equal(ErrorCodes.AccountDisabled, ex.Code);
        // 登录失败时不应更新 LastLogin
        Assert.Null(db.SysUsers.Single().LastLoginTime);
    }

    [Fact]
    public async Task LoginAsync_成功登录_更新LastLogin_写登录日志_返回Token和上下文()
    {
        using var db = TestDbFactory.Create();
        SeedUser(db, "alice", "Pass12345", UserStatus.Enabled, displayName: "张三");
        var jwt = new StubJwtTokenService();
        var svc = new AuthService(db, jwt);

        var resp = await svc.LoginAsync(new LoginRequest { UserName = "alice", Password = "Pass12345" }, "10.0.0.1");

        Assert.Equal("stub-token", resp.Token);
        Assert.Equal(7200, resp.ExpiresIn);
        Assert.Equal("alice", resp.UserName);
        Assert.Equal("张三", resp.DisplayName);

        // 用户 LastLogin 被更新
        var dbUser = db.SysUsers.Single();
        Assert.Equal("10.0.0.1", dbUser.LastLoginIp);
        Assert.NotNull(dbUser.LastLoginTime);

        // 登录日志被写入
        var log = db.SysOperationLogs.Single();
        Assert.Equal("登录", log.Action);
        Assert.Equal("/api/auth/login", log.Path);
        Assert.Equal("10.0.0.1", log.IpAddress);
        Assert.Equal((int)200, log.StatusCode);

        // JWT Stub 收到的上下文正确
        Assert.Equal("alice", jwt.LastUser!.UserName);
        Assert.Equal("张三", jwt.LastUser.DisplayName);
        Assert.Equal(dbUser.Id, jwt.LastUser.Id);
    }

    [Fact]
    public async Task LoginAsync_软删除用户_不计入登录集合()
    {
        using var db = TestDbFactory.Create();
        SeedUser(db, "alice", "Pass12345", UserStatus.Enabled, isDeleted: true);
        var svc = NewAuthService(db);

        await Assert.ThrowsAsync<BusinessException>(() =>
            svc.LoginAsync(new LoginRequest { UserName = "alice", Password = "Pass12345" }, "127.0.0.1"));
    }

    // ==================== GetProfileAsync ====================

    [Fact]
    public async Task GetProfileAsync_用户不存在_抛出NotFound()
    {
        using var db = TestDbFactory.Create();
        var svc = NewAuthService(db);

        await Assert.ThrowsAsync<BusinessException>(() => svc.GetProfileAsync(999));
    }

    [Fact]
    public async Task GetProfileAsync_含角色和权限_返回角色集合权限集合菜单树()
    {
        using var db = TestDbFactory.Create();
        var alice = SeedUser(db, "alice", "Pass12345", UserStatus.Enabled);
        var role = SeedRole(db, "admin", "管理员");

        // 角色分配
        db.SysUserRoles.Add(new SysUserRole { UserId = alice.Id, RoleId = role.Id });

        // 三层菜单：目录 / 菜单 / 按钮
        SeedMenu(db, parentId: 0, code: "sys",       name: "系统",        type: MenuType.Directory, sortOrder: 1);
        SeedMenu(db, parentId: 1, code: "user-mgmt", name: "用户管理",     type: MenuType.Menu,      sortOrder: 1);
        SeedMenu(db, parentId: 2, code: "user.add",  name: "新增用户",     type: MenuType.Button,    permCode: "user:add", sortOrder: 1);

        // 三个菜单都授权给该角色
        db.SysRoleMenus.AddRange(
            new SysRoleMenu { RoleId = role.Id, MenuId = 1 },
            new SysRoleMenu { RoleId = role.Id, MenuId = 2 },
            new SysRoleMenu { RoleId = role.Id, MenuId = 3 }
        );
        await db.SaveChangesAsync();

        var svc = NewAuthService(db);
        var profile = await svc.GetProfileAsync(alice.Id);

        // 角色
        Assert.Single(profile.Roles);
        Assert.Equal("admin", profile.Roles[0]);
        // 按钮权限
        Assert.Single(profile.Permissions);
        Assert.Equal("user:add", profile.Permissions[0]);
        // 菜单树：目录 -> 用户管理（不含按钮）
        Assert.Single(profile.Menus);
        Assert.Equal("系统", profile.Menus[0].MenuName);
        Assert.Single(profile.Menus[0].Children);
        Assert.Equal("用户管理", profile.Menus[0].Children[0].MenuName);
        // 用户管理下不应再嵌套按钮
        Assert.Empty(profile.Menus[0].Children[0].Children);
    }

    [Fact]
    public async Task GetProfileAsync_无角色_返回空角色空权限空菜单()
    {
        using var db = TestDbFactory.Create();
        var alice = SeedUser(db, "alice", "Pass12345", UserStatus.Enabled);
        var svc = NewAuthService(db);

        var profile = await svc.GetProfileAsync(alice.Id);

        Assert.Empty(profile.Roles);
        Assert.Empty(profile.Permissions);
        Assert.Empty(profile.Menus);
    }

    // ==================== ChangePasswordAsync ====================

    [Fact]
    public async Task ChangePasswordAsync_用户不存在_抛出NotFound()
    {
        using var db = TestDbFactory.Create();
        var svc = NewAuthService(db);

        await Assert.ThrowsAsync<BusinessException>(() =>
            svc.ChangePasswordAsync(999, new ChangePasswordRequest { OldPassword = "x", NewPassword = "NewPass123" }));
    }

    [Fact]
    public async Task ChangePasswordAsync_旧密码错_抛出InvalidParameter()
    {
        using var db = TestDbFactory.Create();
        var alice = SeedUser(db, "alice", "CorrectPass1", UserStatus.Enabled);
        var svc = NewAuthService(db);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            svc.ChangePasswordAsync(alice.Id, new ChangePasswordRequest { OldPassword = "WrongPass1", NewPassword = "NewPass123" }));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        // 密码未变
        var still = db.SysUsers.Single();
        Assert.True(svc.VerifyPassword("CorrectPass1", still.PasswordSalt, still.PasswordHash));
    }

    [Fact]
    public async Task ChangePasswordAsync_成功改密_盐和哈希都更新_MustChangePassword清零()
    {
        using var db = TestDbFactory.Create();
        var alice = SeedUser(db, "alice", "OldPass123", UserStatus.Enabled);
        alice.MustChangePassword = true;
        await db.SaveChangesAsync();
        var oldSalt = alice.PasswordSalt;
        var oldHash = alice.PasswordHash;

        var svc = NewAuthService(db);

        await svc.ChangePasswordAsync(alice.Id, new ChangePasswordRequest
        {
            OldPassword = "OldPass123",
            NewPassword = "NewPass123"
        });

        var dbUser = db.SysUsers.Single();
        Assert.NotEqual(oldSalt, dbUser.PasswordSalt);   // 新盐
        Assert.NotEqual(oldHash, dbUser.PasswordHash);   // 新哈希
        Assert.False(dbUser.MustChangePassword);          // 强制改密标记清零

        // 旧密码不再可用
        Assert.False(svc.VerifyPassword("OldPass123", dbUser.PasswordSalt, dbUser.PasswordHash));
        // 新密码可用
        Assert.True(svc.VerifyPassword("NewPass123", dbUser.PasswordSalt, dbUser.PasswordHash));
    }

    // ==================== 工具方法 ====================

    private static AuthService NewAuthService(ErpDbContext db) => new(db, new StubJwtTokenService());

    private static SysUser SeedUser(ErpDbContext db, string userName, string password, UserStatus status,
        string displayName = "DisplayName", bool isDeleted = false)
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
            MustChangePassword = false,
            IsDeleted = isDeleted
        };
        db.SysUsers.Add(u);
        db.SaveChanges();
        return u;
    }

    private static SysRole SeedRole(ErpDbContext db, string code, string name)
    {
        var r = new SysRole { RoleCode = code, RoleName = name, IsSystem = false };
        db.SysRoles.Add(r);
        db.SaveChanges();
        return r;
    }

    private static SysMenu SeedMenu(ErpDbContext db, long parentId, string code, string name, MenuType type,
        string permCode = "", int sortOrder = 0)
    {
        var m = new SysMenu
        {
            ParentId = parentId,
            MenuCode = code,
            MenuName = name,
            MenuType = type,
            PermissionCode = permCode,
            SortOrder = sortOrder,
            Path = "/" + code
        };
        db.SysMenus.Add(m);
        db.SaveChanges();
        return m;
    }

    /// <summary>JWT 服务桩：固定返回 stub-token / 7200s，并捕获被传入的 CurrentUser</summary>
    private class StubJwtTokenService : IJwtTokenService
    {
        public string LastToken { get; } = "stub-token";
        public int LastExpiresIn { get; } = 7200;
        public CurrentUser? LastUser { get; private set; }

        public string GenerateToken(CurrentUser user, out int expiresInSeconds)
        {
            LastUser = user;
            expiresInSeconds = LastExpiresIn;
            return LastToken;
        }
    }
}