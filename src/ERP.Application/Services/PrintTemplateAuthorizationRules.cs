using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ERP.Application.Services;

/// <summary>
/// 打印模板（<see cref="SysPrintTemplate"/>，<c>api/sys/print-templates</c>）实时身份、既有「样式设计」功能菜单
/// 与持久化列有界校验护栏（ERP-450，阶段 3 文档输出收口）。
/// <para>打印模板渲染全部运营单据（销售订单 / 采购订单 / 报价单 / 形式发票 PI / 库存单据 / 装柜与单证），
/// 控制器此前只声明 <c>[Authorize]</c>：任何已登录账号都能读取、改写、导出与导入模板布局。本类把
/// 授权与校验统一抽到 Application 层。</para>
/// <list type="number">
/// <item><b>实时身份</b>：<c>userId</c> 缺失 / 非法（非正整数）按未认证拒绝，账号不存在 / 已删除按未认证拒绝，
/// 账号已禁用按权限不足拒绝；绝不把空身份当作匿名或管理员，也绝不缓存（每次请求重新查询），
/// 账号停用 / 删除 / 授权撤销后下一次请求立即收敛。</item>
/// <item><b>既有菜单授权</b>：每个账号都必须实时具备既有「样式设计」功能菜单（<c>print-design</c>，
/// 与 <c>SchemaUpgrader</c> 第 5 / 6 步同源）；缺失 / 已删除 / 已撤销一律按权限不足拒绝，
/// <b>无特权 / 管理员兜底</b>，也绝不新增任何菜单 / 角色 / 用户授权。</item>
/// <item><b>持久化列有界校验</b>：保存前校验 <see cref="SysPrintTemplate.BillType"/> 属于既有可打印单据类型、
/// <see cref="SysPrintTemplate.TemplateName"/> 非空且有界，其余文本 / 颜色列不超既有持久化上界且为已知格式，
/// 三个字号在受支持范围内 —— 一律以既有受控错误 <b>拒绝而不静默截断 / 改写</b>；
/// 被拒绝的保存不落任何模板行，既有模板 / 默认标记 / 打印输出保持不变。</item>
/// </list>
/// <para><b>身份来源唯一</b>：身份只来自已认证请求主体（<c>ClaimTypes.NameIdentifier</c>），由调用方
/// （<c>ERP.Api.Controllers.PrintTemplateController</c>）解析并传入；请求提交体中的任何字段都不能指定或扩大账号身份。</para>
/// <para><b>边界</b>：本类只做纯判定与有界只读查询；不新增表 / 列 / 实体 / 菜单 / 角色 / 用户授权或权限模型，
/// 不引入匿名 / 管理员兜底，也不改变既有 <c>(BillType, TemplateName)</c> 唯一索引与默认标记语义。</para>
/// </summary>
public static class PrintTemplateAuthorizationRules
{
    /// <summary>打印模板模块复用的既有菜单编码（与 <c>SchemaUpgrader</c> 的 <c>print-design</c> 同源）。</summary>
    public const string RequiredMenuCode = "print-design";

    /// <summary>打印模板模块菜单中文文案（与既有菜单名一致）。</summary>
    public const string RequiredMenuText = "样式设计";

    // ==================== 既有持久化长度上界（与 SysPrintTemplate 的 [MaxLength] 同源） ====================

    /// <summary>单据类型长度上界（与 <see cref="SysPrintTemplate.BillType"/> <c>MaxLength(50)</c> 同源）</summary>
    public const int MaxBillTypeLength = 50;

    /// <summary>模板名称长度上界（与 <see cref="SysPrintTemplate.TemplateName"/> <c>MaxLength(100)</c> 同源）</summary>
    public const int MaxTemplateNameLength = 100;

    /// <summary>打印标题长度上界（与 <see cref="SysPrintTemplate.Title"/> <c>MaxLength(200)</c> 同源）</summary>
    public const int MaxTitleLength = 200;

    /// <summary>公司抬头长度上界（与 <see cref="SysPrintTemplate.CompanyName"/> <c>MaxLength(200)</c> 同源）</summary>
    public const int MaxCompanyNameLength = 200;

    /// <summary>公司地址长度上界（与 <see cref="SysPrintTemplate.CompanyAddress"/> <c>MaxLength(300)</c> 同源）</summary>
    public const int MaxCompanyAddressLength = 300;

    /// <summary>公司电话长度上界（与 <see cref="SysPrintTemplate.CompanyPhone"/> <c>MaxLength(100)</c> 同源）</summary>
    public const int MaxCompanyPhoneLength = 100;

    /// <summary>纸张规格长度上界（与 <see cref="SysPrintTemplate.PaperSize"/> <c>MaxLength(20)</c> 同源）</summary>
    public const int MaxPaperSizeLength = 20;

    /// <summary>打印字段顺序长度上界（与 <see cref="SysPrintTemplate.FieldKeys"/> <c>MaxLength(2000)</c> 同源）</summary>
    public const int MaxFieldKeysLength = 2000;

    /// <summary>页脚文本长度上界（与 <see cref="SysPrintTemplate.FooterText"/> <c>MaxLength(500)</c> 同源）</summary>
    public const int MaxFooterTextLength = 500;

    /// <summary>字体族长度上界（与 <see cref="SysPrintTemplate.FontFamily"/> <c>MaxLength(50)</c> 同源）</summary>
    public const int MaxFontFamilyLength = 50;

    /// <summary>颜色值长度上界（与各颜色列 <c>MaxLength(20)</c> 同源）</summary>
    public const int MaxColorLength = 20;

    // ==================== 受支持字号范围（与既有打印设计器口径一致） ====================

    /// <summary>正文字号下界</summary>
    public const int MinBodyFontSize = 8;

    /// <summary>正文字号上界</summary>
    public const int MaxBodyFontSize = 24;

    /// <summary>单据标题字号下界</summary>
    public const int MinTitleFontSize = 8;

    /// <summary>单据标题字号上界</summary>
    public const int MaxTitleFontSize = 48;

    /// <summary>公司名称字号下界</summary>
    public const int MinCompanyFontSize = 8;

    /// <summary>公司名称字号上界</summary>
    public const int MaxCompanyFontSize = 48;

    // ==================== 已知枚举 / 格式口径 ====================

    /// <summary>受支持的纸张规格（与既有打印设计器一致）</summary>
    public static readonly string[] PaperSizes = { "A4", "A5", "A4-L", "80mm" };

    /// <summary>受支持的标题对齐方式</summary>
    public static readonly string[] TitleAligns = { "left", "center", "right" };

    /// <summary>受支持的边框样式</summary>
    public static readonly string[] BorderStyles = { "solid", "dashed", "none" };

    /// <summary>默认纸张规格（空白时回落，与既有行为一致）</summary>
    public const string DefaultPaperSize = "A4";

    /// <summary>默认字体族（空白时回落，与既有行为一致）</summary>
    public const string DefaultFontFamily = "Microsoft YaHei";

    /// <summary>默认标题对齐方式（空白 / 未知时回落，与既有行为一致）</summary>
    public const string DefaultTitleAlign = "center";

    /// <summary>默认边框样式（空白 / 未知时回落，与既有行为一致）</summary>
    public const string DefaultBorderStyle = "solid";

    /// <summary>颜色值合法格式：<c>#RGB</c> / <c>#RRGGBB</c>（与既有 NormalizeColor 同源）</summary>
    private static readonly Regex ColorPattern = new("^#([0-9a-fA-F]{3}|[0-9a-fA-F]{6})$", RegexOptions.Compiled);

    // ==================== 受控非披露错误 / 校验文案 ====================

    /// <summary>无身份 / 非法身份的拒绝文案</summary>
    public const string UnauthorizedText = "请先登录后再访问打印模板";

    /// <summary>账号不存在 / 已删除的拒绝文案</summary>
    public const string UserDeletedText = "登录账号不存在或已删除，禁止访问打印模板";

    /// <summary>账号已禁用的拒绝文案</summary>
    public const string UserDisabledText = "登录账号已禁用，禁止访问打印模板（fail closed）";

    /// <summary>缺少既有「样式设计」菜单授权的拒绝文案</summary>
    public const string MenuDeniedText =
        "当前账号没有「样式设计」（print-design）模块授权：拒绝访问打印模板"
        + "（fail closed，不返回 / 不新增 / 不改写任何模板，也不产生任何导出构件）";

    /// <summary>单据类型为空的拒绝文案</summary>
    public const string BillTypeRequiredText = "单据类型（BillType）不能为空";

    /// <summary>单据类型不属于既有可打印单据类型的拒绝文案</summary>
    public const string BillTypeUnsupportedText = "单据类型（BillType）必须是既有可打印单据类型";

    /// <summary>单据类型超长的拒绝文案</summary>
    public const string BillTypeTooLongText = "单据类型（BillType）长度不能超过 50 个字符";

    /// <summary>模板名称为空的拒绝文案</summary>
    public const string TemplateNameRequiredText = "模板名称（TemplateName）不能为空";

    /// <summary>模板名称超长的拒绝文案</summary>
    public const string TemplateNameTooLongText = "模板名称（TemplateName）长度不能超过 100 个字符";

    /// <summary>打印标题超长的拒绝文案</summary>
    public const string TitleTooLongText = "打印标题（Title）长度不能超过 200 个字符";

    /// <summary>公司抬头超长的拒绝文案</summary>
    public const string CompanyNameTooLongText = "公司抬头（CompanyName）长度不能超过 200 个字符";

    /// <summary>公司地址超长的拒绝文案</summary>
    public const string CompanyAddressTooLongText = "公司地址（CompanyAddress）长度不能超过 300 个字符";

    /// <summary>公司电话超长的拒绝文案</summary>
    public const string CompanyPhoneTooLongText = "公司联系电话（CompanyPhone）长度不能超过 100 个字符";

    /// <summary>纸张规格不受支持的拒绝文案</summary>
    public const string PaperSizeUnsupportedText = "纸张规格（PaperSize）只接受 A4 / A5 / A4-L / 80mm";

    /// <summary>打印字段顺序超长的拒绝文案</summary>
    public const string FieldKeysTooLongText = "打印字段顺序（FieldKeys）长度不能超过 2000 个字符";

    /// <summary>打印字段顺序格式非法的拒绝文案</summary>
    public const string FieldKeysInvalidText = "打印字段顺序（FieldKeys）必须是字段键的 JSON 字符串数组";

    /// <summary>页脚文本超长的拒绝文案</summary>
    public const string FooterTextTooLongText = "页脚文本（FooterText）长度不能超过 500 个字符";

    /// <summary>字体族超长的拒绝文案</summary>
    public const string FontFamilyTooLongText = "字体族（FontFamily）长度不能超过 50 个字符";

    /// <summary>正文字号越界的拒绝文案</summary>
    public const string FontSizeRangeText = "正文字号（FontSize）必须在 8~24 之间";

    /// <summary>单据标题字号越界的拒绝文案</summary>
    public const string TitleFontSizeRangeText = "单据标题字号（TitleFontSize）必须在 8~48 之间";

    /// <summary>公司名称字号越界的拒绝文案</summary>
    public const string CompanyFontSizeRangeText = "公司名称字号（CompanyFontSize）必须在 8~48 之间";

    /// <summary>颜色值格式非法的拒绝文案（颜色列名由调用方拼接）</summary>
    public const string ColorInvalidText = "颜色值只接受 #RGB / #RRGGBB 格式";

    /// <summary>授权与校验口径文案（接口 / 文档同源）</summary>
    public const string RuleText =
        "打印模板（清单 / 默认模板 / 保存 / 删除 / Excel 导出 / Excel 导入）在读取任何模板行或写入之前，"
        + "都会重新校验实时启用身份（缺失 / 非法 / 账号不存在 / 已删除按未认证，禁用按权限不足）与既有"
        + "「样式设计」（print-design）功能菜单授权；撤销授权后下一次请求立即收敛，绝不把空身份当作管理员。"
        + "保存前校验 BillType 属于既有可打印单据类型、TemplateName 非空且有界，"
        + "Title / CompanyName / CompanyAddress / CompanyPhone / PaperSize / FieldKeys / FooterText / FontFamily "
        + "与各颜色值有界且为已知格式，且 FontSize / TitleFontSize / CompanyFontSize 在受支持范围内，"
        + "一律拒绝而不静默截断 / 改写任何模板行；身份只来自已认证请求主体。";

    /// <summary>边界文案（不新增权限 / 表列，不改变唯一索引与默认标记语义）</summary>
    public const string BoundaryText =
        "本护栏只保护打印模板的访问与持久化取值校验：不新增菜单 / 角色 / 用户授权或表 / 列 / 实体，"
        + "无匿名 / 管理员兜底，也不改变既有 (BillType, TemplateName) 唯一索引与默认标记语义；"
        + "被拒绝的读取 / 保存 / 删除 / 导出 / 导入不加载、不写入、不改写任何模板行，也不产生任何下载构件。";

    // ==================== 1. 身份 / 账号状态 / 菜单授权（fail closed） ====================

    /// <summary>
    /// 身份 / 账号状态 / 既有「样式设计」菜单授权实时校验（fail closed）：
    /// 缺失 / 非法身份按未认证拒绝，账号不存在 / 已删除按未认证拒绝，禁用按权限不足拒绝，
    /// 缺少既有 <c>print-design</c> 菜单授权按权限不足拒绝。
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

    // ==================== 2. 可选字段规范化（判空回落默认值，不改写任何已存储行） ====================

    /// <summary>
    /// 写入前规范化拟议模板（只改写入参）：单据类型 / 模板名称 / 字体族去首尾空白，
    /// 纸张规格 / 字体族空白时回落默认值，标题对齐 / 边框样式未知值回落默认值；
    /// 可空文本为 <c>null</c> 时归一为空字符串。<b>绝不</b>在此静默截断任何超界取值。
    /// </summary>
    public static void NormalizeForWrite(SysPrintTemplate model)
    {
        ArgumentNullException.ThrowIfNull(model);

        model.BillType = (model.BillType ?? string.Empty).Trim();
        model.TemplateName = (model.TemplateName ?? string.Empty).Trim();
        model.Title ??= string.Empty;
        model.CompanyName ??= string.Empty;
        model.CompanyAddress ??= string.Empty;
        model.CompanyPhone ??= string.Empty;
        model.FieldKeys ??= string.Empty;
        model.FooterText ??= string.Empty;

        model.PaperSize = string.IsNullOrWhiteSpace(model.PaperSize) ? DefaultPaperSize : model.PaperSize.Trim();
        model.FontFamily = string.IsNullOrWhiteSpace(model.FontFamily) ? DefaultFontFamily : model.FontFamily.Trim();

        var align = (model.TitleAlign ?? string.Empty).Trim();
        model.TitleAlign = MatchesKnown(TitleAligns, align) ? align : DefaultTitleAlign;

        var border = (model.BorderStyle ?? string.Empty).Trim();
        model.BorderStyle = MatchesKnown(BorderStyles, border) ? border : DefaultBorderStyle;
    }

    // ==================== 3. 持久化列有界校验（不改写实体） ====================

    /// <summary>
    /// 保存打印模板的持久化列有界校验：<see cref="SysPrintTemplate.BillType"/> 必须属于
    /// <paramref name="allowedBillTypes"/>（既有可打印单据类型目录）、
    /// <see cref="SysPrintTemplate.TemplateName"/> 非空且有界，
    /// <see cref="SysPrintTemplate.Title"/> / <see cref="SysPrintTemplate.CompanyName"/> /
    /// <see cref="SysPrintTemplate.CompanyAddress"/> / <see cref="SysPrintTemplate.CompanyPhone"/> /
    /// <see cref="SysPrintTemplate.PaperSize"/> / <see cref="SysPrintTemplate.FieldKeys"/> /
    /// <see cref="SysPrintTemplate.FooterText"/> / <see cref="SysPrintTemplate.FontFamily"/> 有界（纸张 / 字段顺序另校验已知格式），
    /// 各颜色值为 <c>#RGB</c> / <c>#RRGGBB</c>，
    /// <see cref="SysPrintTemplate.FontSize"/> / <see cref="SysPrintTemplate.TitleFontSize"/> /
    /// <see cref="SysPrintTemplate.CompanyFontSize"/> 在受支持范围内。
    /// 任一不满足即在改写任何字段之前以既有受控错误 <see cref="ErrorCodes.InvalidParameter"/> 拒绝，
    /// <b>绝不静默截断或改写</b>任何模板行；被拒绝的保存不落任何行。
    /// </summary>
    public static void ValidateForSave(SysPrintTemplate model, IReadOnlyCollection<string> allowedBillTypes)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(allowedBillTypes);

        var billType = (model.BillType ?? string.Empty).Trim();
        if (billType.Length == 0)
            throw BusinessException.InvalidParameter(BillTypeRequiredText);
        if (billType.Length > MaxBillTypeLength)
            throw BusinessException.InvalidParameter($"{BillTypeTooLongText}：当前长度 {billType.Length}");
        if (!IsPrintableBillType(billType, allowedBillTypes))
            throw BusinessException.InvalidParameter($"{BillTypeUnsupportedText}：当前值“{billType}”");

        var templateName = (model.TemplateName ?? string.Empty).Trim();
        if (templateName.Length == 0)
            throw BusinessException.InvalidParameter(TemplateNameRequiredText);
        if (templateName.Length > MaxTemplateNameLength)
            throw BusinessException.InvalidParameter($"{TemplateNameTooLongText}：当前长度 {templateName.Length}");

        RequireWithinBounds(model.Title, MaxTitleLength, TitleTooLongText);
        RequireWithinBounds(model.CompanyName, MaxCompanyNameLength, CompanyNameTooLongText);
        RequireWithinBounds(model.CompanyAddress, MaxCompanyAddressLength, CompanyAddressTooLongText);
        RequireWithinBounds(model.CompanyPhone, MaxCompanyPhoneLength, CompanyPhoneTooLongText);

        var paperSize = (model.PaperSize ?? string.Empty).Trim();
        if (paperSize.Length > MaxPaperSizeLength)
            throw BusinessException.InvalidParameter(
                $"{PaperSizeUnsupportedText}（长度不能超过 {MaxPaperSizeLength} 个字符）：当前值“{paperSize}”");
        if (paperSize.Length > 0 && !MatchesKnown(PaperSizes, paperSize))
            throw BusinessException.InvalidParameter($"{PaperSizeUnsupportedText}：当前值“{paperSize}”");

        RequireWithinBounds(model.FieldKeys, MaxFieldKeysLength, FieldKeysTooLongText);
        ValidateFieldKeys(model.FieldKeys);

        RequireWithinBounds(model.FooterText, MaxFooterTextLength, FooterTextTooLongText);
        RequireWithinBounds(model.FontFamily, MaxFontFamilyLength, FontFamilyTooLongText);

        if (model.FontSize is < MinBodyFontSize or > MaxBodyFontSize)
            throw BusinessException.InvalidParameter($"{FontSizeRangeText}：当前值 {model.FontSize}");
        if (model.TitleFontSize is < MinTitleFontSize or > MaxTitleFontSize)
            throw BusinessException.InvalidParameter($"{TitleFontSizeRangeText}：当前值 {model.TitleFontSize}");
        if (model.CompanyFontSize is < MinCompanyFontSize or > MaxCompanyFontSize)
            throw BusinessException.InvalidParameter($"{CompanyFontSizeRangeText}：当前值 {model.CompanyFontSize}");

        ValidateColor(model.TitleColor, nameof(SysPrintTemplate.TitleColor));
        ValidateColor(model.CompanyColor, nameof(SysPrintTemplate.CompanyColor));
        ValidateColor(model.TextColor, nameof(SysPrintTemplate.TextColor));
        ValidateColor(model.HeaderBgColor, nameof(SysPrintTemplate.HeaderBgColor));
        ValidateColor(model.BorderColor, nameof(SysPrintTemplate.BorderColor));
    }

    /// <summary>拟议单据类型是否属于既有可打印单据类型目录（大小写无关，忽略首尾空白）。</summary>
    public static bool IsPrintableBillType(string? billType, IEnumerable<string> allowedBillTypes)
    {
        ArgumentNullException.ThrowIfNull(allowedBillTypes);
        var normalized = (billType ?? string.Empty).Trim();
        if (normalized.Length == 0) return false;
        foreach (var allowed in allowedBillTypes)
        {
            if (string.Equals(normalized, (allowed ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>颜色值是否为已知格式（<c>#RGB</c> / <c>#RRGGBB</c>；空白视为未指定，按合法处理）。</summary>
    public static bool IsKnownColor(string? value)
        => string.IsNullOrWhiteSpace(value) || ColorPattern.IsMatch(value.Trim());

    // ==================== 私有辅助 ====================

    private static bool MatchesKnown(IReadOnlyCollection<string> known, string? value)
    {
        if (string.IsNullOrEmpty(value)) return false;
        foreach (var candidate in known)
        {
            if (string.Equals(candidate, value, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static void RequireWithinBounds(string? value, int maxLength, string tooLongText)
    {
        var length = (value ?? string.Empty).Length;
        if (length > maxLength)
            throw BusinessException.InvalidParameter($"{tooLongText}：当前长度 {length}");
    }

    private static void ValidateColor(string? value, string column)
    {
        var normalized = (value ?? string.Empty).Trim();
        if (normalized.Length > MaxColorLength || !IsKnownColor(normalized))
            throw BusinessException.InvalidParameter($"{column}：{ColorInvalidText}：当前值“{normalized}”");
    }

    private static void ValidateFieldKeys(string? raw)
    {
        var text = (raw ?? string.Empty).Trim();
        if (text.Length == 0) return;

        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                throw BusinessException.InvalidParameter(FieldKeysInvalidText);
            foreach (var element in doc.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.String)
                    throw BusinessException.InvalidParameter(FieldKeysInvalidText);
            }
        }
        catch (JsonException)
        {
            throw BusinessException.InvalidParameter(FieldKeysInvalidText);
        }
    }
}
