using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using System.Linq.Expressions;

namespace ERP.Application.Services;

/// <summary>
/// 附件证据归属授权上下文（ERP-407）：一次解析得到「实时启用身份 + 既有父单据模块菜单授权 +
/// 当前客户数据范围」的权威快照，供附件证据普通路由与附件中心工作台在同一请求内复用（绝不缓存，
/// 每次请求重新解析，授权 / 账号状态 / 客户分配变更后下一次请求立即收敛）。
/// <para>身份只来自已认证请求主体（<c>ClaimTypes.NameIdentifier</c>），请求体中的任何字段都不能指定或扩大身份；
/// <c>AuthorizedOwnerTypes</c> 是当前账号<strong>已获既有菜单授权</strong>的归属单据类型集合。</para>
/// </summary>
public sealed class AttachmentOwnerAccessContext
{
    /// <summary>当前登录账号 Id（正整数）</summary>
    public required long UserId { get; init; }

    /// <summary>当前登录账号名（已按既有口径去首尾空白；缺失时为空串）</summary>
    public required string UserName { get; init; }

    /// <summary>当前账号的客户数据范围（ERP-097 唯一权威口径）</summary>
    public required SalespersonDataScope Scope { get; init; }

    /// <summary>当前账号已获既有菜单授权的归属单据类型（白名单子集；可能为空 = 无任何附件证据可见类型）</summary>
    public required List<string> AuthorizedOwnerTypes { get; init; }

    /// <summary>是否特权账号（超级管理员 / 系统内置角色 / 显式配置的特权角色）：保留既有历史访问，但仍受既有菜单限制</summary>
    public bool IsPrivileged => Scope.IsPrivileged;

    /// <summary>当前账号是否已获该归属类型的既有父单据模块菜单授权（未知 / 空类型一律不授权）</summary>
    public bool AllowsOwnerType(string? ownerType)
    {
        var type = (ownerType ?? string.Empty).Trim();
        return type.Length > 0
               && AuthorizedOwnerTypes.Contains(type, StringComparer.OrdinalIgnoreCase);
    }
}

/// <summary>
/// 附件证据归属授权规则（ERP-407，<strong>唯一权威口径</strong>）：为 ERP-061 / ERP-062 / ERP-063 / ERP-064
/// 交付的<strong>同一</strong>附件证据模型补齐「实时身份 + 既有父单据模块菜单 + 当前客户数据范围」三重授权，
/// 且一律在读取元数据 / 计数 / 文件名 / 摘要 / 内容 / 存储，或写入任何内容之前生效。
/// <list type="number">
/// <item><b>实时身份</b>：缺失 / 非法身份按未认证拒绝；账号不存在 / 已删除按未认证拒绝；账号已禁用按权限不足拒绝；
/// 绝不把空身份当作匿名或管理员，也绝不放宽为全局可见。</item>
/// <item><b>既有模块菜单</b>：归属类型 → 既有父单据模块菜单（销售订单 <c>sales-order</c>、采购订单 <c>purchase-order</c>、
/// 出口单证 <c>doc-center</c>、验货记录（既有采购订单 QC 记录）复用 <c>purchase-order</c>、样品 <c>sample</c>），
/// 复用既有「角色 → 菜单」授权，<strong>不新增</strong>任何菜单 / 角色 / 用户授权；未授权类型不披露记录、计数、
/// 文件名或摘要。</item>
/// <item><b>权威归属与客户范围</b>：销售订单只认持久化 <c>SalesOrder.CustomerId</c>；采购订单 / 验货记录只认
/// 持久化 <c>PurchaseOrder.OwningCustomerId</c> 与按 <c>OwningSalesOrderId</c> 精确解析的销售订单客户
/// （复用 ERP-371 既有特权口径）；出口单证只认持久化 <c>TradeDocument.CustomerId</c>；样品只认持久化
/// <c>Sample.CustomerId</c>。<strong>绝不</strong>按归属号码快照、自由文本、文件名或摘要推断归属；
/// 受限账号遇到权威归属缺失 / 越界一律 fail closed（不披露存在性），特权账号保留既有历史可见性。</item>
/// <item><b>范围先于计数</b>：列表 / 计数 / 分页 / 候选 / 摘要都把归属类型 + 客户范围<strong>下推到数据库</strong>
/// （计数与分页之前），绝不「先查全量再内存过滤」，混合归属摘要中范围外归属连零计数都不返回。</item>
/// </list>
/// <para><b>边界</b>：本类只做纯判定与有界只读查询（<c>AsNoTracking</c>、无写入、无存储访问、无通知）；
/// 不新增表 / 列 / 菜单 / 权限模型，不改写任何父单据的状态 / 金额 / 明细 / 库存 / 财务记录，
/// 也不清理或删除任何历史证据。</para>
/// </summary>
public static class AttachmentOwnerAuthorizationRules
{
    // ==================== 1. 文案（接口 / 文档同源） ====================

    /// <summary>无身份 / 非法身份的拒绝文案</summary>
    public const string UnauthorizedText = "请先登录后再访问附件证据（fail closed）";

    /// <summary>账号不存在 / 已删除的拒绝文案</summary>
    public const string UserDeletedText = "登录账号不存在或已删除，禁止访问附件证据（fail closed）";

    /// <summary>账号已禁用的拒绝文案</summary>
    public const string UserDisabledText = "登录账号已禁用，禁止访问附件证据（fail closed）";

    /// <summary>未授权归属类型 / 未知证据的拒绝文案（不披露附件 Id 与归属类型，避免区分「存在但无权访问」与「不存在」）</summary>
    public const string NotFoundText =
        "附件证据不存在或当前账号无权访问（系统不披露附件 Id、归属类型、文件名与摘要）："
        + "附件证据只显示当前账号已获既有父单据模块菜单授权、且归属客户落在当前客户数据范围内的记录";

    /// <summary>授权与数据范围口径文案（接口 / 文档同源）</summary>
    public const string RuleText =
        "附件证据归属授权：普通路由（台账 / 按归属清单 / 归属候选 / 归属摘要 / 详情 / 上传 / 下载 / 作废）与"
        + "附件中心工作台（台账 / 摘要 / 详情 / 下载）在读取任何元数据、计数、文件名、摘要、内容或存储之前，"
        + "都重新校验实时身份（缺失 / 非法 / 已删除按未认证拒绝，已禁用按权限不足拒绝）、归属类型对应的既有父单据模块菜单授权"
        + "与 ERP-097 客户数据范围；范围在计数 / 分页之前下推到数据库，未授权类型与范围外归属一律 fail closed"
        + "（不披露记录、计数、文件名、摘要与内容）。";

    /// <summary>边界文案（不改写任何业务数据，不新增权限模型）</summary>
    public const string BoundaryText =
        "本护栏只保护附件证据的读取与登记：不新增表 / 列 / 菜单 / 角色 / 用户授权，不把空身份当作管理员，"
        + "不按归属号码快照 / 自由文本 / 文件名推断归属，不改写任何父单据的状态 / 金额 / 明细、库存、出运、单证、"
        + "发票、财务与结算记录，也不清理、改派或硬删除任何历史证据。";

    /// <summary>缺少既有父单据模块菜单授权时的拒绝文案</summary>
    public static string MenuDeniedText(string? ownerType)
    {
        var typeText = AttachmentEvidenceRules.OwnerTypeText(ownerType);
        var menuCode = AttachmentEvidenceRules.RequiredMenuCodeOf(ownerType);
        var menuText = AttachmentEvidenceRules.RequiredMenuTextOf(ownerType);
        var code = menuCode.Length == 0 ? "无对应菜单" : menuCode;
        return $"当前账号没有「{typeText}」所依赖的既有模块菜单授权（{menuText} / {code}）："
               + "拒绝访问该类归属的附件证据（fail closed，不返回 / 不修改任何记录、计数、文件名、摘要与内容）";
    }

    /// <summary>受限账号未映射业务员（无任何可见客户）的拒绝文案</summary>
    public const string UnmappedOperatorText =
        "当前账号未映射为业务员（未分配任何客户），不能访问附件证据（fail closed，不泄露任何范围外归属）";

    /// <summary>受限账号遇到权威归属缺失 / 越界的拒绝文案（fail closed，不披露存在性）</summary>
    public const string OutOfScopeText =
        "该附件证据的归属单据不在当前账号的客户数据范围内或权威归属缺失：拒绝访问"
        + "（fail closed，不泄露范围外归属的记录、计数、文件名、摘要与内容）";

    // ==================== 2. 实时身份 + 既有菜单 + 当前范围 ====================

    /// <summary>
    /// 实时身份校验（只校验身份与账号状态，供无数据读取的模块元数据路由使用）：
    /// 缺失 / 非法按未认证拒绝，账号不存在 / 已删除按未认证拒绝，账号已禁用按权限不足拒绝。
    /// </summary>
    public static async Task EnsureLiveIdentityAsync(
        IErpDbContext db, long? userId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        cancellationToken.ThrowIfCancellationRequested();

        if (userId is null or <= 0)
            throw new BusinessException(UnauthorizedText, ErrorCodes.Unauthorized);

        var user = await db.SysUsers.AsNoTracking()
            .Where(u => u.Id == userId.Value && !u.IsDeleted)
            .Select(u => new { u.Status })
            .FirstOrDefaultAsync(cancellationToken);
        if (user is null)
            throw new BusinessException(UserDeletedText, ErrorCodes.Unauthorized);
        if (user.Status != UserStatus.Enabled)
            throw new BusinessException(UserDisabledText, ErrorCodes.Forbidden);
    }

    /// <summary>
    /// 解析当前请求的权威附件证据访问上下文（fail closed）：
    /// 实时身份（缺失 / 已删除 → 未认证，已禁用 → 权限不足）+ ERP-097 客户数据范围 + 既有菜单授权归属类型。
    /// </summary>
    public static async Task<AttachmentOwnerAccessContext> ResolveAsync(
        IErpDbContext db, long? userId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        cancellationToken.ThrowIfCancellationRequested();

        if (userId is null or <= 0)
            throw new BusinessException(UnauthorizedText, ErrorCodes.Unauthorized);

        var user = await db.SysUsers.AsNoTracking()
            .Where(u => u.Id == userId.Value && !u.IsDeleted)
            .Select(u => new { u.UserName, u.Status })
            .FirstOrDefaultAsync(cancellationToken);
        if (user is null)
            throw new BusinessException(UserDeletedText, ErrorCodes.Unauthorized);
        if (user.Status != UserStatus.Enabled)
            throw new BusinessException(UserDisabledText, ErrorCodes.Forbidden);

        var scope = await SalespersonDataScopeService.ResolveAsync(db, userId.Value);
        var authorized = await LoadAuthorizedOwnerTypesAsync(db, userId.Value, cancellationToken);

        return new AttachmentOwnerAccessContext
        {
            UserId = userId.Value,
            UserName = (user.UserName ?? string.Empty).Trim(),
            Scope = scope,
            AuthorizedOwnerTypes = authorized
        };
    }

    /// <summary>
    /// 宽容解析（只读路由 fail closed 但不抛身份异常时使用）：账号缺失 / 已删除 / 已禁用返回 <c>null</c>
    /// （调用方据此按「无任何可见归属」处理，绝不披露存在性）；身份可用时返回上下文（即使没有任何菜单授权）。
    /// </summary>
    public static async Task<AttachmentOwnerAccessContext?> TryResolveAsync(
        IErpDbContext db, long? userId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (userId is null or <= 0) return null;

        var user = await db.SysUsers.AsNoTracking()
            .Where(u => u.Id == userId.Value && !u.IsDeleted)
            .Select(u => new { u.UserName, u.Status })
            .FirstOrDefaultAsync(cancellationToken);
        if (user is null || user.Status != UserStatus.Enabled) return null;

        var scope = await SalespersonDataScopeService.ResolveAsync(db, userId.Value);
        var authorized = await LoadAuthorizedOwnerTypesAsync(db, userId.Value, cancellationToken);

        return new AttachmentOwnerAccessContext
        {
            UserId = userId.Value,
            UserName = (user.UserName ?? string.Empty).Trim(),
            Scope = scope,
            AuthorizedOwnerTypes = authorized
        };
    }

    /// <summary>
    /// 当前账号已获既有菜单授权的归属单据类型（复用既有「角色 → 菜单」授权，忽略按钮型菜单与已删除记录）；
    /// 无身份 / 无角色 / 无菜单一律返回空集合（fail closed，不授权任何类型）。
    /// </summary>
    public static async Task<List<string>> LoadAuthorizedOwnerTypesAsync(
        IErpDbContext db, long? userId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (userId is null or <= 0) return new List<string>();

        var roleIds = await db.SysUserRoles.AsNoTracking()
            .Where(ur => ur.UserId == userId.Value && !ur.IsDeleted)
            .Select(ur => ur.RoleId)
            .ToListAsync(cancellationToken);
        if (roleIds.Count == 0) return new List<string>();

        var roleSet = roleIds.ToHashSet();
        var menuIds = await db.SysRoleMenus.AsNoTracking()
            .Where(rm => roleSet.Contains(rm.RoleId) && !rm.IsDeleted)
            .Select(rm => rm.MenuId)
            .ToListAsync(cancellationToken);
        if (menuIds.Count == 0) return new List<string>();

        var menuIdSet = menuIds.ToHashSet();
        var menuCodes = await db.SysMenus.AsNoTracking()
            .Where(m => menuIdSet.Contains(m.Id) && !m.IsDeleted && m.MenuType != MenuType.Button)
            .Select(m => m.MenuCode)
            .ToListAsync(cancellationToken);

        var codes = menuCodes
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return AttachmentEvidenceRules.SupportedOwnerTypes
            .Where(type => codes.Contains(AttachmentEvidenceRules.RequiredMenuCodeOf(type)))
            .ToList();
    }

    /// <summary>
    /// 归属类型必须落在当前账号既有菜单授权内，否则 fail closed 拒绝（授权不足，不返回任何记录 / 计数 / 文件名 / 摘要）。
    /// </summary>
    public static void EnsureOwnerTypeAuthorized(AttachmentOwnerAccessContext access, string? ownerType)
    {
        ArgumentNullException.ThrowIfNull(access);
        if (access.AllowsOwnerType(ownerType)) return;

        throw new BusinessException(MenuDeniedText(ownerType), ErrorCodes.Forbidden);
    }

    /// <summary>
    /// 归属类型授权复核（不披露存在性）：未授权一律按「不存在」处理，避免把「存在但无权访问」与「不存在」区分出来。
    /// </summary>
    public static void EnsureOwnerTypeAuthorizedOrNotFound(AttachmentOwnerAccessContext access, string? ownerType)
    {
        ArgumentNullException.ThrowIfNull(access);
        if (access.AllowsOwnerType(ownerType)) return;

        throw BusinessException.NotFound(NotFoundText);
    }

    // ==================== 3. 权威归属解析与客户范围复核 ====================

    /// <summary>
    /// 归属单据的权威归属投影（只读、最小字段）：<see cref="Available"/> 表示父单据存在且未删除；
    /// <see cref="CustomerId"/> 为权威归属客户（销售订单 <c>CustomerId</c> / 采购订单与验货记录
    /// <c>OwningCustomerId</c> / 出口单证与样品 <c>CustomerId</c>）；<see cref="OwningSalesOrderId"/> 仅采购订单 /
    /// 验货记录使用（按精确 Id 解析来源销售订单客户，绝不按号码推断）。
    /// </summary>
    public sealed record AttachmentOwnerOwnership(
        string OwnerType, long OwnerId, bool Available, long? CustomerId, long? OwningSalesOrderId);

    /// <summary>
    /// 按归属类型与精确 Id 装载权威归属投影（未删除）；未知类型 / 非法 Id / 不存在一律返回
    /// <see cref="AttachmentOwnerOwnership.Available"/> = <c>false</c>（不查询、不猜测、不按号码推断）。
    /// </summary>
    public static async Task<AttachmentOwnerOwnership> LoadOwnershipAsync(
        IErpDbContext db, string? ownerType, long ownerId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var type = (ownerType ?? string.Empty).Trim();
        if (ownerId <= 0) return new AttachmentOwnerOwnership(type, ownerId, false, null, null);

        if (string.Equals(type, AttachmentEvidenceRules.OwnerTypeSalesOrder, StringComparison.OrdinalIgnoreCase))
        {
            var row = await db.SalesOrders.AsNoTracking()
                .Where(o => o.Id == ownerId && !o.IsDeleted)
                .Select(o => new { o.CustomerId })
                .FirstOrDefaultAsync(cancellationToken);
            return row is null
                ? new AttachmentOwnerOwnership(type, ownerId, false, null, null)
                : new AttachmentOwnerOwnership(type, ownerId, true, row.CustomerId, null);
        }

        if (string.Equals(type, AttachmentEvidenceRules.OwnerTypePurchaseOrder, StringComparison.OrdinalIgnoreCase)
            || string.Equals(type, AttachmentEvidenceRules.OwnerTypeQualityInspection, StringComparison.OrdinalIgnoreCase))
        {
            var row = await db.PurchaseOrders.AsNoTracking()
                .Where(o => o.Id == ownerId && !o.IsDeleted)
                .Select(o => new { o.OwningCustomerId, o.OwningSalesOrderId })
                .FirstOrDefaultAsync(cancellationToken);
            return row is null
                ? new AttachmentOwnerOwnership(type, ownerId, false, null, null)
                : new AttachmentOwnerOwnership(
                    type, ownerId, true, row.OwningCustomerId, row.OwningSalesOrderId);
        }

        if (string.Equals(type, AttachmentEvidenceRules.OwnerTypeTradeDocument, StringComparison.OrdinalIgnoreCase))
        {
            var customerId = await db.TradeDocuments.AsNoTracking()
                .Where(d => d.Id == ownerId && !d.IsDeleted)
                .Select(d => d.CustomerId)
                .FirstOrDefaultAsync(cancellationToken);
            return customerId is null
                ? new AttachmentOwnerOwnership(type, ownerId, false, null, null)
                : new AttachmentOwnerOwnership(type, ownerId, true, customerId, null);
        }

        if (string.Equals(type, AttachmentEvidenceRules.OwnerTypeSample, StringComparison.OrdinalIgnoreCase))
        {
            var customerId = await db.Samples.AsNoTracking()
                .Where(s => s.Id == ownerId && !s.IsDeleted)
                .Select(s => s.CustomerId)
                .FirstOrDefaultAsync(cancellationToken);
            return customerId is null
                ? new AttachmentOwnerOwnership(type, ownerId, false, null, null)
                : new AttachmentOwnerOwnership(type, ownerId, true, customerId, null);
        }

        return new AttachmentOwnerOwnership(type, ownerId, false, null, null);
    }

    /// <summary>
    /// 权威归属客户范围复核（fail closed）：特权账号保留既有历史可见性；受限账号遇到父单据缺失 / 已删除、
    /// 权威归属缺失或越界时一律拒绝。采购订单 / 验货记录复用 ERP-371 既有权威口径
    /// （显式 <c>OwningCustomerId</c> 与来源销售订单客户都必须落在范围内，无归属备货采购拒绝）。
    /// </summary>
    public static async Task EnsureOwnershipScopeAsync(
        IErpDbContext db, AttachmentOwnerAccessContext access, AttachmentOwnerOwnership ownership,
        bool denyAsNotFound = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(ownership);

        if (access.IsPrivileged) return;
        if (!ownership.Available) throw Denied(denyAsNotFound);

        var type = (ownership.OwnerType ?? string.Empty).Trim();
        if (string.Equals(type, AttachmentEvidenceRules.OwnerTypePurchaseOrder, StringComparison.OrdinalIgnoreCase)
            || string.Equals(type, AttachmentEvidenceRules.OwnerTypeQualityInspection, StringComparison.OrdinalIgnoreCase))
        {
            var stub = new ERP.Domain.Entities.PurchaseOrder
            {
                Id = ownership.OwnerId,
                OwningCustomerId = ownership.CustomerId,
                OwningSalesOrderId = ownership.OwningSalesOrderId
            };
            try
            {
                await PurchaseOrderAuthorizationRules.EnsureOrderScopeAllowedAsync(
                    db, access.Scope, stub, cancellationToken);
            }
            catch (BusinessException) when (denyAsNotFound)
            {
                throw BusinessException.NotFound(NotFoundText);
            }
            return;
        }

        if (string.Equals(type, AttachmentEvidenceRules.OwnerTypeSalesOrder, StringComparison.OrdinalIgnoreCase)
            || string.Equals(type, AttachmentEvidenceRules.OwnerTypeTradeDocument, StringComparison.OrdinalIgnoreCase)
            || string.Equals(type, AttachmentEvidenceRules.OwnerTypeSample, StringComparison.OrdinalIgnoreCase))
        {
            if (ownership.CustomerId is null or <= 0 || !access.Scope.AllowsCustomer(ownership.CustomerId))
                throw Denied(denyAsNotFound);
            return;
        }

        // 未知 / 历史归属类型：无法判定权威归属 → 受限账号 fail closed
        throw Denied(denyAsNotFound);
    }

    /// <summary>按归属类型 + 精确 Id 装载并复核权威归属范围（调用方尚未装载父单据时使用）。</summary>
    public static async Task EnsureOwnerScopeAsync(
        IErpDbContext db, AttachmentOwnerAccessContext access, string? ownerType, long ownerId,
        bool denyAsNotFound = false, CancellationToken cancellationToken = default)
    {
        var ownership = await LoadOwnershipAsync(db, ownerType, ownerId, cancellationToken);
        await EnsureOwnershipScopeAsync(db, access, ownership, denyAsNotFound, cancellationToken);
    }

    private static BusinessException Denied(bool asNotFound)
        => asNotFound
            ? BusinessException.NotFound(NotFoundText)
            : new BusinessException(OutOfScopeText, ErrorCodes.Forbidden);

    // ==================== 4. 证据级授权与范围下推 ====================

    /// <summary>
    /// 正证据 Id 的授权复核（fail closed、不披露存在性）：必须先按归属类型复核既有菜单授权，
    /// 再按权威父单据复核客户数据范围；未授权 / 范围外 / 父单据缺失一律按「不存在」处理。
    /// </summary>
    public static async Task<ERP.Domain.Entities.AttachmentEvidence> EnsureEvidenceAuthorizedAsync(
        IErpDbContext db, AttachmentOwnerAccessContext access, long id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(access);
        if (id <= 0)
            throw BusinessException.InvalidParameter("附件证据 Id 无效：必须为正整数");

        var row = await db.AttachmentEvidences.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == id && !r.IsDeleted, cancellationToken)
            ?? throw BusinessException.NotFound($"附件证据不存在或已删除（Id={id}）");

        await EnsureEvidenceAuthorizedAsync(db, access, row, cancellationToken);
        return row;
    }

    /// <summary>已装载证据行的授权复核（避免重复读取；口径与按 Id 的重载一致）。</summary>
    public static async Task EnsureEvidenceAuthorizedAsync(
        IErpDbContext db, AttachmentOwnerAccessContext access, ERP.Domain.Entities.AttachmentEvidence row,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(row);

        EnsureOwnerTypeAuthorizedOrNotFound(access, row.OwnerType);
        var ownership = await LoadOwnershipAsync(db, row.OwnerType, row.OwnerId, cancellationToken);
        await EnsureOwnershipScopeAsync(db, access, ownership, true, cancellationToken);
    }

    /// <summary>
    /// 证据查询范围下推（计数 / 分页 / 摘要之前调用）：只保留「当前账号已获既有菜单授权的归属类型」，
    /// 受限账号再按权威父单据客户范围收敛（未映射业务员 / 无可见客户 → 空结果）。
    /// <paramref name="requiredOwnerType"/> 非空时只允许该类型（未授权 → 授权不足拒绝）。
    /// </summary>
    public static IQueryable<ERP.Domain.Entities.AttachmentEvidence> ApplyEvidenceScope(
        IErpDbContext db, IQueryable<ERP.Domain.Entities.AttachmentEvidence> source,
        AttachmentOwnerAccessContext access, string? requiredOwnerType)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(access);

        string? required = null;
        if (!string.IsNullOrWhiteSpace(requiredOwnerType))
        {
            required = AttachmentEvidenceRules.NormalizeOwnerType(requiredOwnerType);
            EnsureOwnerTypeAuthorized(access, required);
        }

        var types = required is null ? access.AuthorizedOwnerTypes : new List<string> { required };
        if (types.Count == 0) return source.Where(r => false);

        if (access.IsPrivileged)
            return source.Where(r => types.Contains(r.OwnerType));

        var allowed = access.Scope.AllowedCustomerIds;
        if (allowed is null || allowed.Count == 0) return source.Where(r => false);

        var predicate = BuildRestrictedPredicate(
            db,
            allowed.ToList(),
            Has(types, AttachmentEvidenceRules.OwnerTypeSalesOrder),
            Has(types, AttachmentEvidenceRules.OwnerTypePurchaseOrder),
            Has(types, AttachmentEvidenceRules.OwnerTypeQualityInspection),
            Has(types, AttachmentEvidenceRules.OwnerTypeTradeDocument),
            Has(types, AttachmentEvidenceRules.OwnerTypeSample));

        return predicate is null ? source.Where(r => false) : source.Where(predicate);
    }

    /// <summary>
    /// 受限账号的证据归属谓词：按权威父单据客户范围逐类型收敛（只构造已授权类型的子查询）。
    /// 采购订单 / 验货记录复用 ERP-371 权威口径（<c>OwningCustomerId</c> 与来源销售订单客户都必须落在范围内，
    /// 无归属备货采购拒绝）；出口单证 / 样品要求持久化客户归属存在。
    /// </summary>
    private static Expression<Func<ERP.Domain.Entities.AttachmentEvidence, bool>>? BuildRestrictedPredicate(
        IErpDbContext db, List<long> allowedList,
        bool includeSales, bool includePurchase, bool includeInspection, bool includeDocument, bool includeSample)
    {
        Expression<Func<ERP.Domain.Entities.AttachmentEvidence, bool>>? predicate = null;

        if (includeSales)
        {
            var soTypes = new List<string> { AttachmentEvidenceRules.OwnerTypeSalesOrder };
            var soIds = db.SalesOrders.AsNoTracking()
                .Where(o => !o.IsDeleted && allowedList.Contains(o.CustomerId))
                .Select(o => o.Id);
            predicate = Or(predicate, r => soTypes.Contains(r.OwnerType) && soIds.Contains(r.OwnerId));
        }

        if (includePurchase || includeInspection)
        {
            var poTypes = new List<string>();
            if (includePurchase) poTypes.Add(AttachmentEvidenceRules.OwnerTypePurchaseOrder);
            if (includeInspection) poTypes.Add(AttachmentEvidenceRules.OwnerTypeQualityInspection);

            var existingSoIds = db.SalesOrders.AsNoTracking().Where(o => !o.IsDeleted).Select(o => o.Id);
            var scopedSoIds = db.SalesOrders.AsNoTracking()
                .Where(o => !o.IsDeleted && allowedList.Contains(o.CustomerId))
                .Select(o => o.Id);
            var purchaseIds = db.PurchaseOrders.AsNoTracking()
                .Where(o => !o.IsDeleted
                    && (o.OwningCustomerId.HasValue || o.OwningSalesOrderId.HasValue)
                    && (!o.OwningCustomerId.HasValue || allowedList.Contains(o.OwningCustomerId.Value))
                    && !(o.OwningSalesOrderId.HasValue
                         && existingSoIds.Contains(o.OwningSalesOrderId.Value)
                         && !scopedSoIds.Contains(o.OwningSalesOrderId.Value)))
                .Select(o => o.Id);
            predicate = Or(predicate, r => poTypes.Contains(r.OwnerType) && purchaseIds.Contains(r.OwnerId));
        }

        if (includeDocument)
        {
            var docTypes = new List<string> { AttachmentEvidenceRules.OwnerTypeTradeDocument };
            var docIds = db.TradeDocuments.AsNoTracking()
                .Where(d => !d.IsDeleted && d.CustomerId.HasValue && allowedList.Contains(d.CustomerId.Value))
                .Select(d => d.Id);
            predicate = Or(predicate, r => docTypes.Contains(r.OwnerType) && docIds.Contains(r.OwnerId));
        }

        if (includeSample)
        {
            var sampleTypes = new List<string> { AttachmentEvidenceRules.OwnerTypeSample };
            var sampleIds = db.Samples.AsNoTracking()
                .Where(s => !s.IsDeleted && s.CustomerId.HasValue && allowedList.Contains(s.CustomerId.Value))
                .Select(s => s.Id);
            predicate = Or(predicate, r => sampleTypes.Contains(r.OwnerType) && sampleIds.Contains(r.OwnerId));
        }

        return predicate;
    }

    private static bool Has(IReadOnlyList<string> types, string ownerType)
        => types.Contains(ownerType, StringComparer.OrdinalIgnoreCase);

    // ==================== 5. 归属候选 / 归属摘要的范围收敛 ====================

    /// <summary>受限账号的可见客户集合（特权账号返回 <c>null</c> = 不过滤；空集合 = 看不到任何客户）。</summary>
    private static List<long>? RestrictedCustomers(SalespersonDataScope scope)
        => scope.AllowedCustomerIds is null ? null : scope.AllowedCustomerIds.ToList();

    /// <summary>销售订单候选范围下推（只按持久化 <c>CustomerId</c>；受限账号未分配客户 → 空）。</summary>
    public static IQueryable<SalesOrder> ApplySalesOrderScope(
        IQueryable<SalesOrder> source, SalespersonDataScope scope)
    {
        var allowed = RestrictedCustomers(scope);
        return allowed is null ? source : source.Where(o => allowed.Contains(o.CustomerId));
    }

    /// <summary>
    /// 采购订单 / 验货记录候选范围下推（复用 ERP-371 权威口径：显式 <c>OwningCustomerId</c> 与来源销售订单
    /// 客户都必须落在范围内；受限账号不得访问无归属备货采购）。
    /// </summary>
    public static IQueryable<PurchaseOrder> ApplyPurchaseOrderScope(
        IErpDbContext db, IQueryable<PurchaseOrder> source, SalespersonDataScope scope)
    {
        ArgumentNullException.ThrowIfNull(db);
        var allowed = RestrictedCustomers(scope);
        if (allowed is null) return source;

        var existingSoIds = db.SalesOrders.AsNoTracking().Where(o => !o.IsDeleted).Select(o => o.Id);
        var scopedSoIds = db.SalesOrders.AsNoTracking()
            .Where(o => !o.IsDeleted && allowed.Contains(o.CustomerId))
            .Select(o => o.Id);
        return source.Where(o => (o.OwningCustomerId.HasValue || o.OwningSalesOrderId.HasValue)
            && (!o.OwningCustomerId.HasValue || allowed.Contains(o.OwningCustomerId.Value))
            && !(o.OwningSalesOrderId.HasValue
                 && existingSoIds.Contains(o.OwningSalesOrderId.Value)
                 && !scopedSoIds.Contains(o.OwningSalesOrderId.Value)));
    }

    /// <summary>出口单证候选范围下推（只按持久化 <c>CustomerId</c>；受限账号无权威归属 → 不返回）。</summary>
    public static IQueryable<TradeDocument> ApplyTradeDocumentScope(
        IQueryable<TradeDocument> source, SalespersonDataScope scope)
    {
        var allowed = RestrictedCustomers(scope);
        return allowed is null
            ? source
            : source.Where(d => d.CustomerId.HasValue && allowed.Contains(d.CustomerId.Value));
    }

    /// <summary>样品候选范围下推（只按持久化 <c>CustomerId</c>；受限账号无权威归属 → 不返回）。</summary>
    public static IQueryable<Sample> ApplySampleScope(
        IQueryable<Sample> source, SalespersonDataScope scope)
    {
        var allowed = RestrictedCustomers(scope);
        return allowed is null
            ? source
            : source.Where(s => s.CustomerId.HasValue && allowed.Contains(s.CustomerId.Value));
    }

    /// <summary>
    /// 归属摘要的 Id 收敛（有界只读）：只返回权威父单据落在当前客户数据范围内、且类型已授权的 Id；
    /// 范围外 / 无权威归属的 Id 连零计数都不返回（不泄露存在性）。特权账号保留既有全部请求 Id。
    /// </summary>
    public static async Task<List<long>> FilterAuthorizedOwnerIdsAsync(
        IErpDbContext db, AttachmentOwnerAccessContext access, string? ownerType,
        IReadOnlyList<long> ownerIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(ownerIds);

        var type = AttachmentEvidenceRules.NormalizeOwnerType(ownerType);
        EnsureOwnerTypeAuthorized(access, type);

        var ids = ownerIds.Where(id => id > 0).Distinct().ToList();
        if (ids.Count == 0) return ids;
        if (access.IsPrivileged) return ids;

        var allowed = RestrictedCustomers(access.Scope);
        if (allowed is null) return ids;
        if (allowed.Count == 0) return new List<long>();

        var visible = new List<long>();
        if (string.Equals(type, AttachmentEvidenceRules.OwnerTypeSalesOrder, StringComparison.OrdinalIgnoreCase))
        {
            visible = await db.SalesOrders.AsNoTracking()
                .Where(o => !o.IsDeleted && ids.Contains(o.Id) && allowed.Contains(o.CustomerId))
                .Select(o => o.Id).ToListAsync(cancellationToken);
        }
        else if (string.Equals(type, AttachmentEvidenceRules.OwnerTypePurchaseOrder, StringComparison.OrdinalIgnoreCase)
                 || string.Equals(type, AttachmentEvidenceRules.OwnerTypeQualityInspection, StringComparison.OrdinalIgnoreCase))
        {
            var existingSoIds = db.SalesOrders.AsNoTracking().Where(o => !o.IsDeleted).Select(o => o.Id);
            var scopedSoIds = db.SalesOrders.AsNoTracking()
                .Where(o => !o.IsDeleted && allowed.Contains(o.CustomerId))
                .Select(o => o.Id);
            visible = await db.PurchaseOrders.AsNoTracking()
                .Where(o => !o.IsDeleted && ids.Contains(o.Id)
                    && (o.OwningCustomerId.HasValue || o.OwningSalesOrderId.HasValue)
                    && (!o.OwningCustomerId.HasValue || allowed.Contains(o.OwningCustomerId.Value))
                    && !(o.OwningSalesOrderId.HasValue
                         && existingSoIds.Contains(o.OwningSalesOrderId.Value)
                         && !scopedSoIds.Contains(o.OwningSalesOrderId.Value)))
                .Select(o => o.Id).ToListAsync(cancellationToken);
        }
        else if (string.Equals(type, AttachmentEvidenceRules.OwnerTypeTradeDocument, StringComparison.OrdinalIgnoreCase))
        {
            visible = await db.TradeDocuments.AsNoTracking()
                .Where(d => !d.IsDeleted && ids.Contains(d.Id)
                    && d.CustomerId.HasValue && allowed.Contains(d.CustomerId.Value))
                .Select(d => d.Id).ToListAsync(cancellationToken);
        }
        else if (string.Equals(type, AttachmentEvidenceRules.OwnerTypeSample, StringComparison.OrdinalIgnoreCase))
        {
            visible = await db.Samples.AsNoTracking()
                .Where(s => !s.IsDeleted && ids.Contains(s.Id)
                    && s.CustomerId.HasValue && allowed.Contains(s.CustomerId.Value))
                .Select(s => s.Id).ToListAsync(cancellationToken);
        }

        var visibleSet = visible.ToHashSet();
        return ids.Where(visibleSet.Contains).ToList();
    }

    private static Expression<Func<T, bool>> Or<T>(
        Expression<Func<T, bool>>? left, Expression<Func<T, bool>> right)
    {
        if (left is null) return right;

        var parameter = left.Parameters[0];
        var body = new ParameterReplaceVisitor(right.Parameters[0], parameter).Visit(right.Body)!;
        return Expression.Lambda<Func<T, bool>>(Expression.OrElse(left.Body, body), parameter);
    }

    /// <summary>把右式参数替换为左式参数（组合两个独立谓词的唯一手段；不引入 Expression.Invoke）。</summary>
    private sealed class ParameterReplaceVisitor(ParameterExpression from, ParameterExpression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node)
            => node == from ? to : base.VisitParameter(node);
    }
}
