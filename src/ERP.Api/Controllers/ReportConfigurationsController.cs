using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Infrastructure.Export;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
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
[ReportRequestBodyLimit]
public class ReportConfigurationsController : ControllerBase
{
    /// <summary>
    /// 通用报表配置写请求的 UTF-8 传输层体积上限（512 KiB）：在 JSON 模型绑定之前由
    /// <see cref="ReportRequestBodyLimitAttribute"/> 通过 ASP.NET 请求体积限制（Kestrel 的
    /// IHttpMaxRequestBodySizeFeature）强制执行，独立于 Content-Length（含 chunked），
    /// 绝不把无界请求读入内存。512 KiB = 8 × 归一化定义上限（64 KiB），足以覆盖传输信封转义
    /// 与普通包装开销而不会静默截断定义。
    /// </summary>
    public const long MaxWireBodyBytes = 512 * 1024;

    /// <summary>请求体过大（工作台输入过大）的受控业务错误码（1009）。</summary>
    public const int ErrorCodeInputTooLarge = 1009;

    /// <summary>请求体过大（工作台输入过大）的受控提示文案（绝不回声载荷 / SQL / 密钥）。</summary>
    public const string InputTooLargeMessage = "报表配置请求体过大，已拒绝：工作台输入过大（超过 512 KiB 上限，请精简定义后重试）";

    private readonly IReportConfigurationCatalog _catalog;
    private readonly IReportConfigurationService _service;
    private readonly IReportConfigurationExecutionService _execution;
    private readonly IReportConfigurationSharingService _sharing;
    private readonly IReportConfigurationTransferService? _transfer;
    private readonly IReportConfigurationExecutionBudget _budget;
    private readonly ILogger<ReportConfigurationsController>? _logger;

    public ReportConfigurationsController(
        IReportConfigurationCatalog catalog,
        IReportConfigurationService service,
        IReportConfigurationExecutionService execution,
        IReportConfigurationSharingService sharing,
        IReportConfigurationExecutionBudget? budget = null,
        ILogger<ReportConfigurationsController>? logger = null,
        IReportConfigurationTransferService? transfer = null)
    {
        _catalog = catalog;
        _service = service;
        _execution = execution;
        _sharing = sharing;
        _transfer = transfer;
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

    /// <summary>
    /// 在请求生命周期内执行报表配置操作：把 <see cref="HttpContext.RequestAborted"/> 原样传给既有应用接口，
    /// 并把「调用方取消」统一转换为受控的「已取消」业务异常（1007）。绝不包装成会丢弃仍在提交的写操作的超时助手，
    /// 也绝不把已经提交的事务谎称为已回滚。
    /// </summary>
    private async Task<T> WithRequestCancellationAsync<T>(Func<CancellationToken, Task<T>> work)
    {
        var token = HttpContext?.RequestAborted ?? CancellationToken.None;
        try
        {
            return await work(token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw new BusinessException("报表配置操作已取消", ReportConfigurationExecutionLimits.ErrorCodeCancelled);
        }
    }

    private async Task WithRequestCancellationAsync(Func<CancellationToken, Task> work)
    {
        var token = HttpContext?.RequestAborted ?? CancellationToken.None;
        try
        {
            await work(token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw new BusinessException("报表配置操作已取消", ReportConfigurationExecutionLimits.ErrorCodeCancelled);
        }
    }

    /// <summary>数据集目录（有限、只读；仅当前账号已授权数据集）</summary>
    [HttpGet("catalog")]
    public async Task<IActionResult> Catalog()
    {
        var catalog = await WithRequestCancellationAsync(ct => _catalog.GetCatalogAsync(CurrentUserId(), ct));
        return Ok(ApiResponse<ReportConfigurationCatalogDto>.Success(catalog));
    }

    /// <summary>有界 keyset 分页列出当前用户未删除私有报表配置（owner-only；limit 默认 25、最大 100）</summary>
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] int? limit, [FromQuery] string? cursor)
    {
        var result = await WithRequestCancellationAsync(ct => _service.ListPageAsync(CurrentUserId(), limit, cursor, ct));
        return Ok(ApiResponse<ReportConfigurationPage<ReportConfigurationSummaryDto>>.Success(result));
    }

    /// <summary>加载单条私有报表配置详情（重新校验当前数据集授权）</summary>
    [HttpGet("{id:long}")]
    public async Task<IActionResult> Get(long id)
    {
        var result = await WithRequestCancellationAsync(ct => _service.GetAsync(CurrentUserId(), id, ct));
        return Ok(ApiResponse<ReportConfigurationDto>.Success(result));
    }

    /// <summary>新增一条私有报表配置草稿（所有者由服务端认证注入）</summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] ReportConfigurationSaveDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);
        var result = await WithRequestCancellationAsync(ct => _service.CreateAsync(CurrentUserId(), dto, ct));
        return Ok(ApiResponse<ReportConfigurationDto>.Success(result, "保存成功"));
    }

    /// <summary>更新草稿定义 / 名称（需匹配当前预期版本令牌）</summary>
    [HttpPut("{id:long}")]
    public async Task<IActionResult> Update(long id, [FromQuery] int version, [FromBody] ReportConfigurationSaveDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);
        var result = await WithRequestCancellationAsync(ct => _service.UpdateAsync(CurrentUserId(), id, version, dto, ct));
        return Ok(ApiResponse<ReportConfigurationDto>.Success(result, "更新成功"));
    }

    /// <summary>重命名私有报表配置（需匹配当前预期版本令牌）</summary>
    [HttpPost("{id:long}/rename")]
    public async Task<IActionResult> Rename(long id, [FromQuery] int version, [FromBody] ReportConfigurationRenameDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);
        var result = await WithRequestCancellationAsync(ct => _service.RenameAsync(CurrentUserId(), id, version, dto.Name, ct));
        return Ok(ApiResponse<ReportConfigurationDto>.Success(result, "重命名成功"));
    }

    /// <summary>复制为新的私有草稿（同数据集、同定义；重新校验当前数据集授权）</summary>
    [HttpPost("{id:long}/copy")]
    public async Task<IActionResult> Copy(long id)
    {
        var result = await WithRequestCancellationAsync(ct => _service.CopyAsync(CurrentUserId(), id, ct));
        return Ok(ApiResponse<ReportConfigurationDto>.Success(result, "复制成功"));
    }

    /// <summary>软删除私有报表配置及其全部发布修订（需匹配当前预期版本令牌）</summary>
    [HttpDelete("{id:long}")]
    public async Task<IActionResult> Delete(long id, [FromQuery] int version)
    {
        await WithRequestCancellationAsync(ct => _service.DeleteAsync(CurrentUserId(), id, version, ct));
        return Ok(ApiResponse<object>.Success(null, "删除成功"));
    }

    /// <summary>发布：把当前定义固定为一条新的不可变修订快照（需匹配当前预期版本令牌）</summary>
    [HttpPost("{id:long}/publish")]
    public async Task<IActionResult> Publish(long id, [FromQuery] int version)
    {
        var result = await WithRequestCancellationAsync(ct => _service.PublishAsync(CurrentUserId(), id, version, ct));
        return Ok(ApiResponse<ReportConfigurationDto>.Success(result, "发布成功"));
    }

    /// <summary>恢复历史发布版本（追加为一条新修订；需匹配当前预期版本令牌）</summary>
    [HttpPost("{id:long}/restore")]
    public async Task<IActionResult> Restore(long id, [FromQuery] int version, [FromQuery] int revisionVersion)
    {
        var result = await WithRequestCancellationAsync(ct => _service.RestoreAsync(CurrentUserId(), id, version, revisionVersion, ct));
        return Ok(ApiResponse<ReportConfigurationDto>.Success(result, "恢复成功"));
    }

    /// <summary>有界 keyset 分页列出单条私有报表配置的发布修订（owner-only，按版本号升序；limit 默认 25、最大 100）</summary>
    [HttpGet("{id:long}/revisions")]
    public async Task<IActionResult> Revisions(long id, [FromQuery] int? limit, [FromQuery] string? cursor)
    {
        var result = await WithRequestCancellationAsync(ct => _service.ListRevisionsPageAsync(CurrentUserId(), id, limit, cursor, ct));
        return Ok(ApiResponse<ReportConfigurationPage<ReportConfigurationRevisionDto>>.Success(result));
    }

    /// <summary>有界 keyset 分页列出某条私有报表配置的有效只读授权（owner-only；limit 默认 25、最大 100）</summary>
    [HttpGet("{id:long}/grants")]
    public async Task<IActionResult> Grants(long id, [FromQuery] int? limit, [FromQuery] string? cursor)
    {
        var result = await WithRequestCancellationAsync(ct => _sharing.ListGrantsPageAsync(CurrentUserId(), id, limit, cursor, ct));
        return Ok(ApiResponse<ReportConfigurationPage<ReportConfigurationGrantDto>>.Success(result));
    }

    /// <summary>授予 / 变更某条私有报表配置的只读授权（owner-only；固定发布修订；变更 pin 需回传预期版本）</summary>
    [HttpPost("{id:long}/grants")]
    public async Task<IActionResult> Grant(long id, [FromBody] ReportConfigurationGrantRequestDto request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var result = await WithRequestCancellationAsync(ct => _sharing.GrantAsync(CurrentUserId(), id, request, ct));
        return Ok(ApiResponse<ReportConfigurationGrantDto>.Success(result, "授权成功"));
    }

    /// <summary>撤销某条私有报表配置的只读授权（owner-only；需回传预期版本，防止陈旧撤销）</summary>
    [HttpDelete("{id:long}/grants/{recipientUserId:long}")]
    public async Task<IActionResult> Revoke(long id, long recipientUserId, [FromQuery] int version)
    {
        await WithRequestCancellationAsync(ct => _sharing.RevokeAsync(CurrentUserId(), id, recipientUserId, version, ct));
        return Ok(ApiResponse<object>.Success(null, "撤销授权成功"));
    }

    /// <summary>有界 keyset 分页列出当前用户被共享的只读发布快照（recipient-only；limit 默认 25、最大 100）</summary>
    [HttpGet("shared")]
    public async Task<IActionResult> Shared([FromQuery] int? limit, [FromQuery] string? cursor)
    {
        var result = await WithRequestCancellationAsync(ct => _sharing.ListSharedPageAsync(CurrentUserId(), limit, cursor, ct));
        return Ok(ApiResponse<ReportConfigurationPage<ReportConfigurationSharedSummaryDto>>.Success(result));
    }

    /// <summary>加载当前用户被共享的只读发布快照详情（recipient-only）</summary>
    [HttpGet("shared/{configurationId:long}")]
    public async Task<IActionResult> SharedDetail(long configurationId)
    {
        var result = await WithRequestCancellationAsync(ct => _sharing.GetSharedAsync(CurrentUserId(), configurationId, ct));
        return Ok(ApiResponse<ReportConfigurationSharedDetailDto>.Success(result));
    }

    /// <summary>复制被共享的只读发布快照为当前用户自有草稿（recipient-only，新鲜校验，绝不改写原配置）</summary>
    [HttpPost("shared/{configurationId:long}/copy")]
    public async Task<IActionResult> CopyShared(long configurationId)
    {
        var result = await WithRequestCancellationAsync(ct => _sharing.CopySharedAsync(CurrentUserId(), configurationId, ct));
        return Ok(ApiResponse<ReportConfigurationDto>.Success(result, "复制成功"));
    }

    /// <summary>
    /// 导出可移植报表定义信封（自有草稿 / 自有发布修订 / 被共享的固定发布快照，只读）。
    /// <para>只返回格式 / schema 版本 / 安全名称 / 结构化定义；绝不返回 ERP 行、身份、授权、历史、SQL 连接或附件；
    /// 共享来源撤销后导出失败（fail closed）。</para>
    /// </summary>
    [HttpPost("transfer/export")]
    public async Task<IActionResult> ExportDefinition([FromBody] ReportConfigurationTransferExportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var transfer = _transfer
            ?? throw new BusinessException("报表配置传输服务未初始化", ErrorCodes.InternalError);
        var result = await WithRequestCancellationAsync(ct => transfer.ExportAsync(CurrentUserId(), request, ct));
        return Ok(ApiResponse<ReportConfigurationTransferEnvelopeDto>.Success(result));
    }

    /// <summary>
    /// 导入可移植报表定义信封为当前用户新的私有草稿（严格校验；绝不覆盖 / 发布 / 授予任何权限）。
    /// </summary>
    [HttpPost("transfer/import")]
    public async Task<IActionResult> ImportDefinition([FromBody] ReportConfigurationTransferImportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var transfer = _transfer
            ?? throw new BusinessException("报表配置传输服务未初始化", ErrorCodes.InternalError);
        var result = await WithRequestCancellationAsync(ct => transfer.ImportAsync(CurrentUserId(), request, ct));
        return Ok(ApiResponse<ReportConfigurationDto>.Success(result, "导入成功"));
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
                // ERP-275：导出复用同一执行租约；全匹配覆盖时由同一有界一致快照派生完整事实（≤1000）与全部选中汇总，
                // 普通当前页覆盖维持 ≤200 行；绝不二次获取租约、绝不信任客户端行。
                var result = await _execution.BuildExportResultAsync(userId, request, lease);

                var bytes = exportKind == "pdf"
                    ? RenderPdf(result, lease)
                    : RenderExcel(result, lease);

                if (bytes.Length > ReportConfigurationExecutionLimits.MaxGeneratedFileBytes)
                    throw new BusinessException(
                        "报表文件过大，已拒绝下载（关联ID：" + lease.CorrelationId + "）",
                        ReportConfigurationExecutionLimits.ErrorCodeResultTooLarge);

                LogExport(userId, request, result.Preview, exportKind, ReportConfigurationExecutionOutcomes.Success, stopwatch.ElapsedMilliseconds);

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
            throw new BusinessException("报表导出已取消", ReportConfigurationExecutionLimits.ErrorCodeCancelled);
        }
        catch (Exception)
        {
            LogExport(userId, request, null, exportKind, ReportConfigurationExecutionOutcomes.Error, stopwatch.ElapsedMilliseconds);
            throw;
        }
    }

    private static byte[] RenderExcel(ReportConfigurationExportResultDto result, IReportConfigurationExecutionLease lease)
    {
        try
        {
            return new ReportConfigurationExcelExporter().Build(result, lease.Token);
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

    private static byte[] RenderPdf(ReportConfigurationExportResultDto result, IReportConfigurationExecutionLease lease)
    {
        try
        {
            return ReportConfigurationPdfExporter.Export(result, lease.Token);
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

/// <summary>
/// 通用报表配置写请求的传输层体积护栏（ERP-285 Stage 1）：在 JSON 模型绑定之前把 ASP.NET
/// 请求体积上限收紧到 <see cref="ReportConfigurationsController.MaxWireBodyBytes"/>（512 KiB UTF-8），
/// 并把超限导致的 413 <see cref="BadHttpRequestException"/> 映射为受控的模块内 413 响应
/// （HTTP 413 + { code, message, data } 信封）。绝不把无界请求读入内存，也不影响其他模块的异常映射。
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
public sealed class ReportRequestBodyLimitAttribute : Attribute, IAuthorizationFilter, IExceptionFilter
{
    /// <summary>在模型绑定之前通过 ASP.NET 请求体积限制收紧传输层上限（Kestrel 强制执行，含 chunked）。</summary>
    public void OnAuthorization(AuthorizationFilterContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var feature = context.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (feature is { IsReadOnly: false })
        {
            feature.MaxRequestBodySize = ReportConfigurationsController.MaxWireBodyBytes;
        }
    }

    /// <summary>把超限 413 映射为受控的模块内响应；其余异常原样放行给全局异常中间件。</summary>
    public void OnException(ExceptionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Exception is BadHttpRequestException { StatusCode: StatusCodes.Status413PayloadTooLarge })
        {
            context.Result = new JsonResult(new ApiResponse<object>(
                ReportConfigurationsController.ErrorCodeInputTooLarge,
                ReportConfigurationsController.InputTooLargeMessage,
                null))
            {
                StatusCode = StatusCodes.Status413PayloadTooLarge
            };
            context.ExceptionHandled = true;
        }
    }
}
