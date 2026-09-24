namespace ERP.Application.DTOs;

/// <summary>
/// 装柜清单多客户参与方写入 DTO（ERP-041）。
/// <para>客户端只提交「客户 + 是否主参与方 + 状态 + 排序号 + 备注」这类归属字段；
/// 客户编码 / 名称快照、可用性判定、重复判定与兼容客户字段同步一律由服务端推导，客户端提交值不被采信。</para>
/// <para>边界：本 DTO 不含任何数量 / 体积 / 金额字段 —— 参与方只记录客户归属，
/// 不参与费用分摊、不生成费用单，也不改写装柜明细与任何其他单据。</para>
/// </summary>
public sealed class ContainerLoadingParticipantSaveDto
{
    /// <summary>参与客户 Id（新增 / 更换客户时必须是未删除且启用中的客户）</summary>
    public long CustomerId { get; set; }

    /// <summary>
    /// 是否设为该装柜清单的主参与方（同一清单最多一条启用主参与方；为空按「否」/保持原值）。
    /// <para>主参与方会被同步到装柜清单的兼容客户字段（<c>ContainerLoadingList.CustomerId</c>）；
    /// 停用状态的参与方不能作为主参与方。</para>
    /// </summary>
    public bool? IsPrimary { get; set; }

    /// <summary>状态（1=启用 / 0=停用；新增时为空按启用处理）</summary>
    public int? Status { get; set; }

    /// <summary>排序号（为空按 0；仅影响展示顺序，与主参与方判定无关）</summary>
    public int? SortOrder { get; set; }

    /// <summary>备注</summary>
    public string Remark { get; set; } = string.Empty;
}

/// <summary>
/// 装柜清单多客户参与方读取 DTO（ERP-041）：参与方自身字段 + 由服务端解析的客户信息与可用性标注。
/// <para>列表与维护视图共用本结构，按装柜清单聚合返回**有界**列表（无逐行数据库查询）。</para>
/// </summary>
/// <param name="Id">参与方 Id</param>
/// <param name="LoadingListId">归属装柜清单 Id</param>
/// <param name="LoadingListNo">装柜清单号</param>
/// <param name="CustomerId">参与客户 Id</param>
/// <param name="CustomerCode">客户编码快照（服务端按客户主数据写入）</param>
/// <param name="CustomerName">客户名称快照（客户后来停用 / 删除时仍显示当时的名称）</param>
/// <param name="CustomerCurrentName">客户主数据中的当前名称（客户行不存在时为空串）</param>
/// <param name="CustomerRenamed">快照名称与客户当前名称是否不一致（客户不可用或无名称时恒为 false）</param>
/// <param name="CustomerAvailable">客户当前是否可用（未删除且启用）</param>
/// <param name="IsPrimary">是否为该清单的主参与方</param>
/// <param name="Status">状态（1=启用 / 0=停用）</param>
/// <param name="Selectable">当前是否可作为该柜的启用参与方（停用或已删除为 false）</param>
/// <param name="StatusText">状态文案（启用 / 停用）</param>
/// <param name="AvailabilityText">可用性文案（可选用 / 已停用 / 客户不可用，界面显式提示，不静默消失）</param>
/// <param name="SortOrder">排序号</param>
/// <param name="Remark">备注</param>
/// <param name="CreatedAt">创建时间</param>
/// <param name="UpdatedAt">更新时间</param>
public sealed record ContainerLoadingParticipantDto(
    long Id,
    long LoadingListId,
    string LoadingListNo,
    long CustomerId,
    string CustomerCode,
    string CustomerName,
    string CustomerCurrentName,
    bool CustomerRenamed,
    bool CustomerAvailable,
    bool IsPrimary,
    int Status,
    bool Selectable,
    string StatusText,
    string AvailabilityText,
    int SortOrder,
    string Remark,
    DateTime CreatedAt,
    DateTime? UpdatedAt);
