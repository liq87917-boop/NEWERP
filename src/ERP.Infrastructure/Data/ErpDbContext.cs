using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace ERP.Infrastructure.Data;

/// <summary>
/// ERP 数据库上下文：实现数据访问抽象接口，映射全部业务实体
/// </summary>
public partial class ErpDbContext : DbContext, IErpDbContext
{
    public ErpDbContext(DbContextOptions<ErpDbContext> options) : base(options)
    {
    }

    // ============ 系统设置 ============
    public DbSet<SysUser> SysUsers => Set<SysUser>();
    public DbSet<SysRole> SysRoles => Set<SysRole>();
    public DbSet<SysUserRole> SysUserRoles => Set<SysUserRole>();
    public DbSet<SysMenu> SysMenus => Set<SysMenu>();
    public DbSet<SysRoleMenu> SysRoleMenus => Set<SysRoleMenu>();
    public DbSet<SysParameter> SysParameters => Set<SysParameter>();
    public DbSet<SysUserParameter> SysUserParameters => Set<SysUserParameter>();
    public DbSet<SysDocumentNumberRule> SysDocumentNumberRules => Set<SysDocumentNumberRule>();
    public DbSet<SysClientLimit> SysClientLimits => Set<SysClientLimit>();
    public DbSet<SysOperationLog> SysOperationLogs => Set<SysOperationLog>();

    /// <summary>钉钉通知发送记录</summary>
    public DbSet<SysDingTalkLog> SysDingTalkLogs => Set<SysDingTalkLog>();

    /// <summary>打印模板（打印设计）</summary>
    public DbSet<SysPrintTemplate> SysPrintTemplates => Set<SysPrintTemplate>();

    // ============ 基础资料 ============
    public DbSet<BaseCustomer> BaseCustomers => Set<BaseCustomer>();
    public DbSet<BaseSupplier> BaseSuppliers => Set<BaseSupplier>();
    public DbSet<BaseEmployee> BaseEmployees => Set<BaseEmployee>();
    public DbSet<BaseExpenseAccount> BaseExpenseAccounts => Set<BaseExpenseAccount>();
    public DbSet<BaseWarehouse> BaseWarehouses => Set<BaseWarehouse>();
    public DbSet<BaseProduct> BaseProducts => Set<BaseProduct>();
    public DbSet<BaseProductVariant> BaseProductVariants => Set<BaseProductVariant>();
    public DbSet<BaseOtherInfo> BaseOtherInfos => Set<BaseOtherInfo>();

    /// <summary>出口退税台账（阶段 1 新增）</summary>
    public DbSet<BaseTaxRefund> BaseTaxRefunds => Set<BaseTaxRefund>();

    /// <summary>费用单（阶段 2 新增：出口杂费台账与分摊）</summary>
    public DbSet<FinanceExpense> FinanceExpenses => Set<FinanceExpense>();

    /// <summary>供应商报价比价（阶段 2 新增）</summary>
    public DbSet<PurchaseQuote> PurchaseQuotes => Set<PurchaseQuote>();

    /// <summary>出口单证台账（阶段 2 新增）</summary>
    public DbSet<TradeDocument> TradeDocuments => Set<TradeDocument>();

    /// <summary>客户跟进记录（阶段 2 新增）</summary>
    public DbSet<CustomerFollowUp> CustomerFollowUps => Set<CustomerFollowUp>();

    /// <summary>样品管理（阶段 2 新增）</summary>
    public DbSet<Sample> Samples => Set<Sample>();

    /// <summary>报价单主表 / 明细（阶段 3 新增）</summary>
    public DbSet<Quotation> Quotations => Set<Quotation>();
    public DbSet<QuotationDetail> QuotationDetails => Set<QuotationDetail>();

    /// <summary>形式发票 PI 主表 / 明细（阶段 3 新增：报价单 → PI → 销售订单）</summary>
    public DbSet<ProformaInvoice> ProformaInvoices => Set<ProformaInvoice>();
    public DbSet<ProformaInvoiceDetail> ProformaInvoiceDetails => Set<ProformaInvoiceDetail>();
}
