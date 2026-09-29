using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ERP.Application.Services;

/// <summary>
/// 供应商首收交期派生（ERP-109，只读）。按供应商 + 订单日期区间筛选「未删除且已审核」的采购订单，
/// 用显式 <c>StockIn.PurchaseOrderId</c> 链接批量装载本页入库单，派生每张订单「订单日期 → 首张有效已审核入库」的
/// 首收日期与间隔天数；未审核 / 已删除 / 供应商不一致 / 早于订单日期的入库仅作异常证据列出，不计入首收，
/// 缺失或不一致日期保持未知，绝不当作 0 或正常。
/// <para>只读：不写任何表、不执行迁移 / 生产 SQL / 真实数据库操作 / 部署，不改写采购订单 / 入库单 / 库存与库存成本。</para>
/// <para>查询有界：分页 + 批量取数，固定次数数据集访问（无逐单查库）。</para>
/// </summary>
public static class SupplierFirstReceiptLeadTimeService
{
    /// <summary>派生供应商首收交期报表。</summary>
    public static async Task<SupplierFirstReceiptLeadTimeReport> ForQueryAsync(
        IErpDbContext db, SupplierFirstReceiptLeadTimeQuery query)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(query);
        query.Normalize();

        // 1) 筛选（仅未删除且已审核的采购订单）+ 稳定排序 + 分页（先取本页订单 Id，有界）
        var source = db.PurchaseOrders.AsNoTracking()
            .Where(o => !o.IsDeleted && o.Status == DocumentStatus.Approved);
        if (query.SupplierId.HasValue)
            source = source.Where(o => o.SupplierId == query.SupplierId.Value);
        if (query.OrderDateFrom.HasValue)
            source = source.Where(o => o.OrderDate >= query.OrderDateFrom.Value);
        if (query.OrderDateTo.HasValue)
            source = source.Where(o => o.OrderDate <= query.OrderDateTo.Value);
        if (!string.IsNullOrWhiteSpace(query.Keyword))
        {
            var kw = query.Keyword;
            source = source.Where(o => o.OrderNo.Contains(kw) || o.ContractNo.Contains(kw)
                                       || o.OwningSalesOrderNo.Contains(kw));
        }

        var total = await source.CountAsync();
        var pageIds = await source
            .OrderBy(o => o.SupplierId).ThenBy(o => o.OrderDate).ThenBy(o => o.Id)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .Select(o => o.Id)
            .ToListAsync();

        // 2) 加载本页订单
        var pageOrders = await db.PurchaseOrders.AsNoTracking()
            .Where(o => pageIds.Contains(o.Id))
            .ToListAsync();
        var ordersById = pageOrders.ToDictionary(o => o.Id);

        // 3) 供应商名称（有界批量，一次查询）
        var supplierIds = pageOrders.Select(o => o.SupplierId).Distinct().ToList();
        var suppliers = await db.BaseSuppliers.AsNoTracking()
            .Where(s => supplierIds.Contains(s.Id))
            .Select(s => new { s.Id, s.SupplierName })
            .ToListAsync();
        var supplierNameById = suppliers.ToDictionary(s => s.Id, s => s.SupplierName);

        // 4) 批量装载本页关联入库单（一次查询，无逐单查库；含已删除 / 未审核 / 供应商不一致，用于异常标注）
        var ceiling = SupplierFirstReceiptLeadTimeRules.ReceiptEvidenceCeiling;
        var receiptRows = await db.StockIns.AsNoTracking()
            .Where(s => s.PurchaseOrderId != null && pageIds.Contains(s.PurchaseOrderId.Value))
            .OrderBy(s => s.StockInDate).ThenBy(s => s.Id)
            .Select(s => new SupplierFirstReceiptReceiptRow(
                s.PurchaseOrderId!.Value, s.StockInNo, s.StockInDate, s.IsDeleted, s.Status, s.SupplierId))
            .Take(ceiling + 1)
            .ToListAsync();
        var truncated = receiptRows.Count > ceiling;
        if (truncated) receiptRows = receiptRows.Take(ceiling).ToList();
        var receiptsByOrder = receiptRows
            .GroupBy(r => r.PurchaseOrderId)
            .ToDictionary(g => g.Key, g => g.ToList());

        // 5) 按稳定分页顺序组装行
        var items = new List<SupplierFirstReceiptLeadTimeItem>(pageIds.Count);
        foreach (var id in pageIds)
        {
            if (!ordersById.TryGetValue(id, out var order)) continue;

            var receipts = receiptsByOrder.GetValueOrDefault(id, new List<SupplierFirstReceiptReceiptRow>());
            var derivation = truncated
                ? new SupplierFirstReceiptLeadTimeDerivation(
                    null, null, null,
                    SupplierFirstReceiptLeadTimeRules.LeadTimeUnavailable, true,
                    new List<string> { SupplierFirstReceiptLeadTimeRules.AnomalyReceiptOverCeiling },
                    SupplierFirstReceiptLeadTimeRules.AnomalyReceiptOverCeiling)
                : SupplierFirstReceiptLeadTimeRules.Derive(order.OrderDate, order.SupplierId, receipts);

            items.Add(new SupplierFirstReceiptLeadTimeItem
            {
                OrderId = order.Id,
                OrderNo = order.OrderNo,
                OrderDate = order.OrderDate,
                SupplierId = order.SupplierId,
                SupplierName = supplierNameById.GetValueOrDefault(order.SupplierId, string.Empty),
                Currency = order.Currency.ToString(),
                Status = order.Status.ToString(),
                FirstReceiptNo = derivation.FirstReceiptNo,
                FirstReceiptDate = derivation.FirstReceiptDate,
                ElapsedDays = derivation.ElapsedDays,
                LeadTimeStatus = derivation.LeadTimeStatus,
                IsAnomalous = derivation.IsAnomalous,
                Anomalies = derivation.Anomalies,
                Note = derivation.Note,
            });
        }

        var counts = new SupplierFirstReceiptLeadTimeCounts
        {
            Total = items.Count,
            Received = items.Count(i => i.LeadTimeStatus == SupplierFirstReceiptLeadTimeRules.LeadTimeReceived),
            NegativeInterval = items.Count(i => i.LeadTimeStatus == SupplierFirstReceiptLeadTimeRules.LeadTimeNegativeInterval),
            Unavailable = items.Count(i => i.LeadTimeStatus == SupplierFirstReceiptLeadTimeRules.LeadTimeUnavailable),
            Anomalous = items.Count(i => i.IsAnomalous),
        };

        return new SupplierFirstReceiptLeadTimeReport
        {
            SupplierId = query.SupplierId,
            OrderDateFrom = query.OrderDateFrom,
            OrderDateTo = query.OrderDateTo,
            Total = total,
            Page = query.Page,
            PageSize = query.PageSize,
            TotalPages = query.PageSize <= 0 ? 0 : (int)Math.Ceiling(total / (double)query.PageSize),
            Rule = SupplierFirstReceiptLeadTimeRules.RuleText,
            ScopeNote = SupplierFirstReceiptLeadTimeRules.ScopeNoteText,
            Boundary = SupplierFirstReceiptLeadTimeRules.BoundaryText,
            Counts = counts,
            Items = items,
        };
    }
}

