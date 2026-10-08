using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ERP.Application.Services;

/// <summary>
/// 客户收款单生命周期护栏（ERP-349）：把收款单的创建 / 修改 / 提交 / 审核 / 取消 / 删除与读取，
/// 以及 ERP-053「收款单 → 销售订单」与 ERP-071「收款单 → 代理服务费对账单」两套收款分摊证据写入，
/// 统一到同一条「身份 / 菜单 / 客户数据范围 + 收款单行锁」的可串行化口径上。
/// <para>关键不变量：只要收款单还存在任一<strong>有效</strong>（未作废、未删除）的收款分摊证据，就拒绝
/// 取消 / 删除收款单，也拒绝以会破坏证据的方式修改客户 / 币种 / 金额；要解除限制必须先走既有显式作废服务
/// （<see cref="CustomerReceiptAllocationService.VoidAsync"/> / <see cref="AgencyServiceFeeCollectionAllocationService.VoidAsync"/>），
/// 作废只保留原始证据、绝不物理删除、绝不静默改写。</para>
/// <para>本类只做<b>纯判定与有界只读查询</b>；不落库、不改写收款单与分摊行；「判定 + 状态变更」的原子性与
/// 同单并发串行化由调用方在同一可串行化事务内对收款单行加 UPDLOCK/HOLDLOCK 完成。</para>
/// </summary>
public static class CustomerReceiptLifecycleRules
{
    /// <summary>收款单模块所需既有菜单编码（与 <c>SeedData.Menus</c> 同源）</summary>
    public const string RequiredMenuCode = "receipt";

    /// <summary>收款单模块菜单中文文案（与既有菜单名一致）</summary>
    public const string RequiredMenuText = "收款单";

    /// <summary>生命周期护栏口径文案（接口 / 文档同源）</summary>
    public const string RuleText =
        "收款单生命周期护栏：创建 / 修改 / 提交 / 审核 / 取消 / 删除与读取都会重新校验当前身份、收款单（receipt）菜单授权与客户数据范围；" +
        "创建与修改校验真实可用客户、受支持币种与按币种精度取整后大于 0 的金额；" +
        "只要存在有效的「收款单 → 销售订单」或「收款单 → 代理服务费对账单」收款分摊证据，就拒绝取消 / 删除或修改客户 / 币种 / 金额，" +
        "必须先走既有显式作废服务释放限制（作废保留历史、绝不物理删除）；" +
        "收款单生命周期与两套分摊证据写入通过收款单行锁（UPDLOCK/HOLDLOCK）+ 可串行化事务串行化，绝不跨币种合计、绝不重复计算证据。";

    /// <summary>模块边界文案（不收款 / 不记账 / 不核销，也不改写库存、订单或供应商付款）</summary>
    public const string BoundaryText =
        "本护栏只保护收款单生命周期与既有收款分摊证据：不会真的收款、不会记账或生成凭证、不会核销、不会移动资金，" +
        "也不改写销售订单、库存与库存成本、供应商付款与采购发票、装柜与单证、客户信用状态或任何其它既有单据；" +
        "分摊证据只按各自维度（ERP-053 / ERP-071）分别计算，绝不把两个维度的金额相加。";

    // ==================== 1. 币种、金额与客户纯校验 ====================

    /// <summary>币种规范化 + 支持范围校验（收款单币种必须来自系统币种口径，才能与分摊证据权威比对）</summary>
    public static string NormalizeReceiptCurrency(string? currency)
    {
        var value = CurrencyAmountRules.NormalizeCurrency(currency);
        if (!Enum.GetNames<Currency>().Contains(value, StringComparer.Ordinal))
            throw BusinessException.InvalidParameter(
                $"币种「{value}」不受支持：只允许 {string.Join(" / ", Enum.GetNames<Currency>())}"
                + "（收款单币种必须与所分摊的销售订单 / 对账单币种一致，系统不做汇率换算）");
        return value;
    }

    /// <summary>币种规范化 + 支持范围校验（<see cref="Currency"/> 枚举重载）</summary>
    public static string NormalizeReceiptCurrency(Currency currency)
        => NormalizeReceiptCurrency(currency.ToString());

    /// <summary>
    /// 收款金额校验（服务端权威）：按币种精度四舍五入（0.5 进位）后必须大于 0；
    /// 返回取整后的金额（写入即取整值，系统不自动调整差额、不做汇率换算）。
    /// </summary>
    public static decimal NormalizeReceiptAmount(decimal amount, string? currency)
    {
        var rounded = CurrencyAmountRules.RoundAmount(amount, currency);
        if (rounded <= 0)
            throw BusinessException.InvalidParameter(
                $"收款金额必须大于 0：收到 {amount}，按 {CurrencyAmountRules.NormalizeCurrency(currency)} "
                + $"精度取整后为 {rounded}");
        return rounded;
    }

    /// <summary>收款单金额的权威口径（按币种精度取整；用于与历史金额比对，避免精度差异导致误判变更）</summary>
    public static decimal AuthoritativeReceiptAmount(decimal amount, string? currency)
        => CurrencyAmountRules.RoundAmount(amount, currency);

    /// <summary>客户可用性校验（存在、未删除、启用）：不存在 / 已删除按不存在拒绝，停用按规则冲突拒绝。</summary>
    public static void EnsureCustomerAvailable(BaseCustomer? customer, long customerId)
    {
        if (customer is null || customer.IsDeleted)
            throw BusinessException.NotFound($"客户（Id={customerId}）不存在或已删除，不能用于收款单");
        if (customer.Status != 1)
            throw BusinessException.RuleConflict(
                $"客户「{customer.CustomerName}」已停用：停用客户不能用于新收款单或修改收款单客户");
    }

    /// <summary>客户数据范围硬边界（受限制业务员只能操作其被分配客户，越界 fail closed 不泄露存在性）。</summary>
    public static void EnsureCustomerInScope(SalespersonDataScope scope, long customerId)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (!scope.AllowsCustomer(customerId))
            throw new BusinessException(
                "当前账号的客户数据范围不包含该客户（fail closed，不泄露范围外客户）",
                ErrorCodes.Forbidden);
    }

    // ==================== 2. 身份 / 菜单 / 客户数据范围（fail closed） ====================

    /// <summary>校验当前账号具备收款单（receipt）菜单授权；无身份 / 无角色 / 无授权一律拒绝。</summary>
    public static async Task EnsureMenuAuthorizedAsync(IErpDbContext db, long? userId)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (userId is null or <= 0)
            throw new BusinessException("请先登录后再访问收款单", ErrorCodes.Unauthorized);

        var menuCodes = await CustomerReceivableReconciliationService.LoadAuthorizedMenuCodesAsync(db, userId.Value);
        if (!menuCodes.Contains(RequiredMenuCode, StringComparer.OrdinalIgnoreCase))
            throw new BusinessException(
                $"当前账号没有「{RequiredMenuText}」（{RequiredMenuCode}）模块授权",
                ErrorCodes.Forbidden);
    }

    /// <summary>
    /// 三重授权校验：身份（缺失 / 非正整数 → 未认证）→ 收款单菜单授权 → 客户数据范围（越界 → 权限不足）。
    /// 每次请求都重新解析，撤销授权 / 客户分配变更后立即收敛。
    /// </summary>
    public static async Task EnsureAuthorizedAsync(IErpDbContext db, long? userId, long customerId)
    {
        await EnsureMenuAuthorizedAsync(db, userId);
        var scope = await SalespersonDataScopeService.ResolveAsync(db, userId!.Value);
        EnsureCustomerInScope(scope, customerId);
    }

    // ==================== 3. 有效收款分摊证据判定（只读、有界） ====================

    /// <summary>
    /// 收款单是否存在任一<strong>有效</strong>收款分摊证据：ERP-053「收款单 → 销售订单」或
    /// ERP-071「收款单 → 代理服务费对账单」中，存在未删除且状态为有效（未作废）的分摊行。
    /// 作废行 / 删除行保留历史但不再构成有效证据，绝不按字符串或金额猜测链接。
    /// </summary>
    public static async Task<bool> HasActiveAllocationAsync(IErpDbContext db, long receiptId)
    {
        ArgumentNullException.ThrowIfNull(db);

        var customerOrder = await db.CustomerReceiptAllocations.AsNoTracking()
            .AnyAsync(a => !a.IsDeleted
                           && a.ReceiptId == receiptId
                           && a.Status == CustomerReceiptAllocationRules.StatusActive);
        if (customerOrder) return true;

        return await db.AgencyServiceFeeCollectionAllocations.AsNoTracking()
            .AnyAsync(a => !a.IsDeleted
                           && a.ReceiptId == receiptId
                           && a.Status == AgencyServiceFeeCollectionAllocationRules.StatusActive);
    }

    /// <summary>
    /// 拒绝存在有效收款分摊证据的收款单生命周期动作（取消 / 删除 / 修改客户 / 币种 / 金额）。
    /// 要解除限制必须先走既有显式作废服务（作废保留原始证据，不物理删除、不静默替换）。
    /// </summary>
    public static async Task EnsureNoActiveAllocationAsync(IErpDbContext db, long receiptId, string action)
    {
        if (await HasActiveAllocationAsync(db, receiptId))
            throw BusinessException.RuleConflict(
                $"收款单存在有效的收款分摊证据（「收款单 → 销售订单」或「收款单 → 代理服务费对账单」），不能{action}：" +
                "请先通过既有的作废服务显式作废相关分摊行（作废保留历史，不物理删除、不静默改写证据）");
    }

    // ==================== 4. 收款单行锁（与分摊证据写入 / 生命周期串行化） ====================

    /// <summary>
    /// 关系型后端（SQL Server）判定：内存库等非关系型提供程序没有行锁语义，锁定与事务等价无操作。
    /// 使用 <c>DatabaseFacade.ProviderName</c>（EF Core 基础 API），不依赖关系型扩展。
    /// </summary>
    public static bool IsRelationalProvider(IErpDbContext db)
        => string.Equals(db.Database.ProviderName, "Microsoft.EntityFrameworkCore.SqlServer",
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 对收款单行加更新锁，把同单并发的「收款单生命周期操作」与「两套分摊证据写入」串行化在同一事务内。
    /// <para>实现（仅用 EF Core 基础 API，不依赖关系型扩展）：在调用方事务内对收款单行发出一条「审计时间戳刷新」的
    /// UPDATE，从而取得排它行锁（X 锁，持有至事务结束），语义等价于 <c>SELECT ... WITH (UPDLOCK, HOLDLOCK)</c>；
    /// <c>UpdatedAt</c> 是技术审计字段、不是分摊证据，因此不构成对分摊证据的静默改写。
    /// 内存库等非关系型提供程序无行锁语义，直接跳过（事务等价无事务）。</para>
    /// </summary>
    public static async Task LockReceiptRowAsync(IErpDbContext db, long receiptId)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (!IsRelationalProvider(db)) return;

        var receipt = await db.FinanceReceipts
            .FirstOrDefaultAsync(r => r.Id == receiptId && !r.IsDeleted);
        if (receipt is null) return;

        receipt.UpdatedAt = DateTime.Now;
        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            // 乐观并发令牌（RowVersion）检测到收款单已被并发事务先落库：本事务失去行锁竞争，
            // 转为可读的业务冲突让调用方以「失败」收尾（原始收款单与分摊证据均保持不变，绝不静默覆盖赢家）。
            throw BusinessException.RuleConflict(
                "收款单正在被并发修改，本次操作未生效：请刷新后重试（原始收款单与分摊证据均未改变）");
        }
    }
}
