using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ERP.Application.Services;

/// <summary>
/// 基础资料导入导出（<c>api/base/io/{resource}/export|import-template|import</c>）的实时身份、
/// 既有功能菜单与业务员客户数据范围护栏（ERP-440）。
/// <list type="number">
/// <item><b>实时身份</b>：每次请求按已认证主体 <c>ClaimTypes.NameIdentifier</c> 解析，缺失 / 非法 →
/// 未认证；账号不存在或已 <c>IsDeleted</c> → 未认证；<c>Status != Enabled</c> → 权限不足。</item>
/// <item><b>既有功能菜单</b>：非特权账号必须显式具备该基础资料<b>既有</b>功能菜单
/// （客户 / 供应商 / 员工 / 费用科目 / 仓库 / 商品 / 出口退税台账 / 其他资料），
/// 在读取 / 计数 / 写入任何行<b>之前</b>校验；<b>不新增任何菜单 / 角色 / 用户授权</b>，也无匿名 / 管理员兜底。</item>
/// <item><b>权威客户范围</b>：复用 ERP-097 <see cref="SalespersonDataScopeService"/> 唯一口径，
/// 受限账号的客户导出只返回本人客户，导入的客户行业务员必须是本人（越界行逐行拒绝）。</item>
/// </list>
/// <para>边界：本类只做纯判定与有界只读查询，不落库、不改写任何基础资料，
/// 不新增表 / 列 / 菜单 / 权限模型，也不把空身份当作管理员。</para>
/// </summary>
public static class BaseDataIoAuthorizationRules
{
    /// <summary>单个基础资料资源的授权描述（资源键 → 既有功能菜单；<see cref="CustomerScoped"/> 标记客户数据范围）。</summary>
    public sealed record ResourceAuthority(string Resource, string MenuCode, string MenuText, bool CustomerScoped);

    /// <summary>资源键到既有功能菜单的映射（与 <c>SeedData.Menus</c> / <c>SchemaUpgrader</c> 同源，不新增菜单）。</summary>
    private static readonly Dictionary<string, ResourceAuthority> Authorities =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["customers"] = new("customers", "customer", "客户资料", true),
            ["suppliers"] = new("suppliers", "supplier", "供应商资料", false),
            ["employees"] = new("employees", "employee", "员工资料", false),
            ["expense-accounts"] = new("expense-accounts", "expense-account", "费用科目", false),
            ["warehouses"] = new("warehouses", "warehouse", "仓库资料", false),
            ["products"] = new("products", "product", "商品资料", false),
            ["tax-refunds"] = new("tax-refunds", "tax-refund", "出口退税台账", false),
            ["other-infos"] = new("other-infos", "other-info", "其他资料", false),
        };

    /// <summary>无身份 / 非法身份的拒绝文案（受控、不泄露数据）</summary>
    public const string UnauthorizedText = "请先登录后再访问基础资料导入导出";

    /// <summary>账号不存在 / 已删除的拒绝文案（受控、不泄露数据）</summary>
    public const string UserDeletedText = "登录账号不存在或已删除，禁止访问基础资料导入导出";

    /// <summary>账号已禁用的拒绝文案（受控、不泄露数据）</summary>
    public const string UserDisabledText = "登录账号已禁用，禁止访问基础资料导入导出（fail closed）";

    /// <summary>客户行业务员越界（范围外）的拒绝文案（fail closed，不改写任何客户）</summary>
    public const string CustomerRowOutOfScopeText =
        "客户所属业务员超出当前账号的客户数据范围：该行被拒绝" +
        "（fail closed，不新增 / 不改写任何客户）";

    /// <summary>受支持的基础资料资源键集合</summary>
    public static IReadOnlyCollection<string> SupportedResources => Authorities.Keys;

    /// <summary>解析资源键对应的授权描述；未知资源返回 <c>false</c>（由调用方保持既有「不支持该基础资料」契约）。</summary>
    public static bool TryResolve(string? resource, out ResourceAuthority authority)
    {
        authority = null!;
        if (string.IsNullOrWhiteSpace(resource))
            return false;
        if (Authorities.TryGetValue(resource.Trim(), out var found))
        {
            authority = found;
            return true;
        }
        return false;
    }

    /// <summary>缺少既有功能菜单的拒绝文案（受控、不泄露数据）</summary>
    public static string MenuDeniedText(ResourceAuthority authority)
    {
        ArgumentNullException.ThrowIfNull(authority);
        return $"当前账号没有「{authority.MenuText}」（{authority.MenuCode}）模块授权：" +
               $"拒绝访问「{authority.MenuText}」导入导出（fail closed，不返回 / 不新增 / 不改写任何基础资料）";
    }

    /// <summary>
    /// 身份 / 账号状态 / 既有功能菜单三重校验（fail closed），返回资源授权描述与本次请求的权威数据范围。
    /// <list type="bullet">
    /// <item>身份缺失 / 非法 → <see cref="ErrorCodes.Unauthorized"/>；</item>
    /// <item>账号不存在或已删除 → <see cref="ErrorCodes.Unauthorized"/>；</item>
    /// <item>账号已禁用 → <see cref="ErrorCodes.Forbidden"/>；</item>
    /// <item>非特权账号无该资源既有功能菜单授权 → <see cref="ErrorCodes.Forbidden"/>。</item>
    /// </list>
    /// 全部判定发生在任何实体读取 / 计数 / 写入之前；每次请求重新解析，菜单或账号状态变更后立即收敛；
    /// 特权账号继承既有全部访问（与 ERP-097 同源），但绝不因为身份缺失而降级为管理员。
    /// </summary>
    public static async Task<(ResourceAuthority Authority, SalespersonDataScope Scope)> EnsureAuthorizedAsync(
        IErpDbContext db, long? userId, string resource, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ct.ThrowIfCancellationRequested();

        if (!TryResolve(resource, out var authority))
            throw BusinessException.InvalidParameter("不支持该基础资料的导入导出");

        if (userId is null or <= 0)
            throw new BusinessException(UnauthorizedText, ErrorCodes.Unauthorized);

        var user = await db.SysUsers.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == userId.Value && !u.IsDeleted, ct);
        if (user is null)
            throw new BusinessException(UserDeletedText, ErrorCodes.Unauthorized);
        if (user.Status != UserStatus.Enabled)
            throw new BusinessException(UserDisabledText, ErrorCodes.Forbidden);

        var scope = await SalespersonDataScopeService.ResolveAsync(db, userId.Value);

        // 特权账号继承既有全部访问（与 ERP-097 同源）；普通账号必须显式具备该资源既有功能菜单。
        if (!scope.IsPrivileged)
        {
            var menuCodes = await CustomerReceivableReconciliationService
                .LoadAuthorizedMenuCodesAsync(db, userId.Value);
            if (!menuCodes.Contains(authority.MenuCode, StringComparer.OrdinalIgnoreCase))
                throw new BusinessException(MenuDeniedText(authority), ErrorCodes.Forbidden);
        }

        return (authority, scope);
    }

    /// <summary>
    /// 单行客户（导入新增行）是否落在当前账号的权威客户范围内：特权账号恒为 <c>true</c>；
    /// 受限账号只有当客户业务员 <paramref name="empId"/> 等于本人映射员工 Id 时才放行（fail closed）。
    /// </summary>
    public static bool AllowsCustomerRow(SalespersonDataScope scope, long? empId)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (scope.AllowedCustomerIds is null)
            return true;
        return scope.SalesmanId is > 0 && empId == scope.SalesmanId;
    }

    /// <summary>单行客户导入的范围守卫：越界（业务员非本人 / 未指定业务员）一律拒绝（fail closed）。</summary>
    public static void EnsureCustomerRowInScope(SalespersonDataScope scope, long? empId)
    {
        if (AllowsCustomerRow(scope, empId))
            return;
        throw new BusinessException(CustomerRowOutOfScopeText, ErrorCodes.Forbidden);
    }
}
