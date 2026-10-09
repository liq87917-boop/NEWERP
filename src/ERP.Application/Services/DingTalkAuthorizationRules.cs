using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ERP.Application.Services;

/// <summary>
/// 钉钉通知（<c>api/sys/dingtalk</c>）实时身份、既有功能菜单与白名单配置取值护栏（ERP-460，阶段 3 访问控制收口）。
/// <list type="number">
/// <item><b>实时授权</b>：配置读取 / 保存 / 测试发送 / 手动推送提醒在读取任何配置或发送任何消息<b>之前</b>、
/// 发送记录列表 / 重发 / 删除在读取、重发或软删除任何记录<b>之前</b>，都先解析<b>实时启用身份</b>
/// （缺失 / 非法按未认证拒绝，账号不存在 / 已删除按未认证拒绝，禁用按权限不足拒绝）与既有功能菜单授权
/// （配置侧复用既有「钉钉通知配置」<c>dingtalk-config</c>，记录侧复用既有「钉钉发送记录」<c>dingtalk-log</c>，
/// 与 <c>SchemaUpgrader</c> 同源）；每个账号都必须显式具备，撤销后下一次请求立即收敛；<b>无匿名 / 管理员兜底</b>。</item>
/// <item><b>白名单配置取值校验</b>：保存钉钉配置在任何配置行落库之前校验只提交<b>钉钉白名单键</b>
/// （<c>DingTalk_*</c>，与 <c>ERP.Api.Services.DingTalkService.AllKeys</c> 同源），且每个值不超既有
/// <c>SysParameter.ParamValue</c> 的持久化上界（500）；一律以既有受控错误 <b>拒绝而不静默截断 / 改写</b>，
/// 被拒绝的保存不落任何 <c>SysParameter</c> 行。</item>
/// </list>
/// <para><b>身份来源唯一</b>：请求提交体中的任何字段都不能指定或扩大账号身份 —— 身份只来自已认证请求主体
/// （<c>ClaimTypes.NameIdentifier</c>），由调用方（<c>ERP.Api.Controllers.DingTalkController</c>）
/// 解析并传入；<c>null</c> 表示无可用身份，一律 fail closed，绝不代表匿名或管理员。</para>
/// <para>边界：本类只做纯判定与有界只读查询；不新增表 / 列 / 实体 / 菜单 / 角色 / 用户授权，不引入匿名 /
/// 管理员兜底，也不改变钉钉配置既有白名单保存语义、发送记录脱敏写入与软删除语义。</para>
/// </summary>
public static class DingTalkAuthorizationRules
{
    // ==================== 复用的既有功能菜单（与 SchemaUpgrader 同源，不新增菜单） ====================

    /// <summary>钉钉通知配置复用既有菜单编码（<c>dingtalk-config</c>，与 <c>SchemaUpgrader</c> 同源）</summary>
    public const string ConfigMenuCode = "dingtalk-config";

    /// <summary>钉钉通知配置菜单中文文案（与既有菜单名一致）</summary>
    public const string ConfigMenuText = "钉钉通知配置";

    /// <summary>钉钉发送记录复用既有菜单编码（<c>dingtalk-log</c>，与 <c>SchemaUpgrader</c> 同源）</summary>
    public const string LogMenuCode = "dingtalk-log";

    /// <summary>钉钉发送记录菜单中文文案（与既有菜单名一致）</summary>
    public const string LogMenuText = "钉钉发送记录";

    // ==================== 既有钉钉配置白名单（与 ERP.Api.Services.DingTalkService.AllKeys 同源） ====================

    /// <summary>是否启用推送（与 <c>DingTalkService.KeyEnabled</c> 同源）</summary>
    public const string KeyEnabled = "DingTalk_Enabled";

    /// <summary>群机器人 Webhook（与 <c>DingTalkService.KeyWebhook</c> 同源，敏感）</summary>
    public const string KeyWebhook = "DingTalk_Webhook";

    /// <summary>加签密钥（与 <c>DingTalkService.KeySecret</c> 同源，敏感）</summary>
    public const string KeySecret = "DingTalk_Secret";

    /// <summary>消息类型 text / markdown（与 <c>DingTalkService.KeyMsgType</c> 同源）</summary>
    public const string KeyMsgType = "DingTalk_MsgType";

    /// <summary>@ 手机号（与 <c>DingTalkService.KeyAtMobiles</c> 同源）</summary>
    public const string KeyAtMobiles = "DingTalk_AtMobiles";

    /// <summary>是否 @ 所有人（与 <c>DingTalkService.KeyAtAll</c> 同源）</summary>
    public const string KeyAtAll = "DingTalk_AtAll";

    /// <summary>触发规则 JSON（与 <c>DingTalkService.KeyRules</c> 同源）</summary>
    public const string KeyRules = "DingTalk_Rules";

    /// <summary>消息模板（与 <c>DingTalkService.KeyTemplate</c> 同源）</summary>
    public const string KeyTemplate = "DingTalk_Template";

    /// <summary>
    /// 钉钉通知配置可写入的<b>白名单键</b>（与 <c>ERP.Api.Services.DingTalkService.AllKeys</c> 同源）：
    /// 只有提交体中的这些键会被接受，其余键一律以既有受控错误拒绝（未白名单键绝不落库）。
    /// </summary>
    public static readonly string[] AllowedConfigKeys =
    {
        KeyEnabled, KeyWebhook, KeySecret, KeyMsgType, KeyAtMobiles, KeyAtAll, KeyRules, KeyTemplate
    };

    // ==================== 既有持久化长度上界（与 SysParameter 的 [MaxLength] 同源） ====================

    /// <summary>配置键长度上界（与 <c>SysParameter.ParamKey</c> <c>MaxLength(100)</c> 同源）</summary>
    public const int MaxParamKeyLength = 100;

    /// <summary>配置值长度上界（与 <c>SysParameter.ParamValue</c> <c>MaxLength(500)</c> 同源）</summary>
    public const int MaxParamValueLength = 500;

    // ==================== 受控非披露错误 / 校验文案 ====================

    /// <summary>无身份 / 非法身份的拒绝文案</summary>
    public const string UnauthorizedText = "请先登录后再访问钉钉通知";

    /// <summary>账号不存在 / 已删除的拒绝文案</summary>
    public const string UserDeletedText = "登录账号不存在或已删除，禁止访问钉钉通知";

    /// <summary>账号已禁用的拒绝文案</summary>
    public const string UserDisabledText = "登录账号已禁用，禁止访问钉钉通知（fail closed）";

    /// <summary>缺少既有「钉钉通知配置」菜单授权时的拒绝文案</summary>
    public const string ConfigMenuDeniedText =
        "当前账号没有「钉钉通知配置」（dingtalk-config）模块授权：拒绝访问钉钉通知配置"
        + "（fail closed，不返回 / 不写入任何配置，不发送任何消息）";

    /// <summary>缺少既有「钉钉发送记录」菜单授权时的拒绝文案</summary>
    public const string LogMenuDeniedText =
        "当前账号没有「钉钉发送记录」（dingtalk-log）模块授权：拒绝访问钉钉发送记录"
        + "（fail closed，不读取 / 不重发 / 不删除任何发送记录）";

    /// <summary>提交了非白名单配置键的拒绝文案（不回声提交内容）</summary>
    public const string UnknownConfigKeyText =
        "钉钉通知配置只接受白名单配置键（DingTalk_Enabled / DingTalk_Webhook / DingTalk_Secret / "
        + "DingTalk_MsgType / DingTalk_AtMobiles / DingTalk_AtAll / DingTalk_Rules / DingTalk_Template）";

    /// <summary>配置值超长的拒绝文案</summary>
    public const string ParamValueTooLongText = "钉钉通知配置值长度不能超过 500 个字符";

    /// <summary>授权与校验口径文案（接口 / 文档同源）</summary>
    public const string RuleText =
        "钉钉通知（配置读取 / 保存 / 测试发送 / 手动推送提醒 / 发送记录列表 / 重发 / 删除）在读取配置、"
        + "发送消息或读取 / 重发 / 删除任何发送记录之前，都会重新校验实时启用身份（缺失 / 非法 / 账号不存在 / "
        + "已删除按未认证，禁用按权限不足）与既有功能菜单授权（配置侧 dingtalk-config、记录侧 dingtalk-log）；"
        + "撤销授权后下一次请求立即收敛，绝不把空身份当作管理员。保存前校验只提交钉钉白名单键且每个值不超既有"
        + "持久化上界，被拒绝的保存 / 删除不落任何 SysParameter / SysDingTalkLog 行，被拒绝的读取不返回任何 "
        + "Webhook / 访问令牌 / 加签密钥；身份只来自已认证请求主体。";

    /// <summary>边界文案（不新增权限 / 表列，不改变既有脱敏写入与软删除语义）</summary>
    public const string BoundaryText =
        "本护栏只保护钉钉通知路由的访问与白名单配置取值校验：不新增菜单 / 角色 / 用户授权或表结构，"
        + "无匿名 / 管理员兜底，也不改变既有白名单保存语义（缺失项即建、已有项改写、软删除行不复用）、"
        + "发送记录脱敏写入与软删除语义；被拒绝的请求不加载 / 不写入 / 不改写任何配置或发送记录。";

    // ==================== 1. 身份 / 账号状态 / 菜单授权（fail closed） ====================

    /// <summary>
    /// 钉钉通知配置侧（配置读取 / 保存 / 测试发送 / 手动推送提醒）实时授权：解析实时启用身份与既有
    /// 「钉钉通知配置」（<c>dingtalk-config</c>）菜单，任一不满足即 fail closed（无匿名 / 管理员兜底）。
    /// </summary>
    public static Task EnsureConfigAuthorizedAsync(IErpDbContext db, long? userId, CancellationToken ct = default)
        => EnsureMenuAuthorizedAsync(db, userId, ConfigMenuCode, ConfigMenuDeniedText, ct);

    /// <summary>
    /// 钉钉发送记录侧（发送记录列表 / 重发 / 删除）实时授权：解析实时启用身份与既有
    /// 「钉钉发送记录」（<c>dingtalk-log</c>）菜单，任一不满足即 fail closed（无匿名 / 管理员兜底）。
    /// </summary>
    public static Task EnsureLogAuthorizedAsync(IErpDbContext db, long? userId, CancellationToken ct = default)
        => EnsureMenuAuthorizedAsync(db, userId, LogMenuCode, LogMenuDeniedText, ct);

    /// <summary>
    /// 身份 / 账号状态 / 既有功能菜单授权实时校验（fail closed）：
    /// 缺失 / 非法身份按未认证拒绝，账号不存在 / 已删除按未认证拒绝，禁用按权限不足拒绝，
    /// 缺少指定既有菜单授权按权限不足拒绝。
    /// 每次调用都重新查询（无缓存），账号停用 / 删除或菜单撤销后下一次请求立即收敛；
    /// 每个账号都必须显式具备既有功能菜单，<b>无特权 / 管理员兜底</b>。
    /// </summary>
    private static async Task EnsureMenuAuthorizedAsync(IErpDbContext db, long? userId,
        string menuCode, string deniedText, CancellationToken ct = default)
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
        if (!menuCodes.Contains(menuCode, StringComparer.OrdinalIgnoreCase))
            throw new BusinessException(deniedText, ErrorCodes.Forbidden);
    }

    // ==================== 2. 配置键白名单与持久化列有界校验（不改写任何配置行） ====================

    /// <summary>
    /// 配置键是否为既有钉钉白名单键（纯函数，不抛异常；大小写敏感，与
    /// <c>ERP.Api.Services.DingTalkService.AllKeys</c> 的精确键匹配同源）。
    /// </summary>
    public static bool IsAllowedConfigKey(string? key)
    {
        if (key is null) return false;
        foreach (var allowed in AllowedConfigKeys)
        {
            if (string.Equals(allowed, key, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    /// <summary>
    /// 钉钉通知配置保存校验：提交体中的<b>每个键必须是既有白名单键</b>，且每个值不超
    /// <c>SysParameter.ParamValue</c> 的既有持久化上界（500）。
    /// 任一不满足即在写入任何配置行之前以既有受控错误拒绝（形状 / 取值 <c>1001</c>），
    /// <b>绝不静默截断或改写</b>任何配置行，也<b>绝不回声</b>提交的敏感取值；被拒绝的保存不落任何行。
    /// </summary>
    public static void ValidateConfigValues(IReadOnlyDictionary<string, string>? values)
    {
        if (values is null || values.Count == 0) return;

        foreach (var pair in values)
        {
            if (!IsAllowedConfigKey(pair.Key))
                throw BusinessException.InvalidParameter(UnknownConfigKeyText);

            var value = pair.Value ?? string.Empty;
            if (value.Length > MaxParamValueLength)
                throw BusinessException.InvalidParameter($"{ParamValueTooLongText}：当前长度 {value.Length}");
        }
    }
}





