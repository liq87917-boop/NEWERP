using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ERP.Application.Services;

/// <summary>
/// 系统用户（<see cref="SysUser"/>，<c>api/sys/users</c>）的实时身份 / 既有功能菜单授权
/// 与有界字段校验护栏（ERP-453）。用户记录是每一次已认证请求、ERP-097 业务员数据范围与
/// 每一条运营权限判定<b>共同解析</b>的权威对象，因此其管理端点必须像其它主数据一样 fail closed。
/// <list type="number">
/// <item><b>实时授权</b>（<see cref="EnsureAuthorizedAsync"/>）：分页 / 按主键读取 / 新增 / 修改 /
/// 切换状态 / 重置密码 / 删除<b>每一</b>路由在读取或写入任何 <c>SysUsers</c> / <c>SysUserRoles</c> 行
/// <b>之前</b>都重新解析实时身份（缺失 / 非法 / 账号不存在或已删除按未认证，禁用按权限不足）与既有
/// 「用户管理」（<c>user</c>）功能菜单授权（缺菜单 / 被撤销按权限不足），一律 fail closed；</item>
/// <item><b>有界字段校验</b>（<see cref="ValidateCreate"/> / <see cref="ValidateUpdate"/> /
/// <see cref="ValidateResetPassword"/>）：新增 / 修改在落库之前校验<b>用户名</b>非空且在持久化长度上限内、
/// 显示姓名 / 邮箱 / 手机号不超持久化长度、状态为已知的启用 / 禁用值、重置密码载荷有界；
/// 角色 Id（<see cref="EnsureRoleIdsResolvedAsync"/>）必须全部解析为已知的非删除角色；
/// 被拒绝的写入不落任何 <c>SysUsers</c> / <c>SysUserRoles</c> 行。</item>
/// </list>
/// <para>边界（重要）：本护栏只新增「读取 / 写入前的判定」，<b>不</b>静默截断 / 改写任何用户字段，
/// 也<b>不</b>改变既有用户名唯一索引语义、<c>SeedData.AdminUserName</c> 内置管理员保护与 PBKDF2
/// 密码哈希语义；<b>不</b>新增任何表 / 列 / 实体 / 菜单 / 权限 / 用户授权，不伪造任何授权，
/// <b>不</b>因身份缺失而降级为管理员，也<b>不</b>新增匿名 / 特权旁路。</para>
/// </summary>
public static class SysUserAuthorizationRules
{
    /// <summary>所需既有功能菜单编码（与 <c>SeedData.Menus</c> 的 <c>("system","user","用户管理",…)</c> 同源，<b>不新增菜单</b>）</summary>
    public const string RequiredMenuCode = "user";

    /// <summary>既有功能菜单中文文案</summary>
    public const string RequiredMenuText = "用户管理";

    /// <summary>登录账号持久化长度上限（<c>NVARCHAR(50)</c>，与实体 <c>[MaxLength(50)]</c> 一致）</summary>
    public const int MaxUserNameLength = 50;

    /// <summary>显示姓名持久化长度上限（<c>NVARCHAR(50)</c>，与实体 <c>[MaxLength(50)]</c> 一致）</summary>
    public const int MaxDisplayNameLength = 50;

    /// <summary>邮箱持久化长度上限（<c>NVARCHAR(100)</c>，与实体 <c>[MaxLength(100)]</c> 一致）</summary>
    public const int MaxEmailLength = 100;

    /// <summary>手机号持久化长度上限（<c>NVARCHAR(30)</c>，与实体 <c>[MaxLength(30)]</c> 一致）</summary>
    public const int MaxPhoneLength = 30;

    /// <summary>重置 / 初始密码的最小长度（与既有 <c>MinLength(6)</c> 语义一致）</summary>
    public const int MinPasswordLength = 6;

    /// <summary>重置 / 初始密码的最大长度（拒绝无界载荷；密码本身不落库，仅哈希 + 盐）</summary>
    public const int MaxPasswordLength = 128;

    /// <summary>已知启用状态（<c>UserStatus.Enabled = 1</c>）</summary>
    public static readonly UserStatus EnabledStatus = UserStatus.Enabled;

    /// <summary>已知禁用状态（<c>UserStatus.Disabled = 0</c>）</summary>
    public static readonly UserStatus DisabledStatus = UserStatus.Disabled;

    /// <summary>无身份 / 非法身份的拒绝文案（受控、不泄露数据）</summary>
    public const string UnauthorizedText = "请先登录后再访问用户管理";

    /// <summary>账号不存在 / 已删除的拒绝文案（受控、不泄露数据）</summary>
    public const string UserDeletedText = "登录账号不存在或已删除，禁止访问用户管理";

    /// <summary>账号已禁用的拒绝文案（受控、不泄露数据）</summary>
    public const string UserDisabledText = "登录账号已禁用，禁止访问用户管理（fail closed）";

    /// <summary>缺少既有「用户管理」菜单授权时的拒绝文案（受控、不泄露数据）</summary>
    public const string MenuDeniedText =
        "当前账号没有「用户管理」（user）模块授权：" +
        "拒绝访问用户管理（fail closed，不返回 / 不新增 / 不改写任何用户）";

    /// <summary>授权口径文案（接口 / 文档同源）</summary>
    public const string AuthorizationRuleText =
        "用户管理（分页 / 按主键读取 / 新增 / 修改 / 切换状态 / 重置密码 / 删除）在读取或写入任何用户之前，" +
        "都会重新校验实时身份（缺失 / 非法 / 已删除按未认证，禁用按权限不足，一律 fail closed）" +
        "与既有「用户管理」（user）功能菜单授权；菜单授权复用既有「角色 → 菜单」口径，" +
        "每次请求重新查询，撤销后下一次请求立即收敛。";

    /// <summary>授权边界文案（不改写既有业务口径）</summary>
    public const string AuthorizationBoundaryText =
        "本护栏只新增「读取 / 写入前的授权与有界字段校验」：不改变用户名唯一索引语义、" +
        "SeedData.AdminUserName 内置管理员保护、PBKDF2 密码哈希语义与分页 / 响应契约，" +
        "也不静默截断 / 改写任何用户字段；不新增表 / 列 / 实体 / 菜单 / 权限 / 用户授权，" +
        "也不把空身份当作管理员。";

    /// <summary>
    /// 实时身份 / 账号状态 / 既有「用户管理」功能菜单三项校验（fail closed），返回本次请求的权威用户 Id。
    /// <list type="bullet">
    /// <item>身份缺失 / 非法 → <see cref="ErrorCodes.Unauthorized"/>；</item>
    /// <item>账号不存在或已删除 → <see cref="ErrorCodes.Unauthorized"/>；</item>
    /// <item>账号已禁用 → <see cref="ErrorCodes.Forbidden"/>；</item>
    /// <item>缺少既有「用户管理」（<c>user</c>）菜单授权（含被撤销授权）→ <see cref="ErrorCodes.Forbidden"/>。</item>
    /// </list>
    /// 判定发生在任何用户读取 / 写入<b>之前</b>；每次请求重新解析，菜单或账号状态变更后立即收敛；
    /// <b>不</b>新增任何菜单 / 角色 / 用户授权，也<b>不</b>因身份缺失而降级为管理员。
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
        if (user.Status != UserStatus.Enabled)
            throw new BusinessException(UserDisabledText, ErrorCodes.Forbidden);

        var menuCodes = await CustomerReceivableReconciliationService
            .LoadAuthorizedMenuCodesAsync(db, userId.Value);

        if (!menuCodes.Contains(RequiredMenuCode, StringComparer.OrdinalIgnoreCase))
            throw new BusinessException(MenuDeniedText, ErrorCodes.Forbidden);

        return user.Id;
    }

    /// <summary>
    /// 新增用户请求的有界校验（落库之前调用）：
    /// <list type="bullet">
    /// <item>登录账号：非空且 ≤ <see cref="MaxUserNameLength"/>（唯一性由调用方在同一非删除集合上判定）；</item>
    /// <item>显示姓名 / 邮箱 / 手机号：不超各自持久化长度上限；</item>
    /// <item>初始密码：长度落在 <see cref="MinPasswordLength"/> ~ <see cref="MaxPasswordLength"/>。</item>
    /// </list>
    /// 任一项不满足即按 <see cref="ErrorCodes.InvalidParameter"/> 的受控错误拒绝，<b>不</b>静默截断或改写任何字段。
    /// </summary>
    public static void ValidateCreate(SysUserCreateRequest? request)
    {
        if (request is null)
            throw BusinessException.InvalidParameter("用户数据不能为空");

        EnsureRequiredText(request.UserName, MaxUserNameLength, "用户名");
        EnsureOptionalText(request.DisplayName, MaxDisplayNameLength, "显示姓名");
        EnsureOptionalText(request.Email, MaxEmailLength, "邮箱");
        EnsureOptionalText(request.Phone, MaxPhoneLength, "手机号");
        EnsurePassword(request.Password, "初始密码");
    }

    /// <summary>
    /// 修改用户请求的有界校验（落库之前调用）：显示姓名 / 邮箱 / 手机号不超各自持久化长度上限，
    /// 状态仅接受 <see cref="EnabledStatus"/>（启用）或 <see cref="DisabledStatus"/>（禁用）。
    /// 任一项不满足即按 <see cref="ErrorCodes.InvalidParameter"/> 的受控错误拒绝，
    /// <b>不</b>静默截断或改写任何字段，被拒绝的修改不改写任何既有 <c>SysUsers</c> 行。
    /// </summary>
    public static void ValidateUpdate(SysUserUpdateRequest? request)
    {
        if (request is null)
            throw BusinessException.InvalidParameter("用户数据不能为空");

        EnsureOptionalText(request.DisplayName, MaxDisplayNameLength, "显示姓名");
        EnsureOptionalText(request.Email, MaxEmailLength, "邮箱");
        EnsureOptionalText(request.Phone, MaxPhoneLength, "手机号");
        EnsureKnownStatus(request.Status);
    }

    /// <summary>
    /// 重置密码请求的有界校验（落库之前调用）：新密码长度落在
    /// <see cref="MinPasswordLength"/> ~ <see cref="MaxPasswordLength"/>。
    /// 任一项不满足即按 <see cref="ErrorCodes.InvalidParameter"/> 的受控错误拒绝，
    /// 被拒绝的重置不改写任何既有 <c>SysUsers</c> 行（密码哈希 / 盐语义保持不变）。
    /// </summary>
    public static void ValidateResetPassword(ResetPasswordRequest? request)
    {
        if (request is null)
            throw BusinessException.InvalidParameter("重置密码数据不能为空");

        EnsurePassword(request.NewPassword, "新密码");
    }

    /// <summary>
    /// 角色关联校验（新增 / 修改在写入任何 <c>SysUserRoles</c> 行之前调用）：每个角色 Id 必须为有效正数
    /// 且解析为<b>已知的非删除</b>角色；否则整批按 <see cref="ErrorCodes.InvalidParameter"/> 拒绝，
    /// 不留下任何半写的角色关联，也不新增任何角色 / 菜单 / 权限授权。
    /// </summary>
    public static async Task EnsureRoleIdsResolvedAsync(
        IErpDbContext db, IEnumerable<long>? roleIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ct.ThrowIfCancellationRequested();

        if (roleIds is null) return;

        var distinct = new List<long>();
        foreach (var roleId in roleIds)
        {
            if (roleId <= 0)
                throw BusinessException.InvalidParameter("角色 Id 必须为有效的正数");
            if (!distinct.Contains(roleId)) distinct.Add(roleId);
        }
        if (distinct.Count == 0) return;

        var known = await db.SysRoles.AsNoTracking()
            .Where(r => distinct.Contains(r.Id) && !r.IsDeleted)
            .Select(r => r.Id)
            .ToListAsync(ct);

        if (known.Count != distinct.Count)
            throw BusinessException.InvalidParameter("所选角色不存在或已删除，禁止分配");
    }

    /// <summary>必填文本校验：空白即拒绝，超过持久化长度上限即拒绝（不做静默截断）。</summary>
    private static void EnsureRequiredText(string? value, int maxLength, string fieldText)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw BusinessException.InvalidParameter($"用户{fieldText}不能为空");
        if (value.Length > maxLength)
            throw BusinessException.InvalidParameter($"用户{fieldText}长度不能超过 {maxLength} 个字符");
    }

    /// <summary>可选文本校验：仅在提供且超过持久化长度上限时拒绝（不做静默截断）。</summary>
    private static void EnsureOptionalText(string? value, int maxLength, string fieldText)
    {
        if (value is not null && value.Length > maxLength)
            throw BusinessException.InvalidParameter($"用户{fieldText}长度不能超过 {maxLength} 个字符");
    }

    /// <summary>密码校验：非空且长度有界（密码本身不落库，仅 PBKDF2 哈希 + 盐）。</summary>
    private static void EnsurePassword(string? password, string fieldText)
    {
        if (string.IsNullOrEmpty(password))
            throw BusinessException.InvalidParameter($"用户{fieldText}不能为空");
        if (password.Length < MinPasswordLength)
            throw BusinessException.InvalidParameter($"用户{fieldText}长度不能少于 {MinPasswordLength} 位");
        if (password.Length > MaxPasswordLength)
            throw BusinessException.InvalidParameter($"用户{fieldText}长度不能超过 {MaxPasswordLength} 个字符");
    }

    /// <summary>状态校验：仅接受已知的启用 / 禁用值（不做静默映射）。</summary>
    private static void EnsureKnownStatus(UserStatus status)
    {
        if (status != EnabledStatus && status != DisabledStatus)
            throw BusinessException.InvalidParameter("用户状态只能是 1（启用）或 0（禁用）");
    }
}
