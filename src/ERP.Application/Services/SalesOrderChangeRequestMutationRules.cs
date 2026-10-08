using ERP.Application.Common;
using ERP.Application.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace ERP.Application.Services;

/// <summary>
/// 销售订单变更申请「登记 / 编辑 / 提交 / 取消」原子性与确定性行锁护栏（ERP-415，<strong>唯一权威口径</strong>）。
/// <para><b>协议</b>：变更申请（<c>SalesOrderChangeRequests</c> / <c>SalesOrderChangeRequestDetails</c>，ERP-047
/// 不可变登记册）的<strong>登记</strong>（冻结来源快照 + 生成拟议快照）、<strong>编辑</strong>（整体替换拟议明细）、
/// <strong>提交</strong>（冻结拟议快照）与<strong>取消</strong>（状态 + 原因留痕）全部落在<strong>同一个原子事务</strong>与
/// <strong>确定性行锁</strong>内：</para>
/// <list type="number">
/// <item><b>登记</b>：先对<strong>来源销售订单行</strong>加锁（复用既有<strong>规范销售订单行锁协议</strong>
/// <see cref="SalesOrderRowLockSql"/>，与销售订单取消 / 出库审核 / 预装柜流程 / 单证生成<strong>同一把来源行锁</strong>），
/// 再在锁内重新读取<strong>实时身份 + 既有「销售订单」菜单 + ERP-097 客户数据范围</strong>与<strong>权威来源订单主表 + 明细</strong>，
/// 之后才生成申请单号、冻结来源快照与写入 —— 「来源并发取消 / 改派 / 软删除」要么在加锁前完成（锁内复核即失败，零写入），
/// 要么被阻塞到本次登记提交之后再发生，绝不出现「先读快照、后改来源，再落库」的撕裂。</item>
/// <item><b>编辑 / 提交 / 取消</b>：先对<strong>不可变持久化来源订单行</strong>加锁，再对<strong>申请行</strong>加锁
/// （同一确定性口径），随后在锁内<strong>重新读取</strong>申请（tracked，含明细）、<strong>实时身份授权</strong>与
/// <strong>当前状态</strong>；两条并发请求只有一个能提交，<strong>输家</strong>在锁内读到的已是新状态：
/// 编辑 / 提交读到「已提交 / 已取消」、取消读到「已取消」一律拒绝，绝不覆盖赢家冻结的拟议快照，
/// 也绝不覆盖赢家已保留的<strong>原始取消原因与时间戳</strong>。</item>
/// </list>
/// <para><b>锁序</b>：恒定「<b>来源销售订单行 → 变更申请行</b>」，跨模块统一、绝不反向获取（因此不存在锁环）。
/// 加锁只对<strong>来源销售订单行</strong>使用既有规范行锁协议（<c>SELECT ... WITH (UPDLOCK, HOLDLOCK)</c>，
/// <strong>不</strong>刷新技术时间戳），因此来源快照列 <c>SourceUpdatedAt</c> 始终是登记当时的<strong>真实</strong>值，
/// 绝不为加锁而改写来源的状态 / 金额 / 数量 / 客户 / 明细或任何商业字段。</para>
/// <para><b>受控写入</b>：任何写入失败（明细替换 / 快照写入 / 并发令牌过期）都随调用方事务<strong>完整回滚</strong>
/// （零部分写入），由 <see cref="SaveProposalAsync"/> 把 <see cref="DbUpdateException"/> 映射为<strong>稳定的业务冲突</strong>，
/// <strong>绝不</strong>把 <c>SqlException</c> / 错误码 / 索引名 / 连接串等内部细节暴露给调用方。
/// <strong>不</strong>新增任何表 / 列 / 索引，也不新增菜单 / 角色 / 用户授权，更不把空身份当作匿名或管理员。</para>
/// </summary>
public static class SalesOrderChangeRequestMutationRules
{
    /// <summary>行锁重试上限（并发方持续改写同一行时；耗尽即原子拒绝，绝不误报为业务拒绝）。</summary>
    public const int RowLockRetryAttempts = 8;

    /// <summary>
    /// 来源销售订单行锁语句：<strong>直接复用既有规范销售订单行锁协议</strong>
    /// （<see cref="PreLoadingSalesOrderLinkRules.LockSalesOrderRowSql"/>，与销售订单取消 / 出库审核 / 预装柜流程 /
    /// 单证生成共用同一常量，保证锁身份同源）。加锁<strong>不</strong>改写来源订单的任何字段（含 <c>UpdatedAt</c>）。
    /// </summary>
    public const string SalesOrderRowLockSql = PreLoadingSalesOrderLinkRules.LockSalesOrderRowSql;

    /// <summary>变更申请行锁的等价 T-SQL 形式（口径与来源销售订单行锁完全一致）。</summary>
    public const string ChangeRequestRowLockSql =
        "SELECT Id FROM db_owner.SalesOrderChangeRequests WITH (UPDLOCK, HOLDLOCK) WHERE Id = {0}";

    /// <summary>锁序口径文案（接口 / 文档同源）。</summary>
    public const string LockOrderText =
        "ERP-415 确定性行锁：登记先锁「来源销售订单行」，编辑 / 提交 / 取消先锁「来源销售订单行」再锁「变更申请行」，"
        + "来源行复用既有规范销售订单行锁协议（SELECT ... WITH (UPDLOCK, HOLDLOCK)，与销售订单取消 / 出库审核 / "
        + "预装柜流程 / 单证生成同一把锁），申请行锁口径一致，二者都持有至事务结束；锁序恒定「来源订单行 → 申请行」，"
        + "绝不反向获取其它锁。加锁不刷新技术时间戳，绝不为加锁而改写来源订单的状态 / 金额 / 数量 / 客户 / 明细等商业字段。";

    /// <summary>边界文案（不新增权限 / 表列 / 索引，不改写来源订单与下游记录）。</summary>
    public const string BoundaryText =
        "本护栏只保护变更申请「登记 / 编辑 / 提交 / 取消」的并发原子性：不新增菜单 / 角色 / 用户授权或表 / 列 / 索引，"
        + "也不把空身份当作匿名或管理员；不改写来源销售订单的状态、金额、数量、客户与明细，不生成任何库存移动 / 出运 / "
        + "收付款 / 发票 / 单证 / 财务记录，也不删除历史证据与审计留痕。";

    /// <summary>行锁重试耗尽（并发方持续改写同一行）的对外文案。</summary>
    public const string ConcurrentMutationText =
        "变更申请正在被并发修改，本次操作未生效：请刷新后重试（来源快照与拟议证据均未改变）";

    /// <summary>来源销售订单在锁内已并发取消 / 软删除 / 不存在时的拒绝文案（fail closed）。</summary>
    public const string SourceUnavailableText =
        "来源销售订单不存在或已删除（或已并发作废）：变更申请操作不能继续（fail closed，未写入任何数据）";

    /// <summary>变更申请在锁内已被并发删除 / 不存在时的拒绝文案（fail closed）。</summary>
    public const string RequestUnavailableText =
        "变更申请不存在或已删除（或已并发删除）：本次操作未生效（fail closed，未写入任何数据）";

    /// <summary>受控写入失败文案：只说明事务未提交并已完整回滚，绝不暴露驱动异常 / 错误码 / 索引名。</summary>
    public const string WriteConflictText =
        "变更申请写入未完成（数据库写入冲突）：本次操作已完整回滚，零部分写入，请刷新后重试";

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
    /// 登记 / 编辑 / 提交 / 取消使用的原子事务：关系型后端开启真实事务（失败整体回滚，绝不留半成品申请）；
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
    /// 锁执行口径说明：<see cref="SalesOrderRowLockSql"/> 与 <see cref="ChangeRequestRowLockSql"/> 由<strong>调用方</strong>
    /// （Api 层控制器，具备原始 SQL 执行能力）在<strong>同一个可串行化事务</strong>内<strong>按「来源订单行 → 申请行」顺序</strong>
    /// 执行（与既有销售订单取消 / 出库审核 / 采购创建同一位置口径），本类只保留锁语句常量、锁序文案与事务帮助方法。
    /// 加锁<strong>不</strong>改写来源订单任何字段（含 <c>UpdatedAt</c>），因此登记快照 <c>SourceUpdatedAt</c> 保持真实值；
    /// 内存库等非关系型提供程序无行锁语义，等价无操作。
    /// </summary>
    public const string LockExecutionText =
        "行锁由调用方（Api 层）在同一可串行化事务内按「来源销售订单行 → 变更申请行」顺序执行 SQL 表提示；"
        + "非关系型提供程序无行锁语义等价无操作。";

    // ==================== 3. 锁内实时授权 ====================

    /// <summary>
    /// 锁内重新解析实时授权范围：携带实时身份（<paramref name="actingUserId"/>）时按规范口径
    /// <see cref="SalesOrderChangeRequestAuthorizationRules.EnsureAccessAuthorizedAsync"/> 重新解析
    /// （实时身份 + 既有「销售订单」菜单 + ERP-097 客户数据范围，<strong>绝不</strong>信任加锁前的授权快照）；
    /// 进程内无身份直调（<c>null</c>）时沿用调用方传入的范围，保持既有免授权口径。
    /// </summary>
    public static async Task<SalespersonDataScope?> ResolveLiveScopeAsync(
        IErpDbContext db, long? actingUserId, SalespersonDataScope? fallbackScope,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (actingUserId is null or <= 0) return fallbackScope;

        return await SalesOrderChangeRequestAuthorizationRules.EnsureAccessAuthorizedAsync(
            db, actingUserId, ct);
    }

    // ==================== 4. 受控写入 ====================

    /// <summary>
    /// 保存变更申请写入：把数据库层写入冲突（<see cref="DbUpdateException"/> / 并发令牌过期）映射为
    /// <strong>稳定的业务冲突</strong>，完整回滚由调用方在同一事务上执行（零部分写入）。
    /// <para><strong>绝不</strong>把 <c>SqlException</c> / 错误码 / 索引名 / 连接串等内部细节暴露给调用方；
    /// 非数据库异常原样上抛，不被误报为写入冲突。</para>
    /// </summary>
    public static async Task SaveProposalAsync(IErpDbContext db, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            throw BusinessException.RuleConflict(WriteConflictText);
        }
    }
}
