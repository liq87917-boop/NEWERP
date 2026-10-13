using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ERP.Application.Services;

/// <summary>
/// 系统操作日志（<see cref="ERP.Domain.Entities.SysOperationLog"/>，<c>api/sys/logs</c>）实时身份、账号状态与
/// 既有「系统日志」（<c>sys-log</c>）功能菜单授权护栏（ERP-499，阶段 3 访问控制收口）。
/// <list type="number">
/// <item><b>实时授权</b>：分页查询在读取任何 <c>SysOperationLogs</c> 计数或行<b>之前</b>都先解析<b>实时启用身份</b>
/// （缺失 / 非法按未认证拒绝，账号不存在 / 已删除按未认证拒绝，禁用按权限不足拒绝）与既有「系统日志」
/// （<c>sys-log</c>，与 <c>SeedData.Menus</c> 同源）功能菜单授权（每个账号都必须显式具备，撤销后下一次请求立即收敛；
/// 无匿名 / 管理员兜底）。</item>
/// </list>
/// <para><b>身份来源唯一</b>：请求中的任何字段都不能指定或扩大账号身份 —— 身份只来自已认证请求主体
/// （<c>ClaimTypes.NameIdentifier</c>），由调用方（<c>ERP.Api.Controllers.OperationLogController</c>）
/// 解析并传入；<c>null</c> 表示无可用身份，一律 fail closed，绝不代表匿名或管理员。</para>
/// <para><b>与请求形状无关</b>：本判定与请求路径 / 请求形状 / 是否绑定 <c>HttpContext</c> <b>完全无关</b>
/// （<see cref="PathIndependenceText"/>）：空路径与已赋值路径口径完全一致，完全未绑定任何请求上下文的纯进程内
/// 直调同样执行本护栏，<b>不再存在</b>「无请求路径 / 无请求上下文」的授权豁免。</para>
/// <para>边界：本类只做纯判定与有界只读查询；不新增表 / 列 / 实体 / 菜单 / 角色 / 用户授权 / 索引，不引入匿名 /
/// 管理员 / 环境 / 测试专用兜底，也不改变既有分页 / 筛选 / 响应契约与同一文件中其它控制器的行为。</para>
/// </summary>
public static class OperationLogAuthorizationRules
{
    /// <summary>操作日志模块复用的既有菜单编码（与 <c>SeedData.Menus</c> 同源：<c>("system","sys-log","系统日志",…)</c>）</summary>
    public const string RequiredMenuCode = "sys-log";

    /// <summary>操作日志模块菜单中文文案（与既有菜单名一致）</summary>
    public const string RequiredMenuText = "系统日志";

    // ==================== 受控非披露错误文案 ====================

    /// <summary>无身份 / 非法身份的拒绝文案</summary>
    public const string UnauthorizedText = "请先登录后再访问系统日志";

    /// <summary>账号不存在 / 已删除的拒绝文案</summary>
    public const string UserDeletedText = "登录账号不存在或已删除，禁止访问系统日志";

    /// <summary>账号已禁用的拒绝文案</summary>
    public const string UserDisabledText = "登录账号已禁用，禁止访问系统日志（fail closed）";

    /// <summary>缺少既有「系统日志」菜单授权的拒绝文案</summary>
    public const string MenuDeniedText =
        "当前账号没有「系统日志」（sys-log）模块授权：拒绝访问系统日志"
        + "（fail closed，不返回任何日志行、计数、用户名、路径、单据号或请求体）";

    /// <summary>授权口径文案（接口 / 文档同源）</summary>
    public const string RuleText =
        "系统日志（api/sys/logs 分页查询）在读取任何 SysOperationLogs 计数或行之前，都会重新校验实时启用身份"
        + "（缺失 / 非法 / 账号不存在 / 已删除按未认证，禁用按权限不足，一律 fail closed）与既有「系统日志」"
        + "（sys-log）功能菜单授权；撤销授权后下一次请求立即收敛，绝不把空身份或特权当作管理员，"
        + "也绝无匿名 / 管理员兜底。";

    /// <summary>与请求路径 / 请求形状 / 请求上下文无关的口径文案（接口 / 文档 / 测试同源）</summary>
    public const string PathIndependenceText =
        "系统日志的实时身份 / 账号状态 / 既有「系统日志」（sys-log）菜单判定与请求路径 / 请求形状 / 是否绑定 "
        + "HttpContext 完全无关：空路径与已赋值路径口径完全一致，未绑定任何请求上下文的纯进程内直调同样执行本护栏；"
        + "不再读取 Request.Path，也不存在任何授权豁免。";

    /// <summary>边界文案（不新增权限 / 表列，不改变既有读契约）</summary>
    public const string BoundaryText =
        "本护栏只保护系统日志分页读取的入口授权：不新增菜单 / 角色 / 用户授权 / 表 / 列 / 索引，无匿名 / 管理员 / "
        + "环境 / 测试专用兜底，也不改变既有分页筛选与 ApiResponse<PagedResult<SysOperationLog>> 响应契约；"
        + "被拒绝的读取不加载、不计数、不返回任何日志行、计数、用户名、路径、单据号或请求体。";

    // ==================== 1. 身份 / 账号状态 / 菜单授权（fail closed） ====================

    /// <summary>
    /// 身份 / 账号状态 / 既有「系统日志」菜单授权实时校验（fail closed）：
    /// 缺失 / 非法身份按未认证拒绝，账号不存在 / 已删除按未认证拒绝，禁用按权限不足拒绝，
    /// 缺少既有 <c>sys-log</c> 菜单授权按权限不足拒绝。
    /// 每次调用都重新查询（无缓存），账号停用 / 删除或菜单撤销后下一次请求立即收敛；
    /// 每个账号都必须显式具备既有功能菜单，<b>无特权 / 管理员兜底</b>。
    /// <para>该判定只读取身份 / 账号 / 菜单相关数据集，<b>绝不</b>读取 <c>SysOperationLogs</c>：
    /// 拒绝发生在任何日志计数或行读取之前。调用形状（请求路径、是否绑定 <c>HttpContext</c>）对结果无影响。</para>
    /// </summary>
    public static async Task EnsureAuthorizedAsync(IErpDbContext db, long? userId, CancellationToken ct = default)
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

        var menuCodes = await CustomerReceivableReconciliationService
            .LoadAuthorizedMenuCodesAsync(db, userId.Value);
        if (!menuCodes.Contains(RequiredMenuCode, StringComparer.OrdinalIgnoreCase))
            throw new BusinessException(MenuDeniedText, ErrorCodes.Forbidden);
    }
}
