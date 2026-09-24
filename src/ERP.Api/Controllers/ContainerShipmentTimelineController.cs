using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ERP.Api.Controllers;

/// <summary>
/// 装柜出运证据时间线 / 跟踪工作台控制器（ERP-059）：把 ERP-057 出运引用证据（计划时间 / 港口 / B/L / S/O 等）
/// 与 ERP-058 里程碑证据（实际开船 / 到港 / 查验 / 放行）以**只读**方式合成为时间线，并提供有界分页的工作台。
/// <para>审计口径：ERP-057 是出运引用证据的唯一权威登记册、ERP-058 是里程碑证据的唯一登记册；
/// 本控制器<strong>不新建任何表、不新增任何列</strong>，只按显式的源记录类型 + Id / 出运引用 Id 读取。</para>
/// <para>边界（控制器层同样遵守）：所有接口都是只读 —— 不写任何表、不改写订柜信息 / 预装柜单 / 装柜清单 /
/// 出运引用 / 里程碑，不推进任何业务单据，不改写订单、库存、单证、发票、费用与分摊、收付款、税务与结算记录，
/// 也不联系承运人、海关、货代或任何外部系统；缺失事件一律显示「无 / 未知」，绝不推断为已开船 / 已到港 /
/// 已清关 / 延误 / 逾期；时间差只作算术证据，不做任何时区换算。</para>
/// </summary>
[ApiController]
[Route("api/container/shipment-timeline")]
[Authorize]
public class ContainerShipmentTimelineController : ControllerBase
{
    private readonly IErpDbContext _db;

    public ContainerShipmentTimelineController(IErpDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// 出运跟踪工作台（分页，只读）：只按显式字段筛选（源记录类型 / Id、柜号、单号、B/L、S/O、起运·中转·目的港、
    /// 计划开船·到港时间、记录事件类型与事件时间、关键字），默认包含已作废引用历史；单页内汇总批量装载，
    /// 不产生逐行数据库访问。
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetPaged([FromQuery] ContainerShipmentTimelineQuery query)
        => Ok(ApiResponse<PagedResult<ContainerShipmentTimelineShipmentDto>>.Success(
            await ContainerShipmentTimelineService.ListAsync(_db, query)));

    /// <summary>
    /// 按**显式源记录**读取出运证据时间线（订柜信息 / 预装柜单 / 装柜清单）：仅按「源记录类型 + 源记录 Id」
    /// 取当前有效出运引用，并把计划值与有效里程碑证据分开标注；没有有效引用时返回「未关联」
    /// （证据显示「未知 / 无」，不按柜号 / S/O / B/L 兜底匹配引用）。
    /// </summary>
    [HttpGet("sources/{sourceType}/{sourceId:long}")]
    public async Task<IActionResult> GetForSource(
        string sourceType, long sourceId,
        [FromQuery] bool includeHistory = true,
        [FromQuery] int historyTake = ContainerShipmentTimelineRules.MaxHistoryEvents)
        => Ok(ApiResponse<ContainerShipmentTimelineDetailDto>.Success(
            await ContainerShipmentTimelineService.GetForSourceAsync(
                _db, sourceType, sourceId, includeHistory, historyTake),
            "已按显式源记录返回出运证据时间线（只读：计划与实际分开标注，缺失事件显示「无 / 未知」）"));

    /// <summary>
    /// 按**显式出运引用**读取出运证据时间线（工作台行入口）：有效时间线只含已登记证据，
    /// 已作废 / 历史异常状态条目只出现在历史视图（有界），时间差仅在两条持久化时间戳可比时给出。
    /// </summary>
    [HttpGet("references/{referenceId:long}")]
    public async Task<IActionResult> GetForReference(
        long referenceId,
        [FromQuery] bool includeHistory = true,
        [FromQuery] int historyTake = ContainerShipmentTimelineRules.MaxHistoryEvents)
        => Ok(ApiResponse<ContainerShipmentTimelineDetailDto>.Success(
            await ContainerShipmentTimelineService.GetForReferenceAsync(
                _db, referenceId, includeHistory, historyTake),
            "已按显式出运引用返回出运证据时间线（只读：已作废证据只出现在历史视图，不改派任何记录）"));
}
