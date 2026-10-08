using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ERP.Application.Services;

/// <summary>
/// 供应商付款单生命周期护栏（ERP-351）：把付款单的创建 / 修改 / 提交 / 审核 / 取消 / 删除与读取，
/// 以及 ERP-049「付款单 → 采购订单」与 ERP-066「付款单 → 供应商采购发票」两套付款引用证据写入，
/// 统一到同一条「身份 / 付款单（payment）菜单 / 客户数据范围 + 付款单行锁」的可串行化口径上。
/// <para>关键不变量：只要付款单还存在任一<strong>有效</strong>（未作废、未删除）的付款引用证据，就拒绝
/// 取消 / 删除付款单，也拒绝以会破坏证据的方式修改供应商 / 币种 / 金额；要解除限制必须先走既有显式作废服务
/// （<see cref="SupplierPaymentAllocationService.VoidAsync"/> / <see cref="SupplierPaymentInvoiceAllocationService.VoidAsync"/>），
/// 作废只保留原始证据、绝不物理删除、绝不静默改写。</para>
/// <para>付款单金额是同一张付款单的<b>唯一、同币种分摊额度</b>：「付款单 → 采购订单」与
/// 「付款单 → 供应商采购发票」两套有效引用行在付款单行锁下共同占用同一额度，任一写入都必须把两套
/// 有效行合计后与付款单权威金额比较（绝不跨币种合计、绝不重复计算证据）。</para>
/// <para>ERP-379：付款单的<b>提交 / 审核</b>与已有的修改 / 取消 / 删除一样，都在同一个事务内先取付款单行锁
/// （<see cref="LockPaymentRowAsync"/>），再加载权威状态，并在锁内重新复核<strong>实时启用身份</strong>
/// （账号存在、未删除且启用）、付款单（payment）菜单授权、权威来源客户数据范围与允许的状态流转，
/// 再用既有规则校验持久化的金额 / 币种 / 来源（<see cref="EnsurePersistedPaymentConsistentAsync"/>，
/// 不新增任何审批要求）；任一校验失败整体回滚，状态、原始字段、删除标记与审计不变。</para>
/// <para>本类只做<b>纯判定与有界只读查询</b>；不落库、不改写付款单与引用行；「判定 + 状态变更」的原子性与
/// 同单并发串行化由调用方在同一可串行化事务内对付款单行加排它行锁完成。</para>
/// </summary>
public static class SupplierPaymentLifecycleRules
{
    /// <summary>付款单模块所需既有菜单编码（与 <c>SeedData.Menus</c> 同源）</summary>
    public const string RequiredMenuCode = "payment";

    /// <summary>付款单模块菜单中文文案（与既有菜单名一致）</summary>
    public const string RequiredMenuText = "付款单";

    /// <summary>生命周期护栏口径文案（接口 / 文档同源）</summary>
    public const string RuleText =
        "付款单生命周期护栏：创建 / 修改 / 提交 / 审核 / 取消 / 删除与读取都会重新校验当前身份、付款单（payment）菜单授权与客户数据范围；" +
        "创建与修改校验真实可用供应商、受支持币种与按币种精度取整后大于 0 的金额；" +
        "提供货款申请单 Id 时按 Id 解析既有、未删除且未取消的申请单，并校验币种与金额兼容与客户数据范围（绝不按单号文本或金额猜测来源）；" +
        "只要存在有效的「付款单 → 采购订单」或「付款单 → 供应商采购发票」付款引用证据，就拒绝取消 / 删除或修改供应商 / 币种 / 金额，" +
        "必须先走既有显式作废服务释放限制（作废保留历史、绝不物理删除）；" +
        "付款单的修改 / 提交 / 审核 / 取消 / 删除都在同一事务内先取付款单行锁再加载权威状态与字段，" +
        "并在锁内重新校验实时启用身份、付款单菜单授权、权威来源客户数据范围与允许的状态流转，再用既有规则复核持久化金额 / 币种 / 来源（不新增审批要求），" +
        "失败整体回滚、状态与审计不变；" +
        "付款单生命周期与两套引用证据写入使用同一锁序（先付款单行、后引用行），两套证据共同占用同一付款额度，绝不跨币种合计、绝不重复计算证据。" +
        "ERP-380：付款单的创建 / 修改 / 提交 / 审核 / 取消 / 删除与两套付款引用证据写入在存在来源货款申请单时，" +
        "都先取来源货款申请单行锁、再取付款单行锁（先来源申请单行、后付款单行），与来源申请单的商业改动 / 取消 / 删除串行化，" +
        "并在锁内重新复核来源链接未被并发改写。";

    /// <summary>模块边界文案（不付款 / 不记账 / 不核销，也不改写采购订单、发票、库存或供应商余额）</summary>
    public const string BoundaryText =
        "本护栏只保护付款单生命周期与既有付款引用证据：不会真的付款、不会记账或生成凭证、不会核销、不会移动资金，" +
        "也不改写采购订单状态 / 到货进度 / 金额与明细 / 结算进度、供应商采购发票、采购订单引用证据、发票引用证据、" +
        "库存与库存成本、库存流水、退税记录、费用与供应商余额；付款单金额作为同一张付款单的唯一、同币种分摊额度，" +
        "由「付款单 → 采购订单」与「付款单 → 供应商采购发票」两套证据在付款单行锁下共同占用；绝不跨币种合计、绝不重复计算证据、绝不静默改写或删除任一证据。";

    // ==================== 1. 币种、金额、供应商纯校验 ====================

    /// <summary>币种规范化 + 支持范围校验（付款单币种必须来自系统币种口径，才能与引用证据权威比对）</summary>
    public static string NormalizePaymentCurrency(string? currency)
    {
        var value = CurrencyAmountRules.NormalizeCurrency(currency);
        if (!Enum.GetNames<Currency>().Contains(value, StringComparer.Ordinal))
            throw BusinessException.InvalidParameter(
                $"币种「{value}」不受支持：只允许 {string.Join(" / ", Enum.GetNames<Currency>())}"
                + "（付款单币种必须与所引用采购订单 / 采购发票的币种一致，系统不做汇率换算）");
        return value;
    }

    /// <summary>币种规范化 + 支持范围校验（<see cref="Currency"/> 枚举重载）</summary>
    public static string NormalizePaymentCurrency(Currency currency)
        => NormalizePaymentCurrency(currency.ToString());

    /// <summary>
    /// 付款金额校验（服务端权威）：按币种精度四舍五入（0.5 进位）后必须大于 0；
    /// 返回取整后的金额（写入即取整值，系统不自动调整差额、不做汇率换算）。
    /// </summary>
    public static decimal NormalizePaymentAmount(decimal amount, string? currency)
    {
        var rounded = CurrencyAmountRules.RoundAmount(amount, currency);
        if (rounded <= 0)
            throw BusinessException.InvalidParameter(
                $"付款金额必须大于 0：收到 {amount}，按 {CurrencyAmountRules.NormalizeCurrency(currency)} "
                + $"精度取整后为 {rounded}");
        return rounded;
    }

    /// <summary>付款单金额的权威口径（按币种精度取整；用于与历史金额比对，避免精度差异导致误判变更）</summary>
    public static decimal AuthoritativePaymentAmount(decimal amount, string? currency)
        => CurrencyAmountRules.RoundAmount(amount, currency);

    /// <summary>供应商可用性校验（存在、未删除、启用）：不存在 / 已删除按不存在拒绝，停用按规则冲突拒绝。</summary>
    public static void EnsureSupplierAvailable(BaseSupplier? supplier, long supplierId)
    {
        if (supplier is null || supplier.IsDeleted)
            throw BusinessException.NotFound($"供应商（Id={supplierId}）不存在或已删除，不能用于付款单");
        if (supplier.Status != 1)
            throw BusinessException.RuleConflict(
                $"供应商「{supplier.SupplierName}」已停用：停用供应商不能用于新付款单或修改付款单供应商");
    }

    // ==================== 2. 身份 / 菜单 / 客户数据范围（fail closed） ====================

    /// <summary>
    /// 校验当前账号的<strong>实时启用身份</strong>与付款单（payment）菜单授权：无身份 / 账号不存在或被删除 /
    /// 账号被禁用 / 无角色 / 无授权一律拒绝（fail closed，绝不退化为匿名或管理员）。
    /// <para>每次调用都重新查询 <c>SysUsers</c> 与「角色 → 菜单」授权（无缓存），因此账号停用或授权撤销后
    /// 下一次请求立即收敛；付款单的修改 / 提交 / 审核 / 取消 / 删除会在同一事务内、取得付款单行锁之后再调用本方法，
    /// 用实时身份与授权覆盖「先读后写」窗口。</para>
    /// </summary>
    public static async Task EnsureMenuAuthorizedAsync(IErpDbContext db, long? userId)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (userId is null or <= 0)
            throw new BusinessException("请先登录后再访问付款单", ErrorCodes.Unauthorized);

        var user = await db.SysUsers.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == userId.Value && !u.IsDeleted);
        if (user is null)
            throw new BusinessException("登录账号不存在或已删除，禁止操作付款单", ErrorCodes.Unauthorized);
        if (user.Status != UserStatus.Enabled)
            throw new BusinessException("登录账号已禁用，禁止操作付款单（fail closed）", ErrorCodes.Forbidden);

        var menuCodes = await CustomerReceivableReconciliationService.LoadAuthorizedMenuCodesAsync(db, userId.Value);
        if (!menuCodes.Contains(RequiredMenuCode, StringComparer.OrdinalIgnoreCase))
            throw new BusinessException(
                $"当前账号没有「{RequiredMenuText}」（{RequiredMenuCode}）模块授权",
                ErrorCodes.Forbidden);
    }

    /// <summary>
    /// 三重授权校验：身份（缺失 / 非正整数 → 未认证）→ 付款单菜单授权 → 客户数据范围。
    /// <paramref name="customerId"/> 为 null（付款单未关联货款申请单）时仅特权账号可通过，
    /// 受限制账号 fail closed（绝不泄露范围外付款单）。每次请求都重新解析，撤销授权 / 客户分配变更后立即收敛。
    /// </summary>
    public static async Task EnsureAuthorizedAsync(IErpDbContext db, long? userId, long? customerId)
    {
        await EnsureMenuAuthorizedAsync(db, userId);
        var scope = await SalespersonDataScopeService.ResolveAsync(db, userId!.Value);

        if (customerId is null or <= 0)
        {
            if (!scope.IsPrivileged)
                throw new BusinessException(
                    "该付款单未关联客户（未关联货款申请单），当前账号的数据范围无法覆盖（fail closed，不泄露范围外付款单）",
                    ErrorCodes.Forbidden);
            return;
        }

        EnsureCustomerInScope(scope, customerId.Value);
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

    // ==================== 3. 货款申请单来源解析（按 Id，绝不从标签猜测） ====================

    /// <summary>
    /// 解析付款单引用的货款申请单（可空）：未提供 Id 返回 null（保留有效的历史未关联付款场景）；
    /// 提供 Id 时必须解析到既有、未删除且未取消的申请单，并校验币种一致、付款金额不超过申请金额。
    /// <para>绝不按申请单号文本、金额或相似度猜测来源：Id 不存在 / 已删除即按「来源不存在」拒绝（dangling / forged）。</para>
    /// </summary>
    public static async Task<FinancePaymentApply?> ResolvePaymentApplyAsync(
        IErpDbContext db, long? paymentApplyId, string currency, decimal paymentAmount)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (paymentApplyId is null or <= 0) return null;

        var apply = await db.FinancePaymentApplies.AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == paymentApplyId.Value && !a.IsDeleted)
            ?? throw BusinessException.NotFound(
                $"货款申请单（Id={paymentApplyId.Value}）不存在或已删除，不能作为付款来源"
                + "（系统不按申请单号文本、金额或相似度猜测来源）");

        if (apply.Status == DocumentStatus.Cancelled)
            throw BusinessException.RuleConflict(
                $"货款申请单「{apply.ApplyNo}」已取消，不能作为付款来源");

        var applyCurrency = CurrencyAmountRules.NormalizeCurrency(apply.Currency.ToString());
        if (!string.Equals(applyCurrency, currency, StringComparison.Ordinal))
            throw BusinessException.RuleConflict(
                $"货款申请单「{apply.ApplyNo}」的币种 {applyCurrency} 与付款单币种 {currency} 不一致，"
                + "不能引用（不做汇率换算）");

        var applyAmount = CurrencyAmountRules.RoundAmount(apply.Amount, applyCurrency);
        if (paymentAmount > applyAmount)
            throw BusinessException.RuleConflict(
                $"付款金额 {paymentAmount} {currency} 超过货款申请单「{apply.ApplyNo}」的申请金额 "
                + $"{applyAmount} {applyCurrency}：不能引用（系统不做超额付款、不自动调整差额）");

        return apply;
    }

    /// <summary>
    /// 用既有规则复核<strong>已持久化</strong>付款单的金额 / 币种 / 来源一致性（只读，不写库、不新增审批要求）：
    /// 币种必须在既有系统币种口径内，金额按币种精度取整后必须大于 0，关联货款申请单（若存在）必须仍可按 Id 解析到
    /// 既有、未删除且未取消的申请单且币种一致、付款金额不超过申请金额；未关联申请单的历史付款场景保持不变。
    /// <para>提交 / 审核会在付款单行锁与同一事务内调用本方法，防止在锁外被改写的非法持久化数据被流转放行；
    /// 不使用任何新阈值、新审批人或新单据状态。</para>
    /// </summary>
    public static async Task EnsurePersistedPaymentConsistentAsync(IErpDbContext db, FinancePayment payment)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(payment);

        var currency = NormalizePaymentCurrency(payment.Currency);
        var amount = NormalizePaymentAmount(payment.Amount, currency);
        await ResolvePaymentApplyAsync(db, payment.PaymentApplyId, currency, amount);
    }

    /// <summary>付款单当前派生的客户 Id：未关联货款申请单时为 null；关联申请单已删除时也为 null（fail closed）。</summary>
    public static async Task<long?> ResolvePaymentCustomerAsync(IErpDbContext db, FinancePayment payment)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(payment);
        if (payment.PaymentApplyId is null or <= 0) return null;

        return await db.FinancePaymentApplies.AsNoTracking()
            .Where(a => a.Id == payment.PaymentApplyId.Value && !a.IsDeleted)
            .Select(a => (long?)a.CustomerId)
            .FirstOrDefaultAsync();
    }

    /// <summary>
    /// 按客户数据范围过滤付款单查询（仅列表用）：特权账号不过滤；受限制账号只保留
    /// 关联到其被分配客户的货款申请单的付款单（未关联客户的付款单对受限制账号不可见，fail closed）。
    /// </summary>
    public static async Task<IQueryable<FinancePayment>> ApplyCustomerScopeAsync(
        IErpDbContext db, IQueryable<FinancePayment> source, SalespersonDataScope scope)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(scope);
        if (scope.AllowedCustomerIds is null) return source;

        var allowed = scope.AllowedCustomerIds.ToList();
        var applyIds = await db.FinancePaymentApplies.AsNoTracking()
            .Where(a => !a.IsDeleted && allowed.Contains(a.CustomerId))
            .Select(a => a.Id)
            .ToListAsync();

        return source.Where(p => p.PaymentApplyId != null && applyIds.Contains(p.PaymentApplyId.Value));
    }

    // ==================== 4. 有效付款引用证据判定（只读、有界） ====================

    /// <summary>
    /// 付款单是否存在任一<strong>有效</strong>付款引用证据：ERP-049「付款单 → 采购订单」或
    /// ERP-066「付款单 → 供应商采购发票」中，存在未删除且状态为有效（未作废）的引用行。
    /// 作废行 / 删除行保留历史但不再构成有效证据，绝不按字符串或金额猜测链接。
    /// </summary>
    public static async Task<bool> HasActiveAllocationAsync(IErpDbContext db, long paymentId)
    {
        ArgumentNullException.ThrowIfNull(db);

        var order = await db.SupplierPaymentAllocations.AsNoTracking()
            .AnyAsync(a => !a.IsDeleted
                           && a.PaymentId == paymentId
                           && a.Status == SupplierPaymentAllocationRules.StatusActive);
        if (order) return true;

        return await db.SupplierPaymentInvoiceAllocations.AsNoTracking()
            .AnyAsync(a => !a.IsDeleted
                           && a.PaymentId == paymentId
                           && a.Status == SupplierPaymentInvoiceAllocationRules.StatusActive);
    }

    /// <summary>
    /// 拒绝存在有效付款引用证据的付款单生命周期动作（取消 / 删除 / 修改供应商 / 币种 / 金额）。
    /// 要解除限制必须先走既有显式作废服务（作废保留原始证据，不物理删除、不静默替换）。
    /// </summary>
    public static async Task EnsureNoActiveAllocationAsync(IErpDbContext db, long paymentId, string action)
    {
        if (await HasActiveAllocationAsync(db, paymentId))
            throw BusinessException.RuleConflict(
                $"付款单存在有效的付款引用证据（「付款单 → 采购订单」或「付款单 → 供应商采购发票」），不能{action}："
                + "请先通过既有的作废服务显式作废相关引用行（作废保留历史，不物理删除、不静默改写证据）");
    }

    // ==================== 5. 付款单唯一分摊额度（两套证据共享、同币种） ====================

    /// <summary>
    /// 同一张付款单的唯一、同币种分摊额度快照：把「付款单 → 采购订单」与
    /// 「付款单 → 供应商采购发票」两套<b>有效（未删除、未作废）</b>引用行各自合计，
    /// 由 <see cref="PaymentFunding.CombinedAllocated"/> 给出两套证据共同占用的金额（每个维度各计一次、绝不重复）。
    /// <para>只按同一付款单的持久化有效行派生；作废行 / 删除行保留历史但不占用额度；只有显式作废才释放额度。</para>
    /// </summary>
    public static async Task<PaymentFunding> LoadPaymentFundingAsync(IErpDbContext db, long paymentId)
    {
        ArgumentNullException.ThrowIfNull(db);

        var payment = await db.FinancePayments.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == paymentId && !p.IsDeleted);
        var orders = db.SupplierPaymentAllocations.AsNoTracking()
            .Where(a => !a.IsDeleted && a.PaymentId == paymentId && a.Status == SupplierPaymentAllocationRules.StatusActive);
        var invoices = db.SupplierPaymentInvoiceAllocations.AsNoTracking()
            .Where(a => !a.IsDeleted && a.PaymentId == paymentId && a.Status == SupplierPaymentInvoiceAllocationRules.StatusActive);
        if (payment is null)
        {
            if (await orders.AnyAsync() || await invoices.AnyAsync())
                throw BusinessException.RuleConflict("有效付款引用缺少权威付款单，不能使用资金余额");
        }
        else
        {
            var currency = NormalizePaymentCurrency(payment.Currency);
            if (await orders.AnyAsync(a => a.SupplierId != payment.SupplierId || a.Currency != currency || a.AllocatedAmount <= 0m)
                || await invoices.AnyAsync(a => a.SupplierId != payment.SupplierId || a.Currency != currency || a.AllocatedAmount <= 0m))
                throw BusinessException.RuleConflict("有效付款引用的供应商、币种或金额异常，请先通过授权业务流程处理，不能使用资金余额");
        }

        var order = await db.SupplierPaymentAllocations.AsNoTracking()
            .Where(a => !a.IsDeleted && a.PaymentId == paymentId
                        && a.Status == SupplierPaymentAllocationRules.StatusActive)
            .GroupBy(a => a.PaymentId)
            .Select(g => new { Amount = g.Sum(a => a.AllocatedAmount), Count = g.Count() })
            .FirstOrDefaultAsync();

        var invoice = await db.SupplierPaymentInvoiceAllocations.AsNoTracking()
            .Where(a => !a.IsDeleted && a.PaymentId == paymentId
                        && a.Status == SupplierPaymentInvoiceAllocationRules.StatusActive)
            .GroupBy(a => a.PaymentId)
            .Select(g => new { Amount = g.Sum(a => a.AllocatedAmount), Count = g.Count() })
            .FirstOrDefaultAsync();

        return new PaymentFunding(
            order?.Amount ?? 0m,
            invoice?.Amount ?? 0m,
            order?.Count ?? 0,
            invoice?.Count ?? 0);
    }

    // ==================== 6. 付款单行锁（与引用证据写入 / 生命周期串行化） ====================

    /// <summary>
    /// 关系型后端（SQL Server）判定：内存库等非关系型提供程序没有行锁语义，锁定与事务等价无操作。
    /// 使用 <c>DatabaseFacade.ProviderName</c>（EF Core 基础 API），不依赖关系型扩展。
    /// </summary>
    public static bool IsRelationalProvider(IErpDbContext db)
        => string.Equals(db.Database.ProviderName, "Microsoft.EntityFrameworkCore.SqlServer",
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 对付款单行加更新锁，把同单并发的「付款单生命周期操作」与「两套付款引用证据写入」串行化在同一事务内。
    /// <para>实现（仅用 EF Core 基础 API，不依赖关系型扩展）：在调用方事务内对付款单行发出一条「审计时间戳刷新」的
    /// UPDATE，从而取得排它行锁（X 锁，持有至事务结束），语义等价于 <c>SELECT ... WITH (UPDLOCK, HOLDLOCK)</c>；
    /// <c>UpdatedAt</c> 是技术审计字段、不是引用证据，因此不构成对引用证据的静默改写。
    /// 内存库等非关系型提供程序无行锁语义，直接跳过（事务等价无事务）。</para>
    /// <para><b>锁序（ERP-379，全模块统一）</b>：付款单生命周期动作（修改 / 提交 / 审核 / 取消 / 删除）与两套
    /// 付款引用证据写入都必须先取本锁、再读写引用行（<b>先付款单行、后引用行</b>），绝不反向获取下游锁；
    /// 因此「提交 / 审核 / 取消 / 删除 / 改动供应商 / 币种 / 金额」与「登记引用证据」只能串行执行，
    /// 不会产生孤儿引用、超额度付款或半成品写入。</para>
    /// <para><b>锁序（ERP-380）</b>：只要付款单关联了来源货款申请单，上述动作与证据写入都必须<strong>先取来源申请单行锁
    /// （<see cref="FinancePaymentApplyLifecycleRules.LockApplyRowAsync"/>）、再取本锁</strong>
    /// （<b>先来源申请单行、后付款单行</b>），与来源申请单的商业改动 / 取消 / 删除串行化；
    /// 未关联申请单的历史付款场景保持既有语义（无申请单行锁可加）。</para>
    /// </summary>
    public static async Task LockPaymentRowAsync(IErpDbContext db, long paymentId)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (!IsRelationalProvider(db)) return;

        var payment = await db.FinancePayments
            .FirstOrDefaultAsync(p => p.Id == paymentId && !p.IsDeleted);
        if (payment is null) return;

        payment.UpdatedAt = DateTime.Now;
        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            throw BusinessException.RuleConflict(
                "付款单正在被并发修改，本次操作未生效：请刷新后重试（原始付款单与引用证据均未改变）");
        }
    }
}

/// <summary>
/// 付款单唯一、同币种分摊额度快照。
/// <para><see cref="OrderAllocated"/> 为「付款单 → 采购订单」有效行合计，
/// <see cref="InvoiceAllocated"/> 为「付款单 → 供应商采购发票」有效行合计；
/// <see cref="CombinedAllocated"/> 是两套证据共同占用的金额（每个维度各计一次、绝不重复计算）。</para>
/// </summary>
public readonly record struct PaymentFunding(
    decimal OrderAllocated,
    decimal InvoiceAllocated,
    int OrderCount,
    int InvoiceCount)
{
    /// <summary>两套证据共同占用的有效引用金额（每个维度各计一次）</summary>
    public decimal CombinedAllocated => OrderAllocated + InvoiceAllocated;
}



