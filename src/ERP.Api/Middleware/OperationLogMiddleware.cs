using ERP.Domain.Entities;
using ERP.Infrastructure.Data;
using System.Diagnostics;

namespace ERP.Api.Middleware;

/// <summary>
/// 操作日志中间件：记录请求耗时与关键信息到系统日志表
/// </summary>
public class OperationLogMiddleware
{
    private readonly RequestDelegate _next;

    public OperationLogMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var sw = Stopwatch.StartNew();
        var originalBody = context.Response.Body;

        // 仅记录写操作（POST/PUT/DELETE）
        if (context.Request.Method is "GET" or "OPTIONS")
        {
            await _next(context);
            return;
        }

        try
        {
            await _next(context);
        }
        finally
        {
            sw.Stop();
            await TryLogAsync(context, sw.ElapsedMilliseconds);
        }
    }

    private static async Task TryLogAsync(HttpContext context, long durationMs)
    {
        try
        {
            var db = context.RequestServices.GetService<ErpDbContext>();
            if (db is null) return;

            // 跳过认证接口的重复记录（登录已在服务内记录）
            // 跳过业务单据接口：由 BillProcController 显式记录（含单据号，便于按单据追溯）
            if (context.Request.Path.StartsWithSegments("/api/auth") ||
                context.Request.Path.StartsWithSegments("/api/v2/bills"))
                return;

            var userIdClaim = context.User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
            long.TryParse(userIdClaim, out var userId);
            var userName = context.User?.Identity?.Name ?? "匿名";

            db.SysOperationLogs.Add(new SysOperationLog
            {
                UserId = userId == 0 ? null : userId,
                UserName = userName,
                Module = GetModule(context.Request.Path),
                Action = GetActionName(context.Request.Method, context.Request.Path),
                Method = context.Request.Method,
                Path = context.Request.Path,
                IpAddress = context.Connection.RemoteIpAddress?.ToString() ?? string.Empty,
                StatusCode = context.Response.StatusCode,
                DurationMs = durationMs,
                CreatedAt = DateTime.Now
            });
            await db.SaveChangesAsync();
        }
        catch
        {
            // 日志记录失败不影响主流程
        }
    }

    /// <summary>根据路径推断所属模块</summary>
    private static string GetModule(PathString path)
    {
        var segments = path.Value?.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments is null || segments.Length < 2) return "未知";
        return segments[1] switch
        {
            "auth" => "认证",
            "sys" => "系统设置",
            "base" => "基础资料",
            "inquiries" => "询价管理",
            "v2" => "业务单据",
            "sales-orders" => "订单管理",
            "purchase-orders" => "订单管理",
            "stock-in" => "物流管理",
            "stock-out" => "物流管理",
            "stocks" => "物流管理",
            "container" => "装柜管理",
            "finance" => "账务管理",
            "reports" => "报表管理",
            _ => "其他"
        };
    }

    /// <summary>根据 HTTP 方法与路径推断友好的操作动作名称</summary>
    private static string GetActionName(string method, PathString path)
    {
        var value = path.Value ?? string.Empty;
        if (method == "DELETE") return value.EndsWith("/batch-delete", StringComparison.OrdinalIgnoreCase) ? "批量删除" : "删除";
        if (method == "PUT") return "更新";
        if (method == "POST")
        {
            if (value.EndsWith("/import", StringComparison.OrdinalIgnoreCase)) return "导入";
            if (value.EndsWith("/export", StringComparison.OrdinalIgnoreCase)) return "导出";
            if (value.EndsWith("/upload", StringComparison.OrdinalIgnoreCase) ||
                value.EndsWith("/upload-batch", StringComparison.OrdinalIgnoreCase)) return "上传文件";
            if (value.EndsWith("/reset-password", StringComparison.OrdinalIgnoreCase)) return "重置密码";
            if (value.EndsWith("/toggle-status", StringComparison.OrdinalIgnoreCase)) return "启用/停用";
            if (value.EndsWith("/change-password", StringComparison.OrdinalIgnoreCase)) return "修改密码";
            if (value.EndsWith("/login", StringComparison.OrdinalIgnoreCase)) return "登录";
            return "新增";
        }
        return method;
    }
}
