using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ERP.Api.Controllers;

/// <summary>
/// 销售订单变更申请控制器（ERP-047）：登记「拟议变更」并提供来源 / 拟议对照、提交与取消留痕。
/// <para>边界（控制器层同样遵守）：本模块<strong>不</strong>审核、<strong>不</strong>套用、<strong>不</strong>定义审批阈值、
/// <strong>不</strong>发号生效，也<strong>不</strong>提供硬删除（更正走取消并保留原因）；
/// 所有接口只读写 <c>SalesOrderChangeRequests</c> / <c>SalesOrderChangeRequestDetails</c> 两张表，
/// <strong>不</strong>改写来源销售订单与报价单 / 出库 / 装柜与出运 / 收款与发票 / 佣金 / 库存 / 单证 / 财务记录；
/// 生产库结构变更仍由 Human Gate 控制（本控制器不做任何 DDL，建表 / 索引由 SchemaUpgrader 幂等补齐）。</para>
/// </summary>
[ApiController]
[Route("api/sales-order-change-requests")]
[Authorize]
public class SalesOrderChangeRequestController : ControllerBase
{
    private readonly IErpDbContext _db;
    private readonly IDocumentNumberService _noService;

    public SalesOrderChangeRequestController(IErpDbContext db, IDocumentNumberService noService)
    {
        _db = db;
        _noService = noService;
    }

    /// <summary>
    /// 变更申请台账（分页，只读）：可按来源销售订单 Id / 状态 / 关键字过滤；
    /// 每条都包含来源快照标记、来源可用性、来源是否已变化提示与主表 / 明细对照。
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetPaged([FromQuery] SalesOrderChangeRequestQuery query)
        => Ok(ApiResponse<PagedResult<SalesOrderChangeRequestDto>>.Success(
            await SalesOrderChangeRequestService.ListAsync(_db, query)));

    /// <summary>
    /// 模块元数据（只读）：状态 / 币种白名单、分页与上限、快照 / 金额 / 提交口径与审批边界声明，
    /// 供界面与接口同源显示（避免前端硬编码与后端校验口径漂移）。
    /// </summary>
    [HttpGet("metadata")]
    public IActionResult Metadata()
        => Ok(ApiResponse<SalesOrderChangeRequestMetadataDto>.Success(
            SalesOrderChangeRequestService.GetMetadata()));

    /// <summary>
    /// 来源销售订单候选（只读、**有界**）：用于选择发起申请的对象；只返回存在且未删除的订单，
    /// 已作废订单标注为不可选择，绝不按相似度猜测来源。
    /// </summary>
    [HttpGet("source-options")]
    public async Task<IActionResult> SourceOptions(
        [FromQuery] string? keyword,
        [FromQuery] int take = SalesOrderChangeRequestService.MaxSourceOptions)
        => Ok(ApiResponse<List<SalesOrderChangeRequestSourceOptionDto>>.Success(
            await SalesOrderChangeRequestService.ListSourceOrderOptionsAsync(_db, keyword, take)));

    /// <summary>
    /// 指定来源销售订单的变更申请清单（**有界**，单据行操作入口用；默认含全部状态）。
    /// </summary>
    [HttpGet("by-source")]
    public async Task<IActionResult> GetForSource(
        [FromQuery] long salesOrderId,
        [FromQuery] int take = SalesOrderChangeRequestService.MaxPerSourceOrder)
        => Ok(ApiResponse<List<SalesOrderChangeRequestDto>>.Success(
            await SalesOrderChangeRequestService.ListForSalesOrderAsync(_db, salesOrderId, take)));

    /// <summary>变更申请详情（含来源快照、来源可用性与来源变化提示；只读）</summary>
    [HttpGet("{id:long}")]
    public async Task<IActionResult> GetById(long id)
        => Ok(ApiResponse<SalesOrderChangeRequestDto>.Success(
            await SalesOrderChangeRequestService.GetAsync(_db, id)));

    /// <summary>
    /// 登记变更申请草稿：校验来源销售订单（存在、未删除、未作废）与有界拟议值，
    /// 冻结来源快照并由服务端按销售订单唯一权威算法重算拟议金额。
    /// <para>本接口<strong>不</strong>改写来源销售订单，也<strong>不</strong>包含任何审批 / 套用语义。</para>
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] SalesOrderChangeRequestSaveDto dto)
        => Ok(ApiResponse<SalesOrderChangeRequestDto>.Success(
            await SalesOrderChangeRequestService.CreateDraftAsync(_db, _noService, dto),
            "变更申请草稿已登记（仅登记拟议，未批准、未套用、未改写来源订单）"));

    /// <summary>
    /// 编辑草稿的拟议值：仅草稿可编辑，来源快照列永不被覆盖，金额仍由服务端按同一权威算法重算。
    /// </summary>
    [HttpPut("{id:long}")]
    public async Task<IActionResult> Update(long id, [FromBody] SalesOrderChangeRequestSaveDto dto)
        => Ok(ApiResponse<SalesOrderChangeRequestDto>.Success(
            await SalesOrderChangeRequestService.UpdateDraftAsync(_db, id, dto),
            "变更申请草稿已更新（来源快照与来源订单均未改写）"));

    /// <summary>
    /// 提交变更申请（仅草稿可提交）：冻结拟议快照并记录提交时间；提交后不可编辑。
    /// <para>提交<strong>不</strong>代表批准或套用，来源销售订单与下游记录一律不变。</para>
    /// </summary>
    [HttpPost("{id:long}/submit")]
    public async Task<IActionResult> Submit(long id)
        => Ok(ApiResponse<SalesOrderChangeRequestDto>.Success(
            await SalesOrderChangeRequestService.SubmitAsync(_db, id),
            "变更申请已提交（拟议快照已冻结；系统未批准、未套用任何变更）"));

    /// <summary>
    /// 取消变更申请（草稿与已提交都可取消，必须填写原因）：保留原始与拟议证据，不做硬删除。
    /// </summary>
    [HttpPost("{id:long}/cancel")]
    public async Task<IActionResult> Cancel(long id, [FromBody] SalesOrderChangeRequestCancelRequest? request)
        => Ok(ApiResponse<SalesOrderChangeRequestDto>.Success(
            await SalesOrderChangeRequestService.CancelAsync(_db, id, request?.Reason),
            "变更申请已取消（原始与拟议证据保留，可读）"));
}
