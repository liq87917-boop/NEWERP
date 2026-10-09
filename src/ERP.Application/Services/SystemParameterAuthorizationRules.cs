using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using System.Globalization;

namespace ERP.Application.Services;

/// <summary>
/// 系统参数（<see cref="SysParameter"/>，<c>api/sys/parameters</c>）实时身份、既有功能菜单与运营取值护栏
/// （ERP-446，阶段 3 运营配置收口）。
/// <list type="number">
/// <item><b>实时授权</b>：分页 / 主键详情 / 按键详情 / 新增 / 修改在读取任何参数行或写入任何参数行
/// <b>之前</b>都先解析<b>实时启用身份</b>（缺失 / 非法按未认证拒绝，账号不存在 / 已删除按未认证拒绝，
/// 禁用按权限不足拒绝）与既有「系统参数」（<c>sys-parameter</c>，与 <c>SeedData.Menus</c> 同源）功能菜单授权
/// （每个账号都必须显式具备，撤销后下一次请求立即收敛；无匿名 / 管理员兜底）。</item>
/// <item><b>持久化列有界校验</b>：新增 / 修改在任何字段落库之前校验 <see cref="SysParameter.ParamKey"/> /
/// <see cref="SysParameter.ParamName"/> 非空且不超既有持久化上界、<see cref="SysParameter.ParamKey"/> 在未删除参数中唯一、
/// <see cref="SysParameter.ParamValue"/> / <see cref="SysParameter.Description"/> 不超既有持久化上界。</item>
/// <item><b>运营取值护栏</b>：被运营单据默认值消费的键 <c>DefaultCurrency</c> 只接受<b>受支持币种代码</b>，
/// <c>ExchangeRate</c> 只接受<b>正的可解析十进制数</b>（与 <c>BillProcController</c> 的币种 / 汇率回退口径同源），
/// 一律以既有受控错误 <b>拒绝而不静默截断 / 改写</b>。</item>
/// </list>
/// <para><b>身份来源唯一</b>：请求提交体中的任何字段都不能指定或扩大账号身份 —— 身份只来自已认证请求主体
/// （<c>ClaimTypes.NameIdentifier</c>），由调用方（<c>ERP.Api.Controllers.ParameterController</c>）
/// 解析并传入；<c>null</c> 表示无可用身份，一律 fail closed，绝不代表匿名或管理员。</para>
/// <para>边界：本类只做纯判定与有界只读查询；不新增表 / 列 / 实体 / 菜单 / 角色 / 用户授权，不引入匿名 /
/// 管理员兜底，也不改变 <c>BillProcController</c> 既有的参数驱动默认币种 / 汇率回退行为。</para>
/// </summary>
public static class SystemParameterAuthorizationRules
{
    /// <summary>系统参数模块复用的既有菜单编码（与 <c>SeedData.Menus</c> 同源：<c>("system","sys-parameter","系统参数",…)</c>）</summary>
    public const string RequiredMenuCode = "sys-parameter";

    /// <summary>系统参数模块菜单中文文案（与既有菜单名一致）</summary>
    public const string RequiredMenuText = "系统参数";

    // ==================== 既有持久化长度上界（与 SysParameter 的 [MaxLength] 同源） ====================

    /// <summary>参数键长度上界（与 <see cref="SysParameter.ParamKey"/> <c>MaxLength(100)</c> 同源）</summary>
    public const int MaxParamKeyLength = 100;

    /// <summary>参数值长度上界（与 <see cref="SysParameter.ParamValue"/> <c>MaxLength(500)</c> 同源）</summary>
    public const int MaxParamValueLength = 500;

    /// <summary>参数名称长度上界（与 <see cref="SysParameter.ParamName"/> <c>MaxLength(100)</c> 同源）</summary>
    public const int MaxParamNameLength = 100;

    /// <summary>参数说明长度上界（与 <see cref="SysParameter.Description"/> <c>MaxLength(500)</c> 同源）</summary>
    public const int MaxDescriptionLength = 500;

    // ==================== 被运营单据默认值消费的既有参数键（与 BillProcController 同源） ====================

    /// <summary>默认币种参数键（<c>BillProcController.LoadCurrencyDefaultsAsync</c> 读取）</summary>
    public const string DefaultCurrencyKey = "DefaultCurrency";

    /// <summary>默认汇率参数键（<c>BillProcController.LoadCurrencyDefaultsAsync</c> 读取）</summary>
    public const string ExchangeRateKey = "ExchangeRate";

    /// <summary>
    /// 受支持的币种代码（与 <c>BillProcController.MapCurrencyCode</c> 及 <see cref="Currency"/> 枚举同源）：
    /// 只有这些代码才会被运营单据默认值解析为非美元币种，其它任意字符串都会静默退化为美元。
    /// </summary>
    public static readonly string[] SupportedCurrencyCodes = { "CNY", "USD", "EUR", "HKD", "GBP", "JPY" };

    // ==================== 受控非披露错误 / 校验文案 ====================

    /// <summary>无身份 / 非法身份的拒绝文案</summary>
    public const string UnauthorizedText = "请先登录后再访问系统参数";

    /// <summary>账号不存在 / 已删除的拒绝文案</summary>
    public const string UserDeletedText = "登录账号不存在或已删除，禁止访问系统参数";

    /// <summary>账号已禁用的拒绝文案</summary>
    public const string UserDisabledText = "登录账号已禁用，禁止访问系统参数（fail closed）";

    /// <summary>缺少既有「系统参数」菜单授权的拒绝文案</summary>
    public const string MenuDeniedText =
        "当前账号没有「系统参数」（sys-parameter）模块授权：拒绝访问系统参数"
        + "（fail closed，不返回 / 不新增 / 不改写任何参数）";

    /// <summary>参数键为空的拒绝文案</summary>
    public const string ParamKeyRequiredText = "参数键（ParamKey）不能为空";

    /// <summary>参数键超长的拒绝文案</summary>
    public const string ParamKeyTooLongText = "参数键（ParamKey）长度不能超过 100 个字符";

    /// <summary>参数键重复的拒绝文案</summary>
    public const string ParamKeyDuplicatedText = "参数键（ParamKey）已存在：不得维护重复的系统参数";

    /// <summary>参数名称为空的拒绝文案</summary>
    public const string ParamNameRequiredText = "参数名称（ParamName）不能为空";

    /// <summary>参数名称超长的拒绝文案</summary>
    public const string ParamNameTooLongText = "参数名称（ParamName）长度不能超过 100 个字符";

    /// <summary>参数值超长的拒绝文案</summary>
    public const string ParamValueTooLongText = "参数值（ParamValue）长度不能超过 500 个字符";

    /// <summary>参数说明超长的拒绝文案</summary>
    public const string DescriptionTooLongText = "参数说明（Description）长度不能超过 500 个字符";

    /// <summary>默认币种不受支持的拒绝文案</summary>
    public const string CurrencyUnsupportedText =
        "默认币种（DefaultCurrency）只接受受支持的币种代码：CNY / USD / EUR / HKD / GBP / JPY";

    /// <summary>默认汇率非正数 / 不可解析的拒绝文案</summary>
    public const string ExchangeRateInvalidText =
        "默认汇率（ExchangeRate）只接受正的可解析十进制数（必须大于 0）";

    /// <summary>授权与校验口径文案（接口 / 文档同源）</summary>
    public const string RuleText =
        "系统参数（分页 / 主键详情 / 按键详情 / 新增 / 修改）在读取任何参数行或写入之前，"
        + "都会重新校验实时启用身份（缺失 / 非法 / 账号不存在 / 已删除按未认证，禁用按权限不足）与既有"
        + "「系统参数」（sys-parameter）功能菜单授权；撤销授权后下一次请求立即收敛，绝不把空身份当作管理员。"
        + "新增 / 修改前校验 ParamKey / ParamName 非空且有界、ParamKey 在未删除参数中唯一、"
        + "ParamValue / Description 有界，且被运营单据默认值消费的 DefaultCurrency 只接受受支持币种代码、"
        + "ExchangeRate 只接受正的可解析十进制数，一律拒绝而不静默截断 / 改写任何参数行；身份只来自已认证请求主体。";

    /// <summary>边界文案（不新增权限 / 表列，不改变既有参数驱动默认值行为）</summary>
    public const string BoundaryText =
        "本护栏只保护系统参数的访问与持久化取值校验：不新增菜单 / 角色 / 用户授权或表结构，"
        + "无匿名 / 管理员兜底，也不改变 BillProcController 既有的参数驱动默认币种 / 汇率回退行为"
        + "（DefaultCurrency 缺失 / 无效时仍回退美元、ExchangeRate 缺失 / 无效时仍回退 1）；"
        + "被拒绝的读取 / 新增 / 修改不加载、不写入、不改写任何参数行。";

    // ==================== 1. 身份 / 账号状态 / 菜单授权（fail closed） ====================

    /// <summary>
    /// 身份 / 账号状态 / 既有「系统参数」菜单授权实时校验（fail closed）：
    /// 缺失 / 非法身份按未认证拒绝，账号不存在 / 已删除按未认证拒绝，禁用按权限不足拒绝，
    /// 缺少既有 <c>sys-parameter</c> 菜单授权按权限不足拒绝。
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

    // ==================== 2. 运营取值判定（纯函数，不抛异常） ====================

    /// <summary>
    /// 币种代码是否为受支持币种（纯函数，不抛异常）：大小写无关且忽略首尾空白，
    /// 只接受 <see cref="SupportedCurrencyCodes"/> 中的代码（与 <c>BillProcController.MapCurrencyCode</c> 同源）。
    /// </summary>
    public static bool IsSupportedCurrencyCode(string? code)
    {
        var normalized = (code ?? string.Empty).Trim();
        foreach (var supported in SupportedCurrencyCodes)
        {
            if (string.Equals(normalized, supported, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// 汇率是否为正的可解析十进制数（纯函数，不抛异常）：以区域性无关十进制口径解析，且必须严格大于 0。
    /// </summary>
    public static bool IsPositiveExchangeRate(string? value)
        => decimal.TryParse((value ?? string.Empty).Trim(), NumberStyles.Number, CultureInfo.InvariantCulture,
               out var rate) && rate > 0;

    // ==================== 3. 可选字段规范化（判空去空白，不改写任何已存储行） ====================

    /// <summary>
    /// 写入前规范化拟议参数（只改写入参）：<see cref="SysParameter.ParamKey"/> /
    /// <see cref="SysParameter.ParamName"/> 去首尾空白，可空的 <see cref="SysParameter.ParamValue"/> /
    /// <see cref="SysParameter.Description"/> 为 <c>null</c> 时归一为空字符串。
    /// </summary>
    public static void NormalizeForWrite(SysParameter entity)
    {
        ArgumentNullException.ThrowIfNull(entity);

        entity.ParamKey = (entity.ParamKey ?? string.Empty).Trim();
        entity.ParamName = (entity.ParamName ?? string.Empty).Trim();
        entity.ParamValue ??= string.Empty;
        entity.Description ??= string.Empty;
    }

    // ==================== 4. 持久化列 + 运营取值有界校验（不改写实体） ====================

    /// <summary>
    /// 系统参数写入校验：<see cref="SysParameter.ParamKey"/> 非空、不超既有持久化上界且不与既有未删除参数重复
    /// （修改自身不占用键）、<see cref="SysParameter.ParamName"/> 非空且有界、
    /// <see cref="SysParameter.ParamValue"/> / <see cref="SysParameter.Description"/> 有界；
    /// 若键为 <see cref="DefaultCurrencyKey"/> 则值必须是受支持币种代码，若键为 <see cref="ExchangeRateKey"/>
    /// 则值必须是正的可解析十进制数。
    /// 任一不满足即在改写任何字段之前以既有受控错误拒绝（形状 / 取值 <c>1001</c>，键重复 <c>1003</c>），
    /// <b>绝不静默截断或改写</b>任何参数行；被拒绝的写入不落任何行。
    /// </summary>
    public static async Task ValidateAsync(IErpDbContext db, SysParameter entity, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(entity);
        ct.ThrowIfCancellationRequested();

        var paramKey = (entity.ParamKey ?? string.Empty).Trim();
        if (paramKey.Length == 0)
            throw BusinessException.InvalidParameter(ParamKeyRequiredText);
        if (paramKey.Length > MaxParamKeyLength)
            throw BusinessException.InvalidParameter($"{ParamKeyTooLongText}：当前长度 {paramKey.Length}");

        // 既有唯一参数键语义：未删除参数之间不得重复（修改自身不占用键）。
        var duplicated = await db.SysParameters.AsNoTracking()
            .AnyAsync(p => !p.IsDeleted && p.Id != entity.Id && p.ParamKey == paramKey, ct);
        if (duplicated)
            throw BusinessException.Duplicate($"{ParamKeyDuplicatedText}：当前键“{paramKey}”");

        var paramName = (entity.ParamName ?? string.Empty).Trim();
        if (paramName.Length == 0)
            throw BusinessException.InvalidParameter(ParamNameRequiredText);
        if (paramName.Length > MaxParamNameLength)
            throw BusinessException.InvalidParameter($"{ParamNameTooLongText}：当前长度 {paramName.Length}");

        var paramValue = entity.ParamValue ?? string.Empty;
        if (paramValue.Length > MaxParamValueLength)
            throw BusinessException.InvalidParameter($"{ParamValueTooLongText}：当前长度 {paramValue.Length}");

        var description = entity.Description ?? string.Empty;
        if (description.Length > MaxDescriptionLength)
            throw BusinessException.InvalidParameter($"{DescriptionTooLongText}：当前长度 {description.Length}");

        // 运营取值护栏：被 BillProcController 作为权威默认值消费的两个键。
        if (string.Equals(paramKey, DefaultCurrencyKey, StringComparison.OrdinalIgnoreCase)
            && !IsSupportedCurrencyCode(paramValue))
        {
            throw BusinessException.InvalidParameter(
                $"{CurrencyUnsupportedText}：当前值“{paramValue.Trim()}”");
        }

        if (string.Equals(paramKey, ExchangeRateKey, StringComparison.OrdinalIgnoreCase)
            && !IsPositiveExchangeRate(paramValue))
        {
            throw BusinessException.InvalidParameter(
                $"{ExchangeRateInvalidText}：当前值“{paramValue.Trim()}”");
        }
    }
}
