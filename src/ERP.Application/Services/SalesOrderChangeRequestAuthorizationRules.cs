using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace ERP.Application.Services;

/// <summary>
/// 销售订单变更申请登记册（<c>api/sales-order-change-requests</c>，ERP-047）的实时授权与来源归属护栏（ERP-414）。
/// <para>背景：台账 / 来源候选 / 指定来源清单 / 详情以及登记 / 编辑 / 提交 / 取消此前直接调用服务，
/// 不解析实时身份、既有「销售订单」菜单与业务员客户数据范围：任何已登录账号只要猜到一张范围外订单 Id，
/// 就能读取 / 登记其变更申请（含来源快照、拟议金额与客户归属）。</para>
/// <list type="number">
/// <item><b>实时身份 + 既有菜单</b>：复用规范销售订单入口的权威口径
/// <see cref="SalesOrderExecutionAuthorizationRules.EnsureReadAuthorizedAsync"/>（ERP-413）：身份缺失 / 非法 /
/// 账号不存在 / 已删除按未认证拒绝，账号已禁用按权限不足拒绝，非特权账号必须实时具备既有「销售订单」
/// （<c>sales-order</c>）功能菜单。绝不新增菜单 / 角色 / 用户授权，也绝不把空身份当作匿名或管理员。</item>
/// <item><b>唯一权威数据范围</b>：复用 <see cref="SalespersonDataScopeService"/>（ERP-097）；归属只按
/// <b>来源销售订单当前持久化的 <see cref="SalesOrder.CustomerId"/></b> 判定 —— 台账在
/// <c>Count</c> / 分页 / 来源候选之前把范围<b>下推</b>到数据库（<see cref="ApplySourceScope"/>），
/// 绝不「先查全量再内存过滤」。</item>
/// <item><b>拟议字段不授予归属</b>：拟议客户（<see cref="SalesOrderChangeRequest.ProposedCustomerId"/>）与
/// 来源客户快照（<see cref="SalesOrderChangeRequest.SourceCustomerId"/>）都<b>不</b>是授权依据：
/// 来源被改派后，历史快照客户不再代表归属，一律以<b>实时来源订单</b>为准。</item>
/// <item><b>来源不可解析 fail closed</b>：受限账号不能读取「实时来源订单缺失 / 已删除 / 无法解析归属」的申请；
/// 特权账号保留既有不受限历史访问（显式声明）。</item>
/// <item><b>非披露错误</b>：范围外 / 已删除 / 不存在的来源与申请返回<b>同一</b>受控错误（<see cref="NotFoundText"/>），
/// 授权先于任何计数 / 分页 / 发号 / 明细替换 / 状态变更，绝不通过差异化错误透露不可访问记录的存在性。</item>
/// </list>
/// <para><b>边界</b>：本类只做纯判定与有界只读查询，不落库、不审核、不套用、不发号，也不改写来源销售订单
/// 与报价单 / 出库 / 装柜与出运 / 收款与发票 / 佣金 / 库存 / 单证 / 财务记录；不新增表 / 列 / 索引 / 菜单 /
/// 权限模型。调用方（控制器）负责在任何读取 / 写入之前调用。</para>
/// </summary>
public static class SalesOrderChangeRequestAuthorizationRules
{
    /// <summary>复用规范销售订单模块的既有菜单编码（与 <c>SeedData.Menus</c> 同源，绝不新增菜单）。</summary>
    public const string RequiredMenuCode = SalesOrderExecutionAuthorizationRules.RequiredMenuCode;

    /// <summary>销售订单模块菜单中文文案（与既有菜单名一致）。</summary>
    public const string RequiredMenuText = SalesOrderExecutionAuthorizationRules.RequiredMenuText;

    /// <summary>模块中文名称（接口 / 文档同源）。</summary>
    public const string ModuleText = "销售订单变更申请登记册";

    /// <summary>无身份 / 非法身份的拒绝文案（与规范销售订单入口同源）。</summary>
    public const string UnauthorizedText = SalesOrderExecutionAuthorizationRules.UnauthorizedText;

    /// <summary>账号不存在 / 已删除的拒绝文案（与规范销售订单入口同源）。</summary>
    public const string UserDeletedText = SalesOrderExecutionAuthorizationRules.UserDeletedText;

    /// <summary>账号已禁用的拒绝文案（与规范销售订单入口同源）。</summary>
    public const string UserDisabledText = SalesOrderExecutionAuthorizationRules.UserDisabledText;

    /// <summary>缺少既有「销售订单」菜单授权时的拒绝文案（与规范销售订单入口同源）。</summary>
    public const string MenuDeniedText = SalesOrderExecutionAuthorizationRules.MenuDeniedText;

    /// <summary>
    /// 统一的非披露错误文案：范围外 / 已删除 / 不存在的来源销售订单与变更申请都返回**同一**受控错误，
    /// 绝不通过差异化错误 / 计数 / 部分行透露不可访问记录的存在性。
    /// </summary>
    public const string NotFoundText = "变更申请或来源销售订单不存在";

    /// <summary>授权与来源归属口径文案（接口 / 文档同源）。</summary>
    public const string RuleText =
        "销售订单变更申请护栏：台账 / 来源候选 / 指定来源清单 / 详情 / 登记 / 编辑 / 提交 / 取消在读取任何计数、"
        + "分页、候选、来源快照或写入任何数据之前，都重新校验实时身份（缺失 / 非法 / 已删除按未认证，已禁用按权限不足）、"
        + "既有「销售订单」（sales-order）功能菜单授权（非特权账号必须显式具备）与 SalespersonDataScopeService"
        + "（ERP-097 唯一权威口径）客户数据范围；归属只按来源销售订单**当前持久化的 CustomerId** 判定，"
        + "台账在计数 / 分页 / 来源候选之前把范围下推到数据库，拟议客户与来源客户历史快照都不是授权依据；"
        + "实时来源缺失 / 已删除 / 无法解析归属时受限账号 fail closed，范围外 / 已删除 / 不存在的来源与申请"
        + "返回同一非披露错误；绝不新增权限模型、绝不把空身份当作管理员，也绝不发号或写入任何被拒数据。";

    /// <summary>边界文案（不审核、不套用、不改写来源与下游记录，也不新增权限模型）。</summary>
    public const string BoundaryText =
        "本护栏只保护销售订单变更申请登记册的数据与状态入口：不审核、不套用、不自动转换、不做硬删除，"
        + "也不改写来源销售订单、报价单、出库、装柜与出运、收款与发票、佣金、库存与库存成本、单证中心与财务记录；"
        + "已取消与已提交的申请对其有权限账号仍是只读证据；不新增任何表 / 列 / 索引 / 菜单 / 角色 / 用户授权。";

    /// <summary>
    /// 本模块数据 / 状态入口的完整授权（实时身份 + 既有「销售订单」菜单 + ERP-097 权威客户数据范围）。
    /// 直接复用规范销售订单入口的权威口径 <see cref="SalesOrderExecutionAuthorizationRules.EnsureReadAuthorizedAsync"/>：
    /// 特权账号豁免菜单校验但仍须通过实时身份校验；每次调用都重新查询（无缓存），授权撤销 / 账号停用后立即收敛。
    /// </summary>
    public static Task<SalespersonDataScope> EnsureAccessAuthorizedAsync(
        IErpDbContext db, long? userId, CancellationToken ct = default)
        => SalesOrderExecutionAuthorizationRules.EnsureReadAuthorizedAsync(db, userId, ct);

    /// <summary>
    /// 按**来源订单实时归属**把台账下推到数据库：特权 / 进程内（<paramref name="scope"/> 为 <c>null</c> 或
    /// <c>AllowedCustomerIds</c> 为 <c>null</c>）不过滤（保留既有不受限历史访问，含来源已缺失的历史申请）；
    /// 受限账号只保留「实时来源订单存在、未删除且 CustomerId 在范围内」的申请 —— 来源缺失 / 已删除 /
    /// 归属无法解析的申请对受限账号不可见（fail closed），绝不使用来源客户历史快照或拟议客户判定归属。
    /// </summary>
    public static IQueryable<SalesOrderChangeRequest> ApplySourceScope(
        IErpDbContext db, SalespersonDataScope? scope, IQueryable<SalesOrderChangeRequest> query)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(query);
        if (scope is null || scope.AllowedCustomerIds is null) return query;

        var allowed = scope.AllowedCustomerIds.ToList();
        return query.Where(r => db.SalesOrders.Any(
            o => o.Id == r.SalesOrderId && !o.IsDeleted && allowed.Contains(o.CustomerId)));
    }

    /// <summary>
    /// 显式来源销售订单 Id 的归属复核（登记 / 编辑 / 指定来源清单之前调用）：
    /// 受限账号下，来源不存在 / 已删除 / 范围外一律返回同一非披露错误（fail closed）；
    /// 特权 / 进程内调用（<paramref name="scope"/> 为 <c>null</c> 或不受限）不在此处判定归属 —— 存在性仍由服务层校验。
    /// </summary>
    public static async Task<long> EnsureSourceOrderAllowedAsync(
        IErpDbContext db, SalespersonDataScope? scope, long salesOrderId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (salesOrderId <= 0) throw BusinessException.NotFound(NotFoundText);

        if (scope is null || scope.AllowedCustomerIds is null) return 0;

        var customerId = await db.SalesOrders.AsNoTracking()
            .Where(o => o.Id == salesOrderId && !o.IsDeleted)
            .Select(o => (long?)o.CustomerId)
            .FirstOrDefaultAsync(ct);

        if (customerId is null || !scope.AllowsCustomer(customerId.Value))
            throw BusinessException.NotFound(NotFoundText);

        return customerId.Value;
    }

    /// <summary>
    /// 已存变更申请的归属复核（详情 / 编辑 / 提交 / 取消之前调用）：申请不存在 / 已删除，或其**实时来源**不存在 /
    /// 已删除 / 范围外，一律返回同一非披露错误；返回在库申请供调用方复用（受限账号 fail closed，
    /// 特权 / 进程内调用只校验申请存在性，保留既有不受限历史访问）。
    /// </summary>
    public static async Task<SalesOrderChangeRequest> EnsureRequestAllowedAsync(
        IErpDbContext db, SalespersonDataScope? scope, long requestId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (requestId <= 0) throw BusinessException.NotFound(NotFoundText);

        var request = await db.SalesOrderChangeRequests.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == requestId && !r.IsDeleted, ct)
            ?? throw BusinessException.NotFound(NotFoundText);

        await EnsureSourceOrderAllowedAsync(db, scope, request.SalesOrderId, ct);
        return request;
    }
}
