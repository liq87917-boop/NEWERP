using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Domain.Common;
using ERP.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace ERP.Application.Services;

/// <summary>
/// 供应商采购发票并发护栏（ERP-382）：把供应商采购发票的<strong>修改 / 关联整体替换 / 登记 / 作废</strong>
/// 与「付款单 → 供应商采购发票」付款引用证据的<strong>登记 / 作废</strong>，收敛到同一套确定性行锁与原子事务上，
/// 使「并发不同付款单引用同一发票」「并发多张发票引用同一采购订单」「发票作废 vs 付款引用登记」
/// 「草稿修改 vs 登记」「关联替换 vs 登记」都只能得到一致证据或原子拒绝。
/// <para><b>唯一全局锁序（跨模块统一，绝不反向获取）</b>：<br/>
/// 1) 来源采购订单行（<see cref="PurchaseOrders"/>，Id <b>升序</b>；与采购订单取消 / 状态流转的
/// <c>UPDLOCK, HOLDLOCK</c> 是同一把行锁）→ 2) 供应商采购发票行（<see cref="PurchaseInvoices"/>）→
/// 3) 来源货款申请单行（ERP-380，先申请单后付款单）→ 4) 付款单行 → 5) 引用 / 证据行（关联行，
/// 由持有上述行锁的事务顺带写入）。<br/>
/// 采购订单取消（ERP-345）、付款单生命周期（ERP-379 / ERP-380）、发票模块都遵守该顺序，
/// 因此不会出现「A 持发票等订单、B 持订单等发票」的死锁环。</para>
/// <para><b>锁实现</b>：仅用 EF Core 基础 API（不依赖关系型扩展，内存库可运行）：在调用方事务内对目标行发出
/// 一条「审计时间戳刷新」的 UPDATE，取得排它行锁（X 锁，持有至事务结束），语义等价于
/// <c>SELECT ... WITH (UPDLOCK, HOLDLOCK)</c>；<c>UpdatedAt</c> 是技术审计字段、不是商业证据，因此不构成对发票 /
/// 订单 / 付款证据的静默改写。非关系型提供程序（内存库）无行锁语义，直接跳过（事务等价无事务）。</para>
/// <para><b>边界</b>：本类不落任何商业字段、不改写发票身份 / 金额 / 状态 / 关联证据 / 作废原因、不改写采购订单商业口径、
/// 不改写付款单与付款引用证据、不记账、不生成凭证 / 收付款单，也不物理删除历史证据。</para>
/// </summary>
public static class PurchaseInvoiceConcurrencyRules
{
    /// <summary>锁序口径文案（接口 / 文档同源）</summary>
    public const string GlobalLockOrderText =
        "ERP-382 唯一全局锁序：来源采购订单行（Id 升序，与采购订单取消共用同一把行锁）→ 供应商采购发票行 → " +
        "来源货款申请单行（ERP-380）→ 付款单行 → 引用 / 证据行；所有调用方都按此顺序加锁且绝不反向获取，" +
        "以求「条件判定 + 写入」原子并把容量竞争串行化。";

    /// <summary>行锁重试次数（乐观并发令牌过期时重读权威行后有界重试；行锁语义 = 阻塞后成功）。</summary>
    private const int LockRetryAttempts = 8;

    /// <summary>关系型后端（SQL Server）判定：内存库等非关系型提供程序没有行锁语义，锁定与事务等价无操作。</summary>
    public static bool IsRelationalProvider(IErpDbContext db)
        => string.Equals(db.Database.ProviderName, "Microsoft.EntityFrameworkCore.SqlServer",
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 合并本次动作需要加锁的采购订单 Id（已存储来源 + 请求来源）：去重、只保留正整数，并按 Id 升序返回，
    /// 保证多把订单行锁的确定性获取顺序（绝不反向获取下游锁）。
    /// </summary>
    public static IReadOnlyList<long> MergeOrderLockIds(IEnumerable<long>? orderIds)
        => (orderIds ?? Array.Empty<long>())
            .Where(id => id > 0)
            .Distinct()
            .OrderBy(id => id)
            .ToList();

    /// <summary>
    /// 对采购订单行加更新锁（Id 升序逐个获取）：与采购订单取消 / 状态流转的
    /// <c>SELECT ... WITH (UPDLOCK, HOLDLOCK)</c> 争用同一把行锁，因此发票关联 / 登记与订单取消、
    /// 上游订单商业改动在同一事务边界内串行化。非关系型提供程序跳过。
    /// </summary>
    public static async Task LockPurchaseOrderRowsAsync(IErpDbContext db, IEnumerable<long>? orderIds)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (!IsRelationalProvider(db)) return;

        foreach (var orderId in MergeOrderLockIds(orderIds))
            await LockRowAsync(db, orderId, isOrderRow: true);
    }

    /// <summary>
    /// 对供应商采购发票行加更新锁：把同单并发的「草稿修改 / 关联整体替换 / 登记 / 作废」与
    /// 「付款单 → 发票付款引用证据的登记 / 作废」串行化在同一事务内（发票容量竞争的唯一汇聚点）。
    /// 非关系型提供程序跳过。
    /// </summary>
    public static async Task LockInvoiceRowAsync(IErpDbContext db, long invoiceId)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (invoiceId <= 0) return;
        if (!IsRelationalProvider(db)) return;

        await LockRowAsync(db, invoiceId, isOrderRow: false);
    }

    /// <summary>
    /// 行锁 + 审计时间戳刷新：在调用方事务内对目标行发出 UPDATE 取得排它行锁（持有至事务结束），
    /// 语义等价于 <c>SELECT ... WITH (UPDLOCK, HOLDLOCK)</c>。
    /// <para>并发方先提交时本地的乐观并发令牌（<c>RowVersion</c>）会过期，SQL Server 返回 0 行受影响，
    /// EF 抛 <see cref="DbUpdateConcurrencyException"/>；行锁语义应为「阻塞后成功」，因此这里重读权威行
    /// （含新令牌）后有界重试，绝不把纯粹的锁等待误报成业务拒绝。真实业务冲突仍由调用方的锁内校验判定。</para>
    /// </summary>
    private static async Task LockRowAsync(IErpDbContext db, long id, bool isOrderRow)
    {
        for (var attempt = 1; attempt <= LockRetryAttempts; attempt++)
        {
            BaseEntity? entity = isOrderRow
                ? await db.PurchaseOrders.FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
                : await db.PurchaseInvoices.FirstOrDefaultAsync(x => x.Id == id && !x.IsDeleted);
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
                    throw BusinessException.RuleConflict(
                        "目标单据正在被并发修改，本次操作未生效：请刷新后重试（原始证据均未改变）");
            }
        }
    }

    /// <summary>
    /// 取来源货款申请单行锁（存在来源时）后再取付款单行锁，严格保持 ERP-380 的「先申请单、后付款单」顺序；
    /// 未关联申请单的历史付款场景不新增限制。
    /// </summary>
    public static async Task LockApplicationAndPaymentRowsAsync(IErpDbContext db, long paymentId)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (paymentId <= 0) return;

        var sourceApplyId = await FinancePaymentApplyLifecycleRules.ReadPaymentApplyIdAsync(db, paymentId);
        if (sourceApplyId is > 0)
            await FinancePaymentApplyLifecycleRules.LockApplyRowAsync(db, sourceApplyId.Value);

        await SupplierPaymentLifecycleRules.LockPaymentRowAsync(db, paymentId);
    }
}
