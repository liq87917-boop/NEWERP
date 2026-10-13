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

    // ==================== ERP-502：草稿写入护栏（形状 + 实际存储精度 + 在用主数据） ====================

    /// <summary>
    /// 出库数量 / 毛重 / 体积与表头合计的实际存储精度：<c>DECIMAL(18,2)</c>。
    /// <para>与 <c>ErpDbContext</c> 对 <c>StockOutDetail.Quantity</c> / <c>Weight</c> / <c>Volume</c> 与
    /// <c>StockOut.TotalQuantity</c> / <c>TotalWeight</c> / <c>TotalVolume</c> 的 EF 默认精度（未显式声明即
    /// <c>DECIMAL(18,2)</c>）同源；校验必须使用<b>同一份</b>精度口径，否则超精度值会被落库静默取整。</para>
    /// </summary>
    public const int QuantityPrecision = 18;

    /// <summary>出库数量 / 毛重 / 体积与表头合计的实际小数位：2（与 <see cref="QuantityPrecision"/> 同源）。</summary>
    public const int QuantityScale = 2;

    /// <summary><c>DECIMAL(18,2)</c> 的最大可表示绝对值（16 位整数 + 2 位小数）。</summary>
    public const decimal MaxDecimal18Scale2 = 9_999_999_999_999_999.99m;

    /// <summary>
    /// 草稿写入护栏口径文案（接口 / 文档同源，ERP-502）：创建 / 修改在单位折算之后、单号预约与任何写入<b>之前</b>
    /// 校验仓库在用（正数 Id、存在、未删除、启用）与每一行有效明细（正数且在存储精度内可表示的数量、
    /// 非负且可表示的毛重 / 体积、商品存在 / 未删除 / 启用），并按同一精度口径校验表头合计（含溢出）。
    /// </summary>
    public const string DraftGuardRuleText =
        "销售出库单创建 / 修改在单位折算之后、单号预约与任何写入之前执行统一草稿护栏：仓库 Id 必须为正数且仓库存在、未删除、已启用；" +
        "每一行有效明细的数量必须为正数、毛重与体积不得为负，且与表头合计一并落在实际存储精度（DECIMAL(18,2)）内（含溢出）；" +
        "商品 Id 必须有效且商品存在、未删除、已启用；空草稿头（0 行有效明细）仍受支持（提交 / 审核要求至少一行有效明细），" +
        "重复商品行按累计数量语义保留，绝不引入任意唯一性规则；被拒绝的请求零单号消耗、零单据 / 明细 / 库存 / 流水变化，" +
        "绝不新增表 / 列 / 索引 / 菜单 / 权限。";

    /// <summary>出库数量 / 毛重 / 体积是否在实际存储精度 <c>DECIMAL(18,2)</c> 内可表示（最多 2 位小数且绝对值有界）。</summary>
    public static bool IsQuantityRepresentable(decimal value)
        => decimal.Round(value, QuantityScale) == value
           && value >= -MaxDecimal18Scale2 && value <= MaxDecimal18Scale2;

    /// <summary>
    /// 草稿明细形状校验（纯内存，不访问数据库）：只校验<b>未删除</b>的有效明细行 ——
    /// 商品 Id 有效、数量为正且在 <c>DECIMAL(18,2)</c> 内可表示、毛重与体积非负且可表示；
    /// 随后按与 <c>StockOutController.Calculate</c> <b>同源</b>的公式重算表头合计数量 / 毛重 / 体积并校验可表示性与溢出。
    /// <para>刻意<b>不</b>校验商品唯一性：同一商品允许在一张出库单中重复出现（累计数量语义），不引入任何任意唯一性规则。</para>
    /// <para>空明细（0 行有效明细）在草稿阶段仍受支持；是否要求非空由调用方（提交 / 审核）决定。</para>
    /// </summary>
    public static void EnsureLineShape(IEnumerable<StockOutDetail> details)
    {
        ArgumentNullException.ThrowIfNull(details);

        var effective = details.Where(d => !d.IsDeleted).ToList();
        var line = 0;
        foreach (var d in effective)
        {
            line++;
            var label = string.IsNullOrWhiteSpace(d.ProductName) ? $"第 {line} 行" : d.ProductName;

            if (d.ProductId <= 0)
                throw BusinessException.InvalidParameter($"出库明细 [{label}] 缺少有效商品 Id，不能出库");
            if (d.Quantity <= 0m)
                throw BusinessException.InvalidParameter($"商品 [{label}] 的出库数量必须大于 0");
            if (!IsQuantityRepresentable(d.Quantity))
                throw BusinessException.InvalidParameter(
                    $"商品 [{label}] 的出库数量 {d.Quantity} 超出存储精度（DECIMAL(18,2)）：已拒绝，绝不静默截断或取整");
            if (d.Weight < 0m)
                throw BusinessException.InvalidParameter($"商品 [{label}] 的毛重不能为负数");
            if (!IsQuantityRepresentable(d.Weight))
                throw BusinessException.InvalidParameter(
                    $"商品 [{label}] 的毛重 {d.Weight} 超出存储精度（DECIMAL(18,2)）：已拒绝，绝不静默截断或取整");
            if (d.Volume < 0m)
                throw BusinessException.InvalidParameter($"商品 [{label}] 的体积不能为负数");
            if (!IsQuantityRepresentable(d.Volume))
                throw BusinessException.InvalidParameter(
                    $"商品 [{label}] 的体积 {d.Volume} 超出存储精度（DECIMAL(18,2)）：已拒绝，绝不静默截断或取整");
        }

        EnsureRepresentableTotals(effective);
    }

    /// <summary>
    /// 表头合计（数量 / 毛重 / 体积）的 checked 精度校验：与 <c>StockOutController.Calculate</c> 同源重算，
    /// 任一不可表示 / 溢出都返回受控业务错误。通过本校验后 <c>Calculate</c> 的结果可被 EF <b>原样</b>持久化，
    /// 明细之和与表头合计逐分一致（不存在落库取整差），也不会把正数量静默舍入为 0。
    /// </summary>
    private static void EnsureRepresentableTotals(IReadOnlyList<StockOutDetail> details)
    {
        try
        {
            decimal totalQuantity = 0m, totalWeight = 0m, totalVolume = 0m;
            foreach (var d in details)
            {
                totalQuantity += d.Quantity;
                totalWeight += d.Weight;
                totalVolume += d.Volume;
            }

            AssertTotalRepresentable("合计数量", totalQuantity);
            AssertTotalRepresentable("合计毛重", totalWeight);
            AssertTotalRepresentable("合计体积", totalVolume);
        }
        catch (OverflowException)
        {
            // decimal 加法溢出：转换为受控业务错误，绝不落库为截断值。
            throw BusinessException.InvalidParameter(
                "出库数量 / 毛重 / 体积超出可存储范围（DECIMAL(18,2)）：已拒绝，绝不静默截断或取整");
        }
    }

    private static void AssertTotalRepresentable(string name, decimal value)
    {
        if (!IsQuantityRepresentable(value))
            throw BusinessException.InvalidParameter(
                $"出库单{name} {value} 超出存储精度（DECIMAL(18,2)）：已拒绝，绝不静默截断或取整");
    }

    /// <summary>仓库必须真实可用（存在、未删除、已启用），否则拒绝销售出库单草稿写入。</summary>
    public static async Task EnsureWarehouseAvailableAsync(IErpDbContext db, long warehouseId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (warehouseId <= 0)
            throw BusinessException.InvalidParameter("销售出库单必须选择有效仓库（仓库 Id 必须为正数）");

        var warehouse = await db.BaseWarehouses.AsNoTracking()
            .FirstOrDefaultAsync(w => w.Id == warehouseId && !w.IsDeleted, ct);
        if (warehouse is null)
            throw BusinessException.InvalidParameter($"仓库（Id={warehouseId}）不存在或已删除，不能用于销售出库单");
        if (warehouse.Status != 1)
            throw BusinessException.RuleConflict($"仓库「{warehouse.WarehouseName}」已停用，不能用于销售出库单");
    }

    /// <summary>每一行有效明细的商品必须真实可用（存在、未删除、已启用），否则拒绝销售出库单草稿写入。</summary>
    public static async Task EnsureProductsAvailableAsync(IErpDbContext db, IEnumerable<StockOutDetail> details,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(details);

        var effective = details.Where(d => !d.IsDeleted).ToList();
        var ids = effective.Select(d => d.ProductId).Distinct().ToList();
        var products = ids.Count == 0
            ? new Dictionary<long, BaseProduct>()
            : await db.BaseProducts.AsNoTracking()
                .Where(p => ids.Contains(p.Id) && !p.IsDeleted)
                .ToDictionaryAsync(p => p.Id, ct);

        var line = 0;
        foreach (var d in effective)
        {
            line++;
            var label = string.IsNullOrWhiteSpace(d.ProductName) ? $"第 {line} 行" : d.ProductName;

            if (!products.TryGetValue(d.ProductId, out var product))
                throw BusinessException.InvalidParameter($"商品 [{label}] 不存在或已删除，不能出库");
            if (product.Status != 1)
                throw BusinessException.RuleConflict($"商品「{product.ProductName}」已停用，不能出库（fail closed）");
        }
    }

    /// <summary>
    /// ERP-502 共享草稿护栏（创建 / 修改在单位折算之后、单号预约与任何写入之前调用）：
    /// <see cref="EnsureLineShape"/>（形状 + 实际存储精度 + 表头合计）→ <see cref="EnsureWarehouseAvailableAsync"/> →
    /// <see cref="EnsureProductsAvailableAsync"/>。任一不满足即按既有受控业务错误
    /// （<c>InvalidParameter</c> / <c>RuleConflict</c>）拒绝，绝不消耗单据号、绝不落任何单据 / 明细 / 库存 / 流水。
    /// </summary>
    public static async Task EnsureDraftValidAsync(IErpDbContext db, long warehouseId,
        IEnumerable<StockOutDetail> details, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(details);

        EnsureLineShape(details);
        await EnsureWarehouseAvailableAsync(db, warehouseId, ct);
        await EnsureProductsAvailableAsync(db, details, ct);
    }

    /// <summary>提交 / 审核前必须至少存在一行有效明细（未删除）；空草稿头可保存但不可提交 / 审核。</summary>
    public static void EnsureHasEffectiveLine(IEnumerable<StockOutDetail> details, string action)
    {
        ArgumentNullException.ThrowIfNull(details);
        if (!details.Any(d => !d.IsDeleted))
            throw BusinessException.RuleConflict($"出库单无明细，不能{action}");
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
