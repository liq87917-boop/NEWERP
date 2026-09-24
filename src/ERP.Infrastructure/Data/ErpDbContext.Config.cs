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

        // ============ ERP-041：装柜清单多客户参与方（一柜多客户的客户归属清单子表） ============
        // 设计口径：
        //   1. 参与方只描述「这柜装了哪几个客户、哪个是主客户」：不按体积 / 重量 / 金额自动分摊费用、
        //      不生成费用单与结算记录，不改写装柜清单明细数量、柜号、订柜跟踪值、单证、库存与客户主数据；
        //   2. 刻意不建外键：客户允许被软删除 / 停用，历史参与方必须继续可读；
        //   3. CustomerCode / CustomerName 是服务端按客户主数据权威写入的**快照**，客户端提交值一律不被采信；
        //   4. 两个过滤唯一索引与 SchemaUpgrader 第 29 段创建的同名索引保持一致：
        //      - ListCustomer：同一装柜清单内同一客户不重复（软删除行不占用，停用的历史行仍占用，
        //        因此恢复参与应直接启用原参与方而不是另建一条）；
        //      - ListPrimary：同一装柜清单最多一条启用中的主参与方（停用 / 已删除行不占用主参与方位）。
        //   5. 客户侧检索单独建过滤索引，避免全表扫描；主参与方与列表顺序无关（服务端显式置主）。
        modelBuilder.Entity<ContainerLoadingListParticipant>().Property(x => x.CustomerCode).HasMaxLength(50);
        modelBuilder.Entity<ContainerLoadingListParticipant>().Property(x => x.CustomerName).HasMaxLength(200);
        modelBuilder.Entity<ContainerLoadingListParticipant>().Property(x => x.Remark).HasMaxLength(500);

        modelBuilder.Entity<ContainerLoadingListParticipant>().HasIndex(x => new { x.LoadingListId, x.CustomerId })
            .IsUnique().HasDatabaseName("UX_ContainerLoadingListParticipants_ListCustomer")
            .HasFilter("IsDeleted = 0");

        modelBuilder.Entity<ContainerLoadingListParticipant>().HasIndex(x => x.LoadingListId)
            .IsUnique().HasDatabaseName("UX_ContainerLoadingListParticipants_ListPrimary")
            .HasFilter("IsDeleted = 0 AND Status = 1 AND IsPrimary = 1");

        modelBuilder.Entity<ContainerLoadingListParticipant>().HasIndex(x => x.CustomerId)
            .HasDatabaseName("IX_ContainerLoadingListParticipants_CustomerId")
            .HasFilter("IsDeleted = 0");

        // ============ ERP-042：装柜费用分摊批次与分摊行（既有费用单之上的留痕层） ============
        // 设计口径：
        //   1. 分摊结果仍然是既有 FinanceExpense 行（一参与方一行），本两张表只记录
        //      「这次分摊是谁、按什么方法 / 基数 / 比例生成的」以及它分摊的来源费用 —— 不建第二套账务引擎；
        //   2. 有效批次唯一性：同一「来源费用 + 装柜清单 + 分摊方法」最多一条**有效**批次
        //      （UX_FinanceExpenseAllocationBatches_SourceLive，过滤 Status = 1），作废后可重新生成；
        //   3. 分摊行在同一批次内对同一参与方不重复（UX_FinanceExpenseAllocationLines_BatchParticipant）；
        //   4. 金额 2 位小数、比例 4 位小数、汇率 6 位小数，与 SchemaUpgrader 第 30 段的建表类型一致；
        //   5. FinanceExpense 侧的批次 / 来源列**可空**：历史行保持 NULL，读取侧按留痕分类显式标注，绝不回填。
        modelBuilder.Entity<FinanceExpenseAllocationBatch>().Property(x => x.BatchNo).HasMaxLength(50);
        modelBuilder.Entity<FinanceExpenseAllocationBatch>().Property(x => x.SourceExpenseNo).HasMaxLength(50);
        modelBuilder.Entity<FinanceExpenseAllocationBatch>().Property(x => x.LoadingListNo).HasMaxLength(50);
        modelBuilder.Entity<FinanceExpenseAllocationBatch>().Property(x => x.ContainerNo).HasMaxLength(50);
        modelBuilder.Entity<FinanceExpenseAllocationBatch>().Property(x => x.AllocationMethod).HasMaxLength(30);
        modelBuilder.Entity<FinanceExpenseAllocationBatch>().Property(x => x.BasisKind).HasMaxLength(30);
        modelBuilder.Entity<FinanceExpenseAllocationBatch>().Property(x => x.Currency).HasMaxLength(20);
        modelBuilder.Entity<FinanceExpenseAllocationBatch>().Property(x => x.ExchangeRate).HasPrecision(18, 6);
        modelBuilder.Entity<FinanceExpenseAllocationBatch>().Property(x => x.SourceAmount).HasPrecision(18, 2);
        modelBuilder.Entity<FinanceExpenseAllocationBatch>().Property(x => x.AllocatedTotal).HasPrecision(18, 2);
        modelBuilder.Entity<FinanceExpenseAllocationBatch>().Property(x => x.VoidReason).HasMaxLength(500);
        modelBuilder.Entity<FinanceExpenseAllocationBatch>().Property(x => x.Remark).HasMaxLength(500);

        modelBuilder.Entity<FinanceExpenseAllocationBatch>().HasIndex(x => x.BatchNo)
            .IsUnique().HasDatabaseName("UX_FinanceExpenseAllocationBatches_BatchNo");

        modelBuilder.Entity<FinanceExpenseAllocationBatch>()
            .HasIndex(x => new { x.SourceExpenseId, x.LoadingListId, x.AllocationMethod })
            .IsUnique().HasDatabaseName("UX_FinanceExpenseAllocationBatches_SourceLive")
            .HasFilter("IsDeleted = 0 AND Status = 1");

        modelBuilder.Entity<FinanceExpenseAllocationBatch>().HasIndex(x => x.LoadingListId)
            .HasDatabaseName("IX_FinanceExpenseAllocationBatches_LoadingListId")
            .HasFilter("IsDeleted = 0");

        modelBuilder.Entity<FinanceExpenseAllocationLine>().Property(x => x.BatchNo).HasMaxLength(50);
        modelBuilder.Entity<FinanceExpenseAllocationLine>().Property(x => x.SourceExpenseNo).HasMaxLength(50);
        modelBuilder.Entity<FinanceExpenseAllocationLine>().Property(x => x.LoadingListNo).HasMaxLength(50);
        modelBuilder.Entity<FinanceExpenseAllocationLine>().Property(x => x.ContainerNo).HasMaxLength(50);
        modelBuilder.Entity<FinanceExpenseAllocationLine>().Property(x => x.CustomerCode).HasMaxLength(50);
        modelBuilder.Entity<FinanceExpenseAllocationLine>().Property(x => x.CustomerName).HasMaxLength(200);
        modelBuilder.Entity<FinanceExpenseAllocationLine>().Property(x => x.AllocationMethod).HasMaxLength(30);
        modelBuilder.Entity<FinanceExpenseAllocationLine>().Property(x => x.BasisKind).HasMaxLength(30);
        modelBuilder.Entity<FinanceExpenseAllocationLine>().Property(x => x.BasisSource).HasMaxLength(30);
        modelBuilder.Entity<FinanceExpenseAllocationLine>().Property(x => x.BasisValue).HasPrecision(18, 4);
        modelBuilder.Entity<FinanceExpenseAllocationLine>().Property(x => x.Ratio).HasPrecision(18, 4);
        modelBuilder.Entity<FinanceExpenseAllocationLine>().Property(x => x.AllocatedAmount).HasPrecision(18, 2);
        modelBuilder.Entity<FinanceExpenseAllocationLine>().Property(x => x.AllocatedAmountCny).HasPrecision(18, 2);
        modelBuilder.Entity<FinanceExpenseAllocationLine>().Property(x => x.Currency).HasMaxLength(20);
        modelBuilder.Entity<FinanceExpenseAllocationLine>().Property(x => x.ExpenseNo).HasMaxLength(50);
        modelBuilder.Entity<FinanceExpenseAllocationLine>().Property(x => x.Remark).HasMaxLength(500);

        modelBuilder.Entity<FinanceExpenseAllocationLine>().HasIndex(x => new { x.BatchId, x.ParticipantId })
            .IsUnique().HasDatabaseName("UX_FinanceExpenseAllocationLines_BatchParticipant")
            .HasFilter("IsDeleted = 0");

        modelBuilder.Entity<FinanceExpenseAllocationLine>().HasIndex(x => x.SourceExpenseId)
            .HasDatabaseName("IX_FinanceExpenseAllocationLines_SourceExpenseId")
            .HasFilter("IsDeleted = 0");

        modelBuilder.Entity<FinanceExpenseAllocationLine>().HasIndex(x => x.ParticipantId)
            .HasDatabaseName("IX_FinanceExpenseAllocationLines_ParticipantId")
            .HasFilter("IsDeleted = 0");

        modelBuilder.Entity<FinanceExpenseAllocationLine>()
            .HasOne<FinanceExpenseAllocationBatch>().WithMany(o => o.Lines)
            .HasForeignKey(d => d.BatchId).OnDelete(DeleteBehavior.Cascade);

        // 分摊行 → 生成的费用单行：外键仅用于让 EF 在**同一次 SaveChanges** 内回填 ExpenseId
        // （写入顺序由依赖决定，费用单先于分摊行插入），删除行为为 Restrict —— 费用单只做软删除，
        // 因此不会影响任何既有删除语义。
        modelBuilder.Entity<FinanceExpenseAllocationLine>()
            .HasOne(x => x.Expense).WithMany()
            .HasForeignKey(x => x.ExpenseId).OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<FinanceExpenseAllocationLine>().HasIndex(x => x.ExpenseId)
            .HasDatabaseName("IX_FinanceExpenseAllocationLines_ExpenseId");

        // FinanceExpense 侧新增的批次 / 来源留痕列（可空 / 空串）：长度与建表脚本一致，
        // 批次号建过滤索引便于读取侧一次批量解析批次状态（不做逐行查询）
        modelBuilder.Entity<FinanceExpense>().Property(x => x.AllocationBatchNo).HasMaxLength(50);
        modelBuilder.Entity<FinanceExpense>().Property(x => x.AllocationSourceExpenseNo).HasMaxLength(50);
        modelBuilder.Entity<FinanceExpense>().HasIndex(x => x.AllocationBatchNo)
            .HasDatabaseName("IX_FinanceExpenses_AllocationBatchNo")
            .HasFilter("IsDeleted = 0 AND AllocationBatchNo <> N''");

        // ============ ERP-043：供应商采购发票登记（普票 / 专票证据 + 可选采购订单关联） ============
        // 设计口径：
        //   1. 本模块只是**运营证据台账**：不建应付账款 / 账龄 / 税务申报表，不生成凭证、收付款或结算单，
        //      也不改写采购订单状态 / 到货进度 / 库存成本 / 退税 / 供应商余额与付款状态；
        //   2. 有效身份唯一：同一「供应商 + 发票类型 + 规范化代码 / 号码」在**未作废**（Status <> 2）记录内唯一
        //      （UX_PurchaseInvoices_ActiveIdentity，过滤 IsDeleted = 0 AND Status <> 2）：作废记录保留可读但不占用身份；
        //   3. 关联行保存的服务端快照（单号 / 日期 / 币种 / 供应商）与关联金额；**刻意不建**到采购订单 / 供应商的外键，
        //      订单软删除、取消或供应商停用都不影响历史证据可读；
        //   4. 金额 DECIMAL(18,2)（币种精度最多 2 位，JPY 等 0 位由服务端按币种口径取整），
        //      与 SchemaUpgrader 第 31 段的建表类型一致；
        //   5. 索引与 SchemaUpgrader 第 31 段同名同过滤条件，供供应商 / 状态 / 开票日期与订单侧有界检索。
        modelBuilder.Entity<PurchaseInvoice>().Property(x => x.InvoiceType).HasMaxLength(20);
        modelBuilder.Entity<PurchaseInvoice>().Property(x => x.InvoiceCode).HasMaxLength(50);
        modelBuilder.Entity<PurchaseInvoice>().Property(x => x.InvoiceNumber).HasMaxLength(50);
        modelBuilder.Entity<PurchaseInvoice>().Property(x => x.NormalizedInvoiceCode).HasMaxLength(50);
        modelBuilder.Entity<PurchaseInvoice>().Property(x => x.NormalizedInvoiceNumber).HasMaxLength(50);
        modelBuilder.Entity<PurchaseInvoice>().Property(x => x.SupplierCode).HasMaxLength(50);
        modelBuilder.Entity<PurchaseInvoice>().Property(x => x.SupplierName).HasMaxLength(200);
        modelBuilder.Entity<PurchaseInvoice>().Property(x => x.Currency).HasMaxLength(20);
        modelBuilder.Entity<PurchaseInvoice>().Property(x => x.NetAmount).HasPrecision(18, 2);
        modelBuilder.Entity<PurchaseInvoice>().Property(x => x.TaxAmount).HasPrecision(18, 2);
        modelBuilder.Entity<PurchaseInvoice>().Property(x => x.GrossAmount).HasPrecision(18, 2);
        modelBuilder.Entity<PurchaseInvoice>().Property(x => x.VoidReason).HasMaxLength(500);
        modelBuilder.Entity<PurchaseInvoice>().Property(x => x.Remark).HasMaxLength(500);

        modelBuilder.Entity<PurchaseInvoice>()
            .HasIndex(x => new
            {
                x.SupplierId,
                x.InvoiceType,
                x.NormalizedInvoiceCode,
                x.NormalizedInvoiceNumber
            })
            .IsUnique().HasDatabaseName("UX_PurchaseInvoices_ActiveIdentity")
            .HasFilter("IsDeleted = 0 AND Status <> 2");

        modelBuilder.Entity<PurchaseInvoice>().HasIndex(x => x.SupplierId)
            .HasDatabaseName("IX_PurchaseInvoices_SupplierId")
            .HasFilter("IsDeleted = 0");

        modelBuilder.Entity<PurchaseInvoice>().HasIndex(x => new { x.Status, x.InvoiceDate })
            .HasDatabaseName("IX_PurchaseInvoices_Status_InvoiceDate")
            .HasFilter("IsDeleted = 0");

        modelBuilder.Entity<PurchaseInvoice>().HasIndex(x => x.NormalizedInvoiceNumber)
            .HasDatabaseName("IX_PurchaseInvoices_NormalizedInvoiceNumber")
            .HasFilter("IsDeleted = 0");

        modelBuilder.Entity<PurchaseInvoiceAllocation>().Property(x => x.OrderNo).HasMaxLength(50);
        modelBuilder.Entity<PurchaseInvoiceAllocation>().Property(x => x.OrderCurrency).HasMaxLength(20);
        modelBuilder.Entity<PurchaseInvoiceAllocation>().Property(x => x.SupplierCode).HasMaxLength(50);
        modelBuilder.Entity<PurchaseInvoiceAllocation>().Property(x => x.SupplierName).HasMaxLength(200);
        modelBuilder.Entity<PurchaseInvoiceAllocation>().Property(x => x.AllocatedAmount).HasPrecision(18, 2);
        modelBuilder.Entity<PurchaseInvoiceAllocation>().Property(x => x.Currency).HasMaxLength(20);
        modelBuilder.Entity<PurchaseInvoiceAllocation>().Property(x => x.Remark).HasMaxLength(500);

        // 同一张发票内同一张采购订单只能关联一次（重复提交由服务端先行拒绝，索引为并发兜底）
        modelBuilder.Entity<PurchaseInvoiceAllocation>()
            .HasIndex(x => new { x.PurchaseInvoiceId, x.PurchaseOrderId })
            .IsUnique().HasDatabaseName("UX_PurchaseInvoiceAllocations_InvoiceOrder")
            .HasFilter("IsDeleted = 0");

        // 订单侧有界检索（不加 IsDeleted 过滤：已软删除的关联行也要能被订单侧一次读全）
        modelBuilder.Entity<PurchaseInvoiceAllocation>().HasIndex(x => x.PurchaseOrderId)
            .HasDatabaseName("IX_PurchaseInvoiceAllocations_PurchaseOrderId");

        // 关联行 → 发票（明细级联）：发票只做软删除，物理删除时才清理关联行
        modelBuilder.Entity<PurchaseInvoiceAllocation>()
            .HasOne(x => x.Invoice).WithMany(i => i.Allocations)
            .HasForeignKey(x => x.PurchaseInvoiceId).OnDelete(DeleteBehavior.Cascade);

        // ============ ERP-045：业务单据附件引用登记（仅元数据的附件引用册） ============
        // 设计口径：
        //   1. 本表只登记**元数据引用**：分类 / 安全显示名 / 不透明引用标识 / 可选内容类型 / 字节数 /
        //      校验和 / 备注 + 来源授权确认留痕；**不**保存文件内容、**不**保存链接、
        //      **不**上传 / 下载 / 预览 / 抓取 / 覆盖 / 删除任何 OSS 对象（真正的存储集成另行人工审批）；
        //   2. 父单据只保存**服务端写入**的号码 / 类型快照（ParentNo / ParentTypeText），
        //      **刻意不建**到销售订单 / 采购订单 / 装柜清单 / 单证的数据库外键：父单据改名、停用或软删除
        //      都不影响历史引用可读，本表也不参与父单据的金额、库存、财务与出运计算；
        //   3. 有效身份唯一：同一「父单据类型 + 父单据 Id + 分类 + 不透明引用标识」在**有效**记录内唯一
        //      （UX_DocumentAttachmentReferences_ActiveIdentity，过滤 IsDeleted = 0 AND Status = 0）：
        //      已作废记录保留可读但不占用身份，作废后可重新登记同一引用标识；
        //   4. 索引与 SchemaUpgrader 第 32 段同名同过滤条件（父单据有界检索 / 引用标识检索）；
        //   5. 本段只改本模块模型映射：不改写父单据、库存与库存成本、财务、出运与审批数据。
        modelBuilder.Entity<DocumentAttachmentReference>().Property(x => x.ParentType).HasMaxLength(30);
        modelBuilder.Entity<DocumentAttachmentReference>().Property(x => x.ParentNo).HasMaxLength(50);
        modelBuilder.Entity<DocumentAttachmentReference>().Property(x => x.ParentTypeText).HasMaxLength(30);
        modelBuilder.Entity<DocumentAttachmentReference>().Property(x => x.Category).HasMaxLength(30);
        modelBuilder.Entity<DocumentAttachmentReference>().Property(x => x.DisplayName).HasMaxLength(200);
        modelBuilder.Entity<DocumentAttachmentReference>().Property(x => x.ReferenceId).HasMaxLength(200);
        modelBuilder.Entity<DocumentAttachmentReference>().Property(x => x.ContentType).HasMaxLength(120);
        modelBuilder.Entity<DocumentAttachmentReference>().Property(x => x.Checksum).HasMaxLength(128);
        modelBuilder.Entity<DocumentAttachmentReference>().Property(x => x.Notes).HasMaxLength(500);
        modelBuilder.Entity<DocumentAttachmentReference>().Property(x => x.SourceAuthorizationNote).HasMaxLength(300);
        modelBuilder.Entity<DocumentAttachmentReference>().Property(x => x.AuthorizedBy).HasMaxLength(100);
        modelBuilder.Entity<DocumentAttachmentReference>().Property(x => x.VoidReason).HasMaxLength(500);

        modelBuilder.Entity<DocumentAttachmentReference>()
            .HasIndex(x => new { x.ParentType, x.ParentId, x.Category, x.ReferenceId })
            .IsUnique().HasDatabaseName("UX_DocumentAttachmentReferences_ActiveIdentity")
            .HasFilter("IsDeleted = 0 AND Status = 0");

        modelBuilder.Entity<DocumentAttachmentReference>()
            .HasIndex(x => new { x.ParentType, x.ParentId, x.Status })
            .HasDatabaseName("IX_DocumentAttachmentReferences_ParentType_ParentId")
            .HasFilter("IsDeleted = 0");

        modelBuilder.Entity<DocumentAttachmentReference>()
            .HasIndex(x => x.ReferenceId)
            .HasDatabaseName("IX_DocumentAttachmentReferences_ReferenceId")
            .HasFilter("IsDeleted = 0");

        // ============ ERP-047：销售订单变更申请登记（只登记拟议变更的不可变登记册） ============
        // 设计口径：
        //   1. 只建「申请 + 拟议明细」两张表：来源销售订单只保存**服务端写入的快照**（Source* 列），
        //      **刻意不建**到 SalesOrders / SalesOrderDetails 的外键 —— 来源改名、停用或软删除都不影响历史申请可读，
        //      本模块也不参与来源订单的金额、库存、库存成本、出运与财务计算；
        //   2. 状态只有 0 草稿 / 1 已提交 / 2 已取消（**没有**「已批准 / 已套用」）；更正走取消并保留原因，不提供硬删除；
        //   3. 拟议金额（明细金额 / 总额 / 定金金额）由服务端按销售订单唯一权威算法
        //      （SalesOrderAmountRules：金额 = 数量 × 单价、总额 = Σ 明细金额、定金 = 总额 × 定金比例%）重算，
        //      精度声明与 SchemaUpgrader 第 33 段一致：数量 / 金额 DECIMAL(18,4)、汇率 DECIMAL(18,6)；
        //   4. 索引与 SchemaUpgrader 第 33 段同名同过滤条件：申请号 / 来源订单 / 状态均有界检索；
        //   5. 明细 → 申请（级联）：申请本身只做软删除，物理删除时才清理明细行。
        modelBuilder.Entity<SalesOrderChangeRequest>().Property(x => x.RequestNo).HasMaxLength(50);
        modelBuilder.Entity<SalesOrderChangeRequest>().Property(x => x.SalesOrderNo).HasMaxLength(50);
        modelBuilder.Entity<SalesOrderChangeRequest>().Property(x => x.SourceDetailSignature).HasMaxLength(500);
        modelBuilder.Entity<SalesOrderChangeRequest>().Property(x => x.SourceSnapshotMarker).HasMaxLength(300);
        modelBuilder.Entity<SalesOrderChangeRequest>().Property(x => x.Reason).HasMaxLength(500);
        modelBuilder.Entity<SalesOrderChangeRequest>().Property(x => x.CancelledReason).HasMaxLength(500);
        modelBuilder.Entity<SalesOrderChangeRequest>().Property(x => x.SourceExchangeRate).HasPrecision(18, 6);
        modelBuilder.Entity<SalesOrderChangeRequest>().Property(x => x.ProposedExchangeRate).HasPrecision(18, 6);
        modelBuilder.Entity<SalesOrderChangeRequest>().Property(x => x.SourceTotalAmount).HasPrecision(18, 4);
        modelBuilder.Entity<SalesOrderChangeRequest>().Property(x => x.ProposedTotalAmount).HasPrecision(18, 4);
        modelBuilder.Entity<SalesOrderChangeRequest>().Property(x => x.SourceDepositRatio).HasPrecision(18, 4);
        modelBuilder.Entity<SalesOrderChangeRequest>().Property(x => x.ProposedDepositRatio).HasPrecision(18, 4);
        modelBuilder.Entity<SalesOrderChangeRequest>().Property(x => x.SourceDepositAmount).HasPrecision(18, 4);
        modelBuilder.Entity<SalesOrderChangeRequest>().Property(x => x.ProposedDepositAmount).HasPrecision(18, 4);
        modelBuilder.Entity<SalesOrderChangeRequest>().Property(x => x.SourceCommissionRatio).HasPrecision(18, 4);
        modelBuilder.Entity<SalesOrderChangeRequest>().Property(x => x.ProposedCommissionRatio).HasPrecision(18, 4);

        modelBuilder.Entity<SalesOrderChangeRequest>()
            .HasIndex(x => x.RequestNo)
            .IsUnique().HasDatabaseName("UX_SalesOrderChangeRequests_RequestNo")
            .HasFilter("IsDeleted = 0");

        modelBuilder.Entity<SalesOrderChangeRequest>()
            .HasIndex(x => new { x.SalesOrderId, x.Status })
            .HasDatabaseName("IX_SalesOrderChangeRequests_SourceOrder_Status")
            .HasFilter("IsDeleted = 0");

        modelBuilder.Entity<SalesOrderChangeRequest>()
            .HasIndex(x => new { x.Status, x.CreatedAt })
            .HasDatabaseName("IX_SalesOrderChangeRequests_Status_CreatedAt")
            .HasFilter("IsDeleted = 0");

        modelBuilder.Entity<SalesOrderChangeRequestDetail>().Property(x => x.SourceProductName).HasMaxLength(200);
        modelBuilder.Entity<SalesOrderChangeRequestDetail>().Property(x => x.SourceSpec).HasMaxLength(200);
        modelBuilder.Entity<SalesOrderChangeRequestDetail>().Property(x => x.SourceUnit).HasMaxLength(20);
        modelBuilder.Entity<SalesOrderChangeRequestDetail>().Property(x => x.SourceRemark).HasMaxLength(500);
        modelBuilder.Entity<SalesOrderChangeRequestDetail>().Property(x => x.ProposedProductName).HasMaxLength(200);
        modelBuilder.Entity<SalesOrderChangeRequestDetail>().Property(x => x.ProposedSpec).HasMaxLength(200);
        modelBuilder.Entity<SalesOrderChangeRequestDetail>().Property(x => x.ProposedUnit).HasMaxLength(20);
        modelBuilder.Entity<SalesOrderChangeRequestDetail>().Property(x => x.ProposedQuantity).HasPrecision(18, 4);
        modelBuilder.Entity<SalesOrderChangeRequestDetail>().Property(x => x.ProposedUnitPrice).HasPrecision(18, 4);
        modelBuilder.Entity<SalesOrderChangeRequestDetail>().Property(x => x.ProposedAmount).HasPrecision(18, 4);
        modelBuilder.Entity<SalesOrderChangeRequestDetail>().Property(x => x.SourceQuantity).HasPrecision(18, 4);
        modelBuilder.Entity<SalesOrderChangeRequestDetail>().Property(x => x.SourceUnitPrice).HasPrecision(18, 4);
        modelBuilder.Entity<SalesOrderChangeRequestDetail>().Property(x => x.SourceAmount).HasPrecision(18, 4);
        modelBuilder.Entity<SalesOrderChangeRequestDetail>().Property(x => x.ProposedRemark).HasMaxLength(500);

        modelBuilder.Entity<SalesOrderChangeRequestDetail>()
            .HasIndex(x => new { x.ChangeRequestId, x.LineNo })
            .HasDatabaseName("IX_SalesOrderChangeRequestDetails_Request_LineNo")
            .HasFilter("IsDeleted = 0");

        modelBuilder.Entity<SalesOrderChangeRequestDetail>()
            .HasOne<SalesOrderChangeRequest>().WithMany(o => o.Details)
            .HasForeignKey(d => d.ChangeRequestId).OnDelete(DeleteBehavior.Cascade);

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

        // 装柜清单多客户参与方（ERP-041）：明细/子表命名符合约定（LoadingListId），此处显式声明级联删除，
        // 与既有单据子表保持一致：装柜清单被物理删除时参与方一并清理（本表仍只做软删除，不做物理删除）。
        modelBuilder.Entity<ContainerLoadingListParticipant>()
            .HasOne<ContainerLoadingList>().WithMany(o => o.Participants)
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

        // ============ ERP-049：供应商付款引用登记（付款单 → 采购订单 的引用证据行） ============
        // 设计口径：
        //   1. 本表只登记**引用证据**：不建应付账款 / 核销 / 账龄 / 税务表，不生成凭证、收付款或结算单，
        //      也不改写付款单状态 / 金额 / 币种 / 付款方式 / 银行账户与采购订单状态 / 到货进度 / 结算进度；
        //   2. 有效引用行唯一：同一「付款单 + 采购订单」在**有效**（Status <> 2）记录内唯一
        //      （UX_SupplierPaymentAllocations_PaymentOrder，过滤 IsDeleted = 0 AND Status <> 2）——
        //      已作废行保留可读但不占用额度，可重新登记新行；
        //   3. 付款单 / 供应商 / 采购订单只保存**服务端写入**的快照（单号 / 日期 / 状态 / 金额 / 币种 / 编码名称）；
        //      **刻意不建**到采购订单与供应商的外键：订单软删除或取消、供应商停用或改名都不影响历史证据可读；
        //     4. 金额 DECIMAL(18,2)（币种精度最多 2 位，JPY 等 0 位由服务端按币种口径取整），
        //      与 SchemaUpgrader 第 34 段建表类型一致；
        //   5. **刻意不建任何外键**（付款单只做软删除；采购订单与供应商可能被软删除 / 取消 / 改名）——
        //      本表只保存服务端快照，历史引用证据必须始终可读，也不参与付款单与采购订单的计算；
        //   6. 索引与 SchemaUpgrader 第 34 段同名同过滤条件，供付款单 / 采购订单侧有界检索。
        modelBuilder.Entity<SupplierPaymentAllocation>().Property(x => x.PaymentNo).HasMaxLength(50);
        modelBuilder.Entity<SupplierPaymentAllocation>().Property(x => x.PaymentStatusText).HasMaxLength(30);
        modelBuilder.Entity<SupplierPaymentAllocation>().Property(x => x.PaymentAmount).HasPrecision(18, 2);
        modelBuilder.Entity<SupplierPaymentAllocation>().Property(x => x.OrderNo).HasMaxLength(50);
        modelBuilder.Entity<SupplierPaymentAllocation>().Property(x => x.OrderCurrency).HasMaxLength(20);
        modelBuilder.Entity<SupplierPaymentAllocation>().Property(x => x.SupplierCode).HasMaxLength(50);
        modelBuilder.Entity<SupplierPaymentAllocation>().Property(x => x.SupplierName).HasMaxLength(200);
        modelBuilder.Entity<SupplierPaymentAllocation>().Property(x => x.AllocatedAmount).HasPrecision(18, 2);
        modelBuilder.Entity<SupplierPaymentAllocation>().Property(x => x.Currency).HasMaxLength(20);
        modelBuilder.Entity<SupplierPaymentAllocation>().Property(x => x.Remark).HasMaxLength(500);
        modelBuilder.Entity<SupplierPaymentAllocation>().Property(x => x.VoidReason).HasMaxLength(500);

        // 同一张付款单内同一张采购订单只能有一条有效引用行（重复提交由服务端先行拒绝，索引为并发兜底）
        modelBuilder.Entity<SupplierPaymentAllocation>()
            .HasIndex(x => new { x.PaymentId, x.PurchaseOrderId })
            .IsUnique().HasDatabaseName("UX_SupplierPaymentAllocations_PaymentOrder")
            .HasFilter("IsDeleted = 0 AND Status <> 2");

        modelBuilder.Entity<SupplierPaymentAllocation>().HasIndex(x => new { x.PaymentId, x.Status })
            .HasDatabaseName("IX_SupplierPaymentAllocations_PaymentId_Status")
            .HasFilter("IsDeleted = 0");

        modelBuilder.Entity<SupplierPaymentAllocation>().HasIndex(x => x.PurchaseOrderId)
            .HasDatabaseName("IX_SupplierPaymentAllocations_PurchaseOrderId")
            .HasFilter("IsDeleted = 0");

        modelBuilder.Entity<SupplierPaymentAllocation>().HasIndex(x => new { x.Status, x.AllocatedAt })
            .HasDatabaseName("IX_SupplierPaymentAllocations_Status_AllocatedAt")
            .HasFilter("IsDeleted = 0");

        // 刻意不建任何外键（与 ERP-045 / ERP-047 一致）：付款单只做软删除，采购订单 / 供应商可能被软删除、
        // 取消或改名 —— 本表只保存服务端快照，历史引用证据必须始终可读，且不参与付款单与采购订单的计算。

        // ============ ERP-053：客户收款引用登记（收款单 → 销售订单 的引用证据行） ============
        // 设计口径：
        //   1. 本表只登记**引用证据**：不建应收账款 / 核销 / 账龄 / 税务表，不生成凭证、收款或结算单，
        //      也不改写收款单状态 / 金额 / 币种 / 付款方式 / 银行账户与销售订单状态 / 出货进度 / 金额与明细；
        //   2. 有效引用行唯一：同一「收款单 + 销售订单」在**有效**（Status <> 2）记录内唯一
        //      （UX_CustomerReceiptAllocations_ReceiptOrder，过滤 IsDeleted = 0 AND Status <> 2）——
        //      已作废行保留可读但不占用额度，可重新登记新行；
        //   3. 收款单 / 客户 / 销售订单只保存**服务端写入**的快照（单号 / 日期 / 状态 / 金额 / 币种 / 编码名称）；
        //      与 SchemaUpgrader 第 36 段建表类型一致；
        //   4. 金额 DECIMAL(18,2)（币种精度最多 2 位，JPY 等 0 位由服务端按币种口径取整）；
        //   5. **刻意不建任何外键**（收款单只做软删除；销售订单与客户可能被软删除 / 取消 / 停用 / 改名）——
        //      本表只保存服务端快照，历史引用证据必须始终可读，也不参与收款单与销售订单的计算；
        //   6. 索引与 SchemaUpgrader 第 36 段同名同过滤条件，供收款单 / 销售订单侧有界检索。
        modelBuilder.Entity<CustomerReceiptAllocation>().Property(x => x.ReceiptNo).HasMaxLength(50);
        modelBuilder.Entity<CustomerReceiptAllocation>().Property(x => x.ReceiptStatusText).HasMaxLength(30);
        modelBuilder.Entity<CustomerReceiptAllocation>().Property(x => x.ReceiptAmount).HasPrecision(18, 2);
        modelBuilder.Entity<CustomerReceiptAllocation>().Property(x => x.OrderNo).HasMaxLength(50);
        modelBuilder.Entity<CustomerReceiptAllocation>().Property(x => x.OrderCurrency).HasMaxLength(20);
        modelBuilder.Entity<CustomerReceiptAllocation>().Property(x => x.CustomerCode).HasMaxLength(50);
        modelBuilder.Entity<CustomerReceiptAllocation>().Property(x => x.CustomerName).HasMaxLength(200);
        modelBuilder.Entity<CustomerReceiptAllocation>().Property(x => x.AllocatedAmount).HasPrecision(18, 2);
        modelBuilder.Entity<CustomerReceiptAllocation>().Property(x => x.Currency).HasMaxLength(20);
        modelBuilder.Entity<CustomerReceiptAllocation>().Property(x => x.Remark).HasMaxLength(500);
        modelBuilder.Entity<CustomerReceiptAllocation>().Property(x => x.VoidReason).HasMaxLength(500);

        // 同一张收款单内同一张销售订单只能有一条有效引用行（重复提交由服务端先行拒绝，索引为并发兜底）
        modelBuilder.Entity<CustomerReceiptAllocation>()
            .HasIndex(x => new { x.ReceiptId, x.SalesOrderId })
            .IsUnique().HasDatabaseName("UX_CustomerReceiptAllocations_ReceiptOrder")
            .HasFilter("IsDeleted = 0 AND Status <> 2");

        modelBuilder.Entity<CustomerReceiptAllocation>().HasIndex(x => new { x.ReceiptId, x.Status })
            .HasDatabaseName("IX_CustomerReceiptAllocations_ReceiptId_Status")
            .HasFilter("IsDeleted = 0");

        modelBuilder.Entity<CustomerReceiptAllocation>().HasIndex(x => x.SalesOrderId)
            .HasDatabaseName("IX_CustomerReceiptAllocations_SalesOrderId")
            .HasFilter("IsDeleted = 0");

        modelBuilder.Entity<CustomerReceiptAllocation>().HasIndex(x => new { x.Status, x.AllocatedAt })
            .HasDatabaseName("IX_CustomerReceiptAllocations_Status_AllocatedAt")
            .HasFilter("IsDeleted = 0");

        // 刻意不建任何外键（与 ERP-045 / ERP-047 / ERP-049 一致）：收款单只做软删除，销售订单 / 客户可能被软删除、
        // 取消、停用或改名 —— 本表只保存服务端快照，历史引用证据必须始终可读，也不参与二者的计算。

        // ============ ERP-055：客户销项发票证据登记（普票 / 专票 / 出口发票证据台账 + 可选销售订单分摊） ============
        // 设计口径：
        //   1. 本模块只是**运营证据台账**：不建开票 / 税务申报 / 应收账款 / 账龄表，不生成凭证、收付款或结算单，
        //      也不调用任何开票 / 税务服务、不改写销售订单状态 / 出货进度 / 金额与明细、客户信用状态、
        //      客户收款单与其引用行、库存成本 / 流水、装柜与单证、佣金 / 回佣、退税与费用；
        //   2. 有效身份唯一：同一「客户 + 发票类型 + 规范化代码 / 号码」在**未作废**（Status <> 2）记录内唯一
        //      （UX_CustomerSalesInvoiceEvidences_ActiveIdentity，过滤 IsDeleted = 0 AND Status <> 2）：
        //      作废记录保留可读但不占用身份；
        //   3. 分摊行保存服务端快照（订单号 / 日期 / 状态 / 币种 / 客户）与分摊金额；**刻意不建**到销售订单 /
        //      客户 / 单证的外键：订单软删除或取消、客户停用、单证删除都不影响历史证据可读；
        //   4. 金额 DECIMAL(18,2)（币种精度最多 2 位，JPY 等 0 位由服务端按币种口径取整），
        //      与 SchemaUpgrader 第 37 段建表类型一致；
        //   5. 索引与 SchemaUpgrader 第 37 段同名同过滤条件，供客户 / 状态 / 开票日期与订单侧有界检索。
        modelBuilder.Entity<CustomerSalesInvoiceEvidence>().Property(x => x.InvoiceType).HasMaxLength(20);
        modelBuilder.Entity<CustomerSalesInvoiceEvidence>().Property(x => x.InvoiceCode).HasMaxLength(50);
        modelBuilder.Entity<CustomerSalesInvoiceEvidence>().Property(x => x.InvoiceNumber).HasMaxLength(50);
        modelBuilder.Entity<CustomerSalesInvoiceEvidence>().Property(x => x.NormalizedInvoiceCode).HasMaxLength(50);
        modelBuilder.Entity<CustomerSalesInvoiceEvidence>().Property(x => x.NormalizedInvoiceNumber).HasMaxLength(50);
        modelBuilder.Entity<CustomerSalesInvoiceEvidence>().Property(x => x.CustomerCode).HasMaxLength(50);
        modelBuilder.Entity<CustomerSalesInvoiceEvidence>().Property(x => x.CustomerName).HasMaxLength(200);
        modelBuilder.Entity<CustomerSalesInvoiceEvidence>().Property(x => x.Currency).HasMaxLength(20);
        modelBuilder.Entity<CustomerSalesInvoiceEvidence>().Property(x => x.NetAmount).HasPrecision(18, 2);
        modelBuilder.Entity<CustomerSalesInvoiceEvidence>().Property(x => x.TaxAmount).HasPrecision(18, 2);
        modelBuilder.Entity<CustomerSalesInvoiceEvidence>().Property(x => x.GrossAmount).HasPrecision(18, 2);
        modelBuilder.Entity<CustomerSalesInvoiceEvidence>().Property(x => x.TradeDocumentNo).HasMaxLength(50);
        modelBuilder.Entity<CustomerSalesInvoiceEvidence>().Property(x => x.TradeDocumentDocType).HasMaxLength(30);
        modelBuilder.Entity<CustomerSalesInvoiceEvidence>().Property(x => x.CommercialInvoiceReference).HasMaxLength(100);
        modelBuilder.Entity<CustomerSalesInvoiceEvidence>().Property(x => x.VoidReason).HasMaxLength(500);
        modelBuilder.Entity<CustomerSalesInvoiceEvidence>().Property(x => x.Remark).HasMaxLength(500);

        modelBuilder.Entity<CustomerSalesInvoiceEvidence>()
            .HasIndex(x => new
            {
                x.CustomerId,
                x.InvoiceType,
                x.NormalizedInvoiceCode,
                x.NormalizedInvoiceNumber
            })
            .IsUnique().HasDatabaseName("UX_CustomerSalesInvoiceEvidences_ActiveIdentity")
            .HasFilter("IsDeleted = 0 AND Status <> 2");

        modelBuilder.Entity<CustomerSalesInvoiceEvidence>().HasIndex(x => new { x.CustomerId, x.Status })
            .HasDatabaseName("IX_CustomerSalesInvoiceEvidences_CustomerId_Status")
            .HasFilter("IsDeleted = 0");

        modelBuilder.Entity<CustomerSalesInvoiceEvidence>().HasIndex(x => new { x.Status, x.InvoiceDate })
            .HasDatabaseName("IX_CustomerSalesInvoiceEvidences_Status_InvoiceDate")
            .HasFilter("IsDeleted = 0");

        modelBuilder.Entity<CustomerSalesInvoiceEvidence>().HasIndex(x => x.NormalizedInvoiceNumber)
            .HasDatabaseName("IX_CustomerSalesInvoiceEvidences_NormalizedInvoiceNumber")
            .HasFilter("IsDeleted = 0");

        modelBuilder.Entity<CustomerSalesInvoiceAllocation>().Property(x => x.OrderNo).HasMaxLength(50);
        modelBuilder.Entity<CustomerSalesInvoiceAllocation>().Property(x => x.OrderCurrency).HasMaxLength(20);
        modelBuilder.Entity<CustomerSalesInvoiceAllocation>().Property(x => x.CustomerCode).HasMaxLength(50);
        modelBuilder.Entity<CustomerSalesInvoiceAllocation>().Property(x => x.CustomerName).HasMaxLength(200);
        modelBuilder.Entity<CustomerSalesInvoiceAllocation>().Property(x => x.AllocatedAmount).HasPrecision(18, 2);
        modelBuilder.Entity<CustomerSalesInvoiceAllocation>().Property(x => x.Currency).HasMaxLength(20);
        modelBuilder.Entity<CustomerSalesInvoiceAllocation>().Property(x => x.Remark).HasMaxLength(500);

        // 同一张发票内同一张销售订单只能分摊一次（重复提交由服务端先行拒绝，索引为并发兜底）
        modelBuilder.Entity<CustomerSalesInvoiceAllocation>()
            .HasIndex(x => new { x.CustomerSalesInvoiceEvidenceId, x.SalesOrderId })
            .IsUnique().HasDatabaseName("UX_CustomerSalesInvoiceAllocations_InvoiceOrder")
            .HasFilter("IsDeleted = 0");

        modelBuilder.Entity<CustomerSalesInvoiceAllocation>().HasIndex(x => x.SalesOrderId)
            .HasDatabaseName("IX_CustomerSalesInvoiceAllocations_SalesOrderId")
            .HasFilter("IsDeleted = 0");

        // 刻意不建任何外键、也不建导航属性（与 ERP-045 / ERP-047 / ERP-049 / ERP-053 一致）：
        // 发票证据本身只做软删除，销售订单可能被取消或软删除、客户可能停用或删除、单证可能被删除 ——
        // 本表只保存服务端快照，历史证据必须始终可读，也不参与它们的金额与状态计算。

        // ============ ERP-051：单证明细行快照（商业发票 / 装箱单 的商品明细证据行） ============
        // 设计口径：
        //   1. 明细行是单证的**行级快照证据**：不是第二套商品主数据、不是库存交易、不是报关核定价格、
        //      也不是退税或税务依据；维护明细行不改写单证表头 / 状态 / 金额，也不改写商品资料、销售订单、
        //      采购订单、装柜清单、库存与库存流水、发票、退税、费用或财务记录；
        //   2. 商品资料**刻意不建外键**（商品可能被停用 / 软删除，历史明细行必须始终可读）；
        //      只保存服务端写入的编码 / 中英文名称 / 规格 / 单位快照，且商品引用未变化时不刷新历史快照；
        //   3. 行序（LineNo）在**同一单证内**对未删除行唯一：UX_TradeDocumentItems_Document_LineNo
        //      （TradeDocumentId + LineNo，过滤 IsDeleted = 0）—— 与 SchemaUpgrader 第 35 段同名同过滤条件，
        //      作为并发兜底（服务端仍先行拒绝重复行序）；
        //   4. 精度：数量 / 单价 / 净重 / 毛重 DECIMAL(18,4)，行金额 DECIMAL(18,2)（币种精度由服务端取整）；
        //      与 SchemaUpgrader 第 35 段建表类型一致；
        //   5. 明细行与单证主表建立外键（EF 侧 `DeleteBehavior.Cascade`：应用层物理删除单证时一并清理明细）；
        //      数据库脚本中的外键**不含级联动作**（与第 30 / 31 / 33 段同一幂等口径）；
        //      单证主表本身只做软删除：软删除不触发级联，明细行仍保留可读，但新增 / 修改 / 删除一律被服务端拒绝。
        modelBuilder.Entity<TradeDocumentItem>().Property(x => x.ProductCode).HasMaxLength(50);
        modelBuilder.Entity<TradeDocumentItem>().Property(x => x.ProductNameCn).HasMaxLength(200);
        modelBuilder.Entity<TradeDocumentItem>().Property(x => x.ProductNameEn).HasMaxLength(200);
        modelBuilder.Entity<TradeDocumentItem>().Property(x => x.Spec).HasMaxLength(200);
        modelBuilder.Entity<TradeDocumentItem>().Property(x => x.Quantity).HasPrecision(18, 4);
        modelBuilder.Entity<TradeDocumentItem>().Property(x => x.Unit).HasMaxLength(20);
        modelBuilder.Entity<TradeDocumentItem>().Property(x => x.UnitPrice).HasPrecision(18, 4);
        modelBuilder.Entity<TradeDocumentItem>().Property(x => x.LineAmount).HasPrecision(18, 2);
        modelBuilder.Entity<TradeDocumentItem>().Property(x => x.NetWeight).HasPrecision(18, 4);
        modelBuilder.Entity<TradeDocumentItem>().Property(x => x.GrossWeight).HasPrecision(18, 4);
        modelBuilder.Entity<TradeDocumentItem>().Property(x => x.Currency).HasMaxLength(20);
        modelBuilder.Entity<TradeDocumentItem>().Property(x => x.Remark).HasMaxLength(500);

        // 同一单证内同一行序只能有一条未删除明细行（重复由服务端先行拒绝，索引为并发兜底）
        modelBuilder.Entity<TradeDocumentItem>()
            .HasIndex(x => new { x.TradeDocumentId, x.LineNo })
            .IsUnique().HasDatabaseName("UX_TradeDocumentItems_Document_LineNo")
            .HasFilter("IsDeleted = 0");

        // 明细行随单证主表物理清理（单证本身只做软删除：软删除不会触发级联，明细仍保留可读）
        modelBuilder.Entity<TradeDocumentItem>()
            .HasOne<TradeDocument>().WithMany()
            .HasForeignKey(x => x.TradeDocumentId).OnDelete(DeleteBehavior.Cascade);

        // ============ ERP-057：装柜出运引用登记（显式源记录关联 + 出运证据快照 + 修订留痕） ============
        // 设计口径：
        //   1. 审计结论：装柜链路已有**唯一**的持久化引用关系（ContainerPreLoading.BookingId /
        //      ContainerLoadingList.PreLoadingId），订柜信息（ContainerBooking）由 ERP-040 承载本套跟踪值的
        //      权威记录 —— 因此本模块**不**新建出运主数据、**不**在订柜 / 预装柜 / 装柜清单上加列，
        //      只按显式（SourceType, SourceId）指向一条权威记录登记用户录入的出运证据；
        //   2. 有效引用唯一：同一条源记录最多 1 条有效引用
        //      （UX_ContainerShipmentReferences_ActiveSource，过滤 IsDeleted = 0 AND Status <> 2）——
        //      已作废行保留可读但不占用额度（作废后可重新登记，新旧并存可查）；
        //   3. 源记录只保存**服务端写入**的快照（单号 / 日期 / 状态 / 柜号），**刻意不建任何外键**：
        //      源记录被软删除 / 改名后历史证据必须始终可读，只是显式标注不可用；
        //   4. 出运方式 / 港口 / 计划时间 / 承运人 / 货代 / 拖车 / 报关行快照全部可选：未填写保持空串 / NULL
        //      （= 未知），绝不由柜型、体积、客户、航线或自由文本推断；
        //   5. 修订留痕表只追加（唯一索引 UX_ContainerShipmentReferenceRevisions_Reference_Revision 保证
        //      同一引用内修订号不重复），保存修订前的原值，保证历史证据不被静默改写；
        //   6. 本模块只写本登记册两张表：不改写源记录与任何下游单据（订单 / 库存 / 单证 / 发票 / 费用 / 结算）。
        modelBuilder.Entity<ContainerShipmentReference>().Property(x => x.SourceType).HasMaxLength(20);
        modelBuilder.Entity<ContainerShipmentReference>().Property(x => x.SourceNo).HasMaxLength(50);
        modelBuilder.Entity<ContainerShipmentReference>().Property(x => x.SourceStatusText).HasMaxLength(30);
        modelBuilder.Entity<ContainerShipmentReference>().Property(x => x.ContainerNo).HasMaxLength(50);
        modelBuilder.Entity<ContainerShipmentReference>().Property(x => x.ShipmentMode).HasMaxLength(10);
        modelBuilder.Entity<ContainerShipmentReference>().Property(x => x.ShippingOrderNo).HasMaxLength(50);
        modelBuilder.Entity<ContainerShipmentReference>().Property(x => x.BillOfLadingNo).HasMaxLength(50);
        modelBuilder.Entity<ContainerShipmentReference>().Property(x => x.CarrierName).HasMaxLength(200);
        modelBuilder.Entity<ContainerShipmentReference>().Property(x => x.ForwarderName).HasMaxLength(200);
        modelBuilder.Entity<ContainerShipmentReference>().Property(x => x.DeparturePort).HasMaxLength(100);
        modelBuilder.Entity<ContainerShipmentReference>().Property(x => x.TransitPort).HasMaxLength(100);
        modelBuilder.Entity<ContainerShipmentReference>().Property(x => x.DestinationPort).HasMaxLength(100);
        modelBuilder.Entity<ContainerShipmentReference>().Property(x => x.TruckerName).HasMaxLength(200);
        modelBuilder.Entity<ContainerShipmentReference>().Property(x => x.CustomsBrokerName).HasMaxLength(100);
        modelBuilder.Entity<ContainerShipmentReference>().Property(x => x.Remark).HasMaxLength(500);
        modelBuilder.Entity<ContainerShipmentReference>().Property(x => x.LastRevisionReason).HasMaxLength(200);
        modelBuilder.Entity<ContainerShipmentReference>().Property(x => x.VoidReason).HasMaxLength(500);

        // 同一条源记录最多一条有效出运引用（重复提交由服务端先行拒绝，索引为并发兜底）
        modelBuilder.Entity<ContainerShipmentReference>()
            .HasIndex(x => new { x.SourceType, x.SourceId })
            .IsUnique().HasDatabaseName("UX_ContainerShipmentReferences_ActiveSource")
            .HasFilter("IsDeleted = 0 AND Status <> 2");

        modelBuilder.Entity<ContainerShipmentReference>().HasIndex(x => new { x.SourceType, x.SourceId, x.Status })
            .HasDatabaseName("IX_ContainerShipmentReferences_Source_Status")
            .HasFilter("IsDeleted = 0");

        modelBuilder.Entity<ContainerShipmentReference>().HasIndex(x => new { x.Status, x.RecordedAt })
            .HasDatabaseName("IX_ContainerShipmentReferences_Status_RecordedAt")
            .HasFilter("IsDeleted = 0");

        modelBuilder.Entity<ContainerShipmentReference>().HasIndex(x => x.ShipmentMode)
            .HasDatabaseName("IX_ContainerShipmentReferences_ShipmentMode")
            .HasFilter("IsDeleted = 0");

        // 修订留痕：同一引用内修订号唯一（只追加，不提供修改 / 删除），供按修订号倒序有界读取
        modelBuilder.Entity<ContainerShipmentReferenceRevision>().Property(x => x.SourceType).HasMaxLength(20);
        modelBuilder.Entity<ContainerShipmentReferenceRevision>().Property(x => x.SourceNo).HasMaxLength(50);
        modelBuilder.Entity<ContainerShipmentReferenceRevision>().Property(x => x.Reason).HasMaxLength(200);
        modelBuilder.Entity<ContainerShipmentReferenceRevision>().Property(x => x.ShipmentMode).HasMaxLength(10);
        modelBuilder.Entity<ContainerShipmentReferenceRevision>().Property(x => x.ShippingOrderNo).HasMaxLength(50);
        modelBuilder.Entity<ContainerShipmentReferenceRevision>().Property(x => x.BillOfLadingNo).HasMaxLength(50);
        modelBuilder.Entity<ContainerShipmentReferenceRevision>().Property(x => x.CarrierName).HasMaxLength(200);
        modelBuilder.Entity<ContainerShipmentReferenceRevision>().Property(x => x.ForwarderName).HasMaxLength(200);
        modelBuilder.Entity<ContainerShipmentReferenceRevision>().Property(x => x.DeparturePort).HasMaxLength(100);
        modelBuilder.Entity<ContainerShipmentReferenceRevision>().Property(x => x.TransitPort).HasMaxLength(100);
        modelBuilder.Entity<ContainerShipmentReferenceRevision>().Property(x => x.DestinationPort).HasMaxLength(100);
        modelBuilder.Entity<ContainerShipmentReferenceRevision>().Property(x => x.TruckerName).HasMaxLength(200);
        modelBuilder.Entity<ContainerShipmentReferenceRevision>().Property(x => x.CustomsBrokerName)
            .HasMaxLength(100);
        modelBuilder.Entity<ContainerShipmentReferenceRevision>().Property(x => x.Remark).HasMaxLength(500);

        modelBuilder.Entity<ContainerShipmentReferenceRevision>()
            .HasIndex(x => new { x.ContainerShipmentReferenceId, x.RevisionNo })
            .IsUnique().HasDatabaseName("UX_ContainerShipmentReferenceRevisions_Reference_Revision")
            .HasFilter("IsDeleted = 0");

        // 刻意不建任何外键、也不建导航属性（与 ERP-045 / ERP-047 / ERP-049 / ERP-053 / ERP-055 一致）：
        // 源记录可能被软删除、报关行字典项可能被停用 / 删除 —— 本模块只保存服务端快照，
        // 历史出运证据必须始终可读，也不参与源记录与任何下游单据的计算。
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
