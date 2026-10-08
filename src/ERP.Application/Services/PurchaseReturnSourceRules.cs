using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ERP.Application.Services;

/// <summary>
/// 采购退货显式来源与可退容量护栏（ERP-358）。
/// <para>只复用既有 <see cref="PurchaseReturn.SourceStockInId"/> 显式链接（可空，刻意不建外键）：未链接（null）的
/// 历史退货保持显式「无来源」行为，<b>绝不</b>按 <see cref="PurchaseReturn.SourceStockInNo"/> 文本、金额或相似度
/// 猜测链接；显式链接必须构成<b>权威来源</b>（入库单存在、未删除、已审核、供应商一致、入库仓库与退货出库仓库一致、
/// 来源单号快照一致），且每条退货明细都能在来源入库单里找到<b>单位可折算为基础单位</b>的商品入库证据。</para>
/// <para>审核时按商品逐行比较「已审核退货（含重复商品行按基础单位合计） + 本次退货 ≤ 已审核入库数量」：
/// 单位不一致、数量为负（证据损坏）一律 fail closed 拒绝，<b>绝不静默钳制</b>；跨单位、跨单据不臆造授权数量。</para>
/// <para>本类只做<b>纯判定与有界只读查询</b>：不落库、不改单据 / 库存 / 流水、不消耗单据号、不新增任何表 / 列 /
/// 菜单 / 权限模型，也不把空身份当作管理员。「判定 + 状态变更 + 库存写入」的原子性，以及来源入库单行锁
/// （<c>UPDLOCK/HOLDLOCK</c>）的实际获取，由调用方（<c>PurchaseReturnController</c>）在同一可串行化事务内完成。</para>
/// </summary>
public static class PurchaseReturnSourceRules
{
    /// <summary>
    /// 采购退货模块所需既有菜单编码（与 <c>SchemaUpgrader</c> 播种的菜单同源）：复用既有「采购退货」
    /// 菜单，<b>不新增任何权限模型</b>，也不把空身份当作管理员。
    /// </summary>
    public const string RequiredMenuCode = "purchase-return";

    /// <summary>采购退货模块菜单中文文案（与既有菜单名一致）</summary>
    public const string RequiredMenuText = "采购退货";

    /// <summary>数量比较容差（与基础单位数量保留 4 位小数同口径，吸收装箱换算的舍入尾差）</summary>
    public const decimal QuantityTolerance = 0.0001m;

    /// <summary>来源与可退容量口径文案（接口 / 文档同源）</summary>
    public const string RuleText =
        "采购退货单显式关联来源采购入库单后，仅当链接权威（入库单存在、未删除、已审核、供应商一致、入库仓库与退货出库仓库一致、来源单号快照一致）" +
        "且每条退货明细都能在来源入库单找到单位可折算为基础单位的商品入库证据时，创建 / 修改 / 提交才被接受；" +
        "审核时按商品逐行比较「已审核退货（重复商品行按基础单位合计） + 本次退货 ≤ 已审核入库数量」，" +
        "单位不一致、数量为负等损坏证据一律 fail closed 拒绝，绝不静默钳制、绝不臆造授权数量；" +
        "未关联（null）的历史退货保持显式无来源，绝不按来源单号文本猜测链接。";

    /// <summary>模块边界文案（不改估值口径、不改主数据、不删历史证据）</summary>
    public const string BoundaryText =
        "本护栏只新增「来源权威性 + 可退容量」判定与实时授权：不改变库存成本口径（移动加权平均），" +
        "不改写商品 / 仓库 / 供应商主数据，不重写退货原始数量与历史库存流水，不删除任何审计证据，" +
        "不新增表 / 列 / 权限 / 菜单；所有库存增减与冲销仍复用既有 InventoryService（ERP-009）记账并写流水。";

    /// <summary>
    /// 身份 / 账号状态 / 模块授权三重校验（fail closed）：缺失或非法身份按未认证拒绝，
    /// 禁用账号、无既有「采购退货」菜单授权按权限不足拒绝，绝不猜测身份、绝不退化为管理员。
    /// </summary>
    public static async Task EnsureMenuAuthorizedAsync(IErpDbContext db, long? userId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (userId is null or <= 0)
            throw new BusinessException("请先登录后再操作采购退货单", ErrorCodes.Unauthorized);

        var user = await db.SysUsers.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == userId.Value && !u.IsDeleted, ct);
        if (user is null)
            throw new BusinessException("登录账号不存在或已删除，禁止操作采购退货单", ErrorCodes.Unauthorized);
        if (user.Status != UserStatus.Enabled)
            throw new BusinessException("登录账号已禁用，禁止操作采购退货单（fail closed）", ErrorCodes.Forbidden);

        var menuCodes = await CustomerReceivableReconciliationService.LoadAuthorizedMenuCodesAsync(db, userId.Value);
        if (!menuCodes.Contains(RequiredMenuCode, StringComparer.OrdinalIgnoreCase))
        {
            throw new BusinessException(
                $"当前账号没有「{RequiredMenuText}」（{RequiredMenuCode}）模块授权：拒绝操作采购退货单" +
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
    /// <item><b>显式链接（非空）</b>：入库单必须存在、未删除、已审核，供应商一致、入库仓库与退货出库仓库一致、
    /// 来源单号快照一致，每条退货明细的商品 / 单位都能在来源入库单找到可折算为基础单位的入库证据，
    /// 否则抛 <see cref="BusinessException"/>，<b>不做任何写入、不消耗单据号</b>。</item>
    /// </list>
    /// </summary>
    public static async Task<PurchaseReturnSourceLink?> ResolveLinkAsync(IErpDbContext db, PurchaseReturn entity,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(entity);

        // 未关联：显式保留「无来源」语义，绝不按文本单号推断链接。
        if (entity.SourceStockInId is null) return null;
        if (entity.SourceStockInId is not > 0)
            throw BusinessException.InvalidParameter("来源采购入库单 Id 必须为正整数");

        var receipt = await db.StockIns.AsNoTracking().Include(o => o.Details)
            .FirstOrDefaultAsync(o => o.Id == entity.SourceStockInId!.Value, ct)
            ?? throw BusinessException.RuleConflict("来源采购入库单不存在，不能作为退货依据");

        if (receipt.IsDeleted)
            throw BusinessException.RuleConflict("来源采购入库单已删除，不能作为退货依据");
        if (receipt.Status != DocumentStatus.Approved)
            throw BusinessException.RuleConflict("来源采购入库单未审核（或已取消 / 已驳回），不能作为退货依据");
        if (entity.SupplierId != receipt.SupplierId)
            throw BusinessException.RuleConflict("退货单供应商与来源采购入库单供应商不一致");
        if (entity.WarehouseId != receipt.WarehouseId)
            throw BusinessException.RuleConflict("退货出库仓库与来源采购入库单入库仓库不一致");

        // 来源快照一致性：显式填写的来源单号必须与权威来源一致，冲突即拒绝（绝不静默改写）。
        var snapshot = (entity.SourceStockInNo ?? string.Empty).Trim();
        if (snapshot.Length > 0 && !snapshot.Equals(receipt.StockInNo, StringComparison.OrdinalIgnoreCase))
            throw BusinessException.RuleConflict(
                $"退货单来源入库单号 [{snapshot}] 与来源采购入库单 [{receipt.StockInNo}] 不一致");

        var lines = entity.Details.Where(d => !d.IsDeleted).ToList();
        if (lines.Count == 0)
            throw BusinessException.RuleConflict("采购退货单无明细，不能关联来源采购入库单");

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

        // 来源入库明细 → 基础单位（重复商品行合计，仅统计被退货引用的商品）；
        // 负数量 / 单位不兼容视为证据损坏，fail closed。
        var received = new Dictionary<long, decimal>();
        foreach (var s in receipt.Details.Where(s => !s.IsDeleted && returnedProductIds.Contains(s.ProductId)))
        {
            var product = products[s.ProductId];
            if (s.Quantity < 0)
                throw BusinessException.RuleConflict(
                    $"商品 [{product.ProductName}] 的来源采购入库单存在负数量明细，入库证据已损坏（fail closed）");
            if (!TryBaseUnitQuantity(product, s.Unit, s.Quantity, out var baseQuantity))
                throw BusinessException.RuleConflict(
                    $"商品 [{product.ProductName}] 的来源入库单位 [{s.Unit}] 无法折算为基础单位（fail closed）");
            received[s.ProductId] = received.GetValueOrDefault(s.ProductId) + baseQuantity;
        }

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
            if (received.GetValueOrDefault(d.ProductId.Value) <= 0)
                throw BusinessException.RuleConflict(
                    $"来源采购入库单没有商品 [{product.ProductName}] 的入库证据，不能退货");
        }

        return new PurchaseReturnSourceLink(receipt, received);
    }

    /// <summary>
    /// 创建 / 修改 / 提交时的链接校验：直接复用 <see cref="ResolveLinkAsync"/>，未链接（null）保持历史行为。
    /// </summary>
    public static Task<PurchaseReturnSourceLink?> ValidateLinkAsync(IErpDbContext db, PurchaseReturn entity,
        CancellationToken ct = default)
        => ResolveLinkAsync(db, entity, ct);

    /// <summary>
    /// 审核时的来源校验 + 累计可退容量校验：先做权威链接判定，再按商品逐行比较
    /// 「已审核退货（同一来源入库单、未删除、已审核、非本单，重复商品行按基础单位合计） + 本次退货 ≤ 已审核入库数量」。
    /// <para>单位不一致、负数量（损坏证据）一律 fail closed 拒绝，绝不静默钳制；未链接（null）不做容量校验。</para>
    /// <para>调用方必须先在可串行化事务内取得来源入库单行锁（<c>UPDLOCK/HOLDLOCK</c>）并折算本单明细单位，
    /// 使并发退货在同一来源入库单上串行化。</para>
    /// </summary>
    public static async Task<PurchaseReturnSourceLink?> ValidateApprovalAsync(IErpDbContext db, PurchaseReturn entity,
        CancellationToken ct = default)
    {
        var link = await ResolveLinkAsync(db, entity, ct);
        if (link is null) return null;

        var receiptId = link.Receipt.Id;

        var approvedRows = await (
                from r in db.PurchaseReturns
                join d in db.PurchaseReturnDetails on r.Id equals d.PurchaseReturnId
                where r.SourceStockInId == receiptId
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

            var received = link.ReceivedBaseQuantity.GetValueOrDefault(productId);
            var already = approved.GetValueOrDefault(productId);
            var total = already + thisQuantity;
            if (total > received + QuantityTolerance)
            {
                throw BusinessException.RuleConflict(
                    $"商品 [{product.ProductName}] 累计退货数量 {total} 超过来源采购入库单可退数量 {received}" +
                    $"（已审核退货 {already} + 本次 {thisQuantity}）");
            }
        }

        return link;
    }

    /// <summary>
    /// 上游客户数据范围（ERP-097 唯一权威口径，<b>仅在适用时</b>）：来源采购入库单链接的采购订单若存在归属客户，
    /// 则该归属客户必须落在当前账号可见范围内，否则按「不存在」拒绝（不泄露归属）。来源为空、未链接采购订单或
    /// 采购订单未登记归属客户时视为不适用（采购退货单本身没有客户字段），继续走既有「采购退货」菜单授权口径。
    /// </summary>
    public static async Task EnsureUpstreamCustomerScopeAsync(IErpDbContext db, long? userId, StockIn? receipt,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (receipt is null) return;

        var owningCustomerId = await StockInAuthorizationRules
            .ResolveOwningCustomerIdAsync(db, receipt.PurchaseOrderId, ct);
        if (owningCustomerId is null or <= 0) return;

        var scope = await SalespersonDataScopeService.ResolveAsync(db, userId);
        if (scope.IsPrivileged) return;
        if (scope.AllowsCustomer(owningCustomerId)) return;

        throw BusinessException.NotFound("采购退货单不存在");
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
}

/// <summary>
/// 已解析的权威来源上下文（只读事实）：来源采购入库单 + 按商品合计的已审核入库基础单位数量。
/// </summary>
public sealed class PurchaseReturnSourceLink
{
    public PurchaseReturnSourceLink(StockIn receipt, IReadOnlyDictionary<long, decimal> receivedBaseQuantity)
    {
        Receipt = receipt;
        ReceivedBaseQuantity = receivedBaseQuantity;
    }

    /// <summary>权威来源采购入库单（含明细）</summary>
    public StockIn Receipt { get; }

    /// <summary>来源入库单按商品合计的已审核入库数量（商品基础单位）</summary>
    public IReadOnlyDictionary<long, decimal> ReceivedBaseQuantity { get; }
}
