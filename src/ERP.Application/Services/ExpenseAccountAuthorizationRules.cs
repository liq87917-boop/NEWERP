using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ERP.Application.Services;

/// <summary>
/// 费用科目（<see cref="BaseExpenseAccount"/>，<c>api/base/expense-accounts</c>）的实时身份 / 既有功能菜单授权
/// 与有界字段校验护栏（ERP-498，阶段 3 主数据授权收口，与 ERP-448 / ERP-451 / ERP-452 同缺陷类）。
/// <list type="number">
/// <item><b>实时授权</b>（<see cref="EnsureAuthorizedAsync"/>）：分页 / 全部 / 按主键读取与新增 / 修改 / 删除 /
/// 批量删除<b>每一个</b>路由在读取或写入任何 <c>BaseExpenseAccounts</c> 行<b>之前</b>都重新解析实时身份
/// （缺失 / 非法 / 账号不存在或已删除按未认证，禁用按权限不足）与既有「费用科目」（<c>expense-account</c>）
/// 功能菜单授权（缺菜单 / 被撤销按权限不足），一律 fail closed；</item>
/// <item><b>有界字段校验</b>（<see cref="Validate"/>）：新增 / 修改在落库之前校验科目编码与名称非空且在
/// 持久化长度上限内、科目类型为已知的 <c>1</c>（收入）/ <c>2</c>（支出）、说明不超持久化长度、
/// 状态为已知的 <c>1</c>（启用）/ <c>0</c>（停用）；</item>
/// <item><b>唯一性与父级引用</b>（<see cref="EnsureWritableAsync"/>）：科目编码在<b>未删除行中唯一</b>
/// （修改时排除自身），<c>ParentId</c> 非空时必须指向一条<b>未删除</b>的费用科目；被拒绝的写入不落任何行。</item>
/// </list>
/// <para>边界（重要）：本护栏只新增「读取 / 写入前的判定」，<b>不</b>静默截断 / 夹取或改写任何科目字段，
/// 也<b>不</b>改变既有「科目编码」唯一索引语义、分页 / 响应契约与 <see cref="GenericService{TEntity}"/> 契约；
/// <b>不</b>新增任何表 / 列 / 实体 / 菜单 / 权限 / 用户授权，不伪造任何授权，<b>不</b>因身份缺失而降级为管理员，
/// 也<b>不</b>新增匿名 / 特权旁路。</para>
/// </summary>
public static class ExpenseAccountAuthorizationRules
{
    /// <summary>所需既有功能菜单编码（与 <c>SeedData.Menus</c> 同源，<b>不新增菜单</b>）</summary>
    public const string RequiredMenuCode = "expense-account";

    /// <summary>既有功能菜单中文文案</summary>
    public const string RequiredMenuText = "费用科目";

    /// <summary>科目编码持久化长度上限（<c>NVARCHAR(50)</c>，与实体 <c>[MaxLength(50)]</c> 一致）</summary>
    public const int MaxAccountCodeLength = 50;

    /// <summary>科目名称持久化长度上限（<c>NVARCHAR(100)</c>，与实体 <c>[MaxLength(100)]</c> 一致）</summary>
    public const int MaxAccountNameLength = 100;

    /// <summary>科目说明持久化长度上限（<c>NVARCHAR(500)</c>，与实体 <c>[MaxLength(500)]</c> 一致）</summary>
    public const int MaxDescriptionLength = 500;

    /// <summary>已知收入科目类型（<c>BaseExpenseAccount.AccountType = 1</c>）</summary>
    public const int IncomeAccountType = 1;

    /// <summary>已知支出科目类型（<c>BaseExpenseAccount.AccountType = 2</c>）</summary>
    public const int ExpenseAccountType = 2;

    /// <summary>已知启用状态（<c>BaseExpenseAccount.Status = 1</c>）</summary>
    public const int EnabledStatus = 1;

    /// <summary>已知停用状态（<c>BaseExpenseAccount.Status = 0</c>）</summary>
    public const int DisabledStatus = 0;

    /// <summary>无身份 / 非法身份的拒绝文案（受控、不泄露数据）</summary>
    public const string UnauthorizedText = "请先登录后再访问费用科目";

    /// <summary>账号不存在 / 已删除的拒绝文案（受控、不泄露数据）</summary>
    public const string UserDeletedText = "登录账号不存在或已删除，禁止访问费用科目";

    /// <summary>账号已禁用的拒绝文案（受控、不泄露数据）</summary>
    public const string UserDisabledText = "登录账号已禁用，禁止访问费用科目（fail closed）";

    /// <summary>缺少既有「费用科目」菜单授权时的拒绝文案（受控、不泄露数据）</summary>
    public const string MenuDeniedText =
        "当前账号没有「费用科目」（expense-account）模块授权：" +
        "拒绝访问费用科目（fail closed，不返回 / 不新增 / 不改写任何费用科目）";

    /// <summary>授权口径文案（接口 / 文档同源）</summary>
    public const string AuthorizationRuleText =
        "费用科目（分页 / 全部 / 按主键读取 / 新增 / 修改 / 删除 / 批量删除）在读取或写入任何费用科目之前，" +
        "都会重新校验实时身份（缺失 / 非法 / 已删除按未认证，禁用按权限不足，一律 fail closed）" +
        "与既有「费用科目」（expense-account）单项功能菜单授权；菜单授权复用既有「角色 → 菜单」口径，" +
        "每次请求重新查询，撤销后下一次请求立即收敛。";

    /// <summary>授权边界文案（不改写既有业务口径）</summary>
    public const string AuthorizationBoundaryText =
        "本护栏只新增「读取 / 写入前的授权与有界字段校验」：不改变科目编码唯一索引语义、" +
        "分页 / 响应契约与 GenericService 契约，也不静默截断 / 夹取或改写任何科目字段；" +
        "不新增表 / 列 / 实体 / 菜单 / 权限 / 用户授权，也不把空身份当作管理员。";

    /// <summary>
    /// 与请求形状无关的授权契约（沿用 ERP-467 口径）：读取 / 写入前的实时身份 / 账号状态 / 既有「费用科目」菜单判定
    /// 必须在<b>每一个入口</b>无条件执行，与 <c>Request.Path</c> 是否赋值、请求形状、以及控制器是否绑定
    /// <c>HttpContext</c> <b>完全无关</b>——空路径与已赋值路径口径完全一致，完全未绑定 <c>HttpContext</c>
    /// 的纯进程内直调同样执行本护栏；缺失 / 零 / 非法 / 已删除身份一律按未认证拒绝，
    /// 禁用账号 / 撤销或缺少既有菜单一律按权限不足拒绝。
    /// </summary>
    public const string PathIndependenceText =
        "费用科目的实时身份 / 账号状态 / 既有「费用科目」（expense-account）菜单判定与请求路径 / 请求形状 / 是否绑定 HttpContext 完全无关：" +
        "空路径与已赋值路径口径完全一致，未绑定任何请求上下文的纯进程内直调同样执行本护栏；" +
        "缺失 / 零 / 非法 / 已删除身份按未认证拒绝，禁用账号与撤销 / 缺少既有菜单按权限不足拒绝；" +
        "绝无匿名 / 管理员回退，不读取环境变量、不区分数据库提供程序，也不存在任何测试专用放行开关。";

    /// <summary>
    /// 实时身份 / 账号状态 / 既有「费用科目」功能菜单三项校验（fail closed），返回本次请求的权威用户 Id。
    /// <list type="bullet">
    /// <item>身份缺失 / 非法 → <see cref="ErrorCodes.Unauthorized"/>；</item>
    /// <item>账号不存在或已删除 → <see cref="ErrorCodes.Unauthorized"/>；</item>
    /// <item>账号已禁用 → <see cref="ErrorCodes.Forbidden"/>；</item>
    /// <item>缺少既有「费用科目」（<c>expense-account</c>）菜单授权（含被撤销授权）→ <see cref="ErrorCodes.Forbidden"/>。</item>
    /// </list>
    /// 判定发生在任何费用科目读取 / 写入<b>之前</b>；每次请求重新解析，菜单或账号状态变更后立即收敛；
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
    /// 费用科目持久化字段的有界校验（新增 / 修改都在落库之前调用，纯内存判定、不访问数据库）：
    /// <list type="bullet">
    /// <item>科目编码：非空且 ≤ <see cref="MaxAccountCodeLength"/>；</item>
    /// <item>科目名称：非空且 ≤ <see cref="MaxAccountNameLength"/>；</item>
    /// <item>科目类型：仅接受 <see cref="IncomeAccountType"/>（收入）或 <see cref="ExpenseAccountType"/>（支出）；</item>
    /// <item>科目说明：不超 <see cref="MaxDescriptionLength"/>；</item>
    /// <item>状态：仅接受 <see cref="EnabledStatus"/>（启用）或 <see cref="DisabledStatus"/>（停用）。</item>
    /// </list>
    /// 任一项不满足即按 <see cref="ErrorCodes.InvalidParameter"/> 的受控错误拒绝，<b>不</b>静默截断 / 夹取或改写任何字段。
    /// </summary>
    public static void Validate(BaseExpenseAccount? entity)
    {
        if (entity is null)
            throw BusinessException.InvalidParameter("费用科目数据不能为空");

        EnsureRequiredText(entity.AccountCode, MaxAccountCodeLength, "编码");
        EnsureRequiredText(entity.AccountName, MaxAccountNameLength, "名称");
        EnsureOptionalText(entity.Description, MaxDescriptionLength, "说明");

        if (entity.AccountType != IncomeAccountType && entity.AccountType != ExpenseAccountType)
            throw BusinessException.InvalidParameter("费用科目类型只能是 1（收入）或 2（支出）");

        if (entity.Status != EnabledStatus && entity.Status != DisabledStatus)
            throw BusinessException.InvalidParameter("费用科目状态只能是 1（启用）或 0（停用）");
    }

    /// <summary>
    /// 费用科目写入前的完整护栏（新增传 <paramref name="excludeId"/> = <c>null</c>，修改传当前行主键）：
    /// 先做<b>不访问数据库</b>的有界字段校验（<see cref="Validate"/>），再校验科目编码在<b>未删除行中唯一</b>
    /// （修改时排除自身），最后校验 <c>ParentId</c> 非空时指向一条<b>未删除</b>的费用科目。
    /// <list type="bullet">
    /// <item>字段越界 / 类型或状态非法 / 父级不存在或已删除 → <see cref="ErrorCodes.InvalidParameter"/>；</item>
    /// <item>科目编码与既有未删除行重复 → <see cref="ErrorCodes.Duplicate"/>。</item>
    /// </list>
    /// 任一项不满足即拒绝，被拒绝的写入不落任何 <c>BaseExpenseAccounts</c> 行、也不改写任何既有行。
    /// </summary>
    public static async Task EnsureWritableAsync(
        IErpDbContext db, BaseExpenseAccount? entity, long? excludeId = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ct.ThrowIfCancellationRequested();

        // 1) 有界字段校验（不访问数据库）。
        Validate(entity);

        var code = entity!.AccountCode;
        var id = excludeId ?? 0L;

        // 2) 科目编码在未删除行中唯一（修改时排除自身）。
        var duplicated = await db.BaseExpenseAccounts.AsNoTracking()
            .AnyAsync(x => !x.IsDeleted && x.AccountCode == code && x.Id != id, ct);
        if (duplicated)
            throw BusinessException.Duplicate($"费用科目编码「{code}」已存在（未删除行中唯一）");

        // 3) 父级科目（如提供）必须指向一条未删除的费用科目。
        if (entity.ParentId is { } parentId)
        {
            if (parentId <= 0 || parentId == id)
                throw BusinessException.InvalidParameter("费用科目父级科目无效，请选择已存在的未删除科目");
            var parentExists = await db.BaseExpenseAccounts.AsNoTracking()
                .AnyAsync(x => x.Id == parentId && !x.IsDeleted, ct);
            if (!parentExists)
                throw BusinessException.InvalidParameter($"费用科目父级科目（{parentId}）不存在或已删除");
        }
    }

    /// <summary>必填文本校验：空白即拒绝，超过持久化长度上限即拒绝（不做静默截断）。</summary>
    private static void EnsureRequiredText(string? value, int maxLength, string fieldText)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw BusinessException.InvalidParameter($"费用科目{fieldText}不能为空");
        if (value.Length > maxLength)
            throw BusinessException.InvalidParameter($"费用科目{fieldText}长度不能超过 {maxLength} 个字符");
    }

    /// <summary>可选文本校验：仅在提供且超过持久化长度上限时拒绝（不做静默截断）。</summary>
    private static void EnsureOptionalText(string? value, int maxLength, string fieldText)
    {
        if (value is not null && value.Length > maxLength)
            throw BusinessException.InvalidParameter($"费用科目{fieldText}长度不能超过 {maxLength} 个字符");
    }
}
