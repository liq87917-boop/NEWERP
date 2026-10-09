using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using System.Globalization;

namespace ERP.Application.Services;

/// <summary>
/// 单据号规则（<see cref="SysDocumentNumberRule"/>，<c>api/sys/document-number-rules</c>）实时身份、既有功能菜单与
/// 编号形状有界校验护栏（ERP-445，阶段 3 单据控制收口）。
/// <list type="number">
/// <item><b>实时授权</b>：分页 / 全部 / 详情 / 新增 / 修改 / 删除 / 批量删除，在读取任何计数或写入任何规则行
/// <b>之前</b>都先解析<b>实时启用身份</b>（缺失 / 非法按未认证拒绝，账号不存在 / 已删除按未认证拒绝，
/// 禁用按权限不足拒绝）与既有「单据号规则」（<c>doc-rule</c>，与 <c>SeedData.Menus</c> 同源）功能菜单授权
/// （每个账号都必须显式具备，撤销后下一次请求立即收敛；无匿名 / 管理员兜底）。</item>
/// <item><b>编号形状有界校验</b>：新增 / 修改在任何字段落库之前校验 <see cref="SysDocumentNumberRule.DocumentType"/>
/// 是已知单据类型、<see cref="SysDocumentNumberRule.RuleCode"/> 非空且不与既有未删除规则重复、
/// <see cref="SysDocumentNumberRule.Prefix"/> / <see cref="SysDocumentNumberRule.Separator"/> /
/// <see cref="SysDocumentNumberRule.DateFormat"/> 不超过既有持久化上界且日期格式为受支持形状、
/// <see cref="SysDocumentNumberRule.SerialLength"/> 落在有界的正整数区间、
/// <see cref="SysDocumentNumberRule.CurrentSequence"/> 非负；一律以既有受控错误 <b>拒绝而不静默截断 / 改写</b>。</item>
/// </list>
/// <para><b>身份来源唯一</b>：请求提交体中的任何字段都不能指定或扩大账号身份 —— 身份只来自已认证请求主体
/// （<c>ClaimTypes.NameIdentifier</c>），由调用方（<c>ERP.Api.Controllers.DocumentNumberRuleController</c>）
/// 解析并传入；<c>null</c> 表示无可用身份，一律 fail closed，绝不代表匿名或管理员。</para>
/// <para>边界：本类只做纯判定与有界只读查询；不新增表 / 列 / 实体 / 菜单 / 角色 / 用户授权，不引入匿名 /
/// 管理员兜底，也不改变 <see cref="DocumentNumberService"/> 的默认兜底（prefix + yyyyMMdd + 4 位流水）与既有流水语义。</para>
/// </summary>
public static class DocumentNumberRuleAuthorizationRules
{
    /// <summary>单据号规则模块复用的既有菜单编码（与 <c>SeedData.Menus</c> 同源：<c>("system","doc-rule","单据号规则",…)</c>）</summary>
    public const string RequiredMenuCode = "doc-rule";

    /// <summary>单据号规则模块菜单中文文案（与既有菜单名一致）</summary>
    public const string RequiredMenuText = "单据号规则";

    // ==================== 既有持久化长度上界（与 SysDocumentNumberRule 的 [MaxLength] 同源） ====================

    /// <summary>规则编码长度上界（与 <see cref="SysDocumentNumberRule.RuleCode"/> <c>MaxLength(50)</c> 同源）</summary>
    public const int MaxRuleCodeLength = 50;

    /// <summary>规则名称长度上界（与 <see cref="SysDocumentNumberRule.RuleName"/> <c>MaxLength(100)</c> 同源）</summary>
    public const int MaxRuleNameLength = 100;

    /// <summary>前缀长度上界（与 <see cref="SysDocumentNumberRule.Prefix"/> <c>MaxLength(20)</c> 同源）</summary>
    public const int MaxPrefixLength = 20;

    /// <summary>日期格式长度上界（与 <see cref="SysDocumentNumberRule.DateFormat"/> <c>MaxLength(20)</c> 同源）</summary>
    public const int MaxDateFormatLength = 20;

    /// <summary>分隔符长度上界（与 <see cref="SysDocumentNumberRule.Separator"/> <c>MaxLength(5)</c> 同源）</summary>
    public const int MaxSeparatorLength = 5;

    /// <summary>备注长度上界（与 <see cref="SysDocumentNumberRule.Remark"/> <c>MaxLength(500)</c> 同源）</summary>
    public const int MaxRemarkLength = 500;

    // ==================== 既有流水号位数有界区间（DocumentNumberService 兜底为 4） ====================

    /// <summary>流水号位数下界（正整数）</summary>
    public const int MinSerialLength = 1;

    /// <summary>流水号位数上界（与既有兜底 4 位同量级，避免生成不可用的超长编号）</summary>
    public const int MaxSerialLength = 10;

    /// <summary>既有默认日期格式（与 <see cref="DocumentNumberService"/> 的 null 兜底一致）</summary>
    public const string DefaultDateFormat = "yyyyMMdd";

    // ==================== 受控非披露错误 / 校验文案 ====================

    /// <summary>无身份 / 非法身份的拒绝文案</summary>
    public const string UnauthorizedText = "请先登录后再访问单据号规则";

    /// <summary>账号不存在 / 已删除的拒绝文案</summary>
    public const string UserDeletedText = "登录账号不存在或已删除，禁止访问单据号规则";

    /// <summary>账号已禁用的拒绝文案</summary>
    public const string UserDisabledText = "登录账号已禁用，禁止访问单据号规则（fail closed）";

    /// <summary>缺少既有「单据号规则」菜单授权的拒绝文案</summary>
    public const string MenuDeniedText =
        "当前账号没有「单据号规则」（doc-rule）模块授权：拒绝访问单据号规则"
        + "（fail closed，不返回 / 不新增 / 不改写任何规则）";

    /// <summary>单据类型不是已知单据类型的拒绝文案</summary>
    public const string DocumentTypeUnknownText = "单据类型（DocumentType）不是已知的单据类型";

    /// <summary>规则编码为空的拒绝文案</summary>
    public const string RuleCodeRequiredText = "规则编码（RuleCode）不能为空";

    /// <summary>规则编码超长的拒绝文案</summary>
    public const string RuleCodeTooLongText = "规则编码（RuleCode）长度不能超过 50 个字符";

    /// <summary>规则编码重复的拒绝文案</summary>
    public const string RuleCodeDuplicatedText = "规则编码（RuleCode）已存在：不得维护重复的编号规则";

    /// <summary>规则名称为空的拒绝文案</summary>
    public const string RuleNameRequiredText = "规则名称（RuleName）不能为空";

    /// <summary>规则名称超长的拒绝文案</summary>
    public const string RuleNameTooLongText = "规则名称（RuleName）长度不能超过 100 个字符";

    /// <summary>前缀超长的拒绝文案</summary>
    public const string PrefixTooLongText = "前缀（Prefix）长度不能超过 20 个字符";

    /// <summary>日期格式超长的拒绝文案</summary>
    public const string DateFormatTooLongText = "日期格式（DateFormat）长度不能超过 20 个字符";

    /// <summary>日期格式不受支持形状的拒绝文案</summary>
    public const string DateFormatUnsupportedText =
        "日期格式（DateFormat）不是受支持的日期形状：只允许日期记号 y/M/d 与时间记号 H/h/m/s/f/F/g/t 及"
        + " - / . : _ 空格分隔符，且必须包含日期部分";

    /// <summary>分隔符超长的拒绝文案</summary>
    public const string SeparatorTooLongText = "分隔符（Separator）长度不能超过 5 个字符";

    /// <summary>流水号位数越界的拒绝文案</summary>
    public const string SerialLengthOutOfRangeText = "流水号位数（SerialLength）必须在 1 到 10 之间";

    /// <summary>当前流水号为负的拒绝文案</summary>
    public const string CurrentSequenceNegativeText = "当前流水号（CurrentSequence）不能为负数";

    /// <summary>备注超长的拒绝文案</summary>
    public const string RemarkTooLongText = "备注（Remark）长度不能超过 500 个字符";

    /// <summary>授权与校验口径文案（接口 / 文档同源）</summary>
    public const string RuleText =
        "单据号规则（分页 / 全部 / 详情 / 新增 / 修改 / 删除 / 批量删除）在读取任何计数或写入之前，"
        + "都会重新校验实时启用身份（缺失 / 非法 / 账号不存在 / 已删除按未认证，禁用按权限不足）与既有"
        + "「单据号规则」（doc-rule）功能菜单授权；撤销授权后下一次请求立即收敛，绝不把空身份当作管理员。"
        + "新增 / 修改前校验 DocumentType 是已知单据类型、RuleCode 非空且不与既有未删除规则重复、"
        + "Prefix / Separator / DateFormat 有界且日期格式为受支持形状、SerialLength 落在有界正整数区间、"
        + "CurrentSequence 非负，一律拒绝而不静默截断 / 改写任何规则行；身份只来自已认证请求主体。";

    /// <summary>边界文案（不新增权限 / 表列，不改变既有编号语义）</summary>
    public const string BoundaryText =
        "本护栏只保护单据号规则的访问与编号形状校验：不新增菜单 / 角色 / 用户授权或表结构，"
        + "无匿名 / 管理员兜底，也不改变 DocumentNumberService 的默认兜底（prefix + yyyyMMdd + 4 位流水）"
        + "与既有流水号自增语义；被拒绝的读取 / 新增 / 修改 / 删除 / 批量删除不加载、不写入、不改写任何规则行，"
        + "也不消耗任何单据号。";

    // ==================== 1. 日期格式受支持形状判定 ====================

    /// <summary>
    /// 日期格式是否为受支持的编号形状（纯函数，不抛异常）：
    /// <list type="bullet">
    /// <item>空 / 空白返回 <c>false</c>（调用方按「未设置 → DocumentNumberService 默认 yyyyMMdd」处理）；</item>
    /// <item>长度超过既有持久化上界返回 <c>false</c>；</item>
    /// <item>只允许日期记号 <c>y / M / d</c>、时间记号 <c>H / h / m / s / f / F / g / t</c> 与
    /// 分隔符 <c>- / . : _</c> 及空格，其余字符（含未知字母，如 <c>q</c>）返回 <c>false</c>；</item>
    /// <item>必须至少包含一个日期记号（<c>y / M / d</c>），纯时间 / 纯分隔符返回 <c>false</c>；</item>
    /// <item>以既有区域性无关口径真实格式化一次，格式化异常返回 <c>false</c>。</item>
    /// </list>
    /// </summary>
    public static bool IsSupportedDateFormat(string? dateFormat)
    {
        if (string.IsNullOrWhiteSpace(dateFormat))
            return false;

        var value = dateFormat.Trim();
        if (value.Length > MaxDateFormatLength)
            return false;

        var hasDateToken = false;
        foreach (var ch in value)
        {
            switch (ch)
            {
                case 'y':
                case 'M':
                case 'd':
                    hasDateToken = true;
                    continue;
                case 'H':
                case 'h':
                case 'm':
                case 's':
                case 'f':
                case 'F':
                case 'g':
                case 't':
                case '-':
                case '/':
                case '.':
                case ':':
                case '_':
                case ' ':
                    continue;
                default:
                    return false;
            }
        }

        if (!hasDateToken)
            return false;

        try
        {
            _ = DateTime.Today.ToString(value, CultureInfo.InvariantCulture);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    // ==================== 2. 身份 / 账号状态 / 菜单授权（fail closed） ====================

    /// <summary>
    /// 身份 / 账号状态 / 既有「单据号规则」菜单授权实时校验（fail closed）：
    /// 缺失 / 非法身份按未认证拒绝，账号不存在 / 已删除按未认证拒绝，禁用按权限不足拒绝，
    /// 缺少既有 <c>doc-rule</c> 菜单授权按权限不足拒绝。
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

    // ==================== 3. 可选字段规范化（对齐 DocumentNumberService 的 null 兜底） ====================

    /// <summary>
    /// 写入前规范化拟议规则（只改写入参、判空 / 去空白，不改写任何已存储行）：
    /// <see cref="SysDocumentNumberRule.RuleCode"/> / <see cref="SysDocumentNumberRule.RuleName"/> /
    /// <see cref="SysDocumentNumberRule.Prefix"/> / <see cref="SysDocumentNumberRule.Separator"/> 去首尾空白；
    /// <see cref="SysDocumentNumberRule.DateFormat"/> 为空白时归一为 <c>null</c>，
    /// 使其回到 <see cref="DocumentNumberService"/> 的 <c>?? "yyyyMMdd"</c> 默认兜底口径。
    /// </summary>
    public static void NormalizeForWrite(SysDocumentNumberRule entity)
    {
        ArgumentNullException.ThrowIfNull(entity);

        entity.RuleCode = (entity.RuleCode ?? string.Empty).Trim();
        entity.RuleName = (entity.RuleName ?? string.Empty).Trim();
        entity.Prefix = (entity.Prefix ?? string.Empty).Trim();
        entity.Separator = (entity.Separator ?? string.Empty).Trim();
        entity.DateFormat = string.IsNullOrWhiteSpace(entity.DateFormat) ? null : entity.DateFormat.Trim();
    }

    // ==================== 4. 编号形状有界校验（不改写实体） ====================

    /// <summary>
    /// 单据号规则写入校验：<see cref="SysDocumentNumberRule.DocumentType"/> 必须是已知单据类型、
    /// <see cref="SysDocumentNumberRule.RuleCode"/> 非空、不超既有持久化上界且不与既有未删除规则重复、
    /// <see cref="SysDocumentNumberRule.RuleName"/> 非空且有界、<see cref="SysDocumentNumberRule.Prefix"/> /
    /// <see cref="SysDocumentNumberRule.Separator"/> / <see cref="SysDocumentNumberRule.DateFormat"/> 有界且日期格式为受支持形状、
    /// <see cref="SysDocumentNumberRule.SerialLength"/> 落在 [1, 10]、
    /// <see cref="SysDocumentNumberRule.CurrentSequence"/> 非负、<see cref="SysDocumentNumberRule.Remark"/> 有界。
    /// 任一不满足即在改写任何字段之前以既有受控错误拒绝（形状 / 取值 <c>1001</c>，编码重复 <c>1003</c>），
    /// <b>绝不静默截断、删除或复活</b>任何规则行；被拒绝的写入不落任何行，也不消耗任何单据号。
    /// </summary>
    public static async Task ValidateAsync(
        IErpDbContext db, SysDocumentNumberRule entity, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(entity);
        ct.ThrowIfCancellationRequested();

        if (!Enum.IsDefined(typeof(DocumentType), entity.DocumentType))
            throw BusinessException.InvalidParameter($"{DocumentTypeUnknownText}：当前值 {(int)entity.DocumentType}");

        var ruleCode = (entity.RuleCode ?? string.Empty).Trim();
        if (ruleCode.Length == 0)
            throw BusinessException.InvalidParameter(RuleCodeRequiredText);
        if (ruleCode.Length > MaxRuleCodeLength)
            throw BusinessException.InvalidParameter($"{RuleCodeTooLongText}：当前长度 {ruleCode.Length}");

        // 既有唯一规则编码语义：未删除规则之间不得重复（修改自身不占用编码）。
        var duplicated = await db.SysDocumentNumberRules.AsNoTracking()
            .AnyAsync(r => !r.IsDeleted && r.Id != entity.Id && r.RuleCode == ruleCode, ct);
        if (duplicated)
            throw BusinessException.Duplicate($"{RuleCodeDuplicatedText}：当前编码“{ruleCode}”");

        var ruleName = (entity.RuleName ?? string.Empty).Trim();
        if (ruleName.Length == 0)
            throw BusinessException.InvalidParameter(RuleNameRequiredText);
        if (ruleName.Length > MaxRuleNameLength)
            throw BusinessException.InvalidParameter($"{RuleNameTooLongText}：当前长度 {ruleName.Length}");

        var prefix = entity.Prefix ?? string.Empty;
        if (prefix.Length > MaxPrefixLength)
            throw BusinessException.InvalidParameter($"{PrefixTooLongText}：当前长度 {prefix.Length}");

        if (entity.DateFormat is not null)
        {
            if (entity.DateFormat.Length > MaxDateFormatLength)
                throw BusinessException.InvalidParameter(
                    $"{DateFormatTooLongText}：当前长度 {entity.DateFormat.Length}");
            if (!IsSupportedDateFormat(entity.DateFormat))
                throw BusinessException.InvalidParameter(
                    $"{DateFormatUnsupportedText}：当前值“{entity.DateFormat}”");
        }

        var separator = entity.Separator ?? string.Empty;
        if (separator.Length > MaxSeparatorLength)
            throw BusinessException.InvalidParameter($"{SeparatorTooLongText}：当前长度 {separator.Length}");

        if (entity.SerialLength < MinSerialLength || entity.SerialLength > MaxSerialLength)
            throw BusinessException.InvalidParameter(
                $"{SerialLengthOutOfRangeText}：当前值 {entity.SerialLength}");

        if (entity.CurrentSequence < 0)
            throw BusinessException.InvalidParameter(
                $"{CurrentSequenceNegativeText}：当前值 {entity.CurrentSequence}");

        var remark = entity.Remark ?? string.Empty;
        if (remark.Length > MaxRemarkLength)
            throw BusinessException.InvalidParameter($"{RemarkTooLongText}：当前长度 {remark.Length}");
    }
}
