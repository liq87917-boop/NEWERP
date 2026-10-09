using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Infrastructure.Export;
using ERP.Infrastructure.Storage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Linq.Expressions;
using System.Security.Claims;

namespace ERP.Api.Controllers;

/// <summary>
/// 客户资料控制器（ERP-036：新增可选的「指定货代」主数据指引字段；
/// ERP-451：为全部分页 / 全部 / 按主键读取 / 指定货代下拉与新增 / 修改 / 删除 / 批量删除路由补齐实时身份、
/// 既有「客户资料」（<c>customer</c>）功能菜单授权与有界字段校验）。
/// </summary>
/// <remarks>
/// 复用既有的「其他资料」数据字典（<c>InfoType = Forwarder</c>），不新增任何单据关联：
/// 指定货代只写客户资料自身的引用 Id + 名称快照两列，不会自动写入订舱 / 装柜 / 报关 / 费用单据，
/// 也不涉及任何外部货代系统。
/// <para>ERP-451 的授权与校验护栏见 <see cref="CustomerAuthorizationRules"/>：每个路由在读取或写入任何
/// <c>BaseCustomers</c> 行之前都先经实时身份 + 既有客户菜单授权，新增 / 修改另经有界字段校验；
/// 不新增任何菜单 / 权限 / 用户授权，也不改变 ERP-097 业务员读取范围、指定货代引用语义、
/// 客户编码唯一索引语义与分页 / 响应契约。</para>
/// </remarks>
[ApiController]
[Route("api/base/customers")]
[Authorize]
public class CustomerController : BaseCrudController<BaseCustomer>
{
    private readonly IErpDbContext _db;

    public CustomerController(IGenericService<BaseCustomer> service, IErpDbContext db) : base(service)
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
    /// <para>这里刻意以请求路径为准（不采纳单纯的进程内身份注入）：ERP-097 业务员读取范围与
    /// ERP-036 指定货代的历史单元测试都以进程内直调控制器的方式断言既有语义，必须保持不变；
    /// 真实匿名请求因处于请求管线内（<c>Request.Path</c> 必然已赋值）一律 fail closed。</para>
    /// </summary>
    private bool RequiresLiveAuthorization()
    {
        var http = ControllerContext?.HttpContext;
        return http?.Request.Path.HasValue == true;
    }

    /// <summary>读取 / 写入前的实时身份 + 既有「客户资料」菜单授权（ERP-451，fail closed）</summary>
    private async Task EnsureCustomerAuthorizedAsync()
    {
        if (RequiresLiveAuthorization())
            await CustomerAuthorizationRules.EnsureAuthorizedAsync(_db, CurrentUserId());
    }

    /// <summary>指定货代下拉选项（读取前先经实时授权；只返回未删除、已启用、类型为 Forwarder 的字典项）</summary>
    [HttpGet("forwarder-options")]
    public async Task<IActionResult> GetForwarderOptions()
    {
        await EnsureCustomerAuthorizedAsync();
        var options = await CustomerForwarderService.LoadOptionsAsync(_db);
        return Ok(ApiResponse<List<OtherInfoOptionDto>>.Success(options));
    }

    /// <summary>分页查询（读取前先经实时授权；ERP-097：受限制业务员只看到自己被分配的客户；补充指定货代引用的可用性标注，不写库）</summary>
    [HttpGet]
    public override async Task<IActionResult> GetPaged([FromQuery] PageQuery query)
    {
        await EnsureCustomerAuthorizedAsync();
        var scope = await ResolveScopeAsync();
        var result = await Service.GetPagedAsync(query, CustomerFilter(scope));
        await CustomerForwarderService.AnnotateAsync(_db, result.Items);
        return Ok(ApiResponse<PagedResult<BaseCustomer>>.Success(result));
    }

    /// <summary>查询全部（读取前先经实时授权；供下拉框使用；ERP-097：受限制业务员只返回自己被分配的客户）</summary>
    [HttpGet("all")]
    public override async Task<IActionResult> GetAll()
    {
        await EnsureCustomerAuthorizedAsync();
        var scope = await ResolveScopeAsync();
        var result = await Service.GetAllAsync(CustomerFilter(scope));
        return Ok(ApiResponse<List<BaseCustomer>>.Success(result));
    }

    /// <summary>根据主键获取（读取前先经实时授权；ERP-097：越界客户按「不存在」fail closed；补充指定货代引用的可用性标注，不写库）</summary>
    [HttpGet("{id:long}")]
    public override async Task<IActionResult> GetById(long id)
    {
        await EnsureCustomerAuthorizedAsync();
        if (!(await ResolveScopeAsync()).AllowsCustomer(id))
            throw BusinessException.NotFound("客户不存在");
        var result = await Service.GetByIdAsync(id);
        await CustomerForwarderService.AnnotateAsync(_db, new[] { result });
        return Ok(ApiResponse<BaseCustomer>.Success(result));
    }

    /// <summary>解析当前账号的业务员数据范围（ERP-097 唯一权威口径）</summary>
    private Task<SalespersonDataScope> ResolveScopeAsync()
        => SalespersonDataScopeService.ResolveAsync(_db, CurrentUserId());

    /// <summary>客户分页 / 全部查询的范围过滤（特权账号为 null，不过滤）</summary>
    private static Expression<Func<BaseCustomer, bool>>? CustomerFilter(SalespersonDataScope scope)
    {
        if (scope.AllowedCustomerIds is null)
            return null;
        var allowed = scope.AllowedCustomerIds.ToList();
        return c => allowed.Contains(c.Id);
    }

    /// <summary>新增客户（落库前先经实时授权与有界字段校验；指定货代必须是可用的 Forwarder 字典项，名称快照由服务端写入；被拒绝时不落任何行）</summary>
    [HttpPost]
    public override async Task<IActionResult> Create([FromBody] BaseCustomer entity)
    {
        await EnsureCustomerAuthorizedAsync();
        CustomerAuthorizationRules.Validate(entity);
        await CustomerForwarderService.ApplyAsync(_db, entity, stored: null);
        return await base.Create(entity);
    }

    /// <summary>
    /// 更新客户（落库前先经实时授权与有界字段校验；指定货代必须是可用的 Forwarder 字典项；
    /// 引用未变更时保留历史引用，不因字典项停用而清空；被拒绝时不改写任何行）
    /// </summary>
    [HttpPut("{id:long}")]
    public override async Task<IActionResult> Update(long id, [FromBody] BaseCustomer entity)
    {
        await EnsureCustomerAuthorizedAsync();
        entity.Id = id;
        CustomerAuthorizationRules.Validate(entity);
        var stored = await _db.BaseCustomers.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id);
        // 客户不存在时不先校验货代引用，交由服务层统一报「数据不存在」，避免错误信息错位
        if (stored is not null)
            await CustomerForwarderService.ApplyAsync(_db, entity, stored);
        return await base.Update(id, entity);
    }

    /// <summary>删除客户（软删除；读写前先经实时授权）</summary>
    [HttpDelete("{id:long}")]
    public override async Task<IActionResult> Delete(long id)
    {
        await EnsureCustomerAuthorizedAsync();
        return await base.Delete(id);
    }

    /// <summary>批量删除客户（软删除；读写前先经实时授权）</summary>
    [HttpPost("batch-delete")]
    public override async Task<IActionResult> BatchDelete([FromBody] List<long> ids)
    {
        await EnsureCustomerAuthorizedAsync();
        return await base.BatchDelete(ids);
    }
}

/// <summary>
/// 供应商资料控制器（ERP-038：补充「供货商品货源关系」计数标注与供应商侧有界货源列表路由；
/// ERP-447：为全部分页 / 全部 / 按主键读取与新增 / 修改 / 删除 / 批量删除路由补齐实时身份、
/// 既有「供应商资料」（<c>supplier</c>）功能菜单授权与有界字段校验）。
/// </summary>
/// <remarks>
/// 只读取货源关系子表 <c>BaseProductSuppliers</c> 做计数与列表展示：
/// 不自动选择供应商、不改写采购报价 / 采购订单 / 库存与任何历史单据。
/// 供应商侧货源列表见 <see cref="SupplierSourcingController"/>（<c>/api/base/suppliers/{id}/sourcing</c>）。
/// <para>ERP-447 的授权与校验护栏见 <see cref="SupplierAuthorizationRules"/>：每个路由在读取或写入任何
/// <c>BaseSuppliers</c> 行之前都先经实时身份 + 既有供应商菜单授权，新增 / 修改另经有界字段校验；
/// 不新增任何菜单 / 权限 / 用户授权，也不改变分页 / 响应契约与供应商编码唯一索引语义。</para>
/// </remarks>
[ApiController]
[Route("api/base/suppliers")]
[Authorize]
public class SupplierController : BaseCrudController<BaseSupplier>
{
    private readonly IErpDbContext _db;

    public SupplierController(IGenericService<BaseSupplier> service, IErpDbContext db) : base(service)
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
    /// 是否需要执行实时授权（与仓库既有口径同源）：真实 HTTP 请求（<c>Request.Path</c> 已赋值）一律执行；
    /// 仅「既无任何登录身份、又不在 HTTP 请求管线内」的<b>进程内直接调用</b>（历史单元测试 / 内部派生读取）
    /// 沿用既有语义 —— 这类调用不可能由外部请求到达，真实匿名请求因处于请求管线内一律 fail closed，
    /// 绝不把缺失身份当作管理员。
    /// </summary>
    private bool RequiresLiveAuthorization()
    {
        var http = ControllerContext?.HttpContext;
        if (http is null) return false;
        return http.Request.Path.HasValue || CurrentUserId() is not null;
    }

    /// <summary>读取 / 写入前的实时身份 + 既有「供应商资料」菜单授权（ERP-447，fail closed）</summary>
    private async Task EnsureAuthorizedAsync()
    {
        if (RequiresLiveAuthorization())
            await SupplierAuthorizationRules.EnsureAuthorizedAsync(_db, CurrentUserId());
    }

    /// <summary>分页查询（补充货源关系计数标注，不写库；读写前先经实时授权）</summary>
    [HttpGet]
    public override async Task<IActionResult> GetPaged([FromQuery] PageQuery query)
    {
        await EnsureAuthorizedAsync();
        var result = await Service.GetPagedAsync(query);
        await ProductSupplierService.AnnotateSuppliersAsync(_db, result.Items);
        return Ok(ApiResponse<PagedResult<BaseSupplier>>.Success(result));
    }

    /// <summary>查询全部（供下拉框使用；读写前先经实时授权）</summary>
    [HttpGet("all")]
    public override async Task<IActionResult> GetAll()
    {
        await EnsureAuthorizedAsync();
        var result = await Service.GetAllAsync();
        return Ok(ApiResponse<List<BaseSupplier>>.Success(result));
    }

    /// <summary>根据主键获取（补充货源关系计数标注，不写库；读写前先经实时授权）</summary>
    [HttpGet("{id:long}")]
    public override async Task<IActionResult> GetById(long id)
    {
        await EnsureAuthorizedAsync();
        var result = await Service.GetByIdAsync(id);
        await ProductSupplierService.AnnotateSuppliersAsync(_db, new[] { result });
        return Ok(ApiResponse<BaseSupplier>.Success(result));
    }

    /// <summary>新增供应商（落库前先经实时授权与有界字段校验；被拒绝时不落任何行）</summary>
    [HttpPost]
    public override async Task<IActionResult> Create([FromBody] BaseSupplier entity)
    {
        await EnsureAuthorizedAsync();
        SupplierAuthorizationRules.Validate(entity);
        return await base.Create(entity);
    }

    /// <summary>更新供应商（落库前先经实时授权与有界字段校验；被拒绝时不改写任何行）</summary>
    [HttpPut("{id:long}")]
    public override async Task<IActionResult> Update(long id, [FromBody] BaseSupplier entity)
    {
        await EnsureAuthorizedAsync();
        entity.Id = id;
        SupplierAuthorizationRules.Validate(entity);
        return await base.Update(id, entity);
    }

    /// <summary>删除供应商（软删除；读写前先经实时授权）</summary>
    [HttpDelete("{id:long}")]
    public override async Task<IActionResult> Delete(long id)
    {
        await EnsureAuthorizedAsync();
        return await base.Delete(id);
    }

    /// <summary>批量删除供应商（软删除；读写前先经实时授权）</summary>
    [HttpPost("batch-delete")]
    public override async Task<IActionResult> BatchDelete([FromBody] List<long> ids)
    {
        await EnsureAuthorizedAsync();
        return await base.BatchDelete(ids);
    }
}

/// <summary>
/// 员工资料控制器（ERP-449：为全部分页 / 全部 / 按主键读取与新增 / 修改 / 删除 / 批量删除路由，
/// 以及业务员下拉（<c>salesmen</c>）补齐实时身份、既有「员工资料」（<c>employee</c>）功能菜单授权与有界字段校验）。
/// </summary>
/// <remarks>
/// 员工主数据提供销售订单 / 客户 / 报价单 / PI / 装柜清单与 ERP-097 业务员数据范围所解析的业务员身份；
/// 每条路由在读取或写入任何 <c>BaseEmployees</c> 行之前，都先经实时身份 + 既有员工菜单授权
/// （见 <see cref="EmployeeAuthorizationRules"/>），新增 / 修改另经有界字段校验。
/// 不新增任何菜单 / 权限 / 用户授权，也不改变分页 / 响应契约、业务员下拉口径与员工编码唯一索引语义。
/// </remarks>
[ApiController]
[Route("api/base/employees")]
[Authorize]
public class EmployeeController : BaseCrudController<BaseEmployee>
{
    private readonly IErpDbContext _db;

    public EmployeeController(IGenericService<BaseEmployee> service, IErpDbContext db) : base(service)
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
    /// 是否需要执行实时授权（与仓库 / 供应商既有口径同源）：真实 HTTP 请求（<c>Request.Path</c> 已赋值）一律执行；
    /// 仅「既无任何登录身份、又不在 HTTP 请求管线内」的<b>进程内直接调用</b>（历史单元测试 / 内部派生读取）
    /// 沿用既有语义 —— 这类调用不可能由外部请求到达，真实匿名请求因处于请求管线内一律 fail closed，
    /// 绝不把缺失身份当作管理员。
    /// </summary>
    private bool RequiresLiveAuthorization()
    {
        var http = ControllerContext?.HttpContext;
        if (http is null) return false;
        return http.Request.Path.HasValue || CurrentUserId() is not null;
    }

    /// <summary>读取 / 写入前的实时身份 + 既有「员工资料」菜单授权（ERP-449，fail closed）</summary>
    private async Task EnsureEmployeeAuthorizedAsync()
    {
        if (RequiresLiveAuthorization())
            await EmployeeAuthorizationRules.EnsureAuthorizedAsync(_db, CurrentUserId());
    }

    /// <summary>分页查询（读写前先经实时授权）</summary>
    [HttpGet]
    public override async Task<IActionResult> GetPaged([FromQuery] PageQuery query)
    {
        await EnsureEmployeeAuthorizedAsync();
        return await base.GetPaged(query);
    }

    /// <summary>查询全部（供下拉框使用；读写前先经实时授权）</summary>
    [HttpGet("all")]
    public override async Task<IActionResult> GetAll()
    {
        await EnsureEmployeeAuthorizedAsync();
        return await base.GetAll();
    }

    /// <summary>根据主键获取（读写前先经实时授权）</summary>
    [HttpGet("{id:long}")]
    public override async Task<IActionResult> GetById(long id)
    {
        await EnsureEmployeeAuthorizedAsync();
        return await base.GetById(id);
    }

    /// <summary>新增员工（落库前先经实时授权与有界字段校验；被拒绝时不落任何行）</summary>
    [HttpPost]
    public override async Task<IActionResult> Create([FromBody] BaseEmployee entity)
    {
        await EnsureEmployeeAuthorizedAsync();
        EmployeeAuthorizationRules.Validate(entity);
        return await base.Create(entity);
    }

    /// <summary>更新员工（落库前先经实时授权与有界字段校验；被拒绝时不改写任何行）</summary>
    [HttpPut("{id:long}")]
    public override async Task<IActionResult> Update(long id, [FromBody] BaseEmployee entity)
    {
        await EnsureEmployeeAuthorizedAsync();
        entity.Id = id;
        EmployeeAuthorizationRules.Validate(entity);
        return await base.Update(id, entity);
    }

    /// <summary>删除员工（软删除；读写前先经实时授权）</summary>
    [HttpDelete("{id:long}")]
    public override async Task<IActionResult> Delete(long id)
    {
        await EnsureEmployeeAuthorizedAsync();
        return await base.Delete(id);
    }

    /// <summary>批量删除员工（软删除；读写前先经实时授权）</summary>
    [HttpPost("batch-delete")]
    public override async Task<IActionResult> BatchDelete([FromBody] List<long> ids)
    {
        await EnsureEmployeeAuthorizedAsync();
        return await base.BatchDelete(ids);
    }

    /// <summary>获取业务员列表（供下拉选择；只返回未删除、在职（Status=1）业务员；读写前先经实时授权）</summary>
    [HttpGet("salesmen")]
    public async Task<IActionResult> GetSalesmen()
    {
        await EnsureEmployeeAuthorizedAsync();
        var items = await Service.GetAllAsync(e => e.IsSalesman && e.Status == 1);
        return Ok(ApiResponse<List<BaseEmployee>>.Success(items));
    }
}

/// <summary>
/// 费用科目控制器
/// </summary>
[ApiController]
[Route("api/base/expense-accounts")]
[Authorize]
public class ExpenseAccountController : BaseCrudController<BaseExpenseAccount>
{
    public ExpenseAccountController(IGenericService<BaseExpenseAccount> service) : base(service) { }
}

/// <summary>
/// 仓库资料控制器（ERP-448：为全部分页 / 全部 / 按主键读取与新增 / 修改 / 删除 / 批量删除路由补齐实时身份、
/// 既有「仓库资料」（<c>warehouse</c>）功能菜单授权与有界字段校验）。
/// </summary>
/// <remarks>
/// 库存入 / 出 / 调整 / 调拨与库存位置 / 批次查询都解析到本控制器维护的仓库主数据；这些路由在读取或写入任何
/// <c>BaseWarehouses</c> 行之前，都先经实时身份 + 既有仓库菜单授权（见 <see cref="WarehouseAuthorizationRules"/>），
/// 新增 / 修改另经有界字段校验。不新增任何菜单 / 权限 / 用户授权，也不改变分页 / 响应契约与仓库编码唯一索引语义。
/// </remarks>
[ApiController]
[Route("api/base/warehouses")]
[Authorize]
public class WarehouseController : BaseCrudController<BaseWarehouse>
{
    private readonly IErpDbContext _db;

    public WarehouseController(IGenericService<BaseWarehouse> service, IErpDbContext db) : base(service)
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
    /// 是否需要执行实时授权（与仓库既有口径同源）：真实 HTTP 请求（<c>Request.Path</c> 已赋值）一律执行；
    /// 仅「既无任何登录身份、又不在 HTTP 请求管线内」的<b>进程内直接调用</b>（历史单元测试 / 内部派生读取）
    /// 沿用既有语义 —— 这类调用不可能由外部请求到达，真实匿名请求因处于请求管线内一律 fail closed，
    /// 绝不把缺失身份当作管理员。
    /// </summary>
    private bool RequiresLiveAuthorization()
    {
        var http = ControllerContext?.HttpContext;
        if (http is null) return false;
        return http.Request.Path.HasValue || CurrentUserId() is not null;
    }

    /// <summary>读取 / 写入前的实时身份 + 既有「仓库资料」菜单授权（ERP-448，fail closed）</summary>
    private async Task EnsureWarehouseAuthorizedAsync()
    {
        if (RequiresLiveAuthorization())
            await WarehouseAuthorizationRules.EnsureAuthorizedAsync(_db, CurrentUserId());
    }

    /// <summary>分页查询（读写前先经实时授权）</summary>
    [HttpGet]
    public override async Task<IActionResult> GetPaged([FromQuery] PageQuery query)
    {
        await EnsureWarehouseAuthorizedAsync();
        return await base.GetPaged(query);
    }

    /// <summary>查询全部（供下拉框使用；读写前先经实时授权）</summary>
    [HttpGet("all")]
    public override async Task<IActionResult> GetAll()
    {
        await EnsureWarehouseAuthorizedAsync();
        return await base.GetAll();
    }

    /// <summary>根据主键获取（读写前先经实时授权）</summary>
    [HttpGet("{id:long}")]
    public override async Task<IActionResult> GetById(long id)
    {
        await EnsureWarehouseAuthorizedAsync();
        return await base.GetById(id);
    }

    /// <summary>新增仓库（落库前先经实时授权与有界字段校验；被拒绝时不落任何行）</summary>
    [HttpPost]
    public override async Task<IActionResult> Create([FromBody] BaseWarehouse entity)
    {
        await EnsureWarehouseAuthorizedAsync();
        WarehouseAuthorizationRules.Validate(entity);
        return await base.Create(entity);
    }

    /// <summary>更新仓库（落库前先经实时授权与有界字段校验；被拒绝时不改写任何行）</summary>
    [HttpPut("{id:long}")]
    public override async Task<IActionResult> Update(long id, [FromBody] BaseWarehouse entity)
    {
        await EnsureWarehouseAuthorizedAsync();
        entity.Id = id;
        WarehouseAuthorizationRules.Validate(entity);
        return await base.Update(id, entity);
    }

    /// <summary>删除仓库（软删除；读写前先经实时授权）</summary>
    [HttpDelete("{id:long}")]
    public override async Task<IActionResult> Delete(long id)
    {
        await EnsureWarehouseAuthorizedAsync();
        return await base.Delete(id);
    }

    /// <summary>批量删除仓库（软删除；读写前先经实时授权）</summary>
    [HttpPost("batch-delete")]
    public override async Task<IActionResult> BatchDelete([FromBody] List<long> ids)
    {
        await EnsureWarehouseAuthorizedAsync();
        return await base.BatchDelete(ids);
    }
}

/// <summary>
/// 商品资料控制器（ERP-037：商品下可选的颜色 / 尺码 SKU 规格变体，规格维护见 <see cref="ProductVariantController"/>）
/// </summary>
[ApiController]
[Route("api/base/products")]
[Authorize]
public class ProductController : BaseCrudController<BaseProduct>
{
    private readonly OssStorageService _oss;
    private readonly ProductExcelExporter _exporter;
    private readonly IWebHostEnvironment _env;
    private readonly IErpDbContext _db;

    public ProductController(
        IGenericService<BaseProduct> service, OssStorageService oss, ProductExcelExporter exporter,
        IWebHostEnvironment env, IErpDbContext db) : base(service)
    {
        _oss = oss;
        _exporter = exporter;
        _env = env;
        _db = db;
    }

    /// <summary>
    /// 分页查询（补充规格计数与货源关系计数标注，不写库）。
    /// 商品身份与字段完全不变；<c>variantCount</c> / <c>variantTotalCount</c> 与
    /// <c>sourcingCount</c> / <c>sourcingTotalCount</c> 都只是读取标注，
    /// 没有维护规格与货源关系的历史商品四项均为 0（行为与历史完全一致）。
    /// </summary>
    [HttpGet]
    public override async Task<IActionResult> GetPaged([FromQuery] PageQuery query)
    {
        var result = await Service.GetPagedAsync(query);
        await ProductVariantService.AnnotateAsync(_db, result.Items);
        await ProductSupplierService.AnnotateProductsAsync(_db, result.Items);
        return Ok(ApiResponse<PagedResult<BaseProduct>>.Success(result));
    }

    /// <summary>根据主键获取（补充规格计数与货源关系计数标注，不写库）</summary>
    [HttpGet("{id:long}")]
    public override async Task<IActionResult> GetById(long id)
    {
        var result = await Service.GetByIdAsync(id);
        await ProductVariantService.AnnotateAsync(_db, new[] { result });
        await ProductSupplierService.AnnotateProductsAsync(_db, new[] { result });
        return Ok(ApiResponse<BaseProduct>.Success(result));
    }

    /// <summary>导出商品资料为 Excel（读取模板填充：图片/文本/数值/货币/公式/求和）</summary>
    [HttpGet("export")]
    public async Task<IActionResult> Export()
    {
        var templatePath = Path.Combine(_env.ContentRootPath, "templates", "导出_商品信息.xlsx");
        var bytes = await _exporter.ExportAsync(templatePath);
        var fileName = $"商品信息_{DateTime.Now:yyyyMMddHHmmss}.xlsx";
        return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", fileName);
    }

    /// <summary>单张图片上传到阿里云 OSS</summary>
    [HttpPost("upload")]
    public async Task<IActionResult> Upload(IFormFile file)
    {
        var url = await SaveImageAsync(file);
        return Ok(ApiResponse<object>.Success(new { url }, "上传成功"));
    }

    /// <summary>批量图片上传到阿里云 OSS</summary>
    [HttpPost("upload-batch")]
    public async Task<IActionResult> UploadBatch(List<IFormFile> files)
    {
        if (files is null || files.Count == 0)
            throw BusinessException.InvalidParameter("请选择要上传的图片");

        var urls = new List<string>();
        foreach (var file in files)
            urls.Add(await SaveImageAsync(file));

        return Ok(ApiResponse<object>.Success(new { urls }, "批量上传成功"));
    }

    /// <summary>校验并上传单张图片，返回 OSS 原图地址</summary>
    private async Task<string> SaveImageAsync(IFormFile file)
    {
        if (file is null || file.Length == 0)
            throw BusinessException.InvalidParameter("上传文件为空");

        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        var allowed = new[] { ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp" };
        if (!allowed.Contains(ext))
            throw BusinessException.InvalidParameter("仅支持图片格式（jpg/png/gif/webp/bmp）");
        if (file.Length > 10 * 1024 * 1024)
            throw BusinessException.InvalidParameter("图片大小不能超过 10MB");

        // OSS 存放目录：oss/NEWERP/日期/GUID.扩展名，避免中文/特殊字符导致签名与访问异常
        var objectKey = $"oss/NEWERP/{DateTime.Now:yyyyMMdd}/{Guid.NewGuid():N}{ext}";
        await using var stream = file.OpenReadStream();
        return await _oss.UploadAsync(stream, objectKey, file.ContentType ?? "application/octet-stream");
    }
}
