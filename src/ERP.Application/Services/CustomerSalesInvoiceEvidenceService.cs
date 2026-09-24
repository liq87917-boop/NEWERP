using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ERP.Application.Services;

/// <summary>
/// 客户销项发票证据登记服务（ERP-055）。职责：
/// <list type="number">
/// <item><b>登记 / 修改</b>（<see cref="CreateAsync"/> / <see cref="UpdateAsync"/>）：只允许操作**草稿**；
/// 客户必须存在、未删除且启用，客户编码 / 名称由服务端写成快照，金额必须满足金额等式与币种精度；</item>
/// <item><b>唯一身份</b>：同一「客户 + 发票类型 + 规范化代码 / 号码」在**有效（未作废）**记录内唯一，
/// 重复请求一律拒绝（不静默合并、不覆盖）；作废记录保留并可读，但不占用身份；</item>
/// <item><b>分摊</b>（<see cref="PreviewAllocationsAsync"/> / <see cref="SaveAllocationsAsync"/>）：
/// 只能分摊到既有、未删除、未取消且**客户与币种一致**的销售订单，同一订单不重复、合计不超过含税总额；</item>
/// <item><b>登记 / 作废</b>（<see cref="RecordAsync"/> / <see cref="VoidAsync"/>）：登记冻结证据，
/// 作废保留身份 / 金额 / 分摊 / 审计历史，不物理删除、不静默改写；</item>
/// <item><b>台账读取</b>（<see cref="ListAsync"/> / <see cref="GetAsync"/> /
/// <see cref="ListOrderCandidatesAsync"/> / <see cref="ListTradeDocumentCandidatesAsync"/>）：
/// 分页 / 有界、批量装载分摊行与订单，无逐行数据库查询。</item>
/// </list>
/// <para>边界（重要）：本服务<strong>不</strong>开具或作废任何真实发票、<strong>不</strong>调用任何开票 / 税务平台接口、
/// <strong>不</strong>改写销售订单状态 / 出货进度 / 金额与明细、客户信用状态与信用额度、客户收款单与其引用行、
/// 库存与库存成本、库存流水、装柜与单证记录、佣金 / 回佣、费用与退税记录，也<strong>不</strong>记账、
/// <strong>不</strong>生成凭证 / 收款 / 付款 / 结算单；它不是开票系统、不是税务申报系统，也不是应收账款台账。</para>
/// </summary>
public static class CustomerSalesInvoiceEvidenceService
{
    // ==================== 1. 登记 / 修改（仅草稿） ====================

    /// <summary>
    /// 新增草稿发票证据：校验发票类型 / 代码 / 号码、客户可用性、币种与金额等式、
    /// 可选单证交叉引用，写入客户与单证快照与规范化身份列。
    /// </summary>
    public static async Task<CustomerSalesInvoiceEvidenceDto> CreateAsync(
        IErpDbContext db, CustomerSalesInvoiceEvidenceSaveDto dto)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(dto);

        var input = await ValidateHeaderAsync(db, dto);
        await EnsureIdentityAvailableAsync(db, input, excludeInvoiceId: null);

        var invoice = new CustomerSalesInvoiceEvidence
        {
            InvoiceType = input.InvoiceType,
            InvoiceCode = input.InvoiceCode,
            InvoiceNumber = input.InvoiceNumber,
            NormalizedInvoiceCode = input.NormalizedCode,
            NormalizedInvoiceNumber = input.NormalizedNumber,
            InvoiceDate = input.InvoiceDate,
            CustomerId = input.Customer.Id,
            CustomerCode = input.Customer.CustomerCode ?? string.Empty,
            CustomerName = input.Customer.CustomerName ?? string.Empty,
            Currency = input.Currency,
            NetAmount = input.Net,
            TaxAmount = input.Tax,
            GrossAmount = input.Gross,
            TradeDocumentId = input.TradeDocumentId,
            TradeDocumentNo = input.TradeDocumentNo,
            TradeDocumentDocType = input.TradeDocumentDocType,
            CommercialInvoiceReference = input.CommercialInvoiceReference,
            Status = CustomerSalesInvoiceEvidenceRules.StatusDraft,
            Remark = input.Remark
        };

        db.CustomerSalesInvoiceEvidences.Add(invoice);
        await db.SaveChangesAsync();

        return await MapAsync(db, invoice);
    }

    /// <summary>
    /// 修改草稿发票证据：已登记 / 已作废拒绝修改（保留可读）；重复身份拒绝；
    /// 已有销售订单分摊时不允许更换客户或币种（会破坏分摊口径，须先清空分摊再改）。
    /// </summary>
    public static async Task<CustomerSalesInvoiceEvidenceDto> UpdateAsync(
        IErpDbContext db, long invoiceId, CustomerSalesInvoiceEvidenceSaveDto dto)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(dto);
        if (invoiceId <= 0) throw BusinessException.InvalidParameter("请选择要修改的发票");

        var invoice = await LoadAsync(db, invoiceId);
        CustomerSalesInvoiceEvidenceRules.EnsureEditable(invoice.Status, IdentityOf(invoice));

        var input = await ValidateHeaderAsync(db, dto);

        var hasAllocations = await db.CustomerSalesInvoiceAllocations.AsNoTracking()
            .AnyAsync(a => !a.IsDeleted && a.CustomerSalesInvoiceEvidenceId == invoice.Id);
        if (hasAllocations && input.Customer.Id != invoice.CustomerId)
            throw BusinessException.RuleConflict(
                $"发票「{IdentityOf(invoice)}」已存在销售订单分摊：不能更换客户"
                + "（会破坏分摊口径，请先清空分摊再修改客户）");
        if (hasAllocations
            && !string.Equals(input.Currency, CurrencyAmountRules.NormalizeCurrency(invoice.Currency), StringComparison.Ordinal))
            throw BusinessException.RuleConflict(
                $"发票「{IdentityOf(invoice)}」已存在销售订单分摊：不能更换币种"
                + "（会破坏分摊口径，请先清空分摊再修改币种）");

        await EnsureIdentityAvailableAsync(db, input, invoice.Id);

        invoice.InvoiceType = input.InvoiceType;
        invoice.InvoiceCode = input.InvoiceCode;
        invoice.InvoiceNumber = input.InvoiceNumber;
        invoice.NormalizedInvoiceCode = input.NormalizedCode;
        invoice.NormalizedInvoiceNumber = input.NormalizedNumber;
        invoice.InvoiceDate = input.InvoiceDate;
        invoice.CustomerId = input.Customer.Id;
        invoice.CustomerCode = input.Customer.CustomerCode ?? string.Empty;
        invoice.CustomerName = input.Customer.CustomerName ?? string.Empty;
        invoice.Currency = input.Currency;
        invoice.NetAmount = input.Net;
        invoice.TaxAmount = input.Tax;
        invoice.GrossAmount = input.Gross;
        invoice.TradeDocumentId = input.TradeDocumentId;
        invoice.TradeDocumentNo = input.TradeDocumentNo;
        invoice.TradeDocumentDocType = input.TradeDocumentDocType;
        invoice.CommercialInvoiceReference = input.CommercialInvoiceReference;
        invoice.Remark = input.Remark;
        invoice.UpdatedAt = DateTime.Now;

        await db.SaveChangesAsync();

        return await MapAsync(db, invoice);
    }

    // ==================== 2. 台账读取（分页 / 有界，批量装载） ====================

    /// <summary>发票证据详情（含分摊行、已分摊 / 未分摊金额与订单可用性标注；只读）</summary>
    public static async Task<CustomerSalesInvoiceEvidenceDto> GetAsync(IErpDbContext db, long invoiceId)
    {
        ArgumentNullException.ThrowIfNull(db);
        var invoice = await LoadAsync(db, invoiceId);
        return await MapAsync(db, invoice);
    }

    /// <summary>
    /// 台账分页查询（只读）：支持客户 / 发票类型 / 状态 / 币种 / 开票日期区间 / 分摊状态 / 销售订单 / 关键字过滤。
    /// <para>分摊状态过滤与分页口径一致：先按**持久化分摊行**派生每张发票的已分摊金额（子查询）再过滤，
    /// 因此「只看未分摊」这类筛选不会因分页而漏行；本页分摊行一次批量装载（无逐行数据库查询）。</para>
    /// </summary>
    public static async Task<PagedResult<CustomerSalesInvoiceEvidenceDto>> ListAsync(
        IErpDbContext db, CustomerSalesInvoiceEvidenceQuery query)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(query);
        query.Normalize();

        var status = CustomerSalesInvoiceEvidenceRules.NormalizeStatusFilter(query.Status);
        var invoiceType = CustomerSalesInvoiceEvidenceRules.NormalizeInvoiceTypeFilter(query.InvoiceType);
        var linkage = CustomerSalesInvoiceEvidenceRules.NormalizeLinkageFilter(query.LinkageStatus);
        var currency = string.IsNullOrWhiteSpace(query.Currency)
            ? null
            : CustomerSalesInvoiceEvidenceRules.NormalizeCurrencyStrict(query.Currency);
        var keyword = CustomerSalesInvoiceEvidenceRules.NormalizeKeyword(query.Keyword);

        var source = db.CustomerSalesInvoiceEvidences.AsNoTracking().Where(x => !x.IsDeleted);
        if (query.CustomerId is not null) source = source.Where(x => x.CustomerId == query.CustomerId.Value);
        if (invoiceType is not null) source = source.Where(x => x.InvoiceType == invoiceType);
        if (status is not null) source = source.Where(x => x.Status == status.Value);
        if (currency is not null) source = source.Where(x => x.Currency == currency);
        if (query.InvoiceDateFrom is not null)
            source = source.Where(x => x.InvoiceDate >= query.InvoiceDateFrom.Value.Date);
        if (query.InvoiceDateTo is not null)
            source = source.Where(x => x.InvoiceDate <= query.InvoiceDateTo.Value.Date);

        if (query.SalesOrderId is not null && query.SalesOrderId.Value > 0)
        {
            var orderId = query.SalesOrderId.Value;
            source = source.Where(x => db.CustomerSalesInvoiceAllocations
                .Any(a => !a.IsDeleted && a.CustomerSalesInvoiceEvidenceId == x.Id && a.SalesOrderId == orderId));
        }

        if (keyword.Length > 0)
        {
            source = source.Where(x => x.InvoiceNumber.Contains(keyword)
                || x.InvoiceCode.Contains(keyword)
                || x.CustomerName.Contains(keyword)
                || x.CustomerCode.Contains(keyword)
                || x.CommercialInvoiceReference.Contains(keyword));
        }

        var withTotals = source.Select(x => new
        {
            Invoice = x,
            LinkedAmount = db.CustomerSalesInvoiceAllocations
                .Where(a => !a.IsDeleted && a.CustomerSalesInvoiceEvidenceId == x.Id)
                .Sum(a => (decimal?)a.AllocatedAmount) ?? 0m
        });

        if (linkage is not null)
        {
            withTotals = linkage switch
            {
                CustomerSalesInvoiceEvidenceRules.LinkageUnlinked => withTotals.Where(t => t.LinkedAmount <= 0),
                CustomerSalesInvoiceEvidenceRules.LinkageLinked => withTotals.Where(t => t.LinkedAmount >= t.Invoice.GrossAmount),
                _ => withTotals.Where(t => t.LinkedAmount > 0 && t.LinkedAmount < t.Invoice.GrossAmount)
            };
        }

        var total = await withTotals.CountAsync();
        var page = await withTotals
            .OrderByDescending(t => t.Invoice.InvoiceDate)
            .ThenByDescending(t => t.Invoice.Id)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .ToListAsync();

        var invoices = page.Select(t => t.Invoice).ToList();
        return new PagedResult<CustomerSalesInvoiceEvidenceDto>
        {
            Items = await MapManyAsync(db, invoices),
            Total = total,
            Page = query.Page,
            PageSize = query.PageSize
        };
    }

    // ==================== 3. 候选读取（只读、有界，绝不写库） ====================

    /// <summary>
    /// 可分摊销售订单候选（只读、有界）：只列出**同客户 + 同币种**的未删除订单（含已取消订单并显式标注不可分摊），
    /// 每张订单附带订单总额、本发票已分摊、其他有效发票已分摊与剩余未被发票证据覆盖的金额
    /// （只按持久化分摊行派生，下限 0 —— 不是应收余额、账龄或收款依据）。
    /// </summary>
    public static async Task<List<CustomerSalesInvoiceOrderCandidateDto>> ListOrderCandidatesAsync(
        IErpDbContext db, long invoiceId, string? keyword,
        int take = CustomerSalesInvoiceEvidenceRules.MaxOrderCandidates)
    {
        ArgumentNullException.ThrowIfNull(db);
        var invoice = await LoadAsync(db, invoiceId);

        var keywordText = CustomerSalesInvoiceEvidenceRules.NormalizeKeyword(keyword);
        var size = take <= 0 ? CustomerSalesInvoiceEvidenceRules.MaxOrderCandidates
            : Math.Min(take, CustomerSalesInvoiceEvidenceRules.MaxOrderCandidates);

        // 币种不在系统币种口径内：没有任何销售订单可以权威匹配（不猜测、不换算）
        if (!Enum.TryParse<Currency>(CurrencyAmountRules.NormalizeCurrency(invoice.Currency), out var currency))
            return new List<CustomerSalesInvoiceOrderCandidateDto>();

        var source = db.SalesOrders.AsNoTracking()
            .Where(o => !o.IsDeleted && o.CustomerId == invoice.CustomerId && o.Currency == currency);
        if (keywordText.Length > 0)
            source = source.Where(o => o.OrderNo.Contains(keywordText) || o.ContractNo.Contains(keywordText));

        var orders = await source.OrderByDescending(o => o.Id).Take(size).ToListAsync();
        if (orders.Count == 0) return new List<CustomerSalesInvoiceOrderCandidateDto>();

        var orderIds = orders.Select(o => o.Id).ToList();

        // 一次查询取回候选订单的分摊行：已作废发票的分摊不占用「已被发票证据覆盖」的金额
        var rows = await (from allocation in db.CustomerSalesInvoiceAllocations.AsNoTracking()
                          join owner in db.CustomerSalesInvoiceEvidences.AsNoTracking()
                              on allocation.CustomerSalesInvoiceEvidenceId equals owner.Id
                          where !allocation.IsDeleted && !owner.IsDeleted
                                && owner.Status != CustomerSalesInvoiceEvidenceRules.StatusVoided
                                && orderIds.Contains(allocation.SalesOrderId)
                          select new
                          {
                              allocation.SalesOrderId,
                              allocation.CustomerSalesInvoiceEvidenceId,
                              allocation.AllocatedAmount
                          }).ToListAsync();

        var customer = await db.BaseCustomers.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == invoice.CustomerId);

        return orders.Select(order =>
        {
            var orderRows = rows.Where(r => r.SalesOrderId == order.Id).ToList();
            var linkedByThis = orderRows
                .Where(r => r.CustomerSalesInvoiceEvidenceId == invoice.Id).Sum(r => r.AllocatedAmount);
            var linkedByOthers = orderRows
                .Where(r => r.CustomerSalesInvoiceEvidenceId != invoice.Id).Sum(r => r.AllocatedAmount);
            var remaining = order.TotalAmount - linkedByThis - linkedByOthers;
            if (remaining < 0) remaining = 0;

            var (eligible, text) = CustomerSalesInvoiceEvidenceRules.EvaluateOrderEligibility(
                invoice.CustomerId, invoice.Currency, order);

            return new CustomerSalesInvoiceOrderCandidateDto(
                order.Id,
                order.OrderNo ?? string.Empty,
                order.OrderDate,
                OrderStatusText(order),
                order.Status == DocumentStatus.Cancelled,
                CurrencyAmountRules.NormalizeCurrency(order.Currency.ToString()),
                order.CustomerId,
                customer?.CustomerCode ?? string.Empty,
                customer?.CustomerName ?? string.Empty,
                order.TotalAmount,
                linkedByThis,
                linkedByOthers,
                remaining,
                eligible,
                text);
        }).ToList();
    }

    /// <summary>
    /// 可显式交叉引用的单证中心商业发票候选（只读、有界）：只列出既有、未删除且类型为商业发票的单证
    /// （权威类型常量来自 <c>TradeDocumentItemRules.CommercialInvoiceDocType</c>），仅用于**显式选择**交叉引用来源。
    /// <para>本方法<strong>不</strong>读取单证明细金额、<strong>不</strong>把单证转换成本登记册记录、
    /// <strong>不</strong>建立任何自动链接，也不写库。</para>
    /// </summary>
    public static async Task<List<CustomerSalesInvoiceTradeDocumentCandidateDto>> ListTradeDocumentCandidatesAsync(
        IErpDbContext db, string? keyword,
        int take = CustomerSalesInvoiceEvidenceRules.MaxTradeDocumentCandidates)
    {
        ArgumentNullException.ThrowIfNull(db);

        var keywordText = CustomerSalesInvoiceEvidenceRules.NormalizeKeyword(keyword);
        var size = take <= 0 ? CustomerSalesInvoiceEvidenceRules.MaxTradeDocumentCandidates
            : Math.Min(take, CustomerSalesInvoiceEvidenceRules.MaxTradeDocumentCandidates);

        var source = db.TradeDocuments.AsNoTracking()
            .Where(d => !d.IsDeleted && d.DocType == TradeDocumentItemRules.CommercialInvoiceDocType);
        if (keywordText.Length > 0)
            source = source.Where(d => d.DocNo.Contains(keywordText) || d.SalesOrderNo.Contains(keywordText));

        var documents = await source.OrderByDescending(d => d.Id).Take(size).ToListAsync();

        return documents.Select(document =>
        {
            var (eligible, text) = CustomerSalesInvoiceEvidenceRules.EvaluateTradeDocumentEligibility(document);
            return new CustomerSalesInvoiceTradeDocumentCandidateDto(
                document.Id,
                document.DocNo ?? string.Empty,
                document.DocType ?? string.Empty,
                document.IssueDate,
                document.SalesOrderNo ?? string.Empty,
                document.CustomerName ?? string.Empty,
                CurrencyAmountRules.NormalizeCurrency(document.Currency),
                document.Amount,
                eligible,
                text);
        }).ToList();
    }

    // ==================== 4. 分摊（草稿专用，整体替换） ====================

    /// <summary>
    /// 分摊预览（**只读，不写库**）：逐行校验销售订单资格与分摊金额，返回订单快照、资格文案、
    /// 拟分摊合计与保存后的已分摊 / 未分摊金额；预览与保存共用同一校验口径。
    /// </summary>
    public static async Task<CustomerSalesInvoiceAllocationPreviewDto> PreviewAllocationsAsync(
        IErpDbContext db, long invoiceId, CustomerSalesInvoiceAllocationSaveRequest? request)
    {
        ArgumentNullException.ThrowIfNull(db);
        var invoice = await LoadAsync(db, invoiceId);
        var plan = await BuildAllocationPlanAsync(db, invoice, request);

        var proposed = plan.AllocatedAmount;
        var unlinked = invoice.GrossAmount - proposed;
        if (unlinked < 0) unlinked = 0;

        return new CustomerSalesInvoiceAllocationPreviewDto(
            invoice.Id,
            IdentityOf(invoice),
            invoice.CustomerId,
            invoice.CustomerName ?? string.Empty,
            CurrencyAmountRules.NormalizeCurrency(invoice.Currency),
            invoice.GrossAmount,
            plan.PersistedLinkedAmount,
            proposed,
            unlinked,
            CustomerSalesInvoiceEvidenceRules.LinkageStatusOf(invoice.GrossAmount, proposed),
            CustomerSalesInvoiceEvidenceRules.LinkageText(
                invoice.GrossAmount, proposed, plan.Count, invoice.Currency),
            plan.Count,
            CustomerSalesInvoiceEvidenceRules.LinkageRuleText,
            CustomerSalesInvoiceEvidenceRules.BoundaryText,
            plan.Lines);
    }

    /// <summary>
    /// 保存分摊（草稿专用，整体替换）：在同一 <c>SaveChanges</c> 内删除原分摊行并写入新分摊行
    /// （校验失败不会留下部分行）；已登记 / 已作废发票拒绝任何分摊改动。
    /// </summary>
    public static async Task<CustomerSalesInvoiceEvidenceDto> SaveAllocationsAsync(
        IErpDbContext db, long invoiceId, CustomerSalesInvoiceAllocationSaveRequest? request)
    {
        ArgumentNullException.ThrowIfNull(db);
        var invoice = await LoadAsync(db, invoiceId);
        var plan = await BuildAllocationPlanAsync(db, invoice, request);

        // 草稿期分摊属于工作数据：整体替换；一旦登记即冻结，作废也不改写（保留证据）
        var existing = await db.CustomerSalesInvoiceAllocations
            .Where(a => !a.IsDeleted && a.CustomerSalesInvoiceEvidenceId == invoice.Id)
            .ToListAsync();
        db.CustomerSalesInvoiceAllocations.RemoveRange(existing);
        foreach (var row in plan.Rows) db.CustomerSalesInvoiceAllocations.Add(row);

        invoice.UpdatedAt = DateTime.Now;
        await db.SaveChangesAsync();

        return await MapAsync(db, invoice);
    }

    /// <summary>分摊计划（预览与保存的唯一校验 / 计算入口）</summary>
    private sealed record AllocationPlan(
        decimal PersistedLinkedAmount,
        List<CustomerSalesInvoiceAllocation> Rows,
        List<CustomerSalesInvoiceAllocationPreviewLineDto> Lines)
    {
        /// <summary>本次拟分摊条数</summary>
        public int Count => Rows.Count;

        /// <summary>本次拟分摊金额合计</summary>
        public decimal AllocatedAmount => Rows.Sum(r => r.AllocatedAmount);
    }

    /// <summary>
    /// 构建分摊计划（预览与保存共用的唯一校验入口）：
    /// 发票必须处于草稿；同一订单不重复；订单必须存在 / 未删除 / 未取消，且客户与币种与发票一致；
    /// 分摊金额按币种精度取整且大于 0；合计不得超过发票含税总额。全部校验通过后才返回，本方法不写库。
    /// </summary>
    private static async Task<AllocationPlan> BuildAllocationPlanAsync(
        IErpDbContext db, CustomerSalesInvoiceEvidence invoice, CustomerSalesInvoiceAllocationSaveRequest? request)
    {
        CustomerSalesInvoiceEvidenceRules.EnsureEditable(invoice.Status, IdentityOf(invoice));
        var currency = CurrencyAmountRules.NormalizeCurrency(invoice.Currency);

        var lines = request?.Lines ?? new List<CustomerSalesInvoiceAllocationSaveDto>();
        if (lines.Count > CustomerSalesInvoiceEvidenceRules.MaxAllocationsPerInvoice)
            throw BusinessException.InvalidParameter(
                $"单张发票最多分摊 {CustomerSalesInvoiceEvidenceRules.MaxAllocationsPerInvoice} 张销售订单，本次提交 {lines.Count} 条");

        if (lines.Select(l => l.SalesOrderId).Distinct().Count() != lines.Count)
            throw BusinessException.Duplicate(
                "同一张销售订单在同一张发票内只能分摊一次：请合并重复行（系统不静默合并也不改写既有行）");

        // 先逐行校验订单 Id：未选择订单的行一律拒绝，绝不静默忽略（空清单 = 清空全部分摊，允许）
        foreach (var line in lines)
        {
            if (line.SalesOrderId <= 0)
                throw BusinessException.InvalidParameter("请选择要分摊的销售订单");
        }

        var persisted = await db.CustomerSalesInvoiceAllocations.AsNoTracking()
            .Where(a => !a.IsDeleted && a.CustomerSalesInvoiceEvidenceId == invoice.Id)
            .SumAsync(a => (decimal?)a.AllocatedAmount) ?? 0m;

        var orderIds = lines.Select(l => l.SalesOrderId).Distinct().ToList();
        if (orderIds.Count == 0)
            return new AllocationPlan(persisted, new List<CustomerSalesInvoiceAllocation>(),
                new List<CustomerSalesInvoiceAllocationPreviewLineDto>());

        // 订单快照与「其他有效发票已分摊金额」各一次批量查询（无逐行数据库访问）
        var orders = (await db.SalesOrders.AsNoTracking()
                .Where(o => orderIds.Contains(o.Id)).ToListAsync())
            .ToDictionary(o => o.Id);

        var linkedByOthers = (await (from allocation in db.CustomerSalesInvoiceAllocations.AsNoTracking()
                                     join owner in db.CustomerSalesInvoiceEvidences.AsNoTracking()
                                         on allocation.CustomerSalesInvoiceEvidenceId equals owner.Id
                                     where !allocation.IsDeleted && !owner.IsDeleted
                                           && owner.Status != CustomerSalesInvoiceEvidenceRules.StatusVoided
                                           && owner.Id != invoice.Id
                                           && orderIds.Contains(allocation.SalesOrderId)
                                     select new { allocation.SalesOrderId, allocation.AllocatedAmount })
                                 .ToListAsync())
            .GroupBy(r => r.SalesOrderId)
            .ToDictionary(g => g.Key, g => g.Sum(r => r.AllocatedAmount));

        var rows = new List<CustomerSalesInvoiceAllocation>();
        var previewLines = new List<CustomerSalesInvoiceAllocationPreviewLineDto>();
        decimal total = 0;
        var sort = 0;

        foreach (var line in lines)
        {
            if (line.SalesOrderId <= 0)
                throw BusinessException.InvalidParameter("请选择要分摊的销售订单");

            orders.TryGetValue(line.SalesOrderId, out var order);
            CustomerSalesInvoiceEvidenceRules.EnsureOrderLinkable(invoice.CustomerId, invoice.Currency, order);
            if (order is null)
                throw BusinessException.NotFound("销售订单不存在或已删除，不能分摊");

            var amount = CustomerSalesInvoiceEvidenceRules.NormalizeAllocationAmount(line.AllocatedAmount, invoice.Currency);
            total += amount;
            if (total > invoice.GrossAmount)
                throw BusinessException.RuleConflict(
                    $"分摊金额合计 {total} 超过发票含税总额 {invoice.GrossAmount} {currency}："
                    + "请调整分摊金额（发票允许部分分摊，未分摊部分保留为未分摊金额）");

            var remark = CustomerSalesInvoiceEvidenceRules.NormalizeRemark(line.Remark);
            sort++;

            rows.Add(new CustomerSalesInvoiceAllocation
            {
                CustomerSalesInvoiceEvidenceId = invoice.Id,
                SalesOrderId = order.Id,
                OrderNo = order.OrderNo ?? string.Empty,
                OrderDate = order.OrderDate,
                OrderStatus = (int)order.Status,
                OrderCurrency = CurrencyAmountRules.NormalizeCurrency(order.Currency.ToString()),
                CustomerId = invoice.CustomerId,
                CustomerCode = invoice.CustomerCode ?? string.Empty,
                CustomerName = invoice.CustomerName ?? string.Empty,
                AllocatedAmount = amount,
                Currency = currency,
                SortOrder = sort,
                Remark = remark
            });

            previewLines.Add(new CustomerSalesInvoiceAllocationPreviewLineDto(
                order.Id,
                order.OrderNo ?? string.Empty,
                order.OrderDate,
                OrderStatusText(order),
                CurrencyAmountRules.NormalizeCurrency(order.Currency.ToString()),
                order.CustomerId,
                invoice.CustomerCode ?? string.Empty,
                invoice.CustomerName ?? string.Empty,
                amount,
                remark,
                true,
                CustomerSalesInvoiceEvidenceRules.EvaluateOrderEligibility(
                    invoice.CustomerId, invoice.Currency, order).Text,
                order.TotalAmount,
                linkedByOthers.TryGetValue(order.Id, out var other) ? other : 0m,
                amount));
        }

        return new AllocationPlan(persisted, rows, previewLines);
    }

    // ==================== 5. 登记 / 作废（证据冻结与保留） ====================

    /// <summary>
    /// 登记草稿发票（草稿 → 已登记）：登记前**复核**已持久化分摊行仍可权威分摊
    /// （订单未删除 / 未取消、客户与币种一致、合计不超过含税总额），通过后只改发票状态与登记时间。
    /// <para>登记<strong>不</strong>开具真实发票、<strong>不</strong>调用任何开票 / 税务服务、<strong>不</strong>改动销售订单、
    /// 客户信用状态、收款单与其引用行、库存、退税与财务记录。</para>
    /// </summary>
    public static async Task<CustomerSalesInvoiceEvidenceDto> RecordAsync(IErpDbContext db, long invoiceId)
    {
        ArgumentNullException.ThrowIfNull(db);
        var invoice = await LoadAsync(db, invoiceId);
        CustomerSalesInvoiceEvidenceRules.EnsureRecordable(invoice.Status, IdentityOf(invoice));

        await RevalidatePersistedAllocationsAsync(db, invoice);

        invoice.Status = CustomerSalesInvoiceEvidenceRules.StatusRecorded;
        invoice.RecordedAt = DateTime.Now;
        invoice.UpdatedAt = DateTime.Now;
        await db.SaveChangesAsync();

        return await MapAsync(db, invoice);
    }

    /// <summary>
    /// 作废发票（草稿 / 已登记 → 已作废）：必须填写作废原因；**保留**发票身份、金额、分摊行与审计历史，
    /// 不物理删除、不改写已登记金额与分摊，也<strong>不</strong>作废任何真实发票、不产生任何财务 / 税务动作；
    /// 重复作废被拒绝。
    /// </summary>
    public static async Task<CustomerSalesInvoiceEvidenceDto> VoidAsync(
        IErpDbContext db, long invoiceId, string? reason)
    {
        ArgumentNullException.ThrowIfNull(db);
        var invoice = await LoadAsync(db, invoiceId);
        CustomerSalesInvoiceEvidenceRules.EnsureVoidable(invoice.Status, IdentityOf(invoice));
        var reasonText = CustomerSalesInvoiceEvidenceRules.NormalizeVoidReason(reason);

        invoice.Status = CustomerSalesInvoiceEvidenceRules.StatusVoided;
        invoice.VoidedAt = DateTime.Now;
        invoice.VoidReason = reasonText;
        invoice.UpdatedAt = DateTime.Now;
        await db.SaveChangesAsync();

        return await MapAsync(db, invoice);
    }

    /// <summary>
    /// 登记前的分摊复核（只读）：已持久化分摊行必须仍然权威可分摊 ——
    /// 订单存在且未删除、未取消、客户与币种一致，且分摊合计不超过含税总额；
    /// 不满足时拒绝登记（不静默丢弃分摊行，也不改写销售订单）。
    /// </summary>
    private static async Task RevalidatePersistedAllocationsAsync(
        IErpDbContext db, CustomerSalesInvoiceEvidence invoice)
    {
        var allocations = await db.CustomerSalesInvoiceAllocations.AsNoTracking()
            .Where(a => !a.IsDeleted && a.CustomerSalesInvoiceEvidenceId == invoice.Id)
            .OrderBy(a => a.SortOrder).ThenBy(a => a.Id)
            .ToListAsync();
        if (allocations.Count == 0) return;

        var orderIds = allocations.Select(a => a.SalesOrderId).Distinct().ToList();
        var orders = (await db.SalesOrders.AsNoTracking()
                .Where(o => orderIds.Contains(o.Id)).ToListAsync())
            .ToDictionary(o => o.Id);

        decimal total = 0;
        foreach (var allocation in allocations)
        {
            orders.TryGetValue(allocation.SalesOrderId, out var order);
            if (order is null || order.IsDeleted)
                throw BusinessException.RuleConflict(
                    $"发票「{IdentityOf(invoice)}」分摊的销售订单「{allocation.OrderNo}」已不存在或已删除："
                    + "登记前请先调整分摊（不存在 / 已删除订单的分摊不能形成登记证据）");

            CustomerSalesInvoiceEvidenceRules.EnsureOrderLinkable(invoice.CustomerId, invoice.Currency, order);
            total += allocation.AllocatedAmount;
        }

        if (total > invoice.GrossAmount)
            throw BusinessException.RuleConflict(
                $"发票「{IdentityOf(invoice)}」的分摊金额合计 {total} 超过含税总额 {invoice.GrossAmount}："
                + "请先调整分摊再登记");
    }

    // ==================== 6. 校验与映射（内部） ====================

    /// <summary>校验后的发票头（全部为服务端权威值：含客户实体、规范化身份与可选单证交叉引用快照）</summary>
    private sealed record HeaderInput(
        string InvoiceType,
        string InvoiceCode,
        string InvoiceNumber,
        string NormalizedCode,
        string NormalizedNumber,
        DateTime InvoiceDate,
        BaseCustomer Customer,
        string Currency,
        decimal Net,
        decimal Tax,
        decimal Gross,
        long? TradeDocumentId,
        string TradeDocumentNo,
        string TradeDocumentDocType,
        string CommercialInvoiceReference,
        string Remark);

    /// <summary>
    /// 校验发票头（类型 / 代码 / 号码 / 日期 / 客户 / 币种 / 金额等式 / 可选单证交叉引用）：
    /// 全部通过后才返回权威值；客户必须存在、未删除且启用（停用 / 删除一律拒绝新增与修改），
    /// 客户与单证快照由服务端写入。
    /// </summary>
    private static async Task<HeaderInput> ValidateHeaderAsync(
        IErpDbContext db, CustomerSalesInvoiceEvidenceSaveDto dto)
    {
        var invoiceType = CustomerSalesInvoiceEvidenceRules.NormalizeInvoiceType(dto.InvoiceType);
        var invoiceCode = CustomerSalesInvoiceEvidenceRules.NormalizeInvoiceCode(dto.InvoiceCode, invoiceType);
        var invoiceNumber = CustomerSalesInvoiceEvidenceRules.NormalizeInvoiceNumber(dto.InvoiceNumber);
        var currency = CustomerSalesInvoiceEvidenceRules.NormalizeCurrencyStrict(dto.Currency);
        var remark = CustomerSalesInvoiceEvidenceRules.NormalizeRemark(dto.Remark);
        var commercialReference = CustomerSalesInvoiceEvidenceRules
            .NormalizeCommercialInvoiceReference(dto.CommercialInvoiceReference);
        var (net, tax, gross) = CustomerSalesInvoiceEvidenceRules.ValidateAmounts(
            dto.NetAmount, dto.TaxAmount, dto.GrossAmount, currency);

        if (dto.CustomerId <= 0) throw BusinessException.InvalidParameter("请选择客户");

        var customer = await db.BaseCustomers
                .FirstOrDefaultAsync(c => c.Id == dto.CustomerId && !c.IsDeleted)
            ?? throw BusinessException.NotFound($"客户（Id={dto.CustomerId}）不存在或已删除，不能登记发票");

        if (customer.Status != 1)
            throw BusinessException.RuleConflict(
                $"客户「{customer.CustomerName}」已停用：停用客户不能登记新发票（历史发票保持可读）");

        // 单证交叉引用（可选、显式）：只写入快照，不读取单证金额、不转换单证、不建立自动链接
        long? tradeDocumentId = null;
        var tradeDocumentNo = string.Empty;
        var tradeDocumentDocType = string.Empty;
        if (dto.TradeDocumentId is not null && dto.TradeDocumentId.Value > 0)
        {
            var document = await db.TradeDocuments.AsNoTracking()
                .FirstOrDefaultAsync(d => d.Id == dto.TradeDocumentId.Value);
            CustomerSalesInvoiceEvidenceRules.EnsureTradeDocumentReferenceable(document);

            tradeDocumentId = document!.Id;
            tradeDocumentNo = document.DocNo ?? string.Empty;
            tradeDocumentDocType = document.DocType ?? string.Empty;
        }

        return new HeaderInput(
            invoiceType,
            invoiceCode,
            invoiceNumber,
            CustomerSalesInvoiceEvidenceRules.NormalizeIdentityPart(invoiceCode),
            CustomerSalesInvoiceEvidenceRules.NormalizeIdentityPart(invoiceNumber),
            (dto.InvoiceDate ?? DateTime.Today).Date,
            customer,
            currency,
            net,
            tax,
            gross,
            tradeDocumentId,
            tradeDocumentNo,
            tradeDocumentDocType,
            commercialReference,
            remark);
    }

    /// <summary>
    /// 有效发票身份唯一：同一「客户 + 发票类型 + 规范化代码 / 号码」在**未作废、未删除**记录内唯一；
    /// 重复一律拒绝（不静默合并、不覆盖）；已作废记录保留可读但不占用身份（可重新登记）。
    /// </summary>
    private static async Task EnsureIdentityAvailableAsync(
        IErpDbContext db, HeaderInput input, long? excludeInvoiceId)
    {
        var excludedId = excludeInvoiceId ?? 0;
        var identity = CustomerSalesInvoiceEvidenceRules.IdentityText(
            input.InvoiceType, input.InvoiceCode, input.InvoiceNumber);

        var duplicate = await db.CustomerSalesInvoiceEvidences.AsNoTracking()
            .Where(x => !x.IsDeleted
                        && x.Status != CustomerSalesInvoiceEvidenceRules.StatusVoided
                        && x.CustomerId == input.Customer.Id
                        && x.InvoiceType == input.InvoiceType
                        && x.NormalizedInvoiceCode == input.NormalizedCode
                        && x.NormalizedInvoiceNumber == input.NormalizedNumber
                        && x.Id != excludedId)
            .Select(x => new { x.Id, x.InvoiceDate, x.Status })
            .FirstOrDefaultAsync();

        if (duplicate is null) return;

        throw BusinessException.Duplicate(
            $"客户「{input.Customer.CustomerName}」已存在同一身份的未作废发票：{identity}"
            + $"（Id={duplicate.Id}，状态 {CustomerSalesInvoiceEvidenceRules.StatusText(duplicate.Status)}，"
            + $"开票日期 {duplicate.InvoiceDate:yyyy-MM-dd}）；重复发票被拒绝而不是静默合并"
            + "（如需更正请先作废原记录再重新登记）");
    }

    /// <summary>按 Id 装载未删除发票（不存在 / 已删除 → 数据不存在）</summary>
    private static async Task<CustomerSalesInvoiceEvidence> LoadAsync(IErpDbContext db, long invoiceId)
    {
        if (invoiceId <= 0) throw BusinessException.InvalidParameter("请选择要操作的发票");
        return await db.CustomerSalesInvoiceEvidences
                   .FirstOrDefaultAsync(x => x.Id == invoiceId && !x.IsDeleted)
               ?? throw BusinessException.NotFound($"客户销项发票证据（Id={invoiceId}）不存在或已删除");
    }

    /// <summary>发票对外身份文案（提示与台账共用；与唯一性判定口径一致）</summary>
    private static string IdentityOf(CustomerSalesInvoiceEvidence invoice)
    {
        ArgumentNullException.ThrowIfNull(invoice);
        return CustomerSalesInvoiceEvidenceRules.IdentityText(
            invoice.InvoiceType, invoice.InvoiceCode, invoice.InvoiceNumber);
    }

    /// <summary>单张发票映射（详情与单条操作返回用；只读标注，不写库）</summary>
    private static async Task<CustomerSalesInvoiceEvidenceDto> MapAsync(
        IErpDbContext db, CustomerSalesInvoiceEvidence invoice)
    {
        var mapped = await MapManyAsync(db, new List<CustomerSalesInvoiceEvidence> { invoice });
        return mapped[0];
    }

    /// <summary>
    /// 批量映射（台账分页用）：分摊行 / 销售订单 / 客户 / 被引用单证各一次批量查询，绝无逐行数据库查询；
    /// 客户可用性、订单可用性与单证引用可用性都是**只读标注**：停用 / 删除 / 取消不改变历史证据的可读性。
    /// </summary>
    private static async Task<List<CustomerSalesInvoiceEvidenceDto>> MapManyAsync(
        IErpDbContext db, IReadOnlyList<CustomerSalesInvoiceEvidence> invoices)
    {
        if (invoices.Count == 0) return new List<CustomerSalesInvoiceEvidenceDto>();

        var invoiceIds = invoices.Select(i => i.Id).ToList();
        var allocationRows = await db.CustomerSalesInvoiceAllocations.AsNoTracking()
            .Where(a => !a.IsDeleted && invoiceIds.Contains(a.CustomerSalesInvoiceEvidenceId))
            .OrderBy(a => a.SortOrder).ThenBy(a => a.Id)
            .ToListAsync();

        var allocationsByInvoice = allocationRows
            .GroupBy(a => a.CustomerSalesInvoiceEvidenceId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<CustomerSalesInvoiceAllocation>)g.ToList());

        var orderIds = allocationRows.Select(a => a.SalesOrderId).Distinct().ToList();
        var orders = orderIds.Count == 0
            ? new Dictionary<long, SalesOrder>()
            : (await db.SalesOrders.AsNoTracking().Where(o => orderIds.Contains(o.Id)).ToListAsync())
                .ToDictionary(o => o.Id);

        var customerIds = invoices.Select(i => i.CustomerId).Distinct().ToList();
        var customers = (await db.BaseCustomers.AsNoTracking()
                .Where(c => customerIds.Contains(c.Id)).ToListAsync())
            .ToDictionary(c => c.Id);

        var documentIds = invoices.Where(i => i.TradeDocumentId is not null)
            .Select(i => i.TradeDocumentId!.Value).Distinct().ToList();
        var documents = documentIds.Count == 0
            ? new Dictionary<long, TradeDocument>()
            : (await db.TradeDocuments.AsNoTracking().Where(d => documentIds.Contains(d.Id)).ToListAsync())
                .ToDictionary(d => d.Id);

        return invoices.Select(invoice =>
            Map(
                invoice,
                allocationsByInvoice.TryGetValue(invoice.Id, out var rows)
                    ? rows
                    : (IReadOnlyList<CustomerSalesInvoiceAllocation>)new List<CustomerSalesInvoiceAllocation>(),
                customers,
                orders,
                documents)).ToList();
    }

    /// <summary>发票实体 → 台账 DTO（含分摊行、已分摊 / 未分摊金额与可用性标注；纯映射，不写库）</summary>
    private static CustomerSalesInvoiceEvidenceDto Map(
        CustomerSalesInvoiceEvidence invoice,
        IReadOnlyList<CustomerSalesInvoiceAllocation> allocations,
        Dictionary<long, BaseCustomer> customers,
        Dictionary<long, SalesOrder> orders,
        Dictionary<long, TradeDocument> documents)
    {
        ArgumentNullException.ThrowIfNull(invoice);

        var currency = CurrencyAmountRules.NormalizeCurrency(invoice.Currency);
        var linked = allocations.Sum(a => a.AllocatedAmount);
        var unlinked = invoice.GrossAmount - linked;
        if (unlinked < 0) unlinked = 0;

        customers.TryGetValue(invoice.CustomerId, out var customer);
        var customerAvailable = CustomerSalesInvoiceEvidenceRules.IsCustomerSelectable(customer);

        documents.TryGetValue(invoice.TradeDocumentId ?? 0, out var document);
        var documentAvailable = invoice.TradeDocumentId is null
            || (document is not null && !document.IsDeleted);

        var rows = allocations.Select(a =>
        {
            orders.TryGetValue(a.SalesOrderId, out var order);
            var orderAvailable = order is not null && !order.IsDeleted;

            var availabilityText = orderAvailable
                ? (order!.Status == DocumentStatus.Cancelled
                    ? "订单已取消：历史分摊保留可读，不再作为可分摊订单"
                    : $"订单可用（{OrderStatusText(order)}）")
                : "订单已删除或不存在：历史分摊保留可读";

            return new CustomerSalesInvoiceAllocationDto(
                a.Id,
                a.SalesOrderId,
                a.OrderNo ?? string.Empty,
                a.OrderDate,
                OrderStatusText(order),
                CurrencyAmountRules.NormalizeCurrency(a.OrderCurrency),
                a.CustomerId,
                a.CustomerCode ?? string.Empty,
                a.CustomerName ?? string.Empty,
                CurrencyAmountRules.NormalizeCurrency(a.Currency),
                a.AllocatedAmount,
                a.SortOrder,
                a.Remark ?? string.Empty,
                orderAvailable,
                availabilityText);
        }).ToList();

        return new CustomerSalesInvoiceEvidenceDto(
            invoice.Id,
            invoice.InvoiceType ?? string.Empty,
            invoice.InvoiceCode ?? string.Empty,
            invoice.InvoiceNumber ?? string.Empty,
            IdentityOf(invoice),
            invoice.InvoiceDate,
            invoice.CustomerId,
            invoice.CustomerCode ?? string.Empty,
            invoice.CustomerName ?? string.Empty,
            customerAvailable,
            CustomerSalesInvoiceEvidenceRules.CustomerAvailabilityText(customer),
            currency,
            CurrencyAmountRules.PrecisionOf(currency),
            invoice.NetAmount,
            invoice.TaxAmount,
            invoice.GrossAmount,
            invoice.Status,
            CustomerSalesInvoiceEvidenceRules.StatusText(invoice.Status),
            invoice.Status == CustomerSalesInvoiceEvidenceRules.StatusDraft,
            invoice.Status == CustomerSalesInvoiceEvidenceRules.StatusRecorded,
            invoice.Status == CustomerSalesInvoiceEvidenceRules.StatusVoided,
            invoice.RecordedAt,
            invoice.VoidedAt,
            invoice.VoidReason ?? string.Empty,
            invoice.Remark ?? string.Empty,
            invoice.TradeDocumentId,
            invoice.TradeDocumentNo ?? string.Empty,
            invoice.TradeDocumentDocType ?? string.Empty,
            invoice.CommercialInvoiceReference ?? string.Empty,
            documentAvailable,
            CustomerSalesInvoiceEvidenceRules.TradeDocumentAvailabilityText(invoice.TradeDocumentId, document),
            rows.Count,
            linked,
            unlinked,
            CustomerSalesInvoiceEvidenceRules.LinkageStatusOf(invoice.GrossAmount, linked),
            CustomerSalesInvoiceEvidenceRules.LinkageText(invoice.GrossAmount, linked, rows.Count, currency),
            invoice.CreatedAt,
            invoice.UpdatedAt,
            CustomerSalesInvoiceEvidenceRules.AmountEquationText,
            CustomerSalesInvoiceEvidenceRules.LinkageRuleText,
            CustomerSalesInvoiceEvidenceRules.TradeDocumentSeparationText,
            CustomerSalesInvoiceEvidenceRules.BoundaryText,
            rows);
    }

    /// <summary>销售订单状态文案（只读标注；订单不存在时照实说明「不存在或已删除」，不假定为可用）</summary>
    private static string OrderStatusText(SalesOrder? order)
    {
        if (order is null || order.IsDeleted) return "订单不存在或已删除";

        return order.Status switch
        {
            DocumentStatus.Pending => "待提交",
            DocumentStatus.Submitted => "已提交",
            DocumentStatus.Approved => "已审核",
            DocumentStatus.Rejected => "已驳回",
            DocumentStatus.Completed => "已完成",
            DocumentStatus.Cancelled => "已取消",
            _ => order.Status.ToString()
        };
    }
}
