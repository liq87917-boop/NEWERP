using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ERP.Application.Services;

/// <summary>
/// 采购入库衔接来源订单并防止累计超收（ERP-342）。
/// <para>只复用既有 <see cref="StockIn.PurchaseOrderId"/> 显式链接：未链接（null）的历史单据不做任何校验，行为保持不变。</para>
/// <para>显式链接构成「权威来源」要求订单存在、未删除、已审核、供应商一致、每商品有且仅有一行可折算的订单明细，
/// 且入库明细单位与商品基础单位兼容；任一不满足即在审核前 fail closed 拒绝履约，绝不因来源语义不明确而跳过数量护栏。</para>
/// <para>ERP-033 成本回退只作用于「价格 / 汇率」估值缺失（不影响订单身份与数量授权）：权威来源的入库单仍按
/// 基础单位累计校验，成本则回退移动加权平均，不把「有效来源 + 成本不可用」与「无效来源」混为一谈。</para>
/// <para>已取消 / 已驳回 / 待提交 / 已提交的入库单不计入已审核数量，取消后自动释放剩余额度；跨单位、跨币种不合计。</para>
/// <para>本类只做纯内存判定与有界查询，不落库、不改单据、不开启事务；同单并发审核的串行化由调用方
/// （<c>StockInController.Approve</c>）在同一关系型事务内对来源订单行加更新锁完成。</para>
/// </summary>
public static class StockInOrderFulfillmentRules
{
    /// <summary>数量比较容差（与入库基础单位数量保留 4 位小数同口径，吸收装箱换算的舍入尾差）</summary>
    public const decimal QuantityTolerance = 0.0001m;

    /// <summary>收货口径说明（界面 / 文档同源）</summary>
    public const string RuleText =
        "采购入库单关联采购订单后，仅当链接权威（订单已审核、供应商一致、每商品唯一兼容明细行）时，审核时累计" +
        "「以该订单为来源、未删除、已审核」的入库数量（入库明细已折算基础单位）加上本单数量不得超过订单授权数量（按商品逐行比对）；" +
        "未关联的入库单不做累计校验；显式链接无效（订单不存在 / 已删除 / 未审核 / 供应商不一致 / 商品或单位不兼容 / 重复明细）" +
        "在审核前直接拒绝履约，绝不猜测重复订单行、绝不臆造授权数量。";

    /// <summary>
    /// 校验审核时的来源订单链接与累计数量上限。调用方必须已把本单明细折算为基础单位（<c>StockUnitConversion.NormalizeAsync</c>）。
    /// <para>未链接（null）直接返回；显式链接无效时 fail closed 抛业务异常，且不落库、不改库存 / 流水 / 单据状态。</para>
    /// </summary>
    public static async Task ValidateApprovalAsync(IErpDbContext db, StockIn entity, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(entity);

        if (entity.PurchaseOrderId is null) return;

        var link = await ResolveAuthoritativeLinkOrThrowAsync(db, entity, ct);
        await EnforceCumulativeAsync(db, entity, link, ct);
    }

    /// <summary>
    /// 校验创建 / 更新时的显式来源链接。未链接（null）保持历史行为；显式链接必须为正整数且构成权威来源，
    /// 否则 fail closed 拒绝保存，避免把无效来源链接落到待提交 / 待审核单据上。
    /// </summary>
    public static async Task ValidateLinkAsync(IErpDbContext db, StockIn entity, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(entity);

        if (entity.PurchaseOrderId is null) return;
        if (entity.PurchaseOrderId is not > 0)
            throw BusinessException.InvalidParameter("来源采购订单 Id 必须为正整数");

        await ResolveAuthoritativeLinkOrThrowAsync(db, entity, ct);
    }

    // ==================== 私有助手 ====================

    private static async Task<LinkContext> ResolveAuthoritativeLinkOrThrowAsync(IErpDbContext db, StockIn entity,
        CancellationToken ct)
    {
        var order = await db.PurchaseOrders.AsNoTracking().Include(o => o.Details)
            .FirstOrDefaultAsync(o => o.Id == entity.PurchaseOrderId!.Value && !o.IsDeleted, ct)
            ?? throw BusinessException.RuleConflict("来源采购订单不存在或已删除");
        if (order.Status != DocumentStatus.Approved)
            throw BusinessException.RuleConflict("来源采购订单未审核，不能作为入库依据");
        if (entity.SupplierId != order.SupplierId)
            throw BusinessException.RuleConflict("入库单供应商与来源采购订单供应商不一致");

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
                throw BusinessException.RuleConflict("入库明细必须指定商品");
            if (!products.TryGetValue(detail.ProductId, out var product))
                throw BusinessException.RuleConflict($"商品 [{detail.ProductName}] 不存在或已删除");
            var matches = lines.Where(l => l.ProductId == detail.ProductId).ToList();
            if (matches.Count == 0)
                throw BusinessException.RuleConflict($"来源采购订单无商品 [{product.ProductName}] 的明细行");
            if (matches.Count > 1)
                throw BusinessException.RuleConflict($"来源采购订单存在多条商品 [{product.ProductName}] 明细，授权数量不唯一");
            if (!TryBaseUnitQuantity(product, matches[0], out _))
                throw BusinessException.RuleConflict($"来源采购订单商品 [{product.ProductName}] 明细单位无法折算为基础单位");
            if (!IsStockInUnitCompatible(product, detail))
                throw BusinessException.RuleConflict($"入库明细单位与商品 [{product.ProductName}] 基础单位不兼容");
        }

        return new LinkContext(order, lines, products);
    }

    private static async Task EnforceCumulativeAsync(IErpDbContext db, StockIn entity, LinkContext link,
        CancellationToken ct)
    {
        var orderId = link.Order.Id;

        // 已审核入库数量：以本单为来源、未删除、已审核、且非本单的入库明细数量按商品汇总。
        // 本单由调用方在拿到订单锁后进入本方法，因此并发同单审核会被串行化，后到者能看到先到者已提交的数量。
        var approvedRows = await (
                from s in db.StockIns
                join d in db.StockInDetails on s.Id equals d.StockInId
                where s.PurchaseOrderId == orderId
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

            var received = already.TryGetValue(productId, out var value) ? value : 0m;
            var thisQuantity = group.Sum(d => d.Quantity);
            var total = received + thisQuantity;

            if (total > authorized + QuantityTolerance)
            {
                throw BusinessException.RuleConflict(
                    $"商品 [{product.ProductName}] 累计入库数量 {total} 超过来源采购订单授权数量 {authorized}" +
                    $"（已审核 {received} + 本次 {thisQuantity}）");
            }
        }
    }


    /// <summary>
    /// 订单行数量 → 基础单位数量：订单行单位等于商品基础单位直接使用；等于装箱单位时按 UnitsPerPackage 折算；
    /// 其他 / 空单位或无效装箱数返回 false（由调用方 fail closed）。
    /// </summary>
    private static bool TryBaseUnitQuantity(BaseProduct product, PurchaseOrderDetail line, out decimal baseQuantity)
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
    /// 入库明细单位是否与商品基础单位兼容：空单位按基础单位处理（入库流水读取时回退商品基础单位）；
    /// 装箱单位已由 <c>StockUnitConversion.NormalizeAsync</c> 折算为基础单位，因此此处只认基础单位。
    /// </summary>
    private static bool IsStockInUnitCompatible(BaseProduct product, StockInDetail detail)
    {
        var unit = (detail.Unit ?? string.Empty).Trim();
        if (unit.Length == 0) return true;

        var baseUnit = (product.Unit ?? string.Empty).Trim();
        return baseUnit.Length > 0 && unit.Equals(baseUnit, StringComparison.OrdinalIgnoreCase);
    }

    // ==================== ERP-375：采购入库来源候选 / 详情（只读、有界） ====================

    /// <summary>来源候选查询默认返回条数</summary>
    public const int DefaultCandidateTake = 50;

    /// <summary>来源候选查询返回条数上限（有界，绝不无界拉取）</summary>
    public const int MaxCandidateTake = 200;

    /// <summary>来源候选扫描上限（有界：先取有限采购订单，再在内存按「订单 + 商品」聚合 / 计算剩余可收数量）</summary>
    public const int MaxCandidateScan = 500;

    /// <summary>关键字长度上限（超长截断，避免无界匹配）</summary>
    public const int MaxKeywordLength = 100;

    /// <summary>来源不存在 / 范围外 / 已删除的拒绝文案（不泄露归属）</summary>
    public const string SourceNotFoundText = "来源采购订单不存在、已删除或不在当前账号数据范围内";

    /// <summary>来源未审核的拒绝文案（必须重新显式选择来源）</summary>
    public const string SourceNotApprovedText = "来源采购订单未审核，不能作为入库依据（必须重新显式选择来源）";

    /// <summary>不可用原因文案（剩余可收数量为 0）</summary>
    public const string ZeroCapacityText = "剩余可收数量为 0：订单授权数量已被已审核入库占用";

    /// <summary>不可用原因文案（订单同商品重复 / 歧义明细，授权数量不唯一）</summary>
    public const string AmbiguousLineText = "来源采购订单同商品存在多条明细，授权数量不唯一（不可用）";

    /// <summary>不可用原因文案（订单明细单位无法折算为基础单位）</summary>
    public const string UnknownUnitText = "来源订单明细单位无法折算为商品基础单位（不可用）";

    /// <summary>不可用原因文案（商品主数据缺失 / 负数量等损坏证据）</summary>
    public const string CorruptSourceText = "来源证据损坏（商品主数据缺失或负数量），不可用";

    /// <summary>候选 / 详情口径文案（接口 / 文档同源）</summary>
    public const string CandidateRuleText =
        "采购入库来源候选只返回当前账号客户数据范围之内「未删除、已审核」的采购订单，并按「来源采购订单 + 商品」聚合：" +
        "订单授权数量按 ERP-342 口径折算为基础单位（订单行单位等于商品基础单位直接使用，等于装箱单位按 UnitsPerPackage 折算），" +
        "扣除「以该订单为来源、未删除、已审核」的入库基础单位数量，得到剩余可收数量；" +
        "关键字只在订单号 / 供应商名 / 商品名称 / 规格内做有界匹配（空串 = 不过滤，绝不用于推断来源）；" +
        "同商品重复 / 歧义明细、单位无法折算、商品主数据缺失、负数量、已收货满额的来源一律标记为不可用（available=false + 原因），绝不猜容量；" +
        "候选 / 详情是只读投影：不落库、不改单据 / 库存 / 流水、不消耗单据号、不新增表 / 列 / 菜单 / 权限或用户授权，也绝不臆造成本。";

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
    /// 有界只读候选查询：返回当前账号客户数据范围内、供应商匹配且关键字命中的「已审核、未删除」采购订单
    /// 可收货商品行（按「来源采购订单 + 商品」聚合，授权数量按 ERP-342 折算基础单位，扣除已审核入库数量）。
    /// <para>先按客户数据范围硬收窄（<paramref name="scopedOrders"/> 已由调用方用 ERP-371 权威范围下推，绝不返回范围外客户），
    /// 再做显式筛选，最后只取最近 <see cref="MaxCandidateScan"/> 张采购订单与至多 <see cref="MaxCandidateTake"/> 条候选。</para>
    /// <para>重复 / 歧义明细、零容量 / 负数量 / 单位无法折算 / 商品缺失的候选保留并显式标记
    /// <c>Available = false</c> + 原因，绝不猜容量；只读投影：不落库、不改单据 / 库存 / 流水、不新增表 / 列 / 菜单 / 权限。</para>
    /// </summary>
    public static async Task<IReadOnlyList<StockInSourceCandidateDto>> QuerySourceCandidatesAsync(
        IErpDbContext db, IQueryable<PurchaseOrder> scopedOrders, long? supplierId, string? keyword, int take,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(scopedOrders);

        var bounded = ClampTake(take);
        var kw = NormalizeKeyword(keyword);

        var source = scopedOrders.AsNoTracking()
            .Where(o => !o.IsDeleted && o.Status == DocumentStatus.Approved);
        if (supplierId is > 0) source = source.Where(o => o.SupplierId == supplierId.Value);

        var orders = await source.OrderByDescending(o => o.Id)
            .Take(MaxCandidateScan)
            .Include(o => o.Details)
            .ToListAsync(ct);

        var lines = await BuildCandidatesAsync(db, orders, kw, ct);
        return lines
            .OrderByDescending(l => l.Available)
            .ThenByDescending(l => l.RemainingBaseQuantity)
            .ThenByDescending(l => l.PurchaseOrderId)
            .ThenBy(l => l.ProductId)
            .Take(bounded)
            .ToList();
    }

    /// <summary>
    /// 来源详情（只读、有界）：返回一张权威来源采购订单的表头 + 全部商品剩余可收行。
    /// <para>客户范围外 / 不存在 / 已删除的来源按「不存在」拒绝（不泄露归属）；未审核 / 已取消 / 已驳回按冲突拒绝；
    /// 任一失败都不写库、不改写任何已保存的来源链接，界面必须重新显式选择来源。</para>
    /// </summary>
    public static async Task<StockInSourceDetailDto> ResolveSourceDetailAsync(
        IErpDbContext db, IQueryable<PurchaseOrder> scopedOrders, long purchaseOrderId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(scopedOrders);
        if (purchaseOrderId <= 0)
            throw BusinessException.InvalidParameter("来源采购订单 Id 必须为正整数");

        var order = await scopedOrders.AsNoTracking().Include(o => o.Details)
            .FirstOrDefaultAsync(o => o.Id == purchaseOrderId, ct);
        if (order is null || order.IsDeleted)
            throw BusinessException.NotFound(SourceNotFoundText);
        if (order.Status != DocumentStatus.Approved)
            throw BusinessException.RuleConflict(SourceNotApprovedText);

        var lines = (await BuildCandidatesAsync(db, new List<PurchaseOrder> { order }, string.Empty, ct))
            .OrderByDescending(l => l.Available)
            .ThenBy(l => l.ProductId)
            .ToList();
        var supplierNames = await LoadSupplierNamesAsync(db, new List<long> { order.SupplierId }, ct);
        var available = lines.Any(l => l.Available);

        return new StockInSourceDetailDto
        {
            PurchaseOrderId = order.Id,
            OrderNo = order.OrderNo,
            OrderDate = order.OrderDate,
            SupplierId = order.SupplierId,
            SupplierName = supplierNames.GetValueOrDefault(order.SupplierId, string.Empty),
            Currency = order.Currency,
            ExchangeRate = order.ExchangeRate,
            Available = available,
            UnavailableReason = available
                ? string.Empty
                : lines.FirstOrDefault()?.UnavailableReason ?? ZeroCapacityText,
            Lines = lines
        };
    }

    /// <summary>
    /// 按「来源采购订单 + 商品」构建候选行：授权数量按 ERP-342 口径折算基础单位，扣除「以本订单为来源、未删除、已审核」
    /// 的入库基础单位数量。重复明细 / 单位未知 / 商品缺失一律显式不可用，绝不猜授权数量。
    /// </summary>
    private static async Task<List<StockInSourceCandidateDto>> BuildCandidatesAsync(
        IErpDbContext db, IReadOnlyList<PurchaseOrder> orders, string keyword, CancellationToken ct)
    {
        var result = new List<StockInSourceCandidateDto>();
        if (orders.Count == 0) return result;

        var orderIds = orders.Select(o => o.Id).ToList();
        var supplierNames = await LoadSupplierNamesAsync(
            db, orders.Select(o => o.SupplierId).Distinct().ToList(), ct);

        var lines = new List<(long OrderId, PurchaseOrderDetail Line)>();
        foreach (var order in orders)
        {
            foreach (var detail in order.Details.Where(d => !d.IsDeleted && d.ProductId > 0))
                lines.Add((order.Id, detail));
        }
        if (lines.Count == 0) return result;

        var productIds = lines.Select(l => l.Line.ProductId).Distinct().ToList();

        // 已审核入库数量：与 ValidateApprovalAsync / EnforceCumulativeAsync 同一口径——以本订单为来源、未删除、已审核，
        // 入库明细已折算基础单位，按商品直接合计（绝不按订单行重复相乘）。
        var receivedRows = await (
                from s in db.StockIns
                join d in db.StockInDetails on s.Id equals d.StockInId
                where s.PurchaseOrderId != null
                      && orderIds.Contains(s.PurchaseOrderId.Value)
                      && !s.IsDeleted
                      && s.Status == DocumentStatus.Approved
                      && !d.IsDeleted
                      && d.ProductId > 0
                select new { OrderId = s.PurchaseOrderId!.Value, d.ProductId, d.Quantity })
            .ToListAsync(ct);

        var products = await db.BaseProducts.AsNoTracking()
            .Where(p => productIds.Contains(p.Id) && !p.IsDeleted)
            .ToDictionaryAsync(p => p.Id, ct);

        var received = receivedRows
            .GroupBy(r => (r.OrderId, r.ProductId))
            .ToDictionary(g => g.Key, g => g.Sum(r => r.Quantity));

        var ordersById = orders.ToDictionary(o => o.Id);

        foreach (var group in lines.GroupBy(l => (l.OrderId, l.Line.ProductId)))
        {
            var (orderId, productId) = group.Key;
            var order = ordersById[orderId];
            var groupLines = group.ToList();
            var first = groupLines[0].Line;
            var receivedQty = received.GetValueOrDefault((orderId, productId), 0m);

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
                // 同商品多条明细：授权数量不唯一（与 ERP-342 ValidateLinkAsync 同口径 fail closed）。
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
                var remaining = authorized - receivedQty;
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

            var remainingQty = available ? Math.Max(authorized - receivedQty, 0m) : 0m;

            var line = new StockInSourceCandidateDto
            {
                PurchaseOrderId = order.Id,
                OrderNo = order.OrderNo,
                OrderDate = order.OrderDate,
                SupplierId = order.SupplierId,
                SupplierName = supplierNames.GetValueOrDefault(order.SupplierId, string.Empty),
                Currency = order.Currency,
                ExchangeRate = order.ExchangeRate,
                ProductId = productId,
                ProductName = product?.ProductName ?? first.ProductName,
                Spec = product?.Spec ?? first.Spec,
                BaseUnit = product?.Unit ?? string.Empty,
                AuthorizedBaseQuantity = authorized,
                ReceivedBaseQuantity = receivedQty,
                RemainingBaseQuantity = remainingQty,
                Available = available,
                UnavailableReason = reason
            };

            if (keyword.Length > 0 && !MatchesKeyword(line, keyword)) continue;
            result.Add(line);
        }

        return result;
    }

    /// <summary>关键字有界匹配（订单号 / 供应商名 / 商品名称 / 规格），大小写不敏感；绝不用于推断来源。</summary>
    private static bool MatchesKeyword(StockInSourceCandidateDto line, string keyword)
    {
        static bool Hit(string? value, string kw)
            => !string.IsNullOrEmpty(value) && value.Contains(kw, StringComparison.OrdinalIgnoreCase);

        return Hit(line.OrderNo, keyword)
            || Hit(line.SupplierName, keyword)
            || Hit(line.ProductName, keyword)
            || Hit(line.Spec, keyword);
    }

    private static async Task<Dictionary<long, string>> LoadSupplierNamesAsync(
        IErpDbContext db, List<long> supplierIds, CancellationToken ct)
        => supplierIds.Count == 0
            ? new Dictionary<long, string>()
            : await db.BaseSuppliers.AsNoTracking()
                .Where(s => supplierIds.Contains(s.Id))
                .ToDictionaryAsync(s => s.Id, s => s.SupplierName, ct);

    private sealed class LinkContext
    {
        public LinkContext(PurchaseOrder order, IReadOnlyList<PurchaseOrderDetail> lines,
            IReadOnlyDictionary<long, BaseProduct> products)
        {
            Order = order;
            Lines = lines;
            Products = products;
        }

        public PurchaseOrder Order { get; }
        public IReadOnlyList<PurchaseOrderDetail> Lines { get; }
        public IReadOnlyDictionary<long, BaseProduct> Products { get; }
    }
}

