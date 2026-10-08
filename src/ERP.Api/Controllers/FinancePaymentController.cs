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

    /// <summary>
    /// 创建（ERP-380 锁序）：先按来源货款申请单取申请单行锁、再在锁内重新解析来源并校验授权，最后生成付款单号并落库；
    /// 与来源申请单的商业改动 / 取消 / 删除串行化，失败整体回滚（不产生半成品付款单、不改写申请单商业字段）。
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] FinancePayment entity)
    {
        var currency = SupplierPaymentLifecycleRules.NormalizePaymentCurrency(entity.Currency);
        var amount = SupplierPaymentLifecycleRules.NormalizePaymentAmount(entity.Amount, currency);

        var supplier = await Db.BaseSuppliers.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == entity.SupplierId);
        SupplierPaymentLifecycleRules.EnsureSupplierAvailable(supplier, entity.SupplierId);

        await using var transaction = SupplierPaymentLifecycleRules.IsRelationalProvider(Db)
            ? await Db.Database.BeginTransactionAsync()
            : null;
        try
        {
            // ERP-380 锁序：先来源申请单行，后付款单行（此处付款单尚未创建，只需来源申请单行锁）
            await LockSourceApplyAsync(entity.PaymentApplyId);

            // 锁内重新解析来源：申请单若已被并发取消 / 删除即拒绝（绝不用陈旧来源创建付款单）
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

            if (transaction is not null) await transaction.CommitAsync();
            return Ok(ApiResponse<object>.Success(new { entity.Id, entity.PaymentNo }, "付款单创建成功"));
        }
        catch
        {
            if (transaction is not null) await transaction.RollbackAsync();
            throw;
        }
    }

    /// <summary>
    /// 修改（ERP-380 锁序：先来源申请单行、后付款单行）：请求来源与既有来源的申请单行都先加锁，再取付款单行锁并在锁内
    /// 复核来源链接未被并发改写；仅待提交可改，改供应商 / 币种 / 金额前先校验有效引用证据；失败整体回滚。
    /// </summary>
    [HttpPut("{id:long}")]
    public async Task<IActionResult> Update(long id, [FromBody] FinancePayment entity)
    {
        // 与付款引用证据写入互斥，并与来源申请单的商业改动 / 取消串行化（ERP-380）
        await using var transaction = SupplierPaymentLifecycleRules.IsRelationalProvider(Db)
            ? await Db.Database.BeginTransactionAsync()
            : null;
        try
        {
            // 请求来源（可能变更）也遵循同一锁序：先申请单行、后付款单行
            await LockSourceApplyAsync(entity.PaymentApplyId);

            var existing = await LockPaymentWithSourceAsync(id);

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

    /// <summary>
    /// 提交（ERP-379）：同一事务内先取付款单行锁，再读权威状态，并在锁内重新校验实时启用身份 / 付款单菜单授权 /
    /// 权威来源客户数据范围与允许的状态流转，再用既有规则复核持久化金额 / 币种 / 来源；仅待提交 → 已提交，
    /// 失败整体回滚（状态、字段、删除标记与审计不变）。
    /// </summary>
    [HttpPost("{id:long}/submit")]
    public override async Task<IActionResult> Submit(long id)
    {
        await using var transaction = SupplierPaymentLifecycleRules.IsRelationalProvider(Db)
            ? await Db.Database.BeginTransactionAsync()
            : null;
        try
        {
            var entity = await LockPaymentWithSourceAsync(id);

            await EnsurePaymentAuthorizedAsync(entity);
            if (GetStatus(entity) != DocumentStatus.Pending)
                throw BusinessException.RuleConflict("当前状态不允许该操作");
            await SupplierPaymentLifecycleRules.EnsurePersistedPaymentConsistentAsync(Db, entity);

            SetStatus(entity, DocumentStatus.Submitted);
            await Db.SaveChangesAsync();

            if (transaction is not null) await transaction.CommitAsync();
            return Ok(ApiResponse<object>.Success(null, "提交成功"));
        }
        catch
        {
            if (transaction is not null) await transaction.RollbackAsync();
            throw;
        }
    }

    /// <summary>
    /// 审核（ERP-379）：同一事务内先取付款单行锁，再读权威状态，并在锁内重新校验实时启用身份 / 付款单菜单授权 /
    /// 权威来源客户数据范围与允许的状态流转，再用既有规则复核持久化金额 / 币种 / 来源；仅已提交 → 已审核，
    /// 与并发取消互斥且失败整体回滚。
    /// </summary>
    [HttpPost("{id:long}/approve")]
    public override async Task<IActionResult> Approve(long id)
    {
        await using var transaction = SupplierPaymentLifecycleRules.IsRelationalProvider(Db)
            ? await Db.Database.BeginTransactionAsync()
            : null;
        try
        {
            var entity = await LockPaymentWithSourceAsync(id);

            await EnsurePaymentAuthorizedAsync(entity);
            if (GetStatus(entity) != DocumentStatus.Submitted)
                throw BusinessException.RuleConflict("当前状态不允许该操作");
            await SupplierPaymentLifecycleRules.EnsurePersistedPaymentConsistentAsync(Db, entity);

            SetStatus(entity, DocumentStatus.Approved);
            await Db.SaveChangesAsync();

            if (transaction is not null) await transaction.CommitAsync();
            return Ok(ApiResponse<object>.Success(null, "审核通过"));
        }
        catch
        {
            if (transaction is not null) await transaction.RollbackAsync();
            throw;
        }
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
            var entity = await LockPaymentWithSourceAsync(id);

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
            var entity = await LockPaymentWithSourceAsync(id);

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

    /// <summary>
    /// ERP-380 统一锁序（先来源货款申请单行、后付款单行）：先按持久化来源申请单 Id 取申请单行锁，再取付款单行锁；
    /// 锁定后在锁内复核来源链接未被并发改写（否则按业务冲突拒绝，绝不使用陈旧来源），并返回锁内重载的权威付款单
    /// （跟踪状态，供后续状态流转写入）。未关联来源的历史付款场景保持既有语义（无申请单行锁可加）。
    /// </summary>
    private async Task<FinancePayment> LockPaymentWithSourceAsync(long id)
    {
        var sourceApplyId = await FinancePaymentApplyLifecycleRules.ReadPaymentApplyIdAsync(Db, id);
        await LockSourceApplyAsync(sourceApplyId);

        await SupplierPaymentLifecycleRules.LockPaymentRowAsync(Db, id);

        var entity = await Db.FinancePayments
            .FirstOrDefaultAsync(p => p.Id == id && !p.IsDeleted)
            ?? throw BusinessException.NotFound("付款单不存在");

        if ((entity.PaymentApplyId ?? 0L) != (sourceApplyId ?? 0L))
            throw BusinessException.RuleConflict(
                "付款单的来源货款申请单在并发中已被变更，本次操作未生效：请刷新后重试（原始付款单与引用证据均未改变）");
        return entity;
    }

    /// <summary>按 ERP-380 锁序对来源货款申请单行加锁（无来源 / 非法 Id 时跳过；申请单不存在时锁定为空操作）。</summary>
    private Task LockSourceApplyAsync(long? paymentApplyId)
        => paymentApplyId is > 0
            ? FinancePaymentApplyLifecycleRules.LockApplyRowAsync(Db, paymentApplyId.Value)
            : Task.CompletedTask;
}
