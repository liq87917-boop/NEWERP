using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ERP.Application.Services;

/// <summary>
/// 扩展报表实现（五 · ERP-018）：报价成交率分析。
/// </summary>
/// <remarks>
/// 计算口径（唯一权威定义，文档同步见 <c>docs/报价单与PI设计方案.md</c> §10.3）：
/// <list type="number">
/// <item>统计范围：`Quotations` 中 <c>QuotationDate</c> 落在 [start, end]、未软删除的报价单。</item>
/// <item>分母「有效报价数」= 上述范围内 **未作废**（状态 ≠ 已取消）的报价单数；已作废单只计入 <c>CancelledCount</c>，不参与成交率。</item>
/// <item>分子「已转出数」= 分母中满足任一条件者：存在未删除的 <c>ProformaInvoices.QuotationId</c>（已转 PI）、
///     存在未删除的 <c>SalesOrders.SourceQuotationId</c>（已转销售订单）、或报价单状态为「已完成」。
///     以来源外键为主、状态为兜底，保证历史数据（外键回填前的状态变更）也能正确归类。</item>
/// <item>成交率 = 已转出数 ÷ 有效报价数 × 100，保留 2 位小数；分母为 0 时按 0 处理（避免除零）。</item>
/// <item>已过期未成交数：有效期（<c>ValidUntil</c>）早于**报表期间结束日**且未转出的报价单数，口径与「有效期提醒」一致。</item>
/// <item>金额均为报价单原币金额（`TotalAmount`）合计，不做汇率折算，避免期间内汇率波动影响可比性；不同币种分列，绝不跨币种合计。</item>
/// <item>按业务员 × 原币聚合（未填写业务员归入「未指定业务员」；空值 / 未知币种归入「未知币种」），按成交率降序、报价数降序、业务员升序、币种升序排列。</item>
/// </list>
/// 本报表只读现有表，**不新增任何数据库结构**。
/// </remarks>
public partial class ReportService
{
    /// <summary>未指定业务员时的分组名称</summary>
    private const string NoSalesmanGroup = "未指定业务员";

    /// <summary>空值 / 未知币种时的分组名称（绝不默认币种或推断汇率）</summary>
    public const string UnknownCurrencyGroup = "未知币种";

    /// <summary>允许的报价日期区间最大跨度（含首尾日历日）：366 天</summary>
    private const int MaxDateRangeDays = 366;

    /// <summary>单次报表允许物化的报价单上限（当前账号范围内）</summary>
    private const int MaxScopedQuotations = 2000;

    /// <summary>为检测超限额外多读一条（2001）</summary>
    private const int ScopedQuotationReadLimit = MaxScopedQuotations + 1;

    /// <summary>PI / 销售订单转换链接查询的分批大小（远低于 SQL Server 2100 参数上限）</summary>
    private const int LinkBatchSize = 1000;

    /// <summary>报价成交率分析（按业务员 × 原币聚合；显式传入当前账号业务员数据范围）</summary>
    public async Task<List<ReportDtos.QuotationConversionItem>> GetQuotationConversionAsync(
        DateTime start, DateTime end, SalespersonDataScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);

        // 1) 日期校验先于任何报价单读取（fail closed；含首尾日历日最多 366 天）
        var startDate = start.Date;
        var endDate = end.Date;
        if (endDate < startDate)
            throw new BusinessException("报价成交率报表的结束日期不能早于开始日期", ErrorCodes.InvalidParameter);

        var inclusiveDays = (endDate - startDate).Days + 1;
        if (inclusiveDays > MaxDateRangeDays)
            throw new BusinessException($"报价成交率报表的日期范围最多 {MaxDateRangeDays} 天（含首尾）", ErrorCodes.InvalidParameter);

        var endExclusive = endDate.AddDays(1);

        // 2) 只在当前账号业务员数据范围内读取报价单；受限制业务员仅其被分配客户，空客户不可见（fail closed）。
        var source = _db.Quotations.AsNoTracking()
            .Where(q => !q.IsDeleted && q.QuotationDate >= startDate && q.QuotationDate < endExclusive);
        source = SalespersonDataScopeService.FilterByCustomer(source, scope, q => q.CustomerId);

        var quotations = await source.Take(ScopedQuotationReadLimit).ToListAsync();
        if (quotations.Count > MaxScopedQuotations)
        {
            throw new BusinessException(
                $"报价成交率报表在所选期间内的报价单超过 {MaxScopedQuotations} 张，请缩小日期范围后再查询",
                ErrorCodes.InvalidParameter);
        }
        if (quotations.Count == 0) return new List<ReportDtos.QuotationConversionItem>();

        var ids = quotations.Select(q => q.Id).ToList();

        // 3) 已转 PI / 已转销售订单：只查 scoped 报价单 Id，分批避免 SQL Server 参数上限。
        var piQuotationIds = new List<long>();
        var orderQuotationIds = new List<long>();
        foreach (var batch in ids.Chunk(LinkBatchSize))
        {
            var batchList = batch.ToList();

            // 已转 PI：以报价单外键为准（转 PI 时写入，见 QuotationController.ToProformaInvoice）
            piQuotationIds.AddRange(await _db.ProformaInvoices.AsNoTracking()
                .Where(p => !p.IsDeleted && p.QuotationId != null && batchList.Contains(p.QuotationId.Value))
                .Select(p => p.QuotationId!.Value)
                .ToListAsync());

            // 已转销售订单：以销售订单来源字段为准（ERP-010 / ERP-008 来源留痕）
            orderQuotationIds.AddRange(await _db.SalesOrders.AsNoTracking()
                .Where(o => !o.IsDeleted && o.SourceQuotationId != null && batchList.Contains(o.SourceQuotationId.Value))
                .Select(o => o.SourceQuotationId!.Value)
                .ToListAsync());
        }

        var convertedIds = new HashSet<long>(piQuotationIds);
        foreach (var id in orderQuotationIds) convertedIds.Add(id);

        var result = new List<ReportDtos.QuotationConversionItem>();
        foreach (var group in quotations.GroupBy(q => (Salesman: GroupKey(q), Currency: CurrencyGroupKey(q))))
        {
            var all = group.ToList();
            var active = all.Where(q => q.Status != DocumentStatus.Cancelled).ToList();
            var converted = active.Where(q => IsConverted(q, convertedIds)).ToList();

            var quotationCount = active.Count;
            var convertedCount = converted.Count;
            var convertedAmount = converted.Sum(q => q.TotalAmount);

            result.Add(new ReportDtos.QuotationConversionItem
            {
                SalesmanName = group.Key.Salesman,
                Currency = group.Key.Currency,
                QuotationCount = quotationCount,
                ConvertedCount = convertedCount,
                ConversionRate = quotationCount == 0 ? 0m : Math.Round(convertedCount * 100m / quotationCount, 2),
                ExpiredCount = active.Count(q => !IsConverted(q, convertedIds)
                                                  && QuotationValidityRules.StatusOf(q.ValidUntil, endDate) == QuotationValidityRules.ExpiredText),
                CancelledCount = all.Count(q => q.Status == DocumentStatus.Cancelled),
                TotalAmount = active.Sum(q => q.TotalAmount),
                ConvertedAmount = convertedAmount,
                AvgConvertedAmount = convertedCount == 0 ? 0m : Math.Round(convertedAmount / convertedCount, 2),
            });
        }

        return result
            .OrderByDescending(r => r.ConversionRate)
            .ThenByDescending(r => r.QuotationCount)
            .ThenBy(r => r.SalesmanName, StringComparer.Ordinal)
            .ThenBy(r => r.Currency, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>分组键：业务员姓名为空时归入「未指定业务员」</summary>
    private static string GroupKey(Domain.Entities.Quotation quotation)
        => string.IsNullOrWhiteSpace(quotation.SalesmanName) ? NoSalesmanGroup : quotation.SalesmanName.Trim();

    /// <summary>分组键：原币币种规范化（空值 / 未知取值归入「未知币种」）</summary>
    private static string CurrencyGroupKey(Domain.Entities.Quotation quotation)
        => NormalizeCurrencyCode(quotation.Currency.ToString());

    /// <summary>
    /// 币种分组键规范化：去首尾空白并大写；空值或不在 <see cref="Currency"/> 枚举内的取值一律归入
    /// <see cref="UnknownCurrencyGroup"/>（绝不默认币种、绝不推断汇率）。
    /// </summary>
    public static string NormalizeCurrencyCode(string? currency)
    {
        var value = (currency ?? string.Empty).Trim().ToUpperInvariant();
        if (value.Length == 0) return UnknownCurrencyGroup;
        return Enum.TryParse<Currency>(value, true, out var parsed) && Enum.IsDefined(parsed)
            ? parsed.ToString()
            : UnknownCurrencyGroup;
    }

    /// <summary>是否已转出：来源外键（PI / 销售订单）优先，报价单状态「已完成」兜底</summary>
    private static bool IsConverted(Domain.Entities.Quotation quotation, HashSet<long> convertedIds)
        => convertedIds.Contains(quotation.Id) || quotation.Status == DocumentStatus.Completed;
}
