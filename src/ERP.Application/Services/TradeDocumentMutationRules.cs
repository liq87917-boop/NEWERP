using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace ERP.Application.Services;

/// <summary>
/// 单证台账（<see cref="TradeDocument"/>，<c>api/trade/documents</c>）表头生命周期与明细行变更的共享并发护栏（ERP-395）。
/// <para><b>协议</b>：明细行新增 / 修改 / 删除，与表头状态 / 商业字段修改 / 软删除 / 批量删除，
/// 全部落在一个<strong>共享父单证行锁</strong>（<c>SELECT Id FROM db_owner.TradeDocuments WITH (UPDLOCK, HOLDLOCK)</c>）
/// 与同一原子事务内：并发时只能得到唯一一致结果（确定性赢家）或显式原子拒绝（合法冲突输家），
/// 绝不允许「后到的明细行写入」在父单证已被删除 / 冻结（已提交客户 / 已使用）之后落库。</para>
/// <para><b>锁序</b>：先按路由 / 明细行绑定有界读取解析父单证 Id，再对父单证行加锁；
/// 批量删除时父单证行一律按 <b>Id 升序</b>确定性获取（去重 + 仅正整数），绝不反向获取其它锁。</para>
/// <para><b>锁内复核</b>：取得父行锁之后必须重新加载父单证并复核 —— 存在 / 未删除、权威客户范围（实时身份）、
/// 允许的单证类型（商业发票 / 装箱单）、可维护状态（待制作 / 已制作）、重复行序、服务端数量 / 金额 / 单位规则；
/// 未知状态与冻结状态一律 fail closed（拒绝而不放行、不改写任何行）。</para>
/// <para><b>边界</b>：本类只做锁定、事务与状态 / 商业字段判定；不新增表 / 列 / 菜单 / 角色 / 用户授权，
/// 不伪造任何授权，也绝不把空身份当作匿名或管理员；不改写库存、来源单据、明细行快照与既有差异提示口径，
/// 不做表头金额自动重算，也不做财务 / 库存过账。</para>
/// </summary>
public static class TradeDocumentMutationRules
{
    /// <summary>
    /// 共享父单证行锁的等价 T-SQL 形式（契约 / 文档同源）：<c>UPDLOCK, HOLDLOCK</c> 语义等价可串行化行锁，
    /// 持有至调用方事务结束。实际执行走 <see cref="LockDocumentRowAsync"/> 的「审计时间戳刷新」UPDATE
    /// （仅用 EF Core 基础 API，语义与下表提示一致），全部表头生命周期写路由与明细行写路由共用同一把锁。
    /// </summary>
    public const string LockDocumentRowSql =
        "SELECT Id FROM db_owner.TradeDocuments WITH (UPDLOCK, HOLDLOCK) WHERE Id = {0}";

    /// <summary>锁序口径文案（接口 / 文档同源）。</summary>
    public const string LockOrderText =
        "ERP-395 共享父单证行锁：明细行新增 / 修改 / 删除与表头状态 / 商业字段修改 / 软删除 / 批量删除都先取" +
        "「父单证行锁」（db_owner.TradeDocuments WITH (UPDLOCK, HOLDLOCK)），把同单并发写串行化在同一原子事务内；" +
        "批量删除按父单证 Id 升序确定性加锁（去重、仅正整数），绝不反向获取其它锁。";

    /// <summary>边界文案（不新增权限 / 表列，不改写快照与来源，不做自动重算 / 过账）。</summary>
    public const string BoundaryText =
        "本护栏只保护单证表头生命周期与明细行变更的并发完整性：不新增菜单 / 角色 / 用户授权或表结构，也不把空身份当作管理员；" +
        "不改写商品资料、销售订单、采购订单、装柜清单、库存与库存流水、发票、退税、费用或财务记录；" +
        "不改写明细行历史商品快照，不自动重算 / 改写表头金额（既有差异只作提示），不做任何财务 / 库存过账。";

    /// <summary>未知状态 fail closed 的拒绝文案。</summary>
    public const string UnknownStatusText =
        "单证状态不在已知口径内（待制作 / 已制作 / 已提交客户 / 已使用）：拒绝变更（fail closed，不改写任何行）";

    /// <summary>持久化状态未知时 fail closed 的拒绝文案。</summary>
    public const string UnknownStoredStatusText =
        "该单证持久化状态不在已知口径内：拒绝变更（fail closed，不改写任何行，请先人工核对单证状态）";

    /// <summary>冻结状态下修改商业字段 fail closed 的拒绝文案。</summary>
    public const string FrozenHeaderText =
        "单证已提交客户 / 已使用（冻结状态）：商业字段不能再修改（fail closed，不改写任何行；仅允许向已使用前进的状态变更）";

    /// <summary>状态回退 fail closed 的拒绝文案。</summary>
    public const string BackwardStatusText =
        "单证状态不能回退（只允许 待制作 → 已制作 → 已提交客户 → 已使用 前进）：拒绝本次修改（fail closed，不改写任何行）";

    /// <summary>父单证在锁内已被删除 / 不存在时明细行写入的拒绝文案。</summary>
    public const string ParentDeletedText =
        "父单证不存在或已删除：明细行不能写入（并发删除已先行提交，fail closed，不改写任何行）";

    /// <summary>行锁重试耗尽（并发方持续改写同一行）的对外文案。</summary>
    public const string ConcurrentMutationText =
        "目标单证正在被并发修改，本次操作未生效：请刷新后重试（原始证据均未改变）";

    /// <summary>状态前进序：待制作（0）→ 已制作（1）→ 已提交客户（2）→ 已使用（3）。</summary>
    private static readonly IReadOnlyDictionary<string, int> StatusOrder =
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            [TradeDocumentItemRules.StatusPreparing] = 0,
            [TradeDocumentItemRules.StatusPrepared] = 1,
            [TradeDocumentItemRules.StatusSubmitted] = 2,
            [TradeDocumentItemRules.StatusUsed] = 3,
        };

    /// <summary>行锁重试次数（乐观并发令牌过期时重读权威行后有界重试；行锁语义 = 阻塞后成功）</summary>
    private const int LockRetryAttempts = 8;

    // ==================== 1. 关系型判定 / 事务 ====================

    /// <summary>关系型后端（SQL Server）判定：内存库等非关系型提供程序没有行锁语义，锁定与事务等价无操作。</summary>
    public static bool IsRelationalProvider(IErpDbContext db)
    {
        ArgumentNullException.ThrowIfNull(db);
        return string.Equals(db.Database.ProviderName, "Microsoft.EntityFrameworkCore.SqlServer",
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 表头 / 明细行变更使用的原子事务：关系型后端开启真实事务（失败整体回滚，绝不留半成品行，
    /// 也不会「消费」已计算的序号），非关系型提供程序返回 <c>null</c>（等价无事务，声明式校验不变）。
    /// <para>调用方已经开启事务时<strong>绝不嵌套</strong>：直接复用既有事务与锁，由最外层调用方提交 / 回滚；
    /// 此时返回 <c>null</c> 表示「复用既有事务，本层不提交」。</para>
    /// </summary>
    public static async Task<IDbContextTransaction?> BeginMutationTransactionAsync(
        IErpDbContext db, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (!IsRelationalProvider(db)) return null;
        if (db.Database.CurrentTransaction is not null) return null;

        return await db.Database.BeginTransactionAsync(ct);
    }

    /// <summary>
    /// 合并需要加锁的父单证 Id：去重、只保留正整数，并按 Id 升序返回，保证多把行锁的确定性获取顺序
    /// （绝不反向获取，也就不存在锁环）。
    /// </summary>
    public static IReadOnlyList<long> MergeDocumentLockIds(IEnumerable<long>? ids)
        => (ids ?? Array.Empty<long>())
            .Where(id => id > 0)
            .Distinct()
            .OrderBy(id => id)
            .ToList();

    /// <summary>
    /// 对父单证行加排它行锁：把同单并发的「明细行新增 / 修改 / 删除」与「表头状态 / 商业字段修改 /
    /// 软删除 / 批量删除」串行化在同一事务内。返回 <c>false</c> 表示该行不存在 / 已被并发删除。
    /// <para><b>实现</b>（与 <see cref="ExpenseAllocationConcurrencyRules"/> 同一既有口径，仅用 EF Core 基础 API，
    /// 不依赖关系型扩展）：在调用方事务内对目标行发一条「审计时间戳刷新」的 UPDATE，取得排它行锁（X 锁，
    /// 持有至事务结束），语义等价于 <see cref="LockDocumentRowSql"/> 的 <c>SELECT ... WITH (UPDLOCK, HOLDLOCK)</c>；
    /// <c>UpdatedAt</c> 是技术审计字段、不是商业证据，因此不构成对单证表头金额 / 状态 / 客户等的静默改写。</para>
    /// <para>并发方先提交时本地的乐观并发令牌（<c>RowVersion</c>）会过期，UPDATE 影响 0 行并触发
    /// <see cref="DbUpdateConcurrencyException"/>；行锁语义应为「阻塞后成功」，因此这里重读权威行（含新令牌）
    /// 后有界重试，绝不把纯粹的锁等待误报成业务拒绝。重读后调用方必须再次加载权威行做锁内复核。</para>
    /// <para>内存库等非关系型提供程序无行锁语义，直接返回 <c>true</c>（存在性仍由锁内重新加载判定）。</para>
    /// </summary>
    public static async Task<bool> LockDocumentRowAsync(IErpDbContext db, long documentId)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (documentId <= 0) return false;
        if (!IsRelationalProvider(db)) return true;

        for (var attempt = 1; attempt <= LockRetryAttempts; attempt++)
        {
            var document = await db.TradeDocuments
                .FirstOrDefaultAsync(d => d.Id == documentId && !d.IsDeleted);
            if (document is null) return false;

            document.UpdatedAt = DateTime.Now;
            try
            {
                await db.SaveChangesAsync();
                return true;
            }
            catch (DbUpdateConcurrencyException ex)
            {
                foreach (var entry in ex.Entries)
                {
                    try
                    {
                        await entry.ReloadAsync();
                    }
                    catch (DbUpdateConcurrencyException)
                    {
                        return false; // 行已被并发事务删除
                    }
                }

                if (attempt == LockRetryAttempts)
                    throw BusinessException.RuleConflict(ConcurrentMutationText);
            }
        }

        return false;
    }

    /// <summary>
    /// 对一组父单证行按 <b>Id 升序</b>确定性加锁（批量删除等）。非关系型提供程序跳过。
    /// </summary>
    public static async Task LockDocumentRowsAsync(IErpDbContext db, IEnumerable<long>? documentIds)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (!IsRelationalProvider(db)) return;

        foreach (var id in MergeDocumentLockIds(documentIds))
            await LockDocumentRowAsync(db, id);
    }

    /// <summary>事务回滚 / 显式拒绝后丢弃变更跟踪器中的半成品变更，绝不残留部分写入（内存库同样生效）。</summary>
    public static void DiscardTrackedChanges(IErpDbContext db)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (db is DbContext context) context.ChangeTracker.Clear();
    }

    // ==================== 2. 状态口径与表头生命周期判定（纯判定，不写库） ====================

    /// <summary>状态是否处于已知口径内（待制作 / 已制作 / 已提交客户 / 已使用）。</summary>
    public static bool IsKnownStatus(string? status) => TradeDocumentItemRules.IsKnownStatus(status);

    /// <summary>状态归一化：去首尾空白；空值按「待制作」处理（与实体默认值同源）。</summary>
    public static string NormalizeStatus(string? status)
    {
        var value = (status ?? string.Empty).Trim();
        return value.Length == 0 ? TradeDocumentItemRules.StatusPreparing : value;
    }

    /// <summary>状态必须在已知口径内，否则 fail closed（拒绝而不放行）。</summary>
    public static void EnsureKnownStatus(string? status)
    {
        if (IsKnownStatus(status)) return;
        var value = (status ?? string.Empty).Trim();
        throw BusinessException.InvalidParameter(
            $"{UnknownStatusText}（收到「{(value.Length == 0 ? "（未填写）" : value)}」）");
    }

    /// <summary>
    /// 表头修改的生命周期判定（锁内重新加载后调用）：持久化状态与拟议状态都必须在已知口径内、
    /// 状态只允许前进、冻结状态（已提交客户 / 已使用）下商业字段不得改动。任一不满足即 fail closed。
    /// </summary>
    public static void EnsureHeaderUpdateAllowed(TradeDocument stored, TradeDocument proposed)
    {
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentNullException.ThrowIfNull(proposed);

        var storedStatus = (stored.Status ?? string.Empty).Trim();
        if (!IsKnownStatus(storedStatus))
            throw BusinessException.RuleConflict(
                $"{UnknownStoredStatusText}（收到「{(storedStatus.Length == 0 ? "（未填写）" : storedStatus)}」）");

        var proposedStatus = NormalizeStatus(proposed.Status);
        EnsureKnownStatus(proposedStatus);

        if (StatusOrder[proposedStatus] < StatusOrder[storedStatus])
            throw BusinessException.RuleConflict(
                $"{BackwardStatusText}（当前「{storedStatus}」→ 拟议「{proposedStatus}」）");

        if (TradeDocumentItemRules.IsFrozenStatus(storedStatus) && HasCommercialChange(stored, proposed))
            throw BusinessException.RuleConflict(
                $"{FrozenHeaderText}（当前状态「{storedStatus}」）");
    }

    /// <summary>冻结状态下的商业字段是否发生变化（状态字段除外，只允许前进）。</summary>
    private static bool HasCommercialChange(TradeDocument stored, TradeDocument proposed)
        => stored.Amount != proposed.Amount
           || stored.CustomerId != proposed.CustomerId
           || stored.Copies != proposed.Copies
           || stored.IssueDate != proposed.IssueDate
           || !TextEquals(stored.Currency, proposed.Currency)
           || !TextEquals(stored.CustomerName, proposed.CustomerName)
           || !TextEquals(stored.DocNo, proposed.DocNo)
           || !TextEquals(stored.DocType, proposed.DocType)
           || !TextEquals(stored.SalesOrderNo, proposed.SalesOrderNo)
           || !TextEquals(stored.RefNo, proposed.RefNo)
           || !TextEquals(stored.DeclareNo, proposed.DeclareNo)
           || !TextEquals(stored.DeparturePort, proposed.DeparturePort)
           || !TextEquals(stored.DestinationPort, proposed.DestinationPort)
           || !TextEquals(stored.IssuedBy, proposed.IssuedBy)
           || !TextEquals(stored.FileNote, proposed.FileNote)
           || !TextEquals(stored.Remark, proposed.Remark);

    private static bool TextEquals(string? left, string? right)
        => string.Equals((left ?? string.Empty).Trim(), (right ?? string.Empty).Trim(), StringComparison.Ordinal);
}
