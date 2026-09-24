using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Export;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ERP.Api.Controllers;

/// <summary>
/// 销售订单控制器
/// </summary>
[Route("api/sales-orders")]
public class SalesOrderController : DocumentControllerBase<SalesOrder>
{
    private readonly IDocumentNumberService _noService;

    public SalesOrderController(IErpDbContext db, IDocumentNumberService noService) : base(db)
    {
        _noService = noService;
    }

    /// <summary>分页查询</summary>
    [HttpGet]
    public async Task<IActionResult> GetPaged([FromQuery] PageQuery query, [FromQuery] DocumentStatus? status)
    {
        query.Normalize();
        var source = Set.AsNoTracking().Where(o => !o.IsDeleted);
        if (status.HasValue) source = source.Where(o => o.Status == status.Value);
        if (!string.IsNullOrWhiteSpace(query.Keyword))
        {
            // 关键字同时匹配订单号 / 客户 PO 号 / 合同号（外贸合同核对时按客户 PO 或合同号检索）
            var kw = query.Keyword;
            source = source.Where(o => o.OrderNo.Contains(kw) || o.CustomerPoNo.Contains(kw) || o.ContractNo.Contains(kw));
        }

        var total = await source.CountAsync();
        var items = await source.OrderByDescending(o => o.Id)
            .Skip((query.Page - 1) * query.PageSize).Take(query.PageSize).ToListAsync();
        return Ok(ApiResponse<PagedResult<SalesOrder>>.Success(
            new PagedResult<SalesOrder> { Items = items, Total = total, Page = query.Page, PageSize = query.PageSize }));
    }

    /// <summary>详情</summary>
    [HttpGet("{id:long}")]
    public async Task<IActionResult> GetById(long id)
    {
        var entity = await Set.AsNoTracking().Include(o => o.Details)
            .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
            ?? throw BusinessException.NotFound("销售订单不存在");
        return Ok(ApiResponse<SalesOrder>.Success(entity));
    }

    /// <summary>从现有订单、销售出库、出口单证和客诉记录派生只读执行时间线。</summary>
    [HttpGet("{id:long}/timeline")]
    public async Task<IActionResult> Timeline(long id)
        => Ok(ApiResponse<List<OrderTimelineEvent>>.Success(
            await OrderExecutionTimeline.ForSalesOrderAsync(Db, id)));

    /// <summary>
    /// 财务核对（ERP-028，只读派生）：按既有引用字段把本单与定金 / 货款申请单、付款单、费用单、客诉单、
    /// 收款单与结算单关联；只有「权威引用 + 已审核 + 币种一致」的记录计入金额，其余仅列出，金额未知为 null（不推断）。
    /// </summary>
    [HttpGet("{id:long}/finance-reconciliation")]
    public async Task<IActionResult> FinanceReconciliation(long id)
        => Ok(ApiResponse<OrderFinanceReconciliationView>.Success(
            await OrderFinanceReconciliation.ForSalesOrderAsync(Db, id)));

    /// <summary>
    /// 出货与收款进度（ERP-032，只读派生）：出货数量按「以本单为来源（SalesOrderId）、未删除、已审核」的销售出库单明细派生
    /// （待提交 / 已提交只单列，已驳回 / 已取消不计入）；收款链接复用 ERP-028 的既有引用字段（定金 / 货款申请单的 SalesOrderId），
    /// 只有「已审核 + 币种一致」计入金额，无权威引用或命中派生上限时金额为 null（未知，不用 0 顶替）。
    /// </summary>
    [HttpGet("{id:long}/progress")]
    public async Task<IActionResult> Progress(long id)
        => Ok(ApiResponse<SalesOrderProgressView>.Success(
            await SalesOrderProgress.ForSalesOrderAsync(Db, id)));

    /// <summary>
    /// 销售订单出货 / 财务进度报表（ERP-032，只读派生、分页有界）：按「客户 + 币种」分组汇总已按权威口径派生的出货数量与收款链接金额，
    /// 不同币种分别成行、绝不合并、不做汇率换算；未链接 / 命中上限一律显式标注未知，不作为应收余额或账龄使用。
    /// </summary>
    [HttpGet("shipment-finance-report")]
    public async Task<IActionResult> ShipmentFinanceReport([FromQuery] SalesOrderShipmentFinanceQuery query)
        => Ok(ApiResponse<SalesOrderShipmentFinanceReportView>.Success(
            await SalesOrderShipmentFinanceReport.ForQueryAsync(Db, query)));

    /// <summary>
    /// 客户订单与收款核对报表（ERP-046，**只读派生**、分页有界）：按「客户 + 币种」分组核对销售订单与收款证据 ——
    /// 订单侧暴露已订 / 已出 / 未出数量与订单金额（完全复用 ERP-032 的权威派生：已审核销售出库单；定金 / 货款申请单的
    /// SalesOrderId 才是权威收款引用，且只有「已审核 + 同币种」计入已关联收款金额），
    /// 收款单（<c>FinanceReceipt</c>）只记录客户、没有订单级引用，因此一律作为**未关联证据**单独列出（链接状态恒为 unlinked），
    /// 系统绝不按客户名 / 订单号文本 / 日期 / 金额相似度把它归到任何销售订单；未知一律记 null（不用 0 顶替）。
    /// <para>本接口<strong>不是</strong>应收账款台账、<strong>不是</strong>客户对账单、<strong>不是</strong>收款授权或结算结果，
    /// 也<strong>不是</strong>账龄表：不推算账期与到期日、不判断是否已收讫，且<strong>不写库</strong>
    /// （不改销售订单、出库单、收款单、收款申请、客户信用、库存、财务与税务记录）。</para>
    /// </summary>
    [HttpGet("receipt-reconciliation-report")]
    public async Task<IActionResult> ReceiptReconciliationReport([FromQuery] SalesOrderReceiptReconciliationQuery query)
        => Ok(ApiResponse<SalesOrderReceiptReconciliationReport>.Success(
            await SalesOrderReceiptReconciliation.ForQueryAsync(Db, query)));


    /// <summary>创建</summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] SalesOrder entity)
    {
        entity.Id = 0;
        entity.OrderNo = await _noService.GenerateAsync(DocumentType.SalesOrder);
        entity.Status = DocumentStatus.Pending;
        entity.CreatedAt = DateTime.Now;
        // 与 Update 对齐：明细金额由服务端按「数量 × 单价」重算（唯一权威口径，忽略客户端金额）
        SalesOrderAmountRules.ApplyDetailAmounts(entity);
        Calculate(entity);
        Validate(entity);
        Db.SalesOrders.Add(entity);
        await Db.SaveChangesAsync();
        return Ok(ApiResponse<object>.Success(new { entity.Id, entity.OrderNo }, "销售订单创建成功"));
    }

    /// <summary>更新</summary>
    [HttpPut("{id:long}")]
    public async Task<IActionResult> Update(long id, [FromBody] SalesOrder entity)
    {
        var existing = await Db.SalesOrders.Include(o => o.Details)
            .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
            ?? throw BusinessException.NotFound("销售订单不存在");
        if (GetStatus(existing) != DocumentStatus.Pending)
            throw BusinessException.RuleConflict("仅待提交状态的单据可修改");

        existing.OrderDate = entity.OrderDate;
        existing.CustomerId = entity.CustomerId;
        existing.SalesmanId = entity.SalesmanId;
        existing.Currency = entity.Currency;
        existing.ExchangeRate = entity.ExchangeRate;
        existing.DepositRatio = entity.DepositRatio;
        existing.PaymentTerms = entity.PaymentTerms;
        existing.DeliveryDate = entity.DeliveryDate;
        existing.ShippingMethod = entity.ShippingMethod;
        existing.PortId = entity.PortId;
        existing.Remark = entity.Remark;

        // 外贸合同与运输信息（ERP-008）
        existing.CustomerPoNo = entity.CustomerPoNo;
        existing.ContractNo = entity.ContractNo;
        existing.TradeTerms = entity.TradeTerms;
        existing.DestinationPort = entity.DestinationPort;
        existing.Consignee = entity.Consignee;
        existing.NotifyParty = entity.NotifyParty;
        existing.ShippingMarks = entity.ShippingMarks;
        // 来源追溯（报价单 / PI → 销售订单）
        existing.SourceQuotationId = entity.SourceQuotationId;
        existing.SourceQuotationNo = entity.SourceQuotationNo;
        existing.SourcePiId = entity.SourcePiId;
        existing.SourcePiNo = entity.SourcePiNo;
        existing.ExportMode = entity.ExportMode;
        existing.CommissionRatio = entity.CommissionRatio;
        existing.BusinessNature = entity.BusinessNature;
        existing.SplitShipment = entity.SplitShipment;
        existing.InspectionRequirement = entity.InspectionRequirement;
        existing.PackagingRequirement = entity.PackagingRequirement;

        Db.SalesOrderDetails.RemoveRange(existing.Details);
        foreach (var d in entity.Details)
        {
            d.Id = 0;
            d.SalesOrderId = id;
            d.CreatedAt = DateTime.Now;
        }
        // 明细金额与合计一律由服务端按唯一权威口径重算（ERP-047：SalesOrderAmountRules）
        SalesOrderAmountRules.ApplyDetailAmounts(entity);
        existing.Details = entity.Details;
        Calculate(existing);
        Validate(existing);
        existing.UpdatedAt = DateTime.Now;
        await Db.SaveChangesAsync();
        return Ok(ApiResponse<object>.Success(null, "销售订单更新成功"));
    }

    /// <summary>打印数据（主表 + 明细；打印模板由 /api/sys/print-templates/sales-order 提供）</summary>
    [HttpGet("{id:long}/print")]
    public async Task<IActionResult> GetPrint(long id)
    {
        var entity = await Set.AsNoTracking().Include(o => o.Details)
            .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
            ?? throw BusinessException.NotFound("销售订单不存在");
        entity.Details = entity.Details.Where(d => !d.IsDeleted).ToList();
        return Ok(ApiResponse<SalesOrder>.Success(entity));
    }

    /// <summary>导出</summary>
    [HttpGet("export")]
    public async Task<IActionResult> Export([FromQuery] DateTime? start, [FromQuery] DateTime? end)
    {
        var source = Db.SalesOrders.AsNoTracking().Include(o => o.Details).Where(o => !o.IsDeleted);
        if (start.HasValue) source = source.Where(o => o.OrderDate >= start.Value);
        if (end.HasValue) source = source.Where(o => o.OrderDate <= end.Value);
        var items = await source.OrderByDescending(o => o.Id).ToListAsync();
        return Ok(ApiResponse<List<SalesOrder>>.Success(items));
    }

    /// <summary>
    /// 带入单证预填（ERP-019）：按销售订单返回**未落库**的单证草稿（商业发票 / 装箱单 / 报关单 / 产地证 / 提单），
    /// 并回传该订单已生成过的单证类型（前端置灰，避免重复生成）。不写库、不占用单证编号流水。
    /// </summary>
    [HttpGet("{id:long}/trade-documents/prefill")]
    public async Task<IActionResult> TradeDocumentPrefill(long id)
    {
        var order = await Set.AsNoTracking()
            .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted, default)
            ?? throw BusinessException.NotFound("销售订单不存在");

        var customer = await TradeDocumentGeneration.LoadCustomerAsync(Db, order.CustomerId);
        var drafts = TradeDocumentGeneration.SalesOrderDocTypes
            .Select(docType => TradeDocumentGeneration.BuildFromSalesOrder(order, customer, docType))
            .ToList();

        var result = await TradeDocumentGeneration.PrefillAsync(Db,
            TradeDocumentGeneration.SalesOrderSourceType, order.Id, order.OrderNo,
            containerNo: null, salesOrderNo: order.OrderNo, loadingListNo: null, drafts: drafts);

        return Ok(ApiResponse<TradeDocPrefillResult>.Success(result, "已按销售订单带入单证草稿"));
    }

    /// <summary>
    /// 生成单证（ERP-019）：按销售订单生成单证中心台账记录（默认商业发票 + 装箱单）。
    /// 守卫：已作废订单拒绝；同一订单 + 同一单证类型只允许一张（重复点击不会产生重复单证）；
    /// 单证落库状态统一为「待制作」，生成后仍可在单证中心人工修改后再流转。
    /// </summary>
    [HttpPost("{id:long}/trade-documents")]
    public async Task<IActionResult> GenerateTradeDocuments(long id, [FromBody] TradeDocGenerateRequest? request)
    {
        var order = await Set.AsNoTracking()
            .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted, default)
            ?? throw BusinessException.NotFound("销售订单不存在");
        if (order.Status == DocumentStatus.Cancelled)
            throw BusinessException.RuleConflict("已作废的销售订单不能生成单证");

        var customer = await TradeDocumentGeneration.LoadCustomerAsync(Db, order.CustomerId);
        var result = await TradeDocumentGeneration.GenerateAsync(Db,
            TradeDocumentGeneration.SalesOrderSourceType, order.Id, order.OrderNo,
            containerNo: null, salesOrderNo: order.OrderNo, loadingListNo: null,
            requestedDocTypes: request?.DocTypes,
            buildDraft: docType => TradeDocumentGeneration.BuildFromSalesOrder(order, customer, docType));

        var numbers = string.Join("、", result.Documents.Select(d => d.DocNo));
        return Ok(ApiResponse<TradeDocGenerateResult>.Success(result, $"已生成单证：{numbers}"));
    }

    /// <summary>导出列定义（含 ERP-008 外贸合同与追溯字段；Excel 导出菜单「销售订单导出」使用）</summary>
    private static readonly List<(string Key, string Title)> ExcelColumns = new()
    {
        ("OrderNo", "订单号"), ("OrderDate", "订单日期"), ("CustomerId", "客户Id"), ("SalesmanId", "业务员Id"),
        ("CustomerPoNo", "客户PO号"), ("ContractNo", "合同号"), ("TradeTerms", "价格条款"),
        ("DestinationPort", "目的港"), ("Consignee", "收货人"), ("NotifyParty", "通知人"), ("ShippingMarks", "唛头"),
        ("SourceQuotationNo", "来源报价单号"), ("SourcePiNo", "来源PI号"),
        ("ExportMode", "出口方式"), ("BusinessNature", "业务性质"), ("CommissionRatio", "佣金比例%"),
        ("SplitShipment", "分批出货"), ("InspectionRequirement", "验货要求"), ("PackagingRequirement", "包装要求"),
        ("Currency", "币种"), ("ExchangeRate", "汇率"), ("TotalAmount", "订单总额"),
        ("DepositRatio", "定金比例%"), ("DepositAmount", "定金金额"),
        ("PaymentTerms", "付款条件"), ("DeliveryDate", "交货日期"), ("ShippingMethod", "运输方式"),
        ("Status", "状态"), ("Remark", "备注"),
    };

    /// <summary>导出销售订单为 Excel（含新增外贸合同与追溯字段）</summary>
    [HttpGet("export-excel")]
    public async Task<IActionResult> ExportExcel([FromQuery] string? keyword, [FromQuery] DocumentStatus? status,
        [FromQuery] DateTime? start, [FromQuery] DateTime? end)
    {
        var source = Db.SalesOrders.AsNoTracking().Where(o => !o.IsDeleted);
        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var kw = keyword;
            source = source.Where(o => o.OrderNo.Contains(kw) || o.CustomerPoNo.Contains(kw) || o.ContractNo.Contains(kw));
        }
        if (status.HasValue) source = source.Where(o => o.Status == status.Value);
        if (start.HasValue) source = source.Where(o => o.OrderDate >= start.Value);
        if (end.HasValue) source = source.Where(o => o.OrderDate <= end.Value);

        var orders = await source.OrderByDescending(o => o.Id).ToListAsync();
        var rows = orders.Select(o => new Dictionary<string, object?>
        {
            ["OrderNo"] = o.OrderNo, ["OrderDate"] = o.OrderDate, ["CustomerId"] = o.CustomerId,
            ["SalesmanId"] = o.SalesmanId, ["CustomerPoNo"] = o.CustomerPoNo, ["ContractNo"] = o.ContractNo,
            ["TradeTerms"] = o.TradeTerms, ["DestinationPort"] = o.DestinationPort, ["Consignee"] = o.Consignee,
            ["NotifyParty"] = o.NotifyParty, ["ShippingMarks"] = o.ShippingMarks,
            ["SourceQuotationNo"] = o.SourceQuotationNo, ["SourcePiNo"] = o.SourcePiNo,
            ["ExportMode"] = o.ExportMode, ["BusinessNature"] = o.BusinessNature,
            ["CommissionRatio"] = o.CommissionRatio, ["SplitShipment"] = o.SplitShipment,
            ["InspectionRequirement"] = o.InspectionRequirement, ["PackagingRequirement"] = o.PackagingRequirement,
            ["Currency"] = o.Currency.ToString(), ["ExchangeRate"] = o.ExchangeRate,
            ["TotalAmount"] = o.TotalAmount, ["DepositRatio"] = o.DepositRatio, ["DepositAmount"] = o.DepositAmount,
            ["PaymentTerms"] = o.PaymentTerms, ["DeliveryDate"] = o.DeliveryDate,
            ["ShippingMethod"] = o.ShippingMethod, ["Status"] = o.Status.ToString(), ["Remark"] = o.Remark,
        }).ToList();

        var bytes = ExcelExporter.ExportRows("SalesOrders", rows, ExcelColumns);
        return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            $"SalesOrders_{DateTime.Now:yyyyMMddHHmmss}.xlsx");
    }

    /// <summary>
    /// 合计口径（销售订单唯一权威算法）：总额 = Σ 明细数量×单价，定金金额 = 总额 × 定金比例%。
    /// 算法实现已抽到 <see cref="SalesOrderAmountRules.Calculate"/>（ERP-047）：报价单 / PI 转销售订单
    /// （ERP-010）与销售订单变更申请登记（ERP-047）复用同一份实现，避免出现第二套金额口径。
    /// </summary>
    public static void Calculate(SalesOrder entity) => SalesOrderAmountRules.Calculate(entity);

    /// <summary>
    /// 业务字段校验（佣金比例 0~100；历史单据不填时为 0，不受影响）。
    /// 实现位于 <see cref="SalesOrderAmountRules.Validate"/>，与合计算法同源。
    /// </summary>
    public static void Validate(SalesOrder entity) => SalesOrderAmountRules.Validate(entity);
}
