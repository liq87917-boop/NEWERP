using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Export;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ERP.Api.Controllers;

/// <summary>
/// 采购订单控制器
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
        var source = Set.AsNoTracking().Where(o => !o.IsDeleted);
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
        return Ok(ApiResponse<PurchaseOrder>.Success(entity));
    }

    /// <summary>从现有订单、供应商确认交期和采购入库记录派生只读执行时间线。</summary>
    [HttpGet("{id:long}/timeline")]
    public async Task<IActionResult> Timeline(long id)
        => Ok(ApiResponse<List<OrderTimelineEvent>>.Success(
            await OrderExecutionTimeline.ForPurchaseOrderAsync(Db, id)));

    /// <summary>创建</summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] PurchaseOrder entity)
    {
        entity.Id = 0;
        entity.OrderNo = await _noService.GenerateAsync(DocumentType.PurchaseOrder);
        entity.Status = DocumentStatus.Pending;
        entity.CreatedAt = DateTime.Now;
        foreach (var d in entity.Details) d.Amount = d.Quantity * d.UnitPrice;   // 与 Update 对齐：补齐明细金额
        Calculate(entity);
        Validate(entity);
        Db.PurchaseOrders.Add(entity);
        await Db.SaveChangesAsync();
        return Ok(ApiResponse<object>.Success(new { entity.Id, entity.OrderNo }, "采购订单创建成功"));
    }

    /// <summary>更新</summary>
    [HttpPut("{id:long}")]
    public async Task<IActionResult> Update(long id, [FromBody] PurchaseOrder entity)
    {
        var existing = await Db.PurchaseOrders.Include(o => o.Details)
            .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
            ?? throw BusinessException.NotFound("采购订单不存在");
        if (GetStatus(existing) != DocumentStatus.Pending)
            throw BusinessException.RuleConflict("仅待提交状态的单据可修改");

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

    /// <summary>打印数据（主表 + 明细；打印模板由 /api/sys/print-templates/purchase-order 提供）</summary>
    [HttpGet("{id:long}/print")]
    public async Task<IActionResult> GetPrint(long id)
    {
        var entity = await Set.AsNoTracking().Include(o => o.Details)
            .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
            ?? throw BusinessException.NotFound("采购订单不存在");
        entity.Details = entity.Details.Where(d => !d.IsDeleted).ToList();
        return Ok(ApiResponse<PurchaseOrder>.Success(entity));
    }

    /// <summary>导出</summary>
    [HttpGet("export")]
    public async Task<IActionResult> Export([FromQuery] DateTime? start, [FromQuery] DateTime? end)
    {
        var source = Db.PurchaseOrders.AsNoTracking().Include(o => o.Details).Where(o => !o.IsDeleted);
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
        var source = Db.PurchaseOrders.AsNoTracking().Where(o => !o.IsDeleted);
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
}
