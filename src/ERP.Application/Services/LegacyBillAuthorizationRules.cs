using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ERP.Application.Services;

/// <summary>
/// 旧单据（BillProc 通用控制器 <c>api/v2/bills</c>）的<strong>实时身份 + 既有模块授权</strong>护栏（ERP-404）。
/// <list type="number">
/// <item><b>实时身份</b>：缺失 / 非法身份按未认证拒绝（fail closed），账号不存在 / 已删除按未认证拒绝，
/// 账号已禁用按权限不足拒绝；绝不把空身份当作匿名或管理员，也不缓存（每次请求重新查询）。</item>
/// <item><b>既有模块授权</b>：普通账号必须实时具备该单据族<b>既有功能菜单</b>
/// （<c>seed</c>：<c>SalesOrder</c> / <c>PurchaseOrder</c> / <c>Inquiry</c> / <c>StockIn</c> / <c>StockOut</c> /
/// <c>FinanceReceipt</c> / <c>FinancePayment</c> / <c>FinanceDepositApply</c> / <c>FinancePaymentApply</c> /
/// <c>FinanceContainerSettlement</c> / <c>FinanceBulkSettlement</c> / <c>FinanceComplaint</c> /
/// <c>ContainerReceivingPlan</c> / <c>ContainerBooking</c> / <c>ContainerPreLoading</c> /
/// <c>ContainerLoadingList</c> 对应的中文菜单）；<b>绝不</b>把「导出」类菜单（<c>*-export</c>）当模块权限，
/// 也绝不新增任何菜单 / 角色 / 用户授权。特权账号（超级管理员 / 系统内置角色）沿用既有全部访问口径。</item>
/// <item><b>权威范围复用</b>：客户数据范围只复用唯一权威口径
/// <see cref="SalespersonDataScopeService"/>（ERP-097），本类不重新定义范围语义。</item>
/// </list>
/// <para><b>边界</b>：本类只做纯判定与有界只读查询，不落库、不调用任何存储过程、不写日志、不发送通知、
/// 不改写任何来源单据 / 库存 / 财务记录；调用方（控制器）负责在读取计数、生成单号或写入任何数据之前调用。</para>
/// </summary>
public static class LegacyBillAuthorizationRules
{
    /// <summary>无身份 / 非法身份的拒绝文案。</summary>
    public const string UnauthorizedText = "请先登录后再访问旧单据路由（fail closed）";

    /// <summary>账号不存在 / 已删除的拒绝文案。</summary>
    public const string UserDeletedText = "登录账号不存在或已删除，禁止访问旧单据路由（fail closed）";

    /// <summary>账号已禁用的拒绝文案。</summary>
    public const string UserDisabledText = "登录账号已禁用，禁止访问旧单据路由（fail closed）";

    /// <summary>授权与数据范围口径文案（接口 / 文档同源）。</summary>
    public const string RuleText =
        "旧单据路由（存单 / 状态流转 / Excel 导入）在读取任何计数、生成 / 消耗单号、调用任何 sp_Biz_* 或写入任何数据之前，"
        + "都重新校验实时身份（缺失 / 非法 / 已删除按未认证拒绝，已禁用按权限不足拒绝）、该单据族既有功能菜单授权与业务员数据范围；"
        + "导出类菜单（*-export）绝不当作模块权限，也绝不新增任何用户授权或提供匿名 / 管理员降级。";

    /// <summary>缺少既有功能菜单授权时的拒绝文案（按族拼装，导出菜单绝不替代功能菜单）。</summary>
    public static string MenuDeniedText(string requiredMenuCode, string requiredMenuText)
        => $"当前账号没有「{requiredMenuText}」（{requiredMenuCode}）模块授权：拒绝访问旧单据路由"
           + "（fail closed，不执行任何写入、不返回任何数据）";

    /// <summary>
    /// 实时身份校验（fail closed）：返回当前账号的既有数据范围（供写 / 读两侧复用），
    /// <c>userId</c> 缺失 / 非法按未认证拒绝，账号不存在 / 已删除按未认证拒绝，账号已禁用按权限不足拒绝。
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
    /// 既有功能模块授权校验（fail closed）：先复用 <see cref="EnsureLiveIdentityAsync"/> 的实时身份口径，
    /// 再要求普通账号实时具备 <paramref name="requiredMenuCode"/> 对应的既有功能菜单
    /// （<b>绝不</b>把导出菜单当模块权限）；特权账号沿用既有全部访问口径。授权撤销或账号停用后下一次请求立即收敛。
    /// </summary>
    public static async Task<SalespersonDataScope> EnsureModuleAuthorizedAsync(
        IErpDbContext db, long? userId, string requiredMenuCode, string requiredMenuText,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(requiredMenuCode))
            throw new ArgumentException("必须提供既有功能模块菜单编码", nameof(requiredMenuCode));

        var scope = await EnsureLiveIdentityAsync(db, userId, ct);

        // 特权账号（超级管理员 / 系统内置角色）沿用既有全部访问口径；其余账号必须实时具备既有功能菜单。
        if (scope.IsPrivileged)
            return scope;

        var menuCodes = await CustomerReceivableReconciliationService
            .LoadAuthorizedMenuCodesAsync(db, userId!.Value);
        if (!menuCodes.Contains(requiredMenuCode, StringComparer.OrdinalIgnoreCase))
            throw new BusinessException(MenuDeniedText(requiredMenuCode, requiredMenuText), ErrorCodes.Forbidden);

        return scope;
    }
}
