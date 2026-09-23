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
    DbSet<BaseOtherInfo> BaseOtherInfos { get; }

    /// <summary>出口退税台账（阶段 1 新增）</summary>
    DbSet<BaseTaxRefund> BaseTaxRefunds { get; }

    /// <summary>费用单（阶段 2 新增：出口杂费台账与分摊）</summary>
    DbSet<FinanceExpense> FinanceExpenses { get; }

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

    // ============ 物流管理 ============
    DbSet<StockIn> StockIns { get; }
    DbSet<StockInDetail> StockInDetails { get; }
    DbSet<StockOut> StockOuts { get; }
    DbSet<StockOutDetail> StockOutDetails { get; }
    DbSet<Stock> Stocks { get; }

    // ============ 装柜管理 ============
    DbSet<ContainerReceivingPlan> ContainerReceivingPlans { get; }
    DbSet<ContainerBooking> ContainerBookings { get; }
    DbSet<ContainerPreLoading> ContainerPreLoadings { get; }
    DbSet<ContainerPreLoadingDetail> ContainerPreLoadingDetails { get; }
    DbSet<ContainerLoadingList> ContainerLoadingLists { get; }
    DbSet<ContainerLoadingDetail> ContainerLoadingDetails { get; }

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
