using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ERP.Application.Services;

/// <summary>
/// 装柜清单（<see cref="ContainerLoadingList"/>，<c>api/container/loading-lists</c>）实时授权与权威客户数据范围护栏（ERP-364）。
/// <para><b>实时授权先于任何读取 / 计数 / 单据号生成 / 单证生成 / 写入</b>：列表 / 详情 / 出运跟踪 / 出运时间线 / 费用分摊证据 /
/// 参与方读取 / 新增 / 修改 / 提交 / 审核 / 取消 / 删除每一个路由都先重新解析：<b>实时身份</b>（缺失 / 非法按未认证拒绝）
/// → <b>账号状态</b>（不存在 / 已删除按未认证，禁用按权限不足）→ 既有「装柜清单」（<c>loading-list</c>）菜单授权
/// （非特权账号必须显式具备）→ 权威客户数据范围（复用 ERP-097 <see cref="SalespersonDataScopeService"/>；未映射业务员的
/// 受限账号 fail closed，绝不降级为全局 / 管理员可见）。</para>
/// <para><b>一柜多客户（参与方）与上游共享出运的权威归属</b>：装柜清单的客户归属既可能来自历史单客户兼容字段
/// <see cref="ContainerLoadingList.CustomerId"/>，也可能来自 ERP-041 的参与方子表（拼柜），还可能经显式
/// <see cref="ContainerLoadingList.PreLoadingId"/> → <see cref="ContainerPreLoading.BookingId"/> →
/// <see cref="ContainerBooking.CustomerId"/> 共享上游出运。为避免受限账号通过共享柜 / 共享出运看到他人客户，受限账号
/// 必须对<b>每一个有效（启用、未删除）参与方客户</b>以及<b>显式可解析的上游订柜客户</b>同时有数据范围权限：
/// 多客户单据按参与方客户逐一判定；历史单客户单据按持久化 <see cref="ContainerLoadingList.CustomerId"/> 判定；
/// 两处都拿不到权威归属（无有效参与方且 CustomerId 非正）时对受限账号 fail closed。</para>
/// <para><b>数据库侧范围先于计数与分页</b>：列表通过 <see cref="ApplyScope"/> 在 <c>Count</c> 之前把范围下推到 SQL，
/// 绝不先查全量再内存过滤。</para>
/// <para><b>绝不按单号 / 柜号等自由文本猜测归属</b>：范围判定只认显式参与方客户与显式 PreLoadingId → BookingId → 订柜客户，
/// 不补链接、不回填、不臆造归属。</para>
/// <para>本类只做<b>纯判定与有界只读查询</b>：不落库、不改单据、不改明细数量、不写库存 / 财务 / 单证，不新增任何表 / 列 / 菜单 /
/// 权限模型，也不把空身份当作管理员；并发串行化（预装柜单行 → 装柜清单行 <c>UPDLOCK, HOLDLOCK</c>）与可串行化事务
/// 由调用方（<c>ContainerLoadingListController</c>）负责。</para>
/// </summary>
public static class LoadingListAuthorizationRules
{
    /// <summary>装柜清单操作所需既有菜单编码（与 <see cref="ContainerLoadingFulfillmentRules.RequiredMenuCode"/> 同源）</summary>
    public const string RequiredMenuCode = ContainerLoadingFulfillmentRules.RequiredMenuCode;

    /// <summary>装柜清单模块菜单中文文案（与既有菜单名一致）</summary>
    public const string RequiredMenuText = ContainerLoadingFulfillmentRules.RequiredMenuText;

    /// <summary>未映射业务员的受限账号拒绝文案（fail closed，不泄露任何范围外单据，也不降级为全局可见）</summary>
    public const string UnmappedOperatorText =
        "当前账号未映射为业务员（装柜清单操作员），不能访问装柜清单（fail closed，不泄露任何范围外单据）";

    /// <summary>无权威客户归属（无有效参与方且兼容客户字段非正）的拒绝文案（受限账号 fail closed）</summary>
    public const string UnlinkedSourceText =
        "装柜清单没有可判定的权威客户归属（无有效参与方且兼容客户字段缺失）：受限账号拒绝访问（fail closed，不泄露无主单据）";

    /// <summary>历史单客户 / 兼容客户字段越范围的拒绝文案（fail closed，不泄露范围外单据）</summary>
    public const string OutOfScopeText =
        "当前账号的客户数据范围不包含该装柜清单的客户：拒绝操作（fail closed，不泄露范围外单据）";

    /// <summary>参与方客户越范围的拒绝文案（fail closed，不泄露共享柜中其他客户的装柜信息）</summary>
    public const string ParticipantOutOfScopeText =
        "当前账号的客户数据范围不包含该装柜清单的有效参与方客户：拒绝操作（fail closed，不泄露共享柜中其他客户的数据）";

    /// <summary>显式上游共享出运客户越范围的拒绝文案（fail closed，不通过共享柜泄露其他客户）</summary>
    public const string UpstreamOutOfScopeText =
        "当前账号的客户数据范围不包含该装柜清单显式上游（预装柜单 → 订柜信息）客户：拒绝操作（fail closed，不泄露共享出运的其他客户）";

    /// <summary>授权与数据范围口径文案（接口 / 文档同源）</summary>
    public const string RuleText =
        "装柜清单（列表 / 详情 / 出运跟踪 / 出运时间线 / 费用分摊证据 / 参与方读取 / 新增 / 修改 / 提交 / 审核 / 取消 / 删除）在读取任何计数、" +
        "参与方或生成单据号 / 单证之前，都会重新校验实时身份（缺失 / 非法 / 已删除按未认证，禁用按权限不足，一律 fail closed）、" +
        "既有「装柜清单」（loading-list）菜单授权与权威客户数据范围（复用 ERP-097）；受限账号必须对每一个有效参与方客户以及" +
        "显式上游（预装柜单 → 订柜信息）客户同时有权限，历史单客户按持久化 CustomerId 判定，无权威归属 fail closed，" +
        "数据库侧范围先于计数与分页，绝不按单号等自由文本猜测归属；特权账号保留历史访问。";

    /// <summary>边界文案（不新增权限 / 表列 / 外部调用，不改写历史证据）</summary>
    public const string BoundaryText =
        "本护栏只保护装柜清单的实时授权与权威客户归属：不新增任何表 / 列 / 菜单 / 权限，不写库存 / 财务 / 单证，" +
        "不调用船公司 / 海关 / 货代等外部系统，不为历史单据补链接或回填字段，也不改写既有单据、参与方与审计时间戳；" +
        "ERP-348 累计已审核装柜数量护栏与 ERP-363 上游预装柜单权威链接护栏全部保留。";

    /// <summary>
    /// 身份 / 账号状态 / 既有「装柜清单」菜单授权 / 权威数据范围四重实时校验（fail closed），返回本次请求的数据范围。
    /// <list type="bullet">
    /// <item>身份缺失 / 非法 → <see cref="ErrorCodes.Unauthorized"/>；</item>
    /// <item>账号不存在或已删除 → <see cref="ErrorCodes.Unauthorized"/>；</item>
    /// <item>账号已禁用 → <see cref="ErrorCodes.Forbidden"/>；</item>
    /// <item>非特权账号无既有 loading-list 菜单授权 → <see cref="ErrorCodes.Forbidden"/>；</item>
    /// <item>非特权账号未映射为业务员 → <see cref="ErrorCodes.Forbidden"/>（fail closed，不降级为全局可见）。</item>
    /// </list>
    /// 每次请求重新解析，授权 / 菜单 / 员工映射变更后下一次请求立即收敛；特权账号继承既有全部访问。
    /// </summary>
    public static async Task<SalespersonDataScope> EnsureAuthorizedAsync(
        IErpDbContext db, long? userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ct.ThrowIfCancellationRequested();

        if (userId is null or <= 0)
            throw new BusinessException("请先登录后再访问装柜清单", ErrorCodes.Unauthorized);

        var user = await db.SysUsers.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == userId.Value && !u.IsDeleted, ct);
        if (user is null)
            throw new BusinessException("登录账号不存在或已删除，禁止访问装柜清单", ErrorCodes.Unauthorized);
        if (user.Status != UserStatus.Enabled)
            throw new BusinessException(
                $"登录账号已禁用，禁止访问{RequiredMenuText}（fail closed）", ErrorCodes.Forbidden);

        var scope = await SalespersonDataScopeService.ResolveAsync(db, userId.Value);

        // 特权账号继承既有全部访问（与 ERP-097 数据范围同源）；普通账号必须显式具备装柜清单菜单并已映射业务员。
        if (!scope.IsPrivileged)
        {
            var menuCodes = await CustomerReceivableReconciliationService
                .LoadAuthorizedMenuCodesAsync(db, userId.Value);
            if (!menuCodes.Contains(RequiredMenuCode, StringComparer.OrdinalIgnoreCase))
            {
                throw new BusinessException(
                    $"当前账号没有「{RequiredMenuText}」（{RequiredMenuCode}）模块授权：拒绝访问{RequiredMenuText}" +
                    "（fail closed，不返回 / 不修改任何装柜清单数据）",
                    ErrorCodes.Forbidden);
            }

            if (scope.SalesmanId is null or <= 0)
                throw new BusinessException(UnmappedOperatorText, ErrorCodes.Forbidden);
        }

        return scope;
    }


    /// <summary>
    /// 把当前账号的数据范围下推到 <b>SQL 侧</b>（列表计数 / 分页之前）：特权账号（<c>AllowedCustomerIds == null</c>）不过滤；
    /// 受限账号只保留满足下列全部条件的装柜清单：① 没有任何「启用、未删除」的参与方客户在范围外；② 若存在有效参与方则放行，
    /// 否则要求持久化 <see cref="ContainerLoadingList.CustomerId"/> 在范围内；③ 显式可解析的上游订柜客户（若有）也在范围内。
    /// 绝不先查全量再内存过滤。
    /// </summary>
    public static IQueryable<ContainerLoadingList> ApplyScope(
        IQueryable<ContainerLoadingList> source, IErpDbContext db, SalespersonDataScope scope)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(scope);
        if (scope.AllowedCustomerIds is null) return source;

        var allowed = scope.AllowedCustomerIds.ToList();

        return from l in source
               let hasActiveParticipant = db.ContainerLoadingListParticipants.Any(p =>
                   p.LoadingListId == l.Id
                   && !p.IsDeleted
                   && p.Status == ContainerLoadingParticipantRules.ActiveStatus)
               let hasForeignActiveParticipant = db.ContainerLoadingListParticipants.Any(p =>
                   p.LoadingListId == l.Id
                   && !p.IsDeleted
                   && p.Status == ContainerLoadingParticipantRules.ActiveStatus
                   && !allowed.Contains(p.CustomerId))
               let hasForeignUpstream = (from pre in db.ContainerPreLoadings
                                         join b in db.ContainerBookings on pre.BookingId equals (long?)b.Id
                                         where pre.Id == l.PreLoadingId && !pre.IsDeleted && !b.IsDeleted
                                               && !allowed.Contains(b.CustomerId)
                                         select pre.Id).Any()
               where !hasForeignActiveParticipant
                     && !hasForeignUpstream
                     && (hasActiveParticipant || allowed.Contains(l.CustomerId))
               select l;
    }

    /// <summary>
    /// 把「候选客户必须是该装柜清单的<b>权威归属客户</b>」下推到 SQL 侧（ERP-392，用于来源候选的有界只读查询）：
    /// 精确客户必须是该清单的一个<b>有效（启用、未删除）参与方客户</b>；若该清单没有任何有效参与方，则要求持久化
    /// 兼容客户字段 <see cref="ContainerLoadingList.CustomerId"/> 恰好等于该客户（历史单客户视图）。
    /// <para><b>绝不按柜号 / 单号 / 相似度等自由文本猜测归属</b>，也不补链接 / 不回填；客户 Id 非正时返回空集（fail closed）。
    /// 本方法只构造只读查询，不落库、不改单据、不写库存 / 财务。</para>
    /// </summary>
    public static IQueryable<ContainerLoadingList> ApplyAuthoritativeCustomerMembership(
        IQueryable<ContainerLoadingList> source, IErpDbContext db, long customerId)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(db);
        if (customerId <= 0) return source.Where(_ => false);

        return from l in source
               let hasAnyActiveParticipant = db.ContainerLoadingListParticipants.Any(p =>
                   p.LoadingListId == l.Id
                   && !p.IsDeleted
                   && p.Status == ContainerLoadingParticipantRules.ActiveStatus)
               let isActiveParticipant = db.ContainerLoadingListParticipants.Any(p =>
                   p.LoadingListId == l.Id
                   && !p.IsDeleted
                   && p.Status == ContainerLoadingParticipantRules.ActiveStatus
                   && p.CustomerId == customerId)
               where (hasAnyActiveParticipant && isActiveParticipant)
                     || (!hasAnyActiveParticipant && l.CustomerId == customerId)
               select l;
    }

    /// <summary>
    /// 校验「库中已存储单据」的权威客户归属（见 <see cref="ApplyScope"/> 同一口径）是否落在当前账号范围内
    /// （读取 / 详情 / 时间线 / 参与方读取 / 修改 / 删除 / 状态变更之前调用）。特权账号保留历史访问。
    /// </summary>
    public static Task EnsureStoredScopeAllowedAsync(
        IErpDbContext db, SalespersonDataScope scope, ContainerLoadingList entity, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entity);
        return EnsureScopeAllowedCoreAsync(db, scope, entity.Id, entity.CustomerId, entity.PreLoadingId, null, ct);
    }

    /// <summary>
    /// 校验「本次提交的拟议单据」的权威客户归属（新增 / 修改在写入任何字段或替换明细 <b>之前</b>调用）。
    /// 受限账号把单据改派到范围外客户 / 上游订柜、或把兼容客户字段清空（无权威归属）都会被拒绝；特权账号保留既有行为。
    /// </summary>
    public static Task EnsureProposedScopeAllowedAsync(
        IErpDbContext db, SalespersonDataScope scope, ContainerLoadingList entity,
        long proposedCustomerId, long? proposedPreLoadingId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entity);
        return EnsureScopeAllowedCoreAsync(
            db, scope, entity.Id, proposedCustomerId, proposedPreLoadingId, null, ct);
    }

    /// <summary>
    /// 校验参与方维护的「完整拟议范围」（新增 / 修改 / 置主 / 停用 / 启用 / 删除参与方在改写参与方行与兼容客户字段 <b>之前</b>调用）：
    /// 拟议的有效参与方客户集合、拟议的兼容客户字段与显式上游订柜客户都必须落在当前账号范围内；特权账号保留既有行为。
    /// </summary>
    public static Task EnsureParticipantScopeAllowedAsync(
        IErpDbContext db, SalespersonDataScope scope, ContainerLoadingList loadingList,
        IReadOnlyCollection<long> proposedActiveParticipantCustomerIds, long proposedCustomerId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(loadingList);
        ArgumentNullException.ThrowIfNull(proposedActiveParticipantCustomerIds);
        return EnsureScopeAllowedCoreAsync(
            db, scope, loadingList.Id, proposedCustomerId, loadingList.PreLoadingId,
            proposedActiveParticipantCustomerIds, ct);
    }


    /// <summary>读取装柜清单下「启用、未删除」参与方的客户 Id（有界只读查询，不写库）</summary>
    public static Task<List<long>> LoadActiveParticipantCustomerIdsAsync(
        IErpDbContext db, long loadingListId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (loadingListId <= 0) return Task.FromResult(new List<long>());

        return db.ContainerLoadingListParticipants.AsNoTracking()
            .Where(p => p.LoadingListId == loadingListId
                        && !p.IsDeleted
                        && p.Status == ContainerLoadingParticipantRules.ActiveStatus)
            .Select(p => p.CustomerId)
            .ToListAsync(ct);
    }

    /// <summary>
    /// 解析装柜清单的显式上游权威客户：<see cref="ContainerLoadingList.PreLoadingId"/> →
    /// <see cref="ContainerPreLoading.BookingId"/> → <see cref="ContainerBooking.CustomerId"/>（均要求未删除）。
    /// 未链接 / 链接缺失时返回 <c>null</c>（不按自由文本猜测，不补链接）。
    /// </summary>
    public static async Task<long?> ResolveUpstreamCustomerIdAsync(
        IErpDbContext db, long? preLoadingId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (preLoadingId is not > 0) return null;

        var bookingId = await db.ContainerPreLoadings.AsNoTracking()
            .Where(p => p.Id == preLoadingId.Value && !p.IsDeleted)
            .Select(p => p.BookingId)
            .FirstOrDefaultAsync(ct);
        if (bookingId is not > 0) return null;

        return await db.ContainerBookings.AsNoTracking()
            .Where(b => b.Id == bookingId.Value && !b.IsDeleted)
            .Select(b => (long?)b.CustomerId)
            .FirstOrDefaultAsync(ct);
    }

    private static async Task EnsureScopeAllowedCoreAsync(
        IErpDbContext db, SalespersonDataScope scope, long loadingListId,
        long customerId, long? preLoadingId,
        IReadOnlyCollection<long>? proposedActiveParticipantCustomerIds,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(scope);

        // 特权账号保留既有全部访问（含历史单客户与共享柜单据），不受范围限制。
        if (scope.AllowedCustomerIds is null) return;

        IReadOnlyList<long> activeParticipantCustomerIds;
        if (proposedActiveParticipantCustomerIds is not null)
            activeParticipantCustomerIds = proposedActiveParticipantCustomerIds.ToList();
        else if (loadingListId > 0)
            activeParticipantCustomerIds = await LoadActiveParticipantCustomerIdsAsync(db, loadingListId, ct);
        else
            activeParticipantCustomerIds = Array.Empty<long>();

        if (activeParticipantCustomerIds.Count > 0)
        {
            // 一柜多客户：必须对每一个有效参与方客户都有权限（否则视为通过共享柜泄露他人客户）。
            foreach (var participantCustomerId in activeParticipantCustomerIds)
            {
                if (!scope.AllowsCustomer(participantCustomerId))
                    throw new BusinessException(ParticipantOutOfScopeText, ErrorCodes.Forbidden);
            }
        }
        else
        {
            // 历史单客户视图 / 无有效参与方：权威归属只来自持久化兼容客户字段。
            if (customerId <= 0)
                throw new BusinessException(UnlinkedSourceText, ErrorCodes.Forbidden);
            if (!scope.AllowsCustomer(customerId))
                throw new BusinessException(OutOfScopeText, ErrorCodes.Forbidden);
        }

        var upstreamCustomerId = await ResolveUpstreamCustomerIdAsync(db, preLoadingId, ct);
        if (upstreamCustomerId is > 0 && !scope.AllowsCustomer(upstreamCustomerId))
            throw new BusinessException(UpstreamOutOfScopeText, ErrorCodes.Forbidden);
    }
}
