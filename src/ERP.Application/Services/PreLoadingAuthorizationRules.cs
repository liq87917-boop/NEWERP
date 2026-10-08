using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ERP.Application.Services;

/// <summary>
/// 预装柜单（<see cref="ContainerPreLoading"/>，<c>api/container/pre-loadings</c>）实时授权与权威客户数据范围护栏（ERP-363）。
/// <para><b>实时授权先于任何读取 / 计数 / 单据号生成 / 写入</b>：列表 / 详情 / 出运时间线 / 新增 / 修改 /
/// 提交 / 审核 / 取消 / 删除每一个路由都先重新解析：<b>实时身份</b>（缺失 / 非法按未认证拒绝）→ <b>账号状态</b>
/// （不存在 / 已删除按未认证，禁用按权限不足）→ 既有「预装柜单」（<c>pre-loading</c>）菜单授权（非特权账号必须显式具备）
/// → 权威客户数据范围（复用 ERP-097 <see cref="SalespersonDataScopeService"/>；未映射业务员的受限账号 fail closed，
/// 绝不降级为全局 / 管理员可见）。</para>
/// <para><b>权威客户归属只来自显式链接</b>：预装柜单自身没有客户列，唯一的上游权威引用是
/// <see cref="ContainerPreLoading.BookingId"/>；受限账号的可见范围按「显式 BookingId → 订柜信息归属客户
/// <see cref="ContainerBooking.CustomerId"/>」判定。受限账号对 <b>未关联（BookingId 为空）或来源订柜已删除</b>
/// 的「无主」单据一律 fail closed 拒绝；<b>特权账号保留历史访问</b>（含历史未关联单据）。</para>
/// <para><b>数据库侧范围先于计数与分页</b>：列表通过 <see cref="ApplyScope"/> 在 <c>Count</c> 之前把范围下推到 SQL
/// （受限账号只统计 / 只返回本人客户订柜下的预装柜单），绝不先查全量再内存过滤。</para>
/// <para><b>绝不按单号等自由文本猜测归属</b>：范围判定只认显式 <see cref="ContainerPreLoading.BookingId"/>，
/// 不按柜号 / 单号文本兜底匹配，也不为历史单据补链接或回填。</para>
/// <para>本类只做<b>纯判定与有界只读查询</b>：不落库、不改单据、不改明细数量、不写库存 / 财务、不新增任何表 / 列 / 菜单 /
/// 权限模型，也不把空身份当作管理员；并发串行化（订柜行 + 预装柜单行 UPDLOCK/HOLDLOCK）与可串行化事务由调用方
/// （<c>ContainerPreLoadingController</c>）负责。</para>
/// </summary>
public static class PreLoadingAuthorizationRules
{
    /// <summary>预装柜单操作所需既有菜单编码（与 <see cref="PreLoadingBookingLinkRules.RequiredMenuCode"/> 同源）</summary>
    public const string RequiredMenuCode = PreLoadingBookingLinkRules.RequiredMenuCode;

    /// <summary>预装柜单模块菜单中文文案（与既有菜单名一致）</summary>
    public const string RequiredMenuText = PreLoadingBookingLinkRules.RequiredMenuText;

    /// <summary>未映射业务员的受限账号拒绝文案（fail closed，不泄露任何范围外单据，也不降级为全局可见）</summary>
    public const string UnmappedOperatorText =
        "当前账号未映射为业务员（预装柜单操作员），不能访问预装柜单（fail closed，不泄露任何范围外单据）";

    /// <summary>未关联 / 来源已删除的「无主」单据拒绝文案（受限账号 fail closed，不泄露无权威归属单据）</summary>
    public const string UnlinkedSourceText =
        "预装柜单未关联显式订柜信息（或其来源订柜已删除）：受限账号无权威客户归属，拒绝访问（fail closed，不泄露无主单据）";

    /// <summary>越客户范围的拒绝文案（fail closed，不泄露范围外单据）</summary>
    public const string OutOfScopeText =
        "当前账号的客户数据范围不包含该预装柜单来源订柜信息的客户：拒绝操作（fail closed，不泄露范围外单据）";

    /// <summary>授权与数据范围口径文案（接口 / 文档同源）</summary>
    public const string RuleText =
        "预装柜单（列表 / 详情 / 出运时间线 / 新增 / 修改 / 提交 / 审核 / 取消 / 删除）在读取任何计数、" +
        "来源字段或生成单据号之前，都会重新校验实时身份（缺失 / 非法 / 已删除按未认证，禁用按权限不足，一律 fail closed）、" +
        "既有「预装柜单」（pre-loading）菜单授权与权威客户数据范围（复用 ERP-097）；受限账号的可见范围只按显式 BookingId 与" +
        "订柜信息权威归属客户判定，未关联 / 来源已删除的无主单据 fail closed，绝不按单号等自由文本猜测归属；" +
        "特权账号保留历史访问，数据库侧范围先于计数与分页。";

    /// <summary>边界文案（不新增权限 / 表列 / 外部调用，不改写历史证据）</summary>
    public const string BoundaryText =
        "本护栏只保护预装柜单的实时授权与权威客户归属：不新增任何表 / 列 / 菜单 / 权限，不写库存 / 财务 / 单证，" +
        "不调用船公司 / 海关 / 货代等外部系统，不为历史单据补链接或回填字段，也不改写既有单据、明细与审计时间戳；" +
        "ERP-353 上游「订柜信息」权威链接护栏（存在 / 未删除 / 已审核 + 柜号链接不冲突）与 ERP-348 下游装柜清单" +
        "取消护栏全部保留。";

    /// <summary>
    /// 身份 / 账号状态 / 既有「预装柜单」菜单授权 / 权威数据范围四重实时校验（fail closed），返回本次请求的数据范围。
    /// <list type="bullet">
    /// <item>身份缺失 / 非法 → <see cref="ErrorCodes.Unauthorized"/>；</item>
    /// <item>账号不存在或已删除 → <see cref="ErrorCodes.Unauthorized"/>；</item>
    /// <item>账号已禁用 → <see cref="ErrorCodes.Forbidden"/>；</item>
    /// <item>非特权账号无既有 pre-loading 菜单授权 → <see cref="ErrorCodes.Forbidden"/>；</item>
    /// <item>非特权账号未映射为业务员 → <see cref="ErrorCodes.Forbidden"/>（fail closed，不降级为全局可见）。</item>
    /// </list>
    /// </summary>
    public static async Task<SalespersonDataScope> EnsureAuthorizedAsync(
        IErpDbContext db, long? userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ct.ThrowIfCancellationRequested();
        if (userId is null or <= 0)
            throw new BusinessException($"请先登录后再访问{RequiredMenuText}", ErrorCodes.Unauthorized);

        var user = await db.SysUsers.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == userId.Value, ct);
        if (user is null || user.IsDeleted)
            throw new BusinessException($"请先登录后再访问{RequiredMenuText}", ErrorCodes.Unauthorized);
        if (user.Status != UserStatus.Enabled)
            throw new BusinessException($"登录账号已禁用，不能访问{RequiredMenuText}（fail closed）", ErrorCodes.Forbidden);

        var scope = await SalespersonDataScopeService.ResolveAsync(db, userId.Value);

        // 特权账号继承既有全部访问（与 ERP-097 数据范围同源）；普通账号必须显式具备预装柜单菜单并已映射业务员。
        if (!scope.IsPrivileged)
        {
            var menuCodes = await CustomerReceivableReconciliationService
                .LoadAuthorizedMenuCodesAsync(db, userId.Value);
            if (!menuCodes.Contains(RequiredMenuCode, StringComparer.OrdinalIgnoreCase))
            {
                throw new BusinessException(
                    $"当前账号没有「{RequiredMenuText}」（{RequiredMenuCode}）模块授权：拒绝操作{RequiredMenuText}" +
                    "（fail closed，不执行任何写入）",
                    ErrorCodes.Forbidden);
            }

            if (scope.SalesmanId is null)
                throw new BusinessException(UnmappedOperatorText, ErrorCodes.Forbidden);
        }

        return scope;
    }

    /// <summary>
    /// 把当前账号的数据范围下推到 <b>SQL 侧</b>（列表计数 / 分页之前）：特权账号（<c>AllowedCustomerIds == null</c>）不过滤；
    /// 受限账号只保留「显式 <see cref="ContainerPreLoading.BookingId"/> 指向一条未删除、且归属客户在范围内的订柜信息」的预装柜单，
    /// 未关联或来源已删除的无主单据一律不在结果集内（fail closed，绝不先查全量再内存过滤）。
    /// </summary>
    public static IQueryable<ContainerPreLoading> ApplyScope(
        IQueryable<ContainerPreLoading> source, IErpDbContext db, SalespersonDataScope scope)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(scope);
        if (scope.AllowedCustomerIds is null) return source;

        var bookings = db.ContainerBookings.AsNoTracking().Where(b => !b.IsDeleted);
        bookings = SalespersonDataScopeService.FilterByCustomer(bookings, scope, b => b.CustomerId);

        return from p in source
               join b in bookings on p.BookingId equals (long?)b.Id
               select p;
    }

    /// <summary>
    /// 校验「库中已存储单据」的权威客户归属是否落在当前账号范围内（读取 / 详情 / 时间线 / 修改 / 删除 /
    /// 状态变更之前调用）。受限账号对未关联或来源已删除的无主单据 fail closed；特权账号保留历史访问。
    /// </summary>
    public static Task EnsureStoredScopeAllowedAsync(
        IErpDbContext db, SalespersonDataScope scope, ContainerPreLoading entity, CancellationToken ct = default)
        => EnsureScopeAllowedAsync(db, scope, entity, ct);

    /// <summary>
    /// 校验「本次提交的拟议单据」的权威客户归属是否落在当前账号范围内（新增 / 修改写入任何字段或替换明细 <b>之前</b>调用）。
    /// 受限账号把单据改派到范围外订柜、或清空订柜链接（无权威归属）都会被拒绝；特权账号保留历史行为。
    /// </summary>
    public static Task EnsureProposedScopeAllowedAsync(
        IErpDbContext db, SalespersonDataScope scope, ContainerPreLoading entity, CancellationToken ct = default)
        => EnsureScopeAllowedAsync(db, scope, entity, ct);

    private static async Task EnsureScopeAllowedAsync(
        IErpDbContext db, SalespersonDataScope scope, ContainerPreLoading entity, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(entity);

        // 特权账号保留既有全部访问（含历史未关联单据），不受范围限制。
        if (scope.AllowedCustomerIds is null) return;

        if (entity.BookingId is not > 0)
            throw new BusinessException(UnlinkedSourceText, ErrorCodes.Forbidden);

        var booking = await db.ContainerBookings.AsNoTracking()
            .FirstOrDefaultAsync(b => b.Id == entity.BookingId.Value && !b.IsDeleted, ct);
        if (booking is null)
            throw new BusinessException(UnlinkedSourceText, ErrorCodes.Forbidden);

        if (!scope.AllowsCustomer(booking.CustomerId))
            throw new BusinessException(OutOfScopeText, ErrorCodes.Forbidden);
    }
}
