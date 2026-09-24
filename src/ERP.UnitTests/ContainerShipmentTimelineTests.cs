using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Reflection;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// 装柜出运证据时间线与跟踪工作台单元测试（ERP-059）。覆盖：装柜三单详情按显式源记录呈现 ERP-057 出运引用证据
/// 与 ERP-058 有效里程碑时间线（**计划与实际分开标注**）、缺失事件显示「无 / 未知」且绝不推断为已开船 / 已到港 /
/// 已清关 / 延误 / 逾期、时间差仅在两条持久化时间戳可比时给出并标注为算术证据（保留日期时间语义）、
/// 已作废里程碑从有效时间线排除但历史视图可读（有界且显式说明截断）、未登记有效引用的「未关联」详情与
/// 无效链接的显式标注（绝不改派、不做任何回填）、工作台显式字段筛选与分页有界（不按文本合并记录）、
/// 未知筛选取值拒绝、固定数量数据集访问（无逐行查库、只读不写库）、相邻业务记录非变更、纯规则文案，
/// 以及前端接线 / 路由契约与 ERP-059 审计结论（只读派生、无新增表与结构变更）。
/// 全部使用内存库（TestDbFactory），不连接 SQL Server、不执行任何 SQL / 部署脚本、不联系任何外部系统，
/// 不做任何浏览器 / UI 验收（浏览器验收按项目策略延后到 FINAL-UI-ACCEPTANCE）。
/// </summary>
public class ContainerShipmentTimelineTests
{
    // ==================== 0. 测试脚手架 ====================

    private static readonly DateTime PlannedDepartureAt = new(2026, 9, 10, 8, 0, 0);
    private static readonly DateTime PlannedArrivalAt = new(2026, 9, 25, 8, 0, 0);
    private static readonly DateTime ActualDepartureAt = new(2026, 9, 12, 10, 30, 0);
    private static readonly DateTime ActualArrivalAt = new(2026, 9, 27, 6, 15, 0);

    private static ContainerShipmentTimelineController BuildController(ErpDbContext db) => new(db);

    private static ContainerBooking SeedBooking(
        ErpDbContext db, string bookingNo, string containerNo = "CONT-059",
        DocumentStatus status = DocumentStatus.Pending, bool deleted = false,
        long customerId = 100)
    {
        var booking = new ContainerBooking
        {
            BookingNo = bookingNo,
            BookingDate = new DateTime(2026, 9, 1),
            CustomerId = customerId,
            ContainerType = ContainerType.GP40,
            ShippingCompany = "COSCO",
            DeparturePort = "NINGBO",
            DestinationPort = "HAMBURG",
            ShipmentMode = "FCL",
            Etd = PlannedDepartureAt,
            Eta = PlannedArrivalAt,
            Status = status,
            Remark = "订柜备注",
            IsDeleted = deleted
        };
        db.ContainerBookings.Add(booking);
        db.SaveChanges();
        return booking;
    }

    private static ContainerPreLoading SeedPreLoading(
        ErpDbContext db, string no, string containerNo = "CONT-059", long? bookingId = null,
        DocumentStatus status = DocumentStatus.Pending, bool deleted = false)
    {
        var preLoading = new ContainerPreLoading
        {
            PreLoadingNo = no,
            LoadingDate = new DateTime(2026, 9, 3),
            BookingId = bookingId,
            ContainerNo = containerNo,
            SealNo = "SEAL-059",
            TotalCartons = 100,
            Status = status,
            Remark = "预装柜备注",
            IsDeleted = deleted
        };
        db.ContainerPreLoadings.Add(preLoading);
        db.SaveChanges();
        return preLoading;
    }

    private static ContainerLoadingList SeedLoadingList(
        ErpDbContext db, string no, string containerNo = "CONT-059", long? preLoadingId = null,
        DocumentStatus status = DocumentStatus.Pending, bool deleted = false, long customerId = 100)
    {
        var loadingList = new ContainerLoadingList
        {
            LoadingListNo = no,
            PreLoadingId = preLoadingId,
            LoadingDate = new DateTime(2026, 9, 5),
            ContainerNo = containerNo,
            CustomerId = customerId,
            ShippingMark = "MARK-059",
            TotalCartons = 100,
            Status = status,
            Remark = "装柜清单备注",
            IsDeleted = deleted
        };
        db.ContainerLoadingLists.Add(loadingList);
        db.SaveChanges();
        return loadingList;
    }

    /// <summary>ERP-057 出运引用（时间线的计划值来源；本模块只读它，绝不改写）</summary>
    private static ContainerShipmentReference SeedReference(
        ErpDbContext db, long sourceId,
        string sourceType = ContainerShipmentReferenceRules.SourceTypeBooking,
        string sourceNo = "DG-059", string containerNo = "CONT-059",
        string destinationPort = "HAMBURG", string billOfLadingNo = "BL-059",
        DateTime? plannedDepartureAt = null, DateTime? plannedArrivalAt = null,
        int status = ContainerShipmentReferenceRules.StatusRecorded, bool deleted = false,
        string voidReason = "")
    {
        var reference = new ContainerShipmentReference
        {
            SourceType = sourceType,
            SourceId = sourceId,
            SourceNo = sourceNo,
            SourceDate = new DateTime(2026, 9, 1),
            SourceStatus = (int)DocumentStatus.Pending,
            SourceStatusText = "待提交",
            ContainerNo = containerNo,
            ShipmentMode = "FCL",
            ShippingOrderNo = "SO-059",
            BillOfLadingNo = billOfLadingNo,
            CarrierName = "COSCO",
            ForwarderName = "FORWARDER-059",
            DeparturePort = "NINGBO",
            TransitPort = "SINGAPORE",
            DestinationPort = destinationPort,
            PlannedDepartureAt = plannedDepartureAt ?? PlannedDepartureAt,
            PlannedArrivalAt = plannedArrivalAt ?? PlannedArrivalAt,
            TruckerName = "TRUCKER-059",
            Remark = "出运引用备注",
            Status = status,
            VoidReason = voidReason,
            VoidedAt = status == ContainerShipmentReferenceRules.StatusVoided
                ? new DateTime(2026, 9, 20, 9, 0, 0)
                : null,
            RecordedAt = new DateTime(2026, 9, 2, 9, 0, 0),
            RevisionNo = 1,
            IsDeleted = deleted
        };
        db.ContainerShipmentReferences.Add(reference);
        db.SaveChanges();
        return reference;
    }

    /// <summary>ERP-058 里程碑证据（实际事件来源；本模块只读它，绝不改写）</summary>
    private static ContainerShipmentMilestone SeedMilestone(
        ErpDbContext db, long referenceId,
        string eventType = ContainerShipmentMilestoneRules.EventTypeActualDeparture,
        DateTime? eventAt = null, int status = ContainerShipmentMilestoneRules.StatusRecorded,
        string sourceDescription = "", string notes = "", string recordedBy = "",
        DateTime? voidedAt = null, string voidReason = "", bool deleted = false)
    {
        var milestone = new ContainerShipmentMilestone
        {
            ContainerShipmentReferenceId = referenceId,
            EventType = eventType,
            EventAt = eventAt ?? ActualDepartureAt,
            SourceDescription = sourceDescription,
            Notes = notes,
            RecordedBy = recordedBy,
            RecordedAt = new DateTime(2026, 9, 12, 11, 0, 0),
            Status = status,
            VoidedAt = voidedAt,
            VoidReason = voidReason,
            CreatedAt = new DateTime(2026, 9, 12, 11, 0, 0),
            IsDeleted = deleted
        };
        db.ContainerShipmentMilestones.Add(milestone);
        db.SaveChanges();
        return milestone;
    }

    /// <summary>断言成功响应并取出数据（业务码必须为 0）</summary>
    private static T AssertOk<T>(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<ApiResponse<T>>(ok.Value);
        Assert.Equal(ErrorCodes.Success, resp.Code);
        Assert.NotNull(resp.Data);
        return resp.Data!;
    }

    /// <summary>断言业务异常的错误码（避免只断言消息文案）</summary>
    private static async Task<BusinessException> AssertBusinessAsync(int expectedCode, Func<Task> action)
    {
        var ex = await Assert.ThrowsAsync<BusinessException>(action);
        Assert.Equal(expectedCode, ex.Code);
        return ex;
    }

    /// <summary>按仓库根目录拼接文件的绝对路径（与其它契约测试口径一致）</summary>
    private static string RepoFile(params string[] segments)
        => Path.GetFullPath(Path.Combine(
            new[] { AppContext.BaseDirectory, "..", "..", "..", "..", ".." }.Concat(segments).ToArray()));

    private static async Task<ContainerShipmentTimelineDetailDto> TimelineAsync(
        ContainerShipmentTimelineController controller, string sourceType, long sourceId)
        => AssertOk<ContainerShipmentTimelineDetailDto>(await controller.GetForSource(sourceType, sourceId));

    private static async Task<PagedResult<ContainerShipmentTimelineShipmentDto>> WorkspaceAsync(
        ContainerShipmentTimelineController controller, ContainerShipmentTimelineQuery? query = null)
        => AssertOk<PagedResult<ContainerShipmentTimelineShipmentDto>>(
            await controller.GetPaged(query ?? new ContainerShipmentTimelineQuery()));

    // ==================== 1. 装柜三单详情：显式源记录 + 计划 / 实际分开标注 ====================

    [Fact]
    public async Task 装柜三单详情按显式源记录展示出运引用证据与有效时间线_计划与实际分开标注()
    {
        using var db = TestDbFactory.Create();
        var booking = SeedBooking(db, "DG-059-A");
        var preLoading = SeedPreLoading(db, "YZ-059-A", bookingId: booking.Id);
        var loadingList = SeedLoadingList(db, "ZJ-059-A", preLoadingId: preLoading.Id);

        var bookingReference = SeedReference(db, booking.Id, sourceNo: "DG-059-A");
        var preLoadingReference = SeedReference(db, preLoading.Id,
            ContainerShipmentReferenceRules.SourceTypePreLoading, sourceNo: "YZ-059-A");
        var loadingListReference = SeedReference(db, loadingList.Id,
            ContainerShipmentReferenceRules.SourceTypeLoadingList, sourceNo: "ZJ-059-A");

        SeedMilestone(db, bookingReference.Id,
            ContainerShipmentMilestoneRules.EventTypeActualDeparture, ActualDepartureAt,
            sourceDescription: "船公司网站截图", notes: "首个航次", recordedBy: "张三");
        SeedMilestone(db, bookingReference.Id,
            ContainerShipmentMilestoneRules.EventTypeInspection, new DateTime(2026, 9, 14, 9, 0, 0),
            sourceDescription: "报关行通知");
        var controller = BuildController(db);

        foreach (var (type, id, typeText, sourceNo) in new[]
        {
            (ContainerShipmentReferenceRules.SourceTypeBooking, booking.Id, "订柜信息", "DG-059-A"),
            (ContainerShipmentReferenceRules.SourceTypePreLoading, preLoading.Id, "预装柜单", "YZ-059-A"),
            (ContainerShipmentReferenceRules.SourceTypeLoadingList, loadingList.Id, "装柜清单", "ZJ-059-A"),
        })
        {
            var detail = await TimelineAsync(controller, type, id);

            Assert.True(detail.Shipment.Linked);
            Assert.Equal(typeText, detail.Shipment.SourceTypeText);
            Assert.Equal(sourceNo, detail.Shipment.SourceNo);
            Assert.True(detail.Shipment.ReferenceAvailable);
            Assert.True(detail.Shipment.SourceAvailable);
            Assert.Contains("只读关联", detail.Shipment.SourceAvailabilityText);

            // 计划条目：两条固定条目，时间来自出运引用的持久化计划值，明确标注为「计划」
            var planned = detail.Events.Where(e => e.IsPlanned).ToList();
            Assert.Equal(2, planned.Count);
            Assert.All(planned, e => Assert.False(e.IsActual));
            Assert.Equal(PlannedDepartureAt, planned[0].EventAt);
            Assert.Equal(PlannedArrivalAt, planned[1].EventAt);
            Assert.Contains("计划开船", planned[0].KindText);
            Assert.Contains("计划到港", planned[1].KindText);
            Assert.Contains("ERP-057", planned[0].SourceLabelText);
        }

        var bookingDetail = await TimelineAsync(
            controller, ContainerShipmentReferenceRules.SourceTypeBooking, booking.Id);
        var preLoadingDetail = await TimelineAsync(
            controller, ContainerShipmentReferenceRules.SourceTypePreLoading, preLoading.Id);
        var loadingListDetail = await TimelineAsync(
            controller, ContainerShipmentReferenceRules.SourceTypeLoadingList, loadingList.Id);

        // 三个模块各自取自己的显式引用（不串台、不按文本合并）
        Assert.Equal(bookingReference.Id, bookingDetail.Shipment.ReferenceId);
        Assert.Equal(preLoadingReference.Id, preLoadingDetail.Shipment.ReferenceId);
        Assert.Equal(loadingListReference.Id, loadingListDetail.Shipment.ReferenceId);

        // 实际条目：只含已登记（有效）里程碑，按事件时间升序，与计划条目分开标注
        var actual = bookingDetail.Events.Where(e => e.IsActual).ToList();
        Assert.Equal(2, actual.Count);
        Assert.All(actual, e => Assert.False(e.IsPlanned));
        Assert.Equal(ActualDepartureAt, actual[0].EventAt);
        Assert.Contains("实际开船", actual[0].KindText);
        Assert.Contains("ERP-058", actual[0].SourceLabelText);
        Assert.Equal("船公司网站截图", actual[0].SourceDescription);
        Assert.Equal("张三", actual[0].RecordedBy);
        Assert.Contains("查验", actual[1].KindText);
        Assert.Contains("不是海关决定", actual[1].EvidenceCategoryText);
        Assert.Equal(2, bookingDetail.ActiveEventCount);
        Assert.Equal(0, bookingDetail.HistoryCount);
        Assert.Empty(bookingDetail.HistoryEvents);
        Assert.False(bookingDetail.ActiveEventsTruncated);

        // 没有里程碑的预装柜单：实际事件显示「无（未登记）」，不需要任何回填
        Assert.DoesNotContain(preLoadingDetail.Events, e => e.IsActual);
        Assert.Contains("无（未登记）", preLoadingDetail.Shipment.Summary.TimelineStateText);
        Assert.Contains("ERP-057", preLoadingDetail.PlannedVersusActualText);
    }

    // ==================== 2. 缺失事件：显示「无 / 未知」，绝不推断业务状态 ====================

    [Fact]
    public async Task 缺失实际事件显示无未登记_计划时间已过也不变成已开船到港清关延误逾期()
    {
        using var db = TestDbFactory.Create();
        var booking = SeedBooking(db, "DG-059-B");
        var reference = SeedReference(db, booking.Id, sourceNo: "DG-059-B",
            plannedDepartureAt: new DateTime(2026, 1, 5, 8, 0, 0),     // 早已过期
            plannedArrivalAt: new DateTime(2026, 1, 20, 8, 0, 0));
        var controller = BuildController(db);

        var detail = await TimelineAsync(controller, ContainerShipmentReferenceRules.SourceTypeBooking, booking.Id);

        Assert.Equal(reference.Id, detail.Shipment.ReferenceId);
        Assert.DoesNotContain(detail.Events, e => e.IsActual);
        Assert.Equal(0, detail.ActiveEventCount);
        Assert.Empty(detail.HistoryEvents);

        // 计划条目照常显示（来自持久化计划值），但实际事件一条都没有
        var planned = detail.Events.Where(e => e.IsPlanned).ToList();
        Assert.Equal(2, planned.Count);
        Assert.All(planned, e => Assert.True(e.HasTimestamp));

        // 汇总：所有「最近实际事件」保持 null，并按「无（未登记）」说明
        Assert.Null(detail.Shipment.Summary.LatestActualDepartureAt);
        Assert.Null(detail.Shipment.Summary.LatestActualArrivalAt);
        Assert.Null(detail.Shipment.Summary.LatestInspectionAt);
        Assert.Null(detail.Shipment.Summary.LatestCustomsReleaseAt);
        Assert.Contains("无（未登记）", detail.Shipment.Summary.TimelineStateText);
        Assert.Contains("不是已开船", detail.Shipment.Summary.TimelineStateText);

        // 时间线条目上不存在任何「已开船 / 已到港 / 已清关 / 已放行 / 延误 / 逾期」结论
        var forbidden = new[] { "已开船", "已到港", "已清关", "已放行", "延误", "逾期" };
        foreach (var entry in detail.Events)
        {
            var text = entry.KindText + entry.StatusText + entry.SourceLabelText + entry.EvidenceCategoryText;
            Assert.DoesNotContain(forbidden, word => text.Contains(word, StringComparison.Ordinal));
        }
        Assert.Contains("不产生任何业务状态", detail.NoStatusInferenceText);

        // 时间差：两侧都缺实际事件 → 无法比较，且显示「无（未登记）」
        Assert.All(detail.Variances, v => Assert.False(v.Comparable));
        Assert.All(detail.Variances, v => Assert.Contains("缺少实际事件", v.Text));
        Assert.All(detail.Variances, v => Assert.Contains("无（未登记）", v.ActualAtText));
        Assert.All(detail.Variances, v => Assert.Contains("算术", v.BasisText));
    }

    // ==================== 3. 部分时间线：时间差按缺失侧说明 ====================

    [Fact]
    public async Task 部分时间线只登记开船时到港仍显示无且时间差按缺失侧说明()
    {
        using var db = TestDbFactory.Create();
        var booking = SeedBooking(db, "DG-059-C");
        var reference = SeedReference(db, booking.Id, sourceNo: "DG-059-C");
        SeedMilestone(db, reference.Id,
            ContainerShipmentMilestoneRules.EventTypeActualDeparture, ActualDepartureAt);
        var controller = BuildController(db);

        var detail = await TimelineAsync(controller, ContainerShipmentReferenceRules.SourceTypeBooking, booking.Id);

        var only = Assert.Single(detail.Events.Where(e => e.IsActual));
        Assert.Equal(ContainerShipmentMilestoneRules.EventTypeActualDeparture, only.EventType);

        var departure = detail.Variances.Single(v => v.Kind == ContainerShipmentTimelineRules.VarianceKindDeparture);
        var arrival = detail.Variances.Single(v => v.Kind == ContainerShipmentTimelineRules.VarianceKindArrival);

        // 开船：计划与实际都存在 → 可比，且按算术证据标注（保留两侧日期时间语义）
        Assert.True(departure.Comparable);
        Assert.Equal(ActualDepartureAt, departure.ActualAt);
        Assert.Contains("算术证据", departure.Text);
        Assert.Contains("开船时间差", departure.Label);
        Assert.Equal(ContainerShipmentTimelineRules.TimestampKindText(ActualDepartureAt), departure.ActualKindText);
        Assert.Contains("最近一条有效", departure.ActualLabel);

        // 到港：只有计划值 → 无法比较，明确说明缺的是实际事件且显示「无（未登记）」
        Assert.False(arrival.Comparable);
        Assert.Null(arrival.ActualAt);
        Assert.Contains("缺少实际事件", arrival.Text);
        Assert.Contains("无（未登记）", arrival.ActualAtText);
        Assert.Contains("无（未登记）", arrival.ActualEvidenceText);

        // 汇总只报告登记情况：已登记开船、未登记到港 / 查验 / 放行
        Assert.Equal(ActualDepartureAt, detail.Shipment.Summary.LatestActualDepartureAt);
        Assert.Null(detail.Shipment.Summary.LatestActualArrivalAt);
        Assert.Contains("实际开船", detail.Shipment.Summary.TimelineStateText);
        Assert.Contains("未登记", detail.Shipment.Summary.TimelineStateText);
    }

    // ==================== 4. 时间差：可比才给值，保留日期时间语义、标注为算术证据 ====================

    [Fact]
    public async Task 时间差仅在两条持久化时间戳可比时给出_保留日期时间语义且标注为算术证据()
    {
        var later = ContainerShipmentTimelineRules.DescribeVariance(
            "开船时间差", new DateTime(2026, 9, 10, 8, 0, 0), new DateTime(2026, 9, 12, 10, 30, 0));
        Assert.True(later.Comparable);
        Assert.Contains("实际比计划晚 2 天 2 小时 30 分钟", later.Text);
        Assert.Contains("算术证据", later.Text);
        Assert.Contains("不是延误 / 逾期结论", later.Text);

        var earlier = ContainerShipmentTimelineRules.DescribeVariance(
            "到港时间差", new DateTime(2026, 9, 25, 8, 0, 0), new DateTime(2026, 9, 24, 6, 0, 0));
        Assert.True(earlier.Comparable);
        Assert.Contains("实际比计划早 1 天 2 小时 0 分钟", earlier.Text);

        var same = ContainerShipmentTimelineRules.DescribeVariance(
            "开船时间差", PlannedDepartureAt, PlannedDepartureAt);
        Assert.True(same.Comparable);
        Assert.Contains("实际与计划一致", same.Text);

        // 任一侧缺失：显式说明缺的是哪一侧，绝不推断
        var missingPlanned = ContainerShipmentTimelineRules.DescribeVariance("开船时间差", null, ActualDepartureAt);
        Assert.False(missingPlanned.Comparable);
        Assert.Contains("缺少计划时间", missingPlanned.Text);

        var missingBoth = ContainerShipmentTimelineRules.DescribeVariance("开船时间差", null, null);
        Assert.False(missingBoth.Comparable);
        Assert.Contains("都没有登记", missingBoth.Text);

        // 两侧日期时间语义不一致：不做时区换算、不假定可比
        var mixed = ContainerShipmentTimelineRules.DescribeVariance(
            "开船时间差",
            new DateTime(2026, 9, 10, 8, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 9, 12, 8, 0, 0, DateTimeKind.Unspecified));
        Assert.False(mixed.Comparable);
        Assert.Contains("日期时间语义不一致", mixed.Text);
        Assert.Contains("不做时区换算", mixed.Text);

        // 时间差文案口径（算术证据 / 不做时区换算 / 不推断缺失一侧）
        Assert.Contains("算术证据", ContainerShipmentTimelineRules.VarianceBasisText);
        Assert.Contains("不换算时区", ContainerShipmentTimelineRules.VarianceBasisText);
        Assert.Contains("不推断缺失的一侧", ContainerShipmentTimelineRules.VarianceBasisText);

        // 端到端：两侧持久化语义不一致时同样「无法比较」，且不给出任何差值
        using var db = TestDbFactory.Create();
        var booking = SeedBooking(db, "DG-059-D");
        var reference = SeedReference(db, booking.Id, sourceNo: "DG-059-D",
            plannedDepartureAt: new DateTime(2026, 9, 10, 8, 0, 0, DateTimeKind.Utc));
        SeedMilestone(db, reference.Id, ContainerShipmentMilestoneRules.EventTypeActualDeparture,
            new DateTime(2026, 9, 12, 8, 0, 0, DateTimeKind.Unspecified));
        var controller = BuildController(db);

        var detail = await TimelineAsync(controller, ContainerShipmentReferenceRules.SourceTypeBooking, booking.Id);
        var departure = detail.Variances.Single(v => v.Kind == ContainerShipmentTimelineRules.VarianceKindDeparture);

        Assert.False(departure.Comparable);
        Assert.Contains("日期时间语义不一致", departure.Text);
        Assert.DoesNotContain("实际比计划", departure.Text);
        Assert.NotNull(departure.ActualAt);
        Assert.Contains("UTC", departure.PlannedKindText);
        Assert.Contains("Unspecified", departure.ActualKindText);
    }

    // ==================== 5. 已作废里程碑：从有效时间线排除，历史视图可读且不静默截断 ====================

    [Fact]
    public async Task 已作废里程碑从有效时间线排除但历史视图可读_原始值与原因保留且有界()
    {
        using var db = TestDbFactory.Create();
        var booking = SeedBooking(db, "DG-059-E");
        var reference = SeedReference(db, booking.Id, sourceNo: "DG-059-E");
        SeedMilestone(db, reference.Id,
            ContainerShipmentMilestoneRules.EventTypeActualDeparture, ActualDepartureAt,
            sourceDescription: "船公司网站截图");
        SeedMilestone(db, reference.Id,
            ContainerShipmentMilestoneRules.EventTypeInspection, new DateTime(2026, 9, 14, 9, 0, 0));
        SeedMilestone(db, reference.Id,
            ContainerShipmentMilestoneRules.EventTypeCustomsRelease, new DateTime(2026, 9, 30, 9, 0, 0));
        var voided = SeedMilestone(db, reference.Id,
            ContainerShipmentMilestoneRules.EventTypeActualArrival, ActualArrivalAt,
            status: ContainerShipmentMilestoneRules.StatusVoided,
            sourceDescription: "货代邮件", notes: "误录", recordedBy: "李四",
            voidedAt: new DateTime(2026, 9, 28, 10, 0, 0), voidReason: "时间录错，已重新登记");
        var controller = BuildController(db);

        var detail = AssertOk<ContainerShipmentTimelineDetailDto>(await controller.GetForReference(reference.Id));

        // 有效时间线只含计划条目 + 已登记（有效）证据：已作废条目不在其中
        var actual = detail.Events.Where(e => e.IsActual).ToList();
        Assert.Equal(3, actual.Count);
        Assert.DoesNotContain(actual, e => e.MilestoneId == voided.Id);
        Assert.Equal(3, detail.ActiveEventCount);
        Assert.Equal(1, detail.HistoryCount);

        // 历史视图按原值保留：事件类型 / 时间 / 来源 / 备注 / 记录人 / 作废时间与原因
        var history = Assert.Single(detail.HistoryEvents);
        Assert.Equal(voided.Id, history.MilestoneId);
        Assert.True(history.IsVoided);
        Assert.False(history.IsPlanned);
        Assert.True(history.IsActual);
        Assert.Equal(ActualArrivalAt, history.EventAt);
        Assert.Equal("货代邮件", history.SourceDescription);
        Assert.Equal("误录", history.Notes);
        Assert.Equal("李四", history.RecordedBy);
        Assert.Equal("时间录错，已重新登记", history.VoidReason);
        Assert.Equal(new DateTime(2026, 9, 28, 10, 0, 0), history.VoidedAt);
        Assert.Contains("已作废", history.StatusText);

        // 已作废证据不参与有效时间线、汇总与时间差
        Assert.Equal(ActualDepartureAt, detail.Shipment.Summary.LatestActualDepartureAt);
        Assert.Null(detail.Shipment.Summary.LatestActualArrivalAt);
        Assert.Contains("已作废（历史视图）1 条", detail.Shipment.Summary.TimelineStateText);
        var arrival = detail.Variances.Single(v => v.Kind == ContainerShipmentTimelineRules.VarianceKindArrival);
        Assert.False(arrival.Comparable);
        Assert.Contains("缺少实际事件", arrival.Text);

        // 历史视图有界：超出上限时显式说明截断，绝不静默丢弃
        SeedMilestone(db, reference.Id, ContainerShipmentMilestoneRules.EventTypeActualArrival,
            ActualArrivalAt.AddHours(2), status: ContainerShipmentMilestoneRules.StatusVoided,
            voidedAt: new DateTime(2026, 9, 28, 11, 0, 0), voidReason: "第二条历史");
        SeedMilestone(db, reference.Id, ContainerShipmentMilestoneRules.EventTypeActualArrival,
            ActualArrivalAt.AddHours(4), status: ContainerShipmentMilestoneRules.StatusVoided,
            voidedAt: new DateTime(2026, 9, 28, 12, 0, 0), voidReason: "第三条历史");

        var truncated = AssertOk<ContainerShipmentTimelineDetailDto>(
            await controller.GetForReference(reference.Id, includeHistory: true, historyTake: 2));
        Assert.Equal(3, truncated.HistoryCount);
        Assert.Equal(2, truncated.HistoryEvents.Count);
        Assert.True(truncated.HistoryTruncated);
        Assert.Equal(2, truncated.HistoryTakeLimit);
        Assert.True(truncated.HistoryTakeLimit < truncated.HistoryCount);   // 截断有界且可见
        Assert.Equal(truncated.HistoryTakeLimit, ContainerShipmentTimelineRules.NormalizeHistoryTake(2));

        // 显式不取历史时：有效时间线不受影响，历史条数仍照实统计
        var withoutHistory = AssertOk<ContainerShipmentTimelineDetailDto>(
            await controller.GetForReference(reference.Id, includeHistory: false));
        Assert.Empty(withoutHistory.HistoryEvents);
        Assert.False(withoutHistory.HistoryTruncated);
        Assert.Equal(3, withoutHistory.HistoryCount);
        Assert.Equal(3, withoutHistory.ActiveEventCount);
    }

    // ==================== 6. 历史异常事件类型 / 状态：照实可读，不静默修正 ====================

    [Fact]
    public async Task 历史异常事件类型与状态照实可读_不静默修正也不进入有效时间线()
    {
        using var db = TestDbFactory.Create();
        var booking = SeedBooking(db, "DG-059-F");
        var reference = SeedReference(db, booking.Id, sourceNo: "DG-059-F");
        var anomaly = SeedMilestone(db, reference.Id,
            eventType: "customs-detention", eventAt: new DateTime(2026, 9, 16, 9, 0, 0),
            status: 9, sourceDescription: "历史来源");
        SeedMilestone(db, reference.Id,
            ContainerShipmentMilestoneRules.EventTypeActualDeparture, ActualDepartureAt);
        var controller = BuildController(db);

        var detail = AssertOk<ContainerShipmentTimelineDetailDto>(await controller.GetForReference(reference.Id));

        // 未知状态不进入有效时间线（有效口径只认已登记状态）
        Assert.Equal(1, detail.ActiveEventCount);
        Assert.DoesNotContain(detail.Events, e => e.MilestoneId == anomaly.Id);

        // 历史视图照实显示原值：未知事件类型 / 未知状态都不被「修好」
        var history = Assert.Single(detail.HistoryEvents);
        Assert.Equal(anomaly.Id, history.MilestoneId);
        Assert.Equal("customs-detention", history.EventType);
        Assert.Contains("未知", history.KindText);
        Assert.Contains("customs-detention", history.KindText);
        Assert.Equal(9, history.Status);
        Assert.Contains("未知（9）", history.StatusText);
        Assert.False(history.IsVoided);
        Assert.Equal("历史来源", history.SourceDescription);

        // 汇总把未知状态单独统计，不并入有效 / 已作废
        Assert.Equal(1, detail.Shipment.Summary.OtherStatusEventCount);
        Assert.Equal(1, detail.Shipment.Summary.ActiveEventCount);
        Assert.Equal(0, detail.Shipment.Summary.VoidedEventCount);
        Assert.Contains("历史异常状态 1 条", detail.Shipment.Summary.TimelineStateText);
        Assert.Contains("照实保留", detail.Shipment.Summary.TimelineStateText);

        // 未知事件类型不参与任何「最近实际事件」与时间差（已登记的开船证据照常参与）
        Assert.Null(detail.Shipment.Summary.LatestCustomsReleaseAt);
        Assert.Null(detail.Shipment.Summary.LatestActualArrivalAt);
        var arrival = detail.Variances.Single(v => v.Kind == ContainerShipmentTimelineRules.VarianceKindArrival);
        Assert.False(arrival.Comparable);
        Assert.Contains("缺少实际事件", arrival.Text);
    }

    // ==================== 7. 未关联 / 无回填：不按文本兜底匹配 ====================

    [Fact]
    public async Task 未登记有效引用的源记录返回未关联详情_不按文本兜底匹配也不回填()
    {
        using var db = TestDbFactory.Create();
        var booking = SeedBooking(db, "DG-059-G");
        var otherBooking = SeedBooking(db, "DG-059-H");
        // 另一条记录的柜号 / B/L 文本与本条完全相同：仍然绝不借用
        var otherReference = SeedReference(db, otherBooking.Id, sourceNo: "DG-059-H",
            containerNo: "CONT-059", billOfLadingNo: "BL-059");
        SeedMilestone(db, otherReference.Id,
            ContainerShipmentMilestoneRules.EventTypeActualDeparture, ActualDepartureAt);
        var controller = BuildController(db);

        var detail = await TimelineAsync(controller, ContainerShipmentReferenceRules.SourceTypeBooking, booking.Id);

        Assert.False(detail.Shipment.Linked);
        Assert.Equal(0, detail.Shipment.ReferenceId);
        Assert.Equal(booking.Id, detail.Shipment.SourceId);
        Assert.False(detail.Shipment.ReferenceAvailable);
        Assert.Contains("自由文本", detail.Shipment.NotLinkedReason);
        Assert.Contains("无（未登记）", detail.Shipment.NotLinkedReason);

        // 未关联：计划显示「未知」、实际显示「无（未登记）」，且不借用任何别的记录的证据
        var planned = detail.Events.Where(e => e.IsPlanned).ToList();
        Assert.Equal(2, planned.Count);
        Assert.All(planned, e => Assert.False(e.HasTimestamp));
        Assert.All(planned, e => Assert.Contains("未知", e.EventAtText));
        Assert.DoesNotContain(detail.Events, e => e.IsActual);
        Assert.Empty(detail.HistoryEvents);
        Assert.Equal(0, detail.ActiveEventCount);
        Assert.All(detail.Variances, v => Assert.False(v.Comparable));
        Assert.All(detail.Variances, v => Assert.Contains("都没有登记", v.Text));
        Assert.Contains("无（未登记）", detail.Shipment.Summary.TimelineStateText);

        // 文本相同的另一条记录自己的时间线正常（说明数据在库里，只是绝不跨记录借用）
        var otherDetail = await TimelineAsync(
            controller, ContainerShipmentReferenceRules.SourceTypeBooking, otherBooking.Id);
        Assert.True(otherDetail.Shipment.Linked);
        Assert.Equal(otherReference.Id, otherDetail.Shipment.ReferenceId);
        Assert.Single(otherDetail.Events.Where(e => e.IsActual));

        // 只读呈现：没有任何回填（仍只有那一条出运引用）
        Assert.Equal(1, await db.ContainerShipmentReferences.CountAsync());
    }

    // ==================== 8. 已作废引用：只作历史呈现 ====================

    [Fact]
    public async Task 已作废引用只作历史呈现_三单显示未关联而引用详情照实可读()
    {
        using var db = TestDbFactory.Create();
        var booking = SeedBooking(db, "DG-059-I");
        var reference = SeedReference(db, booking.Id, sourceNo: "DG-059-I",
            status: ContainerShipmentReferenceRules.StatusVoided, voidReason: "登记错柜");
        SeedMilestone(db, reference.Id,
            ContainerShipmentMilestoneRules.EventTypeActualDeparture, ActualDepartureAt);
        var controller = BuildController(db);

        // 三单详情：已作废引用不再是「当前证据」，显式说明历史仍可查
        var detail = await TimelineAsync(controller, ContainerShipmentReferenceRules.SourceTypeBooking, booking.Id);
        Assert.False(detail.Shipment.Linked);
        Assert.Contains("已作废", detail.Shipment.NotLinkedReason);
        Assert.Contains("历史", detail.Shipment.NotLinkedReason);

        // 按显式引用 Id 读取：历史证据照常可读，引用状态与作废原因照实标注
        var byReference = AssertOk<ContainerShipmentTimelineDetailDto>(
            await controller.GetForReference(reference.Id));
        Assert.True(byReference.Shipment.Linked);
        Assert.True(byReference.Shipment.IsVoided);
        Assert.False(byReference.Shipment.IsRecorded);
        Assert.Equal("已作废", byReference.Shipment.StatusText);
        Assert.Equal("登记错柜", byReference.Shipment.VoidReason);
        Assert.Contains("不新增任何证据", byReference.Shipment.ReferenceAvailabilityText);
        Assert.Single(byReference.Events.Where(e => e.IsActual));

        // 工作台默认包含已作废历史引用，并可按状态过滤（历史不被隐藏、也不改派）
        var all = await WorkspaceAsync(controller);
        Assert.Equal(1, all.Total);
        Assert.True(all.Items[0].IsVoided);
        var recordedOnly = await WorkspaceAsync(controller,
            new ContainerShipmentTimelineQuery { Status = ContainerShipmentReferenceRules.StatusRecorded });
        Assert.Equal(0, recordedOnly.Total);
        var voidedOnly = await WorkspaceAsync(controller,
            new ContainerShipmentTimelineQuery { Status = ContainerShipmentReferenceRules.StatusVoided });
        Assert.Equal(1, voidedOnly.Total);
    }

    // ==================== 9. 失效链接：显式标注，绝不改派 ====================

    [Fact]
    public async Task 源记录删除或链接无效时显式标注不可用_绝不改派也不回填()
    {
        using var db = TestDbFactory.Create();
        var deletedBooking = SeedBooking(db, "DG-059-J", deleted: true);
        var deletedSourceReference = SeedReference(db, deletedBooking.Id, sourceNo: "DG-059-J");
        var invalidLinkReference = SeedReference(db, 0, sourceNo: "LEGACY-059", containerNo: "CONT-OLD");
        SeedMilestone(db, deletedSourceReference.Id,
            ContainerShipmentMilestoneRules.EventTypeActualDeparture, ActualDepartureAt);
        SeedMilestone(db, invalidLinkReference.Id,
            ContainerShipmentMilestoneRules.EventTypeActualArrival, ActualArrivalAt);
        var controller = BuildController(db);

        // 源记录已软删除：引用可读、证据照读，但显式标注不可用且绝不改派
        var byDeletedSource = AssertOk<ContainerShipmentTimelineDetailDto>(
            await controller.GetForReference(deletedSourceReference.Id));
        Assert.True(byDeletedSource.Shipment.Linked);
        Assert.False(byDeletedSource.Shipment.SourceAvailable);
        Assert.Contains("已删除或不存在", byDeletedSource.Shipment.SourceAvailabilityText);
        Assert.Contains("不会改派", byDeletedSource.Shipment.SourceAvailabilityText);
        Assert.Single(byDeletedSource.Events.Where(e => e.IsActual));

        // 未记录源记录 Id 的历史行：链接无效，同样显式标注且证据照读
        var byInvalidLink = AssertOk<ContainerShipmentTimelineDetailDto>(
            await controller.GetForReference(invalidLinkReference.Id));
        Assert.False(byInvalidLink.Shipment.SourceAvailable);
        Assert.Contains("链接无效", byInvalidLink.Shipment.SourceAvailabilityText);
        Assert.Single(byInvalidLink.Events.Where(e => e.IsActual));

        // 工作台把这些历史行照实列出并标注不可用（不隐藏、不改派）
        var workspace = await WorkspaceAsync(controller, new ContainerShipmentTimelineQuery
        {
            Status = ContainerShipmentReferenceRules.StatusRecorded
        });
        Assert.Equal(2, workspace.Total);
        Assert.All(workspace.Items, i => Assert.True(i.ReferenceAvailable));
        Assert.All(workspace.Items, i => Assert.False(i.SourceAvailable));
        Assert.All(workspace.Items, i => Assert.Contains("不会改派", i.SourceAvailabilityText));

        // 未选择源记录（Id ≤ 0）一律 400：不按柜号等自由文本兜底挑记录
        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => controller.GetForSource(ContainerShipmentReferenceRules.SourceTypeBooking, 0));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => controller.GetForSource("supplier", 1));
        await AssertBusinessAsync(ErrorCodes.NotFound, () => controller.GetForReference(999_999));
    }

    // ==================== 10. 工作台：显式字段筛选，不合并相似记录 ====================

    [Fact]
    public async Task 工作台按显式字段筛选_多客户多港口不合并也不做相似匹配()
    {
        using var db = TestDbFactory.Create();
        var bookingA = SeedBooking(db, "DG-059-L", customerId: 100);
        var bookingB = SeedBooking(db, "DG-059-M", customerId: 200);
        // 同一柜号 / 同一 B/L 文本，但源记录、客户与目的港都不同
        var referenceA = SeedReference(db, bookingA.Id, sourceNo: "DG-059-L",
            containerNo: "CONT-059", destinationPort: "HAMBURG");
        var referenceB = SeedReference(db, bookingB.Id, sourceNo: "DG-059-M",
            containerNo: "CONT-059", destinationPort: "ROTTERDAM");
        var controller = BuildController(db);

        // 不带筛选：两条记录各自一行（不因文本相同而合并）
        var all = await WorkspaceAsync(controller);
        Assert.Equal(2, all.Total);
        Assert.Equal(2, all.Items.Select(i => i.ReferenceId).Distinct().Count());
        Assert.Equal(2, all.Items.Select(i => i.SourceId).Distinct().Count());
        Assert.Equal(new[] { "HAMBURG", "ROTTERDAM" },
            all.Items.Select(i => i.DestinationPort).OrderBy(x => x, StringComparer.Ordinal).ToArray());

        // 航线（目的港）显式筛选：只命中一条（大小写不敏感，但仍为等值匹配）
        var byRoute = await WorkspaceAsync(controller,
            new ContainerShipmentTimelineQuery { DestinationPort = "rotterdam" });
        var onlyB = Assert.Single(byRoute.Items);
        Assert.Equal(referenceB.Id, onlyB.ReferenceId);
        Assert.Equal("DG-059-M", onlyB.SourceNo);

        // 源记录单号显式筛选：只命中自己的那条
        var bySourceNo = await WorkspaceAsync(controller,
            new ContainerShipmentTimelineQuery { SourceNo = "DG-059-L" });
        Assert.Equal(referenceA.Id, Assert.Single(bySourceNo.Items).ReferenceId);

        // 同一柜号的显式筛选会命中两条（因为显式取值确实相同），但仍然是两条独立记录
        var byContainer = await WorkspaceAsync(controller,
            new ContainerShipmentTimelineQuery { ContainerNo = "CONT-059" });
        Assert.Equal(2, byContainer.Total);
        Assert.Equal(2, byContainer.Items.Select(i => i.ReferenceId).Distinct().Count());

        // 部分文本（相似度）一律不匹配：不做模糊 / 相似度筛选
        var partial = await WorkspaceAsync(controller,
            new ContainerShipmentTimelineQuery { ContainerNo = "CONT-05" });
        Assert.Equal(0, partial.Total);

        // 计划时间区间：只匹配已持久化的计划值
        var plannedInRange = await WorkspaceAsync(controller, new ContainerShipmentTimelineQuery
        {
            PlannedDepartureFrom = new DateTime(2026, 9, 10),
            PlannedDepartureTo = new DateTime(2026, 9, 10)
        });
        Assert.Equal(2, plannedInRange.Total);
        var plannedOutOfRange = await WorkspaceAsync(controller, new ContainerShipmentTimelineQuery
        {
            PlannedDepartureFrom = new DateTime(2026, 9, 11)
        });
        Assert.Equal(0, plannedOutOfRange.Total);

        // 源记录类型 / 关键字只命中显式字段
        var byType = await WorkspaceAsync(controller, new ContainerShipmentTimelineQuery
        {
            SourceType = ContainerShipmentReferenceRules.SourceTypeBooking
        });
        Assert.Equal(2, byType.Total);
        var byOtherType = await WorkspaceAsync(controller, new ContainerShipmentTimelineQuery
        {
            SourceType = ContainerShipmentReferenceRules.SourceTypePreLoading
        });
        Assert.Equal(0, byOtherType.Total);
        var byKeyword = await WorkspaceAsync(controller, new ContainerShipmentTimelineQuery { Keyword = "ROTTERDAM" });
        Assert.Equal(referenceB.Id, Assert.Single(byKeyword.Items).ReferenceId);
    }

    // ==================== 11. 工作台：记录事件筛选 + 分页有界 ====================

    [Fact]
    public async Task 工作台记录事件筛选只命中有效证据且分页有界_汇总按页批量装载()
    {
        using var db = TestDbFactory.Create();
        var withDeparture = SeedBooking(db, "DG-059-N");
        var withInspection = SeedBooking(db, "DG-059-O");
        var withoutEvents = SeedBooking(db, "DG-059-P");
        var referenceA = SeedReference(db, withDeparture.Id, sourceNo: "DG-059-N");
        var referenceB = SeedReference(db, withInspection.Id, sourceNo: "DG-059-O");
        SeedReference(db, withoutEvents.Id, sourceNo: "DG-059-P");

        SeedMilestone(db, referenceA.Id,
            ContainerShipmentMilestoneRules.EventTypeActualDeparture, ActualDepartureAt);
        SeedMilestone(db, referenceA.Id,
            ContainerShipmentMilestoneRules.EventTypeInspection, new DateTime(2026, 9, 18, 9, 0, 0),
            status: ContainerShipmentMilestoneRules.StatusVoided,
            voidedAt: new DateTime(2026, 9, 20, 9, 0, 0), voidReason: "重复登记");
        SeedMilestone(db, referenceB.Id,
            ContainerShipmentMilestoneRules.EventTypeInspection, new DateTime(2026, 9, 18, 9, 0, 0));
        var controller = BuildController(db);

        // 事件类型筛选（默认只看有效证据）：已作废的历史证据不参与匹配
        var byDeparture = await WorkspaceAsync(controller, new ContainerShipmentTimelineQuery
        {
            EventType = ContainerShipmentMilestoneRules.EventTypeActualDeparture
        });
        Assert.Equal(referenceA.Id, Assert.Single(byDeparture.Items).ReferenceId);

        var byInspection = await WorkspaceAsync(controller, new ContainerShipmentTimelineQuery
        {
            EventType = ContainerShipmentMilestoneRules.EventTypeInspection
        });
        Assert.Equal(referenceB.Id, Assert.Single(byInspection.Items).ReferenceId);

        // 显式包含已作废历史后，referenceA 的已作废查验证据才参与匹配，并在汇总里标注为历史
        var inspectionWithHistory = await WorkspaceAsync(controller, new ContainerShipmentTimelineQuery
        {
            EventType = ContainerShipmentMilestoneRules.EventTypeInspection,
            IncludeVoidedEvents = true
        });
        Assert.Equal(2, inspectionWithHistory.Total);
        var rowA = inspectionWithHistory.Items.Single(i => i.ReferenceId == referenceA.Id);
        Assert.Equal(1, rowA.Summary.ActiveEventCount);          // 有效开船证据仍在
        Assert.Equal(1, rowA.Summary.VoidedEventCount);
        Assert.Contains("已作废（历史视图）1 条", rowA.Summary.TimelineStateText);

        // 事件时间区间（含端点当天）
        var inRange = await WorkspaceAsync(controller, new ContainerShipmentTimelineQuery
        {
            EventType = ContainerShipmentMilestoneRules.EventTypeInspection,
            EventDateFrom = new DateTime(2026, 9, 18),
            EventDateTo = new DateTime(2026, 9, 18)
        });
        Assert.Equal(referenceB.Id, Assert.Single(inRange.Items).ReferenceId);

        var outOfRange = await WorkspaceAsync(controller, new ContainerShipmentTimelineQuery
        {
            EventType = ContainerShipmentMilestoneRules.EventTypeInspection,
            EventDateFrom = new DateTime(2026, 9, 19)
        });
        Assert.Equal(0, outOfRange.Total);

        // 没有匹配事件的记录不出现在筛选结果中；不筛选时三条都在（历史记录不需要回填）
        Assert.Equal(3, (await WorkspaceAsync(controller)).Total);

        // 分页有界：页大小收敛到上限，页码越界返回空页而不报错
        var capped = await WorkspaceAsync(controller, new ContainerShipmentTimelineQuery { PageSize = 100_000 });
        Assert.Equal(ContainerShipmentTimelineQuery.MaxPageSize, capped.PageSize);
        var single = await WorkspaceAsync(controller, new ContainerShipmentTimelineQuery { PageSize = 1 });
        Assert.Single(single.Items);
        Assert.Equal(3, single.Total);
        Assert.Equal(3, single.TotalPages);
        var thirdPage = await WorkspaceAsync(controller, new ContainerShipmentTimelineQuery { Page = 3, PageSize = 1 });
        Assert.Single(thirdPage.Items);
        var beyondLastPage = await WorkspaceAsync(controller, new ContainerShipmentTimelineQuery { Page = 4, PageSize = 1 });
        Assert.Empty(beyondLastPage.Items);

        // 单页汇总按页批量装载：每行都带自己的汇总
        Assert.All(single.Items, i => Assert.NotNull(i.Summary));
    }

    // ==================== 12. 工作台：未知筛选取值一律拒绝 ====================

    [Fact]
    public async Task 工作台未知筛选取值一律拒绝_不静默忽略筛选条件()
    {
        using var db = TestDbFactory.Create();
        var controller = BuildController(db);

        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => controller.GetPaged(
            new ContainerShipmentTimelineQuery { SourceType = "supplier" }));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => controller.GetPaged(
            new ContainerShipmentTimelineQuery { EventType = "customs-detention" }));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => controller.GetPaged(
            new ContainerShipmentTimelineQuery { Status = 9 }));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => controller.GetPaged(
            new ContainerShipmentTimelineQuery { SourceId = 0 }));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => controller.GetPaged(
            new ContainerShipmentTimelineQuery
            {
                PlannedDepartureFrom = new DateTime(2026, 9, 20),
                PlannedDepartureTo = new DateTime(2026, 9, 10)
            }));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => controller.GetPaged(
            new ContainerShipmentTimelineQuery
            {
                PlannedArrivalFrom = new DateTime(2026, 9, 20),
                PlannedArrivalTo = new DateTime(2026, 9, 10)
            }));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => controller.GetPaged(
            new ContainerShipmentTimelineQuery
            {
                EventDateFrom = new DateTime(2026, 9, 20),
                EventDateTo = new DateTime(2026, 9, 10)
            }));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => controller.GetPaged(
            new ContainerShipmentTimelineQuery { PlannedDepartureFrom = new DateTime(1999, 1, 1) }));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => controller.GetPaged(
            new ContainerShipmentTimelineQuery
            {
                Keyword = new string('k', ContainerShipmentTimelineRules.MaxKeywordLength + 1)
            }));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => controller.GetPaged(
            new ContainerShipmentTimelineQuery
            {
                ContainerNo = new string('c', ContainerShipmentTimelineRules.MaxFilterTextLength + 1)
            }));
    }

    // ==================== 13. 有界读取：固定数量数据集访问，只读不写库 ====================

    [Fact]
    public async Task 工作台与详情读取是固定数量的数据集访问_分页行数变化不改变访问次数且只读不写库()
    {
        using var db = TestDbFactory.Create();
        for (var i = 0; i < 300; i++)
        {
            var booking = SeedBooking(db, $"DG-059-Q{i}");
            var reference = SeedReference(db, booking.Id, sourceNo: $"DG-059-Q{i}");
            if (i == 0)
                SeedMilestone(db, reference.Id,
                    ContainerShipmentMilestoneRules.EventTypeActualDeparture, ActualDepartureAt);
        }

        var counting = CountingDbContext.Wrap(db);

        // 工作台：单页 1 行
        var single = await ContainerShipmentTimelineService.ListAsync(
            counting.Proxy, new ContainerShipmentTimelineQuery { PageSize = 1 });
        var singleReads = counting.DatasetReads;

        Assert.Single(single.Items);
        Assert.Equal(300, single.Total);
        Assert.Equal(
            new[]
            {
                nameof(IErpDbContext.ContainerShipmentReferences),
                nameof(IErpDbContext.ContainerShipmentMilestones),
                nameof(IErpDbContext.ContainerBookings)
            },
            counting.ReadProperties.Distinct().ToArray());

        // 工作台：单页 200 行（跨多页数据）——访问次数必须保持不变（汇总按页批量装载，无逐行查库）
        var large = await ContainerShipmentTimelineService.ListAsync(
            counting.Proxy, new ContainerShipmentTimelineQuery { PageSize = 200 });
        var largeReads = counting.DatasetReads - singleReads;

        Assert.Equal(300, large.Total);
        Assert.Equal(ContainerShipmentTimelineQuery.MaxPageSize, large.Items.Count);
        Assert.Equal(singleReads, largeReads);
        Assert.Equal(0, counting.WriteCalls);                       // 只读：不落库

        // 详情：条目从 1 条涨到 301 条，访问次数同样不变
        var firstReference = await db.ContainerShipmentReferences.AsNoTracking().OrderBy(r => r.Id).FirstAsync();

        var beforeOne = counting.DatasetReads;
        var detailOne = await ContainerShipmentTimelineService.GetForReferenceAsync(counting.Proxy, firstReference.Id);
        var oneReads = counting.DatasetReads - beforeOne;

        Assert.Equal(1, detailOne.ActiveEventCount);

        for (var i = 0; i < 300; i++)
            SeedMilestone(db, firstReference.Id, ContainerShipmentMilestoneRules.EventTypeInspection,
                new DateTime(2026, 9, 14, 9, 0, 0).AddMinutes(i));

        var beforeMany = counting.DatasetReads;
        var detailMany = await ContainerShipmentTimelineService.GetForReferenceAsync(counting.Proxy, firstReference.Id);
        var manyReads = counting.DatasetReads - beforeMany;

        Assert.Equal(301, detailMany.ActiveEventCount);
        Assert.Equal(oneReads, manyReads);
        Assert.Equal(0, counting.WriteCalls);
        Assert.Contains(nameof(IErpDbContext.ContainerShipmentMilestones),
            counting.ReadProperties.Distinct().ToArray());
    }

    // ==================== 14. 只读：不改写任何记录 ====================

    [Fact]
    public async Task 时间线与工作台读取不改写引用里程碑装柜三单与相邻业务记录()
    {
        using var db = TestDbFactory.Create();
        var customer = new BaseCustomer
        {
            CustomerCode = "C-059",
            CustomerName = "时间线测试客户",
            Status = 1,
            CreditStatus = "正常",
            CreditLimit = 50000m
        };
        var order = new SalesOrder
        {
            OrderNo = "SO-059",
            OrderDate = new DateTime(2026, 9, 1),
            CustomerId = 1,
            Currency = Currency.USD,
            TotalAmount = 1000m,
            Status = DocumentStatus.Approved
        };
        var receipt = new FinanceReceipt
        {
            ReceiptNo = "SK-059",
            ReceiptDate = new DateTime(2026, 9, 10),
            CustomerId = 1,
            Amount = 1000m,
            Currency = Currency.USD,
            PaymentMethod = PaymentMethod.BankTransfer,
            Status = DocumentStatus.Approved
        };
        var stock = new Stock
        {
            WarehouseId = 1,
            ProductId = 1,
            Quantity = 120m,
            AvailableQuantity = 120m,
            AverageCost = 10.5m
        };
        db.BaseCustomers.Add(customer);
        db.SalesOrders.Add(order);
        db.FinanceReceipts.Add(receipt);
        db.Stocks.Add(stock);
        await db.SaveChangesAsync();

        var booking = SeedBooking(db, "DG-059-R");
        var preLoading = SeedPreLoading(db, "YZ-059-R", bookingId: booking.Id);
        var loadingList = SeedLoadingList(db, "ZJ-059-R", preLoadingId: preLoading.Id);
        var reference = SeedReference(db, booking.Id, sourceNo: "DG-059-R");
        var milestone = SeedMilestone(db, reference.Id,
            ContainerShipmentMilestoneRules.EventTypeActualDeparture, ActualDepartureAt,
            sourceDescription: "船公司网站截图");
        var controller = BuildController(db);

        // 只读读取：三单详情 + 引用详情 + 工作台
        await TimelineAsync(controller, ContainerShipmentReferenceRules.SourceTypeBooking, booking.Id);
        await TimelineAsync(controller, ContainerShipmentReferenceRules.SourceTypePreLoading, preLoading.Id);
        await TimelineAsync(controller, ContainerShipmentReferenceRules.SourceTypeLoadingList, loadingList.Id);
        await controller.GetForReference(reference.Id);
        await WorkspaceAsync(controller);

        // 没有任何实体处于「已修改 / 已新增 / 已删除」状态（只读，不写库）
        Assert.Empty(db.ChangeTracker.Entries()
            .Where(e => e.State != EntityState.Unchanged)
            .Select(e => e.Entity));

        // 出运引用 / 里程碑：字段与状态完全不变
        var storedReference = await db.ContainerShipmentReferences.AsNoTracking()
            .FirstAsync(o => o.Id == reference.Id);
        Assert.Equal(ContainerShipmentReferenceRules.StatusRecorded, storedReference.Status);
        Assert.Equal(1, storedReference.RevisionNo);
        Assert.Null(storedReference.LastRevisedAt);
        Assert.Equal(PlannedDepartureAt, storedReference.PlannedDepartureAt);
        Assert.Equal("CONT-059", storedReference.ContainerNo);

        var storedMilestone = await db.ContainerShipmentMilestones.AsNoTracking()
            .FirstAsync(o => o.Id == milestone.Id);
        Assert.Equal(ContainerShipmentMilestoneRules.StatusRecorded, storedMilestone.Status);
        Assert.Equal(ActualDepartureAt, storedMilestone.EventAt);
        Assert.Equal("船公司网站截图", storedMilestone.SourceDescription);
        Assert.Null(storedMilestone.VoidedAt);

        // 装柜三单：状态与跟踪列完全不变（读取不推进任何状态）
        var storedBooking = await db.ContainerBookings.AsNoTracking().FirstAsync(o => o.Id == booking.Id);
        Assert.Equal(DocumentStatus.Pending, storedBooking.Status);
        Assert.Null(storedBooking.Atd);
        Assert.Null(storedBooking.Ata);
        Assert.Null(storedBooking.CustomsReleaseDate);

        Assert.Equal(DocumentStatus.Pending,
            (await db.ContainerPreLoadings.AsNoTracking().FirstAsync(o => o.Id == preLoading.Id)).Status);
        Assert.Equal(DocumentStatus.Pending,
            (await db.ContainerLoadingLists.AsNoTracking().FirstAsync(o => o.Id == loadingList.Id)).Status);

        // 相邻业务记录（客户 / 销售订单 / 收款单 / 库存与成本）：完全不变
        var storedCustomer = await db.BaseCustomers.AsNoTracking().FirstAsync(o => o.Id == customer.Id);
        Assert.Equal("C-059", storedCustomer.CustomerCode);
        Assert.Equal(50000m, storedCustomer.CreditLimit);

        var storedOrder = await db.SalesOrders.AsNoTracking().FirstAsync(o => o.Id == order.Id);
        Assert.Equal(1000m, storedOrder.TotalAmount);
        Assert.Equal(DocumentStatus.Approved, storedOrder.Status);

        var storedReceipt = await db.FinanceReceipts.AsNoTracking().FirstAsync(o => o.Id == receipt.Id);
        Assert.Equal(1000m, storedReceipt.Amount);
        Assert.Equal(DocumentStatus.Approved, storedReceipt.Status);

        var storedStock = await db.Stocks.AsNoTracking().FirstAsync(o => o.Id == stock.Id);
        Assert.Equal(120m, storedStock.Quantity);
        Assert.Equal(10.5m, storedStock.AverageCost);

        // 引用修订留痕表保持为空；里程碑 / 出运引用条数不变（读取不产生任何新行）
        Assert.Equal(0, await db.ContainerShipmentReferenceRevisions.CountAsync());
        Assert.Equal(1, await db.ContainerShipmentMilestones.CountAsync());
        Assert.Equal(1, await db.ContainerShipmentReferences.CountAsync());
    }

    // ==================== 15. 纯规则：计划 / 实际分开标注与缺失事件文案 ====================

    [Fact]
    public void 纯规则_计划与实际分开标注与缺失事件文案()
    {
        // 条目种类：计划 / 实际互斥；未知历史取值照实回显
        Assert.True(ContainerShipmentTimelineRules.IsPlannedKind(ContainerShipmentTimelineRules.KindPlannedDeparture));
        Assert.True(ContainerShipmentTimelineRules.IsPlannedKind(ContainerShipmentTimelineRules.KindPlannedArrival));
        Assert.False(ContainerShipmentTimelineRules.IsActualKind(ContainerShipmentTimelineRules.KindPlannedDeparture));
        Assert.True(ContainerShipmentTimelineRules.IsActualKind(
            ContainerShipmentMilestoneRules.EventTypeActualDeparture));
        Assert.True(ContainerShipmentTimelineRules.IsActualKind("customs-detention"));

        Assert.Contains("计划开船", ContainerShipmentTimelineRules.KindText(
            ContainerShipmentTimelineRules.KindPlannedDeparture));
        Assert.Contains("计划到港", ContainerShipmentTimelineRules.KindText(
            ContainerShipmentTimelineRules.KindPlannedArrival));
        Assert.Contains("实际开船", ContainerShipmentTimelineRules.KindText(
            ContainerShipmentMilestoneRules.EventTypeActualDeparture));
        Assert.Contains("查验", ContainerShipmentTimelineRules.KindText(
            ContainerShipmentMilestoneRules.EventTypeInspection));
        Assert.Contains("放行", ContainerShipmentTimelineRules.KindText(
            ContainerShipmentMilestoneRules.EventTypeCustomsRelease));
        Assert.Equal("未知（customs-detention）", ContainerShipmentTimelineRules.KindText("customs-detention"));

        // 时间戳文案：缺失时按计划 / 实际分别显示「未知」与「无（未登记）」，绝不回落为空白或今天
        Assert.Equal("未知（未登记该计划时间）", ContainerShipmentTimelineRules.TimestampText(null, isPlanned: true));
        Assert.Equal("无（未登记）", ContainerShipmentTimelineRules.TimestampText(null, isPlanned: false));
        Assert.Equal("2026-09-10 08:00",
            ContainerShipmentTimelineRules.TimestampText(PlannedDepartureAt, isPlanned: true));
        Assert.Contains("Unspecified", ContainerShipmentTimelineRules.TimestampKindText(PlannedDepartureAt));
        Assert.Equal("未登记", ContainerShipmentTimelineRules.TimestampKindText(null));

        // 口径文案：分开标注 / 不推断状态 / 算术证据 / 显式 Id 关联 / 只读 / 历史排除
        Assert.Contains("分开标注", ContainerShipmentTimelineRules.PlannedVersusActualText);
        Assert.Contains("无（未登记）", ContainerShipmentTimelineRules.PlannedVersusActualText);
        Assert.Contains("未知", ContainerShipmentTimelineRules.PlannedVersusActualText);
        Assert.Contains("不产生任何业务状态", ContainerShipmentTimelineRules.NoStatusInferenceText);
        Assert.Contains("已开船", ContainerShipmentTimelineRules.NoStatusInferenceText);     // 明确列出「不推断成这些」
        Assert.Contains("不是海关决定", ContainerShipmentTimelineRules.EvidenceText);
        Assert.Contains("算术证据", ContainerShipmentTimelineRules.VarianceBasisText);
        Assert.Contains("自由文本", ContainerShipmentTimelineRules.SourceLinkText);
        Assert.Contains("只读", ContainerShipmentTimelineRules.BoundaryText);
        Assert.Contains("已作废", ContainerShipmentTimelineRules.HistoryText);

        // 筛选规范化：显式等值筛选统一大写、留空 = 不过滤
        Assert.Null(ContainerShipmentTimelineRules.NormalizeExactFilter("  ", "柜号"));
        Assert.Equal("CONT-059", ContainerShipmentTimelineRules.NormalizeExactFilter(" cont-059 ", "柜号"));
        Assert.Null(ContainerShipmentTimelineRules.NormalizeSourceTypeFilter("  "));
        Assert.Equal(ContainerShipmentReferenceRules.SourceTypeBooking,
            ContainerShipmentTimelineRules.NormalizeSourceTypeFilter(" Booking "));
        Assert.Equal(ContainerShipmentMilestoneRules.EventTypeInspection,
            ContainerShipmentTimelineRules.NormalizeEventTypeFilter(" Inspection "));
        Assert.Equal(0, ContainerShipmentTimelineRules.NormalizeKeyword(null).Length);

        // 未知筛选 / 越界 / 区间颠倒一律拒绝，不静默忽略筛选条件
        Assert.Throws<BusinessException>(() =>
        {
            _ = ContainerShipmentTimelineRules.NormalizeSourceTypeFilter("supplier");
        });
        Assert.Throws<BusinessException>(() =>
        {
            _ = ContainerShipmentTimelineRules.NormalizeEventTypeFilter("customs-detention");
        });
        Assert.Throws<BusinessException>(() =>
        {
            _ = ContainerShipmentTimelineRules.NormalizeStatusFilter(9);
        });
        Assert.Throws<BusinessException>(() => ContainerShipmentTimelineRules.EnsureRange(
            new DateTime(2026, 9, 20), new DateTime(2026, 9, 10), "计划开船时间"));
        Assert.Throws<BusinessException>(() => ContainerShipmentTimelineRules.EnsureRange(
            new DateTime(1999, 1, 1), null, "计划开船时间"));
        Assert.Throws<BusinessException>(() =>
        {
            _ = ContainerShipmentTimelineRules.NormalizeKeyword(new string('k', 101));
        });

        // 历史视图条数收敛（有界）
        Assert.Equal(ContainerShipmentTimelineRules.MaxHistoryEvents,
            ContainerShipmentTimelineRules.NormalizeHistoryTake(0));
        Assert.Equal(5, ContainerShipmentTimelineRules.NormalizeHistoryTake(5));
        Assert.Equal(ContainerShipmentTimelineRules.MaxHistoryEvents,
            ContainerShipmentTimelineRules.NormalizeHistoryTake(100_000));
    }

    // ==================== 16. 前端与路由接线契约 ====================

    [Fact]
    public void 前端与路由接线契约()
    {
        var index = File.ReadAllText(RepoFile("src", "ERP.Api", "wwwroot", "index.html"));
        Assert.Contains("/js/container-shipment-timeline.js", index);

        // 装柜三单模块提供两个入口：行操作「时间线」与工具栏「🧭 跟踪工作台」
        var modules = File.ReadAllText(RepoFile("src", "ERP.Api", "wwwroot", "js", "modules-doc2.js"));
        Assert.Contains("showContainerShipmentTimeline", modules);
        Assert.Contains("openContainerShipmentTimelineWorkspace()", modules);
        Assert.Contains("🧭 跟踪工作台", modules);
        Assert.Contains("时间线", modules);

        var js = File.ReadAllText(RepoFile("src", "ERP.Api", "wwwroot", "js", "container-shipment-timeline.js"));
        Assert.Contains("async function showContainerShipmentTimeline(id)", js);
        Assert.Contains("async function openContainerShipmentTimelineWorkspace()", js);
        Assert.Contains("const CST_MODULE_ENDPOINTS", js);
        Assert.Contains("'/api/container/bookings'", js);
        Assert.Contains("'/api/container/pre-loadings'", js);
        Assert.Contains("'/api/container/loading-lists'", js);
        Assert.Contains("/shipment-timeline?includeHistory=true", js);
        Assert.Contains("'/api/container/shipment-timeline?'", js);
        Assert.Contains("/api/container/shipment-timeline/references/", js);
        Assert.Contains("const CST_EVENT_TYPES", js);
        Assert.Contains("{ value: 'actual-departure'", js);
        Assert.Contains("{ value: 'actual-arrival'", js);
        Assert.Contains("{ value: 'inspection'", js);
        Assert.Contains("{ value: 'customs-release'", js);
        Assert.Contains("cstOpenReference", js);
        Assert.Contains("cstBackToList", js);
        Assert.Contains("cstSearch", js);
        Assert.Contains("cstResetFilters", js);
        Assert.Contains("历史条目超过单次上限", js);
        Assert.Contains("有效条目超过单次上限", js);
        Assert.Contains("无（未登记）", js);
        Assert.Contains("算术证据", js);
        Assert.Contains("无法比较", js);
        Assert.Contains("已作废 / 历史异常状态视图", js);
        Assert.Contains("记录事件筛选含已作废历史", js);
        Assert.Contains("自由文本合并记录", js);
        Assert.Contains("不推断", js);

        var controller = File.ReadAllText(
            RepoFile("src", "ERP.Api", "Controllers", "ContainerShipmentTimelineController.cs"));
        Assert.Contains("[Route(\"api/container/shipment-timeline\")]", controller);
        Assert.Contains("[HttpGet]", controller);
        Assert.Contains("sources/{sourceType}/{sourceId:long}", controller);
        Assert.Contains("references/{referenceId:long}", controller);

        // 装柜三单详情各自暴露只读时间线端点（显式源记录类型 + 本单 Id）
        foreach (var (file, sourceTypeConstant) in new[]
        {
            ("ContainerControllers.cs", "SourceTypeBooking"),
            ("ContainerPreLoadingController.cs", "SourceTypePreLoading"),
            ("ContainerLoadingListController.cs", "SourceTypeLoadingList"),
        })
        {
            var source = File.ReadAllText(RepoFile("src", "ERP.Api", "Controllers", file));
            Assert.Contains("{id:long}/shipment-timeline", source);
            Assert.Contains(sourceTypeConstant, source);
        }
    }

    // ==================== 17. ERP-059 审计结论：只读派生、无新增表与结构变更 ====================

    [Fact]
    public void ERP059审计_时间线只读派生_不新增表不改结构也不回写既有登记册()
    {
        // IErpDbContext 里没有新增「时间线」相关数据集（只读派生，无新表）
        var dbSets = typeof(IErpDbContext).GetProperties()
            .Where(p => p.PropertyType.IsGenericType
                        && p.PropertyType.GetGenericTypeDefinition() == typeof(DbSet<>))
            .Select(p => p.Name)
            .ToList();
        Assert.DoesNotContain(dbSets, name => name.Contains("Timeline", StringComparison.Ordinal));
        Assert.DoesNotContain(dbSets, name => name.Contains("ShipmentTimeline", StringComparison.Ordinal));

        // 既有两张登记册仍在（本模块只读它们，不复制、不新增第三套）
        Assert.Contains(nameof(IErpDbContext.ContainerShipmentReferences), dbSets);
        Assert.Contains(nameof(IErpDbContext.ContainerShipmentMilestones), dbSets);

        // 服务层只读：没有任何写入调用（新增 / 修改 / 删除 / 落库都没有）
        var service = File.ReadAllText(
            RepoFile("src", "ERP.Application", "Services", "ContainerShipmentTimelineService.cs"));
        Assert.DoesNotContain("SaveChanges", service);
        Assert.DoesNotContain(".Add(", service);
        Assert.DoesNotContain(".Remove(", service);
        Assert.DoesNotContain(".Update(", service);
        Assert.Contains("AsNoTracking", service);
        Assert.Contains("只读", service);

        // 规则层明确「只读 / 不推断 / 分开标注」口径
        var rules = File.ReadAllText(
            RepoFile("src", "ERP.Application", "Services", "ContainerShipmentTimelineRules.cs"));
        Assert.Contains("不产生任何业务状态", rules);
        Assert.Contains("不换算时区", rules);
        Assert.Contains("分开标注", rules);

        // 结构脚本不包含任何时间线相关 DDL（本任务不新增表 / 列、不做任何回填）
        var schema = File.ReadAllText(RepoFile("src", "ERP.Infrastructure", "Data", "SchemaUpgrader.cs"));
        Assert.DoesNotContain("ShipmentTimeline", schema);
        Assert.DoesNotContain("ContainerShipmentTimelines", schema);

        // 前端不出现任何写请求（工作台 / 详情都是只读 GET）
        var js = File.ReadAllText(
            RepoFile("src", "ERP.Api", "wwwroot", "js", "container-shipment-timeline.js"));
        Assert.DoesNotContain("POST", js);
        Assert.DoesNotContain("PUT", js);
        Assert.DoesNotContain("DELETE", js);
        Assert.Contains("只读", js);

        // 文档与实现口径同源
        var doc = File.ReadAllText(RepoFile("docs", "装柜出运证据时间线与跟踪工作台说明.md"));
        Assert.Contains("ERP-059", doc);
        Assert.Contains("计划", doc);
        Assert.Contains("无（未登记）", doc);
        Assert.Contains("算术证据", doc);
        Assert.Contains("只读", doc);
    }

    /// <summary>
    /// 只读计数上下文代理（<see cref="DispatchProxy"/>）：记录访问的数据集（<c>DbSet</c> 属性）名称与写入次数，
    /// 用于断言「分页有界 / 无逐行查库」与「只读不写库」；不改动生产代码。
    /// </summary>
    public class CountingDbContext : DispatchProxy
    {
        private IErpDbContext _inner = null!;

        /// <summary>包装后的上下文（服务 / 控制器按 <see cref="IErpDbContext"/> 使用）</summary>
        public IErpDbContext Proxy { get; private set; } = null!;

        /// <summary>数据集（<c>DbSet</c> 属性）访问次数：即本次查询实际发起的数据集访问次数</summary>
        public int DatasetReads => ReadProperties.Count;

        /// <summary>被访问的数据集属性名（本模块预期只有出运引用 / 里程碑 / 装柜三单）</summary>
        public List<string> ReadProperties { get; } = new();

        /// <summary><c>SaveChangesAsync</c> 调用次数：只读库恒为 0</summary>
        public int WriteCalls { get; private set; }

        /// <summary>包装一个真实上下文（计数从返回对象上读取）</summary>
        public static CountingDbContext Wrap(IErpDbContext inner)
        {
            var proxy = DispatchProxy.Create<IErpDbContext, CountingDbContext>();
            var counting = (CountingDbContext)(object)proxy;
            counting._inner = inner;
            counting.Proxy = proxy;
            return counting;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod is null) return null;
            if (targetMethod.Name == nameof(IErpDbContext.SaveChangesAsync))
            {
                WriteCalls++;
                return _inner.SaveChangesAsync(args is { Length: > 0 } ? (CancellationToken)args[0]! : default);
            }

            if (targetMethod.Name.StartsWith("get_", StringComparison.Ordinal))
            {
                ReadProperties.Add(targetMethod.Name[4..]);
            }

            return targetMethod.Invoke(_inner, args);
        }
    }
}
