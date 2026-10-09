using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

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
/// 单据号规则控制器（ERP-445）：分页 / 全部 / 详情 / 新增 / 修改 / 删除 / 批量删除在读取任何计数或写入任何规则行
/// <b>之前</b>都先复核实时启用身份 + 既有「单据号规则」（<c>doc-rule</c>）功能菜单
/// （见 <see cref="DocumentNumberRuleAuthorizationRules"/>）；新增 / 修改另校验单据类型 / 编码唯一 /
/// 前缀 / 分隔符 / 日期格式 / 流水位 / 当前流水的有界形状。缺失 / 禁用 / 已删除 / 撤销菜单的身份一律以
/// 既有受控非披露错误 fail closed，不新增任何授权，也不把空身份当作管理员。
/// </summary>
[ApiController]
[Route("api/sys/document-number-rules")]
[Authorize]
public class DocumentNumberRuleController : BaseCrudController<SysDocumentNumberRule>
{
    private readonly IErpDbContext _db;

    public DocumentNumberRuleController(IGenericService<SysDocumentNumberRule> service, IErpDbContext db)
        : base(service)
    {
        _db = db;
    }

    /// <summary>当前登录账号 Id（只来自已认证请求主体；缺失 / 非数字 / 非正返回 <c>null</c>，由规则层 fail closed）。</summary>
    private long? CurrentUserId()
    {
        var value = ControllerContext?.HttpContext?.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return long.TryParse(value, out var id) && id > 0 ? id : null;
    }

    /// <summary>单据号规则入口授权（实时身份 + 账号状态 + 既有 doc-rule 菜单）。</summary>
    private Task EnsureAuthorizedAsync()
        => DocumentNumberRuleAuthorizationRules.EnsureAuthorizedAsync(_db, CurrentUserId());

    /// <summary>分页查询：先授权，再按既有分页 / 响应契约返回。</summary>
    [HttpGet]
    public override async Task<IActionResult> GetPaged([FromQuery] PageQuery query)
    {
        await EnsureAuthorizedAsync();
        var result = await Service.GetPagedAsync(query);
        return Ok(ApiResponse<PagedResult<SysDocumentNumberRule>>.Success(result));
    }

    /// <summary>查询全部（供下拉框等使用）：先授权，再返回既有全量契约。</summary>
    [HttpGet("all")]
    public override async Task<IActionResult> GetAll()
    {
        await EnsureAuthorizedAsync();
        var result = await Service.GetAllAsync();
        return Ok(ApiResponse<List<SysDocumentNumberRule>>.Success(result));
    }

    /// <summary>根据主键获取：先授权，再返回既有详情契约。</summary>
    [HttpGet("{id:long}")]
    public override async Task<IActionResult> GetById(long id)
    {
        await EnsureAuthorizedAsync();
        var result = await Service.GetByIdAsync(id);
        return Ok(ApiResponse<SysDocumentNumberRule>.Success(result));
    }

    /// <summary>新增：写入任何字段之前完成授权 + 编号形状校验（含规则编码唯一）。</summary>
    [HttpPost]
    public override async Task<IActionResult> Create([FromBody] SysDocumentNumberRule entity)
    {
        if (entity is null) throw BusinessException.InvalidParameter("请求内容不能为空");
        await EnsureAuthorizedAsync();
        entity.Id = 0;
        DocumentNumberRuleAuthorizationRules.NormalizeForWrite(entity);
        await DocumentNumberRuleAuthorizationRules.ValidateAsync(_db, entity);
        var result = await Service.CreateAsync(entity);
        return Ok(ApiResponse<SysDocumentNumberRule>.Success(result, "新增成功"));
    }

    /// <summary>修改：先授权，再校验拟议编号形状，失败不改写任何规则行。</summary>
    [HttpPut("{id:long}")]
    public override async Task<IActionResult> Update(long id, [FromBody] SysDocumentNumberRule entity)
    {
        if (entity is null) throw BusinessException.InvalidParameter("请求内容不能为空");
        await EnsureAuthorizedAsync();
        entity.Id = id;
        DocumentNumberRuleAuthorizationRules.NormalizeForWrite(entity);
        await DocumentNumberRuleAuthorizationRules.ValidateAsync(_db, entity);
        var result = await Service.UpdateAsync(entity);
        return Ok(ApiResponse<SysDocumentNumberRule>.Success(result, "更新成功"));
    }

    /// <summary>删除（软删除）：先授权，再按既有软删除契约执行。</summary>
    [HttpDelete("{id:long}")]
    public override async Task<IActionResult> Delete(long id)
    {
        await EnsureAuthorizedAsync();
        await Service.DeleteAsync(id);
        return Ok(ApiResponse<object>.Success(null, "删除成功"));
    }

    /// <summary>批量删除（软删除）：先授权，再按既有批量软删除契约执行。</summary>
    [HttpPost("batch-delete")]
    public override async Task<IActionResult> BatchDelete([FromBody] List<long> ids)
    {
        await EnsureAuthorizedAsync();
        await Service.BatchDeleteAsync(ids);
        return Ok(ApiResponse<object>.Success(null, "批量删除成功"));
    }
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
