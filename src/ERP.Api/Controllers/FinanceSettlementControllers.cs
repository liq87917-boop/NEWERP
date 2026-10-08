using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ERP.Api.Controllers;

/// <summary>
/// 装柜结算单控制器（ERP-384 生命周期护栏）：列表 / 详情 / 创建 / 修改 / 提交 / 审核 / 取消 / 删除与费用分摊证据读取都先重新校验
/// 实时启用身份（账号存在、未删除且启用）、既有「装柜结算单」（<c>container-settlement</c>）菜单授权与当前权威客户数据范围；
/// 创建 / 修改校验客户真实可用（存在 / 未删除 / 启用）与可空来源装柜清单（既有、未删除、未取消，且结算客户为该清单权威客户，
/// 复用 ERP-364 参与方 / 上游客户范围；空来源保留历史「未关联来源」语义，绝不按柜号 / 单号文本猜测）。
/// <para>金额只按既有存储精度（<c>decimal(18,2)</c>，2 位小数）校验：总金额 &gt; 0，海运费 / 其他费用 &gt;= 0；
/// 结算单没有币种字段，因此不做任何汇率换算、跨币种聚合或 <c>total = sum(costs)</c> 公式。</para>
/// <para>修改 / 提交 / 审核 / 取消 / 删除都在同一事务内先取来源装柜清单行锁、再取装柜结算单行锁（ERP-384 锁序：
/// 先上游清单行、后结算单行），再加载权威状态 / 字段并在锁内复核身份 + 菜单授权 + 客户范围 + 状态 + 金额与来源链接；
/// 失败整体回滚（状态、原始字段与审计不变）。存在未删除且未取消的结算单引用装柜清单时，装柜清单取消由
/// <c>ContainerLoadingListController</c> 协同拒绝，直到显式取消结算单释放。</para>
/// <para>本护栏不写库存 / 资金 / 会计，不产生收款、付款、核销、分摊入账或任何流水，也不改写装柜清单与费用分摊证据。</para>
/// </summary>
[Route("api/finance/container-settlements")]
public class FinanceContainerSettlementController : DocumentControllerBase<FinanceContainerSettlement>
{
    private readonly IDocumentNumberService _noService;

    public FinanceContainerSettlementController(IErpDbContext db, IDocumentNumberService noService) : base(db)
    {
        _noService = noService;
    }

    /// <summary>分页：身份 / 菜单授权与客户数据范围在计数与取行之前生效（受限制账号看不到范围外结算单）。</summary>
    [HttpGet]
    public async Task<IActionResult> GetPaged([FromQuery] PageQuery query, [FromQuery] DocumentStatus? status)
    {
        var scope = await FinanceContainerSettlementLifecycleRules.EnsureMenuAuthorizedAsync(Db, CurrentUserId());
        query.Normalize();
        var source = SalespersonDataScopeService.FilterByCustomer(
            Set.AsNoTracking().Where(o => !o.IsDeleted), scope, o => o.CustomerId);
        if (status.HasValue) source = source.Where(o => o.Status == status.Value);
        if (!string.IsNullOrWhiteSpace(query.Keyword)) source = source.Where(o => o.SettlementNo.Contains(query.Keyword));
        var total = await source.CountAsync();
        var items = await source.OrderByDescending(o => o.Id)
            .Skip((query.Page - 1) * query.PageSize).Take(query.PageSize).ToListAsync();
        return Ok(ApiResponse<PagedResult<FinanceContainerSettlement>>.Success(
            new PagedResult<FinanceContainerSettlement> { Items = items, Total = total, Page = query.Page, PageSize = query.PageSize }));
    }

    /// <summary>详情：按结算单权威客户做身份 / 菜单授权 / 客户数据范围（含来源清单参与方范围）复核，越界 fail closed。</summary>
    [HttpGet("{id:long}")]
    public async Task<IActionResult> GetById(long id)
    {
        var scope = await FinanceContainerSettlementLifecycleRules.EnsureMenuAuthorizedAsync(Db, CurrentUserId());
        var entity = await Set.AsNoTracking()
            .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
            ?? throw BusinessException.NotFound("装柜结算单不存在");
        await FinanceContainerSettlementLifecycleRules.EnsureStoredSettlementScopeAllowedAsync(Db, scope, entity);
        return Ok(ApiResponse<FinanceContainerSettlement>.Success(entity));
    }

    /// <summary>
    /// 来源装柜清单候选（ERP-392，只读、有界、分页）：先做实时身份 + 既有「装柜结算单」（<c>container-settlement</c>）菜单
    /// + 既有「装柜清单」（<c>loading-list</c>）菜单授权，再校验<b>精确客户</b>落在当前账号客户数据范围，之后才计数 / 取数；
    /// 候选只含未删除、未取消，且该客户确实是权威归属客户（有效参与方之一或历史兼容客户）的装柜清单，
    /// 共享柜的全部有效参与方与显式上游客户都必须在范围内。
    /// <para><b>候选选择不等于授权</b>：候选只供显式选择，最终保存仍由 ERP-384 生命周期规则在锁内复核精确来源；
    /// 不返回任意客户清单，也不提供按猜测 Id 直取候选的旁路；<b>不新增任何用户授权，也不提供匿名 / 管理员降级</b>。</para>
    /// </summary>
    [HttpGet("loading-list-candidates")]
    public async Task<IActionResult> GetLoadingListCandidates(
        [FromQuery] long? customerId, [FromQuery] string? keyword,
        [FromQuery] int page = 0, [FromQuery] int pageSize = 0)
    {
        var scope = await FinanceContainerSettlementLifecycleRules.EnsureMenuAuthorizedAsync(Db, CurrentUserId());
        // 既有「装柜清单」菜单授权（实时、fail closed）也必须显式具备，与结算单授权同时生效。
        await LoadingListAuthorizationRules.EnsureAuthorizedAsync(Db, CurrentUserId());
        if (customerId is not > 0)
            throw BusinessException.InvalidParameter(ContainerSettlementLoadingSourceService.ExactCustomerRequiredText);
        FinanceContainerSettlementLifecycleRules.EnsureCustomerInScope(scope, customerId.Value);

        var candidates = await ContainerSettlementLoadingSourceService.QueryCandidatesAsync(
            Db, scope, customerId.Value, keyword, page, pageSize);
        return Ok(ApiResponse<ContainerSettlementLoadingListCandidatePageDto>.Success(
            candidates, ContainerSettlementLoadingSourceService.CandidateRuleText));
    }

    /// <summary>
    /// 已存储来源装柜清单的只读展示（ERP-392，详情 / 重开用）：按结算单权威客户复核实时身份 / 菜单授权 / 客户数据范围后，
    /// 显式标注未关联 / 已关联 / 来源已取消 / 来源不可用；历史已取消 / 不可用来源原样保留、只读可读，
    /// 绝不写库、绝不静默清除 / 重绑定。</summary>
    [HttpGet("{id:long}/loading-list-source")]
    public async Task<IActionResult> GetLoadingListSource(long id)
    {
        var scope = await FinanceContainerSettlementLifecycleRules.EnsureMenuAuthorizedAsync(Db, CurrentUserId());
        var entity = await Set.AsNoTracking()
            .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
            ?? throw BusinessException.NotFound("装柜结算单不存在");
        await FinanceContainerSettlementLifecycleRules.EnsureStoredSettlementScopeAllowedAsync(Db, scope, entity);

        var view = await ContainerSettlementLoadingSourceService.DescribeStoredSourceAsync(
            Db, null, entity.LoadingListId);
        return Ok(ApiResponse<ContainerSettlementLoadingSourceViewDto>.Success(
            view, ContainerSettlementLoadingSourceService.StoredSourceRuleText));
    }

    /// <summary>
    /// 创建：在生成结算单号之前先完成身份 / 菜单授权 / 客户数据范围 + 金额 + 客户可用性 + 来源清单资格校验
    /// （授权与校验失败绝不消耗单号、绝不写入）；关系型后端在同一事务内先取来源装柜清单行锁、锁内权威复核后才生成单号并落库。
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] FinanceContainerSettlement entity)
    {
        var scope = await FinanceContainerSettlementLifecycleRules.EnsureMenuAuthorizedAsync(Db, CurrentUserId());
        var (totalAmount, freightCost, otherCost) = FinanceContainerSettlementLifecycleRules.NormalizeAmounts(
            entity.TotalAmount, entity.FreightCost, entity.OtherCost);
        FinanceContainerSettlementLifecycleRules.EnsureCustomerInScope(scope, entity.CustomerId);
        await FinanceContainerSettlementLifecycleRules.EnsureCustomerAvailableAsync(Db, entity.CustomerId);
        await FinanceContainerSettlementLifecycleRules.ResolveAndAuthorizeLoadingListAsync(
            Db, scope, entity.LoadingListId, entity.CustomerId);

        var lockIds = FinanceContainerSettlementLifecycleRules.MergeLoadingListLockIds(entity.LoadingListId);
        await using var transaction = FinanceContainerSettlementLifecycleRules.IsRelationalProvider(Db)
            ? await Db.Database.BeginTransactionAsync()
            : null;
        try
        {
            // ERP-384 锁序：先来源装柜清单行，后结算单行（本动作无既有结算单行，仅取来源清单行锁）。
            await FinanceContainerSettlementLifecycleRules.LockLoadingListRowsAsync(Db, lockIds);
            await FinanceContainerSettlementLifecycleRules.ResolveAndAuthorizeLoadingListAsync(
                Db, scope, entity.LoadingListId, entity.CustomerId);
            await FinanceContainerSettlementLifecycleRules.EnsureCustomerAvailableAsync(Db, entity.CustomerId);

            entity.Id = 0;
            entity.SettlementNo = await _noService.GenerateAsync(DocumentType.ContainerSettlement);
            entity.Status = DocumentStatus.Pending;
            entity.TotalAmount = totalAmount;
            entity.FreightCost = freightCost;
            entity.OtherCost = otherCost;
            entity.CreatedAt = DateTime.Now;
            Db.FinanceContainerSettlements.Add(entity);
            await Db.SaveChangesAsync();
            if (transaction is not null) await transaction.CommitAsync();
            return Ok(ApiResponse<object>.Success(new { entity.Id, entity.SettlementNo }, "装柜结算单创建成功"));
        }
        catch
        {
            if (transaction is not null) await transaction.RollbackAsync();
            throw;
        }
    }

    /// <summary>
    /// 修改：仅待提交可改；同一事务内先取既有 + 请求来源装柜清单行锁、再取结算单行锁，
    /// 锁内复核权威客户范围 / 状态 / 金额与来源链接；任一步失败即回滚，原始字段与审计保持不变。
    /// </summary>
    [HttpPut("{id:long}")]
    public async Task<IActionResult> Update(long id, [FromBody] FinanceContainerSettlement entity)
    {
        var scope = await FinanceContainerSettlementLifecycleRules.EnsureMenuAuthorizedAsync(Db, CurrentUserId());
        var (totalAmount, freightCost, otherCost) = FinanceContainerSettlementLifecycleRules.NormalizeAmounts(
            entity.TotalAmount, entity.FreightCost, entity.OtherCost);
        FinanceContainerSettlementLifecycleRules.EnsureCustomerInScope(scope, entity.CustomerId);
        await FinanceContainerSettlementLifecycleRules.EnsureCustomerAvailableAsync(Db, entity.CustomerId);

        var persistedLoadingListId = await FinanceContainerSettlementLifecycleRules
            .ReadSourceLoadingListIdAsync(Db, id);
        var lockIds = FinanceContainerSettlementLifecycleRules.MergeLoadingListLockIds(
            persistedLoadingListId, entity.LoadingListId);

        await using var transaction = FinanceContainerSettlementLifecycleRules.IsRelationalProvider(Db)
            ? await Db.Database.BeginTransactionAsync()
            : null;
        try
        {
            await FinanceContainerSettlementLifecycleRules.LockLoadingListRowsAsync(Db, lockIds);
            await FinanceContainerSettlementLifecycleRules.LockSettlementRowAsync(Db, id);

            var existing = await Db.FinanceContainerSettlements
                .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
                ?? throw BusinessException.NotFound("装柜结算单不存在");

            // 锁内复核：既有单据权威客户范围 + 允许的状态 + 请求来源 / 客户资格。
            await FinanceContainerSettlementLifecycleRules
                .EnsureStoredSettlementScopeAllowedAsync(Db, scope, existing);
            if (GetStatus(existing) != DocumentStatus.Pending)
                throw BusinessException.RuleConflict("仅待提交状态的单据可修改");
            await FinanceContainerSettlementLifecycleRules.ResolveAndAuthorizeLoadingListAsync(
                Db, scope, entity.LoadingListId, entity.CustomerId);

            existing.SettlementDate = entity.SettlementDate;
            existing.LoadingListId = entity.LoadingListId;
            existing.CustomerId = entity.CustomerId;
            existing.TotalAmount = totalAmount;
            existing.FreightCost = freightCost;
            existing.OtherCost = otherCost;
            existing.Remark = entity.Remark;
            existing.UpdatedAt = DateTime.Now;
            await Db.SaveChangesAsync();
            if (transaction is not null) await transaction.CommitAsync();
            return Ok(ApiResponse<object>.Success(null, "装柜结算单更新成功"));
        }
        catch
        {
            if (transaction is not null) await transaction.RollbackAsync();
            throw;
        }
    }

    /// <summary>
    /// 费用分摊证据（ERP-060 + ERP-384 护栏，**只读**）：本单持久化字段（结算总金额 / 海运费 / 其他费用 / 客户）的只读回显
    /// + 本单显式关联装柜清单上 ERP-042 分摊批次 / 分摊行的证据（按「币种 → 客户」分组、含未分摊参考与
    /// 已作废历史）。授权（实时身份 / 菜单 / 客户数据范围 + 来源清单参与方范围 + 分摊行每一个客户范围）
    /// 在读取任何证据之前完成；分摊证据**不参与**结算金额计算，也**不会**被写入本单任何字段；金额对照只作算术证据，
    /// 不是结算差异、应收应付或对账结论。未关联装柜清单时证据显示「未知」，不按柜号或客户推断。
    /// </summary>
    [HttpGet("{id:long}/expense-allocation-evidence")]
    public async Task<IActionResult> GetExpenseAllocationEvidence(
        long id,
        [FromQuery] bool includeHistory = true,
        [FromQuery] int historyTake = ContainerExpenseAllocationEvidenceRules.DefaultHistoryTake)
    {
        var scope = await FinanceContainerSettlementLifecycleRules.EnsureMenuAuthorizedAsync(Db, CurrentUserId());
        var entity = await Set.AsNoTracking()
            .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
            ?? throw BusinessException.NotFound("装柜结算单不存在");
        await FinanceContainerSettlementLifecycleRules.EnsureStoredSettlementScopeAllowedAsync(Db, scope, entity);
        await FinanceContainerSettlementLifecycleRules
            .EnsureEvidenceCustomersInScopeAsync(Db, scope, entity.LoadingListId);

        var evidence = await ContainerExpenseAllocationEvidenceService.GetForSettlementAsync(
            Db, entity.Id, includeHistory, historyTake);
        return Ok(ApiResponse<ContainerSettlementAllocationEvidenceDto>.Success(
            evidence, "已按显式装柜结算单返回分摊证据（只读：结算金额字段为原值回显，分摊证据不参与结算计算）"));
    }

    /// <summary>提交：待提交 → 已提交。同一事务内先取来源装柜清单行锁、再取结算单行锁，锁内复核范围 / 状态 / 金额。</summary>
    [HttpPost("{id:long}/submit")]
    public override Task<IActionResult> Submit(long id)
        => TransitionAsync(id, DocumentStatus.Pending, DocumentStatus.Submitted, "提交成功", validateAmounts: true);

    /// <summary>审核：已提交 → 已审核。锁内复核范围 / 状态 / 金额；本护栏不写库存 / 资金 / 会计。</summary>
    [HttpPost("{id:long}/approve")]
    public override Task<IActionResult> Approve(long id)
        => TransitionAsync(id, DocumentStatus.Submitted, DocumentStatus.Approved, "审核通过", validateAmounts: true);

    /// <summary>
    /// 取消：把状态置为「已取消」（幂等拒绝重复取消）。显式取消即释放「装柜清单取消占用」护栏；
    /// 历史字段 / 来源链接与审计原样保留，绝不物理删除，也不写库存 / 资金。
    /// </summary>
    [HttpPost("{id:long}/cancel")]
    public override Task<IActionResult> Cancel(long id)
        => TransitionAsync(id, null, DocumentStatus.Cancelled, "已取消", validateAmounts: false);

    /// <summary>删除（软删除）：仅待提交可删；锁内复核范围 / 状态，失败回滚且不改写原字段与审计。</summary>
    [HttpDelete("{id:long}")]
    public override async Task<IActionResult> Delete(long id)
    {
        var scope = await FinanceContainerSettlementLifecycleRules.EnsureMenuAuthorizedAsync(Db, CurrentUserId());
        var persistedLoadingListId = await FinanceContainerSettlementLifecycleRules
            .ReadSourceLoadingListIdAsync(Db, id);
        var lockIds = FinanceContainerSettlementLifecycleRules.MergeLoadingListLockIds(persistedLoadingListId);

        await using var transaction = FinanceContainerSettlementLifecycleRules.IsRelationalProvider(Db)
            ? await Db.Database.BeginTransactionAsync()
            : null;
        try
        {
            await FinanceContainerSettlementLifecycleRules.LockLoadingListRowsAsync(Db, lockIds);
            await FinanceContainerSettlementLifecycleRules.LockSettlementRowAsync(Db, id);

            var entity = await Db.FinanceContainerSettlements
                .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
                ?? throw BusinessException.NotFound("装柜结算单不存在");

            await FinanceContainerSettlementLifecycleRules.EnsureStoredSettlementScopeAllowedAsync(Db, scope, entity);
            if (GetStatus(entity) != DocumentStatus.Pending)
                throw BusinessException.RuleConflict("仅待提交状态的单据可删除");

            entity.IsDeleted = true;
            entity.UpdatedAt = DateTime.Now;
            await Db.SaveChangesAsync();
            if (transaction is not null) await transaction.CommitAsync();
            return Ok(ApiResponse<object>.Success(null, "删除成功"));
        }
        catch
        {
            if (transaction is not null) await transaction.RollbackAsync();
            throw;
        }
    }

    /// <summary>
    /// 提交 / 审核 / 取消共用：同一事务内先取来源装柜清单行锁、再取结算单行锁，加载权威单据并在锁内复核
    /// 身份 / 菜单授权 / 客户范围 / 状态（可选金额），失败整体回滚（状态与审计不变）。
    /// </summary>
    private async Task<IActionResult> TransitionAsync(
        long id, DocumentStatus? from, DocumentStatus to, string message, bool validateAmounts)
    {
        var scope = await FinanceContainerSettlementLifecycleRules.EnsureMenuAuthorizedAsync(Db, CurrentUserId());
        var persistedLoadingListId = await FinanceContainerSettlementLifecycleRules
            .ReadSourceLoadingListIdAsync(Db, id);
        var lockIds = FinanceContainerSettlementLifecycleRules.MergeLoadingListLockIds(persistedLoadingListId);

        await using var transaction = FinanceContainerSettlementLifecycleRules.IsRelationalProvider(Db)
            ? await Db.Database.BeginTransactionAsync()
            : null;
        try
        {
            await FinanceContainerSettlementLifecycleRules.LockLoadingListRowsAsync(Db, lockIds);
            await FinanceContainerSettlementLifecycleRules.LockSettlementRowAsync(Db, id);

            var entity = await Db.FinanceContainerSettlements
                .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
                ?? throw BusinessException.NotFound("装柜结算单不存在");

            await FinanceContainerSettlementLifecycleRules.EnsureStoredSettlementScopeAllowedAsync(Db, scope, entity);

            if (from.HasValue)
            {
                if (GetStatus(entity) != from.Value)
                    throw BusinessException.RuleConflict("当前状态不允许该操作");
            }
            else if (to == DocumentStatus.Cancelled && GetStatus(entity) == DocumentStatus.Cancelled)
            {
                throw BusinessException.RuleConflict("装柜结算单已取消，不能重复取消");
            }

            if (validateAmounts)
            {
                // 锁内复核持久化金额仍满足既有口径（历史合法数据原样保留，绝不改写）。
                FinanceContainerSettlementLifecycleRules.NormalizeAmounts(
                    entity.TotalAmount, entity.FreightCost, entity.OtherCost);
            }

            SetStatus(entity, to);
            await Db.SaveChangesAsync();
            if (transaction is not null) await transaction.CommitAsync();
            return Ok(ApiResponse<object>.Success(null, message));
        }
        catch
        {
            if (transaction is not null) await transaction.RollbackAsync();
            throw;
        }
    }
}

/// <summary>
/// 散货结算单控制器（ERP-387 生命周期护栏）：列表 / 详情 / 创建 / 修改 / 提交 / 审核 / 取消 / 删除
/// 都先重新校验实时启用身份（账号存在、未删除且启用）、既有「散货结算单」（<c>bulk-settlement</c>）菜单授权
/// 与当前权威客户数据范围；创建 / 修改校验真实启用客户，读取 / 状态变更 / 删除前复核已存储客户归属
/// （受限账号对无权威归属或范围外客户 fail closed，特权账号保留历史访问）。
/// <para>金额只按既有存储精度（<c>decimal(18,2)</c>，2 位小数）校验：总金额 &gt; 0，海运费 &gt;= 0；
/// 散货结算单没有币种字段，因此不做任何汇率换算、跨币种聚合或 <c>total = freight</c> 公式，
/// 也不按备注 / 文本推断装运 / 订单来源。</para>
/// <para>修改 / 提交 / 审核 / 取消 / 删除都在同一事务内先取散货结算单行锁，锁内重新加载并复核权威状态 / 客户 / 金额；
/// 失败整体回滚（状态、原始字段与审计不变）。创建在生成单号与写入之前完成全部校验（失败绝不消耗单号）。</para>
/// <para>本护栏不写库存 / 资金 / 会计，不产生收款、付款、核销、分摊入账或任何流水，只做软删除。</para>
/// </summary>
[Route("api/finance/bulk-settlements")]
public class FinanceBulkSettlementController : DocumentControllerBase<FinanceBulkSettlement>
{
    private readonly IDocumentNumberService _noService;

    public FinanceBulkSettlementController(IErpDbContext db, IDocumentNumberService noService) : base(db)
    {
        _noService = noService;
    }

    /// <summary>分页：身份 / 菜单授权与客户数据范围在计数与取行之前生效（受限制账号看不到范围外结算单）。</summary>
    [HttpGet]
    public async Task<IActionResult> GetPaged([FromQuery] PageQuery query, [FromQuery] DocumentStatus? status)
    {
        var scope = await FinanceBulkSettlementLifecycleRules.EnsureMenuAuthorizedAsync(Db, CurrentUserId());
        query.Normalize();
        var source = SalespersonDataScopeService.FilterByCustomer(
            Set.AsNoTracking().Where(o => !o.IsDeleted), scope, o => o.CustomerId);
        if (status.HasValue) source = source.Where(o => o.Status == status.Value);
        if (!string.IsNullOrWhiteSpace(query.Keyword)) source = source.Where(o => o.SettlementNo.Contains(query.Keyword));
        var total = await source.CountAsync();
        var items = await source.OrderByDescending(o => o.Id)
            .Skip((query.Page - 1) * query.PageSize).Take(query.PageSize).ToListAsync();
        return Ok(ApiResponse<PagedResult<FinanceBulkSettlement>>.Success(
            new PagedResult<FinanceBulkSettlement> { Items = items, Total = total, Page = query.Page, PageSize = query.PageSize }));
    }

    /// <summary>详情：按结算单权威客户做身份 / 菜单授权 / 客户数据范围复核，越界 fail closed。</summary>
    [HttpGet("{id:long}")]
    public async Task<IActionResult> GetById(long id)
    {
        var scope = await FinanceBulkSettlementLifecycleRules.EnsureMenuAuthorizedAsync(Db, CurrentUserId());
        var entity = await Set.AsNoTracking()
            .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
            ?? throw BusinessException.NotFound("散货结算单不存在");
        FinanceBulkSettlementLifecycleRules.EnsureStoredSettlementScopeAllowedAsync(scope, entity);
        return Ok(ApiResponse<FinanceBulkSettlement>.Success(entity));
    }

    /// <summary>
    /// 创建：在生成结算单号之前先完成身份 / 菜单授权 / 客户数据范围 + 金额 + 客户可用性校验
    /// （授权与校验失败绝不消耗单号、绝不写入）；关系型后端在同一事务内锁内复核客户仍真实可用后才生成单号并落库。
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] FinanceBulkSettlement entity)
    {
        var scope = await FinanceBulkSettlementLifecycleRules.EnsureMenuAuthorizedAsync(Db, CurrentUserId());
        var (totalAmount, freightCost) = FinanceBulkSettlementLifecycleRules.NormalizeAmounts(
            entity.TotalAmount, entity.FreightCost);
        FinanceBulkSettlementLifecycleRules.EnsureCustomerInScope(scope, entity.CustomerId);
        await FinanceBulkSettlementLifecycleRules.EnsureCustomerAvailableAsync(Db, entity.CustomerId);

        await using var transaction = FinanceBulkSettlementLifecycleRules.IsRelationalProvider(Db)
            ? await Db.Database.BeginTransactionAsync()
            : null;
        try
        {
            // 锁内复核客户仍真实可用（并发停用 / 删除收敛）；校验一律先于单号预留。
            await FinanceBulkSettlementLifecycleRules.EnsureCustomerAvailableAsync(Db, entity.CustomerId);

            entity.Id = 0;
            entity.SettlementNo = await _noService.GenerateAsync(DocumentType.BulkSettlement);
            entity.Status = DocumentStatus.Pending;
            entity.TotalAmount = totalAmount;
            entity.FreightCost = freightCost;
            entity.CreatedAt = DateTime.Now;
            Db.FinanceBulkSettlements.Add(entity);
            await Db.SaveChangesAsync();
            if (transaction is not null) await transaction.CommitAsync();
            return Ok(ApiResponse<object>.Success(new { entity.Id, entity.SettlementNo }, "散货结算单创建成功"));
        }
        catch
        {
            if (transaction is not null) await transaction.RollbackAsync();
            throw;
        }
    }

    /// <summary>
    /// 修改：仅待提交可改；在同一事务内先取散货结算单行锁，锁内重新加载并复核身份 / 菜单授权 / 客户范围 /
    /// 状态 / 请求客户与金额，失败整体回滚（原字段与审计不变）。
    /// </summary>
    [HttpPut("{id:long}")]
    public async Task<IActionResult> Update(long id, [FromBody] FinanceBulkSettlement entity)
    {
        var scope = await FinanceBulkSettlementLifecycleRules.EnsureMenuAuthorizedAsync(Db, CurrentUserId());
        var (totalAmount, freightCost) = FinanceBulkSettlementLifecycleRules.NormalizeAmounts(
            entity.TotalAmount, entity.FreightCost);
        FinanceBulkSettlementLifecycleRules.EnsureCustomerInScope(scope, entity.CustomerId);
        await FinanceBulkSettlementLifecycleRules.EnsureCustomerAvailableAsync(Db, entity.CustomerId);

        await using var transaction = FinanceBulkSettlementLifecycleRules.IsRelationalProvider(Db)
            ? await Db.Database.BeginTransactionAsync()
            : null;
        try
        {
            await FinanceBulkSettlementLifecycleRules.LockSettlementRowAsync(Db, id);

            var existing = await Db.FinanceBulkSettlements
                .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
                ?? throw BusinessException.NotFound("散货结算单不存在");

            // 锁内复核：既有单据权威客户范围 + 允许的状态 + 请求客户仍真实可用。
            FinanceBulkSettlementLifecycleRules.EnsureStoredSettlementScopeAllowedAsync(scope, existing);
            if (GetStatus(existing) != DocumentStatus.Pending)
                throw BusinessException.RuleConflict("仅待提交状态的单据可修改");
            await FinanceBulkSettlementLifecycleRules.EnsureCustomerAvailableAsync(Db, entity.CustomerId);

            existing.SettlementDate = entity.SettlementDate;
            existing.CustomerId = entity.CustomerId;
            existing.TotalAmount = totalAmount;
            existing.FreightCost = freightCost;
            existing.Remark = entity.Remark;
            existing.UpdatedAt = DateTime.Now;
            await Db.SaveChangesAsync();
            if (transaction is not null) await transaction.CommitAsync();
            return Ok(ApiResponse<object>.Success(null, "散货结算单更新成功"));
        }
        catch
        {
            if (transaction is not null) await transaction.RollbackAsync();
            throw;
        }
    }
    /// <summary>提交：待提交 → 已提交。同一事务内取散货结算单行锁，锁内复核范围 / 状态 / 金额。</summary>
    [HttpPost("{id:long}/submit")]
    public override Task<IActionResult> Submit(long id)
        => TransitionAsync(id, DocumentStatus.Pending, DocumentStatus.Submitted, "提交成功", validateAmounts: true);

    /// <summary>审核：已提交 → 已审核。锁内复核范围 / 状态 / 金额；本护栏不写库存 / 资金 / 会计。</summary>
    [HttpPost("{id:long}/approve")]
    public override Task<IActionResult> Approve(long id)
        => TransitionAsync(id, DocumentStatus.Submitted, DocumentStatus.Approved, "审核通过", validateAmounts: true);

    /// <summary>
    /// 取消：把状态置为「已取消」（幂等拒绝重复取消）。历史字段与审计原样保留，绝不物理删除，也不写库存 / 资金。
    /// </summary>
    [HttpPost("{id:long}/cancel")]
    public override Task<IActionResult> Cancel(long id)
        => TransitionAsync(id, null, DocumentStatus.Cancelled, "已取消", validateAmounts: false);

    /// <summary>删除（软删除）：仅待提交可删；锁内复核范围 / 状态，失败回滚且不改写原字段与审计。</summary>
    [HttpDelete("{id:long}")]
    public override async Task<IActionResult> Delete(long id)
    {
        var scope = await FinanceBulkSettlementLifecycleRules.EnsureMenuAuthorizedAsync(Db, CurrentUserId());

        await using var transaction = FinanceBulkSettlementLifecycleRules.IsRelationalProvider(Db)
            ? await Db.Database.BeginTransactionAsync()
            : null;
        try
        {
            await FinanceBulkSettlementLifecycleRules.LockSettlementRowAsync(Db, id);

            var entity = await Db.FinanceBulkSettlements
                .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
                ?? throw BusinessException.NotFound("散货结算单不存在");

            FinanceBulkSettlementLifecycleRules.EnsureStoredSettlementScopeAllowedAsync(scope, entity);
            if (GetStatus(entity) != DocumentStatus.Pending)
                throw BusinessException.RuleConflict("仅待提交状态的单据可删除");

            entity.IsDeleted = true;
            entity.UpdatedAt = DateTime.Now;
            await Db.SaveChangesAsync();
            if (transaction is not null) await transaction.CommitAsync();
            return Ok(ApiResponse<object>.Success(null, "删除成功"));
        }
        catch
        {
            if (transaction is not null) await transaction.RollbackAsync();
            throw;
        }
    }

    /// <summary>
    /// 提交 / 审核 / 取消共用：同一事务内先取散货结算单行锁，加载权威单据并在锁内复核
    /// 身份 / 菜单授权 / 客户范围 / 状态（可选金额），失败整体回滚（状态与审计不变）。
    /// </summary>
    private async Task<IActionResult> TransitionAsync(
        long id, DocumentStatus? from, DocumentStatus to, string message, bool validateAmounts)
    {
        var scope = await FinanceBulkSettlementLifecycleRules.EnsureMenuAuthorizedAsync(Db, CurrentUserId());

        await using var transaction = FinanceBulkSettlementLifecycleRules.IsRelationalProvider(Db)
            ? await Db.Database.BeginTransactionAsync()
            : null;
        try
        {
            await FinanceBulkSettlementLifecycleRules.LockSettlementRowAsync(Db, id);

            var entity = await Db.FinanceBulkSettlements
                .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
                ?? throw BusinessException.NotFound("散货结算单不存在");

            FinanceBulkSettlementLifecycleRules.EnsureStoredSettlementScopeAllowedAsync(scope, entity);

            if (from.HasValue)
            {
                if (GetStatus(entity) != from.Value)
                    throw BusinessException.RuleConflict("当前状态不允许该操作");
            }
            else if (to == DocumentStatus.Cancelled && GetStatus(entity) == DocumentStatus.Cancelled)
            {
                throw BusinessException.RuleConflict("散货结算单已取消，不能重复取消");
            }

            if (validateAmounts)
            {
                // 锁内复核持久化金额仍满足既有口径（历史合法数据原样保留，绝不改写）。
                FinanceBulkSettlementLifecycleRules.NormalizeAmounts(entity.TotalAmount, entity.FreightCost);
            }

            SetStatus(entity, to);
            await Db.SaveChangesAsync();
            if (transaction is not null) await transaction.CommitAsync();
            return Ok(ApiResponse<object>.Success(null, message));
        }
        catch
        {
            if (transaction is not null) await transaction.RollbackAsync();
            throw;
        }
    }

}
