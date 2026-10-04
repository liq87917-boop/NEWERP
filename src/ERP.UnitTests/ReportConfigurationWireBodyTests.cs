using ERP.Api.Controllers;
using ERP.Api.Middleware;
using ERP.Application.Common;
using ERP.Application.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using System.Reflection;
using System.Text.Json;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-285：通用报表配置写请求的传输层体积护栏回归测试。覆盖 512 KiB 传输层上限常量与控制器级
/// 属性、授权阶段收紧 IHttpMaxRequestBodySizeFeature、超限 413 的受控模块内映射、全局异常中间件
/// 的报表路径 413 映射与无关路径保留，以及不回声载荷 / SQL / 密钥。
/// </summary>
public class ReportConfigurationWireBodyTests
{
    private const string FakeSqlPayload = "SELECT secret FROM users WHERE id=1";

    // ==================== 常量与控制器声明 ====================

    [Fact]
    public void 请求体上限_512KiB传输上限高于64KiB归一化定义上限()
    {
        Assert.Equal(512L * 1024, ReportConfigurationsController.MaxWireBodyBytes);
        Assert.True(ReportConfigurationsController.MaxWireBodyBytes >= ReportConfigurationRules.MaxSerializedBytes * 8);
        Assert.Equal(1009, ReportConfigurationsController.ErrorCodeInputTooLarge);
        Assert.Contains("工作台输入过大", ReportConfigurationsController.InputTooLargeMessage);
    }

    [Fact]
    public void 请求体上限_控制器级属性覆盖全部POSTPUT动作族()
    {
        var type = typeof(ReportConfigurationsController);

        Assert.NotNull(type.GetCustomAttribute<ReportRequestBodyLimitAttribute>());

        var writeActions = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttribute<HttpPostAttribute>() is not null
                     || m.GetCustomAttribute<HttpPutAttribute>() is not null)
            .Select(m => m.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(writeActions);
        foreach (var expected in new[]
        {
            nameof(ReportConfigurationsController.Create),
            nameof(ReportConfigurationsController.Update),
            nameof(ReportConfigurationsController.Rename),
            nameof(ReportConfigurationsController.Grant),
            nameof(ReportConfigurationsController.Preview),
            nameof(ReportConfigurationsController.Export),
            nameof(ReportConfigurationsController.ExportPdf),
            nameof(ReportConfigurationsController.ExportDefinition),
            nameof(ReportConfigurationsController.ImportDefinition),
        })
        {
            Assert.Contains(expected, writeActions);
        }
    }

    // ==================== 授权阶段收紧请求体积上限 ====================

    [Fact]
    public void 请求体上限_授权阶段收紧IHttpMaxRequestBodySizeFeature()
    {
        var httpContext = new DefaultHttpContext();
        var feature = new TestMaxRequestBodySizeFeature();
        httpContext.Features.Set<IHttpMaxRequestBodySizeFeature>(feature);

        new ReportRequestBodyLimitAttribute().OnAuthorization(
            new AuthorizationFilterContext(NewActionContext(httpContext), new List<IFilterMetadata>()));

        Assert.Equal(ReportConfigurationsController.MaxWireBodyBytes, feature.MaxRequestBodySize);
    }

    [Fact]
    public void 请求体上限_只读请求体限制特征保持不变()
    {
        var httpContext = new DefaultHttpContext();
        var feature = new TestMaxRequestBodySizeFeature { IsReadOnly = true, MaxRequestBodySize = 30L * 1024 * 1024 };
        httpContext.Features.Set<IHttpMaxRequestBodySizeFeature>(feature);

        new ReportRequestBodyLimitAttribute().OnAuthorization(
            new AuthorizationFilterContext(NewActionContext(httpContext), new List<IFilterMetadata>()));

        Assert.Equal(30L * 1024 * 1024, feature.MaxRequestBodySize);
    }

    // ==================== 超限 413 的受控模块内映射 ====================

    [Fact]
    public void 请求体上限_超限413映射为受控模块内响应且不回声载荷()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Response.Body = new MemoryStream();
        var exceptionContext = new ExceptionContext(NewActionContext(httpContext), new List<IFilterMetadata>())
        {
            Exception = new BadHttpRequestException(FakeSqlPayload + " request body too large.", StatusCodes.Status413PayloadTooLarge)
        };

        new ReportRequestBodyLimitAttribute().OnException(exceptionContext);

        Assert.True(exceptionContext.ExceptionHandled);
        var json = Assert.IsType<JsonResult>(exceptionContext.Result);
        Assert.Equal(StatusCodes.Status413PayloadTooLarge, json.StatusCode);

        var envelope = Assert.IsType<ApiResponse<object>>(json.Value);
        Assert.Equal(ReportConfigurationsController.ErrorCodeInputTooLarge, envelope.Code);
        Assert.Contains("工作台输入过大", envelope.Message);
        Assert.DoesNotContain(FakeSqlPayload, envelope.Message);
        Assert.DoesNotContain("request body too large", envelope.Message);
    }

    [Fact]
    public void 请求体上限_非413异常原样放行()
    {
        var httpContext = new DefaultHttpContext();
        var exceptionContext = new ExceptionContext(NewActionContext(httpContext), new List<IFilterMetadata>())
        {
            Exception = new InvalidOperationException("boom")
        };

        new ReportRequestBodyLimitAttribute().OnException(exceptionContext);

        Assert.False(exceptionContext.ExceptionHandled);
        Assert.Null(exceptionContext.Result);
    }

    // ==================== 全局异常中间件：模块作用域映射 ====================

    [Fact]
    public async Task 异常中间件_报表路径413_返回受控413且不回声载荷()
    {
        var middleware = new ExceptionHandlingMiddleware(ThrowReport413);
        var context = NewResponseContext("/api/report-configurations");

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status413PayloadTooLarge, context.Response.StatusCode);
        var body = await ReadBodyAsync(context.Response);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(ReportConfigurationsController.ErrorCodeInputTooLarge, doc.RootElement.GetProperty("code").GetInt32());
        Assert.Contains("工作台输入过大", doc.RootElement.GetProperty("message").GetString());
        Assert.DoesNotContain(FakeSqlPayload, body);
        Assert.DoesNotContain("request body too large", body);
    }

    [Fact]
    public async Task 异常中间件_非报表路径413_保留原有行为()
    {
        var middleware = new ExceptionHandlingMiddleware(_ =>
        {
            throw new BadHttpRequestException("request body too large.", StatusCodes.Status413PayloadTooLarge);
        });
        var context = NewResponseContext("/api/other");

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        var body = await ReadBodyAsync(context.Response);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(ErrorCodes.InternalError, doc.RootElement.GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task 异常中间件_报表路径非413异常_保留原有行为()
    {
        var middleware = new ExceptionHandlingMiddleware(_ =>
        {
            throw new InvalidOperationException("boom");
        });
        var context = NewResponseContext("/api/report-configurations");

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        var body = await ReadBodyAsync(context.Response);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(ErrorCodes.InternalError, doc.RootElement.GetProperty("code").GetInt32());
    }

    // ==================== 测试脚手架 ====================

    private static Task ThrowReport413(HttpContext _)
        => throw new BadHttpRequestException(FakeSqlPayload + " request body too large.", StatusCodes.Status413PayloadTooLarge);

    private static DefaultHttpContext NewResponseContext(string path)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static async Task<string> ReadBodyAsync(HttpResponse response)
    {
        response.Body.Position = 0;
        using var reader = new StreamReader(response.Body, leaveOpen: true);
        return await reader.ReadToEndAsync();
    }

    private static ActionContext NewActionContext(HttpContext httpContext)
        => new(httpContext, new RouteData(), new ActionDescriptor());

    private sealed class TestMaxRequestBodySizeFeature : IHttpMaxRequestBodySizeFeature
    {
        public bool IsReadOnly { get; set; }
        public long? MaxRequestBodySize { get; set; }
    }
}
