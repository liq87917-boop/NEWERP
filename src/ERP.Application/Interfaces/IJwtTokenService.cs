using ERP.Application.Common;

namespace ERP.Application.Interfaces;

/// <summary>
/// JWT 令牌服务接口
/// </summary>
public interface IJwtTokenService
{
    /// <summary>根据用户上下文生成访问令牌</summary>
    /// <param name="user">当前用户上下文</param>
    /// <param name="expiresInSeconds">输出：令牌有效期（秒）</param>
    string GenerateToken(CurrentUser user, out int expiresInSeconds);
}
