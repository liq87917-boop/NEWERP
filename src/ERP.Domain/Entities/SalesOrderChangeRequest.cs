using ERP.Domain.Common;
using ERP.Domain.Enums;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ERP.Domain.Entities;

/// <summary>
/// 销售订单变更申请（ERP-047）：**只登记拟议变更的不可变登记册**。
/// <para>定位：把「某张既有销售订单希望改成什么」登记为一条带来源快照的申请，供人工审阅与留痕；
/// 申请本身<strong>不是</strong>销售订单的新版本，<strong>不</strong>审核、<strong>不</strong>套用、
/// <strong>不</strong>改写来源订单。</para>
/// <para>边界（重要，代码与界面同源）：</para>
/// <list type="bullet">
/// <item>创建 / 编辑 / 提交 / 取消申请都<strong>不</strong>改写来源销售订单的主表与明细（来源只读快照），
/// 也<strong>不</strong>改写报价单、销售出库、装柜 / 出运、收款与发票、佣金、库存与库存成本、
/// 单证中心与财务记录；</item>
/// <item>拟议主表值 <see cref="ProposedTotalAmount"/> / <see cref="ProposedDepositAmount"/> 由服务端按
/// **销售订单唯一权威算法**（总额 = Σ 明细数量 × 单价；定金 = 总额 × 定金比例%）重算，
/// 绝不信任客户端合计，也不引入第二套算法；</item>
/// <item>状态只有 草稿 → 已提交 → 已取消（常量见 <c>SalesOrderChangeRequestRules</c>）：
/// 已提交申请冻结拟议快照并记录提交时间，之后**不可编辑**；取消必须填写原因且保留原始与拟议证据；
/// 本表<strong>没有</strong>「已批准 / 已套用」状态，也不提供硬删除；</item>
/// <item>来源订单在快照之后发生变化时，读取侧只**显式提示**「来源已变化」并给出差异依据，
/// <strong>不</strong>覆盖、<strong>不</strong>合并、<strong>不</strong>静默刷新拟议值；</item>
/// <item>来源订单只保存服务端权威写入的快照（<see cref="SalesOrderNo"/> 等 Source* 列），
/// **刻意不建**到 <c>SalesOrders</c> 的数据库外键：来源订单改名 / 软删除都不影响历史申请可读；</item>
/// <item>本模块不执行任何生产库 DDL / 回填：建表与索引只由 <c>SchemaUpgrader</c> 幂等补齐，
/// 既有销售订单在没有变更申请时行为与历史完全一致。</item>
/// </list>
/// </summary>
public class SalesOrderChangeRequest : BaseEntity
{
    /// <summary>申请单号（服务端按字轨 <c>DocumentType.SalesOrderChangeRequest</c> 生成）</summary>
    [Required, MaxLength(50)]
    public string RequestNo { get; set; } = string.Empty;

    /// <summary>来源销售订单 Id（必须指向存在且未删除的权威销售订单；本表刻意不建外键）</summary>
    public long SalesOrderId { get; set; }

    /// <summary>来源销售订单号快照（服务端权威写入；来源改名后历史申请保持登记当时口径）</summary>
    [MaxLength(50)]
    public string SalesOrderNo { get; set; } = string.Empty;

    /// <summary>来源订单状态快照（登记当时的 <see cref="DocumentStatus"/>；只用于「来源是否已变化」对照）</summary>
    public int SourceStatus { get; set; }

    /// <summary>来源订单最后更新时间快照（登记当时取 <c>UpdatedAt ?? CreatedAt</c>）</summary>
    public DateTime SourceUpdatedAt { get; set; }

    /// <summary>来源订单明细确定性签名快照（登记当时按「行数 + 逐行数量 × 单价」拼装，用于来源变化检测）</summary>
    [MaxLength(500)]
    public string SourceDetailSignature { get; set; } = string.Empty;

    /// <summary>来源快照标记（服务端权威写入的确定性文本：单号 / 状态 / 更新时间 / 明细摘要）</summary>
    [MaxLength(300)]
    public string SourceSnapshotMarker { get; set; } = string.Empty;

    /// <summary>变更原因（必填、有界；说明为什么要变更，作为人工审阅依据）</summary>
    [Required, MaxLength(500)]
    public string Reason { get; set; } = string.Empty;

    // ============ 来源订单主表与明细快照（服务端在登记时冻结，草稿编辑不得改写） ============

    /// <summary>来源订单日期快照</summary>
    public DateTime SourceOrderDate { get; set; }

    /// <summary>来源订单客户 Id 快照</summary>
    public long SourceCustomerId { get; set; }

    /// <summary>来源订单业务员 Id 快照</summary>
    public long? SourceSalesmanId { get; set; }

    /// <summary>来源订单币种快照</summary>
    public Currency SourceCurrency { get; set; } = Currency.USD;

    /// <summary>来源订单汇率快照</summary>
    public decimal SourceExchangeRate { get; set; }

    /// <summary>来源订单总额快照（来源当时的权威合计）</summary>
    public decimal SourceTotalAmount { get; set; }

    /// <summary>来源订单定金比例快照（%）</summary>
    public decimal SourceDepositRatio { get; set; }

    /// <summary>来源订单定金金额快照</summary>
    public decimal SourceDepositAmount { get; set; }

    /// <summary>来源订单付款条件快照</summary>
    [MaxLength(200)]
    public string SourcePaymentTerms { get; set; } = string.Empty;

    /// <summary>来源订单交货日期快照</summary>
    public DateTime? SourceDeliveryDate { get; set; }

    /// <summary>来源订单运输方式快照</summary>
    [MaxLength(100)]
    public string SourceShippingMethod { get; set; } = string.Empty;

    /// <summary>来源订单目的港 Id 快照</summary>
    public long? SourcePortId { get; set; }

    /// <summary>来源订单备注快照</summary>
    [MaxLength(500)]
    public string SourceRemark { get; set; } = string.Empty;

    /// <summary>来源订单客户 PO 号快照</summary>
    [MaxLength(50)]
    public string SourceCustomerPoNo { get; set; } = string.Empty;

    /// <summary>来源订单外销合同号快照</summary>
    [MaxLength(50)]
    public string SourceContractNo { get; set; } = string.Empty;

    /// <summary>来源订单价格条款快照</summary>
    [MaxLength(50)]
    public string SourceTradeTerms { get; set; } = string.Empty;

    /// <summary>来源订单目的港文本快照</summary>
    [MaxLength(100)]
    public string SourceDestinationPort { get; set; } = string.Empty;

    /// <summary>来源订单收货人快照</summary>
    [MaxLength(300)]
    public string SourceConsignee { get; set; } = string.Empty;

    /// <summary>来源订单通知人快照</summary>
    [MaxLength(300)]
    public string SourceNotifyParty { get; set; } = string.Empty;

    /// <summary>来源订单唛头快照</summary>
    [MaxLength(500)]
    public string SourceShippingMarks { get; set; } = string.Empty;

    /// <summary>来源订单「来源报价单号」快照（追溯链只读保留，拟议不修改追溯字段）</summary>
    [MaxLength(50)]
    public string SourceQuotationNoSnapshot { get; set; } = string.Empty;

    /// <summary>来源订单「来源 PI 号」快照（追溯链只读保留，拟议不修改追溯字段）</summary>
    [MaxLength(50)]
    public string SourcePiNoSnapshot { get; set; } = string.Empty;

    /// <summary>来源订单出口方式快照</summary>
    [MaxLength(20)]
    public string SourceExportMode { get; set; } = string.Empty;

    /// <summary>来源订单佣金比例快照（%）</summary>
    public decimal SourceCommissionRatio { get; set; }

    /// <summary>来源订单业务性质快照</summary>
    [MaxLength(20)]
    public string SourceBusinessNature { get; set; } = string.Empty;

    /// <summary>来源订单是否分批出货快照</summary>
    public bool SourceSplitShipment { get; set; }

    /// <summary>来源订单验货要求快照</summary>
    [MaxLength(500)]
    public string SourceInspectionRequirement { get; set; } = string.Empty;

    /// <summary>来源订单包装要求快照</summary>
    [MaxLength(500)]
    public string SourcePackagingRequirement { get; set; } = string.Empty;

    // ============ 拟议主表值（草稿期可编辑；提交后冻结） ============

    /// <summary>拟议订单日期</summary>
    public DateTime ProposedOrderDate { get; set; }

    /// <summary>拟议客户 Id</summary>
    public long ProposedCustomerId { get; set; }

    /// <summary>拟议业务员 Id</summary>
    public long? ProposedSalesmanId { get; set; }

    /// <summary>拟议币种</summary>
    public Currency ProposedCurrency { get; set; } = Currency.USD;

    /// <summary>拟议汇率</summary>
    public decimal ProposedExchangeRate { get; set; } = 1;

    /// <summary>拟议总额（服务端按销售订单唯一权威算法重算，不使用客户端合计）</summary>
    public decimal ProposedTotalAmount { get; set; }

    /// <summary>拟议定金比例（%，0~100；越界拒绝）</summary>
    public decimal ProposedDepositRatio { get; set; }

    /// <summary>拟议定金金额（服务端按销售订单唯一权威算法重算）</summary>
    public decimal ProposedDepositAmount { get; set; }

    /// <summary>拟议付款条件</summary>
    [MaxLength(200)]
    public string ProposedPaymentTerms { get; set; } = string.Empty;

    /// <summary>拟议交货日期</summary>
    public DateTime? ProposedDeliveryDate { get; set; }

    /// <summary>拟议运输方式</summary>
    [MaxLength(100)]
    public string ProposedShippingMethod { get; set; } = string.Empty;

    /// <summary>拟议目的港 Id</summary>
    public long? ProposedPortId { get; set; }

    /// <summary>拟议备注</summary>
    [MaxLength(500)]
    public string ProposedRemark { get; set; } = string.Empty;

    /// <summary>拟议客户 PO 号</summary>
    [MaxLength(50)]
    public string ProposedCustomerPoNo { get; set; } = string.Empty;

    /// <summary>拟议外销合同号</summary>
    [MaxLength(50)]
    public string ProposedContractNo { get; set; } = string.Empty;

    /// <summary>拟议价格条款</summary>
    [MaxLength(50)]
    public string ProposedTradeTerms { get; set; } = string.Empty;

    /// <summary>拟议目的港文本</summary>
    [MaxLength(100)]
    public string ProposedDestinationPort { get; set; } = string.Empty;

    /// <summary>拟议收货人</summary>
    [MaxLength(300)]
    public string ProposedConsignee { get; set; } = string.Empty;

    /// <summary>拟议通知人</summary>
    [MaxLength(300)]
    public string ProposedNotifyParty { get; set; } = string.Empty;

    /// <summary>拟议唛头</summary>
    [MaxLength(500)]
    public string ProposedShippingMarks { get; set; } = string.Empty;

    /// <summary>拟议出口方式</summary>
    [MaxLength(20)]
    public string ProposedExportMode { get; set; } = string.Empty;

    /// <summary>拟议佣金比例（%，0~100；越界拒绝，与来源订单同口径）</summary>
    public decimal ProposedCommissionRatio { get; set; }

    /// <summary>拟议业务性质</summary>
    [MaxLength(20)]
    public string ProposedBusinessNature { get; set; } = string.Empty;

    /// <summary>拟议是否分批出货</summary>
    public bool ProposedSplitShipment { get; set; }

    /// <summary>拟议验货要求</summary>
    [MaxLength(500)]
    public string ProposedInspectionRequirement { get; set; } = string.Empty;

    /// <summary>拟议包装要求</summary>
    [MaxLength(500)]
    public string ProposedPackagingRequirement { get; set; } = string.Empty;

    // ============ 生命周期留痕 ============

    /// <summary>状态（0=草稿，1=已提交，2=已取消；本表没有「已批准 / 已套用」）</summary>
    public int Status { get; set; }

    /// <summary>提交时间（服务端权威写入；提交后拟议快照冻结、申请不可编辑）</summary>
    public DateTime? SubmittedAt { get; set; }

    /// <summary>取消时间</summary>
    public DateTime? CancelledAt { get; set; }

    /// <summary>取消原因（必填：取消保留原始与拟议证据，必须记录原因而不是静默删除）</summary>
    [MaxLength(500)]
    public string CancelledReason { get; set; } = string.Empty;

    /// <summary>拟议明细集合（来源行只读快照 + 逐行拟议值）</summary>
    public List<SalesOrderChangeRequestDetail> Details { get; set; } = new();

    // ============ 读取侧标注（**非持久化列**，读取时由服务端计算，不落库） ============

    /// <summary>状态文案（草稿 / 已提交 / 已取消；**非持久化列**）</summary>
    [NotMapped]
    public string StatusText { get; set; } = string.Empty;

    /// <summary>来源订单当前是否仍可用（存在且未删除；**非持久化列**）</summary>
    [NotMapped]
    public bool SourceAvailable { get; set; }

    /// <summary>来源订单可用性文案（不可用时照实说明，历史申请照常可读；**非持久化列**）</summary>
    [NotMapped]
    public string SourceAvailabilityText { get; set; } = string.Empty;

    /// <summary>来源订单在快照之后是否已变化（**非持久化列**；只提示，不覆盖拟议值）</summary>
    [NotMapped]
    public bool SourceChanged { get; set; }

    /// <summary>来源变化说明文案（**非持久化列**）</summary>
    [NotMapped]
    public string SourceChangedText { get; set; } = string.Empty;

    /// <summary>来源变化差异依据（逐项列出对比字段；**非持久化列**）</summary>
    [NotMapped]
    public string SourceChangeDetailText { get; set; } = string.Empty;

    /// <summary>边界声明（**非持久化列**：申请不是已批准 / 已套用的变更）</summary>
    [NotMapped]
    public string BoundaryText { get; set; } = string.Empty;
}

/// <summary>
/// 销售订单变更申请明细（ERP-047）：一行 = 「来源订单明细行快照 + 该行的拟议值」。
/// <para>边界：</para>
/// <list type="bullet">
/// <item><see cref="HasSourceLine"/> = false 表示这是一行**新增**（来源订单没有对应行）；
/// <see cref="ProposedRemoved"/> = true 表示来源行在拟议中**被移除**（来源快照仍完整保留）；</item>
/// <item>Source* 列在登记时冻结，草稿编辑只改 Proposed* 列，来源证据永不被覆盖；</item>
/// <item><see cref="ProposedAmount"/> 由服务端按「数量 × 单价」重算（与销售订单同口径），不接受客户端金额；</item>
/// <item>本表<strong>不</strong>写回 <c>SalesOrderDetails</c>，也不参与来源订单的合计、库存、出运与财务计算。</item>
/// </list>
/// </summary>
public class SalesOrderChangeRequestDetail : BaseEntity
{
    /// <summary>所属变更申请 Id</summary>
    public long ChangeRequestId { get; set; }

    /// <summary>行号（1 起；来源行沿用来源顺序，新增行排在来源行之后，保证对照稳定）</summary>
    public int LineNo { get; set; }

    /// <summary>是否存在对应的来源订单明细行（false = 拟议新增行）</summary>
    public bool HasSourceLine { get; set; }

    /// <summary>来源明细行商品 Id 快照</summary>
    public long SourceProductId { get; set; }

    /// <summary>来源明细行商品名称快照</summary>
    [MaxLength(200)]
    public string SourceProductName { get; set; } = string.Empty;

    /// <summary>来源明细行规格快照</summary>
    [MaxLength(200)]
    public string SourceSpec { get; set; } = string.Empty;

    /// <summary>来源明细行单位快照</summary>
    [MaxLength(20)]
    public string SourceUnit { get; set; } = string.Empty;

    /// <summary>来源明细行数量快照</summary>
    public decimal SourceQuantity { get; set; }

    /// <summary>来源明细行单价快照</summary>
    public decimal SourceUnitPrice { get; set; }

    /// <summary>来源明细行金额快照（来源当时的数量 × 单价）</summary>
    public decimal SourceAmount { get; set; }

    /// <summary>来源明细行交货日期快照</summary>
    public DateTime? SourceDeliveryDate { get; set; }

    /// <summary>来源明细行备注快照</summary>
    [MaxLength(500)]
    public string SourceRemark { get; set; } = string.Empty;

    /// <summary>拟议移除该来源行（来源快照保留；新增行恒为 false）</summary>
    public bool ProposedRemoved { get; set; }

    /// <summary>拟议商品 Id</summary>
    public long ProposedProductId { get; set; }

    /// <summary>拟议商品名称</summary>
    [MaxLength(200)]
    public string ProposedProductName { get; set; } = string.Empty;

    /// <summary>拟议规格</summary>
    [MaxLength(200)]
    public string ProposedSpec { get; set; } = string.Empty;

    /// <summary>拟议单位</summary>
    [MaxLength(20)]
    public string ProposedUnit { get; set; } = string.Empty;

    /// <summary>拟议数量（必须 > 0，与销售订单同口径）</summary>
    public decimal ProposedQuantity { get; set; }

    /// <summary>拟议单价（不得为负，与销售订单同口径）</summary>
    public decimal ProposedUnitPrice { get; set; }

    /// <summary>拟议金额（服务端按数量 × 单价重算）</summary>
    public decimal ProposedAmount { get; set; }

    /// <summary>拟议交货日期</summary>
    public DateTime? ProposedDeliveryDate { get; set; }

    /// <summary>拟议备注</summary>
    [MaxLength(500)]
    public string ProposedRemark { get; set; } = string.Empty;

    /// <summary>对照文案（新增 / 移除 / 已修改 / 未修改；**非持久化列**）</summary>
    [NotMapped]
    public string ComparisonText { get; set; } = string.Empty;
}
