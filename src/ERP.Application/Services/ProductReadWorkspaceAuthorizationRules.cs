using ERP.Application.Common;
using ERP.Application.Interfaces;

namespace ERP.Application.Services;

/// <summary>
/// 商品只读工作台（商品图片库 <c>api/base/product-images</c> 与出口字段完整度
/// <c>api/base/products/export-field-completeness</c>）在读取任何 <c>BaseProducts</c> 行之前的
/// 实时身份 / 账号状态 / 既有「商品资料」功能菜单授权（ERP-461）。
/// <para>两个工作台都是从<b>商品资料页工具栏</b>打开的<b>只读</b>工作区（沿 ERP-039 / ERP-107），
/// 但它们是与 <c>api/base/products</c> <b>并列</b>的独立路由：仅凭 <c>[Authorize]</c> 时任何已认证账号
/// 都能枚举整张商品主表，因此必须与 ERP-452 的 <c>api/base/products</c> 采用<b>完全同源</b>的
/// 实时授权口径（<see cref="ProductAuthorizationRules.EnsureAuthorizedAsync"/>）。</para>
/// <list type="number">
/// <item><b>实时授权</b>（<see cref="EnsureAuthorizedAsync"/>）：分页图片库查询与出口字段完整度工作台
/// <b>每一个</b>路由在读取任何商品行之前重新解析实时身份（缺失 / 非法 / 账号不存在或已删除按未认证，
/// 禁用按权限不足）与既有「商品资料」（<c>product</c>）功能菜单授权（缺菜单 / 被撤销按权限不足），
/// 一律 fail closed，并复用既有受控、不泄露数据的错误文案；</item>
/// <item><b>只读</b>：两个工作台都<b>只有</b> GET 端点，没有任何新增 / 修改 / 删除 / 保存路径，
/// 不执行任意 SQL、不读写 OSS、不做服务端图片抓取。</item>
/// </list>
/// <para>边界（重要）：本护栏<b>不</b>新增任何菜单 / 角色 / 用户授权（直接复用既有 <c>product</c> 菜单），
/// <b>不</b>改变既有商品身份 / 字段与 ERP-452 的 <c>api/base/products</c> 契约，<b>不</b>因身份缺失而降级为管理员，
/// 也<b>不</b>新增匿名 / 特权旁路或依赖 <c>Request.Path</c> / 环境的放行条件。</para>
/// </summary>
public static class ProductReadWorkspaceAuthorizationRules
{
    /// <summary>所需既有功能菜单编码（与 <see cref="ProductAuthorizationRules.RequiredMenuCode"/> / <c>SeedData.Menus</c> 同源，<b>不新增菜单</b>）</summary>
    public const string RequiredMenuCode = ProductAuthorizationRules.RequiredMenuCode;

    /// <summary>既有功能菜单中文文案</summary>
    public const string RequiredMenuText = ProductAuthorizationRules.RequiredMenuText;

    /// <summary>无身份 / 非法身份的拒绝文案（与商品资料护栏同源，受控、不泄露数据）</summary>
    public const string UnauthorizedText = ProductAuthorizationRules.UnauthorizedText;

    /// <summary>账号不存在 / 已删除的拒绝文案（与商品资料护栏同源，受控、不泄露数据）</summary>
    public const string UserDeletedText = ProductAuthorizationRules.UserDeletedText;

    /// <summary>账号已禁用的拒绝文案（与商品资料护栏同源，受控、不泄露数据）</summary>
    public const string UserDisabledText = ProductAuthorizationRules.UserDisabledText;

    /// <summary>缺少既有「商品资料」菜单授权时的拒绝文案（与商品资料护栏同源，受控、不泄露数据）</summary>
    public const string MenuDeniedText = ProductAuthorizationRules.MenuDeniedText;

    /// <summary>授权口径文案（接口 / 文档同源）</summary>
    public const string AuthorizationRuleText =
        "商品只读工作台（商品图片库分页查询、出口字段完整度工作台）在读取任何商品行之前，" +
        "都会重新校验实时身份（缺失 / 非法 / 已删除按未认证，禁用按权限不足，一律 fail closed）" +
        "与既有「商品资料」（product）单项功能菜单授权；菜单授权复用既有「角色 → 菜单」口径，" +
        "每次请求重新查询，撤销后下一次请求立即收敛，绝不缓存。";

    /// <summary>授权边界文案（不改写既有商品 / 只读工作台契约）</summary>
    public const string AuthorizationBoundaryText =
        "本护栏只新增「读取前的实时授权」：两个工作台保持严格只读（无 Add / Update / Remove / SaveChanges、" +
        "无任意 SQL、无 OSS 或图片抓取），不改变既有商品身份 / 字段、ERP-039 图片库与 ERP-107 出口字段完整度的" +
        "响应契约，以及 ERP-452 的 api/base/products 契约；不新增表 / 列 / 实体 / 菜单 / 权限 / 用户授权，" +
        "也不把空身份当作管理员。";

    /// <summary>
    /// 实时身份 / 账号状态 / 既有「商品资料」功能菜单三项校验（fail closed），返回本次请求的权威用户 Id。
    /// <para>直接复用 <see cref="ProductAuthorizationRules.EnsureAuthorizedAsync"/>，因此两个只读工作台与
    /// <c>api/base/products</c> 采用<b>同一</b>权威判定与<b>同一</b>受控错误文案：</para>
    /// <list type="bullet">
    /// <item>身份缺失 / 非法 → <see cref="ErrorCodes.Unauthorized"/>；</item>
    /// <item>账号不存在或已删除 → <see cref="ErrorCodes.Unauthorized"/>；</item>
    /// <item>账号已禁用 → <see cref="ErrorCodes.Forbidden"/>；</item>
    /// <item>缺少既有「商品资料」（<c>product</c>）菜单授权（含被撤销授权）→ <see cref="ErrorCodes.Forbidden"/>。</item>
    /// </list>
    /// 判定发生在任何商品读取<b>之前</b>；每次请求重新解析，菜单或账号状态变更后立即收敛；
    /// <b>不</b>新增任何菜单 / 角色 / 用户授权，也<b>不</b>因身份缺失而降级为管理员。
    /// </summary>
    public static Task<long> EnsureAuthorizedAsync(
        IErpDbContext db, long? userId, CancellationToken ct = default)
        => ProductAuthorizationRules.EnsureAuthorizedAsync(db, userId, ct);
}
