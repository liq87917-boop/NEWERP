using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ERP.Application.Services;

/// <summary>
/// 采购订单取消护栏（ERP-345）：在既有取消路由上校验身份 / 菜单 / 客户数据范围与实时单据状态，
/// 并在取消前拒绝仍有「已审核且未冲销」的入库履约或「有效」供应商付款 / 发票引用证据的订单。
/// <para>本类只做<b>纯判定与有界只读查询</b>，不落库、不改单据、不冲销库存与财务、不开启事务；
/// 「取消状态变更 + 拒绝判定」的原子性与同单并发串行化由调用方（<c>PurchaseOrderController.Cancel</c>）
/// 在同一可串行化事务内对采购订单行加 UPDLOCK/HOLDLOCK 完成（与 ERP-342 同源口径）。</para>
/// <para>有效证据判定严格复用既有证据口径（ERP-050 / ERP-067）：只有「引用行有效（未作废 / 未删除）、
/// 付款单可用、发票仍为已登记、供应商 / 币种 / 快照自相一致」才视为有效证据；作废 / 冲销 / 删除的证据
/// 只有在既有权威工作流已显式标记其失效时才被忽略，绝不按单号 / 金额 / 字符串猜测链接，也绝不跨币种合计。</para>
/// </summary>
public static class PurchaseOrderCancellationRules
{
    /// <summary>取消所需既有菜单编码（复用采购订单模块菜单，与 <c>SeedData.Menus</c> 同源）</summary>
    public const string RequiredMenuCode = "purchase-order";

    /// <summary>取消所需菜单的中文文案（与既有菜单名一致）</summary>
    public const string RequiredMenuText = "采购订单";

    /// <summary>取消护栏口径文案（接口 / 文档同源）</summary>
    public const string RuleText =
        "取消采购订单前，先校验当前身份、采购订单（purchase-order）菜单授权与客户数据范围；" +
        "当存在「以本单为来源、未删除、已审核」的采购入库单，或存在有效的供应商付款引用证据（付款单 → 采购订单）与" +
        "「付款单 → 采购发票（发票关联本订单）」的有效付款引用证据时拒绝取消；" +
        "作废 / 冲销 / 删除的证据只有在既有权威工作流显式标记其失效后才被忽略，绝不按字符串或金额猜测链接、绝不跨币种合计；" +
        "取消本身不冲销库存或财务，冲销只走既有冲销服务。";

    /// <summary>
    /// 校验采购订单能否取消（不写库）。调用方必须在同一可串行化事务内持有该订单行更新锁后再调用，
    /// 以保证「判定」与「状态变更」原子，且与同单入库审核串行化。
    /// </summary>
    /// <param name="db">数据上下文（只读查询）。</param>
    /// <param name="order">已加载且未删除的采购订单（含明细），不能为 null。</param>
    /// <param name="userId">当前登录用户 Id；缺失或非正整数按未认证拒绝。</param>
    /// <param name="ct">取消令牌。</param>
    public static async Task ValidateCancellationAsync(
        IErpDbContext db, PurchaseOrder order, long? userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(order);

        await EnsureAuthorizedAsync(db, order, userId, ct);
        EnsureCancellableState(order);
        await EnsureNoActiveFulfillmentAsync(db, order, ct);
    }

    // ==================== 授权（fail closed） ====================

    /// <summary>
    /// 身份 / 账号状态 / 菜单 / 客户数据范围校验（ERP-371 起统一收敛到
    /// <see cref="PurchaseOrderAuthorizationRules.EnsureOrderAuthorizedAsync"/>）：菜单授权每次请求重新解析，
    /// 范围同时覆盖显式归属客户与权威归属销售订单客户，任一缺失即拒绝，绝不猜测身份或范围。
    /// </summary>
    private static async Task EnsureAuthorizedAsync(
        IErpDbContext db, PurchaseOrder order, long? userId, CancellationToken ct)
        => await PurchaseOrderAuthorizationRules.EnsureOrderAuthorizedAsync(db, userId, order, ct);

    // ==================== 实时单据状态 ====================

    /// <summary>实时单据状态：已取消拒绝重复取消；终止态（驳回 / 完成）不允许取消。</summary>
    private static void EnsureCancellableState(PurchaseOrder order)
    {
        if (order.Status == DocumentStatus.Cancelled)
            throw BusinessException.RuleConflict("采购订单已取消，不能重复取消");
        if (order.Status is not (DocumentStatus.Pending or DocumentStatus.Submitted or DocumentStatus.Approved))
            throw BusinessException.RuleConflict("当前状态不允许取消");
    }

    // ==================== 有效履约证据护栏 ====================

    /// <summary>
    /// 拒绝仍有「已审核且未冲销」的入库履约或有效供应商付款 / 发票引用证据的订单。
    /// 判定只做有界只读查询，不合计金额、不猜测链接。
    /// </summary>
    private static async Task EnsureNoActiveFulfillmentAsync(
        IErpDbContext db, PurchaseOrder order, CancellationToken ct)
    {
        // 1) 已审核且未冲销（未取消）的采购入库单：存在即拒绝
        var hasApprovedReceipt = await db.StockIns.AsNoTracking()
            .AnyAsync(s => !s.IsDeleted && s.PurchaseOrderId == order.Id
                           && s.Status == DocumentStatus.Approved, ct);
        if (hasApprovedReceipt)
            throw BusinessException.RuleConflict("存在已审核且未冲销的采购入库单：请先取消 / 冲销入库单，再取消采购订单");

        // 2) 有效「付款单 → 采购订单」引用证据
        await EnsureNoEffectivePaymentAllocationAsync(db, order, ct);

        // 3) 有效「付款单 → 采购发票（发票关联本订单）」引用证据
        await EnsureNoEffectiveInvoicePaymentAllocationAsync(db, order, ct);
    }

    private static async Task EnsureNoEffectivePaymentAllocationAsync(
        IErpDbContext db, PurchaseOrder order, CancellationToken ct)
    {
        var rows = await db.SupplierPaymentAllocations.AsNoTracking()
            .Where(a => !a.IsDeleted && a.PurchaseOrderId == order.Id
                        && a.Status == SupplierPaymentAllocationRules.StatusActive)
            .ToListAsync(ct);
        if (rows.Count == 0) return;

        var paymentIds = rows.Select(r => r.PaymentId).Distinct().ToList();
        var payments = paymentIds.Count == 0
            ? new Dictionary<long, FinancePayment>()
            : (await db.FinancePayments.AsNoTracking().Where(p => paymentIds.Contains(p.Id)).ToListAsync(ct))
                .ToDictionary(p => p.Id);

        foreach (var row in rows)
        {
            payments.TryGetValue(row.PaymentId, out var payment);
            if (IsEffectivePaymentAllocation(row, payment, order))
                throw BusinessException.RuleConflict("存在有效的供应商付款引用证据（付款单 → 采购订单），不能取消采购订单");
        }
    }

    private static async Task EnsureNoEffectiveInvoicePaymentAllocationAsync(
        IErpDbContext db, PurchaseOrder order, CancellationToken ct)
    {
        var invoiceIds = await db.PurchaseInvoiceAllocations.AsNoTracking()
            .Where(a => !a.IsDeleted && a.PurchaseOrderId == order.Id)
            .Select(a => a.PurchaseInvoiceId)
            .Distinct()
            .ToListAsync(ct);
        if (invoiceIds.Count == 0) return;

        var rows = await db.SupplierPaymentInvoiceAllocations.AsNoTracking()
            .Where(a => !a.IsDeleted && a.Status == SupplierPaymentInvoiceAllocationRules.StatusActive
                        && invoiceIds.Contains(a.PurchaseInvoiceId))
            .ToListAsync(ct);
        if (rows.Count == 0) return;

        var paymentIds = rows.Select(r => r.PaymentId).Distinct().ToList();
        var linkedInvoiceIds = rows.Select(r => r.PurchaseInvoiceId).Distinct().ToList();
        var payments = paymentIds.Count == 0
            ? new Dictionary<long, FinancePayment>()
            : (await db.FinancePayments.AsNoTracking().Where(p => paymentIds.Contains(p.Id)).ToListAsync(ct))
                .ToDictionary(p => p.Id);
        var invoices = linkedInvoiceIds.Count == 0
            ? new Dictionary<long, PurchaseInvoice>()
            : (await db.PurchaseInvoices.AsNoTracking().Where(i => linkedInvoiceIds.Contains(i.Id)).ToListAsync(ct))
                .ToDictionary(i => i.Id);

        foreach (var row in rows)
        {
            payments.TryGetValue(row.PaymentId, out var payment);
            invoices.TryGetValue(row.PurchaseInvoiceId, out var invoice);
            if (IsEffectiveInvoicePaymentAllocation(row, payment, invoice))
                throw BusinessException.RuleConflict(
                    "存在有效的供应商付款引用证据（付款单 → 采购发票，且发票关联本采购订单），不能取消采购订单");
        }
    }


    // ==================== 有效证据纯判定（与 ERP-050 / ERP-067 同源口径） ====================

    /// <summary>
    /// 「付款单 → 采购订单」引用行是否有效：引用行有效、付款单可用、快照（供应商 / 币种 / 订单币种）自相一致，
    /// 且订单供应商 / 币种与付款单一致。不换算、不合并、不改派。
    /// </summary>
    private static bool IsEffectivePaymentAllocation(
        SupplierPaymentAllocation row, FinancePayment? payment, PurchaseOrder order)
    {
        if (payment is null || payment.IsDeleted) return false;
        if (row.AllocatedAmount <= 0) return false;
        if (row.SupplierId != payment.SupplierId) return false;

        var rowCurrency = CurrencyAmountRules.NormalizeCurrency(row.Currency);
        var paymentCurrency = CurrencyAmountRules.NormalizeCurrency(payment.Currency.ToString());
        if (!string.Equals(rowCurrency, paymentCurrency, StringComparison.Ordinal)) return false;

        var orderCurrencySnapshot = CurrencyAmountRules.NormalizeCurrency(row.OrderCurrency);
        if (!string.Equals(orderCurrencySnapshot, rowCurrency, StringComparison.Ordinal)) return false;

        if (order.SupplierId != payment.SupplierId) return false;
        var orderCurrency = CurrencyAmountRules.NormalizeCurrency(order.Currency.ToString());
        if (!string.Equals(orderCurrency, paymentCurrency, StringComparison.Ordinal)) return false;

        return true;
    }

    /// <summary>
    /// 「付款单 → 采购发票」引用行是否有效：引用行有效、付款单可用、发票仍为已登记，
    /// 金额不超两侧快照上限，且供应商 / 币种 / 快照自相一致。不换算、不合并、不改派。
    /// </summary>
    private static bool IsEffectiveInvoicePaymentAllocation(
        SupplierPaymentInvoiceAllocation row, FinancePayment? payment, PurchaseInvoice? invoice)
    {
        if (payment is null || payment.IsDeleted) return false;
        if (invoice is null || invoice.IsDeleted) return false;
        if (invoice.Status is PurchaseInvoiceRules.StatusDraft or PurchaseInvoiceRules.StatusVoided) return false;

        if (row.AllocatedAmount <= 0) return false;
        if (row.AllocatedAmount > row.PaymentAmount) return false;
        if (row.AllocatedAmount > row.InvoiceGrossAmount) return false;

        var currency = CurrencyAmountRules.NormalizeCurrency(row.Currency);
        var paymentCurrency = CurrencyAmountRules.NormalizeCurrency(payment.Currency.ToString());
        if (!string.Equals(currency, paymentCurrency, StringComparison.Ordinal)) return false;
        var invoiceCurrency = CurrencyAmountRules.NormalizeCurrency(invoice.Currency);
        if (!string.Equals(currency, invoiceCurrency, StringComparison.Ordinal)) return false;

        if (row.SupplierId != payment.SupplierId) return false;
        if (row.SupplierId != invoice.SupplierId) return false;

        return true;
    }
}

