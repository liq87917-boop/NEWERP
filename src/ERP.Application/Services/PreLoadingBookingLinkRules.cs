using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ERP.Application.Services;

/// <summary>
/// 预装柜单 ↔ 订柜信息权威链接护栏（ERP-353）。
/// <para>预装柜单的 <see cref="ContainerPreLoading.BookingId"/> 是装柜链路**上游**唯一的持久化引用：
/// 只要显式填写，就必须指向一条**仍然有效**的订柜信息（存在、未删除、已审核），并且落在当前账号
/// 既有的「预装柜单（pre-loading）」菜单授权与客户数据范围之内 —— 否则在创建 / 修改 / 提交 / 审核
/// 任一环节 fail closed 拒绝，绝不把无效来源链接落库、绝不带着失效来源去审核。</para>
/// <para>未链接（<c>BookingId = null</c>）的历史预装柜单保持既有行为（仍校验身份 / 菜单），
/// 不强制补链接、不回填、不做任何猜测。</para>
/// <para>权威字段口径：订柜信息（<see cref="ContainerBooking"/>）上是**哪些字段**就只认哪些字段 ——
/// 订柜信息没有柜号、没有封条号、没有参与方字段，因此本模块**绝不**从订柜信息推导 / 臆造预装柜单的
/// 柜号、封条号或参与方；预装柜单自己的柜号 / 封条号始终是操作员录入的本单运行记录。</para>
/// <para>本类只做<b>纯判定与有界只读查询</b>，不落库、不改单据、不改明细数量、不写库存 / 财务、也不
/// 调用船公司 / 海关 / 货代等外部系统；并发串行化（订柜行 UPDLOCK/HOLDLOCK）与可串行化事务由调用方
/// （<c>ContainerPreLoadingController.Approve</c> / <c>ContainerBookingController.Cancel</c>）负责。</para>
/// </summary>
public static class PreLoadingBookingLinkRules
{
    /// <summary>预装柜单操作所需既有菜单编码（与 SeedData 同源）</summary>
    public const string RequiredMenuCode = "pre-loading";

    /// <summary>预装柜单操作所需菜单的中文文案（与既有菜单名一致）</summary>
    public const string RequiredMenuText = "预装柜单";

    /// <summary>取消订柜信息所需既有菜单编码（与 SeedData 同源）</summary>
    public const string BookingRequiredMenuCode = "booking";

    /// <summary>取消订柜信息所需菜单的中文文案（与既有菜单名一致）</summary>
    public const string BookingRequiredMenuText = "订柜信息";

    /// <summary>权威链接口径说明（界面 / 文档同源）</summary>
    public const string RuleText =
        "预装柜单填写订柜信息（BookingId）后，创建 / 修改 / 提交 / 审核都会重新解析该订柜信息是否为**仍然有效**的权威来源" +
        "（存在、未删除、已审核），并要求当前账号具备既有「预装柜单」菜单授权、客户数据范围包含该订柜信息的客户；" +
        "非正 / 不存在 / 已删除 / 已取消 / 未审核的订柜信息，以及范围外的订柜信息一律拒绝（fail closed）；" +
        "同一订柜信息下已存在**其他仍然有效**的预装柜单且申报了**不同柜号**时，视为权威柜号链接冲突并拒绝；" +
        "未填写订柜信息的历史预装柜单保持既有行为；绝不按单号等自由文本猜测来源，也绝不臆造封条号与参与方数据。";

    /// <summary>权威字段边界说明（订柜信息上没有的字段一律不推导）</summary>
    public const string AuthoritativeFieldsText =
        "只认订柜信息上实际存在的权威字段（订柜号 / 状态 / 客户 / 船公司 / 航次 / 开船日 / 起运港 / 目的港 / " +
        "ERP-040 外贸与物流跟踪列）。订柜信息没有柜号、没有封条号、没有参与方字段：" +
        "因此预装柜单的柜号 / 封条号只作为本单自己的运行记录（由操作员显式录入），" +
        "绝不从订柜信息推导，也绝不臆造封条号、参与方或客户归属。";

    /// <summary>模块边界文案（不写库存 / 财务 / 外部系统，不改写历史证据）</summary>
    public const string BoundaryText =
        "本护栏只保护「预装柜单 ↔ 订柜信息」上游链接的有效性与并发一致性：不写库存 / 库存流水 / 财务 / 结算记录，" +
        "不调用船公司 / 海关 / 货代等外部系统，不按订柜号等自由文本猜测链接，也不由审核推断开船 / 离港；" +
        "订柜信息的取消仍走既有「先取消装柜清单、再取消预装柜单、最后取消订柜信息」显式流程。";

    /// <summary>
    /// 校验创建 / 修改 / 提交时的上游链接（当前账号身份 / 菜单 / 数据范围 + 权威订柜信息 + 柜号链接不冲突）。
    /// <para>未链接（<c>null</c>）的历史预装柜单保持既有行为；显式链接无效时 fail closed 拒绝保存 / 履约，
    /// 且不落库、不改单据、不改明细数量。</para>
    /// </summary>
    /// <param name="currentPreLoadingId">修改 / 审核时本单 Id（用于把本单排除在「冲突链接」判定之外）；创建时传 <c>null</c>。</param>
    public static async Task ValidateLinkAsync(
        IErpDbContext db, ContainerPreLoading entity, long? userId,
        long? currentPreLoadingId = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(entity);

        var scope = await EnsureAuthorizedAsync(db, userId, ct);
        if (entity.BookingId is null) return;

        var booking = await ResolveAuthoritativeBookingOrThrowAsync(db, entity.BookingId, ct);
        EnsureScopeAllowsBooking(scope, booking);
        await EnsureNoConflictingContainerLinkageAsync(db, entity, currentPreLoadingId, ct);
    }

    /// <summary>
    /// 校验审核时的上游链接（调用方必须已在本可串行化事务内取得该订柜信息行更新锁，并重新加载本单）。
    /// <para>与 <see cref="ValidateLinkAsync"/> 同一份权威判定：订柜信息失效 / 范围外 / 柜号链接冲突一律
    /// fail closed 拒绝，绝不带着失效来源进入已审核状态。</para>
    /// </summary>
    public static Task ValidateApprovalAsync(
        IErpDbContext db, ContainerPreLoading entity, long? userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entity);
        return ValidateLinkAsync(db, entity, userId, entity.Id, ct);
    }

    /// <summary>
    /// 身份 / 既有「预装柜单」菜单授权校验（fail closed），返回本次请求的数据范围（每次重新解析，不缓存）。
    /// </summary>
    public static async Task<SalespersonDataScope> EnsureAuthorizedAsync(
        IErpDbContext db, long? userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ct.ThrowIfCancellationRequested();
        if (userId is null or <= 0)
            throw new BusinessException($"请先登录后再操作{RequiredMenuText}", ErrorCodes.Unauthorized);

        var scope = await SalespersonDataScopeService.ResolveAsync(db, userId.Value);

        // 特权账号继承既有全部访问（与 ERP-097 数据范围同源），普通账号必须显式具备预装柜单菜单。
        if (!scope.IsPrivileged)
        {
            var menuCodes = await CustomerReceivableReconciliationService
                .LoadAuthorizedMenuCodesAsync(db, userId.Value);
            if (!menuCodes.Contains(RequiredMenuCode, StringComparer.OrdinalIgnoreCase))
            {
                throw new BusinessException(
                    $"当前账号没有「{RequiredMenuText}」（{RequiredMenuCode}）模块授权：拒绝操作预装柜单" +
                    "（fail closed，不执行任何写入）",
                    ErrorCodes.Forbidden);
            }
        }

        return scope;
    }

    /// <summary>
    /// 解析权威订柜信息：必须为正整数 Id、存在、未删除且 <c>Status = Approved</c>。
    /// 已取消 / 已驳回 / 未审核 / 待提交 / 已提交 / 已删除 / 不存在一律拒绝（不猜、不兜底）。
    /// </summary>
    public static async Task<ContainerBooking> ResolveAuthoritativeBookingOrThrowAsync(
        IErpDbContext db, long? bookingId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (bookingId is null)
            throw BusinessException.InvalidParameter("订柜信息 Id 不能为空");
        if (bookingId is not > 0)
            throw BusinessException.InvalidParameter("订柜信息 Id 必须为正整数");

        var booking = await db.ContainerBookings.AsNoTracking()
            .FirstOrDefaultAsync(o => o.Id == bookingId.Value && !o.IsDeleted, ct);
        if (booking is null)
            throw BusinessException.RuleConflict("订柜信息不存在或已删除，不能作为预装柜单的权威来源");

        if (booking.Status == DocumentStatus.Cancelled)
            throw BusinessException.RuleConflict(
                $"订柜信息 [{booking.BookingNo}] 已取消，不能作为预装柜单的权威来源");
        if (booking.Status != DocumentStatus.Approved)
            throw BusinessException.RuleConflict(
                $"订柜信息 [{booking.BookingNo}] 未审核，不能作为预装柜单的权威来源");

        return booking;
    }

    /// <summary>
    /// 权威柜号链接冲突判定：同一订柜信息下，其他**仍然有效**（未删除且未取消 / 未驳回）的预装柜单若申报了
    /// **不同**柜号，则两者对同一票订柜的柜号口径互相冲突 → 拒绝保存 / 审核。
    /// <para>未申报柜号（空串）视为「无柜号主张」，不构成冲突、也不反向推导柜号；本方法只比较自由文本快照，
    /// 绝不写入、绝不臆造封条号与参与方。</para>
    /// </summary>
    public static async Task EnsureNoConflictingContainerLinkageAsync(
        IErpDbContext db, ContainerPreLoading entity, long? currentPreLoadingId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(entity);
        if (entity.BookingId is not > 0) return;

        var containerNo = NormalizeContainerNo(entity.ContainerNo);
        if (containerNo.Length == 0) return;

        var bookingId = entity.BookingId.Value;
        var others = await db.ContainerPreLoadings.AsNoTracking()
            .Where(o => !o.IsDeleted
                        && o.BookingId == bookingId
                        && o.Status != DocumentStatus.Cancelled
                        && o.Status != DocumentStatus.Rejected
                        && o.Id != entity.Id
                        && (currentPreLoadingId == null || o.Id != currentPreLoadingId.Value))
            .Select(o => new { o.PreLoadingNo, o.ContainerNo })
            .ToListAsync(ct);

        foreach (var other in others)
        {
            var otherNo = NormalizeContainerNo(other.ContainerNo);
            if (otherNo.Length == 0) continue;
            if (string.Equals(otherNo, containerNo, StringComparison.OrdinalIgnoreCase)) continue;

            throw BusinessException.RuleConflict(
                $"订柜信息已由预装柜单 [{other.PreLoadingNo}] 关联柜号 [{other.ContainerNo}]：" +
                $"本单柜号 [{entity.ContainerNo}] 与同一订柜信息下的权威柜号链接冲突，不能保存");
        }
    }

    /// <summary>
    /// 校验订柜信息能否取消（不写库）。调用方必须在同一可串行化事务内持有该订柜信息行更新锁后调用，
    /// 以保证「判定」与「状态变更」原子，并与同一订柜下的预装柜单审核串行化。
    /// <para>存在「以本订柜为权威来源、未删除、已审核」的预装柜单时拒绝；若历史数据里还存在以该订柜下预装柜单
    /// 为来源的**已审核**装柜清单（下游有效引用）也一并拒绝。已取消 / 已驳回 / 待提交 / 已提交 / 已删除的
    /// 预装柜单不算有效引用，不阻断取消；解除只走既有显式取消流程（先装柜清单、再预装柜单、最后订柜信息）。</para>
    /// </summary>
    public static async Task ValidateBookingCancellationAsync(
        IErpDbContext db, ContainerBooking booking, long? userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(booking);

        await EnsureBookingAuthorizedAsync(db, booking, userId, ct);

        var approvedPreLoadings = await db.ContainerPreLoadings.AsNoTracking()
            .Where(o => !o.IsDeleted && o.BookingId == booking.Id && o.Status == DocumentStatus.Approved)
            .OrderBy(o => o.Id)
            .Select(o => o.PreLoadingNo)
            .ToListAsync(ct);
        if (approvedPreLoadings.Count > 0)
        {
            throw BusinessException.RuleConflict(
                $"存在已审核且未取消的预装柜单 [{string.Join("、", approvedPreLoadings)}] 引用本订柜信息：" +
                "请先按既有流程取消预装柜单，再取消订柜信息");
        }

        var downstreamLoadingLists = await (
                from l in db.ContainerLoadingLists
                join p in db.ContainerPreLoadings on l.PreLoadingId equals (long?)p.Id
                where !l.IsDeleted && !p.IsDeleted && p.BookingId == booking.Id
                      && l.Status == DocumentStatus.Approved
                orderby l.Id
                select l.LoadingListNo)
            .ToListAsync(ct);
        if (downstreamLoadingLists.Count > 0)
        {
            throw BusinessException.RuleConflict(
                $"存在已审核且未取消的装柜清单 [{string.Join("、", downstreamLoadingLists)}] 引用本订柜信息下的预装柜单：" +
                "请先按既有流程取消装柜清单，再取消订柜信息");
        }
    }

    /// <summary>客户数据范围：特权账号放行；否则订柜信息的归属客户必须落在当前账号范围内。</summary>
    private static void EnsureScopeAllowsBooking(SalespersonDataScope scope, ContainerBooking booking)
    {
        if (scope.AllowsCustomer(booking.CustomerId))
            return;

        throw new BusinessException(
            "当前账号的客户数据范围不包含该订柜信息的客户：拒绝关联（fail closed，不泄露范围外单据）",
            ErrorCodes.Forbidden);
    }

    /// <summary>取消订柜信息的身份 / 既有「订柜信息」菜单授权 / 客户数据范围校验（不写库）。</summary>
    /// <remarks>
    /// ERP-360：订柜信息模块的授权口径统一收敛到 <see cref="BookingAuthorizationRules.EnsureAuthorizedAsync"/>
    /// （实时身份 → 账号状态 → 既有「订柜信息」菜单 → 权威客户数据范围；特权账号继承既有全部访问，
    /// 非特权账号未映射业务员 fail closed），避免同一模块出现两套口径。
    /// </remarks>
    private static async Task EnsureBookingAuthorizedAsync(
        IErpDbContext db, ContainerBooking booking, long? userId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var scope = await BookingAuthorizationRules.EnsureAuthorizedAsync(db, userId, ct);
        EnsureScopeAllowsBooking(scope, booking);
    }

    private static string NormalizeContainerNo(string? value) => (value ?? string.Empty).Trim();
}
