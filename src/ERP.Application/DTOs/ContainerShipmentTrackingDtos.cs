namespace ERP.Application.DTOs;

/// <summary>
/// 装柜外贸与物流跟踪的只读投影（ERP-040）。
/// <para>权威记录是订柜信息 <c>ContainerBooking</c>：预装柜单 / 装柜清单按**持久化引用**
/// （<c>ContainerPreLoading.BookingId</c>）取到订柜记录后原样回显，不复制、不各自维护第二份跟踪值。</para>
/// <para>未知口径：没有持久化引用（或引用指向的订柜记录已不可读）时 <see cref="Linked"/> = <c>false</c>，
/// 所有业务字段保持空串 / <c>null</c>，前端显示「未知 / 未关联」——
/// **绝不**按柜号、提单号等自由文本去匹配一条订柜记录。</para>
/// <para>边界：本投影不写库、不改写任何单据与明细，也不调用船公司 / 海关 / 货代等外部跟踪系统。</para>
/// </summary>
public sealed class ContainerShipmentTrackingDto
{
    /// <summary>是否存在可读的持久化订柜引用（false = 未关联 / 引用不可用，跟踪字段一律未知）</summary>
    public bool Linked { get; set; }

    /// <summary>订柜信息 Id（未关联时为 null）</summary>
    public long? BookingId { get; set; }

    /// <summary>订柜单号（未关联时为空串）</summary>
    public string BookingNo { get; set; } = string.Empty;

    /// <summary>出运方式（LCL / FCL；空串 = 未知 / 未指定）</summary>
    public string ShipmentMode { get; set; } = string.Empty;

    /// <summary>出运方式文案（拼箱 LCL / 整箱 FCL / 未知）</summary>
    public string ShipmentModeText { get; set; } = string.Empty;

    /// <summary>提单号（B/L；空串 = 未知）</summary>
    public string BillOfLadingNo { get; set; } = string.Empty;

    /// <summary>订舱号 / 托运单号（S/O；空串 = 未知）</summary>
    public string ShippingOrderNo { get; set; } = string.Empty;

    /// <summary>起运港（空串 = 未知）</summary>
    public string DeparturePort { get; set; } = string.Empty;

    /// <summary>目的港（空串 = 未知）</summary>
    public string DestinationPort { get; set; } = string.Empty;

    /// <summary>中转港（空串 = 未知）</summary>
    public string TransitPort { get; set; } = string.Empty;

    /// <summary>预计开船日 ETD（null = 未知）</summary>
    public DateTime? Etd { get; set; }

    /// <summary>预计到港日 ETA（null = 未知）</summary>
    public DateTime? Eta { get; set; }

    /// <summary>实际开船日 ATD（null = 未知 / 尚未开船）</summary>
    public DateTime? Atd { get; set; }

    /// <summary>实际到港日 ATA（null = 未知 / 尚未到港）</summary>
    public DateTime? Ata { get; set; }

    /// <summary>拖车 / 集卡公司名称（空串 = 未知）</summary>
    public string TruckerName { get; set; } = string.Empty;

    /// <summary>报关行 Id（null = 未指定）</summary>
    public long? CustomsBrokerId { get; set; }

    /// <summary>报关行名称快照（空串 = 未指定 / 未知）</summary>
    public string CustomsBrokerName { get; set; } = string.Empty;

    /// <summary>报关行引用是否仍可选用（false = 字典项已停用 / 删除 / 改类型，历史名称照常显示但显式标注）</summary>
    public bool CustomsBrokerAvailable { get; set; } = true;

    /// <summary>查验要求（null = 未知；false = 不需要查验；true = 需要查验）</summary>
    public bool? InspectionRequired { get; set; }

    /// <summary>查验要求文案（需要查验 / 不需要查验 / 未知）</summary>
    public string InspectionRequiredText { get; set; } = string.Empty;

    /// <summary>查验日期（null = 未知）</summary>
    public DateTime? InspectionDate { get; set; }

    /// <summary>海关放行日期（null = 未知）</summary>
    public DateTime? CustomsReleaseDate { get; set; }

    /// <summary>未关联时的说明文案（已关联时为空串）</summary>
    public string NotLinkedReason { get; set; } = string.Empty;
}
