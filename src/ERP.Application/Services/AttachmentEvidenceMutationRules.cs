using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Domain.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace ERP.Application.Services;

/// <summary>
/// 附件内容证据「登记 / 作废」原子性与补偿护栏（ERP-409，<strong>唯一权威口径</strong>）。
/// <para><b>协议</b>：附件证据的<strong>登记</strong>（写入新证据）与<strong>作废</strong>（状态 + 作废留痕）
/// 全部落在<strong>同一个原子事务</strong>与<strong>确定性行锁</strong>内：</para>
/// <list type="number">
/// <item><b>登记</b>：先对<strong>归属（父）业务单据行</strong>加锁
/// （<c>SELECT Id FROM &lt;父表&gt; WITH (UPDLOCK, HOLDLOCK)</c> 语义），再在锁内<strong>重新读取</strong>
/// 实时父单据状态与权威归属（实时身份 / 既有菜单 / 客户数据范围），随后才发布内容并提交元数据。
/// 因此「父单据并发删除 / 归属客户变更 / 权限撤销」要么在加锁前完成（本请求锁内复核即失败），
/// 要么在加锁后被阻塞到本请求提交之后再发生，绝不出现「先复核、后改归属，再登记」的撕裂。</item>
/// <item><b>作废</b>：先对<strong>证据行</strong>加锁（同一确定性口径），再在锁内重新读取证据状态与实时归属授权；
/// 两个并发作废请求只有一个能提交，<strong>输家</strong>在锁内读到的已是「已作废」，一律拒绝，
/// 绝不覆盖赢家已保留的<strong>原始作废原因与时间戳</strong>。</item>
/// </list>
/// <para><b>锁序</b>：单锁场景（登记只锁归属行；作废只锁证据行）天然无锁环；批量 / 多锁场景一律按
/// <b>Id 升序</b>（去重 + 仅正整数）确定性获取，绝不反向获取其它锁。加锁<strong>只刷新技术字段</strong>
/// <c>UpdatedAt</c>（与既有 <c>TradeDocumentMutationRules</c> / <c>PurchaseInvoiceConcurrencyRules</c> 同一口径），
/// <strong>绝不</strong>为了加锁而改写父单据的状态、金额、数量、客户、明细或任何商业字段。</para>
/// <para><b>补偿</b>：登记在「内容已落盘」之后若元数据写入被<strong>确认回滚</strong>，只对<strong>本次请求新建</strong>的
/// 不透明内容键做一次性有界补偿（见 <see cref="TryCompensateContentAsync"/>）；<strong>不</strong>触碰既有键、
/// 已受理证据、目录扫荡或任何任意路径。若提交结果不确定（提交异常 / 取消 / 回滚失败）或补偿本身失败，
/// 一律<strong>保留</strong>可能已提交的内容并保留可恢复的精确内部证据（<see cref="AttachmentEvidenceIntegrityException"/>），
/// 由上层按内部渠道留存，<strong>绝不</strong>出现在用户可见响应里（隐藏存储键与路径）。</para>
/// </summary>
public static class AttachmentEvidenceMutationRules
{
    /// <summary>行锁重试上限（并发方持续改写同一行时；耗尽即原子拒绝，绝不误报为业务拒绝）。</summary>
    public const int RowLockRetryAttempts = 8;


    /// <summary>
    /// 归属（父）业务单据行锁的等价 T-SQL 形式（契约 / 文档同源）：<c>UPDLOCK, HOLDLOCK</c> 语义等价可串行化行锁，
    /// 持有至调用方事务结束。实际执行走 <see cref="LockOwnerRowAsync"/> 的「审计时间戳刷新」
    /// （仅用 EF Core 基础 API），语义与下表提示一致。
    /// </summary>
    public const string OwnerRowLockSqlTemplate =
        "SELECT Id FROM <owner table> WITH (UPDLOCK, HOLDLOCK) WHERE Id = {0}";

    /// <summary>证据行锁的等价 T-SQL 形式（作废用；口径与归属行锁完全一致）。</summary>
    public const string EvidenceRowLockSql =
        "SELECT Id FROM db_owner.AttachmentEvidences WITH (UPDLOCK, HOLDLOCK) WHERE Id = {0}";

    /// <summary>锁序口径文案（接口 / 文档同源）。</summary>
    public const string LockOrderText =
        "ERP-409 确定性行锁：登记先锁「归属（父）业务单据行」，作废先锁「证据行」，二者都是 "
        + "SELECT ... WITH (UPDLOCK, HOLDLOCK) 语义（持有至事务结束）；批量 / 多锁一律按 Id 升序确定性获取，"
        + "绝不反向获取其它锁。加锁只刷新技术字段 UpdatedAt，绝不为加锁而改写父单据的商业字段。";

    /// <summary>边界文案（不新增权限 / 表列，不改写父单据业务字段与库存 / 财务）。</summary>
    public const string BoundaryText =
        "本护栏只保护附件内容证据「登记 / 作废」的并发原子性与内容补偿：不新增菜单 / 角色 / 用户授权或表结构，"
        + "也不把空身份当作匿名或管理员；不改写归属单据的状态、金额、数量、客户与明细，"
        + "不生成任何库存移动，也不触碰生产对象存储、任意文件系统路径或目录扫荡。";

    /// <summary>行锁重试耗尽（并发方持续改写同一行）的对外文案。</summary>
    public const string ConcurrentMutationText =
        "目标附件证据 / 归属单据正在被并发修改，本次操作未生效：请刷新后重试（原始证据均未改变）";

    /// <summary>归属单据在锁内已被并发删除 / 不存在时的拒绝文案。</summary>
    public const string OwnerUnavailableText =
        "归属单据不存在或已删除：附件证据不能登记（并发删除 / 归属变更已先行生效，fail closed，"
        + "未保存任何内容，也未改写任何业务单据）";

    /// <summary>补偿失败时对用户的**安全**文案（不含存储键 / 路径；精确内部证据另见异常属性与日志）。</summary>
    public const string CompensationFailedUserText =
        "附件内容登记未完成（元数据已回滚）：新建内容的有界清理未成功，系统已保留可恢复的内部证据，"
        + "本次登记未生效，请稍后重试或联系管理员核对（不会把存储键或路径返回给界面）";

    /// <summary>提交结果不确定时对用户的**安全**文案（不含存储键 / 路径）。</summary>
    public const string AmbiguousRetentionUserText =
        "附件内容登记的提交结果不确定：为避免删除可能已提交的内容，系统保留本次新建内容与精确内部证据，"
        + "请刷新后核对本次登记是否生效，切勿重复上传（不会把存储键或路径返回给界面）";

    // ==================== 1. 关系型判定 / 事务 ====================

    /// <summary>关系型后端（SQL Server）判定：内存库等非关系型提供程序没有行锁语义，锁定与事务等价无操作。</summary>
    public static bool IsRelationalProvider(IErpDbContext db)
    {
        ArgumentNullException.ThrowIfNull(db);
        return string.Equals(db.Database.ProviderName, "Microsoft.EntityFrameworkCore.SqlServer",
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 登记 / 作废使用的原子事务：关系型后端开启真实事务（失败整体回滚，绝不留半成品证据或孤儿内容）；
    /// 非关系型提供程序返回 <c>null</c>（等价无事务，声明式校验不变）。
    /// <para>调用方已经开启事务时<strong>绝不嵌套</strong>：直接复用既有事务，由最外层调用方提交 / 回滚；
    /// 此时返回 <c>null</c> 表示「复用既有事务，本层不提交」。</para>
    /// </summary>
    public static async Task<IDbContextTransaction?> BeginMutationTransactionAsync(
        IErpDbContext db, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (!IsRelationalProvider(db)) return null;
        if (db.Database.CurrentTransaction is not null) return null;

        return await db.Database.BeginTransactionAsync(cancellationToken);
    }

    /// <summary>提交事务（<c>null</c> 表示无事务 / 复用外层事务，本层不提交）。</summary>
    public static async Task CommitAsync(
        IDbContextTransaction? transaction, CancellationToken cancellationToken = default)
    {
        if (transaction is null) return;
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// 尝试回滚并丢弃变更跟踪器中的半成品变更。返回 <c>true</c> 表示<strong>已确认回滚</strong>
    /// （无事务，或回滚成功）；返回 <c>false</c> 表示回滚本身失败（提交 / 回滚结果不确定 → 上层必须保留内容与内部证据）。
    /// </summary>
    public static async Task<bool> TryRollbackAsync(IDbContextTransaction? transaction, IErpDbContext db)
    {
        ArgumentNullException.ThrowIfNull(db);

        // 先丢弃跟踪器中的半成品变更，避免后续操作误用（内存库同样生效）。
        DiscardTrackedChanges(db);

        if (transaction is null) return true;   // 非关系型 / 复用外层事务：本层无「已确认提交」，视为已回滚

        try
        {
            await transaction.RollbackAsync(CancellationToken.None);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>回滚 / 显式拒绝后丢弃变更跟踪器中的半成品变更，绝不残留部分写入（内存库同样生效）。</summary>
    public static void DiscardTrackedChanges(IErpDbContext db)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (db is DbContext context) context.ChangeTracker.Clear();
    }


    // ==================== 2. 确定性行锁 ====================

    /// <summary>
    /// 合并需要加锁的 Id：去重、只保留正整数，并按 Id 升序返回，保证多把行锁的确定性获取顺序
    /// （绝不反向获取，也就不存在锁环）。
    /// </summary>
    public static IReadOnlyList<long> MergeLockIds(IEnumerable<long>? ids)
        => (ids ?? Array.Empty<long>())
            .Where(id => id > 0)
            .Distinct()
            .OrderBy(id => id)
            .ToList();

    /// <summary>
    /// 对<strong>归属（父）业务单据行</strong>加排它行锁：把「附件证据登记」与「父单据并发删除 / 归属客户变更 /
    /// 权限撤销」串行化在同一事务内。返回 <c>false</c> 表示该父单据不存在 / 已被并发删除。
    /// <para><b>实现</b>（与既有 <c>TradeDocumentMutationRules</c> / <c>PurchaseInvoiceConcurrencyRules</c> 同一口径，
    /// 仅用 EF Core 基础 API）：在调用方事务内对目标行发一条「审计时间戳刷新」的 UPDATE，取得排它行锁
    /// （X 锁，持有至事务结束），语义等价于 <see cref="OwnerRowLockSqlTemplate"/> 的
    /// <c>SELECT ... WITH (UPDLOCK, HOLDLOCK)</c>；<c>UpdatedAt</c> 是技术审计字段、不是商业证据，
    /// 因此<strong>不</strong>构成对父单据状态 / 金额 / 数量 / 客户 / 明细的静默改写。</para>
    /// <para>内存库等非关系型提供程序无行锁语义，直接返回 <c>true</c>（存在性仍由锁内重新加载判定）。</para>
    /// </summary>
    public static Task<bool> LockOwnerRowAsync(IErpDbContext db, string? ownerType, long ownerId)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (ownerId <= 0) return Task.FromResult(false);

        // 归属类型必须在白名单内（未知 / 历史类型无法判定归属 → 加锁失败，绝不放过）
        if (!AttachmentEvidenceRules.IsSupportedOwnerType(ownerType)) return Task.FromResult(false);
        if (!IsRelationalProvider(db)) return Task.FromResult(true);

        var type = AttachmentEvidenceRules.NormalizeOwnerType(ownerType);
        return type switch
        {
            AttachmentEvidenceRules.OwnerTypeSalesOrder => LockTrackedRowAsync(
                db, () => db.SalesOrders.FirstOrDefaultAsync(o => o.Id == ownerId && !o.IsDeleted),
                row => row.UpdatedAt = DateTime.Now),
            AttachmentEvidenceRules.OwnerTypePurchaseOrder => LockTrackedRowAsync(
                db, () => db.PurchaseOrders.FirstOrDefaultAsync(o => o.Id == ownerId && !o.IsDeleted),
                row => row.UpdatedAt = DateTime.Now),
            AttachmentEvidenceRules.OwnerTypeQualityInspection => LockTrackedRowAsync(
                db, () => db.PurchaseOrders.FirstOrDefaultAsync(o => o.Id == ownerId && !o.IsDeleted),
                row => row.UpdatedAt = DateTime.Now),
            AttachmentEvidenceRules.OwnerTypeTradeDocument => LockTrackedRowAsync(
                db, () => db.TradeDocuments.FirstOrDefaultAsync(d => d.Id == ownerId && !d.IsDeleted),
                row => row.UpdatedAt = DateTime.Now),
            AttachmentEvidenceRules.OwnerTypeSample => LockTrackedRowAsync(
                db, () => db.Samples.FirstOrDefaultAsync(s => s.Id == ownerId && !s.IsDeleted),
                row => row.UpdatedAt = DateTime.Now),
            _ => Task.FromResult(false)
        };
    }

    /// <summary>
    /// 对<strong>证据行</strong>加排它行锁（作废用；口径与 <see cref="LockOwnerRowAsync"/> 完全一致）：
    /// 把同一条证据的并发作废串行化在同一事务内。返回 <c>false</c> 表示该证据不存在 / 已被并发删除。
    /// </summary>
    public static Task<bool> LockEvidenceRowAsync(IErpDbContext db, long evidenceId)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (evidenceId <= 0) return Task.FromResult(false);
        if (!IsRelationalProvider(db)) return Task.FromResult(true);

        return LockTrackedRowAsync(
            db, () => db.AttachmentEvidences.FirstOrDefaultAsync(r => r.Id == evidenceId && !r.IsDeleted),
            row => row.UpdatedAt = DateTime.Now);
    }

    /// <summary>
    /// 通用的「刷新技术审计时间戳以取得排它行锁」核心：重读 → 刷新 <c>UpdatedAt</c> → 保存；
    /// 并发方先提交使本地乐观令牌过期时按可读业务语义「阻塞后成功」有界重试，绝不把锁等待误报成业务拒绝。
    /// </summary>
    private static async Task<bool> LockTrackedRowAsync<TEntity>(
        IErpDbContext db, Func<Task<TEntity?>> load, Action<TEntity> touch)
        where TEntity : BaseEntity
    {
        for (var attempt = 1; attempt <= RowLockRetryAttempts; attempt++)
        {
            var row = await load();
            if (row is null) return false;

            touch(row);
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

                if (attempt == RowLockRetryAttempts)
                    throw BusinessException.RuleConflict(ConcurrentMutationText);
            }
        }

        return false;
    }

    // ==================== 3. 有界内容补偿与内部证据 ====================

    /// <summary>
    /// 有界补偿：删除<strong>本次请求新建</strong>的不透明内容键。只有在元数据写入被<strong>确认回滚</strong>后
    /// 才允许调用，且键必须来自本次操作刚刚返回的 <c>SaveAsync</c> 结果（绝不传既有键、已受理证据键或任何
    /// 用户输入）。存储实现负责校验键形态、解析隔离根目录并只删除单个文件；生产提供程序 / 不支持的实现
    /// 一律返回 <c>false</c>（上层据此保留内部证据）。任何异常都转成 <c>false</c>，绝不外泄为「已成功清理」。
    /// </summary>
    public static async Task<bool> TryCompensateContentAsync(
        IAttachmentContentStore store, string? requestOwnedKey)
    {
        ArgumentNullException.ThrowIfNull(store);
        var key = (requestOwnedKey ?? string.Empty).Trim();
        if (key.Length == 0) return false;

        try
        {
            return await store.TryRemoveRequestOwnedAsync(key, CancellationToken.None);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 构造「有界补偿失败」的完整异常（对用户的安全文案 + 精确内部证据，绝不暴露存储键 / 路径）。
    /// </summary>
    public static AttachmentEvidenceIntegrityException CompensationFailed(
        string ownerType, long ownerId, string? requestOwnedKey, string? sha256, long sizeBytes,
        string? providerCode, Exception? inner)
        => new(
            stage: "confirmed-rollback-compensation-failed",
            internalEvidence: BuildInternalEvidence(
                "元数据已确认回滚，但新建内容的有界补偿未成功", ownerType, ownerId, requestOwnedKey,
                sha256, sizeBytes, providerCode),
            userMessage: CompensationFailedUserText,
            inner: inner);

    /// <summary>
    /// 构造「提交结果不确定」的完整异常（保留可能已提交的内容；绝不删除；对用户的安全文案不含键 / 路径）。
    /// </summary>
    public static AttachmentEvidenceIntegrityException AmbiguousRetention(
        string ownerType, long ownerId, string? requestOwnedKey, string? sha256, long sizeBytes,
        string? providerCode, string stage, string reason, Exception? inner)
        => new(
            stage: stage,
            internalEvidence: BuildInternalEvidence(
                $"提交结果不确定（{reason}）：已保留可能已提交的内容，绝不删除", ownerType, ownerId,
                requestOwnedKey, sha256, sizeBytes, providerCode),
            userMessage: AmbiguousRetentionUserText,
            inner: inner);

    /// <summary>精确内部证据文本（只在内部渠道 / 日志留存，绝不进入用户可见响应）。</summary>
    private static string BuildInternalEvidence(
        string summary, string ownerType, long ownerId, string? requestOwnedKey, string? sha256,
        long sizeBytes, string? providerCode)
        => string.Join(" | ",
            $"summary={summary}",
            $"ownerType={ownerType}",
            $"ownerId={ownerId}",
            $"provider={providerCode ?? "(unknown)"}",
            $"sizeBytes={sizeBytes}",
            $"sha256={(string.IsNullOrWhiteSpace(sha256) ? "(not-computed)" : sha256)}",
            $"storageKey={(string.IsNullOrWhiteSpace(requestOwnedKey) ? "(none)" : requestOwnedKey)}");
}

/// <summary>
/// 附件证据「登记 / 作废」原子性护栏在<strong>内容补偿 / 提交不确定</strong>场景保留的内部完整性异常（ERP-409）。
/// <para>用户可见响应只使用 <see cref="Exception.Message"/> 里的<strong>安全</strong>文案（不含存储键与任何路径）；
/// 精确内部证据（归属、存储键、摘要、长度、提供程序、阶段）保存在 <see cref="InternalEvidence"/>，
/// 并通过 <see cref="ToString"/> 追加，使服务端日志（Serilog 的异常渲染）能完整留存、可恢复、可核对。</para>
/// <para>刻意不是 <see cref="BusinessException"/>：全局异常中间件对非业务异常返回固定通用文案，
/// 因此存储键 / 路径<strong>绝不</strong>可能出现在用户可见响应中。</para>
/// </summary>
public sealed class AttachmentEvidenceIntegrityException : Exception
{
    /// <summary>护栏阶段（如 <c>confirmed-rollback-compensation-failed</c> / <c>ambiguous-commit</c>）。</summary>
    public string Stage { get; }

    /// <summary>精确内部证据（仅内部渠道 / 日志留存；绝不返回给用户）。</summary>
    public string InternalEvidence { get; }

    public AttachmentEvidenceIntegrityException(
        string stage, string internalEvidence, string userMessage, Exception? inner = null)
        : base(userMessage, inner)
    {
        Stage = stage;
        InternalEvidence = internalEvidence;
    }

    /// <summary>追加内部证据，确保服务端日志（异常渲染）保留可恢复的精确坐标。</summary>
    public override string ToString()
        => $"{base.ToString()}{Environment.NewLine}[internal-evidence] {InternalEvidence}";
}


