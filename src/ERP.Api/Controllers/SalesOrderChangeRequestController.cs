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
/// 销售订单变更申请控制器（ERP-047）：登记「拟议变更」并提供来源 / 拟议对照、提交与取消留痕。
/// <para>边界（控制器层同样遵守）：本模块<strong>不</strong>审核、<strong>不</strong>套用、<strong>不</strong>定义审批阈值、
/// <strong>不</strong>发号生效，也<strong>不</strong>提供硬删除（更正走取消并保留原因）；
/// 所有接口只读写 <c>SalesOrderChangeRequests</c> / <c>SalesOrderChangeRequestDetails</c> 两张表，
/// <strong>不</strong>改写来源销售订单与报价单 / 出库 / 装柜与出运 / 收款与发票 / 佣金 / 库存 / 单证 / 财务记录；
/// 生产库结构变更仍由 Human Gate 控制（本控制器不做任何 DDL，建表 / 索引由 SchemaUpgrader 幂等补齐）。</para>
/// <para>原子性（ERP-415）：登记 / 编辑 / 提交 / 取消都在**可串行化事务 + 确定性行锁**（复用既有规范来源销售订单行锁协议，
/// 锁序「来源销售订单行 → 变更申请行」）内完成，锁内重读实时授权与当前状态；失败整体回滚（零部分写入），
/// 并发输家受控拒绝且绝不覆盖赢家已冻结的拟议快照或原始取消原因 / 时间戳（见
/// <see cref="SalesOrderChangeRequestMutationRules"/> 与 <c>docs/sales-order-change-request-atomicity.md</c>）。</para>
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
    /// 当前登录用户 Id（缺失 / 非数字 / 无 HTTP 管线时返回 null，由实时授权 fail closed 拒绝，绝不猜测身份）。
    /// 显式空安全，保证进程内直调（无 <see cref="ControllerBase.ControllerContext"/>）也返回 null 而不抛出。
    /// </summary>
    private long? CurrentUserId()
    {
        var value = ControllerContext?.HttpContext?.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return long.TryParse(value, out var id) ? id : null;
    }

    /// <summary>
    /// ERP-414：是否必须执行实时授权。真实 HTTP 请求（MVC 绑定，<c>Request.Path</c> 已赋值）一律执行；
    /// 进程内直接调用（历史单元测试 / 内部派生读取，无 HTTP 请求管线）仅在携带当前登录身份时执行。
    /// 只对「既无任何登录身份、又不在 HTTP 请求管线内」的调用免授权：这类调用不可能由外部请求到达，
    /// 也绝不把缺失身份当作管理员（真实匿名请求因处于请求管线内一律 fail closed）。
    /// </summary>
    private bool RequiresLiveAuthorization()
    {
        var http = ControllerContext?.HttpContext;
        if (http is null) return false;
        return http.Request.Path.HasValue || CurrentUserId() is not null;
    }

    /// <summary>
    /// ERP-414 数据 / 状态入口授权（实时身份 + 既有「销售订单」菜单 + ERP-097 权威客户范围）：
    /// 进程内无身份直调返回 <c>null</c>（免授权，保持既有单元测试口径），其余一律 fail closed。
    /// </summary>
    private async Task<SalespersonDataScope?> EnsureAuthorizedAsync()
        => RequiresLiveAuthorization()
            ? await SalesOrderChangeRequestAuthorizationRules.EnsureAccessAuthorizedAsync(_db, CurrentUserId())
            : null;

    /// <summary>
    /// 变更申请台账（分页，只读）：可按来源销售订单 Id / 状态 / 关键字过滤；
    /// 每条都包含来源快照标记、来源可用性、来源是否已变化提示与主表 / 明细对照。
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetPaged([FromQuery] SalesOrderChangeRequestQuery query)
    {
        var scope = await EnsureAuthorizedAsync();
        return Ok(ApiResponse<PagedResult<SalesOrderChangeRequestDto>>.Success(
            await SalesOrderChangeRequestService.ListAsync(_db, query, scope)));
    }

    /// <summary>
    /// 模块元数据（只读）：状态 / 币种白名单、分页与上限、快照 / 金额 / 提交口径与审批边界声明，
    /// 供界面与接口同源显示（避免前端硬编码与后端校验口径漂移）。
    /// </summary>
    /// <summary>
    /// 模块元数据（只读、静态口径文案，不含任何订单 / 客户数据）：状态 / 币种白名单、分页与上限、快照 / 金额 /
    /// 提交口径与审批边界声明，供界面与接口同源显示（避免前端硬编码与后端校验口径漂移）。
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
    {
        var scope = await EnsureAuthorizedAsync();
        return Ok(ApiResponse<List<SalesOrderChangeRequestSourceOptionDto>>.Success(
            await SalesOrderChangeRequestService.ListSourceOrderOptionsAsync(_db, keyword, take, scope)));
    }

    /// <summary>
    /// 指定来源销售订单的变更申请清单（**有界**，单据行操作入口用；默认含全部状态）。
    /// </summary>
    [HttpGet("by-source")]
    public async Task<IActionResult> GetForSource(
        [FromQuery] long salesOrderId,
        [FromQuery] int take = SalesOrderChangeRequestService.MaxPerSourceOrder)
    {
        var scope = await EnsureAuthorizedAsync();
        return Ok(ApiResponse<List<SalesOrderChangeRequestDto>>.Success(
            await SalesOrderChangeRequestService.ListForSalesOrderAsync(_db, salesOrderId, take, scope)));
    }

    /// <summary>变更申请详情（含来源快照、来源可用性与来源变化提示；只读）</summary>
    [HttpGet("{id:long}")]
    public async Task<IActionResult> GetById(long id)
    {
        var scope = await EnsureAuthorizedAsync();
        if (scope is not null)
            await SalesOrderChangeRequestAuthorizationRules.EnsureRequestAllowedAsync(_db, scope, id);
        return Ok(ApiResponse<SalesOrderChangeRequestDto>.Success(
            await SalesOrderChangeRequestService.GetAsync(_db, id, scope)));
    }

    /// <summary>
    /// 登记变更申请草稿：校验来源销售订单（存在、未删除、未作废）与有界拟议值，
    /// 冻结来源快照并由服务端按销售订单唯一权威算法重算拟议金额。
    /// <para>本接口<strong>不</strong>改写来源销售订单，也<strong>不</strong>包含任何审批 / 套用语义。</para>
    /// <para>ERP-415：登记在**可串行化事务 + 确定性「来源销售订单行」锁**（复用既有规范销售订单行锁协议）
    /// 内完成，锁内重新读取实时权限与权威来源主表 + 明细之后才发号与冻结快照；失败整体回滚（零部分写入）。</para>
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] SalesOrderChangeRequestSaveDto dto)
    {
        var scope = await EnsureAuthorizedAsync();
        var result = await RunAtomicAsync(async () =>
        {
            await AcquireSourceOrderLockAsync(dto.SalesOrderId);
            return await SalesOrderChangeRequestService.CreateDraftAsync(
                _db, _noService, dto, scope, CurrentUserId());
        });
        return Ok(ApiResponse<SalesOrderChangeRequestDto>.Success(
            result, "变更申请草稿已登记（仅登记拟议，未批准、未套用、未改写来源订单）"));
    }

    /// <summary>
    /// 编辑草稿的拟议值：仅草稿可编辑，来源快照列永不被覆盖，金额仍由服务端按同一权威算法重算。
    /// <para>ERP-415：编辑在**可串行化事务 + 确定性锁序「来源销售订单行 → 变更申请行」**内完成，
    /// 锁内重读实时授权与当前状态；并发提交 / 取消的输家被状态门拒绝，绝不覆盖赢家冻结的拟议快照。</para>
    /// </summary>
    [HttpPut("{id:long}")]
    public async Task<IActionResult> Update(long id, [FromBody] SalesOrderChangeRequestSaveDto dto)
    {
        var scope = await EnsureAuthorizedAsync();
        var sourceOrderId = await SalesOrderChangeRequestService.ResolveStoredSourceOrderIdAsync(_db, id);
        var result = await RunAtomicAsync(async () =>
        {
            await AcquireLifecycleLocksAsync(sourceOrderId, id);
            return await SalesOrderChangeRequestService.UpdateDraftAsync(
                _db, id, dto, scope, CurrentUserId());
        });
        return Ok(ApiResponse<SalesOrderChangeRequestDto>.Success(
            result, "变更申请草稿已更新（来源快照与来源订单均未改写）"));
    }

    /// <summary>
    /// 提交变更申请（仅草稿可提交）：冻结拟议快照并记录提交时间；提交后不可编辑。
    /// <para>提交<strong>不</strong>代表批准或套用，来源销售订单与下游记录一律不变。</para>
    /// <para>ERP-415：提交在**可串行化事务 + 确定性锁序「来源销售订单行 → 变更申请行」**内完成，
    /// 锁内重读实时授权与当前状态并冻结一个完整正数、经既有权威算法复核的拟议快照。</para>
    /// </summary>
    [HttpPost("{id:long}/submit")]
    public async Task<IActionResult> Submit(long id)
    {
        var scope = await EnsureAuthorizedAsync();
        var sourceOrderId = await SalesOrderChangeRequestService.ResolveStoredSourceOrderIdAsync(_db, id);
        var result = await RunAtomicAsync(async () =>
        {
            await AcquireLifecycleLocksAsync(sourceOrderId, id);
            return await SalesOrderChangeRequestService.SubmitAsync(_db, id, scope, CurrentUserId());
        });
        return Ok(ApiResponse<SalesOrderChangeRequestDto>.Success(
            result, "变更申请已提交（拟议快照已冻结；系统未批准、未套用任何变更）"));
    }

    /// <summary>
    /// 取消变更申请（草稿与已提交都可取消，必须填写原因）：保留原始与拟议证据，不做硬删除。
    /// <para>ERP-415：取消在**可串行化事务 + 确定性锁序「来源销售订单行 → 变更申请行」**内完成，
    /// 锁内重读实时授权与当前状态；并发取消只有一个赢家，输家绝不覆盖赢家已保留的原始取消原因与时间戳。</para>
    /// </summary>
    [HttpPost("{id:long}/cancel")]
    public async Task<IActionResult> Cancel(long id, [FromBody] SalesOrderChangeRequestCancelRequest? request)
    {
        var scope = await EnsureAuthorizedAsync();
        var sourceOrderId = await SalesOrderChangeRequestService.ResolveStoredSourceOrderIdAsync(_db, id);
        var result = await RunAtomicAsync(async () =>
        {
            await AcquireLifecycleLocksAsync(sourceOrderId, id);
            return await SalesOrderChangeRequestService.CancelAsync(
                _db, id, request?.Reason, scope, CurrentUserId());
        });
        return Ok(ApiResponse<SalesOrderChangeRequestDto>.Success(
            result, "变更申请已取消（原始与拟议证据保留，可读）"));
    }

    // ==================== ERP-415：可串行化事务 + 确定性行锁 ====================

    /// <summary>
    /// ERP-415：在<strong>可串行化事务</strong>内执行一次变更申请生命周期写入（登记 / 编辑 / 提交 / 取消）。
    /// 关系型后端开启真实事务，委托内先取确定性行锁再执行业务；任一步失败整体回滚（零部分写入）。
    /// 非关系型提供程序（内存库）无事务 / 行锁语义，等价直接执行（声明式校验与状态判定不变）。
    /// </summary>
    private async Task<T> RunAtomicAsync<T>(Func<Task<T>> action, CancellationToken ct = default)
    {
        if (!_db.Database.IsRelational()) return await action();

        await using var transaction = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        try
        {
            var result = await action();
            await transaction.CommitAsync(ct);
            return result;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    /// <summary>
    /// 对**来源销售订单行**加更新锁（<c>UPDLOCK, HOLDLOCK</c>）：直接复用既有<strong>规范销售订单行锁协议</strong>
    /// （<see cref="SalesOrderChangeRequestMutationRules.SalesOrderRowLockSql"/>，与销售订单取消 / 出库审核 / 预装柜流程 /
    /// 单证生成同一把来源行锁）。加锁<strong>不</strong>改写来源订单任何字段（含 <c>UpdatedAt</c>）。
    /// </summary>
    private async Task AcquireSourceOrderLockAsync(long salesOrderId, CancellationToken ct = default)
    {
        if (!_db.Database.IsRelational() || salesOrderId <= 0) return;
        await _db.Database
            .SqlQueryRaw<long>(SalesOrderChangeRequestMutationRules.SalesOrderRowLockSql, salesOrderId)
            .ToListAsync(ct);
    }

    /// <summary>对**变更申请行**加更新锁（口径与来源销售订单行锁完全一致）。</summary>
    private async Task AcquireChangeRequestLockAsync(long requestId, CancellationToken ct = default)
    {
        if (!_db.Database.IsRelational() || requestId <= 0) return;
        await _db.Database
            .SqlQueryRaw<long>(SalesOrderChangeRequestMutationRules.ChangeRequestRowLockSql, requestId)
            .ToListAsync(ct);
    }

    /// <summary>
    /// 确定性锁序「<strong>来源销售订单行 → 变更申请行</strong>」（编辑 / 提交 / 取消用，跨模块统一、绝不反向获取）：
    /// 先取来源行锁（不存在 / 已删除时不阻断 —— 申请的快照与身份仍然不可变），再取申请行锁；申请行存在性由服务层
    /// 锁内重读判定（不存在 / 已删除一律受控拒绝）。
    /// </summary>
    private async Task AcquireLifecycleLocksAsync(long salesOrderId, long requestId, CancellationToken ct = default)
    {
        await AcquireSourceOrderLockAsync(salesOrderId, ct);
        await AcquireChangeRequestLockAsync(requestId, ct);
    }
}
