using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace ERP.Application.Services;

/// <summary>
/// 采购订单（<c>api/purchase-orders</c>）**普通写入**（创建 / 修改草稿）的确定性锁协议、
/// 合法状态约束与「锁内权威重读」口径（ERP-425）。
/// <para>背景：ERP-371 已把提交 / 审核 / 删除 / 取消收敛到「归属销售订单 → 采购订单」行锁与可串行化事务，
/// ERP-346 / -347 已把**显式链接**来源的创建 / 修改纳入来源销售订单行锁，但**普通无归属（备货）创建 / 修改**，
/// 以及「已链接单据但请求未携带来源」的修改，此前仍在「加锁 / 事务之外」直接读写，
/// 并仅凭请求 <c>OwningSalesOrderId == null</c> 就选择安全路径 —— 与提交 / 审核 / 删除 / 取消并发时可能读到过期状态、
/// 静默清除来源血缘，或在明细替换失败后残留半成品写入。本类把这些口径统一抽到 Application 层。</para>
/// <list type="number">
/// <item><b>统一事务协议</b>：复用既有 <see cref="TradeDocumentMutationRules.BeginMutationTransactionAsync"/>
/// 口径（关系型后端开启真实事务；调用方已开启事务时<b>绝不嵌套</b>，返回 <c>null</c> 复用外层事务；
/// 非关系型提供程序等价无事务）。</item>
/// <item><b>确定性锁序</b>：无论手工（无归属）/ 已链接，都按「全部必要<b>来源销售订单行</b>
/// （<c>db_owner.SalesOrders WITH (UPDLOCK, HOLDLOCK)</c>，去重 + 仅正整数 + <b>Id 升序</b>）→
/// <b>本采购订单行</b>（<c>db_owner.PurchaseOrders WITH (UPDLOCK, HOLDLOCK)</c>）」取得排它行锁；
/// 与 ERP-371 状态流转、ERP-346 链接、销售订单取消共用同一把锁，<b>绝不反向获取下游锁</b>（不存在锁环）。</item>
/// <item><b>不凭请求空归属选择安全</b>：并发发现（<see cref="ReadPersistedSalesOrderPointerAsync"/>）以无锁只读读取
/// <b>持久化</b>归属来源，并与请求<b>拟议</b>来源取并集后才加锁；锁内重读持久化表头 / 明细 / 状态 / 来源与实时权限，
/// 发现来源指针被并发改写时原子拒绝（<see cref="SourceChangedUnderLockText"/>）。</item>
/// </list>
/// <para><b>边界</b>：本类只做锁定、事务、只读复核与纯判定；不新增表 / 列 / 索引 / 菜单 / 角色 / 用户授权，
/// 不伪造任何授权、不把空身份当作匿名或管理员，不改写供应商 / 币种 / 单位 / 单价等原始商业语义，
/// 不做库存 / 财务过账，也不删除任何历史证据与审计留痕。</para>
/// </summary>
public static class PurchaseOrderMutationRules
{
    /// <summary>
    /// 采购订单目标行锁语句（与 ERP-371 提交 / 审核 / 删除 / 取消共用同一把锁，锁身份同源）。
    /// </summary>
    public const string PurchaseOrderRowLockSql =
        "SELECT Id FROM db_owner.PurchaseOrders WITH (UPDLOCK, HOLDLOCK) WHERE Id = {0}";

    /// <summary>
    /// 归属销售订单行锁语句（与销售订单取消 / 出库审核 / 预装柜 / 采购归属链接共用同一把上游锁）。
    /// </summary>
    public const string SalesOrderRowLockSql = PreLoadingSalesOrderLinkRules.LockSalesOrderRowSql;

    /// <summary>确定性锁序文案（接口 / 文档同源）。</summary>
    public const string LockOrderText =
        "ERP-425 采购订单普通写入（创建 / 修改）的确定性锁协议：无论手工（无归属）还是已链接，都恒定按" +
        "「来源销售订单行（db_owner.SalesOrders WITH (UPDLOCK, HOLDLOCK)，去重 + 仅正整数 + Id 升序）→ " +
        "本采购订单行（db_owner.PurchaseOrders WITH (UPDLOCK, HOLDLOCK)）」取得排它行锁，" +
        "与 ERP-371 提交 / 审核 / 删除 / 取消、ERP-346 链接、销售订单取消共用同一把锁，" +
        "绝不反向获取下游锁（不存在锁环）；并发发现以无锁只读读取持久化归属来源，绝不凭请求空归属选择安全路径。";

    /// <summary>口径说明（接口 / 文档同源）。</summary>
    public const string RuleText =
        "采购订单普通写入（创建 / 修改）统一进入「DbContext 原子事务 + 确定性行锁（来源销售订单行 → 采购订单行）" +
        "+ 锁内权威重读」协议：创建 / 修改无论手工还是已链接都在同一原子事务内完成，表头 / 明细替换、" +
        "单据号预约与归属赋值构成一次原子保存，任一失败整体回滚并清除变更跟踪器中的半成品变更；" +
        "并发发现（无锁只读）同时读取持久化归属来源与请求拟议来源，取并集后按 Id 升序加锁，" +
        "锁内重读持久化表头 / 明细 / 状态 / 来源与实时权限，来源指针被并发改写时原子拒绝；" +
        "仅待提交状态的单据可修改，已提交 / 已审核 / 已删除 / 已取消的记录在并发流转后一律拒绝、绝不复活；" +
        "请求未给出归属来源时保留已存来源（绝不静默清除血缘），显式改绑仍按权威来源重新解析并校验实时已审核来源。";

    /// <summary>边界文案（不新增权限 / 表列，不改写商业语义与下游库存 / 财务）。</summary>
    public const string BoundaryText =
        "本协议只保护采购订单普通草稿写入：不新增表 / 列 / 索引 / 菜单 / 角色 / 用户授权，也不把空身份当作匿名或管理员；" +
        "不改写供应商 / 币种 / 汇率 / 单位 / 单价 / 数量 / 金额等原始商业语义，不改写归属销售订单、客户、商品主数据，" +
        "不做库存 / 库存流水 / 发票 / 付款 / 财务过账，也不删除任何历史单据与审计证据。";

    /// <summary>
    /// 并发方在本次「加锁前发现」之后改写了持久化来源指针时的受控拒绝文案：
    /// 本次并未对该新来源行加锁，因此原子拒绝，绝不改写到未加锁的来源行。
    /// </summary>
    public const string SourceChangedUnderLockText =
        "采购订单的归属来源指针已被并发操作改写：本次未对新的来源行加锁，已原子拒绝（请刷新后重试，原始证据均未改变）";

    /// <summary>非「待提交」状态修改的拒绝文案（与既有控制器口径逐字一致）。</summary>
    public const string NotPendingEditText = "仅待提交状态的单据可修改";

    /// <summary>
    /// 原子事务协议：复用既有 <see cref="TradeDocumentMutationRules.BeginMutationTransactionAsync"/> 口径 ——
    /// 关系型后端开启真实事务，调用方已开启事务时<b>绝不嵌套</b>（返回 <c>null</c> 复用外层事务），
    /// 非关系型提供程序返回 <c>null</c>（等价无事务，声明式校验不变）。
    /// </summary>
    public static Task<IDbContextTransaction?> BeginMutationTransactionAsync(
        IErpDbContext db, CancellationToken ct = default)
        => TradeDocumentMutationRules.BeginMutationTransactionAsync(db, ct);

    /// <summary>事务回滚 / 显式拒绝后丢弃变更跟踪器中的半成品变更，绝不残留部分写入（内存库同样生效）。</summary>
    public static void DiscardTrackedChanges(IErpDbContext db)
        => TradeDocumentMutationRules.DiscardTrackedChanges(db);

    /// <summary>来源 Id 归一化：仅正整数视为有效来源；<c>null</c> / 0 / 负数一律视为未给出。</summary>
    public static long? NormalizeId(long? id) => id is > 0 ? id : null;

    /// <summary>
    /// 合并需要加锁的来源销售订单 Id：去重、只保留正整数，并按 <b>Id 升序</b>返回，
    /// 保证多把来源行锁的确定性获取顺序（绝不反向获取，也就不存在锁环）。
    /// 传入持久化归属与请求拟议归属的并集，绝不仅凭请求 null 决定是否加锁。
    /// </summary>
    public static IReadOnlyList<long> MergeSalesOrderLockIds(params long?[] ids)
        => (ids ?? Array.Empty<long?>())
            .Select(NormalizeId)
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .Distinct()
            .OrderBy(id => id)
            .ToList();

    /// <summary>
    /// <b>加锁前发现</b>：以只读查询读取采购订单的持久化归属销售订单指针，仅用于确定锁序，
    /// 绝不凭请求空归属选择安全路径；锁内必须以受跟踪重读的权威值再次核对
    /// （<see cref="PersistedSourceUnchanged"/>）。
    /// <para>本方法只做 EF Core 基础 API 的有界只读查询，因此必须在<b>读已提交</b>事务内调用
    /// （<see cref="BeginMutationTransactionAsync"/> 的默认隔离级别），此时普通 SELECT 不保留共享锁，
    /// 绝不在取得上游来源锁之前持有采购订单共享锁；<b>可串行化</b>状态流转路径请使用控制器的
    /// <c>READUNCOMMITTED</c> 无锁发现（该路径会保留共享锁，必须显式避免）。</para>
    /// </summary>
    public static async Task<long?> ReadPersistedSalesOrderPointerAsync(
        IErpDbContext db, long orderId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (orderId <= 0) return null;

        return NormalizeId(await db.PurchaseOrders.AsNoTracking()
            .Where(o => o.Id == orderId && !o.IsDeleted)
            .Select(o => (long?)o.OwningSalesOrderId)
            .FirstOrDefaultAsync(ct));
    }

    /// <summary>
    /// 合法状态约束：仅<b>待提交</b>状态的草稿可修改；已提交 / 已审核 / 已驳回 / 已完成 / 已取消一律拒绝
    /// （并发提交 / 审核 / 删除 / 取消之后过期的编辑绝不复活任何终止态）。
    /// </summary>
    public static void EnsureEditable(DocumentStatus status)
    {
        if (status != DocumentStatus.Pending)
            throw BusinessException.RuleConflict(NotPendingEditText);
    }

    /// <summary>
    /// ERP-426 **规范写入条款校验**（新增 / 修改 / 提交 / 审核共用入口）：把唯一权威金额与精度校验与本类
    /// **确定性锁协议 / 原子事务**串成一条不可绕过的服务端校验 —— 调用方必须在「单号预约与字段改写之前」
    /// （新增 / 修改）或「既有采购订单行锁内、提交 / 审核之前」（提交 / 审核）调用本方法。
    /// <para>实现只委托 <see cref="PurchaseOrderAmountRules.ValidateNewWrite"/>（系统内唯一权威口径），
    /// 因此不存在第二套数量 / 单价 / 币种 / 汇率 / 税率 / 金额精度规则；本方法不写库、不改写任何字段。</para>
    /// </summary>
    public static void EnsureValidatedTerms(PurchaseOrder order)
        => PurchaseOrderAmountRules.ValidateNewWrite(order);

    /// <summary>ERP-426 规范写入条款校验口径（接口 / 文档同源）。</summary>
    public const string ValidatedTermsText =
        "ERP-426：规范采购订单的新增 / 修改在单号预约与任何字段改写之前、提交 / 审核在既有采购订单行锁内提交之前，"
        + "一律复用 PurchaseOrderAmountRules.ValidateNewWrite（唯一权威口径）校验「有效明细非空、数量为正且可表示、"
        + "单价非负且可表示、币种为已定义枚举值、汇率为正且可表示、税率 0~100、逐行金额与总额"
        + "在实际 EF 精度（DECIMAL(18,2)）内 checked 可表示」；任一项不满足即返回受控业务错误"
        + "（ErrorCodes.InvalidParameter），绝不静默取整、绝不把正数量舍入为 0，也绝不落库与明细合计不一致的金额。"
        + "客户端提交的金额 / 合计 / 状态 / 审计字段一律由服务端重置或按明细重算覆盖，"
        + "历史读取 / 打印不调用本校验、不被自动修正。";

    /// <summary>
    /// 「加锁前发现」的持久化来源指针是否与锁内权威重读一致：任一不等即表示来源已被并发改写，
    /// 本次并未对其加锁，必须原子拒绝。
    /// </summary>
    public static bool PersistedSourceUnchanged(long? discoveredSalesOrderId, long? lockedSalesOrderId)
        => NormalizeId(discoveredSalesOrderId) == NormalizeId(lockedSalesOrderId);
}
