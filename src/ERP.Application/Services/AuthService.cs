using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ERP.Application.Services;

/// <summary>
/// 认证服务实现：处理登录、用户信息、密码修改
/// </summary>
public partial class AuthService : IAuthService
{
    private readonly IErpDbContext _db;
    private readonly IJwtTokenService _jwtTokenService;

    public AuthService(IErpDbContext db, IJwtTokenService jwtTokenService)
    {
        _db = db;
        _jwtTokenService = jwtTokenService;
    }

    /// <summary>用户登录</summary>
    public async Task<LoginResponse> LoginAsync(LoginRequest request, string ipAddress)
    {
        var user = await _db.SysUsers
            .FirstOrDefaultAsync(u => u.UserName == request.UserName && !u.IsDeleted);

        if (user is null)
            throw new BusinessException("用户名或密码错误", ErrorCodes.LoginFailed);

        if (!VerifyPassword(request.Password, user.PasswordSalt, user.PasswordHash))
            throw new BusinessException("用户名或密码错误", ErrorCodes.LoginFailed);

        if (user.Status != UserStatus.Enabled)
            throw new BusinessException("账号已被禁用，请联系管理员", ErrorCodes.AccountDisabled);

        user.LastLoginTime = DateTime.Now;
        user.LastLoginIp = ipAddress;
        await _db.SaveChangesAsync();

        var context = await BuildCurrentUserAsync(user);
        var token = _jwtTokenService.GenerateToken(context, out var expiresIn);

        _db.SysOperationLogs.Add(new SysOperationLog
        {
            UserId = user.Id,
            UserName = user.UserName,
            Module = "认证",
            Action = "登录",
            Method = "POST",
            Path = "/api/auth/login",
            IpAddress = ipAddress,
            StatusCode = 200,
            DurationMs = 0,
            CreatedAt = DateTime.Now
        });
        await _db.SaveChangesAsync();

        return new LoginResponse
        {
            Token = token,
            ExpiresIn = expiresIn,
            UserId = user.Id,
            UserName = user.UserName,
            DisplayName = user.DisplayName,
            MustChangePassword = user.MustChangePassword
        };
    }

    /// <summary>获取当前用户信息</summary>
    public async Task<UserProfileResponse> GetProfileAsync(long userId)
    {
        var user = await _db.SysUsers.FirstOrDefaultAsync(u => u.Id == userId && !u.IsDeleted)
            ?? throw BusinessException.NotFound("用户不存在");

        var context = await BuildCurrentUserAsync(user);
        var menus = await BuildMenuTreeAsync(user.Id);

        return new UserProfileResponse
        {
            Id = user.Id,
            UserName = user.UserName,
            DisplayName = user.DisplayName,
            Email = user.Email,
            Avatar = user.Avatar,
            Roles = context.Roles,
            Permissions = context.Permissions,
            Menus = menus
        };
    }

    /// <summary>修改密码</summary>
    public async Task ChangePasswordAsync(long userId, ChangePasswordRequest request)
    {
        var user = await _db.SysUsers.FirstOrDefaultAsync(u => u.Id == userId && !u.IsDeleted)
            ?? throw BusinessException.NotFound("用户不存在");

        if (!VerifyPassword(request.OldPassword, user.PasswordSalt, user.PasswordHash))
            throw new BusinessException("原密码不正确", ErrorCodes.InvalidParameter);

        user.PasswordSalt = PasswordHasher.GenerateSalt();
        user.PasswordHash = PasswordHasher.HashPassword(request.NewPassword, user.PasswordSalt);
        user.MustChangePassword = false;
        user.UpdatedAt = DateTime.Now;
        await _db.SaveChangesAsync();
    }

    /// <summary>校验密码</summary>
    public bool VerifyPassword(string password, string salt, string passwordHash)
        => PasswordHasher.VerifyPassword(password, salt, passwordHash);
}
