using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace ERP.Application.Services;

/// <summary>
/// 代理服务费协议证据登记服务（ERP-069）。职责：
/// <list type="number">
/// <item><b>登记 / 修改</b>（<see cref="CreateAsync"/> / <see cref="UpdateAsync"/>）：只允许操作**草稿**；
/// 客户必须存在、未删除且启用，客户编码 / 名称由服务端写成快照，费用条款按显式计费方式逐项校验；</item>
/// <item><b>唯一身份</b>：同一「客户 + 规范化协议号」在**有效（未作废）**记录内唯一，重复请求一律拒绝
/// （不静默合并、不覆盖、不按金额或日期模糊匹配）；作废记录保留可读但不占用身份；</item>
/// <item><b>登记 / 作废</b>（<see cref="RecordAsync"/> / <see cref="VoidAsync"/>）：登记冻结证据并按已认证身份
/// 记录登记人；作废必须填写原因，保留原始条款、客户快照、登记人与时间戳，不物理删除、不静默改写；</item>
/// <item><b>并发（ERP-519）</b>：新增 / 修改 / 登记 / 作废都落在<strong>原子事务</strong>内，
/// 修改 / 登记 / 作废先取<strong>同一把协议行锁</strong>（<see cref="AgencyFeeEvidenceConcurrencyRules"/>），
/// 锁内重读权威状态、客户范围与已持久化条款后才写入；并发登记 / 作废只有唯一赢家，
/// 迟到的草稿修改一律被拒绝，零部分写入、零审计漂移，历史证据与作废留痕不删除、不回填；</item>

/// <item><b>台账读取</b>（<see cref="ListAsync"/> / <see cref="GetAsync"/>）：分页 / 有界、客户一次批量装载，
/// 无逐行数据库查询。</item>
/// </list>
/// <para>费用条款口径（服务端权威）：费率 / 固定金额 / 计费依据说明只来自用户显式提交的值，
/// 本服务<strong>不</strong>从业务员提成设置（系统参数 <c>SalesCommissionRate</c>）、客户 / 供应商主数据比例、
/// 历史订单、自由文本、客户默认值或金额相似度推断、补齐或改写任何商业含义。</para>
/// <para>边界（重要）：本服务只读写 <c>AgencyServiceFeeAgreements</c> 一张表；<strong>不</strong>开发票、不报税、
/// 不记账、不生成凭证 / 收款 / 付款 / 结算单、<strong>不</strong>授权或发起任何付款、不提供法律意见，
/// 也不改写客户主数据（含佣金比例与信用状态）、销售订单（含佣金比例与金额）、装柜与单证、收款单及其引用行、
/// 销项发票证据、库存与库存成本、费用与退税记录；业务员提成报表保持**独立的模型与口径**。</para>
/// </summary>
public static class AgencyServiceFeeAgreementService
{
    // ==================== 1. 登记 / 修改（仅草稿） ====================

    /// <summary>
    /// 新增草稿协议证据：校验协议号 / 客户可用性 / 生效区间 / 币种 / 计费方式与费用条款 / 计费依据说明，
    /// 写入客户快照与规范化协议号，并拒绝重复身份。
    /// <para>ERP-519：校验与插入落在<strong>同一原子事务</strong>内（被拒请求零写入、零身份消耗）；
    /// 并发同身份的登记由既有过滤唯一索引 <c>UX_AgencyServiceFeeAgreements_ActiveIdentity</c> 收敛为
    /// 「恰好一条有效身份」，输家被映射为受控重复错误并整体回滚（绝不泄露数据库细节）。</para>
    /// </summary>
    public static async Task<AgencyServiceFeeAgreementDto> CreateAsync(
        IErpDbContext db, AgencyServiceFeeAgreementSaveDto dto)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(dto);

        // ERP-519：草稿登记（校验 + 插入）落在同一原子事务内。
        await using var transaction = await AgencyFeeEvidenceConcurrencyRules.BeginMutationTransactionAsync(db);
        AgencyServiceFeeAgreement agreement = null!;
        try
        {
            var input = await ValidateAsync(db, dto);
            await EnsureIdentityAvailableAsync(db, input, excludeAgreementId: null);

            agreement = new AgencyServiceFeeAgreement
            {
                AgreementNo = input.AgreementNo,
                NormalizedAgreementNo = input.NormalizedAgreementNo,
                CustomerId = input.Customer.Id,
                CustomerCode = input.Customer.CustomerCode ?? string.Empty,
                CustomerName = input.Customer.CustomerName ?? string.Empty,
                EffectiveFrom = input.EffectiveFrom,
                EffectiveTo = input.EffectiveTo,
                Currency = input.Currency,
                FeeMethod = input.FeeMethod,
                RatePercent = input.RatePercent,
                FixedAmount = input.FixedAmount,
                FeeBasis = input.FeeBasis,
                Status = AgencyServiceFeeAgreementRules.StatusDraft,
                Remark = input.Remark
            };

            db.AgencyServiceFeeAgreements.Add(agreement);
            await AgencyFeeEvidenceConcurrencyRules.SaveEvidenceAsync(db);
            await AgencyFeeEvidenceConcurrencyRules.CommitAsync(transaction);
        }
        catch
        {
            await AgencyFeeEvidenceConcurrencyRules.TryRollbackAsync(transaction, db);
            throw;
        }

        return await MapAsync(db, agreement);
    }

    /// <summary>
    /// 路由级登记草稿协议证据（ERP-439）：<paramref name="scope"/> 非空时，显式提交的客户必须落在
    /// ERP-097 权威客户范围内；越范围客户一律按同一条不披露存在性的错误在**写入之前**拒绝——
    /// 绝不落任何协议行，也绝不消耗任何协议身份。
    /// </summary>
    public static async Task<AgencyServiceFeeAgreementDto> CreateAsync(
        IErpDbContext db, AgencyServiceFeeAgreementSaveDto dto, SalespersonDataScope? scope)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(dto);
        if (scope is not null && dto.CustomerId > 0)
            AgencyServiceFeeAgreementRules.EnsureCustomerInScope(scope, dto.CustomerId);
        return await CreateAsync(db, dto);
    }

    /// <summary>
    /// 修改草稿协议证据：已登记 / 已作废拒绝修改（保留可读）；重复身份拒绝；
    /// 修改不改写任何既有记录，也不触碰登记人 / 登记时间（草稿尚未登记时它们为空）。
    /// <para>ERP-519：在同一原子事务内先取<strong>同一把协议行锁</strong>，锁内重读权威状态后才判定可编辑性，
    /// 因此「并发登记 / 作废」与草稿修改严格串行——已登记 / 已作废的证据绝不会再接收迟到的草稿编辑。</para>
    /// </summary>
    public static async Task<AgencyServiceFeeAgreementDto> UpdateAsync(
        IErpDbContext db, long agreementId, AgencyServiceFeeAgreementSaveDto dto)
        => await UpdateCoreAsync(db, agreementId, dto, scope: null);

    /// <summary>
    /// 路由级修改草稿协议证据（ERP-439）：<paramref name="scope"/> 非空时先按**已存**协议的持久化客户与
    /// 拟提交客户双向收敛——范围外 / 已删除 / 不存在的协议或越范围客户一律拒绝，且拒绝发生在**写库之前**
    /// （不取锁、不刷新任何技术时间戳），绝不改写任何既有协议字段或状态。
    /// <para>ERP-519：范围预检之后，核心写入仍在同一事务 + 同一协议行锁内把范围重新施加到**权威行**
    /// （并发归属变化 / 缓存范围都不被信任），并以受控不披露存在性的错误拒绝。</para>
    /// </summary>
    public static async Task<AgencyServiceFeeAgreementDto> UpdateAsync(
        IErpDbContext db, long agreementId, AgencyServiceFeeAgreementSaveDto dto, SalespersonDataScope? scope)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(dto);
        if (scope is not null)
        {
            if (agreementId > 0)
                await AgencyServiceFeeAgreementRules.EnsureAgreementInScopeAsync(db, scope, agreementId);
            if (dto.CustomerId > 0)
                AgencyServiceFeeAgreementRules.EnsureCustomerInScope(scope, dto.CustomerId);
        }
        return await UpdateCoreAsync(db, agreementId, dto, scope);
    }

    /// <summary>
    /// 修改核心（ERP-519）：同一事务 + 同一协议行锁 → 锁内重读权威状态 / 客户范围 → 校验条款与唯一身份 →
    /// 写入；任一步失败整体回滚并丢弃半成品变更（零部分写入、零审计漂移）。
    /// </summary>
    private static async Task<AgencyServiceFeeAgreementDto> UpdateCoreAsync(
        IErpDbContext db, long agreementId, AgencyServiceFeeAgreementSaveDto dto, SalespersonDataScope? scope)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(dto);
        if (agreementId <= 0) throw BusinessException.InvalidParameter("请选择要修改的代理服务费协议");

        await using var transaction = await AgencyFeeEvidenceConcurrencyRules.BeginMutationTransactionAsync(db);
        AgencyServiceFeeAgreement agreement = null!;
        try
        {
            // ERP-519：先取同一把协议行锁，再按**锁内权威状态**判定可编辑性并把范围重新施加到权威行。
            agreement = await LockAuthoritativeAsync(db, agreementId);
            AgencyServiceFeeAgreementRules.EnsureEditable(agreement.Status, IdentityOf(agreement));
            EnsureAuthoritativeScope(scope, agreement);

            var input = await ValidateAsync(db, dto);
            await EnsureIdentityAvailableAsync(db, input, agreement.Id);
            if (scope is not null)
                AgencyServiceFeeAgreementRules.EnsureCustomerInScope(scope, input.Customer.Id);

            agreement.AgreementNo = input.AgreementNo;
            agreement.NormalizedAgreementNo = input.NormalizedAgreementNo;
            agreement.CustomerId = input.Customer.Id;
            agreement.CustomerCode = input.Customer.CustomerCode ?? string.Empty;
            agreement.CustomerName = input.Customer.CustomerName ?? string.Empty;
            agreement.EffectiveFrom = input.EffectiveFrom;
            agreement.EffectiveTo = input.EffectiveTo;
            agreement.Currency = input.Currency;
            agreement.FeeMethod = input.FeeMethod;
            agreement.RatePercent = input.RatePercent;
            agreement.FixedAmount = input.FixedAmount;
            agreement.FeeBasis = input.FeeBasis;
            agreement.Remark = input.Remark;
            agreement.UpdatedAt = DateTime.Now;

            await AgencyFeeEvidenceConcurrencyRules.SaveEvidenceAsync(db);
            await AgencyFeeEvidenceConcurrencyRules.CommitAsync(transaction);
        }
        catch
        {
            await AgencyFeeEvidenceConcurrencyRules.TryRollbackAsync(transaction, db);
            throw;
        }

        return await MapAsync(db, agreement);
    }

    // ==================== 2. 台账读取（分页 / 有界，批量装载） ====================

    /// <summary>协议证据详情（含客户可用性标注与口径文案；只读）</summary>
    public static async Task<AgencyServiceFeeAgreementDto> GetAsync(IErpDbContext db, long agreementId)
    {
        ArgumentNullException.ThrowIfNull(db);
        var agreement = await LoadAsync(db, agreementId);
        return await MapAsync(db, agreement);
    }

    /// <summary>
    /// 路由级协议证据详情（ERP-439）：<paramref name="scope"/> 非空时按权威客户范围收敛——
    /// 范围外 / 已删除 / 不存在的协议返回同一条不披露存在性的错误
    /// （绝不暴露范围外协议 Id、协议号、客户、金额或费率）；范围先于读取，绝不「先读全量再内存过滤」。
    /// </summary>
    public static async Task<AgencyServiceFeeAgreementDto> GetAsync(
        IErpDbContext db, long agreementId, SalespersonDataScope? scope)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (scope is not null && agreementId > 0)
            await AgencyServiceFeeAgreementRules.EnsureAgreementInScopeAsync(db, scope, agreementId);
        return await GetAsync(db, agreementId);
    }

    /// <summary>
    /// 台账分页查询（只读）：支持客户 / 状态 / 币种 / 计费方式 / 生效起始日期区间 / 关键字过滤；
    /// 默认包含已作废历史（证据保留可读）。页内客户一次批量装载（无逐行数据库查询）。
    /// <para>ERP-439：<paramref name="scope"/> 非空时，权威客户范围在**计数、分页与物化之前**下推到数据库，
    /// 受限账号绝不返回范围外协议或其计数；范围外协议不会通过 Id / 计数旁路泄露。</para>
    /// </summary>
    public static async Task<PagedResult<AgencyServiceFeeAgreementDto>> ListAsync(
        IErpDbContext db, AgencyServiceFeeAgreementQuery query, SalespersonDataScope? scope = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(query);
        query.Normalize();

        var status = AgencyServiceFeeAgreementRules.NormalizeStatusFilter(query.Status);
        var feeMethod = AgencyServiceFeeAgreementRules.NormalizeFeeMethodFilter(query.FeeMethod);
        var currency = string.IsNullOrWhiteSpace(query.Currency)
            ? null
            : AgencyServiceFeeAgreementRules.NormalizeCurrencyStrict(query.Currency);
        var keyword = AgencyServiceFeeAgreementRules.NormalizeKeyword(query.Keyword);

        var source = db.AgencyServiceFeeAgreements.AsNoTracking().Where(x => !x.IsDeleted);
        // ERP-439：权威客户范围在计数 / 分页之前下推（受限账号绝不统计 / 返回范围外协议）。
        if (scope is not null)
            source = SalespersonDataScopeService.FilterByCustomer(source, scope, x => x.CustomerId);
        if (query.CustomerId is not null) source = source.Where(x => x.CustomerId == query.CustomerId.Value);
        if (status is not null) source = source.Where(x => x.Status == status.Value);
        if (feeMethod is not null) source = source.Where(x => x.FeeMethod == feeMethod);
        if (currency is not null) source = source.Where(x => x.Currency == currency);
        if (query.EffectiveFromFrom is not null)
            source = source.Where(x => x.EffectiveFrom >= query.EffectiveFromFrom.Value.Date);
        if (query.EffectiveFromTo is not null)
            source = source.Where(x => x.EffectiveFrom <= query.EffectiveFromTo.Value.Date);

        if (keyword.Length > 0)
        {
            source = source.Where(x => x.AgreementNo.Contains(keyword)
                || x.CustomerName.Contains(keyword)
                || x.CustomerCode.Contains(keyword)
                || x.FeeBasis.Contains(keyword));
        }

        var total = await source.CountAsync();
        var page = await source
            .OrderByDescending(x => x.EffectiveFrom)
            .ThenByDescending(x => x.Id)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .ToListAsync();

        return new PagedResult<AgencyServiceFeeAgreementDto>
        {
            Items = await MapManyAsync(db, page),
            Total = total,
            Page = query.Page,
            PageSize = query.PageSize
        };
    }

    // ==================== 3. 登记 / 作废（证据冻结与保留） ====================

    /// <summary>
    /// 登记草稿协议证据（草稿 → 已登记）：只改状态、登记时间与登记人（按已认证身份写入），
    /// 冻结条款与客户快照；<strong>不</strong>开发票、不记账、不授权付款、不改写任何既有记录。
    /// <para>ERP-516：冻结状态与审计字段**之前**，先调用
    /// <see cref="AgencyServiceFeeAgreementRules.EnsureStoredFeeTermsConsistent"/> 只读复核**已持久化**费用条款
    /// 仍按权威口径原样可存储（计费方式互斥 / 费率 (0,100] 且 4 位小数 / 固定金额按币种精度取整后 &gt; 0 且可在
    /// <c>DECIMAL(18,2)</c> 内原样存储）；不一致即受控拒绝，协议、来源引用与审计逐字段不变。</para>
    /// </summary>
    public static async Task<AgencyServiceFeeAgreementDto> RecordAsync(
        IErpDbContext db, long agreementId, string? recordedBy)
        => await RecordCoreAsync(db, agreementId, recordedBy, scope: null);

    /// <summary>
    /// 路由级登记协议证据（ERP-439）：<paramref name="scope"/> 非空时先按权威客户范围收敛协议；
    /// 范围外 / 已删除 / 不存在的协议一律拒绝，且拒绝发生在**写库之前**——
    /// 绝不改变任何协议状态、登记人或时间戳。
    /// <para>ERP-519：范围预检之后，冻结仍在同一事务 + 同一协议行锁内把范围重新施加到**权威行**
    /// （绝不信任加锁前的读取），因此并发归属变化不会被旧的授权快照掩盖。</para>
    /// </summary>
    public static async Task<AgencyServiceFeeAgreementDto> RecordAsync(
        IErpDbContext db, long agreementId, string? recordedBy, SalespersonDataScope? scope)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (scope is not null && agreementId > 0)
            await AgencyServiceFeeAgreementRules.EnsureAgreementInScopeAsync(db, scope, agreementId);
        return await RecordCoreAsync(db, agreementId, recordedBy, scope);
    }

    /// <summary>
    /// 登记核心（ERP-519）：同一事务 + 同一协议行锁 → 锁内重读权威状态（草稿资格）、客户范围与
    /// 已持久化费用条款（ERP-516）→ 冻结状态 / 登记人 / 时间戳。并发重复登记只有唯一赢家：
    /// 输家在锁内读到「已登记」一律拒绝，绝不覆盖赢家已写入的登记人与时间戳。
    /// </summary>
    private static async Task<AgencyServiceFeeAgreementDto> RecordCoreAsync(
        IErpDbContext db, long agreementId, string? recordedBy, SalespersonDataScope? scope)
    {
        ArgumentNullException.ThrowIfNull(db);

        await using var transaction = await AgencyFeeEvidenceConcurrencyRules.BeginMutationTransactionAsync(db);
        AgencyServiceFeeAgreement agreement = null!;
        try
        {
            // ERP-519：先取同一把协议行锁；锁内重读的权威状态才是登记资格与客户范围的唯一依据。
            agreement = await LockAuthoritativeAsync(db, agreementId);
            AgencyServiceFeeAgreementRules.EnsureRecordable(agreement.Status, IdentityOf(agreement));
            EnsureAuthoritativeScope(scope, agreement);
            // ERP-516：登记**冻结状态与审计字段之前**，只读复核已持久化的费用条款仍按权威口径原样可存储
            // （计费方式互斥 / 费率 (0,100] 且 4 位小数 / 固定金额按币种精度取整后 > 0 且可在 DECIMAL(18,2) 内原样存储）；
            // 不一致即受控拒绝，协议、来源引用与审计逐字段不变，绝不静默取整 / 回填历史。
            AgencyServiceFeeAgreementRules.EnsureStoredFeeTermsConsistent(agreement);

            agreement.Status = AgencyServiceFeeAgreementRules.StatusRecorded;
            agreement.RecordedAt = DateTime.Now;
            agreement.RecordedBy = AgencyServiceFeeAgreementRules.NormalizeRecordedBy(recordedBy);
            agreement.UpdatedAt = DateTime.Now;

            await AgencyFeeEvidenceConcurrencyRules.SaveEvidenceAsync(db);
            await AgencyFeeEvidenceConcurrencyRules.CommitAsync(transaction);
        }
        catch
        {
            await AgencyFeeEvidenceConcurrencyRules.TryRollbackAsync(transaction, db);
            throw;
        }

        return await MapAsync(db, agreement);
    }

    /// <summary>
    /// 作废协议证据（草稿 / 已登记 → 已作废）：必须填写作废原因；**保留**协议身份、费用条款、客户快照、
    /// 登记人与时间戳，不物理删除、不改写原始条款，也不产生任何发票 / 记账 / 付款 / 法律动作；重复作废被拒绝。
    /// </summary>
    public static async Task<AgencyServiceFeeAgreementDto> VoidAsync(
        IErpDbContext db, long agreementId, string? reason)
        => await VoidCoreAsync(db, agreementId, reason, scope: null);

    /// <summary>
    /// 路由级作废协议证据（ERP-439）：<paramref name="scope"/> 非空时先按权威客户范围收敛协议；
    /// 范围外 / 已删除 / 不存在的协议一律拒绝，且拒绝发生在**写库之前**——
    /// 绝不改变任何协议状态、作废原因或时间戳。
    /// <para>ERP-519：范围预检之后，作废仍在同一事务 + 同一协议行锁内把范围重新施加到**权威行**。</para>
    /// </summary>
    public static async Task<AgencyServiceFeeAgreementDto> VoidAsync(
        IErpDbContext db, long agreementId, string? reason, SalespersonDataScope? scope)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (scope is not null && agreementId > 0)
            await AgencyServiceFeeAgreementRules.EnsureAgreementInScopeAsync(db, scope, agreementId);
        return await VoidCoreAsync(db, agreementId, reason, scope);
    }

    /// <summary>
    /// 作废核心（ERP-519）：同一事务 + 同一协议行锁 → 锁内重读权威状态后判定可作废性 → 写入作废状态 /
    /// 原因 / 时间戳。并发重复作废只有唯一赢家，输家在锁内读到「已作废」一律拒绝，绝不覆盖赢家
    /// 已保留的原始作废原因与时间戳；历史读取与作废证据全部保留。
    /// </summary>
    private static async Task<AgencyServiceFeeAgreementDto> VoidCoreAsync(
        IErpDbContext db, long agreementId, string? reason, SalespersonDataScope? scope)
    {
        ArgumentNullException.ThrowIfNull(db);

        await using var transaction = await AgencyFeeEvidenceConcurrencyRules.BeginMutationTransactionAsync(db);
        AgencyServiceFeeAgreement agreement = null!;
        try
        {
            // ERP-519：先取同一把协议行锁；锁内权威状态决定可作废性，客户范围同样在锁内重新施加。
            agreement = await LockAuthoritativeAsync(db, agreementId);
            AgencyServiceFeeAgreementRules.EnsureVoidable(agreement.Status, IdentityOf(agreement));
            EnsureAuthoritativeScope(scope, agreement);
            var reasonText = AgencyServiceFeeAgreementRules.NormalizeVoidReason(reason);

            agreement.Status = AgencyServiceFeeAgreementRules.StatusVoided;
            agreement.VoidedAt = DateTime.Now;
            agreement.VoidReason = reasonText;
            agreement.UpdatedAt = DateTime.Now;

            await AgencyFeeEvidenceConcurrencyRules.SaveEvidenceAsync(db);
            await AgencyFeeEvidenceConcurrencyRules.CommitAsync(transaction);
        }
        catch
        {
            await AgencyFeeEvidenceConcurrencyRules.TryRollbackAsync(transaction, db);
            throw;
        }

        return await MapAsync(db, agreement);
    }

    // ==================== 4. 校验与映射（内部） ====================

    /// <summary>校验后的协议条款（全部为服务端权威值：含客户实体、规范化协议号与取整后的费用条款）</summary>
    private sealed record TermsInput(
        string AgreementNo,
        string NormalizedAgreementNo,
        BaseCustomer Customer,
        DateTime EffectiveFrom,
        DateTime? EffectiveTo,
        string Currency,
        string FeeMethod,
        decimal RatePercent,
        decimal FixedAmount,
        string FeeBasis,
        string Remark);

    /// <summary>
    /// 校验协议条款（协议号 / 客户 / 生效区间 / 币种 / 计费方式与费用条款 / 计费依据说明 / 备注）：
    /// 全部通过后才返回权威值；客户必须存在、未删除且启用（停用 / 删除一律拒绝新增与修改），
    /// 客户快照由服务端写入。
    /// </summary>
    private static async Task<TermsInput> ValidateAsync(IErpDbContext db, AgencyServiceFeeAgreementSaveDto dto)
    {
        var agreementNo = AgencyServiceFeeAgreementRules.NormalizeAgreementNo(dto.AgreementNo);
        var currency = AgencyServiceFeeAgreementRules.NormalizeCurrencyStrict(dto.Currency);
        var feeMethod = AgencyServiceFeeAgreementRules.NormalizeFeeMethod(dto.FeeMethod);
        var (from, to) = AgencyServiceFeeAgreementRules.ValidateEffectiveRange(dto.EffectiveFrom, dto.EffectiveTo);
        var (rate, fixedAmount) = AgencyServiceFeeAgreementRules.ValidateFeeTerms(
            feeMethod, dto.RatePercent, dto.FixedAmount, currency);
        var feeBasis = AgencyServiceFeeAgreementRules.NormalizeFeeBasis(dto.FeeBasis);
        var remark = AgencyServiceFeeAgreementRules.NormalizeRemark(dto.Remark);

        if (dto.CustomerId <= 0) throw BusinessException.InvalidParameter("请选择客户");

        var customer = await db.BaseCustomers
                .FirstOrDefaultAsync(c => c.Id == dto.CustomerId && !c.IsDeleted)
            ?? throw BusinessException.NotFound(
                $"客户（Id={dto.CustomerId}）不存在或已删除，不能登记代理服务费协议");

        if (customer.Status != 1)
            throw BusinessException.RuleConflict(
                $"客户「{customer.CustomerName}」已停用：停用客户不能登记新协议（历史证据保持可读）");

        return new TermsInput(
            agreementNo,
            AgencyServiceFeeAgreementRules.NormalizeIdentityPart(agreementNo),
            customer,
            from,
            to,
            currency,
            feeMethod,
            rate,
            fixedAmount,
            feeBasis,
            remark);
    }

    /// <summary>
    /// 有效身份唯一：同一「客户 + 规范化协议号」在**未作废、未删除**记录内唯一；重复一律拒绝
    /// （不静默合并、不覆盖、不按费率 / 金额 / 生效日期相似度匹配）；已作废记录保留可读但不占用身份。
    /// </summary>
    private static async Task EnsureIdentityAvailableAsync(
        IErpDbContext db, TermsInput input, long? excludeAgreementId)
    {
        var excludedId = excludeAgreementId ?? 0;
        var identity = AgencyServiceFeeAgreementRules.IdentityText(input.AgreementNo);

        var duplicate = await db.AgencyServiceFeeAgreements.AsNoTracking()
            .Where(x => !x.IsDeleted
                        && x.Status != AgencyServiceFeeAgreementRules.StatusVoided
                        && x.CustomerId == input.Customer.Id
                        && x.NormalizedAgreementNo == input.NormalizedAgreementNo
                        && x.Id != excludedId)
            .Select(x => new { x.Id, x.EffectiveFrom, x.Status })
            .FirstOrDefaultAsync();

        if (duplicate is null) return;

        throw BusinessException.Duplicate(
            $"客户「{input.Customer.CustomerName}」已存在同一协议的未作废记录：{identity}"
            + $"（Id={duplicate.Id}，状态 {AgencyServiceFeeAgreementRules.StatusText(duplicate.Status)}，"
            + $"生效起始 {duplicate.EffectiveFrom:yyyy-MM-dd}）；重复协议被拒绝而不是静默合并"
            + "（如需更正请先作废原记录再重新登记）");
    }

    /// <summary>按 Id 装载未删除协议（不存在 / 已删除 → 数据不存在）</summary>
    private static async Task<AgencyServiceFeeAgreement> LoadAsync(IErpDbContext db, long agreementId)
    {
        if (agreementId <= 0) throw BusinessException.InvalidParameter("请选择要操作的代理服务费协议");
        return await db.AgencyServiceFeeAgreements
                   .FirstOrDefaultAsync(x => x.Id == agreementId && !x.IsDeleted)
               ?? throw BusinessException.NotFound($"代理服务费协议证据（Id={agreementId}）不存在或已删除");
    }

    /// <summary>
    /// ERP-519：取**同一把协议行锁**并返回锁内重读的受跟踪权威实体。关系型后端在调用方事务内对协议行发出
    /// 排它行锁（阻塞至并发事务提交后重读，语义等价 <c>SELECT ... WITH (UPDLOCK, HOLDLOCK)</c>）；
    /// 非关系型提供程序无行锁语义（等价无操作），直接按 Id 装载。行已被并发软删除 / 不存在 →
    /// 既有「不存在或已删除」受控错误（由 <see cref="LoadAsync"/> 给出，不新增错误口径）。
    /// </summary>
    private static async Task<AgencyServiceFeeAgreement> LockAuthoritativeAsync(
        IErpDbContext db, long agreementId)
    {
        var locked = await AgencyFeeEvidenceConcurrencyRules.LockAgreementRowAsync(db, agreementId);
        return locked ?? await LoadAsync(db, agreementId);
    }

    /// <summary>
    /// ERP-519：在锁内把已解析的客户数据范围重新施加到**权威行**（绝不信任加锁前的读取）；
    /// 越范围 / 已删除 / 不存在一律返回同一条不披露存在性的受控错误，且拒绝发生在任何字段 / 状态写入之前。
    /// 进程内无范围的直调（<paramref name="scope"/> 为 <c>null</c>）保持既有免范围口径（不新增授权）。
    /// </summary>
    private static void EnsureAuthoritativeScope(
        SalespersonDataScope? scope, AgencyServiceFeeAgreement agreement)
    {
        if (scope is not null)
            AgencyServiceFeeAgreementRules.EnsureCustomerInScope(scope, agreement.CustomerId);
    }

    /// <summary>协议对外身份文案（提示与台账共用；与唯一性判定口径一致）</summary>
    private static string IdentityOf(AgencyServiceFeeAgreement agreement)
    {
        ArgumentNullException.ThrowIfNull(agreement);
        return AgencyServiceFeeAgreementRules.IdentityText(agreement.AgreementNo);
    }

    /// <summary>单条映射（详情与单条操作返回用；只读标注，不写库）</summary>
    private static async Task<AgencyServiceFeeAgreementDto> MapAsync(
        IErpDbContext db, AgencyServiceFeeAgreement agreement)
    {
        var mapped = await MapManyAsync(db, new List<AgencyServiceFeeAgreement> { agreement });
        return mapped[0];
    }

    /// <summary>批量映射（台账分页用）：客户一次批量装载，绝无逐行数据库查询</summary>
    private static async Task<List<AgencyServiceFeeAgreementDto>> MapManyAsync(
        IErpDbContext db, IReadOnlyList<AgencyServiceFeeAgreement> agreements)
    {
        if (agreements.Count == 0) return new List<AgencyServiceFeeAgreementDto>();

        var customerIds = agreements.Select(a => a.CustomerId).Distinct().ToList();
        var customers = (await db.BaseCustomers.AsNoTracking()
                .Where(c => customerIds.Contains(c.Id)).ToListAsync())
            .ToDictionary(c => c.Id);

        return agreements.Select(agreement => Map(agreement, customers)).ToList();
    }

    /// <summary>协议实体 → 台账 DTO（含客户可用性标注与同源口径文案；纯映射，不写库）</summary>
    private static AgencyServiceFeeAgreementDto Map(
        AgencyServiceFeeAgreement agreement, Dictionary<long, BaseCustomer> customers)
    {
        ArgumentNullException.ThrowIfNull(agreement);

        var currency = CurrencyAmountRules.NormalizeCurrency(agreement.Currency);
        customers.TryGetValue(agreement.CustomerId, out var customer);

        return new AgencyServiceFeeAgreementDto(
            agreement.Id,
            agreement.AgreementNo ?? string.Empty,
            IdentityOf(agreement),
            agreement.CustomerId,
            agreement.CustomerCode ?? string.Empty,
            agreement.CustomerName ?? string.Empty,
            AgencyServiceFeeAgreementRules.IsCustomerSelectable(customer),
            AgencyServiceFeeAgreementRules.CustomerAvailabilityText(customer),
            agreement.EffectiveFrom,
            agreement.EffectiveTo,
            AgencyServiceFeeAgreementRules.EffectiveRangeText(agreement.EffectiveFrom, agreement.EffectiveTo),
            currency,
            CurrencyAmountRules.PrecisionOf(currency),
            (agreement.FeeMethod ?? string.Empty).Trim(),
            agreement.RatePercent,
            agreement.FixedAmount,
            AgencyServiceFeeAgreementRules.FeeTermsText(
                agreement.FeeMethod, agreement.RatePercent, agreement.FixedAmount, currency),
            agreement.FeeBasis ?? string.Empty,
            agreement.Status,
            AgencyServiceFeeAgreementRules.StatusText(agreement.Status),
            agreement.Status == AgencyServiceFeeAgreementRules.StatusDraft,
            agreement.Status == AgencyServiceFeeAgreementRules.StatusRecorded,
            agreement.Status == AgencyServiceFeeAgreementRules.StatusVoided,
            agreement.RecordedAt,
            agreement.RecordedBy ?? string.Empty,
            agreement.VoidedAt,
            agreement.VoidReason ?? string.Empty,
            agreement.Remark ?? string.Empty,
            agreement.CreatedAt,
            agreement.UpdatedAt,
            AgencyServiceFeeAgreementRules.FeeTermsRuleText,
            AgencyServiceFeeAgreementRules.CommissionSeparationText,
            AgencyServiceFeeAgreementRules.BoundaryText);
    }
}
