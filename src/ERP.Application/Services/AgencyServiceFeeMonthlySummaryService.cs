using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using System.Linq.Expressions;

namespace ERP.Application.Services;

/// <summary>
/// 代理服务费对账单**月度汇总**（ERP-110，只读派生）：把未删除的 ERP-070 对账单证据按
/// 「对账日期所属年月 + 客户 + 原币」分组，仅未删除且已登记的对账单计入原币合计，草稿与已作废
/// 单独计数；服务期间跨越多个月的对账单全额计入其对账日期所属月份，不按期间分摊。
/// <para>读取口径（关键）：分组键（年月 + 客户 + 币种）与分页在**一次**去重查询内完成，
/// 本页对账单再**一次**批量装载（固定次数数据集访问，与对账单张数 / 行数无关，无逐行查库）。</para>
/// <para>边界：本服务只读，不新增 / 不修改任何表与列、不写库、不迁移、不回填；也不改写对账单证据、
/// 协议、客户、销售订单、装柜清单、单证、发票、收款、库存、费用与结算记录，不开票、不记账、
/// 不收款或付款、不催收或联系客户、不调用任何外部服务。</para>
/// </summary>
public static class AgencyServiceFeeMonthlySummaryService
{
    /// <summary>分组键（只读派生内部使用）：对账日期所属年月 + 客户 + 原币。</summary>
    private sealed record MonthGroupKey(int Year, int Month, long CustomerId, string Currency);

    /// <summary>
    /// 月度汇总报表（只读派生，分页有界）：筛选（对账日期区间 / 客户 / 币种，全部只用持久化字段）→
    /// 去重得到「年月 + 客户 + 币种」分组键 → 稳定分页 → 一次性批量装载本页对账单 → 内存聚合。
    /// </summary>
    public static async Task<AgencyServiceFeeMonthlySummaryView> ForQueryAsync(
        IErpDbContext db, AgencyServiceFeeMonthlySummaryQuery query, SalespersonDataScope scope)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(scope);

        var (from, to) = AgencyServiceFeeMonthlySummaryRules.NormalizeDateRange(
            query.StatementDateFrom, query.StatementDateTo);
        var customerId = AgencyServiceFeeMonthlySummaryRules.NormalizeCustomerFilter(query.CustomerId);
        var currency = AgencyServiceFeeMonthlySummaryRules.NormalizeCurrencyFilter(query.Currency);
        var (page, pageSize) = AgencyServiceFeeMonthlySummaryRules.NormalizePaging(query.Page, query.PageSize);

        // 0) 当前账号业务员数据范围（ERP-097，唯一权威口径）：特权账号不过滤，受限制业务员仅其被分配客户。
        //    同一范围同时作用于「分组 / 计数」与「本页行装载」两组查询，保证合计、页数与金额不会泄露范围外客户。
        var scopedSource = SalespersonDataScopeService.FilterByCustomer(
            db.AgencyServiceFeeStatements.AsNoTracking().Where(s => !s.IsDeleted), scope, s => s.CustomerId);

        // 1) 去重得到「年月 + 客户 + 币种」分组键（只用持久化字段，固定一次数据集访问）
        var distinctKeys = scopedSource
            .Where(s => customerId == null || s.CustomerId == customerId.Value)
            .Where(s => currency == null || s.Currency == currency)
            .Where(s => from == null || s.StatementDate >= from.Value)
            .Where(s => to == null || s.StatementDate <= to.Value)
            .Select(s => new { s.StatementDate.Year, s.StatementDate.Month, s.CustomerId, s.Currency })
            .Distinct();

        var total = await distinctKeys.CountAsync();

        var pageKeys = (await distinctKeys
                .OrderBy(k => k.Year)
                .ThenBy(k => k.Month)
                .ThenBy(k => k.CustomerId)
                .ThenBy(k => k.Currency)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync())
            .Select(k => new MonthGroupKey(k.Year, k.Month, k.CustomerId, k.Currency))
            .ToList();

        // 2) 一次性批量装载本页分组内的对账单（只命中本页分组键，无逐行查库）
        var pageStatements = new List<AgencyServiceFeeStatement>();
        if (pageKeys.Count > 0)
        {
            pageStatements = await SalespersonDataScopeService.FilterByCustomer(
                    db.AgencyServiceFeeStatements.AsNoTracking().Where(s => !s.IsDeleted), scope, s => s.CustomerId)
                .Where(BuildPageKeyPredicate(pageKeys))
                .OrderBy(s => s.StatementDate)
                .ThenBy(s => s.Id)
                .ToListAsync();
        }

        var lookup = pageStatements.ToLookup(
            s => new MonthGroupKey(s.StatementDate.Year, s.StatementDate.Month, s.CustomerId, s.Currency));

        var rows = pageKeys.Select(key => BuildRow(key, lookup[key].ToList())).ToList();

        var totalPages = pageSize <= 0 ? 0 : (int)Math.Ceiling(total / (double)pageSize);

        return new AgencyServiceFeeMonthlySummaryView
        {
            StatementDateFrom = from,
            StatementDateTo = to,
            CustomerId = customerId,
            Currency = currency ?? string.Empty,
            Total = total,
            Page = page,
            PageSize = pageSize,
            TotalPages = totalPages,
            Truncated = (page - 1) * pageSize + rows.Count < total,
            GroupCount = rows.Count,
            Rows = rows,
            EmptyText = total == 0
                ? "没有符合筛选条件的代理服务费对账单证据（或已被软删除；证据数字不代表收入或应收）"
                : string.Empty,
        };
    }

    /// <summary>按分组键聚合一个「年月 + 客户 + 币种」分组（纯内存计算，不写库）</summary>
    private static AgencyServiceFeeMonthlySummaryRow BuildRow(
        MonthGroupKey key, IReadOnlyList<AgencyServiceFeeStatement> statements)
    {
        var currency = key.Currency;
        var decimals = CurrencyAmountRules.PrecisionOf(currency);

        var registered = statements
            .Where(s => s.Status == AgencyServiceFeeStatementRules.StatusRecorded)
            .ToList();
        var drafts = statements
            .Where(s => s.Status == AgencyServiceFeeStatementRules.StatusDraft)
            .ToList();
        var voided = statements
            .Where(s => s.Status == AgencyServiceFeeStatementRules.StatusVoided)
            .ToList();

        var registeredAmount = registered.Sum(s => s.TotalAmount);
        var draftAmount = drafts.Sum(s => s.TotalAmount);
        var voidedAmount = voided.Sum(s => s.TotalAmount);

        var customerCode = statements
            .Select(s => s.CustomerCode)
            .FirstOrDefault(c => !string.IsNullOrWhiteSpace(c)) ?? string.Empty;
        var customerName = statements
            .Select(s => s.CustomerName)
            .FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)) ?? string.Empty;

        return new AgencyServiceFeeMonthlySummaryRow
        {
            StatementYear = key.Year,
            StatementMonth = key.Month,
            StatementMonthText = AgencyServiceFeeMonthlySummaryRules.MonthText(key.Year, key.Month),
            CustomerId = key.CustomerId,
            CustomerCode = customerCode,
            CustomerName = customerName,
            Currency = currency,
            AmountDecimals = decimals,
            RegisteredCount = registered.Count,
            RegisteredTotalAmount = registeredAmount,
            RegisteredTotalAmountText = AgencyServiceFeeStatementRules.AmountText(registeredAmount, currency),
            DraftCount = drafts.Count,
            DraftTotalAmount = draftAmount,
            DraftTotalAmountText = AgencyServiceFeeStatementRules.AmountText(draftAmount, currency),
            VoidedCount = voided.Count,
            VoidedTotalAmount = voidedAmount,
            VoidedTotalAmountText = AgencyServiceFeeStatementRules.AmountText(voidedAmount, currency),
            StatementCount = statements.Count,
        };
    }

    /// <summary>
    /// 构建「本页分组键」的批量命中谓词：<c>(年 = y 且 月 = m 且 客户 = c 且 币种 = cur) 或 ...</c>。
    /// 全部用持久化字段的等值比较，可直接翻译为数据库端 WHERE，无需逐行查询。
    /// </summary>
    private static Expression<Func<AgencyServiceFeeStatement, bool>> BuildPageKeyPredicate(
        IReadOnlyList<MonthGroupKey> keys)
    {
        var parameter = Expression.Parameter(typeof(AgencyServiceFeeStatement), "s");
        var statementDate = Expression.Property(parameter, nameof(AgencyServiceFeeStatement.StatementDate));
        var year = Expression.Property(statementDate, nameof(DateTime.Year));
        var month = Expression.Property(statementDate, nameof(DateTime.Month));
        var customerId = Expression.Property(parameter, nameof(AgencyServiceFeeStatement.CustomerId));
        var currency = Expression.Property(parameter, nameof(AgencyServiceFeeStatement.Currency));

        Expression? body = null;
        foreach (var key in keys)
        {
            var match = Expression.AndAlso(
                Expression.AndAlso(
                    Expression.AndAlso(
                        Expression.Equal(year, Expression.Constant(key.Year)),
                        Expression.Equal(month, Expression.Constant(key.Month))),
                    Expression.Equal(customerId, Expression.Constant(key.CustomerId))),
                Expression.Equal(currency, Expression.Constant(key.Currency)));

            body = body is null ? match : Expression.OrElse(body, match);
        }

        return Expression.Lambda<Func<AgencyServiceFeeStatement, bool>>(
            body ?? Expression.Constant(false), parameter);
    }
}
