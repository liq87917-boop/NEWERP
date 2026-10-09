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
/// 用户参数控制器（ERP-456）：分页 / 主键详情 / 新增 / 修改 / 删除在读取任何用户参数行或写入任何用户参数行
/// <b>之前</b>都先复核实时启用身份 + 既有「用户参数」（<c>user-parameter</c>）功能菜单
/// （见 <see cref="SysUserParameterAuthorizationRules"/>）；新增 / 修改另校验参数键 / 参数值的既有持久化边界
/// 与用户 Id 解析。缺失 / 禁用 / 已删除 / 撤销菜单的身份一律以既有受控非披露错误 fail closed，
/// 不新增任何授权，也不把空身份当作管理员。
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

    /// <summary>当前登录账号 Id（只来自已认证请求主体；缺失 / 非数字 / 非正返回 <c>null</c>，由规则层 fail closed）。</summary>
    private long? CurrentUserId()
    {
        var value = ControllerContext?.HttpContext?.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return long.TryParse(value, out var id) && id > 0 ? id : null;
    }

    /// <summary>用户参数入口授权（实时身份 + 账号状态 + 既有 user-parameter 菜单）。</summary>
    private Task EnsureAuthorizedAsync()
        => SysUserParameterAuthorizationRules.EnsureAuthorizedAsync(_db, CurrentUserId());

    /// <summary>分页查询用户参数（先授权，再按既有分页 / 响应契约返回）</summary>
    [HttpGet]
    public async Task<IActionResult> GetPaged([FromQuery] PageQuery query, [FromQuery] long? userId = null, [FromQuery] string? paramKey = null)
    {
        await EnsureAuthorizedAsync();
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

    /// <summary>根据主键获取用户参数（先授权，再返回既有详情契约）</summary>
    [HttpGet("{id:long}")]
    public async Task<IActionResult> GetById(long id)
    {
        await EnsureAuthorizedAsync();
        var item = await _db.SysUserParameters.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id && !p.IsDeleted)
            ?? throw BusinessException.NotFound("用户参数不存在");
        return Ok(ApiResponse<SysUserParameter>.Success(item));
    }

    /// <summary>新增用户参数（先授权，再校验有界字段 / 用户解析与既有重复键，失败不落任何行）</summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] SysUserParameter parameter)
    {
        if (parameter is null) throw BusinessException.InvalidParameter("请求内容不能为空");
        await EnsureAuthorizedAsync();

        parameter.Id = 0;
        SysUserParameterAuthorizationRules.NormalizeForWrite(parameter);
        await SysUserParameterAuthorizationRules.ValidateAsync(_db, parameter);

        // 既有按用户参数键唯一语义保持不变：未删除行之间同一用户不得重复参数键。
        if (await _db.SysUserParameters.AnyAsync(p => p.UserId == parameter.UserId && p.ParamKey == parameter.ParamKey && !p.IsDeleted))
            throw BusinessException.Duplicate(SysUserParameterAuthorizationRules.ParamKeyDuplicatedText);

        parameter.CreatedAt = DateTime.Now;
        _db.SysUserParameters.Add(parameter);
        await _db.SaveChangesAsync();
        return Ok(ApiResponse<object>.Success(null, "用户参数新增成功"));
    }

    /// <summary>更新用户参数（先授权，再校验有界字段 / 用户解析，失败不改写任何用户参数行）</summary>
    [HttpPut("{id:long}")]
    public async Task<IActionResult> Update(long id, [FromBody] SysUserParameter parameter)
    {
        if (parameter is null) throw BusinessException.InvalidParameter("请求内容不能为空");
        await EnsureAuthorizedAsync();

        var existing = await _db.SysUserParameters.FirstOrDefaultAsync(p => p.Id == id && !p.IsDeleted)
            ?? throw BusinessException.NotFound("用户参数不存在");

        parameter.Id = id;
        SysUserParameterAuthorizationRules.NormalizeForWrite(parameter);
        // 既有语义不改变行的归属：提交体未指定 UserId 时按既有行的所属用户校验；
        // 指定了 UserId（含未知 / 已删除）则必须以该 UserId 解析通过（fail closed）。
        if (parameter.UserId <= 0) parameter.UserId = existing.UserId;
        await SysUserParameterAuthorizationRules.ValidateAsync(_db, parameter);

        existing.ParamKey = parameter.ParamKey;
        existing.ParamValue = parameter.ParamValue;
        existing.UpdatedAt = DateTime.Now;
        await _db.SaveChangesAsync();
        return Ok(ApiResponse<object>.Success(null, "用户参数更新成功"));
    }

    /// <summary>删除用户参数（先授权，再按既有软删除语义标记 IsDeleted）</summary>
    [HttpDelete("{id:long}")]
    public async Task<IActionResult> Delete(long id)
    {
        await EnsureAuthorizedAsync();
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
