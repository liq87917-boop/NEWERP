using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ERP.Application.Services;

/// <summary>
/// 采购入库单实时授权与数据范围护栏（ERP-352）。
/// <para>读取 / 创建 / 修改 / 提交 / 审核 / 取消 / 删除 / 流水每一路由都重新校验当前身份
/// （缺失 / 禁用 / 未映射一律 fail closed）、采购入库（stock-in）菜单授权与供应商 / 仓库 / 客户数据范围；
/// 链接采购订单的归属客户不能暴露到当前账号客户范围之外，未链接的合法入库单仅允许既有已映射入库操作员访问。</para>
/// <para>本类只做<b>纯判定与有界只读查询</b>，不落库、不改单据、不改库存 / 流水 / 供应商 / 仓库 / 客户主数据，
/// 也不新增任何权限模型；所有库存写入仍复用既有 <see cref="IInventoryService"/>。</para>
/// </summary>
public static class StockInAuthorizationRules
{
    /// <summary>采购入库模块所需既有菜单编码（与 <c>SeedData.Menus</c> 同源）</summary>
    public const string RequiredMenuCode = "stock-in";

    /// <summary>采购入库模块菜单中文文案（与既有菜单名一致）</summary>
    public const string RequiredMenuText = "采购入库";

    /// <summary>读取 / 选择入库来源所需既有菜单编码（与既有「采购订单」菜单同源，绝不新增权限模型）</summary>
    public const string SourceRequiredMenuCode = "purchase-order";

    /// <summary>采购订单模块菜单中文文案（与既有菜单名一致）</summary>
    public const string SourceRequiredMenuText = "采购订单";

    /// <summary>授权与数据范围口径文案（接口 / 文档同源）</summary>
    public const string RuleText =
        "采购入库单授权与数据范围护栏：读取 / 创建 / 修改 / 提交 / 审核 / 取消 / 删除 / 流水每一路由都会重新校验当前身份" +
        "（缺失 / 禁用 / 未映射一律 fail closed）、采购入库（stock-in）菜单授权与供应商 / 仓库 / 客户数据范围；" +
        "链接采购订单的归属客户不能暴露到当前账号客户范围之外，未链接的合法入库单仅允许既有已映射入库操作员访问；" +
        "创建与修改校验真实可用（存在、未删除、已启用）的供应商与仓库；绝不新增权限模型、绝不把空身份当作管理员，也绝不泄露范围外入库单。";

    /// <summary>模块边界文案（不改变库存成本口径、不改写来源订单或主数据）</summary>
    public const string BoundaryText =
        "本护栏只保护采购入库单生命周期与库存写入：不会改变库存成本口径，不会改写采购订单、供应商、仓库或客户主数据，" +
        "也不会删除历史单据；所有库存增减与冲销仍复用既有 InventoryService（ERP-009 / ERP-025）记账与写流水。";

    /// <summary>
    /// 身份 / 账号状态 / 模块授权三重校验（fail closed）：缺失或非法身份按未认证拒绝，
    /// 禁用账号、无 stock-in 菜单授权按权限不足拒绝，绝不猜测身份或范围。
    /// </summary>
    public static async Task EnsureMenuAuthorizedAsync(IErpDbContext db, long? userId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (userId is null or <= 0)
            throw new BusinessException("请先登录后再访问采购入库", ErrorCodes.Unauthorized);

        var user = await db.SysUsers.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == userId.Value && !u.IsDeleted, ct);
        if (user is null)
            throw new BusinessException("登录账号不存在或已删除，禁止访问采购入库", ErrorCodes.Unauthorized);
        if (user.Status != UserStatus.Enabled)
            throw new BusinessException("登录账号已禁用，禁止访问采购入库（fail closed）", ErrorCodes.Forbidden);

        var menuCodes = await CustomerReceivableReconciliationService.LoadAuthorizedMenuCodesAsync(db, userId.Value);
        if (!menuCodes.Contains(RequiredMenuCode, StringComparer.OrdinalIgnoreCase))
        {
            throw new BusinessException(
                $"当前账号没有「{RequiredMenuText}」（{RequiredMenuCode}）模块授权：拒绝访问采购入库" +
                "（fail closed，不执行任何写入）",
                ErrorCodes.Forbidden);
        }
    }

    /// <summary>
    /// 读取 / 选择入库来源所需授权（fail closed，ERP-375）：先复用采购入库（<c>stock-in</c>）身份 / 账号状态 / 菜单校验，
    /// 再要求当前账号实时具备既有「采购订单」（<c>purchase-order</c>）菜单授权；撤销任一授权后下一次请求立即收敛。
    /// <para>绝不新增用户授权，也不提供匿名 / 管理员降级；每次请求重新解析角色 → 菜单，不缓存。</para>
    /// </summary>
    public static async Task EnsureSourceMenuAuthorizedAsync(IErpDbContext db, long? userId,
        CancellationToken ct = default)
    {
        // 采购入库权限（身份 / 账号状态 / stock-in 菜单）先 fail closed。
        await EnsureMenuAuthorizedAsync(db, userId, ct);

        var menuCodes = await CustomerReceivableReconciliationService.LoadAuthorizedMenuCodesAsync(db, userId!.Value);
        if (!menuCodes.Contains(SourceRequiredMenuCode, StringComparer.OrdinalIgnoreCase))
        {
            throw new BusinessException(
                $"当前账号没有「{SourceRequiredMenuText}」（{SourceRequiredMenuCode}）模块授权：拒绝读取采购入库来源候选" +
                "（fail closed，不返回任何来源证据）",
                ErrorCodes.Forbidden);
        }
    }

    /// <summary>
    /// 创建 / 修改时的完整校验：身份 / 菜单 / 真实可用供应商与仓库 / 来源采购订单归属客户数据范围。
    /// <paramref name="purchaseOrderId"/> 为 null 时按未链接入库单处理（仅允许已映射入库操作员）。
    /// </summary>
    public static async Task EnsureAuthorizedAsync(IErpDbContext db, long? userId, long supplierId,
        long warehouseId, long? purchaseOrderId, CancellationToken ct = default)
    {
        await EnsureMenuAuthorizedAsync(db, userId, ct);
        await EnsureSupplierAvailableAsync(db, supplierId, ct);
        await EnsureWarehouseAvailableAsync(db, warehouseId, ct);

        var owningCustomerId = await ResolveOwningCustomerIdAsync(db, purchaseOrderId, ct);
        await EnsureCustomerScopeAsync(db, userId!.Value, owningCustomerId);
    }

    /// <summary>
    /// 读取 / 提交 / 审核 / 取消 / 删除 / 流水时的归属校验：身份 / 菜单 / 来源采购订单归属客户数据范围。
    /// 已存单据的供应商 / 仓库仅作历史引用，不在此处复核可用性。
    /// </summary>
    public static async Task EnsureAuthorizedAsync(IErpDbContext db, long? userId, StockIn entity,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entity);
        await EnsureMenuAuthorizedAsync(db, userId, ct);

        var owningCustomerId = await ResolveOwningCustomerIdAsync(db, entity.PurchaseOrderId, ct);
        await EnsureCustomerScopeAsync(db, userId!.Value, owningCustomerId);
    }

    /// <summary>
    /// 分页列表作用域：特权账号不过滤；未映射账号 fail closed；已映射账号仅可见
    /// 「链接采购订单归属客户在其客户范围内」的入库单，以及未链接入库单（不泄露范围外客户）。
    /// </summary>
    public static async Task<IQueryable<StockIn>> ApplyScopeAsync(IErpDbContext db,
        IQueryable<StockIn> source, long? userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        await EnsureMenuAuthorizedAsync(db, userId, ct);

        var scope = await SalespersonDataScopeService.ResolveAsync(db, userId!.Value);
        if (scope.IsPrivileged)
            return source;

        if (scope.SalesmanId is null or <= 0)
            throw new BusinessException(
                "当前账号未映射为业务员（入库操作员），不能访问采购入库列表（fail closed）",
                ErrorCodes.Forbidden);

        var allowed = scope.AllowedCustomerIds?.ToList() ?? new List<long>();
        var linkedIds = await db.PurchaseOrders.AsNoTracking()
            .Where(o => !o.IsDeleted && o.OwningCustomerId.HasValue && allowed.Contains(o.OwningCustomerId.Value))
            .Select(o => o.Id)
            .ToListAsync(ct);

        return source.Where(s => !s.PurchaseOrderId.HasValue || linkedIds.Contains(s.PurchaseOrderId.Value));
    }


    /// <summary>按采购订单 Id 解析入库单暴露的归属客户；未链接或来源不存在 / 已删除时为 null。</summary>
    public static async Task<long?> ResolveOwningCustomerIdAsync(IErpDbContext db, long? purchaseOrderId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (purchaseOrderId is null or <= 0)
            return null;

        return await db.PurchaseOrders.AsNoTracking()
            .Where(o => o.Id == purchaseOrderId.Value && !o.IsDeleted)
            .Select(o => (long?)o.OwningCustomerId)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>供应商必须真实可用（存在、未删除、已启用），否则拒绝入库单。</summary>
    public static async Task EnsureSupplierAvailableAsync(IErpDbContext db, long supplierId,
        CancellationToken ct = default)
    {
        var supplier = await db.BaseSuppliers.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == supplierId && !s.IsDeleted, ct);
        if (supplier is null)
            throw BusinessException.NotFound($"供应商（Id={supplierId}）不存在或已删除，不能用于采购入库单");
        if (supplier.Status != 1)
            throw BusinessException.RuleConflict($"供应商「{supplier.SupplierName}」已停用，不能用于采购入库单");
    }

    /// <summary>仓库必须真实可用（存在、未删除、已启用），否则拒绝入库单。</summary>
    public static async Task EnsureWarehouseAvailableAsync(IErpDbContext db, long warehouseId,
        CancellationToken ct = default)
    {
        var warehouse = await db.BaseWarehouses.AsNoTracking()
            .FirstOrDefaultAsync(w => w.Id == warehouseId && !w.IsDeleted, ct);
        if (warehouse is null)
            throw BusinessException.NotFound($"仓库（Id={warehouseId}）不存在或已删除，不能用于采购入库单");
        if (warehouse.Status != 1)
            throw BusinessException.RuleConflict($"仓库「{warehouse.WarehouseName}」已停用，不能用于采购入库单");
    }

    /// <summary>客户数据范围：特权账号放行；未链接入库单仅允许已映射操作员；链接入库单归属客户必须落在范围内。</summary>
    private static async Task EnsureCustomerScopeAsync(IErpDbContext db, long userId, long? owningCustomerId)
    {
        var scope = await SalespersonDataScopeService.ResolveAsync(db, userId);
        if (scope.IsPrivileged)
            return;

        if (owningCustomerId is null or <= 0)
        {
            if (scope.SalesmanId is null or <= 0)
                throw new BusinessException(
                    "当前账号未映射为业务员（入库操作员），不能访问未关联客户的采购入库单（fail closed）",
                    ErrorCodes.Forbidden);
            return;
        }

        if (!scope.AllowsCustomer(owningCustomerId))
            throw new BusinessException(
                "当前账号的客户数据范围不包含该入库单归属客户（fail closed，不泄露范围外入库单）",
                ErrorCodes.Forbidden);
    }

}
