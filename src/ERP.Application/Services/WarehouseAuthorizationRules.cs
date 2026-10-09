using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ERP.Application.Services;

/// <summary>
/// 仓库资料（<see cref="BaseWarehouse"/>，<c>api/base/warehouses</c>）的实时身份 / 既有功能菜单授权
/// 与有界字段校验护栏（ERP-448）。
/// <list type="number">
/// <item><b>实时授权</b>（<see cref="EnsureAuthorizedAsync"/>）：分页 / 全部 / 按主键读取与新增 / 修改 / 删除 /
/// 批量删除<b>每一个</b>路由在读取或写入任何 <c>BaseWarehouses</c> 行<b>之前</b>都重新解析实时身份
/// （缺失 / 非法 / 账号不存在或已删除按未认证，禁用按权限不足）与既有「仓库资料」（<c>warehouse</c>）
/// 功能菜单授权（缺菜单 / 被撤销按权限不足），一律 fail closed；</item>
/// <item><b>有界字段校验</b>（<see cref="Validate"/>）：新增 / 修改在落库之前校验仓库编码与名称非空且在
/// 持久化长度上限内、地址 / 负责人 / 联系电话 / 备注不超持久化长度、状态为已知的 <c>1</c>（启用）/
/// <c>0</c>（停用）；被拒绝的写入不落任何行。</item>
/// </list>
/// <para>边界（重要）：本护栏只新增「读取 / 写入前的判定」，<b>不</b>静默截断 / 夹取或改写任何仓库字段，
/// 也<b>不</b>改变既有「仓库编码」唯一索引语义、分页 / 响应契约与 <see cref="GenericService{TEntity}"/> 契约；
/// <b>不</b>新增任何表 / 列 / 实体 / 菜单 / 权限 / 用户授权，不伪造任何授权，<b>不</b>因身份缺失而降级为管理员，
/// 也<b>不</b>新增匿名 / 特权旁路。</para>
/// </summary>
public static class WarehouseAuthorizationRules
{
    /// <summary>所需既有功能菜单编码（与 <c>SeedData.Menus</c> 同源，<b>不新增菜单</b>）</summary>
    public const string RequiredMenuCode = "warehouse";

    /// <summary>既有功能菜单中文文案</summary>
    public const string RequiredMenuText = "仓库资料";

    /// <summary>仓库编码持久化长度上限（<c>NVARCHAR(50)</c>，与实体 <c>[MaxLength(50)]</c> 一致）</summary>
    public const int MaxWarehouseCodeLength = 50;

    /// <summary>仓库名称持久化长度上限（<c>NVARCHAR(100)</c>，与实体 <c>[MaxLength(100)]</c> 一致）</summary>
    public const int MaxWarehouseNameLength = 100;

    /// <summary>仓库地址持久化长度上限（<c>NVARCHAR(500)</c>，与实体 <c>[MaxLength(500)]</c> 一致）</summary>
    public const int MaxAddressLength = 500;

    /// <summary>负责人持久化长度上限（<c>NVARCHAR(50)</c>，与实体 <c>[MaxLength(50)]</c> 一致）</summary>
    public const int MaxManagerLength = 50;

    /// <summary>联系电话持久化长度上限（<c>NVARCHAR(50)</c>，与实体 <c>[MaxLength(50)]</c> 一致）</summary>
    public const int MaxPhoneLength = 50;

    /// <summary>备注持久化长度上限（<c>NVARCHAR(500)</c>，与实体 <c>[MaxLength(500)]</c> 一致）</summary>
    public const int MaxRemarkLength = 500;

    /// <summary>已知启用状态（<c>BaseWarehouse.Status = 1</c>）</summary>
    public const int EnabledStatus = 1;

    /// <summary>已知停用状态（<c>BaseWarehouse.Status = 0</c>）</summary>
    public const int DisabledStatus = 0;

    /// <summary>无身份 / 非法身份的拒绝文案（受控、不泄露数据）</summary>
    public const string UnauthorizedText = "请先登录后再访问仓库资料";

    /// <summary>账号不存在 / 已删除的拒绝文案（受控、不泄露数据）</summary>
    public const string UserDeletedText = "登录账号不存在或已删除，禁止访问仓库资料";

    /// <summary>账号已禁用的拒绝文案（受控、不泄露数据）</summary>
    public const string UserDisabledText = "登录账号已禁用，禁止访问仓库资料（fail closed）";

    /// <summary>缺少既有「仓库资料」菜单授权时的拒绝文案（受控、不泄露数据）</summary>
    public const string MenuDeniedText =
        "当前账号没有「仓库资料」（warehouse）模块授权：" +
        "拒绝访问仓库资料（fail closed，不返回 / 不新增 / 不改写任何仓库）";

    /// <summary>授权口径文案（接口 / 文档同源）</summary>
    public const string AuthorizationRuleText =
        "仓库资料（分页 / 全部 / 按主键读取 / 新增 / 修改 / 删除 / 批量删除）在读取或写入任何仓库之前，" +
        "都会重新校验实时身份（缺失 / 非法 / 已删除按未认证，禁用按权限不足，一律 fail closed）" +
        "与既有「仓库资料」（warehouse）单项功能菜单授权；菜单授权复用既有「角色 → 菜单」口径，" +
        "每次请求重新查询，撤销后下一次请求立即收敛。";

    /// <summary>授权边界文案（不改写既有业务口径）</summary>
    public const string AuthorizationBoundaryText =
        "本护栏只新增「读取 / 写入前的授权与有界字段校验」：不改变仓库编码唯一索引语义、" +
        "分页 / 响应契约与 GenericService 契约，也不静默截断 / 夹取或改写任何仓库字段；" +
        "不新增表 / 列 / 实体 / 菜单 / 权限 / 用户授权，也不把空身份当作管理员。";

    /// <summary>
    /// 实时身份 / 账号状态 / 既有「仓库资料」功能菜单三项校验（fail closed），返回本次请求的权威用户 Id。
    /// <list type="bullet">
    /// <item>身份缺失 / 非法 → <see cref="ErrorCodes.Unauthorized"/>；</item>
    /// <item>账号不存在或已删除 → <see cref="ErrorCodes.Unauthorized"/>；</item>
    /// <item>账号已禁用 → <see cref="ErrorCodes.Forbidden"/>；</item>
    /// <item>缺少既有「仓库资料」（<c>warehouse</c>）菜单授权（含被撤销授权）→ <see cref="ErrorCodes.Forbidden"/>。</item>
    /// </list>
    /// 判定发生在任何仓库读取 / 写入<b>之前</b>；每次请求重新解析，菜单或账号状态变更后立即收敛；
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
    /// 仓库持久化字段的有界校验（新增 / 修改都在落库之前调用）：
    /// <list type="bullet">
    /// <item>仓库编码：非空且 ≤ <see cref="MaxWarehouseCodeLength"/>；</item>
    /// <item>仓库名称：非空且 ≤ <see cref="MaxWarehouseNameLength"/>；</item>
    /// <item>地址 / 负责人 / 联系电话 / 备注：不超各自持久化长度上限；</item>
    /// <item>状态：仅接受 <see cref="EnabledStatus"/>（启用）或 <see cref="DisabledStatus"/>（停用）。</item>
    /// </list>
    /// 任一项不满足即按 <see cref="ErrorCodes.InvalidParameter"/> 的受控错误拒绝，<b>不</b>静默截断 / 夹取或改写任何字段，
    /// 被拒绝的写入不落任何 <c>BaseWarehouses</c> 行。
    /// </summary>
    public static void Validate(BaseWarehouse? entity)
    {
        if (entity is null)
            throw BusinessException.InvalidParameter("仓库数据不能为空");

        EnsureRequiredText(entity.WarehouseCode, MaxWarehouseCodeLength, "编码");
        EnsureRequiredText(entity.WarehouseName, MaxWarehouseNameLength, "名称");

        EnsureOptionalText(entity.Address, MaxAddressLength, "地址");
        EnsureOptionalText(entity.Manager, MaxManagerLength, "负责人");
        EnsureOptionalText(entity.Phone, MaxPhoneLength, "联系电话");
        EnsureOptionalText(entity.Remark, MaxRemarkLength, "备注");

        if (entity.Status != EnabledStatus && entity.Status != DisabledStatus)
            throw BusinessException.InvalidParameter("仓库状态只能是 1（启用）或 0（停用）");
    }

    /// <summary>必填文本校验：空白即拒绝，超过持久化长度上限即拒绝（不做静默截断）。</summary>
    private static void EnsureRequiredText(string? value, int maxLength, string fieldText)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw BusinessException.InvalidParameter($"仓库{fieldText}不能为空");
        if (value.Length > maxLength)
            throw BusinessException.InvalidParameter($"仓库{fieldText}长度不能超过 {maxLength} 个字符");
    }

    /// <summary>可选文本校验：仅在提供且超过持久化长度上限时拒绝（不做静默截断）。</summary>
    private static void EnsureOptionalText(string? value, int maxLength, string fieldText)
    {
        if (value is not null && value.Length > maxLength)
            throw BusinessException.InvalidParameter($"仓库{fieldText}长度不能超过 {maxLength} 个字符");
    }
}
