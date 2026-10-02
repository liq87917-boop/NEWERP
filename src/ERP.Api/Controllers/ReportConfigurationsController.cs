using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Infrastructure.Export;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
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

    public ReportConfigurationsController(
        IReportConfigurationCatalog catalog,
        IReportConfigurationService service,
        IReportConfigurationExecutionService execution)
    {
        _catalog = catalog;
        _service = service;
        _execution = execution;
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
        var preview = await _execution.PreviewAsync(CurrentUserId(), request);
        var bytes = new ReportConfigurationExcelExporter().Build(preview);
        return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            $"ReportConfiguration_{DateTime.Now:yyyyMMddHHmmss}.xlsx");
    }
}
