using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace ERP.Application.Services;

/// <summary>
/// 装柜出运引用证据登记服务（ERP-057）。职责：
/// <list type="number">
/// <item><b>登记引用</b>（<see cref="CreateAsync"/>）：源记录必须是显式类型下**存在且未删除**的既有记录
/// （订柜信息 / 预装柜单 / 装柜清单），且同一条源记录最多保留 1 条有效引用；出运证据字段全部可选，
/// 服务端只做规范化与边界校验（出运方式只接受 LCL / FCL / 未指定，计划时间必须先后一致）；</item>
/// <item><b>修订引用</b>（<see cref="UpdateAsync"/>）：先把修订前的**原值**写入只追加的修订留痕
/// （<see cref="ContainerShipmentReferenceRevision"/>），再写回新值并递增修订号；源记录类型 / Id 不允许改派；</item>
/// <item><b>作废引用</b>（<see cref="VoidAsync"/>）：必须填写原因，保留原始值、源记录快照与全部修订留痕，
/// 不物理删除、不静默替换；</item>
/// <item><b>台账与详情读取</b>（<see cref="ListAsync"/> / <see cref="GetAsync"/> / <see cref="ListRevisionsAsync"/>）：
/// 分页 / 有界、批量装载（源记录可用性、报关行可用性、修订条数），无逐行数据库查询；</item>
/// <item><b>源记录候选读取</b>（<see cref="ListSourceCandidatesAsync"/>）：只读、有界，只列出该类型下未删除的
/// 既有记录并标注是否已有有效引用（用于**显式选择**，绝不按柜号 / 单号猜测）。</item>
/// </list>
/// <para>审计口径：装柜链路已有唯一的持久化引用关系（<c>ContainerPreLoading.BookingId</c> /
/// <c>ContainerLoadingList.PreLoadingId</c>），订柜信息由 ERP-040 承载本套跟踪值的权威记录；
/// 因此本服务<strong>不</strong>新建出运主数据、<strong>不</strong>在订柜 / 预装柜 / 装柜清单上加列。</para>
/// <para>边界（重要）：本服务只读写 <c>ContainerShipmentReferences</c> 与
/// <c>ContainerShipmentReferenceRevisions</c> 两张表，<strong>不</strong>改写源记录的任何列、状态与工作流，
/// <strong>不</strong>改写销售订单、采购订单、库存与库存成本、库存流水、单证中心、发票、费用与分摊、
/// 收付款、税务与结算记录，也不联系承运人、海关、货代或任何外部跟踪系统。</para>
/// </summary>
public static class ContainerShipmentReferenceService
{
    // ==================== 1. 登记出运引用（新增） ====================

    /// <summary>
    /// 登记一条出运引用证据：全部校验通过后才写一行证据，源记录单号 / 日期 / 状态 / 柜号快照与
    /// 报关行名称快照一律由服务端权威写入。
    /// <para>校验顺序：源记录类型（allowlist）→ 源记录 Id → 源记录存在且未删除 → 有效引用唯一性
    /// （同一源记录最多 1 条有效引用）→ 出运证据字段（规范化 / 长度 / 出运方式 / 计划时间一致性）→
    /// 报关行引用可选用性。</para>
    /// </summary>
    public static async Task<ContainerShipmentReferenceDto> CreateAsync(
        IErpDbContext db, ContainerShipmentReferenceSaveDto dto)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(dto);

        var sourceType = ContainerShipmentReferenceRules.NormalizeSourceType(dto.SourceType);
        if (dto.SourceId <= 0)
            throw BusinessException.InvalidParameter("请选择要登记出运引用的源记录（订柜信息 / 预装柜单 / 装柜清单）");

        var source = await LoadSourceAsync(db, sourceType, dto.SourceId);
        var sourceTypeText = ContainerShipmentReferenceRules.SourceTypeText(sourceType);

        // 同一源记录最多 1 条有效引用（已作废行不占额度：作废后可重新登记，新旧并存可查）
        var existing = await db.ContainerShipmentReferences.AsNoTracking()
            .FirstOrDefaultAsync(r => !r.IsDeleted && r.Status == ContainerShipmentReferenceRules.StatusRecorded
                                      && r.SourceType == sourceType && r.SourceId == source.Id);

        if (existing is not null)
            throw BusinessException.Duplicate(
                $"{sourceTypeText}「{source.SourceNo}」已有有效出运引用（Id={existing.Id}，"
                + $"登记于 {existing.RecordedAt:yyyy-MM-dd HH:mm}）：请改为修订该引用，"
                + "或先作废它再重新登记（系统不会静默覆盖已有证据）");

        var row = new ContainerShipmentReference
        {
            SourceType = sourceType,
            SourceId = source.Id,
            SourceNo = source.SourceNo,
            SourceDate = source.SourceDate,
            SourceStatus = source.Status,
            SourceStatusText = source.StatusText,
            ContainerNo = source.ContainerNo,
            Status = ContainerShipmentReferenceRules.StatusRecorded,
            RecordedAt = DateTime.Now,
            RevisionNo = 1,
            CreatedAt = DateTime.Now
        };

        // 先校验 / 规范化证据字段（不合格直接拒绝，不产生半条记录）
        await ApplyEvidenceAsync(db, row, dto, storedBrokerId: null, storedBrokerName: string.Empty);

        db.ContainerShipmentReferences.Add(row);
        await db.SaveChangesAsync();

        return await MapSingleAsync(db, row);
    }

    // ==================== 2. 修订出运引用（保留修订前原值） ====================

    /// <summary>
    /// 修订一条**已登记**出运引用的证据字段：先写入一条只追加的「修订前原值」留痕（含必填修订原因），
    /// 再写回新值并递增修订号。
    /// <para>不允许改派源记录（类型 / Id 必须与库中一致）；已作废引用只读；修订只改本登记册自己的两行数据，
    /// <strong>不</strong>改写源记录与任何下游单据。</para>
    /// </summary>
    public static async Task<ContainerShipmentReferenceDto> UpdateAsync(
        IErpDbContext db, long id, ContainerShipmentReferenceUpdateDto dto)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(dto);
        if (id <= 0) throw BusinessException.InvalidParameter("请指定要修订的出运引用");

        var row = await LoadAsync(db, id);
        var sourceTypeText = ContainerShipmentReferenceRules.SourceTypeText(row.SourceType);
        ContainerShipmentReferenceRules.EnsureRecordedForChange(
            row.Status, $"{sourceTypeText}「{row.SourceNo}」");
        ContainerShipmentReferenceRules.EnsureSourceUnchanged(
            row.SourceType, row.SourceId, dto.SourceType, dto.SourceId);

        var reason = ContainerShipmentReferenceRules.NormalizeRevisionReason(dto.Reason);

        // 修订前的报关行引用与名称快照：用于识别「引用未变更」并保护历史名称
        var storedBrokerId = row.CustomsBrokerId;
        var storedBrokerName = row.CustomsBrokerName;

        // 修订留痕：逐列记录**修订前**的原值（只追加，永不改写）
        var revision = new ContainerShipmentReferenceRevision
        {
            ContainerShipmentReferenceId = row.Id,
            RevisionNo = row.RevisionNo,
            SourceType = row.SourceType,
            SourceId = row.SourceId,
            SourceNo = row.SourceNo,
            SupersededAt = DateTime.Now,
            Reason = reason,
            ShipmentMode = row.ShipmentMode,
            ShippingOrderNo = row.ShippingOrderNo,
            BillOfLadingNo = row.BillOfLadingNo,
            CarrierName = row.CarrierName,
            ForwarderName = row.ForwarderName,
            DeparturePort = row.DeparturePort,
            TransitPort = row.TransitPort,
            DestinationPort = row.DestinationPort,
            PlannedDepartureAt = row.PlannedDepartureAt,
            PlannedArrivalAt = row.PlannedArrivalAt,
            TruckerName = row.TruckerName,
            CustomsBrokerId = row.CustomsBrokerId,
            CustomsBrokerName = row.CustomsBrokerName,
            Remark = row.Remark,
            CreatedAt = DateTime.Now
        };

        // 先校验 / 规范化新值（不合格直接拒绝：留痕与新值都不落库）
        await ApplyEvidenceAsync(db, row, dto, storedBrokerId, storedBrokerName);

        row.RevisionNo += 1;
        row.LastRevisedAt = DateTime.Now;
        row.LastRevisionReason = reason;
        row.UpdatedAt = DateTime.Now;

        db.ContainerShipmentReferenceRevisions.Add(revision);
        await db.SaveChangesAsync();

        return await MapSingleAsync(db, row);
    }

    // ==================== 3. 作废出运引用（保留历史） ====================

    /// <summary>
    /// 作废一条已登记出运引用（必须填写原因）：只把状态改为已作废并记录作废时间 / 原因，
    /// 保留原始证据字段、源记录快照与全部修订留痕；重复作废被拒绝，不提供硬删除。
    /// </summary>
    public static async Task<ContainerShipmentReferenceDto> VoidAsync(IErpDbContext db, long id, string? reason)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (id <= 0) throw BusinessException.InvalidParameter("请指定要作废的出运引用");

        var row = await LoadAsync(db, id);
        var sourceTypeText = ContainerShipmentReferenceRules.SourceTypeText(row.SourceType);
        ContainerShipmentReferenceRules.EnsureRecordedForChange(
            row.Status, $"{sourceTypeText}「{row.SourceNo}」");

        var voidReason = ContainerShipmentReferenceRules.NormalizeVoidReason(reason);

        row.Status = ContainerShipmentReferenceRules.StatusVoided;
        row.VoidedAt = DateTime.Now;
        row.VoidReason = voidReason;
        row.UpdatedAt = DateTime.Now;
        await db.SaveChangesAsync();

        return await MapSingleAsync(db, row);
    }

    // ==================== 4. 出运证据字段落地（登记 / 修订共用） ====================

    /// <summary>
    /// 规范化并落地出运证据字段（登记 / 修订共用）：文本去空白 + 长度校验（超长拒绝，不静默截断）、
    /// 出运方式只接受 LCL / FCL / 未指定、计划时间有界且必须先后一致、报关行必须是可选用字典项。
    /// <para>缺失字段一律保持空串 / <c>null</c>（= 未知）：绝不按柜型、体积、客户、航线或自由文本推断补全。</para>
    /// </summary>
    /// <param name="db">数据访问上下文（只读报关行字典项）</param>
    /// <param name="row">即将写入的出运引用（含客户端提交的证据字段）</param>
    /// <param name="dto">客户端提交的请求</param>
    /// <param name="storedBrokerId">库中已存在的报关行引用（登记时为 <c>null</c>），用于识别「引用未变更」</param>
    /// <param name="storedBrokerName">库中已存在的报关行名称快照（引用不可用时保留历史名称，绝不接受客户端改写）</param>
    private static async Task ApplyEvidenceAsync(
        IErpDbContext db, ContainerShipmentReference row, ContainerShipmentReferenceSaveDto dto,
        long? storedBrokerId, string storedBrokerName)
    {
        row.ShipmentMode = ContainerShipmentTrackingRules.NormalizeShipmentMode(dto.ShipmentMode);
        row.ShippingOrderNo = ContainerShipmentReferenceRules.NormalizeText(
            dto.ShippingOrderNo, ContainerShipmentTrackingRules.ShippingOrderNoMaxLength, "订舱号（S/O）");
        row.BillOfLadingNo = ContainerShipmentReferenceRules.NormalizeText(
            dto.BillOfLadingNo, ContainerShipmentTrackingRules.BillOfLadingNoMaxLength, "提单号（B/L）");
        row.CarrierName = ContainerShipmentReferenceRules.NormalizeText(
            dto.CarrierName, ContainerShipmentReferenceRules.CarrierNameMaxLength, "承运人");
        row.ForwarderName = ContainerShipmentReferenceRules.NormalizeText(
            dto.ForwarderName, ContainerShipmentReferenceRules.ForwarderNameMaxLength, "货代");
        row.DeparturePort = ContainerShipmentReferenceRules.NormalizeText(
            dto.DeparturePort, ContainerShipmentTrackingRules.PortMaxLength, "起运港");
        row.TransitPort = ContainerShipmentReferenceRules.NormalizeText(
            dto.TransitPort, ContainerShipmentTrackingRules.PortMaxLength, "中转港");
        row.DestinationPort = ContainerShipmentReferenceRules.NormalizeText(
            dto.DestinationPort, ContainerShipmentTrackingRules.PortMaxLength, "目的港");
        row.PlannedDepartureAt = ContainerShipmentReferenceRules.NormalizePlannedTimestamp(
            dto.PlannedDepartureAt, "计划开船时间（ETD）");
        row.PlannedArrivalAt = ContainerShipmentReferenceRules.NormalizePlannedTimestamp(
            dto.PlannedArrivalAt, "计划到港时间（ETA）");
        ContainerShipmentReferenceRules.EnsurePlannedTimestampsCoherent(
            row.PlannedDepartureAt, row.PlannedArrivalAt);
        row.TruckerName = ContainerShipmentReferenceRules.NormalizeText(
            dto.TruckerName, ContainerShipmentTrackingRules.TruckerNameMaxLength, "拖车 / 集卡服务商");
        row.Remark = ContainerShipmentReferenceRules.NormalizeRemark(dto.Remark);

        row.CustomsBrokerId = dto.CustomsBrokerId is > 0 ? dto.CustomsBrokerId : null;
        await ApplyCustomsBrokerAsync(db, row, storedBrokerId, storedBrokerName);
    }

    /// <summary>
    /// 报关行引用落地（与 ERP-040 同一口径）：<c>null</c> / <c>0</c> = 未指定（两字段一并清空）；
    /// 引用未变更时保留历史引用（字典项不可用时保留库中名称快照并标注不可用，绝不接受客户端改写历史名称）；
    /// 新增 / 更换时必须是未删除、已启用、类型为 CustomsBroker 的字典项，名称快照由服务端权威写入。
    /// </summary>
    private static async Task ApplyCustomsBrokerAsync(
        IErpDbContext db, ContainerShipmentReference row, long? storedBrokerId, string storedBrokerName)
    {
        var requested = row.CustomsBrokerId;
        if (requested is not > 0)
        {
            row.CustomsBrokerId = null;
            row.CustomsBrokerName = string.Empty;
            row.CustomsBrokerAvailable = true;
            return;
        }

        var entry = await db.BaseOtherInfos.AsNoTracking()
            .FirstOrDefaultAsync(o => o.Id == requested!.Value);
        var unchanged = storedBrokerId is > 0 && storedBrokerId == requested;

        if (unchanged)
        {
            if (entry is not null && ContainerShipmentTrackingRules.IsSelectableCustomsBroker(entry))
            {
                row.CustomsBrokerName = ContainerShipmentTrackingRules.SnapshotName(entry);
                row.CustomsBrokerAvailable = true;
            }
            else
            {
                row.CustomsBrokerName = storedBrokerName;
                row.CustomsBrokerAvailable = false;
            }
            return;
        }

        ContainerShipmentTrackingRules.EnsureSelectableCustomsBroker(entry, requested.Value);
        row.CustomsBrokerName = ContainerShipmentTrackingRules.SnapshotName(entry!);
        row.CustomsBrokerAvailable = true;
    }

    // ==================== 5. 源记录加载（显式类型 + Id，拒绝不存在 / 已删除） ====================

    /// <summary>源记录快照（服务端按源记录权威写入，客户端提交值一律不被采信）</summary>
    private sealed record SourceSnapshot(
        long Id, string SourceNo, DateTime SourceDate, int Status, string StatusText, string ContainerNo);

    /// <summary>
    /// 按显式类型 + Id 加载源记录并生成快照：不存在 → 数据不存在；已软删除 → 业务规则冲突
    /// （历史证据仍可读，但不能新增引用）；<strong>不</strong>按柜号 / 单号等自由文本兜底匹配。
    /// </summary>
    private static async Task<SourceSnapshot> LoadSourceAsync(IErpDbContext db, string sourceType, long sourceId)
    {
        var sourceTypeText = ContainerShipmentReferenceRules.SourceTypeText(sourceType);

        switch (sourceType)
        {
            case ContainerShipmentReferenceRules.SourceTypeBooking:
                var booking = await db.ContainerBookings.AsNoTracking()
                    .FirstOrDefaultAsync(o => o.Id == sourceId);
                EnsureSourceEligible(booking is not null, booking?.IsDeleted == true, sourceTypeText, sourceId);
                return new SourceSnapshot(booking!.Id, booking.BookingNo ?? string.Empty, booking.BookingDate,
                    (int)booking.Status, ContainerShipmentReferenceRules.DocumentStatusText((int)booking.Status),
                    string.Empty);

            case ContainerShipmentReferenceRules.SourceTypePreLoading:
                var preLoading = await db.ContainerPreLoadings.AsNoTracking()
                    .FirstOrDefaultAsync(o => o.Id == sourceId);
                EnsureSourceEligible(
                    preLoading is not null, preLoading?.IsDeleted == true, sourceTypeText, sourceId);
                return new SourceSnapshot(preLoading!.Id, preLoading.PreLoadingNo ?? string.Empty,
                    preLoading.LoadingDate, (int)preLoading.Status,
                    ContainerShipmentReferenceRules.DocumentStatusText((int)preLoading.Status),
                    preLoading.ContainerNo ?? string.Empty);

            default:
                var loadingList = await db.ContainerLoadingLists.AsNoTracking()
                    .FirstOrDefaultAsync(o => o.Id == sourceId);
                EnsureSourceEligible(
                    loadingList is not null, loadingList?.IsDeleted == true, sourceTypeText, sourceId);
                return new SourceSnapshot(loadingList!.Id, loadingList.LoadingListNo ?? string.Empty,
                    loadingList.LoadingDate, (int)loadingList.Status,
                    ContainerShipmentReferenceRules.DocumentStatusText((int)loadingList.Status),
                    loadingList.ContainerNo ?? string.Empty);
        }
    }

    /// <summary>源记录资格校验：不存在 → 404；已删除 → 规则冲突（不允许新登记引用）</summary>
    private static void EnsureSourceEligible(bool exists, bool deleted, string sourceTypeText, long sourceId)
    {
        var (eligible, text) = ContainerShipmentReferenceRules.EvaluateSourceEligibility(
            exists, deleted, sourceTypeText);
        if (eligible) return;
        if (!exists) throw BusinessException.NotFound($"{text}（Id={sourceId}）");
        throw BusinessException.RuleConflict($"{text}（Id={sourceId}）");
    }

    /// <summary>加载未删除的出运引用（跟踪实体，供写入路径使用）；不存在 → 数据不存在</summary>
    private static async Task<ContainerShipmentReference> LoadAsync(IErpDbContext db, long id)
        => await db.ContainerShipmentReferences.FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
           ?? throw BusinessException.NotFound($"出运引用不存在（Id={id}）");

    // ==================== 6. 台账 / 详情 / 修订留痕（只读，分页有界，批量装载） ====================

    /// <summary>
    /// 出运引用台账（分页，只读）：可按源记录类型 / Id、状态、出运方式、登记日期区间与关键字过滤；
    /// 默认包含已作废历史（证据保留可读）。单页内的源记录可用性、报关行可用性与修订条数
    /// 一律**批量装载**（固定数量查询），不产生逐行数据库访问。
    /// </summary>
    public static async Task<PagedResult<ContainerShipmentReferenceDto>> ListAsync(
        IErpDbContext db, ContainerShipmentReferenceQuery query)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(query);
        query.Normalize();

        var keyword = ContainerShipmentReferenceRules.NormalizeKeyword(query.Keyword);
        var status = ContainerShipmentReferenceRules.NormalizeStatusFilter(query.Status);
        var mode = ContainerShipmentReferenceRules.NormalizeModeFilter(query.ShipmentMode);
        var sourceType = string.IsNullOrWhiteSpace(query.SourceType)
            ? null
            : ContainerShipmentReferenceRules.NormalizeSourceType(query.SourceType);

        var source = db.ContainerShipmentReferences.AsNoTracking().Where(o => !o.IsDeleted);
        if (sourceType is not null) source = source.Where(o => o.SourceType == sourceType);
        if (query.SourceId is > 0) source = source.Where(o => o.SourceId == query.SourceId!.Value);
        if (status is not null) source = source.Where(o => o.Status == status.Value);
        if (mode is not null) source = source.Where(o => o.ShipmentMode == mode);
        if (query.RecordedDateFrom is not null)
            source = source.Where(o => o.RecordedAt >= query.RecordedDateFrom.Value);
        if (query.RecordedDateTo is not null)
        {
            var upperBound = query.RecordedDateTo.Value.Date.AddDays(1);
            source = source.Where(o => o.RecordedAt < upperBound);
        }
        if (keyword.Length > 0)
            source = source.Where(o => o.SourceNo.Contains(keyword)
                                       || o.ContainerNo.Contains(keyword)
                                       || o.BillOfLadingNo.Contains(keyword)
                                       || o.ShippingOrderNo.Contains(keyword)
                                       || o.CarrierName.Contains(keyword)
                                       || o.ForwarderName.Contains(keyword));

        var total = await source.CountAsync();
        var rows = await source.OrderByDescending(o => o.Id)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .ToListAsync();

        await AnnotateAsync(db, rows);

        return new PagedResult<ContainerShipmentReferenceDto>
        {
            Items = rows.Select(Map).ToList(),
            Total = total,
            Page = query.Page,
            PageSize = query.PageSize
        };
    }

    /// <summary>
    /// 出运引用详情（只读）：当前证据 + 有界修订留痕（按修订号倒序，最多
    /// <see cref="ContainerShipmentReferenceRules.MaxRevisionTake"/> 条，超出时显式说明被截断）。
    /// </summary>
    public static async Task<ContainerShipmentReferenceDetailDto> GetAsync(IErpDbContext db, long id)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (id <= 0) throw BusinessException.InvalidParameter("请指定要查看的出运引用");

        var row = await db.ContainerShipmentReferences.AsNoTracking()
            .FirstOrDefaultAsync(o => o.Id == id && !o.IsDeleted)
            ?? throw BusinessException.NotFound($"出运引用不存在（Id={id}）");

        await AnnotateAsync(db, new[] { row });
        var revisions = await ListRevisionsAsync(db, id, ContainerShipmentReferenceRules.MaxRevisionTake);
        var revisionCount = row.RevisionCount;

        return new ContainerShipmentReferenceDetailDto(
            Map(row),
            revisions,
            revisionCount,
            revisionCount > revisions.Count,
            ContainerShipmentReferenceRules.MaxRevisionTake,
            ContainerShipmentReferenceRules.RuleText + " "
                + ContainerShipmentReferenceRules.SourceLinkText + " "
                + ContainerShipmentReferenceRules.EvidenceText,
            ContainerShipmentReferenceRules.BoundaryText);
    }

    /// <summary>
    /// 修订留痕（只读、有界）：按修订号倒序返回最多 <paramref name="take"/> 条（默认
    /// <see cref="ContainerShipmentReferenceRules.MaxRevisionTake"/>）；留痕只追加，不提供修改与删除。
    /// </summary>
    public static async Task<List<ContainerShipmentReferenceRevisionDto>> ListRevisionsAsync(
        IErpDbContext db, long id, int take = ContainerShipmentReferenceRules.MaxRevisionTake)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (id <= 0) throw BusinessException.InvalidParameter("请指定要查看修订留痕的出运引用");

        var exists = await db.ContainerShipmentReferences.AsNoTracking()
            .AnyAsync(o => o.Id == id && !o.IsDeleted);
        if (!exists) throw BusinessException.NotFound($"出运引用不存在（Id={id}）");

        var limit = take <= 0 ? ContainerShipmentReferenceRules.MaxRevisionTake
            : Math.Min(take, ContainerShipmentReferenceRules.MaxRevisionTake);

        var rows = await db.ContainerShipmentReferenceRevisions.AsNoTracking()
            .Where(r => !r.IsDeleted && r.ContainerShipmentReferenceId == id)
            .OrderByDescending(r => r.RevisionNo).ThenByDescending(r => r.Id)
            .Take(limit)
            .ToListAsync();

        return rows.Select(MapRevision).ToList();
    }

    // ==================== 7. 源记录候选（只读、有界、显式选择） ====================

    /// <summary>
    /// 可登记出运引用的源记录候选（只读、有界）：只列出指定类型下**未删除**的既有记录，
    /// 标注是否已有有效出运引用与资格文案。
    /// <para>用于**显式选择**源记录：系统<strong>不</strong>按柜号 / 订单号 / 单证号等自由文本
    /// 自动挑选或匹配记录（与 ERP-040「未关联不猜引用」同一口径）；没有出运引用的历史记录
    /// 照常出现在候选中，不需要任何回填。</para>
    /// </summary>
    public static async Task<List<ContainerShipmentReferenceSourceCandidateDto>> ListSourceCandidatesAsync(
        IErpDbContext db, string? sourceType, string? keyword,
        int take = ContainerShipmentReferenceRules.MaxSourceCandidates)
    {
        ArgumentNullException.ThrowIfNull(db);

        var type = ContainerShipmentReferenceRules.NormalizeSourceType(sourceType);
        var filter = ContainerShipmentReferenceRules.NormalizeKeyword(keyword);
        var limit = take <= 0 ? ContainerShipmentReferenceRules.MaxSourceCandidates
            : Math.Min(take, ContainerShipmentReferenceRules.MaxSourceCandidates);

        if (type == ContainerShipmentReferenceRules.SourceTypeBooking)
        {
            var rows = await db.ContainerBookings.AsNoTracking()
                .Where(o => !o.IsDeleted)
                .Where(o => filter.Length == 0 || o.BookingNo.Contains(filter))
                .OrderByDescending(o => o.Id).Take(limit)
                .Select(o => new { o.Id, o.BookingNo, o.BookingDate, o.Status })
                .ToListAsync();

            var references = await LoadActiveReferenceMapAsync(db, type, rows.Select(r => r.Id).ToList());
            return rows.Select(r => BuildCandidate(type, r.Id, r.BookingNo, r.BookingDate,
                (int)r.Status, string.Empty, references)).ToList();
        }

        if (type == ContainerShipmentReferenceRules.SourceTypePreLoading)
        {
            var rows = await db.ContainerPreLoadings.AsNoTracking()
                .Where(o => !o.IsDeleted)
                .Where(o => filter.Length == 0 || o.PreLoadingNo.Contains(filter) || o.ContainerNo.Contains(filter))
                .OrderByDescending(o => o.Id).Take(limit)
                .Select(o => new { o.Id, o.PreLoadingNo, o.LoadingDate, o.Status, o.ContainerNo })
                .ToListAsync();

            var references = await LoadActiveReferenceMapAsync(db, type, rows.Select(r => r.Id).ToList());
            return rows.Select(r => BuildCandidate(type, r.Id, r.PreLoadingNo, r.LoadingDate,
                (int)r.Status, r.ContainerNo, references)).ToList();
        }

        var lists = await db.ContainerLoadingLists.AsNoTracking()
            .Where(o => !o.IsDeleted)
            .Where(o => filter.Length == 0 || o.LoadingListNo.Contains(filter) || o.ContainerNo.Contains(filter))
            .OrderByDescending(o => o.Id).Take(limit)
            .Select(o => new { o.Id, o.LoadingListNo, o.LoadingDate, o.Status, o.ContainerNo })
            .ToListAsync();

        var listReferences = await LoadActiveReferenceMapAsync(
            db, type, lists.Select(r => r.Id).ToList());
        return lists.Select(r => BuildCandidate(type, r.Id, r.LoadingListNo, r.LoadingDate,
            (int)r.Status, r.ContainerNo, listReferences)).ToList();
    }

    /// <summary>该类型下这些源记录的有效出运引用映射（源记录 Id → 引用 Id；一次查询，有界）</summary>
    private static async Task<Dictionary<long, long>> LoadActiveReferenceMapAsync(
        IErpDbContext db, string sourceType, List<long> sourceIds)
        => sourceIds.Count == 0
            ? new Dictionary<long, long>()
            : (await db.ContainerShipmentReferences.AsNoTracking()
                    .Where(r => !r.IsDeleted && r.Status == ContainerShipmentReferenceRules.StatusRecorded
                                && r.SourceType == sourceType && sourceIds.Contains(r.SourceId))
                    .Select(r => new { r.Id, r.SourceId })
                    .ToListAsync())
                .GroupBy(r => r.SourceId)
                .ToDictionary(g => g.Key, g => g.OrderBy(r => r.Id).First().Id);

    /// <summary>构造源记录候选（含是否已有有效引用与资格文案；候选自身一律可登记，只读关联）</summary>
    private static ContainerShipmentReferenceSourceCandidateDto BuildCandidate(
        string sourceType, long sourceId, string? sourceNo, DateTime sourceDate, int status, string? containerNo,
        Dictionary<long, long> activeReferences)
    {
        var sourceTypeText = ContainerShipmentReferenceRules.SourceTypeText(sourceType);
        var no = sourceNo ?? string.Empty;
        var referenced = activeReferences.TryGetValue(sourceId, out var existingReferenceId);

        var text = referenced
            ? $"{sourceTypeText}「{no}」已有有效出运引用（Id={existingReferenceId}）：请改为修订该引用，"
              + "或先作废它再重新登记（系统不会静默覆盖已有证据）"
            : $"{sourceTypeText}记录可登记出运引用（当前没有有效引用；登记只读关联，不会改写该记录）";

        return new ContainerShipmentReferenceSourceCandidateDto(
            sourceType, sourceId, no, sourceDate, status,
            ContainerShipmentReferenceRules.DocumentStatusText(status), containerNo ?? string.Empty,
            referenced, referenced ? existingReferenceId : null, true, text);
    }

    // ==================== 8. 读取标注与映射（批量装载，无逐行查询） ====================

    /// <summary>
    /// 为一批出运引用统一标注（固定数量查询，不产生逐行数据库访问）：修订留痕条数（一次分组查询）、
    /// 源记录可用性（按源记录类型各一次查询，最多 3 次）、报关行字典项可用性（一次查询），
    /// 并补齐状态 / 出运方式 / 边界文案。标注只是 <c>[NotMapped]</c> 读取值，不写库。
    /// </summary>
    private static async Task AnnotateAsync(IErpDbContext db, IList<ContainerShipmentReference> rows)
    {
        if (rows.Count == 0) return;

        var ids = rows.Select(r => r.Id).Distinct().ToList();

        var revisionCounts = await db.ContainerShipmentReferenceRevisions.AsNoTracking()
            .Where(r => !r.IsDeleted && ids.Contains(r.ContainerShipmentReferenceId))
            .GroupBy(r => r.ContainerShipmentReferenceId)
            .Select(g => new { ReferenceId = g.Key, Count = g.Count() })
            .ToListAsync();

        var availableSourceIds = new HashSet<(string Type, long Id)>();
        foreach (var group in rows.GroupBy(r => r.SourceType ?? string.Empty))
        {
            var type = group.Key;
            var sourceIds = group.Select(r => r.SourceId).Distinct().ToList();
            var found = type switch
            {
                ContainerShipmentReferenceRules.SourceTypeBooking => await db.ContainerBookings.AsNoTracking()
                    .Where(o => !o.IsDeleted && sourceIds.Contains(o.Id)).Select(o => o.Id).ToListAsync(),
                ContainerShipmentReferenceRules.SourceTypePreLoading => await db.ContainerPreLoadings.AsNoTracking()
                    .Where(o => !o.IsDeleted && sourceIds.Contains(o.Id)).Select(o => o.Id).ToListAsync(),
                _ => await db.ContainerLoadingLists.AsNoTracking()
                    .Where(o => !o.IsDeleted && sourceIds.Contains(o.Id)).Select(o => o.Id).ToListAsync()
            };
            foreach (var id in found) availableSourceIds.Add((type, id));
        }

        var brokerIds = rows.Where(r => r.CustomsBrokerId is > 0)
            .Select(r => r.CustomsBrokerId!.Value).Distinct().ToList();
        var brokers = brokerIds.Count == 0
            ? new List<BaseOtherInfo>()
            : await db.BaseOtherInfos.AsNoTracking().Where(o => brokerIds.Contains(o.Id)).ToListAsync();
        var brokerById = brokers.ToDictionary(o => o.Id);
        var countByReference = revisionCounts.ToDictionary(x => x.ReferenceId, x => x.Count);

        foreach (var row in rows)
        {
            var sourceType = row.SourceType ?? string.Empty;
            var sourceAvailable = availableSourceIds.Contains((sourceType, row.SourceId));
            row.SourceAvailable = sourceAvailable;
            row.SourceAvailabilityText = ContainerShipmentReferenceRules.SourceAvailabilityText(
                sourceAvailable, ContainerShipmentReferenceRules.SourceTypeText(sourceType));

            row.CustomsBrokerAvailable = row.CustomsBrokerId is not > 0
                || (brokerById.TryGetValue(row.CustomsBrokerId!.Value, out var broker)
                    && ContainerShipmentTrackingRules.IsSelectableCustomsBroker(broker));
            row.CustomsBrokerAvailabilityText = ContainerShipmentReferenceRules.CustomsBrokerAvailabilityText(
                row.CustomsBrokerAvailable, row.CustomsBrokerName);

            row.RevisionCount = countByReference.TryGetValue(row.Id, out var count) ? count : 0;
            row.StatusText = ContainerShipmentReferenceRules.StatusText(row.Status);
            row.ShipmentModeText = ContainerShipmentTrackingRules.ShipmentModeText(row.ShipmentMode);
            row.BoundaryText = ContainerShipmentReferenceRules.BoundaryText;
        }
    }

    /// <summary>单条引用的写入响应映射（标注口径与台账读取完全一致，不写库）</summary>
    private static async Task<ContainerShipmentReferenceDto> MapSingleAsync(
        IErpDbContext db, ContainerShipmentReference row)
    {
        await AnnotateAsync(db, new List<ContainerShipmentReference> { row });
        return Map(row);
    }

    /// <summary>出运引用实体 → DTO（纯映射，不写库）</summary>
    private static ContainerShipmentReferenceDto Map(ContainerShipmentReference row)
    {
        ArgumentNullException.ThrowIfNull(row);

        return new ContainerShipmentReferenceDto(
            row.Id,
            row.SourceType ?? string.Empty,
            ContainerShipmentReferenceRules.SourceTypeText(row.SourceType),
            row.SourceId,
            row.SourceNo ?? string.Empty,
            row.SourceDate,
            row.SourceStatus,
            string.IsNullOrEmpty(row.SourceStatusText)
                ? ContainerShipmentReferenceRules.DocumentStatusText(row.SourceStatus)
                : row.SourceStatusText,
            row.ContainerNo ?? string.Empty,
            ContainerShipmentTrackingRules.NormalizeText(row.ShipmentMode).ToUpperInvariant(),
            string.IsNullOrEmpty(row.ShipmentModeText)
                ? ContainerShipmentTrackingRules.ShipmentModeText(row.ShipmentMode)
                : row.ShipmentModeText,
            row.ShippingOrderNo ?? string.Empty,
            row.BillOfLadingNo ?? string.Empty,
            row.CarrierName ?? string.Empty,
            row.ForwarderName ?? string.Empty,
            row.DeparturePort ?? string.Empty,
            row.TransitPort ?? string.Empty,
            row.DestinationPort ?? string.Empty,
            row.PlannedDepartureAt,
            row.PlannedArrivalAt,
            row.TruckerName ?? string.Empty,
            row.CustomsBrokerId is > 0 ? row.CustomsBrokerId : null,
            row.CustomsBrokerName ?? string.Empty,
            row.Remark ?? string.Empty,
            row.Status,
            string.IsNullOrEmpty(row.StatusText)
                ? ContainerShipmentReferenceRules.StatusText(row.Status)
                : row.StatusText,
            row.Status == ContainerShipmentReferenceRules.StatusRecorded,
            row.Status == ContainerShipmentReferenceRules.StatusVoided,
            row.RevisionNo,
            row.RecordedAt,
            row.LastRevisedAt,
            row.LastRevisionReason ?? string.Empty,
            row.VoidedAt,
            row.VoidReason ?? string.Empty,
            row.RevisionCount,
            row.SourceAvailable,
            row.SourceAvailabilityText ?? string.Empty,
            row.CustomsBrokerAvailable,
            row.CustomsBrokerAvailabilityText ?? string.Empty,
            row.CreatedAt,
            row.UpdatedAt,
            string.IsNullOrEmpty(row.BoundaryText)
                ? ContainerShipmentReferenceRules.BoundaryText
                : row.BoundaryText);
    }

    /// <summary>修订留痕实体 → DTO（纯映射，不写库）</summary>
    private static ContainerShipmentReferenceRevisionDto MapRevision(ContainerShipmentReferenceRevision row)
    {
        ArgumentNullException.ThrowIfNull(row);

        return new ContainerShipmentReferenceRevisionDto(
            row.Id,
            row.ContainerShipmentReferenceId,
            row.RevisionNo,
            row.SourceType ?? string.Empty,
            row.SourceId,
            row.SourceNo ?? string.Empty,
            row.SupersededAt,
            row.Reason ?? string.Empty,
            ContainerShipmentTrackingRules.NormalizeText(row.ShipmentMode).ToUpperInvariant(),
            ContainerShipmentTrackingRules.ShipmentModeText(row.ShipmentMode),
            row.ShippingOrderNo ?? string.Empty,
            row.BillOfLadingNo ?? string.Empty,
            row.CarrierName ?? string.Empty,
            row.ForwarderName ?? string.Empty,
            row.DeparturePort ?? string.Empty,
            row.TransitPort ?? string.Empty,
            row.DestinationPort ?? string.Empty,
            row.PlannedDepartureAt,
            row.PlannedArrivalAt,
            row.TruckerName ?? string.Empty,
            row.CustomsBrokerId is > 0 ? row.CustomsBrokerId : null,
            row.CustomsBrokerName ?? string.Empty,
            row.Remark ?? string.Empty);
    }
}
