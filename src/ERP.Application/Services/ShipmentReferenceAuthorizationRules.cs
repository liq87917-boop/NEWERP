using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ERP.Application.Services;

/// <summary>
/// 装柜出运引用登记（ERP-362）的实时授权结果：一次请求解析出的**既有**授权与数据范围。
/// <para><see cref="AllowedSourceTypes"/> 只包含当前账号真正具备**既有源模块菜单**
/// （<c>booking</c> / <c>pre-loading</c> / <c>loading-list</c>）的源记录类型；特权账号继承全部三种。
/// 非特权账号还必须映射到业务员（否则 fail closed，不降级为全局可见）。</para>
/// </summary>
public sealed class ShipmentReferenceAccess
{
    /// <summary>ERP-097 权威数据范围（未映射业务员的受限账号为空集合，即「看不到任何客户」）</summary>
    public SalespersonDataScope Scope { get; init; } = new();

    /// <summary>当前登录用户 Id（写入审计字段 <c>CreatedBy</c> / <c>UpdatedBy</c>）</summary>
    public long? UserId { get; init; }

    /// <summary>是否特权账号（不受菜单与数据范围限制）</summary>
    public bool IsPrivileged => Scope.IsPrivileged;

    /// <summary>当前账号被允许访问的源记录类型（显式 allowlist 的子集）</summary>
    public HashSet<string> AllowedSourceTypes { get; init; } = new(StringComparer.Ordinal);

    /// <summary>该源记录类型是否在当前账号的既有菜单授权范围内</summary>
    public bool AllowsSourceType(string? sourceType)
        => !string.IsNullOrEmpty(sourceType) && AllowedSourceTypes.Contains(sourceType);

    /// <summary>该客户是否在当前账号的权威数据范围内（特权账号恒为真）</summary>
    public bool AllowsCustomer(long? customerId) => Scope.AllowsCustomer(customerId);
}

/// <summary>
/// 装柜出运引用登记（ERP-057）的实时授权、既有源模块菜单与客户数据范围护栏（ERP-362）。
/// <para>本模块的证据挂靠在**既有**装柜链路三种源记录（订柜信息 <c>booking</c> / 预装柜单
/// <c>pre-loading</c> / 装柜清单 <c>loading-list</c>）之上，因此授权口径不允许自造权限：
/// 每次请求都重新解析 <b>实时身份</b>（缺失 / 非法 / 不存在 / 已删除按未认证拒绝）→ <b>账号状态</b>
/// （禁用按权限不足）→ <b>既有源模块菜单</b>（非特权账号必须显式具备对应源记录类型的既有菜单）→
/// <b>权威客户数据范围</b>（复用 ERP-097 <see cref="SalespersonDataScopeService"/>；未映射业务员的受限账号
/// fail closed，绝不降级为全局 / 管理员可见）。</para>
/// <para><b>读取与写入同口径</b>：台账 / 详情 / 修订留痕 / 源记录候选 / 报关行选项 / 登记 / 修订 / 作废
/// 全部先授权；受限账号只能看到（且在写入前必须命中）本人客户的源记录与引用行，
/// 范围判定以**源记录的权威客户**（订柜信息 / 装柜清单的 <c>CustomerId</c>；预装柜单经
/// <see cref="ContainerPreLoading.BookingId"/> 归属的订柜信息客户）为准：没有权威客户归属的历史预装柜单
/// 对受限账号 fail closed。</para>
/// <para>本类只做<b>纯判定与有界只读查询</b>：不落库、不改写源记录与任何下游单据，不新增任何表 / 列 /
/// 菜单 / 权限模型，也不把空身份当作管理员；事务与行锁由调用方（<c>ContainerShipmentReferenceController</c>）
/// 负责。</para>
/// </summary>
public static class ShipmentReferenceAuthorizationRules
{
    /// <summary>模块中文名称（接口 / 文档同源）</summary>
    public const string ModuleText = "装柜出运引用登记";

    /// <summary>订柜信息既有菜单编码（与 <c>SeedData</c> 同源，绝不新增菜单）</summary>
    public const string BookingMenuCode = PreLoadingBookingLinkRules.BookingRequiredMenuCode;

    /// <summary>预装柜单既有菜单编码（与 <c>SeedData</c> 同源）</summary>
    public const string PreLoadingMenuCode = PreLoadingBookingLinkRules.RequiredMenuCode;

    /// <summary>装柜清单既有菜单编码（与 <c>SeedData</c> 同源）</summary>
    public const string LoadingListMenuCode = ContainerLoadingFulfillmentRules.RequiredMenuCode;

    /// <summary>未映射业务员的受限账号拒绝文案（fail closed，不泄露任何范围外单据）</summary>
    public const string UnmappedOperatorText =
        "当前账号未映射为业务员（装柜出运引用操作员），不能访问装柜出运引用登记（fail closed，不泄露任何范围外单据）";

    /// <summary>越客户范围的拒绝文案（fail closed，不泄露范围外单据）</summary>
    public const string OutOfScopeText =
        "当前账号的客户数据范围不包含该源记录的客户：拒绝操作（fail closed，不泄露范围外单据）";

    /// <summary>没有任何既有源模块菜单时的拒绝文案（fail closed）</summary>
    public const string NoSourceMenuText =
        "当前账号没有「订柜信息」（booking）/「预装柜单」（pre-loading）/「装柜清单」（loading-list）任一既有模块授权："
        + "拒绝访问装柜出运引用登记";

    /// <summary>授权与范围口径文案（接口 / 文档同源）</summary>
    public const string RuleText =
        "装柜出运引用登记（台账 / 详情 / 修订留痕 / 源记录候选 / 报关行选项 / 登记 / 修订 / 作废）在读取任何计数、"
        + "候选或证据字段之前，都会重新校验实时身份（缺失 / 非法 / 已删除按未认证，禁用按权限不足，一律 fail closed）、"
        + "与源记录类型对应的既有源模块菜单授权（订柜信息 booking / 预装柜单 pre-loading / 装柜清单 loading-list）"
        + "与权威客户数据范围（复用 ERP-097）；受限账号只能读写本人客户的源记录及其出运引用证据，"
        + "数据库侧范围先于计数与分页；未映射业务员的受限账号 fail closed，绝不降级为全局 / 管理员可见。";

    /// <summary>边界文案（不新增权限 / 表列 / 外部调用）</summary>
    public const string BoundaryText =
        "本护栏只复用既有权限模型与既有数据范围：不新增用户授权、表 / 列 / 菜单，不做任何特权扩展与匿名回退；"
        + "只读写 ContainerShipmentReferences 与 ContainerShipmentReferenceRevisions 两张表，不改写源记录的状态与工作流、"
        + "不写库存与库存成本、库存流水、单证中心、发票、费用与分摊、收付款、税务与结算记录，"
        + "也不联系承运人 / 海关 / 货代或任何外部跟踪系统。";

    /// <summary>源记录类型对应的既有源模块菜单编码（未知类型返回空串，由调用方按未授权拒绝）</summary>
    public static string SourceMenuCode(string? sourceType) => sourceType switch
    {
        ContainerShipmentReferenceRules.SourceTypeBooking => BookingMenuCode,
        ContainerShipmentReferenceRules.SourceTypePreLoading => PreLoadingMenuCode,
        ContainerShipmentReferenceRules.SourceTypeLoadingList => LoadingListMenuCode,
        _ => string.Empty
    };

    /// <summary>源记录类型对应的既有源模块菜单中文文案（与既有菜单名一致）</summary>
    public static string SourceMenuText(string? sourceType) => sourceType switch
    {
        ContainerShipmentReferenceRules.SourceTypeBooking => PreLoadingBookingLinkRules.BookingRequiredMenuText,
        ContainerShipmentReferenceRules.SourceTypePreLoading => PreLoadingBookingLinkRules.RequiredMenuText,
        ContainerShipmentReferenceRules.SourceTypeLoadingList => ContainerLoadingFulfillmentRules.RequiredMenuText,
        _ => "未知模块"
    };

    /// <summary>
    /// 身份 / 账号状态 / 既有源模块菜单 / 权威数据范围四重校验（fail closed），返回本次请求的授权与范围。
    /// <list type="bullet">
    /// <item>身份缺失 / 非法 → <see cref="ErrorCodes.Unauthorized"/>；</item>
    /// <item>账号不存在或已删除 → <see cref="ErrorCodes.Unauthorized"/>；</item>
    /// <item>账号已禁用 → <see cref="ErrorCodes.Forbidden"/>；</item>
    /// <item>非特权账号没有任何既有源模块菜单 → <see cref="ErrorCodes.Forbidden"/>；</item>
    /// <item>非特权账号未映射为业务员 → <see cref="ErrorCodes.Forbidden"/>（fail closed，不降级为全局可见）。</item>
    /// </list>
    /// 每次请求重新解析，授权 / 菜单 / 员工映射变更后下一次请求立即收敛。
    /// </summary>
    public static async Task<ShipmentReferenceAccess> EnsureAuthorizedAsync(
        IErpDbContext db, long? userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ct.ThrowIfCancellationRequested();

        if (userId is null or <= 0)
            throw new BusinessException($"请先登录后再访问{ModuleText}", ErrorCodes.Unauthorized);

        var user = await db.SysUsers.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == userId.Value && !u.IsDeleted, ct);
        if (user is null)
            throw new BusinessException($"登录账号不存在或已删除，禁止访问{ModuleText}", ErrorCodes.Unauthorized);
        if (user.Status != UserStatus.Enabled)
            throw new BusinessException($"登录账号已禁用，禁止访问{ModuleText}（fail closed）", ErrorCodes.Forbidden);

        var scope = await SalespersonDataScopeService.ResolveAsync(db, userId.Value);

        var allowed = new HashSet<string>(
            ContainerShipmentReferenceRules.SupportedSourceTypes, StringComparer.Ordinal);

        // 特权账号继承既有全部访问（与 ERP-097 / ERP-360 同源），但绝不因为身份缺失而降级为管理员。
        if (!scope.IsPrivileged)
        {
            var menuCodes = await CustomerReceivableReconciliationService
                .LoadAuthorizedMenuCodesAsync(db, userId.Value);
            allowed.RemoveWhere(type => !menuCodes.Contains(SourceMenuCode(type), StringComparer.OrdinalIgnoreCase));
            if (allowed.Count == 0)
                throw new BusinessException(
                    NoSourceMenuText + "（fail closed，不返回 / 不修改任何出运引用证据）", ErrorCodes.Forbidden);

            if (scope.SalesmanId is null or <= 0)
                throw new BusinessException(UnmappedOperatorText, ErrorCodes.Forbidden);
        }

        return new ShipmentReferenceAccess
        {
            Scope = scope,
            UserId = userId.Value,
            AllowedSourceTypes = allowed
        };
    }

    /// <summary>源记录类型必须落在当前账号的既有源模块菜单授权内，否则 fail closed 拒绝。</summary>
    public static void RequireSourceType(ShipmentReferenceAccess access, string? sourceType)
    {
        ArgumentNullException.ThrowIfNull(access);
        if (access.AllowsSourceType(sourceType)) return;

        var type = sourceType ?? string.Empty;
        throw new BusinessException(
            $"当前账号没有「{SourceMenuText(type)}」（{SourceMenuCode(type)}）模块授权：拒绝访问"
            + $"{ContainerShipmentReferenceRules.SourceTypeText(type)}的出运引用证据"
            + "（fail closed，不返回 / 不修改任何数据）",
            ErrorCodes.Forbidden);
    }

    /// <summary>指定客户必须落在当前账号范围内（特权账号恒放行；其余 fail closed 拒绝）。</summary>
    public static void EnsureScopeAllowsCustomer(
        ShipmentReferenceAccess access, long? customerId, string sourceTypeText)
    {
        ArgumentNullException.ThrowIfNull(access);
        if (access.AllowsCustomer(customerId)) return;

        throw new BusinessException(
            $"{OutOfScopeText}（{sourceTypeText}客户 Id={(customerId?.ToString() ?? "未知")}）",
            ErrorCodes.Forbidden);
    }

    /// <summary>
    /// 把授权与数据范围下推到出运引用台账查询：受限账号只返回「本人客户的源记录」的引用行，
    /// 且源记录类型必须在其既有源模块菜单授权内（在 <c>Count</c> / 分页之前）。
    /// <para>客户归属一律取源记录的**权威客户**：订柜信息 / 装柜清单取本单 <c>CustomerId</c>；
    /// 预装柜单取其 <see cref="ContainerPreLoading.BookingId"/> 指向的订柜信息客户；没有权威客户归属的
    /// 历史预装柜单对受限账号 fail closed（不猜、不推断）。</para>
    /// </summary>
    public static IQueryable<ContainerShipmentReference> ApplyScope(
        IErpDbContext db, IQueryable<ContainerShipmentReference> source, ShipmentReferenceAccess access)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(access);

        var allowedTypes = access.AllowedSourceTypes.ToList();
        var query = source.Where(r => allowedTypes.Contains(r.SourceType));

        if (access.Scope.AllowedCustomerIds is null) return query;

        var customers = access.Scope.AllowedCustomerIds.ToList();
        return query.Where(r =>
            (r.SourceType == ContainerShipmentReferenceRules.SourceTypeBooking
             && db.ContainerBookings.Any(b => !b.IsDeleted && b.Id == r.SourceId
                                              && customers.Contains(b.CustomerId)))
            || (r.SourceType == ContainerShipmentReferenceRules.SourceTypeLoadingList
                && db.ContainerLoadingLists.Any(l => !l.IsDeleted && l.Id == r.SourceId
                                                     && customers.Contains(l.CustomerId)))
            || (r.SourceType == ContainerShipmentReferenceRules.SourceTypePreLoading
                && db.ContainerPreLoadings.Any(p => !p.IsDeleted && p.Id == r.SourceId && p.BookingId != null
                    && db.ContainerBookings.Any(b => !b.IsDeleted && b.Id == p.BookingId!.Value
                                                     && customers.Contains(b.CustomerId)))));
    }

    /// <summary>订柜信息候选的客户范围下推（特权账号不过滤）。</summary>
    public static IQueryable<ContainerBooking> ApplyScopeToBookings(
        IQueryable<ContainerBooking> source, ShipmentReferenceAccess access)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(access);
        return SalespersonDataScopeService.FilterByCustomer(source, access.Scope, b => b.CustomerId);
    }

    /// <summary>装柜清单候选的客户范围下推（特权账号不过滤）。</summary>
    public static IQueryable<ContainerLoadingList> ApplyScopeToLoadingLists(
        IQueryable<ContainerLoadingList> source, ShipmentReferenceAccess access)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(access);
        return SalespersonDataScopeService.FilterByCustomer(source, access.Scope, l => l.CustomerId);
    }

    /// <summary>
    /// 预装柜单候选的客户范围下推：受限账号只保留 <see cref="ContainerPreLoading.BookingId"/> 指向
    /// 本人客户订柜信息的预装柜单（没有权威归属的历史预装柜单 fail closed）。
    /// </summary>
    public static IQueryable<ContainerPreLoading> ApplyScopeToPreLoadings(
        IErpDbContext db, IQueryable<ContainerPreLoading> source, ShipmentReferenceAccess access)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(access);
        if (access.Scope.AllowedCustomerIds is null) return source;

        var customers = access.Scope.AllowedCustomerIds.ToList();
        return source.Where(p => p.BookingId != null
            && db.ContainerBookings.Any(b => !b.IsDeleted && b.Id == p.BookingId!.Value
                                             && customers.Contains(b.CustomerId)));
    }

    /// <summary>
    /// 解析源记录归属的**权威客户** Id（订柜信息 / 装柜清单取本单客户；预装柜单取所链接订柜信息的客户）；
    /// 没有权威归属（不存在 / 未链接）返回 <c>null</c>。
    /// </summary>
    public static async Task<long?> ResolveSourceCustomerIdAsync(
        IErpDbContext db, string sourceType, long sourceId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);

        switch (sourceType)
        {
            case ContainerShipmentReferenceRules.SourceTypeBooking:
                return await db.ContainerBookings.AsNoTracking()
                    .Where(o => o.Id == sourceId)
                    .Select(o => (long?)o.CustomerId)
                    .FirstOrDefaultAsync(ct);

            case ContainerShipmentReferenceRules.SourceTypeLoadingList:
                return await db.ContainerLoadingLists.AsNoTracking()
                    .Where(o => o.Id == sourceId)
                    .Select(o => (long?)o.CustomerId)
                    .FirstOrDefaultAsync(ct);

            case ContainerShipmentReferenceRules.SourceTypePreLoading:
                var bookingId = await db.ContainerPreLoadings.AsNoTracking()
                    .Where(o => o.Id == sourceId)
                    .Select(o => o.BookingId)
                    .FirstOrDefaultAsync(ct);
                if (bookingId is not > 0) return null;
                return await db.ContainerBookings.AsNoTracking()
                    .Where(o => o.Id == bookingId!.Value)
                    .Select(o => (long?)o.CustomerId)
                    .FirstOrDefaultAsync(ct);

            default:
                return null;
        }
    }

    /// <summary>
    /// 单条源记录的客户数据范围守卫：受限账号只能读写本人客户的源记录，其余 fail closed
    /// （特权账号跳过查询，不产生额外数据库访问）。
    /// </summary>
    public static async Task EnsureScopeAllowsSourceAsync(
        IErpDbContext db, ShipmentReferenceAccess access, string sourceType, long sourceId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(access);
        if (access.Scope.AllowedCustomerIds is null) return;

        var customerId = await ResolveSourceCustomerIdAsync(db, sourceType, sourceId, ct);
        EnsureScopeAllowsCustomer(
            access, customerId, ContainerShipmentReferenceRules.SourceTypeText(sourceType));
    }

    // ==================== 里程碑证据（ERP-058 / ERP-365）：同一授权口径 ====================

    /// <summary>父出运引用已被物理删除时的拒绝文案（无法解析权威客户归属，受限账号 fail closed）</summary>
    public const string MilestoneParentMissingText =
        "父出运引用已不存在，无法解析其源记录的权威客户归属（受限账号 fail closed）";

    /// <summary>当前账号是否具备全部三种既有源模块菜单授权（特权账号继承全部三种）</summary>
    private static bool HasAllSourceMenus(ShipmentReferenceAccess access)
        => ContainerShipmentReferenceRules.SupportedSourceTypes.All(access.AllowsSourceType);

    /// <summary>
    /// 把同一套授权与数据范围下推到**里程碑台账**（ERP-058）：受限账号只返回「父出运引用指向本人客户
    /// 源记录」的里程碑行，且父出运引用的源记录类型必须在其既有源模块菜单授权内
    /// （在 <c>Count</c> / 分页之前，绝不「先查全量再内存过滤」）。
    /// <para>只按**持久化的父出运引用 Id** 关联判定（绝不按柜号 / S/O / B/L 等自由文本匹配，
    /// 也绝不改派父记录）；父出运引用或源记录被删除 / 取消后历史里程碑仍保留可读，
    /// 但只有落在本人客户范围内的受限账号可见 —— 无法解析权威客户的孤儿父记录对受限账号 fail closed，
    /// 绝不降级为全局可见。</para>
    /// <para>特权账号（全部三种源类型 + 不限客户）不做行级过滤，不产生额外数据集访问。</para>
    /// </summary>
    public static IQueryable<ContainerShipmentMilestone> ApplyScopeToMilestones(
        IErpDbContext db, IQueryable<ContainerShipmentMilestone> source, ShipmentReferenceAccess access)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(access);

        // 特权 / 全域账号：不做行级过滤（等价于 ApplyScope 的空过滤分支），避免额外数据集访问。
        if (access.Scope.AllowedCustomerIds is null && HasAllSourceMenus(access)) return source;

        var parents = ApplyScope(db, db.ContainerShipmentReferences.AsNoTracking(), access);
        return source.Where(m => parents.Any(r => r.Id == m.ContainerShipmentReferenceId));
    }

    /// <summary>
    /// 单条里程碑的父出运引用授权守卫（读 / 写同口径）：父引用缺失（历史异常 / 物理删除）时，
    /// 受限账号无法解析权威客户归属 → fail closed，特权账号照常（仅用于历史证据的显式更正）；
    /// 父引用存在时按**持久化的源记录类型 + Id** 校验既有源模块菜单与权威客户数据范围，
    /// 绝不按自由文本匹配、也不改派。
    /// </summary>
    public static async Task EnsureScopeAllowsParentReferenceAsync(
        IErpDbContext db, ShipmentReferenceAccess access, ContainerShipmentReference? parent,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(access);

        if (parent is null)
        {
            if (access.Scope.AllowedCustomerIds is null && HasAllSourceMenus(access)) return;
            throw new BusinessException($"{OutOfScopeText}（{MilestoneParentMissingText}）",
                ErrorCodes.Forbidden);
        }

        RequireSourceType(access, parent.SourceType);
        await EnsureScopeAllowsSourceAsync(db, access, parent.SourceType, parent.SourceId, ct);
    }
}
