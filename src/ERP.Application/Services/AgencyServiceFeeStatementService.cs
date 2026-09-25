using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ERP.Application.Services;

/// <summary>
/// 代理服务费对账单证据服务（ERP-070）。职责：
/// <list type="number">
/// <item><b>登记 / 修改</b>（<see cref="CreateAsync"/> / <see cref="UpdateAsync"/>）：只允许操作**草稿**；
/// 客户必须存在、未删除且启用，**必须显式关联**一条已登记的 ERP-069 协议（协议客户 / 币种必须与对账单一致），
/// 每条行必须用来源类型 + 来源记录 Id **显式**引用可用的服务来源，金额按币种精度取整并**在服务端求和**；</item>
/// <item><b>唯一性</b>：同一「客户 + 规范化对账单号」在未作废对账单内唯一；同一服务来源在未作废行内**全局唯一**
/// （防重复计费证据）—— 重复一律拒绝，**不静默合并、不覆盖、不改派**；</item>
/// <item><b>登记 / 作废</b>（<see cref="RecordAsync"/> / <see cref="VoidAsync"/>）：登记冻结表头与全部行并按已认证身份
/// 记录登记人，同时按持久化行金额**重算合计**；作废必须填写原因，保留原始行、来源与客户 / 协议快照、登记人与时间戳，
/// 不物理删除、不静默改写；</item>
/// <item><b>台账 / 详情读取</b>（<see cref="ListAsync"/> / <see cref="GetAsync"/>）：分页 / 有界，
/// 客户、协议与行数**一次批量装载**，绝无逐行数据库查询。</item>
/// </list>
/// <para>来源链接口径（关键）：行只按「来源类型 + 来源记录 Id」的**持久化标识符**引用来源，
/// 本服务<strong>不</strong>按单号文本、金额、日期或相似度匹配 / 猜测任何来源。</para>
/// <para>金额口径（服务端权威）：行金额 / 计费基础数量只来自用户显式提交，本服务<strong>不</strong>从协议费率、
/// 协议固定金额、客户账期与默认值、来源单据金额、自由文本或历史对账单推断、折算、补齐任何费用；
/// 合计只按已校验行金额求和，客户端提交的合计（若有）不被采信。</para>
/// <para>边界（重要）：本服务只读写 <c>AgencyServiceFeeStatements</c> 与 <c>AgencyServiceFeeStatementLines</c>
/// 两张表；<strong>不</strong>开票 / 报税、<strong>不</strong>记账或生成凭证 / 收款 / 付款 / 结算单、
/// <strong>不</strong>收款或付款、<strong>不</strong>催收或联系客户、<strong>不</strong>调用任何外部服务，
/// 也<strong>不</strong>改写 ERP-069 协议证据、客户主数据、销售订单、装柜与装柜清单、单证、发票、收款单及其引用行、
/// 库存与库存成本、库存流水、费用与退税、结算与余额记录。</para>
/// </summary>
public static class AgencyServiceFeeStatementService
{
    /// <summary>来源候选单次返回上限（有界，避免一次拉全表）</summary>
    public const int MaxSourceOptions = AgencyServiceFeeStatementRules.MaxSourceOptions;

    // ==================== 1. 登记 / 修改（仅草稿） ====================

    /// <summary>
    /// 新增草稿对账单证据：校验对账单号 / 客户 / 协议资格与兼容性 / 对账日期 / 可选到期日 / 服务期间 /
    /// 每一条显式来源行（存在、未删除、未取消、客户与币种兼容）与行金额，写入客户与协议快照、行号与来源快照，
    /// 并在服务端计算合计；重复对账单身份与已被其它未作废对账单引用的来源都被拒绝。
    /// </summary>
    public static async Task<AgencyServiceFeeStatementDto> CreateAsync(
        IErpDbContext db, AgencyServiceFeeStatementSaveDto dto)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(dto);

        var input = await ValidateAsync(db, dto);
        await EnsureIdentityAvailableAsync(db, input, excludeStatementId: null);
        await EnsureSourcesNotChargedAsync(db, input, excludeStatementId: null);

        var statement = new AgencyServiceFeeStatement();
        ApplyHeader(statement, input);

        await using var transaction = await db.Database.BeginTransactionAsync();
        db.AgencyServiceFeeStatements.Add(statement);
        await db.SaveChangesAsync();
        db.AgencyServiceFeeStatementLines.AddRange(BuildLines(statement.Id, input));
        await db.SaveChangesAsync();
        await transaction.CommitAsync();

        return await MapAsync(db, statement, includeLines: true);
    }

    /// <summary>
    /// 修改草稿对账单证据：已登记 / 已作废拒绝修改（保留可读）；原草稿行按**软删除**替换（草稿尚未构成证据，
    /// 但也不做物理删除），新行重新校验客户 / 币种兼容性与来源唯一性，合计在服务端重算；
    /// 修改不会触碰登记人 / 登记时间（草稿尚未登记时它们为空）。
    /// </summary>
    public static async Task<AgencyServiceFeeStatementDto> UpdateAsync(
        IErpDbContext db, long statementId, AgencyServiceFeeStatementSaveDto dto)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(dto);
        if (statementId <= 0) throw BusinessException.InvalidParameter("请选择要修改的代理服务费对账单");

        var statement = await LoadAsync(db, statementId);
        AgencyServiceFeeStatementRules.EnsureEditable(statement.Status, IdentityOf(statement));

        var input = await ValidateAsync(db, dto);
        await EnsureIdentityAvailableAsync(db, input, statement.Id);
        await EnsureSourcesNotChargedAsync(db, input, statement.Id);

        var existingLines = await db.AgencyServiceFeeStatementLines
            .Where(l => l.StatementId == statement.Id && !l.IsDeleted)
            .ToListAsync();
        foreach (var line in existingLines)
        {
            // 草稿行被替换：软删除（保留可审计痕迹），不作物理删除、不静默改写
            line.IsDeleted = true;
            line.UpdatedAt = DateTime.Now;
        }

        ApplyHeader(statement, input);
        statement.UpdatedAt = DateTime.Now;

        await using var transaction = await db.Database.BeginTransactionAsync();
        db.AgencyServiceFeeStatementLines.AddRange(BuildLines(statement.Id, input));
        await db.SaveChangesAsync();
        await transaction.CommitAsync();

        return await MapAsync(db, statement, includeLines: true);
    }

    // ==================== 2. 台账 / 详情 / 元数据 / 来源候选（只读、有界） ====================

    /// <summary>对账单证据详情（含**全部有界行清单**与客户 / 协议可用性标注；只读）</summary>
    public static async Task<AgencyServiceFeeStatementDto> GetAsync(IErpDbContext db, long statementId)
    {
        ArgumentNullException.ThrowIfNull(db);
        var statement = await LoadAsync(db, statementId);
        return await MapAsync(db, statement, includeLines: true);
    }

    /// <summary>
    /// 台账分页查询（只读）：支持客户 / 协议 / 状态 / 币种 / 来源类型 / 对账日期区间 / 关键字过滤；
    /// 默认包含已作废历史（证据保留可读）。页内客户、协议与行数**一次批量装载**（无逐行数据库查询）；
    /// 列表只返回行数摘要，行的完整快照由详情接口给出（有界，避免一次拉取无界行数据）。
    /// </summary>
    public static async Task<PagedResult<AgencyServiceFeeStatementDto>> ListAsync(
        IErpDbContext db, AgencyServiceFeeStatementQuery query)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(query);
        query.Normalize();

        var status = AgencyServiceFeeStatementRules.NormalizeStatusFilter(query.Status);
        var sourceType = AgencyServiceFeeStatementRules.NormalizeSourceTypeFilter(query.SourceType);
        var currency = string.IsNullOrWhiteSpace(query.Currency)
            ? null
            : AgencyServiceFeeStatementRules.NormalizeCurrencyStrict(query.Currency);
        var keyword = AgencyServiceFeeStatementRules.NormalizeKeyword(query.Keyword);

        var source = db.AgencyServiceFeeStatements.AsNoTracking().Where(x => !x.IsDeleted);
        if (query.CustomerId is not null) source = source.Where(x => x.CustomerId == query.CustomerId.Value);
        if (query.AgreementId is not null) source = source.Where(x => x.AgreementId == query.AgreementId.Value);
        if (status is not null) source = source.Where(x => x.Status == status.Value);
        if (currency is not null) source = source.Where(x => x.Currency == currency);
        if (query.StatementDateFrom is not null)
            source = source.Where(x => x.StatementDate >= query.StatementDateFrom.Value.Date);
        if (query.StatementDateTo is not null)
            source = source.Where(x => x.StatementDate <= query.StatementDateTo.Value.Date);

        if (sourceType is not null)
        {
            // 来源类型过滤：只按行的**持久化来源类型列**判定（不做文本 / 金额 / 相似度匹配）
            var statementIds = db.AgencyServiceFeeStatementLines.AsNoTracking()
                .Where(l => !l.IsDeleted && l.SourceType == sourceType)
                .Select(l => l.StatementId);
            source = source.Where(x => statementIds.Contains(x.Id));
        }

        if (keyword.Length > 0)
        {
            source = source.Where(x => x.StatementNo.Contains(keyword)
                || x.CustomerName.Contains(keyword)
                || x.CustomerCode.Contains(keyword)
                || x.AgreementNo.Contains(keyword)
                || x.Remark.Contains(keyword));
        }

        var total = await source.CountAsync();
        var page = await source
            .OrderByDescending(x => x.StatementDate)
            .ThenByDescending(x => x.Id)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .ToListAsync();

        return new PagedResult<AgencyServiceFeeStatementDto>
        {
            Items = await MapManyAsync(db, page, includeLines: false),
            Total = total,
            Page = query.Page,
            PageSize = query.PageSize
        };
    }

    /// <summary>
    /// 模块元数据（只读）：来源类型 / 状态白名单、支持币种、有界额度与口径文案，
    /// 供界面与接口同源显示（避免前端硬编码与后端校验口径漂移）。
    /// </summary>
    public static AgencyServiceFeeStatementMetadataDto GetMetadata()
        => new(
            AgencyServiceFeeStatementRules.SupportedSourceTypes
                .Select(t => new AgencyServiceFeeStatementOptionDto(
                    t, AgencyServiceFeeStatementRules.SourceTypeText(t)))
                .ToList(),
            new List<AgencyServiceFeeStatementOptionDto>
            {
                new(AgencyServiceFeeStatementRules.StatusDraft.ToString(), "草稿"),
                new(AgencyServiceFeeStatementRules.StatusRecorded.ToString(), "已登记"),
                new(AgencyServiceFeeStatementRules.StatusVoided.ToString(), "已作废")
            },
            AgencyServiceFeeStatementRules.SupportedCurrencies.ToList(),
            AgencyServiceFeeStatementRules.MaxLinesPerStatement,
            MaxSourceOptions,
            AgencyServiceFeeStatementQuery.MaxPageSize,
            AgencyServiceFeeStatementRules.SourceLinkRuleText,
            AgencyServiceFeeStatementRules.AmountRuleText,
            AgencyServiceFeeStatementRules.DueDateRuleText,
            AgencyServiceFeeStatementRules.UniquenessRuleText,
            AgencyServiceFeeStatementRules.SeparationText,
            AgencyServiceFeeStatementRules.BoundaryText);

    /// <summary>
    /// 可引用的**显式服务来源**候选（只读、**有界**）：必须显式给出客户与对账单币种（资格判定依赖它们），
    /// 单次最多 <see cref="MaxSourceOptions"/> 条；未删除的记录都可作为候选，资格（未取消 / 客户一致 /
    /// 币种一致）由服务端逐条判定并给出原因文案，关键字只匹配单号。
    /// <para>刻意**不回显来源金额**：来源金额不是费用依据，服务端不会把来源金额转换成行金额。</para>
    /// </summary>
    public static async Task<List<AgencyServiceFeeStatementSourceOptionDto>> ListSourceOptionsAsync(
        IErpDbContext db, string? sourceType, long customerId, string? currency, string? keyword, int take)
    {
        ArgumentNullException.ThrowIfNull(db);

        if (customerId <= 0)
            throw BusinessException.InvalidParameter(
                "请先选择客户：来源候选的资格判定依赖客户（系统不按相似度推荐来源）");
        if (string.IsNullOrWhiteSpace(currency))
            throw BusinessException.InvalidParameter(
                "请先选择对账单币种：来源候选的资格判定依赖币种（系统不替来源补一个币种）");

        var statementCurrency = AgencyServiceFeeStatementRules.NormalizeCurrencyStrict(currency);
        var normalizedType = AgencyServiceFeeStatementRules.NormalizeSourceTypeFilter(sourceType);
        var key = AgencyServiceFeeStatementRules.NormalizeKeyword(keyword);
        var limit = Math.Clamp(take <= 0 ? MaxSourceOptions : take, 1, MaxSourceOptions);

        var options = new List<AgencyServiceFeeStatementSourceOptionDto>();

        if (normalizedType is null or AgencyServiceFeeStatementRules.SourceTypeSalesOrder)
        {
            var orders = db.SalesOrders.AsNoTracking()
                .Where(o => !o.IsDeleted && o.CustomerId == customerId);
            if (key.Length > 0) orders = orders.Where(o => o.OrderNo.Contains(key));
            var rows = await orders
                .OrderByDescending(o => o.OrderDate).ThenByDescending(o => o.Id)
                .Take(limit)
                .Select(o => new
                {
                    o.Id,
                    o.OrderNo,
                    o.OrderDate,
                    Status = (int)o.Status,
                    o.CustomerId,
                    Currency = o.Currency.ToString()
                })
                .ToListAsync();

            options.AddRange(rows.Select(row => SourceOption(
                AgencyServiceFeeStatementRules.SourceTypeSalesOrder, row.Id, row.OrderNo, row.OrderDate,
                row.Status, row.CustomerId, row.Currency, customerId, statementCurrency)));
        }

        if (normalizedType is null or AgencyServiceFeeStatementRules.SourceTypeLoadingList)
        {
            var lists = db.ContainerLoadingLists.AsNoTracking()
                .Where(l => !l.IsDeleted && l.CustomerId == customerId);
            if (key.Length > 0) lists = lists.Where(l => l.LoadingListNo.Contains(key));
            var rows = await lists
                .OrderByDescending(l => l.LoadingDate).ThenByDescending(l => l.Id)
                .Take(limit)
                .Select(l => new
                {
                    l.Id,
                    l.LoadingListNo,
                    l.LoadingDate,
                    Status = (int)l.Status,
                    l.CustomerId
                })
                .ToListAsync();

            options.AddRange(rows.Select(row => SourceOption(
                AgencyServiceFeeStatementRules.SourceTypeLoadingList, row.Id, row.LoadingListNo,
                row.LoadingDate, row.Status, row.CustomerId,
                // 装柜清单**没有币种列**：照实记空串（= 来源不携带币种），绝不由客户 / 订单 / 自由文本补一个币种
                string.Empty, customerId, statementCurrency)));
        }

        return options
            .OrderByDescending(x => x.SourceDate)
            .ThenByDescending(x => x.SourceId)
            .Take(limit)
            .ToList();
    }

    /// <summary>构造来源候选（资格判定复用纯规则；候选只回显身份 / 日期 / 状态 / 客户 / 币种，不回显金额）</summary>
    private static AgencyServiceFeeStatementSourceOptionDto SourceOption(
        string sourceType, long sourceId, string? sourceNo, DateTime sourceDate, int sourceStatus,
        long sourceCustomerId, string? sourceCurrency, long statementCustomerId, string statementCurrency)
    {
        var (eligible, text) = AgencyServiceFeeStatementRules.EvaluateSourceEligibility(
            sourceType, exists: true, deleted: false, sourceStatus, sourceCustomerId, sourceCurrency,
            statementCustomerId, statementCurrency);

        return new AgencyServiceFeeStatementSourceOptionDto(
            sourceType,
            AgencyServiceFeeStatementRules.SourceTypeText(sourceType),
            sourceId,
            sourceNo ?? string.Empty,
            sourceDate,
            sourceStatus,
            DocumentStatusText(sourceStatus),
            sourceCustomerId,
            string.Empty,
            string.Empty,
            AgencyServiceFeeStatementRules.SourceCarriesCurrency(sourceType)
                ? CurrencyAmountRules.NormalizeCurrency(sourceCurrency)
                : string.Empty,
            eligible,
            text);
    }

    /// <summary>来源状态文案（未知状态码照实回显，不假定为已审核）</summary>
    private static string DocumentStatusText(int status) => status switch
    {
        (int)DocumentStatus.Pending => "待提交",
        (int)DocumentStatus.Submitted => "已提交",
        (int)DocumentStatus.Approved => "已审核",
        (int)DocumentStatus.Rejected => "已驳回",
        (int)DocumentStatus.Completed => "已完成",
        (int)DocumentStatus.Cancelled => "已取消",
        _ => $"未知（{status}）"
    };

    // ==================== 3. 登记 / 作废（证据冻结与保留） ====================

    /// <summary>
    /// 登记对账单证据（草稿 → 已登记）：按**持久化行金额**在服务端重算合计，冻结表头与全部行、
    /// 写入登记时间与登记人（按已认证身份写入，客户端不能提交该字段）；
    /// <strong>不</strong>开票、<strong>不</strong>记账、<strong>不</strong>收款或催收、<strong>不</strong>调用外部服务，
    /// 也不改写任何来源记录与协议 / 客户 / 发票 / 收款 / 库存 / 费用记录。
    /// </summary>
    public static async Task<AgencyServiceFeeStatementDto> RecordAsync(
        IErpDbContext db, long statementId, string? recordedBy)
    {
        ArgumentNullException.ThrowIfNull(db);
        var statement = await LoadAsync(db, statementId);
        AgencyServiceFeeStatementRules.EnsureRecordable(statement.Status, IdentityOf(statement));

        var lines = await db.AgencyServiceFeeStatementLines
            .Where(l => l.StatementId == statement.Id && !l.IsDeleted)
            .ToListAsync();
        AgencyServiceFeeStatementRules.EnsureLineCountWithinLimit(lines.Count);

        var now = DateTime.Now;
        var recordedByName = AgencyServiceFeeStatementRules.NormalizeRecordedBy(recordedBy);

        statement.TotalAmount = AgencyServiceFeeStatementRules.ComputeTotal(
            lines.Select(l => l.Amount), statement.Currency);
        statement.Status = AgencyServiceFeeStatementRules.StatusRecorded;
        statement.RecordedAt = now;
        statement.RecordedBy = recordedByName;
        statement.UpdatedAt = now;

        foreach (var line in lines)
        {
            line.Status = AgencyServiceFeeStatementRules.StatusRecorded;
            line.RecordedAt = now;
            line.RecordedBy = recordedByName;
            line.UpdatedAt = now;
        }

        await db.SaveChangesAsync();

        return await MapAsync(db, statement, includeLines: true);
    }

    /// <summary>
    /// 作废对账单证据（草稿 / 已登记 → 已作废）：必须填写作废原因；**保留**对账单身份、全部原始行、来源快照、
    /// 客户与协议快照、登记人与时间戳，不物理删除、不改写原始金额，也不产生任何发票 / 记账 / 收款 / 催收动作；
    /// 作废同时释放被占用的来源身份（该来源可重新被新对账单显式引用）；重复作废被拒绝。
    /// </summary>
    public static async Task<AgencyServiceFeeStatementDto> VoidAsync(
        IErpDbContext db, long statementId, string? reason)
    {
        ArgumentNullException.ThrowIfNull(db);
        var statement = await LoadAsync(db, statementId);
        AgencyServiceFeeStatementRules.EnsureVoidable(statement.Status, IdentityOf(statement));
        var reasonText = AgencyServiceFeeStatementRules.NormalizeVoidReason(reason);

        var lines = await db.AgencyServiceFeeStatementLines
            .Where(l => l.StatementId == statement.Id && !l.IsDeleted)
            .ToListAsync();

        var now = DateTime.Now;
        statement.Status = AgencyServiceFeeStatementRules.StatusVoided;
        statement.VoidedAt = now;
        statement.VoidReason = reasonText;
        statement.UpdatedAt = now;

        foreach (var line in lines)
        {
            line.Status = AgencyServiceFeeStatementRules.StatusVoided;
            line.VoidedAt = now;
            line.VoidReason = reasonText;
            line.UpdatedAt = now;
        }

        await db.SaveChangesAsync();

        return await MapAsync(db, statement, includeLines: true);
    }

    // ==================== 4. 校验、写入与映射（内部） ====================

    /// <summary>校验后的对账单输入（全部为服务端权威值：含客户与协议实体、规范化身份、已取整行金额与服务端合计）</summary>
    private sealed record StatementInput(
        string StatementNo,
        string NormalizedStatementNo,
        BaseCustomer Customer,
        string Currency,
        DateTime StatementDate,
        DateTime? DueDate,
        DateTime ServicePeriodFrom,
        DateTime ServicePeriodTo,
        AgencyServiceFeeAgreement Agreement,
        string AgreementCurrency,
        string AgreementTermsText,
        string Remark,
        List<LineInput> Lines,
        decimal TotalAmount);

    /// <summary>校验后的行输入（含服务端写入的来源快照与已按币种精度取整的金额）</summary>
    private sealed record LineInput(
        int LineNo,
        string SourceType,
        long SourceId,
        string SourceNo,
        DateTime SourceDate,
        int SourceStatus,
        long SourceCustomerId,
        string SourceCurrency,
        string Description,
        decimal? BasisQuantity,
        string BasisNote,
        decimal Amount,
        string Remark);

    /// <summary>来源快照（只用于校验与快照写入；不读取来源金额，也不据其推断费用）</summary>
    private sealed record SourceSnapshot(
        bool Exists, bool Deleted, int Status, long CustomerId, string Currency, string SourceNo, DateTime SourceDate);

    /// <summary>来源键（类型 + 持久化 Id；与来源唯一性判定口径一致）</summary>
    private static string SourceKey(string sourceType, long sourceId) => $"{sourceType}|{sourceId}";

    /// <summary>
    /// 校验对账单表头：对账单号 / 币种 / 对账日期 / 可选到期日 / 服务期间 / 备注、客户资格、
    /// 显式关联协议的存在性 / 状态 / 「客户 + 币种」兼容性。返回的客户与协议实体作为快照写入依据。
    /// </summary>
    private static async Task<(BaseCustomer Customer, AgencyServiceFeeAgreement Agreement, string AgreementCurrency, string AgreementTermsText)>
        ValidateHeaderAsync(IErpDbContext db, AgencyServiceFeeStatementSaveDto dto, long customerId)
    {
        if (customerId <= 0) throw BusinessException.InvalidParameter("请选择客户");

        var customer = await db.BaseCustomers
                .FirstOrDefaultAsync(c => c.Id == customerId && !c.IsDeleted)
            ?? throw BusinessException.NotFound(
                $"客户（Id={customerId}）不存在或已删除，不能登记代理服务费对账单");
        if (customer.Status != 1)
            throw BusinessException.RuleConflict(
                $"客户「{customer.CustomerName}」已停用：停用客户不能登记新对账单（历史证据保持可读）");

        if (dto.AgreementId <= 0)
            throw BusinessException.InvalidParameter(
                "请显式选择关联的代理服务费协议证据（ERP-069）：系统不会自动匹配协议、也不按客户或金额猜测");
        var agreement = await db.AgencyServiceFeeAgreements.AsNoTracking()
                .FirstOrDefaultAsync(a => a.Id == dto.AgreementId && !a.IsDeleted)
            ?? throw BusinessException.NotFound(
                $"代理服务费协议证据（Id={dto.AgreementId}）不存在或已删除，不能作为对账单依据");
        if (agreement.Status != AgencyServiceFeeAgreementRules.StatusRecorded)
            throw BusinessException.RuleConflict(
                AgencyServiceFeeStatementRules.AgreementAvailabilityText(agreement.Status, deleted: false));

        var agreementCurrency = AgencyServiceFeeAgreementRules.NormalizeCurrencyStrict(agreement.Currency);
        if (agreement.CustomerId != customer.Id)
            throw BusinessException.RuleConflict(
                $"协议「{agreement.AgreementNo}」的客户（Id={agreement.CustomerId}）与对账单客户"
                + $"（Id={customer.Id}）不一致，不能登记（不做跨客户合并）");

        var statementCurrency = AgencyServiceFeeStatementRules.NormalizeCurrencyStrict(dto.Currency);
        if (!string.Equals(agreementCurrency, statementCurrency, StringComparison.Ordinal))
            throw BusinessException.RuleConflict(
                $"协议「{agreement.AgreementNo}」的币种 {agreementCurrency} 与对账单币种 {statementCurrency} 不一致，"
                + "不能登记（不做汇率换算）");

        var agreementTerms = AgencyServiceFeeAgreementRules.FeeTermsText(
            agreement.FeeMethod, agreement.RatePercent, agreement.FixedAmount, agreementCurrency);
        if (agreementTerms.Length > 200) agreementTerms = agreementTerms[..200];

        return (customer, agreement, agreementCurrency, agreementTerms);
    }

    /// <summary>
    /// 校验对账单与全部行（**先校验后写入**）：先校验表头与客户 / 协议兼容性，再校验行数与请求内重复来源、
    /// 批量装载来源快照、逐行校验来源资格与行证据，最后在服务端计算合计。
    /// </summary>
    private static async Task<StatementInput> ValidateAsync(IErpDbContext db, AgencyServiceFeeStatementSaveDto dto)
    {
        var statementNo = AgencyServiceFeeStatementRules.NormalizeStatementNo(dto.StatementNo);
        var currency = AgencyServiceFeeStatementRules.NormalizeCurrencyStrict(dto.Currency);
        var statementDate = AgencyServiceFeeStatementRules.ValidateStatementDate(dto.StatementDate);
        var dueDate = AgencyServiceFeeStatementRules.ValidateDueDate(dto.DueDate, statementDate);
        var (periodFrom, periodTo) = AgencyServiceFeeStatementRules.ValidateServicePeriod(
            dto.ServicePeriodFrom, dto.ServicePeriodTo);
        var remark = AgencyServiceFeeStatementRules.NormalizeRemark(dto.Remark);

        var (customer, agreement, agreementCurrency, agreementTerms) =
            await ValidateHeaderAsync(db, dto, dto.CustomerId);

        var requestedLines = dto.Lines ?? new List<AgencyServiceFeeStatementLineSaveDto>();
        AgencyServiceFeeStatementRules.EnsureLineCountWithinLimit(requestedLines.Count);

        // 第一步：纯规范化 + 请求内重复来源检测（无数据库访问；文本 / 金额 / 相似度匹配绝不参与）
        var normalized = new List<(string SourceType, long SourceId, int Index)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < requestedLines.Count; index++)
        {
            var lineNumber = index + 1;
            var raw = requestedLines[index];
            var sourceType = AgencyServiceFeeStatementRules.NormalizeSourceType(raw.SourceType);
            if (raw.SourceId <= 0)
                throw BusinessException.InvalidParameter(
                    $"第 {lineNumber} 行：请显式选择具体的服务来源记录（来源 Id 必须由用户显式选择，"
                    + "系统不按单号文本、金额或相似度匹配来源）");
            if (!seen.Add(SourceKey(sourceType, raw.SourceId)))
                throw BusinessException.Duplicate(
                    $"第 {lineNumber} 行与本次提交中的另一行引用了同一个服务来源"
                    + $"（{AgencyServiceFeeStatementRules.SourceTypeText(sourceType)} Id={raw.SourceId}）："
                    + "同一对账单内重复引用被拒绝，而不是合并或忽略");
            normalized.Add((sourceType, raw.SourceId, index));
        }

        // 第二步：**批量**装载来源快照（每种来源类型一次查询，绝无逐行数据库访问）
        var snapshots = await LoadSourceSnapshotsAsync(db,
            normalized.Select(x => (x.SourceType, x.SourceId)).ToList());

        // 第三步：逐行校验来源资格与行证据
        var lineInputs = new List<LineInput>();
        foreach (var item in normalized)
        {
            var lineNumber = item.Index + 1;
            var raw = requestedLines[item.Index];
            var snapshot = snapshots[SourceKey(item.SourceType, item.SourceId)];

            var (eligible, eligibilityText) = AgencyServiceFeeStatementRules.EvaluateSourceEligibility(
                item.SourceType, snapshot.Exists, snapshot.Deleted, snapshot.Status,
                snapshot.CustomerId, snapshot.Currency, customer.Id, currency);
            if (!eligible)
                throw BusinessException.RuleConflict($"第 {lineNumber} 行：{eligibilityText}");

            lineInputs.Add(new LineInput(
                lineNumber,
                item.SourceType,
                item.SourceId,
                snapshot.SourceNo,
                snapshot.SourceDate,
                snapshot.Status,
                snapshot.CustomerId,
                snapshot.Currency,
                AgencyServiceFeeStatementRules.NormalizeDescription(raw.Description),
                AgencyServiceFeeStatementRules.ValidateBasisQuantity(raw.BasisQuantity),
                AgencyServiceFeeStatementRules.NormalizeBasisNote(raw.BasisNote),
                AgencyServiceFeeStatementRules.ValidateAmount(raw.Amount, currency),
                AgencyServiceFeeStatementRules.NormalizeRemark(raw.Remark)));
        }

        var total = AgencyServiceFeeStatementRules.ComputeTotal(lineInputs.Select(x => x.Amount), currency);

        return new StatementInput(
            statementNo,
            AgencyServiceFeeStatementRules.NormalizeIdentityPart(statementNo),
            customer,
            currency,
            statementDate,
            dueDate,
            periodFrom,
            periodTo,
            agreement,
            agreementCurrency,
            agreementTerms,
            remark,
            lineInputs,
            total);
    }

    /// <summary>
    /// **批量**装载来源快照（销售订单 / 装柜清单各一次查询）：装柜清单**没有币种列**，
    /// 因此其快照币种记空串（= 来源不携带币种），绝不由客户 / 订单 / 自由文本补一个币种。
    /// </summary>
    private static async Task<Dictionary<string, SourceSnapshot>> LoadSourceSnapshotsAsync(
        IErpDbContext db, IReadOnlyList<(string SourceType, long SourceId)> keys)
    {
        var result = new Dictionary<string, SourceSnapshot>(StringComparer.Ordinal);

        var orderIds = keys
            .Where(k => k.SourceType == AgencyServiceFeeStatementRules.SourceTypeSalesOrder)
            .Select(k => k.SourceId).Distinct().ToList();
        if (orderIds.Count > 0)
        {
            var orders = await db.SalesOrders.AsNoTracking()
                .Where(o => orderIds.Contains(o.Id)).ToListAsync();
            foreach (var order in orders)
            {
                result[SourceKey(AgencyServiceFeeStatementRules.SourceTypeSalesOrder, order.Id)] =
                    new SourceSnapshot(true, order.IsDeleted, (int)order.Status, order.CustomerId,
                        order.Currency.ToString(), order.OrderNo ?? string.Empty, order.OrderDate);
            }
        }

        var listIds = keys
            .Where(k => k.SourceType == AgencyServiceFeeStatementRules.SourceTypeLoadingList)
            .Select(k => k.SourceId).Distinct().ToList();
        if (listIds.Count > 0)
        {
            var lists = await db.ContainerLoadingLists.AsNoTracking()
                .Where(l => listIds.Contains(l.Id)).ToListAsync();
            foreach (var list in lists)
            {
                result[SourceKey(AgencyServiceFeeStatementRules.SourceTypeLoadingList, list.Id)] =
                    new SourceSnapshot(true, list.IsDeleted, (int)list.Status, list.CustomerId,
                        string.Empty, list.LoadingListNo ?? string.Empty, list.LoadingDate);
            }
        }

        foreach (var key in keys)
        {
            var mapKey = SourceKey(key.SourceType, key.SourceId);
            if (!result.ContainsKey(mapKey))
                result[mapKey] = new SourceSnapshot(false, false, 0, 0, string.Empty, string.Empty, default);
        }

        return result;
    }

    /// <summary>
    /// 有效身份唯一：同一「客户 + 规范化对账单号」在**未作废、未删除**记录内唯一；重复一律拒绝
    /// （不静默合并、不覆盖、不按金额或日期相似度匹配）；已作废记录保留可读但不占用身份。
    /// </summary>
    private static async Task EnsureIdentityAvailableAsync(
        IErpDbContext db, StatementInput input, long? excludeStatementId)
    {
        var excludedId = excludeStatementId ?? 0;
        var identity = AgencyServiceFeeStatementRules.IdentityText(input.StatementNo);

        var duplicate = await db.AgencyServiceFeeStatements.AsNoTracking()
            .Where(x => !x.IsDeleted
                        && x.Status != AgencyServiceFeeStatementRules.StatusVoided
                        && x.CustomerId == input.Customer.Id
                        && x.NormalizedStatementNo == input.NormalizedStatementNo
                        && x.Id != excludedId)
            .Select(x => new { x.Id, x.StatementDate, x.Status })
            .FirstOrDefaultAsync();

        if (duplicate is null) return;

        throw BusinessException.Duplicate(
            $"客户「{input.Customer.CustomerName}」已存在同一对账单号的未作废记录：{identity}"
            + $"（Id={duplicate.Id}，状态 {AgencyServiceFeeStatementRules.StatusText(duplicate.Status)}，"
            + $"对账日期 {duplicate.StatementDate:yyyy-MM-dd}）；重复对账单被拒绝而不是静默合并"
            + "（如需更正请先作废原记录再重新登记）");
    }

    /// <summary>
    /// 防「重复计费证据」：同一服务来源（来源类型 + 来源记录 Id）在**未作废、未删除**的行内**全局唯一**；
    /// 已作废对账单的行不占用来源身份（作废后该来源可重新被新对账单显式引用）。
    /// 一次查询装载相关来源的既有活跃行，绝无逐行数据库访问。
    /// </summary>
    private static async Task EnsureSourcesNotChargedAsync(
        IErpDbContext db, StatementInput input, long? excludeStatementId)
    {
        if (input.Lines.Count == 0) return;

        var sourceIds = input.Lines.Select(x => x.SourceId).Distinct().ToList();
        var excludedId = excludeStatementId ?? 0;

        var existing = await db.AgencyServiceFeeStatementLines.AsNoTracking()
            .Where(l => !l.IsDeleted
                        && l.Status != AgencyServiceFeeStatementRules.StatusVoided
                        && l.StatementId != excludedId
                        && sourceIds.Contains(l.SourceId))
            .Select(l => new { l.StatementId, l.SourceType, l.SourceId, l.SourceNo, l.Status })
            .ToListAsync();

        var byKey = existing
            .GroupBy(l => SourceKey(l.SourceType, l.SourceId), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        foreach (var line in input.Lines)
        {
            if (!byKey.TryGetValue(SourceKey(line.SourceType, line.SourceId), out var occupied)) continue;

            throw BusinessException.Duplicate(
                "服务来源已被另一张未作废对账单引用："
                + $"{AgencyServiceFeeStatementRules.SourceTypeText(line.SourceType)}"
                + $"「{occupied.SourceNo}」（对账单 Id={occupied.StatementId}，"
                + $"该行状态 {AgencyServiceFeeStatementRules.StatusText(occupied.Status)}）；"
                + "同一来源同时只能被一条未作废对账单行引用，重复计费证据被拒绝而不是合并"
                + "（如需更正请先作废原对账单）");
        }
    }

    /// <summary>写入对账单表头的**服务端权威字段**（客户与协议快照、规范化身份、服务端合计、草稿状态）</summary>
    private static void ApplyHeader(AgencyServiceFeeStatement statement, StatementInput input)
    {
        statement.StatementNo = input.StatementNo;
        statement.NormalizedStatementNo = input.NormalizedStatementNo;
        statement.CustomerId = input.Customer.Id;
        statement.CustomerCode = input.Customer.CustomerCode ?? string.Empty;
        statement.CustomerName = input.Customer.CustomerName ?? string.Empty;
        statement.Currency = input.Currency;
        statement.StatementDate = input.StatementDate;
        statement.DueDate = input.DueDate;
        statement.ServicePeriodFrom = input.ServicePeriodFrom;
        statement.ServicePeriodTo = input.ServicePeriodTo;
        statement.AgreementId = input.Agreement.Id;
        statement.AgreementNo = input.Agreement.AgreementNo ?? string.Empty;
        statement.AgreementCurrency = input.AgreementCurrency;
        statement.AgreementCustomerId = input.Agreement.CustomerId;
        statement.AgreementFeeMethod = (input.Agreement.FeeMethod ?? string.Empty).Trim();
        statement.AgreementTermsText = input.AgreementTermsText;
        statement.TotalAmount = input.TotalAmount;
        statement.Status = AgencyServiceFeeStatementRules.StatusDraft;
        statement.Remark = input.Remark;
    }

    /// <summary>构造行实体（行号、来源快照与金额全部取自服务端校验结果；草稿状态与表头一致）</summary>
    private static List<AgencyServiceFeeStatementLine> BuildLines(long statementId, StatementInput input)
    {
        var currency = string.IsNullOrEmpty(input.Currency) ? "CNY" : input.Currency;
        return input.Lines.Select(line => new AgencyServiceFeeStatementLine
        {
            StatementId = statementId,
            LineNo = line.LineNo,
            SourceType = line.SourceType,
            SourceId = line.SourceId,
            SourceNo = line.SourceNo,
            SourceDate = line.SourceDate,
            SourceStatus = line.SourceStatus,
            SourceStatusText = DocumentStatusText(line.SourceStatus),
            SourceCustomerId = line.SourceCustomerId,
            SourceCustomerCode = input.Customer.CustomerCode ?? string.Empty,
            SourceCustomerName = input.Customer.CustomerName ?? string.Empty,
            SourceCurrency = line.SourceCurrency,
            Description = line.Description,
            BasisQuantity = line.BasisQuantity,
            BasisNote = line.BasisNote,
            Amount = line.Amount,
            Currency = currency,
            Status = AgencyServiceFeeStatementRules.StatusDraft,
            Remark = line.Remark
        }).ToList();
    }

    /// <summary>按 Id 装载未删除对账单（不存在 / 已删除 → 数据不存在）</summary>
    private static async Task<AgencyServiceFeeStatement> LoadAsync(IErpDbContext db, long statementId)
    {
        if (statementId <= 0) throw BusinessException.InvalidParameter("请选择要操作的代理服务费对账单");
        return await db.AgencyServiceFeeStatements
                   .FirstOrDefaultAsync(x => x.Id == statementId && !x.IsDeleted)
               ?? throw BusinessException.NotFound($"代理服务费对账单证据（Id={statementId}）不存在或已删除");
    }

    /// <summary>对账单对外身份文案（提示与台账共用；与唯一性判定口径一致）</summary>
    private static string IdentityOf(AgencyServiceFeeStatement statement)
    {
        ArgumentNullException.ThrowIfNull(statement);
        return AgencyServiceFeeStatementRules.IdentityText(statement.StatementNo);
    }

    // ==================== 5. 映射（批量装载，绝无逐行数据库查询） ====================

    /// <summary>单条映射（单条操作与详情返回用；只读标注，不写库）</summary>
    private static async Task<AgencyServiceFeeStatementDto> MapAsync(
        IErpDbContext db, AgencyServiceFeeStatement statement, bool includeLines = false)
    {
        var mapped = await MapManyAsync(db, new List<AgencyServiceFeeStatement> { statement }, includeLines);
        return mapped[0];
    }

    /// <summary>
    /// 批量映射：客户、协议与行数各**一次查询**装载（详情另加一次有界行查询与两次来源存在性查询），
    /// 绝无逐行数据库查询。
    /// </summary>
    private static async Task<List<AgencyServiceFeeStatementDto>> MapManyAsync(
        IErpDbContext db, IReadOnlyList<AgencyServiceFeeStatement> statements, bool includeLines)
    {
        if (statements.Count == 0) return new List<AgencyServiceFeeStatementDto>();

        var customerIds = statements.Select(x => x.CustomerId).Distinct().ToList();
        var customers = (await db.BaseCustomers.AsNoTracking()
                .Where(c => customerIds.Contains(c.Id)).ToListAsync())
            .ToDictionary(c => c.Id);

        var agreementIds = statements.Select(x => x.AgreementId).Distinct().ToList();
        var agreements = (await db.AgencyServiceFeeAgreements.AsNoTracking()
                .Where(a => agreementIds.Contains(a.Id)).ToListAsync())
            .ToDictionary(a => a.Id);

        var statementIds = statements.Select(x => x.Id).ToList();
        var lineCounts = (await db.AgencyServiceFeeStatementLines.AsNoTracking()
                .Where(l => !l.IsDeleted && statementIds.Contains(l.StatementId))
                .GroupBy(l => l.StatementId)
                .Select(g => new { StatementId = g.Key, Count = g.Count() })
                .ToListAsync())
            .ToDictionary(x => x.StatementId, x => x.Count);

        var linesByStatement = new Dictionary<long, List<AgencyServiceFeeStatementLine>>();
        var sourceAvailability = new Dictionary<string, (bool Exists, bool Deleted)>(StringComparer.Ordinal);

        if (includeLines)
        {
            var lines = await db.AgencyServiceFeeStatementLines.AsNoTracking()
                .Where(l => !l.IsDeleted && statementIds.Contains(l.StatementId))
                .OrderBy(l => l.StatementId).ThenBy(l => l.LineNo).ThenBy(l => l.Id)
                .ToListAsync();

            linesByStatement = lines
                .GroupBy(l => l.StatementId)
                .ToDictionary(g => g.Key, g => g.ToList());

            sourceAvailability = await LoadSourceAvailabilityAsync(db, lines);
        }

        return statements
            .Select(statement => Map(statement, customers, agreements, lineCounts,
                linesByStatement, sourceAvailability))
            .ToList();
    }

    /// <summary>
    /// **批量**装载行的来源可用性（销售订单 / 装柜清单各一次查询）：
    /// 来源被软删除或已不存在时只做只读标注，历史行快照照常可读、绝不改派。
    /// </summary>
    private static async Task<Dictionary<string, (bool Exists, bool Deleted)>> LoadSourceAvailabilityAsync(
        IErpDbContext db, IReadOnlyList<AgencyServiceFeeStatementLine> lines)
    {
        var result = new Dictionary<string, (bool Exists, bool Deleted)>(StringComparer.Ordinal);

        var orderIds = lines
            .Where(l => l.SourceType == AgencyServiceFeeStatementRules.SourceTypeSalesOrder)
            .Select(l => l.SourceId).Distinct().ToList();
        if (orderIds.Count > 0)
        {
            var orders = await db.SalesOrders.AsNoTracking()
                .Where(o => orderIds.Contains(o.Id))
                .Select(o => new { o.Id, o.IsDeleted })
                .ToListAsync();
            foreach (var order in orders)
                result[SourceKey(AgencyServiceFeeStatementRules.SourceTypeSalesOrder, order.Id)] =
                    (true, order.IsDeleted);
        }

        var listIds = lines
            .Where(l => l.SourceType == AgencyServiceFeeStatementRules.SourceTypeLoadingList)
            .Select(l => l.SourceId).Distinct().ToList();
        if (listIds.Count > 0)
        {
            var lists = await db.ContainerLoadingLists.AsNoTracking()
                .Where(l => listIds.Contains(l.Id))
                .Select(l => new { l.Id, l.IsDeleted })
                .ToListAsync();
            foreach (var list in lists)
                result[SourceKey(AgencyServiceFeeStatementRules.SourceTypeLoadingList, list.Id)] =
                    (true, list.IsDeleted);
        }

        foreach (var line in lines)
        {
            var key = SourceKey(line.SourceType, line.SourceId);
            if (!result.ContainsKey(key)) result[key] = (false, false);
        }

        return result;
    }

    /// <summary>对账单实体 → DTO（含客户 / 协议可用性标注、行清单与同源口径文案；纯映射，不写库）</summary>
    private static AgencyServiceFeeStatementDto Map(
        AgencyServiceFeeStatement statement,
        Dictionary<long, BaseCustomer> customers,
        Dictionary<long, AgencyServiceFeeAgreement> agreements,
        Dictionary<long, int> lineCounts,
        Dictionary<long, List<AgencyServiceFeeStatementLine>> linesByStatement,
        Dictionary<string, (bool Exists, bool Deleted)> sourceAvailability)
    {
        ArgumentNullException.ThrowIfNull(statement);

        var currency = CurrencyAmountRules.NormalizeCurrency(statement.Currency);
        var decimals = CurrencyAmountRules.PrecisionOf(currency);

        customers.TryGetValue(statement.CustomerId, out var customer);
        var customerAvailable = customer is not null
            && AgencyServiceFeeStatementRules.IsCustomerSelectable(true, customer.IsDeleted, customer.Status);

        agreements.TryGetValue(statement.AgreementId, out var agreement);
        var agreementAvailable = agreement is not null
            && AgencyServiceFeeStatementRules.IsAgreementSelectable(agreement.Status, agreement.IsDeleted);

        linesByStatement.TryGetValue(statement.Id, out var lines);
        lineCounts.TryGetValue(statement.Id, out var lineCount);

        var lineDtos = lines is null
            ? new List<AgencyServiceFeeStatementLineDto>()
            : lines.Select(line => MapLine(line, currency, sourceAvailability)).ToList();

        return new AgencyServiceFeeStatementDto(
            statement.Id,
            statement.StatementNo ?? string.Empty,
            IdentityOf(statement),
            statement.CustomerId,
            statement.CustomerCode ?? string.Empty,
            statement.CustomerName ?? string.Empty,
            customerAvailable,
            AgencyServiceFeeStatementRules.CustomerAvailabilityText(
                customer is null ? null : customer.Status, customer?.IsDeleted ?? true),
            currency,
            decimals,
            statement.StatementDate,
            statement.DueDate,
            AgencyServiceFeeStatementRules.DueDateText(statement.DueDate),
            statement.ServicePeriodFrom,
            statement.ServicePeriodTo,
            AgencyServiceFeeStatementRules.ServicePeriodText(
                statement.ServicePeriodFrom, statement.ServicePeriodTo),
            statement.AgreementId,
            statement.AgreementNo ?? string.Empty,
            CurrencyAmountRules.NormalizeCurrency(statement.AgreementCurrency),
            (statement.AgreementFeeMethod ?? string.Empty).Trim(),
            statement.AgreementTermsText ?? string.Empty,
            agreementAvailable,
            AgencyServiceFeeStatementRules.AgreementAvailabilityText(
                agreement is null ? null : agreement.Status, agreement?.IsDeleted ?? true),
            statement.TotalAmount,
            AgencyServiceFeeStatementRules.AmountText(statement.TotalAmount, currency),
            lineDtos.Count > 0 ? lineDtos.Count : lineCount,
            statement.Status,
            AgencyServiceFeeStatementRules.StatusText(statement.Status),
            statement.Status == AgencyServiceFeeStatementRules.StatusDraft,
            statement.Status == AgencyServiceFeeStatementRules.StatusRecorded,
            statement.Status == AgencyServiceFeeStatementRules.StatusVoided,
            statement.RecordedAt,
            statement.RecordedBy ?? string.Empty,
            statement.VoidedAt,
            statement.VoidReason ?? string.Empty,
            statement.Remark ?? string.Empty,
            lineDtos,
            statement.CreatedAt,
            statement.UpdatedAt,
            AgencyServiceFeeStatementRules.SourceLinkRuleText,
            AgencyServiceFeeStatementRules.AmountRuleText,
            AgencyServiceFeeStatementRules.UniquenessRuleText,
            AgencyServiceFeeStatementRules.SeparationText,
            AgencyServiceFeeStatementRules.BoundaryText);
    }

    /// <summary>行实体 → DTO（含来源可用性与币种兼容标注、同源金额文案；纯映射，不写库）</summary>
    private static AgencyServiceFeeStatementLineDto MapLine(
        AgencyServiceFeeStatementLine line, string currency,
        Dictionary<string, (bool Exists, bool Deleted)> sourceAvailability)
    {
        ArgumentNullException.ThrowIfNull(line);

        var key = SourceKey(line.SourceType, line.SourceId);
        var (exists, deleted) = sourceAvailability.TryGetValue(key, out var availability)
            ? availability
            : (false, false);
        var sourceAvailable = exists && !deleted;
        var typeText = AgencyServiceFeeStatementRules.SourceTypeText(line.SourceType);
        var availabilityText = !exists
            ? $"{typeText}记录不存在（历史快照仍可读，不会改派）"
            : deleted
                ? $"{typeText}记录已删除（历史快照仍可读，不会改派）"
                : $"{typeText}记录可读（只读关联，未被本模块改写）";

        return new AgencyServiceFeeStatementLineDto(
            line.Id,
            line.StatementId,
            line.LineNo,
            (line.SourceType ?? string.Empty).Trim(),
            typeText,
            line.SourceId,
            line.SourceNo ?? string.Empty,
            AgencyServiceFeeStatementRules.SourceIdentityText(line.SourceType, line.SourceNo),
            line.SourceDate,
            line.SourceStatus,
            line.SourceStatusText ?? string.Empty,
            line.SourceCustomerId,
            line.SourceCustomerCode ?? string.Empty,
            line.SourceCustomerName ?? string.Empty,
            line.SourceCurrency ?? string.Empty,
            AgencyServiceFeeStatementRules.SourceCurrencyCompatibilityText(
                line.SourceType, line.SourceCurrency, currency),
            line.Description ?? string.Empty,
            line.BasisQuantity,
            line.BasisNote ?? string.Empty,
            line.Amount,
            currency,
            CurrencyAmountRules.PrecisionOf(currency),
            AgencyServiceFeeStatementRules.AmountText(line.Amount, currency),
            line.Status,
            AgencyServiceFeeStatementRules.StatusText(line.Status),
            line.Status == AgencyServiceFeeStatementRules.StatusDraft,
            line.Status == AgencyServiceFeeStatementRules.StatusRecorded,
            line.Status == AgencyServiceFeeStatementRules.StatusVoided,
            line.RecordedAt,
            line.RecordedBy ?? string.Empty,
            line.VoidedAt,
            line.VoidReason ?? string.Empty,
            line.Remark ?? string.Empty,
            sourceAvailable,
            availabilityText,
            AgencyServiceFeeStatementRules.AmountRuleText);
    }
}

