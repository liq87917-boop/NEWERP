using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
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

    /// <summary>
    /// 创建新版本（ERP-035 多轮议价版本留痕）：把既有报价单整单复制为一张新的**草稿**版本。
    /// </summary>
    /// <remarks>
    /// 规则（服务端唯一入口）：
    /// 1) 源版本必须存在、未删除、未作废；源版本一行都不改，创建后即成为只读历史（见 <see cref="EnsureNotSupersededAsync"/>）；
    /// 2) 版本号由服务端分配（链内单调递增，根 V1），单号 = 根单号 + <c>-R版本号</c>；并发创建同一版本号时
    ///    由数据库唯一索引兜底，返回「请重试」而不会产生重复版本号；
    /// 3) 复制可议价的主表字段与明细，合计经 <see cref="QuotationLineRules.Normalize"/> 服务端复算；
    /// 4) **不复制**审核状态（新版本一律草稿）、下游转换状态与已生成单据链接（PI / 销售订单仍指向被显式选中的版本）。
    /// </remarks>
    [HttpPost("{id:long}/revisions")]
    public async Task<IActionResult> CreateRevision(long id)
    {
        var revision = await QuotationRevisionService.CreateRevisionAsync(Db, id);
        return Ok(ApiResponse<QuotationRevisionResult>.Success(new QuotationRevisionResult
        {
            Id = revision.Id,
            QuotationNo = revision.QuotationNo,
            RevisionNumber = revision.RevisionNumber,
            RootQuotationId = revision.RootQuotationId ?? revision.Id,
            RootQuotationNo = revision.RootQuotationNo,
            PreviousRevisionId = revision.PreviousRevisionId ?? 0,
            PreviousRevisionNo = revision.PreviousRevisionNo
        }, $"已创建新版本 {revision.QuotationNo}（草稿，可继续修改）"));
    }

    /// <summary>
    /// 版本链（ERP-035）：返回该报价单所属版本链的**完整**列表（根单 + 全部历史版本，按版本号升序）。
    /// </summary>
    /// <remarks>
    /// 每行含版本号、根单（Id / 单号）、上一版本（Id / 单号）、是否最新版本、是否已被后续版本取代（只读历史）、
    /// 是否已转出（已转 PI / 已转销售订单，口径与成交率报表一致）与状态 / 金额；
    /// 历史报价单（无版本元数据）按初始版本 V1 返回，且不做任何写库 / 回填。
    /// </remarks>
    [HttpGet("{id:long}/revisions")]
    public async Task<IActionResult> GetRevisions(long id)
    {
        var selected = await Set.AsNoTracking()
            .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
            ?? throw BusinessException.NotFound("报价单不存在");
        var chain = await QuotationRevisionService.LoadChainAsync(Db, selected);
        return Ok(ApiResponse<List<QuotationRevisionChainItem>>.Success(chain,
            $"版本链共 {chain.Count} 个版本（根单：{chain.FirstOrDefault()?.RootQuotationNo}）"));
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

    /// <summary>提交（ERP-035：已被后续版本取代的历史版本只读，不允许再流转状态）</summary>
    [HttpPost("{id:long}/submit")]
    public override async Task<IActionResult> Submit(long id)
    {
        await EnsureNotSupersededAsync(id);
        return await base.Submit(id);
    }

    /// <summary>审核（草稿可直接审核，也支持提交后审核；历史版本只读）</summary>
    [HttpPost("{id:long}/approve")]
    public override async Task<IActionResult> Approve(long id)
    {
        await EnsureNotSupersededAsync(id);
        var entity = await GetOrThrowAsync(id, "报价单不存在");
        if (GetStatus(entity) == DocumentStatus.Approved)
            throw BusinessException.RuleConflict("报价单已审核");
        SetStatus(entity, DocumentStatus.Approved);
        entity.Details.Clear();
        await Db.SaveChangesAsync();
        return Ok(ApiResponse<object>.Success(null, "报价单已审核"));
    }

    /// <summary>销审（退回草稿，可继续修改；历史版本只读）</summary>
    [HttpPost("{id:long}/unaudit")]
    public async Task<IActionResult> Unaudit(long id)
    {
        await EnsureNotSupersededAsync(id);
        var entity = await GetOrThrowAsync(id, "报价单不存在");
        SetStatus(entity, DocumentStatus.Pending);
        await Db.SaveChangesAsync();
        return Ok(ApiResponse<object>.Success(null, "已销审，可继续修改"));
    }

    /// <summary>取消（ERP-035：历史版本只读，需在最新版本上操作）</summary>
    [HttpPost("{id:long}/cancel")]
    public override async Task<IActionResult> Cancel(long id)
    {
        await EnsureNotSupersededAsync(id);
        return await base.Cancel(id);
    }

    /// <summary>删除（软删除，仅待提交状态可删；ERP-035：历史版本不可删除）</summary>
    [HttpDelete("{id:long}")]
    public override async Task<IActionResult> Delete(long id)
    {
        await EnsureNotSupersededAsync(id);
        return await base.Delete(id);
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
        /* ERP-035：手工新建的报价单不携带任何版本元数据 → 库默认值即初始版本 V1（不做回填、不写映射） */
        QuotationLineRules.Normalize(entity);
        Db.Quotations.Add(entity);
        await Db.SaveChangesAsync();
        return Ok(ApiResponse<object>.Success(new { entity.Id, entity.QuotationNo }, "报价单创建成功"));
    }

    /// <summary>修改（已审核 / 已取消不可改；ERP-035：已被后续版本取代的历史版本不可改；明细整体替换）</summary>
    [HttpPut("{id:long}")]
    public async Task<IActionResult> Update(long id, [FromBody] Quotation entity)
    {
        await EnsureNotSupersededAsync(id);
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
        QuotationLineRules.Normalize(entity);
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
            throw BusinessException.RuleConflict("该报价单已完成转换（已转 PI 或已转销售订单），不能重复转换");
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

    /// <summary>
    /// 带入预填销售订单（ERP-010）：按报价单返回一张**未落库**的销售订单草稿，
    /// 前端据此打开「销售订单 → 新增」表单继续编辑后再保存（保存走 <c>POST /api/sales-orders</c>，服务端复核数量 / 单价 / 合计）。
    /// 与 <see cref="ToSalesOrder"/> 共用同一套守卫（见 <see cref="SalesOrderConversion.FromQuotationAsync"/>）：
    /// 只允许已审核报价单，已作废 / 已转 PI / 已生成销售订单均被拒绝，本接口不占用单据号、不写库。
    /// </summary>
    [HttpGet("{id:long}/order-prefill")]
    public async Task<IActionResult> OrderPrefill(long id)
    {
        var quotation = await Db.Quotations.AsNoTracking().Include(o => o.Details)
            .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
            ?? throw BusinessException.NotFound("报价单不存在");

        var order = await SalesOrderConversion.FromQuotationAsync(Db, quotation);
        return Ok(ApiResponse<SalesOrderPrefillResult>.Success(new SalesOrderPrefillResult
        {
            SourceType = SalesOrderConversion.QuotationSourceType,
            SourceId = quotation.Id,
            SourceNo = quotation.QuotationNo,
            Order = order
        }, "已按报价单带入销售订单草稿"));
    }

    /// <summary>
    /// 转为销售订单（ERP-010）：按已审核报价单生成一张销售订单（EF 主子表路径，不走旧版存储过程）。
    /// 守卫：同一报价单仅生成一张（以销售订单的来源字段为准，见 <see cref="SalesOrderConversion"/>），
    /// 只新增单据、绝不覆盖既有订单；生成后报价单状态置「已完成」（已转 PI 或已转销售订单）。
    /// </summary>
    [HttpPost("{id:long}/to-order")]
    public async Task<IActionResult> ToSalesOrder(long id)
    {
        var quotation = await Db.Quotations.Include(o => o.Details)
            .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
            ?? throw BusinessException.NotFound("报价单不存在");

        var order = await SalesOrderConversion.FromQuotationAsync(Db, quotation);
        order.OrderNo = await _noService.GenerateAsync(DocumentType.SalesOrder);
        Db.SalesOrders.Add(order);
        SetStatus(quotation, DocumentStatus.Completed);
        await Db.SaveChangesAsync();
        return Ok(ApiResponse<SalesOrderConversionResult>.Success(new SalesOrderConversionResult
        {
            Id = order.Id,
            OrderNo = order.OrderNo,
            SourceNo = quotation.QuotationNo
        }, "已生成销售订单"));
    }

    /// <summary>
    /// 打印数据（主表 + 有效明细，按行号排序）：与「报价单工作流」完全同一份持久化数据
    /// （单号 / 客户 / 条款 / 明细行号 · 数量 · 单价 · 金额 / 合计与折人民币 / 有效期），
    /// 打印预览、直接打印与打印设计共用本端点，服务端**不重算、不落库**，
    /// 保证打印件与页面显示逐字一致；明细只取未删除行并按 `SortNo` 排序，避免软删除行进入打印件。
    /// </summary>
    [HttpGet("{id:long}/print")]
    public async Task<IActionResult> GetPrint(long id)
    {
        var entity = await Set.AsNoTracking().Include(o => o.Details)
            .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
            ?? throw BusinessException.NotFound("报价单不存在");
        entity.Details = entity.Details.Where(d => !d.IsDeleted).OrderBy(d => d.SortNo).ToList();
        return Ok(ApiResponse<Quotation>.Success(entity));
    }

    /// <summary>
    /// 报价单有效期到期提醒（ERP-018）：列出「已过期」与「提醒窗口内即将到期」的报价单，
    /// 已作废单据不提醒；已转出（已转 PI / 已转销售订单）的单据仍会列出但以 <c>Converted</c> 标记，
    /// 便于业务员判断是否还需催单。判定口径见 <see cref="QuotationValidityRules"/>，只读现有
    /// <see cref="Quotation.ValidUntil"/> 字段，**不新增任何数据库结构**。
    /// </summary>
    /// <param name="asOfDate">判定基准日（默认今天）</param>
    /// <param name="aheadDays">提醒窗口天数（默认 7 天；负数按默认值处理）</param>
    [HttpGet("validity-due")]
    public async Task<IActionResult> ValidityDue([FromQuery] DateTime? asOfDate,
        [FromQuery] int aheadDays = QuotationValidityRules.DefaultAheadDays)
    {
        var asOf = (asOfDate ?? DateTime.Today).Date;
        var window = QuotationValidityRules.NormalizeAheadDays(aheadDays);

        var items = await Set.AsNoTracking()
            .Where(o => !o.IsDeleted && o.Status != DocumentStatus.Cancelled
                        && o.ValidUntil != null && o.ValidUntil <= asOf.AddDays(window))
            .ToListAsync();

        var converted = await LoadConvertedQuotationIdsAsync(items.Select(o => o.Id).ToList());

        var result = items
            .OrderByDescending(o => QuotationValidityRules.UrgencyOf(o.ValidUntil, asOf, window))
            .ThenBy(o => o.ValidUntil)
            .ThenBy(o => o.Id)
            .Select(o => new QuotationValidityItem
            {
                Id = o.Id,
                QuotationNo = o.QuotationNo,
                CustomerName = o.CustomerName,
                SalesmanName = o.SalesmanName,
                QuotationDate = o.QuotationDate,
                ValidUntil = o.ValidUntil,
                ValidDays = QuotationValidityRules.DaysRemaining(o.ValidUntil, asOf),
                ValidityStatus = QuotationValidityRules.StatusOf(o.ValidUntil, asOf, window),
                ValidityLevel = QuotationValidityRules.LevelOf(o.ValidUntil, asOf, window),
                TotalAmount = o.TotalAmount,
                TotalAmountCny = o.TotalAmountCny,
                Currency = o.Currency,
                Status = o.Status,
                Converted = converted.Contains(o.Id) || o.Status == DocumentStatus.Completed
            })
            .ToList();

        return Ok(ApiResponse<List<QuotationValidityItem>>.Success(result));
    }

    /// <summary>
    /// 已转出报价单 Id 集合（来源外键：已转 PI / 已转销售订单；口径与成交率报表一致）。
    /// ERP-035 起统一由 <see cref="QuotationRevisionService.LoadConvertedQuotationIdsAsync"/> 提供，
    /// 版本链与有效期提醒共用同一口径（不会因为存在新版本而把转换状态挂到别的版本上）。
    /// </summary>
    private Task<HashSet<long>> LoadConvertedQuotationIdsAsync(List<long> quotationIds)
        => QuotationRevisionService.LoadConvertedQuotationIdsAsync(Db, quotationIds);

    /// <summary>
    /// 历史版本只读守卫（ERP-035）：报价单若已被后续版本取代（链内存在指向它的下一版本）则不允许
    /// 修改 / 提交 / 审核 / 销审 / 取消 / 删除 —— 源版本作为不可变历史原样保留。
    /// </summary>
    private async Task EnsureNotSupersededAsync(long id)
    {
        if (await QuotationRevisionService.IsSupersededAsync(Db, id))
            throw BusinessException.RuleConflict("该报价单已有后续版本，历史版本只读；请在最新版本上继续操作");
    }
}
