using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Domain.Common;
using ERP.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace ERP.Application.Services;

/// <summary>
/// 客户销项发票并发护栏（ERP-383）：把客户销项发票证据的<strong>修改 / 分摊整体替换 / 登记 / 作废</strong>
/// 与「客户收款单 → 客户销项发票」收款分摊证据的<strong>登记 / 作废</strong>收敛到同一套确定性行锁与原子事务上，
/// 使「并发不同收款单分摊同一发票」「并发多张发票分摊同一销售订单」「发票作废 vs 收款分摊登记」
/// 「草稿修改 vs 登记」「分摊替换 vs 登记」都只能得到一致证据或原子拒绝。
/// <para><b>唯一全局锁序（跨模块统一，绝不反向获取）</b>：<br/>
/// 1) 来源销售订单行（<see cref="SalesOrders"/>，Id <b>升序</b>；与 ERP-347 销售订单取消 / ERP-343 出库审核的
/// <c>UPDLOCK, HOLDLOCK</c> 是同一把行锁）→ 2) 客户销项发票行（<see cref="CustomerSalesInvoiceEvidences"/>）→
/// 3) 客户收款单行（<see cref="FinanceReceipts"/>，与 ERP-349 / ERP-378 收款单生命周期、ERP-053 / ERP-071
/// 分摊写入共用同一把行锁）→ 4) 引用 / 分摊 / 证据行（<see cref="CustomerSalesInvoiceAllocations"/> /
/// <see cref="CustomerSalesInvoiceCollectionAllocations"/> / <see cref="CustomerReceiptAllocations"/>，
/// 由持有上述行锁的事务顺带写入）。<br/>
/// 销售订单取消（ERP-347）、收款单生命周期（ERP-378）与发票模块都遵守该顺序，
/// 因此不会出现「A 持发票等收款单、B 持收款单等发票」的死锁环。</para>
/// <para><b>锁实现</b>：仅用 EF Core 基础 API（不依赖关系型扩展，内存库可运行）：在调用方事务内对目标行发出
/// 一条「审计时间戳刷新」的 UPDATE，取得排它行锁（X 锁，持有至事务结束），语义等价于
/// <c>SELECT ... WITH (UPDLOCK, HOLDLOCK)</c>；<c>UpdatedAt</c> 是技术审计字段、不是商业证据，因此不构成对发票 /
/// 销售订单 / 收款单的静默改写。非关系型提供程序（内存库）无行锁语义，直接跳过（事务等价无事务）。</para>
/// <para><b>边界</b>：本类不落任何商业字段、不改写发票身份 / 金额 / 状态 / 分摊证据 / 作废原因、不改写销售订单商业口径、
/// 不改写收款单与既有分摊证据、不记账、不生成凭证 / 收款 / 付款 / 结算单，也不物理删除历史证据。</para>
/// </summary>
public static class CustomerSalesInvoiceConcurrencyRules
{
    /// <summary>唯一全局锁序口径文案（接口 / 文档同源）</summary>
    public const string GlobalLockOrderText =
        "ERP-383 唯一全局锁序：来源销售订单行（Id 升序，与销售订单取消 / 出库审核共用同一把行锁）→ 客户销项发票行 → " +
        "客户收款单行（ERP-378 / ERP-053 / ERP-071 共用同一把行锁）→ 引用 / 分摊 / 证据行；" +
        "所有调用方都按此顺序加锁且绝不反向获取，以求「条件判定 + 写入」原子并把容量竞争串行化。";

    /// <summary>行锁重试次数（乐观并发令牌过期时重读权威行后有界重试；行锁语义 = 阻塞后成功）。</summary>
    private const int LockRetryAttempts = 8;

    /// <summary>关系型后端（SQL Server）判定：内存库等非关系型提供程序没有行锁语义，锁定与事务等价无操作。</summary>
    public static bool IsRelationalProvider(IErpDbContext db)
        => string.Equals(db.Database.ProviderName, "Microsoft.EntityFrameworkCore.SqlServer",
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 合并本次动作需要加锁的销售订单 Id（已存储来源 + 请求来源）：去重、只保留正整数，并按 Id 升序返回，
    /// 保证多把订单行锁的确定性获取顺序（绝不反向获取下游锁）。
    /// </summary>
    public static IReadOnlyList<long> MergeOrderLockIds(IEnumerable<long>? orderIds)
        => (orderIds ?? Array.Empty<long>())
            .Where(id => id > 0)
            .Distinct()
            .OrderBy(id => id)
            .ToList();

    /// <summary>
    /// 对来源销售订单行加更新锁（Id 升序逐个获取）：与销售订单取消 / 出库审核的
    /// <c>SELECT ... WITH (UPDLOCK, HOLDLOCK)</c> 争用同一把行锁，因此发票分摊 / 登记与订单取消、
    /// 收款引用登记在同一事务边界内串行化。非关系型提供程序跳过。
    /// </summary>
    public static async Task LockSalesOrderRowsAsync(IErpDbContext db, IEnumerable<long>? orderIds)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (!IsRelationalProvider(db)) return;

        foreach (var orderId in MergeOrderLockIds(orderIds))
            await LockRowAsync(db, orderId, LockTarget.SalesOrder);
    }

    /// <summary>
    /// 对客户销项发票行加更新锁：把同单并发的「草稿修改 / 分摊整体替换 / 登记 / 作废」与
    /// 「收款单 → 发票分摊证据的登记 / 作废」串行化在同一事务内（发票同币种容量竞争的唯一汇聚点）。
    /// 非关系型提供程序跳过。
    /// </summary>
    public static async Task LockInvoiceRowAsync(IErpDbContext db, long invoiceId)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (invoiceId <= 0) return;
        if (!IsRelationalProvider(db)) return;

        await LockRowAsync(db, invoiceId, LockTarget.CustomerSalesInvoice);
    }

    /// <summary>
    /// 对客户收款单行加更新锁（复用 ERP-378 既有实现，锁序第 3 段）：与收款单生命周期
    /// （取消 / 删除 / 改金额）及现有分摊证据写入互斥。非关系型提供程序跳过。
    /// </summary>
    public static Task LockReceiptRowAsync(IErpDbContext db, long receiptId)
    {
        ArgumentNullException.ThrowIfNull(db);
        return CustomerReceiptLifecycleRules.LockReceiptRowAsync(db, receiptId);
    }

    /// <summary>
    /// 按唯一全局锁序对「发票行 → 收款单行」加锁（ERP-073 收款分摊登记 / 作废使用）：
    /// 先发票行（发票同币种收款容量竞争的汇聚点），再收款单行（与收款单生命周期互斥）。
    /// </summary>
    public static async Task LockInvoiceAndReceiptRowsAsync(IErpDbContext db, long invoiceId, long receiptId)
    {
        ArgumentNullException.ThrowIfNull(db);
        await LockInvoiceRowAsync(db, invoiceId);
        if (receiptId > 0) await LockReceiptRowAsync(db, receiptId);
    }

    /// <summary>行锁目标类型（同一把行键对应同一业务实体）。</summary>
    private enum LockTarget
    {
        /// <summary>来源销售订单行</summary>
        SalesOrder,

        /// <summary>客户销项发票证据行</summary>
        CustomerSalesInvoice
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
            BaseEntity? entity = target == LockTarget.SalesOrder
                ? await db.SalesOrders.FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
                : await db.CustomerSalesInvoiceEvidences.FirstOrDefaultAsync(x => x.Id == id && !x.IsDeleted);
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
}
