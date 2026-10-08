using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace ERP.Api.Controllers;

/// <summary>
/// 询价单控制器（业务链：询价单 Inquiry → 报价单 Quotation → PI → 销售订单）。
/// <para>ERP-402：列表 / 详情 / 新增 / 修改 / 提交 / 审核 / 取消 / 删除 / 批量删除 / 导出 / 明细带入 / 转报价单，
/// 每一路由在读取任何计数、生成 / 消耗单据号或写入任何数据<b>之前</b>都先经
/// <see cref="InquiryAuthorizationRules"/> 复核<b>实时启用身份</b>、既有「询价单」（<c>inquiry</c>）菜单授权与
/// 既有业务员数据范围；转报价单额外要求既有「报价单」（<c>quotation</c>）菜单授权。
/// 生命周期变更 / 批量删除 / 转报价单都在「询价单来源行锁 + 原子事务 + 锁内权威重读」内执行，
/// 与报价单锁的确定顺序为「询价单行锁 → 报价单行锁」。</para>
/// </summary>
[Route("api/inquiries")]
public class InquiryController : DocumentControllerBase<Inquiry>
{
    private readonly IDocumentNumberService _noService;

    public InquiryController(IErpDbContext db, IDocumentNumberService noService) : base(db)
    {
        _noService = noService;
    }

    /// <summary>询价单不存在 / 越界（不泄露范围外询价单）统一按「不存在」拒绝（fail closed）。</summary>
    private static BusinessException InquiryNotFound() => BusinessException.NotFound("询价单不存在");

    /// <summary>已存询价单的权威归属范围复核：受限账号越界按范围拒绝（fail closed，不泄露范围外询价单）。</summary>
    private static void EnsureVisible(SalespersonDataScope? scope, Inquiry entity)
        => InquiryAuthorizationRules.EnsureStoredCustomerInScope(scope, entity.CustomerId);

    // ==================== ERP-402 确定性询价单来源行锁 + 原子事务 ====================

    /// <summary>
    /// 锁内**权威重读**询价单（不复用加锁前的内存实体）：先取询价单来源行锁，再重新加载含明细的权威行，
    /// 并按实时身份复核已存归属 —— 并发改写 / 删除 / 越界一律在改写任何字段之前原子拒绝。
    /// </summary>
    private async Task<Inquiry> ReloadLockedAsync(SalespersonDataScope scope, long id)
    {
        if (!await InquiryMutationRules.LockInquiryRowAsync(Db, id))
            throw InquiryNotFound();
        var entity = await Db.Inquiries.Include(o => o.Details)
            .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
            ?? throw InquiryNotFound();
        InquiryAuthorizationRules.EnsureStoredCustomerInScope(scope, entity.CustomerId);
        return entity;
    }

    /// <summary>
    /// 在「询价单来源行锁 + 原子事务」内执行一次生命周期变更：授权 → 开事务 → 加锁并锁内权威重读 →
    /// 调用 <paramref name="body"/>（自行 SaveChanges）→ 提交；任一步失败整体回滚并丢弃半成品变更，
    /// 绝不留下撕裂状态或半成品写入。并发令牌过期（RowVersion）转为可读的业务冲突。
    /// </summary>
    private async Task<IActionResult> RunLockedMutationAsync(long id,
        Func<SalespersonDataScope, Inquiry, Task<IActionResult>> body)
    {
        var scope = await InquiryAuthorizationRules.EnsureAuthorizedAsync(Db, CurrentUserId());
        await using var transaction = await InquiryMutationRules.BeginMutationTransactionAsync(Db);
        try
        {
            var entity = await ReloadLockedAsync(scope, id);
            var result = await body(scope, entity);
            if (transaction is not null) await transaction.CommitAsync();
            return result;
        }
        catch (DbUpdateConcurrencyException)
        {
            await RollbackAsync(transaction);
            throw BusinessException.RuleConflict(InquiryMutationRules.StaleRowVersionText);
        }
        catch
        {
            await RollbackAsync(transaction);
            throw;
        }
    }

    /// <summary>回滚当前事务并丢弃变更跟踪器中的半成品变更（内存库无事务时同样清理，绝不残留部分写入）。</summary>
    private async Task RollbackAsync(IDbContextTransaction? transaction)
    {
        if (transaction is not null) await transaction.RollbackAsync();
        InquiryMutationRules.DiscardTrackedChanges(Db);
    }

    /// <summary>是否存在实时（未删除）报价单下游链接（只按持久化 <c>Quotation.InquiryId</c> 判定）。</summary>
    private Task<bool> HasLiveQuotationAsync(long inquiryId)
        => InquiryMutationRules.HasLiveQuotationAsync(Db, inquiryId);

    /// <summary>分页查询（ERP-402：实时授权 + 范围先于计数 / 分页下推）</summary>
    [HttpGet]
    public async Task<IActionResult> GetPaged([FromQuery] PageQuery query, [FromQuery] DocumentStatus? status)
    {
        var scope = await InquiryAuthorizationRules.EnsureAuthorizedAsync(Db, CurrentUserId());
        query.Normalize();
        var source = InquiryAuthorizationRules.ApplyScope(
            Set.AsNoTracking().Where(o => !o.IsDeleted), scope);
        if (status.HasValue) source = source.Where(o => o.Status == status.Value);
        if (!string.IsNullOrWhiteSpace(query.Keyword)) source = source.Where(o => o.InquiryNo.Contains(query.Keyword));

        var total = await source.CountAsync();
        var items = await source.OrderByDescending(o => o.Id)
            .Skip((query.Page - 1) * query.PageSize).Take(query.PageSize).ToListAsync();
        return Ok(ApiResponse<PagedResult<Inquiry>>.Success(
            new PagedResult<Inquiry> { Items = items, Total = total, Page = query.Page, PageSize = query.PageSize }));
    }

    /// <summary>查询详情（含明细；ERP-402：实时授权 + 权威归属复核）</summary>
    [HttpGet("{id:long}")]
    public async Task<IActionResult> GetById(long id)
    {
        var scope = await InquiryAuthorizationRules.EnsureAuthorizedAsync(Db, CurrentUserId());
        var entity = await Set.AsNoTracking().Include(o => o.Details)
            .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
            ?? throw InquiryNotFound();
        EnsureVisible(scope, entity);
        return Ok(ApiResponse<Inquiry>.Success(entity));
    }

    /// <summary>
    /// 把已审核询价单带入报价单新增表单，不落库（ERP-402：既有「询价单」+「报价单」菜单授权 +
    /// 权威归属 + 既有转换资格；只读，不占号、不写库、不改写来源状态）。
    /// </summary>
    [HttpGet("{id:long}/quotation-prefill")]
    public async Task<IActionResult> QuotationPrefill(long id)
    {
        var scope = await InquiryAuthorizationRules
            .EnsureQuotationConversionAuthorizedAsync(Db, CurrentUserId());
        var inquiry = await Db.Inquiries.AsNoTracking().Include(o => o.Details)
            .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
            ?? throw InquiryNotFound();
        EnsureVisible(scope, inquiry);

        var existing = await InquiryMutationRules.FindLiveQuotationAsync(Db, id);
        InquiryMutationRules.EnsureQuotationConversionEligible(inquiry, existing,
            InquiryMutationRules.ActiveDetailCount(inquiry));

        var quotation = await InquiryQuotationConversion.BuildDraftAsync(Db, inquiry);
        return Ok(ApiResponse<object>.Success(new { SourceId = id, quotation }));
    }

    /// <summary>
    /// 把已审核询价单直接生成一张报价单；同一询价单只允许一张（ERP-402：既有「询价单」+「报价单」菜单授权，
    /// 「询价单来源行锁 + 原子事务 + 锁内权威重读」内完成重复检测 / 单号生成 / 草稿构造，任一失败整体回滚）。
    /// </summary>
    [HttpPost("{id:long}/to-quotation")]
    public async Task<IActionResult> ToQuotation(long id)
    {
        var scope = await InquiryAuthorizationRules
            .EnsureQuotationConversionAuthorizedAsync(Db, CurrentUserId());
        await using var transaction = await InquiryMutationRules.BeginMutationTransactionAsync(Db);
        try
        {
            // ERP-402：先取询价单来源行锁，再锁内权威重读（生命周期 / 明细都以持久化行为准）。
            var inquiry = await ReloadLockedAsync(scope, id);

            var existing = await InquiryMutationRules.FindLiveQuotationAsync(Db, id);
            InquiryMutationRules.EnsureQuotationConversionEligible(inquiry, existing,
                InquiryMutationRules.ActiveDetailCount(inquiry));

            var quotation = await InquiryQuotationConversion.BuildDraftAsync(Db, inquiry);
            quotation.QuotationNo = await _noService.GenerateAsync(DocumentType.Quotation);
            quotation.CreatedAt = DateTime.Now;
            foreach (var detail in quotation.Details)
            {
                detail.QuotationNo = quotation.QuotationNo;
                detail.CreatedAt = DateTime.Now;
            }

            Db.Quotations.Add(quotation);
            inquiry.Status = DocumentStatus.Completed;
            inquiry.UpdatedAt = DateTime.Now;
            await Db.SaveChangesAsync();
            if (transaction is not null) await transaction.CommitAsync();
            return Ok(ApiResponse<object>.Success(new
            {
                quotation.Id,
                quotation.QuotationNo,
                SourceId = id,
                SourceNo = inquiry.InquiryNo
            }, "报价单生成成功"));
        }
        catch (DbUpdateConcurrencyException)
        {
            await RollbackAsync(transaction);
            throw BusinessException.RuleConflict(InquiryMutationRules.StaleRowVersionText);
        }
        catch
        {
            await RollbackAsync(transaction);
            throw;
        }
    }

    /// <summary>创建（ERP-402：实时授权 + 拟议客户范围先于单号生成与任何写入）</summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] Inquiry entity)
    {
        var scope = await InquiryAuthorizationRules.EnsureAuthorizedAsync(Db, CurrentUserId());
        InquiryAuthorizationRules.EnsureProposedCustomerInScope(scope, entity.CustomerId);
        entity.Id = 0;
        entity.InquiryNo = await _noService.GenerateAsync(DocumentType.Inquiry);
        entity.Status = DocumentStatus.Pending;
        entity.CreatedAt = DateTime.Now;
        foreach (var d in entity.Details) d.Amount = d.Quantity * d.UnitPrice;
        Db.Inquiries.Add(entity);
        await Db.SaveChangesAsync();
        return Ok(ApiResponse<object>.Success(new { entity.Id, entity.InquiryNo }, "询价单创建成功"));
    }

    /// <summary>
    /// 更新（ERP-402：询价单来源行锁 + 原子事务内锁内权威重读后改写；已存与拟议客户两侧都复核范围；
    /// 存在实时报价单下游的询价单一律冻结，不做反向自动冲销）。
    /// </summary>
    [HttpPut("{id:long}")]
    public async Task<IActionResult> Update(long id, [FromBody] Inquiry entity)
        => await RunLockedMutationAsync(id, async (scope, existing) =>
        {
            InquiryAuthorizationRules.EnsureProposedCustomerInScope(scope, entity.CustomerId);
            InquiryMutationRules.EnsureNoLiveDownstream(await HasLiveQuotationAsync(id));
            InquiryMutationRules.EnsureEditAllowed(GetStatus(existing));

            existing.InquiryDate = entity.InquiryDate;
            existing.CustomerId = entity.CustomerId;
            existing.ContactPerson = entity.ContactPerson;
            existing.ContactPhone = entity.ContactPhone;
            existing.SalesmanId = entity.SalesmanId;
            existing.Currency = entity.Currency;
            existing.ExchangeRate = entity.ExchangeRate;
            existing.ValidDays = entity.ValidDays;
            existing.Remark = entity.Remark;

            Db.InquiryDetails.RemoveRange(existing.Details);
            foreach (var d in entity.Details)
            {
                d.Id = 0;
                d.InquiryId = id;
                d.CreatedAt = DateTime.Now;
                d.Amount = d.Quantity * d.UnitPrice;
            }
            existing.Details = entity.Details;
            existing.UpdatedAt = DateTime.Now;
            await Db.SaveChangesAsync();
            return Ok(ApiResponse<object>.Success(null, "询价单更新成功"));
        });

    /// <summary>提交（ERP-402：询价单来源行锁 + 原子事务内锁内权威重读后 待提交 → 已提交）</summary>
    [HttpPost("{id:long}/submit")]
    public override async Task<IActionResult> Submit(long id)
        => await RunLockedMutationAsync(id, async (_, entity) =>
        {
            InquiryMutationRules.EnsureNoLiveDownstream(await HasLiveQuotationAsync(id));
            InquiryMutationRules.EnsureSubmitAllowed(GetStatus(entity));
            SetStatus(entity, DocumentStatus.Submitted);
            await Db.SaveChangesAsync();
            return Ok(ApiResponse<object>.Success(null, "提交成功"));
        });

    /// <summary>审核（ERP-402：询价单来源行锁 + 原子事务内锁内权威重读后 已提交 → 已审核）</summary>
    [HttpPost("{id:long}/approve")]
    public override async Task<IActionResult> Approve(long id)
        => await RunLockedMutationAsync(id, async (_, entity) =>
        {
            InquiryMutationRules.EnsureNoLiveDownstream(await HasLiveQuotationAsync(id));
            InquiryMutationRules.EnsureApproveAllowed(GetStatus(entity));
            SetStatus(entity, DocumentStatus.Approved);
            await Db.SaveChangesAsync();
            return Ok(ApiResponse<object>.Success(null, "审核通过"));
        });

    /// <summary>取消（ERP-402：已生成实时报价单的询价单一律拒绝取消，保留显式历史，不做反向冲销）</summary>
    [HttpPost("{id:long}/cancel")]
    public override async Task<IActionResult> Cancel(long id)
        => await RunLockedMutationAsync(id, async (_, entity) =>
        {
            InquiryMutationRules.EnsureNoLiveDownstream(await HasLiveQuotationAsync(id));
            InquiryMutationRules.EnsureCancelAllowed(GetStatus(entity));
            SetStatus(entity, DocumentStatus.Cancelled);
            await Db.SaveChangesAsync();
            return Ok(ApiResponse<object>.Success(null, "已取消"));
        });

    /// <summary>删除（软删除，仅待提交状态可删；ERP-402：询价单来源行锁 + 原子事务内锁内权威重读后软删除）</summary>
    [HttpDelete("{id:long}")]
    public override async Task<IActionResult> Delete(long id)
        => await RunLockedMutationAsync(id, async (_, entity) =>
        {
            InquiryMutationRules.EnsureNoLiveDownstream(await HasLiveQuotationAsync(id));
            InquiryMutationRules.EnsureDeleteAllowed(GetStatus(entity));
            entity.IsDeleted = true;
            entity.UpdatedAt = DateTime.Now;
            await Db.SaveChangesAsync();
            return Ok(ApiResponse<object>.Success(null, "删除成功"));
        });

    /// <summary>
    /// 批量软删除（ERP-402）：授权后<strong>按询价单 Id 升序确定性加锁</strong>（与单行生命周期路由
    /// 共用同一把询价单来源行锁），锁内权威重读并逐行复核归属 / 实时报价单下游 / 状态，
    /// 混合允许 / 越界批次<b>整体拒绝</b>、不做部分删除；全部通过后才在同一原子事务内一次性软删除。
    /// </summary>
    [HttpPost("batch-delete")]
    public async Task<IActionResult> BatchDelete([FromBody] List<long> ids)
    {
        var scope = await InquiryAuthorizationRules.EnsureAuthorizedAsync(Db, CurrentUserId());
        var requested = InquiryMutationRules.MergeLockIds(ids);
        if (requested.Count == 0)
            throw BusinessException.InvalidParameter("请指定要删除的询价单");

        await using var transaction = await InquiryMutationRules.BeginMutationTransactionAsync(Db);
        try
        {
            // 锁序：全部询价单来源行按 Id 升序确定性加锁（去重、仅正整数；绝不反向获取其它锁）。
            await InquiryMutationRules.LockInquiryRowsAsync(Db, requested);

            // 锁内权威重读：存在性 / 归属 / 实时报价单下游 / 状态逐行复核，任一行不满足即整体拒绝（无部分删除）。
            var entities = await Db.Inquiries
                .Where(o => requested.Contains(o.Id) && !o.IsDeleted).ToListAsync();
            var found = entities.Select(o => o.Id).ToHashSet();
            var missing = requested.Where(x => !found.Contains(x)).ToList();
            if (missing.Count > 0)
                throw BusinessException.NotFound(
                    $"批量删除的询价单不存在或已删除：{string.Join("、", missing)}（未做任何部分删除）");

            foreach (var inquiry in entities)
            {
                InquiryAuthorizationRules.EnsureStoredCustomerInScope(scope, inquiry.CustomerId);
                InquiryMutationRules.EnsureNoLiveDownstream(await HasLiveQuotationAsync(inquiry.Id));
            }

            InquiryMutationRules.EnsureBatchDeleteAllowed(entities.Select(GetStatus));

            foreach (var entity in entities)
            {
                entity.IsDeleted = true;
                entity.UpdatedAt = DateTime.Now;
            }
            await Db.SaveChangesAsync();
            if (transaction is not null) await transaction.CommitAsync();
            return Ok(ApiResponse<object>.Success(null, $"已删除 {entities.Count} 张询价单"));
        }
        catch (DbUpdateConcurrencyException)
        {
            await RollbackAsync(transaction);
            throw BusinessException.RuleConflict(InquiryMutationRules.StaleRowVersionText);
        }
        catch
        {
            await RollbackAsync(transaction);
            throw;
        }
    }

    /// <summary>导出（ERP-402：实时授权 + 范围先于读取下推，绝不返回范围外询价单）</summary>
    [HttpGet("export")]
    public async Task<IActionResult> Export([FromQuery] DateTime? start, [FromQuery] DateTime? end)
    {
        var scope = await InquiryAuthorizationRules.EnsureAuthorizedAsync(Db, CurrentUserId());
        var source = InquiryAuthorizationRules.ApplyScope(
            Db.Inquiries.AsNoTracking().Include(o => o.Details).Where(o => !o.IsDeleted), scope);
        if (start.HasValue) source = source.Where(o => o.InquiryDate >= start.Value);
        if (end.HasValue) source = source.Where(o => o.InquiryDate <= end.Value);
        var items = await source.OrderByDescending(o => o.Id).ToListAsync();
        return Ok(ApiResponse<List<Inquiry>>.Success(items));
    }
}
