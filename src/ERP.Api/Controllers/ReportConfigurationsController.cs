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
/// 通用报表配置（ERP-261 Stage 1）控制器：数据集目录 + 用户私有报表配置的
/// 「保存 / 列表 / 加载 / 更新 / 重命名 / 复制 / 软删除 / 发布 / 恢复 / 修订列表 / 预览」。
/// <para>身份与权限：仅使用服务端认证的 <c>ClaimTypes.NameIdentifier</c> 作为所有者 Id（客户端不得提交）；
/// 每次目录 / 保存 / 加载 / 预览都重新校验数据集既有菜单授权（fail closed）；已保存定义本身绝不授予权限。</para>
/// <para>审计：写操作（POST / PUT / DELETE）由既有 <c>OperationLogMiddleware</c> 记录；GET 目录 / 列表 / 详情沿用只读约定。</para>
/// </summary>
[ApiController]
[Route("api/report-configurations")]
[Authorize]
public class ReportConfigurationsController : ControllerBase
{
    private readonly IReportConfigurationCatalog _catalog;
    private readonly IReportConfigurationService _service;
    private readonly IReportConfigurationExecutionService _execution;
    private readonly IReportConfigurationSharingService _sharing;
    private readonly IReportConfigurationExecutionBudget _budget;
    private readonly ILogger<ReportConfigurationsController>? _logger;

    public ReportConfigurationsController(
        IReportConfigurationCatalog catalog,
        IReportConfigurationService service,
        IReportConfigurationExecutionService execution,
        IReportConfigurationSharingService sharing,
        IReportConfigurationExecutionBudget? budget = null,
        ILogger<ReportConfigurationsController>? logger = null)
    {
        _catalog = catalog;
        _service = service;
        _execution = execution;
        _sharing = sharing;
        _budget = budget ?? new ReportConfigurationExecutionBudget();
        _logger = logger;
    }

    /// <summary>当前登录用户 Id（缺失或非正数时抛未认证，绝不猜测身份）</summary>
    private long CurrentUserId()
    {
        var value = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (long.TryParse(value, out var id) && id > 0)
            return id;
        throw new BusinessException("无法获取当前用户信息", ErrorCodes.Unauthorized);
    }

    /// <summary>数据集目录（有限、只读；仅当前账号已授权数据集）</summary>
    [HttpGet("catalog")]
    public async Task<IActionResult> Catalog()
    {
        var catalog = await _catalog.GetCatalogAsync(CurrentUserId());
        return Ok(ApiResponse<ReportConfigurationCatalogDto>.Success(catalog));
    }

    /// <summary>列出当前用户全部未删除私有报表配置（owner-only）</summary>
    [HttpGet]
    public async Task<IActionResult> List()
    {
        var result = await _service.ListAsync(CurrentUserId());
        return Ok(ApiResponse<List<ReportConfigurationSummaryDto>>.Success(result));
    }

    /// <summary>加载单条私有报表配置详情（重新校验当前数据集授权）</summary>
    [HttpGet("{id:long}")]
    public async Task<IActionResult> Get(long id)
    {
        var result = await _service.GetAsync(CurrentUserId(), id);
        return Ok(ApiResponse<ReportConfigurationDto>.Success(result));
    }

    /// <summary>新增一条私有报表配置草稿（所有者由服务端认证注入）</summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] ReportConfigurationSaveDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);
        var result = await _service.CreateAsync(CurrentUserId(), dto);
        return Ok(ApiResponse<ReportConfigurationDto>.Success(result, "保存成功"));
    }

    /// <summary>更新草稿定义 / 名称（需匹配当前预期版本令牌）</summary>
    [HttpPut("{id:long}")]
    public async Task<IActionResult> Update(long id, [FromQuery] int version, [FromBody] ReportConfigurationSaveDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);
        var result = await _service.UpdateAsync(CurrentUserId(), id, version, dto);
        return Ok(ApiResponse<ReportConfigurationDto>.Success(result, "更新成功"));
    }

    /// <summary>重命名私有报表配置（需匹配当前预期版本令牌）</summary>
    [HttpPost("{id:long}/rename")]
    public async Task<IActionResult> Rename(long id, [FromQuery] int version, [FromBody] ReportConfigurationRenameDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);
        var result = await _service.RenameAsync(CurrentUserId(), id, version, dto.Name);
        return Ok(ApiResponse<ReportConfigurationDto>.Success(result, "重命名成功"));
    }

    /// <summary>复制为新的私有草稿（同数据集、同定义；重新校验当前数据集授权）</summary>
    [HttpPost("{id:long}/copy")]
    public async Task<IActionResult> Copy(long id)
    {
        var result = await _service.CopyAsync(CurrentUserId(), id);
        return Ok(ApiResponse<ReportConfigurationDto>.Success(result, "复制成功"));
    }

    /// <summary>软删除私有报表配置及其全部发布修订（需匹配当前预期版本令牌）</summary>
    [HttpDelete("{id:long}")]
    public async Task<IActionResult> Delete(long id, [FromQuery] int version)
    {
        await _service.DeleteAsync(CurrentUserId(), id, version);
        return Ok(ApiResponse<object>.Success(null, "删除成功"));
    }

    /// <summary>发布：把当前定义固定为一条新的不可变修订快照（需匹配当前预期版本令牌）</summary>
    [HttpPost("{id:long}/publish")]
    public async Task<IActionResult> Publish(long id, [FromQuery] int version)
    {
        var result = await _service.PublishAsync(CurrentUserId(), id, version);
        return Ok(ApiResponse<ReportConfigurationDto>.Success(result, "发布成功"));
    }

    /// <summary>恢复历史发布版本（追加为一条新修订；需匹配当前预期版本令牌）</summary>
    [HttpPost("{id:long}/restore")]
    public async Task<IActionResult> Restore(long id, [FromQuery] int version, [FromQuery] int revisionVersion)
    {
        var result = await _service.RestoreAsync(CurrentUserId(), id, version, revisionVersion);
        return Ok(ApiResponse<ReportConfigurationDto>.Success(result, "恢复成功"));
    }

    /// <summary>列出单条私有报表配置的全部发布修订（owner-only，按版本号升序）</summary>
    [HttpGet("{id:long}/revisions")]
    public async Task<IActionResult> Revisions(long id)
    {
        var result = await _service.ListRevisionsAsync(CurrentUserId(), id);
        return Ok(ApiResponse<List<ReportConfigurationRevisionDto>>.Success(result));
    }

    /// <summary>列出某条私有报表配置的全部有效只读授权（owner-only）</summary>
    [HttpGet("{id:long}/grants")]
    public async Task<IActionResult> Grants(long id)
    {
        var result = await _sharing.ListGrantsAsync(CurrentUserId(), id);
        return Ok(ApiResponse<List<ReportConfigurationGrantDto>>.Success(result));
    }

    /// <summary>授予 / 变更某条私有报表配置的只读授权（owner-only；固定发布修订；变更 pin 需回传预期版本）</summary>
    [HttpPost("{id:long}/grants")]
    public async Task<IActionResult> Grant(long id, [FromBody] ReportConfigurationGrantRequestDto request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var result = await _sharing.GrantAsync(CurrentUserId(), id, request);
        return Ok(ApiResponse<ReportConfigurationGrantDto>.Success(result, "授权成功"));
    }

    /// <summary>撤销某条私有报表配置的只读授权（owner-only；需回传预期版本，防止陈旧撤销）</summary>
    [HttpDelete("{id:long}/grants/{recipientUserId:long}")]
    public async Task<IActionResult> Revoke(long id, long recipientUserId, [FromQuery] int version)
    {
        await _sharing.RevokeAsync(CurrentUserId(), id, recipientUserId, version);
        return Ok(ApiResponse<object>.Success(null, "撤销授权成功"));
    }

    /// <summary>列出当前用户被共享的只读发布快照（recipient-only；只暴露固定快照）</summary>
    [HttpGet("shared")]
    public async Task<IActionResult> Shared()
    {
        var result = await _sharing.ListSharedAsync(CurrentUserId());
        return Ok(ApiResponse<List<ReportConfigurationSharedSummaryDto>>.Success(result));
    }

    /// <summary>加载当前用户被共享的只读发布快照详情（recipient-only）</summary>
    [HttpGet("shared/{configurationId:long}")]
    public async Task<IActionResult> SharedDetail(long configurationId)
    {
        var result = await _sharing.GetSharedAsync(CurrentUserId(), configurationId);
        return Ok(ApiResponse<ReportConfigurationSharedDetailDto>.Success(result));
    }

    /// <summary>复制被共享的只读发布快照为当前用户自有草稿（recipient-only，新鲜校验，绝不改写原配置）</summary>
    [HttpPost("shared/{configurationId:long}/copy")]
    public async Task<IActionResult> CopyShared(long configurationId)
    {
        var result = await _sharing.CopySharedAsync(CurrentUserId(), configurationId);
        return Ok(ApiResponse<ReportConfigurationDto>.Success(result, "复制成功"));
    }

    /// <summary>预览：草稿（默认）或指定发布修订；每次重新校验身份 / 数据集授权 / 数据范围（fail closed）</summary>
    [HttpPost("preview")]
    public async Task<IActionResult> Preview([FromBody] ReportConfigurationPreviewRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var result = await _execution.PreviewAsync(CurrentUserId(), request);
        return Ok(ApiResponse<ReportConfigurationPreviewDto>.Success(result));
    }

    /// <summary>
    /// 导出当前页为 Excel（ERP-263，只读）：复用同一有界、已授权预览管线，仅导出当前预览页选定列，
    /// 绝不信任客户端行 / 身份 / 范围 / 预览缓存；追加「报表口径」工作表标注定义名称 / 版本、数据集 / 行粒度、
    /// 规范化查询筛选与日期范围、当前页覆盖口径、币种 / 单位语义、只读与边界、未知值说明（即使对应展示列被取消选择也始终包含）。
    /// 分组小计仅覆盖当前预览页且按币种分区，绝不追加全匹配合计；数值保留符号、null 未知留空、日期 / 布尔按类型呈现、文本做公式注入转义。
    /// 每次请求重新校验身份 / 数据集菜单授权 / 数据范围（fail closed）；授权撤销 / 无效修订 / 环境不可用 / 导出失败返回受控错误，不返回过期下载。
    /// <para>全程只读，不写库、不执行任意 SQL；请求由既有 <c>OperationLogMiddleware</c> 记录审计（动作「导出」）。</para>
    /// </summary>
    [HttpPost("export")]
    public async Task<IActionResult> Export([FromBody] ReportConfigurationPreviewRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await ExportBoundedAsync(CurrentUserId(), request, exportKind: "excel");
    }

    /// <summary>
    /// 导出当前页为中文 PDF（ERP-264，只读）：复用同一有界、已授权预览管线，仅导出当前预览页选定列，
    /// 绝不信任客户端行 / 身份 / 范围 / 预览缓存；宽列集按列页拆分并重复允许标识列 / 表头，文本折行、数值保留符号，
    /// 分组小计仅覆盖当前预览页且按币种分区（绝不追加全匹配合计），元数据始终包含定义名称 / 版本、数据集 / 行粒度、
    /// 规范化查询筛选与日期范围、当前页覆盖口径、币种 / 单位语义、只读与边界、未知值说明。
    /// 每次请求重新校验身份 / 数据集菜单授权 / 数据范围（fail closed）；授权撤销 / 无效修订 / 环境不可用 / 字体缺失 /
    /// 渲染失败返回受控错误，绝不返回过期或半成品下载。
    /// <para>全程只读，不写库、不执行任意 SQL；请求由既有 <c>OperationLogMiddleware</c> 记录审计（动作「导出」）。</para>
    /// </summary>
    [HttpPost("export/pdf")]
    public async Task<IActionResult> ExportPdf([FromBody] ReportConfigurationPreviewRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await ExportBoundedAsync(CurrentUserId(), request, exportKind: "pdf");
    }

    private async Task<IActionResult> ExportBoundedAsync(long userId, ReportConfigurationPreviewRequest request, string exportKind)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            return await _budget.ExecuteAsync<IActionResult>(userId, HttpContext.RequestAborted, async lease =>
            {
                var preview = await _execution.PreviewAsync(userId, request, lease);

                var bytes = exportKind == "pdf"
                    ? RenderPdf(preview, lease)
                    : RenderExcel(preview, lease);

                if (bytes.Length > ReportConfigurationExecutionLimits.MaxGeneratedFileBytes)
                    throw new BusinessException(
                        "报表文件过大，已拒绝下载（关联ID：" + lease.CorrelationId + "）",
                        ReportConfigurationExecutionLimits.ErrorCodeResultTooLarge);

                LogExport(userId, request, preview, exportKind, ReportConfigurationExecutionOutcomes.Success, stopwatch.ElapsedMilliseconds);

                return exportKind == "pdf"
                    ? File(bytes, "application/pdf", $"ReportConfiguration_{DateTime.Now:yyyyMMddHHmmss}.pdf")
                    : File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                        $"ReportConfiguration_{DateTime.Now:yyyyMMddHHmmss}.xlsx");
            });
        }
        catch (BusinessException ex)
        {
            LogExport(userId, request, null, exportKind, ReportConfigurationExecutionOutcomes.For(ex.Code), stopwatch.ElapsedMilliseconds);
            throw;
        }
        catch (OperationCanceledException)
        {
            LogExport(userId, request, null, exportKind, ReportConfigurationExecutionOutcomes.Cancelled, stopwatch.ElapsedMilliseconds);
            throw;
        }
        catch (Exception)
        {
            LogExport(userId, request, null, exportKind, ReportConfigurationExecutionOutcomes.Error, stopwatch.ElapsedMilliseconds);
            throw;
        }
    }

    private static byte[] RenderExcel(ReportConfigurationPreviewDto preview, IReportConfigurationExecutionLease lease)
    {
        try
        {
            return new ReportConfigurationExcelExporter().Build(preview, lease.Token);
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

    private static byte[] RenderPdf(ReportConfigurationPreviewDto preview, IReportConfigurationExecutionLease lease)
    {
        try
        {
            return ReportConfigurationPdfExporter.Export(preview, lease.Token);
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

    private void LogExport(long userId, ReportConfigurationPreviewRequest request, ReportConfigurationPreviewDto? preview, string operation, string outcome, long durationMs)
    {
        if (_logger is null)
            return;

        try
        {
            _logger.LogInformation(
                "ReportConfigurationExecution {@Execution}",
                new
                {
                    UserId = userId,
                    ConfigurationId = request.ConfigurationId,
                    PinnedRevision = preview?.PinnedRevisionVersion,
                    DatasetKey = preview?.DatasetKey,
                    Operation = operation,
                    Outcome = outcome,
                    DurationMs = durationMs,
                    RowCount = preview?.Rows?.Count ?? 0,
                    ColumnCount = preview?.Columns?.Count ?? 0,
                    RelationKeys = RelationKeysOf(preview),
                });
        }
        catch
        {
            // 日志失败不影响主流程
        }
    }

    private static string[] RelationKeysOf(ReportConfigurationPreviewDto? preview)
    {
        if (preview?.RelationEvidence is null)
            return Array.Empty<string>();

        return preview.RelationEvidence
            .Select(r => r.RelationKey)
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }
}
