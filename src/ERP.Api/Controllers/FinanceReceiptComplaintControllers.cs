using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ERP.Api.Controllers;

/// <summary>
/// 收款单控制器（ERP-349 生命周期护栏 + ERP-378 同单串行化）：创建 / 修改 / 提交 / 审核 / 取消 / 删除与读取都重新校验
/// 实时启用身份（账号存在、未删除且启用）、收款单（receipt）菜单授权与客户数据范围；创建与修改校验真实可用客户、
/// 受支持币种与按币种精度取整后大于 0 的金额；存在有效收款分摊证据时拒绝取消 / 删除或修改客户 / 币种 / 金额
/// （先走既有作废服务释放）。
/// <para>ERP-378：修改 / 提交 / 审核 / 取消 / 删除五个动作都在同一事务内先取收款单行锁
/// （<see cref="CustomerReceiptLifecycleRules.LockReceiptRowAsync"/>，与两套分摊证据写入 / 作废同一锁序：先收款单行、
/// 后分摊行），再加载权威状态 / 客户 / 币种 / 金额并在锁内复核身份 / 菜单 / 客户范围与有效证据；任一步失败整体回滚，
/// 原状态、原始字段与审计保持不变。</para>
/// </summary>
[Route("api/finance/receipts")]
public class FinanceReceiptController : DocumentControllerBase<FinanceReceipt>
{
    private readonly IDocumentNumberService _noService;

    public FinanceReceiptController(IErpDbContext db, IDocumentNumberService noService) : base(db)
    {
        _noService = noService;
    }

    [HttpGet]
    public async Task<IActionResult> GetPaged([FromQuery] PageQuery query, [FromQuery] DocumentStatus? status)
    {
        query.Normalize();
        await CustomerReceiptLifecycleRules.EnsureMenuAuthorizedAsync(Db, CurrentUserId());
        var scope = await ResolveScopeAsync();
        var source = SalespersonDataScopeService.FilterByCustomer(
            Set.AsNoTracking().Where(o => !o.IsDeleted), scope, o => o.CustomerId);
        if (status.HasValue) source = source.Where(o => o.Status == status.Value);
        if (!string.IsNullOrWhiteSpace(query.Keyword)) source = source.Where(o => o.ReceiptNo.Contains(query.Keyword));
        var total = await source.CountAsync();
        var items = await source.OrderByDescending(o => o.Id)
            .Skip((query.Page - 1) * query.PageSize).Take(query.PageSize).ToListAsync();
        return Ok(ApiResponse<PagedResult<FinanceReceipt>>.Success(
            new PagedResult<FinanceReceipt> { Items = items, Total = total, Page = query.Page, PageSize = query.PageSize }));
    }

    [HttpGet("{id:long}")]
    public async Task<IActionResult> GetById(long id)
    {
        var entity = await Set.AsNoTracking()
            .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
            ?? throw BusinessException.NotFound("收款单不存在");
        await CustomerReceiptLifecycleRules.EnsureAuthorizedAsync(Db, CurrentUserId(), entity.CustomerId);
        return Ok(ApiResponse<FinanceReceipt>.Success(entity));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] FinanceReceipt entity)
    {
        var currency = CustomerReceiptLifecycleRules.NormalizeReceiptCurrency(entity.Currency);
        var amount = CustomerReceiptLifecycleRules.NormalizeReceiptAmount(entity.Amount, currency);

        var customer = await Db.BaseCustomers.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == entity.CustomerId);
        CustomerReceiptLifecycleRules.EnsureCustomerAvailable(customer, entity.CustomerId);

        await CustomerReceiptLifecycleRules.EnsureAuthorizedAsync(Db, CurrentUserId(), entity.CustomerId);

        entity.Id = 0;
        entity.ReceiptNo = await _noService.GenerateAsync(DocumentType.Receipt);
        entity.Status = DocumentStatus.Pending;
        entity.Currency = Enum.Parse<Currency>(currency);
        entity.Amount = amount;
        entity.CreatedAt = DateTime.Now;
        Db.FinanceReceipts.Add(entity);
        await Db.SaveChangesAsync();
        return Ok(ApiResponse<object>.Success(new { entity.Id, entity.ReceiptNo }, "收款单创建成功"));
    }

    /// <summary>
    /// 修改（ERP-378）：在同一事务内先对收款单行加排它行锁，再加载权威状态 / 客户 / 币种 / 金额，并在锁内用实时
    /// 启用身份、收款单菜单授权与当前客户数据范围复核既有与请求客户；仅待提交可改，改客户 / 币种 / 金额前必须先
    /// 通过既有显式作废服务释放有效分摊证据，失败整体回滚（状态、原始字段与审计不变）。
    /// </summary>
    [HttpPut("{id:long}")]
    public async Task<IActionResult> Update(long id, [FromBody] FinanceReceipt entity)
    {
        // 与两套分摊证据写入互斥：同一事务内先取收款单行锁，再读权威状态 / 客户 / 币种 / 金额。
        await using var transaction = CustomerReceiptLifecycleRules.IsRelationalProvider(Db)
            ? await Db.Database.BeginTransactionAsync()
            : null;
        try
        {
            await CustomerReceiptLifecycleRules.LockReceiptRowAsync(Db, id);

            var existing = await Db.FinanceReceipts
                .FirstOrDefaultAsync(r => r.Id == id && !r.IsDeleted)
                ?? throw BusinessException.NotFound("收款单不存在");

            // 先在锁内用实时启用身份 / 菜单授权 / 数据范围复核权威客户，再做业务状态判定（授权优先，fail closed）
            await EnsureReceiptAuthorizedAsync(existing);

            // 单据冻结规则：仅待提交状态的收款单可修改
            if (GetStatus(existing) != DocumentStatus.Pending)
                throw BusinessException.RuleConflict("仅待提交状态的单据可修改");

            var currency = CustomerReceiptLifecycleRules.NormalizeReceiptCurrency(entity.Currency);
            var amount = CustomerReceiptLifecycleRules.NormalizeReceiptAmount(entity.Amount, currency);
            var requestedCustomer = await Db.BaseCustomers.AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == entity.CustomerId);
            CustomerReceiptLifecycleRules.EnsureCustomerAvailable(requestedCustomer, entity.CustomerId);

            // 请求客户也必须在当前账号数据范围内（锁内按实时身份 / 菜单授权 / 数据范围复核）
            await CustomerReceiptLifecycleRules.EnsureAuthorizedAsync(Db, CurrentUserId(), entity.CustomerId);

            // 分摊证据保护：修改客户 / 币种 / 金额会破坏既有证据，先拒绝（除非已显式作废释放）
            var existingCurrency = CurrencyAmountRules.NormalizeCurrency(existing.Currency.ToString());
            var changesEvidence = existing.CustomerId != entity.CustomerId
                || existing.Currency != entity.Currency
                || amount != CustomerReceiptLifecycleRules.AuthoritativeReceiptAmount(existing.Amount, existingCurrency);
            if (changesEvidence)
                await CustomerReceiptLifecycleRules.EnsureNoActiveAllocationAsync(Db, id, "修改客户 / 币种 / 金额");

            existing.ReceiptDate = entity.ReceiptDate;
            existing.CustomerId = entity.CustomerId;
            existing.Currency = Enum.Parse<Currency>(currency);
            existing.Amount = amount;
            existing.PaymentMethod = entity.PaymentMethod;
            existing.BankAccount = entity.BankAccount;
            existing.Remark = entity.Remark;
            existing.UpdatedAt = DateTime.Now;
            await Db.SaveChangesAsync();

            if (transaction is not null) await transaction.CommitAsync();
            return Ok(ApiResponse<object>.Success(null, "收款单更新成功"));
        }
        catch
        {
            if (transaction is not null) await transaction.RollbackAsync();
            throw;
        }
    }

    /// <summary>
    /// 提交（ERP-378）：同一事务内先取收款单行锁，再读权威状态并在锁内重新校验实时启用身份 / 菜单授权 / 客户数据范围；
    /// 仅待提交 → 已提交，失败整体回滚。
    /// </summary>
    [HttpPost("{id:long}/submit")]
    public override async Task<IActionResult> Submit(long id)
    {
        await using var transaction = CustomerReceiptLifecycleRules.IsRelationalProvider(Db)
            ? await Db.Database.BeginTransactionAsync()
            : null;
        try
        {
            await CustomerReceiptLifecycleRules.LockReceiptRowAsync(Db, id);

            var entity = await Db.FinanceReceipts
                .FirstOrDefaultAsync(r => r.Id == id && !r.IsDeleted)
                ?? throw BusinessException.NotFound("收款单不存在");

            await EnsureReceiptAuthorizedAsync(entity);
            if (GetStatus(entity) != DocumentStatus.Pending)
                throw BusinessException.RuleConflict("当前状态不允许该操作");

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
    /// 审核（ERP-378）：同一事务内先取收款单行锁，再读权威状态并在锁内重新校验实时启用身份 / 菜单授权 / 客户数据范围；
    /// 仅已提交 → 已审核，与并发取消互斥且失败整体回滚。
    /// </summary>
    [HttpPost("{id:long}/approve")]
    public override async Task<IActionResult> Approve(long id)
    {
        await using var transaction = CustomerReceiptLifecycleRules.IsRelationalProvider(Db)
            ? await Db.Database.BeginTransactionAsync()
            : null;
        try
        {
            await CustomerReceiptLifecycleRules.LockReceiptRowAsync(Db, id);

            var entity = await Db.FinanceReceipts
                .FirstOrDefaultAsync(r => r.Id == id && !r.IsDeleted)
                ?? throw BusinessException.NotFound("收款单不存在");

            await EnsureReceiptAuthorizedAsync(entity);
            if (GetStatus(entity) != DocumentStatus.Submitted)
                throw BusinessException.RuleConflict("当前状态不允许该操作");

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

    /// <summary>
    /// 取消（ERP-378）：同一事务内先取收款单行锁，再读权威状态并在锁内重新校验实时启用身份 / 菜单授权 / 客户数据范围，
    /// 并拒绝存在有效收款分摊证据的取消；重复取消被拒绝，失败整体回滚。
    /// </summary>
    [HttpPost("{id:long}/cancel")]
    public override async Task<IActionResult> Cancel(long id)
    {
        await using var transaction = CustomerReceiptLifecycleRules.IsRelationalProvider(Db)
            ? await Db.Database.BeginTransactionAsync()
            : null;
        try
        {
            await CustomerReceiptLifecycleRules.LockReceiptRowAsync(Db, id);

            var entity = await Db.FinanceReceipts
                .FirstOrDefaultAsync(r => r.Id == id && !r.IsDeleted)
                ?? throw BusinessException.NotFound("收款单不存在");

            await EnsureReceiptAuthorizedAsync(entity);
            if (GetStatus(entity) == DocumentStatus.Cancelled)
                throw BusinessException.RuleConflict("收款单已取消，不能重复取消");
            await CustomerReceiptLifecycleRules.EnsureNoActiveAllocationAsync(Db, id, "取消");

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

    /// <summary>
    /// 删除（软删除，ERP-378）：同一事务内先取收款单行锁，再读权威状态并在锁内重新校验实时启用身份 / 菜单授权 /
    /// 客户数据范围；仅待提交可删，并拒绝存在有效收款分摊证据的删除，失败整体回滚。
    /// </summary>
    [HttpDelete("{id:long}")]
    public override async Task<IActionResult> Delete(long id)
    {
        await using var transaction = CustomerReceiptLifecycleRules.IsRelationalProvider(Db)
            ? await Db.Database.BeginTransactionAsync()
            : null;
        try
        {
            await CustomerReceiptLifecycleRules.LockReceiptRowAsync(Db, id);

            var entity = await Db.FinanceReceipts
                .FirstOrDefaultAsync(r => r.Id == id && !r.IsDeleted)
                ?? throw BusinessException.NotFound("收款单不存在");

            await EnsureReceiptAuthorizedAsync(entity);
            if (GetStatus(entity) != DocumentStatus.Pending)
                throw BusinessException.RuleConflict("仅待提交状态的单据可删除");
            await CustomerReceiptLifecycleRules.EnsureNoActiveAllocationAsync(Db, id, "删除");

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

    /// <summary>按收款单权威客户做身份 / 菜单 / 客户数据范围复核（修改 / 提交 / 审核 / 取消 / 删除共用，锁内调用）。</summary>
    private Task EnsureReceiptAuthorizedAsync(FinanceReceipt entity)
        => CustomerReceiptLifecycleRules.EnsureAuthorizedAsync(Db, CurrentUserId(), entity.CustomerId);
}

/// <summary>
/// 客诉单控制器（ERP-388 生命周期护栏）：列表 / 详情 / 创建 / 修改 / 提交 / 审核 / 取消 / 删除与读取都重新校验
/// 实时启用身份（账号存在、未删除且启用）、既有「客诉单」（<c>complaint</c>）菜单授权与当前权威客户数据范围。
/// <para>创建 / 修改校验文本长度契约与真实可用客户；可空来源销售订单按 Id 精确解析既有、未删除、未取消、同客户的
/// 订单，并要求既有「销售订单」（<c>sales-order</c>）菜单授权（非特权账号）；历史「未关联来源」（null）语义原样保留，
/// 已记录的来源保持只读可读并附显式状态，绝不静默重绑定 / 清除。</para>
/// <para>修改 / 提交 / 审核 / 取消 / 删除都在同一事务内先取来源销售订单行锁、再取客诉单行锁（ERP-388 锁序：
/// 先上游订单行、后客诉单行），再加载权威状态并在锁内复核身份 + 菜单授权 + 客户范围 + 状态 + 文本与来源链接；
/// 失败整体回滚（状态、原始字段与审计不变）。取消来源销售订单侧由 <see cref="SalesOrderCancellationRules"/> 协同：
/// 客诉单绝不阻断其取消，取消原样保留客诉历史与其来源链接。</para>
/// <para>本护栏不写库存 / 资金 / 会计，不产生销售数量、应收、收款、库存或任何财务流水，也不改写销售订单。</para>
/// </summary>
[Route("api/finance/complaints")]
public class FinanceComplaintController : DocumentControllerBase<FinanceComplaint>
{
    private readonly IDocumentNumberService _noService;

    public FinanceComplaintController(IErpDbContext db, IDocumentNumberService noService) : base(db)
    {
        _noService = noService;
    }

    /// <summary>分页：身份 / 菜单授权与客户数据范围在计数与取行之前生效（受限制账号看不到范围外客诉单）。</summary>
    [HttpGet]
    public async Task<IActionResult> GetPaged([FromQuery] PageQuery query, [FromQuery] DocumentStatus? status)
    {
        var scope = await FinanceComplaintLifecycleRules.EnsureMenuAuthorizedAsync(Db, CurrentUserId());
        query.Normalize();
        var source = SalespersonDataScopeService.FilterByCustomer(
            Set.AsNoTracking().Where(o => !o.IsDeleted), scope, o => o.CustomerId);
        if (status.HasValue) source = source.Where(o => o.Status == status.Value);
        if (!string.IsNullOrWhiteSpace(query.Keyword)) source = source.Where(o => o.ComplaintNo.Contains(query.Keyword));
        var total = await source.CountAsync();
        var items = await source.OrderByDescending(o => o.Id)
            .Skip((query.Page - 1) * query.PageSize).Take(query.PageSize).ToListAsync();
        return Ok(ApiResponse<PagedResult<FinanceComplaint>>.Success(
            new PagedResult<FinanceComplaint> { Items = items, Total = total, Page = query.Page, PageSize = query.PageSize }));
    }

    /// <summary>
    /// 详情：按客诉单权威客户做身份 / 菜单授权 / 客户数据范围复核（越界 fail closed）；
    /// 消息中附带来源销售订单链接的显式状态（未关联 / 已关联 / 来源已取消 / 来源不可用），历史链接始终可读。
    /// </summary>
    [HttpGet("{id:long}")]
    public async Task<IActionResult> GetById(long id)
    {
        var scope = await FinanceComplaintLifecycleRules.EnsureMenuAuthorizedAsync(Db, CurrentUserId());
        var entity = await Set.AsNoTracking()
            .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
            ?? throw BusinessException.NotFound("客诉单不存在");
        FinanceComplaintLifecycleRules.EnsureStoredComplaintScopeAllowed(scope, entity);
        var sourceStatus = await FinanceComplaintLifecycleRules.DescribeStoredSourceAsync(Db, entity);
        return Ok(ApiResponse<FinanceComplaint>.Success(entity, sourceStatus));
    }

    /// <summary>
    /// 创建：在生成客诉单号之前先完成文本长度 / 客户可用性 / 客户数据范围 / 来源销售订单资格（含来源菜单授权）
    /// 校验（授权与校验失败绝不消耗单号）；关系型后端在同一事务内先取来源销售订单行锁，锁内权威复核来源资格之后
    /// 才生成单号并落库。
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] FinanceComplaint entity)
    {
        FinanceComplaintLifecycleRules.EnsureEditableTextLengths(entity);

        var customer = await Db.BaseCustomers.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == entity.CustomerId);
        FinanceComplaintLifecycleRules.EnsureCustomerAvailable(customer, entity.CustomerId);

        await FinanceComplaintLifecycleRules.EnsureAuthorizedAsync(Db, CurrentUserId(), entity.CustomerId);
        await FinanceComplaintLifecycleRules.ValidateAndAuthorizeLinkAsync(
            Db, CurrentUserId(), entity.SalesOrderId, entity.CustomerId);

        await using var transaction = FinanceComplaintLifecycleRules.IsRelationalProvider(Db)
            ? await Db.Database.BeginTransactionAsync()
            : null;
        try
        {
            await LockSourceOrderRowsAsync(entity.SalesOrderId);

            // 锁内权威复核来源订单资格（绝不把已被并发取消或篡改的来源写入新客诉单）
            await FinanceComplaintLifecycleRules.ValidateAndAuthorizeLinkAsync(
                Db, CurrentUserId(), entity.SalesOrderId, entity.CustomerId);

            entity.Id = 0;
            entity.ComplaintNo = await _noService.GenerateAsync(DocumentType.Complaint);
            entity.Status = DocumentStatus.Pending;
            entity.CreatedAt = DateTime.Now;
            Db.FinanceComplaints.Add(entity);
            await Db.SaveChangesAsync();
            if (transaction is not null) await transaction.CommitAsync();
            return Ok(ApiResponse<object>.Success(new { entity.Id, entity.ComplaintNo }, "客诉单创建成功"));
        }
        catch
        {
            if (transaction is not null) await transaction.RollbackAsync();
            throw;
        }
    }

    /// <summary>
    /// 修改：同一事务内先取既有 / 请求来源销售订单行锁（Id 升序），再取客诉单行锁，然后加载权威单据，
    /// 并在锁内复核实时身份 / 菜单授权、既有客户范围、请求客户范围与请求来源资格、文本长度契约；仅待提交可改；
    /// 失败整体回滚（状态、原始字段与审计不变）。
    /// </summary>
    [HttpPut("{id:long}")]
    public async Task<IActionResult> Update(long id, [FromBody] FinanceComplaint entity)
    {
        var storedSourceOrderId = await FinanceComplaintLifecycleRules.ReadPersistedSalesOrderIdAsync(Db, id);
        await using var transaction = FinanceComplaintLifecycleRules.IsRelationalProvider(Db)
            ? await Db.Database.BeginTransactionAsync()
            : null;
        try
        {
            await LockSourceOrderRowsAsync(storedSourceOrderId, entity.SalesOrderId);
            await FinanceComplaintLifecycleRules.LockComplaintRowAsync(Db, id);

            var existing = await Db.FinanceComplaints
                .FirstOrDefaultAsync(c => c.Id == id && !c.IsDeleted)
                ?? throw BusinessException.NotFound("客诉单不存在");

            // 授权优先（fail closed）：锁内按实时身份 / 菜单授权 / 数据范围复核既有客户
            var scope = await FinanceComplaintLifecycleRules.EnsureMenuAuthorizedAsync(Db, CurrentUserId());
            FinanceComplaintLifecycleRules.EnsureStoredComplaintScopeAllowed(scope, existing);
            if (GetStatus(existing) != DocumentStatus.Pending)
                throw BusinessException.RuleConflict("仅待提交状态的单据可修改");

            // 既有持久化客户 / 文本 / 来源链接仍在权威范围内且资格有效（锁内复核，绝不基于陈旧来源放行）
            await FinanceComplaintLifecycleRules.EnsurePersistedComplaintConsistentAsync(Db, existing);

            FinanceComplaintLifecycleRules.EnsureEditableTextLengths(entity);
            var customer = await Db.BaseCustomers.AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == entity.CustomerId);
            FinanceComplaintLifecycleRules.EnsureCustomerAvailable(customer, entity.CustomerId);
            FinanceComplaintLifecycleRules.EnsureCustomerInScope(scope, entity.CustomerId);
            await FinanceComplaintLifecycleRules.ValidateAndAuthorizeLinkAsync(
                Db, CurrentUserId(), entity.SalesOrderId, entity.CustomerId);

            existing.ComplaintDate = entity.ComplaintDate;
            existing.CustomerId = entity.CustomerId;
            existing.SalesOrderId = entity.SalesOrderId;
            existing.ComplaintType = entity.ComplaintType;
            existing.Description = entity.Description;
            existing.ResponsibleDept = entity.ResponsibleDept;
            existing.HandleResult = entity.HandleResult;
            existing.Remark = entity.Remark;
            existing.UpdatedAt = DateTime.Now;
            await Db.SaveChangesAsync();

            if (transaction is not null) await transaction.CommitAsync();
            return Ok(ApiResponse<object>.Success(null, "客诉单更新成功"));
        }
        catch
        {
            if (transaction is not null) await transaction.RollbackAsync();
            throw;
        }
    }

    /// <summary>提交：锁内复核实时身份 / 菜单授权 / 客户数据范围、持久化文本与来源链接；仅待提交 → 已提交。</summary>
    [HttpPost("{id:long}/submit")]
    public override Task<IActionResult> Submit(long id)
        => TransitionAsync(id, DocumentStatus.Pending, DocumentStatus.Submitted, "提交成功");

    /// <summary>审核：锁内复核实时身份 / 菜单授权 / 客户数据范围、持久化文本与来源链接；仅已提交 → 已审核。</summary>
    [HttpPost("{id:long}/approve")]
    public override Task<IActionResult> Approve(long id)
        => TransitionAsync(id, DocumentStatus.Submitted, DocumentStatus.Approved, "审核通过");

    /// <summary>
    /// 取消：锁内复核授权并拒绝重复取消；只把状态改为已取消，<strong>不改动</strong>客户 / 来源 / 文本等原始字段，
    /// 也绝不改写来源销售订单或产生任何销售数量 / 应收 / 收款 / 库存 / 财务影响；失败整体回滚（状态与审计不变）。
    /// </summary>
    [HttpPost("{id:long}/cancel")]
    public override async Task<IActionResult> Cancel(long id)
    {
        var storedSourceOrderId = await FinanceComplaintLifecycleRules.ReadPersistedSalesOrderIdAsync(Db, id);
        await using var transaction = FinanceComplaintLifecycleRules.IsRelationalProvider(Db)
            ? await Db.Database.BeginTransactionAsync()
            : null;
        try
        {
            await LockSourceOrderRowsAsync(storedSourceOrderId);
            await LockAndLoadComplaintAsync(id);

            var scope = await FinanceComplaintLifecycleRules.EnsureMenuAuthorizedAsync(Db, CurrentUserId());
            var entity = await Db.FinanceComplaints.FirstAsync(c => c.Id == id);
            FinanceComplaintLifecycleRules.EnsureStoredComplaintScopeAllowed(scope, entity);
            if (GetStatus(entity) == DocumentStatus.Cancelled)
                throw BusinessException.RuleConflict("客诉单已取消，不能重复取消");

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

    /// <summary>删除（软删除）：仅待提交可删；锁内复核范围 / 状态，失败整体回滚且不改写原字段与审计。</summary>
    [HttpDelete("{id:long}")]
    public override async Task<IActionResult> Delete(long id)
    {
        var storedSourceOrderId = await FinanceComplaintLifecycleRules.ReadPersistedSalesOrderIdAsync(Db, id);
        await using var transaction = FinanceComplaintLifecycleRules.IsRelationalProvider(Db)
            ? await Db.Database.BeginTransactionAsync()
            : null;
        try
        {
            await LockSourceOrderRowsAsync(storedSourceOrderId);
            var entity = await LockAndLoadComplaintAsync(id);

            var scope = await FinanceComplaintLifecycleRules.EnsureMenuAuthorizedAsync(Db, CurrentUserId());
            FinanceComplaintLifecycleRules.EnsureStoredComplaintScopeAllowed(scope, entity);
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
    /// 提交 / 审核共用：同一事务内先取来源销售订单行锁、再取客诉单行锁，再加载权威状态并在锁内复核
    /// 身份 / 菜单授权 / 客户范围 / 状态 / 持久化文本与来源链接，失败整体回滚（状态与审计不变）。
    /// </summary>
    private async Task<IActionResult> TransitionAsync(
        long id, DocumentStatus from, DocumentStatus to, string message)
    {
        var storedSourceOrderId = await FinanceComplaintLifecycleRules.ReadPersistedSalesOrderIdAsync(Db, id);
        await using var transaction = FinanceComplaintLifecycleRules.IsRelationalProvider(Db)
            ? await Db.Database.BeginTransactionAsync()
            : null;
        try
        {
            await LockSourceOrderRowsAsync(storedSourceOrderId);
            var entity = await LockAndLoadComplaintAsync(id);

            var scope = await FinanceComplaintLifecycleRules.EnsureMenuAuthorizedAsync(Db, CurrentUserId());
            FinanceComplaintLifecycleRules.EnsureStoredComplaintScopeAllowed(scope, entity);
            if (GetStatus(entity) != from)
                throw BusinessException.RuleConflict("当前状态不允许该操作");
            await FinanceComplaintLifecycleRules.EnsurePersistedComplaintConsistentAsync(Db, entity);

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
    /// 按 ERP-388 锁序对来源销售订单行加更新锁（UPDLOCK, HOLDLOCK）：多个来源订单按 Id 升序获取，
    /// 与销售订单取消 / 出库审核共用同一把上游订单行锁；非关系型提供程序（内存库）无行锁语义，跳过即可。
    /// </summary>
    private async Task LockSourceOrderRowsAsync(params long?[] salesOrderIds)
    {
        if (!Db.Database.IsRelational()) return;
        foreach (var orderId in FinanceComplaintLifecycleRules.MergeSourceOrderLockIds(salesOrderIds))
            await Db.Database.SqlQueryRaw<long>(PreLoadingSalesOrderLinkRules.LockSalesOrderRowSql, orderId).ToListAsync();
    }

    /// <summary>先取客诉单行锁，再加载锁内权威客诉单（未找到 / 已删除 → 不存在）。</summary>
    private async Task<FinanceComplaint> LockAndLoadComplaintAsync(long id)
    {
        await FinanceComplaintLifecycleRules.LockComplaintRowAsync(Db, id);
        return await Db.FinanceComplaints
                   .FirstOrDefaultAsync(c => c.Id == id && !c.IsDeleted)
               ?? throw BusinessException.NotFound("客诉单不存在");
    }
}
