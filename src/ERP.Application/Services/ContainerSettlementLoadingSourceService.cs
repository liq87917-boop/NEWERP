using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ERP.Application.Services;

/// <summary>
/// 装柜结算单来源装柜清单候选 / 已存储来源展示（ERP-392，只读、有界、分页）。
/// <para><b>精确定位（绝不按文本猜测）</b>：候选只返回<b>当前精确客户</b>确实是权威归属客户的装柜清单——
/// 即该客户是 ERP-041 的<b>有效（启用、未删除）参与方客户之一</b>，或该清单没有任何有效参与方时兼容客户字段
/// <see cref="ContainerLoadingList.CustomerId"/> 恰好等于该客户；绝不按柜号 / 单号 / 相似度猜测链接，
/// 也不提供按猜测 Id 直取候选 / 来源的旁路。</para>
/// <para><b>共享柜 / 上游共享出运 fail closed</b>：复用 <see cref="LoadingListAuthorizationRules.ApplyScope"/>
/// 把客户数据范围<b>下推到 SQL 侧、计数与分页之前</b>，共享柜的每一个有效参与方客户与显式上游（预装柜单 → 订柜信息）
/// 客户都必须在范围内，否则整张清单不返回（不通过共享柜泄露其他客户）。</para>
/// <para><b>先归一化再计数</b>：关键字去首尾空白并截断到 <see cref="MaxKeywordLength"/>，页码 / 每页条数按
/// <see cref="NormalizePage"/> / <see cref="NormalizePageSize"/> 收敛后再 <c>CountAsync</c> 与分页，绝不无界拉取。</para>
/// <para><b>候选不是授权</b>：本类只做<b>有界只读投影</b>，不落库、不改单据 / 库存 / 流水、不消耗单据号、
/// 不新增表 / 列 / 菜单 / 权限或用户授权；装柜结算单没有币种字段，因此绝不返回金额、汇率或跨币种合计，
/// 也绝不发明任何金额公式；显式选择后的最终保存仍由调用方按
/// <see cref="FinanceContainerSettlementLifecycleRules.ResolveAndAuthorizeLoadingListAsync"/>（ERP-384 锁内复核）
/// 精确解析来源并复核客户资格。</para>
/// </summary>
public static class ContainerSettlementLoadingSourceService
{
    /// <summary>候选查询默认每页条数（有界）</summary>
    public const int DefaultPageSize = 20;

    /// <summary>候选查询每页条数上限（有界，绝不无界拉取）</summary>
    public const int MaxPageSize = 100;

    /// <summary>候选查询页码上限（有界，防止越界深分页）</summary>
    public const int MaxPage = 10000;

    /// <summary>关键字长度上限（超长截断，避免无界匹配）</summary>
    public const int MaxKeywordLength = 100;

    /// <summary>缺少精确客户的拒绝文案（绝不返回任意客户清单）</summary>
    public const string ExactCustomerRequiredText =
        "必须提供精确的客户 Id（正整数）才能查询来源装柜清单候选：拒绝返回任意客户清单（fail closed）";

    /// <summary>候选口径文案（接口 / 文档同源）</summary>
    public const string CandidateRuleText =
        "来源装柜清单候选只返回当前账号客户数据范围之内、未删除、未取消，且当前精确客户确实是权威归属客户"
        + "（有效参与方之一，或历史单客户兼容客户字段）的装柜清单；共享柜的全部有效参与方与显式上游客户都必须在范围内，"
        + "否则整张清单不返回；关键字 / 分页参数先归一化再计数，只返回有界字段（绝不返回金额 / 汇率 / 跨币种合计），"
        + "绝不返回任意客户清单，也不接受按猜测 Id 直取；候选选择不等于授权，"
        + "最终保存仍由装柜结算单生命周期规则在锁内复核精确来源（不新增表 / 列 / 菜单 / 权限或用户授权）。";

    /// <summary>已存储来源展示口径文案（接口 / 文档同源）</summary>
    public const string StoredSourceRuleText =
        "已存储来源按装柜结算单权威客户做实时身份 / 菜单 / 客户数据范围复核后显式标注"
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
    /// 有界只读候选查询：返回<b>精确客户</b>名下「未删除、未取消」的装柜清单候选页（只含该客户确实是权威归属客户的清单）。
    /// <para>客户数据范围（<see cref="LoadingListAuthorizationRules.ApplyScope"/>）与精确客户成员资格
    /// （<see cref="LoadingListAuthorizationRules.ApplyAuthoritativeCustomerMembership"/>）都在 <c>CountAsync</c> 之前下推；
    /// 关键字 / 页码 / 每页条数先归一化；排序按 Id 倒序保证确定性。</para>
    /// </summary>
    public static async Task<ContainerSettlementLoadingListCandidatePageDto> QueryCandidatesAsync(
        IErpDbContext db, SalespersonDataScope scope, long customerId, string? keyword,
        int page, int pageSize, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(scope);

        if (customerId <= 0)
            throw BusinessException.InvalidParameter(ExactCustomerRequiredText);

        var normalizedKeyword = NormalizeKeyword(keyword);
        var normalizedPage = NormalizePage(page);
        var normalizedPageSize = NormalizePageSize(pageSize);

        var source = db.ContainerLoadingLists.AsNoTracking()
            .Where(l => !l.IsDeleted && l.Status != DocumentStatus.Cancelled);

        // 归属：精确客户必须是有效参与方，或历史单客户兼容字段客户（绝不按柜号 / 单号猜测）。
        source = LoadingListAuthorizationRules.ApplyAuthoritativeCustomerMembership(source, db, customerId);
        // 范围：共享柜的全部有效参与方与显式上游客户都必须在当前账号范围内（计数 / 分页之前下推）。
        source = LoadingListAuthorizationRules.ApplyScope(source, db, scope);

        if (normalizedKeyword.Length > 0)
            source = source.Where(l => l.LoadingListNo.Contains(normalizedKeyword)
                                       || l.ContainerNo.Contains(normalizedKeyword));

        var total = await source.CountAsync(ct);
        var rows = await source.OrderByDescending(l => l.Id)
            .Skip((normalizedPage - 1) * normalizedPageSize)
            .Take(normalizedPageSize)
            .Select(l => new { l.Id, l.LoadingListNo, l.LoadingDate, l.ContainerNo, l.Status, l.PreLoadingId })
            .ToListAsync(ct);

        var customerName = await db.BaseCustomers.AsNoTracking()
            .Where(c => c.Id == customerId && !c.IsDeleted)
            .Select(c => c.CustomerName)
            .FirstOrDefaultAsync(ct) ?? string.Empty;

        var pageIds = rows.Select(r => r.Id).ToList();
        var participantRows = pageIds.Count == 0
            ? new List<(long LoadingListId, int Count)>()
            : (await db.ContainerLoadingListParticipants.AsNoTracking()
                    .Where(p => pageIds.Contains(p.LoadingListId)
                                && !p.IsDeleted
                                && p.Status == ContainerLoadingParticipantRules.ActiveStatus)
                    .GroupBy(p => p.LoadingListId)
                    .Select(g => new { LoadingListId = g.Key, Count = g.Count() })
                    .ToListAsync(ct))
                .Select(x => (x.LoadingListId, x.Count))
                .ToList();
        var participantCounts = participantRows.ToDictionary(x => x.LoadingListId, x => x.Count);

        var preLoadingIds = rows.Where(r => r.PreLoadingId is > 0).Select(r => r.PreLoadingId!.Value)
            .Distinct().ToList();
        var upstreamCustomers = preLoadingIds.Count == 0
            ? new Dictionary<long, long>()
            : (await (from pre in db.ContainerPreLoadings.AsNoTracking()
                      join b in db.ContainerBookings.AsNoTracking() on pre.BookingId equals (long?)b.Id
                      where preLoadingIds.Contains(pre.Id) && !pre.IsDeleted && !b.IsDeleted
                      select new { pre.Id, b.CustomerId })
                    .ToListAsync(ct))
                .GroupBy(x => x.Id)
                .ToDictionary(g => g.Key, g => g.First().CustomerId);

        var items = rows.Select(r =>
        {
            var activeParticipantCount = participantCounts.GetValueOrDefault(r.Id);
            return new ContainerSettlementLoadingListCandidateDto
            {
                LoadingListId = r.Id,
                LoadingListNo = r.LoadingListNo,
                LoadingDate = r.LoadingDate,
                ContainerNo = r.ContainerNo,
                CustomerId = customerId,
                CustomerName = customerName,
                Status = r.Status.ToString(),
                Eligible = true,
                IneligibleReason = string.Empty,
                ActiveParticipantCount = activeParticipantCount,
                LegacySingleCustomer = activeParticipantCount == 0,
                PreLoadingId = r.PreLoadingId is > 0 ? r.PreLoadingId : null,
                UpstreamBookingCustomerId = r.PreLoadingId is > 0
                    && upstreamCustomers.TryGetValue(r.PreLoadingId!.Value, out var upstream)
                    ? upstream
                    : null
            };
        }).ToList();

        return new ContainerSettlementLoadingListCandidatePageDto
        {
            Items = items,
            Total = total,
            Page = normalizedPage,
            PageSize = normalizedPageSize
        };
    }

    /// <summary>
    /// 已存储来源的只读展示（详情 / 重开）：未关联（历史）→ 显式「未关联」；已关联且来源有效 → 已关联 + 单号；
    /// 来源已取消 → 已关联 + 「来源已取消，只读保留」；来源已删除 / 无法解析 → 「来源不可用，原链接原样保留」。
    /// <para>历史已取消 / 已删除来源绝不抛异常、绝不写库、绝不静默清除 / 重绑定；
    /// 绝不因柜号 / 单号文本、金额或相似度猜测来源。</para>
    /// </summary>
    public static async Task<ContainerSettlementLoadingSourceViewDto> DescribeStoredSourceAsync(
        IErpDbContext db, ContainerLoadingList? loadingList, long? storedLoadingListId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);

        if (storedLoadingListId is not > 0)
            return new ContainerSettlementLoadingSourceViewDto
            {
                Linked = false,
                Annotation = FinanceContainerSettlementLifecycleRules.StoredSourceUnlinkedText
            };

        var resolved = loadingList;
        if (resolved is null || resolved.Id != storedLoadingListId.Value)
        {
            resolved = await db.ContainerLoadingLists.AsNoTracking()
                .FirstOrDefaultAsync(l => l.Id == storedLoadingListId.Value, ct);
        }

        if (resolved is null)
            return new ContainerSettlementLoadingSourceViewDto
            {
                LoadingListId = storedLoadingListId.Value,
                Linked = true,
                Unavailable = true,
                EligibleForNewLink = false,
                Annotation = FinanceContainerSettlementLifecycleRules.StoredSourceUnavailableText
            };

        if (resolved.IsDeleted)
            return new ContainerSettlementLoadingSourceViewDto
            {
                LoadingListId = storedLoadingListId.Value,
                LoadingListNo = resolved.LoadingListNo,
                LoadingDate = resolved.LoadingDate,
                ContainerNo = resolved.ContainerNo,
                Status = resolved.Status.ToString(),
                Linked = true,
                Unavailable = true,
                EligibleForNewLink = false,
                Annotation = FinanceContainerSettlementLifecycleRules.StoredSourceUnavailableText
            };

        var cancelled = resolved.Status == DocumentStatus.Cancelled;
        return new ContainerSettlementLoadingSourceViewDto
        {
            LoadingListId = storedLoadingListId.Value,
            LoadingListNo = resolved.LoadingListNo,
            LoadingDate = resolved.LoadingDate,
            ContainerNo = resolved.ContainerNo,
            Status = resolved.Status.ToString(),
            Linked = true,
            Cancelled = cancelled,
            Unavailable = false,
            EligibleForNewLink = !cancelled,
            Annotation = cancelled
                ? FinanceContainerSettlementLifecycleRules.StoredSourceCancelledText
                : FinanceContainerSettlementLifecycleRules.StoredSourceLinkedText
        };
    }
}
