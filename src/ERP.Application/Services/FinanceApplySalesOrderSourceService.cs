using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace ERP.Application.Services;

/// <summary>
/// 财务申请单（定金申请单）来源销售订单候选 / 已存储来源展示（ERP-390，只读、有界、分页）。
/// <para><b>精确定位</b>：候选只按<b>精确客户 Id</b>（正整数，否则参数错误）返回该客户名下「未删除」的销售订单，
/// 绝不返回任意客户订单，也绝不提供按猜测 Id 直取候选 / 来源的旁路；已取消订单显式标记
/// <c>Eligible = false</c> + 原因，币种与申请单归一化币种不一致同样显式标记不可选（历史已记录的链接只读保留，
/// 绝不静默重绑定）。</para>
/// <para><b>先归一化再计数</b>：关键字去首尾空白并截断到 <see cref="MaxKeywordLength"/>，页码 / 每页条数按
/// <see cref="NormalizePage"/> / <see cref="NormalizePageSize"/> 收敛后再 <c>CountAsync</c> 与分页，绝不无界拉取。</para>
/// <para><b>候选不是授权</b>：本类只做<b>有界只读投影</b>，不落库、不改单据 / 库存 / 流水、不消耗单据号、
/// 不新增表 / 列 / 菜单 / 权限或用户授权；显式选择后的最终保存仍由调用方按
/// <see cref="FinanceDepositApplyLifecycleRules"/> 的生命周期规则（实时身份 + 既有菜单 + 客户数据范围 +
/// 按 Id 精确解析未删除 / 未取消 / 同客户 / 同币种来源）复核。</para>
/// </summary>
public static class FinanceApplySalesOrderSourceService
{
    /// <summary>候选查询默认每页条数（有界）</summary>
    public const int DefaultPageSize = 20;

    /// <summary>候选查询每页条数上限（有界，绝不无界拉取）</summary>
    public const int MaxPageSize = 100;

    /// <summary>候选查询页码上限（有界，防止越界深分页）</summary>
    public const int MaxPage = 10000;

    /// <summary>关键字长度上限（超长截断，避免无界匹配）</summary>
    public const int MaxKeywordLength = 100;

    /// <summary>缺少精确客户的拒绝文案（绝不返回任意客户订单）</summary>
    public const string ExactCustomerRequiredText =
        "必须提供精确的客户 Id（正整数）才能查询来源销售订单候选：拒绝返回任意客户订单（fail closed）";

    /// <summary>候选币种与申请单币种不一致时的不可选原因文案（不做汇率换算）</summary>
    public const string CurrencyMismatchCandidateText =
        "销售订单币种与申请单币种不一致：不能作为定金申请单来源（历史已记录的链接按显式状态只读保留，系统不做汇率换算）";

    /// <summary>候选口径文案（接口 / 文档同源）</summary>
    public const string CandidateRuleText =
        "来源销售订单候选只返回当前账号客户数据范围之内、按精确客户 Id 匹配「未删除」的销售订单"
        + "（已取消或币种不一致显式标记不可选），关键字 / 分页参数先归一化再计数；只返回有界字段，"
        + "绝不返回任意客户订单，也不接受按猜测 Id 直取；候选选择不等于授权，"
        + "最终保存仍按定金申请单生命周期规则复核精确来源（不新增表 / 列 / 菜单 / 权限或用户授权）。";

    /// <summary>已存储来源展示口径文案（接口 / 文档同源）</summary>
    public const string StoredSourceRuleText =
        "已存储来源按定金申请单权威客户做实时身份 / 菜单 / 客户数据范围复核后显式标注"
        + "（未关联 / 已关联 / 来源已取消 / 来源不可用）；历史已取消 / 不可用来源原样保留、只读可读，"
        + "绝不静默清除或重绑定。";

    /// <summary>关键字归一化：去首尾空白并截断到 <see cref="MaxKeywordLength"/>（先归一化再计数 / 匹配）。</summary>
    public static string NormalizeKeyword(string? keyword)
    {
        var text = (keyword ?? string.Empty).Trim();
        return text.Length <= MaxKeywordLength ? text : text[..MaxKeywordLength];
    }

    /// <summary>页码归一化：小于 1 取 1，超过 <see cref="MaxPage"/> 收敛到上限（有界）。</summary>
    public static int NormalizePage(int page)
        => page < 1 ? 1 : (page > MaxPage ? MaxPage : page);

    /// <summary>每页条数归一化：小于 1 取默认值，超过 <see cref="MaxPageSize"/> 收敛到上限（有界）。</summary>
    public static int NormalizePageSize(int pageSize)
        => pageSize < 1 ? DefaultPageSize : (pageSize > MaxPageSize ? MaxPageSize : pageSize);

    /// <summary>
    /// 有界只读候选查询：返回<b>精确客户</b>名下「未删除」销售订单的候选页（含已取消 / 币种不一致行与不可选原因）。
    /// <para>关键字 / 页码 / 每页条数都会先归一化，再统计总数与取当页；排序按 Id 倒序保证确定性。
    /// <paramref name="normalizedCurrency"/> 为 <c>null</c> 时只按客户返回（币种仅作展示），
    /// 非空时按既有币种口径精确比对决定可选性（调用方已用生命周期规则归一化，本方法不再重复校验币种范围）。</para>
    /// </summary>
    public static async Task<FinanceApplySalesOrderCandidatePageDto> QueryCandidatesAsync(
        IErpDbContext db, long customerId, string? normalizedCurrency, string? keyword, int page, int pageSize,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (customerId <= 0)
            throw BusinessException.InvalidParameter(ExactCustomerRequiredText);

        // 归一化必须先于任何计数 / 取数。
        var kw = NormalizeKeyword(keyword);
        var normalizedPage = NormalizePage(page);
        var normalizedSize = NormalizePageSize(pageSize);

        var source = db.SalesOrders.AsNoTracking()
            .Where(o => !o.IsDeleted && o.CustomerId == customerId);
        if (kw.Length > 0)
        {
            source = source.Where(o => o.OrderNo.Contains(kw)
                || o.CustomerPoNo.Contains(kw)
                || o.ContractNo.Contains(kw));
        }

        var total = await source.CountAsync(ct);
        var orders = await source
            .OrderByDescending(o => o.Id)
            .Skip((normalizedPage - 1) * normalizedSize)
            .Take(normalizedSize)
            .Select(o => new { o.Id, o.OrderNo, o.OrderDate, o.CustomerId, o.Currency, o.Status })
            .ToListAsync(ct);

        var customerName = await db.BaseCustomers.AsNoTracking()
            .Where(c => c.Id == customerId && !c.IsDeleted)
            .Select(c => c.CustomerName)
            .FirstOrDefaultAsync(ct) ?? string.Empty;

        var items = orders.Select(o =>
        {
            var orderCurrency = CurrencyAmountRules.NormalizeCurrency(o.Currency.ToString());
            var stateEligible = FinanceDepositApplyLifecycleRules.IsEligibleNewSource(o.Status);
            var currencyMatches = normalizedCurrency is null
                || string.Equals(orderCurrency, normalizedCurrency, StringComparison.Ordinal);
            var eligible = stateEligible && currencyMatches;
            return new FinanceApplySalesOrderCandidateDto
            {
                SalesOrderId = o.Id,
                OrderNo = o.OrderNo,
                OrderDate = o.OrderDate,
                CustomerId = o.CustomerId,
                CustomerName = customerName,
                Currency = orderCurrency,
                Status = o.Status.ToString(),
                Eligible = eligible,
                IneligibleReason = eligible
                    ? string.Empty
                    : (stateEligible
                        ? CurrencyMismatchCandidateText
                        : FinanceDepositApplyLifecycleRules.SourceCancelledCandidateText)
            };
        }).ToList();

        return new FinanceApplySalesOrderCandidatePageDto
        {
            Items = items,
            Total = total,
            Page = normalizedPage,
            PageSize = normalizedSize
        };
    }

    /// <summary>
    /// 已存储来源的只读展示：复用 <see cref="FinanceDepositApplyLifecycleRules.DescribeStoredSourceAsync"/>
    /// 的显式状态文案，并补充结构化的订单号 / 日期 / 币种 / 状态 / 可用性，供表单重开与详情原样回显。
    /// <para>历史已取消 / 已删除来源绝不抛异常、绝不写库、绝不静默清除 / 重绑定。</para>
    /// </summary>
    public static async Task<FinanceApplySalesOrderSourceViewDto> DescribeStoredSourceAsync(
        IErpDbContext db, FinanceDepositApply apply, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(apply);

        var annotation = await FinanceDepositApplyLifecycleRules.DescribeStoredSourceAsync(db, apply, ct);
        if (apply.SalesOrderId is null or <= 0)
            return new FinanceApplySalesOrderSourceViewDto { Linked = false, Annotation = annotation };

        var order = await db.SalesOrders.AsNoTracking()
            .Where(o => o.Id == apply.SalesOrderId.Value && !o.IsDeleted)
            .Select(o => new { o.OrderNo, o.OrderDate, o.Currency, o.Status })
            .FirstOrDefaultAsync(ct);

        if (order is null)
        {
            return new FinanceApplySalesOrderSourceViewDto
            {
                SalesOrderId = apply.SalesOrderId.Value,
                Linked = true,
                Unavailable = true,
                EligibleForNewLink = false,
                Annotation = annotation
            };
        }

        var orderCurrency = CurrencyAmountRules.NormalizeCurrency(order.Currency.ToString());
        var applyCurrency = CurrencyAmountRules.NormalizeCurrency(apply.Currency.ToString());
        var currencyMatches = string.Equals(orderCurrency, applyCurrency, StringComparison.Ordinal);

        return new FinanceApplySalesOrderSourceViewDto
        {
            SalesOrderId = apply.SalesOrderId.Value,
            OrderNo = order.OrderNo,
            OrderDate = order.OrderDate,
            Currency = orderCurrency,
            Status = order.Status.ToString(),
            Linked = true,
            Unavailable = false,
            EligibleForNewLink = FinanceDepositApplyLifecycleRules.IsEligibleNewSource(order.Status)
                && currencyMatches,
            Annotation = annotation
        };
    }
}
