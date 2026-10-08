using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace ERP.Application.Services;

/// <summary>
/// 形式发票 PI（<see cref="ProformaInvoice"/>，<c>api/sales/proforma-invoices</c>）生命周期变更与
/// 「PI → 销售订单」转换的共享<strong>确定性来源行锁 + 原子事务 + 锁内权威复核</strong>护栏（ERP-399）。
/// <list type="number">
/// <item><b>来源行锁</b>：修改 / 提交 / 审核 / 销审 / 取消 / 作废 / 删除 / 批量删除，以及「转销售订单」，
/// 都在重复检测、单号生成、字段改写<b>之前</b>先对 <b>PI 来源行</b>取得排它行锁
/// （<see cref="PiRowLockSql"/>，持有至事务结束）；取得方式与 ERP-395 父单证行锁同源
/// （仅用 EF Core 基础 API 的「审计时间戳刷新」<c>UPDATE</c>，语义等价 <c>SELECT ... WITH (UPDLOCK, HOLDLOCK)</c>）。
/// 批量删除按 PI <b>Id 升序</b>确定性加锁（去重、仅正整数），绝不反向获取其它锁，因此不存在锁环。</item>
/// <item><b>锁内权威复核</b>：取得 PI 行锁之后必须由调用方<b>重新加载</b> PI（不是复用加锁前的内存实体），
/// 并复核持久化生命周期状态 / <c>RowVersion</c>、实时客户范围授权、有效明细行、以及既有的
/// <c>SalesOrderConversion</c> 转换资格（来源字段唯一守卫）；任一不满足即原子拒绝。</item>
/// <item><b>目标订单</b>：转换只在<b>PI 行锁内</b>复核「是否已存在来源销售订单」（<c>SourcePiId</c>），
/// 然后 <c>INSERT</c> 全新销售订单行 —— 目标订单没有既有行可加锁，重复生成因此由来源行锁 + 锁内重读唯一化；
/// 已完成且存在下游链接的 PI 一律禁止重新打开 / 破坏性变更，保留显式历史，<b>绝不</b>用「取消目标订单」
/// 的反向冲销伪造一致性。</item>
/// <item><b>原子回滚</b>：并发生命周期冲突、转换资格不符、编号生成失败、数据库写入失败等任一步失败都整体回滚，
/// 状态 / 字段 / 已预约的销售订单编号与新建订单一起回滚，绝不留下半成品订单或撕裂的来源状态。</item>
/// </list>
/// <para><b>边界</b>：不新增表 / 列 / 索引 / 菜单 / 角色 / 用户授权，不把空身份当作匿名或管理员，也不伪造任何授权；
/// 不改写库存与库存流水、客户主数据、来源报价单、财务记录，也不做任何财务 / 库存过账；
/// 行锁只刷新 PI 的技术审计时间戳 <c>UpdatedAt</c>（非商业证据，不参与任何金额 / 状态判定）。</para>
/// </summary>
public static class ProformaInvoiceMutationRules
{
    /// <summary>
    /// PI 来源行锁语句（契约 / 文档同源）：<c>UPDLOCK, HOLDLOCK</c> 语义等价可串行化行锁，持有至调用方事务结束。
    /// 实际执行走 <see cref="LockPiRowAsync"/> 的「审计时间戳刷新」UPDATE（仅用 EF Core 基础 API）。
    /// </summary>
    public const string PiRowLockSql =
        "SELECT Id FROM db_owner.ProformaInvoices WITH (UPDLOCK, HOLDLOCK) WHERE Id = {0}";

    /// <summary>
    /// 目标销售订单行锁语句（与销售订单取消 / 出库审核 / 预装柜流程 / 单证生成共用同一把来源行锁）：
    /// 转换只在 PI 行锁内**只读复核**既有来源订单，不反向获取下游锁；共享常量保证锁身份同源。
    /// </summary>
    public const string SalesOrderRowLockSql = PreLoadingSalesOrderLinkRules.LockSalesOrderRowSql;

    /// <summary>
    /// 报价单（Quotation）来源行锁语句（与 ERP-400 共用同一把报价单行锁）：跨单据链恒定锁序为
    /// 「报价单行锁 → PI 行锁 / 销售订单行锁」；PI 侧只读引用该常量，保证「报价单 → PI」与
    /// 「报价单 → 销售订单」共用同一把报价单行锁，绝不反向获取上游报价单锁。
    /// </summary>
    public const string QuotationRowLockSql = QuotationMutationRules.QuotationRowLockSql;

    /// <summary>
    /// 跨单据链锁序口径文案（接口 / 文档同源）：报价单 → PI / 销售订单的写路径恒定先取报价单行锁，
    /// 再在 PI 行锁内处理 PI 生命周期，绝不反向获取上游锁。
    /// </summary>
    public const string CrossDocumentLockOrderText =
        "跨单据链确定性锁序：报价单来源行锁（db_owner.Quotations）→ PI 来源行锁（db_owner.ProformaInvoices）→ "
        + "销售订单来源行锁；任何写路径都不得逆序获取，因此不存在锁环。";

    /// <summary>行锁重试次数（乐观并发令牌过期时重读权威行后有界重试；行锁语义 = 阻塞后成功）。</summary>
    public const int RowLockRetryAttempts = 8;

    private const int LockRetryAttempts = RowLockRetryAttempts;

    /// <summary>锁序口径文案（接口 / 文档同源）。</summary>
    public const string LockOrderText =
        "ERP-399 PI 生命周期的确定性锁协议：修改 / 提交 / 审核 / 销审 / 取消 / 作废 / 删除 / 批量删除与「转销售订单」都先取" +
        "「PI 来源行锁」（db_owner.ProformaInvoices WITH (UPDLOCK, HOLDLOCK)，等价实现为审计时间戳刷新的 UPDATE），" +
        "把同一 PI 的并发写串行化在同一原子事务内；批量删除按 PI Id 升序确定性加锁（去重、仅正整数）。" +
        "转换在 PI 行锁内只读复核既有来源销售订单（SourcePiId）后再 INSERT 全新订单，绝不反向获取下游订单行锁。";

    /// <summary>边界文案（不新增权限 / 表列，不改写来源与历史证据，不做财务 / 库存过账）。</summary>
    public const string BoundaryText =
        "本护栏只保护 PI 生命周期变更与 PI → 销售订单转换的并发完整性：不新增菜单 / 角色 / 用户授权或表结构，" +
        "也不把空身份当作管理员；不改写数量 / 单价 / 金额 / 合计 / 定金 / 币种 / 汇率 / 单位等原始商业语义（服务端口径不变），" +
        "不改写来源报价单、销售订单、客户主数据、库存与库存流水、发票、费用或财务记录，不做任何财务 / 库存过账；" +
        "不删除历史证据，不用「取消目标订单」的反向冲销伪造一致性；行锁只刷新 PI 技术审计时间戳 UpdatedAt（非商业证据）。";

    /// <summary>并发方持续改写同一 PI 行时对外给出的拒绝文案。</summary>
    public const string ConcurrentMutationText =
        "目标 PI 正在被并发修改，本次操作未生效：请刷新后重试（原始证据均未改变）";

    /// <summary>持久化 <c>RowVersion</c> 与调用方持有的乐观令牌不一致（被并发改写）时的拒绝文案。</summary>
    public const string StaleRowVersionText =
        "该 PI 已被并发操作改写（RowVersion 过期）：本次操作未生效，请刷新后重试（绝不覆盖赢家，原始证据均未改变）";

    /// <summary>已完成且存在下游销售订单链接时禁止重新打开 / 破坏性变更的拒绝文案。</summary>
    public const string DownstreamLinkedText =
        "该 PI 已完成并存在下游销售订单链接：禁止重新打开或破坏性变更（保留显式历史证据，不做取消目标订单的反向冲销）";

    /// <summary>转换资格：PI 未审核的拒绝文案（与既有转换守卫同源）。</summary>
    public const string NotApprovedText = "PI 未审核，请先审核后再转销售订单";

    /// <summary>转换资格：PI 无有效明细的拒绝文案（与既有转换守卫同源）。</summary>
    public const string NoActiveDetailsConversionText = "PI 无商品明细，不能转销售订单";

    /// <summary>审核资格：PI 无有效明细的拒绝文案（与既有审核守卫同源）。</summary>
    public const string NoActiveDetailsApproveText = "PI 无商品明细，不能审核";

    /// <summary>已完成（已转销售订单）PI 不能重复转换的拒绝文案。</summary>
    public const string AlreadyCompletedText = "PI 已完成（已转销售订单），不能重复转换";

    // ==================== 1. 关系型判定 / 事务 / 来源行锁 ====================

    /// <summary>关系型后端（SQL Server）判定：内存库等非关系型提供程序没有行锁语义，锁定与事务等价无操作。</summary>
    public static bool IsRelationalProvider(IErpDbContext db)
        => TradeDocumentMutationRules.IsRelationalProvider(db);

    /// <summary>
    /// 生命周期变更 / 转换使用的原子事务（复用 ERP-395 既有口径）：关系型后端开启真实事务，
    /// 失败整体回滚；调用方已开启事务时<strong>绝不嵌套</strong>（返回 <c>null</c> 表示复用外层事务）；
    /// 非关系型提供程序返回 <c>null</c>（等价无事务，声明式校验不变）。
    /// </summary>
    public static Task<IDbContextTransaction?> BeginMutationTransactionAsync(
        IErpDbContext db, CancellationToken ct = default)
        => TradeDocumentMutationRules.BeginMutationTransactionAsync(db, ct);

    /// <summary>
    /// 合并需要加锁的 PI Id：去重、只保留正整数，并按 Id 升序返回，保证多把行锁的确定性获取顺序
    /// （绝不反向获取，也就不存在锁环）。
    /// </summary>
    public static IReadOnlyList<long> MergeLockIds(IEnumerable<long>? ids)
        => (ids ?? Array.Empty<long>())
            .Where(id => id > 0)
            .Distinct()
            .OrderBy(id => id)
            .ToList();

    /// <summary>
    /// 对 **PI 来源行**加排它行锁：把「同单并发的生命周转变更 / 删除 / 转换」串行化在同一事务内。
    /// 返回 <c>false</c> 表示该行不存在 / 已被并发删除。
    /// <para>实现与 ERP-395 父单证行锁同源（仅用 EF Core 基础 API）：在调用方事务内对 PI 行发一条
    /// 「审计时间戳刷新」<c>UPDATE</c>（<c>UpdatedAt</c> 为技术审计字段、不是商业证据），取得排它行锁（X 锁，
    /// 持有至事务结束）；并发方先提交使本地乐观令牌 <c>RowVersion</c> 过期时重读权威行后**有界重试**，
    /// 绝不把纯粹锁等待误报成业务拒绝。</para>
    /// <para>内存库等非关系型提供程序无行锁语义，直接返回 <c>true</c>（存在性仍由锁内权威重读判定）。</para>
    /// </summary>
    public static async Task<bool> LockPiRowAsync(IErpDbContext db, long piId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (piId <= 0) return false;
        if (!IsRelationalProvider(db)) return true;

        for (var attempt = 1; attempt <= LockRetryAttempts; attempt++)
        {
            var pi = await db.ProformaInvoices
                .FirstOrDefaultAsync(o => o.Id == piId && !o.IsDeleted, ct);
            if (pi is null) return false;

            pi.UpdatedAt = DateTime.Now;
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
                        return false; // PI 行已被并发事务删除
                    }
                }

                if (attempt == LockRetryAttempts)
                    throw BusinessException.RuleConflict(ConcurrentMutationText);
            }
        }

        return false;
    }

    /// <summary>对一组 PI 来源行按 <b>Id 升序</b>确定性加锁（批量删除等）。非关系型提供程序跳过。</summary>
    public static async Task LockPiRowsAsync(IErpDbContext db, IEnumerable<long>? piIds,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (!IsRelationalProvider(db)) return;

        foreach (var id in MergeLockIds(piIds))
            await LockPiRowAsync(db, id, ct);
    }

    /// <summary>事务回滚 / 显式拒绝后丢弃变更跟踪器中的半成品变更，绝不残留部分写入（内存库同样生效）。</summary>
    public static void DiscardTrackedChanges(IErpDbContext db)
        => TradeDocumentMutationRules.DiscardTrackedChanges(db);

    // ==================== 2. 锁内权威事实 ====================

    /// <summary>有效（未删除）明细行数量：审核 / 转换的资格判定以锁内权威重读为准。</summary>
    public static int ActiveDetailCount(ProformaInvoice? pi)
        => pi?.Details.Count(d => !d.IsDeleted) ?? 0;

    /// <summary>是否已存在未删除的下游销售订单链接（只按持久化 <c>SourcePiId</c> 判定，绝不按自由文本推断）。</summary>
    public static Task<bool> HasDownstreamLinkAsync(IErpDbContext db, long piId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (piId <= 0) return Task.FromResult(false);
        return db.SalesOrders.AsNoTracking().AnyAsync(o => o.SourcePiId == piId && !o.IsDeleted, ct);
    }

    /// <summary>读取来源 PI 的下游销售订单（未删除；用于重复生成守卫与提示单号）。</summary>
    public static Task<SalesOrder?> FindDownstreamOrderAsync(IErpDbContext db, long piId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (piId <= 0) return Task.FromResult<SalesOrder?>(null);
        return db.SalesOrders.AsNoTracking()
            .FirstOrDefaultAsync(o => o.SourcePiId == piId && !o.IsDeleted, ct);
    }

    /// <summary>
    /// 锁内权威重读必须与调用方持有的乐观令牌一致：不一致即判定为并发改写并原子拒绝（绝不静默覆盖赢家）。
    /// 任一侧为 <c>null</c>（非关系型 / 历史行）时不判定。
    /// </summary>
    public static void EnsurePersistedVersionCurrent(byte[]? expectedRowVersion, byte[]? persistedRowVersion)
    {
        if (expectedRowVersion is null || persistedRowVersion is null) return;
        if (!expectedRowVersion.SequenceEqual(persistedRowVersion))
            throw BusinessException.RuleConflict(StaleRowVersionText);
    }

    // ==================== 3. 生命周期守卫（锁内重新加载后调用） ====================

    /// <summary>
    /// 已完成且有下游链接的 PI 一律冻结：禁止修改 / 提交 / 审核 / 销审 / 取消 / 作废 / 删除 / 批量删除，
    /// 保留显式历史。<b>无下游链接</b>时保留既有允许的流转（本护栏不新增任何状态机限制）。
    /// </summary>
    public static void EnsureNoDownstreamLink(bool hasDownstreamLink)
    {
        if (hasDownstreamLink)
            throw BusinessException.RuleConflict(DownstreamLinkedText);
    }

    /// <summary>修改资格（锁内调用）：仅待提交 / 已提交可改，已审核 / 已完成 / 已作废一律拒绝。</summary>
    public static void EnsureEditAllowed(DocumentStatus stored)
    {
        if (stored is DocumentStatus.Approved or DocumentStatus.Cancelled or DocumentStatus.Completed)
            throw BusinessException.RuleConflict("已审核、已转订单或已作废的 PI 不可修改，请先销审");
    }

    /// <summary>提交资格（锁内调用）：仅待提交可提交。</summary>
    public static void EnsureSubmitAllowed(DocumentStatus stored)
    {
        if (stored != DocumentStatus.Pending)
            throw BusinessException.RuleConflict("当前状态不允许该操作");
    }

    /// <summary>审核资格（锁内调用，含有效明细行复核），与既有审核守卫同口径。</summary>
    public static void EnsureApproveAllowed(DocumentStatus stored, int activeDetailCount)
    {
        if (stored == DocumentStatus.Cancelled)
            throw BusinessException.RuleConflict("已作废的 PI 不能审核");
        if (stored == DocumentStatus.Completed)
            throw BusinessException.RuleConflict("已转销售订单的 PI 不能审核");
        if (stored == DocumentStatus.Approved)
            throw BusinessException.RuleConflict("PI 已审核");
        if (activeDetailCount <= 0)
            throw BusinessException.RuleConflict(NoActiveDetailsApproveText);
    }

    /// <summary>销审（重新打开）资格（锁内调用）：仅已审核可销审。</summary>
    public static void EnsureUnauditAllowed(DocumentStatus stored)
    {
        if (stored != DocumentStatus.Approved)
            throw BusinessException.RuleConflict("仅已审核的 PI 可销审");
    }

    /// <summary>取消资格（锁内调用）：保留既有语义（无状态限制），由下游链接守卫单独冻结。</summary>
    public static void EnsureCancelAllowed(DocumentStatus stored)
    {
        _ = stored; // 既有允许口径保持不变；仅由 EnsureNoDownstreamLink 冻结已完成且已链接的 PI。
    }

    /// <summary>作废资格（锁内调用）：已作废 / 已转销售订单拒绝。</summary>
    public static void EnsureVoidAllowed(DocumentStatus stored)
    {
        if (stored == DocumentStatus.Cancelled)
            throw BusinessException.RuleConflict("PI 已作废");
        if (stored == DocumentStatus.Completed)
            throw BusinessException.RuleConflict("已转销售订单的 PI 不能作废");
    }

    /// <summary>删除资格（锁内调用）：仅待提交可删。</summary>
    public static void EnsureDeleteAllowed(DocumentStatus stored)
    {
        if (stored != DocumentStatus.Pending)
            throw BusinessException.RuleConflict("仅待提交状态的单据可删除");
    }

    /// <summary>批量删除资格（锁内调用）：任一行非待提交即整体拒绝（不做部分删除）。</summary>
    public static void EnsureBatchDeleteAllowed(IEnumerable<DocumentStatus> storedStatuses)
    {
        ArgumentNullException.ThrowIfNull(storedStatuses);
        if (storedStatuses.Any(status => status != DocumentStatus.Pending))
            throw BusinessException.RuleConflict("仅待提交状态的单据可删除");
    }

    // ==================== 4. 转换资格（锁内重新加载后调用） ====================

    /// <summary>
    /// 「PI → 销售订单」转换资格（锁内权威重读后调用，与 <c>SalesOrderConversion.FromProformaInvoiceAsync</c>
    /// 同口径）：已作废拒绝 → 已存在来源订单（重复生成）拒绝 → 已完成拒绝 → 未审核拒绝 → 无有效明细拒绝。
    /// 任一不满足即原子拒绝，绝不消耗单号、绝不写入订单、绝不改写来源 PI。
    /// </summary>
    public static void EnsureConversionEligible(ProformaInvoice pi, SalesOrder? existingOrder, int activeDetailCount)
    {
        ArgumentNullException.ThrowIfNull(pi);

        if (pi.Status == DocumentStatus.Cancelled)
            throw BusinessException.RuleConflict("已作废的 PI 不能转销售订单");
        if (existingOrder is not null)
            throw BusinessException.RuleConflict($"该 PI 已生成销售订单：{existingOrder.OrderNo}，不能重复生成");
        if (pi.Status == DocumentStatus.Completed)
            throw BusinessException.RuleConflict(AlreadyCompletedText);
        if (pi.Status != DocumentStatus.Approved)
            throw BusinessException.RuleConflict(NotApprovedText);
        if (activeDetailCount <= 0)
            throw BusinessException.RuleConflict(NoActiveDetailsConversionText);
    }

    /// <summary>
    /// 普通销售订单表单保存**显式链接来源 PI** 时的既有转换资格（ERP-401）：与
    /// <see cref="EnsureConversionEligible"/>（「PI → 销售订单」直接转换）**完全同口径**且复用同一实现 ——
    /// 已作废 / 未审核 / 无有效明细 / 已完成一律拒绝；唯一目标由
    /// <c>SalesOrderConversion.FindOtherTargetAsync</c> 在来源行锁内独立复核。
    /// 本方法不写库、不改写来源 PI、不消耗单据号。
    /// </summary>
    public static void EnsureManualOrderLinkEligible(ProformaInvoice pi, int activeDetailCount)
        => EnsureConversionEligible(pi, null, activeDetailCount);

    /// <summary>
    /// 普通表单保存链接来源 PI 的锁序口径（接口 / 文档同源，ERP-401）：与「PI → 销售订单」直接转换共用同一把
    /// PI 来源行锁，人工保存与直接转换因此串行化在同一事务内，同一 PI 至多一张目标销售订单。
    /// </summary>
    public const string ManualLinkLockOrderText =
        "ERP-401：普通销售订单表单显式链接来源 PI 时先取「PI 来源行锁」"
        + "（db_owner.ProformaInvoices WITH (UPDLOCK, HOLDLOCK)，等价实现为审计时间戳刷新的 UPDATE），"
        + "与「PI → 销售订单」直接转换共用同一把来源行锁：并发人工保存与直接转换串行化在同一原子事务内，"
        + "同一 PI 至多一张完整目标订单；不反向获取下游锁，因此不存在锁环。";
}
