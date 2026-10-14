using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace ERP.Application.Services;

/// <summary>
/// 容器单据草稿行的统一投影（ERP-505 / ERP-506 共享）：
/// <see cref="ContainerPreLoadingDetail"/>（预装柜明细）与 <see cref="ContainerLoadingDetail"/>（装柜明细）
/// 的数值 / 主数据形状完全同源（商品 / 数量 / 箱数 / 毛重 / 体积 + 删除态），
/// 因此两个单据共用**同一份**判定与**同一份**口径文案，避免出现「一套规则两处实现」的分叉。
/// </summary>
/// <param name="ProductId">商品 Id（必须为正且指向在用商品）。</param>
/// <param name="ProductName">商品名称快照（仅用于受控错误文案；不参与判定）。</param>
/// <param name="Quantity">行数量（基础单位口径；必须为正且在实际存储精度内可表示）。</param>
/// <param name="Cartons">行箱数（不得为负且可表示）。</param>
/// <param name="Weight">行毛重（kg；不得为负且可表示）。</param>
/// <param name="Volume">行体积（m³；不得为负且可表示）。</param>
/// <param name="IsDeleted">行的删除态：请求侧校验**不**据此跳过任何行（服务端持久化时会把每一行都恢复为「正常」行）。</param>
public readonly record struct ContainerLoadingDraftLine(
    long ProductId,
    string ProductName,
    decimal Quantity,
    decimal Cartons,
    decimal Weight,
    decimal Volume,
    bool IsDeleted);

/// <summary>
/// 容器单据（预装柜单 / 装柜清单）手工与链接草稿的**统一写入护栏**（ERP-505）。
/// <para><b>缺口</b>：预装柜单 / 装柜清单的创建与修改只复用既有权威校验（实时身份 · 菜单 · 客户数据范围、
/// 上游订柜 / 预装柜链接、显式需求计划 · 出运证据链接）；而这些链接规则对<b>未链接</b>（<c>null</c>）的历史语义
/// 直接返回 —— 未链接草稿因而可以带着 <c>0</c> / 负数 / 超出 <c>DECIMAL(18,2)</c> 的数量、负箱数 / 负毛重 / 负体积，
/// 或缺失 / 不存在 / 已软删除 / 已停用商品的明细行落库，控制器 <c>Calculate</c> 直接求和也会把不可表示 / 溢出的表头合计写入。</para>
/// <para><b>口径</b>：创建 / 修改在既有授权与来源证据校验之后、<b>单号预约与任何写入之前</b>逐行校验**全部提交行**
/// （客户端 <c>IsDeleted = true</c> 的行同样参与 —— 服务端持久化时会把每一行都强制为「正常」行，客户端删除态
/// <b>绝不可能</b>隐藏坏行后被重新启用），并按与 <c>Calculate</c> <b>同源</b>的公式重算并校验表头合计（箱数 / 毛重 / 体积，含溢出）。
/// 提交 / 审核作为<b>承诺边界</b>在既有订柜行 · 上游销售订单行 · 本单行更新锁内复核仍然生效的已存行与当前在用商品，
/// 且要求至少一行有效明细（空草稿头仍可保存，但不可提交 / 审核）。</para>
/// <para><b>保持不变的语义</b>：重复商品行按累计数量语义保留（不引入任何任意唯一性规则）；历史 / 未链接明细保持显式
/// <c>null</c>，显式 <c>SourceSalesOrderDetailId</c> / <c>SourceStockOutDetailId</c> 证据链接原样保留、绝不臆造、绝不静默清空；
/// 单据号 / 状态 / 删除态 / 时间戳与明细身份的权威覆盖仍在<b>全部校验通过之后</b>执行。</para>
/// <para><b>边界</b>：本类只做纯判定 + <b>一次有界只读</b>商品主数据查询：不落库、不改单据 / 明细 / 状态 / 来源 / 库存 /
/// 财务 / 单证，不开启事务、不加锁（锁与事务由既有调用方负责），也不新增表 / 列 / 索引 / 菜单 / 权限。
/// 本护栏<b>不是</b>库存预留、<b>不是</b>出运凭证，也绝不触发任何库存 / 财务过账。</para>
/// </summary>
public static class ContainerLoadingDraftValidationRules
{
    /// <summary>
    /// 数量 / 箱数 / 毛重 / 体积与表头合计的实际存储精度：<c>DECIMAL(18,2)</c>。
    /// <para>与 <c>ErpDbContext</c> 对 <see cref="ContainerPreLoadingDetail.Quantity"/> /
    /// <see cref="ContainerPreLoadingDetail.Cartons"/> / <see cref="ContainerPreLoadingDetail.Weight"/> /
    /// <see cref="ContainerPreLoadingDetail.Volume"/>、<see cref="ContainerPreLoading.TotalCartons"/> /
    /// <see cref="ContainerPreLoading.TotalWeight"/> / <see cref="ContainerPreLoading.TotalVolume"/>，以及
    /// <see cref="ContainerLoadingDetail"/> / <see cref="ContainerLoadingList"/> 同名属性的 EF 默认精度同源
    /// （未显式声明精度即 <c>DECIMAL(18,2)</c>）；校验必须使用**同一份**精度口径，否则超精度值会被落库静默取整。</para>
    /// </summary>
    public const int QuantityPrecision = 18;

    /// <summary>数量 / 箱数 / 毛重 / 体积与表头合计的实际小数位：2（与 <see cref="QuantityPrecision"/> 同源）。</summary>
    public const int QuantityScale = 2;

    /// <summary><c>DECIMAL(18,2)</c> 的最大可表示绝对值（16 位整数 + 2 位小数）。</summary>
    public const decimal MaxDecimal18Scale2 = 9_999_999_999_999_999.99m;

    /// <summary>请求侧 / 承诺边界通用的行集合文案（含客户端删除态行与已存有效行两种口径）。</summary>
    public const string CommitRequiresLineText = "提交 / 审核要求至少一行有效明细（空草稿头仍可保存）";

    /// <summary>
    /// ERP-505 草稿护栏口径文案（接口 / 文档同源）：创建 / 修改在既有授权与来源证据链接校验之后、单号预约与任何写入
    /// <b>之前</b>校验每一行将被持久化的明细（正数且在 <c>DECIMAL(18,2)</c> 内可表示的数量、非负且可表示的箱数 / 毛重 / 体积、
    /// 商品 Id 有效且商品存在 / 未删除 / 启用），并按同一精度口径校验与 <c>Calculate</c> 同源的表头合计（含溢出）。
    /// <para>预装柜单与装柜清单的明细都<b>不承载</b>客户端单价 / 成本 / 币种 / 金额字段：不存在可校验的「货币合计」，
    /// 本护栏也绝不引入币种 / 单位混算、绝不臆造金额或改写任何既有商业条款；库存 / 财务仍由既有显式过账路径单独承担。</para>
    /// <para>客户端 <c>IsDeleted</c> <b>不可能</b>隐藏坏行：服务端持久化时把提交的每一行都强制为「正常」行
    /// （<c>ApplyServerOwnedDetailIdentity</c>），因此请求侧校验覆盖全部提交行；空草稿头（0 行）仍受支持，
    /// 但提交 / 审核要求至少一行有效明细。被拒绝的请求零单号消耗、零表头 / 明细 / 来源 / 状态变化，
    /// 也绝不新增表 / 列 / 索引 / 菜单 / 权限。</para>
    /// </summary>
    public const string DraftGuardRuleText =
        "预装柜单 / 装柜清单创建与修改在既有授权与来源证据链接校验之后、单号预约与任何写入之前执行统一草稿护栏：" +
        "每一行将被持久化的明细（含客户端提交 IsDeleted=true、服务端会强制恢复为正常的行）数量必须为正数、" +
        "箱数 / 毛重 / 体积不得为负，且与表头合计一并落在实际存储精度（DECIMAL(18,2)）内（含溢出）；" +
        "商品 Id 必须有效且商品存在、未删除、已启用；空草稿头（0 行有效明细）仍受支持（提交 / 审核要求至少一行有效明细），" +
        "重复商品行按累计数量语义保留，绝不引入任意唯一性规则；显式需求计划 / 出运证据链接原样保留，绝不臆造、绝不静默清空；" +
        "明细没有客户端单价 / 成本 / 币种 / 金额字段，不存在货币合计，绝不引入币种 / 单位混算；" +
        "被拒绝的请求零单号消耗、零单据 / 明细 / 来源 / 状态 / 库存 / 财务变化，绝不新增表 / 列 / 索引 / 菜单 / 权限。";

    /// <summary>数量 / 箱数 / 毛重 / 体积是否在实际存储精度 <c>DECIMAL(18,2)</c> 内可表示（最多 2 位小数且绝对值有界）。</summary>
    public static bool IsQuantityRepresentable(decimal value)
        => decimal.Round(value, QuantityScale) == value
           && value >= -MaxDecimal18Scale2 && value <= MaxDecimal18Scale2;

    /// <summary>把预装柜明细投影为共享草稿行（只读快照，不修改来源实体）。</summary>
    public static List<ContainerLoadingDraftLine> Project(IEnumerable<ContainerPreLoadingDetail> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return rows.Select(d => new ContainerLoadingDraftLine(
            d.ProductId, d.ProductName, d.Quantity, d.Cartons, d.Weight, d.Volume, d.IsDeleted)).ToList();
    }

    /// <summary>把装柜明细投影为共享草稿行（只读快照，不修改来源实体）。</summary>
    public static List<ContainerLoadingDraftLine> Project(IEnumerable<ContainerLoadingDetail> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return rows.Select(d => new ContainerLoadingDraftLine(
            d.ProductId, d.ProductName, d.Quantity, d.Cartons, d.Weight, d.Volume, d.IsDeleted)).ToList();
    }

    /// <summary>
    /// 请求侧草稿明细形状校验（纯内存，不访问数据库）：校验<b>客户端提交的每一行</b>明细 ——
    /// 服务端持久化时把每一行都强制为「正常」行（<c>ApplyServerOwnedDetailIdentity</c> 覆盖 <c>IsDeleted = false</c>），
    /// 客户端 <c>IsDeleted</c> 绝不可能隐藏坏行后被重新启用；商品 Id 有效、数量为正且在 <c>DECIMAL(18,2)</c> 内可表示、
    /// 箱数 / 毛重 / 体积非负且可表示，随后按与控制器 <c>Calculate</c> <b>同源</b>的公式重算表头合计
    /// （箱数 / 毛重 / 体积）并校验可表示性与溢出。
    /// <para>刻意<b>不</b>校验商品唯一性：同一商品允许在一张单据中重复出现（累计数量语义），不引入任何任意唯一性规则；
    /// 空明细（0 行）在草稿阶段仍受支持，是否要求非空由提交 / 审核承诺边界决定。</para>
    /// </summary>
    public static void EnsureRequestLineShape(IEnumerable<ContainerLoadingDraftLine> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var all = rows.ToList();
        EnsureLineShape(all);
        EnsureRepresentableTotals(all);
    }

    /// <summary>
    /// 承诺边界（提交 / 审核）的已存明细形状复核：只逐行校验<b>仍然生效</b>（未删除）的明细，
    /// 但表头合计仍按与控制器 <c>Calculate</c> 同源的口径对<b>全部已存行</b>重算，
    /// 保证校验口径与实际持久化口径一致（历史软删除行不改写、不阻断）。
    /// </summary>
    public static void EnsureEffectiveLineShape(IEnumerable<ContainerLoadingDraftLine> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var all = rows.ToList();
        EnsureLineShape(all.Where(d => !d.IsDeleted).ToList());
        EnsureRepresentableTotals(all);
    }

    /// <summary>提交 / 审核前必须至少存在一行有效明细（未删除）；空草稿头可保存但不可提交 / 审核。</summary>
    public static void EnsureHasEffectiveLine(IEnumerable<ContainerLoadingDraftLine> rows, string action)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (!rows.Any(d => !d.IsDeleted))
            throw BusinessException.RuleConflict($"单据无明细，不能{action}（{CommitRequiresLineText}）");
    }

    /// <summary>
    /// 批量「在用商品」校验（一次有界只读查询，绝不逐行查库）：给定集合中的每一行商品必须存在、未删除、已启用。
    /// 调用方决定集合口径：请求侧传入<b>全部提交行</b>（含客户端 IsDeleted 隐藏行，服务端会重新启用它们），
    /// 承诺边界传入仍然生效的已存行。商品缺失 / 已删除按参数错误拒绝，已停用按规则冲突拒绝。
    /// </summary>
    public static async Task EnsureProductsAvailableAsync(IErpDbContext db,
        IEnumerable<ContainerLoadingDraftLine> rows, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(rows);

        var all = rows.ToList();
        var ids = all.Where(d => d.ProductId > 0).Select(d => d.ProductId).Distinct().ToList();
        var products = ids.Count == 0
            ? new Dictionary<long, BaseProduct>()
            : await db.BaseProducts.AsNoTracking()
                .Where(p => ids.Contains(p.Id) && !p.IsDeleted)
                .ToDictionaryAsync(p => p.Id, ct);

        var line = 0;
        foreach (var d in all)
        {
            line++;
            var label = string.IsNullOrWhiteSpace(d.ProductName) ? $"第 {line} 行" : d.ProductName;

            if (d.ProductId <= 0)
                throw BusinessException.InvalidParameter($"明细 [{label}] 缺少有效商品 Id，不能保存");
            if (!products.TryGetValue(d.ProductId, out var product))
                throw BusinessException.InvalidParameter($"商品 [{label}] 不存在或已删除，不能保存");
            if (product.Status != 1)
                throw BusinessException.RuleConflict($"商品「{product.ProductName}」已停用，不能保存（fail closed）");
        }
    }

    /// <summary>
    /// 创建 / 修改共享草稿护栏：<see cref="EnsureRequestLineShape"/>（全部提交行的形状 + 实际存储精度 + 表头合计）
    /// → <see cref="EnsureProductsAvailableAsync"/>（批量在用商品）。任一不满足即按既有受控业务错误
    /// （<c>InvalidParameter</c> / <c>RuleConflict</c>）拒绝；调用点保证发生在单号预约与任何表头 / 明细写入之前。
    /// </summary>
    public static Task EnsureDraftValidAsync(IErpDbContext db, IEnumerable<ContainerLoadingDraftLine> rows,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(rows);

        var all = rows.ToList();
        EnsureRequestLineShape(all);
        return EnsureProductsAvailableAsync(db, all, ct);
    }

    /// <summary>
    /// 承诺边界（提交 / 审核）复核：至少一行有效明细 + 有效行形状 / 实际存储精度 + 与 <c>Calculate</c> 同源的表头合计
    /// + 当前在用商品（批量，当前主数据实时复核）。调用方保证在既有来源 / 单据锁内执行；
    /// 任一不满足即拒绝，且零单据 / 状态 / 明细 / 来源 / 库存 / 财务 / 单号变化。
    /// </summary>
    public static Task EnsureCommitReadyAsync(IErpDbContext db, IEnumerable<ContainerLoadingDraftLine> rows,
        string action, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(rows);

        var all = rows.ToList();
        EnsureHasEffectiveLine(all, action);
        EnsureEffectiveLineShape(all);
        return EnsureProductsAvailableAsync(db, all.Where(d => !d.IsDeleted), ct);
    }

    /// <summary>预装柜单创建 / 修改共享护栏（在请求对象上校验全部提交行，服务端随后才覆盖明细身份 / 删除态）。</summary>
    public static Task EnsurePreLoadingDraftValidAsync(IErpDbContext db, ContainerPreLoading entity,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entity);
        return EnsureDraftValidAsync(db, Project(entity.Details), ct);
    }

    /// <summary>预装柜单提交 / 审核共享复核（对已存明细复核形状 / 精度 / 表头合计与当前在用商品）。</summary>
    public static Task EnsurePreLoadingCommitReadyAsync(IErpDbContext db, ContainerPreLoading entity,
        string action, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entity);
        return EnsureCommitReadyAsync(db, Project(entity.Details), action, ct);
    }

    /// <summary>
    /// 装柜清单创建 / 修改共享护栏（ERP-506）：在**请求对象**上校验**全部提交行**（含客户端 <c>IsDeleted = true</c> 的行 ——
    /// 服务端随后由 <c>ApplyServerOwnedDetailIdentity</c> 把每一行都强制为「正常」行，客户端删除态绝不可能隐藏坏行），
    /// 调用点保证发生在单号预约与任何表头 / 明细 / 来源写入之前。仅作薄适配，判定口径与预装柜侧**完全同源**。
    /// </summary>
    public static Task EnsureLoadingListDraftValidAsync(IErpDbContext db, ContainerLoadingList entity,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entity);
        return EnsureDraftValidAsync(db, Project(entity.Details), ct);
    }

    /// <summary>
    /// 装柜清单提交 / 审核共享复核（ERP-506）：对**已存明细**复核至少一行有效明细、形状 / 实际存储精度 /
    /// 与 <c>Calculate</c> 同源的表头合计，并实时复核当前在用商品（批量，当前主数据）。调用方保证在既有
    /// 上游出库行 · 预装柜行 · 本单行锁内执行；任一不满足即拒绝，且零单据 / 状态 / 明细 / 来源 / 库存 / 财务 / 单号变化。
    /// </summary>
    public static Task EnsureLoadingListCommitReadyAsync(IErpDbContext db, ContainerLoadingList entity,
        string action, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entity);
        return EnsureCommitReadyAsync(db, Project(entity.Details), action, ct);
    }

    /// <summary>逐行形状 / 精度校验（调用方决定行集合口径）。</summary>
    private static void EnsureLineShape(IReadOnlyList<ContainerLoadingDraftLine> rows)
    {
        var line = 0;
        foreach (var d in rows)
        {
            line++;
            var label = string.IsNullOrWhiteSpace(d.ProductName) ? $"第 {line} 行" : d.ProductName;

            if (d.ProductId <= 0)
                throw BusinessException.InvalidParameter($"明细 [{label}] 缺少有效商品 Id，不能保存");
            if (d.Quantity <= 0m)
                throw BusinessException.InvalidParameter($"商品 [{label}] 的数量必须大于 0");
            if (!IsQuantityRepresentable(d.Quantity))
                throw BusinessException.InvalidParameter(
                    $"商品 [{label}] 的数量 {d.Quantity} 超出存储精度（DECIMAL(18,2)）：已拒绝，绝不静默截断或取整");
            if (d.Cartons < 0m)
                throw BusinessException.InvalidParameter($"商品 [{label}] 的箱数不能为负数");
            if (!IsQuantityRepresentable(d.Cartons))
                throw BusinessException.InvalidParameter(
                    $"商品 [{label}] 的箱数 {d.Cartons} 超出存储精度（DECIMAL(18,2)）：已拒绝，绝不静默截断或取整");
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
    /// 表头合计（箱数 / 毛重 / 体积）与实际存储精度校验：与控制器 <c>Calculate</c> 同源重算，
    /// 任一不可表示 / 溢出都返回受控业务错误。通过本校验后 <c>Calculate</c> 的结果可被 EF <b>原样</b>持久化，
    /// 明细之和与表头合计逐分一致（不存在落库取整差）。
    /// </summary>
    private static void EnsureRepresentableTotals(IReadOnlyList<ContainerLoadingDraftLine> rows)
    {
        try
        {
            decimal totalCartons = 0m, totalWeight = 0m, totalVolume = 0m;
            foreach (var d in rows)
            {
                totalCartons += d.Cartons;
                totalWeight += d.Weight;
                totalVolume += d.Volume;
            }

            AssertTotalRepresentable("合计箱数", totalCartons);
            AssertTotalRepresentable("合计毛重", totalWeight);
            AssertTotalRepresentable("合计体积", totalVolume);
        }
        catch (OverflowException)
        {
            // decimal 加法溢出：转换为受控业务错误，绝不落库为截断值。
            throw BusinessException.InvalidParameter(
                "箱数 / 毛重 / 体积合计超出可存储范围（DECIMAL(18,2)）：已拒绝，绝不静默截断或取整");
        }
    }

    private static void AssertTotalRepresentable(string name, decimal value)
    {
        if (!IsQuantityRepresentable(value))
            throw BusinessException.InvalidParameter(
                $"单据{name} {value} 超出存储精度（DECIMAL(18,2)）：已拒绝，绝不静默截断或取整");
    }
}

