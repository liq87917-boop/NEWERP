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

namespace ERP.Api.Controllers;

/// <summary>
/// 用户管理控制器
/// </summary>
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

    /// <summary>分页查询用户</summary>
    [HttpGet]
    public async Task<IActionResult> GetPaged([FromQuery] PageQuery query)
    {
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

    /// <summary>创建用户</summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] SysUserCreateRequest request)
    {
        if (await _db.SysUsers.AnyAsync(u => u.UserName == request.UserName && !u.IsDeleted))
            throw BusinessException.Duplicate("用户名已存在");

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

    /// <summary>更新用户</summary>
    [HttpPut("{id:long}")]
    public async Task<IActionResult> Update(long id, [FromBody] SysUserUpdateRequest request)
    {
        var user = await _db.SysUsers.FirstOrDefaultAsync(u => u.Id == id && !u.IsDeleted)
            ?? throw BusinessException.NotFound("用户不存在");
        user.DisplayName = request.DisplayName;
        user.Email = request.Email;
        user.Phone = request.Phone;
        user.Status = request.Status;
        user.UpdatedAt = DateTime.Now;
        await _db.SaveChangesAsync();
        await SaveRolesAsync(id, request.RoleIds);
        return Ok(ApiResponse<object>.Success(null, "用户更新成功"));
    }

    /// <summary>获取用户详情（含角色）</summary>
    [HttpGet("{id:long}")]
    public async Task<IActionResult> GetById(long id)
    {
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

    /// <summary>切换用户启用/禁用状态</summary>
    [HttpPost("{id:long}/toggle-status")]
    public async Task<IActionResult> ToggleStatus(long id)
    {
        var user = await _db.SysUsers.FirstOrDefaultAsync(u => u.Id == id && !u.IsDeleted)
            ?? throw BusinessException.NotFound("用户不存在");
        if (user.UserName == SeedData.AdminUserName)
            throw BusinessException.RuleConflict("系统内置管理员不可禁用");
        user.Status = user.Status == UserStatus.Enabled ? UserStatus.Disabled : UserStatus.Enabled;
        user.UpdatedAt = DateTime.Now;
        await _db.SaveChangesAsync();
        return Ok(ApiResponse<object>.Success(null, user.Status == UserStatus.Enabled ? "已启用" : "已禁用"));
    }

    /// <summary>重置密码</summary>
    [HttpPost("{id:long}/reset-password")]
    public async Task<IActionResult> ResetPassword(long id, [FromBody] ResetPasswordRequest request)
    {
        var user = await _db.SysUsers.FirstOrDefaultAsync(u => u.Id == id && !u.IsDeleted)
            ?? throw BusinessException.NotFound("用户不存在");
        user.PasswordSalt = PasswordHasher.GenerateSalt();
        user.PasswordHash = PasswordHasher.HashPassword(request.NewPassword, user.PasswordSalt);
        user.MustChangePassword = true;
        user.UpdatedAt = DateTime.Now;
        await _db.SaveChangesAsync();
        return Ok(ApiResponse<object>.Success(null, "密码重置成功"));
    }

    /// <summary>删除用户（软删除）</summary>
    [HttpDelete("{id:long}")]
    public async Task<IActionResult> Delete(long id)
    {
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
