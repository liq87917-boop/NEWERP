using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using System.Linq.Expressions;

namespace ERP.Application.Services;

/// <summary>
/// 报价单 Quotation（<c>api/sales/quotations</c>）实时授权、权威客户范围与转换 / 版本护栏（ERP-400）。
/// <list type="number">
/// <item><b>实时授权</b>：列表 / 详情 / 新增 / 修改 / 提交 / 审核 / 销审 / 取消 / 删除 / 创建版本 / 版本链 /
/// 有效期提醒 / 打印 / 询价带入 / 带入预填销售订单 / 转 PI / 转销售订单，每一路由在读取任何计数、
/// 生成 / 消耗单据号或写入任何数据<b>之前</b>都先解析<b>实时启用身份</b>（缺失 / 非法按未认证拒绝，
/// 账号不存在 / 已删除按未认证拒绝，禁用按权限不足拒绝）、既有「报价单」（<c>quotation</c>）菜单授权
/// （非特权账号必须显式具备，未映射业务员 fail closed）与权威客户数据范围
/// （复用 ERP-097 <see cref="SalespersonDataScopeService"/> 唯一口径）。</item>
/// <item><b>权威归属</b>：报价单只按<b>持久化 <see cref="Quotation.CustomerId"/></b> 判定，绝不按
/// <see cref="Quotation.QuotationNo"/> / <see cref="Quotation.CustomerName"/> / <see cref="Quotation.InquiryNo"/>
/// 等自由文本推断；受限账号不能读取 / 改写缺失权威归属（<c>CustomerId</c> 为空）的「无主」报价单，
/// 真正不受限（特权）的授权账号保留既有历史访问。</item>
/// <item><b>列表 / 计数下推</b>：范围在 <c>Count</c> 与分页之前下推到 SQL（<see cref="ApplyScope"/>），
/// 绝不「先查全量再内存过滤」，绝不泄露范围外报价单的计数 / 明细 / 打印内容。</item>
/// <item><b>写入校验</b>：改写任何字段之前，受限账号的拟议客户必须落在实时范围内；修改同时复核
/// <b>已存客户</b>与<b>拟议客户</b>两侧。</item>
/// <item><b>转换护栏</b>：报价单 → 形式发票 PI 除报价单授权外，另需既有「形式发票 PI」
/// （<c>proforma-invoice</c>）授权；报价单 → 销售订单除报价单授权外，另需既有「销售订单」
/// （<c>sales-order</c>）授权；两种转换都同时复核<b>来源客户</b>与<b>目标客户</b>在实时范围内 ——
/// 绝不因来源报价单可见而授予目标菜单 / 目标客户权限。</item>
/// </list>
/// <para><b>身份来源唯一</b>：请求提交体中的任何字段都不能指定或扩大账号身份 —— 身份只来自已认证请求主体
/// （<c>ClaimTypes.NameIdentifier</c>），由控制器解析后调用本类；<c>null</c> 范围只表示「未经过 MVC 授权管线的
/// 进程内调用」，绝不代表匿名或管理员。每次调用都重新查询（绝不缓存），账号停用或授权撤销后下一次请求立即收敛。</para>
/// <para>边界：本类只做纯判定与有界只读查询；不新增表 / 列 / 菜单 / 权限模型，不新增任何用户授权，
/// 也不改写任何来源单据、库存来源审计与历史留痕。原始商业口径（计算 / 币种 / 单位）与既有转换 /
/// 版本资格保持不变。</para>
/// </summary>
public static class QuotationAuthorizationRules
{
    /// <summary>报价单模块复用的既有菜单编码（与 <c>SchemaUpgrader</c> 同源）</summary>
    public const string RequiredMenuCode = "quotation";

    /// <summary>报价单模块菜单中文文案（与既有菜单名一致）</summary>
    public const string RequiredMenuText = "报价单";

    /// <summary>转换目标（形式发票 PI）复用的既有菜单编码（与 <c>SchemaUpgrader</c> 同源）</summary>
    public const string ProformaInvoiceMenuCode = "proforma-invoice";

    /// <summary>形式发票 PI 模块菜单中文文案（与既有菜单名一致）</summary>
    public const string ProformaInvoiceMenuText = "形式发票 PI";

    /// <summary>转换目标（销售订单）复用的既有菜单编码（与 <c>SeedData.Menus</c> 同源）</summary>
    public const string SalesOrderMenuCode = "sales-order";

    /// <summary>销售订单模块菜单中文文案（与既有菜单名一致）</summary>
    public const string SalesOrderMenuText = "销售订单";

    /// <summary>无身份 / 非法身份的拒绝文案</summary>
    public const string UnauthorizedText = "请先登录后再访问报价单";

    /// <summary>账号不存在 / 已删除的拒绝文案</summary>
    public const string UserDeletedText = "登录账号不存在或已删除，禁止访问报价单";

    /// <summary>账号已禁用的拒绝文案</summary>
    public const string UserDisabledText = "登录账号已禁用，禁止访问报价单（fail closed）";

    /// <summary>缺少既有「报价单」菜单授权的拒绝文案</summary>
    public const string MenuDeniedText =
        "当前账号没有「报价单」（quotation）模块授权：拒绝访问报价单台账 / 明细 / 打印 / 版本 / 转换"
        + "（fail closed，不返回 / 不修改任何报价单数据）";

    /// <summary>受限账号未映射业务员的拒绝文案</summary>
    public const string UnmappedOperatorText =
        "当前账号未映射为业务员（报价单操作员），不能访问报价单（fail closed，不泄露任何范围外报价单）";

    /// <summary>缺少既有「形式发票 PI」菜单授权的转换拒绝文案</summary>
    public const string ProformaInvoiceMenuDeniedText =
        "当前账号没有「形式发票 PI」（proforma-invoice）模块授权：拒绝由报价单生成 PI"
        + "（fail closed，不产生任何 PI，也不改写来源报价单）";

    /// <summary>缺少既有「销售订单」菜单授权的转换拒绝文案</summary>
    public const string SalesOrderMenuDeniedText =
        "当前账号没有「销售订单」（sales-order）模块授权：拒绝由报价单生成销售订单"
        + "（fail closed，不产生任何销售订单，也不改写来源报价单）";

    /// <summary>缺少既有「销售订单」菜单授权时普通销售订单保存来源链接的拒绝文案（ERP-401）</summary>
    public const string SalesOrderLineageDeniedText =
        "当前账号没有「销售订单」（sales-order）模块授权：拒绝在销售订单上链接来源报价单"
        + "（fail closed，不写入任何销售订单，也不改写来源报价单）";

    /// <summary>受限账号访问缺失权威归属（CustomerId 为空）报价单的拒绝文案</summary>
    public const string UnlinkedCustomerText =
        "该报价单没有可判定的权威客户归属（持久化 CustomerId 缺失）：受限账号拒绝访问（fail closed，不泄露无主报价单）";

    /// <summary>报价单客户越范围的拒绝文案</summary>
    public const string OutOfScopeText =
        "当前账号的客户数据范围不包含该报价单的归属客户：拒绝操作（fail closed，不泄露范围外报价单）";

    /// <summary>拟议客户越范围的拒绝文案</summary>
    public const string ProposedOutOfScopeText =
        "拟议客户不在当前账号客户数据范围内：拒绝写入（fail closed，不泄露范围外客户）";

    /// <summary>来源报价单缺失权威归属 / 越界时转换的拒绝文案</summary>
    public const string SourceOutOfScopeText =
        "来源报价单的归属客户缺失或不在当前账号客户数据范围内：拒绝转换"
        + "（fail closed，不泄露范围外客户，也不产生任何目标单据）";

    /// <summary>目标单据客户缺失权威归属 / 越界的拒绝文案（绝不因来源可见而授予目标权限）</summary>
    public const string TargetOutOfScopeText =
        "目标单据客户缺失或不在当前账号客户数据范围内：拒绝转换"
        + "（fail closed，绝不因来源报价单可见而授予目标客户权限）";

    /// <summary>授权与数据范围口径文案（接口 / 文档同源）</summary>
    public const string RuleText =
        "报价单授权与数据范围护栏：列表 / 详情 / 新增 / 修改 / 提交 / 审核 / 销审 / 取消 / 删除 / 创建版本 / 版本链 / "
        + "有效期提醒 / 打印 / 询价带入 / 带入预填销售订单 / 转 PI / 转销售订单，每一路由都会重新校验当前身份"
        + "（缺失 / 已删除 / 禁用一律 fail closed）、既有「报价单」（quotation）菜单授权与 "
        + "SalespersonDataScopeService（ERP-097 唯一权威口径）客户数据范围；报价单只按持久化 CustomerId 判定权威归属"
        + "（绝不按单号 / 客户名等自由文本推断），受限账号不得访问无主报价单，特权账号保留历史访问；"
        + "列表在计数与分页之前把范围下推到数据库；新增 / 修改在写入前校验拟议客户（修改同时校验已存与拟议两侧）落在实时范围内；"
        + "转 PI 额外要求既有「形式发票 PI」（proforma-invoice）授权，转销售订单额外要求既有「销售订单」（sales-order）授权，"
        + "并同时复核来源客户与目标客户在范围内，绝不因来源可见而授予目标权限；"
        + "被拒绝的调用方绝不消耗单据号、绝不改写任何单据 / 明细 / 状态 / 金额；"
        + "绝不新增权限模型、绝不把空身份当作管理员，也绝不泄露范围外报价单。";

    /// <summary>边界文案（不改变报价单商业口径、不改写来源单据 / 库存来源审计）</summary>
    public const string BoundaryText =
        "本护栏只保护报价单的授权、客户范围、版本与转换入口：不改写数量 / 单价 / 金额 / 合计 / 币种 / 汇率 / 单位等原始商业语义，"
        + "不改写报价单编号 / 状态 / 明细行、来源询价单、形式发票 PI、销售订单、客户主数据，也不删除历史单据与审计证据；"
        + "不新增任何表 / 列 / 菜单 / 权限，也不新增任何用户授权。";

    // ==================== 1. 身份 / 账号状态 / 菜单授权（fail closed） ====================

    /// <summary>
    /// 身份 / 账号状态 / 既有「报价单」菜单授权三重实时校验（fail closed），返回本次请求的权威客户数据范围：
    /// 缺失 / 非法身份按未认证拒绝，账号不存在 / 已删除按未认证拒绝，禁用按权限不足拒绝，
    /// 非特权账号缺少既有 <c>quotation</c> 菜单授权或未映射业务员按权限不足拒绝。
    /// 每次调用都重新查询（无缓存），账号停用或授权撤销后下一次请求立即收敛；特权账号保留既有全部访问。
    /// </summary>
    public static async Task<SalespersonDataScope> EnsureAuthorizedAsync(
        IErpDbContext db, long? userId, CancellationToken ct = default)
    {
        var scope = await ResolveLiveIdentityAsync(db, userId, ct);
        await EnsureMenuAsync(db, scope, userId!.Value, RequiredMenuCode, MenuDeniedText, ct);
        return scope;
    }

    /// <summary>
    /// 普通销售订单表单保存**显式链接来源报价单**所需的授权（ERP-401）：实时启用身份阶梯与
    /// <see cref="EnsureAuthorizedAsync"/> **完全同源**，但只要求既有「销售订单」（<c>sales-order</c>）
    /// 菜单授权（不要求报价单菜单 —— 保存的是销售订单，不是报价单台账），返回本次请求的权威客户数据范围。
    /// </summary>
    public static async Task<SalespersonDataScope> EnsureSalesOrderLineageAuthorizedAsync(
        IErpDbContext db, long? userId, CancellationToken ct = default)
    {
        var scope = await ResolveLiveIdentityAsync(db, userId, ct);
        await EnsureMenuAsync(db, scope, userId!.Value, SalesOrderMenuCode, SalesOrderLineageDeniedText, ct);
        return scope;
    }

    /// <summary>
    /// 实时身份阶梯（每次重新查询、绝不缓存）：缺失 / 非法身份 → 未认证；账号不存在 / 已删除 → 未认证；
    /// 禁用 → 权限不足。空身份绝不降级为匿名或管理员。
    /// </summary>
    private static async Task<SalespersonDataScope> ResolveLiveIdentityAsync(
        IErpDbContext db, long? userId, CancellationToken ct)
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

        return await SalespersonDataScopeService.ResolveAsync(db, userId.Value);
    }

    /// <summary>
    /// 转换授权（报价单 → 形式发票 PI，带入 / 直接生成）：在 <see cref="EnsureAuthorizedAsync"/> 之上，
    /// 额外要求非特权账号具备既有「形式发票 PI」（<c>proforma-invoice</c>）菜单授权 ——
    /// 绝不因来源报价单可见而放行转换。
    /// </summary>
    public static async Task<SalespersonDataScope> EnsureProformaInvoiceConversionAuthorizedAsync(
        IErpDbContext db, long? userId, CancellationToken ct = default)
    {
        var scope = await EnsureAuthorizedAsync(db, userId, ct);
        await EnsureMenuAsync(db, scope, userId!.Value, ProformaInvoiceMenuCode, ProformaInvoiceMenuDeniedText, ct);
        return scope;
    }

    /// <summary>
    /// 转换授权（报价单 → 销售订单，带入预填 / 直接生成）：在 <see cref="EnsureAuthorizedAsync"/> 之上，
    /// 额外要求非特权账号具备既有「销售订单」（<c>sales-order</c>）菜单授权 ——
    /// 绝不因来源报价单可见而放行转换。
    /// </summary>
    public static async Task<SalespersonDataScope> EnsureSalesOrderConversionAuthorizedAsync(
        IErpDbContext db, long? userId, CancellationToken ct = default)
    {
        var scope = await EnsureAuthorizedAsync(db, userId, ct);
        await EnsureMenuAsync(db, scope, userId!.Value, SalesOrderMenuCode, SalesOrderMenuDeniedText, ct);
        return scope;
    }

    /// <summary>非特权账号的菜单授权复核（特权账号豁免同一口径；未映射业务员 fail closed）。</summary>
    private static async Task EnsureMenuAsync(IErpDbContext db, SalespersonDataScope scope, long userId,
        string menuCode, string deniedText, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (scope.IsPrivileged) return;

        var menuCodes = await CustomerReceivableReconciliationService.LoadAuthorizedMenuCodesAsync(db, userId);
        if (!menuCodes.Contains(menuCode, StringComparer.OrdinalIgnoreCase))
            throw new BusinessException(deniedText, ErrorCodes.Forbidden);

        if (scope.SalesmanId is null or <= 0)
            throw new BusinessException(UnmappedOperatorText, ErrorCodes.Forbidden);
    }

    // ==================== 2. 范围下推（计数 / 分页之前） ====================

    /// <summary>
    /// 报价单作用域谓词：<c>null</c>（进程内调用）或特权账号恒真；受限账号只保留<b>已登记归属客户且在该账号范围内</b>
    /// 的报价单，缺失权威归属（<c>CustomerId</c> 为空）或越界一律排除，绝不「先查全量再内存过滤」。
    /// </summary>
    public static Expression<Func<Quotation, bool>> ScopeFilter(SalespersonDataScope? scope)
    {
        if (scope is null || scope.AllowedCustomerIds is null) return _ => true;
        if (scope.AllowedCustomerIds.Count == 0) return _ => false;

        var allowed = scope.AllowedCustomerIds.ToList();
        return q => q.CustomerId.HasValue && allowed.Contains(q.CustomerId.Value);
    }

    /// <summary>按权威归属客户把报价单范围下推到查询（<see cref="ScopeFilter"/> 的便捷重载）。</summary>
    public static IQueryable<Quotation> ApplyScope(IQueryable<Quotation> source, SalespersonDataScope? scope)
    {
        ArgumentNullException.ThrowIfNull(source);
        return source.Where(ScopeFilter(scope));
    }

    // ==================== 3. 已存 / 拟议 / 来源 / 目标归属校验 ====================

    /// <summary>
    /// 受限账号按持久化客户 Id 的硬范围边界：<c>null</c>（进程内）/ 特权账号放行；
    /// 受限账号缺失权威归属（<c>null</c> / <c>&lt;= 0</c>）与越界一律 fail closed（用于写入路径）。
    /// </summary>
    public static void EnsureStoredCustomerInScope(SalespersonDataScope? scope, long? customerId)
        => EnsureCustomerInScope(scope, customerId, OutOfScopeText);

    /// <summary>
    /// 拟议客户（新增 / 修改请求体）的实时范围校验：受限账号的拟议客户必须落在实时范围内，
    /// 缺失 / 未知 / 越界一律 fail closed（绝不按自由文本猜测客户）。特权与进程内调用保持既有口径。
    /// </summary>
    public static void EnsureProposedCustomerInScope(SalespersonDataScope? scope, long? customerId)
        => EnsureCustomerInScope(scope, customerId, ProposedOutOfScopeText);

    /// <summary>来源报价单客户的范围校验（转换之前调用；绝不通过共享来源泄露范围外客户）。</summary>
    public static void EnsureSourceCustomerInScope(SalespersonDataScope? scope, long? customerId)
        => EnsureCustomerInScope(scope, customerId, SourceOutOfScopeText);

    /// <summary>
    /// 目标单据（PI / 销售订单）客户的范围校验（转换之前调用）：即使来源报价单与目标同属一个客户，
    /// 也必须<b>独立</b>复核目标客户在实时范围内 —— 来源可见绝不等于目标授权。
    /// </summary>
    public static void EnsureTargetCustomerInScope(SalespersonDataScope? scope, long? customerId)
        => EnsureCustomerInScope(scope, customerId, TargetOutOfScopeText);

    /// <summary>受限账号客户范围通用判定：<c>null</c> / 特权账号放行；受限账号缺失归属或越界一律 fail closed。</summary>
    private static void EnsureCustomerInScope(SalespersonDataScope? scope, long? customerId, string deniedText)
    {
        if (scope is null || scope.AllowedCustomerIds is null) return;

        if (customerId is null or <= 0)
            throw new BusinessException(deniedText, ErrorCodes.Forbidden);
        if (!scope.AllowsCustomer(customerId.Value))
            throw new BusinessException(deniedText, ErrorCodes.Forbidden);
    }

    /// <summary>
    /// 版本链可见性复核：链内每一张报价单都必须落在实时范围内（受限账号缺失归属或越界即整体拒绝），
    /// 避免因为存在一个可见版本而泄露范围外版本的单号 / 金额 / 状态。
    /// </summary>
    public static void EnsureChainCustomerInScope(SalespersonDataScope? scope, IEnumerable<long?> customerIds)
    {
        ArgumentNullException.ThrowIfNull(customerIds);
        foreach (var customerId in customerIds)
            EnsureCustomerInScope(scope, customerId, OutOfScopeText);
    }
}
