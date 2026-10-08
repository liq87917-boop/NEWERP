using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ERP.Application.Services;

/// <summary>
/// 散货结算单（<see cref="FinanceBulkSettlement"/>）生命周期护栏（ERP-387）：把散货结算单的
/// 列表 / 详情 / 创建 / 修改 / 提交 / 审核 / 取消 / 删除，统一到同一条
/// 「实时启用身份 / 散货结算单（<c>bulk-settlement</c>）菜单 / 当前权威客户数据范围 +
/// 散货结算单行锁」的可串行化口径上。
/// <para><b>实时授权先于任何读取 / 计数 / 单据号生成 / 写入</b>：每一个路由都先重新解析
/// <b>实时身份</b>（缺失 / 非法按未认证拒绝）→ <b>账号状态</b>（不存在 / 已删除按未认证，禁用按权限不足）
/// → 既有「散货结算单」（<c>bulk-settlement</c>）菜单授权（非特权账号必须显式具备）→ 权威客户数据范围
/// （复用 ERP-097 <see cref="SalespersonDataScopeService"/>，未映射业务员的受限账号 fail closed）。
/// 每次请求重新查询（无缓存），账号停用或授权撤销后下一次请求立即收敛。</para>
/// <para><b>客户权威性</b>：创建 / 修改校验结算客户真实可用（存在 / 未删除 / 启用），且必须在当前账号
/// 客户数据范围内；读取 / 状态变更 / 删除前复核<b>库中已存储</b>结算单的客户归属仍落在当前账号范围内，
/// 受限账号对无权威归属（客户字段缺失）或范围外客户一律 fail closed；特权账号保留既有不受限历史访问，
/// 绝不新增任何菜单 / 角色 / 用户授权或降级为匿名 / 管理员。</para>
/// <para><b>金额语义</b>：散货结算单没有币种字段，因此金额只按<b>既有存储精度</b>（<c>decimal(18,2)</c>，2 位小数，
/// 0.5 进位）做正负校验：<see cref="FinanceBulkSettlement.TotalAmount"/> 必须大于 0，
/// <see cref="FinanceBulkSettlement.FreightCost"/> 不得为负数。绝不发明汇率、绝不跨币种聚合，
/// 也不施加 <c>total = freight</c> 之类的公式。</para>
/// <para>本类只做<b>纯判定与有界只读查询</b>；不落库、不改写单据（<see cref="LockSettlementRowAsync"/> 只刷新技术审计
/// 时间戳以取得排它行锁）；「判定 + 写入」的原子性与同单并发串行化由调用方在同一事务内加锁完成。
/// 本护栏不写库存 / 资金 / 会计，不产生任何记账、核销或流水。</para>
/// </summary>
public static class FinanceBulkSettlementLifecycleRules
{
    /// <summary>散货结算单模块所需既有菜单编码（与 <c>SeedData.Menus</c> 同源）</summary>
    public const string RequiredMenuCode = "bulk-settlement";

    /// <summary>散货结算单模块菜单中文文案（与既有菜单名一致）</summary>
    public const string RequiredMenuText = "散货结算单";

    /// <summary>金额的既有存储精度（<c>decimal(18,2)</c>，EF Core SQL Server 默认），固定 2 位小数、0.5 进位</summary>
    public const int AmountDecimals = 2;

    /// <summary>生命周期护栏口径文案（接口 / 文档同源）</summary>
    public const string RuleText =
        "散货结算单（列表 / 详情 / 创建 / 修改 / 提交 / 审核 / 取消 / 删除）在读取任何计数、单据号或写入之前，"
        + "都会重新校验实时启用身份（缺失 / 非法 / 已删除按未认证，禁用按权限不足）、既有「散货结算单」（bulk-settlement）菜单授权与当前权威客户数据范围；"
        + "结算客户必须真实启用且在范围内，读取 / 状态变更 / 删除前复核已存储客户归属（受限账号对无权威归属 fail closed）；"
        + "结算总金额必须大于 0，海运费不得为负（只按既有存储精度校验，不换算、不跨币种聚合、不施加合计公式）；"
        + "创建在生成单号与写入之前完成全部校验（失败绝不消耗单号）；修改 / 提交 / 审核 / 取消 / 删除都在同一事务内先取散货结算单行锁，"
        + "并在锁内重新复核身份 / 菜单 / 客户范围 / 状态 / 金额；失败整体回滚，历史与原始审计保持不变。";

    /// <summary>模块边界文案（不新增权限 / 表列，不写库存 / 资金 / 会计）</summary>
    public const string BoundaryText =
        "本护栏只保护散货结算单生命周期与本单既有客户 / 金额字段：不新增菜单 / 角色 / 用户授权或表结构，也不把空身份当作管理员；"
        + "不写库存 / 库存流水 / 资金 / 会计凭证，不产生收款、付款、核销、分摊入账或对账结论；"
        + "散货结算单没有币种字段与来源单据，因此不做汇率换算、不跨币种聚合、不推断装运 / 订单来源；"
        + "散货结算单的商业字段（日期 / 客户 / 金额 / 备注）与删除标记只在既有允许的状态流转下变更，绝不物理删除任一历史记录。";

    /// <summary>锁序文案（同单串行化：仅散货结算单行锁，无上游来源行）</summary>
    public const string LockOrderText =
        "ERP-387 锁序：散货结算单没有上游来源单据，因此只对散货结算单行取更新锁（UPDLOCK / 乐观并发），"
        + "把同单并发的「修改 / 提交 / 审核 / 取消 / 删除」串行化在同一事务内，绝不反向获取其他锁。";

    /// <summary>无身份 / 非法身份的拒绝文案</summary>
    public const string UnauthorizedText = "请先登录后再访问散货结算单";

    /// <summary>账号已删除的拒绝文案</summary>
    public const string UserDeletedText = "登录账号不存在或已删除，禁止访问散货结算单";

    /// <summary>账号已禁用的拒绝文案</summary>
    public const string UserDisabledText = "登录账号已禁用，禁止访问散货结算单（fail closed）";

    /// <summary>无既有菜单授权的拒绝文案</summary>
    public const string MenuDeniedText =
        "当前账号没有「散货结算单」（bulk-settlement）模块授权：拒绝访问散货结算单（fail closed，不返回 / 不修改任何结算数据）";

    /// <summary>结算客户越范围的拒绝文案（fail closed，不泄露范围外单据）</summary>
    public const string OutOfScopeText =
        "当前账号的客户数据范围不包含该散货结算单的客户：拒绝操作（fail closed，不泄露范围外单据）";

    /// <summary>结算客户缺失 / 无权威归属的拒绝文案（受限账号 fail closed）</summary>
    public const string UnlinkedCustomerText =
        "散货结算单没有可判定的权威客户归属（客户字段缺失）：受限账号拒绝访问（fail closed，不泄露无主单据）";

    // ==================== 1. 金额纯校验（既有存储精度，不换算 / 不聚合 / 不施加合计公式） ====================

    /// <summary>
    /// 结算总金额校验：按既有存储精度（<see cref="AmountDecimals"/> 位小数，0.5 进位）取整后必须大于 0；返回取整后的值。
    /// </summary>
    public static decimal NormalizeTotalAmount(decimal totalAmount)
    {
        var rounded = Math.Round(totalAmount, AmountDecimals, MidpointRounding.AwayFromZero);
        if (rounded <= 0m)
            throw BusinessException.InvalidParameter(
                $"结算总金额必须大于 0：当前值 {totalAmount} 按既有存储精度（{AmountDecimals} 位小数）取整后为 {rounded}");
        return rounded;
    }

    /// <summary>费用校验：按既有存储精度取整后不得为负数；返回取整后的值。</summary>
    public static decimal NormalizeCost(decimal cost, string label)
    {
        var rounded = Math.Round(cost, AmountDecimals, MidpointRounding.AwayFromZero);
        if (rounded < 0m)
            throw BusinessException.InvalidParameter($"{label}不能为负数：当前值 {cost}（按既有存储精度取整后为 {rounded}）");
        return rounded;
    }

    /// <summary>
    /// 一次性校验散货结算单两个金额字段：总金额 &gt; 0，海运费 &gt;= 0，全部按既有存储精度取整。
    /// <para>明确<b>不</b>校验 <c>total == freight</c>：散货结算单没有币种字段，也没有既有的合计约束，
    /// 本护栏不发明任何计算公式。</para>
    /// </summary>
    public static (decimal TotalAmount, decimal FreightCost) NormalizeAmounts(
        decimal totalAmount, decimal freightCost)
        => (NormalizeTotalAmount(totalAmount), NormalizeCost(freightCost, "海运费"));

    // ==================== 2. 身份 / 菜单 / 客户数据范围（fail closed） ====================

    /// <summary>
    /// 校验当前账号的<b>实时启用身份</b>与散货结算单（<c>bulk-settlement</c>）菜单授权，返回本次请求的客户数据范围。
    /// <list type="bullet">
    /// <item>身份缺失 / 非法 → <see cref="ErrorCodes.Unauthorized"/>；</item>
    /// <item>账号不存在或已删除 → <see cref="ErrorCodes.Unauthorized"/>；</item>
    /// <item>账号已禁用 → <see cref="ErrorCodes.Forbidden"/>；</item>
    /// <item>非特权账号无既有菜单授权 → <see cref="ErrorCodes.Forbidden"/>。</item>
    /// </list>
    /// 每次调用都重新查询（无缓存），因此账号停用或授权撤销后下一次请求立即收敛；特权账号保留历史访问。
    /// </summary>
    public static async Task<SalespersonDataScope> EnsureMenuAuthorizedAsync(
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

        var scope = await SalespersonDataScopeService.ResolveAsync(db, userId.Value);

        // 特权账号（超级管理员 / 系统内置角色 / 显式特权角色）保留既有全部访问；普通账号必须显式具备菜单授权。
        if (!scope.IsPrivileged)
        {
            var menuCodes = await CustomerReceivableReconciliationService
                .LoadAuthorizedMenuCodesAsync(db, userId.Value);
            if (!menuCodes.Contains(RequiredMenuCode, StringComparer.OrdinalIgnoreCase))
                throw new BusinessException(MenuDeniedText, ErrorCodes.Forbidden);
        }

        return scope;
    }

    /// <summary>客户数据范围硬边界：无权威归属（&lt;= 0）与越范围一律 fail closed（不泄露存在性）。</summary>
    public static void EnsureCustomerInScope(SalespersonDataScope scope, long customerId)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (customerId <= 0)
            throw new BusinessException(UnlinkedCustomerText, ErrorCodes.Forbidden);
        if (!scope.AllowsCustomer(customerId))
            throw new BusinessException(OutOfScopeText, ErrorCodes.Forbidden);
    }

    /// <summary>三重授权：实时启用身份 → 既有散货结算单菜单 → 客户数据范围（越界 fail closed）。</summary>
    public static async Task EnsureAuthorizedAsync(
        IErpDbContext db, long? userId, long customerId, CancellationToken ct = default)
    {
        var scope = await EnsureMenuAuthorizedAsync(db, userId, ct);
        EnsureCustomerInScope(scope, customerId);
    }


    /// <summary>
    /// 按 Id 精确解析结算客户：必须真实存在、未删除、启用，否则按不存在 / 规则冲突拒绝（绝不按文本猜测客户）。
    /// </summary>
    public static async Task<BaseCustomer> EnsureCustomerAvailableAsync(
        IErpDbContext db, long customerId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (customerId <= 0)
            throw BusinessException.InvalidParameter("散货结算单客户 Id 必须为正整数");

        var customer = await db.BaseCustomers.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == customerId, ct);
        if (customer is null || customer.IsDeleted)
            throw BusinessException.NotFound($"客户（Id={customerId}）不存在或已删除，不能用于散货结算单");
        if (customer.Status != ContainerLoadingParticipantRules.ActiveStatus)
            throw BusinessException.RuleConflict($"客户「{customer.CustomerName}」已停用：停用客户不能用于散货结算单");
        return customer;
    }

    /// <summary>
    /// 校验<b>库中已存储</b>散货结算单的权威归属是否落在当前账号范围内（读取 / 详情 / 状态变更 / 删除之前）：
    /// 结算客户必须在范围内，无权威归属（客户字段缺失）对受限账号一律 fail closed；特权账号保留历史访问。
    /// </summary>
    public static void EnsureStoredSettlementScopeAllowedAsync(
        SalespersonDataScope scope, FinanceBulkSettlement settlement)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(settlement);

        EnsureCustomerInScope(scope, settlement.CustomerId);
    }

    // ==================== 3. 行锁（同单并发串行化） ====================

    /// <summary>
    /// 关系型后端（SQL Server）判定：内存库等非关系型提供程序没有行锁语义，锁定与事务等价无操作。
    /// 使用 <c>DatabaseFacade.ProviderName</c>（EF Core 基础 API），不依赖关系型扩展。
    /// </summary>
    public static bool IsRelationalProvider(IErpDbContext db)
        => string.Equals(db.Database.ProviderName, "Microsoft.EntityFrameworkCore.SqlServer",
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 对散货结算单行加更新锁，把同单并发的「结算单生命周期 / 商业改动」串行化在同一事务内。
    /// <para>实现（仅用 EF Core 基础 API）：在调用方事务内对结算单行发出一条「审计时间戳刷新」的 UPDATE，
    /// 从而取得排它行锁（X 锁，持有至事务结束），语义等价于 <c>SELECT ... WITH (UPDLOCK, HOLDLOCK)</c>；
    /// <c>UpdatedAt</c> 是技术审计字段、不是商业字段，因此不构成对结算单商业内容的静默改写。</para>
    /// <para>非关系型提供程序跳过（事务等价无事务）；散货结算单没有上游来源行，因此无跨表锁序。</para>
    /// </summary>
    public static async Task LockSettlementRowAsync(
        IErpDbContext db, long settlementId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (settlementId <= 0) return;
        if (!IsRelationalProvider(db)) return;

        var settlement = await db.FinanceBulkSettlements
            .FirstOrDefaultAsync(s => s.Id == settlementId && !s.IsDeleted, ct);
        if (settlement is null) return;

        settlement.UpdatedAt = DateTime.Now;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw BusinessException.RuleConflict(
                "散货结算单正在被并发修改，本次操作未生效：请刷新后重试（原始结算单与审计均未改变）");
        }
    }
}

