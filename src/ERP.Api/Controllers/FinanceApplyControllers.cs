using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ERP.Api.Controllers;

/// <summary>
/// 定金申请单控制器（ERP-381 生命周期护栏）：列表 / 详情 / 创建 / 修改 / 提交 / 审核 / 取消 / 删除与读取都重新校验
/// 实时启用身份（账号存在、未删除且启用）、定金申请单（deposit-apply）菜单授权与当前权威客户数据范围；
/// 创建 / 修改校验真实可用客户、受支持币种、按币种精度取整后大于 0 的金额、大于 0 的汇率，并按 Id 校验可空来源
/// 销售订单（必须既有 / 未删除 / 未取消 / 客户一致 / 币种兼容；空来源保留历史「未关联来源」语义，绝不按文本猜测）。
/// <para>修改 / 提交 / 审核 / 取消 / 删除都在同一事务内先取来源销售订单行锁、再取定金申请单行锁（ERP-381 锁序：
/// 先上游销售订单行、后申请单行），再加载权威状态 / 字段并在锁内复核身份 + 菜单授权 + 既有与请求客户 / 来源范围、
/// 持久化商业字段与来源链接；失败整体回滚（状态、原始字段与审计不变）。取消销售订单侧由
/// <see cref="SalesOrderCancellationRules"/> 协同：仍有未删除且未取消的定金申请单指向该订单时拒绝取消。</para>
/// <para>本护栏不真的收款、不记账 / 生成凭证、不核销或移动资金，也不改写销售订单、收款单与分摊证据、库存与成本。</para>
/// </summary>
[Route("api/finance/deposit-applies")]
public class FinanceDepositApplyController : DocumentControllerBase<FinanceDepositApply>
{
    private readonly IDocumentNumberService _noService;

    public FinanceDepositApplyController(IErpDbContext db, IDocumentNumberService noService) : base(db)
    {
        _noService = noService;
    }

    /// <summary>分页：身份 / 菜单授权与客户数据范围在计数与取行之前生效（受限制账号看不到范围外申请单）。</summary>
    [HttpGet]
    public async Task<IActionResult> GetPaged([FromQuery] PageQuery query, [FromQuery] DocumentStatus? status)
    {
        query.Normalize();
        await FinanceDepositApplyLifecycleRules.EnsureMenuAuthorizedAsync(Db, CurrentUserId());
        var scope = await ResolveScopeAsync();
        var source = SalespersonDataScopeService.FilterByCustomer(
            Set.AsNoTracking().Where(o => !o.IsDeleted), scope, o => o.CustomerId);
        if (status.HasValue) source = source.Where(o => o.Status == status.Value);
        if (!string.IsNullOrWhiteSpace(query.Keyword)) source = source.Where(o => o.ApplyNo.Contains(query.Keyword));
        var total = await source.CountAsync();
        var items = await source.OrderByDescending(o => o.Id)
            .Skip((query.Page - 1) * query.PageSize).Take(query.PageSize).ToListAsync();
        return Ok(ApiResponse<PagedResult<FinanceDepositApply>>.Success(
            new PagedResult<FinanceDepositApply> { Items = items, Total = total, Page = query.Page, PageSize = query.PageSize }));
    }

    /// <summary>详情：按申请单权威客户做身份 / 菜单授权 / 客户数据范围复核，越界 fail closed。</summary>
    [HttpGet("{id:long}")]
    public async Task<IActionResult> GetById(long id)
    {
        var entity = await Set.AsNoTracking()
            .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
            ?? throw BusinessException.NotFound("定金申请单不存在");
        await FinanceDepositApplyLifecycleRules.EnsureAuthorizedAsync(Db, CurrentUserId(), entity.CustomerId);
        return Ok(ApiResponse<FinanceDepositApply>.Success(entity));
    }

    /// <summary>
    /// 创建：在生成申请单号之前先完成身份 / 菜单授权 / 客户数据范围与商业字段 / 来源链接校验（授权与校验失败绝不消耗单号）；
    /// 关系型后端在同一事务内先取来源销售订单行锁、锁内权威复核来源资格之后才生成单号并落库。
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] FinanceDepositApply entity)
    {
        var currency = FinanceDepositApplyLifecycleRules.NormalizeApplyCurrency(entity.Currency);
        var amount = FinanceDepositApplyLifecycleRules.NormalizeApplyAmount(entity.Amount, currency);
        var exchangeRate = FinanceDepositApplyLifecycleRules.NormalizeExchangeRate(entity.ExchangeRate);

        var customer = await Db.BaseCustomers.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == entity.CustomerId);
        FinanceDepositApplyLifecycleRules.EnsureCustomerAvailable(customer, entity.CustomerId);

        await FinanceDepositApplyLifecycleRules.EnsureAuthorizedAsync(Db, CurrentUserId(), entity.CustomerId);
        await FinanceDepositApplyLifecycleRules.ResolveSourceSalesOrderAsync(
            Db, entity.SalesOrderId, entity.CustomerId, currency);

        await using var transaction = FinanceDepositApplyLifecycleRules.IsRelationalProvider(Db)
            ? await Db.Database.BeginTransactionAsync()
            : null;
        try
        {
            await LockSourceOrderRowsAsync(entity.SalesOrderId);

            // 锁内权威复核来源订单资格（绝不把已被并发取消或篡改的来源写入新申请单）
            await FinanceDepositApplyLifecycleRules.ResolveSourceSalesOrderAsync(
                Db, entity.SalesOrderId, entity.CustomerId, currency);

            entity.Id = 0;
            entity.ApplyNo = await _noService.GenerateAsync(DocumentType.DepositApply);
            entity.Status = DocumentStatus.Pending;
            entity.Currency = Enum.Parse<Currency>(currency);
            entity.Amount = amount;
            entity.ExchangeRate = exchangeRate;
            entity.CreatedAt = DateTime.Now;
            Db.FinanceDepositApplies.Add(entity);
            await Db.SaveChangesAsync();
            if (transaction is not null) await transaction.CommitAsync();
            return Ok(ApiResponse<object>.Success(new { entity.Id, entity.ApplyNo }, "定金申请单创建成功"));
        }
        catch
        {
            if (transaction is not null) await transaction.RollbackAsync();
            throw;
        }
    }

    /// <summary>
    /// 修改：同一事务内先取既有 / 请求来源销售订单行锁（Id 升序），再取定金申请单行锁，然后加载权威状态 / 字段，
    /// 并在锁内复核实时身份 / 菜单授权、既有客户范围、请求客户范围与请求来源资格；仅待提交可改；
    /// 失败整体回滚（状态、原始字段与审计不变）。
    /// </summary>
    [HttpPut("{id:long}")]
    public async Task<IActionResult> Update(long id, [FromBody] FinanceDepositApply entity)
    {
        var storedSourceOrderId = await FinanceDepositApplyLifecycleRules.ReadSourceSalesOrderIdAsync(Db, id);
        await using var transaction = FinanceDepositApplyLifecycleRules.IsRelationalProvider(Db)
            ? await Db.Database.BeginTransactionAsync()
            : null;
        try
        {
            await LockSourceOrderRowsAsync(storedSourceOrderId, entity.SalesOrderId);
            await FinanceDepositApplyLifecycleRules.LockApplyRowAsync(Db, id);

            var existing = await Db.FinanceDepositApplies
                .FirstOrDefaultAsync(a => a.Id == id && !a.IsDeleted)
                ?? throw BusinessException.NotFound("定金申请单不存在");

            // 授权优先（fail closed）：锁内按实时身份 / 菜单授权 / 数据范围复核既有客户
            await EnsureApplyAuthorizedAsync(existing);
            if (GetStatus(existing) != DocumentStatus.Pending)
                throw BusinessException.RuleConflict("仅待提交状态的单据可修改");

            // 既有持久化客户 / 来源仍在权威范围内且资格有效（锁内复核，绝不基于陈旧来源放行）
            await FinanceDepositApplyLifecycleRules.EnsurePersistedApplyConsistentAsync(Db, existing);

            var currency = FinanceDepositApplyLifecycleRules.NormalizeApplyCurrency(entity.Currency);
            var amount = FinanceDepositApplyLifecycleRules.NormalizeApplyAmount(entity.Amount, currency);
            var exchangeRate = FinanceDepositApplyLifecycleRules.NormalizeExchangeRate(entity.ExchangeRate);
            var currencyEnum = Enum.Parse<Currency>(currency);

            var customer = await Db.BaseCustomers.AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == entity.CustomerId);
            FinanceDepositApplyLifecycleRules.EnsureCustomerAvailable(customer, entity.CustomerId);

            // 请求客户也必须在当前账号数据范围内（锁内按实时身份 / 菜单授权 / 数据范围复核）
            await FinanceDepositApplyLifecycleRules.EnsureAuthorizedAsync(Db, CurrentUserId(), entity.CustomerId);

            // 可空来源按 Id 精确解析：悬空 / 越界 / 不兼容一律拒绝（空来源保留历史语义）
            await FinanceDepositApplyLifecycleRules.ResolveSourceSalesOrderAsync(
                Db, entity.SalesOrderId, entity.CustomerId, currency);

            existing.ApplyDate = entity.ApplyDate;
            existing.SalesOrderId = entity.SalesOrderId;
            existing.CustomerId = entity.CustomerId;
            existing.Amount = amount;
            existing.Currency = currencyEnum;
            existing.ExchangeRate = exchangeRate;
            existing.BankAccount = entity.BankAccount;
            existing.Payee = entity.Payee;
            existing.Reason = entity.Reason;
            existing.Remark = entity.Remark;
            existing.UpdatedAt = DateTime.Now;
            await Db.SaveChangesAsync();

            if (transaction is not null) await transaction.CommitAsync();
            return Ok(ApiResponse<object>.Success(null, "定金申请单更新成功"));
        }
        catch
        {
            if (transaction is not null) await transaction.RollbackAsync();
            throw;
        }
    }


    /// <summary>提交：锁内复核实时身份 / 菜单授权 / 客户数据范围、持久化商业字段与来源链接；仅待提交 → 已提交。</summary>
    [HttpPost("{id:long}/submit")]
    public override Task<IActionResult> Submit(long id)
        => TransitionAsync(id, DocumentStatus.Pending, DocumentStatus.Submitted, "提交成功");

    /// <summary>审核：锁内复核实时身份 / 菜单授权 / 客户数据范围、持久化商业字段与来源链接；仅已提交 → 已审核。</summary>
    [HttpPost("{id:long}/approve")]
    public override Task<IActionResult> Approve(long id)
        => TransitionAsync(id, DocumentStatus.Submitted, DocumentStatus.Approved, "审核通过");

    /// <summary>取消：锁内复核授权并拒绝重复取消；失败整体回滚（状态与审计不变）。</summary>
    [HttpPost("{id:long}/cancel")]
    public override async Task<IActionResult> Cancel(long id)
    {
        var storedSourceOrderId = await FinanceDepositApplyLifecycleRules.ReadSourceSalesOrderIdAsync(Db, id);
        await using var transaction = FinanceDepositApplyLifecycleRules.IsRelationalProvider(Db)
            ? await Db.Database.BeginTransactionAsync()
            : null;
        try
        {
            await LockSourceOrderRowsAsync(storedSourceOrderId);
            var entity = await LockAndLoadApplyAsync(id);

            await EnsureApplyAuthorizedAsync(entity);
            if (GetStatus(entity) == DocumentStatus.Cancelled)
                throw BusinessException.RuleConflict("定金申请单已取消，不能重复取消");

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

    /// <summary>删除（软删除）：仅待提交可删；失败整体回滚（删除标记与审计不变）。</summary>
    [HttpDelete("{id:long}")]
    public override async Task<IActionResult> Delete(long id)
    {
        var storedSourceOrderId = await FinanceDepositApplyLifecycleRules.ReadSourceSalesOrderIdAsync(Db, id);
        await using var transaction = FinanceDepositApplyLifecycleRules.IsRelationalProvider(Db)
            ? await Db.Database.BeginTransactionAsync()
            : null;
        try
        {
            await LockSourceOrderRowsAsync(storedSourceOrderId);
            var entity = await LockAndLoadApplyAsync(id);

            await EnsureApplyAuthorizedAsync(entity);
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

    /// <summary>提交 / 审核共用：同一事务内先取来源销售订单行锁、再取申请单行锁，再加载权威状态并在锁内复核身份 / 菜单 / 客户范围 / 持久化商业字段与来源。</summary>
    private async Task<IActionResult> TransitionAsync(
        long id, DocumentStatus from, DocumentStatus to, string message)
    {
        var storedSourceOrderId = await FinanceDepositApplyLifecycleRules.ReadSourceSalesOrderIdAsync(Db, id);
        await using var transaction = FinanceDepositApplyLifecycleRules.IsRelationalProvider(Db)
            ? await Db.Database.BeginTransactionAsync()
            : null;
        try
        {
            await LockSourceOrderRowsAsync(storedSourceOrderId);
            var entity = await LockAndLoadApplyAsync(id);

            await EnsureApplyAuthorizedAsync(entity);
            if (GetStatus(entity) != from)
                throw BusinessException.RuleConflict("当前状态不允许该操作");
            await FinanceDepositApplyLifecycleRules.EnsurePersistedApplyConsistentAsync(Db, entity);

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

    /// <summary>
    /// 按 ERP-381 锁序对来源销售订单行加更新锁（UPDLOCK, HOLDLOCK）：多个来源订单按 Id 升序获取，
    /// 与销售订单取消 / 出库审核共用同一把上游订单行锁；非关系型提供程序（内存库）无行锁语义，跳过即可。
    /// </summary>
    private async Task LockSourceOrderRowsAsync(params long?[] salesOrderIds)
    {
        if (!Db.Database.IsRelational()) return;
        foreach (var orderId in FinanceDepositApplyLifecycleRules.MergeSourceOrderLockIds(salesOrderIds))
            await Db.Database.SqlQueryRaw<long>(PreLoadingSalesOrderLinkRules.LockSalesOrderRowSql, orderId).ToListAsync();
    }

    /// <summary>先取定金申请单行锁，再加载锁内权威申请单（未找到 / 已删除 → 不存在）。</summary>
    private async Task<FinanceDepositApply> LockAndLoadApplyAsync(long id)
    {
        await FinanceDepositApplyLifecycleRules.LockApplyRowAsync(Db, id);
        return await Db.FinanceDepositApplies
                   .FirstOrDefaultAsync(a => a.Id == id && !a.IsDeleted)
               ?? throw BusinessException.NotFound("定金申请单不存在");
    }

    /// <summary>按申请单权威客户做身份 / 菜单 / 客户数据范围复核（修改 / 提交 / 审核 / 取消 / 删除共用）。</summary>
    private Task EnsureApplyAuthorizedAsync(FinanceDepositApply entity)
        => FinanceDepositApplyLifecycleRules.EnsureAuthorizedAsync(Db, CurrentUserId(), entity.CustomerId);
}

/// <summary>
/// 货款申请单控制器（ERP-380 生命周期护栏）：列表 / 详情 / 创建 / 修改 / 提交 / 审核 / 取消 / 删除与读取都重新校验
/// 实时启用身份（账号存在、未删除且启用）、货款申请单（payment-apply）菜单授权与当前权威客户数据范围；创建 / 修改
/// 校验真实可用客户、受支持币种、按币种精度取整后大于 0 的金额、大于 0 的汇率，并按 Id 校验可空来源销售订单
/// （必须既有 / 未删除 / 未取消 / 客户一致 / 币种兼容；空来源保留历史「未关联来源」语义，绝不按文本猜测）。
/// <para>存在未删除且未取消的引用付款单时拒绝取消 / 删除或修改客户 / 来源 / 币种 / 金额 / 汇率（显式取消的付款单释放
/// 限制但历史与审计原样保留）。修改 / 提交 / 审核 / 取消 / 删除五个动作都在同一事务内先取申请单行锁
/// （ERP-380 锁序：先来源申请单行、后付款单行），再加载权威状态 / 字段并在锁内复核实时身份 / 菜单授权 / 客户数据范围 /
/// 引用付款护栏与允许的状态流转；任一步失败整体回滚，状态、原始字段与审计不变。</para>
/// </summary>
[Route("api/finance/payment-applies")]
public class FinancePaymentApplyController : DocumentControllerBase<FinancePaymentApply>
{
    private readonly IDocumentNumberService _noService;

    public FinancePaymentApplyController(IErpDbContext db, IDocumentNumberService noService) : base(db)
    {
        _noService = noService;
    }

    /// <summary>分页：身份 / 菜单授权与客户数据范围在计数与取行之前生效（受限制账号看不到范围外申请单）。</summary>
    [HttpGet]
    public async Task<IActionResult> GetPaged([FromQuery] PageQuery query, [FromQuery] DocumentStatus? status)
    {
        query.Normalize();
        await FinancePaymentApplyLifecycleRules.EnsureMenuAuthorizedAsync(Db, CurrentUserId());
        var scope = await ResolveScopeAsync();
        var source = SalespersonDataScopeService.FilterByCustomer(
            Set.AsNoTracking().Where(o => !o.IsDeleted), scope, o => o.CustomerId);
        if (status.HasValue) source = source.Where(o => o.Status == status.Value);
        if (!string.IsNullOrWhiteSpace(query.Keyword)) source = source.Where(o => o.ApplyNo.Contains(query.Keyword));
        var total = await source.CountAsync();
        var items = await source.OrderByDescending(o => o.Id)
            .Skip((query.Page - 1) * query.PageSize).Take(query.PageSize).ToListAsync();
        return Ok(ApiResponse<PagedResult<FinancePaymentApply>>.Success(
            new PagedResult<FinancePaymentApply> { Items = items, Total = total, Page = query.Page, PageSize = query.PageSize }));
    }

    /// <summary>详情：按申请单权威客户做身份 / 菜单授权 / 客户数据范围复核，越界 fail closed。</summary>
    [HttpGet("{id:long}")]
    public async Task<IActionResult> GetById(long id)
    {
        var entity = await Set.AsNoTracking()
            .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
            ?? throw BusinessException.NotFound("货款申请单不存在");
        await FinancePaymentApplyLifecycleRules.EnsureAuthorizedAsync(Db, CurrentUserId(), entity.CustomerId);
        return Ok(ApiResponse<FinancePaymentApply>.Success(entity));
    }

    /// <summary>
    /// 创建：在生成申请单号之前先完成身份 / 菜单授权 / 客户数据范围与商业字段 / 来源链接校验（授权与校验失败绝不消耗单号）。
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] FinancePaymentApply entity)
    {
        var currency = FinancePaymentApplyLifecycleRules.NormalizeApplyCurrency(entity.Currency);
        var amount = FinancePaymentApplyLifecycleRules.NormalizeApplyAmount(entity.Amount, currency);
        var exchangeRate = FinancePaymentApplyLifecycleRules.NormalizeExchangeRate(entity.ExchangeRate);

        var customer = await Db.BaseCustomers.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == entity.CustomerId);
        FinancePaymentApplyLifecycleRules.EnsureCustomerAvailable(customer, entity.CustomerId);

        await FinancePaymentApplyLifecycleRules.EnsureAuthorizedAsync(Db, CurrentUserId(), entity.CustomerId);

        await FinancePaymentApplyLifecycleRules.ResolveSourceSalesOrderAsync(
            Db, entity.SalesOrderId, entity.CustomerId, currency);

        entity.Id = 0;
        entity.ApplyNo = await _noService.GenerateAsync(DocumentType.PaymentApply);
        entity.Status = DocumentStatus.Pending;
        entity.Currency = Enum.Parse<Currency>(currency);
        entity.Amount = amount;
        entity.ExchangeRate = exchangeRate;
        entity.CreatedAt = DateTime.Now;
        Db.FinancePaymentApplies.Add(entity);
        await Db.SaveChangesAsync();
        return Ok(ApiResponse<object>.Success(new { entity.Id, entity.ApplyNo }, "货款申请单创建成功"));
    }

    /// <summary>
    /// 修改：同一事务内先取申请单行锁，再加载权威状态 / 字段并在锁内复核实时身份 / 菜单授权与既有 + 请求客户数据范围；
    /// 仅待提交可改；修改客户 / 来源销售订单 / 币种 / 金额 / 汇率前拒绝存在未删除且未取消的引用付款单；
    /// 失败整体回滚（状态、原始字段与审计不变）。
    /// </summary>
    [HttpPut("{id:long}")]
    public async Task<IActionResult> Update(long id, [FromBody] FinancePaymentApply entity)
    {
        await using var transaction = FinancePaymentApplyLifecycleRules.IsRelationalProvider(Db)
            ? await Db.Database.BeginTransactionAsync()
            : null;
        try
        {
            await FinancePaymentApplyLifecycleRules.LockApplyRowAsync(Db, id);

            var existing = await Db.FinancePaymentApplies
                .FirstOrDefaultAsync(a => a.Id == id && !a.IsDeleted)
                ?? throw BusinessException.NotFound("货款申请单不存在");

            // 授权优先（fail closed）：锁内按实时身份 / 菜单授权 / 数据范围复核既有客户
            await EnsureApplyAuthorizedAsync(existing);
            if (GetStatus(existing) != DocumentStatus.Pending)
                throw BusinessException.RuleConflict("仅待提交状态的单据可修改");

            var currency = FinancePaymentApplyLifecycleRules.NormalizeApplyCurrency(entity.Currency);
            var amount = FinancePaymentApplyLifecycleRules.NormalizeApplyAmount(entity.Amount, currency);
            var exchangeRate = FinancePaymentApplyLifecycleRules.NormalizeExchangeRate(entity.ExchangeRate);
            var currencyEnum = Enum.Parse<Currency>(currency);

            var customer = await Db.BaseCustomers.AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == entity.CustomerId);
            FinancePaymentApplyLifecycleRules.EnsureCustomerAvailable(customer, entity.CustomerId);

            // 请求客户也必须在当前账号数据范围内（锁内按实时身份 / 菜单授权 / 数据范围复核）
            await FinancePaymentApplyLifecycleRules.EnsureAuthorizedAsync(Db, CurrentUserId(), entity.CustomerId);

            // 可空来源按 Id 精确解析：悬空 / 越界 / 不兼容一律拒绝（空来源保留历史语义）
            await FinancePaymentApplyLifecycleRules.ResolveSourceSalesOrderAsync(
                Db, entity.SalesOrderId, entity.CustomerId, currency);

            var existingCurrency = CurrencyAmountRules.NormalizeCurrency(existing.Currency.ToString());
            var changesCommercial = existing.CustomerId != entity.CustomerId
                || (existing.SalesOrderId ?? 0L) != (entity.SalesOrderId ?? 0L)
                || existing.Currency != currencyEnum
                || amount != FinancePaymentApplyLifecycleRules.AuthoritativeApplyAmount(existing.Amount, existingCurrency)
                || exchangeRate != FinancePaymentApplyLifecycleRules.AuthoritativeExchangeRate(existing.ExchangeRate);
            if (changesCommercial)
                await FinancePaymentApplyLifecycleRules.EnsureNoReferencingPaymentsAsync(
                    Db, id, "修改客户 / 来源 / 币种 / 金额 / 汇率");

            existing.ApplyDate = entity.ApplyDate;
            existing.SalesOrderId = entity.SalesOrderId;
            existing.CustomerId = entity.CustomerId;
            existing.Amount = amount;
            existing.Currency = currencyEnum;
            existing.ExchangeRate = exchangeRate;
            existing.BankAccount = entity.BankAccount;
            existing.Payee = entity.Payee;
            existing.Reason = entity.Reason;
            existing.Remark = entity.Remark;
            existing.UpdatedAt = DateTime.Now;
            await Db.SaveChangesAsync();

            if (transaction is not null) await transaction.CommitAsync();
            return Ok(ApiResponse<object>.Success(null, "货款申请单更新成功"));
        }
        catch
        {
            if (transaction is not null) await transaction.RollbackAsync();
            throw;
        }
    }

    /// <summary>提交：锁内复核实时身份 / 菜单授权 / 客户数据范围、持久化商业字段与来源链接；仅待提交 → 已提交。</summary>
    [HttpPost("{id:long}/submit")]
    public override Task<IActionResult> Submit(long id)
        => TransitionAsync(id, DocumentStatus.Pending, DocumentStatus.Submitted, "提交成功");

    /// <summary>审核：锁内复核实时身份 / 菜单授权 / 客户数据范围、持久化商业字段与来源链接；仅已提交 → 已审核。</summary>
    [HttpPost("{id:long}/approve")]
    public override Task<IActionResult> Approve(long id)
        => TransitionAsync(id, DocumentStatus.Submitted, DocumentStatus.Approved, "审核通过");

    /// <summary>取消：锁内复核授权并拒绝存在未删除且未取消引用付款单的取消；重复取消被拒绝，失败整体回滚。</summary>
    [HttpPost("{id:long}/cancel")]
    public override async Task<IActionResult> Cancel(long id)
    {
        await using var transaction = FinancePaymentApplyLifecycleRules.IsRelationalProvider(Db)
            ? await Db.Database.BeginTransactionAsync()
            : null;
        try
        {
            var entity = await LockAndLoadApplyAsync(id);

            await EnsureApplyAuthorizedAsync(entity);
            if (GetStatus(entity) == DocumentStatus.Cancelled)
                throw BusinessException.RuleConflict("货款申请单已取消，不能重复取消");
            await FinancePaymentApplyLifecycleRules.EnsureNoReferencingPaymentsAsync(Db, id, "取消");

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

    /// <summary>删除（软删除）：仅待提交可删，且拒绝存在未删除且未取消引用付款单的删除；失败整体回滚。</summary>
    [HttpDelete("{id:long}")]
    public override async Task<IActionResult> Delete(long id)
    {
        await using var transaction = FinancePaymentApplyLifecycleRules.IsRelationalProvider(Db)
            ? await Db.Database.BeginTransactionAsync()
            : null;
        try
        {
            var entity = await LockAndLoadApplyAsync(id);

            await EnsureApplyAuthorizedAsync(entity);
            if (GetStatus(entity) != DocumentStatus.Pending)
                throw BusinessException.RuleConflict("仅待提交状态的单据可删除");
            await FinancePaymentApplyLifecycleRules.EnsureNoReferencingPaymentsAsync(Db, id, "删除");

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

    /// <summary>提交 / 审核共用：同一事务内先取申请单行锁，再加载权威状态并在锁内复核身份 / 菜单 / 客户范围 / 持久化商业字段。</summary>
    private async Task<IActionResult> TransitionAsync(
        long id, DocumentStatus from, DocumentStatus to, string message)
    {
        await using var transaction = FinancePaymentApplyLifecycleRules.IsRelationalProvider(Db)
            ? await Db.Database.BeginTransactionAsync()
            : null;
        try
        {
            var entity = await LockAndLoadApplyAsync(id);

            await EnsureApplyAuthorizedAsync(entity);
            if (GetStatus(entity) != from)
                throw BusinessException.RuleConflict("当前状态不允许该操作");
            await FinancePaymentApplyLifecycleRules.EnsurePersistedApplyConsistentAsync(Db, entity);

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

    /// <summary>先取申请单行锁（ERP-380 锁序：来源申请单行先于付款单行），再加载锁内权威申请单（未找到 / 已删除 → 不存在）。</summary>
    private async Task<FinancePaymentApply> LockAndLoadApplyAsync(long id)
    {
        await FinancePaymentApplyLifecycleRules.LockApplyRowAsync(Db, id);
        return await Db.FinancePaymentApplies
                   .FirstOrDefaultAsync(a => a.Id == id && !a.IsDeleted)
               ?? throw BusinessException.NotFound("货款申请单不存在");
    }

    /// <summary>按申请单权威客户做身份 / 菜单 / 客户数据范围复核（修改 / 提交 / 审核 / 取消 / 删除共用）。</summary>
    private Task EnsureApplyAuthorizedAsync(FinancePaymentApply entity)
        => FinancePaymentApplyLifecycleRules.EnsureAuthorizedAsync(Db, CurrentUserId(), entity.CustomerId);
}
