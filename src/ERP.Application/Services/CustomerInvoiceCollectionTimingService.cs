using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ERP.Application.Services;

/// <summary>
/// 客户销项发票收款时效证据派生（ERP-111，只读）。按客户 + 开票日期区间筛选「未删除且已登记」的客户销项发票证据，
/// 用显式 <c>CustomerSalesInvoiceCollectionAllocation.CustomerSalesInvoiceEvidenceId</c> 链接批量装载本页分摊行与收款单，
/// 派生每张发票「开票日期 → 首张 / 末张有效收款日期」的间隔天数与可比较的已分摊 / 剩余证据；
/// 缺链接、已作废、收款单取消、早于开票日期、币种不一致的分摊行仅作异常证据列出、不计入首末收款与可比较金额。
/// <para>只读：不写任何表、不执行迁移 / 生产 SQL / 真实数据库操作 / 部署，不改写发票 / 收款单 / 分摊行 / 客户；</para>
/// <para>查询有界：分页 + 批量取数，固定次数数据集访问（无逐单查库）；应用业务员数据范围。</para>
/// </summary>
public static class CustomerInvoiceCollectionTimingService
{
    /// <summary>派生客户销项发票收款时效报表。</summary>
    public static async Task<CustomerInvoiceCollectionTimingReport> ForQueryAsync(
        IErpDbContext db, CustomerInvoiceCollectionTimingQuery query, HashSet<long>? allowedCustomerIds = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(query);
        query.Normalize();

        // 1) 筛选（仅未删除且已登记的发票）+ 业务员数据范围 + 稳定排序 + 分页（先取本页发票 Id，有界）
        var source = db.CustomerSalesInvoiceEvidences.AsNoTracking()
            .Where(x => !x.IsDeleted && x.Status == CustomerSalesInvoiceEvidenceRules.StatusRecorded);

        if (query.CustomerId.HasValue)
            source = source.Where(x => x.CustomerId == query.CustomerId.Value);
        if (query.InvoiceDateFrom.HasValue)
            source = source.Where(x => x.InvoiceDate >= query.InvoiceDateFrom.Value);
        if (query.InvoiceDateTo.HasValue)
            source = source.Where(x => x.InvoiceDate <= query.InvoiceDateTo.Value);
        if (!string.IsNullOrWhiteSpace(query.Keyword))
        {
            var kw = query.Keyword;
            source = source.Where(x => x.InvoiceNumber.Contains(kw) || x.InvoiceCode.Contains(kw)
                                       || x.CustomerCode.Contains(kw) || x.CustomerName.Contains(kw));
        }

        if (allowedCustomerIds is not null)
        {
            var allowed = allowedCustomerIds.ToList();
            source = source.Where(x => allowed.Contains(x.CustomerId));
        }

        var total = await source.CountAsync();
        var pageIds = await source
            .OrderBy(x => x.CustomerId).ThenBy(x => x.InvoiceDate).ThenBy(x => x.Id)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .Select(x => x.Id)
            .ToListAsync();

        // 2) 加载本页发票
        var pageInvoices = await db.CustomerSalesInvoiceEvidences.AsNoTracking()
            .Where(x => pageIds.Contains(x.Id))
            .ToListAsync();
        var invoicesById = pageInvoices.ToDictionary(x => x.Id);

        // 3) 批量装载本页关联分摊行（一次查询，无逐单查库；含已作废，用于异常标注）
        var ceiling = CustomerInvoiceCollectionTimingRules.AllocationEvidenceCeiling;
        var allocationRows = await db.CustomerSalesInvoiceCollectionAllocations.AsNoTracking()
            .Where(a => !a.IsDeleted && pageIds.Contains(a.CustomerSalesInvoiceEvidenceId))
            .OrderBy(a => a.CustomerSalesInvoiceEvidenceId).ThenBy(a => a.AllocatedAt).ThenBy(a => a.Id)
            .Take(ceiling + 1)
            .ToListAsync();
        var truncated = allocationRows.Count > ceiling;
        if (truncated) allocationRows = allocationRows.Take(ceiling).ToList();

        // 4) 批量装载关联收款单（一次查询，无逐单查库）
        var receiptIds = allocationRows.Select(a => a.ReceiptId).Distinct().ToList();
        var receipts = await db.FinanceReceipts.AsNoTracking()
            .Where(r => receiptIds.Contains(r.Id))
            .ToListAsync();
        var receiptById = receipts.ToDictionary(r => r.Id);

        var allocationsByInvoice = allocationRows
            .GroupBy(a => a.CustomerSalesInvoiceEvidenceId)
            .ToDictionary(g => g.Key, g => g.ToList());

        // 5) 按稳定分页顺序组装行
        var items = new List<CustomerInvoiceCollectionTimingItem>(pageIds.Count);
        foreach (var id in pageIds)
        {
            if (!invoicesById.TryGetValue(id, out var invoice)) continue;

            var rows = new List<CustomerInvoiceCollectionTimingReceiptRow>();
            if (!truncated && allocationsByInvoice.TryGetValue(id, out var allocs))
            {
                foreach (var a in allocs)
                {
                    receiptById.TryGetValue(a.ReceiptId, out var receipt);
                    var missing = receipt is null || receipt.IsDeleted;
                    rows.Add(new CustomerInvoiceCollectionTimingReceiptRow(
                        a.Id,
                        a.Status,
                        a.ReceiptId,
                        a.ReceiptNo ?? string.Empty,
                        missing ? a.ReceiptDate : receipt!.ReceiptDate,
                        a.AllocatedAmount,
                        a.Currency,
                        missing,
                        !missing && receipt!.Status == DocumentStatus.Cancelled,
                        missing ? null : receipt!.Currency.ToString()));
                }
            }

            var derivation = truncated
                ? new CustomerInvoiceCollectionTimingDerivation(
                    null, null, null, null, null, null,
                    0m, invoice.GrossAmount, 0,
                    0, 0, 0, 0, 0,
                    true,
                    new List<string> { CustomerInvoiceCollectionTimingRules.AnomalyAllocationOverCeiling },
                    CustomerInvoiceCollectionTimingRules.AnomalyAllocationOverCeiling)
                : CustomerInvoiceCollectionTimingRules.Derive(
                    invoice.InvoiceDate, invoice.Currency, invoice.GrossAmount, rows);

            var currency = CurrencyAmountRules.NormalizeCurrency(invoice.Currency);

            items.Add(new CustomerInvoiceCollectionTimingItem
            {
                InvoiceId = invoice.Id,
                InvoiceType = invoice.InvoiceType,
                InvoiceCode = invoice.InvoiceCode,
                InvoiceNumber = invoice.InvoiceNumber,
                IdentityText = CustomerSalesInvoiceEvidenceRules.IdentityText(
                    invoice.InvoiceType, invoice.InvoiceCode, invoice.InvoiceNumber),
                InvoiceDate = invoice.InvoiceDate,
                CustomerId = invoice.CustomerId,
                CustomerCode = invoice.CustomerCode,
                CustomerName = invoice.CustomerName,
                Currency = currency,
                GrossAmount = invoice.GrossAmount,
                GrossAmountText = CustomerSalesInvoiceCollectionAllocationRules.AmountText(invoice.GrossAmount, currency),
                FirstReceiptNo = derivation.FirstReceiptNo,
                FirstReceiptDate = derivation.FirstReceiptDate,
                LastReceiptNo = derivation.LastReceiptNo,
                LastReceiptDate = derivation.LastReceiptDate,
                FirstCollectionDays = derivation.FirstCollectionDays,
                LastCollectionDays = derivation.LastCollectionDays,
                ComparableAllocatedAmount = derivation.ComparableAllocatedAmount,
                ComparableAllocatedText = CustomerSalesInvoiceCollectionAllocationRules.AmountText(
                    derivation.ComparableAllocatedAmount, currency),
                ComparableRemainingAmount = derivation.ComparableRemainingAmount,
                ComparableRemainingText = CustomerSalesInvoiceCollectionAllocationRules.AmountText(
                    derivation.ComparableRemainingAmount, currency),
                ComparableAllocationCount = derivation.ComparableAllocationCount,
                MissingCount = derivation.MissingCount,
                VoidedCount = derivation.VoidedCount,
                CancelledCount = derivation.CancelledCount,
                PreInvoiceCount = derivation.PreInvoiceCount,
                CurrencyConflictCount = derivation.CurrencyConflictCount,
                IsAnomalous = derivation.IsAnomalous,
                Anomalies = derivation.Anomalies,
                Note = derivation.Note,
            });
        }

        var counts = new CustomerInvoiceCollectionTimingCounts
        {
            Total = items.Count,
            Available = items.Count(i => i.FirstReceiptDate.HasValue),
            Unavailable = items.Count(i => !i.FirstReceiptDate.HasValue),
            Anomalous = items.Count(i => i.IsAnomalous),
        };

        return new CustomerInvoiceCollectionTimingReport
        {
            CustomerId = query.CustomerId,
            InvoiceDateFrom = query.InvoiceDateFrom,
            InvoiceDateTo = query.InvoiceDateTo,
            Total = total,
            Page = query.Page,
            PageSize = query.PageSize,
            TotalPages = query.PageSize <= 0 ? 0 : (int)Math.Ceiling(total / (double)query.PageSize),
            Rule = CustomerInvoiceCollectionTimingRules.RuleText,
            ScopeNote = CustomerInvoiceCollectionTimingRules.ScopeNoteText,
            Boundary = CustomerInvoiceCollectionTimingRules.BoundaryText,
            Counts = counts,
            Items = items,
        };
    }
}

