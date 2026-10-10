using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ERP.Application.Services;

/// <summary>
/// 规范销售订单（<c>api/sales-orders</c>）**普通写入入口**的实时授权与权威客户范围护栏（ERP-420）。
/// <para>背景：ERP-401 只为「显式链接且可精确解析来源」的保存 / 提交 / 审核施加实时授权；<b>无来源的手工订单</b>
/// 与<b>无法解析的显式历史来源</b>路径，以及继承自 <c>DocumentControllerBase</c> 的软删除入口，此前都不解析
/// 实时身份 / 既有菜单 / 权威客户范围 —— 任何已登录账号只要构造一个请求就能创建 / 改写 / 删除 / 提交流转
/// 范围外客户的销售订单。本类把这些口径统一抽到 Application 层，覆盖规范订单的
/// <b>新增 / 修改 / 删除 / 提交 / 审核 / 取消</b>全部写入动作。</para>
/// <list type="number">
/// <item><b>实时身份</b>：缺失 / 非法（非正整数）按未认证拒绝，账号不存在 / 已删除按未认证拒绝，账号已禁用按权限不足拒绝；
/// 绝不把空身份当作匿名或管理员，也绝不缓存（每次请求重新查询），账号停用 / 删除后下一次请求立即收敛。</item>
/// <item><b>既有菜单授权</b>：普通账号必须实时具备既有「销售订单」（<c>sales-order</c>）功能菜单
/// （导出专用菜单 <c>sales-order-export</c> 绝不当作模块权限）；特权账号（超级管理员 / 系统内置角色 / 显式特权角色）
/// 沿用既有全部访问口径，不额外要求逐条菜单授权。绝不新增任何菜单 / 角色 / 用户授权。</item>
/// <item><b>权威客户范围</b>：只复用唯一权威口径 <see cref="SalespersonDataScopeService"/>（ERP-097）：
/// 拟议客户（新增 / 改派）必须在范围内，持久化订单的 <see cref="SalesOrder.CustomerId"/> 复核同理；
/// 归属只按持久化 <c>CustomerId</c> 判定 —— 绝不按来源单号 / 客户名 / 业务员快照或相似度推断，
/// 也绝不把旧库 <c>Oid</c> 推断为规范 <c>Id</c>。</item>
/// <item><b>非披露错误</b>：范围外订单、已删除订单与不存在订单返回**同一**「销售订单不存在」受控错误
/// （既不在授权前读取任何明细 / 状态，也不返回计数或部分行透露不可访问订单的存在性）。</item>
/// </list>
/// <para><b>边界</b>：本类只做纯判定与有界只读查询，不落库、不改写销售订单 / 出库 / 退货 / 收款 / 发票 / 财务 / 库存记录，
/// 不删除历史证据与审计留痕，也不新增表 / 列 / 索引 / 菜单 / 角色 / 用户授权。调用方（控制器）负责在任何写入
/// （含单号预约 / 明细替换 / 状态变更 / 软删除）之前调用；写入路径不做「无请求路径 / 匿名进程内调用」豁免。ERP-465：只读执行证据 / 列表 / 详情入口的实时授权门同样与请求形状无关（空路径与已赋值路径口径一致），写入路径口径保持不变。</para>
/// </summary>
public static class SalesOrderMutationAuthorizationRules
{
    /// <summary>规范销售订单模块复用的既有菜单编码（与 <c>SeedData.Menus</c> 同源，绝不新增菜单）。</summary>
    public const string RequiredMenuCode = SalesOrderExecutionAuthorizationRules.RequiredMenuCode;

    /// <summary>销售订单模块菜单中文文案（与既有菜单名一致）。</summary>
    public const string RequiredMenuText = SalesOrderExecutionAuthorizationRules.RequiredMenuText;

    /// <summary>无身份 / 非法身份的拒绝文案。</summary>
    public const string UnauthorizedText = "请先登录后再保存销售订单";

    /// <summary>账号不存在 / 已删除的拒绝文案。</summary>
    public const string UserDeletedText = "登录账号不存在或已删除，禁止保存销售订单";

    /// <summary>账号已禁用的拒绝文案。</summary>
    public const string UserDisabledText = "登录账号已禁用，禁止保存销售订单（fail closed）";

    /// <summary>缺少既有「销售订单」菜单授权时的拒绝文案（导出菜单绝不替代功能菜单）。</summary>
    public const string MenuDeniedText =
        "当前账号没有「销售订单」（sales-order）模块授权：拒绝保存销售订单"
        + "（fail closed，不预约单号、不写入任何数据）";

    /// <summary>
    /// 范围外 / 已删除 / 不存在订单的统一非披露错误文案：三者返回同一错误，绝不通过差异化的
    /// 错误 / 计数 / 部分行透露不可访问订单的存在性。
    /// </summary>
    public const string OrderDeniedText = "销售订单不存在";

    /// <summary>拟议客户越界（新增 / 改派）的拒绝文案。</summary>
    public const string ProposedCustomerDeniedText =
        "拟议客户不在当前账号的客户数据范围内：拒绝保存销售订单"
        + "（fail closed，不预约单号、不写入任何数据，也不泄露越界客户）";

    /// <summary>授权与数据范围口径文案（接口 / 文档同源）。</summary>
    public const string RuleText =
        "销售订单写入护栏：新增 / 修改 / 删除 / 提交 / 审核 / 取消在预约单号、替换明细、状态流转或软删除之前，"
        + "都重新校验实时身份（缺失 / 非法 / 已删除按未认证拒绝，已禁用按权限不足拒绝）、既有「销售订单」"
        + "（sales-order）功能菜单授权（非特权账号必须显式具备）与 SalespersonDataScopeService（ERP-097 唯一权威口径）"
        + "客户数据范围；拟议客户（新增 / 改派）与持久化订单客户都必须落在实时范围内，归属只按持久化 CustomerId 判定；"
        + "范围外 / 已删除 / 不存在订单返回同一受控「销售订单不存在」错误；写入路径不做无请求路径 / 匿名进程内调用豁免，"
        + "也绝不新增权限模型、绝不把空身份当作管理员、绝不把旧库 Oid 推断为规范 Id。";

    /// <summary>边界文案（不改变既有来源血缘 / 转换资格 / 下游护栏，也不改写任何业务 / 库存 / 财务记录）。</summary>
    public const string BoundaryText =
        "本护栏只保护规范销售订单写入入口：不改写数量 / 单价 / 金额 / 合计 / 定金 / 币种 / 汇率 / 单位等原始商业语义，"
        + "不新增表 / 列 / 索引 / 菜单 / 角色 / 用户授权，不删除历史证据与审计留痕；既有显式来源授权 / 转换资格 / 唯一目标 / "
        + "下游冻结与取消护栏保持不变，拒绝时绝不产生任何库存 / 财务副作用。";

    /// <summary>
    /// 写入入口的实时身份校验（fail closed）：缺失 / 非法身份按未认证拒绝，账号不存在 / 已删除按未认证拒绝，
    /// 已禁用按权限不足拒绝；随后复用 <see cref="SalespersonDataScopeService"/>（ERP-097）解析权威客户范围。
    /// </summary>
    public static async Task<SalespersonDataScope> EnsureLiveIdentityAsync(
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

        return await SalespersonDataScopeService.ResolveAsync(db, userId.Value);
    }

    /// <summary>
    /// 写入入口的完整授权（实时身份 + 既有「销售订单」菜单 + ERP-097 权威客户数据范围）。
    /// 特权账号豁免菜单校验但仍须通过实时身份校验；每次调用都重新查询（无缓存），授权撤销 / 账号停用后立即收敛。
    /// </summary>
    public static async Task<SalespersonDataScope> EnsureWriteAuthorizedAsync(
        IErpDbContext db, long? userId, CancellationToken ct = default)
    {
        var scope = await EnsureLiveIdentityAsync(db, userId, ct);
        if (scope.IsPrivileged) return scope;

        var menuCodes = await CustomerReceivableReconciliationService
            .LoadAuthorizedMenuCodesAsync(db, userId!.Value);
        if (!menuCodes.Contains(RequiredMenuCode, StringComparer.OrdinalIgnoreCase))
            throw new BusinessException(MenuDeniedText, ErrorCodes.Forbidden);

        return scope;
    }

    /// <summary>
    /// 拟议客户（新增 / 改派）的权威范围复核：特权账号放行；受限账号下客户 Id 缺失 / 非正 / 越界一律 fail closed 拒绝。
    /// 只按拟议 <c>CustomerId</c> 判定归属，绝不采信来源 / 客户名 / 业务员快照。
    /// </summary>
    public static void EnsureProposedCustomerAllowed(SalespersonDataScope scope, long? customerId)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (scope.AllowedCustomerIds is null) return;
        if (customerId is not > 0 || !scope.AllowsCustomer(customerId.Value))
            throw new BusinessException(ProposedCustomerDeniedText, ErrorCodes.Forbidden);
    }

    /// <summary>
    /// 持久化订单的权威归属复核：按 <c>Id</c> 精确读取**持久化 <see cref="SalesOrder.CustomerId"/>**（最小投影、
    /// 有界只读，绝不装载明细）；不存在 / 已删除 / 范围外一律返回同一非披露错误。返回范围内的持久化客户 Id。
    /// </summary>
    public static async Task<long> EnsureOrderAllowedAsync(
        IErpDbContext db, SalespersonDataScope scope, long orderId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(scope);

        if (orderId <= 0)
            throw BusinessException.NotFound(OrderDeniedText);

        var row = await db.SalesOrders.AsNoTracking()
            .Where(o => o.Id == orderId && !o.IsDeleted)
            .Select(o => new { o.CustomerId })
            .FirstOrDefaultAsync(ct);

        if (row is null || !scope.AllowsCustomer(row.CustomerId))
            throw BusinessException.NotFound(OrderDeniedText);

        return row.CustomerId;
    }
}
