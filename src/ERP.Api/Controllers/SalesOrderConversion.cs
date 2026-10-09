using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ERP.Api.Controllers;

/// <summary>
/// 报价单 / 形式发票 PI → 销售订单「带入预填 / 直接生成」共用逻辑（ERP-010）。
/// 业务链：询价单 → 报价单 Quotation → 形式发票 PI → 销售订单；报价单也可直接转销售订单。
/// </summary>
/// <remarks>
/// 设计口径：
/// 1) 只使用 ERP-008 已交付的 EF 销售订单路径与来源追溯字段（<c>SourceQuotationId/No</c>、<c>SourcePiId/No</c>），
///    不触碰旧版销售订单存储过程，也不新增任何数据库结构；
/// 2) 「带入预填」返回**未落库**的销售订单草稿（不占用单据号），由前端打开销售订单新增表单继续编辑后再保存
///    （保存走 <c>POST /api/sales-orders</c>，同一套服务端复核）；
/// 3) 「直接生成」在服务端一次落库，并以来源字段守卫重复生成，绝不更新 / 覆盖既有销售订单；
/// 4) 明细数量、单价、金额与合计、定金金额一律由服务端按销售订单口径复核
///    （复用 <see cref="SalesOrderController.Calculate" /> 与 <see cref="SalesOrderController.Validate" />）。
/// </remarks>
public static class SalesOrderConversion
{
    /// <summary>来源单据类型：报价单</summary>
    public const string QuotationSourceType = "Quotation";

    /// <summary>来源单据类型：形式发票 PI</summary>
    public const string ProformaInvoiceSourceType = "ProformaInvoice";

    /// <summary>默认定金比例（%）：客户资料未维护时按外贸惯例 30%，与「报价单转 PI」同一口径</summary>
    private const decimal DefaultDepositRatio = 30m;

    /// <summary>来源客户缺失权威归属 / 越界时转换的拒绝文案（与 <see cref="ProformaInvoiceAuthorizationRules"/> 同源）</summary>
    public const string SourceOutOfScopeText = ProformaInvoiceAuthorizationRules.SourceOutOfScopeText;

    /// <summary>目标客户缺失权威归属 / 越界时转换的拒绝文案（绝不因来源可见而授予目标权限）</summary>
    public const string TargetOutOfScopeText = ProformaInvoiceAuthorizationRules.TargetOutOfScopeText;

    /// <summary>
    /// 来源 → 目标锁序口径（ERP-401 与 ERP-399 / ERP-400 转换共用同一把来源行锁）：
    /// 普通表单保存显式链接来源时恒定按「报价单来源行锁 → PI 来源行锁 → 销售订单目标行锁」取得排它行锁，
    /// 与「PI → 销售订单」「报价单 → 销售订单」直接转换串行化在同一把来源行锁上，绝不反向获取下游锁。
    /// </summary>
    public const string SourceLockOrderText = SalesOrderSourceLineageRules.LockOrderText;

    /// <summary>
    /// 转换范围守卫（ERP-398）：PI → 销售订单在落库任何销售订单、消耗任何单据号<b>之前</b>，
    /// 独立复核<b>来源客户</b>与<b>目标客户</b>都在当前账号实时客户数据范围内 ——
    /// 来源 PI 可见绝不等于目标客户获得授权；受限账号缺失归属 / 越界一律 fail closed。
    /// <c>null</c> 范围（进程内调用）保持既有内部口径，绝不把空身份当作匿名或管理员。
    /// </summary>
    public static void EnsureConversionScopeAuthorized(SalespersonDataScope? scope, long? sourceCustomerId,
        long? targetCustomerId)
    {
        ProformaInvoiceAuthorizationRules.EnsureSourceCustomerInScope(scope, sourceCustomerId);
        ProformaInvoiceAuthorizationRules.EnsureTargetCustomerInScope(scope, targetCustomerId);
    }

    /// <summary>
    /// 转换结果与来源 PI 的权威重读不一致时的拒绝文案（ERP-399）。
    /// </summary>
    public const string DraftMismatchText =
        "转换结果与来源 PI 的权威重读不一致（来源留痕 / 币种 / 汇率 / 明细数量 / 合计 / 定金）：原子拒绝，" +
        "绝不写入与来源不符的销售订单";

    /// <summary>
    /// 转换结果的权威一致性复核（ERP-399）：落库的销售订单必须**完全**来自本次锁内权威重读的 PI ——
    /// 显式 <c>SourcePiId</c> / 来源 PI 号、币种、汇率、明细数量合计、合计金额与定金口径逐项一致；
    /// 任一不一致即判定为陈旧 / 撕裂证据并原子拒绝（绝不写入与来源不符的订单，也绝不改写来源 PI）。
    /// </summary>
    public static void EnsureDraftMatchesSource(ProformaInvoice pi, SalesOrder order)
    {
        ArgumentNullException.ThrowIfNull(pi);
        ArgumentNullException.ThrowIfNull(order);

        if (order.SourcePiId != pi.Id
            || !string.Equals(order.SourcePiNo ?? string.Empty, Clamp(pi.PiNo, 50), StringComparison.Ordinal)
            || order.Currency != pi.Currency)
            throw BusinessException.RuleConflict(DraftMismatchText);

        var expectedRate = pi.ExchangeRate > 0 ? pi.ExchangeRate : DefaultExchangeRate;
        if (order.ExchangeRate != expectedRate)
            throw BusinessException.RuleConflict(DraftMismatchText);

        var activeDetails = pi.Details.Where(d => !d.IsDeleted).ToList();
        if (order.Details.Sum(d => d.Quantity) != activeDetails.Sum(d => d.Quantity))
            throw BusinessException.RuleConflict(DraftMismatchText);

        var expectedAmount = activeDetails.Sum(d => d.Quantity * d.UnitPrice);
        if (order.TotalAmount != expectedAmount)
            throw BusinessException.RuleConflict(DraftMismatchText);

        var expectedDeposit = order.TotalAmount * order.DepositRatio / 100m;
        if (order.DepositAmount != expectedDeposit)
            throw BusinessException.RuleConflict(DraftMismatchText);
    }

    /// <summary>
    /// 报价单转换范围守卫（ERP-400）：报价单 → PI / 销售订单在落库任何目标单据、消耗任何单据号<b>之前</b>，
    /// 独立复核<b>来源报价单客户</b>与<b>目标客户</b>都在当前账号实时客户数据范围内 ——
    /// 来源报价单可见绝不等于目标客户获得授权；受限账号缺失归属 / 越界一律 fail closed。
    /// <c>null</c> 范围（进程内调用）保持既有内部口径，绝不把空身份当作匿名或管理员。
    /// </summary>
    public static void EnsureQuotationConversionScopeAuthorized(SalespersonDataScope? scope,
        long? sourceCustomerId, long? targetCustomerId)
    {
        QuotationAuthorizationRules.EnsureSourceCustomerInScope(scope, sourceCustomerId);
        QuotationAuthorizationRules.EnsureTargetCustomerInScope(scope, targetCustomerId);
    }

    /// <summary>
    /// 报价单转换结果与来源报价单的权威重读不一致时的拒绝文案（ERP-400）。
    /// </summary>
    public const string QuotationDraftMismatchText =
        "转换结果与来源报价单的权威重读不一致（来源留痕 / 币种 / 汇率 / 明细数量 / 合计 / 定金）：原子拒绝，"
        + "绝不写入与来源不符的销售订单";

    /// <summary>
    /// 报价单转换结果的权威一致性复核（ERP-400）：落库的销售订单必须**完全**来自本次锁内权威重读的报价单 ——
    /// 显式 <c>SourceQuotationId</c> / 来源报价单号、币种、汇率、明细数量合计、合计金额与定金口径逐项一致；
    /// 任一不一致即判定为陈旧 / 撕裂证据并原子拒绝（绝不写入与来源不符的订单，也绝不改写来源报价单）。
    /// <para>定金口径与销售订单权威算法（<see cref="SalesOrderAmountRules.Calculate"/>）一致：
    /// <c>定金金额 = 合计 × 定金比例 %</c>，不做额外取整。</para>
    /// </summary>
    public static void EnsureQuotationDraftMatchesSource(Quotation quotation, SalesOrder order)
    {
        ArgumentNullException.ThrowIfNull(quotation);
        ArgumentNullException.ThrowIfNull(order);

        if (order.SourceQuotationId != quotation.Id
            || !string.Equals(order.SourceQuotationNo ?? string.Empty, Clamp(quotation.QuotationNo, 50), StringComparison.Ordinal)
            || order.Currency != quotation.Currency)
            throw BusinessException.RuleConflict(QuotationDraftMismatchText);

        var expectedRate = quotation.ExchangeRate > 0 ? quotation.ExchangeRate : DefaultExchangeRate;
        if (order.ExchangeRate != expectedRate)
            throw BusinessException.RuleConflict(QuotationDraftMismatchText);

        var activeDetails = quotation.Details.Where(d => !d.IsDeleted).ToList();
        if (order.Details.Sum(d => d.Quantity) != activeDetails.Sum(d => d.Quantity))
            throw BusinessException.RuleConflict(QuotationDraftMismatchText);

        var expectedAmount = activeDetails.Sum(d => d.Quantity * d.UnitPrice);
        if (order.TotalAmount != expectedAmount)
            throw BusinessException.RuleConflict(QuotationDraftMismatchText);

        var expectedDeposit = order.TotalAmount * order.DepositRatio / 100m;
        if (order.DepositAmount != expectedDeposit)
            throw BusinessException.RuleConflict(QuotationDraftMismatchText);
    }

    /// <summary>汇率缺省值：来源单据汇率为 0（未维护）时按 1 处理，避免销售订单金额折算异常</summary>
    private const decimal DefaultExchangeRate = 1m;

    // ==================== 报价单 → 销售订单 ====================

    /// <summary>
    /// 按报价单构造销售订单草稿（未落库）：先执行状态与重复守卫，再按固定映射表复制主表与明细。
    /// </summary>
    /// <exception cref="BusinessException">
    /// 报价单已作废、未审核、无明细、已转 PI 或已生成销售订单时抛出（错误码 RuleConflict）；
    /// 带入数据非法（数量 / 单价 / 定金比例）时抛出（错误码 InvalidParameter）。
    /// </exception>
    public static async Task<SalesOrder> FromQuotationAsync(IErpDbContext db, Quotation quotation,
        CancellationToken ct = default)
    {
        if (quotation.Status == DocumentStatus.Cancelled)
            throw BusinessException.RuleConflict("已作废的报价单不能转销售订单");

        var existing = await FindBySourceQuotationAsync(db, quotation.Id, ct);
        if (existing is not null)
            throw BusinessException.RuleConflict($"该报价单已生成销售订单：{existing.OrderNo}，不能重复生成");

        var pi = await db.ProformaInvoices.AsNoTracking()
            .FirstOrDefaultAsync(o => o.QuotationId == quotation.Id && !o.IsDeleted, ct);
        if (pi is not null)
            throw BusinessException.RuleConflict($"该报价单已转为 PI（{pi.PiNo}），请从 PI 转销售订单");

        if (quotation.Status != DocumentStatus.Approved)
            throw BusinessException.RuleConflict("报价单未审核，请先审核后再转销售订单");
        if (!quotation.Details.Any(d => !d.IsDeleted))
            throw BusinessException.RuleConflict("报价单无商品明细，不能转销售订单");

        var customer = await LoadCustomerAsync(db, quotation.CustomerId, ct);
        var order = NewDraft(customer);
        order.CustomerId = quotation.CustomerId ?? 0;
        order.SalesmanId = quotation.SalesmanId;
        order.Currency = quotation.Currency;
        order.ExchangeRate = quotation.ExchangeRate > 0 ? quotation.ExchangeRate : DefaultExchangeRate;
        order.TradeTerms = Prefer(quotation.TradeTerms, customer?.TradeTerms, 50);
        order.DestinationPort = Prefer(quotation.PortOfDestination, customer?.DestinationPort, 100);
        order.PaymentTerms = Prefer(quotation.PaymentTerms, customer?.PaymentTerms, 200);
        order.Remark = MergeRemark(quotation.Remark, quotation.LeadTime);
        order.SourceQuotationId = quotation.Id;
        order.SourceQuotationNo = Clamp(quotation.QuotationNo, 50);
        order.Details = MapDetails(quotation.Details.Where(d => !d.IsDeleted));

        Revalidate(order);
        SalesOrderController.Calculate(order);
        SalesOrderController.Validate(order);
        return order;
    }
    // ==================== 形式发票 PI → 销售订单 ====================

    /// <summary>
    /// 按 PI 构造销售订单草稿（未落库）：收货人 / 通知人 / 唛头 / 运输条款 / 定金口径随 PI，
    /// 同时保留 PI 背后的来源报价单，形成「报价单 → PI → 销售订单」完整追溯链。
    /// </summary>
    public static async Task<SalesOrder> FromProformaInvoiceAsync(IErpDbContext db, ProformaInvoice pi,
        CancellationToken ct = default)
    {
        if (pi.Status == DocumentStatus.Cancelled)
            throw BusinessException.RuleConflict("已作废的 PI 不能转销售订单");

        var existing = await FindBySourcePiAsync(db, pi.Id, ct);
        if (existing is not null)
            throw BusinessException.RuleConflict($"该 PI 已生成销售订单：{existing.OrderNo}，不能重复生成");

        if (pi.Status != DocumentStatus.Approved)
            throw BusinessException.RuleConflict("PI 未审核，请先审核后再转销售订单");
        if (!pi.Details.Any(d => !d.IsDeleted))
            throw BusinessException.RuleConflict("PI 无商品明细，不能转销售订单");

        var customer = await LoadCustomerAsync(db, pi.CustomerId, ct);
        var order = NewDraft(customer);
        order.CustomerId = pi.CustomerId ?? 0;
        order.SalesmanId = pi.SalesmanId;
        order.Currency = pi.Currency;
        order.ExchangeRate = pi.ExchangeRate > 0 ? pi.ExchangeRate : DefaultExchangeRate;
        order.TradeTerms = Prefer(pi.TradeTerms, customer?.TradeTerms, 50);
        order.DestinationPort = Prefer(pi.PortOfDestination, customer?.DestinationPort, 100);
        order.PaymentTerms = Prefer(pi.PaymentTerms, customer?.PaymentTerms, 200);
        order.Consignee = Prefer(pi.Consignee, customer?.Consignee, 300);
        order.NotifyParty = Prefer(pi.NotifyParty, customer?.NotifyParty, 300);
        order.ShippingMarks = Prefer(pi.ShippingMarks, customer?.DefaultShippingMark, 500);
        order.ShippingMethod = Clamp(pi.ShippingTerms, 100);
        order.DepositRatio = ResolveDepositRatio(pi, customer);
        order.Remark = MergeRemark(pi.Remark, pi.LeadTime);
        order.SourcePiId = pi.Id;
        order.SourcePiNo = Clamp(pi.PiNo, 50);
        // PI 记录了自己由哪张报价单转来：一并留痕，追溯链不因经过 PI 而断掉
        order.SourceQuotationId = pi.QuotationId;
        order.SourceQuotationNo = Clamp(pi.QuotationNo, 50);
        order.Details = MapDetails(pi.Details.Where(d => !d.IsDeleted));

        Revalidate(order);
        SalesOrderController.Calculate(order);
        SalesOrderController.Validate(order);
        return order;
    }

    /// <summary>
    /// 唯一目标复核（ERP-401 与既有转换守卫同一口径）：按**持久化来源字段**查找除
    /// <paramref name="excludeOrderId"/>（修改本单时排除自身）之外、未删除的既有销售订单 ——
    /// 同一 PI 以 <c>SourcePiId</c> 唯一化，同一报价单以「<c>SourceQuotationId</c> 且未绑定 PI」唯一化
    /// （PI 来源订单会同时留痕报价单祖先，不能因此误判为重复报价单目标）。
    /// 只按显式 Id 判定，绝不按来源单号等自由文本推断。
    /// </summary>
    public static Task<SalesOrder?> FindOtherTargetAsync(IErpDbContext db, long? quotationId, long? piId,
        long? excludeOrderId, CancellationToken ct = default)
        => SalesOrderSourceLineageRules.FindOtherTargetAsync(db, quotationId, piId, excludeOrderId, ct);

    // ==================== 重复生成守卫（同一来源仅一张销售订单） ====================

    /// <summary>按来源报价单查找未删除的销售订单（已软删除的历史订单不阻断重新生成）</summary>
    public static Task<SalesOrder?> FindBySourceQuotationAsync(IErpDbContext db, long quotationId,
        CancellationToken ct = default)
        => db.SalesOrders.AsNoTracking()
            .FirstOrDefaultAsync(o => o.SourceQuotationId == quotationId && !o.IsDeleted, ct);

    /// <summary>按来源 PI 查找未删除的销售订单</summary>
    public static Task<SalesOrder?> FindBySourcePiAsync(IErpDbContext db, long piId,
        CancellationToken ct = default)
        => db.SalesOrders.AsNoTracking()
            .FirstOrDefaultAsync(o => o.SourcePiId == piId && !o.IsDeleted, ct);

    // ==================== 映射与复核 ====================

    /// <summary>销售订单草稿骨架（未落库；单号由调用方按字轨生成，预填接口不占用单号）</summary>
    private static SalesOrder NewDraft(BaseCustomer? customer)
    {
        var order = new SalesOrder
        {
            OrderNo = string.Empty,
            OrderDate = DateTime.Today,
            Status = DocumentStatus.Pending,
            Currency = Currency.USD,
            ExchangeRate = DefaultExchangeRate,
            CreatedAt = DateTime.Now
        };
        ApplyCustomerDefaults(order, customer);
        return order;
    }

    /// <summary>客户档案默认值：收货人 / 通知人 / 唛头 / 定金比例 / 业务性质 / 佣金比例（与「报价单转 PI」同一套客户默认值）</summary>
    private static void ApplyCustomerDefaults(SalesOrder order, BaseCustomer? customer)
    {
        order.DepositRatio = customer is { DepositRatio: > 0 } ? customer.DepositRatio : DefaultDepositRatio;
        if (customer is null) return;

        order.Consignee = Clamp(customer.Consignee, 300);
        order.NotifyParty = Clamp(customer.NotifyParty, 300);
        order.ShippingMarks = Clamp(customer.DefaultShippingMark, 500);
        order.BusinessNature = Clamp(customer.BusinessNature, 20);
        // 佣金比例只接受 0~100（销售订单佣金校验同一区间，超出范围按未维护处理）
        order.CommissionRatio = customer.CommissionRatio is >= 0m and <= 100m ? customer.CommissionRatio : 0m;
    }

    /// <summary>
    /// 定金比例优先级：PI 比例 → 定金金额反算 → 客户档案比例 → 30% 默认。
    /// （PI 允许手工覆盖定金金额，此时比例可能为 0，按金额反算才能保证销售订单定金口径与 PI 一致。）
    /// </summary>
    private static decimal ResolveDepositRatio(ProformaInvoice pi, BaseCustomer? customer)
    {
        if (pi.DepositRatio > 0) return pi.DepositRatio;
        if (pi.DepositAmount > 0 && pi.TotalAmount > 0)
            return Math.Round(pi.DepositAmount / pi.TotalAmount * 100m, 4);
        return customer is { DepositRatio: > 0 } ? customer.DepositRatio : DefaultDepositRatio;
    }

    /// <summary>报价单明细 → 销售订单明细（按行号排序；金额由服务端重算，不采信来源金额）</summary>
    private static List<SalesOrderDetail> MapDetails(IEnumerable<QuotationDetail> details)
        => details.OrderBy(d => d.SortNo).ThenBy(d => d.Id)
            .Select(d => NewDetail(d.ProductId ?? 0, d.ProductName, d.Spec, d.Unit, d.Quantity, d.UnitPrice, d.Remark))
            .ToList();

    /// <summary>PI 明细 → 销售订单明细（按行号排序；金额由服务端重算，不采信来源金额）</summary>
    private static List<SalesOrderDetail> MapDetails(IEnumerable<ProformaInvoiceDetail> details)
        => details.OrderBy(d => d.SortNo).ThenBy(d => d.Id)
            .Select(d => NewDetail(d.ProductId ?? 0, d.ProductName, d.Spec, d.Unit, d.Quantity, d.UnitPrice, d.Remark))
            .ToList();

    private static SalesOrderDetail NewDetail(long productId, string? productName, string? spec, string? unit,
        decimal quantity, decimal unitPrice, string? remark)
        => new()
        {
            ProductId = productId,
            ProductName = Clamp(productName, 200),
            Spec = Clamp(spec, 200),
            Unit = Clamp(unit, 20),
            Quantity = quantity,
            UnitPrice = unitPrice,
            Amount = Math.Round(quantity * unitPrice, 2),
            Remark = Clamp(remark, 500),
            CreatedAt = DateTime.Now
        };

    /// <summary>
    /// 服务端复核带入结果（ERP-422）：逐行金额按唯一权威口径重算，并复用
    /// <see cref="SalesOrderAmountRules.ValidateNewWrite"/> 校验「明细非空、数量 / 单价 / 币种 / 汇率 /
    /// 定金 / 佣金比例与金额精度」——与页面手工录入**完全同一套**服务端规则，绝不出现第二套校验。
    /// 合计与定金金额随后由 <see cref="SalesOrderController.Calculate"/> 计算。
    /// </summary>
    private static void Revalidate(SalesOrder order)
    {
        SalesOrderAmountRules.ApplyDetailAmounts(order);
        SalesOrderAmountRules.ValidateNewWrite(order);
    }

    /// <summary>备注 + 来源文本交期合并（销售订单没有交期文本列，超过 500 字符按列长截断）</summary>
    private static string MergeRemark(string? remark, string? leadTime)
    {
        var text = remark ?? string.Empty;
        var lead = (leadTime ?? string.Empty).Trim();
        if (lead.Length > 0) text = text.Length > 0 ? $"{text} ｜ 交期：{lead}" : $"交期：{lead}";
        return Clamp(text, 500);
    }

    /// <summary>优先取主值，主值为空白时回退默认值，最后按目标列长度截断</summary>
    private static string Prefer(string? primary, string? fallback, int maxLength)
        => Clamp(string.IsNullOrWhiteSpace(primary) ? fallback : primary, maxLength);

    /// <summary>按目标列长度截断（来源字段列长与本表不完全一致时避免超长写入失败）</summary>
    private static string Clamp(string? value, int maxLength)
    {
        var text = value ?? string.Empty;
        return text.Length <= maxLength ? text : text[..maxLength];
    }

    private static async Task<BaseCustomer?> LoadCustomerAsync(IErpDbContext db, long? customerId,
        CancellationToken ct)
        => customerId is null or <= 0
            ? null
            : await db.BaseCustomers.AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == customerId.Value && !c.IsDeleted, ct);
}

/// <summary>带入预填响应：来源单据信息 + 未落库的销售订单草稿（前端据此打开销售订单新增表单）</summary>
public sealed class SalesOrderPrefillResult
{
    /// <summary>来源单据类型（Quotation / ProformaInvoice）</summary>
    public string SourceType { get; set; } = string.Empty;

    /// <summary>来源单据 Id</summary>
    public long SourceId { get; set; }

    /// <summary>来源单据号（报价单号 / PI 号）</summary>
    public string SourceNo { get; set; } = string.Empty;

    /// <summary>带入后的销售订单草稿（未落库、无单号）</summary>
    public SalesOrder Order { get; set; } = new();
}

/// <summary>直接生成响应：新建销售订单的 Id / 单号 + 来源单据号</summary>
public sealed class SalesOrderConversionResult
{
    /// <summary>销售订单 Id</summary>
    public long Id { get; set; }

    /// <summary>销售订单号（单据字轨生成）</summary>
    public string OrderNo { get; set; } = string.Empty;

    /// <summary>来源单据号（报价单号 / PI 号）</summary>
    public string SourceNo { get; set; } = string.Empty;
}
