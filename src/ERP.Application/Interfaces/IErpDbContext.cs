using ERP.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace ERP.Application.Interfaces;

/// <summary>
/// 数据访问抽象接口：将应用层与具体 DbContext 解耦，便于单元测试与仓储隔离
/// </summary>
public interface IErpDbContext
{
    /// <summary>
    /// 数据库门面（事务 / 连接 / 提供程序信息）。ERP-052 用途：单证生成时把「单证表头 + 明细行快照」
    /// 放在**同一个事务**内写入，任何一步失败都不留下半成品单证。
    /// <para>由 <c>DbContext</c> 基类直接实现，无需各实现类另行编写；内存库等不支持事务的提供程序由
    /// EF 侧按「无事务」执行（仅记录警告），不影响单元测试的可读性。</para>
    /// </summary>
    DatabaseFacade Database { get; }

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

    // ============ 供应商采购发票登记（ERP-043：运营证据台账 + 可选采购订单关联） ============

    /// <summary>
    /// 供应商采购发票（ERP-043：普票 / 专票证据台账；不是应付账款台账、不是税务申报系统、不是付款授权机制）
    /// </summary>
    DbSet<PurchaseInvoice> PurchaseInvoices { get; }

    /// <summary>供应商采购发票 → 采购订单 关联（分摊）行（ERP-043；只保存订单快照与关联金额，不建到订单的外键）</summary>
    DbSet<PurchaseInvoiceAllocation> PurchaseInvoiceAllocations { get; }

    // ============ 业务单据附件引用登记（ERP-045：仅元数据的附件引用册） ============

    /// <summary>
    /// 业务单据附件引用（ERP-045：为销售订单 / 采购订单 / 装柜清单 / 出口单证登记**仅元数据**的附件引用；
    /// 不上传 / 下载 / 预览 / 抓取任何对象，不建到父单据的外键）
    /// </summary>
    DbSet<DocumentAttachmentReference> DocumentAttachmentReferences { get; }

    // ============ 销售订单变更申请登记（ERP-047：只登记拟议变更的不可变登记册） ============

    /// <summary>
    /// 销售订单变更申请（ERP-047）：保存「来源销售订单快照 + 拟议主/明细值 + 提交 / 取消留痕」，
    /// <strong>不</strong>审核、<strong>不</strong>套用、<strong>不</strong>改写销售订单与任何下游记录，也不建到销售订单的外键
    /// </summary>
    DbSet<SalesOrderChangeRequest> SalesOrderChangeRequests { get; }

    /// <summary>销售订单变更申请明细（拟议明细行；已提交 / 已取消申请一律只读，不做硬删除）</summary>
    DbSet<SalesOrderChangeRequestDetail> SalesOrderChangeRequestDetails { get; }

    // ============ 供应商付款引用登记（ERP-049：付款单 → 采购订单 的引用证据行） ============

    /// <summary>
    /// 供应商付款单 → 采购订单 付款引用（分摊）证据行（ERP-049）：只登记「某张既有付款单把多少钱指向了哪几张
    /// 既有采购订单」，保留付款单 / 供应商 / 订单快照；不是付款凭证、不是应付账款核销、不是发票核销、
    /// 不是税务判断、也不是供应商余额，且不改写付款单与采购订单
    /// </summary>
    DbSet<SupplierPaymentAllocation> SupplierPaymentAllocations { get; }

    // ============ 供应商付款 → 采购发票 引用登记（ERP-066：付款单 → 已登记采购发票 的引用证据行） ============

    /// <summary>
    /// 供应商付款单 → 供应商采购发票 付款引用（分摊）证据行（ERP-066）：只登记「某张既有付款单把多少钱指向了
    /// 哪几张既有已登记（未作废）的采购发票」，保留付款单 / 供应商 / 发票快照与登记人；
    /// 不是付款凭证、不是应付账款核销、不是发票认证 / 抵扣、不是税务申报、也不是供应商余额，
    /// 且不改写付款单、发票与采购订单，也不与 ERP-049 的采购订单引用金额相加
    /// </summary>
    DbSet<SupplierPaymentInvoiceAllocation> SupplierPaymentInvoiceAllocations { get; }

    // ============ 客户收款引用登记（ERP-053：收款单 → 销售订单 的引用证据行） ============

    /// <summary>
    /// 客户收款单 → 销售订单 收款引用（分摊）证据行（ERP-053）：只登记「某张既有收款单把多少钱指向了哪几张
    /// 既有销售订单」，保留收款单 / 客户 / 订单快照；不是到账凭证、不是应收账款台账或余额、不是货款核销、
    /// 不是客户对账单、不是税务判断，也不构成债务清偿，且不改写收款单与销售订单。
    /// <para>ERP-032 / ERP-046 的权威口径里收款单只记录客户、没有订单级持久化引用，因此本表是**唯一**的
    /// 收款引用登记模型（不在收款单 / 销售订单上加列，也不建第二套链接表）。</para>
    /// </summary>
    DbSet<CustomerReceiptAllocation> CustomerReceiptAllocations { get; }

    // ============ 客户销项发票证据登记（ERP-055：普票 / 专票 / 出口发票证据台账 + 可选销售订单分摊） ============

    /// <summary>
    /// 客户销项发票证据（ERP-055）：登记普通发票 / 增值税专用发票 / 出口发票的运营证据，保留客户与
    /// 可选单证交叉引用快照；不是开票系统、不是税务申报、不是应收账款台账或余额、不是收款核销，
    /// 也不构成开票 / 纳税 / 债权结论，且不调用任何开票 / 税务服务、不改写销售订单与客户数据
    /// </summary>
    DbSet<CustomerSalesInvoiceEvidence> CustomerSalesInvoiceEvidences { get; }

    /// <summary>
    /// 客户销项发票证据 → 销售订单 的分摊证据行（ERP-055）：一张发票可把含税总额全部或部分分摊到一张或多张
    /// 既有、未取消且客户与币种一致的销售订单，保留订单快照；不是已开票 / 已收款 / 已核销 / 应收余额结论，
    /// 也不参与销售订单的出货、收款引用与库存计算
    /// </summary>
    DbSet<CustomerSalesInvoiceAllocation> CustomerSalesInvoiceAllocations { get; }

    // ============ 装柜出运引用登记（ERP-057：显式源记录关联 + 出运证据快照 + 修订留痕） ============

    /// <summary>
    /// 装柜出运引用证据（ERP-057）：显式指向订柜信息 / 预装柜单 / 装柜清单之一（按类型 + Id，绝不按柜号 /
    /// 单号等自由文本匹配），登记用户录入的出运证据快照；同一源记录最多 1 条有效引用（过滤唯一索引兜底）；
    /// 不是承运人 / 海关 / 货代确认，也不改写源记录与任何下游单据
    /// </summary>
    DbSet<ContainerShipmentReference> ContainerShipmentReferences { get; }

    /// <summary>
    /// 出运引用修订留痕（ERP-057）：只追加的「修订前原值」快照，记录修订号 / 时间 / 原因，
    /// 保证历史证据不会被静默改写；不提供修改与删除接口
    /// </summary>
    DbSet<ContainerShipmentReferenceRevision> ContainerShipmentReferenceRevisions { get; }

    /// <summary>
    /// 装柜出运里程碑证据（ERP-058）：挂在 ERP-057 出运引用记录**之下**的只追加操作性事件留痕
    /// （实际开船 / 实际到港 / 查验 / 放行），按显式父出运引用 Id 关联，绝不按柜号 / S/O / B/L
    /// 等自由文本匹配；同一父记录 + 事件类型 + 事件时间不允许重复有效登记，只提供显式作废
    /// （必填原因，保留原始证据），不是承运人 / 海关确认，也不推进任何业务单据
    /// </summary>
    DbSet<ContainerShipmentMilestone> ContainerShipmentMilestones { get; }

    // ============ 业务单据附件内容证据（ERP-061：仓库内唯一的附件二进制内容证据册） ============

    /// <summary>
    /// 附件内容证据（ERP-061）：用户上传的 PDF / PNG / JPEG 证据 + 服务端权威元数据（净化文件名快照 /
    /// 媒体类型 / 字节长度 / SHA-256 摘要 / 上传人 / 登记时间）+ 服务端生成的不透明存储键；内容经唯一接缝
    /// <see cref="IAttachmentContentStore"/> 托管（开发 / 测试只启用隔离的非生产本地存储，生产 OSS 未激活），
    /// 只提供显式作废（必填原因，保留原始元数据与内容），<strong>不</strong>建到归属单据的外键
    /// </summary>
    DbSet<AttachmentEvidence> AttachmentEvidences { get; }

    // ============ 代理服务费协议证据登记（ERP-069：客户代理服务费的仓库内商业条款证据） ============

    /// <summary>
    /// 代理服务费协议证据（ERP-069）：把授权用户**显式提供**的客户代理服务费商业条款（协议号 / 权威客户 /
    /// 生效日期区间 / 币种 / 披露的计费方式与费率或固定金额 / 有界计费依据说明 / 有界备注）登记为可审计证据；
    /// 不是税务发票、不是会计凭证或记账分录、不是付款授权或资金指令、不是法律意见，也不是服务已交付 /
    /// 已收付款的证明；系统不提供任何「不披露 / 账外 / 隐匿佣金」字段或流程，费用条款也不从业务员提成设置
    /// （<c>SalesCommissionRate</c>）、客户 / 供应商主数据比例、历史订单、自由文本或金额相似度推断
    /// </summary>
    DbSet<AgencyServiceFeeAgreement> AgencyServiceFeeAgreements { get; }

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

    /// <summary>单证明细行快照（ERP-051：商业发票 / 装箱单 的商品明细证据行）</summary>
    DbSet<TradeDocumentItem> TradeDocumentItems { get; }

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
