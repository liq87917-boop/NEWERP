using ERP.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace ERP.Infrastructure.Data;

/// <summary>
/// 数据库上下文：业务单据实体映射
/// </summary>
public partial class ErpDbContext
{
    // ============ 询价管理 ============
    public DbSet<Inquiry> Inquiries => Set<Inquiry>();
    public DbSet<InquiryDetail> InquiryDetails => Set<InquiryDetail>();

    // ============ 订单管理 ============
    public DbSet<SalesOrder> SalesOrders => Set<SalesOrder>();
    public DbSet<SalesOrderDetail> SalesOrderDetails => Set<SalesOrderDetail>();
    public DbSet<PurchaseOrder> PurchaseOrders => Set<PurchaseOrder>();
    public DbSet<PurchaseOrderDetail> PurchaseOrderDetails => Set<PurchaseOrderDetail>();

    // ============ 物流管理 ============
    public DbSet<StockIn> StockIns => Set<StockIn>();
    public DbSet<StockInDetail> StockInDetails => Set<StockInDetail>();
    public DbSet<StockOut> StockOuts => Set<StockOut>();
    public DbSet<StockOutDetail> StockOutDetails => Set<StockOutDetail>();
    public DbSet<Stock> Stocks => Set<Stock>();

    // ============ 库存单据与库存流水（ERP-009 新增） ============
    public DbSet<StockAdjustment> StockAdjustments => Set<StockAdjustment>();
    public DbSet<StockAdjustmentDetail> StockAdjustmentDetails => Set<StockAdjustmentDetail>();
    public DbSet<StockTransfer> StockTransfers => Set<StockTransfer>();
    public DbSet<StockTransferDetail> StockTransferDetails => Set<StockTransferDetail>();
    public DbSet<SalesReturn> SalesReturns => Set<SalesReturn>();
    public DbSet<SalesReturnDetail> SalesReturnDetails => Set<SalesReturnDetail>();
    public DbSet<PurchaseReturn> PurchaseReturns => Set<PurchaseReturn>();
    public DbSet<PurchaseReturnDetail> PurchaseReturnDetails => Set<PurchaseReturnDetail>();
    public DbSet<StockMovement> StockMovements => Set<StockMovement>();

    // ============ 装柜管理 ============
    public DbSet<ContainerReceivingPlan> ContainerReceivingPlans => Set<ContainerReceivingPlan>();
    public DbSet<ContainerBooking> ContainerBookings => Set<ContainerBooking>();
    public DbSet<ContainerPreLoading> ContainerPreLoadings => Set<ContainerPreLoading>();
    public DbSet<ContainerPreLoadingDetail> ContainerPreLoadingDetails => Set<ContainerPreLoadingDetail>();
    public DbSet<ContainerLoadingList> ContainerLoadingLists => Set<ContainerLoadingList>();
    public DbSet<ContainerLoadingDetail> ContainerLoadingDetails => Set<ContainerLoadingDetail>();
    /// <summary>装柜清单多客户参与方（ERP-041：一柜多客户的客户归属清单子表）</summary>
    public DbSet<ContainerLoadingListParticipant> ContainerLoadingListParticipants => Set<ContainerLoadingListParticipant>();

    // ============ 账务管理 ============
    public DbSet<FinanceDepositApply> FinanceDepositApplies => Set<FinanceDepositApply>();
    public DbSet<FinancePaymentApply> FinancePaymentApplies => Set<FinancePaymentApply>();
    public DbSet<FinancePayment> FinancePayments => Set<FinancePayment>();
    public DbSet<FinanceContainerSettlement> FinanceContainerSettlements => Set<FinanceContainerSettlement>();
    public DbSet<FinanceBulkSettlement> FinanceBulkSettlements => Set<FinanceBulkSettlement>();
    public DbSet<FinanceReceipt> FinanceReceipts => Set<FinanceReceipt>();
    public DbSet<FinanceComplaint> FinanceComplaints => Set<FinanceComplaint>();

    // ============ 装柜费用分摊批次与来源留痕（ERP-042） ============
    /// <summary>装柜费用分摊批次（既有费用单之上的批次留痕；有效批次唯一性由过滤唯一索引兜底）</summary>
    public DbSet<FinanceExpenseAllocationBatch> FinanceExpenseAllocationBatches => Set<FinanceExpenseAllocationBatch>();

    /// <summary>装柜费用分摊行（批次 → 参与方；含来源费用 / 装柜清单 / 客户快照与生成费用单引用）</summary>
    public DbSet<FinanceExpenseAllocationLine> FinanceExpenseAllocationLines => Set<FinanceExpenseAllocationLine>();

    // ============ 供应商采购发票登记（ERP-043） ============
    /// <summary>供应商采购发票（普票 / 专票证据台账；有效身份唯一性由过滤唯一索引兜底）</summary>
    public DbSet<PurchaseInvoice> PurchaseInvoices => Set<PurchaseInvoice>();

    /// <summary>供应商采购发票 → 采购订单 关联（分摊）行（含订单快照与关联金额）</summary>
    public DbSet<PurchaseInvoiceAllocation> PurchaseInvoiceAllocations => Set<PurchaseInvoiceAllocation>();

    // ============ 业务单据附件引用登记（ERP-045） ============
    /// <summary>业务单据附件引用（仅元数据的附件引用册；对父单据只保存号码 / 类型快照，不建外键）</summary>
    public DbSet<DocumentAttachmentReference> DocumentAttachmentReferences => Set<DocumentAttachmentReference>();

    // ============ 销售订单变更申请登记（ERP-047） ============
    /// <summary>销售订单变更申请（来源订单快照 + 拟议值 + 提交 / 取消留痕；不审核、不套用、不改写来源订单）</summary>
    public DbSet<SalesOrderChangeRequest> SalesOrderChangeRequests => Set<SalesOrderChangeRequest>();

    /// <summary>销售订单变更申请明细（拟议明细行；作为来源明细快照与拟议值的对照来源）</summary>
    public DbSet<SalesOrderChangeRequestDetail> SalesOrderChangeRequestDetails => Set<SalesOrderChangeRequestDetail>();

    // ============ 供应商付款引用登记（ERP-049：付款单 → 采购订单 的引用证据行） ============
    /// <summary>付款引用行（付款单 / 供应商 / 采购订单快照 + 引用金额 + 作废留痕；有效行唯一性由过滤唯一索引兜底）</summary>
    public DbSet<SupplierPaymentAllocation> SupplierPaymentAllocations => Set<SupplierPaymentAllocation>();
}
