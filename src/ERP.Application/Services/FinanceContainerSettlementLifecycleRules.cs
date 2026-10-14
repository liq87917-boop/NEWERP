using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ERP.Application.Services;

/// <summary>
/// 装柜结算单（<see cref="FinanceContainerSettlement"/>）生命周期护栏（ERP-384）：把装柜结算单的
/// 列表 / 详情 / 创建 / 修改 / 提交 / 审核 / 取消 / 删除 / 费用分摊证据读取，统一到同一条
/// 「实时启用身份 / 装柜结算单（<c>container-settlement</c>）菜单 / 当前权威客户数据范围 +
/// 来源装柜清单行锁 + 装柜结算单行锁」的可串行化口径上。
/// <para><b>实时授权先于任何读取 / 计数 / 单据号生成 / 费用证据 / 写入</b>：每一个路由都先重新解析
/// <b>实时身份</b>（缺失 / 非法按未认证拒绝）→ <b>账号状态</b>（不存在 / 已删除按未认证，禁用按权限不足）
/// → 既有「装柜结算单」（<c>container-settlement</c>）菜单授权（非特权账号必须显式具备）→ 权威客户数据范围
/// （复用 ERP-097 <see cref="SalespersonDataScopeService"/>，未映射业务员的受限账号 fail closed）。
/// 每次请求重新查询（无缓存），账号停用或授权撤销后下一次请求立即收敛。</para>
/// <para><b>来源装柜清单归属（复用 ERP-364 口径）</b>：可空 <see cref="FinanceContainerSettlement.LoadingListId"/>
/// 按 Id 精确解析既有、未删除、未取消的装柜清单；没有任何有效参与方时按持久化
/// <see cref="ContainerLoadingList.CustomerId"/> 判定，多参与方时结算客户必须是其中一个有效参与方客户；
/// 参与方客户与显式上游（预装柜单 → 订柜信息）客户全部落在当前账号范围内
/// （<see cref="LoadingListAuthorizationRules.EnsureStoredScopeAllowedAsync"/>）。<c>null</c> 保留历史「未关联来源」语义，
/// 绝不按柜号 / 单号等自由文本猜测来源，也不补链接 / 回填。</para>
/// <para><b>金额语义</b>：装柜结算单没有币种字段，因此金额只按<b>既有存储精度</b>（<c>decimal(18,2)</c>，2 位小数，
/// 0.5 进位）做正负校验：<see cref="FinanceContainerSettlement.TotalAmount"/> 必须大于 0，
/// <see cref="FinanceContainerSettlement.FreightCost"/> / <see cref="FinanceContainerSettlement.OtherCost"/>
/// 不得为负数。绝不发明汇率、绝不跨币种聚合，也不施加 <c>total = sum(costs)</c> 之类的公式。</para>
/// <para><b>标量存储边界（ERP-514）</b>：三个金额列在 EF 模型中<b>未</b>显式 <c>HasPrecision</c>，按 SQL Server 提供程序默认精度
/// 落库为 <c>DECIMAL(18,2)</c>（16 位整数 + 2 位小数）；因此在既有两位小数取整<b>之后</b>，复用既有
/// <see cref="FinancePersistedAmountRules"/> 判定每个标量可在该实际列精度内<b>原样</b>存储，过精度 / 溢出在单号预约与任何
/// 写入之前受控拒绝（绝不静默截断 / 取整）。提交 / 审核在既有装柜结算单（与来源装柜清单）行锁内对<b>已持久化</b>金额做
/// 同口径只读复核（<see cref="EnsurePersistedAmountsConsistent"/>）：只拒绝不可存储 / 精度不一致的值，
/// 绝不取整 / 截断 / 改写历史快照，也不新增任何草稿有效性闸门（取消 / 删除等撤销路径保持既有语义）。</para>
/// <para>本类只做<b>纯判定与有界只读查询</b>；不落库、不改写单据（<see cref="LockSettlementRowAsync"/> 只刷新技术审计
/// 时间戳以取得排它行锁）；「判定 + 写入」的原子性与同单并发串行化由调用方在同一事务内加锁完成。
/// 本护栏不写库存 / 资金 / 会计，不产生任何记账、核销或流水。</para>
/// </summary>
public static class FinanceContainerSettlementLifecycleRules
{
    /// <summary>装柜结算单模块所需既有菜单编码（与 <c>SeedData.Menus</c> 同源）</summary>
    public const string RequiredMenuCode = "container-settlement";

    /// <summary>装柜结算单模块菜单中文文案（与既有菜单名一致）</summary>
    public const string RequiredMenuText = "装柜结算单";

    /// <summary>金额的既有存储精度（<c>decimal(18,2)</c>，EF Core SQL Server 默认），固定 2 位小数、0.5 进位</summary>
    public const int AmountDecimals = 2;

    /// <summary>生命周期护栏口径文案（接口 / 文档同源）</summary>
    public const string RuleText =
        "装柜结算单（列表 / 详情 / 创建 / 修改 / 提交 / 审核 / 取消 / 删除 / 费用分摊证据）在读取任何计数、单据号、来源证据或写入之前，"
        + "都会重新校验实时启用身份（缺失 / 非法 / 已删除按未认证，禁用按权限不足）、既有「装柜结算单」（container-settlement）菜单授权与当前权威客户数据范围；"
        + "可空 LoadingListId 按 Id 精确解析既有、未删除、未取消的装柜清单（复用 ERP-364 参与方 / 上游客户范围），"
        + "结算客户必须是真实启用的客户且是该清单的权威客户（多参与方时须为有效参与方之一），空来源保留历史语义，无权威归属 fail closed；"
        + "结算总金额必须大于 0，海运费 / 其他费用不得为负，且三者按既有存储精度取整后还必须可在实际列精度 DECIMAL(18,2) 内原样存储"
        + "（过精度 / 溢出一律在单号预约与任何写入之前受控拒绝，绝不静默截断 / 取整；不换算、不跨币种聚合、不施加合计公式）；"
        + "提交 / 审核在既有行锁内对已持久化金额做同口径只读复核（不可存储 / 精度不一致即拒绝，绝不改写历史快照），"
        + "取消 / 删除等撤销路径不新增草稿有效性闸门；"
        + "修改 / 提交 / 审核 / 取消 / 删除都在同一事务内先取来源装柜清单行锁、再取装柜结算单行锁，并在锁内重新复核身份 / 菜单 / 客户范围 / 状态 / 金额与来源链接；"
        + "存在未删除且未取消的装柜结算单引用某装柜清单时拒绝取消该装柜清单，直到显式取消结算单释放；失败整体回滚，历史与原始审计保持不变。";

    /// <summary>模块边界文案（不新增权限 / 表列，不写库存 / 资金 / 会计）</summary>
    public const string BoundaryText =
        "本护栏只保护装柜结算单生命周期与本单既有来源装柜清单链接：不新增菜单 / 角色 / 用户授权或表结构，也不把空身份当作管理员；"
        + "不写库存 / 库存流水 / 资金 / 会计凭证，不产生收款、付款、核销、分摊入账或对账结论；"
        + "不改写装柜清单 / 明细 / 参与方 / 订柜跟踪值 / 单证，也不改写费用分摊批次与分摊行证据；"
        + "标量金额存储边界只作用于本单三个金额列，绝不施加到聚合 / 报表合计或只读权威余额比对；"
        + "装柜结算单的商业字段（日期 / 来源 / 客户 / 金额 / 备注）与删除标记只在既有允许的状态流转下变更，绝不物理删除任一历史记录。";

    /// <summary>锁序文案（跨模块统一：先来源装柜清单行、后装柜结算单行）</summary>
    public const string LockOrderText =
        "ERP-384 统一锁序：先来源装柜清单行（UPDLOCK, HOLDLOCK，与装柜清单取消 / 参与方维护共用同一把清单行锁），"
        + "后装柜结算单行（UPDLOCK / 乐观并发），绝不反向获取。";

    /// <summary>无身份 / 非法身份的拒绝文案</summary>
    public const string UnauthorizedText = "请先登录后再访问装柜结算单";

    /// <summary>账号已删除的拒绝文案</summary>
    public const string UserDeletedText = "登录账号不存在或已删除，禁止访问装柜结算单";

    /// <summary>账号已禁用的拒绝文案</summary>
    public const string UserDisabledText = "登录账号已禁用，禁止访问装柜结算单（fail closed）";

    /// <summary>无既有菜单授权的拒绝文案</summary>
    public const string MenuDeniedText =
        "当前账号没有「装柜结算单」（container-settlement）模块授权：拒绝访问装柜结算单（fail closed，不返回 / 不修改任何结算数据）";

    /// <summary>结算客户越范围的拒绝文案（fail closed，不泄露范围外单据）</summary>
    public const string OutOfScopeText =
        "当前账号的客户数据范围不包含该装柜结算单的客户：拒绝操作（fail closed，不泄露范围外单据）";

    /// <summary>结算客户缺失 / 无权威归属的拒绝文案（受限账号 fail closed）</summary>
    public const string UnlinkedCustomerText =
        "装柜结算单没有可判定的权威客户归属（客户字段缺失）：受限账号拒绝访问（fail closed，不泄露无主单据）";

    /// <summary>来源装柜清单无权威客户归属的拒绝文案</summary>
    public const string UnlinkedLoadingListText =
        "装柜清单没有可判定的权威客户归属（无有效参与方且兼容客户字段缺失）：拒绝作为装柜结算单来源（fail closed）";

    /// <summary>费用分摊证据含范围外客户的拒绝文案（fail closed，不泄露共享柜中其他客户）</summary>
    public const string EvidenceOutOfScopeText =
        "费用分摊证据包含当前账号客户数据范围外的客户：拒绝读取（fail closed，不泄露共享柜中其他客户的分摊证据）";

    /// <summary>存在未删除且未取消的装柜结算单引用本装柜清单时的拒绝文案</summary>
    public const string LoadingCancelBlockedText =
        "存在未删除且未取消的装柜结算单引用本装柜清单：请先取消装柜结算单，再取消装柜清单（历史与审计保留）";

    // ==================== 0-b. 已存储来源装柜清单的只读标注文案（ERP-392，与来源候选 / 前端同源） ====================

    /// <summary>历史「未关联来源」的只读标注（原样保留，绝不回填）</summary>
    public const string StoredSourceUnlinkedText =
        "当前未关联来源装柜清单（历史未关联语义原样保留，绝不回填）";

    /// <summary>已关联且来源仍有效（未删除、未取消）的只读标注</summary>
    public const string StoredSourceLinkedText = "已关联来源装柜清单";

    /// <summary>来源装柜清单已取消的只读标注（原链接只读保留，绝不静默清除 / 重绑定）</summary>
    public const string StoredSourceCancelledText =
        "已关联来源装柜清单：来源装柜清单已取消，原链接只读保留";

    /// <summary>来源装柜清单不可用（已删除 / 无法解析）的只读标注（原链接原样保留）</summary>
    public const string StoredSourceUnavailableText =
        "已关联来源装柜清单：来源装柜清单不可用（已删除或无法解析），原链接原样保留";

    // ==================== 1. 金额纯校验（既有存储精度，不换算 / 不聚合 / 不施加合计公式） ====================

    /// <summary>结算总金额标量标签（<see cref="FinanceContainerSettlement.TotalAmount"/>，接口 / 文档同源）</summary>
    public const string TotalAmountLabel = "结算总金额";

    /// <summary>海运费标量标签（<see cref="FinanceContainerSettlement.FreightCost"/>，接口 / 文档同源）</summary>
    public const string FreightCostLabel = "海运费";

    /// <summary>其他费用标量标签（<see cref="FinanceContainerSettlement.OtherCost"/>，接口 / 文档同源）</summary>
    public const string OtherCostLabel = "其他费用";

    /// <summary>
    /// 已持久化金额无法在**实际列精度**内原样存储时的拒绝文案（提交 / 审核锁内只读复核，接口 / 文档同源）：
    /// 过精度（超过 2 位小数）与越界一律受控拒绝，绝不静默截断 / 取整 / 改写历史快照。
    /// </summary>
    public const string PersistedAmountStorageText =
        "装柜结算单已持久化金额必须可在实际列精度 DECIMAL(18,2) 内原样存储（至多 2 位小数且有界）：已拒绝，绝不静默截断或取整";

    /// <summary>
    /// 结算总金额校验：按既有存储精度（<see cref="AmountDecimals"/> 位小数，0.5 进位）取整后必须大于 0；返回取整后的值。
    /// <para>ERP-514：取整之后、<b>单号预约与任何写入之前</b>，还要复用既有标量守卫
    /// <see cref="FinancePersistedAmountRules.EnsureRepresentable"/> 判定可在实际列精度 <c>DECIMAL(18,2)</c> 内原样存储
    /// （过精度 / 溢出受控拒绝，绝不静默截断 / 取整）；本方法只判定<b>单个标量</b>，
    /// 不发明 <c>total = sum(costs)</c> 公式，也不做币种换算。</para>
    /// </summary>
    public static decimal NormalizeTotalAmount(decimal totalAmount)
    {
        var rounded = Math.Round(totalAmount, AmountDecimals, MidpointRounding.AwayFromZero);
        if (rounded <= 0m)
            throw BusinessException.InvalidParameter(
                $"{TotalAmountLabel}必须大于 0：当前值 {totalAmount} 按既有存储精度（{AmountDecimals} 位小数）取整后为 {rounded}");
        return FinancePersistedAmountRules.EnsureRepresentable(rounded, $"{TotalAmountLabel}按既有存储精度取整后");
    }

    /// <summary>
    /// 费用校验：按既有存储精度取整后不得为负数；返回取整后的值。
    /// <para>ERP-514：取整之后、任何写入之前追加同一既有标量存储边界（<c>DECIMAL(18,2)</c>），
    /// 过精度 / 溢出受控拒绝；零费用照常放行。</para>
    /// </summary>
    public static decimal NormalizeCost(decimal cost, string label)
    {
        var rounded = Math.Round(cost, AmountDecimals, MidpointRounding.AwayFromZero);
        if (rounded < 0m)
            throw BusinessException.InvalidParameter($"{label}不能为负数：当前值 {cost}（按既有存储精度取整后为 {rounded}）");
        return FinancePersistedAmountRules.EnsureRepresentable(rounded, $"{label}按既有存储精度取整后");
    }

    /// <summary>
    /// 一次性校验结算单三个金额字段：总金额 &gt; 0，海运费 / 其他费用 &gt;= 0，全部按既有存储精度取整，
    /// 且每个标量取整后都必须可在实际列精度 <c>DECIMAL(18,2)</c> 内原样存储（ERP-514）。
    /// <para>明确<b>不</b>校验 <c>total == freight + other</c>：装柜结算单没有币种字段，也没有既有的合计约束，
    /// 本护栏不发明任何计算公式。</para>
    /// </summary>
    public static (decimal TotalAmount, decimal FreightCost, decimal OtherCost) NormalizeAmounts(
        decimal totalAmount, decimal freightCost, decimal otherCost)
        => (NormalizeTotalAmount(totalAmount), NormalizeCost(freightCost, FreightCostLabel),
            NormalizeCost(otherCost, OtherCostLabel));

    /// <summary>
    /// 提交 / 审核在既有装柜结算单（与来源装柜清单）行锁内对**已持久化**金额做只读一致性复核（ERP-514）：
    /// 三个标量都必须能在实际列精度 <c>DECIMAL(18,2)</c> 内原样存储（至多 2 位小数且有界，复用既有
    /// <see cref="FinancePersistedAmountRules.IsRepresentable"/>），并按既有语义要求
    /// <see cref="TotalAmountLabel"/> &gt; 0、<see cref="FreightCostLabel"/> / <see cref="OtherCostLabel"/> &gt;= 0。
    /// <para>纯判定：绝不取整 / 截断 / 改写历史快照，绝不把精度不一致的存量值静默改写成可存储值；
    /// 也绝不发明 <c>total = sum(costs)</c> 公式、不做币种换算、不施加任何聚合 / 报表合计上限。
    /// 取消 / 删除等撤销路径<b>不调用</b>本方法（不新增草稿有效性闸门）。</para>
    /// </summary>
    public static void EnsurePersistedAmountsConsistent(
        decimal totalAmount, decimal freightCost, decimal otherCost)
    {
        EnsurePersistedAmountStorage(totalAmount, TotalAmountLabel);
        EnsurePersistedAmountStorage(freightCost, FreightCostLabel);
        EnsurePersistedAmountStorage(otherCost, OtherCostLabel);

        if (totalAmount <= 0m)
            throw BusinessException.InvalidParameter(
                $"{TotalAmountLabel}必须大于 0：已持久化值 {totalAmount}（锁内复核，不取整、不改写）");
        if (freightCost < 0m)
            throw BusinessException.InvalidParameter(
                $"{FreightCostLabel}不能为负数：已持久化值 {freightCost}（锁内复核，不取整、不改写）");
        if (otherCost < 0m)
            throw BusinessException.InvalidParameter(
                $"{OtherCostLabel}不能为负数：已持久化值 {otherCost}（锁内复核，不取整、不改写）");
    }

    /// <summary>
    /// 已持久化单标量的实际存储可表示性（只读、共用既有 <see cref="FinancePersistedAmountRules.IsRepresentable"/>）：
    /// 过精度（超过 2 位小数）或越界一律受控拒绝（<c>InvalidParameter</c>），绝不取整 / 截断 / 改写。
    /// </summary>
    private static void EnsurePersistedAmountStorage(decimal amount, string label)
    {
        if (!FinancePersistedAmountRules.IsRepresentable(amount))
            throw BusinessException.InvalidParameter(
                $"{PersistedAmountStorageText}（{label}，当前值 {amount}）");
    }

    // ==================== 2. 身份 / 菜单 / 客户数据范围（fail closed） ====================

    /// <summary>
    /// 校验当前账号的<b>实时启用身份</b>与装柜结算单（<c>container-settlement</c>）菜单授权，返回本次请求的客户数据范围。
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

    /// <summary>三重授权：实时启用身份 → 既有装柜结算单菜单 → 客户数据范围（越界 fail closed）。</summary>
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
            throw BusinessException.InvalidParameter("装柜结算单客户 Id 必须为正整数");

        var customer = await db.BaseCustomers.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == customerId, ct);
        if (customer is null || customer.IsDeleted)
            throw BusinessException.NotFound($"客户（Id={customerId}）不存在或已删除，不能用于装柜结算单");
        if (customer.Status != ContainerLoadingParticipantRules.ActiveStatus)
            throw BusinessException.RuleConflict($"客户「{customer.CustomerName}」已停用：停用客户不能用于装柜结算单");
        return customer;
    }

    // ==================== 3. 来源装柜清单的精确解析（复用 ERP-364 口径） ====================

    /// <summary>
    /// 按 Id 精确解析可空来源装柜清单：<c>null</c> 保留历史「未关联来源」语义；
    /// 提供 Id 时必须为正整数且解析到既有、未删除、未取消的装柜清单（悬空 / 已删除 / 已取消一律 fail closed）。
    /// 绝不按柜号 / 单号等自由文本猜测来源。
    /// </summary>
    public static async Task<ContainerLoadingList?> ResolveEligibleLoadingListAsync(
        IErpDbContext db, long? loadingListId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (loadingListId is null) return null;
        if (loadingListId is not > 0)
            throw BusinessException.InvalidParameter("装柜清单 Id 必须为正整数（或留空表示历史未关联来源）");

        var loadingList = await db.ContainerLoadingLists.AsNoTracking()
            .FirstOrDefaultAsync(l => l.Id == loadingListId.Value && !l.IsDeleted, ct)
            ?? throw BusinessException.NotFound(
                $"装柜清单（Id={loadingListId.Value}）不存在或已删除，不能作为装柜结算单来源");

        if (loadingList.Status == DocumentStatus.Cancelled)
            throw BusinessException.RuleConflict(
                $"装柜清单「{loadingList.LoadingListNo}」已取消，不能作为装柜结算单来源");

        return loadingList;
    }

    /// <summary>
    /// 结算客户必须是来源装柜清单的权威客户：存在有效（启用、未删除）参与方时按参与方逐一判定
    /// （结算客户须为其中之一）；无有效参与方时按持久化兼容客户字段判定，字段非正视为无权威归属 fail closed。
    /// </summary>
    public static async Task EnsureSettlementCustomerMatchesLoadingListAsync(
        IErpDbContext db, ContainerLoadingList loadingList, long customerId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(loadingList);

        var participantCustomerIds = await LoadingListAuthorizationRules
            .LoadActiveParticipantCustomerIdsAsync(db, loadingList.Id, ct);

        if (participantCustomerIds.Count > 0)
        {
            if (!participantCustomerIds.Contains(customerId))
                throw BusinessException.RuleConflict(
                    $"装柜结算单客户（Id={customerId}）不是装柜清单「{loadingList.LoadingListNo}」的有效参与方客户："
                    + "来源归属不一致，拒绝写入");
            return;
        }

        if (loadingList.CustomerId <= 0)
            throw new BusinessException(UnlinkedLoadingListText, ErrorCodes.Forbidden);
        if (loadingList.CustomerId != customerId)
            throw BusinessException.RuleConflict(
                $"装柜结算单客户（Id={customerId}）与装柜清单「{loadingList.LoadingListNo}」的客户"
                + $"（Id={loadingList.CustomerId}）不一致：来源归属不一致，拒绝写入");
    }

    /// <summary>
    /// 解析 + 授权可空来源装柜清单：按 Id 精确解析，再用 ERP-364 口径复核该清单的参与方 / 上游客户范围，
    /// 最后校验结算客户与清单权威客户一致。返回解析到的装柜清单（<c>null</c> 表示历史未关联来源）。
    /// </summary>
    public static async Task<ContainerLoadingList?> ResolveAndAuthorizeLoadingListAsync(
        IErpDbContext db, SalespersonDataScope scope, long? loadingListId, long customerId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var loadingList = await ResolveEligibleLoadingListAsync(db, loadingListId, ct);
        if (loadingList is null) return null;

        await LoadingListAuthorizationRules.EnsureStoredScopeAllowedAsync(db, scope, loadingList, ct);
        await EnsureSettlementCustomerMatchesLoadingListAsync(db, loadingList, customerId, ct);
        return loadingList;
    }

    /// <summary>
    /// 校验<b>库中已存储</b>装柜结算单的权威归属是否落在当前账号范围内（读取 / 详情 / 证据 / 状态变更 / 删除之前）：
    /// 结算客户必须在范围内；若显式关联的装柜清单仍可解析（存在且未删除），其参与方 / 上游客户范围也必须同时满足
    /// （复用 <see cref="LoadingListAuthorizationRules.EnsureStoredScopeAllowedAsync"/>）；已删除的来源清单视为历史，
    /// 以结算单自身客户作为权威归属。特权账号保留历史访问。
    /// </summary>
    public static async Task EnsureStoredSettlementScopeAllowedAsync(
        IErpDbContext db, SalespersonDataScope scope, FinanceContainerSettlement settlement,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(settlement);

        EnsureCustomerInScope(scope, settlement.CustomerId);

        if (settlement.LoadingListId is not > 0 || scope.AllowedCustomerIds is null) return;

        var loadingList = await db.ContainerLoadingLists.AsNoTracking()
            .FirstOrDefaultAsync(l => l.Id == settlement.LoadingListId.Value && !l.IsDeleted, ct);
        if (loadingList is null) return;

        await LoadingListAuthorizationRules.EnsureStoredScopeAllowedAsync(db, scope, loadingList, ct);
    }

    /// <summary>
    /// 费用分摊证据的客户范围护栏：受限账号读取结算单链路上装柜清单的分摊证据之前，必须对该链路上
    /// <b>每一个被分摊行显式引用的客户</b>都有数据范围权限（多客户共享柜 fail closed，不泄露其他客户）。
    /// 特权账号与历史未关联来源（无清单）不做额外限制。
    /// </summary>
    public static async Task EnsureEvidenceCustomersInScopeAsync(
        IErpDbContext db, SalespersonDataScope scope, long? loadingListId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(scope);
        if (scope.AllowedCustomerIds is null) return;
        if (loadingListId is not > 0) return;

        var batchIds = await db.FinanceExpenseAllocationBatches.AsNoTracking()
            .Where(b => b.LoadingListId == loadingListId.Value)
            .Select(b => b.Id)
            .ToListAsync(ct);
        if (batchIds.Count == 0) return;

        var customerIds = await db.FinanceExpenseAllocationLines.AsNoTracking()
            .Where(l => batchIds.Contains(l.BatchId))
            .Select(l => l.CustomerId)
            .Distinct()
            .ToListAsync(ct);

        foreach (var customerId in customerIds)
        {
            if (customerId > 0 && !scope.AllowsCustomer(customerId))
                throw new BusinessException(EvidenceOutOfScopeText, ErrorCodes.Forbidden);
        }
    }

    // ==================== 4. 装柜清单取消前的结算占用护栏 ====================

    /// <summary>
    /// 装柜清单取消护栏：只要仍存在<b>未删除且未取消</b>的装柜结算单以该清单为显式来源，即拒绝取消装柜清单，
    /// 直到显式取消结算单释放（保留历史与审计，绝不物理删除）。调用方必须已持有装柜清单行更新锁，
    /// 以与装柜结算单创建 / 审核串行化。
    /// </summary>
    public static async Task EnsureNoActiveSettlementForLoadingListAsync(
        IErpDbContext db, long loadingListId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (loadingListId <= 0) return;

        var hasActiveSettlement = await db.FinanceContainerSettlements.AsNoTracking()
            .AnyAsync(s => s.LoadingListId == loadingListId
                           && !s.IsDeleted
                           && s.Status != DocumentStatus.Cancelled, ct);
        if (hasActiveSettlement)
            throw BusinessException.RuleConflict(LoadingCancelBlockedText);
    }

    /// <summary>
    /// 合并本次动作需要加锁的来源装柜清单 Id（既有持久化来源 + 请求来源）：去重、只保留正整数，并按 Id 升序返回，
    /// 以保证多个清单行锁的确定性获取顺序（绝不反向获取下游锁）。
    /// </summary>
    public static IReadOnlyList<long> MergeLoadingListLockIds(params long?[] loadingListIds)
    {
        ArgumentNullException.ThrowIfNull(loadingListIds);
        return loadingListIds
            .Where(id => id is > 0)
            .Select(id => id!.Value)
            .Distinct()
            .OrderBy(id => id)
            .ToList();
    }

    // ==================== 5. 行锁（与装柜清单取消 / 参与方维护串行化） ====================

    /// <summary>
    /// 关系型后端（SQL Server）判定：内存库等非关系型提供程序没有行锁语义，锁定与事务等价无操作。
    /// 使用 <c>DatabaseFacade.ProviderName</c>（EF Core 基础 API），不依赖关系型扩展。
    /// </summary>
    public static bool IsRelationalProvider(IErpDbContext db)
        => string.Equals(db.Database.ProviderName, "Microsoft.EntityFrameworkCore.SqlServer",
            StringComparison.OrdinalIgnoreCase);

    /// <summary>行锁重试次数（乐观并发令牌过期时重读权威行后有界重试；行锁语义 = 阻塞后成功）。</summary>
    private const int LockRetryAttempts = 8;

    /// <summary>
    /// 按 Id 升序对来源装柜清单行加更新锁（与装柜清单取消 / 参与方维护共用同一把清单行锁），
    /// 把并发「装柜清单取消」与「装柜结算单创建 / 修改 / 状态变更」串行化在同一事务内。
    /// <para>实现（仅用 EF Core 基础 API，不依赖关系型扩展，内存库可运行）：在调用方事务内对清单行发出
    /// 一条「审计时间戳刷新」的 UPDATE 取得排它行锁（X 锁，持有至事务结束），语义等价于
    /// <c>SELECT ... WITH (UPDLOCK, HOLDLOCK)</c>；装柜清单取消侧使用的 <c>UPDLOCK/HOLDLOCK</c> 与本 X 锁互斥，
    /// 因此两侧必然串行。<c>UpdatedAt</c> 是技术审计字段、不是商业字段，不构成对装柜清单商业内容的静默改写。</para>
    /// <para>锁序固定为「来源装柜清单行 → 装柜结算单行」，绝不反向获取；非关系型提供程序跳过。</para>
    /// </summary>
    public static async Task LockLoadingListRowsAsync(
        IErpDbContext db, IEnumerable<long> loadingListIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (!IsRelationalProvider(db)) return;

        foreach (var loadingListId in loadingListIds.Where(id => id > 0).Distinct().OrderBy(id => id))
        {
            ct.ThrowIfCancellationRequested();
            await LockLoadingListRowAsync(db, loadingListId, ct);
        }
    }

    /// <summary>
    /// 单行行锁 + 审计时间戳刷新：并发方先提交时本地乐观并发令牌（<c>RowVersion</c>）会过期，EF 抛
    /// <see cref="DbUpdateConcurrencyException"/>；行锁语义应为「阻塞后成功」，因此重读权威行（含新令牌）后
    /// 有界重试，绝不把纯粹的锁等待误报成业务拒绝。真实业务冲突仍由调用方的锁内校验判定。
    /// </summary>
    private static async Task LockLoadingListRowAsync(
        IErpDbContext db, long loadingListId, CancellationToken ct)
    {
        for (var attempt = 1; attempt <= LockRetryAttempts; attempt++)
        {
            var loadingList = await db.ContainerLoadingLists
                .FirstOrDefaultAsync(l => l.Id == loadingListId && !l.IsDeleted, ct);
            if (loadingList is null) return;

            loadingList.UpdatedAt = DateTime.Now;
            try
            {
                await db.SaveChangesAsync(ct);
                return;
            }
            catch (DbUpdateConcurrencyException ex)
            {
                foreach (var entry in ex.Entries) await entry.ReloadAsync(ct);
                if (attempt == LockRetryAttempts)
                    throw BusinessException.RuleConflict(
                        "来源装柜清单正在被并发修改，本次操作未生效：请刷新后重试（原始装柜清单与审计均未改变）");
            }
        }
    }

    /// <summary>
    /// 对装柜结算单行加更新锁，把同单并发的「结算单生命周期 / 商业改动」串行化在同一事务内。
    /// <para>实现（仅用 EF Core 基础 API）：在调用方事务内对结算单行发出一条「审计时间戳刷新」的 UPDATE，
    /// 从而取得排它行锁（X 锁，持有至事务结束），语义等价于 <c>SELECT ... WITH (UPDLOCK, HOLDLOCK)</c>；
    /// <c>UpdatedAt</c> 是技术审计字段、不是商业字段，因此不构成对结算单商业内容的静默改写。</para>
    /// <para><b>锁序（ERP-384）</b>：来源装柜清单行必须先于结算单行获取，本方法必须由调用方在同一事务内、
    /// 任何来源装柜清单行锁之后调用，绝不反向获取上游锁。</para>
    /// </summary>
    public static async Task LockSettlementRowAsync(
        IErpDbContext db, long settlementId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (settlementId <= 0) return;
        if (!IsRelationalProvider(db)) return;

        var settlement = await db.FinanceContainerSettlements
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
                "装柜结算单正在被并发修改，本次操作未生效：请刷新后重试（原始结算单与审计均未改变）");
        }
    }

    /// <summary>
    /// 读取结算单当前持久化的来源装柜清单 Id（仅用于按「先清单行、后结算单行」取锁；权威校验一律在锁内重做）。
    /// 未关联来源（null）/ 不存在 / 已删除返回 null，保留历史未关联来源语义。
    /// </summary>
    public static async Task<long?> ReadSourceLoadingListIdAsync(
        IErpDbContext db, long settlementId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (settlementId <= 0) return null;

        return await db.FinanceContainerSettlements.AsNoTracking()
            .Where(s => s.Id == settlementId && !s.IsDeleted)
            .Select(s => s.LoadingListId)
            .FirstOrDefaultAsync(ct);
    }
}
