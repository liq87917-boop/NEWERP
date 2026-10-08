using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ERP.Application.Services;

/// <summary>
/// 销售订单取消护栏（ERP-347 / ERP-369 / ERP-381）：在既有取消路由上校验身份 / 菜单 / 客户数据范围与实时单据状态，
/// 并在取消前拒绝仍有「以本单为来源、未删除、已审核」的销售出库履约、仍有「未删除且未取消」的
/// 采购订单履约（<c>PurchaseOrder.OwningSalesOrderId</c> 指向本单）、仍有「有效（未作废）」客户收款引用
/// （分摊）证据（<c>CustomerReceiptAllocation.SalesOrderId</c> 指向本单）、仍有「未删除且未取消」的定金申请单
/// （<c>FinanceDepositApply.SalesOrderId</c> 指向本单，ERP-381 的权威收款申请来源链接），或仍有「未删除、已审核」
/// 且明细通过显式来源销售订单明细链接本单明细的预装柜需求计划证据（ERP-368 的 <c>SourceSalesOrderDetailId</c>，ERP-369）的订单。
/// <para>本类只做<b>纯判定与有界只读查询</b>，不落库、不改单据、不冲销库存与财务、不开启事务；
/// 「取消状态变更 + 拒绝判定」的原子性与同单并发串行化由调用方（<c>SalesOrderController.Cancel</c>）
/// 在同一可串行化事务内对销售订单行加 UPDLOCK/HOLDLOCK 完成（与 ERP-343 出库审核、ERP-346 采购归属关联同源口径）。</para>
/// <para>链接只按既有显式引用字段判定（出库单 <c>SalesOrderId</c> / 采购单 <c>OwningSalesOrderId</c> /
/// 收款引用行 <c>SalesOrderId</c> / 预装柜明细 <c>SourceSalesOrderDetailId</c>），绝不按单号文本、金额或相似度猜测链接，
/// 也绝不跨币种合计金额；已取消 / 已冲销 / 已作废 / 已删除的证据只有在既有权威工作流显式标记其失效时才被忽略。</para>
/// </summary>
public static class SalesOrderCancellationRules
{
    /// <summary>取消所需既有菜单编码（复用销售订单模块菜单，与 <c>SeedData.Menus</c> 同源）</summary>
    public const string RequiredMenuCode = "sales-order";

    /// <summary>取消所需菜单的中文文案（与既有菜单名一致）</summary>
    public const string RequiredMenuText = "销售订单";

    /// <summary>
    /// ERP-383 唯一全局锁序与本模块的锁序审计：来源销售订单行（Id 升序，本取消护栏在可串行化事务内以
    /// <c>UPDLOCK, HOLDLOCK</c> 取得该行）→ 客户销项发票行 → 客户收款单行 → 引用 / 分摊 / 证据行。
    /// 本类只做纯判定（不落库、不取行锁），行锁由 <c>SalesOrderController.Cancel</c> 在事务内取得；
    /// 客户销项发票模块（ERP-383）与收款单模块（ERP-349 / ERP-378）都只在本行锁之后获取下游锁，绝不反向加锁。
    /// </summary>
    public const string GlobalLockOrderText = CustomerSalesInvoiceConcurrencyRules.GlobalLockOrderText
        + "销售订单取消在可串行化事务内先取来源销售订单行锁（第 1 段），发票 / 收款单模块只在其后获取下游锁。";

    /// <summary>取消护栏口径文案（接口 / 文档同源）</summary>
    public const string RuleText =
        "取消销售订单前，先校验当前身份、销售订单（sales-order）菜单授权与客户数据范围；" +
        "当存在「以本单为来源、未删除、已审核」的销售出库单，或存在「未删除且未取消」的采购订单（归属销售订单指向本单）、" +
        "「有效（未作废）」客户收款引用（分摊）证据与「未删除且未取消」的定金申请单（SalesOrderId 显式指向本单），" +
        "或存在「未删除、已审核」且明细显式链接本单明细的预装柜需求计划证据时拒绝取消；" +
        "已取消 / 已冲销 / 已作废 / 已删除的证据只有在既有权威工作流显式标记其失效后才被忽略，绝不按字符串或金额猜测链接、绝不跨币种合计；" +
        "取消本身不冲销库存或财务，冲销只走既有冲销 / 作废工作流。";

    /// <summary>
    /// 拒绝文案中允许列举的预装柜单数量上限（有界：绝不无界输出单据明细，超出时只给出总数）。
    /// </summary>
    public const int MaxReportedPreLoadingLinks = 5;

    /// <summary>
    /// 预装柜需求计划证据的解除路径（可执行要求，与 <see cref="PreLoadingSalesOrderLinkRules.EffectiveDemandEvidenceText"/> 同源）。
    /// </summary>
    public const string PreLoadingReversalRequirementText =
        "请先在「预装柜单」中取消该预装柜单，或清除其显式来源销售订单明细链接后再取消销售订单；" +
        "本护栏不改动下游库存 / 采购 / 财务，也不删除任何历史证据（取消预装柜单会原样保留其链接与数量）。";

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
    /// 拒绝仍有「已审核且未冲销」的销售出库履约、未删除且未取消的采购履约、有效客户收款引用证据、
    /// 未删除且未取消的定金申请单来源链接（ERP-381），或「未删除、已审核」且明细显式链接本单明细的
    /// 预装柜需求计划证据（ERP-369）的订单。判定只做有界只读查询，不合计金额、不猜测链接。
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

        // 4) 以本单为显式来源（SalesOrderId）、未删除且未取消的定金申请单（ERP-381 权威收款申请来源链接）：存在即拒绝
        var hasActiveDepositApply = await db.FinanceDepositApplies.AsNoTracking()
            .AnyAsync(a => !a.IsDeleted && a.SalesOrderId == order.Id
                           && a.Status != DocumentStatus.Cancelled, ct);
        if (hasActiveDepositApply)
            throw BusinessException.RuleConflict(
                "存在未删除且未取消的定金申请单（SalesOrderId 显式指向本销售订单）："
                + "请先在定金申请单模块取消 / 删除后再取消销售订单"
                + "（取消保留历史与审计，不物理删除收款申请证据，也绝不按文本猜测来源）");

        // 5) 以本单明细为显式来源、未删除且已审核的预装柜需求计划证据（ERP-369）：存在即拒绝
        await EnsureNoLinkedPreLoadingDemandAsync(db, order, ct);
    }

    /// <summary>
    /// 拒绝仍有「未删除、已审核」预装柜单通过显式来源销售订单明细链接本单明细的订单（ERP-369）。
    /// <para>来源明细 Id 一律从库中**实时读取**（不依赖调用方是否加载导航属性），未链接（<c>null</c> 历史遗留）/
    /// 已删除预装柜单 / 未审核（待提交 / 已提交 / 已驳回 / 已取消）以及链接到其它订单明细的无关链接**都不阻断**；
    /// 判定只做有界只读查询，绝不改写下游库存 / 采购 / 财务，也绝不清除预装柜单链接与历史证据。</para>
    /// </summary>
    private static async Task EnsureNoLinkedPreLoadingDemandAsync(
        IErpDbContext db, SalesOrder order, CancellationToken ct)
    {
        var detailIds = await db.SalesOrderDetails.AsNoTracking()
            .Where(d => d.SalesOrderId == order.Id && !d.IsDeleted)
            .Select(d => d.Id)
            .ToListAsync(ct);
        if (detailIds.Count == 0) return;

        var evidence = await PreLoadingSalesOrderLinkRules
            .LoadEffectiveApprovedSourceLinksAsync(db, detailIds, ct);
        if (evidence.Count == 0) return;

        throw BusinessException.RuleConflict(DescribePreLoadingBlock(evidence));
    }

    /// <summary>
    /// 构造**可执行**的预装柜需求计划证据拒绝文案：有界列举预装柜单号（附链接行数与商品名，最多
    /// <see cref="MaxReportedPreLoadingLinks"/> 张，超出只给总数），并显式给出解除路径
    /// （取消预装柜单或清除其显式来源链接）。绝不输出连接串 / 金额 / 币种等无关信息。
    /// </summary>
    /// <param name="evidence">有效需求承诺证据（来自 <see cref="PreLoadingSalesOrderLinkRules.LoadEffectiveApprovedSourceLinksAsync"/>）。</param>
    public static string DescribePreLoadingBlock(
        IReadOnlyCollection<PreLoadingSalesOrderLinkEvidence> evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);

        var groups = evidence
            .GroupBy(e => e.PreLoadingId)
            .OrderBy(g => g.Key)
            .ToList();

        var shown = groups.Take(MaxReportedPreLoadingLinks).Select(group =>
        {
            var no = group.Select(e => e.PreLoadingNo)
                .FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));
            var label = string.IsNullOrWhiteSpace(no) ? $"预装柜单 Id {group.Key}" : $"预装柜单 {no}";
            var productNames = group.Select(e => e.ProductName)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct().Take(3).ToList();
            return productNames.Count == 0
                ? $"{label}（{group.Count()} 行）"
                : $"{label}（{group.Count()} 行：{string.Join(" / ", productNames)}）";
        });

        var overflow = groups.Count > MaxReportedPreLoadingLinks
            ? $"等共 {groups.Count} 张"
            : string.Empty;

        return $"存在已审核且未删除的预装柜需求计划证据（{string.Join("、", shown)}{overflow}）：" +
               "其明细已通过显式来源销售订单明细链接本单，取消销售订单会使已承诺需求失去依据。" +
               PreLoadingReversalRequirementText;
    }
}
