using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace ERP.Api.Controllers;

/// <summary>
/// 装柜出运证据时间线 / 跟踪工作台控制器（ERP-059）：把 ERP-057 出运引用证据（计划时间 / 港口 / B/L / S/O 等）
/// 与 ERP-058 里程碑证据（实际开船 / 到港 / 查验 / 放行）以**只读**方式合成为时间线，并提供有界分页的工作台。
/// <para>审计口径：ERP-057 是出运引用证据的唯一权威登记册、ERP-058 是里程碑证据的唯一登记册；
/// 本控制器<strong>不新建任何表、不新增任何列</strong>，只按显式的源记录类型 + Id / 出运引用 Id 读取。</para>
/// <para>授权（ERP-435）：每一个路由都先重新解析实时身份 / 账号状态 / 既有源模块菜单（<c>booking</c> /
/// <c>pre-loading</c> / <c>loading-list</c>，按引用**持久化**的源记录类型判定）/ 权威客户数据范围（复用 ERP-097），
/// 再把范围下推到时间线查询 —— 在读取任何时间线、出运、引用、里程碑、港口、承运人、货代、报关行与计划 / 实际
/// 时间字段之前先授权；<strong>不</strong>新增任何用户授权、也<strong>不</strong>做匿名 / 管理员回退
/// （缺失身份按未认证、禁用 / 已删除账号与越范围一律 fail closed，不返回任何行 / 计数 / 汇总）。</para>
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

    /// <summary>当前登录用户 Id（缺失或非数字时返回 null，由授权规则 fail closed 拒绝，绝不猜测身份）</summary>
    private long? CurrentUserId()
        => long.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var id) ? id : null;

    /// <summary>
    /// 出运跟踪工作台（分页，只读）：只按显式字段筛选（源记录类型 / Id、柜号、单号、B/L、S/O、起运·中转·目的港、
    /// 计划开船·到港时间、记录事件类型与事件时间、关键字），默认包含已作废引用历史；单页内汇总批量装载，
    /// 不产生逐行数据库访问。
    /// <para>ERP-435：先解析实时身份 / 账号状态 / 既有源模块菜单 / 客户数据范围，再把范围下推到数据库。</para>
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetPaged([FromQuery] ContainerShipmentTimelineQuery query)
    {
        var access = await ShipmentReferenceAuthorizationRules.EnsureAuthorizedAsync(_db, CurrentUserId());
        return Ok(ApiResponse<PagedResult<ContainerShipmentTimelineShipmentDto>>.Success(
            await ContainerShipmentTimelineService.ListAsync(_db, query, access)));
    }

    /// <summary>
    /// 按**显式源记录**读取出运证据时间线（订柜信息 / 预装柜单 / 装柜清单）：仅按「源记录类型 + 源记录 Id」
    /// 取当前有效出运引用，并把计划值与有效里程碑证据分开标注；没有有效引用时返回「未关联」
    /// （证据显示「未知 / 无」，不按柜号 / S/O / B/L 兜底匹配引用）。
    /// <para>ERP-435：未知源记录类型一律先按非法参数拒绝（不静默忽略），再校验该类型对应的既有菜单授权与客户数据范围。</para>
    /// </summary>
    [HttpGet("sources/{sourceType}/{sourceId:long}")]
    public async Task<IActionResult> GetForSource(
        string sourceType, long sourceId,
        [FromQuery] bool includeHistory = true,
        [FromQuery] int historyTake = ContainerShipmentTimelineRules.MaxHistoryEvents)
    {
        var access = await ShipmentReferenceAuthorizationRules.EnsureAuthorizedAsync(_db, CurrentUserId());
        var normalizedType = ContainerShipmentReferenceRules.NormalizeSourceType(sourceType);
        ShipmentReferenceAuthorizationRules.RequireSourceType(access, normalizedType);
        return Ok(ApiResponse<ContainerShipmentTimelineDetailDto>.Success(
            await ContainerShipmentTimelineService.GetForSourceAsync(
                _db, normalizedType, sourceId, includeHistory, historyTake, access),
            "已按显式源记录返回出运证据时间线（只读：计划与实际分开标注，缺失事件显示「无 / 未知」）"));
    }

    /// <summary>
    /// 按**显式出运引用**读取出运证据时间线（工作台行入口）：有效时间线只含已登记证据，
    /// 已作废 / 历史异常状态条目只出现在历史视图（有界），时间差仅在两条持久化时间戳可比时给出。
    /// <para>ERP-435：按父出运引用持久化的源记录类型 + Id 授权后才返回证据，受限账号越范围 fail closed。</para>
    /// </summary>
    [HttpGet("references/{referenceId:long}")]
    public async Task<IActionResult> GetForReference(
        long referenceId,
        [FromQuery] bool includeHistory = true,
        [FromQuery] int historyTake = ContainerShipmentTimelineRules.MaxHistoryEvents)
    {
        var access = await ShipmentReferenceAuthorizationRules.EnsureAuthorizedAsync(_db, CurrentUserId());
        return Ok(ApiResponse<ContainerShipmentTimelineDetailDto>.Success(
            await ContainerShipmentTimelineService.GetForReferenceAsync(
                _db, referenceId, includeHistory, historyTake, access),
            "已按显式出运引用返回出运证据时间线（只读：已作废证据只出现在历史视图，不改派任何记录）"));
    }
}
