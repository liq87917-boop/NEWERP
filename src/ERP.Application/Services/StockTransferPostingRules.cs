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
/// <para>ERP-501 新增<b>草稿写入护栏</b>（<see cref="EnsureLineShape"/> + <see cref="EnsureLiveMasterDataAsync"/>）：
/// 创建 / 修改在单号预约与明细整体替换<b>之前</b>校验「明细形状 + 实际存储精度可表示性 + 在用主数据」，
/// 提交与审核要求至少一行有效明细；任一不满足即按既有受控业务错误（<c>InvalidParameter</c> / <c>RuleConflict</c>）
/// 拒绝，绝不消耗单据号、绝不落任何单据 / 明细 / 库存行。空草稿头（0 行明细）与重复商品行仍受支持，
/// 不引入任何任意唯一性规则。</para>
/// <para>ERP-525 新增<b>基础单位资格护栏</b>（<see cref="IsBaseUnitEligible"/> + <see cref="EnsureBaseUnitsEligible"/> +
/// <see cref="EnsureStoredBaseUnitsEligibleAsync"/>，并接入 <see cref="EnsureLiveMasterDataAsync"/> 的同一次商品读取）：
/// 仓库调拨流程<b>全程按商品基础单位计量</b>且<b>不实现任何包装折算</b>，因此显式提供的非空明细单位（去首尾空白、大小写不敏感）
/// 必须恰好等于商品当前基础单位；装箱单位（<c>PackageUnit</c>）与任何任意单位一律在<b>单号预约 / 明细整体替换 / 落库之前</b>
/// 受控拒绝，<b>绝不把数量或成本按任意单位记账、绝不猜测单位换算</b>（此前审核会用 <c>product.Unit</c> 静默覆盖明细单位，
/// 使标注为 CTN / KG 的数量被当成基础单位记账）。空 / 空白单位保持既有回退（审核时按商品基础单位落库展示），
/// 商品未维护基础单位时对显式非空单位同样受控拒绝；提交 / 审核在既有单据 / 库存锁内用当前商品快照复核仍然生效的已存单位资格。</para>
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

    /// <summary>
    /// 调拨数量 / 行金额 / 合计数量 / 合计金额的实际存储精度：<c>DECIMAL(18,4)</c>（与 <c>ErpDbContext</c> 对
    /// <c>StockTransferDetail.Quantity</c> / <c>Amount</c>、<c>StockTransfer.TotalQuantity</c> / <c>TotalAmount</c>
    /// 的显式精度声明、以及 <c>SchemaUpgrader</c> 的建表脚本同源）。
    /// <para>校验必须使用**同一份**精度口径：否则正数量会被静默取整为 0，或写入与明细合计不一致的金额。</para>
    /// </summary>
    public const int QuantityPrecision = 18;

    /// <summary>调拨数量 / 行金额 / 合计的实际小数位：4（与 <see cref="QuantityPrecision"/> 同源）。</summary>
    public const int QuantityScale = 4;

    /// <summary><c>DECIMAL(18,4)</c> 的最大可表示绝对值（14 位整数 + 4 位小数）。</summary>
    public const decimal MaxDecimal18Scale4 = 99_999_999_999_999.9999m;

    /// <summary>调拨成本单价的实际存储精度：<c>DECIMAL(18,6)</c>。</summary>
    public const int CostPrecision = 18;

    /// <summary>调拨成本单价的实际小数位：6（与 <see cref="CostPrecision"/> 同源）。</summary>
    public const int CostScale = 6;

    /// <summary><c>DECIMAL(18,6)</c> 的最大可表示绝对值（12 位整数 + 6 位小数）。</summary>
    public const decimal MaxDecimal18Scale6 = 999_999_999_999.999999m;

    /// <summary>调拨数量是否在实际存储精度 <c>DECIMAL(18,4)</c> 内可表示（最多 4 位小数且绝对值有界）。</summary>
    public static bool IsQuantityRepresentable(decimal value)
        => decimal.Round(value, QuantityScale) == value
           && value >= -MaxDecimal18Scale4 && value <= MaxDecimal18Scale4;

    /// <summary>调拨成本单价是否在实际存储精度 <c>DECIMAL(18,6)</c> 内可表示（最多 6 位小数且绝对值有界）。</summary>
    public static bool IsCostRepresentable(decimal value)
        => decimal.Round(value, CostScale) == value
           && value >= -MaxDecimal18Scale6 && value <= MaxDecimal18Scale6;

    /// <summary>过账护栏口径文案（接口 / 文档同源）</summary>
    public const string RuleText =
        "仓库调拨单的创建 / 修改 / 提交 / 审核 / 销审 / 取消 / 删除都会重新校验当前身份（缺失 / 禁用 / 未映射一律 fail closed）与既有「库存查询」（stock-query）菜单授权；" +
        "创建与修改在单号预约与明细整体替换之前校验明细形状（正数量、非负成本、行金额与合计在实际存储精度内可表示）、在用主数据（两侧仓库互异且存在 / 未删除 / 启用，商品存在 / 未删除 / 启用）与明细单位的基础单位资格（显式非空单位必须等于商品当前基础单位；空 / 空白单位保持既有回退；调拨流程不实现任何包装折算），" +
        "被拒绝方绝不消耗单据号、不落任何单据 / 明细 / 库存行；提交与审核要求至少一行有效明细，并在既有单据 / 库存锁内用当前商品快照复核仍然生效的已存明细单位资格；" +
        "审核先取得调拨单行锁（UPDLOCK/HOLDLOCK），再在锁内重读权威状态与有效流水，随后把「调出仓减少 + 调入仓增加（同一成本单价）、明细成本、库存流水与单据状态」" +
        "包在同一个可串行化事务内，恰好一次；销审按既有红字流水冲销，调入仓货物已被消耗时拒绝且两侧余额 / 流水 / 状态全部不变；" +
        "两侧仓库的库存行按确定性顺序（仓库 Id 升序 → 商品 Id 升序）加锁，对向调拨不互相死锁；" +
        "并发审核 / 审核或审核 / 取消因共用同一把单据行锁而只能成功其一，绝不产生重复或已取消单据的库存。";

    /// <summary>模块边界文案（不改估值口径、不改主数据、不删历史证据）</summary>
    public const string BoundaryText =
        "本护栏只保护仓库调拨单的状态流转与库存写入：不改变库存成本口径（移动加权平均 / 同一成本单价双仓守恒），" +
        "不改写商品、仓库主数据（草稿护栏只做只读主数据核验），不改写其它单据与历史库存流水，" +
        "不新增表 / 列 / 索引 / 权限 / 菜单，也不删除任何历史证据；" +
        "所有库存增减与冲销仍复用既有 InventoryService（ERP-009 / ERP-025）记账并写流水。";

    /// <summary>
    /// ERP-525 调拨明细单位资格口径文案（接口 / 文档 / 测试同源）：仓库调拨流程<b>全程按商品基础单位计量</b>
    /// 且<b>不实现任何包装折算</b>，因此显式提供的非空明细单位必须恰好等于商品当前基础单位。
    /// </summary>
    public const string BaseUnitEligibilityRuleText =
        "仓库调拨明细显式提供的非空单位（去首尾空白、大小写不敏感）必须恰好等于商品当前基础单位：" +
        "等于基础单位则按基础单位记账，数量与成本原样保留；空 / 空白单位保持既有回退（审核时按商品基础单位落库展示）；" +
        "装箱单位（PackageUnit）或任何其它显式非空单位一律在单号预约、明细整体替换与任何落库之前受控拒绝 —— " +
        "调拨流程不实现包装折算，绝不把 CTN / KG 之类单位按基础单位记账、绝不猜测单位换算、绝不改写数量或成本；" +
        "商品未维护基础单位时对显式非空单位同样受控拒绝；提交 / 审核在既有单据 / 库存锁内用当前商品快照复核仍然生效的已存单位资格。";

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
    /// 草稿明细静态形状校验（不访问数据库，ERP-501）：商品 Id 必须有效、调拨数量必须为正且在实际存储精度
    /// <c>DECIMAL(18,4)</c> 内可表示、成本单价不得为负且在实际存储精度 <c>DECIMAL(18,6)</c> 内可表示；
    /// 随后按既有单位归一化（<see cref="InventoryService.RoundAmount"/>）重算逐行金额、合计数量与合计金额，
    /// 任一超出实际存储精度或溢出即按参数非法（<c>InvalidParameter</c>）拒绝，绝不静默取整 / 截断。
    /// <para>刻意<b>不</b>校验商品唯一性：同一商品允许在一张调拨单中重复出现（重复行按累计数量口径合并，
    /// 锁定集合由 <see cref="StockKeys"/> 去重），不引入任何任意唯一性规则。</para>
    /// <para>空明细分表（0 行有效明细）在草稿阶段仍受支持；是否要求非空由调用方（提交 / 审核）决定。</para>
    /// </summary>
    public static void EnsureLineShape(IEnumerable<StockTransferDetail> details)
    {
        ArgumentNullException.ThrowIfNull(details);

        var effective = details.Where(d => !d.IsDeleted).ToList();
        var line = 0;
        foreach (var d in effective)
        {
            line++;
            var label = string.IsNullOrWhiteSpace(d.ProductName) ? $"第 {line} 行" : d.ProductName;

            if (d.ProductId is null or <= 0)
                throw BusinessException.InvalidParameter($"调拨明细 [{label}] 缺少有效商品 Id，无法调拨");
            if (d.Quantity <= 0m)
                throw BusinessException.InvalidParameter($"商品 [{label}] 的调拨数量必须大于 0");
            if (!IsQuantityRepresentable(d.Quantity))
                throw BusinessException.InvalidParameter(
                    $"商品 [{label}] 的调拨数量 {d.Quantity} 超出存储精度（DECIMAL(18,4)）：已拒绝，绝不静默截断或取整");
            if (d.UnitCost < 0m)
                throw BusinessException.InvalidParameter($"商品 [{label}] 的成本单价不能为负数");
            if (!IsCostRepresentable(d.UnitCost))
                throw BusinessException.InvalidParameter(
                    $"商品 [{label}] 的成本单价 {d.UnitCost} 超出存储精度（DECIMAL(18,6)）：已拒绝，绝不静默截断或取整");
        }

        EnsureRepresentableAmounts(effective);
    }

    /// <summary>
    /// 逐行金额与合计的 checked 精度校验：与 <c>StockTransferController.Normalize</c> **同源**地重算
    /// 「行金额 = 数量 × 成本单价（按既有 4 位归一化）」、合计数量与合计金额，任一不可表示 / 溢出都返回
    /// 受控业务错误。通过本校验后，<c>Normalize</c> 的结果可被 EF **原样**持久化，
    /// 明细金额之和与主表合计逐分一致（不存在落库取整差），也不会把正数量 / 金额静默舍入为 0。
    /// </summary>
    private static void EnsureRepresentableAmounts(IReadOnlyList<StockTransferDetail> details)
    {
        try
        {
            decimal totalQuantity = 0m, totalAmount = 0m;
            for (var i = 0; i < details.Count; i++)
            {
                var amount = InventoryService.RoundAmount(details[i].Quantity * details[i].UnitCost);
                if (amount < -MaxDecimal18Scale4 || amount > MaxDecimal18Scale4)
                    throw BusinessException.InvalidParameter(
                        $"调拨明细第 {i + 1} 行金额 {amount} 超出存储精度（DECIMAL(18,4)）：已拒绝，绝不静默截断或取整");

                totalQuantity += details[i].Quantity;
                totalAmount += amount;
            }

            if (totalQuantity < -MaxDecimal18Scale4 || totalQuantity > MaxDecimal18Scale4)
                throw BusinessException.InvalidParameter(
                    $"调拨单合计数量 {totalQuantity} 超出存储精度（DECIMAL(18,4)）：已拒绝，绝不静默截断或取整");

            var roundedTotal = InventoryService.RoundAmount(totalAmount);
            if (roundedTotal < -MaxDecimal18Scale4 || roundedTotal > MaxDecimal18Scale4)
                throw BusinessException.InvalidParameter(
                    $"调拨单合计金额 {roundedTotal} 超出存储精度（DECIMAL(18,4)）：已拒绝，绝不静默截断或取整");
        }
        catch (OverflowException)
        {
            // decimal 乘法 / 加法溢出：转换为受控业务错误，绝不落库为截断值。
            throw BusinessException.InvalidParameter(
                "调拨数量 / 金额超出可存储范围（DECIMAL(18,4)）：已拒绝，绝不静默截断或取整改变合计");
        }
    }

    /// <summary>
    /// 主数据在用校验（只读，ERP-501）：调出仓与调入仓必须为正、互不相同、存在且未删除且处于启用状态
    /// （Status = 1）；每条有效明细的商品必须存在、未删除且启用。只读查询，不修改任何主数据。
    /// <para>必须在 <see cref="EnsureLineShape"/> 之后调用（保证商品 Id 有效）。空明细分表同样要求两侧仓库有效。</para>
    /// <para>「仓库名称为空串」「商品名回退为前端文本」都只是展示快照，绝不作为主数据在用的证明。</para>
    /// </summary>
    public static async Task EnsureLiveMasterDataAsync(IErpDbContext db, long fromWarehouseId,
        long toWarehouseId, IEnumerable<StockTransferDetail> details, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(details);

        if (fromWarehouseId <= 0 || toWarehouseId <= 0)
            throw BusinessException.InvalidParameter("调出仓与调入仓都必须填写");
        if (fromWarehouseId == toWarehouseId)
            throw BusinessException.RuleConflict("调出仓与调入仓不能相同");

        await EnsureLiveWarehouseAsync(db, fromWarehouseId, "调出", ct);
        await EnsureLiveWarehouseAsync(db, toWarehouseId, "调入", ct);

        foreach (var d in details.Where(d => !d.IsDeleted))
        {
            if (d.ProductId is null or <= 0)
                throw BusinessException.InvalidParameter($"商品 [{d.ProductName}] 缺少有效商品 Id，无法调拨");

            var productId = d.ProductId.Value;
            var product = await db.BaseProducts.AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == productId && !p.IsDeleted, ct);
            if (product is null)
                throw BusinessException.InvalidParameter($"商品 [{d.ProductName}] 不存在或已删除，无法调拨");
            if (product.Status != 1)
                throw BusinessException.RuleConflict($"商品 [{product.ProductName}] 已停用，禁止调拨（fail closed）");

            // ERP-525：复用同一次商品读取复核明细单位的基础单位资格（先于单号预约 / 明细整体替换 / 任何落库）。
            EnsureBaseUnitEligible(product, d.Unit);
        }
    }

    /// <summary>
    /// ERP-525 共享纯判定（无 IO）：明细单位是否落在调拨可用的<b>基础单位</b>口径内。
    /// <para>空 / 空白单位 → 放行（既有回退，审核时按商品基础单位落库展示）；去首尾空白后与商品基础单位
    /// 大小写不敏感相等 → 放行；其余任何显式非空单位（<b>包括商品自身的装箱单位</b>）→ 拒绝：
    /// 调拨流程不实现包装折算，绝不把任意单位按基础单位记账、绝不猜测单位换算。</para>
    /// <para>商品未维护基础单位（空 / 空白）时，显式非空单位一律拒绝（fail closed，绝不臆造新单位）。</para>
    /// </summary>
    public static bool IsBaseUnitEligible(string? lineUnit, string? baseUnit)
    {
        var unit = (lineUnit ?? string.Empty).Trim();
        if (unit.Length == 0) return true;

        var effectiveBase = (baseUnit ?? string.Empty).Trim();
        return effectiveBase.Length > 0 && unit.Equals(effectiveBase, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// ERP-525 批量复核（纯判定，无 IO）：对已在用商品快照逐行校验明细单位资格，任一越界即整体受控拒绝。
    /// 商品缺失 / 商品 Id 无效 / 已删除的行跳过（由既有在用主数据护栏负责）；只做只读判定，不改任何数量或成本。
    /// </summary>
    public static void EnsureBaseUnitsEligible(IReadOnlyDictionary<long, BaseProduct> products,
        IEnumerable<StockTransferDetail> details)
    {
        ArgumentNullException.ThrowIfNull(products);
        ArgumentNullException.ThrowIfNull(details);

        foreach (var d in details.Where(d => !d.IsDeleted && d.ProductId is > 0))
        {
            if (!products.TryGetValue(d.ProductId!.Value, out var product)) continue;
            EnsureBaseUnitEligible(product, d.Unit);
        }
    }

    /// <summary>
    /// ERP-525 提交 / 审核承诺边界复核（<b>一次有界只读查询</b>）：加载当前在用商品快照后复核<b>仍然生效</b>的
    /// 已存明细单位资格；调用方仍须先取得既有单据 / 库存行锁。主数据被改写（基础单位变更 / 商品删除）或单位越界时
    /// 受控拒绝，且不落库、不改单据 / 明细 / 状态 / 库存 / 流水 / 单号。
    /// </summary>
    public static async Task EnsureStoredBaseUnitsEligibleAsync(IErpDbContext db,
        IEnumerable<StockTransferDetail> details, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(details);

        var rows = details.Where(d => !d.IsDeleted && d.ProductId is > 0).ToList();
        if (rows.Count == 0) return;

        var ids = rows.Select(d => d.ProductId!.Value).Distinct().ToList();
        var products = await db.BaseProducts.AsNoTracking()
            .Where(p => ids.Contains(p.Id) && !p.IsDeleted)
            .ToDictionaryAsync(p => p.Id, ct);
        EnsureBaseUnitsEligible(products, rows);
    }

    /// <summary>
    /// ERP-525 受控拒绝（<c>RuleConflict</c>）：显式非空单位不是商品当前基础单位（主数据未维护基础单位时同样拒绝），
    /// 绝不臆造单位 / 换算，绝不改写数量或成本。
    /// </summary>
    private static void EnsureBaseUnitEligible(BaseProduct product, string? lineUnit)
    {
        if (IsBaseUnitEligible(lineUnit, product.Unit)) return;

        throw BusinessException.RuleConflict(
            $"商品 [{product.ProductName}] 的明细单位 [{(lineUnit ?? string.Empty).Trim()}] 不是当前基础单位 " +
            $"[{UnitDisplay(product.Unit)}]：仓库调拨按基础单位计量、流程内不做任何包装折算，已拒绝" +
            "（绝不把任意单位按基础单位记账、绝不猜测单位换算、绝不改写数量 / 成本）");
    }

    private static string UnitDisplay(string? unit)
        => string.IsNullOrWhiteSpace(unit) ? "未设置" : unit.Trim();

    /// <summary>单个调拨仓库的存在性 / 未删除 / 启用校验（只读；<paramref name="role"/> = 调出 / 调入）。</summary>
    private static async Task EnsureLiveWarehouseAsync(IErpDbContext db, long warehouseId, string role,
        CancellationToken ct)
    {
        var warehouse = await db.BaseWarehouses.AsNoTracking()
            .FirstOrDefaultAsync(w => w.Id == warehouseId && !w.IsDeleted, ct);
        if (warehouse is null)
            throw BusinessException.InvalidParameter($"{role}仓库不存在或已删除，无法调拨");
        if (warehouse.Status != 1)
            throw BusinessException.RuleConflict($"{role}仓库 [{warehouse.WarehouseName}] 已停用，禁止调拨（fail closed）");
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
