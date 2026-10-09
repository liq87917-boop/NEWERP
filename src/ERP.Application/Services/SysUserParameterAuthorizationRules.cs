using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ERP.Application.Services;

/// <summary>
/// 用户参数（<see cref="SysUserParameter"/>，<c>api/sys/user-parameters</c>）实时身份、既有功能菜单与
/// 持久化列有界校验护栏（ERP-456，阶段 3 访问控制收口）。
/// <list type="number">
/// <item><b>实时授权</b>（<see cref="EnsureAuthorizedAsync"/>）：分页 / 主键详情 / 新增 / 修改 / 删除
/// <b>每一</b>路由在读取任何用户参数行或写入任何用户参数行<b>之前</b>都先解析<b>实时启用身份</b>
/// （缺失 / 非法按未认证拒绝，账号不存在 / 已删除按未认证拒绝，禁用按权限不足拒绝）与既有
/// 「用户参数」（<c>user-parameter</c>，与 <c>SeedData.Menus</c> 同源）功能菜单授权
/// （每个账号都必须显式具备，撤销后下一次请求立即收敛；无匿名 / 管理员兜底）。</item>
/// <item><b>持久化列有界校验</b>（<see cref="ValidateAsync"/>）：新增 / 修改在任何字段落库之前校验
/// <see cref="SysUserParameter.ParamKey"/> 非空且不超既有持久化上界、
/// <see cref="SysUserParameter.ParamValue"/> 不超既有持久化上界，且
/// <see cref="SysUserParameter.UserId"/> 解析为<b>已知的非删除</b>用户；一律以既有受控错误
/// <b>拒绝而不静默截断 / 改写</b>，被拒绝的写入不落任何用户参数行。</item>
/// </list>
/// <para><b>身份来源唯一</b>：请求提交体中的任何字段都不能指定或扩大账号身份 —— 身份只来自已认证请求主体
/// （<c>ClaimTypes.NameIdentifier</c>），由调用方（<c>ERP.Api.Controllers.UserParameterController</c>）
/// 解析并传入；<c>null</c> 表示无可用身份，一律 fail closed，绝不代表匿名或管理员。</para>
/// <para>边界：本类只做纯判定与有界只读查询；不新增表 / 列 / 实体 / 菜单 / 角色 / 用户授权，不引入匿名 /
/// 管理员兜底，也不改变既有的按用户参数键唯一语义与软删除语义（<c>IsDeleted</c> 标记）。</para>
/// </summary>
public static class SysUserParameterAuthorizationRules
{
    /// <summary>
    /// 用户参数模块复用的既有菜单编码（与 <c>SeedData.Menus</c> 同源：
    /// <c>("system","user-parameter","用户参数",…)</c>，<b>不新增菜单</b>）。
    /// </summary>
    public const string RequiredMenuCode = "user-parameter";

    /// <summary>用户参数模块菜单中文文案（与既有菜单名一致）</summary>
    public const string RequiredMenuText = "用户参数";

    // ==================== 既有持久化长度上界（与 SysUserParameter 的 [MaxLength] 同源） ====================

    /// <summary>参数键长度上界（与 <see cref="SysUserParameter.ParamKey"/> <c>MaxLength(100)</c> 同源）</summary>
    public const int MaxParamKeyLength = 100;

    /// <summary>参数值长度上界（与 <see cref="SysUserParameter.ParamValue"/> <c>MaxLength(500)</c> 同源）</summary>
    public const int MaxParamValueLength = 500;

    // ==================== 受控非披露错误 / 校验文案 ====================

    /// <summary>无身份 / 非法身份的拒绝文案（受控、不泄露数据）</summary>
    public const string UnauthorizedText = "请先登录后再访问用户参数";

    /// <summary>账号不存在 / 已删除的拒绝文案（受控、不泄露数据）</summary>
    public const string UserDeletedText = "登录账号不存在或已删除，禁止访问用户参数";

    /// <summary>账号已禁用的拒绝文案（受控、不泄露数据）</summary>
    public const string UserDisabledText = "登录账号已禁用，禁止访问用户参数（fail closed）";

    /// <summary>缺少既有「用户参数」菜单授权时的拒绝文案（受控、不泄露数据）</summary>
    public const string MenuDeniedText =
        "当前账号没有「用户参数」（user-parameter）模块授权：拒绝访问用户参数"
        + "（fail closed，不返回 / 不新增 / 不改写任何用户参数）";

    /// <summary>参数键为空的拒绝文案</summary>
    public const string ParamKeyRequiredText = "参数键（ParamKey）不能为空";

    /// <summary>参数键超长的拒绝文案</summary>
    public const string ParamKeyTooLongText = "参数键（ParamKey）长度不能超过 100 个字符";

    /// <summary>参数值超长的拒绝文案</summary>
    public const string ParamValueTooLongText = "参数值（ParamValue）长度不能超过 500 个字符";

    /// <summary>拟议用户不存在 / 已删除的拒绝文案</summary>
    public const string UnknownUserText = "指定用户不存在或已删除，禁止维护用户参数";

    /// <summary>用户参数键在未删除行中重复的拒绝文案（保持既有重复检查文案）</summary>
    public const string ParamKeyDuplicatedText = "该用户下参数键已存在";

    /// <summary>授权与校验口径文案（接口 / 文档同源）</summary>
    public const string RuleText =
        "用户参数（分页 / 主键详情 / 新增 / 修改 / 删除）在读取任何用户参数行或写入之前，"
        + "都会重新校验实时启用身份（缺失 / 非法 / 账号不存在 / 已删除按未认证，禁用按权限不足）与既有"
        + "「用户参数」（user-parameter）功能菜单授权；撤销授权后下一次请求立即收敛，绝不把空身份当作管理员。"
        + "新增 / 修改前校验 ParamKey 非空且有界、ParamValue 有界，且 UserId 解析为已知的非删除用户，"
        + "一律拒绝而不静默截断 / 改写任何用户参数行；身份只来自已认证请求主体。";

    /// <summary>边界文案（不新增权限 / 表列，不改变既有唯一与软删除语义）</summary>
    public const string BoundaryText =
        "本护栏只保护用户参数的访问与持久化取值校验：不新增菜单 / 角色 / 用户授权或表结构，"
        + "无匿名 / 管理员兜底，也不改变既有的按用户参数键唯一检查与软删除（IsDeleted）语义；"
        + "被拒绝的读取 / 新增 / 修改 / 删除不加载、不写入、不改写任何用户参数行。";

    // ==================== 1. 身份 / 账号状态 / 菜单授权（fail closed） ====================

    /// <summary>
    /// 身份 / 账号状态 / 既有「用户参数」菜单授权实时校验（fail closed）：
    /// 缺失 / 非法身份按未认证拒绝，账号不存在 / 已删除按未认证拒绝，禁用按权限不足拒绝，
    /// 缺少既有 <c>user-parameter</c> 菜单授权按权限不足拒绝。
    /// 每次调用都重新查询（无缓存），账号停用 / 删除或菜单撤销后下一次请求立即收敛；
    /// 每个账号都必须显式具备既有功能菜单，<b>无特权 / 管理员兜底</b>。
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

    // ==================== 2. 可选字段规范化（判空去空白，不改写任何已存储行） ====================

    /// <summary>
    /// 写入前规范化拟议用户参数（只改写入参）：<see cref="SysUserParameter.ParamKey"/> 去首尾空白，
    /// 可空的 <see cref="SysUserParameter.ParamValue"/> 为 <c>null</c> 时归一为空字符串。
    /// </summary>
    public static void NormalizeForWrite(SysUserParameter entity)
    {
        ArgumentNullException.ThrowIfNull(entity);

        entity.ParamKey = (entity.ParamKey ?? string.Empty).Trim();
        entity.ParamValue ??= string.Empty;
    }

    // ==================== 3. 持久化列有界校验（不改写实体） ====================

    /// <summary>
    /// 用户参数写入校验（新增 / 修改）：<see cref="SysUserParameter.ParamKey"/> 非空且不超既有持久化上界、
    /// <see cref="SysUserParameter.ParamValue"/> 不超既有持久化上界，且
    /// <see cref="SysUserParameter.UserId"/> 解析为<b>已知的非删除</b>用户。
    /// 任一不满足即在改写任何字段之前以既有受控错误拒绝（形状 / 取值 <c>1001</c>），
    /// <b>绝不静默截断或改写</b>任何用户参数行；被拒绝的写入不落任何行。
    /// </summary>
    public static async Task ValidateAsync(IErpDbContext db, SysUserParameter entity, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(entity);
        ct.ThrowIfCancellationRequested();

        var paramKey = (entity.ParamKey ?? string.Empty).Trim();
        if (paramKey.Length == 0)
            throw BusinessException.InvalidParameter(ParamKeyRequiredText);
        if (paramKey.Length > MaxParamKeyLength)
            throw BusinessException.InvalidParameter($"{ParamKeyTooLongText}：当前长度 {paramKey.Length}");

        var paramValue = entity.ParamValue ?? string.Empty;
        if (paramValue.Length > MaxParamValueLength)
            throw BusinessException.InvalidParameter($"{ParamValueTooLongText}：当前长度 {paramValue.Length}");

        if (entity.UserId <= 0)
            throw BusinessException.InvalidParameter(UnknownUserText);

        var knownUser = await db.SysUsers.AsNoTracking()
            .AnyAsync(u => u.Id == entity.UserId && !u.IsDeleted, ct);
        if (!knownUser)
            throw BusinessException.InvalidParameter(UnknownUserText);
    }
}
