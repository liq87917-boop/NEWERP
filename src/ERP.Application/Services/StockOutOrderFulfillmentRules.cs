using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ERP.Application.Services;

/// <summary>
/// 销售出库衔接来源订单并防止累计超发（ERP-343）。
/// <para>只复用既有 <see cref="StockOut.SalesOrderId"/> 显式链接：未链接（null）的历史单据不做任何校验，行为保持不变。</para>
/// <para>显式链接构成「权威来源」要求订单存在、未删除、已审核、客户一致、每商品有且仅有一行可折算的订单明细，
/// 且出库明细单位与商品基础单位兼容；任一不满足即在审核前 fail closed 拒绝履约，绝不因来源语义不明确而跳过数量护栏。</para>
/// <para>已取消 / 已驳回 / 待提交 / 已提交的出库单不计入已审核数量，取消后自动释放剩余额度；跨单位、跨币种不合计。</para>
/// <para>本类只做纯内存判定与有界查询，不落库、不改单据、不开启事务；同单并发审核的串行化由调用方
/// （<c>StockOutController.Approve</c>）在同一关系型事务内对来源订单行加更新锁完成。</para>
/// </summary>
public static class StockOutOrderFulfillmentRules
{
    /// <summary>数量比较容差（与出库基础单位数量保留 4 位小数同口径，吸收装箱换算的舍入尾差）</summary>
    public const decimal QuantityTolerance = 0.0001m;

    /// <summary>发货口径说明（界面 / 文档同源）</summary>
    public const string RuleText =
        "销售出库单关联销售订单后，仅当链接权威（订单已审核、客户一致、每商品唯一兼容明细行）时，审核时累计" +
        "「以该订单为来源、未删除、已审核」的出库数量（出库明细已折算基础单位）加上本单数量不得超过订单授权数量（按商品逐行比对）；" +
        "未关联的出库单不做累计校验；显式链接无效（订单不存在 / 已删除 / 未审核 / 客户不一致 / 商品或单位不兼容 / 重复明细）" +
        "在审核前直接拒绝履约，绝不猜测重复订单行、绝不臆造授权数量。";

    /// <summary>来源候选查询默认返回条数（&lt;= 0 时生效；有界，绝不无界拉取）</summary>
    public const int DefaultCandidateTake = 50;

    /// <summary>来源候选查询返回条数上限（有界，绝不无界拉取）</summary>
    public const int MaxCandidateTake = 200;

    /// <summary>来源候选扫描上限（有界：先取有限销售订单，再在内存按「订单 + 商品」聚合 / 计算剩余可发数量）</summary>
    public const int MaxCandidateScan = 500;

    /// <summary>关键字长度上限（超长截断，避免无界匹配）</summary>
    public const int MaxKeywordLength = 100;

    /// <summary>来源不存在 / 范围外 / 已删除的拒绝文案（不泄露归属）</summary>
    public const string SourceNotFoundText = "来源销售订单不存在、已删除或不在当前账号数据范围内";

    /// <summary>来源未审核 / 已取消 / 已驳回的拒绝文案（必须重新显式选择来源）</summary>
    public const string SourceNotApprovedText = "来源销售订单未审核，不能作为出库依据（必须重新显式选择来源）";

    /// <summary>不可用原因文案（剩余可发数量为 0）</summary>
    public const string ZeroCapacityText = "剩余可发数量为 0：订单授权数量已被已审核出库占用";

    /// <summary>不可用原因文案（订单同商品重复 / 歧义明细，授权数量不唯一）</summary>
    public const string AmbiguousLineText = "来源销售订单同商品存在多条明细，授权数量不唯一（不可用）";

    /// <summary>不可用原因文案（订单明细单位无法折算为基础单位）</summary>
    public const string UnknownUnitText = "来源订单明细单位无法折算为商品基础单位（不可用）";

    /// <summary>不可用原因文案（商品主数据缺失 / 负数量等损坏证据）</summary>
    public const string CorruptSourceText = "来源证据损坏（商品主数据缺失或负数量），不可用";

    /// <summary>候选 / 详情口径文案（接口 / 文档同源）</summary>
    public const string CandidateRuleText =
        "销售出库来源候选只返回当前账号客户数据范围之内「未删除、已审核」的销售订单，并按「来源销售订单 + 商品」聚合：" +
        "订单授权数量按 ERP-343 口径折算为基础单位（订单行单位等于商品基础单位直接使用，等于装箱单位按 UnitsPerPackage 折算），" +
        "扣除「以该订单为来源、未删除、已审核」的出库基础单位数量，得到剩余可发数量（待提交 / 已提交预留不计入，绝不当作已过账发货）；" +
        "关键字只在订单号 / 客户名称 / 商品名称 / 规格内做有界匹配（空串 = 不过滤，绝不用于推断来源）；" +
        "同商品重复 / 歧义明细、单位无法折算、商品主数据缺失、负数量、已发货满额的来源一律标记为不可用（available=false + 原因），绝不猜容量；" +
        "候选 / 详情是只读投影：不落库、不改单据 / 库存 / 流水、不消耗单据号、不新增表 / 列 / 菜单 / 权限或用户授权，" +
        "也绝不暴露币种 / 金额 / 条款等无关订单字段。";

    /// <summary>候选 / 详情返回条数钳制（&lt;= 0 取默认值，上限 <see cref="MaxCandidateTake"/>，绝不无界拉取）。</summary>
    public static int ClampTake(int take)
        => take <= 0 ? DefaultCandidateTake : Math.Min(take, MaxCandidateTake);

    /// <summary>关键字规范化（去首尾空白并截断到 <see cref="MaxKeywordLength"/>；空串 = 不过滤）。</summary>
    public static string NormalizeKeyword(string? keyword)
    {
        var kw = (keyword ?? string.Empty).Trim();
        return kw.Length <= MaxKeywordLength ? kw : kw[..MaxKeywordLength];
    }

    /// <summary>
    /// 校验审核时的来源订单链接与累计数量上限。调用方必须已把本单明细折算为基础单位（<c>StockUnitConversion.NormalizeAsync</c>）。
    /// <para>未链接（null）直接返回；显式链接无效时 fail closed 抛业务异常，且不落库、不改库存 / 流水 / 单据状态。</para>
    /// </summary>
    public static async Task ValidateApprovalAsync(IErpDbContext db, StockOut entity, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(entity);

        if (entity.SalesOrderId is null) return;

        var link = await ResolveAuthoritativeLinkOrThrowAsync(db, entity, ct);
        await EnforceCumulativeAsync(db, entity, link, ct);
    }

    /// <summary>
    /// 校验创建 / 更新时的显式来源链接。未链接（null）保持历史行为；显式链接必须为正整数且构成权威来源，
    /// 否则 fail closed 拒绝保存，避免把无效来源链接落到待提交 / 待审核单据上。
    /// </summary>
    public static async Task ValidateLinkAsync(IErpDbContext db, StockOut entity, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(entity);

        if (entity.SalesOrderId is null) return;
        if (entity.SalesOrderId is not > 0)
            throw BusinessException.InvalidParameter("来源销售订单 Id 必须为正整数");

        await ResolveAuthoritativeLinkOrThrowAsync(db, entity, ct);
    }

    // ==================== ERP-376：来源候选 / 详情（只读、有界） ====================

    /// <summary>
    /// 有界只读候选查询：返回当前账号客户数据范围内、客户匹配且关键字命中的「已审核、未删除」销售订单
    /// 可发货商品行（按「来源销售订单 + 商品」聚合，授权数量按 ERP-343 折算基础单位，扣除已审核出库数量）。
    /// <para>先按客户数据范围硬收窄（<paramref name="scopedOrders"/> 已由调用方用 ERP-097 权威范围下推，绝不返回范围外客户），
    /// 再做显式筛选，最后只取最近 <see cref="MaxCandidateScan"/> 张销售订单与至多 <see cref="MaxCandidateTake"/> 条候选。</para>
    /// <para>重复 / 歧义明细、零容量 / 负数量 / 单位无法折算 / 商品缺失的候选保留并显式标记
    /// <c>Available = false</c> + 原因，绝不猜容量；只读投影：不落库、不改单据 / 库存 / 流水、不新增表 / 列 / 菜单 / 权限。</para>
    /// </summary>
    public static async Task<IReadOnlyList<StockOutSourceCandidateDto>> QuerySourceCandidatesAsync(
        IErpDbContext db, IQueryable<SalesOrder> scopedOrders, long? customerId, string? keyword, int take,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(scopedOrders);

        var bounded = ClampTake(take);
        var kw = NormalizeKeyword(keyword);

        var source = scopedOrders.AsNoTracking()
            .Where(o => !o.IsDeleted && o.Status == DocumentStatus.Approved);
        if (customerId is > 0) source = source.Where(o => o.CustomerId == customerId.Value);

        var orders = await source.OrderByDescending(o => o.Id)
            .Take(MaxCandidateScan)
            .Include(o => o.Details)
            .ToListAsync(ct);

        var lines = await BuildCandidatesAsync(db, orders, kw, ct);
        return lines
            .OrderByDescending(l => l.Available)
            .ThenByDescending(l => l.RemainingBaseQuantity)
            .ThenByDescending(l => l.SalesOrderId)
            .ThenBy(l => l.ProductId)
            .Take(bounded)
            .ToList();
    }

    /// <summary>
    /// 来源详情（只读、有界）：返回一张权威来源销售订单的表头 + 全部商品剩余可发行。
    /// <para>客户范围外 / 不存在 / 已删除的来源按「不存在」拒绝（不泄露归属）；未审核 / 已取消 / 已驳回按冲突拒绝；
    /// 任一失败都不写库、不改写任何已保存的来源链接，界面必须重新显式选择来源。</para>
    /// </summary>
    public static async Task<StockOutSourceDetailDto> ResolveSourceDetailAsync(
        IErpDbContext db, IQueryable<SalesOrder> scopedOrders, long salesOrderId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(scopedOrders);
        if (salesOrderId <= 0)
            throw BusinessException.InvalidParameter("来源销售订单 Id 必须为正整数");

        var order = await scopedOrders.AsNoTracking().Include(o => o.Details)
            .FirstOrDefaultAsync(o => o.Id == salesOrderId, ct);
        if (order is null || order.IsDeleted)
            throw BusinessException.NotFound(SourceNotFoundText);
        if (order.Status != DocumentStatus.Approved)
            throw BusinessException.RuleConflict(SourceNotApprovedText);

        var lines = (await BuildCandidatesAsync(db, new List<SalesOrder> { order }, string.Empty, ct))
            .OrderByDescending(l => l.Available)
            .ThenBy(l => l.ProductId)
            .ToList();
        var customerNames = await LoadCustomerNamesAsync(db, new List<long> { order.CustomerId }, ct);
        var available = lines.Any(l => l.Available);

        return new StockOutSourceDetailDto
        {
            SalesOrderId = order.Id,
            OrderNo = order.OrderNo,
            OrderDate = order.OrderDate,
            CustomerId = order.CustomerId,
            CustomerName = customerNames.GetValueOrDefault(order.CustomerId, string.Empty),
            Available = available,
            UnavailableReason = available
                ? string.Empty
                : lines.FirstOrDefault()?.UnavailableReason ?? ZeroCapacityText,
            Lines = lines
        };
    }

    // ==================== ERP-376 私有助手 ====================

    /// <summary>
    /// 按「来源销售订单 + 商品」构建候选行：授权数量按 ERP-343 口径折算基础单位，扣除「以本订单为来源、未删除、已审核」
    /// 的出库基础单位数量。重复明细 / 单位未知 / 商品缺失一律显式不可用，绝不猜授权数量。
    /// </summary>
    private static async Task<List<StockOutSourceCandidateDto>> BuildCandidatesAsync(
        IErpDbContext db, IReadOnlyList<SalesOrder> orders, string keyword, CancellationToken ct)
    {
        var result = new List<StockOutSourceCandidateDto>();
        if (orders.Count == 0) return result;

        var orderIds = orders.Select(o => o.Id).ToList();
        var customerNames = await LoadCustomerNamesAsync(
            db, orders.Select(o => o.CustomerId).Distinct().ToList(), ct);

        var lines = new List<(long OrderId, SalesOrderDetail Line)>();
        foreach (var order in orders)
        {
            foreach (var detail in order.Details.Where(d => !d.IsDeleted && d.ProductId > 0))
                lines.Add((order.Id, detail));
        }
        if (lines.Count == 0) return result;

        var productIds = lines.Select(l => l.Line.ProductId).Distinct().ToList();

        // 已审核出库数量：与 EnforceCumulativeAsync 同一口径——以本订单为来源、未删除、已审核，出库明细已折算基础单位，
        // 按商品直接合计（绝不按订单行重复相乘）；待提交 / 已提交预留一律不计入，绝不当作已过账发货。
        var shippedRows = await (
                from s in db.StockOuts
                join d in db.StockOutDetails on s.Id equals d.StockOutId
                where s.SalesOrderId != null
                      && orderIds.Contains(s.SalesOrderId.Value)
                      && !s.IsDeleted
                      && s.Status == DocumentStatus.Approved
                      && !d.IsDeleted
                      && d.ProductId > 0
                select new { OrderId = s.SalesOrderId!.Value, d.ProductId, d.Quantity })
            .ToListAsync(ct);

        var products = await db.BaseProducts.AsNoTracking()
            .Where(p => productIds.Contains(p.Id) && !p.IsDeleted)
            .ToDictionaryAsync(p => p.Id, ct);

        var shipped = shippedRows
            .GroupBy(r => (r.OrderId, r.ProductId))
            .ToDictionary(g => g.Key, g => g.Sum(r => r.Quantity));

        var ordersById = orders.ToDictionary(o => o.Id);

        foreach (var group in lines.GroupBy(l => (l.OrderId, l.Line.ProductId)))
        {
            var (orderId, productId) = group.Key;
            var order = ordersById[orderId];
            var groupLines = group.ToList();
            var first = groupLines[0].Line;
            var shippedQty = shipped.GetValueOrDefault((orderId, productId), 0m);

            products.TryGetValue(productId, out var product);

            var authorized = 0m;
            bool available;
            string reason;

            if (product is null || first.Quantity < 0)
            {
                // 商品主数据缺失 / 负数量：证据损坏，绝不猜测授权数量。
                available = false;
                reason = CorruptSourceText;
            }
            else if (groupLines.Count > 1)
            {
                // 同商品多条明细：授权数量不唯一（与 ERP-343 ValidateLinkAsync 同口径 fail closed）。
                available = false;
                reason = AmbiguousLineText;
            }
            else if (!TryBaseUnitQuantity(product, first, out authorized))
            {
                available = false;
                reason = UnknownUnitText;
            }
            else
            {
                var remaining = authorized - shippedQty;
                if (remaining < 0) remaining = 0m;
                if (authorized <= 0 || remaining <= QuantityTolerance)
                {
                    available = false;
                    reason = ZeroCapacityText;
                }
                else
                {
                    available = true;
                    reason = string.Empty;
                }
            }

            var remainingQty = available ? Math.Max(authorized - shippedQty, 0m) : 0m;

            var line = new StockOutSourceCandidateDto
            {
                SalesOrderId = order.Id,
                OrderNo = order.OrderNo,
                OrderDate = order.OrderDate,
                CustomerId = order.CustomerId,
                CustomerName = customerNames.GetValueOrDefault(order.CustomerId, string.Empty),
                ProductId = productId,
                ProductName = product?.ProductName ?? first.ProductName,
                Spec = product?.Spec ?? first.Spec,
                BaseUnit = product?.Unit ?? string.Empty,
                AuthorizedBaseQuantity = authorized,
                ShippedBaseQuantity = shippedQty,
                RemainingBaseQuantity = remainingQty,
                Available = available,
                UnavailableReason = reason
            };

            if (keyword.Length > 0 && !MatchesKeyword(line, keyword)) continue;
            result.Add(line);
        }

        return result;
    }

    /// <summary>关键字有界匹配（订单号 / 客户名称 / 商品名称 / 规格），大小写不敏感；绝不用于推断来源。</summary>
    private static bool MatchesKeyword(StockOutSourceCandidateDto line, string keyword)
    {
        static bool Hit(string? value, string kw)
            => !string.IsNullOrEmpty(value) && value.Contains(kw, StringComparison.OrdinalIgnoreCase);

        return Hit(line.OrderNo, keyword)
            || Hit(line.CustomerName, keyword)
            || Hit(line.ProductName, keyword)
            || Hit(line.Spec, keyword);
    }

    private static async Task<Dictionary<long, string>> LoadCustomerNamesAsync(
        IErpDbContext db, List<long> customerIds, CancellationToken ct)
        => customerIds.Count == 0
            ? new Dictionary<long, string>()
            : await db.BaseCustomers.AsNoTracking()
                .Where(c => customerIds.Contains(c.Id))
                .ToDictionaryAsync(c => c.Id, c => c.CustomerName, ct);

    // ==================== 私有助手 ====================

    private static async Task<LinkContext> ResolveAuthoritativeLinkOrThrowAsync(IErpDbContext db, StockOut entity,
        CancellationToken ct)
    {
        var order = await db.SalesOrders.AsNoTracking().Include(o => o.Details)
            .FirstOrDefaultAsync(o => o.Id == entity.SalesOrderId!.Value && !o.IsDeleted, ct)
            ?? throw BusinessException.RuleConflict("来源销售订单不存在或已删除");
        if (order.Status != DocumentStatus.Approved)
            throw BusinessException.RuleConflict("来源销售订单未审核，不能作为出库依据");
        if (entity.CustomerId != order.CustomerId)
            throw BusinessException.RuleConflict("出库单客户与来源销售订单客户不一致");

        var productIds = entity.Details.Where(d => !d.IsDeleted && d.ProductId > 0)
            .Select(d => d.ProductId).Distinct().ToList();
        var products = productIds.Count == 0
            ? new Dictionary<long, BaseProduct>()
            : await db.BaseProducts.AsNoTracking()
                .Where(p => productIds.Contains(p.Id) && !p.IsDeleted)
                .ToDictionaryAsync(p => p.Id, ct);

        var lines = order.Details.Where(l => !l.IsDeleted).ToList();

        foreach (var detail in entity.Details.Where(d => !d.IsDeleted))
        {
            if (detail.ProductId <= 0)
                throw BusinessException.RuleConflict("出库明细必须指定商品");
            if (!products.TryGetValue(detail.ProductId, out var product))
                throw BusinessException.RuleConflict($"商品 [{detail.ProductName}] 不存在或已删除");
            var matches = lines.Where(l => l.ProductId == detail.ProductId).ToList();
            if (matches.Count == 0)
                throw BusinessException.RuleConflict($"来源销售订单无商品 [{product.ProductName}] 的明细行");
            if (matches.Count > 1)
                throw BusinessException.RuleConflict($"来源销售订单存在多条商品 [{product.ProductName}] 明细，授权数量不唯一");
            if (!TryBaseUnitQuantity(product, matches[0], out _))
                throw BusinessException.RuleConflict($"来源销售订单商品 [{product.ProductName}] 明细单位无法折算为基础单位");
            if (!IsStockOutUnitCompatible(product, detail))
                throw BusinessException.RuleConflict($"出库明细单位与商品 [{product.ProductName}] 基础单位不兼容");
        }

        return new LinkContext(order, lines, products);
    }

    private static async Task EnforceCumulativeAsync(IErpDbContext db, StockOut entity, LinkContext link,
        CancellationToken ct)
    {
        var orderId = link.Order.Id;

        // 已审核出库数量：以本单为来源、未删除、已审核、且非本单的出库明细数量按商品汇总。
        // 本单由调用方在拿到订单锁后进入本方法，因此并发同单审核会被串行化，后到者能看到先到者已提交的数量。
        var approvedRows = await (
                from s in db.StockOuts
                join d in db.StockOutDetails on s.Id equals d.StockOutId
                where s.SalesOrderId == orderId
                      && !s.IsDeleted
                      && s.Status == DocumentStatus.Approved
                      && s.Id != entity.Id
                      && !d.IsDeleted
                      && d.ProductId > 0
                select new { d.ProductId, d.Quantity })
            .ToListAsync(ct);

        var already = approvedRows.GroupBy(r => r.ProductId)
            .ToDictionary(g => g.Key, g => g.Sum(r => r.Quantity));

        foreach (var group in entity.Details.Where(d => !d.IsDeleted && d.ProductId > 0 && d.Quantity > 0)
                     .GroupBy(d => d.ProductId))
        {
            var productId = group.Key;
            var product = link.Products[productId];
            var line = link.Lines.Single(l => l.ProductId == productId);
            TryBaseUnitQuantity(product, line, out var authorized);

            var shipped = already.TryGetValue(productId, out var value) ? value : 0m;
            var thisQuantity = group.Sum(d => d.Quantity);
            var total = shipped + thisQuantity;

            if (total > authorized + QuantityTolerance)
            {
                throw BusinessException.RuleConflict(
                    $"商品 [{product.ProductName}] 累计出库数量 {total} 超过来源销售订单授权数量 {authorized}" +
                    $"（已审核 {shipped} + 本次 {thisQuantity}）");
            }
        }
    }


    /// <summary>
    /// 订单行数量 → 基础单位数量：订单行单位等于商品基础单位直接使用；等于装箱单位时按 UnitsPerPackage 折算；
    /// 其他 / 空单位或无效装箱数返回 false（由调用方 fail closed）。
    /// </summary>
    private static bool TryBaseUnitQuantity(BaseProduct product, SalesOrderDetail line, out decimal baseQuantity)
    {
        baseQuantity = 0m;

        var lineUnit = (line.Unit ?? string.Empty).Trim();
        var baseUnit = (product.Unit ?? string.Empty).Trim();
        var packageUnit = (product.PackageUnit ?? string.Empty).Trim();

        if (baseUnit.Length > 0 && lineUnit.Equals(baseUnit, StringComparison.OrdinalIgnoreCase))
        {
            baseQuantity = line.Quantity;
            return true;
        }

        if (packageUnit.Length == 0 || !lineUnit.Equals(packageUnit, StringComparison.OrdinalIgnoreCase))
            return false;
        if (product.UnitsPerPackage <= 0)
            return false;

        baseQuantity = Math.Round(line.Quantity * product.UnitsPerPackage, 4);
        return true;
    }

    /// <summary>
    /// 出库明细单位是否与商品基础单位兼容：空单位按基础单位处理（出库流水读取时回退商品基础单位）；
    /// 装箱单位已由 <c>StockUnitConversion.NormalizeAsync</c> 折算为基础单位，因此此处只认基础单位。
    /// </summary>
    private static bool IsStockOutUnitCompatible(BaseProduct product, StockOutDetail detail)
    {
        var unit = (detail.Unit ?? string.Empty).Trim();
        if (unit.Length == 0) return true;

        var baseUnit = (product.Unit ?? string.Empty).Trim();
        return baseUnit.Length > 0 && unit.Equals(baseUnit, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class LinkContext
    {
        public LinkContext(SalesOrder order, IReadOnlyList<SalesOrderDetail> lines,
            IReadOnlyDictionary<long, BaseProduct> products)
        {
            Order = order;
            Lines = lines;
            Products = products;
        }

        public SalesOrder Order { get; }
        public IReadOnlyList<SalesOrderDetail> Lines { get; }
        public IReadOnlyDictionary<long, BaseProduct> Products { get; }
    }
}

