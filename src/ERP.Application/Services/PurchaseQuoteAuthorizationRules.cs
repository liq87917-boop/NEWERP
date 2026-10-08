using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ERP.Application.Services;

/// <summary>
/// 供应商比价（<c>api/purchase/quotes</c>）与比价审批（<c>api/purchase/quote-decisions</c>）的实时授权与
/// 客户数据范围护栏（ERP-416）。
/// <para>背景：比价控制器继承通用 CRUD（列表 / 全部 / 详情 / 新增 / 修改 / 删除 / 批量删除）且带入预填 / 单行转单 /
/// 批次计划 / 批次转单 / 报价历史 / 价格差异 / 转化漏斗等路由此前只有类级 <c>[Authorize]</c>，
/// 审批控制器同样只有类级 <c>[Authorize]</c>：任何已登录账号只要猜到一条**范围外**比价行 Id（或一个批次号），
/// 就能读取其报价、审批状态与派生证据，或直接对其审批 / 转单。</para>
/// <list type="number">
/// <item><b>实时身份 + 既有菜单</b>：身份缺失 / 非法 / 账号不存在 / 已删除按未认证拒绝，账号已禁用按权限不足拒绝；
/// 非特权账号必须实时具备既有「供应商比价」（<c>purchase-quote</c>）功能菜单。比价 → 采购订单的**带入预填与真实转单**
/// 还须实时具备既有「采购订单」（<c>purchase-order</c>）功能菜单（复用
/// <see cref="PurchaseOrderAuthorizationRules.EnsureMenuAuthorizedAsync"/>）。绝不新增菜单 / 角色 / 用户授权，
/// 也绝不把空身份当作匿名或管理员。</item>
/// <item><b>唯一权威数据范围</b>：复用 <see cref="SalespersonDataScopeService"/>（ERP-097）；归属**只按比价行持久化的
/// <see cref="PurchaseQuote.CustomerId"/>** 判定，绝不按 <see cref="PurchaseQuote.CustomerName"/>、金额或
/// <see cref="PurchaseQuote.RefOrderNo"/> 文本推断。列表 / 派生查询在计数 / 分页 / 分组之前把范围下推到数据库。</item>
/// <item><b>null 归属只在显式不受限口径下可见</b>：比价行 <c>CustomerId</c> 为空时，受限账号一律 fail closed
/// （不可见 / 不可审批 / 不可转单），特权账号（<c>AllowedCustomerIds == null</c>）保留既有不受限口径。</item>
/// <item><b>批次整批判定</b>：批次计划 / 批次审批状态 / 批次转单在受限账号下要求该批次**全部未删除行**都在范围内，
/// 混入任何范围外 / 空归属行即**整批拒绝**并返回同一非披露错误，绝不返回隐藏行的计数 / 跳过原因 / 部分行，
/// 也绝不产生任何部分写入 / 单据号消耗。</item>
/// <item><b>非披露错误</b>：范围外 / 已删除 / 不存在的比价行（与批次）返回**同一**受控错误
/// （<see cref="NotFoundText"/>），授权先于任何计数 / 分页 / 发号 / 明细替换 / 状态变更 / 转单。</item>
/// <item><b>RefOrderNo 文本不授予归属</b>：转换后的 <see cref="PurchaseQuote.RefOrderNo"/> 可能是采购单号，
/// 转换前是关联销售订单号；本护栏一律不以该文本推断归属或订单所有权，归属只取持久化 <c>CustomerId</c>，
/// 权威转换证据仍由既有 <c>PurchaseQuoteConversion.FindGeneratedOrderAsync</c> 负责。</item>
/// </list>
/// <para><b>边界</b>：本类只做纯判定与有界只读查询，不落库、不改写比价行 / 审批决定 / 采购订单 / 库存 / 财务记录，
/// 不新增表 / 列 / 索引 / 菜单 / 权限模型，也不改变既有转换与审批口径。调用方（控制器）负责在任何读取 / 写入之前调用。</para>
/// </summary>
public static class PurchaseQuoteAuthorizationRules
{
    /// <summary>供应商比价模块所需的既有菜单编码（与 <c>SeedData.Menus</c> 同源）。</summary>
    public const string RequiredMenuCode = "purchase-quote";

    /// <summary>供应商比价模块菜单中文文案（与既有菜单名一致）。</summary>
    public const string RequiredMenuText = "供应商比价";

    /// <summary>比价 → 采购订单所需的既有「采购订单」菜单编码（与 <c>PurchaseOrderAuthorizationRules</c> 同源）。</summary>
    public const string DestinationMenuCode = PurchaseOrderAuthorizationRules.RequiredMenuCode;

    /// <summary>比价 → 采购订单所需的既有「采购订单」菜单中文文案。</summary>
    public const string DestinationMenuText = PurchaseOrderAuthorizationRules.RequiredMenuText;

    /// <summary>无身份 / 非法身份的拒绝文案。</summary>
    public const string UnauthorizedText = "请先登录后再访问供应商比价";

    /// <summary>账号不存在 / 已删除的拒绝文案。</summary>
    public const string UserDeletedText = "登录账号不存在或已删除，禁止访问供应商比价";

    /// <summary>账号已禁用的拒绝文案。</summary>
    public const string UserDisabledText = "登录账号已禁用，禁止访问供应商比价（fail closed）";

    /// <summary>缺少既有「供应商比价」菜单授权时的拒绝文案。</summary>
    public const string MenuDeniedText =
        "当前账号没有「供应商比价」（purchase-quote）模块授权：拒绝访问供应商比价数据"
        + "（fail closed，不返回任何报价 / 审批 / 派生行）";

    /// <summary>缺少既有「采购订单」菜单授权时比价 → 采购订单的拒绝文案（带入预填与真实转单都拦截）。</summary>
    public const string DestinationMenuDeniedText =
        "当前账号没有「采购订单」（purchase-order）模块授权：拒绝供应商比价带入预填 / 转采购订单"
        + "（fail closed，不发号、不写入）";

    /// <summary>拟提交归属客户不在当前客户数据范围时的拒绝文案（不确认客户是否存在，非披露）。</summary>
    public const string ProposedCustomerDeniedText =
        "当前账号的客户数据范围不包含拟提交的归属客户（fail closed，不写入）";

    /// <summary>
    /// 范围外 / 已删除 / 不存在的比价行与批次统一非披露错误：三者返回同一错误，
    /// 绝不通过差异化的错误 / 计数 / 跳过原因透露不可访问记录的存在性。
    /// </summary>
    public const string NotFoundText = "比价记录不存在";

    /// <summary>授权与数据范围口径文案（接口 / 文档同源）。</summary>
    public const string RuleText =
        "供应商比价护栏：列表 / 全部 / 详情 / 新增 / 修改 / 删除 / 批量删除、带入预填 / 单行转单 / 批次计划 / 批次转单"
        + "以及报价历史 / 价格差异 / 转化漏斗等派生路由，在读取任何计数、分页、分组、候选或写入任何数据之前，"
        + "都重新校验实时身份（缺失 / 非法 / 已删除按未认证，已禁用按权限不足）、既有「供应商比价」（purchase-quote）"
        + "功能菜单授权（非特权账号必须显式具备）与 SalespersonDataScopeService（ERP-097 唯一权威口径）客户数据范围；"
        + "归属只按比价行**持久化 CustomerId** 判定，CustomerName 与 RefOrderNo 文本都不是授权依据，"
        + "列表与派生查询在计数 / 分页 / 分组之前把范围下推到数据库；比价带入预填与真实转单还须具备既有「采购订单」"
        + "菜单与权威目的地范围；批次混入任何范围外 / 空归属行即整批拒绝（无隐藏行计数 / 跳过原因 / 部分写入）；"
        + "范围外 / 已删除 / 不存在的比价行与批次返回同一非披露错误；绝不新增权限模型、绝不把空身份当作管理员。";

    /// <summary>边界文案（不改写报价 / 审批 / 采购订单与下游记录，也不新增权限模型）。</summary>
    public const string BoundaryText =
        "本护栏只保护供应商比价与比价审批的数据与状态入口：不改写报价单价 / 数量 / 金额 / 供应商 / 币种 / 单位等原始商业语义，"
        + "不改写比价审批决定、采购订单与明细、库存与库存成本、财务记录，也不删除历史证据与审计留痕；"
        + "既有转换守卫（重复生成 + 审批参考）与只读派生口径保持不变；不新增任何表 / 列 / 索引 / 菜单 / 角色 / 用户授权。";

    /// <summary>
    /// 实时身份校验（fail closed）：缺失 / 非法身份按未认证拒绝，账号不存在 / 已删除按未认证拒绝，
    /// 已禁用按权限不足拒绝；随后复用 <see cref="SalespersonDataScopeService"/>（ERP-097）解析权威客户范围。
    /// 普通账号仍须显式具备既有菜单 —— 该要求由 <see cref="EnsureAccessAuthorizedAsync"/> 施加。
    /// </summary>
    public static async Task<SalespersonDataScope> EnsureLiveIdentityAsync(
        IErpDbContext db, long? userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ct.ThrowIfCancellationRequested();

        if (userId is null or <= 0)
            throw new BusinessException(UnauthorizedText, ErrorCodes.Unauthorized);

        var user = await db.SysUsers.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == userId.Value && !u.IsDeleted, ct);
        if (user is null)
            throw new BusinessException(UserDeletedText, ErrorCodes.Unauthorized);
        if (user.Status != UserStatus.Enabled)
            throw new BusinessException(UserDisabledText, ErrorCodes.Forbidden);

        return await SalespersonDataScopeService.ResolveAsync(db, userId.Value);
    }

    /// <summary>
    /// 比价数据 / 状态入口的完整授权（实时身份 + 既有「供应商比价」菜单 + 权威客户数据范围）。
    /// 特权账号豁免菜单校验但仍须通过实时身份校验；每次调用都重新查询（无缓存），授权撤销 / 账号停用后立即收敛。
    /// </summary>
    public static async Task<SalespersonDataScope> EnsureAccessAuthorizedAsync(
        IErpDbContext db, long? userId, CancellationToken ct = default)
    {
        var scope = await EnsureLiveIdentityAsync(db, userId, ct);
        if (scope.IsPrivileged) return scope;

        var menuCodes = await CustomerReceivableReconciliationService
            .LoadAuthorizedMenuCodesAsync(db, userId!.Value);
        if (!menuCodes.Contains(RequiredMenuCode, StringComparer.OrdinalIgnoreCase))
            throw new BusinessException(MenuDeniedText, ErrorCodes.Forbidden);

        return scope;
    }

    /// <summary>
    /// 比价 → 采购订单（带入预填 / 单行转单 / 批次计划 / 批次转单）的目的地授权：复用既有
    /// <see cref="PurchaseOrderAuthorizationRules.EnsureMenuAuthorizedAsync"/>（实时身份 + 既有「采购订单」菜单 +
    /// 权威客户范围）。任一不满足即 fail closed（不发号、不写入）。该范围用于复核生成草稿的权威目的地归属。
    /// </summary>
    public static async Task<SalespersonDataScope> EnsureDestinationAuthorizedAsync(
        IErpDbContext db, long? userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        try
        {
            return await PurchaseOrderAuthorizationRules.EnsureMenuAuthorizedAsync(db, userId, ct);
        }
        catch (BusinessException ex) when (ex.Code == ErrorCodes.Forbidden)
        {
            // 统一为比价转单的目的地拒绝文案（不改变 fail closed 结论，也不泄露范围外采购订单）。
            throw new BusinessException(DestinationMenuDeniedText, ErrorCodes.Forbidden);
        }
    }

    /// <summary>
    /// 按**比价行持久化归属**把查询下推到数据库：特权 / 进程内（<paramref name="scope"/> 为 <c>null</c> 或
    /// <c>AllowedCustomerIds</c> 为 <c>null</c>）不过滤（保留既有不受限口径）；受限账号只保留
    /// <see cref="PurchaseQuote.CustomerId"/> 落在范围内且非空的比价行（空归属 fail closed），
    /// 绝不使用 <see cref="PurchaseQuote.CustomerName"/> 或 <see cref="PurchaseQuote.RefOrderNo"/> 文本判定归属。
    /// </summary>
    public static IQueryable<PurchaseQuote> ApplyScope(SalespersonDataScope? scope, IQueryable<PurchaseQuote> query)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (scope is null || scope.AllowedCustomerIds is null) return query;

        var allowed = scope.AllowedCustomerIds.ToList();
        return query.Where(q => q.CustomerId.HasValue && allowed.Contains(q.CustomerId.Value));
    }

    /// <summary>
    /// 单条比价行的归属复核（详情 / 审批 / 带入预填 / 单行转单 / 单行派生之前调用）：比价行不存在 / 已删除，
    /// 或受限账号下 <c>CustomerId</c> 为空 / 范围外，一律返回同一非披露错误；返回在库归属客户供调用方复用。
    /// </summary>
    public static async Task<long?> EnsureQuoteAllowedAsync(
        IErpDbContext db, SalespersonDataScope? scope, long quoteId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (quoteId <= 0) throw BusinessException.NotFound(NotFoundText);

        var row = await db.PurchaseQuotes.AsNoTracking()
            .Where(q => q.Id == quoteId && !q.IsDeleted)
            .Select(q => new { q.Id, q.CustomerId })
            .FirstOrDefaultAsync(ct);
        if (row is null) throw BusinessException.NotFound(NotFoundText);

        if (scope is null || scope.AllowedCustomerIds is null) return row.CustomerId;
        if (!scope.AllowsCustomer(row.CustomerId)) throw BusinessException.NotFound(NotFoundText);
        return row.CustomerId;
    }

    /// <summary>
    /// 一批显式比价行 Id 的归属复核（批量删除 / 批次指定行之前调用）：去重后按 Id 有界读取最小归属投影；
    /// 只要**任一** Id 不存在 / 已删除 / 范围外即**整批**返回同一非披露错误 —— 绝不透露哪些 Id 不可访问，
    /// 也绝不产生部分写入。空集合视为「未请求任何行」，直接放行。
    /// </summary>
    public static async Task EnsureQuotesAllowedAsync(
        IErpDbContext db, SalespersonDataScope? scope, IEnumerable<long> quoteIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(quoteIds);

        var ids = quoteIds.Where(id => id > 0).Distinct().ToList();
        if (ids.Count == 0) return;

        var rows = await db.PurchaseQuotes.AsNoTracking()
            .Where(q => ids.Contains(q.Id) && !q.IsDeleted)
            .Select(q => new { q.Id, q.CustomerId })
            .ToListAsync(ct);

        if (rows.Count != ids.Count) throw BusinessException.NotFound(NotFoundText);
        if (scope is null || scope.AllowedCustomerIds is null) return;
        if (rows.Any(r => !scope.AllowsCustomer(r.CustomerId))) throw BusinessException.NotFound(NotFoundText);
    }

    /// <summary>
    /// 拟提交客户（新增 / 改派）的归属复核：受限账号必须显式提供且落在客户数据范围内
    /// （空归属 fail closed，仅在特权 / 进程内不受限口径下可用）；绝不采信 <see cref="PurchaseQuote.CustomerName"/>。
    /// </summary>
    public static void EnsureProposedCustomerAllowed(SalespersonDataScope? scope, long? customerId)
    {
        if (scope is null || scope.AllowedCustomerIds is null) return;
        if (!scope.AllowsCustomer(customerId))
            throw new BusinessException(ProposedCustomerDeniedText, ErrorCodes.Forbidden);
    }

    /// <summary>
    /// 批次整批归属复核（批次计划 / 批次审批状态 / 批次转单之前调用）：解析出的批次**全部未删除行**都必须在
    /// 当前客户数据范围内（含显式 <paramref name="lineIds"/> 指定行），混入任何范围外 / 空归属行即整批返回同一
    /// 非披露错误；绝不返回隐藏行的计数 / 跳过原因，也绝不产生部分写入。批次不存在 / 无行时返回批次号，
    /// 由既有服务层按自身口径拒绝（不在此处臆造）。
    /// </summary>
    public static async Task<string?> EnsureBatchAllowedAsync(
        IErpDbContext db, SalespersonDataScope? scope, string? quoteNo, long? lineId,
        IReadOnlyCollection<long>? lineIds = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);

        var batchNo = (quoteNo ?? string.Empty).Trim();
        if (batchNo.Length == 0)
        {
            if (lineId is null or <= 0) return null; // 交由既有服务按参数校验口径拒绝
            batchNo = await db.PurchaseQuotes.AsNoTracking()
                .Where(q => q.Id == lineId && !q.IsDeleted)
                .Select(q => q.QuoteNo)
                .FirstOrDefaultAsync(ct) ?? throw BusinessException.NotFound(NotFoundText);
        }

        if (lineIds is { Count: > 0 })
            await EnsureQuotesAllowedAsync(db, scope, lineIds, ct);

        if (scope is null || scope.AllowedCustomerIds is null) return batchNo;

        var ownerIds = await db.PurchaseQuotes.AsNoTracking()
            .Where(q => q.QuoteNo == batchNo && !q.IsDeleted)
            .Select(q => q.CustomerId)
            .ToListAsync(ct);
        if (ownerIds.Count == 0) return batchNo;

        if (ownerIds.Any(customerId => !scope.AllowsCustomer(customerId)))
            throw BusinessException.NotFound(NotFoundText);

        return batchNo;
    }

    /// <summary>
    /// 转换草稿的权威目的地范围复核：复用 <see cref="PurchaseOrderAuthorizationRules.EnsureOrderScopeAllowedAsync"/>
    /// （显式归属客户 + 权威归属销售订单客户）。在分配采购单号 / 落库之前调用，任一越界即 fail closed。
    /// </summary>
    public static Task EnsureDestinationScopeAllowedAsync(
        IErpDbContext db, SalespersonDataScope destinationScope, PurchaseOrder draft, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(destinationScope);
        ArgumentNullException.ThrowIfNull(draft);
        return PurchaseOrderAuthorizationRules.EnsureOrderScopeAllowedAsync(db, destinationScope, draft, ct);
    }

    /// <summary>
    /// ERP-418：批次转换的权威目的地范围复核（逐组草稿复用单草稿判定，任一越界即 fail closed）。
    /// 在分配采购单号 / 落库<b>之前</b>调用；批次内任一草稿越界即整批拒绝，绝不部分写入。
    /// </summary>
    public static async Task EnsureDestinationScopeAllowedAsync(
        IErpDbContext db, SalespersonDataScope destinationScope, IReadOnlyList<PurchaseOrder> drafts,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(destinationScope);
        ArgumentNullException.ThrowIfNull(drafts);

        foreach (var draft in drafts)
            await EnsureDestinationScopeAllowedAsync(db, destinationScope, draft, ct);
    }

    // ==================== ERP-417：实时可信操作人（审批决定人一律取自登录账号） ====================

    /// <summary>
    /// 实时可信操作人快照：Id 与显示名一律来自**当前登录账号**的持久化行，
    /// 绝不采信请求体中的 <c>DecidedBy</c> / <c>DecidedByName</c> 等任何客户端字段。
    /// </summary>
    public readonly record struct LiveActor(long UserId, string DisplayName);

    /// <summary>
    /// 解析**实时可信操作人**（fail closed）：身份缺失 / 非法 / 账号不存在 / 已删除按未认证拒绝，
    /// 账号已禁用按权限不足拒绝；显示名取 <see cref="SysUser.DisplayName"/>，为空回退登录名。
    /// 绝不新增 / 不修改任何用户授权，也绝不把空身份当作匿名或管理员。
    /// </summary>
    public static async Task<LiveActor> EnsureLiveActorAsync(IErpDbContext db, long? userId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ct.ThrowIfCancellationRequested();

        if (userId is null or <= 0)
            throw new BusinessException(UnauthorizedText, ErrorCodes.Unauthorized);

        var user = await db.SysUsers.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == userId.Value && !u.IsDeleted, ct);
        if (user is null)
            throw new BusinessException(UserDeletedText, ErrorCodes.Unauthorized);
        if (user.Status != UserStatus.Enabled)
            throw new BusinessException(UserDisabledText, ErrorCodes.Forbidden);

        var name = !string.IsNullOrWhiteSpace(user.DisplayName) ? user.DisplayName : user.UserName;
        return new LiveActor(user.Id, (name ?? string.Empty).Trim());
    }
}
