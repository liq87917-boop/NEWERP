using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ERP.Application.Services;

/// <summary>
/// 客诉单（<see cref="FinanceComplaint"/>）生命周期护栏（ERP-388）：把客诉单的
/// 列表 / 详情 / 创建 / 修改 / 提交 / 审核 / 取消 / 删除，统一到同一条
/// 「实时启用身份 / 客诉单（<c>complaint</c>）菜单 / 当前权威客户数据范围 +
/// 来源销售订单行锁 + 客诉单行锁」的可串行化口径上。
/// <para><b>实时授权先于任何读取 / 计数 / 单据号生成 / 写入</b>：每一个路由都先重新解析
/// <b>实时身份</b>（缺失 / 非法按未认证拒绝）→ <b>账号状态</b>（不存在 / 已删除按未认证，禁用按权限不足）
/// → 既有「客诉单」（<c>complaint</c>）菜单授权 → 权威客户数据范围
/// （复用 ERP-097 <see cref="SalespersonDataScopeService"/>，未映射业务员的受限账号 fail closed）。
/// 每次请求重新查询（无缓存），账号停用或授权撤销后下一次请求立即收敛。</para>
/// <para><b>客户权威性</b>：创建 / 修改校验客诉客户真实可用（存在 / 未删除 / 启用），且必须在当前账号
/// 客户数据范围内；读取 / 状态变更 / 删除前复核<b>库中已存储</b>客诉单的客户归属仍落在当前账号范围内，
/// 受限账号对无权威归属（客户字段缺失）或范围外客户一律 fail closed。</para>
/// <para><b>可空来源销售订单</b>：<see cref="FinanceComplaint.SalesOrderId"/> 可空，历史「未关联来源」
/// （<c>null</c>）语义原样保留，绝不回填、绝不要求历史客诉补链接。新建 / 变更链接时提供 Id 必须按 Id
/// 精确解析到既有、未删除、<b>未取消</b>、客户一致（同客户）的销售订单，并要求当前账号具备既有
/// 「销售订单」（<c>sales-order</c>）菜单授权（非特权账号），否则 fail closed。
/// <b>已记录的来源</b>（例如来源订单事后被取消）保持只读可读，附带显式状态，绝不静默重绑定 / 清除。</para>
/// <para><b>文本长度契约</b>：复用客诉单实体既有 <c>[MaxLength]</c>（客诉类型 100 / 客诉描述 1000 /
/// 责任部门 100 / 处理结果 1000 / 备注 500 / 客诉单号 50），创建 / 修改与持久化复核都按同一契约校验。</para>
/// <para><b>锁序（兼容 ERP-347 / ERP-383 全局锁序）</b>：先<b>来源销售订单行</b>（Id 升序，UPDLOCK/HOLDLOCK，
/// 与销售订单取消共用同一把上游订单行锁），后<b>客诉单行</b>，绝不反向获取。行锁的实际执行留在控制器
/// （关系型提供程序的原始 SQL 能力属基础设施层）：本类只保留锁语句说明常量、需要锁定哪些订单行的确定性
/// 解析与「审计时间戳刷新」式客诉单行锁。</para>
/// <para><b>取消销售订单不受客诉阻断</b>：客诉只是投诉 / 质量反馈追溯，不是库存出运 / 采购履约 / 收款引用 /
/// 定金申请 / 预装柜需求承诺证据；<see cref="SalesOrderCancellationRules"/> 绝不因存在客诉而拒绝取消来源销售订单
/// （见 <see cref="SalesOrderCancellationRules.ComplaintNonBlockingText"/>）。</para>
/// <para>本类只做<b>纯判定与有界只读查询</b>；不落库、不改写单据（<see cref="LockComplaintRowAsync"/> 只刷新技术审计
/// 时间戳以取得排它行锁）；「判定 + 写入」的原子性与同单并发串行化由调用方在同一事务内加锁完成。
/// 本护栏不写库存 / 资金 / 会计，不产生任何销售数量、应收、收款、库存或财务影响。</para>
/// </summary>
public static class FinanceComplaintLifecycleRules
{
    /// <summary>客诉单模块所需既有菜单编码（与 <c>SeedData.Menus</c> 同源）</summary>
    public const string RequiredMenuCode = "complaint";

    /// <summary>客诉单模块菜单中文文案（与既有菜单名一致）</summary>
    public const string RequiredMenuText = "客诉单";

    /// <summary>链接 / 读取销售订单来源所需既有菜单编码（与 <c>SeedData.Menus</c> 同源）</summary>
    public const string SourceRequiredMenuCode = "sales-order";

    /// <summary>销售订单模块菜单中文文案（与既有菜单名一致）</summary>
    public const string SourceRequiredMenuText = "销售订单";

    /// <summary>客诉单号既有长度契约（<see cref="FinanceComplaint.ComplaintNo"/>）</summary>
    public const int ComplaintNoMaxLength = 50;

    /// <summary>客诉类型既有长度契约（<see cref="FinanceComplaint.ComplaintType"/>）</summary>
    public const int ComplaintTypeMaxLength = 100;

    /// <summary>客诉描述既有长度契约（<see cref="FinanceComplaint.Description"/>）</summary>
    public const int DescriptionMaxLength = 1000;

    /// <summary>责任部门既有长度契约（<see cref="FinanceComplaint.ResponsibleDept"/>）</summary>
    public const int ResponsibleDeptMaxLength = 100;

    /// <summary>处理结果既有长度契约（<see cref="FinanceComplaint.HandleResult"/>）</summary>
    public const int HandleResultMaxLength = 1000;

    /// <summary>备注既有长度契约（<see cref="FinanceComplaint.Remark"/>）</summary>
    public const int RemarkMaxLength = 500;

    /// <summary>生命周期护栏口径文案（接口 / 文档同源）</summary>
    public const string RuleText =
        "客诉单（列表 / 详情 / 创建 / 修改 / 提交 / 审核 / 取消 / 删除）在读取任何计数、单据号或写入之前，"
        + "都会重新校验实时启用身份（缺失 / 非法 / 已删除按未认证，禁用按权限不足）、既有「客诉单」（complaint）菜单授权与当前权威客户数据范围；"
        + "客诉客户必须真实启用且在范围内，读取 / 状态变更 / 删除前复核已存储客户归属（受限账号对无权威归属 fail closed）；"
        + "客诉类型 / 客诉描述 / 责任部门 / 处理结果 / 备注按既有长度契约校验；"
        + "可空来源销售订单按 Id 精确解析既有、未删除、未取消、同客户的订单，并要求既有「销售订单」（sales-order）菜单授权（非特权账号），"
        + "历史未关联来源（null）语义原样保留，绝不回填；已记录的来源保持只读可读并附显式状态，绝不静默重绑定或清除；"
        + "创建在生成单号与写入之前完成全部校验（失败绝不消耗单号）；修改 / 提交 / 审核 / 取消 / 删除都在同一事务内先取来源销售订单行锁、再取客诉单行锁，"
        + "并在锁内重新复核身份 / 菜单 / 客户范围 / 状态 / 文本与来源链接；失败整体回滚，历史与原始审计保持不变。";

    /// <summary>模块边界文案（不新增权限 / 表列，不写库存 / 资金 / 会计，也不阻断销售订单取消）</summary>
    public const string BoundaryText =
        "本护栏只保护客诉单生命周期与本单既有客户 / 来源链接 / 文本字段：不新增菜单 / 角色 / 用户授权或表结构，也不把空身份当作管理员；"
        + "不写库存 / 库存流水 / 资金 / 会计凭证，不产生销售数量、收款、付款、核销、应收或分摊入账；"
        + "客诉只是投诉 / 质量反馈追溯，不是库存出运 / 采购履约 / 收款引用 / 定金申请 / 预装柜需求承诺证据；"
        + "取消来源销售订单绝不因存在客诉而拒绝，也不改写 / 清除 / 重绑定任何客诉链接；"
        + "客诉单的商业字段与删除标记只在既有允许的状态流转下变更，绝不物理删除任一历史记录。";

    /// <summary>锁序文案（兼容全局锁序：先来源销售订单行、后客诉单行）</summary>
    public const string LockOrderText =
        "ERP-388 锁序：先来源销售订单行（Id 升序，UPDLOCK, HOLDLOCK，与销售订单取消共用同一把上游订单行锁），"
        + "后客诉单行，绝不反向获取。";

    /// <summary>来源销售订单行锁说明（行锁由基础设施层执行，与销售订单取消共用同一把来源行锁）</summary>
    public const string SourceOrderLockNote =
        "来源销售订单行锁语句与销售订单取消共用同一常量（PreLoadingSalesOrderLinkRules.LockSalesOrderRowSql），"
        + "多个来源订单按 Id 升序加锁，绝不反向获取下游锁。";

    /// <summary>无身份 / 非法身份的拒绝文案</summary>
    public const string UnauthorizedText = "请先登录后再访问客诉单";

    /// <summary>账号已删除的拒绝文案</summary>
    public const string UserDeletedText = "登录账号不存在或已删除，禁止访问客诉单";

    /// <summary>账号已禁用的拒绝文案</summary>
    public const string UserDisabledText = "登录账号已禁用，禁止访问客诉单（fail closed）";

    /// <summary>无既有菜单授权的拒绝文案</summary>
    public const string MenuDeniedText =
        "当前账号没有「客诉单」（complaint）模块授权：拒绝访问客诉单（fail closed，不返回 / 不修改任何客诉数据）";

    /// <summary>链接销售订单所需既有「销售订单」菜单授权缺失的拒绝文案</summary>
    public const string SourceMenuDeniedText =
        "当前账号没有「销售订单」（sales-order）模块授权：拒绝读取 / 链接来源销售订单（fail closed，不返回任何订单数据）";

    /// <summary>客户越范围的拒绝文案</summary>
    public const string OutOfScopeText =
        "当前账号的客户数据范围不包含该客户（fail closed，不泄露范围外客户）";

    /// <summary>客诉客户无权威归属（字段缺失）时对受限账号的拒绝文案</summary>
    public const string UnlinkedCustomerText =
        "客诉单缺少权威客户归属且当前账号无全局权限：拒绝访问（fail closed）";

    /// <summary>未关联销售订单的显式证据文案（历史 / 未登记来源，绝不回填）</summary>
    public const string UnlinkedEvidenceText =
        "未关联销售订单（历史 / 未登记来源；保持显式未关联，绝不回填）";

    /// <summary>已关联销售订单的显式证据文案前缀</summary>
    public const string LinkedEvidenceText = "已关联销售订单";

    /// <summary>来源销售订单已取消的显式状态文案（历史链接只读保留，绝不静默重绑定）</summary>
    public const string SourceCancelledEvidenceText =
        "来源销售订单已取消（历史链接只读保留，绝不静默重绑定 / 清除）";

    /// <summary>来源销售订单已删除 / 无法解析的显式状态文案（原链接原样保留，绝不静默清除）</summary>
    public const string UnavailableEvidenceText =
        "来源不可用（销售订单已删除或无法解析；原链接原样保留，绝不静默清除）";

    /// <summary>新链接拒绝已取消来源销售订单的文案</summary>
    public const string CancelledLinkRejectedText =
        "已取消的销售订单不能作为新建 / 变更客诉单的来源（仅历史已记录的链接可按显式状态只读保留）";

    /// <summary>来源销售订单已删除 / 悬空的拒绝文案</summary>
    public const string DanglingLinkRejectedText =
        "销售订单不存在或已删除，不能作为客诉单来源（系统不按订单号文本、金额或相似度猜测来源）";

    /// <summary>候选列表中「已取消来源不可作为新来源」的不可选原因文案（ERP-389，与链接拒绝同口径）</summary>
    public const string SourceCancelledCandidateText =
        "销售订单已取消：不能作为新建 / 变更客诉单的来源（历史已记录的链接按显式状态只读保留，绝不静默重绑定）";

    /// <summary>来源销售订单客户与客诉客户不一致的拒绝文案</summary>
    public const string ForeignCustomerLinkText =
        "销售订单的客户与客诉单客户不一致，不能作为来源（来源必须是本客诉单客户的既有订单）";

    // ==================== 1. 文本、客户纯校验 ====================

    /// <summary>文本长度契约校验（既有 <c>[MaxLength]</c> 同源）：超长即按参数错误拒绝，返回原值（不静默截断）。</summary>
    public static string EnsureTextWithinLength(string? value, int maxLength, string fieldName)
    {
        var text = value ?? string.Empty;
        if (text.Length > maxLength)
            throw BusinessException.InvalidParameter(
                $"{fieldName}长度不能超过 {maxLength} 个字符（当前 {text.Length}）");
        return text;
    }

    /// <summary>校验客诉单可编辑文本字段（客诉类型 / 客诉描述 / 责任部门 / 处理结果 / 备注）的既有长度契约。</summary>
    public static void EnsureEditableTextLengths(FinanceComplaint entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        EnsureTextWithinLength(entity.ComplaintType, ComplaintTypeMaxLength, "客诉类型");
        EnsureTextWithinLength(entity.Description, DescriptionMaxLength, "客诉描述");
        EnsureTextWithinLength(entity.ResponsibleDept, ResponsibleDeptMaxLength, "责任部门");
        EnsureTextWithinLength(entity.HandleResult, HandleResultMaxLength, "处理结果");
        EnsureTextWithinLength(entity.Remark, RemarkMaxLength, "备注");
    }

    /// <summary>校验客诉单全部文本字段（含客诉单号）的既有长度契约（持久化复核用）。</summary>
    public static void EnsureTextLengths(FinanceComplaint entity)
    {
        EnsureEditableTextLengths(entity);
        EnsureTextWithinLength(entity.ComplaintNo, ComplaintNoMaxLength, "客诉单号");
    }

    /// <summary>客户可用性校验（存在、未删除、启用）：不存在 / 已删除按不存在拒绝，停用按规则冲突拒绝。</summary>
    public static void EnsureCustomerAvailable(BaseCustomer? customer, long customerId)
    {
        if (customer is null || customer.IsDeleted)
            throw BusinessException.NotFound($"客户（Id={customerId}）不存在或已删除，不能用于客诉单");
        if (customer.Status != 1)
            throw BusinessException.RuleConflict(
                $"客户「{customer.CustomerName}」已停用：停用客户不能用于新客诉单或修改客诉单客户");
    }

    /// <summary>客户数据范围硬边界（受限制业务员只能操作其被分配客户，越界 fail closed 不泄露存在性）。</summary>
    public static void EnsureCustomerInScope(SalespersonDataScope scope, long customerId)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (!scope.AllowsCustomer(customerId))
            throw new BusinessException(OutOfScopeText, ErrorCodes.Forbidden);
    }

    // ==================== 2. 身份 / 菜单 / 客户数据范围（fail closed） ====================

    /// <summary>客诉单菜单授权 + 解析当前权威客户数据范围（供列表过滤与范围复核复用，绝不缓存）。</summary>
    public static Task<SalespersonDataScope> EnsureMenuAuthorizedAsync(
        IErpDbContext db, long? userId, CancellationToken ct = default)
        => EnsureMenuCoreAsync(db, userId, RequiredMenuCode, RequiredMenuText, MenuDeniedText, ct, bypassForPrivileged: false);

    /// <summary>
    /// 链接 / 读取「销售订单」来源证据所需既有「销售订单」（<c>sales-order</c>）菜单授权
    /// （非特权账号必须显式具备；特权账号保留既有全局访问）。
    /// </summary>
    public static Task<SalespersonDataScope> EnsureSourceMenuAuthorizedAsync(
        IErpDbContext db, long? userId, CancellationToken ct = default)
        => EnsureMenuCoreAsync(db, userId, SourceRequiredMenuCode, SourceRequiredMenuText, SourceMenuDeniedText, ct, bypassForPrivileged: true);

    /// <summary>
    /// 三重授权校验：身份（缺失 / 非正整数 → 未认证）→ 客诉单菜单授权 → 客户数据范围（越界 → 权限不足）。
    /// 每次请求都重新解析，撤销授权 / 客户分配变更后立即收敛。
    /// </summary>
    public static async Task EnsureAuthorizedAsync(
        IErpDbContext db, long? userId, long customerId, CancellationToken ct = default)
    {
        var scope = await EnsureMenuAuthorizedAsync(db, userId, ct);
        EnsureCustomerInScope(scope, customerId);
    }

    /// <summary>
    /// 复核<b>库中已存储</b>客诉单的权威归属是否落在当前账号范围内（读取 / 详情 / 状态变更 / 删除之前）：
    /// 客诉客户必须在范围内，无权威归属（客户字段缺失）对受限账号一律 fail closed；特权账号保留历史访问。
    /// </summary>
    public static void EnsureStoredComplaintScopeAllowed(SalespersonDataScope scope, FinanceComplaint complaint)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(complaint);
        EnsureCustomerInScope(scope, complaint.CustomerId);
    }

    private static async Task<SalespersonDataScope> EnsureMenuCoreAsync(
        IErpDbContext db, long? userId, string menuCode, string menuText, string deniedText,
        CancellationToken ct, bool bypassForPrivileged)
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
        if (bypassForPrivileged && scope.IsPrivileged) return scope;

        var menuCodes = await CustomerReceivableReconciliationService
            .LoadAuthorizedMenuCodesAsync(db, userId.Value);
        if (!menuCodes.Contains(menuCode, StringComparer.OrdinalIgnoreCase))
            throw new BusinessException(deniedText, ErrorCodes.Forbidden);

        return scope;
    }

    // ==================== 3. 来源销售订单解析（按 Id，绝不从文本猜测） ====================

    /// <summary>
    /// 解析客诉单的来源销售订单（可空）：未提供 Id 返回 <c>null</c>（保留有效的历史「未关联来源」语义）；
    /// 提供 Id 时必须按 Id 精确解析到既有、未删除、<b>未取消</b>且客户一致的销售订单。
    /// <para>绝不按订单号文本、金额或相似度猜测来源：Id 不存在 / 已删除即按「来源不存在」拒绝（dangling / forged）；
    /// 来源已取消或客户不一致时 fail closed，不泄露范围外订单。</para>
    /// </summary>
    public static async Task<SalesOrder?> ResolveSourceSalesOrderAsync(
        IErpDbContext db, long? salesOrderId, long customerId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (salesOrderId is null or <= 0) return null;

        var order = await db.SalesOrders.AsNoTracking()
            .FirstOrDefaultAsync(o => o.Id == salesOrderId.Value && !o.IsDeleted, ct)
            ?? throw BusinessException.NotFound(
                $"{DanglingLinkRejectedText}（Id={salesOrderId.Value}）");

        if (order.Status == DocumentStatus.Cancelled)
            throw BusinessException.RuleConflict($"{CancelledLinkRejectedText}：销售订单「{order.OrderNo}」");

        if (order.CustomerId != customerId)
            throw BusinessException.RuleConflict($"{ForeignCustomerLinkText}：销售订单「{order.OrderNo}」");

        return order;
    }

    /// <summary>
    /// 新建 / 变更来源时可被显式选择的状态判定（ERP-389）：既有「未取消」订单可以为新来源建立链接；
    /// 已取消订单只能作为<b>历史</b>已记录链接只读保留，绝不作为新来源（与
    /// <see cref="ResolveSourceSalesOrderAsync"/> 的取消拒绝同口径）。
    /// </summary>
    public static bool IsEligibleNewSource(DocumentStatus status) => status != DocumentStatus.Cancelled;

    /// <summary>
    /// 校验并授权<b>新建 / 变更</b>的销售订单来源链接：未提供来源时直接返回 <c>null</c>
    /// （历史未关联来源语义，**不要求**「销售订单」菜单授权）；提供来源时才要求当前账号具备既有
    /// 「销售订单」（<c>sales-order</c>）菜单授权，再按 Id 精确解析未删除、未取消、同客户的来源订单。
    /// </summary>
    public static async Task<SalesOrder?> ValidateAndAuthorizeLinkAsync(
        IErpDbContext db, long? userId, long? salesOrderId, long customerId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (salesOrderId is null or <= 0) return null;

        await EnsureSourceMenuAuthorizedAsync(db, userId, ct);
        return await ResolveSourceSalesOrderAsync(db, salesOrderId, customerId, ct);
    }

    /// <summary>
    /// 用既有规则复核<strong>已持久化</strong>客诉单的客户 / 文本 / 来源链接一致性（只读，不写库）：
    /// 客户必须存在、未删除且启用，文本字段必须满足既有长度契约，关联销售订单（若存在）必须仍可按 Id 解析到
    /// 既有、未删除且同客户的订单。
    /// <para><b>历史已取消的来源不阻断流转</b>：来源订单事后被取消时，客诉单仍保持有效、可读（附显式状态），
    /// 本复核<b>不因来源已取消而拒绝</b>——否则会变相阻断销售订单取消或使历史客诉失效。
    /// 来源已删除 / 悬空或客户不一致（forged）才 fail closed。</para>
    /// </summary>
    public static async Task EnsurePersistedComplaintConsistentAsync(
        IErpDbContext db, FinanceComplaint complaint, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(complaint);

        EnsureTextLengths(complaint);

        var customer = await db.BaseCustomers.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == complaint.CustomerId, ct);
        EnsureCustomerAvailable(customer, complaint.CustomerId);

        if (complaint.SalesOrderId is null or <= 0) return;

        var order = await db.SalesOrders.AsNoTracking()
            .FirstOrDefaultAsync(o => o.Id == complaint.SalesOrderId.Value && !o.IsDeleted, ct);
        if (order is null)
            throw BusinessException.NotFound(
                $"{DanglingLinkRejectedText}（Id={complaint.SalesOrderId.Value}）");
        if (order.CustomerId != complaint.CustomerId)
            throw BusinessException.RuleConflict($"{ForeignCustomerLinkText}：销售订单「{order.OrderNo}」");
        // 已取消的来源保留历史链接语义，不阻断流转、不静默清除。
    }

    /// <summary>
    /// 读取客诉单当前持久化的来源销售订单 Id（仅用于按「先来源销售订单行、后客诉单行」取锁；权威校验一律在锁内
    /// 重做）。未关联来源（null）/ 不存在 / 已删除返回 null，保留历史未关联来源语义。
    /// </summary>
    public static async Task<long?> ReadPersistedSalesOrderIdAsync(
        IErpDbContext db, long complaintId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (complaintId <= 0) return null;

        return await db.FinanceComplaints.AsNoTracking()
            .Where(c => c.Id == complaintId && !c.IsDeleted)
            .Select(c => c.SalesOrderId)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// 构造客诉单来源链接的<b>显式状态</b>文案（详情 / 只读展示用）：未关联（历史）→ 显式「未关联」；
    /// 已关联且来源有效 → 已关联 + 订单号；来源已取消 → 已关联 + 「来源已取消，只读保留」；
    /// 来源已删除 / 无法解析 → 「来源不可用，原链接原样保留」。
    /// <para>本方法绝不写库、绝不重绑定 / 清除链接，也绝不因来源失效而抛异常（历史必须可读）。</para>
    /// </summary>
    public static async Task<string> DescribeStoredSourceAsync(
        IErpDbContext db, FinanceComplaint complaint, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(complaint);

        if (complaint.SalesOrderId is null or <= 0) return UnlinkedEvidenceText;

        var order = await db.SalesOrders.AsNoTracking()
            .Where(o => o.Id == complaint.SalesOrderId.Value && !o.IsDeleted)
            .Select(o => new { o.OrderNo, o.Status })
            .FirstOrDefaultAsync(ct);

        if (order is null)
            return $"{LinkedEvidenceText} Id {complaint.SalesOrderId.Value}：{UnavailableEvidenceText}";

        return order.Status == DocumentStatus.Cancelled
            ? $"{LinkedEvidenceText}「{order.OrderNo}」：{SourceCancelledEvidenceText}"
            : $"{LinkedEvidenceText}「{order.OrderNo}」";
    }

    /// <summary>
    /// 合并本次动作需要加锁的来源销售订单 Id（既有持久化来源 + 请求来源）：去重、只保留正整数，并按 Id 升序返回，
    /// 以保证多个订单行锁的确定性获取顺序（绝不反向获取下游锁）。
    /// </summary>
    public static IReadOnlyList<long> MergeSourceOrderLockIds(params long?[] salesOrderIds)
    {
        ArgumentNullException.ThrowIfNull(salesOrderIds);
        return salesOrderIds
            .Where(id => id is > 0)
            .Select(id => id!.Value)
            .Distinct()
            .OrderBy(id => id)
            .ToList();
    }

    // ==================== 4. 行锁（与来源销售订单取消串行化） ====================

    /// <summary>
    /// 关系型后端（SQL Server）判定：内存库等非关系型提供程序没有行锁语义，锁定与事务等价无操作。
    /// 使用 <c>DatabaseFacade.ProviderName</c>（EF Core 基础 API），不依赖关系型扩展。
    /// </summary>
    public static bool IsRelationalProvider(IErpDbContext db)
        => string.Equals(db.Database.ProviderName, "Microsoft.EntityFrameworkCore.SqlServer",
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 对客诉单行加更新锁，把同单并发的「客诉单生命周期 / 商业改动」串行化在同一事务内。
    /// <para>实现（仅用 EF Core 基础 API，不依赖关系型扩展）：在调用方事务内对客诉单行发出一条「审计时间戳刷新」的
    /// UPDATE，从而取得排它行锁（X 锁，持有至事务结束），语义等价于 <c>SELECT ... WITH (UPDLOCK, HOLDLOCK)</c>；
    /// <c>UpdatedAt</c> 是技术审计字段、不是商业字段，因此不构成对客诉单商业内容的静默改写。
    /// 内存库等非关系型提供程序无行锁语义，直接跳过（事务等价无事务）。</para>
    /// <para><b>锁序（ERP-388）</b>：来源销售订单行必须先于客诉单行获取，本方法必须由调用方在同一事务内、
    /// 任何来源销售订单行锁之后调用，绝不反向获取上游锁。</para>
    /// </summary>
    public static async Task LockComplaintRowAsync(
        IErpDbContext db, long complaintId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (complaintId <= 0) return;
        if (!IsRelationalProvider(db)) return;

        var complaint = await db.FinanceComplaints
            .FirstOrDefaultAsync(c => c.Id == complaintId && !c.IsDeleted, ct);
        if (complaint is null) return;

        complaint.UpdatedAt = DateTime.Now;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw BusinessException.RuleConflict(
                "客诉单正在被并发修改，本次操作未生效：请刷新后重试（原始客诉单与审计均未改变）");
        }
    }
}
