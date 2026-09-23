using ERP.Application.Common;
using ERP.Application.DTOs;

namespace ERP.Application.Interfaces;

/// <summary>
/// 认证服务接口
/// </summary>
public interface IAuthService
{
    /// <summary>用户登录</summary>
    Task<LoginResponse> LoginAsync(LoginRequest request, string ipAddress);

    /// <summary>获取当前用户信息（含角色、权限、菜单）</summary>
    Task<UserProfileResponse> GetProfileAsync(long userId);

    /// <summary>修改密码</summary>
    Task ChangePasswordAsync(long userId, ChangePasswordRequest request);

    /// <summary>校验密码</summary>
    bool VerifyPassword(string password, string salt, string passwordHash);
}
