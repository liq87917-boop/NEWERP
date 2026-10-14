using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ERP.Application.Services;

/// <summary>
/// 采购入库单实时授权与数据范围护栏（ERP-352）。
/// <para>读取 / 创建 / 修改 / 提交 / 审核 / 取消 / 删除 / 流水每一路由都<b>先</b>执行统一实时闸门
/// （缺失 / 已删除身份按未认证拒绝，禁用 / 未映射业务员 / 未映射「采购入库」（<c>stock-in</c>）菜单按权限不足拒绝），
/// 闸门<b>先于任何入库单 / 来源 / 流水字节读取与任何行锁</b>，再校验供应商 / 仓库可用性与客户数据范围；
/// 链接采购订单的归属客户不能暴露到当前账号客户范围之外，未链接的合法入库单仅允许既有已映射入库操作员访问。</para>
/// <para><b>非披露</b>：被拒绝的调用方对「已存在」与「不存在」的 Id 得到同一受控错误；已授权调用方对范围外 /
/// 已删除 / 归属未知的单据与「不存在」得到同一非披露错误（<see cref="NotFoundText"/>），
/// 分页列表 / 详情 / 流水 / 生命周期可见性完全同口径，绝不泄露范围外单据标识与归属客户。</para>
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

    /// <summary>
    /// 受限账号未映射为业务员（入库操作员）时的拒绝文案（fail closed）：在统一闸门内先于任何单据字节读取返回，
    /// 因此未映射账号无从区分「已存在」与「不存在」的入库单。
    /// </summary>
    public const string UnmappedOperatorText =
        "当前账号未映射为业务员（入库操作员），不能访问采购入库（fail closed，不泄露任何范围外单据）";

    /// <summary>
    /// 不存在 / 已删除 / 范围外入库单的统一非披露错误文案（读取与生命周期统一口径）：
    /// 三者返回<b>同一</b>错误，绝不通过差异化错误 / 计数 / 部分行透露不可访问单据的存在性与归属。
    /// </summary>
    public const string NotFoundText = "入库单不存在";

    /// <summary>
    /// 请求侧来源（创建 / 修改提交的采购订单链接）不在当前账号客户数据范围时的非披露文案：
    /// 复用既有 <see cref="StockInOrderFulfillmentRules.SourceNotFoundText"/> 口径（不存在 / 已删除 / 范围外同一文案），
    /// 绝不透露范围外来源的归属客户。
    /// </summary>
    public const string SourceScopeDeniedText = StockInOrderFulfillmentRules.SourceNotFoundText;

    /// <summary>授权与数据范围口径文案（接口 / 文档同源）</summary>
    public const string RuleText =
        "采购入库单授权与数据范围护栏：读取 / 创建 / 修改 / 提交 / 审核 / 取消 / 删除 / 流水每一路由都会重新校验当前身份" +
        "（缺失 / 禁用 / 未映射一律 fail closed）、采购入库（stock-in）菜单授权与供应商 / 仓库 / 客户数据范围；" +
        "链接采购订单的归属客户不能暴露到当前账号客户范围之外，未链接的合法入库单仅允许既有已映射入库操作员访问；" +
        "创建与修改校验真实可用（存在、未删除、已启用）的供应商与仓库；绝不新增权限模型、绝不把空身份当作管理员，也绝不泄露范围外入库单。";

    /// <summary>
    /// ERP-352 路径无关 / 闸门先行的契约文案：实时身份 / 账号状态 / 业务员映射 / 既有「采购入库」菜单的闸门
    /// <b>无条件</b>先于任何入库单 / 来源 / 流水字节读取与任何行锁执行，与 <c>Request.Path</c> 是否赋值、
    /// 请求形状以及是否绑定 <c>HttpContext</c> 完全无关；缺失 / 零 / 非数字 / 已删除身份一律未认证（2000），
    /// 禁用 / 未映射 / 缺失或撤销既有菜单一律权限不足（2002），被拒绝的调用方对「已存在」与「不存在」的 Id
    /// 得到同一受控错误；已授权调用方对范围外与不存在的单据得到同一非披露错误（<see cref="NotFoundText"/>）。
    /// 不存在 <c>Request.Path</c> / 环境变量 / 数据库提供程序 / 测试专用开关旁路，也绝无匿名、管理员或
    /// 「无请求上下文」回退。
    /// </summary>
    public const string PathIndependenceText =
        "采购入库实时授权无条件执行：身份 / 账号状态 / 业务员映射 / 既有「采购入库」（stock-in）菜单闸门" +
        "先于任何入库单 / 来源 / 流水字节读取与任何行锁，与请求路径 / 请求形状 / 是否绑定 HttpContext 完全无关；" +
        "缺失 / 零 / 非法 / 已删除身份按未认证拒绝（2000），禁用 / 未映射 / 无菜单按权限不足拒绝（2002），" +
        "已授权调用方的范围外与不存在单据共用同一非披露错误（入库单不存在）；" +
        "不存在任何免授权回退或测试专用开关，绝不把空身份当作管理员。";

    /// <summary>模块边界文案（不改变库存成本口径、不改写来源订单或主数据）</summary>
    public const string BoundaryText =
        "本护栏只保护采购入库单生命周期与库存写入：不会改变库存成本口径，不会改写采购订单、供应商、仓库或客户主数据，" +
        "也不会删除历史单据；所有库存增减与冲销仍复用既有 InventoryService（ERP-009 / ERP-025）记账与写流水。";

    /// <summary>
    /// 身份 / 账号状态 / 业务员映射 / 模块授权的统一实时闸门（fail closed）：缺失或非法身份按未认证拒绝，
    /// 账号不存在 / 已删除按未认证拒绝，禁用账号 / 受限未映射操作员 / 无 stock-in 菜单授权按权限不足拒绝。
    /// <para><b>每一条</b>读取 / 创建 / 修改 / 提交 / 审核 / 取消 / 删除 / 流水入口都必须在任何入库单 / 来源 / 流水
    /// 字节读取以及任何行锁<b>之前</b>先调用本方法；本方法只读取账号 / 角色 / 菜单与权威数据范围，绝不读取单据字节，
    /// 因此缺失 / 禁用 / 未映射 / 无菜单调用方对「已存在」与「不存在」的 Id 得到<b>完全相同</b>的受控错误，
    /// 无从探测单据存在性。</para>
    /// <para>缺少请求上下文 / 缺少身份声明 / 非数字 / 非正数一律安全解析为 <c>null</c> 并由本方法先返回受控未认证；
    /// <b>不存在</b>「无请求上下文 / 空路径 / 匿名进程内调用」的免授权回退，也不读取 <c>Request.Path</c>、环境变量或测试开关。
    /// 返回解析出的权威数据范围，供调用方在同一请求内复用于范围判定（每次调用重新解析，绝不缓存）。</para>
    /// </summary>
    public static async Task<SalespersonDataScope> EnsureMenuAuthorizedAsync(IErpDbContext db, long? userId,
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

        // 每次请求重新解析（绝不缓存）：角色 / 员工映射 / 客户分配变更后下一次请求立即收敛。
        var scope = await SalespersonDataScopeService.ResolveAsync(db, userId.Value);
        if (!scope.IsPrivileged && scope.SalesmanId is null or <= 0)
            throw new BusinessException(UnmappedOperatorText, ErrorCodes.Forbidden);

        var menuCodes = await CustomerReceivableReconciliationService.LoadAuthorizedMenuCodesAsync(db, userId.Value);
        if (!menuCodes.Contains(RequiredMenuCode, StringComparer.OrdinalIgnoreCase))
        {
            throw new BusinessException(
                $"当前账号没有「{RequiredMenuText}」（{RequiredMenuCode}）模块授权：拒绝访问采购入库" +
                "（fail closed，不执行任何写入）",
                ErrorCodes.Forbidden);
        }

        return scope;
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
    /// 创建 / 修改（请求侧）的完整校验：统一闸门（身份 / 账号状态 / 业务员映射 / 菜单，先于任何入库单字节读取）
    /// → 真实可用（存在、未删除、已启用）的供应商与仓库 → 来源采购订单归属客户数据范围。
    /// <paramref name="purchaseOrderId"/> 为 null / 非正数时按未链接入库单处理（仅允许已映射入库操作员）。
    /// <para>范围外 / 已删除 / 无法解析归属的来源一律按「来源不存在、已删除或不在当前账号数据范围内」拒绝
    /// （<see cref="SourceScopeDeniedText"/>，不泄露范围外来源归属），被拒绝的调用方绝不消耗单据号、绝不写入。</para>
    /// </summary>
    public static async Task EnsureAuthorizedAsync(IErpDbContext db, long? userId, long supplierId,
        long warehouseId, long? purchaseOrderId, CancellationToken ct = default)
    {
        var scope = await EnsureMenuAuthorizedAsync(db, userId, ct);
        await EnsureSupplierAvailableAsync(db, supplierId, ct);
        await EnsureWarehouseAvailableAsync(db, warehouseId, ct);

        // 显式来源必须落在当前账号客户数据范围内：受限账号对「范围外 / 已删除 / 归属未知」来源
        // 与「不存在」来源给出同一非披露拒绝（特权账号 AllowsCustomer 恒为 true，不受影响）。
        if (purchaseOrderId is > 0)
        {
            var owningCustomerId = await ResolveOwningCustomerIdAsync(db, purchaseOrderId, ct);
            if (!scope.AllowsCustomer(owningCustomerId))
                throw BusinessException.NotFound(SourceScopeDeniedText);
        }
    }

    /// <summary>
    /// 已存单据的生命周期 / 读取归属校验：统一闸门（身份 / 账号状态 / 业务员映射 / 菜单，先于任何入库单字节读取）
    /// + 来源采购订单归属客户可见性。范围外 / 已删除 / 归属未知与「不存在」返回<b>同一</b>非披露错误
    /// （<see cref="NotFoundText"/>），绝不透露范围外单据的存在性与归属。
    /// <para>已存单据的供应商 / 仓库仅作历史引用，不在此处复核可用性；本方法只做有界只读判定，不落库。</para>
    /// </summary>
    public static async Task EnsureAuthorizedAsync(IErpDbContext db, long? userId, StockIn entity,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entity);
        var scope = await EnsureMenuAuthorizedAsync(db, userId, ct);
        await EnsureReceiptVisibleAsync(db, scope, entity, ct);
    }

    /// <summary>
    /// 入库单可见性判定（非披露）：特权账号放行；受限账号仅当「未链接」或「来源订单归属客户在范围内」时可见。
    /// 与 <see cref="ApplyScopeAsync"/> 的列表谓词<b>完全同口径</b>（链接来源已删除 / 归属未知同样不可见），
    /// 保证分页列表与详情 / 流水 / 生命周期一致，绝不「列表隐藏、详情可读」。
    /// </summary>
    public static async Task<bool> IsReceiptVisibleAsync(IErpDbContext db, SalespersonDataScope scope,
        StockIn entity, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(entity);
        return await IsReceiptVisibleAsync(db, scope, entity.PurchaseOrderId, ct);
    }

    /// <summary>
    /// 入库单可见性判定（按持久化来源 Id 的最小投影重载，供只读探测 / 生命周期前置校验复用）。
    /// 未链接（null / 非正数）对已映射操作员可见（未映射已由统一闸门 fail closed）；
    /// 链接来源的归属客户必须精确落在权威客户范围内，归属未知 / 已删除一律不可见。
    /// </summary>
    public static async Task<bool> IsReceiptVisibleAsync(IErpDbContext db, SalespersonDataScope scope,
        long? purchaseOrderId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(scope);
        if (scope.IsPrivileged) return true;
        if (purchaseOrderId is not > 0) return true;

        var owningCustomerId = await ResolveOwningCustomerIdAsync(db, purchaseOrderId, ct);
        return scope.AllowsCustomer(owningCustomerId);
    }

    /// <summary>
    /// 可见性硬边界（非披露）：不可见的入库单与「不存在」返回同一 <see cref="NotFoundText"/> 错误，
    /// 绝不透露范围外单据的 Id / 单号 / 归属客户，也绝不先做任何状态 / 明细判定。
    /// </summary>
    public static async Task EnsureReceiptVisibleAsync(IErpDbContext db, SalespersonDataScope scope,
        StockIn entity, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entity);
        if (!await IsReceiptVisibleAsync(db, scope, entity, ct))
            throw BusinessException.NotFound(NotFoundText);
    }

    /// <summary>可见性硬边界（按持久化来源 Id 的最小投影重载）。</summary>
    public static async Task EnsureReceiptVisibleAsync(IErpDbContext db, SalespersonDataScope scope,
        long? purchaseOrderId, CancellationToken ct = default)
    {
        if (!await IsReceiptVisibleAsync(db, scope, purchaseOrderId, ct))
            throw BusinessException.NotFound(NotFoundText);
    }

    /// <summary>
    /// 分页列表作用域下推：统一闸门（身份 / 账号状态 / 业务员映射 / 菜单）fail closed 后，
    /// 把权威客户范围<b>在计数 / 分页之前</b>下推到数据库。特权账号不过滤；受限账号仅返回
    /// 「未链接」或「链接采购订单归属客户在其客户范围内」的入库单，绝不「先查全量再内存过滤」，
    /// 与详情 / 流水 / 生命周期的可见性判定完全同口径。
    /// </summary>
    public static async Task<IQueryable<StockIn>> ApplyScopeAsync(IErpDbContext db,
        IQueryable<StockIn> source, long? userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        var scope = await EnsureMenuAuthorizedAsync(db, userId, ct);
        if (scope.IsPrivileged)
            return source;

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

    // ==================== ERP-504：草稿写入护栏（形状 + 实际存储精度 + 在用商品） ====================

    /// <summary>
    /// 入库数量 / 毛重 / 体积与表头合计的实际存储精度：<c>DECIMAL(18,2)</c>。
    /// <para>与 <c>ErpDbContext</c> 对 <see cref="StockInDetail.Quantity"/> / <see cref="StockInDetail.Weight"/> /
    /// <see cref="StockInDetail.Volume"/> 与 <see cref="StockIn.TotalQuantity"/> / <see cref="StockIn.TotalWeight"/> /
    /// <see cref="StockIn.TotalVolume"/> 的 EF 默认精度同源（未显式声明精度即 <c>DECIMAL(18,2)</c>，
    /// 与 ERP-502 销售出库同口径）；校验必须使用<b>同一份</b>精度口径，否则超精度值会被落库静默取整。</para>
    /// </summary>
    public const int QuantityPrecision = 18;

    /// <summary>入库数量 / 毛重 / 体积与表头合计的实际小数位：2（与 <see cref="QuantityPrecision"/> 同源）。</summary>
    public const int QuantityScale = 2;

    /// <summary><c>DECIMAL(18,2)</c> 的最大可表示绝对值（16 位整数 + 2 位小数）。</summary>
    public const decimal MaxDecimal18Scale2 = 9_999_999_999_999_999.99m;

    /// <summary>
    /// ERP-504 草稿护栏口径文案（接口 / 文档同源）：创建 / 修改在单位折算与来源链接校验之后、单号预约与任何写入
    /// <b>之前</b>校验每一行将被持久化的明细（正数且在 <c>DECIMAL(18,2)</c> 内可表示的数量、非负且可表示的毛重 / 体积、
    /// 商品 Id 有效且商品存在 / 未删除 / 启用），并按同一精度口径校验与 <c>StockInController.Calculate</c> 同源的表头合计（含溢出）。
    /// <para>采购入库明细<b>不承载</b>客户端单价 / 成本 / 金额字段：成本一律由 ERP-033 既有权威来源解析
    /// （成本来源解析器，绝不臆造来源、绝不改写已审核采购价），因此本护栏没有可校验的价格字段；
    /// 库存流水成本精度仍由既有 <c>InventoryService</c> / <c>StockMovement</c>（<c>DECIMAL(18,6)</c>）口径兜底。</para>
    /// <para>客户端 <c>IsDeleted</c> <b>不可能</b>隐藏坏行：服务端持久化时把提交的每一行都强制为「正常」行
    /// （<c>ApplyServerOwnedDetailIdentity</c>），因此请求侧校验覆盖全部提交行；空草稿头（0 行）仍受支持，
    /// 但提交 / 审核要求至少一行有效明细。被拒绝的请求零单号消耗、零表头 / 明细 / 来源 / 状态变化，
    /// 也绝不新增表 / 列 / 索引 / 菜单 / 权限。</para>
    /// </summary>
    public const string DraftGuardRuleText =
        "采购入库单创建 / 修改在单位折算与来源链接校验之后、单号预约与任何写入之前执行统一草稿护栏：" +
        "每一行将被持久化的明细（含客户端提交 IsDeleted=true、服务端会强制恢复为正常的行）数量必须为正数、" +
        "毛重与体积不得为负，且与表头合计一并落在实际存储精度（DECIMAL(18,2)）内（含溢出）；" +
        "商品 Id 必须有效且商品存在、未删除、已启用；空草稿头（0 行有效明细）仍受支持（提交 / 审核要求至少一行有效明细），" +
        "重复商品行按累计数量语义保留，绝不引入任意唯一性规则；采购入库明细没有客户端价格 / 成本字段，" +
        "成本一律经既有权威来源解析，绝不臆造来源或改写已审核采购价；" +
        "被拒绝的请求零单号消耗、零单据 / 明细 / 来源 / 状态 / 库存 / 流水变化，绝不新增表 / 列 / 索引 / 菜单 / 权限。";

    /// <summary>入库数量 / 毛重 / 体积是否在实际存储精度 <c>DECIMAL(18,2)</c> 内可表示（最多 2 位小数且绝对值有界）。</summary>
    public static bool IsQuantityRepresentable(decimal value)
        => decimal.Round(value, QuantityScale) == value
           && value >= -MaxDecimal18Scale2 && value <= MaxDecimal18Scale2;

    /// <summary>
    /// 请求侧草稿明细形状校验（纯内存，不访问数据库）：校验<b>客户端提交的每一行</b>明细 ——
    /// 服务端持久化时把每一行都强制为「正常」行（<c>ApplyServerOwnedDetailIdentity</c> 覆盖 <c>IsDeleted = false</c>），
    /// 客户端 <c>IsDeleted</c> 绝不可能隐藏坏行后被重新启用；商品 Id 有效、数量为正且在 <c>DECIMAL(18,2)</c> 内可表示、
    /// 毛重与体积非负且可表示，随后按与 <c>StockInController.Calculate</c> <b>同源</b>的公式重算表头合计
    /// （数量 / 毛重 / 体积）并校验可表示性与溢出。
    /// <para>刻意<b>不</b>校验商品唯一性：同一商品允许在一张入库单中重复出现（累计数量语义），不引入任何任意唯一性规则；
    /// 空明细（0 行）在草稿阶段仍受支持，是否要求非空由提交 / 审核承诺边界决定。</para>
    /// </summary>
    public static void EnsureRequestLineShape(IEnumerable<StockInDetail> details)
    {
        ArgumentNullException.ThrowIfNull(details);
        var rows = details.ToList();
        EnsureLineShape(rows);
        EnsureRepresentableTotals(rows);
    }

    /// <summary>
    /// 承诺边界（提交 / 审核）的已存明细形状复核：只逐行校验<b>仍然生效</b>（未删除）的明细，
    /// 但表头合计仍按与 <c>StockInController.Calculate</c> 同源的口径对<b>全部已存行</b>重算，
    /// 保证校验口径与实际持久化 / 记账口径一致（历史软删除行不改写、不阻断）。
    /// </summary>
    public static void EnsureEffectiveLineShape(IEnumerable<StockInDetail> details)
    {
        ArgumentNullException.ThrowIfNull(details);
        var rows = details.ToList();
        EnsureLineShape(rows.Where(d => !d.IsDeleted).ToList());
        EnsureRepresentableTotals(rows);
    }

    /// <summary>提交 / 审核前必须至少存在一行有效明细（未删除）；空草稿头可保存但不可提交 / 审核。</summary>
    public static void EnsureHasEffectiveLine(IEnumerable<StockInDetail> details, string action)
    {
        ArgumentNullException.ThrowIfNull(details);
        if (!details.Any(d => !d.IsDeleted))
            throw BusinessException.RuleConflict($"入库单无明细，不能{action}");
    }

    /// <summary>
    /// 批量「在用商品」校验（一次有界只读查询，绝不逐行查库）：给定集合中的每一行商品必须存在、未删除、已启用。
    /// 调用方决定集合口径：请求侧传入<b>全部提交行</b>（含客户端 IsDeleted 隐藏行，服务端会重新启用它们），
    /// 承诺边界传入仍然生效的已存行。商品缺失 / 已删除按参数错误拒绝，已停用按规则冲突拒绝。
    /// </summary>
    public static async Task EnsureProductsAvailableAsync(IErpDbContext db, IEnumerable<StockInDetail> details,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(details);

        var rows = details.ToList();
        var ids = rows.Where(d => d.ProductId > 0).Select(d => d.ProductId).Distinct().ToList();
        var products = ids.Count == 0
            ? new Dictionary<long, BaseProduct>()
            : await db.BaseProducts.AsNoTracking()
                .Where(p => ids.Contains(p.Id) && !p.IsDeleted)
                .ToDictionaryAsync(p => p.Id, ct);

        var line = 0;
        foreach (var d in rows)
        {
            line++;
            var label = string.IsNullOrWhiteSpace(d.ProductName) ? $"第 {line} 行" : d.ProductName;

            if (d.ProductId <= 0)
                throw BusinessException.InvalidParameter($"入库明细 [{label}] 缺少有效商品 Id，不能入库");
            if (!products.TryGetValue(d.ProductId, out var product))
                throw BusinessException.InvalidParameter($"商品 [{label}] 不存在或已删除，不能入库");
            if (product.Status != 1)
                throw BusinessException.RuleConflict($"商品「{product.ProductName}」已停用，不能入库（fail closed）");
        }
    }

    /// <summary>
    /// ERP-504 创建 / 修改共享草稿护栏：<see cref="EnsureRequestLineShape"/>（全部提交行的形状 + 实际存储精度 + 表头合计）
    /// → <see cref="EnsureProductsAvailableAsync"/>（批量在用商品）。任一不满足即按既有受控业务错误
    /// （<c>InvalidParameter</c> / <c>RuleConflict</c>）拒绝；调用点保证发生在单号预约与任何表头 / 明细写入之前。
    /// </summary>
    public static async Task EnsureDraftValidAsync(IErpDbContext db, IEnumerable<StockInDetail> details,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(details);

        var rows = details.ToList();
        EnsureRequestLineShape(rows);
        await EnsureProductsAvailableAsync(db, rows, ct);
    }

    /// <summary>
    /// ERP-504 承诺边界（提交 / 审核）复核：至少一行有效明细 + 有效行形状 / 实际存储精度 + 与 <c>Calculate</c> 同源的表头合计
    /// + 当前在用商品（批量，当前主数据实时复核）。调用方保证在既有来源 / 单据锁内执行；
    /// 任一不满足即拒绝，且零单据 / 状态 / 来源 / 成本 / 库存 / 流水 / 单号变化。
    /// </summary>
    public static async Task EnsureCommitReadyAsync(IErpDbContext db, IEnumerable<StockInDetail> details,
        string action, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(details);

        var rows = details.ToList();
        EnsureHasEffectiveLine(rows, action);
        EnsureEffectiveLineShape(rows);
        await EnsureProductsAvailableAsync(db, rows.Where(d => !d.IsDeleted), ct);
    }

    /// <summary>逐行形状 / 精度校验（调用方决定行集合口径）。</summary>
    private static void EnsureLineShape(IReadOnlyList<StockInDetail> rows)
    {
        var line = 0;
        foreach (var d in rows)
        {
            line++;
            var label = string.IsNullOrWhiteSpace(d.ProductName) ? $"第 {line} 行" : d.ProductName;

            if (d.ProductId <= 0)
                throw BusinessException.InvalidParameter($"入库明细 [{label}] 缺少有效商品 Id，不能入库");
            if (d.Quantity <= 0m)
                throw BusinessException.InvalidParameter($"商品 [{label}] 的入库数量必须大于 0");
            if (!IsQuantityRepresentable(d.Quantity))
                throw BusinessException.InvalidParameter(
                    $"商品 [{label}] 的入库数量 {d.Quantity} 超出存储精度（DECIMAL(18,2)）：已拒绝，绝不静默截断或取整");
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
    }

    /// <summary>
    /// 表头合计（数量 / 毛重 / 体积）与实际存储精度校验：与 <c>StockInController.Calculate</c> 同源重算，
    /// 任一不可表示 / 溢出都返回受控业务错误。通过本校验后 <c>Calculate</c> 的结果可被 EF <b>原样</b>持久化，
    /// 明细之和与表头合计逐分一致（不存在落库取整差），也不会把正数量静默舍入为 0。
    /// </summary>
    private static void EnsureRepresentableTotals(IReadOnlyList<StockInDetail> rows)
    {
        try
        {
            decimal totalQuantity = 0m, totalWeight = 0m, totalVolume = 0m;
            foreach (var d in rows)
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
                "入库数量 / 毛重 / 体积超出可存储范围（DECIMAL(18,2)）：已拒绝，绝不静默截断或取整");
        }
    }

    private static void AssertTotalRepresentable(string name, decimal value)
    {
        if (!IsQuantityRepresentable(value))
            throw BusinessException.InvalidParameter(
                $"入库单{name} {value} 超出存储精度（DECIMAL(18,2)）：已拒绝，绝不静默截断或取整");
    }
}
