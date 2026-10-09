using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Domain.Enums;
using Microsoft.EntityFrameworkCore.Storage;

namespace ERP.Application.Services;

/// <summary>
/// 规范销售订单（<c>api/sales-orders</c>）**普通写入**（修改 / 删除 / 提交 / 审核）的确定性锁协议、
/// 合法状态流转与「锁内权威重读」口径（ERP-421）。
/// <para>背景：ERP-347 / ERP-369 取消、ERP-397 单证生成与 ERP-401「显式链接来源」的保存此前已经使用同一把
/// 销售订单行锁与原子事务，但<b>普通表单保存</b>的<b>无来源手工订单</b>与<b>无法解析的显式历史来源</b>路径、
/// 以及继承自 <c>DocumentControllerBase</c> 的软删除入口，都在「加锁 / 事务之外」直接读写 ——
/// 与提交 / 审核 / 取消 / 单证生成并发时可能读到过期状态并放行，或在明细替换失败后残留半成品写入。
/// 本类把这些口径统一抽到 Application 层：<b>无论手工 / 历史 / 已解析来源</b>，修改 / 删除 / 提交 / 审核
/// 都进入同一套「DbContext 原子事务 + 确定性行锁 + 锁内权威重读」协议。</para>
/// <list type="number">
/// <item><b>确定性锁序</b>：已解析实时来源时恒定按「报价单来源行锁（<c>db_owner.Quotations</c>）→
/// PI 来源行锁（<c>db_owner.ProformaInvoices</c>）→ 销售订单目标行锁（<c>db_owner.SalesOrders</c>）」取得排它行锁，
/// 与 ERP-399 / ERP-400 转换、ERP-347 / ERP-369 取消、ERP-397 单证生成共用同一把锁，绝不反向获取下游锁；
/// 手工订单与无法解析的历史来源不取任何来源行锁，只取目标订单行锁（不存在的来源行绝不加锁）。</item>
/// <item><b>绝不嵌套事务</b>：复用既有 <see cref="SalesOrderSourceLineageRules.BeginWriteTransactionAsync"/>，
/// 调用方已开启事务时返回 <c>null</c>（复用外层事务），非关系型提供程序等价无事务。</item>
/// <item><b>锁内权威重读与过期拒绝</b>：加锁后重新读取持久化表头 / 明细 / 状态 / 来源与实时权限后才决定是否放行；
/// 若「加锁前读到的来源」已被并发方改写，则原子拒绝（<see cref="SourceChangedUnderLockText"/>）——
/// 绝不把来源改写到本次并未加锁的来源行。</item>
/// <item><b>合法状态流转</b>：待提交可编辑 / 删除、待提交→已提交、已提交→已审核；
/// 已删除 / 已取消的记录不可复活（<see cref="EnsureEditable"/> / <see cref="EnsureDeletable"/> /
/// <see cref="EnsureTransitionAllowed"/>）。</item>
/// </list>
/// <para><b>边界</b>：不新增表 / 列 / 索引 / 菜单 / 角色 / 用户授权，不把空身份当作匿名或管理员，
/// 不伪造任何授权，不改写数量 / 单价 / 金额 / 币种 / 单位等原始商业语义（<c>SalesOrderAmountRules</c> 唯一权威），
/// 不做财务 / 库存过账，也不删除任何历史证据与审计留痕；行锁只刷新来源行的技术审计时间戳 <c>UpdatedAt</c>。</para>
/// </summary>
public static class SalesOrderMutationRules
{
    /// <summary>确定性锁序文案（接口 / 文档同源）。</summary>
    public const string LockOrderText =
        "ERP-421 规范销售订单写入（修改 / 删除 / 提交 / 审核）的确定性锁协议：无论手工订单、无法解析的历史来源"
        + "还是已解析实时来源，都恒定按「报价单来源行锁（db_owner.Quotations）→ PI 来源行锁（db_owner.ProformaInvoices）"
        + "→ 销售订单目标行锁（db_owner.SalesOrders）」取得排它行锁（WITH (UPDLOCK, HOLDLOCK)，等价实现为审计时间戳刷新的 UPDATE），"
        + "并在同一原子事务内于加锁后**重新读取**持久化表头 / 明细 / 状态 / 来源与实时权限，再复核既有转换资格与唯一目标；"
        + "因此与 ERP-399 / ERP-400 转换、ERP-347 / ERP-369 取消、ERP-397 单证生成串行化在同一把锁上，"
        + "绝不反向获取下游锁（不存在锁环）；手工订单与无法解析的历史来源不取任何来源行锁，只取目标订单行锁。";

    /// <summary>并发方「加锁前读到的来源」已提交改变时的拒绝文案（绝不改写到本次并未加锁的来源行）。</summary>
    public const string SourceChangedUnderLockText =
        "销售订单的来源绑定已被并发操作改写：本次未对新的来源行加锁，已原子拒绝（请刷新后重试，原始证据均未改变）";

    /// <summary>非「待提交」状态可修改时的拒绝文案（与既有口径一致）。</summary>
    public const string NotPendingEditText = "仅待提交状态的单据可修改";

    /// <summary>非「待提交」状态可删除时的拒绝文案（与既有一致）。</summary>
    public const string NotPendingDeleteText = "仅待提交状态的单据可删除";

    /// <summary>非法状态流转的拒绝文案（与既有口径一致）。</summary>
    public const string IllegalTransitionText = "当前状态不允许该操作";

    /// <summary>口径说明（接口 / 文档同源）。</summary>
    public const string RuleText =
        "销售订单普通写入（修改 / 删除 / 提交 / 审核）统一进入「DbContext 原子事务 + 确定性行锁（报价单 → PI → 销售订单）"
        + "+ 锁内权威重读」协议：加锁后重新读取持久化表头 / 明细 / 状态 / 来源与实时权限，"
        + "合法流转保持待提交可编辑 / 删除、待提交→已提交、已提交→已审核；"
        + "并发提交 / 审核 / 取消后过期的编辑 / 删除一律拒绝，已删除 / 已取消记录不可复活；"
        + "来源绑定一经过期即原子拒绝，绝不静默清除或改写历史来源；任一失败整体回滚并清除变更跟踪器中的半成品变更。";

    /// <summary>边界文案。</summary>
    public const string BoundaryText =
        "本协议只保护规范销售订单写入入口：不新增表 / 列 / 索引 / 菜单 / 角色 / 用户授权，不把空身份当作匿名或管理员，"
        + "不改写数量 / 单价 / 金额 / 合计 / 定金 / 币种 / 汇率 / 单位等原始商业语义，"
        + "不改写来源报价单 / PI / 客户主数据 / 库存 / 财务记录，不删除任何历史证据与审计留痕；"
        + "行锁只刷新技术审计时间戳 UpdatedAt（非商业证据）；既有取消 / 单证生成 / 转换护栏保持不变。";

    /// <summary>关系型后端（SQL Server）判定：内存库等非关系型提供程序没有行锁语义，锁定与事务等价无操作。</summary>
    public static bool IsRelationalProvider(IErpDbContext db)
        => SalesOrderSourceLineageRules.IsRelationalProvider(db);

    /// <summary>
    /// 普通写入使用的原子事务（复用 <see cref="SalesOrderSourceLineageRules.BeginWriteTransactionAsync"/>）：
    /// 关系型后端开启真实事务，失败整体回滚；调用方已开启事务时<b>绝不嵌套</b>（返回 <c>null</c> 表示复用外层事务）；
    /// 非关系型提供程序返回 <c>null</c>（等价无事务，声明式校验不变）。
    /// </summary>
    public static Task<IDbContextTransaction?> BeginMutationTransactionAsync(IErpDbContext db,
        CancellationToken ct = default)
        => SalesOrderSourceLineageRules.BeginWriteTransactionAsync(db, ct);

    /// <summary>是否为合法状态流转：待提交→已提交、已提交→已审核（其余一律非法）。</summary>
    public static bool IsLegalTransition(DocumentStatus from, DocumentStatus to)
        => (from, to) switch
        {
            (DocumentStatus.Pending, DocumentStatus.Submitted) => true,
            (DocumentStatus.Submitted, DocumentStatus.Approved) => true,
            _ => false
        };

    /// <summary>仅「待提交」状态可编辑（修改）：已提交 / 已审核 / 已取消 / 已驳回一律拒绝（已删除由查询过滤拒绝）。</summary>
    public static void EnsureEditable(DocumentStatus status)
    {
        if (status != DocumentStatus.Pending)
            throw BusinessException.RuleConflict(NotPendingEditText);
    }

    /// <summary>仅「待提交」状态可删除：已提交 / 已审核 / 已取消 / 已驳回一律拒绝（已删除由查询过滤拒绝）。</summary>
    public static void EnsureDeletable(DocumentStatus status)
    {
        if (status != DocumentStatus.Pending)
            throw BusinessException.RuleConflict(NotPendingDeleteText);
    }

    /// <summary>
    /// 状态流转授权：当前状态必须等于期望前置状态，且「前置 → 目标」属于合法流转；
    /// 由调用方在**锁内权威重读**到的当前状态上复核，绝不按加锁前的陈旧状态放行。
    /// </summary>
    public static void EnsureTransitionAllowed(DocumentStatus current, DocumentStatus from, DocumentStatus to)
    {
        if (current != from || !IsLegalTransition(from, to))
            throw BusinessException.RuleConflict(IllegalTransitionText);
    }

    /// <summary>
    /// 从**持久化来源 Id** 解析本次写入需要加锁的「实时来源行」（只读、有界）：
    /// 按既有权威解析（<see cref="SalesOrderSourceLineageRules.ResolveAsync"/>）区分已解析实时来源与无法解析的历史值；
    /// 只有**确实解析到行**的来源才返回其 Id（PI 的报价单祖先行不存在时只锁 PI，绝不对不存在的行加锁）；
    /// 完全未链接 / 无法解析的历史值返回 <see cref="SalesOrderMutationLockScope.None"/>（只取目标订单行锁）。
    /// 来源缺失 / 已删除 / 异客户等严格错误按既有口径原子抛出（绝不静默忽略）。
    /// </summary>
    public static async Task<SalesOrderMutationLockScope> ResolveLiveSourceLockScopeAsync(IErpDbContext db,
        long orderCustomerId, long? persistedQuotationId, long? persistedPiId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);

        var quotationId = SalesOrderSourceLineageRules.NormalizeId(persistedQuotationId);
        var piId = SalesOrderSourceLineageRules.NormalizeId(persistedPiId);
        if (quotationId is null && piId is null) return SalesOrderMutationLockScope.None;

        var change = SalesOrderSourceLineageRules.ResolveChange(quotationId, piId, quotationId, piId);
        var lineage = await SalesOrderSourceLineageRules.ResolveAsync(db, change, orderCustomerId, ct);
        return DescribeLiveScope(lineage);
    }

    /// <summary>
    /// 与 <see cref="ResolveLiveSourceLockScopeAsync"/> 同一口径，但把「来源缺失 / 已删除 / 异客户」等业务拒绝
    /// 视为**无可加锁的实时来源**（返回 <see cref="SalesOrderMutationLockScope.None"/>）。
    /// 仅用于「来源合法性由后续锁内复核（<see cref="SalesOrderSourceLineageRules.EnsurePersistedSourceIntactAsync"/>）
    /// 或本操作不依赖来源合法性（如软删除）」的写入动作：绝不吞掉拒绝，只是把是否放行的判定留给锁内复核。
    /// </summary>
    public static async Task<SalesOrderMutationLockScope> TryResolveLiveSourceLockScopeAsync(IErpDbContext db,
        long orderCustomerId, long? persistedQuotationId, long? persistedPiId, CancellationToken ct = default)
    {
        try
        {
            return await ResolveLiveSourceLockScopeAsync(db, orderCustomerId, persistedQuotationId, persistedPiId, ct);
        }
        catch (BusinessException)
        {
            return SalesOrderMutationLockScope.None;
        }
    }

    /// <summary>
    /// 由权威解析结果抽取「确实解析到行」的实时来源 Id（PI 优先；报价单祖先行不存在时不返回报价单 Id）。
    /// </summary>
    public static SalesOrderMutationLockScope DescribeLiveScope(SalesOrderSourceLineage lineage)
    {
        ArgumentNullException.ThrowIfNull(lineage);
        if (lineage.IsUnresolvedLegacy || lineage.IsUnlinked) return SalesOrderMutationLockScope.None;

        return new SalesOrderMutationLockScope
        {
            QuotationId = lineage.Quotation?.Id,
            PiId = lineage.ProformaInvoice?.Id,
            IsLiveSource = lineage.Quotation is not null || lineage.ProformaInvoice is not null
        };
    }

    /// <summary>
    /// 本次加锁的来源范围是否覆盖（重新解析出的）实时来源：来源 Id 归一化后逐一相等。
    /// 不相等说明持久化来源已被并发方改写为另一组来源，而本次并未对其加锁 —— 必须原子拒绝。
    /// </summary>
    public static bool ScopeCovers(SalesOrderMutationLockScope scope, long? quotationId, long? piId)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return scope.QuotationId == SalesOrderSourceLineageRules.NormalizeId(quotationId)
               && scope.PiId == SalesOrderSourceLineageRules.NormalizeId(piId);
    }

    /// <summary>
    /// 加锁前读到的持久化来源是否与锁内权威重读一致：任一来源 Id 变化即视为「来源已过期」。
    /// </summary>
    public static bool PersistedSourceUnchanged(long? preliminaryQuotationId, long? preliminaryPiId,
        long? lockedQuotationId, long? lockedPiId)
        => SalesOrderSourceLineageRules.NormalizeId(preliminaryQuotationId)
               == SalesOrderSourceLineageRules.NormalizeId(lockedQuotationId)
           && SalesOrderSourceLineageRules.NormalizeId(preliminaryPiId)
               == SalesOrderSourceLineageRules.NormalizeId(lockedPiId);
}

/// <summary>
/// 规范销售订单写入的加锁范围（ERP-421）：本次需要按确定性锁序取得排它行锁的**实时来源行**。
/// <see cref="None"/> 表示手工订单 / 无法解析的历史来源 —— 不取任何来源行锁，只取目标订单行锁。
/// </summary>
public sealed record SalesOrderMutationLockScope
{
    /// <summary>共享的「无来源锁」范围（只取目标订单行锁）。</summary>
    public static readonly SalesOrderMutationLockScope None = new();

    /// <summary>需加锁的来源报价单 Id（确实解析到行时）；未链接 / 祖先行缺失为 <c>null</c>。</summary>
    public long? QuotationId { get; init; }

    /// <summary>需加锁的来源 PI Id（确实解析到行时）；未链接为 <c>null</c>。</summary>
    public long? PiId { get; init; }

    /// <summary>是否存在需加锁的实时来源行（PI 或报价单）。</summary>
    public bool IsLiveSource { get; init; }
}
