using ERP.Application.Common;
using ERP.Application.DTOs;
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
/// 客户销项发票证据登记控制器（ERP-055）：登记普通发票 / 增值税专用发票 / 出口发票的**运营证据**，
/// 并可（可选、显式）把含税总额分摊到既有销售订单。
/// <para>边界（控制器层同样遵守）：本模块<strong>不是</strong>发票开具系统（不连税务局、不调用任何开票服务）、
/// <strong>不是</strong>税务申报与销项税金计算、<strong>不是</strong>应收账款台账或余额、<strong>不是</strong>收款核销；
/// 所有接口只读写 <c>CustomerSalesInvoiceEvidences</c> / <c>CustomerSalesInvoiceAllocations</c> 两张表，
/// <strong>不</strong>开具或作废任何真实发票、<strong>不</strong>改写销售订单状态 / 出货进度 / 金额与明细、
/// 客户信用状态、客户收款单与其引用行、库存与库存成本、装柜与单证记录、佣金 / 回佣、费用与退税记录，
/// 也不记账、不生成凭证 / 收款 / 付款 / 结算单；与单证中心商业发票刻意分离，只接受显式、有界的交叉引用；
/// 生产库结构变更仍由 Human Gate 控制（本控制器不做任何 DDL，建表 / 索引由 SchemaUpgrader 幂等补齐）。</para>
/// <para>ERP-383 授权与并发：台账 / 详情 / 收款时效证据 / 商业发票候选 / 新增 / 修改 / 可分摊订单候选 /
/// 分摊预览 / 分摊整体替换 / 登记 / 作废<strong>每一路由</strong>都在读取 / 计数 / 候选 / 预览 / 写入之前
/// 重新校验当前启用身份、既有「销售订单」（<c>sales-order</c>）菜单授权与
/// <see cref="ERP.Application.Services.SalespersonDataScopeService"/>（ERP-097 唯一权威口径）客户数据范围；
/// 发票归属同时覆盖已存储客户、全部已存储销售订单分摊来源与显式交叉引用单证的归属客户（混源要求全部允许），
/// 受限账号不得访问来源不可证明的发票，被拒绝的调用方绝不改写任何证据；未新增任何用户 / 角色 / 菜单 / 权限授权。</para>
/// </summary>
[ApiController]
[Route("api/customer-sales-invoices")]
[Authorize]
public class CustomerSalesInvoiceEvidenceController : ControllerBase
{
    private readonly IErpDbContext _db;

    public CustomerSalesInvoiceEvidenceController(IErpDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// 发票证据台账（分页，只读）：可按客户 / 发票类型 / 状态 / 币种 / 开票日期区间 / 分摊状态 /
    /// 销售订单 / 关键字过滤；默认包含已作废历史（证据保留可读），分摊状态按持久化分摊行派生，
    /// 未分摊金额绝不被猜测到任何订单。
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetPaged([FromQuery] CustomerSalesInvoiceEvidenceQuery query)
    {
        // ERP-383：身份 / 菜单 / 权威来源范围谓词在**计数与分页之前**下推数据库（绝不先查全量再内存过滤）。
        var scopePredicate = await BuildScopePredicateAsync();
        return Ok(ApiResponse<PagedResult<CustomerSalesInvoiceEvidenceDto>>.Success(
            await CustomerSalesInvoiceEvidenceService.ListAsync(_db, query, scopePredicate: scopePredicate)));
    }

    /// <summary>
    /// 客户销项发票收款时效证据（ERP-111，只读派生）：按客户 / 开票日期区间筛选已登记未删除的发票证据，
    /// 用显式 ERP-073「收款单 → 发票」分摊行批量装载收款单，派生「开票日期 → 首张 / 末张有效收款日期」的
    /// 间隔天数与可比较的已分摊 / 剩余证据；缺链接、已作废、收款单取消、早于开票日期、币种不一致的分摊行
    /// 仅作异常证据列出、不计入首末收款与可比较金额；应用业务员数据范围。
    /// <para>只读：不写任何表、不执行迁移 / 生产 SQL / 真实数据库操作 / 部署，不改写发票 / 收款单 / 分摊行 / 客户；
    /// 分摊行<strong>绝不</strong>被当作银行到账、法定账龄或催收 SLA。</para>
    /// </summary>
    [HttpGet("collection-timing")]
    public async Task<IActionResult> CollectionTiming([FromQuery] CustomerInvoiceCollectionTimingQuery query)
    {
        // ERP-383：只读派生报表同样先校验实时身份 / 菜单授权，再按权威客户数据范围过滤（fail closed）。
        var scope = await ResolveAuthorizedScopeAsync();
        return Ok(ApiResponse<CustomerInvoiceCollectionTimingReport>.Success(
            await CustomerInvoiceCollectionTimingService.ForQueryAsync(_db, query, scope.AllowedCustomerIds)));
    }

    /// <summary>
    /// 可显式交叉引用的单证中心商业发票候选（只读、有界）：只列出既有、未删除且类型为商业发票的单证，
    /// 仅用于显式选择交叉引用来源；不读取单证金额、不转换单证、不建立自动链接。
    /// </summary>
    [HttpGet("commercial-invoice-candidates")]
    public async Task<IActionResult> CommercialInvoiceCandidates(
        [FromQuery] string? keyword,
        [FromQuery] int take = CustomerSalesInvoiceEvidenceRules.MaxTradeDocumentCandidates)
    {
        // ERP-383：候选按既有单证的权威归属客户范围下推（受限账号看不到无归属 / 范围外单证，绝不按单号文本猜测来源）。
        var scopePredicate = await BuildTradeDocumentScopePredicateAsync();
        return Ok(ApiResponse<List<CustomerSalesInvoiceTradeDocumentCandidateDto>>.Success(
            await CustomerSalesInvoiceEvidenceService.ListTradeDocumentCandidatesAsync(
                _db, keyword, take, scopePredicate)));
    }

    /// <summary>发票证据详情（含分摊行、已分摊 / 未分摊金额与订单可用性标注；只读）</summary>
    [HttpGet("{id:long}")]
    public async Task<IActionResult> GetById(long id)
    {
        var scope = await EnsureInvoiceIdAuthorizedAsync(id);
        return Ok(ApiResponse<CustomerSalesInvoiceEvidenceDto>.Success(
            await CustomerSalesInvoiceEvidenceService.GetAsync(_db, id, scope?.AllowedCustomerIds)));
    }

    /// <summary>
    /// 新增草稿发票证据：校验发票类型 / 代码 / 号码、客户（必须存在且启用）、币种与金额等式
    /// （含税总额 = 不含税金额 + 税额，按币种精度取整后严格相等）、可选单证交叉引用，并拒绝重复身份。
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CustomerSalesInvoiceEvidenceSaveDto dto)
    {
        // ERP-383：拟提议客户 / 单证必须落在当前账号的权威客户数据范围内（在写入之前 fail closed）。
        await EnsureProposedEvidenceScopeAuthorizedAsync(dto);
        return Ok(ApiResponse<CustomerSalesInvoiceEvidenceDto>.Success(
            await CustomerSalesInvoiceEvidenceService.CreateAsync(_db, dto),
            "销项发票证据草稿已登记（仅证据留痕；未开票、未报税、未记账）"));
    }

    /// <summary>修改草稿发票证据（已登记 / 已作废拒绝修改；已有销售订单分摊时不允许更换客户或币种）</summary>
    [HttpPut("{id:long}")]
    public async Task<IActionResult> Update(long id, [FromBody] CustomerSalesInvoiceEvidenceSaveDto dto)
    {
        // ERP-383：先校验已存储发票的全部来源范围，再校验拟提议客户 / 单证范围（被拒绝者绝不改写任何证据）。
        await EnsureInvoiceIdAuthorizedAsync(id);
        await EnsureProposedEvidenceScopeAuthorizedAsync(dto);
        return Ok(ApiResponse<CustomerSalesInvoiceEvidenceDto>.Success(
            await CustomerSalesInvoiceEvidenceService.UpdateAsync(_db, id, dto),
            "销项发票证据草稿已更新"));
    }

    /// <summary>
    /// 可分摊销售订单候选（只读、有界）：只返回同客户 + 同币种的订单（含已取消订单并标注不可分摊），
    /// 附带订单总额、本发票已分摊、其他有效发票已分摊与剩余未被发票证据覆盖的金额（派生值，不是应收余额）。
    /// </summary>
    [HttpGet("{id:long}/order-candidates")]
    public async Task<IActionResult> OrderCandidates(
        long id, [FromQuery] string? keyword,
        [FromQuery] int take = CustomerSalesInvoiceEvidenceRules.MaxOrderCandidates)
    {
        await EnsureInvoiceIdAuthorizedAsync(id);
        return Ok(ApiResponse<List<CustomerSalesInvoiceOrderCandidateDto>>.Success(
            await CustomerSalesInvoiceEvidenceService.ListOrderCandidatesAsync(_db, id, keyword, take)));
    }

    /// <summary>
    /// 分摊预览（**只读，不写库**）：逐行校验销售订单资格与分摊金额，返回订单快照、资格文案、
    /// 拟分摊合计与保存后的已分摊 / 未分摊金额；预览与保存共用同一校验口径。
    /// </summary>
    [HttpPost("{id:long}/allocations/preview")]
    public async Task<IActionResult> PreviewAllocations(
        long id, [FromBody] CustomerSalesInvoiceAllocationSaveRequest? request)
    {
        await EnsureInvoiceIdAuthorizedAsync(id);
        await EnsureProposedAllocationsAuthorizedAsync(request);
        return Ok(ApiResponse<CustomerSalesInvoiceAllocationPreviewDto>.Success(
            await CustomerSalesInvoiceEvidenceService.PreviewAllocationsAsync(_db, id, request),
            "分摊预览完成（未写库）"));
    }

    /// <summary>
    /// 保存分摊（草稿专用，整体替换）：同一发票内同一订单不重复、客户与币种必须一致、
    /// 分摊金额合计不得超过含税总额；已登记 / 已作废发票拒绝任何分摊改动。
    /// </summary>
    [HttpPost("{id:long}/allocations")]
    public async Task<IActionResult> SaveAllocations(
        long id, [FromBody] CustomerSalesInvoiceAllocationSaveRequest? request)
    {
        // ERP-383：先复核「已存储来源」与「拟提议来源」的完整授权范围，再做整体替换（被拒绝者绝不改写任何分摊行）。
        await EnsureInvoiceIdAuthorizedAsync(id);
        await EnsureProposedAllocationsAuthorizedAsync(request);
        return Ok(ApiResponse<CustomerSalesInvoiceEvidenceDto>.Success(
            await CustomerSalesInvoiceEvidenceService.SaveAllocationsAsync(_db, id, request),
            "发票分摊已保存"));
    }

    /// <summary>
    /// 登记发票证据（草稿 → 已登记）：登记前复核已持久化分摊行仍权威可分摊；只改发票状态与登记时间，
    /// <strong>不</strong>开具真实发票、<strong>不</strong>调用任何开票 / 税务服务、<strong>不</strong>改动销售订单、
    /// 客户信用状态、收款单与其引用行、库存与财务记录。
    /// </summary>
    [HttpPost("{id:long}/record")]
    public async Task<IActionResult> Record(long id)
    {
        await EnsureInvoiceIdAuthorizedAsync(id);
        return Ok(ApiResponse<CustomerSalesInvoiceEvidenceDto>.Success(
            await CustomerSalesInvoiceEvidenceService.RecordAsync(_db, id),
            "销项发票证据已登记（证据已冻结，可作废但不可改写；未开票、未报税、未记账）"));
    }

    /// <summary>
    /// 作废发票证据（必须填写作废原因）：保留身份、金额、分摊行与审计历史，不物理删除、不改写已登记证据，
    /// 也<strong>不</strong>作废任何真实发票、不产生任何财务 / 税务动作；重复作废被拒绝。
    /// </summary>
    [HttpPost("{id:long}/void")]
    public async Task<IActionResult> Void(long id, [FromBody] CustomerSalesInvoiceVoidRequest? request)
    {
        await EnsureInvoiceIdAuthorizedAsync(id);
        return Ok(ApiResponse<CustomerSalesInvoiceEvidenceDto>.Success(
            await CustomerSalesInvoiceEvidenceService.VoidAsync(_db, id, request?.Reason),
            "销项发票证据已作废（历史证据与分摊保留，可读）"));
    }

    // ==================== ERP-383：实时授权辅助（身份 / 菜单 / 权威来源范围） ====================

    /// <summary>
    /// 是否必须执行实时授权：真实 HTTP 请求（MVC 绑定，<c>Request.Path</c> 已赋值）一律执行；
    /// 进程内直接调用（历史单元测试 / 内部派生读取，无 HTTP 请求管线）仅在携带当前登录身份时执行。
    /// 只对「既无任何登录身份、又不在 HTTP 请求管线内」的调用免授权：这类调用不可能由外部请求到达，
    /// 也绝不把缺失身份当作管理员（真实匿名请求因处于请求管线内一律 fail closed）。
    /// </summary>
    private bool RequiresLiveAuthorization()
    {
        var http = ControllerContext?.HttpContext;
        if (http is null) return false;
        return http.Request.Path.HasValue || CurrentUserId() is not null;
    }

    /// <summary>
    /// 身份 / 账号状态 / 既有「销售订单」菜单授权 + 权威客户数据范围（fail closed：缺失或已删除按未认证，
    /// 禁用 / 无菜单 / 未映射业务员按权限不足）。每次请求重新解析，撤销授权后下一次请求立即收敛。
    /// </summary>
    private Task<SalespersonDataScope> ResolveAuthorizedScopeAsync()
        => CustomerSalesInvoiceAuthorizationRules.EnsureMenuAuthorizedAsync(_db, CurrentUserId());

    /// <summary>
    /// 台账范围谓词：真实请求在计数 / 分页之前下推身份 / 菜单 / 权威来源范围；进程内无身份调用返回 null（既有语义不变）。
    /// </summary>
    private async Task<Expression<Func<CustomerSalesInvoiceEvidence, bool>>?> BuildScopePredicateAsync()
    {
        if (!RequiresLiveAuthorization()) return null;
        return await CustomerSalesInvoiceAuthorizationRules.BuildScopePredicateAsync(_db, CurrentUserId());
    }

    /// <summary>单证候选范围谓词：真实请求按既有单证的权威归属客户范围下推；进程内无身份调用返回 null。</summary>
    private async Task<Expression<Func<TradeDocument, bool>>?> BuildTradeDocumentScopePredicateAsync()
    {
        if (!RequiresLiveAuthorization()) return null;
        return await CustomerSalesInvoiceAuthorizationRules.BuildTradeDocumentScopePredicateAsync(_db, CurrentUserId());
    }

    /// <summary>
    /// 单张发票的范围授权：身份 / 菜单 fail closed → 发票不存在或已删除按业务「不存在」拒绝 →
    /// 复核发票<strong>全部权威客户来源</strong>（已存储客户 + 每一张来源销售订单客户 + 交叉引用单证归属客户）范围；
    /// 返回解析出的数据范围（进程内无身份调用返回 null，仅用于保持既有读取语义）。
    /// </summary>
    private async Task<SalespersonDataScope?> EnsureInvoiceIdAuthorizedAsync(long invoiceId)
    {
        if (!RequiresLiveAuthorization()) return null;
        if (invoiceId <= 0) throw BusinessException.InvalidParameter("发票 Id 不合法");

        // 身份 / 菜单 / 数据范围先行 fail closed（绝不退化为匿名或管理员，也不泄露单据存在性）。
        var scope = await ResolveAuthorizedScopeAsync();

        var invoice = await _db.CustomerSalesInvoiceEvidences.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == invoiceId && !x.IsDeleted)
            ?? throw BusinessException.NotFound("客户销项发票证据不存在或已删除");

        await CustomerSalesInvoiceAuthorizationRules.EnsureInvoiceAuthorizedWithScopeAsync(_db, scope, invoice);
        return scope;
    }

    /// <summary>
    /// 拟提议发票头的范围授权：请求客户与显式交叉引用单证都必须落在当前账号的权威客户数据范围内
    /// （受限账号越界一律在写入之前拒绝），绝不新增任何权限、绝不改派来源。
    /// </summary>
    private async Task EnsureProposedEvidenceScopeAuthorizedAsync(CustomerSalesInvoiceEvidenceSaveDto? dto)
    {
        if (!RequiresLiveAuthorization() || dto is null) return;

        await CustomerSalesInvoiceAuthorizationRules.EnsureCustomersAuthorizedAsync(
            _db, CurrentUserId(), new[] { (long?)dto.CustomerId });
        await CustomerSalesInvoiceAuthorizationRules.EnsureTradeDocumentAuthorizedAsync(
            _db, CurrentUserId(), dto.TradeDocumentId);
    }

    /// <summary>
    /// 拟提议分摊来源的范围授权：在替换 / 预览分摊行之前复核请求中每一张销售订单的来源归属
    /// （受限账号必须存在、未删除且在本人客户范围内），绝不新增任何权限。
    /// </summary>
    private async Task EnsureProposedAllocationsAuthorizedAsync(CustomerSalesInvoiceAllocationSaveRequest? request)
    {
        if (!RequiresLiveAuthorization()) return;

        var orderIds = (request?.Lines ?? new List<CustomerSalesInvoiceAllocationSaveDto>())
            .Select(l => l.SalesOrderId)
            .Where(id => id > 0)
            .Distinct()
            .ToList();
        await CustomerSalesInvoiceAuthorizationRules.EnsureSalesOrderIdsAuthorizedAsync(
            _db, CurrentUserId(), orderIds);
    }

    /// <summary>当前登录用户 Id（缺失或非数字时返回 null，由数据范围解析 fail closed 拒绝）</summary>
    private long? CurrentUserId()
        => long.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var id) ? id : null;
}
