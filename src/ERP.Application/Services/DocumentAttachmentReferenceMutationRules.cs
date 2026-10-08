using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Domain.Common;
using ERP.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace ERP.Application.Services;

/// <summary>
/// 业务单据附件引用「登记 / 作废」原子性与确定性行锁护栏（ERP-412，<strong>唯一权威口径</strong>）。
/// <para><b>协议</b>：附件引用（<c>DocumentAttachmentReferences</c>，ERP-045 仅元数据引用册）的
/// <strong>登记</strong>（写入新引用）与<strong>作废</strong>（状态 + 作废留痕）全部落在
/// <strong>同一个原子事务</strong>与<strong>确定性行锁</strong>内：</para>
/// <list type="number">
/// <item><b>登记</b>：先对<strong>权威行父单据行</strong>加锁
/// （<c>SELECT Id FROM &lt;父表&gt; WITH (UPDLOCK, HOLDLOCK)</c> 语义），再在锁内<strong>重新读取</strong>
/// 实时身份 / 既有菜单 / 客户数据范围与权威父单据（存在 / 未删除 + 号码 / 类型快照），随后才判定有效身份唯一并写入。
/// 因此「父单据并发软删除 / 归属客户变更 / 权限撤销」要么在加锁前完成（锁内复核即失败，零写入），
/// 要么被阻塞到本次登记提交之后再发生，绝不出现「先复核、后改归属，再登记」的撕裂。</item>
/// <item><b>作废</b>：先对<strong>附件引用行</strong>加锁（同一确定性口径），再在锁内重新读取引用（tracked）
/// 与实时身份授权；两个并发作废请求只有一个能提交，<strong>输家</strong>在锁内读到的已是「已作废」，
/// 一律拒绝，绝不覆盖赢家已保留的<strong>原始作废原因与时间戳</strong>。</item>
/// </list>
/// <para><b>锁序</b>：单锁场景（登记只锁父单据行；作废只锁引用行）天然无锁环；批量 / 多锁场景一律按
/// <b>Id 升序</b>（去重 + 仅正整数，见 <see cref="MergeLockIds"/>）确定性获取，绝不反向获取其它锁。
/// 加锁<strong>只刷新技术字段</strong> <c>UpdatedAt</c>（与既有 ERP-409 <c>AttachmentEvidenceMutationRules</c> /
/// <c>TradeDocumentMutationRules</c> 同一口径），<strong>绝不</strong>为了加锁而改写父单据的状态、金额、
/// 数量、客户、明细或任何商业字段。</para>
/// <para><b>唯一口径</b>：复用既有<strong>过滤唯一索引</strong>
/// <c>UX_DocumentAttachmentReferences_ActiveIdentity</c>（<c>ParentType, ParentId, Category, ReferenceId</c>
/// WHERE <c>IsDeleted = 0 AND Status = 0</c>）作为最后防线；并发登记输家在插入时被索引拒绝，由
/// <see cref="SaveReferenceAsync"/> 映射为<strong>稳定的业务冲突</strong>（<c>Duplicate</c>），
/// 并随调用方事务<strong>完整回滚</strong>（零部分写入）；<strong>绝不</strong>暴露
/// <c>SqlException</c> / 错误码 / 索引名 / 连接串等内部细节。<strong>不</strong>新增任何表 / 列 / 索引，
/// 也不新增菜单 / 角色 / 用户授权，更不把空身份当作匿名或管理员。</para>
/// </summary>
public static class DocumentAttachmentReferenceMutationRules
{
    /// <summary>行锁重试上限（并发方持续改写同一行时；耗尽即原子拒绝，绝不误报为业务拒绝）。</summary>
    public const int RowLockRetryAttempts = 8;

    /// <summary>
    /// 权威行父单据行锁的等价 T-SQL 形式（契约 / 文档同源）：
    /// <c>UPDLOCK, HOLDLOCK</c> 语义等价可串行化行锁，持有至调用方事务结束。
    /// 实际执行走 <see cref="LockParentRowAsync"/> 的「审计时间戳刷新」（仅用 EF Core 基础 API），语义与下表提示一致。
    /// </summary>
    public const string ParentRowLockSqlTemplate =
        "SELECT Id FROM <parent table> WITH (UPDLOCK, HOLDLOCK) WHERE Id = {0}";

    /// <summary>附件引用行锁的等价 T-SQL 形式（作废用；口径与父单据行锁完全一致）。</summary>
    public const string ReferenceRowLockSql =
        "SELECT Id FROM db_owner.DocumentAttachmentReferences WITH (UPDLOCK, HOLDLOCK) WHERE Id = {0}";

    /// <summary>锁序口径文案（接口 / 文档同源）。</summary>
    public const string LockOrderText =
        "ERP-412 确定性行锁：登记先锁「权威父单据行」（销售订单 / 采购订单 / 装柜清单 / 出口单证），"
        + "作废先锁「附件引用行」，二者都是 SELECT ... WITH (UPDLOCK, HOLDLOCK) 语义（持有至事务结束）；"
        + "批量 / 多锁一律按 Id 升序确定性获取，绝不反向获取其它锁。加锁只刷新技术字段 UpdatedAt，"
        + "绝不为加锁而改写父单据的状态 / 金额 / 数量 / 客户 / 明细等商业字段。";

    /// <summary>边界文案（不新增权限 / 表列 / 索引，不改写父单据业务字段与库存 / 财务）。</summary>
    public const string BoundaryText =
        "本护栏只保护附件引用「登记 / 作废」的并发原子性与有效身份唯一口径：不新增菜单 / 角色 / 用户授权或表 / 列 / 索引，"
        + "也不把空身份当作匿名或管理员；不改写父单据的状态、金额、数量、客户与明细，不生成任何库存移动，"
        + "也不触碰对象存储、任意文件系统路径或任何远端对象。";

    /// <summary>行锁重试耗尽（并发方持续改写同一行）的对外文案。</summary>
    public const string ConcurrentMutationText =
        "目标附件引用 / 权威父单据正在被并发修改，本次操作未生效：请刷新后重试（原始引用均未改变）";

    /// <summary>权威父单据在锁内已被并发删除 / 不存在时的拒绝文案（含既有「不存在或已删除」口径）。</summary>
    public const string ParentUnavailableText =
        "父单据不存在或已删除：附件引用不能登记（并发删除 / 归属变更已先行生效，fail closed，未保存任何记录）";

    /// <summary>并发登记输家被既有过滤唯一索引拒绝时映射的稳定业务冲突文案。</summary>
    public const string ActiveIdentityConflictText =
        "同一父单据（类型 + Id）+ 分类 + 引用标识已存在有效记录：并发登记的输家被唯一身份口径拒绝，"
        + "本次登记未生效且已完整回滚（重复登记被拒绝而不是静默合并）";

    // ==================== 1. 事务 ====================

    /// <summary>
    /// 是否关系型提供程序（只有关系型后端具备真实事务与行锁语义；内存库等非关系型等价无操作）。
    /// </summary>
    public static bool IsRelationalProvider(IErpDbContext db)
    {
        ArgumentNullException.ThrowIfNull(db);
        return string.Equals(db.Database.ProviderName, "Microsoft.EntityFrameworkCore.SqlServer",
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 登记 / 作废使用的原子事务：关系型后端开启真实事务（失败整体回滚，绝不留半成品引用）；
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
    /// （无事务，或回滚成功）；返回 <c>false</c> 表示回滚本身失败。
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
    /// 对<strong>权威父单据行</strong>加排它行锁：把「附件引用登记」与「父单据并发软删除 / 归属客户变更 /
    /// 权限撤销」串行化在同一事务内。返回 <c>false</c> 表示父单据类型非法 / Id 非法 / 该行不存在或已被并发删除。
    /// <para><b>实现</b>（与既有 ERP-409 <c>AttachmentEvidenceMutationRules</c> / <c>TradeDocumentMutationRules</c>
    /// 同一口径，仅用 EF Core 基础 API）：在调用方事务内对目标行发一条「审计时间戳刷新」的 UPDATE，取得排它行锁
    /// （X 锁，持有至事务结束），语义等价于 <see cref="ParentRowLockSqlTemplate"/> 的
    /// <c>SELECT ... WITH (UPDLOCK, HOLDLOCK)</c>；<c>UpdatedAt</c> 是技术审计字段、不是商业证据，
    /// 因此<strong>不</strong>构成对父单据状态 / 金额 / 数量 / 客户 / 明细的静默改写。</para>
    /// <para>内存库等非关系型提供程序无行锁语义，直接返回 <c>true</c>
    /// （存在性仍由调用方在锁内重新读取权威父单据判定）。</para>
    /// </summary>
    public static Task<bool> LockParentRowAsync(IErpDbContext db, string? parentType, long parentId)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (parentId <= 0) return Task.FromResult(false);

        // 父单据类型必须在白名单内（未知 / 历史类型无法判定权威行 → 加锁失败，绝不放过）
        if (!DocumentAttachmentReferenceRules.IsSupportedParentType(parentType)) return Task.FromResult(false);
        if (!IsRelationalProvider(db)) return Task.FromResult(true);

        var type = DocumentAttachmentReferenceRules.NormalizeParentType(parentType);
        return type switch
        {
            DocumentAttachmentReferenceRules.ParentTypeSalesOrder => LockTrackedRowAsync(
                db, () => db.SalesOrders.FirstOrDefaultAsync(o => o.Id == parentId && !o.IsDeleted),
                row => row.UpdatedAt = DateTime.Now),
            DocumentAttachmentReferenceRules.ParentTypePurchaseOrder => LockTrackedRowAsync(
                db, () => db.PurchaseOrders.FirstOrDefaultAsync(o => o.Id == parentId && !o.IsDeleted),
                row => row.UpdatedAt = DateTime.Now),
            DocumentAttachmentReferenceRules.ParentTypeContainerLoadingList => LockTrackedRowAsync(
                db, () => db.ContainerLoadingLists.FirstOrDefaultAsync(o => o.Id == parentId && !o.IsDeleted),
                row => row.UpdatedAt = DateTime.Now),
            DocumentAttachmentReferenceRules.ParentTypeTradeDocument => LockTrackedRowAsync(
                db, () => db.TradeDocuments.FirstOrDefaultAsync(d => d.Id == parentId && !d.IsDeleted),
                row => row.UpdatedAt = DateTime.Now),
            _ => Task.FromResult(false)
        };
    }

    /// <summary>
    /// 对<strong>附件引用行</strong>加排它行锁（作废用；口径与 <see cref="LockParentRowAsync"/> 完全一致），
    /// 并把锁内重新读取的受跟踪实体返回给调用方：把同一条引用的并发作废串行化在同一事务内。
    /// 返回 <c>null</c> 表示 Id 非法 / 该引用不存在 / 已被并发删除。
    /// <para>内存库等非关系型提供程序无行锁语义，等价于一次按 Id 的受跟踪读取。</para>
    /// </summary>
    public static Task<DocumentAttachmentReference?> LockReferenceRowAsync(IErpDbContext db, long referenceId)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (referenceId <= 0) return Task.FromResult<DocumentAttachmentReference?>(null);

        Func<Task<DocumentAttachmentReference?>> load = () => db.DocumentAttachmentReferences
            .FirstOrDefaultAsync(x => x.Id == referenceId && !x.IsDeleted);

        if (!IsRelationalProvider(db)) return load();

        return LockTrackedEntityAsync(db, load, row => row.UpdatedAt = DateTime.Now);
    }

    /// <summary>
    /// 通用的「刷新技术审计时间戳以取得排它行锁」核心：重读 → 刷新 <c>UpdatedAt</c> → 保存；
    /// 并发方先提交使本地乐观令牌过期时按可读业务语义「阻塞后成功」有界重试，
    /// 绝不把锁等待误报成业务拒绝；返回锁内重读的受跟踪实体（行已被并发删除 → <c>null</c>）。
    /// </summary>
    private static async Task<TEntity?> LockTrackedEntityAsync<TEntity>(
        IErpDbContext db, Func<Task<TEntity?>> load, Action<TEntity> touch)
        where TEntity : BaseEntity
    {
        for (var attempt = 1; attempt <= RowLockRetryAttempts; attempt++)
        {
            var row = await load();
            if (row is null) return null;

            touch(row);
            try
            {
                await db.SaveChangesAsync();
                return row;
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
                        return null; // 行已被并发事务删除
                    }
                }

                if (attempt == RowLockRetryAttempts)
                    throw BusinessException.RuleConflict(ConcurrentMutationText);
            }
        }

        return null;
    }

    /// <summary>布尔形式的行锁（父单据行锁用：只关心是否存在且成功加锁）。</summary>
    private static async Task<bool> LockTrackedRowAsync<TEntity>(
        IErpDbContext db, Func<Task<TEntity?>> load, Action<TEntity> touch)
        where TEntity : BaseEntity
        => await LockTrackedEntityAsync(db, load, touch) is not null;

    // ==================== 3. 唯一口径与写入映射 ====================

    /// <summary>
    /// 保存附件引用写入：把数据库层<strong>唯一索引冲突</strong>（并发登记输家被既有过滤唯一索引
    /// <c>UX_DocumentAttachmentReferences_ActiveIdentity</c> 拒绝）映射为<strong>稳定的业务冲突</strong>
    /// （<c>Duplicate</c>），完整回滚由调用方在同一事务上执行（零部分写入）。
    /// <para><strong>绝不</strong>把 <c>SqlException</c> / 错误码 / 索引名 / 连接串等内部细节暴露给调用方；
    /// 非唯一性冲突（如结构缺失）原样上抛，不被误报成重复。</para>
    /// </summary>
    public static async Task SaveReferenceAsync(IErpDbContext db, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (LooksLikeUniqueViolation(ex))
        {
            throw BusinessException.Duplicate(ActiveIdentityConflictText);
        }
    }

    /// <summary>
    /// 是否为唯一索引 / 唯一约束冲突（SQL Server 2601 重复键 / 2627 违反唯一约束）。
    /// 说明：应用层不引用 <c>Microsoft.Data.SqlClient</c>，故按错误码与错误文案识别；
    /// 非唯一性冲突的 <see cref="DbUpdateException"/>（如结构缺失）原样上抛，不被误报成并发重复。
    /// </summary>
    public static bool LooksLikeUniqueViolation(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            var message = current.Message;
            if (message.Contains("2601", StringComparison.Ordinal)
                || message.Contains("2627", StringComparison.Ordinal)
                || message.Contains("duplicate key", StringComparison.OrdinalIgnoreCase)
                || message.Contains("UNIQUE", StringComparison.Ordinal))
                return true;
        }
        return false;
    }
}
