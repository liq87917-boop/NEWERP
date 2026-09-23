using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ERP.Api.Controllers;

/// <summary>
/// 客户端限制控制器
/// </summary>
[ApiController]
[Route("api/sys/client-limits")]
[Authorize]
public class ClientLimitController : BaseCrudController<SysClientLimit>
{
    public ClientLimitController(IGenericService<SysClientLimit> service) : base(service) { }
}

/// <summary>
/// 单据号规则控制器
/// </summary>
[ApiController]
[Route("api/sys/document-number-rules")]
[Authorize]
public class DocumentNumberRuleController : BaseCrudController<SysDocumentNumberRule>
{
    public DocumentNumberRuleController(IGenericService<SysDocumentNumberRule> service) : base(service) { }
}

/// <summary>
/// 系统日志控制器
/// </summary>
[ApiController]
[Route("api/sys/logs")]
[Authorize]
public class OperationLogController : ControllerBase
{
    private readonly IErpDbContext _db;

    public OperationLogController(IErpDbContext db)
    {
        _db = db;
    }

    /// <summary>分页查询操作日志（支持模块、用户、单据号、关键字与时间范围筛选）</summary>
    [HttpGet]
    public async Task<IActionResult> GetPaged([FromQuery] PageQuery query,
        [FromQuery] string? module,
        [FromQuery] string? userName,
        [FromQuery] string? billNo,
        [FromQuery] DateTime? start,
        [FromQuery] DateTime? end)
    {
        query.Normalize();
        var source = _db.SysOperationLogs.AsNoTracking().Where(l => !l.IsDeleted);
        if (!string.IsNullOrWhiteSpace(module))
            source = source.Where(l => l.Module == module);
        if (!string.IsNullOrWhiteSpace(userName))
            source = source.Where(l => l.UserName.Contains(userName));
        if (!string.IsNullOrWhiteSpace(billNo))
            source = source.Where(l => l.BillNo.Contains(billNo) || l.Path.Contains(billNo));
        if (start.HasValue)
            source = source.Where(l => l.CreatedAt >= start.Value);
        if (end.HasValue)
        {
            var endDate = end.Value.Date.AddDays(1);
            source = source.Where(l => l.CreatedAt < endDate);
        }
        // 关键字：模糊匹配路径、单据号、用户、动作
        if (!string.IsNullOrWhiteSpace(query.Keyword))
        {
            var keyword = query.Keyword.Trim();
            source = source.Where(l => l.Path.Contains(keyword) || l.BillNo.Contains(keyword)
                || l.UserName.Contains(keyword) || l.Action.Contains(keyword) || l.Module.Contains(keyword));
        }

        var total = await source.CountAsync();
        var items = await source.OrderByDescending(l => l.Id)
            .Skip((query.Page - 1) * query.PageSize).Take(query.PageSize).ToListAsync();

        return Ok(ApiResponse<PagedResult<SysOperationLog>>.Success(
            new PagedResult<SysOperationLog> { Items = items, Total = total, Page = query.Page, PageSize = query.PageSize }));
    }
}
