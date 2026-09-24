using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace ERP.Application.Services;

/// <summary>
/// 装柜出运证据时间线与跟踪工作台服务（ERP-059，**纯只读**）。职责：
/// <list type="number">
/// <item><b>按显式源记录读取时间线</b>（<see cref="GetForSourceAsync"/>）：订柜信息 / 预装柜单 / 装柜清单的详情入口
/// 只按「源记录类型 + 源记录 Id」这一持久化链接取当前有效出运引用，再把 ERP-057 计划值与 ERP-058 里程碑证据
/// 合成为**计划 / 实际分开标注**的时间线；没有有效引用时显式返回「未关联」而不是按柜号 / S/O / B/L 猜测；</item>
/// <item><b>按显式出运引用读取时间线</b>（<see cref="GetForReferenceAsync"/>）：工作台行点击进入的只读详情
/// （含已作废历史视图与时间差算术证据）；</item>
/// <item><b>有界分页工作台</b>（<see cref="ListAsync"/>）：只按显式字段（柜号 / 单号 / B/L / S/O / 港口 /
/// 计划时间 / 记录事件）筛选，单页内批量装载里程碑汇总与源记录可用性（固定数量查询，无逐行查库）。</item>
/// </list>
/// <para>审计口径：ERP-057 是出运引用证据的**唯一权威**登记册、ERP-058 是里程碑证据的**唯一**登记册；
/// 本服务**不新建任何表、不新增任何列**，只读呈现。</para>
/// <para>边界（重要）：本服务全程只读 —— 不写任何表（没有任何新增 / 修改 / 删除实体或落库调用），
/// 不改写订柜信息 / 预装柜单 / 装柜清单 / 出运引用 / 里程碑的任何列、状态与工作流，不推进任何业务单据，
/// 不改写订单、库存、单证、发票、费用与分摊、收付款、税务与结算记录，也不轮询承运人、海关、货代或任何外部系统；
/// 不把缺失事件推断为已开船 / 已到港 / 已清关 / 延误 / 逾期，也不做任何时区换算。</para>
/// </summary>
public static class ContainerShipmentTimelineService
{
    // ==================== 1. 按显式源记录读取时间线（装柜三单详情入口） ====================

    /// <summary>
    /// 读取某条**显式源记录**（订柜信息 / 预装柜单 / 装柜清单）的出运证据时间线。
    /// <para>只按「源记录类型 + 源记录 Id」取当前**有效**出运引用（ERP-057 唯一性口径）；
    /// 没有有效引用时返回「未关联」详情（证据全部显示「未知 / 无」），
    /// <strong>不</strong>按柜号 / S/O / B/L 等自由文本兜底挑选引用，也<strong>不</strong>会把已作废引用当成当前证据。</para>
    /// </summary>
    public static async Task<ContainerShipmentTimelineDetailDto> GetForSourceAsync(
        IErpDbContext db, string? sourceType, long sourceId,
        bool includeHistory = true, int historyTake = ContainerShipmentTimelineRules.MaxHistoryEvents)
    {
        ArgumentNullException.ThrowIfNull(db);

        var type = ContainerShipmentReferenceRules.NormalizeSourceType(sourceType);
        if (sourceId <= 0)
            throw BusinessException.InvalidParameter(
                "请指定要查看出运证据时间线的源记录（订柜信息 / 预装柜单 / 装柜清单）：系统不按柜号 / 单号等自由文本匹配记录");

        var sourceAvailable = await SourceExistsAsync(db, type, sourceId);

        var reference = await db.ContainerShipmentReferences.AsNoTracking()
            .Where(r => !r.IsDeleted
                        && r.Status == ContainerShipmentReferenceRules.StatusRecorded
                        && r.SourceType == type && r.SourceId == sourceId)
            .OrderBy(r => r.Id)
            .FirstOrDefaultAsync();

        if (reference is null)
            return await NotLinkedDetailAsync(db, type, sourceId, sourceAvailable, historyTake);

        return await BuildDetailAsync(db, reference, sourceAvailable, includeHistory, historyTake);
    }

    // ==================== 2. 按显式出运引用读取时间线（工作台行入口） ====================

    /// <summary>
    /// 读取一条**显式出运引用**（含已作废历史）的出运证据时间线：只按引用 Id 定位，
    /// 源记录可用性按「源记录类型 + Id」单独标注（缺失 / 无效一律显式标注，绝不改派）。
    /// </summary>
    public static async Task<ContainerShipmentTimelineDetailDto> GetForReferenceAsync(
        IErpDbContext db, long referenceId,
        bool includeHistory = true, int historyTake = ContainerShipmentTimelineRules.MaxHistoryEvents)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (referenceId <= 0)
            throw BusinessException.InvalidParameter("请指定要查看出运证据时间线的出运引用");

        var reference = await db.ContainerShipmentReferences.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == referenceId && !r.IsDeleted)
            ?? throw BusinessException.NotFound($"出运引用不存在（Id={referenceId}）");

        var sourceAvailable = ContainerShipmentReferenceRules.IsSupportedSourceType(reference.SourceType)
                              && await SourceExistsAsync(
                                  db,
                                  ContainerShipmentReferenceRules.NormalizeSourceType(reference.SourceType),
                                  reference.SourceId);

        return await BuildDetailAsync(db, reference, sourceAvailable, includeHistory, historyTake);
    }

    // ==================== 3. 跟踪工作台（只读、分页、显式字段筛选） ====================

    /// <summary>
    /// 出运跟踪工作台（分页，只读）：只按**显式字段**筛选（源记录类型 / Id、柜号、单号、B/L、S/O、
    /// 起运·中转·目的港、计划开船·到港时间、记录事件类型与事件时间、关键字），默认包含已作废引用历史。
    /// <para>记录事件筛选在同一 SQL 查询内用相关 <c>EXISTS</c> 表达（不是逐行查库）；单页内里程碑汇总
    /// （有效 / 已作废 / 异常状态条数、各事件类型最近一次有效证据）与源记录可用性一律**批量装载**，
    /// 访问次数与页大小无关。</para>
    /// <para>未知筛选取值（源记录类型 / 事件类型 / 状态 / 时间区间颠倒 / 非正数源记录 Id）一律拒绝，
    /// 不静默忽略筛选条件；不同记录只按显式 Id 区分，绝不因文本相似而合并。</para>
    /// </summary>
    public static async Task<PagedResult<ContainerShipmentTimelineShipmentDto>> ListAsync(
        IErpDbContext db, ContainerShipmentTimelineQuery query)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(query);
        query.Normalize();

        // ---- 筛选参数规范化（未知取值一律拒绝） ----
        var sourceType = ContainerShipmentTimelineRules.NormalizeSourceTypeFilter(query.SourceType);
        var status = ContainerShipmentTimelineRules.NormalizeStatusFilter(query.Status);
        var eventType = ContainerShipmentTimelineRules.NormalizeEventTypeFilter(query.EventType);
        var sourceNo = ContainerShipmentTimelineRules.NormalizeExactFilter(query.SourceNo, "源记录单号");
        var containerNo = ContainerShipmentTimelineRules.NormalizeExactFilter(query.ContainerNo, "柜号");
        var billOfLadingNo = ContainerShipmentTimelineRules.NormalizeExactFilter(query.BillOfLadingNo, "提单号 B/L");
        var shippingOrderNo = ContainerShipmentTimelineRules.NormalizeExactFilter(query.ShippingOrderNo, "订舱号 S/O");
        var departurePort = ContainerShipmentTimelineRules.NormalizeExactFilter(query.DeparturePort, "起运港");
        var transitPort = ContainerShipmentTimelineRules.NormalizeExactFilter(query.TransitPort, "中转港");
        var destinationPort = ContainerShipmentTimelineRules.NormalizeExactFilter(query.DestinationPort, "目的港");
        var keyword = ContainerShipmentTimelineRules.NormalizeKeyword(query.Keyword);
        ContainerShipmentTimelineRules.EnsureRange(query.PlannedDepartureFrom, query.PlannedDepartureTo, "计划开船时间");
        ContainerShipmentTimelineRules.EnsureRange(query.PlannedArrivalFrom, query.PlannedArrivalTo, "计划到港时间");
        ContainerShipmentTimelineRules.EnsureRange(query.EventDateFrom, query.EventDateTo, "记录事件时间");

        if (query.SourceId is not null && query.SourceId <= 0)
            throw BusinessException.InvalidParameter("源记录 Id 必须是正整数（留空 = 不按源记录 Id 筛选）");

        var plannedDepartureFrom = query.PlannedDepartureFrom;
        var plannedDepartureTo = query.PlannedDepartureTo?.Date.AddDays(1);
        var plannedArrivalFrom = query.PlannedArrivalFrom;
        var plannedArrivalTo = query.PlannedArrivalTo?.Date.AddDays(1);

        var source = db.ContainerShipmentReferences.AsNoTracking().Where(r => !r.IsDeleted);
        if (sourceType is not null) source = source.Where(r => r.SourceType == sourceType);
        if (query.SourceId is not null) source = source.Where(r => r.SourceId == query.SourceId!.Value);
        if (status is not null) source = source.Where(r => r.Status == status.Value);

        // 显式等值筛选（统一大写后比较；不做相似度匹配）
        if (sourceNo is not null) source = source.Where(r => r.SourceNo.ToUpper() == sourceNo);
        if (containerNo is not null) source = source.Where(r => r.ContainerNo.ToUpper() == containerNo);
        if (billOfLadingNo is not null) source = source.Where(r => r.BillOfLadingNo.ToUpper() == billOfLadingNo);
        if (shippingOrderNo is not null) source = source.Where(r => r.ShippingOrderNo.ToUpper() == shippingOrderNo);
        if (departurePort is not null) source = source.Where(r => r.DeparturePort.ToUpper() == departurePort);
        if (transitPort is not null) source = source.Where(r => r.TransitPort.ToUpper() == transitPort);
        if (destinationPort is not null) source = source.Where(r => r.DestinationPort.ToUpper() == destinationPort);

        // 计划时间区间：只匹配**已持久化**的计划值（缺失 = 未知，绝不推断、绝不补全）
        if (plannedDepartureFrom is not null)
            source = source.Where(r => r.PlannedDepartureAt != null && r.PlannedDepartureAt >= plannedDepartureFrom);
        if (plannedDepartureTo is not null)
            source = source.Where(r => r.PlannedDepartureAt != null && r.PlannedDepartureAt < plannedDepartureTo);
        if (plannedArrivalFrom is not null)
            source = source.Where(r => r.PlannedArrivalAt != null && r.PlannedArrivalAt >= plannedArrivalFrom);
        if (plannedArrivalTo is not null)
            source = source.Where(r => r.PlannedArrivalAt != null && r.PlannedArrivalAt < plannedArrivalTo);

        // 记录事件筛选：命中至少一条匹配的里程碑证据（相关 EXISTS；默认只看有效证据）
        var includeVoidedEvents = query.IncludeVoidedEvents;
        var eventFrom = query.EventDateFrom;
        var eventToUpper = query.EventDateTo?.Date.AddDays(1);
        if (eventType is not null || eventFrom is not null || eventToUpper is not null || includeVoidedEvents)
        {
            source = source.Where(r => db.ContainerShipmentMilestones.Any(m =>
                m.ContainerShipmentReferenceId == r.Id
                && !m.IsDeleted
                && (includeVoidedEvents || m.Status == ContainerShipmentMilestoneRules.StatusRecorded)
                && (eventType == null || m.EventType == eventType)
                && (eventFrom == null || m.EventAt >= eventFrom)
                && (eventToUpper == null || m.EventAt < eventToUpper)));
        }

        // 关键字：只命中显式列（不做跨记录文本推断，也不会因此合并记录）
        if (keyword.Length > 0)
            source = source.Where(r => r.SourceNo.Contains(keyword)
                                       || r.ContainerNo.Contains(keyword)
                                       || r.BillOfLadingNo.Contains(keyword)
                                       || r.ShippingOrderNo.Contains(keyword)
                                       || r.CarrierName.Contains(keyword)
                                       || r.DestinationPort.Contains(keyword));

        var total = await source.CountAsync();
        var rows = await source.OrderByDescending(r => r.Id)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .ToListAsync();

        // 单页汇总**批量装载**（固定数量查询，无逐行访问）
        var ids = rows.Select(r => r.Id).ToList();
        var statusCounts = await LoadStatusCountMapAsync(db, ids);
        var latest = await LoadLatestActiveEventMapAsync(db, ids);
        var sourceAvailability = await LoadSourceAvailabilityMapAsync(db, rows);

        return new PagedResult<ContainerShipmentTimelineShipmentDto>
        {
            Items = rows.Select(r => MapShipment(
                r,
                IsSourceAvailable(sourceAvailability, r),
                statusCounts.TryGetValue(r.Id, out var counts) ? counts : new Dictionary<int, int>(),
                latest.TryGetValue(r.Id, out var events) ? events : new Dictionary<string, LatestEvent>())).ToList(),
            Total = total,
            Page = query.Page,
            PageSize = query.PageSize
        };
    }

    // ==================== 4. 时间线详情（有效时间线 + 显式历史视图 + 时间差） ====================

    /// <summary>
    /// 构造出运引用详情：计划条目（ETD / ETA，来自出运引用）+ **有效**实际事件（已登记里程碑，按事件时间升序）
    /// + 显式的已作废 / 历史异常状态视图（有界，按事件时间降序）+ 时间差算术证据。
    /// <para>只读：不写库、不改写引用与里程碑，也不把已作废证据混入有效时间线。</para>
    /// </summary>
    private static async Task<ContainerShipmentTimelineDetailDto> BuildDetailAsync(
        IErpDbContext db, ContainerShipmentReference reference, bool sourceAvailable,
        bool includeHistory, int historyTake)
    {
        var limit = ContainerShipmentTimelineRules.NormalizeHistoryTake(historyTake);
        var parentIds = new List<long> { reference.Id };

        var statusCounts = await LoadStatusCountMapAsync(db, parentIds);
        var counts = statusCounts.TryGetValue(reference.Id, out var counted)
            ? counted
            : new Dictionary<int, int>();
        var latestMap = await LoadLatestActiveEventMapAsync(db, parentIds);
        var latest = latestMap.TryGetValue(reference.Id, out var found)
            ? found
            : new Dictionary<string, LatestEvent>();

        var activeRows = await db.ContainerShipmentMilestones.AsNoTracking()
            .Where(m => !m.IsDeleted
                        && m.Status == ContainerShipmentMilestoneRules.StatusRecorded
                        && m.ContainerShipmentReferenceId == reference.Id)
            .OrderBy(m => m.EventAt).ThenBy(m => m.Id)
            .Take(ContainerShipmentTimelineRules.MaxActiveEvents + 1)
            .ToListAsync();
        var activeTruncated = activeRows.Count > ContainerShipmentTimelineRules.MaxActiveEvents;
        if (activeTruncated)
            activeRows = activeRows.Take(ContainerShipmentTimelineRules.MaxActiveEvents).ToList();

        // 历史视图（已作废 / 历史异常状态：从有效时间线排除，但按原值保留可读；有界且显式说明截断）
        var historyRows = new List<ContainerShipmentMilestone>();
        if (includeHistory)
        {
            historyRows = await db.ContainerShipmentMilestones.AsNoTracking()
                .Where(m => !m.IsDeleted
                            && m.Status != ContainerShipmentMilestoneRules.StatusRecorded
                            && m.ContainerShipmentReferenceId == reference.Id)
                .OrderByDescending(m => m.EventAt).ThenByDescending(m => m.Id)
                .Take(limit + 1)
                .ToListAsync();
        }
        var historyTruncated = historyRows.Count > limit;
        if (historyTruncated)
            historyRows = historyRows.Take(limit).ToList();

        var activeCount = counts.TryGetValue(ContainerShipmentMilestoneRules.StatusRecorded, out var recorded)
            ? recorded : 0;
        var voidedCount = counts.TryGetValue(ContainerShipmentMilestoneRules.StatusVoided, out var voided)
            ? voided : 0;
        var otherCount = counts.Where(kv => kv.Key != ContainerShipmentMilestoneRules.StatusRecorded
                                            && kv.Key != ContainerShipmentMilestoneRules.StatusVoided)
            .Sum(kv => kv.Value);

        var events = new List<ContainerShipmentTimelineEventDto>();
        events.AddRange(BuildPlannedEvents(reference.PlannedDepartureAt, reference.PlannedArrivalAt));
        events.AddRange(activeRows.Select(ActualEvent));

        return new ContainerShipmentTimelineDetailDto(
            MapShipment(reference, sourceAvailable, counts, latest),
            events,
            historyRows.Select(ActualEvent).ToList(),
            activeCount,
            voidedCount + otherCount,
            activeTruncated,
            historyTruncated,
            ContainerShipmentTimelineRules.MaxActiveEvents,
            limit,
            BuildVariances(reference, latest),
            ContainerShipmentTimelineRules.PlannedVersusActualText,
            ContainerShipmentTimelineRules.NoStatusInferenceText,
            ContainerShipmentTimelineRules.EvidenceText,
            ContainerShipmentTimelineRules.HistoryText,
            ContainerShipmentTimelineRules.RuleText + " "
                + ContainerShipmentTimelineRules.SourceLinkText + " "
                + ContainerShipmentTimelineRules.PlannedVersusActualText,
            ContainerShipmentTimelineRules.BoundaryText);
    }

    /// <summary>
    /// 「未关联有效出运引用」详情（只读）：证据全部显示「未知 / 无」，并显式说明原因
    /// （源记录不存在 / 已删除、或只有已作废引用历史）—— <strong>不</strong>按柜号 / S/O / B/L 兜底匹配引用，
    /// 也<strong>不</strong>把已作废引用当成当前证据。
    /// </summary>
    private static async Task<ContainerShipmentTimelineDetailDto> NotLinkedDetailAsync(
        IErpDbContext db, string sourceType, long sourceId, bool sourceAvailable, int historyTake)
    {
        var typeText = ContainerShipmentReferenceRules.SourceTypeText(sourceType);
        var reason = $"该{typeText}（Id={sourceId}）没有有效出运引用：时间线不显示任何出运证据"
                     + "（计划时间显示「未知」、实际事件显示「无（未登记）」）。请在装柜出运引用登记册按显式记录登记一条引用"
                     + "后再查看；系统不会按柜号 / S/O / B/L 等自由文本匹配引用，也不会把已作废引用当成当前证据。";

        var hasAnyReference = await db.ContainerShipmentReferences.AsNoTracking()
            .AnyAsync(r => !r.IsDeleted && r.SourceType == sourceType && r.SourceId == sourceId);
        if (hasAnyReference)
            reason += "（该源记录存在已作废的出运引用历史：可在跟踪工作台按状态「已作废」查看其历史证据，"
                      + "历史证据不会被当成当前证据，也不会被改派。）";
        if (!sourceAvailable)
            reason += $"（源记录（{typeText}）已删除或不存在：历史证据照实呈现，系统不会改派到其它记录。）";

        return new ContainerShipmentTimelineDetailDto(
            NotLinkedShipment(sourceType, sourceId, sourceAvailable, reason),
            BuildPlannedEvents(null, null),
            new List<ContainerShipmentTimelineEventDto>(),
            0,
            0,
            false,
            false,
            ContainerShipmentTimelineRules.MaxActiveEvents,
            ContainerShipmentTimelineRules.NormalizeHistoryTake(historyTake),
            BuildVariances(null, new Dictionary<string, LatestEvent>()),
            ContainerShipmentTimelineRules.PlannedVersusActualText,
            ContainerShipmentTimelineRules.NoStatusInferenceText,
            ContainerShipmentTimelineRules.EvidenceText,
            ContainerShipmentTimelineRules.HistoryText,
            ContainerShipmentTimelineRules.RuleText + " "
                + ContainerShipmentTimelineRules.SourceLinkText + " "
                + ContainerShipmentTimelineRules.PlannedVersusActualText,
            ContainerShipmentTimelineRules.BoundaryText);
    }

    // ==================== 5. 批量装载（固定数量查询，无逐行访问） ====================

    /// <summary>某个出运引用下**最近一条有效证据**的时间与有效证据条数（只统计已登记状态）</summary>
    private sealed record LatestEvent(DateTime EventAt, int Count);

    /// <summary>
    /// 这些出运引用下的里程碑状态条数（一次分组查询，固定数量；无逐行数据库访问）。
    /// 未知状态码照实计入（界面按「未知（原值）」呈现，绝不静默修正）。
    /// </summary>
    private static async Task<Dictionary<long, Dictionary<int, int>>> LoadStatusCountMapAsync(
        IErpDbContext db, List<long> parentIds)
    {
        if (parentIds.Count == 0) return new Dictionary<long, Dictionary<int, int>>();

        var rows = await db.ContainerShipmentMilestones.AsNoTracking()
            .Where(m => !m.IsDeleted && parentIds.Contains(m.ContainerShipmentReferenceId))
            .GroupBy(m => new { m.ContainerShipmentReferenceId, m.Status })
            .Select(g => new { g.Key.ContainerShipmentReferenceId, g.Key.Status, Count = g.Count() })
            .ToListAsync();

        return rows.GroupBy(r => r.ContainerShipmentReferenceId)
            .ToDictionary(
                g => g.Key,
                g => g.ToDictionary(x => x.Status, x => x.Count));
    }

    /// <summary>
    /// 这些出运引用下**每个事件类型最近一条有效证据**的时间与条数（一次分组查询，每个引用最多 4 行，固定数量）：
    /// 用于工作台摘要与时间差依据 —— 已作废证据**不**参与（从有效口径排除），只作历史呈现。
    /// </summary>
    private static async Task<Dictionary<long, Dictionary<string, LatestEvent>>> LoadLatestActiveEventMapAsync(
        IErpDbContext db, List<long> parentIds)
    {
        if (parentIds.Count == 0) return new Dictionary<long, Dictionary<string, LatestEvent>>();

        var rows = await db.ContainerShipmentMilestones.AsNoTracking()
            .Where(m => !m.IsDeleted
                        && m.Status == ContainerShipmentMilestoneRules.StatusRecorded
                        && parentIds.Contains(m.ContainerShipmentReferenceId))
            .GroupBy(m => new { m.ContainerShipmentReferenceId, m.EventType })
            .Select(g => new
            {
                g.Key.ContainerShipmentReferenceId,
                g.Key.EventType,
                EventAt = g.Max(x => x.EventAt),
                Count = g.Count()
            })
            .ToListAsync();

        return rows.GroupBy(r => r.ContainerShipmentReferenceId)
            .ToDictionary(
                g => g.Key,
                g => g.ToDictionary(
                    x => x.EventType ?? string.Empty,
                    x => new LatestEvent(x.EventAt, x.Count)));
    }

    /// <summary>
    /// 这些出运引用的源记录可用性（按源记录类型各一次查询，最多 3 次；未删除 = 可用）：
    /// 源记录被删除 / 不存在 / 源记录 Id 无效时一律显式标注不可用，且<strong>不</strong>改派到别的记录。
    /// </summary>
    private static async Task<Dictionary<(string Type, long Id), bool>> LoadSourceAvailabilityMapAsync(
        IErpDbContext db, IList<ContainerShipmentReference> rows)
    {
        var result = new Dictionary<(string Type, long Id), bool>();

        foreach (var type in ContainerShipmentReferenceRules.SupportedSourceTypes)
        {
            var ids = rows
                .Where(r => string.Equals(NormalizeSourceTypeValue(r.SourceType), type, StringComparison.Ordinal)
                            && r.SourceId > 0)
                .Select(r => r.SourceId)
                .Distinct()
                .ToList();
            if (ids.Count == 0) continue;

            var existing = type switch
            {
                ContainerShipmentReferenceRules.SourceTypeBooking => await db.ContainerBookings.AsNoTracking()
                    .Where(o => ids.Contains(o.Id) && !o.IsDeleted).Select(o => o.Id).ToListAsync(),
                ContainerShipmentReferenceRules.SourceTypePreLoading => await db.ContainerPreLoadings.AsNoTracking()
                    .Where(o => ids.Contains(o.Id) && !o.IsDeleted).Select(o => o.Id).ToListAsync(),
                _ => await db.ContainerLoadingLists.AsNoTracking()
                    .Where(o => ids.Contains(o.Id) && !o.IsDeleted).Select(o => o.Id).ToListAsync()
            };

            var found = existing.ToHashSet();
            foreach (var id in ids)
                result[(type, id)] = found.Contains(id);
        }

        return result;
    }

    /// <summary>源记录类型规范化（小写；未知历史取值照实保留，不抛异常，保证历史行可读）</summary>
    private static string NormalizeSourceTypeValue(string? sourceType) =>
        (sourceType ?? string.Empty).Trim().ToLowerInvariant();

    /// <summary>按批量映射判断某条引用的源记录是否可用（Id 无效 / 类型未知一律视为不可用）</summary>
    private static bool IsSourceAvailable(
        Dictionary<(string Type, long Id), bool> map, ContainerShipmentReference reference) =>
        map.TryGetValue((NormalizeSourceTypeValue(reference.SourceType), reference.SourceId), out var available)
        && available;

    /// <summary>源记录是否存在且未删除（按显式源记录类型各查一次；Id 无效或类型未知一律视为不存在）</summary>
    private static Task<bool> SourceExistsAsync(IErpDbContext db, string sourceType, long sourceId) =>
        sourceId <= 0
            ? Task.FromResult(false)
            : sourceType switch
            {
                ContainerShipmentReferenceRules.SourceTypeBooking =>
                    db.ContainerBookings.AsNoTracking().AnyAsync(o => o.Id == sourceId && !o.IsDeleted),
                ContainerShipmentReferenceRules.SourceTypePreLoading =>
                    db.ContainerPreLoadings.AsNoTracking().AnyAsync(o => o.Id == sourceId && !o.IsDeleted),
                ContainerShipmentReferenceRules.SourceTypeLoadingList =>
                    db.ContainerLoadingLists.AsNoTracking().AnyAsync(o => o.Id == sourceId && !o.IsDeleted),
                _ => Task.FromResult(false)
            };

    // ==================== 6. 时间线条目构造（计划 / 实际分开标注） ====================

    /// <summary>计划条目（两条固定条目：计划开船 ETD、计划到港 ETA）——未填写保持「未知」，绝不补全</summary>
    private static List<ContainerShipmentTimelineEventDto> BuildPlannedEvents(
        DateTime? plannedDepartureAt, DateTime? plannedArrivalAt) => new()
    {
        PlannedEvent(ContainerShipmentTimelineRules.KindPlannedDeparture, plannedDepartureAt),
        PlannedEvent(ContainerShipmentTimelineRules.KindPlannedArrival, plannedArrivalAt)
    };

    /// <summary>计划条目映射（纯映射，不查库；<c>null</c> = 未知，显示「未知（未登记该计划时间）」）</summary>
    private static ContainerShipmentTimelineEventDto PlannedEvent(string kind, DateTime? plannedAt) => new(
        kind,
        ContainerShipmentTimelineRules.KindText(kind),
        true,
        false,
        plannedAt is not null,
        plannedAt,
        ContainerShipmentTimelineRules.TimestampText(plannedAt, isPlanned: true),
        ContainerShipmentTimelineRules.TimestampKindText(plannedAt),
        null,
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty,
        null,
        null,
        string.Empty,
        false,
        null,
        string.Empty,
        "计划值（来自 ERP-057 出运引用登记册）：是计划，不是实际事件，也不代表已发生",
        "来源：出运引用登记册（ERP-057）计划值");

    /// <summary>
    /// 实际事件条目映射（纯映射，不查库）：事件类型 / 状态 / 证据性质文案一律按<strong>当前</strong>字段值重算
    /// （未知历史取值照实回显，绝不映射成已知类型，也不把状态改成「已登记」）。
    /// </summary>
    private static ContainerShipmentTimelineEventDto ActualEvent(ContainerShipmentMilestone row)
    {
        ArgumentNullException.ThrowIfNull(row);
        var kind = ContainerShipmentTimelineRules.KindActual(row.EventType);
        var isVoided = row.Status == ContainerShipmentMilestoneRules.StatusVoided;

        return new ContainerShipmentTimelineEventDto(
            kind,
            ContainerShipmentTimelineRules.KindText(kind),
            false,
            true,
            true,
            row.EventAt,
            ContainerShipmentTimelineRules.TimestampText(row.EventAt, isPlanned: false),
            ContainerShipmentTimelineRules.TimestampKindText(row.EventAt),
            row.Id,
            row.EventType ?? string.Empty,
            ContainerShipmentMilestoneRules.EventTypeText(row.EventType),
            row.SourceDescription ?? string.Empty,
            row.Notes ?? string.Empty,
            row.RecordedBy ?? string.Empty,
            row.RecordedAt,
            row.Status,
            ContainerShipmentMilestoneRules.StatusText(row.Status),
            isVoided,
            row.VoidedAt,
            row.VoidReason ?? string.Empty,
            ContainerShipmentMilestoneRules.EvidenceCategoryText(row.EventType),
            $"来源：里程碑证据（ERP-058，Id={row.Id}；记录人 {Label(row.RecordedBy)}）");
    }

    /// <summary>只读文本（空 = 未知；不回落为空串）</summary>
    private static string Label(string? value)
    {
        var text = ContainerShipmentTrackingRules.NormalizeText(value);
        return text.Length == 0 ? ContainerShipmentTimelineRules.UnknownText : text;
    }

    // ==================== 7. 时间差（算术证据，仅在两条持久化时间戳可比时给出） ====================

    /// <summary>
    /// 两个对比项：开船（计划开船 ETD vs 最近一条有效实际开船证据）与到港（计划到港 ETA vs 最近一条有效实际到港证据）。
    /// <para>实际侧只取**有效**（已登记）里程碑：已作废证据不参与时间差（但仍保留在历史视图中）。</para>
    /// </summary>
    private static List<ContainerShipmentTimelineVarianceDto> BuildVariances(
        ContainerShipmentReference? reference, Dictionary<string, LatestEvent> latest) => new()
    {
        Variance(ContainerShipmentTimelineRules.VarianceKindDeparture, "开船时间差", "计划开船（ETD）",
            "实际开船（最近一条有效证据）", reference?.PlannedDepartureAt,
            ContainerShipmentMilestoneRules.EventTypeActualDeparture, latest),
        Variance(ContainerShipmentTimelineRules.VarianceKindArrival, "到港时间差", "计划到港（ETA）",
            "实际到港（最近一条有效证据）", reference?.PlannedArrivalAt,
            ContainerShipmentMilestoneRules.EventTypeActualArrival, latest)
    };

    /// <summary>单个时间差对比项（纯映射，不查库）：任一侧缺失一律显示「无法比较」并说明缺失的是哪一侧</summary>
    private static ContainerShipmentTimelineVarianceDto Variance(
        string kind, string label, string plannedLabel, string actualLabel,
        DateTime? plannedAt, string eventType, Dictionary<string, LatestEvent> latest)
    {
        var hasActual = latest.TryGetValue(eventType, out var actual);
        var actualAt = hasActual ? actual!.EventAt : (DateTime?)null;
        var (comparable, text) = ContainerShipmentTimelineRules.DescribeVariance(label, plannedAt, actualAt);
        var eventTypeText = ContainerShipmentMilestoneRules.EventTypeText(eventType);
        var evidenceText = hasActual
            ? $"依据：最近一条有效「{eventTypeText}」证据（{actualAt:yyyy-MM-dd HH:mm}，有效证据共 {actual!.Count} 条）"
            : $"依据：{ContainerShipmentTimelineRules.MissingEventText}（没有有效的「{eventTypeText}」证据）";

        return new ContainerShipmentTimelineVarianceDto(
            kind,
            label,
            plannedLabel,
            plannedAt,
            ContainerShipmentTimelineRules.TimestampText(plannedAt, isPlanned: true),
            ContainerShipmentTimelineRules.TimestampKindText(plannedAt),
            actualLabel,
            actualAt,
            ContainerShipmentTimelineRules.TimestampText(actualAt, isPlanned: false),
            ContainerShipmentTimelineRules.TimestampKindText(actualAt),
            evidenceText,
            comparable,
            text,
            ContainerShipmentTimelineRules.VarianceBasisText);
    }

    // ==================== 8. 汇总（只说明登记情况，不是业务状态结论） ====================

    private static ContainerShipmentTimelineSummaryDto BuildSummary(
        Dictionary<string, LatestEvent> latest, int activeCount, int voidedCount, int otherCount)
    {
        DateTime? LatestOf(string eventType) =>
            latest.TryGetValue(eventType, out var hit) ? hit.EventAt : null;

        var registered = ContainerShipmentMilestoneRules.SupportedEventTypes
            .Where(latest.ContainsKey)
            .Select(ContainerShipmentMilestoneRules.EventTypeText)
            .ToList();
        var missing = ContainerShipmentMilestoneRules.SupportedEventTypes
            .Where(t => !latest.ContainsKey(t))
            .Select(ContainerShipmentMilestoneRules.EventTypeText)
            .ToList();

        var text = $"有效里程碑证据 {activeCount} 条"
                   + (voidedCount > 0 ? $"，已作废（历史视图）{voidedCount} 条" : string.Empty)
                   + (otherCount > 0 ? $"，历史异常状态 {otherCount} 条（照实保留，不静默修正）" : string.Empty)
                   + $"，已登记实际事件：{(registered.Count == 0 ? $"无（未登记任何实际事件，显示「{ContainerShipmentTimelineRules.MissingEventText}」）" : string.Join(" / ", registered))}"
                   + $"，未登记：{(missing.Count == 0 ? "无" : string.Join(" / ", missing))}"
                   + "（只说明登记情况，不是已开船 / 已到港 / 已清关 / 延误 / 逾期的业务状态结论）";

        return new ContainerShipmentTimelineSummaryDto(
            activeCount,
            voidedCount,
            otherCount,
            LatestOf(ContainerShipmentMilestoneRules.EventTypeActualDeparture),
            LatestOf(ContainerShipmentMilestoneRules.EventTypeActualArrival),
            LatestOf(ContainerShipmentMilestoneRules.EventTypeInspection),
            LatestOf(ContainerShipmentMilestoneRules.EventTypeCustomsRelease),
            text,
            ContainerShipmentTimelineRules.NoStatusInferenceText);
    }

    // ==================== 9. 映射（纯映射，不查库） ====================

    /// <summary>
    /// 出运引用（ERP-057）→ 工作台行 / 详情头（纯映射）：引用上的证据字段原样回显，缺失一律「未知」；
    /// 可用性（引用自身、源记录）与汇总按只读标注写入，未知历史状态 / 类型照实说明。
    /// </summary>
    private static ContainerShipmentTimelineShipmentDto MapShipment(
        ContainerShipmentReference r, bool sourceAvailable,
        Dictionary<int, int> statusCounts, Dictionary<string, LatestEvent> latest)
    {
        ArgumentNullException.ThrowIfNull(r);
        var activeCount = statusCounts.TryGetValue(ContainerShipmentMilestoneRules.StatusRecorded, out var a) ? a : 0;
        var voidedCount = statusCounts.TryGetValue(ContainerShipmentMilestoneRules.StatusVoided, out var v) ? v : 0;
        var otherCount = statusCounts.Where(kv => kv.Key != ContainerShipmentMilestoneRules.StatusRecorded
                                                  && kv.Key != ContainerShipmentMilestoneRules.StatusVoided)
            .Sum(kv => kv.Value);

        return new ContainerShipmentTimelineShipmentDto(
            true,
            string.Empty,
            r.Id,
            r.SourceType ?? string.Empty,
            ContainerShipmentReferenceRules.SourceTypeText(r.SourceType),
            r.SourceId,
            r.SourceNo ?? string.Empty,
            r.SourceDate,
            r.SourceStatus,
            string.IsNullOrEmpty(r.SourceStatusText)
                ? ContainerShipmentReferenceRules.DocumentStatusText(r.SourceStatus)
                : r.SourceStatusText,
            r.ContainerNo ?? string.Empty,
            r.ShipmentMode ?? string.Empty,
            ContainerShipmentTrackingRules.ShipmentModeText(r.ShipmentMode),
            r.ShippingOrderNo ?? string.Empty,
            r.BillOfLadingNo ?? string.Empty,
            r.CarrierName ?? string.Empty,
            r.ForwarderName ?? string.Empty,
            r.DeparturePort ?? string.Empty,
            r.TransitPort ?? string.Empty,
            r.DestinationPort ?? string.Empty,
            r.PlannedDepartureAt,
            r.PlannedArrivalAt,
            r.TruckerName ?? string.Empty,
            r.Remark ?? string.Empty,
            r.Status,
            ReferenceStatusText(r.Status),
            r.Status == ContainerShipmentReferenceRules.StatusRecorded,
            r.Status == ContainerShipmentReferenceRules.StatusVoided,
            r.RevisionNo,
            r.RecordedAt,
            r.VoidedAt,
            r.VoidReason ?? string.Empty,
            true,
            ReferenceAvailabilityText(r),
            sourceAvailable,
            SourceAvailabilityText(r, sourceAvailable),
            BuildSummary(latest, activeCount, voidedCount, otherCount));
    }

    /// <summary>
    /// 「未关联」行（纯映射）：证据字段一律空 / <c>null</c>（前端显示「未知 / 无」），
    /// 并显式给出未关联原因 —— 系统绝不按柜号 / S/O / B/L 兜底挑一条引用，也不做任何推断。
    /// </summary>
    private static ContainerShipmentTimelineShipmentDto NotLinkedShipment(
        string sourceType, long sourceId, bool sourceAvailable, string reason) => new(
        false,
        reason,
        0,
        sourceType,
        ContainerShipmentReferenceRules.SourceTypeText(sourceType),
        sourceId,
        string.Empty,
        default,
        0,
        string.Empty,
        string.Empty,
        string.Empty,
        ContainerShipmentTrackingRules.UnknownText,
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty,
        null,
        null,
        string.Empty,
        string.Empty,
        0,
        string.Empty,
        false,
        false,
        0,
        null,
        null,
        string.Empty,
        false,
        "没有有效出运引用（未关联）：不显示任何出运证据，系统不会按柜号 / S/O / B/L 匹配引用，也不会改派",
        sourceAvailable,
        sourceAvailable
            ? $"源记录（{ContainerShipmentReferenceRules.SourceTypeText(sourceType)}）存在：登记出运引用后即可在此查看时间线"
            : $"源记录（{ContainerShipmentReferenceRules.SourceTypeText(sourceType)}）已删除、不存在或链接无效："
              + "历史证据照实呈现，系统不会改派到其它记录",
        BuildSummary(new Dictionary<string, LatestEvent>(), 0, 0, 0));

    /// <summary>出运引用状态文案（读取侧）：未知状态码**照实说明**而不是抛异常，保证历史异常行可读、不被静默修正</summary>
    private static string ReferenceStatusText(int status) => status switch
    {
        ContainerShipmentReferenceRules.StatusRecorded => "已登记",
        ContainerShipmentReferenceRules.StatusVoided => "已作废",
        _ => $"未知（{status}）"
    };

    /// <summary>出运引用可用性文案（已作废引用只作历史呈现，不新增任何证据）</summary>
    private static string ReferenceAvailabilityText(ContainerShipmentReference r) =>
        r.Status == ContainerShipmentReferenceRules.StatusVoided
            ? "出运引用已作废：证据保留可读（含作废原因），时间线只作历史呈现，不新增任何证据"
            : "出运引用可用（只读呈现，不改写该记录）";

    /// <summary>
    /// 源记录可用性文案（只读派生）：源记录被删除 / 不存在 / 源记录 Id 无效时一律显式标注，
    /// 历史证据照实呈现，<strong>不</strong>改派到别的记录、也<strong>不</strong>回填任何快照。
    /// </summary>
    private static string SourceAvailabilityText(ContainerShipmentReference r, bool available)
    {
        var typeText = ContainerShipmentReferenceRules.SourceTypeText(r.SourceType);
        if (available) return $"源记录（{typeText}）可用：按显式源记录类型 + Id 只读关联";
        return r.SourceId <= 0
            ? "源记录链接无效（未记录源记录 Id）：历史证据按原值呈现，系统不会改派到其它记录"
            : $"源记录（{typeText}，Id={r.SourceId}）已删除或不存在：历史证据按原值呈现，系统不会改派到其它记录";
    }
}
