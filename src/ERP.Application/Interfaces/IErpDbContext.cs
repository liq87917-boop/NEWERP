using ERP.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace ERP.Application.Interfaces;

/// <summary>
/// 数据访问抽象接口：将应用层与具体 DbContext 解耦，便于单元测试与仓储隔离
/// </summary>
public interface IErpDbContext
{
    // ============ 系统设置 ============
    DbSet<SysUser> SysUsers { get; }
    DbSet<SysRole> SysRoles { get; }
    DbSet<SysUserRole> SysUserRoles { get; }
    DbSet<SysMenu> SysMenus { get; }
    DbSet<SysRoleMenu> SysRoleMenus { get; }
    DbSet<SysParameter> SysParameters { get; }
    DbSet<SysUserParameter> SysUserParameters { get; }
    DbSet<SysDocumentNumberRule> SysDocumentNumberRules { get; }
    DbSet<SysClientLimit> SysClientLimits { get; }
    DbSet<SysOperationLog> SysOperationLogs { get; }

    /// <summary>钉钉通知发送记录</summary>
    DbSet<SysDingTalkLog> SysDingTalkLogs { get; }

    /// <summary>打印模板（打印设计）</summary>
    DbSet<SysPrintTemplate> SysPrintTemplates { get; }

    // ============ 基础资料 ============
    DbSet<BaseCustomer> BaseCustomers { get; }
    DbSet<BaseSupplier> BaseSuppliers { get; }
    DbSet<BaseEmployee> BaseEmployees { get; }
    DbSet<BaseExpenseAccount> BaseExpenseAccounts { get; }
    DbSet<BaseWarehouse> BaseWarehouses { get; }
    DbSet<BaseProduct> BaseProducts { get; }

    /// <summary>商品规格变体（颜色 / 尺码 SKU 子表，ERP-037；商品身份不变，规格只是可选细分）</summary>
    DbSet<BaseProductVariant> BaseProductVariants { get; }

    /// <summary>
    /// 商品 / SKU 货源关系（ERP-038；多供应商货源指引，不自动选供应商、不定价、不改写采购单据与库存）
    /// </summary>
    DbSet<BaseProductSupplier> BaseProductSuppliers { get; }

    DbSet<BaseOtherInfo> BaseOtherInfos { get; }

    /// <summary>出口退税台账（阶段 1 新增）</summary>
    DbSet<BaseTaxRefund> BaseTaxRefunds { get; }

    /// <summary>费用单（阶段 2 新增：出口杂费台账与分摊）</summary>
    DbSet<FinanceExpense> FinanceExpenses { get; }

    /// <summary>
    /// 装柜费用分摊批次（ERP-042；既有费用单之上的批次 / 来源留痕，不引入第二套账务引擎）
    /// </summary>
    DbSet<FinanceExpenseAllocationBatch> FinanceExpenseAllocationBatches { get; }

    /// <summary>装柜费用分摊行（ERP-042；批次 → 参与方 的逐行留痕与生成费用单引用）</summary>
    DbSet<FinanceExpenseAllocationLine> FinanceExpenseAllocationLines { get; }

    // ============ 询价管理 ============
    DbSet<Inquiry> Inquiries { get; }
    DbSet<InquiryDetail> InquiryDetails { get; }

    // ============ 订单管理 ============
    DbSet<SalesOrder> SalesOrders { get; }
    DbSet<SalesOrderDetail> SalesOrderDetails { get; }
    /// <summary>采购订单</summary>
    DbSet<PurchaseOrder> PurchaseOrders { get; }
    DbSet<PurchaseOrderDetail> PurchaseOrderDetails { get; }

    /// <summary>供应商报价比价（阶段 2 新增）</summary>
    DbSet<PurchaseQuote> PurchaseQuotes { get; }

    /// <summary>出口单证台账（阶段 2 新增）</summary>
    DbSet<TradeDocument> TradeDocuments { get; }

    /// <summary>客户跟进记录（阶段 2 新增）</summary>
    DbSet<CustomerFollowUp> CustomerFollowUps { get; }

    /// <summary>样品管理（阶段 2 新增）</summary>
    DbSet<Sample> Samples { get; }

    /// <summary>报价单（阶段 3 新增：询价单 → 报价单 → PI → 销售订单）</summary>
    DbSet<Quotation> Quotations { get; }
    DbSet<QuotationDetail> QuotationDetails { get; }

    /// <summary>形式发票 PI（阶段 3 新增：报价单 → PI → 销售订单）</summary>
    DbSet<ProformaInvoice> ProformaInvoices { get; }
    DbSet<ProformaInvoiceDetail> ProformaInvoiceDetails { get; }

    // ============ 物流管理 ============
    DbSet<StockIn> StockIns { get; }
    DbSet<StockInDetail> StockInDetails { get; }
    DbSet<StockOut> StockOuts { get; }
    DbSet<StockOutDetail> StockOutDetails { get; }
    DbSet<Stock> Stocks { get; }

    // ============ 库存单据与库存流水（ERP-009 新增） ============
    /// <summary>库存盘点/调整单</summary>
    DbSet<StockAdjustment> StockAdjustments { get; }
    DbSet<StockAdjustmentDetail> StockAdjustmentDetails { get; }

    /// <summary>仓库调拨单</summary>
    DbSet<StockTransfer> StockTransfers { get; }
    DbSet<StockTransferDetail> StockTransferDetails { get; }

    /// <summary>销售退货单</summary>
    DbSet<SalesReturn> SalesReturns { get; }
    DbSet<SalesReturnDetail> SalesReturnDetails { get; }

    /// <summary>采购退货单</summary>
    DbSet<PurchaseReturn> PurchaseReturns { get; }
    DbSet<PurchaseReturnDetail> PurchaseReturnDetails { get; }

    /// <summary>库存流水（可审计移动记录，库存估价成本基准）</summary>
    DbSet<StockMovement> StockMovements { get; }

    // ============ 装柜管理 ============
    DbSet<ContainerReceivingPlan> ContainerReceivingPlans { get; }
    DbSet<ContainerBooking> ContainerBookings { get; }
    DbSet<ContainerPreLoading> ContainerPreLoadings { get; }
    DbSet<ContainerPreLoadingDetail> ContainerPreLoadingDetails { get; }
    DbSet<ContainerLoadingList> ContainerLoadingLists { get; }
    DbSet<ContainerLoadingDetail> ContainerLoadingDetails { get; }

    /// <summary>
    /// 装柜清单多客户参与方（ERP-041；一柜多客户的客户归属清单，不自动分摊费用、不改写装柜 / 单证 / 库存与客户主数据）
    /// </summary>
    DbSet<ContainerLoadingListParticipant> ContainerLoadingListParticipants { get; }

    // ============ 账务管理 ============
    DbSet<FinanceDepositApply> FinanceDepositApplies { get; }
    DbSet<FinancePaymentApply> FinancePaymentApplies { get; }
    DbSet<FinancePayment> FinancePayments { get; }
    DbSet<FinanceContainerSettlement> FinanceContainerSettlements { get; }
    DbSet<FinanceBulkSettlement> FinanceBulkSettlements { get; }
    DbSet<FinanceReceipt> FinanceReceipts { get; }
    DbSet<FinanceComplaint> FinanceComplaints { get; }

    /// <summary>保存变更（返回受影响行数）</summary>
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
