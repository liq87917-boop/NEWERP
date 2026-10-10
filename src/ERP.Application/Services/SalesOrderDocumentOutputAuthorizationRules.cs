using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ERP.Application.Services;

/// <summary>
/// 规范销售订单（<c>api/sales-orders</c>）**文档输出入口**的实时授权与权威客户范围护栏（ERP-424）：
/// 打印（<c>{id}/print</c>）、JSON 单据导出（<c>export</c>）与 Excel 导出（<c>export-excel</c>）。
/// <para>背景：这三个入口此前不解析实时身份 / 既有菜单 / 客户范围 —— 打印只按 <c>Id</c> 猜单即返回主表 + 明细；
/// JSON 导出返回匹配到的**全部**订单与明细（无客户范围）；Excel 导出只用 <see cref="SalespersonDataScopeService"/>
/// 解析范围，既不校验账号是否存在 / 已删除 / 已禁用，也不施加任何既有菜单授权。任何已登录账号只要拿到一张订单 Id
/// 或直接导出，就能读取范围外订单的合同 / 明细 / 金额 / 币种。本类把这三种规范化文档输出的授权统一抽到 Application 层。</para>
/// <list type="number">
/// <item><b>实时身份</b>：<c>userId</c> 缺失 / 非法（非正整数）按未认证拒绝，账号不存在 / 已删除按未认证拒绝，
/// 账号已禁用按权限不足拒绝；绝不把空身份当作匿名或管理员，也绝不缓存（每次请求重新查询），账号停用 / 删除 /
/// 授权撤销后下一次请求立即收敛。</item>
/// <item><b>既有菜单授权</b>：普通账号必须实时同时具备既有「销售订单」功能菜单（<c>sales-order</c>）与既有
/// 「销售订单导出」导出菜单（<c>sales-order-export</c>）—— 与既有受控导出族目录
/// （<see cref="LegacyBillExportCatalog"/> 的 sales-order 族）以及既有销售单据打印模板族的菜单口径**逐字一致**；
/// 仅持有导出菜单绝不构成模块权限（export-only menu never grants base order access），反之亦然。
/// 特权账号（超级管理员 / 系统内置角色 / 显式特权角色）沿用既有全部访问口径，但仍须通过实时身份校验。
/// 绝不新增任何菜单 / 角色 / 用户授权。</item>
/// <item><b>权威客户范围</b>：只复用唯一权威口径 <see cref="SalespersonDataScopeService"/>（ERP-097），
/// 并在 <c>Count</c> / 关键字 / 状态 / 日期过滤与任何物化**之前**把客户范围下推到数据库（<see cref="ApplyCustomerScope"/>）；
/// 归属严格按**持久化 <see cref="SalesOrder.CustomerId"/></b> 判定 —— 绝不按单号 / 客户名 / 金额或相似度推断，
/// 也绝不把旧库 <c>Oid</c> 推断为规范 <c>Id</c>。</item>
/// <item><b>非披露错误</b>：范围外订单、已删除订单与不存在订单返回**同一**「销售订单不存在」受控错误
/// （既不在授权前读取任何明细 / 金额，也不返回计数、部分行或导出字节透露不可访问订单的存在性）；
/// 拒绝路径绝不返回任何订单 Id / 明细 / 输出字节。</item>
/// <item><b>无豁免</b>：文档输出入口**不做**「无请求路径 / 匿名进程内调用」豁免；
/// ERP-465 起只读执行证据 / 列表 / 详情入口同样只要绑定到 HTTP 请求管线（空路径或已赋值路径口径一致）就一律实时授权，与本文档输出入口口径对齐；直接调用控制器同样 fail closed。</item>
/// </list>
/// <para><b>边界</b>：本类只做纯判定与有界只读查询，不落库、不改写销售订单 / 明细 / 出库 / 退货 / 收款 / 发票 /
/// 财务 / 库存记录，不新增表 / 列 / 索引 / 菜单 / 权限模型，不新增任何用户授权，也不改变既有 JSON / Excel 列、
/// 打印字段契约与原始金额 / 币种 / 单位口径。调用方（控制器）负责在任何读取与物化之前调用。</para>
/// </summary>
public static class SalesOrderDocumentOutputAuthorizationRules
{

    /// <summary>
    /// 规范销售订单模块复用的既有**功能菜单**编码（与 <see cref="SalesOrderExecutionAuthorizationRules.RequiredMenuCode"/>
    /// 及 <c>SeedData.Menus</c> 同源，绝不新增菜单）。
    /// </summary>
    public const string FunctionalMenuCode = SalesOrderExecutionAuthorizationRules.RequiredMenuCode;

    /// <summary>销售订单模块菜单中文文案（与既有菜单名一致）。</summary>
    public const string FunctionalMenuText = SalesOrderExecutionAuthorizationRules.RequiredMenuText;

    /// <summary>
    /// 既有**销售订单导出**菜单编码（与 <c>SeedData.Menus</c> / <see cref="LegacyBillExportCatalog"/> 的
    /// sales-order 族同源）。**仅持有**该菜单绝不构成模块权限（不授予任何基础订单访问）。
    /// </summary>
    public const string ExportMenuCode = "sales-order-export";

    /// <summary>销售订单导出菜单中文文案（与既有菜单名一致）。</summary>
    public const string ExportMenuText = "销售订单导出";

    /// <summary>无身份 / 非法身份的拒绝文案。</summary>
    public const string UnauthorizedText = "请先登录后再访问销售订单打印 / 导出";

    /// <summary>账号不存在 / 已删除的拒绝文案。</summary>
    public const string UserDeletedText = "登录账号不存在或已删除，禁止访问销售订单打印 / 导出";

    /// <summary>账号已禁用的拒绝文案。</summary>
    public const string UserDisabledText = "登录账号已禁用，禁止访问销售订单打印 / 导出（fail closed）";

    /// <summary>缺少既有「销售订单」功能菜单授权时的拒绝文案（导出菜单绝不替代功能菜单）。</summary>
    public const string FunctionalMenuDeniedText =
        "当前账号没有「销售订单」（sales-order）模块授权：拒绝打印 / 导出销售订单"
        + "（fail closed，不返回任何订单号 / 计数 / 明细 / 导出字节；仅「销售订单导出」菜单绝不作为模块权限）";

    /// <summary>缺少既有「销售订单导出」导出菜单授权时的拒绝文案（与既有受控导出族菜单口径一致）。</summary>
    public const string ExportMenuDeniedText =
        "当前账号没有「销售订单导出」（sales-order-export）导出菜单授权：拒绝打印 / 导出销售订单"
        + "（fail closed，不返回任何订单号 / 计数 / 明细 / 导出字节）";

    /// <summary>
    /// 范围外 / 已删除 / 不存在订单的统一非披露错误文案（与既有 <c>api/sales-orders/{id}</c> 详情口径一致）：
    /// 三者返回同一错误，绝不通过差异化的错误 / 计数 / 部分行透露不可访问订单的存在性。
    /// </summary>
    public const string NotFoundText = SalesOrderExecutionAuthorizationRules.NotFoundText;

    /// <summary>授权与数据范围口径文案（接口 / 文档同源）。</summary>
    public const string RuleText =
        "销售订单文档输出护栏：打印 / JSON 单据导出 / Excel 导出在读取任何订单 / 明细或物化任何导出字节之前，"
        + "都重新校验实时身份（缺失 / 非法 / 已删除按未认证拒绝，已禁用按权限不足拒绝）、既有「销售订单」"
        + "（sales-order）功能菜单与既有「销售订单导出」（sales-order-export）导出菜单授权"
        + "（与既有受控导出族目录逐字一致，仅导出菜单绝不授予基础订单访问）以及 SalespersonDataScopeService"
        + "（ERP-097 唯一权威口径）客户数据范围；范围在关键字 / 状态 / 日期过滤与物化之前下推到数据库；"
        + "归属只按持久化 CustomerId 判定，范围外 / 已删除 / 不存在订单返回同一受控「销售订单不存在」错误；"
        + "文档输出入口不做无请求路径 / 匿名进程内调用豁免，也绝不新增权限模型、绝不返回任何越界订单 Id / 明细 / 导出字节。";

    /// <summary>边界文案（不改变既有列 / 打印字段契约 / 原始金额币种单位语义，也不改写任何业务 / 库存 / 财务记录）。</summary>
    public const string BoundaryText =
        "本护栏只保护规范销售订单文档输出入口：不改写数量 / 单价 / 金额 / 合计 / 定金 / 币种 / 汇率 / 单位等原始商业语义，"
        + "既有 JSON / Excel 列与打印字段契约、null-as-unknown 与响应 DTO 保持不变；不改写销售订单 / 明细 / 出库 / 退货 / "
        + "收款 / 发票 / 财务 / 客户主数据，不删除历史证据与审计留痕，也不新增表 / 列 / 索引 / 菜单 / 权限或用户授权。";

    /// <summary>
    /// 文档输出入口的完整授权（实时身份 + 既有「销售订单」功能菜单 + 既有「销售订单导出」导出菜单 + ERP-097 权威客户数据范围）。
    /// 特权账号豁免菜单校验但仍须通过实时身份校验；每次调用都重新查询（无缓存），授权撤销 / 账号停用后立即收敛。
    /// 本方法**无进程内 / 无请求路径豁免**：调用方（控制器）必须在任何输出入口**无条件**调用。
    /// </summary>
    public static async Task<SalespersonDataScope> EnsureDocumentOutputAuthorizedAsync(
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
        if (scope.IsPrivileged) return scope;

        var menuCodes = await CustomerReceivableReconciliationService
            .LoadAuthorizedMenuCodesAsync(db, userId.Value);
        if (!menuCodes.Contains(FunctionalMenuCode, StringComparer.OrdinalIgnoreCase))
            throw new BusinessException(FunctionalMenuDeniedText, ErrorCodes.Forbidden);
        if (!menuCodes.Contains(ExportMenuCode, StringComparer.OrdinalIgnoreCase))
            throw new BusinessException(ExportMenuDeniedText, ErrorCodes.Forbidden);

        return scope;
    }

    /// <summary>
    /// 把权威客户范围下推到数据库查询（复用 <see cref="SalespersonDataScopeService.FilterByCustomer"/>）：
    /// 授权通过后、关键字 / 状态 / 日期过滤与任何物化**之前**调用，受限账号只命中范围内父订单，
    /// 绝不「先查全量再内存过滤」。
    /// </summary>
    public static IQueryable<SalesOrder> ApplyCustomerScope(
        IQueryable<SalesOrder> source, SalespersonDataScope scope)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(scope);
        return SalespersonDataScopeService.FilterByCustomer(source, scope, o => o.CustomerId);
    }

    /// <summary>
    /// 单张销售订单的权威归属复核：按 <c>Id</c> 精确读取**持久化 <see cref="SalesOrder.CustomerId"/>**（最小投影、
    /// 有界只读，绝不装载任何明细 / 金额），返回范围内的持久化客户 Id；不存在 / 已删除 / 范围外一律返回同一非披露错误。
    /// 调用方（控制器）在装载主表 + 明细**之前**调用，保证「先取持久化 CustomerId，再取明细」。
    /// </summary>
    public static async Task<long> EnsureOrderAllowedAsync(
        IErpDbContext db, SalespersonDataScope scope, long orderId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(scope);

        if (orderId <= 0)
            throw BusinessException.NotFound(NotFoundText);

        var customerId = await db.SalesOrders.AsNoTracking()
            .Where(o => o.Id == orderId && !o.IsDeleted)
            .Select(o => (long?)o.CustomerId)
            .FirstOrDefaultAsync(ct);

        if (customerId is null || !scope.AllowsCustomer(customerId.Value))
            throw BusinessException.NotFound(NotFoundText);

        return customerId.Value;
    }
}

