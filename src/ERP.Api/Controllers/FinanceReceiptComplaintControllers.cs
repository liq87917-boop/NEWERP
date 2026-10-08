using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ERP.Api.Controllers;

/// <summary>
/// 收款单控制器（ERP-349 生命周期护栏）：创建 / 修改 / 提交 / 审核 / 取消 / 删除与读取都重新校验
/// 当前身份、收款单（receipt）菜单授权与客户数据范围；创建与修改校验真实可用客户、受支持币种与按币种精度
/// 取整后大于 0 的金额；存在有效收款分摊证据时拒绝取消 / 删除或修改客户 / 币种 / 金额（先走既有作废服务释放）。
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

    [HttpPut("{id:long}")]
    public async Task<IActionResult> Update(long id, [FromBody] FinanceReceipt entity)
    {
        var existing = await Db.FinanceReceipts
            .FirstOrDefaultAsync(r => r.Id == id && !r.IsDeleted)
            ?? throw BusinessException.NotFound("收款单不存在");

        // 单据冻结规则：仅待提交状态的收款单可修改
        if (existing.Status != DocumentStatus.Pending)
            throw BusinessException.RuleConflict("仅待提交状态的单据可修改");

        var currency = CustomerReceiptLifecycleRules.NormalizeReceiptCurrency(entity.Currency);
        var amount = CustomerReceiptLifecycleRules.NormalizeReceiptAmount(entity.Amount, currency);
        var requestedCustomer = await Db.BaseCustomers.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == entity.CustomerId);
        CustomerReceiptLifecycleRules.EnsureCustomerAvailable(requestedCustomer, entity.CustomerId);

        // 保护既有客户范围与请求客户范围：两次都必须在当前账号数据范围内
        await CustomerReceiptLifecycleRules.EnsureAuthorizedAsync(Db, CurrentUserId(), existing.CustomerId);
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
        return Ok(ApiResponse<object>.Success(null, "收款单更新成功"));
    }

    /// <summary>提交：重新校验身份 / 菜单 / 客户范围后，仅待提交 → 已提交。</summary>
    [HttpPost("{id:long}/submit")]
    public override async Task<IActionResult> Submit(long id)
    {
        var entity = await GetOrThrowAsync(id, "收款单不存在");
        await CustomerReceiptLifecycleRules.EnsureAuthorizedAsync(Db, CurrentUserId(), entity.CustomerId);
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
        var entity = await GetOrThrowAsync(id, "收款单不存在");
        await CustomerReceiptLifecycleRules.EnsureAuthorizedAsync(Db, CurrentUserId(), entity.CustomerId);
        if (GetStatus(entity) != DocumentStatus.Submitted)
            throw BusinessException.RuleConflict("当前状态不允许该操作");
        SetStatus(entity, DocumentStatus.Approved);
        await Db.SaveChangesAsync();
        return Ok(ApiResponse<object>.Success(null, "审核通过"));
    }

    /// <summary>取消：重新校验身份 / 菜单 / 客户范围，并拒绝存在有效收款分摊证据的取消；重复取消被拒绝。</summary>
    [HttpPost("{id:long}/cancel")]
    public override async Task<IActionResult> Cancel(long id)
    {
        var entity = await GetOrThrowAsync(id, "收款单不存在");
        await CustomerReceiptLifecycleRules.EnsureAuthorizedAsync(Db, CurrentUserId(), entity.CustomerId);
        if (GetStatus(entity) == DocumentStatus.Cancelled)
            throw BusinessException.RuleConflict("收款单已取消，不能重复取消");
        await CustomerReceiptLifecycleRules.EnsureNoActiveAllocationAsync(Db, id, "取消");
        SetStatus(entity, DocumentStatus.Cancelled);
        await Db.SaveChangesAsync();
        return Ok(ApiResponse<object>.Success(null, "已取消"));
    }

    /// <summary>删除（软删除）：重新校验身份 / 菜单 / 客户范围，仅待提交可删，并拒绝存在有效收款分摊证据的删除。</summary>
    [HttpDelete("{id:long}")]
    public override async Task<IActionResult> Delete(long id)
    {
        var entity = await GetOrThrowAsync(id, "收款单不存在");
        await CustomerReceiptLifecycleRules.EnsureAuthorizedAsync(Db, CurrentUserId(), entity.CustomerId);
        if (GetStatus(entity) != DocumentStatus.Pending)
            throw BusinessException.RuleConflict("仅待提交状态的单据可删除");
        await CustomerReceiptLifecycleRules.EnsureNoActiveAllocationAsync(Db, id, "删除");
        entity.IsDeleted = true;
        entity.UpdatedAt = DateTime.Now;
        await Db.SaveChangesAsync();
        return Ok(ApiResponse<object>.Success(null, "删除成功"));
    }
}

/// <summary>
/// 客诉单控制器
/// </summary>
[Route("api/finance/complaints")]
public class FinanceComplaintController : DocumentControllerBase<FinanceComplaint>
{
    private readonly IDocumentNumberService _noService;

    public FinanceComplaintController(IErpDbContext db, IDocumentNumberService noService) : base(db)
    {
        _noService = noService;
    }

    [HttpGet]
    public async Task<IActionResult> GetPaged([FromQuery] PageQuery query, [FromQuery] DocumentStatus? status)
    {
        query.Normalize();
        var source = Set.AsNoTracking().Where(o => !o.IsDeleted);
        if (status.HasValue) source = source.Where(o => o.Status == status.Value);
        if (!string.IsNullOrWhiteSpace(query.Keyword)) source = source.Where(o => o.ComplaintNo.Contains(query.Keyword));
        var total = await source.CountAsync();
        var items = await source.OrderByDescending(o => o.Id)
            .Skip((query.Page - 1) * query.PageSize).Take(query.PageSize).ToListAsync();
        return Ok(ApiResponse<PagedResult<FinanceComplaint>>.Success(
            new PagedResult<FinanceComplaint> { Items = items, Total = total, Page = query.Page, PageSize = query.PageSize }));
    }

    [HttpGet("{id:long}")]
    public async Task<IActionResult> GetById(long id)
        => Ok(ApiResponse<FinanceComplaint>.Success(await GetOrThrowAsync(id, "客诉单不存在")));

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] FinanceComplaint entity)
    {
        entity.Id = 0;
        entity.ComplaintNo = await _noService.GenerateAsync(DocumentType.Complaint);
        entity.Status = DocumentStatus.Pending;
        entity.CreatedAt = DateTime.Now;
        Db.FinanceComplaints.Add(entity);
        await Db.SaveChangesAsync();
        return Ok(ApiResponse<object>.Success(new { entity.Id, entity.ComplaintNo }, "客诉单创建成功"));
    }

    [HttpPut("{id:long}")]
    public async Task<IActionResult> Update(long id, [FromBody] FinanceComplaint entity)
    {
        var existing = await GetOrThrowAsync(id, "客诉单不存在");
        if (GetStatus(existing) != DocumentStatus.Pending)
            throw BusinessException.RuleConflict("仅待提交状态的单据可修改");
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
        return Ok(ApiResponse<object>.Success(null, "客诉单更新成功"));
    }
}
