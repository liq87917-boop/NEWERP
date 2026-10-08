using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ERP.Api.Controllers;

/// <summary>
/// 付款单控制器
/// </summary>
[Route("api/finance/payments")]
public class FinancePaymentController : DocumentControllerBase<FinancePayment>
{
    private readonly IDocumentNumberService _noService;

    public FinancePaymentController(IErpDbContext db, IDocumentNumberService noService) : base(db)
    {
        _noService = noService;
    }

    [HttpGet]
    public async Task<IActionResult> GetPaged([FromQuery] PageQuery query, [FromQuery] DocumentStatus? status)
    {
        query.Normalize();
        await SupplierPaymentLifecycleRules.EnsureMenuAuthorizedAsync(Db, CurrentUserId());
        var scope = await ResolveScopeAsync();
        var source = await SupplierPaymentLifecycleRules.ApplyCustomerScopeAsync(
            Db, Set.AsNoTracking().Where(o => !o.IsDeleted), scope);
        if (status.HasValue) source = source.Where(o => o.Status == status.Value);
        if (!string.IsNullOrWhiteSpace(query.Keyword)) source = source.Where(o => o.PaymentNo.Contains(query.Keyword));
        var total = await source.CountAsync();
        var items = await source.OrderByDescending(o => o.Id)
            .Skip((query.Page - 1) * query.PageSize).Take(query.PageSize).ToListAsync();
        return Ok(ApiResponse<PagedResult<FinancePayment>>.Success(
            new PagedResult<FinancePayment> { Items = items, Total = total, Page = query.Page, PageSize = query.PageSize }));
    }

    [HttpGet("{id:long}")]
    public async Task<IActionResult> GetById(long id)
    {
        var entity = await Set.AsNoTracking()
            .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
            ?? throw BusinessException.NotFound("付款单不存在");
        var customerId = await SupplierPaymentLifecycleRules.ResolvePaymentCustomerAsync(Db, entity);
        await SupplierPaymentLifecycleRules.EnsureAuthorizedAsync(Db, CurrentUserId(), customerId);
        return Ok(ApiResponse<FinancePayment>.Success(entity));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] FinancePayment entity)
    {
        var currency = SupplierPaymentLifecycleRules.NormalizePaymentCurrency(entity.Currency);
        var amount = SupplierPaymentLifecycleRules.NormalizePaymentAmount(entity.Amount, currency);

        var supplier = await Db.BaseSuppliers.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == entity.SupplierId);
        SupplierPaymentLifecycleRules.EnsureSupplierAvailable(supplier, entity.SupplierId);

        var apply = await SupplierPaymentLifecycleRules.ResolvePaymentApplyAsync(
            Db, entity.PaymentApplyId, currency, amount);
        await SupplierPaymentLifecycleRules.EnsureAuthorizedAsync(Db, CurrentUserId(), apply?.CustomerId);

        entity.Id = 0;
        entity.PaymentNo = await _noService.GenerateAsync(DocumentType.Payment);
        entity.Status = DocumentStatus.Pending;
        entity.Currency = Enum.Parse<Currency>(currency);
        entity.Amount = amount;
        entity.CreatedAt = DateTime.Now;
        Db.FinancePayments.Add(entity);
        await Db.SaveChangesAsync();
        return Ok(ApiResponse<object>.Success(new { entity.Id, entity.PaymentNo }, "付款单创建成功"));
    }

    [HttpPut("{id:long}")]
    public async Task<IActionResult> Update(long id, [FromBody] FinancePayment entity)
    {
        // 与付款引用证据写入互斥：在同一事务内先对付款单行加排它行锁，再读权威金额与状态。
        await using var transaction = SupplierPaymentLifecycleRules.IsRelationalProvider(Db)
            ? await Db.Database.BeginTransactionAsync()
            : null;
        try
        {
            await SupplierPaymentLifecycleRules.LockPaymentRowAsync(Db, id);

            var existing = await Db.FinancePayments
                .FirstOrDefaultAsync(p => p.Id == id && !p.IsDeleted)
                ?? throw BusinessException.NotFound("付款单不存在");

            if (GetStatus(existing) != DocumentStatus.Pending)
                throw BusinessException.RuleConflict("仅待提交状态的单据可修改");

            var currency = SupplierPaymentLifecycleRules.NormalizePaymentCurrency(entity.Currency);
            var amount = SupplierPaymentLifecycleRules.NormalizePaymentAmount(entity.Amount, currency);

            var supplier = await Db.BaseSuppliers.AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == entity.SupplierId);
            SupplierPaymentLifecycleRules.EnsureSupplierAvailable(supplier, entity.SupplierId);

            var requestedApply = await SupplierPaymentLifecycleRules.ResolvePaymentApplyAsync(
                Db, entity.PaymentApplyId, currency, amount);

            // 既有来源客户与请求来源客户都必须落在当前账号数据范围内
            var existingCustomerId = await SupplierPaymentLifecycleRules.ResolvePaymentCustomerAsync(Db, existing);
            await SupplierPaymentLifecycleRules.EnsureAuthorizedAsync(Db, CurrentUserId(), existingCustomerId);
            await SupplierPaymentLifecycleRules.EnsureAuthorizedAsync(Db, CurrentUserId(), requestedApply?.CustomerId);

            // 引用证据保护：修改供应商 / 币种 / 金额会破坏既有证据，先拒绝（除非已显式作废释放）
            var existingCurrency = CurrencyAmountRules.NormalizeCurrency(existing.Currency.ToString());
            var changesEvidence = existing.SupplierId != entity.SupplierId
                || existing.Currency != entity.Currency
                || amount != SupplierPaymentLifecycleRules.AuthoritativePaymentAmount(existing.Amount, existingCurrency);
            if (changesEvidence)
                await SupplierPaymentLifecycleRules.EnsureNoActiveAllocationAsync(Db, id, "修改供应商 / 币种 / 金额");

            existing.PaymentDate = entity.PaymentDate;
            existing.SupplierId = entity.SupplierId;
            existing.PaymentApplyId = entity.PaymentApplyId;
            existing.Amount = amount;
            existing.Currency = Enum.Parse<Currency>(currency);
            existing.PaymentMethod = entity.PaymentMethod;
            existing.BankAccount = entity.BankAccount;
            existing.Remark = entity.Remark;
            existing.UpdatedAt = DateTime.Now;
            await Db.SaveChangesAsync();

            if (transaction is not null) await transaction.CommitAsync();
            return Ok(ApiResponse<object>.Success(null, "付款单更新成功"));
        }
        catch
        {
            if (transaction is not null) await transaction.RollbackAsync();
            throw;
        }
    }

    /// <summary>提交：重新校验身份 / 菜单 / 客户范围后，仅待提交 → 已提交。</summary>
    [HttpPost("{id:long}/submit")]
    public override async Task<IActionResult> Submit(long id)
    {
        var entity = await GetOrThrowAsync(id, "付款单不存在");
        await EnsurePaymentAuthorizedAsync(entity);
        if (GetStatus(entity) != DocumentStatus.Pending)
            throw BusinessException.RuleConflict("当前状态不允许该操作");
        SetStatus(entity, DocumentStatus.Submitted);
        await Db.SaveChangesAsync();
        return Ok(ApiResponse<object>.Success(null, "提交成功"));
    }

    /// <summary>审核：重新校验身份 / 菜单 / 客户范围后，仅已提交 → 已审核。</summary>
    [HttpPost("{id:long}/approve")]
    public override async Task<IActionResult> Approve(long id)
    {
        var entity = await GetOrThrowAsync(id, "付款单不存在");
        await EnsurePaymentAuthorizedAsync(entity);
        if (GetStatus(entity) != DocumentStatus.Submitted)
            throw BusinessException.RuleConflict("当前状态不允许该操作");
        SetStatus(entity, DocumentStatus.Approved);
        await Db.SaveChangesAsync();
        return Ok(ApiResponse<object>.Success(null, "审核通过"));
    }

    /// <summary>取消：重新校验身份 / 菜单 / 客户范围，并拒绝存在有效付款引用证据的取消；重复取消被拒绝。</summary>
    [HttpPost("{id:long}/cancel")]
    public override async Task<IActionResult> Cancel(long id)
    {
        await using var transaction = SupplierPaymentLifecycleRules.IsRelationalProvider(Db)
            ? await Db.Database.BeginTransactionAsync()
            : null;
        try
        {
            await SupplierPaymentLifecycleRules.LockPaymentRowAsync(Db, id);

            var entity = await Db.FinancePayments
                .FirstOrDefaultAsync(p => p.Id == id && !p.IsDeleted)
                ?? throw BusinessException.NotFound("付款单不存在");

            await EnsurePaymentAuthorizedAsync(entity);
            if (GetStatus(entity) == DocumentStatus.Cancelled)
                throw BusinessException.RuleConflict("付款单已取消，不能重复取消");
            await SupplierPaymentLifecycleRules.EnsureNoActiveAllocationAsync(Db, id, "取消");

            SetStatus(entity, DocumentStatus.Cancelled);
            await Db.SaveChangesAsync();
            if (transaction is not null) await transaction.CommitAsync();
            return Ok(ApiResponse<object>.Success(null, "已取消"));
        }
        catch
        {
            if (transaction is not null) await transaction.RollbackAsync();
            throw;
        }
    }

    /// <summary>删除（软删除）：重新校验身份 / 菜单 / 客户范围，仅待提交可删，并拒绝存在有效付款引用证据的删除。</summary>
    [HttpDelete("{id:long}")]
    public override async Task<IActionResult> Delete(long id)
    {
        await using var transaction = SupplierPaymentLifecycleRules.IsRelationalProvider(Db)
            ? await Db.Database.BeginTransactionAsync()
            : null;
        try
        {
            await SupplierPaymentLifecycleRules.LockPaymentRowAsync(Db, id);

            var entity = await Db.FinancePayments
                .FirstOrDefaultAsync(p => p.Id == id && !p.IsDeleted)
                ?? throw BusinessException.NotFound("付款单不存在");

            await EnsurePaymentAuthorizedAsync(entity);
            if (GetStatus(entity) != DocumentStatus.Pending)
                throw BusinessException.RuleConflict("仅待提交状态的单据可删除");
            await SupplierPaymentLifecycleRules.EnsureNoActiveAllocationAsync(Db, id, "删除");

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

    /// <summary>按付款单派生的客户做身份 / 菜单 / 客户数据范围复核（提交 / 审核 / 取消 / 删除共用）。</summary>
    private async Task EnsurePaymentAuthorizedAsync(FinancePayment entity)
    {
        var customerId = await SupplierPaymentLifecycleRules.ResolvePaymentCustomerAsync(Db, entity);
        await SupplierPaymentLifecycleRules.EnsureAuthorizedAsync(Db, CurrentUserId(), customerId);
    }
}
