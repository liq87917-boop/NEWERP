using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ERP.Application.Services;

/// <summary>
/// 销售订单取消护栏（ERP-347）：在既有取消路由上校验身份 / 菜单 / 客户数据范围与实时单据状态，
/// 并在取消前拒绝仍有「以本单为来源、未删除、已审核」的销售出库履约、仍有「未删除且未取消」的
/// 采购订单履约（<c>PurchaseOrder.OwningSalesOrderId</c> 指向本单）或仍有「有效（未作废）」客户收款引用
/// （分摊）证据（<c>CustomerReceiptAllocation.SalesOrderId</c> 指向本单）的订单。
/// <para>本类只做<b>纯判定与有界只读查询</b>，不落库、不改单据、不冲销库存与财务、不开启事务；
/// 「取消状态变更 + 拒绝判定」的原子性与同单并发串行化由调用方（<c>SalesOrderController.Cancel</c>）
/// 在同一可串行化事务内对销售订单行加 UPDLOCK/HOLDLOCK 完成（与 ERP-343 出库审核、ERP-346 采购归属关联同源口径）。</para>
/// <para>链接只按既有显式引用字段判定（出库单 <c>SalesOrderId</c> / 采购单 <c>OwningSalesOrderId</c> /
/// 收款引用行 <c>SalesOrderId</c>），绝不按单号文本、金额或相似度猜测链接，也绝不跨币种合计金额；
/// 已取消 / 已冲销 / 已作废 / 已删除的证据只有在既有权威工作流显式标记其失效时才被忽略。</para>
/// </summary>
public static class SalesOrderCancellationRules
{
    /// <summary>取消所需既有菜单编码（复用销售订单模块菜单，与 <c>SeedData.Menus</c> 同源）</summary>
    public const string RequiredMenuCode = "sales-order";

    /// <summary>取消所需菜单的中文文案（与既有菜单名一致）</summary>
    public const string RequiredMenuText = "销售订单";

    /// <summary>取消护栏口径文案（接口 / 文档同源）</summary>
    public const string RuleText =
        "取消销售订单前，先校验当前身份、销售订单（sales-order）菜单授权与客户数据范围；" +
        "当存在「以本单为来源、未删除、已审核」的销售出库单，或存在「未删除且未取消」的采购订单（归属销售订单指向本单）" +
        "与「有效（未作废）」客户收款引用（分摊）证据时拒绝取消；" +
        "已取消 / 已冲销 / 已作废 / 已删除的证据只有在既有权威工作流显式标记其失效后才被忽略，绝不按字符串或金额猜测链接、绝不跨币种合计；" +
        "取消本身不冲销库存或财务，冲销只走既有冲销 / 作废工作流。";

    /// <summary>
    /// 校验销售订单能否取消（不写库）。调用方必须在同一可串行化事务内持有该订单行更新锁后再调用，
    /// 以保证「判定」与「状态变更」原子，且与同单出库审核、采购归属关联与收款引用登记串行化。
    /// </summary>
    /// <param name="db">数据上下文（只读查询）。</param>
    /// <param name="order">已加载且未删除的销售订单（含明细），不能为 null。</param>
    /// <param name="userId">当前登录用户 Id；缺失或非正整数按未认证拒绝。</param>
    /// <param name="ct">取消令牌。</param>
    public static async Task ValidateCancellationAsync(
        IErpDbContext db, SalesOrder order, long? userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(order);

        await EnsureAuthorizedAsync(db, order, userId, ct);
        EnsureCancellableState(order);
        await EnsureNoActiveFulfillmentAsync(db, order, ct);
    }

    // ==================== 授权（fail closed） ====================

    /// <summary>身份 / 菜单 / 客户数据范围三重校验：任一缺失即拒绝，绝不猜测身份或范围。</summary>
    private static async Task EnsureAuthorizedAsync(
        IErpDbContext db, SalesOrder order, long? userId, CancellationToken ct)
    {
        if (userId is null or <= 0)
            throw new BusinessException("请先登录后再取消销售订单", ErrorCodes.Unauthorized);

        var scope = await SalespersonDataScopeService.ResolveAsync(db, userId.Value);

        // 特权账号（超级管理员 / 系统内置角色 / 显式特权角色）继承既有全部访问，与 ERP-097 数据范围同源，
        // 不再要求逐条菜单授权（生产端系统角色由 SeedData 授予全部菜单）；普通账号仍必须显式具备销售订单菜单。
        if (!scope.IsPrivileged)
        {
            var menuCodes = await CustomerReceivableReconciliationService.LoadAuthorizedMenuCodesAsync(db, userId.Value);
            if (!menuCodes.Contains(RequiredMenuCode, StringComparer.OrdinalIgnoreCase))
            {
                throw new BusinessException(
                    $"当前账号没有「{RequiredMenuText}」（{RequiredMenuCode}）模块授权：拒绝取消销售订单" +
                    "（fail closed，不执行任何状态变更）",
                    ErrorCodes.Forbidden);
            }
        }

        if (!scope.AllowsCustomer(order.CustomerId))
        {
            throw new BusinessException(
                "当前账号的客户数据范围不包含该销售订单的客户：拒绝取消（fail closed，不泄露范围外订单）",
                ErrorCodes.Forbidden);
        }
    }

    // ==================== 实时单据状态 ====================

    /// <summary>实时单据状态：已取消拒绝重复取消；终止态（驳回 / 完成）不允许取消。</summary>
    private static void EnsureCancellableState(SalesOrder order)
    {
        if (order.Status == DocumentStatus.Cancelled)
            throw BusinessException.RuleConflict("销售订单已取消，不能重复取消");
        if (order.Status is not (DocumentStatus.Pending or DocumentStatus.Submitted or DocumentStatus.Approved))
            throw BusinessException.RuleConflict("当前状态不允许取消");
    }

    // ==================== 有效履约证据护栏 ====================

    /// <summary>
    /// 拒绝仍有「已审核且未冲销」的销售出库履约、未删除且未取消的采购履约或有效客户收款引用证据的订单。
    /// 判定只做有界只读查询，不合计金额、不猜测链接。
    /// </summary>
    private static async Task EnsureNoActiveFulfillmentAsync(
        IErpDbContext db, SalesOrder order, CancellationToken ct)
    {
        // 1) 以本单为来源、未删除、已审核（未冲销）的销售出库单：存在即拒绝
        var hasApprovedOutbound = await db.StockOuts.AsNoTracking()
            .AnyAsync(s => !s.IsDeleted && s.SalesOrderId == order.Id
                           && s.Status == DocumentStatus.Approved, ct);
        if (hasApprovedOutbound)
            throw BusinessException.RuleConflict("存在已审核且未冲销的销售出库单：请先取消 / 冲销出库单，再取消销售订单");

        // 2) 归属本销售订单、未删除且未取消的采购订单（采购备货履约）：存在即拒绝
        var hasActiveProcurement = await db.PurchaseOrders.AsNoTracking()
            .AnyAsync(p => !p.IsDeleted && p.OwningSalesOrderId == order.Id
                           && p.Status != DocumentStatus.Cancelled, ct);
        if (hasActiveProcurement)
            throw BusinessException.RuleConflict("存在未删除且未取消的采购订单（归属本销售订单）：请先取消 / 删除采购订单，再取消销售订单");

        // 3) 指向本销售订单、未删除且有效（未作废）的客户收款引用（分摊）证据：存在即拒绝
        var hasActiveAllocation = await db.CustomerReceiptAllocations.AsNoTracking()
            .AnyAsync(a => !a.IsDeleted && a.SalesOrderId == order.Id
                           && a.Status == CustomerReceiptAllocationRules.StatusActive, ct);
        if (hasActiveAllocation)
            throw BusinessException.RuleConflict("存在有效的客户收款引用（分摊）证据：请先作废收款引用行，再取消销售订单");
    }
}
