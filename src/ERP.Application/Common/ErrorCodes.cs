namespace ERP.Application.Common;

/// <summary>
/// 统一错误码规划
/// </summary>
public static class ErrorCodes
{
    /// <summary>成功</summary>
    public const int Success = 0;

    /// <summary>通用业务错误</summary>
    public const int General = 1000;

    /// <summary>参数校验失败</summary>
    public const int InvalidParameter = 1001;

    /// <summary>数据不存在</summary>
    public const int NotFound = 1002;

    /// <summary>数据已存在/重复</summary>
    public const int Duplicate = 1003;

    /// <summary>业务规则冲突</summary>
    public const int RuleConflict = 1004;

    /// <summary>未认证</summary>
    public const int Unauthorized = 2000;

    /// <summary>用户名或密码错误</summary>
    public const int LoginFailed = 2001;

    /// <summary>权限不足</summary>
    public const int Forbidden = 2002;

    /// <summary>令牌过期</summary>
    public const int TokenExpired = 2003;

    /// <summary>账号被禁用</summary>
    public const int AccountDisabled = 2004;

    /// <summary>服务器内部错误</summary>
    public const int InternalError = 5000;
}
