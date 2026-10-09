using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ERP.Application.Services;

/// <summary>
/// 客户端限制（<see cref="SysClientLimit"/>，<c>api/sys/client-limits</c>）实时身份、既有功能菜单与
/// 限制形状有界校验护栏（ERP-457，阶段 3 访问控制收口）。
/// <list type="number">
/// <item><b>实时授权</b>：分页 / 全部 / 详情 / 新增 / 修改 / 删除 / 批量删除，在读取任何计数或写入任何限制行
/// <b>之前</b>都先解析<b>实时启用身份</b>（缺失 / 非法按未认证拒绝，账号不存在 / 已删除按未认证拒绝，
/// 禁用按权限不足拒绝）与既有「客户端限制」（<c>client-limit</c>，与 <c>SeedData.Menus</c> 同源）功能菜单授权
/// （每个账号都必须显式具备，撤销后下一次请求立即收敛；无匿名 / 管理员兜底）。</item>
/// <item><b>限制形状有界校验</b>：新增 / 修改在任何字段落库之前校验 <see cref="SysClientLimit.LimitType"/>
/// 是已知客户端限制类型、<see cref="SysClientLimit.LimitValue"/> 非空且不超过既有持久化上界、
/// <see cref="SysClientLimit.Remark"/> 不超过既有持久化上界；一律以既有受控错误 <b>拒绝而不静默截断 / 改写</b>。</item>
/// </list>
/// <para><b>身份来源唯一</b>：请求提交体中的任何字段都不能指定或扩大账号身份 —— 身份只来自已认证请求主体
/// （<c>ClaimTypes.NameIdentifier</c>），由调用方（<c>ERP.Api.Controllers.ClientLimitController</c>）
/// 解析并传入；<c>null</c> 表示无可用身份，一律 fail closed，绝不代表匿名或管理员。</para>
/// <para>边界：本类只做纯判定与有界只读查询；不新增表 / 列 / 实体 / 菜单 / 角色 / 用户授权，不引入匿名 /
/// 管理员兜底，也不改变 <c>BaseCrudController</c> 的通用软删除契约。</para>
/// </summary>
public static class ClientLimitAuthorizationRules
{
    /// <summary>客户端限制模块复用的既有菜单编码（与 <c>SeedData.Menus</c> 同源：<c>("system","client-limit","客户端限制",…)</c>）</summary>
    public const string RequiredMenuCode = "client-limit";

    /// <summary>客户端限制模块菜单中文文案（与既有菜单名一致）</summary>
    public const string RequiredMenuText = "客户端限制";

    // ==================== 既有持久化长度上界（与 SysClientLimit 的 [MaxLength] 同源） ====================

    /// <summary>限制值长度上界（与 <see cref="SysClientLimit.LimitValue"/> <c>MaxLength(200)</c> 同源）</summary>
    public const int MaxLimitValueLength = 200;

    /// <summary>备注长度上界（与 <see cref="SysClientLimit.Remark"/> <c>MaxLength(500)</c> 同源）</summary>
    public const int MaxRemarkLength = 500;

    // ==================== 受控非披露错误 / 校验文案 ====================

    /// <summary>无身份 / 非法身份的拒绝文案</summary>
    public const string UnauthorizedText = "请先登录后再访问客户端限制";

    /// <summary>账号不存在 / 已删除的拒绝文案</summary>
    public const string UserDeletedText = "登录账号不存在或已删除，禁止访问客户端限制";

    /// <summary>账号已禁用的拒绝文案</summary>
    public const string UserDisabledText = "登录账号已禁用，禁止访问客户端限制（fail closed）";

    /// <summary>缺少既有「客户端限制」菜单授权的拒绝文案</summary>
    public const string MenuDeniedText =
        "当前账号没有「客户端限制」（client-limit）模块授权：拒绝访问客户端限制"
        + "（fail closed，不返回 / 不新增 / 不改写任何客户端限制）";

    /// <summary>限制类型不是已知客户端限制类型的拒绝文案</summary>
    public const string LimitTypeUnknownText = "限制类型（LimitType）不是已知的客户端限制类型";

    /// <summary>限制值为空的拒绝文案</summary>
    public const string LimitValueRequiredText = "限制值（LimitValue）不能为空";

    /// <summary>限制值超长的拒绝文案</summary>
    public const string LimitValueTooLongText = "限制值（LimitValue）长度不能超过 200 个字符";

    /// <summary>备注超长的拒绝文案</summary>
    public const string RemarkTooLongText = "备注（Remark）长度不能超过 500 个字符";

    /// <summary>授权与校验口径文案（接口 / 文档同源）</summary>
    public const string RuleText =
        "客户端限制（分页 / 全部 / 详情 / 新增 / 修改 / 删除 / 批量删除）在读取任何计数或写入之前，"
        + "都会重新校验实时启用身份（缺失 / 非法 / 账号不存在 / 已删除按未认证，禁用按权限不足）与既有"
        + "「客户端限制」（client-limit）功能菜单授权；撤销授权后下一次请求立即收敛，绝不把空身份当作管理员。"
        + "新增 / 修改前校验 LimitType 是已知客户端限制类型、LimitValue 非空且不超过既有持久化上界、"
        + "Remark 不超过既有持久化上界，一律拒绝而不静默截断 / 改写任何客户端限制行；身份只来自已认证请求主体。";

    /// <summary>边界文案（不新增权限 / 表列，不改变通用软删除契约）</summary>
    public const string BoundaryText =
        "本护栏只保护客户端限制的访问与限制形状校验：不新增菜单 / 角色 / 用户授权或表结构，"
        + "无匿名 / 管理员兜底，也不改变 BaseCrudController 的通用软删除契约；被拒绝的读取 / 新增 / 修改 / 删除 / "
        + "批量删除不加载、不写入、不改写任何客户端限制行。";

    // ==================== 1. 身份 / 账号状态 / 菜单授权（fail closed） ====================

    /// <summary>
    /// 身份 / 账号状态 / 既有「客户端限制」菜单授权实时校验（fail closed）：
    /// 缺失 / 非法身份按未认证拒绝，账号不存在 / 已删除按未认证拒绝，禁用按权限不足拒绝，
    /// 缺少既有 <c>client-limit</c> 菜单授权按权限不足拒绝。
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

    // ==================== 2. 写入前规范化（只改写入参，不改写任何已存储行） ====================

    /// <summary>
    /// 写入前规范化拟议客户端限制（只改写入参、去空白，不改写任何已存储行）：
    /// <see cref="SysClientLimit.LimitValue"/> / <see cref="SysClientLimit.Remark"/> 去首尾空白。
    /// </summary>
    public static void NormalizeForWrite(SysClientLimit entity)
    {
        ArgumentNullException.ThrowIfNull(entity);

        entity.LimitValue = (entity.LimitValue ?? string.Empty).Trim();
        entity.Remark = (entity.Remark ?? string.Empty).Trim();
    }

    // ==================== 3. 限制形状有界校验（不改写实体） ====================

    /// <summary>
    /// 客户端限制写入校验：<see cref="SysClientLimit.LimitType"/> 必须是已知客户端限制类型、
    /// <see cref="SysClientLimit.LimitValue"/> 非空且不超过既有持久化上界、
    /// <see cref="SysClientLimit.Remark"/> 不超过既有持久化上界。
    /// 任一不满足即在改写任何字段之前以既有受控错误拒绝（形状 / 取值 <c>1001</c>），
    /// <b>绝不静默截断、删除或复活</b>任何客户端限制行；被拒绝的写入不落任何行。
    /// </summary>
    public static void Validate(SysClientLimit entity)
    {
        ArgumentNullException.ThrowIfNull(entity);

        if (!Enum.IsDefined(typeof(ClientLimitType), entity.LimitType))
            throw BusinessException.InvalidParameter($"{LimitTypeUnknownText}：当前值 {(int)entity.LimitType}");

        var limitValue = (entity.LimitValue ?? string.Empty).Trim();
        if (limitValue.Length == 0)
            throw BusinessException.InvalidParameter(LimitValueRequiredText);
        if (limitValue.Length > MaxLimitValueLength)
            throw BusinessException.InvalidParameter($"{LimitValueTooLongText}：当前长度 {limitValue.Length}");

        var remark = entity.Remark ?? string.Empty;
        if (remark.Length > MaxRemarkLength)
            throw BusinessException.InvalidParameter($"{RemarkTooLongText}：当前长度 {remark.Length}");
    }
}
