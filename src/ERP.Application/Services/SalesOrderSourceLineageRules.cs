using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace ERP.Application.Services;

/// <summary>
/// 销售订单（<c>api/sales-orders</c>）**普通表单保存**（新增 / 修改 / 提交 / 审核）的来源血缘护栏（ERP-401）。
/// <para>背景：报价单 / 形式发票 PI → 销售订单的「带入预填 + 直接生成」由 ERP-010 / ERP-398 / ERP-399 / ERP-400 保护，
/// 但普通表单保存（新增 / 修改）此前直接照抄调用方提交的 <c>SourcePiId/No</c>、<c>SourceQuotationId/No</c>，
/// 于是「预填后手工保存」可以绕过来源资格 / 客户范围 / 唯一目标护栏，或伪造 / 张冠李戴地写死一张并不属于本客户、
/// 已删除、已作废或已转换的来源。本类把这些口径统一抽到 Application 层。</para>
/// <list type="number">
/// <item><b>权威解析（绝不按单号推断）</b>：显式来源 <c>Id</c> 必须精确解析到「存在且未删除」的来源行；
/// 来源单号一律按来源行**规范化**（<c>SourcePiNo</c> / <c>SourceQuotationNo</c>，长度按销售订单列宽截断），
/// 调用方提交的自由文本单号只是**非权威文本**，绝不因为「单号看起来像」就建立链接。
/// PI 的报价单祖先（<c>ProformaInvoice.QuotationId</c>）一并规范化，保证「报价单 → PI → 销售订单」追溯链不断裂；
/// 显式报价单与 PI 的权威祖先不一致 = 冲突来源对，原子拒绝。</item>
/// <item><b>实时授权与客户范围</b>：显式链接来源的保存先复核**实时启用身份**（缺失 / 非法按未认证拒绝，
/// 账号不存在 / 已删除按未认证拒绝，禁用按权限不足拒绝）、既有「销售订单」（<c>sales-order</c>）菜单授权
/// （非特权账号必须显式具备且已映射业务员）与**权威客户数据范围**（ERP-097 唯一口径）；
/// 目标客户与来源客户**都必须**落在此次请求的实时范围内，来源客户还必须等于目标订单客户 ——
/// 绝不因来源可见而授予目标授权，也绝不跨客户共享来源。</item>
/// <item><b>既有转换资格</b>：解析成功后按**既有转换资格**（<see cref="ProformaInvoiceMutationRules.EnsureManualOrderLinkEligible"/> /
/// <see cref="QuotationMutationRules.EnsureManualOrderLinkEligible"/>）复核来源状态（已作废 / 未审核 / 无有效明细 /
/// 已完成 / 已转 PI 一律拒绝），并复核**来源唯一目标**（<see cref="SalesOrderConversion.FindOtherTargetAsync"/>）——
/// 与「带入预填 + 直接生成」完全同口径，不新增任何金额 / 单价相等性要求。</item>
/// <item><b>确定性锁序与原子事务</b>：显式链接来源的保存恒定按
/// 「报价单来源行锁 → PI 来源行锁 → 销售订单目标行锁」取得排它行锁（与 ERP-395 / ERP-399 / ERP-400 同源，
/// 仅用 EF Core 基础 API 的「审计时间戳刷新」<c>UPDATE</c>，语义等价 <c>UPDLOCK, HOLDLOCK</c>），
/// 且**先于**单据号预约与任何写入；锁内**重新读取**持久化来源 / 目标状态后才决定是否放行，
/// 任一步失败整体回滚（状态、字段、明细、已预约单号与新建订单一起回滚）。</item>
/// <item><b>历史来源不被静默改写</b>：修改时若请求未给出任何来源 <c>Id</c>，**保留**历史上已登记的来源
/// （绝不静默清除 / 清空）；显式改绑（请求给出与历史不同的来源）必须完整实时复核，且目标订单已存在下游证据
/// （变更申请 / 销售出库）时冻结拒绝；显式来源 <c>Id</c> 全部无法解析时按**显式历史值**原样保留，
/// 既不当作实时链接（不参与资格 / 授权 / 唯一目标 / 加锁），也绝不改写或清除调用方提交的单号文本。</item>
/// </list>
/// <para><b>边界</b>：不新增表 / 列 / 索引 / 菜单 / 角色 / 用户授权，不把空身份当作匿名或管理员，也不伪造任何授权；
/// 不改写来源报价单 / PI / 销售订单明细与金额口径（<c>SalesOrderAmountRules</c> 唯一权威），不做财务 / 库存过账；
/// 行锁只刷新来源行的技术审计时间戳 <c>UpdatedAt</c>（非商业证据，不参与任何金额 / 状态判定）。</para>
/// </summary>
public static class SalesOrderSourceLineageRules
{
    /// <summary>销售订单模块复用的既有菜单编码（与 <c>SeedData.Menus</c> 同源）。</summary>
    public const string SalesOrderMenuCode = "sales-order";

    /// <summary>销售订单模块菜单中文文案（与既有菜单名一致）。</summary>
    public const string SalesOrderMenuText = "销售订单";

    /// <summary>来源报价单行锁语句（引用 ERP-400 同一把报价单来源行锁，跨单据锁序的第一把锁）。</summary>
    public const string QuotationRowLockSql = QuotationMutationRules.QuotationRowLockSql;

    /// <summary>来源 PI 行锁语句（引用 ERP-399 同一把 PI 来源行锁）。</summary>
    public const string ProformaInvoiceRowLockSql = ProformaInvoiceMutationRules.PiRowLockSql;

    /// <summary>目标销售订单行锁语句（与销售订单取消 / 出库审核 / 预装柜流程 / 单证生成共用同一把来源行锁）。</summary>
    public const string SalesOrderRowLockSql = PreLoadingSalesOrderLinkRules.LockSalesOrderRowSql;

    /// <summary>行锁重试次数（乐观并发令牌过期时重读权威行后有界重试；行锁语义 = 阻塞后成功）。</summary>
    public const int RowLockRetryAttempts = ProformaInvoiceMutationRules.RowLockRetryAttempts;

    /// <summary>确定性锁序文案（接口 / 文档同源）：来源行锁恒定先于目标订单行锁，绝不反向获取。</summary>
    public const string LockOrderText =
        "ERP-401 普通销售订单表单保存的确定性锁协议：显式链接来源时恒定按「报价单来源行锁（db_owner.Quotations）→ " +
        "PI 来源行锁（db_owner.ProformaInvoices）→ 销售订单目标行锁（db_owner.SalesOrders）」顺序取得排它行锁" +
        "（WITH (UPDLOCK, HOLDLOCK)，等价实现为审计时间戳刷新的 UPDATE），并在同一原子事务内于加锁后**重新读取**" +
        "持久化来源 / 目标状态，再复核既有转换资格与唯一目标；因此与「带入预填 + 直接生成」的人工保存 / 直接转换竞态" +
        "在同一把来源行锁上串行化，同一来源至多一张目标订单；绝不反向获取下游锁，因此不存在锁环。" +
        "未链接的手工订单不取任何来源锁，保持既有口径。";

    /// <summary>来源缺失 / 已删除 / 无法解析时的拒绝文案（不泄露范围外来源字段）。</summary>
    public const string SourceNotFoundText =
        "来源单据不存在或已删除：拒绝写入来源血缘（绝不按单号推断来源，也不写入无法解析的来源）";

    /// <summary>来源单据已删除时（无效来源）的拒绝文案。</summary>
    public const string SourceDeletedText =
        "来源单据已删除：已删除的来源不可作为实时链接，拒绝写入 / 沿用该来源血缘";

    /// <summary>来源客户与目标订单客户不一致（跨客户共享来源）时的拒绝文案。</summary>
    public const string ForeignCustomerText =
        "来源单据的权威客户与目标销售订单客户不一致：拒绝跨客户链接来源";

    /// <summary>来源对冲突（显式报价单与 PI 的权威报价单祖先不一致）时的拒绝文案。</summary>
    public const string ConflictingPairText =
        "来源冲突：显式来源报价单与来源 PI 记录的权威报价单祖先不一致，拒绝写入冲突的来源对";

    /// <summary>同一来源已存在未删除目标订单时的拒绝文案（既有重复规则唯一化）。</summary>
    public const string DuplicateTargetText = "该来源已生成销售订单，不能重复链接";

    /// <summary>目标订单已存在下游证据（变更申请 / 销售出库）时禁止改绑来源的拒绝文案。</summary>
    public const string RebindFrozenText =
        "该销售订单已进入下游流程（存在变更申请或销售出库）：来源链接已冻结，禁止改绑（保留显式历史，不做反向冲销）";

    /// <summary>显式改绑到无法解析来源时的拒绝文案（绝不静默放弃历史来源）。</summary>
    public const string RebindUnknownSourceText =
        "存在历史来源链接的销售订单只能改绑到可精确解析的来源：拒绝改绑到无法解析的来源 Id";

    /// <summary>受限账号客户范围越界 / 缺失归属时的拒绝文案。</summary>
    public const string CustomerOutOfScopeText =
        "该销售订单客户或来源客户不在当前账号的实时数据范围内：拒绝写入来源血缘";

    /// <summary>无身份 / 非法身份的拒绝文案。</summary>
    public const string UnauthorizedText = "请先登录后再保存销售订单来源链接";

    /// <summary>账号不存在 / 已删除的拒绝文案。</summary>
    public const string UserDeletedText = "登录账号不存在或已删除，禁止保存销售订单来源链接";

    /// <summary>账号已禁用的拒绝文案。</summary>
    public const string UserDisabledText = "登录账号已禁用，禁止保存销售订单来源链接（fail closed）";

    /// <summary>缺少既有「销售订单」菜单授权的拒绝文案。</summary>
    public const string MenuDeniedText =
        "当前账号没有「销售订单」（sales-order）模块授权：拒绝在销售订单上链接来源报价单 / PI"
        + "（fail closed，不写入任何销售订单，也不改写来源单据）";

    /// <summary>受限账号未映射业务员的拒绝文案。</summary>
    public const string UnmappedOperatorText =
        "当前账号未映射为业务员：不能保存销售订单来源链接（fail closed）";

    /// <summary>并发方持续改写来源 / 目标行时对外给出的拒绝文案。</summary>
    public const string ConcurrentMutationText =
        "来源单据或目标销售订单正在被并发修改，本次操作未生效：请刷新后重试（原始证据均未改变）";

    /// <summary>口径说明（接口 / 文档同源）。</summary>
    public const string RuleText =
        "销售订单普通表单保存的显式来源只能是「权威 Id」：精确解析「存在且未删除」的报价单 / PI，按来源行规范化" +
        "来源单号与 PI 报价单祖先，复核实时启用身份 + 既有「销售订单」菜单 + 权威客户范围（目标与来源客户都必须在内且彼此一致）、" +
        "既有转换资格与唯一目标；显式来源 Id 全部无法解析时按「显式历史值」原样保留（不构成实时链接，也不参与资格 / 授权 / 加锁）；" +
        "修改时未给出来源 Id 保留历史来源（绝不静默清除），显式改绑必须完整实时复核且下游已有证据时冻结；" +
        "未链接的手工订单保持完全有效。";

    /// <summary>边界文案（不新增权限 / 表列，不改写来源与历史证据，不做财务 / 库存过账）。</summary>
    public const string BoundaryText =
        "本护栏只保护销售订单普通表单保存（新增 / 修改 / 提交 / 审核）的来源血缘：不新增表 / 列 / 菜单 / 角色 / 用户授权，" +
        "不把空身份当作匿名或管理员；不改写数量 / 单价 / 金额 / 合计 / 定金 / 币种 / 汇率 / 单位等原始商业语义" +
        "（服务端 SalesOrderAmountRules 口径不变），不改写来源报价单 / PI / 客户主数据 / 库存与库存流水 / 财务记录，" +
        "不做任何财务 / 库存过账，不删除任何历史证据；行锁只刷新来源行技术审计时间戳 UpdatedAt（非商业证据）。";

    // ==================== 1. 纯归一化 / 合并 ====================

    /// <summary>来源 Id 归一化：非正整数（含历史脏值）一律按 <c>null</c> 处理（不猜测）。</summary>
    public static long? NormalizeId(long? id) => id is > 0 ? id : null;

    /// <summary>来源单号规范化：去首尾空白并按目标列宽截断（<c>null</c> 归一为空串）。</summary>
    public static string NormalizeNo(string? value, int maxLength)
    {
        var text = (value ?? string.Empty).Trim();
        if (maxLength <= 0 || text.Length <= maxLength) return text;
        return text[..maxLength];
    }

    /// <summary>
    /// 合并历史来源与本次请求：历史上已登记的来源**绝不**被静默清除；请求显式给出与历史不同的来源 = 显式改绑；
    /// 请求未给出任何来源 Id 时继续沿用历史来源（清空尝试被忽略）；两侧都没有则保持显式未链接。
    /// </summary>
    public static SalesOrderSourceChange ResolveChange(long? persistedQuotationId, long? persistedPiId,
        long? requestedQuotationId, long? requestedPiId)
    {
        var persistedQ = NormalizeId(persistedQuotationId);
        var persistedP = NormalizeId(persistedPiId);
        var requestedQ = NormalizeId(requestedQuotationId);
        var requestedP = NormalizeId(requestedPiId);

        if (requestedQ is null && requestedP is null)
        {
            return persistedQ is null && persistedP is null
                ? new SalesOrderSourceChange()
                : new SalesOrderSourceChange
                {
                    QuotationId = persistedQ, PiId = persistedP, IsClearingAttempt = true
                };
        }

        if (persistedQ is null && persistedP is null)
            return new SalesOrderSourceChange
            {
                QuotationId = requestedQ, PiId = requestedP, IsExplicitChange = true
            };

        var unchanged = persistedQ == requestedQ && persistedP == requestedP;
        return new SalesOrderSourceChange
        {
            QuotationId = unchanged ? persistedQ : requestedQ,
            PiId = unchanged ? persistedP : requestedP,
            IsExplicitChange = !unchanged
        };
    }

    // ==================== 2. 权威解析 ====================

    /// <summary>
    /// 按显式来源 Id 精确解析权威来源（只读、有界）：
    /// <list type="number">
    /// <item>PI：按 <c>Id</c> 精确读取来源行（<b>存在但已删除</b> = 无效来源，原子拒绝；<b>完全不存在</b> = 显式历史值）；
    /// 显式给出的报价单必须等于 PI 记录的权威报价单祖先，否则冲突来源对原子拒绝；
    /// PI 的报价单祖先一并规范化（祖先行缺失时按 PI 记录的权威文本留痕，形成「报价单 → PI → 销售订单」完整链）；</item>
    /// <item>报价单（未给 PI 的直接链接）：必须精确解析「存在且未删除」的报价单，否则原子拒绝；</item>
    /// <item>客户：来源客户必须存在且等于目标订单客户，否则跨客户链接原子拒绝（绝不按文本推断归属）；</item>
    /// <item>两侧 <c>Id</c> 都无任何可解析行（都不存在）时按「显式历史值」返回
    /// <see cref="SalesOrderSourceLineage.IsUnresolvedLegacy"/>：原样保留，不构成实时链接。</item>
    /// </list>
    /// </summary>
    public static async Task<SalesOrderSourceLineage> ResolveAsync(IErpDbContext db, SalesOrderSourceChange change,
        long orderCustomerId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(change);

        if (!change.HasSource) return new SalesOrderSourceLineage();

        ProformaInvoice? pi = null;
        Quotation? quotation = null;

        if (change.PiId is long piId)
        {
            // 不做 IsDeleted 过滤：区分「存在但已删除」（无效来源，拒绝）与「完全不存在」（显式历史值，原样保留）。
            var piRow = await db.ProformaInvoices.AsNoTracking()
                .FirstOrDefaultAsync(o => o.Id == piId, ct);
            if (piRow is not null && piRow.IsDeleted) throw BusinessException.RuleConflict(SourceDeletedText);
            pi = piRow;

            // PI 是权威来源：显式给出的报价单必须等于 PI 记录的权威报价单祖先（冲突来源对原子拒绝）。
            if (pi is not null && change.QuotationId is long requestedQuotationId
                && pi.QuotationId != requestedQuotationId)
                throw BusinessException.RuleConflict(ConflictingPairText);
        }
        else if (change.QuotationId is long quotationId)
        {
            // 只有「直接链接报价单」（未给 PI）才要求报价单本身可精确解析。
            var quotationRow = await db.Quotations.AsNoTracking()
                .FirstOrDefaultAsync(o => o.Id == quotationId, ct);
            if (quotationRow is not null && quotationRow.IsDeleted)
                throw BusinessException.RuleConflict(SourceDeletedText);
            quotation = quotationRow;
            if (quotation is null) throw BusinessException.RuleConflict(SourceNotFoundText);
        }

        // PI 记录的报价单祖先：一并规范化，追溯链不因经过 PI 而断掉（绝不按单号推断）。
        // 祖先行不存在时按 PI 记录的权威文本留痕（元数据），不因此拒绝 —— PI 才是本次链接的权威来源。
        if (pi?.QuotationId is long ancestryId && ancestryId > 0)
        {
            quotation ??= await db.Quotations.AsNoTracking()
                .FirstOrDefaultAsync(o => o.Id == ancestryId && !o.IsDeleted, ct);
        }

        if (pi is null && quotation is null)
            return new SalesOrderSourceLineage
            {
                QuotationId = change.QuotationId, PiId = change.PiId, IsUnresolvedLegacy = true
            };

        var sourceCustomerId = pi?.CustomerId ?? quotation?.CustomerId;
        if (sourceCustomerId is null || sourceCustomerId.Value != orderCustomerId)
            throw BusinessException.RuleConflict(ForeignCustomerText);

        // 直接链接报价单（未给 PI）：报价单本身即权威来源（上方已要求精确解析）。
        if (pi is null)
            return new SalesOrderSourceLineage
            {
                QuotationId = quotation!.Id,
                QuotationNo = NormalizeNo(quotation!.QuotationNo, 50),
                SourceCustomerId = sourceCustomerId,
                Quotation = quotation
            };

        return new SalesOrderSourceLineage
        {
            QuotationId = quotation?.Id ?? pi.QuotationId,
            QuotationNo = quotation is not null
                ? NormalizeNo(quotation.QuotationNo, 50)
                : NormalizeNo(pi.QuotationNo, 50),
            PiId = pi.Id,
            PiNo = NormalizeNo(pi.PiNo, 50),
            SourceCustomerId = sourceCustomerId,
            ProformaInvoice = pi,
            Quotation = quotation
        };
    }

    // ==================== 3. 实时身份 / 菜单 / 客户范围 ====================

    /// <summary>
    /// 显式链接来源的保存授权：解析**实时启用身份**（缺失 / 非法 → 未认证；账号不存在 / 已删除 → 未认证；
    /// 禁用 → 权限不足）、既有「销售订单」（<c>sales-order</c>）菜单授权（非特权账号必须显式具备且已映射业务员），
    /// 并复核**权威客户数据范围**：目标订单客户与来源客户都必须落在本次请求的实时范围内，越界 / 缺失归属 fail closed。
    /// 每次调用都重新查询（无缓存），撤销授权后下一次请求立即收敛；菜单授权按其来源模块的既有授权口径复用。
    /// </summary>
    public static async Task EnsureWriteAuthorizedAsync(IErpDbContext db, long? userId,
        SalesOrderSourceLineage lineage, long orderCustomerId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(lineage);

        var scope = lineage.ProformaInvoice is not null
            ? await ProformaInvoiceAuthorizationRules.EnsureSalesOrderLineageAuthorizedAsync(db, userId, ct)
            : await QuotationAuthorizationRules.EnsureSalesOrderLineageAuthorizedAsync(db, userId, ct);

        EnsureCustomerInScope(scope, orderCustomerId);
        EnsureCustomerInScope(scope, lineage.SourceCustomerId);
    }

    /// <summary>受限账号客户范围硬边界：特权账号放行；受限账号缺失归属 / 越界一律 fail closed（写入路径）。</summary>
    public static void EnsureCustomerInScope(SalespersonDataScope scope, long? customerId)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (scope.AllowedCustomerIds is null) return;
        if (customerId is not > 0 || !scope.AllowsCustomer(customerId.Value))
            throw new BusinessException(CustomerOutOfScopeText, ErrorCodes.Forbidden);
    }

    // ==================== 4. 来源资格 / 唯一目标 / 下游冻结 ====================

    /// <summary>
    /// 唯一目标复核（与既有转换守卫同一口径）：按**持久化来源字段**查找除 <paramref name="excludeOrderId"/>
    /// （修改本单时排除自身）之外、未删除的既有销售订单 —— 同一 PI 以 <c>SourcePiId</c> 唯一化，
    /// 同一报价单以「<c>SourceQuotationId</c> 且未绑定 PI」唯一化（PI 来源订单会同时留痕报价单祖先，
    /// 不能因此误判为重复报价单目标）。只按显式 Id 判定，绝不按来源单号等自由文本推断。
    /// </summary>
    public static async Task<SalesOrder?> FindOtherTargetAsync(IErpDbContext db, long? quotationId, long? piId,
        long? excludeOrderId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);

        if (NormalizeId(piId) is long sourcePiId)
        {
            var byPi = await db.SalesOrders.AsNoTracking()
                .FirstOrDefaultAsync(o => o.SourcePiId == sourcePiId && !o.IsDeleted
                                          && (excludeOrderId == null || o.Id != excludeOrderId), ct);
            if (byPi is not null) return byPi;
        }

        if (NormalizeId(quotationId) is long sourceQuotationId)
        {
            var byQuotation = await db.SalesOrders.AsNoTracking()
                .FirstOrDefaultAsync(o => o.SourceQuotationId == sourceQuotationId && o.SourcePiId == null
                                          && !o.IsDeleted
                                          && (excludeOrderId == null || o.Id != excludeOrderId), ct);
            if (byQuotation is not null) return byQuotation;
        }

        return null;
    }

    /// <summary>
    /// 新增链接（或显式改绑）的完整实时复核：先按持久化字段复核**唯一目标**
    /// （<see cref="FindOtherTargetAsync"/>，改绑时排除本单），再按既有转换资格复核来源
    /// （PI 与「PI → 销售订单」、报价单与「报价单 → 销售订单」完全同口径）。任一不满足即原子拒绝。
    /// </summary>
    public static async Task EnsureNewLinkEligibleAsync(IErpDbContext db, SalesOrderSourceLineage lineage,
        long? currentOrderId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(lineage);
        if (lineage.IsUnlinked || lineage.IsUnresolvedLegacy) return;

        var other = await FindOtherTargetAsync(db, lineage.QuotationId, lineage.PiId, currentOrderId, ct);
        if (other is not null)
            throw BusinessException.RuleConflict($"{DuplicateTargetText}：{other.OrderNo}");

        if (lineage.PiId is long piId)
        {
            var pi = await db.ProformaInvoices.AsNoTracking().Include(o => o.Details)
                .FirstOrDefaultAsync(o => o.Id == piId && !o.IsDeleted, ct)
                ?? throw BusinessException.RuleConflict(SourceNotFoundText);
            ProformaInvoiceMutationRules.EnsureManualOrderLinkEligible(pi,
                ProformaInvoiceMutationRules.ActiveDetailCount(pi));
            return;
        }

        var quotationId = lineage.QuotationId!.Value;
        var quotation = await db.Quotations.AsNoTracking().Include(o => o.Details)
            .FirstOrDefaultAsync(o => o.Id == quotationId && !o.IsDeleted, ct)
            ?? throw BusinessException.RuleConflict(SourceNotFoundText);
        var existingPi = await QuotationMutationRules.FindDownstreamPiAsync(db, quotationId, ct);
        QuotationMutationRules.EnsureManualOrderLinkEligible(quotation, existingPi,
            QuotationMutationRules.ActiveDetailCount(quotation));
    }

    /// <summary>
    /// 目标订单已存在的下游证据（未删除的销售订单变更申请 / 显式 <c>SalesOrderId</c> 的销售出库）冻结显式改绑：
    /// 只按既有显式引用字段判定（有界只读），绝不按单号文本 / 金额 / 相似度猜测，也不改写任何下游数据。
    /// </summary>
    public static async Task EnsureRebindNotFrozenAsync(IErpDbContext db, long orderId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (orderId <= 0) return;

        var hasChangeRequest = await db.SalesOrderChangeRequests.AsNoTracking()
            .AnyAsync(r => r.SalesOrderId == orderId && !r.IsDeleted, ct);
        var hasShipment = await db.StockOuts.AsNoTracking()
            .AnyAsync(s => s.SalesOrderId == orderId && !s.IsDeleted, ct);

        if (hasChangeRequest || hasShipment) throw BusinessException.RuleConflict(RebindFrozenText);
    }

    /// <summary>
    /// 提交 / 审核（以及修改时未改动的历史链接）的**持久化来源重查**：来源必须仍可精确解析
    /// （缺失 / 已删除 fail closed），且同一来源不得存在**其它**未删除目标订单。
    /// 不重复做转换资格（来源可能已因本单而置「已完成」，那是合法持久化状态）。
    /// </summary>
    public static async Task EnsurePersistedSourceIntactAsync(IErpDbContext db, SalesOrder order,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(order);

        var change = ResolveChange(order.SourceQuotationId, order.SourcePiId,
            order.SourceQuotationId, order.SourcePiId);
        if (!change.HasSource) return;

        var lineage = await ResolveAsync(db, change, order.CustomerId, ct);
        if (lineage.IsUnresolvedLegacy) return;

        var other = await FindOtherTargetAsync(db, change.QuotationId, change.PiId, order.Id, ct);
        if (other is not null)
            throw BusinessException.RuleConflict($"{DuplicateTargetText}：{other.OrderNo}");
    }

    // ==================== 5. 应用血缘 / 显式历史值 ====================

    /// <summary>把权威解析结果写入销售订单（来源 Id 与**规范化**单号一律以来源行为准，不采信提交文本）。</summary>
    public static void Apply(SalesOrder order, SalesOrderSourceLineage lineage)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(lineage);

        order.SourcePiId = lineage.PiId;
        order.SourcePiNo = lineage.PiNo;
        order.SourceQuotationId = lineage.QuotationId;
        order.SourceQuotationNo = lineage.QuotationNo;
    }

    /// <summary>
    /// 显式来源 Id 全部无法解析时按**显式历史值**原样保留：来源 Id 与调用方提交的单号文本都原样写入
    /// （既不当作实时链接，也绝不静默改写或清除）。未携带任何来源 Id 时保持显式未链接
    /// （单号文本按调用方提交保留，绝不因「看起来像单号」而建立链接）。
    /// </summary>
    public static void ApplyExplicitValues(SalesOrder order, long? quotationId, string? quotationNo,
        long? piId, string? piNo)
    {
        ArgumentNullException.ThrowIfNull(order);

        order.SourceQuotationId = NormalizeId(quotationId);
        order.SourceQuotationNo = NormalizeNo(quotationNo, 50);
        order.SourcePiId = NormalizeId(piId);
        order.SourcePiNo = NormalizeNo(piNo, 50);
    }

    // ==================== 6. 原子事务与确定性行锁 ====================

    /// <summary>关系型后端（SQL Server）判定：内存库等非关系型提供程序没有行锁语义，锁定与事务等价无操作。</summary>
    public static bool IsRelationalProvider(IErpDbContext db)
        => ProformaInvoiceMutationRules.IsRelationalProvider(db);

    /// <summary>
    /// 来源血缘保存使用的原子事务（复用 ERP-395 / ERP-399 / ERP-400 既有口径）：关系型后端开启真实事务，
    /// 失败整体回滚；调用方已开启事务时**绝不嵌套**（返回 <c>null</c> 表示复用外层事务）；
    /// 非关系型提供程序返回 <c>null</c>（等价无事务，声明式校验不变）。
    /// </summary>
    public static Task<IDbContextTransaction?> BeginWriteTransactionAsync(IErpDbContext db,
        CancellationToken ct = default)
        => ProformaInvoiceMutationRules.BeginMutationTransactionAsync(db, ct);

    /// <summary>
    /// 按**确定性锁序**对来源行加排它行锁：先报价单行（若有），后 PI 行（若有）。
    /// 返回 <c>false</c> 表示任一行不存在 / 已被并发删除（调用方按「来源不存在或已删除」原子拒绝）。
    /// 非关系型提供程序等价无操作（存在性仍由锁内权威重读判定）。
    /// </summary>
    public static async Task<bool> LockSourcesAsync(IErpDbContext db, long? quotationId, long? piId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);

        if (NormalizeId(quotationId) is long qid
            && !await QuotationMutationRules.LockQuotationRowAsync(db, qid, ct))
            return false;
        if (NormalizeId(piId) is long pid
            && !await ProformaInvoiceMutationRules.LockPiRowAsync(db, pid, ct))
            return false;
        return true;
    }

    /// <summary>
    /// 对**目标销售订单行**加排它行锁（与销售订单取消 / 出库审核 / 预装柜流程 / 单证生成同一把订单行锁）：
    /// 在调用方事务内发出「审计时间戳刷新」<c>UPDATE</c> 取得排它行锁（持有至事务结束）；
    /// 并发方先提交造成本地乐观令牌过期时重读权威行后有界重试，绝不把纯粹锁等待误报成业务拒绝；
    /// 非关系型提供程序等价无操作。
    /// </summary>
    public static async Task<bool> LockSalesOrderRowAsync(IErpDbContext db, long orderId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (orderId <= 0) return false;
        if (!IsRelationalProvider(db)) return true;

        for (var attempt = 1; attempt <= RowLockRetryAttempts; attempt++)
        {
            var order = await db.SalesOrders.FirstOrDefaultAsync(o => o.Id == orderId && !o.IsDeleted, ct);
            if (order is null) return false;

            order.UpdatedAt = DateTime.Now;
            try
            {
                await db.SaveChangesAsync(ct);
                return true;
            }
            catch (DbUpdateConcurrencyException ex)
            {
                foreach (var entry in ex.Entries)
                {
                    try
                    {
                        await entry.ReloadAsync(ct);
                    }
                    catch (DbUpdateConcurrencyException)
                    {
                        return false; // 目标订单行已被并发事务删除
                    }
                }

                if (attempt == RowLockRetryAttempts)
                    throw BusinessException.RuleConflict(ConcurrentMutationText);
            }
        }

        return false;
    }
}

/// <summary>
/// 权威来源血缘（解析结果，ERP-401）：显式 <see cref="QuotationId"/> / <see cref="PiId"/> 与**规范化**单号，
/// 以及本次解析出的来源行快照。<see cref="IsUnlinked"/> 表示未链接（手工订单，完全有效）；
/// <see cref="IsUnresolvedLegacy"/> 表示调用方给出的来源 Id 无任何可解析行 —— 按显式历史值原样保留，不构成实时链接。
/// </summary>
public sealed record SalesOrderSourceLineage
{
    /// <summary>来源报价单 Id（权威解析后的值）。</summary>
    public long? QuotationId { get; init; }

    /// <summary>来源报价单号（按来源行规范化的权威值；未链接为空串）。</summary>
    public string QuotationNo { get; init; } = string.Empty;

    /// <summary>来源形式发票 PI Id（权威解析后的值）。</summary>
    public long? PiId { get; init; }

    /// <summary>来源 PI 号（按来源行规范化的权威值；未链接为空串）。</summary>
    public string PiNo { get; init; } = string.Empty;

    /// <summary>权威来源客户 Id（PI 优先；未链接为 <c>null</c>）。</summary>
    public long? SourceCustomerId { get; init; }

    /// <summary>本次解析到的来源 PI（只读快照；未链接 / 无法解析为 <c>null</c>）。</summary>
    public ProformaInvoice? ProformaInvoice { get; init; }

    /// <summary>本次解析到的来源报价单（只读快照；未链接 / 无法解析为 <c>null</c>）。</summary>
    public Quotation? Quotation { get; init; }

    /// <summary>是否显式未链接（手工订单；完全有效）。</summary>
    public bool IsUnlinked => PiId is null && QuotationId is null && !IsUnresolvedLegacy;

    /// <summary>是否为「无法解析的显式历史值」（原样保留，不作为实时链接）。</summary>
    public bool IsUnresolvedLegacy { get; init; }
}

/// <summary>
/// 修改时的来源合并结果（ERP-401）：历史（持久化）来源 vs 本次请求给出值的权威合并。
/// <see cref="IsExplicitChange"/> = 与历史不同（新增链接或改绑）；<see cref="IsClearingAttempt"/> = 请求未给出来源
/// 但历史已有（**绝不静默清除**，继续沿用历史来源）。
/// </summary>
public sealed record SalesOrderSourceChange
{
    /// <summary>本次生效的来源报价单 Id（已归一化：非正整数按 <c>null</c> 处理）。</summary>
    public long? QuotationId { get; init; }

    /// <summary>本次生效的来源 PI Id（已归一化：非正整数按 <c>null</c> 处理）。</summary>
    public long? PiId { get; init; }

    /// <summary>是否为显式链接变更（新增链接或改绑到不同来源）。</summary>
    public bool IsExplicitChange { get; init; }

    /// <summary>请求未给出来源但历史已有（清空尝试；按「保留历史来源」处理）。</summary>
    public bool IsClearingAttempt { get; init; }

    /// <summary>本次是否携带任何来源 Id。</summary>
    public bool HasSource => QuotationId is not null || PiId is not null;
}
