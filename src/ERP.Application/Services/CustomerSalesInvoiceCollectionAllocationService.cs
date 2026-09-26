using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ERP.Application.Services;

/// <summary>
/// 客户收款单 → 客户销项发票证据 收款分摊证据服务（ERP-073）。职责：
/// <list type="number">
/// <item><b>登记分摊行</b>（<see cref="CreateAsync"/>）：按**持久化标识符**（发票证据 Id + 收款单 Id）显式分摊，
/// 发票证据必须存在、未删除且**已登记**，收款单必须存在、未删除且**未取消**，两者客户与币种必须一致；
/// 分摊金额按币种精度取整且大于 0，并不得超过收款单可分摊余额与发票未分摊含税额；</item>
/// <item><b>作废分摊行</b>（<see cref="VoidAsync"/>）：必须填写原因，保留原始金额 / 快照 / 登记人与时间戳，
/// 不物理删除、不改派、不静默替换；</item>
/// <item><b>台账 / 详情 / 两侧汇总 / 两侧候选</b>（只读、有界）：批量装载，绝无逐行数据库查询。</item>
/// </list>
/// <para>证据维度分离（关键）：本服务只派生**本维度**（收款 → 客户销项发票）的已分摊与未分摊金额；
/// <strong>绝不</strong>把 ERP-053「收款单 → 销售订单」引用、ERP-055「销项发票 → 销售订单」分摊、
/// ERP-071「客户收款 → 代理服务费对账单」收款分摊的金额相加，也不把它们当作几张不同的收款单。</para>
/// <para>边界（重要）：本服务只读写 <c>CustomerSalesInvoiceCollectionAllocations</c> 一张表；<strong>不</strong>收款、
/// <strong>不</strong>付款、<strong>不</strong>记账或生成凭证 / 结算单、<strong>不</strong>核销、<strong>不</strong>催收或
/// 联系客户、<strong>不</strong>调用任何外部服务，也<strong>不</strong>改写收款单、发票证据、客户主数据、销售订单、
/// 装柜与装柜清单、单证、库存与库存成本、库存流水、费用与退税、结算与余额记录。</para>
/// </summary>
public static class CustomerSalesInvoiceCollectionAllocationService
{
    /// <summary>可分摊收款单候选单次返回上限（有界，避免一次拉全表）</summary>
    public const int MaxReceiptCandidates = CustomerSalesInvoiceCollectionAllocationRules.MaxReceiptCandidates;

    /// <summary>可承接分摊的发票候选单次返回上限（有界，避免一次拉全表）</summary>
    public const int MaxInvoiceCandidates = CustomerSalesInvoiceCollectionAllocationRules.MaxInvoiceCandidates;

    /// <summary>汇总明细单次返回上限（有界：一次读取不允许无界行数）</summary>
    public const int MaxDetailsPerSummary = 100;

    // ==================== 1. 登记分摊行（新增一条证据） ====================

    /// <summary>
    /// 登记一条收款分摊行：全部校验通过后才写一行证据，并写入发票 / 收款单 / 客户的服务端快照；
    /// 登记人由服务端按已认证身份写入（<paramref name="allocatedBy"/>，客户端不能提交该值）。
    /// </summary>
    public static async Task<CustomerSalesInvoiceCollectionAllocationDto> CreateAsync(
        IErpDbContext db, CustomerSalesInvoiceCollectionAllocationSaveDto dto, string? allocatedBy)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(dto);

        if (dto.CustomerSalesInvoiceEvidenceId <= 0)
            throw BusinessException.InvalidParameter(
                "请显式选择要分摊的客户销项发票证据（发票证据 Id 必须由用户显式选择，"
                + "系统不按单号文本、金额或相似度匹配发票）");
        if (dto.ReceiptId <= 0)
            throw BusinessException.InvalidParameter(
                "请显式选择要分摊的客户收款单（收款单 Id 必须由用户显式选择，"
                + "系统不按单号文本、金额或日期相似度匹配收款单）");

        var invoice = await LoadInvoiceAsync(db, dto.CustomerSalesInvoiceEvidenceId);
        var receipt = await LoadReceiptAsync(db, dto.ReceiptId);
        var invoiceCurrency = CustomerSalesInvoiceCollectionAllocationRules.NormalizeCurrencyStrict(invoice.Currency);
        var receiptCurrency = CustomerSalesInvoiceCollectionAllocationRules.NormalizeCurrencyStrict(
            receipt.Currency.ToString());

        CustomerSalesInvoiceCollectionAllocationRules.EnsureInvoiceAllocatable(
            invoice, allocatedAmount: 0m,
            CustomerSalesInvoiceCollectionAllocationRules.IdentityText(
                invoice.InvoiceType, invoice.InvoiceCode, invoice.InvoiceNumber));
        CustomerSalesInvoiceCollectionAllocationRules.EnsureReceiptAllocatable(
            receipt, allocatedAmount: 0m, receipt.ReceiptNo);
        CustomerSalesInvoiceCollectionAllocationRules.EnsureCompatible(receipt, receiptCurrency, invoice);

        var amount = CustomerSalesInvoiceCollectionAllocationRules.NormalizeAllocationAmount(
            dto.AllocatedAmount, invoiceCurrency);
        var remark = CustomerSalesInvoiceCollectionAllocationRules.NormalizeRemark(dto.Remark);

        var activeRows = await LoadActiveRowsAsync(db, invoice.Id, receipt.Id);
        var invoiceAllocated = activeRows
            .Where(a => a.CustomerSalesInvoiceEvidenceId == invoice.Id).Sum(a => a.AllocatedAmount);
        var receiptAllocated = activeRows
            .Where(a => a.ReceiptId == receipt.Id).Sum(a => a.AllocatedAmount);

        EnsureWithinLimit(
            activeRows.Count(a => a.CustomerSalesInvoiceEvidenceId == invoice.Id),
            activeRows.Count(a => a.ReceiptId == receipt.Id),
            invoice, receipt);

        var duplicate = activeRows.FirstOrDefault(a =>
            a.CustomerSalesInvoiceEvidenceId == invoice.Id && a.ReceiptId == receipt.Id);
        if (duplicate is not null)
            throw BusinessException.Duplicate(
                "同一发票证据与收款单只允许一条有效分摊行："
                + CustomerSalesInvoiceCollectionAllocationRules.AllocationIdentityText(
                    invoice.InvoiceType, invoice.InvoiceCode, invoice.InvoiceNumber, receipt.ReceiptNo)
                + $" 已存在有效分摊行（Id={duplicate.Id}，金额 {duplicate.AllocatedAmount} {invoiceCurrency}）；"
                + "重复提交被拒绝而不是合并或覆盖（如需更正请先作废原分摊行）");

        var receiptAvailable = CustomerSalesInvoiceCollectionAllocationRules.AuthoritativeAmount(
            receipt.Amount, receiptCurrency) - receiptAllocated;
        if (amount > receiptAvailable)
            throw BusinessException.RuleConflict(
                $"收款单「{receipt.ReceiptNo}」的可分摊余额为 {receiptAvailable} {invoiceCurrency}"
                + $"（收款金额已按币种精度取整，本维度已分摊 {receiptAllocated}），"
                + $"不能分摊 {amount} {invoiceCurrency}：系统不做超额分摊、不自动调整差额，"
                + "也不把差额猜测到别的记录");

        var invoiceUnallocated = CustomerSalesInvoiceCollectionAllocationRules.AuthoritativeAmount(
            invoice.GrossAmount, invoiceCurrency) - invoiceAllocated;
        if (amount > invoiceUnallocated)
            throw BusinessException.RuleConflict(
                $"发票「{CustomerSalesInvoiceCollectionAllocationRules.IdentityText(
                    invoice.InvoiceType, invoice.InvoiceCode, invoice.InvoiceNumber)}」"
                + $"的未分摊含税额为 {invoiceUnallocated} {invoiceCurrency}"
                + $"（发票含税总额已按币种精度取整，本维度已分摊 {invoiceAllocated}），"
                + $"不能分摊 {amount} {invoiceCurrency}：系统不做超额分摊、不自动调整差额，"
                + "也不把差额猜测到别的收款单");

        var customer = await LoadCustomerAsync(db, invoice.CustomerId);
        if (customer.Status != 1)
            throw BusinessException.RuleConflict(
                $"客户「{customer.CustomerName}」已停用：停用客户不能登记新的收款分摊"
                + "（历史分摊保持可读，不因停用被改写或删除）");
        var now = DateTime.Now;

        var allocation = new CustomerSalesInvoiceCollectionAllocation
        {
            CustomerSalesInvoiceEvidenceId = invoice.Id,
            InvoiceType = invoice.InvoiceType ?? string.Empty,
            InvoiceCode = invoice.InvoiceCode ?? string.Empty,
            InvoiceNumber = invoice.InvoiceNumber ?? string.Empty,
            InvoiceDate = invoice.InvoiceDate,
            InvoiceStatus = invoice.Status,
            InvoiceStatusText = CustomerSalesInvoiceCollectionAllocationRules.InvoiceStatusText(invoice.Status),
            InvoiceGrossAmount = CustomerSalesInvoiceCollectionAllocationRules.AuthoritativeAmount(
                invoice.GrossAmount, invoiceCurrency),
            InvoiceCurrency = invoiceCurrency,
            ReceiptId = receipt.Id,
            ReceiptNo = receipt.ReceiptNo ?? string.Empty,
            ReceiptDate = receipt.ReceiptDate,
            ReceiptStatus = (int)receipt.Status,
            ReceiptStatusText = CustomerSalesInvoiceCollectionAllocationRules.ReceiptStatusText((int)receipt.Status),
            ReceiptAmount = CustomerSalesInvoiceCollectionAllocationRules.AuthoritativeAmount(
                receipt.Amount, receiptCurrency),
            CustomerId = invoice.CustomerId,
            CustomerCode = customer.CustomerCode ?? string.Empty,
            CustomerName = customer.CustomerName ?? string.Empty,
            AllocatedAmount = amount,
            Currency = invoiceCurrency,
            Remark = remark,
            Status = CustomerSalesInvoiceCollectionAllocationRules.StatusActive,
            AllocatedAt = now,
            AllocatedBy = CustomerSalesInvoiceCollectionAllocationRules.NormalizeAllocatedBy(allocatedBy)
        };

        db.CustomerSalesInvoiceCollectionAllocations.Add(allocation);
        await db.SaveChangesAsync();

        return Map(allocation,
            new Dictionary<long, FinanceReceipt> { [receipt.Id] = receipt },
            new Dictionary<long, CustomerSalesInvoiceEvidence> { [invoice.Id] = invoice });
    }

    // ==================== 2. 作废分摊行（证据保留） ====================

    /// <summary>
    /// 作废一条收款分摊行（有效 → 已作废）：必须填写作废原因；<strong>保留</strong>原始分摊金额、发票与
    /// 收款单快照、客户快照、登记人与时间戳，不物理删除、不改派、不改写原始金额；作废后该组合可重新登记。
    /// </summary>
    public static async Task<CustomerSalesInvoiceCollectionAllocationDto> VoidAsync(
        IErpDbContext db, long allocationId, string? reason)
    {
        ArgumentNullException.ThrowIfNull(db);
        var allocation = await LoadAllocationAsync(db, allocationId);
        var identity = CustomerSalesInvoiceCollectionAllocationRules.AllocationIdentityText(
            allocation.InvoiceType, allocation.InvoiceCode, allocation.InvoiceNumber, allocation.ReceiptNo);
        CustomerSalesInvoiceCollectionAllocationRules.EnsureVoidable(allocation.Status, identity);

        var reasonText = CustomerSalesInvoiceCollectionAllocationRules.NormalizeVoidReason(reason);
        var now = DateTime.Now;
        allocation.Status = CustomerSalesInvoiceCollectionAllocationRules.StatusVoided;
        allocation.VoidedAt = now;
        allocation.VoidReason = reasonText;
        allocation.UpdatedAt = now;

        await db.SaveChangesAsync();

        return await MapOneAsync(db, allocation);
    }

    // ==================== 3. 台账 / 详情 / 两侧汇总 / 两侧候选（只读、有界） ====================

    /// <summary>分摊行详情（含发票与收款单可用性标注；只读）</summary>
    public static async Task<CustomerSalesInvoiceCollectionAllocationDto> GetAsync(IErpDbContext db, long allocationId)
    {
        ArgumentNullException.ThrowIfNull(db);
        var allocation = await LoadAllocationAsync(db, allocationId);
        return await MapOneAsync(db, allocation);
    }

    /// <summary>收款分摊行台账（分页，只读）：可按发票 / 收款单 / 客户 / 状态 / 币种 / 登记时间区间 / 关键字过滤。</summary>
    public static async Task<PagedResult<CustomerSalesInvoiceCollectionAllocationDto>> ListAsync(
        IErpDbContext db, CustomerSalesInvoiceCollectionAllocationQuery query)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(query);
        query.Normalize();

        var status = CustomerSalesInvoiceCollectionAllocationRules.NormalizeStatusFilter(query.Status);
        var currency = string.IsNullOrWhiteSpace(query.Currency)
            ? null
            : CustomerSalesInvoiceCollectionAllocationRules.NormalizeCurrencyStrict(query.Currency);
        var keyword = CustomerSalesInvoiceCollectionAllocationRules.NormalizeKeyword(query.Keyword);

        var source = db.CustomerSalesInvoiceCollectionAllocations.AsNoTracking().Where(x => !x.IsDeleted);
        if (query.CustomerSalesInvoiceEvidenceId is not null)
            source = source.Where(x => x.CustomerSalesInvoiceEvidenceId == query.CustomerSalesInvoiceEvidenceId.Value);
        if (query.ReceiptId is not null) source = source.Where(x => x.ReceiptId == query.ReceiptId.Value);
        if (query.CustomerId is not null) source = source.Where(x => x.CustomerId == query.CustomerId.Value);
        if (status is not null) source = source.Where(x => x.Status == status.Value);
        if (currency is not null) source = source.Where(x => x.Currency == currency);
        if (query.AllocatedDateFrom is not null)
            source = source.Where(x => x.AllocatedAt >= query.AllocatedDateFrom.Value.Date);
        if (query.AllocatedDateTo is not null)
            source = source.Where(x => x.AllocatedAt < query.AllocatedDateTo.Value.Date.AddDays(1));
        if (keyword.Length > 0)
        {
            source = source.Where(x => x.InvoiceNumber.Contains(keyword)
                || x.InvoiceCode.Contains(keyword)
                || x.ReceiptNo.Contains(keyword)
                || x.CustomerName.Contains(keyword)
                || x.CustomerCode.Contains(keyword)
                || x.Remark.Contains(keyword));
        }

        var total = await source.CountAsync();
        var page = await source
            .OrderByDescending(x => x.AllocatedAt)
            .ThenByDescending(x => x.Id)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .ToListAsync();

        return new PagedResult<CustomerSalesInvoiceCollectionAllocationDto>
        {
            Items = await MapManyAsync(db, page),
            Total = total,
            Page = query.Page,
            PageSize = query.PageSize
        };
    }

    /// <summary>指定发票的分摊行清单（只读、有界；发票详情工作流用）：默认返回全部状态（含已作废历史）。</summary>
    public static async Task<List<CustomerSalesInvoiceCollectionAllocationDto>> ListForInvoiceAsync(
        IErpDbContext db, long customerSalesInvoiceEvidenceId, int? status, int take)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (customerSalesInvoiceEvidenceId <= 0)
            throw BusinessException.InvalidParameter("请选择要查看分摊行的客户销项发票证据");

        var statusFilter = CustomerSalesInvoiceCollectionAllocationRules.NormalizeStatusFilter(status);
        var size = Math.Clamp(
            take <= 0 ? CustomerSalesInvoiceCollectionAllocationRules.MaxAllocationsPerInvoice : take,
            1, MaxDetailsPerSummary);

        var source = db.CustomerSalesInvoiceCollectionAllocations.AsNoTracking()
            .Where(a => !a.IsDeleted && a.CustomerSalesInvoiceEvidenceId == customerSalesInvoiceEvidenceId);
        if (statusFilter is not null) source = source.Where(a => a.Status == statusFilter.Value);

        var rows = await source
            .OrderBy(a => a.Status)
            .ThenBy(a => a.AllocatedAt)
            .ThenBy(a => a.Id)
            .Take(size)
            .ToListAsync();

        return await MapManyAsync(db, rows);
    }

    /// <summary>指定收款单的分摊行清单（只读、有界；收款单工作流用）：默认返回全部状态（含已作废历史）。</summary>
    public static async Task<List<CustomerSalesInvoiceCollectionAllocationDto>> ListForReceiptAsync(
        IErpDbContext db, long receiptId, int? status, int take)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (receiptId <= 0)
            throw BusinessException.InvalidParameter("请选择要查看分摊行的客户收款单");

        var statusFilter = CustomerSalesInvoiceCollectionAllocationRules.NormalizeStatusFilter(status);
        var size = Math.Clamp(
            take <= 0 ? CustomerSalesInvoiceCollectionAllocationRules.MaxAllocationsPerReceipt : take,
            1, MaxDetailsPerSummary);

        var source = db.CustomerSalesInvoiceCollectionAllocations.AsNoTracking()
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
    /// 发票侧汇总（只读派生）：发票快照 + **本维度**有效分摊金额 / 未分摊含税额、有效行数与已作废行数 + 有界逐行明细。
    /// </summary>
    public static async Task<CustomerSalesInvoiceCollectionAllocationInvoiceSummaryDto> GetInvoiceSummaryAsync(
        IErpDbContext db, long customerSalesInvoiceEvidenceId)
    {
        ArgumentNullException.ThrowIfNull(db);
        var invoice = await LoadInvoiceAsync(db, customerSalesInvoiceEvidenceId);

        var currency = CurrencyAmountRules.NormalizeCurrency(invoice.Currency);
        var total = CustomerSalesInvoiceCollectionAllocationRules.AuthoritativeAmount(
            invoice.GrossAmount, currency);

        var stats = await db.CustomerSalesInvoiceCollectionAllocations.AsNoTracking()
            .Where(a => !a.IsDeleted && a.CustomerSalesInvoiceEvidenceId == invoice.Id)
            .GroupBy(a => a.Status)
            .Select(g => new { Status = g.Key, Count = g.Count(), Amount = g.Sum(a => a.AllocatedAmount) })
            .ToListAsync();

        var active = stats
            .Where(s => s.Status == CustomerSalesInvoiceCollectionAllocationRules.StatusActive).ToList();
        var allocated = active.Sum(s => s.Amount);
        var allocationCount = active.Sum(s => s.Count);
        var voidedCount = stats
            .Where(s => s.Status == CustomerSalesInvoiceCollectionAllocationRules.StatusVoided)
            .Sum(s => s.Count);
        var unallocated = total - allocated;
        if (unallocated < 0) unallocated = 0;

        var rows = await ListForInvoiceAsync(db, invoice.Id, null, MaxDetailsPerSummary);
        var invoiceAvailable = CustomerSalesInvoiceCollectionAllocationRules
            .EvaluateInvoiceEligibility(invoice, allocated).Eligible;

        return new CustomerSalesInvoiceCollectionAllocationInvoiceSummaryDto(
            invoice.Id,
            invoice.InvoiceType ?? string.Empty,
            invoice.InvoiceCode ?? string.Empty,
            invoice.InvoiceNumber ?? string.Empty,
            CustomerSalesInvoiceCollectionAllocationRules.IdentityText(
                invoice.InvoiceType, invoice.InvoiceCode, invoice.InvoiceNumber),
            invoice.InvoiceDate,
            invoice.Status,
            CustomerSalesInvoiceCollectionAllocationRules.InvoiceStatusText(invoice.Status),
            invoice.CustomerId,
            invoice.CustomerCode ?? string.Empty,
            invoice.CustomerName ?? string.Empty,
            currency,
            CurrencyAmountRules.PrecisionOf(currency),
            total,
            allocated,
            unallocated,
            allocationCount,
            voidedCount,
            CustomerSalesInvoiceCollectionAllocationRules.LinkageStatusOf(total, allocated),
            CustomerSalesInvoiceCollectionAllocationRules.LinkageText(
                total, allocated, allocationCount, currency, "发票在本维度"),
            invoiceAvailable,
            CustomerSalesInvoiceCollectionAllocationRules.InvoiceAvailabilityText(invoice),
            CustomerSalesInvoiceCollectionAllocationRules.RuleText,
            CustomerSalesInvoiceCollectionAllocationRules.AmountRuleText,
            CustomerSalesInvoiceCollectionAllocationRules.DimensionSeparationText,
            CustomerSalesInvoiceCollectionAllocationRules.BoundaryText,
            rows);
    }

    /// <summary>收款单侧汇总（只读派生）：收款单快照 + **本维度**有效分摊金额 / 可分摊余额、行数 + 有界逐行明细。</summary>
    public static async Task<CustomerSalesInvoiceCollectionAllocationReceiptSummaryDto> GetReceiptSummaryAsync(
        IErpDbContext db, long receiptId)
    {
        ArgumentNullException.ThrowIfNull(db);
        var receipt = await LoadReceiptAsync(db, receiptId);

        var currency = CurrencyAmountRules.NormalizeCurrency(receipt.Currency.ToString());
        var amount = CustomerSalesInvoiceCollectionAllocationRules.AuthoritativeAmount(receipt.Amount, currency);

        var stats = await db.CustomerSalesInvoiceCollectionAllocations.AsNoTracking()
            .Where(a => !a.IsDeleted && a.ReceiptId == receipt.Id)
            .GroupBy(a => a.Status)
            .Select(g => new { Status = g.Key, Count = g.Count(), Amount = g.Sum(a => a.AllocatedAmount) })
            .ToListAsync();

        var active = stats
            .Where(s => s.Status == CustomerSalesInvoiceCollectionAllocationRules.StatusActive).ToList();
        var allocated = active.Sum(s => s.Amount);
        var allocationCount = active.Sum(s => s.Count);
        var voidedCount = stats
            .Where(s => s.Status == CustomerSalesInvoiceCollectionAllocationRules.StatusVoided)
            .Sum(s => s.Count);
        var unallocated = amount - allocated;
        if (unallocated < 0) unallocated = 0;

        var rows = await ListForReceiptAsync(db, receipt.Id, null, MaxDetailsPerSummary);
        var customer = await LoadCustomerAsync(db, receipt.CustomerId);

        return new CustomerSalesInvoiceCollectionAllocationReceiptSummaryDto(
            receipt.Id,
            receipt.ReceiptNo ?? string.Empty,
            receipt.ReceiptDate,
            (int)receipt.Status,
            CustomerSalesInvoiceCollectionAllocationRules.ReceiptStatusText((int)receipt.Status),
            receipt.CustomerId,
            customer.CustomerCode ?? string.Empty,
            customer.CustomerName ?? string.Empty,
            currency,
            CurrencyAmountRules.PrecisionOf(currency),
            amount,
            allocated,
            unallocated,
            allocationCount,
            voidedCount,
            CustomerSalesInvoiceCollectionAllocationRules.LinkageStatusOf(amount, allocated),
            CustomerSalesInvoiceCollectionAllocationRules.LinkageText(
                amount, allocated, allocationCount, currency, "收款单在本维度"),
            CustomerSalesInvoiceCollectionAllocationRules.EvaluateReceiptEligibility(receipt, allocated).Eligible,
            CustomerSalesInvoiceCollectionAllocationRules.ReceiptAvailabilityText(receipt),
            CustomerSalesInvoiceCollectionAllocationRules.RuleText,
            CustomerSalesInvoiceCollectionAllocationRules.AmountRuleText,
            CustomerSalesInvoiceCollectionAllocationRules.DimensionSeparationText,
            CustomerSalesInvoiceCollectionAllocationRules.BoundaryText,
            rows);
    }

    // ==================== 4. 候选读取（只读、有界，绝不写库） ====================

    /// <summary>可分摊收款单候选（只读、有界）：必须显式给出客户与币种；只列未删除且未取消的收款单。</summary>
    public static async Task<List<CustomerSalesInvoiceCollectionAllocationReceiptCandidateDto>> ListReceiptCandidatesAsync(
        IErpDbContext db, long customerId, string? currency, string? keyword, int take)
    {
        ArgumentNullException.ThrowIfNull(db);

        if (customerId <= 0)
            throw BusinessException.InvalidParameter(
                "请先选择客户：收款单候选的资格判定依赖客户（系统不按相似度推荐收款单）");
        if (string.IsNullOrWhiteSpace(currency))
            throw BusinessException.InvalidParameter(
                "请先选择发票币种：收款单候选的资格判定依赖币种（系统不替收款单补一个币种）");

        var normalizedCurrency = CustomerSalesInvoiceCollectionAllocationRules.NormalizeCurrencyStrict(currency);
        var currencyValue = Enum.Parse<Currency>(normalizedCurrency);
        var key = CustomerSalesInvoiceCollectionAllocationRules.NormalizeKeyword(keyword);
        var limit = Math.Clamp(take <= 0 ? MaxReceiptCandidates : take, 1, MaxReceiptCandidates);

        var customer = await LoadCustomerAsync(db, customerId);

        var receipts = db.FinanceReceipts.AsNoTracking()
            .Where(r => !r.IsDeleted
                        && r.CustomerId == customerId
                        && r.Currency == currencyValue
                        && r.Status != DocumentStatus.Cancelled);
        if (key.Length > 0) receipts = receipts.Where(r => r.ReceiptNo.Contains(key));
        var rows = await receipts
            .OrderByDescending(r => r.ReceiptDate).ThenByDescending(r => r.Id)
            .Take(limit)
            .ToListAsync();

        var totals = await LoadActiveTotalsForReceiptsAsync(db, rows.Select(r => r.Id).ToList());

        return rows.Select(receipt =>
        {
            var allocated = totals.TryGetValue(receipt.Id, out var value) ? value.Amount : 0m;
            var count = totals.TryGetValue(receipt.Id, out var value2) ? value2.Count : 0;
            var receivedTotal = CustomerSalesInvoiceCollectionAllocationRules.AuthoritativeAmount(
                receipt.Amount, normalizedCurrency);
            var remaining = receivedTotal - allocated;
            if (remaining < 0) remaining = 0;
            var (eligible, text) = CustomerSalesInvoiceCollectionAllocationRules.EvaluateReceiptEligibility(
                receipt, allocated);

            return new CustomerSalesInvoiceCollectionAllocationReceiptCandidateDto(
                receipt.Id,
                receipt.ReceiptNo ?? string.Empty,
                receipt.ReceiptDate,
                (int)receipt.Status,
                CustomerSalesInvoiceCollectionAllocationRules.ReceiptStatusText((int)receipt.Status),
                receipt.CustomerId,
                customer.CustomerCode ?? string.Empty,
                customer.CustomerName ?? string.Empty,
                normalizedCurrency,
                CurrencyAmountRules.PrecisionOf(normalizedCurrency),
                receivedTotal,
                allocated,
                remaining,
                count,
                eligible,
                text);
        }).ToList();
    }

    /// <summary>可承接收款分摊的发票候选（只读、有界）：只列未删除且已登记的发票证据（草稿 / 已作废不出现）。</summary>
    public static async Task<List<CustomerSalesInvoiceCollectionAllocationInvoiceCandidateDto>> ListInvoiceCandidatesAsync(
        IErpDbContext db, long customerId, string? currency, string? keyword, int take)
    {
        ArgumentNullException.ThrowIfNull(db);

        if (customerId <= 0)
            throw BusinessException.InvalidParameter(
                "请先选择客户：发票候选的资格判定依赖客户（系统不按相似度推荐发票）");
        if (string.IsNullOrWhiteSpace(currency))
            throw BusinessException.InvalidParameter(
                "请先选择币种：发票候选的资格判定依赖币种（系统不替发票补一个币种）");

        var normalizedCurrency = CustomerSalesInvoiceCollectionAllocationRules.NormalizeCurrencyStrict(currency);
        var key = CustomerSalesInvoiceCollectionAllocationRules.NormalizeKeyword(keyword);
        var limit = Math.Clamp(take <= 0 ? MaxInvoiceCandidates : take, 1, MaxInvoiceCandidates);

        var customer = await LoadCustomerAsync(db, customerId);

        var invoices = db.CustomerSalesInvoiceEvidences.AsNoTracking()
            .Where(x => !x.IsDeleted
                        && x.Status == CustomerSalesInvoiceEvidenceRules.StatusRecorded
                        && x.CustomerId == customerId
                        && x.Currency == normalizedCurrency);
        if (key.Length > 0)
            invoices = invoices.Where(x => x.InvoiceNumber.Contains(key) || x.InvoiceCode.Contains(key));
        var rows = await invoices
            .OrderByDescending(x => x.InvoiceDate).ThenByDescending(x => x.Id)
            .Take(limit)
            .ToListAsync();

        var totals = await LoadActiveTotalsForInvoicesAsync(db, rows.Select(x => x.Id).ToList());

        return rows.Select(invoice =>
        {
            var allocated = totals.TryGetValue(invoice.Id, out var total) ? total.Amount : 0m;
            var count = totals.TryGetValue(invoice.Id, out var total2) ? total2.Count : 0;
            var invoiceTotal = CustomerSalesInvoiceCollectionAllocationRules.AuthoritativeAmount(
                invoice.GrossAmount, normalizedCurrency);
            var remaining = invoiceTotal - allocated;
            if (remaining < 0) remaining = 0;
            var (eligible, text) = CustomerSalesInvoiceCollectionAllocationRules.EvaluateInvoiceEligibility(
                invoice, allocated);

            return new CustomerSalesInvoiceCollectionAllocationInvoiceCandidateDto(
                invoice.Id,
                invoice.InvoiceType ?? string.Empty,
                invoice.InvoiceCode ?? string.Empty,
                invoice.InvoiceNumber ?? string.Empty,
                CustomerSalesInvoiceCollectionAllocationRules.IdentityText(
                    invoice.InvoiceType, invoice.InvoiceCode, invoice.InvoiceNumber),
                invoice.InvoiceDate,
                invoice.Status,
                CustomerSalesInvoiceCollectionAllocationRules.InvoiceStatusText(invoice.Status),
                invoice.CustomerId,
                customer.CustomerCode ?? string.Empty,
                customer.CustomerName ?? string.Empty,
                normalizedCurrency,
                CurrencyAmountRules.PrecisionOf(normalizedCurrency),
                invoiceTotal,
                allocated,
                remaining,
                count,
                eligible,
                text);
        }).ToList();
    }

    /// <summary>模块元数据（只读）：支持币种、有界额度与接口 / 界面 / 文档同源的口径文案。</summary>
    public static CustomerSalesInvoiceCollectionAllocationMetadataDto GetMetadata()
        => new(
            CustomerSalesInvoiceCollectionAllocationRules.SupportedCurrencies.ToList(),
            CustomerSalesInvoiceCollectionAllocationRules.MaxAllocationsPerInvoice,
            CustomerSalesInvoiceCollectionAllocationRules.MaxAllocationsPerReceipt,
            MaxReceiptCandidates,
            MaxInvoiceCandidates,
            CustomerSalesInvoiceCollectionAllocationQuery.MaxPageSize,
            CustomerSalesInvoiceCollectionAllocationRules.RuleText,
            CustomerSalesInvoiceCollectionAllocationRules.AmountRuleText,
            CustomerSalesInvoiceCollectionAllocationRules.UniquenessRuleText,
            CustomerSalesInvoiceCollectionAllocationRules.DimensionSeparationText,
            CustomerSalesInvoiceCollectionAllocationRules.HistoricalEvidenceText,
            CustomerSalesInvoiceCollectionAllocationRules.BoundaryText);

    // ==================== 5. 装载、限额与映射（内部） ====================

    /// <summary>按 Id 装载未删除的发票证据（不存在 / 已删除 → 数据不存在；只读快照用途）</summary>
    private static async Task<CustomerSalesInvoiceEvidence> LoadInvoiceAsync(
        IErpDbContext db, long customerSalesInvoiceEvidenceId)
    {
        if (customerSalesInvoiceEvidenceId <= 0)
            throw BusinessException.InvalidParameter("请选择要分摊的客户销项发票证据");
        return await db.CustomerSalesInvoiceEvidences.AsNoTracking()
                   .FirstOrDefaultAsync(x => x.Id == customerSalesInvoiceEvidenceId && !x.IsDeleted)
               ?? throw BusinessException.NotFound(
                   $"客户销项发票证据（Id={customerSalesInvoiceEvidenceId}）不存在或已删除，不能登记收款分摊");
    }

    /// <summary>按 Id 装载未删除的收款单（不存在 / 已删除 → 数据不存在；只读快照用途）</summary>
    private static async Task<FinanceReceipt> LoadReceiptAsync(IErpDbContext db, long receiptId)
    {
        if (receiptId <= 0)
            throw BusinessException.InvalidParameter("请选择要分摊的客户收款单");
        return await db.FinanceReceipts.AsNoTracking()
                   .FirstOrDefaultAsync(r => r.Id == receiptId && !r.IsDeleted)
               ?? throw BusinessException.NotFound(
                   $"客户收款单（Id={receiptId}）不存在或已删除，不能登记收款分摊");
    }

    /// <summary>按 Id 装载未删除的分摊行（**跟踪**实体：作废需要写回；不存在 / 已删除 → 数据不存在）</summary>
    private static async Task<CustomerSalesInvoiceCollectionAllocation> LoadAllocationAsync(
        IErpDbContext db, long allocationId)
    {
        if (allocationId <= 0)
            throw BusinessException.InvalidParameter("请选择要操作的收款分摊行");
        return await db.CustomerSalesInvoiceCollectionAllocations
                   .FirstOrDefaultAsync(a => a.Id == allocationId && !a.IsDeleted)
               ?? throw BusinessException.NotFound($"收款分摊行（Id={allocationId}）不存在或已删除");
    }

    /// <summary>按 Id 装载未删除的客户（不存在 / 已删除 → 数据不存在；客户停用只影响新登记，历史证据照常可读）</summary>
    private static async Task<BaseCustomer> LoadCustomerAsync(IErpDbContext db, long customerId)
        => await db.BaseCustomers.AsNoTracking()
               .FirstOrDefaultAsync(c => c.Id == customerId && !c.IsDeleted)
           ?? throw BusinessException.NotFound(
               $"客户（Id={customerId}）不存在或已删除，不能登记收款分摊");

    /// <summary>一次装载「该发票 + 该收款单」的全部**有效行**（单次数据集访问，绝无逐行查库）。</summary>
    private static async Task<List<CustomerSalesInvoiceCollectionAllocation>> LoadActiveRowsAsync(
        IErpDbContext db, long customerSalesInvoiceEvidenceId, long receiptId)
        => await db.CustomerSalesInvoiceCollectionAllocations.AsNoTracking()
            .Where(a => !a.IsDeleted
                        && a.Status == CustomerSalesInvoiceCollectionAllocationRules.StatusActive
                        && (a.CustomerSalesInvoiceEvidenceId == customerSalesInvoiceEvidenceId || a.ReceiptId == receiptId))
            .ToListAsync();

    /// <summary>有效行数上限校验（单条发票 / 单张收款单各自有界；超限拒绝并给出可读原因，不静默截断）</summary>
    private static void EnsureWithinLimit(
        int invoiceActiveCount, int receiptActiveCount,
        CustomerSalesInvoiceEvidence invoice, FinanceReceipt receipt)
    {
        if (invoiceActiveCount >= CustomerSalesInvoiceCollectionAllocationRules.MaxAllocationsPerInvoice)
            throw BusinessException.RuleConflict(
                $"发票「{CustomerSalesInvoiceCollectionAllocationRules.IdentityText(
                    invoice.InvoiceType, invoice.InvoiceCode, invoice.InvoiceNumber)}」的有效分摊行已达上限 "
                + $"{CustomerSalesInvoiceCollectionAllocationRules.MaxAllocationsPerInvoice} 条："
                + "如需新增请先作废不需要的行（作废保留历史，不物理删除）");

        if (receiptActiveCount >= CustomerSalesInvoiceCollectionAllocationRules.MaxAllocationsPerReceipt)
            throw BusinessException.RuleConflict(
                $"收款单「{receipt.ReceiptNo}」的有效分摊行已达上限 "
                + $"{CustomerSalesInvoiceCollectionAllocationRules.MaxAllocationsPerReceipt} 条："
                + "如需新增请先作废不需要的行（作废保留历史，不物理删除）");
    }

    /// <summary>按收款单批量取回**本维度**有效行合计与行数（一次分组查询，无逐行查库）</summary>
    private static async Task<Dictionary<long, (decimal Amount, int Count)>> LoadActiveTotalsForReceiptsAsync(
        IErpDbContext db, IReadOnlyList<long> receiptIds)
    {
        var rows = await db.CustomerSalesInvoiceCollectionAllocations.AsNoTracking()
            .Where(a => !a.IsDeleted
                        && a.Status == CustomerSalesInvoiceCollectionAllocationRules.StatusActive
                        && receiptIds.Contains(a.ReceiptId))
            .GroupBy(a => a.ReceiptId)
            .Select(g => new { Key = g.Key, Amount = g.Sum(a => a.AllocatedAmount), Count = g.Count() })
            .ToListAsync();
        return rows.ToDictionary(r => r.Key, r => (r.Amount, r.Count));
    }

    /// <summary>按发票批量取回**本维度**有效行合计与行数（一次分组查询，无逐行查库）</summary>
    private static async Task<Dictionary<long, (decimal Amount, int Count)>> LoadActiveTotalsForInvoicesAsync(
        IErpDbContext db, IReadOnlyList<long> invoiceIds)
    {
        var rows = await db.CustomerSalesInvoiceCollectionAllocations.AsNoTracking()
            .Where(a => !a.IsDeleted
                        && a.Status == CustomerSalesInvoiceCollectionAllocationRules.StatusActive
                        && invoiceIds.Contains(a.CustomerSalesInvoiceEvidenceId))
            .GroupBy(a => a.CustomerSalesInvoiceEvidenceId)
            .Select(g => new { Key = g.Key, Amount = g.Sum(a => a.AllocatedAmount), Count = g.Count() })
            .ToListAsync();
        return rows.ToDictionary(r => r.Key, r => (r.Amount, r.Count));
    }

    // ==================== 6. 实体 → DTO（纯映射，不写库） ====================

    /// <summary>分摊行实体 → DTO：快照全部取自行内服务端权威快照，只按当前收款单与发票的只读可用性做标注。</summary>
    private static CustomerSalesInvoiceCollectionAllocationDto Map(
        CustomerSalesInvoiceCollectionAllocation row,
        IReadOnlyDictionary<long, FinanceReceipt> receipts,
        IReadOnlyDictionary<long, CustomerSalesInvoiceEvidence> invoices)
    {
        ArgumentNullException.ThrowIfNull(row);

        var currency = CurrencyAmountRules.NormalizeCurrency(row.Currency);
        var decimals = CurrencyAmountRules.PrecisionOf(currency);

        receipts.TryGetValue(row.ReceiptId, out var receipt);
        invoices.TryGetValue(row.CustomerSalesInvoiceEvidenceId, out var invoice);

        var receiptAvailable = receipt is not null
            && CustomerSalesInvoiceCollectionAllocationRules.EvaluateReceiptEligibility(receipt, 0m).Eligible;
        var invoiceAvailable = invoice is not null
            && CustomerSalesInvoiceCollectionAllocationRules.EvaluateInvoiceEligibility(invoice, 0m).Eligible;

        return new CustomerSalesInvoiceCollectionAllocationDto(
            row.Id,
            row.CustomerSalesInvoiceEvidenceId,
            row.InvoiceType ?? string.Empty,
            row.InvoiceCode ?? string.Empty,
            row.InvoiceNumber ?? string.Empty,
            CustomerSalesInvoiceCollectionAllocationRules.AllocationIdentityText(
                row.InvoiceType, row.InvoiceCode, row.InvoiceNumber, row.ReceiptNo),
            row.InvoiceDate,
            row.InvoiceStatus,
            row.InvoiceStatusText ?? CustomerSalesInvoiceCollectionAllocationRules.InvoiceStatusText(row.InvoiceStatus),
            row.InvoiceGrossAmount,
            CurrencyAmountRules.NormalizeCurrency(row.InvoiceCurrency),
            CustomerSalesInvoiceCollectionAllocationRules.AmountText(row.InvoiceGrossAmount, row.InvoiceCurrency),
            row.ReceiptId,
            row.ReceiptNo ?? string.Empty,
            row.ReceiptDate,
            row.ReceiptStatus,
            row.ReceiptStatusText ?? CustomerSalesInvoiceCollectionAllocationRules.ReceiptStatusText(row.ReceiptStatus),
            row.ReceiptAmount,
            CustomerSalesInvoiceCollectionAllocationRules.AmountText(row.ReceiptAmount, currency),
            row.CustomerId,
            row.CustomerCode ?? string.Empty,
            row.CustomerName ?? string.Empty,
            currency,
            decimals,
            row.AllocatedAmount,
            CustomerSalesInvoiceCollectionAllocationRules.AmountText(row.AllocatedAmount, currency),
            row.Remark ?? string.Empty,
            row.Status,
            CustomerSalesInvoiceCollectionAllocationRules.StatusText(row.Status),
            row.Status == CustomerSalesInvoiceCollectionAllocationRules.StatusActive,
            row.Status == CustomerSalesInvoiceCollectionAllocationRules.StatusVoided,
            row.AllocatedAt,
            row.AllocatedBy ?? string.Empty,
            row.VoidedAt,
            row.VoidReason ?? string.Empty,
            receiptAvailable,
            CustomerSalesInvoiceCollectionAllocationRules.ReceiptAvailabilityText(receipt),
            invoiceAvailable,
            CustomerSalesInvoiceCollectionAllocationRules.InvoiceAvailabilityText(invoice),
            row.CreatedAt,
            row.UpdatedAt,
            CustomerSalesInvoiceCollectionAllocationRules.RuleText,
            CustomerSalesInvoiceCollectionAllocationRules.AmountRuleText,
            CustomerSalesInvoiceCollectionAllocationRules.DimensionSeparationText,
            CustomerSalesInvoiceCollectionAllocationRules.BoundaryText);
    }

    /// <summary>单行映射（一次批量装载收款单与发票：固定 2 次数据集访问 + 1 次行装载，无逐行查库）</summary>
    private static async Task<CustomerSalesInvoiceCollectionAllocationDto> MapOneAsync(
        IErpDbContext db, CustomerSalesInvoiceCollectionAllocation row)
        => (await MapManyAsync(db, new List<CustomerSalesInvoiceCollectionAllocation> { row })).Single();

    /// <summary>批量映射：本页的收款单与发票**各一次**查询装载（与行数无关、无 N+1）。</summary>
    private static async Task<List<CustomerSalesInvoiceCollectionAllocationDto>> MapManyAsync(
        IErpDbContext db, IReadOnlyList<CustomerSalesInvoiceCollectionAllocation> rows)
    {
        var receiptIds = rows.Select(r => r.ReceiptId).Distinct().ToList();
        var invoiceIds = rows.Select(r => r.CustomerSalesInvoiceEvidenceId).Distinct().ToList();

        var receipts = await db.FinanceReceipts.AsNoTracking()
            .Where(r => receiptIds.Contains(r.Id)).ToListAsync();
        var invoices = await db.CustomerSalesInvoiceEvidences.AsNoTracking()
            .Where(x => invoiceIds.Contains(x.Id)).ToListAsync();

        var receiptMap = receipts.ToDictionary(r => r.Id);
        var invoiceMap = invoices.ToDictionary(x => x.Id);

        return rows.Select(row => Map(row, receiptMap, invoiceMap)).ToList();
    }
}
