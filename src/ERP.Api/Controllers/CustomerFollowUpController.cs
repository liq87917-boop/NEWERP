using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Linq.Expressions;
using System.Security.Claims;

namespace ERP.Api.Controllers;

/// <summary>
/// 客户跟进记录控制器（CRM）
/// 维护：与客户的每一次接触（电话 / 微信 / 邮件 / 拜访 / 展会 / 寄样）、跟进结果与下次跟进日期
/// <para>ERP-458：分页 / 全部 / 按主键读取 / 新增 / 修改 / 删除 / 批量删除<b>每一个</b>路由在执行任何读取或写入
/// <b>之前</b>都重新解析实时身份（缺失 / 非法 / 已删除按未认证，禁用按权限不足）与既有「客户跟进记录」
/// （<c>customer-follow</c>）功能菜单授权（缺菜单 / 被撤销按权限不足），一律 fail closed；
/// 读取按 ERP-097 业务员数据范围（<see cref="SalespersonDataScopeService"/> 唯一权威口径）下推过滤，
/// 新增 / 修改要求写入的 <c>CustomerId</c> 落在当前范围并指向已知的未删除客户，且经有界字段校验。
/// <b>不</b>新增任何菜单 / 权限 / 用户授权，也<b>不</b>因身份缺失而降级为管理员；软删除语义与
/// <see cref="GenericService{TEntity}"/> 契约保持不变。</para>
/// </summary>
[ApiController]
[Route("api/crm/follow-ups")]
[Authorize]
public class CustomerFollowUpController : BaseCrudController<CustomerFollowUp>
{
    private readonly IErpDbContext _db;

    public CustomerFollowUpController(IGenericService<CustomerFollowUp> service, IErpDbContext db) : base(service)
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
    /// 读取 / 写入前的实时身份 + 既有「客户跟进记录」菜单授权 + ERP-097 业务员数据范围（fail closed）。
    /// <para>与仓库既有口径一致：无论 HTTP 请求还是进程内调用都执行实时授权，绝不按请求路径 / 环境 /
    /// 空请求降级为匿名或管理员，也绝不提供测试专用旁路。</para>
    /// </summary>
    private Task<SalespersonDataScope> EnsureAuthorizedScopeAsync()
        => CustomerFollowUpAuthorizationRules.EnsureAuthorizedScopeAsync(_db, CurrentUserId());

    /// <summary>跟进记录分页 / 全部查询的范围过滤（特权账号为 null，不过滤；受限账号按 <c>CustomerId</c> 下推）。</summary>
    private static Expression<Func<CustomerFollowUp, bool>>? CustomerScopeFilter(SalespersonDataScope scope)
    {
        if (scope.AllowedCustomerIds is null)
            return null;
        var allowed = scope.AllowedCustomerIds.ToList();
        return f => f.CustomerId.HasValue && allowed.Contains(f.CustomerId.Value);
    }

    /// <summary>分页查询（读取前先经实时授权与数据范围；受限账号只返回自己被分配客户的跟进记录）</summary>
    [HttpGet]
    public override async Task<IActionResult> GetPaged([FromQuery] PageQuery query)
    {
        var scope = await EnsureAuthorizedScopeAsync();
        var result = await Service.GetPagedAsync(query, CustomerScopeFilter(scope));
        return Ok(ApiResponse<PagedResult<CustomerFollowUp>>.Success(result));
    }

    /// <summary>查询全部（读取前先经实时授权与数据范围；受限账号只返回自己被分配客户的跟进记录）</summary>
    [HttpGet("all")]
    public override async Task<IActionResult> GetAll()
    {
        var scope = await EnsureAuthorizedScopeAsync();
        var result = await Service.GetAllAsync(CustomerScopeFilter(scope));
        return Ok(ApiResponse<List<CustomerFollowUp>>.Success(result));
    }

    /// <summary>根据主键获取（读取前先经实时授权；受限账号越界跟进记录按「不存在」fail closed，不加载任何行）</summary>
    [HttpGet("{id:long}")]
    public override async Task<IActionResult> GetById(long id)
    {
        var scope = await EnsureAuthorizedScopeAsync();
        if (scope.AllowedCustomerIds is null)
        {
            var result = await Service.GetByIdAsync(id);
            return Ok(ApiResponse<CustomerFollowUp>.Success(result));
        }

        var allowed = scope.AllowedCustomerIds.ToList();
        var entity = await _db.CustomerFollowUps.AsNoTracking()
            .Where(f => !f.IsDeleted && f.Id == id)
            .Where(f => f.CustomerId.HasValue && allowed.Contains(f.CustomerId.Value))
            .FirstOrDefaultAsync();
        if (entity is null)
            throw BusinessException.NotFound("客户跟进记录不存在");
        return Ok(ApiResponse<CustomerFollowUp>.Success(entity));
    }

    /// <summary>新增（落库前先经实时授权、有界字段校验与客户 / 跟进人引用与范围校验；被拒绝时不落任何行）</summary>
    [HttpPost]
    public override async Task<IActionResult> Create([FromBody] CustomerFollowUp entity)
    {
        var scope = await EnsureAuthorizedScopeAsync();
        CustomerFollowUpAuthorizationRules.Validate(entity);
        await CustomerFollowUpAuthorizationRules.EnsureWritableReferencesAsync(_db, scope, entity);
        return await base.Create(entity);
    }

    /// <summary>更新（落库前先经实时授权、有界字段校验与客户 / 跟进人引用与范围校验；被拒绝时不改写任何行）</summary>
    [HttpPut("{id:long}")]
    public override async Task<IActionResult> Update(long id, [FromBody] CustomerFollowUp entity)
    {
        var scope = await EnsureAuthorizedScopeAsync();
        entity.Id = id;
        CustomerFollowUpAuthorizationRules.Validate(entity);
        await CustomerFollowUpAuthorizationRules.EnsureWritableReferencesAsync(_db, scope, entity);
        return await base.Update(id, entity);
    }

    /// <summary>删除（软删除；读写前先经实时授权；软删除语义与其它派生控制器保持不变）</summary>
    [HttpDelete("{id:long}")]
    public override async Task<IActionResult> Delete(long id)
    {
        await EnsureAuthorizedScopeAsync();
        return await base.Delete(id);
    }

    /// <summary>批量删除（软删除；读写前先经实时授权；软删除语义与其它派生控制器保持不变）</summary>
    [HttpPost("batch-delete")]
    public override async Task<IActionResult> BatchDelete([FromBody] List<long> ids)
    {
        await EnsureAuthorizedScopeAsync();
        return await base.BatchDelete(ids);
    }
}

