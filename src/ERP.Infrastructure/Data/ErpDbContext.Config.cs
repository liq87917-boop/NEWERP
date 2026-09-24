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

        // ============ ERP-036：客户「指定货代」（引用「其他资料」Forwarder 字典项） ============
        // 设计口径：
        //   1. 只存引用 Id + 名称快照，**刻意不建外键**：字典项允许被软删除 / 停用，
        //      历史客户资料必须继续可读（外键会阻止字典项删除或使读取失败）；
        //   2. ForwarderName 为服务端权威写入的快照，长度与实体 [MaxLength(100)] 一致；
        //   3. ForwarderAvailable 是 [NotMapped] 的读取标注，不落库（SchemaUpgrader 第 25 段同样只加两列）。
        modelBuilder.Entity<BaseCustomer>().Property(x => x.ForwarderName).HasMaxLength(100);

        // ============ ERP-037：商品规格变体（颜色 / 尺码 SKU 子表） ============
        // 设计口径：
        //   1. 商品仍是唯一权威身份（BaseProducts 不变），规格是它下面「零到多条」的可选细分：
        //      没有规格的历史商品继续按单规格商品使用，无需任何回填；
        //   2. 规格**刻意不建外键**、也不被任何单据引用 —— 询价 / 报价 / PI / 订单 / 库存 / 库存流水的行
        //      保持原有 ProductId 口径，规格的新增 / 修改 / 停用 / 删除不改写历史行、不拆分库存、不重算成本；
        //   3. VariantCode 与 ColorSizeKey 都是服务端规范化后写入的列，因此唯一性可以直接落在数据库层；
        //   4. 两个过滤唯一索引与 SchemaUpgrader 第 26 段创建的同名索引保持一致：
        //      - 编码在同商品内唯一（软删除行不占用编码）；
        //      - 启用状态下「颜色 + 尺码」组合在同商品内唯一（停用行作为历史保留，不占用组合，
        //        因此允许「停用规格 + 新启用规格」并存；服务端在启用时会再次判定，二者不会同时启用）。
        //   5. VariantName / VariantCount / VariantTotalCount 均为 [NotMapped] 读取标注，不落库。
        modelBuilder.Entity<BaseProductVariant>().Property(x => x.VariantCode).HasMaxLength(50);
        modelBuilder.Entity<BaseProductVariant>().Property(x => x.Color).HasMaxLength(50);
        modelBuilder.Entity<BaseProductVariant>().Property(x => x.Size).HasMaxLength(50);
        modelBuilder.Entity<BaseProductVariant>().Property(x => x.ColorSizeKey).HasMaxLength(120);
        modelBuilder.Entity<BaseProductVariant>().Property(x => x.Remark).HasMaxLength(500);

        modelBuilder.Entity<BaseProductVariant>().HasIndex(x => new { x.ProductId, x.VariantCode })
            .IsUnique().HasDatabaseName("UX_BaseProductVariants_ProductCode")
            .HasFilter("IsDeleted = 0");

        modelBuilder.Entity<BaseProductVariant>().HasIndex(x => new { x.ProductId, x.ColorSizeKey })
            .IsUnique().HasDatabaseName("UX_BaseProductVariants_ProductColorSize")
            .HasFilter("IsDeleted = 0 AND Status = 1");

        // ============ ERP-038：商品 / SKU 货源关系（多供应商货源指引子表） ============
        // 设计口径：
        //   1. 关系只描述「这个商品 / 规格可以从哪些供应商采购、货号 / 采购单位 / MOQ / 交期是多少、哪家首选」，
        //      属于人工比价与下单前的**指引**：不自动选供应商、不定价、不生成或改写采购报价
        //      （PurchaseQuotes）、采购订单（PurchaseOrders / PurchaseOrderDetails）、库存成本
        //      （Stocks.AverageCost / TotalCost）与库存流水（StockMovements），也不改任何历史单据；
        //   2. 刻意不建外键：商品 / 规格 / 供应商允许软删除或停用，历史货源关系必须继续可读；
        //   3. ScopeKey 是服务端按规格推导的作用域键（P = 商品级；V{规格Id} = 规格级）：
        //      SQL Server 唯一索引对 NULL 不去重，用该列才能把「同一范围 + 同一供应商不重复」
        //      与「同一范围只有一个启用首选」落到数据库层；
        //   4. 两个过滤唯一索引与 SchemaUpgrader 第 27 段创建的同名索引保持一致：
        //      - ScopeSupplier：同一「商品 + 作用域 + 供应商」不重复（软删除行不占用，停用的历史行仍占用，
        //        因此恢复供货应启用原关系而不是另建一条）；
        //      - ScopePreferred：同一「商品 + 作用域」最多一条启用中的首选（停用 / 已删除行不占用首选位）。
        //   5. 供应商侧列表按 SupplierId 检索：单独建一个过滤索引，避免全表扫描。
        modelBuilder.Entity<BaseProductSupplier>().Property(x => x.ScopeKey).HasMaxLength(30);
        modelBuilder.Entity<BaseProductSupplier>().Property(x => x.SupplierItemCode).HasMaxLength(100);
        modelBuilder.Entity<BaseProductSupplier>().Property(x => x.PurchaseUnit).HasMaxLength(20);
        modelBuilder.Entity<BaseProductSupplier>().Property(x => x.Remark).HasMaxLength(500);
        modelBuilder.Entity<BaseProductSupplier>().Property(x => x.MinOrderQty).HasPrecision(18, 4);

        modelBuilder.Entity<BaseProductSupplier>().HasIndex(x => new { x.ProductId, x.ScopeKey, x.SupplierId })
            .IsUnique().HasDatabaseName("UX_BaseProductSuppliers_ScopeSupplier")
            .HasFilter("IsDeleted = 0");

        modelBuilder.Entity<BaseProductSupplier>().HasIndex(x => new { x.ProductId, x.ScopeKey })
            .IsUnique().HasDatabaseName("UX_BaseProductSuppliers_ScopePreferred")
            .HasFilter("IsDeleted = 0 AND Status = 1 AND IsPreferred = 1");

        modelBuilder.Entity<BaseProductSupplier>().HasIndex(x => x.SupplierId)
            .HasDatabaseName("IX_BaseProductSuppliers_SupplierId")
            .HasFilter("IsDeleted = 0");

        // ============ ERP-040：订柜信息的外贸 / 物流跟踪字段（订柜信息 = 权威记录） ============
        // 设计口径：
        //   1. 文本列默认空串、日期列可空 = 「未知」，历史订柜记录无需回填
        //      （SchemaUpgrader 第 28 段同样只加列，不写任何历史数据）；
        //   2. CustomsBrokerId 刻意不建外键：报关行是允许软删除 / 停用的字典项，历史订柜记录必须继续可读；
        //   3. CustomsBrokerAvailable 是 [NotMapped] 的读取标注，不落库；
        //   4. InspectionRequired 为 bool? 三态（null = 未知 / false = 不需要查验 / true = 需要查验），
        //      未知绝不回落为 false；
        //   5. 预装柜单 / 装柜清单不加跟踪列：只按持久化引用只读回显订柜信息。
        modelBuilder.Entity<ContainerBooking>().Property(x => x.ShipmentMode).HasMaxLength(10);
        modelBuilder.Entity<ContainerBooking>().Property(x => x.BillOfLadingNo).HasMaxLength(50);
        modelBuilder.Entity<ContainerBooking>().Property(x => x.ShippingOrderNo).HasMaxLength(50);
        modelBuilder.Entity<ContainerBooking>().Property(x => x.TransitPort).HasMaxLength(100);
        modelBuilder.Entity<ContainerBooking>().Property(x => x.TruckerName).HasMaxLength(200);
        modelBuilder.Entity<ContainerBooking>().Property(x => x.CustomsBrokerName).HasMaxLength(100);

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

        // ============ 报价单版本链唯一约束（ERP-035） ============
        // 同一版本链内「版本号」唯一：并发创建版本时由该唯一索引在数据库层兜底，链内不可能出现重复版本号
        // （服务端捕获唯一冲突后返回「请重试」的业务错误，见 QuotationRevisionService）。
        // 根单的 RootQuotationId 为 NULL（自身即根，按主键天然唯一），因此只约束后续版本；
        // 软删除行不占用版本号，与 SysMenu / SysPrintTemplates 的过滤索引策略一致。
        // 与 SchemaUpgrader 第 24 段创建的同名索引保持一致。
        modelBuilder.Entity<Quotation>().HasIndex(x => new { x.RootQuotationId, x.RevisionNumber })
            .IsUnique().HasDatabaseName("UX_Quotations_RevisionChain")
            .HasFilter("IsDeleted = 0 AND RootQuotationId IS NOT NULL");

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
