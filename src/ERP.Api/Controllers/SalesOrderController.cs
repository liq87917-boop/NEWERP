using ERP.Application.Common;
using ERP.Application.Interfaces;
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

    /// <summary>创建</summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] SalesOrder entity)
    {
        entity.Id = 0;
        entity.OrderNo = await _noService.GenerateAsync(DocumentType.SalesOrder);
        entity.Status = DocumentStatus.Pending;
        entity.CreatedAt = DateTime.Now;
        foreach (var d in entity.Details) d.Amount = d.Quantity * d.UnitPrice;   // 与 Update 对齐：补齐明细金额
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
            d.Amount = d.Quantity * d.UnitPrice;
        }
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
    /// 声明为 public：报价单 / PI 转销售订单（ERP-010，<see cref="SalesOrderConversion"/>）复用同一算法，
    /// 避免带入路径与页面录入路径出现两套口径。
    /// </summary>
    public static void Calculate(SalesOrder entity)
    {
        entity.TotalAmount = entity.Details.Sum(d => d.Quantity * d.UnitPrice);
        entity.DepositAmount = entity.TotalAmount * entity.DepositRatio / 100;
    }

    /// <summary>业务字段校验（佣金比例 0~100；历史单据不填时为 0，不受影响）</summary>
    public static void Validate(SalesOrder entity)
    {
        if (entity.CommissionRatio < 0 || entity.CommissionRatio > 100)
            throw BusinessException.InvalidParameter("佣金比例必须在 0~100 之间");
    }
}
