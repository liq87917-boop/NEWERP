using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace ERP.Application.Services;

/// <summary>
/// 询价单 Inquiry（<c>api/inquiries</c>）生命周期变更、「询价单 → 报价单」转换与批量删除的
/// 共享<strong>确定性来源行锁 + 原子事务 + 锁内权威复核</strong>护栏（ERP-402）。
/// <list type="number">
/// <item><b>来源行锁</b>：修改 / 提交 / 审核 / 取消 / 删除 / 批量删除 / 转报价单，都在重复检测、单号生成、
/// 字段改写<b>之前</b>先对 <b>询价单来源行</b>取得排它行锁（<see cref="InquiryRowLockSql"/>，持有至事务结束）；
/// 取得方式与 ERP-395 父单证行锁、ERP-399 PI 行锁、ERP-400 报价单行锁同源（仅用 EF Core 基础 API 的
/// 「审计时间戳刷新」<c>UPDATE</c>，语义等价 <c>SELECT ... WITH (UPDLOCK, HOLDLOCK)</c>）。
/// 批量删除按询价单 <b>Id 升序</b>确定性加锁（去重、仅正整数）。</item>
/// <item><b>锁序（与报价单生命周期兼容）</b>：跨单据链恒定先取「询价单来源行锁」，再取「报价单来源行锁」
/// （<see cref="QuotationRowLockSql"/>，与 ERP-400 生命周期 / 版本 / 转换共用同一把），
/// 之后才是 PI / 销售订单行锁；写路径绝不反向获取上游锁，因此不存在锁环。</item>
/// <item><b>锁内权威复核</b>：取得询价单行锁之后必须由调用方<b>重新加载</b>询价单（不是复用加锁前的内存实体），
/// 并复核持久化生命周期状态 / <c>RowVersion</c>、实时客户范围授权、有效明细行与既有转换资格；
/// 存在<b>实时（未删除）报价单下游</b>的询价单一律冻结 —— 已转换来源不得被重新打开 / 取消 / 删除，
/// 保留显式历史，<b>绝不</b>做「取消报价单」的反向自动冲销。</item>
/// <item><b>转换唯一化</b>：先取询价单来源行锁，再锁内权威重读询价单与既有来源报价单（持久化
/// <see cref="Quotation.InquiryId"/>），重复检测 / 单号生成 / 草稿构造全部在锁内完成，
/// 同一询价单至多产生一张完整报价单（既有规则）。</item>
/// <item><b>原子回滚</b>：并发生命周期冲突、转换资格不符、编号生成失败、数据库写入失败等任一步失败都
/// 整体回滚，状态 / 字段 / 已预约的报价单编号与新建单据一起回滚，绝不留下半成品或撕裂的来源状态。</item>
/// </list>
/// <para><b>边界</b>：不新增表 / 列 / 索引 / 菜单 / 角色 / 用户授权，不把空身份当作匿名或管理员，也不伪造任何授权；
/// 不改写库存与库存流水、客户主数据、报价单 / PI / 销售订单、财务记录，也不做任何财务 / 库存过账；
/// 不改写询价单明细 / 数量 / 单价 / 金额 / 币种 / 汇率 / 单位等原始商业语义（服务端口径不变）；
/// 行锁只刷新询价单的技术审计时间戳 <c>UpdatedAt</c>（非商业证据，不参与任何金额 / 状态判定）。</para>
/// </summary>
public static class InquiryMutationRules
{
    /// <summary>
    /// 询价单来源行锁语句（契约 / 文档同源，跨单据锁序的<b>第一把锁</b>）：<c>UPDLOCK, HOLDLOCK</c> 语义等价
    /// 可串行化行锁，持有至调用方事务结束。实际执行走 <see cref="LockInquiryRowAsync"/> 的「审计时间戳刷新」
    /// UPDATE（仅用 EF Core 基础 API）。
    /// </summary>
    public const string InquiryRowLockSql =
        "SELECT Id FROM db_owner.Inquiries WITH (UPDLOCK, HOLDLOCK) WHERE Id = {0}";

    /// <summary>
    /// 目标报价单来源行锁语句（引用 ERP-400 同一把报价单来源行锁）：询价单 → 报价单只新增报价单行，
    /// 锁内只读复核既有来源报价单（<see cref="Quotation.InquiryId"/>），锁序恒定为「询价单行锁 → 报价单行锁」。
    /// </summary>
    public const string QuotationRowLockSql = QuotationMutationRules.QuotationRowLockSql;

    /// <summary>行锁重试次数（乐观并发令牌过期时重读权威行后有界重试；行锁语义 = 阻塞后成功）。</summary>
    public const int RowLockRetryAttempts = QuotationMutationRules.RowLockRetryAttempts;

    private const int LockRetryAttempts = RowLockRetryAttempts;

    /// <summary>并发方持续改写同一询价单行时对外给出的拒绝文案。</summary>
    public const string ConcurrentMutationText =
        "目标询价单正在被并发修改，本次操作未生效：请刷新后重试（原始证据均未改变）";

    /// <summary>持久化 <c>RowVersion</c> 与调用方持有的乐观令牌不一致（被并发改写）时的拒绝文案。</summary>
    public const string StaleRowVersionText =
        "该询价单已被并发操作改写（RowVersion 过期）：本次操作未生效，请刷新后重试（绝不覆盖赢家，原始证据均未改变）";

    /// <summary>已存在实时报价单下游时禁止重新打开 / 取消 / 删除 / 破坏性变更的拒绝文案。</summary>
    public const string DownstreamLinkedText =
        "该询价单已生成报价单并存在实时下游链接：禁止重新打开 / 取消 / 删除或破坏性变更"
        + "（保留显式历史证据，不做报价单的反向自动冲销）";

    /// <summary>重复转换（同一询价单只允许一张报价单）的拒绝文案（与既有转换守卫同源）。</summary>
    public const string DuplicateQuotationText = "该询价单已生成报价单，不能重复转换";

    /// <summary>未审核询价单的转换拒绝文案（与既有转换守卫同源）。</summary>
    public const string NotApprovedText = "仅已审核的询价单可转为报价单";

    /// <summary>无有效明细的转换拒绝文案（与既有转换守卫同源）。</summary>
    public const string NoActiveDetailsText = "询价单没有有效明细，不能生成报价单";

    /// <summary>锁序口径文案（接口 / 文档同源）。</summary>
    public const string LockOrderText =
        "ERP-402 询价单生命周期与转换的确定性锁协议：修改 / 提交 / 审核 / 取消 / 删除 / 批量删除 / 转报价单都先取"
        + "「询价单来源行锁」（db_owner.Inquiries WITH (UPDLOCK, HOLDLOCK)，等价实现为审计时间戳刷新的 UPDATE），"
        + "把同一询价单的并发写串行化在同一原子事务内；批量删除按询价单 Id 升序确定性加锁（去重、仅正整数）。"
        + "跨单据链恒定锁序为「询价单行锁 → 报价单行锁 → PI 行锁 / 销售订单行锁」，"
        + "转换在询价单行锁内重读来源与既有报价单后再 INSERT 全新目标，绝不反向获取下游锁，因此不存在锁环。";

    /// <summary>边界文案（不新增权限 / 表列，不改写来源与历史证据，不做财务 / 库存过账）。</summary>
    public const string BoundaryText =
        "本护栏只保护询价单生命周期变更 / 批量删除与询价单 → 报价单转换的并发完整性：不新增菜单 / 角色 / 用户授权或表结构，"
        + "也不把空身份当作管理员；不改写询价单明细 / 数量 / 单价 / 金额 / 币种 / 汇率 / 单位等原始商业语义（服务端口径不变），"
        + "不改写报价单 / PI / 销售订单、客户主数据、库存与库存流水、发票、费用或财务记录，不做任何财务 / 库存过账；"
        + "不删除历史证据，不用「取消报价单」的反向自动冲销伪造一致性；行锁只刷新询价单技术审计时间戳 UpdatedAt（非商业证据）。";

    // ==================== 1. 关系型判定 / 事务 / 来源行锁 ====================

    /// <summary>关系型后端（SQL Server）判定：内存库等非关系型提供程序没有行锁语义，锁定与事务等价无操作。</summary>
    public static bool IsRelationalProvider(IErpDbContext db)
        => QuotationMutationRules.IsRelationalProvider(db);

    /// <summary>
    /// 生命周期变更 / 批量删除 / 转换使用的原子事务（与 ERP-395 / ERP-399 / ERP-400 同源）：关系型后端开启真实事务，
    /// 失败整体回滚；调用方已开启事务时<strong>绝不嵌套</strong>（返回 <c>null</c> 表示复用外层事务）；
    /// 非关系型提供程序返回 <c>null</c>（等价无事务，声明式校验不变）。
    /// </summary>
    public static Task<IDbContextTransaction?> BeginMutationTransactionAsync(
        IErpDbContext db, CancellationToken ct = default)
        => QuotationMutationRules.BeginMutationTransactionAsync(db, ct);

    /// <summary>事务回滚 / 显式拒绝后丢弃变更跟踪器中的半成品变更，绝不残留部分写入（内存库同样生效）。</summary>
    public static void DiscardTrackedChanges(IErpDbContext db)
        => QuotationMutationRules.DiscardTrackedChanges(db);

    /// <summary>
    /// 合并需要加锁的询价单 Id：去重、只保留正整数，并按 Id 升序返回，保证多把行锁的确定性获取顺序
    /// （绝不反向获取，也就不存在锁环）。
    /// </summary>
    public static IReadOnlyList<long> MergeLockIds(IEnumerable<long>? ids)
        => (ids ?? Array.Empty<long>())
            .Where(id => id > 0)
            .Distinct()
            .OrderBy(id => id)
            .ToList();

    /// <summary>
    /// 对 <b>询价单来源行</b>加排它行锁：把「同单并发的生命周期变更 / 删除 / 转报价单」串行化在同一事务内。
    /// 返回 <c>false</c> 表示该行不存在 / 已被并发删除。
    /// <para>实现与 ERP-395 / ERP-399 / ERP-400 行锁同源（仅用 EF Core 基础 API）：在调用方事务内对询价单行发一条
    /// 「审计时间戳刷新」<c>UPDATE</c>（<c>UpdatedAt</c> 为技术审计字段、不是商业证据），取得排它行锁（X 锁，
    /// 持有至事务结束）；并发方先提交使本地乐观令牌 <c>RowVersion</c> 过期时重读权威行后**有界重试**。</para>
    /// <para>内存库等非关系型提供程序无行锁语义，直接返回 <c>true</c>（存在性仍由锁内权威重读判定）。</para>
    /// </summary>
    public static async Task<bool> LockInquiryRowAsync(IErpDbContext db, long inquiryId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (inquiryId <= 0) return false;
        if (!IsRelationalProvider(db)) return true;

        for (var attempt = 1; attempt <= LockRetryAttempts; attempt++)
        {
            var inquiry = await db.Inquiries
                .FirstOrDefaultAsync(o => o.Id == inquiryId && !o.IsDeleted, ct);
            if (inquiry is null) return false;

            inquiry.UpdatedAt = DateTime.Now;
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
                        return false; // 询价单行已被并发事务删除
                    }
                }

                if (attempt == LockRetryAttempts)
                    throw BusinessException.RuleConflict(ConcurrentMutationText);
            }
        }

        return false;
    }

    /// <summary>对一组询价单来源行按 <b>Id 升序</b>确定性加锁（批量删除等）。非关系型提供程序跳过。</summary>
    public static async Task LockInquiryRowsAsync(IErpDbContext db, IEnumerable<long>? inquiryIds,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (!IsRelationalProvider(db)) return;

        foreach (var id in MergeLockIds(inquiryIds))
            await LockInquiryRowAsync(db, id, ct);
    }

    // ==================== 2. 锁内权威事实 ====================

    /// <summary>有效（未删除）明细行数量：转换资格判定以锁内权威重读为准。</summary>
    public static int ActiveDetailCount(Inquiry? inquiry)
        => inquiry?.Details.Count(d => !d.IsDeleted) ?? 0;

    /// <summary>
    /// 读取询价单已生成的<b>实时（未删除）</b>报价单（只按持久化 <c>Quotation.InquiryId</c> 判定，
    /// 绝不按自由文本单号推断；用于重复转换守卫与下游冻结）。无下游时返回 <c>null</c>。
    /// </summary>
    public static Task<Quotation?> FindLiveQuotationAsync(IErpDbContext db, long inquiryId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (inquiryId <= 0) return Task.FromResult<Quotation?>(null);
        return db.Quotations.AsNoTracking()
            .FirstOrDefaultAsync(q => q.InquiryId == inquiryId && !q.IsDeleted, ct);
    }

    /// <summary>询价单是否已存在实时（未删除）报价单下游（只按持久化 <c>Quotation.InquiryId</c> 判定）。</summary>
    public static async Task<bool> HasLiveQuotationAsync(IErpDbContext db, long inquiryId,
        CancellationToken ct = default)
        => await FindLiveQuotationAsync(db, inquiryId, ct) is not null;

    /// <summary>
    /// 存在实时报价单下游的询价单一律冻结：禁止重新打开 / 取消 / 删除 / 修改 / 破坏性变更，保留显式历史；
    /// <b>无下游链接</b>时保留既有允许的全部流转（本护栏不新增任何状态机限制）。
    /// </summary>
    public static void EnsureNoLiveDownstream(bool hasLiveDownstream)
    {
        if (hasLiveDownstream)
            throw BusinessException.RuleConflict(DownstreamLinkedText);
    }

    // ==================== 3. 生命周期守卫（锁内重新加载后调用） ====================

    /// <summary>修改资格（锁内调用）：仅待提交可改（与既有口径同源，不新增状态机限制）。</summary>
    public static void EnsureEditAllowed(DocumentStatus stored)
    {
        if (stored != DocumentStatus.Pending)
            throw BusinessException.RuleConflict("仅待提交状态的单据可修改");
    }

    /// <summary>提交资格（锁内调用）：仅待提交可提交。</summary>
    public static void EnsureSubmitAllowed(DocumentStatus stored)
    {
        if (stored != DocumentStatus.Pending)
            throw BusinessException.RuleConflict("当前状态不允许该操作");
    }

    /// <summary>审核资格（锁内调用）：仅已提交可审核（保留既有状态机语义）。</summary>
    public static void EnsureApproveAllowed(DocumentStatus stored)
    {
        if (stored != DocumentStatus.Submitted)
            throw BusinessException.RuleConflict("当前状态不允许该操作");
    }

    /// <summary>取消资格（锁内调用）：保留既有语义（无状态限制），由下游链接守卫单独冻结已转换的来源。</summary>
    public static void EnsureCancelAllowed(DocumentStatus stored)
    {
        _ = stored; // 既有允许口径保持不变；仅由 EnsureNoLiveDownstream 冻结已转换的询价单。
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
    /// 「询价单 → 报价单」转换资格（锁内权威重读后调用，与既有 <c>BuildDraftAsync</c> 守卫同口径）：
    /// 已存在实时来源报价单（重复生成）拒绝 → 已完成（终态）拒绝 → 未审核拒绝 → 无有效明细拒绝。
    /// 任一不满足即原子拒绝，绝不消耗单号、绝不写入目标、绝不改写来源询价单。
    /// </summary>
    public static void EnsureQuotationConversionEligible(Inquiry inquiry, Quotation? existingQuotation,
        int activeDetailCount)
    {
        ArgumentNullException.ThrowIfNull(inquiry);

        if (existingQuotation is not null)
            throw BusinessException.RuleConflict(DuplicateQuotationText);
        if (inquiry.Status == DocumentStatus.Completed)
            throw BusinessException.RuleConflict(DuplicateQuotationText);
        if (inquiry.Status != DocumentStatus.Approved)
            throw BusinessException.RuleConflict(NotApprovedText);
        if (activeDetailCount <= 0)
            throw BusinessException.RuleConflict(NoActiveDetailsText);
    }
}
