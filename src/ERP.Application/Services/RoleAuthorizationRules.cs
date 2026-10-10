using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace ERP.Application.Services;

/// <summary>
/// 角色管理（<see cref="SysRole"/> / <see cref="SysRoleMenu"/>，<c>api/sys/roles</c>）的实时身份 /
/// 既有功能菜单授权、有界字段校验与受控菜单分配护栏（ERP-454）。角色 → 菜单绑定是每一条运营路由
/// 所读取的<b>菜单授权权威</b>（<see cref="AuthService"/> 的
/// <c>BuildCurrentUserAsync</c> / <c>BuildMenuTreeAsync</c> 与每一条运营授权判定都解析它），
/// 因此其管理端点必须像其它主数据一样 fail closed。
/// <list type="number">
/// <item><b>实时授权</b>（<see cref="EnsureAuthorizedAsync"/>）：分页 / 全部 / 按主键读取 / 角色菜单 /
/// 新增 / 修改 / 删除<b>每一</b>路由在读取或写入任何 <c>SysRoles</c> / <c>SysRoleMenus</c> 行<b>之前</b>
/// 都重新解析实时身份（缺失 / 非法 / 账号不存在或已删除按未认证，禁用按权限不足）与既有
/// 「角色管理」（<c>role</c>）功能菜单授权（缺菜单 / 被撤销按权限不足），一律 fail closed；
/// 该判定与 <c>Request.Path</c> 是否赋值、请求形状以及是否绑定 <c>HttpContext</c> <b>完全无关</b>
/// （见 <see cref="PathIndependenceText"/>）；角色 → 菜单批量写入入口同样独立复检；</item>
/// <item><b>有界字段校验</b>（<see cref="Validate"/>）：新增 / 修改在落库之前校验角色名称 / 角色编码
/// 非空且在持久化长度上限内、角色描述不超持久化长度；</item>
/// <item><b>受控菜单分配</b>（<see cref="EnsureMenuIdsResolvedAsync"/>）：每一个被分配的菜单 Id
/// 必须为有效正数且解析为<b>已知的非删除</b>菜单，否则整批按受控参数错误拒绝，
/// 被拒绝的写入<b>不落任何</b> <c>SysRoles</c> / <c>SysRoleMenus</c> 行、也<b>不改写</b>任何既有行。</item>
/// </list>
/// <para>边界（重要）：本护栏只新增「读取 / 写入前的判定」，<b>不</b>静默截断 / 改写任何角色字段，
/// 也<b>不</b>改变既有角色编码唯一索引语义（<c>ErpDbContext.Config</c>）、<c>IsSystem</c> 内置角色删除保护
/// 与「全删全建」角色菜单替换语义；<b>不</b>新增任何表 / 列 / 实体 / 菜单 / 权限 / 用户授权，
/// <b>不</b>因身份缺失而降级为管理员，也<b>不</b>新增匿名 / 特权旁路。</para>
/// </summary>
public static class RoleAuthorizationRules
{
    /// <summary>所需既有功能菜单编码（与 <c>SeedData.Menus</c> 的 <c>("system","role","角色管理",…)</c> 同源，<b>不新增菜单</b>）</summary>
    public const string RequiredMenuCode = "role";

    /// <summary>既有功能菜单中文文案</summary>
    public const string RequiredMenuText = "角色管理";

    /// <summary>角色名称持久化长度上限（<c>NVARCHAR(50)</c>，与实体 <c>[MaxLength(50)]</c> 一致）</summary>
    public const int MaxRoleNameLength = 50;

    /// <summary>角色编码持久化长度上限（<c>NVARCHAR(50)</c>，与实体 <c>[MaxLength(50)]</c> 一致，唯一索引语义不变）</summary>
    public const int MaxRoleCodeLength = 50;

    /// <summary>角色描述持久化长度上限（<c>NVARCHAR(200)</c>，与实体 <c>[MaxLength(200)]</c> 一致）</summary>
    public const int MaxDescriptionLength = 200;

    /// <summary>无身份 / 非法身份的拒绝文案（受控、不泄露数据）</summary>
    public const string UnauthorizedText = "请先登录后再访问角色管理";

    /// <summary>账号不存在 / 已删除的拒绝文案（受控、不泄露数据）</summary>
    public const string UserDeletedText = "登录账号不存在或已删除，禁止访问角色管理";

    /// <summary>账号已禁用的拒绝文案（受控、不泄露数据）</summary>
    public const string UserDisabledText = "登录账号已禁用，禁止访问角色管理（fail closed）";

    /// <summary>缺少既有「角色管理」菜单授权时的拒绝文案（受控、不泄露数据）</summary>
    public const string MenuDeniedText =
        "当前账号没有「角色管理」（role）模块授权：" +
        "拒绝访问角色管理（fail closed，不返回 / 不新增 / 不改写任何角色）";

    /// <summary>授权口径文案（接口 / 文档同源）</summary>
    public const string AuthorizationRuleText =
        "角色管理（分页 / 全部 / 按主键读取 / 角色菜单 / 新增 / 修改 / 删除）在读取或写入任何角色之前，" +
        "都会重新校验实时身份（缺失 / 非法 / 已删除按未认证，禁用按权限不足，一律 fail closed）" +
        "与既有「角色管理」（role）功能菜单授权；菜单授权复用既有「角色 → 菜单」口径，" +
        "每次请求重新查询，撤销后下一次请求立即收敛。";

    /// <summary>授权边界文案（不改写既有业务口径）</summary>
    public const string AuthorizationBoundaryText =
        "本护栏只新增「读取 / 写入前的授权与有界字段 / 菜单校验」：不改变角色编码唯一索引语义、" +
        "IsSystem 内置角色删除保护与全删全建角色菜单替换语义，也不静默截断 / 改写任何角色字段；" +
        "不新增表 / 列 / 实体 / 菜单 / 权限 / 用户授权，也不把空身份当作管理员。";

    /// <summary>
    /// 与请求形状无关的授权契约（ERP-464）：读取 / 写入前的实时身份 / 账号状态 / 既有「角色管理」菜单判定
    /// 必须在<b>每一个入口</b>无条件执行，与 <c>Request.Path</c> 是否赋值、请求形状、以及控制器是否绑定
    /// <c>HttpContext</c> <b>完全无关</b>——空路径与已赋值路径口径完全一致，完全未绑定 <c>HttpContext</c>
    /// 的纯进程内直调同样执行本护栏；缺失 / 零 / 非法 / 已删除身份一律按未认证拒绝，
    /// 禁用账号 / 撤销或缺少既有菜单一律按权限不足拒绝。
    /// </summary>
    public const string PathIndependenceText =
        "角色管理的实时身份 / 账号状态 / 既有「角色管理」（role）菜单判定与请求路径 / 请求形状 / 是否绑定 HttpContext 完全无关：" +
        "空路径与已赋值路径口径完全一致，未绑定任何请求上下文的纯进程内直调同样执行本护栏；" +
        "缺失 / 零 / 非法 / 已删除身份按未认证拒绝，禁用账号与撤销 / 缺少既有菜单按权限不足拒绝；" +
        "绝无匿名 / 管理员回退，不读取环境变量、不区分数据库提供程序，也不存在任何测试专用放行开关。";

    /// <summary>
    /// 实时身份 / 账号状态 / 既有「角色管理」功能菜单三项校验（fail closed），返回本次请求的权威用户 Id。
    /// <list type="bullet">
    /// <item>身份缺失 / 非法 → <see cref="ErrorCodes.Unauthorized"/>；</item>
    /// <item>账号不存在或已删除 → <see cref="ErrorCodes.Unauthorized"/>；</item>
    /// <item>账号已禁用 → <see cref="ErrorCodes.Forbidden"/>；</item>
    /// <item>缺少既有「角色管理」（<c>role</c>）菜单授权（含被撤销授权）→ <see cref="ErrorCodes.Forbidden"/>。</item>
    /// </list>
    /// 判定发生在任何角色读取 / 写入<b>之前</b>；每次请求重新解析，菜单或账号状态变更后立即收敛；
    /// <b>不</b>新增任何菜单 / 角色 / 用户授权，也<b>不</b>因身份缺失而降级为管理员。
    /// <para>与请求形状无关（见 <see cref="PathIndependenceText"/>，ERP-464）：调用方必须在<b>每一个路由入口 /
    /// 每一个角色菜单写入入口</b>无条件调用本方法，不得因为空路径、缺少身份、未绑定 <c>HttpContext</c>
    /// 或任何请求形状而跳过；<b>空路径 / 无上下文与已认证真实请求的判定口径完全一致</b>。</para>
    /// </summary>
    public static async Task<long> EnsureAuthorizedAsync(
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
        if (user.Status != Domain.Enums.UserStatus.Enabled)
            throw new BusinessException(UserDisabledText, ErrorCodes.Forbidden);

        var menuCodes = await CustomerReceivableReconciliationService
            .LoadAuthorizedMenuCodesAsync(db, userId.Value);

        if (!menuCodes.Contains(RequiredMenuCode, StringComparer.OrdinalIgnoreCase))
            throw new BusinessException(MenuDeniedText, ErrorCodes.Forbidden);

        return user.Id;
    }

    /// <summary>
    /// 新增 / 修改角色请求的有界校验（落库之前调用）：
    /// <list type="bullet">
    /// <item>角色名称：非空且 ≤ <see cref="MaxRoleNameLength"/>；</item>
    /// <item>角色编码：非空且 ≤ <see cref="MaxRoleCodeLength"/>（唯一性由调用方在同一非删除集合上判定）；</item>
    /// <item>角色描述：不超 <see cref="MaxDescriptionLength"/>。</item>
    /// </list>
    /// 任一项不满足即按 <see cref="ErrorCodes.InvalidParameter"/> 的受控错误拒绝，<b>不</b>静默截断或改写任何字段。
    /// </summary>
    public static void Validate(RoleRequest? request)
    {
        if (request is null)
            throw BusinessException.InvalidParameter("角色数据不能为空");

        EnsureRequiredText(request.RoleName, MaxRoleNameLength, "角色名称");
        EnsureRequiredText(request.RoleCode, MaxRoleCodeLength, "角色编码");
        EnsureOptionalText(request.Description, MaxDescriptionLength, "角色描述");
    }

    /// <summary>
    /// 角色菜单关联校验（新增 / 修改在写入任何 <c>SysRoleMenus</c> 行之前调用）：每个菜单 Id 必须为有效正数
    /// 且解析为<b>已知的非删除</b>菜单；否则整批按 <see cref="ErrorCodes.InvalidParameter"/> 拒绝，
    /// 不留下任何半写的菜单关联，也不接受任意 <c>menuId</c>（含系统菜单伪造）。
    /// </summary>
    public static async Task EnsureMenuIdsResolvedAsync(
        IErpDbContext db, IEnumerable<long>? menuIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ct.ThrowIfCancellationRequested();

        if (menuIds is null) return;

        var distinct = new List<long>();
        foreach (var menuId in menuIds)
        {
            if (menuId <= 0)
                throw BusinessException.InvalidParameter("菜单 Id 必须为有效的正数");
            if (!distinct.Contains(menuId)) distinct.Add(menuId);
        }
        if (distinct.Count == 0) return;

        var known = await db.SysMenus.AsNoTracking()
            .Where(m => distinct.Contains(m.Id) && !m.IsDeleted)
            .Select(m => m.Id)
            .ToListAsync(ct);

        if (known.Count != distinct.Count)
            throw BusinessException.InvalidParameter("所选菜单不存在或已删除，禁止分配");
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
