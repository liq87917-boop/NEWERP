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
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge
            && context.Request.Path.StartsWithSegments("/api/report-configurations"))
        {
            // 仅对通用报表配置模块映射 413：请求体超限在进入模型绑定前被拒绝，返回受控信封，绝不回声载荷 / SQL / 密钥。
            Log.Warning("报表配置请求体过大已拒绝：路径：{Path}", context.Request.Path);
            await WritePayloadTooLargeAsync(context);
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

    /// <summary>
    /// 通用报表配置请求体超限的受控 413 响应：HTTP 413 + { code, message, data } 信封，
    /// 文案固定、有界，绝不回声请求载荷 / SQL / 密钥。
    /// </summary>
    private static async Task WritePayloadTooLargeAsync(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsJsonAsync(new ApiResponse<object>(
            ERP.Api.Controllers.ReportConfigurationsController.ErrorCodeInputTooLarge,
            ERP.Api.Controllers.ReportConfigurationsController.InputTooLargeMessage,
            null));
    }
}
