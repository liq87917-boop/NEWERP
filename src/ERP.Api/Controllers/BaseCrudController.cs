using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using System.Security.Claims;

namespace ERP.Api.Controllers;

/// <summary>
/// 通用 CRUD 控制器基类：适用于基础资料等简单实体，统一分页/增删改查路由
/// </summary>
[ApiController]
[Authorize]
public abstract class BaseCrudController<TEntity> : ControllerBase where TEntity : BaseEntity
{
    protected readonly IGenericService<TEntity> Service;

    protected BaseCrudController(IGenericService<TEntity> service)
    {
        Service = service;
    }

    /// <summary>
    /// 本次请求由 <see cref="ExpenseRequestAuthorizationFilter"/> 解析出的权威客户数据范围；
    /// <c>null</c> 表示该动作未经 MVC 授权管线（进程内直接调用），绝不代表匿名或管理员。
    /// </summary>
    protected SalespersonDataScope? ExpenseRequestScope
        => HttpContext?.Items.TryGetValue(ExpenseRequestAuthorizationFilter.ScopeItemKey, out var value) == true
           && value is SalespersonDataScope scope
            ? scope
            : null;

    /// <summary>分页查询（基础资料可在派生控制器中 override 以补充读取标注）</summary>
    [HttpGet]
    public virtual async Task<IActionResult> GetPaged([FromQuery] PageQuery query)
    {
        var result = await Service.GetPagedAsync(query);
        return Ok(ApiResponse<PagedResult<TEntity>>.Success(result));
    }

    /// <summary>查询全部（供下拉框等使用；派生控制器可 override 以补充数据范围过滤，见 CustomerController）</summary>
    [HttpGet("all")]
    public virtual async Task<IActionResult> GetAll()
    {
        var result = await Service.GetAllAsync();
        return Ok(ApiResponse<List<TEntity>>.Success(result));
    }

    /// <summary>根据主键获取</summary>
    [HttpGet("{id:long}")]
    public virtual async Task<IActionResult> GetById(long id)
    {
        var result = await Service.GetByIdAsync(id);
        return Ok(ApiResponse<TEntity>.Success(result));
    }

    /// <summary>新增（派生控制器可 override 以补充字段级校验，见 CustomerController 的指定货代）</summary>
    [HttpPost]
    public virtual async Task<IActionResult> Create([FromBody] TEntity entity)
    {
        var result = await Service.CreateAsync(entity);
        return Ok(ApiResponse<TEntity>.Success(result, "新增成功"));
    }

    /// <summary>更新（派生控制器可 override 以补充字段级校验，见 CustomerController 的指定货代）</summary>
    [HttpPut("{id:long}")]
    public virtual async Task<IActionResult> Update(long id, [FromBody] TEntity entity)
    {
        entity.Id = id;
        var result = await Service.UpdateAsync(entity);
        return Ok(ApiResponse<TEntity>.Success(result, "更新成功"));
    }

    /// <summary>删除（软删除；ERP-385 起 virtual，供费用单等模块在删除前复核权威客户范围）</summary>
    [HttpDelete("{id:long}")]
    public virtual async Task<IActionResult> Delete(long id)
    {
        await Service.DeleteAsync(id);
        return Ok(ApiResponse<object>.Success(null, "删除成功"));
    }

    /// <summary>批量删除（软删除；ERP-385 起 virtual，供费用单等模块在删除前复核权威客户范围）</summary>
    [HttpPost("batch-delete")]
    public virtual async Task<IActionResult> BatchDelete([FromBody] List<long> ids)
    {
        await Service.BatchDeleteAsync(ids);
        return Ok(ApiResponse<object>.Success(null, "批量删除成功"));
    }
}

/// <summary>
/// 费用单 / 分摊批次 / 分摊证据路由的实时授权过滤器（ERP-385）：任何进入 MVC 管线的请求在动作方法执行
/// <b>之前</b>重新解析实时启用身份、既有「费用单」（<c>expense-bill</c>）菜单授权与权威客户数据范围，
/// 并把本次请求范围写入 <see cref="ScopeItemKey"/> 供动作方法做 SQL 侧范围下推与实体级复核；
/// 解析失败立即 fail closed（不执行任何计数 / 单号生成 / 写入），且绝不把空身份当作管理员。
/// <para>本过滤器是 HTTP 入口的强制门；进程内直接调用动作方法（单元测试 / 内部派生读取）不经过过滤器，
/// 因此其范围只能取 <c>null</c>（不加范围限制），等价于既有内部口径，绝不代表匿名 HTTP 请求。</para>
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class ExpenseRequestAuthorizationFilter : Attribute, IAsyncActionFilter
{
    /// <summary>本次请求权威客户范围在 <c>HttpContext.Items</c> 中的键</summary>
    public const string ScopeItemKey = "ERP385.ExpenseAuthorizationScope";

    /// <inheritdoc />
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var db = context.HttpContext.RequestServices.GetService(typeof(IErpDbContext)) as IErpDbContext
            ?? throw new InvalidOperationException("未注册 IErpDbContext，无法执行费用单实时授权");

        var scope = await ExpenseAuthorizationRules.EnsureMenuAuthorizedAsync(
            db, ResolveUserId(context.HttpContext.User));
        context.HttpContext.Items[ScopeItemKey] = scope;

        await next();
    }

    /// <summary>从已认证请求主体解析账号 Id（缺失 / 非法返回 <c>null</c>，由规则层 fail closed）</summary>
    public static long? ResolveUserId(ClaimsPrincipal? user)
    {
        var value = user?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return long.TryParse(value, out var id) && id > 0 ? id : null;
    }
}
