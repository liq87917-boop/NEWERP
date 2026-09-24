using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ERP.Api.Controllers;

/// <summary>
/// 供应商比价（PurchaseQuote）→ 采购订单（PurchaseOrder）「带入预填 / 直接生成」共用逻辑（ERP-020）。
/// 业务链：采购需求 → 供应商比价（同一 QuoteNo 多家报价）→ 选中供应商 → 采购订单。
/// </summary>
/// <remarks>
/// 设计口径（与本任务「不改生产库结构」的边界一致）：
/// 1) 只使用既有 EF 采购订单路径与既有列：不新增 / 不修改任何数据库结构，也不触碰存储过程；
/// 2) 「比价行 → 采购订单」的权威链接沿用比价行既有列 <see cref="PurchaseQuote.RefOrderNo" />：
///    转换前该列按原语义保存「关联销售订单号」（会先解析进采购订单的归属销售订单字段），
///    转换后写回生成的采购单号，作为来源留痕与重复生成的兜底判据；
/// 3) 「带入预填」返回**未落库**的采购订单草稿（不占用单据号），由前端打开「采购订单 → 新增」表单继续编辑后再保存
///    （保存走 <c>POST /api/purchase-orders</c>，同一套服务端复核）；
/// 4) 「直接生成」在服务端一次落库，并以来源判据守卫重复生成，绝不更新 / 覆盖既有采购订单；
/// 5) 明细数量 / 单价 / 金额与订单总额一律由服务端按采购订单口径复核
///    （复用 <see cref="PurchaseOrderController.Calculate" /> 与 <see cref="PurchaseOrderController.Validate" />）。
/// </remarks>
public static class PurchaseQuoteConversion
{
    /// <summary>来源单据类型标识（写入带入预填响应）</summary>
    public const string PurchaseQuoteSourceType = "PurchaseQuote";

    /// <summary>比价行状态：已选中（唯一可转采购订单的选中态）</summary>
    public const string SelectedStatus = "已选中";

    /// <summary>比价行状态：已放弃（即使误勾选中也不允许转采购订单）</summary>
    public const string DiscardedStatus = "已放弃";

    /// <summary>比价行状态：已转采购订单（转换成功后由服务端写入）</summary>
    public const string ConvertedStatus = "已转采购订单";

    /// <summary>采购订单汇率缺省值：比价表没有汇率列，按 1 处理（不臆造汇率）</summary>
    private const decimal DefaultExchangeRate = 1m;

    /// <summary>
    /// 按选中的比价行构造采购订单草稿（未落库、无单号）：
    /// 先执行「资格 + 重复生成」守卫，再按固定映射表生成主表与一行明细，最后由服务端复核金额与合计。
    /// 「带入预填」与「直接生成」共用本方法，保证两条路径口径完全一致。
    /// </summary>
    /// <exception cref="BusinessException">
    /// 未选中供应商 / 已放弃 / 未维护供应商 / 已生成过采购订单时抛出（错误码 RuleConflict）；
    /// 需求数量或报价单价非法时抛出（错误码 InvalidParameter）。
    /// </exception>
    public static async Task<PurchaseOrder> BuildDraftAsync(IErpDbContext db, PurchaseQuote quote,
        CancellationToken ct = default)
        => await BuildDraftAsync(db, quote, null, ct);

    /// <summary>
    /// 重载（ERP-027 批次路径）：<paramref name="lookup"/> 为「批次一次性预取」的既有单据索引，
    /// 把重复生成守卫与归属销售订单解析的**按行查询**换成预取匹配（判定口径逐字一致，不复制守卫逻辑）；
    /// 传 null 时与单行路径完全相同（按行各查一次库）。
    /// </summary>
    public static async Task<PurchaseOrder> BuildDraftAsync(IErpDbContext db, PurchaseQuote quote,
        PurchaseQuoteBatchLookup? lookup, CancellationToken ct = default)
    {
        var generated = lookup is null
            ? await FindGeneratedOrderAsync(db, quote, ct)
            : lookup.FindGeneratedOrder(quote);
        if (generated is not null)
            throw BusinessException.RuleConflict($"该比价行已生成采购订单：{generated.OrderNo}，不能重复生成");
        if (quote.Status == ConvertedStatus)
            throw BusinessException.RuleConflict($"该比价行已转为采购订单（{RefOrderNoText(quote)}），不能重复生成");

        if (quote.Status == DiscardedStatus)
            throw BusinessException.RuleConflict("已放弃的比价行不能生成采购订单");
        if (!quote.IsSelected)
            throw BusinessException.RuleConflict("该比价行未选中供应商，请先勾选「选中该供应商」并保存后再生成采购订单");
        if (quote.SupplierId is null or <= 0)
            throw BusinessException.RuleConflict("比价行未维护供应商，不能生成采购订单");

        var (salesOrderId, salesOrderNo) = await ResolveOwningSalesOrderAsync(db, quote, lookup, ct);
        return MapDraft(quote, salesOrderId, salesOrderNo);
    }

    // ==================== 重复生成守卫（同一比价行仅一张采购订单） ====================

    /// <summary>
    /// 重复生成守卫（两道，全部只读既有列，不新增结构）：
    /// ① 比价行 <see cref="PurchaseQuote.RefOrderNo" /> 指向的未删除采购订单
    ///    —— 即使有人把比价行状态人工改回「已选中」，也由该链接兜底拦截；
    /// ② 采购订单备注中保留的来源标记（<see cref="SourceMarker" />）
    ///    —— 防止人工清空 <see cref="PurchaseQuote.RefOrderNo" /> 后再次生成。
    /// 已软删除的历史采购订单不阻断重新生成（与「报价单 / PI 转销售订单」同一口径）。
    /// </summary>
    public static async Task<PurchaseOrder?> FindGeneratedOrderAsync(IErpDbContext db, PurchaseQuote quote,
        CancellationToken ct = default)
    {
        var refNo = (quote.RefOrderNo ?? string.Empty).Trim();
        if (refNo.Length > 0)
        {
            var byRef = await db.PurchaseOrders.AsNoTracking()
                .FirstOrDefaultAsync(o => o.OrderNo == refNo && !o.IsDeleted, ct);
            if (byRef is not null) return byRef;
        }

        var marker = SourceMarker(quote);
        return await db.PurchaseOrders.AsNoTracking()
            .FirstOrDefaultAsync(o => o.Remark.Contains(marker) && !o.IsDeleted, ct);
    }

    /// <summary>
    /// 转换落库后的来源留痕（不改任何数据库结构）：比价行状态置「已转采购订单」，
    /// 并把生成的采购单号写回 <see cref="PurchaseQuote.RefOrderNo" />，
    /// 作为「比价行 → 采购订单」的唯一权威链接与重复生成兜底判据。
    /// </summary>
    public static void MarkConverted(PurchaseQuote quote, string orderNo)
    {
        quote.Status = ConvertedStatus;
        quote.RefOrderNo = Clamp(orderNo, 50);
        quote.UpdatedAt = DateTime.Now;
    }

    // ==================== 映射与复核 ====================

    /// <summary>
    /// 固定映射表（比价行 → 采购订单；未列出的字段一律保持采购订单默认值，不臆造业务数据）：
    /// 供应商 ← SupplierId（必填校验）；订单日期 = 今天；币种 ← Currency 文本（无法识别回退 CNY）；
    /// 汇率 = 1；是否含税 ← TaxIncluded；税率 = 0（比价表无税率列）；
    /// 付款条件 ← PaymentTerms；交货日期 = 今天 + DeliveryDays（≤0 留空）；
    /// 供应商确认交期 = 交货日期（比价交期本身即供应商承诺）；归属客户 ← CustomerId / CustomerName；
    /// 归属销售订单 ← RefOrderNo 解析出的销售订单（匹配不到留空）；备注 ← 比价备注 + 来源标记；
    /// 明细 = 该比价行一行（商品 / 规格 / 单位 / 数量 / 报价单价，商品未引用档案时 ProductId 按 0 占位）。
    /// </summary>
    private static PurchaseOrder MapDraft(PurchaseQuote quote, long? owningSalesOrderId, string owningSalesOrderNo)
    {
        var orderDate = DateTime.Today;
        var deliveryDate = quote.DeliveryDays > 0 ? orderDate.AddDays(quote.DeliveryDays) : (DateTime?)null;

        var order = new PurchaseOrder
        {
            OrderNo = string.Empty,                      // 预填不占用单据号，直接生成时由调用方按字轨赋值
            OrderDate = orderDate,
            SupplierId = quote.SupplierId ?? 0,
            BuyerId = null,                              // 比价表无采购员列，留空由采购员在采购订单上指定
            Currency = ParseCurrency(quote.Currency),
            ExchangeRate = DefaultExchangeRate,
            TaxIncluded = quote.TaxIncluded,
            TaxRate = 0m,
            PaymentTerms = Clamp(quote.PaymentTerms, 200),
            DeliveryDate = deliveryDate,
            SupplierConfirmedDate = deliveryDate,
            OwningCustomerId = quote.CustomerId,
            OwningCustomerName = Clamp(quote.CustomerName, 200),
            OwningSalesOrderId = owningSalesOrderId,
            OwningSalesOrderNo = Clamp(owningSalesOrderNo, 50),
            AdvanceOnBehalf = false,                     // 是否代垫由采购员按实际决定，带入不自动置位
            ArrivalProgress = string.Empty,
            QcStatus = string.Empty,
            SettlementProgress = string.Empty,
            ContractNo = string.Empty,
            Status = DocumentStatus.Pending,
            Remark = MergeRemark(quote.Remark, SourceMarker(quote)),
            CreatedAt = DateTime.Now,
            Details = new List<PurchaseOrderDetail> { NewDetail(quote, deliveryDate) }
        };

        Revalidate(order);
        PurchaseOrderController.Calculate(order);
        PurchaseOrderController.Validate(order);
        return order;
    }

    /// <summary>比价行币种文本 → 采购订单币种枚举：按枚举名不区分大小写解析，无法识别按人民币（比价表默认 CNY）处理</summary>
    public static Currency ParseCurrency(string? currency)
        => Enum.TryParse<Currency>(currency?.Trim(), ignoreCase: true, out var parsed) ? parsed : Currency.CNY;

    /// <summary>比价行 → 采购订单明细（金额由服务端重算，不采信来源报价总额）</summary>
    private static PurchaseOrderDetail NewDetail(PurchaseQuote quote, DateTime? deliveryDate)
        => new()
        {
            ProductId = quote.ProductId ?? 0,
            ProductName = Clamp(quote.ProductName, 200),
            Spec = Clamp(quote.Spec, 200),
            Unit = Clamp(quote.Unit, 20),
            Quantity = quote.Quantity,
            UnitPrice = quote.QuotePrice,
            Amount = Math.Round(quote.Quantity * quote.QuotePrice, 2),
            DeliveryDate = deliveryDate,
            Remark = Clamp(quote.Remark, 500),
            CreatedAt = DateTime.Now
        };

    /// <summary>
    /// 服务端复核带入结果：明细数量 / 单价逐行校验并重算金额。
    /// 订单总额随后由采购订单口径 <see cref="PurchaseOrderController.Calculate" /> 计算，
    /// 保证「带入生成 + 人工编辑」与「页面手工录入」两条路径完全同口径。
    /// </summary>
    private static void Revalidate(PurchaseOrder order)
    {
        var line = 0;
        foreach (var d in order.Details)
        {
            line++;
            if (d.Quantity <= 0)
                throw BusinessException.InvalidParameter($"采购订单明细第 {line} 行数量必须大于 0");
            if (d.UnitPrice < 0)
                throw BusinessException.InvalidParameter($"采购订单明细第 {line} 行单价不能为负数");
            d.Amount = Math.Round(d.Quantity * d.UnitPrice, 2);
        }
    }

    /// <summary>
    /// 归属销售订单解析：比价行 <see cref="PurchaseQuote.RefOrderNo" /> 转换前的语义为「关联销售订单号」
    /// （代理采购时该采购为哪张销售订单备货），按单号匹配未删除销售订单后写入采购订单归属字段；
    /// 匹配不到（含该列已是采购单号或留空）时留空，不臆造关联。
    /// </summary>
    private static async Task<(long? Id, string No)> ResolveOwningSalesOrderAsync(IErpDbContext db,
        PurchaseQuote quote, PurchaseQuoteBatchLookup? lookup, CancellationToken ct)
    {
        var refNo = (quote.RefOrderNo ?? string.Empty).Trim();
        if (refNo.Length == 0) return (null, string.Empty);

        var order = lookup is null
            ? await db.SalesOrders.AsNoTracking()
                .FirstOrDefaultAsync(o => o.OrderNo == refNo && !o.IsDeleted, ct)
            : lookup.FindSalesOrder(refNo);
        return order is null ? (null, string.Empty) : (order.Id, order.OrderNo);
    }

    // ==================== 比价批次 → 采购订单（ERP-027：批次多行合并） ====================

    /// <summary>来源标记前缀（同一批次的全部行共用，用于一次查回该批次的来源采购订单，见 <see cref="PurchaseQuoteBatchLookup" />）</summary>
    public static string SourceMarkerPrefix(string quoteNo) => $"来源比价 {quoteNo}（比价行 #";

    /// <summary>
    /// 解析本次要处理的比价行（只读）：优先按批次号 <paramref name="quoteNo"/>，为空时按比价行
    /// <paramref name="lineId"/> 反查其所属批次；<paramref name="lineIds"/> 非空时只保留指定行
    /// （必须属于同一批次，否则抛 InvalidParameter）。
    /// </summary>
    public static async Task<List<PurchaseQuote>> ResolveBatchLinesAsync(IErpDbContext db, string? quoteNo, long? lineId,
        IReadOnlyCollection<long>? lineIds = null, CancellationToken ct = default)
    {
        var batchNo = (quoteNo ?? string.Empty).Trim();
        if (batchNo.Length == 0)
        {
            if (lineId is null or <= 0)
                throw BusinessException.InvalidParameter("请提供比价批次号或比价行 Id");

            var anchor = await db.PurchaseQuotes.AsNoTracking()
                .FirstOrDefaultAsync(q => q.Id == lineId && !q.IsDeleted, ct)
                ?? throw BusinessException.NotFound("比价记录不存在");
            batchNo = anchor.QuoteNo;
        }

        var lines = await db.PurchaseQuotes.AsNoTracking()
            .Where(q => q.QuoteNo == batchNo && !q.IsDeleted)
            .OrderBy(q => q.Id)
            .ToListAsync(ct);
        if (lines.Count == 0) throw BusinessException.NotFound($"比价批次 {batchNo} 不存在或已删除");

        if (lineIds is { Count: > 0 })
        {
            var batchIds = lines.Select(l => l.Id).ToHashSet();
            var outside = lineIds.Where(id => !batchIds.Contains(id)).ToList();
            if (outside.Count > 0)
                throw BusinessException.InvalidParameter($"比价行 #{outside[0]} 不属于比价批次 {batchNo}");

            var wanted = lineIds.ToHashSet();
            lines = lines.Where(l => wanted.Contains(l.Id)).ToList();
        }
        return lines;
    }

    /// <summary>
    /// 批次构造（只读、不落库）：解析来源行 → 逐行复用单行权威守卫与映射（<see cref="BuildDraftAsync(IErpDbContext, PurchaseQuote, PurchaseQuoteBatchLookup?, CancellationToken)" />）→
    /// 按兼容分组键合并为「一批采购订单草稿」。
    /// 不合格行**不静默丢弃**，而是作为 <see cref="PurchaseQuoteBatchSkip" /> 显式列出原因。
    /// </summary>
    public static async Task<PurchaseQuoteBatchBuildResult> BuildBatchAsync(IErpDbContext db, string? quoteNo, long? lineId,
        IReadOnlyCollection<long>? lineIds = null, CancellationToken ct = default)
    {
        var lines = await ResolveBatchLinesAsync(db, quoteNo, lineId, lineIds, ct);
        var build = new PurchaseQuoteBatchBuildResult { SourceNo = lines[0].QuoteNo, LineCount = lines.Count };

        // 批次内一次性预取既有单据索引：重复生成守卫与归属销售订单解析不再按行查库（批次内固定 2 次查询）
        var lookup = await PurchaseQuoteBatchLookup.LoadAsync(db, lines, ct);

        var drafts = new List<(PurchaseQuote Line, PurchaseOrder Draft)>();
        foreach (var line in lines)
        {
            try
            {
                drafts.Add((line, await BuildDraftAsync(db, line, lookup, ct)));
            }
            catch (BusinessException ex)
            {
                build.Skipped.Add(new PurchaseQuoteBatchSkip
                {
                    LineId = line.Id,
                    QuoteNo = line.QuoteNo,
                    ProductName = line.ProductName,
                    Reason = ex.Message
                });
            }
        }

        var index = new Dictionary<BatchGroupKey, PurchaseQuoteBatchGroup>();
        foreach (var (line, draft) in drafts)
        {
            var key = BatchGroupKey.From(draft);
            if (!index.TryGetValue(key, out var group))
            {
                group = new PurchaseQuoteBatchGroup();
                index[key] = group;
                build.Groups.Add(group);
            }
            group.Lines.Add(line);
            group.LineDrafts.Add(draft);
        }

        foreach (var group in build.Groups)
            group.Draft = MergeGroup(group.Lines, group.LineDrafts);
        return build;
    }

    /// <summary>
    /// 组内多行合并为一张采购订单草稿：表头取组内统一口径（分组键保证完全一致），
    /// 明细按来源行顺序逐行铺开（每行保留自己的交期），订单交期取组内最晚一行（一单覆盖所有行），
    /// 备注与明细备注逐行写入来源标记，最后由采购订单口径重算总额并校验。
    /// </summary>
    private static PurchaseOrder MergeGroup(IReadOnlyList<PurchaseQuote> lines, IReadOnlyList<PurchaseOrder> drafts)
    {
        var head = drafts[0];
        var deliveryDates = drafts.Where(d => d.DeliveryDate.HasValue).Select(d => d.DeliveryDate!.Value).ToList();
        var deliveryDate = deliveryDates.Count > 0 ? deliveryDates.Max() : (DateTime?)null;

        var order = new PurchaseOrder
        {
            OrderNo = string.Empty,                      // 计划 / 预填不占用单据号，直接生成时由调用方按字轨赋值
            OrderDate = head.OrderDate,
            SupplierId = head.SupplierId,
            BuyerId = head.BuyerId,
            Currency = head.Currency,
            ExchangeRate = head.ExchangeRate,
            TaxIncluded = head.TaxIncluded,
            TaxRate = head.TaxRate,
            PaymentTerms = head.PaymentTerms,
            DeliveryDate = deliveryDate,
            SupplierConfirmedDate = deliveryDate,
            OwningCustomerId = head.OwningCustomerId,
            OwningCustomerName = head.OwningCustomerName,
            OwningSalesOrderId = head.OwningSalesOrderId,
            OwningSalesOrderNo = head.OwningSalesOrderNo,
            AdvanceOnBehalf = head.AdvanceOnBehalf,
            ContractNo = head.ContractNo,
            Status = DocumentStatus.Pending,
            Remark = MergeBatchRemark(lines),
            CreatedAt = DateTime.Now,
            Details = new List<PurchaseOrderDetail>()
        };

        for (var i = 0; i < lines.Count; i++)
        {
            var detail = drafts[i].Details[0];           // 单行草稿固定一行明细
            detail.Remark = Clamp(MergeRemark(lines[i].Remark, SourceMarker(lines[i])), 500);   // 行级来源留痕
            order.Details.Add(detail);
        }

        Revalidate(order);
        PurchaseOrderController.Calculate(order);
        PurchaseOrderController.Validate(order);
        return order;
    }

    /// <summary>
    /// 批次转采购订单（直接生成）：先统一取号（全部取号发生在任何单据落库之前，避免半成品数据），
    /// 再一次性新增全部采购订单并逐行回写来源留痕，最后单次 SaveChanges 提交（只新增，绝不覆盖既有订单）。
    /// 没有任何合格行时抛 <see cref="BusinessException" />（RuleConflict），不落库、不占号。
    /// </summary>
    public static async Task<PurchaseQuoteBatchConversionResult> ConvertBatchAsync(IErpDbContext db,
        IDocumentNumberService noService, PurchaseQuoteBatchConversionRequest? request, CancellationToken ct = default)
    {
        if (request is null) throw BusinessException.InvalidParameter("请求内容不能为空");

        var build = await BuildBatchAsync(db, request.QuoteNo, request.LineId, request.LineIds, ct);
        if (build.Groups.Count == 0)
            throw BusinessException.RuleConflict(
                $"比价批次 {build.SourceNo} 没有可转换的「已选中」行：{SkipSummary(build.Skipped)}");

        var assigned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in build.Groups)
        {
            var candidate = await noService.GenerateAsync(DocumentType.PurchaseOrder);
            group.Draft.OrderNo = await EnsureUniqueOrderNoAsync(db, candidate, assigned, ct);
        }

        foreach (var group in build.Groups)
        {
            db.PurchaseOrders.Add(group.Draft);
        }

        // 来源行留痕必须写在**受跟踪**的实体上（批次行是按 AsNoTracking 读出来做守卫 / 分组的，
        // 直接改副本不会落库）：此处一次性取回本批次要留痕的行（1 次查询），逐行回写状态与采购单号。
        var orderNoByLineId = build.Groups
            .SelectMany(g => g.Lines.Select(l => (LineId: l.Id, OrderNo: g.Draft.OrderNo)))
            .ToDictionary(x => x.LineId, x => x.OrderNo);
        var lineIds = orderNoByLineId.Keys.ToList();
        var trackedLines = await db.PurchaseQuotes.Where(q => lineIds.Contains(q.Id)).ToListAsync(ct);
        foreach (var line in trackedLines) MarkConverted(line, orderNoByLineId[line.Id]);

        await db.SaveChangesAsync(ct);

        return new PurchaseQuoteBatchConversionResult
        {
            SourceType = PurchaseQuoteSourceType,
            SourceNo = build.SourceNo,
            OrderCount = build.Groups.Count,
            ConvertedLineCount = build.Groups.Sum(g => g.Lines.Count),
            TotalAmount = build.Groups.Sum(g => g.Draft.TotalAmount),
            Skipped = build.Skipped,
            Orders = build.Groups.Select(g => new PurchaseQuoteBatchOrderResult
            {
                Id = g.Draft.Id,
                OrderNo = g.Draft.OrderNo,
                SupplierId = g.Draft.SupplierId,
                SupplierName = Clamp(g.Lines[0].SupplierName, 200),
                Currency = g.Draft.Currency.ToString(),
                LineIds = g.Lines.Select(l => l.Id).ToList(),
                LineCount = g.Lines.Count,
                TotalAmount = g.Draft.TotalAmount
            }).ToList()
        };
    }

    /// <summary>批次构造结果：兼容分组（每组 = 一张采购订单）+ 被显式跳过的来源行</summary>
    public sealed class PurchaseQuoteBatchBuildResult
    {
        /// <summary>比价批次号</summary>
        public string SourceNo { get; set; } = string.Empty;

        /// <summary>批次内参与判定的比价行数（含不合格行）</summary>
        public int LineCount { get; set; }

        /// <summary>兼容分组（顺序 = 组内首行的比价行 Id 升序）</summary>
        public List<PurchaseQuoteBatchGroup> Groups { get; } = new();

        /// <summary>不合格行及原因（未选中 / 已放弃 / 已转 / 未维护供应商 / 数量或单价非法）</summary>
        public List<PurchaseQuoteBatchSkip> Skipped { get; } = new();
    }

    /// <summary>组内可合并的比价行集合：来源行 + 逐行草稿 + 合并后的采购订单草稿</summary>
    public sealed class PurchaseQuoteBatchGroup
    {
        /// <summary>来源比价行（按比价行 Id 升序，与明细行一一对应）</summary>
        public List<PurchaseQuote> Lines { get; } = new();

        /// <summary>逐行草稿（合并前：保留每行独立交期 / 金额 / 来源标记口径）</summary>
        public List<PurchaseOrder> LineDrafts { get; } = new();

        /// <summary>合并后的采购订单草稿（未落库、无单号）</summary>
        public PurchaseOrder Draft { get; set; } = new();
    }

    /// <summary>
    /// 分组键（合并为同一张采购订单的**必要条件**）：供应商 + 币种 + 归属客户 + 归属销售订单 +
    /// 付款条件（去空格、不区分大小写）+ 是否含税。组内这些采购订单表头字段完全一致，
    /// 合并时才不会出现「一张订单两套表头」；不一致的行保持独立成单，绝不静默改写来源值。
    /// </summary>
    private readonly record struct BatchGroupKey(long SupplierId, Currency Currency, long OwningCustomerId,
        long OwningSalesOrderId, string PaymentTerms, bool TaxIncluded)
    {
        public static BatchGroupKey From(PurchaseOrder draft) => new(
            draft.SupplierId,
            draft.Currency,
            draft.OwningCustomerId ?? 0,
            draft.OwningSalesOrderId ?? 0,
            (draft.PaymentTerms ?? string.Empty).Trim().ToUpperInvariant(),
            draft.TaxIncluded);
    }

    /// <summary>
    /// 批次内采购单号唯一性守卫：以单据号服务（字轨）返回的候选号为准，
    /// 与库内既有采购单号或本批次已分配的单号重复时，按 ERP-019 单证编号的既有约定追加 <c>-2 / -3 …</c>（最多 200 次）。
    /// </summary>
    /// <remarks>
    /// 单据号规则表未配置时，单据号服务按「单据数量 + 毫秒后四位」兜底，同一毫秒内连续取号可能得到相同候选号，
    /// 因此批次生成必须在落库前显式保证号互不相同（全部取号在任何单据落库之前完成，避免半成品数据）。
    /// </remarks>
    public static async Task<string> EnsureUniqueOrderNoAsync(IErpDbContext db, string candidate,
        ISet<string> assignedInBatch, CancellationToken ct = default)
    {
        var normalized = (candidate ?? string.Empty).Trim();
        if (normalized.Length == 0) throw BusinessException.InvalidParameter("采购单号候选值不能为空");

        for (var i = 1; i <= 200; i++)
        {
            var no = i == 1 ? normalized : $"{normalized}-{i}";
            if (assignedInBatch.Contains(no)) continue;
            if (await db.PurchaseOrders.AsNoTracking().AnyAsync(o => o.OrderNo == no && !o.IsDeleted, ct)) continue;

            assignedInBatch.Add(no);
            return no;
        }
        throw BusinessException.RuleConflict($"采购单号 {normalized} 连续冲突，无法为比价批次生成唯一单号");
    }

    /// <summary>把批次构造结果映射为只读计划响应（含每组未落库草稿、合格行数、服务端重算合计与跳过原因）</summary>
    public static PurchaseQuoteBatchPlan BuildPlan(PurchaseQuoteBatchBuildResult build) => new()
    {
        SourceType = PurchaseQuoteSourceType,
        SourceNo = build.SourceNo,
        BatchLineCount = build.LineCount,
        EligibleLineCount = build.Groups.Sum(g => g.Lines.Count),
        GroupCount = build.Groups.Count,
        TotalAmount = build.Groups.Sum(g => g.Draft.TotalAmount),
        Skipped = build.Skipped,
        Groups = build.Groups.Select(g => new PurchaseQuoteBatchGroupPlan
        {
            SupplierId = g.Draft.SupplierId,
            SupplierName = Clamp(g.Lines[0].SupplierName, 200),
            Currency = g.Draft.Currency.ToString(),
            OwningCustomerId = g.Draft.OwningCustomerId,
            OwningCustomerName = g.Draft.OwningCustomerName,
            OwningSalesOrderNo = g.Draft.OwningSalesOrderNo,
            PaymentTerms = g.Draft.PaymentTerms,
            TaxIncluded = g.Draft.TaxIncluded,
            DeliveryDate = g.Draft.DeliveryDate,
            LineIds = g.Lines.Select(l => l.Id).ToList(),
            LineCount = g.Lines.Count,
            TotalAmount = g.Draft.TotalAmount,
            Order = g.Draft
        }).ToList()
    };

    /// <summary>跳过原因摘要（按原因分组计数，便于一次看清批次内有多少行不合格）</summary>
    public static string SkipSummary(IReadOnlyList<PurchaseQuoteBatchSkip> skipped)
    {
        if (skipped.Count == 0) return "该批次没有「已选中」的比价行";
        var parts = skipped.GroupBy(s => s.Reason).Select(gr => $"{gr.Key}（{gr.Count()} 行）");
        return $"{skipped.Count} 行不合格：" + string.Join("；", parts);
    }

    /// <summary>
    /// 组内多行备注合并：各行的父备注（去重、按行序）+ 每行来源标记，按 500 字符截断。
    /// 单行时与单行转换的「备注 ｜ 来源标记」完全一致；每行的来源标记同时写进对应明细行备注，行级可追溯。
    /// 父备注过长时**先截断父备注、保留来源标记**（来源标记是重复生成的第二道判据，不能因截断丢失）。
    /// </summary>
    private static string MergeBatchRemark(IReadOnlyList<PurchaseQuote> lines)
    {
        var markers = string.Join(" ｜ ", lines.Select(SourceMarker));

        var parents = new List<string>();
        foreach (var line in lines)
        {
            var text = (line.Remark ?? string.Empty).Trim();
            if (text.Length > 0 && !parents.Contains(text)) parents.Add(text);
        }
        if (parents.Count == 0) return Clamp(markers, 500);
        if (markers.Length + 3 >= 500) return Clamp(markers, 500);        // 极端批次：标记本身超长（逐行标记仍见明细备注）

        var parentText = string.Join(" ｜ ", parents);
        var budget = 500 - markers.Length - 3;
        if (parentText.Length > budget) parentText = parentText[..budget];
        return $"{parentText} ｜ {markers}";
    }

    // ==================== 文本工具 ====================

    /// <summary>来源标记（写入采购订单备注，同时作为重复生成的第二道判据）：不含日期等易变内容，可稳定比对</summary>
    public static string SourceMarker(PurchaseQuote quote)
        => $"来源比价 {quote.QuoteNo}（比价行 #{quote.Id}）";

    /// <summary>备注 + 来源标记合并（采购订单备注列 500 字符，超长按列长截断）</summary>
    private static string MergeRemark(string? remark, string marker)
    {
        var text = (remark ?? string.Empty).Trim();
        return Clamp(text.Length > 0 ? $"{text} ｜ {marker}" : marker, 500);
    }

    /// <summary>比价行上记录的采购单号（用于重复生成的错误提示）</summary>
    private static string RefOrderNoText(PurchaseQuote quote)
        => (quote.RefOrderNo ?? string.Empty).Trim() is { Length: > 0 } no ? $"采购单号 {no}" : "未记录采购单号";

    /// <summary>按目标列长度截断（比价表与采购订单列长不完全一致时避免超长写入失败）</summary>
    private static string Clamp(string? value, int maxLength)
    {
        var text = value ?? string.Empty;
        return text.Length <= maxLength ? text : text[..maxLength];
    }
}

/// <summary>带入预填响应：来源比价行信息 + 未落库的采购订单草稿（前端据此打开采购订单新增表单）</summary>
public sealed class PurchaseOrderPrefillResult
{
    /// <summary>来源单据类型（PurchaseQuote）</summary>
    public string SourceType { get; set; } = string.Empty;

    /// <summary>来源比价行 Id</summary>
    public long SourceId { get; set; }

    /// <summary>来源比价批次号</summary>
    public string SourceNo { get; set; } = string.Empty;

    /// <summary>带入后的采购订单草稿（未落库、无单号）</summary>
    public PurchaseOrder Order { get; set; } = new();
}

/// <summary>直接生成响应：新建采购订单的 Id / 单号 + 来源比价批次号</summary>
public sealed class PurchaseOrderConversionResult
{
    /// <summary>采购订单 Id</summary>
    public long Id { get; set; }

    /// <summary>采购单号（单据字轨生成）</summary>
    public string OrderNo { get; set; } = string.Empty;

    /// <summary>来源比价批次号</summary>
    public string SourceNo { get; set; } = string.Empty;
}

/// <summary>
/// 批次一次性预取索引（ERP-027，避免按行 N+1 查询：批次内固定 2 次查询）：
/// ① 批次行 <c>RefOrderNo</c> 指向的关联销售订单（转换前语义）；
/// ② 该批次既有来源采购订单（采购单号回写 + 备注来源标记两种重复生成判据）。
/// 判定口径与单行路径（<see cref="PurchaseQuoteConversion.FindGeneratedOrderAsync(IErpDbContext, PurchaseQuote, CancellationToken)" />）
/// 完全一致，只是把按行查询换成一次性预取。
/// </summary>
public sealed class PurchaseQuoteBatchLookup
{
    private readonly Dictionary<string, PurchaseOrder> _ordersByNo = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<PurchaseOrder> _ordersByMarkerRemark = new();
    private readonly Dictionary<string, SalesOrder> _salesOrdersByNo = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>按批次行一次性装载索引（只读；仅未删除单据参与判定）</summary>
    public static async Task<PurchaseQuoteBatchLookup> LoadAsync(IErpDbContext db, IReadOnlyList<PurchaseQuote> lines,
        CancellationToken ct = default)
    {
        var lookup = new PurchaseQuoteBatchLookup();
        if (lines.Count == 0) return lookup;

        var refNos = lines.Select(l => (l.RefOrderNo ?? string.Empty).Trim())
            .Where(no => no.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // ① 关联销售订单（RefOrderNo 转换前的语义：代理采购为哪张销售订单备货）
        if (refNos.Count > 0)
        {
            var salesOrders = await db.SalesOrders.AsNoTracking()
                .Where(o => !o.IsDeleted && refNos.Contains(o.OrderNo))
                .ToListAsync(ct);
            foreach (var salesOrder in salesOrders) lookup._salesOrdersByNo[salesOrder.OrderNo] = salesOrder;
        }

        // ② 既有来源采购订单：按号（回写判据）或按备注来源标记（同一批次共用标记前缀，一次查回后逐行匹配）
        var prefix = PurchaseQuoteConversion.SourceMarkerPrefix(lines[0].QuoteNo);
        var orders = await db.PurchaseOrders.AsNoTracking()
            .Where(o => !o.IsDeleted && (refNos.Contains(o.OrderNo) || o.Remark.Contains(prefix)))
            .ToListAsync(ct);
        foreach (var order in orders)
        {
            lookup._ordersByNo[order.OrderNo] = order;
            if (order.Remark.Contains(prefix, StringComparison.Ordinal)) lookup._ordersByMarkerRemark.Add(order);
        }
        return lookup;
    }

    /// <summary>该比价行是否已生成过采购订单（与单行路径同一判定顺序：先 RefOrderNo 链接，再备注来源标记）</summary>
    public PurchaseOrder? FindGeneratedOrder(PurchaseQuote quote)
    {
        var refNo = (quote.RefOrderNo ?? string.Empty).Trim();
        if (refNo.Length > 0 && _ordersByNo.TryGetValue(refNo, out var byRef)) return byRef;

        var marker = PurchaseQuoteConversion.SourceMarker(quote);
        return _ordersByMarkerRemark.FirstOrDefault(o => o.Remark.Contains(marker, StringComparison.Ordinal));
    }

    /// <summary>按单号取关联销售订单（未预取到 = 不存在或已删除，与单行路径匹配结果一致）</summary>
    public SalesOrder? FindSalesOrder(string orderNo)
        => (orderNo ?? string.Empty).Trim() is { Length: > 0 } no && _salesOrdersByNo.TryGetValue(no, out var order)
            ? order
            : null;
}

/// <summary>批次转换中被显式跳过的比价行（不合格，附原因；不生成采购订单也不改来源状态）</summary>
public sealed class PurchaseQuoteBatchSkip
{
    /// <summary>比价行 Id</summary>
    public long LineId { get; set; }

    /// <summary>比价批次号</summary>
    public string QuoteNo { get; set; } = string.Empty;

    /// <summary>商品名称</summary>
    public string ProductName { get; set; } = string.Empty;

    /// <summary>跳过原因（与单行守卫的错误文案一致）</summary>
    public string Reason { get; set; } = string.Empty;
}

/// <summary>批次转换计划中的一组：将合并为一张采购订单的兼容行集合（未落库草稿）</summary>
public sealed class PurchaseQuoteBatchGroupPlan
{
    public long SupplierId { get; set; }
    public string SupplierName { get; set; } = string.Empty;
    public string Currency { get; set; } = string.Empty;
    public long? OwningCustomerId { get; set; }
    public string OwningCustomerName { get; set; } = string.Empty;
    public string OwningSalesOrderNo { get; set; } = string.Empty;
    public string PaymentTerms { get; set; } = string.Empty;
    public bool TaxIncluded { get; set; }

    /// <summary>组内最晚交期（一单覆盖组内所有行）</summary>
    public DateTime? DeliveryDate { get; set; }

    /// <summary>组内来源比价行 Id（升序，与草稿明细一一对应）</summary>
    public List<long> LineIds { get; set; } = new();

    /// <summary>组内来源行数</summary>
    public int LineCount { get; set; }

    /// <summary>组内金额合计（服务端按采购订单口径重算，不采信来源报价总额）</summary>
    public decimal TotalAmount { get; set; }

    /// <summary>合并后的采购订单草稿（未落库、无单号）</summary>
    public PurchaseOrder Order { get; set; } = new();
}

/// <summary>批次转换计划（只读）：将生成几张采购订单、每张含哪些来源行、合计多少、哪些行会被跳过</summary>
public sealed class PurchaseQuoteBatchPlan
{
    public string SourceType { get; set; } = string.Empty;
    public string SourceNo { get; set; } = string.Empty;

    /// <summary>批次内比价行数（含不合格行）</summary>
    public int BatchLineCount { get; set; }

    /// <summary>可转换行数（= 各组行数合计）</summary>
    public int EligibleLineCount { get; set; }

    /// <summary>将生成的采购订单张数</summary>
    public int GroupCount { get; set; }

    /// <summary>全部合格行金额合计（服务端重算）</summary>
    public decimal TotalAmount { get; set; }

    public List<PurchaseQuoteBatchGroupPlan> Groups { get; set; } = new();
    public List<PurchaseQuoteBatchSkip> Skipped { get; set; } = new();
}

/// <summary>批次转换结果中的一张采购订单</summary>
public sealed class PurchaseQuoteBatchOrderResult
{
    public long Id { get; set; }
    public string OrderNo { get; set; } = string.Empty;
    public long SupplierId { get; set; }
    public string SupplierName { get; set; } = string.Empty;
    public string Currency { get; set; } = string.Empty;

    /// <summary>本单合并的来源比价行 Id（升序）</summary>
    public List<long> LineIds { get; set; } = new();

    public int LineCount { get; set; }

    /// <summary>本单金额（服务端重算）</summary>
    public decimal TotalAmount { get; set; }
}

/// <summary>批次转换响应：生成的采购订单清单 + 被跳过的来源行（显式拒绝，不静默丢弃）</summary>
public sealed class PurchaseQuoteBatchConversionResult
{
    public string SourceType { get; set; } = string.Empty;
    public string SourceNo { get; set; } = string.Empty;

    /// <summary>生成的采购订单张数</summary>
    public int OrderCount { get; set; }

    /// <summary>已转换来源行数</summary>
    public int ConvertedLineCount { get; set; }

    /// <summary>生成金额合计（服务端重算）</summary>
    public decimal TotalAmount { get; set; }

    public List<PurchaseQuoteBatchOrderResult> Orders { get; set; } = new();
    public List<PurchaseQuoteBatchSkip> Skipped { get; set; } = new();
}

/// <summary>
/// 批次转换请求：`QuoteNo` 与 `LineId` 至少给一个（批次号优先，`LineId` 用于按某行反查所属批次）；
/// `LineIds` 可选，用于只转换批次内的指定行（不属于该批次时抛 InvalidParameter）。
/// </summary>
public sealed class PurchaseQuoteBatchConversionRequest
{
    /// <summary>比价批次号（同一需求的各家报价共用）</summary>
    public string? QuoteNo { get; set; }

    /// <summary>比价行 Id（不给批次号时按其所属批次转换）</summary>
    public long? LineId { get; set; }

    /// <summary>可选：只转换这些比价行（必须属于同一批次）</summary>
    public List<long>? LineIds { get; set; }
}
