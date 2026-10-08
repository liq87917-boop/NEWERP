using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Export;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Data;

namespace ERP.Api.Controllers;

/// <summary>
/// 采购订单控制器
/// <para>ERP-371：读取（列表 / 详情 / 导出 / 派生只读视图）/ 创建 / 修改 / 提交 / 审核 / 取消 / 删除每一路由都先做实时授权
/// （既有登录身份 + 账号启用状态 + 既有「采购订单」菜单 + <see cref="SalespersonDataScopeService"/> 权威客户数据范围，
/// 同时覆盖显式归属客户与权威归属销售订单客户），列表在计数 / 分页之前把客户范围下推到数据库；</para>
/// <para>显式销售链接的权威快照与来源校验仍为 ERP-346，取消护栏仍为 ERP-345；提交 / 审核 / 删除 / 取消 / 改单
/// 共用「归属销售订单 → 本采购订单」行锁与可串行化事务，审核在锁内复核来源仍为权威可用。</para>
/// </summary>
[Route("api/purchase-orders")]
public class PurchaseOrderController : DocumentControllerBase<PurchaseOrder>
{
    private readonly IDocumentNumberService _noService;

    public PurchaseOrderController(IErpDbContext db, IDocumentNumberService noService) : base(db)
    {
        _noService = noService;
    }

    /// <summary>分页查询</summary>
    [HttpGet]
    public async Task<IActionResult> GetPaged([FromQuery] PageQuery query, [FromQuery] DocumentStatus? status)
    {
        query.Normalize();
        // ERP-371：身份 / 账号状态 / 既有「采购订单」菜单 / 权威客户范围先于任何计数与分页（数据库侧范围下推）。
        var source = await ApplyProcurementScopeAsync(Set.AsNoTracking().Where(o => !o.IsDeleted));
        if (status.HasValue) source = source.Where(o => o.Status == status.Value);
        if (!string.IsNullOrWhiteSpace(query.Keyword))
        {
            // 关键字同时匹配采购单号 / 合同号 / 归属销售订单号（代理出口核对单客户毛利时按销售订单检索）
            var kw = query.Keyword;
            source = source.Where(o => o.OrderNo.Contains(kw) || o.ContractNo.Contains(kw)
                                       || o.OwningSalesOrderNo.Contains(kw));
        }

        var total = await source.CountAsync();
        var items = await source.OrderByDescending(o => o.Id)
            .Skip((query.Page - 1) * query.PageSize).Take(query.PageSize).ToListAsync();
        return Ok(ApiResponse<PagedResult<PurchaseOrder>>.Success(
            new PagedResult<PurchaseOrder> { Items = items, Total = total, Page = query.Page, PageSize = query.PageSize }));
    }

    /// <summary>详情</summary>
    [HttpGet("{id:long}")]
    public async Task<IActionResult> GetById(long id)
    {
        var entity = await Set.AsNoTracking().Include(o => o.Details)
            .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
            ?? throw BusinessException.NotFound("采购订单不存在");
        // ERP-371：详情与列表同一口径（身份 / 菜单 / 权威归属客户范围）。
        await EnsureOrderAuthorizedAsync(entity);
        return Ok(ApiResponse<PurchaseOrder>.Success(entity));
    }

    /// <summary>从现有订单、供应商确认交期和采购入库记录派生只读执行时间线。</summary>
    [HttpGet("{id:long}/timeline")]
    public async Task<IActionResult> Timeline(long id)
    {
        await EnsureOrderIdAuthorizedAsync(id);
        return Ok(ApiResponse<List<OrderTimelineEvent>>.Success(
            await OrderExecutionTimeline.ForPurchaseOrderAsync(Db, id)));
    }

    /// <summary>
    /// 采购执行进度（ERP-026，只读派生）：按既有采购入库单派生已订 / 已收 / 未收数量（含待审与订单外数量），
    /// 并仅在既有引用可用时暴露已结算 / 未结算金额；无可用引用时金额为 null（未知，不推断）。
    /// </summary>
    [HttpGet("{id:long}/progress")]
    public async Task<IActionResult> Progress(long id)
    {
        await EnsureOrderIdAuthorizedAsync(id);
        return Ok(ApiResponse<PurchaseOrderProgressView>.Success(
            await PurchaseOrderProgress.ForPurchaseOrderAsync(Db, id)));
    }
    /// <summary>
    /// 财务核对（ERP-028，只读派生）：结算金额复用 ERP-026 的权威引用链（付款单 → 货款申请单 → 本单归属销售订单），
    /// 并按既有引用字段列出费用单、客诉单、收款单与结算单；无法按权威引用归属的记录仅列出，金额未知为 null（不推断）。
    /// </summary>
    [HttpGet("{id:long}/finance-reconciliation")]
    public async Task<IActionResult> FinanceReconciliation(long id)
    {
        await EnsureOrderIdAuthorizedAsync(id);
        return Ok(ApiResponse<OrderFinanceReconciliationView>.Success(
            await OrderFinanceReconciliation.ForPurchaseOrderAsync(Db, id)));
    }

    /// <summary>
    /// 供应商采购敞口报表（ERP-031，只读派生）：按供应商 + 币种聚合采购订单金额，并复用 ERP-026 的入库 / 结算权威引用口径
    /// （同一套匹配规则，不引入第二套算法）。链接不唯一或无可用引用时金额为未知（null）并单列为「未链接敞口」，
    /// 绝不折算为应付余额；不同币种分别汇总，不做汇率换算。
    /// </summary>
    [HttpGet("supplier-exposure")]
    public async Task<IActionResult> SupplierExposure([FromQuery] SupplierPurchaseExposureQuery query)
    {
        await EnsureMenuAuthorizedAsync();
        return Ok(ApiResponse<SupplierPurchaseExposureReport>.Success(
            await SupplierPurchaseExposure.ForQueryAsync(Db, query)));
    }

    /// <summary>
    /// 采购交期异常工作台（ERP-099，只读派生）：按供应商 + 显式 as-of 基准日过滤采购订单，
    /// 报告要求交期 / 供应商确认交期 / 已审核入库收货证据，并派生逾期 / 即将到期 / 晚确认 / 已收齐等交期状态；
    /// 缺日期或收货证据不完整为未知；不改写订单任何已登记进度，不执行迁移 / 生产 SQL / 真实数据库操作 / 部署。
    /// </summary>
    [HttpGet("delivery-exceptions")]
    public async Task<IActionResult> DeliveryExceptions([FromQuery] PurchaseOrderDeliveryExceptionQuery query)
    {
        await EnsureMenuAuthorizedAsync();
        return Ok(ApiResponse<PurchaseOrderDeliveryExceptionReport>.Success(
            await PurchaseOrderDeliveryExceptions.ForQueryAsync(Db, query)));
    }

    /// <summary>
    /// 供应商首收交期（ERP-109，只读派生）：按供应商 + 订单日期区间筛选未删除且已审核的采购订单，
    /// 用显式入库单 PurchaseOrderId 链接批量装载本页入库，派生「订单日期 → 首张有效已审核入库」的首收日期与间隔天数；
    /// 未审核 / 已删除 / 供应商不一致 / 早于订单日期的入库仅作异常证据列出、不计入首收，缺失或不一致日期保持未知。
    /// <para>只读：不写任何表、不执行迁移 / 生产 SQL / 真实数据库操作 / 部署，不改写采购订单 / 入库单 / 库存与库存成本；
    /// 这不是完整交付完成度，也不是准时率评分。</para>
    /// </summary>
    [HttpGet("first-receipt-lead-times")]
    public async Task<IActionResult> SupplierFirstReceiptLeadTimes([FromQuery] SupplierFirstReceiptLeadTimeQuery query)
    {
        await EnsureMenuAuthorizedAsync();
        return Ok(ApiResponse<SupplierFirstReceiptLeadTimeReport>.Success(
            await SupplierFirstReceiptLeadTimeService.ForQueryAsync(Db, query)));
    }

    /// <summary>
    /// 采购订单退货影响（ERP-100，只读派生）：按显式链接链「采购退货 → 来源入库单 → 本采购订单」派生
    /// 毛收货 / 有效退货 / 净收货数量，并把未审核、供应商不一致、来源已删除、来源不属于本单、未关联来源
    /// 的退货作为异常单列（绝不推断为扣减）；超退净额为负、不静默钳制；证据不完整为未知。
    /// <para>只读：不写任何表、不执行迁移 / 生产 SQL / 真实数据库操作 / 部署，不改写订单任何已登记进度。</para>
    /// </summary>
    [HttpGet("{id:long}/return-impact")]
    public async Task<IActionResult> ReturnImpact(long id)
    {
        await EnsureOrderIdAuthorizedAsync(id);
        return Ok(ApiResponse<PurchaseOrderReturnImpactView>.Success(
            await PurchaseOrderReturnImpact.ForOrderAsync(Db, id)));
    }

    /// <summary>
    /// 发票证据汇总（ERP-048，只读派生）：只按 ERP-043 的持久化关联行派生「已登记且未作废」的已开票金额、
    /// 未开票金额、发票张数与覆盖状态，并把草稿 / 已作废 / 无效（供应商 / 币种不一致）/ 无法确认证据单独列出
    /// （覆盖状态与订单可用性文案复用 ERP-044 的同一套口径）。
    /// <para>只读：不改写采购订单状态、到货进度、结算进度文本、发票与关联行、库存与库存成本、收付款、
    /// 供应商余额、费用或退税记录；本值只是采购发票证据，不是应付余额、付款授权、税务申报判断或结算状态。</para>
    /// </summary>
    [HttpGet("{id:long}/invoice-evidence")]
    public async Task<IActionResult> InvoiceEvidence(long id)
    {
        await EnsureOrderIdAuthorizedAsync(id);
        return Ok(ApiResponse<PurchaseOrderInvoiceEvidenceDetail>.Success(
            await PurchaseOrderInvoiceEvidence.ForOrderAsync(Db, id)));
    }

    /// <summary>
    /// 采购订单列表用有界发票覆盖汇总（ERP-048，只读派生）：逗号分隔的订单 Id（一次最多 200 张），
    /// 固定 3 次数据集访问、无逐行查库；命中读取上限时金额与计数按「未知」返回，绝不报出部分合计。
    /// </summary>
    [HttpGet("invoice-evidence-summaries")]
    public async Task<IActionResult> InvoiceEvidenceSummaries([FromQuery] string? ids)
    {
        await EnsureOrderIdsAuthorizedAsync(ParseOrderIds(ids));
        return Ok(ApiResponse<PurchaseOrderInvoiceEvidenceBatch>.Success(
            await PurchaseOrderInvoiceEvidence.ForOrdersAsync(
                Db, new PurchaseOrderInvoiceEvidenceQuery { Ids = ids })));
    }

    /// <summary>
    /// 付款引用证据详情（ERP-050，只读派生）：只按 ERP-049 的持久化引用行派生该订单的**有效**已引用金额、
    /// 付款单张数、行数，以及「参与证据的付款单金额 − 本订单已引用金额」的未指向上下文，
    /// 并把已作废 / 无效（供应商 / 币种或快照不一致）/ 无法确认（付款单已删除）证据单独分桶逐条列出。
    /// <para>只读：不改写采购订单、付款单、发票与发票关联、库存与库存成本、收付款、供应商余额、费用或退税记录；
    /// 本值只是付款引用证据，不是银行付款金额、应付余额、发票核销或结算结果，也不据此判定已付款 / 逾期。</para>
    /// </summary>
    [HttpGet("{id:long}/payment-evidence")]
    public async Task<IActionResult> PaymentEvidence(long id)
    {
        await EnsureOrderIdAuthorizedAsync(id);
        return Ok(ApiResponse<PurchaseOrderPaymentEvidenceDetail>.Success(
            await PurchaseOrderPaymentEvidence.ForOrderAsync(Db, id)));
    }

    /// <summary>
    /// 采购订单列表用有界付款引用证据汇总（ERP-050，只读派生）：逗号分隔的订单 Id（一次最多 200 张），
    /// 固定 4 次数据集访问（订单 + 引用行 + 付款单 + 付款单侧有效引用合计聚合）、无逐行查库；
    /// 命中读取上限时金额与计数按「未知」返回，绝不报出部分合计，也绝不呈现为已付款 / 未付款 / 逾期。
    /// </summary>
    [HttpGet("payment-evidence-summaries")]
    public async Task<IActionResult> PaymentEvidenceSummaries([FromQuery] string? ids)
    {
        await EnsureOrderIdsAuthorizedAsync(ParseOrderIds(ids));
        return Ok(ApiResponse<PurchaseOrderPaymentEvidenceBatch>.Success(
            await PurchaseOrderPaymentEvidence.ForOrdersAsync(
                Db, new PurchaseOrderPaymentEvidenceQuery { Ids = ids })));
    }

    /// <summary>
    /// 已分配付款引用证据详情（ERP-067，只读派生）：只按 ERP-066 的持久化「付款单 → 供应商采购发票」引用行派生，
    /// 并只经 ERP-043 / ERP-065 的持久化「发票 → 采购订单」关联行归属到本订单 —— 暴露有效（未作废、发票仍为已登记、
    /// 供应商与币种自相一致）引用金额、可**安全归属本订单**的金额（仅关联本订单的发票）、发票级金额（发票还被其他订单关联，
    /// 不按比例摊派）、发票张数与付款单张数，并把已作废 / 发票失效 / 无效 / 无法确认证据单独分桶逐条列出。
    /// <para>只读：不改写采购订单、发票与关联行、付款单与引用行、库存与库存成本、收付款、供应商余额、费用或退税记录；
    /// 本值只是**运营性的证据视图**，不是总账或应付余额、不是法定供应商对账单、不是税务申报、不是付款授权或结算确认，
    /// 也不据此判定已付款 / 已结清 / 逾期（三类证据绝不轧差）。</para>
    /// </summary>
    [HttpGet("{id:long}/invoice-payment-evidence")]
    public async Task<IActionResult> InvoicePaymentEvidence(long id)
    {
        await EnsureOrderIdAuthorizedAsync(id);
        return Ok(ApiResponse<PurchaseOrderInvoicePaymentEvidenceDetail>.Success(
            await PurchaseOrderInvoicePaymentEvidence.ForOrderAsync(Db, id)));
    }

    /// <summary>
    /// 采购订单列表用有界「已分配付款引用证据」汇总（ERP-067，只读派生）：逗号分隔的订单 Id（一次最多 200 张），
    /// 固定 7 次数据集访问（订单 + 订单侧关联行 + 发票 + 发票侧关联行 + 引用行 + 付款单 + 付款单侧有效合计聚合）、
    /// 无逐行查库、无逐行存储访问；命中读取上限时金额与计数按「未知」返回，绝不报出部分合计，
    /// 也绝不呈现为已付款 / 未付款 / 逾期，绝不与「付款 → 采购订单」引用金额相加。
    /// </summary>
    [HttpGet("invoice-payment-evidence-summaries")]
    public async Task<IActionResult> InvoicePaymentEvidenceSummaries([FromQuery] string? ids)
    {
        await EnsureOrderIdsAuthorizedAsync(ParseOrderIds(ids));
        return Ok(ApiResponse<PurchaseOrderInvoicePaymentEvidenceBatch>.Success(
            await PurchaseOrderInvoicePaymentEvidence.ForOrdersAsync(
                Db, new PurchaseOrderInvoicePaymentEvidenceQuery { Ids = ids })));
    }

    /// <summary>创建</summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] PurchaseOrder entity)
    {
        if (entity.OwningSalesOrderId is null)
            return await CreateUnlinkedAsync(entity);

        // ERP-347：显式归属销售订单的采购创建与「来源销售订单取消」使用同一把销售订单行锁（UPDLOCK/HOLDLOCK），
        // 在可串行化事务内先锁来源订单行，再解析权威来源；并发时「来源取消」与「采购创建」只能成功其一。
        await using var transaction = await Db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        try
        {
            await AcquireSalesOrderLinkLockAsync(entity.OwningSalesOrderId.Value);

            // ERP-371：身份 / 菜单 / 归属来源范围先于单据号与任何写入（fail closed）。
            await EnsureProposedAuthorizedAsync(entity);

            entity.Id = 0;
            entity.OrderNo = await _noService.GenerateAsync(DocumentType.PurchaseOrder);
            entity.Status = DocumentStatus.Pending;
            entity.CreatedAt = DateTime.Now;
            foreach (var d in entity.Details) d.Amount = d.Quantity * d.UnitPrice;
            await PurchaseSalesOrderLinkRules.ApplyLinkAsync(Db, entity, CurrentUserId());
            Calculate(entity);
            Validate(entity);
            Db.PurchaseOrders.Add(entity);
            await Db.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }

        return Ok(ApiResponse<object>.Success(new { entity.Id, entity.OrderNo }, "采购订单创建成功"));
    }

    /// <summary>更新</summary>
    [HttpPut("{id:long}")]
    public async Task<IActionResult> Update(long id, [FromBody] PurchaseOrder entity)
    {
        if (entity.OwningSalesOrderId is null)
            return await UpdateUnlinkedAsync(id, entity);

        // ERP-347：与创建同口径——显式归属销售订单的采购更新与「来源销售订单取消」使用同一把销售订单行锁串行化。
        await using var transaction = await Db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        try
        {
            await AcquireSalesOrderLinkLockAsync(entity.OwningSalesOrderId.Value);
            // ERP-371：同序（销售订单 → 采购订单）加行锁，把来源链接变更与状态流转串行化，避免死锁。
            await AcquirePurchaseOrderLockAsync(id);

            var existing = await Db.PurchaseOrders.Include(o => o.Details)
                .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
                ?? throw BusinessException.NotFound("采购订单不存在");
            if (GetStatus(existing) != DocumentStatus.Pending)
                throw BusinessException.RuleConflict("仅待提交状态的单据可修改");

            // ERP-371：分配字段 / 替换明细之前先校验「已存」与「请求」两侧归属（身份 / 菜单 / 权威客户范围）。
            await EnsureOrderAuthorizedAsync(existing);
            await EnsureProposedAuthorizedAsync(entity);

            await PurchaseSalesOrderLinkRules.ApplyLinkAsync(Db, entity, CurrentUserId());

            existing.OrderDate = entity.OrderDate;
            existing.SupplierId = entity.SupplierId;
            existing.BuyerId = entity.BuyerId;
            existing.Currency = entity.Currency;
            existing.ExchangeRate = entity.ExchangeRate;
            existing.PaymentTerms = entity.PaymentTerms;
            existing.DeliveryDate = entity.DeliveryDate;
            existing.PortId = entity.PortId;
            existing.Remark = entity.Remark;

            // 采购执行与结算追溯（ERP-008）
            existing.OwningCustomerId = entity.OwningCustomerId;
            existing.OwningCustomerName = entity.OwningCustomerName;
            existing.OwningSalesOrderId = entity.OwningSalesOrderId;
            existing.OwningSalesOrderNo = entity.OwningSalesOrderNo;
            existing.AdvanceOnBehalf = entity.AdvanceOnBehalf;
            existing.SupplierConfirmedDate = entity.SupplierConfirmedDate;
            existing.TaxRate = entity.TaxRate;
            existing.TaxIncluded = entity.TaxIncluded;
            existing.ArrivalProgress = entity.ArrivalProgress;
            existing.QcStatus = entity.QcStatus;
            existing.ContractNo = entity.ContractNo;
            existing.SettlementProgress = entity.SettlementProgress;

            Db.PurchaseOrderDetails.RemoveRange(existing.Details);
            foreach (var d in entity.Details)
            {
                d.Id = 0;
                d.PurchaseOrderId = id;
                d.CreatedAt = DateTime.Now;
                d.Amount = d.Quantity * d.UnitPrice;
            }
            existing.Details = entity.Details;
            Calculate(existing);
            Validate(existing);
            existing.UpdatedAt = DateTime.Now;
            await Db.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }

        return Ok(ApiResponse<object>.Success(null, "采购订单更新成功"));
    }

    /// <summary>无归属销售订单的采购创建（保持历史行为，不加锁、不开事务）。</summary>
    private async Task<IActionResult> CreateUnlinkedAsync(PurchaseOrder entity)
    {
        // ERP-371：无归属备货采购同样先解析身份 / 菜单 / 请求归属范围（fail closed，特权备货采购保持可用）。
        await EnsureProposedAuthorizedAsync(entity);

        entity.Id = 0;
        entity.OrderNo = await _noService.GenerateAsync(DocumentType.PurchaseOrder);
        entity.Status = DocumentStatus.Pending;
        entity.CreatedAt = DateTime.Now;
        foreach (var d in entity.Details) d.Amount = d.Quantity * d.UnitPrice;
        Calculate(entity);
        Validate(entity);
        Db.PurchaseOrders.Add(entity);
        await Db.SaveChangesAsync();
        return Ok(ApiResponse<object>.Success(new { entity.Id, entity.OrderNo }, "采购订单创建成功"));
    }

    /// <summary>无归属销售订单的采购更新（保持历史行为，不加锁、不开事务）。</summary>
    private async Task<IActionResult> UpdateUnlinkedAsync(long id, PurchaseOrder entity)
    {
        var existing = await Db.PurchaseOrders.Include(o => o.Details)
            .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
            ?? throw BusinessException.NotFound("采购订单不存在");
        if (GetStatus(existing) != DocumentStatus.Pending)
            throw BusinessException.RuleConflict("仅待提交状态的单据可修改");

        // ERP-371：分配字段 / 替换明细之前先校验「已存」与「请求」两侧归属（身份 / 菜单 / 权威客户范围）。
        await EnsureOrderAuthorizedAsync(existing);
        await EnsureProposedAuthorizedAsync(entity);

        existing.OrderDate = entity.OrderDate;
        existing.SupplierId = entity.SupplierId;
        existing.BuyerId = entity.BuyerId;
        existing.Currency = entity.Currency;
        existing.ExchangeRate = entity.ExchangeRate;
        existing.PaymentTerms = entity.PaymentTerms;
        existing.DeliveryDate = entity.DeliveryDate;
        existing.PortId = entity.PortId;
        existing.Remark = entity.Remark;

        // 采购执行与结算追溯（ERP-008）
        existing.OwningCustomerId = entity.OwningCustomerId;
        existing.OwningCustomerName = entity.OwningCustomerName;
        existing.OwningSalesOrderId = entity.OwningSalesOrderId;
        existing.OwningSalesOrderNo = entity.OwningSalesOrderNo;
        existing.AdvanceOnBehalf = entity.AdvanceOnBehalf;
        existing.SupplierConfirmedDate = entity.SupplierConfirmedDate;
        existing.TaxRate = entity.TaxRate;
        existing.TaxIncluded = entity.TaxIncluded;
        existing.ArrivalProgress = entity.ArrivalProgress;
        existing.QcStatus = entity.QcStatus;
        existing.ContractNo = entity.ContractNo;
        existing.SettlementProgress = entity.SettlementProgress;

        Db.PurchaseOrderDetails.RemoveRange(existing.Details);
        foreach (var d in entity.Details)
        {
            d.Id = 0;
            d.PurchaseOrderId = id;
            d.CreatedAt = DateTime.Now;
            d.Amount = d.Quantity * d.UnitPrice;
        }
        existing.Details = entity.Details;
        Calculate(existing);
        Validate(existing);
        existing.UpdatedAt = DateTime.Now;
        await Db.SaveChangesAsync();
        return Ok(ApiResponse<object>.Success(null, "采购订单更新成功"));
    }

    /// <summary>
    /// 对归属销售订单行加更新锁（UPDLOCK, HOLDLOCK），把同单并发「来源取消 / 采购归属关联」串行化在同一事务内；
    /// 非关系型提供程序（内存库）无法执行表提示，跳过即可（事务本身等价无事务）。
    /// </summary>
    private async Task AcquireSalesOrderLinkLockAsync(long salesOrderId)
    {
        if (!Db.Database.IsRelational()) return;
        await Db.Database
            .SqlQueryRaw<long>(
                "SELECT Id FROM db_owner.SalesOrders WITH (UPDLOCK, HOLDLOCK) WHERE Id = {0}",
                salesOrderId)
            .ToListAsync();
    }

    /// <summary>打印数据（主表 + 明细；打印模板由 /api/sys/print-templates/purchase-order 提供）</summary>
    [HttpGet("{id:long}/print")]
    public async Task<IActionResult> GetPrint(long id)
    {
        var entity = await Set.AsNoTracking().Include(o => o.Details)
            .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
            ?? throw BusinessException.NotFound("采购订单不存在");
        // ERP-371：打印与详情同一授权口径（身份 / 菜单 / 权威归属客户范围）。
        await EnsureOrderAuthorizedAsync(entity);
        entity.Details = entity.Details.Where(d => !d.IsDeleted).ToList();
        return Ok(ApiResponse<PurchaseOrder>.Success(entity));
    }

    /// <summary>导出</summary>
    [HttpGet("export")]
    public async Task<IActionResult> Export([FromQuery] DateTime? start, [FromQuery] DateTime? end)
    {
        var source = await ApplyProcurementScopeAsync(
            Db.PurchaseOrders.AsNoTracking().Include(o => o.Details).Where(o => !o.IsDeleted));
        if (start.HasValue) source = source.Where(o => o.OrderDate >= start.Value);
        if (end.HasValue) source = source.Where(o => o.OrderDate <= end.Value);
        var items = await source.OrderByDescending(o => o.Id).ToListAsync();
        return Ok(ApiResponse<List<PurchaseOrder>>.Success(items));
    }

    /// <summary>导出列定义（含 ERP-008 归属客户 / 采购执行与结算字段；Excel 导出菜单「采购订单导出」使用）</summary>
    private static readonly List<(string Key, string Title)> ExcelColumns = new()
    {
        ("OrderNo", "采购单号"), ("OrderDate", "订单日期"), ("SupplierId", "供应商Id"), ("BuyerId", "采购员Id"),
        ("ContractNo", "采购合同号"), ("OwningCustomerName", "归属客户"), ("OwningSalesOrderNo", "归属销售订单号"),
        ("AdvanceOnBehalf", "代垫货款"), ("SupplierConfirmedDate", "供应商确认交期"),
        ("TaxRate", "税率%"), ("TaxIncluded", "含税单价"),
        ("ArrivalProgress", "到货进度"), ("QcStatus", "验货状态"), ("SettlementProgress", "结算进度"),
        ("Currency", "币种"), ("ExchangeRate", "汇率"), ("TotalAmount", "订单总额"),
        ("PaymentTerms", "付款条件"), ("DeliveryDate", "交货日期"),
        ("Status", "状态"), ("Remark", "备注"),
    };

    /// <summary>导出采购订单为 Excel（含新增归属与采购执行追溯字段）</summary>
    [HttpGet("export-excel")]
    public async Task<IActionResult> ExportExcel([FromQuery] string? keyword, [FromQuery] DocumentStatus? status,
        [FromQuery] DateTime? start, [FromQuery] DateTime? end)
    {
        var source = await ApplyProcurementScopeAsync(Db.PurchaseOrders.AsNoTracking().Where(o => !o.IsDeleted));
        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var kw = keyword;
            source = source.Where(o => o.OrderNo.Contains(kw) || o.ContractNo.Contains(kw)
                                       || o.OwningSalesOrderNo.Contains(kw));
        }
        if (status.HasValue) source = source.Where(o => o.Status == status.Value);
        if (start.HasValue) source = source.Where(o => o.OrderDate >= start.Value);
        if (end.HasValue) source = source.Where(o => o.OrderDate <= end.Value);

        var orders = await source.OrderByDescending(o => o.Id).ToListAsync();
        var rows = orders.Select(o => new Dictionary<string, object?>
        {
            ["OrderNo"] = o.OrderNo, ["OrderDate"] = o.OrderDate, ["SupplierId"] = o.SupplierId,
            ["BuyerId"] = o.BuyerId, ["ContractNo"] = o.ContractNo,
            ["OwningCustomerName"] = o.OwningCustomerName, ["OwningSalesOrderNo"] = o.OwningSalesOrderNo,
            ["AdvanceOnBehalf"] = o.AdvanceOnBehalf, ["SupplierConfirmedDate"] = o.SupplierConfirmedDate,
            ["TaxRate"] = o.TaxRate, ["TaxIncluded"] = o.TaxIncluded,
            ["ArrivalProgress"] = o.ArrivalProgress, ["QcStatus"] = o.QcStatus,
            ["SettlementProgress"] = o.SettlementProgress,
            ["Currency"] = o.Currency.ToString(), ["ExchangeRate"] = o.ExchangeRate,
            ["TotalAmount"] = o.TotalAmount, ["PaymentTerms"] = o.PaymentTerms,
            ["DeliveryDate"] = o.DeliveryDate, ["Status"] = o.Status.ToString(), ["Remark"] = o.Remark,
        }).ToList();

        var bytes = ExcelExporter.ExportRows("PurchaseOrders", rows, ExcelColumns);
        return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            $"PurchaseOrders_{DateTime.Now:yyyyMMddHHmmss}.xlsx");
    }

    /// <summary>
    /// 合计口径（采购订单唯一权威算法）：总额 = Σ 明细数量×单价。
    /// 声明为 public：供应商比价选中行转采购订单（ERP-020，<see cref="PurchaseQuoteConversion" />）
    /// 复用同一算法，避免带入路径与页面录入路径出现两套口径。
    /// </summary>
    public static void Calculate(PurchaseOrder entity)
    {
        entity.TotalAmount = entity.Details.Sum(d => d.Quantity * d.UnitPrice);
    }

    /// <summary>业务字段校验（税率 0~100；历史单据不填时为 0，不受影响）</summary>
    public static void Validate(PurchaseOrder entity)
    {
        if (entity.TaxRate < 0 || entity.TaxRate > 100)
            throw BusinessException.InvalidParameter("税率必须在 0~100 之间");
    }

    /// <summary>
    /// 取消采购订单（ERP-345）：在可串行化事务内对订单行加更新锁，把「拒绝判定」与「状态变更」做成一个原子步骤，
    /// 并与同单入库审核（ERP-342）串行化——并发场景下「入库审核通过」与「来源取消」不可能同时成功。
    /// <para>只把状态改为已取消，<strong>不改动</strong>订单明细 / 金额 / 供应商 / 币种等原始字段；
    /// 存在已审核且未冲销的入库或有效付款 / 发票引用证据时拒绝，且本方法<strong>绝不</strong>静默冲销库存或财务。</para>
    /// </summary>
    [HttpPost("{id:long}/cancel")]
    public override async Task<IActionResult> Cancel(long id)
    {
        await using var transaction = await Db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        try
        {
            await AcquireOrderStateLocksAsync(id);

            var entity = await Db.PurchaseOrders.Include(o => o.Details)
                .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
                ?? throw BusinessException.NotFound("采购订单不存在");

            await PurchaseOrderCancellationRules.ValidateCancellationAsync(Db, entity, CurrentUserId());

            SetStatus(entity, DocumentStatus.Cancelled);
            await Db.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }

        return Ok(ApiResponse<object>.Success(null, "已取消"));
    }

    /// <summary>
    /// 采购订单状态流转锁序（ERP-371）：先锁归属销售订单行、再锁本采购订单行（<c>UPDLOCK, HOLDLOCK</c>），
    /// 与创建 / 更新的「来源销售订单 → 采购订单」锁序一致，避免与来源取消 / 链接变更死锁；
    /// 同单并发「提交 / 审核 / 删除 / 取消 / 改单」因此串行化在同一事务内。
    /// 非关系型提供程序（内存库）无法执行表提示，跳过即可（事务本身等价无事务）。
    /// </summary>
    private async Task AcquireOrderStateLocksAsync(long orderId)
    {
        // Discovery must not retain a shared purchase lock before acquiring the
        // upstream sales lock. Recheck the pointer under the final update lock.
        long? salesOrderId;
        if (Db.Database.IsRelational())
        {
            var pointers = await Db.Database.SqlQueryRaw<long>(
                "SELECT COALESCE(OwningSalesOrderId, 0) AS Value FROM db_owner.PurchaseOrders WITH (READUNCOMMITTED) WHERE Id = {0} AND IsDeleted = 0",
                orderId).ToListAsync();
            salesOrderId = pointers.FirstOrDefault();
        }
        else
            salesOrderId = await Db.PurchaseOrders.AsNoTracking()
                .Where(o => o.Id == orderId && !o.IsDeleted)
                .Select(o => (long?)o.OwningSalesOrderId).FirstOrDefaultAsync();
        if (salesOrderId is > 0)
            await AcquireSalesOrderLinkLockAsync(salesOrderId.Value);
        await AcquirePurchaseOrderLockAsync(orderId);
        var lockedSourceId = await Db.PurchaseOrders.AsNoTracking()
            .Where(o => o.Id == orderId && !o.IsDeleted)
            .Select(o => (long?)o.OwningSalesOrderId).FirstOrDefaultAsync();
        if ((lockedSourceId ?? 0) != (salesOrderId ?? 0))
            throw BusinessException.RuleConflict("采购订单来源已变更，请刷新后重试");
    }

    /// <summary>对采购订单行加更新锁（UPDLOCK, HOLDLOCK）；非关系型提供程序跳过。</summary>
    private async Task AcquirePurchaseOrderLockAsync(long orderId)
    {
        if (!Db.Database.IsRelational()) return;
        await Db.Database
            .SqlQueryRaw<long>(
                "SELECT Id FROM db_owner.PurchaseOrders WITH (UPDLOCK, HOLDLOCK) WHERE Id = {0}",
                orderId)
            .ToListAsync();
    }

    /// <summary>
    /// 提交（ERP-371）：与本采购订单行锁 / 可串行化事务同口径，锁内先复核实时授权，
    /// 被拒绝时不改任何状态；授权接入后既有 Pending → Submitted 语义不变。
    /// </summary>
    [HttpPost("{id:long}/submit")]
    public override async Task<IActionResult> Submit(long id)
    {
        await using var transaction = await Db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        try
        {
            await AcquireOrderStateLocksAsync(id);

            var entity = await GetOrThrowAsync(id, "采购订单不存在");
            await EnsureOrderAuthorizedAsync(entity);

            var result = await base.Submit(id);
            await transaction.CommitAsync();
            return result;
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    /// <summary>
    /// 审核（ERP-371）：与提交 / 取消同一把本采购订单行锁 + 可串行化事务，锁内复核实时授权，
    /// 并重新校验归属来源销售订单仍为权威可用（存在、未删除、已审核、未取消）——
    /// 并发场景下「来源失效」与「采购审核」不可能同时成功，消除审核与来源作废的竞争。
    /// </summary>
    [HttpPost("{id:long}/approve")]
    public override async Task<IActionResult> Approve(long id)
    {
        await using var transaction = await Db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        try
        {
            await AcquireOrderStateLocksAsync(id);

            var entity = await GetOrThrowAsync(id, "采购订单不存在");
            await EnsureOrderAuthorizedAsync(entity);
            await PurchaseSalesOrderLinkRules.EnsureSourceLinkStillValidAsync(Db, entity.OwningSalesOrderId);

            var result = await base.Approve(id);
            await transaction.CommitAsync();
            return result;
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    /// <summary>
    /// 删除（软删除，仅待提交）：与本采购订单行锁 / 可串行化事务同口径，锁内先复核实时授权，
    /// 被拒绝时不删除任何单据 / 明细，历史审计证据保持不变。
    /// </summary>
    [HttpDelete("{id:long}")]
    public override async Task<IActionResult> Delete(long id)
    {
        await using var transaction = await Db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        try
        {
            await AcquireOrderStateLocksAsync(id);

            var entity = await GetOrThrowAsync(id, "采购订单不存在");
            await EnsureOrderAuthorizedAsync(entity);

            var result = await base.Delete(id);
            await transaction.CommitAsync();
            return result;
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    // ==================== ERP-371：实时授权辅助（身份 / 菜单 / 权威客户范围） ====================

    /// <summary>
    /// 是否必须执行实时授权：真实 HTTP 请求（MVC 绑定，<c>Request.Path</c> 已赋值）一律执行；
    /// 进程内直接调用（历史单元测试 / 内部派生读取，无 HTTP 请求管线）仅在携带当前登录身份时执行。
    /// 只对「既无任何登录身份、又不在 HTTP 请求管线内」的调用免授权：这类调用不可能由外部请求到达，
    /// 也绝不把缺失身份当作管理员（真实匿名请求因处于请求管线内一律 fail closed）。
    /// </summary>
    private bool RequiresLiveAuthorization()
    {
        var http = ControllerContext?.HttpContext;
        if (http is null) return false;
        return http.Request.Path.HasValue || CurrentUserId() is not null;
    }

    /// <summary>身份 / 账号状态 / 菜单授权（fail closed）：缺失 / 已删除按未认证，禁用 / 无菜单按权限不足。</summary>
    private async Task EnsureMenuAuthorizedAsync()
    {
        if (!RequiresLiveAuthorization()) return;
        await PurchaseOrderAuthorizationRules.EnsureMenuAuthorizedAsync(Db, CurrentUserId());
    }

    /// <summary>单据级授权：身份 / 菜单 + 权威归属客户范围（显式归属客户 + 权威归属销售订单客户）。</summary>
    private async Task EnsureOrderAuthorizedAsync(PurchaseOrder order)
    {
        if (!RequiresLiveAuthorization()) return;
        await PurchaseOrderAuthorizationRules.EnsureOrderAuthorizedAsync(Db, CurrentUserId(), order);
    }

    /// <summary>请求侧（创建 / 修改提交的归属）授权：先校验再分配字段 / 替换明细 / 消耗单据号。</summary>
    private async Task EnsureProposedAuthorizedAsync(PurchaseOrder proposed)
    {
        if (!RequiresLiveAuthorization()) return;
        await PurchaseOrderAuthorizationRules.EnsureProposedAuthorizedAsync(Db, CurrentUserId(), proposed);
    }

    /// <summary>按 Id 复核单张采购订单的权威归属客户范围（详情 / 打印 / 派生只读视图使用）。</summary>
    private async Task EnsureOrderIdAuthorizedAsync(long id)
    {
        if (!RequiresLiveAuthorization()) return;
        var ownership = await PurchaseOrderAuthorizationRules.LoadOwnershipAsync(Db, new[] { id });
        await PurchaseOrderAuthorizationRules.EnsureOrdersAuthorizedAsync(Db, CurrentUserId(), ownership);
    }

    /// <summary>按 Id 批量复核采购订单权威归属客户范围（列表用批量派生汇总使用）。</summary>
    private async Task EnsureOrderIdsAuthorizedAsync(IReadOnlyCollection<long> ids)
    {
        if (!RequiresLiveAuthorization() || ids.Count == 0) return;
        var ownership = await PurchaseOrderAuthorizationRules.LoadOwnershipAsync(Db, ids);
        await PurchaseOrderAuthorizationRules.EnsureOrdersAuthorizedAsync(Db, CurrentUserId(), ownership);
    }

    /// <summary>列表 / 导出的权威客户范围下推（计数 / 分页之前）；非 HTTP / 无身份的内部调用不改变既有查询。</summary>
    private async Task<IQueryable<PurchaseOrder>> ApplyProcurementScopeAsync(IQueryable<PurchaseOrder> source)
    {
        if (!RequiresLiveAuthorization()) return source;
        return await PurchaseOrderAuthorizationRules.ApplyScopeAsync(Db, source, CurrentUserId());
    }

    /// <summary>解析逗号分隔的订单 Id（与既有批量证据接口同一入参口径，非法片段忽略）。</summary>
    private static List<long> ParseOrderIds(string? ids)
    {
        var result = new List<long>();
        if (string.IsNullOrWhiteSpace(ids)) return result;
        foreach (var segment in ids.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (long.TryParse(segment, out var id) && id > 0)
                result.Add(id);
        }
        return result;
    }
}
