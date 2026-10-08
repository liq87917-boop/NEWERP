using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using System.Linq.Expressions;

namespace ERP.Application.Services;

/// <summary>
/// 询价单 Inquiry（<c>api/inquiries</c>）实时授权、权威客户范围与「询价单 → 报价单」转换护栏（ERP-402）。
/// <list type="number">
/// <item><b>实时授权</b>：列表 / 详情 / 新增 / 修改 / 提交 / 审核 / 取消 / 删除 / 批量删除 / 导出 / 明细带入 /
/// 转报价单，每一路由在读取任何计数、生成 / 消耗单据号或写入任何数据<b>之前</b>都先解析
/// <b>实时启用身份</b>（缺失 / 非法按未认证拒绝，账号不存在 / 已删除按未认证拒绝，禁用按权限不足拒绝）、
/// 既有「询价单」（<c>inquiry</c>）菜单授权（非特权账号必须显式具备且已映射业务员）与权威客户数据范围
/// （复用 ERP-097 <see cref="SalespersonDataScopeService"/> 唯一口径）。</item>
/// <item><b>转换另需目标菜单</b>：由询价单生成报价单除询价单授权外，另需既有「报价单」（<c>quotation</c>）授权 ——
/// 询价单可见绝不等于报价单授权。</item>
/// <item><b>权威归属</b>：询价单只按<b>持久化 <see cref="Inquiry.CustomerId"/></b> 判定，绝不按
/// <see cref="Inquiry.InquiryNo"/> / 联系人等自由文本推断；修改同时复核<b>已存客户</b>与<b>拟议客户</b>两侧。</item>
/// <item><b>计数 / 分页 / 导出下推</b>：范围在 <c>Count</c> 与分页之前下推到 SQL（<see cref="ApplyScope"/>），
/// 绝不「先查全量再内存过滤」，绝不泄露范围外询价单的计数 / 明细 / 导出内容。</item>
/// </list>
/// <para><b>身份来源唯一</b>：请求提交体中的任何字段都不能指定或扩大账号身份 —— 身份只来自已认证请求主体
/// （<c>ClaimTypes.NameIdentifier</c>），由控制器解析后调用本类；<c>null</c> 范围只表示「未经过 MVC 授权管线的
/// 进程内调用」，绝不代表匿名或管理员。每次调用都重新查询（绝不缓存），账号停用或授权撤销后下一次请求立即收敛。</para>
/// <para>边界：本类只做纯判定与有界只读查询；不新增表 / 列 / 菜单 / 角色 / 用户授权，也不改写任何来源单据、
/// 库存来源审计与历史留痕。原始商业口径（明细 / 数量 / 单价 / 金额 / 币种 / 单位）保持不变。</para>
/// </summary>
public static class InquiryAuthorizationRules
{
    /// <summary>询价单模块复用的既有菜单编码（与 <c>SeedData.Menus</c> / <c>SchemaUpgrader</c> 同源）</summary>
    public const string RequiredMenuCode = "inquiry";

    /// <summary>询价单模块菜单中文文案（与既有菜单名一致）</summary>
    public const string RequiredMenuText = "询价单";

    /// <summary>转换目标（报价单）复用的既有菜单编码（与 ERP-400 <see cref="QuotationAuthorizationRules"/> 同源）</summary>
    public const string QuotationMenuCode = QuotationAuthorizationRules.RequiredMenuCode;

    /// <summary>报价单模块菜单中文文案（与既有菜单名一致）</summary>
    public const string QuotationMenuText = QuotationAuthorizationRules.RequiredMenuText;

    /// <summary>无身份 / 非法身份的拒绝文案</summary>
    public const string UnauthorizedText = "请先登录后再访问询价单";

    /// <summary>账号不存在 / 已删除的拒绝文案</summary>
    public const string UserDeletedText = "登录账号不存在或已删除，禁止访问询价单";

    /// <summary>账号已禁用的拒绝文案</summary>
    public const string UserDisabledText = "登录账号已禁用，禁止访问询价单（fail closed）";

    /// <summary>缺少既有「询价单」菜单授权的拒绝文案</summary>
    public const string MenuDeniedText =
        "当前账号没有「询价单」（inquiry）模块授权：拒绝访问询价单台账 / 明细 / 导出 / 明细带入 / 状态流转 / 转报价单"
        + "（fail closed，不返回 / 不修改任何询价单数据）";

    /// <summary>缺少既有「报价单」菜单授权的转换拒绝文案</summary>
    public const string QuotationMenuDeniedText =
        "当前账号没有「报价单」（quotation）模块授权：拒绝由询价单生成报价单"
        + "（fail closed，不消耗单据号、不写入任何报价单）";

    /// <summary>受限账号未映射业务员的拒绝文案</summary>
    public const string UnmappedOperatorText =
        "当前账号未映射为业务员（询价单操作员），不能访问询价单（fail closed，不泄露任何范围外询价单）";

    /// <summary>已存询价单越客户范围的拒绝文案（fail closed，不泄露范围外询价单）</summary>
    public const string OutOfScopeText =
        "当前账号的客户数据范围不包含该询价单的客户：拒绝操作（fail closed，不泄露范围外询价单）";

    /// <summary>拟议客户越范围的拒绝文案（写入之前 fail closed）</summary>
    public const string ProposedOutOfScopeText =
        "当前账号的客户数据范围不包含拟议客户：拒绝写入询价单（fail closed，不产生任何写入）";

    /// <summary>授权与数据范围口径文案（接口 / 文档同源）</summary>
    public const string RuleText =
        "询价单（列表 / 详情 / 新增 / 修改 / 提交 / 审核 / 取消 / 删除 / 批量删除 / 导出 / 明细带入 / 转报价单）"
        + "在读取任何计数、生成 / 消耗单据号或写入任何数据之前，都重新校验实时身份（缺失 / 非法 / 已删除按未认证，"
        + "禁用按权限不足，一律 fail closed）、既有「询价单」（inquiry）菜单授权与权威客户数据范围（复用 ERP-097）；"
        + "转报价单另需既有「报价单」（quotation）菜单授权。受限制账号只能读写本人客户的询价单，"
        + "计数 / 分页 / 导出在数据库侧先于读取下推范围；未映射业务员的受限账号 fail closed，绝不降级为全局 / 管理员可见。";

    /// <summary>边界文案（不新增权限 / 表列 / 授权，不改写来源与库存审计）</summary>
    public const string BoundaryText =
        "本护栏不新增任何表 / 列 / 菜单 / 角色 / 用户授权，不把空身份当作匿名或管理员，也不伪造任何授权；"
        + "不改写询价单明细 / 数量 / 单价 / 金额 / 币种 / 汇率 / 单位等原始商业语义（服务端口径不变），"
        + "不改写客户主数据、库存与库存流水、报价单、PI、销售订单、发票或财务记录，不做任何财务 / 库存过账；"
        + "保留库存来源单据审计与原始失败日志：被拒绝 / 回滚的请求只回滚自己的写入，不清理 / 不删除任何既有行。";

    /// <summary>
    /// 身份 / 账号状态 / 既有「询价单」菜单授权 / 权威数据范围四重校验（fail closed），返回本次请求的数据范围。
    /// </summary>
    public static Task<SalespersonDataScope> EnsureAuthorizedAsync(
        IErpDbContext db, long? userId, CancellationToken ct = default)
        => EnsureCoreAsync(db, userId, requireQuotationMenu: false, ct);

    /// <summary>
    /// 「询价单 → 报价单」转换授权：在询价单四重校验之上，另需既有「报价单」（quotation）菜单授权；
    /// 询价单可见绝不等于报价单授权，也绝不新增任何用户授权。
    /// </summary>
    public static Task<SalespersonDataScope> EnsureQuotationConversionAuthorizedAsync(
        IErpDbContext db, long? userId, CancellationToken ct = default)
        => EnsureCoreAsync(db, userId, requireQuotationMenu: true, ct);

    private static async Task<SalespersonDataScope> EnsureCoreAsync(
        IErpDbContext db, long? userId, bool requireQuotationMenu, CancellationToken ct)
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

        // 特权账号继承既有全部访问（与 ERP-097 同源）；普通账号必须显式具备既有菜单。
        if (!scope.IsPrivileged)
        {
            var menuCodes = await CustomerReceivableReconciliationService
                .LoadAuthorizedMenuCodesAsync(db, userId.Value);
            if (!menuCodes.Contains(RequiredMenuCode, StringComparer.OrdinalIgnoreCase))
                throw new BusinessException(MenuDeniedText, ErrorCodes.Forbidden);

            if (scope.SalesmanId is null or <= 0)
                throw new BusinessException(UnmappedOperatorText, ErrorCodes.Forbidden);

            if (requireQuotationMenu
                && !menuCodes.Contains(QuotationMenuCode, StringComparer.OrdinalIgnoreCase))
            {
                throw new BusinessException(QuotationMenuDeniedText, ErrorCodes.Forbidden);
            }
        }

        return scope;
    }

    /// <summary>
    /// 询价单作用域谓词：<c>null</c>（进程内调用）或特权账号恒真；受限账号只保留<b>归属客户在实时范围内</b>的询价单，
    /// 未映射业务员（空集合）恒假，绝不「先查全量再内存过滤」。
    /// </summary>
    public static Expression<Func<Inquiry, bool>> ScopeFilter(SalespersonDataScope? scope)
    {
        if (scope is null || scope.AllowedCustomerIds is null) return _ => true;
        if (scope.AllowedCustomerIds.Count == 0) return _ => false;

        var allowed = scope.AllowedCustomerIds.ToList();
        return i => allowed.Contains(i.CustomerId);
    }

    /// <summary>按权威归属客户把询价单范围下推到查询（<see cref="ScopeFilter"/> 的便捷重载）。</summary>
    public static IQueryable<Inquiry> ApplyScope(IQueryable<Inquiry> source, SalespersonDataScope? scope)
    {
        ArgumentNullException.ThrowIfNull(source);
        return source.Where(ScopeFilter(scope));
    }

    /// <summary>已存询价单的权威归属范围校验（读取 / 写路径之前调用；越界 fail closed）。</summary>
    public static void EnsureStoredCustomerInScope(SalespersonDataScope? scope, long customerId)
        => EnsureCustomerInScope(scope, customerId, OutOfScopeText);

    /// <summary>拟议客户（新增 / 修改请求体）的实时范围校验：缺失 / 越界一律 fail closed。</summary>
    public static void EnsureProposedCustomerInScope(SalespersonDataScope? scope, long customerId)
        => EnsureCustomerInScope(scope, customerId, ProposedOutOfScopeText);

    /// <summary>受限账号客户范围通用判定：<c>null</c> / 特权账号放行；受限账号缺失归属或越界一律 fail closed。</summary>
    private static void EnsureCustomerInScope(SalespersonDataScope? scope, long customerId, string deniedText)
    {
        if (scope is null || scope.AllowedCustomerIds is null) return;

        if (customerId <= 0 || !scope.AllowsCustomer(customerId))
            throw new BusinessException(deniedText, ErrorCodes.Forbidden);
    }
}
