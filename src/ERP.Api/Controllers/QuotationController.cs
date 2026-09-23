using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ERP.Api.Controllers;

/// <summary>
/// 报价单控制器（主子表：一张报价单多行商品）
/// 业务链：询价单 Inquiry → **报价单 Quotation** → 形式发票 PI → 销售订单
/// 说明：本单据使用 EF 主子表实现（不走存储过程），不触碰现有单据的 SP
/// </summary>
[Route("api/sales/quotations")]
public class QuotationController : DocumentControllerBase<Quotation>
{
    private readonly IDocumentNumberService _noService;

    public QuotationController(IErpDbContext db, IDocumentNumberService noService) : base(db)
    {
        _noService = noService;
    }

    /// <summary>分页查询（keyword 匹配单号 / 客户名 / 来源询价单号 / 业务员）</summary>
    [HttpGet]
    public async Task<IActionResult> GetPaged([FromQuery] PageQuery query, [FromQuery] DocumentStatus? status,
        [FromQuery] DateTime? start, [FromQuery] DateTime? end)
    {
        query.Normalize();
        var source = Set.AsNoTracking().Where(o => !o.IsDeleted);
        if (status.HasValue) source = source.Where(o => o.Status == status.Value);
        if (start.HasValue) source = source.Where(o => o.QuotationDate >= start.Value);
        if (end.HasValue) source = source.Where(o => o.QuotationDate <= end.Value);
        if (!string.IsNullOrWhiteSpace(query.Keyword))
        {
            var kw = query.Keyword;
            source = source.Where(o => o.QuotationNo.Contains(kw) || o.CustomerName.Contains(kw)
                                       || o.InquiryNo.Contains(kw) || o.SalesmanName.Contains(kw));
        }

        var total = await source.CountAsync();
        var items = await source.OrderByDescending(o => o.Id)
            .Skip((query.Page - 1) * query.PageSize).Take(query.PageSize).ToListAsync();
        return Ok(ApiResponse<PagedResult<Quotation>>.Success(new PagedResult<Quotation>
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
            ?? throw BusinessException.NotFound("报价单不存在");
        entity.Details = entity.Details.Where(d => !d.IsDeleted).OrderBy(d => d.SortNo).ToList();
        return Ok(ApiResponse<Quotation>.Success(entity));
    }

    /// <summary>按询价单带出客户与明细（新建报价单时用「带入询价明细」）</summary>
    [HttpGet("from-inquiry/{inquiryId:long}")]
    public async Task<IActionResult> FromInquiry(long inquiryId)
    {
        var inquiry = await Db.Inquiries.AsNoTracking().Include(o => o.Details)
            .FirstOrDefaultAsync(o => o.Id == inquiryId && !o.IsDeleted)
            ?? throw BusinessException.NotFound("询价单不存在");

        BaseCustomer? customer = null;
        if (inquiry.CustomerId > 0)
            customer = await Db.BaseCustomers.AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == inquiry.CustomerId && !c.IsDeleted);

        var result = new
        {
            inquiry.Id,
            inquiry.InquiryNo,
            inquiry.CustomerId,
            CustomerName = customer?.CustomerName ?? string.Empty,
            ContactPerson = string.IsNullOrWhiteSpace(inquiry.ContactPerson) ? customer?.ContactPerson : inquiry.ContactPerson,
            ContactPhone = string.IsNullOrWhiteSpace(inquiry.ContactPhone) ? customer?.Phone : inquiry.ContactPhone,
            ContactEmail = customer?.Email ?? string.Empty,
            TradeTerms = customer?.TradeTerms ?? string.Empty,
            PortOfDestination = customer?.DestinationPort ?? string.Empty,
            PaymentTerms = customer?.PaymentTerms ?? string.Empty,
            inquiry.Currency,
            inquiry.ExchangeRate,
            inquiry.ValidDays,
            Details = inquiry.Details.Where(d => !d.IsDeleted).OrderBy(d => d.Id).Select((d, i) => new
            {
                SortNo = i + 1,
                d.ProductId,
                d.ProductName,
                d.Spec,
                d.Unit,
                d.Quantity,
                d.UnitPrice,
                d.Amount,
                d.Remark,
                Moq = string.Empty
            }).ToList()
        };
        return Ok(ApiResponse<object>.Success(result));
    }

    /// <summary>审核（草稿可直接审核，也支持提交后审核）</summary>
    [HttpPost("{id:long}/approve")]
    public override async Task<IActionResult> Approve(long id)
    {
        var entity = await GetOrThrowAsync(id, "报价单不存在");
        if (GetStatus(entity) == DocumentStatus.Approved)
            throw BusinessException.RuleConflict("报价单已审核");
        SetStatus(entity, DocumentStatus.Approved);
        entity.Details.Clear();
        await Db.SaveChangesAsync();
        return Ok(ApiResponse<object>.Success(null, "报价单已审核"));
    }

    /// <summary>销审（退回草稿，可继续修改）</summary>
    [HttpPost("{id:long}/unaudit")]
    public async Task<IActionResult> Unaudit(long id)
    {
        var entity = await GetOrThrowAsync(id, "报价单不存在");
        SetStatus(entity, DocumentStatus.Pending);
        await Db.SaveChangesAsync();
        return Ok(ApiResponse<object>.Success(null, "已销审，可继续修改"));
    }

    /// <summary>创建（单号缺省由字轨生成；行号/金额/合计后端复核）</summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] Quotation entity)
    {
        entity.Id = 0;
        if (string.IsNullOrWhiteSpace(entity.QuotationNo))
            entity.QuotationNo = await _noService.GenerateAsync(DocumentType.Quotation);
        entity.Status = DocumentStatus.Pending;
        entity.CreatedAt = DateTime.Now;
        Normalize(entity);
        Db.Quotations.Add(entity);
        await Db.SaveChangesAsync();
        return Ok(ApiResponse<object>.Success(new { entity.Id, entity.QuotationNo }, "报价单创建成功"));
    }

    /// <summary>修改（已审核 / 已取消不可改；明细整体替换）</summary>
    [HttpPut("{id:long}")]
    public async Task<IActionResult> Update(long id, [FromBody] Quotation entity)
    {
        var existing = await Db.Quotations.Include(o => o.Details)
            .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
            ?? throw BusinessException.NotFound("报价单不存在");
        if (GetStatus(existing) is DocumentStatus.Approved or DocumentStatus.Cancelled)
            throw BusinessException.RuleConflict("已审核或已取消的报价单不可修改，请先销审");

        existing.QuotationDate = entity.QuotationDate;
        existing.ValidUntil = entity.ValidUntil;
        existing.CustomerId = entity.CustomerId;
        existing.CustomerName = entity.CustomerName;
        existing.ContactPerson = entity.ContactPerson;
        existing.ContactPhone = entity.ContactPhone;
        existing.ContactEmail = entity.ContactEmail;
        existing.InquiryId = entity.InquiryId;
        existing.InquiryNo = entity.InquiryNo;
        existing.TradeTerms = entity.TradeTerms;
        existing.PortOfLoading = entity.PortOfLoading;
        existing.PortOfDestination = entity.PortOfDestination;
        existing.PaymentTerms = entity.PaymentTerms;
        existing.LeadTime = entity.LeadTime;
        existing.Currency = entity.Currency;
        existing.ExchangeRate = entity.ExchangeRate;
        existing.SalesmanId = entity.SalesmanId;
        existing.SalesmanName = entity.SalesmanName;
        existing.Remark = entity.Remark;

        Db.QuotationDetails.RemoveRange(existing.Details);
        entity.QuotationNo = existing.QuotationNo;
        entity.Id = id;
        Normalize(entity);
        existing.Details = entity.Details;
        existing.TotalAmount = entity.TotalAmount;
        existing.TotalAmountCny = entity.TotalAmountCny;
        existing.UpdatedAt = DateTime.Now;
        await Db.SaveChangesAsync();
        return Ok(ApiResponse<object>.Success(null, "报价单更新成功"));
    }

    /// <summary>转为 PI：复制主表业务字段与明细 / 回填来源报价单 / 原报价单状态改为「已转 PI」（Completed）</summary>
    /// <remarks>
    /// 转换规则（唯一入口，防重复）：
    /// 1) 报价单必须已审核（草稿 / 已提交先审核），已作废与已转 PI 均被拒绝；
    /// 2) 同一报价单只允许生成一张 PI（即使状态被人工改回，也由 PI.QuotationId 兜底拦截）；
    /// 3) 银行信息取系统参数 PI_BankInfo 默认值，收货人 / 通知人 / 唛头取客户资料默认值，PI 上均可再改。
    /// </remarks>
    [HttpPost("{id:long}/to-pi")]
    public async Task<IActionResult> ToProformaInvoice(long id)
    {
        var quotation = await Db.Quotations.Include(o => o.Details)
            .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
            ?? throw BusinessException.NotFound("报价单不存在");

        var status = GetStatus(quotation);
        if (status == DocumentStatus.Cancelled)
            throw BusinessException.RuleConflict("已作废的报价单不能转 PI");
        if (status == DocumentStatus.Completed)
            throw BusinessException.RuleConflict("该报价单已转为 PI，不能重复转换");
        if (status != DocumentStatus.Approved)
            throw BusinessException.RuleConflict("报价单未审核，请先审核后再转 PI");
        if (!quotation.Details.Any(d => !d.IsDeleted))
            throw BusinessException.RuleConflict("报价单无商品明细，不能转 PI");

        var generated = await Db.ProformaInvoices.AsNoTracking()
            .FirstOrDefaultAsync(o => o.QuotationId == id && !o.IsDeleted);
        if (generated is not null)
            throw BusinessException.RuleConflict($"该报价单已转为 PI：{generated.PiNo}");

        BaseCustomer? customer = null;
        if (quotation.CustomerId > 0)
            customer = await Db.BaseCustomers.AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == quotation.CustomerId && !c.IsDeleted);
        var bankInfo = await Db.SysParameters.AsNoTracking()
            .Where(p => !p.IsDeleted && p.ParamKey == "PI_BankInfo")
            .Select(p => p.ParamValue).FirstOrDefaultAsync() ?? string.Empty;

        var pi = new ProformaInvoice
        {
            PiNo = await _noService.GenerateAsync(DocumentType.ProformaInvoice),
            PiDate = DateTime.Today,
            QuotationId = quotation.Id,
            QuotationNo = quotation.QuotationNo,
            CustomerId = quotation.CustomerId,
            CustomerName = quotation.CustomerName,
            ContactPerson = quotation.ContactPerson,
            ContactPhone = quotation.ContactPhone,
            ContactEmail = quotation.ContactEmail,
            Consignee = customer?.Consignee ?? string.Empty,
            NotifyParty = customer?.NotifyParty ?? string.Empty,
            ShippingMarks = customer?.DefaultShippingMark ?? string.Empty,
            BankInfo = bankInfo,
            TradeTerms = quotation.TradeTerms,
            PortOfLoading = quotation.PortOfLoading,
            PortOfDestination = quotation.PortOfDestination,
            PaymentTerms = quotation.PaymentTerms,
            LeadTime = quotation.LeadTime,
            Currency = quotation.Currency,
            ExchangeRate = quotation.ExchangeRate,
            // 定金比例：优先客户资料约定值，未维护时按外贸惯例 30%
            DepositRatio = customer is { DepositRatio: > 0 } ? customer.DepositRatio : 30m,
            SalesmanId = quotation.SalesmanId,
            SalesmanName = quotation.SalesmanName,
            Status = DocumentStatus.Pending,
            Remark = quotation.Remark,
            CreatedAt = DateTime.Now,
            Details = quotation.Details.Where(d => !d.IsDeleted).OrderBy(d => d.SortNo)
                .Select(d => new ProformaInvoiceDetail
                {
                    ProductId = d.ProductId,
                    ProductCode = d.ProductCode,
                    ProductName = d.ProductName,
                    Spec = d.Spec,
                    Unit = d.Unit,
                    Quantity = d.Quantity,
                    UnitPrice = d.UnitPrice,
                    Moq = d.Moq,
                    Remark = d.Remark,
                    CreatedAt = DateTime.Now
                }).ToList()
        };

        ProformaInvoiceController.Normalize(pi);
        Db.ProformaInvoices.Add(pi);
        SetStatus(quotation, DocumentStatus.Completed);   // 报价单 →「已转 PI」
        await Db.SaveChangesAsync();
        return Ok(ApiResponse<object>.Success(new { pi.Id, pi.PiNo, QuotationNo = quotation.QuotationNo },
            "已生成形式发票 PI"));
    }

    /// <summary>打印数据（主表 + 明细；打印模板由 /api/sys/print-templates/quotation 提供）</summary>
    [HttpGet("{id:long}/print")]
    public async Task<IActionResult> GetPrint(long id)
    {
        var entity = await Set.AsNoTracking().Include(o => o.Details)
            .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
            ?? throw BusinessException.NotFound("报价单不存在");
        entity.Details = entity.Details.Where(d => !d.IsDeleted).OrderBy(d => d.SortNo).ToList();
        return Ok(ApiResponse<Quotation>.Success(entity));
    }

    /// <summary>行号 / 金额 / 合计 / 有效期统一整理（后端复核，防止前端篡改合计）</summary>
    private static void Normalize(Quotation e)
    {
        decimal total = 0;
        var line = 0;
        foreach (var d in e.Details)
        {
            d.Id = 0;
            d.QuotationId = e.Id;
            d.QuotationNo = e.QuotationNo;
            d.SortNo = ++line;
            d.Amount = Math.Round(d.Quantity * d.UnitPrice, 2);
            d.CreatedAt = DateTime.Now;
            total += d.Amount;
        }
        e.TotalAmount = Math.Round(total, 2);
        var rate = e.ExchangeRate == 0 ? 1 : e.ExchangeRate;
        e.TotalAmountCny = Math.Round(e.TotalAmount * rate, 2);
        if (e.QuotationDate == default) e.QuotationDate = DateTime.Today;
        e.ValidUntil ??= e.QuotationDate.Date.AddDays(30);
    }
}
