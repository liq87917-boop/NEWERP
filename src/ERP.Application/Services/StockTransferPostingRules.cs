using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ERP.Application.Services;

/// <summary>
/// 仓库调拨单过账 / 冲销护栏（ERP-354）：把调拨单的「审核（过账）、销审（冲销）、取消、删除」统一到
/// 同一条「身份 / 菜单授权 + 调拨单行锁 + 可串行化事务」的口径上。
/// <para>关键不变量：审核必须<b>恰好一次</b>产生「调出仓减少 + 调入仓增加」的库存移动与流水；
/// 审核 / 销审 / 取消 / 删除共用同一把调拨单行锁（<c>SELECT ... WITH (UPDLOCK, HOLDLOCK)</c>），
/// 因此并发「审核 / 审核」或「审核 / 取消」不可能同时成功，也不会产生重复或已取消单据的库存。</para>
/// <para>两侧仓库的库存行按<b>确定性顺序</b>（仓库 Id 升序 → 商品 Id 升序，见
/// <see cref="IInventoryService.LockStockRowsAsync"/>）加锁，对向调拨（A→B 与 B→A）不会互相死锁。</para>
/// <para>本类只做<b>纯判定与有界只读查询</b>，不改单据、不改库存 / 流水、不改任何估值规则；
/// 「判定 + 状态变更 + 库存写入」的原子性，以及 `UPDLOCK/HOLDLOCK` 单据行锁的实际获取，
/// 由调用方（<c>StockTransferController</c>）在同一可串行化事务内完成。</para>
/// </summary>
public static class StockTransferPostingRules
{
    /// <summary>
    /// 仓库调拨模块所需既有菜单编码（与 <c>SeedData.Menus</c> 同源）：复用既有「库存查询」菜单，
    /// 与其它库存模块（ERP-029 / ERP-135 库存移动 / 库龄报表）同一口径，<b>不新增任何权限模型</b>，
    /// 也不把空身份当作管理员。
    /// </summary>
    public const string RequiredMenuCode = "stock-query";

    /// <summary>仓库调拨模块菜单中文文案（与既有菜单名一致）</summary>
    public const string RequiredMenuText = "库存查询";

    /// <summary>过账护栏口径文案（接口 / 文档同源）</summary>
    public const string RuleText =
        "仓库调拨单的审核 / 销审 / 取消 / 删除都会重新校验当前身份（缺失 / 禁用 / 未映射一律 fail closed）与既有「库存查询」（stock-query）菜单授权；" +
        "审核先取得调拨单行锁（UPDLOCK/HOLDLOCK），再在锁内重读权威状态与有效流水，随后把「调出仓减少 + 调入仓增加（同一成本单价）、明细成本、库存流水与单据状态」" +
        "包在同一个可串行化事务内，恰好一次；销审按既有红字流水冲销，调入仓货物已被消耗时拒绝且两侧余额 / 流水 / 状态全部不变；" +
        "两侧仓库的库存行按确定性顺序（仓库 Id 升序 → 商品 Id 升序）加锁，对向调拨不互相死锁；" +
        "并发审核 / 审核或审核 / 取消因共用同一把单据行锁而只能成功其一，绝不产生重复或已取消单据的库存。";

    /// <summary>模块边界文案（不改估值口径、不改主数据、不删历史证据）</summary>
    public const string BoundaryText =
        "本护栏只保护仓库调拨单的状态流转与库存写入：不改变库存成本口径（移动加权平均 / 同一成本单价双仓守恒），" +
        "不改写商品、仓库主数据，不改写其它单据与历史库存流水，不新增表 / 列 / 权限 / 菜单，也不删除任何历史证据；" +
        "所有库存增减与冲销仍复用既有 InventoryService（ERP-009 / ERP-025）记账并写流水。";

    /// <summary>
    /// 身份 / 账号状态 / 模块授权三重校验（fail closed）：缺失或非法身份按未认证拒绝，
    /// 禁用账号、无既有「库存查询」菜单授权按权限不足拒绝，绝不猜测身份、绝不退化为管理员。
    /// </summary>
    public static async Task EnsureMenuAuthorizedAsync(IErpDbContext db, long? userId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (userId is null or <= 0)
            throw new BusinessException("请先登录后再操作仓库调拨单", ErrorCodes.Unauthorized);

        var user = await db.SysUsers.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == userId.Value && !u.IsDeleted, ct);
        if (user is null)
            throw new BusinessException("登录账号不存在或已删除，禁止操作仓库调拨单", ErrorCodes.Unauthorized);
        if (user.Status != UserStatus.Enabled)
            throw new BusinessException("登录账号已禁用，禁止操作仓库调拨单（fail closed）", ErrorCodes.Forbidden);

        var menuCodes = await CustomerReceivableReconciliationService.LoadAuthorizedMenuCodesAsync(db, userId.Value);
        if (!menuCodes.Contains(RequiredMenuCode, StringComparer.OrdinalIgnoreCase))
        {
            throw new BusinessException(
                $"当前账号没有「{RequiredMenuText}」（{RequiredMenuCode}）模块授权：拒绝操作仓库调拨单" +
                "（fail closed，不执行任何写入）",
                ErrorCodes.Forbidden);
        }
    }

    /// <summary>
    /// Serialize transfer transactions before document/table range locks. Without a covering
    /// stock index, SQL Server may scan and lock rows outside the ordered identity set.
    /// A transaction-owned application lock prevents opposing scans from deadlocking.
    /// Other document types retain their existing protocols; this does not grant access.
    /// </summary>
    public const string PostingBoundarySql =
        "DECLARE @result int; EXEC @result = sys.sp_getapplock " +
        "@Resource = N'NEWERP.StockTransfer.Posting', @LockMode = 'Exclusive', " +
        "@LockOwner = 'Transaction', @LockTimeout = 30000; " +
        "IF @result < 0 THROW 51000, 'Stock transfer posting lock unavailable', 1;";

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
    /// 由调拨单两侧仓库与明细商品构造需要按确定性顺序加锁的库存行集合（去重后仍为「两侧仓库 × 商品」）。
    /// 这只是锁定集合，不读取 / 不写入库存，也不改变任何数量或成本。
    /// </summary>
    public static IReadOnlyList<StockIdentity> StockKeys(long fromWarehouseId, long toWarehouseId,
        IEnumerable<StockTransferDetail> details)
    {
        ArgumentNullException.ThrowIfNull(details);
        var productIds = details.Where(d => !d.IsDeleted && d.ProductId is > 0)
            .Select(d => d.ProductId!.Value)
            .Distinct()
            .ToList();

        var keys = new List<StockIdentity>(productIds.Count * 2);
        foreach (var productId in productIds)
        {
            if (fromWarehouseId > 0) keys.Add(new StockIdentity(fromWarehouseId, productId));
            if (toWarehouseId > 0) keys.Add(new StockIdentity(toWarehouseId, productId));
        }
        return keys;
    }
}
