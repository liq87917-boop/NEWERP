using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ERP.Application.Services;

/// <summary>
/// 采购订单显式归属销售订单链接规则（ERP-346）：保存（创建 / 更新）前把客户端提交的
/// <see cref="PurchaseOrder.OwningSalesOrderId"/> 解析为权威来源，并统一来源快照。
/// <para>未链接（<c>OwningSalesOrderId</c> 为 null）的采购订单保持原有行为，绝不臆造销售链接；</para>
/// <para>显式链接构成「权威来源」要求：当前账号具备采购订单（purchase-order）模块授权、其客户数据范围覆盖来源销售订单客户；
/// 来源销售订单存在、未删除、已审核且未被取消；采购订单归属客户与来源客户一致（未填写时由来源派生）；
/// 采购明细商品必须能在来源销售订单明细中找到且单位兼容；任一不满足即 fail closed 拒绝保存。</para>
/// <para>归属客户 / 归属客户名称 / 归属销售订单号一律由来源销售订单与客户档案权威派生，绝不采信客户端冲突快照。</para>
/// <para>本类只做纯内存判定与有界只读查询（按 Id 精确解析单个来源订单、按来源客户 Id 精确读取客户档案），
/// 不落库、不改销售 / 库存 / 财务、不开启事务；原始商业币种 / 单价保持不变，采购总额仍由调用方复用既有
/// <c>PurchaseOrderController.Calculate</c> 口径（Σ 数量 × 单价）计算，不强制币种相等、不跨币种合计。</para>
/// </summary>
public static class PurchaseSalesOrderLinkRules
{
    /// <summary>关联销售订单所需的既有菜单编码（复用采购订单模块菜单，与 <c>PurchaseOrderCancellationRules</c> 同源）</summary>
    public const string RequiredMenuCode = "purchase-order";

    /// <summary>关联销售订单所需菜单的中文文案（与既有菜单名一致）</summary>
    public const string RequiredMenuText = "采购订单";

    /// <summary>链接口径文案（接口 / 文档同源）</summary>
    public const string RuleText =
        "采购订单显式填写归属销售订单 Id 后，服务端只以该 Id 权威解析来源：来源必须存在、未删除、已审核、未被取消，"
        + "且当前账号具备采购订单（purchase-order）模块授权、其客户数据范围覆盖来源销售订单客户；"
        + "采购订单归属客户须与来源客户一致（未填写时由来源派生），采购明细商品必须能在来源销售订单明细中找到且单位兼容；"
        + "归属客户 / 归属客户名称 / 归属销售订单号一律由来源销售订单与客户档案权威派生，绝不采信客户端冲突快照；"
        + "未填写归属销售订单 Id 的采购订单保持原有未关联行为，不臆造任何销售链接；"
        + "原始商业币种 / 单价保持不变，采购总额仍按既有 Calculate 口径（Σ 数量 × 单价）计算，不强制币种相等、不跨币种合计，"
        + "不静默改写销售 / 库存 / 财务数据；更新时重新解析来源与权限，已提交或已审核单据的冻结规则保持不变。";

    /// <summary>来源销售订单不存在 / 已删除的拒绝文案（ERP-393 候选 / 已存储来源解析同源复用）</summary>
    public const string SourceNotFoundText = "归属销售订单不存在或已删除，不能作为采购备货来源";

    /// <summary>来源销售订单已取消的拒绝文案（ERP-393 候选 / 已存储来源解析同源复用）</summary>
    public const string SourceCancelledText = "归属销售订单已取消，不能作为采购备货来源";

    /// <summary>来源销售订单未审核的拒绝文案（ERP-393 候选 / 已存储来源解析同源复用）</summary>
    public const string SourceNotApprovedText = "归属销售订单未审核，不能作为采购备货来源";

    /// <summary>
    /// 来源销售订单是否可作为<b>新</b>归属来源（ERP-393 候选 / 已存储来源解析复用）：仅「已审核」满足，
    /// 已取消 / 未审核 / 已驳回 / 已完成一律不可作为新来源（历史已记录的链接只读保留）。
    /// </summary>
    public static bool IsEligibleNewSource(DocumentStatus status) => status == DocumentStatus.Approved;

    /// <summary>来源销售订单不可作为新来源时的权威原因文案（已审核返回空串）。</summary>
    public static string SourceIneligibleReason(DocumentStatus status)
        => status == DocumentStatus.Cancelled ? SourceCancelledText
            : status == DocumentStatus.Approved ? string.Empty
            : SourceNotApprovedText;

    /// <summary>
    /// 在保存前解析并应用归属销售订单链接。未链接（null）直接返回，保持历史行为；
    /// 显式链接必须为正整数且构成权威来源，否则 fail closed 抛业务异常，不落库、不改任何状态。
    /// </summary>
    /// <param name="db">数据上下文（只读查询）。</param>
    /// <param name="entity">待保存的采购订单（含明细）。</param>
    /// <param name="userId">当前登录用户 Id；缺失或非正整数按未认证拒绝。</param>
    /// <param name="ct">取消令牌。</param>
    public static async Task ApplyLinkAsync(
        IErpDbContext db, PurchaseOrder entity, long? userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(entity);

        if (entity.OwningSalesOrderId is null) return;
        if (entity.OwningSalesOrderId is not > 0)
            throw BusinessException.InvalidParameter("归属销售订单 Id 必须为正整数");

        var link = await ResolveAuthoritativeLinkOrThrowAsync(db, entity, userId, ct);

        // 权威派生：归属客户 / 归属客户名称 / 归属销售订单号一律来自来源销售订单与客户档案，绝不采信客户端快照
        entity.OwningCustomerId = link.Order.CustomerId;
        entity.OwningCustomerName = link.Customer.CustomerName;
        entity.OwningSalesOrderNo = link.Order.OrderNo;
    }

    /// <summary>
    /// 状态流转（尤其是审核）在锁内重新校验归属来源仍为权威可用：来源销售订单必须存在、未删除、已审核且未被取消。
    /// 未链接（null / 非正）直接返回；任一不满足即 fail closed，拒绝「来源已作废」的采购审核（消除审核与来源失效的竞争）。
    /// </summary>
    public static async Task EnsureSourceLinkStillValidAsync(IErpDbContext db, long? owningSalesOrderId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (owningSalesOrderId is null or <= 0) return;

        var order = await db.SalesOrders.AsNoTracking()
            .FirstOrDefaultAsync(o => o.Id == owningSalesOrderId.Value && !o.IsDeleted, ct)
            ?? throw BusinessException.RuleConflict(SourceNotFoundText);
        if (order.Status == DocumentStatus.Cancelled)
            throw BusinessException.RuleConflict(SourceCancelledText);
        if (order.Status != DocumentStatus.Approved)
            throw BusinessException.RuleConflict(SourceNotApprovedText);
    }


    // ==================== 私有助手 ====================

    private static async Task<LinkContext> ResolveAuthoritativeLinkOrThrowAsync(
        IErpDbContext db, PurchaseOrder entity, long? userId, CancellationToken ct)
    {
        await EnsureAuthorizedAsync(db, userId, ct);

        var order = await db.SalesOrders.AsNoTracking().Include(o => o.Details)
            .FirstOrDefaultAsync(o => o.Id == entity.OwningSalesOrderId!.Value && !o.IsDeleted, ct)
            ?? throw BusinessException.RuleConflict("归属销售订单不存在或已删除");
        if (order.Status == DocumentStatus.Cancelled)
            throw BusinessException.RuleConflict(SourceCancelledText);
        if (order.Status != DocumentStatus.Approved)
            throw BusinessException.RuleConflict(SourceNotApprovedText);

        var scope = await SalespersonDataScopeService.ResolveAsync(db, userId!.Value);
        if (!scope.AllowsCustomer(order.CustomerId))
            throw new BusinessException(
                "当前账号的客户数据范围不包含归属销售订单的客户：拒绝关联（fail closed，不泄露范围外销售订单）",
                ErrorCodes.Forbidden);

        if (entity.OwningCustomerId.HasValue && entity.OwningCustomerId.Value > 0
            && entity.OwningCustomerId.Value != order.CustomerId)
            throw BusinessException.RuleConflict("采购订单归属客户与归属销售订单客户不一致");

        var customer = await db.BaseCustomers.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == order.CustomerId && !c.IsDeleted, ct)
            ?? throw BusinessException.RuleConflict("归属销售订单的客户不存在或已删除");

        EnsureProductAndUnitCompatible(entity, order);

        return new LinkContext(order, customer);
    }

    /// <summary>
    /// 身份 / 账号状态 / 模块授权校验（ERP-371 起统一收敛到
    /// <see cref="PurchaseOrderAuthorizationRules.EnsureMenuAuthorizedAsync"/>）：缺失 / 已删除身份按未认证拒绝，
    /// 禁用账号与缺少既有 <c>purchase-order</c> 菜单授权按权限不足拒绝，绝不猜测身份或范围。
    /// </summary>
    private static async Task EnsureAuthorizedAsync(IErpDbContext db, long? userId, CancellationToken ct)
        => await PurchaseOrderAuthorizationRules.EnsureMenuAuthorizedAsync(db, userId, ct);

    /// <summary>
    /// ERP-418 转换目的地归属复核（比价 → 采购订单转换专用，纯只读）：
    /// 生成草稿的归属客户必须与权威归属销售订单客户一致（任一侧缺失时不臆造冲突）；
    /// 归属销售订单存在明细时，采购明细商品必须能在来源明细中找到且单位兼容；
    /// 来源明细为空（历史 / 摘要订单）时无法判定，保持放行（不误伤既有可转换比价行）。
    /// 与显式链接保存（<see cref="ApplyLinkAsync"/>）共用同一商品 / 单位兼容判定与文案，绝不新增口径。
    /// </summary>
    public static async Task EnsureOwningSourceCompatibleAsync(IErpDbContext db, PurchaseOrder draft,
        long owningSalesOrderId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(draft);
        if (owningSalesOrderId <= 0) return;

        var order = await db.SalesOrders.AsNoTracking().Include(o => o.Details)
            .FirstOrDefaultAsync(o => o.Id == owningSalesOrderId, ct);
        if (order is null || order.IsDeleted) return;      // 存在性 / 删除由锁内协议统一受控判定

        if (order.CustomerId > 0 && draft.OwningCustomerId is > 0
            && draft.OwningCustomerId.Value != order.CustomerId)
            throw BusinessException.RuleConflict("采购订单归属客户与归属销售订单客户不一致");

        var salesLines = order.Details.Where(d => !d.IsDeleted && d.ProductId > 0).ToList();
        if (salesLines.Count == 0) return;                // 来源无明细：不可判定，保持放行

        foreach (var line in draft.Details.Where(d => !d.IsDeleted && d.ProductId > 0))
        {
            var salesLine = salesLines.FirstOrDefault(s => s.ProductId == line.ProductId)
                ?? throw BusinessException.RuleConflict(
                    $"采购明细商品 [{DisplayName(line)}] 不在归属销售订单明细中");

            if (!UnitsCompatible(line.Unit, salesLine.Unit))
                throw BusinessException.RuleConflict(
                    $"采购明细商品 [{DisplayName(line)}] 单位 [{line.Unit}] 与归属销售订单单位 [{salesLine.Unit}] 不兼容");
        }
    }

    /// <summary>商品 / 单位兼容性：采购明细商品必须能在来源销售订单明细中找到，且单位兼容。</summary>
    private static void EnsureProductAndUnitCompatible(PurchaseOrder entity, SalesOrder order)
    {
        var salesLines = order.Details
            .Where(d => !d.IsDeleted && d.ProductId > 0)
            .ToList();

        foreach (var line in entity.Details.Where(d => !d.IsDeleted && d.ProductId > 0))
        {
            var salesLine = salesLines.FirstOrDefault(s => s.ProductId == line.ProductId)
                ?? throw BusinessException.RuleConflict(
                    $"采购明细商品 [{DisplayName(line)}] 不在归属销售订单明细中");

            if (!UnitsCompatible(line.Unit, salesLine.Unit))
                throw BusinessException.RuleConflict(
                    $"采购明细商品 [{DisplayName(line)}] 单位 [{line.Unit}] 与归属销售订单单位 [{salesLine.Unit}] 不兼容");
        }
    }

    /// <summary>单位兼容：任一侧单位缺失（无法判定不兼容）时放行，避免误伤历史单据；否则要求忽略大小写、去首尾空白后相等。</summary>
    private static bool UnitsCompatible(string? purchaseUnit, string? salesUnit)
    {
        var p = (purchaseUnit ?? string.Empty).Trim();
        var s = (salesUnit ?? string.Empty).Trim();
        if (p.Length == 0 || s.Length == 0) return true;
        return p.Equals(s, StringComparison.OrdinalIgnoreCase);
    }

    private static string DisplayName(PurchaseOrderDetail line)
        => string.IsNullOrWhiteSpace(line.ProductName) ? line.ProductId.ToString() : line.ProductName;

    private sealed class LinkContext
    {
        public LinkContext(SalesOrder order, BaseCustomer customer)
        {
            Order = order;
            Customer = customer;
        }

        public SalesOrder Order { get; }
        public BaseCustomer Customer { get; }
    }
}
