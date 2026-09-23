using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ERP.Api.Controllers;

/// <summary>
/// 用户参数控制器：用户级个性化配置的增删改查
/// </summary>
[ApiController]
[Route("api/sys/user-parameters")]
[Authorize]
public class UserParameterController : ControllerBase
{
    private readonly IErpDbContext _db;

    public UserParameterController(IErpDbContext db)
    {
        _db = db;
    }

    /// <summary>分页查询用户参数（支持按用户、关键字筛选）</summary>
    [HttpGet]
    public async Task<IActionResult> GetPaged([FromQuery] PageQuery query, [FromQuery] long? userId, [FromQuery] string? paramKey)
    {
        query.Normalize();
        var source = _db.SysUserParameters.AsNoTracking().Where(p => !p.IsDeleted);
        if (userId.HasValue) source = source.Where(p => p.UserId == userId.Value);
        if (!string.IsNullOrWhiteSpace(paramKey)) source = source.Where(p => p.ParamKey.Contains(paramKey));
        if (!string.IsNullOrWhiteSpace(query.Keyword)) source = source.Where(p => p.ParamKey.Contains(query.Keyword) || p.ParamValue.Contains(query.Keyword));

        var total = await source.CountAsync();
        var items = await source.OrderByDescending(p => p.Id)
            .Skip((query.Page - 1) * query.PageSize).Take(query.PageSize).ToListAsync();

        // 关联用户显示名称
        var userIds = items.Select(p => p.UserId).Distinct().ToList();
        var users = await _db.SysUsers.Where(u => userIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.DisplayName);

        var views = items.Select(p => new UserParameterView
        {
            Id = p.Id,
            UserId = p.UserId,
            UserName = users.TryGetValue(p.UserId, out var name) ? name : string.Empty,
            ParamKey = p.ParamKey,
            ParamValue = p.ParamValue,
            UpdatedAt = p.UpdatedAt
        }).ToList();

        return Ok(ApiResponse<PagedResult<UserParameterView>>.Success(
            new PagedResult<UserParameterView> { Items = views, Total = total, Page = query.Page, PageSize = query.PageSize }));
    }

    /// <summary>根据主键获取用户参数</summary>
    [HttpGet("{id:long}")]
    public async Task<IActionResult> GetById(long id)
    {
        var item = await _db.SysUserParameters.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id && !p.IsDeleted)
            ?? throw BusinessException.NotFound("用户参数不存在");
        return Ok(ApiResponse<SysUserParameter>.Success(item));
    }

    /// <summary>新增用户参数</summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] SysUserParameter parameter)
    {
        if (await _db.SysUserParameters.AnyAsync(p => p.UserId == parameter.UserId && p.ParamKey == parameter.ParamKey && !p.IsDeleted))
            throw BusinessException.Duplicate("该用户下参数键已存在");

        parameter.Id = 0;
        parameter.CreatedAt = DateTime.Now;
        _db.SysUserParameters.Add(parameter);
        await _db.SaveChangesAsync();
        return Ok(ApiResponse<object>.Success(null, "用户参数新增成功"));
    }

    /// <summary>更新用户参数</summary>
    [HttpPut("{id:long}")]
    public async Task<IActionResult> Update(long id, [FromBody] SysUserParameter parameter)
    {
        var existing = await _db.SysUserParameters.FirstOrDefaultAsync(p => p.Id == id && !p.IsDeleted)
            ?? throw BusinessException.NotFound("用户参数不存在");

        existing.ParamKey = parameter.ParamKey;
        existing.ParamValue = parameter.ParamValue;
        existing.UpdatedAt = DateTime.Now;
        await _db.SaveChangesAsync();
        return Ok(ApiResponse<object>.Success(null, "用户参数更新成功"));
    }

    /// <summary>删除用户参数（软删除）</summary>
    [HttpDelete("{id:long}")]
    public async Task<IActionResult> Delete(long id)
    {
        var item = await _db.SysUserParameters.FirstOrDefaultAsync(p => p.Id == id && !p.IsDeleted)
            ?? throw BusinessException.NotFound("用户参数不存在");
        item.IsDeleted = true;
        item.UpdatedAt = DateTime.Now;
        await _db.SaveChangesAsync();
        return Ok(ApiResponse<object>.Success(null, "删除成功"));
    }
}

/// <summary>用户参数视图（含用户名显示）</summary>
public class UserParameterView
{
    public long Id { get; set; }
    public long UserId { get; set; }
    public string UserName { get; set; } = string.Empty;
    public string ParamKey { get; set; } = string.Empty;
    public string ParamValue { get; set; } = string.Empty;
    public DateTime? UpdatedAt { get; set; }
}
