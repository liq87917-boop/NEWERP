using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ERP.Application.Services;

/// <summary>
/// 装柜明细 → 销售出库明细 的显式出运证据链接与累计容量护栏（ERP-366）。
/// <para><b>显式链接</b>：<see cref="ContainerLoadingDetail.SourceStockOutDetailId"/> 可空，历史 / 未链接明细
/// 保持 <c>null</c>（显式「无证据」）——<b>绝不回填、绝不按出库单号 / 相似度猜测来源</b>，未链接数量
/// <b>不计入</b>「已证明出运」容量。</para>
/// <para><b>权威来源</b>：链接必须指向「存在、未删除、已审核」的销售出库单明细，商品与基础单位语义一致
/// （出库明细单位为空或等于商品基础单位），且出库单客户必须属于本装柜清单的权威客户范围
/// （兼容客户字段 + 启用参与方；一柜多客户必须逐客户匹配）。任一不满足即在保存 / 审核前 fail closed 拒绝。</para>
/// <para><b>容量口径</b>：审核时按「来源出库单 + 商品」聚合，比较
/// 「其它已审核装柜清单已链接数量 + 本次链接数量」≤「来源出库明细数量 − 已生效（已审核、未删除）销售退货数量」；
/// 退货缺明细 Id，因此退货按商品保守聚合，绝不猜测退货归属到哪一条出库明细；跨商品不合计，未链接数量不参与。</para>
/// <para>本类只做<b>纯判定与有界只读查询 + 行锁 SQL 常量</b>：不落库、不改单据 / 明细 / 状态 / 库存 / 财务，
/// 不新增表 / 列 / 菜单 / 权限。锁序（上游销售出库行 → 装柜清单行 <c>UPDLOCK, HOLDLOCK</c>）与可串行化事务
/// 由调用方（<c>ContainerLoadingListController</c>）负责。</para>
/// </summary>
public static class LoadingStockOutLinkRules
{
    /// <summary>装柜清单模块所需既有菜单编码（与 <see cref="ContainerLoadingFulfillmentRules.RequiredMenuCode"/> 同源）</summary>
    public const string RequiredMenuCode = ContainerLoadingFulfillmentRules.RequiredMenuCode;

    /// <summary>装柜清单模块菜单中文文案</summary>
    public const string RequiredMenuText = ContainerLoadingFulfillmentRules.RequiredMenuText;

    /// <summary>读取 / 链接销售出库证据所需既有菜单编码（与 <c>SeedData.Menus</c> / <c>SchemaUpgrader</c> 同源）</summary>
    public const string SourceRequiredMenuCode = "stock-out";

    /// <summary>销售出库模块菜单中文文案（与既有菜单名一致）</summary>
    public const string SourceRequiredMenuText = "销售出库";

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
        "装柜明细通过显式来源销售出库明细 Id 认定物理出运证据：只接受「存在、未删除、已审核」的出库明细，" +
        "商品与基础单位语义一致，且出库单客户属于本装柜清单权威客户范围（一柜多客户逐客户匹配）；" +
        "新链接数量必须为正；审核时按「来源出库单 + 商品」聚合比较「其它已审核装柜已链接数量 + 本次」≤" +
        "「来源出库明细数量 − 已生效销售退货数量」（退货缺明细 Id 时按商品保守聚合）；" +
        "历史 / 未链接明细保持显式「无证据」，绝不回填、绝不按单号猜测来源、绝不计入已证明出运容量；" +
        "审核不二次过账库存、不改财务，判定与状态变更在同一可串行化事务内原子提交。";

    /// <summary>边界文案（不改库存 / 财务 / 主数据，不删历史证据）</summary>
    public const string BoundaryText =
        "本护栏只新增「显式出运证据链接 + 累计容量」判定与实时授权：不改变审核既有库存过账与财务口径，" +
        "不改写商品 / 客户 / 销售订单 / 出库单主数据，不重写历史装柜明细数量与状态，不删除任何审计证据，" +
        "也不新增表 / 列 / 菜单 / 权限（只新增一个可空证据列与其读取侧索引）。";

    /// <summary>上游销售出库单行锁语句（与销售退货审核 / 销审 / 取消共用同一把来源行锁）。</summary>
    public const string LockStockOutRowSql =
        "SELECT Id FROM db_owner.StockOuts WITH (UPDLOCK, HOLDLOCK) WHERE Id = {0}";

    /// <summary>
    /// 读取 / 链接销售出库证据所需的既有「销售出库」菜单授权（非特权账号必须显式具备，
    /// 缺失 / 非法身份按未认证拒绝，禁用 / 无菜单按权限不足拒绝，绝不降级为管理员）。
    /// </summary>
    public static async Task EnsureSourceMenuAuthorizedAsync(
        IErpDbContext db, long? userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ct.ThrowIfCancellationRequested();

        if (userId is null or <= 0)
            throw new BusinessException("请先登录后再选择销售出库证据", ErrorCodes.Unauthorized);

        var scope = await SalespersonDataScopeService.ResolveAsync(db, userId.Value);
        if (scope.IsPrivileged) return;

        var menuCodes = await CustomerReceivableReconciliationService
            .LoadAuthorizedMenuCodesAsync(db, userId.Value);
        if (!menuCodes.Contains(SourceRequiredMenuCode, StringComparer.OrdinalIgnoreCase))
        {
            throw new BusinessException(
                $"当前账号没有「{SourceRequiredMenuText}」（{SourceRequiredMenuCode}）模块授权：" +
                "拒绝读取 / 链接销售出库证据（fail closed，不返回任何出库数据）",
                ErrorCodes.Forbidden);
        }
    }

    /// <summary>
    /// 读取装柜清单的权威客户成员集合：兼容客户字段 <see cref="ContainerLoadingList.CustomerId"/>（为正时）
    /// 加上全部「启用、未删除」参与方客户（<see cref="ContainerLoadingList.Id"/> 为正时）；
    /// 两处都拿不到时为空集合（由调用方 fail closed，绝不降级为「任意客户」）。
    /// </summary>
    public static async Task<HashSet<long>> LoadMembershipCustomerIdsAsync(
        IErpDbContext db, ContainerLoadingList entity, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(entity);

        var members = new HashSet<long>();
        if (entity.CustomerId > 0) members.Add(entity.CustomerId);
        if (entity.Id > 0)
        {
            foreach (var id in await LoadingListAuthorizationRules
                         .LoadActiveParticipantCustomerIdsAsync(db, entity.Id, ct))
            {
                if (id > 0) members.Add(id);
            }
        }
        return members;
    }

    /// <summary>
    /// 来源出库明细单位是否与商品基础单位兼容：空单位按基础单位处理，否则必须等于商品基础单位；
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
    /// 读取装柜清单明细已显式链接的来源**销售出库单** Id（升序、去重；有界只读查询）。
    /// 供调用方在取得任何写锁之前确定需要锁定的上游出库行，实现确定性锁序（升序，避免锁环）。
    /// </summary>
    public static async Task<List<long>> LoadLinkedStockOutIdsAsync(
        IErpDbContext db, ContainerLoadingList entity, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(entity);

        var detailIds = entity.Details
            .Where(d => !d.IsDeleted && d.SourceStockOutDetailId is > 0)
            .Select(d => d.SourceStockOutDetailId!.Value)
            .Distinct().ToList();
        if (detailIds.Count == 0) return new List<long>();

        var ids = await db.StockOutDetails.AsNoTracking()
            .Where(d => detailIds.Contains(d.Id))
            .Select(d => d.StockOutId)
            .Distinct()
            .ToListAsync(ct);
        ids.Sort();
        return ids;
    }

    /// <summary>确定性锁序：对上游销售出库单行按 <c>StockOutId</c> 升序加更新锁（由调用方执行，非关系型后端跳过）。</summary>
    /// <remarks>
    /// 锁的实际执行留在 <c>ContainerLoadingListController</c>：关系型提供程序的 <c>SqlQueryRaw</c> 扩展
    /// 属原始 SQL 执行能力，与既有 <c>ContainerPreLoadings</c> / <c>ContainerLoadingLists</c> 行锁同一位置执行；
    /// 本类只保留锁语句常量与「需要锁定哪些出库行」有界只读解析。
    /// </remarks>
    public const string LockOrderNote =
        "审核 / 链接时锁序固定为：上游销售出库单行（StockOutId 升序，UPDLOCK, HOLDLOCK）→ 预装柜单行 → 装柜清单行。";

    /// <summary>
    /// 解析给定来源出库明细 Id 集合所涉及的来源**销售出库单** Id（升序、去重）。
    /// 供调用方在取得本次链接指派的上游行锁之前确定锁集合。
    /// </summary>
    public static async Task<List<long>> LoadStockOutIdsBySourceDetailIdsAsync(
        IErpDbContext db, IReadOnlyCollection<long> sourceStockOutDetailIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(sourceStockOutDetailIds);

        var ids = sourceStockOutDetailIds.Where(id => id > 0).Distinct().ToList();
        if (ids.Count == 0) return new List<long>();

        var stockOutIds = await db.StockOutDetails.AsNoTracking()
            .Where(d => ids.Contains(d.Id))
            .Select(d => d.StockOutId)
            .Distinct()
            .ToListAsync(ct);
        stockOutIds.Sort();
        return stockOutIds;
    }

    /// <summary>
    /// 有界、实时授权的「可链接出运证据」候选查询（只读）：返回属于本装柜清单权威客户范围、
    /// 「已审核、未删除」的销售出库明细，并显式回传父出库单 / 销售订单 / 商品 / 客户与**剩余可链接基础单位数量**。
    /// <para>范围口径：受限账号必须先通过装柜清单权威客户范围校验（调用方），候选再按成员客户过滤；
    /// 一柜多客户必须逐客户匹配，绝不放行范围外客户的出库证据。</para>
    /// <para>有界口径：先取有限候选（<see cref="MaxCandidateScan"/>）再按剩余容量过滤，最终返回不超过 <paramref name="take"/>
    /// （钳制在 1..<see cref="MaxCandidateTake"/>），绝不无界拉取。</para>
    /// </summary>
    public static async Task<List<LoadingStockOutCandidateDto>> QueryCandidatesAsync(
        IErpDbContext db, ContainerLoadingList list, SalespersonDataScope scope,
        string? keyword, int take, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(list);
        ArgumentNullException.ThrowIfNull(scope);

        if (take <= 0) take = DefaultCandidateTake;
        if (take > MaxCandidateTake) take = MaxCandidateTake;

        var membership = await LoadMembershipCustomerIdsAsync(db, list, ct);
        if (membership.Count == 0)
        {
            if (!scope.IsPrivileged)
                throw new BusinessException(LoadingListAuthorizationRules.UnlinkedSourceText, ErrorCodes.Forbidden);
            return new List<LoadingStockOutCandidateDto>();
        }
        if (!scope.IsPrivileged && membership.Any(id => !scope.AllowsCustomer(id)))
            throw new BusinessException(LoadingListAuthorizationRules.ParticipantOutOfScopeText, ErrorCodes.Forbidden);

        var memberIds = membership.ToList();
        var query =
            from d in db.StockOutDetails
            join s in db.StockOuts on d.StockOutId equals s.Id
            where !d.IsDeleted && !s.IsDeleted && s.Status == DocumentStatus.Approved
                  && d.ProductId > 0 && memberIds.Contains(s.CustomerId)
            select new { Detail = d, Shipment = s };

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var k = keyword.Trim();
            query = query.Where(x => x.Shipment.StockOutNo.Contains(k) || x.Detail.ProductName.Contains(k));
        }

        var scanned = await query
            .OrderByDescending(x => x.Shipment.Id).ThenBy(x => x.Detail.Id)
            .Take(MaxCandidateScan)
            .ToListAsync(ct);
        if (scanned.Count == 0) return new List<LoadingStockOutCandidateDto>();

        var stockOutIds = scanned.Select(x => x.Shipment.Id).Distinct().ToList();
        var productIds = scanned.Select(x => x.Detail.ProductId).Distinct().ToList();
        var salesOrderIds = scanned
            .Where(x => x.Shipment.SalesOrderId is > 0)
            .Select(x => x.Shipment.SalesOrderId!.Value).Distinct().ToList();

        var groups = await LoadSourceGroupQuantitiesAsync(db, stockOutIds, productIds, ct);
        var returnByGroup = await LoadApprovedReturnBaseQuantitiesAsync(db, stockOutIds, productIds, ct);
        var linkedByGroup = await LoadLinkedLoadingBaseQuantitiesAsync(db, 0, groups.GroupDetailIds, ct);

        var products = await db.BaseProducts.AsNoTracking()
            .Where(p => productIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
        var customers = await db.BaseCustomers.AsNoTracking()
            .Where(c => memberIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, ct);
        var orders = salesOrderIds.Count == 0
            ? new Dictionary<long, SalesOrder>()
            : await db.SalesOrders.AsNoTracking()
                .Where(o => salesOrderIds.Contains(o.Id)).ToDictionaryAsync(o => o.Id, ct);

        var results = new List<LoadingStockOutCandidateDto>();
        foreach (var row in scanned)
        {
            var key = (row.Shipment.Id, row.Detail.ProductId);
            var sourceQuantity = groups.SourceQuantityByGroup.GetValueOrDefault(key, row.Detail.Quantity);
            var returned = returnByGroup.GetValueOrDefault(key);
            var linked = linkedByGroup.GetValueOrDefault(key);
            var remaining = sourceQuantity - returned - linked;
            if (remaining <= QuantityTolerance) continue;

            products.TryGetValue(row.Detail.ProductId, out var product);
            customers.TryGetValue(row.Shipment.CustomerId, out var customer);
            var orderNo = string.Empty;
            if (row.Shipment.SalesOrderId is > 0
                && orders.TryGetValue(row.Shipment.SalesOrderId.Value, out var order))
            {
                orderNo = order.OrderNo;
            }

            results.Add(new LoadingStockOutCandidateDto
            {
                StockOutDetailId = row.Detail.Id,
                StockOutId = row.Shipment.Id,
                StockOutNo = row.Shipment.StockOutNo,
                StockOutDate = row.Shipment.StockOutDate,
                SalesOrderId = row.Shipment.SalesOrderId,
                SalesOrderNo = orderNo,
                ProductId = row.Detail.ProductId,
                ProductName = product?.ProductName ?? string.Empty,
                Spec = product?.Spec ?? row.Detail.Spec,
                Unit = product?.Unit ?? string.Empty,
                CustomerId = row.Shipment.CustomerId,
                CustomerName = customer?.CustomerName ?? string.Empty,
                SourceBaseQuantity = sourceQuantity,
                EffectiveReturnedBaseQuantity = returned,
                LinkedLoadingBaseQuantity = linked,
                RemainingBaseQuantity = remaining
            });

            if (results.Count >= take) break;
        }

        return results;
    }

    /// <summary>
    /// 校验创建 / 修改 / 提交时的显式出运证据链接（在改写任何明细之前调用）：
    /// 未链接（null）保持历史行为；显式链接必须构成权威来源（存在、未删除、已审核、商品 / 基础单位一致、
    /// 客户属于权威客户范围）且新链接数量为正，否则 fail closed 拒绝。
    /// </summary>
    public static async Task ValidateLinksAsync(
        IErpDbContext db, ContainerLoadingList entity, long? userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(entity);

        await ContainerLoadingFulfillmentRules.EnsureAuthorizedAsync(db, entity, userId, ct);

        if (entity.Details.Any(d => !d.IsDeleted && d.SourceStockOutDetailId is <= 0))
            throw BusinessException.InvalidParameter("来源销售出库明细 Id 必须为正整数");

        var linked = entity.Details
            .Where(d => !d.IsDeleted && d.SourceStockOutDetailId is > 0).ToList();
        if (linked.Count == 0) return;

        // 只有真正提交了显式出运证据链接时才要求「销售出库」菜单授权，避免影响未链接的历史流程。
        await EnsureSourceMenuAuthorizedAsync(db, userId, ct);

        var context = await ResolveSourceContextAsync(db, entity, ct);
        ValidateEachLink(entity, context);
    }

    /// <summary>
    /// 校验审核时的显式出运证据链接与累计容量上限（调用方必须已持有上游销售出库行与装柜清单行更新锁，
    /// 并处于同一可串行化事务内）。未链接（null）直接返回；判定失败即 fail closed，
    /// 不落库、不改状态 / 历史、不二次过账库存、不改财务。
    /// </summary>
    public static async Task ValidateApprovalAsync(
        IErpDbContext db, ContainerLoadingList entity, long? userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(entity);

        // 实时授权（身份 / 账号 / 既有「装柜清单」菜单 / 权威客户范围）：与 ERP-348 同一口径。
        // 说明：调用方（ContainerLoadingListController.Approve）已在同一锁内先行执行
        // ContainerLoadingFulfillmentRules.ValidateApprovalAsync（预装柜单来源与累计超装），此处不重复执行其数量判定。
        await ContainerLoadingFulfillmentRules.EnsureAuthorizedAsync(db, entity, userId, ct);

        if (entity.Details.Any(d => !d.IsDeleted && d.SourceStockOutDetailId is <= 0))
            throw BusinessException.InvalidParameter("来源销售出库明细 Id 必须为正整数");

        var linked = entity.Details
            .Where(d => !d.IsDeleted && d.SourceStockOutDetailId is > 0).ToList();
        if (linked.Count == 0) return;

        await EnsureSourceMenuAuthorizedAsync(db, userId, ct);

        var context = await ResolveSourceContextAsync(db, entity, ct);
        ValidateEachLink(entity, context);
        await EnforceCapacityAsync(db, entity, context, ct);
    }

    /// <summary>
    /// 回显装柜清单全部明细的最终链接状态（含历史未链接行）：<c>SourceStockOutDetailId</c> 为空 =
    /// 显式未链接，作为「无出运证据」的显式事实返回，绝不臆造来源单号。
    /// </summary>
    public static async Task<List<LoadingStockOutLinkLineDto>> DescribeLinksAsync(
        IErpDbContext db, ContainerLoadingList entity, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(entity);

        var details = entity.Details.Where(d => !d.IsDeleted).OrderBy(d => d.Id).ToList();
        var detailIds = details
            .Where(d => d.SourceStockOutDetailId is > 0)
            .Select(d => d.SourceStockOutDetailId!.Value).Distinct().ToList();

        var sourceRows = detailIds.Count == 0
            ? new List<StockOutDetail>()
            : await db.StockOutDetails.AsNoTracking()
                .Where(d => detailIds.Contains(d.Id)).ToListAsync(ct);
        var sourceByDetailId = sourceRows.ToDictionary(d => d.Id);

        var stockOutIds = sourceRows.Select(d => d.StockOutId).Distinct().ToList();
        var shipments = stockOutIds.Count == 0
            ? new List<StockOut>()
            : await db.StockOuts.AsNoTracking()
                .Where(s => stockOutIds.Contains(s.Id)).ToListAsync(ct);
        var shipmentById = shipments.ToDictionary(s => s.Id);

        var results = new List<LoadingStockOutLinkLineDto>();
        foreach (var detail in details)
        {
            long? stockOutId = null;
            var stockOutNo = string.Empty;
            if (detail.SourceStockOutDetailId is > 0
                && sourceByDetailId.TryGetValue(detail.SourceStockOutDetailId.Value, out var source)
                && shipmentById.TryGetValue(source.StockOutId, out var shipment))
            {
                stockOutId = shipment.Id;
                stockOutNo = shipment.StockOutNo;
            }

            results.Add(new LoadingStockOutLinkLineDto
            {
                LoadingDetailId = detail.Id,
                ProductId = detail.ProductId,
                ProductName = detail.ProductName,
                Quantity = detail.Quantity,
                SourceStockOutDetailId = detail.SourceStockOutDetailId,
                StockOutId = stockOutId,
                StockOutNo = stockOutNo
            });
        }
        return results;
    }

    // ==================== 私有助手 ====================

    /// <summary>
    /// 逐行校验显式出运证据链接：新链接数量为正、来源出库明细存在且未删除、父出库单已审核、商品一致、
    /// 基础单位语义一致、客户属于权威客户范围。任一不满足 fail closed。
    /// </summary>
    private static void ValidateEachLink(ContainerLoadingList entity, SourceLinkContext context)
    {
        if (context.Membership.Count == 0)
        {
            throw new BusinessException(LoadingListAuthorizationRules.UnlinkedSourceText, ErrorCodes.Forbidden);
        }

        foreach (var detail in entity.Details.Where(d => !d.IsDeleted))
        {
            if (detail.SourceStockOutDetailId is null) continue;
            if (detail.SourceStockOutDetailId is <= 0)
                throw BusinessException.InvalidParameter("来源销售出库明细 Id 必须为正整数");

            if (detail.ProductId <= 0)
                throw BusinessException.RuleConflict("装柜明细必须指定商品");
            if (!context.ProductsById.TryGetValue(detail.ProductId, out var product) || product.IsDeleted)
                throw BusinessException.RuleConflict($"商品 [{detail.ProductName}] 不存在或已删除，不能链接出运证据");
            if (detail.Quantity <= 0)
                throw BusinessException.InvalidParameter(
                    $"商品 [{product.ProductName}] 的装柜数量必须为正数（新链接数量必须为正）");

            if (!context.DetailsById.TryGetValue(detail.SourceStockOutDetailId.Value, out var sourceDetail))
                throw BusinessException.RuleConflict("来源销售出库明细不存在或 Id 无效，不能作为出运证据");
            if (sourceDetail.IsDeleted)
                throw BusinessException.RuleConflict("来源销售出库明细已被删除，不能作为出运证据");

            if (!context.StockOutsById.TryGetValue(sourceDetail.StockOutId, out var stockOut) || stockOut.IsDeleted)
                throw BusinessException.RuleConflict("来源销售出库单不存在或已删除");
            if (stockOut.Status != DocumentStatus.Approved)
                throw BusinessException.RuleConflict(
                    $"来源销售出库单 [{stockOut.StockOutNo}] 未审核（当前状态 {stockOut.Status}），不能作为出运证据");

            if (sourceDetail.ProductId != detail.ProductId)
                throw BusinessException.RuleConflict(
                    $"商品 [{product.ProductName}] 与来源销售出库明细商品不一致（来源为商品 Id {sourceDetail.ProductId}）");

            if (!IsBaseUnitCompatible(product, sourceDetail.Unit))
                throw BusinessException.RuleConflict(
                    $"商品 [{product.ProductName}] 的来源出库明细单位 [{sourceDetail.Unit}] 与商品基础单位 [{product.Unit}] " +
                    "不一致（基础数量语义不匹配，fail closed）");

            if (!context.Membership.Contains(stockOut.CustomerId))
                throw BusinessException.RuleConflict(
                    $"来源销售出库单 [{stockOut.StockOutNo}] 的客户不属于本装柜清单的权威客户范围" +
                    "（一柜多客户必须逐客户匹配），拒绝链接");
        }
    }

    /// <summary>
    /// 解析本单显式链接所涉及的来源出库明细 / 父出库单 / 商品 / 权威客户成员（有界只读，含软删除行以 fail closed）。
    /// </summary>
    private static async Task<SourceLinkContext> ResolveSourceContextAsync(
        IErpDbContext db, ContainerLoadingList entity, CancellationToken ct)
    {
        var sourceDetailIds = entity.Details
            .Where(d => !d.IsDeleted && d.SourceStockOutDetailId is > 0)
            .Select(d => d.SourceStockOutDetailId!.Value).Distinct().ToList();

        var details = sourceDetailIds.Count == 0
            ? new List<StockOutDetail>()
            : await db.StockOutDetails.AsNoTracking()
                .Where(d => sourceDetailIds.Contains(d.Id)).ToListAsync(ct);
        var detailsById = details.ToDictionary(d => d.Id);

        var stockOutIds = details.Select(d => d.StockOutId).Distinct().ToList();
        var stockOuts = stockOutIds.Count == 0
            ? new List<StockOut>()
            : await db.StockOuts.AsNoTracking()
                .Where(s => stockOutIds.Contains(s.Id)).ToListAsync(ct);
        var stockOutsById = stockOuts.ToDictionary(s => s.Id);

        var productIds = entity.Details
            .Where(d => !d.IsDeleted && d.ProductId > 0).Select(d => d.ProductId)
            .Concat(details.Select(d => d.ProductId))
            .Distinct().ToList();
        var products = productIds.Count == 0
            ? new List<BaseProduct>()
            : await db.BaseProducts.AsNoTracking()
                .Where(p => productIds.Contains(p.Id)).ToListAsync(ct);
        var productsById = products.ToDictionary(p => p.Id);

        var membership = await LoadMembershipCustomerIdsAsync(db, entity, ct);

        return new SourceLinkContext(detailsById, stockOutsById, productsById, membership);
    }

    /// <summary>
    /// 审核时按「来源出库单 + 商品」聚合校验累计已链接装柜数量 ≤ 来源数量 − 已生效退货：
    /// 只统计显式链接行，历史未链接数量不参与（既不计入已证明出运，也不占用容量）。
    /// </summary>
    private static async Task EnforceCapacityAsync(
        IErpDbContext db, ContainerLoadingList entity, SourceLinkContext context, CancellationToken ct)
    {
        var thisByGroup = new Dictionary<(long StockOutId, long ProductId), decimal>();
        foreach (var detail in entity.Details.Where(d => !d.IsDeleted && d.SourceStockOutDetailId is > 0))
        {
            if (!context.DetailsById.TryGetValue(detail.SourceStockOutDetailId!.Value, out var sourceDetail))
                continue; // 已在校验中 fail closed
            var key = (sourceDetail.StockOutId, sourceDetail.ProductId);
            thisByGroup[key] = thisByGroup.GetValueOrDefault(key) + detail.Quantity;
        }
        if (thisByGroup.Count == 0) return;

        var stockOutIds = thisByGroup.Keys.Select(k => k.StockOutId).Distinct().ToList();
        var productIds = thisByGroup.Keys.Select(k => k.ProductId).Distinct().ToList();

        var groups = await LoadSourceGroupQuantitiesAsync(db, stockOutIds, productIds, ct);
        var returnByGroup = await LoadApprovedReturnBaseQuantitiesAsync(db, stockOutIds, productIds, ct);
        var linkedByGroup = await LoadLinkedLoadingBaseQuantitiesAsync(db, entity.Id, groups.GroupDetailIds, ct);

        foreach (var group in thisByGroup)
        {
            var key = group.Key;
            if (!groups.SourceQuantityByGroup.TryGetValue(key, out var sourceQuantity))
                throw BusinessException.RuleConflict("来源销售出库明细已不存在或已删除，不能审核（fail closed）");

            var returned = returnByGroup.GetValueOrDefault(key);
            var capacity = sourceQuantity - returned;
            var already = linkedByGroup.GetValueOrDefault(key);
            var thisQuantity = group.Value;
            var total = already + thisQuantity;

            if (total > capacity + QuantityTolerance)
            {
                var productName = context.ProductsById.TryGetValue(key.ProductId, out var product)
                    ? product.ProductName
                    : $"商品 Id {key.ProductId}";
                throw BusinessException.RuleConflict(
                    $"商品 [{productName}] 累计已链接装柜数量 {total} 超过来源销售出库可装柜容量 {capacity}" +
                    $"（来源 {sourceQuantity} − 已生效退货 {returned}；其它已审核装柜 {already} + 本次 {thisQuantity}）");
            }
        }
    }

    /// <summary>按「来源出库单 + 商品」聚合来源出库明细数量，并保留组内明细 Id 集合（重复行先聚合，不臆造行）。</summary>
    private static async Task<SourceGroupQuantities> LoadSourceGroupQuantitiesAsync(
        IErpDbContext db, List<long> stockOutIds, List<long> productIds, CancellationToken ct)
    {
        var result = new SourceGroupQuantities();
        if (stockOutIds.Count == 0 || productIds.Count == 0) return result;

        var rows = await db.StockOutDetails.AsNoTracking()
            .Where(d => !d.IsDeleted && stockOutIds.Contains(d.StockOutId) && productIds.Contains(d.ProductId))
            .Select(d => new { d.Id, d.StockOutId, d.ProductId, d.Quantity })
            .ToListAsync(ct);

        foreach (var row in rows)
        {
            var key = (row.StockOutId, row.ProductId);
            result.SourceQuantityByGroup[key] =
                result.SourceQuantityByGroup.GetValueOrDefault(key) + row.Quantity;
            if (!result.GroupDetailIds.TryGetValue(key, out var set))
                result.GroupDetailIds[key] = set = new HashSet<long>();
            set.Add(row.Id);
        }
        return result;
    }

    /// <summary>
    /// 按「来源出库单 + 商品」聚合已生效（已审核、未删除）销售退货数量：退货缺明细 Id，故按商品保守聚合；
    /// 单位可折算为基础单位时按基础单位，无法折算时按原始数量计入（不忽略退货，避免高估可装柜容量）。
    /// </summary>
    private static async Task<Dictionary<(long StockOutId, long ProductId), decimal>> LoadApprovedReturnBaseQuantitiesAsync(
        IErpDbContext db, List<long> stockOutIds, List<long> productIds, CancellationToken ct)
    {
        var result = new Dictionary<(long, long), decimal>();
        if (stockOutIds.Count == 0 || productIds.Count == 0) return result;

        var rows = await (from r in db.SalesReturns
                          join rd in db.SalesReturnDetails on r.Id equals rd.SalesReturnId
                          where !r.IsDeleted && r.Status == DocumentStatus.Approved
                                && r.SourceStockOutId != null && stockOutIds.Contains(r.SourceStockOutId.Value)
                                && !rd.IsDeleted && rd.ProductId != null && productIds.Contains(rd.ProductId.Value)
                          select new
                          {
                              StockOutId = r.SourceStockOutId!.Value,
                              ProductId = rd.ProductId!.Value,
                              rd.Unit,
                              rd.Quantity
                          }).ToListAsync(ct);
        if (rows.Count == 0) return result;

        var products = await db.BaseProducts.AsNoTracking()
            .Where(p => productIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);

        foreach (var row in rows)
        {
            var key = (row.StockOutId, row.ProductId);
            var baseQuantity = row.Quantity;
            if (products.TryGetValue(row.ProductId, out var product)
                && SalesReturnSourceRules.TryBaseUnitQuantity(product, row.Unit, row.Quantity, out var converted))
            {
                baseQuantity = converted;
            }
            result[key] = result.GetValueOrDefault(key) + baseQuantity;
        }
        return result;
    }

    /// <summary>
    /// 按「来源出库单 + 商品」聚合**其它已审核装柜清单**已链接占用的基础单位数量（只统计显式链接行；
    /// 历史未链接明细不计入，绝不被当作已证明出运；<paramref name="excludeLoadingListId"/> 用于审核时排除本单）。
    /// </summary>
    private static async Task<Dictionary<(long StockOutId, long ProductId), decimal>> LoadLinkedLoadingBaseQuantitiesAsync(
        IErpDbContext db, long excludeLoadingListId,
        Dictionary<(long StockOutId, long ProductId), HashSet<long>> groupDetailIds, CancellationToken ct)
    {
        var result = new Dictionary<(long, long), decimal>();
        var detailToGroup = new Dictionary<long, (long StockOutId, long ProductId)>();
        foreach (var kv in groupDetailIds)
        {
            foreach (var id in kv.Value) detailToGroup[id] = kv.Key;
        }
        if (detailToGroup.Count == 0) return result;

        var allDetailIds = detailToGroup.Keys.ToList();
        var rows = await (from l in db.ContainerLoadingLists
                          join d in db.ContainerLoadingDetails on l.Id equals d.LoadingListId
                          where !l.IsDeleted && l.Status == DocumentStatus.Approved && l.Id != excludeLoadingListId
                                && !d.IsDeleted && d.SourceStockOutDetailId != null
                                && allDetailIds.Contains(d.SourceStockOutDetailId.Value)
                          select new { d.SourceStockOutDetailId, d.Quantity })
                         .ToListAsync(ct);

        foreach (var row in rows)
        {
            if (row.SourceStockOutDetailId is null) continue;
            if (!detailToGroup.TryGetValue(row.SourceStockOutDetailId.Value, out var key)) continue;
            result[key] = result.GetValueOrDefault(key) + row.Quantity;
        }
        return result;
    }

    /// <summary>权威来源上下文（只读事实：来源出库明细 / 父出库单 / 商品 / 权威客户成员）。</summary>
    private sealed class SourceLinkContext
    {
        public SourceLinkContext(
            Dictionary<long, StockOutDetail> detailsById,
            Dictionary<long, StockOut> stockOutsById,
            Dictionary<long, BaseProduct> productsById,
            HashSet<long> membership)
        {
            DetailsById = detailsById;
            StockOutsById = stockOutsById;
            ProductsById = productsById;
            Membership = membership;
        }

        public Dictionary<long, StockOutDetail> DetailsById { get; }
        public Dictionary<long, StockOut> StockOutsById { get; }
        public Dictionary<long, BaseProduct> ProductsById { get; }
        public HashSet<long> Membership { get; }
    }

    /// <summary>「来源出库单 + 商品」组的来源数量与组内来源明细 Id 集合。</summary>
    private sealed class SourceGroupQuantities
    {
        public Dictionary<(long StockOutId, long ProductId), decimal> SourceQuantityByGroup { get; } = new();
        public Dictionary<(long StockOutId, long ProductId), HashSet<long>> GroupDetailIds { get; } = new();
    }
}

