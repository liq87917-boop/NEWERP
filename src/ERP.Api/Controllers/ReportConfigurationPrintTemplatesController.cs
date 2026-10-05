using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Infrastructure.Export;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Security.Claims;

namespace ERP.Api.Controllers;

/// <summary>
/// 受控打印模板控制器（ERP-312 / ERP-313 Stage 2）：只读枚举已保存打印模板为「兼容 / 显式不支持」的封闭族目录，
/// 执行只读绑定校验，并把现有通用报表定义（Id + 可选发布修订）与已保存模板绑定为有界渲染结果（standard / grid）。
/// <para>身份与权限：仅使用服务端认证的 <c>ClaimTypes.NameIdentifier</c>（客户端不得提交）；绝不回写模板、绝不授予权限。</para>
/// </summary>
[ApiController]
[Route("api/report-configuration-print-templates")]
[Authorize]
public class ReportConfigurationPrintTemplatesController : ControllerBase
{
    private readonly IReportConfigurationPrintTemplateCatalog _catalog;
    private readonly IReportConfigurationPrintRenderService _render;
    private readonly IReportConfigurationExecutionBudget _budget;
    private readonly ILogger<ReportConfigurationPrintTemplatesController>? _logger;

    public ReportConfigurationPrintTemplatesController(
        IReportConfigurationPrintTemplateCatalog catalog,
        IReportConfigurationPrintRenderService render,
        IReportConfigurationExecutionBudget? budget = null,
        ILogger<ReportConfigurationPrintTemplatesController>? logger = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _render = render ?? throw new ArgumentNullException(nameof(render));
        _budget = budget ?? new ReportConfigurationExecutionBudget();
        _logger = logger;
    }

    private long CurrentUserId()
    {
        var value = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (long.TryParse(value, out var id) && id > 0)
            return id;
        throw new BusinessException("无法获取当前用户信息", ErrorCodes.Unauthorized);
    }

    private async Task<T> WithRequestCancellationAsync<T>(Func<CancellationToken, Task<T>> work)
    {
        var token = HttpContext?.RequestAborted ?? CancellationToken.None;
        try
        {
            return await work(token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw new BusinessException("打印渲染操作已取消", ReportConfigurationExecutionLimits.ErrorCodeCancelled);
        }
    }

    /// <summary>只读枚举当前账号可访问的打印模板族目录（兼容 / 显式不支持）。</summary>
    [HttpGet("catalog")]
    public async Task<IActionResult> Catalog()
    {
        var result = await _catalog.GetCatalogAsync(CurrentUserId(), HttpContext.RequestAborted);
        return Ok(ApiResponse<ReportPrintTemplateCatalogDto>.Success(result));
    }

    /// <summary>只读绑定校验：把已保存模板绑定到受控数据集，返回有序受控列。</summary>
    [HttpPost("bind")]
    public async Task<IActionResult> Bind([FromBody] ReportPrintTemplateBindingRequest request)
    {
        var result = await _catalog.BindAsync(request, CurrentUserId(), HttpContext.RequestAborted);
        return Ok(ApiResponse<ReportPrintTemplateBindingDto>.Success(result));
    }

    /// <summary>只读打印渲染预览：现有报表定义 + 已保存模板 → 有界渲染结果（standard / grid）。</summary>
    [HttpPost("render/preview")]
    public async Task<IActionResult> RenderPreview([FromBody] ReportConfigurationPrintRenderRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var result = await WithRequestCancellationAsync(ct => _render.PreviewAsync(CurrentUserId(), request, ct));
        return Ok(ApiResponse<ReportConfigurationPrintRenderDto>.Success(result));
    }

    /// <summary>只读打印渲染 PDF 下载：复用同一执行租约渲染（绝不二次获取租约），字体缺失 / 渲染失败显式失败。</summary>
    [HttpPost("render/export-pdf")]
    public async Task<IActionResult> RenderExportPdf([FromBody] ReportConfigurationPrintRenderRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var userId = CurrentUserId();
        var stopwatch = Stopwatch.StartNew();
        try
        {
            return await _budget.ExecuteAsync<IActionResult>(userId, HttpContext.RequestAborted, async lease =>
            {
                var render = await _render.BuildAsync(userId, request, lease);
                var bytes = ReportConfigurationPrintPdfExporter.Export(render, lease.Token);

                if (bytes.Length > ReportConfigurationExecutionLimits.MaxGeneratedFileBytes)
                    throw new BusinessException(
                        "打印文件过大，已拒绝下载（关联ID：" + lease.CorrelationId + "）",
                        ReportConfigurationExecutionLimits.ErrorCodeResultTooLarge);

                LogRender(userId, request, render, ReportConfigurationExecutionOutcomes.Success, stopwatch.ElapsedMilliseconds);
                return File(bytes, "application/pdf", $"PrintTemplate_{DateTime.Now:yyyyMMddHHmmss}.pdf");
            });
        }
        catch (BusinessException ex)
        {
            LogRender(userId, request, null, ReportConfigurationExecutionOutcomes.For(ex.Code), stopwatch.ElapsedMilliseconds);
            throw;
        }
        catch (OperationCanceledException)
        {
            throw new BusinessException("打印导出已取消", ReportConfigurationExecutionLimits.ErrorCodeCancelled);
        }
        catch (Exception)
        {
            LogRender(userId, request, null, ReportConfigurationExecutionOutcomes.Error, stopwatch.ElapsedMilliseconds);
            throw;
        }
    }

    private void LogRender(
        long userId,
        ReportConfigurationPrintRenderRequest request,
        ReportConfigurationPrintRenderDto? render,
        string outcome,
        long durationMs)
    {
        if (_logger is null)
            return;

        try
        {
            _logger.LogInformation(
                "ReportConfigurationPrintRender {@Execution}",
                new
                {
                    UserId = userId,
                    ConfigurationId = request.ConfigurationId,
                    TemplateId = request.TemplateId,
                    PinnedRevision = render?.IsPinnedRevision == true ? request.RevisionVersion : null,
                    DatasetKey = render?.DatasetKey,
                    Layout = render?.Layout,
                    Outcome = outcome,
                    DurationMs = durationMs,
                    RowCount = render?.Rows?.Count ?? 0,
                    GridBlockCount = render?.GridBlocks?.Count ?? 0,
                });
        }
        catch
        {
            // 日志失败不影响主流程
        }
    }
}
