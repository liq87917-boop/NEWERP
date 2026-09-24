using ERP.Domain.Common;
using ERP.Domain.Enums;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ERP.Domain.Entities;

/// <summary>
/// 收货计划
/// </summary>
public class ContainerReceivingPlan : BaseEntity
{
    /// <summary>计划单号</summary>
    [Required, MaxLength(50)]
    public string PlanNo { get; set; } = string.Empty;

    /// <summary>计划日期</summary>
    public DateTime PlanDate { get; set; } = DateTime.Today;

    /// <summary>供应商 Id</summary>
    public long SupplierId { get; set; }

    /// <summary>订柜单号（关联订柜信息）</summary>
    [MaxLength(50)]
    public string BookingNo { get; set; } = string.Empty;

    /// <summary>柜型</summary>
    public ContainerType ContainerType { get; set; } = ContainerType.GP40;

    /// <summary>柜号</summary>
    [MaxLength(50)]
    public string ContainerNo { get; set; } = string.Empty;

    /// <summary>预计到货日期</summary>
    public DateTime? ExpectedArrivalDate { get; set; }

    /// <summary>目的港 Id</summary>
    public long? PortId { get; set; }

    /// <summary>目的地</summary>
    [MaxLength(200)]
    public string Destination { get; set; } = string.Empty;

    /// <summary>总件数</summary>
    public decimal TotalQuantity { get; set; }

    /// <summary>单据状态</summary>
    public DocumentStatus Status { get; set; } = DocumentStatus.Pending;

    /// <summary>备注</summary>
    [MaxLength(500)]
    public string Remark { get; set; } = string.Empty;
}

/// <summary>
/// 订柜信息
/// </summary>
public class ContainerBooking : BaseEntity
{
    /// <summary>订柜单号</summary>
    [Required, MaxLength(50)]
    public string BookingNo { get; set; } = string.Empty;

    /// <summary>订柜日期</summary>
    public DateTime BookingDate { get; set; } = DateTime.Today;

    /// <summary>客户 Id</summary>
    public long CustomerId { get; set; }

    /// <summary>供应商 Id</summary>
    public long? SupplierId { get; set; }

    /// <summary>柜型</summary>
    public ContainerType ContainerType { get; set; } = ContainerType.GP40;

    /// <summary>船公司/物流公司</summary>
    [MaxLength(200)]
    public string ShippingCompany { get; set; } = string.Empty;

    /// <summary>航次</summary>
    [MaxLength(100)]
    public string VoyageNo { get; set; } = string.Empty;

    /// <summary>开船日期</summary>
    public DateTime? SailingDate { get; set; }

    /// <summary>起运港</summary>
    [MaxLength(100)]
    public string DeparturePort { get; set; } = string.Empty;

    /// <summary>目的港</summary>
    [MaxLength(100)]
    public string DestinationPort { get; set; } = string.Empty;

    // ============ ERP-040：外贸与物流跟踪字段（订柜信息 = 权威记录） ============
    // 统一口径：
    //   1. 文本列默认空串（NULL / 空串同义）= 「未知 / 未填写」，服务端不写「无」「待定」这类占位值；
    //   2. 日期列一律可空，**绝不**由其他字段或自由文本推断（例如不因「已开船」就猜 ETD）；
    //   3. 订柜信息是本套跟踪值的唯一权威记录：预装柜单 / 装柜清单只按**持久化引用**只读回显，
    //      不复制、不各自维护第二份跟踪值，也不按柜号等自由文本匹配；
    //   4. 整套字段都只作记录，不写费用 / 单证 / 库存，也不调用船公司、海关、货代等外部系统。

    /// <summary>出运方式（<c>LCL</c> 拼箱 / <c>FCL</c> 整箱；空串 = 未指定）</summary>
    [MaxLength(10)]
    public string ShipmentMode { get; set; } = string.Empty;

    /// <summary>提单号（B/L）</summary>
    [MaxLength(50)]
    public string BillOfLadingNo { get; set; } = string.Empty;

    /// <summary>订舱号 / 托运单号（S/O）</summary>
    [MaxLength(50)]
    public string ShippingOrderNo { get; set; } = string.Empty;

    /// <summary>中转港（可空 = 未填写）</summary>
    [MaxLength(100)]
    public string TransitPort { get; set; } = string.Empty;

    /// <summary>预计开船日（ETD，可空 = 未知）</summary>
    public DateTime? Etd { get; set; }

    /// <summary>预计到港日（ETA，可空 = 未知）</summary>
    public DateTime? Eta { get; set; }

    /// <summary>实际开船日（ATD，可空 = 未知 / 尚未开船）</summary>
    public DateTime? Atd { get; set; }

    /// <summary>实际到港日（ATA，可空 = 未知 / 尚未到港）</summary>
    public DateTime? Ata { get; set; }

    /// <summary>拖车 / 集卡公司名称（自由文本，仅作联系记录）</summary>
    [MaxLength(200)]
    public string TruckerName { get; set; } = string.Empty;

    /// <summary>
    /// 报关行 Id（引用 <see cref="BaseOtherInfo"/> 中 <c>InfoType = CustomsBroker</c> 的字典项；可空，不建外键）。
    /// <para>只接受启用、未删除的报关行字典项；随意填写的自由文本不会被采信。</para>
    /// </summary>
    public long? CustomsBrokerId { get; set; }

    /// <summary>
    /// 报关行名称快照（由服务端按字典项权威写入，客户端提交的自由文本一律不被采信）。
    /// 保留快照是为了字典项后来被停用 / 删除时，历史订柜记录仍能显示当时的报关行名称。
    /// </summary>
    [MaxLength(100)]
    public string CustomsBrokerName { get; set; } = string.Empty;

    /// <summary>
    /// 查验要求（**显式三态**，互不混淆）：
    /// <c>null</c> = 未知（未标注）；<c>false</c> = 不需要查验；<c>true</c> = 需要查验。
    /// 空串 / 未填写一律落为 <c>null</c>，绝不回落为「不需要查验」。
    /// </summary>
    public bool? InspectionRequired { get; set; }

    /// <summary>查验日期（可选；明确「不需要查验」时不允许填写，见 ContainerShipmentTrackingRules）</summary>
    public DateTime? InspectionDate { get; set; }

    /// <summary>海关放行日期（可选，可空 = 未知）</summary>
    public DateTime? CustomsReleaseDate { get; set; }

    /// <summary>
    /// 报关行引用是否仍可选用（**非持久化列**，读取时由服务端标注）：
    /// 未指定报关行，或指定报关行仍是「启用、未删除、类型为 CustomsBroker」的字典项时为 <c>true</c>；
    /// 字典项被删除 / 停用 / 改类型后为 <c>false</c>（历史名称照常显示，但不允许再次选用）。
    /// </summary>
    [NotMapped]
    public bool CustomsBrokerAvailable { get; set; } = true;

    /// <summary>单据状态</summary>
    public DocumentStatus Status { get; set; } = DocumentStatus.Pending;

    /// <summary>备注</summary>
    [MaxLength(500)]
    public string Remark { get; set; } = string.Empty;
}
