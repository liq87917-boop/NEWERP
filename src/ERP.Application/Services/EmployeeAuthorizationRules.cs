using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ERP.Application.Services;

/// <summary>
/// 员工资料（<see cref="BaseEmployee"/>，<c>api/base/employees</c>）的实时身份 / 既有功能菜单授权
/// 与有界字段校验护栏（ERP-449）。员工主数据提供销售订单 / 客户 / 报价单 / PI / 装柜清单与
/// ERP-097 业务员数据范围所解析的<b>业务员身份</b>，因此必须与其它主数据同源地 fail closed。
/// <list type="number">
/// <item><b>实时授权</b>（<see cref="EnsureAuthorizedAsync"/>）：分页 / 全部 / 按主键读取与新增 / 修改 / 删除 /
/// 批量删除<b>每一</b>路由，以及业务员下拉（<c>salesmen</c>），在读取或写入任何 <c>BaseEmployees</c> 行
/// <b>之前</b>都重新解析实时身份（缺失 / 非法 / 账号不存在或已删除按未认证，禁用按权限不足）与既有
/// 「员工资料」（<c>employee</c>）功能菜单授权（缺菜单 / 被撤销按权限不足），一律 fail closed；</item>
/// <item><b>有界字段校验</b>（<see cref="Validate"/>）：新增 / 修改在落库之前校验员工编码与姓名非空且在
/// 持久化长度上限内、部门 / 职位 / 联系电话 / 邮箱不超持久化长度、入职日期落在持久化日期形状内、
/// 状态为已知的 <c>1</c>（在职）/ <c>0</c>（离职）；被拒绝的写入不落任何行。</item>
/// </list>
/// <para>边界（重要）：本护栏只新增「读取 / 写入前的判定」，<b>不</b>静默截断 / 夹取或改写任何员工字段，
/// 也<b>不</b>改变既有「员工编码」唯一索引语义、分页 / 响应契约与 <see cref="GenericService{TEntity}"/> 契约；
/// <b>不</b>新增任何表 / 列 / 实体 / 菜单 / 权限 / 用户授权，不伪造任何授权，<b>不</b>因身份缺失而降级为管理员，
/// 也<b>不</b>新增匿名 / 特权旁路。</para>
/// </summary>
public static class EmployeeAuthorizationRules
{
    /// <summary>所需既有功能菜单编码（与 <c>SeedData.Menus</c> 同源，<b>不新增菜单</b>）</summary>
    public const string RequiredMenuCode = "employee";

    /// <summary>既有功能菜单中文文案</summary>
    public const string RequiredMenuText = "员工资料";

    /// <summary>员工编码持久化长度上限（<c>NVARCHAR(50)</c>，与实体 <c>[MaxLength(50)]</c> 一致）</summary>
    public const int MaxEmployeeCodeLength = 50;

    /// <summary>员工姓名持久化长度上限（<c>NVARCHAR(50)</c>，与实体 <c>[MaxLength(50)]</c> 一致）</summary>
    public const int MaxEmployeeNameLength = 50;

    /// <summary>部门持久化长度上限（<c>NVARCHAR(100)</c>，与实体 <c>[MaxLength(100)]</c> 一致）</summary>
    public const int MaxDepartmentLength = 100;

    /// <summary>职位持久化长度上限（<c>NVARCHAR(100)</c>，与实体 <c>[MaxLength(100)]</c> 一致）</summary>
    public const int MaxPositionLength = 100;

    /// <summary>联系电话持久化长度上限（<c>NVARCHAR(50)</c>，与实体 <c>[MaxLength(50)]</c> 一致）</summary>
    public const int MaxPhoneLength = 50;

    /// <summary>邮箱持久化长度上限（<c>NVARCHAR(100)</c>，与实体 <c>[MaxLength(100)]</c> 一致）</summary>
    public const int MaxEmailLength = 100;

    /// <summary>
    /// 入职日期持久化下限（SQL Server 日期列可存储的最早日期 <c>1753-01-01</c>）：
    /// 早于该日期（含默认哨兵 <c>0001-01-01</c>）无法作为真实入职日期持久化，写入前按参数错误拒绝。
    /// </summary>
    public static readonly DateTime MinHireDate = new(1753, 1, 1);

    /// <summary>入职日期持久化上限（SQL Server 日期列可存储的最晚日期 <c>9999-12-31</c>）</summary>
    public static readonly DateTime MaxHireDate = new(9999, 12, 31);

    /// <summary>已知在职状态（<c>BaseEmployee.Status = 1</c>）</summary>
    public const int OnDutyStatus = 1;

    /// <summary>已知离职状态（<c>BaseEmployee.Status = 0</c>）</summary>
    public const int OffDutyStatus = 0;

    /// <summary>无身份 / 非法身份的拒绝文案（受控、不泄露数据）</summary>
    public const string UnauthorizedText = "请先登录后再访问员工资料";

    /// <summary>账号不存在 / 已删除的拒绝文案（受控、不泄露数据）</summary>
    public const string UserDeletedText = "登录账号不存在或已删除，禁止访问员工资料";

    /// <summary>账号已禁用的拒绝文案（受控、不泄露数据）</summary>
    public const string UserDisabledText = "登录账号已禁用，禁止访问员工资料（fail closed）";

    /// <summary>缺少既有「员工资料」菜单授权时的拒绝文案（受控、不泄露数据）</summary>
    public const string MenuDeniedText =
        "当前账号没有「员工资料」（employee）模块授权：" +
        "拒绝访问员工资料（fail closed，不返回 / 不新增 / 不改写任何员工）";

    /// <summary>授权口径文案（接口 / 文档同源）</summary>
    public const string AuthorizationRuleText =
        "员工资料（分页 / 全部 / 按主键读取 / 新增 / 修改 / 删除 / 批量删除）与业务员下拉（salesmen）在读取或写入任何员工之前，" +
        "都会重新校验实时身份（缺失 / 非法 / 已删除按未认证，禁用按权限不足，一律 fail closed）" +
        "与既有「员工资料」（employee）单项功能菜单授权；菜单授权复用既有「角色 → 菜单」口径，" +
        "每次请求重新查询，撤销后下一次请求立即收敛。";

    /// <summary>授权边界文案（不改写既有业务口径）</summary>
    public const string AuthorizationBoundaryText =
        "本护栏只新增「读取 / 写入前的授权与有界字段校验」：不改变员工编码唯一索引语义、" +
        "分页 / 响应契约与 GenericService 契约，也不静默截断 / 夹取或改写任何员工字段；" +
        "不新增表 / 列 / 实体 / 菜单 / 权限 / 用户授权，也不把空身份当作管理员。";

    /// <summary>
    /// 实时身份 / 账号状态 / 既有「员工资料」功能菜单三项校验（fail closed），返回本次请求的权威用户 Id。
    /// <list type="bullet">
    /// <item>身份缺失 / 非法 → <see cref="ErrorCodes.Unauthorized"/>；</item>
    /// <item>账号不存在或已删除 → <see cref="ErrorCodes.Unauthorized"/>；</item>
    /// <item>账号已禁用 → <see cref="ErrorCodes.Forbidden"/>；</item>
    /// <item>缺少既有「员工资料」（<c>employee</c>）菜单授权（含被撤销授权）→ <see cref="ErrorCodes.Forbidden"/>。</item>
    /// </list>
    /// 判定发生在任何员工读取 / 写入<b>之前</b>；每次请求重新解析，菜单或账号状态变更后立即收敛；
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
    /// 员工持久化字段的有界校验（新增 / 修改都在落库之前调用）：
    /// <list type="bullet">
    /// <item>员工编码：非空且 ≤ <see cref="MaxEmployeeCodeLength"/>；</item>
    /// <item>员工姓名：非空且 ≤ <see cref="MaxEmployeeNameLength"/>；</item>
    /// <item>部门 / 职位 / 联系电话 / 邮箱：不超各自持久化长度上限；</item>
    /// <item>入职日期：落在持久化日期形状 <see cref="MinHireDate"/> ~ <see cref="MaxHireDate"/> 内（可空）；</item>
    /// <item>状态：仅接受 <see cref="OnDutyStatus"/>（在职）或 <see cref="OffDutyStatus"/>（离职）。</item>
    /// </list>
    /// 任一项不满足即按 <see cref="ErrorCodes.InvalidParameter"/> 的受控错误拒绝，<b>不</b>静默截断 / 夹取或改写任何字段，
    /// 被拒绝的写入不落任何 <c>BaseEmployees</c> 行（也<b>不</b>改写任何既有行）。
    /// </summary>
    public static void Validate(BaseEmployee? entity)
    {
        if (entity is null)
            throw BusinessException.InvalidParameter("员工数据不能为空");

        EnsureRequiredText(entity.EmployeeCode, MaxEmployeeCodeLength, "编码");
        EnsureRequiredText(entity.EmployeeName, MaxEmployeeNameLength, "姓名");

        EnsureOptionalText(entity.Department, MaxDepartmentLength, "部门");
        EnsureOptionalText(entity.Position, MaxPositionLength, "职位");
        EnsureOptionalText(entity.Phone, MaxPhoneLength, "联系电话");
        EnsureOptionalText(entity.Email, MaxEmailLength, "邮箱");

        EnsureHireDate(entity.HireDate);

        if (entity.Status != OnDutyStatus && entity.Status != OffDutyStatus)
            throw BusinessException.InvalidParameter("员工状态只能是 1（在职）或 0（离职）");
    }

    /// <summary>必填文本校验：空白即拒绝，超过持久化长度上限即拒绝（不做静默截断）。</summary>
    private static void EnsureRequiredText(string? value, int maxLength, string fieldText)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw BusinessException.InvalidParameter($"员工{fieldText}不能为空");
        if (value.Length > maxLength)
            throw BusinessException.InvalidParameter($"员工{fieldText}长度不能超过 {maxLength} 个字符");
    }

    /// <summary>可选文本校验：仅在提供且超过持久化长度上限时拒绝（不做静默截断）。</summary>
    private static void EnsureOptionalText(string? value, int maxLength, string fieldText)
    {
        if (value is not null && value.Length > maxLength)
            throw BusinessException.InvalidParameter($"员工{fieldText}长度不能超过 {maxLength} 个字符");
    }

    /// <summary>入职日期校验：可空；非空值必须落在持久化日期形状内（不做静默夹取 / 改写）。</summary>
    private static void EnsureHireDate(DateTime? value)
    {
        if (value is null) return;
        if (value.Value < MinHireDate || value.Value > MaxHireDate)
            throw BusinessException.InvalidParameter(
                $"员工入职日期超出可存储范围（{MinHireDate:yyyy-MM-dd} ~ {MaxHireDate:yyyy-MM-dd}），请填写有效日期");
    }
}
