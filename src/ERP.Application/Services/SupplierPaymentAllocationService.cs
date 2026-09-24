using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ERP.Application.Services;

/// <summary>
/// 供应商付款单 → 采购订单 付款引用（分摊）证据登记服务（ERP-049）。职责：
/// <list type="number">
/// <item><b>登记引用行</b>（<see cref="CreateAsync"/>）：付款单必须存在且未删除，采购订单必须存在、未删除、
/// 未取消，且订单供应商与币种都必须与付款单**权威一致**；引用金额按币种精度取整且大于 0，
/// 同一付款单内有效行合计不得超过付款单金额，同一订单在同一付款单内不得重复（有效行）；</item>
/// <item><b>作废引用行</b>（<see cref="VoidAsync"/>）：必须填写原因，保留原始金额 / 快照 / 审计历史，
/// 不物理删除、不静默替换、不重写已作废证据；</item>
/// <item><b>台账与汇总读取</b>（<see cref="ListAsync"/> / <see cref="GetAsync"/> /
/// <see cref="ListForPaymentAsync"/> / <see cref="GetPaymentSummaryAsync"/>）：分页 / 有界、批量装载，
/// 无逐行数据库查询；</item>
/// <item><b>候选读取</b>（<see cref="ListPaymentCandidatesAsync"/> / <see cref="ListOrderCandidatesAsync"/>）：
/// 只读、有界，含资格文案与只读派生金额（绝不写入、绝不猜测订单）。</item>
/// </list>
/// <para>边界（重要）：本服务只读写 <c>SupplierPaymentAllocations</c> 一张表，<strong>不</strong>改写付款单的
/// 审批 / 执行状态、金额、币种、付款方式、银行账户、供应商或备注，<strong>不</strong>改写采购订单状态、
/// 到货进度、已收数量、金额与明细、结算进度，<strong>不</strong>改写发票与发票关联、库存与库存成本、
/// 库存流水、退税记录、费用与供应商余额，也<strong>不</strong>执行任何付款、记账、核销或结算动作。</para>
/// </summary>
public static class SupplierPaymentAllocationService
{
    // ==================== 1. 登记引用行（新增一条分摊证据） ====================

    /// <summary>
    /// 登记一条付款引用行：全部校验通过后才写一行证据，并写入付款单 / 供应商 / 采购订单的服务端快照。
    /// <para>校验顺序：付款单可用 → 币种口径 → 引用金额（精度 + 大于 0）→ 备注 → 采购订单资格
    /// （存在 / 未删除 / 未取消 / 供应商一致 / 币种一致）→ 单付款单行数上限 → 重复有效行 → 付款金额上限。</para>
    /// </summary>
    public static async Task<SupplierPaymentAllocationDto> CreateAsync(
        IErpDbContext db, SupplierPaymentAllocationSaveDto dto)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(dto);

        if (dto.PaymentId <= 0) throw BusinessException.InvalidParameter("请选择要引用的付款单");
        if (dto.PurchaseOrderId <= 0) throw BusinessException.InvalidParameter("请选择要引用的采购订单");

        var payment = await LoadPaymentAsync(db, dto.PaymentId);
        var currency = SupplierPaymentAllocationRules.NormalizeCurrencyStrict(payment.Currency.ToString());
        var amount = SupplierPaymentAllocationRules.NormalizeAllocationAmount(dto.AllocatedAmount, currency);
        var remark = SupplierPaymentAllocationRules.NormalizeRemark(dto.Remark);

        var order = await db.PurchaseOrders.AsNoTracking()
            .FirstOrDefaultAsync(o => o.Id == dto.PurchaseOrderId);
        SupplierPaymentAllocationRules.EnsureOrderLinkable(payment.SupplierId, currency, order);
        if (order is null) throw BusinessException.NotFound("采购订单不存在或已删除，不能引用");

        // 有效行（未作废、未删除）：重复、行数与金额上限都以它为准；作废行保留历史但不再占用额度
        var activeRows = await db.SupplierPaymentAllocations.AsNoTracking()
            .Where(a => !a.IsDeleted && a.PaymentId == payment.Id && a.Status == SupplierPaymentAllocationRules.StatusActive)
            .ToListAsync();

        if (activeRows.Count >= SupplierPaymentAllocationRules.MaxAllocationsPerPayment)
            throw BusinessException.RuleConflict(
                $"付款单「{payment.PaymentNo}」的有效引用行已达上限 {SupplierPaymentAllocationRules.MaxAllocationsPerPayment} 条："
                + "如需新增请先作废不需要的行（作废保留历史，不物理删除）");

        var duplicate = activeRows.FirstOrDefault(a => a.PurchaseOrderId == order.Id);
        if (duplicate is not null)
            throw BusinessException.Duplicate(
                $"付款单「{payment.PaymentNo}」已存在指向采购订单「{order.OrderNo}」的有效引用行"
                + $"（Id={duplicate.Id}，金额 {duplicate.AllocatedAmount} {duplicate.Currency}）："
                + "同一订单在同一付款单内只能有一条有效引用行（如需更正请先作废原行，再登记新行；作废保留历史）");

        var paymentAmount = SupplierPaymentAllocationRules.AuthoritativePaymentAmount(payment.Amount, currency);
        var allocated = activeRows.Sum(a => a.AllocatedAmount);
        var total = allocated + amount;
        if (total > paymentAmount)
            throw BusinessException.RuleConflict(
                $"付款单「{payment.PaymentNo}」的引用金额合计 {total} 超过付款单金额 {paymentAmount} {currency}"
                + $"（已引用 {allocated}，本次 {amount}）：请调整引用金额"
                + "（付款单允许部分或全部未被引用，未引用部分保持为未引用金额）");

        var supplier = await db.BaseSuppliers.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == payment.SupplierId);

        var row = new SupplierPaymentAllocation
        {
            PaymentId = payment.Id,
            PaymentNo = payment.PaymentNo ?? string.Empty,
            PaymentDate = payment.PaymentDate,
            PaymentStatus = (int)payment.Status,
            PaymentStatusText = SupplierPaymentAllocationRules.PaymentStatusText((int)payment.Status),
            PaymentAmount = paymentAmount,
            PurchaseOrderId = order.Id,
            OrderNo = order.OrderNo ?? string.Empty,
            OrderDate = order.OrderDate,
            OrderStatus = (int)order.Status,
            OrderCurrency = CurrencyAmountRules.NormalizeCurrency(order.Currency.ToString()),
            SupplierId = payment.SupplierId,
            SupplierCode = supplier?.SupplierCode ?? string.Empty,
            SupplierName = supplier?.SupplierName ?? string.Empty,
            AllocatedAmount = amount,
            Currency = currency,
            Remark = remark,
            Status = SupplierPaymentAllocationRules.StatusActive,
            AllocatedAt = DateTime.Now
        };

        db.SupplierPaymentAllocations.Add(row);
        await db.SaveChangesAsync();

        return await MapAsync(db, row);
    }

    // ==================== 2. 作废引用行（保留历史，不删除） ====================

    /// <summary>
    /// 作废一条引用行（必须填写原因）：保留原始金额、付款单 / 供应商 / 订单快照与审计历史，
    /// 不物理删除、不静默替换、不重写已作废证据；重复作废被拒绝。
    /// <para>作废<strong>不</strong>改写付款单与采购订单的任何字段，也不产生任何收付款 / 记账 / 核销动作。</para>
    /// </summary>
    public static async Task<SupplierPaymentAllocationDto> VoidAsync(
        IErpDbContext db, long allocationId, string? reason)
    {
        ArgumentNullException.ThrowIfNull(db);

        var row = await LoadAsync(db, allocationId);
        SupplierPaymentAllocationRules.EnsureVoidable(row.Status, row.PaymentNo, row.OrderNo);
        var reasonText = SupplierPaymentAllocationRules.NormalizeVoidReason(reason);

        row.Status = SupplierPaymentAllocationRules.StatusVoided;
        row.VoidedAt = DateTime.Now;
        row.VoidReason = reasonText;
        row.UpdatedAt = DateTime.Now;
        await db.SaveChangesAsync();

        return await MapAsync(db, row);
    }

    // ==================== 3. 读取（台账 / 详情 / 付款单侧汇总） ====================

    /// <summary>引用行详情（含付款单与采购订单可用性标注；只读，不写库）</summary>
    public static async Task<SupplierPaymentAllocationDto> GetAsync(IErpDbContext db, long allocationId)
    {
        ArgumentNullException.ThrowIfNull(db);
        var row = await LoadAsync(db, allocationId);
        return await MapAsync(db, row);
    }

    /// <summary>
    /// 台账分页查询（只读，有界）：支持付款单 / 采购订单 / 供应商 / 状态 / 币种 / 登记时间区间 / 关键字过滤；
    /// 默认包含已作废历史（证据保留可读）。
    /// <para>本页行一次批量装载付款单 / 供应商 / 采购订单，<strong>无逐行数据库查询</strong>。</para>
    /// </summary>
    public static async Task<PagedResult<SupplierPaymentAllocationDto>> ListAsync(
        IErpDbContext db, SupplierPaymentAllocationQuery query)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(query);
        query.Normalize();

        var status = SupplierPaymentAllocationRules.NormalizeStatusFilter(query.Status);
        var currency = string.IsNullOrWhiteSpace(query.Currency)
            ? null
            : SupplierPaymentAllocationRules.NormalizeCurrencyStrict(query.Currency);

        var source = db.SupplierPaymentAllocations.AsNoTracking().Where(x => !x.IsDeleted);
        if (query.PaymentId is not null) source = source.Where(x => x.PaymentId == query.PaymentId.Value);
        if (query.PurchaseOrderId is not null)
            source = source.Where(x => x.PurchaseOrderId == query.PurchaseOrderId.Value);
        if (query.SupplierId is not null) source = source.Where(x => x.SupplierId == query.SupplierId.Value);
        if (status is not null) source = source.Where(x => x.Status == status.Value);
        if (currency is not null) source = source.Where(x => x.Currency == currency);
        if (query.AllocatedDateFrom is not null)
            source = source.Where(x => x.AllocatedAt >= query.AllocatedDateFrom.Value.Date);
        if (query.AllocatedDateTo is not null)
            source = source.Where(x => x.AllocatedAt < query.AllocatedDateTo.Value.Date.AddDays(1));

        var keyword = SupplierPaymentAllocationRules.NormalizeKeyword(query.Keyword);
        if (keyword.Length > 0)
            source = source.Where(x => x.PaymentNo.Contains(keyword)
                || x.OrderNo.Contains(keyword)
                || x.SupplierName.Contains(keyword)
                || x.SupplierCode.Contains(keyword));

        var total = await source.CountAsync();
        var rows = await source
            .OrderByDescending(x => x.AllocatedAt)
            .ThenByDescending(x => x.Id)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .ToListAsync();

        return new PagedResult<SupplierPaymentAllocationDto>
        {
            Items = await MapManyAsync(db, rows),
            Total = total,
            Page = query.Page,
            PageSize = query.PageSize
        };
    }

    /// <summary>
    /// 单张付款单的引用行清单（只读、**有界**，付款单详情工作流用）：默认返回全部状态（含已作废历史）；
    /// status 传 1 只看有效 / 传 2 只看已作废；单次最多 <see cref="SupplierPaymentAllocationRules.MaxAllocationsPerPayment"/> 行。
    /// </summary>
    public static async Task<List<SupplierPaymentAllocationDto>> ListForPaymentAsync(
        IErpDbContext db, long paymentId, int? status = null,
        int take = SupplierPaymentAllocationRules.MaxAllocationsPerPayment)
    {
        ArgumentNullException.ThrowIfNull(db);
        _ = await LoadPaymentAsync(db, paymentId);

        var statusFilter = SupplierPaymentAllocationRules.NormalizeStatusFilter(status);
        var size = take <= 0 ? SupplierPaymentAllocationRules.MaxAllocationsPerPayment
            : Math.Min(take, SupplierPaymentAllocationRules.MaxAllocationsPerPayment);

        var source = db.SupplierPaymentAllocations.AsNoTracking()
            .Where(a => !a.IsDeleted && a.PaymentId == paymentId);
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
    /// 付款单侧汇总（只读派生）：付款单快照 + **有效行**已引用金额 / 未引用金额 / 行数 / 已作废行数 + 有界明细。
    /// <para>统计口径：已引用金额只按 <c>Status = 有效</c> 的持久化行合计（已作废历史永不并入有效合计，
    /// 但单独计数并列出）；付款金额上限按币种精度取整，未引用金额下限 0。
    /// 明细行按有界上限返回（不逐行查库）；本方法<strong>不写库</strong>、不改写付款单与采购订单。</para>
    /// </summary>
    public static async Task<SupplierPaymentAllocationPaymentSummaryDto> GetPaymentSummaryAsync(
        IErpDbContext db, long paymentId)
    {
        ArgumentNullException.ThrowIfNull(db);
        var payment = await LoadPaymentAsync(db, paymentId);

        var currency = CurrencyAmountRules.NormalizeCurrency(payment.Currency.ToString());
        var paymentAmount = SupplierPaymentAllocationRules.AuthoritativePaymentAmount(payment.Amount, currency);

        // 一次分组统计取回有效 / 已作废的行数与金额（不逐行查库）
        var stats = await db.SupplierPaymentAllocations.AsNoTracking()
            .Where(a => !a.IsDeleted && a.PaymentId == payment.Id)
            .GroupBy(a => a.Status)
            .Select(g => new { Status = g.Key, Count = g.Count(), Amount = g.Sum(a => a.AllocatedAmount) })
            .ToListAsync();

        var activeRows = stats.Where(s => s.Status == SupplierPaymentAllocationRules.StatusActive).ToList();
        var allocated = activeRows.Sum(s => s.Amount);
        var allocationCount = activeRows.Sum(s => s.Count);
        var voidedCount = stats.Where(s => s.Status == SupplierPaymentAllocationRules.StatusVoided).Sum(s => s.Count);
        var unallocated = paymentAmount - allocated;
        if (unallocated < 0) unallocated = 0;

        var rows = await ListForPaymentAsync(db, payment.Id);
        var supplier = await db.BaseSuppliers.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == payment.SupplierId);

        return new SupplierPaymentAllocationPaymentSummaryDto(
            payment.Id,
            payment.PaymentNo ?? string.Empty,
            payment.PaymentDate,
            (int)payment.Status,
            SupplierPaymentAllocationRules.PaymentStatusText((int)payment.Status),
            payment.SupplierId,
            supplier?.SupplierCode ?? string.Empty,
            supplier?.SupplierName ?? string.Empty,
            currency,
            CurrencyAmountRules.PrecisionOf(currency),
            paymentAmount,
            allocated,
            unallocated,
            allocationCount,
            voidedCount,
            SupplierPaymentAllocationRules.LinkageStatusOf(paymentAmount, allocated),
            SupplierPaymentAllocationRules.LinkageText(paymentAmount, allocated, allocationCount, currency),
            SupplierPaymentAllocationRules.IsPaymentSelectable(payment),
            SupplierPaymentAllocationRules.PaymentAvailabilityText(payment),
            SupplierPaymentAllocationRules.RuleText,
            SupplierPaymentAllocationRules.BoundaryText,
            rows);
    }

    // ==================== 4. 候选读取（只读、有界，绝不写库） ====================

    /// <summary>
    /// 可引用付款单候选（只读、有界）：只列出**既有、未删除**的付款单（可按供应商筛选、按付款单号关键字检索），
    /// 每张付款单附带有效行已引用金额、未引用金额、有效行数与资格文案（已全额引用 / 币种不受支持时不可引用）。
    /// <para><c>UnallocatedAmount</c> 只按持久化有效行派生，它不是银行未付金额、应付余额或发票余额，
    /// 也不代表付款是否真的发生。</para>
    /// </summary>
    public static async Task<List<SupplierPaymentAllocationPaymentCandidateDto>> ListPaymentCandidatesAsync(
        IErpDbContext db, long? supplierId, string? keyword,
        int take = SupplierPaymentAllocationRules.MaxPaymentCandidates)
    {
        ArgumentNullException.ThrowIfNull(db);

        var keywordText = SupplierPaymentAllocationRules.NormalizeKeyword(keyword);
        var size = take <= 0 ? SupplierPaymentAllocationRules.MaxPaymentCandidates
            : Math.Min(take, SupplierPaymentAllocationRules.MaxPaymentCandidates);

        var source = db.FinancePayments.AsNoTracking().Where(p => !p.IsDeleted);
        if (supplierId is not null && supplierId.Value > 0)
            source = source.Where(p => p.SupplierId == supplierId.Value);
        if (keywordText.Length > 0) source = source.Where(p => p.PaymentNo.Contains(keywordText));

        var payments = await source
            .OrderByDescending(p => p.PaymentDate)
            .ThenByDescending(p => p.Id)
            .Take(size)
            .ToListAsync();
        if (payments.Count == 0) return new List<SupplierPaymentAllocationPaymentCandidateDto>();

        var paymentIds = payments.Select(p => p.Id).ToList();

        // 一次查询取回候选付款单的有效引用行（已作废行不占用额度）
        var activeRows = await db.SupplierPaymentAllocations.AsNoTracking()
            .Where(a => !a.IsDeleted
                        && a.Status == SupplierPaymentAllocationRules.StatusActive
                        && paymentIds.Contains(a.PaymentId))
            .Select(a => new { a.PaymentId, a.AllocatedAmount })
            .ToListAsync();

        var supplierIds = payments.Select(p => p.SupplierId).Distinct().ToList();
        var suppliers = (await db.BaseSuppliers.AsNoTracking()
                .Where(s => supplierIds.Contains(s.Id)).ToListAsync())
            .ToDictionary(s => s.Id);

        return payments.Select(payment =>
        {
            var rows = activeRows.Where(r => r.PaymentId == payment.Id).ToList();
            var allocated = rows.Sum(r => r.AllocatedAmount);
            var currency = CurrencyAmountRules.NormalizeCurrency(payment.Currency.ToString());
            var paymentAmount = SupplierPaymentAllocationRules.AuthoritativePaymentAmount(payment.Amount, currency);
            var unallocated = paymentAmount - allocated;
            if (unallocated < 0) unallocated = 0;

            suppliers.TryGetValue(payment.SupplierId, out var supplier);
            var (eligible, text) = SupplierPaymentAllocationRules.EvaluatePaymentEligibility(payment, allocated);
            // 资格文案附带供应商可用性（停用 / 删除只作只读说明，历史快照照常可读）
            var eligibilityText = $"{text}；{SupplierPaymentAllocationRules.SupplierAvailabilityText(supplier)}";

            return new SupplierPaymentAllocationPaymentCandidateDto(
                payment.Id,
                payment.PaymentNo ?? string.Empty,
                payment.PaymentDate,
                (int)payment.Status,
                SupplierPaymentAllocationRules.PaymentStatusText((int)payment.Status),
                payment.SupplierId,
                supplier?.SupplierCode ?? string.Empty,
                supplier?.SupplierName ?? string.Empty,
                currency,
                paymentAmount,
                allocated,
                unallocated,
                rows.Count,
                eligible,
                eligibilityText);
        }).ToList();
    }
    /// <summary>
    /// 可引用采购订单候选（只读、有界）：只列出**同供应商 + 同币种**的未删除订单（含已取消订单并显式标注不可引用），
    /// 每张订单附带订单总额、本付款单已引用、其他付款单已引用与剩余未被付款引用证据覆盖的金额
    /// （只按持久化有效行派生，下限 0 —— 不是应付余额、账龄或结算依据）。
    /// </summary>
    public static async Task<List<SupplierPaymentAllocationOrderCandidateDto>> ListOrderCandidatesAsync(
        IErpDbContext db, long paymentId, string? keyword,
        int take = SupplierPaymentAllocationRules.MaxOrderCandidates)
    {
        ArgumentNullException.ThrowIfNull(db);
        var payment = await LoadPaymentAsync(db, paymentId);

        var keywordText = SupplierPaymentAllocationRules.NormalizeKeyword(keyword);
        var size = take <= 0 ? SupplierPaymentAllocationRules.MaxOrderCandidates
            : Math.Min(take, SupplierPaymentAllocationRules.MaxOrderCandidates);

        // 付款单币种不在系统币种口径内：没有任何采购订单可以权威匹配（不猜测、不换算）
        if (!Enum.TryParse<Currency>(CurrencyAmountRules.NormalizeCurrency(payment.Currency.ToString()), out var currency))
            return new List<SupplierPaymentAllocationOrderCandidateDto>();

        var source = db.PurchaseOrders.AsNoTracking()
            .Where(o => !o.IsDeleted && o.SupplierId == payment.SupplierId && o.Currency == currency);
        if (keywordText.Length > 0)
            source = source.Where(o => o.OrderNo.Contains(keywordText) || o.ContractNo.Contains(keywordText));

        var orders = await source.OrderByDescending(o => o.Id).Take(size).ToListAsync();
        if (orders.Count == 0) return new List<SupplierPaymentAllocationOrderCandidateDto>();

        var orderIds = orders.Select(o => o.Id).ToList();

        // 一次查询取回候选订单的有效引用行：作废行不再占用「已被付款引用证据覆盖」的金额
        var rows = await db.SupplierPaymentAllocations.AsNoTracking()
            .Where(a => !a.IsDeleted
                        && a.Status == SupplierPaymentAllocationRules.StatusActive
                        && orderIds.Contains(a.PurchaseOrderId))
            .Select(a => new { a.PurchaseOrderId, a.PaymentId, a.AllocatedAmount })
            .ToListAsync();

        var supplier = await db.BaseSuppliers.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == payment.SupplierId);
        var paymentCurrency = CurrencyAmountRules.NormalizeCurrency(payment.Currency.ToString());

        return orders.Select(order =>
        {
            var orderRows = rows.Where(r => r.PurchaseOrderId == order.Id).ToList();
            var allocatedByThis = orderRows.Where(r => r.PaymentId == payment.Id).Sum(r => r.AllocatedAmount);
            var allocatedByOthers = orderRows.Where(r => r.PaymentId != payment.Id).Sum(r => r.AllocatedAmount);
            var remaining = order.TotalAmount - allocatedByThis - allocatedByOthers;
            if (remaining < 0) remaining = 0;

            var (eligible, text) = SupplierPaymentAllocationRules.EvaluateOrderEligibility(
                payment.SupplierId, paymentCurrency, order);

            return new SupplierPaymentAllocationOrderCandidateDto(
                order.Id,
                order.OrderNo ?? string.Empty,
                order.OrderDate,
                SupplierPaymentAllocationRules.OrderStatusText((int)order.Status),
                order.Status == DocumentStatus.Cancelled,
                CurrencyAmountRules.NormalizeCurrency(order.Currency.ToString()),
                order.SupplierId,
                supplier?.SupplierCode ?? string.Empty,
                supplier?.SupplierName ?? string.Empty,
                order.TotalAmount,
                allocatedByThis,
                allocatedByOthers,
                remaining,
                eligible,
                text);
        }).ToList();
    }



    // ==================== 5. 装载与映射（内部） ====================

    /// <summary>按 Id 装载未删除付款单（不存在 / 已删除 → 数据不存在）</summary>
    private static async Task<FinancePayment> LoadPaymentAsync(IErpDbContext db, long paymentId)
    {
        if (paymentId <= 0) throw BusinessException.InvalidParameter("付款单 Id 不合法");
        return await db.FinancePayments.FirstOrDefaultAsync(p => p.Id == paymentId && !p.IsDeleted)
            ?? throw BusinessException.NotFound($"付款单（Id={paymentId}）不存在或已删除，不能登记付款引用");
    }

    /// <summary>按 Id 装载未删除引用行（不存在 / 已删除 → 数据不存在）</summary>
    private static async Task<SupplierPaymentAllocation> LoadAsync(IErpDbContext db, long allocationId)
    {
        if (allocationId <= 0) throw BusinessException.InvalidParameter("引用行 Id 不合法");
        return await db.SupplierPaymentAllocations.FirstOrDefaultAsync(a => a.Id == allocationId && !a.IsDeleted)
            ?? throw BusinessException.NotFound($"付款引用行（Id={allocationId}）不存在或已删除");
    }

    /// <summary>单行映射（详情与单条操作返回用；只读标注，不写库）</summary>
    private static async Task<SupplierPaymentAllocationDto> MapAsync(IErpDbContext db, SupplierPaymentAllocation row)
    {
        var mapped = await MapManyAsync(db, new List<SupplierPaymentAllocation> { row });
        return mapped[0];
    }

    /// <summary>
    /// 批量映射（台账分页与付款单侧清单用）：付款单 / 采购订单各一次批量查询，<strong>绝无逐行数据库查询</strong>；
    /// 付款单与采购订单可用性都是**只读标注**：软删除 / 取消不改变历史引用证据的可读性。
    /// </summary>
    private static async Task<List<SupplierPaymentAllocationDto>> MapManyAsync(
        IErpDbContext db, IReadOnlyList<SupplierPaymentAllocation> rows)
    {
        if (rows.Count == 0) return new List<SupplierPaymentAllocationDto>();

        var paymentIds = rows.Select(r => r.PaymentId).Distinct().ToList();
        var payments = (await db.FinancePayments.AsNoTracking()
                .Where(p => paymentIds.Contains(p.Id)).ToListAsync())
            .ToDictionary(p => p.Id);

        var orderIds = rows.Select(r => r.PurchaseOrderId).Distinct().ToList();
        var orders = orderIds.Count == 0
            ? new Dictionary<long, PurchaseOrder>()
            : (await db.PurchaseOrders.AsNoTracking().Where(o => orderIds.Contains(o.Id)).ToListAsync())
                .ToDictionary(o => o.Id);

        return rows.Select(row => Map(row, payments, orders)).ToList();
    }

    /// <summary>引用行实体 → DTO（含付款单 / 采购订单可用性标注；纯映射，不写库）</summary>
    private static SupplierPaymentAllocationDto Map(
        SupplierPaymentAllocation row,
        Dictionary<long, FinancePayment> payments,
        Dictionary<long, PurchaseOrder> orders)
    {
        ArgumentNullException.ThrowIfNull(row);

        var currency = CurrencyAmountRules.NormalizeCurrency(row.Currency);
        payments.TryGetValue(row.PaymentId, out var payment);
        orders.TryGetValue(row.PurchaseOrderId, out var order);

        var paymentAvailable = payment is not null && !payment.IsDeleted;
        var orderAvailable = order is not null && !order.IsDeleted;

        var paymentAvailabilityText = paymentAvailable
            ? "付款单可用"
            : "付款单已删除或不存在：历史引用保留可读";

        var orderAvailabilityText = orderAvailable
            ? (order!.Status == DocumentStatus.Cancelled
                ? "订单已取消：历史引用保留可读，不再作为可引用订单"
                : $"订单可用（{SupplierPaymentAllocationRules.OrderStatusText((int)order.Status)}）")
            : "订单已删除或不存在：历史引用保留可读";

        return new SupplierPaymentAllocationDto(
            row.Id,
            row.PaymentId,
            row.PaymentNo ?? string.Empty,
            row.PaymentDate,
            row.PaymentStatus,
            string.IsNullOrEmpty(row.PaymentStatusText)
                ? SupplierPaymentAllocationRules.PaymentStatusText(row.PaymentStatus)
                : row.PaymentStatusText,
            row.PaymentAmount,
            row.PurchaseOrderId,
            row.OrderNo ?? string.Empty,
            row.OrderDate,
            SupplierPaymentAllocationRules.OrderStatusText(row.OrderStatus),
            CurrencyAmountRules.NormalizeCurrency(row.OrderCurrency),
            row.SupplierId,
            row.SupplierCode ?? string.Empty,
            row.SupplierName ?? string.Empty,
            currency,
            CurrencyAmountRules.PrecisionOf(currency),
            row.AllocatedAmount,
            row.Remark ?? string.Empty,
            row.Status,
            SupplierPaymentAllocationRules.StatusText(row.Status),
            row.Status == SupplierPaymentAllocationRules.StatusActive,
            row.Status == SupplierPaymentAllocationRules.StatusVoided,
            row.AllocatedAt,
            row.VoidedAt,
            row.VoidReason ?? string.Empty,
            paymentAvailable,
            paymentAvailabilityText,
            orderAvailable,
            orderAvailabilityText,
            row.CreatedAt,
            row.UpdatedAt,
            SupplierPaymentAllocationRules.BoundaryText);
    }
}
