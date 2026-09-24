using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

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
        => Ok(ApiResponse<PagedResult<CustomerSalesInvoiceEvidenceDto>>.Success(
            await CustomerSalesInvoiceEvidenceService.ListAsync(_db, query)));

    /// <summary>
    /// 可显式交叉引用的单证中心商业发票候选（只读、有界）：只列出既有、未删除且类型为商业发票的单证，
    /// 仅用于显式选择交叉引用来源；不读取单证金额、不转换单证、不建立自动链接。
    /// </summary>
    [HttpGet("commercial-invoice-candidates")]
    public async Task<IActionResult> CommercialInvoiceCandidates(
        [FromQuery] string? keyword,
        [FromQuery] int take = CustomerSalesInvoiceEvidenceRules.MaxTradeDocumentCandidates)
        => Ok(ApiResponse<List<CustomerSalesInvoiceTradeDocumentCandidateDto>>.Success(
            await CustomerSalesInvoiceEvidenceService.ListTradeDocumentCandidatesAsync(_db, keyword, take)));

    /// <summary>发票证据详情（含分摊行、已分摊 / 未分摊金额与订单可用性标注；只读）</summary>
    [HttpGet("{id:long}")]
    public async Task<IActionResult> GetById(long id)
        => Ok(ApiResponse<CustomerSalesInvoiceEvidenceDto>.Success(
            await CustomerSalesInvoiceEvidenceService.GetAsync(_db, id)));

    /// <summary>
    /// 新增草稿发票证据：校验发票类型 / 代码 / 号码、客户（必须存在且启用）、币种与金额等式
    /// （含税总额 = 不含税金额 + 税额，按币种精度取整后严格相等）、可选单证交叉引用，并拒绝重复身份。
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CustomerSalesInvoiceEvidenceSaveDto dto)
        => Ok(ApiResponse<CustomerSalesInvoiceEvidenceDto>.Success(
            await CustomerSalesInvoiceEvidenceService.CreateAsync(_db, dto),
            "销项发票证据草稿已登记（仅证据留痕；未开票、未报税、未记账）"));

    /// <summary>修改草稿发票证据（已登记 / 已作废拒绝修改；已有销售订单分摊时不允许更换客户或币种）</summary>
    [HttpPut("{id:long}")]
    public async Task<IActionResult> Update(long id, [FromBody] CustomerSalesInvoiceEvidenceSaveDto dto)
        => Ok(ApiResponse<CustomerSalesInvoiceEvidenceDto>.Success(
            await CustomerSalesInvoiceEvidenceService.UpdateAsync(_db, id, dto),
            "销项发票证据草稿已更新"));

    /// <summary>
    /// 可分摊销售订单候选（只读、有界）：只返回同客户 + 同币种的订单（含已取消订单并标注不可分摊），
    /// 附带订单总额、本发票已分摊、其他有效发票已分摊与剩余未被发票证据覆盖的金额（派生值，不是应收余额）。
    /// </summary>
    [HttpGet("{id:long}/order-candidates")]
    public async Task<IActionResult> OrderCandidates(
        long id, [FromQuery] string? keyword,
        [FromQuery] int take = CustomerSalesInvoiceEvidenceRules.MaxOrderCandidates)
        => Ok(ApiResponse<List<CustomerSalesInvoiceOrderCandidateDto>>.Success(
            await CustomerSalesInvoiceEvidenceService.ListOrderCandidatesAsync(_db, id, keyword, take)));

    /// <summary>
    /// 分摊预览（**只读，不写库**）：逐行校验销售订单资格与分摊金额，返回订单快照、资格文案、
    /// 拟分摊合计与保存后的已分摊 / 未分摊金额；预览与保存共用同一校验口径。
    /// </summary>
    [HttpPost("{id:long}/allocations/preview")]
    public async Task<IActionResult> PreviewAllocations(
        long id, [FromBody] CustomerSalesInvoiceAllocationSaveRequest? request)
        => Ok(ApiResponse<CustomerSalesInvoiceAllocationPreviewDto>.Success(
            await CustomerSalesInvoiceEvidenceService.PreviewAllocationsAsync(_db, id, request),
            "分摊预览完成（未写库）"));

    /// <summary>
    /// 保存分摊（草稿专用，整体替换）：同一发票内同一订单不重复、客户与币种必须一致、
    /// 分摊金额合计不得超过含税总额；已登记 / 已作废发票拒绝任何分摊改动。
    /// </summary>
    [HttpPost("{id:long}/allocations")]
    public async Task<IActionResult> SaveAllocations(
        long id, [FromBody] CustomerSalesInvoiceAllocationSaveRequest? request)
        => Ok(ApiResponse<CustomerSalesInvoiceEvidenceDto>.Success(
            await CustomerSalesInvoiceEvidenceService.SaveAllocationsAsync(_db, id, request),
            "发票分摊已保存"));

    /// <summary>
    /// 登记发票证据（草稿 → 已登记）：登记前复核已持久化分摊行仍权威可分摊；只改发票状态与登记时间，
    /// <strong>不</strong>开具真实发票、<strong>不</strong>调用任何开票 / 税务服务、<strong>不</strong>改动销售订单、
    /// 客户信用状态、收款单与其引用行、库存与财务记录。
    /// </summary>
    [HttpPost("{id:long}/record")]
    public async Task<IActionResult> Record(long id)
        => Ok(ApiResponse<CustomerSalesInvoiceEvidenceDto>.Success(
            await CustomerSalesInvoiceEvidenceService.RecordAsync(_db, id),
            "销项发票证据已登记（证据已冻结，可作废但不可改写；未开票、未报税、未记账）"));

    /// <summary>
    /// 作废发票证据（必须填写作废原因）：保留身份、金额、分摊行与审计历史，不物理删除、不改写已登记证据，
    /// 也<strong>不</strong>作废任何真实发票、不产生任何财务 / 税务动作；重复作废被拒绝。
    /// </summary>
    [HttpPost("{id:long}/void")]
    public async Task<IActionResult> Void(long id, [FromBody] CustomerSalesInvoiceVoidRequest? request)
        => Ok(ApiResponse<CustomerSalesInvoiceEvidenceDto>.Success(
            await CustomerSalesInvoiceEvidenceService.VoidAsync(_db, id, request?.Reason),
            "销项发票证据已作废（历史证据与分摊保留，可读）"));
}
