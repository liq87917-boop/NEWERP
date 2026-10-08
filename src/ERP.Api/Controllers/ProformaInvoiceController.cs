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
/// 形式发票 PI 控制器（主子表：一张 PI 多行商品）
/// 业务链：询价单 Inquiry → 报价单 Quotation → **形式发票 PI** → 销售订单
/// 说明：本单据使用 EF 主子表实现（不走存储过程），不触碰现有单据的 SP。
/// <para>ERP-398：列表 / 详情 / 新增 / 修改 / 提交 / 审核 / 销审 / 取消 / 作废 / 删除 / 批量删除，以及打印 /
/// 带入预填 / 转销售订单，每一路由在读取任何计数、生成 / 消耗单据号或写入任何数据<b>之前</b>，都先经
/// <see cref="ProformaInvoiceAuthorizationRules"/> 复核<b>实时启用身份</b>、既有「形式发票 PI」
/// （<c>proforma-invoice</c>）菜单授权与既有业务员数据范围；转换额外要求既有「销售订单」（<c>sales-order</c>）
/// 授权并独立复核来源 / 目标客户范围，绝不因来源 PI 可见而授予目标客户权限。</para>
/// </summary>
[Route("api/sales/proforma-invoices")]
public class ProformaInvoiceController : DocumentControllerBase<ProformaInvoice>
{
    private readonly IDocumentNumberService _noService;

    public ProformaInvoiceController(IErpDbContext db, IDocumentNumberService noService) : base(db)
    {
        _noService = noService;
    }

    /// <summary>PI 不存在 / 越界（不泄露范围外 PI）统一按「不存在」拒绝（fail closed）。</summary>
    private static BusinessException PiNotFound() => BusinessException.NotFound("形式发票 PI 不存在");

    /// <summary>已存 PI 的权威归属读取复核：受限账号缺失归属 / 越界按「不存在」拒绝（不泄露范围外 PI）。</summary>
    private static void EnsureVisible(SalespersonDataScope scope, ProformaInvoice entity)
    {
        if (!scope.AllowsCustomer(entity.CustomerId)) throw PiNotFound();
    }

    // ==================== ERP-399 确定性 PI 来源行锁 + 原子事务 ====================

    /// <summary>
    /// 锁内**权威重读** PI（不复用加锁前的内存实体）：先取 PI 来源行锁，再重新加载含明细的权威行，
    /// 并按实时身份复核已存归属 —— 并发改写 / 删除 / 越界一律在改写任何字段之前原子拒绝。
    /// </summary>
    private async Task<ProformaInvoice> ReloadLockedAsync(SalespersonDataScope scope, long id)
    {
        if (!await ProformaInvoiceMutationRules.LockPiRowAsync(Db, id))
            throw PiNotFound();
        var entity = await Db.ProformaInvoices.Include(o => o.Details)
            .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
            ?? throw PiNotFound();
        ProformaInvoiceAuthorizationRules.EnsureStoredCustomerInScope(scope, entity.CustomerId);
        return entity;
    }

    /// <summary>
    /// 在「PI 来源行锁 + 原子事务」内执行一次生命周期变更：授权 → 开事务 → 加锁并锁内权威重读 →
    /// 调用 <paramref name="body"/>（自行 SaveChanges）→ 提交；任一步失败整体回滚并丢弃半成品变更，
    /// 绝不留下撕裂状态或半成品写入。并发令牌过期（RowVersion）转为可读的业务冲突。
    /// </summary>
    private async Task<IActionResult> RunLockedMutationAsync(long id,
        Func<SalespersonDataScope, ProformaInvoice, Task<IActionResult>> body)
    {
        var scope = await ProformaInvoiceAuthorizationRules.EnsureAuthorizedAsync(Db, CurrentUserId());
        await using var transaction = await ProformaInvoiceMutationRules.BeginMutationTransactionAsync(Db);
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
            throw BusinessException.RuleConflict(ProformaInvoiceMutationRules.StaleRowVersionText);
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
        ProformaInvoiceMutationRules.DiscardTrackedChanges(Db);
    }

    /// <summary>是否存在未删除的下游销售订单链接（只按持久化 SourcePiId 判定，绝不按自由文本推断）。</summary>
    private Task<bool> HasDownstreamLinkAsync(long piId)
        => ProformaInvoiceMutationRules.HasDownstreamLinkAsync(Db, piId);

    /// <summary>分页查询（keyword 匹配 PI 号 / 客户 / 来源报价单号 / 业务员）</summary>
    [HttpGet]
    public async Task<IActionResult> GetPaged([FromQuery] PageQuery query, [FromQuery] DocumentStatus? status,
        [FromQuery] DateTime? start, [FromQuery] DateTime? end)
    {
        // ERP-398：授权先于计数 / 分页（受限账号范围下推到 SQL，缺失归属 / 越界绝不进入计数）。
        var scope = await ProformaInvoiceAuthorizationRules.EnsureAuthorizedAsync(Db, CurrentUserId());
        query.Normalize();
        var source = ProformaInvoiceAuthorizationRules.ApplyScope(
            Set.AsNoTracking().Where(o => !o.IsDeleted), scope);
        if (status.HasValue) source = source.Where(o => o.Status == status.Value);
        if (start.HasValue) source = source.Where(o => o.PiDate >= start.Value);
        if (end.HasValue) source = source.Where(o => o.PiDate <= end.Value);
        if (!string.IsNullOrWhiteSpace(query.Keyword))
        {
            var kw = query.Keyword;
            source = source.Where(o => o.PiNo.Contains(kw) || o.CustomerName.Contains(kw)
                                       || o.QuotationNo.Contains(kw) || o.SalesmanName.Contains(kw));
        }

        var total = await source.CountAsync();
        var items = await source.OrderByDescending(o => o.Id)
            .Skip((query.Page - 1) * query.PageSize).Take(query.PageSize).ToListAsync();
        return Ok(ApiResponse<PagedResult<ProformaInvoice>>.Success(new PagedResult<ProformaInvoice>
        {
            Items = items,
            Total = total,
            Page = query.Page,
            PageSize = query.PageSize
        }));
    }

    /// <summary>查询详情（含明细）</summary>
    [HttpGet("{id:long}")]
    public async Task<IActionResult> GetById(long id)
    {
        var scope = await ProformaInvoiceAuthorizationRules.EnsureAuthorizedAsync(Db, CurrentUserId());
        var entity = await Set.AsNoTracking().Include(o => o.Details)
            .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
            ?? throw PiNotFound();
        EnsureVisible(scope, entity);
        entity.Details = entity.Details.Where(d => !d.IsDeleted).OrderBy(d => d.SortNo).ToList();
        return Ok(ApiResponse<ProformaInvoice>.Success(entity));
    }

    /// <summary>创建（单号缺省由字轨生成；行号 / 金额 / 合计 / 定金由后端复核）</summary>
    /// <summary>
    /// 创建（ERP-398 授权 + ERP-403 来源血缘）：显式来源报价单先做**权威解析 + 来源行锁 + 原子事务**，
    /// 在锁内复核实时身份 / 既有「报价单」+「形式发票 PI」菜单 / 权威客户范围、既有转换资格与唯一目标，
    /// <b>之后</b>才预约单据号与写入；显式来源 Id 全部无法解析时按「显式历史值」原样保留（不构成实时链接）；
    /// 未链接的手工 PI 保持既有口径（不取任何来源锁、不开事务）。
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] ProformaInvoice entity)
    {
        // ERP-398：授权与拟议客户范围校验先于单号生成与任何写入（被拒绝的调用方绝不消耗单据号）。
        var scope = await ProformaInvoiceAuthorizationRules.EnsureAuthorizedAsync(Db, CurrentUserId());
        ProformaInvoiceAuthorizationRules.EnsureProposedCustomerInScope(scope, entity.CustomerId);

        var requestedQuotationId = SalesDocumentSourceLineageRules.NormalizeId(entity.QuotationId);
        var change = SalesDocumentSourceLineageRules.ResolveChange(
            SalesDocumentSourceKind.Quotation, null, requestedQuotationId);

        IDbContextTransaction? transaction = null;
        try
        {
            var lineage = new SalesDocumentSourceLineage();
            if (change.HasSource)
            {
                // 第一阶段：有界只读权威解析（不加锁）—— 无法解析的显式历史值不占用任何来源锁。
                lineage = await SalesDocumentSourceLineageRules.ResolveAsync(Db, change, entity.CustomerId);
                if (!lineage.IsUnresolvedLegacy)
                {
                    // 第二阶段：原子事务内先取来源报价单行锁（与「报价单 → PI」直接转换共用同一把），锁内权威重读后放行。
                    transaction = await SalesDocumentSourceLineageRules.BeginWriteTransactionAsync(Db);
                    if (!await SalesDocumentSourceLineageRules.LockSourceRowAsync(Db,
                            SalesDocumentSourceKind.Quotation, change.SourceId))
                        throw BusinessException.RuleConflict(SalesDocumentSourceLineageRules.SourceNotFoundText);

                    lineage = await SalesDocumentSourceLineageRules.ResolveAsync(Db, change, entity.CustomerId);
                    if (lineage.IsUnresolvedLegacy)
                        throw BusinessException.RuleConflict(SalesDocumentSourceLineageRules.SourceNotFoundText);

                    await SalesDocumentSourceLineageRules.EnsureWriteAuthorizedAsync(
                        Db, CurrentUserId(), lineage, entity.CustomerId);
                    await SalesDocumentSourceLineageRules.EnsureNewLinkEligibleAsync(Db, lineage, null);
                }
            }

            entity.Id = 0;
            if (string.IsNullOrWhiteSpace(entity.PiNo))
                entity.PiNo = await _noService.GenerateAsync(DocumentType.ProformaInvoice);
            entity.Status = DocumentStatus.Pending;
            entity.CreatedAt = DateTime.Now;
            Normalize(entity);

            // 来源字段：权威血缘优先；显式历史值原样保留；未链接时保持调用方提交的自由文本（不构成链接）。
            if (change.HasSource)
            {
                if (lineage.IsUnresolvedLegacy)
                    SalesDocumentSourceLineageRules.ApplyExplicitValues(entity, change.SourceId, entity.QuotationNo);
                else
                    SalesDocumentSourceLineageRules.Apply(entity, lineage);
            }

            Db.ProformaInvoices.Add(entity);
            await Db.SaveChangesAsync();
            if (transaction is not null) await transaction.CommitAsync();
            return Ok(ApiResponse<object>.Success(new { entity.Id, entity.PiNo }, "形式发票 PI 创建成功"));
        }
        catch (DbUpdateConcurrencyException)
        {
            await RollbackAsync(transaction);
            throw BusinessException.RuleConflict(SalesDocumentSourceLineageRules.ConcurrentMutationText);
        }
        catch
        {
            await RollbackAsync(transaction);
            throw;
        }
    }

    /// <summary>
    /// 修改（ERP-399：PI 来源行锁 + 原子事务内锁内权威重读后改写；明细整体替换）。
    /// 已审核 / 已转订单 / 已作废不可改；存在下游销售订单链接的 PI 一律冻结（保留显式历史，不做反向冲销）。
    /// </summary>
    /// <summary>
    /// 修改（ERP-399：PI 来源行锁 + 原子事务内锁内权威重读后改写；明细整体替换）。
    /// ERP-403：PI 修改**沿用持久化来源报价单链接**（调用方提交的来源字段不构成链接），
    /// 并在改写前复核该持久化来源仍可精确解析（已删除来源 fail closed）。
    /// 已审核 / 已转订单 / 已作废不可改；存在下游销售订单链接的 PI 一律冻结（保留显式历史，不做反向冲销）。
    /// </summary>
    [HttpPut("{id:long}")]
    public async Task<IActionResult> Update(long id, [FromBody] ProformaInvoice entity)
        => await RunLockedMutationAsync(id, async (scope, existing) =>
        {
            ProformaInvoiceAuthorizationRules.EnsureProposedCustomerInScope(scope, entity.CustomerId);
            ProformaInvoiceMutationRules.EnsureNoDownstreamLink(await HasDownstreamLinkAsync(id));
            ProformaInvoiceMutationRules.EnsureEditAllowed(GetStatus(existing));
            await SalesDocumentSourceLineageRules.EnsurePersistedSourceIntactAsync(Db,
                SalesDocumentSourceKind.Quotation, existing.QuotationId, entity.CustomerId, id);
            ApplyUpdate(existing, entity, id);
            await Db.SaveChangesAsync();
            return Ok(ApiResponse<object>.Success(null, "形式发票 PI 更新成功"));
        });

    /// <summary>把请求体字段写入锁内权威实体（明细整体替换，定金比例变化时按新比例重算定金金额）。</summary>
    private void ApplyUpdate(ProformaInvoice existing, ProformaInvoice entity, long id)
    {
        var originalDepositRatio = existing.DepositRatio;   // 用于判断定金比例是否被修改

        existing.PiDate = entity.PiDate;
        existing.CustomerId = entity.CustomerId;
        existing.CustomerName = entity.CustomerName;
        existing.ContactPerson = entity.ContactPerson;
        existing.ContactPhone = entity.ContactPhone;
        existing.ContactEmail = entity.ContactEmail;
        existing.Consignee = entity.Consignee;
        existing.NotifyParty = entity.NotifyParty;
        existing.ShippingMarks = entity.ShippingMarks;
        existing.BankInfo = entity.BankInfo;
        existing.TradeTerms = entity.TradeTerms;
        existing.PortOfLoading = entity.PortOfLoading;
        existing.PortOfDestination = entity.PortOfDestination;
        existing.PaymentTerms = entity.PaymentTerms;
        existing.ShippingTerms = entity.ShippingTerms;
        existing.LeadTime = entity.LeadTime;
        existing.Currency = entity.Currency;
        existing.ExchangeRate = entity.ExchangeRate;
        existing.DepositRatio = entity.DepositRatio;
        existing.SalesmanId = entity.SalesmanId;
        existing.SalesmanName = entity.SalesmanName;
        existing.Remark = entity.Remark;

        Db.ProformaInvoiceDetails.RemoveRange(existing.Details);
        entity.PiNo = existing.PiNo;
        entity.QuotationId = existing.QuotationId;
        entity.QuotationNo = existing.QuotationNo;
        entity.Id = id;
        // 定金口径：比例被修改时按新比例重算定金金额；比例不变则保留（允许手改的）原金额
        if (originalDepositRatio != entity.DepositRatio) entity.DepositAmount = 0m;
        Normalize(entity);
        existing.Details = entity.Details;
        existing.TotalAmount = entity.TotalAmount;
        existing.TotalAmountCny = entity.TotalAmountCny;
        existing.DepositAmount = entity.DepositAmount;
        existing.UpdatedAt = DateTime.Now;
    }

    /// <summary>
    /// 审核（ERP-399：PI 来源行锁 + 原子事务内锁内权威重读后审核；草稿可直接审核，也支持提交后审核；
    /// 无有效明细不允许审核）。存在下游销售订单链接的 PI 一律冻结。
    /// </summary>
    [HttpPost("{id:long}/approve")]
    public override async Task<IActionResult> Approve(long id)
        => await RunLockedMutationAsync(id, async (_, entity) =>
        {
            ProformaInvoiceMutationRules.EnsureNoDownstreamLink(await HasDownstreamLinkAsync(id));
            await SalesDocumentSourceLineageRules.EnsurePersistedSourceIntactAsync(Db,
                SalesDocumentSourceKind.Quotation, entity.QuotationId, entity.CustomerId, id);
            ProformaInvoiceMutationRules.EnsureApproveAllowed(GetStatus(entity),
                ProformaInvoiceMutationRules.ActiveDetailCount(entity));
            SetStatus(entity, DocumentStatus.Approved);
            await Db.SaveChangesAsync();
            return Ok(ApiResponse<object>.Success(null, "PI 已审核"));
        });

    /// <summary>销审（ERP-399：PI 来源行锁 + 原子事务内锁内权威重读后退回草稿，可继续修改）</summary>
    [HttpPost("{id:long}/unaudit")]
    public async Task<IActionResult> Unaudit(long id)
        => await RunLockedMutationAsync(id, async (_, entity) =>
        {
            ProformaInvoiceMutationRules.EnsureNoDownstreamLink(await HasDownstreamLinkAsync(id));
            ProformaInvoiceMutationRules.EnsureUnauditAllowed(GetStatus(entity));
            SetStatus(entity, DocumentStatus.Pending);
            await Db.SaveChangesAsync();
            return Ok(ApiResponse<object>.Success(null, "已销审，可继续修改"));
        });

    /// <summary>提交（ERP-399：PI 来源行锁 + 原子事务内锁内权威重读后 草稿 → 已提交）</summary>
    [HttpPost("{id:long}/submit")]
    public override async Task<IActionResult> Submit(long id)
        => await RunLockedMutationAsync(id, async (_, entity) =>
        {
            ProformaInvoiceMutationRules.EnsureNoDownstreamLink(await HasDownstreamLinkAsync(id));
            await SalesDocumentSourceLineageRules.EnsurePersistedSourceIntactAsync(Db,
                SalesDocumentSourceKind.Quotation, entity.QuotationId, entity.CustomerId, id);
            ProformaInvoiceMutationRules.EnsureSubmitAllowed(GetStatus(entity));
            SetStatus(entity, DocumentStatus.Submitted);
            await Db.SaveChangesAsync();
            return Ok(ApiResponse<object>.Success(null, "提交成功"));
        });

    /// <summary>取消（ERP-399：PI 来源行锁 + 原子事务内锁内权威重读后取消；已链接 PI 一律冻结）</summary>
    [HttpPost("{id:long}/cancel")]
    public override async Task<IActionResult> Cancel(long id)
        => await RunLockedMutationAsync(id, async (_, entity) =>
        {
            ProformaInvoiceMutationRules.EnsureNoDownstreamLink(await HasDownstreamLinkAsync(id));
            ProformaInvoiceMutationRules.EnsureCancelAllowed(GetStatus(entity));
            SetStatus(entity, DocumentStatus.Cancelled);
            await Db.SaveChangesAsync();
            return Ok(ApiResponse<object>.Success(null, "已取消"));
        });

    /// <summary>删除（ERP-399：PI 来源行锁 + 原子事务内锁内权威重读后软删除，仅待提交状态可删）</summary>
    [HttpDelete("{id:long}")]
    public override async Task<IActionResult> Delete(long id)
        => await RunLockedMutationAsync(id, async (_, entity) =>
        {
            ProformaInvoiceMutationRules.EnsureNoDownstreamLink(await HasDownstreamLinkAsync(id));
            ProformaInvoiceMutationRules.EnsureDeleteAllowed(GetStatus(entity));
            entity.IsDeleted = true;
            entity.UpdatedAt = DateTime.Now;
            await Db.SaveChangesAsync();
            return Ok(ApiResponse<object>.Success(null, "删除成功"));
        });

    /// <summary>
    /// 批量软删除（ERP-398 / ERP-399）：授权后<strong>按 PI Id 升序确定性加锁</strong>（与单行生命周期路由
    /// 共用同一把 PI 来源行锁），锁内权威重读并逐行复核归属 / 下游链接 / 状态，混合允许 / 越界 / 无主批次
    /// <b>整体拒绝</b>、不做部分删除；全部通过后才在同一原子事务内一次性软删除。
    /// </summary>
    [HttpPost("batch-delete")]
    public async Task<IActionResult> BatchDelete([FromBody] List<long> ids)
    {
        var scope = await ProformaInvoiceAuthorizationRules.EnsureAuthorizedAsync(Db, CurrentUserId());
        var requested = ProformaInvoiceMutationRules.MergeLockIds(ids);
        if (requested.Count == 0)
            throw BusinessException.InvalidParameter("请指定要删除的 PI");

        await using var transaction = await ProformaInvoiceMutationRules.BeginMutationTransactionAsync(Db);
        try
        {
            // 锁序：全部 PI 来源行按 Id 升序确定性加锁（去重、仅正整数；绝不反向获取其它锁）。
            await ProformaInvoiceMutationRules.LockPiRowsAsync(Db, requested);

            // 锁内权威重读：存在性 / 归属 / 下游链接 / 状态逐行复核，任一行不满足即整体拒绝（无部分删除）。
            var entities = await Db.ProformaInvoices
                .Where(o => requested.Contains(o.Id) && !o.IsDeleted).ToListAsync();
            var found = entities.Select(o => o.Id).ToHashSet();
            var missing = requested.Where(id => !found.Contains(id)).ToList();
            if (missing.Count > 0)
                throw BusinessException.NotFound(
                    $"批量删除的 PI 不存在或已删除：{string.Join("、", missing)}（未做任何部分删除）");

            foreach (var pi in entities)
            {
                if (!scope.AllowsCustomer(pi.CustomerId))
                    throw new BusinessException(ProformaInvoiceAuthorizationRules.BatchOutOfScopeText,
                        ErrorCodes.Forbidden);
            }

            foreach (var pi in entities)
                ProformaInvoiceMutationRules.EnsureNoDownstreamLink(await HasDownstreamLinkAsync(pi.Id));

            ProformaInvoiceMutationRules.EnsureBatchDeleteAllowed(entities.Select(GetStatus));

            foreach (var entity in entities)
            {
                entity.IsDeleted = true;
                entity.UpdatedAt = DateTime.Now;
            }
            await Db.SaveChangesAsync();
            if (transaction is not null) await transaction.CommitAsync();
            return Ok(ApiResponse<object>.Success(null, "批量删除成功"));
        }
        catch (DbUpdateConcurrencyException)
        {
            await RollbackAsync(transaction);
            throw BusinessException.RuleConflict(ProformaInvoiceMutationRules.StaleRowVersionText);
        }
        catch
        {
            await RollbackAsync(transaction);
            throw;
        }
    }

    /// <summary>
    /// 作废（ERP-399：PI 来源行锁 + 原子事务内锁内权威重读后作废；已转销售订单 / 已链接 PI 不可作废，
    /// 重复作废给出明确提示）。绝不通过取消目标订单做反向冲销。
    /// </summary>
    [HttpPost("{id:long}/void")]
    public async Task<IActionResult> Void(long id)
        => await RunLockedMutationAsync(id, async (_, entity) =>
        {
            ProformaInvoiceMutationRules.EnsureNoDownstreamLink(await HasDownstreamLinkAsync(id));
            ProformaInvoiceMutationRules.EnsureVoidAllowed(GetStatus(entity));
            SetStatus(entity, DocumentStatus.Cancelled);
            await Db.SaveChangesAsync();
            return Ok(ApiResponse<object>.Success(null, "PI 已作废"));
        });

    /// <summary>
    /// 带入预填销售订单（ERP-010）：按 PI 返回一张**未落库**的销售订单草稿
    /// （收货人 / 通知人 / 唛头 / 运输条款 / 定金口径随 PI，同时保留 PI 背后的来源报价单），
    /// 前端据此打开「销售订单 → 新增」表单继续编辑后再保存（保存走 <c>POST /api/sales-orders</c>）。
    /// 守卫与 <see cref="ToSalesOrder"/> 一致：只允许已审核 PI，已作废或已生成销售订单均被拒绝；
    /// <para>ERP-399：预填<b>保持只读</b>（不加锁、不开事务、不占用单据号、不写库），最终保存以销售订单
    /// 新增路由为权威；PI 侧的并发一致性由 <see cref="ToSalesOrder"/> 的 PI 来源行锁协议保证。</para>
    /// </summary>
    [HttpGet("{id:long}/order-prefill")]
    public async Task<IActionResult> OrderPrefill(long id)
    {
        // ERP-398：转换需 PI 授权 + 既有「销售订单」授权；来源 / 目标客户范围在生成草稿之前独立复核。
        var scope = await ProformaInvoiceAuthorizationRules
            .EnsureSalesOrderConversionAuthorizedAsync(Db, CurrentUserId());
        var pi = await Db.ProformaInvoices.AsNoTracking().Include(o => o.Details)
            .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
            ?? throw PiNotFound();
        EnsureVisible(scope, pi);
        ProformaInvoiceAuthorizationRules.EnsureSourceCustomerInScope(scope, pi.CustomerId);

        var order = await SalesOrderConversion.FromProformaInvoiceAsync(Db, pi);
        ProformaInvoiceAuthorizationRules.EnsureTargetCustomerInScope(scope, order.CustomerId);
        SalesOrderConversion.EnsureConversionScopeAuthorized(scope, pi.CustomerId, order.CustomerId);
        return Ok(ApiResponse<SalesOrderPrefillResult>.Success(new SalesOrderPrefillResult
        {
            SourceType = SalesOrderConversion.ProformaInvoiceSourceType,
            SourceId = pi.Id,
            SourceNo = pi.PiNo,
            Order = order
        }, "已按形式发票 PI 带入销售订单草稿"));
    }

    /// <summary>
    /// 转为销售订单（ERP-010 / ERP-399）：在「PI 来源行锁 + 原子事务」内按已审核 PI 生成一张销售订单
    /// （EF 主子表路径，不走旧版存储过程）。锁内权威重读 PI 并复核转换资格（同一 PI 仅生成一张，以销售订单
    /// 的来源字段 <c>SourcePiId</c> 为准）；只新增订单、绝不覆盖既有订单；生成后 PI 状态置「已完成」
    /// （即「已转销售订单」，与该状态在审核 / 作废处的既有语义一致）。
    /// 任一步失败（资格不符 / 范围越界 / 单号生成失败 / 写入失败）整体回滚：订单、明细、已预约单号与来源状态都不变。
    /// </summary>
    [HttpPost("{id:long}/to-order")]
    public async Task<IActionResult> ToSalesOrder(long id)
    {
        // ERP-398：转换需 PI 授权 + 既有「销售订单」授权（实时身份，绝不缓存）。
        var scope = await ProformaInvoiceAuthorizationRules
            .EnsureSalesOrderConversionAuthorizedAsync(Db, CurrentUserId());

        await using var transaction = await ProformaInvoiceMutationRules.BeginMutationTransactionAsync(Db);
        try
        {
            // ERP-399：先取 PI 来源行锁，再锁内权威重读（生命周期 / RowVersion / 明细都以持久化行为准）。
            if (!await ProformaInvoiceMutationRules.LockPiRowAsync(Db, id))
                throw PiNotFound();
            var pi = await Db.ProformaInvoices.Include(o => o.Details)
                .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
                ?? throw PiNotFound();
            EnsureVisible(scope, pi);
            ProformaInvoiceAuthorizationRules.EnsureSourceCustomerInScope(scope, pi.CustomerId);

            // 锁内转换资格 + 既有来源订单复核（重复生成唯一化：同一 PI 至多一张完整销售订单）。
            var existing = await ProformaInvoiceMutationRules.FindDownstreamOrderAsync(Db, pi.Id);
            ProformaInvoiceMutationRules.EnsureConversionEligible(pi, existing,
                ProformaInvoiceMutationRules.ActiveDetailCount(pi));

            var order = await SalesOrderConversion.FromProformaInvoiceAsync(Db, pi);
            ProformaInvoiceAuthorizationRules.EnsureTargetCustomerInScope(scope, order.CustomerId);
            SalesOrderConversion.EnsureConversionScopeAuthorized(scope, pi.CustomerId, order.CustomerId);
            // 落库订单必须与本次锁内权威重读完全一致（数量 / 币种 / 汇率 / 合计 / 定金 / 显式 SourcePiId）。
            SalesOrderConversion.EnsureDraftMatchesSource(pi, order);

            order.OrderNo = await _noService.GenerateAsync(DocumentType.SalesOrder);
            Db.SalesOrders.Add(order);
            SetStatus(pi, DocumentStatus.Completed);
            await Db.SaveChangesAsync();
            if (transaction is not null) await transaction.CommitAsync();
            return Ok(ApiResponse<SalesOrderConversionResult>.Success(new SalesOrderConversionResult
            {
                Id = order.Id,
                OrderNo = order.OrderNo,
                SourceNo = pi.PiNo
            }, "已生成销售订单"));
        }
        catch (DbUpdateConcurrencyException)
        {
            await RollbackAsync(transaction);
            throw BusinessException.RuleConflict(ProformaInvoiceMutationRules.StaleRowVersionText);
        }
        catch
        {
            await RollbackAsync(transaction);
            throw;
        }
    }

    /// <summary>打印数据（主表 + 明细；打印模板由 /api/sys/print-templates/proforma-invoice 提供）</summary>
    [HttpGet("{id:long}/print")]
    public async Task<IActionResult> GetPrint(long id)
    {
        var scope = await ProformaInvoiceAuthorizationRules.EnsureAuthorizedAsync(Db, CurrentUserId());
        var entity = await Set.AsNoTracking().Include(o => o.Details)
            .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
            ?? throw PiNotFound();
        EnsureVisible(scope, entity);
        entity.Details = entity.Details.Where(d => !d.IsDeleted).OrderBy(d => d.SortNo).ToList();
        return Ok(ApiResponse<ProformaInvoice>.Success(entity));
    }

    /// <summary>
    /// 行号 / 金额 / 合计 / 定金统一整理（后端复核，防止前端篡改合计）。
    /// 定金口径：比例限制 0~100；金额留 0 时按比例自动计算，允许手工覆盖但不得超过 PI 总额
    /// （修改时若比例发生变化，调用方会先清零金额，使定金按新比例重算）。
    /// </summary>
    public static void Normalize(ProformaInvoice e)
    {
        decimal total = 0;
        var line = 0;
        foreach (var d in e.Details)
        {
            d.Id = 0;
            d.PiId = e.Id;
            d.PiNo = e.PiNo;
            d.SortNo = ++line;
            d.Amount = Math.Round(d.Quantity * d.UnitPrice, 2);
            d.CreatedAt = DateTime.Now;
            total += d.Amount;
        }
        e.TotalAmount = Math.Round(total, 2);
        var rate = e.ExchangeRate == 0 ? 1 : e.ExchangeRate;
        e.TotalAmountCny = Math.Round(e.TotalAmount * rate, 2);
        if (e.PiDate == default) e.PiDate = DateTime.Today;
        if (e.DepositRatio < 0 || e.DepositRatio > 100)
            throw BusinessException.InvalidParameter("定金比例必须在 0~100 之间");

        var depositByRatio = Math.Round(e.TotalAmount * e.DepositRatio / 100m, 2);
        if (e.DepositAmount <= 0) e.DepositAmount = depositByRatio;
        else if (e.DepositAmount > e.TotalAmount)
            throw BusinessException.InvalidParameter("定金金额不能大于 PI 总额");
    }
}


