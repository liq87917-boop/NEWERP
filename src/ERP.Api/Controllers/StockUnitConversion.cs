using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace ERP.Api.Controllers;

/// <summary>
/// 入出库统一以商品基础单位保存和记账。明细单位等于商品 PackageUnit 时，
/// Quantity × UnitsPerPackage 后改写为基础 Unit；其他/历史单位保持原数量。
/// <para>ERP-522：新增<b>共享、纯判定 / 批量读</b>的单位资格闸门
/// （<see cref="IsUnitEligible"/> / <see cref="EnsureUnitsEligible(IReadOnlyDictionary{long, BaseProduct}, IEnumerable{StockInDetail})"/> /
/// <see cref="EnsureUnitsEligibleAsync"/>），并<b>先只为采购入库路径</b>启用
/// （<see cref="NormalizeAsync(IErpDbContext, IEnumerable{StockInDetail}, bool)"/> 与提交 / 审核承诺边界）：
/// 显式提供的非空明细单位（去首尾空白、大小写不敏感）必须落在商品当前基础单位 / 合法装箱单位内，
/// 否则在单号预约与任何表头 / 明细 / 状态写入之前受控拒绝。其他单据族（销售出库 / 销售退货 / 采购退货）
/// 保持既有语义，直至各自任务显式接入等价校验。</para>
/// <para>ERP-523：销售出库路径显式接入<b>同一套</b>共享单位资格闸门
/// （<see cref="NormalizeAsync(IErpDbContext, IEnumerable{StockOutDetail}, bool)"/> 与
/// <see cref="EnsureUnitsEligibleAsync(IErpDbContext, IEnumerable{StockOutDetail}, CancellationToken)"/> 提交 / 审核承诺边界），
/// 显式提供的非空明细单位必须落在商品当前基础 / 装箱单位内，否则在单号预约与任何表头 / 明细 / 来源 / 状态写入之前受控拒绝；
/// <b>不改变采购入库策略</b>（ERP-522 原样）。</para>
/// <para>ERP-524：销售退货 / 采购退货路径显式接入<b>同一套</b>共享单位资格闸门
/// （<see cref="NormalizeAsync(IErpDbContext, IEnumerable{SalesReturnDetail}, bool)"/> /
/// <see cref="NormalizeAsync(IErpDbContext, IEnumerable{PurchaseReturnDetail}, bool)"/> 草稿写入，
/// 以及 <see cref="EnsureUnitsEligibleAsync(IErpDbContext, IEnumerable{SalesReturnDetail}, CancellationToken)"/> /
/// <see cref="EnsureUnitsEligibleAsync(IErpDbContext, IEnumerable{PurchaseReturnDetail}, CancellationToken)"/>
/// 提交 / 审核承诺边界）：手工（未链接）与显式链接来源的草稿一律在单号预约与任何表头 / 明细 / 来源 / 状态写入之前受控拒绝越界单位，
/// 提交 / 审核在既有单据 / 来源锁内用当前商品快照复核仍然生效的已存单位资格。
/// <b>不改变采购入库 / 销售出库策略</b>（ERP-522 / ERP-523 原样）。</para>
/// <para>绝不臆造来源、绝不猜测任意单位之间的换算、绝不改写历史单位：装箱折算仍恰好执行一次
/// （当前正数 <c>UnitsPerPackage</c>，保留 4 位小数），基础单位数量保持权威。</para>
/// </summary>
public static class StockUnitConversion
{
    /// <summary>装箱换算乘积的保留小数位（与既有基础单位数量口径一致）</summary>
    public const int QuantityScale = 4;

    /// <summary>
    /// ERP-522 单位资格口径文案（接口 / 文档同源）：显式提供的非空明细单位必须等于商品当前基础单位
    /// 或合法装箱单位；等于装箱单位时按当前正数 <c>UnitsPerPackage</c> 恰好折算一次为基础单位；
    /// 空单位保持既有回退（库存流水按商品基础单位兜底）；主数据缺失 / 非法（无可用基础单位且单位非空）
    /// 时受控拒绝，绝不臆造新单位、绝不猜测任意单位换算。
    /// </summary>
    public const string UnitEligibilityRuleText =
        "采购入库明细显式提供的非空单位（去首尾空白、大小写不敏感）必须等于商品当前基础单位或合法装箱单位：" +
        "等于基础单位直接按基础单位记账；等于装箱单位按当前正数 UnitsPerPackage 恰好折算一次为基础单位（保留 4 位小数，" +
        "不重复折算）；其他任意显式非空单位一律在单号预约与任何落库之前受控拒绝；" +
        "空单位保持既有回退语义（库存流水按商品基础单位兜底）；商品未维护可用基础 / 装箱单位时对显式非空单位受控拒绝，" +
        "绝不臆造新单位或猜测任意单位换算。";

    /// <summary>
    /// ERP-523 销售出库单位资格口径文案（接口 / 文档同源）：与采购入库<b>同一套</b>判定与受控拒绝口径，
    /// 仅在启用边界上独立 —— 销售出库草稿写入（创建 / 修改）与提交 / 审核承诺边界显式启用，
    /// 采购入库策略保持 ERP-522 原样，销售退货 / 采购退货保持既有重载。
    /// </summary>
    public const string StockOutUnitEligibilityRuleText =
        "销售出库明细显式提供的非空单位（去首尾空白、大小写不敏感）必须等于商品当前基础单位或合法装箱单位：" +
        "等于基础单位直接按基础单位记账；等于装箱单位按当前正数 UnitsPerPackage 恰好折算一次为基础单位（保留 4 位小数，" +
        "不重复折算）；其他任意显式非空单位一律在单号预约与任何落库之前受控拒绝；" +
        "空单位保持既有回退语义（库存流水按商品基础单位兜底）；商品未维护可用基础 / 装箱单位时对显式非空单位受控拒绝，" +
        "绝不臆造新单位或猜测任意单位换算。";

    /// <summary>
    /// ERP-524 销售退货 / 采购退货单位资格口径文案（接口 / 文档 / 测试同源）：与采购入库（ERP-522）、
    /// 销售出库（ERP-523）<b>同一套</b>判定与受控拒绝口径，仅在启用边界上独立 ——
    /// 销售退货 / 采购退货的草稿写入（创建 / 修改）与提交 / 审核承诺边界显式启用，
    /// 采购入库 / 销售出库策略保持既有原样。
    /// </summary>
    public const string ReturnUnitEligibilityRuleText =
        "销售退货单与采购退货单明细显式提供的非空单位（去首尾空白、大小写不敏感）必须等于商品当前基础单位或合法装箱单位：" +
        "等于基础单位直接按基础单位记账；等于装箱单位按当前正数 UnitsPerPackage 恰好折算一次为基础单位（保留 4 位小数，" +
        "不重复折算）；其他任意显式非空单位一律在单号预约与任何落库之前受控拒绝（手工未链接与显式链接来源同口径）；" +
        "空单位保持既有回退语义（库存流水按商品基础单位兜底）；商品未维护可用基础 / 装箱单位时对显式非空单位受控拒绝，" +
        "绝不臆造新单位或猜测任意单位换算；提交 / 审核在既有单据 / 来源锁内复核仍然生效的已存单位资格。";

    public static Task NormalizeAsync(IErpDbContext db, IEnumerable<StockInDetail> details)
        => NormalizeAsync(db, details, ensureUnitEligible: false);

    /// <summary>
    /// ERP-522 采购入库路径显式启用单位资格校验：在<b>同一次批量商品读取</b>内先判定明细单位资格
    /// （<see cref="IsUnitEligible"/>：空单位保持回退、基础 / 装箱单位放行、其他显式非空单位拒绝），
    /// 再执行既有装箱折算（恰好一次）。任一不合格行在单号预约 / 任何写入之前抛既有受控业务错误。
    /// </summary>
    public static Task NormalizeAsync(IErpDbContext db, IEnumerable<StockInDetail> details, bool ensureUnitEligible)
        => NormalizeAsync(db, details.Select(d => new MutableLine(d.ProductId, () => d.Unit,
            value => d.Unit = value, () => d.Quantity, value => d.Quantity = value)), ensureUnitEligible);

    public static Task NormalizeAsync(IErpDbContext db, IEnumerable<StockOutDetail> details)
        => NormalizeAsync(db, details.Select(d => new MutableLine(d.ProductId, () => d.Unit,
            value => d.Unit = value, () => d.Quantity, value => d.Quantity = value)), ensureUnitEligible: false);

    /// <summary>
    /// ERP-523 销售出库路径显式启用单位资格校验（与采购入库同口径、同一次批量商品读取）：
    /// 在<b>同一次批量商品读取</b>内先判定明细单位资格
    /// （<see cref="IsUnitEligible"/>：空单位保持回退、基础 / 装箱单位放行、其他显式非空单位拒绝），
    /// 再执行既有装箱折算（恰好一次）。任一不合格行在单号预约 / 任何写入之前抛既有受控业务错误。
    /// </summary>
    public static Task NormalizeAsync(IErpDbContext db, IEnumerable<StockOutDetail> details, bool ensureUnitEligible)
        => NormalizeAsync(db, details.Select(d => new MutableLine(d.ProductId, () => d.Unit,
            value => d.Unit = value, () => d.Quantity, value => d.Quantity = value)), ensureUnitEligible);

    /// <summary>
    /// 销售退货明细同口径折算（ERP-357）：明细单位等于商品 PackageUnit 时按 UnitsPerPackage 折算为基础 Unit，
    /// 保证退货数量与来源出库数量都以商品基础单位对齐、可直接合计比较。
    /// </summary>
    public static Task NormalizeAsync(IErpDbContext db, IEnumerable<SalesReturnDetail> details)
        => NormalizeAsync(db, details.Select(d => new MutableLine(d.ProductId ?? 0, () => d.Unit,
            value => d.Unit = value, () => d.Quantity, value => d.Quantity = value)), ensureUnitEligible: false);

    /// <summary>
    /// 采购退货明细同口径折算（ERP-358）：明细单位等于商品 PackageUnit 时按 UnitsPerPackage 折算为基础 Unit，
    /// 保证退货数量与来源入库数量都以商品基础单位对齐、可直接合计比较。
    /// </summary>
    public static Task NormalizeAsync(IErpDbContext db, IEnumerable<PurchaseReturnDetail> details)
        => NormalizeAsync(db, details.Select(d => new MutableLine(d.ProductId ?? 0, () => d.Unit,
            value => d.Unit = value, () => d.Quantity, value => d.Quantity = value)), ensureUnitEligible: false);

    /// <summary>
    /// ERP-524 销售退货路径显式启用单位资格校验（与采购入库 / 销售出库同口径、同一次批量商品读取）：
    /// 在<b>同一次批量商品读取</b>内先判定明细单位资格
    /// （<see cref="IsUnitEligible"/>：空单位保持回退、基础 / 装箱单位放行、其他显式非空单位拒绝），
    /// 再执行既有装箱折算（恰好一次）。任一不合格行在单号预约 / 任何写入之前抛既有受控业务错误；
    /// 手工（未链接）与显式链接来源的草稿同口径，绝不臆造来源、绝不猜测任意单位换算。
    /// </summary>
    public static Task NormalizeAsync(IErpDbContext db, IEnumerable<SalesReturnDetail> details, bool ensureUnitEligible)
        => NormalizeAsync(db, details.Select(d => new MutableLine(d.ProductId ?? 0, () => d.Unit,
            value => d.Unit = value, () => d.Quantity, value => d.Quantity = value)), ensureUnitEligible);

    /// <summary>
    /// ERP-524 采购退货路径显式启用单位资格校验（与销售退货同口径、同一次批量商品读取）：
    /// 在<b>同一次批量商品读取</b>内先判定明细单位资格
    /// （<see cref="IsUnitEligible"/>：空单位保持回退、基础 / 装箱单位放行、其他显式非空单位拒绝），
    /// 再执行既有装箱折算（恰好一次）。任一不合格行在单号预约 / 任何写入之前抛既有受控业务错误。
    /// </summary>
    public static Task NormalizeAsync(IErpDbContext db, IEnumerable<PurchaseReturnDetail> details, bool ensureUnitEligible)
        => NormalizeAsync(db, details.Select(d => new MutableLine(d.ProductId ?? 0, () => d.Unit,
            value => d.Unit = value, () => d.Quantity, value => d.Quantity = value)), ensureUnitEligible);

    private static async Task NormalizeAsync(IErpDbContext db, IEnumerable<MutableLine> source, bool ensureUnitEligible)
    {
        var lines = source.ToList();
        var ids = lines.Select(l => l.ProductId).Where(id => id > 0).Distinct().ToList();
        var products = await db.BaseProducts.AsNoTracking().Where(p => ids.Contains(p.Id) && !p.IsDeleted)
            .ToDictionaryAsync(p => p.Id);
        foreach (var line in lines)
        {
            if (!products.TryGetValue(line.ProductId, out var product)) continue;
            var lineUnit = line.GetUnit().Trim();
            var packageUnit = (product.PackageUnit ?? string.Empty).Trim();
            var baseUnit = (product.Unit ?? string.Empty).Trim();
            // ERP-522：先判定单位资格（显式非空单位必须落在基础 / 装箱单位内，空单位保持既有回退），
            // 再执行既有装箱折算；其他单据族沿用不启用，保证「先入库、后其他族」的启用边界。
            if (ensureUnitEligible && !IsUnitEligible(lineUnit, baseUnit, packageUnit))
                throw UnsupportedUnit(product, lineUnit);
            if (packageUnit.Length == 0 || !lineUnit.Equals(packageUnit, StringComparison.OrdinalIgnoreCase)
                || lineUnit.Equals(baseUnit, StringComparison.OrdinalIgnoreCase)) continue;
            if (product.UnitsPerPackage <= 0)
                throw BusinessException.RuleConflict($"商品 [{product.ProductName}] 的每包装数量必须大于 0");
            if (baseUnit.Length == 0)
                throw BusinessException.RuleConflict($"商品 [{product.ProductName}] 未设置基础单位，不能执行包装换算");
            // ERP-502：装箱换算的乘法必须在受控范围内完成 —— decimal 溢出转换为受控业务错误，
            // 绝不把溢出 / 截断后的数量交给后续草稿护栏或落库。
            decimal converted;
            try
            {
                converted = line.GetQuantity() * product.UnitsPerPackage;
            }
            catch (OverflowException)
            {
                throw BusinessException.InvalidParameter(
                    $"商品 [{product.ProductName}] 的装箱换算数量超出可表示范围：已拒绝，绝不静默截断或取整");
            }

            line.SetQuantity(Math.Round(converted, QuantityScale));
            line.SetUnit(baseUnit);
        }
    }

    /// <summary>
    /// ERP-522 纯判定（无 IO）：明细单位是否落在商品当前有效单位口径内（去首尾空白、大小写不敏感）。
    /// <list type="bullet">
    /// <item>空 / 空白单位 → 允许（保持既有「空单位按商品基础单位兜底」回退，绝不臆造新单位）；</item>
    /// <item>等于非空基础单位 → 允许（按基础单位直接记账，不折算）；</item>
    /// <item>等于非空装箱单位 → 允许（由既有折算逻辑按当前 UnitsPerPackage 恰好折算一次）；</item>
    /// <item>其他显式非空单位（含商品未维护可用单位）→ 拒绝，绝不猜测任意单位换算。</item>
    /// </list>
    /// </summary>
    public static bool IsUnitEligible(string? lineUnit, string? baseUnit, string? packageUnit)
    {
        var unit = (lineUnit ?? string.Empty).Trim();
        if (unit.Length == 0) return true;

        var baseNormalized = (baseUnit ?? string.Empty).Trim();
        if (baseNormalized.Length > 0 && unit.Equals(baseNormalized, StringComparison.OrdinalIgnoreCase))
            return true;

        var packageNormalized = (packageUnit ?? string.Empty).Trim();
        return packageNormalized.Length > 0
               && unit.Equals(packageNormalized, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// ERP-522 纯判定批量复核（无 IO，绝不逐行查库）：给定商品快照逐行复核明细单位资格；
    /// 商品缺失 / 无效 Id 的行由既有在用商品护栏负责（此处跳过，绝不越权判定）。
    /// 任一显式非空且不落在基础 / 装箱单位内的行抛既有受控业务错误（<c>RuleConflict</c>）。
    /// </summary>
    public static void EnsureUnitsEligible(IReadOnlyDictionary<long, BaseProduct> products,
        IEnumerable<StockInDetail> details)
        => EnsureUnitsEligibleRows(products, AsUnitRows(details));

    /// <summary>
    /// ERP-523 纯判定批量复核（销售出库，口径与采购入库完全一致，无 IO）：给定商品快照逐行复核明细单位资格；
    /// 商品缺失 / 无效 Id 的行由既有在用商品护栏负责（此处跳过，绝不越权判定）。
    /// 任一显式非空且不落在基础 / 装箱单位内的行抛既有受控业务错误（<c>RuleConflict</c>）。
    /// </summary>
    public static void EnsureUnitsEligible(IReadOnlyDictionary<long, BaseProduct> products,
        IEnumerable<StockOutDetail> details)
        => EnsureUnitsEligibleRows(products, AsUnitRows(details));

    /// <summary>
    /// ERP-524 纯判定批量复核（销售退货，口径与采购入库 / 销售出库完全一致，无 IO）：给定商品快照逐行复核明细单位资格；
    /// 商品缺失 / 无效 Id 的行由既有在用商品护栏负责（此处跳过，绝不越权判定）。
    /// 任一显式非空且不落在基础 / 装箱单位内的行抛既有受控业务错误（<c>RuleConflict</c>）。
    /// </summary>
    public static void EnsureUnitsEligible(IReadOnlyDictionary<long, BaseProduct> products,
        IEnumerable<SalesReturnDetail> details)
        => EnsureUnitsEligibleRows(products, AsUnitRows(details));

    /// <summary>
    /// ERP-524 纯判定批量复核（采购退货，口径与销售退货完全一致，无 IO）：给定商品快照逐行复核明细单位资格；
    /// 商品缺失 / 无效 Id 的行由既有在用商品护栏负责（此处跳过，绝不越权判定）。
    /// 任一显式非空且不落在基础 / 装箱单位内的行抛既有受控业务错误（<c>RuleConflict</c>）。
    /// </summary>
    public static void EnsureUnitsEligible(IReadOnlyDictionary<long, BaseProduct> products,
        IEnumerable<PurchaseReturnDetail> details)
        => EnsureUnitsEligibleRows(products, AsUnitRows(details));

    /// <summary>
    /// ERP-522 承诺边界（提交 / 审核）批量复核：<b>一次有界只读查询</b>加载当前在用商品快照后逐行复核
    /// <b>仍然生效</b>明细的单位资格（调用方决定集合口径）。主数据被改写 / 单位越界时受控拒绝，
    /// 且不落库、不改单据 / 状态 / 库存 / 流水 / 单号。
    /// </summary>
    public static async Task EnsureUnitsEligibleAsync(IErpDbContext db, IEnumerable<StockInDetail> details,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(details);

        var rows = details.ToList();
        var products = await LoadEligibleProductSnapshotAsync(db, AsUnitRows(rows), ct);
        EnsureUnitsEligible(products, rows);
    }

    /// <summary>
    /// ERP-523 承诺边界（提交 / 审核）批量复核（销售出库）：<b>一次有界只读查询</b>加载当前在用商品快照后
    /// 逐行复核 <b>仍然生效</b>明细的单位资格（调用方决定集合口径）。主数据被改写 / 单位越界时受控拒绝，
    /// 且不落库、不改单据 / 状态 / 库存 / 流水 / 单号。
    /// </summary>
    public static async Task EnsureUnitsEligibleAsync(IErpDbContext db, IEnumerable<StockOutDetail> details,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(details);

        var rows = details.ToList();
        var products = await LoadEligibleProductSnapshotAsync(db, AsUnitRows(rows), ct);
        EnsureUnitsEligible(products, rows);
    }

    /// <summary>
    /// ERP-524 承诺边界（提交 / 审核）批量复核（销售退货）：<b>一次有界只读查询</b>加载当前在用商品快照后
    /// 逐行复核 <b>仍然生效</b>明细的单位资格（调用方决定集合口径，调用方仍须先取得既有单据 / 来源行锁）。
    /// 主数据被改写 / 单位越界时受控拒绝，且不落库、不改单据 / 状态 / 库存 / 流水 / 单号。
    /// </summary>
    public static async Task EnsureUnitsEligibleAsync(IErpDbContext db, IEnumerable<SalesReturnDetail> details,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(details);

        var rows = details.ToList();
        var products = await LoadEligibleProductSnapshotAsync(db, AsUnitRows(rows), ct);
        EnsureUnitsEligible(products, rows);
    }

    /// <summary>
    /// ERP-524 承诺边界（提交 / 审核）批量复核（采购退货）：与销售退货同口径，<b>一次有界只读查询</b>加载当前在用
    /// 商品快照后逐行复核 <b>仍然生效</b>明细的单位资格（调用方仍须先取得既有单据 / 来源行锁）。
    /// 主数据被改写 / 单位越界时受控拒绝，且不落库、不改单据 / 状态 / 库存 / 流水 / 单号。
    /// </summary>
    public static async Task EnsureUnitsEligibleAsync(IErpDbContext db, IEnumerable<PurchaseReturnDetail> details,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(details);

        var rows = details.ToList();
        var products = await LoadEligibleProductSnapshotAsync(db, AsUnitRows(rows), ct);
        EnsureUnitsEligible(products, rows);
    }

    private static IEnumerable<(long ProductId, string? Unit)> AsUnitRows(IEnumerable<StockInDetail> details)
        => details.Select(d => ((long)d.ProductId, (string?)d.Unit));

    private static IEnumerable<(long ProductId, string? Unit)> AsUnitRows(IEnumerable<StockOutDetail> details)
        => details.Select(d => ((long)d.ProductId, (string?)d.Unit));

    private static IEnumerable<(long ProductId, string? Unit)> AsUnitRows(IEnumerable<SalesReturnDetail> details)
        => details.Select(d => (d.ProductId ?? 0L, (string?)d.Unit));

    private static IEnumerable<(long ProductId, string? Unit)> AsUnitRows(IEnumerable<PurchaseReturnDetail> details)
        => details.Select(d => (d.ProductId ?? 0L, (string?)d.Unit));

    private static void EnsureUnitsEligibleRows(IReadOnlyDictionary<long, BaseProduct> products,
        IEnumerable<(long ProductId, string? Unit)> rows)
    {
        ArgumentNullException.ThrowIfNull(products);
        ArgumentNullException.ThrowIfNull(rows);

        foreach (var row in rows)
        {
            if (!products.TryGetValue(row.ProductId, out var product)) continue;
            if (!IsUnitEligible(row.Unit, product.Unit, product.PackageUnit))
                throw UnsupportedUnit(product, (row.Unit ?? string.Empty).Trim());
        }
    }

    private static async Task<Dictionary<long, BaseProduct>> LoadEligibleProductSnapshotAsync(IErpDbContext db,
        IEnumerable<(long ProductId, string? Unit)> rows, CancellationToken ct)
    {
        var ids = rows.Where(r => r.ProductId > 0).Select(r => r.ProductId).Distinct().ToList();
        return ids.Count == 0
            ? new Dictionary<long, BaseProduct>()
            : await db.BaseProducts.AsNoTracking()
                .Where(p => ids.Contains(p.Id) && !p.IsDeleted)
                .ToDictionaryAsync(p => p.Id, ct);
    }

    /// <summary>
    /// ERP-522 受控拒绝文案：显式非空单位不落在商品当前有效单位口径内（主数据缺失时同样拒绝，绝不臆造单位 / 换算）。
    /// </summary>
    private static BusinessException UnsupportedUnit(BaseProduct product, string lineUnit)
        => BusinessException.RuleConflict(
            $"商品 [{product.ProductName}] 的明细单位 [{lineUnit}] 不在当前有效单位口径内（基础单位 " +
            $"[{UnitDisplay(product.Unit)}] / 装箱单位 [{UnitDisplay(product.PackageUnit)}]）：" +
            "已拒绝，绝不把任意单位按基础单位记账、绝不猜测单位换算");

    private static string UnitDisplay(string? unit)
        => string.IsNullOrWhiteSpace(unit) ? "未设置" : unit.Trim();

    private sealed record MutableLine(long ProductId, Func<string> GetUnit, Action<string> SetUnit,
        Func<decimal> GetQuantity, Action<decimal> SetQuantity);
}
