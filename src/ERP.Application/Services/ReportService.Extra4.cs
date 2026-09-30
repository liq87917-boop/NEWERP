using ERP.Application.Interfaces;
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
}
