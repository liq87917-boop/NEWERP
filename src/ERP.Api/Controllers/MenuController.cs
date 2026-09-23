using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ERP.Api.Controllers;

/// <summary>
/// 菜单管理控制器
/// </summary>
[ApiController]
[Route("api/sys/menus")]
[Authorize]
public class MenuController : ControllerBase
{
    private readonly IErpDbContext _db;

    public MenuController(IErpDbContext db)
    {
        _db = db;
    }

    /// <summary>获取菜单树（用于角色授权界面）</summary>
    [HttpGet("tree")]
    public async Task<IActionResult> GetTree()
    {
        var menus = await _db.SysMenus.AsNoTracking().Where(m => !m.IsDeleted)
            .OrderBy(m => m.SortOrder).ToListAsync();
        var nodes = menus.Select(m => new MenuTreeNode
        {
            Id = m.Id, ParentId = m.ParentId, MenuName = m.MenuName,
            MenuCode = m.MenuCode, Path = m.Path, Icon = m.Icon,
            SortOrder = m.SortOrder, MenuType = (int)m.MenuType
        }).ToList();
        return Ok(ApiResponse<List<MenuTreeNode>>.Success(BuildTree(nodes, 0)));
    }

    /// <summary>创建菜单</summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] SysMenu menu)
    {
        if (await _db.SysMenus.AnyAsync(m => m.MenuCode == menu.MenuCode && !m.IsDeleted))
            throw BusinessException.Duplicate("菜单编码已存在");
        menu.Id = 0;
        menu.CreatedAt = DateTime.Now;
        _db.SysMenus.Add(menu);
        await _db.SaveChangesAsync();
        return Ok(ApiResponse<object>.Success(null, "菜单创建成功"));
    }

    /// <summary>更新菜单</summary>
    [HttpPut("{id:long}")]
    public async Task<IActionResult> Update(long id, [FromBody] SysMenu menu)
    {
        var existing = await _db.SysMenus.FirstOrDefaultAsync(m => m.Id == id && !m.IsDeleted)
            ?? throw BusinessException.NotFound("菜单不存在");
        existing.ParentId = menu.ParentId;
        existing.MenuName = menu.MenuName;
        existing.Path = menu.Path;
        existing.Icon = menu.Icon;
        existing.SortOrder = menu.SortOrder;
        existing.MenuType = menu.MenuType;
        existing.PermissionCode = menu.PermissionCode;
        existing.UpdatedAt = DateTime.Now;
        await _db.SaveChangesAsync();
        return Ok(ApiResponse<object>.Success(null, "菜单更新成功"));
    }

    /// <summary>删除菜单</summary>
    [HttpDelete("{id:long}")]
    public async Task<IActionResult> Delete(long id)
    {
        var hasChild = await _db.SysMenus.AnyAsync(m => m.ParentId == id && !m.IsDeleted);
        if (hasChild)
            throw BusinessException.RuleConflict("存在子菜单，请先删除子菜单");

        var menu = await _db.SysMenus.FirstOrDefaultAsync(m => m.Id == id && !m.IsDeleted)
            ?? throw BusinessException.NotFound("菜单不存在");
        menu.IsDeleted = true;
        menu.UpdatedAt = DateTime.Now;
        await _db.SaveChangesAsync();
        return Ok(ApiResponse<object>.Success(null, "删除成功"));
    }

    private static List<MenuTreeNode> BuildTree(List<MenuTreeNode> all, long parentId)
    {
        return all.Where(n => n.ParentId == parentId).OrderBy(n => n.SortOrder)
            .Select(n => { n.Children = BuildTree(all, n.Id); return n; }).ToList();
    }
}

/// <summary>菜单树节点</summary>
public class MenuTreeNode
{
    public long Id { get; set; }
    public long ParentId { get; set; }
    public string MenuName { get; set; } = string.Empty;
    public string MenuCode { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string Icon { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public int MenuType { get; set; }
    public List<MenuTreeNode> Children { get; set; } = new();
}
