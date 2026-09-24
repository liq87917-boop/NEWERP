using ERP.Application.Common;
using ERP.Domain.Entities;
using System.Text;

namespace ERP.Application.Services;

/// <summary>
/// 装柜清单多客户参与方的纯规则（ERP-041，无数据库依赖，便于逐条单测）：
/// 文本与数值规范化、重复参与方判定、「同一清单最多一条启用主参与方」判定、
/// 主参与方不可直接移除（需先改指他人）的判定，以及不可用引用的显式文案。
/// <para>边界：本规则只判定「客户归属清单自身是否合法」——不按数量 / 体积 / 金额分摊费用、
/// 不生成费用单、不改写装柜明细 / 柜号 / 订柜跟踪值 / 单证 / 库存与客户主数据。</para>
/// </summary>
public static class ContainerLoadingParticipantRules
{
    /// <summary>启用状态（可作为该柜的参与客户）</summary>
    public const int ActiveStatus = 1;

    /// <summary>停用状态（历史保留，可读但不再作为参与客户）</summary>
    public const int DisabledStatus = 0;

    /// <summary>客户编码快照长度上限（与实体 / 建表脚本一致）</summary>
    public const int MaxCustomerCodeLength = 50;

    /// <summary>客户名称快照长度上限</summary>
    public const int MaxCustomerNameLength = 200;

    /// <summary>备注长度上限</summary>
    public const int MaxRemarkLength = 500;

    /// <summary>排序号上限（仅影响展示顺序）</summary>
    public const int MaxSortOrder = 9999;

    /// <summary>
    /// 单个装柜清单允许维护的参与方条数上限（含停用行）：
    /// 装柜侧的读取与维护视图按此上限收敛，保证列表**有界**。
    /// </summary>
    public const int MaxParticipantsPerLoadingList = 100;

    /// <summary>引用不可用（客户已停用 / 已删除）时的显式展示标注</summary>
    public const string UnavailableMark = "（已停用/不可用）";

    /// <summary>历史单客户视图文案：没有任何参与方行时的客户归属口径</summary>
    public const string LegacySingleCustomerText = "历史单客户视图（未维护参与方，客户归属沿用装柜清单原有客户字段）";

    /// <summary>有参与方但未指定主参与方时的文案（不臆造主客户，也不按列表顺序取胜）</summary>
    public const string NoPrimaryParticipantText = "未指定主参与方（兼容客户字段沿用装柜清单原值）";

    /// <summary>主参与方文案</summary>
    public const string PrimaryText = "主参与方";

    /// <summary>把空白字符压缩为单个空格并去掉首尾空白（中文全角空格同样按空白处理）</summary>
    private static string CollapseWhitespace(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        var builder = new StringBuilder(value.Length);
        var pendingSpace = false;
        foreach (var ch in value.Trim())
        {
            if (char.IsWhiteSpace(ch) || ch == '\u3000')
            {
                if (builder.Length > 0) pendingSpace = true;
                continue;
            }
            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }
            builder.Append(ch);
        }
        return builder.ToString();
    }

    /// <summary>展示值：压缩空白并去首尾空白，保留原大小写</summary>
    public static string DisplayValue(string? value) => CollapseWhitespace(value);

    /// <summary>状态规范化：为空按启用；只接受 1（启用）/ 0（停用），其他取值一律拒绝，不做隐式修正</summary>
    public static int NormalizeStatus(int? status) => status switch
    {
        null => ActiveStatus,
        ActiveStatus => ActiveStatus,
        DisabledStatus => DisabledStatus,
        _ => throw BusinessException.InvalidParameter($"参与方状态只能是 1（启用）或 0（停用），收到 {status}")
    };

    /// <summary>排序号规范化：为空按 0；不允许负数或超过上限（仅影响展示顺序）</summary>
    public static int NormalizeSortOrder(int? sortOrder)
    {
        if (sortOrder is null) return 0;
        if (sortOrder.Value < 0 || sortOrder.Value > MaxSortOrder)
            throw BusinessException.InvalidParameter($"排序号必须在 0 ~ {MaxSortOrder} 之间，收到 {sortOrder.Value}");
        return sortOrder.Value;
    }

    /// <summary>备注规范化：压缩空白并校验长度（超长直接拒绝，不静默截断）</summary>
    public static string NormalizeRemark(string? remark)
    {
        var value = CollapseWhitespace(remark);
        EnsureLength(value, MaxRemarkLength, "备注");
        return value;
    }

    /// <summary>文本长度校验：超长直接拒绝，不静默截断</summary>
    public static void EnsureLength(string value, int maxLength, string fieldLabel)
    {
        if (value.Length > maxLength)
            throw BusinessException.InvalidParameter(
                $"{fieldLabel}最长 {maxLength} 个字符，当前 {value.Length} 个字符");
    }

    /// <summary>参与方当前是否可作为该柜的**启用参与客户**（未删除且启用）；停用 / 已删除只能作为历史读取</summary>
    public static bool IsSelectable(ContainerLoadingListParticipant? participant) =>
        participant is not null && !participant.IsDeleted && participant.Status == ActiveStatus;

    /// <summary>该行是否属于「启用中的主参与方」（主参与方唯一性只针对启用中的参与方判定）</summary>
    public static bool IsPrimaryActive(ContainerLoadingListParticipant? participant) =>
        IsSelectable(participant) && participant!.IsPrimary;

    /// <summary>主参与方文案（主参与方 / —），与列表顺序无关</summary>
    public static string PrimaryTextOf(bool isPrimary) => isPrimary ? PrimaryText : "—";

    /// <summary>状态文案（启用 / 停用）</summary>
    public static string StatusText(int status) => status == ActiveStatus ? "启用" : "停用";

    /// <summary>
    /// 主参与方与状态的一致性：只有启用中的参与方才可以作为主参与方
    /// （停用行即使带主标记也不生效，服务端会在停用时释放标记）。
    /// </summary>
    public static void EnsurePrimaryCompatible(bool isPrimary, int status)
    {
        if (isPrimary && status != ActiveStatus)
            throw BusinessException.InvalidParameter(
                "只有启用状态的参与方可以设为主参与方，请先启用该参与方或取消主参与方标记");
    }

    /// <summary>单清单参与方条数上限校验（含停用行，保证维护视图有界）</summary>
    public static void EnsureParticipantBound(int existingCount)
    {
        if (existingCount >= MaxParticipantsPerLoadingList)
            throw BusinessException.InvalidParameter(
                $"单个装柜清单的参与方条数已达上限 {MaxParticipantsPerLoadingList} 条，请先清理不再使用的参与方");
    }

    /// <summary>
    /// 同一装柜清单内同一客户不允许重复维护（含停用行，软删除行不占用）：
    /// 重复参与方会被拒绝，已停用的历史参与方照常保留可读，恢复参与请直接启用原参与方而不是另建一条。
    /// <para>数据库层由过滤唯一索引 <c>UX_ContainerLoadingListParticipants_ListCustomer</c> 兜底并发写入。</para>
    /// </summary>
    public static void EnsureListCustomerUnique(
        IEnumerable<ContainerLoadingListParticipant> siblings, long customerId, long? excludeId = null)
    {
        if (customerId <= 0)
            throw BusinessException.InvalidParameter("参与客户 Id 不合法");

        var duplicated = siblings.Any(x =>
            !x.IsDeleted && x.Id != (excludeId ?? 0) && x.CustomerId == customerId);

        if (duplicated)
            throw BusinessException.Duplicate(
                "该客户在本装柜清单中已存在参与方记录（含停用记录），不允许重复维护；"
                + "如需恢复参与请直接启用原有参与方");
    }

    /// <summary>
    /// 同一装柜清单最多一条**启用中的**主参与方：停用 / 已删除行不占用主参与方位；
    /// 更换主参与方必须走显式的「设为主参与方」操作（先释放旧主再置新主），
    /// 不允许通过「并列勾选」或依赖列表顺序来隐式取胜。
    /// <para>数据库层由过滤唯一索引 <c>UX_ContainerLoadingListParticipants_ListPrimary</c> 兜底。</para>
    /// </summary>
    public static void EnsurePrimaryUnique(
        IEnumerable<ContainerLoadingListParticipant> siblings, bool requirePrimary, long? excludeId = null)
    {
        if (!requirePrimary) return;

        var existing = siblings.Any(x => x.Id != (excludeId ?? 0) && IsPrimaryActive(x));

        if (existing)
            throw BusinessException.Duplicate(
                "该装柜清单已存在启用中的主参与方：更换主参与方请对目标参与方执行「设为主参与方」"
                + "（服务端会先释放旧主参与方再设置新主参与方并同步兼容客户字段），不支持并列填写");
    }

    /// <summary>
    /// 主参与方不能被直接停用 / 删除：只有在本清单不再有其他启用参与方时才允许
    /// （此时清单回退为按兼容字段读取的历史单客户视图）；否则必须先显式改指其他参与方，
    /// 以保证「有启用参与方时兼容客户字段始终跟随主参与方」，且结果与列表顺序无关。
    /// </summary>
    public static void EnsurePrimaryRemovable(
        IEnumerable<ContainerLoadingListParticipant> siblings, ContainerLoadingListParticipant target, string actionText)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!IsPrimaryActive(target)) return;

        var others = siblings.Where(x => x.Id != target.Id && IsSelectable(x)).ToList();
        if (others.Count > 0)
            throw BusinessException.RuleConflict(
                $"该客户是本装柜清单当前的主参与方，不能直接{actionText}：请先把其他启用参与方设为主参与方；"
                + "若本清单只剩这一个启用参与方，可在停用 / 删除后回退为历史单客户视图");
    }

    /// <summary>
    /// 校验被选中的客户确实可作为参与方：
    /// 不存在 / 已删除 → 404（无法定位客户）；已停用 → 参数校验失败（不允许新选，历史参与方仍可读）。
    /// </summary>
    public static void EnsureCustomerSelectable(BaseCustomer? customer, long customerId)
    {
        if (customer is null || customer.IsDeleted)
            throw BusinessException.NotFound($"客户（Id={customerId}）不存在或已删除，请重新选择");

        if (customer.Status != ActiveStatus)
            throw BusinessException.InvalidParameter(
                $"客户「{DisplayValue(customer.CustomerName)}」已停用，不能新增或更换为参与方"
                + "（历史参与方仍可读取，如需恢复参与请先启用该客户）");
    }

    /// <summary>
    /// 设为「主参与方」的额外门槛：客户必须仍然可用（未删除且启用），
    /// 否则会把装柜清单的兼容客户字段指向一个不可用客户。历史参与方照常只读显示，只是不能再被置主。
    /// </summary>
    public static void EnsureCustomerPromotable(BaseCustomer? customer, long customerId)
    {
        if (customer is null || customer.IsDeleted)
            throw BusinessException.InvalidParameter(
                $"客户（Id={customerId}）已删除，不能设为主参与方（历史参与方仍可只读查看）");

        if (customer.Status != ActiveStatus)
            throw BusinessException.InvalidParameter(
                $"客户「{DisplayValue(customer.CustomerName)}」已停用，不能设为主参与方"
                + "（历史参与方仍可只读查看，如需恢复请先启用该客户）");
    }

    /// <summary>客户编码快照（服务端按客户主数据权威写入，客户端提交值不被采信）</summary>
    public static string SnapshotCustomerCode(BaseCustomer customer)
    {
        ArgumentNullException.ThrowIfNull(customer);
        var code = DisplayValue(customer.CustomerCode);
        EnsureLength(code, MaxCustomerCodeLength, "客户编码");
        return code;
    }

    /// <summary>客户名称快照（服务端按客户主数据权威写入；名称为空时退化为空串，不写入伪造名称）</summary>
    public static string SnapshotCustomerName(BaseCustomer customer)
    {
        ArgumentNullException.ThrowIfNull(customer);
        var name = DisplayValue(customer.CustomerName);
        EnsureLength(name, MaxCustomerNameLength, "客户名称");
        return name;
    }

    /// <summary>引用不可用时的显式展示文案：名称 + 标注（名称为空时只显示标注，不留空白）</summary>
    public static string MarkUnavailable(string? name) =>
        string.IsNullOrWhiteSpace(name) ? UnavailableMark : $"{DisplayValue(name)}{UnavailableMark}";

    /// <summary>
    /// 可用性文案：显式说明该参与方当前能否作为该柜的参与客户，
    /// 避免停用 / 已删除的客户被静默当成有效参与方（历史引用照常显示，但界面必须能看出不可用）。
    /// </summary>
    public static string AvailabilityText(bool selectable, bool customerAvailable)
    {
        if (!selectable) return "已停用（历史保留，不再作为参与客户）";
        if (!customerAvailable) return $"客户已停用 / 已删除{UnavailableMark}，仅历史可读";
        return "可选用";
    }

    /// <summary>参与方展示名：优先用名称快照，缺失时退化用编码快照，再缺失退化为「客户#Id」，历史仍可辨识</summary>
    public static string DisplayName(string? name, string? code, long customerId)
    {
        var normalizedName = DisplayValue(name);
        if (normalizedName.Length > 0) return normalizedName;
        var normalizedCode = DisplayValue(code);
        return normalizedCode.Length > 0 ? normalizedCode : $"客户#{customerId}";
    }
}
