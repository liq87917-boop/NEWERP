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
/// <para>授权与串行化（ERP-365，复用 ERP-362 口径）：五个入口（<see cref="CreateAsync"/> /
/// <see cref="VoidAsync"/> / <see cref="ListAsync"/> / <see cref="GetAsync"/> /
/// <see cref="ListParentCandidatesAsync"/>）都先按父出运引用<strong>持久化</strong>的源记录类型 + Id 授权
/// （既有源模块菜单 + 权威客户数据范围），再读取任何计数 / 证据字段；受限账号的范围在
/// <c>Count</c> / 分页 / <c>Take</c> 之前下推到数据库。写路径由控制器在可序列化事务内先对
/// <b>父引用行</b>与<b>源记录行</b>加 <c>UPDLOCK, HOLDLOCK</c>（作废时对<b>里程碑行</b>加锁）后调用本服务。</para>
/// <para>边界（重要）：本服务只读写 <c>ContainerShipmentMilestones</c> 一张表（读取时只读父出运引用，
/// 以及经 <see cref="ContainerShipmentReferenceService.ResolveSourceStateAsync"/> 读取父引用**源记录**的
/// 只读资格快照），<strong>不</strong>改写父出运引用、订柜信息 / 预装柜单 / 装柜清单的任何列、状态与工作流，
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
    /// <para>校验顺序（ERP-365）：父出运引用 Id → 事件类型（allowlist）→ 事件时间（必填 + 有界）→
    /// 父出运引用存在且未删除未作废 → <b>既有源模块菜单授权</b>（按父引用持久化的源记录类型）→
    /// <b>权威客户数据范围</b> → <b>父引用源记录资格</b>（存在 / 未删除 / 未取消）→
    /// 重复有效证据（同父 + 同类型 + 同时间）拒绝。</para>
    /// <para>授权与资格校验全部先于任何计数 / 证据读取：先授权再读，绝不「先读再判」。</para>
    /// <para>调用方（控制器）必须在可串行化事务内先对**父引用行**与**源记录行**加
    /// <c>UPDLOCK, HOLDLOCK</c> 后再调用本方法：父引用资格、源记录资格与「同父 + 同类型 + 同时间」
    /// 唯一性都落在同一把锁与同一事务内（并发登记只能成功一条）。</para>
    /// </summary>
    public static async Task<ContainerShipmentMilestoneDto> CreateAsync(
        IErpDbContext db, ContainerShipmentMilestoneSaveDto dto, ShipmentReferenceAccess access)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(dto);
        ArgumentNullException.ThrowIfNull(access);

        if (dto.ContainerShipmentReferenceId <= 0)
            throw BusinessException.InvalidParameter(
                "请选择要登记里程碑证据的出运引用（ERP-057 登记册中的一条记录）");

        var eventType = ContainerShipmentMilestoneRules.NormalizeEventType(dto.EventType);
        var eventAt = ContainerShipmentMilestoneRules.NormalizeEventAt(dto.EventAt);

        var parent = await LoadParentAsync(db, dto.ContainerShipmentReferenceId);
        var parentLabel = $"{ContainerShipmentReferenceRules.SourceTypeText(parent.SourceType)}"
                          + $"「{parent.SourceNo}」";

        // ERP-365：授权先于任何计数 / 证据读取 —— 父引用持久化的源记录类型必须落在既有源模块菜单内，
        // 受限账号还必须命中该源记录的权威客户范围（fail closed，不泄露范围外证据）。
        ShipmentReferenceAuthorizationRules.RequireSourceType(access, parent.SourceType);
        await ShipmentReferenceAuthorizationRules.EnsureScopeAllowsSourceAsync(
            db, access, parent.SourceType, parent.SourceId);

        // ERP-365：父出运引用的**源记录**也必须仍在资格内（存在、未删除、未取消），否则拒绝新增证据
        // （历史里程碑照常可读，也绝不因此改写源记录 / 改派父记录）。
        await EnsureParentSourceEligibleAsync(db, parent);

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
            CreatedAt = DateTime.Now,
            CreatedBy = access.UserId,
            UpdatedBy = access.UserId
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
        IErpDbContext db, long id, string? reason, ShipmentReferenceAccess access)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(access);
        if (id <= 0) throw BusinessException.InvalidParameter("请指定要作废的里程碑证据");

        var row = await LoadAsync(db, id);

        // ERP-365：按父出运引用**持久化的源记录类型 + Id** 授权（读 / 写同口径）；父引用被物理删除时
        // 受限账号无法解析权威客户归属 → fail closed，特权账号照常（仅用于历史证据的显式更正）。
        var parent = await db.ContainerShipmentReferences.AsNoTracking()
            .FirstOrDefaultAsync(o => o.Id == row.ContainerShipmentReferenceId);
        await ShipmentReferenceAuthorizationRules.EnsureScopeAllowsParentReferenceAsync(db, access, parent);

        ContainerShipmentMilestoneRules.EnsureRecordedForVoid(row.Status);

        var voidReason = ContainerShipmentMilestoneRules.NormalizeVoidReason(reason);
        var now = DateTime.Now;

        // 只改本行状态与作废留痕：原始事件类型 / 事件时间 / 来源说明 / 备注 / 记录人 / 登记时间照常保留。
        row.Status = ContainerShipmentMilestoneRules.StatusVoided;
        row.VoidedAt = now;
        row.VoidReason = voidReason;
        row.UpdatedAt = now;
        row.UpdatedBy = access.UserId;
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

    /// <summary>
    /// 父出运引用的**源记录**资格守卫（ERP-365，与 ERP-057 登记口径同源）：源记录只按显式类型 + Id
    /// 读取（绝不按柜号 / S/O / B/L 等自由文本匹配），不存在 → 数据不存在；已删除 / 已取消 →
    /// 业务规则冲突（拒绝**新增**里程碑证据，历史里程碑照常可读）。只读快照，不改写源记录任何列。
    /// </summary>
    private static async Task EnsureParentSourceEligibleAsync(
        IErpDbContext db, ContainerShipmentReference parent)
    {
        var sourceTypeText = ContainerShipmentReferenceRules.SourceTypeText(parent.SourceType);
        var state = await ContainerShipmentReferenceService.ResolveSourceStateAsync(
            db, parent.SourceType, parent.SourceId);
        var (eligible, text) = ContainerShipmentMilestoneRules.EvaluateParentSourceEligibility(
            state.Exists, state.Deleted, state.Cancelled, sourceTypeText);
        if (eligible) return;

        var detail = $"{sourceTypeText}「{parent.SourceNo}」（源记录 Id={parent.SourceId}）";
        if (!state.Exists) throw BusinessException.NotFound($"{text}：{detail}");
        throw BusinessException.RuleConflict($"{text}：{detail}");
    }

    // ==================== 4. 台账 / 详情（只读，分页有界，批量装载） ====================

    /// <summary>
    /// 里程碑台账（分页，只读）：可按父出运引用、事件类型、状态、事件时间区间与关键字过滤；
    /// 默认包含已作废历史（证据保留可读）。单页内的父记录信息（源记录类型 / 单号 / 柜号 / 状态 /
    /// 可用性）一律**批量装载**（固定数量查询），不产生逐行数据库访问。
    /// <para>排序按事件时间倒序（同一时间按 Id 倒序）：时间线一眼可读，且结果稳定可复现。</para>
    /// <para>ERP-365：受限账号的既有源模块菜单授权与权威客户数据范围在 <c>Count</c> 与分页**之前**
    /// 下推到数据库（按持久化的父出运引用 Id 关联判定），绝不「先查全量再内存过滤」。</para>
    /// </summary>
    public static async Task<PagedResult<ContainerShipmentMilestoneDto>> ListAsync(
        IErpDbContext db, ContainerShipmentMilestoneQuery query, ShipmentReferenceAccess access)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(access);
        query.Normalize();

        var keyword = ContainerShipmentMilestoneRules.NormalizeKeyword(query.Keyword);
        var status = ContainerShipmentMilestoneRules.NormalizeStatusFilter(query.Status);
        var eventType = ContainerShipmentMilestoneRules.NormalizeEventTypeFilter(query.EventType);

        var source = db.ContainerShipmentMilestones.AsNoTracking().Where(o => !o.IsDeleted);
        // ERP-365：受限账号的既有源模块菜单 + 权威客户数据范围先于计数与分页下推到数据库。
        source = ShipmentReferenceAuthorizationRules.ApplyScopeToMilestones(db, source, access);
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
    /// <para>ERP-365：先按父出运引用**持久化的源记录类型 + Id** 授权（读 / 写同口径），再标注；
    /// 父引用缺失时受限账号 fail closed，特权账号照常（历史证据显式标注不可用）。</para>
    /// </summary>
    public static async Task<ContainerShipmentMilestoneDetailDto> GetAsync(
        IErpDbContext db, long id, ShipmentReferenceAccess access)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(access);
        if (id <= 0) throw BusinessException.InvalidParameter("请指定要查看的里程碑证据");

        var row = await db.ContainerShipmentMilestones.AsNoTracking()
            .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
            ?? throw BusinessException.NotFound($"里程碑证据不存在（Id={id}）");

        var parent = await db.ContainerShipmentReferences.AsNoTracking()
            .FirstOrDefaultAsync(o => o.Id == row.ContainerShipmentReferenceId);
        await ShipmentReferenceAuthorizationRules.EnsureScopeAllowsParentReferenceAsync(db, access, parent);

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
    /// <para>ERP-365：候选同样受既有源模块菜单与权威客户数据范围约束（受限账号只列出本人客户的源记录，
    /// 范围先于 <c>Take</c> 下推到数据库）。</para>
    /// </summary>
    public static async Task<List<ContainerShipmentMilestoneParentCandidateDto>> ListParentCandidatesAsync(
        IErpDbContext db, string? keyword, ShipmentReferenceAccess access,
        int take = ContainerShipmentMilestoneRules.MaxParentCandidates)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(access);

        var filter = ContainerShipmentMilestoneRules.NormalizeKeyword(keyword);
        var limit = take <= 0 ? ContainerShipmentMilestoneRules.MaxParentCandidates
            : Math.Min(take, ContainerShipmentMilestoneRules.MaxParentCandidates);

        var source = db.ContainerShipmentReferences.AsNoTracking()
            .Where(r => !r.IsDeleted && r.Status == ContainerShipmentReferenceRules.StatusRecorded);
        // ERP-365：既有源模块菜单 + 权威客户数据范围先于 Take 下推（受限账号只见本人客户）。
        source = ShipmentReferenceAuthorizationRules.ApplyScope(db, source, access);

        var rows = await source
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
                available, parentVoided,
                available ? ContainerShipmentReferenceRules.SourceTypeText(parent!.SourceType) : string.Empty);
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
