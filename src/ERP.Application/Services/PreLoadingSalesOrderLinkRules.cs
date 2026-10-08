using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ERP.Application.Services;

/// <summary>
/// 预装柜明细 → 销售订单明细 的显式**需求计划证据**链接与累计容量护栏（ERP-368）。
/// <para><b>显式链接</b>：<see cref="ContainerPreLoadingDetail.SourceSalesOrderDetailId"/> 可空，历史 / 未链接明细
/// 保持 <c>null</c>（显式「无需求来源」）——<b>绝不回填、绝不按订单号 / 相似度猜测来源</b>，未链接数量
/// <b>不计入</b>「已链接需求」容量。</para>
/// <para><b>权威来源</b>：链接必须指向「存在、未删除」的销售订单明细，其父销售订单必须「存在、未删除、已审核」
/// （已取消 / 未审核 / 待提交 / 已提交一律拒绝），商品与基础单位语义一致（订单明细单位为空或等于商品基础单位，
/// 商品未维护基础单位时 fail closed），且订单客户必须等于本预装柜单**权威订柜信息**的客户
/// （<see cref="ContainerPreLoading.BookingId"/> → <see cref="ContainerBooking.CustomerId"/>）。
/// 任一不满足即在保存 / 提交 / 审核前 fail closed 拒绝。</para>
/// <para><b>容量口径</b>：审核时**按来源销售订单明细逐条**聚合，比较
/// 「其它已审核、未删除预装柜单已链接数量 + 本次链接数量」≤「来源销售订单明细数量」；
/// 跨商品不合计、不混合单位与币种（每条明细商品唯一，只比较基础单位数量），未链接数量不参与。</para>
/// <para><b>边界</b>：本类只做<b>纯判定与有界只读查询 + 行锁 SQL 常量</b>：不落库、不改单据 / 明细 / 状态 / 库存 /
/// 财务 / 单证，不新增表 / 列 / 菜单 / 权限。本链接是<b>需求计划 / 追溯证据</b>，<b>不是库存预留、不是出运凭证</b>。
/// 锁序（上游销售订单行 → 订柜信息行 → 预装柜单行 <c>UPDLOCK, HOLDLOCK</c>）与可串行化事务由调用方
/// （<c>ContainerPreLoadingController</c>）负责。</para>
/// </summary>
public static class PreLoadingSalesOrderLinkRules
{
    /// <summary>预装柜单模块所需既有菜单编码（与 <see cref="PreLoadingAuthorizationRules.RequiredMenuCode"/> 同源）</summary>
    public const string RequiredMenuCode = PreLoadingAuthorizationRules.RequiredMenuCode;

    /// <summary>预装柜单模块菜单中文文案</summary>
    public const string RequiredMenuText = PreLoadingAuthorizationRules.RequiredMenuText;

    /// <summary>读取 / 链接销售订单需求证据所需既有菜单编码（与 <c>SeedData.Menus</c> 同源）</summary>
    public const string SourceRequiredMenuCode = "sales-order";

    /// <summary>销售订单模块菜单中文文案（与既有菜单名一致）</summary>
    public const string SourceRequiredMenuText = "销售订单";

    /// <summary>数量比较容差（与基础单位数量保留 4 位小数同口径，吸收舍入尾差）</summary>
    public const decimal QuantityTolerance = 0.0001m;

    /// <summary>候选查询默认返回条数</summary>
    public const int DefaultCandidateTake = 50;

    /// <summary>候选查询返回条数上限（有界，绝不无界拉取）</summary>
    public const int MaxCandidateTake = 200;

    /// <summary>候选查询扫描上限（有界：先取有限候选再按剩余容量过滤）</summary>
    public const int MaxCandidateScan = 500;

    /// <summary>口径说明（接口 / 文档同源）</summary>
    public const string RuleText =
        "预装柜明细通过显式来源销售订单明细 Id 认定需求计划证据：只接受「存在、未删除」的订单明细，其父销售订单" +
        "必须「存在、未删除、已审核」（已取消 / 未审核 / 待提交 / 已提交一律拒绝），商品与基础单位语义一致，" +
        "且订单客户必须等于本预装柜单权威订柜信息客户；新链接数量必须为正；审核时**按来源销售订单明细逐条**聚合比较" +
        "「其它已审核预装柜已链接数量 + 本次」≤「来源销售订单明细数量」；历史 / 未链接明细保持显式「无需求来源」，" +
        "绝不回填、绝不按订单号猜测来源、绝不计入已链接需求容量；本证据只是需求计划 / 追溯，不是库存预留、不是出运凭证，" +
        "审核不过账库存、不改财务，判定与状态变更在同一可串行化事务内原子提交。";

    /// <summary>边界文案（不改库存 / 财务 / 主数据，不删历史证据）</summary>
    public const string BoundaryText =
        "本护栏只新增「显式需求计划证据链接 + 累计容量」判定与实时授权：不锁库、不预留库存，不改变审核既有语义，" +
        "不生成库存流水 / 费用 / 单证，不改写商品 / 客户 / 销售订单 / 订柜信息主数据，不重写历史预装柜明细数量与状态，" +
        "不删除任何审计证据，也不新增表 / 列 / 菜单 / 权限（只新增一个可空证据列与其读取侧索引）。";

    /// <summary>需求计划证据定位文案（不是库存预留 / 出运凭证）</summary>
    public const string DemandPlanningText =
        "本链接是**需求计划 / 追溯证据**：只说明该预装柜行计划满足哪一条已审核销售订单明细，不是库存预留、" +
        "不是出运凭证，不锁库、不生成库存流水、不改财务、不生成单证；装柜清单的物理出运证据由装柜明细的" +
        "销售出库明细链接（ERP-366）单独认定。";

    /// <summary>上游销售订单行锁语句（与销售订单取消 / 出库审核共用同一把来源行锁）。</summary>
    public const string LockSalesOrderRowSql =
        "SELECT Id FROM db_owner.SalesOrders WITH (UPDLOCK, HOLDLOCK) WHERE Id = {0}";

    /// <summary>确定性锁序说明（接口 / 文档同源）</summary>
    public const string LockOrderNote =
        "审核 / 链接时锁序固定为：上游销售订单行（SalesOrderId 升序，UPDLOCK, HOLDLOCK）→ 订柜信息行 → 预装柜单行。";

    /// <summary>
    /// 读取 / 链接销售订单需求证据所需的既有「销售订单」菜单授权（非特权账号必须显式具备，
    /// 缺失 / 非法身份按未认证拒绝，禁用 / 无菜单按权限不足拒绝，绝不降级为管理员）。
    /// </summary>
    public static async Task EnsureSourceMenuAuthorizedAsync(
        IErpDbContext db, long? userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ct.ThrowIfCancellationRequested();

        if (userId is null or <= 0)
            throw new BusinessException("请先登录后再选择销售订单需求证据", ErrorCodes.Unauthorized);

        var scope = await SalespersonDataScopeService.ResolveAsync(db, userId.Value);
        if (scope.IsPrivileged) return;

        var menuCodes = await CustomerReceivableReconciliationService
            .LoadAuthorizedMenuCodesAsync(db, userId.Value);
        if (!menuCodes.Contains(SourceRequiredMenuCode, StringComparer.OrdinalIgnoreCase))
        {
            throw new BusinessException(
                $"当前账号没有「{SourceRequiredMenuText}」（{SourceRequiredMenuCode}）模块授权：" +
                "拒绝读取 / 链接销售订单需求证据（fail closed，不返回任何订单数据）",
                ErrorCodes.Forbidden);
        }
    }

    /// <summary>
    /// 来源订单明细单位是否与商品基础单位兼容：空单位按基础单位处理，否则必须等于商品基础单位；
    /// 商品未维护基础单位时返回 <c>false</c>（由调用方 fail closed，绝不臆造基础数量语义）。
    /// </summary>
    public static bool IsBaseUnitCompatible(BaseProduct product, string? unit)
    {
        ArgumentNullException.ThrowIfNull(product);
        var baseUnit = (product.Unit ?? string.Empty).Trim();
        if (baseUnit.Length == 0) return false;
        var lineUnit = (unit ?? string.Empty).Trim();
        return lineUnit.Length == 0 || lineUnit.Equals(baseUnit, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>关系型后端（SQL Server）判定：内存库等非关系型提供程序没有行锁语义，锁定等价无操作。</summary>
    public static bool IsRelationalProvider(IErpDbContext db)
        => ReturnSourceCancellationRules.IsRelationalProvider(db);

    /// <summary>
    /// 读取预装柜明细已显式链接的来源**销售订单** Id（升序、去重；有界只读查询）。
    /// 供调用方在取得任何写锁之前确定需要锁定的上游订单行，实现确定性锁序（升序，避免锁环）。
    /// </summary>
    public static async Task<List<long>> LoadLinkedSalesOrderIdsAsync(
        IErpDbContext db, ContainerPreLoading entity, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(entity);

        var detailIds = entity.Details
            .Where(d => !d.IsDeleted && d.SourceSalesOrderDetailId is > 0)
            .Select(d => d.SourceSalesOrderDetailId!.Value)
            .Distinct().ToList();
        if (detailIds.Count == 0) return new List<long>();

        var ids = await db.SalesOrderDetails.AsNoTracking()
            .Where(d => detailIds.Contains(d.Id))
            .Select(d => d.SalesOrderId)
            .Distinct()
            .ToListAsync(ct);
        ids.Sort();
        return ids;
    }

    /// <summary>
    /// 解析给定来源销售订单明细 Id 集合所涉及的来源**销售订单** Id（升序、去重）。
    /// 供调用方在取得本次链接指派的上游行锁之前确定锁集合。
    /// </summary>
    public static async Task<List<long>> LoadSalesOrderIdsBySourceDetailIdsAsync(
        IErpDbContext db, IReadOnlyCollection<long> sourceSalesOrderDetailIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(sourceSalesOrderDetailIds);

        var ids = sourceSalesOrderDetailIds.Where(id => id > 0).Distinct().ToList();
        if (ids.Count == 0) return new List<long>();

        var orderIds = await db.SalesOrderDetails.AsNoTracking()
            .Where(d => ids.Contains(d.Id))
            .Select(d => d.SalesOrderId)
            .Distinct()
            .ToListAsync(ct);
        orderIds.Sort();
        return orderIds;
    }

    /// <summary>
    /// 有界、实时授权的「可链接需求计划证据」候选查询（只读）：返回属于本预装柜单**权威订柜客户**、
    /// 「已审核、未删除」的销售订单明细，并显式回传父销售订单 / 商品 / 客户与**剩余可链接基础单位数量**。
    /// <para>范围口径：受限账号先按权威订柜客户复核数据范围，候选再按该客户过滤，绝不放行范围外订单证据。</para>
    /// <para>有界口径：先取有限候选（<see cref="MaxCandidateScan"/>）再按剩余容量过滤，最终返回不超过 <paramref name="take"/>
    /// （钳制在 1..<see cref="MaxCandidateTake"/>），绝不无界拉取。</para>
    /// </summary>
    public static async Task<List<PreLoadingSalesOrderCandidateDto>> QueryCandidatesAsync(
        IErpDbContext db, ContainerPreLoading entity, SalespersonDataScope scope,
        string? keyword, int take, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(scope);

        if (take <= 0) take = DefaultCandidateTake;
        if (take > MaxCandidateTake) take = MaxCandidateTake;

        var customerId = await LoadAuthoritativeCustomerIdAsync(db, entity, ct);
        if (customerId is null)
        {
            if (!scope.IsPrivileged)
                throw new BusinessException(PreLoadingAuthorizationRules.UnlinkedSourceText, ErrorCodes.Forbidden);
            return new List<PreLoadingSalesOrderCandidateDto>();
        }
        if (!scope.AllowsCustomer(customerId.Value))
            throw new BusinessException(PreLoadingAuthorizationRules.OutOfScopeText, ErrorCodes.Forbidden);

        var query =
            from d in db.SalesOrderDetails
            join o in db.SalesOrders on d.SalesOrderId equals o.Id
            where !d.IsDeleted && !o.IsDeleted && o.Status == DocumentStatus.Approved
                  && d.ProductId > 0 && o.CustomerId == customerId.Value
            select new { Detail = d, Order = o };

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var k = keyword.Trim();
            query = query.Where(x => x.Order.OrderNo.Contains(k) || x.Detail.ProductName.Contains(k));
        }

        var scanned = await query
            .OrderByDescending(x => x.Order.Id).ThenBy(x => x.Detail.Id)
            .Take(MaxCandidateScan)
            .ToListAsync(ct);
        if (scanned.Count == 0) return new List<PreLoadingSalesOrderCandidateDto>();

        var productIds = scanned.Select(x => x.Detail.ProductId).Distinct().ToList();
        var sourceDetailIds = scanned.Select(x => x.Detail.Id).Distinct().ToList();

        var linkedByDetail = await LoadLinkedPreLoadingBaseQuantitiesAsync(db, sourceDetailIds, 0, ct);
        var products = await db.BaseProducts.AsNoTracking()
            .Where(p => productIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
        var customer = await db.BaseCustomers.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == customerId.Value, ct);

        var results = new List<PreLoadingSalesOrderCandidateDto>();
        foreach (var row in scanned)
        {
            var sourceQuantity = row.Detail.Quantity;
            var linked = linkedByDetail.GetValueOrDefault(row.Detail.Id);
            var remaining = sourceQuantity - linked;
            if (remaining <= QuantityTolerance) continue;

            products.TryGetValue(row.Detail.ProductId, out var product);
            results.Add(new PreLoadingSalesOrderCandidateDto
            {
                SalesOrderDetailId = row.Detail.Id,
                SalesOrderId = row.Order.Id,
                OrderNo = row.Order.OrderNo,
                OrderDate = row.Order.OrderDate,
                ProductId = row.Detail.ProductId,
                ProductName = product?.ProductName ?? row.Detail.ProductName,
                Spec = product?.Spec ?? row.Detail.Spec,
                Unit = product?.Unit ?? string.Empty,
                CustomerId = row.Order.CustomerId,
                CustomerName = customer?.CustomerName ?? string.Empty,
                OrderBaseQuantity = sourceQuantity,
                LinkedPreLoadingBaseQuantity = linked,
                RemainingBaseQuantity = remaining
            });

            if (results.Count >= take) break;
        }

        return results;
    }

    /// <summary>
    /// 校验创建 / 修改 / 提交时的显式需求计划证据链接（在改写任何明细之前调用）：
    /// 未链接（null）保持历史行为；显式链接必须构成权威来源（订单存在、未删除、已审核、商品 / 基础单位一致、
    /// 客户等于权威订柜客户）且新链接数量为正，否则 fail closed 拒绝。
    /// </summary>
    public static async Task ValidateLinksAsync(
        IErpDbContext db, ContainerPreLoading entity, long? userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(entity);

        await PreLoadingAuthorizationRules.EnsureAuthorizedAsync(db, userId, ct);

        if (entity.Details.Any(d => !d.IsDeleted && d.SourceSalesOrderDetailId is <= 0))
            throw BusinessException.InvalidParameter("来源销售订单明细 Id 必须为正整数");

        var linked = entity.Details
            .Where(d => !d.IsDeleted && d.SourceSalesOrderDetailId is > 0).ToList();
        if (linked.Count == 0) return;

        // 只有真正提交了显式需求计划证据链接时才要求「销售订单」菜单授权，避免影响未链接的历史流程。
        await EnsureSourceMenuAuthorizedAsync(db, userId, ct);

        var context = await ResolveSourceContextAsync(db, entity, ct);
        ValidateEachLink(entity, context);
    }

    /// <summary>
    /// 校验审核时的显式需求计划证据链接与累计容量上限（调用方必须已持有上游销售订单行、订柜信息行与预装柜单行
    /// 更新锁，并处于同一可串行化事务内）。未链接（null）直接返回；判定失败即 fail closed，
    /// 不落库、不改状态 / 历史、不过账库存、不改财务、不生成单证。
    /// </summary>
    public static async Task ValidateApprovalAsync(
        IErpDbContext db, ContainerPreLoading entity, long? userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(entity);

        await PreLoadingAuthorizationRules.EnsureAuthorizedAsync(db, userId, ct);

        if (entity.Details.Any(d => !d.IsDeleted && d.SourceSalesOrderDetailId is <= 0))
            throw BusinessException.InvalidParameter("来源销售订单明细 Id 必须为正整数");

        var linked = entity.Details
            .Where(d => !d.IsDeleted && d.SourceSalesOrderDetailId is > 0).ToList();
        if (linked.Count == 0) return;

        await EnsureSourceMenuAuthorizedAsync(db, userId, ct);

        var context = await ResolveSourceContextAsync(db, entity, ct);
        ValidateEachLink(entity, context);
        await EnforceCapacityAsync(db, entity, context, ct);
    }

    /// <summary>
    /// 回显预装柜单全部明细的最终链接状态（含历史未链接行）：<c>SourceSalesOrderDetailId</c> 为空 =
    /// 显式未链接，作为「无需求来源」的显式事实返回，绝不臆造来源单号。
    /// </summary>
    public static async Task<List<PreLoadingSalesOrderLinkLineDto>> DescribeLinksAsync(
        IErpDbContext db, ContainerPreLoading entity, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(entity);

        var details = entity.Details.Where(d => !d.IsDeleted).OrderBy(d => d.Id).ToList();
        var detailIds = details
            .Where(d => d.SourceSalesOrderDetailId is > 0)
            .Select(d => d.SourceSalesOrderDetailId!.Value).Distinct().ToList();

        var sourceRows = detailIds.Count == 0
            ? new List<SalesOrderDetail>()
            : await db.SalesOrderDetails.AsNoTracking()
                .Where(d => detailIds.Contains(d.Id)).ToListAsync(ct);
        var sourceByDetailId = sourceRows.ToDictionary(d => d.Id);

        var orderIds = sourceRows.Select(d => d.SalesOrderId).Distinct().ToList();
        var orders = orderIds.Count == 0
            ? new List<SalesOrder>()
            : await db.SalesOrders.AsNoTracking()
                .Where(o => orderIds.Contains(o.Id)).ToListAsync(ct);
        var orderById = orders.ToDictionary(o => o.Id);

        var results = new List<PreLoadingSalesOrderLinkLineDto>();
        foreach (var detail in details)
        {
            long? orderId = null;
            var orderNo = string.Empty;
            if (detail.SourceSalesOrderDetailId is > 0
                && sourceByDetailId.TryGetValue(detail.SourceSalesOrderDetailId.Value, out var source)
                && orderById.TryGetValue(source.SalesOrderId, out var order))
            {
                orderId = order.Id;
                orderNo = order.OrderNo;
            }

            results.Add(new PreLoadingSalesOrderLinkLineDto
            {
                PreLoadingDetailId = detail.Id,
                ProductId = detail.ProductId,
                ProductName = detail.ProductName,
                Quantity = detail.Quantity,
                SourceSalesOrderDetailId = detail.SourceSalesOrderDetailId,
                SalesOrderId = orderId,
                OrderNo = orderNo
            });
        }
        return results;
    }

    // ==================== 私有助手 ====================

    /// <summary>读取本预装柜单的权威订柜客户（显式 BookingId → 未删除订柜信息客户）；无法解析时返回 <c>null</c>。</summary>
    private static async Task<long?> LoadAuthoritativeCustomerIdAsync(
        IErpDbContext db, ContainerPreLoading entity, CancellationToken ct)
    {
        if (entity.BookingId is not > 0) return null;
        var booking = await db.ContainerBookings.AsNoTracking()
            .FirstOrDefaultAsync(b => b.Id == entity.BookingId.Value && !b.IsDeleted, ct);
        return booking?.CustomerId is > 0 ? booking.CustomerId : null;
    }

    /// <summary>
    /// 按「来源销售订单明细」聚合**其它已审核预装柜单**已链接占用的基础单位数量（只统计显式链接行；
    /// 历史未链接明细不计入，绝不被当作已链接需求；<paramref name="excludePreLoadingId"/> 用于审核时排除本单）。
    /// </summary>
    private static async Task<Dictionary<long, decimal>> LoadLinkedPreLoadingBaseQuantitiesAsync(
        IErpDbContext db, IReadOnlyCollection<long> sourceDetailIds, long excludePreLoadingId,
        CancellationToken ct)
    {
        var result = new Dictionary<long, decimal>();
        var ids = sourceDetailIds.Where(id => id > 0).Distinct().ToList();
        if (ids.Count == 0) return result;

        var rows = await (from p in db.ContainerPreLoadings
                          join d in db.ContainerPreLoadingDetails on p.Id equals d.PreLoadingId
                          where !p.IsDeleted && p.Status == DocumentStatus.Approved
                                && p.Id != excludePreLoadingId
                                && !d.IsDeleted && d.SourceSalesOrderDetailId != null
                                && ids.Contains(d.SourceSalesOrderDetailId.Value)
                          select new { SourceDetailId = d.SourceSalesOrderDetailId!.Value, d.Quantity })
                         .ToListAsync(ct);

        foreach (var row in rows)
        {
            result[row.SourceDetailId] = result.GetValueOrDefault(row.SourceDetailId) + row.Quantity;
        }
        return result;
    }

    /// <summary>权威来源上下文（只读事实：来源订单明细 / 父订单 / 商品 / 权威订柜客户）。</summary>
    private sealed class SourceLinkContext
    {
        public SourceLinkContext(
            Dictionary<long, SalesOrderDetail> detailsById,
            Dictionary<long, SalesOrder> ordersById,
            Dictionary<long, BaseProduct> productsById,
            long? bookingCustomerId)
        {
            DetailsById = detailsById;
            OrdersById = ordersById;
            ProductsById = productsById;
            BookingCustomerId = bookingCustomerId;
        }

        public Dictionary<long, SalesOrderDetail> DetailsById { get; }
        public Dictionary<long, SalesOrder> OrdersById { get; }
        public Dictionary<long, BaseProduct> ProductsById { get; }
        public long? BookingCustomerId { get; }
    }

    /// <summary>
    /// 逐行校验显式需求计划证据链接：新链接数量为正、来源订单明细存在且未删除、父订单已审核、商品一致、
    /// 基础单位语义一致、订单客户等于权威订柜客户。任一不满足 fail closed。
    /// </summary>
    private static void ValidateEachLink(ContainerPreLoading entity, SourceLinkContext context)
    {
        if (context.BookingCustomerId is null)
        {
            throw new BusinessException(PreLoadingAuthorizationRules.UnlinkedSourceText, ErrorCodes.Forbidden);
        }

        foreach (var detail in entity.Details.Where(d => !d.IsDeleted))
        {
            if (detail.SourceSalesOrderDetailId is null) continue;
            if (detail.SourceSalesOrderDetailId is <= 0)
                throw BusinessException.InvalidParameter("来源销售订单明细 Id 必须为正整数");

            if (detail.ProductId <= 0)
                throw BusinessException.RuleConflict("预装柜明细必须指定商品");
            if (!context.ProductsById.TryGetValue(detail.ProductId, out var product) || product.IsDeleted)
                throw BusinessException.RuleConflict($"商品 [{detail.ProductName}] 不存在或已删除，不能链接需求计划证据");
            if (detail.Quantity <= 0)
                throw BusinessException.InvalidParameter(
                    $"商品 [{product.ProductName}] 的预装数量必须为正数（新链接数量必须为正）");

            if (!context.DetailsById.TryGetValue(detail.SourceSalesOrderDetailId.Value, out var sourceDetail))
                throw BusinessException.RuleConflict("来源销售订单明细不存在或 Id 无效，不能作为需求计划证据");
            if (sourceDetail.IsDeleted)
                throw BusinessException.RuleConflict("来源销售订单明细已被删除，不能作为需求计划证据");

            if (!context.OrdersById.TryGetValue(sourceDetail.SalesOrderId, out var order) || order.IsDeleted)
                throw BusinessException.RuleConflict("来源销售订单不存在或已删除");

            if (order.Status == DocumentStatus.Cancelled)
                throw BusinessException.RuleConflict(
                    $"来源销售订单 [{order.OrderNo}] 已取消，不能作为需求计划证据");
            if (order.Status != DocumentStatus.Approved)
                throw BusinessException.RuleConflict(
                    $"来源销售订单 [{order.OrderNo}] 未审核（当前状态 {order.Status}），不能作为需求计划证据");

            if (sourceDetail.ProductId != detail.ProductId)
                throw BusinessException.RuleConflict(
                    $"商品 [{product.ProductName}] 与来源销售订单明细商品不一致（来源为商品 Id {sourceDetail.ProductId}）");

            if (!IsBaseUnitCompatible(product, sourceDetail.Unit))
                throw BusinessException.RuleConflict(
                    $"商品 [{product.ProductName}] 的来源订单明细单位 [{sourceDetail.Unit}] 与商品基础单位 [{product.Unit}] " +
                    "不一致（基础数量语义不匹配，fail closed）");

            if (order.CustomerId != context.BookingCustomerId.Value)
                throw BusinessException.RuleConflict(
                    $"来源销售订单 [{order.OrderNo}] 的客户与预装柜单权威订柜信息客户不一致，拒绝链接");
        }
    }

    /// <summary>
    /// 解析本单显式链接所涉及的来源订单明细 / 父订单 / 商品 / 权威订柜客户（有界只读，含软删除行以 fail closed）。
    /// </summary>
    private static async Task<SourceLinkContext> ResolveSourceContextAsync(
        IErpDbContext db, ContainerPreLoading entity, CancellationToken ct)
    {
        var sourceDetailIds = entity.Details
            .Where(d => !d.IsDeleted && d.SourceSalesOrderDetailId is > 0)
            .Select(d => d.SourceSalesOrderDetailId!.Value).Distinct().ToList();

        var details = sourceDetailIds.Count == 0
            ? new List<SalesOrderDetail>()
            : await db.SalesOrderDetails.AsNoTracking()
                .Where(d => sourceDetailIds.Contains(d.Id)).ToListAsync(ct);
        var detailsById = details.ToDictionary(d => d.Id);

        var orderIds = details.Select(d => d.SalesOrderId).Distinct().ToList();
        var orders = orderIds.Count == 0
            ? new List<SalesOrder>()
            : await db.SalesOrders.AsNoTracking()
                .Where(o => orderIds.Contains(o.Id)).ToListAsync(ct);
        var ordersById = orders.ToDictionary(o => o.Id);

        var productIds = entity.Details
            .Where(d => !d.IsDeleted && d.ProductId > 0).Select(d => d.ProductId)
            .Concat(details.Select(d => d.ProductId))
            .Distinct().ToList();
        var products = productIds.Count == 0
            ? new List<BaseProduct>()
            : await db.BaseProducts.AsNoTracking()
                .Where(p => productIds.Contains(p.Id)).ToListAsync(ct);
        var productsById = products.ToDictionary(p => p.Id);

        var bookingCustomerId = await LoadAuthoritativeCustomerIdAsync(db, entity, ct);

        return new SourceLinkContext(detailsById, ordersById, productsById, bookingCustomerId);
    }

    /// <summary>
    /// 审核时**按来源销售订单明细逐条**聚合校验累计已链接预装数量 ≤ 来源订单明细数量：
    /// 只统计显式链接行，历史未链接数量不参与（既不计入已链接需求，也不占用容量）。
    /// </summary>
    private static async Task EnforceCapacityAsync(
        IErpDbContext db, ContainerPreLoading entity, SourceLinkContext context, CancellationToken ct)
    {
        var thisByDetail = new Dictionary<long, decimal>();
        foreach (var detail in entity.Details.Where(d => !d.IsDeleted && d.SourceSalesOrderDetailId is > 0))
        {
            if (!context.DetailsById.TryGetValue(detail.SourceSalesOrderDetailId!.Value, out var sourceDetail))
                continue; // 已在校验中 fail closed
            thisByDetail[sourceDetail.Id] = thisByDetail.GetValueOrDefault(sourceDetail.Id) + detail.Quantity;
        }
        if (thisByDetail.Count == 0) return;

        var sourceDetailIds = thisByDetail.Keys.ToList();
        var linkedByDetail = await LoadLinkedPreLoadingBaseQuantitiesAsync(db, sourceDetailIds, entity.Id, ct);

        foreach (var group in thisByDetail)
        {
            if (!context.DetailsById.TryGetValue(group.Key, out var sourceDetail))
                throw BusinessException.RuleConflict("来源销售订单明细已不存在或已删除，不能审核（fail closed）");

            var capacity = sourceDetail.Quantity;
            var already = linkedByDetail.GetValueOrDefault(group.Key);
            var thisQuantity = group.Value;
            var total = already + thisQuantity;

            if (total > capacity + QuantityTolerance)
            {
                var productName = context.ProductsById.TryGetValue(sourceDetail.ProductId, out var product)
                    ? product.ProductName
                    : $"商品 Id {sourceDetail.ProductId}";
                throw BusinessException.RuleConflict(
                    $"商品 [{productName}] 累计已链接预装数量 {total} 超过来源销售订单明细可计划数量 {capacity}" +
                    $"（其它已审核预装柜 {already} + 本次 {thisQuantity}）");
            }
        }
    }
}
