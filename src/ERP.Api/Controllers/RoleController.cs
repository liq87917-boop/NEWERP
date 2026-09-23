using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ERP.Api.Controllers;

/// <summary>
/// 角色管理控制器
/// </summary>
[ApiController]
[Route("api/sys/roles")]
[Authorize]
public class RoleController : ControllerBase
{
    private readonly IErpDbContext _db;

    public RoleController(IErpDbContext db)
    {
        _db = db;
    }

    /// <summary>分页查询角色</summary>
    [HttpGet]
    public async Task<IActionResult> GetPaged([FromQuery] PageQuery query)
    {
        query.Normalize();
        var source = _db.SysRoles.AsNoTracking().Where(r => !r.IsDeleted);
        if (!string.IsNullOrWhiteSpace(query.Keyword))
            source = source.Where(r => r.RoleName.Contains(query.Keyword) || r.RoleCode.Contains(query.Keyword));

        var total = await source.CountAsync();
        var items = await source.OrderByDescending(r => r.Id)
            .Skip((query.Page - 1) * query.PageSize).Take(query.PageSize).ToListAsync();

        return Ok(ApiResponse<PagedResult<SysRole>>.Success(
            new PagedResult<SysRole> { Items = items, Total = total, Page = query.Page, PageSize = query.PageSize }));
    }

    /// <summary>查询全部角色</summary>
    [HttpGet("all")]
    public async Task<IActionResult> GetAll()
    {
        var items = await _db.SysRoles.AsNoTracking().Where(r => !r.IsDeleted).OrderBy(r => r.Id).ToListAsync();
        return Ok(ApiResponse<List<SysRole>>.Success(items));
    }

    /// <summary>获取角色分配的菜单 Id 集合</summary>
    [HttpGet("{id:long}/menus")]
    public async Task<IActionResult> GetRoleMenus(long id)
    {
        var menuIds = await _db.SysRoleMenus.Where(rm => rm.RoleId == id && !rm.IsDeleted)
            .Select(rm => rm.MenuId).ToListAsync();
        return Ok(ApiResponse<List<long>>.Success(menuIds));
    }

    /// <summary>获取角色详情</summary>
    [HttpGet("{id:long}")]
    public async Task<IActionResult> GetById(long id)
    {
        var role = await _db.SysRoles.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id && !r.IsDeleted)
            ?? throw BusinessException.NotFound("角色不存在");
        return Ok(ApiResponse<SysRole>.Success(role));
    }

    /// <summary>创建角色</summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] RoleRequest request)
    {
        if (await _db.SysRoles.AnyAsync(r => r.RoleCode == request.RoleCode && !r.IsDeleted))
            throw BusinessException.Duplicate("角色编码已存在");

        var role = new SysRole
        {
            RoleName = request.RoleName,
            RoleCode = request.RoleCode,
            Description = request.Description,
            IsSystem = false,
            CreatedAt = DateTime.Now
        };
        _db.SysRoles.Add(role);
        await _db.SaveChangesAsync();
        await SaveRoleMenusAsync(role.Id, request.MenuIds);
        return Ok(ApiResponse<object>.Success(null, "角色创建成功"));
    }

    /// <summary>更新角色</summary>
    [HttpPut("{id:long}")]
    public async Task<IActionResult> Update(long id, [FromBody] RoleRequest request)
    {
        var role = await _db.SysRoles.FirstOrDefaultAsync(r => r.Id == id && !r.IsDeleted)
            ?? throw BusinessException.NotFound("角色不存在");

        role.RoleName = request.RoleName;
        role.Description = request.Description;
        role.UpdatedAt = DateTime.Now;
        await _db.SaveChangesAsync();
        await SaveRoleMenusAsync(id, request.MenuIds);
        return Ok(ApiResponse<object>.Success(null, "角色更新成功"));
    }

    /// <summary>删除角色</summary>
    [HttpDelete("{id:long}")]
    public async Task<IActionResult> Delete(long id)
    {
        var role = await _db.SysRoles.FirstOrDefaultAsync(r => r.Id == id && !r.IsDeleted)
            ?? throw BusinessException.NotFound("角色不存在");
        if (role.IsSystem)
            throw BusinessException.RuleConflict("系统内置角色不可删除");

        role.IsDeleted = true;
        role.UpdatedAt = DateTime.Now;
        await _db.SaveChangesAsync();
        return Ok(ApiResponse<object>.Success(null, "删除成功"));
    }

    private async Task SaveRoleMenusAsync(long roleId, List<long> menuIds)
    {
        var existing = await _db.SysRoleMenus.Where(rm => rm.RoleId == roleId).ToListAsync();
        _db.SysRoleMenus.RemoveRange(existing);
        foreach (var menuId in menuIds.Distinct())
            _db.SysRoleMenus.Add(new SysRoleMenu { RoleId = roleId, MenuId = menuId, CreatedAt = DateTime.Now });
        await _db.SaveChangesAsync();
    }
}
