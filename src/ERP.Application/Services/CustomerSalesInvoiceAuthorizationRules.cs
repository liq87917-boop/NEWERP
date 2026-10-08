using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using System.Linq.Expressions;

namespace ERP.Application.Services;

/// <summary>
/// 客户销项发票实时授权与数据范围护栏（ERP-383）。
/// <para>读取（台账 / 详情 / 收款时效证据 / 商业发票候选）/ 新增 / 修改 / 可分摊订单候选 / 分摊预览 /
/// 分摊整体替换 / 登记 / 作废<strong>每一路由</strong>都重新校验当前身份（缺失 / 已删除 / 禁用一律 fail closed）、
/// 既有「销售订单」（<c>sales-order</c>）模块授权与 <see cref="SalespersonDataScopeService"/>（ERP-097 唯一权威口径）
/// 客户数据范围；台账在计数 / 分页之前把范围下推到数据库，绝不「先查全量再内存过滤」。</para>
/// <para>发票的权威归属客户是<strong>已存储客户</strong>（<see cref="CustomerSalesInvoiceEvidence.CustomerId"/>），
/// 并且必须同时覆盖：<strong>已存储的每一张销售订单分摊来源</strong>（<c>CustomerSalesInvoiceAllocations</c> →
/// <c>SalesOrders.CustomerId</c>）与<strong>显式交叉引用单证的归属客户</strong>
/// （<see cref="TradeDocument.CustomerId"/>）。混源（多客户来源）场景要求<strong>每一个</strong>相关客户都在范围内，
/// 任一越界即拒绝；受限账号不得读取 / 改写无权威归属（订单 / 单证已删除，无法证明归属）的发票，而真正不受限
/// （特权）的授权账号保留历史发票的可读 / 可操作能力。</para>
/// <para>被拒绝的编辑 / 分摊替换绝不改写任何发票、分摊行、销售订单、单证或收款单；本类只做<b>纯判定与有界只读查询</b>，
/// 不落库、不新增任何表 / 列 / 菜单 / 权限模型，也绝不新增任何用户授权或退化为匿名 / 管理员。</para>
/// </summary>
public static class CustomerSalesInvoiceAuthorizationRules
{
    /// <summary>模块复用的既有菜单编码（与 <c>SeedData.Menus</c> 同源：销售订单）</summary>
    public const string RequiredMenuCode = SalesOrderCancellationRules.RequiredMenuCode;

    /// <summary>复用菜单的中文文案（与既有菜单名一致）</summary>
    public const string RequiredMenuText = SalesOrderCancellationRules.RequiredMenuText;

    /// <summary>未映射业务员的受限账号拒绝文案（fail closed，不泄露任何范围外单据）</summary>
    public const string UnmappedOperatorText =
        "当前账号未映射为业务员（销售订单操作员），不能访问客户销项发票（fail closed，不泄露任何范围外发票）";

    /// <summary>受限账号访问「来源订单 / 单证已删除，无法证明归属」发票的拒绝文案（fail closed）</summary>
    public const string UnprovableSourceText =
        "当前账号不能访问来源销售订单或交叉引用单证已删除、无法证明归属客户的客户销项发票"
        + "（fail closed，不可证明来源归属即不泄露历史证据）";

    /// <summary>受限账号访问无权威客户归属单证的拒绝文案（fail closed，无来源即不泄露）</summary>
    public const string UnlinkedTradeDocumentText =
        "当前账号未授权访问没有权威客户归属的单证中心商业发票（fail closed，无权威归属即不泄露）";

    /// <summary>授权与数据范围口径文案（接口 / 文档同源）</summary>
    public const string RuleText =
        "客户销项发票授权与数据范围护栏：台账 / 详情 / 收款时效证据 / 商业发票候选 / 新增 / 修改 / 可分摊订单候选 / "
        + "分摊预览 / 分摊整体替换 / 登记 / 作废每一路由都重新校验当前身份（缺失 / 已删除 / 禁用一律 fail closed）、"
        + "既有「销售订单」（sales-order）菜单授权与 SalespersonDataScopeService（ERP-097 唯一权威口径）客户数据范围；"
        + "台账在计数 / 分页之前把范围下推到数据库；发票归属同时校验已存储客户、全部已存储销售订单分摊来源与显式交叉引用单证的归属客户，"
        + "混源要求每一个相关客户都在范围内，任一越界即拒绝；受限账号不得访问来源不可证明（订单 / 单证已删除）的发票，"
        + "特权账号的历史发票保持可读；分摊整体替换同时校验「已存储」与「拟提议」的完整来源范围，被拒绝的调用方绝不改写任何发票 / 分摊行 / 销售订单 / 单证；"
        + "绝不新增权限模型、绝不把空身份当作管理员，也绝不泄露范围外发票。";

    /// <summary>边界文案（不改变发票商业口径、不改写来源单据与主数据）</summary>
    public const string BoundaryText =
        "本护栏只保护客户销项发票的授权与并发边界：不改写发票身份 / 金额 / 状态 / 分摊证据 / 作废原因与审计，"
        + "不改写销售订单、客户收款单、单证中心单证、库存与财务数据，不删除历史证据，也不新增任何表 / 列 / 菜单 / 权限；"
        + "收款分摊证据仍由 ERP-073 保护，收款单生命周期护栏仍为 ERP-349 / ERP-378，销售订单取消护栏仍为 ERP-347。";

    /// <summary>
    /// 身份 / 账号状态 / 菜单授权三重校验（fail closed）：缺失或非法身份按未认证拒绝，账号不存在 / 已删除按未认证拒绝，
    /// 禁用账号按权限不足拒绝，受限未映射业务员按权限不足拒绝，缺少既有 <c>sales-order</c> 菜单授权（在系统已部署该模块时）
    /// 按权限不足拒绝。返回解析出的权威数据范围，供调用方在同一请求内复用（绝不缓存）。
    /// <para>菜单口径与既有模块一致：账号已配置任何菜单授权时，必须显式包含 <c>sales-order</c>（撤销后下一次请求立即收敛）；
    /// 未配置任何菜单授权且账号已分配角色、且系统已部署该菜单时同样 fail closed；只有既有特权账号（生产端系统内置角色
    /// 已被授予全部菜单）与未分配角色、仅按员工编码映射的历史业务员账号沿用既有 ERP-097 权威数据范围。</para>
    /// </summary>
    public static async Task<SalespersonDataScope> EnsureMenuAuthorizedAsync(
        IErpDbContext db, long? userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (userId is null or <= 0)
            throw new BusinessException("请先登录后再访问客户销项发票", ErrorCodes.Unauthorized);

        var user = await db.SysUsers.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == userId.Value && !u.IsDeleted, ct);
        if (user is null)
            throw new BusinessException("登录账号不存在或已删除，禁止访问客户销项发票", ErrorCodes.Unauthorized);
        if (user.Status != UserStatus.Enabled)
            throw new BusinessException("登录账号已禁用，禁止访问客户销项发票（fail closed）", ErrorCodes.Forbidden);

        // 每次请求重新解析（绝不缓存）：授权 / 员工 / 客户分配变更后下一次请求立即收敛。
        var scope = await SalespersonDataScopeService.ResolveAsync(db, userId.Value);
        if (!scope.IsPrivileged && scope.SalesmanId is null or <= 0)
            throw new BusinessException(UnmappedOperatorText, ErrorCodes.Forbidden);

        var menuCodes = await CustomerReceivableReconciliationService.LoadAuthorizedMenuCodesAsync(db, userId.Value);
        if (menuCodes.Count > 0 && !menuCodes.Contains(RequiredMenuCode, StringComparer.OrdinalIgnoreCase))
            throw MenuDenied();

        if (menuCodes.Count == 0 && !scope.IsPrivileged)
        {
            var hasAnyRole = await db.SysUserRoles.AsNoTracking()
                .AnyAsync(ur => ur.UserId == userId.Value && !ur.IsDeleted, ct);
            var menuProvisioned = await db.SysMenus.AsNoTracking()
                .AnyAsync(m => !m.IsDeleted && m.MenuCode == RequiredMenuCode, ct);
            if (hasAnyRole && menuProvisioned) throw MenuDenied();
        }

        return scope;
    }

    /// <summary>缺少既有「销售订单」菜单授权时的拒绝（fail closed，不执行任何写入）。</summary>
    private static BusinessException MenuDenied()
        => new($"当前账号没有「{RequiredMenuText}」（{RequiredMenuCode}）模块授权：拒绝访问客户销项发票"
               + "（fail closed，不执行任何写入）",
            ErrorCodes.Forbidden);

    /// <summary>客户数据范围外的发票 / 来源拒绝（fail closed，不泄露范围外发票归属）。</summary>
    private static BusinessException ScopeDenied()
        => new("当前账号的客户数据范围不包含该客户销项发票的客户或其来源单据归属客户（fail closed，不泄露范围外发票）",
            ErrorCodes.Forbidden);

    // ==================== 1. 单据级授权（发票 / 拟提议来源） ====================

    /// <summary>按发票 Id 读取其已持久化分摊行的销售订单 Id（去重、只保留正整数；只读、有界）。</summary>
    public static async Task<List<long>> LoadLinkedSalesOrderIdsAsync(
        IErpDbContext db, long invoiceId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (invoiceId <= 0) return new List<long>();

        return await db.CustomerSalesInvoiceAllocations.AsNoTracking()
            .Where(a => !a.IsDeleted && a.CustomerSalesInvoiceEvidenceId == invoiceId && a.SalesOrderId > 0)
            .Select(a => a.SalesOrderId)
            .Distinct()
            .ToListAsync(ct);
    }

    /// <summary>
    /// 单据级授权：身份 / 菜单（<see cref="EnsureMenuAuthorizedAsync"/>）+ 发票<strong>全部权威客户来源</strong>的数据范围 ——
    /// 已存储客户（必须落范围内）、每一张已存储分摊来源销售订单的客户、显式交叉引用单证的归属客户；
    /// 混源场景要求每一个相关客户都在范围内。特权账号放行（保留历史发票可读 / 可操作能力）。
    /// 返回解析出的权威数据范围，供调用方在同一请求内复用（绝不缓存）。
    /// </summary>
    public static async Task<SalespersonDataScope> EnsureInvoiceAuthorizedAsync(
        IErpDbContext db, long? userId, CustomerSalesInvoiceEvidence invoice, CancellationToken ct = default)
        => await EnsureInvoiceAuthorizedWithScopeAsync(
            db, await EnsureMenuAuthorizedAsync(db, userId, ct), invoice, ct);

    /// <summary>
    /// 单据级授权（复用调用方已解析的数据范围重载）：先复核已存储客户，再复核<strong>每一张</strong>已存储分摊来源
    /// 销售订单客户的归属，最后复核显式交叉引用单证的归属客户；任一越界 / 不可证明即 fail closed。
    /// </summary>
    public static async Task<SalespersonDataScope> EnsureInvoiceAuthorizedWithScopeAsync(
        IErpDbContext db, SalespersonDataScope scope, CustomerSalesInvoiceEvidence invoice,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(invoice);
        if (scope.IsPrivileged) return scope;

        EnsureCustomerInScope(scope, invoice.CustomerId);
        await EnsureOrderIdsInScopeAsync(db, scope, await LoadLinkedSalesOrderIdsAsync(db, invoice.Id, ct), ct);
        await EnsureTradeDocumentInScopeAsync(db, scope, invoice.TradeDocumentId, ct);
        return scope;
    }

    /// <summary>
    /// 拟提议客户的授权（新增 / 修改发票的请求客户）：受限账号必须显式落在本人客户范围内，
    /// 任一越界即拒绝（在写入之前），被拒绝的调用方绝不改写任何发票。
    /// </summary>
    public static async Task EnsureCustomersAuthorizedAsync(
        IErpDbContext db, long? userId, IEnumerable<long?> customerIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(customerIds);
        var scope = await EnsureMenuAuthorizedAsync(db, userId, ct);
        if (scope.IsPrivileged) return;

        var ids = customerIds.Where(id => id is > 0).Select(id => id!.Value).Distinct().ToList();
        if (ids.Count == 0)
            throw new BusinessException("请选择客户", ErrorCodes.InvalidParameter);

        foreach (var id in ids) EnsureCustomerInScope(scope, id);
    }

    /// <summary>
    /// 按拟提议的销售订单 Id 集合复核权威归属客户范围（分摊整体替换 / 分摊预览的请求侧）：
    /// 受限账号的每一张订单都必须存在、未删除且在本人客户范围内，任一越界一律拒绝。
    /// </summary>
    public static async Task EnsureSalesOrderIdsAuthorizedAsync(
        IErpDbContext db, long? userId, IReadOnlyCollection<long> orderIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(orderIds);
        var scope = await EnsureMenuAuthorizedAsync(db, userId, ct);
        await EnsureOrderIdsInScopeAsync(db, scope, orderIds, ct);
    }

    /// <summary>
    /// 显式交叉引用单证的授权（新增 / 修改发票的请求单证）：受限账号引用<strong>归属客户在范围外</strong>的单证一律拒绝；
    /// 无权威归属（未登记客户 / 单证缺失或已删除）不构成额外约束（发票自身客户仍须在范围内）。
    /// </summary>
    public static async Task EnsureTradeDocumentAuthorizedAsync(
        IErpDbContext db, long? userId, long? tradeDocumentId, CancellationToken ct = default)
    {
        var scope = await EnsureMenuAuthorizedAsync(db, userId, ct);
        if (scope.IsPrivileged) return;
        await EnsureTradeDocumentInScopeAsync(db, scope, tradeDocumentId, ct);
    }

    private static void EnsureCustomerInScope(SalespersonDataScope scope, long customerId)
    {
        if (customerId <= 0) throw new BusinessException("请选择客户", ErrorCodes.InvalidParameter);
        if (!scope.AllowsCustomer(customerId)) throw ScopeDenied();
    }

    private static async Task EnsureOrderIdsInScopeAsync(
        IErpDbContext db, SalespersonDataScope scope, IReadOnlyCollection<long> orderIds, CancellationToken ct)
    {
        // 真正不受限（特权）的授权账号保留历史发票 / 任意来源的访问能力。
        if (scope.IsPrivileged) return;

        var ids = orderIds.Where(id => id > 0).Distinct().ToList();
        if (ids.Count == 0) return;

        var customers = await db.SalesOrders.AsNoTracking()
            .Where(o => ids.Contains(o.Id) && !o.IsDeleted)
            .Select(o => new { o.Id, o.CustomerId })
            .ToListAsync(ct);

        // 来源订单已删除 / 不存在时无法证明归属：受限账号一律拒绝（fail closed，不泄露）。
        if (customers.Count != ids.Count) throw new BusinessException(UnprovableSourceText, ErrorCodes.Forbidden);

        foreach (var order in customers) EnsureCustomerInScope(scope, order.CustomerId);
    }

    private static async Task EnsureTradeDocumentInScopeAsync(
        IErpDbContext db, SalespersonDataScope scope, long? tradeDocumentId, CancellationToken ct)
    {
        if (tradeDocumentId is null or <= 0) return;

        var owner = await db.TradeDocuments.AsNoTracking()
            .Where(d => d.Id == tradeDocumentId.Value && !d.IsDeleted)
            .Select(d => d.CustomerId)
            .FirstOrDefaultAsync(ct);

        // 单证已删除 / 不存在 / 未登记归属客户：无权威归属，不构成额外约束（发票客户已单独校验）；
        // 登记了归属客户且不在范围内时一律拒绝（不泄露范围外单证归属）。
        if (owner is > 0 && !scope.AllowsCustomer(owner.Value)) throw ScopeDenied();
    }

    // ==================== 2. 台账 / 候选范围下推（计数 / 分页之前） ====================

    /// <summary>
    /// 台账作用域下推谓词：身份 / 菜单 fail closed 后，把权威来源范围下推到数据库（调用方必须在计数 / 分页之前应用）。
    /// 特权账号返回恒真谓词；受限账号只保留「已存储客户在范围内」且「没有分摊来源越界」且
    /// 「显式交叉引用单证归属客户未越界」的发票，绝不「先查全量再内存过滤」，也绝不泄露范围外发票。
    /// </summary>
    public static async Task<Expression<Func<CustomerSalesInvoiceEvidence, bool>>> BuildScopePredicateAsync(
        IErpDbContext db, long? userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var scope = await EnsureMenuAuthorizedAsync(db, userId, ct);
        if (scope.IsPrivileged) return _ => true;

        var allowed = scope.AllowedCustomerIds?.ToList() ?? new List<long>();
        if (allowed.Count == 0) return _ => false;

        var allowedOrderIds = await db.SalesOrders.AsNoTracking()
            .Where(o => !o.IsDeleted && allowed.Contains(o.CustomerId))
            .Select(o => o.Id)
            .ToListAsync(ct);

        // 归属客户明确落在范围外的单证：引用它的发票对受限账号 fail closed
        // （不可证明归属的历史单证不额外阻断 —— 历史来源显式保留、不泄露范围外字段）。
        var foreignDocumentIds = await db.TradeDocuments.AsNoTracking()
            .Where(d => !d.IsDeleted && d.CustomerId.HasValue && !allowed.Contains(d.CustomerId.Value))
            .Select(d => d.Id)
            .ToListAsync(ct);

        var allocations = db.CustomerSalesInvoiceAllocations;
        return invoice =>
            allowed.Contains(invoice.CustomerId)
            && !allocations.Any(a => !a.IsDeleted && a.CustomerSalesInvoiceEvidenceId == invoice.Id
                                     && !allowedOrderIds.Contains(a.SalesOrderId))
            && (invoice.TradeDocumentId == null || !foreignDocumentIds.Contains(invoice.TradeDocumentId.Value));
    }

    /// <summary>台账作用域下推（<see cref="BuildScopePredicateAsync"/> 的 IQueryable 便捷重载）。</summary>
    public static async Task<IQueryable<CustomerSalesInvoiceEvidence>> ApplyScopeAsync(
        IErpDbContext db, IQueryable<CustomerSalesInvoiceEvidence> source, long? userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        var predicate = await BuildScopePredicateAsync(db, userId, ct);
        return source.Where(predicate);
    }

    /// <summary>
    /// 单证中心商业发票候选的作用域下推谓词（只读、有界）：受限账号只保留<strong>已登记归属客户且该客户在范围内</strong>
    /// 的单证；无权威归属（未登记客户）或归属越界的单证一律不进入候选，绝不按单号文本 / 相似度猜测来源。
    /// </summary>
    public static async Task<Expression<Func<TradeDocument, bool>>> BuildTradeDocumentScopePredicateAsync(
        IErpDbContext db, long? userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var scope = await EnsureMenuAuthorizedAsync(db, userId, ct);
        if (scope.IsPrivileged) return _ => true;

        var allowed = scope.AllowedCustomerIds?.ToList() ?? new List<long>();
        if (allowed.Count == 0) return _ => false;

        return document => document.CustomerId.HasValue && allowed.Contains(document.CustomerId.Value);
    }

    /// <summary>单证候选作用域下推（<see cref="BuildTradeDocumentScopePredicateAsync"/> 的 IQueryable 便捷重载）。</summary>
    public static async Task<IQueryable<TradeDocument>> ApplyTradeDocumentScopeAsync(
        IErpDbContext db, IQueryable<TradeDocument> source, long? userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        var predicate = await BuildTradeDocumentScopePredicateAsync(db, userId, ct);
        return source.Where(predicate);
    }
}

