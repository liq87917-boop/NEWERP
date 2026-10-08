using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ERP.Application.Services;

/// <summary>
/// 来源出库单 / 来源入库单「取消前的退货引用护栏」（ERP-359）。
/// <para>背景：采购入库单取消（<c>StockInController.Cancel</c>）与销售出库单取消（<c>StockOutController.Cancel</c>）
/// 会冲销已过账的库存，但既有实现没有检查是否仍存在<b>显式引用</b>该来源单据的已审核退货单——一旦来源被冲销，
/// 退货依据（成交 / 入库证据）即失效，库存台账与退货审计证据链随之断裂。</para>
/// <para>本类只做<b>纯判定与有界只读查询 + 行锁</b>：只读取既有的
/// <see cref="SalesReturn.SourceStockOutId"/> / <see cref="PurchaseReturn.SourceStockInId"/> 显式链接（可空，刻意不建外键），
/// <b>绝不</b>按来源单号文本、金额或相似度推断链接（<c>null</c> 来源的无关退货永不阻断）；不落库、不改单据 / 库存 / 流水、
/// 不消耗单据号、不新增任何表 / 列 / 菜单 / 权限模型。「判定 + 状态变更 + 库存冲销」的原子性，以及来源单据行锁
/// （<c>UPDLOCK/HOLDLOCK</c>）的实际获取，由调用方在同一可串行化事务内完成，并与退货审核 / 销审共用同一把来源行锁。</para>
/// </summary>
public static class ReturnSourceCancellationRules
{
    /// <summary>
    /// 来源销售出库单行锁（与销售退货审核 / 销审<b>同一</b>把 <c>UPDLOCK, HOLDLOCK</c>）：
    /// 把「取消来源出库单」与「销售退货审核 / 销审」串行化在同一事务内，保证并发时只出现一种一致结果。
    /// </summary>
    public const string LockStockOutRowSql =
        "SELECT Id FROM db_owner.StockOuts WITH (UPDLOCK, HOLDLOCK) WHERE Id = {0}";

    /// <summary>
    /// 来源采购入库单行锁（与采购退货审核 / 销审<b>同一</b>把 <c>UPDLOCK, HOLDLOCK</c>）：
    /// 把「取消来源入库单」与「采购退货审核 / 销审」串行化在同一事务内，保证并发时只出现一种一致结果。
    /// </summary>
    public const string LockStockInRowSql =
        "SELECT Id FROM db_owner.StockIns WITH (UPDLOCK, HOLDLOCK) WHERE Id = {0}";

    /// <summary>拒绝来源取消时给出的可执行处置要求（先销审 / 取消退货单，再取消来源单据）</summary>
    public const string ReversalRequirementText =
        "请先对已审核退货单执行销审（红字冲销）或取消，再取消来源单据";

    /// <summary>口径文案（接口 / 文档同源）</summary>
    public const string RuleText =
        "来源出库单 / 来源入库单取消前，必须在同一来源单据行锁与可串行化事务内，用实时数据确认没有仍然生效的已审核退货单" +
        "显式引用本单（SalesReturn.SourceStockOutId / PurchaseReturn.SourceStockInId，未删除 + 已审核）；" +
        "存在时 fail closed 拒绝取消并给出「先销审或取消退货单」的可执行要求，绝不冲销库存、绝不改状态；" +
        "null 来源的无关退货不阻断；绝不按来源单号文本推断引用；销审 / 取消退货单后即可按既有流程取消来源单据。";

    /// <summary>边界文案（不改估值口径、不改主数据、不删历史证据）</summary>
    public const string BoundaryText =
        "本护栏只新增「来源取消前的退货引用判定」与来源单据行锁：不改变库存成本口径（移动加权平均），" +
        "不改写退货原始数量、历史库存流水与财务分摊历史，不改商品 / 仓库 / 客户 / 供应商主数据，" +
        "不删除任何审计证据，不新增表 / 列 / 权限 / 菜单；库存冲销仍复用既有 InventoryService（ERP-009）记账并写流水。";

    /// <summary>
    /// 关系型后端（SQL Server）判定：内存库等非关系型提供程序没有行锁语义，锁定等价无操作。
    /// 使用 <c>DatabaseFacade.ProviderName</c>（EF Core 基础 API），不依赖关系型扩展。
    /// </summary>
    public static bool IsRelationalProvider(IErpDbContext db)
    {
        ArgumentNullException.ThrowIfNull(db);
        return string.Equals(db.Database.ProviderName, "Microsoft.EntityFrameworkCore.SqlServer",
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 取消来源销售出库单前的实时判定：存在「显式来源 = 本出库单」且未被删除、状态为已审核的销售退货单时，
    /// 抛 <see cref="BusinessException"/>（<see cref="ErrorCodes.RuleConflict"/>）并给出可执行的处置要求。
    /// <para>只读判定：被拒绝时不改写单据 / 明细 / 状态 / 库存 / 流水。null 来源的无关退货不参与判定，
    /// 绝不按来源单号文本推断引用。</para>
    /// </summary>
    public static async Task EnsureNoEffectiveApprovedSalesReturnAsync(IErpDbContext db, long stockOutId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);

        var blocking = await db.SalesReturns.AsNoTracking()
            .Where(r => r.SourceStockOutId == stockOutId
                        && !r.IsDeleted
                        && r.Status == DocumentStatus.Approved)
            .OrderBy(r => r.Id)
            .Select(r => new { r.Id, r.ReturnNo })
            .FirstOrDefaultAsync(ct);

        if (blocking is null) return;

        throw BusinessException.RuleConflict(
            $"来源销售出库单已被已审核销售退货单 [{blocking.ReturnNo}]（Id {blocking.Id}）显式引用，" +
            $"取消会破坏退货依据与库存台账：{ReversalRequirementText}");
    }

    /// <summary>
    /// 取消来源采购入库单前的实时判定：存在「显式来源 = 本入库单」且未被删除、状态为已审核的采购退货单时，
    /// 抛 <see cref="BusinessException"/>（<see cref="ErrorCodes.RuleConflict"/>）并给出可执行的处置要求。
    /// <para>只读判定：被拒绝时不改写单据 / 明细 / 状态 / 库存 / 流水。null 来源的无关退货不参与判定，
    /// 绝不按来源单号文本推断引用。</para>
    /// </summary>
    public static async Task EnsureNoEffectiveApprovedPurchaseReturnAsync(IErpDbContext db, long stockInId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);

        var blocking = await db.PurchaseReturns.AsNoTracking()
            .Where(r => r.SourceStockInId == stockInId
                        && !r.IsDeleted
                        && r.Status == DocumentStatus.Approved)
            .OrderBy(r => r.Id)
            .Select(r => new { r.Id, r.ReturnNo })
            .FirstOrDefaultAsync(ct);

        if (blocking is null) return;

        throw BusinessException.RuleConflict(
            $"来源采购入库单已被已审核采购退货单 [{blocking.ReturnNo}]（Id {blocking.Id}）显式引用，" +
            $"取消会破坏退货依据与库存台账：{ReversalRequirementText}");
    }
}
