using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace ERP.Application.Services;

/// <summary>
/// 代理服务费证据（ERP-519 协议证据；后续 ERP-520 / ERP-521 对账单与收款分摊证据复用）共用的
/// <strong>并发护栏</strong>：把「草稿修改 / 登记冻结 / 作废」这类会影响同一行证据生命周期的写路由
/// 收敛到<strong>同一把既有实体行锁 + 同一原子事务</strong>上，使并发请求只能得到一条一致证据或原子拒绝。
/// <list type="number">
/// <item><b>事务</b>（<see cref="BeginMutationTransactionAsync"/>）：只对关系型后端开启真实事务
/// （失败整体回滚，绝不留半条证据 / 半截审计）；非关系型提供程序（内存库）返回 <c>null</c>，等价无事务。
/// 调用方已经开启事务时<strong>绝不嵌套</strong>，由最外层调用方提交 / 回滚（<see cref="CommitAsync"/> 对
/// <c>null</c> 是空操作，因此嵌套场景本层绝不提交）。</item>
/// <item><b>行锁</b>（<see cref="LockAgreementRowAsync"/>）：在调用方事务内对目标协议行发出「技术审计时间戳刷新」
/// 的 UPDATE，取得排它行锁（X 锁，持有至事务结束），语义等价于
/// <c>SELECT ... WITH (UPDLOCK, HOLDLOCK)</c>；<c>UpdatedAt</c> 是技术审计字段、不是商业证据，因此不构成对
/// 协议条款 / 状态 / 客户快照的静默改写。并发方先提交使本地乐观令牌过期时按「阻塞后成功」语义重读权威行后
/// 有界重试，绝不把锁等待误报成业务拒绝。</item>
/// <item><b>拒绝清理</b>（<see cref="TryRollbackAsync"/> / <see cref="DiscardTrackedChanges"/>）：
/// 与既有业务护栏（ERP-415 变更申请）同口径，先丢弃变更跟踪器中的半成品变更、再回滚事务，
/// 保证「被拒请求零部分状态 / 零部分行 / 零审计漂移」。</item>
/// <item><b>受控写入</b>（<see cref="SaveEvidenceAsync"/>）：把数据库层唯一索引冲突（并发同身份输家被既有过滤
/// 唯一索引 <c>UX_AgencyServiceFeeAgreements_ActiveIdentity</c> 拒绝）映射为稳定的业务重复错误，
/// <strong>绝不</strong>暴露 <c>SqlException</c> / 错误码 / 索引名 / 连接串等内部细节；非唯一性冲突原样上抛。</item>
/// </list>
/// <para><b>唯一确定性锁序（绝不反向获取）</b>：1) 代理服务费协议证据行（<c>AgencyServiceFeeAgreements</c>，同一 Id，
/// 本任务）；更下游的对账单证据行 / 收款分摊证据行 / 收款单行锁（ERP-520 / ERP-521）只允许在该协议行锁
/// <b>之后</b>获取，因此不会出现「A 持协议等对账单、B 持对账单等协议」的死锁环。</para>
/// <para><b>边界</b>：本类不新增 / 不修改任何表、列、索引、菜单、角色或用户授权，也不把空身份当作匿名或管理员；
/// 不开发票、不记账、不生成凭证 / 收款 / 付款 / 结算单、不授权任何付款；不改写客户主数据（含佣金比例与信用状态）、
/// 销售订单、装柜与单证、对账单证据、收款分摊证据、收款单、库存与费用；更正仍走显式作废（原因必填），
/// 历史证据与审计留痕不删除、不回填。</para>
/// </summary>
public static class AgencyFeeEvidenceConcurrencyRules
{
    /// <summary>锁序口径文案（接口 / 文档同源）。</summary>
    public const string GlobalLockOrderText =
        "ERP-519 / ERP-520 唯一确定性锁序：① 代理服务费协议证据行（AgencyServiceFeeAgreements，同一 Id）→ "
        + "② 代理服务费对账单证据行（AgencyServiceFeeStatements，同一 Id，ERP-520）；更下游的收款分摊证据行 / "
        + "收款单行锁（ERP-521）只允许在对账单证据行锁之后获取。同一事务需要同时锁定多级证据行时，只能按此顺序加锁，"
        + "绝不反向获取。所有调用方都按此顺序加锁，以求「条件判定 + 写入」原子并把同一协议的草稿修改 / 登记 / 作废竞争、"
        + "同一对账单的草稿行替换 / 登记冻结 / 作废竞争串行化。";

    /// <summary>协议行锁的等价 T-SQL 形式（契约 / 文档同源；实际执行走 <see cref="LockAgreementRowAsync"/> 的审计时间戳刷新）。</summary>
    public const string AgreementRowLockSql =
        "SELECT [Id] FROM [db_owner].[AgencyServiceFeeAgreements] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {0}";

    /// <summary>
    /// 对账单行锁的等价 T-SQL 形式（ERP-520；契约 / 文档同源；实际执行走 <see cref="LockStatementRowAsync"/> 的审计时间戳刷新）。
    /// </summary>
    public const string StatementRowLockSql =
        "SELECT [Id] FROM [db_owner].[AgencyServiceFeeStatements] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {0}";

    /// <summary>边界文案（不新增权限 / 表列 / 索引，不改写既有业务记录与历史证据）。</summary>
    public const string BoundaryText =
        "本护栏只保护代理服务费证据「草稿修改 / 登记冻结 / 作废」的并发完整性：不新增菜单 / 角色 / 用户授权或表 / 列 / 索引，"
        + "也不把空身份当作匿名或管理员；不开发票、不记账、不生成凭证 / 收付款 / 结算单、不授权任何付款；"
        + "不改写客户主数据、销售订单、装柜与单证、对账单证据、收款分摊证据、收款单、库存与费用记录；"
        + "加锁只刷新既有技术审计字段 UpdatedAt（既有业务护栏同口径），绝不为了加锁而改写条款 / 状态 / 客户快照。";

    /// <summary>行锁重试耗尽（并发方持续改写同一行）的对外文案。</summary>
    public const string ConcurrentModificationText =
        "目标代理服务费证据正在被并发修改，本次操作未生效：请刷新后重试（原始证据均未改变）";

    /// <summary>有效身份并发冲突（同一个「客户 + 规范化协议号」的并发登记输家）的对外文案。</summary>
    public const string ActiveIdentityConflictText =
        "同一客户与规范化协议号的未作废记录已存在（或已被并发登记）：重复协议被拒绝而不是静默合并或覆盖"
        + "（如需更正请先作废原记录再重新登记）";

    /// <summary>
    /// 对账单证据并发冲突（同一个「客户 + 规范化对账单号」或同一条服务来源的并发登记输家）的对外文案（ERP-520）：
    /// 与上游 ERP-519 协议文案同口径，绝不暴露数据库内部细节。
    /// </summary>
    public const string StatementActiveEvidenceConflictText =
        "同一客户与规范化对账单号、或同一服务来源的未作废记录已存在（或已被并发登记）："
        + "重复对账单 / 重复计费证据被拒绝而不是静默合并或覆盖（如需更正请先作废原对账单再重新登记）";

    /// <summary>行锁重试次数（乐观并发令牌过期时重读权威行后有界重试；行锁语义 = 阻塞后成功）。</summary>
    private const int RowLockRetryAttempts = 8;

    /// <summary>关系型后端（SQL Server）判定：内存库等非关系型提供程序没有行锁语义，锁定与事务等价无操作。</summary>
    public static bool IsRelationalProvider(IErpDbContext db)
    {
        ArgumentNullException.ThrowIfNull(db);
        return string.Equals(db.Database.ProviderName, "Microsoft.EntityFrameworkCore.SqlServer",
            StringComparison.OrdinalIgnoreCase);
    }

    // ==================== 1. 事务 ====================

    /// <summary>
    /// 开启本次写路由的原子事务：关系型后端开启真实事务（失败整体回滚）；非关系型提供程序返回 <c>null</c>
    /// （等价无事务，声明式校验不变）。
    /// <para>调用方已经开启事务时<strong>绝不嵌套</strong>：直接复用既有事务，由最外层调用方提交 / 回滚；此时返回
    /// <c>null</c> 表示「复用既有事务，本层不提交」。</para>
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
    /// 拒绝 / 失败路径：先丢弃变更跟踪器中的半成品变更，再回滚本层开启的事务。返回 <c>true</c> 表示
    /// <strong>已确认回滚</strong>（无事务，或回滚成功）；<c>false</c> 表示回滚本身失败。
    /// 与既有业务护栏（ERP-412 / ERP-415）同口径，保证被拒请求零部分状态。
    /// <para>丢弃只在<strong>关系型后端</strong>执行：只有关系型后端才具备真实事务与行锁，也才可能存在
    /// 「已加锁 / 已部分写入」的半成品受跟踪状态；非关系型提供程序（内存库）没有真实事务与行锁语义，
    /// 本层的写前校验失败不会产生任何写入，因此保持既有内存语义不变（既有业务护栏的
    /// <see cref="DiscardTrackedChanges"/> 仍可被调用方显式调用）。</para>
    /// </summary>
    public static async Task<bool> TryRollbackAsync(
        IDbContextTransaction? transaction, IErpDbContext db)
    {
        ArgumentNullException.ThrowIfNull(db);

        // 先丢弃跟踪器中的半成品变更（并发输家 / 唯一索引冲突留下的 Added / Modified 实体），
        // 避免失败请求的部分状态被后续操作误用。
        if (IsRelationalProvider(db)) DiscardTrackedChanges(db);

        if (transaction is null) return true;

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

    /// <summary>丢弃变更跟踪器中的半成品变更（既有业务护栏 <c>DiscardTrackedChanges</c> 同口径）。</summary>
    public static void DiscardTrackedChanges(IErpDbContext db)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (db is DbContext context) context.ChangeTracker.Clear();
    }

    // ==================== 2. 确定性行锁 ====================

    /// <summary>
    /// 对代理服务费协议证据行加更新锁（唯一汇聚点）：把同一协议并发的「草稿修改 / 登记冻结 / 作废」
    /// 串行化在同一事务内。返回锁内重读的<strong>受跟踪权威实体</strong>；行已被并发软删除 / 不存在 →
    /// <c>null</c>（由调用方按既有「不存在或已删除」受控错误处理）；非关系型提供程序跳过（返回 <c>null</c>）。
    /// </summary>
    public static async Task<AgencyServiceFeeAgreement?> LockAgreementRowAsync(
        IErpDbContext db, long agreementId)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (agreementId <= 0) return null;
        if (!IsRelationalProvider(db)) return null;

        return await LockAgreementRowCoreAsync(db, agreementId);
    }

    /// <summary>
    /// 行锁核心：在调用方事务内「重读 → 刷新 <c>UpdatedAt</c> → 保存」以取得排它行锁（持有至事务结束），
    /// 语义等价于 <see cref="AgreementRowLockSql"/>。并发方先提交使本地乐观令牌过期时，重读权威行（含新令牌）
    /// 后有界重试，绝不把纯粹的锁等待误报成业务拒绝；真实业务冲突仍由调用方的锁内校验判定。
    /// </summary>
    private static async Task<AgencyServiceFeeAgreement?> LockAgreementRowCoreAsync(
        IErpDbContext db, long agreementId)
    {
        for (var attempt = 1; attempt <= RowLockRetryAttempts; attempt++)
        {
            var agreement = await db.AgencyServiceFeeAgreements
                .FirstOrDefaultAsync(x => x.Id == agreementId && !x.IsDeleted);
            if (agreement is null) return null;

            agreement.UpdatedAt = DateTime.Now;
            try
            {
                await db.SaveChangesAsync();
                return agreement;
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
                        return null;   // 行已被并发软删除 / 删除：由调用方按既有「不存在或已删除」处理
                    }
                }

                if (attempt == RowLockRetryAttempts)
                    throw BusinessException.RuleConflict(ConcurrentModificationText);
            }
        }

        return null;
    }

    /// <summary>
    /// 对代理服务费<strong>对账单证据行</strong>加更新锁（ERP-520 唯一汇聚点）：把同一对账单并发的
    /// 「草稿行替换 / 登记冻结 / 作废」串行化在同一事务内，使「权威状态与行读取 + 校验 + 写入」形成一个原子临界区。
    /// 返回锁内重读的<strong>受跟踪权威实体</strong>；行已被并发软删除 / 不存在 → <c>null</c>
    /// （由调用方按既有「不存在或已删除」受控错误处理）；非关系型提供程序跳过（返回 <c>null</c>）。
    /// <para>锁序：只在对账单证据行上加锁；本任务不在对账单写路由内再取协议 / 来源 / 收款行锁，
    /// 因此不存在交叉锁序（见 <see cref="GlobalLockOrderText"/>）。</para>
    /// </summary>
    public static async Task<AgencyServiceFeeStatement?> LockStatementRowAsync(
        IErpDbContext db, long statementId)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (statementId <= 0) return null;
        if (!IsRelationalProvider(db)) return null;

        return await LockStatementRowCoreAsync(db, statementId);
    }

    /// <summary>
    /// 对账单行锁核心：在调用方事务内「重读 → 刷新 <c>UpdatedAt</c> → 保存」以取得排它行锁（持有至事务结束），
    /// 语义等价于 <see cref="StatementRowLockSql"/>。并发方先提交使本地乐观令牌过期时，重读权威行（含新令牌）
    /// 后有界重试，绝不把纯粹的锁等待误报成业务拒绝；真实业务冲突仍由调用方的锁内校验判定。
    /// </summary>
    private static async Task<AgencyServiceFeeStatement?> LockStatementRowCoreAsync(
        IErpDbContext db, long statementId)
    {
        for (var attempt = 1; attempt <= RowLockRetryAttempts; attempt++)
        {
            var statement = await db.AgencyServiceFeeStatements
                .FirstOrDefaultAsync(x => x.Id == statementId && !x.IsDeleted);
            if (statement is null) return null;

            statement.UpdatedAt = DateTime.Now;
            try
            {
                await db.SaveChangesAsync();
                return statement;
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
                        return null;   // 行已被并发软删除 / 删除：由调用方按既有「不存在或已删除」处理
                    }
                }

                if (attempt == RowLockRetryAttempts)
                    throw BusinessException.RuleConflict(ConcurrentModificationText);
            }
        }

        return null;
    }

    // ==================== 3. 受控写入 ====================

    /// <summary>
    /// 保存代理服务费协议证据写入：把数据库层<strong>唯一索引冲突</strong>映射为<strong>稳定的业务重复错误</strong>
    /// （<c>Duplicate</c>），完整回滚由调用方在同一事务上执行（零部分写入）。
    /// <para><strong>绝不</strong>把 <c>SqlException</c> / 错误码 / 索引名 / 连接串等内部细节暴露给调用方；
    /// 非唯一性冲突（如结构缺失）原样上抛，不被误报成重复。</para>
    /// </summary>
    public static async Task SaveEvidenceAsync(IErpDbContext db, CancellationToken cancellationToken = default)
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
    /// 保存代理服务费<strong>对账单证据</strong>写入（ERP-520：对账单表头与全部行）：把数据库层唯一索引冲突
    /// （<c>UX_AgencyServiceFeeStatements_ActiveIdentity</c> / <c>UX_AgencyServiceFeeStatementLines_ActiveSource</c>）
    /// 映射为对账单口径的<strong>稳定业务重复错误</strong>（<c>Duplicate</c>）；完整回滚由调用方在同一事务上执行
    /// （零部分写入）。与 <see cref="SaveEvidenceAsync"/> 同口径：<strong>绝不</strong>把 <c>SqlException</c> /
    /// 错误码 / 索引名 / 连接串等内部细节暴露给调用方，非唯一性冲突原样上抛。
    /// </summary>
    public static async Task SaveStatementEvidenceAsync(IErpDbContext db, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (LooksLikeUniqueViolation(ex))
        {
            throw BusinessException.Duplicate(StatementActiveEvidenceConflictText);
        }
    }

    /// <summary>
    /// 是否为唯一索引 / 唯一约束冲突（SQL Server 2601 重复键 / 2627 违反唯一约束）。
    /// 说明：应用层不引用 <c>Microsoft.Data.SqlClient</c>，故按错误码与错误文案识别；
    /// 非唯一性冲突的 <see cref="DbUpdateException"/>（如结构缺失）原样上抛，不被误报成并发重复。
    /// </summary>
    public static bool LooksLikeUniqueViolation(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
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
