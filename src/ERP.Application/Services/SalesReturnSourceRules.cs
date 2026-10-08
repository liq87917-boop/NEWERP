using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ERP.Application.Services;

/// <summary>
/// 销售退货显式来源与可退容量护栏（ERP-357）。
/// <para>只复用既有 <see cref="SalesReturn.SourceStockOutId"/> 显式链接（可空，刻意不建外键）：未链接（null）的
/// 历史退货保持显式「无来源」行为，<b>绝不</b>按 <see cref="SalesReturn.SourceStockOutNo"/> 文本、金额或相似度
/// 猜测链接；显式链接必须构成<b>权威来源</b>（出库单存在、未删除、已审核、客户一致、出库仓库与退货入库仓库一致、
/// 来源单号快照一致），且每条退货明细都能在来源出库单里找到<b>单位可折算为基础单位</b>的商品出库证据。</para>
/// <para>审核时按商品逐行比较「已审核退货（含重复商品行按基础单位合计） + 本次退货 ≤ 已审核出库数量」：
/// 单位不一致、数量为负（证据损坏）一律 fail closed 拒绝，<b>绝不静默钳制</b>；跨单位、跨单据不臆造授权数量。</para>
/// <para>本类只做<b>纯判定与有界只读查询</b>：不落库、不改单据 / 库存 / 流水、不消耗单据号、不新增任何表 / 列 /
/// 菜单 / 权限模型，也不把空身份当作管理员。「判定 + 状态变更 + 库存写入」的原子性，以及来源出库单行锁
/// （<c>UPDLOCK/HOLDLOCK</c>）的实际获取，由调用方（<c>SalesReturnController</c>）在同一可串行化事务内完成。</para>
/// </summary>
public static class SalesReturnSourceRules
{
    /// <summary>
    /// 销售退货模块所需既有菜单编码（与 <c>SchemaUpgrader</c> 第 23.6 节播种的菜单同源）：复用既有「销售退货」
    /// 菜单，<b>不新增任何权限模型</b>，也不把空身份当作管理员。
    /// </summary>
    public const string RequiredMenuCode = "sales-return";

    /// <summary>销售退货模块菜单中文文案（与既有菜单名一致）</summary>
    public const string RequiredMenuText = "销售退货";

    /// <summary>数量比较容差（与基础单位数量保留 4 位小数同口径，吸收装箱换算的舍入尾差）</summary>
    public const decimal QuantityTolerance = 0.0001m;

    /// <summary>来源与可退容量口径文案（接口 / 文档同源）</summary>
    public const string RuleText =
        "销售退货单显式关联来源销售出库单后，仅当链接权威（出库单存在、未删除、已审核、客户一致、出库仓库与退货入库仓库一致、来源单号快照一致）" +
        "且每条退货明细都能在来源出库单找到单位可折算为基础单位的商品出库证据时，创建 / 修改 / 提交才被接受；" +
        "审核时按商品逐行比较「已审核退货（重复商品行按基础单位合计） + 本次退货 ≤ 已审核出库数量」，" +
        "单位不一致、数量为负等损坏证据一律 fail closed 拒绝，绝不静默钳制、绝不臆造授权数量；" +
        "未关联（null）的历史退货保持显式无来源，绝不按来源单号文本猜测链接。";

    /// <summary>模块边界文案（不改估值口径、不改主数据、不删历史证据）</summary>
    public const string BoundaryText =
        "本护栏只新增「来源权威性 + 可退容量」判定与实时授权：不改变库存成本口径（移动加权平均），" +
        "不改写商品 / 仓库 / 客户主数据，不重写退货原始数量与历史库存流水，不删除任何审计证据，" +
        "不新增表 / 列 / 权限 / 菜单；所有库存增减与冲销仍复用既有 InventoryService（ERP-009）记账并写流水。";

    /// <summary>读取 / 选择退货来源所需既有菜单编码（与既有「销售出库」菜单同源）</summary>
    public const string SourceRequiredMenuCode = "stock-out";

    /// <summary>销售出库模块菜单中文文案（与既有菜单名一致）</summary>
    public const string SourceRequiredMenuText = "销售出库";

    /// <summary>来源候选查询默认返回条数</summary>
    public const int DefaultCandidateTake = 50;

    /// <summary>来源候选查询返回条数上限（有界，绝不无界拉取）</summary>
    public const int MaxCandidateTake = 200;

    /// <summary>来源候选扫描上限（有界：先取有限来源出库单，再在内存按商品聚合 / 计算剩余容量）</summary>
    public const int MaxCandidateScan = 500;

    /// <summary>关键字长度上限（超长截断，避免无界匹配）</summary>
    public const int MaxKeywordLength = 100;

    /// <summary>候选 / 详情口径文案（接口 / 文档同源）</summary>
    public const string CandidateRuleText =
        "退货来源候选只返回当前账号客户数据范围之内「未删除、已审核」的销售出库单，并按「来源出库单 + 商品」聚合" +
        "（同商品重复出库行按基础单位合计，绝不重复相乘），给出「来源数量 − 已生效（已审核、未删除）退货数量」的净可退容量；" +
        "关键字只在单号 / 客户名 / 商品编码 / 名称 / 规格内做有界匹配；" +
        "零容量、负数量或单位无法折算为基础单位的来源一律标记为不可用（available=false + 原因），绝不猜容量；" +
        "候选 / 详情是只读投影：不落库、不改单据 / 库存 / 流水、不新增表 / 列 / 菜单 / 权限或用户授权。";

    /// <summary>不可用原因文案（零容量）</summary>
    public const string ZeroCapacityText = "可退容量为 0：来源出库数量已被有效（已审核）退货占用";

    /// <summary>不可用原因文案（来源证据损坏）</summary>
    public const string CorruptSourceText = "来源证据损坏（负数量或商品主数据缺失），不可用";

    /// <summary>不可用原因文案（单位无法折算）</summary>
    public const string UnknownUnitText = "来源单位无法折算为基础单位（单位未知），不可用";

    /// <summary>范围外 / 不存在 / 已删除的来源统一按「不存在」拒绝（不泄露单据归属）</summary>
    public const string SourceNotFoundText = "来源销售出库单不存在";

    /// <summary>来源未审核 / 已取消 / 已驳回，不能作为退货依据</summary>
    public const string SourceNotApprovedText = "来源销售出库单未审核（或已取消 / 已驳回），不能作为退货依据";

    /// <summary>
    /// 身份 / 账号状态 / 模块授权三重校验（fail closed）：缺失或非法身份按未认证拒绝，
    /// 禁用账号、无既有「销售退货」菜单授权按权限不足拒绝，绝不猜测身份、绝不退化为管理员。
    /// </summary>
    public static async Task EnsureMenuAuthorizedAsync(IErpDbContext db, long? userId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (userId is null or <= 0)
            throw new BusinessException("请先登录后再操作销售退货单", ErrorCodes.Unauthorized);

        var user = await db.SysUsers.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == userId.Value && !u.IsDeleted, ct);
        if (user is null)
            throw new BusinessException("登录账号不存在或已删除，禁止操作销售退货单", ErrorCodes.Unauthorized);
        if (user.Status != UserStatus.Enabled)
            throw new BusinessException("登录账号已禁用，禁止操作销售退货单（fail closed）", ErrorCodes.Forbidden);

        var menuCodes = await CustomerReceivableReconciliationService.LoadAuthorizedMenuCodesAsync(db, userId.Value);
        if (!menuCodes.Contains(RequiredMenuCode, StringComparer.OrdinalIgnoreCase))
        {
            throw new BusinessException(
                $"当前账号没有「{RequiredMenuText}」（{RequiredMenuCode}）模块授权：拒绝操作销售退货单" +
                "（fail closed，不执行任何写入）",
                ErrorCodes.Forbidden);
        }
    }

    /// <summary>
    /// 关系型后端（SQL Server）判定：内存库等非关系型提供程序没有行锁语义，锁定与事务等价无操作。
    /// 使用 <c>DatabaseFacade.ProviderName</c>（EF Core 基础 API），不依赖关系型扩展。
    /// </summary>
    public static bool IsRelationalProvider(IErpDbContext db)
    {
        ArgumentNullException.ThrowIfNull(db);
        return string.Equals(db.Database.ProviderName, "Microsoft.EntityFrameworkCore.SqlServer",
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 解析并校验显式来源链接（创建 / 修改 / 提交 / 审核共用同一份权威判定）：
    /// <list type="bullet">
    /// <item><b>未链接（null）</b>：返回 <c>null</c>，保持历史行为，绝不按来源单号文本猜测链接；</item>
    /// <item><b>显式链接（非空）</b>：出库单必须存在、未删除、已审核，客户一致、出库仓库与退货入库仓库一致、
    /// 来源单号快照一致，每条退货明细的商品 / 单位都能在来源出库单找到可折算为基础单位的出库证据，
    /// 否则抛 <see cref="BusinessException"/>，<b>不做任何写入、不消耗单据号</b>。</item>
    /// </list>
    /// </summary>
    public static async Task<SalesReturnSourceLink?> ResolveLinkAsync(IErpDbContext db, SalesReturn entity,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(entity);

        // 未关联：显式保留「无来源」语义，绝不按文本单号推断链接。
        if (entity.SourceStockOutId is null) return null;
        if (entity.SourceStockOutId is not > 0)
            throw BusinessException.InvalidParameter("来源销售出库单 Id 必须为正整数");

        var shipment = await db.StockOuts.AsNoTracking().Include(o => o.Details)
            .FirstOrDefaultAsync(o => o.Id == entity.SourceStockOutId!.Value, ct)
            ?? throw BusinessException.RuleConflict("来源销售出库单不存在，不能作为退货依据");

        if (shipment.IsDeleted)
            throw BusinessException.RuleConflict("来源销售出库单已删除，不能作为退货依据");
        if (shipment.Status != DocumentStatus.Approved)
            throw BusinessException.RuleConflict("来源销售出库单未审核（或已取消 / 已驳回），不能作为退货依据");
        if (entity.CustomerId != shipment.CustomerId)
            throw BusinessException.RuleConflict("退货单客户与来源销售出库单客户不一致");
        if (entity.WarehouseId != shipment.WarehouseId)
            throw BusinessException.RuleConflict("退货入库仓库与来源销售出库单出库仓库不一致");

        // 来源快照一致性：显式填写的来源单号必须与权威来源一致，冲突即拒绝（绝不静默改写）。
        var snapshot = (entity.SourceStockOutNo ?? string.Empty).Trim();
        if (snapshot.Length > 0 && !snapshot.Equals(shipment.StockOutNo, StringComparison.OrdinalIgnoreCase))
            throw BusinessException.RuleConflict(
                $"退货单来源出库单号 [{snapshot}] 与来源销售出库单 [{shipment.StockOutNo}] 不一致");

        var lines = entity.Details.Where(d => !d.IsDeleted).ToList();
        if (lines.Count == 0)
            throw BusinessException.RuleConflict("销售退货单无明细，不能关联来源销售出库单");

        var returnedProductIds = lines.Where(d => d.ProductId is > 0).Select(d => d.ProductId!.Value)
            .Distinct()
            .ToList();
        if (returnedProductIds.Count == 0)
            throw BusinessException.RuleConflict("退货明细必须指定商品");

        var products = await db.BaseProducts.AsNoTracking()
            .Where(p => returnedProductIds.Contains(p.Id) && !p.IsDeleted)
            .ToDictionaryAsync(p => p.Id, ct);
        if (products.Count != returnedProductIds.Count)
            throw BusinessException.RuleConflict("退货明细存在已删除 / 不存在的商品");

        // 来源出库明细 → 基础单位（重复商品行合计，仅统计被退货引用的商品）；
        // 负数量 / 单位不兼容视为证据损坏，fail closed。
        var shipped = new Dictionary<long, decimal>();
        foreach (var s in shipment.Details.Where(s => !s.IsDeleted && returnedProductIds.Contains(s.ProductId)))
        {
            var product = products[s.ProductId];
            if (s.Quantity < 0)
                throw BusinessException.RuleConflict(
                    $"来源销售出库单商品 [{product.ProductName}] 存在负数量明细，退货证据已损坏（fail closed）");
            if (!TryBaseUnitQuantity(product, s.Unit, s.Quantity, out var baseQuantity))
                throw BusinessException.RuleConflict(
                    $"来源销售出库单商品 [{product.ProductName}] 明细单位 [{s.Unit}] 无法折算为基础单位（fail closed）");
            shipped[s.ProductId] = shipped.GetValueOrDefault(s.ProductId) + baseQuantity;
        }

        // 退货明细 → 基础单位（重复商品行合计）；单位不兼容 / 负数量 / 商品无出库证据，一律 fail closed。
        foreach (var d in lines)
        {
            if (d.ProductId is not > 0)
                throw BusinessException.RuleConflict("退货明细必须指定商品");
            var product = products[d.ProductId!.Value];
            if (d.Quantity < 0)
                throw BusinessException.InvalidParameter($"商品 [{d.ProductName}] 的退货数量不能为负数");
            if (!TryBaseUnitQuantity(product, d.Unit, d.Quantity, out _))
                throw BusinessException.RuleConflict(
                    $"商品 [{d.ProductName}] 的退货单位 [{d.Unit}] 无法折算为基础单位（fail closed）");
            if (shipped.GetValueOrDefault(d.ProductId.Value) <= 0)
                throw BusinessException.RuleConflict(
                    $"来源销售出库单没有商品 [{product.ProductName}] 的出库证据，不能退货");
        }

        return new SalesReturnSourceLink(shipment, shipped);
    }


    /// <summary>
    /// 创建 / 修改 / 提交时的链接校验：直接复用 <see cref="ResolveLinkAsync"/>，未链接（null）保持历史行为。
    /// </summary>
    public static Task<SalesReturnSourceLink?> ValidateLinkAsync(IErpDbContext db, SalesReturn entity,
        CancellationToken ct = default)
        => ResolveLinkAsync(db, entity, ct);

    /// <summary>
    /// 审核时的来源校验 + 累计可退容量校验：先做权威链接判定，再按商品逐行比较
    /// 「已审核退货（同一来源出库单、未删除、已审核、非本单，重复商品行按基础单位合计） + 本次退货 ≤ 已审核出库数量」。
    /// <para>单位不一致、负数量（损坏证据）一律 fail closed 拒绝，绝不静默钳制；未链接（null）不做容量校验。</para>
    /// <para>调用方必须先在可串行化事务内取得来源出库单行锁（<c>UPDLOCK/HOLDLOCK</c>）并折算本单明细单位，
    /// 使并发退货在同一来源出库单上串行化。</para>
    /// </summary>
    public static async Task<SalesReturnSourceLink?> ValidateApprovalAsync(IErpDbContext db, SalesReturn entity,
        CancellationToken ct = default)
    {
        var link = await ResolveLinkAsync(db, entity, ct);
        if (link is null) return null;

        var shipmentId = link.Shipment.Id;

        var approvedRows = await (
                from r in db.SalesReturns
                join d in db.SalesReturnDetails on r.Id equals d.SalesReturnId
                where r.SourceStockOutId == shipmentId
                      && !r.IsDeleted
                      && r.Status == DocumentStatus.Approved
                      && r.Id != entity.Id
                      && !d.IsDeleted
                      && d.ProductId > 0
                select new { ProductId = d.ProductId!.Value, d.Unit, d.Quantity })
            .ToListAsync(ct);

        var productIds = entity.Details.Where(d => !d.IsDeleted && d.ProductId is > 0)
            .Select(d => d.ProductId!.Value)
            .Concat(approvedRows.Select(r => r.ProductId))
            .Distinct()
            .ToList();
        var products = productIds.Count == 0
            ? new Dictionary<long, BaseProduct>()
            : await db.BaseProducts.AsNoTracking()
                .Where(p => productIds.Contains(p.Id) && !p.IsDeleted)
                .ToDictionaryAsync(p => p.Id, ct);

        var approved = new Dictionary<long, decimal>();
        foreach (var row in approvedRows)
        {
            if (!products.TryGetValue(row.ProductId, out var product))
                throw BusinessException.RuleConflict(
                    "已审核退货单存在已删除 / 不存在的商品，退货证据不一致（fail closed）");
            if (row.Quantity < 0)
                throw BusinessException.RuleConflict(
                    $"商品 [{product.ProductName}] 的已审核退货存在负数量明细，退货证据已损坏（fail closed）");
            if (!TryBaseUnitQuantity(product, row.Unit, row.Quantity, out var baseQuantity))
                throw BusinessException.RuleConflict(
                    $"商品 [{product.ProductName}] 的已审核退货单位 [{row.Unit}] 无法折算为基础单位（fail closed）");
            approved[row.ProductId] = approved.GetValueOrDefault(row.ProductId) + baseQuantity;
        }

        foreach (var group in entity.Details.Where(d => !d.IsDeleted && d.ProductId is > 0)
                     .GroupBy(d => d.ProductId!.Value))
        {
            var productId = group.Key;
            if (!products.TryGetValue(productId, out var product))
                throw BusinessException.RuleConflict($"商品 [{group.First().ProductName}] 不存在或已删除，不能审核");

            decimal thisQuantity = 0m;
            foreach (var d in group)
            {
                if (d.Quantity < 0)
                    throw BusinessException.InvalidParameter($"商品 [{d.ProductName}] 的退货数量不能为负数");
                if (!TryBaseUnitQuantity(product, d.Unit, d.Quantity, out var baseQuantity))
                    throw BusinessException.RuleConflict(
                        $"商品 [{d.ProductName}] 的退货单位 [{d.Unit}] 无法折算为基础单位（fail closed）");
                thisQuantity += baseQuantity;
            }

            var shipped = link.ShippedBaseQuantity.GetValueOrDefault(productId);
            var already = approved.GetValueOrDefault(productId);
            var total = already + thisQuantity;
            if (total > shipped + QuantityTolerance)
            {
                throw BusinessException.RuleConflict(
                    $"商品 [{product.ProductName}] 累计退货数量 {total} 超过来源销售出库单可退数量 {shipped}" +
                    $"（已审核退货 {already} + 本次 {thisQuantity}）");
            }
        }

        return link;
    }

    /// <summary>
    /// 数量 → 基础单位数量：单位为空或等于商品基础单位直接使用；等于装箱单位时按 <c>UnitsPerPackage</c> 折算；
    /// 其他单位、缺失基础单位或无效装箱数返回 <c>false</c>（由调用方 fail closed）。
    /// </summary>
    public static bool TryBaseUnitQuantity(BaseProduct product, string? unit, decimal quantity,
        out decimal baseUnitQuantity)
    {
        ArgumentNullException.ThrowIfNull(product);
        baseUnitQuantity = 0m;

        var baseUnit = (product.Unit ?? string.Empty).Trim();
        if (baseUnit.Length == 0) return false;

        var lineUnit = (unit ?? string.Empty).Trim();
        if (lineUnit.Length == 0 || lineUnit.Equals(baseUnit, StringComparison.OrdinalIgnoreCase))
        {
            baseUnitQuantity = quantity;
            return true;
        }

        var packageUnit = (product.PackageUnit ?? string.Empty).Trim();
        if (packageUnit.Length == 0 || !lineUnit.Equals(packageUnit, StringComparison.OrdinalIgnoreCase))
            return false;
        if (product.UnitsPerPackage <= 0) return false;

        baseUnitQuantity = Math.Round(quantity * product.UnitsPerPackage, 4);
        return true;
    }

    // ==================== ERP-374：退货来源候选 / 详情（只读、有界） ====================

    /// <summary>
    /// 读取 / 选择退货来源所需既有「销售出库」菜单授权（fail closed）：缺失或非法身份按未认证拒绝，
    /// 禁用账号、无既有「销售出库」菜单授权按权限不足拒绝；绝不新增用户授权，也绝不把空身份 / 管理员当兜底。
    /// </summary>
    public static async Task EnsureSourceMenuAuthorizedAsync(IErpDbContext db, long? userId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (userId is null or <= 0)
            throw new BusinessException("请先登录后再选择销售退货来源出库单", ErrorCodes.Unauthorized);

        var user = await db.SysUsers.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == userId.Value && !u.IsDeleted, ct);
        if (user is null)
            throw new BusinessException("登录账号不存在或已删除，禁止选择销售退货来源出库单", ErrorCodes.Unauthorized);
        if (user.Status != UserStatus.Enabled)
            throw new BusinessException("登录账号已禁用，禁止选择销售退货来源出库单（fail closed）", ErrorCodes.Forbidden);

        var menuCodes = await CustomerReceivableReconciliationService.LoadAuthorizedMenuCodesAsync(db, userId.Value);
        if (!menuCodes.Contains(SourceRequiredMenuCode, StringComparer.OrdinalIgnoreCase))
        {
            throw new BusinessException(
                $"当前账号没有「{SourceRequiredMenuText}」（{SourceRequiredMenuCode}）模块授权：拒绝读取退货来源候选" +
                "（fail closed，不返回任何证据）",
                ErrorCodes.Forbidden);
        }
    }

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
    /// 有界只读候选查询：返回当前账号客户数据范围内、客户 / 仓库匹配且关键字命中的「已审核、未删除」
    /// 销售出库单可退货商品行（按「来源出库单 + 商品」聚合，重复商品行按基础单位合计，绝不重复相乘）。
    /// <para>先按客户数据范围硬收窄（绝不返回范围外客户），再做显式筛选，最后只取最近
    /// <see cref="MaxCandidateScan"/> 张来源出库单与至多 <see cref="MaxCandidateTake"/> 条候选。</para>
    /// <para>零容量 / 负数量 / 单位无法折算的候选保留并显式标记 <c>Available = false</c> + 原因，绝不猜容量；
    /// 只读投影：不落库、不改单据 / 库存 / 流水、不新增表 / 列 / 菜单 / 权限。</para>
    /// </summary>
    public static async Task<IReadOnlyList<SalesReturnSourceCandidateDto>> QuerySourceCandidatesAsync(
        IErpDbContext db, SalespersonDataScope scope, long? customerId, long? warehouseId,
        string? keyword, int take, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(scope);

        var bounded = ClampTake(take);
        var kw = NormalizeKeyword(keyword);

        var source = db.StockOuts.AsNoTracking()
            .Where(o => !o.IsDeleted && o.Status == DocumentStatus.Approved);
        source = SalespersonDataScopeService.FilterByCustomer(source, scope, o => o.CustomerId);
        if (customerId is > 0) source = source.Where(o => o.CustomerId == customerId.Value);
        if (warehouseId is > 0) source = source.Where(o => o.WarehouseId == warehouseId.Value);

        var shipments = await source.OrderByDescending(o => o.Id)
            .Take(MaxCandidateScan)
            .Include(o => o.Details)
            .ToListAsync(ct);

        var lines = await BuildCandidatesAsync(db, shipments, kw, ct);
        return lines
            .OrderByDescending(l => l.Available)
            .ThenByDescending(l => l.RemainingBaseQuantity)
            .ThenByDescending(l => l.SourceStockOutId)
            .ThenBy(l => l.ProductId)
            .Take(bounded)
            .ToList();
    }

    /// <summary>
    /// 来源详情（只读、有界）：返回一张权威来源销售出库单的表头 + 全部商品可退容量行。
    /// <para>客户范围外 / 不存在 / 已删除的来源按「不存在」拒绝（不泄露归属）；未审核 / 已取消 / 已驳回按冲突拒绝；
    /// 任一失败都不写库、不改写任何已保存的来源链接，界面必须重新显式选择来源。</para>
    /// </summary>
    public static async Task<SalesReturnSourceDetailDto> ResolveSourceDetailAsync(
        IErpDbContext db, SalespersonDataScope scope, long sourceStockOutId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(scope);
        if (sourceStockOutId <= 0)
            throw BusinessException.InvalidParameter("来源销售出库单 Id 必须为正整数");

        var shipment = await db.StockOuts.AsNoTracking().Include(o => o.Details)
            .FirstOrDefaultAsync(o => o.Id == sourceStockOutId, ct)
            ?? throw BusinessException.NotFound(SourceNotFoundText);

        // 客户数据范围是硬边界：范围外的来源按「不存在」拒绝，不泄露单据归属。
        if (!scope.AllowsCustomer(shipment.CustomerId))
            throw BusinessException.NotFound(SourceNotFoundText);
        if (shipment.IsDeleted)
            throw BusinessException.NotFound(SourceNotFoundText);
        if (shipment.Status != DocumentStatus.Approved)
            throw BusinessException.RuleConflict(SourceNotApprovedText);

        var lines = (await BuildCandidatesAsync(db, new List<StockOut> { shipment }, string.Empty, ct))
            .OrderByDescending(l => l.Available)
            .ThenBy(l => l.ProductId)
            .ToList();
        var customerNames = await LoadCustomerNamesAsync(db, new List<long> { shipment.CustomerId }, ct);
        var warehouseNames = await LoadWarehouseNamesAsync(db, new List<long> { shipment.WarehouseId }, ct);
        var available = lines.Any(l => l.Available);

        return new SalesReturnSourceDetailDto
        {
            SourceStockOutId = shipment.Id,
            SourceStockOutNo = shipment.StockOutNo,
            SourceStockOutDate = shipment.StockOutDate,
            CustomerId = shipment.CustomerId,
            CustomerName = customerNames.GetValueOrDefault(shipment.CustomerId, string.Empty),
            WarehouseId = shipment.WarehouseId,
            WarehouseName = warehouseNames.GetValueOrDefault(shipment.WarehouseId, string.Empty),
            Available = available,
            UnavailableReason = available
                ? string.Empty
                : lines.FirstOrDefault()?.UnavailableReason ?? ZeroCapacityText,
            Lines = lines
        };
    }

    /// <summary>按「来源出库单 + 商品」构建候选行（聚合来源出库数量与已生效退货数量，绝不重复相乘）。</summary>
    private static async Task<List<SalesReturnSourceCandidateDto>> BuildCandidatesAsync(
        IErpDbContext db, IReadOnlyList<StockOut> shipments, string keyword, CancellationToken ct)
    {
        var result = new List<SalesReturnSourceCandidateDto>();
        if (shipments.Count == 0) return result;

        var sourceIds = shipments.Select(s => s.Id).ToList();
        var customerNames = await LoadCustomerNamesAsync(
            db, shipments.Select(s => s.CustomerId).Distinct().ToList(), ct);
        var warehouseNames = await LoadWarehouseNamesAsync(
            db, shipments.Select(s => s.WarehouseId).Distinct().ToList(), ct);

        var sourceRows = new List<(long SourceId, long ProductId, string? Unit, decimal Quantity,
            string ProductName, string Spec)>();
        foreach (var shipment in shipments)
        {
            foreach (var detail in shipment.Details.Where(d => !d.IsDeleted && d.ProductId > 0))
            {
                sourceRows.Add((shipment.Id, detail.ProductId, detail.Unit, detail.Quantity,
                    detail.ProductName, detail.Spec));
            }
        }
        if (sourceRows.Count == 0) return result;

        var productIds = sourceRows.Select(r => r.ProductId).Distinct().ToList();

        // 已生效（已审核、未删除）退货按「来源出库单 + 商品」保守聚合：
        // 退货缺少来源明细 Id，绝不猜测退货归属到哪一条出库明细。
        var approvedRows = await (from r in db.SalesReturns
                                  join d in db.SalesReturnDetails on r.Id equals d.SalesReturnId
                                  where r.SourceStockOutId != null
                                        && sourceIds.Contains(r.SourceStockOutId.Value)
                                        && !r.IsDeleted
                                        && r.Status == DocumentStatus.Approved
                                        && !d.IsDeleted
                                        && d.ProductId > 0
                                  select new
                                  {
                                      SourceStockOutId = r.SourceStockOutId!.Value,
                                      ProductId = d.ProductId!.Value,
                                      d.Unit,
                                      d.Quantity
                                  })
            .ToListAsync(ct);
        productIds.AddRange(approvedRows.Select(r => r.ProductId));
        productIds = productIds.Distinct().ToList();

        var products = await db.BaseProducts.AsNoTracking()
            .Where(p => productIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, ct);

        var groups = new Dictionary<(long SourceId, long ProductId), CandidateGroup>();
        foreach (var row in sourceRows)
        {
            var key = (row.SourceId, row.ProductId);
            if (!groups.TryGetValue(key, out var group))
                groups[key] = group = new CandidateGroup { ProductName = row.ProductName, Spec = row.Spec };

            if (group.Product is null && products.TryGetValue(row.ProductId, out var product) && !product.IsDeleted)
                group.Product = product;

            if (row.Quantity < 0) { group.Corrupt = true; continue; }
            if (group.Product is null) { group.UnitUnknown = true; continue; }
            if (!TryBaseUnitQuantity(group.Product, row.Unit, row.Quantity, out var baseQuantity))
            {
                group.UnitUnknown = true;
                continue;
            }
            group.SourceBaseQuantity += baseQuantity;
        }

        foreach (var row in approvedRows)
        {
            if (!groups.TryGetValue((row.SourceStockOutId, row.ProductId), out var group)) continue;
            if (row.Quantity < 0) { group.Corrupt = true; continue; }
            if (group.Product is null) { group.UnitUnknown = true; continue; }
            if (!TryBaseUnitQuantity(group.Product, row.Unit, row.Quantity, out var baseQuantity))
            {
                group.UnitUnknown = true;
                continue;
            }
            group.EffectiveReturnedBaseQuantity += baseQuantity;
        }

        var shipmentsById = shipments.ToDictionary(s => s.Id);
        foreach (var kv in groups)
        {
            var (sourceId, productId) = kv.Key;
            var group = kv.Value;
            var shipment = shipmentsById[sourceId];

            var remaining = group.SourceBaseQuantity - group.EffectiveReturnedBaseQuantity;
            if (remaining < 0) remaining = 0m;

            bool available;
            string reason;
            if (group.Corrupt) { available = false; reason = CorruptSourceText; }
            else if (group.UnitUnknown) { available = false; reason = UnknownUnitText; }
            else if (group.SourceBaseQuantity <= 0 || remaining <= QuantityTolerance)
            {
                available = false;
                reason = ZeroCapacityText;
            }
            else { available = true; reason = string.Empty; }

            var line = new SalesReturnSourceCandidateDto
            {
                SourceStockOutId = shipment.Id,
                SourceStockOutNo = shipment.StockOutNo,
                SourceStockOutDate = shipment.StockOutDate,
                CustomerId = shipment.CustomerId,
                CustomerName = customerNames.GetValueOrDefault(shipment.CustomerId, string.Empty),
                WarehouseId = shipment.WarehouseId,
                WarehouseName = warehouseNames.GetValueOrDefault(shipment.WarehouseId, string.Empty),
                ProductId = productId,
                ProductName = group.Product?.ProductName ?? group.ProductName,
                Spec = group.Product?.Spec ?? group.Spec,
                BaseUnit = group.Product?.Unit ?? string.Empty,
                SourceBaseQuantity = group.SourceBaseQuantity,
                EffectiveReturnedBaseQuantity = group.EffectiveReturnedBaseQuantity,
                RemainingBaseQuantity = available ? remaining : 0m,
                Available = available,
                UnavailableReason = reason
            };

            if (keyword.Length > 0 && !MatchesKeyword(line, keyword)) continue;
            result.Add(line);
        }

        return result;
    }

    /// <summary>关键字有界匹配（来源单号 / 客户名 / 商品名称 / 规格），大小写不敏感；绝不用于推断来源。</summary>
    private static bool MatchesKeyword(SalesReturnSourceCandidateDto line, string keyword)
    {
        static bool Hit(string? value, string kw)
            => !string.IsNullOrEmpty(value) && value.Contains(kw, StringComparison.OrdinalIgnoreCase);

        return Hit(line.SourceStockOutNo, keyword)
            || Hit(line.CustomerName, keyword)
            || Hit(line.ProductName, keyword)
            || Hit(line.Spec, keyword);
    }

    private static async Task<Dictionary<long, string>> LoadCustomerNamesAsync(
        IErpDbContext db, List<long> customerIds, CancellationToken ct)
        => customerIds.Count == 0
            ? new Dictionary<long, string>()
            : await db.BaseCustomers.AsNoTracking()
                .Where(c => customerIds.Contains(c.Id) && !c.IsDeleted)
                .ToDictionaryAsync(c => c.Id, c => c.CustomerName, ct);

    private static async Task<Dictionary<long, string>> LoadWarehouseNamesAsync(
        IErpDbContext db, List<long> warehouseIds, CancellationToken ct)
        => warehouseIds.Count == 0
            ? new Dictionary<long, string>()
            : await db.BaseWarehouses.AsNoTracking()
                .Where(w => warehouseIds.Contains(w.Id) && !w.IsDeleted)
                .ToDictionaryAsync(w => w.Id, w => w.WarehouseName, ct);

    /// <summary>「来源出库单 + 商品」聚合中间态（来源数量 / 已生效退货数量 / 证据可用性）。</summary>
    private sealed class CandidateGroup
    {
        public BaseProduct? Product { get; set; }
        public string ProductName { get; set; } = string.Empty;
        public string Spec { get; set; } = string.Empty;
        public bool Corrupt { get; set; }
        public bool UnitUnknown { get; set; }
        public decimal SourceBaseQuantity { get; set; }
        public decimal EffectiveReturnedBaseQuantity { get; set; }
    }

}

/// <summary>
/// 已解析的权威来源上下文（只读事实）：来源销售出库单 + 按商品合计的已审核出库基础单位数量。
/// </summary>
public sealed class SalesReturnSourceLink
{
    public SalesReturnSourceLink(StockOut shipment, IReadOnlyDictionary<long, decimal> shippedBaseQuantity)
    {
        Shipment = shipment;
        ShippedBaseQuantity = shippedBaseQuantity;
    }

    /// <summary>权威来源销售出库单（含明细）</summary>
    public StockOut Shipment { get; }

    /// <summary>来源出库单按商品合计的已审核出库数量（商品基础单位）</summary>
    public IReadOnlyDictionary<long, decimal> ShippedBaseQuantity { get; }
}

