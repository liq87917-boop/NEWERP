using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ERP.Api.Controllers;

/// <summary>
/// 系统参数控制器
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

    /// <summary>分页查询系统参数</summary>
    [HttpGet]
    public async Task<IActionResult> GetAll([FromQuery] PageQuery query)
    {
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

    /// <summary>根据主键获取参数</summary>
    [HttpGet("{id:long}")]
    public async Task<IActionResult> GetById(long id)
    {
        var item = await _db.SysParameters.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id && !p.IsDeleted)
            ?? throw BusinessException.NotFound("参数不存在");
        return Ok(ApiResponse<SysParameter>.Success(item));
    }

    /// <summary>根据键获取参数值</summary>
    [HttpGet("key/{key}")]
    public async Task<IActionResult> GetByKey(string key)
    {
        var item = await _db.SysParameters.AsNoTracking().FirstOrDefaultAsync(p => p.ParamKey == key && !p.IsDeleted)
            ?? throw BusinessException.NotFound("参数不存在");
        return Ok(ApiResponse<SysParameter>.Success(item));
    }

    /// <summary>新增参数</summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] SysParameter parameter)
    {
        if (await _db.SysParameters.AnyAsync(p => p.ParamKey == parameter.ParamKey && !p.IsDeleted))
            throw BusinessException.Duplicate("参数键已存在");
        parameter.Id = 0;
        parameter.CreatedAt = DateTime.Now;
        _db.SysParameters.Add(parameter);
        await _db.SaveChangesAsync();
        return Ok(ApiResponse<object>.Success(null, "参数新增成功"));
    }

    /// <summary>更新参数（按 Id）</summary>
    [HttpPut("{id:long}")]
    public async Task<IActionResult> Update(long id, [FromBody] SysParameter parameter)
    {
        var existing = await _db.SysParameters.FirstOrDefaultAsync(p => p.Id == id && !p.IsDeleted)
            ?? throw BusinessException.NotFound("参数不存在");
        existing.ParamValue = parameter.ParamValue;
        existing.ParamName = parameter.ParamName;
        existing.Description = parameter.Description;
        existing.UpdatedAt = DateTime.Now;
        await _db.SaveChangesAsync();
        return Ok(ApiResponse<object>.Success(null, "参数更新成功"));
    }
}
