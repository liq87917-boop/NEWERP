using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ERP.Application.Services;

/// <summary>
/// 供应商采购发票登记服务（ERP-043）。职责：
/// <list type="number">
/// <item><b>登记 / 修改</b>（<see cref="CreateAsync"/> / <see cref="UpdateAsync"/>）：只允许操作**草稿**；
/// 供应商必须存在、未删除且启用，供应商编码 / 名称由服务端写成快照，金额必须满足金额等式与币种精度；</item>
/// <item><b>唯一身份</b>：同一「供应商 + 发票类型 + 规范化代码 / 号码」在**有效（未作废）**记录内唯一，
/// 重复请求一律拒绝（不静默合并、不覆盖）；作废记录保留并可读，但不占用身份；</item>
/// <item><b>关联（分摊）</b>（<see cref="PreviewAllocationsAsync"/> / <see cref="SaveAllocationsAsync"/>）：
/// 只能关联到既有、未删除、未取消且**供应商与币种一致**的采购订单，同一订单不重复、合计不超过含税总额；</item>
/// <item><b>登记 / 作废</b>（<see cref="RecordAsync"/> / <see cref="VoidAsync"/>）：登记冻结证据，
/// 作废保留身份 / 金额 / 关联 / 审计历史，不物理删除、不静默改写；</item>
/// <item><b>台账读取</b>（<see cref="ListAsync"/> / <see cref="GetAsync"/> / <see cref="ListOrderCandidatesAsync"/>）：
/// 分页 / 有界、批量装载关联行与订单，无逐行数据库查询。</item>
/// </list>
/// <para>边界（重要）：本服务<strong>不</strong>改写采购订单状态 / 到货进度 / 金额与明细、库存与库存成本、库存流水、
/// 退税记录、供应商余额与结算方式、付款状态，也<strong>不</strong>记账、<strong>不</strong>生成凭证 / 收款 / 付款 / 结算单；
/// 它不是应付账款台账、不是税务申报系统、也不是付款授权机制。</para>
/// </summary>
public static class PurchaseInvoiceService
{
    /// <summary>发票列表关键字长度上限（超长直接拒绝，避免全表模糊扫描）</summary>
    public const int MaxKeywordLength = 100;

    // ==================== 1. 登记 / 修改（仅草稿） ====================

    /// <summary>
    /// 新增草稿发票：校验发票类型 / 代码 / 号码、供应商可用性、币种与金额等式，写入供应商快照与规范化身份列。
    /// </summary>
    public static async Task<PurchaseInvoiceDto> CreateAsync(IErpDbContext db, PurchaseInvoiceSaveDto dto)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(dto);

        var input = await ValidateHeaderAsync(db, dto);
        await EnsureIdentityAvailableAsync(db, input, excludeInvoiceId: null);

        var invoice = new PurchaseInvoice
        {
            InvoiceType = input.InvoiceType,
            InvoiceCode = input.InvoiceCode,
            InvoiceNumber = input.InvoiceNumber,
            NormalizedInvoiceCode = input.NormalizedCode,
            NormalizedInvoiceNumber = input.NormalizedNumber,
            InvoiceDate = input.InvoiceDate,
            SupplierId = input.Supplier.Id,
            SupplierCode = input.Supplier.SupplierCode ?? string.Empty,
            SupplierName = input.Supplier.SupplierName ?? string.Empty,
            Currency = input.Currency,
            NetAmount = input.Net,
            TaxAmount = input.Tax,
            GrossAmount = input.Gross,
            Status = PurchaseInvoiceRules.StatusDraft,
            Remark = input.Remark
        };

        db.PurchaseInvoices.Add(invoice);
        await db.SaveChangesAsync();

        return await MapAsync(db, invoice);
    }

    /// <summary>
    /// 修改草稿发票：已登记 / 已作废拒绝修改（保留可读）；重复身份拒绝；
    /// 已有采购订单关联时不允许更换供应商或币种（会破坏关联口径，须先清空关联再改）。
    /// </summary>
    public static async Task<PurchaseInvoiceDto> UpdateAsync(
        IErpDbContext db, long invoiceId, PurchaseInvoiceSaveDto dto)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(dto);
        if (invoiceId <= 0) throw BusinessException.InvalidParameter("请选择要修改的发票");

        var invoice = await LoadAsync(db, invoiceId);
        PurchaseInvoiceRules.EnsureEditable(invoice.Status, IdentityOf(invoice));

        var input = await ValidateHeaderAsync(db, dto);
        await EnsureIdentityAvailableAsync(db, input, excludeInvoiceId: invoiceId);

        var allocationCount = await db.PurchaseInvoiceAllocations
            .CountAsync(a => !a.IsDeleted && a.PurchaseInvoiceId == invoiceId);

        var supplierChanged = invoice.SupplierId != input.Supplier.Id;
        var currencyChanged = !string.Equals(
            CurrencyAmountRules.NormalizeCurrency(invoice.Currency), input.Currency, StringComparison.Ordinal);

        if (allocationCount > 0 && (supplierChanged || currencyChanged))
            throw BusinessException.RuleConflict(
                $"发票「{IdentityOf(invoice)}」已有 {allocationCount} 条采购订单关联："
                + "更换供应商或币种会破坏关联口径，请先把关联保存为空（清空）后再修改");

        invoice.InvoiceType = input.InvoiceType;
        invoice.InvoiceCode = input.InvoiceCode;
        invoice.InvoiceNumber = input.InvoiceNumber;
        invoice.NormalizedInvoiceCode = input.NormalizedCode;
        invoice.NormalizedInvoiceNumber = input.NormalizedNumber;
        invoice.InvoiceDate = input.InvoiceDate;
        invoice.SupplierId = input.Supplier.Id;
        invoice.SupplierCode = input.Supplier.SupplierCode ?? string.Empty;
        invoice.SupplierName = input.Supplier.SupplierName ?? string.Empty;
        invoice.Currency = input.Currency;
        invoice.NetAmount = input.Net;
        invoice.TaxAmount = input.Tax;
        invoice.GrossAmount = input.Gross;
        invoice.Remark = input.Remark;
        invoice.UpdatedAt = DateTime.Now;

        await db.SaveChangesAsync();
        return await MapAsync(db, invoice);
    }

    // ==================== 2. 读取（台账 / 详情 / 可关联订单候选） ====================

    /// <summary>详情（含关联行、已关联 / 未关联金额与订单可用性标注；只读，不写库）</summary>
    public static async Task<PurchaseInvoiceDto> GetAsync(IErpDbContext db, long invoiceId)
    {
        ArgumentNullException.ThrowIfNull(db);
        var invoice = await LoadAsync(db, invoiceId);
        return await MapAsync(db, invoice);
    }

    /// <summary>
    /// 台账分页查询（只读）：支持供应商 / 发票类型 / 状态 / 币种 / 开票日期区间 / 关联状态 / 关键字过滤。
    /// <para>关联状态过滤与分页口径一致：先按**持久化关联行**派生每张发票的已关联金额（子查询）再过滤，
    /// 因此「只看未关联」这类筛选不会因分页而漏行；本页关联行一次批量装载（无逐行数据库查询）。</para>
    /// </summary>
    public static async Task<PagedResult<PurchaseInvoiceDto>> ListAsync(
        IErpDbContext db, PurchaseInvoiceQuery query)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(query);
        query.Normalize();

        var status = PurchaseInvoiceRules.NormalizeStatusFilter(query.Status);
        var invoiceType = PurchaseInvoiceRules.NormalizeInvoiceTypeFilter(query.InvoiceType);
        var linkage = PurchaseInvoiceRules.NormalizeLinkageFilter(query.LinkageStatus);
        var currency = string.IsNullOrWhiteSpace(query.Currency)
            ? null
            : PurchaseInvoiceRules.NormalizeCurrencyStrict(query.Currency);

        var source = db.PurchaseInvoices.AsNoTracking().Where(x => !x.IsDeleted);
        if (query.SupplierId is not null) source = source.Where(x => x.SupplierId == query.SupplierId.Value);
        if (invoiceType is not null) source = source.Where(x => x.InvoiceType == invoiceType);
        if (status is not null) source = source.Where(x => x.Status == status.Value);
        if (currency is not null) source = source.Where(x => x.Currency == currency);
        if (query.InvoiceDateFrom is not null)
            source = source.Where(x => x.InvoiceDate >= query.InvoiceDateFrom.Value.Date);
        if (query.InvoiceDateTo is not null)
            source = source.Where(x => x.InvoiceDate <= query.InvoiceDateTo.Value.Date);

        if (!string.IsNullOrWhiteSpace(query.Keyword))
        {
            var keyword = query.Keyword.Trim();
            if (keyword.Length > MaxKeywordLength)
                throw BusinessException.InvalidParameter($"关键字长度不能超过 {MaxKeywordLength} 个字符");
            source = source.Where(x => x.InvoiceNumber.Contains(keyword)
                || x.InvoiceCode.Contains(keyword)
                || x.SupplierName.Contains(keyword)
                || x.SupplierCode.Contains(keyword));
        }

        var withTotals = source.Select(x => new
        {
            Invoice = x,
            LinkedAmount = db.PurchaseInvoiceAllocations
                .Where(a => !a.IsDeleted && a.PurchaseInvoiceId == x.Id)
                .Sum(a => (decimal?)a.AllocatedAmount) ?? 0m
        });

        if (linkage is not null)
        {
            withTotals = linkage switch
            {
                PurchaseInvoiceRules.LinkageUnlinked => withTotals.Where(t => t.LinkedAmount <= 0),
                PurchaseInvoiceRules.LinkageLinked => withTotals.Where(t => t.LinkedAmount >= t.Invoice.GrossAmount),
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
        return new PagedResult<PurchaseInvoiceDto>
        {
            Items = await MapManyAsync(db, invoices),
            Total = total,
            Page = query.Page,
            PageSize = query.PageSize
        };
    }

    /// <summary>
    /// 可关联采购订单候选（只读、有界）：只列出**同供应商 + 同币种**的未删除订单（含已取消订单并显式标注不可关联），
    /// 并按上限收敛；每张订单附带订单总额、本发票已关联、其他有效发票已关联与剩余未被发票证据覆盖的金额
    /// （只按持久化关联行派生，下限 0 —— 不是应付余额、账龄或付款依据）。
    /// </summary>
    public static async Task<List<PurchaseInvoiceOrderCandidateDto>> ListOrderCandidatesAsync(
        IErpDbContext db, long invoiceId, string? keyword, int take = PurchaseInvoiceRules.MaxOrderCandidates)
    {
        ArgumentNullException.ThrowIfNull(db);
        var invoice = await LoadAsync(db, invoiceId);

        var keywordText = (keyword ?? string.Empty).Trim();
        if (keywordText.Length > MaxKeywordLength)
            throw BusinessException.InvalidParameter($"关键字长度不能超过 {MaxKeywordLength} 个字符");

        var size = take <= 0 ? PurchaseInvoiceRules.MaxOrderCandidates
            : Math.Min(take, PurchaseInvoiceRules.MaxOrderCandidates);

        // 币种不在系统币种口径内：没有任何采购订单可以权威匹配（不猜测、不换算）
        if (!Enum.TryParse<Currency>(CurrencyAmountRules.NormalizeCurrency(invoice.Currency), out var currency))
            return new List<PurchaseInvoiceOrderCandidateDto>();

        var source = db.PurchaseOrders.AsNoTracking()
            .Where(o => !o.IsDeleted && o.SupplierId == invoice.SupplierId && o.Currency == currency);
        if (keywordText.Length > 0)
            source = source.Where(o => o.OrderNo.Contains(keywordText) || o.ContractNo.Contains(keywordText));

        var orders = await source.OrderByDescending(o => o.Id).Take(size).ToListAsync();
        if (orders.Count == 0) return new List<PurchaseInvoiceOrderCandidateDto>();

        var orderIds = orders.Select(o => o.Id).ToList();

        // 一次查询取回候选订单的关联行：作废发票不占用「已被发票证据覆盖」的金额
        var rows = await (from allocation in db.PurchaseInvoiceAllocations.AsNoTracking()
                          join owner in db.PurchaseInvoices.AsNoTracking()
                              on allocation.PurchaseInvoiceId equals owner.Id
                          where !allocation.IsDeleted && !owner.IsDeleted
                                && owner.Status != PurchaseInvoiceRules.StatusVoided
                                && orderIds.Contains(allocation.PurchaseOrderId)
                          select new
                          {
                              allocation.PurchaseOrderId,
                              allocation.PurchaseInvoiceId,
                              allocation.AllocatedAmount
                          }).ToListAsync();

        var supplier = await db.BaseSuppliers.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == invoice.SupplierId);

        return orders.Select(order =>
        {
            var orderRows = rows.Where(r => r.PurchaseOrderId == order.Id).ToList();
            var linkedByThis = orderRows.Where(r => r.PurchaseInvoiceId == invoice.Id).Sum(r => r.AllocatedAmount);
            var linkedByOthers = orderRows.Where(r => r.PurchaseInvoiceId != invoice.Id).Sum(r => r.AllocatedAmount);
            var remaining = order.TotalAmount - linkedByThis - linkedByOthers;
            if (remaining < 0) remaining = 0;

            var (eligible, text) = PurchaseInvoiceRules.EvaluateOrderEligibility(invoice, order);

            return new PurchaseInvoiceOrderCandidateDto(
                order.Id,
                order.OrderNo ?? string.Empty,
                order.OrderDate,
                OrderStatusText(order),
                order.Status == DocumentStatus.Cancelled,
                CurrencyAmountRules.NormalizeCurrency(order.Currency.ToString()),
                order.SupplierId,
                supplier?.SupplierCode ?? string.Empty,
                supplier?.SupplierName ?? string.Empty,
                order.TotalAmount,
                linkedByThis,
                linkedByOthers,
                remaining,
                eligible,
                text);
        }).ToList();
    }

    // ==================== 3. 关联（分摊）到采购订单 ====================

    /// <summary>
    /// 关联预览（**只读，不写库**）：逐行校验采购订单资格与关联金额，返回订单快照、资格文案、
    /// 拟关联合计与保存后的已关联 / 未关联金额。预览与保存共用同一校验入口，预览所示即保存结果。
    /// </summary>
    public static async Task<PurchaseInvoiceAllocationPreviewDto> PreviewAllocationsAsync(
        IErpDbContext db, long invoiceId, PurchaseInvoiceAllocationSaveRequest? request)
    {
        ArgumentNullException.ThrowIfNull(db);
        var invoice = await LoadAsync(db, invoiceId);
        var plan = await BuildAllocationPlanAsync(db, invoice, request);

        var proposed = plan.AllocatedAmount;
        var unlinked = invoice.GrossAmount - proposed;
        if (unlinked < 0) unlinked = 0;

        return new PurchaseInvoiceAllocationPreviewDto(
            invoice.Id,
            IdentityOf(invoice),
            invoice.SupplierId,
            invoice.SupplierName ?? string.Empty,
            CurrencyAmountRules.NormalizeCurrency(invoice.Currency),
            invoice.GrossAmount,
            plan.PersistedLinkedAmount,
            proposed,
            unlinked,
            PurchaseInvoiceRules.LinkageStatusOf(invoice.GrossAmount, proposed),
            PurchaseInvoiceRules.LinkageText(invoice.GrossAmount, proposed, plan.Count, invoice.Currency),
            plan.Count,
            PurchaseInvoiceRules.LinkageRuleText,
            PurchaseInvoiceRules.BoundaryText,
            plan.Lines);
    }

    /// <summary>
    /// 保存关联（草稿专用，整体替换）：在同一 <c>SaveChanges</c> 内删除原关联行并写入新关联行
    /// （校验失败不会留下部分行）；已登记 / 已作废发票拒绝任何关联改动。
    /// </summary>
    public static async Task<PurchaseInvoiceDto> SaveAllocationsAsync(
        IErpDbContext db, long invoiceId, PurchaseInvoiceAllocationSaveRequest? request)
    {
        ArgumentNullException.ThrowIfNull(db);
        var invoice = await LoadAsync(db, invoiceId);
        var plan = await BuildAllocationPlanAsync(db, invoice, request);

        // 草稿期关联属于工作数据：整体替换；一旦登记即冻结，作废也不改写（保留证据）
        var existing = await db.PurchaseInvoiceAllocations
            .Where(a => !a.IsDeleted && a.PurchaseInvoiceId == invoice.Id)
            .ToListAsync();
        db.PurchaseInvoiceAllocations.RemoveRange(existing);
        foreach (var row in plan.Rows) db.PurchaseInvoiceAllocations.Add(row);

        invoice.UpdatedAt = DateTime.Now;
        await db.SaveChangesAsync();

        return await MapAsync(db, invoice);
    }

    /// <summary>关联计划（预览与保存的唯一校验 / 计算入口）</summary>
    private sealed record AllocationPlan(
        decimal PersistedLinkedAmount,
        List<PurchaseInvoiceAllocation> Rows,
        List<PurchaseInvoiceAllocationPreviewLineDto> Lines)
    {
        /// <summary>本次拟关联条数</summary>
        public int Count => Rows.Count;

        /// <summary>本次拟关联金额合计</summary>
        public decimal AllocatedAmount => Rows.Sum(r => r.AllocatedAmount);
    }

    /// <summary>
    /// 构建关联计划（预览与保存共用的唯一校验入口）：
    /// 发票必须处于草稿；同一订单不重复；订单必须存在 / 未删除 / 未取消，且供应商与币种与发票一致；
    /// 关联金额按币种精度取整且大于 0；合计不得超过发票含税总额。全部校验通过后才返回，本方法不写库。
    /// </summary>
    private static async Task<AllocationPlan> BuildAllocationPlanAsync(
        IErpDbContext db, PurchaseInvoice invoice, PurchaseInvoiceAllocationSaveRequest? request)
    {
        PurchaseInvoiceRules.EnsureEditable(invoice.Status, IdentityOf(invoice));
        var currency = CurrencyAmountRules.NormalizeCurrency(invoice.Currency);

        var lines = request?.Lines ?? new List<PurchaseInvoiceAllocationSaveDto>();
        if (lines.Count > PurchaseInvoiceRules.MaxAllocationsPerInvoice)
            throw BusinessException.InvalidParameter(
                $"单张发票最多关联 {PurchaseInvoiceRules.MaxAllocationsPerInvoice} 张采购订单，本次提交 {lines.Count} 条");

        // 同一采购订单在同一发票内只能关联一次：重复提交一律拒绝（不静默合并、不覆盖其中一条）
        var duplicates = lines
            .GroupBy(l => l.PurchaseOrderId)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();
        if (duplicates.Count > 0)
            throw BusinessException.Duplicate(
                $"采购订单（Id={string.Join("、", duplicates)}）在本次关联中重复出现：同一张订单只能关联一次（请合并金额）");

        var orderIds = lines.Select(l => l.PurchaseOrderId).Where(id => id > 0).Distinct().ToList();
        var orders = orderIds.Count == 0
            ? new Dictionary<long, PurchaseOrder>()
            : (await db.PurchaseOrders.AsNoTracking().Where(o => orderIds.Contains(o.Id)).ToListAsync())
                .ToDictionary(o => o.Id);

        // 其他有效发票已占用该订单的金额（一次查询）：仅供预览上下文展示，不改变本单校验口径
        var linkedByOthers = orderIds.Count == 0
            ? new Dictionary<long, decimal>()
            : (await (from allocation in db.PurchaseInvoiceAllocations.AsNoTracking()
                      join owner in db.PurchaseInvoices.AsNoTracking()
                          on allocation.PurchaseInvoiceId equals owner.Id
                      where !allocation.IsDeleted && !owner.IsDeleted
                            && owner.Status != PurchaseInvoiceRules.StatusVoided
                            && owner.Id != invoice.Id
                            && orderIds.Contains(allocation.PurchaseOrderId)
                      select new { allocation.PurchaseOrderId, allocation.AllocatedAmount })
                     .ToListAsync())
                .GroupBy(r => r.PurchaseOrderId)
                .ToDictionary(g => g.Key, g => g.Sum(r => r.AllocatedAmount));

        var rows = new List<PurchaseInvoiceAllocation>();
        var previewLines = new List<PurchaseInvoiceAllocationPreviewLineDto>();
        decimal total = 0;
        var sort = 0;

        foreach (var line in lines)
        {
            if (line.PurchaseOrderId <= 0)
                throw BusinessException.InvalidParameter("请选择要关联的采购订单");

            orders.TryGetValue(line.PurchaseOrderId, out var order);
            PurchaseInvoiceRules.EnsureOrderLinkable(invoice, order);
            if (order is null)
                throw BusinessException.NotFound("采购订单不存在或已删除，不能关联");

            var amount = PurchaseInvoiceRules.NormalizeAllocationAmount(line.AllocatedAmount, invoice.Currency);
            total += amount;
            if (total > invoice.GrossAmount)
                throw BusinessException.RuleConflict(
                    $"关联金额合计 {total} 超过发票含税总额 {invoice.GrossAmount} {currency}："
                    + "请调整关联金额（发票允许部分关联，未关联部分保留为未关联金额）");

            var remark = PurchaseInvoiceRules.NormalizeRemark(line.Remark);
            sort++;

            rows.Add(new PurchaseInvoiceAllocation
            {
                PurchaseInvoiceId = invoice.Id,
                PurchaseOrderId = order.Id,
                OrderNo = order.OrderNo ?? string.Empty,
                OrderDate = order.OrderDate,
                OrderCurrency = CurrencyAmountRules.NormalizeCurrency(order.Currency.ToString()),
                SupplierId = invoice.SupplierId,
                SupplierCode = invoice.SupplierCode ?? string.Empty,
                SupplierName = invoice.SupplierName ?? string.Empty,
                AllocatedAmount = amount,
                Currency = currency,
                SortOrder = sort,
                Remark = remark
            });

            previewLines.Add(new PurchaseInvoiceAllocationPreviewLineDto(
                order.Id,
                order.OrderNo ?? string.Empty,
                order.OrderDate,
                OrderStatusText(order),
                CurrencyAmountRules.NormalizeCurrency(order.Currency.ToString()),
                order.SupplierId,
                invoice.SupplierCode ?? string.Empty,
                invoice.SupplierName ?? string.Empty,
                amount,
                remark,
                true,
                PurchaseInvoiceRules.EvaluateOrderEligibility(invoice, order).Text,
                order.TotalAmount,
                linkedByOthers.TryGetValue(order.Id, out var other) ? other : 0m,
                amount));
        }

        var persisted = await db.PurchaseInvoiceAllocations.AsNoTracking()
            .Where(a => !a.IsDeleted && a.PurchaseInvoiceId == invoice.Id)
            .SumAsync(a => (decimal?)a.AllocatedAmount) ?? 0m;

        return new AllocationPlan(persisted, rows, previewLines);
    }

    // ==================== 4. 登记 / 作废（证据冻结与保留） ====================

    /// <summary>
    /// 登记草稿发票（草稿 → 已登记）：登记前**复核**已持久化关联行仍可权威关联
    /// （订单未删除 / 未取消、供应商与币种一致、合计不超过含税总额），通过后只改发票状态与登记时间。
    /// <para>登记<strong>不</strong>改动采购订单、库存、退税、供应商余额与付款状态，也不生成任何财务单据。</para>
    /// </summary>
    public static async Task<PurchaseInvoiceDto> RecordAsync(IErpDbContext db, long invoiceId)
    {
        ArgumentNullException.ThrowIfNull(db);
        var invoice = await LoadAsync(db, invoiceId);
        PurchaseInvoiceRules.EnsureRecordable(invoice.Status, IdentityOf(invoice));

        await RevalidatePersistedAllocationsAsync(db, invoice);

        invoice.Status = PurchaseInvoiceRules.StatusRecorded;
        invoice.RecordedAt = DateTime.Now;
        invoice.UpdatedAt = DateTime.Now;
        await db.SaveChangesAsync();

        return await MapAsync(db, invoice);
    }

    /// <summary>
    /// 作废发票（草稿 / 已登记 → 已作废）：必须填写作废原因；**保留**发票身份、金额、关联行与审计历史，
    /// 不物理删除、不改写已登记金额与关联，也不产生任何收付款 / 记账动作；重复作废被拒绝。
    /// </summary>
    public static async Task<PurchaseInvoiceDto> VoidAsync(IErpDbContext db, long invoiceId, string? reason)
    {
        ArgumentNullException.ThrowIfNull(db);
        var invoice = await LoadAsync(db, invoiceId);
        PurchaseInvoiceRules.EnsureVoidable(invoice.Status, IdentityOf(invoice));
        var reasonText = PurchaseInvoiceRules.NormalizeVoidReason(reason);

        invoice.Status = PurchaseInvoiceRules.StatusVoided;
        invoice.VoidedAt = DateTime.Now;
        invoice.VoidReason = reasonText;
        invoice.UpdatedAt = DateTime.Now;
        await db.SaveChangesAsync();

        return await MapAsync(db, invoice);
    }

    /// <summary>
    /// 登记前的关联复核（只读）：已持久化关联行必须仍然权威可关联 ——
    /// 订单存在且未删除、未取消、供应商与币种一致，且关联合计不超过含税总额；
    /// 不满足时拒绝登记（不静默丢弃关联行，也不改写采购订单）。
    /// </summary>
    private static async Task RevalidatePersistedAllocationsAsync(IErpDbContext db, PurchaseInvoice invoice)
    {
        var allocations = await db.PurchaseInvoiceAllocations.AsNoTracking()
            .Where(a => !a.IsDeleted && a.PurchaseInvoiceId == invoice.Id)
            .OrderBy(a => a.SortOrder).ThenBy(a => a.Id)
            .ToListAsync();
        if (allocations.Count == 0) return;

        var orderIds = allocations.Select(a => a.PurchaseOrderId).Distinct().ToList();
        var orders = (await db.PurchaseOrders.AsNoTracking()
                .Where(o => orderIds.Contains(o.Id)).ToListAsync())
            .ToDictionary(o => o.Id);

        decimal total = 0;
        foreach (var allocation in allocations)
        {
            orders.TryGetValue(allocation.PurchaseOrderId, out var order);
            if (order is null || order.IsDeleted)
                throw BusinessException.RuleConflict(
                    $"发票「{IdentityOf(invoice)}」关联的采购订单「{allocation.OrderNo}」已不存在或已删除："
                    + "登记前请先调整关联（不存在 / 已删除订单的关联不能形成登记证据）");

            PurchaseInvoiceRules.EnsureOrderLinkable(invoice, order);
            total += allocation.AllocatedAmount;
        }

        if (total > invoice.GrossAmount)
            throw BusinessException.RuleConflict(
                $"发票「{IdentityOf(invoice)}」的关联金额合计 {total} 超过含税总额 {invoice.GrossAmount}："
                + "请先调整关联再登记");
    }

    // ==================== 5. 校验与映射（内部） ====================

    /// <summary>校验后的发票头（全部为服务端权威值：含供应商实体与规范化身份）</summary>
    private sealed record HeaderInput(
        string InvoiceType,
        string InvoiceCode,
        string InvoiceNumber,
        string NormalizedCode,
        string NormalizedNumber,
        DateTime InvoiceDate,
        BaseSupplier Supplier,
        string Currency,
        decimal Net,
        decimal Tax,
        decimal Gross,
        string Remark);

    /// <summary>
    /// 校验发票头（类型 / 代码 / 号码 / 日期 / 供应商 / 币种 / 金额等式）：全部通过后才返回权威值；
    /// 供应商必须存在、未删除且启用（停用 / 删除一律拒绝新增与修改），供应商快照由服务端写入。
    /// </summary>
    private static async Task<HeaderInput> ValidateHeaderAsync(IErpDbContext db, PurchaseInvoiceSaveDto dto)
    {
        var invoiceType = PurchaseInvoiceRules.NormalizeInvoiceType(dto.InvoiceType);
        var invoiceCode = PurchaseInvoiceRules.NormalizeInvoiceCode(dto.InvoiceCode, invoiceType);
        var invoiceNumber = PurchaseInvoiceRules.NormalizeInvoiceNumber(dto.InvoiceNumber);
        var currency = PurchaseInvoiceRules.NormalizeCurrencyStrict(dto.Currency);
        var remark = PurchaseInvoiceRules.NormalizeRemark(dto.Remark);
        var (net, tax, gross) = PurchaseInvoiceRules.ValidateAmounts(
            dto.NetAmount, dto.TaxAmount, dto.GrossAmount, currency);

        if (dto.SupplierId <= 0) throw BusinessException.InvalidParameter("请选择供应商");

        var supplier = await db.BaseSuppliers
                .FirstOrDefaultAsync(s => s.Id == dto.SupplierId && !s.IsDeleted)
            ?? throw BusinessException.NotFound($"供应商（Id={dto.SupplierId}）不存在或已删除，不能登记发票");

        if (supplier.Status != 1)
            throw BusinessException.RuleConflict(
                $"供应商「{supplier.SupplierName}」已停用：停用供应商不能登记新发票（历史发票保持可读）");

        return new HeaderInput(
            invoiceType,
            invoiceCode,
            invoiceNumber,
            PurchaseInvoiceRules.NormalizeIdentityPart(invoiceCode),
            PurchaseInvoiceRules.NormalizeIdentityPart(invoiceNumber),
            (dto.InvoiceDate ?? DateTime.Today).Date,
            supplier,
            currency,
            net,
            tax,
            gross,
            remark);
    }

    /// <summary>
    /// 有效发票身份唯一：同一「供应商 + 发票类型 + 规范化代码 / 号码」在**未作废、未删除**记录内唯一；
    /// 重复一律拒绝（不静默合并、不覆盖）；已作废记录保留可读但不占用身份（可重新登记）。
    /// </summary>
    private static async Task EnsureIdentityAvailableAsync(
        IErpDbContext db, HeaderInput input, long? excludeInvoiceId)
    {
        var excludedId = excludeInvoiceId ?? 0;
        var identity = PurchaseInvoiceRules.IdentityText(
            input.InvoiceType, input.InvoiceCode, input.InvoiceNumber);

        var duplicate = await db.PurchaseInvoices.AsNoTracking()
            .Where(x => !x.IsDeleted
                        && x.Status != PurchaseInvoiceRules.StatusVoided
                        && x.SupplierId == input.Supplier.Id
                        && x.InvoiceType == input.InvoiceType
                        && x.NormalizedInvoiceCode == input.NormalizedCode
                        && x.NormalizedInvoiceNumber == input.NormalizedNumber
                        && x.Id != excludedId)
            .Select(x => new { x.Id, x.InvoiceDate, x.Status })
            .FirstOrDefaultAsync();

        if (duplicate is null) return;

        throw BusinessException.Duplicate(
            $"供应商「{input.Supplier.SupplierName}」已存在同一身份的未作废发票：{identity}"
            + $"（Id={duplicate.Id}，状态 {PurchaseInvoiceRules.StatusText(duplicate.Status)}，"
            + $"开票日期 {duplicate.InvoiceDate:yyyy-MM-dd}）；重复发票被拒绝而不是静默合并"
            + "（如需更正请先作废原记录再重新登记）");
    }

    /// <summary>按 Id 装载未删除发票（不存在 / 已删除 → 数据不存在）</summary>
    private static async Task<PurchaseInvoice> LoadAsync(IErpDbContext db, long invoiceId)
    {
        if (invoiceId <= 0) throw BusinessException.InvalidParameter("发票 Id 不合法");
        return await db.PurchaseInvoices.FirstOrDefaultAsync(x => x.Id == invoiceId && !x.IsDeleted)
            ?? throw BusinessException.NotFound($"供应商采购发票（Id={invoiceId}）不存在或已删除");
    }

    /// <summary>发票对外身份文案（提示与台账共用；与唯一性判定口径一致）</summary>
    private static string IdentityOf(PurchaseInvoice invoice)
    {
        ArgumentNullException.ThrowIfNull(invoice);
        return PurchaseInvoiceRules.IdentityText(
            invoice.InvoiceType, invoice.InvoiceCode, invoice.InvoiceNumber);
    }

    /// <summary>单张发票映射（详情与单条操作返回用；只读标注，不写库）</summary>
    private static async Task<PurchaseInvoiceDto> MapAsync(IErpDbContext db, PurchaseInvoice invoice)
    {
        var mapped = await MapManyAsync(db, new List<PurchaseInvoice> { invoice });
        return mapped[0];
    }

    /// <summary>
    /// 批量映射（台账分页用）：关联行 / 采购订单 / 供应商各一次批量查询，绝无逐行数据库查询；
    /// 供应商可用性与订单可用性都是**只读标注**：停用 / 删除 / 取消不改变历史证据的可读性。
    /// </summary>
    private static async Task<List<PurchaseInvoiceDto>> MapManyAsync(
        IErpDbContext db, IReadOnlyList<PurchaseInvoice> invoices)
    {
        if (invoices.Count == 0) return new List<PurchaseInvoiceDto>();

        var invoiceIds = invoices.Select(i => i.Id).ToList();
        var allocationRows = await db.PurchaseInvoiceAllocations.AsNoTracking()
            .Where(a => !a.IsDeleted && invoiceIds.Contains(a.PurchaseInvoiceId))
            .OrderBy(a => a.SortOrder).ThenBy(a => a.Id)
            .ToListAsync();

        var allocationsByInvoice = allocationRows
            .GroupBy(a => a.PurchaseInvoiceId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<PurchaseInvoiceAllocation>)g.ToList());

        var orderIds = allocationRows.Select(a => a.PurchaseOrderId).Distinct().ToList();
        var orders = orderIds.Count == 0
            ? new Dictionary<long, PurchaseOrder>()
            : (await db.PurchaseOrders.AsNoTracking().Where(o => orderIds.Contains(o.Id)).ToListAsync())
                .ToDictionary(o => o.Id);

        var supplierIds = invoices.Select(i => i.SupplierId).Distinct().ToList();
        var suppliers = (await db.BaseSuppliers.AsNoTracking()
                .Where(s => supplierIds.Contains(s.Id)).ToListAsync())
            .ToDictionary(s => s.Id);

        return invoices.Select(invoice =>
            Map(
                invoice,
                allocationsByInvoice.TryGetValue(invoice.Id, out var rows)
                    ? rows
                    : (IReadOnlyList<PurchaseInvoiceAllocation>)new List<PurchaseInvoiceAllocation>(),
                suppliers,
                orders)).ToList();
    }

    /// <summary>发票实体 → 台账 DTO（含关联行、已关联 / 未关联金额与可用性标注；纯映射，不写库）</summary>
    private static PurchaseInvoiceDto Map(
        PurchaseInvoice invoice,
        IReadOnlyList<PurchaseInvoiceAllocation> allocations,
        Dictionary<long, BaseSupplier> suppliers,
        Dictionary<long, PurchaseOrder> orders)
    {
        ArgumentNullException.ThrowIfNull(invoice);

        var currency = CurrencyAmountRules.NormalizeCurrency(invoice.Currency);
        var linked = allocations.Sum(a => a.AllocatedAmount);
        var unlinked = invoice.GrossAmount - linked;
        if (unlinked < 0) unlinked = 0;

        suppliers.TryGetValue(invoice.SupplierId, out var supplier);
        var supplierAvailable = PurchaseInvoiceRules.IsSupplierSelectable(supplier);

        var rows = allocations.Select(a =>
        {
            orders.TryGetValue(a.PurchaseOrderId, out var order);
            var orderAvailable = order is not null && !order.IsDeleted;

            var availabilityText = orderAvailable
                ? (order!.Status == DocumentStatus.Cancelled
                    ? "订单已取消：历史关联保留可读，不再作为可关联订单"
                    : $"订单可用（{OrderStatusText(order)}）")
                : "订单已删除或不存在：历史关联保留可读";

            return new PurchaseInvoiceAllocationDto(
                a.Id,
                a.PurchaseOrderId,
                a.OrderNo ?? string.Empty,
                a.OrderDate,
                OrderStatusText(order),
                CurrencyAmountRules.NormalizeCurrency(a.Currency),
                a.SupplierId,
                a.SupplierCode ?? string.Empty,
                a.SupplierName ?? string.Empty,
                a.AllocatedAmount,
                a.SortOrder,
                a.Remark ?? string.Empty,
                orderAvailable,
                availabilityText);
        }).ToList();

        return new PurchaseInvoiceDto(
            invoice.Id,
            invoice.InvoiceType ?? string.Empty,
            invoice.InvoiceType ?? string.Empty,
            invoice.InvoiceCode ?? string.Empty,
            invoice.InvoiceNumber ?? string.Empty,
            IdentityOf(invoice),
            invoice.InvoiceDate,
            invoice.SupplierId,
            invoice.SupplierCode ?? string.Empty,
            invoice.SupplierName ?? string.Empty,
            supplierAvailable,
            PurchaseInvoiceRules.SupplierAvailabilityText(supplier),
            currency,
            CurrencyAmountRules.PrecisionOf(currency),
            invoice.NetAmount,
            invoice.TaxAmount,
            invoice.GrossAmount,
            invoice.Status,
            PurchaseInvoiceRules.StatusText(invoice.Status),
            invoice.Status == PurchaseInvoiceRules.StatusDraft,
            invoice.Status == PurchaseInvoiceRules.StatusRecorded,
            invoice.Status == PurchaseInvoiceRules.StatusVoided,
            invoice.RecordedAt,
            invoice.VoidedAt,
            invoice.VoidReason ?? string.Empty,
            invoice.Remark ?? string.Empty,
            rows.Count,
            linked,
            unlinked,
            PurchaseInvoiceRules.LinkageStatusOf(invoice.GrossAmount, linked),
            PurchaseInvoiceRules.LinkageText(invoice.GrossAmount, linked, rows.Count, currency),
            invoice.CreatedAt,
            invoice.UpdatedAt,
            PurchaseInvoiceRules.AmountEquationText,
            PurchaseInvoiceRules.LinkageRuleText,
            PurchaseInvoiceRules.BoundaryText,
            rows);
    }

    /// <summary>采购订单状态文案（只读标注；订单不存在时照实说明「不存在或已删除」，不假定为可用）</summary>
    private static string OrderStatusText(PurchaseOrder? order)
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
