using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Common;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using System.Linq.Expressions;
using System.Security.Claims;

namespace ERP.Api.Controllers;

/// <summary>
/// 供应商报价比价控制器（阶段 2 新增）
/// 维护：同一采购需求向多家供应商询价，一条记录 = 一家供应商对某需求的报价；
///       相同 QuoteNo 归为一批，便于横向比价与标记选中（IsSelected）。
/// ERP-020：选中的比价行可「带入预填 / 直接生成」采购订单（见 <see cref="PurchaseQuoteConversion" />）。
/// ERP-416：列表 / 全部 / 详情 / 新增 / 修改 / 删除 / 批量删除、带入预填 / 单行转单 / 批次计划 / 批次转单
/// 与报价历史 / 价格差异 / 转化漏斗等派生路由，都先经 <see cref="PurchaseQuoteAuthorizationRules"/>
/// 复核实时身份 + 既有「供应商比价」菜单 + ERP-097 客户数据范围（转单另须既有「采购订单」菜单与权威目的地范围），
/// 范围在计数 / 分页 / 分组之前下推数据库，范围外 / 已删除 / 不存在返回同一非披露错误。
/// </summary>
[ApiController]
[Route("api/purchase/quotes")]
[Authorize]
public class PurchaseQuoteController : BaseCrudController<PurchaseQuote>
{
    private readonly IErpDbContext _db;
    private readonly IDocumentNumberService _noService;

    public PurchaseQuoteController(IGenericService<PurchaseQuote> service, IErpDbContext db,
        IDocumentNumberService noService) : base(service)
    {
        _db = db;
        _noService = noService;
    }

    // ==================== ERP-416：实时授权（身份 + 既有菜单 + 客户数据范围） ====================

    /// <summary>当前登录用户 Id（缺失 / 非数字 / 无 HTTP 管线时返回 null，由实时授权 fail closed 拒绝，绝不猜测身份）。</summary>
    private long? CurrentUserId()
    {
        var value = ControllerContext?.HttpContext?.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return long.TryParse(value, out var id) && id > 0 ? id : null;
    }

    /// <summary>
    /// ERP-416：是否必须执行实时授权。真实 HTTP 请求（MVC 绑定，<c>Request.Path</c> 已赋值）一律执行；
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

    /// <summary>比价数据入口授权（实时身份 + 既有「供应商比价」菜单 + ERP-097 客户数据范围）。</summary>
    private async Task<SalespersonDataScope?> EnsureAuthorizedAsync()
        => RequiresLiveAuthorization()
            ? await PurchaseQuoteAuthorizationRules.EnsureAccessAuthorizedAsync(_db, CurrentUserId())
            : null;

    /// <summary>比价 → 采购订单目的地授权（既有「采购订单」菜单 + 权威客户范围），用于带入预填与真实转单。</summary>
    private async Task<SalespersonDataScope?> EnsureDestinationAuthorizedAsync()
        => RequiresLiveAuthorization()
            ? await PurchaseQuoteAuthorizationRules.EnsureDestinationAuthorizedAsync(_db, CurrentUserId())
            : null;

    /// <summary>受限账号的客户数据范围谓词（下推到服务层，先于计数 / 分页 / 关键字）；不受限口径返回 null。</summary>
    private static Expression<Func<PurchaseQuote, bool>>? ScopeFilter(SalespersonDataScope? scope)
    {
        if (scope is null || scope.AllowedCustomerIds is null) return null;
        var allowed = scope.AllowedCustomerIds.ToList();
        return q => q.CustomerId.HasValue && allowed.Contains(q.CustomerId.Value);
    }

    /// <summary>分页查询（ERP-416：先授权，再把客户数据范围下推到服务层，先于计数 / 分页 / 关键字）。</summary>
    [HttpGet]
    public override async Task<IActionResult> GetPaged([FromQuery] PageQuery query)
    {
        var scope = await EnsureAuthorizedAsync();
        var result = await Service.GetPagedAsync(query, ScopeFilter(scope));
        return Ok(ApiResponse<PagedResult<PurchaseQuote>>.Success(result));
    }

    /// <summary>查询全部（ERP-416：先授权，再按客户数据范围收敛；供下拉框等使用）。</summary>
    [HttpGet("all")]
    public override async Task<IActionResult> GetAll()
    {
        var scope = await EnsureAuthorizedAsync();
        var result = await Service.GetAllAsync(ScopeFilter(scope));
        return Ok(ApiResponse<List<PurchaseQuote>>.Success(result));
    }

    /// <summary>详情（ERP-416：先授权并复核持久化归属，范围外 / 已删除 / 不存在返回同一非披露错误）。</summary>
    [HttpGet("{id:long}")]
    public override async Task<IActionResult> GetById(long id)
    {
        var scope = await EnsureAuthorizedAsync();
        await PurchaseQuoteAuthorizationRules.EnsureQuoteAllowedAsync(_db, scope, id);
        var result = await Service.GetByIdAsync(id);
        return Ok(ApiResponse<PurchaseQuote>.Success(result));
    }

    /// <summary>
    /// 新增（ERP-416：先授权并复核拟提交归属客户，绝不采信 CustomerName，授权先于写入）。
    /// ERP-417：校验对比草稿（正数量 / 非负价格 / EF 精度 / 受支持币种 / 选中与状态一致 / 服务端重算总额），
    /// 明确拒绝客户端伪造的「已转采购订单」状态、采购单号式 <c>RefOrderNo</c> 与主键 / 审计 / 软删除 / 并发令牌字段。
    /// </summary>
    [HttpPost]
    public override async Task<IActionResult> Create([FromBody] PurchaseQuote entity)
    {
        if (entity is null) throw BusinessException.InvalidParameter("请求内容不能为空");

        var scope = await EnsureAuthorizedAsync();
        PurchaseQuoteAuthorizationRules.EnsureProposedCustomerAllowed(scope, entity.CustomerId);

        await using var transaction = await PurchaseQuoteMutationRules.BeginMutationTransactionAsync(_db);
        try
        {
            await PurchaseQuoteMutationRules.EnsureNoForgedConversionEvidenceAsync(_db, entity);
            PurchaseQuoteMutationRules.ValidateDraft(entity);
            PurchaseQuoteMutationRules.NormalizeForCreate(entity, CurrentUserId());

            var result = await Service.CreateAsync(entity);
            if (transaction is not null) await transaction.CommitAsync();
            return Ok(ApiResponse<PurchaseQuote>.Success(result, "新增成功"));
        }
        catch
        {
            await RollbackAsync(transaction, _db);
            throw;
        }
    }

    /// <summary>
    /// 更新（ERP-416：先复核**已存**比价行归属与**拟提交**归属客户）。
    /// ERP-417：在「比价行锁 + 原子事务 + 锁内权威重读」内改写字段——锁内重新读取实时身份 / 既有菜单 / 客户范围；
    /// 已转采购订单的行一律冻结；已存在审批决定的行只允许安全非商业备注修改，其余商业条款 / 改派 / 批次 /
    /// 选中状态变更一律拒绝；待比较草稿按服务端口径校验并重算总额。被拒整体回滚（零部分写入）。
    /// </summary>
    [HttpPut("{id:long}")]
    public override async Task<IActionResult> Update(long id, [FromBody] PurchaseQuote entity)
    {
        if (entity is null) throw BusinessException.InvalidParameter("请求内容不能为空");

        var scope = await EnsureAuthorizedAsync();
        await PurchaseQuoteAuthorizationRules.EnsureQuoteAllowedAsync(_db, scope, id);
        PurchaseQuoteAuthorizationRules.EnsureProposedCustomerAllowed(scope, entity.CustomerId);

        entity.Id = id;
        await using var transaction = await PurchaseQuoteMutationRules.BeginMutationTransactionAsync(_db);
        try
        {
            // ERP-417：锁内重新读取实时身份 / 既有菜单 / 权威客户范围。
            var liveScope = await ReauthorizeAsync();
            if (!await PurchaseQuoteMutationRules.LockQuoteRowAsync(_db, id))
                throw BusinessException.NotFound(PurchaseQuoteAuthorizationRules.NotFoundText);
            var stored = await _db.PurchaseQuotes.FirstOrDefaultAsync(q => q.Id == id && !q.IsDeleted)
                ?? throw BusinessException.NotFound(PurchaseQuoteAuthorizationRules.NotFoundText);
            await PurchaseQuoteAuthorizationRules.EnsureQuoteAllowedAsync(_db, liveScope, id);
            PurchaseQuoteAuthorizationRules.EnsureProposedCustomerAllowed(liveScope, entity.CustomerId);

            if (string.Equals((stored.Status ?? string.Empty).Trim(), PurchaseQuoteMutationRules.ConvertedStatus,
                    StringComparison.Ordinal))
                throw BusinessException.RuleConflict(PurchaseQuoteMutationRules.ConvertedImmutableText);

            if (await PurchaseQuoteApproval.FindCurrentDecisionAsync(_db, stored) is not null)
            {
                // 已决定：只允许安全非商业备注修改，绝不覆盖审批依据或血缘。
                if (!PurchaseQuoteMutationRules.IsRemarkOnlyChange(stored, entity))
                    throw BusinessException.RuleConflict(PurchaseQuoteMutationRules.DecidedImmutableText);
                PurchaseQuoteMutationRules.ApplyRemarkOnly(stored, entity);
            }
            else
            {
                await PurchaseQuoteMutationRules.EnsureNoForgedConversionEvidenceAsync(_db, entity);
                PurchaseQuoteMutationRules.ValidateDraft(entity);
                PurchaseQuoteMutationRules.ApplyEditableFields(stored, entity);
            }

            stored.UpdatedAt = DateTime.Now;
            stored.UpdatedBy = CurrentUserId();
            await _db.SaveChangesAsync();
            if (transaction is not null) await transaction.CommitAsync();
            return Ok(ApiResponse<PurchaseQuote>.Success(stored, "更新成功"));
        }
        catch (DbUpdateConcurrencyException)
        {
            await RollbackAsync(transaction, _db);
            throw BusinessException.RuleConflict(PurchaseQuoteMutationRules.StaleRowVersionText);
        }
        catch
        {
            await RollbackAsync(transaction, _db);
            throw;
        }
    }

    /// <summary>
    /// 删除（软删除；ERP-416：先授权并复核持久化归属，被拒不删除任何行）。
    /// ERP-417 / ERP-419：在比价行锁内确认「未转换 + 无有效审批决定」后才软删除；已转换、或已存在任何有效审批决定
    /// （已批准 / 已拒绝，<see cref="PurchaseQuoteMutationRules.IsDecisionFreezingDeletion"/>）的行拒绝删除——
    /// <b>与操作人归属无关</b>：历史 / 种子 / 导入决定缺失 <c>CreatedBy</c> 只是「未知归属」，绝不构成删除许可。
    /// 保留原始审批决定与来源历史，绝不硬删除、绝不回溯臆造操作人；真正未决定的草稿仍可软删除，失败整体回滚。
    /// </summary>
    [HttpDelete("{id:long}")]
    public override async Task<IActionResult> Delete(long id)
    {
        var scope = await EnsureAuthorizedAsync();
        await PurchaseQuoteAuthorizationRules.EnsureQuoteAllowedAsync(_db, scope, id);

        await using var transaction = await PurchaseQuoteMutationRules.BeginMutationTransactionAsync(_db);
        try
        {
            var liveScope = await ReauthorizeAsync();
            if (!await PurchaseQuoteMutationRules.LockQuoteRowAsync(_db, id))
                throw BusinessException.NotFound(PurchaseQuoteAuthorizationRules.NotFoundText);
            var stored = await _db.PurchaseQuotes.FirstOrDefaultAsync(q => q.Id == id && !q.IsDeleted)
                ?? throw BusinessException.NotFound(PurchaseQuoteAuthorizationRules.NotFoundText);
            await PurchaseQuoteAuthorizationRules.EnsureQuoteAllowedAsync(_db, liveScope, id);

            if (string.Equals((stored.Status ?? string.Empty).Trim(), PurchaseQuoteMutationRules.ConvertedStatus,
                    StringComparison.Ordinal))
                throw BusinessException.RuleConflict(PurchaseQuoteMutationRules.ConvertedImmutableText);
            var decision = await PurchaseQuoteApproval.FindCurrentDecisionAsync(_db, stored);
            if (PurchaseQuoteMutationRules.IsDecisionFreezingDeletion(decision))
                throw BusinessException.RuleConflict(PurchaseQuoteMutationRules.DecidedNoDeleteText);

            stored.IsDeleted = true;
            stored.UpdatedAt = DateTime.Now;
            stored.UpdatedBy = CurrentUserId();
            await _db.SaveChangesAsync();
            if (transaction is not null) await transaction.CommitAsync();
            return Ok(ApiResponse<object>.Success(null, "删除成功"));
        }
        catch (DbUpdateConcurrencyException)
        {
            await RollbackAsync(transaction, _db);
            throw BusinessException.RuleConflict(PurchaseQuoteMutationRules.StaleRowVersionText);
        }
        catch
        {
            await RollbackAsync(transaction, _db);
            throw;
        }
    }

    /// <summary>
    /// 批量删除（软删除；ERP-416：混入任何范围外 / 不存在 Id 即整批拒绝，绝无部分写入）。
    /// ERP-417 / ERP-419：全部 Id 按升序确定性加锁，锁内校验所有行「未转换且无有效审批决定」后**全有或全无**提交；
    /// 任一行的有效决定（含缺失 / 零操作人归属的历史决定）即整批拒绝并回滚（绝不部分删除，也绝不因操作人元数据缺失而放行）。
    /// </summary>
    [HttpPost("batch-delete")]
    public override async Task<IActionResult> BatchDelete([FromBody] List<long> ids)
    {
        var scope = await EnsureAuthorizedAsync();
        var list = PurchaseQuoteMutationRules.MergeLockIds(ids);
        await PurchaseQuoteAuthorizationRules.EnsureQuotesAllowedAsync(_db, scope, list);
        if (list.Count == 0) return Ok(ApiResponse<object>.Success(null, "批量删除成功"));

        await using var transaction = await PurchaseQuoteMutationRules.BeginMutationTransactionAsync(_db);
        try
        {
            var liveScope = await ReauthorizeAsync();
            await PurchaseQuoteMutationRules.LockQuoteRowsAsync(_db, list); // Id 升序确定性加锁

            var rows = await _db.PurchaseQuotes
                .Where(q => list.Contains(q.Id) && !q.IsDeleted)
                .ToListAsync();
            if (rows.Count != list.Count)
                throw BusinessException.NotFound(PurchaseQuoteAuthorizationRules.NotFoundText);
            await PurchaseQuoteAuthorizationRules.EnsureQuotesAllowedAsync(_db, liveScope, list);

            if (rows.Any(r => string.Equals((r.Status ?? string.Empty).Trim(),
                    PurchaseQuoteMutationRules.ConvertedStatus, StringComparison.Ordinal)))
                throw BusinessException.RuleConflict(PurchaseQuoteMutationRules.ConvertedImmutableText);
            var decisions = await _db.PurchaseQuoteDecisions.AsNoTracking()
                .Where(d => list.Contains(d.QuoteId) && !d.IsDeleted)
                .ToListAsync();
            if (decisions.Any(PurchaseQuoteMutationRules.IsDecisionFreezingDeletion))
                throw BusinessException.RuleConflict(PurchaseQuoteMutationRules.DecidedNoDeleteText);

            var now = DateTime.Now;
            var actor = CurrentUserId();
            foreach (var row in rows)
            {
                row.IsDeleted = true;
                row.UpdatedAt = now;
                row.UpdatedBy = actor;
            }

            await _db.SaveChangesAsync();
            if (transaction is not null) await transaction.CommitAsync();
            return Ok(ApiResponse<object>.Success(null, "批量删除成功"));
        }
        catch (DbUpdateConcurrencyException)
        {
            await RollbackAsync(transaction, _db);
            throw BusinessException.RuleConflict(PurchaseQuoteMutationRules.StaleRowVersionText);
        }
        catch
        {
            await RollbackAsync(transaction, _db);
            throw;
        }
    }

    /// <summary>锁内重新读取实时身份 / 既有「供应商比价」菜单 / 权威客户范围（无 HTTP 管线时返回 null，保留内部口径）。</summary>
    private async Task<SalespersonDataScope?> ReauthorizeAsync()
        => RequiresLiveAuthorization()
            ? await PurchaseQuoteAuthorizationRules.EnsureAccessAuthorizedAsync(_db, CurrentUserId())
            : null;

    /// <summary>失败 / 拒绝后整体回滚并丢弃半成品变更（绝不残留部分写入）。</summary>
    private static async Task RollbackAsync(IDbContextTransaction? transaction, IErpDbContext db)
    {
        if (transaction is not null) await transaction.RollbackAsync();
        PurchaseQuoteMutationRules.DiscardTrackedChanges(db);
    }

    /// <summary>
    /// 带入预填采购订单（ERP-020）：按选中的比价行返回一张**未落库**的采购订单草稿，
    /// 前端据此打开「采购订单 → 新增」表单继续编辑后再保存
    /// （保存走 <c>POST /api/purchase-orders</c>，服务端复核数量 / 单价 / 合计）。
    /// 与 <see cref="ToPurchaseOrder" /> 共用同一套守卫（见 <see cref="PurchaseQuoteConversion.BuildDraftAsync" />）：
    /// 只允许「已选中且未放弃、未生成过采购订单」的比价行，本接口不占用单据号、不写库。
    /// </summary>
    [HttpGet("{id:long}/order-prefill")]
    public async Task<IActionResult> OrderPrefill(long id)
    {
        // ERP-416：比价数据授权 + 采购订单目的地授权先于任何读取；来源与目的地归属都必须落在当前客户数据范围内。
        var scope = await EnsureAuthorizedAsync();
        var destination = await EnsureDestinationAuthorizedAsync();

        var quote = await _db.PurchaseQuotes.AsNoTracking()
            .FirstOrDefaultAsync(q => q.Id == id && !q.IsDeleted)
            ?? throw BusinessException.NotFound(PurchaseQuoteAuthorizationRules.NotFoundText);

        await PurchaseQuoteAuthorizationRules.EnsureQuoteAllowedAsync(_db, scope, id);

        var order = await PurchaseQuoteConversion.BuildDraftAsync(_db, quote);
        if (destination is not null)
            await PurchaseQuoteAuthorizationRules.EnsureDestinationScopeAllowedAsync(_db, destination, order);
        return Ok(ApiResponse<PurchaseOrderPrefillResult>.Success(new PurchaseOrderPrefillResult
        {
            SourceType = PurchaseQuoteConversion.PurchaseQuoteSourceType,
            SourceId = quote.Id,
            SourceNo = quote.QuoteNo,
            Order = order
        }, "已按比价行带入采购订单草稿"));
    }

    /// <summary>
    /// 转为采购订单（ERP-020）：按选中的比价行生成一张采购订单（EF 主子表路径，不走存储过程）。
    /// 守卫：同一比价行仅生成一张（以比价行 <c>RefOrderNo</c> 指向的采购单号 +
    /// 采购订单备注来源标记为准，见 <see cref="PurchaseQuoteConversion.FindGeneratedOrderAsync" />），
    /// 只新增单据、绝不覆盖既有订单；生成后比价行状态置「已转采购订单」并把采购单号写回 <c>RefOrderNo</c> 留痕
    /// （不新增 / 不修改任何数据库结构）。
    /// </summary>
    [HttpPost("{id:long}/to-order")]
    public async Task<IActionResult> ToPurchaseOrder(long id)
    {
        // ERP-416：授权（比价 + 目的地菜单）与来源 / 目的地归属复核先于发号与写入。
        var scope = await EnsureAuthorizedAsync();
        var destination = await EnsureDestinationAuthorizedAsync();

        // ERP-418：与批次转单**共用同一协议**——先解析权威归属销售订单并按 Id 升序加锁，
        // 再按比价行 Id 升序加锁（与 ERP-417 修改 / 删除 / 审批决定同一把行锁），锁内权威重读与复核后才发号写入。
        await using var transaction = await PurchaseQuoteConversionMutationRules.BeginConversionTransactionAsync(_db);
        try
        {
            var liveScope = await ReauthorizeAsync();

            var source = await PurchaseQuoteConversionMutationRules.LockAndReloadAsync(_db, new[] { id }, null);
            var quote = source.Lines.Single();

            await PurchaseQuoteAuthorizationRules.EnsureQuoteAllowedAsync(_db, liveScope, id);
            await PurchaseQuoteConversionMutationRules.EnsureConversionReadyAsync(_db, source, new[] { id });

            var order = await PurchaseQuoteConversion.BuildDraftAsync(_db, quote);

            // ERP-417 / ERP-418：批准供应商与当前供应商必须一致（绝不采购批准之外的供应商）。
            await PurchaseQuoteApproval.EnsureApprovedSupplierCoherentAsync(_db, quote);

            // ERP-418：目的地归属规则——归属客户 / 商品 / 单位与权威归属销售订单兼容（来源无明细时不误伤）。
            if (destination is not null)
                await PurchaseQuoteAuthorizationRules.EnsureDestinationScopeAllowedAsync(_db, destination, order);
            if (source.OwningSalesOrderIdByLineId.TryGetValue(id, out var owningId) && owningId is long owning)
                await PurchaseSalesOrderLinkRules.EnsureOwningSourceCompatibleAsync(_db, order, owning);

            order.OrderNo = await _noService.GenerateAsync(DocumentType.PurchaseOrder);
            _db.PurchaseOrders.Add(order);
            PurchaseQuoteConversion.MarkConverted(quote, order.OrderNo);
            await _db.SaveChangesAsync();
            if (transaction is not null) await transaction.CommitAsync();
            return Ok(ApiResponse<PurchaseOrderConversionResult>.Success(new PurchaseOrderConversionResult
            {
                Id = order.Id,
                OrderNo = order.OrderNo,
                SourceNo = quote.QuoteNo
            }, "已生成采购订单"));
        }
        catch (DbUpdateConcurrencyException)
        {
            await RollbackAsync(transaction, _db);
            throw BusinessException.RuleConflict(PurchaseQuoteMutationRules.StaleRowVersionText);
        }
        catch
        {
            await RollbackAsync(transaction, _db);
            throw;
        }
    }

    /// <summary>
    /// 批次转换计划（ERP-027，**只读**）：按比价批次号（或比价行 Id）返回「将合并为哪些采购订单」的分组计划：
    /// 每组的供应商 / 币种 / 归属客户 / 付款条件等表头口径、包含的来源行、服务端重算合计与未落库草稿，
    /// 以及不合格行（未选中 / 已放弃 / 已转 / 未维护供应商 / 数量或单价非法）的跳过原因。
    /// 不写库、不占用单据号、不改来源状态；前端据此展示「将生成几张订单」并可取消。
    /// </summary>
    [HttpGet("batch-order-plan")]
    public async Task<IActionResult> BatchOrderPlan([FromQuery] string? quoteNo, [FromQuery] long? lineId)
    {
        // ERP-416：比价 + 目的地授权，批次整批归属复核（混入范围外 / 空归属行即整批非披露拒绝）。
        var scope = await EnsureAuthorizedAsync();
        var destination = await EnsureDestinationAuthorizedAsync();
        await PurchaseQuoteAuthorizationRules.EnsureBatchAllowedAsync(_db, scope, quoteNo, lineId);

        var build = await PurchaseQuoteConversion.BuildBatchAsync(_db, quoteNo, lineId, null);
        var plan = PurchaseQuoteConversion.BuildPlan(build);
        if (destination is not null)
            foreach (var group in plan.Groups)
                await PurchaseQuoteAuthorizationRules.EnsureDestinationScopeAllowedAsync(_db, destination, group.Order);
        return Ok(ApiResponse<PurchaseQuoteBatchPlan>.Success(plan,
            $"比价批次 {plan.SourceNo}：可转换 {plan.EligibleLineCount} 行、将生成 {plan.GroupCount} 张采购订单"));
    }

    /// <summary>
    /// 批次转采购订单（ERP-027）：把该批次内全部「已选中」且未转换的比价行按兼容分组
    /// （供应商 + 币种 + 归属客户 / 销售订单 + 付款条件 + 是否含税）合并生成采购订单：
    /// 组内多行合并为一张订单的多行明细（金额与总额由服务端按采购订单口径重算），
    /// 每组一张订单、逐行回写来源留痕（`RefOrderNo` + 备注来源标记 + 状态「已转采购订单」）；
    /// 不合格行不静默丢弃，随响应显式返回原因。同一批次重复转换由既有两道重复守卫拦截，不产生重复单据。
    /// </summary>
    [HttpPost("batch-to-order")]
    public async Task<IActionResult> BatchToOrder([FromBody] PurchaseQuoteBatchConversionRequest request)
    {
        // ERP-416：比价 + 目的地授权，批次整批归属复核（混入范围外 / 空归属行即整批拒绝，无部分写入）。
        var scope = await EnsureAuthorizedAsync();
        var destination = await EnsureDestinationAuthorizedAsync();
        var batchNo = await PurchaseQuoteAuthorizationRules.EnsureBatchAllowedAsync(_db, scope,
            request?.QuoteNo, request?.LineId, request?.LineIds);
        if (request is null) throw BusinessException.InvalidParameter("请求内容不能为空");

        // ERP-418：与单行转单共用同一协议——事务内解析不可变批次成员 → 确定性加锁（归属销售订单 → 比价行）
        // → 锁内权威重读与复核（实时权限 / 资格 / 币种 / 主数据 / 目的地归属规则）→ 发号 → 写入 → 提交。
        await using var transaction = await PurchaseQuoteConversionMutationRules.BeginConversionTransactionAsync(_db);
        try
        {
            var liveScope = await ReauthorizeAsync();

            // ① 锁定前解析不可变批次成员（锁定后逐字复核，成员变化 / 被删 / 改批次即受控拒绝）。
            var members = await PurchaseQuoteConversion.ResolveBatchLinesAsync(
                _db, request?.QuoteNo, request?.LineId, request?.LineIds);
            var lineIds = members.Select(l => l.Id).ToList();
            var expectedBatchNo = (batchNo ?? string.Empty).Trim();

            // ② 归属销售订单行锁（Id 升序）→ ③ 来源比价行锁（Id 升序）→ ④ 锁内权威重读 + 成员一致复核。
            var source = await PurchaseQuoteConversionMutationRules.LockAndReloadAsync(_db, lineIds, expectedBatchNo);
            await PurchaseQuoteAuthorizationRules.EnsureBatchAllowedAsync(_db, liveScope, source.BatchNo, null);

            // ⑤ 计划 / 复核（只读）：合格行 = 将写库的行（不合格行按批次既有语义显式跳过，不阻断整批）。
            var precheck = await PurchaseQuoteConversion.BuildBatchFromLinesAsync(_db, source.Lines);
            var convertibleIds = precheck.Groups.SelectMany(g => g.Lines).Select(l => l.Id).ToList();
            await PurchaseQuoteConversionMutationRules.EnsureConversionReadyAsync(_db, source, convertibleIds);
            await PurchaseQuoteApproval.EnsureApprovedSuppliersCoherentAsync(_db, source.Lines);

            // ⑥ 目的地归属复核（权威客户范围 + 归属销售订单客户 / 商品 / 单位兼容）先于发号与落库。
            if (destination is not null)
                await PurchaseQuoteAuthorizationRules.EnsureDestinationScopeAllowedAsync(
                    _db, destination, precheck.Groups.Select(g => g.Draft).ToList());
            foreach (var group in precheck.Groups)
            {
                var owningIds = group.Lines
                    .Select(l => source.OwningSalesOrderIdByLineId.TryGetValue(l.Id, out var owning) ? owning : null)
                    .Where(owning => owning is > 0)
                    .Select(owning => owning!.Value)
                    .Distinct()
                    .ToList();
                foreach (var owningId in owningIds)
                    await PurchaseSalesOrderLinkRules.EnsureOwningSourceCompatibleAsync(_db, group.Draft, owningId);
            }

            // ⑦ 取号 + 写入 + 来源留痕（单次 SaveChanges；任一步失败整体回滚，绝不残留孤儿订单 / 明细）。
            var result = await PurchaseQuoteConversion.ConvertBatchAsync(_db, _noService, source.Lines, source.BatchNo);
            if (transaction is not null) await transaction.CommitAsync();
            var message = result.Skipped.Count > 0
                ? $"已生成 {result.OrderCount} 张采购订单，跳过 {result.Skipped.Count} 行不合格比价行"
                : $"已生成 {result.OrderCount} 张采购订单";
            return Ok(ApiResponse<PurchaseQuoteBatchConversionResult>.Success(result, message));
        }
        catch (DbUpdateConcurrencyException)
        {
            await RollbackAsync(transaction, _db);
            throw BusinessException.RuleConflict(PurchaseQuoteMutationRules.StaleRowVersionText);
        }
        catch
        {
            await RollbackAsync(transaction, _db);
            throw;
        }
    }

    /// <summary>
    /// 报价价格历史（ERP-098，只读派生，通用查询）：按商品 + 可选供应商 / 报价日期区间筛选未删除报价行，
    /// 稳定按报价日期 + 行 Id 排序并分页，按「规格 + 单位 + 币种 + 是否含税」比价口径分组，
    /// 价格差异只在口径内部计算，口径不一致的报价行明确分单列；审批 / 选中仅作证据回显，不写库。
    /// </summary>
    [HttpGet("price-history")]
    public async Task<IActionResult> PriceHistory([FromQuery] PurchaseQuotePriceHistoryQuery query)
    {
        // ERP-416：先授权，再把客户数据范围下推到派生查询（先于计数 / 分页 / 分组）。
        var scope = await EnsureAuthorizedAsync();
        var view = await PurchaseQuotePriceHistory.QueryAsync(_db, query, scope);
        return Ok(ApiResponse<PurchaseQuotePriceHistoryView>.Success(view,
            $"报价历史：{view.TotalCount} 行、{view.GroupCount} 个比价口径"));
    }

    /// <summary>
    /// 从某个比价行打开报价价格历史（ERP-098，只读派生）：以该行商品作为筛选商品、以该行口径作为参照口径，
    /// 与参照口径一致的报价行为「可同比价」组，口径不一致的报价行明确分单列；不写库、不自动选供应商。
    /// </summary>
    [HttpGet("{id:long}/price-history")]
    public async Task<IActionResult> QuotePriceHistory(long id)
    {
        var scope = await EnsureAuthorizedAsync();
        var view = await PurchaseQuotePriceHistory.ForQuoteAsync(_db, id, scope);
        return Ok(ApiResponse<PurchaseQuotePriceHistoryView>.Success(view,
            $"比价行 #{id} 的报价历史：{view.TotalCount} 行、{view.GroupCount} 个比价口径"));
    }

    /// <summary>
    /// 比价 → 采购订单价格差异（ERP-105，只读派生，通用查询）：按报价日期 + 可选供应商筛选
    /// 「已转采购订单」的未删除比价行，稳定按报价日期 + 行 Id 排序并分页，逐行解析采购订单链接；
    /// 仅在「商品 + 规格 + 单位 + 币种 + 是否含税」完全一致时计算单价差与金额差，
    /// 歧义 / 口径不一致 / 陈旧链接 / 未链接显式标注为未解决，不重定价、不改审批。
    /// </summary>
    [HttpGet("order-price-variance")]
    public async Task<IActionResult> OrderPriceVariance([FromQuery] PurchaseQuoteOrderPriceVarianceQuery query)
    {
        // ERP-416：先授权，再把客户数据范围下推到派生查询（先于计数 / 分页 / 订单链接解析）。
        var scope = await EnsureAuthorizedAsync();
        var view = await PurchaseQuoteOrderPriceVariance.QueryAsync(_db, query, scope);
        return Ok(ApiResponse<PurchaseQuoteOrderPriceVarianceView>.Success(view,
            $"比价 → 采购订单价格差异：{view.TotalCount} 行（已核对 {view.ResolvedCount}、未解决 {view.UnresolvedCount}）"));
    }

    /// <summary>
    /// 从某个比价行打开比价 → 采购订单价格差异（ERP-105，只读派生）：只返回该行的来源与采购订单对照；
    /// 未转采购订单时显式标注未解决，不写库、不重定价、不改审批。
    /// </summary>
    [HttpGet("{id:long}/order-price-variance")]
    public async Task<IActionResult> QuoteOrderPriceVariance(long id)
    {
        var scope = await EnsureAuthorizedAsync();
        var view = await PurchaseQuoteOrderPriceVariance.ForQuoteAsync(_db, id, scope);
        return Ok(ApiResponse<PurchaseQuoteOrderPriceVarianceView>.Success(view,
            $"比价行 #{id} 的价格差异：已核对 {view.ResolvedCount}、未解决 {view.UnresolvedCount}"));
    }

    /// <summary>
    /// 供应商报价转采购订单转化漏斗（ERP-103，只读派生）：按比价批次号分组，按报价日期 + 可选供应商筛选未删除报价行，
    /// 稳定按批次最早报价日期 + 批次号排序并分页；逐批次区分 已报价 / 已选中 / 已批准 / 已转采购订单 / 已拒绝 / 未解决 六类证据
    /// （批准 / 拒绝来自审批决定，转采购订单只以「状态 + 采购订单备注来源标记 + 订单链接」三重证据认定，绝不凭 RefOrderNo 文本推断）。
    /// 不写库、不转单、不创建订单、不改报价 / 审批决定。
    /// </summary>
    [HttpGet("conversion-funnel")]
    public async Task<IActionResult> ConversionFunnel([FromQuery] PurchaseQuoteConversionFunnelQuery query)
    {
        // ERP-416：先授权，再把客户数据范围下推到派生查询（先于分组 / 计数 / 分页）。
        var scope = await EnsureAuthorizedAsync();
        var view = await PurchaseQuoteConversionFunnel.QueryAsync(_db, query, scope);
        return Ok(ApiResponse<PurchaseQuoteConversionFunnelView>.Success(view,
            $"供应商报价转单漏斗：{view.TotalBatchCount} 个批次、{view.TotalLineCount} 行"));
    }
}
