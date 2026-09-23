namespace ERP.Application.Common;

/// <summary>
/// 统一 API 响应包装：{ code, message, data }
/// </summary>
public class ApiResponse<T>
{
    /// <summary>业务状态码（0=成功）</summary>
    public int Code { get; set; }

    /// <summary>提示信息</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>业务数据</summary>
    public T? Data { get; set; }

    public ApiResponse() { }

    public ApiResponse(int code, string message, T? data = default)
    {
        Code = code;
        Message = message;
        Data = data;
    }

    /// <summary>成功响应</summary>
    public static ApiResponse<T> Success(T? data, string message = "操作成功")
        => new(ErrorCodes.Success, message, data);

    /// <summary>失败响应</summary>
    public static ApiResponse<T> Fail(int code, string message)
        => new(code, message, default);

    /// <summary>失败响应（无数据泛型）</summary>
    public static ApiResponse<T> Fail(string message, int code = ErrorCodes.General)
        => new(code, message, default);
}
