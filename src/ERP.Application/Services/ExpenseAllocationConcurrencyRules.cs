using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Domain.Common;
using ERP.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace ERP.Application.Services;

/// <summary>
/// 费用分摊并发生成护栏（ERP-386）：把「装柜费用分摊批次生成 / 显式作废」「传统拼柜分摊生成」
/// 「来源费用单商业修改 / 删除」「装柜清单参与方维护」收敛到**同一条确定性锁序 + 同一原子事务**上，
/// 使「并发同一来源生成」「生成 vs 作废」「来源费用修改 / 删除 vs 生成」「参与方维护 vs 生成」
/// 「两条等价传统分摊请求」「两条不同传统分摊请求（单号保留）」都只能得到唯一一致结果或原子拒绝。
/// <para><b>唯一全局锁序（跨模块统一，绝不反向获取）</b>：<br/>
/// 1) 来源费用单行（<c>FinanceExpenses</c>，Id <b>升序</b>；与费用单修改 / 删除争用同一把行锁）→
/// 2) 装柜清单行（<c>ContainerLoadingLists</c>；与 ERP-364 参与方维护 / ERP-384 装柜结算共用同一把行锁）→
/// 3) 参与方行（<c>ContainerLoadingListParticipants</c>，Id 升序）→
/// 4) 分摊批次行（<c>FinanceExpenseAllocationBatches</c>；作废路径）→
/// 5) 模块级单号键行（既有「费用单」<c>expense-bill</c> 菜单行 —— 单号保留 / 重复检查串行化的唯一汇聚点，
/// <b>不是</b>自由文本 <c>RefNo</c>）→ 6) 批次 / 分摊行 / 生成费用单行（由持有上述行锁的事务顺带写入）。<br/>
/// 传统分摊路径只取第 5 段（模块单号键行）：它不引用任何权威来源单据，因此绝不按自由文本柜号 / 单号猜测来源，
/// 也不把自由文本 <c>RefNo</c> 当作权威来源身份。</para>
/// <para><b>锁实现</b>：仅用 EF Core 基础 API（不依赖关系型扩展，内存库可运行，也不执行任何任意 SQL）：
/// 在调用方事务内对目标行发一条「审计时间戳刷新」的 UPDATE，取得排它行锁（X 锁，持有至事务结束），
/// 语义等价于 <c>SELECT ... WITH (UPDLOCK, HOLDLOCK)</c>；<c>UpdatedAt</c> 是技术审计字段、不是商业证据，
/// 因此不构成对费用单 / 装柜清单 / 参与方 / 批次的静默改写。非关系型提供程序（内存库）没有行锁语义，
/// 直接跳过（事务等价无事务）。</para>
/// <para><b>边界</b>：本类只做锁定与判定；不新增表 / 列 / 菜单 / 权限，不记账、不生成凭证 / 收付款 / 结算单，
/// 不改写装柜清单与明细数量 / 箱数 / 重量 / 体积、装柜结算金额、库存与库存流水，也不物理删除任何历史证据
/// （更正仍走既有显式作废：状态 0 + 原因 + 原审计留痕）。</para>
/// </summary>
public static class ExpenseAllocationConcurrencyRules
{
    /// <summary>锁序口径文案（接口 / 文档同源）</summary>
    public const string GlobalLockOrderText =
        "ERP-386 唯一全局锁序：来源费用单行（Id 升序，与费用单修改 / 删除共用同一把行锁）→ 装柜清单行（与参与方维护共用同一把行锁）→ " +
        "参与方行（Id 升序）→ 分摊批次行（作废路径）→ 模块级单号键行（既有 expense-bill 菜单行）→ 批次 / 分摊行 / 生成费用单行；" +
        "所有调用方都按此顺序加锁且绝不反向获取，以求「条件判定 + 单号保留 + 写入」原子并把重复生成竞争串行化。";

    /// <summary>模块级单号键口径文案（单号保留与重复检查的原子汇聚点，绝不用自由文本）</summary>
    public const string NumberKeyText =
        "模块级单号键 = 既有「费用单」（expense-bill）模块菜单行（与 ERP-385 授权同源）：在同一事务内只刷新其技术审计时间戳 UpdatedAt 取得排它行锁，" +
        "把「重复检查 + 单号保留 + 生成写入」串行化；系统绝不按自由文本 RefNo / 柜号推断权威来源，" +
        "批次与分摊行的权威来源身份始终是持久化的 SourceExpenseId / LoadingListId 与来源费用行本身。";

    /// <summary>边界文案（不新增权限 / 表列，不记账，不改写历史证据）</summary>
    public const string BoundaryText =
        "本护栏只保护费用分摊生成 / 作废与来源费用商业改动的并发完整性：不新增菜单 / 角色 / 用户授权或表结构，也不把空身份当作管理员；" +
        "不写库存 / 库存流水 / 资金 / 会计凭证，不产生收款、付款、核销、结算或对账结论；" +
        "不改写装柜清单 / 明细 / 结算金额，也不物理删除费用单、批次与分摊行（更正只走显式作废并保留原审计）。";

    /// <summary>行锁重试耗尽（并发方持续改写同一行）的对外文案</summary>
    public const string ConcurrentModificationText =
        "目标单据正在被并发修改，本次操作未生效：请刷新后重试（原始证据均未改变）";

    /// <summary>有效分摊批次来源费用禁止删除（fail closed）的对外文案</summary>
    public const string SourceBatchDeleteBlockedText =
        "该费用单是有效分摊批次的来源费用：请先作废分摊批次（保留历史与逐行留痕）再删除费用单"
        + "（fail closed，不改写任何批次 / 分摊行 / 生成费用单）";

    /// <summary>有效分摊批次来源费用禁止改写权威分摊基数（fail closed）的对外文案</summary>
    public const string SourceBatchEditBlockedText =
        "该费用单是有效分摊批次的来源费用：权威分摊基数（金额 / 币种 / 汇率 / 归属客户 / 费用类型 / 柜级身份）不能被修改，"
        + "请先作废分摊批次（保留历史）再修改（fail closed，不改写任何批次 / 分摊行 / 生成费用单）";

    /// <summary>行锁重试次数（乐观并发令牌过期时重读权威行后有界重试；行锁语义 = 阻塞后成功）</summary>
    private const int LockRetryAttempts = 8;

    /// <summary>关系型后端（SQL Server）判定：内存库等非关系型提供程序没有行锁语义，锁定与事务等价无操作。</summary>
    public static bool IsRelationalProvider(IErpDbContext db)
    {
        ArgumentNullException.ThrowIfNull(db);
        return string.Equals(db.Database.ProviderName, "Microsoft.EntityFrameworkCore.SqlServer",
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 生成 / 作废 / 来源费用改动使用的原子事务：关系型后端开启真实事务（失败整体回滚，绝不留半成品行，
    /// 也不会「消费」已计算的单号），非关系型提供程序返回 <c>null</c>（等价无事务，声明式校验不变）。
    /// <para>调用方（例如装柜清单参与方路由的既有可串行化事务）已经开启事务时**绝不嵌套**：
    /// 直接复用既有事务与锁，由最外层调用方提交 / 回滚；此时返回 <c>null</c> 表示「复用既有事务，本层不提交」。</para>
    /// </summary>
    public static async Task<IDbContextTransaction?> BeginTransactionIfRelationalAsync(
        IErpDbContext db, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (!IsRelationalProvider(db)) return null;
        if (db.Database.CurrentTransaction is not null) return null;

        return await db.Database.BeginTransactionAsync(ct);
    }

    /// <summary>
    /// 合并需要加锁的行 Id：去重、只保留正整数，并按 Id 升序返回，保证多把行锁的确定性获取顺序
    /// （绝不反向获取下游锁，也就不存在锁环）。
    /// </summary>
    public static IReadOnlyList<long> MergeRowLockIds(IEnumerable<long>? ids)
        => (ids ?? Array.Empty<long>())
            .Where(id => id > 0)
            .Distinct()
            .OrderBy(id => id)
            .ToList();

    /// <summary>
    /// 对来源费用单行加更新锁：与费用单「商业修改 / 软删除」争用同一把行锁，使
    /// 「来源费用改动 vs 分摊批次生成」在同一事务边界内串行化。非关系型提供程序跳过。
    /// </summary>
    public static async Task LockSourceExpenseRowsAsync(IErpDbContext db, IEnumerable<long>? expenseIds)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (!IsRelationalProvider(db)) return;

        foreach (var id in MergeRowLockIds(expenseIds))
            await LockRowAsync(db, id, LockTarget.SourceExpense);
    }

    /// <summary>
    /// 对装柜清单行加更新锁：与参与方维护（ERP-364 / ERP-384）争用同一把清单行锁，
    /// 使「参与方维护 vs 分摊生成」串行化（生成使用的参与方集合在事务内稳定）。非关系型提供程序跳过。
    /// </summary>
    public static async Task LockLoadingListRowsAsync(IErpDbContext db, IEnumerable<long>? loadingListIds)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (!IsRelationalProvider(db)) return;

        foreach (var id in MergeRowLockIds(loadingListIds))
            await LockRowAsync(db, id, LockTarget.LoadingList);
    }

    /// <summary>
    /// 对参与方行加更新锁（Id 升序）：把参与方状态 / 主参与方 / 客户快照的改写与分摊生成串行化。
    /// 非关系型提供程序跳过。
    /// </summary>
    public static async Task LockParticipantRowsAsync(IErpDbContext db, IEnumerable<long>? participantIds)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (!IsRelationalProvider(db)) return;

        foreach (var id in MergeRowLockIds(participantIds))
            await LockRowAsync(db, id, LockTarget.Participant);
    }

    /// <summary>
    /// 对分摊批次行加更新锁：把同批次的「显式作废 vs 生成 / 重复作废」串行化。非关系型提供程序跳过。
    /// </summary>
    public static async Task LockBatchRowAsync(IErpDbContext db, long batchId)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (batchId <= 0) return;
        if (!IsRelationalProvider(db)) return;

        await LockRowAsync(db, batchId, LockTarget.Batch);
    }

    /// <summary>
    /// 对模块级单号键行（既有「费用单」<c>expense-bill</c> 菜单行）加更新锁：模块内所有生成路径
    /// （传统分摊 / ERP-042 批次）的「重复检查 + 单号保留」都在同一把行锁内串行化，因此
    /// 并发等价请求最多生成一套完整费用单、不同请求的当日单号绝不重复。非关系型提供程序跳过；
    /// 菜单行不存在时（历史数据）跳过而不猜测替代键。
    /// </summary>
    public static async Task LockModuleNumberKeyAsync(IErpDbContext db)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (!IsRelationalProvider(db)) return;

        await LockRowAsync(db, 0, LockTarget.NumberKey);
    }

    /// <summary>
    /// 该费用单是否为**有效**分摊批次（状态 1、未删除）的来源费用：来源费用在批次有效期间不得被删除，
    /// 也不得改写权威分摊基数（否则会与已生成的批次 / 费用单快照产生陈旧证据）。
    /// </summary>
    public static async Task<bool> HasEffectiveBatchForSourceAsync(
        IErpDbContext db, long sourceExpenseId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (sourceExpenseId <= 0) return false;

        return await db.FinanceExpenseAllocationBatches.AsNoTracking()
            .AnyAsync(x => !x.IsDeleted
                && x.Status == ContainerExpenseAllocationRules.BatchActive
                && x.SourceExpenseId == sourceExpenseId, ct);
    }

    /// <summary>
    /// 删除前的来源证据护栏：任一行是有效分摊批次的来源费用即整体拒绝（不删除任何行、不改写任何证据），
    /// 直到显式作废批次释放。
    /// </summary>
    public static async Task EnsureSourceDeletableAsync(
        IErpDbContext db, IEnumerable<long>? expenseIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);

        foreach (var id in MergeRowLockIds(expenseIds))
        {
            if (await HasEffectiveBatchForSourceAsync(db, id, ct))
                throw BusinessException.RuleConflict($"{SourceBatchDeleteBlockedText}（费用单 Id={id}）");
        }
    }

    /// <summary>
    /// 来源费用「商业修改」护栏：当该行是有效分摊批次的来源费用时，权威分摊基数（金额 / 币种 / 汇率 /
    /// 归属客户 / 费用类型 / 柜级身份）必须保持不变；备注 / 收款方 / 付款状态等非基数字段仍可修改。
    /// </summary>
    public static void EnsureSourceAllocationBasisUnchanged(FinanceExpense stored, FinanceExpense proposed)
    {
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentNullException.ThrowIfNull(proposed);

        var storedCurrency = CurrencyAmountRules.NormalizeCurrency(stored.Currency);
        var proposedCurrency = CurrencyAmountRules.NormalizeCurrency(proposed.Currency);

        var changed = stored.Amount != proposed.Amount
            || stored.ExchangeRate != proposed.ExchangeRate
            || stored.CustomerId != proposed.CustomerId
            || !string.Equals(storedCurrency, proposedCurrency, StringComparison.Ordinal)
            || !string.Equals((stored.ExpenseType ?? string.Empty).Trim(),
                (proposed.ExpenseType ?? string.Empty).Trim(), StringComparison.Ordinal)
            || !string.Equals((stored.RefType ?? string.Empty).Trim(),
                (proposed.RefType ?? string.Empty).Trim(), StringComparison.Ordinal)
            || !string.Equals((stored.RefNo ?? string.Empty).Trim(),
                (proposed.RefNo ?? string.Empty).Trim(), StringComparison.Ordinal);

        if (changed) throw BusinessException.RuleConflict(SourceBatchEditBlockedText);
    }

    /// <summary>行锁目标类型（同一把行键对应同一业务实体；单号键行固定为模块菜单行）。</summary>
    private enum LockTarget
    {
        /// <summary>来源费用单行</summary>
        SourceExpense,

        /// <summary>装柜清单行</summary>
        LoadingList,

        /// <summary>装柜清单参与方行</summary>
        Participant,

        /// <summary>分摊批次行</summary>
        Batch,

        /// <summary>模块级单号键行（既有 expense-bill 菜单行）</summary>
        NumberKey
    }

    /// <summary>
    /// 行锁 + 审计时间戳刷新：在调用方事务内对目标行发出 UPDATE 取得排它行锁（持有至事务结束），
    /// 语义等价于 <c>SELECT ... WITH (UPDLOCK, HOLDLOCK)</c>。
    /// <para>并发方先提交时本地的乐观并发令牌（<c>RowVersion</c>）会过期，SQL Server 返回 0 行受影响，
    /// EF 抛 <see cref="DbUpdateConcurrencyException"/>；行锁语义应为「阻塞后成功」，因此这里重读权威行
    /// （含新令牌）后有界重试，绝不把纯粹的锁等待误报成业务拒绝。真实业务冲突仍由调用方的锁内校验判定。</para>
    /// </summary>
    private static async Task LockRowAsync(IErpDbContext db, long id, LockTarget target)
    {
        for (var attempt = 1; attempt <= LockRetryAttempts; attempt++)
        {
            var entity = await LoadLockEntityAsync(db, id, target);
            if (entity is null) return;

            entity.UpdatedAt = DateTime.Now;
            try
            {
                await db.SaveChangesAsync();
                return;
            }
            catch (DbUpdateConcurrencyException ex)
            {
                foreach (var entry in ex.Entries) await entry.ReloadAsync();
                if (attempt == LockRetryAttempts)
                    throw BusinessException.RuleConflict(ConcurrentModificationText);
            }
        }
    }

    /// <summary>加载加锁目标行（已删除 / 不存在返回 <c>null</c>，调用方按业务口径处理）。</summary>
    private static async Task<BaseEntity?> LoadLockEntityAsync(IErpDbContext db, long id, LockTarget target)
        => target switch
        {
            LockTarget.SourceExpense => await db.FinanceExpenses
                .FirstOrDefaultAsync(x => x.Id == id && !x.IsDeleted),
            LockTarget.LoadingList => await db.ContainerLoadingLists
                .FirstOrDefaultAsync(x => x.Id == id && !x.IsDeleted),
            LockTarget.Participant => await db.ContainerLoadingListParticipants
                .FirstOrDefaultAsync(x => x.Id == id && !x.IsDeleted),
            LockTarget.Batch => await db.FinanceExpenseAllocationBatches
                .FirstOrDefaultAsync(x => x.Id == id && !x.IsDeleted),
            _ => await db.SysMenus.FirstOrDefaultAsync(
                x => !x.IsDeleted && x.MenuCode == ExpenseAuthorizationRules.RequiredMenuCode)
        };
}
