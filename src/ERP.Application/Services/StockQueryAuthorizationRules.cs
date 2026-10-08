using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ERP.Application.Services;

/// <summary>
/// 库存查询实时授权与数据范围护栏（ERP-356）。
/// <para>库存查询的<b>每一个</b>只读端点（列表 / 库存流水证据 / 汇总）在读取任何数量、成本、来源单据字段之前，
/// 都会重新解析：<b>实时身份</b>（缺失 / 非法按未认证拒绝）→ <b>账号状态</b>（禁用 / 已删除按未认证或权限不足拒绝）→
/// <b>既有「库存查询」（stock-query）菜单授权</b>（无角色 / 无授权按权限不足拒绝）→ <b>权威数据范围</b>。</para>
/// <para>数据范围复用既有 <see cref="SalespersonDataScopeService"/>（ERP-097 唯一权威口径）：特权账号（超级管理员 /
/// 系统内置角色 / 显式特权角色）保留既有全量可见性；<b>受限账号</b>在本系统<b>不存在</b>权威的仓库级数据范围
/// （库存行 <c>Stocks</c> / 库存流水 <c>StockMovements</c> 没有客户或业务员归属），因此按
/// 「无权威范围即拒绝」口径 <b>fail closed</b>，拒绝读取全局库存，而<b>绝不</b>从客户 Id 反推仓库归属、
/// 也绝不把受限账号降级为全局可见。</para>
/// <para>本类只做<b>纯判定与只读查询</b>：不落库、不改库存 / 流水 / 单据、不新增任何表 / 列 / 菜单 / 权限模型，
/// 也不把空身份当作管理员。</para>
/// </summary>
public static class StockQueryAuthorizationRules
{
    /// <summary>
    /// 库存查询模块所需既有菜单编码（与 <c>SeedData.Menus</c> 同源）：复用既有「库存查询」菜单，
    /// 与其它库存模块（ERP-029 / ERP-130 / ERP-354 / ERP-355）同一口径，<b>不新增任何权限模型</b>，
    /// 也不把空身份当作管理员。
    /// </summary>
    public const string RequiredMenuCode = "stock-query";

    /// <summary>库存查询模块菜单中文文案（与既有菜单名一致）</summary>
    public const string RequiredMenuText = "库存查询";

    /// <summary>授权与数据范围口径文案（接口 / 文档同源）</summary>
    public const string RuleText =
        "库存查询（列表 / 库存流水证据 / 汇总）在读取任何数量、成本或来源单据字段之前，都会重新校验实时身份" +
        "（缺失 / 已删除 / 非法按未认证，禁用按权限不足，一律 fail closed）、既有「库存查询」（stock-query）菜单授权" +
        "与权威数据范围；受限账号在没有权威仓库级数据范围时拒绝读取全局库存，绝不从客户 Id 反推仓库归属、绝不授予全局可见性。";

    /// <summary>模块边界文案（保持既有响应契约与数量 / 估值语义）</summary>
    public const string BoundaryText =
        "本护栏只新增「读取前的授权与范围判定」：不改变库存数量、可用数量、锁定数量、加权平均成本与库存金额口径，" +
        "不改变既有响应结构（<c>StockView</c> / <c>StockMovement</c> / 汇总字段），不新增表 / 列 / 菜单 / 权限，" +
        "不写库、不删除或改写任何历史单据与库存流水，也不改动请求筛选（仓库 / 单据号 / 移动类型）的既有语义——" +
        "筛选只能收窄，绝不放大已授权范围。";

    /// <summary>受限账号无权威范围时的拒绝说明（接口 / 文档同源，不泄露任何库存事实）</summary>
    public const string NoAuthoritativeScopeText =
        "当前账号不是全量数据范围账号，且系统不存在权威的仓库级数据范围：拒绝读取全局库存（fail closed，不授予全局可见性）";

    /// <summary>已解析的库存查询范围（授权通过后的只读事实）</summary>
    public sealed class StockQueryScope
    {
        /// <summary>已认证且启用的登录用户 Id</summary>
        public long UserId { get; init; }

        /// <summary>登录用户名（诊断 / 文案用；不参与授权判定）</summary>
        public string UserName { get; init; } = string.Empty;

        /// <summary>是否特权账号（全量数据范围，不过滤库存）</summary>
        public bool IsPrivileged { get; init; }

        /// <summary>是否允许读取全局库存（= 特权账号；受限账号无权威范围时一律拒绝）</summary>
        public bool AllowsGlobalStock => IsPrivileged;
    }

    /// <summary>
    /// 身份 / 账号状态 / 模块授权 / 数据范围四重校验（fail closed）。
    /// <list type="bullet">
    /// <item>身份缺失 / 非法 → <see cref="ErrorCodes.Unauthorized"/>；</item>
    /// <item>账号不存在或已删除 → <see cref="ErrorCodes.Unauthorized"/>；</item>
    /// <item>账号已禁用 → <see cref="ErrorCodes.Forbidden"/>；</item>
    /// <item>无既有 stock-query 菜单授权（含无角色）→ <see cref="ErrorCodes.Forbidden"/>；</item>
    /// <item>受限账号（非特权）→ <see cref="ErrorCodes.Forbidden"/>（无权威仓库级数据范围，拒绝全局库存读取）。</item>
    /// </list>
    /// 菜单授权复用既有「角色 → 菜单」口径，每次请求重新查询：撤销后下一次请求立即收敛。
    /// </summary>
    public static async Task<StockQueryScope> EnsureAuthorizedAsync(IErpDbContext db, long? userId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (userId is null or <= 0)
            throw new BusinessException("请先登录后再查询库存", ErrorCodes.Unauthorized);

        var user = await db.SysUsers.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == userId.Value && !u.IsDeleted, ct);
        if (user is null)
            throw new BusinessException("登录账号不存在或已删除，禁止查询库存", ErrorCodes.Unauthorized);
        if (user.Status != UserStatus.Enabled)
            throw new BusinessException("登录账号已禁用，禁止查询库存（fail closed）", ErrorCodes.Forbidden);

        var menuCodes = await CustomerReceivableReconciliationService
            .LoadAuthorizedMenuCodesAsync(db, userId.Value);
        if (!menuCodes.Contains(RequiredMenuCode, StringComparer.OrdinalIgnoreCase))
        {
            throw new BusinessException(
                $"当前账号没有「{RequiredMenuText}」（{RequiredMenuCode}）模块授权：拒绝查询库存" +
                "（fail closed，不返回任何数量 / 成本 / 来源单据字段）",
                ErrorCodes.Forbidden);
        }

        // 数据范围复用既有权威口径（ERP-097）：受限账号在库存维度没有权威范围 → 拒绝全局库存读取。
        var scope = await SalespersonDataScopeService.ResolveAsync(db, userId.Value);
        if (!scope.IsPrivileged)
            throw new BusinessException(NoAuthoritativeScopeText, ErrorCodes.Forbidden);

        return new StockQueryScope { UserId = user.Id, UserName = user.UserName, IsPrivileged = true };
    }

    /// <summary>
    /// 把已解析范围应用到库存行查询：特权（全量）原样返回；受限范围因不存在权威仓库级范围而
    /// fail closed 拒绝（防止后续任何「放宽授权」被静默放大为全局可见性）。
    /// <para>判定发生在<b>任何</b> <c>Count</c> / <c>Sum</c> / <c>Distinct</c> / 分页之前。</para>
    /// </summary>
    public static IQueryable<Stock> ApplyScope(IQueryable<Stock> source, StockQueryScope scope)
    {
        ArgumentNullException.ThrowIfNull(source);
        EnsureScopeAllowsGlobalStock(scope);
        return source;
    }

    /// <summary>
    /// 把已解析范围应用到库存流水查询：与 <see cref="ApplyScope(IQueryable{Stock}, StockQueryScope)"/> 同一口径，
    /// 保证列表、流水证据与汇总使用<b>同一套</b>授权 / 范围策略。
    /// </summary>
    public static IQueryable<StockMovement> ApplyScope(IQueryable<StockMovement> source, StockQueryScope scope)
    {
        ArgumentNullException.ThrowIfNull(source);
        EnsureScopeAllowsGlobalStock(scope);
        return source;
    }

    /// <summary>范围守卫：仅特权（全量）范围被允许读取全局库存；受限范围一律拒绝。</summary>
    private static void EnsureScopeAllowsGlobalStock(StockQueryScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (!scope.AllowsGlobalStock)
            throw new BusinessException(NoAuthoritativeScopeText, ErrorCodes.Forbidden);
    }
}
