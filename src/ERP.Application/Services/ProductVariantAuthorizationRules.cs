using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ERP.Application.Services;

/// <summary>
/// 商品规格变体（颜色 / 尺码 SKU，ERP-037）在读取 / 写入前的实时授权与权威商品引用守卫（ERP-444）。
/// <para>调用顺序（两者都在任何 <c>BaseProductVariants</c> 读取 / 写入<b>之前</b>）：</para>
/// <list type="number">
/// <item><b>实时授权</b>（<see cref="EnsureAuthorizedAsync"/>）：身份缺失 / 非法 → 未认证；账号不存在或已删除 →
/// 未认证；账号已禁用 → 权限不足；缺少既有「商品资料」（<c>product</c>）功能菜单 → 权限不足（一律 fail closed）；</item>
/// <item><b>权威商品引用</b>（<see cref="EnsureLiveProductAsync"/>）：商品必须存在、未删除且启用，
/// 否则按受控错误拒绝 —— 外部 / 已删除商品 <c>NotFound</c>，已停用商品 <c>InvalidParameter</c>。</item>
/// </list>
/// <para>边界（重要）：本护栏<b>只新增</b>「读取 / 写入前的判定」，不改变 ERP-037 的编码唯一、启用颜色/尺码组合唯一、
/// 状态取值（0 / 1）、规范化或上下限语义；<b>不</b>新增任何表 / 列 / 实体 / 菜单 / 权限 / 用户授权，
/// <b>不</b>因身份缺失而降级为管理员，也<b>不</b>新增匿名 / 特权旁路。</para>
/// </summary>
public static class ProductVariantAuthorizationRules
{
    /// <summary>所需既有功能菜单编码（与 <c>SeedData.Menus</c> 同源，<b>不新增菜单</b>）</summary>
    public const string ProductMenuCode = "product";

    /// <summary>既有功能菜单中文文案</summary>
    public const string ProductMenuText = "商品资料";

    /// <summary>商品启用状态（与 <c>BaseProduct.Status</c> / <c>SeedData</c> 口径一致）</summary>
    public const int ActiveStatus = 1;

    /// <summary>无身份 / 非法身份的拒绝文案（受控、不泄露数据）</summary>
    public const string UnauthorizedText = "请先登录后再访问商品规格变体";

    /// <summary>账号不存在 / 已删除的拒绝文案（受控、不泄露数据）</summary>
    public const string UserDeletedText = "登录账号不存在或已删除，禁止访问商品规格变体";

    /// <summary>账号已禁用的拒绝文案（受控、不泄露数据）</summary>
    public const string UserDisabledText = "登录账号已禁用，禁止访问商品规格变体（fail closed）";

    /// <summary>缺少既有「商品资料」菜单授权时的拒绝文案（受控、不泄露数据）</summary>
    public const string ProductMenuDeniedText =
        "当前账号没有「商品资料」（product）模块授权：" +
        "拒绝访问商品规格变体（fail closed，不返回 / 不新增 / 不改写任何规格）";

    /// <summary>授权口径文案（接口 / 文档同源）</summary>
    public const string AuthorizationRuleText =
        "商品规格变体（明细列表 / 可选用选项 / 新增 / 修改 / 停用 / 启用 / 删除）在读取或写入任何规格之前，" +
        "都会重新校验实时身份（缺失 / 非法 / 已删除按未认证，禁用按权限不足，一律 fail closed）" +
        "与既有「商品资料」（product）单项功能菜单授权；菜单授权复用既有「角色 → 菜单」口径，" +
        "每次请求重新查询，撤销后下一次请求立即收敛。";

    /// <summary>授权边界文案（不改写 ERP-037 的业务口径）</summary>
    public const string AuthorizationBoundaryText =
        "本护栏只新增「读取 / 写入前的授权与权威商品引用判定」：不改变编码规范化与唯一性、" +
        "「启用中颜色 + 尺码组合」唯一性、状态取值（0 / 1）与规格条数上限等既有语义；" +
        "不新增表 / 列 / 实体 / 菜单 / 权限 / 用户授权，也不把空身份当作管理员。";

    /// <summary>
    /// 实时身份 / 账号状态 / 既有「商品资料」功能菜单三项校验（fail closed），返回本次请求的权威用户 Id。
    /// <list type="bullet">
    /// <item>身份缺失 / 非法 → <see cref="ErrorCodes.Unauthorized"/>；</item>
    /// <item>账号不存在或已删除 → <see cref="ErrorCodes.Unauthorized"/>；</item>
    /// <item>账号已禁用 → <see cref="ErrorCodes.Forbidden"/>；</item>
    /// <item>缺少既有「商品资料」（<c>product</c>）菜单授权（含被撤销授权）→ <see cref="ErrorCodes.Forbidden"/>。</item>
    /// </list>
    /// 判定发生在任何规格读取 / 写入<b>之前</b>；每次请求重新解析，菜单或账号状态变更后立即收敛；
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

        if (!menuCodes.Contains(ProductMenuCode, StringComparer.OrdinalIgnoreCase))
            throw new BusinessException(ProductMenuDeniedText, ErrorCodes.Forbidden);

        return user.Id;
    }

    /// <summary>
    /// 权威商品引用校验（读取与写入都先经过这里）：商品必须存在、未删除且<b>启用</b>。
    /// <list type="bullet">
    /// <item>商品 Id 非法 → <see cref="ErrorCodes.InvalidParameter"/>；</item>
    /// <item>商品不存在 / 已删除 → <see cref="ErrorCodes.NotFound"/>；</item>
    /// <item>商品已停用 → <see cref="ErrorCodes.InvalidParameter"/>（fail closed，历史规格行仍保留在库中）。</item>
    /// </list>
    /// 停用商品不再允许读取或维护其规格，避免把已停用商品重新变成可下单 / 可备货的 SKU 指引。
    /// </summary>
    public static async Task<BaseProduct> EnsureLiveProductAsync(
        IErpDbContext db, long productId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ct.ThrowIfCancellationRequested();

        if (productId <= 0)
            throw BusinessException.InvalidParameter("商品 Id 不合法");

        var product = await db.BaseProducts.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == productId && !p.IsDeleted, ct)
            ?? throw BusinessException.NotFound($"商品（Id={productId}）不存在或已删除，不能读取或维护规格");

        if (product.Status != ActiveStatus)
            throw BusinessException.InvalidParameter(
                $"商品「{product.ProductName}」已停用，不能读取或维护规格（fail closed，历史规格仍保留在库中）");

        return product;
    }
}
