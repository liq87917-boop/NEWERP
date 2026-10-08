using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ERP.Application.Services;

/// <summary>
/// 规范销售订单（<c>api/sales-orders</c>）**执行证据入口**的实时授权与权威客户范围护栏（ERP-413）。
/// <para>背景：时间线（<c>timeline</c>）、财务核对（<c>finance-reconciliation</c>）、出货与收款进度（<c>progress</c>）、
/// 退货影响（<c>return-impact</c>）以及收款引用 / 销项发票证据（<c>receipt-evidence</c> / <c>invoice-evidence</c>
/// 及其列表批量汇总 <c>*-summaries</c>）此前直接派生，不解析实时身份 / 既有菜单 / 客户范围，任何已登录账号只要
/// 猜到一张**范围外**订单 Id 即可读取其出运、退货、收款、发票与财务证据。</para>
/// <list type="number">
/// <item><b>实时身份</b>：<c>userId</c> 缺失 / 非法按未认证拒绝，账号不存在 / 已删除按未认证拒绝，已禁用按权限不足拒绝；
/// 绝不把空身份当作匿名或管理员，也不缓存（每次请求重新查询）。</item>
/// <item><b>既有菜单授权</b>：普通账号必须实时具备既有「销售订单」（<c>sales-order</c>）功能菜单
/// （导出专用菜单 <c>sales-order-export</c> 绝不当作模块权限）；特权账号（超级管理员 / 系统内置角色 / 显式特权角色）
/// 沿用既有全部访问口径，但仍须通过实时身份校验。绝不新增任何菜单 / 角色 / 用户授权。</item>
/// <item><b>权威客户范围</b>：客户数据范围只复用唯一权威口径 <see cref="SalespersonDataScopeService"/>（ERP-097），
/// 并严格按**持久化 <see cref="SalesOrder.CustomerId"/></b> 判定归属 —— 绝不按单号文本、金额或相似度推断，
/// 也绝不把旧库 <c>Oid</c> 推断为规范 <c>Id</c>。</item>
/// <item><b>非披露错误</b>：范围外订单、已删除订单与不存在订单返回**同一**「销售订单不存在」受控错误
/// （既不在授权前读取任何证据，也不返回计数或部分行透露不可访问订单的存在性）。</item>
/// </list>
/// <para><b>边界</b>：本类只做纯判定与有界只读查询，不落库、不改写销售订单 / 出库 / 退货 / 收款 / 发票 / 财务 / 库存记录，
/// 不新增表 / 列 / 索引 / 菜单 / 权限模型，也不改变既有派生口径（数量 / 单价 / 金额 / 币种 / 单位 / null-as-unknown）。
/// 调用方（控制器）负责在任何证据派生之前调用。</para>
/// </summary>
public static class SalesOrderExecutionAuthorizationRules
{
    /// <summary>规范销售订单模块复用的既有菜单编码（与 <c>SeedData.Menus</c> 同源）。</summary>
    public const string RequiredMenuCode = "sales-order";

    /// <summary>销售订单模块菜单中文文案（与既有菜单名一致）。</summary>
    public const string RequiredMenuText = "销售订单";

    /// <summary>无身份 / 非法身份的拒绝文案。</summary>
    public const string UnauthorizedText = "请先登录后再访问销售订单执行证据";

    /// <summary>账号不存在 / 已删除的拒绝文案。</summary>
    public const string UserDeletedText = "登录账号不存在或已删除，禁止访问销售订单执行证据";

    /// <summary>账号已禁用的拒绝文案。</summary>
    public const string UserDisabledText = "登录账号已禁用，禁止访问销售订单执行证据（fail closed）";

    /// <summary>缺少既有「销售订单」菜单授权时的拒绝文案（导出菜单绝不替代功能菜单）。</summary>
    public const string MenuDeniedText =
        "当前账号没有「销售订单」（sales-order）模块授权：拒绝读取销售订单执行证据"
        + "（fail closed，不返回任何时间线 / 进度 / 退货 / 收款 / 发票 / 财务证据）";

    /// <summary>
    /// 范围外 / 已删除 / 不存在订单的统一非披露错误文案（与既有 <c>api/sales-orders/{id}</c> 详情口径一致）：
    /// 三者返回同一错误，绝不通过差异化的错误 / 计数 / 部分行透露不可访问订单的存在性。
    /// </summary>
    public const string NotFoundText = "销售订单不存在";

    /// <summary>授权与数据范围口径文案（接口 / 文档同源）。</summary>
    public const string RuleText =
        "销售订单执行证据护栏：时间线 / 财务核对 / 出货与收款进度 / 退货影响 / 收款引用证据 / 销项发票证据"
        + "（含列表批量汇总）在派生任何证据之前，都重新校验实时身份（缺失 / 非法 / 已删除按未认证拒绝，已禁用按权限不足拒绝）、"
        + "既有「销售订单」（sales-order）功能菜单授权与 SalespersonDataScopeService（ERP-097 唯一权威口径）客户数据范围；"
        + "归属严格按持久化 CustomerId 判定；范围外 / 已删除 / 不存在订单返回同一受控「销售订单不存在」错误，"
        + "批量显式 Id 混入任何不可访问订单即**整批拒绝**，绝不返回计数或部分行；"
        + "绝不新增权限模型、绝不把空身份当作管理员，也绝不把旧库 Oid 推断为规范 Id。";

    /// <summary>边界文案（不改变既有派生口径、不改写任何业务 / 库存 / 财务记录）。</summary>
    public const string BoundaryText =
        "本护栏只保护规范销售订单执行证据入口：不改写数量 / 单价 / 金额 / 合计 / 定金 / 币种 / 汇率 / 单位等原始商业语义，"
        + "null-as-unknown 与响应 DTO 契约保持不变；不改写销售订单 / 出库 / 退货 / 收款 / 发票 / 财务 / 客户主数据，"
        + "不删除历史证据与审计留痕，也不新增任何表 / 列 / 索引 / 菜单 / 权限或用户授权。";

    /// <summary>
    /// 执行证据入口的完整授权（实时身份 + 既有「销售订单」菜单 + 权威客户数据范围）。
    /// 特权账号豁免菜单校验但仍须通过实时身份校验；每次调用都重新查询（无缓存），授权撤销 / 账号停用后立即收敛。
    /// </summary>
    public static async Task<SalespersonDataScope> EnsureReadAuthorizedAsync(
        IErpDbContext db, long? userId, CancellationToken ct = default)
    {
        var scope = await EnsureLiveIdentityAsync(db, userId, ct);
        if (scope.IsPrivileged) return scope;

        var menuCodes = await CustomerReceivableReconciliationService
            .LoadAuthorizedMenuCodesAsync(db, userId!.Value);
        if (!menuCodes.Contains(RequiredMenuCode, StringComparer.OrdinalIgnoreCase))
            throw new BusinessException(MenuDeniedText, ErrorCodes.Forbidden);

        return scope;
    }

    /// <summary>
    /// 列表 / 详情入口的实时身份校验（fail closed）：缺失 / 非法身份按未认证拒绝，账号不存在 / 已删除按未认证拒绝，
    /// 已禁用按权限不足拒绝；随后复用 <see cref="SalespersonDataScopeService"/>（ERP-097）解析权威客户范围。
    /// 普通账号仍须显式具备既有「销售订单」菜单 —— 该要求由执行证据入口
    /// <see cref="EnsureReadAuthorizedAsync"/> 施加，本方法只解析实时身份与范围。
    /// </summary>
    public static async Task<SalespersonDataScope> EnsureLiveIdentityAsync(
        IErpDbContext db, long? userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ct.ThrowIfCancellationRequested();

        if (userId is null or <= 0)
            throw new BusinessException(UnauthorizedText, ErrorCodes.Unauthorized);

        var user = await db.SysUsers.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == userId.Value && !u.IsDeleted, ct);
        if (user is null)
            throw new BusinessException(UserDeletedText, ErrorCodes.Unauthorized);
        if (user.Status != UserStatus.Enabled)
            throw new BusinessException(UserDisabledText, ErrorCodes.Forbidden);

        return await SalespersonDataScopeService.ResolveAsync(db, userId.Value);
    }

    /// <summary>
    /// 单张销售订单的权威归属复核：按 <c>Id</c> 精确读取**持久化 <see cref="SalesOrder.CustomerId"/>**（最小投影、
    /// 有界只读，绝不装载任何证据行）；不存在 / 已删除 / 范围外一律返回同一非披露错误。
    /// </summary>
    public static async Task<long> EnsureOrderAllowedAsync(
        IErpDbContext db, SalespersonDataScope scope, long orderId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(scope);

        if (orderId <= 0)
            throw BusinessException.NotFound(NotFoundText);

        var customerId = await db.SalesOrders.AsNoTracking()
            .Where(o => o.Id == orderId && !o.IsDeleted)
            .Select(o => (long?)o.CustomerId)
            .FirstOrDefaultAsync(ct);

        if (customerId is null || !scope.AllowsCustomer(customerId.Value))
            throw BusinessException.NotFound(NotFoundText);

        return customerId.Value;
    }

    /// <summary>
    /// 一批显式销售订单 Id 的权威归属复核（列表批量证据汇总入口）：去重后按 Id 有界读取最小归属投影；
    /// 只要**任一** Id 不存在 / 已删除 / 范围外，即**整批**返回同一非披露错误 —— 绝不派生任何部分行或计数，
    /// 绝不透露哪些 Id 不可访问。空集合视为「未请求任何订单」，直接放行。
    /// </summary>
    public static async Task EnsureOrdersAllowedAsync(
        IErpDbContext db, SalespersonDataScope scope, IEnumerable<long> orderIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(orderIds);

        var ids = orderIds.Where(id => id > 0).Distinct().ToList();
        if (ids.Count == 0) return;

        var rows = await db.SalesOrders.AsNoTracking()
            .Where(o => ids.Contains(o.Id) && !o.IsDeleted)
            .Select(o => new { o.Id, o.CustomerId })
            .ToListAsync(ct);

        if (rows.Count != ids.Count || rows.Any(r => !scope.AllowsCustomer(r.CustomerId)))
            throw BusinessException.NotFound(NotFoundText);
    }
}
