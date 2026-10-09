using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace ERP.Api.Controllers;

/// <summary>
/// 角色管理控制器（ERP-454：为分页 / 全部 / 按主键读取 / 角色菜单 / 新增 / 修改 / 删除路由
/// 补齐实时身份、账号状态、既有「角色管理」（<c>role</c>）功能菜单授权、有界字段校验与受控菜单分配）。
/// </summary>
/// <remarks>
/// 角色 → 菜单绑定是每一条运营路由菜单授权所读取的权威来源，因此每个路由在读取或写入任何
/// <c>SysRoles</c> / <c>SysRoleMenus</c> 行之前都先经实时授权；新增 / 修改另经有界字段校验与
/// 菜单 Id 解析校验，任何被分配的菜单 Id 都必须解析为已知的非删除菜单；
/// 不新增任何菜单 / 权限 / 用户授权，也不改变既有角色编码唯一索引语义、
/// <c>IsSystem</c> 内置角色删除保护与「全删全建」角色菜单替换语义。
/// </remarks>
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

    /// <summary>当前登录用户 Id（只来自已认证请求主体；缺失 / 非数字 / 非正返回 null，由实时授权护栏 fail closed）</summary>
    private long? CurrentUserId()
    {
        var value = ControllerContext?.HttpContext?.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return long.TryParse(value, out var id) && id > 0 ? id : null;
    }

    /// <summary>
    /// 是否需要执行实时授权（与仓库既有口径同源）：真实 HTTP 请求（MVC 绑定，<c>Request.Path</c> 已赋值）
    /// 一律执行；仅「未进入 HTTP 请求管线」的<b>进程内直接调用</b>（历史单元测试 / 内部派生读取，
    /// 无请求路径，不可能由外部请求到达）沿用既有语义，绝不把缺失身份当作管理员。
    /// </summary>
    private bool RequiresLiveAuthorization()
    {
        var http = ControllerContext?.HttpContext;
        return http?.Request.Path.HasValue == true;
    }

    /// <summary>读取 / 写入前的实时身份 / 账号状态 / 既有「角色管理」菜单授权（ERP-454，fail closed）</summary>
    private async Task EnsureRoleAuthorizedAsync()
    {
        if (RequiresLiveAuthorization())
            await RoleAuthorizationRules.EnsureAuthorizedAsync(_db, CurrentUserId());
    }

    /// <summary>分页查询角色</summary>
    [HttpGet]
    public async Task<IActionResult> GetPaged([FromQuery] PageQuery query)
    {
        await EnsureRoleAuthorizedAsync();
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
        await EnsureRoleAuthorizedAsync();
        var items = await _db.SysRoles.AsNoTracking().Where(r => !r.IsDeleted).OrderBy(r => r.Id).ToListAsync();
        return Ok(ApiResponse<List<SysRole>>.Success(items));
    }

    /// <summary>获取角色分配的菜单 Id 集合</summary>
    [HttpGet("{id:long}/menus")]
    public async Task<IActionResult> GetRoleMenus(long id)
    {
        await EnsureRoleAuthorizedAsync();
        var menuIds = await _db.SysRoleMenus.Where(rm => rm.RoleId == id && !rm.IsDeleted)
            .Select(rm => rm.MenuId).ToListAsync();
        return Ok(ApiResponse<List<long>>.Success(menuIds));
    }

    /// <summary>获取角色详情</summary>
    [HttpGet("{id:long}")]
    public async Task<IActionResult> GetById(long id)
    {
        await EnsureRoleAuthorizedAsync();
        var role = await _db.SysRoles.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id && !r.IsDeleted)
            ?? throw BusinessException.NotFound("角色不存在");
        return Ok(ApiResponse<SysRole>.Success(role));
    }

    /// <summary>创建角色</summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] RoleRequest request)
    {
        await EnsureRoleAuthorizedAsync();
        RoleAuthorizationRules.Validate(request);
        if (await _db.SysRoles.AnyAsync(r => r.RoleCode == request.RoleCode && !r.IsDeleted))
            throw BusinessException.Duplicate("角色编码已存在");

        // ERP-454：菜单必须在创建角色**落库之前**解析为已知的非删除菜单，否则整批拒绝、不落任何 SysRoles / SysRoleMenus 行。
        await RoleAuthorizationRules.EnsureMenuIdsResolvedAsync(_db, request.MenuIds);

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
        await EnsureRoleAuthorizedAsync();
        RoleAuthorizationRules.Validate(request);
        var role = await _db.SysRoles.FirstOrDefaultAsync(r => r.Id == id && !r.IsDeleted)
            ?? throw BusinessException.NotFound("角色不存在");

        // ERP-454：菜单必须在改写角色字段与菜单关联之前解析为已知的非删除菜单，否则整批拒绝、零写入。
        await RoleAuthorizationRules.EnsureMenuIdsResolvedAsync(_db, request.MenuIds);

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
        await EnsureRoleAuthorizedAsync();
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
        // ERP-454：写入任何 SysRoleMenus 行之前，逐一解析菜单 Id 为已知的非删除菜单；否则整批拒绝、不留下半写关联。
        await RoleAuthorizationRules.EnsureMenuIdsResolvedAsync(_db, menuIds);

        var existing = await _db.SysRoleMenus.Where(rm => rm.RoleId == roleId).ToListAsync();
        _db.SysRoleMenus.RemoveRange(existing);
        foreach (var menuId in menuIds.Distinct())
            _db.SysRoleMenus.Add(new SysRoleMenu { RoleId = roleId, MenuId = menuId, CreatedAt = DateTime.Now });
        await _db.SaveChangesAsync();
    }
}
