using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ERP.Api.Controllers;

/// <summary>
/// 供应商采购发票登记控制器（ERP-043）：登记普通发票 / 增值税专用发票的**运营证据**，
/// 并可（可选、显式）把含税总额关联到既有采购订单。
/// <para>边界（控制器层同样遵守）：本模块<strong>不是</strong>应付账款台账、<strong>不是</strong>税务申报系统、
/// <strong>不是</strong>付款授权机制；所有接口只读写 <c>PurchaseInvoices</c> / <c>PurchaseInvoiceAllocations</c> 两张表，
/// <strong>不</strong>改写采购订单状态 / 到货进度 / 金额与明细、库存与库存成本、库存流水、退税记录、
/// 供应商余额与结算方式、付款状态，也不记账、不生成凭证 / 收款 / 付款 / 结算单；
/// 生产库结构变更仍由 Human Gate 控制（本控制器不做任何 DDL，建表 / 索引由 SchemaUpgrader 幂等补齐）。</para>
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
            await PurchaseInvoiceService.ListAsync(_db, query)));

    /// <summary>发票详情（含关联行、已关联 / 未关联金额与订单可用性标注；只读）</summary>
    [HttpGet("{id:long}")]
    public async Task<IActionResult> GetById(long id)
        => Ok(ApiResponse<PurchaseInvoiceDto>.Success(
            await PurchaseInvoiceService.GetAsync(_db, id)));

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
        => Ok(ApiResponse<SupplierInvoiceReconciliationReport>.Success(
            await SupplierInvoiceReconciliation.ForQueryAsync(_db, query)));

    /// <summary>
    /// 新增草稿发票：校验发票类型 / 代码 / 号码、供应商（必须存在且启用）、币种与金额等式
    /// （含税总额 = 不含税金额 + 税额，按币种精度取整后严格相等），并拒绝重复身份。
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] PurchaseInvoiceSaveDto dto)
        => Ok(ApiResponse<PurchaseInvoiceDto>.Success(
            await PurchaseInvoiceService.CreateAsync(_db, dto), "发票草稿已登记"));

    /// <summary>修改草稿发票（已登记 / 已作废拒绝修改；已有采购订单关联时不允许更换供应商或币种）</summary>
    [HttpPut("{id:long}")]
    public async Task<IActionResult> Update(long id, [FromBody] PurchaseInvoiceSaveDto dto)
        => Ok(ApiResponse<PurchaseInvoiceDto>.Success(
            await PurchaseInvoiceService.UpdateAsync(_db, id, dto), "发票草稿已更新"));

    /// <summary>
    /// 可关联采购订单候选（只读、有界）：只返回同供应商 + 同币种的订单（含已取消订单并标注不可关联），
    /// 附带订单总额、本发票已关联、其他有效发票已关联与剩余未被发票证据覆盖的金额（派生值，不是应付余额）。
    /// </summary>
    [HttpGet("{id:long}/order-candidates")]
    public async Task<IActionResult> OrderCandidates(
        long id, [FromQuery] string? keyword, [FromQuery] int take = PurchaseInvoiceRules.MaxOrderCandidates)
        => Ok(ApiResponse<List<PurchaseInvoiceOrderCandidateDto>>.Success(
            await PurchaseInvoiceService.ListOrderCandidatesAsync(_db, id, keyword, take)));

    /// <summary>
    /// 关联预览（**只读，不写库**）：逐行校验采购订单资格与关联金额，返回订单快照、资格文案、
    /// 拟关联合计与保存后的已关联 / 未关联金额；预览与保存共用同一校验口径。
    /// </summary>
    [HttpPost("{id:long}/allocations/preview")]
    public async Task<IActionResult> PreviewAllocations(
        long id, [FromBody] PurchaseInvoiceAllocationSaveRequest? request)
        => Ok(ApiResponse<PurchaseInvoiceAllocationPreviewDto>.Success(
            await PurchaseInvoiceService.PreviewAllocationsAsync(_db, id, request), "关联预览完成（未写库）"));

    /// <summary>
    /// 保存关联（草稿专用，整体替换，事务性）：同一发票内同一订单不重复、供应商与币种必须一致、
    /// 关联金额合计不得超过含税总额；已登记 / 已作废发票拒绝任何关联改动。
    /// </summary>
    [HttpPost("{id:long}/allocations")]
    public async Task<IActionResult> SaveAllocations(
        long id, [FromBody] PurchaseInvoiceAllocationSaveRequest? request)
        => Ok(ApiResponse<PurchaseInvoiceDto>.Success(
            await PurchaseInvoiceService.SaveAllocationsAsync(_db, id, request), "发票关联已保存"));

    /// <summary>
    /// 登记发票（草稿 → 已登记）：登记前复核已持久化关联行仍权威可关联；只改发票状态与登记时间，
    /// 不改写采购订单、库存、退税、供应商余额与付款状态，也不生成任何财务单据。
    /// </summary>
    [HttpPost("{id:long}/record")]
    public async Task<IActionResult> Record(long id)
        => Ok(ApiResponse<PurchaseInvoiceDto>.Success(
            await PurchaseInvoiceService.RecordAsync(_db, id), "发票已登记（证据已冻结，可作废但不可改写）"));

    /// <summary>
    /// 作废发票（必须填写作废原因）：保留身份、金额、关联行与审计历史，不物理删除、不改写已登记证据，
    /// 也不产生任何收付款 / 记账动作；重复作废被拒绝。
    /// </summary>
    [HttpPost("{id:long}/void")]
    public async Task<IActionResult> Void(long id, [FromBody] PurchaseInvoiceVoidRequest? request)
        => Ok(ApiResponse<PurchaseInvoiceDto>.Success(
            await PurchaseInvoiceService.VoidAsync(_db, id, request?.Reason),
            "发票已作废（历史证据与关联保留，可读）"));
}
