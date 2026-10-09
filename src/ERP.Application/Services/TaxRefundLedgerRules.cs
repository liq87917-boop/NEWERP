using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using System.Linq.Expressions;

namespace ERP.Application.Services;

/// <summary>
/// 出口退税台账（<see cref="BaseTaxRefund"/>，<c>api/base/tax-refunds</c>）实时授权、权威客户范围与
/// 金额 / 税率 / 期间 / 日期写入护栏（ERP-442，阶段 3 财务台账收口）。
/// <list type="number">
/// <item><b>实时授权</b>：分页 / 全部 / 详情 / 新增 / 修改 / 删除 / 批量删除，在读取任何计数或写入任何数据
/// <b>之前</b>都先解析<b>实时启用身份</b>（缺失 / 非法按未认证拒绝，账号不存在 / 已删除按未认证拒绝，
/// 禁用按权限不足拒绝）、既有「出口退税台账」（<c>tax-refund</c>）功能菜单授权（非特权账号必须显式具备，
/// 未映射业务员 fail closed）与权威客户数据范围（复用 ERP-097 <see cref="SalespersonDataScopeService"/>）。</item>
/// <item><b>权威归属</b>：台账只按<b>持久化 <see cref="BaseTaxRefund.CustomerId"/></b> 判定，绝不按
/// <see cref="BaseTaxRefund.CustomerName"/> / <see cref="BaseTaxRefund.RefundNo"/> / <see cref="BaseTaxRefund.DeclareNo"/>
/// 等自由文本推断；受限账号不能读取 / 改写缺失权威归属（<c>CustomerId</c> 为空）的历史无主行，真正不受限
/// （特权）的授权账号保留既有历史访问。</item>
/// <item><b>列表 / 计数下推</b>：范围在 <c>Count</c> 与分页之前下推到 SQL（<see cref="ScopeFilter"/>），
/// 绝不「先查全量再内存过滤」，计数与当前页绝不泄露范围外或范围外无主行。</item>
/// <item><b>写入校验</b>：新增 / 修改在任何字段落库之前校验 <see cref="BaseTaxRefund.ExportAmount"/> /
/// <see cref="BaseTaxRefund.RefundableAmount"/> / <see cref="BaseTaxRefund.RefundedAmount"/> 非负、
/// <see cref="BaseTaxRefund.RefundRate"/> 落在 0..100、已退税不超过可退税、币种与退税期间非空且有界、
/// 申报 / 到账日期内部一致；一律<b>拒绝而不静默截断 / 回填</b>。被拒绝的写入不落任何台账行。</item>
/// </list>
/// <para><b>身份来源唯一</b>：请求提交体中的任何字段都不能指定或扩大账号身份 —— 身份只来自已认证请求主体
/// （<c>ClaimTypes.NameIdentifier</c>），由调用方（<see cref="ERP.Api.Controllers.TaxRefundController"/>）解析并
/// 传入；<c>null</c> 表示无可用身份，一律 fail closed，绝不代表匿名或管理员。</para>
/// <para>边界：本类只做纯判定与有界只读查询；不新增表 / 列 / 菜单 / 权限模型，不伪造任何授权，也不改变既有
/// 只读退税汇总报表（<c>ReportController.TaxRefundSummary</c>）的聚合语义。</para>
/// </summary>
public static class TaxRefundLedgerRules
{
    /// <summary>出口退税台账模块复用的既有菜单编码（与 <c>SchemaUpgrader</c> 种子同源：<c>MenuCode N'tax-refund'</c>）</summary>
    public const string RequiredMenuCode = "tax-refund";

    /// <summary>出口退税台账模块菜单中文文案（与既有菜单名一致）</summary>
    public const string RequiredMenuText = "出口退税台账";

    /// <summary>币种字段既有存储上界（与 <see cref="BaseTaxRefund.Currency"/> <c>MaxLength(20)</c> 同源）</summary>
    public const int MaxCurrencyLength = 20;

    /// <summary>退税期间字段既有存储上界（与 <see cref="BaseTaxRefund.RefundPeriod"/> <c>MaxLength(20)</c> 同源）</summary>
    public const int MaxRefundPeriodLength = 20;

    /// <summary>可接受的最早日期（防止 <c>default(DateTime)</c> / 极小值被当作「已填」日期）</summary>
    public static readonly DateTime MinPlausibleDate = new(1900, 1, 1);

    /// <summary>无身份 / 非法身份的拒绝文案</summary>
    public const string UnauthorizedText = "请先登录后再访问出口退税台账";

    /// <summary>账号不存在 / 已删除的拒绝文案</summary>
    public const string UserDeletedText = "登录账号不存在或已删除，禁止访问出口退税台账";

    /// <summary>账号已禁用的拒绝文案</summary>
    public const string UserDisabledText = "登录账号已禁用，禁止访问出口退税台账（fail closed）";

    /// <summary>缺少既有「出口退税台账」菜单授权的拒绝文案</summary>
    public const string MenuDeniedText =
        "当前账号没有「出口退税台账」（tax-refund）模块授权：拒绝访问出口退税台账"
        + "（fail closed，不返回 / 不修改任何退税台账数据）";

    /// <summary>受限账号未映射业务员的拒绝文案</summary>
    public const string UnmappedOperatorText =
        "当前账号未映射为业务员（出口退税台账操作员），不能访问出口退税台账（fail closed，不泄露任何范围外台账行）";

    /// <summary>受限账号访问缺失权威归属（CustomerId 为空）历史无主行的拒绝文案</summary>
    public const string UnlinkedLedgerText =
        "该出口退税台账行没有可判定的权威客户归属（持久化 CustomerId 缺失）：受限账号拒绝访问（fail closed，不泄露无主行）";

    /// <summary>台账客户越范围的拒绝文案</summary>
    public const string OutOfScopeText =
        "当前账号的客户数据范围不包含该出口退税台账行的归属客户：拒绝操作（fail closed，不泄露范围外台账行）";

    /// <summary>客户不存在 / 已删除 / 已停用的拒绝文案</summary>
    public const string CustomerUnavailableText = "归属客户不存在、已删除或已停用，不能作为出口退税台账客户";

    /// <summary>出口金额为负的拒绝文案</summary>
    public const string NegativeExportAmountText = "出口金额（ExportAmount）不能为负数";

    /// <summary>可退税额为负的拒绝文案</summary>
    public const string NegativeRefundableAmountText = "可退税额（RefundableAmount）不能为负数";

    /// <summary>已退税额为负的拒绝文案</summary>
    public const string NegativeRefundedAmountText = "已退税额（RefundedAmount）不能为负数";

    /// <summary>退税率越界的拒绝文案</summary>
    public const string InvalidRefundRateText = "退税率（RefundRate）必须落在 0..100（百分比）区间内";

    /// <summary>已退税超过可退税的拒绝文案</summary>
    public const string RefundedExceedsRefundableText =
        "已退税额（RefundedAmount）不能超过可退税额（RefundableAmount）";

    /// <summary>币种为空的拒绝文案</summary>
    public const string CurrencyRequiredText = "币种（Currency）不能为空";

    /// <summary>币种超长的拒绝文案</summary>
    public const string CurrencyTooLongText = "币种（Currency）长度不能超过 20 个字符";

    /// <summary>退税期间为空的拒绝文案</summary>
    public const string RefundPeriodRequiredText = "退税所属期间（RefundPeriod）不能为空";

    /// <summary>退税期间超长的拒绝文案</summary>
    public const string RefundPeriodTooLongText = "退税所属期间（RefundPeriod）长度不能超过 20 个字符";

    /// <summary>申报 / 到账日期内部不一致的拒绝文案</summary>
    public const string InconsistentDatesText =
        "申报日期（DeclareDate）与退税到账日期（RefundDate）内部不一致：到账日期不能早于申报日期，且都必须是合理日期";

    /// <summary>授权与数据范围口径文案（接口 / 文档同源）</summary>
    public const string RuleText =
        "出口退税台账（分页 / 全部 / 详情 / 新增 / 修改 / 删除 / 批量删除）在读取任何计数或写入之前，"
        + "都会重新校验实时启用身份（缺失 / 非法 / 账号不存在 / 已删除按未认证，禁用按权限不足）、"
        + "既有「出口退税台账」（tax-refund）菜单授权与 ERP-097 权威客户数据范围；"
        + "台账只按持久化 CustomerId 判定归属，受限账号不得访问无主行与范围外行，特权账号保留历史访问；"
        + "新增 / 修改前校验金额非负、退税率 0..100、已退税不超过可退税、币种与期间非空有界、申报 / 到账日期一致，"
        + "拒绝而不静默截断或回填；身份只来自已认证请求主体，客户端提交体不能指定或扩大范围。";

    /// <summary>边界文案（不新增权限 / 表列，不改变只读报表口径）</summary>
    public const string BoundaryText =
        "本护栏只保护出口退税台账的授权、范围与写入校验：不新增菜单 / 角色 / 用户授权或表结构，也不把空身份当作管理员；"
        + "不记账、不生成凭证、不写资金 / 库存 / 会计数据，不改变既有只读退税汇总报表的聚合语义；"
        + "被拒绝的读取 / 新增 / 修改 / 删除 / 批量删除不改写任何台账行与审计时间戳。";



    // ==================== 1. 身份 / 账号状态 / 菜单授权（fail closed） ====================

    /// <summary>
    /// 身份 / 账号状态 / 既有「出口退税台账」菜单授权三重实时校验（fail closed），返回本次请求的权威客户数据范围：
    /// 缺失 / 非法身份按未认证拒绝，账号不存在 / 已删除按未认证拒绝，禁用按权限不足拒绝，
    /// 非特权账号缺少既有 <c>tax-refund</c> 菜单授权或未映射业务员按权限不足拒绝。
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

    // ==================== 2. 范围下推（计数 / 分页之前） ====================

    /// <summary>
    /// 出口退税台账范围谓词：<c>null</c>（进程内调用）或特权账号返回恒真；受限账号只保留<b>已登记归属客户且该客户在范围内</b>
    /// 的台账行，缺失权威归属（历史无主行）或越界一律排除，绝不「先查全量再内存过滤」。
    /// </summary>
    public static Expression<Func<BaseTaxRefund, bool>> ScopeFilter(SalespersonDataScope? scope)
    {
        if (scope is null || scope.AllowedCustomerIds is null) return _ => true;
        if (scope.AllowedCustomerIds.Count == 0) return _ => false;

        var allowed = scope.AllowedCustomerIds.ToList();
        return row => row.CustomerId.HasValue && allowed.Contains(row.CustomerId.Value);
    }

    /// <summary>按权威归属客户把台账范围下推到查询（<see cref="ScopeFilter"/> 的便捷重载）。</summary>
    public static IQueryable<BaseTaxRefund> ApplyScope(IQueryable<BaseTaxRefund> source, SalespersonDataScope? scope)
    {
        ArgumentNullException.ThrowIfNull(source);
        return source.Where(ScopeFilter(scope));
    }


    // ==================== 3. 已存 / 拟议归属校验 ====================

    /// <summary>
    /// 受限账号按持久化客户 Id 的硬范围边界：<c>null</c>（进程内）/ 特权账号放行；
    /// 受限账号的缺失权威归属（<c>null</c> / <c>&lt;= 0</c>）与越界一律 fail closed。
    /// </summary>
    public static void EnsureCustomerInScope(SalespersonDataScope? scope, long? customerId)
    {
        if (scope is null || scope.AllowedCustomerIds is null) return;

        if (customerId is null or <= 0)
            throw new BusinessException(UnlinkedLedgerText, ErrorCodes.Forbidden);
        if (!scope.AllowsCustomer(customerId.Value))
            throw new BusinessException(OutOfScopeText, ErrorCodes.Forbidden);
    }

    /// <summary>已存台账行的范围校验（详情 / 修改 / 删除 / 批量删除之前调用；特权账号保留历史访问）。</summary>
    public static void EnsureStoredScopeAllowed(SalespersonDataScope? scope, BaseTaxRefund entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        EnsureCustomerInScope(scope, entity.CustomerId);
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

    /// <summary>
    /// 拟议台账行（新增 / 修改）的完整范围校验（改写任何字段之前调用）：受限账号的拟议客户必须在实时范围内；
    /// 已登记客户必须是真实启用客户。<c>null</c> 范围（进程内调用）保持既有内部口径、不做范围限制。
    /// </summary>
    public static async Task EnsureProposedScopeAllowedAsync(
        IErpDbContext db, SalespersonDataScope? scope, BaseTaxRefund proposed, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(proposed);

        EnsureCustomerInScope(scope, proposed.CustomerId);
        if (scope is not null && proposed.CustomerId is > 0)
            await EnsureCustomerAvailableAsync(db, proposed.CustomerId.Value, ct);
    }

    /// <summary>
    /// 最小归属投影：按 Id 精确读取未删除台账行的归属字段，供控制器在不装载全量行的前提下复核范围
    /// （批量删除逐行复核，混合允许 / 越界批次整体拒绝）。
    /// </summary>
    public static async Task<List<BaseTaxRefund>> LoadOwnershipAsync(
        IErpDbContext db, IEnumerable<long> ledgerIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(ledgerIds);

        var ids = ledgerIds.Where(id => id > 0).Distinct().ToList();
        if (ids.Count == 0) return new List<BaseTaxRefund>();

        return await db.BaseTaxRefunds.AsNoTracking()
            .Where(r => ids.Contains(r.Id) && !r.IsDeleted)
            .Select(r => new BaseTaxRefund { Id = r.Id, CustomerId = r.CustomerId })
            .ToListAsync(ct);
    }


    // ==================== 4. 金额 / 税率 / 期间 / 日期校验（不改写实体） ====================

    /// <summary>
    /// 出口退税台账写入校验：出口金额、可退税额、已退税额非负；退税率落在 0..100；已退税不超过可退税；
    /// 币种与退税期间非空且不超过既有存储上界；申报 / 到账日期合理且到账不早于申报。
    /// 任一不满足即在改写任何字段之前以既有受控校验错误（<c>1001 InvalidParameter</c>）拒绝，
    /// <b>绝不静默截断、取整或回填</b>任何字段。
    /// </summary>
    public static void Validate(BaseTaxRefund entity)
    {
        ArgumentNullException.ThrowIfNull(entity);

        if (entity.ExportAmount < 0m)
            throw BusinessException.InvalidParameter($"{NegativeExportAmountText}：当前值 {entity.ExportAmount}");
        if (entity.RefundableAmount < 0m)
            throw BusinessException.InvalidParameter(
                $"{NegativeRefundableAmountText}：当前值 {entity.RefundableAmount}");
        if (entity.RefundedAmount < 0m)
            throw BusinessException.InvalidParameter(
                $"{NegativeRefundedAmountText}：当前值 {entity.RefundedAmount}");

        if (entity.RefundRate < 0m || entity.RefundRate > 100m)
            throw BusinessException.InvalidParameter($"{InvalidRefundRateText}：当前值 {entity.RefundRate}");

        if (entity.RefundedAmount > entity.RefundableAmount)
            throw BusinessException.InvalidParameter(
                $"{RefundedExceedsRefundableText}：已退 {entity.RefundedAmount} > 可退 {entity.RefundableAmount}");

        var currency = entity.Currency ?? string.Empty;
        if (string.IsNullOrWhiteSpace(currency))
            throw BusinessException.InvalidParameter(CurrencyRequiredText);
        if (currency.Length > MaxCurrencyLength)
            throw BusinessException.InvalidParameter(
                $"{CurrencyTooLongText}：当前长度 {currency.Length}");

        var period = entity.RefundPeriod ?? string.Empty;
        if (string.IsNullOrWhiteSpace(period))
            throw BusinessException.InvalidParameter(RefundPeriodRequiredText);
        if (period.Length > MaxRefundPeriodLength)
            throw BusinessException.InvalidParameter(
                $"{RefundPeriodTooLongText}：当前长度 {period.Length}");

        var declareDate = entity.DeclareDate;
        var refundDate = entity.RefundDate;

        if (declareDate is { } declare && declare.Date < MinPlausibleDate)
            throw BusinessException.InvalidParameter(
                $"{InconsistentDatesText}：申报日期 {declare:yyyy-MM-dd} 不是合理日期");
        if (refundDate is { } refund && refund.Date < MinPlausibleDate)
            throw BusinessException.InvalidParameter(
                $"{InconsistentDatesText}：到账日期 {refund:yyyy-MM-dd} 不是合理日期");

        if (declareDate is { } declareValue && refundDate is { } refundValue
            && refundValue.Date < declareValue.Date)
        {
            throw BusinessException.InvalidParameter(
                $"{InconsistentDatesText}：到账日期 {refundValue:yyyy-MM-dd} 早于申报日期 {declareValue:yyyy-MM-dd}");
        }
    }

}
