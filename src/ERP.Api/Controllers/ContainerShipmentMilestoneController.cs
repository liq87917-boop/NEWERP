using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ERP.Api.Controllers;

/// <summary>
/// 装柜出运里程碑证据控制器（ERP-058）：在 ERP-057 出运引用记录**之下**登记用户录入的操作性
/// 里程碑证据（实际开船 / 实际到港 / 查验 / 放行），只追加、显式作废，并支持分页台账与详情读取。
/// <para>审计口径：ERP-057 已建立唯一权威的出运引用登记册，本控制器只在其之下追加事件留痕；
/// <strong>不</strong>新建出运 / 跟踪主数据、<strong>不</strong>在装柜三单或出运引用上加列，也
/// <strong>不</strong>按柜号 / S/O / B/L 等自由文本匹配任何记录。</para>
/// <para>边界（控制器层同样遵守）：所有接口只读写 <c>ContainerShipmentMilestones</c> 一张表
/// （读取时只读父出运引用），<strong>不</strong>改写父出运引用与订柜信息 / 预装柜单 / 装柜清单的状态与
/// 工作流，<strong>不</strong>自动推进任何业务单据，<strong>不</strong>改写销售订单、采购订单、库存与库存成本、
/// 库存流水、单证中心、发票、费用与分摊、收付款、税务与结算记录，也不轮询承运人、海关、货代或任何外部系统
/// （查验 / 放行只是仓库操作性证据，不是海关决定，也不代表允许出运）；生产库结构变更仍由 Human Gate 控制
/// （本控制器不做任何 DDL，建表 / 索引由 SchemaUpgrader 幂等补齐）。</para>
/// </summary>
[ApiController]
[Route("api/container/shipment-milestones")]
[Authorize]
public class ContainerShipmentMilestoneController : ControllerBase
{
    private readonly IErpDbContext _db;

    public ContainerShipmentMilestoneController(IErpDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// 里程碑台账（分页，只读）：可按父出运引用、事件类型、状态、事件时间区间与关键字过滤；
    /// 默认包含已作废历史（证据保留可读），父记录可用性为只读标注。
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetPaged([FromQuery] ContainerShipmentMilestoneQuery query)
        => Ok(ApiResponse<PagedResult<ContainerShipmentMilestoneDto>>.Success(
            await ContainerShipmentMilestoneService.ListAsync(_db, query)));

    /// <summary>
    /// 可挂里程碑证据的出运引用候选（只读、有界）：只列出未删除、未作废的 ERP-057 出运引用，
    /// 并标注已有里程碑条数（含已作废历史）；仅用于**显式选择**父记录，不按柜号 / S/O / B/L 猜测。
    /// </summary>
    [HttpGet("parent-candidates")]
    public async Task<IActionResult> ParentCandidates(
        [FromQuery] string? keyword,
        [FromQuery] int take = ContainerShipmentMilestoneRules.MaxParentCandidates)
        => Ok(ApiResponse<List<ContainerShipmentMilestoneParentCandidateDto>>.Success(
            await ContainerShipmentMilestoneService.ListParentCandidatesAsync(_db, keyword, take)));

    /// <summary>里程碑证据详情（含父记录可用性标注与证据性质文案；只读）</summary>
    [HttpGet("{id:long}")]
    public async Task<IActionResult> GetById(long id)
        => Ok(ApiResponse<ContainerShipmentMilestoneDetailDto>.Success(
            await ContainerShipmentMilestoneService.GetAsync(_db, id)));

    /// <summary>
    /// 登记里程碑证据：父记录必须是存在、未删除且未作废的 ERP-057 出运引用；事件类型只接受
    /// 实际开船 / 实际到港 / 查验 / 放行，事件时间必填且有界；同一父记录 + 类型 + 时间重复登记被拒绝；
    /// 登记<strong>不</strong>推进任何业务状态、<strong>不</strong>改写任何既有记录。
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] ContainerShipmentMilestoneSaveDto dto)
        => Ok(ApiResponse<ContainerShipmentMilestoneDto>.Success(
            await ContainerShipmentMilestoneService.CreateAsync(_db, dto),
            "里程碑证据已登记（只登记仓库操作性证据：未推进装柜 / 报关 / 出运状态，未联系承运人 / 海关 / 货代）"));

    /// <summary>
    /// 作废里程碑证据（必须填写原因）：保留原始事件类型、事件时间、来源说明与作废原因，
    /// 不物理删除；重复作废被拒绝；作废后同一父记录 + 类型 + 时间可重新登记一条新的有效证据。
    /// </summary>
    [HttpPost("{id:long}/void")]
    public async Task<IActionResult> Void(long id, [FromBody] ContainerShipmentMilestoneVoidRequest? request)
        => Ok(ApiResponse<ContainerShipmentMilestoneDto>.Success(
            await ContainerShipmentMilestoneService.VoidAsync(_db, id, request?.Reason),
            "里程碑证据已作废（原始事件类型 / 时间 / 来源与作废原因保留可读）"));
}
