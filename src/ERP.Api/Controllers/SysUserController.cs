using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace ERP.Api.Controllers;

/// <summary>
/// 用户管理控制器（ERP-453 / ERP-463：为全部分页 / 按主键读取 / 新增 / 修改 / 切换状态 / 重置密码 / 删除路由
/// 补齐实时身份、账号状态、既有「用户管理」（<c>user</c>）功能菜单授权与有界字段 / 角色校验，
/// 并使该授权判定与请求路径 / 请求形状 / 是否绑定 <c>HttpContext</c> 完全无关）。
/// </summary>
/// <remarks>
/// 用户记录是每一次已认证请求、ERP-097 业务员数据范围与每一条运营权限判定共同解析的权威对象，
/// 因此每个路由在读取或写入任何 <c>SysUsers</c> / <c>SysUserRoles</c> 行之前都先经实时授权
/// （<b>不受请求路径是否赋值、空路径、请求形状或未绑定 <c>HttpContext</c> 影响</b>），
/// 新增 / 修改另经有界字段校验、角色 Id 解析校验与重置密码载荷校验；
/// 不新增任何菜单 / 权限 / 用户授权，也不改变既有用户名唯一索引语义、
/// <c>SeedData.AdminUserName</c> 内置管理员保护与 PBKDF2 密码哈希语义。
/// </remarks>
[ApiController]
[Route("api/sys/users")]
[Authorize]
public partial class SysUserController : ControllerBase
{
    private readonly IErpDbContext _db;

    public SysUserController(IErpDbContext db)
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
    /// 读取 / 写入前的实时身份 / 账号状态 / 既有「用户管理」菜单授权（ERP-453 / ERP-463，fail closed）。
    /// <para>与请求路径是否赋值、请求形状、以及控制器是否绑定 <c>HttpContext</c> <b>完全无关</b>：
    /// 空路径与已赋值路径口径完全一致；即便未绑定任何请求上下文（纯进程内直接调用）也照常执行本护栏，
    /// 缺失 / 零 / 已删除身份一律按未认证拒绝，禁用账号 / 撤销或缺少既有菜单一律按权限不足拒绝。
    /// 不读取环境变量、不区分数据库提供程序，也不存在任何测试专用放行开关，绝无匿名 / 管理员回退。
    /// 契约与边界见 <see cref="SysUserAuthorizationRules.PathIndependenceText"/>。</para>
    /// </summary>
    private async Task EnsureUserAuthorizedAsync()
    {
        await SysUserAuthorizationRules.EnsureAuthorizedAsync(_db, CurrentUserId());
    }

    /// <summary>分页查询用户</summary>
    [HttpGet]
    public async Task<IActionResult> GetPaged([FromQuery] PageQuery query)
    {
        await EnsureUserAuthorizedAsync();
        query.Normalize();
        var source = _db.SysUsers.AsNoTracking().Where(u => !u.IsDeleted);
        if (!string.IsNullOrWhiteSpace(query.Keyword))
            source = source.Where(u => u.UserName.Contains(query.Keyword) || u.DisplayName.Contains(query.Keyword));

        var total = await source.CountAsync();
        var users = await source.OrderByDescending(u => u.Id)
            .Skip((query.Page - 1) * query.PageSize).Take(query.PageSize).ToListAsync();

        var roleMap = await GetRoleMapAsync(users.Select(u => u.Id).ToList());
        var items = users.Select(u =>
        {
            var roles = roleMap.TryGetValue(u.Id, out var r) ? r : new List<(long, string)>();
            return new SysUserView
            {
                Id = u.Id, UserName = u.UserName, DisplayName = u.DisplayName,
                Email = u.Email, Phone = u.Phone, Avatar = u.Avatar, Status = u.Status,
                LastLoginTime = u.LastLoginTime, LastLoginIp = u.LastLoginIp,
                MustChangePassword = u.MustChangePassword, CreatedAt = u.CreatedAt,
                RoleIds = roles.Select(x => x.Item1).ToList(),
                RoleNames = roles.Select(x => x.Item2).ToList()
            };
        }).ToList();

        var paged = new PagedResult<SysUserView> { Items = items, Total = total, Page = query.Page, PageSize = query.PageSize };
        return Ok(ApiResponse<PagedResult<SysUserView>>.Success(paged));
    }

    /// <summary>创建用户（落库前先经实时授权与有界字段 / 角色校验；被拒绝时不落任何行）</summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] SysUserCreateRequest request)
    {
        await EnsureUserAuthorizedAsync();
        SysUserAuthorizationRules.ValidateCreate(request);
        if (await _db.SysUsers.AnyAsync(u => u.UserName == request.UserName && !u.IsDeleted))
            throw BusinessException.Duplicate("用户名已存在");

        // ERP-453：角色必须在创建用户**落库之前**解析为已知的非删除角色，否则整批拒绝、不落任何 SysUsers / SysUserRoles 行。
        await SysUserAuthorizationRules.EnsureRoleIdsResolvedAsync(_db, request.RoleIds);

        var salt = PasswordHasher.GenerateSalt();
        var user = new SysUser
        {
            UserName = request.UserName,
            PasswordSalt = salt,
            PasswordHash = PasswordHasher.HashPassword(request.Password, salt),
            DisplayName = request.DisplayName, Email = request.Email, Phone = request.Phone,
            Status = UserStatus.Enabled, MustChangePassword = true, CreatedAt = DateTime.Now
        };
        _db.SysUsers.Add(user);
        await _db.SaveChangesAsync();
        await SaveRolesAsync(user.Id, request.RoleIds);
        return Ok(ApiResponse<object>.Success(null, "用户创建成功"));
    }

    /// <summary>更新用户（落库前先经实时授权、有界字段校验与角色 Id 解析；被拒绝时不改写任何行）</summary>
    [HttpPut("{id:long}")]
    public async Task<IActionResult> Update(long id, [FromBody] SysUserUpdateRequest request)
    {
        await EnsureUserAuthorizedAsync();
        SysUserAuthorizationRules.ValidateUpdate(request);
        var user = await _db.SysUsers.FirstOrDefaultAsync(u => u.Id == id && !u.IsDeleted)
            ?? throw BusinessException.NotFound("用户不存在");
        // ERP-453：角色必须在改写用户字段与角色关联之前解析为已知的非删除角色，否则整批拒绝、零写入。
        await SysUserAuthorizationRules.EnsureRoleIdsResolvedAsync(_db, request.RoleIds);
        user.DisplayName = request.DisplayName;
        user.Email = request.Email;
        user.Phone = request.Phone;
        user.Status = request.Status;
        user.UpdatedAt = DateTime.Now;
        await _db.SaveChangesAsync();
        await SaveRolesAsync(id, request.RoleIds);
        return Ok(ApiResponse<object>.Success(null, "用户更新成功"));
    }

    /// <summary>获取用户详情（含角色；读取前先经实时授权）</summary>
    [HttpGet("{id:long}")]
    public async Task<IActionResult> GetById(long id)
    {
        await EnsureUserAuthorizedAsync();
        var user = await _db.SysUsers.FirstOrDefaultAsync(u => u.Id == id && !u.IsDeleted)
            ?? throw BusinessException.NotFound("用户不存在");

        var roleIds = await _db.SysUserRoles.Where(ur => ur.UserId == id && !ur.IsDeleted)
            .Select(ur => ur.RoleId).ToListAsync();
        var roleNames = await _db.SysRoles.Where(r => roleIds.Contains(r.Id))
            .Select(r => r.RoleName).ToListAsync();

        var view = new SysUserView
        {
            Id = user.Id,
            UserName = user.UserName,
            DisplayName = user.DisplayName,
            Email = user.Email,
            Phone = user.Phone,
            Avatar = user.Avatar,
            Status = user.Status,
            LastLoginTime = user.LastLoginTime,
            LastLoginIp = user.LastLoginIp,
            MustChangePassword = user.MustChangePassword,
            CreatedAt = user.CreatedAt,
            RoleIds = roleIds,
            RoleNames = roleNames
        };
        return Ok(ApiResponse<SysUserView>.Success(view));
    }

    /// <summary>切换用户启用/禁用状态（读写前先经实时授权；内置管理员保护不变）</summary>
    [HttpPost("{id:long}/toggle-status")]
    public async Task<IActionResult> ToggleStatus(long id)
    {
        await EnsureUserAuthorizedAsync();
        var user = await _db.SysUsers.FirstOrDefaultAsync(u => u.Id == id && !u.IsDeleted)
            ?? throw BusinessException.NotFound("用户不存在");
        if (user.UserName == SeedData.AdminUserName)
            throw BusinessException.RuleConflict("系统内置管理员不可禁用");
        user.Status = user.Status == UserStatus.Enabled ? UserStatus.Disabled : UserStatus.Enabled;
        user.UpdatedAt = DateTime.Now;
        await _db.SaveChangesAsync();
        return Ok(ApiResponse<object>.Success(null, user.Status == UserStatus.Enabled ? "已启用" : "已禁用"));
    }

    /// <summary>重置密码（读写前先经实时授权与有界密码校验；PBKDF2 哈希语义不变）</summary>
    [HttpPost("{id:long}/reset-password")]
    public async Task<IActionResult> ResetPassword(long id, [FromBody] ResetPasswordRequest request)
    {
        await EnsureUserAuthorizedAsync();
        SysUserAuthorizationRules.ValidateResetPassword(request);
        var user = await _db.SysUsers.FirstOrDefaultAsync(u => u.Id == id && !u.IsDeleted)
            ?? throw BusinessException.NotFound("用户不存在");
        user.PasswordSalt = PasswordHasher.GenerateSalt();
        user.PasswordHash = PasswordHasher.HashPassword(request.NewPassword, user.PasswordSalt);
        user.MustChangePassword = true;
        user.UpdatedAt = DateTime.Now;
        await _db.SaveChangesAsync();
        return Ok(ApiResponse<object>.Success(null, "密码重置成功"));
    }

    /// <summary>删除用户（软删除；读写前先经实时授权；内置管理员保护不变）</summary>
    [HttpDelete("{id:long}")]
    public async Task<IActionResult> Delete(long id)
    {
        await EnsureUserAuthorizedAsync();
        var user = await _db.SysUsers.FirstOrDefaultAsync(u => u.Id == id && !u.IsDeleted)
            ?? throw BusinessException.NotFound("用户不存在");
        if (user.UserName == SeedData.AdminUserName)
            throw BusinessException.RuleConflict("系统内置管理员不可删除");
        user.IsDeleted = true;
        user.UpdatedAt = DateTime.Now;
        await _db.SaveChangesAsync();
        return Ok(ApiResponse<object>.Success(null, "删除成功"));
    }
}
