namespace ERP.Application.Common;

/// <summary>
/// 业务异常：用于在服务层抛出可预期的业务错误，由全局异常中间件统一处理
/// </summary>
public class BusinessException : Exception
{
    /// <summary>业务错误码</summary>
    public int Code { get; }

    public BusinessException(string message, int code = ErrorCodes.General)
        : base(message)
    {
        Code = code;
    }

    /// <summary>数据不存在</summary>
    public static BusinessException NotFound(string message = "数据不存在")
        => new(message, ErrorCodes.NotFound);

    /// <summary>参数校验失败</summary>
    public static BusinessException InvalidParameter(string message)
        => new(message, ErrorCodes.InvalidParameter);

    /// <summary>数据重复</summary>
    public static BusinessException Duplicate(string message)
        => new(message, ErrorCodes.Duplicate);

    /// <summary>业务规则冲突</summary>
    public static BusinessException RuleConflict(string message)
        => new(message, ErrorCodes.RuleConflict);
}
