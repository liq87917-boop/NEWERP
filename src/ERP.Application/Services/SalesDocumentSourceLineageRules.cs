using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace ERP.Application.Services;

/// <summary>
/// 销售链路「上游来源」单据种类：报价单的来源是<b>询价单</b>，形式发票 PI 的来源是<b>报价单</b>。
/// 枚举值只用于选择权威解析 / 资格 / 行锁路径，不代表任何持久化字段。
/// </summary>
public enum SalesDocumentSourceKind
{
    /// <summary>来源 = 询价单 Inquiry（报价单普通表单保存）。</summary>
    Inquiry = 0,

    /// <summary>来源 = 报价单 Quotation（形式发票 PI 普通表单保存）。</summary>
    Quotation = 1,
}

/// <summary>
/// 销售单据（报价单 Quotation / 形式发票 PI）**普通表单保存**（新增 / 修改 / 提交 / 审核）的上游来源血缘护栏（ERP-403）。
/// <para>背景：询价单 → 报价单 → PI 的「带入预填 + 直接转换」由 ERP-402 / ERP-400 / ERP-399 保护，
/// 但普通表单保存此前直接照抄调用方提交的 <c>Quotation.InquiryId/No</c>、<c>ProformaInvoice.QuotationId/No</c>，
/// 于是「预填后手工保存」可以绕过来源资格 / 客户范围 / 唯一目标护栏，或伪造 / 张冠李戴地写死一张并不属于本客户、
/// 已删除、已作废、未审核或已转换的上游来源。本类把这些口径统一抽到 Application 层。</para>
/// <list type="number">
/// <item><b>权威解析（绝不按单号推断）</b>：显式来源 <c>Id</c> 必须精确解析到「存在且未删除」的来源行；
/// 来源单号一律按来源行**规范化**（报价单 <c>InquiryNo</c> / PI <c>QuotationNo</c>，长度按目标列宽 50 截断），
/// 调用方提交的自由文本单号只是**非权威文本**，绝不因为「单号看起来像」就建立链接。</item>
/// <item><b>实时授权与客户范围</b>：显式链接来源的保存先复核**实时启用身份**（缺失 / 非法按未认证拒绝，
/// 账号不存在 / 已删除按未认证拒绝，禁用按权限不足拒绝）、既有**目标菜单**（报价单 <c>quotation</c> /
/// PI <c>proforma-invoice</c>）与既有**来源菜单**（询价单 <c>inquiry</c> / 报价单 <c>quotation</c>）授权
/// （非特权账号必须显式具备且已映射业务员，ERP-097 唯一口径）与**权威客户数据范围**：目标客户与来源客户
/// **都必须**落在此次请求的实时范围内，来源客户还必须等于目标单据客户 —— 绝不因来源可见而授予目标授权，
/// 也绝不跨客户共享来源。</item>
/// <item><b>既有转换资格</b>：解析成功后按**既有转换资格**（<see cref="InquiryMutationRules.EnsureQuotationConversionEligible"/> /
/// <see cref="QuotationMutationRules.EnsureProformaInvoiceConversionEligible"/>）复核来源状态（已作废 / 未审核 /
/// 无有效明细 / 已完成 / 已有实时目标一律拒绝），并复核**来源唯一目标**（新建或显式改绑时排除本单与其版本链）——
/// 与「带入预填 + 直接转换」完全同口径，不新增任何金额 / 单价相等性要求。</item>
/// <item><b>确定性锁序与原子事务</b>：显式链接来源的保存恒定按「来源行锁 → 目标行锁」取得排它行锁
/// （询价单 <c>db_owner.Inquiries</c> / 报价单 <c>db_owner.Quotations</c>，与 ERP-402 / ERP-400 / ERP-399 共用同一把
/// 来源行锁，仅用 EF Core 基础 API 的「审计时间戳刷新」<c>UPDATE</c>，语义等价 <c>UPDLOCK, HOLDLOCK</c>），
/// 且**先于**单据号预约与任何写入；锁内**重新读取**持久化来源 / 目标状态后才决定是否放行，
/// 任一步失败整体回滚（状态、字段、明细、已预约单号与新建单据一起回滚）。</item>
/// <item><b>历史来源不被静默改写</b>：修改时若请求未给出任何来源 <c>Id</c>，**保留**历史上已登记的来源
/// （绝不静默清除 / 清空）；显式改绑（请求给出与历史不同的来源）必须完整实时复核；显式来源 <c>Id</c> 全部
/// 无法解析时按**显式历史值**原样保留，既不当作实时链接（不参与资格 / 授权 / 唯一目标 / 加锁），
/// 也绝不改写或清除调用方提交的单号文本。PI 修改沿用持久化来源（调用方提交的来源字段不构成链接）。</item>
/// </list>
/// <para><b>边界</b>：不新增表 / 列 / 索引 / 菜单 / 角色 / 用户授权，不把空身份当作匿名或管理员，也不伪造任何授权；
/// 不改写来源询价单 / 报价单 / 客户主数据 / 库存与库存流水 / 财务记录，不改写数量 / 单价 / 金额 / 合计 /
/// 定金 / 币种 / 汇率 / 单位等商业口径（既有 <c>QuotationLineRules</c> / <c>ProformaInvoiceController.Normalize</c>
/// 唯一权威），不做财务 / 库存过账；行锁只刷新来源行的技术审计时间戳 <c>UpdatedAt</c>
/// （非商业证据，不参与任何金额 / 状态判定）。</para>
/// </summary>
public static class SalesDocumentSourceLineageRules
{
    /// <summary>报价单模块复用的既有菜单编码（与 <see cref="QuotationAuthorizationRules.RequiredMenuCode"/> 同源）。</summary>
    public const string QuotationMenuCode = "quotation";

    /// <summary>报价单模块菜单中文文案（与既有菜单名一致）。</summary>
    public const string QuotationMenuText = "报价单";

    /// <summary>询价单模块复用的既有菜单编码（与 <see cref="InquiryAuthorizationRules.RequiredMenuCode"/> 同源）。</summary>
    public const string InquiryMenuCode = "inquiry";

    /// <summary>询价单模块菜单中文文案（与既有菜单名一致）。</summary>
    public const string InquiryMenuText = "询价单";

    /// <summary>形式发票 PI 模块复用的既有菜单编码（与 <see cref="ProformaInvoiceAuthorizationRules.RequiredMenuCode"/> 同源）。</summary>
    public const string ProformaInvoiceMenuCode = "proforma-invoice";

    /// <summary>形式发票 PI 模块菜单中文文案（与既有菜单名一致）。</summary>
    public const string ProformaInvoiceMenuText = "形式发票 PI";

    /// <summary>来源询价单行锁语句（引用 ERP-402 同一把询价单来源行锁，跨单据链的第一把锁）。</summary>
    public const string InquiryRowLockSql = InquiryMutationRules.InquiryRowLockSql;

    /// <summary>来源报价单行锁语句（引用 ERP-400 / ERP-399 同一把报价单来源行锁）。</summary>
    public const string QuotationRowLockSql = QuotationMutationRules.QuotationRowLockSql;

    /// <summary>来源 PI 行锁语句（引用 ERP-399 同一把 PI 来源行锁，供文档 / 契约断言同源）。</summary>
    public const string ProformaInvoiceRowLockSql = ProformaInvoiceMutationRules.PiRowLockSql;

    /// <summary>行锁重试次数（乐观并发令牌过期时重读权威行后有界重试；行锁语义 = 阻塞后成功）。</summary>
    public const int RowLockRetryAttempts = QuotationMutationRules.RowLockRetryAttempts;


    /// <summary>确定性锁序文案（接口 / 文档同源）：来源行锁恒定先于目标行锁，绝不反向获取。</summary>
    public const string LockOrderText =
        "ERP-403 普通销售单据表单保存的确定性锁协议：显式链接来源时恒定按「来源行锁 → 目标单据行锁」顺序取得排它行锁" +
        "（报价单来源询价单 = db_owner.Inquiries、PI 来源报价单 = db_owner.Quotations，均与 ERP-402 / ERP-400 / ERP-399 " +
        "共用同一把来源行锁，WITH (UPDLOCK, HOLDLOCK)，等价实现为审计时间戳刷新的 UPDATE），并在同一原子事务内于加锁后" +
        "**重新读取**持久化来源 / 目标状态，再复核实时授权、既有转换资格与唯一目标；因此「带入预填 + 手工保存」与" +
        "「直接转换」的竞态在同一把来源行锁上串行化，同一来源至多一张有效目标单据；绝不反向获取上游锁，因此不存在锁环。" +
        "未链接的手工单据不取任何来源锁，保持既有口径。";

    /// <summary>来源缺失 / 已删除 / 无法解析时的拒绝文案（不泄露范围外来源字段）。</summary>
    public const string SourceNotFoundText =
        "来源单据不存在或已删除：拒绝写入来源血缘（绝不按单号推断来源，也不写入无法解析的来源）";

    /// <summary>来源单据已删除时（无效来源）的拒绝文案。</summary>
    public const string SourceDeletedText =
        "来源单据已删除：已删除的来源不可作为实时链接，拒绝写入 / 沿用该来源血缘";

    /// <summary>来源客户与目标单据客户不一致（跨客户共享来源）时的拒绝文案。</summary>
    public const string ForeignCustomerText =
        "来源单据的权威客户与目标单据客户不一致：拒绝跨客户链接来源";

    /// <summary>同一来源已存在未删除目标单据时的拒绝文案（既有重复规则唯一化）。</summary>
    public const string DuplicateTargetText = "该来源已生成目标单据，不能重复链接";

    /// <summary>显式改绑到无法解析来源时的拒绝文案（绝不静默放弃历史来源）。</summary>
    public const string RebindUnknownSourceText =
        "存在历史来源链接的单据只能改绑到可精确解析的来源：拒绝改绑到无法解析的来源 Id";

    /// <summary>受限账号客户范围越界 / 缺失归属时的拒绝文案。</summary>
    public const string CustomerOutOfScopeText =
        "该单据客户或来源客户不在当前账号的实时数据范围内：拒绝写入来源血缘";

    /// <summary>无身份 / 非法身份的拒绝文案。</summary>
    public const string UnauthorizedText = "请先登录后再保存单据来源链接";

    /// <summary>账号不存在 / 已删除的拒绝文案。</summary>
    public const string UserDeletedText = "登录账号不存在或已删除，禁止保存单据来源链接";

    /// <summary>账号已禁用的拒绝文案。</summary>
    public const string UserDisabledText = "登录账号已禁用，禁止保存单据来源链接（fail closed）";

    /// <summary>缺少既有来源 / 目标菜单授权的拒绝文案。</summary>
    public const string MenuDeniedText =
        "当前账号缺少既有来源 / 目标模块授权：拒绝在单据上链接上游来源"
        + "（fail closed，不写入任何单据，也不改写来源单据）";

    /// <summary>受限账号未映射业务员的拒绝文案。</summary>
    public const string UnmappedOperatorText =
        "当前账号未映射为业务员：不能保存单据来源链接（fail closed）";

    /// <summary>并发方持续改写来源 / 目标行时对外给出的拒绝文案。</summary>
    public const string ConcurrentMutationText =
        "来源单据或目标单据正在被并发修改，本次操作未生效：请刷新后重试（原始证据均未改变）";

    /// <summary>口径说明（接口 / 文档同源）。</summary>
    public const string RuleText =
        "销售单据普通表单保存的显式来源只能是「权威 Id」：精确解析「存在且未删除」的询价单（报价单来源）/ 报价单（PI 来源），" +
        "按来源行规范化来源单号，复核实时启用身份 + 既有来源 / 目标菜单 + 权威客户范围（目标与来源客户都必须在内且彼此一致）、" +
        "既有转换资格与唯一目标；显式来源 Id 全部无法解析时按「显式历史值」原样保留（不构成实时链接，也不参与资格 / 授权 / 加锁）；" +
        "修改时未给出来源 Id 保留历史来源（绝不静默清除），显式改绑必须完整实时复核；未链接的手工单据保持完全有效。";

    /// <summary>边界文案（不新增权限 / 表列，不改写来源与历史证据，不做财务 / 库存过账）。</summary>
    public const string BoundaryText =
        "本护栏只保护报价单 / PI 普通表单保存（新增 / 修改 / 提交 / 审核）的上游来源血缘：不新增表 / 列 / 菜单 / 角色 / 用户授权，" +
        "不把空身份当作匿名或管理员；不改写数量 / 单价 / 金额 / 合计 / 定金 / 币种 / 汇率 / 单位等原始商业语义" +
        "（QuotationLineRules / ProformaInvoiceController.Normalize 口径不变），不改写来源询价单 / 报价单 / 客户主数据 / " +
        "库存与库存流水 / 财务记录，不做任何财务 / 库存过账，不删除任何历史证据；行锁只刷新来源行技术审计时间戳 UpdatedAt（非商业证据）。";

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
    public static SalesDocumentSourceChange ResolveChange(SalesDocumentSourceKind kind,
        long? persistedSourceId, long? requestedSourceId)
    {
        var persisted = NormalizeId(persistedSourceId);
        var requested = NormalizeId(requestedSourceId);

        if (requested is null)
        {
            return persisted is null
                ? new SalesDocumentSourceChange { Kind = kind }
                : new SalesDocumentSourceChange
                {
                    Kind = kind, SourceId = persisted, IsClearingAttempt = true
                };
        }

        if (persisted is null)
            return new SalesDocumentSourceChange { Kind = kind, SourceId = requested, IsExplicitChange = true };

        var unchanged = persisted == requested;
        return new SalesDocumentSourceChange
        {
            Kind = kind,
            SourceId = unchanged ? persisted : requested,
            IsExplicitChange = !unchanged
        };
    }

    // ==================== 2. 权威解析 ====================

    /// <summary>
    /// 按显式来源 Id 精确解析权威来源（只读、有界）：存在但已删除 = 无效来源（原子拒绝）；
    /// 完全不存在 = 显式历史值（原样保留而不构成实时链接）；来源客户必须存在且等于目标单据客户；
    /// 解析成功时按来源行规范化来源单号（目标列宽 50），绝不采信调用方提交文本。
    /// </summary>
    public static async Task<SalesDocumentSourceLineage> ResolveAsync(IErpDbContext db,
        SalesDocumentSourceChange change, long? targetCustomerId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(change);

        if (!change.HasSource || change.SourceId is not long sourceId)
            return new SalesDocumentSourceLineage { Kind = change.Kind };

        if (change.Kind == SalesDocumentSourceKind.Inquiry)
        {
            var inquiryRow = await db.Inquiries.AsNoTracking().FirstOrDefaultAsync(o => o.Id == sourceId, ct);
            if (inquiryRow is not null && inquiryRow.IsDeleted)
                throw BusinessException.RuleConflict(SourceDeletedText);
            if (inquiryRow is null)
                return new SalesDocumentSourceLineage
                {
                    Kind = change.Kind, SourceId = sourceId, IsUnresolvedLegacy = true
                };

            var sourceCustomerId = (long?)inquiryRow.CustomerId;
            if (sourceCustomerId is null || targetCustomerId is null
                || sourceCustomerId.Value != targetCustomerId.Value)
                throw BusinessException.RuleConflict(ForeignCustomerText);

            return new SalesDocumentSourceLineage
            {
                Kind = change.Kind,
                SourceId = inquiryRow.Id,
                SourceNo = NormalizeNo(inquiryRow.InquiryNo, 50),
                SourceCustomerId = sourceCustomerId,
                Inquiry = inquiryRow
            };
        }

        var quotationRow = await db.Quotations.AsNoTracking().FirstOrDefaultAsync(o => o.Id == sourceId, ct);
        if (quotationRow is not null && quotationRow.IsDeleted)
            throw BusinessException.RuleConflict(SourceDeletedText);
        if (quotationRow is null)
            return new SalesDocumentSourceLineage
            {
                Kind = change.Kind, SourceId = sourceId, IsUnresolvedLegacy = true
            };

        if (quotationRow.CustomerId is null || targetCustomerId is null
            || quotationRow.CustomerId.Value != targetCustomerId.Value)
            throw BusinessException.RuleConflict(ForeignCustomerText);

        return new SalesDocumentSourceLineage
        {
            Kind = change.Kind,
            SourceId = quotationRow.Id,
            SourceNo = NormalizeNo(quotationRow.QuotationNo, 50),
            SourceCustomerId = quotationRow.CustomerId,
            Quotation = quotationRow
        };
    }


    // ==================== 3. 实时身份 / 菜单 / 客户范围 ====================

    /// <summary>
    /// 显式链接来源的保存授权：解析**实时启用身份**（缺失 / 非法 → 未认证；账号不存在 / 已删除 → 未认证；
    /// 禁用 → 权限不足）、既有**来源 / 目标菜单**授权（非特权账号必须显式具备且已映射业务员），
    /// 并复核**权威客户数据范围**：目标单据客户与来源客户都必须落在本次请求的实时范围内，越界 / 缺失归属 fail closed。
    /// 每次调用都重新查询（无缓存），撤销授权后下一次请求立即收敛。
    /// </summary>
    public static async Task EnsureWriteAuthorizedAsync(IErpDbContext db, long? userId,
        SalesDocumentSourceLineage lineage, long? targetCustomerId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(lineage);

        var scope = lineage.Kind == SalesDocumentSourceKind.Inquiry
            ? await QuotationAuthorizationRules.EnsureInquiryLineageAuthorizedAsync(db, userId, ct)
            : await ProformaInvoiceAuthorizationRules.EnsureQuotationLineageAuthorizedAsync(db, userId, ct);

        EnsureCustomerInScope(scope, targetCustomerId);
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

    // ==================== 4. 来源资格 / 唯一目标 ====================

    /// <summary>
    /// 同一来源已存在的**其它**未删除目标：报价单以 <c>InquiryId</c> 唯一化（排除本单所在版本链，
    /// 版本链合法共享同一来源询价单）；PI 以 <c>QuotationId</c> 唯一化（排除本单）。
    /// 只按显式 Id 判定，绝不按来源单号等自由文本推断。
    /// </summary>
    public static async Task<SalesDocumentSourceTarget?> FindOtherTargetAsync(IErpDbContext db,
        SalesDocumentSourceKind kind, long sourceId, long? excludeTargetId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (sourceId <= 0) return null;

        if (kind == SalesDocumentSourceKind.Inquiry)
        {
            long? rootId = null;
            if (excludeTargetId is long currentId)
            {
                rootId = await db.Quotations.AsNoTracking()
                    .Where(q => q.Id == currentId)
                    .Select(q => q.RootQuotationId)
                    .FirstOrDefaultAsync(ct) ?? currentId;
            }

            return await db.Quotations.AsNoTracking()
                .Where(q => q.InquiryId == sourceId && !q.IsDeleted
                            && (excludeTargetId == null || q.Id != excludeTargetId))
                .Where(q => rootId == null || (q.Id != rootId && q.RootQuotationId != rootId))
                .Select(q => new SalesDocumentSourceTarget { Id = q.Id, No = q.QuotationNo })
                .FirstOrDefaultAsync(ct);
        }

        return await db.ProformaInvoices.AsNoTracking()
            .Where(p => p.QuotationId == sourceId && !p.IsDeleted
                        && (excludeTargetId == null || p.Id != excludeTargetId))
            .Select(p => new SalesDocumentSourceTarget { Id = p.Id, No = p.PiNo })
            .FirstOrDefaultAsync(ct);
    }


    /// <summary>
    /// 新增链接（或显式改绑）的完整实时复核：先按持久化字段复核**唯一目标**（改绑时排除本单与其版本链），
    /// 再按既有转换资格复核来源（询价单与「询价单 → 报价单」、报价单与「报价单 → PI」完全同口径）。
    /// 任一不满足即原子拒绝。
    /// </summary>
    public static async Task EnsureNewLinkEligibleAsync(IErpDbContext db, SalesDocumentSourceLineage lineage,
        long? currentTargetId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(lineage);
        if (lineage.IsUnlinked || lineage.IsUnresolvedLegacy || lineage.SourceId is not long sourceId) return;

        if (lineage.Kind == SalesDocumentSourceKind.Inquiry)
        {
            var inquiry = await db.Inquiries.AsNoTracking().Include(o => o.Details)
                .FirstOrDefaultAsync(o => o.Id == sourceId && !o.IsDeleted, ct)
                ?? throw BusinessException.RuleConflict(SourceNotFoundText);

            var other = await FindOtherTargetAsync(db, lineage.Kind, sourceId, currentTargetId, ct);
            if (other is not null)
                throw BusinessException.RuleConflict($"{DuplicateTargetText}：{other.No}");

            InquiryMutationRules.EnsureQuotationConversionEligible(inquiry, null,
                InquiryMutationRules.ActiveDetailCount(inquiry));
            return;
        }

        var quotation = await db.Quotations.AsNoTracking().Include(o => o.Details)
            .FirstOrDefaultAsync(o => o.Id == sourceId && !o.IsDeleted, ct)
            ?? throw BusinessException.RuleConflict(SourceNotFoundText);

        var otherPi = await FindOtherTargetAsync(db, lineage.Kind, sourceId, currentTargetId, ct);
        if (otherPi is not null)
            throw BusinessException.RuleConflict($"{DuplicateTargetText}：{otherPi.No}");

        QuotationMutationRules.EnsureProformaInvoiceConversionEligible(quotation, null,
            QuotationMutationRules.ActiveDetailCount(quotation));
    }

    /// <summary>
    /// 提交 / 审核（以及修改时未改动的历史链接）的**持久化来源重查**：来源必须仍可精确解析
    /// （存在但已删除 → fail closed；完全不存在 → 显式历史值，跳过重查，绝不据此判定为实时链接）。
    /// 对 PI（来源报价单）额外复核「同一报价单不得存在其它未删除 PI」；报价单的来源询价单允许被同版本链共享，
    /// 故不做唯一目标重查（版本链与本单合法共享来源）。不重复做转换资格（来源可能已因本单而置
    /// 「已完成」，那是合法持久化状态）。
    /// </summary>
    public static async Task EnsurePersistedSourceIntactAsync(IErpDbContext db, SalesDocumentSourceKind kind,
        long? persistedSourceId, long? targetCustomerId, long? currentTargetId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);

        var change = ResolveChange(kind, persistedSourceId, persistedSourceId);
        if (!change.HasSource) return;

        var lineage = await ResolveAsync(db, change, targetCustomerId, ct);
        if (lineage.IsUnresolvedLegacy || lineage.SourceId is not long sourceId) return;

        if (kind == SalesDocumentSourceKind.Quotation)
        {
            var other = await FindOtherTargetAsync(db, kind, sourceId, currentTargetId, ct);
            if (other is not null)
                throw BusinessException.RuleConflict($"{DuplicateTargetText}：{other.No}");
        }
    }

    // ==================== 5. 应用血缘 / 显式历史值 ====================

    /// <summary>把权威解析结果写入报价单（来源询价单 Id 与**规范化**单号一律以来源行为准，不采信提交文本）。</summary>
    public static void Apply(Quotation quotation, SalesDocumentSourceLineage lineage)
    {
        ArgumentNullException.ThrowIfNull(quotation);
        ArgumentNullException.ThrowIfNull(lineage);

        if (lineage.Kind == SalesDocumentSourceKind.Inquiry)
        {
            quotation.InquiryId = lineage.SourceId;
            quotation.InquiryNo = lineage.SourceNo;
        }
    }

    /// <summary>把权威解析结果写入 PI（来源报价单 Id 与**规范化**单号一律以来源行为准，不采信提交文本）。</summary>
    public static void Apply(ProformaInvoice pi, SalesDocumentSourceLineage lineage)
    {
        ArgumentNullException.ThrowIfNull(pi);
        ArgumentNullException.ThrowIfNull(lineage);

        if (lineage.Kind == SalesDocumentSourceKind.Quotation)
        {
            pi.QuotationId = lineage.SourceId;
            pi.QuotationNo = lineage.SourceNo;
        }
    }

    /// <summary>
    /// 显式来源 Id 无法解析时按**显式历史值**原样保留：来源 Id 与调用方提交的单号文本都原样写入
    /// （既不当作实时链接，也绝不静默改写或清除）。未携带任何来源 Id 时保持显式未链接（单号文本按调用方提交保留）。
    /// </summary>
    public static void ApplyExplicitValues(Quotation quotation, long? inquiryId, string? inquiryNo)
    {
        ArgumentNullException.ThrowIfNull(quotation);
        quotation.InquiryId = NormalizeId(inquiryId);
        quotation.InquiryNo = NormalizeNo(inquiryNo, 50);
    }

    /// <summary>PI 的显式历史值保留（来源报价单 Id 与单号文本原样写入，不构成实时链接）。</summary>
    public static void ApplyExplicitValues(ProformaInvoice pi, long? quotationId, string? quotationNo)
    {
        ArgumentNullException.ThrowIfNull(pi);
        pi.QuotationId = NormalizeId(quotationId);
        pi.QuotationNo = NormalizeNo(quotationNo, 50);
    }

    // ==================== 6. 原子事务与确定性行锁 ====================

    /// <summary>关系型后端（SQL Server）判定：内存库等非关系型提供程序没有行锁语义，锁定与事务等价无操作。</summary>
    public static bool IsRelationalProvider(IErpDbContext db)
        => QuotationMutationRules.IsRelationalProvider(db);

    /// <summary>
    /// 来源血缘保存使用的原子事务（复用 ERP-399 / ERP-400 / ERP-402 既有口径）：关系型后端开启真实事务，
    /// 失败整体回滚；调用方已开启事务时**绝不嵌套**（返回 <c>null</c> 表示复用外层事务）；
    /// 非关系型提供程序返回 <c>null</c>（等价无事务，声明式校验不变）。
    /// </summary>
    public static Task<IDbContextTransaction?> BeginWriteTransactionAsync(IErpDbContext db,
        CancellationToken ct = default)
        => QuotationMutationRules.BeginMutationTransactionAsync(db, ct);

    /// <summary>
    /// 对**来源行**按种类取得排它行锁（询价单引用 ERP-402、报价单引用 ERP-400 同一把来源行锁）。
    /// 返回 <c>false</c> 表示来源行不存在 / 已被并发删除（调用方按「来源不存在或已删除」原子拒绝）。
    /// 非关系型提供程序等价无操作（存在性仍由锁内权威重读判定）。
    /// </summary>
    public static async Task<bool> LockSourceRowAsync(IErpDbContext db, SalesDocumentSourceKind kind,
        long? sourceId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (NormalizeId(sourceId) is not long id) return true;

        return kind == SalesDocumentSourceKind.Inquiry
            ? await InquiryMutationRules.LockInquiryRowAsync(db, id, ct)
            : await QuotationMutationRules.LockQuotationRowAsync(db, id, ct);
    }
}

/// <summary>
/// 权威来源血缘（解析结果，ERP-403）：显式来源 Id 与**规范化**单号，以及本次解析出的来源行快照。
/// <see cref="IsUnlinked"/> 表示未链接（手工单据，完全有效）；<see cref="IsUnresolvedLegacy"/> 表示调用方
/// 给出的来源 Id 无任何可解析行 —— 按显式历史值原样保留，不构成实时链接。
/// </summary>
public sealed record SalesDocumentSourceLineage
{
    /// <summary>来源种类（未链接时默认为询价单，仅用于选择解析 / 资格路径）。</summary>
    public SalesDocumentSourceKind Kind { get; init; } = SalesDocumentSourceKind.Inquiry;

    /// <summary>来源行 Id（权威解析后的值；询价单或报价单）。</summary>
    public long? SourceId { get; init; }

    /// <summary>来源单号（按来源行规范化的权威值；未链接为空串）。</summary>
    public string SourceNo { get; init; } = string.Empty;

    /// <summary>权威来源客户 Id（未链接为 <c>null</c>）。</summary>
    public long? SourceCustomerId { get; init; }

    /// <summary>本次解析到的来源询价单（只读快照；未链接 / 无法解析为 <c>null</c>）。</summary>
    public Inquiry? Inquiry { get; init; }

    /// <summary>本次解析到的来源报价单（只读快照；未链接 / 无法解析为 <c>null</c>）。</summary>
    public Quotation? Quotation { get; init; }

    /// <summary>是否显式未链接（手工单据；完全有效）。</summary>
    public bool IsUnlinked => SourceId is null && !IsUnresolvedLegacy;

    /// <summary>是否为「无法解析的显式历史值」（原样保留，不作为实时链接）。</summary>
    public bool IsUnresolvedLegacy { get; init; }
}

/// <summary>
/// 修改时的来源合并结果（ERP-403）：历史（持久化）来源 vs 本次请求给出值的权威合并。
/// <see cref="IsExplicitChange"/> = 与历史不同（新增链接或改绑）；<see cref="IsClearingAttempt"/> = 请求未给出来源
/// 但历史已有（**绝不静默清除**，继续沿用历史来源）。
/// </summary>
public sealed record SalesDocumentSourceChange
{
    /// <summary>来源种类。</summary>
    public SalesDocumentSourceKind Kind { get; init; } = SalesDocumentSourceKind.Inquiry;

    /// <summary>本次生效的来源 Id（已归一化：非正整数按 <c>null</c> 处理）。</summary>
    public long? SourceId { get; init; }

    /// <summary>是否为显式链接变更（新增链接或改绑到不同来源）。</summary>
    public bool IsExplicitChange { get; init; }

    /// <summary>请求未给出来源但历史已有（清空尝试；按「保留历史来源」处理）。</summary>
    public bool IsClearingAttempt { get; init; }

    /// <summary>本次是否携带任何来源 Id。</summary>
    public bool HasSource => SourceId is not null;
}

/// <summary>同一来源的既有目标单据最小投影（Id + 单号，用于唯一目标拒绝文案，不泄露其它字段）。</summary>
public sealed record SalesDocumentSourceTarget
{
    /// <summary>目标单据 Id。</summary>
    public long Id { get; init; }

    /// <summary>目标单据单号（用于可读拒绝文案）。</summary>
    public string No { get; init; } = string.Empty;
}
