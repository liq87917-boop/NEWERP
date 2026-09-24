using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace ERP.Application.Services;

/// <summary>
/// 装柜出运里程碑证据服务（ERP-058）。职责：
/// <list type="number">
/// <item><b>登记里程碑</b>（<see cref="CreateAsync"/>）：父记录必须是显式指定的 ERP-057 出运引用
/// （存在、未删除、未作废），事件类型只接受 allowlist，事件时间必填且有界；同一父记录 + 事件类型 +
/// 事件时间不允许重复有效登记（重复直接拒绝，不静默合并）；</item>
/// <item><b>作废里程碑</b>（<see cref="VoidAsync"/>）：必须填写原因，只改状态与作废留痕，
/// 保留原始事件类型 / 事件时间 / 来源说明 / 备注 / 记录人，不物理删除、不提供改写；</item>
/// <item><b>台账与详情读取</b>（<see cref="ListAsync"/> / <see cref="GetAsync"/>）：分页 / 有界，
/// 单页内父记录信息**批量装载**（固定数量查询），无逐行数据库查询；</item>
/// <item><b>父记录候选读取</b>（<see cref="ListParentCandidatesAsync"/>）：只读、有界，只列出未删除、
/// 未作废的出运引用并批量统计里程碑条数（用于**显式选择**父记录，绝不按柜号 / S/O / B/L 猜测）。</item>
/// </list>
/// <para>审计口径：ERP-057 已建立唯一权威的出运引用登记册，本服务只在其<strong>之下</strong>追加
/// 操作性事件证据：<strong>不</strong>新建出运 / 跟踪主数据、<strong>不</strong>在装柜三单或出运引用上加列。</para>
/// <para>边界（重要）：本服务只读写 <c>ContainerShipmentMilestones</c> 一张表（读取时只读父出运引用），
/// <strong>不</strong>改写父出运引用、订柜信息 / 预装柜单 / 装柜清单的任何列、状态与工作流，
/// <strong>不</strong>自动推进任何业务单据，也<strong>不</strong>改写销售订单、采购订单、库存与库存成本、
/// 库存流水、单证中心、发票、费用与分摊、收付款、税务与结算记录，更<strong>不</strong>轮询承运人、海关、
/// 货代或任何外部系统（里程碑不是承运人 / 海关确认，也不是出运许可）。</para>
/// </summary>
public static class ContainerShipmentMilestoneService
{
    // ==================== 1. 登记里程碑证据（只追加） ====================

    /// <summary>
    /// 登记一条里程碑证据：全部校验通过后才写一行证据；事件时间与登记时间由服务端权威写入，
    /// 可选字段（来源说明 / 备注 / 记录人）留空时保持空串 = 未知，绝不推断。
    /// <para>校验顺序：父出运引用 Id → 事件类型（allowlist）→ 事件时间（必填 + 有界）→
    /// 父记录存在且未删除未作废 → 重复有效证据（同父 + 同类型 + 同时间）拒绝。</para>
    /// </summary>
    public static async Task<ContainerShipmentMilestoneDto> CreateAsync(
        IErpDbContext db, ContainerShipmentMilestoneSaveDto dto)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(dto);

        if (dto.ContainerShipmentReferenceId <= 0)
            throw BusinessException.InvalidParameter(
                "请选择要登记里程碑证据的出运引用（ERP-057 登记册中的一条记录）");

        var eventType = ContainerShipmentMilestoneRules.NormalizeEventType(dto.EventType);
        var eventAt = ContainerShipmentMilestoneRules.NormalizeEventAt(dto.EventAt);

        var parent = await LoadParentAsync(db, dto.ContainerShipmentReferenceId);
        var parentLabel = $"{ContainerShipmentReferenceRules.SourceTypeText(parent.SourceType)}"
                          + $"「{parent.SourceNo}」";

        // 同一父记录 + 事件类型 + 事件时间：不允许重复有效证据（重复直接拒绝，不静默合并、
        // 也不覆盖既有来源说明 / 备注 / 记录人）；已作废行不占用额度，作废后可重新登记。
        var duplicated = await db.ContainerShipmentMilestones.AsNoTracking()
            .FirstOrDefaultAsync(m => !m.IsDeleted
                                      && m.Status == ContainerShipmentMilestoneRules.StatusRecorded
                                      && m.ContainerShipmentReferenceId == parent.Id
                                      && m.EventType == eventType
                                      && m.EventAt == eventAt);

        if (duplicated is not null)
            throw BusinessException.Duplicate(
                $"{parentLabel}已有相同里程碑有效证据（{ContainerShipmentMilestoneRules.EventTypeText(eventType)}"
                + $" · {eventAt:yyyy-MM-dd HH:mm}，Id={duplicated.Id}，登记于 {duplicated.RecordedAt:yyyy-MM-dd HH:mm}）："
                + "请先核对该证据；如确为误录请显式作废它，系统不会静默合并或覆盖既有证据");

        var row = new ContainerShipmentMilestone
        {
            ContainerShipmentReferenceId = parent.Id,
            EventType = eventType,
            EventAt = eventAt,
            SourceDescription = ContainerShipmentMilestoneRules.NormalizeSourceDescription(dto.SourceDescription),
            Notes = ContainerShipmentMilestoneRules.NormalizeNotes(dto.Notes),
            RecordedBy = ContainerShipmentMilestoneRules.NormalizeRecordedBy(dto.RecordedBy),
            RecordedAt = DateTime.Now,
            Status = ContainerShipmentMilestoneRules.StatusRecorded,
            CreatedAt = DateTime.Now
        };

        db.ContainerShipmentMilestones.Add(row);
        await db.SaveChangesAsync();

        return await MapSingleAsync(db, row);
    }

    // ==================== 2. 作废里程碑证据（保留原始证据与留痕） ====================

    /// <summary>
    /// 作废一条**已登记**里程碑证据（必须填写原因）：只把状态改为已作废并记录作废时间 / 原因，
    /// 保留原始事件类型、事件时间、来源说明、备注、记录人与登记时间；重复作废被拒绝，
    /// 不提供硬删除，也不提供改写已作废证据的接口。
    /// </summary>
    public static async Task<ContainerShipmentMilestoneDto> VoidAsync(
        IErpDbContext db, long id, string? reason)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (id <= 0) throw BusinessException.InvalidParameter("请指定要作废的里程碑证据");

        var row = await LoadAsync(db, id);
        ContainerShipmentMilestoneRules.EnsureRecordedForVoid(row.Status);

        var voidReason = ContainerShipmentMilestoneRules.NormalizeVoidReason(reason);

        row.Status = ContainerShipmentMilestoneRules.StatusVoided;
        row.VoidedAt = DateTime.Now;
        row.VoidReason = voidReason;
        row.UpdatedAt = DateTime.Now;
        await db.SaveChangesAsync();

        return await MapSingleAsync(db, row);
    }

    // ==================== 3. 记录加载（显式 Id，拒绝不存在 / 已删除 / 已作废父记录） ====================

    /// <summary>
    /// 按显式出运引用 Id 加载父记录：不存在 → 数据不存在；已软删除或已作废 → 业务规则冲突
    /// （历史里程碑仍可读，但不能新增证据）；<strong>不</strong>按柜号 / S/O / B/L 等自由文本兜底匹配。
    /// </summary>
    private static async Task<ContainerShipmentReference> LoadParentAsync(IErpDbContext db, long referenceId)
    {
        var parent = await db.ContainerShipmentReferences.AsNoTracking()
            .FirstOrDefaultAsync(o => o.Id == referenceId);

        var (eligible, text) = ContainerShipmentMilestoneRules.EvaluateParentEligibility(
            parent is not null, parent?.IsDeleted == true, parent?.Status ?? 0);
        if (eligible) return parent!;

        if (parent is null) throw BusinessException.NotFound($"{text}（Id={referenceId}）");
        throw BusinessException.RuleConflict($"{text}（Id={referenceId}）");
    }

    /// <summary>加载未删除的里程碑证据（跟踪实体，供写入路径使用）；不存在 → 数据不存在</summary>
    private static async Task<ContainerShipmentMilestone> LoadAsync(IErpDbContext db, long id)
        => await db.ContainerShipmentMilestones.FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
           ?? throw BusinessException.NotFound($"里程碑证据不存在（Id={id}）");

    // ==================== 4. 台账 / 详情（只读，分页有界，批量装载） ====================

    /// <summary>
    /// 里程碑台账（分页，只读）：可按父出运引用、事件类型、状态、事件时间区间与关键字过滤；
    /// 默认包含已作废历史（证据保留可读）。单页内的父记录信息（源记录类型 / 单号 / 柜号 / 状态 /
    /// 可用性）一律**批量装载**（固定数量查询），不产生逐行数据库访问。
    /// <para>排序按事件时间倒序（同一时间按 Id 倒序）：时间线一眼可读，且结果稳定可复现。</para>
    /// </summary>
    public static async Task<PagedResult<ContainerShipmentMilestoneDto>> ListAsync(
        IErpDbContext db, ContainerShipmentMilestoneQuery query)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(query);
        query.Normalize();

        var keyword = ContainerShipmentMilestoneRules.NormalizeKeyword(query.Keyword);
        var status = ContainerShipmentMilestoneRules.NormalizeStatusFilter(query.Status);
        var eventType = ContainerShipmentMilestoneRules.NormalizeEventTypeFilter(query.EventType);

        var source = db.ContainerShipmentMilestones.AsNoTracking().Where(o => !o.IsDeleted);
        if (query.ContainerShipmentReferenceId is > 0)
            source = source.Where(o => o.ContainerShipmentReferenceId == query.ContainerShipmentReferenceId!.Value);
        if (eventType is not null) source = source.Where(o => o.EventType == eventType);
        if (status is not null) source = source.Where(o => o.Status == status.Value);
        if (query.EventDateFrom is not null)
            source = source.Where(o => o.EventAt >= query.EventDateFrom.Value);
        if (query.EventDateTo is not null)
        {
            var upperBound = query.EventDateTo.Value.Date.AddDays(1);
            source = source.Where(o => o.EventAt < upperBound);
        }
        if (keyword.Length > 0)
            source = source.Where(o => o.SourceDescription.Contains(keyword)
                                       || o.Notes.Contains(keyword)
                                       || o.RecordedBy.Contains(keyword));

        var total = await source.CountAsync();
        var rows = await source.OrderByDescending(o => o.EventAt).ThenByDescending(o => o.Id)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .ToListAsync();

        await AnnotateAsync(db, rows);

        return new PagedResult<ContainerShipmentMilestoneDto>
        {
            Items = rows.Select(Map).ToList(),
            Total = total,
            Page = query.Page,
            PageSize = query.PageSize
        };
    }

    /// <summary>
    /// 里程碑详情（只读）：当前证据 + 父记录关联口径 / 证据语义 / 模块边界文案。
    /// <para>里程碑不提供修改接口：更正走显式作废（必填原因），已作废证据保留原始值可读。</para>
    /// </summary>
    public static async Task<ContainerShipmentMilestoneDetailDto> GetAsync(IErpDbContext db, long id)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (id <= 0) throw BusinessException.InvalidParameter("请指定要查看的里程碑证据");

        var row = await db.ContainerShipmentMilestones.AsNoTracking()
            .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
            ?? throw BusinessException.NotFound($"里程碑证据不存在（Id={id}）");

        await AnnotateAsync(db, new[] { row });

        return new ContainerShipmentMilestoneDetailDto(
            Map(row),
            ContainerShipmentMilestoneRules.RuleText + " "
                + ContainerShipmentMilestoneRules.ParentLinkText + " "
                + ContainerShipmentMilestoneRules.EvidenceText,
            ContainerShipmentMilestoneRules.BoundaryText);
    }

    // ==================== 5. 父记录候选（只读、有界、显式选择） ====================

    /// <summary>
    /// 可挂里程碑证据的出运引用候选（只读、有界）：只列出 ERP-057 中**未删除且未作废**的出运引用，
    /// 并按父记录**批量统计**里程碑条数与有效条数（固定数量查询，无逐行访问）。
    /// <para>用于**显式选择**父记录：系统<strong>不</strong>按柜号 / S/O / B/L 或任何自由文本
    /// 自动挑选父记录；没有任何里程碑的历史出运引用照常出现在候选中，不需要任何回填。</para>
    /// </summary>
    public static async Task<List<ContainerShipmentMilestoneParentCandidateDto>> ListParentCandidatesAsync(
        IErpDbContext db, string? keyword, int take = ContainerShipmentMilestoneRules.MaxParentCandidates)
    {
        ArgumentNullException.ThrowIfNull(db);

        var filter = ContainerShipmentMilestoneRules.NormalizeKeyword(keyword);
        var limit = take <= 0 ? ContainerShipmentMilestoneRules.MaxParentCandidates
            : Math.Min(take, ContainerShipmentMilestoneRules.MaxParentCandidates);

        var rows = await db.ContainerShipmentReferences.AsNoTracking()
            .Where(r => !r.IsDeleted && r.Status == ContainerShipmentReferenceRules.StatusRecorded)
            .Where(r => filter.Length == 0 || r.SourceNo.Contains(filter)
                        || r.ContainerNo.Contains(filter)
                        || r.BillOfLadingNo.Contains(filter)
                        || r.ShippingOrderNo.Contains(filter)
                        || r.CarrierName.Contains(filter))
            .OrderByDescending(r => r.Id).Take(limit)
            .Select(r => new
            {
                r.Id, r.SourceType, r.SourceNo, r.ContainerNo, r.SourceDate, r.SourceStatus, r.SourceStatusText
            })
            .ToListAsync();

        var counts = await LoadMilestoneCountMapAsync(db, rows.Select(r => r.Id).ToList());

        return rows.Select(r =>
        {
            counts.TryGetValue(r.Id, out var count);
            var sourceTypeText = ContainerShipmentReferenceRules.SourceTypeText(r.SourceType);
            var no = r.SourceNo ?? string.Empty;
            var text = count.Total == 0
                ? $"{sourceTypeText}「{no}」可登记里程碑证据（当前没有任何里程碑；登记只读关联，不会改写该出运引用）"
                : $"{sourceTypeText}「{no}」已有 {count.Total} 条里程碑（有效 {count.Active} 条，"
                  + "含已作废历史）：请先核对时间线，系统不会静默合并或覆盖既有证据";

            return new ContainerShipmentMilestoneParentCandidateDto(
                r.Id,
                r.SourceType ?? string.Empty,
                sourceTypeText,
                no,
                r.ContainerNo ?? string.Empty,
                r.SourceDate,
                r.SourceStatus,
                string.IsNullOrEmpty(r.SourceStatusText)
                    ? ContainerShipmentReferenceRules.DocumentStatusText(r.SourceStatus)
                    : r.SourceStatusText,
                true,
                count.Total,
                count.Active,
                text);
        }).ToList();
    }

    /// <summary>
    /// 这些父出运引用下的里程碑条数 / 有效条数（两次分组查询，固定数量；无逐行数据库访问）。
    /// 已作废行计入总数（保留可读的作废历史），但不计入有效条数。
    /// </summary>
    private static async Task<Dictionary<long, (int Total, int Active)>> LoadMilestoneCountMapAsync(
        IErpDbContext db, List<long> parentIds)
    {
        if (parentIds.Count == 0) return new Dictionary<long, (int Total, int Active)>();

        var totals = await db.ContainerShipmentMilestones.AsNoTracking()
            .Where(m => !m.IsDeleted && parentIds.Contains(m.ContainerShipmentReferenceId))
            .GroupBy(m => m.ContainerShipmentReferenceId)
            .Select(g => new { ParentId = g.Key, Count = g.Count() })
            .ToListAsync();

        var actives = await db.ContainerShipmentMilestones.AsNoTracking()
            .Where(m => !m.IsDeleted && m.Status == ContainerShipmentMilestoneRules.StatusRecorded
                        && parentIds.Contains(m.ContainerShipmentReferenceId))
            .GroupBy(m => m.ContainerShipmentReferenceId)
            .Select(g => new { ParentId = g.Key, Count = g.Count() })
            .ToListAsync();

        var activeMap = actives.ToDictionary(a => a.ParentId, a => a.Count);
        return totals.ToDictionary(
            t => t.ParentId,
            t => (t.Count, activeMap.TryGetValue(t.ParentId, out var active) ? active : 0));
    }

    // ==================== 6. 读取标注与映射（批量装载，无逐行查询） ====================

    /// <summary>
    /// 为一批里程碑证据统一标注（固定数量查询，不产生逐行数据库访问）：父出运引用信息
    /// （源记录类型 / 单号 / 柜号 / 状态 / 可用性）一次批量装载，并补齐事件类型 / 状态 /
    /// 证据性质与边界文案。标注只是 <c>[NotMapped]</c> 读取值，不写库。
    /// <para>父记录被删除 / 不存在时一律显式标注不可用：历史里程碑照常可读，
    /// <strong>不</strong>改派到别的出运引用，也<strong>不</strong>回填任何快照。</para>
    /// </summary>
    private static async Task AnnotateAsync(IErpDbContext db, IList<ContainerShipmentMilestone> rows)
    {
        if (rows.Count == 0) return;

        var parentIds = rows.Select(r => r.ContainerShipmentReferenceId).Distinct().ToList();
        var parents = await db.ContainerShipmentReferences.AsNoTracking()
            .Where(p => parentIds.Contains(p.Id))
            .Select(p => new { p.Id, p.SourceType, p.SourceNo, p.ContainerNo, p.Status, p.IsDeleted })
            .ToListAsync();
        var map = parents.ToDictionary(p => p.Id);

        foreach (var row in rows)
        {
            // 读取侧文案一律按**当前**字段值重算（不沿用实体上可能残留的旧标注：
            // 同一个跟踪实体在「登记 → 作废」后再次标注时，状态文案必须跟着新状态走）。
            row.EventTypeText = ContainerShipmentMilestoneRules.EventTypeText(row.EventType);
            row.StatusText = ContainerShipmentMilestoneRules.StatusText(row.Status);
            row.EvidenceCategoryText = ContainerShipmentMilestoneRules.EvidenceCategoryText(row.EventType);
            row.BoundaryText = ContainerShipmentMilestoneRules.BoundaryText;

            map.TryGetValue(row.ContainerShipmentReferenceId, out var parent);
            var available = parent is not null && !parent.IsDeleted;
            var parentVoided = parent is not null
                               && parent.Status == ContainerShipmentReferenceRules.StatusVoided;

            row.ParentAvailable = available;
            row.ParentSourceType = available ? parent!.SourceType ?? string.Empty : string.Empty;
            row.ParentSourceTypeText = available
                ? ContainerShipmentReferenceRules.SourceTypeText(parent!.SourceType)
                : string.Empty;
            row.ParentSourceNo = available ? parent!.SourceNo ?? string.Empty : string.Empty;
            row.ParentContainerNo = available ? parent!.ContainerNo ?? string.Empty : string.Empty;
            row.ParentStatusText = available
                ? ContainerShipmentMilestoneRules.ParentStatusText(parent!.Status)
                : string.Empty;
            row.ParentAvailabilityText = ContainerShipmentMilestoneRules.ParentAvailabilityText(
                available, parentVoided);
        }
    }

    /// <summary>登记 / 作废后返回单条证据（先批量标注再映射；不写库）</summary>
    private static async Task<ContainerShipmentMilestoneDto> MapSingleAsync(
        IErpDbContext db, ContainerShipmentMilestone row)
    {
        await AnnotateAsync(db, new[] { row });
        return Map(row);
    }

    /// <summary>里程碑实体 → DTO（纯映射，不写库；未知取值照实回显，绝不按「已登记」兜底）</summary>
    private static ContainerShipmentMilestoneDto Map(ContainerShipmentMilestone row)
    {
        ArgumentNullException.ThrowIfNull(row);

        return new ContainerShipmentMilestoneDto(
            row.Id,
            row.ContainerShipmentReferenceId,
            row.ParentSourceType ?? string.Empty,
            row.ParentSourceTypeText ?? string.Empty,
            row.ParentSourceNo ?? string.Empty,
            row.ParentContainerNo ?? string.Empty,
            row.ParentStatusText ?? string.Empty,
            row.ParentAvailable,
            row.ParentAvailabilityText ?? string.Empty,
            ContainerShipmentTrackingRules.NormalizeText(row.EventType).ToLowerInvariant(),
            string.IsNullOrEmpty(row.EventTypeText)
                ? ContainerShipmentMilestoneRules.EventTypeText(row.EventType)
                : row.EventTypeText,
            row.EventAt,
            row.SourceDescription ?? string.Empty,
            row.Notes ?? string.Empty,
            row.RecordedBy ?? string.Empty,
            row.RecordedAt,
            row.Status,
            string.IsNullOrEmpty(row.StatusText)
                ? ContainerShipmentMilestoneRules.StatusText(row.Status)
                : row.StatusText,
            row.Status == ContainerShipmentMilestoneRules.StatusRecorded,
            row.Status == ContainerShipmentMilestoneRules.StatusVoided,
            row.VoidedAt,
            row.VoidReason ?? string.Empty,
            string.IsNullOrEmpty(row.EvidenceCategoryText)
                ? ContainerShipmentMilestoneRules.EvidenceCategoryText(row.EventType)
                : row.EvidenceCategoryText,
            row.CreatedAt,
            row.UpdatedAt,
            string.IsNullOrEmpty(row.BoundaryText)
                ? ContainerShipmentMilestoneRules.BoundaryText
                : row.BoundaryText);
    }
}
