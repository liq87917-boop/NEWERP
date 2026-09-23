using ERP.Application.Common;
using Serilog;

namespace ERP.Api.Middleware;

/// <summary>
/// 全局异常处理中间件：统一捕获异常并返回 { code, message, data } 格式
/// </summary>
public class ExceptionHandlingMiddleware
{
    private readonly RequestDelegate _next;

    public ExceptionHandlingMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (BusinessException ex)
        {
            Log.Warning("业务异常：{Message}，错误码：{Code}，路径：{Path}", ex.Message, ex.Code, context.Request.Path);
            await WriteErrorAsync(context, ex.Code, ex.Message);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "系统异常：{Message}，路径：{Path}", ex.Message, context.Request.Path);
            await WriteErrorAsync(context, ErrorCodes.InternalError, "服务器内部错误，请联系管理员");
        }
    }

    private static async Task WriteErrorAsync(HttpContext context, int code, string message)
    {
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsJsonAsync(new ApiResponse<object>(code, message, null));
    }
}
