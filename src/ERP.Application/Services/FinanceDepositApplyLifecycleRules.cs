using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ERP.Application.Services;

/// <summary>
/// 定金申请单（<see cref="FinanceDepositApply"/>）生命周期护栏（ERP-381）：把定金申请单的创建 / 修改 / 提交 / 审核 /
/// 取消 / 删除与读取，统一到同一条「实时启用身份 / 定金申请单（<c>deposit-apply</c>）菜单 / 当前权威客户数据范围 +
/// 来源销售订单行锁 + 定金申请单行锁」的可串行化口径上。
/// <para><b>商业字段完整性</b>：创建与修改校验真实可用客户（存在、未删除、启用）、受支持币种、按币种精度取整后大于 0
/// 的金额、大于 0 的汇率；可空的 <see cref="FinanceDepositApply.SalesOrderId"/> 必须按 Id 精确解析到既有、未删除、
/// 未取消、客户与本申请单一致且币种兼容的销售订单（绝不按单号文本、金额或相似度猜测来源）；历史「未关联来源」
/// （<c>SalesOrderId == null</c>）语义原样保留，只拒绝悬空 / 越界 / 不兼容的伪造链接。</para>
/// <para><b>与销售订单取消证据护栏协同</b>：定金申请单以 <c>SalesOrderId</c> 显式指向销售订单，属于既有「收款申请」
/// 权威引用；只要仍存在<strong>未删除且未取消</strong>的定金申请单指向某销售订单，
/// <see cref="SalesOrderCancellationRules"/> 就拒绝取消该销售订单。本护栏在修改 / 提交 / 审核 / 取消 / 删除时
/// <strong>先取来源销售订单行锁、再取定金申请单行锁</strong>（与销售订单取消同一把订单行锁），因此
/// 「定金申请单审核 / 提交」与「来源销售订单取消」不可能同时成功。</para>
/// <para><b>锁序（ERP-381，跨模块统一）</b>：先<strong>来源销售订单行</strong>（UPDLOCK/HOLDLOCK，与销售订单取消 /
/// 出库审核 / 预装柜流程共用同一把上游订单行锁）、后<strong>定金申请单行</strong>，绝不反向获取。行锁的实际执行留在
/// 控制器（关系型提供程序的原始 SQL 能力属基础设施层）：本类只保留锁语句说明常量、需要锁定哪些订单行的确定性解析
/// 与「审计时间戳刷新」式定金申请单行锁。</para>
/// <para>本类只做<b>纯判定与有界只读查询</b>；不落库、不改写申请单（<see cref="LockApplyRowAsync"/> 只刷新技术审计
/// 时间戳以取得排它行锁）；「判定 + 写入」的原子性与同单并发串行化由调用方在同一事务内加锁完成。</para>
/// </summary>
public static class FinanceDepositApplyLifecycleRules
{
    /// <summary>定金申请单模块所需既有菜单编码（与 <c>SeedData.Menus</c> 同源）</summary>
    public const string RequiredMenuCode = "deposit-apply";

    /// <summary>定金申请单模块菜单中文文案（与既有菜单名一致）</summary>
    public const string RequiredMenuText = "定金申请单";

    /// <summary>汇率保留小数位（0.5 进位，确定性；仅用于比对是否发生商业改动，不做汇率换算）</summary>
    public const int ExchangeRateDecimals = 6;

    /// <summary>生命周期护栏口径文案（接口 / 文档同源）</summary>
    public const string RuleText =
        "定金申请单生命周期护栏：创建 / 修改 / 提交 / 审核 / 取消 / 删除与读取都会重新校验当前实时启用身份、" +
        "定金申请单（deposit-apply）菜单授权与当前权威客户数据范围；" +
        "创建与修改校验真实可用客户、受支持币种、按币种精度取整后大于 0 的金额与大于 0 的汇率；" +
        "提供销售订单 Id 时按 Id 精确解析既有、未删除、未取消、客户一致且币种兼容的来源订单（绝不按文本或金额猜测来源），" +
        "历史未关联来源（空来源）语义原样保留；" +
        "修改 / 提交 / 审核 / 取消 / 删除都在同一事务内先取来源销售订单行锁、再取定金申请单行锁，" +
        "并在锁内重新校验身份、菜单授权、客户数据范围、既有与请求客户 / 来源范围、允许的状态流转、持久化商业字段与来源链接；" +
        "取消销售订单时，只要仍存在未删除且未取消的定金申请单以其为显式来源即拒绝，两条生命周期不可能同时成功；" +
        "失败整体回滚、状态与审计不变。";

    /// <summary>模块边界文案（不收款 / 不记账 / 不核销，也不改写订单、库存或收款证据）</summary>
    public const string BoundaryText =
        "本护栏只保护定金申请单生命周期与既有销售订单来源链接：不会真的收款、不会记账或生成凭证、不会核销、不会移动资金，" +
        "也不改写销售订单状态 / 金额与明细、收款单与分摊证据、库存与库存成本、库存流水、退税记录或客户余额；" +
        "不新增菜单 / 角色 / 用户授权或表结构，也不把空身份当作管理员；" +
        "定金申请单的商业字段（客户 / 来源 / 币种 / 金额 / 汇率）与删除标记只在既有允许的状态流转下变更，绝不物理删除任一历史记录。";

    /// <summary>锁序文案（跨模块统一：先来源销售订单行、后定金申请单行）</summary>
    public const string LockOrderText =
        "ERP-381 统一锁序：先来源销售订单行（UPDLOCK, HOLDLOCK，与销售订单取消共用同一把上游订单行锁），" +
        "后定金申请单行，绝不反向获取。";

    /// <summary>来源销售订单行锁说明（行锁由基础设施层执行，与销售订单取消 / 出库审核共用同一把来源行锁）</summary>
    public const string SourceOrderLockNote =
        "来源销售订单行锁语句与销售订单取消共用同一常量（PreLoadingSalesOrderLinkRules.LockSalesOrderRowSql），" +
        "多个来源订单按 Id 升序加锁，绝不反向获取下游锁。";

    /// <summary>
    /// 链接 / 读取「销售订单」来源证据所需既有「销售订单」（<c>sales-order</c>）菜单编码（ERP-390，与
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

    /// <summary>候选列表中「已取消来源不可作为新来源」的不可选原因文案（ERP-390，与链接拒绝同口径）</summary>
    public const string SourceCancelledCandidateText =
        "销售订单已取消：不能作为新建 / 变更定金申请单的来源（历史已记录的链接按显式状态只读保留，绝不静默重绑定）";

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

    /// <summary>币种规范化 + 支持范围校验（申请单币种必须来自系统币种口径，才能与来源订单权威比对）</summary>
    public static string NormalizeApplyCurrency(string? currency)
    {
        var value = CurrencyAmountRules.NormalizeCurrency(currency);
        if (!Enum.GetNames<Currency>().Contains(value, StringComparer.Ordinal))
            throw BusinessException.InvalidParameter(
                $"币种「{value}」不受支持：只允许 {string.Join(" / ", Enum.GetNames<Currency>())}"
                + "（定金申请单币种必须与所引用销售订单及后续收款单币种一致，系统不做汇率换算）");
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
            throw BusinessException.NotFound($"客户（Id={customerId}）不存在或已删除，不能用于定金申请单");
        if (customer.Status != 1)
            throw BusinessException.RuleConflict(
                $"客户「{customer.CustomerName}」已停用：停用客户不能用于新定金申请单或修改申请单客户");
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
    /// 校验当前账号的<strong>实时启用身份</strong>与定金申请单（<c>deposit-apply</c>）菜单授权：无身份 / 账号不存在或
    /// 被删除 / 账号被禁用 / 无角色 / 无授权一律拒绝（fail closed，绝不退化为匿名或管理员）。
    /// <para>每次调用都重新查询 <c>SysUsers</c> 与「角色 → 菜单」授权（无缓存），因此账号停用或授权撤销后
    /// 下一次请求立即收敛；申请单的修改 / 提交 / 审核 / 取消 / 删除会在同一事务内、取得行锁之后再调用本方法，
    /// 用实时身份与授权覆盖「先读后写」窗口。</para>
    /// </summary>
    public static Task EnsureMenuAuthorizedAsync(IErpDbContext db, long? userId)
        => EnsureMenuCoreAsync(db, userId, RequiredMenuCode,
            $"当前账号没有「{RequiredMenuText}」（{RequiredMenuCode}）模块授权");

    /// <summary>
    /// 链接 / 读取「销售订单」来源证据所需既有「销售订单」（<c>sales-order</c>）菜单授权（ERP-390）：
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
            throw new BusinessException("请先登录后再访问定金申请单", ErrorCodes.Unauthorized);

        var user = await db.SysUsers.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == userId.Value && !u.IsDeleted);
        if (user is null)
            throw new BusinessException("登录账号不存在或已删除，禁止操作定金申请单", ErrorCodes.Unauthorized);
        if (user.Status != UserStatus.Enabled)
            throw new BusinessException("登录账号已禁用，禁止操作定金申请单（fail closed）", ErrorCodes.Forbidden);

        var menuCodes = await CustomerReceivableReconciliationService.LoadAuthorizedMenuCodesAsync(db, userId.Value);
        if (!menuCodes.Contains(menuCode, StringComparer.OrdinalIgnoreCase))
            throw new BusinessException(deniedText, ErrorCodes.Forbidden);
    }

    /// <summary>
    /// 三重授权校验：身份（缺失 / 非正整数 → 未认证）→ 定金申请单菜单授权 → 客户数据范围（越界 → 权限不足）。
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
    /// 解析定金申请单的来源销售订单（可空）：未提供 Id 返回 null（保留有效的历史「未关联来源」语义）；
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
                $"销售订单（Id={salesOrderId.Value}）不存在或已删除，不能作为定金申请单来源"
                + "（系统不按订单号文本、金额或相似度猜测来源）");

        if (order.Status == DocumentStatus.Cancelled)
            throw BusinessException.RuleConflict(
                $"销售订单「{order.OrderNo}」已取消，不能作为定金申请单来源");

        if (order.CustomerId != customerId)
            throw BusinessException.RuleConflict(
                $"销售订单「{order.OrderNo}」的客户与定金申请单客户不一致，不能作为来源"
                + "（来源必须是本申请单客户的既有订单）");

        var orderCurrency = CurrencyAmountRules.NormalizeCurrency(order.Currency.ToString());
        if (!string.Equals(orderCurrency, currency, StringComparison.Ordinal))
            throw BusinessException.RuleConflict(
                $"销售订单「{order.OrderNo}」的币种 {orderCurrency} 与定金申请单币种 {currency} 不一致，"
                + "不能引用（不做汇率换算）");

        return order;
    }

    /// <summary>
    /// 新建 / 变更来源时可被显式选择的状态判定（ERP-390）：既有「未取消」订单可以为新来源建立链接；
    /// 已取消订单只能作为<b>历史</b>已记录链接只读保留，绝不作为新来源（与
    /// <see cref="ResolveSourceSalesOrderAsync"/> 的取消拒绝同口径）。
    /// </summary>
    public static bool IsEligibleNewSource(DocumentStatus status) => status != DocumentStatus.Cancelled;

    /// <summary>
    /// 构造定金申请单来源链接的<b>显式状态</b>文案（详情 / 只读展示用）：未关联（历史）→ 显式「未关联」；
    /// 已关联且来源有效 → 已关联 + 订单号；来源已取消 → 已关联 + 「来源已取消，只读保留」；
    /// 来源已删除 / 无法解析 → 「来源不可用，原链接原样保留」。
    /// <para>本方法绝不写库、绝不重绑定 / 清除链接，也绝不因来源失效而抛异常（历史必须可读）。</para>
    /// </summary>
    public static async Task<string> DescribeStoredSourceAsync(
        IErpDbContext db, FinanceDepositApply apply, CancellationToken ct = default)
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
    /// 用既有规则复核<strong>已持久化</strong>定金申请单的客户 / 币种 / 金额 / 汇率 / 来源链接一致性（只读，不写库、
    /// 不新增审批要求）：客户必须存在、未删除且启用，币种必须在既有系统币种口径内，金额按币种精度取整后必须大于 0，
    /// 汇率必须大于 0，关联销售订单（若存在）必须仍可按 Id 解析到既有、未删除、未取消、客户一致且币种兼容的订单；
    /// 未关联销售订单的历史申请场景保持不变。
    /// <para>修改 / 提交 / 审核会在来源订单行锁与定金申请单行锁内、同一事务中调用本方法，防止在锁外被改写的非法
    /// 持久化数据被流转放行。</para>
    /// </summary>
    public static async Task EnsurePersistedApplyConsistentAsync(IErpDbContext db, FinanceDepositApply apply)
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

    /// <summary>
    /// 读取申请单当前持久化的来源销售订单 Id（仅用于按「先来源销售订单行、后定金申请单行」取锁；权威校验一律在锁内
    /// 重做）。未关联来源（null）/ 不存在 / 已删除返回 null，保留历史未关联来源语义。
    /// </summary>
    public static async Task<long?> ReadSourceSalesOrderIdAsync(IErpDbContext db, long applyId)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (applyId <= 0) return null;

        return await db.FinanceDepositApplies.AsNoTracking()
            .Where(a => a.Id == applyId && !a.IsDeleted)
            .Select(a => a.SalesOrderId)
            .FirstOrDefaultAsync();
    }

    /// <summary>
    /// 合并本次动作需要加锁的来源销售订单 Id（既有持久化来源 + 请求来源）：去重、只保留正整数，并按 Id 升序返回，
    /// 以保证多个订单行锁的确定性获取顺序（绝不反向获取下游锁）。
    /// </summary>
    public static IReadOnlyList<long> MergeSourceOrderLockIds(params long?[] salesOrderIds)
    {
        ArgumentNullException.ThrowIfNull(salesOrderIds);
        return salesOrderIds
            .Where(id => id is > 0)
            .Select(id => id!.Value)
            .Distinct()
            .OrderBy(id => id)
            .ToList();
    }

    // ==================== 4. 行锁（与来源销售订单取消串行化） ====================

    /// <summary>
    /// 关系型后端（SQL Server）判定：内存库等非关系型提供程序没有行锁语义，锁定与事务等价无操作。
    /// 使用 <c>DatabaseFacade.ProviderName</c>（EF Core 基础 API），不依赖关系型扩展。
    /// </summary>
    public static bool IsRelationalProvider(IErpDbContext db)
        => string.Equals(db.Database.ProviderName, "Microsoft.EntityFrameworkCore.SqlServer",
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 对定金申请单行加更新锁，把同单并发的「申请单生命周期 / 商业改动」串行化在同一事务内。
    /// <para>实现（仅用 EF Core 基础 API，不依赖关系型扩展）：在调用方事务内对申请单行发出一条「审计时间戳刷新」的
    /// UPDATE，从而取得排它行锁（X 锁，持有至事务结束），语义等价于 <c>SELECT ... WITH (UPDLOCK, HOLDLOCK)</c>；
    /// <c>UpdatedAt</c> 是技术审计字段、不是商业字段，因此不构成对申请单商业内容的静默改写。
    /// 内存库等非关系型提供程序无行锁语义，直接跳过（事务等价无事务）。</para>
    /// <para><b>锁序（ERP-381）</b>：来源销售订单行必须先于定金申请单行获取，本方法必须由调用方在同一事务内、
    /// 任何来源销售订单行锁之后调用，绝不反向获取上游锁。</para>
    /// </summary>
    public static async Task LockApplyRowAsync(IErpDbContext db, long applyId)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (applyId <= 0) return;
        if (!IsRelationalProvider(db)) return;

        var apply = await db.FinanceDepositApplies
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
                "定金申请单正在被并发修改，本次操作未生效：请刷新后重试（原始申请单与审计均未改变）");
        }
    }
}
