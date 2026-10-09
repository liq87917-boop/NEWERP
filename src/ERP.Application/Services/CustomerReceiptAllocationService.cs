using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ERP.Application.Services;

/// <summary>
/// 客户收款单 → 销售订单 收款引用（分摊）证据登记服务（ERP-053）。职责：
/// <list type="number">
/// <item><b>登记引用行</b>（<see cref="CreateAsync"/>）：收款单必须存在且未删除，销售订单必须存在、未删除、
/// 未取消，且订单客户与币种都必须与收款单**权威一致**；引用金额按币种精度取整且大于 0，
/// 同一收款单内两套有效分摊行（含「收款单 → 代理服务费对账单」）合计不得超过收款单金额，
/// 同一订单在同一收款单内不得重复（有效行）；</item>
/// <item><b>作废引用行</b>（<see cref="VoidAsync"/>）：必须填写原因，保留原始金额 / 快照 / 审计历史，
/// 不物理删除、不静默替换、不重写已作废证据；</item>
/// <item><b>台账与汇总读取</b>（<see cref="ListAsync"/> / <see cref="GetAsync"/> /
/// <see cref="ListForReceiptAsync"/> / <see cref="GetReceiptSummaryAsync"/>）：分页 / 有界、批量装载，
/// 无逐行数据库查询；</item>
/// <item><b>候选读取</b>（<see cref="ListReceiptCandidatesAsync"/> / <see cref="ListOrderCandidatesAsync"/>）：
/// 只读、有界，含资格文案与只读派生金额（绝不写入、绝不猜测订单）。</item>
/// </list>
/// <para>边界（重要）：本服务只读写 <c>CustomerReceiptAllocations</c> 一张表，<strong>不</strong>改写收款单的
/// 审批 / 执行状态、金额、币种、付款方式、银行账户、客户或备注，<strong>不</strong>改写销售订单状态、出货进度、
/// 金额与明细、交期与合同字段，<strong>不</strong>改写客户信用状态与信用额度、发票记录、库存与库存成本、
/// 库存流水、装柜与单证、佣金 / 回佣、费用与退税记录，也<strong>不</strong>执行任何收款、记账、核销、结算或催收动作。</para>
/// <para>ERP-383 锁序审计：本服务只取<b>收款单行锁</b>（ERP-383 唯一全局锁序第 3 段：来源销售订单行 → 客户销项发票行 →
/// 客户收款单行 → 引用 / 分摊 / 证据行），在登记 / 显式作废时先取收款单行锁再读写引用行；本服务<strong>不</strong>获取
/// 上游销售订单行 / 客户销项发票行锁，因此与 ERP-347 销售订单取消、ERP-383 客户销项发票模块都不构成反向加锁。</para>
/// </summary>
public static class CustomerReceiptAllocationService
{
    // ==================== 1. 登记引用行（新增一条分摊证据） ====================

    /// <summary>
    /// 登记一条收款引用行：全部校验通过后才写一行证据，并写入收款单 / 客户 / 销售订单的服务端快照。
    /// <para>校验顺序：收款单可用 → 币种口径 → 引用金额（精度 + 大于 0）→ 备注 → 销售订单资格
    /// （存在 / 未删除 / 未取消 / 客户一致 / 币种一致）→ 单收款单行数上限 → 重复有效行 →
    /// 同一收款单的唯一同币种分摊额度上限（两套证据合计，ERP-350）。</para>
    /// </summary>
    public static async Task<CustomerReceiptAllocationDto> CreateAsync(
        IErpDbContext db, CustomerReceiptAllocationSaveDto dto)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(dto);

        if (dto.ReceiptId <= 0) throw BusinessException.InvalidParameter("请选择要引用的收款单");
        if (dto.SalesOrderId <= 0) throw BusinessException.InvalidParameter("请选择要引用的销售订单");

        // 与收款单生命周期（取消 / 修改 / 删除）互斥：在同一事务内先对收款单行加排它行锁，
        // 再读权威金额与状态，避免「登记证据」与「收款单被取消 / 改金额」并发竞态。
        await using var transaction = CustomerReceiptLifecycleRules.IsRelationalProvider(db)
            ? await db.Database.BeginTransactionAsync()
            : null;
        try
        {
            await CustomerReceiptLifecycleRules.LockReceiptRowAsync(db, dto.ReceiptId);

            var receipt = await LoadReceiptAsync(db, dto.ReceiptId);
            if (receipt.Status == DocumentStatus.Cancelled)
                throw BusinessException.RuleConflict(
                    $"收款单「{receipt.ReceiptNo}」已取消，不能登记新的收款引用（历史引用仍可读，不再新增）");

            var currency = CustomerReceiptAllocationRules.NormalizeCurrencyStrict(receipt.Currency.ToString());
            var amount = CustomerReceiptAllocationRules.NormalizeAllocationAmount(dto.AllocatedAmount, currency);
            var remark = CustomerReceiptAllocationRules.NormalizeRemark(dto.Remark);

        var order = await db.SalesOrders.AsNoTracking()
            .FirstOrDefaultAsync(o => o.Id == dto.SalesOrderId);
        CustomerReceiptAllocationRules.EnsureOrderLinkable(receipt.CustomerId, currency, order);
        if (order is null) throw BusinessException.NotFound("销售订单不存在或已删除，不能引用");

        // 有效行（未作废、未删除）：重复、行数与金额上限都以它为准；作废行保留历史但不再占用额度
        var activeRows = await db.CustomerReceiptAllocations.AsNoTracking()
            .Where(a => !a.IsDeleted && a.ReceiptId == receipt.Id
                        && a.Status == CustomerReceiptAllocationRules.StatusActive)
            .ToListAsync();

        if (activeRows.Count >= CustomerReceiptAllocationRules.MaxAllocationsPerReceipt)
            throw BusinessException.RuleConflict(
                $"收款单「{receipt.ReceiptNo}」的有效引用行已达上限 "
                + $"{CustomerReceiptAllocationRules.MaxAllocationsPerReceipt} 条："
                + "如需新增请先作废不需要的行（作废保留历史，不物理删除）");

        var duplicate = activeRows.FirstOrDefault(a => a.SalesOrderId == order.Id);
        if (duplicate is not null)
            throw BusinessException.Duplicate(
                $"收款单「{receipt.ReceiptNo}」已存在指向销售订单「{order.OrderNo}」的有效引用行"
                + $"（Id={duplicate.Id}，金额 {duplicate.AllocatedAmount} {duplicate.Currency}）："
                + "同一订单在同一收款单内只能有一条有效引用行（如需更正请先作废原行，再登记新行；作废保留历史）");

        var receiptAmount = CustomerReceiptAllocationRules.AuthoritativeReceiptAmount(receipt.Amount, currency);
        // 唯一、同币种分摊额度（ERP-350）：同一收款单的「收款单 → 销售订单」与「收款单 → 代理服务费对账单」
        // 两套有效分摊行在收款单行锁下共同占用同一额度，因此这里取两套合计（绝不跨币种、绝不重复计算）。
        var funding = await CustomerReceiptLifecycleRules.LoadReceiptFundingAsync(db, receipt.Id);
        var allocated = funding.CombinedAllocated;
        var total = allocated + amount;
        if (total > receiptAmount)
            throw BusinessException.RuleConflict(
                $"收款单「{receipt.ReceiptNo}」的引用金额合计 {total} 超过收款单金额 {receiptAmount} {currency}"
                + $"（已引用 {allocated}，其中「收款单 → 销售订单」{funding.CustomerOrderAllocated}，"
                + $"「收款单 → 代理服务费对账单」{funding.AgencyAllocated}；本次 {amount}）：请调整引用金额"
                + "（收款单允许部分或全部未被引用，未引用部分保持为未引用金额）");

        var customer = await db.BaseCustomers.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == receipt.CustomerId);

        var row = new CustomerReceiptAllocation
        {
            ReceiptId = receipt.Id,
            ReceiptNo = receipt.ReceiptNo ?? string.Empty,
            ReceiptDate = receipt.ReceiptDate,
            ReceiptStatus = (int)receipt.Status,
            ReceiptStatusText = CustomerReceiptAllocationRules.ReceiptStatusText((int)receipt.Status),
            ReceiptAmount = receiptAmount,
            SalesOrderId = order.Id,
            OrderNo = order.OrderNo ?? string.Empty,
            OrderDate = order.OrderDate,
            OrderStatus = (int)order.Status,
            OrderCurrency = CurrencyAmountRules.NormalizeCurrency(order.Currency.ToString()),
            CustomerId = receipt.CustomerId,
            CustomerCode = customer?.CustomerCode ?? string.Empty,
            CustomerName = customer?.CustomerName ?? string.Empty,
            AllocatedAmount = amount,
            Currency = currency,
            Remark = remark,
            Status = CustomerReceiptAllocationRules.StatusActive,
            AllocatedAt = DateTime.Now
        };

            db.CustomerReceiptAllocations.Add(row);
            await db.SaveChangesAsync();

            if (transaction is not null) await transaction.CommitAsync();
            return await MapAsync(db, row);
        }
        catch
        {
            if (transaction is not null) await transaction.RollbackAsync();
            throw;
        }
    }

    /// <summary>
    /// 路由级授权登记（ERP-053）：先实时身份 + 既有收款单（receipt）菜单 + 权威客户数据范围，再解析被引用
    /// 收款单（缺失 / 已删除 / 越范围一律同一条不披露存在性的错误；销售订单在核心登记中按同一收款单客户复核），
    /// 授权通过后才复用 <see cref="CreateAsync"/> 写证据 —— 越权 / 已删除 / 范围外请求绝不落任何引用行。
    /// </summary>
    public static async Task<CustomerReceiptAllocationDto> CreateAuthorizedAsync(
        IErpDbContext db, CustomerReceiptAllocationSaveDto dto, long? userId)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(dto);
        // 纯参数形状校验先于授权（不读库、不泄露信息），保持既有错误码。
        if (dto.ReceiptId <= 0) throw BusinessException.InvalidParameter("请选择要引用的收款单");
        await CustomerReceiptLifecycleRules.ResolveAuthorizedReceiptAsync(db, userId, dto.ReceiptId);
        return await CreateAsync(db, dto);
    }

    // ==================== 2. 作废引用行（保留历史，不删除） ====================

    /// <summary>
    /// 作废一条引用行（必须填写原因）：保留原始金额、收款单 / 客户 / 订单快照与审计历史，
    /// 不物理删除、不静默替换、不重写已作废证据；重复作废被拒绝。
    /// <para>作废<strong>不</strong>改写收款单与销售订单的任何字段，也不产生任何收款 / 记账 / 核销 /
    /// 结算或催收动作。</para>
    /// <para>ERP-378：作废在<b>同一事务</b>内先取收款单行锁（<b>先收款单行、后分摊行</b>，与收款单生命周期
    /// 及两套分摊证据写入同一锁序），再读分摊行做权威作废判定与写入；因此「作废释放证据」与
    /// 「收款单取消 / 删除 / 改动客户 / 币种 / 金额」严格串行，失败整体回滚、原始证据与审计保持不变。</para>
    /// </summary>
    public static async Task<CustomerReceiptAllocationDto> VoidAsync(
        IErpDbContext db, long allocationId, string? reason)
    {
        ArgumentNullException.ThrowIfNull(db);

        // ERP-378 锁序：与收款单生命周期（取消 / 删除 / 改金额）及两套分摊写入共用同一把收款单行锁，
        // 且一律「先收款单行、后分摊行」。这里先做一次轻量投影读取仅为取得收款单 Id（不是权威判定、不构成锁），
        // 取锁之后才读分摊行做权威作废判定，避免「作废证据」与「收款单取消 / 改金额」并发互相失效。
        var receiptId = await db.CustomerReceiptAllocations.AsNoTracking()
            .Where(a => a.Id == allocationId)
            .Select(a => (long?)a.ReceiptId)
            .FirstOrDefaultAsync();

        await using var transaction = CustomerReceiptLifecycleRules.IsRelationalProvider(db)
            ? await db.Database.BeginTransactionAsync()
            : null;
        try
        {
            if (receiptId is > 0) await CustomerReceiptLifecycleRules.LockReceiptRowAsync(db, receiptId.Value);

            var row = await LoadAsync(db, allocationId);
            CustomerReceiptAllocationRules.EnsureVoidable(row.Status, row.ReceiptNo, row.OrderNo);
            var reasonText = CustomerReceiptAllocationRules.NormalizeVoidReason(reason);

            row.Status = CustomerReceiptAllocationRules.StatusVoided;
            row.VoidedAt = DateTime.Now;
            row.VoidReason = reasonText;
            row.UpdatedAt = DateTime.Now;
            await db.SaveChangesAsync();

            if (transaction is not null) await transaction.CommitAsync();
            return await MapAsync(db, row);
        }
        catch
        {
            if (transaction is not null) await transaction.RollbackAsync();
            throw;
        }
    }

    /// <summary>
    /// 路由级授权作废（ERP-053）：先实时身份 + 既有收款单（receipt）菜单 + 权威客户数据范围，再解析引用行所属
    /// 权威收款单（引用行 / 收款单缺失、已删除或越范围一律同一条不披露错误），授权通过后才复用
    /// <see cref="VoidAsync"/> 写作废状态 —— 越权请求绝不改写任何引用行。
    /// </summary>
    public static async Task<CustomerReceiptAllocationDto> VoidAuthorizedAsync(
        IErpDbContext db, long allocationId, string? reason, long? userId)
    {
        ArgumentNullException.ThrowIfNull(db);
        var scope = await CustomerReceiptLifecycleRules.ResolveAuthorizedScopeAsync(db, userId);

        var receiptId = allocationId > 0
            ? await db.CustomerReceiptAllocations.AsNoTracking()
                .Where(a => a.Id == allocationId && !a.IsDeleted)
                .Select(a => (long?)a.ReceiptId)
                .FirstOrDefaultAsync()
            : null;
        await CustomerReceiptLifecycleRules.EnsureReceiptInScopeAsync(db, scope, receiptId);

        return await VoidAsync(db, allocationId, reason);
    }

    // ==================== 3. 读取（台账 / 详情 / 收款单侧汇总） ====================

    /// <summary>引用行详情（含收款单与销售订单可用性标注；只读，不写库）</summary>
    public static async Task<CustomerReceiptAllocationDto> GetAsync(IErpDbContext db, long allocationId)
    {
        ArgumentNullException.ThrowIfNull(db);
        var row = await LoadAsync(db, allocationId);
        return await MapAsync(db, row);
    }

    /// <summary>
    /// 路由级引用行详情：先实时身份 + 收款单菜单 + 权威客户数据范围，再解析引用行并按**历史**口径收敛其
    /// 被引用收款单（软删除后历史仍可读、可用性只作只读标注），越范围 / 物理缺失一律同一条不披露错误。
    /// </summary>
    public static async Task<CustomerReceiptAllocationDto> GetAsync(
        IErpDbContext db, long allocationId, long? userId)
    {
        ArgumentNullException.ThrowIfNull(db);
        var scope = await CustomerReceiptLifecycleRules.ResolveAuthorizedScopeAsync(db, userId);
        var row = await LoadAsync(db, allocationId);
        await CustomerReceiptLifecycleRules.EnsureHistoricalReceiptInScopeAsync(db, scope, row.ReceiptId);
        return await MapAsync(db, row);
    }

    /// <summary>
    /// 台账分页查询（只读，有界）：支持收款单 / 销售订单 / 客户 / 状态 / 币种 / 登记时间区间 / 关键字过滤；
    /// 默认包含已作废历史（证据保留可读）。
    /// <para>本页行一次批量装载收款单 / 客户 / 销售订单，<strong>无逐行数据库查询</strong>。</para>
    /// </summary>
    public static async Task<PagedResult<CustomerReceiptAllocationDto>> ListAsync(
        IErpDbContext db, CustomerReceiptAllocationQuery query, long? userId)
    {
        var scope = await CustomerReceiptLifecycleRules.ResolveAuthorizedScopeAsync(db, userId);
        return await ListAsync(db, query, scope);
    }

    /// <summary>
    /// 台账分页查询（只读，有界）：支持收款单 / 销售订单 / 客户 / 状态 / 币种 / 登记时间区间 / 关键字过滤；
    /// 默认包含已作废历史（证据保留可读）。
    /// <para><paramref name="scope"/> 为 null 时保持既有内部复用语义（不过滤）；路由读取一律由
    /// <see cref="ListAsync(IErpDbContext, CustomerReceiptAllocationQuery, long?)"/> 传入权威客户范围。</para>
    /// <para>本页行一次批量装载收款单 / 客户 / 销售订单，<strong>无逐行数据库查询</strong>。</para>
    /// </summary>
    public static async Task<PagedResult<CustomerReceiptAllocationDto>> ListAsync(
        IErpDbContext db, CustomerReceiptAllocationQuery query, SalespersonDataScope? scope = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(query);
        query.Normalize();

        var status = CustomerReceiptAllocationRules.NormalizeStatusFilter(query.Status);
        var currency = string.IsNullOrWhiteSpace(query.Currency)
            ? null
            : CustomerReceiptAllocationRules.NormalizeCurrencyStrict(query.Currency);

        var source = db.CustomerReceiptAllocations.AsNoTracking().Where(x => !x.IsDeleted);
        if (query.ReceiptId is not null) source = source.Where(x => x.ReceiptId == query.ReceiptId.Value);
        if (query.SalesOrderId is not null)
            source = source.Where(x => x.SalesOrderId == query.SalesOrderId.Value);
        if (query.CustomerId is not null) source = source.Where(x => x.CustomerId == query.CustomerId.Value);
        if (status is not null) source = source.Where(x => x.Status == status.Value);
        if (currency is not null) source = source.Where(x => x.Currency == currency);
        if (query.AllocatedDateFrom is not null)
            source = source.Where(x => x.AllocatedAt >= query.AllocatedDateFrom.Value.Date);
        if (query.AllocatedDateTo is not null)
            source = source.Where(x => x.AllocatedAt < query.AllocatedDateTo.Value.Date.AddDays(1));

        var keyword = CustomerReceiptAllocationRules.NormalizeKeyword(query.Keyword);
        if (keyword.Length > 0)
            source = source.Where(x => x.ReceiptNo.Contains(keyword)
                || x.OrderNo.Contains(keyword)
                || x.CustomerName.Contains(keyword)
                || x.CustomerCode.Contains(keyword));

        // 权威客户数据范围先于任何计数 / 明细读取：范围外条目绝不参与计数或响应体。
        if (scope is not null)
            source = SalespersonDataScopeService.FilterByCustomer(source, scope, x => x.CustomerId);

        var total = await source.CountAsync();
        var rows = await source
            .OrderByDescending(x => x.AllocatedAt)
            .ThenByDescending(x => x.Id)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .ToListAsync();

        return new PagedResult<CustomerReceiptAllocationDto>
        {
            Items = await MapManyAsync(db, rows),
            Total = total,
            Page = query.Page,
            PageSize = query.PageSize
        };
    }

    /// <summary>
    /// 单张收款单的引用行清单（只读、**有界**，收款单详情工作流用）：默认返回全部状态（含已作废历史）；
    /// status 传 1 只看有效 / 传 2 只看已作废；单次最多 <see cref="CustomerReceiptAllocationRules.MaxAllocationsPerReceipt"/> 行。
    /// </summary>
    public static async Task<List<CustomerReceiptAllocationDto>> ListForReceiptAsync(
        IErpDbContext db, long receiptId, long? userId, int? status = null,
        int take = CustomerReceiptAllocationRules.MaxAllocationsPerReceipt)
    {
        await CustomerReceiptLifecycleRules.ResolveAuthorizedReceiptAsync(db, userId, receiptId);
        return await ListForReceiptAsync(db, receiptId, status, take);
    }

    /// <summary>
    /// 单张收款单的引用行清单（只读、**有界**，收款单详情工作流用）：默认返回全部状态（含已作废历史）；
    /// status 传 1 只看有效 / 传 2 只看已作废；单次最多 <see cref="CustomerReceiptAllocationRules.MaxAllocationsPerReceipt"/> 行。
    /// </summary>
    public static async Task<List<CustomerReceiptAllocationDto>> ListForReceiptAsync(
        IErpDbContext db, long receiptId, int? status = null,
        int take = CustomerReceiptAllocationRules.MaxAllocationsPerReceipt)
    {
        ArgumentNullException.ThrowIfNull(db);
        _ = await LoadReceiptAsync(db, receiptId);

        var statusFilter = CustomerReceiptAllocationRules.NormalizeStatusFilter(status);
        var size = take <= 0 ? CustomerReceiptAllocationRules.MaxAllocationsPerReceipt
            : Math.Min(take, CustomerReceiptAllocationRules.MaxAllocationsPerReceipt);

        var source = db.CustomerReceiptAllocations.AsNoTracking()
            .Where(a => !a.IsDeleted && a.ReceiptId == receiptId);
        if (statusFilter is not null) source = source.Where(a => a.Status == statusFilter.Value);

        var rows = await source
            .OrderBy(a => a.Status)
            .ThenBy(a => a.AllocatedAt)
            .ThenBy(a => a.Id)
            .Take(size)
            .ToListAsync();

        return await MapManyAsync(db, rows);
    }

    /// <summary>
    /// 收款单侧汇总（只读派生）：收款单快照 + 已引用金额 / 未引用金额 / 行数 / 已作废行数 + 有界明细。
    /// <para>统计口径（ERP-350）：已引用金额按同一张收款单的「收款单 → 销售订单」与「收款单 → 代理服务费对账单」
    /// 两套 <c>Status = 有效</c> 的持久化行合计（已作废历史永不并入有效合计，但单独计数并列出）；
    /// 收款金额上限按币种精度取整，未引用金额下限 0。
    /// 明细行按有界上限返回（不逐行查库）；本方法<strong>不写库</strong>、不改写收款单与销售订单，
    /// 也不把结果表述为已到账金额、应收账款余额或客户欠款。</para>
    /// </summary>
    public static async Task<CustomerReceiptAllocationReceiptSummaryDto> GetReceiptSummaryAsync(
        IErpDbContext db, long receiptId, long? userId)
    {
        await CustomerReceiptLifecycleRules.ResolveAuthorizedReceiptAsync(db, userId, receiptId);
        return await GetReceiptSummaryAsync(db, receiptId);
    }

    public static async Task<CustomerReceiptAllocationReceiptSummaryDto> GetReceiptSummaryAsync(
        IErpDbContext db, long receiptId)
    {
        ArgumentNullException.ThrowIfNull(db);
        var receipt = await LoadReceiptAsync(db, receiptId);

        var currency = CurrencyAmountRules.NormalizeCurrency(receipt.Currency.ToString());
        var receiptAmount = CustomerReceiptAllocationRules.AuthoritativeReceiptAmount(receipt.Amount, currency);

        // 一次分组统计取回有效 / 已作废的行数与金额（不逐行查库）
        var stats = await db.CustomerReceiptAllocations.AsNoTracking()
            .Where(a => !a.IsDeleted && a.ReceiptId == receipt.Id)
            .GroupBy(a => a.Status)
            .Select(g => new { Status = g.Key, Count = g.Count(), Amount = g.Sum(a => a.AllocatedAmount) })
            .ToListAsync();

        var ownActiveRows = stats.Where(s => s.Status == CustomerReceiptAllocationRules.StatusActive).ToList();
        // 唯一、同币种分摊额度（ERP-350）：剩余额度必须扣减两套有效分摊证据（本维度 + 代理服务费对账单维度）。
        var funding = await CustomerReceiptLifecycleRules.LoadReceiptFundingAsync(db, receipt.Id);
        var allocated = funding.CombinedAllocated;
        var allocationCount = ownActiveRows.Sum(s => s.Count);
        var voidedCount = stats
            .Where(s => s.Status == CustomerReceiptAllocationRules.StatusVoided)
            .Sum(s => s.Count);
        var unallocated = receiptAmount - allocated;
        if (unallocated < 0) unallocated = 0;

        var rows = await ListForReceiptAsync(db, receipt.Id);
        var customer = await db.BaseCustomers.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == receipt.CustomerId);

        return new CustomerReceiptAllocationReceiptSummaryDto(
            receipt.Id,
            receipt.ReceiptNo ?? string.Empty,
            receipt.ReceiptDate,
            (int)receipt.Status,
            CustomerReceiptAllocationRules.ReceiptStatusText((int)receipt.Status),
            receipt.CustomerId,
            customer?.CustomerCode ?? string.Empty,
            customer?.CustomerName ?? string.Empty,
            currency,
            CurrencyAmountRules.PrecisionOf(currency),
            receiptAmount,
            allocated,
            unallocated,
            allocationCount,
            voidedCount,
            CustomerReceiptAllocationRules.LinkageStatusOf(receiptAmount, allocated),
            CustomerReceiptAllocationRules.LinkageText(receiptAmount, allocated, allocationCount, currency),
            CustomerReceiptAllocationRules.IsReceiptSelectable(receipt),
            CustomerReceiptAllocationRules.ReceiptAvailabilityText(receipt),
            CustomerReceiptAllocationRules.RuleText,
            CustomerReceiptAllocationRules.BoundaryText,
            rows);
    }

    // ==================== 4. 候选读取（只读、有界，绝不写库） ====================

    /// <summary>
    /// 可引用收款单候选（只读、有界）：只列出**既有、未删除**的客户收款单（可按客户筛选、按收款单号关键字检索），
    /// 每张收款单附带已引用金额、未引用金额、有效行数与资格文案（已全额引用 / 币种不受支持时不可引用）。
    /// <para><c>UnallocatedAmount</c> 按同一张收款单的「收款单 → 销售订单」与「收款单 → 代理服务费对账单」
    /// 两套有效分摊行合计派生（ERP-350）；它不是银行未到账金额、应收账款余额或客户欠款，
    /// 也不代表款项是否真的收到。</para>
    /// </summary>
    public static async Task<List<CustomerReceiptAllocationReceiptCandidateDto>> ListReceiptCandidatesAsync(
        IErpDbContext db, long? customerId, string? keyword, long? userId,
        int take = CustomerReceiptAllocationRules.MaxReceiptCandidates)
    {
        var scope = await CustomerReceiptLifecycleRules.ResolveAuthorizedScopeAsync(db, userId);
        if (customerId is > 0 && !scope.AllowsCustomer(customerId.Value))
            throw BusinessException.NotFound(CustomerReceiptLifecycleRules.AllocationNotFoundText);
        return await ListReceiptCandidatesAsync(db, customerId, keyword, take, scope);
    }

    /// <summary>
    /// 可引用收款单候选（只读、有界）：只列出**既有、未删除**的客户收款单（可按客户筛选、按收款单号关键字检索）。
    /// <para><paramref name="scope"/> 非空时先按权威客户范围过滤，再取候选（范围外收款单绝不进入响应体）。</para>
    /// </summary>
    public static async Task<List<CustomerReceiptAllocationReceiptCandidateDto>> ListReceiptCandidatesAsync(
        IErpDbContext db, long? customerId, string? keyword,
        int take = CustomerReceiptAllocationRules.MaxReceiptCandidates,
        SalespersonDataScope? scope = null)
    {
        ArgumentNullException.ThrowIfNull(db);

        var keywordText = CustomerReceiptAllocationRules.NormalizeKeyword(keyword);
        var size = take <= 0 ? CustomerReceiptAllocationRules.MaxReceiptCandidates
            : Math.Min(take, CustomerReceiptAllocationRules.MaxReceiptCandidates);

        var source = db.FinanceReceipts.AsNoTracking().Where(r => !r.IsDeleted);
        if (customerId is not null && customerId.Value > 0)
            source = source.Where(r => r.CustomerId == customerId.Value);
        if (keywordText.Length > 0) source = source.Where(r => r.ReceiptNo.Contains(keywordText));
        if (scope is not null)
            source = SalespersonDataScopeService.FilterByCustomer(source, scope, r => r.CustomerId);

        var receipts = await source
            .OrderByDescending(r => r.ReceiptDate)
            .ThenByDescending(r => r.Id)
            .Take(size)
            .ToListAsync();
        if (receipts.Count == 0) return new List<CustomerReceiptAllocationReceiptCandidateDto>();

        var receiptIds = receipts.Select(r => r.Id).ToList();

        // 一次查询取回候选收款单的有效引用行（已作废行不占用额度；仅用于本维度行数）
        var ownRows = await db.CustomerReceiptAllocations.AsNoTracking()
            .Where(a => !a.IsDeleted
                        && a.Status == CustomerReceiptAllocationRules.StatusActive
                        && receiptIds.Contains(a.ReceiptId))
            .Select(a => new { a.ReceiptId, a.AllocatedAmount })
            .ToListAsync();

        // 唯一、同币种分摊额度（ERP-350）：候选的已占用 / 剩余额度扣减两套有效分摊证据。
        var fundingByReceipt = await CustomerReceiptLifecycleRules.LoadReceiptFundingForReceiptsAsync(db, receiptIds);

        var customerIds = receipts.Select(r => r.CustomerId).Distinct().ToList();
        var customers = (await db.BaseCustomers.AsNoTracking()
                .Where(c => customerIds.Contains(c.Id)).ToListAsync())
            .ToDictionary(c => c.Id);

        return receipts.Select(receipt =>
        {
            var ownCount = ownRows.Count(r => r.ReceiptId == receipt.Id);
            var allocated = fundingByReceipt.TryGetValue(receipt.Id, out var funding)
                ? funding.CombinedAllocated : 0m;
            var currency = CurrencyAmountRules.NormalizeCurrency(receipt.Currency.ToString());
            var receiptAmount = CustomerReceiptAllocationRules.AuthoritativeReceiptAmount(receipt.Amount, currency);
            var unallocated = receiptAmount - allocated;
            if (unallocated < 0) unallocated = 0;

            customers.TryGetValue(receipt.CustomerId, out var customer);
            var (eligible, text) = CustomerReceiptAllocationRules.EvaluateReceiptEligibility(receipt, allocated);
            // 资格文案附带客户可用性（停用 / 删除只作只读说明，历史快照照常可读）
            var eligibilityText = $"{text}；{CustomerReceiptAllocationRules.CustomerAvailabilityText(customer)}";

            return new CustomerReceiptAllocationReceiptCandidateDto(
                receipt.Id,
                receipt.ReceiptNo ?? string.Empty,
                receipt.ReceiptDate,
                (int)receipt.Status,
                CustomerReceiptAllocationRules.ReceiptStatusText((int)receipt.Status),
                receipt.CustomerId,
                customer?.CustomerCode ?? string.Empty,
                customer?.CustomerName ?? string.Empty,
                currency,
                receiptAmount,
                allocated,
                unallocated,
                ownCount,
                eligible,
                eligibilityText);
        }).ToList();
    }

    /// <summary>
    /// 可引用销售订单候选（只读、有界）：只列出**同客户 + 同币种**的未删除订单（含已取消订单并显式标注不可引用），
    /// 每张订单附带订单总额、本收款单已引用、其他收款单已引用与剩余未被收款引用证据覆盖的金额
    /// （只按持久化有效行派生，下限 0 —— 不是应收余额、账龄、信用额度或催收依据）。
    /// </summary>
    public static async Task<List<CustomerReceiptAllocationOrderCandidateDto>> ListOrderCandidatesAsync(
        IErpDbContext db, long receiptId, string? keyword, long? userId,
        int take = CustomerReceiptAllocationRules.MaxOrderCandidates)
    {
        await CustomerReceiptLifecycleRules.ResolveAuthorizedReceiptAsync(db, userId, receiptId);
        return await ListOrderCandidatesAsync(db, receiptId, keyword, take);
    }

    /// <summary>
    /// 可引用销售订单候选（只读、有界）：只列出**同客户 + 同币种**的未删除订单（含已取消订单并显式标注不可引用）。
    /// </summary>
    public static async Task<List<CustomerReceiptAllocationOrderCandidateDto>> ListOrderCandidatesAsync(
        IErpDbContext db, long receiptId, string? keyword,
        int take = CustomerReceiptAllocationRules.MaxOrderCandidates)
    {
        ArgumentNullException.ThrowIfNull(db);
        var receipt = await LoadReceiptAsync(db, receiptId);

        var keywordText = CustomerReceiptAllocationRules.NormalizeKeyword(keyword);
        var size = take <= 0 ? CustomerReceiptAllocationRules.MaxOrderCandidates
            : Math.Min(take, CustomerReceiptAllocationRules.MaxOrderCandidates);

        // 收款单币种不在系统币种口径内：没有任何销售订单可以权威匹配（不猜测、不换算）
        if (!Enum.TryParse<Currency>(CurrencyAmountRules.NormalizeCurrency(receipt.Currency.ToString()), out var currency))
            return new List<CustomerReceiptAllocationOrderCandidateDto>();

        var source = db.SalesOrders.AsNoTracking()
            .Where(o => !o.IsDeleted && o.CustomerId == receipt.CustomerId && o.Currency == currency);
        if (keywordText.Length > 0)
            source = source.Where(o => o.OrderNo.Contains(keywordText) || o.ContractNo.Contains(keywordText));

        var orders = await source.OrderByDescending(o => o.Id).Take(size).ToListAsync();
        if (orders.Count == 0) return new List<CustomerReceiptAllocationOrderCandidateDto>();

        var orderIds = orders.Select(o => o.Id).ToList();

        // 一次查询取回候选订单的有效引用行：作废行不再占用「已被收款引用证据覆盖」的金额
        var rows = await db.CustomerReceiptAllocations.AsNoTracking()
            .Where(a => !a.IsDeleted
                        && a.Status == CustomerReceiptAllocationRules.StatusActive
                        && orderIds.Contains(a.SalesOrderId))
            .Select(a => new { a.SalesOrderId, a.ReceiptId, a.AllocatedAmount })
            .ToListAsync();

        var customer = await db.BaseCustomers.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == receipt.CustomerId);
        var receiptCurrency = CurrencyAmountRules.NormalizeCurrency(receipt.Currency.ToString());

        return orders.Select(order =>
        {
            var orderRows = rows.Where(r => r.SalesOrderId == order.Id).ToList();
            var allocatedByThis = orderRows.Where(r => r.ReceiptId == receipt.Id).Sum(r => r.AllocatedAmount);
            var allocatedByOthers = orderRows.Where(r => r.ReceiptId != receipt.Id).Sum(r => r.AllocatedAmount);
            var remaining = order.TotalAmount - allocatedByThis - allocatedByOthers;
            if (remaining < 0) remaining = 0;

            var (eligible, text) = CustomerReceiptAllocationRules.EvaluateOrderEligibility(
                receipt.CustomerId, receiptCurrency, order);

            return new CustomerReceiptAllocationOrderCandidateDto(
                order.Id,
                order.OrderNo ?? string.Empty,
                order.OrderDate,
                CustomerReceiptAllocationRules.OrderStatusText((int)order.Status),
                order.Status == DocumentStatus.Cancelled,
                CurrencyAmountRules.NormalizeCurrency(order.Currency.ToString()),
                order.CustomerId,
                customer?.CustomerCode ?? string.Empty,
                customer?.CustomerName ?? string.Empty,
                order.TotalAmount,
                allocatedByThis,
                allocatedByOthers,
                remaining,
                eligible,
                text);
        }).ToList();
    }

    // ==================== 5. 装载与映射（内部） ====================

    /// <summary>
    /// 按 Id 装载未删除收款单（不存在 / 已删除 → 数据不存在）。
    /// 使用 <c>AsNoTracking</c>：登记证据前已在锁内重新读取权威金额 / 状态，非跟踪读取保证拿到并发提交后的最新值，
    /// 绝不复用加锁时的陈旧跟踪实体。
    /// </summary>
    private static async Task<FinanceReceipt> LoadReceiptAsync(IErpDbContext db, long receiptId)
    {
        if (receiptId <= 0) throw BusinessException.InvalidParameter("收款单 Id 不合法");
        return await db.FinanceReceipts.AsNoTracking()
                   .FirstOrDefaultAsync(r => r.Id == receiptId && !r.IsDeleted)
            ?? throw BusinessException.NotFound($"收款单（Id={receiptId}）不存在或已删除，不能登记收款引用");
    }

    /// <summary>按 Id 装载未删除引用行（不存在 / 已删除 → 数据不存在）</summary>
    private static async Task<CustomerReceiptAllocation> LoadAsync(IErpDbContext db, long allocationId)
    {
        if (allocationId <= 0) throw BusinessException.InvalidParameter("引用行 Id 不合法");
        return await db.CustomerReceiptAllocations
                   .FirstOrDefaultAsync(a => a.Id == allocationId && !a.IsDeleted)
            ?? throw BusinessException.NotFound($"收款引用行（Id={allocationId}）不存在或已删除");
    }

    /// <summary>单行映射（详情与单条操作返回用；只读标注，不写库）</summary>
    private static async Task<CustomerReceiptAllocationDto> MapAsync(
        IErpDbContext db, CustomerReceiptAllocation row)
    {
        var mapped = await MapManyAsync(db, new List<CustomerReceiptAllocation> { row });
        return mapped[0];
    }

    /// <summary>
    /// 批量映射（台账分页与收款单侧清单用）：收款单 / 客户 / 销售订单各一次批量查询，<strong>绝无逐行数据库查询</strong>；
    /// 收款单与销售订单可用性都是**只读标注**：软删除 / 取消不改变历史引用证据的可读性。
    /// </summary>
    private static async Task<List<CustomerReceiptAllocationDto>> MapManyAsync(
        IErpDbContext db, IReadOnlyList<CustomerReceiptAllocation> rows)
    {
        if (rows.Count == 0) return new List<CustomerReceiptAllocationDto>();

        var receiptIds = rows.Select(r => r.ReceiptId).Distinct().ToList();
        var receipts = (await db.FinanceReceipts.AsNoTracking()
                .Where(r => receiptIds.Contains(r.Id)).ToListAsync())
            .ToDictionary(r => r.Id);

        var orderIds = rows.Select(r => r.SalesOrderId).Distinct().ToList();
        var orders = orderIds.Count == 0
            ? new Dictionary<long, SalesOrder>()
            : (await db.SalesOrders.AsNoTracking().Where(o => orderIds.Contains(o.Id)).ToListAsync())
                .ToDictionary(o => o.Id);

        var customerIds = rows.Select(r => r.CustomerId).Distinct().ToList();
        var customers = customerIds.Count == 0
            ? new Dictionary<long, BaseCustomer>()
            : (await db.BaseCustomers.AsNoTracking().Where(c => customerIds.Contains(c.Id)).ToListAsync())
                .ToDictionary(c => c.Id);

        return rows.Select(row => Map(row, receipts, orders, customers)).ToList();
    }

    /// <summary>引用行实体 → DTO（含收款单 / 销售订单可用性标注；纯映射，不写库）</summary>
    private static CustomerReceiptAllocationDto Map(
        CustomerReceiptAllocation row,
        Dictionary<long, FinanceReceipt> receipts,
        Dictionary<long, SalesOrder> orders,
        Dictionary<long, BaseCustomer> customers)
    {
        ArgumentNullException.ThrowIfNull(row);

        var currency = CurrencyAmountRules.NormalizeCurrency(row.Currency);
        receipts.TryGetValue(row.ReceiptId, out var receipt);
        orders.TryGetValue(row.SalesOrderId, out var order);
        customers.TryGetValue(row.CustomerId, out var customer);

        var receiptAvailable = receipt is not null && !receipt.IsDeleted;
        var orderAvailable = order is not null && !order.IsDeleted;

        var receiptAvailabilityText = receiptAvailable
            ? "收款单可用"
            : "收款单已删除或不存在：历史引用保留可读";

        var orderAvailabilityText = orderAvailable
            ? (order!.Status == DocumentStatus.Cancelled
                ? "订单已取消：历史引用保留可读，不再作为可引用订单"
                : $"订单可用（{CustomerReceiptAllocationRules.OrderStatusText((int)order.Status)}）")
            : "订单已删除或不存在：历史引用保留可读";

        // 客户可用性只在存在客户记录时补充说明（历史快照始终可读，不回填、不改写）
        var customerNote = customer is null
            ? string.Empty
            : $"；{CustomerReceiptAllocationRules.CustomerAvailabilityText(customer)}";

        return new CustomerReceiptAllocationDto(
            row.Id,
            row.ReceiptId,
            row.ReceiptNo ?? string.Empty,
            row.ReceiptDate,
            row.ReceiptStatus,
            string.IsNullOrEmpty(row.ReceiptStatusText)
                ? CustomerReceiptAllocationRules.ReceiptStatusText(row.ReceiptStatus)
                : row.ReceiptStatusText,
            row.ReceiptAmount,
            row.SalesOrderId,
            row.OrderNo ?? string.Empty,
            row.OrderDate,
            CustomerReceiptAllocationRules.OrderStatusText(row.OrderStatus),
            CurrencyAmountRules.NormalizeCurrency(row.OrderCurrency),
            row.CustomerId,
            row.CustomerCode ?? string.Empty,
            row.CustomerName ?? string.Empty,
            currency,
            CurrencyAmountRules.PrecisionOf(currency),
            row.AllocatedAmount,
            row.Remark ?? string.Empty,
            row.Status,
            CustomerReceiptAllocationRules.StatusText(row.Status),
            row.Status == CustomerReceiptAllocationRules.StatusActive,
            row.Status == CustomerReceiptAllocationRules.StatusVoided,
            row.AllocatedAt,
            row.VoidedAt,
            row.VoidReason ?? string.Empty,
            receiptAvailable,
            receiptAvailabilityText,
            orderAvailable,
            orderAvailabilityText + customerNote,
            row.CreatedAt,
            row.UpdatedAt,
            CustomerReceiptAllocationRules.BoundaryText);
    }
}
