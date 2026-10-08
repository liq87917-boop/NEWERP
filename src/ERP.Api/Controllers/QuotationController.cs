using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace ERP.Api.Controllers;

/// <summary>
/// 报价单控制器（主子表：一张报价单多行商品）
/// 业务链：询价单 Inquiry → **报价单 Quotation** → 形式发票 PI → 销售订单
/// 说明：本单据使用 EF 主子表实现（不走存储过程），不触碰现有单据的 SP
/// <para>ERP-400：列表 / 详情 / 新增 / 修改 / 提交 / 审核 / 销审 / 取消 / 作废 / 删除 / 创建版本 / 版本链 /
/// 有效期提醒 / 打印 / 询价带入 / 带入预填销售订单 / 转 PI / 转销售订单，每一路由在读取任何计数、
/// 生成 / 消耗单据号或写入任何数据<b>之前</b>都先经 <see cref="QuotationAuthorizationRules"/> 复核
/// <b>实时启用身份</b>、既有「报价单」（<c>quotation</c>）菜单授权与既有业务员数据范围；
/// 转换额外要求既有「形式发票 PI」（<c>proforma-invoice</c>）或「销售订单」（<c>sales-order</c>）授权，
/// 并独立复核来源 / 目标客户范围，绝不因来源报价单可见而授予目标菜单 / 客户权限。</para>
/// <para>ERP-400：所有生命周期写路径（修改 / 提交 / 审核 / 销审 / 取消 / 作废 / 删除 / 创建版本 / 转换）都在
/// 「报价单来源行锁 + 原子事务 + 锁内权威重读」内执行，与 PI 锁的确定顺序为「报价单行锁 → PI 行锁」。</para>
/// </summary>
[Route("api/sales/quotations")]
public class QuotationController : DocumentControllerBase<Quotation>
{
    private readonly IDocumentNumberService _noService;

    public QuotationController(IErpDbContext db, IDocumentNumberService noService) : base(db)
    {
        _noService = noService;
    }

    /// <summary>报价单不存在 / 越界（不泄露范围外报价单）统一按「不存在」拒绝（fail closed）。</summary>
    private static BusinessException QuotationNotFound() => BusinessException.NotFound("报价单不存在");

    /// <summary>已存报价单的权威归属读取复核：受限账号缺失归属 / 越界按「不存在」拒绝（不泄露范围外报价单）。</summary>
    private static void EnsureVisible(SalespersonDataScope scope, Quotation entity)
    {
        if (!scope.AllowsCustomer(entity.CustomerId)) throw QuotationNotFound();
    }

    // ==================== ERP-400 确定性报价单来源行锁 + 原子事务 ====================

    /// <summary>
    /// 锁内**权威重读**报价单（不复用加锁前的内存实体）：先取报价单来源行锁，再重新加载含明细的权威行，
    /// 并按实时身份复核已存归属 —— 并发改写 / 删除 / 越界一律在改写任何字段之前原子拒绝；
    /// <paramref name="allowSuperseded"/> 为 <c>false</c> 时同时复核「历史版本只读」（ERP-035 既有口径）。
    /// </summary>
    private async Task<Quotation> ReloadLockedAsync(SalespersonDataScope scope, long id, bool allowSuperseded = false)
    {
        if (!await QuotationMutationRules.LockQuotationRowAsync(Db, id))
            throw QuotationNotFound();
        var entity = await Db.Quotations.Include(o => o.Details)
            .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
            ?? throw QuotationNotFound();
        QuotationAuthorizationRules.EnsureStoredCustomerInScope(scope, entity.CustomerId);
        if (!allowSuperseded && await QuotationRevisionService.IsSupersededAsync(Db, id))
            throw BusinessException.RuleConflict(QuotationMutationRules.SupersededText);
        return entity;
    }

    /// <summary>
    /// 在「报价单来源行锁 + 原子事务」内执行一次生命周期变更：授权 → 开事务 → 加锁并锁内权威重读 →
    /// 调用 <paramref name="body"/>（自行 SaveChanges）→ 提交；任一步失败整体回滚并丢弃半成品变更，
    /// 绝不留下撕裂状态或半成品写入。并发令牌过期（RowVersion）转为可读的业务冲突。
    /// </summary>
    private async Task<IActionResult> RunLockedMutationAsync(long id,
        Func<SalespersonDataScope, Quotation, Task<IActionResult>> body, bool allowSuperseded = false)
    {
        var scope = await QuotationAuthorizationRules.EnsureAuthorizedAsync(Db, CurrentUserId());
        await using var transaction = await QuotationMutationRules.BeginMutationTransactionAsync(Db);
        try
        {
            var entity = await ReloadLockedAsync(scope, id, allowSuperseded);
            var result = await body(scope, entity);
            if (transaction is not null) await transaction.CommitAsync();
            return result;
        }
        catch (DbUpdateConcurrencyException)
        {
            await RollbackAsync(transaction);
            throw BusinessException.RuleConflict(QuotationMutationRules.StaleRowVersionText);
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
        QuotationMutationRules.DiscardTrackedChanges(Db);
    }

    /// <summary>是否存在未删除的下游 PI / 销售订单链接（只按持久化外键判定，绝不按自由文本推断）。</summary>
    private Task<bool> HasDownstreamLinkAsync(long quotationId)
        => QuotationMutationRules.HasDownstreamLinkAsync(Db, quotationId);


    /// <summary>分页查询（keyword 匹配单号 / 客户名 / 来源询价单号 / 业务员）</summary>
    [HttpGet]
    public async Task<IActionResult> GetPaged([FromQuery] PageQuery query, [FromQuery] DocumentStatus? status,
        [FromQuery] DateTime? start, [FromQuery] DateTime? end)
    {
        // ERP-400：授权先于计数 / 分页（受限账号范围下推到 SQL，缺失归属 / 越界绝不进入计数）。
        var scope = await QuotationAuthorizationRules.EnsureAuthorizedAsync(Db, CurrentUserId());
        query.Normalize();
        var source = QuotationAuthorizationRules.ApplyScope(
            Set.AsNoTracking().Where(o => !o.IsDeleted), scope);
        if (status.HasValue) source = source.Where(o => o.Status == status.Value);
        if (start.HasValue) source = source.Where(o => o.QuotationDate >= start.Value);
        if (end.HasValue) source = source.Where(o => o.QuotationDate <= end.Value);
        if (!string.IsNullOrWhiteSpace(query.Keyword))
        {
            var kw = query.Keyword;
            source = source.Where(o => o.QuotationNo.Contains(kw) || o.CustomerName.Contains(kw)
                                       || o.InquiryNo.Contains(kw) || o.SalesmanName.Contains(kw));
        }

        var total = await source.CountAsync();
        var items = await source.OrderByDescending(o => o.Id)
            .Skip((query.Page - 1) * query.PageSize).Take(query.PageSize).ToListAsync();
        return Ok(ApiResponse<PagedResult<Quotation>>.Success(new PagedResult<Quotation>
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
        var scope = await QuotationAuthorizationRules.EnsureAuthorizedAsync(Db, CurrentUserId());
        var entity = await Set.AsNoTracking().Include(o => o.Details)
            .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
            ?? throw QuotationNotFound();
        EnsureVisible(scope, entity);
        entity.Details = entity.Details.Where(d => !d.IsDeleted).OrderBy(d => d.SortNo).ToList();
        return Ok(ApiResponse<Quotation>.Success(entity));
    }

    /// <summary>
    /// 创建新版本（ERP-035 多轮议价版本留痕）：把既有报价单整单复制为一张新的**草稿**版本。
    /// </summary>
    /// <remarks>
    /// 规则（服务端唯一入口）：
    /// 1) 源版本必须存在、未删除、未作废；源版本一行都不改，创建后即成为只读历史（见 <see cref="EnsureNotSupersededAsync"/>）；
    /// 2) 版本号由服务端分配（链内单调递增，根 V1），单号 = 根单号 + <c>-R版本号</c>；并发创建同一版本号时
    ///    由数据库唯一索引兜底，返回「请重试」而不会产生重复版本号；
    /// 3) 复制可议价的主表字段与明细，合计经 <see cref="QuotationLineRules.Normalize"/> 服务端复算；
    /// 4) **不复制**审核状态（新版本一律草稿）、下游转换状态与已生成单据链接（PI / 销售订单仍指向被显式选中的版本）。
    /// </remarks>
    [HttpPost("{id:long}/revisions")]
    public async Task<IActionResult> CreateRevision(long id)
    {
        // ERP-400：授权 → 报价单来源行锁 → 锁内权威重读（版本创建允许从历史版本分支，故 allowSuperseded=true）→
        // 既有版本号 / 单号权威分配在同一原子事务内完成，失败整体回滚（源版本一行不改）。
        var scope = await QuotationAuthorizationRules.EnsureAuthorizedAsync(Db, CurrentUserId());
        await using var transaction = await QuotationMutationRules.BeginMutationTransactionAsync(Db);
        try
        {
            var source = await ReloadLockedAsync(scope, id, allowSuperseded: true);
            QuotationRevisionRules.EnsureSourceEligible(source.Status);

            var revision = await QuotationRevisionService.CreateRevisionAsync(Db, source.Id);
            if (transaction is not null) await transaction.CommitAsync();
            return Ok(ApiResponse<QuotationRevisionResult>.Success(new QuotationRevisionResult
            {
                Id = revision.Id,
                QuotationNo = revision.QuotationNo,
                RevisionNumber = revision.RevisionNumber,
                RootQuotationId = revision.RootQuotationId ?? revision.Id,
                RootQuotationNo = revision.RootQuotationNo,
                PreviousRevisionId = revision.PreviousRevisionId ?? 0,
                PreviousRevisionNo = revision.PreviousRevisionNo
            }, $"已创建新版本 {revision.QuotationNo}（草稿，可继续修改）"));
        }
        catch (DbUpdateConcurrencyException)
        {
            await RollbackAsync(transaction);
            throw BusinessException.RuleConflict(QuotationMutationRules.StaleRowVersionText);
        }
        catch
        {
            await RollbackAsync(transaction);
            throw;
        }
    }

    /// <summary>
    /// 版本链（ERP-035）：返回该报价单所属版本链的**完整**列表（根单 + 全部历史版本，按版本号升序）。
    /// </summary>
    /// <remarks>
    /// 每行含版本号、根单（Id / 单号）、上一版本（Id / 单号）、是否最新版本、是否已被后续版本取代（只读历史）、
    /// 是否已转出（已转 PI / 已转销售订单，口径与成交率报表一致）与状态 / 金额；
    /// 历史报价单（无版本元数据）按初始版本 V1 返回，且不做任何写库 / 回填。
    /// </remarks>
    [HttpGet("{id:long}/revisions")]
    public async Task<IActionResult> GetRevisions(long id)
    {
        var scope = await QuotationAuthorizationRules.EnsureAuthorizedAsync(Db, CurrentUserId());
        var selected = await Set.AsNoTracking()
            .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
            ?? throw QuotationNotFound();
        EnsureVisible(scope, selected);

        // ERP-400：链内每一张版本都必须落在实时范围内（存在一个可见版本不得泄露范围外版本的金额 / 状态）。
        var rootId = selected.RootQuotationId ?? selected.Id;
        var chainCustomerIds = await Db.Quotations.AsNoTracking()
            .Where(o => !o.IsDeleted && (o.Id == rootId || o.RootQuotationId == rootId))
            .Select(o => o.CustomerId).ToListAsync();
        QuotationAuthorizationRules.EnsureChainCustomerInScope(scope, chainCustomerIds);

        var chain = await QuotationRevisionService.LoadChainAsync(Db, selected);
        return Ok(ApiResponse<List<QuotationRevisionChainItem>>.Success(chain,
            $"版本链共 {chain.Count} 个版本（根单：{chain.FirstOrDefault()?.RootQuotationNo}）"));
    }

    /// <summary>按询价单带出客户与明细（新建报价单时用「带入询价明细」）</summary>
    [HttpGet("from-inquiry/{inquiryId:long}")]
    public async Task<IActionResult> FromInquiry(long inquiryId)
    {
        // ERP-400：带入预填同样先实时授权，并按权威归属复核询价单客户在实时范围内（fail closed）。
        var scope = await QuotationAuthorizationRules.EnsureAuthorizedAsync(Db, CurrentUserId());
        var inquiry = await Db.Inquiries.AsNoTracking().Include(o => o.Details)
            .FirstOrDefaultAsync(o => o.Id == inquiryId && !o.IsDeleted)
            ?? throw BusinessException.NotFound("询价单不存在");
        QuotationAuthorizationRules.EnsureProposedCustomerInScope(scope, inquiry.CustomerId);

        BaseCustomer? customer = null;
        if (inquiry.CustomerId > 0)
            customer = await Db.BaseCustomers.AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == inquiry.CustomerId && !c.IsDeleted);

        var result = new
        {
            inquiry.Id,
            inquiry.InquiryNo,
            inquiry.CustomerId,
            CustomerName = customer?.CustomerName ?? string.Empty,
            ContactPerson = string.IsNullOrWhiteSpace(inquiry.ContactPerson) ? customer?.ContactPerson : inquiry.ContactPerson,
            ContactPhone = string.IsNullOrWhiteSpace(inquiry.ContactPhone) ? customer?.Phone : inquiry.ContactPhone,
            ContactEmail = customer?.Email ?? string.Empty,
            TradeTerms = customer?.TradeTerms ?? string.Empty,
            PortOfDestination = customer?.DestinationPort ?? string.Empty,
            PaymentTerms = customer?.PaymentTerms ?? string.Empty,
            inquiry.Currency,
            inquiry.ExchangeRate,
            inquiry.ValidDays,
            Details = inquiry.Details.Where(d => !d.IsDeleted).OrderBy(d => d.Id).Select((d, i) => new
            {
                SortNo = i + 1,
                d.ProductId,
                d.ProductName,
                d.Spec,
                d.Unit,
                d.Quantity,
                d.UnitPrice,
                d.Amount,
                d.Remark,
                Moq = string.Empty
            }).ToList()
        };
        return Ok(ApiResponse<object>.Success(result));
    }

    /// <summary>
    /// 提交（ERP-400：报价单来源行锁 + 原子事务内锁内权威重读后 草稿 → 已提交；
    /// ERP-035：已被后续版本取代的历史版本只读；存在下游链接的已完成报价单一律冻结）。
    /// </summary>
    [HttpPost("{id:long}/submit")]
    public override async Task<IActionResult> Submit(long id)
        => await RunLockedMutationAsync(id, async (_, entity) =>
        {
            QuotationMutationRules.EnsureNoDownstreamLink(await HasDownstreamLinkAsync(id));
            QuotationMutationRules.EnsureSubmitAllowed(GetStatus(entity));
            SetStatus(entity, DocumentStatus.Submitted);
            await Db.SaveChangesAsync();
            return Ok(ApiResponse<object>.Success(null, "提交成功"));
        });

    /// <summary>
    /// 审核（ERP-400：报价单来源行锁 + 原子事务内锁内权威重读后审核；草稿可直接审核，也支持提交后审核；
    /// 无有效明细不允许审核；历史版本只读；存在下游链接的已完成报价单一律冻结）。
    /// </summary>
    [HttpPost("{id:long}/approve")]
    public override async Task<IActionResult> Approve(long id)
        => await RunLockedMutationAsync(id, async (_, entity) =>
        {
            QuotationMutationRules.EnsureNoDownstreamLink(await HasDownstreamLinkAsync(id));
            QuotationMutationRules.EnsureApproveAllowed(GetStatus(entity),
                QuotationMutationRules.ActiveDetailCount(entity));
            SetStatus(entity, DocumentStatus.Approved);
            await Db.SaveChangesAsync();
            return Ok(ApiResponse<object>.Success(null, "报价单已审核"));
        });

    /// <summary>销审（ERP-400：报价单来源行锁 + 原子事务内锁内权威重读后退回草稿，可继续修改；历史版本只读）</summary>
    [HttpPost("{id:long}/unaudit")]
    public async Task<IActionResult> Unaudit(long id)
        => await RunLockedMutationAsync(id, async (_, entity) =>
        {
            QuotationMutationRules.EnsureNoDownstreamLink(await HasDownstreamLinkAsync(id));
            QuotationMutationRules.EnsureUnauditAllowed(GetStatus(entity));
            SetStatus(entity, DocumentStatus.Pending);
            await Db.SaveChangesAsync();
            return Ok(ApiResponse<object>.Success(null, "已销审，可继续修改"));
        });

    /// <summary>取消（ERP-400：报价单来源行锁 + 原子事务内锁内权威重读后取消；已链接报价单一律冻结）</summary>
    [HttpPost("{id:long}/cancel")]
    public override async Task<IActionResult> Cancel(long id)
        => await RunLockedMutationAsync(id, async (_, entity) =>
        {
            QuotationMutationRules.EnsureNoDownstreamLink(await HasDownstreamLinkAsync(id));
            QuotationMutationRules.EnsureCancelAllowed(GetStatus(entity));
            SetStatus(entity, DocumentStatus.Cancelled);
            await Db.SaveChangesAsync();
            return Ok(ApiResponse<object>.Success(null, "已取消"));
        });

    /// <summary>
    /// 作废（ERP-400：报价单来源行锁 + 原子事务内锁内权威重读后作废；已转 PI / 已转销售订单 / 已链接报价单不可作废，
    /// 重复作废给出明确提示）。绝不通过取消目标单据做反向冲销。
    /// </summary>
    [HttpPost("{id:long}/void")]
    public async Task<IActionResult> Void(long id)
        => await RunLockedMutationAsync(id, async (_, entity) =>
        {
            QuotationMutationRules.EnsureNoDownstreamLink(await HasDownstreamLinkAsync(id));
            QuotationMutationRules.EnsureVoidAllowed(GetStatus(entity));
            SetStatus(entity, DocumentStatus.Cancelled);
            await Db.SaveChangesAsync();
            return Ok(ApiResponse<object>.Success(null, "报价单已作废"));
        });

    /// <summary>删除（软删除，仅待提交状态可删；ERP-400：报价单来源行锁 + 原子事务内锁内权威重读后软删除）</summary>
    [HttpDelete("{id:long}")]
    public override async Task<IActionResult> Delete(long id)
        => await RunLockedMutationAsync(id, async (_, entity) =>
        {
            QuotationMutationRules.EnsureNoDownstreamLink(await HasDownstreamLinkAsync(id));
            QuotationMutationRules.EnsureDeleteAllowed(GetStatus(entity));
            entity.IsDeleted = true;
            entity.UpdatedAt = DateTime.Now;
            await Db.SaveChangesAsync();
            return Ok(ApiResponse<object>.Success(null, "删除成功"));
        });

    /// <summary>
    /// 批量软删除（ERP-400）：授权后<strong>按报价单 Id 升序确定性加锁</strong>（与单行生命周期路由
    /// 共用同一把报价单来源行锁），锁内权威重读并逐行复核归属 / 下游 PI 或销售订单链接 / 状态，
    /// 混合允许 / 越界 / 无主批次<b>整体拒绝</b>、不做部分删除；全部通过后才在同一原子事务内一次性软删除。
    /// </summary>
    [HttpPost("batch-delete")]
    public async Task<IActionResult> BatchDelete([FromBody] List<long> ids)
    {
        var scope = await QuotationAuthorizationRules.EnsureAuthorizedAsync(Db, CurrentUserId());
        var requested = QuotationMutationRules.MergeLockIds(ids);
        if (requested.Count == 0)
            throw BusinessException.InvalidParameter("请指定要删除的报价单");

        await using var transaction = await QuotationMutationRules.BeginMutationTransactionAsync(Db);
        try
        {
            // 锁序：全部报价单来源行按 Id 升序确定性加锁（去重、仅正整数；绝不反向获取其它锁）。
            await QuotationMutationRules.LockQuotationRowsAsync(Db, requested);

            // 锁内权威重读：存在性 / 归属 / 下游链接 / 状态逐行复核，任一行不满足即整体拒绝（无部分删除）。
            var entities = await Db.Quotations
                .Where(o => requested.Contains(o.Id) && !o.IsDeleted).ToListAsync();
            var found = entities.Select(o => o.Id).ToHashSet();
            var missing = requested.Where(id => !found.Contains(id)).ToList();
            if (missing.Count > 0)
                throw BusinessException.NotFound(
                    $"批量删除的报价单不存在或已删除：{string.Join("、", missing)}（未做任何部分删除）");

            foreach (var quotation in entities)
            {
                QuotationAuthorizationRules.EnsureStoredCustomerInScope(scope, quotation.CustomerId);
                QuotationMutationRules.EnsureNoDownstreamLink(await HasDownstreamLinkAsync(quotation.Id));
            }

            QuotationMutationRules.EnsureBatchDeleteAllowed(entities.Select(GetStatus));

            foreach (var entity in entities)
            {
                entity.IsDeleted = true;
                entity.UpdatedAt = DateTime.Now;
            }
            await Db.SaveChangesAsync();
            if (transaction is not null) await transaction.CommitAsync();
            return Ok(ApiResponse<object>.Success(null, $"已删除 {entities.Count} 张报价单"));
        }
        catch (DbUpdateConcurrencyException)
        {
            await RollbackAsync(transaction);
            throw BusinessException.RuleConflict(QuotationMutationRules.StaleRowVersionText);
        }
        catch
        {
            await RollbackAsync(transaction);
            throw;
        }
    }

    /// <summary>创建（单号缺省由字轨生成；行号/金额/合计后端复核）</summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] Quotation entity)
    {
        // ERP-400：授权与拟议客户范围校验先于单号生成与任何写入（被拒绝的调用方绝不消耗单据号）。
        var scope = await QuotationAuthorizationRules.EnsureAuthorizedAsync(Db, CurrentUserId());
        QuotationAuthorizationRules.EnsureProposedCustomerInScope(scope, entity.CustomerId);
        entity.Id = 0;
        if (string.IsNullOrWhiteSpace(entity.QuotationNo))
            entity.QuotationNo = await _noService.GenerateAsync(DocumentType.Quotation);
        entity.Status = DocumentStatus.Pending;
        entity.CreatedAt = DateTime.Now;
        /* ERP-035：手工新建的报价单不携带任何版本元数据 → 库默认值即初始版本 V1（不做回填、不写映射） */
        QuotationLineRules.Normalize(entity);
        Db.Quotations.Add(entity);
        await Db.SaveChangesAsync();
        return Ok(ApiResponse<object>.Success(new { entity.Id, entity.QuotationNo }, "报价单创建成功"));
    }

    /// <summary>
    /// 修改（ERP-400：报价单来源行锁 + 原子事务内锁内权威重读后改写；明细整体替换）。
    /// 已审核 / 已转订单 / 已作废不可改；已被后续版本取代的历史版本只读；
    /// 存在下游 PI / 销售订单链接的报价单一律冻结（保留显式历史，不做反向冲销）。
    /// </summary>
    [HttpPut("{id:long}")]
    public async Task<IActionResult> Update(long id, [FromBody] Quotation entity)
        => await RunLockedMutationAsync(id, async (scope, existing) =>
        {
            QuotationAuthorizationRules.EnsureProposedCustomerInScope(scope, entity.CustomerId);
            QuotationMutationRules.EnsureNoDownstreamLink(await HasDownstreamLinkAsync(id));
            QuotationMutationRules.EnsureEditAllowed(GetStatus(existing));
            ApplyUpdate(existing, entity, id);
            await Db.SaveChangesAsync();
            return Ok(ApiResponse<object>.Success(null, "报价单更新成功"));
        });

    /// <summary>把请求体字段写入锁内权威实体（明细整体替换，单号 / 版本元数据 / 主键不可被客户端改写）。</summary>
    private void ApplyUpdate(Quotation existing, Quotation entity, long id)
    {
        existing.QuotationDate = entity.QuotationDate;
        existing.ValidUntil = entity.ValidUntil;
        existing.CustomerId = entity.CustomerId;
        existing.CustomerName = entity.CustomerName;
        existing.ContactPerson = entity.ContactPerson;
        existing.ContactPhone = entity.ContactPhone;
        existing.ContactEmail = entity.ContactEmail;
        existing.InquiryId = entity.InquiryId;
        existing.InquiryNo = entity.InquiryNo;
        existing.TradeTerms = entity.TradeTerms;
        existing.PortOfLoading = entity.PortOfLoading;
        existing.PortOfDestination = entity.PortOfDestination;
        existing.PaymentTerms = entity.PaymentTerms;
        existing.LeadTime = entity.LeadTime;
        existing.Currency = entity.Currency;
        existing.ExchangeRate = entity.ExchangeRate;
        existing.SalesmanId = entity.SalesmanId;
        existing.SalesmanName = entity.SalesmanName;
        existing.Remark = entity.Remark;

        Db.QuotationDetails.RemoveRange(existing.Details);
        entity.QuotationNo = existing.QuotationNo;
        entity.Id = id;
        QuotationLineRules.Normalize(entity);
        existing.Details = entity.Details;
        existing.TotalAmount = entity.TotalAmount;
        existing.TotalAmountCny = entity.TotalAmountCny;
        existing.UpdatedAt = DateTime.Now;
    }

    /// <summary>
    /// 转为 PI（ERP-400）：在「报价单来源行锁 + 原子事务」内按已审核报价单生成一张 PI，
    /// 并回填来源报价单、把原报价单状态改为「已转 PI」（Completed）。
    /// </summary>
    /// <remarks>
    /// 转换规则（唯一入口，防重复）：
    /// 1) 授权：既有「报价单」+「形式发票 PI」菜单授权，且来源 / 目标客户都在实时范围内；
    /// 2) 报价单必须已审核（草稿 / 已提交先审核），已作废、已完成与已生成 PI 均被拒绝；
    /// 3) 同一报价单只允许生成一张 PI（即使状态被人工改回，也由 PI.QuotationId 兜底拦截）；
    /// 4) 银行信息取系统参数 PI_BankInfo 默认值，收货人 / 通知人 / 唛头取客户资料默认值，PI 上均可再改；
    /// 5) 任一失败整体回滚（不消耗单据号、不落半成品 PI、不改来源状态）。
    /// </remarks>
    [HttpPost("{id:long}/to-pi")]
    public async Task<IActionResult> ToProformaInvoice(long id)
    {
        var scope = await QuotationAuthorizationRules
            .EnsureProformaInvoiceConversionAuthorizedAsync(Db, CurrentUserId());
        await using var transaction = await QuotationMutationRules.BeginMutationTransactionAsync(Db);
        try
        {
            // ERP-400：先取报价单来源行锁，再锁内权威重读（生命周期 / 明细都以持久化行为准）。
            var quotation = await ReloadLockedAsync(scope, id, allowSuperseded: true);
            QuotationAuthorizationRules.EnsureSourceCustomerInScope(scope, quotation.CustomerId);

            // 锁内转换资格 + 既有来源 PI 复核（重复生成唯一化：同一报价单至多一张完整 PI）。
            var existingPi = await QuotationMutationRules.FindDownstreamPiAsync(Db, id);
            QuotationMutationRules.EnsureProformaInvoiceConversionEligible(quotation, existingPi,
                QuotationMutationRules.ActiveDetailCount(quotation));

            var pi = await BuildProformaInvoiceDraftAsync(quotation);
            QuotationAuthorizationRules.EnsureTargetCustomerInScope(scope, pi.CustomerId);
            SalesOrderConversion.EnsureQuotationConversionScopeAuthorized(scope, quotation.CustomerId, pi.CustomerId);

            pi.PiNo = await _noService.GenerateAsync(DocumentType.ProformaInvoice);
            ProformaInvoiceController.Normalize(pi);
            Db.ProformaInvoices.Add(pi);
            SetStatus(quotation, DocumentStatus.Completed);   // 报价单 →「已转 PI」
            await Db.SaveChangesAsync();
            if (transaction is not null) await transaction.CommitAsync();
            return Ok(ApiResponse<object>.Success(new { pi.Id, pi.PiNo, QuotationNo = quotation.QuotationNo },
                "已生成形式发票 PI"));
        }
        catch (DbUpdateConcurrencyException)
        {
            await RollbackAsync(transaction);
            throw BusinessException.RuleConflict(QuotationMutationRules.StaleRowVersionText);
        }
        catch
        {
            await RollbackAsync(transaction);
            throw;
        }
    }

    /// <summary>
    /// 构造 PI 草稿（未落库、无单号）：复制报价单可议价字段与明细，并带入客户档案 / 系统参数默认值。
    /// 单号与合计由调用方在锁内按权威口径生成 / 复算，绝不信任客户端。
    /// </summary>
    private async Task<ProformaInvoice> BuildProformaInvoiceDraftAsync(Quotation quotation)
    {
        BaseCustomer? customer = null;
        if (quotation.CustomerId > 0)
            customer = await Db.BaseCustomers.AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == quotation.CustomerId && !c.IsDeleted);
        var bankInfo = await Db.SysParameters.AsNoTracking()
            .Where(p => !p.IsDeleted && p.ParamKey == "PI_BankInfo")
            .Select(p => p.ParamValue).FirstOrDefaultAsync() ?? string.Empty;

        return new ProformaInvoice
        {
            PiNo = string.Empty,
            PiDate = DateTime.Today,
            QuotationId = quotation.Id,
            QuotationNo = quotation.QuotationNo,
            CustomerId = quotation.CustomerId,
            CustomerName = quotation.CustomerName,
            ContactPerson = quotation.ContactPerson,
            ContactPhone = quotation.ContactPhone,
            ContactEmail = quotation.ContactEmail,
            Consignee = customer?.Consignee ?? string.Empty,
            NotifyParty = customer?.NotifyParty ?? string.Empty,
            ShippingMarks = customer?.DefaultShippingMark ?? string.Empty,
            BankInfo = bankInfo,
            TradeTerms = quotation.TradeTerms,
            PortOfLoading = quotation.PortOfLoading,
            PortOfDestination = quotation.PortOfDestination,
            PaymentTerms = quotation.PaymentTerms,
            LeadTime = quotation.LeadTime,
            Currency = quotation.Currency,
            ExchangeRate = quotation.ExchangeRate,
            // 定金比例：优先客户资料约定值，未维护时按外贸惯例 30%
            DepositRatio = customer is { DepositRatio: > 0 } ? customer.DepositRatio : 30m,
            SalesmanId = quotation.SalesmanId,
            SalesmanName = quotation.SalesmanName,
            Status = DocumentStatus.Pending,
            Remark = quotation.Remark,
            CreatedAt = DateTime.Now,
            Details = quotation.Details.Where(d => !d.IsDeleted).OrderBy(d => d.SortNo)
                .Select(d => new ProformaInvoiceDetail
                {
                    ProductId = d.ProductId,
                    ProductCode = d.ProductCode,
                    ProductName = d.ProductName,
                    Spec = d.Spec,
                    Unit = d.Unit,
                    Quantity = d.Quantity,
                    UnitPrice = d.UnitPrice,
                    Moq = d.Moq,
                    Remark = d.Remark,
                    CreatedAt = DateTime.Now
                }).ToList()
        };
    }

    /// <summary>
    /// 带入预填销售订单（ERP-010 / ERP-400）：按报价单返回一张**未落库**的销售订单草稿，
    /// 前端据此打开「销售订单 → 新增」表单继续编辑后再保存（保存走 <c>POST /api/sales-orders</c>，服务端复核数量 / 单价 / 合计）。
    /// 授权要求既有「报价单」+「销售订单」菜单，且来源 / 目标客户都在实时范围内；
    /// 只允许已审核报价单，已作废 / 已转 PI / 已生成销售订单均被拒绝，本接口不占用单据号、不写库。
    /// </summary>
    [HttpGet("{id:long}/order-prefill")]
    public async Task<IActionResult> OrderPrefill(long id)
    {
        var scope = await QuotationAuthorizationRules
            .EnsureSalesOrderConversionAuthorizedAsync(Db, CurrentUserId());
        var quotation = await Db.Quotations.AsNoTracking().Include(o => o.Details)
            .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
            ?? throw QuotationNotFound();
        EnsureVisible(scope, quotation);
        QuotationAuthorizationRules.EnsureSourceCustomerInScope(scope, quotation.CustomerId);

        var order = await SalesOrderConversion.FromQuotationAsync(Db, quotation);
        QuotationAuthorizationRules.EnsureTargetCustomerInScope(scope, order.CustomerId);
        SalesOrderConversion.EnsureQuotationConversionScopeAuthorized(scope, quotation.CustomerId, order.CustomerId);
        return Ok(ApiResponse<SalesOrderPrefillResult>.Success(new SalesOrderPrefillResult
        {
            SourceType = SalesOrderConversion.QuotationSourceType,
            SourceId = quotation.Id,
            SourceNo = quotation.QuotationNo,
            Order = order
        }, "已按报价单带入销售订单草稿"));
    }

    /// <summary>
    /// 转为销售订单（ERP-010 / ERP-400）：在「报价单来源行锁 + 原子事务」内按已审核报价单生成一张销售订单
    /// （EF 主子表路径，不走旧版存储过程）。
    /// 授权要求既有「报价单」+「销售订单」菜单，且来源 / 目标客户都在实时范围内；
    /// 同一报价单仅生成一张（以销售订单的来源字段为准），只新增单据、绝不覆盖既有订单；
    /// 生成后报价单状态置「已完成」（已转 PI 或已转销售订单）；任一失败整体回滚。
    /// </summary>
    [HttpPost("{id:long}/to-order")]
    public async Task<IActionResult> ToSalesOrder(long id)
    {
        var scope = await QuotationAuthorizationRules
            .EnsureSalesOrderConversionAuthorizedAsync(Db, CurrentUserId());
        await using var transaction = await QuotationMutationRules.BeginMutationTransactionAsync(Db);
        try
        {
            // ERP-400：先取报价单来源行锁，再锁内权威重读（生命周期 / 明细都以持久化行为准）。
            var quotation = await ReloadLockedAsync(scope, id, allowSuperseded: true);
            QuotationAuthorizationRules.EnsureSourceCustomerInScope(scope, quotation.CustomerId);

            // 锁内转换资格 + 既有来源订单 / PI 复核（重复生成唯一化：同一报价单至多一张完整销售订单）。
            var existingOrder = await QuotationMutationRules.FindDownstreamOrderAsync(Db, id);
            var existingPi = await QuotationMutationRules.FindDownstreamPiAsync(Db, id);
            QuotationMutationRules.EnsureSalesOrderConversionEligible(quotation, existingOrder, existingPi,
                QuotationMutationRules.ActiveDetailCount(quotation));

            var order = await SalesOrderConversion.FromQuotationAsync(Db, quotation);
            QuotationAuthorizationRules.EnsureTargetCustomerInScope(scope, order.CustomerId);
            SalesOrderConversion.EnsureQuotationConversionScopeAuthorized(scope, quotation.CustomerId, order.CustomerId);
            // 落库订单必须与本次锁内权威重读完全一致（数量 / 币种 / 汇率 / 合计 / 定金 / 显式 SourceQuotationId）。
            SalesOrderConversion.EnsureQuotationDraftMatchesSource(quotation, order);

            order.OrderNo = await _noService.GenerateAsync(DocumentType.SalesOrder);
            Db.SalesOrders.Add(order);
            SetStatus(quotation, DocumentStatus.Completed);
            await Db.SaveChangesAsync();
            if (transaction is not null) await transaction.CommitAsync();
            return Ok(ApiResponse<SalesOrderConversionResult>.Success(new SalesOrderConversionResult
            {
                Id = order.Id,
                OrderNo = order.OrderNo,
                SourceNo = quotation.QuotationNo
            }, "已生成销售订单"));
        }
        catch (DbUpdateConcurrencyException)
        {
            await RollbackAsync(transaction);
            throw BusinessException.RuleConflict(QuotationMutationRules.StaleRowVersionText);
        }
        catch
        {
            await RollbackAsync(transaction);
            throw;
        }
    }

    /// <summary>
    /// 打印数据（主表 + 有效明细，按行号排序）：与「报价单工作流」完全同一份持久化数据
    /// （单号 / 客户 / 条款 / 明细行号 · 数量 · 单价 · 金额 / 合计与折人民币 / 有效期），
    /// 打印预览、直接打印与打印设计共用本端点，服务端**不重算、不落库**，
    /// 保证打印件与页面显示逐字一致；明细只取未删除行并按 `SortNo` 排序，避免软删除行进入打印件。
    /// </summary>
    [HttpGet("{id:long}/print")]
    public async Task<IActionResult> GetPrint(long id)
    {
        var scope = await QuotationAuthorizationRules.EnsureAuthorizedAsync(Db, CurrentUserId());
        var entity = await Set.AsNoTracking().Include(o => o.Details)
            .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
            ?? throw QuotationNotFound();
        EnsureVisible(scope, entity);
        entity.Details = entity.Details.Where(d => !d.IsDeleted).OrderBy(d => d.SortNo).ToList();
        return Ok(ApiResponse<Quotation>.Success(entity));
    }

    /// <summary>
    /// 报价单有效期到期提醒（ERP-018）：列出「已过期」与「提醒窗口内即将到期」的报价单，
    /// 已作废单据不提醒；已转出（已转 PI / 已转销售订单）的单据仍会列出但以 <c>Converted</c> 标记，
    /// 便于业务员判断是否还需催单。判定口径见 <see cref="QuotationValidityRules"/>，只读现有
    /// <see cref="Quotation.ValidUntil"/> 字段，**不新增任何数据库结构**。
    /// </summary>
    /// <param name="asOfDate">判定基准日（默认今天）</param>
    /// <param name="aheadDays">提醒窗口天数（默认 7 天；负数按默认值处理）</param>
    [HttpGet("validity-due")]
    public async Task<IActionResult> ValidityDue([FromQuery] DateTime? asOfDate,
        [FromQuery] int aheadDays = QuotationValidityRules.DefaultAheadDays)
    {
        var asOf = (asOfDate ?? DateTime.Today).Date;
        var window = QuotationValidityRules.NormalizeAheadDays(aheadDays);

        // ERP-400：有效期提醒同样先实时授权，并把客户范围下推到 SQL（绝不泄露范围外报价单）。
        var scope = await QuotationAuthorizationRules.EnsureAuthorizedAsync(Db, CurrentUserId());
        var items = await QuotationAuthorizationRules.ApplyScope(Set.AsNoTracking(), scope)
            .Where(o => !o.IsDeleted && o.Status != DocumentStatus.Cancelled
                        && o.ValidUntil != null && o.ValidUntil <= asOf.AddDays(window))
            .ToListAsync();

        var converted = await LoadConvertedQuotationIdsAsync(items.Select(o => o.Id).ToList());

        var result = items
            .OrderByDescending(o => QuotationValidityRules.UrgencyOf(o.ValidUntil, asOf, window))
            .ThenBy(o => o.ValidUntil)
            .ThenBy(o => o.Id)
            .Select(o => new QuotationValidityItem
            {
                Id = o.Id,
                QuotationNo = o.QuotationNo,
                CustomerName = o.CustomerName,
                SalesmanName = o.SalesmanName,
                QuotationDate = o.QuotationDate,
                ValidUntil = o.ValidUntil,
                ValidDays = QuotationValidityRules.DaysRemaining(o.ValidUntil, asOf),
                ValidityStatus = QuotationValidityRules.StatusOf(o.ValidUntil, asOf, window),
                ValidityLevel = QuotationValidityRules.LevelOf(o.ValidUntil, asOf, window),
                TotalAmount = o.TotalAmount,
                TotalAmountCny = o.TotalAmountCny,
                Currency = o.Currency,
                Status = o.Status,
                Converted = converted.Contains(o.Id) || o.Status == DocumentStatus.Completed
            })
            .ToList();

        return Ok(ApiResponse<List<QuotationValidityItem>>.Success(result));
    }

    /// <summary>
    /// 已转出报价单 Id 集合（来源外键：已转 PI / 已转销售订单；口径与成交率报表一致）。
    /// ERP-035 起统一由 <see cref="QuotationRevisionService.LoadConvertedQuotationIdsAsync"/> 提供，
    /// 版本链与有效期提醒共用同一口径（不会因为存在新版本而把转换状态挂到别的版本上）。
    /// </summary>
    private Task<HashSet<long>> LoadConvertedQuotationIdsAsync(List<long> quotationIds)
        => QuotationRevisionService.LoadConvertedQuotationIdsAsync(Db, quotationIds);
}
