using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ERP.Application.Services;

/// <summary>
/// 客户收款单 → 代理服务费对账单 收款分摊证据服务（ERP-071）。职责：
/// <list type="number">
/// <item><b>登记分摊行</b>（<see cref="CreateAsync"/>）：按**持久化标识符**（对账单 Id + 收款单 Id）显式分摊，
/// 对账单必须存在、未删除且**已登记**，收款单必须存在、未删除且**未取消**，两者客户与币种必须一致；
/// 分摊金额按币种精度取整且大于 0，并不得超过收款单可分摊余额与对账单未分摊额；</item>
/// <item><b>作废分摊行</b>（<see cref="VoidAsync"/>）：必须填写原因，保留原始金额 / 快照 / 登记人与时间戳，
/// 不物理删除、不改派、不静默替换；</item>
/// <item><b>台账 / 详情 / 两侧汇总 / 两侧候选</b>（只读、有界）：批量装载，绝无逐行数据库查询。</item>
/// </list>
/// <para>证据维度分离（关键）：本服务只派生**本维度**（收款 → 代理服务费对账单）的已分摊与未分摊金额；
/// <strong>绝不</strong>把 ERP-053「收款单 → 销售订单」引用与 ERP-055「销项发票 → 销售订单」分摊的金额相加，
/// 也不把它们当作几张不同的收款单。</para>
/// <para>边界（重要）：本服务只读写 <c>AgencyServiceFeeCollectionAllocations</c> 一张表；<strong>不</strong>收款、
/// <strong>不</strong>付款、<strong>不</strong>记账或生成凭证 / 结算单、<strong>不</strong>核销、<strong>不</strong>催收或
/// 联系客户、<strong>不</strong>调用任何外部服务，也<strong>不</strong>改写收款单（含状态 / 金额 / 币种 / 付款方式 /
/// 银行账户 / 备注）、对账单证据（含合计 / 状态 / 登记人）、ERP-069 协议证据、客户主数据、销售订单、
/// 装柜与装柜清单、单证、发票、库存与库存成本、库存流水、费用与退税、结算与余额记录。</para>
/// </summary>
public static class AgencyServiceFeeCollectionAllocationService
{
    /// <summary>可分摊收款单候选单次返回上限（有界，避免一次拉全表）</summary>
    public const int MaxReceiptCandidates = AgencyServiceFeeCollectionAllocationRules.MaxReceiptCandidates;

    /// <summary>可承接分摊的对账单候选单次返回上限（有界，避免一次拉全表）</summary>
    public const int MaxStatementCandidates = AgencyServiceFeeCollectionAllocationRules.MaxStatementCandidates;

    /// <summary>汇总明细单次返回上限（有界：一次读取不允许无界行数）</summary>
    public const int MaxDetailsPerSummary = 100;

    // ==================== 1. 登记分摊行（新增一条证据） ====================

    /// <summary>
    /// 登记一条收款分摊行：全部校验通过后才写一行证据，并写入对账单 / 收款单 / 客户的服务端快照；
    /// 登记人由服务端按已认证身份写入（<paramref name="allocatedBy"/>，客户端不能提交该值）。
    /// <para>校验顺序：对账单可用（未删除 → 不存在；草稿 / 已作废 → 拒绝）→ 收款单可用（未删除 → 不存在；
    /// 已取消 → 拒绝）→ 币种口径 → 客户与币种一致性 → 分摊金额（精度 + 大于 0）→ 备注 →
    /// 两侧有效行数上限 → 重复有效行 → 收款单可分摊余额 → 对账单未分摊额。</para>
    /// </summary>
    public static async Task<AgencyServiceFeeCollectionAllocationDto> CreateAsync(
        IErpDbContext db, AgencyServiceFeeCollectionAllocationSaveDto dto, string? allocatedBy)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(dto);

        if (dto.StatementId <= 0)
            throw BusinessException.InvalidParameter(
                "请显式选择要分摊的代理服务费对账单证据（对账单 Id 必须由用户显式选择，"
                + "系统不按单号文本、金额或相似度匹配对账单）");
        if (dto.ReceiptId <= 0)
            throw BusinessException.InvalidParameter(
                "请显式选择要分摊的客户收款单（收款单 Id 必须由用户显式选择，"
                + "系统不按单号文本、金额或日期相似度匹配收款单）");

        var statement = await LoadStatementAsync(db, dto.StatementId);
        var receipt = await LoadReceiptAsync(db, dto.ReceiptId);
        var statementCurrency = AgencyServiceFeeCollectionAllocationRules.NormalizeCurrencyStrict(statement.Currency);
        var receiptCurrency = AgencyServiceFeeCollectionAllocationRules.NormalizeCurrencyStrict(
            receipt.Currency.ToString());

        // 权威资格：对账单必须已登记；收款单必须未取消（草稿 / 已作废 / 已取消一律拒绝）
        AgencyServiceFeeCollectionAllocationRules.EnsureStatementAllocatable(
            statement, allocatedAmount: 0m,
            AgencyServiceFeeCollectionAllocationRules.IdentityText(statement.StatementNo));
        AgencyServiceFeeCollectionAllocationRules.EnsureReceiptAllocatable(
            receipt, allocatedAmount: 0m, receipt.ReceiptNo);
        AgencyServiceFeeCollectionAllocationRules.EnsureCompatible(receipt, receiptCurrency, statement);

        var amount = AgencyServiceFeeCollectionAllocationRules.NormalizeAllocationAmount(
            dto.AllocatedAmount, statementCurrency);
        var remark = AgencyServiceFeeCollectionAllocationRules.NormalizeRemark(dto.Remark);

        // 一次装载两侧**有效行**（本模块把单侧有效行限制在 50 条内，故装载量有界且与页大小无关）
        var activeRows = await LoadActiveRowsAsync(db, statement.Id, receipt.Id);
        var statementAllocated = activeRows
            .Where(a => a.StatementId == statement.Id).Sum(a => a.AllocatedAmount);
        var receiptAllocated = activeRows
            .Where(a => a.ReceiptId == receipt.Id).Sum(a => a.AllocatedAmount);

        EnsureWithinLimit(
            activeRows.Count(a => a.StatementId == statement.Id),
            activeRows.Count(a => a.ReceiptId == receipt.Id),
            statement, receipt);

        var duplicate = activeRows.FirstOrDefault(a =>
            a.StatementId == statement.Id && a.ReceiptId == receipt.Id);
        if (duplicate is not null)
            throw BusinessException.Duplicate(
                "同一对账单与收款单只允许一条有效分摊行："
                + AgencyServiceFeeCollectionAllocationRules.AllocationIdentityText(statement.StatementNo, receipt.ReceiptNo)
                + $" 已存在有效分摊行（Id={duplicate.Id}，金额 {duplicate.AllocatedAmount} {statementCurrency}）；"
                + "重复提交被拒绝而不是合并或覆盖（如需更正请先作废原分摊行）");

        var receiptAvailable = AgencyServiceFeeCollectionAllocationRules.AuthoritativeAmount(
            receipt.Amount, receiptCurrency) - receiptAllocated;
        if (amount > receiptAvailable)
            throw BusinessException.RuleConflict(
                $"收款单「{receipt.ReceiptNo}」的可分摊余额为 {receiptAvailable} {statementCurrency}"
                + $"（收款金额已按币种精度取整，本维度已分摊 {receiptAllocated}），"
                + $"不能分摊 {amount} {statementCurrency}：系统不做超额分摊、不自动调整差额，"
                + "也不把差额猜测到别的记录");

        var statementUnallocated = AgencyServiceFeeCollectionAllocationRules.AuthoritativeAmount(
            statement.TotalAmount, statementCurrency) - statementAllocated;
        if (amount > statementUnallocated)
            throw BusinessException.RuleConflict(
                $"对账单「{statement.StatementNo}」的未分摊额为 {statementUnallocated} {statementCurrency}"
                + $"（对账单服务端合计已按币种精度取整，本维度已分摊 {statementAllocated}），"
                + $"不能分摊 {amount} {statementCurrency}：系统不做超额分摊、不自动调整差额，"
                + "也不把差额猜测到别的收款单");

        var customer = await LoadCustomerAsync(db, statement.CustomerId);
        if (customer.Status != 1)
            throw BusinessException.RuleConflict(
                $"客户「{customer.CustomerName}」已停用：停用客户不能登记新的收款分摊"
                + "（历史分摊保持可读，不因停用被改写或删除）");
        var now = DateTime.Now;

        var allocation = new AgencyServiceFeeCollectionAllocation
        {
            StatementId = statement.Id,
            StatementNo = statement.StatementNo ?? string.Empty,
            StatementDate = statement.StatementDate,
            StatementStatus = statement.Status,
            StatementStatusText = AgencyServiceFeeCollectionAllocationRules.StatementStatusText(statement.Status),
            StatementTotalAmount = AgencyServiceFeeCollectionAllocationRules.AuthoritativeAmount(
                statement.TotalAmount, statementCurrency),
            StatementCurrency = statementCurrency,
            StatementAgreementId = statement.AgreementId,
            StatementAgreementNo = statement.AgreementNo ?? string.Empty,
            ReceiptId = receipt.Id,
            ReceiptNo = receipt.ReceiptNo ?? string.Empty,
            ReceiptDate = receipt.ReceiptDate,
            ReceiptStatus = (int)receipt.Status,
            ReceiptStatusText = AgencyServiceFeeCollectionAllocationRules.ReceiptStatusText((int)receipt.Status),
            ReceiptAmount = AgencyServiceFeeCollectionAllocationRules.AuthoritativeAmount(
                receipt.Amount, receiptCurrency),
            CustomerId = statement.CustomerId,
            CustomerCode = customer.CustomerCode ?? string.Empty,
            CustomerName = customer.CustomerName ?? string.Empty,
            AllocatedAmount = amount,
            Currency = statementCurrency,
            Remark = remark,
            Status = AgencyServiceFeeCollectionAllocationRules.StatusActive,
            AllocatedAt = now,
            AllocatedBy = AgencyServiceFeeCollectionAllocationRules.NormalizeAllocatedBy(allocatedBy)
        };

        db.AgencyServiceFeeCollectionAllocations.Add(allocation);
        await db.SaveChangesAsync();

        return Map(allocation,
            new Dictionary<long, FinanceReceipt> { [receipt.Id] = receipt },
            new Dictionary<long, AgencyServiceFeeStatement> { [statement.Id] = statement });
    }

    // ==================== 2. 作废分摊行（证据保留） ====================

    /// <summary>
    /// 作废一条收款分摊行（有效 → 已作废）：必须填写作废原因；<strong>保留</strong>原始分摊金额、对账单与
    /// 收款单快照、客户快照、登记人与时间戳，不物理删除、不改派、不改写原始金额，也不产生任何收款 / 记账 /
    /// 核销 / 结算 / 催收动作；作废后该组合可重新登记一条新的有效分摊行（新旧并存可查）；重复作废被拒绝。
    /// </summary>
    public static async Task<AgencyServiceFeeCollectionAllocationDto> VoidAsync(
        IErpDbContext db, long allocationId, string? reason)
    {
        ArgumentNullException.ThrowIfNull(db);
        var allocation = await LoadAllocationAsync(db, allocationId);
        var identity = AgencyServiceFeeCollectionAllocationRules.AllocationIdentityText(
            allocation.StatementNo, allocation.ReceiptNo);
        AgencyServiceFeeCollectionAllocationRules.EnsureVoidable(allocation.Status, identity);

        var reasonText = AgencyServiceFeeCollectionAllocationRules.NormalizeVoidReason(reason);
        var now = DateTime.Now;
        allocation.Status = AgencyServiceFeeCollectionAllocationRules.StatusVoided;
        allocation.VoidedAt = now;
        allocation.VoidReason = reasonText;
        allocation.UpdatedAt = now;

        await db.SaveChangesAsync();

        return await MapOneAsync(db, allocation);
    }

    // ==================== 3. 台账 / 详情 / 两侧汇总 / 两侧候选（只读、有界） ====================

    /// <summary>分摊行详情（含对账单与收款单可用性标注；只读）</summary>
    public static async Task<AgencyServiceFeeCollectionAllocationDto> GetAsync(IErpDbContext db, long allocationId)
    {
        ArgumentNullException.ThrowIfNull(db);
        var allocation = await LoadAllocationAsync(db, allocationId);
        return await MapOneAsync(db, allocation);
    }

    /// <summary>
    /// 台账分页查询（只读）：支持对账单 / 收款单 / 客户 / 状态 / 币种 / 登记时间区间 / 关键字过滤；
    /// 默认包含已作废历史（证据保留可读）。页内对账单与收款单**一次批量装载**（无逐行数据库查询）。
    /// </summary>
    public static async Task<PagedResult<AgencyServiceFeeCollectionAllocationDto>> ListAsync(
        IErpDbContext db, AgencyServiceFeeCollectionAllocationQuery query)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(query);
        query.Normalize();

        var status = AgencyServiceFeeCollectionAllocationRules.NormalizeStatusFilter(query.Status);
        var currency = string.IsNullOrWhiteSpace(query.Currency)
            ? null
            : AgencyServiceFeeCollectionAllocationRules.NormalizeCurrencyStrict(query.Currency);
        var keyword = AgencyServiceFeeCollectionAllocationRules.NormalizeKeyword(query.Keyword);

        var source = db.AgencyServiceFeeCollectionAllocations.AsNoTracking().Where(x => !x.IsDeleted);
        if (query.StatementId is not null) source = source.Where(x => x.StatementId == query.StatementId.Value);
        if (query.ReceiptId is not null) source = source.Where(x => x.ReceiptId == query.ReceiptId.Value);
        if (query.CustomerId is not null) source = source.Where(x => x.CustomerId == query.CustomerId.Value);
        if (status is not null) source = source.Where(x => x.Status == status.Value);
        if (currency is not null) source = source.Where(x => x.Currency == currency);
        if (query.AllocatedDateFrom is not null)
            source = source.Where(x => x.AllocatedAt >= query.AllocatedDateFrom.Value.Date);
        if (query.AllocatedDateTo is not null)
            source = source.Where(x => x.AllocatedAt < query.AllocatedDateTo.Value.Date.AddDays(1));
        if (keyword.Length > 0)
        {
            source = source.Where(x => x.StatementNo.Contains(keyword)
                || x.ReceiptNo.Contains(keyword)
                || x.CustomerName.Contains(keyword)
                || x.CustomerCode.Contains(keyword)
                || x.StatementAgreementNo.Contains(keyword)
                || x.Remark.Contains(keyword));
        }

        var total = await source.CountAsync();
        var page = await source
            .OrderByDescending(x => x.AllocatedAt)
            .ThenByDescending(x => x.Id)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .ToListAsync();

        return new PagedResult<AgencyServiceFeeCollectionAllocationDto>
        {
            Items = await MapManyAsync(db, page),
            Total = total,
            Page = query.Page,
            PageSize = query.PageSize
        };
    }

    /// <summary>
    /// 指定对账单的分摊行清单（只读、有界；对账单详情工作流用）：默认返回全部状态（含已作废历史），
    /// status 传 1 只看有效 / 传 2 只看已作废。
    /// </summary>
    public static async Task<List<AgencyServiceFeeCollectionAllocationDto>> ListForStatementAsync(
        IErpDbContext db, long statementId, int? status, int take)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (statementId <= 0)
            throw BusinessException.InvalidParameter("请选择要查看分摊行的代理服务费对账单证据");

        var statusFilter = AgencyServiceFeeCollectionAllocationRules.NormalizeStatusFilter(status);
        var size = Math.Clamp(
            take <= 0 ? AgencyServiceFeeCollectionAllocationRules.MaxAllocationsPerStatement : take,
            1, MaxDetailsPerSummary);

        var source = db.AgencyServiceFeeCollectionAllocations.AsNoTracking()
            .Where(a => !a.IsDeleted && a.StatementId == statementId);
        if (statusFilter is not null) source = source.Where(a => a.Status == statusFilter.Value);

        var rows = await source
            .OrderBy(a => a.Status)
            .ThenBy(a => a.AllocatedAt)
            .ThenBy(a => a.Id)
            .Take(size)
            .ToListAsync();

        return await MapManyAsync(db, rows);
    }

    /// <summary>
    /// 指定收款单的分摊行清单（只读、有界；收款单工作流用）：默认返回全部状态（含已作废历史）。
    /// </summary>
    public static async Task<List<AgencyServiceFeeCollectionAllocationDto>> ListForReceiptAsync(
        IErpDbContext db, long receiptId, int? status, int take)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (receiptId <= 0)
            throw BusinessException.InvalidParameter("请选择要查看分摊行的客户收款单");

        var statusFilter = AgencyServiceFeeCollectionAllocationRules.NormalizeStatusFilter(status);
        var size = Math.Clamp(
            take <= 0 ? AgencyServiceFeeCollectionAllocationRules.MaxAllocationsPerReceipt : take,
            1, MaxDetailsPerSummary);

        var source = db.AgencyServiceFeeCollectionAllocations.AsNoTracking()
            .Where(a => !a.IsDeleted && a.ReceiptId == receiptId);
        if (statusFilter is not null) source = source.Where(a => a.Status == statusFilter.Value);

        var rows = await source
            .OrderBy(a => a.Status)
            .ThenBy(a => a.AllocatedAt)
            .ThenBy(a => a.Id)
            .Take(size)
            .ToListAsync();

        return await MapManyAsync(db, rows);
    }

    /// <summary>
    /// 对账单侧汇总（只读派生）：对账单快照 + **本维度**有效分摊金额 / 未分摊额、有效行数与已作废行数 +
    /// 有界逐行明细。
    /// <para>统计口径：有效分摊金额只按 <c>Status = 有效</c> 的持久化行合计（已作废历史永不并入有效合计，
    /// 但单独计数并列出）；未分摊额下限 0。本方法<strong>不写库</strong>、不改写对账单与收款单，
    /// 也不把结果表述为已收款金额、已结清、逾期、收入确认或记账状态。</para>
    /// </summary>
    public static async Task<AgencyServiceFeeCollectionAllocationStatementSummaryDto> GetStatementSummaryAsync(
        IErpDbContext db, long statementId)
    {
        ArgumentNullException.ThrowIfNull(db);
        var statement = await LoadStatementAsync(db, statementId);

        var currency = CurrencyAmountRules.NormalizeCurrency(statement.Currency);
        var total = AgencyServiceFeeCollectionAllocationRules.AuthoritativeAmount(
            statement.TotalAmount, currency);

        // 一次分组统计取回有效 / 已作废的行数与金额（不逐行查库）
        var stats = await db.AgencyServiceFeeCollectionAllocations.AsNoTracking()
            .Where(a => !a.IsDeleted && a.StatementId == statement.Id)
            .GroupBy(a => a.Status)
            .Select(g => new { Status = g.Key, Count = g.Count(), Amount = g.Sum(a => a.AllocatedAmount) })
            .ToListAsync();

        var active = stats
            .Where(s => s.Status == AgencyServiceFeeCollectionAllocationRules.StatusActive).ToList();
        var allocated = active.Sum(s => s.Amount);
        var allocationCount = active.Sum(s => s.Count);
        var voidedCount = stats
            .Where(s => s.Status == AgencyServiceFeeCollectionAllocationRules.StatusVoided)
            .Sum(s => s.Count);
        var unallocated = total - allocated;
        if (unallocated < 0) unallocated = 0;

        var rows = await ListForStatementAsync(db, statement.Id, null, MaxDetailsPerSummary);
        var statementAvailable = AgencyServiceFeeCollectionAllocationRules
            .EvaluateStatementEligibility(statement, allocated).Eligible;

        return new AgencyServiceFeeCollectionAllocationStatementSummaryDto(
            statement.Id,
            statement.StatementNo ?? string.Empty,
            AgencyServiceFeeCollectionAllocationRules.IdentityText(statement.StatementNo),
            statement.StatementDate,
            statement.Status,
            AgencyServiceFeeCollectionAllocationRules.StatementStatusText(statement.Status),
            statement.CustomerId,
            statement.CustomerCode ?? string.Empty,
            statement.CustomerName ?? string.Empty,
            currency,
            CurrencyAmountRules.PrecisionOf(currency),
            total,
            allocated,
            unallocated,
            allocationCount,
            voidedCount,
            AgencyServiceFeeCollectionAllocationRules.LinkageStatusOf(total, allocated),
            AgencyServiceFeeCollectionAllocationRules.LinkageText(
                total, allocated, allocationCount, currency, "对账单在本维度"),
            statementAvailable,
            AgencyServiceFeeCollectionAllocationRules.StatementAvailabilityText(statement),
            AgencyServiceFeeCollectionAllocationRules.RuleText,
            AgencyServiceFeeCollectionAllocationRules.AmountRuleText,
            AgencyServiceFeeCollectionAllocationRules.DimensionSeparationText,
            AgencyServiceFeeCollectionAllocationRules.BoundaryText,
            rows);
    }

    /// <summary>
    /// 收款单侧汇总（只读派生）：收款单快照 + **本维度**有效分摊金额 / 可分摊余额、有效行数与已作废行数 +
    /// 有界逐行明细。
    /// <para>统计口径：有效分摊金额只按 <c>Status = 有效</c> 的持久化行合计（已作废历史永不并入有效合计，
    /// 但单独计数并列出）；可分摊余额下限 0，且<strong>只属于本维度</strong>（不与 ERP-053 销售订单收款引用
    /// 相加），<strong>不是</strong>银行未到账金额、应收账款余额或客户欠款。</para>
    /// </summary>
    public static async Task<AgencyServiceFeeCollectionAllocationReceiptSummaryDto> GetReceiptSummaryAsync(
        IErpDbContext db, long receiptId)
    {
        ArgumentNullException.ThrowIfNull(db);
        var receipt = await LoadReceiptAsync(db, receiptId);

        var currency = CurrencyAmountRules.NormalizeCurrency(receipt.Currency.ToString());
        var amount = AgencyServiceFeeCollectionAllocationRules.AuthoritativeAmount(receipt.Amount, currency);

        var stats = await db.AgencyServiceFeeCollectionAllocations.AsNoTracking()
            .Where(a => !a.IsDeleted && a.ReceiptId == receipt.Id)
            .GroupBy(a => a.Status)
            .Select(g => new { Status = g.Key, Count = g.Count(), Amount = g.Sum(a => a.AllocatedAmount) })
            .ToListAsync();

        var active = stats
            .Where(s => s.Status == AgencyServiceFeeCollectionAllocationRules.StatusActive).ToList();
        var allocated = active.Sum(s => s.Amount);
        var allocationCount = active.Sum(s => s.Count);
        var voidedCount = stats
            .Where(s => s.Status == AgencyServiceFeeCollectionAllocationRules.StatusVoided)
            .Sum(s => s.Count);
        var unallocated = amount - allocated;
        if (unallocated < 0) unallocated = 0;

        var rows = await ListForReceiptAsync(db, receipt.Id, null, MaxDetailsPerSummary);
        var customer = await LoadCustomerAsync(db, receipt.CustomerId);

        return new AgencyServiceFeeCollectionAllocationReceiptSummaryDto(
            receipt.Id,
            receipt.ReceiptNo ?? string.Empty,
            receipt.ReceiptDate,
            (int)receipt.Status,
            AgencyServiceFeeCollectionAllocationRules.ReceiptStatusText((int)receipt.Status),
            receipt.CustomerId,
            customer.CustomerCode ?? string.Empty,
            customer.CustomerName ?? string.Empty,
            currency,
            CurrencyAmountRules.PrecisionOf(currency),
            amount,
            allocated,
            unallocated,
            allocationCount,
            voidedCount,
            AgencyServiceFeeCollectionAllocationRules.LinkageStatusOf(amount, allocated),
            AgencyServiceFeeCollectionAllocationRules.LinkageText(
                amount, allocated, allocationCount, currency, "收款单在本维度"),
            AgencyServiceFeeCollectionAllocationRules.EvaluateReceiptEligibility(receipt, allocated).Eligible,
            AgencyServiceFeeCollectionAllocationRules.ReceiptAvailabilityText(receipt),
            AgencyServiceFeeCollectionAllocationRules.RuleText,
            AgencyServiceFeeCollectionAllocationRules.AmountRuleText,
            AgencyServiceFeeCollectionAllocationRules.DimensionSeparationText,
            AgencyServiceFeeCollectionAllocationRules.BoundaryText,
            rows);
    }

    // ==================== 4. 候选读取（只读、有界，绝不写库） ====================

    /// <summary>
    /// 可分摊收款单候选（只读、有界）：必须显式给出客户与币种（资格判定依赖它们），单次最多
    /// <see cref="MaxReceiptCandidates"/> 条；只列出**未删除且未取消**的收款单，附 **本维度** 已分摊金额、
    /// 可分摊余额与资格文案（已占满 → 不可分摊）。
    /// <para>刻意**不与** ERP-053 的销售订单收款引用合并：可分摊余额只扣减本登记册的有效分摊行。</para>
    /// </summary>
    public static async Task<List<AgencyServiceFeeCollectionAllocationReceiptCandidateDto>> ListReceiptCandidatesAsync(
        IErpDbContext db, long customerId, string? currency, string? keyword, int take)
    {
        ArgumentNullException.ThrowIfNull(db);

        if (customerId <= 0)
            throw BusinessException.InvalidParameter(
                "请先选择客户：收款单候选的资格判定依赖客户（系统不按相似度推荐收款单）");
        if (string.IsNullOrWhiteSpace(currency))
            throw BusinessException.InvalidParameter(
                "请先选择对账单币种：收款单候选的资格判定依赖币种（系统不替收款单补一个币种）");

        var normalizedCurrency = AgencyServiceFeeCollectionAllocationRules.NormalizeCurrencyStrict(currency);
        var currencyValue = Enum.Parse<Currency>(normalizedCurrency);
        var key = AgencyServiceFeeCollectionAllocationRules.NormalizeKeyword(keyword);
        var limit = Math.Clamp(take <= 0 ? MaxReceiptCandidates : take, 1, MaxReceiptCandidates);

        var customer = await LoadCustomerAsync(db, customerId);

        var receipts = db.FinanceReceipts.AsNoTracking()
            .Where(r => !r.IsDeleted
                        && r.CustomerId == customerId
                        && r.Currency == currencyValue
                        && r.Status != DocumentStatus.Cancelled);
        if (key.Length > 0) receipts = receipts.Where(r => r.ReceiptNo.Contains(key));
        var rows = await receipts
            .OrderByDescending(r => r.ReceiptDate).ThenByDescending(r => r.Id)
            .Take(limit)
            .ToListAsync();

        var totals = await LoadActiveTotalsForReceiptsAsync(db, rows.Select(r => r.Id).ToList());

        return rows.Select(receipt =>
        {
            var allocated = totals.TryGetValue(receipt.Id, out var value) ? value.Amount : 0m;
            var count = totals.TryGetValue(receipt.Id, out var value2) ? value2.Count : 0;
            var receivedTotal = AgencyServiceFeeCollectionAllocationRules.AuthoritativeAmount(
                receipt.Amount, normalizedCurrency);
            var remaining = receivedTotal - allocated;
            if (remaining < 0) remaining = 0;
            var (eligible, text) = AgencyServiceFeeCollectionAllocationRules.EvaluateReceiptEligibility(
                receipt, allocated);

            return new AgencyServiceFeeCollectionAllocationReceiptCandidateDto(
                receipt.Id,
                receipt.ReceiptNo ?? string.Empty,
                receipt.ReceiptDate,
                (int)receipt.Status,
                AgencyServiceFeeCollectionAllocationRules.ReceiptStatusText((int)receipt.Status),
                receipt.CustomerId,
                customer.CustomerCode ?? string.Empty,
                customer.CustomerName ?? string.Empty,
                normalizedCurrency,
                CurrencyAmountRules.PrecisionOf(normalizedCurrency),
                receivedTotal,
                allocated,
                remaining,
                count,
                eligible,
                text);
        }).ToList();
    }

    /// <summary>
    /// 可承接收款分摊的对账单候选（只读、有界）：必须显式给出客户与币种，单次最多
    /// <see cref="MaxStatementCandidates"/> 条；只列出**未删除且已登记**的对账单证据
    /// （草稿 / 已作废不静默当作可分摊），附 **本维度** 已分摊金额、未分摊额与资格文案。
    /// </summary>
    public static async Task<List<AgencyServiceFeeCollectionAllocationStatementCandidateDto>> ListStatementCandidatesAsync(
        IErpDbContext db, long customerId, string? currency, string? keyword, int take)
    {
        ArgumentNullException.ThrowIfNull(db);

        if (customerId <= 0)
            throw BusinessException.InvalidParameter(
                "请先选择客户：对账单候选的资格判定依赖客户（系统不按相似度推荐对账单）");
        if (string.IsNullOrWhiteSpace(currency))
            throw BusinessException.InvalidParameter(
                "请先选择币种：对账单候选的资格判定依赖币种（系统不替对账单补一个币种）");

        var normalizedCurrency = AgencyServiceFeeCollectionAllocationRules.NormalizeCurrencyStrict(currency);
        var key = AgencyServiceFeeCollectionAllocationRules.NormalizeKeyword(keyword);
        var limit = Math.Clamp(take <= 0 ? MaxStatementCandidates : take, 1, MaxStatementCandidates);

        var customer = await LoadCustomerAsync(db, customerId);

        var statements = db.AgencyServiceFeeStatements.AsNoTracking()
            .Where(x => !x.IsDeleted
                        && x.Status == AgencyServiceFeeStatementRules.StatusRecorded
                        && x.CustomerId == customerId
                        && x.Currency == normalizedCurrency);
        if (key.Length > 0)
            statements = statements.Where(x => x.StatementNo.Contains(key) || x.AgreementNo.Contains(key));
        var rows = await statements
            .OrderByDescending(x => x.StatementDate).ThenByDescending(x => x.Id)
            .Take(limit)
            .ToListAsync();

        var totals = await LoadActiveTotalsForStatementsAsync(db, rows.Select(x => x.Id).ToList());

        return rows.Select(statement =>
        {
            var allocated = totals.TryGetValue(statement.Id, out var total) ? total.Amount : 0m;
            var count = totals.TryGetValue(statement.Id, out var total2) ? total2.Count : 0;
            var statementTotal = AgencyServiceFeeCollectionAllocationRules.AuthoritativeAmount(
                statement.TotalAmount, normalizedCurrency);
            var remaining = statementTotal - allocated;
            if (remaining < 0) remaining = 0;
            var (eligible, text) = AgencyServiceFeeCollectionAllocationRules.EvaluateStatementEligibility(
                statement, allocated);

            return new AgencyServiceFeeCollectionAllocationStatementCandidateDto(
                statement.Id,
                statement.StatementNo ?? string.Empty,
                AgencyServiceFeeCollectionAllocationRules.IdentityText(statement.StatementNo),
                statement.StatementDate,
                statement.Status,
                AgencyServiceFeeCollectionAllocationRules.StatementStatusText(statement.Status),
                statement.CustomerId,
                customer.CustomerCode ?? string.Empty,
                customer.CustomerName ?? string.Empty,
                normalizedCurrency,
                CurrencyAmountRules.PrecisionOf(normalizedCurrency),
                statementTotal,
                allocated,
                remaining,
                count,
                eligible,
                text);
        }).ToList();
    }

    /// <summary>
    /// 模块元数据（只读）：支持币种、有界额度与接口 / 界面 / 文档同源的口径文案
    /// （分摊 / 金额 / 唯一性 / 证据维度分离 / 历史只读 / 模块边界），避免前端硬编码造成口径漂移。
    /// </summary>
    public static AgencyServiceFeeCollectionAllocationMetadataDto GetMetadata()
        => new(
            AgencyServiceFeeCollectionAllocationRules.SupportedCurrencies.ToList(),
            AgencyServiceFeeCollectionAllocationRules.MaxAllocationsPerStatement,
            AgencyServiceFeeCollectionAllocationRules.MaxAllocationsPerReceipt,
            MaxReceiptCandidates,
            MaxStatementCandidates,
            AgencyServiceFeeCollectionAllocationQuery.MaxPageSize,
            AgencyServiceFeeCollectionAllocationRules.RuleText,
            AgencyServiceFeeCollectionAllocationRules.AmountRuleText,
            AgencyServiceFeeCollectionAllocationRules.UniquenessRuleText,
            AgencyServiceFeeCollectionAllocationRules.DimensionSeparationText,
            AgencyServiceFeeCollectionAllocationRules.HistoricalEvidenceText,
            AgencyServiceFeeCollectionAllocationRules.BoundaryText);

    // ==================== 5. 装载、限额与映射（内部） ====================

    /// <summary>按 Id 装载未删除的对账单证据（不存在 / 已删除 → 数据不存在；只读快照用途）</summary>
    private static async Task<AgencyServiceFeeStatement> LoadStatementAsync(IErpDbContext db, long statementId)
    {
        if (statementId <= 0)
            throw BusinessException.InvalidParameter("请选择要分摊的代理服务费对账单证据");
        return await db.AgencyServiceFeeStatements.AsNoTracking()
                   .FirstOrDefaultAsync(x => x.Id == statementId && !x.IsDeleted)
               ?? throw BusinessException.NotFound(
                   $"代理服务费对账单证据（Id={statementId}）不存在或已删除，不能登记收款分摊");
    }

    /// <summary>按 Id 装载未删除的收款单（不存在 / 已删除 → 数据不存在；只读快照用途）</summary>
    private static async Task<FinanceReceipt> LoadReceiptAsync(IErpDbContext db, long receiptId)
    {
        if (receiptId <= 0)
            throw BusinessException.InvalidParameter("请选择要分摊的客户收款单");
        return await db.FinanceReceipts.AsNoTracking()
                   .FirstOrDefaultAsync(r => r.Id == receiptId && !r.IsDeleted)
               ?? throw BusinessException.NotFound(
                   $"客户收款单（Id={receiptId}）不存在或已删除，不能登记收款分摊");
    }

    /// <summary>按 Id 装载未删除的分摊行（**跟踪**实体：作废需要写回；不存在 / 已删除 → 数据不存在）</summary>
    private static async Task<AgencyServiceFeeCollectionAllocation> LoadAllocationAsync(
        IErpDbContext db, long allocationId)
    {
        if (allocationId <= 0)
            throw BusinessException.InvalidParameter("请选择要操作的收款分摊行");
        return await db.AgencyServiceFeeCollectionAllocations
                   .FirstOrDefaultAsync(a => a.Id == allocationId && !a.IsDeleted)
               ?? throw BusinessException.NotFound($"收款分摊行（Id={allocationId}）不存在或已删除");
    }

    /// <summary>按 Id 装载未删除的客户（不存在 / 已删除 → 数据不存在；客户停用只影响新登记，历史证据照常可读）</summary>
    private static async Task<BaseCustomer> LoadCustomerAsync(IErpDbContext db, long customerId)
        => await db.BaseCustomers.AsNoTracking()
               .FirstOrDefaultAsync(c => c.Id == customerId && !c.IsDeleted)
           ?? throw BusinessException.NotFound(
               $"客户（Id={customerId}）不存在或已删除，不能登记收款分摊");

    /// <summary>
    /// 一次装载「该对账单 + 该收款单」的全部**有效行**（单次数据集访问，绝无逐行查库）：
    /// 用于重复行判定、两侧有效行数上限与两侧已分摊金额（本模块把单侧有效行限制在 50 条内，故装载量有界）。
    /// </summary>
    private static async Task<List<AgencyServiceFeeCollectionAllocation>> LoadActiveRowsAsync(
        IErpDbContext db, long statementId, long receiptId)
        => await db.AgencyServiceFeeCollectionAllocations.AsNoTracking()
            .Where(a => !a.IsDeleted
                        && a.Status == AgencyServiceFeeCollectionAllocationRules.StatusActive
                        && (a.StatementId == statementId || a.ReceiptId == receiptId))
            .ToListAsync();

    /// <summary>有效行数上限校验（单条对账单 / 单张收款单各自有界；超限拒绝并给出可读原因，不静默截断）</summary>
    private static void EnsureWithinLimit(
        int statementActiveCount, int receiptActiveCount,
        AgencyServiceFeeStatement statement, FinanceReceipt receipt)
    {
        if (statementActiveCount >= AgencyServiceFeeCollectionAllocationRules.MaxAllocationsPerStatement)
            throw BusinessException.RuleConflict(
                $"对账单「{statement.StatementNo}」的有效分摊行已达上限 "
                + $"{AgencyServiceFeeCollectionAllocationRules.MaxAllocationsPerStatement} 条："
                + "如需新增请先作废不需要的行（作废保留历史，不物理删除）");

        if (receiptActiveCount >= AgencyServiceFeeCollectionAllocationRules.MaxAllocationsPerReceipt)
            throw BusinessException.RuleConflict(
                $"收款单「{receipt.ReceiptNo}」的有效分摊行已达上限 "
                + $"{AgencyServiceFeeCollectionAllocationRules.MaxAllocationsPerReceipt} 条："
                + "如需新增请先作废不需要的行（作废保留历史，不物理删除）");
    }

    /// <summary>按收款单批量取回**本维度**有效行合计与行数（一次分组查询，无逐行查库）</summary>
    private static async Task<Dictionary<long, (decimal Amount, int Count)>> LoadActiveTotalsForReceiptsAsync(
        IErpDbContext db, IReadOnlyList<long> receiptIds)
    {
        var rows = await db.AgencyServiceFeeCollectionAllocations.AsNoTracking()
            .Where(a => !a.IsDeleted
                        && a.Status == AgencyServiceFeeCollectionAllocationRules.StatusActive
                        && receiptIds.Contains(a.ReceiptId))
            .GroupBy(a => a.ReceiptId)
            .Select(g => new { Key = g.Key, Amount = g.Sum(a => a.AllocatedAmount), Count = g.Count() })
            .ToListAsync();
        return rows.ToDictionary(r => r.Key, r => (r.Amount, r.Count));
    }

    /// <summary>按对账单批量取回**本维度**有效行合计与行数（一次分组查询，无逐行查库）</summary>
    private static async Task<Dictionary<long, (decimal Amount, int Count)>> LoadActiveTotalsForStatementsAsync(
        IErpDbContext db, IReadOnlyList<long> statementIds)
    {
        var rows = await db.AgencyServiceFeeCollectionAllocations.AsNoTracking()
            .Where(a => !a.IsDeleted
                        && a.Status == AgencyServiceFeeCollectionAllocationRules.StatusActive
                        && statementIds.Contains(a.StatementId))
            .GroupBy(a => a.StatementId)
            .Select(g => new { Key = g.Key, Amount = g.Sum(a => a.AllocatedAmount), Count = g.Count() })
            .ToListAsync();
        return rows.ToDictionary(r => r.Key, r => (r.Amount, r.Count));
    }

    // ==================== 6. 实体 → DTO（纯映射，不写库） ====================

    /// <summary>
    /// 分摊行实体 → DTO：快照全部取自行内**服务端权威快照**（客户 / 币种 / 状态 / 金额文案），
    /// 只按当前收款单与对账单的**只读可用性**做标注（软删除 / 已取消 / 已作废 / 草稿），
    /// <strong>绝不</strong>改写历史行的快照、金额或归属。
    /// </summary>
    private static AgencyServiceFeeCollectionAllocationDto Map(
        AgencyServiceFeeCollectionAllocation row,
        IReadOnlyDictionary<long, FinanceReceipt> receipts,
        IReadOnlyDictionary<long, AgencyServiceFeeStatement> statements)
    {
        ArgumentNullException.ThrowIfNull(row);

        var currency = CurrencyAmountRules.NormalizeCurrency(row.Currency);
        var decimals = CurrencyAmountRules.PrecisionOf(currency);

        receipts.TryGetValue(row.ReceiptId, out var receipt);
        statements.TryGetValue(row.StatementId, out var statement);

        var receiptAvailable = receipt is not null
            && AgencyServiceFeeCollectionAllocationRules.EvaluateReceiptEligibility(receipt, 0m).Eligible;
        var statementAvailable = statement is not null
            && AgencyServiceFeeCollectionAllocationRules.EvaluateStatementEligibility(statement, 0m).Eligible;

        return new AgencyServiceFeeCollectionAllocationDto(
            row.Id,
            row.StatementId,
            row.StatementNo ?? string.Empty,
            AgencyServiceFeeCollectionAllocationRules.AllocationIdentityText(row.StatementNo, row.ReceiptNo),
            row.StatementDate,
            row.StatementStatus,
            row.StatementStatusText ?? AgencyServiceFeeCollectionAllocationRules.StatementStatusText(row.StatementStatus),
            row.StatementTotalAmount,
            CurrencyAmountRules.NormalizeCurrency(row.StatementCurrency),
            row.StatementAgreementId,
            row.StatementAgreementNo ?? string.Empty,
            AgencyServiceFeeCollectionAllocationRules.AmountText(row.StatementTotalAmount, row.StatementCurrency),
            row.ReceiptId,
            row.ReceiptNo ?? string.Empty,
            row.ReceiptDate,
            row.ReceiptStatus,
            row.ReceiptStatusText ?? AgencyServiceFeeCollectionAllocationRules.ReceiptStatusText(row.ReceiptStatus),
            row.ReceiptAmount,
            AgencyServiceFeeCollectionAllocationRules.AmountText(row.ReceiptAmount, currency),
            row.CustomerId,
            row.CustomerCode ?? string.Empty,
            row.CustomerName ?? string.Empty,
            currency,
            decimals,
            row.AllocatedAmount,
            AgencyServiceFeeCollectionAllocationRules.AmountText(row.AllocatedAmount, currency),
            row.Remark ?? string.Empty,
            row.Status,
            AgencyServiceFeeCollectionAllocationRules.StatusText(row.Status),
            row.Status == AgencyServiceFeeCollectionAllocationRules.StatusActive,
            row.Status == AgencyServiceFeeCollectionAllocationRules.StatusVoided,
            row.AllocatedAt,
            row.AllocatedBy ?? string.Empty,
            row.VoidedAt,
            row.VoidReason ?? string.Empty,
            receiptAvailable,
            AgencyServiceFeeCollectionAllocationRules.ReceiptAvailabilityText(receipt),
            statementAvailable,
            AgencyServiceFeeCollectionAllocationRules.StatementAvailabilityText(statement),
            row.CreatedAt,
            row.UpdatedAt,
            AgencyServiceFeeCollectionAllocationRules.RuleText,
            AgencyServiceFeeCollectionAllocationRules.AmountRuleText,
            AgencyServiceFeeCollectionAllocationRules.DimensionSeparationText,
            AgencyServiceFeeCollectionAllocationRules.BoundaryText);
    }

    /// <summary>单行映射（一次批量装载收款单与对账单：固定 2 次数据集访问 + 1 次行装载，无逐行查库）</summary>
    private static async Task<AgencyServiceFeeCollectionAllocationDto> MapOneAsync(
        IErpDbContext db, AgencyServiceFeeCollectionAllocation row)
        => (await MapManyAsync(db, new List<AgencyServiceFeeCollectionAllocation> { row })).Single();

    /// <summary>
    /// 批量映射：本页的收款单与对账单**各一次**查询装载（与行数无关、无 N+1），
    /// 空页同样执行固定次数的装载（访问次数不随行数变化）。
    /// </summary>
    private static async Task<List<AgencyServiceFeeCollectionAllocationDto>> MapManyAsync(
        IErpDbContext db, IReadOnlyList<AgencyServiceFeeCollectionAllocation> rows)
    {
        var receiptIds = rows.Select(r => r.ReceiptId).Distinct().ToList();
        var statementIds = rows.Select(r => r.StatementId).Distinct().ToList();

        var receipts = await db.FinanceReceipts.AsNoTracking()
            .Where(r => receiptIds.Contains(r.Id)).ToListAsync();
        var statements = await db.AgencyServiceFeeStatements.AsNoTracking()
            .Where(x => statementIds.Contains(x.Id)).ToListAsync();

        var receiptMap = receipts.ToDictionary(r => r.Id);
        var statementMap = statements.ToDictionary(x => x.Id);

        return rows.Select(row => Map(row, receiptMap, statementMap)).ToList();
    }
}
