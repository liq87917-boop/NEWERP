using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ERP.Api.Controllers;

/// <summary>
/// 装柜出运引用登记控制器（ERP-057）：为既有装柜链路记录（订柜信息 / 预装柜单 / 装柜清单）登记
/// 用户录入的出运引用证据（出运方式 / 订舱号 / 提单号 / 港口 / 计划时间 / 承运人 / 货代 / 拖车 / 报关行快照），
/// 并支持带修订留痕的修订与显式作废。
/// <para>审计口径：装柜链路已有唯一的持久化引用关系（<c>ContainerPreLoading.BookingId</c> /
/// <c>ContainerLoadingList.PreLoadingId</c>），订柜信息由 ERP-040 承载本套跟踪值的权威记录；
/// 本控制器<strong>不</strong>新建出运主数据、<strong>不</strong>在装柜三单上加列、也<strong>不</strong>按柜号 /
/// 订单号 / 单证号等自由文本匹配记录。</para>
/// <para>边界（控制器层同样遵守）：所有接口只读写 <c>ContainerShipmentReferences</c> 与
/// <c>ContainerShipmentReferenceRevisions</c> 两张表，<strong>不</strong>改写源记录的状态与工作流、
/// <strong>不</strong>改写销售订单、采购订单、库存与库存成本、库存流水、单证中心、发票、费用与分摊、
/// 收付款、税务与结算记录，也不联系承运人、海关、货代或任何外部跟踪系统；
/// 生产库结构变更仍由 Human Gate 控制（本控制器不做任何 DDL，建表 / 索引由 SchemaUpgrader 幂等补齐）。</para>
/// </summary>
[ApiController]
[Route("api/container/shipment-references")]
[Authorize]
public class ContainerShipmentReferenceController : ControllerBase
{
    private readonly IErpDbContext _db;

    public ContainerShipmentReferenceController(IErpDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// 出运引用台账（分页，只读）：可按源记录类型 / Id、状态、出运方式、登记日期区间与关键字过滤；
    /// 默认包含已作废历史（证据保留可读），源记录与报关行可用性都是只读标注。
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetPaged([FromQuery] ContainerShipmentReferenceQuery query)
        => Ok(ApiResponse<PagedResult<ContainerShipmentReferenceDto>>.Success(
            await ContainerShipmentReferenceService.ListAsync(_db, query)));

    /// <summary>
    /// 可登记出运引用的源记录候选（只读、有界）：只列出指定类型下未删除的既有记录，并标注是否已有有效引用；
    /// 仅用于**显式选择**源记录，不按柜号 / 单号等自由文本猜测。
    /// </summary>
    [HttpGet("source-candidates")]
    public async Task<IActionResult> SourceCandidates(
        [FromQuery] string sourceType,
        [FromQuery] string? keyword,
        [FromQuery] int take = ContainerShipmentReferenceRules.MaxSourceCandidates)
        => Ok(ApiResponse<List<ContainerShipmentReferenceSourceCandidateDto>>.Success(
            await ContainerShipmentReferenceService.ListSourceCandidatesAsync(_db, sourceType, keyword, take)));

    /// <summary>
    /// 报关行下拉选项（只读，复用 ERP-040 同一口径）：只返回未删除、已启用、类型为 CustomsBroker 的字典项，
    /// 与写入校验完全一致，因此已停用 / 已删除 / 类型不符的字典项不会出现在下拉中。
    /// </summary>
    [HttpGet("customs-broker-options")]
    public async Task<IActionResult> CustomsBrokerOptions()
        => Ok(ApiResponse<List<OtherInfoOptionDto>>.Success(
            await ContainerShipmentTrackingService.LoadCustomsBrokerOptionsAsync(_db)));

    /// <summary>出运引用详情（含只读标注与有界修订留痕；只读）</summary>
    [HttpGet("{id:long}")]
    public async Task<IActionResult> GetById(long id)
        => Ok(ApiResponse<ContainerShipmentReferenceDetailDto>.Success(
            await ContainerShipmentReferenceService.GetAsync(_db, id)));

    /// <summary>出运引用的修订留痕（只读、有界；按修订号倒序，最多 50 条）</summary>
    [HttpGet("{id:long}/revisions")]
    public async Task<IActionResult> Revisions(
        long id, [FromQuery] int take = ContainerShipmentReferenceRules.MaxRevisionTake)
        => Ok(ApiResponse<List<ContainerShipmentReferenceRevisionDto>>.Success(
            await ContainerShipmentReferenceService.ListRevisionsAsync(_db, id, take)));

    /// <summary>
    /// 登记出运引用证据：源记录必须是显式类型下存在且未删除的既有记录，同一条源记录最多 1 条有效引用；
    /// 出运方式只接受 LCL / FCL / 未指定，计划开船时间不得晚于计划到港时间，报关行必须是可选用字典项；
    /// 登记<strong>不</strong>推进装柜状态、<strong>不</strong>改柜号、<strong>不</strong>写任何单据号。
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] ContainerShipmentReferenceSaveDto dto)
        => Ok(ApiResponse<ContainerShipmentReferenceDto>.Success(
            await ContainerShipmentReferenceService.CreateAsync(_db, dto),
            "出运引用证据已登记（只登记证据：未推进装柜状态、未联系承运人 / 海关 / 货代）"));

    /// <summary>
    /// 修订出运引用证据（必须填写修订原因）：服务端先把修订前的原值写入只追加的修订留痕，再写回新值并递增修订号；
    /// 不允许改派源记录，已作废引用只读。
    /// </summary>
    [HttpPut("{id:long}")]
    public async Task<IActionResult> Update(long id, [FromBody] ContainerShipmentReferenceUpdateDto dto)
        => Ok(ApiResponse<ContainerShipmentReferenceDto>.Success(
            await ContainerShipmentReferenceService.UpdateAsync(_db, id, dto),
            "出运引用证据已修订（修订前的原值已写入修订留痕，可追溯）"));

    /// <summary>
    /// 作废出运引用证据（必须填写原因）：保留原始证据、源记录快照与全部修订留痕，不物理删除；
    /// 重复作废被拒绝；作废后该源记录可重新登记一条新的有效引用。
    /// </summary>
    [HttpPost("{id:long}/void")]
    public async Task<IActionResult> Void(long id, [FromBody] ContainerShipmentReferenceVoidRequest? request)
        => Ok(ApiResponse<ContainerShipmentReferenceDto>.Success(
            await ContainerShipmentReferenceService.VoidAsync(_db, id, request?.Reason),
            "出运引用证据已作废（原始值与修订留痕保留可读）"));
}
