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

    // ============ 账务管理 ============
    public DbSet<FinanceDepositApply> FinanceDepositApplies => Set<FinanceDepositApply>();
    public DbSet<FinancePaymentApply> FinancePaymentApplies => Set<FinancePaymentApply>();
    public DbSet<FinancePayment> FinancePayments => Set<FinancePayment>();
    public DbSet<FinanceContainerSettlement> FinanceContainerSettlements => Set<FinanceContainerSettlement>();
    public DbSet<FinanceBulkSettlement> FinanceBulkSettlements => Set<FinanceBulkSettlement>();
    public DbSet<FinanceReceipt> FinanceReceipts => Set<FinanceReceipt>();
    public DbSet<FinanceComplaint> FinanceComplaints => Set<FinanceComplaint>();
}
