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
/// 系统参数控制器（ERP-446）：分页 / 主键详情 / 按键详情 / 新增 / 修改在读取任何参数行或写入任何参数行
/// <b>之前</b>都先复核实时启用身份 + 既有「系统参数」（<c>sys-parameter</c>）功能菜单
/// （见 <see cref="SystemParameterAuthorizationRules"/>）；新增 / 修改另校验参数键 / 名称 / 值 / 说明的既有
/// 持久化边界与被运营单据默认值消费的币种 / 汇率取值。缺失 / 禁用 / 已删除 / 撤销菜单的身份一律以
/// 既有受控非披露错误 fail closed，不新增任何授权，也不把空身份当作管理员。
/// </summary>
[ApiController]
[Route("api/sys/parameters")]
[Authorize]
public class ParameterController : ControllerBase
{
    private readonly IErpDbContext _db;

    public ParameterController(IErpDbContext db)
    {
        _db = db;
    }

    /// <summary>当前登录账号 Id（只来自已认证请求主体；缺失 / 非数字 / 非正返回 <c>null</c>，由规则层 fail closed）。</summary>
    private long? CurrentUserId()
    {
        var value = ControllerContext?.HttpContext?.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return long.TryParse(value, out var id) && id > 0 ? id : null;
    }

    /// <summary>系统参数入口授权（实时身份 + 账号状态 + 既有 sys-parameter 菜单）。</summary>
    private Task EnsureAuthorizedAsync()
        => SystemParameterAuthorizationRules.EnsureAuthorizedAsync(_db, CurrentUserId());

    /// <summary>分页查询系统参数（先授权，再按既有分页 / 响应契约返回）</summary>
    [HttpGet]
    public async Task<IActionResult> GetAll([FromQuery] PageQuery query)
    {
        await EnsureAuthorizedAsync();
        query.Normalize();
        var source = _db.SysParameters.AsNoTracking().Where(p => !p.IsDeleted);
        if (!string.IsNullOrWhiteSpace(query.Keyword))
            source = source.Where(p => p.ParamKey.Contains(query.Keyword) || p.ParamName.Contains(query.Keyword));

        var total = await source.CountAsync();
        var items = await source.OrderBy(p => p.ParamKey)
            .Skip((query.Page - 1) * query.PageSize).Take(query.PageSize).ToListAsync();

        return Ok(ApiResponse<PagedResult<SysParameter>>.Success(
            new PagedResult<SysParameter> { Items = items, Total = total, Page = query.Page, PageSize = query.PageSize }));
    }

    /// <summary>根据主键获取参数（先授权，再返回既有详情契约）</summary>
    [HttpGet("{id:long}")]
    public async Task<IActionResult> GetById(long id)
    {
        await EnsureAuthorizedAsync();
        var item = await _db.SysParameters.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id && !p.IsDeleted)
            ?? throw BusinessException.NotFound("参数不存在");
        return Ok(ApiResponse<SysParameter>.Success(item));
    }

    /// <summary>根据键获取参数值（先授权，再返回既有详情契约）</summary>
    [HttpGet("key/{key}")]
    public async Task<IActionResult> GetByKey(string key)
    {
        await EnsureAuthorizedAsync();
        var item = await _db.SysParameters.AsNoTracking().FirstOrDefaultAsync(p => p.ParamKey == key && !p.IsDeleted)
            ?? throw BusinessException.NotFound("参数不存在");
        return Ok(ApiResponse<SysParameter>.Success(item));
    }

    /// <summary>新增参数（先授权，再校验既有持久化列与运营取值，失败不落任何行）</summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] SysParameter parameter)
    {
        if (parameter is null) throw BusinessException.InvalidParameter("请求内容不能为空");
        await EnsureAuthorizedAsync();
        parameter.Id = 0;
        SystemParameterAuthorizationRules.NormalizeForWrite(parameter);
        await SystemParameterAuthorizationRules.ValidateAsync(_db, parameter);
        parameter.CreatedAt = DateTime.Now;
        _db.SysParameters.Add(parameter);
        await _db.SaveChangesAsync();
        return Ok(ApiResponse<object>.Success(null, "参数新增成功"));
    }

    /// <summary>更新参数（按 Id）：先授权，再校验拟议参数，失败不改写任何参数行</summary>
    [HttpPut("{id:long}")]
    public async Task<IActionResult> Update(long id, [FromBody] SysParameter parameter)
    {
        if (parameter is null) throw BusinessException.InvalidParameter("请求内容不能为空");
        await EnsureAuthorizedAsync();
        var existing = await _db.SysParameters.FirstOrDefaultAsync(p => p.Id == id && !p.IsDeleted)
            ?? throw BusinessException.NotFound("参数不存在");
        parameter.Id = id;
        SystemParameterAuthorizationRules.NormalizeForWrite(parameter);
        await SystemParameterAuthorizationRules.ValidateAsync(_db, parameter);
        existing.ParamValue = parameter.ParamValue;
        existing.ParamName = parameter.ParamName;
        existing.Description = parameter.Description;
        existing.UpdatedAt = DateTime.Now;
        await _db.SaveChangesAsync();
        return Ok(ApiResponse<object>.Success(null, "参数更新成功"));
    }
}
