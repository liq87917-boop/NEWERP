using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ERP.Application.Services;

/// <summary>
/// 菜单记录（<see cref="SysMenu"/>，<c>api/sys/menus</c>）的实时身份 / 既有功能菜单授权、
/// 有界字段校验与父级完整性护栏（ERP-455）。菜单编码（<c>SysMenu.MenuCode</c>）是每一条运营路由
/// 菜单授权所校验的权威、也是 <see cref="AuthService"/> 为每个登录账号构建菜单树时读取的来源，
/// 因此定义菜单的维护端点必须像其它主数据一样 fail closed。
/// <list type="number">
/// <item><b>实时授权</b>：读树（<see cref="EnsureReadAuthorizedAsync"/>）与新增 / 修改 / 删除
/// （<see cref="EnsureWriteAuthorizedAsync"/>）在读取或写入任何 <c>SysMenus</c> 行<b>之前</b>都重新解析实时身份
/// （缺失 / 非法 / 账号不存在或已删除按未认证，禁用按权限不足）与既有功能菜单授权
/// （读树接受既有「用户权限」（<c>user-permission</c>）或既有「角色管理」（<c>role</c>）任一；
/// 新增 / 修改 / 删除只接受既有「用户权限」），一律 fail closed；</item>
/// <item><b>有界字段校验</b>（<see cref="Validate"/>）：新增 / 修改在落库之前校验菜单名称 / 菜单编码非空且在持久化
/// 长度上限内、路由路径 / 图标 / 权限编码不超持久化长度、菜单类型为已知菜单类型、父级 Id 为零或有效正数；</item>
/// <item><b>父级完整性</b>（<see cref="EnsureParentResolvedAsync"/>）：非零父级 Id 必须解析为<b>已知的非删除</b>
/// 菜单，否则按受控参数错误拒绝，被拒绝的写入<b>不落任何</b> <c>SysMenus</c> 行、也<b>不改写</b>任何既有行。</item>
/// </list>
/// <para>边界（重要）：本护栏只新增「读取 / 写入前的判定」，<b>不</b>静默截断 / 改写任何菜单字段，
/// 也<b>不</b>改变既有菜单编码唯一性判定与「存在子菜单不可删除」语义；<b>不</b>新增任何表 / 列 / 实体 / 菜单 /
/// 权限 / 用户授权，<b>不</b>因身份缺失而降级为管理员，也<b>不</b>新增匿名 / 特权旁路。</para>
/// </summary>
public static class SysMenuAuthorizationRules
{
    /// <summary>新增 / 修改 / 删除所需的既有功能菜单编码（与 <c>SeedData.Menus</c> 的 <c>("system","user-permission","用户权限",…)</c> 同源，<b>不新增菜单</b>）</summary>
    public const string WriteRequiredMenuCode = "user-permission";

    /// <summary>新增 / 修改 / 删除所需的既有功能菜单中文文案</summary>
    public const string WriteRequiredMenuText = "用户权限";

    /// <summary>读树可接受的第二个既有功能菜单编码（与 <c>SeedData.Menus</c> 的 <c>("system","role","角色管理",…)</c> 同源，<b>不新增菜单</b>）</summary>
    public const string ReadAlternateMenuCode = "role";

    /// <summary>读树可接受的第二个既有功能菜单中文文案</summary>
    public const string ReadAlternateMenuText = "角色管理";

    /// <summary>菜单名称持久化长度上限（<c>NVARCHAR(50)</c>，与实体 <c>[MaxLength(50)]</c> 一致）</summary>
    public const int MaxMenuNameLength = 50;

    /// <summary>菜单编码持久化长度上限（<c>NVARCHAR(50)</c>，与实体 <c>[MaxLength(50)]</c> 一致，唯一性判定语义不变）</summary>
    public const int MaxMenuCodeLength = 50;

    /// <summary>路由路径持久化长度上限（<c>NVARCHAR(200)</c>，与实体 <c>[MaxLength(200)]</c> 一致）</summary>
    public const int MaxPathLength = 200;

    /// <summary>图标持久化长度上限（<c>NVARCHAR(50)</c>，与实体 <c>[MaxLength(50)]</c> 一致）</summary>
    public const int MaxIconLength = 50;

    /// <summary>权限编码持久化长度上限（<c>NVARCHAR(100)</c>，与实体 <c>[MaxLength(100)]</c> 一致）</summary>
    public const int MaxPermissionCodeLength = 100;

    /// <summary>无身份 / 非法身份的拒绝文案（受控、不泄露数据）</summary>
    public const string UnauthorizedText = "请先登录后再访问菜单管理";

    /// <summary>账号不存在 / 已删除的拒绝文案（受控、不泄露数据）</summary>
    public const string UserDeletedText = "登录账号不存在或已删除，禁止访问菜单管理";

    /// <summary>账号已禁用的拒绝文案（受控、不泄露数据）</summary>
    public const string UserDisabledText = "登录账号已禁用，禁止访问菜单管理（fail closed）";

    /// <summary>缺少读树所需的既有「用户权限」/「角色管理」菜单授权时的拒绝文案（受控、不泄露数据）</summary>
    public const string ReadMenuDeniedText =
        "当前账号没有「用户权限」（user-permission）或「角色管理」（role）模块授权：" +
        "拒绝读取菜单树（fail closed，不返回任何菜单记录）";

    /// <summary>缺少既有「用户权限」菜单授权时的拒绝文案（受控、不泄露数据）</summary>
    public const string WriteMenuDeniedText =
        "当前账号没有「用户权限」（user-permission）模块授权：" +
        "拒绝维护菜单（fail closed，不返回 / 不新增 / 不改写 / 不删除任何菜单）";

    /// <summary>授权口径文案（接口 / 文档同源）</summary>
    public const string AuthorizationRuleText =
        "菜单管理（读树 / 新增 / 修改 / 删除）在读取或写入任何菜单之前，都会重新校验实时身份" +
        "（缺失 / 非法 / 已删除按未认证，禁用按权限不足，一律 fail closed）与既有功能菜单授权：" +
        "读树接受既有「用户权限」（user-permission）或既有「角色管理」（role）任一，新增 / 修改 / 删除只接受" +
        "既有「用户权限」；菜单授权复用既有「角色 → 菜单」口径，每次请求重新查询，撤销后下一次请求立即收敛。";

    /// <summary>授权边界文案（不改写既有业务口径）</summary>
    public const string AuthorizationBoundaryText =
        "本护栏只新增「读取 / 写入前的授权与有界字段 / 父级校验」：不改变菜单编码唯一性判定与" +
        "「存在子菜单不可删除」语义，也不静默截断 / 改写任何菜单字段；" +
        "不新增表 / 列 / 实体 / 菜单 / 权限 / 用户授权，也不把空身份当作管理员。";

    /// <summary>
    /// 读树实时授权（fail closed）：实时身份 / 账号状态 / 既有「用户权限」或「角色管理」菜单三项校验，
    /// 返回本次请求的权威用户 Id。
    /// <list type="bullet">
    /// <item>身份缺失 / 非法 → <see cref="ErrorCodes.Unauthorized"/>；</item>
    /// <item>账号不存在或已删除 → <see cref="ErrorCodes.Unauthorized"/>；</item>
    /// <item>账号已禁用 → <see cref="ErrorCodes.Forbidden"/>；</item>
    /// <item>缺少既有「用户权限」或「角色管理」菜单授权（含被撤销授权）→ <see cref="ErrorCodes.Forbidden"/>。</item>
    /// </list>
    /// </summary>
    public static Task<long> EnsureReadAuthorizedAsync(
        IErpDbContext db, long? userId, CancellationToken ct = default)
        => EnsureAuthorizedAsync(db, userId,
            new[] { WriteRequiredMenuCode, ReadAlternateMenuCode }, ReadMenuDeniedText, ct);

    /// <summary>
    /// 新增 / 修改 / 删除实时授权（fail closed）：实时身份 / 账号状态 / 既有「用户权限」菜单三项校验，
    /// 返回本次请求的权威用户 Id。判定发生在任何菜单读取 / 写入<b>之前</b>；每次请求重新解析，
    /// 菜单或账号状态变更后立即收敛；<b>不</b>新增任何菜单 / 角色 / 用户授权，
    /// 也<b>不</b>因身份缺失而降级为管理员。
    /// </summary>
    public static Task<long> EnsureWriteAuthorizedAsync(
        IErpDbContext db, long? userId, CancellationToken ct = default)
        => EnsureAuthorizedAsync(db, userId,
            new[] { WriteRequiredMenuCode }, WriteMenuDeniedText, ct);

    /// <summary>身份 / 账号状态 / 既有功能菜单三项校验（fail closed）的公共实现。</summary>
    private static async Task<long> EnsureAuthorizedAsync(
        IErpDbContext db, long? userId, string[] requiredMenuCodes, string menuDeniedText, CancellationToken ct)
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

        if (!requiredMenuCodes.Any(code => menuCodes.Contains(code, StringComparer.OrdinalIgnoreCase)))
            throw new BusinessException(menuDeniedText, ErrorCodes.Forbidden);

        return user.Id;
    }

    /// <summary>
    /// 新增 / 修改菜单载荷的有界校验（落库之前调用）：
    /// <list type="bullet">
    /// <item>菜单编码：非空且 ≤ <see cref="MaxMenuCodeLength"/>（唯一性由调用方在同一非删除集合上判定）；</item>
    /// <item>菜单名称：非空且 ≤ <see cref="MaxMenuNameLength"/>；</item>
    /// <item>路由路径：不超 <see cref="MaxPathLength"/>；图标：不超 <see cref="MaxIconLength"/>；
    /// 权限编码：不超 <see cref="MaxPermissionCodeLength"/>；</item>
    /// <item>菜单类型：必须为已知菜单类型（<see cref="MenuType"/> 已定义值）；</item>
    /// <item>父级 Id：零或有效正数（非零是否解析为已知非删除菜单由 <see cref="EnsureParentResolvedAsync"/> 判定）。</item>
    /// </list>
    /// 任一项不满足即按 <see cref="ErrorCodes.InvalidParameter"/> 的受控错误拒绝，<b>不</b>静默截断或改写任何字段。
    /// </summary>
    public static void Validate(SysMenu? menu)
    {
        if (menu is null)
            throw BusinessException.InvalidParameter("菜单数据不能为空");

        EnsureRequiredText(menu.MenuCode, MaxMenuCodeLength, "菜单编码");
        EnsureRequiredText(menu.MenuName, MaxMenuNameLength, "菜单名称");
        EnsureOptionalText(menu.Path, MaxPathLength, "路由路径");
        EnsureOptionalText(menu.Icon, MaxIconLength, "图标");
        EnsureOptionalText(menu.PermissionCode, MaxPermissionCodeLength, "权限编码");

        if (!Enum.IsDefined(typeof(MenuType), menu.MenuType))
            throw BusinessException.InvalidParameter("菜单类型必须为已知的菜单类型（目录 / 菜单 / 按钮）");

        EnsureParentShape(menu.ParentId);
    }

    /// <summary>
    /// 父级完整性校验（新增 / 修改在落库之前调用）：<c>ParentId</c> 为 <c>0</c> 时表示根菜单，直接放行；
    /// 否则必须为有效正数且解析为<b>已知的非删除</b>菜单；不满足即按 <see cref="ErrorCodes.InvalidParameter"/>
    /// 拒绝，伪造 / 未知 / 已删除的父级<b>绝不</b>持久化任何 <c>SysMenus</c> 行。
    /// </summary>
    public static async Task EnsureParentResolvedAsync(
        IErpDbContext db, long parentId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ct.ThrowIfCancellationRequested();

        EnsureParentShape(parentId);
        if (parentId == 0) return;

        var known = await db.SysMenus.AsNoTracking()
            .AnyAsync(m => m.Id == parentId && !m.IsDeleted, ct);
        if (!known)
            throw BusinessException.InvalidParameter("父级菜单不存在或已删除，禁止挂接");
    }

    /// <summary>父级 Id 形状校验：零（根）或有效正数；负数即拒绝（不做静默改写）。</summary>
    private static void EnsureParentShape(long parentId)
    {
        if (parentId < 0)
            throw BusinessException.InvalidParameter("父级菜单 Id 必须为零或有效的正数");
    }

    /// <summary>必填文本校验：空白即拒绝，超过持久化长度上限即拒绝（不做静默截断）。</summary>
    private static void EnsureRequiredText(string? value, int maxLength, string fieldText)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw BusinessException.InvalidParameter($"{fieldText}不能为空");
        if (value.Length > maxLength)
            throw BusinessException.InvalidParameter($"{fieldText}长度不能超过 {maxLength} 个字符");
    }

    /// <summary>可选文本校验：仅在提供且超过持久化长度上限时拒绝（不做静默截断）。</summary>
    private static void EnsureOptionalText(string? value, int maxLength, string fieldText)
    {
        if (value is not null && value.Length > maxLength)
            throw BusinessException.InvalidParameter($"{fieldText}长度不能超过 {maxLength} 个字符");
    }

}
