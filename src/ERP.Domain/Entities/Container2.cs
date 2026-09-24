using ERP.Domain.Common;
using ERP.Domain.Enums;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ERP.Domain.Entities;

/// <summary>
/// 预装柜单主表
/// </summary>
public class ContainerPreLoading : BaseEntity
{
    /// <summary>预装柜单号</summary>
    [Required, MaxLength(50)]
    public string PreLoadingNo { get; set; } = string.Empty;

    /// <summary>装柜日期</summary>
    public DateTime LoadingDate { get; set; } = DateTime.Today;

    /// <summary>订柜 Id</summary>
    public long? BookingId { get; set; }

    /// <summary>柜号</summary>
    [MaxLength(50)]
    public string ContainerNo { get; set; } = string.Empty;

    /// <summary>封条号</summary>
    [MaxLength(50)]
    public string SealNo { get; set; } = string.Empty;

    /// <summary>总箱数</summary>
    public decimal TotalCartons { get; set; }

    /// <summary>总毛重（kg）</summary>
    public decimal TotalWeight { get; set; }

    /// <summary>总体积（m³）</summary>
    public decimal TotalVolume { get; set; }

    /// <summary>单据状态</summary>
    public DocumentStatus Status { get; set; } = DocumentStatus.Pending;

    /// <summary>备注</summary>
    [MaxLength(500)]
    public string Remark { get; set; } = string.Empty;

    /// <summary>明细集合</summary>
    public List<ContainerPreLoadingDetail> Details { get; set; } = new();
}

/// <summary>
/// 预装柜单明细
/// </summary>
public class ContainerPreLoadingDetail : BaseEntity
{
    /// <summary>预装柜单 Id</summary>
    public long PreLoadingId { get; set; }

    /// <summary>商品 Id</summary>
    public long ProductId { get; set; }

    /// <summary>商品名称（冗余）</summary>
    [MaxLength(200)]
    public string ProductName { get; set; } = string.Empty;

    /// <summary>数量</summary>
    public decimal Quantity { get; set; }

    /// <summary>箱数</summary>
    public decimal Cartons { get; set; }

    /// <summary>毛重（kg）</summary>
    public decimal Weight { get; set; }

    /// <summary>体积（m³）</summary>
    public decimal Volume { get; set; }

    /// <summary>备注</summary>
    [MaxLength(500)]
    public string Remark { get; set; } = string.Empty;
}

/// <summary>
/// 装柜清单主表
/// </summary>
public class ContainerLoadingList : BaseEntity
{
    /// <summary>装柜清单号</summary>
    [Required, MaxLength(50)]
    public string LoadingListNo { get; set; } = string.Empty;

    /// <summary>预装柜单 Id</summary>
    public long? PreLoadingId { get; set; }

    /// <summary>装柜日期</summary>
    public DateTime LoadingDate { get; set; } = DateTime.Today;

    /// <summary>柜号</summary>
    [MaxLength(50)]
    public string ContainerNo { get; set; } = string.Empty;

    /// <summary>客户 Id</summary>
    public long CustomerId { get; set; }

    /// <summary>唛头</summary>
    [MaxLength(200)]
    public string ShippingMark { get; set; } = string.Empty;

    /// <summary>总箱数</summary>
    public decimal TotalCartons { get; set; }

    /// <summary>总毛重（kg）</summary>
    public decimal TotalWeight { get; set; }

    /// <summary>总体积（m³）</summary>
    public decimal TotalVolume { get; set; }

    /// <summary>单据状态</summary>
    public DocumentStatus Status { get; set; } = DocumentStatus.Pending;

    /// <summary>备注</summary>
    [MaxLength(500)]
    public string Remark { get; set; } = string.Empty;

    /// <summary>明细集合</summary>
    public List<ContainerLoadingDetail> Details { get; set; } = new();

    // ============ ERP-041：一柜多客户参与方（可选子表 ContainerLoadingListParticipants） ============
    // 统一口径：
    //   1. 参与方是装柜清单下**零到多条**的客户清单，用于拼柜场景记录「这柜装了哪几个客户」；
    //      没有参与方行的历史清单行为完全不变，继续按 CustomerId 作为单客户视图读取，**不做任何回填**；
    //   2. 原有 CustomerId 保留为**兼容主客户字段**：有参与方行时，显式指定的主参与方会把它同步成该客户，
    //      未维护参与方的历史清单则原样沿用；读取请求不会写库；
    //   3. 本表不建外键、不被任何单据引用：参与方的新增 / 停用 / 删除**不**改写装柜明细数量、
    //      订柜跟踪值、单证、费用记录、库存与库存流水，也不改写客户主数据。

    /// <summary>参与方集合（空 = 历史单客户视图，按 <see cref="CustomerId"/> 读取）</summary>
    public List<ContainerLoadingListParticipant> Participants { get; set; } = new();

    /// <summary>
    /// 参与方中**启用中**的条数（**非持久化列**，列表 / 详情读取时由服务端标注）；
    /// 0 = 未维护参与方，装柜清单仍按历史单客户视图使用。
    /// </summary>
    [NotMapped]
    public int ParticipantCount { get; set; }

    /// <summary>
    /// 参与方总条数（含停用，**非持久化列**，读取时由服务端标注）：
    /// 与 <see cref="ParticipantCount"/> 的差值即「已停用参与方数」，界面据此提示历史参与方仍可读但不可再新选。
    /// </summary>
    [NotMapped]
    public int ParticipantTotalCount { get; set; }

    /// <summary>
    /// 当前**启用中主参与方**的客户 Id（**非持久化列**，读取时由服务端标注；无主参与方为 <c>null</c>）。
    /// 有值时该客户与兼容字段 <see cref="CustomerId"/> 一致（由服务端在显式置主时同步）。
    /// </summary>
    [NotMapped]
    public long? PrimaryParticipantCustomerId { get; set; }

    /// <summary>当前启用中主参与方的客户名称快照（**非持久化列**；无主参与方为空串）</summary>
    [NotMapped]
    public string PrimaryParticipantCustomerName { get; set; } = string.Empty;

    /// <summary>
    /// 主参与方客户当前是否仍可用（未删除且启用，**非持久化列**）：客户后来停用 / 删除时为 <c>false</c>，
    /// 历史名称快照照常显示，但不允许再次被新选为主参与方。
    /// </summary>
    [NotMapped]
    public bool PrimaryParticipantAvailable { get; set; } = true;

    /// <summary>
    /// 是否为**历史单客户视图**（没有任何参与方行，**非持久化列**）：为真时客户身份只来自
    /// <see cref="CustomerId"/>，界面必须显式说明，而不是把该客户当成唯一的参与方记录。
    /// </summary>
    [NotMapped]
    public bool LegacySingleCustomer { get; set; }
}

/// <summary>
/// 装柜清单多客户参与方（ERP-041）：把一个既有装柜清单（一柜）与**多个既有客户**关联起来，
/// 用于拼柜（一柜多客户）场景记录该柜的参与客户，并显式指定其中一条为**主参与方**。
/// <para>定位：**装柜清单的客户归属清单**——只描述「这柜装了哪几个客户、哪个是主客户」，
/// 由人工维护，<strong>不参与任何自动决策</strong>。</para>
/// <para>边界（重要）：
/// <list type="bullet">
/// <item>不自动按数量 / 体积 / 金额给客户分摊费用（分摊是独立任务），不产生费用单与结算记录；</item>
/// <item>不改写装柜清单明细的数量 / 箱数 / 重量 / 体积，不改写柜号、订柜跟踪值、单证与库存；</item>
/// <item>不建外键：客户允许软删除 / 停用，历史参与方必须继续可读（用名称 / 编码快照展示并显式标注不可用）；</item>
/// <item>主参与方唯一性由服务端判定 + 过滤唯一索引 <c>UX_ContainerLoadingListParticipants_ListPrimary</c> 双重兜底。</item>
/// </list>
/// </para>
/// </summary>
public class ContainerLoadingListParticipant : BaseEntity
{
    /// <summary>归属装柜清单 Id（引用 <see cref="ContainerLoadingList"/>；本表是清单的可选子表）</summary>
    public long LoadingListId { get; set; }

    /// <summary>参与客户 Id（引用 <see cref="BaseCustomer"/>；刻意不建外键，避免客户软删除时连带影响历史参与方）</summary>
    public long CustomerId { get; set; }

    /// <summary>
    /// 客户编码快照（由服务端按客户主数据权威写入，客户端提交值一律不被采信）：
    /// 客户后来被删除 / 停用时历史参与方仍能显示当时的编码。
    /// </summary>
    [MaxLength(50)]
    public string CustomerCode { get; set; } = string.Empty;

    /// <summary>
    /// 客户名称快照（由服务端按客户主数据权威写入）：
    /// 保留快照是为了客户后来被停用 / 删除时，历史参与方仍能显示当时的客户名称，而不是静默变成空白。
    /// </summary>
    [MaxLength(200)]
    public string CustomerName { get; set; } = string.Empty;

    /// <summary>
    /// 是否为该装柜清单的主参与方（同一清单最多一条**启用中**的主参与方）。
    /// 停用 / 删除时该标记会被释放，重新启用后需再次显式设为「主参与方」。
    /// </summary>
    public bool IsPrimary { get; set; }

    /// <summary>状态（1=启用，可作为该柜的参与客户；0=停用，历史可读但不再作为参与客户）</summary>
    public int Status { get; set; } = 1;

    /// <summary>排序号（同清单内展示顺序；主参与方判定与列表顺序无关）</summary>
    public int SortOrder { get; set; }

    /// <summary>备注</summary>
    [MaxLength(500)]
    public string Remark { get; set; } = string.Empty;

    /// <summary>
    /// 参与客户当前是否仍可用（未删除且启用，**非持久化列**）：客户后来停用 / 删除时为 <c>false</c>，
    /// 历史编码 / 名称快照照常显示，但不允许再次被新选为主参与方。
    /// </summary>
    [NotMapped]
    public bool CustomerAvailable { get; set; } = true;

    /// <summary>
    /// 客户主数据中的**当前**名称（**非持久化列**）：客户行仍存在时由服务端解析，否则为空串。
    /// 用于在界面提示「快照名称与当前客户名称不一致」，而不是静默把快照改写掉。
    /// </summary>
    [NotMapped]
    public string CustomerCurrentName { get; set; } = string.Empty;

    /// <summary>快照名称是否与客户主数据当前名称不一致（**非持久化列**；客户不可用或无名称时恒为 <c>false</c>）</summary>
    [NotMapped]
    public bool CustomerRenamed { get; set; }
}

/// <summary>
/// 装柜清单明细
/// </summary>
public class ContainerLoadingDetail : BaseEntity
{
    /// <summary>装柜清单 Id</summary>
    public long LoadingListId { get; set; }

    /// <summary>商品 Id</summary>
    public long ProductId { get; set; }

    /// <summary>商品名称（冗余）</summary>
    [MaxLength(200)]
    public string ProductName { get; set; } = string.Empty;

    /// <summary>数量</summary>
    public decimal Quantity { get; set; }

    /// <summary>箱数</summary>
    public decimal Cartons { get; set; }

    /// <summary>毛重（kg）</summary>
    public decimal Weight { get; set; }

    /// <summary>体积（m³）</summary>
    public decimal Volume { get; set; }

    /// <summary>备注</summary>
    [MaxLength(500)]
    public string Remark { get; set; } = string.Empty;
}
