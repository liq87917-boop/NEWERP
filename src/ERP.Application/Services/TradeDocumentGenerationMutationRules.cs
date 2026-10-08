using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace ERP.Application.Services;

/// <summary>
/// 来源单据（销售订单 / 装柜清单）→ 单证中心「直接生成」的**确定性来源行锁 + 原子事务 + 锁内权威复核**护栏（ERP-397）。
/// <list type="number">
/// <item><b>来源行锁</b>：生成入口在重复检测、单证编号预约、表头与明细行构造与写入**之前**，先对来源单据行取得排它行锁
/// （销售订单行 / 装柜清单行），取得方式与 ERP-395 父单证行锁同源（仅用 EF Core 基础 API 的「审计时间戳刷新」<c>UPDATE</c>，
/// 语义等价 <c>SELECT ... WITH (UPDLOCK, HOLDLOCK)</c>，持有至事务结束）；同一来源的并发等价请求被串行化，
/// 与既有「来源取消 / 编辑 / 删除」共用同一把来源行锁（<see cref="SalesOrderRowLockSql"/> 与既有
/// <see cref="PreLoadingSalesOrderLinkRules.LockSalesOrderRowSql"/> 同源；<see cref="LoadingListRowLockSql"/> 与装柜清单写路由同一把行锁）。</item>
/// <item><b>锁序</b>：来源单据行 → 新建单证行（<c>INSERT</c>，不取既有单证行锁）。来源生成绝不反向获取父单证行锁，
/// ERP-395 的表头 / 明细行写路由也绝不获取来源行锁，因此两者无锁环；销售订单取消 / 装柜清单取消（先预装柜单行、
/// 后装柜清单行）与来源生成同为「来源行先行」，同样不构成环。</item>
/// <item><b>锁内权威复核</b>：取得来源行锁之后必须由调用方**重新读取来源单据**（不是复用加锁前的内存实体）并返回
/// <see cref="TradeDocumentGenerationSource"/>；本类复核来源状态（已作废 fail closed）、来源客户实时范围、
/// 表头金额与明细行数量 / 单位是否与该次权威重读一致（不一致即原子拒绝，绝不写入陈旧证据）。</item>
/// <item><b>原子回滚</b>：重复生成、类型 / 明细行非法、权限越界、数据库写入失败等任一步失败都整体回滚，
/// 表头、明细行与已预约的单证编号证据（<c>DocNo</c>）一起回滚，绝不留下半成品单证、孤儿明细行或已占用编号。</item>
/// </list>
/// <para><b>边界</b>：不新增表 / 列 / 索引 / 菜单 / 角色 / 用户授权，不把空身份当作匿名或管理员，也不伪造任何授权；
/// 生成仍只新增单证与明细行快照，绝不改写来源单据的商业字段、商品资料、库存与库存流水、发票 / 费用 / 财务记录；
/// 行锁只刷新来源单据的技术审计时间戳 <c>UpdatedAt</c>（非商业证据，不参与任何金额 / 数量 / 状态判定）。</para>
/// <para>历史归属一律只按持久化外键 / 权威字段判定，绝不按 <c>RefNo</c> / <c>SalesOrderNo</c> 等自由文本推断。</para>
/// </summary>
public static class TradeDocumentGenerationMutationRules
{
    /// <summary>来源单据类型：销售订单（与 <c>TradeDocumentGeneration.SalesOrderSourceType</c> 必须一致）</summary>
    public const string SalesOrderSourceType = "SalesOrder";

    /// <summary>来源单据类型：装柜清单（与 <c>TradeDocumentGeneration.LoadingListSourceType</c> 必须一致）</summary>
    public const string LoadingListSourceType = "LoadingList";

    /// <summary>
    /// 来源销售订单行锁语句（与销售订单取消 / 出库审核 / 预装柜流程共用**同一把**来源行锁，契约同源）。
    /// 等价说明见 <see cref="LockSourceRowAsync"/>（实际执行走审计时间戳刷新 UPDATE，仅用 EF Core 基础 API）。
    /// </summary>
    public const string SalesOrderRowLockSql = PreLoadingSalesOrderLinkRules.LockSalesOrderRowSql;

    /// <summary>
    /// 来源装柜清单行锁语句（与装柜清单修改 / 提交 / 审核 / 取消 / 删除 / 参与方维护共用**同一把**清单行锁）。
    /// </summary>
    public const string LoadingListRowLockSql =
        "SELECT Id FROM db_owner.ContainerLoadingLists WITH (UPDLOCK, HOLDLOCK) WHERE Id = {0}";

    /// <summary>来源行锁 + 锁序口径文案（接口 / 文档同源）。</summary>
    public const string LockOrderText =
        "来源生成为「确定性来源行锁 + 原子事务 + 锁内权威复核」：先对来源销售订单行 / 装柜清单行取得 UPDLOCK, HOLDLOCK 语义的行锁" +
        "（与销售订单取消 / 出库审核 / 预装柜流程、装柜清单写路由共用同一把来源行锁），再在锁内权威重读来源、" +
        "做重复生成检测、预约单证编号并构造表头与明细行，最后同一事务写入；" +
        "锁序固定为「来源单据行 → 新建单证行（INSERT，无既有行锁）」，与 ERP-395 父单证行锁互不反向获取，无锁环。";

    /// <summary>边界文案（不新增权限 / 表列，不改写来源与历史证据，不做财务 / 库存过账）。</summary>
    public const string BoundaryText =
        "本护栏只保护「来源单据 → 单证」生成的并发完整性：不新增菜单 / 角色 / 用户授权或表结构，不把空身份当作管理员；" +
        "不改写来源销售订单 / 装柜清单的商业字段、商品资料、库存与库存流水、发票、退税、费用或财务记录；" +
        "不改写既有明细行快照，也不按 RefNo / SalesOrderNo 等自由文本推断历史归属；" +
        "行锁只刷新技术审计时间戳 UpdatedAt（非商业证据），被拒绝的请求不改写任何业务字段与历史证据。";

    /// <summary>来源单据在锁内权威重读时已不存在 / 已被并发删除的拒绝文案。</summary>
    public const string SourceMissingText =
        "来源单据不存在或已删除：不能生成单证（并发删除已先行提交，fail closed，不产生任何单证与明细行）";

    /// <summary>
    /// 锁内权威重读结果与本次生成请求（来源类型 / 来源 Id）不一致时的拒绝文案：
    /// 绝不把「另一个来源」的权威快照当作本次生成依据。
    /// </summary>
    public const string SourceMismatchText =
        "锁内权威重读的来源单据与本次生成请求不一致：拒绝生成（fail closed，不产生任何单证与明细行）";

    /// <summary>锁内复核发现表头 / 明细行未取自本次权威重读（陈旧金额 / 客户 / 数量 / 单位）时的拒绝文案。</summary>
    public const string SourceChangedText =
        "来源单据在生成过程中已被并发改写：本次生成未采用锁内权威值，已整体回滚（fail closed，" +
        "不产生任何单证、明细行或已占用编号）";

    /// <summary>来源行锁重试耗尽（并发方持续改写同一来源行）的对外文案。</summary>
    public const string ConcurrentGenerationText =
        "来源单据正在被并发修改，本次生成未生效：请刷新后重试（原始证据均未改变）";

    /// <summary>行锁重试次数（乐观并发令牌过期时重读权威行后有界重试；行锁语义 = 阻塞后成功）。</summary>
    private const int LockRetryAttempts = TradeDocumentMutationRules.RowLockRetryAttempts;

    // ==================== 1. 确定性锁定键与锁语句 ====================

    /// <summary>来源单据类型归一化（去首尾空白；未知类型原样返回，由调用方 fail closed）。</summary>
    public static string NormalizeSourceType(string? sourceType) => (sourceType ?? string.Empty).Trim();

    /// <summary>来源单据业务名称（错误提示文案）；与既有生成口径一致（非销售订单一律按装柜清单）。</summary>
    public static string SourceLabel(string? sourceType)
        => NormalizeSourceType(sourceType) == SalesOrderSourceType ? "销售订单" : "装柜清单";

    /// <summary>
    /// 确定性**来源行锁键**（同一来源类型 + 同一来源 Id 恒等）：并发等价请求据此识别同一个被锁来源。
    /// </summary>
    public static string SourceLockKey(string? sourceType, long sourceId)
        => $"{NormalizeSourceType(sourceType)}#{sourceId}";

    /// <summary>
    /// 确定性**单证生成键**（来源类型 + 来源 Id + 单证类型）：同一「来源 + 类型」只允许生成一套完整单证。
    /// </summary>
    public static string GenerationKey(string? sourceType, long sourceId, string? docType)
        => $"{NormalizeSourceType(sourceType)}#{sourceId}#{(docType ?? string.Empty).Trim()}";

    /// <summary>来源单据类型对应的既有行锁语句（销售订单 / 装柜清单）；未知类型按参数错误拒绝。</summary>
    public static string SourceRowLockSql(string? sourceType) => NormalizeSourceType(sourceType) switch
    {
        SalesOrderSourceType => SalesOrderRowLockSql,
        LoadingListSourceType => LoadingListRowLockSql,
        _ => throw BusinessException.InvalidParameter($"不支持的来源单据类型：{NormalizeSourceType(sourceType)}")
    };

    /// <summary>来源已作废（已取消）时的拒绝文案（与既有生成守卫文案同源）。</summary>
    public static string SourceCancelledText(string? sourceType)
        => $"已作废的{SourceLabel(sourceType)}不能生成单证";

    // ==================== 2. 原子事务与来源行锁 ====================

    /// <summary>
    /// 生成使用的原子事务：复用既有 <see cref="TradeDocumentMutationRules.BeginMutationTransactionAsync"/> 口径
    /// （关系型后端开启真实事务；已存在事务时绝不嵌套，由最外层提交 / 回滚；内存库等价无事务）。
    /// </summary>
    public static Task<IDbContextTransaction?> BeginGenerationTransactionAsync(
        IErpDbContext db, CancellationToken ct = default)
        => TradeDocumentMutationRules.BeginMutationTransactionAsync(db, ct);

    /// <summary>
    /// 对**来源单据行**加排它行锁（销售订单行 / 装柜清单行），把「同一来源的并发生成」与「来源取消 / 编辑 / 删除」
    /// 串行化在同一事务内。返回 <c>false</c> 表示来源行不存在 / 已被并发删除。
    /// <para>实现与 ERP-395 父单证行锁同源（仅用 EF Core 基础 API，不依赖关系型扩展）：在调用方事务内对来源行发一条
    /// 「审计时间戳刷新」<c>UPDATE</c>（<c>UpdatedAt</c> 为技术审计字段、不是商业证据），取得排它行锁（X 锁，持有至
    /// 事务结束），语义等价 <see cref="SalesOrderRowLockSql"/> / <see cref="LoadingListRowLockSql"/> 的
    /// <c>SELECT ... WITH (UPDLOCK, HOLDLOCK)</c>。并发方先提交会使本地乐观令牌 <c>RowVersion</c> 过期时，
    /// 重读权威行后**有界重试**，绝不把纯粹锁等待误报成业务拒绝。</para>
    /// <para>内存库等非关系型提供程序无行锁语义，直接返回 <c>true</c>（存在性仍由锁内权威重读判定）。</para>
    /// </summary>
    public static async Task<bool> LockSourceRowAsync(
        IErpDbContext db, string sourceType, long sourceId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (sourceId <= 0) return false;
        // 未知来源类型必须在任何写入之前 fail closed（绝不加错锁 / 放行）。
        _ = SourceRowLockSql(sourceType);
        if (!TradeDocumentMutationRules.IsRelationalProvider(db)) return true;

        for (var attempt = 1; attempt <= LockRetryAttempts; attempt++)
        {
            if (!await TouchSourceRowAsync(db, sourceType, sourceId, ct)) return false;

            try
            {
                await db.SaveChangesAsync(ct);
                return true;
            }
            catch (DbUpdateConcurrencyException ex)
            {
                foreach (var entry in ex.Entries)
                {
                    try
                    {
                        await entry.ReloadAsync(ct);
                    }
                    catch (DbUpdateConcurrencyException)
                    {
                        return false; // 来源行已被并发事务删除
                    }
                }

                if (attempt == LockRetryAttempts)
                    throw BusinessException.RuleConflict(ConcurrentGenerationText);
            }
        }

        return false;
    }

    /// <summary>装载来源行（有界、未删除）并刷新技术审计时间戳以取得排它行锁；不存在时返回 <c>false</c>。</summary>
    private static async Task<bool> TouchSourceRowAsync(
        IErpDbContext db, string sourceType, long sourceId, CancellationToken ct)
    {
        switch (NormalizeSourceType(sourceType))
        {
            case SalesOrderSourceType:
            {
                var order = await db.SalesOrders
                    .FirstOrDefaultAsync(o => o.Id == sourceId && !o.IsDeleted, ct);
                if (order is null) return false;
                order.UpdatedAt = DateTime.Now;
                return true;
            }
            case LoadingListSourceType:
            {
                var list = await db.ContainerLoadingLists
                    .FirstOrDefaultAsync(o => o.Id == sourceId && !o.IsDeleted, ct);
                if (list is null) return false;
                list.UpdatedAt = DateTime.Now;
                return true;
            }
            default:
                throw BusinessException.InvalidParameter($"不支持的来源单据类型：{NormalizeSourceType(sourceType)}");
        }
    }

    // ==================== 3. 锁内权威复核 ====================

    /// <summary>
    /// 锁内权威重读结果的身份与状态复核：来源类型 / Id 必须与本次生成请求一致（不一致 fail closed），
    /// 已作废来源一律拒绝（与生成前预检同口径，且以锁内权威状态为准）。
    /// </summary>
    public static void EnsureSourceStable(
        TradeDocumentGenerationSource source, string? expectedSourceType, long expectedSourceId)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (source.SourceId != expectedSourceId
            || !string.Equals(NormalizeSourceType(source.SourceType), NormalizeSourceType(expectedSourceType),
                StringComparison.Ordinal))
            throw BusinessException.NotFound(SourceMismatchText);

        if (source.Status == DocumentStatus.Cancelled)
            throw BusinessException.RuleConflict(SourceCancelledText(source.SourceType));
    }

    /// <summary>
    /// 锁内来源客户实时范围复核（锁定实时身份口径）：受限账号的来源缺失权威客户归属或越界一律 fail closed，
    /// 与生成前的来源范围预检同口径 —— 并发「来源客户改写」不会让生成落到范围外客户。
    /// </summary>
    public static void EnsureSourceScopeAllowed(SalespersonDataScope? scope, TradeDocumentGenerationSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        TradeDocumentAuthorizationRules.EnsureLockedSourceScopeAllowed(scope, source.CustomerId);
    }

    /// <summary>
    /// 拟议表头必须取自本次锁内权威重读：客户归属（持久化 <c>CustomerId</c>，绝不按自由文本推断）与
    /// 金额（销售订单总额 / 装柜清单固定 0）必须与权威快照一致，否则判定为陈旧证据并原子拒绝。
    /// </summary>
    public static void EnsureDraftMatchesSource(TradeDocumentGenerationSource source, TradeDocument draft)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(draft);

        var expectedCustomerId = source.CustomerId is > 0 ? source.CustomerId : null;
        if (draft.CustomerId != expectedCustomerId)
            throw BusinessException.RuleConflict(SourceChangedText);

        if (source.ExpectedAmount.HasValue && draft.Amount != source.ExpectedAmount.Value)
            throw BusinessException.RuleConflict(SourceChangedText);
    }

    /// <summary>
    /// 明细行快照必须取自本次锁内权威重读：行数量合计与每行单位都必须落在权威来源明细（含引用商品资料单位）的
    /// 证据集合内；不一致即判定为陈旧 / 撕裂证据并原子拒绝（行集合为空 = 该单证类型不带明细行，不做校验）。
    /// </summary>
    public static void EnsureLinesMatchSource(
        TradeDocumentGenerationSource source, TradeDocumentLineSnapshotResult lines)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(lines);
        if (lines.Lines.Count == 0) return;

        if (source.ExpectedLineQuantityTotal.HasValue
            && lines.Lines.Sum(line => line.Quantity) != source.ExpectedLineQuantityTotal.Value)
            throw BusinessException.RuleConflict(SourceChangedText);

        var units = source.AuthoritativeUnits;
        if (units is not { Count: > 0 }) return;

        foreach (var line in lines.Lines)
        {
            var unit = (line.Unit ?? string.Empty).Trim();
            if (unit.Length > 0 && !units.Contains(unit))
                throw BusinessException.RuleConflict(SourceChangedText);
        }
    }

    // ==================== 4. 权威快照事实（供锁内复核，全部来自本次权威重读） ====================

    /// <summary>
    /// 来源明细的权威**数量合计**：与明细行快照同口径（<see cref="TradeDocumentLineSnapshotRules.NormalizeQuantity"/>，
    /// 非法数量按 0 计；非法数量会在构造明细行时先行 fail closed）。
    /// </summary>
    public static decimal LineQuantityTotal(IEnumerable<decimal>? quantities)
        => (quantities ?? Enumerable.Empty<decimal>())
            .Sum(quantity => TradeDocumentLineSnapshotRules.NormalizeQuantity(quantity) ?? 0m);

    /// <summary>
    /// 来源明细的权威**单位集合**：明细自身单位 + 其引用商品资料单位的规范化集合（清洗 / 截断口径与明细行快照同源）；
    /// 来源未提供单位为空的明细不产生单位项（快照同样留空，不臆造）。
    /// </summary>
    public static IReadOnlyCollection<string> LineUnits(
        IEnumerable<(long ProductId, string? Unit)>? lines,
        IReadOnlyDictionary<long, BaseProduct>? products)
    {
        var units = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (productId, unit) in lines ?? Enumerable.Empty<(long, string?)>())
        {
            var detailUnit = TradeDocumentLineSnapshotRules.SanitizeUnit(unit);
            if (detailUnit.Length > 0) units.Add(detailUnit);

            if (productId <= 0 || products is null || !products.TryGetValue(productId, out var product)
                || product is null || product.IsDeleted)
                continue;

            var productUnit = TradeDocumentLineSnapshotRules.SanitizeUnit(product.Unit);
            if (productUnit.Length > 0) units.Add(productUnit);
        }

        return units;
    }
}

/// <summary>
/// 锁内**权威重读**的来源单据快照（ERP-397）：携带来源身份与权威商业事实，以及**绑定到本次重读实体**的
/// 单证草稿 / 明细行快照构造委托。
/// <para>由调用方（<c>SalesOrderController</c> / <c>ContainerLoadingListController</c>）在取得来源行锁之后、
/// 在同一事务内重新读取来源单据（含客户档案、来源明细、商品资料）后构造；<b>绝不</b>复用加锁前的内存实体，
/// 也<b>绝不</b>按 <c>RefNo</c> / <c>SalesOrderNo</c> 等自由文本推断来源归属。</para>
/// </summary>
public sealed class TradeDocumentGenerationSource
{
    /// <summary>来源单据类型（<c>SalesOrder</c> / <c>LoadingList</c>）</summary>
    public string SourceType { get; init; } = string.Empty;

    /// <summary>来源单据 Id</summary>
    public long SourceId { get; init; }

    /// <summary>来源单据号（销售订单号 / 装柜清单号；单证编号与结果回显的权威来源）</summary>
    public string SourceNo { get; init; } = string.Empty;

    /// <summary>来源柜号（装柜清单来源；销售订单来源为 <c>null</c>）</summary>
    public string? ContainerNo { get; init; }

    /// <summary>重复生成检测用的销售订单号（销售订单来源；其它来源为 <c>null</c>）</summary>
    public string? SalesOrderNo { get; init; }

    /// <summary>重复生成检测用的装柜清单号（装柜清单来源；其它来源为 <c>null</c>）</summary>
    public string? LoadingListNo { get; init; }

    /// <summary>锁内权威客户归属（持久化 <c>CustomerId</c>；0 表示来源未维护客户，生成客户字段留空）</summary>
    public long? CustomerId { get; init; }

    /// <summary>锁内权威来源状态</summary>
    public DocumentStatus Status { get; init; }

    /// <summary>锁内权威金额预期（销售订单 = 订单总额；装柜清单 = 0；<c>null</c> = 不做金额复核）</summary>
    public decimal? ExpectedAmount { get; init; }

    /// <summary>锁内权威明细数量合计预期（<c>null</c> = 不做数量复核）</summary>
    public decimal? ExpectedLineQuantityTotal { get; init; }

    /// <summary>锁内权威单位集合（<c>null</c> / 空集 = 不做单位复核）</summary>
    public IReadOnlyCollection<string>? AuthoritativeUnits { get; init; }

    /// <summary>绑定到本次权威重读实体的单证草稿构造（与既有字段映射同源）</summary>
    public Func<string, TradeDocument> BuildDraft { get; init; } = _ =>
        throw new InvalidOperationException("来源快照缺少单证草稿构造委托。");

    /// <summary>绑定到本次权威重读实体的明细行快照构造（<c>null</c> = 本次生成不带明细行）</summary>
    public Func<string, TradeDocumentLineSnapshotResult>? BuildLines { get; init; }
}
