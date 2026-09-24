using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ERP.Api.Controllers;

/// <summary>
/// 一条财务记录引用（ERP-028 只读派生）：单号 / 日期 / 金额 / 状态 + 建立引用所用的既有字段 + 是否计入合计。
/// </summary>
public sealed class OrderFinanceRecord
{
    /// <summary>来源实体（FinanceDepositApply / FinancePaymentApply / FinancePayment / FinanceExpense / FinanceComplaint / FinanceReceipt / FinanceContainerSettlement / FinanceBulkSettlement）</summary>
    public string Source { get; init; } = string.Empty;

    /// <summary>单据号（申请单号 / 付款单号 / 费用单号 / 客诉单号 / 收款单号 / 结算单号）</summary>
    public string DocumentNo { get; init; } = string.Empty;

    /// <summary>单据日期（申请 / 付款 / 费用 / 客诉 / 收款 / 结算日期）</summary>
    public DateTime DocumentDate { get; init; }

    /// <summary>单据金额（原币）；无金额字段的记录（如客诉单）为 null —— 未知，不等于 0</summary>
    public decimal? Amount { get; init; }

    /// <summary>币种（原币）；单据无币种列时为空串（金额不做任何换算）</summary>
    public string Currency { get; init; } = string.Empty;

    /// <summary>单据状态（枚举名，或费用单的付款状态文本）</summary>
    public string Status { get; init; } = string.Empty;

    /// <summary>该记录建立引用所用的既有字段</summary>
    public string ReferenceField { get; init; } = string.Empty;

    /// <summary>是否计入本单合计（= 权威引用 + 单据已审核 + 币种与本单一致）</summary>
    public bool Counted { get; init; }

    /// <summary>说明（不计入原因；权威 / 声明 / 客户级引用性质）</summary>
    public string Note { get; init; } = string.Empty;
}

/// <summary>财务记录分组：同一类来源 + 同一引用依据 + 同一引用状态（只读派生）</summary>
public sealed class OrderFinanceSection
{
    /// <summary>分组角色：deposit_apply / payment_apply / supplier_payment / settlement / sales_order_apply / expense / complaint / receipt / container_settlement / bulk_settlement</summary>
    public string Role { get; init; } = string.Empty;

    /// <summary>资金方向：in（收款）/ out（付款 / 成本）/ none（无金额方向）</summary>
    public string Direction { get; init; } = OrderFinanceReconciliation.DirectionNone;

    /// <summary>引用状态：linked（权威引用）/ declared（既有归属字段声明，非权威）/ unattributed（只记录客户或供应商等，无法归属本单）/ ambiguous（引用不唯一）/ unavailable（无可用引用）</summary>
    public string LinkStatus { get; init; } = OrderFinanceReconciliation.LinkUnavailable;

    /// <summary>该分组使用的既有引用字段</summary>
    public string ReferenceField { get; init; } = string.Empty;

    /// <summary>引用状态说明（缺什么、为什么不推断；与派生逻辑同源）</summary>
    public string LinkReason { get; init; } = string.Empty;

    /// <summary>计入合计（权威引用 + 已审核 + 币种与本单一致）；null = 未知（无可用引用），不等于 0</summary>
    public decimal? CountedAmount { get; init; }

    /// <summary>已提交 / 待提交（未审核、同币种）金额，单列不计入；null = 未知</summary>
    public decimal? SubmittedAmount { get; init; }

    /// <summary>仅列出、不计入的金额（只汇总与本单同币种的记录；他币种与无币种列记录不汇总、不做汇率换算）</summary>
    public decimal ListedAmount { get; init; }

    /// <summary>仅列出、不计入的记录数</summary>
    public int ListedRecordCount { get; init; }

    /// <summary>仅列出、未参与同币种汇总的记录数（他币种或无币种列）</summary>
    public int UnsummedRecordCount { get; init; }

    /// <summary>分组内的引用记录（各自带 counted 与说明）</summary>
    public List<OrderFinanceRecord> Records { get; init; } = new();
}

/// <summary>订单财务核对视图（ERP-028，只读派生；不落库、不改单据状态、不新增或修改任何表列）</summary>
public sealed class OrderFinanceReconciliationView
{
    public long OrderId { get; init; }
    public string OrderNo { get; init; } = string.Empty;

    /// <summary>订单类型：sales（销售订单，金额为收款方向）/ purchase（采购订单，金额为付款方向）</summary>
    public string OrderType { get; init; } = string.Empty;

    public string Status { get; init; } = string.Empty;
    public string Currency { get; init; } = string.Empty;

    /// <summary>订单金额（订单主表已落库金额）</summary>
    public decimal OrderAmount { get; init; }

    /// <summary>与本单金额同方向的权威计入合计（销售 = 收款方向；采购 = 付款方向）；null = 未知</summary>
    public decimal? LinkedAmount { get; init; }

    /// <summary>未覆盖金额 = 订单金额 − 权威计入合计；null = 未知；负数表示超过订单金额（超收 / 超付）</summary>
    public decimal? UnlinkedAmount { get; init; }

    /// <summary>反方向的权威计入合计（销售订单的供应商付款）；采购订单不适用（恒为 null，见 amountNote）</summary>
    public decimal? CounterpartLinkedAmount { get; init; }

    /// <summary>同方向「已提交 / 待提交」（未审核）金额；null = 未知</summary>
    public decimal? SubmittedAmount { get; init; }

    /// <summary>金额状态：linked（同方向来源全部可按权威引用归属）/ partial（已归属部分确定，另有既有记录无法归属）/ unknown（无可用引用）</summary>
    public string AmountStatus { get; init; } = OrderFinanceReconciliation.AmountUnknown;

    /// <summary>金额口径说明（与 amountStatus 一一对应；列出无法归属的来源与未计入原因）</summary>
    public string AmountNote { get; init; } = string.Empty;

    /// <summary>财务记录分组（固定顺序，见 <see cref="OrderFinanceReconciliation"/>）</summary>
    public List<OrderFinanceSection> Sections { get; init; } = new();

    /// <summary>核对口径说明（与派生逻辑同源，供界面原样展示）</summary>
    public string Rule { get; init; } = OrderFinanceReconciliation.RuleText;
}


/// <summary>
/// 订单财务核对派生（ERP-028，只读）。
/// <para><b>只使用既有引用字段</b>把订单与既有财务记录关联：定金 / 货款申请单的 SalesOrderId，付款单经货款申请单的 PaymentApplyId，
/// 客诉单的 SalesOrderId，采购单的 OwningSalesOrderId 与 SupplierId。不按客户 / 供应商汇总，不按备注 / 事由 / 金额 / 日期文本匹配，不做任何汇率换算。</para>
/// <para><b>计入金额</b> = 权威引用 + 单据已审核 + 币种与本单一致；其余记录一律列出但 counted = false（未审核单列、他币种不汇总）。
/// 费用单只有既有归属字段（RefType = 订单 + RefNo = 本单号）声明，属声明引用、不视为权威链接、不计入；
/// 收款单 / 装柜结算单 / 散货结算单只记录客户，没有订单级引用，一律仅列出。</para>
/// <para><b>金额未知与 0 严格区分</b>：金额为 null 表示无可用既有引用（未知）；查询有界：固定查询次数 + 每分组固定上限，无 N+1。</para>
/// </summary>
public static class OrderFinanceReconciliation
{
    // ---------- 订单类型 ----------
    /// <summary>销售订单（订单金额为收款方向）</summary>
    public const string OrderTypeSales = "sales";

    /// <summary>采购订单（订单金额为付款方向）</summary>
    public const string OrderTypePurchase = "purchase";

    // ---------- 分组角色 ----------
    public const string RoleDepositApply = "deposit_apply";
    public const string RolePaymentApply = "payment_apply";
    public const string RoleSupplierPayment = "supplier_payment";
    public const string RoleSettlement = "settlement";
    public const string RoleSalesOrderApply = "sales_order_apply";
    public const string RoleExpense = "expense";
    public const string RoleComplaint = "complaint";
    public const string RoleReceipt = "receipt";
    public const string RoleContainerSettlement = "container_settlement";
    public const string RoleBulkSettlement = "bulk_settlement";

    // ---------- 资金方向 ----------
    /// <summary>收款方向</summary>
    public const string DirectionIn = "in";

    /// <summary>付款 / 成本方向</summary>
    public const string DirectionOut = "out";

    /// <summary>无金额方向</summary>
    public const string DirectionNone = "none";

    // ---------- 引用状态 ----------
    /// <summary>引用状态：既有权威引用，可直接归属到本单</summary>
    public const string LinkLinked = "linked";

    /// <summary>引用状态：只有既有归属字段声明（非权威链接），仅列出</summary>
    public const string LinkDeclared = "declared";

    /// <summary>引用状态：来源只记录客户 / 供应商等，无法归属到本单，仅列出</summary>
    public const string LinkUnattributed = "unattributed";

    /// <summary>引用状态：引用存在但无法唯一归属（不推断金额）</summary>
    public const string LinkAmbiguous = "ambiguous";

    /// <summary>引用状态：无可用既有引用（金额未知）</summary>
    public const string LinkUnavailable = "unavailable";

    // ---------- 金额状态 ----------
    /// <summary>金额状态：同方向来源全部可按权威引用归属</summary>
    public const string AmountLinked = "linked";

    /// <summary>金额状态：已归属部分确定，另有既有财务记录无法归属</summary>
    public const string AmountPartial = "partial";

    /// <summary>金额状态：无可用引用，金额未知</summary>
    public const string AmountUnknown = "unknown";

    /// <summary>单个分组的记录上限（避免大单据无界加载）</summary>
    public const int RecordLimit = 200;

    /// <summary>费用单归属类型中表示「订单」的取值（既有 UI 下拉项）</summary>
    public const string ExpenseOrderRefType = "订单";

    /// <summary>无币种列单据的币种占位（空串：金额按原值列出、不参与同币种汇总、不做汇率换算）</summary>
    private const string CurrencyUnspecified = "";

    // ---------- 既有引用字段文案（分组与记录同源，界面原样展示） ----------
    private const string ReferenceDepositApply = "FinanceDepositApply.SalesOrderId";
    private const string ReferencePaymentApply = "FinancePaymentApply.SalesOrderId";
    private const string ReferenceSalesPayment = "FinancePayment.PaymentApplyId → FinancePaymentApply.SalesOrderId";
    private const string ReferenceSettlement = "FinancePayment.PaymentApplyId → FinancePaymentApply.SalesOrderId（本单归属销售订单）";
    private const string ReferenceExpense = "FinanceExpense.RefType=订单 + RefNo=本单号（声明引用）";
    private const string ReferenceComplaint = "FinanceComplaint.SalesOrderId";
    private const string ReferenceReceipt = "FinanceReceipt.CustomerId";
    private const string ReferenceContainerSettlement = "FinanceContainerSettlement.CustomerId";
    private const string ReferenceBulkSettlement = "FinanceBulkSettlement.CustomerId";
    private const string ReferenceSupplierPayment = "FinancePayment.SupplierId";
    private const string ReferenceSalesOrderApply =
        "FinanceDepositApply.SalesOrderId / FinancePaymentApply.SalesOrderId（本单归属销售订单）";

    /// <summary>采购订单反方向口径说明（销售订单级收款申请不归属到采购单）</summary>
    private const string PurchaseCounterpartNote =
        "采购订单为付款方向：销售订单级的定金 / 货款申请单不归属到本采购单，反方向权威计入不适用（null），仅列出供人工核对。";

    /// <summary>核对口径说明（与派生逻辑同源，供界面与文档原样引用）</summary>
    public const string RuleText =
        "只读核对：只用既有引用字段把本单与既有财务记录关联——定金申请单 / 货款申请单的 SalesOrderId，付款单经货款申请单的 PaymentApplyId，" +
        "客诉单的 SalesOrderId，采购单的 OwningSalesOrderId 与 SupplierId。不按客户 / 供应商汇总、不按备注 / 事由 / 金额 / 日期文本匹配、不做汇率换算。" +
        "只有「权威引用 + 单据已审核 + 币种与本单一致」的记录计入金额（counted = true）；其余仅列出（counted = false）：未审核单列、他币种不汇总。" +
        "费用单只有归属字段（RefType = 订单 + RefNo = 本单号）声明，属声明引用、不视为权威链接；收款单 / 装柜结算单 / 散货结算单只记录客户，" +
        "没有订单级引用，一律仅列出。金额为 null 表示未知（无可用引用），不等于 0；未覆盖金额 = 订单金额 − 权威计入金额，负数表示超过订单金额（超收 / 超付）。";

    /// <summary>
    /// 销售订单财务核对：订单金额为收款方向。权威计入 = 定金申请单 + 货款申请单（同币种、已审核）；
    /// 付款单按「付款单 → 货款申请单 → 本单」链归属（付款 / 成本方向）；客诉单为权威引用但无金额字段；
    /// 费用单为声明引用；收款单 / 装柜结算单 / 散货结算单只记录客户，仅列出。固定 9 次有界查询。
    /// </summary>
    public static async Task<OrderFinanceReconciliationView> ForSalesOrderAsync(IErpDbContext db, long id)
    {
        var order = await db.SalesOrders.AsNoTracking()
            .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
            ?? throw BusinessException.NotFound("销售订单不存在");
        var orderCurrency = order.Currency.ToString();

        var deposits = await LoadDepositAppliesAsync(db, order.Id);
        var paymentApplies = await LoadPaymentAppliesAsync(db, order.Id);
        var payments = await LoadPaymentsByAppliesAsync(db, paymentApplies);
        var expenses = await LoadOrderExpensesAsync(db, order.OrderNo);
        var complaints = await LoadComplaintsAsync(db, order.Id);

        var sections = new List<OrderFinanceSection>
        {
            DepositApplySection(deposits, orderCurrency),
            PaymentApplySection(paymentApplies, orderCurrency),
            SalesPaymentsSection(paymentApplies, payments, orderCurrency),
            ExpenseSection(expenses, orderCurrency),
            ComplaintSection(complaints),
            await CustomerLevelSectionAsync(db, RoleReceipt, order.CustomerId, orderCurrency),
            await CustomerLevelSectionAsync(db, RoleContainerSettlement, order.CustomerId, orderCurrency),
            await CustomerLevelSectionAsync(db, RoleBulkSettlement, order.CustomerId, orderCurrency),
        };

        return BuildView(order.Id, order.OrderNo, OrderTypeSales, order.Status.ToString(), orderCurrency,
            order.TotalAmount, sections);
    }

    /// <summary>定金申请单分组（权威引用：SalesOrderId → 本单，收款方向）</summary>
    private static OrderFinanceSection DepositApplySection(List<DocRow> rows, string orderCurrency)
    {
        var records = rows.Select(r => DocumentRecord("FinanceDepositApply", ReferenceDepositApply, r.No, r.Date, r.Amount,
            r.Currency, r.Status, orderCurrency, "定金申请（归属对象 = 本订单）。")).ToList();
        return Section(RoleDepositApply, DirectionIn, LinkLinked, ReferenceDepositApply,
            "定金申请单以 SalesOrderId 指向本单（既有权威引用）：同币种且已审核的申请计入本单收款方向。", records, orderCurrency);
    }

    /// <summary>货款申请单分组（权威引用：SalesOrderId → 本单，收款方向）</summary>
    private static OrderFinanceSection PaymentApplySection(List<PaymentApplyRow> rows, string orderCurrency)
    {
        var records = rows.Select(r => DocumentRecord("FinancePaymentApply", ReferencePaymentApply, r.No, r.Date, r.Amount,
            r.Currency, r.Status, orderCurrency, "货款申请（归属对象 = 本订单）。")).ToList();
        return Section(RolePaymentApply, DirectionIn, LinkLinked, ReferencePaymentApply,
            "货款申请单以 SalesOrderId 指向本单（既有权威引用）：同币种且已审核的申请计入本单收款方向。", records, orderCurrency);
    }

    /// <summary>付款单分组（权威引用链：付款单 PaymentApplyId → 货款申请单 SalesOrderId = 本单；付款 / 成本方向）</summary>
    private static OrderFinanceSection SalesPaymentsSection(List<PaymentApplyRow> applies, List<PaymentRow> rows,
        string orderCurrency)
    {
        var applyNoById = applies.ToDictionary(a => a.Id, a => a.No);
        var records = rows.Select(r => DocumentRecord("FinancePayment", ReferenceSalesPayment, r.No, r.Date, r.Amount,
            r.Currency, r.Status, orderCurrency,
            $"经货款申请单 {BlankOr(applyNoById.TryGetValue(r.ApplyId, out var applyNo) ? applyNo : string.Empty, "（未知）")} 归属本单（付款 / 成本方向）。")).ToList();
        var reason = applies.Count == 0
            ? "本单暂无引用它的货款申请单：付款单只记录供应商（另有可空货款申请单），未建立指向本单的既有引用，因此没有可归属的付款单（不按供应商汇总）。"
            : "付款单只有经货款申请单引用本单时才归属到本单（付款 / 成本方向）；该分组不影响收款方向的订单金额核对。";
        return Section(RoleSupplierPayment, DirectionOut, LinkLinked, ReferenceSalesPayment, reason, records, orderCurrency);
    }

    /// <summary>费用单分组（只有既有归属字段声明，非权威引用 → 仅列出、不计入）</summary>
    private static OrderFinanceSection ExpenseSection(List<ExpenseRow> rows, string orderCurrency)
    {
        var records = rows.Select(r => DocRecord("FinanceExpense", ReferenceExpense, r.No, r.Date, r.Amount, r.Currency,
            r.PaymentStatus, false,
            $"费用单归属字段声明（RefType = {ExpenseOrderRefType} + RefNo = 本单号）：非权威引用，仅列出、不计入合计；付款状态 = {BlankOr(r.PaymentStatus, "（未登记）")}。")).ToList();
        return Section(RoleExpense, DirectionOut, LinkDeclared, ReferenceExpense,
            "费用单没有订单级外键，只有既有归属字段声明：按声明列出供人工核对，不视为权威链接、不计入金额。", records, orderCurrency);
    }

    /// <summary>客诉单分组（权威引用：SalesOrderId → 本单，但单据无金额字段 → 仅列出）</summary>
    private static OrderFinanceSection ComplaintSection(List<ComplaintRow> rows)
    {
        var records = rows.Select(r => DocRecord("FinanceComplaint", ReferenceComplaint, r.No, r.Date, null,
            CurrencyUnspecified, r.Status.ToString(), false,
            $"客诉单以 SalesOrderId 指向本单（权威引用），但单据无金额字段：金额未知（不等于 0），仅列出。客诉类型：{BlankOr(r.Type, "（未登记）")}")).ToList();
        // 客诉单无金额、无币种：币种比较基准传空串，避免把「无币种」误报为他币种
        return Section(RoleComplaint, DirectionNone, LinkLinked, ReferenceComplaint,
            "客诉单以 SalesOrderId 指向本单（权威引用）；客诉单无金额字段，不参与金额核对，仅列出单据与状态。",
            records, CurrencyUnspecified);
    }

    /// <summary>
    /// 采购订单财务核对：订单金额为付款方向。结算分组复用 ERP-026 的权威引用链
    /// （付款单经货款申请单指向本单归属销售订单，且归属唯一、供应商一致）；
    /// 权威链不可用时按供应商列出付款单（仅列出）；归属销售订单级的定金 / 货款申请单不归属到本单；
    /// 费用单为声明引用；客户级收款单 / 结算单仅列出。
    /// </summary>
    public static async Task<OrderFinanceReconciliationView> ForPurchaseOrderAsync(IErpDbContext db, long id)
    {
        var order = await db.PurchaseOrders.AsNoTracking()
            .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
            ?? throw BusinessException.NotFound("采购订单不存在");
        var orderCurrency = order.Currency.ToString();

        var settlement = await PurchaseOrderProgress.SettlementForOrderAsync(db, order);
        var sections = new List<OrderFinanceSection> { SettlementSection(order, settlement, orderCurrency) };

        // 权威链不可用时，供应商级付款单只列出（不按供应商汇总计入）
        if (settlement.LinkStatus != PurchaseOrderProgress.LinkLinked && order.SupplierId > 0)
            sections.Add(await SupplierPaymentSectionAsync(db, order, orderCurrency));

        sections.Add(await SalesOrderApplySectionAsync(db, order.OwningSalesOrderId ?? 0, orderCurrency));
        sections.Add(ExpenseSection(await LoadOrderExpensesAsync(db, order.OrderNo), orderCurrency));

        var owningCustomerId = order.OwningCustomerId ?? 0;
        sections.Add(await CustomerLevelSectionAsync(db, RoleReceipt, owningCustomerId, orderCurrency));
        sections.Add(await CustomerLevelSectionAsync(db, RoleContainerSettlement, owningCustomerId, orderCurrency));
        sections.Add(await CustomerLevelSectionAsync(db, RoleBulkSettlement, owningCustomerId, orderCurrency));

        return BuildView(order.Id, order.OrderNo, OrderTypePurchase, order.Status.ToString(), orderCurrency,
            order.TotalAmount, sections);
    }

    /// <summary>结算分组（复用 ERP-026 权威引用链；金额未知与 0 严格区分）</summary>
    private static OrderFinanceSection SettlementSection(PurchaseOrder order, PurchaseOrderSettlementProgress settlement,
        string orderCurrency)
    {
        var records = settlement.Documents.Select(d => DocRecord("FinancePayment", ReferenceSettlement, d.PaymentNo,
            d.PaymentDate, d.Amount, d.Currency, d.Status, d.Counted,
            d.Counted
                ? $"经货款申请单 {BlankOr(d.PaymentApplyNo, "（无）")} 归属本采购单（已审核且币种一致）。"
                : $"经货款申请单 {BlankOr(d.PaymentApplyNo, "（无）")} 归属本采购单，但{ExclusionText(d.Status, d.Currency, orderCurrency)}。")).ToList();
        var linkStatus = settlement.LinkStatus switch
        {
            PurchaseOrderProgress.LinkLinked => LinkLinked,
            PurchaseOrderProgress.LinkAmbiguous => LinkAmbiguous,
            _ => LinkUnavailable,
        };
        var reason = $"人工登记结算进度：{BlankOr(order.SettlementProgress, "（未登记）")}；{settlement.LinkReason}";
        return Section(RoleSettlement, DirectionOut, linkStatus, ReferenceSettlement, reason, records, orderCurrency,
            settlement.SettledAmount.HasValue);
    }

    /// <summary>供应商级付款单分组（权威链不可用时仅列出；1 次有界查询）</summary>
    private static async Task<OrderFinanceSection> SupplierPaymentSectionAsync(IErpDbContext db, PurchaseOrder order,
        string orderCurrency)
    {
        var rows = await db.FinancePayments.AsNoTracking()
            .Where(p => !p.IsDeleted && p.SupplierId == order.SupplierId)
            .OrderBy(p => p.PaymentDate).ThenBy(p => p.Id).Take(RecordLimit)
            .Select(p => new { p.PaymentNo, p.PaymentDate, p.Amount, p.Currency, p.Status })
            .ToListAsync();
        var records = rows.Select(p => DocRecord("FinancePayment", ReferenceSupplierPayment, p.PaymentNo, p.PaymentDate,
            p.Amount, p.Currency.ToString(), p.Status.ToString(), false,
            "付款单只记录供应商（另有可空货款申请单）：未通过货款申请单指向本单归属销售订单，无法唯一归属到本单，仅列出、不计入、不按供应商汇总。")).ToList();
        return Section(RoleSupplierPayment, DirectionOut, LinkUnattributed, ReferenceSupplierPayment,
            "权威结算引用链不可用：这里按供应商列出既有付款单供人工核对，不视为本单付款、不计入金额。", records, orderCurrency);
    }

    /// <summary>归属销售订单级的定金 / 货款申请单分组（引用对象是销售订单，不归属到本采购单；最多 2 次有界查询）</summary>
    private static async Task<OrderFinanceSection> SalesOrderApplySectionAsync(IErpDbContext db, long owningSalesOrderId,
        string orderCurrency)
    {
        if (owningSalesOrderId <= 0)
        {
            return Section(RoleSalesOrderApply, DirectionIn, LinkUnavailable, ReferenceSalesOrderApply,
                "本单未关联归属销售订单：定金 / 货款申请单只引用销售订单，无法定位范围（不按客户、供应商或文本猜测）。",
                new List<OrderFinanceRecord>(), orderCurrency, amountKnown: false);
        }

        var deposits = await LoadDepositAppliesAsync(db, owningSalesOrderId);
        var applies = await LoadPaymentAppliesAsync(db, owningSalesOrderId);
        var records = new List<OrderFinanceRecord>();
        records.AddRange(deposits.Select(r => DocRecord("FinanceDepositApply", ReferenceSalesOrderApply, r.No, r.Date,
            r.Amount, r.Currency, r.Status.ToString(), false,
            "定金申请单引用的是本单归属销售订单（不是本采购单）：仅列出、不计入本采购单金额。")));
        records.AddRange(applies.Select(r => DocRecord("FinancePaymentApply", ReferenceSalesOrderApply, r.No, r.Date,
            r.Amount, r.Currency, r.Status.ToString(), false,
            "货款申请单引用的是本单归属销售订单（不是本采购单）：仅列出、不计入本采购单金额。")));
        return Section(RoleSalesOrderApply, DirectionIn, LinkUnattributed, ReferenceSalesOrderApply,
            "定金 / 货款申请单的引用对象是销售订单：归属销售订单级别的记录只列出，不归属、不计入本采购单金额。",
            records, orderCurrency);
    }

    /// <summary>
    /// 客户级财务来源分组（收款单 / 装柜结算单 / 散货结算单）：只记录客户，无法归属到本单，一律仅列出。
    /// 客户未知时不查询（金额未知）。
    /// </summary>
    private static async Task<OrderFinanceSection> CustomerLevelSectionAsync(IErpDbContext db, string role, long customerId,
        string orderCurrency)
    {
        var referenceField = ReferenceOf(role);
        if (customerId <= 0)
        {
            return Section(role, DirectionIn, LinkUnavailable, referenceField,
                "本单未维护客户 / 归属客户：这类单据只记录客户，无法定位范围（不按金额、日期或文本猜测），金额未知。",
                new List<OrderFinanceRecord>(), orderCurrency, amountKnown: false);
        }

        var records = await CustomerLevelRecordsAsync(db, role, customerId, orderCurrency);
        return Section(role, DirectionIn, LinkUnattributed, referenceField,
            $"{RoleLabel(role)}只记录客户，没有订单级引用：无法归属到本单，仅列出、不计入（不按客户汇总、不按金额或日期匹配）。",
            records, orderCurrency);
    }

    /// <summary>客户级财务来源记录（按角色各 1 次有界查询）</summary>
    private static async Task<List<OrderFinanceRecord>> CustomerLevelRecordsAsync(IErpDbContext db, string role,
        long customerId, string orderCurrency)
    {
        var label = RoleLabel(role);
        switch (role)
        {
            case RoleReceipt:
            {
                var rows = await db.FinanceReceipts.AsNoTracking()
                    .Where(r => !r.IsDeleted && r.CustomerId == customerId)
                    .OrderBy(r => r.ReceiptDate).ThenBy(r => r.Id).Take(RecordLimit)
                    .Select(r => new { r.ReceiptNo, r.ReceiptDate, r.Amount, r.Currency, r.Status })
                    .ToListAsync();
                return rows.Select(r => DocRecord("FinanceReceipt", ReferenceReceipt, r.ReceiptNo, r.ReceiptDate,
                    r.Amount, r.Currency.ToString(), r.Status.ToString(), false,
                    CustomerLevelNote(label, r.Currency.ToString(), orderCurrency))).ToList();
            }

            case RoleContainerSettlement:
            {
                var rows = await db.FinanceContainerSettlements.AsNoTracking()
                    .Where(s => !s.IsDeleted && s.CustomerId == customerId)
                    .OrderBy(s => s.SettlementDate).ThenBy(s => s.Id).Take(RecordLimit)
                    .Select(s => new { s.SettlementNo, s.SettlementDate, s.TotalAmount, s.Status })
                    .ToListAsync();
                return rows.Select(s => DocRecord("FinanceContainerSettlement", ReferenceContainerSettlement,
                    s.SettlementNo, s.SettlementDate, s.TotalAmount, CurrencyUnspecified, s.Status.ToString(), false,
                    CustomerLevelNote(label, CurrencyUnspecified, orderCurrency))).ToList();
            }

            case RoleBulkSettlement:
            {
                var rows = await db.FinanceBulkSettlements.AsNoTracking()
                    .Where(s => !s.IsDeleted && s.CustomerId == customerId)
                    .OrderBy(s => s.SettlementDate).ThenBy(s => s.Id).Take(RecordLimit)
                    .Select(s => new { s.SettlementNo, s.SettlementDate, s.TotalAmount, s.Status })
                    .ToListAsync();
                return rows.Select(s => DocRecord("FinanceBulkSettlement", ReferenceBulkSettlement, s.SettlementNo,
                    s.SettlementDate, s.TotalAmount, CurrencyUnspecified, s.Status.ToString(), false,
                    CustomerLevelNote(label, CurrencyUnspecified, orderCurrency))).ToList();
            }

            default:
                return new List<OrderFinanceRecord>();
        }
    }

    /// <summary>定金申请单（按 SalesOrderId：权威引用；1 次有界查询）</summary>
    private static async Task<List<DocRow>> LoadDepositAppliesAsync(IErpDbContext db, long salesOrderId)
    {
        var rows = await db.FinanceDepositApplies.AsNoTracking()
            .Where(a => !a.IsDeleted && a.SalesOrderId == salesOrderId)
            .OrderBy(a => a.ApplyDate).ThenBy(a => a.Id).Take(RecordLimit)
            .Select(a => new { a.ApplyNo, a.ApplyDate, a.Amount, a.Currency, a.Status })
            .ToListAsync();
        return rows.Select(a => new DocRow(a.ApplyNo, a.ApplyDate, a.Amount, a.Currency.ToString(), a.Status)).ToList();
    }

    /// <summary>货款申请单（按 SalesOrderId：权威引用、付款单归属链起点；1 次有界查询）</summary>
    private static async Task<List<PaymentApplyRow>> LoadPaymentAppliesAsync(IErpDbContext db, long salesOrderId)
    {
        var rows = await db.FinancePaymentApplies.AsNoTracking()
            .Where(a => !a.IsDeleted && a.SalesOrderId == salesOrderId)
            .OrderBy(a => a.ApplyDate).ThenBy(a => a.Id).Take(RecordLimit)
            .Select(a => new { a.Id, a.ApplyNo, a.ApplyDate, a.Amount, a.Currency, a.Status })
            .ToListAsync();
        return rows.Select(a => new PaymentApplyRow(a.Id, a.ApplyNo, a.ApplyDate, a.Amount, a.Currency.ToString(),
            a.Status)).ToList();
    }

    /// <summary>付款单（经货款申请单引用；无货款申请单时不查询；1 次有界查询）</summary>
    private static async Task<List<PaymentRow>> LoadPaymentsByAppliesAsync(IErpDbContext db, List<PaymentApplyRow> applies)
    {
        if (applies.Count == 0) return new List<PaymentRow>();
        var applyIds = applies.Select(a => a.Id).ToList();
        var rows = await db.FinancePayments.AsNoTracking()
            .Where(p => !p.IsDeleted && p.PaymentApplyId != null && applyIds.Contains(p.PaymentApplyId.Value))
            .OrderBy(p => p.PaymentDate).ThenBy(p => p.Id).Take(RecordLimit)
            .Select(p => new { p.PaymentNo, p.PaymentDate, p.Amount, p.Currency, p.Status, p.PaymentApplyId })
            .ToListAsync();
        return rows.Select(p => new PaymentRow(p.PaymentNo, p.PaymentDate, p.Amount, p.Currency.ToString(), p.Status,
            p.PaymentApplyId ?? 0)).ToList();
    }

    /// <summary>费用单（只按既有归属字段声明：RefType = 订单 + RefNo = 本单号；1 次有界查询）</summary>
    private static async Task<List<ExpenseRow>> LoadOrderExpensesAsync(IErpDbContext db, string orderNo)
    {
        var rows = await db.FinanceExpenses.AsNoTracking()
            .Where(e => !e.IsDeleted && e.RefType == ExpenseOrderRefType && e.RefNo == orderNo)
            .OrderBy(e => e.ExpenseDate).ThenBy(e => e.Id).Take(RecordLimit)
            .Select(e => new { e.ExpenseNo, e.ExpenseDate, e.Amount, e.Currency, e.PaymentStatus })
            .ToListAsync();
        return rows.Select(e => new ExpenseRow(e.ExpenseNo, e.ExpenseDate, e.Amount, e.Currency, e.PaymentStatus))
            .ToList();
    }

    /// <summary>客诉单（按 SalesOrderId：权威引用但无金额字段；1 次有界查询）</summary>
    private static async Task<List<ComplaintRow>> LoadComplaintsAsync(IErpDbContext db, long salesOrderId)
    {
        var rows = await db.FinanceComplaints.AsNoTracking()
            .Where(c => !c.IsDeleted && c.SalesOrderId == salesOrderId)
            .OrderBy(c => c.ComplaintDate).ThenBy(c => c.Id).Take(RecordLimit)
            .Select(c => new { c.ComplaintNo, c.ComplaintDate, c.ComplaintType, c.Status })
            .ToListAsync();
        return rows.Select(c => new ComplaintRow(c.ComplaintNo, c.ComplaintDate, c.ComplaintType, c.Status)).ToList();
    }

    /// <summary>按分组规则汇总记录：计入 / 已提交未审核 / 仅列出（只对同币种记录求金额，他币种与无币种列不汇总、不换算）</summary>
    private static OrderFinanceSection Section(string role, string direction, string linkStatus, string referenceField,
        string linkReason, List<OrderFinanceRecord> records, string orderCurrency, bool amountKnown = true)
    {
        var counted = new List<OrderFinanceRecord>();
        var awaiting = new List<OrderFinanceRecord>();
        var listed = new List<OrderFinanceRecord>();
        foreach (var record in records)
        {
            if (record.Counted) counted.Add(record);
            else if (AwaitingAudit(record.Status) && SameCurrency(record.Currency, orderCurrency)) awaiting.Add(record);
            else listed.Add(record);
        }

        return new OrderFinanceSection
        {
            Role = role,
            Direction = direction,
            LinkStatus = linkStatus,
            ReferenceField = referenceField,
            LinkReason = linkReason,
            CountedAmount = amountKnown ? counted.Sum(r => r.Amount ?? 0m) : null,
            SubmittedAmount = amountKnown ? awaiting.Sum(r => r.Amount ?? 0m) : null,
            ListedAmount = listed.Where(r => SameCurrency(r.Currency, orderCurrency)).Sum(r => r.Amount ?? 0m),
            ListedRecordCount = listed.Count,
            UnsummedRecordCount = listed.Count(r => !SameCurrency(r.Currency, orderCurrency)),
            Records = records,
        };
    }

    /// <summary>构造一条引用记录（金额可为未知 null）</summary>
    private static OrderFinanceRecord DocRecord(string source, string referenceField, string documentNo, DateTime documentDate,
        decimal? amount, string currency, string status, bool counted, string note)
        => new()
        {
            Source = source,
            ReferenceField = referenceField,
            DocumentNo = documentNo,
            DocumentDate = documentDate,
            Amount = amount,
            Currency = currency,
            Status = status,
            Counted = counted,
            Note = note,
        };

    /// <summary>构造一条带「已审核 + 同币种」计入判定的引用记录（不计入时把原因并入说明）</summary>
    private static OrderFinanceRecord DocumentRecord(string source, string referenceField, string documentNo,
        DateTime documentDate, decimal amount, string currency, DocumentStatus status, string orderCurrency, string note)
    {
        var counted = status == DocumentStatus.Approved && SameCurrency(currency, orderCurrency);
        var reason = CountExclusionReason(status, currency, orderCurrency);
        return DocRecord(source, referenceField, documentNo, documentDate, amount, currency, status.ToString(), counted,
            counted || reason.Length == 0 ? note : note + reason);
    }

    /// <summary>不计入原因（未审核 / 他币种），全部不计入时为空串表示「计入」</summary>
    private static string CountExclusionReason(DocumentStatus status, string currency, string orderCurrency)
    {
        var reasons = new List<string>();
        if (status != DocumentStatus.Approved) reasons.Add("单据未审核（待提交 / 已提交 / 已驳回 / 已取消）：仅列出、不计入。");
        if (!SameCurrency(currency, orderCurrency)) reasons.Add("币种与本单不一致：仅列出、不做汇率换算。");
        return string.Join(string.Empty, reasons);
    }

    /// <summary>字符串口径的不计入原因（用于复用 ERP-026 结算引用的既有文本状态）</summary>
    private static string ExclusionText(string status, string currency, string orderCurrency)
    {
        var reasons = new List<string>();
        if (!string.Equals(status, nameof(DocumentStatus.Approved), StringComparison.Ordinal)) reasons.Add("单据未审核");
        if (!SameCurrency(currency, orderCurrency)) reasons.Add("币种与本单不一致");
        if (reasons.Count == 0) reasons.Add("未满足计入条件");
        return string.Join("、", reasons) + "（仅列出、不做汇率换算）";
    }

    /// <summary>是否「已提交 / 待提交」状态（枚举名口径；费用单的付款状态文本不在该词表内）</summary>
    private static bool AwaitingAudit(string status)
        => status == nameof(DocumentStatus.Pending) || status == nameof(DocumentStatus.Submitted);

    private static bool SameCurrency(string left, string right)
        => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    /// <summary>客户级引用记录的说明（只记录客户 + 币种是否可汇总）</summary>
    private static string CustomerLevelNote(string label, string currency, string orderCurrency)
        => SameCurrency(currency, orderCurrency)
            ? $"{label}只记录客户、未建立订单级引用：仅列出、不计入本单金额。"
            : $"{label}只记录客户、未建立订单级引用：仅列出、不计入本单金额；币种与本单不一致或单据无币种列，不参与同币种汇总。";

    private static string BlankOr(string value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value;

    /// <summary>分组角色对应的客户级既有引用字段（客户级分组专用）</summary>
    private static string ReferenceOf(string role) => role switch
    {
        RoleReceipt => ReferenceReceipt,
        RoleContainerSettlement => ReferenceContainerSettlement,
        RoleBulkSettlement => ReferenceBulkSettlement,
        _ => string.Empty,
    };

    /// <summary>分组角色中文名（用于口径说明；界面另有同源标签）</summary>
    private static string RoleLabel(string role) => role switch
    {
        RoleDepositApply => "定金申请单",
        RolePaymentApply => "货款申请单",
        RoleSupplierPayment => "付款单",
        RoleSettlement => "结算（付款单）",
        RoleSalesOrderApply => "归属销售订单的定金 / 货款申请单",
        RoleExpense => "费用单",
        RoleComplaint => "客诉单",
        RoleReceipt => "收款单",
        RoleContainerSettlement => "装柜结算单",
        RoleBulkSettlement => "散货结算单",
        _ => role,
    };

    /// <summary>引用状态中文名（用于口径说明）</summary>
    private static string LinkLabel(string linkStatus) => linkStatus switch
    {
        LinkLinked => "权威引用",
        LinkDeclared => "声明引用（非权威）",
        LinkUnattributed => "无法归属（仅列出）",
        LinkAmbiguous => "引用不唯一（不推断）",
        _ => "无可用引用（未知）",
    };

    /// <summary>某方向的权威计入合计；该方向任一分组金额未知时整体未知（null）</summary>
    private static decimal? DirectionTotal(IEnumerable<OrderFinanceSection> sections, string direction)
    {
        var own = sections.Where(s => s.Direction == direction).ToList();
        if (own.Count == 0) return 0m;
        return own.Any(s => s.CountedAmount is null) ? null : own.Sum(s => s.CountedAmount!.Value);
    }

    /// <summary>某方向的「已提交 / 待提交」合计；任一分组未知时整体未知（null）</summary>
    private static decimal? DirectionSubmitted(IEnumerable<OrderFinanceSection> sections, string direction)
    {
        var own = sections.Where(s => s.Direction == direction).ToList();
        if (own.Count == 0) return 0m;
        return own.Any(s => s.SubmittedAmount is null) ? null : own.Sum(s => s.SubmittedAmount!.Value);
    }

    /// <summary>
    /// 金额状态与口径说明：按本单金额方向的分组判定 linked / partial / unknown。
    /// 声明引用、无法归属、引用不唯一或无可用引用都不得被当作权威金额，因此 partial 明确说明「未覆盖 ≠ 未收付」。
    /// </summary>
    private static (string Status, string Note) AmountSummary(List<OrderFinanceSection> sections, string direction)
    {
        var ownSections = sections.Where(s => s.Direction == direction).ToList();
        var unresolved = ownSections
            .Where(s => s.LinkStatus is LinkDeclared or LinkUnattributed
                ? s.Records.Count > 0
                : s.LinkStatus is LinkUnavailable or LinkAmbiguous)
            .ToList();
        var listedInLinked = ownSections.Where(s => s.LinkStatus == LinkLinked).Sum(s => s.ListedRecordCount);

        var notes = unresolved
            .Select(s => $"{RoleLabel(s.Role)}（{LinkLabel(s.LinkStatus)}）：{s.LinkReason}")
            .ToList();
        if (listedInLinked > 0)
            notes.Add($"另有 {listedInLinked} 条同方向记录未计入（未审核或币种不一致，已在分组中列出、不做汇率换算）。");
        var suffix = notes.Count == 0 ? string.Empty : " " + string.Join(" ", notes);

        if (ownSections.Any(s => s.CountedAmount is null))
        {
            return (AmountUnknown,
                "订单金额方向没有可用权威引用，计入 / 未覆盖金额均为未知（null），不按客户、供应商、备注或金额接近度推断。" + suffix);
        }

        if (unresolved.Any(s => s.CountedAmount is not null))
        {
            return (AmountPartial,
                "已按权威引用计入的金额确定；下列来源存在既有财务记录但无法按权威引用归属到本单，" +
                "因此「未覆盖金额」只表示单据未覆盖部分，不等于未收付：" + suffix);
        }

        return (AmountLinked, "订单金额方向的既有财务来源均已按权威引用归属到本单。" + suffix);
    }

    /// <summary>组装视图（金额未知一律用 null 表达，绝不回落为 0）</summary>
    private static OrderFinanceReconciliationView BuildView(long orderId, string orderNo, string orderType, string status,
        string currency, decimal orderAmount, List<OrderFinanceSection> sections)
    {
        var direction = orderType == OrderTypeSales ? DirectionIn : DirectionOut;
        var linkedAmount = DirectionTotal(sections, direction);
        var (amountStatus, amountNote) = AmountSummary(sections, direction);
        // 销售订单的供应商付款属反方向；采购订单的金额方向是付款，销售订单级收款申请不适用（null）
        var counterpart = orderType == OrderTypeSales ? DirectionTotal(sections, DirectionOut) : null;
        return new OrderFinanceReconciliationView
        {
            OrderId = orderId,
            OrderNo = orderNo,
            OrderType = orderType,
            Status = status,
            Currency = currency,
            OrderAmount = orderAmount,
            LinkedAmount = linkedAmount,
            UnlinkedAmount = linkedAmount is null ? null : orderAmount - linkedAmount.Value,
            CounterpartLinkedAmount = counterpart,
            SubmittedAmount = DirectionSubmitted(sections, direction),
            AmountStatus = amountStatus,
            AmountNote = counterpart is null ? PurchaseCounterpartNote + amountNote : amountNote,
            Sections = sections,
        };
    }

    /// <summary>定金申请单行投影</summary>
    private sealed record DocRow(string No, DateTime Date, decimal Amount, string Currency, DocumentStatus Status);

    /// <summary>货款申请单行投影（Id 用于付款单归属链）</summary>
    private sealed record PaymentApplyRow(long Id, string No, DateTime Date, decimal Amount, string Currency,
        DocumentStatus Status);

    /// <summary>付款单行投影（ApplyId = 货款申请单 Id）</summary>
    private sealed record PaymentRow(string No, DateTime Date, decimal Amount, string Currency, DocumentStatus Status,
        long ApplyId);

    /// <summary>费用单行投影（状态列为付款状态文本）</summary>
    private sealed record ExpenseRow(string No, DateTime Date, decimal Amount, string Currency, string PaymentStatus);

    /// <summary>客诉单行投影（无金额字段）</summary>
    private sealed record ComplaintRow(string No, DateTime Date, string Type, DocumentStatus Status);
}
