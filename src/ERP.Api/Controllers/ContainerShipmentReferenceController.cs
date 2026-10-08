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
/// 装柜出运引用登记控制器（ERP-057）：为既有装柜链路记录（订柜信息 / 预装柜单 / 装柜清单）登记
/// 用户录入的出运引用证据（出运方式 / 订舱号 / 提单号 / 港口 / 计划时间 / 承运人 / 货代 / 拖车 / 报关行快照），
/// 并支持带修订留痕的修订与显式作废。
/// <para>审计口径：装柜链路已有唯一的持久化引用关系（<c>ContainerPreLoading.BookingId</c> /
/// <c>ContainerLoadingList.PreLoadingId</c>），订柜信息由 ERP-040 承载本套跟踪值的权威记录；
/// 本控制器<strong>不</strong>新建出运主数据、<strong>不</strong>在装柜三单上加列、也<strong>不</strong>按柜号 /
/// 订单号 / 单证号等自由文本匹配记录。</para>
/// <para>授权（ERP-362）：每一个路由都先重新解析实时身份 / 账号状态 / 既有源模块菜单（<c>booking</c> /
/// <c>pre-loading</c> / <c>loading-list</c>）/ 权威客户数据范围（复用 ERP-097），再读取任何计数、候选或证据字段；
/// 受限账号只能读写本人客户的源记录及其出运引用证据，未映射业务员的受限账号 fail closed，
/// 不新增任何用户授权、也不做匿名 / 管理员回退。登记 / 修订 / 作废在可串行化事务内分别对
/// **源记录行**与**本引用行**加 <c>UPDLOCK, HOLDLOCK</c>，把「源记录有效性 + 有效引用唯一性」
/// 与「修订 vs 作废」串行化到同一把锁与同一事务内。</para>
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

    /// <summary>当前登录用户 Id（缺失或非数字时返回 null，由授权规则 fail closed 拒绝，绝不猜测身份）</summary>
    private long? CurrentUserId()
        => long.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var id) ? id : null;

    /// <summary>
    /// 出运引用台账（分页，只读）：可按源记录类型 / Id、状态、出运方式、登记日期区间与关键字过滤；
    /// 默认包含已作废历史（证据保留可读），源记录与报关行可用性都是只读标注。
    /// <para>ERP-362：先解析实时身份 / 账号状态 / 既有源模块菜单 / 客户数据范围，再把范围下推到数据库。</para>
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetPaged([FromQuery] ContainerShipmentReferenceQuery query)
    {
        var access = await ShipmentReferenceAuthorizationRules.EnsureAuthorizedAsync(_db, CurrentUserId());
        return Ok(ApiResponse<PagedResult<ContainerShipmentReferenceDto>>.Success(
            await ContainerShipmentReferenceService.ListAsync(_db, query, access)));
    }

    /// <summary>
    /// 可登记出运引用的源记录候选（只读、有界）：只列出指定类型下未删除、未取消的既有记录，并标注是否已有有效引用；
    /// 仅用于**显式选择**源记录，不按柜号 / 单号等自由文本猜测；候选同样受既有源模块菜单与客户数据范围约束。
    /// </summary>
    [HttpGet("source-candidates")]
    public async Task<IActionResult> SourceCandidates(
        [FromQuery] string sourceType,
        [FromQuery] string? keyword,
        [FromQuery] int take = ContainerShipmentReferenceRules.MaxSourceCandidates)
    {
        var access = await ShipmentReferenceAuthorizationRules.EnsureAuthorizedAsync(_db, CurrentUserId());
        // 未知源记录类型一律先按非法参数拒绝（不静默忽略），再校验该类型对应的既有菜单授权。
        var normalizedType = ContainerShipmentReferenceRules.NormalizeSourceType(sourceType);
        ShipmentReferenceAuthorizationRules.RequireSourceType(access, normalizedType);
        return Ok(ApiResponse<List<ContainerShipmentReferenceSourceCandidateDto>>.Success(
            await ContainerShipmentReferenceService.ListSourceCandidatesAsync(
                _db, normalizedType, keyword, access, take)));
    }

    /// <summary>
    /// 报关行下拉选项（只读，复用 ERP-040 同一口径）：只返回未删除、已启用、类型为 CustomsBroker 的字典项，
    /// 与写入校验完全一致，因此已停用 / 已删除 / 类型不符的字典项不会出现在下拉中。
    /// </summary>
    [HttpGet("customs-broker-options")]
    public async Task<IActionResult> CustomsBrokerOptions()
    {
        await ShipmentReferenceAuthorizationRules.EnsureAuthorizedAsync(_db, CurrentUserId());
        return Ok(ApiResponse<List<OtherInfoOptionDto>>.Success(
            await ContainerShipmentTrackingService.LoadCustomsBrokerOptionsAsync(_db)));
    }

    /// <summary>出运引用详情（含只读标注与有界修订留痕；只读；受既有源模块菜单与客户数据范围约束）</summary>
    [HttpGet("{id:long}")]
    public async Task<IActionResult> GetById(long id)
    {
        var access = await ShipmentReferenceAuthorizationRules.EnsureAuthorizedAsync(_db, CurrentUserId());
        return Ok(ApiResponse<ContainerShipmentReferenceDetailDto>.Success(
            await ContainerShipmentReferenceService.GetAsync(_db, id, access)));
    }

    /// <summary>出运引用的修订留痕（只读、有界；按修订号倒序，最多 50 条；受既有源模块菜单与客户数据范围约束）</summary>
    [HttpGet("{id:long}/revisions")]
    public async Task<IActionResult> Revisions(
        long id, [FromQuery] int take = ContainerShipmentReferenceRules.MaxRevisionTake)
    {
        var access = await ShipmentReferenceAuthorizationRules.EnsureAuthorizedAsync(_db, CurrentUserId());
        return Ok(ApiResponse<List<ContainerShipmentReferenceRevisionDto>>.Success(
            await ContainerShipmentReferenceService.ListRevisionsAsync(_db, id, access, take)));
    }

    /// <summary>
    /// 登记出运引用证据：先校验既有源模块菜单 / 客户数据范围，源记录必须是显式类型下存在、未删除且未取消的既有记录，
    /// 同一条源记录最多 1 条有效引用；出运方式只接受 LCL / FCL / 未指定，计划开船时间不得晚于计划到港时间，
    /// 报关行必须是可选用字典项；登记<strong>不</strong>推进装柜状态、<strong>不</strong>改柜号、<strong>不</strong>写任何单据号。
    /// <para>并发串行化（ERP-362）：在可串行化事务内先对**源记录行**加 <c>UPDLOCK, HOLDLOCK</c>，
    /// 再执行「源记录有效性 + 有效引用唯一性 + 插入证据行」，因此同一源记录的并发登记只能成功一条；
    /// 后到者在锁内看到已登记的有效引用并被明确拒绝（不会产生第二条有效引用）。</para>
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] ContainerShipmentReferenceSaveDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);
        var access = await ShipmentReferenceAuthorizationRules.EnsureAuthorizedAsync(_db, CurrentUserId());
        var sourceType = ContainerShipmentReferenceRules.NormalizeSourceType(dto.SourceType);
        ShipmentReferenceAuthorizationRules.RequireSourceType(access, sourceType);

        await using var transaction = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        try
        {
            await AcquireSourceRowLockAsync(sourceType, dto.SourceId);
            var created = await ContainerShipmentReferenceService.CreateAsync(_db, dto, access);
            await transaction.CommitAsync();
            return Ok(ApiResponse<ContainerShipmentReferenceDto>.Success(
                created, "出运引用证据已登记（只登记证据：未推进装柜状态、未联系承运人 / 海关 / 货代）"));
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    /// <summary>
    /// 修订出运引用证据（必须填写修订原因）：服务端先把修订前的原值写入只追加的修订留痕，再写回新值并递增修订号；
    /// 不允许改派源记录，已作废引用只读。
    /// <para>并发串行化（ERP-362）：在可串行化事务内先对**本引用行**加 <c>UPDLOCK, HOLDLOCK</c>，
    /// 修订与作废因此互斥；「修订 vs 作废」竞争时后到者在锁内看到已作废状态并 fail closed，
    /// 绝不改写已作废证据；任一步校验失败都随事务回滚（证据与新值、修订留痕都不落库）。</para>
    /// </summary>
    [HttpPut("{id:long}")]
    public async Task<IActionResult> Update(long id, [FromBody] ContainerShipmentReferenceUpdateDto dto)
    {
        var access = await ShipmentReferenceAuthorizationRules.EnsureAuthorizedAsync(_db, CurrentUserId());

        await using var transaction = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        try
        {
            await AcquireReferenceRowLockAsync(id);
            var updated = await ContainerShipmentReferenceService.UpdateAsync(_db, id, dto, access);
            await transaction.CommitAsync();
            return Ok(ApiResponse<ContainerShipmentReferenceDto>.Success(
                updated, "出运引用证据已修订（修订前的原值已写入修订留痕，可追溯）"));
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    /// <summary>
    /// 作废出运引用证据（必须填写原因）：保留原始证据、源记录快照与全部修订留痕，不物理删除；
    /// 重复作废被拒绝；作废后该源记录可重新登记一条新的有效引用。
    /// <para>并发串行化（ERP-362）：与修订共用同一把引用行锁（<c>UPDLOCK, HOLDLOCK</c>）与可串行化事务，
    /// 重复作废 / 「作废 vs 修订」竞争的一方在锁内看到已作废状态并被明确拒绝。</para>
    /// </summary>
    [HttpPost("{id:long}/void")]
    public async Task<IActionResult> Void(long id, [FromBody] ContainerShipmentReferenceVoidRequest? request)
    {
        var access = await ShipmentReferenceAuthorizationRules.EnsureAuthorizedAsync(_db, CurrentUserId());

        await using var transaction = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        try
        {
            await AcquireReferenceRowLockAsync(id);
            var voided = await ContainerShipmentReferenceService.VoidAsync(_db, id, request?.Reason, access);
            await transaction.CommitAsync();
            return Ok(ApiResponse<ContainerShipmentReferenceDto>.Success(
                voided, "出运引用证据已作废（原始值与修订留痕保留可读）"));
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    // ==================== 并发串行化（与调用方同一事务内使用） ====================

    /// <summary>
    /// 对**源记录行**加更新锁（<c>UPDLOCK, HOLDLOCK</c>）：把「同一源记录的并发登记」串行化，
    /// 使源记录有效性判定、有效引用唯一性判定与证据行插入落在同一把锁与同一事务内，
    /// 从而保证并发登记同一源记录只能成功一条（后到者在锁内看到已登记的有效引用并被明确拒绝）。
    /// <para>只对显式 allowlist 的三种源记录表加锁，且只 <c>SELECT Id</c>：不改写源记录任何列。
    /// 非关系型提供程序（内存库）无法执行表提示，跳过即可（事务本身等价无事务）。</para>
    /// </summary>
    private async Task AcquireSourceRowLockAsync(string? sourceType, long sourceId)
    {
        if (!_db.Database.IsRelational()) return;

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
    /// 对**本引用行**加更新锁（<c>UPDLOCK, HOLDLOCK</c>）：把同一引用的并发「修订 / 作废 / 重复作废」
    /// 串行化在同一把锁与同一事务内，后到者在锁内重新加载时看到最新状态（已作废证据不被改写、
    /// 重复作废被明确拒绝）。非关系型提供程序（内存库）无法执行表提示，跳过即可。
    /// </summary>
    private async Task AcquireReferenceRowLockAsync(long id)
    {
        if (!_db.Database.IsRelational()) return;

        await _db.Database.SqlQueryRaw<long>(
            "SELECT Id FROM db_owner.ContainerShipmentReferences WITH (UPDLOCK, HOLDLOCK) WHERE Id = {0}", id)
            .ToListAsync();
    }
}
