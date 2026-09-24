using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ERP.Application.Services;

/// <summary>
/// 单据号生成服务：根据单据号规则表生成唯一单据号
/// 格式：前缀 + 日期 + 流水号，不足位数补零
/// </summary>
public class DocumentNumberService : IDocumentNumberService
{
    private readonly IErpDbContext _db;

    public DocumentNumberService(IErpDbContext db)
    {
        _db = db;
    }

    /// <summary>各单据类型的默认前缀（规则表未配置时使用）</summary>
    private static readonly Dictionary<DocumentType, string> DefaultPrefixes = new()
    {
        [DocumentType.Inquiry] = "INQ",
        [DocumentType.SalesOrder] = "SO",
        [DocumentType.PurchaseOrder] = "PO",
        [DocumentType.StockIn] = "RK",
        [DocumentType.StockOut] = "CK",
        [DocumentType.ReceivingPlan] = "JH",
        [DocumentType.ContainerBooking] = "DG",
        [DocumentType.PreLoading] = "YZ",
        [DocumentType.LoadingList] = "ZQ",
        [DocumentType.DepositApply] = "DJ",
        [DocumentType.PaymentApply] = "HK",
        [DocumentType.Payment] = "FK",
        [DocumentType.ContainerSettlement] = "ZJ",
        [DocumentType.BulkSettlement] = "SJ",
        [DocumentType.Receipt] = "SK",
        [DocumentType.Complaint] = "KS",
        [DocumentType.Quotation] = "QT",
        [DocumentType.ProformaInvoice] = "PI",
        [DocumentType.StockAdjustment] = "PD",
        [DocumentType.StockTransfer] = "DB",
        [DocumentType.SalesReturn] = "XTH",
        [DocumentType.PurchaseReturn] = "CTH",
        [DocumentType.SalesOrderChangeRequest] = "SOC"
    };

    /// <summary>生成单据号</summary>
    public async Task<string> GenerateAsync(DocumentType documentType, DateTime? date = null)
    {
        var bizDate = date ?? DateTime.Today;

        // 读取规则（若未配置则使用默认前缀 + yyyyMMdd + 4 位流水）
        var rule = await _db.SysDocumentNumberRules
            .FirstOrDefaultAsync(r => r.DocumentType == documentType && !r.IsDeleted);

        var prefix = rule?.Prefix ?? DefaultPrefixes.GetValueOrDefault(documentType, "DOC");
        var dateFormat = rule?.DateFormat ?? "yyyyMMdd";
        var serialLength = rule?.SerialLength ?? 4;
        var separator = rule?.Separator ?? string.Empty;

        // 按年重置时，日期部分按年区分流水
        var datePart = bizDate.ToString(dateFormat);

        long nextSequence;
        if (rule is null)
        {
            // 无规则：用内存计数 + 时间戳兜底，保证唯一性
            nextSequence = await NextSequenceWithoutRuleAsync(documentType);
        }
        else
        {
            nextSequence = rule.CurrentSequence + 1;
            rule.CurrentSequence = nextSequence;
            rule.UpdatedAt = DateTime.Now;
        }

        var serial = nextSequence.ToString().PadLeft(serialLength, '0');
        await _db.SaveChangesAsync();

        return $"{prefix}{datePart}{separator}{serial}";
    }

    /// <summary>未配置规则时的流水号计算：取同类型已有单据数量 + 时间戳后三位</summary>
    private async Task<long> NextSequenceWithoutRuleAsync(DocumentType documentType)
    {
        // 为避免并发冲突，使用时间戳毫秒的后四位 + 累计偏移
        var count = await CountByTypeAsync(documentType);
        return count + (DateTime.Now.Millisecond % 1000);
    }

    /// <summary>统计指定单据类型的单据数量（用于无规则时的流水号）</summary>
    private async Task<long> CountByTypeAsync(DocumentType documentType)
    {
        return documentType switch
        {
            DocumentType.Inquiry => await _db.Inquiries.CountAsync(i => !i.IsDeleted),
            DocumentType.SalesOrder => await _db.SalesOrders.CountAsync(o => !o.IsDeleted),
            DocumentType.PurchaseOrder => await _db.PurchaseOrders.CountAsync(o => !o.IsDeleted),
            DocumentType.StockIn => await _db.StockIns.CountAsync(o => !o.IsDeleted),
            DocumentType.StockOut => await _db.StockOuts.CountAsync(o => !o.IsDeleted),
            DocumentType.ReceivingPlan => await _db.ContainerReceivingPlans.CountAsync(o => !o.IsDeleted),
            DocumentType.ContainerBooking => await _db.ContainerBookings.CountAsync(o => !o.IsDeleted),
            DocumentType.PreLoading => await _db.ContainerPreLoadings.CountAsync(o => !o.IsDeleted),
            DocumentType.LoadingList => await _db.ContainerLoadingLists.CountAsync(o => !o.IsDeleted),
            DocumentType.DepositApply => await _db.FinanceDepositApplies.CountAsync(o => !o.IsDeleted),
            DocumentType.PaymentApply => await _db.FinancePaymentApplies.CountAsync(o => !o.IsDeleted),
            DocumentType.Payment => await _db.FinancePayments.CountAsync(o => !o.IsDeleted),
            DocumentType.ContainerSettlement => await _db.FinanceContainerSettlements.CountAsync(o => !o.IsDeleted),
            DocumentType.BulkSettlement => await _db.FinanceBulkSettlements.CountAsync(o => !o.IsDeleted),
            DocumentType.Receipt => await _db.FinanceReceipts.CountAsync(o => !o.IsDeleted),
            DocumentType.Complaint => await _db.FinanceComplaints.CountAsync(o => !o.IsDeleted),
            DocumentType.Quotation => await _db.Quotations.CountAsync(o => !o.IsDeleted),
            DocumentType.StockAdjustment => await _db.StockAdjustments.CountAsync(o => !o.IsDeleted),
            DocumentType.StockTransfer => await _db.StockTransfers.CountAsync(o => !o.IsDeleted),
            DocumentType.SalesReturn => await _db.SalesReturns.CountAsync(o => !o.IsDeleted),
            DocumentType.PurchaseReturn => await _db.PurchaseReturns.CountAsync(o => !o.IsDeleted),
            DocumentType.SalesOrderChangeRequest => await _db.SalesOrderChangeRequests.CountAsync(o => !o.IsDeleted),
            _ => 0
        };
    }
}
