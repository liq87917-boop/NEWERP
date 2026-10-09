using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ERP.Application.Services;

/// <summary>
/// 客户跟进记录（<see cref="CustomerFollowUp"/>，<c>api/crm/follow-ups</c>，客户跟进记录页面维护）的
/// 实时身份 / 既有功能菜单授权、ERP-097 业务员数据范围与有界字段校验护栏（ERP-458）。
/// <list type="number">
/// <item><b>实时授权</b>（<see cref="EnsureAuthorizedAsync"/>）：分页 / 全部 / 按主键读取与新增 / 修改 / 删除 /
/// 批量删除<b>每一个</b>路由在读取或写入任何 <c>CustomerFollowUps</c> 行<b>之前</b>都重新解析实时身份
/// （缺失 / 非法 / 账号不存在或已删除按未认证，禁用按权限不足）与既有「客户跟进记录」（<c>customer-follow</c>）
/// 功能菜单授权（缺菜单 / 被撤销按权限不足），一律 fail closed；</item>
/// <item><b>数据范围</b>（<see cref="EnsureAuthorizedScopeAsync"/>）：复用 <see cref="SalespersonDataScopeService"/>
/// （ERP-097 唯一权威口径）解析当前账号客户范围，读取按 <c>CustomerFollowUp.CustomerId</c> 下推过滤，
/// 新增 / 修改要求写入的 <c>CustomerId</c> 落在范围内；</item>
/// <item><b>有界字段校验</b>（<see cref="Validate"/>）：新增 / 修改在落库之前校验跟进编号非空且在持久化长度上限内、
/// 其余文本字段不超各自持久化长度上限；<see cref="EnsureWritableReferencesAsync"/> 另校验
/// <c>CustomerId</c> 非空且指向已知的未删除客户、<c>SalesmanId</c>（非空时）指向已知的未删除员工。</item>
/// </list>
/// <para>边界（重要）：本护栏只新增「读取 / 写入前的判定」，<b>不</b>静默截断 / 夹取或改写任何跟进字段，
/// 也<b>不</b>改变既有分页 / 响应契约、软删除语义与 <see cref="GenericService{TEntity}"/> 契约；
/// <b>不</b>新增任何表 / 列 / 实体 / 菜单 / 权限 / 用户授权，不伪造任何授权，<b>不</b>因身份缺失而降级为管理员，
/// 也<b>不</b>新增匿名 / 特权旁路。</para>
/// </summary>
public static class CustomerFollowUpAuthorizationRules
{
    /// <summary>所需既有功能菜单编码（与 <c>SchemaUpgrader</c> 的 <c>customer-follow</c> 同源，<b>不新增菜单</b>）</summary>
    public const string RequiredMenuCode = "customer-follow";

    /// <summary>既有功能菜单中文文案</summary>
    public const string RequiredMenuText = "客户跟进记录";

    /// <summary>跟进编号持久化长度上限（<c>NVARCHAR(50)</c>，与实体 <c>[MaxLength(50)]</c> 一致）</summary>
    public const int MaxFollowNoLength = 50;

    /// <summary>客户名称持久化长度上限（<c>NVARCHAR(200)</c>，与实体 <c>[MaxLength(200)]</c> 一致）</summary>
    public const int MaxCustomerNameLength = 200;

    /// <summary>跟进方式持久化长度上限（<c>NVARCHAR(30)</c>，与实体 <c>[MaxLength(30)]</c> 一致）</summary>
    public const int MaxFollowTypeLength = 30;

    /// <summary>对接人持久化长度上限（<c>NVARCHAR(100)</c>，与实体 <c>[MaxLength(100)]</c> 一致）</summary>
    public const int MaxContactPersonLength = 100;

    /// <summary>跟进人姓名持久化长度上限（<c>NVARCHAR(50)</c>，与实体 <c>[MaxLength(50)]</c> 一致）</summary>
    public const int MaxSalesmanNameLength = 50;

    /// <summary>跟进主题持久化长度上限（<c>NVARCHAR(100)</c>，与实体 <c>[MaxLength(100)]</c> 一致）</summary>
    public const int MaxSubjectLength = 100;

    /// <summary>跟进内容持久化长度上限（<c>NVARCHAR(1000)</c>，与实体 <c>[MaxLength(1000)]</c> 一致）</summary>
    public const int MaxContentLength = 1000;

    /// <summary>跟进结果持久化长度上限（<c>NVARCHAR(30)</c>，与实体 <c>[MaxLength(30)]</c> 一致）</summary>
    public const int MaxResultLength = 30;

    /// <summary>备注持久化长度上限（<c>NVARCHAR(500)</c>，与实体 <c>[MaxLength(500)]</c> 一致）</summary>
    public const int MaxRemarkLength = 500;

    /// <summary>无身份 / 非法身份的拒绝文案（受控、不泄露数据）</summary>
    public const string UnauthorizedText = "请先登录后再访问客户跟进记录";

    /// <summary>账号不存在 / 已删除的拒绝文案（受控、不泄露数据）</summary>
    public const string UserDeletedText = "登录账号不存在或已删除，禁止访问客户跟进记录";

    /// <summary>账号已禁用的拒绝文案（受控、不泄露数据）</summary>
    public const string UserDisabledText = "登录账号已禁用，禁止访问客户跟进记录（fail closed）";

    /// <summary>缺少既有「客户跟进记录」菜单授权时的拒绝文案（受控、不泄露数据）</summary>
    public const string MenuDeniedText =
        "当前账号没有「客户跟进记录」（customer-follow）模块授权：" +
        "拒绝访问客户跟进记录（fail closed，不返回 / 不新增 / 不改写任何跟进记录）";

    /// <summary>写入的客户不在当前数据范围时的拒绝文案（受控，不泄露范围外归属）</summary>
    public const string CustomerOutOfScopeText =
        "客户跟进记录只能关联当前账号数据范围内的客户（fail closed，不新增 / 不改写任何跟进记录）";

    /// <summary>授权口径文案（接口 / 文档同源）</summary>
    public const string AuthorizationRuleText =
        "客户跟进记录（分页 / 全部 / 按主键读取 / 新增 / 修改 / 删除 / 批量删除）在读取或写入任何跟进记录之前，" +
        "都会重新校验实时身份（缺失 / 非法 / 已删除按未认证，禁用按权限不足，一律 fail closed）" +
        "与既有「客户跟进记录」（customer-follow）功能菜单授权；菜单授权复用既有「角色 → 菜单」口径，" +
        "每次请求重新查询，撤销后下一次请求立即收敛。";

    /// <summary>数据范围与校验口径文案（接口 / 文档同源）</summary>
    public const string ScopeRuleText =
        "客户跟进记录读取按 ERP-097 业务员数据范围（SalespersonDataScopeService，唯一权威口径）下推过滤，" +
        "受限账号只看到自己被分配客户的跟进记录；新增 / 修改要求写入的 CustomerId 落在当前范围内，" +
        "且 CustomerId 必须指向已知的未删除客户、SalesmanId（非空时）必须指向已知的未删除员工，" +
        "被拒绝的写入不落任何行、不改写任何既有行。";

    /// <summary>授权边界文案（不改写既有业务口径）</summary>
    public const string AuthorizationBoundaryText =
        "本护栏只新增「读取 / 写入前的授权、数据范围与有界字段校验」：不改变分页 / 响应契约、软删除语义、" +
        "GenericService 契约与其它派生自 BaseCrudController 的控制器的既有语义，也不静默截断 / 夹取或改写任何字段；" +
        "不新增表 / 列 / 实体 / 菜单 / 权限 / 用户授权，也不把空身份当作管理员。";

    /// <summary>
    /// 实时身份 / 账号状态 / 既有「客户跟进记录」功能菜单三项校验（fail closed），返回本次请求的权威用户 Id。
    /// <list type="bullet">
    /// <item>身份缺失 / 非法 → <see cref="ErrorCodes.Unauthorized"/>；</item>
    /// <item>账号不存在或已删除 → <see cref="ErrorCodes.Unauthorized"/>；</item>
    /// <item>账号已禁用 → <see cref="ErrorCodes.Forbidden"/>；</item>
    /// <item>缺少既有「客户跟进记录」（<c>customer-follow</c>）菜单授权（含被撤销授权）→ <see cref="ErrorCodes.Forbidden"/>。</item>
    /// </list>
    /// 判定发生在任何跟进记录读取 / 写入<b>之前</b>；每次请求重新解析，菜单或账号状态变更后立即收敛；
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
    /// 实时授权（<see cref="EnsureAuthorizedAsync"/>）+ ERP-097 业务员数据范围解析（
    /// <see cref="SalespersonDataScopeService.ResolveAsync"/>，唯一权威口径）。返回的范围在同一请求内复用，绝不缓存。
    /// </summary>
    public static async Task<SalespersonDataScope> EnsureAuthorizedScopeAsync(
        IErpDbContext db, long? userId, CancellationToken ct = default)
    {
        var resolvedUserId = await EnsureAuthorizedAsync(db, userId, ct);
        return await SalespersonDataScopeService.ResolveAsync(db, resolvedUserId);
    }

    /// <summary>
    /// 跟进记录持久化字段的有界校验（新增 / 修改都在落库之前调用）：
    /// <list type="bullet">
    /// <item>跟进编号（<c>FollowNo</c>）：非空且 ≤ <see cref="MaxFollowNoLength"/>；</item>
    /// <item>客户名称（<c>CustomerName</c>）：≤ <see cref="MaxCustomerNameLength"/>；</item>
    /// <item>跟进方式（<c>FollowType</c>）：≤ <see cref="MaxFollowTypeLength"/>；</item>
    /// <item>对接人（<c>ContactPerson</c>）：≤ <see cref="MaxContactPersonLength"/>；</item>
    /// <item>跟进人姓名（<c>SalesmanName</c>）：≤ <see cref="MaxSalesmanNameLength"/>；</item>
    /// <item>跟进主题（<c>Subject</c>）：≤ <see cref="MaxSubjectLength"/>；</item>
    /// <item>跟进内容（<c>Content</c>）：≤ <see cref="MaxContentLength"/>；</item>
    /// <item>跟进结果（<c>Result</c>）：≤ <see cref="MaxResultLength"/>；</item>
    /// <item>备注（<c>Remark</c>）：≤ <see cref="MaxRemarkLength"/>。</item>
    /// </list>
    /// 任一项不满足即按 <see cref="ErrorCodes.InvalidParameter"/> 的受控错误拒绝，<b>不</b>静默截断 / 夹取或改写任何字段，
    /// 被拒绝的写入不落任何 <c>CustomerFollowUps</c> 行（也<b>不</b>改写任何既有行）。
    /// </summary>
    public static void Validate(CustomerFollowUp? entity)
    {
        if (entity is null)
            throw BusinessException.InvalidParameter("客户跟进记录数据不能为空");

        EnsureRequiredText(entity.FollowNo, MaxFollowNoLength, "跟进编号");
        EnsureOptionalText(entity.CustomerName, MaxCustomerNameLength, "客户名称");
        EnsureOptionalText(entity.FollowType, MaxFollowTypeLength, "跟进方式");
        EnsureOptionalText(entity.ContactPerson, MaxContactPersonLength, "对接人");
        EnsureOptionalText(entity.SalesmanName, MaxSalesmanNameLength, "跟进人姓名");
        EnsureOptionalText(entity.Subject, MaxSubjectLength, "跟进主题");
        EnsureOptionalText(entity.Content, MaxContentLength, "跟进内容");
        EnsureOptionalText(entity.Result, MaxResultLength, "跟进结果");
        EnsureOptionalText(entity.Remark, MaxRemarkLength, "备注");
    }

    /// <summary>
    /// 新增 / 修改的引用与数据范围校验（落库之前调用）：
    /// <list type="bullet">
    /// <item><c>CustomerId</c> 必须非空且为正，并落在当前账号 ERP-097 客户数据范围之内（范围外按权限不足拒绝，不泄露归属）；</item>
    /// <item><c>CustomerId</c> 必须指向已知的未删除客户；</item>
    /// <item><c>SalesmanId</c> 可空（0 / null 视为未指定），非空时必须指向已知的未删除员工。</item>
    /// </list>
    /// 被拒绝的写入不落任何行、不改写任何既有行。
    /// </summary>
    public static async Task EnsureWritableReferencesAsync(
        IErpDbContext db, SalespersonDataScope scope, CustomerFollowUp entity, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(scope);
        if (entity is null)
            throw BusinessException.InvalidParameter("客户跟进记录数据不能为空");

        if (entity.CustomerId is null or <= 0)
            throw BusinessException.InvalidParameter("客户跟进记录必须关联客户（CustomerId 不能为空且必须为正整数）");

        if (!scope.AllowsCustomer(entity.CustomerId))
            throw new BusinessException(CustomerOutOfScopeText, ErrorCodes.Forbidden);

        var customerExists = await db.BaseCustomers.AsNoTracking()
            .AnyAsync(c => c.Id == entity.CustomerId.Value && !c.IsDeleted, ct);
        if (!customerExists)
            throw BusinessException.InvalidParameter("客户跟进记录的客户（CustomerId）必须指向已知的未删除客户");

        if (entity.SalesmanId is { } salesmanId && salesmanId != 0)
        {
            if (salesmanId < 0)
                throw BusinessException.InvalidParameter("客户跟进记录的跟进人（SalesmanId）不能为负数");

            var salesmanExists = await db.BaseEmployees.AsNoTracking()
                .AnyAsync(e => e.Id == salesmanId && !e.IsDeleted, ct);
            if (!salesmanExists)
                throw BusinessException.InvalidParameter("客户跟进记录的跟进人（SalesmanId）必须指向已知的未删除员工");
        }
    }

    /// <summary>必填文本校验：空白即拒绝，超过持久化长度上限即拒绝（不做静默截断）。</summary>
    private static void EnsureRequiredText(string? value, int maxLength, string fieldText)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw BusinessException.InvalidParameter($"客户跟进记录{fieldText}不能为空");
        if (value.Length > maxLength)
            throw BusinessException.InvalidParameter($"客户跟进记录{fieldText}长度不能超过 {maxLength} 个字符");
    }

    /// <summary>可选文本校验：仅在提供且超过持久化长度上限时拒绝（不做静默截断）。</summary>
    private static void EnsureOptionalText(string? value, int maxLength, string fieldText)
    {
        if (value is not null && value.Length > maxLength)
            throw BusinessException.InvalidParameter($"客户跟进记录{fieldText}长度不能超过 {maxLength} 个字符");
    }
}
