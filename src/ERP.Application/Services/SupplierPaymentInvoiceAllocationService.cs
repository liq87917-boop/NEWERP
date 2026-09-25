using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace ERP.Application.Services;

/// <summary>
/// 供应商付款单 → 供应商采购发票 付款引用（分摊）证据登记服务（ERP-066）。职责：
/// <list type="number">
/// <item><b>登记引用行</b>（<see cref="CreateAsync"/>）：付款单必须存在且未删除，发票必须存在、未删除且
/// **已登记（未作废）**，且发票供应商与币种都必须与付款单**权威一致**；引用金额按币种精度取整且大于 0，
/// 同一付款单内有效行合计不得超过付款单金额，同一发票内有效行合计不得超过发票含税总额，
/// 同一发票在同一付款单内不得重复（有效行）；</item>
/// <item><b>作废引用行</b>（<see cref="VoidAsync"/>）：必须填写原因，保留原始金额 / 快照 / 登记人与审计历史，
/// 不物理删除、不改派、不静默替换已登记金额；</item>
/// <item><b>台账与汇总读取</b>（<see cref="ListAsync"/> / <see cref="GetAsync"/> /
/// <see cref="ListForPaymentAsync"/> / <see cref="GetPaymentSummaryAsync"/> / <see cref="GetInvoiceSummaryAsync"/>）：
/// 分页 / 有界、批量装载，无逐行数据库查询；</item>
/// <item><b>候选读取</b>（<see cref="ListPaymentCandidatesAsync"/> / <see cref="ListInvoiceCandidatesAsync"/>）：
/// 只读、有界，含资格文案与只读派生金额（绝不写入、绝不猜测发票）。</item>
/// </list>
/// <para>边界（重要）：本服务只读写 <c>SupplierPaymentInvoiceAllocations</c> 一张表，<strong>不</strong>改写付款单的
/// 审批 / 执行状态、金额、币种、付款方式、银行账户、供应商或备注，<strong>不</strong>改写供应商采购发票的类型 /
/// 代码 / 号码 / 日期 / 到期日 / 付款条件 / 金额 / 状态 / 关联行，<strong>不</strong>改写采购订单与到货进度、
/// ERP-049 的采购订单引用行、库存与库存成本、库存流水、退税记录、费用与供应商余额，
/// 也<strong>不</strong>执行任何付款、记账、核销或结算动作。</para>
/// </summary>
public static class SupplierPaymentInvoiceAllocationService
{
    // ==================== 1. 登记引用行（新增一条分摊证据） ====================

    /// <summary>
    /// 登记一条「付款单 → 供应商采购发票」付款引用行：全部校验通过后才写一行证据，
    /// 并写入付款单 / 供应商 / 发票的服务端快照与登记人（只取当前账号）。
    /// <para>校验顺序：付款单可用 → 币种口径 → 引用金额（精度 + 大于 0）→ 备注 → 发票资格
    /// （存在 / 未删除 / 已登记未作废 / 供应商一致 / 币种一致）→ 单付款单行数上限 → 重复有效行 →
    /// 付款单金额上限 → 单发票行数上限 → 发票未引用含税总额上限。</para>
    /// </summary>
    public static async Task<SupplierPaymentInvoiceAllocationDto> CreateAsync(
        IErpDbContext db, SupplierPaymentInvoiceAllocationSaveDto dto, string? recordedBy)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(dto);

        if (dto.PaymentId <= 0) throw BusinessException.InvalidParameter("请选择要引用的付款单");
        if (dto.PurchaseInvoiceId <= 0) throw BusinessException.InvalidParameter("请选择要引用的供应商采购发票");

        var payment = await LoadPaymentAsync(db, dto.PaymentId);
        var currency = SupplierPaymentInvoiceAllocationRules.NormalizeCurrencyStrict(payment.Currency.ToString());
        var amount = SupplierPaymentInvoiceAllocationRules.NormalizeAllocationAmount(dto.AllocatedAmount, currency);
        var remark = SupplierPaymentInvoiceAllocationRules.NormalizeRemark(dto.Remark);

        var invoice = await db.PurchaseInvoices.AsNoTracking()
            .FirstOrDefaultAsync(i => i.Id == dto.PurchaseInvoiceId);
        SupplierPaymentInvoiceAllocationRules.EnsureInvoiceLinkable(payment, invoice);
        if (invoice is null) throw BusinessException.NotFound("供应商采购发票不存在或已删除，不能引用");

        // 有效行（未作废、未删除）：重复、行数与金额上限都以它为准；作废行保留历史但不再占用额度
        var activeRows = await db.SupplierPaymentInvoiceAllocations.AsNoTracking()
            .Where(a => !a.IsDeleted
                        && a.Status == SupplierPaymentInvoiceAllocationRules.StatusActive
                        && (a.PaymentId == payment.Id || a.PurchaseInvoiceId == invoice.Id))
            .Select(a => new { a.PaymentId, a.PurchaseInvoiceId, a.AllocatedAmount })
            .ToListAsync();

        var paymentRows = activeRows.Where(a => a.PaymentId == payment.Id).ToList();
        if (paymentRows.Count >= SupplierPaymentInvoiceAllocationRules.MaxAllocationsPerPayment)
            throw BusinessException.RuleConflict(
                $"付款单「{payment.PaymentNo}」的有效发票引用行已达上限 "
                + $"{SupplierPaymentInvoiceAllocationRules.MaxAllocationsPerPayment} 条："
                + "如需新增请先作废不需要的行（作废保留历史，不物理删除）");

        var duplicate = paymentRows.FirstOrDefault(a => a.PurchaseInvoiceId == invoice.Id);
        if (duplicate is not null)
            throw BusinessException.Duplicate(
                $"付款单「{payment.PaymentNo}」已存在指向发票「"
                + $"{SupplierPaymentInvoiceAllocationRules.InvoiceIdentity(invoice)}」的有效引用行"
                + $"（金额 {duplicate.AllocatedAmount} {currency}）："
                + "同一发票在同一付款单内只能有一条有效引用行（如需更正请先作废原行，再登记新行；作废保留历史）");

        var paymentAmount = SupplierPaymentInvoiceAllocationRules.AuthoritativeAmount(payment.Amount, currency);
        var allocatedToInvoices = paymentRows.Sum(a => a.AllocatedAmount);
        var paymentTotal = allocatedToInvoices + amount;
        if (paymentTotal > paymentAmount)
            throw BusinessException.RuleConflict(
                $"付款单「{payment.PaymentNo}」的发票引用金额合计 {paymentTotal} 超过付款单金额 {paymentAmount} {currency}"
                + $"（已引用 {allocatedToInvoices}，本次 {amount}）：请调整引用金额"
                + "（付款单允许部分或全部未被引用，未引用部分保持为未引用金额）");

        var invoiceRows = activeRows.Where(a => a.PurchaseInvoiceId == invoice.Id).ToList();
        if (invoiceRows.Count >= SupplierPaymentInvoiceAllocationRules.MaxAllocationsPerInvoice)
            throw BusinessException.RuleConflict(
                $"发票「{SupplierPaymentInvoiceAllocationRules.InvoiceIdentity(invoice)}」的有效付款引用行已达上限 "
                + $"{SupplierPaymentInvoiceAllocationRules.MaxAllocationsPerInvoice} 条："
                + "如需新增请先作废不需要的行（作废保留历史，不物理删除）");

        var invoiceGross = SupplierPaymentInvoiceAllocationRules.AuthoritativeAmount(invoice.GrossAmount, currency);
        var allocatedOnInvoice = invoiceRows.Sum(a => a.AllocatedAmount);
        var invoiceTotal = allocatedOnInvoice + amount;
        if (invoiceTotal > invoiceGross)
            throw BusinessException.RuleConflict(
                $"发票「{SupplierPaymentInvoiceAllocationRules.InvoiceIdentity(invoice)}」的付款引用金额合计 "
                + $"{invoiceTotal} 超过含税总额 {invoiceGross} {currency}"
                + $"（已引用 {allocatedOnInvoice}，本次 {amount}）：请调整引用金额"
                + "（发票允许部分或全部未被引用，未引用部分保持为未引用含税总额）");

        var supplier = await db.BaseSuppliers.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == payment.SupplierId);

        var row = new SupplierPaymentInvoiceAllocation
        {
            PaymentId = payment.Id,
            PaymentNo = payment.PaymentNo ?? string.Empty,
            PaymentDate = payment.PaymentDate,
            PaymentStatus = (int)payment.Status,
            PaymentStatusText = SupplierPaymentInvoiceAllocationRules.PaymentStatusText((int)payment.Status),
            PaymentAmount = paymentAmount,
            PurchaseInvoiceId = invoice.Id,
            InvoiceType = invoice.InvoiceType ?? string.Empty,
            InvoiceCode = invoice.InvoiceCode ?? string.Empty,
            InvoiceNumber = invoice.InvoiceNumber ?? string.Empty,
            InvoiceIdentityText = SupplierPaymentInvoiceAllocationRules.InvoiceIdentity(invoice),
            InvoiceDate = invoice.InvoiceDate,
            InvoiceStatus = invoice.Status,
            InvoiceStatusText = SupplierPaymentInvoiceAllocationRules.InvoiceStatusText(invoice.Status),
            InvoiceGrossAmount = invoiceGross,
            SupplierId = payment.SupplierId,
            SupplierCode = supplier?.SupplierCode ?? string.Empty,
            SupplierName = supplier?.SupplierName ?? string.Empty,
            AllocatedAmount = amount,
            Currency = currency,
            Remark = remark,
            Status = SupplierPaymentInvoiceAllocationRules.StatusActive,
            AllocatedAt = DateTime.Now,
            RecordedBy = SupplierPaymentInvoiceAllocationRules.NormalizeRecordedBy(recordedBy)
        };

        db.SupplierPaymentInvoiceAllocations.Add(row);
        await db.SaveChangesAsync();

        return await MapAsync(db, row);
    }

    // ==================== 2. 作废引用行（保留历史，不删除） ====================

    /// <summary>
    /// 作废一条引用行（必须填写原因）：保留原始金额、付款单 / 供应商 / 发票快照、登记人与审计历史，
    /// 不物理删除、不改派、不静默替换已登记金额；重复作废被拒绝。
    /// <para>作废<strong>不</strong>改写付款单与发票的任何字段，也不产生任何收付款 / 记账 / 核销动作。</para>
    /// </summary>
    public static async Task<SupplierPaymentInvoiceAllocationDto> VoidAsync(
        IErpDbContext db, long allocationId, string? reason)
    {
        ArgumentNullException.ThrowIfNull(db);

        var row = await LoadAsync(db, allocationId);
        SupplierPaymentInvoiceAllocationRules.EnsureVoidable(
            row.Status, row.PaymentNo,
            SupplierPaymentInvoiceAllocationRules.InvoiceIdentityText(
                row.InvoiceType, row.InvoiceCode, row.InvoiceNumber, row.InvoiceIdentityText));
        var reasonText = SupplierPaymentInvoiceAllocationRules.NormalizeVoidReason(reason);

        row.Status = SupplierPaymentInvoiceAllocationRules.StatusVoided;
        row.VoidedAt = DateTime.Now;
        row.VoidReason = reasonText;
        row.UpdatedAt = DateTime.Now;
        await db.SaveChangesAsync();

        return await MapAsync(db, row);
    }

    // ==================== 3. 读取（台账 / 详情 / 付款单侧清单） ====================

    /// <summary>引用行详情（含付款单与发票可用性标注；只读，不写库）</summary>
    public static async Task<SupplierPaymentInvoiceAllocationDto> GetAsync(IErpDbContext db, long allocationId)
    {
        ArgumentNullException.ThrowIfNull(db);
        var row = await LoadAsync(db, allocationId);
        return await MapAsync(db, row);
    }

    /// <summary>
    /// 台账分页查询（只读，有界）：支持付款单 / 发票 / 供应商 / 状态 / 币种 / 登记时间区间 / 关键字过滤；
    /// 默认包含已作废历史（证据保留可读）。
    /// <para>本页行一次批量装载付款单与发票，<strong>无逐行数据库查询</strong>。</para>
    /// </summary>
    public static async Task<PagedResult<SupplierPaymentInvoiceAllocationDto>> ListAsync(
        IErpDbContext db, SupplierPaymentInvoiceAllocationQuery query)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(query);
        query.Normalize();

        var status = SupplierPaymentInvoiceAllocationRules.NormalizeStatusFilter(query.Status);
        var currency = string.IsNullOrWhiteSpace(query.Currency)
            ? null
            : SupplierPaymentInvoiceAllocationRules.NormalizeCurrencyStrict(query.Currency);

        var source = db.SupplierPaymentInvoiceAllocations.AsNoTracking().Where(x => !x.IsDeleted);
        if (query.PaymentId is not null) source = source.Where(x => x.PaymentId == query.PaymentId.Value);
        if (query.PurchaseInvoiceId is not null)
            source = source.Where(x => x.PurchaseInvoiceId == query.PurchaseInvoiceId.Value);
        if (query.SupplierId is not null) source = source.Where(x => x.SupplierId == query.SupplierId.Value);
        if (status is not null) source = source.Where(x => x.Status == status.Value);
        if (currency is not null) source = source.Where(x => x.Currency == currency);
        if (query.AllocatedDateFrom is not null)
            source = source.Where(x => x.AllocatedAt >= query.AllocatedDateFrom.Value.Date);
        if (query.AllocatedDateTo is not null)
            source = source.Where(x => x.AllocatedAt < query.AllocatedDateTo.Value.Date.AddDays(1));

        var keyword = SupplierPaymentInvoiceAllocationRules.NormalizeKeyword(query.Keyword);
        if (keyword.Length > 0)
            source = source.Where(x => x.PaymentNo.Contains(keyword)
                || x.InvoiceNumber.Contains(keyword)
                || x.InvoiceCode.Contains(keyword)
                || x.SupplierName.Contains(keyword)
                || x.SupplierCode.Contains(keyword));

        var total = await source.CountAsync();
        var rows = await source
            .OrderByDescending(x => x.AllocatedAt)
            .ThenByDescending(x => x.Id)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .ToListAsync();

        return new PagedResult<SupplierPaymentInvoiceAllocationDto>
        {
            Items = await MapManyAsync(db, rows),
            Total = total,
            Page = query.Page,
            PageSize = query.PageSize
        };
    }

    /// <summary>
    /// 指定付款单的引用行清单（只读、有界；付款单详情工作流用）：默认返回全部状态（含已作废历史），
    /// status 传 1 只看有效 / 传 2 只看已作废；不写库、不改写付款单。
    /// </summary>
    public static async Task<List<SupplierPaymentInvoiceAllocationDto>> ListForPaymentAsync(
        IErpDbContext db, long paymentId, int? status = null, int take = SupplierPaymentInvoiceAllocationRules.MaxAllocationsPerPayment)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (paymentId <= 0) throw BusinessException.InvalidParameter("付款单 Id 不合法");

        var statusFilter = SupplierPaymentInvoiceAllocationRules.NormalizeStatusFilter(status);
        var size = take <= 0 ? SupplierPaymentInvoiceAllocationRules.MaxAllocationsPerPayment
            : Math.Min(take, SupplierPaymentInvoiceAllocationQuery.MaxPageSize);

        var source = db.SupplierPaymentInvoiceAllocations.AsNoTracking()
            .Where(a => !a.IsDeleted && a.PaymentId == paymentId);
        if (statusFilter is not null) source = source.Where(a => a.Status == statusFilter.Value);

        var rows = await source
            .OrderByDescending(a => a.AllocatedAt)
            .ThenByDescending(a => a.Id)
            .Take(size)
            .ToListAsync();

        return await MapManyAsync(db, rows);
    }

    /// <summary>
    /// 指定供应商采购发票的引用行清单（只读、有界；发票工作流用）：默认返回全部状态（含已作废历史），
    /// status 传 1 只看有效 / 传 2 只看已作废；不写库、不改写发票。
    /// </summary>
    public static async Task<List<SupplierPaymentInvoiceAllocationDto>> ListForInvoiceAsync(
        IErpDbContext db, long purchaseInvoiceId, int? status = null, int take = SupplierPaymentInvoiceAllocationRules.MaxAllocationsPerInvoice)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (purchaseInvoiceId <= 0) throw BusinessException.InvalidParameter("发票 Id 不合法");

        var statusFilter = SupplierPaymentInvoiceAllocationRules.NormalizeStatusFilter(status);
        var size = take <= 0 ? SupplierPaymentInvoiceAllocationRules.MaxAllocationsPerInvoice
            : Math.Min(take, SupplierPaymentInvoiceAllocationQuery.MaxPageSize);

        var source = db.SupplierPaymentInvoiceAllocations.AsNoTracking()
            .Where(a => !a.IsDeleted && a.PurchaseInvoiceId == purchaseInvoiceId);
        if (statusFilter is not null) source = source.Where(a => a.Status == statusFilter.Value);

        var rows = await source
            .OrderByDescending(a => a.AllocatedAt)
            .ThenByDescending(a => a.Id)
            .Take(size)
            .ToListAsync();

        return await MapManyAsync(db, rows);
    }

    // ==================== 4. 只读汇总（付款单侧 / 发票侧） ====================

    /// <summary>
    /// 付款单侧汇总（只读派生）：付款单快照 + **有效发票引用行**已引用金额 / 未引用金额 / 行数 / 已作废行数
    /// + 有界明细，并单独标注 ERP-049 的采购订单引用维度（独立派生值，绝不与发票引用相加）。
    /// <para>统计口径：已引用金额只按 <c>Status = 有效</c> 的持久化行合计（已作废历史永不并入有效合计，
    /// 但单独计数并列出）；付款金额上限按币种精度取整，未引用金额下限 0。
    /// 明细行按有界上限返回（不逐行查库）；本方法<strong>不写库</strong>、不改写付款单与发票。</para>
    /// </summary>
    public static async Task<SupplierPaymentInvoiceAllocationPaymentSummaryDto> GetPaymentSummaryAsync(
        IErpDbContext db, long paymentId)
    {
        ArgumentNullException.ThrowIfNull(db);
        var payment = await LoadPaymentAsync(db, paymentId);

        var currency = CurrencyAmountRules.NormalizeCurrency(payment.Currency.ToString());
        var paymentAmount = SupplierPaymentInvoiceAllocationRules.AuthoritativeAmount(payment.Amount, currency);

        // 一次分组统计取回有效 / 已作废的行数与金额（不逐行查库）
        var stats = await db.SupplierPaymentInvoiceAllocations.AsNoTracking()
            .Where(a => !a.IsDeleted && a.PaymentId == payment.Id)
            .GroupBy(a => a.Status)
            .Select(g => new { Status = g.Key, Count = g.Count(), Amount = g.Sum(a => a.AllocatedAmount) })
            .ToListAsync();

        var active = stats.Where(s => s.Status == SupplierPaymentInvoiceAllocationRules.StatusActive).ToList();
        var allocated = active.Sum(s => s.Amount);
        var allocationCount = active.Sum(s => s.Count);
        var voidedCount = stats
            .Where(s => s.Status == SupplierPaymentInvoiceAllocationRules.StatusVoided)
            .Sum(s => s.Count);
        var unallocated = paymentAmount - allocated;
        if (unallocated < 0) unallocated = 0;

        // ERP-049 的「付款单 → 采购订单」维度：独立只读派生，仅供对照，绝不与本册金额相加
        var purchaseOrderStats = await db.SupplierPaymentAllocations.AsNoTracking()
            .Where(a => !a.IsDeleted
                        && a.PaymentId == payment.Id
                        && a.Status == SupplierPaymentAllocationRules.StatusActive)
            .GroupBy(a => a.Status)
            .Select(g => new { Count = g.Count(), Amount = g.Sum(a => a.AllocatedAmount) })
            .ToListAsync();
        var purchaseOrderAllocated = purchaseOrderStats.Sum(s => s.Amount);
        var purchaseOrderCount = purchaseOrderStats.Sum(s => s.Count);

        var rows = await ListForPaymentAsync(db, payment.Id);
        var supplier = await db.BaseSuppliers.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == payment.SupplierId);

        return new SupplierPaymentInvoiceAllocationPaymentSummaryDto(
            payment.Id,
            payment.PaymentNo ?? string.Empty,
            payment.PaymentDate,
            (int)payment.Status,
            SupplierPaymentInvoiceAllocationRules.PaymentStatusText((int)payment.Status),
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
            SupplierPaymentInvoiceAllocationRules.LinkageStatusOf(paymentAmount, allocated),
            SupplierPaymentInvoiceAllocationRules.PaymentLinkageText(
                paymentAmount, allocated, allocationCount, currency),
            purchaseOrderAllocated,
            purchaseOrderCount,
            SupplierPaymentInvoiceAllocationRules.SeparateEvidenceText,
            SupplierPaymentInvoiceAllocationRules.IsPaymentSelectable(payment),
            SupplierPaymentInvoiceAllocationRules.PaymentAvailabilityText(payment),
            SupplierPaymentInvoiceAllocationRules.RuleText,
            SupplierPaymentInvoiceAllocationRules.BoundaryText,
            rows);
    }

    /// <summary>
    /// 发票侧汇总（只读派生）：发票快照 + **全部有效引用行**（任意付款单）已引用金额 / 未引用含税总额 /
    /// 行数 / 已作废行数 + 有界明细。
    /// <para>统计口径：已引用金额只按 <c>Status = 有效</c> 的持久化行合计（已作废历史永不并入有效合计，
    /// 但单独计数并列出）；含税总额按币种精度取整，未引用金额下限 0。
    /// 未引用含税总额<strong>不是</strong>应付余额、账龄或付款依据；本方法不写库、不改写发票与付款单。</para>
    /// </summary>
    public static async Task<SupplierPaymentInvoiceAllocationInvoiceSummaryDto> GetInvoiceSummaryAsync(
        IErpDbContext db, long purchaseInvoiceId)
    {
        ArgumentNullException.ThrowIfNull(db);
        var invoice = await LoadInvoiceAsync(db, purchaseInvoiceId);

        var currency = CurrencyAmountRules.NormalizeCurrency(invoice.Currency);
        var grossAmount = SupplierPaymentInvoiceAllocationRules.AuthoritativeAmount(invoice.GrossAmount, currency);

        var stats = await db.SupplierPaymentInvoiceAllocations.AsNoTracking()
            .Where(a => !a.IsDeleted && a.PurchaseInvoiceId == invoice.Id)
            .GroupBy(a => a.Status)
            .Select(g => new { Status = g.Key, Count = g.Count(), Amount = g.Sum(a => a.AllocatedAmount) })
            .ToListAsync();

        var active = stats.Where(s => s.Status == SupplierPaymentInvoiceAllocationRules.StatusActive).ToList();
        var allocated = active.Sum(s => s.Amount);
        var allocationCount = active.Sum(s => s.Count);
        var voidedCount = stats
            .Where(s => s.Status == SupplierPaymentInvoiceAllocationRules.StatusVoided)
            .Sum(s => s.Count);
        var unallocated = grossAmount - allocated;
        if (unallocated < 0) unallocated = 0;

        var rows = await ListForInvoiceAsync(db, invoice.Id);
        var supplier = await db.BaseSuppliers.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == invoice.SupplierId);

        return new SupplierPaymentInvoiceAllocationInvoiceSummaryDto(
            invoice.Id,
            invoice.InvoiceType ?? string.Empty,
            PurchaseInvoiceRules.InvoiceTypeText(invoice.InvoiceType),
            invoice.InvoiceCode ?? string.Empty,
            invoice.InvoiceNumber ?? string.Empty,
            SupplierPaymentInvoiceAllocationRules.InvoiceIdentity(invoice),
            invoice.InvoiceDate,
            invoice.Status,
            SupplierPaymentInvoiceAllocationRules.InvoiceStatusText(invoice.Status),
            invoice.SupplierId,
            supplier?.SupplierCode ?? invoice.SupplierCode ?? string.Empty,
            supplier?.SupplierName ?? invoice.SupplierName ?? string.Empty,
            currency,
            CurrencyAmountRules.PrecisionOf(currency),
            grossAmount,
            allocated,
            unallocated,
            allocationCount,
            voidedCount,
            SupplierPaymentInvoiceAllocationRules.LinkageStatusOf(grossAmount, allocated),
            SupplierPaymentInvoiceAllocationRules.InvoiceLinkageText(
                grossAmount, allocated, allocationCount, currency),
            SupplierPaymentInvoiceAllocationRules.IsInvoiceSelectable(invoice),
            SupplierPaymentInvoiceAllocationRules.InvoiceAvailabilityText(invoice),
            SupplierPaymentInvoiceAllocationRules.RuleText,
            SupplierPaymentInvoiceAllocationRules.BoundaryText,
            rows);
    }

    // ==================== 5. 候选读取（只读、有界，绝不写库） ====================

    /// <summary>
    /// 可引用付款单候选（只读、有界）：只列出**既有、未删除**的付款单（可按供应商筛选、按付款单号关键字检索），
    /// 每张付款单附带**发票引用**有效行已引用金额、未引用金额、有效行数与资格文案
    /// （已全额引用 / 币种不受支持时不可引用）。
    /// <para><c>UnallocatedAmount</c> 只按本册持久化有效行派生，它不是银行未付金额、应付余额或发票余额，
    /// 也不代表付款是否真的发生，更不与 ERP-049 的采购订单引用金额相加。</para>
    /// </summary>
    public static async Task<List<SupplierPaymentInvoiceAllocationPaymentCandidateDto>> ListPaymentCandidatesAsync(
        IErpDbContext db, long? supplierId, string? keyword,
        int take = SupplierPaymentInvoiceAllocationRules.MaxPaymentCandidates)
    {
        ArgumentNullException.ThrowIfNull(db);

        var keywordText = SupplierPaymentInvoiceAllocationRules.NormalizeKeyword(keyword);
        var size = take <= 0 ? SupplierPaymentInvoiceAllocationRules.MaxPaymentCandidates
            : Math.Min(take, SupplierPaymentInvoiceAllocationRules.MaxPaymentCandidates);

        var source = db.FinancePayments.AsNoTracking().Where(p => !p.IsDeleted);
        if (supplierId is not null && supplierId.Value > 0)
            source = source.Where(p => p.SupplierId == supplierId.Value);
        if (keywordText.Length > 0) source = source.Where(p => p.PaymentNo.Contains(keywordText));

        var payments = await source
            .OrderByDescending(p => p.PaymentDate)
            .ThenByDescending(p => p.Id)
            .Take(size)
            .ToListAsync();
        if (payments.Count == 0) return new List<SupplierPaymentInvoiceAllocationPaymentCandidateDto>();

        var paymentIds = payments.Select(p => p.Id).ToList();

        // 一次查询取回候选付款单的有效发票引用行（已作废行不占用额度）
        var activeRows = await db.SupplierPaymentInvoiceAllocations.AsNoTracking()
            .Where(a => !a.IsDeleted
                        && a.Status == SupplierPaymentInvoiceAllocationRules.StatusActive
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
            var paymentAmount = SupplierPaymentInvoiceAllocationRules.AuthoritativeAmount(payment.Amount, currency);
            var unallocated = paymentAmount - allocated;
            if (unallocated < 0) unallocated = 0;

            suppliers.TryGetValue(payment.SupplierId, out var supplier);
            var (eligible, text) = SupplierPaymentInvoiceAllocationRules.EvaluatePaymentEligibility(payment, allocated);
            // 资格文案附带供应商可用性（停用 / 删除只作只读说明，历史快照照常可读）
            var eligibilityText = $"{text}；{SupplierPaymentInvoiceAllocationRules.SupplierAvailabilityText(supplier)}";

            return new SupplierPaymentInvoiceAllocationPaymentCandidateDto(
                payment.Id,
                payment.PaymentNo ?? string.Empty,
                payment.PaymentDate,
                (int)payment.Status,
                SupplierPaymentInvoiceAllocationRules.PaymentStatusText((int)payment.Status),
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
    /// 可引用供应商采购发票候选（只读、有界）：只列出**同供应商 + 同币种**的未删除发票
    /// （含草稿 / 已作废并显式标注不可引用），每张发票附带含税总额、本付款单已引用、其他付款单已引用
    /// 与剩余未被付款引用证据覆盖的金额（只按本册持久化有效行派生，下限 0）。
    /// </summary>
    public static async Task<List<SupplierPaymentInvoiceAllocationInvoiceCandidateDto>> ListInvoiceCandidatesAsync(
        IErpDbContext db, long paymentId, string? keyword,
        int take = SupplierPaymentInvoiceAllocationRules.MaxInvoiceCandidates)
    {
        ArgumentNullException.ThrowIfNull(db);

        var payment = await LoadPaymentAsync(db, paymentId);
        var currency = SupplierPaymentInvoiceAllocationRules.NormalizeCurrencyStrict(payment.Currency.ToString());
        var keywordText = SupplierPaymentInvoiceAllocationRules.NormalizeKeyword(keyword);
        var size = take <= 0 ? SupplierPaymentInvoiceAllocationRules.MaxInvoiceCandidates
            : Math.Min(take, SupplierPaymentInvoiceAllocationRules.MaxInvoiceCandidates);

        // 权威筛选：只列同供应商 + 同币种的未删除发票（草稿 / 已作废一并列出并标注不可引用）
        var source = db.PurchaseInvoices.AsNoTracking()
            .Where(i => !i.IsDeleted && i.SupplierId == payment.SupplierId && i.Currency == currency);
        if (keywordText.Length > 0)
            source = source.Where(i => i.InvoiceNumber.Contains(keywordText) || i.InvoiceCode.Contains(keywordText));

        var invoices = await source
            .OrderByDescending(i => i.InvoiceDate)
            .ThenByDescending(i => i.Id)
            .Take(size)
            .ToListAsync();
        if (invoices.Count == 0) return new List<SupplierPaymentInvoiceAllocationInvoiceCandidateDto>();

        var invoiceIds = invoices.Select(i => i.Id).ToList();

        // 一次查询取回候选发票的有效引用行（已作废行不再占用「已被付款引用证据覆盖」的金额）
        var rows = await db.SupplierPaymentInvoiceAllocations.AsNoTracking()
            .Where(a => !a.IsDeleted
                        && a.Status == SupplierPaymentInvoiceAllocationRules.StatusActive
                        && invoiceIds.Contains(a.PurchaseInvoiceId))
            .Select(a => new { a.PurchaseInvoiceId, a.PaymentId, a.AllocatedAmount })
            .ToListAsync();

        return invoices.Select(invoice =>
        {
            var invoiceRows = rows.Where(r => r.PurchaseInvoiceId == invoice.Id).ToList();
            var allocatedByThis = invoiceRows.Where(r => r.PaymentId == payment.Id).Sum(r => r.AllocatedAmount);
            var allocatedByOthers = invoiceRows.Where(r => r.PaymentId != payment.Id).Sum(r => r.AllocatedAmount);
            var gross = SupplierPaymentInvoiceAllocationRules.AuthoritativeAmount(invoice.GrossAmount, currency);
            var remaining = gross - allocatedByThis - allocatedByOthers;
            if (remaining < 0) remaining = 0;

            var (eligible, text) = SupplierPaymentInvoiceAllocationRules.EvaluateInvoiceEligibility(payment, invoice);
            if (eligible && remaining <= 0)
            {
                eligible = false;
                text = $"发票「{SupplierPaymentInvoiceAllocationRules.InvoiceIdentity(invoice)}」"
                    + $"（含税总额 {gross} {currency}）已被有效付款引用行占满，未引用含税总额为 0，不能新增引用";
            }

            return new SupplierPaymentInvoiceAllocationInvoiceCandidateDto(
                invoice.Id,
                invoice.InvoiceType ?? string.Empty,
                PurchaseInvoiceRules.InvoiceTypeText(invoice.InvoiceType),
                invoice.InvoiceCode ?? string.Empty,
                invoice.InvoiceNumber ?? string.Empty,
                SupplierPaymentInvoiceAllocationRules.InvoiceIdentity(invoice),
                invoice.InvoiceDate,
                invoice.Status,
                SupplierPaymentInvoiceAllocationRules.InvoiceStatusText(invoice.Status),
                invoice.Status == PurchaseInvoiceRules.StatusVoided,
                currency,
                invoice.SupplierId,
                invoice.SupplierCode ?? string.Empty,
                invoice.SupplierName ?? string.Empty,
                gross,
                allocatedByThis,
                allocatedByOthers,
                remaining,
                eligible,
                text);
        }).ToList();
    }

    // ==================== 6. 装载与映射（内部） ====================

    /// <summary>按 Id 装载未删除付款单（不存在 / 已删除 → 数据不存在）</summary>
    private static async Task<FinancePayment> LoadPaymentAsync(IErpDbContext db, long paymentId)
    {
        if (paymentId <= 0) throw BusinessException.InvalidParameter("付款单 Id 不合法");
        return await db.FinancePayments.FirstOrDefaultAsync(p => p.Id == paymentId && !p.IsDeleted)
            ?? throw BusinessException.NotFound($"付款单（Id={paymentId}）不存在或已删除，不能登记付款发票引用");
    }

    /// <summary>按 Id 装载未删除供应商采购发票（不存在 / 已删除 → 数据不存在）</summary>
    private static async Task<PurchaseInvoice> LoadInvoiceAsync(IErpDbContext db, long purchaseInvoiceId)
    {
        if (purchaseInvoiceId <= 0) throw BusinessException.InvalidParameter("发票 Id 不合法");
        return await db.PurchaseInvoices
                   .FirstOrDefaultAsync(i => i.Id == purchaseInvoiceId && !i.IsDeleted)
               ?? throw BusinessException.NotFound(
                   $"供应商采购发票（Id={purchaseInvoiceId}）不存在或已删除，不能登记付款发票引用");
    }

    /// <summary>按 Id 装载未删除引用行（不存在 / 已删除 → 数据不存在）</summary>
    private static async Task<SupplierPaymentInvoiceAllocation> LoadAsync(IErpDbContext db, long allocationId)
    {
        if (allocationId <= 0) throw BusinessException.InvalidParameter("引用行 Id 不合法");
        return await db.SupplierPaymentInvoiceAllocations
                   .FirstOrDefaultAsync(a => a.Id == allocationId && !a.IsDeleted)
               ?? throw BusinessException.NotFound($"付款发票引用行（Id={allocationId}）不存在或已删除");
    }

    /// <summary>单行映射（详情与单条操作返回用；只读标注，不写库）</summary>
    private static async Task<SupplierPaymentInvoiceAllocationDto> MapAsync(
        IErpDbContext db, SupplierPaymentInvoiceAllocation row)
    {
        ArgumentNullException.ThrowIfNull(row);
        var mapped = await MapManyAsync(db, new List<SupplierPaymentInvoiceAllocation> { row });
        return mapped[0];
    }

    /// <summary>
    /// 批量映射（台账分页与付款单 / 发票侧清单用）：付款单与发票各一次批量查询，
    /// <strong>绝无逐行数据库查询</strong>；付款单与发票可用性都是**只读标注**：
    /// 软删除 / 草稿 / 已作废都不改变历史引用证据的可读性。
    /// </summary>
    private static async Task<List<SupplierPaymentInvoiceAllocationDto>> MapManyAsync(
        IErpDbContext db, IReadOnlyList<SupplierPaymentInvoiceAllocation> rows)
    {
        if (rows.Count == 0) return new List<SupplierPaymentInvoiceAllocationDto>();

        var paymentIds = rows.Select(r => r.PaymentId).Distinct().ToList();
        var payments = (await db.FinancePayments.AsNoTracking()
                .Where(p => paymentIds.Contains(p.Id)).ToListAsync())
            .ToDictionary(p => p.Id);

        var invoiceIds = rows.Select(r => r.PurchaseInvoiceId).Distinct().ToList();
        var invoices = (await db.PurchaseInvoices.AsNoTracking()
                .Where(i => invoiceIds.Contains(i.Id)).ToListAsync())
            .ToDictionary(i => i.Id);

        return rows.Select(row => Map(row, payments, invoices)).ToList();
    }

    /// <summary>引用行实体 → DTO（含付款单 / 发票可用性标注；纯映射，不写库）</summary>
    private static SupplierPaymentInvoiceAllocationDto Map(
        SupplierPaymentInvoiceAllocation row,
        Dictionary<long, FinancePayment> payments,
        Dictionary<long, PurchaseInvoice> invoices)
    {
        ArgumentNullException.ThrowIfNull(row);

        var currency = CurrencyAmountRules.NormalizeCurrency(row.Currency);
        payments.TryGetValue(row.PaymentId, out var payment);
        invoices.TryGetValue(row.PurchaseInvoiceId, out var invoice);

        return new SupplierPaymentInvoiceAllocationDto(
            row.Id,
            row.PaymentId,
            row.PaymentNo ?? string.Empty,
            row.PaymentDate,
            row.PaymentStatus,
            string.IsNullOrEmpty(row.PaymentStatusText)
                ? SupplierPaymentInvoiceAllocationRules.PaymentStatusText(row.PaymentStatus)
                : row.PaymentStatusText,
            row.PaymentAmount,
            row.PurchaseInvoiceId,
            row.InvoiceType ?? string.Empty,
            PurchaseInvoiceRules.InvoiceTypeText(row.InvoiceType),
            row.InvoiceCode ?? string.Empty,
            row.InvoiceNumber ?? string.Empty,
            SupplierPaymentInvoiceAllocationRules.InvoiceIdentityText(
                row.InvoiceType, row.InvoiceCode, row.InvoiceNumber, row.InvoiceIdentityText),
            row.InvoiceDate,
            row.InvoiceStatus,
            string.IsNullOrEmpty(row.InvoiceStatusText)
                ? SupplierPaymentInvoiceAllocationRules.InvoiceStatusText(row.InvoiceStatus)
                : row.InvoiceStatusText,
            row.InvoiceGrossAmount,
            row.SupplierId,
            row.SupplierCode ?? string.Empty,
            row.SupplierName ?? string.Empty,
            currency,
            CurrencyAmountRules.PrecisionOf(currency),
            row.AllocatedAmount,
            row.Remark ?? string.Empty,
            row.Status,
            SupplierPaymentInvoiceAllocationRules.StatusText(row.Status),
            row.Status == SupplierPaymentInvoiceAllocationRules.StatusActive,
            row.Status == SupplierPaymentInvoiceAllocationRules.StatusVoided,
            row.AllocatedAt,
            row.RecordedBy ?? string.Empty,
            row.VoidedAt,
            row.VoidReason ?? string.Empty,
            SupplierPaymentInvoiceAllocationRules.IsPaymentSelectable(payment),
            SupplierPaymentInvoiceAllocationRules.PaymentAvailabilityText(payment),
            SupplierPaymentInvoiceAllocationRules.IsInvoiceSelectable(invoice),
            SupplierPaymentInvoiceAllocationRules.InvoiceAvailabilityText(invoice),
            row.CreatedAt,
            row.UpdatedAt,
            SupplierPaymentInvoiceAllocationRules.BoundaryText);
    }
}
