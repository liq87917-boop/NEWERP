using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace ERP.Application.Services;

/// <summary>
/// 供应商比价（<see cref="PurchaseQuote"/>）→ 采购订单（<see cref="PurchaseOrder"/>）
/// 单行 / 批次转换的<b>共享事务协议</b>与<b>锁内权威复核</b>（ERP-418）。
/// <para><b>唯一协议（单行与批次逐字共用，绝不出现两套口径）</b>：
/// ① 事务内先按请求解析<b>不可变批次成员</b>（比价行 Id 集合，去重、仅正整数）；
/// ② 解析每条来源行 <see cref="PurchaseQuote.RefOrderNo" />（转换前语义 = 关联销售订单号）指向的
/// <b>权威归属销售订单</b>，并按 <b>销售订单 Id 升序</b>先取排它行锁
/// （<see cref="OwningSalesOrderRowLockSql"/>，与既有销售订单取消 / 普通采购归属协议同一把行锁）；
/// ③ 再按 <b>比价行 Id 升序</b>取排它行锁（<see cref="QuoteRowLockSql"/>，与 ERP-417
/// 比价修改 / 删除 / 批量删除 / 审批决定同一把行锁）；
/// ④ 锁内<b>重新读取</b>受跟踪的权威行，核对成员集合逐字一致、批次号未被改写、归属解析结果未漂移；
/// ⑤ 复核实时资格（已选中 / 未放弃 / 未转换 / 已批准决定 / 批准供应商一致）、币种可识别、主数据引用与单位、
/// 数量与单价精度，以及目的地采购订单归属规则（<see cref="PurchaseSalesOrderLinkRules"/>）；
/// ⑥ 全部复核通过后才发号与写入，失败整体回滚（<b>绝不</b>残留孤儿采购订单 / 明细 / 来源标记）。</para>
/// <para><b>受控输家</b>：并发单行 / 批次 / 重叠批次转换中，只有先提交者成功；输家得到
/// <b>受控的陈旧 / 重复</b>业务拒绝（<see cref="StaleSourceText"/> / 既有重复生成守卫文案），
/// 既不产生第二张订单，也不产生部分写入。</para>
/// <para><b>币种</b>：只接受可识别的币种文本（<see cref="TryParseCurrency"/>）；无法识别一律拒绝，
/// <b>绝不回退人民币</b>，也绝不把不同币种的金额合计成一个数。</para>
/// <para><b>边界</b>：本类只做锁定、事务、只读复核与受控拒绝；不新增表 / 列 / 索引 / 菜单 / 角色 / 用户授权，
/// 不伪造任何授权、不把空身份当作匿名或管理员；不改写审批决定历史（append-only）、不写库存 / 库存流水 /
/// 发票 / 退税 / 费用 / 财务记录，也不删除任何历史证据（行锁只刷新技术审计时间戳 <c>UpdatedAt</c>）。</para>
/// </summary>
public static class PurchaseQuoteConversionMutationRules
{
    /// <summary>来源比价行锁语句（与 ERP-417 生命周期 / 审批决定共用同一常量）。</summary>
    public const string QuoteRowLockSql = PurchaseQuoteMutationRules.QuoteRowLockSql;

    /// <summary>
    /// 归属销售订单行锁语句（与销售订单取消 / 预装柜 / 普通采购归属链接共用同一常量）。
    /// </summary>
    public const string OwningSalesOrderRowLockSql = PreLoadingSalesOrderLinkRules.LockSalesOrderRowSql;

    /// <summary>
    /// 确定性锁序文案（接口 / 文档同源）：归属销售订单先于来源比价行，任何一侧多把锁一律按 Id 升序。
    /// </summary>
    public const string LockOrderText =
        "ERP-418 供应商比价 → 采购订单转换的确定性锁协议（单行 / 批次逐字共用）：先在可串行化事务内解析不可变批次成员，"
        + "按「归属销售订单行锁（db_owner.SalesOrders WITH (UPDLOCK, HOLDLOCK)，SalesOrderId 升序）→ "
        + "来源比价行锁（db_owner.PurchaseQuotes WITH (UPDLOCK, HOLDLOCK)，比价行 Id 升序）」的顺序取得排它行锁，"
        + "锁内重新读取权威行并复核成员一致 / 实时资格 / 币种 / 主数据 / 目的地归属规则后才发号与写入；"
        + "绝不反向获取其它锁，因此不存在锁环，也绝不出现「同一来源行两张订单」。";

    /// <summary>边界文案（不新增权限 / 表列，不改写审批历史与库存 / 财务）。</summary>
    public const string BoundaryText =
        "本护栏只保护比价 → 采购订单转换的并发完整性：不新增菜单 / 角色 / 用户授权或表结构，也不把空身份当作管理员；"
        + "不改写审批决定历史（append-only），不改写库存 / 库存流水、发票 / 退税 / 费用或财务记录，不删除历史证据；"
        + "行锁只刷新归属销售订单与来源比价行的技术审计时间戳 UpdatedAt（非商业证据）。";

    /// <summary>来源行在锁定前已不存在 / 已删除（或锁定期间被并发删除）时的受控拒绝文案。</summary>
    public const string StaleSourceText =
        "来源比价行在并发中已被删除或批次成员发生变化：本次转换整体未生效，请刷新后重试（绝不生成半成品订单）";

    /// <summary>批次成员在锁定前后不一致时的受控拒绝文案。</summary>
    public const string MembershipChangedText =
        "比价批次成员在锁定前后不一致（被并发删除 / 改批次 / 转换）：本次转换整体未生效，请刷新后重试";

    /// <summary>归属销售订单已被删除时的受控拒绝文案（绝不静默生成无归属订单）。</summary>
    public const string OwnershipUnavailableText =
        "关联销售订单（采购归属来源）不存在或已被删除：拒绝静默生成无归属 / 变更归属的采购订单";

    /// <summary>归属销售订单已取消时的受控拒绝文案。</summary>
    public const string OwnershipCancelledText = "关联销售订单已取消：不能作为采购备货的归属来源";

    /// <summary>归属销售订单已不可用（取消 / 驳回 / 未审核）时的受控拒绝文案。</summary>
    public const string OwnershipNotApprovedText = "关联销售订单未审核：不能作为采购备货的归属来源";

    /// <summary>币种无法识别时的受控拒绝文案（绝不回退人民币）。</summary>
    public const string UnknownCurrencyText =
        "比价行币种无法识别或不受支持（仅接受 CNY / USD）：拒绝转换，绝不回退默认人民币";

    /// <summary>供应商档案已停用 / 已删除时的受控拒绝文案。</summary>
    public const string SupplierMasterText =
        "比价行引用的供应商档案已停用或已删除：拒绝生成采购订单（fail closed，不采信比价行供应商快照）";

    /// <summary>商品档案已停用 / 已删除时的受控拒绝文案。</summary>
    public const string ProductMasterText =
        "比价行引用的商品档案已停用或已删除：拒绝生成采购订单（fail closed，不采信比价行商品快照）";

    /// <summary>单位文本缺失 / 超长时的受控拒绝文案。</summary>
    public const string UnitText = "比价行单位不能为空且不超过 20 个字符：拒绝生成采购订单";

    /// <summary>比价行状态在锁内已不再可转换时的受控拒绝文案（并发转换 / 改状态；批次跳过摘要同源）。</summary>
    public const string NotConvertibleText =
        "来源比价行在锁定后已不可转换（已被转换 / 已放弃 / 未选中）：本次转换整体未生效";

    // ==================== 1. 事务 / 锁 ====================

    /// <summary>关系型后端（SQL Server）判定：内存库等非关系型提供程序没有行锁语义，锁定与事务等价无操作。</summary>
    public static bool IsRelationalProvider(IErpDbContext db) => PurchaseQuoteMutationRules.IsRelationalProvider(db);

    /// <summary>
    /// 转换使用的原子事务（复用 ERP-417 比价生命周期 / 审批决定同一口径）：关系型后端开启真实事务，
    /// 失败整体回滚；调用方已开启事务时<strong>绝不嵌套</strong>（返回 <c>null</c> 表示复用外层事务）。
    /// </summary>
    public static Task<IDbContextTransaction?> BeginConversionTransactionAsync(
        IErpDbContext db, CancellationToken ct = default)
        => PurchaseQuoteMutationRules.BeginMutationTransactionAsync(db, ct);

    /// <summary>回滚 / 受控拒绝后丢弃变更跟踪器中的半成品变更，绝不残留部分写入（内存库同样生效）。</summary>
    public static void DiscardTrackedChanges(IErpDbContext db)
        => PurchaseQuoteMutationRules.DiscardTrackedChanges(db);

    /// <summary>合并需要加锁的 Id：去重、只保留正整数、按 Id 升序（与既有锁协议同一口径）。</summary>
    public static IReadOnlyList<long> MergeLockIds(IEnumerable<long>? ids)
        => PurchaseQuoteMutationRules.MergeLockIds(ids);

    // ==================== 2. 币种（唯一权威解析，绝不回退） ====================

    /// <summary>
    /// 币种文本 → <see cref="Currency"/> 枚举（去首尾空白、不区分大小写），并要求落在既有受支持口径
    /// （<see cref="PurchaseQuoteMutationRules.SupportedCurrencies"/>）内；无法识别一律返回 <c>false</c>，
    /// 绝不回退人民币。
    /// </summary>
    public static bool TryParseCurrency(string? text, out Currency currency)
    {
        currency = default;
        var value = (text ?? string.Empty).Trim();
        if (value.Length == 0) return false;
        if (!Enum.TryParse<Currency>(value, ignoreCase: true, out var parsed)) return false;
        // 拒绝数字 / 组合标志等非枚举名输入（Enum.TryParse 会接受 "0" 之类的纯数字文本）。
        if (!string.Equals(parsed.ToString(), value, StringComparison.OrdinalIgnoreCase)) return false;
        if (!PurchaseQuoteMutationRules.SupportedCurrencies.Contains(parsed.ToString(), StringComparer.Ordinal))
            return false;

        currency = parsed;
        return true;
    }

    /// <summary>币种文本必须可识别且受支持，否则抛 <see cref="ErrorCodes.InvalidParameter"/>（绝不回退人民币）。</summary>
    public static Currency RequireCurrency(string? text)
        => TryParseCurrency(text, out var currency)
            ? currency
            : throw BusinessException.InvalidParameter(UnknownCurrencyText);

    // ==================== 3. 归属销售订单解析（权威 + 受控） ====================

    /// <summary>
    /// 解析来源行 <see cref="PurchaseQuote.RefOrderNo" />（转换前语义 = 关联销售订单号）指向的权威销售订单：
    /// 返回 <c>null</c> 表示该文本不是任何既有销售订单号（历史语义允许，归属字段留空，不臆造关联）；
    /// 命中但已删除 / 已取消 / 未审核时受控拒绝（<b>绝不</b>静默生成无归属或错误归属的采购订单）。
    /// </summary>
    public static async Task<SalesOrder?> ResolveOwningSalesOrderAsync(IErpDbContext db, string? refOrderNo,
        CancellationToken ct = default)
    {
        var refNo = (refOrderNo ?? string.Empty).Trim();
        if (refNo.Length == 0) return null;

        // 已删除行也一并读取：用于区分「不是销售订单号」（留空）与「销售订单已删除」（受控拒绝）。
        var order = await db.SalesOrders.AsNoTracking()
            .Where(o => o.OrderNo == refNo)
            .OrderBy(o => o.Id)
            .FirstOrDefaultAsync(ct);
        if (order is null) return null;
        if (order.IsDeleted) throw BusinessException.RuleConflict(OwnershipUnavailableText);

        EnsureOwningSalesOrderUsable(order);
        return order;
    }

    /// <summary>归属销售订单作为采购归属来源的资格：已审核为唯一可用状态（已取消 / 未审核 / 已驳回一律拒绝）。</summary>
    public static void EnsureOwningSalesOrderUsable(SalesOrder order)
    {
        ArgumentNullException.ThrowIfNull(order);
        if (order.Status == DocumentStatus.Cancelled)
            throw BusinessException.RuleConflict(OwnershipCancelledText);
        if (!PurchaseSalesOrderLinkRules.IsEligibleNewSource(order.Status))
            throw BusinessException.RuleConflict(OwnershipNotApprovedText);
    }

    /// <summary>
    /// 批次内一次性解析全部归属销售订单（<see cref="PurchaseQuote.RefOrderNo" /> → 权威销售订单），
    /// 用于<b>确定性加锁顺序</b>（先归属销售订单、后来源比价行）。同一单号只解析一次，判定口径与单行完全一致。
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, SalesOrder>> ResolveOwningSalesOrdersAsync(
        IErpDbContext db, IEnumerable<string?> refOrderNos, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);

        var wanted = refOrderNos
            .Select(no => (no ?? string.Empty).Trim())
            .Where(no => no.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var result = new Dictionary<string, SalesOrder>(StringComparer.OrdinalIgnoreCase);
        if (wanted.Count == 0) return result;

        var orders = await db.SalesOrders.AsNoTracking()
            .Where(o => wanted.Contains(o.OrderNo))
            .OrderBy(o => o.Id)
            .ToListAsync(ct);
        foreach (var order in orders)
        {
            if (result.ContainsKey(order.OrderNo)) continue;      // 同号取最小 Id（与单行口径一致）
            if (order.IsDeleted) throw BusinessException.RuleConflict(OwnershipUnavailableText);
            EnsureOwningSalesOrderUsable(order);
            result[order.OrderNo] = order;
        }
        return result;
    }


    // ==================== 4. 锁定 + 锁内权威重读 ====================

    /// <summary>
    /// 单行 / 批次转换的权威来源快照：锁内重读的受跟踪比价行 + 权威归属销售订单。
    /// </summary>
    public sealed class ConversionSourceSet
    {
        /// <summary>请求的批次号（按行定位时为该行所属批次；未指定时为该批次首行批次号）。</summary>
        public string BatchNo { get; init; } = string.Empty;

        /// <summary>锁内重读的<b>受跟踪</b>权威来源行（按比价行 Id 升序，成员与请求逐字一致）。</summary>
        public IReadOnlyList<PurchaseQuote> Lines { get; init; } = Array.Empty<PurchaseQuote>();

        /// <summary>按来源行 Id 解析出的权威归属销售订单 Id（无归属时为 <c>null</c>）。</summary>
        public IReadOnlyDictionary<long, long?> OwningSalesOrderIdByLineId { get; init; }
            = new Dictionary<long, long?>();

        /// <summary>本次锁定并复核过的权威归属销售订单（按销售订单 Id）。</summary>
        public IReadOnlyDictionary<long, SalesOrder> OwningSalesOrders { get; init; }
            = new Dictionary<long, SalesOrder>();
    }

    /// <summary>
    /// <b>唯一转换加锁入口</b>（单行 / 批次共用）：
    /// 解析不可变批次成员 → 解析权威归属销售订单并按 Id 升序加锁 → 按比价行 Id 升序加锁 →
    /// 锁内重新读取受跟踪权威行 → 核对成员集合 / 批次号 / 归属解析逐字一致。
    /// 任一不满足即抛业务异常（调用方在同一事务内整体回滚，绝不写入）。
    /// </summary>
    /// <param name="db">数据上下文（已在调用方事务内）。</param>
    /// <param name="requestedLineIds">本次转换的不可变批次成员（客户端 / 控制器已解析的比价行 Id）。</param>
    /// <param name="expectedBatchNo">期望的批次号（按批次号转换时校验成员未被改写；为 <c>null</c> 时跳过）。</param>
    /// <param name="ct">取消令牌。</param>
    public static async Task<ConversionSourceSet> LockAndReloadAsync(IErpDbContext db,
        IReadOnlyCollection<long> requestedLineIds, string? expectedBatchNo, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);

        var ids = MergeLockIds(requestedLineIds);
        if (ids.Count == 0) throw BusinessException.InvalidParameter("请提供比价行 Id");

        // ① 锁定前快照（只读）：成员存在性、批次号与归属来源文本（用于锁定后逐字复核）。
        var snapshot = await db.PurchaseQuotes.AsNoTracking()
            .Where(q => ids.Contains(q.Id))
            .ToListAsync(ct);
        if (snapshot.Count != ids.Count || snapshot.Any(q => q.IsDeleted))
            throw BusinessException.NotFound("比价记录不存在");

        var expected = (expectedBatchNo ?? string.Empty).Trim();
        if (expected.Length > 0 && snapshot.Any(q =>
                !string.Equals((q.QuoteNo ?? string.Empty).Trim(), expected, StringComparison.Ordinal)))
            throw BusinessException.InvalidParameter($"比价行不属于比价批次 {expected}");

        var batchNo = expected.Length > 0
            ? expected
            : (snapshot.First(q => q.Id == ids[0]).QuoteNo ?? string.Empty).Trim();

        // ② 权威归属销售订单：先解析（受控校验删除 / 取消 / 未审核），再按 Id 升序确定性加锁。
        var owningByRefNo = await ResolveOwningSalesOrdersAsync(db, snapshot.Select(q => q.RefOrderNo), ct);
        var owningIds = MergeLockIds(owningByRefNo.Values.Select(o => o.Id));
        foreach (var owningId in owningIds)
            if (!await SalesOrderSourceLineageRules.LockSalesOrderRowAsync(db, owningId, ct))
                throw BusinessException.RuleConflict(OwnershipUnavailableText);

        // ③ 来源比价行锁（Id 升序，与 ERP-417 修改 / 删除 / 审批决定同一把锁）。
        await PurchaseQuoteMutationRules.LockQuoteRowsAsync(db, ids, ct);

        // ④ 锁内重读受跟踪权威行（不复用加锁前的内存副本）。
        var lines = await db.PurchaseQuotes
            .Where(q => ids.Contains(q.Id) && !q.IsDeleted)
            .OrderBy(q => q.Id)
            .ToListAsync(ct);
        if (lines.Count != ids.Count)
            throw BusinessException.RuleConflict(StaleSourceText);

        // ⑤ 成员 / 批次号 / 归属来源必须与锁定前一致（不可变批次成员语义）。
        //    例外：并发**转换**会把 RefOrderNo 写回生成的采购单号——这是受控的「重复转换」证据，
        //    交由既有重复生成守卫按「跳过（批次）/ 受控拒绝（单行）」处理，不视为成员漂移；
        //    其它漂移（改指向别的销售订单 / 改批次号）仍是成员不一致，一律受控拒绝。
        foreach (var line in lines)
        {
            var before = snapshot.First(q => q.Id == line.Id);
            if (!string.Equals((line.QuoteNo ?? string.Empty).Trim(), (before.QuoteNo ?? string.Empty).Trim(),
                    StringComparison.Ordinal))
                throw BusinessException.RuleConflict(MembershipChangedText);
            if (batchNo.Length > 0 && !string.Equals((line.QuoteNo ?? string.Empty).Trim(), batchNo,
                    StringComparison.Ordinal))
                throw BusinessException.RuleConflict(MembershipChangedText);

            var beforeRef = (before.RefOrderNo ?? string.Empty).Trim();
            var afterRef = (line.RefOrderNo ?? string.Empty).Trim();
            if (!string.Equals(beforeRef, afterRef, StringComparison.Ordinal)
                && !await IsCommittedConversionAsync(db, afterRef, ct))
                throw BusinessException.RuleConflict(MembershipChangedText);
        }

        var owningIdByLineId = new Dictionary<long, long?>();
        foreach (var line in lines)
        {
            var refNo = (line.RefOrderNo ?? string.Empty).Trim();
            owningIdByLineId[line.Id] = refNo.Length > 0 && owningByRefNo.TryGetValue(refNo, out var order)
                ? order.Id
                : null;
        }

        return new ConversionSourceSet
        {
            BatchNo = batchNo,
            Lines = lines,
            OwningSalesOrderIdByLineId = owningIdByLineId,
            OwningSalesOrders = owningByRefNo.Values
                .GroupBy(o => o.Id)
                .ToDictionary(g => g.Key, g => g.First())
        };
    }


    // ==================== 5. 锁内主数据 / 资格复核 ====================

    /// <summary>
    /// 该 <c>RefOrderNo</c> 是否指向**已提交的转换证据**（既有未删除采购订单号）：
    /// 是则说明来源行已被并发转换（由既有重复生成守卫按受控重复处理），不是「成员漂移」。
    /// </summary>
    private static async Task<bool> IsCommittedConversionAsync(IErpDbContext db, string? refOrderNo,
        CancellationToken ct = default)
    {
        var refNo = (refOrderNo ?? string.Empty).Trim();
        if (refNo.Length == 0) return false;
        return await db.PurchaseOrders.AsNoTracking()
            .AnyAsync(o => o.OrderNo == refNo && !o.IsDeleted, ct);
    }

    /// <summary>
    /// 锁内主数据与单位复核（绝不采信比价行快照文本）：供应商必须已维护且在库档案未停用 / 未删除；
    /// 商品档案被引用时同样必须在库且未停用 / 未删除；单位必须有值且在列长内。
    /// 只在档案<b>确实存在且无效</b>时拒绝，历史未引用档案的比价行保持既有可转换口径。
    /// </summary>
    public static async Task EnsureMasterReferencesAsync(IErpDbContext db, IReadOnlyList<PurchaseQuote> lines,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(lines);

        var supplierIds = lines.Where(l => l.SupplierId is > 0).Select(l => l.SupplierId!.Value)
            .Distinct().ToList();
        var productIds = lines.Where(l => l.ProductId is > 0).Select(l => l.ProductId!.Value)
            .Distinct().ToList();

        var suppliers = supplierIds.Count == 0
            ? new List<BaseSupplier>()
            : await db.BaseSuppliers.AsNoTracking().Where(s => supplierIds.Contains(s.Id)).ToListAsync(ct);
        var products = productIds.Count == 0
            ? new List<BaseProduct>()
            : await db.BaseProducts.AsNoTracking().Where(p => productIds.Contains(p.Id)).ToListAsync(ct);

        foreach (var line in lines)
        {
            if (line.SupplierId is null or <= 0)
                throw BusinessException.RuleConflict("比价行未维护供应商，不能生成采购订单");

            var supplier = suppliers.FirstOrDefault(s => s.Id == line.SupplierId);
            if (supplier is not null && (supplier.IsDeleted || supplier.Status != 1))
                throw BusinessException.RuleConflict(
                    $"{SupplierMasterText}（比价行 #{line.Id} / 供应商 {line.SupplierId}）");

            var unit = (line.Unit ?? string.Empty).Trim();
            if (unit.Length == 0 || unit.Length > 20)
                throw BusinessException.RuleConflict($"{UnitText}（比价行 #{line.Id}）");

            if (line.ProductId is not > 0) continue;
            var product = products.FirstOrDefault(p => p.Id == line.ProductId);
            if (product is not null && (product.IsDeleted || product.Status != 1))
                throw BusinessException.RuleConflict(
                    $"{ProductMasterText}（比价行 #{line.Id} / 商品 {line.ProductId}）");
        }
    }

    /// <summary>
    /// 锁内转换资格复核（与既有单行守卫<b>逐字同口径</b>，只在锁内<b>重新</b>执行）：
    /// 币种可识别（<b>绝不回退人民币</b>）且单位有值。
    /// <para>其余权威资格（未删除 / 已选中 / 未放弃 / 未转换 / 供应商已维护 / 已批准决定 / 数量与单价 /
    /// 归属来源可用性）紧随其后由 <c>PurchaseQuoteConversion.BuildDraftAsync</c> 在<b>同一事务、同一受跟踪权威行</b>
    /// 上重新执行（发号与写入之前），因此本方法只补足该口径未覆盖的币种与单位检查，绝不复制一套守卫文案。</para>
    /// </summary>
    public static void EnsureEligibleForConversion(PurchaseQuote line)
    {
        ArgumentNullException.ThrowIfNull(line);

        RequireCurrency(line.Currency);   // 无法识别即拒绝（绝不回退 CNY）

        var unit = (line.Unit ?? string.Empty).Trim();
        if (unit.Length == 0 || unit.Length > 20)
            throw BusinessException.RuleConflict($"{UnitText}（比价行 #{line.Id}）");
    }

    /// <summary>
    /// 锁内的一次性复核入口（单行 / 批次共用）：
    /// <list type="bullet">
    /// <item><b>币种（全部请求行）</b>：任一来源行币种无法识别即整次受控拒绝（<b>绝不回退人民币</b>，
    /// 也绝不静默跳过——币种决定分组与金额口径，静默跳过会造成会计口径漂移）。</item>
    /// <item><b>资格 + 主数据（本次将转换的行 = <paramref name="convertibleLineIds"/>）</b>：
    /// 不合格行按批次既有语义显式跳过（不计入本复核，也不会被写入）。</item>
    /// </list>
    /// </summary>
    /// <param name="db">数据上下文（已在调用方事务内）。</param>
    /// <param name="source">锁内权威来源快照。</param>
    /// <param name="convertibleLineIds">本次将要转换的比价行 Id（<c>null</c> = 全部来源行）。</param>
    /// <param name="ct">取消令牌。</param>
    public static async Task EnsureConversionReadyAsync(IErpDbContext db, ConversionSourceSet source,
        IReadOnlyCollection<long>? convertibleLineIds = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(source);

        foreach (var line in source.Lines)
            if (!TryParseCurrency(line.Currency, out _))
                throw BusinessException.InvalidParameter($"{UnknownCurrencyText}（比价行 #{line.Id}）");

        var convertible = convertibleLineIds is null
            ? source.Lines
            : source.Lines.Where(l => convertibleLineIds.Contains(l.Id)).ToList();
        if (convertible.Count == 0) return;

        foreach (var line in convertible) EnsureEligibleForConversion(line);
        await EnsureMasterReferencesAsync(db, convertible, ct);
    }
}
