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
    private readonly IReportConfigurationBundlePresetCatalog _presets;
    private readonly IReportConfigurationExecutionBudget _budget;
    private readonly ILogger<ReportConfigurationBundlesController>? _logger;

    public ReportConfigurationBundlesController(
        IReportConfigurationBundleService bundle,
        IReportConfigurationBundlePresetCatalog presets,
        IReportConfigurationExecutionBudget? budget = null,
        ILogger<ReportConfigurationBundlesController>? logger = null)
    {
        _bundle = bundle ?? throw new ArgumentNullException(nameof(bundle));
        _presets = presets ?? throw new ArgumentNullException(nameof(presets));
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

    /// <summary>组合预览：按服务端声明场景组合表头 / 明细两节；全部校验通过后才返回，绝不返回部分父项。</summary>
    [HttpPost("compose/preview")]
    public async Task<IActionResult> ComposePreview([FromBody] ReportConfigurationBundleCompositionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var result = await WithRequestCancellationAsync(ct => _bundle.ComposePreviewAsync(CurrentUserId(), request, ct));
        return Ok(ApiResponse<ReportConfigurationBundleCompositionPreviewDto>.Success(result));
    }

    /// <summary>导出组合为多工作表 Excel（只读）：复用同一有界、已授权组合管线，绝不信任客户端行 / 缓存。</summary>
    [HttpPost("compose/export")]
    public async Task<IActionResult> ComposeExport([FromBody] ReportConfigurationBundleCompositionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await ComposeExportBoundedAsync(CurrentUserId(), request, exportKind: "excel");
    }

    /// <summary>导出组合为中文 PDF（只读）：复用同一有界、已授权组合管线，字体缺失 / 渲染失败显式失败。</summary>
    [HttpPost("compose/export/pdf")]
    public async Task<IActionResult> ComposeExportPdf([FromBody] ReportConfigurationBundleCompositionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await ComposeExportBoundedAsync(CurrentUserId(), request, exportKind: "pdf");
    }

    /// <summary>只读列出当前账号已授权的捆绑预设（未授权隐藏；readiness 见 DTO，绝不 presence-only parity-passed）。</summary>
    [HttpGet("presets")]
    public async Task<IActionResult> ListPresets()
    {
        var result = await WithRequestCancellationAsync(ct => _presets.ListPresetsAsync(CurrentUserId(), ct));
        return Ok(ApiResponse<List<ReportConfigurationBundlePresetDto>>.Success(result));
    }

    /// <summary>取得单条已授权捆绑预设；未知 / 未授权显式拒绝（fail closed）。</summary>
    [HttpGet("presets/{presetKey}")]
    public async Task<IActionResult> GetPreset(string presetKey)
    {
        var dto = await WithRequestCancellationAsync(ct => _presets.GetPresetAsync(presetKey, CurrentUserId(), ct))
            ?? throw BusinessException.NotFound("捆绑预设不存在或无权访问");
        return Ok(ApiResponse<ReportConfigurationBundlePresetDto>.Success(dto));
    }

    /// <summary>按有限参数把捆绑预设物化为当前用户私有多节草稿（全有或全无；失败整体回滚本次新建草稿）。</summary>
    [HttpPost("presets/{presetKey}/materialize")]
    public async Task<IActionResult> MaterializePreset(string presetKey, [FromBody] ReportConfigurationBundlePresetMaterializeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var result = await WithRequestCancellationAsync(ct =>
            _presets.MaterializeAsync(presetKey, request, CurrentUserId(), ct));
        return Ok(ApiResponse<ReportConfigurationBundlePresetMaterializationDto>.Success(result));
    }

    /// <summary>物化捆绑预设并预览：复用既有有界、已授权捆绑预览；全部节通过后才返回，绝不返回部分节。</summary>
    [HttpPost("presets/{presetKey}/preview")]
    public async Task<IActionResult> PreviewPreset(string presetKey, [FromBody] ReportConfigurationBundlePresetMaterializeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var userId = CurrentUserId();
        var preview = await WithRequestCancellationAsync(async ct =>
        {
            var materialized = await _presets.MaterializeAsync(presetKey, request, userId, ct);
            return await _bundle.PreviewAsync(userId, materialized.Bundle, ct);
        });
        return Ok(ApiResponse<ReportConfigurationBundlePreviewDto>.Success(preview));
    }

    /// <summary>物化捆绑预设并导出为多工作表 Excel（只读）：复用同一有界、已授权导出管线。</summary>
    [HttpPost("presets/{presetKey}/export")]
    public async Task<IActionResult> ExportPreset(string presetKey, [FromBody] ReportConfigurationBundlePresetMaterializeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await ExportPresetBoundedAsync(presetKey, request, exportKind: "excel");
    }

    /// <summary>物化捆绑预设并导出为多节中文 PDF（只读）：复用同一有界、已授权导出管线。</summary>
    [HttpPost("presets/{presetKey}/export/pdf")]
    public async Task<IActionResult> ExportPresetPdf(string presetKey, [FromBody] ReportConfigurationBundlePresetMaterializeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await ExportPresetBoundedAsync(presetKey, request, exportKind: "pdf");
    }

    private async Task<IActionResult> ExportPresetBoundedAsync(
        string presetKey, ReportConfigurationBundlePresetMaterializeRequest request, string exportKind)
    {
        var userId = CurrentUserId();
        var materialized = await _presets.MaterializeAsync(presetKey, request, userId, HttpContext.RequestAborted);
        return await ExportBoundedAsync(userId, materialized.Bundle, exportKind);
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


    private async Task<IActionResult> ComposeExportBoundedAsync(
        long userId,
        ReportConfigurationBundleCompositionRequest request,
        string exportKind)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            return await _budget.ExecuteAsync<IActionResult>(userId, HttpContext.RequestAborted, async lease =>
            {
                var result = await _bundle.BuildCompositionExportResultAsync(userId, request, lease);

                var bytes = exportKind == "pdf"
                    ? RenderComposedPdf(result.Preview, lease)
                    : RenderComposedExcel(result.Preview, lease);

                if (bytes.Length > ReportConfigurationExecutionLimits.MaxGeneratedFileBytes)
                    throw new BusinessException(
                        "报表组合文件过大，已拒绝下载（关联ID：" + lease.CorrelationId + "）",
                        ReportConfigurationExecutionLimits.ErrorCodeResultTooLarge);

                LogComposeExport(userId, request, exportKind, ReportConfigurationExecutionOutcomes.Success, stopwatch.ElapsedMilliseconds);

                return exportKind == "pdf"
                    ? File(bytes, "application/pdf", $"ReportConfigurationComposition_{DateTime.Now:yyyyMMddHHmmss}.pdf")
                    : File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                        $"ReportConfigurationComposition_{DateTime.Now:yyyyMMddHHmmss}.xlsx");
            });
        }
        catch (BusinessException ex)
        {
            LogComposeExport(userId, request, exportKind, ReportConfigurationExecutionOutcomes.For(ex.Code), stopwatch.ElapsedMilliseconds);
            throw;
        }
        catch (OperationCanceledException)
        {
            LogComposeExport(userId, request, exportKind, ReportConfigurationExecutionOutcomes.Cancelled, stopwatch.ElapsedMilliseconds);
            throw new BusinessException("报表组合导出已取消", ReportConfigurationExecutionLimits.ErrorCodeCancelled);
        }
        catch (Exception)
        {
            LogComposeExport(userId, request, exportKind, ReportConfigurationExecutionOutcomes.Error, stopwatch.ElapsedMilliseconds);
            throw;
        }
    }

    private static byte[] RenderComposedExcel(
        ReportConfigurationBundleCompositionPreviewDto result,
        IReportConfigurationExecutionLease lease)
    {
        try
        {
            return new ReportConfigurationBundleExcelExporter().BuildComposed(result, lease.Token);
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

    private static byte[] RenderComposedPdf(
        ReportConfigurationBundleCompositionPreviewDto result,
        IReportConfigurationExecutionLease lease)
    {
        try
        {
            return ReportConfigurationBundlePdfExporter.ExportComposed(result, lease.Token);
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

    private void LogComposeExport(
        long userId,
        ReportConfigurationBundleCompositionRequest request,
        string operation,
        string outcome,
        long durationMs)
    {
        if (_logger is null)
            return;

        try
        {
            _logger.LogInformation(
                "ReportConfigurationBundleComposition {@Execution}",
                new
                {
                    UserId = userId,
                    CompositionKey = request.CompositionKey,
                    HeaderConfigurationId = request.HeaderConfigurationId,
                    DetailConfigurationId = request.DetailConfigurationId,
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
