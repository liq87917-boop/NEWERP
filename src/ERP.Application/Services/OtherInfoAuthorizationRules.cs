using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ERP.Application.Services;

/// <summary>
/// 其他资料数据字典（<see cref="BaseOtherInfo"/>，<c>api/base/other-infos</c>）实时身份、既有功能菜单与
/// 字段有界校验护栏（ERP-443，阶段 3 运营主数据收口）。
/// <list type="number">
/// <item><b>实时授权</b>：分页 / 全部 / 详情 / 按类型查询 / 新增 / 修改 / 删除 / 批量删除，在读取任何计数或写入任何数据
/// <b>之前</b>都先解析<b>实时启用身份</b>（缺失 / 非法按未认证拒绝，账号不存在 / 已删除按未认证拒绝，
/// 禁用按权限不足拒绝）与既有「其他资料」（<c>other-info</c>，与 <c>SeedData.Menus</c> 同源）功能菜单授权
/// （非特权账号必须显式具备，撤销后下一次请求立即收敛）。</item>
/// <item><b>字段有界校验</b>：新增 / 修改在任何字段落库之前校验 <see cref="BaseOtherInfo.InfoType"/> 必须落在
/// 既有「其他资料」页面已知的有界字典类型集合内、<see cref="BaseOtherInfo.InfoCode"/> /
/// <see cref="BaseOtherInfo.InfoName"/> 非空且不超过既有持久化长度上界、<see cref="BaseOtherInfo.EnglishName"/> /
/// <see cref="BaseOtherInfo.Remark"/> 不超过既有持久化长度上界、<see cref="BaseOtherInfo.Status"/> 只能是
/// 1（启用）/ 0（停用）；一律以既有受控校验错误（<c>1001 InvalidParameter</c>）<b>拒绝而不静默截断 / 删除 /
/// 复活</b>任何字典行。</item>
/// </list>
/// <para><b>身份来源唯一</b>：请求提交体中的任何字段都不能指定或扩大账号身份 —— 身份只来自已认证请求主体
/// （<c>ClaimTypes.NameIdentifier</c>），由调用方（<see cref="ERP.Api.Controllers.OtherInfoController"/>）解析并传入；
/// <c>null</c> 表示无可用身份，一律 fail closed，绝不代表匿名或管理员。</para>
/// <para>边界：本类只做纯判定与有界只读查询；不新增表 / 列 / 菜单 / 角色 / 用户授权，不引入匿名 / 管理员兜底，
/// 也不改变既有「其他资料」字典语义（币种 / 港口 / 货代 / 报关行 / 唛头 / 包装 / 贸易条款等）与既有点位读取口径。</para>
/// </summary>
public static class OtherInfoAuthorizationRules
{
    /// <summary>其他资料模块复用的既有菜单编码（与 <c>SeedData.Menus</c> 同源：<c>("base","other-info","其他资料",…)</c>）</summary>
    public const string RequiredMenuCode = "other-info";

    /// <summary>其他资料模块菜单中文文案（与既有菜单名一致）</summary>
    public const string RequiredMenuText = "其他资料";

    // ==================== 既有持久化长度上界（与 BaseOtherInfo 的 [MaxLength] 同源） ====================

    /// <summary>资料类型长度上界（与 <see cref="BaseOtherInfo.InfoType"/> <c>MaxLength(50)</c> 同源）</summary>
    public const int MaxInfoTypeLength = 50;

    /// <summary>资料编码长度上界（与 <see cref="BaseOtherInfo.InfoCode"/> <c>MaxLength(50)</c> 同源）</summary>
    public const int MaxInfoCodeLength = 50;

    /// <summary>资料名称长度上界（与 <see cref="BaseOtherInfo.InfoName"/> <c>MaxLength(100)</c> 同源）</summary>
    public const int MaxInfoNameLength = 100;

    /// <summary>英文名称长度上界（与 <see cref="BaseOtherInfo.EnglishName"/> <c>MaxLength(100)</c> 同源）</summary>
    public const int MaxEnglishNameLength = 100;

    /// <summary>备注长度上界（与 <see cref="BaseOtherInfo.Remark"/> <c>MaxLength(500)</c> 同源）</summary>
    public const int MaxRemarkLength = 500;

    // ==================== 既有状态取值（1=启用，0=停用） ====================

    /// <summary>既有启用状态（与 <c>CustomerForwarderRules.IsSelectable</c> 同源）</summary>
    public const int StatusEnabled = 1;

    /// <summary>既有停用状态</summary>
    public const int StatusDisabled = 0;

    /// <summary>
    /// 既有「其他资料」页面支持的资料类型（与 <c>src/ERP.Api/wwwroot/js/modules.js</c> 的 <c>other-info</c> 页面
    /// InfoType 选项同源，<b>不新增取值</b>）：币种 / 汇率 / 港口 / 货代 / 报关行 / 唛头模板 / 包装单位 /
    /// 贸易条款 / 结算方式 / 运输方式 / 费用类型 / 出口方式 / 认证 / 品牌 / 其他。
    /// </summary>
    public static readonly IReadOnlyList<string> KnownInfoTypes = new[]
    {
        "Currency", "ExchangeRate", "Port", "Forwarder", "CustomsBroker", "ShippingMark",
        "Package", "TradeTerm", "Settlement", "TransportMode", "ExpenseType", "ExportMode",
        "Certification", "Brand", "Other"
    };

    /// <summary>已知资料类型集合（大小写不敏感；与 <see cref="KnownInfoTypes"/> 同源）</summary>
    private static readonly HashSet<string> KnownInfoTypeSet = new(KnownInfoTypes, StringComparer.OrdinalIgnoreCase);

    // ==================== 受控非披露错误 / 校验文案 ====================

    /// <summary>无身份 / 非法身份的拒绝文案</summary>
    public const string UnauthorizedText = "请先登录后再访问其他资料";

    /// <summary>账号不存在 / 已删除的拒绝文案</summary>
    public const string UserDeletedText = "登录账号不存在或已删除，禁止访问其他资料";

    /// <summary>账号已禁用的拒绝文案</summary>
    public const string UserDisabledText = "登录账号已禁用，禁止访问其他资料（fail closed）";

    /// <summary>缺少既有「其他资料」菜单授权的拒绝文案</summary>
    public const string MenuDeniedText =
        "当前账号没有「其他资料」（other-info）模块授权：拒绝访问其他资料"
        + "（fail closed，不返回 / 不新增 / 不改写任何字典行）";

    /// <summary>资料类型为空的拒绝文案</summary>
    public const string InfoTypeRequiredText = "资料类型（InfoType）不能为空";

    /// <summary>资料类型超长的拒绝文案</summary>
    public const string InfoTypeTooLongText = "资料类型（InfoType）长度不能超过 50 个字符";

    /// <summary>资料类型不是已知有界字典类型的拒绝文案</summary>
    public const string InfoTypeUnknownText = "资料类型（InfoType）不是已知的有界字典类型";

    /// <summary>资料编码为空的拒绝文案</summary>
    public const string InfoCodeRequiredText = "资料编码（InfoCode）不能为空";

    /// <summary>资料编码超长的拒绝文案</summary>
    public const string InfoCodeTooLongText = "资料编码（InfoCode）长度不能超过 50 个字符";

    /// <summary>资料名称为空的拒绝文案</summary>
    public const string InfoNameRequiredText = "资料名称（InfoName）不能为空";

    /// <summary>资料名称超长的拒绝文案</summary>
    public const string InfoNameTooLongText = "资料名称（InfoName）长度不能超过 100 个字符";

    /// <summary>英文名称超长的拒绝文案</summary>
    public const string EnglishNameTooLongText = "英文名称（EnglishName）长度不能超过 100 个字符";

    /// <summary>备注超长的拒绝文案</summary>
    public const string RemarkTooLongText = "备注（Remark）长度不能超过 500 个字符";

    /// <summary>状态取值非法的拒绝文案</summary>
    public const string InvalidStatusText = "状态（Status）只能是 1（启用）或 0（停用）";

    /// <summary>授权与校验口径文案（接口 / 文档同源）</summary>
    public const string RuleText =
        "其他资料数据字典（分页 / 全部 / 详情 / 按类型查询 / 新增 / 修改 / 删除 / 批量删除）在读取任何计数或写入之前，"
        + "都会重新校验实时启用身份（缺失 / 非法 / 账号不存在 / 已删除按未认证，禁用按权限不足）与既有「其他资料」"
        + "（other-info）菜单授权；非特权账号必须显式具备该既有功能菜单，特权账号继承既有全部访问，"
        + "绝不把空身份当作管理员。新增 / 修改前校验 InfoType 是既有有界字典类型、InfoCode / InfoName 非空且有界、"
        + "EnglishName / Remark 有界、Status 为 1 或 0，一律拒绝而不静默截断 / 删除 / 复活任何字典行；"
        + "身份只来自已认证请求主体，客户端提交体不能指定或扩大身份。";

    /// <summary>边界文案（不新增权限 / 表列，不改变既有字典语义）</summary>
    public const string BoundaryText =
        "本护栏只保护其他资料数据字典的访问与写入校验：不新增菜单 / 角色 / 用户授权或表结构，"
        + "无匿名 / 管理员兜底，也不改变币种 / 港口 / 货代 / 报关行 / 唛头 / 包装 / 贸易条款等既有字典项的读取语义；"
        + "被拒绝的读取 / 新增 / 修改 / 删除 / 批量删除不加载、不写入、不改写任何字典行。";

    // ==================== 1. 类型判定 ====================

    /// <summary>
    /// 资料类型是否为既有页面的已知有界字典类型（忽略大小写与首尾空白，与既有
    /// <c>CustomerForwarderRules.IsType</c> 的容错口径一致）；空值 / 空白一律返回 <c>false</c>。
    /// </summary>
    public static bool IsKnownInfoType(string? infoType)
        => !string.IsNullOrWhiteSpace(infoType) && KnownInfoTypeSet.Contains(infoType.Trim());

    // ==================== 2. 身份 / 账号状态 / 菜单授权（fail closed） ====================

    /// <summary>
    /// 身份 / 账号状态 / 既有「其他资料」菜单授权实时校验（fail closed）：
    /// 缺失 / 非法身份按未认证拒绝，账号不存在 / 已删除按未认证拒绝，禁用按权限不足拒绝，
    /// 非特权账号缺少既有 <c>other-info</c> 菜单授权按权限不足拒绝。
    /// 每次调用都重新查询（无缓存），账号停用 / 删除或菜单撤销后下一次请求立即收敛；特权账号保留既有全部访问。
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

        var scope = await SalespersonDataScopeService.ResolveAsync(db, userId.Value);

        // 特权账号继承既有全部访问（与 ERP-097 同源）；普通账号必须显式具备既有「其他资料」功能菜单。
        if (!scope.IsPrivileged)
        {
            var menuCodes = await CustomerReceivableReconciliationService
                .LoadAuthorizedMenuCodesAsync(db, userId.Value);
            if (!menuCodes.Contains(RequiredMenuCode, StringComparer.OrdinalIgnoreCase))
                throw new BusinessException(MenuDeniedText, ErrorCodes.Forbidden);
        }
    }

    // ==================== 3. 字段有界校验（不改写实体） ====================

    /// <summary>
    /// 其他资料字典行写入校验：<see cref="BaseOtherInfo.InfoType"/> 必须是既有已知有界字典类型且非空、
    /// <see cref="BaseOtherInfo.InfoCode"/> / <see cref="BaseOtherInfo.InfoName"/> 非空且不超过既有持久化长度上界、
    /// <see cref="BaseOtherInfo.EnglishName"/> / <see cref="BaseOtherInfo.Remark"/> 不超过既有持久化长度上界、
    /// <see cref="BaseOtherInfo.Status"/> 只能是 1（启用）/ 0（停用）。
    /// 任一不满足即在改写任何字段之前以既有受控校验错误（<c>1001 InvalidParameter</c>）拒绝，
    /// <b>绝不静默截断、删除或复活</b>任何字典行；被拒绝的写入不落任何行。
    /// </summary>
    public static void Validate(BaseOtherInfo entity)
    {
        ArgumentNullException.ThrowIfNull(entity);

        var infoType = entity.InfoType ?? string.Empty;
        if (string.IsNullOrWhiteSpace(infoType))
            throw BusinessException.InvalidParameter(InfoTypeRequiredText);
        if (infoType.Length > MaxInfoTypeLength)
            throw BusinessException.InvalidParameter($"{InfoTypeTooLongText}：当前长度 {infoType.Length}");
        if (!IsKnownInfoType(infoType))
            throw BusinessException.InvalidParameter($"{InfoTypeUnknownText}：当前值“{infoType.Trim()}”");

        var infoCode = entity.InfoCode ?? string.Empty;
        if (string.IsNullOrWhiteSpace(infoCode))
            throw BusinessException.InvalidParameter(InfoCodeRequiredText);
        if (infoCode.Length > MaxInfoCodeLength)
            throw BusinessException.InvalidParameter($"{InfoCodeTooLongText}：当前长度 {infoCode.Length}");

        var infoName = entity.InfoName ?? string.Empty;
        if (string.IsNullOrWhiteSpace(infoName))
            throw BusinessException.InvalidParameter(InfoNameRequiredText);
        if (infoName.Length > MaxInfoNameLength)
            throw BusinessException.InvalidParameter($"{InfoNameTooLongText}：当前长度 {infoName.Length}");

        var englishName = entity.EnglishName ?? string.Empty;
        if (englishName.Length > MaxEnglishNameLength)
            throw BusinessException.InvalidParameter($"{EnglishNameTooLongText}：当前长度 {englishName.Length}");

        var remark = entity.Remark ?? string.Empty;
        if (remark.Length > MaxRemarkLength)
            throw BusinessException.InvalidParameter($"{RemarkTooLongText}：当前长度 {remark.Length}");

        if (entity.Status != StatusEnabled && entity.Status != StatusDisabled)
            throw BusinessException.InvalidParameter($"{InvalidStatusText}：当前值 {entity.Status}");
    }
}
