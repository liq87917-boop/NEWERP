using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace ERP.Api.Controllers;

/// <summary>
/// 菜单管理控制器（ERP-455 / ERP-492：为读树 / 新增 / 修改 / 删除路由补齐实时身份、账号状态、
/// 既有功能菜单授权、有界字段校验与父级完整性，菜单编码是每一条运营路由菜单授权所校验的权威）。
/// </summary>
/// <remarks>
/// 菜单（<c>SysMenu</c>）定义每一条运营路由授权所校验的菜单编码、也是
/// <c>AuthService</c> 为每个登录账号构建菜单树时读取的来源，因此<b>每一个入口都无条件</b>在读取或写入任何
/// <c>SysMenus</c> 行之前先经实时授权：读树接受既有「用户权限」（<c>user-permission</c>）或
/// 既有「角色管理」（<c>role</c>）任一，新增 / 修改 / 删除只接受既有「用户权限」；
/// 新增 / 修改另经有界字段校验，非零父级必须在落库前解析为已知的非删除菜单；
/// 不新增任何菜单 / 权限 / 用户授权，也不改变既有菜单编码唯一性判定与「存在子菜单不可删除」语义。
/// <para>ERP-492：原「请求形状探针」（依据请求路径是否赋值判定是否需要授权）与它的两个条件护栏已<b>彻底移除</b> ——
/// 授权判定与请求路径是否赋值、请求形状、以及控制器是否绑定
/// <c>HttpContext</c> <b>完全无关</b>：空路径、已赋值路径与未绑定任何请求上下文的纯进程内直调对同一身份给出
/// 完全相同的判定。缺失 / 为零 / 未知 / 已删除身份一律未认证，禁用账号 / 缺少或被撤销既有菜单一律权限不足；
/// 无匿名 / 管理员 / 环境 / 测试专用开关回退。契约见 <see cref="SysMenuAuthorizationRules.PathIndependenceText"/>。</para>
/// </remarks>
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

    /// <summary>当前登录用户 Id（只来自已认证请求主体；缺失 / 非数字 / 非正返回 null，由实时授权护栏 fail closed）</summary>
    private long? CurrentUserId()
    {
        var value = ControllerContext?.HttpContext?.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return long.TryParse(value, out var id) && id > 0 ? id : null;
    }

    /// <summary>
    /// 读取菜单树前的实时身份 / 账号状态 / 既有「用户权限」或「角色管理」菜单授权（ERP-455 / ERP-492，无条件 fail closed）。
    /// <para>ERP-492：不再存在任何请求形状探针 —— 是否绑定 <c>HttpContext</c>、请求路径是否赋值、
    /// 当前是否携带可解析身份都不影响判定；缺失 / 为零 / 未知 / 已删除身份一律未认证。</para>
    /// </summary>
    private async Task EnsureMenuReadAuthorizedAsync()
    {
        // ERP-492：无条件执行（请求形状探针与请求路径判定已彻底移除）。
        await SysMenuAuthorizationRules.EnsureReadAuthorizedAsync(_db, CurrentUserId());
    }

    /// <summary>
    /// 新增 / 修改 / 删除前的实时身份 / 账号状态 / 既有「用户权限」菜单授权（ERP-455 / ERP-492，无条件 fail closed）。
    /// <para>ERP-492：与读树同一口径，写入入口一律无条件授权，绝不因空路径 / 缺失身份 / 未绑定请求上下文
    /// 而退回无授权的服务直调。</para>
    /// </summary>
    private async Task EnsureMenuWriteAuthorizedAsync()
    {
        // ERP-492：无条件执行（请求形状探针与请求路径判定已彻底移除）。
        await SysMenuAuthorizationRules.EnsureWriteAuthorizedAsync(_db, CurrentUserId());
    }

    /// <summary>获取菜单树（用于角色授权界面）</summary>
    [HttpGet("tree")]
    public async Task<IActionResult> GetTree()
    {
        await EnsureMenuReadAuthorizedAsync();
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
        await EnsureMenuWriteAuthorizedAsync();
        SysMenuAuthorizationRules.Validate(menu);
        // ERP-455：非零父级必须在落库之前解析为已知的非删除菜单，否则整批拒绝、不落任何 SysMenus 行。
        await SysMenuAuthorizationRules.EnsureParentResolvedAsync(_db, menu.ParentId);
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
        await EnsureMenuWriteAuthorizedAsync();
        var existing = await _db.SysMenus.FirstOrDefaultAsync(m => m.Id == id && !m.IsDeleted)
            ?? throw BusinessException.NotFound("菜单不存在");
        SysMenuAuthorizationRules.Validate(menu);
        // ERP-455：非零父级必须在改写任何菜单字段之前解析为已知的非删除菜单，否则整批拒绝、不改写任何行。
        await SysMenuAuthorizationRules.EnsureParentResolvedAsync(_db, menu.ParentId);
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
        await EnsureMenuWriteAuthorizedAsync();
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
