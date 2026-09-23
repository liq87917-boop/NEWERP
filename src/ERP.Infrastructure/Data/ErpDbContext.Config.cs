using ERP.Domain.Common;
using ERP.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace ERP.Infrastructure.Data;

/// <summary>
/// 数据库上下文：模型配置、审计字段、保存变更
/// </summary>
public partial class ErpDbContext
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // 数据库表实际存储在 db_owner schema（而非默认 dbo），需显式指定
        modelBuilder.HasDefaultSchema("db_owner");

        // ============ 唯一索引（系统设置） ============
        modelBuilder.Entity<SysUser>().HasIndex(u => u.UserName).IsUnique();
        modelBuilder.Entity<SysRole>().HasIndex(r => r.RoleCode).IsUnique();
        modelBuilder.Entity<SysMenu>().HasIndex(m => m.MenuCode).IsUnique();
        modelBuilder.Entity<SysParameter>().HasIndex(p => p.ParamKey).IsUnique();

        // ============ 唯一索引（打印模板：单据类型 + 模板名称） ============
        modelBuilder.Entity<SysPrintTemplate>().HasIndex(t => new { t.BillType, t.TemplateName }).IsUnique();

        // ============ 唯一索引（基础资料） ============
        modelBuilder.Entity<BaseCustomer>().HasIndex(c => c.CustomerCode).IsUnique();
        modelBuilder.Entity<BaseSupplier>().HasIndex(s => s.SupplierCode).IsUnique();
        modelBuilder.Entity<BaseEmployee>().HasIndex(e => e.EmployeeCode).IsUnique();
        modelBuilder.Entity<BaseExpenseAccount>().HasIndex(a => a.AccountCode).IsUnique();
        modelBuilder.Entity<BaseWarehouse>().HasIndex(w => w.WarehouseCode).IsUnique();
        modelBuilder.Entity<BaseProduct>().HasIndex(p => p.ProductCode).IsUnique();
        modelBuilder.Entity<BaseOtherInfo>().HasIndex(o => new { o.InfoType, o.InfoCode }).IsUnique();

        // ============ 单据号唯一索引 ============
        modelBuilder.Entity<Inquiry>().HasIndex(x => x.InquiryNo).IsUnique();
        modelBuilder.Entity<SalesOrder>().HasIndex(x => x.OrderNo).IsUnique();
        modelBuilder.Entity<PurchaseOrder>().HasIndex(x => x.OrderNo).IsUnique();
        modelBuilder.Entity<StockIn>().HasIndex(x => x.StockInNo).IsUnique();
        modelBuilder.Entity<StockOut>().HasIndex(x => x.StockOutNo).IsUnique();
        modelBuilder.Entity<ContainerReceivingPlan>().HasIndex(x => x.PlanNo).IsUnique();
        modelBuilder.Entity<ContainerBooking>().HasIndex(x => x.BookingNo).IsUnique();
        modelBuilder.Entity<ContainerPreLoading>().HasIndex(x => x.PreLoadingNo).IsUnique();
        modelBuilder.Entity<ContainerLoadingList>().HasIndex(x => x.LoadingListNo).IsUnique();
        modelBuilder.Entity<FinanceDepositApply>().HasIndex(x => x.ApplyNo).IsUnique();
        modelBuilder.Entity<FinancePaymentApply>().HasIndex(x => x.ApplyNo).IsUnique();
        modelBuilder.Entity<FinancePayment>().HasIndex(x => x.PaymentNo).IsUnique();
        modelBuilder.Entity<FinanceContainerSettlement>().HasIndex(x => x.SettlementNo).IsUnique();
        modelBuilder.Entity<FinanceBulkSettlement>().HasIndex(x => x.SettlementNo).IsUnique();
        modelBuilder.Entity<FinanceReceipt>().HasIndex(x => x.ReceiptNo).IsUnique();
        modelBuilder.Entity<FinanceComplaint>().HasIndex(x => x.ComplaintNo).IsUnique();

        // ============ 单据号唯一索引（阶段 3：形式发票 PI 使用 EF 主子表） ============
        modelBuilder.Entity<ProformaInvoice>().HasIndex(x => x.PiNo).IsUnique();

        // ============ 单据号唯一索引（ERP-009：库存单据使用 EF 主子表） ============
        modelBuilder.Entity<StockAdjustment>().HasIndex(x => x.AdjustmentNo).IsUnique();
        modelBuilder.Entity<StockTransfer>().HasIndex(x => x.TransferNo).IsUnique();
        modelBuilder.Entity<SalesReturn>().HasIndex(x => x.ReturnNo).IsUnique();
        modelBuilder.Entity<PurchaseReturn>().HasIndex(x => x.ReturnNo).IsUnique();

        // ============ 库存唯一约束（仓库 + 商品） ============
        modelBuilder.Entity<Stock>().HasIndex(s => new { s.WarehouseId, s.ProductId }).IsUnique();

        // ============ ERP-009 库存成本与库存单据小数位（显式声明） ============
        // 说明：成本单价 6 位小数、数量与金额 4 位小数，与 SchemaUpgrader 第 23 段的建表脚本保持一致。
        //       若依赖 EF 默认 decimal(18,2)，空库首次 EnsureCreated 建表会把成本单价截断到 2 位，
        //       导致加权平均成本不再等于「库存金额 / 库存数量」，估价与流水无法互相核对。
        modelBuilder.Entity<Stock>().Property(x => x.AverageCost).HasPrecision(18, 6);
        modelBuilder.Entity<Stock>().Property(x => x.TotalCost).HasPrecision(18, 4);

        modelBuilder.Entity<StockMovement>().Property(x => x.UnitCost).HasPrecision(18, 6);
        modelBuilder.Entity<StockMovement>().Property(x => x.BalanceAverageCost).HasPrecision(18, 6);
        modelBuilder.Entity<StockMovement>().Property(x => x.Quantity).HasPrecision(18, 4);
        modelBuilder.Entity<StockMovement>().Property(x => x.Amount).HasPrecision(18, 4);
        modelBuilder.Entity<StockMovement>().Property(x => x.BalanceQuantity).HasPrecision(18, 4);
        modelBuilder.Entity<StockMovement>().Property(x => x.BalanceAmount).HasPrecision(18, 4);

        modelBuilder.Entity<StockAdjustment>().Property(x => x.TotalDiffQuantity).HasPrecision(18, 4);
        modelBuilder.Entity<StockAdjustment>().Property(x => x.TotalDiffAmount).HasPrecision(18, 4);
        modelBuilder.Entity<StockAdjustmentDetail>().Property(x => x.BookQuantity).HasPrecision(18, 4);
        modelBuilder.Entity<StockAdjustmentDetail>().Property(x => x.ActualQuantity).HasPrecision(18, 4);
        modelBuilder.Entity<StockAdjustmentDetail>().Property(x => x.DiffQuantity).HasPrecision(18, 4);
        modelBuilder.Entity<StockAdjustmentDetail>().Property(x => x.UnitCost).HasPrecision(18, 6);
        modelBuilder.Entity<StockAdjustmentDetail>().Property(x => x.DiffAmount).HasPrecision(18, 4);

        modelBuilder.Entity<StockTransfer>().Property(x => x.TotalQuantity).HasPrecision(18, 4);
        modelBuilder.Entity<StockTransfer>().Property(x => x.TotalAmount).HasPrecision(18, 4);
        modelBuilder.Entity<StockTransferDetail>().Property(x => x.Quantity).HasPrecision(18, 4);
        modelBuilder.Entity<StockTransferDetail>().Property(x => x.UnitCost).HasPrecision(18, 6);
        modelBuilder.Entity<StockTransferDetail>().Property(x => x.Amount).HasPrecision(18, 4);

        modelBuilder.Entity<SalesReturn>().Property(x => x.TotalQuantity).HasPrecision(18, 4);
        modelBuilder.Entity<SalesReturn>().Property(x => x.TotalAmount).HasPrecision(18, 4);
        modelBuilder.Entity<SalesReturnDetail>().Property(x => x.Quantity).HasPrecision(18, 4);
        modelBuilder.Entity<SalesReturnDetail>().Property(x => x.UnitPrice).HasPrecision(18, 4);
        modelBuilder.Entity<SalesReturnDetail>().Property(x => x.Amount).HasPrecision(18, 4);
        modelBuilder.Entity<SalesReturnDetail>().Property(x => x.UnitCost).HasPrecision(18, 6);

        modelBuilder.Entity<PurchaseReturn>().Property(x => x.TotalQuantity).HasPrecision(18, 4);
        modelBuilder.Entity<PurchaseReturn>().Property(x => x.TotalAmount).HasPrecision(18, 4);
        modelBuilder.Entity<PurchaseReturnDetail>().Property(x => x.Quantity).HasPrecision(18, 4);
        modelBuilder.Entity<PurchaseReturnDetail>().Property(x => x.UnitPrice).HasPrecision(18, 4);
        modelBuilder.Entity<PurchaseReturnDetail>().Property(x => x.Amount).HasPrecision(18, 4);
        modelBuilder.Entity<PurchaseReturnDetail>().Property(x => x.UnitCost).HasPrecision(18, 6);

        // ============ 明细外键级联删除 ============
        modelBuilder.Entity<InquiryDetail>()
            .HasOne<Inquiry>().WithMany(i => i.Details)
            .HasForeignKey(d => d.InquiryId).OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<SalesOrderDetail>()
            .HasOne<SalesOrder>().WithMany(o => o.Details)
            .HasForeignKey(d => d.SalesOrderId).OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<PurchaseOrderDetail>()
            .HasOne<PurchaseOrder>().WithMany(o => o.Details)
            .HasForeignKey(d => d.PurchaseOrderId).OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<StockInDetail>()
            .HasOne<StockIn>().WithMany(o => o.Details)
            .HasForeignKey(d => d.StockInId).OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<StockOutDetail>()
            .HasOne<StockOut>().WithMany(o => o.Details)
            .HasForeignKey(d => d.StockOutId).OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<ContainerPreLoadingDetail>()
            .HasOne<ContainerPreLoading>().WithMany(o => o.Details)
            .HasForeignKey(d => d.PreLoadingId).OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<ContainerLoadingDetail>()
            .HasOne<ContainerLoadingList>().WithMany(o => o.Details)
            .HasForeignKey(d => d.LoadingListId).OnDelete(DeleteBehavior.Cascade);

        // 形式发票 PI 明细（阶段 3）：外键名为 PiId，不符合 EF 默认命名约定（<主表实体>Id），
        // 必须显式配置，否则主子表不建立关系（Include 取不到明细、保存时也不会回填外键）
        modelBuilder.Entity<ProformaInvoiceDetail>()
            .HasOne<ProformaInvoice>().WithMany(o => o.Details)
            .HasForeignKey(d => d.PiId).OnDelete(DeleteBehavior.Cascade);

        // ERP-009 库存单据明细：外键命名符合约定（<主表实体>Id），此处显式声明级联删除，
        // 与既有单据（入库/出库/装柜）保持一致：主表被物理删除时明细一并清理
        modelBuilder.Entity<StockAdjustmentDetail>()
            .HasOne<StockAdjustment>().WithMany(o => o.Details)
            .HasForeignKey(d => d.StockAdjustmentId).OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<StockTransferDetail>()
            .HasOne<StockTransfer>().WithMany(o => o.Details)
            .HasForeignKey(d => d.StockTransferId).OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<SalesReturnDetail>()
            .HasOne<SalesReturn>().WithMany(o => o.Details)
            .HasForeignKey(d => d.SalesReturnId).OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<PurchaseReturnDetail>()
            .HasOne<PurchaseReturn>().WithMany(o => o.Details)
            .HasForeignKey(d => d.PurchaseReturnId).OnDelete(DeleteBehavior.Cascade);
    }

    /// <summary>保存变更：自动填充审计字段</summary>
    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        ApplyAudit();
        return base.SaveChangesAsync(cancellationToken);
    }

    public override int SaveChanges()
    {
        ApplyAudit();
        return base.SaveChanges();
    }

    private void ApplyAudit()
    {
        var now = DateTime.Now;
        foreach (var entry in ChangeTracker.Entries<BaseEntity>())
        {
            if (entry.State == EntityState.Added && entry.Entity.CreatedAt == default)
            {
                entry.Entity.CreatedAt = now;
            }
            if (entry.State is EntityState.Added or EntityState.Modified)
            {
                entry.Entity.UpdatedAt = now;
            }
        }
    }
}
