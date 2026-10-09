using ERP.Domain.Common;
using ERP.Domain.Enums;
using System.ComponentModel.DataAnnotations;

namespace ERP.Domain.Entities;

/// <summary>
/// 采购订单主表
/// </summary>
public class PurchaseOrder : BaseEntity
{
    /// <summary>
    /// 采购单号（服务端权威生成：普通新增 / 修改入口一律在授权与全部业务校验通过之后由
    /// <c>IDocumentNumberService</c> 预约覆盖，调用方提交值不参与持久化）。
    /// <para>ERP-429：这里刻意<strong>不加</strong> <c>[Required]</c>。手工采购表单不提交单号（服务端自有），
    /// MVC 对非空引用类型属性本就会推断一个 <c>AllowEmptyStrings = true</c> 的隐式必填（允许空串），
    /// 而显式 <c>[Required]</c>（默认 <c>AllowEmptyStrings = false</c>）会把「省略单号」在模型绑定 /
    /// 模型校验阶段直接拒绝为 HTTP 400，服务端权威单号根本没有机会生成。移除显式特性后：
    /// 省略 / 空串照常绑定，服务端仍按唯一权威口径覆盖单号（在既有实时授权、条款与实时主数据校验之后）；
    /// 数据库列仍是 NOT NULL（非空 CLR 引用类型），长度仍受 <see cref="MaxLengthAttribute"/>(50) 约束，
    /// 非法请求 / 越权请求的校验顺序与拒绝口径不变。</para>
    /// </summary>
    [MaxLength(50)]
    public string OrderNo { get; set; } = string.Empty;

    /// <summary>订单日期</summary>
    public DateTime OrderDate { get; set; } = DateTime.Today;

    /// <summary>供应商 Id</summary>
    public long SupplierId { get; set; }

    /// <summary>采购员 Id（员工）</summary>
    public long? BuyerId { get; set; }

    /// <summary>币种</summary>
    public Currency Currency { get; set; } = Currency.CNY;

    /// <summary>汇率</summary>
    public decimal ExchangeRate { get; set; } = 1;

    /// <summary>订单总额</summary>
    public decimal TotalAmount { get; set; }

    /// <summary>付款条件</summary>
    [MaxLength(200)]
    public string PaymentTerms { get; set; } = string.Empty;

    /// <summary>交货日期</summary>
    public DateTime? DeliveryDate { get; set; }

    /// <summary>起运港 Id</summary>
    public long? PortId { get; set; }

    // ============ 采购执行与结算追溯（ERP-008 新增：代理出口归属与采购执行进度，全部可空/带默认值以兼容历史单据） ============

    /// <summary>归属客户 Id（代理出口：该采购单为哪个客户的销售订单备货）</summary>
    public long? OwningCustomerId { get; set; }

    /// <summary>归属客户名称（冗余，列表与报表免关联）</summary>
    [MaxLength(200)]
    public string OwningCustomerName { get; set; } = string.Empty;

    /// <summary>归属销售订单 Id（采购单 → 销售订单追溯）</summary>
    public long? OwningSalesOrderId { get; set; }

    /// <summary>归属销售订单号（冗余）</summary>
    [MaxLength(50)]
    public string OwningSalesOrderNo { get; set; } = string.Empty;

    /// <summary>是否代垫货款（代理出口时由公司代客户垫付供应商货款）</summary>
    public bool AdvanceOnBehalf { get; set; }

    /// <summary>供应商确认交期（与订单交货日期区分：订单交期为要求，此处为供应商回签）</summary>
    public DateTime? SupplierConfirmedDate { get; set; }

    /// <summary>税率（%，0~100；含税报价时用于价税分离）</summary>
    public decimal TaxRate { get; set; }

    /// <summary>是否含税单价（true = 单价已含税）</summary>
    public bool TaxIncluded { get; set; }

    /// <summary>到货进度（未到货 / 部分到货 / 已到货）</summary>
    [MaxLength(50)]
    public string ArrivalProgress { get; set; } = string.Empty;

    /// <summary>验货状态（未验货 / 验货中 / 合格 / 不合格 / 免验）</summary>
    [MaxLength(30)]
    public string QcStatus { get; set; } = string.Empty;

    /// <summary>采购合同号</summary>
    [MaxLength(50)]
    public string ContractNo { get; set; } = string.Empty;

    /// <summary>结算进度（未结算 / 部分结算 / 已结算）</summary>
    [MaxLength(50)]
    public string SettlementProgress { get; set; } = string.Empty;

    /// <summary>单据状态</summary>
    public DocumentStatus Status { get; set; } = DocumentStatus.Pending;

    /// <summary>备注</summary>
    [MaxLength(500)]
    public string Remark { get; set; } = string.Empty;

    /// <summary>明细集合</summary>
    public List<PurchaseOrderDetail> Details { get; set; } = new();
}

/// <summary>
/// 采购订单明细
/// </summary>
public class PurchaseOrderDetail : BaseEntity
{
    /// <summary>采购订单 Id</summary>
    public long PurchaseOrderId { get; set; }

    /// <summary>商品 Id</summary>
    public long ProductId { get; set; }

    /// <summary>商品名称（冗余）</summary>
    [MaxLength(200)]
    public string ProductName { get; set; } = string.Empty;

    /// <summary>规格</summary>
    [MaxLength(200)]
    public string Spec { get; set; } = string.Empty;

    /// <summary>数量</summary>
    public decimal Quantity { get; set; }

    /// <summary>单位</summary>
    [MaxLength(20)]
    public string Unit { get; set; } = string.Empty;

    /// <summary>单价</summary>
    public decimal UnitPrice { get; set; }

    /// <summary>金额</summary>
    public decimal Amount { get; set; }

    /// <summary>交货日期</summary>
    public DateTime? DeliveryDate { get; set; }

    /// <summary>备注</summary>
    [MaxLength(500)]
    public string Remark { get; set; } = string.Empty;
}
