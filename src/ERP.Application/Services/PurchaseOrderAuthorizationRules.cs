using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ERP.Application.Services;

/// <summary>
/// 采购订单实时授权与数据范围护栏（ERP-371）。
/// <para>读取（列表 / 详情 / 导出 / 派生只读视图）/ 创建 / 修改 / 提交 / 审核 / 取消 / 删除每一路由都重新校验当前身份
/// （缺失 / 已删除 / 禁用一律 fail closed）、既有「采购订单」（<c>purchase-order</c>）菜单授权与
/// <see cref="SalespersonDataScopeService"/>（ERP-097 唯一权威口径）客户数据范围；列表在计数 / 分页之前
/// 把客户范围下推到数据库，绝不「先查全量再内存过滤」。</para>
/// <para>范围口径同时覆盖<b>显式归属客户</b>（<see cref="PurchaseOrder.OwningCustomerId"/>）与<b>权威归属销售订单客户</b>
/// （<see cref="PurchaseOrder.OwningSalesOrderId"/> 精确解析来源销售订单的 <c>CustomerId</c>）：两者只要有一方在范围外即拒绝
/// （fail closed）；受限账号不得访问「既无显式归属客户、又无权威销售订单来源」的无归属备货采购
/// （<see cref="UnlinkedDeniedText"/>），而特权账号的合规备货采购（procurement-for-stock）保持可用。</para>
/// <para>本类只做<b>纯判定与有界只读查询 + 行锁 SQL 常量</b>：不落库、不改单据 / 明细 / 状态 / 库存 / 流水，
/// 不新增任何表 / 列 / 菜单 / 权限模型，也不新增任何用户授权；原始商业供应商 / 币种 / 单位与既有导出口径保持不变，
/// 显式销售链接的权威快照仍由 <see cref="PurchaseSalesOrderLinkRules"/> 负责，取消护栏仍为 ERP-345。</para>
/// </summary>
public static class PurchaseOrderAuthorizationRules
{
    /// <summary>采购订单模块所需既有菜单编码（与 <c>SeedData.Menus</c> 同源）</summary>
    public const string RequiredMenuCode = "purchase-order";

    /// <summary>采购订单模块菜单中文文案（与既有菜单名一致）</summary>
    public const string RequiredMenuText = "采购订单";

    /// <summary>未映射业务员的受限账号拒绝文案（fail closed，不泄露任何范围外单据）</summary>
    public const string UnmappedOperatorText =
        "当前账号未映射为业务员（采购操作员），不能访问采购订单（fail closed，不泄露任何范围外单据）";

    /// <summary>受限账号访问无归属备货采购的拒绝文案（fail closed，无权威客户归属即不泄露）</summary>
    public const string UnlinkedDeniedText =
        "当前账号未授权访问无归属客户（既未登记归属客户、也未链接已审核销售订单）的备货采购（fail closed）";

    /// <summary>授权与数据范围口径文案（接口 / 文档同源）</summary>
    public const string RuleText =
        "采购订单授权与数据范围护栏：读取（列表 / 详情 / 导出 / 派生只读视图）/ 创建 / 修改 / 提交 / 审核 / 取消 / 删除" +
        "每一路由都会重新校验当前身份（缺失 / 已删除 / 禁用一律 fail closed）、既有「采购订单」（purchase-order）菜单授权与" +
        "SalespersonDataScopeService（ERP-097 唯一权威口径）客户数据范围；列表在计数 / 分页之前把客户范围下推到数据库；" +
        "范围同时覆盖显式归属客户与权威归属销售订单客户，两者只要有一方在范围外即拒绝；" +
        "受限账号不得访问无归属备货采购，特权账号的合规备货采购保持可用；" +
        "创建 / 修改同时校验「已存」与「请求」两侧归属，被拒绝的调用方绝不消耗单据号、绝不改写任何单据 / 明细 / 状态；" +
        "绝不新增权限模型、绝不把空身份当作管理员，也绝不泄露范围外采购订单。";

    /// <summary>边界文案（不改变采购商业口径、不改写来源销售订单或主数据）</summary>
    public const string BoundaryText =
        "本护栏只保护采购订单生命周期：不改写供应商 / 币种 / 单位 / 单价等原始商业语义，不改写归属销售订单、客户、商品主数据，" +
        "不删除历史单据与审计证据，也不新增任何表 / 列 / 菜单 / 权限；显式销售链接的权威快照仍由 ERP-346 负责，" +
        "取消护栏仍为 ERP-345，审核 / 入库 / 付款等下游口径不变。";

    /// <summary>「与请求路径 / 请求形状无关」契约文案（ERP-466，接口 / 文档同源，绝不读取 <c>Request.Path</c> / 环境变量 / 测试开关）。</summary>
    public const string PathIndependenceText =
        "ERP-466：采购订单实时授权只依据「控制器是否绑定到 HTTP 请求管线」，与 Request.Path 是否赋值、以及是否携带可解析身份完全无关 —— " +
        "空路径与已赋值路径对同一身份给出完全相同的判定；绑定到请求管线但身份缺失 / 为零 / 未知 / 已删除一律未认证（2000），" +
        "账号禁用 / 缺少或被撤销既有「采购订单」（purchase-order）菜单一律权限不足（2002），且全部先于任何订单 / 明细 / 派生读取与写入。 " +
        "不存在 Request.Path / 环境变量 / 数据库提供程序 / 测试专用开关旁路，绝无匿名或管理员回退。";

    /// <summary>
    /// 身份 / 账号状态 / 菜单授权三重校验（fail closed）：缺失或非法身份按未认证拒绝，账号不存在 / 已删除按未认证拒绝，
    /// 禁用账号按权限不足拒绝，缺少既有 <c>purchase-order</c> 菜单授权（含被撤销最后一个菜单）按权限不足拒绝。
    /// 返回解析出的权威数据范围，供调用方在同一请求内复用（绝不缓存）。
    /// </summary>
    public static async Task<SalespersonDataScope> EnsureMenuAuthorizedAsync(IErpDbContext db, long? userId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (userId is null or <= 0)
            throw new BusinessException("请先登录后再访问采购订单", ErrorCodes.Unauthorized);

        var user = await db.SysUsers.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == userId.Value && !u.IsDeleted, ct);
        if (user is null)
            throw new BusinessException("登录账号不存在或已删除，禁止访问采购订单", ErrorCodes.Unauthorized);
        if (user.Status != UserStatus.Enabled)
            throw new BusinessException("登录账号已禁用，禁止访问采购订单（fail closed）", ErrorCodes.Forbidden);

        // 每次请求重新解析（绝不缓存）：授权 / 员工 / 客户分配变更后下一次请求立即收敛。
        var scope = await SalespersonDataScopeService.ResolveAsync(db, userId.Value);

        var menuCodes = await CustomerReceivableReconciliationService.LoadAuthorizedMenuCodesAsync(db, userId.Value);
        if (!menuCodes.Contains(RequiredMenuCode, StringComparer.OrdinalIgnoreCase))
            throw MenuDenied();

        return scope;
    }

    /// <summary>缺少既有「采购订单」菜单授权时的拒绝（fail closed，不执行任何写入）。</summary>
    private static BusinessException MenuDenied()
        => new($"当前账号没有「{RequiredMenuText}」（{RequiredMenuCode}）模块授权：拒绝访问采购订单"
               + "（fail closed，不执行任何写入）",
            ErrorCodes.Forbidden);

    /// <summary>
    /// 单据级授权：身份 / 菜单（<see cref="EnsureMenuAuthorizedAsync"/>）+ 权威归属客户数据范围。
    /// 已存单据的显式归属客户与权威归属销售订单客户都必须落在当前账号客户数据范围之内；
    /// 受限账号访问无归属客户（既无显式归属、也无权威销售订单来源）的备货采购一律拒绝。
    /// </summary>
    public static async Task EnsureOrderAuthorizedAsync(IErpDbContext db, long? userId, PurchaseOrder order,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(order);
        var scope = await EnsureMenuAuthorizedAsync(db, userId, ct);
        await EnsureOrderScopeAllowedAsync(db, scope, order, ct);
    }

    /// <summary>批量单据级授权：菜单 / 身份只解析一次，逐单复核权威归属客户范围（列表外批量派生视图使用）。</summary>
    public static async Task EnsureOrdersAuthorizedAsync(IErpDbContext db, long? userId,
        IReadOnlyCollection<PurchaseOrder> orders, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(orders);
        var scope = await EnsureMenuAuthorizedAsync(db, userId, ct);
        foreach (var order in orders)
            await EnsureOrderScopeAllowedAsync(db, scope, order, ct);
    }

    /// <summary>
    /// 已解析数据范围的可用性（ERP-393 复用）：特权账号放行；受限账号未映射为业务员（采购操作员）时
    /// 一律 fail closed（不泄露任何单据），与 <see cref="ApplyScopeAsync"/> 的列表口径完全一致。
    /// </summary>
    public static void EnsureScopeUsable(SalespersonDataScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (!scope.IsPrivileged && scope.SalesmanId is null or <= 0)
            throw new BusinessException(UnmappedOperatorText, ErrorCodes.Forbidden);
    }

    /// <summary>
    /// 单据级范围复核（ERP-393 复用）：在调用方已解析数据范围（例如候选接口已做身份 / 菜单校验）时，
    /// 按「显式归属客户 + 权威归属销售订单客户」两侧复核本单是否落在范围内，任一越界即 fail closed。
    /// 复用本类唯一权威的 <see cref="EnsureCustomerScope"/> 口径，供只读来源解析等派生接口使用。
    /// </summary>
    public static async Task EnsureOrderScopeAllowedAsync(IErpDbContext db, SalespersonDataScope scope,
        PurchaseOrder order, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(order);
        var (explicitId, linkedId) = await ResolveOwningCustomerIdsAsync(db, order, ct);
        EnsureCustomerScope(scope, explicitId, linkedId);
    }

    /// <summary>
    /// 请求侧（创建 / 修改提交的归属）授权：身份 / 菜单 + 显式归属客户与权威归属销售订单客户范围。
    /// 在分配字段、替换明细或消耗单据号之前调用，任一不满足即 fail closed。
    /// </summary>
    public static async Task EnsureProposedAuthorizedAsync(IErpDbContext db, long? userId, PurchaseOrder proposed,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(proposed);
        var scope = await EnsureMenuAuthorizedAsync(db, userId, ct);
        long? linkedId = null;
        if (proposed.OwningSalesOrderId is > 0)
            linkedId = await ResolveSalesOrderCustomerIdAsync(db, proposed.OwningSalesOrderId.Value, ct);
        EnsureCustomerScope(scope, proposed.OwningCustomerId is > 0 ? proposed.OwningCustomerId : null, linkedId);
    }

    /// <summary>
    /// 列表作用域下推：身份 / 菜单 fail closed 后，把权威客户范围下推到数据库（计数 / 分页之前）。
    /// 特权账号不过滤；受限账号只返回显式归属客户或权威归属销售订单客户落在范围内的采购订单，
    /// 绝不「先查全量再内存过滤」。受限未映射业务员一律 fail closed。
    /// </summary>
    public static async Task<IQueryable<PurchaseOrder>> ApplyScopeAsync(IErpDbContext db, IQueryable<PurchaseOrder> source,
        long? userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        var scope = await EnsureMenuAuthorizedAsync(db, userId, ct);
        if (scope.IsPrivileged)
            return source;
        EnsureScopeUsable(scope);

        var allowed = scope.AllowedCustomerIds?.ToList() ?? new List<long>();
        if (allowed.Count == 0)
            return source.Where(o => false);

        var linkedSalesOrderIds = await db.SalesOrders.AsNoTracking()
            .Where(o => !o.IsDeleted && allowed.Contains(o.CustomerId))
            .Select(o => o.Id)
            .ToListAsync(ct);

        return source.Where(o =>
            (o.OwningCustomerId.HasValue && allowed.Contains(o.OwningCustomerId.Value))
            || (o.OwningSalesOrderId.HasValue && linkedSalesOrderIds.Contains(o.OwningSalesOrderId.Value)));
    }

    /// <summary>
    /// 解析单据的权威归属客户：显式 <see cref="PurchaseOrder.OwningCustomerId"/> 与
    /// 按 <see cref="PurchaseOrder.OwningSalesOrderId"/> 精确解析到的来源销售订单 <c>CustomerId</c>。
    /// 绝不按单号 / 名称 / 金额猜测链接；来源不存在 / 已删除时不臆造归属。
    /// </summary>
    public static async Task<(long? ExplicitCustomerId, long? SalesOrderCustomerId)> ResolveOwningCustomerIdsAsync(
        IErpDbContext db, PurchaseOrder order, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(order);

        long? linkedId = null;
        if (order.OwningSalesOrderId is > 0)
            linkedId = await ResolveSalesOrderCustomerIdAsync(db, order.OwningSalesOrderId.Value, ct);

        return (order.OwningCustomerId is > 0 ? order.OwningCustomerId : null, linkedId);
    }

    /// <summary>按销售订单 Id 精确解析其权威客户；不存在 / 已删除时返回 null（不臆造归属）。</summary>
    public static async Task<long?> ResolveSalesOrderCustomerIdAsync(IErpDbContext db, long salesOrderId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (salesOrderId <= 0) return null;

        return await db.SalesOrders.AsNoTracking()
            .Where(o => o.Id == salesOrderId && !o.IsDeleted)
            .Select(o => (long?)o.CustomerId)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>最小归属投影：按 Id 精确读取归属字段，供控制器在不装载全量单据的前提下复核范围。</summary>
    public static async Task<List<PurchaseOrder>> LoadOwnershipAsync(IErpDbContext db, IEnumerable<long> orderIds,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(orderIds);

        var ids = orderIds.Where(id => id > 0).Distinct().ToList();
        if (ids.Count == 0) return new List<PurchaseOrder>();

        return await db.PurchaseOrders.AsNoTracking()
            .Where(o => ids.Contains(o.Id) && !o.IsDeleted)
            .Select(o => new PurchaseOrder
            {
                Id = o.Id,
                OwningCustomerId = o.OwningCustomerId,
                OwningSalesOrderId = o.OwningSalesOrderId,
            })
            .ToListAsync(ct);
    }

    /// <summary>客户数据范围判定：特权账号放行；受限账号的每个权威归属客户都必须可见，无归属一律拒绝（fail closed）。</summary>
    private static void EnsureCustomerScope(SalespersonDataScope scope, long? explicitCustomerId, long? salesOrderCustomerId)
    {
        if (scope.IsPrivileged) return;

        if (explicitCustomerId is null or <= 0 && salesOrderCustomerId is null or <= 0)
            throw new BusinessException(UnlinkedDeniedText, ErrorCodes.Forbidden);

        if (explicitCustomerId is > 0 && !scope.AllowsCustomer(explicitCustomerId))
            throw ScopeDenied();
        if (salesOrderCustomerId is > 0 && !scope.AllowsCustomer(salesOrderCustomerId))
            throw ScopeDenied();
    }

    /// <summary>客户数据范围外的采购订单拒绝（fail closed，不泄露范围外采购订单归属）。</summary>
    private static BusinessException ScopeDenied()
        => new("当前账号的客户数据范围不包含该采购订单归属客户（fail closed，不泄露范围外采购订单）",
            ErrorCodes.Forbidden);
}

