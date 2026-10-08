using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace ERP.Application.Services;

/// <summary>
/// 报价单 Quotation（<c>api/sales/quotations</c>）生命周期变更、版本创建与「报价单 → PI / 销售订单」转换的
/// 共享<strong>确定性来源行锁 + 原子事务 + 锁内权威复核</strong>护栏（ERP-400）。
/// <list type="number">
/// <item><b>来源行锁</b>：修改 / 提交 / 审核 / 销审 / 取消 / 作废 / 删除 / 批量删除 / 创建版本 / 转 PI / 转销售订单，
/// 都在重复检测、单号生成、字段改写<b>之前</b>先对 <b>报价单来源行</b>取得排它行锁
/// （<see cref="QuotationRowLockSql"/>，持有至事务结束）；取得方式与 ERP-395 父单证行锁、ERP-399 PI 行锁同源
/// （仅用 EF Core 基础 API 的「审计时间戳刷新」<c>UPDATE</c>，语义等价 <c>SELECT ... WITH (UPDLOCK, HOLDLOCK)</c>）。
/// 批量删除按报价单 <b>Id 升序</b>确定性加锁（去重、仅正整数）。</item>
/// <item><b>锁序（与 PI 锁兼容）</b>：跨单据链恒定先取「报价单行锁」，再取「PI 行锁」/「销售订单行锁」；
/// 「报价单 → PI」写路径只新增 PI 行（不反向获取既有 PI 锁），因此不存在锁环。</item>
/// <item><b>锁内权威复核</b>：取得报价单行锁之后必须由调用方<b>重新加载</b>报价单（不是复用加锁前的内存实体），
/// 并复核持久化生命周期状态 / <c>RowVersion</c>、实时客户范围授权、有效明细行、历史版本只读
/// （<see cref="QuotationRevisionService.IsSupersededAsync"/>）以及既有转换 / 版本资格；任一不满足即原子拒绝。</item>
/// <item><b>目标单据</b>：转换只在<b>报价单行锁内</b>复核「是否已存在来源 PI（<c>QuotationId</c>）」与
/// 「是否已存在来源销售订单（<c>SourceQuotationId</c>）」，然后 <c>INSERT</c> 全新目标行 ——
/// 目标单据没有既有行可加锁，重复生成因此由来源行锁 + 锁内重读唯一化；
/// 已完成且存在下游链接的报价单一律禁止重新打开 / 破坏性变更，保留显式历史，<b>绝不</b>用「取消目标单据」
/// 的反向冲销伪造一致性。</item>
/// <item><b>原子回滚</b>：并发生命周期冲突、转换 / 版本资格不符、编号生成失败、数据库写入失败等任一步失败都
/// 整体回滚，状态 / 字段 / 已预约的目标单据编号与新建单据一起回滚，绝不留下半成品或撕裂的来源状态。</item>
/// </list>
/// <para><b>边界</b>：不新增表 / 列 / 索引 / 菜单 / 角色 / 用户授权，不把空身份当作匿名或管理员，也不伪造任何授权；
/// 不改写库存与库存流水、客户主数据、来源询价单、财务记录，也不做任何财务 / 库存过账；
/// 行锁只刷新报价单的技术审计时间戳 <c>UpdatedAt</c>（非商业证据，不参与任何金额 / 状态判定）。</para>
/// </summary>
public static class QuotationMutationRules
{
    /// <summary>
    /// 报价单来源行锁语句（契约 / 文档同源，跨单据锁序的<b>第一把锁</b>）：<c>UPDLOCK, HOLDLOCK</c> 语义等价
    /// 可串行化行锁，持有至调用方事务结束。实际执行走 <see cref="LockQuotationRowAsync"/> 的「审计时间戳刷新」
    /// UPDATE（仅用 EF Core 基础 API）；PI 侧（<see cref="ProformaInvoiceMutationRules.QuotationRowLockSql"/>）
    /// 复用同一常量，保证「报价单 → PI」与「报价单 → 销售订单」共用同一把报价单行锁。
    /// </summary>
    public const string QuotationRowLockSql =
        "SELECT Id FROM db_owner.Quotations WITH (UPDLOCK, HOLDLOCK) WHERE Id = {0}";

    /// <summary>
    /// 目标形式发票 PI 行锁语句（引用 ERP-399 同一把 PI 来源行锁）：报价单 → PI 只新增 PI 行、
    /// 锁内只读复核既有来源 PI（<c>QuotationId</c>），不反向获取既有 PI 行锁。
    /// </summary>
    public const string ProformaInvoiceRowLockSql = ProformaInvoiceMutationRules.PiRowLockSql;

    /// <summary>
    /// 目标销售订单行锁语句（与销售订单取消 / 出库审核 / 预装柜流程 / 单证生成共用同一把来源行锁）：
    /// 转换只在报价单行锁内**只读复核**既有来源订单，不反向获取下游锁；共享常量保证锁身份同源。
    /// </summary>
    public const string SalesOrderRowLockSql = PreLoadingSalesOrderLinkRules.LockSalesOrderRowSql;

    /// <summary>行锁重试次数（乐观并发令牌过期时重读权威行后有界重试；行锁语义 = 阻塞后成功）。</summary>
    public const int RowLockRetryAttempts = ProformaInvoiceMutationRules.RowLockRetryAttempts;

    private const int LockRetryAttempts = RowLockRetryAttempts;

    /// <summary>锁序口径文案（接口 / 文档同源）。</summary>
    public const string LockOrderText =
        "ERP-400 报价单生命周期的确定性锁协议：修改 / 提交 / 审核 / 销审 / 取消 / 作废 / 删除 / 批量删除 / 创建版本 / " +
        "转 PI / 转销售订单都先取「报价单来源行锁」（db_owner.Quotations WITH (UPDLOCK, HOLDLOCK)，等价实现为审计时间戳刷新的 UPDATE），" +
        "把同一报价单的并发写串行化在同一原子事务内；批量删除按报价单 Id 升序确定性加锁（去重、仅正整数）。" +
        "跨单据链恒定锁序为「报价单行锁 → PI 行锁 / 销售订单行锁」，转换在报价单行锁内只读复核既有来源单据后再 INSERT 全新目标，" +
        "绝不反向获取下游锁，因此不存在锁环。";

    /// <summary>边界文案（不新增权限 / 表列，不改写来源与历史证据，不做财务 / 库存过账）。</summary>
    public const string BoundaryText =
        "本护栏只保护报价单生命周期变更、版本创建与报价单 → PI / 销售订单转换的并发完整性：不新增菜单 / 角色 / 用户授权或表结构，" +
        "也不把空身份当作管理员；不改写数量 / 单价 / 金额 / 合计 / 币种 / 汇率 / 单位等原始商业语义（服务端口径不变），" +
        "不改写来源询价单、形式发票 PI、销售订单、客户主数据、库存与库存流水、发票、费用或财务记录，不做任何财务 / 库存过账；" +
        "不删除历史证据，不用「取消目标单据」的反向冲销伪造一致性；行锁只刷新报价单技术审计时间戳 UpdatedAt（非商业证据）。";

    /// <summary>并发方持续改写同一报价单行时对外给出的拒绝文案。</summary>
    public const string ConcurrentMutationText =
        "目标报价单正在被并发修改，本次操作未生效：请刷新后重试（原始证据均未改变）";

    /// <summary>持久化 <c>RowVersion</c> 与调用方持有的乐观令牌不一致（被并发改写）时的拒绝文案。</summary>
    public const string StaleRowVersionText =
        "该报价单已被并发操作改写（RowVersion 过期）：本次操作未生效，请刷新后重试（绝不覆盖赢家，原始证据均未改变）";

    /// <summary>已完成且存在下游链接时禁止重新打开 / 破坏性变更的拒绝文案。</summary>
    public const string DownstreamLinkedText =
        "该报价单已完成并存在下游 PI / 销售订单链接：禁止重新打开或破坏性变更（保留显式历史证据，不做取消目标单据的反向冲销）";

    /// <summary>历史版本只读的拒绝文案（与 ERP-035 既有口径同源）。</summary>
    public const string SupersededText = "该报价单已有后续版本，历史版本只读；请在最新版本上继续操作";

    /// <summary>转 PI：报价单未审核的拒绝文案（与既有转换守卫同源）。</summary>
    public const string NotApprovedToPiText = "报价单未审核，请先审核后再转 PI";

    /// <summary>转 PI：无有效明细的拒绝文案（与既有转换守卫同源）。</summary>
    public const string NoActiveDetailsToPiText = "报价单无商品明细，不能转 PI";

    /// <summary>转 PI：已作废的拒绝文案（与既有转换守卫同源）。</summary>
    public const string CancelledToPiText = "已作废的报价单不能转 PI";

    /// <summary>转销售订单：已作废的拒绝文案（与既有转换守卫同源）。</summary>
    public const string CancelledToOrderText = "已作废的报价单不能转销售订单";

    /// <summary>转销售订单：未审核的拒绝文案（与既有转换守卫同源）。</summary>
    public const string NotApprovedToOrderText = "报价单未审核，请先审核后再转销售订单";

    /// <summary>转销售订单：无有效明细的拒绝文案（与既有转换守卫同源）。</summary>
    public const string NoActiveDetailsToOrderText = "报价单无商品明细，不能转销售订单";

    // ==================== 1. 关系型判定 / 事务 / 来源行锁 ====================

    /// <summary>关系型后端（SQL Server）判定：内存库等非关系型提供程序没有行锁语义，锁定与事务等价无操作。</summary>
    public static bool IsRelationalProvider(IErpDbContext db)
        => TradeDocumentMutationRules.IsRelationalProvider(db);

    /// <summary>
    /// 生命周期变更 / 版本创建 / 转换使用的原子事务（复用 ERP-395 / ERP-399 既有口径）：关系型后端开启真实事务，
    /// 失败整体回滚；调用方已开启事务时<strong>绝不嵌套</strong>（返回 <c>null</c> 表示复用外层事务）；
    /// 非关系型提供程序返回 <c>null</c>（等价无事务，声明式校验不变）。
    /// </summary>
    public static Task<IDbContextTransaction?> BeginMutationTransactionAsync(
        IErpDbContext db, CancellationToken ct = default)
        => TradeDocumentMutationRules.BeginMutationTransactionAsync(db, ct);

    /// <summary>
    /// 合并需要加锁的报价单 Id：去重、只保留正整数，并按 Id 升序返回，保证多把行锁的确定性获取顺序
    /// （绝不反向获取，也就不存在锁环）。
    /// </summary>
    public static IReadOnlyList<long> MergeLockIds(IEnumerable<long>? ids)
        => (ids ?? Array.Empty<long>())
            .Where(id => id > 0)
            .Distinct()
            .OrderBy(id => id)
            .ToList();

    /// <summary>
    /// 对 **报价单来源行**加排它行锁：把「同单并发的生命周转变更 / 删除 / 版本创建 / 转换」串行化在同一事务内。
    /// 返回 <c>false</c> 表示该行不存在 / 已被并发删除。
    /// <para>实现与 ERP-395 父单证行锁、ERP-399 PI 行锁同源（仅用 EF Core 基础 API）：在调用方事务内对报价单行发一条
    /// 「审计时间戳刷新」<c>UPDATE</c>（<c>UpdatedAt</c> 为技术审计字段、不是商业证据），取得排它行锁（X 锁，
    /// 持有至事务结束）；并发方先提交使本地乐观令牌 <c>RowVersion</c> 过期时重读权威行后**有界重试**，
    /// 绝不把纯粹锁等待误报成业务拒绝。</para>
    /// <para>内存库等非关系型提供程序无行锁语义，直接返回 <c>true</c>（存在性仍由锁内权威重读判定）。</para>
    /// </summary>
    public static async Task<bool> LockQuotationRowAsync(IErpDbContext db, long quotationId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (quotationId <= 0) return false;
        if (!IsRelationalProvider(db)) return true;

        for (var attempt = 1; attempt <= LockRetryAttempts; attempt++)
        {
            var quotation = await db.Quotations
                .FirstOrDefaultAsync(o => o.Id == quotationId && !o.IsDeleted, ct);
            if (quotation is null) return false;

            quotation.UpdatedAt = DateTime.Now;
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
                        return false; // 报价单行已被并发事务删除
                    }
                }

                if (attempt == LockRetryAttempts)
                    throw BusinessException.RuleConflict(ConcurrentMutationText);
            }
        }

        return false;
    }

    /// <summary>对一组报价单来源行按 <b>Id 升序</b>确定性加锁（批量删除等）。非关系型提供程序跳过。</summary>
    public static async Task LockQuotationRowsAsync(IErpDbContext db, IEnumerable<long>? quotationIds,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (!IsRelationalProvider(db)) return;

        foreach (var id in MergeLockIds(quotationIds))
            await LockQuotationRowAsync(db, id, ct);
    }

    /// <summary>事务回滚 / 显式拒绝后丢弃变更跟踪器中的半成品变更，绝不残留部分写入（内存库同样生效）。</summary>
    public static void DiscardTrackedChanges(IErpDbContext db)
        => TradeDocumentMutationRules.DiscardTrackedChanges(db);

    // ==================== 2. 锁内权威事实 ====================

    /// <summary>有效（未删除）明细行数量：审核 / 转换的资格判定以锁内权威重读为准。</summary>
    public static int ActiveDetailCount(Quotation? quotation)
        => quotation?.Details.Count(d => !d.IsDeleted) ?? 0;

    /// <summary>是否已存在未删除的来源 PI（只按持久化 <c>QuotationId</c> 判定，绝不按自由文本推断）。</summary>
    public static Task<bool> HasProformaInvoiceLinkAsync(IErpDbContext db, long quotationId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (quotationId <= 0) return Task.FromResult(false);
        return db.ProformaInvoices.AsNoTracking()
            .AnyAsync(o => o.QuotationId == quotationId && !o.IsDeleted, ct);
    }

    /// <summary>读取来源报价单已生成的形式发票 PI（未删除；用于重复生成守卫与提示单号）。</summary>
    public static Task<ProformaInvoice?> FindDownstreamPiAsync(IErpDbContext db, long quotationId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (quotationId <= 0) return Task.FromResult<ProformaInvoice?>(null);
        return db.ProformaInvoices.AsNoTracking()
            .FirstOrDefaultAsync(o => o.QuotationId == quotationId && !o.IsDeleted, ct);
    }

    /// <summary>是否已存在未删除的下游销售订单链接（只按持久化 <c>SourceQuotationId</c> 判定）。</summary>
    public static Task<bool> HasSalesOrderLinkAsync(IErpDbContext db, long quotationId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (quotationId <= 0) return Task.FromResult(false);
        return db.SalesOrders.AsNoTracking()
            .AnyAsync(o => o.SourceQuotationId == quotationId && !o.IsDeleted, ct);
    }

    /// <summary>读取来源报价单已生成的销售订单（未删除；用于重复生成守卫与提示单号）。</summary>
    public static Task<SalesOrder?> FindDownstreamOrderAsync(IErpDbContext db, long quotationId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (quotationId <= 0) return Task.FromResult<SalesOrder?>(null);
        return db.SalesOrders.AsNoTracking()
            .FirstOrDefaultAsync(o => o.SourceQuotationId == quotationId && !o.IsDeleted, ct);
    }

    /// <summary>报价单是否已存在任何下游链接（已生成 PI 或销售订单，只按持久化外键判定）。</summary>
    public static async Task<bool> HasDownstreamLinkAsync(IErpDbContext db, long quotationId,
        CancellationToken ct = default)
        => await HasProformaInvoiceLinkAsync(db, quotationId, ct)
           || await HasSalesOrderLinkAsync(db, quotationId, ct);

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
    /// 已完成且存在下游链接的报价单一律冻结：禁止修改 / 提交 / 审核 / 销审 / 取消 / 作废 / 删除 / 批量删除，
    /// 保留显式历史。<b>无下游链接</b>时保留既有允许的流转（本护栏不新增任何状态机限制）。
    /// </summary>
    public static void EnsureNoDownstreamLink(bool hasDownstreamLink)
    {
        if (hasDownstreamLink)
            throw BusinessException.RuleConflict(DownstreamLinkedText);
    }

    /// <summary>历史版本只读守卫（锁内调用）：已被后续版本取代的报价单不允许再流转 / 修改。</summary>
    public static void EnsureNotSuperseded(bool superseded)
    {
        if (superseded)
            throw BusinessException.RuleConflict(SupersededText);
    }

    /// <summary>修改资格（锁内调用）：仅待提交 / 已提交可改，已审核 / 已完成 / 已作废一律拒绝。</summary>
    public static void EnsureEditAllowed(DocumentStatus stored)
    {
        if (stored is DocumentStatus.Approved or DocumentStatus.Cancelled or DocumentStatus.Completed)
            throw BusinessException.RuleConflict("已审核、已转订单或已作废的报价单不可修改，请先销审");
    }

    /// <summary>提交资格（锁内调用）：仅待提交可提交。</summary>
    public static void EnsureSubmitAllowed(DocumentStatus stored)
    {
        if (stored != DocumentStatus.Pending)
            throw BusinessException.RuleConflict("当前状态不允许该操作");
    }

    /// <summary>审核资格（锁内调用，含有效明细行复核）：草稿可直接审核，也支持提交后审核。</summary>
    public static void EnsureApproveAllowed(DocumentStatus stored, int activeDetailCount)
    {
        if (stored == DocumentStatus.Cancelled)
            throw BusinessException.RuleConflict("已作废的报价单不能审核");
        if (stored == DocumentStatus.Completed)
            throw BusinessException.RuleConflict("已转订单的报价单不能审核");
        if (stored == DocumentStatus.Approved)
            throw BusinessException.RuleConflict("报价单已审核");
        if (activeDetailCount <= 0)
            throw BusinessException.RuleConflict("报价单无商品明细，不能审核");
    }

    /// <summary>销审（重新打开）资格（锁内调用）：仅已审核可销审。</summary>
    public static void EnsureUnauditAllowed(DocumentStatus stored)
    {
        if (stored != DocumentStatus.Approved)
            throw BusinessException.RuleConflict("仅已审核的报价单可销审");
    }

    /// <summary>取消资格（锁内调用）：保留既有语义（无状态限制），由下游链接守卫单独冻结。</summary>
    public static void EnsureCancelAllowed(DocumentStatus stored)
    {
        _ = stored; // 既有允许口径保持不变；仅由 EnsureNoDownstreamLink 冻结已完成且已链接的报价单。
    }

    /// <summary>作废资格（锁内调用）：已作废 / 已完成拒绝。</summary>
    public static void EnsureVoidAllowed(DocumentStatus stored)
    {
        if (stored == DocumentStatus.Cancelled)
            throw BusinessException.RuleConflict("报价单已作废");
        if (stored == DocumentStatus.Completed)
            throw BusinessException.RuleConflict("已转订单的报价单不能作废");
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

    // ==================== 4. 转换 / 版本资格（锁内重新加载后调用） ====================

    /// <summary>
    /// 「报价单 → 形式发票 PI」转换资格（锁内权威重读后调用，与既有 <c>ToProformaInvoice</c> 守卫同口径）：
    /// 已作废拒绝 → 已完成拒绝 → 未审核拒绝 → 无有效明细拒绝 → 已存在来源 PI（重复生成）拒绝。
    /// 任一不满足即原子拒绝，绝不消耗单号、绝不写入目标、绝不改写来源报价单。
    /// </summary>
    public static void EnsureProformaInvoiceConversionEligible(Quotation quotation,
        ProformaInvoice? existingPi, int activeDetailCount)
    {
        ArgumentNullException.ThrowIfNull(quotation);

        if (quotation.Status == DocumentStatus.Cancelled)
            throw BusinessException.RuleConflict(CancelledToPiText);
        if (quotation.Status == DocumentStatus.Completed)
            throw BusinessException.RuleConflict("该报价单已完成转换（已转 PI 或已转销售订单），不能重复转换");
        if (quotation.Status != DocumentStatus.Approved)
            throw BusinessException.RuleConflict(NotApprovedToPiText);
        if (activeDetailCount <= 0)
            throw BusinessException.RuleConflict(NoActiveDetailsToPiText);
        if (existingPi is not null)
            throw BusinessException.RuleConflict($"该报价单已转为 PI：{existingPi.PiNo}");
    }

    /// <summary>
    /// 「报价单 → 销售订单」转换资格（锁内权威重读后调用，与 <c>SalesOrderConversion.FromQuotationAsync</c>
    /// 同口径）：已作废拒绝 → 已存在来源订单（重复生成）拒绝 → 已转 PI 拒绝 → 未审核拒绝 → 无有效明细拒绝。
    /// 任一不满足即原子拒绝，绝不消耗单号、绝不写入订单、绝不改写来源报价单。
    /// </summary>
    public static void EnsureSalesOrderConversionEligible(Quotation quotation, SalesOrder? existingOrder,
        ProformaInvoice? existingPi, int activeDetailCount)
    {
        ArgumentNullException.ThrowIfNull(quotation);

        if (quotation.Status == DocumentStatus.Cancelled)
            throw BusinessException.RuleConflict(CancelledToOrderText);
        if (existingOrder is not null)
            throw BusinessException.RuleConflict($"该报价单已生成销售订单：{existingOrder.OrderNo}，不能重复生成");
        if (existingPi is not null)
            throw BusinessException.RuleConflict($"该报价单已转为 PI（{existingPi.PiNo}），请从 PI 转销售订单");
        if (quotation.Status != DocumentStatus.Approved)
            throw BusinessException.RuleConflict(NotApprovedToOrderText);
        if (activeDetailCount <= 0)
            throw BusinessException.RuleConflict(NoActiveDetailsToOrderText);
    }

    /// <summary>
    /// 普通销售订单表单保存**显式链接来源报价单**时的既有转换资格（ERP-401）：与
    /// <see cref="EnsureSalesOrderConversionEligible"/>（「报价单 → 销售订单」直接转换）**完全同口径**且复用同一实现 ——
    /// 已作废 / 未审核 / 无有效明细 / 已完成 / 已转 PI（<paramref name="existingPi"/> 非空）一律拒绝；
    /// 唯一目标由 <c>SalesOrderConversion.FindOtherTargetAsync</c> 在来源行锁内独立复核。
    /// 本方法不写库、不改写来源报价单、不消耗单据号。
    /// </summary>
    public static void EnsureManualOrderLinkEligible(Quotation quotation, ProformaInvoice? existingPi,
        int activeDetailCount)
        => EnsureSalesOrderConversionEligible(quotation, null, existingPi, activeDetailCount);

    /// <summary>
    /// 普通表单保存链接来源报价单的锁序口径（接口 / 文档同源，ERP-401）：与「报价单 → 销售订单」直接转换共用同一把
    /// 报价单来源行锁（跨单据链的第一把锁），人工保存与直接转换因此串行化在同一事务内，同一报价单至多一张目标销售订单。
    /// </summary>
    public const string ManualLinkLockOrderText =
        "ERP-401：普通销售订单表单显式链接来源报价单时先取「报价单来源行锁」"
        + "（db_owner.Quotations WITH (UPDLOCK, HOLDLOCK)，等价实现为审计时间戳刷新的 UPDATE），"
        + "再按需要取 PI / 销售订单行锁（恒定锁序「报价单 → PI / 销售订单」），与「报价单 → 销售订单」直接转换"
        + "共用同一把来源行锁：并发人工保存与直接转换串行化在同一原子事务内，同一报价单至多一张完整目标订单。";

    /// <summary>
    /// 来源询价单行锁语句（引用 ERP-402 同一把询价单来源行锁，跨单据链的<b>第一把锁</b>）：
    /// 询价单生命周期变更 / 批量删除 / 转报价单与报价单自身生命周期共用同一锁序，绝不反向获取上游锁。
    /// </summary>
    public const string InquiryRowLockSql = InquiryMutationRules.InquiryRowLockSql;

    /// <summary>
    /// 「询价单 → 报价单 → PI / 销售订单」的确定锁序口径（接口 / 文档同源，ERP-402）：
    /// 恒定先取「询价单来源行锁」，再取「报价单来源行锁」（与本类生命周期 / 版本 / 转换同一把），
    /// 之后才是 PI / 销售订单行锁；写路径绝不反向获取上游锁，因此不存在锁环。
    /// </summary>
    public const string InquiryToQuotationLockOrderText =
        "ERP-402：询价单生命周期变更 / 批量删除 / 转报价单恒定先取「询价单来源行锁」"
        + "（db_owner.Inquiries WITH (UPDLOCK, HOLDLOCK)，等价实现为审计时间戳刷新的 UPDATE），"
        + "再取「报价单来源行锁」（db_owner.Quotations，与 ERP-400 生命周期 / 版本 / 转换同一把），"
        + "跨单据链恒定锁序为「询价单行锁 → 报价单行锁 → PI 行锁 / 销售订单行锁」，"
        + "同一询价单的并发转换与报价单生命周期变更因此串行化在同一原子事务内，绝不反向获取上游锁。";
}


