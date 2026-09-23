using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Domain.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ERP.Api.Controllers;

/// <summary>
/// 通用 CRUD 控制器基类：适用于基础资料等简单实体，统一分页/增删改查路由
/// </summary>
[ApiController]
[Authorize]
public abstract class BaseCrudController<TEntity> : ControllerBase where TEntity : BaseEntity
{
    protected readonly IGenericService<TEntity> Service;

    protected BaseCrudController(IGenericService<TEntity> service)
    {
        Service = service;
    }

    /// <summary>分页查询</summary>
    [HttpGet]
    public async Task<IActionResult> GetPaged([FromQuery] PageQuery query)
    {
        var result = await Service.GetPagedAsync(query);
        return Ok(ApiResponse<PagedResult<TEntity>>.Success(result));
    }

    /// <summary>查询全部（供下拉框等使用）</summary>
    [HttpGet("all")]
    public async Task<IActionResult> GetAll()
    {
        var result = await Service.GetAllAsync();
        return Ok(ApiResponse<List<TEntity>>.Success(result));
    }

    /// <summary>根据主键获取</summary>
    [HttpGet("{id:long}")]
    public async Task<IActionResult> GetById(long id)
    {
        var result = await Service.GetByIdAsync(id);
        return Ok(ApiResponse<TEntity>.Success(result));
    }

    /// <summary>新增</summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] TEntity entity)
    {
        var result = await Service.CreateAsync(entity);
        return Ok(ApiResponse<TEntity>.Success(result, "新增成功"));
    }

    /// <summary>更新</summary>
    [HttpPut("{id:long}")]
    public async Task<IActionResult> Update(long id, [FromBody] TEntity entity)
    {
        entity.Id = id;
        var result = await Service.UpdateAsync(entity);
        return Ok(ApiResponse<TEntity>.Success(result, "更新成功"));
    }

    /// <summary>删除（软删除）</summary>
    [HttpDelete("{id:long}")]
    public async Task<IActionResult> Delete(long id)
    {
        await Service.DeleteAsync(id);
        return Ok(ApiResponse<object>.Success(null, "删除成功"));
    }

    /// <summary>批量删除（软删除）</summary>
    [HttpPost("batch-delete")]
    public async Task<IActionResult> BatchDelete([FromBody] List<long> ids)
    {
        await Service.BatchDeleteAsync(ids);
        return Ok(ApiResponse<object>.Success(null, "批量删除成功"));
    }
}
