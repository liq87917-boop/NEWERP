using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ERP.Application.Services;

/// <summary>
/// 库存盘点/调整单过账护栏（ERP-355）：把盘点单的「创建 / 修改 / 提交 / 审核（过账）、销审（冲销）、取消、删除」
/// 统一到同一条「身份 / 菜单授权 + 单据行锁 + 确定性库存行锁 + 可串行化事务」的口径上。
/// <para>关键不变量：审核必须基于<b>已验证的账面数量基线</b>——账面数量必须等于权威当前库存数量；
/// 一旦不一致（账面基线过期）立即拒绝，绝不用「实盘 - 客户端账面」的差额静默过账出虚构差异，
/// 而是要求在待提交状态显式重新盘点 / 修改后再提交审核。</para>
/// <para>审核把全部明细、库存流水与单据状态包在同一个可串行化事务内<b>恰好一次</b>；
/// 审核 / 销审 / 取消 / 提交 / 删除 / 修改共用同一把盘点单行锁
/// （<c>SELECT ... WITH (UPDLOCK, HOLDLOCK)</c>），因此并发「审核 / 审核」或「审核 / 取消」不可能同时成功。
/// 受影响的库存行按<b>确定性顺序</b>（仓库 Id 升序 → 商品 Id 升序，见
/// <see cref="InventoryService.OrderStockIdentities"/>）加锁，不形成环形等待。</para>
/// <para>ERP-507 补齐<b>数值边界护栏</b>（<see cref="EnsureLineShape"/>）：创建 / 修改在单号预约与任何写入之前，
/// 校验每一行将被持久化的明细（含客户端 <c>IsDeleted=true</c>、服务端会强制恢复为正常的行）的账面 / 实盘数量与成本单价
/// 在实际存储精度（<c>DECIMAL(18,4)</c> / <c>DECIMAL(18,6)</c>）内可表示，并用与
/// <c>StockAdjustmentController.Normalize</c> 同源的 <see cref="ComputeSignedDifference"/> 复核有符号差异数量 / 金额
/// 与表头合计（含乘法 / 求和溢出）；精度常量与 <c>ErpDbContext</c> 的 <c>HasPrecision</c> 声明同源。</para>
/// <para>ERP-526 补齐<b>单位完整性护栏</b>：盘点流程<b>全程按商品基础单位计量</b>（账面 / 实盘数量、成本与差异都按基础单位口径），
/// 且<b>不实现任何包装折算</b>。因此显式提供的非空明细单位（去首尾空白、大小写不敏感）必须等于商品当前基础单位，
/// 否则在单号预约、明细整体替换与任何落库之前受控拒绝（<see cref="IsBaseUnitEligible"/> /
/// <see cref="EnsureStoredBaseUnitsEligibleAsync"/>）；空 / 空白单位保持既有回退（审核时按商品基础单位落库展示），
/// 商品未维护基础单位时对显式非空单位同样受控拒绝。护栏只做「放行 / 拒绝」：<b>绝不</b>改写账面 / 实盘数量、成本或差异，
/// 也绝不把装箱 / 任意单位按基础单位记账。</para>
/// <para>本类只做<b>纯判定与有界只读查询</b>，不改单据、不改库存 / 流水、不改任何估值规则；
/// 「判定 + 状态变更 + 库存写入」的原子性由调用方（<c>StockAdjustmentController</c>）在同一可串行化事务内完成。</para>
/// </summary>
public static class StockAdjustmentPostingRules
{
    /// <summary>
    /// 盘点模块所需既有菜单编码（与 <c>SeedData.Menus</c> 同源）：复用既有「库存查询」菜单，
    /// 与其它库存模块（ERP-029 / ERP-135 / ERP-354）同一口径，<b>不新增任何权限模型</b>，
    /// 也不把空身份当作管理员。
    /// </summary>
    public const string RequiredMenuCode = "stock-query";

    /// <summary>盘点模块菜单中文文案（与既有菜单名一致）</summary>
    public const string RequiredMenuText = "库存查询";

    /// <summary>过账护栏口径文案（接口 / 文档同源）</summary>
    public const string RuleText =
        "库存盘点单的创建 / 修改 / 提交 / 审核 / 销审 / 取消 / 删除都会重新校验当前身份（缺失 / 禁用 / 未映射一律 fail closed）与既有「库存查询」（stock-query）菜单授权；" +
        "审核先取得盘点单行锁（UPDLOCK/HOLDLOCK），再在锁内重读权威状态与有效流水，校验明细（非负数量、非负成本、商品不重复、仓库与商品在用），" +
        "随后按确定性顺序（仓库 Id 升序 → 商品 Id 升序）锁定受影响库存行，并把「已验证的账面数量基线（必须等于权威当前库存）」与全部差异调整、" +
        "库存流水、单据状态包在同一个可串行化事务内，恰好一次；账面数量与权威库存不一致（基线过期）时拒绝过账并要求在待提交状态重新盘点 / 修改；" +
        "明细单位（去首尾空白、大小写不敏感）显式非空时必须等于商品当前基础单位：盘点流程全程按基础单位计量、不做任何包装折算，" +
        "绝不把装箱 / 任意单位按基础单位记账，也绝不改写账面 / 实盘数量或成本；空 / 空白单位保持既有回退，商品未维护基础单位时显式非空单位一律拒绝；" +
        "提交 / 审核在既有单据 / 库存锁内用当前商品快照复核仍然生效的已存明细单位；" +
        "销审按既有红字流水冲销，已被后续业务占用时拒绝且余额 / 流水 / 状态全部不变；并发审核 / 审核或审核 / 取消因共用同一把单据行锁而只能成功其一。";

    /// <summary>模块边界文案（不改估值口径、不改主数据、不删历史证据）</summary>
    public const string BoundaryText =
        "本护栏只保护库存盘点单的状态流转与库存写入：不改变库存成本口径（移动加权平均 / 差异金额 = 差异数量 × 成本单价），" +
        "不改写商品、仓库主数据，不改写其它单据与历史库存流水，不做任何「自动更正历史记录」的写回，" +
        "不新增表 / 列 / 权限 / 菜单，也不删除任何历史证据；所有库存增减与冲销仍复用既有 InventoryService（ERP-009）记账并写流水。";

    /// <summary>
    /// 盘点数量 / 行差异数量 / 表头差异合计的实际存储精度：<c>DECIMAL(18,4)</c>（与 <c>ErpDbContext</c> 对
    /// <see cref="StockAdjustmentDetail.BookQuantity"/> / <see cref="StockAdjustmentDetail.ActualQuantity"/> /
    /// <see cref="StockAdjustmentDetail.DiffQuantity"/> / <see cref="StockAdjustmentDetail.DiffAmount"/> 与
    /// <see cref="StockAdjustment.TotalDiffQuantity"/> / <see cref="StockAdjustment.TotalDiffAmount"/> 的显式
    /// 精度声明、以及 <c>SchemaUpgrader</c> 的建表脚本同源）。
    /// <para>校验必须使用**同一份**精度口径：否则超精度值会被 SQL 静默取整 / 截断，或与明细合计出现落库差。</para>
    /// </summary>
    public const int QuantityPrecision = 18;

    /// <summary>盘点数量 / 行差异数量 / 表头差异合计的实际小数位：4（与 <see cref="QuantityPrecision"/> 同源）。</summary>
    public const int QuantityScale = 4;

    /// <summary><c>DECIMAL(18,4)</c> 的最大可表示绝对值（14 位整数 + 4 位小数）。</summary>
    public const decimal MaxDecimal18Scale4 = 99_999_999_999_999.9999m;

    /// <summary>盘点成本单价的实际存储精度：<c>DECIMAL(18,6)</c>（与 <c>ErpDbContext</c> 显式声明同源）。</summary>
    public const int CostPrecision = 18;

    /// <summary>盘点成本单价的实际小数位：6（与 <see cref="CostPrecision"/> 同源）。</summary>
    public const int CostScale = 6;

    /// <summary><c>DECIMAL(18,6)</c> 的最大可表示绝对值（12 位整数 + 6 位小数）。</summary>
    public const decimal MaxDecimal18Scale6 = 999_999_999_999.999999m;

    /// <summary>盘点数量 / 差异数量是否在实际存储精度 <c>DECIMAL(18,4)</c> 内可表示（最多 4 位小数且绝对值有界）。</summary>
    public static bool IsQuantityRepresentable(decimal value)
        => decimal.Round(value, QuantityScale) == value
           && value >= -MaxDecimal18Scale4 && value <= MaxDecimal18Scale4;

    /// <summary>盘点成本单价是否在实际存储精度 <c>DECIMAL(18,6)</c> 内可表示（最多 6 位小数且绝对值有界）。</summary>
    public static bool IsCostRepresentable(decimal value)
        => decimal.Round(value, CostScale) == value
           && value >= -MaxDecimal18Scale6 && value <= MaxDecimal18Scale6;

    /// <summary>
    /// ERP-507 数值边界口径文案（接口 / 文档同源）：创建 / 修改在<b>单号预约与任何表头 / 明细写入之前</b>
    /// 校验<b>每一行将被持久化的明细</b>（账面 / 实盘数量非负且在 <c>DECIMAL(18,4)</c> 内可表示、成本单价非负且
    /// 在 <c>DECIMAL(18,6)</c> 内可表示），并用与 <c>StockAdjustmentController.Normalize</c> <b>同源</b>的公式
    /// 重算<b>有符号</b>差异数量（实盘 − 账面）与差异金额（差异数量 × 成本单价，按 <c>InventoryService.RoundAmount</c> 取整），
    /// 校验每一行与表头合计（差异总数量 / 差异总金额）在实际存储精度内可表示（含乘法 / 求和溢出）。
    /// <para>客户端 <c>IsDeleted</c> <b>不可能</b>隐藏坏行：服务端在持久化时把提交的每一行都强制为「正常」行，
    /// 因此请求侧校验覆盖全部提交行；空草稿头（0 行明细）仍受支持，是否要求非空有效明细由既有审核承诺边界决定。
    /// 被拒绝的请求零单号消耗，零表头 / 明细 / 库存 / 流水 / 状态变化，也绝不新增表 / 列 / 索引 / 菜单 / 权限。</para>
    /// </summary>
    public const string DraftGuardRuleText =
        "库存盘点单创建 / 修改在单号预约与任何表头 / 明细写入之前执行统一数值边界护栏：" +
        "每一行将被持久化的明细（含客户端提交 IsDeleted=true、服务端会强制恢复为正常的行）账面 / 实盘数量必须非负且在 DECIMAL(18,4) 内可表示、" +
        "成本单价非负且在 DECIMAL(18,6) 内可表示，有符号差异数量（实盘 − 账面）与差异金额（差异数量 × 成本单价）及其表头合计（差异总数量 / 差异总金额）" +
        "必须落在实际存储精度内（含乘法与求和溢出）；商品 Id 必须有效且同一商品不得重复，仓库与商品必须存在、未删除、已启用；" +
        "空草稿头（0 行明细）仍受支持，被拒绝的请求零单号消耗、零单据 / 明细 / 库存 / 流水 / 状态变化。";

    /// <summary>
    /// ERP-526 盘点单位资格口径文案（接口 / 文档 / 测试同源）：盘点流程<b>全程按商品基础单位</b>计量金额与数量，
    /// 显式提供的非空明细单位（去首尾空白、大小写不敏感）必须等于商品当前基础单位；等于基础单位直接放行（原样保留口径），
    /// 空 / 空白单位保持既有回退（审核时按商品基础单位落库展示），其他任意显式非空单位（<b>包括商品自身的装箱单位</b>）
    /// 一律在单号预约与任何落库之前受控拒绝 —— 盘点流程不做任何包装折算，绝不臆造新单位、绝不猜测单位换算、
    /// 绝不改写账面 / 实盘数量或成本；商品未维护基础单位时对显式非空单位同样受控拒绝。
    /// </summary>
    public const string BaseUnitEligibilityRuleText =
        "库存盘点单明细显式提供的非空单位（去首尾空白、大小写不敏感）必须等于商品当前基础单位：" +
        "等于基础单位直接放行（数量 / 成本 / 差异口径不变）；空 / 空白单位保持既有回退（审核时按商品基础单位落库展示）；" +
        "其他任意显式非空单位（包括商品自身的装箱单位）一律在单号预约与任何落库之前受控拒绝 —— " +
        "盘点流程全程按基础单位计量、不做任何包装折算，绝不把任意单位按基础单位记账、绝不猜测单位换算、绝不改写账面 / 实盘数量或成本；" +
        "商品未维护基础单位时对显式非空单位同样受控拒绝（绝不臆造新单位）。";

    /// <summary>
    /// 有符号差异数量与差异金额的**唯一**计算口径（与 <c>StockAdjustmentController.Normalize</c> 同源）：
    /// <c>差异数量 = 实盘 − 账面</c>（保留正负号：正数 = 盘盈、负数 = 盘亏），
    /// <c>差异金额 = 差异数量 × 成本单价</c>（复用 <see cref="InventoryService.RoundAmount"/> 的 4 位小数、AwayFromZero 取整口径）。
    /// <para>护栏与控制器共用本方法，确保「校验的数值」与「落库的数值」逐分一致，不做任何静默取整 / 截断 / 绝对化。</para>
    /// </summary>
    public static (decimal DiffQuantity, decimal DiffAmount) ComputeSignedDifference(
        decimal bookQuantity, decimal actualQuantity, decimal unitCost)
    {
        var diffQuantity = actualQuantity - bookQuantity;
        var diffAmount = InventoryService.RoundAmount(diffQuantity * unitCost);
        return (diffQuantity, diffAmount);
    }

    /// <summary>
    /// 身份 / 账号状态 / 模块授权三重校验（fail closed）：缺失或非法身份按未认证拒绝，
    /// 禁用账号、无既有「库存查询」菜单授权按权限不足拒绝，绝不猜测身份、绝不退化为管理员。
    /// </summary>
    public static async Task EnsureMenuAuthorizedAsync(IErpDbContext db, long? userId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (userId is null or <= 0)
            throw new BusinessException("请先登录后再操作库存盘点单", ErrorCodes.Unauthorized);

        var user = await db.SysUsers.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == userId.Value && !u.IsDeleted, ct);
        if (user is null)
            throw new BusinessException("登录账号不存在或已删除，禁止操作库存盘点单", ErrorCodes.Unauthorized);
        if (user.Status != UserStatus.Enabled)
            throw new BusinessException("登录账号已禁用，禁止操作库存盘点单（fail closed）", ErrorCodes.Forbidden);

        var menuCodes = await CustomerReceivableReconciliationService.LoadAuthorizedMenuCodesAsync(db, userId.Value);
        if (!menuCodes.Contains(RequiredMenuCode, StringComparer.OrdinalIgnoreCase))
        {
            throw new BusinessException(
                $"当前账号没有「{RequiredMenuText}」（{RequiredMenuCode}）模块授权：拒绝操作库存盘点单" +
                "（fail closed，不执行任何写入）",
                ErrorCodes.Forbidden);
        }
    }

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
    /// 由盘点仓库与明细商品构造需要按确定性顺序加锁的库存行集合（去重后仍为「仓库 × 商品」）。
    /// 这只是锁定集合，不读取 / 不写入库存，也不改变任何数量或成本。
    /// </summary>
    public static IReadOnlyList<StockIdentity> StockKeys(long warehouseId, IEnumerable<StockAdjustmentDetail> details)
    {
        ArgumentNullException.ThrowIfNull(details);
        if (warehouseId <= 0) return Array.Empty<StockIdentity>();

        return details.Where(d => !d.IsDeleted && d.ProductId is > 0)
            .Select(d => new StockIdentity(warehouseId, d.ProductId!.Value))
            .ToList();
    }

    /// <summary>
    /// 明细数值边界校验（不访问数据库）：校验<b>传入集合中的每一行</b>（调用方在创建 / 修改时传入全部提交行，
    /// 服务端会把客户端 <c>IsDeleted=true</c> 的行也强制恢复为「正常」行并持久化，因此**不存在被隐藏跳过的行**）——
    /// 商品 Id 必须有效、账面 / 实盘数量与成本单价不得为负数且必须在实际存储精度
    /// （数量 <c>DECIMAL(18,4)</c> / 成本 <c>DECIMAL(18,6)</c>）内可表示、同一商品不得在同一张盘点单中重复出现；
    /// 并用与 <c>StockAdjustmentController.Normalize</c> <b>同源</b>的公式重算有符号差异数量与差异金额，
    /// 校验每一行与表头合计（差异总数量 / 差异总金额）在 <c>DECIMAL(18,4)</c> 内可表示（含乘法 / 求和溢出）。
    /// 任一不满足即按参数非法拒绝，绝不进入任何库存写入、绝不静默取整 / 截断。
    /// <para>零账面 / 零实盘与零成本都是合法值（保留盘盈 / 盘亏与「0 差异」语义）；空集合（空草稿头）同样合法。</para>
    /// </summary>
    public static void EnsureLineShape(IEnumerable<StockAdjustmentDetail> details)
    {
        ArgumentNullException.ThrowIfNull(details);
        var rows = details as IReadOnlyList<StockAdjustmentDetail> ?? details.ToList();

        var productIds = new HashSet<long>();
        var line = 0;
        decimal totalQuantity = 0m, totalAmount = 0m;
        try
        {
            foreach (var d in rows)
            {
                line++;
                var label = string.IsNullOrWhiteSpace(d.ProductName) ? $"第 {line} 行" : d.ProductName;

                if (d.ProductId is null or <= 0)
                    throw BusinessException.InvalidParameter($"盘点明细 [{label}] 缺少有效商品 Id，无法盘点");
                if (d.BookQuantity < 0)
                    throw BusinessException.InvalidParameter($"商品 [{label}] 的账面数量不能为负数");
                if (!IsQuantityRepresentable(d.BookQuantity))
                    throw BusinessException.InvalidParameter(
                        $"商品 [{label}] 的账面数量 {d.BookQuantity} 超出存储精度（DECIMAL(18,4) / 4 位小数）：已拒绝，绝不静默截断或取整");
                if (d.ActualQuantity < 0)
                    throw BusinessException.InvalidParameter($"商品 [{label}] 的实盘数量不能为负数");
                if (!IsQuantityRepresentable(d.ActualQuantity))
                    throw BusinessException.InvalidParameter(
                        $"商品 [{label}] 的实盘数量 {d.ActualQuantity} 超出存储精度（DECIMAL(18,4) / 4 位小数）：已拒绝，绝不静默截断或取整");
                if (d.UnitCost < 0)
                    throw BusinessException.InvalidParameter($"商品 [{label}] 的成本单价不能为负数");
                if (!IsCostRepresentable(d.UnitCost))
                    throw BusinessException.InvalidParameter(
                        $"商品 [{label}] 的成本单价 {d.UnitCost} 超出存储精度（DECIMAL(18,6) / 6 位小数）：已拒绝，绝不静默截断或取整");
                if (!productIds.Add(d.ProductId.Value))
                    throw BusinessException.InvalidParameter($"商品 [{label}] 在同一张盘点单中重复出现，请合并为一行");

                // 有符号差异：与 Normalize 同源重算，任一不可表示即拒绝（含 差异数量 × 成本单价 的乘法溢出）。
                var (diffQuantity, diffAmount) = ComputeSignedDifference(d.BookQuantity, d.ActualQuantity, d.UnitCost);
                if (!IsQuantityRepresentable(diffQuantity))
                    throw BusinessException.InvalidParameter(
                        $"商品 [{label}] 的差异数量 {diffQuantity} 超出存储精度（DECIMAL(18,4)）：已拒绝，绝不静默截断或取整");
                if (!IsQuantityRepresentable(diffAmount))
                    throw BusinessException.InvalidParameter(
                        $"商品 [{label}] 的差异金额 {diffAmount} 超出存储精度（DECIMAL(18,4)）：已拒绝，绝不静默截断或取整");

                totalQuantity += diffQuantity;
                totalAmount += diffAmount;
            }

            AssertTotalRepresentable("差异总数量", totalQuantity);
            AssertTotalRepresentable("差异总金额", InventoryService.RoundAmount(totalAmount));
        }
        catch (OverflowException)
        {
            // decimal 乘法 / 加法溢出：转换为受控业务错误，绝不落库为截断值。
            throw BusinessException.InvalidParameter(
                "盘点数量 / 成本 / 差异金额超出可存储范围（DECIMAL(18,4) / DECIMAL(18,6)）：已拒绝，绝不静默截断或取整");
        }
    }

    /// <summary>表头合计（差异总数量 / 差异总金额）的实际存储精度校验（与 <c>Normalize</c> 同源口径）。</summary>
    private static void AssertTotalRepresentable(string name, decimal value)
    {
        if (!IsQuantityRepresentable(value))
            throw BusinessException.InvalidParameter(
                $"盘点单{name} {value} 超出存储精度（DECIMAL(18,4)）：已拒绝，绝不静默截断或取整");
    }

    /// <summary>
    /// 主数据在用校验（只读）：仓库与每个商品都必须存在、未删除且处于启用状态（Status = 1）。
    /// 只读查询，不修改任何主数据；必须在 <see cref="EnsureLineShape"/> 之后调用（保证商品 Id 有效）。
    /// </summary>
    public static async Task EnsureLiveMasterDataAsync(IErpDbContext db, long warehouseId,
        IEnumerable<StockAdjustmentDetail> details, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(details);

        var warehouse = await db.BaseWarehouses.AsNoTracking()
            .FirstOrDefaultAsync(w => w.Id == warehouseId && !w.IsDeleted, ct);
        if (warehouse is null)
            throw BusinessException.InvalidParameter("盘点仓库不存在或已删除，无法盘点");
        if (warehouse.Status != 1)
            throw BusinessException.RuleConflict($"仓库 [{warehouse.WarehouseName}] 已停用，禁止盘点调整（fail closed）");

        foreach (var d in details.Where(d => !d.IsDeleted))
        {
            var productId = d.ProductId!.Value;
            var product = await db.BaseProducts.AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == productId && !p.IsDeleted, ct);
            if (product is null)
                throw BusinessException.InvalidParameter($"商品 [{d.ProductName}] 不存在或已删除，无法盘点");
            if (product.Status != 1)
                throw BusinessException.RuleConflict($"商品 [{product.ProductName}] 已停用，禁止盘点调整（fail closed）");

            // ERP-526：复用同一次商品读取复核明细单位的基础单位资格（先于单号预约 / 明细整体替换 / 任何落库）。
            EnsureBaseUnitEligible(product, d.Unit);
        }
    }

    /// <summary>
    /// ERP-526 共享纯判定（无 IO）：盘点明细单位是否落在盘点可用的<b>基础单位</b>口径内。
    /// <para>空 / 空白单位 → 放行（既有回退，审核时按商品基础单位落库展示）；去首尾空白后与商品基础单位
    /// 大小写不敏感相等 → 放行；其余任何显式非空单位（<b>包括商品自身的装箱单位</b>）→ 拒绝：
    /// 盘点流程不实现包装折算，绝不把任意单位按基础单位记账、绝不猜测单位换算。</para>
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
    /// ERP-526 批量复核（纯判定，无 IO）：对已在用商品快照逐行校验明细单位资格，任一越界即整体受控拒绝。
    /// 商品缺失 / 商品 Id 无效 / 已删除的行跳过（由既有在用主数据护栏负责）；只做只读判定，不改任何数量、成本或差异。
    /// </summary>
    public static void EnsureBaseUnitsEligible(IReadOnlyDictionary<long, BaseProduct> products,
        IEnumerable<StockAdjustmentDetail> details)
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
    /// ERP-526 提交 / 审核承诺边界复核（<b>一次有界只读查询</b>）：加载当前在用商品快照后复核<b>仍然生效</b>的
    /// 已存明细单位资格；调用方仍须先取得既有单据 / 库存行锁。主数据被改写（基础单位变更 / 商品删除）或单位越界时
    /// 受控拒绝，且不落库、不改单据 / 明细 / 状态 / 库存 / 流水 / 单号。
    /// </summary>
    public static async Task EnsureStoredBaseUnitsEligibleAsync(IErpDbContext db,
        IEnumerable<StockAdjustmentDetail> details, CancellationToken ct = default)
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
    /// ERP-526 受控拒绝（<c>RuleConflict</c>）：显式非空单位不是商品当前基础单位（主数据未维护基础单位时同样拒绝），
    /// 绝不臆造单位 / 换算，绝不改写账面 / 实盘数量或成本。
    /// </summary>
    private static void EnsureBaseUnitEligible(BaseProduct product, string? lineUnit)
    {
        if (IsBaseUnitEligible(lineUnit, product.Unit)) return;

        throw BusinessException.RuleConflict(
            $"商品 [{product.ProductName}] 的明细单位 [{(lineUnit ?? string.Empty).Trim()}] 不是当前基础单位 " +
            $"[{UnitDisplay(product.Unit)}]：库存盘点按基础单位计量、流程内不做任何包装折算，已拒绝" +
            "（绝不把任意单位按基础单位记账、绝不猜测单位换算、绝不改写账面 / 实盘数量或成本）");
    }

    private static string UnitDisplay(string? unit)
        => string.IsNullOrWhiteSpace(unit) ? "未设置" : unit.Trim();

    /// <summary>账面数量是否已过期：只要与权威当前库存不一致即视为过期（含账面为正但库存行不存在的情形）。</summary>
    public static bool IsStaleBookQuantity(decimal bookQuantity, decimal currentQuantity)
        => bookQuantity != currentQuantity;

    /// <summary>
    /// 账面基线校验：账面数量必须等于权威当前库存数量，否则拒绝过账（不静默过账、不自动改写账面数量），
    /// 要求操作员在待提交状态显式重新盘点 / 修改后再提交审核。
    /// </summary>
    public static void EnsureFreshBookQuantity(string productName, decimal bookQuantity, decimal currentQuantity)
    {
        if (!IsStaleBookQuantity(bookQuantity, currentQuantity)) return;

        var label = string.IsNullOrWhiteSpace(productName) ? "商品" : productName;
        throw BusinessException.RuleConflict(
            $"商品 [{label}] 的账面数量 {bookQuantity} 与当前库存 {currentQuantity} 不一致：" +
            "账面基线已过期，禁止按虚构差异过账；请在待提交状态重新盘点或修改后再提交审核");
    }
}
