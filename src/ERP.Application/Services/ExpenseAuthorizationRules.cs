using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using System.Linq.Expressions;

namespace ERP.Application.Services;

/// <summary>
/// 费用单（<see cref="FinanceExpense"/>，<c>api/finance/expenses</c>）与装柜费用分摊证据
/// （<c>api/container/expense-allocation-evidence</c>）的实时授权、权威客户范围与写入校验护栏（ERP-385）。
/// <list type="number">
/// <item><b>实时授权</b>：每一个费用 CRUD / 读取 / 列表 / 导出（若存在）/ 传统「拼柜分摊」预览与生成 /
/// ERP-042 分摊批次预览 / 生成 / 台账 / 详情 / 作废 / ERP-060 分摊证据读取路由，在读取任何计数、生成单号或写入任何数据
/// <b>之前</b>都先解析<b>实时启用身份</b>（缺失 / 非法按未认证拒绝，账号不存在 / 已删除按未认证拒绝，禁用按权限不足拒绝）、
/// 既有「费用单」（<c>expense-bill</c>）菜单授权（非特权账号必须显式具备，未映射业务员 fail closed）与权威客户数据范围
/// （复用 ERP-097 <see cref="SalespersonDataScopeService"/>）。</item>
/// <item><b>权威归属</b>：费用单只按<b>持久化 <see cref="FinanceExpense.CustomerId"/></b> 判定，绝不按
/// <see cref="FinanceExpense.RefNo"/> / <see cref="FinanceExpense.CustomerName"/> 等自由文本猜测；受限账号不能读取 / 改写
/// 缺失权威归属（CustomerId 为空）的「无主」费用单，真正不受限（特权）的授权账号保留既有历史访问。</item>
/// <item><b>共享装柜清单与分摊批次</b>：批次的权威归属由其<b>显式</b>
/// <see cref="FinanceExpenseAllocationBatch.LoadingListId"/>（复用 ERP-364 参与方 / 上游客户范围）、
/// <see cref="FinanceExpenseAllocationBatch.SourceExpenseId"/> 来源费用客户与
/// <see cref="FinanceExpenseAllocationLine"/> 逐行生成客户共同证明 —— 任一客户越界即整条拒绝（不通过共享柜泄露他人客户）。</item>
/// <item><b>写入校验</b>：改写任何字段或生成任何行之前，校验拟议 / 已存储客户的完整范围、真实启用客户、受支持币种、
/// 取整后为正的金额与为正的汇率；被拒绝的编辑 / 删除 / 预览 / 生成 / 作废绝不改写原行、状态、审计与任何关联证据
/// （分摊批次只以显式作废（状态 0 + 原因）更正，保留历史）。</item>
/// </list>
/// <para><b>身份来源唯一</b>：请求提交体中的任何字段都不能指定或扩大账号身份 —— 身份只来自已认证请求主体
/// （<c>ClaimTypes.NameIdentifier</c>），由调用方（控制器 / <c>ExpenseRequestAuthorizationFilter</c>）解析后以
/// <see cref="SalespersonDataScope"/> 传入；<c>null</c> 仅表示「未经过 MVC 授权管线的进程内调用」，绝不代表匿名或管理员。</para>
/// <para>边界：本类只做纯判定与有界只读查询；不新增表 / 列 / 菜单 / 权限模型，不伪造任何授权，不记账、不生成
/// 凭证 / 收款 / 付款 / 结算单，也不改写任何历史留痕。</para>
/// </summary>
public static class ExpenseAuthorizationRules
{
    /// <summary>费用单模块复用的既有菜单编码（与 <c>SeedData.Menus</c> / <c>SchemaUpgrader</c> 同源）</summary>
    public const string RequiredMenuCode = "expense-bill";

    /// <summary>费用单模块菜单中文文案（与既有菜单名一致）</summary>
    public const string RequiredMenuText = "费用单";

    /// <summary>金额 / 汇率的既有存储精度（<c>decimal(18,2)</c>），固定 2 位小数、0.5 进位（不使用银行家舍入）</summary>
    public const int AmountDecimals = 2;

    /// <summary>系统支持的币种（与销售订单 / 收款单 / 采购订单币种枚举同源：CNY / USD / EUR / HKD / GBP / JPY）</summary>
    public static readonly string[] SupportedCurrencies = { "CNY", "USD", "EUR", "HKD", "GBP", "JPY" };

    /// <summary>无身份 / 非法身份的拒绝文案</summary>
    public const string UnauthorizedText = "请先登录后再访问费用单";

    /// <summary>账号不存在 / 已删除的拒绝文案</summary>
    public const string UserDeletedText = "登录账号不存在或已删除，禁止访问费用单";

    /// <summary>账号已禁用的拒绝文案</summary>
    public const string UserDisabledText = "登录账号已禁用，禁止访问费用单（fail closed）";

    /// <summary>缺少既有「费用单」菜单授权的拒绝文案</summary>
    public const string MenuDeniedText =
        "当前账号没有「费用单」（expense-bill）模块授权：拒绝访问费用单 / 分摊批次 / 分摊证据"
        + "（fail closed，不返回 / 不修改任何费用数据）";

    /// <summary>受限账号未映射业务员的拒绝文案</summary>
    public const string UnmappedOperatorText =
        "当前账号未映射为业务员（费用单操作员），不能访问费用单（fail closed，不泄露任何范围外单据）";

    /// <summary>受限账号访问缺失权威归属（CustomerId 为空）费用单的拒绝文案</summary>
    public const string UnlinkedExpenseText =
        "该费用单没有可判定的权威客户归属（持久化 CustomerId 缺失）：受限账号拒绝访问（fail closed，不泄露无主单据）";

    /// <summary>客户越范围的拒绝文案</summary>
    public const string OutOfScopeText =
        "当前账号的客户数据范围不包含该费用单 / 分摊批次的客户：拒绝操作（fail closed，不泄露范围外单据）";

    /// <summary>装柜清单无权威归属 / 已删除时作为分摊来源的拒绝文案</summary>
    public const string UnlinkedLoadingListText =
        "分摊批次的装柜清单没有可判定的权威客户归属（不存在 / 已删除 / 无有效参与方且兼容客户字段缺失）："
        + "受限账号拒绝访问（fail closed，不通过共享柜泄露）";

    /// <summary>客户不存在 / 已删除 / 已停用的拒绝文案</summary>
    public const string CustomerUnavailableText = "归属客户不存在、已删除或已停用，不能作为费用单客户";

    /// <summary>不支持币种的拒绝文案</summary>
    public const string UnsupportedCurrencyText =
        "币种不受支持：只允许 CNY / USD / EUR / HKD / GBP / JPY（不做汇率换算，也不接受未知币种）";

    /// <summary>金额非正的拒绝文案</summary>
    public const string NonPositiveAmountText = "费用金额按既有存储精度（2 位小数）取整后必须大于 0";

    /// <summary>汇率非正的拒绝文案</summary>
    public const string InvalidRateText = "汇率按既有存储精度（2 位小数）取整后必须大于 0";

    /// <summary>授权与数据范围口径文案（接口 / 文档同源）</summary>
    public const string RuleText =
        "费用单（CRUD / 读取 / 列表 / 导出（若存在）/ 传统分摊 / ERP-042 分摊批次 / ERP-060 分摊证据）在读取任何计数、生成单号或写入之前，"
        + "都会重新校验实时启用身份（缺失 / 非法 / 已删除按未认证，禁用按权限不足）、既有「费用单」（expense-bill）菜单授权与权威客户数据范围；"
        + "费用单只按持久化 CustomerId 判定，受限账号不能访问缺失权威归属的无主费用单，特权账号保留历史访问；"
        + "共享装柜清单与分摊批次要求显式 LoadingListId 的每一个有效参与方 / 上游客户、SourceExpenseId 来源费用客户与逐行生成客户全部在范围内；"
        + "写入前校验真实启用客户、受支持币种、取整后为正的金额与为正的汇率；身份只来自已认证请求主体，客户端提交体不能指定或扩大范围。";

    /// <summary>边界文案（不新增权限 / 表列，不记账，不改写历史证据）</summary>
    public const string BoundaryText =
        "本护栏只保护费用单与分摊批次 / 分摊证据的授权、范围与写入校验：不新增菜单 / 角色 / 用户授权或表结构，也不把空身份当作管理员；"
        + "不写库存 / 资金 / 会计凭证，不产生收款、付款、核销、分摊入账或对账结论；"
        + "被拒绝的编辑 / 删除 / 预览 / 生成 / 作废不改写原费用行、批次状态、审计时间戳与任何关联证据；"
        + "分摊更正仍走 ERP-042 的显式作废（状态 0 + 原因），历史留痕保持不变。";

    // ==================== 1. 币种 / 金额 / 汇率纯校验 ====================

    /// <summary>币种规范化（去空白 + 大写；空值按 CNY）并校验必须落在受支持币种内（未知币种一律拒绝）</summary>
    public static string NormalizeCurrencyStrict(string? currency)
    {
        var value = CurrencyAmountRules.NormalizeCurrency(currency);
        if (!SupportedCurrencies.Contains(value, StringComparer.Ordinal))
            throw BusinessException.InvalidParameter(
                $"{UnsupportedCurrencyText}（收到「{(currency ?? string.Empty).Trim()}」）");
        return value;
    }

    /// <summary>金额校验：按既有存储精度（2 位小数，0.5 进位）取整后必须大于 0；返回取整后的值</summary>
    public static decimal NormalizeAmount(decimal amount)
    {
        var rounded = Math.Round(amount, AmountDecimals, MidpointRounding.AwayFromZero);
        if (rounded <= 0m)
            throw BusinessException.InvalidParameter($"{NonPositiveAmountText}：当前值 {amount} 取整后为 {rounded}");
        return rounded;
    }

    /// <summary>汇率校验：按既有存储精度（2 位小数，0.5 进位）取整后必须大于 0；返回取整后的值</summary>
    public static decimal NormalizeRate(decimal exchangeRate)
    {
        var rounded = Math.Round(exchangeRate, AmountDecimals, MidpointRounding.AwayFromZero);
        if (rounded <= 0m)
            throw BusinessException.InvalidParameter($"{InvalidRateText}：当前值 {exchangeRate} 取整后为 {rounded}");
        return rounded;
    }

    // ==================== 2. 身份 / 账号状态 / 菜单授权（fail closed） ====================

    /// <summary>
    /// 身份 / 账号状态 / 既有「费用单」菜单授权三重实时校验（fail closed），返回本次请求的权威客户数据范围：
    /// 缺失 / 非法身份按未认证拒绝，账号不存在 / 已删除按未认证拒绝，禁用按权限不足拒绝，
    /// 非特权账号缺少既有 <c>expense-bill</c> 菜单授权或未映射业务员按权限不足拒绝。
    /// 每次调用都重新查询（无缓存），账号停用或授权撤销后下一次请求立即收敛；特权账号保留既有全部访问。
    /// </summary>
    public static async Task<SalespersonDataScope> EnsureMenuAuthorizedAsync(
        IErpDbContext db, long? userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ct.ThrowIfCancellationRequested();

        if (userId is null or <= 0)
            throw new BusinessException(UnauthorizedText, ErrorCodes.Unauthorized);

        var user = await db.SysUsers.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == userId.Value && !u.IsDeleted, ct);
        if (user is null)
            throw new BusinessException(UserDeletedText, ErrorCodes.Unauthorized);
        if (user.Status != UserStatus.Enabled)
            throw new BusinessException(UserDisabledText, ErrorCodes.Forbidden);

        var scope = await SalespersonDataScopeService.ResolveAsync(db, userId.Value);

        if (!scope.IsPrivileged)
        {
            var menuCodes = await CustomerReceivableReconciliationService
                .LoadAuthorizedMenuCodesAsync(db, userId.Value);
            if (!menuCodes.Contains(RequiredMenuCode, StringComparer.OrdinalIgnoreCase))
                throw new BusinessException(MenuDeniedText, ErrorCodes.Forbidden);

            if (scope.SalesmanId is null or <= 0)
                throw new BusinessException(UnmappedOperatorText, ErrorCodes.Forbidden);
        }

        return scope;
    }

    /// <summary>受限账号按持久化客户 Id 的硬范围边界：无权威归属（缺失 / &lt;= 0）与越界一律 fail closed</summary>
    public static void EnsureCustomerInScope(SalespersonDataScope? scope, long? customerId)
    {
        if (scope is null || scope.AllowedCustomerIds is null) return;

        if (customerId is null or <= 0)
            throw new BusinessException(UnlinkedExpenseText, ErrorCodes.Forbidden);
        if (!scope.AllowsCustomer(customerId.Value))
            throw new BusinessException(OutOfScopeText, ErrorCodes.Forbidden);
    }

    /// <summary>
    /// 按 Id 精确解析权威客户：必须真实存在、未删除且启用，否则一律拒绝（绝不按自由文本猜测客户）。
    /// </summary>
    public static async Task<BaseCustomer> EnsureCustomerAvailableAsync(
        IErpDbContext db, long customerId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (customerId <= 0)
            throw BusinessException.InvalidParameter(CustomerUnavailableText);

        var customer = await db.BaseCustomers.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == customerId && !c.IsDeleted, ct);
        if (customer is null || customer.Status != ContainerLoadingParticipantRules.ActiveStatus)
            throw BusinessException.InvalidParameter($"{CustomerUnavailableText}（客户 Id={customerId}）");
        return customer;
    }

    // ==================== 3. 费用单写入 / 读取范围校验 ====================

    /// <summary>已存储费用单的范围校验（读取 / 详情 / 修改 / 删除之前调用；特权账号保留历史访问）</summary>
    public static void EnsureStoredExpenseScopeAllowed(SalespersonDataScope? scope, FinanceExpense expense)
    {
        ArgumentNullException.ThrowIfNull(expense);
        EnsureCustomerInScope(scope, expense.CustomerId);
    }

    /// <summary>
    /// 拟议费用单（新增 / 修改 / 传统分摊生成）的完整校验：客户范围 → 真实启用客户 → 受支持币种 →
    /// 取整后为正的金额 → 为正的汇率；任一不满足即在改写任何字段或生成任何行之前拒绝。
    /// <para>受限账号不能提交无权威客户归属（CustomerId 为空）的费用单；特权账号的柜级 / 客户级既有口径保持不变。</para>
    /// </summary>
    public static async Task EnsureProposedExpenseValidAsync(
        IErpDbContext db, SalespersonDataScope? scope, FinanceExpense proposed, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(proposed);

        EnsureCustomerInScope(scope, proposed.CustomerId);
        if (proposed.CustomerId is > 0)
            await EnsureCustomerAvailableAsync(db, proposed.CustomerId.Value, ct);

        NormalizeCurrencyStrict(proposed.Currency);
        NormalizeAmount(proposed.Amount);
        NormalizeRate(proposed.ExchangeRate);
    }

    /// <summary>
    /// 多个拟议客户的范围与可用性校验（传统「按客户分摊并生成费用单」与批次生成前的完整范围复核）：
    /// 每一个客户都必须在当前账号范围内且真实启用；任一越界 / 不可用即整体拒绝（不生成任何行）。
    /// </summary>
    public static async Task EnsureCustomersValidAsync(
        IErpDbContext db, SalespersonDataScope? scope, IEnumerable<long> customerIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(customerIds);

        foreach (var id in customerIds.Where(id => id > 0).Distinct())
        {
            EnsureCustomerInScope(scope, id);
            await EnsureCustomerAvailableAsync(db, id, ct);
        }
    }

    // ==================== 4. SQL 侧范围下推（计数 / 分页之前） ====================

    /// <summary>
    /// 费用单范围谓词：<c>null</c> = 进程内调用（不额外限制）；特权账号不过滤；受限账号只保留持久化
    /// <see cref="FinanceExpense.CustomerId"/> 落在范围内且非空的费用行（无权威归属的无主行一律不返回）。
    /// </summary>
    public static Expression<Func<FinanceExpense, bool>>? ExpenseScopeFilter(SalespersonDataScope? scope)
    {
        if (scope is null || scope.AllowedCustomerIds is null) return null;

        var allowed = scope.AllowedCustomerIds.ToList();
        return expense => expense.CustomerId.HasValue && allowed.Contains(expense.CustomerId.Value);
    }

    /// <summary>费用单范围下推（<see cref="ExpenseScopeFilter"/> 的 IQueryable 便捷重载）</summary>
    public static IQueryable<FinanceExpense> ApplyExpenseScope(
        IQueryable<FinanceExpense> source, SalespersonDataScope? scope)
    {
        ArgumentNullException.ThrowIfNull(source);
        var filter = ExpenseScopeFilter(scope);
        return filter is null ? source : source.Where(filter);
    }

    /// <summary>
    /// 分摊批次范围谓词（工作台 / 批次台账共用，在计数与分页之前下推）：受限账号只保留满足下列全部条件的批次 ——
    /// ① 显式 <see cref="FinanceExpenseAllocationBatch.LoadingListId"/> 的装柜清单通过 ERP-364 权威范围
    /// （无范围外有效参与方 / 上游客户，且有有效参与方或兼容客户字段在范围内）；② 没有任何范围外的逐行生成客户；
    /// ③ <see cref="FinanceExpenseAllocationBatch.SourceExpenseId"/> 来源费用没有范围外的归属客户。
    /// 无法证明归属的行一律不返回，绝不「先查全量再内存过滤」。
    /// </summary>
    public static Expression<Func<FinanceExpenseAllocationBatch, bool>>? BatchScopeFilter(
        IErpDbContext db, SalespersonDataScope? scope)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (scope is null || scope.AllowedCustomerIds is null) return null;

        var allowed = scope.AllowedCustomerIds.ToList();
        var scopedListIds = LoadingListAuthorizationRules
            .ApplyScope(db.ContainerLoadingLists.AsNoTracking(), db, scope)
            .Select(l => l.Id);

        return batch =>
            scopedListIds.Contains(batch.LoadingListId)
            && !db.FinanceExpenseAllocationLines.Any(line =>
                !line.IsDeleted && line.BatchId == batch.Id && !allowed.Contains(line.CustomerId))
            && !db.FinanceExpenses.Any(expense =>
                expense.Id == batch.SourceExpenseId
                && expense.CustomerId.HasValue
                && !allowed.Contains(expense.CustomerId.Value));
    }

    /// <summary>分摊批次范围下推（<see cref="BatchScopeFilter"/> 的 IQueryable 便捷重载）</summary>
    public static IQueryable<FinanceExpenseAllocationBatch> ApplyBatchScope(
        IQueryable<FinanceExpenseAllocationBatch> source, IErpDbContext db, SalespersonDataScope? scope)
    {
        ArgumentNullException.ThrowIfNull(source);
        var filter = BatchScopeFilter(db, scope);
        return filter is null ? source : source.Where(filter);
    }

    // ==================== 5. 装柜清单 / 批次 / 结算单范围校验 ====================

    /// <summary>
    /// 装柜清单范围校验（分摊上下文 / 预览 / 生成 / 证据读取之前）：显式 Id 解析既有未删除清单，
    /// 受限账号按 ERP-364 口径复核每一个有效参与方客户与显式上游客户。
    /// </summary>
    public static async Task<ContainerLoadingList> EnsureLoadingListScopeAllowedAsync(
        IErpDbContext db, SalespersonDataScope? scope, long loadingListId, CancellationToken ct = default)
    {
        var loadingList = await ContainerLoadingParticipantService.EnsureLoadingListAsync(db, loadingListId);
        if (scope is not null && scope.AllowedCustomerIds is not null)
            await LoadingListAuthorizationRules.EnsureStoredScopeAllowedAsync(db, scope, loadingList, ct);
        return loadingList;
    }

    /// <summary>
    /// 解析并复核装柜清单范围（只读派生场景，如分摊证据）：清单不存在 / 已删除时对受限账号 fail closed，
    /// 特权账号返回 <c>null</c> 以保留历史证据可读（照实标注不可用），绝不按柜号等自由文本推断。
    /// </summary>
    public static async Task<ContainerLoadingList?> ResolveLoadingListScopeAsync(
        IErpDbContext db, SalespersonDataScope? scope, long loadingListId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (loadingListId <= 0) return null;

        var restricted = scope is not null && scope.AllowedCustomerIds is not null;
        var loadingList = await db.ContainerLoadingLists.AsNoTracking()
            .FirstOrDefaultAsync(l => l.Id == loadingListId && !l.IsDeleted, ct);
        if (loadingList is null)
        {
            if (restricted)
                throw new BusinessException(UnlinkedLoadingListText, ErrorCodes.Forbidden);
            return null;
        }

        if (restricted)
            await LoadingListAuthorizationRules.EnsureStoredScopeAllowedAsync(db, scope!, loadingList, ct);
        return loadingList;
    }

    /// <summary>
    /// 装柜清单分摊证据的客户范围校验：受限账号读取某清单的分摊证据之前，必须对该清单上
    /// <b>每一个被分摊行显式引用的客户</b>都有数据范围权限（多客户共享柜 fail closed，不泄露其他客户）。
    /// </summary>
    public static async Task EnsureEvidenceCustomersInScopeAsync(
        IErpDbContext db, SalespersonDataScope? scope, long loadingListId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (scope is null || scope.AllowedCustomerIds is null) return;
        if (loadingListId <= 0) return;

        var batchIds = await db.FinanceExpenseAllocationBatches.AsNoTracking()
            .Where(b => b.LoadingListId == loadingListId)
            .Select(b => b.Id)
            .ToListAsync(ct);
        if (batchIds.Count == 0) return;

        var customerIds = await db.FinanceExpenseAllocationLines.AsNoTracking()
            .Where(l => batchIds.Contains(l.BatchId))
            .Select(l => l.CustomerId)
            .Distinct()
            .ToListAsync(ct);

        if (customerIds.Any(id => !scope.AllowsCustomer(id)))
            throw new BusinessException(OutOfScopeText, ErrorCodes.Forbidden);
    }

    /// <summary>
    /// 已存储分摊批次的完整范围校验（台账详情 / 作废之前）：受限账号要求显式装柜清单（参与方 + 上游客户）可证明、
    /// 来源费用（若已登记归属客户）在范围内、且逐行生成客户全部在范围内；任一不满足即拒绝，且不修改任何行 / 状态 / 审计。
    /// </summary>
    public static async Task EnsureBatchScopeAllowedAsync(
        IErpDbContext db, SalespersonDataScope? scope, FinanceExpenseAllocationBatch batch,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(batch);
        if (scope is null || scope.AllowedCustomerIds is null) return;

        // ① 显式 LoadingListId 的权威归属：清单不存在 / 已删除即 fail closed（不通过共享柜泄露）。
        var loadingList = await db.ContainerLoadingLists.AsNoTracking()
            .FirstOrDefaultAsync(l => l.Id == batch.LoadingListId && !l.IsDeleted, ct);
        if (loadingList is null)
            throw new BusinessException(UnlinkedLoadingListText, ErrorCodes.Forbidden);
        await LoadingListAuthorizationRules.EnsureStoredScopeAllowedAsync(db, scope, loadingList, ct);

        // ② SourceExpenseId 来源费用的归属客户（若已登记）必须落在范围内；无归属登记时以装柜清单为准。
        if (batch.SourceExpenseId > 0)
        {
            var sourceCustomerId = await db.FinanceExpenses.AsNoTracking()
                .Where(e => e.Id == batch.SourceExpenseId)
                .Select(e => e.CustomerId)
                .FirstOrDefaultAsync(ct);
            if (sourceCustomerId is > 0 && !scope.AllowsCustomer(sourceCustomerId.Value))
                throw new BusinessException(OutOfScopeText, ErrorCodes.Forbidden);
        }

        // ③ 逐行生成客户（分摊行是唯一权威登记册）全部必须在范围内。
        var lineCustomers = await db.FinanceExpenseAllocationLines.AsNoTracking()
            .Where(l => !l.IsDeleted && l.BatchId == batch.Id)
            .Select(l => l.CustomerId)
            .Distinct()
            .ToListAsync(ct);
        if (lineCustomers.Any(id => !scope.AllowsCustomer(id)))
            throw new BusinessException(OutOfScopeText, ErrorCodes.Forbidden);
    }

    /// <summary>
    /// 已存储装柜结算单的范围校验（分摊证据读取之前）：结算客户必须在范围内；显式关联的装柜清单仍可解析时，
    /// 其参与方 / 上游客户范围与链路上<b>每一个被分摊行显式引用的客户</b>也必须同时满足（共享柜 fail closed）。
    /// </summary>
    public static async Task EnsureSettlementScopeAllowedAsync(
        IErpDbContext db, SalespersonDataScope? scope, FinanceContainerSettlement settlement,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(settlement);
        if (scope is null || scope.AllowedCustomerIds is null) return;

        if (settlement.CustomerId <= 0)
            throw new BusinessException(UnlinkedExpenseText, ErrorCodes.Forbidden);
        if (!scope.AllowsCustomer(settlement.CustomerId))
            throw new BusinessException(OutOfScopeText, ErrorCodes.Forbidden);

        if (settlement.LoadingListId is not > 0) return;

        var loadingList = await db.ContainerLoadingLists.AsNoTracking()
            .FirstOrDefaultAsync(l => l.Id == settlement.LoadingListId.Value && !l.IsDeleted, ct);
        if (loadingList is null)
            throw new BusinessException(UnlinkedLoadingListText, ErrorCodes.Forbidden);

        await LoadingListAuthorizationRules.EnsureStoredScopeAllowedAsync(db, scope, loadingList, ct);
        await EnsureEvidenceCustomersInScopeAsync(db, scope, settlement.LoadingListId.Value, ct);
    }
}
