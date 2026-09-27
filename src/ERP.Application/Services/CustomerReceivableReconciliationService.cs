                || i.CustomerName.Contains(keyword)
                || i.Remark.Contains(keyword));
        }

        return source;
    }

    // ==================== 1. 本页派生证据（固定次数数据集访问） ====================

    /// <summary>本页一次性批量装载派生证据（本页发票非空时固定 6 次数据集访问，与发票张数 / 行数无关，无逐行查库）</summary>
    private static async Task<PageContext> LoadPageContextAsync(
        IErpDbContext db, IReadOnlyList<CustomerSalesInvoiceEvidence> invoices)
    {
        if (invoices.Count == 0) return new PageContext();

        var invoiceIds = invoices.Select(i => i.Id).ToList();
        var customerIds = invoices.Select(i => i.CustomerId).Distinct().ToList();
        var customerIdSet = customerIds.ToHashSet();

        var invoiceSet = db.CustomerSalesInvoiceEvidences;
        var allocationSet = db.CustomerSalesInvoiceCollectionAllocations;
        var receiptSet = db.FinanceReceipts;
        var customerSet = db.BaseCustomers;

        // ① 分摊行按状态聚合（含已作废历史；绝不逐行回读）
        var statusStats = (await allocationSet.AsNoTracking()
                .Where(a => !a.IsDeleted && invoiceIds.Contains(a.CustomerSalesInvoiceEvidenceId))
                .GroupBy(a => new { a.CustomerSalesInvoiceEvidenceId, a.Status })
                .Select(g => new
                {
                    g.Key.CustomerSalesInvoiceEvidenceId,
                    g.Key.Status,
                    Count = g.Count(),
                    Amount = g.Sum(a => a.AllocatedAmount),
                })
                .ToListAsync())
            .Select(x => new AllocationStatusStat(x.CustomerSalesInvoiceEvidenceId, x.Status, x.Count, x.Amount))
            .ToList();

        // ② 有效分摊聚合（未作废 + 发票仍已登记 + 客户 / 币种自洽 + 收款单仍可读未取消）
        var effectiveStats = (await allocationSet.AsNoTracking()
                .Where(a => !a.IsDeleted
                    && a.Status == CustomerSalesInvoiceCollectionAllocationRules.StatusActive
                    && invoiceIds.Contains(a.CustomerSalesInvoiceEvidenceId)
                    && invoiceSet.Any(i => i.Id == a.CustomerSalesInvoiceEvidenceId && !i.IsDeleted
                        && i.Status == CustomerSalesInvoiceEvidenceRules.StatusRecorded
                        && i.Currency == a.Currency && i.CustomerId == a.CustomerId)
                    && receiptSet.Any(r => r.Id == a.ReceiptId && !r.IsDeleted
                        && r.Status != DocumentStatus.Cancelled))
                .GroupBy(a => a.CustomerSalesInvoiceEvidenceId)
                .Select(g => new
                {
                    InvoiceId = g.Key,
                    Count = g.Count(),
                    Amount = g.Sum(a => a.AllocatedAmount),
                })
                .ToListAsync())
            .Select(x => new AllocationAggregateStat(x.InvoiceId, x.Count, x.Amount))
            .ToList();

        // ③ 有效分摊的（发票, 收款单）组合 → 有效分摊涉及的收款单数
        var effectivePairs = (await allocationSet.AsNoTracking()
                .Where(a => !a.IsDeleted
                    && a.Status == CustomerSalesInvoiceCollectionAllocationRules.StatusActive
                    && invoiceIds.Contains(a.CustomerSalesInvoiceEvidenceId)
                    && invoiceSet.Any(i => i.Id == a.CustomerSalesInvoiceEvidenceId && !i.IsDeleted
                        && i.Status == CustomerSalesInvoiceEvidenceRules.StatusRecorded
                        && i.Currency == a.Currency && i.CustomerId == a.CustomerId)
                    && receiptSet.Any(r => r.Id == a.ReceiptId && !r.IsDeleted
                        && r.Status != DocumentStatus.Cancelled))
                .GroupBy(a => new { a.CustomerSalesInvoiceEvidenceId, a.ReceiptId })
                .Select(g => new { g.Key.CustomerSalesInvoiceEvidenceId, g.Key.ReceiptId })
                .ToListAsync())
            .Select(x => new AllocationPairStat(x.CustomerSalesInvoiceEvidenceId, x.ReceiptId))
            .ToList();

        // ④ 本页客户（一次批量装载）
        var customers = (await customerSet.AsNoTracking()
                .Where(c => customerIdSet.Contains(c.Id)).ToListAsync())
            .ToDictionary(c => c.Id);

        return new PageContext
        {
            Facts = BuildFacts(invoices, statusStats, effectiveStats, effectivePairs),
            Customers = customers,
        };
    }

    /// <summary>把聚合结果按发票折叠为行事实（纯计算，不访问数据集）</summary>
    private static Dictionary<long, InvoiceFacts> BuildFacts(
        IReadOnlyList<CustomerSalesInvoiceEvidence> invoices,
        IReadOnlyList<AllocationStatusStat> statusStats,
        IReadOnlyList<AllocationAggregateStat> effectiveStats,
        IReadOnlyList<AllocationPairStat> effectivePairs)
    {
        var facts = invoices.ToDictionary(i => i.Id, _ => new InvoiceFacts());

        foreach (var s in statusStats)
        {
            if (!facts.TryGetValue(s.InvoiceId, out var f)) continue;
            f.TotalRowCount += s.Count;
            if (s.Status == CustomerSalesInvoiceCollectionAllocationRules.StatusActive)
            {
                f.ActiveRowCount += s.Count;
                f.ActiveAmount += s.Amount;
            }
            else if (s.Status == CustomerSalesInvoiceCollectionAllocationRules.StatusVoided)
            {
                f.VoidedRowCount += s.Count;
            }
        }

        foreach (var e in effectiveStats)
        {
            if (facts.TryGetValue(e.InvoiceId, out var f))
            {
                f.EffectiveCount = e.Count;
                f.EffectiveAmount = e.Amount;
            }
        }

        foreach (var p in effectivePairs)
        {
            if (facts.TryGetValue(p.InvoiceId, out var f)) f.EffectiveReceiptCount++;
        }

        return facts;
    }
}