using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ERP.Application.Services;

/// <summary>
/// 客户资料（<see cref="BaseCustomer"/>，<c>api/base/customers</c>）的实时身份 / 既有功能菜单授权
/// 与有界字段校验护栏（ERP-451）。客户主数据是销售订单 / 报价单 / PI / 装柜清单 / 收款单与
/// ERP-097 业务员数据范围<b>共同解析</b>的权威对象，因此必须像其它主数据一样 fail closed。
/// <list type="number">
/// <item><b>实时授权</b>（<see cref="EnsureAuthorizedAsync"/>）：分页 / 全部 / 按主键读取 / 指定货代下拉
/// 与新增 / 修改 / 删除 / 批量删除<b>每一个</b>路由在读取或写入任何 <c>BaseCustomers</c> 行<b>之前</b>
/// 都重新解析实时身份（缺失 / 非法 / 账号不存在或已删除按未认证，禁用按权限不足）与既有
/// 「客户资料」（<c>customer</c>）功能菜单授权（缺菜单 / 被撤销按权限不足），一律 fail closed；</item>
/// <item><b>有界字段校验</b>（<see cref="Validate"/>）：新增 / 修改在落库之前校验客户编码与名称非空且在
/// 持久化长度上限内、可选文本字段不超持久化长度、信用额度 / 定金比例 / 佣金比例落在
/// <c>DECIMAL(18,4)</c> 可存储范围内、业务员 Id 与账期天数为已知的<b>非负</b>形状、
/// 状态为已知的 <c>1</c>（启用）/ <c>0</c>（停用）；被拒绝的写入不落任何行。</item>
/// </list>
/// <para>边界（重要）：本护栏只新增「读取 / 写入前的判定」，<b>不</b>静默截断 / 夹取或改写任何客户字段，
/// 也<b>不</b>改变既有客户编码唯一索引语义、分页 / 响应契约与 <see cref="GenericService{TEntity}"/> 契约；
/// ERP-097 业务员读取范围与 <see cref="CustomerForwarderService"/> 的指定货代引用语义（引用未变更时保留历史引用、
/// 客户端自由文本一律不被采信）均保持不变；<b>不</b>新增任何表 / 列 / 实体 / 菜单 / 权限 / 用户授权，
/// 不伪造任何授权，<b>不</b>因身份缺失而降级为管理员，也<b>不</b>新增匿名 / 特权旁路。</para>
/// </summary>
public static class CustomerAuthorizationRules
{
    /// <summary>所需既有功能菜单编码（与 <c>SeedData.Menus</c> 同源，<b>不新增菜单</b>）</summary>
    public const string RequiredMenuCode = "customer";

    /// <summary>既有功能菜单中文文案</summary>
    public const string RequiredMenuText = "客户资料";

    /// <summary>客户编码持久化长度上限（<c>NVARCHAR(50)</c>，与实体 <c>[MaxLength(50)]</c> 一致）</summary>
    public const int MaxCustomerCodeLength = 50;

    /// <summary>客户名称持久化长度上限（<c>NVARCHAR(200)</c>，与实体 <c>[MaxLength(200)]</c> 一致）</summary>
    public const int MaxCustomerNameLength = 200;

    /// <summary>英文名称持久化长度上限</summary>
    public const int MaxEnglishNameLength = 200;

    /// <summary>联系人持久化长度上限</summary>
    public const int MaxContactPersonLength = 50;

    /// <summary>联系电话持久化长度上限</summary>
    public const int MaxPhoneLength = 50;

    /// <summary>邮箱持久化长度上限</summary>
    public const int MaxEmailLength = 100;

    /// <summary>国家 / 地区持久化长度上限</summary>
    public const int MaxCountryLength = 100;

    /// <summary>详细地址持久化长度上限</summary>
    public const int MaxAddressLength = 500;

    /// <summary>付款条件持久化长度上限</summary>
    public const int MaxPaymentTermsLength = 200;

    /// <summary>税号持久化长度上限</summary>
    public const int MaxTaxNumberLength = 100;

    /// <summary>业务性质持久化长度上限</summary>
    public const int MaxBusinessNatureLength = 20;

    /// <summary>默认币种持久化长度上限</summary>
    public const int MaxCurrencyLength = 20;

    /// <summary>结算方式持久化长度上限</summary>
    public const int MaxSettlementMethodLength = 100;

    /// <summary>贸易条款持久化长度上限</summary>
    public const int MaxTradeTermsLength = 50;

    /// <summary>目的港持久化长度上限</summary>
    public const int MaxDestinationPortLength = 100;

    /// <summary>收货人持久化长度上限</summary>
    public const int MaxConsigneeLength = 300;

    /// <summary>通知人持久化长度上限</summary>
    public const int MaxNotifyPartyLength = 300;

    /// <summary>默认唛头持久化长度上限</summary>
    public const int MaxDefaultShippingMarkLength = 300;

    /// <summary>客户等级持久化长度上限</summary>
    public const int MaxCustomerLevelLength = 20;

    /// <summary>信用状态持久化长度上限</summary>
    public const int MaxCreditStatusLength = 20;

    /// <summary>客户来源持久化长度上限</summary>
    public const int MaxSourceLength = 50;

    /// <summary>备注持久化长度上限</summary>
    public const int MaxRemarkLength = 500;

    /// <summary>
    /// 客户数值列的持久化可存储上限（<c>DECIMAL(18,4)</c>：14 位整数 + 4 位小数），
    /// 超出即无法落库，必须在写入前按参数错误拒绝。适用于 <c>CreditLimit</c> / <c>DepositRatio</c> /
    /// <c>CommissionRatio</c>。
    /// </summary>
    public const decimal MaxDecimalMagnitude = 99999999999999.9999m;

    /// <summary>已知启用状态（<c>BaseCustomer.Status = 1</c>）</summary>
    public const int EnabledStatus = 1;

    /// <summary>已知停用状态（<c>BaseCustomer.Status = 0</c>）</summary>
    public const int DisabledStatus = 0;

    /// <summary>无身份 / 非法身份的拒绝文案（受控、不泄露数据）</summary>
    public const string UnauthorizedText = "请先登录后再访问客户资料";

    /// <summary>账号不存在 / 已删除的拒绝文案（受控、不泄露数据）</summary>
    public const string UserDeletedText = "登录账号不存在或已删除，禁止访问客户资料";

    /// <summary>账号已禁用的拒绝文案（受控、不泄露数据）</summary>
    public const string UserDisabledText = "登录账号已禁用，禁止访问客户资料（fail closed）";

    /// <summary>缺少既有「客户资料」菜单授权时的拒绝文案（受控、不泄露数据）</summary>
    public const string MenuDeniedText =
        "当前账号没有「客户资料」（customer）模块授权：" +
        "拒绝访问客户资料（fail closed，不返回 / 不新增 / 不改写任何客户）";

    /// <summary>授权口径文案（接口 / 文档同源）</summary>
    public const string AuthorizationRuleText =
        "客户资料（分页 / 全部 / 按主键读取 / 指定货代下拉 / 新增 / 修改 / 删除 / 批量删除）在读取或写入任何客户之前，" +
        "都会重新校验实时身份（缺失 / 非法 / 已删除按未认证，禁用按权限不足，一律 fail closed）" +
        "与既有「客户资料」（customer）单项功能菜单授权；菜单授权复用既有「角色 → 菜单」口径，" +
        "每次请求重新查询，撤销后下一次请求立即收敛。";

    /// <summary>授权边界文案（不改写既有业务口径）</summary>
    public const string AuthorizationBoundaryText =
        "本护栏只新增「读取 / 写入前的授权与有界字段校验」：不改变客户编码唯一索引语义、ERP-097 业务员读取范围、" +
        "指定货代引用语义、分页 / 响应契约与 GenericService 契约，也不静默截断 / 夹取或改写任何客户字段；" +
        "不新增表 / 列 / 实体 / 菜单 / 权限 / 用户授权，也不把空身份当作管理员。";

    /// <summary>
    /// 实时身份 / 账号状态 / 既有「客户资料」功能菜单三项校验（fail closed），返回本次请求的权威用户 Id。
    /// <list type="bullet">
    /// <item>身份缺失 / 非法 → <see cref="ErrorCodes.Unauthorized"/>；</item>
    /// <item>账号不存在或已删除 → <see cref="ErrorCodes.Unauthorized"/>；</item>
    /// <item>账号已禁用 → <see cref="ErrorCodes.Forbidden"/>；</item>
    /// <item>缺少既有「客户资料」（<c>customer</c>）菜单授权（含被撤销授权）→ <see cref="ErrorCodes.Forbidden"/>。</item>
    /// </list>
    /// 判定发生在任何客户读取 / 写入<b>之前</b>；每次请求重新解析，菜单或账号状态变更后立即收敛；
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
    /// 客户持久化字段的有界校验（新增 / 修改都在落库之前调用）：
    /// <list type="bullet">
    /// <item>客户编码：非空且 ≤ <see cref="MaxCustomerCodeLength"/>；</item>
    /// <item>客户名称：非空且 ≤ <see cref="MaxCustomerNameLength"/>；</item>
    /// <item>可选文本字段：不超各自持久化长度上限；</item>
    /// <item>信用额度 / 定金比例 / 佣金比例：落在 <c>DECIMAL(18,4)</c> 可存储范围内（<see cref="MaxDecimalMagnitude"/>）；</item>
    /// <item>业务员 Id（<c>EmpId</c>）与账期天数（<c>CreditDays</c>）：已知的非负形状（可空，0 = 未指定 / 现结）；</item>
    /// <item>状态：仅接受 <see cref="EnabledStatus"/>（启用）或 <see cref="DisabledStatus"/>（停用）。</item>
    /// </list>
    /// 任一项不满足即按 <see cref="ErrorCodes.InvalidParameter"/> 的受控错误拒绝，<b>不</b>静默截断 / 夹取或改写任何字段，
    /// 被拒绝的写入不落任何 <c>BaseCustomers</c> 行（也<b>不</b>改写任何既有行）。
    /// <para>指定货代（<c>ForwarderId</c> / <c>ForwarderName</c>）刻意不在此校验：其引用形状与名称快照由
    /// <see cref="CustomerForwarderService.ApplyAsync"/> 按既有语义权威处置（<c>&lt;=0</c> 视为清空、客户端自由文本不被采信），
    /// 保持既有指定货代引用语义不变。</para>
    /// </summary>
    public static void Validate(BaseCustomer? entity)
    {
        if (entity is null)
            throw BusinessException.InvalidParameter("客户数据不能为空");

        EnsureRequiredText(entity.CustomerCode, MaxCustomerCodeLength, "编码");
        EnsureRequiredText(entity.CustomerName, MaxCustomerNameLength, "名称");

        EnsureOptionalText(entity.EnglishName, MaxEnglishNameLength, "英文名称");
        EnsureOptionalText(entity.ContactPerson, MaxContactPersonLength, "联系人");
        EnsureOptionalText(entity.Phone, MaxPhoneLength, "联系电话");
        EnsureOptionalText(entity.Email, MaxEmailLength, "邮箱");
        EnsureOptionalText(entity.Country, MaxCountryLength, "国家/地区");
        EnsureOptionalText(entity.Address, MaxAddressLength, "详细地址");
        EnsureOptionalText(entity.PaymentTerms, MaxPaymentTermsLength, "付款条件");
        EnsureOptionalText(entity.TaxNumber, MaxTaxNumberLength, "税号");
        EnsureOptionalText(entity.BusinessNature, MaxBusinessNatureLength, "业务性质");
        EnsureOptionalText(entity.Currency, MaxCurrencyLength, "默认币种");
        EnsureOptionalText(entity.SettlementMethod, MaxSettlementMethodLength, "结算方式");
        EnsureOptionalText(entity.TradeTerms, MaxTradeTermsLength, "贸易条款");
        EnsureOptionalText(entity.DestinationPort, MaxDestinationPortLength, "目的港");
        EnsureOptionalText(entity.Consignee, MaxConsigneeLength, "收货人");
        EnsureOptionalText(entity.NotifyParty, MaxNotifyPartyLength, "通知人");
        EnsureOptionalText(entity.DefaultShippingMark, MaxDefaultShippingMarkLength, "默认唛头");
        EnsureOptionalText(entity.CustomerLevel, MaxCustomerLevelLength, "客户等级");
        EnsureOptionalText(entity.CreditStatus, MaxCreditStatusLength, "信用状态");
        EnsureOptionalText(entity.Source, MaxSourceLength, "客户来源");
        EnsureOptionalText(entity.Remark, MaxRemarkLength, "备注");

        EnsurePersistedDecimal(entity.CreditLimit, nameof(BaseCustomer.CreditLimit), "信用额度");
        EnsurePersistedDecimal(entity.DepositRatio, nameof(BaseCustomer.DepositRatio), "定金比例");
        EnsurePersistedDecimal(entity.CommissionRatio, nameof(BaseCustomer.CommissionRatio), "佣金比例");

        EnsureOptionalNonNegative(entity.EmpId, "业务员 Id");
        EnsureOptionalNonNegative(entity.CreditDays, "账期天数");

        if (entity.Status != EnabledStatus && entity.Status != DisabledStatus)
            throw BusinessException.InvalidParameter("客户状态只能是 1（启用）或 0（停用）");
    }

    /// <summary>必填文本校验：空白即拒绝，超过持久化长度上限即拒绝（不做静默截断）。</summary>
    private static void EnsureRequiredText(string? value, int maxLength, string fieldText)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw BusinessException.InvalidParameter($"客户{fieldText}不能为空");
        if (value.Length > maxLength)
            throw BusinessException.InvalidParameter($"客户{fieldText}长度不能超过 {maxLength} 个字符");
    }

    /// <summary>可选文本校验：仅在提供且超过持久化长度上限时拒绝（不做静默截断）。</summary>
    private static void EnsureOptionalText(string? value, int maxLength, string fieldText)
    {
        if (value is not null && value.Length > maxLength)
            throw BusinessException.InvalidParameter($"客户{fieldText}长度不能超过 {maxLength} 个字符");
    }

    /// <summary>数值列校验：数值必须能落在持久化 <c>DECIMAL(18,4)</c> 范围内（不做静默夹取）。</summary>
    private static void EnsurePersistedDecimal(decimal value, string column, string fieldText)
    {
        if (value > MaxDecimalMagnitude || value < -MaxDecimalMagnitude)
            throw BusinessException.InvalidParameter(
                $"客户{fieldText}（{column}）超出可存储范围（DECIMAL(18,4)），请填写 {MaxDecimalMagnitude} 以内的数值");
    }

    /// <summary>可选整数列校验：可空；非空时必须为已知的非负形状（不做静默夹取 / 改写）。</summary>
    private static void EnsureOptionalNonNegative(long? value, string fieldText)
    {
        if (value.HasValue && value.Value < 0)
            throw BusinessException.InvalidParameter($"客户{fieldText}不能为负数（0 表示未指定）");
    }
}

