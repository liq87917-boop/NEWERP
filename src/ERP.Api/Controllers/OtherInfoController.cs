using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace ERP.Api.Controllers;

/// <summary>
/// 其他资料控制器（币种、港口、货代、唛头等）
/// <para>阶段 3（ERP-443）收口：分页 / 全部 / 详情 / 按类型查询 / 新增 / 修改 / 删除 / 批量删除在读取任何计数或写入
/// 任何字典行<b>之前</b>，都先复核实时启用身份 + 既有「其他资料」（<c>other-info</c>）功能菜单
/// （<see cref="OtherInfoAuthorizationRules"/>）；新增 / 修改另校验 InfoType 已知有界字典类型、编码 / 名称非空且有界、
/// 英文名称 / 备注有界与状态取值。缺失 / 禁用 / 已删除 / 撤销菜单的身份一律以既有受控非披露错误 fail closed，
/// 不新增任何授权，也不把空身份当作管理员。</para>
/// </summary>
[ApiController]
[Route("api/base/other-infos")]
[Authorize]
public class OtherInfoController : BaseCrudController<BaseOtherInfo>
{
    private readonly IErpDbContext _db;

    public OtherInfoController(IGenericService<BaseOtherInfo> service, IErpDbContext db) : base(service)
    {
        _db = db;
    }

    /// <summary>当前登录账号 Id（只来自已认证请求主体；缺失 / 非数字 / 非正返回 <c>null</c>，由规则层 fail closed）。</summary>
    private long? CurrentUserId()
    {
        var value = ControllerContext?.HttpContext?.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return long.TryParse(value, out var id) && id > 0 ? id : null;
    }

    /// <summary>其他资料入口授权（实时身份 + 既有 other-info 菜单）。</summary>
    private Task EnsureAuthorizedAsync()
        => OtherInfoAuthorizationRules.EnsureAuthorizedAsync(_db, CurrentUserId());

    /// <summary>分页查询：先授权，再按既有分页 / 响应契约返回。</summary>
    [HttpGet]
    public override async Task<IActionResult> GetPaged([FromQuery] PageQuery query)
    {
        await EnsureAuthorizedAsync();
        var result = await Service.GetPagedAsync(query);
        return Ok(ApiResponse<PagedResult<BaseOtherInfo>>.Success(result));
    }

    /// <summary>查询全部（供下拉框等使用）：先授权，再返回既有全量契约。</summary>
    [HttpGet("all")]
    public override async Task<IActionResult> GetAll()
    {
        await EnsureAuthorizedAsync();
        var result = await Service.GetAllAsync();
        return Ok(ApiResponse<List<BaseOtherInfo>>.Success(result));
    }

    /// <summary>根据主键获取：先授权，再返回既有详情契约。</summary>
    [HttpGet("{id:long}")]
    public override async Task<IActionResult> GetById(long id)
    {
        await EnsureAuthorizedAsync();
        var result = await Service.GetByIdAsync(id);
        return Ok(ApiResponse<BaseOtherInfo>.Success(result));
    }

    /// <summary>按资料类型查询（如 Currency/Port/Forwarder/ShippingMark）：先授权，再按既有筛选口径返回。</summary>
    [HttpGet("by-type/{infoType}")]
    public async Task<IActionResult> GetByType(string infoType)
    {
        await EnsureAuthorizedAsync();
        var items = await Service.GetAllAsync(o => o.InfoType == infoType && o.Status == 1);
        return Ok(ApiResponse<List<BaseOtherInfo>>.Success(items));
    }

    /// <summary>新增：写入任何字段之前完成授权与 InfoType / 编码 / 名称 / 英文名称 / 备注 / 状态校验。</summary>
    [HttpPost]
    public override async Task<IActionResult> Create([FromBody] BaseOtherInfo entity)
    {
        if (entity is null) throw BusinessException.InvalidParameter("请求内容不能为空");
        entity.Id = 0;
        await EnsureAuthorizedAsync();
        OtherInfoAuthorizationRules.Validate(entity);
        var result = await Service.CreateAsync(entity);
        return Ok(ApiResponse<BaseOtherInfo>.Success(result, "新增成功"));
    }

    /// <summary>修改：先授权，再校验拟议字段，失败不落任何改写。</summary>
    [HttpPut("{id:long}")]
    public override async Task<IActionResult> Update(long id, [FromBody] BaseOtherInfo entity)
    {
        if (entity is null) throw BusinessException.InvalidParameter("请求内容不能为空");
        await EnsureAuthorizedAsync();
        entity.Id = id;
        OtherInfoAuthorizationRules.Validate(entity);
        var result = await Service.UpdateAsync(entity);
        return Ok(ApiResponse<BaseOtherInfo>.Success(result, "更新成功"));
    }

    /// <summary>删除（软删除）：先授权，再按既有软删除契约执行。</summary>
    [HttpDelete("{id:long}")]
    public override async Task<IActionResult> Delete(long id)
    {
        await EnsureAuthorizedAsync();
        await Service.DeleteAsync(id);
        return Ok(ApiResponse<object>.Success(null, "删除成功"));
    }

    /// <summary>批量删除（软删除）：先授权，再按既有批量软删除契约执行。</summary>
    [HttpPost("batch-delete")]
    public override async Task<IActionResult> BatchDelete([FromBody] List<long> ids)
    {
        await EnsureAuthorizedAsync();
        await Service.BatchDeleteAsync(ids);
        return Ok(ApiResponse<object>.Success(null, "批量删除成功"));
    }
}

