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
    {
        var generated = await FindGeneratedOrderAsync(db, quote, ct);
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

        var (salesOrderId, salesOrderNo) = await ResolveOwningSalesOrderAsync(db, quote, ct);
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
        PurchaseQuote quote, CancellationToken ct)
    {
        var refNo = (quote.RefOrderNo ?? string.Empty).Trim();
        if (refNo.Length == 0) return (null, string.Empty);

        var order = await db.SalesOrders.AsNoTracking()
            .FirstOrDefaultAsync(o => o.OrderNo == refNo && !o.IsDeleted, ct);
        return order is null ? (null, string.Empty) : (order.Id, order.OrderNo);
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
