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
/// 通用报表配置捆绑（ERP-307 Stage 2）控制器：把有界有序的既有定义 / 版本引用编排为一次多节预览，
/// 或一次性多工作表 Excel / 多节 PDF 下载。捆绑无状态、不新增实体 / 数据库结构、不执行任意 SQL / 联接。
/// <para>身份与权限：仅使用服务端认证的 <c>ClaimTypes.NameIdentifier</c> 作为请求人（客户端不得提交）；
/// 每个节都复用既有执行管线在单一执行租约内重新校验归属 / 共享授权 / 固定修订 / 数据集菜单授权与数据范围
/// （fail closed）；全部节都加载并校验通过后才产出结果，任何一节被拒绝 / 撤销 / 失效即整体失败，绝不产出部分数据或部分下载。</para>
/// </summary>
[ApiController]
[Route("api/report-configuration-bundles")]
[Authorize]
[ReportRequestBodyLimit]
public class ReportConfigurationBundlesController : ControllerBase
{
    private readonly IReportConfigurationBundleService _bundle;
    private readonly IReportConfigurationExecutionBudget _budget;
    private readonly ILogger<ReportConfigurationBundlesController>? _logger;

    public ReportConfigurationBundlesController(
        IReportConfigurationBundleService bundle,
        IReportConfigurationExecutionBudget? budget = null,
        ILogger<ReportConfigurationBundlesController>? logger = null)
    {
        _bundle = bundle ?? throw new ArgumentNullException(nameof(bundle));
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
            throw new BusinessException("报表捆绑操作已取消", ReportConfigurationExecutionLimits.ErrorCodeCancelled);
        }
    }

    /// <summary>预览捆绑：逐节复用既有有界、已授权预览；全部节通过后才返回，绝不返回部分节。</summary>
    [HttpPost("preview")]
    public async Task<IActionResult> Preview([FromBody] ReportConfigurationBundleRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var result = await WithRequestCancellationAsync(ct => _bundle.PreviewAsync(CurrentUserId(), request, ct));
        return Ok(ApiResponse<ReportConfigurationBundlePreviewDto>.Success(result));
    }

    /// <summary>导出捆绑为多工作表 Excel（只读）：复用同一有界、已授权导出管线，绝不信任客户端行 / 缓存。</summary>
    [HttpPost("export")]
    public async Task<IActionResult> Export([FromBody] ReportConfigurationBundleRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await ExportBoundedAsync(CurrentUserId(), request, exportKind: "excel");
    }

    /// <summary>导出捆绑为多节中文 PDF（只读）：复用同一有界、已授权导出管线，字体缺失 / 渲染失败显式失败。</summary>
    [HttpPost("export/pdf")]
    public async Task<IActionResult> ExportPdf([FromBody] ReportConfigurationBundleRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await ExportBoundedAsync(CurrentUserId(), request, exportKind: "pdf");
    }

    private async Task<IActionResult> ExportBoundedAsync(long userId, ReportConfigurationBundleRequest request, string exportKind)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            return await _budget.ExecuteAsync<IActionResult>(userId, HttpContext.RequestAborted, async lease =>
            {
                // 全部节复用同一执行租约（绝不二次获取租约）；任何一节失败即整体失败，不产出部分文件。
                var result = await _bundle.BuildExportResultAsync(userId, request, lease);

                var bytes = exportKind == "pdf"
                    ? RenderPdf(result, lease)
                    : RenderExcel(result, lease);

                if (bytes.Length > ReportConfigurationExecutionLimits.MaxGeneratedFileBytes)
                    throw new BusinessException(
                        "报表捆绑文件过大，已拒绝下载（关联ID：" + lease.CorrelationId + "）",
                        ReportConfigurationExecutionLimits.ErrorCodeResultTooLarge);

                LogExport(userId, request, exportKind, ReportConfigurationExecutionOutcomes.Success, stopwatch.ElapsedMilliseconds);

                return exportKind == "pdf"
                    ? File(bytes, "application/pdf", $"ReportConfigurationBundle_{DateTime.Now:yyyyMMddHHmmss}.pdf")
                    : File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                        $"ReportConfigurationBundle_{DateTime.Now:yyyyMMddHHmmss}.xlsx");
            });
        }
        catch (BusinessException ex)
        {
            LogExport(userId, request, exportKind, ReportConfigurationExecutionOutcomes.For(ex.Code), stopwatch.ElapsedMilliseconds);
            throw;
        }
        catch (OperationCanceledException)
        {
            LogExport(userId, request, exportKind, ReportConfigurationExecutionOutcomes.Cancelled, stopwatch.ElapsedMilliseconds);
            throw new BusinessException("报表捆绑导出已取消", ReportConfigurationExecutionLimits.ErrorCodeCancelled);
        }
        catch (Exception)
        {
            LogExport(userId, request, exportKind, ReportConfigurationExecutionOutcomes.Error, stopwatch.ElapsedMilliseconds);
            throw;
        }
    }

    private static byte[] RenderExcel(ReportConfigurationBundleExportResultDto result, IReportConfigurationExecutionLease lease)
    {
        try
        {
            return new ReportConfigurationBundleExcelExporter().Build(result, lease.Token);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (BusinessException)
        {
            throw;
        }
        catch (Exception)
        {
            throw new BusinessException(
                "Excel 导出渲染失败（关联ID：" + lease.CorrelationId + "）",
                ReportConfigurationExecutionLimits.ErrorCodeRenderingFailed);
        }
    }

    private static byte[] RenderPdf(ReportConfigurationBundleExportResultDto result, IReportConfigurationExecutionLease lease)
    {
        try
        {
            return ReportConfigurationBundlePdfExporter.Export(result, lease.Token);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (BusinessException)
        {
            throw;
        }
        catch (Exception)
        {
            throw new BusinessException(
                "PDF 导出渲染失败（关联ID：" + lease.CorrelationId + "）",
                ReportConfigurationExecutionLimits.ErrorCodeRenderingFailed);
        }
    }

    private void LogExport(long userId, ReportConfigurationBundleRequest request, string operation, string outcome, long durationMs)
    {
        if (_logger is null)
            return;

        try
        {
            _logger.LogInformation(
                "ReportConfigurationBundle {@Execution}",
                new
                {
                    UserId = userId,
                    SectionCount = request.Sections?.Count ?? 0,
                    Operation = operation,
                    Outcome = outcome,
                    DurationMs = durationMs,
                });
        }
        catch
        {
            // 日志失败不影响主流程
        }
    }
}
