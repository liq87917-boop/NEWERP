using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace ERP.Application.Services;

/// <summary>
/// 装柜外贸与物流跟踪服务（ERP-040）。职责：
/// <list type="number">
/// <item><b>写入规范化</b>（<see cref="ApplyAsync"/>）：出运方式只接受 LCL / FCL / 未指定，文本去空白并校验长度，
/// 查验要求三态（<c>null</c> / <c>false</c> / <c>true</c>）原样落库，全部日期可空且**绝不推断**；</item>
/// <item><b>报关行引用</b>：新增 / 更换必须是「其他资料」中 <c>InfoType = CustomsBroker</c> 且启用、未删除的字典项，
/// 名称快照一律按字典项由服务端写回（客户端自由文本不被采信）；引用未变更时保留历史快照，
/// 字典项后来被停用 / 删除也不清空、不改写；</item>
/// <item><b>读取标注</b>（<see cref="AnnotateAsync"/>）：列表 / 详情读取时标注报关行引用是否仍可选用（一次查询解析全部引用）；</item>
/// <item><b>只读回显</b>（<see cref="ResolveForPreLoadingAsync"/> / <see cref="ResolveForLoadingListAsync"/>）：
/// 预装柜单 / 装柜清单按**持久化引用**取订柜信息并原样返回跟踪值；没有引用即为「未关联」（全字段未知），
/// <b>不</b>按柜号 / 提单号等自由文本匹配。</item>
/// </list>
/// <para>边界（重要）：本服务只读写订柜信息自身的上述字段 —— 不写费用、单证、库存与任何历史单据，
/// 也不调用船公司、海关、货代等外部跟踪系统。</para>
/// </summary>
public static class ContainerShipmentTrackingService
{
    /// <summary>
    /// 校验并落地订柜信息的跟踪字段（新增 / 更新共用）。
    /// </summary>
    /// <param name="db">数据访问上下文（只读报关行字典项）</param>
    /// <param name="booking">即将新增 / 更新的订柜信息（含客户端提交的跟踪字段）</param>
    /// <param name="stored">库中已存在的订柜信息（新增时为 <c>null</c>）；用于识别「报关行引用未变更」的历史引用</param>
    /// <remarks>
    /// <see cref="ContainerBooking.CustomsBrokerAvailable"/> 是 <c>[NotMapped]</c> 的读取标注，这里一并按服务端口径重算，
    /// 保证写入响应与后续读取的标注一致，且客户端提交的该标记一律不被采信。
    /// </remarks>
    public static async Task ApplyAsync(IErpDbContext db, ContainerBooking booking, ContainerBooking? stored)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(booking);

        booking.ShipmentMode = ContainerShipmentTrackingRules.NormalizeShipmentMode(booking.ShipmentMode);
        booking.BillOfLadingNo = Checked(booking.BillOfLadingNo,
            ContainerShipmentTrackingRules.BillOfLadingNoMaxLength, "提单号（B/L）");
        booking.ShippingOrderNo = Checked(booking.ShippingOrderNo,
            ContainerShipmentTrackingRules.ShippingOrderNoMaxLength, "订舱号（S/O）");
        booking.DeparturePort = Checked(booking.DeparturePort,
            ContainerShipmentTrackingRules.PortMaxLength, "起运港");
        booking.DestinationPort = Checked(booking.DestinationPort,
            ContainerShipmentTrackingRules.PortMaxLength, "目的港");
        booking.TransitPort = Checked(booking.TransitPort,
            ContainerShipmentTrackingRules.PortMaxLength, "中转港");
        booking.TruckerName = Checked(booking.TruckerName,
            ContainerShipmentTrackingRules.TruckerNameMaxLength, "拖车 / 集卡公司");

        // 查验要求：三态原样保留（null = 未知），只有「明确不需要查验 + 有查验日期」这种自相矛盾才拒绝
        ContainerShipmentTrackingRules.EnsureInspectionConsistency(booking.InspectionRequired, booking.InspectionDate);

        await ApplyCustomsBrokerAsync(db, booking, stored);
    }

    /// <summary>
    /// 报关行引用落地：<c>null</c> / <c>0</c> = 未指定（两字段一并清空，不影响任何其他单据）；
    /// 引用未变更时保留历史引用（字典项不可用时保留库中名称快照并标注不可用）；
    /// 新增 / 更换时必须是可选用字典项，名称快照由服务端权威写入。
    /// </summary>
    private static async Task ApplyCustomsBrokerAsync(IErpDbContext db, ContainerBooking booking, ContainerBooking? stored)
    {
        var requested = booking.CustomsBrokerId;

        // 1. 未指定 / 显式清空（0 与 null 同义）：两个字段一并清空
        if (requested is not > 0)
        {
            booking.CustomsBrokerId = null;
            booking.CustomsBrokerName = string.Empty;
            booking.CustomsBrokerAvailable = true;
            return;
        }

        var entry = await db.BaseOtherInfos.AsNoTracking()
            .FirstOrDefaultAsync(o => o.Id == requested!.Value);
        var unchanged = stored is not null && stored.CustomsBrokerId == requested;

        // 2. 引用未变更（老订柜单直接保存其他字段）：不迁移、不清空历史引用
        if (unchanged)
        {
            if (entry is not null && ContainerShipmentTrackingRules.IsSelectableCustomsBroker(entry))
            {
                // 字典项仍可选用 → 按字典项最新名称刷新快照（字典改名后订柜记录同步）
                booking.CustomsBrokerName = ContainerShipmentTrackingRules.SnapshotName(entry);
                booking.CustomsBrokerAvailable = true;
            }
            else
            {
                // 字典项已删除 / 停用 / 改类型 → 保留库中快照原样，绝不允许客户端借此改写历史名称
                booking.CustomsBrokerName = stored!.CustomsBrokerName;
                booking.CustomsBrokerAvailable = false;
            }
            return;
        }

        // 3. 新增 / 更换引用：必须是可选用字典项，否则拒绝
        ContainerShipmentTrackingRules.EnsureSelectableCustomsBroker(entry, requested.Value);
        booking.CustomsBrokerName = ContainerShipmentTrackingRules.SnapshotName(entry!);
        booking.CustomsBrokerAvailable = true;
    }

    /// <summary>文本去首尾空白 + 长度校验（超长拒绝，不静默截断）</summary>
    private static string Checked(string? raw, int maxLength, string fieldLabel)
    {
        var value = ContainerShipmentTrackingRules.NormalizeText(raw);
        ContainerShipmentTrackingRules.EnsureLength(value, maxLength, fieldLabel);
        return value;
    }

    // ==================== 报关行下拉选项（只读） ====================

    /// <summary>
    /// 读取报关行下拉可选项（只读，不写库）：只返回未删除、已启用、类型为 CustomsBroker 的字典项，
    /// 与写入校验口径完全一致，因此已停用 / 已删除 / 类型不符的字典项不会出现在下拉中。
    /// <para>历史引用（订柜记录上已有的名称快照）不依赖本方法：即使字典项已不可用，
    /// 列表 / 详情仍按 <see cref="ContainerBooking.CustomsBrokerName"/> 照常显示并标注不可用。</para>
    /// </summary>
    public static async Task<List<OtherInfoOptionDto>> LoadCustomsBrokerOptionsAsync(IErpDbContext db)
    {
        ArgumentNullException.ThrowIfNull(db);

        // 先由数据库收敛到「未删除且启用」的候选集，再在内存中按类型口径判定
        // （类型比较忽略大小写与首尾空白，与写入校验使用同一规则，避免大小写差异导致下拉缺项）
        var candidates = await db.BaseOtherInfos.AsNoTracking()
            .Where(o => !o.IsDeleted && o.Status == 1)
            .ToListAsync();

        return candidates
            .Where(ContainerShipmentTrackingRules.IsSelectableCustomsBroker)
            .OrderBy(o => o.SortOrder)
            .ThenBy(o => o.InfoName, StringComparer.Ordinal)
            .Select(o => new OtherInfoOptionDto(o.Id, o.InfoType, o.InfoCode,
                ContainerShipmentTrackingRules.SnapshotName(o), Selectable: true))
            .ToList();
    }

    // ==================== 读取标注（只读） ====================

    /// <summary>
    /// 为列表 / 详情返回的订柜信息标注报关行引用是否仍可选用（不写库）：
    /// 一次查询解析全部引用，不产生逐行查询。
    /// </summary>
    public static async Task AnnotateAsync(IErpDbContext db, IEnumerable<ContainerBooking> bookings)
    {
        ArgumentNullException.ThrowIfNull(db);
        var list = bookings as IList<ContainerBooking> ?? bookings.ToList();
        if (list.Count == 0) return;

        var ids = list.Where(b => b.CustomsBrokerId is > 0)
            .Select(b => b.CustomsBrokerId!.Value)
            .Distinct()
            .ToList();

        var entries = ids.Count == 0
            ? new List<BaseOtherInfo>()
            : await db.BaseOtherInfos.AsNoTracking().Where(o => ids.Contains(o.Id)).ToListAsync();
        var byId = entries.ToDictionary(o => o.Id);

        foreach (var booking in list)
        {
            if (booking.CustomsBrokerId is not > 0)
            {
                // 未指定报关行：字段保持空（不写「无」这类占位值），标注为可用（无可提示的失效引用）
                booking.CustomsBrokerId = null;
                booking.CustomsBrokerAvailable = true;
                continue;
            }

            booking.CustomsBrokerAvailable = byId.TryGetValue(booking.CustomsBrokerId.Value, out var entry)
                && ContainerShipmentTrackingRules.IsSelectableCustomsBroker(entry);
        }
    }

    // ==================== 只读回显（预装柜单 / 装柜清单） ====================

    /// <summary>
    /// 按订柜信息解析权威跟踪值（只读）：<paramref name="booking"/> 为 <c>null</c>
    /// （无持久化引用 / 引用指向的记录已不可读）时返回「未关联」投影。
    /// </summary>
    public static async Task<ContainerShipmentTrackingDto> ResolveAsync(IErpDbContext db, ContainerBooking? booking)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (booking is null) return NotLinked();
        return From(booking, await IsCustomsBrokerAvailableAsync(db, booking.CustomsBrokerId));
    }

    /// <summary>
    /// 预装柜单的权威跟踪值：按 <see cref="ContainerPreLoading.BookingId"/> 持久化引用读取；
    /// 无引用（或订柜记录已被删除）即返回「未关联」。
    /// </summary>
    public static async Task<ContainerShipmentTrackingDto> ResolveForPreLoadingAsync(IErpDbContext db, ContainerPreLoading preLoading)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(preLoading);
        return await ResolveAsync(db, await LoadBookingAsync(db, preLoading.BookingId));
    }

    /// <summary>
    /// 装柜清单的权威跟踪值：按 <see cref="ContainerLoadingList.PreLoadingId"/> → 预装柜单 → 订柜信息
    /// 的**持久化引用链**读取；链上任一环缺失即返回「未关联」，不按柜号等自由文本兜底匹配。
    /// </summary>
    public static async Task<ContainerShipmentTrackingDto> ResolveForLoadingListAsync(IErpDbContext db, ContainerLoadingList loadingList)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(loadingList);
        if (loadingList.PreLoadingId is not > 0) return NotLinked();

        var preLoading = await db.ContainerPreLoadings.AsNoTracking()
            .FirstOrDefaultAsync(o => o.Id == loadingList.PreLoadingId!.Value && !o.IsDeleted);
        return preLoading is null ? NotLinked() : await ResolveForPreLoadingAsync(db, preLoading);
    }

    private static async Task<ContainerBooking?> LoadBookingAsync(IErpDbContext db, long? bookingId) =>
        bookingId is not > 0
            ? null
            : await db.ContainerBookings.AsNoTracking()
                .FirstOrDefaultAsync(o => o.Id == bookingId!.Value && !o.IsDeleted);

    private static async Task<bool> IsCustomsBrokerAvailableAsync(IErpDbContext db, long? customsBrokerId)
    {
        if (customsBrokerId is not > 0) return true;   // 未指定报关行：无可提示的失效引用
        var entry = await db.BaseOtherInfos.AsNoTracking()
            .FirstOrDefaultAsync(o => o.Id == customsBrokerId!.Value);
        return ContainerShipmentTrackingRules.IsSelectableCustomsBroker(entry);
    }

    /// <summary>
    /// 按订柜记录构造只读投影（纯映射，不查库、不改写任何字段）：
    /// 持久化原值原样返回，空值 / null 一律保持「未知」语义（前端显示「未知」）。
    /// </summary>
    public static ContainerShipmentTrackingDto From(ContainerBooking booking, bool customsBrokerAvailable)
    {
        ArgumentNullException.ThrowIfNull(booking);
        return new ContainerShipmentTrackingDto
        {
            Linked = true,
            BookingId = booking.Id,
            BookingNo = booking.BookingNo ?? string.Empty,
            ShipmentMode = booking.ShipmentMode ?? string.Empty,
            ShipmentModeText = ContainerShipmentTrackingRules.ShipmentModeText(booking.ShipmentMode),
            BillOfLadingNo = booking.BillOfLadingNo ?? string.Empty,
            ShippingOrderNo = booking.ShippingOrderNo ?? string.Empty,
            DeparturePort = booking.DeparturePort ?? string.Empty,
            DestinationPort = booking.DestinationPort ?? string.Empty,
            TransitPort = booking.TransitPort ?? string.Empty,
            Etd = booking.Etd,
            Eta = booking.Eta,
            Atd = booking.Atd,
            Ata = booking.Ata,
            TruckerName = booking.TruckerName ?? string.Empty,
            CustomsBrokerId = booking.CustomsBrokerId,
            CustomsBrokerName = booking.CustomsBrokerName ?? string.Empty,
            CustomsBrokerAvailable = customsBrokerAvailable,
            InspectionRequired = booking.InspectionRequired,
            InspectionRequiredText = ContainerShipmentTrackingRules.InspectionRequiredText(booking.InspectionRequired),
            InspectionDate = booking.InspectionDate,
            CustomsReleaseDate = booking.CustomsReleaseDate,
            NotLinkedReason = string.Empty
        };
    }

    /// <summary>
    /// 未关联（没有持久化订柜引用）投影：所有跟踪字段保持空 / <c>null</c>，且不臆造任何取值；
    /// 前端据此显示「未知 / 未关联」，而不是按柜号等自由文本猜一条订柜记录。
    /// </summary>
    public static ContainerShipmentTrackingDto NotLinked() => new()
    {
        Linked = false,
        ShipmentModeText = ContainerShipmentTrackingRules.UnknownText,
        InspectionRequiredText = ContainerShipmentTrackingRules.UnknownText,
        NotLinkedReason = "未关联订柜信息：没有持久化的订柜引用，跟踪字段一律显示「未知」；不会按柜号等自由文本匹配订柜记录"
    };
}
