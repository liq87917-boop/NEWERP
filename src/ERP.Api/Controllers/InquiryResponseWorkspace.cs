using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace ERP.Api.Controllers;

/// <summary>
/// 询价报价响应时间工作台控制器（ERP-104）：把已持久化的询价单与报价单按「显式 <c>InquiryId</c> 链接 + 版本链根单」只读呈现，
/// 计算每张询价单到首张有效报价的间隔日历天，并提供按询价日期区间 / 客户筛选的有界分页工作台。
/// <para>审计口径：报价单上持久化的 <c>InquiryId</c> 是链接的唯一权威依据；本控制器<strong>不新建任何表、不新增任何列、不执行任何写操作</strong>，
/// 只按显式字段读取（全库查询均为 <c>AsNoTracking</c>，无 Add / Update / Remove / SaveChanges），不改写询价单 / 报价单 / 客户主数据。</para>
/// <para>边界：链接绝不按单号 / 文本 / 金额 / 相似度推断；只取版本链根单（<c>RootQuotationId == null</c>），版本不重复计入；
/// 软删除行一律排除；缺失链接 / 缺失日期 / 报价日期早于询价日期分别作为独立证据标注，绝不推断为已报价或成交。</para>
/// </summary>
[ApiController]
[Route("api/inquiries/response-times")]
[Authorize]
public class InquiryResponseWorkspaceController : ControllerBase
{
    private readonly IErpDbContext _db;

    public InquiryResponseWorkspaceController(IErpDbContext db)
    {
        _db = db;
    }

    /// <summary>当前登录用户 Id（缺失或非数字时返回 null，由数据范围解析 fail closed 拒绝，绝不猜测身份）</summary>
    private long? CurrentUserId()
        => long.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var id) ? id : null;

    /// <summary>
    /// 询价报价响应时间工作台（分页，只读）：按询价日期区间（含）/ 可选客户 / 关键字筛选，应用既有业务员数据范围，
    /// 按稳定询价单 Id 分页；单页内批量装载客户名称与显式链接的根单报价日期，不产生逐行数据库访问。
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetPaged([FromQuery] InquiryResponseQuery query)
    {
        query.Normalize();
        var scope = await SalespersonDataScopeService.ResolveAsync(_db, CurrentUserId());

        // 业务员数据范围是硬边界：先按客户范围过滤，再做其它显式筛选
        var source = SalespersonDataScopeService.FilterByCustomer(
            _db.Inquiries.AsNoTracking().Where(o => !o.IsDeleted), scope, o => o.CustomerId);

        if (query.StartDate.HasValue)
            source = source.Where(o => o.InquiryDate.Date >= query.StartDate.Value.Date);
        if (query.EndDate.HasValue)
            source = source.Where(o => o.InquiryDate.Date <= query.EndDate.Value.Date);

        var customerId = InquiryResponseRules.NormalizeCustomerIdFilter(query.CustomerId);
        if (customerId.HasValue)
            source = source.Where(o => o.CustomerId == customerId.Value);
        if (!string.IsNullOrWhiteSpace(query.Keyword))
            source = source.Where(o => o.InquiryNo.Contains(query.Keyword));

        var total = await source.CountAsync();
        var inquiries = await source
            .OrderBy(o => o.Id)   // 稳定询价单 Id 分页
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .ToListAsync();

        // 有界批量装载：本页询价单的客户名称 + 显式链接的根单报价日期（一次批量查询，无逐行查库）
        var inquiryIds = inquiries.Select(i => i.Id).ToList();
        var customerIds = inquiries.Select(i => i.CustomerId).Distinct().ToList();

        var customerNames = await _db.BaseCustomers.AsNoTracking()
            .Where(c => customerIds.Contains(c.Id))
            .Select(c => new { c.Id, c.CustomerName })
            .ToListAsync();

        var quotations = await _db.Quotations.AsNoTracking()
            .Where(q => !q.IsDeleted
                && q.RootQuotationId == null          // 只取版本链根单（初始版本），版本不重复计入
                && q.InquiryId.HasValue
                && inquiryIds.Contains(q.InquiryId.Value))
            .Select(q => new { q.InquiryId, q.QuotationNo, q.QuotationDate })
            .ToListAsync();

        var quotationsByInquiry = quotations
            .GroupBy(q => q.InquiryId!.Value)
            .ToDictionary(g => g.Key, g => g.Select(q => (q.QuotationNo, q.QuotationDate)).ToList());

        var nameById = customerNames.ToDictionary(c => c.Id, c => c.CustomerName);

        var rows = inquiries.Select(i =>
        {
            quotationsByInquiry.TryGetValue(i.Id, out var linked);
            return InquiryResponseRules.BuildRow(
                i.Id, i.InquiryNo, i.InquiryDate, i.CustomerId,
                nameById.TryGetValue(i.CustomerId, out var name) ? name : InquiryResponseRules.UnknownText,
                i.SalesmanId, i.Status, linked ?? new List<(string QuotationNo, DateTime QuotationDate)>());
        }).ToList();

        var workspace = new InquiryResponseWorkspaceDto(
            rows, total, query.Page, query.PageSize,
            InquiryResponseRules.ReadOnlyText,
            InquiryResponseRules.BoundaryText,
            InquiryResponseRules.DisclaimerText);

        return Ok(ApiResponse<InquiryResponseWorkspaceDto>.Success(workspace));
    }
}
