using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ERP.Application.Services;

/// <summary>
/// 样品管理（<see cref="Sample"/>，<c>api/crm/samples</c>，样品管理页面维护）的实时身份 / 既有功能菜单授权、
/// ERP-097 业务员数据范围与有界字段校验护栏（ERP-459）。
/// <list type="number">
/// <item><b>实时授权</b>（<see cref="EnsureAuthorizedAsync"/>）：分页 / 全部 / 按主键读取与新增 / 修改 / 删除 /
/// 批量删除<b>每一个</b>路由在读取或写入任何 <c>Samples</c> 行<b>之前</b>都重新解析实时身份
/// （缺失 / 非法 / 账号不存在或已删除按未认证，禁用按权限不足）与既有「样品管理」（<c>sample</c>）
/// 功能菜单授权（缺菜单 / 被撤销按权限不足），一律 fail closed；</item>
/// <item><b>数据范围</b>（<see cref="EnsureAuthorizedScopeAsync"/>）：复用 <see cref="SalespersonDataScopeService"/>
/// （ERP-097 唯一权威口径）解析当前账号客户范围，读取按 <c>Sample.CustomerId</c> 下推过滤，
/// 新增 / 修改要求写入的 <c>CustomerId</c> 落在范围内；</item>
/// <item><b>有界字段校验</b>（<see cref="Validate"/>）：新增 / 修改在落库之前校验样品编号非空且在持久化长度上限内、
/// 其余文本字段不超各自持久化长度上限、数量与样品费非负且落在 <c>DECIMAL(18,4)</c> 可存储范围内；
/// <see cref="EnsureWritableReferencesAsync"/> 另校验 <c>CustomerId</c> 非空且指向已知的未删除客户、
/// <c>ProductId</c> / <c>SalesmanId</c>（非空时）分别指向已知的未删除商品 / 员工。</item>
/// </list>
/// <para>边界（重要）：本护栏只新增「读取 / 写入前的判定」，<b>不</b>静默截断 / 夹取或改写任何样品字段，
/// 也<b>不</b>改变既有分页 / 响应契约、软删除语义、ERP-063 附件证据归属（<c>OwnerType = Sample</c>）契约与
/// <see cref="GenericService{TEntity}"/> 契约；<b>不</b>新增任何表 / 列 / 实体 / 菜单 / 权限 / 用户授权，
/// 不伪造任何授权，<b>不</b>因身份缺失而降级为管理员，也<b>不</b>新增匿名 / 特权旁路。</para>
/// </summary>
public static class SampleAuthorizationRules
{
    /// <summary>所需既有功能菜单编码（与 <c>SchemaUpgrader</c> 的 <c>sample</c> 同源，<b>不新增菜单</b>）</summary>
    public const string RequiredMenuCode = "sample";

    /// <summary>既有功能菜单中文文案</summary>
    public const string RequiredMenuText = "样品管理";

    /// <summary>样品编号持久化长度上限（<c>NVARCHAR(50)</c>，与实体 <c>[MaxLength(50)]</c> 一致）</summary>
    public const int MaxSampleNoLength = 50;

    /// <summary>样品类型持久化长度上限（<c>NVARCHAR(30)</c>，与实体 <c>[MaxLength(30)]</c> 一致）</summary>
    public const int MaxSampleTypeLength = 30;

    /// <summary>单位持久化长度上限（<c>NVARCHAR(20)</c>，与实体 <c>[MaxLength(20)]</c> 一致）</summary>
    public const int MaxUnitLength = 20;

    /// <summary>币种持久化长度上限（<c>NVARCHAR(20)</c>，与实体 <c>[MaxLength(20)]</c> 一致）</summary>
    public const int MaxCurrencyLength = 20;

    /// <summary>快递公司持久化长度上限（<c>NVARCHAR(50)</c>，与实体 <c>[MaxLength(50)]</c> 一致）</summary>
    public const int MaxExpressLength = 50;

    /// <summary>运单号持久化长度上限（<c>NVARCHAR(50)</c>，与实体 <c>[MaxLength(50)]</c> 一致）</summary>
    public const int MaxTrackingNoLength = 50;

    /// <summary>客户反馈结果持久化长度上限（<c>NVARCHAR(30)</c>，与实体 <c>[MaxLength(30)]</c> 一致）</summary>
    public const int MaxResultLength = 30;

    /// <summary>客户名称持久化长度上限（<c>NVARCHAR(200)</c>，与实体 <c>[MaxLength(200)]</c> 一致）</summary>
    public const int MaxCustomerNameLength = 200;

    /// <summary>商品 / 样品名称持久化长度上限（<c>NVARCHAR(200)</c>，与实体 <c>[MaxLength(200)]</c> 一致）</summary>
    public const int MaxProductNameLength = 200;

    /// <summary>规格持久化长度上限（<c>NVARCHAR(200)</c>，与实体 <c>[MaxLength(200)]</c> 一致）</summary>
    public const int MaxSpecLength = 200;

    /// <summary>业务员姓名持久化长度上限（<c>NVARCHAR(50)</c>，与实体 <c>[MaxLength(50)]</c> 一致）</summary>
    public const int MaxSalesmanNameLength = 50;

    /// <summary>备注持久化长度上限（<c>NVARCHAR(500)</c>，与实体 <c>[MaxLength(500)]</c> 一致）</summary>
    public const int MaxRemarkLength = 500;

    /// <summary>
    /// 样品数值列（<c>Quantity</c> / <c>SampleFee</c>）的持久化可存储上限（<c>DECIMAL(18,4)</c>：
    /// 14 位整数 + 4 位小数），超出即无法落库，必须在写入前按参数错误拒绝。
    /// </summary>
    public const decimal MaxDecimalMagnitude = 99999999999999.9999m;
    /// <summary>无身份 / 非法身份的拒绝文案（受控、不泄露数据）</summary>
    public const string UnauthorizedText = "请先登录后再访问样品管理";

    /// <summary>账号不存在 / 已删除的拒绝文案（受控、不泄露数据）</summary>
    public const string UserDeletedText = "登录账号不存在或已删除，禁止访问样品管理";

    /// <summary>账号已禁用的拒绝文案（受控、不泄露数据）</summary>
    public const string UserDisabledText = "登录账号已禁用，禁止访问样品管理（fail closed）";

    /// <summary>缺少既有「样品管理」菜单授权时的拒绝文案（受控、不泄露数据）</summary>
    public const string MenuDeniedText =
        "当前账号没有「样品管理」（sample）模块授权：" +
        "拒绝访问样品管理（fail closed，不返回 / 不新增 / 不改写 / 不删除任何样品）";

    /// <summary>写入的客户不在当前数据范围时的拒绝文案（受控，不泄露范围外归属）</summary>
    public const string CustomerOutOfScopeText =
        "样品只能关联当前账号数据范围内的客户（fail closed，不新增 / 不改写任何样品）";

    /// <summary>授权口径文案（接口 / 文档同源）</summary>
    public const string AuthorizationRuleText =
        "样品管理（分页 / 全部 / 按主键读取 / 新增 / 修改 / 删除 / 批量删除）在读取或写入任何样品之前，" +
        "都会重新校验实时身份（缺失 / 非法 / 已删除按未认证，禁用按权限不足，一律 fail closed）" +
        "与既有「样品管理」（sample）功能菜单授权；菜单授权复用既有「角色 → 菜单」口径，" +
        "每次请求重新查询，撤销后下一次请求立即收敛。";

    /// <summary>数据范围与校验口径文案（接口 / 文档同源）</summary>
    public const string ScopeRuleText =
        "样品读取按 ERP-097 业务员数据范围（SalespersonDataScopeService，唯一权威口径）下推过滤，" +
        "受限账号只看到自己被分配客户的样品；新增 / 修改要求写入的 CustomerId 落在当前范围内，" +
        "且 CustomerId 必须指向已知的未删除客户、ProductId / SalesmanId（非空时）分别指向已知的未删除商品 / 员工，" +
        "被拒绝的写入不落任何行、不改写任何既有行。";

    /// <summary>授权边界文案（不改写既有业务口径）</summary>
    public const string AuthorizationBoundaryText =
        "本护栏只新增「读取 / 写入前的授权、数据范围与有界字段校验」：不改变分页 / 响应契约、软删除语义、" +
        "ERP-063 附件证据归属（OwnerType = Sample）契约、GenericService 契约与其它派生自 BaseCrudController " +
        "的控制器的既有语义，也不静默截断 / 夹取或改写任何字段；" +
        "不新增表 / 列 / 实体 / 菜单 / 权限 / 用户授权，也不把空身份当作管理员。";

    /// <summary>
    /// 实时身份 / 账号状态 / 既有「样品管理」功能菜单三项校验（fail closed），返回本次请求的权威用户 Id。
    /// <list type="bullet">
    /// <item>身份缺失 / 非法 → <see cref="ErrorCodes.Unauthorized"/>；</item>
    /// <item>账号不存在或已删除 → <see cref="ErrorCodes.Unauthorized"/>；</item>
    /// <item>账号已禁用 → <see cref="ErrorCodes.Forbidden"/>；</item>
    /// <item>缺少既有「样品管理」（<c>sample</c>）菜单授权（含被撤销授权）→ <see cref="ErrorCodes.Forbidden"/>。</item>
    /// </list>
    /// 判定发生在任何样品读取 / 写入<b>之前</b>；每次请求重新解析，菜单或账号状态变更后立即收敛；
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
    /// 样品持久化字段的有界校验（新增 / 修改都在落库之前调用）：
    /// <list type="bullet">
    /// <item>样品编号（<c>SampleNo</c>）：非空且 ≤ <see cref="MaxSampleNoLength"/>；</item>
    /// <item>样品类型（<c>SampleType</c>）：≤ <see cref="MaxSampleTypeLength"/>；</item>
    /// <item>单位（<c>Unit</c>）：≤ <see cref="MaxUnitLength"/>；</item>
    /// <item>币种（<c>Currency</c>）：≤ <see cref="MaxCurrencyLength"/>；</item>
    /// <item>快递公司（<c>Express</c>）：≤ <see cref="MaxExpressLength"/>；</item>
    /// <item>运单号（<c>TrackingNo</c>）：≤ <see cref="MaxTrackingNoLength"/>；</item>
    /// <item>客户反馈结果（<c>Result</c>）：≤ <see cref="MaxResultLength"/>；</item>
    /// <item>客户名称（<c>CustomerName</c>）：≤ <see cref="MaxCustomerNameLength"/>；</item>
    /// <item>样品名称（<c>ProductName</c>）：≤ <see cref="MaxProductNameLength"/>；</item>
    /// <item>规格（<c>Spec</c>）：≤ <see cref="MaxSpecLength"/>；</item>
    /// <item>业务员姓名（<c>SalesmanName</c>）：≤ <see cref="MaxSalesmanNameLength"/>；</item>
    /// <item>备注（<c>Remark</c>）：≤ <see cref="MaxRemarkLength"/>；</item>
    /// <item>数量（<c>Quantity</c>）与样品费（<c>SampleFee</c>）：非负且 ≤ <see cref="MaxDecimalMagnitude"/>
    /// （<c>DECIMAL(18,4)</c> 可存储范围内）。</item>
    /// </list>
    /// 任一项不满足即按 <see cref="ErrorCodes.InvalidParameter"/> 的受控错误拒绝，<b>不</b>静默截断 / 夹取或改写任何字段，
    /// 被拒绝的写入不落任何 <c>Samples</c> 行（也<b>不</b>改写任何既有行）。
    /// </summary>
    public static void Validate(Sample? entity)
    {
        if (entity is null)
            throw BusinessException.InvalidParameter("样品数据不能为空");

        EnsureRequiredText(entity.SampleNo, MaxSampleNoLength, "样品编号");
        EnsureOptionalText(entity.CustomerName, MaxCustomerNameLength, "客户名称");
        EnsureOptionalText(entity.ProductName, MaxProductNameLength, "样品名称");
        EnsureOptionalText(entity.Spec, MaxSpecLength, "规格");
        EnsureOptionalText(entity.SampleType, MaxSampleTypeLength, "样品类型");
        EnsureOptionalText(entity.Unit, MaxUnitLength, "单位");
        EnsureOptionalText(entity.Currency, MaxCurrencyLength, "币种");
        EnsureOptionalText(entity.Express, MaxExpressLength, "快递公司");
        EnsureOptionalText(entity.TrackingNo, MaxTrackingNoLength, "运单号");
        EnsureOptionalText(entity.Result, MaxResultLength, "客户反馈结果");
        EnsureOptionalText(entity.SalesmanName, MaxSalesmanNameLength, "业务员姓名");
        EnsureOptionalText(entity.Remark, MaxRemarkLength, "备注");

        EnsureNonNegativePersistedDecimal(entity.Quantity, nameof(Sample.Quantity), "数量");
        EnsureNonNegativePersistedDecimal(entity.SampleFee, nameof(Sample.SampleFee), "样品费");
    }

    /// <summary>
    /// 新增 / 修改的引用与数据范围校验（落库之前调用）：
    /// <list type="bullet">
    /// <item><c>CustomerId</c> 必须非空且为正，并落在当前账号 ERP-097 客户数据范围之内（范围外按权限不足拒绝，不泄露归属）；</item>
    /// <item><c>CustomerId</c> 必须指向已知的未删除客户；</item>
    /// <item><c>ProductId</c> 可空（0 / null 视为未指定），非空时必须指向已知的未删除商品；</item>
    /// <item><c>SalesmanId</c> 可空（0 / null 视为未指定），非空时必须指向已知的未删除员工。</item>
    /// </list>
    /// 被拒绝的写入不落任何行、不改写任何既有行。
    /// </summary>
    public static async Task EnsureWritableReferencesAsync(
        IErpDbContext db, SalespersonDataScope scope, Sample entity, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(scope);
        if (entity is null)
            throw BusinessException.InvalidParameter("样品数据不能为空");

        if (entity.CustomerId is null or <= 0)
            throw BusinessException.InvalidParameter("样品必须关联客户（CustomerId 不能为空且必须为正整数）");

        if (!scope.AllowsCustomer(entity.CustomerId))
            throw new BusinessException(CustomerOutOfScopeText, ErrorCodes.Forbidden);

        var customerExists = await db.BaseCustomers.AsNoTracking()
            .AnyAsync(c => c.Id == entity.CustomerId.Value && !c.IsDeleted, ct);
        if (!customerExists)
            throw BusinessException.InvalidParameter("样品的客户（CustomerId）必须指向已知的未删除客户");

        if (entity.ProductId is { } productId && productId != 0)
        {
            if (productId < 0)
                throw BusinessException.InvalidParameter("样品的商品（ProductId）不能为负数");

            var productExists = await db.BaseProducts.AsNoTracking()
                .AnyAsync(p => p.Id == productId && !p.IsDeleted, ct);
            if (!productExists)
                throw BusinessException.InvalidParameter("样品的商品（ProductId）必须指向已知的未删除商品");
        }

        if (entity.SalesmanId is { } salesmanId && salesmanId != 0)
        {
            if (salesmanId < 0)
                throw BusinessException.InvalidParameter("样品的业务员（SalesmanId）不能为负数");

            var salesmanExists = await db.BaseEmployees.AsNoTracking()
                .AnyAsync(e => e.Id == salesmanId && !e.IsDeleted, ct);
            if (!salesmanExists)
                throw BusinessException.InvalidParameter("样品的业务员（SalesmanId）必须指向已知的未删除员工");
        }
    }
    /// <summary>必填文本校验：空白即拒绝，超过持久化长度上限即拒绝（不做静默截断）。</summary>
    private static void EnsureRequiredText(string? value, int maxLength, string fieldText)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw BusinessException.InvalidParameter($"样品{fieldText}不能为空");
        if (value.Length > maxLength)
            throw BusinessException.InvalidParameter($"样品{fieldText}长度不能超过 {maxLength} 个字符");
    }

    /// <summary>可选文本校验：仅在提供且超过持久化长度上限时拒绝（不做静默截断）。</summary>
    private static void EnsureOptionalText(string? value, int maxLength, string fieldText)
    {
        if (value is not null && value.Length > maxLength)
            throw BusinessException.InvalidParameter($"样品{fieldText}长度不能超过 {maxLength} 个字符");
    }

    /// <summary>
    /// 数值列校验：非负且能落在持久化 <c>DECIMAL(18,4)</c> 范围内（不做静默夹取 / 改写）。
    /// </summary>
    private static void EnsureNonNegativePersistedDecimal(decimal value, string column, string fieldText)
    {
        if (value < 0)
            throw BusinessException.InvalidParameter($"样品{fieldText}（{column}）不能为负数");
        if (value > MaxDecimalMagnitude)
            throw BusinessException.InvalidParameter(
                $"样品{fieldText}（{column}）超出可存储范围（DECIMAL(18,4)），请填写 {MaxDecimalMagnitude} 以内的非负数值");
    }
}
