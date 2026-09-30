using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace ERP.Application.Services;

/// <summary>
/// 扩展报表实现（四）：跟进提醒
/// 用于业务员每日回访清单：筛出「下次跟进日期」已到期或即将到期（默认未来 7 天）的客户跟进记录。
/// </summary>
public partial class ReportService
{
    /// <summary>跟进提醒（已逾期排最前；先按当前账号业务员数据范围过滤，再读取与排序）</summary>
    public async Task<List<ReportDtos.FollowUpDueItem>> GetFollowUpDueAsync(
        DateTime asOfDate, int aheadDays, SalespersonDataScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);

        var limit = asOfDate.Date.AddDays(aheadDays);

        var source = _db.CustomerFollowUps
            .Where(x => !x.IsDeleted && x.NextFollowDate != null && x.NextFollowDate <= limit);

        // 业务员数据范围（ERP-097，唯一权威口径）：特权账号不过滤；受限制业务员仅其被分配客户，
        // 无客户 Id 的记录对受限制账号不可见（fail closed，绝不返回范围外客户或匿名记录）。
        var list = await SalespersonDataScopeService
            .FilterByCustomer(source, scope, x => x.CustomerId)
            .ToListAsync();

        return list
            .Select(x =>
            {
                var next = (x.NextFollowDate ?? asOfDate).Date;
                var diff = (asOfDate.Date - next).Days;
                return new ReportDtos.FollowUpDueItem
                {
                    CustomerName = x.CustomerName,
                    SalesmanName = x.SalesmanName,
                    FollowDate = x.FollowDate,
                    Result = x.Result,
                    NextFollowDate = next,
                    DueDays = diff,
                    DueStatus = diff > 0 ? "已逾期" : (diff == 0 ? "今日到期" : "即将到期"),
                    Subject = x.Subject
                };
            })
            .OrderByDescending(x => x.DueDays)      // 逾期最久的排最前
            .ThenBy(x => x.CustomerName)
            .ToList();
    }

    /// <summary>
    /// 动态跟进提醒报表预览（ERP-193，只读派生）：先按 fail closed 校验字段 / 到期状态 / 提前天数 / 分页，
    /// 再按当前账号业务员数据范围（ERP-097 唯一权威口径）在数据库端过滤、计数、稳定排序与分页，
    /// 最后只投影选定字段；<c>Total</c> 为范围内记录总数（分页前），<see cref="DynamicFollowUpDueReportPageDto.Truncated"/>
    /// 表示本页之外仍有更多记录，空页显式给出 <see cref="DynamicFollowUpDueReportPageDto.EmptyText"/>。
    /// <para>全程只读：无 Add / Update / Remove / SaveChanges，不执行任意 SQL、不写库。</para>
    /// </summary>
    public async Task<DynamicFollowUpDueReportPageDto> GetDynamicFollowUpDueReportAsync(
        DynamicFollowUpDueReportRequest request, SalespersonDataScope scope)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(scope);

        // 1) 全部校验先于任何跟进记录读取（fail closed）
        var fieldKeys = DynamicFollowUpDueReportRules.NormalizeFields(request.Fields);
        var dueStatus = DynamicFollowUpDueReportRules.NormalizeDueStatus(request.DueStatus);
        DynamicFollowUpDueReportRules.ValidateAheadDays(request.AheadDays);
        DynamicFollowUpDueReportRules.ValidatePageBounds(request.Page, request.PageSize);
        var asOfDate = DynamicFollowUpDueReportRules.NormalizeAsOfDate(request.AsOfDate);
        var limit = asOfDate.AddDays(request.AheadDays);

        // 2) 数据库端：数据范围 → 到期状态 → 计数 → 稳定排序 → 分页（全部在物化之前完成）
        var source = _db.CustomerFollowUps
            .AsNoTracking()
            .Where(x => !x.IsDeleted && x.NextFollowDate != null && x.NextFollowDate <= limit);

        var scoped = SalespersonDataScopeService.FilterByCustomer(source, scope, x => x.CustomerId);
        var filtered = ApplyDueStatus(scoped, dueStatus, asOfDate);

        var total = await filtered.CountAsync();

        var items = await filtered
            .OrderBy(x => x.NextFollowDate)   // 逾期最久（下次跟进日期最早）排最前
            .ThenBy(x => x.CustomerName)
            .ThenBy(x => x.Id)
            .Skip((request.Page - 1) * request.PageSize)
            .Take(request.PageSize)
            .ToListAsync();

        // 3) 只投影选定字段（列与行都保持请求顺序）
        var columns = fieldKeys
            .Select(k => DynamicFollowUpDueReportRules.GetField(k)!)
            .ToList();
        var rows = items
            .Select(x => DynamicFollowUpDueReportRules.BuildRow(x, fieldKeys, asOfDate))
            .ToList();

        var totalPages = total == 0 ? 0 : (int)Math.Ceiling(total / (double)request.PageSize);
        var truncated = (request.Page - 1) * request.PageSize + rows.Count < total;

        return new DynamicFollowUpDueReportPageDto(
            columns,
            rows,
            total,
            request.Page,
            request.PageSize,
            totalPages,
            truncated,
            rows.Count == 0 ? DynamicFollowUpDueReportRules.EmptyText : string.Empty,
            DynamicFollowUpDueReportRules.ReadOnlyText,
            DynamicFollowUpDueReportRules.BoundaryText,
            DynamicFollowUpDueReportRules.DisclaimerText);
    }

    /// <summary>把规范化到期状态映射为数据库端 <c>NextFollowDate</c> 与 as-of 日期的比较（全部可翻译为 SQL）</summary>
    private static IQueryable<CustomerFollowUp> ApplyDueStatus(
        IQueryable<CustomerFollowUp> source, string? dueStatus, DateTime asOfDate)
    {
        if (dueStatus is null)
            return source;

        var day = asOfDate.Date;
        return dueStatus switch
        {
            DynamicFollowUpDueReportRules.DueOverdue => source.Where(x => x.NextFollowDate!.Value < day),
            DynamicFollowUpDueReportRules.DueToday =>
                source.Where(x => x.NextFollowDate!.Value >= day && x.NextFollowDate!.Value < day.AddDays(1)),
            DynamicFollowUpDueReportRules.DueUpcoming => source.Where(x => x.NextFollowDate!.Value >= day.AddDays(1)),
            _ => source,
        };
    }
}
