using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ERP.Application.Services;

/// <summary>
/// 扩展报表实现（三）：业务员提成表（ERP-243，只读派生）
/// <para>口径：仅统计已审核、未删除、当前账号数据范围内的销售订单头，按「持久化业务员桶 × 原始原币」分组。
/// 已知币种按签名订单头 <c>TotalAmount</c> 小计；未知 / 无效币种保留原始键、金额为 null 仅计数。
/// 利润 / 利润率 / 提成额恒为未知（null）：当前商品售价 / 成本价不能证明历史可比较成本 / 利润 / 提成，
/// 因此本报表不再读取销售订单明细与当前商品成本价，绝不从当前成本派生或推断为 0。</para>
/// <para>系统参数 <c>SalesCommissionRate</c> 仅作为「当前参考比例」（可空）：至多读取 2 条未删除记录，
/// 恰好一条不变量普通十进制 0..100（含显式 0）才可知；缺失 / 重复 / 非法 / 负数 / 超范围一律未知，绝不回退为 0。</para>
/// </summary>
public partial class ReportService
{
    /// <summary>业务员提成表允许的日期区间最大跨度（含首尾日历日）：366 天</summary>
    private const int SalesCommissionMaxDateRangeDays = 366;

    /// <summary>业务员提成表订单头读取上限（范围内已审核销售订单）：500 张</summary>
    private const int SalesCommissionMaxOrders = 500;

    /// <summary>
    /// 业务员提成表（只读派生，ERP-243）：日期校验（含结束日溢出防护）先于任何源读取；
    /// 订单头在业务员数据范围之后稳定排序做 501 行探测（500 张上限），超出即 fail closed 且不返回任何行或金额；
    /// 业务员姓名按已限定范围内 Id 一次固定批量查询（未删除，无逐单 / 逐行查库，绝不做员工权威 / 广域目录读取）；
    /// 系统参数 <c>SalesCommissionRate</c> 至多读取 2 条未删除记录并解析当前参考比例（可空）。
    /// </summary>
    public async Task<List<ReportDtos.SalesCommissionItem>> GetSalesCommissionAsync(
        DateTime start, DateTime end, SalespersonDataScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);

        // 1) 日期校验先于任何源读取（fail closed，含结束日溢出防护）
        var (startDate, endDate) = ValidateSalesCommissionDateRange(start, end);
        var endExclusive = endDate.AddDays(1);

        // 2) 订单头：已审核、未删除、日期窗口、业务员数据范围；稳定排序后做 501 行探测（500 张上限）
        var ordersQuery = _db.SalesOrders
            .Where(o => !o.IsDeleted
                        && o.Status == DocumentStatus.Approved
                        && o.OrderDate >= startDate
                        && o.OrderDate < endExclusive);
        ordersQuery = SalespersonDataScopeService.FilterByCustomer(ordersQuery, scope, o => o.CustomerId);

        var orders = await ordersQuery
            .OrderBy(o => o.SalesmanId)
            .ThenBy(o => o.Id)
            .Take(SalesCommissionMaxOrders + 1)
            .ToListAsync();

        ThrowIfSalesCommissionOverflow(orders.Count);

        return await BuildSalesCommissionItemsAsync(orders);
    }

    /// <summary>业务员提成表日期窗口校验（含结束日溢出防护，先于任何源读取）。</summary>
    private static (DateTime Start, DateTime End) ValidateSalesCommissionDateRange(DateTime start, DateTime end)
    {
        var startDate = start.Date;
        var endDate = end.Date;
        if (endDate < startDate)
            throw new BusinessException("业务员提成表的结束日期不能早于开始日期", ErrorCodes.InvalidParameter);

        var inclusiveDays = (endDate - startDate).Days + 1;
        if (inclusiveDays > SalesCommissionMaxDateRangeDays)
            throw new BusinessException(
                $"业务员提成表的日期范围最多 {SalesCommissionMaxDateRangeDays} 天（含首尾）",
                ErrorCodes.InvalidParameter);

        if (endDate == DateTime.MaxValue.Date)
            throw new BusinessException("业务员提成表的结束日期无效（结束日次日溢出）", ErrorCodes.InvalidParameter);

        return (startDate, endDate);
    }

    /// <summary>来源上限探测：超出 500 张即 fail closed（不返回任何行或金额）。</summary>
    private static void ThrowIfSalesCommissionOverflow(int count)
    {
        if (count > SalesCommissionMaxOrders)
            throw new BusinessException(
                $"业务员提成表超出报告上限：范围内已审核销售订单超过 {SalesCommissionMaxOrders} 张"
                + "（fail closed，不返回任何行或金额）",
                ErrorCodes.RuleConflict);
    }

    /// <summary>
    /// 按「持久化业务员桶 × 原始原币」分组（未指定业务员与缺失 / 已删除员工刻意区分）；
    /// 金额仅已知币种签名小计、未知币种 null；利润 / 利润率 / 提成额恒为未知（null）。
    /// 业务员姓名按已限定范围内 Id 一次固定批量查询；系统参数 SalesCommissionRate 至多读取 2 条未删除记录。
    /// </summary>
    private async Task<List<ReportDtos.SalesCommissionItem>> BuildSalesCommissionItemsAsync(
        IReadOnlyList<SalesOrder> orders)
    {
        var salesmanIds = orders
            .Where(o => o.SalesmanId is > 0)
            .Select(o => o.SalesmanId!.Value)
            .Distinct()
            .ToList();
        var employees = await _db.BaseEmployees
            .Where(e => salesmanIds.Contains(e.Id) && !e.IsDeleted)
            .ToDictionaryAsync(e => e.Id, e => e.EmployeeName);

        var rateValues = await _db.SysParameters
            .Where(p => !p.IsDeleted && p.ParamKey == SalesCommissionEvidenceRules.SalesCommissionRateKey)
            .OrderBy(p => p.Id)
            .Select(p => p.ParamValue)
            .Take(2)
            .ToListAsync();
        var (rate, rateReason) = SalesCommissionEvidenceRules.ParseRate(rateValues);

        var result = new List<ReportDtos.SalesCommissionItem>();
        foreach (var salesmanGroup in orders.GroupBy(o => o.SalesmanId is > 0 ? o.SalesmanId : null))
        {
            var bucket = salesmanGroup.Key;
            var nameKnown = bucket.HasValue
                            && employees.TryGetValue(bucket.Value, out var name)
                            && !string.IsNullOrWhiteSpace(name);
            var salesmanName = bucket.HasValue
                ? (nameKnown ? employees[bucket.Value] : SalesCommissionEvidenceRules.UnknownSalesmanName)
                : SalesCommissionEvidenceRules.UnassignedSalesmanName;
            var identityEvidence = bucket.HasValue
                ? (nameKnown ? string.Empty : SalesCommissionEvidenceRules.UnknownSalesmanIdentityEvidence)
                : SalesCommissionEvidenceRules.UnassignedSalesmanIdentityEvidence;

            foreach (var currencyGroup in salesmanGroup
                         .GroupBy(o => SalesCommissionEvidenceRules.CurrencyGroupKey(o.Currency))
                         .OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                var currencyKey = currencyGroup.Key;
                var isKnown = SalesCommissionEvidenceRules.IsKnownCurrency(currencyGroup.First().Currency);
                result.Add(new ReportDtos.SalesCommissionItem
                {
                    SalesmanId = bucket,
                    SalesmanName = salesmanName,
                    SalesmanIdentityEvidence = identityEvidence,
                    Currency = currencyKey,
                    CurrencyLabel = SalesCommissionEvidenceRules.CurrencyGroupLabel(currencyKey),
                    OrderCount = currencyGroup.Count(),
                    SalesAmount = isKnown ? (decimal?)currencyGroup.Sum(o => o.TotalAmount) : null,
                    AmountLabel = SalesCommissionEvidenceRules.AmountLabel,
                    CurrencyEvidence = isKnown
                        ? SalesCommissionEvidenceRules.KnownCurrencyEvidence
                        : SalesCommissionEvidenceRules.UnknownCurrencyEvidence,
                    Profit = null,
                    ProfitEvidence = SalesCommissionEvidenceRules.ProfitEvidence,
                    ProfitRate = null,
                    ProfitRateEvidence = SalesCommissionEvidenceRules.ProfitRateEvidence,
                    CommissionRate = rate,
                    CommissionRateEvidence = string.IsNullOrEmpty(rateReason)
                        ? SalesCommissionEvidenceRules.CommissionRateEvidence
                        : rateReason,
                    CommissionAmount = null,
                    CommissionEvidence = SalesCommissionEvidenceRules.CommissionEvidence,
                    SourceLabel = SalesCommissionEvidenceRules.SourceLabel
                });
            }
        }

        return result
            .OrderBy(x => x.SalesmanId)
            .ThenBy(x => x.Currency, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// 动态业务员提成证据报表预览（ERP-244，只读派生）：先校验字段 / 日期 / 分页 / 可选筛选，再按业务员数据范围过滤，
    /// 应用客户 / 业务员 / 币种谓词（作用于 Take(501) 之前），稳定排序后探测来源上限，最后分组并投影选定列。
    /// </summary>
    public async Task<DynamicSalesCommissionReportPageDto> GetDynamicSalesCommissionReportAsync(
        DynamicSalesCommissionReportRequest request, SalespersonDataScope scope)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(scope);

        // 1) 全部校验先于任何源读取（fail closed）
        var fieldKeys = DynamicSalesCommissionReportRules.NormalizeFields(request.Fields);
        var (startDate, endDate) = DynamicSalesCommissionReportRules.ValidateDateRange(request.Start, request.End);
        DynamicSalesCommissionReportRules.ValidatePageBounds(request.Page, request.PageSize);
        var filter = DynamicSalesCommissionReportRules.NormalizeFilter(request.Filter);

        var endExclusive = endDate.AddDays(1);

        // 2) 订单头：已审核、未删除、日期窗口、业务员数据范围
        var ordersQuery = _db.SalesOrders
            .Where(o => !o.IsDeleted
                        && o.Status == DocumentStatus.Approved
                        && o.OrderDate >= startDate
                        && o.OrderDate < endExclusive);
        ordersQuery = SalespersonDataScopeService.FilterByCustomer(ordersQuery, scope, o => o.CustomerId);

        // 3) 可选筛选与范围 / 来源状态 / 日期谓词相交，作用在 Take(501) 之前（非物化后）
        ordersQuery = ApplySalesCommissionFilter(ordersQuery, filter);

        // 4) 稳定排序 + 501 行探测（500 张上限，超出 fail closed）
        var orders = await ordersQuery
            .OrderBy(o => o.SalesmanId)
            .ThenBy(o => o.Id)
            .Take(SalesCommissionMaxOrders + 1)
            .ToListAsync();

        ThrowIfSalesCommissionOverflow(orders.Count);

        // 5) 复用 ERP-243 的「持久化业务员桶 × 原始原币」分组
        var items = await BuildSalesCommissionItemsAsync(orders);
        var filterText = DynamicSalesCommissionReportRules.BuildFilterContext(filter);

        return DynamicSalesCommissionReportRules.BuildPage(
            items, fieldKeys, request.Page, request.PageSize, startDate, endDate, filterText);
    }

    /// <summary>
    /// 应用 ERP-244 可选筛选（在业务员数据范围之后、501 订单头上限探测之前）：客户 Id / 业务员 Id / 业务员姓名关键字 / 原币币种精确匹配。
    /// 全部为参数化 EF 谓词，非任意 SQL。业务员 Id 仅订单属性（非权限边界），不会扩展客户范围，也不会在聚合后再筛选。
    /// <para>姓名关键字使用对持久化 <c>SalesmanId</c> 的关联 <c>BaseEmployees.Any</c> 谓词（未删除员工 + 字面
    /// <c>EmployeeName.Contains</c>），在范围 / 客户 / 业务员 / 币种谓词之后、501 订单头上限探测之前相交；
    /// 不建立单独员工目录 / 客户端身份权威、不产生逐单 / 逐行额外员工读取。<c>%</c> / <c>_</c> 按字面文本匹配。</para>
    /// </summary>
    private IQueryable<SalesOrder> ApplySalesCommissionFilter(IQueryable<SalesOrder> source, SalesCommissionFilterDto? filter)
    {
        if (filter is null)
            return source;

        if (filter.CustomerId is > 0)
            source = source.Where(o => o.CustomerId == filter.CustomerId.Value);

        if (filter.SalesmanId is > 0)
            source = source.Where(o => o.SalesmanId == filter.SalesmanId.Value);

        if (!string.IsNullOrEmpty(filter.SalesmanName))
        {
            var keyword = filter.SalesmanName;
            source = source.Where(o => o.SalesmanId != null
                && _db.BaseEmployees.Any(e => e.Id == o.SalesmanId
                                              && !e.IsDeleted
                                              && e.EmployeeName.Contains(keyword)));
        }

        if (!string.IsNullOrEmpty(filter.Currency)
            && Enum.TryParse<Currency>(filter.Currency, true, out var currency))
        {
            source = source.Where(o => o.Currency == currency);
        }

        return source;
    }
}
