using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace ERP.Api.Controllers;

/// <summary>
/// 受控打印模板绑定目录控制器（ERP-312 Stage 2）：只读枚举已保存打印模板为「兼容 / 显式不支持」的封闭族目录，
/// 并执行只读绑定校验（族支持 / 菜单授权 / 模板归属 / 数据集与列权限 / 有限字段别名与顺序 / 有界合法布局）。
/// <para>身份与权限：仅使用服务端认证的 <c>ClaimTypes.NameIdentifier</c>（客户端不得提交）；绝不回写模板、绝不授予权限。</para>
/// </summary>
[ApiController]
[Route("api/report-configuration-print-templates")]
[Authorize]
public class ReportConfigurationPrintTemplatesController : ControllerBase
{
    private readonly IReportConfigurationPrintTemplateCatalog _catalog;

    public ReportConfigurationPrintTemplatesController(IReportConfigurationPrintTemplateCatalog catalog)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    }

    private long CurrentUserId()
    {
        var value = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (long.TryParse(value, out var id) && id > 0)
            return id;
        throw new BusinessException("无法获取当前用户信息", ErrorCodes.Unauthorized);
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
}
