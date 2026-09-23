using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ERP.Api.Controllers;

/// <summary>
/// 形式发票 PI 控制器（主子表：一张 PI 多行商品）
/// 业务链：询价单 Inquiry → 报价单 Quotation → **形式发票 PI** → 销售订单
/// 说明：本单据使用 EF 主子表实现（不走存储过程），不触碰现有单据的 SP
/// </summary>
[Route("api/sales/proforma-invoices")]
public class ProformaInvoiceController : DocumentControllerBase<ProformaInvoice>
{
    private readonly IDocumentNumberService _noService;

    public ProformaInvoiceController(IErpDbContext db, IDocumentNumberService noService) : base(db)
    {
        _noService = noService;
    }

    /// <summary>分页查询（keyword 匹配 PI 号 / 客户 / 来源报价单号 / 业务员）</summary>
    [HttpGet]
    public async Task<IActionResult> GetPaged([FromQuery] PageQuery query, [FromQuery] DocumentStatus? status,
        [FromQuery] DateTime? start, [FromQuery] DateTime? end)
    {
        query.Normalize();
        var source = Set.AsNoTracking().Where(o => !o.IsDeleted);
        if (status.HasValue) source = source.Where(o => o.Status == status.Value);
        if (start.HasValue) source = source.Where(o => o.PiDate >= start.Value);
        if (end.HasValue) source = source.Where(o => o.PiDate <= end.Value);
        if (!string.IsNullOrWhiteSpace(query.Keyword))
        {
            var kw = query.Keyword;
            source = source.Where(o => o.PiNo.Contains(kw) || o.CustomerName.Contains(kw)
                                       || o.QuotationNo.Contains(kw) || o.SalesmanName.Contains(kw));
        }

        var total = await source.CountAsync();
        var items = await source.OrderByDescending(o => o.Id)
            .Skip((query.Page - 1) * query.PageSize).Take(query.PageSize).ToListAsync();
        return Ok(ApiResponse<PagedResult<ProformaInvoice>>.Success(new PagedResult<ProformaInvoice>
        {
            Items = items,
            Total = total,
            Page = query.Page,
            PageSize = query.PageSize
        }));
    }

    /// <summary>查询详情（含明细）</summary>
    [HttpGet("{id:long}")]
    public async Task<IActionResult> GetById(long id)
    {
        var entity = await Set.AsNoTracking().Include(o => o.Details)
            .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
            ?? throw BusinessException.NotFound("形式发票 PI 不存在");
        entity.Details = entity.Details.Where(d => !d.IsDeleted).OrderBy(d => d.SortNo).ToList();
        return Ok(ApiResponse<ProformaInvoice>.Success(entity));
    }

    /// <summary>创建（单号缺省由字轨生成；行号 / 金额 / 合计 / 定金由后端复核）</summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] ProformaInvoice entity)
    {
        entity.Id = 0;
        if (string.IsNullOrWhiteSpace(entity.PiNo))
            entity.PiNo = await _noService.GenerateAsync(DocumentType.ProformaInvoice);
        entity.Status = DocumentStatus.Pending;
        entity.CreatedAt = DateTime.Now;
        Normalize(entity);
        Db.ProformaInvoices.Add(entity);
        await Db.SaveChangesAsync();
        return Ok(ApiResponse<object>.Success(new { entity.Id, entity.PiNo }, "形式发票 PI 创建成功"));
    }

    /// <summary>修改（已审核 / 已转订单 / 已作废不可改；明细整体替换）</summary>
    [HttpPut("{id:long}")]
    public async Task<IActionResult> Update(long id, [FromBody] ProformaInvoice entity)
    {
        var existing = await Db.ProformaInvoices.Include(o => o.Details)
            .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
            ?? throw BusinessException.NotFound("形式发票 PI 不存在");
        if (GetStatus(existing) is DocumentStatus.Approved or DocumentStatus.Cancelled or DocumentStatus.Completed)
            throw BusinessException.RuleConflict("已审核、已转订单或已作废的 PI 不可修改，请先销审");

        var originalDepositRatio = existing.DepositRatio;   // 用于判断定金比例是否被修改

        existing.PiDate = entity.PiDate;
        existing.CustomerId = entity.CustomerId;
        existing.CustomerName = entity.CustomerName;
        existing.ContactPerson = entity.ContactPerson;
        existing.ContactPhone = entity.ContactPhone;
        existing.ContactEmail = entity.ContactEmail;
        existing.Consignee = entity.Consignee;
        existing.NotifyParty = entity.NotifyParty;
        existing.ShippingMarks = entity.ShippingMarks;
        existing.BankInfo = entity.BankInfo;
        existing.TradeTerms = entity.TradeTerms;
        existing.PortOfLoading = entity.PortOfLoading;
        existing.PortOfDestination = entity.PortOfDestination;
        existing.PaymentTerms = entity.PaymentTerms;
        existing.ShippingTerms = entity.ShippingTerms;
        existing.LeadTime = entity.LeadTime;
        existing.Currency = entity.Currency;
        existing.ExchangeRate = entity.ExchangeRate;
        existing.DepositRatio = entity.DepositRatio;
        existing.SalesmanId = entity.SalesmanId;
        existing.SalesmanName = entity.SalesmanName;
        existing.Remark = entity.Remark;

        Db.ProformaInvoiceDetails.RemoveRange(existing.Details);
        entity.PiNo = existing.PiNo;
        entity.QuotationId = existing.QuotationId;
        entity.QuotationNo = existing.QuotationNo;
        entity.Id = id;
        // 定金口径：比例被修改时按新比例重算定金金额；比例不变则保留（允许手改的）原金额
        if (originalDepositRatio != entity.DepositRatio) entity.DepositAmount = 0m;
        Normalize(entity);
        existing.Details = entity.Details;
        existing.TotalAmount = entity.TotalAmount;
        existing.TotalAmountCny = entity.TotalAmountCny;
        existing.DepositAmount = entity.DepositAmount;
        existing.UpdatedAt = DateTime.Now;
        await Db.SaveChangesAsync();
        return Ok(ApiResponse<object>.Success(null, "形式发票 PI 更新成功"));
    }

    /// <summary>审核（草稿可直接审核，也支持提交后审核；无明细不允许审核）</summary>
    [HttpPost("{id:long}/approve")]
    public override async Task<IActionResult> Approve(long id)
    {
        var entity = await Db.ProformaInvoices.Include(o => o.Details)
            .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
            ?? throw BusinessException.NotFound("形式发票 PI 不存在");
        var status = GetStatus(entity);
        if (status == DocumentStatus.Cancelled)
            throw BusinessException.RuleConflict("已作废的 PI 不能审核");
        if (status == DocumentStatus.Completed)
            throw BusinessException.RuleConflict("已转销售订单的 PI 不能审核");
        if (status == DocumentStatus.Approved)
            throw BusinessException.RuleConflict("PI 已审核");
        if (!entity.Details.Any(d => !d.IsDeleted))
            throw BusinessException.RuleConflict("PI 无商品明细，不能审核");

        SetStatus(entity, DocumentStatus.Approved);
        await Db.SaveChangesAsync();
        return Ok(ApiResponse<object>.Success(null, "PI 已审核"));
    }

    /// <summary>销审（退回草稿，可继续修改）</summary>
    [HttpPost("{id:long}/unaudit")]
    public async Task<IActionResult> Unaudit(long id)
    {
        var entity = await GetOrThrowAsync(id, "形式发票 PI 不存在");
        if (GetStatus(entity) != DocumentStatus.Approved)
            throw BusinessException.RuleConflict("仅已审核的 PI 可销审");
        SetStatus(entity, DocumentStatus.Pending);
        await Db.SaveChangesAsync();
        return Ok(ApiResponse<object>.Success(null, "已销审，可继续修改"));
    }

    /// <summary>作废（已转销售订单的 PI 不可作废；重复作废给出明确提示）</summary>
    [HttpPost("{id:long}/void")]
    public async Task<IActionResult> Void(long id)
    {
        var entity = await GetOrThrowAsync(id, "形式发票 PI 不存在");
        var status = GetStatus(entity);
        if (status == DocumentStatus.Cancelled)
            throw BusinessException.RuleConflict("PI 已作废");
        if (status == DocumentStatus.Completed)
            throw BusinessException.RuleConflict("已转销售订单的 PI 不能作废");

        SetStatus(entity, DocumentStatus.Cancelled);
        await Db.SaveChangesAsync();
        return Ok(ApiResponse<object>.Success(null, "PI 已作废"));
    }

    /// <summary>
    /// 带入预填销售订单（ERP-010）：按 PI 返回一张**未落库**的销售订单草稿
    /// （收货人 / 通知人 / 唛头 / 运输条款 / 定金口径随 PI，同时保留 PI 背后的来源报价单），
    /// 前端据此打开「销售订单 → 新增」表单继续编辑后再保存（保存走 <c>POST /api/sales-orders</c>）。
    /// 守卫与 <see cref="ToSalesOrder"/> 一致：只允许已审核 PI，已作废或已生成销售订单均被拒绝；
    /// 本接口不占用单据号、不写库。
    /// </summary>
    [HttpGet("{id:long}/order-prefill")]
    public async Task<IActionResult> OrderPrefill(long id)
    {
        var pi = await Db.ProformaInvoices.AsNoTracking().Include(o => o.Details)
            .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
            ?? throw BusinessException.NotFound("形式发票 PI 不存在");

        var order = await SalesOrderConversion.FromProformaInvoiceAsync(Db, pi);
        return Ok(ApiResponse<SalesOrderPrefillResult>.Success(new SalesOrderPrefillResult
        {
            SourceType = SalesOrderConversion.ProformaInvoiceSourceType,
            SourceId = pi.Id,
            SourceNo = pi.PiNo,
            Order = order
        }, "已按形式发票 PI 带入销售订单草稿"));
    }

    /// <summary>
    /// 转为销售订单（ERP-010）：按已审核 PI 生成一张销售订单（EF 主子表路径，不走旧版存储过程）。
    /// 守卫：同一 PI 仅生成一张（以销售订单的来源字段为准），只新增单据、绝不覆盖既有订单；
    /// 生成后 PI 状态置「已完成」（即「已转销售订单」，与该状态在审核 / 作废处的既有语义一致）。
    /// </summary>
    [HttpPost("{id:long}/to-order")]
    public async Task<IActionResult> ToSalesOrder(long id)
    {
        var pi = await Db.ProformaInvoices.Include(o => o.Details)
            .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
            ?? throw BusinessException.NotFound("形式发票 PI 不存在");

        var order = await SalesOrderConversion.FromProformaInvoiceAsync(Db, pi);
        order.OrderNo = await _noService.GenerateAsync(DocumentType.SalesOrder);
        Db.SalesOrders.Add(order);
        SetStatus(pi, DocumentStatus.Completed);
        await Db.SaveChangesAsync();
        return Ok(ApiResponse<SalesOrderConversionResult>.Success(new SalesOrderConversionResult
        {
            Id = order.Id,
            OrderNo = order.OrderNo,
            SourceNo = pi.PiNo
        }, "已生成销售订单"));
    }

    /// <summary>打印数据（主表 + 明细；打印模板由 /api/sys/print-templates/proforma-invoice 提供）</summary>
    [HttpGet("{id:long}/print")]
    public async Task<IActionResult> GetPrint(long id)
    {
        var entity = await Set.AsNoTracking().Include(o => o.Details)
            .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
            ?? throw BusinessException.NotFound("形式发票 PI 不存在");
        entity.Details = entity.Details.Where(d => !d.IsDeleted).OrderBy(d => d.SortNo).ToList();
        return Ok(ApiResponse<ProformaInvoice>.Success(entity));
    }

    /// <summary>
    /// 行号 / 金额 / 合计 / 定金统一整理（后端复核，防止前端篡改合计）。
    /// 定金口径：比例限制 0~100；金额留 0 时按比例自动计算，允许手工覆盖但不得超过 PI 总额
    /// （修改时若比例发生变化，调用方会先清零金额，使定金按新比例重算）。
    /// </summary>
    public static void Normalize(ProformaInvoice e)
    {
        decimal total = 0;
        var line = 0;
        foreach (var d in e.Details)
        {
            d.Id = 0;
            d.PiId = e.Id;
            d.PiNo = e.PiNo;
            d.SortNo = ++line;
            d.Amount = Math.Round(d.Quantity * d.UnitPrice, 2);
            d.CreatedAt = DateTime.Now;
            total += d.Amount;
        }
        e.TotalAmount = Math.Round(total, 2);
        var rate = e.ExchangeRate == 0 ? 1 : e.ExchangeRate;
        e.TotalAmountCny = Math.Round(e.TotalAmount * rate, 2);
        if (e.PiDate == default) e.PiDate = DateTime.Today;
        if (e.DepositRatio < 0 || e.DepositRatio > 100)
            throw BusinessException.InvalidParameter("定金比例必须在 0~100 之间");

        var depositByRatio = Math.Round(e.TotalAmount * e.DepositRatio / 100m, 2);
        if (e.DepositAmount <= 0) e.DepositAmount = depositByRatio;
        else if (e.DepositAmount > e.TotalAmount)
            throw BusinessException.InvalidParameter("定金金额不能大于 PI 总额");
    }
}


