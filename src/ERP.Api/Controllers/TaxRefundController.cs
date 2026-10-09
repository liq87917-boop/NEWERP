using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace ERP.Api.Controllers;

/// <summary>
/// 出口退税台账控制器（阶段 1 新增）
/// 维护：按报关单/出口发票记录退税率、可退税额、已退税额与到账日期
/// <para>阶段 3（ERP-442）收口：分页 / 全部 / 详情 / 新增 / 修改 / 删除 / 批量删除在读取任何计数或写入
/// 任何台账行<b>之前</b>，都先复核实时启用身份 + 既有「出口退税台账」（<c>tax-refund</c>）功能菜单 +
/// ERP-097 权威客户数据范围（<see cref="TaxRefundLedgerRules"/>），并把范围下推到计数 / 分页；新增 / 修改
/// 另校验金额、退税率、币种、退税期间与申报 / 到账日期。缺失 / 禁用 / 已删除 / 撤销菜单的身份一律以既有受控
/// 非披露错误 fail closed，不新增任何授权，也不把空身份当作管理员。</para>
/// </summary>
[ApiController]
[Route("api/base/tax-refunds")]
[Authorize]
public class TaxRefundController : BaseCrudController<BaseTaxRefund>
{
    private readonly IErpDbContext _db;

    public TaxRefundController(IGenericService<BaseTaxRefund> service, IErpDbContext db) : base(service)
    {
        _db = db;
    }

    /// <summary>当前登录账号 Id（只来自已认证请求主体；缺失 / 非数字 / 非正返回 <c>null</c>，由规则层 fail closed）。</summary>
    private long? CurrentUserId()
    {
        var value = ControllerContext?.HttpContext?.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return long.TryParse(value, out var id) && id > 0 ? id : null;
    }

    /// <summary>出口退税台账入口授权（实时身份 + 既有 tax-refund 菜单 + ERP-097 权威客户数据范围）。</summary>
    private Task<SalespersonDataScope> EnsureAuthorizedAsync()
        => TaxRefundLedgerRules.EnsureMenuAuthorizedAsync(_db, CurrentUserId());

    /// <summary>分页查询：先授权，再把权威客户范围下推到计数与分页之前（范围外 / 无主行不进入计数与当前页）。</summary>
    [HttpGet]
    public override async Task<IActionResult> GetPaged([FromQuery] PageQuery query)
    {
        var scope = await EnsureAuthorizedAsync();
        var result = await Service.GetPagedAsync(query, TaxRefundLedgerRules.ScopeFilter(scope));
        return Ok(ApiResponse<PagedResult<BaseTaxRefund>>.Success(result));
    }

    /// <summary>查询全部（下拉用）：先授权，再按权威客户范围在数据库侧过滤后返回。</summary>
    [HttpGet("all")]
    public override async Task<IActionResult> GetAll()
    {
        var scope = await EnsureAuthorizedAsync();
        var result = await Service.GetAllAsync(TaxRefundLedgerRules.ScopeFilter(scope));
        return Ok(ApiResponse<List<BaseTaxRefund>>.Success(result));
    }

    /// <summary>详情：先授权，读取后复核持久化 CustomerId 权威归属（受限账号越界 / 无主 fail closed）。</summary>
    [HttpGet("{id:long}")]
    public override async Task<IActionResult> GetById(long id)
    {
        var scope = await EnsureAuthorizedAsync();
        var entity = await Service.GetByIdAsync(id);
        TaxRefundLedgerRules.EnsureStoredScopeAllowed(scope, entity);
        return Ok(ApiResponse<BaseTaxRefund>.Success(entity));
    }

    /// <summary>新增：写入任何字段之前完成授权、范围 / 真实启用客户与金额 / 税率 / 期间 / 日期校验。</summary>
    [HttpPost]
    public override async Task<IActionResult> Create([FromBody] BaseTaxRefund entity)
    {
        entity.Id = 0;
        var scope = await EnsureAuthorizedAsync();
        await TaxRefundLedgerRules.EnsureProposedScopeAllowedAsync(_db, scope, entity);
        TaxRefundLedgerRules.Validate(entity);
        var result = await Service.CreateAsync(entity);
        return Ok(ApiResponse<BaseTaxRefund>.Success(result, "新增成功"));
    }

    /// <summary>修改：先授权并复核已存行归属，再校验拟议范围与字段规则，失败不落任何改写。</summary>
    [HttpPut("{id:long}")]
    public override async Task<IActionResult> Update(long id, [FromBody] BaseTaxRefund entity)
    {
        var scope = await EnsureAuthorizedAsync();
        var existing = await Service.GetByIdAsync(id);
        TaxRefundLedgerRules.EnsureStoredScopeAllowed(scope, existing);

        entity.Id = id;
        await TaxRefundLedgerRules.EnsureProposedScopeAllowedAsync(_db, scope, entity);
        TaxRefundLedgerRules.Validate(entity);

        var result = await Service.UpdateAsync(entity);
        return Ok(ApiResponse<BaseTaxRefund>.Success(result, "更新成功"));
    }

    /// <summary>删除（软删除）：先授权并复核已存行权威归属，越界 / 无主 fail closed。</summary>
    [HttpDelete("{id:long}")]
    public override async Task<IActionResult> Delete(long id)
    {
        var scope = await EnsureAuthorizedAsync();
        var existing = await Service.GetByIdAsync(id);
        TaxRefundLedgerRules.EnsureStoredScopeAllowed(scope, existing);
        await Service.DeleteAsync(id);
        return Ok(ApiResponse<object>.Success(null, "删除成功"));
    }

    /// <summary>批量删除（软删除）：逐行复核权威归属，任一行越界 / 无主即整批拒绝、不做部分删除。</summary>
    [HttpPost("batch-delete")]
    public override async Task<IActionResult> BatchDelete([FromBody] List<long> ids)
    {
        var scope = await EnsureAuthorizedAsync();
        var ownership = await TaxRefundLedgerRules.LoadOwnershipAsync(_db, ids);
        foreach (var row in ownership)
            TaxRefundLedgerRules.EnsureStoredScopeAllowed(scope, row);

        await Service.BatchDeleteAsync(ids);
        return Ok(ApiResponse<object>.Success(null, "批量删除成功"));
    }
}
