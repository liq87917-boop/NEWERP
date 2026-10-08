using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ERP.Application.Services;

/// <summary>
/// 供应商采购发票实时授权与数据范围护栏（ERP-382）。
/// <para>读取（台账 / 详情 / 可关联订单候选 / 关联预览）/ 新增 / 修改 / 关联整体替换 / 登记 / 作废<strong>每一路由</strong>
/// 都重新校验当前身份（缺失 / 已删除 / 禁用一律 fail closed）、既有「采购订单」（<c>purchase-order</c>）菜单授权与
/// <see cref="SalespersonDataScopeService"/>（ERP-097 唯一权威口径）客户数据范围；台账在计数 / 分页之前把范围下推到数据库，
/// 绝不「先查全量再内存过滤」。</para>
/// <para>发票自身没有客户字段，权威归属只能来自<strong>已持久化的「发票 → 采购订单」关联行</strong>：
/// 关联的每一张采购订单的权威归属客户（显式归属客户 + 权威归属销售订单客户，复用
/// <see cref="PurchaseOrderAuthorizationRules.ResolveOwningCustomerIdsAsync"/>）都必须落在当前账号范围内
/// （fail closed）——受限账号因此既不能读取、也不能改写「无任何采购订单来源」或「来源不在本人客户范围内」的发票；
/// 而真正不受限（特权）的授权账号保留历史无来源发票的可读 / 可操作能力。</para>
/// <para>本类只做<b>纯判定与有界只读查询</b>：不落库、不改写发票 / 关联行 / 采购订单 / 付款单，不新增任何表 / 列 / 菜单 / 权限模型，
/// 也绝不新增任何用户授权；发票原始证据（身份 / 金额 / 关联 / 作废原因 / 审计）保持不变。</para>
/// </summary>
public static class PurchaseInvoiceAuthorizationRules
{
    /// <summary>供应商采购发票模块复用的既有菜单编码（与 <c>SeedData.Menus</c> 同源：采购订单）</summary>
    public const string RequiredMenuCode = PurchaseOrderAuthorizationRules.RequiredMenuCode;

    /// <summary>复用菜单的中文文案（与既有菜单名一致）</summary>
    public const string RequiredMenuText = PurchaseOrderAuthorizationRules.RequiredMenuText;

    /// <summary>未映射业务员的受限账号拒绝文案（fail closed，不泄露任何范围外单据）</summary>
    public const string UnmappedOperatorText = PurchaseOrderAuthorizationRules.UnmappedOperatorText;

    /// <summary>受限账号访问「无任何采购订单来源」发票的拒绝文案（fail closed，无权威来源归属即不泄露）</summary>
    public const string UnlinkedDeniedText =
        "当前账号未授权访问无采购订单来源归属的供应商采购发票（fail closed，无权威采购订单归属即不泄露历史无来源发票）";

    /// <summary>授权与数据范围口径文案（接口 / 文档同源）</summary>
    public const string RuleText =
        "供应商采购发票授权与数据范围护栏：台账 / 详情 / 可关联订单候选 / 关联预览 / 新增 / 修改 / 关联整体替换 / 登记 / 作废" +
        "每一路由都重新校验当前身份（缺失 / 已删除 / 禁用一律 fail closed）、既有「采购订单」（purchase-order）菜单授权与" +
        "SalespersonDataScopeService（ERP-097 唯一权威口径）客户数据范围；台账在计数 / 分页之前把范围下推到数据库；" +
        "发票归属只认已持久化的「发票 → 采购订单」关联行，关联的每一张采购订单的权威归属客户都必须落在当前账号范围内，任一越界即拒绝；" +
        "受限账号不得访问无采购订单来源发票，特权账号的历史无来源发票保持可读；" +
        "关联整体替换同时校验「已存储」与「拟提议」的完整来源范围，被拒绝的调用方绝不改写任何发票 / 关联行 / 采购订单；" +
        "绝不新增权限模型、绝不把空身份当作管理员，也绝不泄露范围外发票。";

    /// <summary>边界文案（不改变发票商业口径、不改写来源采购订单或主数据）</summary>
    public const string BoundaryText =
        "本护栏只保护供应商采购发票的授权与并发边界：不改写发票身份 / 金额 / 状态 / 关联证据 / 作废原因与审计，" +
        "不改写采购订单、付款单、货款申请单、库存与结算数据，不删除历史证据，也不新增任何表 / 列 / 菜单 / 权限；" +
        "付款引用证据仍由 ERP-351 / ERP-066 保护，采购订单取消护栏仍为 ERP-345。";

    /// <summary>
    /// 身份 / 账号状态 / 菜单授权三重校验（fail closed）：缺失或非法身份按未认证拒绝，账号不存在 / 已删除按未认证拒绝，
    /// 禁用账号按权限不足拒绝，缺少既有 <c>purchase-order</c> 菜单授权（含被撤销最后一个菜单）按权限不足拒绝。
    /// 返回解析出的权威数据范围，供调用方在同一请求内复用（绝不缓存）。
    /// </summary>
    public static async Task<SalespersonDataScope> EnsureMenuAuthorizedAsync(
        IErpDbContext db, long? userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (userId is null or <= 0)
            throw new BusinessException("请先登录后再访问供应商采购发票", ErrorCodes.Unauthorized);

        var user = await db.SysUsers.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == userId.Value && !u.IsDeleted, ct);
        if (user is null)
            throw new BusinessException("登录账号不存在或已删除，禁止访问供应商采购发票", ErrorCodes.Unauthorized);
        if (user.Status != UserStatus.Enabled)
            throw new BusinessException("登录账号已禁用，禁止访问供应商采购发票（fail closed）", ErrorCodes.Forbidden);

        // 每次请求重新解析（绝不缓存）：授权 / 员工 / 客户分配变更后下一次请求立即收敛。
        var scope = await SalespersonDataScopeService.ResolveAsync(db, userId.Value);

        var menuCodes = await CustomerReceivableReconciliationService.LoadAuthorizedMenuCodesAsync(db, userId.Value);
        if (!menuCodes.Contains(RequiredMenuCode, StringComparer.OrdinalIgnoreCase))
            throw MenuDenied();

        return scope;
    }

    /// <summary>缺少既有「采购订单」菜单授权时的拒绝（fail closed，不执行任何写入）。</summary>
    private static BusinessException MenuDenied()
        => new($"当前账号没有「{RequiredMenuText}」（{RequiredMenuCode}）模块授权：拒绝访问供应商采购发票"
               + "（fail closed，不执行任何写入）",
            ErrorCodes.Forbidden);

    /// <summary>客户数据范围外的发票来源拒绝（fail closed，不泄露范围外发票归属）。</summary>
    private static BusinessException ScopeDenied()
        => new("当前账号的客户数据范围不包含该供应商采购发票关联的采购订单归属客户（fail closed，不泄露范围外发票）",
            ErrorCodes.Forbidden);

    /// <summary>按发票 Id 读取其已持久化关联行的采购订单 Id（去重、只保留正整数；只读、有界）。</summary>
    public static async Task<List<long>> LoadLinkedOrderIdsAsync(
        IErpDbContext db, long invoiceId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (invoiceId <= 0) return new List<long>();

        return await db.PurchaseInvoiceAllocations.AsNoTracking()
            .Where(a => !a.IsDeleted && a.PurchaseInvoiceId == invoiceId && a.PurchaseOrderId > 0)
            .Select(a => a.PurchaseOrderId)
            .Distinct()
            .ToListAsync(ct);
    }

    /// <summary>
    /// 单据级授权：身份 / 菜单（<see cref="EnsureMenuAuthorizedAsync"/>）+ 发票全部来源采购订单的权威归属客户数据范围。
    /// 受限账号必须至少有一张来源采购订单，且<strong>每一张</strong>来源采购订单的权威归属客户都必须在范围内；
    /// 特权账号保留历史无来源发票的可读 / 可操作能力。
    /// </summary>
    public static async Task EnsureInvoiceAuthorizedAsync(
        IErpDbContext db, long? userId, PurchaseInvoice invoice, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(invoice);
        var scope = await EnsureMenuAuthorizedAsync(db, userId, ct);
        var orderIds = await LoadLinkedOrderIdsAsync(db, invoice.Id, ct);
        await EnsureOrderIdsInScopeAsync(db, scope, orderIds, ct);
    }

    /// <summary>
    /// 按已存储 / 拟提议的采购订单 Id 集合复核权威归属客户范围（关联整体替换的两侧都用同一口径）：
    /// 受限账号必须至少有一张订单，且每一张都必须在范围内；特权账号放行。
    /// </summary>
    public static async Task EnsureOrderIdsAuthorizedAsync(
        IErpDbContext db, long? userId, IReadOnlyCollection<long> orderIds, CancellationToken ct = default)
    {
        var scope = await EnsureMenuAuthorizedAsync(db, userId, ct);
        await EnsureOrderIdsInScopeAsync(db, scope, orderIds, ct);
    }

    private static async Task EnsureOrderIdsInScopeAsync(
        IErpDbContext db, SalespersonDataScope scope, IReadOnlyCollection<long> orderIds, CancellationToken ct)
    {
        // 真正不受限（特权）的授权账号保留历史无来源发票 / 任意来源的访问能力。
        if (scope.IsPrivileged) return;

        if (scope.SalesmanId is null or <= 0)
            throw new BusinessException(UnmappedOperatorText, ErrorCodes.Forbidden);

        var ids = orderIds.Where(id => id > 0).Distinct().ToList();
        if (ids.Count == 0)
            throw new BusinessException(UnlinkedDeniedText, ErrorCodes.Forbidden);

        var orders = await db.PurchaseOrders.AsNoTracking()
            .Where(o => ids.Contains(o.Id) && !o.IsDeleted)
            .Select(o => new PurchaseOrder
            {
                Id = o.Id,
                OwningCustomerId = o.OwningCustomerId,
                OwningSalesOrderId = o.OwningSalesOrderId,
            })
            .ToListAsync(ct);

        // 已存储关联指向的订单被删除时无法证明归属：受限账号一律拒绝（fail closed，不泄露）。
        if (orders.Count != ids.Count) throw ScopeDenied();

        foreach (var order in orders)
        {
            var (explicitId, linkedId) = await PurchaseOrderAuthorizationRules.ResolveOwningCustomerIdsAsync(db, order, ct);
            EnsureOrderScope(scope, explicitId, linkedId);
        }
    }

    private static void EnsureOrderScope(SalespersonDataScope scope, long? explicitCustomerId, long? salesOrderCustomerId)
    {
        if (scope.IsPrivileged) return;

        if (explicitCustomerId is null or <= 0 && salesOrderCustomerId is null or <= 0)
            throw new BusinessException(UnlinkedDeniedText, ErrorCodes.Forbidden);

        if (explicitCustomerId is > 0 && !scope.AllowsCustomer(explicitCustomerId)) throw ScopeDenied();
        if (salesOrderCustomerId is > 0 && !scope.AllowsCustomer(salesOrderCustomerId)) throw ScopeDenied();
    }

    /// <summary>
    /// 台账作用域下推谓词：身份 / 菜单 fail closed 后，把权威来源范围下推到数据库（调用方必须在计数 / 分页之前应用）。
    /// 特权账号返回恒真谓词；受限账号只保留「至少有一条关联行、且所有关联行都指向范围内采购订单」的发票，
    /// 绝不「先查全量再内存过滤」，也绝不泄露无来源发票。
    /// </summary>
    public static async Task<System.Linq.Expressions.Expression<Func<PurchaseInvoice, bool>>> BuildScopePredicateAsync(
        IErpDbContext db, long? userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var scope = await EnsureMenuAuthorizedAsync(db, userId, ct);
        if (scope.IsPrivileged) return _ => true;

        if (scope.SalesmanId is null or <= 0)
            throw new BusinessException(UnmappedOperatorText, ErrorCodes.Forbidden);

        var allowed = scope.AllowedCustomerIds?.ToList() ?? new List<long>();
        if (allowed.Count == 0) return _ => false;

        var linkedSalesOrderIds = await db.SalesOrders.AsNoTracking()
            .Where(o => !o.IsDeleted && allowed.Contains(o.CustomerId))
            .Select(o => o.Id)
            .ToListAsync(ct);

        var allowedOrderIds = await db.PurchaseOrders.AsNoTracking()
            .Where(o => !o.IsDeleted
                        && ((o.OwningCustomerId.HasValue && allowed.Contains(o.OwningCustomerId.Value))
                            || (o.OwningSalesOrderId.HasValue
                                && linkedSalesOrderIds.Contains(o.OwningSalesOrderId.Value))))
            .Select(o => o.Id)
            .ToListAsync(ct);

        // 「至少有一条关联行」且「没有任何一条关联行越界」= 全部来源订单都在范围内。
        var allocations = db.PurchaseInvoiceAllocations;
        return invoice =>
            allocations.Any(a => !a.IsDeleted && a.PurchaseInvoiceId == invoice.Id)
            && !allocations.Any(a => !a.IsDeleted && a.PurchaseInvoiceId == invoice.Id
                                     && !allowedOrderIds.Contains(a.PurchaseOrderId));
    }

    /// <summary>台账作用域下推（<see cref="BuildScopePredicateAsync"/> 的 IQueryable 便捷重载）。</summary>
    public static async Task<IQueryable<PurchaseInvoice>> ApplyScopeAsync(
        IErpDbContext db, IQueryable<PurchaseInvoice> source, long? userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        var predicate = await BuildScopePredicateAsync(db, userId, ct);
        return source.Where(predicate);
    }
}
