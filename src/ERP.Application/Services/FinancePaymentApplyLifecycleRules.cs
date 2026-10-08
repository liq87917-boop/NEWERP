using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ERP.Application.Services;

/// <summary>
/// 货款申请单（<see cref="FinancePaymentApply"/>）生命周期护栏（ERP-380）：把货款申请单的创建 / 修改 / 提交 / 审核 /
/// 取消 / 删除与读取，统一到同一条「实时启用身份 / 货款申请单（<c>payment-apply</c>）菜单 / 当前权威客户数据范围 +
/// 申请单行锁」的可串行化口径上。
/// <para><b>商业字段完整性</b>：创建与修改校验真实可用客户（存在、未删除、启用）、受支持币种、按币种精度取整后大于 0
/// 的金额、大于 0 的汇率；可空的 <see cref="FinancePaymentApply.SalesOrderId"/> 必须按 Id 精确解析到既有、未删除、
/// 未取消、客户与本申请单一致且币种兼容的销售订单（绝不按单号文本、金额或相似度猜测来源）；历史「未关联来源」
/// （<c>SalesOrderId == null</c>）语义原样保留，只拒绝悬空 / 越界 / 不兼容的伪造链接。</para>
/// <para><b>引用付款护栏</b>：只要还存在任一<strong>未删除且未取消</strong>的付款单
/// （<see cref="FinancePayment.PaymentApplyId"/> 指向本申请单），就拒绝取消 / 删除申请单，也拒绝修改客户 / 来源销售订单 /
/// 币种 / 金额 / 汇率；显式取消的付款单会释放该限制，但历史付款单与审计原样保留（绝不物理删除、绝不静默改写）。</para>
/// <para><b>锁序（ERP-380，跨模块统一）</b>：申请单生命周期动作与「引用付款单」的创建 / 修改 / 提交 / 审核 / 取消 / 删除，
/// 以及两套付款引用证据（分摊）写入与分配合格性判定，都先取用途来源的<strong>货款申请单行锁</strong>、再取
/// <strong>付款单行锁</strong>（<b>先来源申请单行、后付款单行</b>），绝不反向获取；因此申请单的商业改动 / 取消与
/// 「登记引用证据」只能串行执行，且任一写入都在锁内重新复核来源（<see cref="LockApplyRowAsync"/> /
/// <see cref="EnsureSourceApplyUsableAsync"/>）。</para>
/// <para>本类只做<b>纯判定与有界只读查询</b>；不落库、不改写申请单与付款单；「判定 + 写入」的原子性与同单并发串行化
/// 由调用方在同一事务内对申请单行加排它行锁完成。</para>
/// </summary>
public static class FinancePaymentApplyLifecycleRules
{
    /// <summary>货款申请单模块所需既有菜单编码（与 <c>SeedData.Menus</c> 同源）</summary>
    public const string RequiredMenuCode = "payment-apply";

    /// <summary>货款申请单模块菜单中文文案（与既有菜单名一致）</summary>
    public const string RequiredMenuText = "货款申请单";

    /// <summary>汇率保留小数位（0.5 进位，确定性；仅用于比对是否发生商业改动，不做汇率换算）</summary>
    public const int ExchangeRateDecimals = 6;

    /// <summary>生命周期护栏口径文案（接口 / 文档同源）</summary>
    public const string RuleText =
        "货款申请单生命周期护栏：创建 / 修改 / 提交 / 审核 / 取消 / 删除与读取都会重新校验当前实时启用身份、" +
        "货款申请单（payment-apply）菜单授权与当前权威客户数据范围；" +
        "创建与修改校验真实可用客户、受支持币种、按币种精度取整后大于 0 的金额与大于 0 的汇率；" +
        "提供销售订单 Id 时按 Id 精确解析既有、未删除、未取消、客户一致且币种兼容的来源订单（绝不按文本或金额猜测来源），" +
        "历史未关联来源（空来源）语义原样保留；" +
        "只要存在未删除且未取消的引用付款单，就拒绝取消 / 删除申请单或修改客户 / 来源 / 币种 / 金额 / 汇率，" +
        "显式取消的付款单释放限制但历史与审计原样保留；" +
        "申请单的修改 / 提交 / 审核 / 取消 / 删除都在同一事务内先取申请单行锁再加载权威状态与字段，" +
        "并在锁内重新校验身份、菜单授权、客户数据范围与允许的状态流转，再复核持久化商业字段与来源链接；" +
        "申请单生命周期与引用付款单的创建 / 修改 / 流转、两套付款引用证据写入使用同一锁序（先来源申请单行、后付款单行），" +
        "失败整体回滚、状态与审计不变。";

    /// <summary>模块边界文案（不付款 / 不记账 / 不核销，也不改写订单、库存或付款证据）</summary>
    public const string BoundaryText =
        "本护栏只保护货款申请单生命周期与既有付款来源链接：不会真的付款、不会记账或生成凭证、不会核销、不会移动资金，" +
        "也不改写销售订单状态 / 金额与明细、付款单与两套付款引用证据、库存与库存成本、库存流水、退税记录或供应商余额；" +
        "申请单的商业字段（客户 / 来源 / 币种 / 金额 / 汇率）在存在引用付款单时保持冻结，绝不静默改写或物理删除任一历史记录。";

    /// <summary>锁序文案（跨模块统一：先来源申请单行、后付款单行）</summary>
    public const string LockOrderText =
        "ERP-380 统一锁序：先来源货款申请单行、后付款单行（再引用行），绝不反向获取。";

    /// <summary>
    /// 链接 / 读取「销售订单」来源证据所需既有「销售订单」（<c>sales-order</c>）菜单编码（ERP-391，与
    /// <see cref="SalesOrderCancellationRules.RequiredMenuCode"/> 同源，不新增权限模型）。
    /// </summary>
    public const string SourceRequiredMenuCode = "sales-order";

    /// <summary>来源销售订单模块菜单中文文案（与既有菜单名一致）</summary>
    public const string SourceRequiredMenuText = "销售订单";

    /// <summary>来源销售订单菜单授权拒绝文案（fail closed，不返回任何订单数据）</summary>
    public const string SourceMenuDeniedText =
        "当前账号没有「销售订单」（sales-order）模块授权：拒绝读取 / 链接来源销售订单（fail closed，不返回任何订单数据）";

    /// <summary>要求精确客户的拒绝文案（绝不返回任意客户订单）</summary>
    public const string ExactCustomerRequiredText =
        "必须提供精确的客户 Id（正整数）才能查询来源销售订单候选：拒绝返回任意客户订单（fail closed）";

    /// <summary>候选列表中「已取消来源不可作为新来源」的不可选原因文案（ERP-391，与链接拒绝同口径）</summary>
    public const string SourceCancelledCandidateText =
        "销售订单已取消：不能作为新建 / 变更货款申请单的来源（历史已记录的链接按显式状态只读保留，绝不静默重绑定）";

    /// <summary>历史未关联来源的显式证据文案（只读展示用，绝不回填 / 猜测来源）</summary>
    public const string UnlinkedEvidenceText = "当前未关联来源销售订单（历史未关联语义原样保留，绝不回填）";

    /// <summary>已关联来源的显式证据文案前缀</summary>
    public const string LinkedEvidenceText = "已关联销售订单";

    /// <summary>来源已取消的显式证据文案（历史链接只读保留）</summary>
    public const string SourceCancelledEvidenceText =
        "来源销售订单已取消，链接只读保留（不再作为新来源，绝不静默清除 / 重绑定）";

    /// <summary>来源已删除 / 无法解析的显式证据文案（历史链接原样保留）</summary>
    public const string UnavailableEvidenceText =
        "来源销售订单不可用（已删除或无法解析），原链接原样保留（绝不静默清除 / 重绑定）";

    // ==================== 1. 币种、金额、汇率与客户纯校验 ====================

    /// <summary>币种规范化 + 支持范围校验（申请单币种必须来自系统币种口径，才能与来源订单及付款单权威比对）</summary>
    public static string NormalizeApplyCurrency(string? currency)
    {
        var value = CurrencyAmountRules.NormalizeCurrency(currency);
        if (!Enum.GetNames<Currency>().Contains(value, StringComparer.Ordinal))
            throw BusinessException.InvalidParameter(
                $"币种「{value}」不受支持：只允许 {string.Join(" / ", Enum.GetNames<Currency>())}"
                + "（货款申请单币种必须与所引用销售订单及后续付款单币种一致，系统不做汇率换算）");
        return value;
    }

    /// <summary>币种规范化 + 支持范围校验（<see cref="Currency"/> 枚举重载）</summary>
    public static string NormalizeApplyCurrency(Currency currency)
        => NormalizeApplyCurrency(currency.ToString());

    /// <summary>
    /// 申请金额校验（服务端权威）：按币种精度四舍五入（0.5 进位）后必须大于 0；返回取整后的金额。
    /// </summary>
    public static decimal NormalizeApplyAmount(decimal amount, string? currency)
    {
        var rounded = CurrencyAmountRules.RoundAmount(amount, currency);
        if (rounded <= 0m)
            throw BusinessException.InvalidParameter(
                $"申请金额必须大于 0：当前值 {amount} 按币种 {CurrencyAmountRules.NormalizeCurrency(currency)} "
                + $"精度取整后为 {rounded}");
        return rounded;
    }

    /// <summary>申请单金额的权威口径（按币种精度取整；用于与历史金额比对，避免精度差异导致误判变更）</summary>
    public static decimal AuthoritativeApplyAmount(decimal amount, string? currency)
        => CurrencyAmountRules.RoundAmount(amount, currency);

    /// <summary>汇率校验（服务端权威）：必须大于 0；返回按 <see cref="ExchangeRateDecimals"/> 取整后的汇率。</summary>
    public static decimal NormalizeExchangeRate(decimal exchangeRate)
    {
        if (exchangeRate <= 0m)
            throw BusinessException.InvalidParameter($"汇率必须大于 0：当前值 {exchangeRate}");
        return Math.Round(exchangeRate, ExchangeRateDecimals, MidpointRounding.AwayFromZero);
    }

    /// <summary>申请单汇率的权威口径（用于与历史汇率比对）</summary>
    public static decimal AuthoritativeExchangeRate(decimal exchangeRate)
        => Math.Round(exchangeRate, ExchangeRateDecimals, MidpointRounding.AwayFromZero);

    /// <summary>客户可用性校验（存在、未删除、启用）：不存在 / 已删除按不存在拒绝，停用按规则冲突拒绝。</summary>
    public static void EnsureCustomerAvailable(BaseCustomer? customer, long customerId)
    {
        if (customer is null || customer.IsDeleted)
            throw BusinessException.NotFound($"客户（Id={customerId}）不存在或已删除，不能用于货款申请单");
        if (customer.Status != 1)
            throw BusinessException.RuleConflict(
                $"客户「{customer.CustomerName}」已停用：停用客户不能用于新货款申请单或修改申请单客户");
    }

    /// <summary>客户数据范围硬边界（受限制业务员只能操作其被分配客户，越界 fail closed 不泄露存在性）。</summary>
    public static void EnsureCustomerInScope(SalespersonDataScope scope, long customerId)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (!scope.AllowsCustomer(customerId))
            throw new BusinessException(
                "当前账号的客户数据范围不包含该客户（fail closed，不泄露范围外客户）",
                ErrorCodes.Forbidden);
    }

    // ==================== 2. 身份 / 菜单 / 客户数据范围（fail closed） ====================

    /// <summary>
    /// 校验当前账号的<strong>实时启用身份</strong>与货款申请单（<c>payment-apply</c>）菜单授权：无身份 / 账号不存在或
    /// 被删除 / 账号被禁用 / 无角色 / 无授权一律拒绝（fail closed，绝不退化为匿名或管理员）。
    /// <para>每次调用都重新查询 <c>SysUsers</c> 与「角色 → 菜单」授权（无缓存），因此账号停用或授权撤销后
    /// 下一次请求立即收敛；申请单的修改 / 提交 / 审核 / 取消 / 删除会在同一事务内、取得申请单行锁之后再调用本方法，
    /// 用实时身份与授权覆盖「先读后写」窗口。</para>
    /// </summary>
    public static Task EnsureMenuAuthorizedAsync(IErpDbContext db, long? userId)
        => EnsureMenuCoreAsync(db, userId, RequiredMenuCode,
            $"当前账号没有「{RequiredMenuText}」（{RequiredMenuCode}）模块授权");

    /// <summary>
    /// 链接 / 读取「销售订单」来源证据所需既有「销售订单」（<c>sales-order</c>）菜单授权（ERP-391）：
    /// 实时启用身份（缺失 / 非法 → 未认证；不存在 / 已删除 → 未认证；禁用 → 权限不足）→ 既有菜单授权
    /// （fail closed，绝不退化为匿名或管理员）。
    /// <para>每次调用都重新查询 <c>SysUsers</c> 与「角色 → 菜单」授权（无缓存），授权撤销后下一次请求立即收敛；
    /// 只复用既有菜单权限，不新增任何用户授权。</para>
    /// </summary>
    public static Task EnsureSourceMenuAuthorizedAsync(IErpDbContext db, long? userId)
        => EnsureMenuCoreAsync(db, userId, SourceRequiredMenuCode, SourceMenuDeniedText);

    private static async Task EnsureMenuCoreAsync(
        IErpDbContext db, long? userId, string menuCode, string deniedText)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (userId is null or <= 0)
            throw new BusinessException("请先登录后再访问货款申请单", ErrorCodes.Unauthorized);

        var user = await db.SysUsers.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == userId.Value && !u.IsDeleted);
        if (user is null)
            throw new BusinessException("登录账号不存在或已删除，禁止操作货款申请单", ErrorCodes.Unauthorized);
        if (user.Status != UserStatus.Enabled)
            throw new BusinessException("登录账号已禁用，禁止操作货款申请单（fail closed）", ErrorCodes.Forbidden);

        var menuCodes = await CustomerReceivableReconciliationService.LoadAuthorizedMenuCodesAsync(db, userId.Value);
        if (!menuCodes.Contains(menuCode, StringComparer.OrdinalIgnoreCase))
            throw new BusinessException(deniedText, ErrorCodes.Forbidden);
    }

    /// <summary>
    /// 三重授权校验：身份（缺失 / 非正整数 → 未认证）→ 货款申请单菜单授权 → 客户数据范围（越界 → 权限不足）。
    /// 每次请求都重新解析，撤销授权 / 客户分配变更后立即收敛。
    /// </summary>
    public static async Task EnsureAuthorizedAsync(IErpDbContext db, long? userId, long customerId)
    {
        await EnsureMenuAuthorizedAsync(db, userId);
        var scope = await SalespersonDataScopeService.ResolveAsync(db, userId!.Value);
        EnsureCustomerInScope(scope, customerId);
    }

    // ==================== 3. 来源销售订单解析（按 Id，绝不从文本猜测） ====================

    /// <summary>
    /// 解析货款申请单的来源销售订单（可空）：未提供 Id 返回 null（保留有效的历史「未关联来源」语义）；
    /// 提供 Id 时必须按 Id 精确解析到既有、未删除、未取消的销售订单，且订单客户与申请单客户一致、币种兼容。
    /// <para>绝不按订单号文本、金额或相似度猜测来源：Id 不存在 / 已删除即按「来源不存在」拒绝（dangling / forged）；
    /// 客户不一致（foreign）或币种不兼容时 fail closed，不泄露范围外订单。</para>
    /// </summary>
    public static async Task<SalesOrder?> ResolveSourceSalesOrderAsync(
        IErpDbContext db, long? salesOrderId, long customerId, string currency)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (salesOrderId is null or <= 0) return null;

        var order = await db.SalesOrders.AsNoTracking()
            .FirstOrDefaultAsync(o => o.Id == salesOrderId.Value && !o.IsDeleted)
            ?? throw BusinessException.NotFound(
                $"销售订单（Id={salesOrderId.Value}）不存在或已删除，不能作为货款申请单来源"
                + "（系统不按订单号文本、金额或相似度猜测来源）");

        if (order.Status == DocumentStatus.Cancelled)
            throw BusinessException.RuleConflict(
                $"销售订单「{order.OrderNo}」已取消，不能作为货款申请单来源");

        if (order.CustomerId != customerId)
            throw BusinessException.RuleConflict(
                $"销售订单「{order.OrderNo}」的客户与货款申请单客户不一致，不能作为来源"
                + "（来源必须是本申请单客户的既有订单）");

        var orderCurrency = CurrencyAmountRules.NormalizeCurrency(order.Currency.ToString());
        if (!string.Equals(orderCurrency, currency, StringComparison.Ordinal))
            throw BusinessException.RuleConflict(
                $"销售订单「{order.OrderNo}」的币种 {orderCurrency} 与货款申请单币种 {currency} 不一致，"
                + "不能引用（不做汇率换算）");

        return order;
    }

    /// <summary>
    /// 新建 / 变更来源时可被显式选择的状态判定（ERP-391）：既有「未取消」订单可以为新来源建立链接；
    /// 已取消订单只能作为<b>历史</b>已记录链接只读保留，绝不作为新来源（与
    /// <see cref="ResolveSourceSalesOrderAsync"/> 的取消拒绝同口径）。
    /// </summary>
    public static bool IsEligibleNewSource(DocumentStatus status) => status != DocumentStatus.Cancelled;

    /// <summary>
    /// 构造货款申请单来源链接的<b>显式状态</b>文案（详情 / 只读展示用）：未关联（历史）→ 显式「未关联」；
    /// 已关联且来源有效 → 已关联 + 订单号；来源已取消 → 已关联 + 「来源已取消，只读保留」；
    /// 来源已删除 / 无法解析 → 「来源不可用，原链接原样保留」。
    /// <para>本方法绝不写库、绝不重绑定 / 清除链接，也绝不因来源失效而抛异常（历史必须可读）。</para>
    /// </summary>
    public static async Task<string> DescribeStoredSourceAsync(
        IErpDbContext db, FinancePaymentApply apply, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(apply);

        if (apply.SalesOrderId is null or <= 0) return UnlinkedEvidenceText;

        var order = await db.SalesOrders.AsNoTracking()
            .Where(o => o.Id == apply.SalesOrderId.Value && !o.IsDeleted)
            .Select(o => new { o.OrderNo, o.Status })
            .FirstOrDefaultAsync(ct);

        if (order is null)
            return $"{LinkedEvidenceText} Id {apply.SalesOrderId.Value}：{UnavailableEvidenceText}";

        return order.Status == DocumentStatus.Cancelled
            ? $"{LinkedEvidenceText}「{order.OrderNo}」：{SourceCancelledEvidenceText}"
            : $"{LinkedEvidenceText}「{order.OrderNo}」";
    }

    /// <summary>
    /// 读取申请单当前持久化的来源销售订单 Id（仅用于按「先来源销售订单行、后货款申请单行」取锁；权威校验一律在锁内
    /// 重做）。未关联来源（null）/ 不存在 / 已删除返回 null，保留历史未关联来源语义。
    /// </summary>
    public static async Task<long?> ReadSourceSalesOrderIdAsync(IErpDbContext db, long applyId)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (applyId <= 0) return null;

        return await db.FinancePaymentApplies.AsNoTracking()
            .Where(a => a.Id == applyId && !a.IsDeleted)
            .Select(a => a.SalesOrderId)
            .FirstOrDefaultAsync();
    }

    /// <summary>
    /// 用既有规则复核<strong>已持久化</strong>货款申请单的客户 / 币种 / 金额 / 汇率 / 来源链接一致性（只读，不写库、
    /// 不新增审批要求）：客户必须存在、未删除且启用，币种必须在既有系统币种口径内，金额按币种精度取整后必须大于 0，
    /// 汇率必须大于 0，关联销售订单（若存在）必须仍可按 Id 解析到既有、未删除、未取消、客户一致且币种兼容的订单；
    /// 未关联销售订单的历史申请场景保持不变。
    /// <para>提交 / 审核会在申请单行锁与同一事务内调用本方法，防止在锁外被改写的非法持久化数据被流转放行。</para>
    /// </summary>
    public static async Task EnsurePersistedApplyConsistentAsync(IErpDbContext db, FinancePaymentApply apply)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(apply);

        var currency = NormalizeApplyCurrency(apply.Currency);
        NormalizeApplyAmount(apply.Amount, currency);
        NormalizeExchangeRate(apply.ExchangeRate);

        var customer = await db.BaseCustomers.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == apply.CustomerId);
        EnsureCustomerAvailable(customer, apply.CustomerId);

        await ResolveSourceSalesOrderAsync(db, apply.SalesOrderId, apply.CustomerId, currency);
    }

    // ==================== 4. 引用付款单护栏（只读、有界） ====================

    /// <summary>
    /// 是否存在任一<strong>引用本申请单</strong>的付款单：未删除且未取消（<see cref="DocumentStatus.Cancelled"/> 的付款单
    /// **显式取消**后释放护栏，但历史记录原样保留）。绝不按金额、日期或备注推断引用关系。
    /// </summary>
    public static async Task<bool> HasReferencingPaymentsAsync(IErpDbContext db, long applyId)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (applyId <= 0) return false;

        return await db.FinancePayments.AsNoTracking()
            .AnyAsync(p => !p.IsDeleted
                           && p.PaymentApplyId == applyId
                           && p.Status != DocumentStatus.Cancelled);
    }

    /// <summary>
    /// 拒绝存在未删除且未取消引用付款单的申请单生命周期动作（取消 / 删除 / 修改客户 / 来源 / 币种 / 金额 / 汇率）。
    /// 要解除限制必须先显式取消相关付款单（取消只改状态、保留历史与审计，绝不物理删除引用证据）。
    /// </summary>
    public static async Task EnsureNoReferencingPaymentsAsync(IErpDbContext db, long applyId, string action)
    {
        if (await HasReferencingPaymentsAsync(db, applyId))
            throw BusinessException.RuleConflict(
                $"货款申请单存在未删除且未取消的引用付款单，不能{action}："
                + "请先在付款单模块显式取消相关付款单（取消保留历史与审计，不物理删除引用证据）");
    }

    /// <summary>
    /// 锁内重新复核<strong>付款单来源</strong>（<c>PaymentApplyId</c>）仍可用：必须解析到既有、未删除且未取消的申请单，
    /// 否则拒绝继续登记引用证据 / 流转（fail closed，绝不使用陈旧或孤立的来源）。未提供来源（null）时不做限制，
    /// 保留历史「未关联申请单」付款场景的既有语义。
    /// </summary>
    public static async Task EnsureSourceApplyUsableAsync(IErpDbContext db, long? paymentApplyId, string action)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (paymentApplyId is null or <= 0) return;

        var apply = await db.FinancePaymentApplies.AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == paymentApplyId.Value && !a.IsDeleted)
            ?? throw BusinessException.NotFound(
                $"货款申请单（Id={paymentApplyId.Value}）不存在或已删除，不能继续{action}"
                + "（绝不按申请单号文本、金额或相似度猜测来源）");

        if (apply.Status == DocumentStatus.Cancelled)
            throw BusinessException.RuleConflict(
                $"来源货款申请单「{apply.ApplyNo}」已取消，不能继续{action}"
                + "（历史付款单与引用证据原样保留，不再新增或流转）");
    }

    /// <summary>
    /// 解析付款单当前持久化的来源申请单 Id（仅用于按「先来源申请单行、后付款单行」取锁；权威校验一律在锁内重做）。
    /// 未关联来源（null）返回 null，保留历史未关联付款语义。
    /// </summary>
    public static async Task<long?> ReadPaymentApplyIdAsync(IErpDbContext db, long paymentId)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (paymentId <= 0) return null;

        return await db.FinancePayments.AsNoTracking()
            .Where(p => p.Id == paymentId && !p.IsDeleted)
            .Select(p => p.PaymentApplyId)
            .FirstOrDefaultAsync();
    }

    // ==================== 5. 申请单行锁（与引用付款写入 / 生命周期串行化） ====================

    /// <summary>
    /// 关系型后端（SQL Server）判定：内存库等非关系型提供程序没有行锁语义，锁定与事务等价无操作。
    /// 使用 <c>DatabaseFacade.ProviderName</c>（EF Core 基础 API），不依赖关系型扩展。
    /// </summary>
    public static bool IsRelationalProvider(IErpDbContext db)
        => string.Equals(db.Database.ProviderName, "Microsoft.EntityFrameworkCore.SqlServer",
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 对货款申请单行加更新锁，把同单并发的「申请单生命周期 / 商业改动」与「引用付款单的创建 / 修改 / 流转、
    /// 两套付款引用证据写入」串行化在同一事务内。
    /// <para>实现（仅用 EF Core 基础 API，不依赖关系型扩展）：在调用方事务内对申请单行发出一条「审计时间戳刷新」的
    /// UPDATE，从而取得排它行锁（X 锁，持有至事务结束），语义等价于 <c>SELECT ... WITH (UPDLOCK, HOLDLOCK)</c>；
    /// <c>UpdatedAt</c> 是技术审计字段、不是商业字段，因此不构成对申请单商业内容的静默改写。
    /// 内存库等非关系型提供程序无行锁语义，直接跳过（事务等价无事务）。</para>
    /// <para><b>锁序（ERP-380）</b>：来源申请单行必须先于付款单行（再引用行）获取，本方法必须由调用方在
    /// 同一事务内、任何付款单行锁之前调用，绝不反向获取下游锁。</para>
    /// </summary>
    public static async Task LockApplyRowAsync(IErpDbContext db, long applyId)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (applyId <= 0) return;
        if (!IsRelationalProvider(db)) return;

        var apply = await db.FinancePaymentApplies
            .FirstOrDefaultAsync(a => a.Id == applyId && !a.IsDeleted);
        if (apply is null) return;

        apply.UpdatedAt = DateTime.Now;
        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            throw BusinessException.RuleConflict(
                "货款申请单正在被并发修改，本次操作未生效：请刷新后重试（原始申请单、付款单与引用证据均未改变）");
        }
    }
}
