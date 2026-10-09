using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace ERP.Api.Controllers;

/// <summary>
/// 供应商采购发票登记控制器（ERP-043，ERP-065 扩展）：登记普通发票 / 增值税专用发票 / 进口发票的**运营证据**，
/// 可（可选、显式）登记到期日与付款条件，并可（可选、显式）把含税总额关联到既有采购订单。
/// <para>边界（控制器层同样遵守）：本模块<strong>不是</strong>应付账款台账、<strong>不是</strong>税务申报系统、
/// <strong>不是</strong>付款授权机制；所有接口只读写 <c>PurchaseInvoices</c> / <c>PurchaseInvoiceAllocations</c> 两张表，
/// <strong>不</strong>改写采购订单状态 / 到货进度 / 金额与明细、库存与库存成本、库存流水、退税记录、
/// 供应商余额与结算方式、付款状态，也不记账、不生成凭证 / 收款 / 付款 / 结算单；
/// 生产库结构变更仍由 Human Gate 控制（本控制器不做任何 DDL，建表 / 索引 / 加列由 SchemaUpgrader 幂等补齐）。</para>
/// </summary>
[ApiController]
[Route("api/purchase-invoices")]
[Authorize]
public class PurchaseInvoiceController : ControllerBase
{
    private readonly IErpDbContext _db;

    public PurchaseInvoiceController(IErpDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// 发票台账（分页，只读）：可按供应商 / 发票类型 / 状态 / 币种 / 开票日期区间 / 关联状态 / 关键字过滤；
    /// 默认包含已作废历史（证据保留可读），关联状态按持久化关联行派生，未关联金额绝不被猜测到任何订单。
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetPaged([FromQuery] PurchaseInvoiceQuery query)
        => Ok(ApiResponse<PagedResult<PurchaseInvoiceDto>>.Success(
            await PurchaseInvoiceService.ListAsync(_db, query, await BuildScopePredicateAsync())));

    /// <summary>发票详情（含关联行、已关联 / 未关联金额与订单可用性标注；只读）</summary>
    [HttpGet("{id:long}")]
    public async Task<IActionResult> GetById(long id)
    {
        await EnsureInvoiceIdAuthorizedAsync(id);
        return Ok(ApiResponse<PurchaseInvoiceDto>.Success(
            await PurchaseInvoiceService.GetAsync(_db, id)));
    }

    /// <summary>
    /// 供应商采购发票对账报表（ERP-044，**只读派生**）：按「供应商 + 币种」分组核对采购订单与**已登记（未作废）**
    /// 发票证据 —— 订单侧暴露订单金额 / 已开票金额 / 未开票余额（只按 ERP-043 持久化关联行派生），
    /// 发票侧把已关联与**未关联**金额分开显示（未关联金额绝不猜测到任何订单）；已作废发票默认排除，
    /// 需显式选择证据状态筛选才可见，且其金额永不并入有效合计。
    /// <para>本接口<strong>不是</strong>应付账款台账、<strong>不是</strong>付款授权、<strong>不是</strong>税务申报报表，
    /// 也<strong>不是</strong>账龄表：不推断账期与到期日、不判断是否已付款，且<strong>不写库</strong>
    /// （不改发票 / 采购订单 / 收退货进度 / 库存成本 / 付款 / 费用 / 退税记录）。</para>
    /// </summary>
    [HttpGet("reconciliation")]
    public async Task<IActionResult> Reconciliation([FromQuery] SupplierInvoiceReconciliationQuery query)
    {
        await EnsureMenuAuthorizedAsync();
        return Ok(ApiResponse<SupplierInvoiceReconciliationReport>.Success(
            await SupplierInvoiceReconciliation.ForQueryAsync(_db, query)));
    }

    /// <summary>
    /// 供应商对账与账龄工作台（ERP-068，**只读派生**）：按「供应商 + 币种」列出 ERP-065 持久化供应商采购发票证据、
    /// ERP-066 有效已分配付款引用证据与算术剩余证据（含税总额 − 有效已分配），并按**显式到期日**与**显式 as-of 日期**
    /// 计算互斥账龄桶；未登记到期日的发票进入独立的「未知到期日」分组，不同币种分别成行、绝不合并、不做汇率换算。
    /// <para>本接口<strong>不是</strong>总账或应付账款余额、<strong>不是</strong>法定供应商对账单、<strong>不是</strong>付款授权、
    /// <strong>不是</strong>税务申报、<strong>不是</strong>结算确认：它<strong>不写库</strong>、不改任何发票 / 引用行 / 付款单 /
    /// 采购订单 / 库存 / 财务 / 税务记录，也不新增或修改任何表列（仓库对账口径的证据数字）。</para>
    /// </summary>
    [HttpGet("reconciliation-aging")]
    public async Task<IActionResult> ReconciliationAging([FromQuery] SupplierReconciliationAgingQuery query)
    {
        await EnsureMenuAuthorizedAsync();
        return Ok(ApiResponse<SupplierReconciliationAgingReport>.Success(
            await SupplierReconciliationAging.ForQueryAsync(_db, query)));
    }

    /// <summary>
    /// 单张发票的对账与账龄证据明细（ERP-068，**只读派生**，与列表同一派生口径）：打开明细时**重新校验**
    /// 当前登录身份与既有「角色 → 菜单」模块授权（<c>SysUserRoles</c> → <c>SysRoleMenus</c> → <c>SysMenus</c>），
    /// 并重新读取权威来源；未认证 / 未获该模块授权 / 发票不存在或已删除时一律拒绝（fail closed），
    /// 不返回任何部分证据，也不做来源修复或改派。
    /// </summary>
    [HttpGet("reconciliation-aging/invoices/{invoiceId:long}")]
    public async Task<IActionResult> ReconciliationAgingInvoiceDetail(
        long invoiceId, [FromQuery] DateTime? asOfDate = null)
    {
        await EnsureInvoiceIdAuthorizedAsync(invoiceId);
        return Ok(ApiResponse<SupplierReconciliationAgingInvoiceDetail>.Success(
            await SupplierReconciliationAging.ForInvoiceDetailAsync(_db, invoiceId, CurrentUserId(), asOfDate)));
    }

    // ==================== ERP-382：实时授权辅助（身份 / 菜单 / 权威来源范围） ====================

    /// <summary>
    /// 是否必须执行实时授权（ERP-462）：<strong>只要控制器绑定到 HTTP 请求管线（<c>ControllerContext.HttpContext</c> 存在）
    /// 就一律执行</strong>，与请求路径是否赋值、以及当前是否携带可解析的登录身份<strong>完全无关</strong>：
    /// 空路径请求与已赋值路径请求的授权口径完全一致，缺少身份的真实请求同样 fail closed（未认证），
    /// 既不存在「空路径 / 无身份」的请求形状旁路，也绝不把缺失身份当作管理员，绝无匿名或管理员兜底。
    /// <para>仅「未被任何请求绑定」（<c>HttpContext</c> 为 null 的纯进程内直接调用，外部请求无法到达，例如既有进程内
    /// 单元测试夹具 <c>PurchaseInvoiceTests</c>）沿用仓库一致的进程内直调边界：该边界不读取请求路径、
    /// 不读取环境变量、也不使用任何测试专用开关，因此不是请求形状旁路，也不能由任何外部请求到达。</para>
    /// </summary>
    private bool RequiresLiveAuthorization()
        => ControllerContext?.HttpContext is not null;

    /// <summary>
    /// 身份 / 账号状态 / 既有「采购订单」菜单授权（fail closed：缺失 / 已删除按未认证，禁用 / 无菜单按权限不足）。
    /// 与请求路径是否赋值无关：只要控制器绑定到请求管线（空路径或已赋值路径）就一律执行。
    /// </summary>
    private async Task EnsureMenuAuthorizedAsync()
    {
        if (!RequiresLiveAuthorization()) return;
        await PurchaseInvoiceAuthorizationRules.EnsureMenuAuthorizedAsync(_db, CurrentUserId());
    }

    /// <summary>
    /// 台账范围谓词：请求在计数 / 分页之前下推身份 / 菜单 / 权威来源范围（空路径 / 无身份请求与已认证请求口径一致，
    /// 一律 fail closed）；仅未绑定任何请求的纯进程内直调返回 null（既有进程内语义不变）。
    /// </summary>
    private async Task<System.Linq.Expressions.Expression<Func<ERP.Domain.Entities.PurchaseInvoice, bool>>?>
        BuildScopePredicateAsync()
    {
        if (!RequiresLiveAuthorization()) return null;
        return await PurchaseInvoiceAuthorizationRules.BuildScopePredicateAsync(_db, CurrentUserId());
    }

    /// <summary>
    /// 单张发票的范围授权：身份 / 菜单 fail closed → 发票不存在或已删除按业务「不存在」拒绝 →
    /// 复核发票<strong>全部来源采购订单</strong>的权威归属客户范围（受限账号无来源 / 越界一律拒绝）。
    /// </summary>
    private async Task EnsureInvoiceIdAuthorizedAsync(long invoiceId)
    {
        if (!RequiresLiveAuthorization()) return;
        if (invoiceId <= 0) throw BusinessException.InvalidParameter("发票 Id 不合法");

        var invoice = await _db.PurchaseInvoices.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == invoiceId && !x.IsDeleted)
            ?? throw BusinessException.NotFound($"供应商采购发票（Id={invoiceId}）不存在或已删除");

        await PurchaseInvoiceAuthorizationRules.EnsureInvoiceAuthorizedAsync(_db, CurrentUserId(), invoice);
    }

    /// <summary>
    /// 拟提议关联的来源范围授权：在替换关联行之前复核请求中每一张采购订单的来源归属
    /// （受限账号必须至少有一张且在本人客户范围内，特权账号放行），绝不新增任何权限。
    /// </summary>
    private async Task EnsureProposedAllocationsAuthorizedAsync(PurchaseInvoiceAllocationSaveRequest? request)
    {
        if (!RequiresLiveAuthorization()) return;

        var orderIds = (request?.Lines ?? new List<PurchaseInvoiceAllocationSaveDto>())
            .Select(l => l.PurchaseOrderId)
            .Where(id => id > 0)
            .Distinct()
            .ToList();
        await PurchaseInvoiceAuthorizationRules.EnsureOrderIdsAuthorizedAsync(_db, CurrentUserId(), orderIds);
    }

    /// <summary>当前登录用户 Id（缺失或非数字时返回 null，由明细接口 fail closed 拒绝，绝不猜测身份）</summary>
    private long? CurrentUserId()
        => long.TryParse(User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value, out var id)
            ? id
            : null;

    /// <summary>
    /// 新增草稿发票：校验发票类型 / 代码 / 号码、供应商（必须存在且启用）、币种与金额等式
    /// （含税总额 = 不含税金额 + 税额，按币种精度取整后严格相等），校验可选到期日（不得早于开票日期；
    /// 留空 = 未知，不按供应商默认账期推算）与付款条件（有界文本快照），并拒绝重复身份。
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] PurchaseInvoiceSaveDto dto)
    {
        await EnsureMenuAuthorizedAsync();
        return Ok(ApiResponse<PurchaseInvoiceDto>.Success(
            await PurchaseInvoiceService.CreateAsync(_db, dto), "发票草稿已登记"));
    }

    /// <summary>
    /// 修改草稿发票（已登记 / 已作废拒绝修改；已有采购订单关联时不允许更换供应商或币种）；
    /// 到期日与付款条件同样是可选、显式证据（可清空为「未知 / 未提供」，系统不会自动补值）。
    /// </summary>
    [HttpPut("{id:long}")]
    public async Task<IActionResult> Update(long id, [FromBody] PurchaseInvoiceSaveDto dto)
    {
        await EnsureInvoiceIdAuthorizedAsync(id);
        return Ok(ApiResponse<PurchaseInvoiceDto>.Success(
            await PurchaseInvoiceService.UpdateAsync(_db, id, dto), "发票草稿已更新"));
    }

    /// <summary>
    /// 可关联采购订单候选（只读、有界）：只返回同供应商 + 同币种的订单（含已取消订单并标注不可关联），
    /// 附带订单总额、本发票已关联、其他有效发票已关联与剩余未被发票证据覆盖的金额（派生值，不是应付余额）。
    /// </summary>
    [HttpGet("{id:long}/order-candidates")]
    public async Task<IActionResult> OrderCandidates(
        long id, [FromQuery] string? keyword, [FromQuery] int take = PurchaseInvoiceRules.MaxOrderCandidates)
    {
        await EnsureInvoiceIdAuthorizedAsync(id);
        return Ok(ApiResponse<List<PurchaseInvoiceOrderCandidateDto>>.Success(
            await PurchaseInvoiceService.ListOrderCandidatesAsync(_db, id, keyword, take)));
    }

    /// <summary>
    /// 关联预览（**只读，不写库**）：逐行校验采购订单资格与关联金额，返回订单快照、资格文案、
    /// 拟关联合计与保存后的已关联 / 未关联金额；预览与保存共用同一校验口径。
    /// </summary>
    [HttpPost("{id:long}/allocations/preview")]
    public async Task<IActionResult> PreviewAllocations(
        long id, [FromBody] PurchaseInvoiceAllocationSaveRequest? request)
    {
        await EnsureInvoiceIdAuthorizedAsync(id);
        await EnsureProposedAllocationsAuthorizedAsync(request);
        return Ok(ApiResponse<PurchaseInvoiceAllocationPreviewDto>.Success(
            await PurchaseInvoiceService.PreviewAllocationsAsync(_db, id, request), "关联预览完成（未写库）"));
    }

    /// <summary>
    /// 保存关联（草稿专用，整体替换，事务性）：同一发票内同一订单不重复、供应商与币种必须一致、
    /// 关联金额合计不得超过含税总额与来源订单的发票容量；已登记 / 已作废发票拒绝任何关联改动。
    /// </summary>
    [HttpPost("{id:long}/allocations")]
    public async Task<IActionResult> SaveAllocations(
        long id, [FromBody] PurchaseInvoiceAllocationSaveRequest? request)
    {
        // ERP-382：先复核「已存储来源」与「拟提议来源」的完整授权范围，再做整体替换（被拒绝者绝不改写任何关联行）。
        await EnsureInvoiceIdAuthorizedAsync(id);
        await EnsureProposedAllocationsAuthorizedAsync(request);
        return Ok(ApiResponse<PurchaseInvoiceDto>.Success(
            await PurchaseInvoiceService.SaveAllocationsAsync(_db, id, request), "发票关联已保存"));
    }

    /// <summary>
    /// 登记发票（草稿 → 已登记）：登记前复核已持久化关联行仍权威可关联；只改发票状态与登记时间，
    /// 不改写采购订单、库存、退税、供应商余额与付款状态，也不生成任何财务单据。
    /// </summary>
    [HttpPost("{id:long}/record")]
    public async Task<IActionResult> Record(long id)
    {
        await EnsureInvoiceIdAuthorizedAsync(id);
        return Ok(ApiResponse<PurchaseInvoiceDto>.Success(
            await PurchaseInvoiceService.RecordAsync(_db, id), "发票已登记（证据已冻结，可作废但不可改写）"));
    }

    /// <summary>
    /// 作废发票（必须填写作废原因）：保留身份、金额、关联行与审计历史，不物理删除、不改写已登记证据，
    /// 也不产生任何收付款 / 记账动作；重复作废被拒绝。
    /// </summary>
    [HttpPost("{id:long}/void")]
    public async Task<IActionResult> Void(long id, [FromBody] PurchaseInvoiceVoidRequest? request)
    {
        await EnsureInvoiceIdAuthorizedAsync(id);
        return Ok(ApiResponse<PurchaseInvoiceDto>.Success(
            await PurchaseInvoiceService.VoidAsync(_db, id, request?.Reason),
            "发票已作废（历史证据与关联保留，可读）"));
    }
}
