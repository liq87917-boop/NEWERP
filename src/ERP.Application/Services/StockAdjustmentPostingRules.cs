using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ERP.Application.Services;

/// <summary>
/// 库存盘点/调整单过账护栏（ERP-355）：把盘点单的「创建 / 修改 / 提交 / 审核（过账）、销审（冲销）、取消、删除」
/// 统一到同一条「身份 / 菜单授权 + 单据行锁 + 确定性库存行锁 + 可串行化事务」的口径上。
/// <para>关键不变量：审核必须基于<b>已验证的账面数量基线</b>——账面数量必须等于权威当前库存数量；
/// 一旦不一致（账面基线过期）立即拒绝，绝不用「实盘 - 客户端账面」的差额静默过账出虚构差异，
/// 而是要求在待提交状态显式重新盘点 / 修改后再提交审核。</para>
/// <para>审核把全部明细、库存流水与单据状态包在同一个可串行化事务内<b>恰好一次</b>；
/// 审核 / 销审 / 取消 / 提交 / 删除 / 修改共用同一把盘点单行锁
/// （<c>SELECT ... WITH (UPDLOCK, HOLDLOCK)</c>），因此并发「审核 / 审核」或「审核 / 取消」不可能同时成功。
/// 受影响的库存行按<b>确定性顺序</b>（仓库 Id 升序 → 商品 Id 升序，见
/// <see cref="InventoryService.OrderStockIdentities"/>）加锁，不形成环形等待。</para>
/// <para>本类只做<b>纯判定与有界只读查询</b>，不改单据、不改库存 / 流水、不改任何估值规则；
/// 「判定 + 状态变更 + 库存写入」的原子性由调用方（<c>StockAdjustmentController</c>）在同一可串行化事务内完成。</para>
/// </summary>
public static class StockAdjustmentPostingRules
{
    /// <summary>
    /// 盘点模块所需既有菜单编码（与 <c>SeedData.Menus</c> 同源）：复用既有「库存查询」菜单，
    /// 与其它库存模块（ERP-029 / ERP-135 / ERP-354）同一口径，<b>不新增任何权限模型</b>，
    /// 也不把空身份当作管理员。
    /// </summary>
    public const string RequiredMenuCode = "stock-query";

    /// <summary>盘点模块菜单中文文案（与既有菜单名一致）</summary>
    public const string RequiredMenuText = "库存查询";

    /// <summary>过账护栏口径文案（接口 / 文档同源）</summary>
    public const string RuleText =
        "库存盘点单的创建 / 修改 / 提交 / 审核 / 销审 / 取消 / 删除都会重新校验当前身份（缺失 / 禁用 / 未映射一律 fail closed）与既有「库存查询」（stock-query）菜单授权；" +
        "审核先取得盘点单行锁（UPDLOCK/HOLDLOCK），再在锁内重读权威状态与有效流水，校验明细（非负数量、非负成本、商品不重复、仓库与商品在用），" +
        "随后按确定性顺序（仓库 Id 升序 → 商品 Id 升序）锁定受影响库存行，并把「已验证的账面数量基线（必须等于权威当前库存）」与全部差异调整、" +
        "库存流水、单据状态包在同一个可串行化事务内，恰好一次；账面数量与权威库存不一致（基线过期）时拒绝过账并要求在待提交状态重新盘点 / 修改；" +
        "销审按既有红字流水冲销，已被后续业务占用时拒绝且余额 / 流水 / 状态全部不变；并发审核 / 审核或审核 / 取消因共用同一把单据行锁而只能成功其一。";

    /// <summary>模块边界文案（不改估值口径、不改主数据、不删历史证据）</summary>
    public const string BoundaryText =
        "本护栏只保护库存盘点单的状态流转与库存写入：不改变库存成本口径（移动加权平均 / 差异金额 = 差异数量 × 成本单价），" +
        "不改写商品、仓库主数据，不改写其它单据与历史库存流水，不做任何「自动更正历史记录」的写回，" +
        "不新增表 / 列 / 权限 / 菜单，也不删除任何历史证据；所有库存增减与冲销仍复用既有 InventoryService（ERP-009）记账并写流水。";

    /// <summary>
    /// 身份 / 账号状态 / 模块授权三重校验（fail closed）：缺失或非法身份按未认证拒绝，
    /// 禁用账号、无既有「库存查询」菜单授权按权限不足拒绝，绝不猜测身份、绝不退化为管理员。
    /// </summary>
    public static async Task EnsureMenuAuthorizedAsync(IErpDbContext db, long? userId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (userId is null or <= 0)
            throw new BusinessException("请先登录后再操作库存盘点单", ErrorCodes.Unauthorized);

        var user = await db.SysUsers.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == userId.Value && !u.IsDeleted, ct);
        if (user is null)
            throw new BusinessException("登录账号不存在或已删除，禁止操作库存盘点单", ErrorCodes.Unauthorized);
        if (user.Status != UserStatus.Enabled)
            throw new BusinessException("登录账号已禁用，禁止操作库存盘点单（fail closed）", ErrorCodes.Forbidden);

        var menuCodes = await CustomerReceivableReconciliationService.LoadAuthorizedMenuCodesAsync(db, userId.Value);
        if (!menuCodes.Contains(RequiredMenuCode, StringComparer.OrdinalIgnoreCase))
        {
            throw new BusinessException(
                $"当前账号没有「{RequiredMenuText}」（{RequiredMenuCode}）模块授权：拒绝操作库存盘点单" +
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
    /// 由盘点仓库与明细商品构造需要按确定性顺序加锁的库存行集合（去重后仍为「仓库 × 商品」）。
    /// 这只是锁定集合，不读取 / 不写入库存，也不改变任何数量或成本。
    /// </summary>
    public static IReadOnlyList<StockIdentity> StockKeys(long warehouseId, IEnumerable<StockAdjustmentDetail> details)
    {
        ArgumentNullException.ThrowIfNull(details);
        if (warehouseId <= 0) return Array.Empty<StockIdentity>();

        return details.Where(d => !d.IsDeleted && d.ProductId is > 0)
            .Select(d => new StockIdentity(warehouseId, d.ProductId!.Value))
            .ToList();
    }

    /// <summary>
    /// 明细静态形状校验（不访问数据库）：商品 Id 必须有效、账面 / 实盘数量与成本单价不得为负数、
    /// 同一商品不得在一张盘点单中重复出现。任一不满足即按参数非法拒绝，绝不进入任何库存写入。
    /// </summary>
    public static void EnsureLineShape(IEnumerable<StockAdjustmentDetail> details)
    {
        ArgumentNullException.ThrowIfNull(details);

        var productIds = new HashSet<long>();
        var line = 0;
        foreach (var d in details.Where(d => !d.IsDeleted))
        {
            line++;
            var label = string.IsNullOrWhiteSpace(d.ProductName) ? $"第 {line} 行" : d.ProductName;

            if (d.ProductId is null or <= 0)
                throw BusinessException.InvalidParameter($"盘点明细 [{label}] 缺少有效商品 Id，无法盘点");
            if (d.BookQuantity < 0)
                throw BusinessException.InvalidParameter($"商品 [{label}] 的账面数量不能为负数");
            if (d.ActualQuantity < 0)
                throw BusinessException.InvalidParameter($"商品 [{label}] 的实盘数量不能为负数");
            if (d.UnitCost < 0)
                throw BusinessException.InvalidParameter($"商品 [{label}] 的成本单价不能为负数");
            if (!productIds.Add(d.ProductId.Value))
                throw BusinessException.InvalidParameter($"商品 [{label}] 在同一张盘点单中重复出现，请合并为一行");
        }
    }

    /// <summary>
    /// 主数据在用校验（只读）：仓库与每个商品都必须存在、未删除且处于启用状态（Status = 1）。
    /// 只读查询，不修改任何主数据；必须在 <see cref="EnsureLineShape"/> 之后调用（保证商品 Id 有效）。
    /// </summary>
    public static async Task EnsureLiveMasterDataAsync(IErpDbContext db, long warehouseId,
        IEnumerable<StockAdjustmentDetail> details, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(details);

        var warehouse = await db.BaseWarehouses.AsNoTracking()
            .FirstOrDefaultAsync(w => w.Id == warehouseId && !w.IsDeleted, ct);
        if (warehouse is null)
            throw BusinessException.InvalidParameter("盘点仓库不存在或已删除，无法盘点");
        if (warehouse.Status != 1)
            throw BusinessException.RuleConflict($"仓库 [{warehouse.WarehouseName}] 已停用，禁止盘点调整（fail closed）");

        foreach (var d in details.Where(d => !d.IsDeleted))
        {
            var productId = d.ProductId!.Value;
            var product = await db.BaseProducts.AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == productId && !p.IsDeleted, ct);
            if (product is null)
                throw BusinessException.InvalidParameter($"商品 [{d.ProductName}] 不存在或已删除，无法盘点");
            if (product.Status != 1)
                throw BusinessException.RuleConflict($"商品 [{product.ProductName}] 已停用，禁止盘点调整（fail closed）");
        }
    }

    /// <summary>账面数量是否已过期：只要与权威当前库存不一致即视为过期（含账面为正但库存行不存在的情形）。</summary>
    public static bool IsStaleBookQuantity(decimal bookQuantity, decimal currentQuantity)
        => bookQuantity != currentQuantity;

    /// <summary>
    /// 账面基线校验：账面数量必须等于权威当前库存数量，否则拒绝过账（不静默过账、不自动改写账面数量），
    /// 要求操作员在待提交状态显式重新盘点 / 修改后再提交审核。
    /// </summary>
    public static void EnsureFreshBookQuantity(string productName, decimal bookQuantity, decimal currentQuantity)
    {
        if (!IsStaleBookQuantity(bookQuantity, currentQuantity)) return;

        var label = string.IsNullOrWhiteSpace(productName) ? "商品" : productName;
        throw BusinessException.RuleConflict(
            $"商品 [{label}] 的账面数量 {bookQuantity} 与当前库存 {currentQuantity} 不一致：" +
            "账面基线已过期，禁止按虚构差异过账；请在待提交状态重新盘点或修改后再提交审核");
    }
}
