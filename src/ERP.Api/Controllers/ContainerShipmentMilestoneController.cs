using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Data;
using System.Security.Claims;

namespace ERP.Api.Controllers;

/// <summary>
/// 装柜出运里程碑证据控制器（ERP-058）：在 ERP-057 出运引用记录**之下**登记用户录入的操作性
/// 里程碑证据（实际开船 / 实际到港 / 查验 / 放行），只追加、显式作废，并支持分页台账与详情读取。
/// <para>审计口径：ERP-057 已建立唯一权威的出运引用登记册，本控制器只在其之下追加事件留痕；
/// <strong>不</strong>新建出运 / 跟踪主数据、<strong>不</strong>在装柜三单或出运引用上加列，也
/// <strong>不</strong>按柜号 / S/O / B/L 等自由文本匹配任何记录。</para>
/// <para>边界（控制器层同样遵守）：所有接口只读写 <c>ContainerShipmentMilestones</c> 一张表
/// （读取时只读父出运引用与其源记录资格快照），<strong>不</strong>改写父出运引用与订柜信息 / 预装柜单 /
/// 装柜清单的状态与工作流，<strong>不</strong>自动推进任何业务单据，<strong>不</strong>改写销售订单、
/// 采购订单、库存与库存成本、库存流水、单证中心、发票、费用与分摊、收付款、税务与结算记录，
/// 也不轮询承运人、海关、货代或任何外部系统（查验 / 放行只是仓库操作性证据，不是海关决定，
/// 也不代表允许出运）；生产库结构变更仍由 Human Gate 控制（本控制器不做任何 DDL，
/// 建表 / 索引由 SchemaUpgrader 幂等补齐）。</para>
/// <para>授权与串行化（ERP-365，复用 ERP-362 口径）：每一个路由都先重新解析实时身份 / 账号状态 /
/// 既有源模块菜单（<c>booking</c> / <c>pre-loading</c> / <c>loading-list</c>，按父出运引用
/// <strong>持久化</strong>的源记录类型判定）/ 权威客户数据范围（复用 ERP-097），再读取任何计数、
/// 候选或证据字段；<strong>不</strong>新增任何用户授权、也<strong>不</strong>做匿名 / 管理员回退。
/// 登记在可序列化事务内先对**父引用行**（与 ERP-362 同一把锁）再对**源记录行**加
/// <c>UPDLOCK, HOLDLOCK</c>，把父引用 / 源记录资格与「同父 + 同类型 + 同时间」唯一性串行化到同一把锁；
/// 作废在可序列化事务内对本里程碑行加锁，重复作废与「作废 vs 登记后作废」竞争都被明确拒绝。</para>
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

    /// <summary>当前登录用户 Id（缺失或非数字时返回 null，由授权规则 fail closed 拒绝，绝不猜测身份）</summary>
    private long? CurrentUserId()
        => long.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var id) ? id : null;

    /// <summary>
    /// 里程碑台账（分页，只读）：可按父出运引用、事件类型、状态、事件时间区间与关键字过滤；
    /// 默认包含已作废历史（证据保留可读），父记录可用性为只读标注。
    /// <para>ERP-365：先解析实时身份 / 账号状态 / 既有源模块菜单 / 客户数据范围，再把范围下推到数据库。</para>
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetPaged([FromQuery] ContainerShipmentMilestoneQuery query)
    {
        var access = await ShipmentReferenceAuthorizationRules.EnsureAuthorizedAsync(_db, CurrentUserId());
        return Ok(ApiResponse<PagedResult<ContainerShipmentMilestoneDto>>.Success(
            await ContainerShipmentMilestoneService.ListAsync(_db, query, access)));
    }

    /// <summary>
    /// 可挂里程碑证据的出运引用候选（只读、有界）：只列出未删除、未作废的 ERP-057 出运引用，
    /// 并标注已有里程碑条数（含已作废历史）；仅用于**显式选择**父记录，不按柜号 / S/O / B/L 猜测；
    /// 候选同样受既有源模块菜单与客户数据范围约束。
    /// </summary>
    [HttpGet("parent-candidates")]
    public async Task<IActionResult> ParentCandidates(
        [FromQuery] string? keyword,
        [FromQuery] int take = ContainerShipmentMilestoneRules.MaxParentCandidates)
    {
        var access = await ShipmentReferenceAuthorizationRules.EnsureAuthorizedAsync(_db, CurrentUserId());
        return Ok(ApiResponse<List<ContainerShipmentMilestoneParentCandidateDto>>.Success(
            await ContainerShipmentMilestoneService.ListParentCandidatesAsync(_db, keyword, access, take)));
    }

    /// <summary>
    /// 里程碑证据详情（含父记录可用性标注与证据性质文案；只读）；
    /// ERP-365：按父出运引用持久化的源记录类型 + Id 授权后才返回证据。
    /// </summary>
    [HttpGet("{id:long}")]
    public async Task<IActionResult> GetById(long id)
    {
        var access = await ShipmentReferenceAuthorizationRules.EnsureAuthorizedAsync(_db, CurrentUserId());
        return Ok(ApiResponse<ContainerShipmentMilestoneDetailDto>.Success(
            await ContainerShipmentMilestoneService.GetAsync(_db, id, access)));
    }

    /// <summary>
    /// 登记里程碑证据：父记录必须是存在、未删除且未作废的 ERP-057 出运引用，且该引用的源记录仍存在、
    /// 未删除、未取消；事件类型只接受实际开船 / 实际到港 / 查验 / 放行，事件时间必填且有界；
    /// 同一父记录 + 类型 + 时间重复登记被拒绝；登记<strong>不</strong>推进任何业务状态、<strong>不</strong>改写任何既有记录。
    /// <para>并发串行化（ERP-365）：在可序列化事务内先对**父引用行**（与 ERP-362 同一把锁）再对
    /// **源记录行**加 <c>UPDLOCK, HOLDLOCK</c>，使父引用 / 源记录资格与有效唯一性判定、证据行插入
    /// 全部落在同一把锁与同一事务内：并发登记同一父记录 + 类型 + 时间只能成功一条。</para>
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] ContainerShipmentMilestoneSaveDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);
        var access = await ShipmentReferenceAuthorizationRules.EnsureAuthorizedAsync(_db, CurrentUserId());

        await using var transaction = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        try
        {
            await AcquireParentRowLockAsync(dto.ContainerShipmentReferenceId);
            await AcquireParentSourceRowLockAsync(dto.ContainerShipmentReferenceId);
            var created = await ContainerShipmentMilestoneService.CreateAsync(_db, dto, access);
            await transaction.CommitAsync();
            return Ok(ApiResponse<ContainerShipmentMilestoneDto>.Success(
                created,
                "里程碑证据已登记（只登记仓库操作性证据：未推进装柜 / 报关 / 出运状态，未联系承运人 / 海关 / 货代）"));
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    /// <summary>
    /// 作废里程碑证据（必须填写原因）：保留原始事件类型、事件时间、来源说明与作废原因，
    /// 不物理删除；重复作废被拒绝；作废后同一父记录 + 类型 + 时间可重新登记一条新的有效证据。
    /// <para>并发串行化（ERP-365）：在可序列化事务内先对**本里程碑行**加 <c>UPDLOCK, HOLDLOCK</c>，
    /// 重复作废的一方在锁内看到已作废状态并被明确拒绝，绝不改写原始证据。</para>
    /// </summary>
    [HttpPost("{id:long}/void")]
    public async Task<IActionResult> Void(long id, [FromBody] ContainerShipmentMilestoneVoidRequest? request)
    {
        var access = await ShipmentReferenceAuthorizationRules.EnsureAuthorizedAsync(_db, CurrentUserId());

        await using var transaction = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        try
        {
            await AcquireMilestoneRowLockAsync(id);
            var voided = await ContainerShipmentMilestoneService.VoidAsync(_db, id, request?.Reason, access);
            await transaction.CommitAsync();
            return Ok(ApiResponse<ContainerShipmentMilestoneDto>.Success(
                voided, "里程碑证据已作废（原始事件类型 / 时间 / 来源与作废原因保留可读）"));
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    // ==================== 并发串行化（与调用方同一事务内使用） ====================

    /// <summary>
    /// 对**父出运引用行**加更新锁（<c>UPDLOCK, HOLDLOCK</c>）：与 ERP-362 的修订 / 作废使用同一把
    /// 引用行锁，因此「父引用作废 vs 里程碑登记」被串行化 —— 后到者要么在锁内看到父引用已作废并被
    /// 明确拒绝，要么在父引用仍有效时完成登记（父引用随后作废，历史里程碑照常可读并显式标注）。
    /// <para>只 <c>SELECT Id</c>：不改写父出运引用任何列。非关系型提供程序（内存库）无法执行表提示，
    /// 跳过即可（事务本身在这些测试里等价「无事务」）。</para>
    /// </summary>
    private async Task AcquireParentRowLockAsync(long referenceId)
    {
        if (!_db.Database.IsRelational() || referenceId <= 0) return;

        await _db.Database.SqlQueryRaw<long>(
            "SELECT Id FROM db_owner.ContainerShipmentReferences WITH (UPDLOCK, HOLDLOCK) WHERE Id = {0}",
            referenceId).ToListAsync();
    }

    /// <summary>
    /// 对父出运引用指向的**源记录行**加更新锁（<c>UPDLOCK, HOLDLOCK</c>）：只按父引用**持久化**的
    /// 源记录类型 + Id 定位（绝不按柜号 / S/O / B/L 等自由文本匹配），只 <c>SELECT Id</c>，不改写源记录。
    /// </summary>
    private async Task AcquireParentSourceRowLockAsync(long referenceId)
    {
        if (!_db.Database.IsRelational() || referenceId <= 0) return;

        var parent = await _db.ContainerShipmentReferences.AsNoTracking()
            .Where(r => r.Id == referenceId)
            .Select(r => new { r.SourceType, r.SourceId })
            .FirstOrDefaultAsync();
        if (parent is null) return;

        await AcquireSourceRowLockAsync(parent.SourceType, parent.SourceId);
    }

    /// <summary>
    /// 对显式 allowlist 的三种源记录表加更新锁（<c>UPDLOCK, HOLDLOCK</c>）：未知类型不加锁
    /// （由授权 / 资格校验明确拒绝），只 <c>SELECT Id</c>，不改写源记录任何列。
    /// </summary>
    private async Task AcquireSourceRowLockAsync(string? sourceType, long sourceId)
    {
        if (!_db.Database.IsRelational() || sourceId <= 0) return;

        switch (sourceType)
        {
            case ContainerShipmentReferenceRules.SourceTypeBooking:
                await _db.Database.SqlQueryRaw<long>(
                    "SELECT Id FROM db_owner.ContainerBookings WITH (UPDLOCK, HOLDLOCK) WHERE Id = {0}",
                    sourceId).ToListAsync();
                return;

            case ContainerShipmentReferenceRules.SourceTypePreLoading:
                await _db.Database.SqlQueryRaw<long>(
                    "SELECT Id FROM db_owner.ContainerPreLoadings WITH (UPDLOCK, HOLDLOCK) WHERE Id = {0}",
                    sourceId).ToListAsync();
                return;

            case ContainerShipmentReferenceRules.SourceTypeLoadingList:
                await _db.Database.SqlQueryRaw<long>(
                    "SELECT Id FROM db_owner.ContainerLoadingLists WITH (UPDLOCK, HOLDLOCK) WHERE Id = {0}",
                    sourceId).ToListAsync();
                return;

            default:
                return;
        }
    }

    /// <summary>
    /// 对**本里程碑证据行**加更新锁（<c>UPDLOCK, HOLDLOCK</c>）：把同一里程碑的并发「作废 / 重复作废」
    /// 串行化在同一把锁与同一事务内，后到者在锁内重新加载时看到最新状态（重复作废被明确拒绝，
    /// 原始事件 / 时间 / 来源 / 作废原因绝不被改写）。非关系型提供程序（内存库）跳过即可。
    /// </summary>
    private async Task AcquireMilestoneRowLockAsync(long id)
    {
        if (!_db.Database.IsRelational() || id <= 0) return;

        await _db.Database.SqlQueryRaw<long>(
            "SELECT Id FROM db_owner.ContainerShipmentMilestones WITH (UPDLOCK, HOLDLOCK) WHERE Id = {0}",
            id).ToListAsync();
    }
}
