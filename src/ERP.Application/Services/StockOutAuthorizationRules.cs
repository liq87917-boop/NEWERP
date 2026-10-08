using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ERP.Application.Services;

/// <summary>
/// 销售出库单实时授权与数据范围护栏（ERP-370）。
/// <para>读取 / 创建 / 修改 / 提交 / 审核 / 取消 / 删除 / 库存流水每一路由都重新校验当前身份
/// （缺失 / 已删除 / 禁用一律 fail closed）、既有「销售出库」（<c>stock-out</c>）菜单授权与
/// <see cref="SalespersonDataScopeService"/>（ERP-097 唯一权威口径）客户数据范围；列表在计数 / 分页之前
/// 把客户范围下推到数据库，绝不「先查全量再内存过滤」。</para>
/// <para>受限账号未映射到业务员时 fail closed（不泄露任何范围外单据），未关联客户的历史单据不做全局兜底。</para>
/// <para>本类只做<b>纯判定与有界只读查询 + 行锁 SQL 常量</b>：不落库、不改单据 / 明细 / 状态 / 库存 / 流水，
/// 不新增任何表 / 列 / 菜单 / 权限模型，也不新增任何用户授权；所有库存写入仍复用既有
/// <see cref="IInventoryService"/>（ERP-009 / ERP-025），数量护栏仍为 ERP-343，取消护栏仍为 ERP-359 / ERP-367。</para>
/// <para><b>菜单授权口径</b>：账号若已配置「角色 → 菜单」授权，则必须包含既有 <c>stock-out</c> 菜单，否则拒绝；
/// 未配置任何菜单授权时，只有既有特权账号（生产端系统内置 / 超级管理员角色已被授予全部菜单）与未分配任何角色、
/// 仅按员工编码映射的历史业务员账号沿用既有 ERP-097 权威数据范围；已分配角色却没有任何菜单授权（例如刚被撤销
/// 最后一个菜单）同样 fail closed。绝不新增授权、绝不把空身份当作管理员、也绝不退化为全局可见。</para>
/// </summary>
public static class StockOutAuthorizationRules
{
    /// <summary>销售出库模块所需既有菜单编码（与 <c>SeedData.Menus</c> 同源）</summary>
    public const string RequiredMenuCode = "stock-out";

    /// <summary>销售出库模块菜单中文文案（与既有菜单名一致）</summary>
    public const string RequiredMenuText = "销售出库";

    /// <summary>读取 / 选择出库来源所需既有菜单编码（与既有「销售订单」菜单同源，绝不新增权限模型）</summary>
    public const string SourceRequiredMenuCode = "sales-order";

    /// <summary>销售订单模块菜单中文文案（与既有菜单名一致）</summary>
    public const string SourceRequiredMenuText = "销售订单";

    /// <summary>
    /// 来源销售出库单行锁（与销售退货审核 / 销审 / 取消 / 装柜审核共用<b>同一</b>把
    /// <c>UPDLOCK, HOLDLOCK</c>）：把「出库审核」「出库修改 / 状态流转」与「来源取消 / 装柜引用」串行化在同一事务内。
    /// </summary>
    public const string LockStockOutRowSql = ReturnSourceCancellationRules.LockStockOutRowSql;

    /// <summary>授权与数据范围口径文案（接口 / 文档同源）</summary>
    public const string RuleText =
        "销售出库单授权与数据范围护栏：读取 / 创建 / 修改 / 提交 / 审核 / 取消 / 删除 / 库存流水每一路由都会重新校验" +
        "当前身份（缺失 / 已删除 / 禁用一律 fail closed）、既有「销售出库」（stock-out）菜单授权与" +
        "SalespersonDataScopeService（ERP-097 唯一权威口径）客户数据范围；列表在计数 / 分页之前把客户范围下推到数据库；" +
        "受限账号未映射业务员时 fail closed，未关联客户的历史单据不做全局兜底；创建 / 修改同时校验「已存」与「请求」两侧客户，" +
        "被拒绝的调用方绝不消耗单据号、绝不改写任何单据 / 明细 / 库存 / 流水；绝不新增权限模型、绝不把空身份当作管理员。";

    /// <summary>边界文案（不改变库存成本口径、不改写来源订单或主数据）</summary>
    public const string BoundaryText =
        "本护栏只保护销售出库单生命周期与库存写入：不改变移动加权平均成本口径，不改写销售订单 / 客户 / 仓库 / 商品主数据，" +
        "不删除历史单据与库存流水审计证据，也不新增任何表 / 列 / 菜单 / 权限；所有库存增减与冲销仍复用既有 " +
        "InventoryService（ERP-009 / ERP-025）记账与写流水，数量护栏仍为 ERP-343，取消护栏仍为 ERP-359 / ERP-367。";

    /// <summary>未映射业务员的受限账号拒绝文案（fail closed，不泄露任何范围外单据）</summary>
    public const string UnmappedOperatorText =
        "当前账号未映射为业务员（销售出库操作员），不能访问销售出库（fail closed，不泄露任何范围外单据）";

    /// <summary>
    /// 身份 / 账号状态 / 菜单授权三重校验（fail closed）：缺失或非法身份按未认证拒绝，账号不存在 / 已删除按未认证拒绝，
    /// 禁用账号按权限不足拒绝，受限未映射业务员按权限不足拒绝，已配置菜单授权但缺少既有 <c>stock-out</c> 菜单按权限不足拒绝。
    /// 返回解析出的权威数据范围，供调用方在同一请求内复用（绝不缓存）。
    /// </summary>
    public static async Task<SalespersonDataScope> EnsureMenuAuthorizedAsync(IErpDbContext db, long? userId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (userId is null or <= 0)
            throw new BusinessException("请先登录后再访问销售出库", ErrorCodes.Unauthorized);

        var user = await db.SysUsers.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == userId.Value && !u.IsDeleted, ct);
        if (user is null)
            throw new BusinessException("登录账号不存在或已删除，禁止访问销售出库", ErrorCodes.Unauthorized);
        if (user.Status != UserStatus.Enabled)
            throw new BusinessException("登录账号已禁用，禁止访问销售出库（fail closed）", ErrorCodes.Forbidden);

        // 每次请求重新解析（绝不缓存）：授权 / 员工 / 客户分配变更后下一次请求立即收敛。
        var scope = await SalespersonDataScopeService.ResolveAsync(db, userId.Value);
        if (!scope.IsPrivileged && scope.SalesmanId is null)
            throw new BusinessException(UnmappedOperatorText, ErrorCodes.Forbidden);

        var menuCodes = await CustomerReceivableReconciliationService.LoadAuthorizedMenuCodesAsync(db, userId.Value);
        if (menuCodes.Count > 0 && !menuCodes.Contains(RequiredMenuCode, StringComparer.OrdinalIgnoreCase))
            throw MenuDenied();

        // 未配置任何菜单授权时：只有既有特权账号（生产端系统内置 / 超级管理员角色已被授予全部菜单）与
        // 未分配任何角色、仅按员工编码映射的历史业务员账号沿用既有 ERP-097 权威数据范围；
        // 其余（已分配角色却没有任何「角色 → 菜单」授权，例如刚被撤销最后一个菜单）一律 fail closed。
        if (menuCodes.Count == 0 && !scope.IsPrivileged)
        {
            var hasAnyRole = await db.SysUserRoles.AsNoTracking()
                .AnyAsync(ur => ur.UserId == userId.Value && !ur.IsDeleted, ct);
            if (hasAnyRole) throw MenuDenied();
        }

        return scope;
    }

    /// <summary>
    /// 读取 / 选择出库来源所需授权（fail closed，ERP-376）：先复用销售出库（<c>stock-out</c>）身份 / 账号状态 / 菜单校验，
    /// 再要求当前账号实时具备既有「销售订单」（<c>sales-order</c>）菜单授权；撤销任一授权后下一次请求立即收敛。
    /// <para>绝不新增用户授权，也不提供匿名 / 管理员降级；每次请求重新解析角色 → 菜单，不缓存。
    /// 返回解析出的权威数据范围，供调用方在同一请求内复用（绝不缓存）。</para>
    /// </summary>
    public static async Task<SalespersonDataScope> EnsureSourceMenuAuthorizedAsync(IErpDbContext db, long? userId,
        CancellationToken ct = default)
    {
        // 销售出库权限（身份 / 账号状态 / stock-out 菜单 + 权威数据范围）先 fail closed。
        var scope = await EnsureMenuAuthorizedAsync(db, userId, ct);

        var menuCodes = await CustomerReceivableReconciliationService.LoadAuthorizedMenuCodesAsync(db, userId!.Value);
        if (!menuCodes.Contains(SourceRequiredMenuCode, StringComparer.OrdinalIgnoreCase))
        {
            throw new BusinessException(
                $"当前账号没有「{SourceRequiredMenuText}」（{SourceRequiredMenuCode}）模块授权：拒绝读取销售出库来源候选" +
                "（fail closed，不返回任何来源证据）",
                ErrorCodes.Forbidden);
        }

        return scope;
    }

    /// <summary>缺少既有「销售出库」菜单授权时的拒绝（fail closed，不执行任何写入）。</summary>
    private static BusinessException MenuDenied()
        => new($"当前账号没有「{RequiredMenuText}」（{RequiredMenuCode}）模块授权：拒绝访问销售出库（fail closed，不执行任何写入）",
            ErrorCodes.Forbidden);

    /// <summary>
    /// 单据级授权：身份 / 菜单（<see cref="EnsureMenuAuthorizedAsync"/>）+ 权威客户
    /// <paramref name="customerId"/> 必须落在当前账号客户数据范围之内。范围外按「不存在」拒绝（不泄露归属）。
    /// </summary>
    public static async Task EnsureCustomerAuthorizedAsync(IErpDbContext db, long? userId, long? customerId,
        CancellationToken ct = default)
    {
        var scope = await EnsureMenuAuthorizedAsync(db, userId, ct);
        if (!scope.AllowsCustomer(customerId))
            throw BusinessException.NotFound("出库单不存在");
    }

    /// <summary>
    /// 列表作用域下推：身份 / 菜单 fail closed 后，把权威客户范围下推到数据库（计数 / 分页之前）。
    /// 特权账号不过滤；受限账号只返回本人客户的出库单，绝不「先查全量再内存过滤」。
    /// </summary>
    public static async Task<IQueryable<StockOut>> ApplyScopeAsync(IErpDbContext db, IQueryable<StockOut> source,
        long? userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        var scope = await EnsureMenuAuthorizedAsync(db, userId, ct);
        return SalespersonDataScopeService.FilterByCustomer(source, scope, o => o.CustomerId);
    }
}
