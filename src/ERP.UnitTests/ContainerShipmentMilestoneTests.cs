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
/// 装柜出运里程碑证据单元测试（ERP-058）。覆盖：四类事件类型（实际开船 / 实际到港 / 查验 / 放行）的
/// 只追加登记与「未知不推断」、事件时间必填与有界校验、父出运引用（不存在 / 已删除 / 已作废）与事件类型
/// allowlist 校验、同一父记录 + 类型 + 时间的重复有效证据拒绝、显式作废（保留原始类型 / 时间 / 来源与原因）
/// 与重复作废拒绝、父记录删除后历史可读与不可用标注（绝不改派）、历史异常事件类型 / 状态照实可读、
/// 台账过滤分页有界、父记录候选有界与批量统计、有界数据集访问（无逐行查库）、相邻业务记录非变更，
/// 以及模型配置契约 / 幂等建表 / 前端接线与 ERP-058 审计结论（里程碑只挂在出运引用之下）。
/// 全部使用内存库（TestDbFactory），不连接 SQL Server、不执行任何 SQL / 部署脚本、不联系任何外部系统，
/// 不做任何浏览器 / UI 验收（浏览器验收按项目策略延后到 FINAL-UI-ACCEPTANCE）。
/// </summary>
public class ContainerShipmentMilestoneTests
{
    // ==================== 0. 测试脚手架 ====================

    /// <summary>默认事件时间（各用例统一口径）</summary>
    private static readonly DateTime DefaultEventAt = new(2026, 9, 12, 10, 30, 0);

    private static ContainerShipmentMilestoneController BuildController(ErpDbContext db) => new(db);

    private static ContainerBooking SeedBooking(
        ErpDbContext db, string bookingNo, DocumentStatus status = DocumentStatus.Pending, bool deleted = false)
    {
        var booking = new ContainerBooking
        {
            BookingNo = bookingNo,
            BookingDate = new DateTime(2026, 9, 1),
            CustomerId = 100,
            ContainerType = ContainerType.GP40,
            ShippingCompany = "COSCO",
            DeparturePort = "NINGBO",
            DestinationPort = "HAMBURG",
            ShipmentMode = "FCL",
            BillOfLadingNo = "BL-IN-BOOKING",
            Etd = new DateTime(2026, 9, 10, 8, 0, 0),
            Eta = new DateTime(2026, 9, 25, 8, 0, 0),
            Status = status,
            Remark = "订柜备注",
            IsDeleted = deleted
        };
        db.ContainerBookings.Add(booking);
        db.SaveChanges();
        return booking;
    }

    private static ContainerPreLoading SeedPreLoading(
        ErpDbContext db, string no, string containerNo = "CONT-058", long? bookingId = null,
        DocumentStatus status = DocumentStatus.Pending, bool deleted = false)
    {
        var preLoading = new ContainerPreLoading
        {
            PreLoadingNo = no,
            LoadingDate = new DateTime(2026, 9, 3),
            BookingId = bookingId,
            ContainerNo = containerNo,
            SealNo = "SEAL-058",
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
        ErpDbContext db, string no, string containerNo = "CONT-058", long? preLoadingId = null,
        DocumentStatus status = DocumentStatus.Pending, bool deleted = false)
    {
        var loadingList = new ContainerLoadingList
        {
            LoadingListNo = no,
            PreLoadingId = preLoadingId,
            LoadingDate = new DateTime(2026, 9, 5),
            ContainerNo = containerNo,
            CustomerId = 100,
            ShippingMark = "MARK-058",
            TotalCartons = 100,
            Status = status,
            Remark = "装柜清单备注",
            IsDeleted = deleted
        };
        db.ContainerLoadingLists.Add(loadingList);
        db.SaveChanges();
        return loadingList;
    }

    /// <summary>既有 ERP-057 出运引用（里程碑的父记录；本模块只读它，绝不改写）</summary>
    private static ContainerShipmentReference SeedReference(
        ErpDbContext db, long sourceId,
        string sourceType = ContainerShipmentReferenceRules.SourceTypeBooking,
        string sourceNo = "DG-058", string containerNo = "CONT-058",
        int status = ContainerShipmentReferenceRules.StatusRecorded, bool deleted = false)
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
            ShippingOrderNo = "SO-058",
            BillOfLadingNo = "BL-058",
            CarrierName = "COSCO",
            ForwarderName = "FORWARDER-058",
            DeparturePort = "NINGBO",
            TransitPort = "SINGAPORE",
            DestinationPort = "HAMBURG",
            PlannedDepartureAt = new DateTime(2026, 9, 10, 8, 0, 0),
            PlannedArrivalAt = new DateTime(2026, 9, 25, 8, 0, 0),
            TruckerName = "TRUCKER-058",
            Remark = "出运引用备注",
            Status = status,
            RecordedAt = new DateTime(2026, 9, 2, 9, 0, 0),
            RevisionNo = 1,
            IsDeleted = deleted
        };
        db.ContainerShipmentReferences.Add(reference);
        db.SaveChanges();
        return reference;
    }

    private static ContainerShipmentMilestoneSaveDto Dto(
        long referenceId,
        string eventType = ContainerShipmentMilestoneRules.EventTypeActualDeparture,
        DateTime? eventAt = null,
        string sourceDescription = "", string notes = "", string recordedBy = "")
        => new()
        {
            ContainerShipmentReferenceId = referenceId,
            EventType = eventType,
            EventAt = eventAt ?? DefaultEventAt,
            SourceDescription = sourceDescription,
            Notes = notes,
            RecordedBy = recordedBy
        };

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

    private static async Task<ContainerShipmentMilestoneDto> RecordAsync(
        ContainerShipmentMilestoneController controller, ContainerShipmentMilestoneSaveDto dto)
        => AssertOk<ContainerShipmentMilestoneDto>(await controller.Create(dto));

    private static async Task<PagedResult<ContainerShipmentMilestoneDto>> QueryAsync(
        ContainerShipmentMilestoneController controller, ContainerShipmentMilestoneQuery? query = null)
        => AssertOk<PagedResult<ContainerShipmentMilestoneDto>>(
            await controller.GetPaged(query ?? new ContainerShipmentMilestoneQuery()));

    // ==================== 1. 登记：四类事件 + 服务端登记时间 + 不改写任何既有记录 ====================

    [Fact]
    public async Task 登记四类里程碑事件_保留录入值且不改写出运引用与装柜三单()
    {
        using var db = TestDbFactory.Create();
        var booking = SeedBooking(db, "DG-058-A");
        var preLoading = SeedPreLoading(db, "YZ-058-A", "CONT-A", booking.Id);
        var loadingList = SeedLoadingList(db, "ZJ-058-A", "CONT-A", preLoading.Id);
        var reference = SeedReference(db, booking.Id, sourceNo: "DG-058-A");
        var controller = BuildController(db);
        var before = DateTime.Now;

        var departure = await RecordAsync(controller, Dto(
            reference.Id, ContainerShipmentMilestoneRules.EventTypeActualDeparture,
            new DateTime(2026, 9, 12, 10, 30, 0), "船公司网站截图", "预配船名：COSCO A", "张三"));
        var arrival = await RecordAsync(controller, Dto(
            reference.Id, ContainerShipmentMilestoneRules.EventTypeActualArrival,
            new DateTime(2026, 9, 27, 6, 15, 0), "货代邮件"));
        var inspection = await RecordAsync(controller, Dto(
            reference.Id, ContainerShipmentMilestoneRules.EventTypeInspection,
            new DateTime(2026, 9, 14, 9, 0, 0), "报关行通知", "抽查 3 箱"));
        var release = await RecordAsync(controller, Dto(
            reference.Id, ContainerShipmentMilestoneRules.EventTypeCustomsRelease,
            new DateTime(2026, 9, 15, 16, 0, 0), "报关行通知"));

        // 事件类型文案（allowlist 口径）与父记录只读派生信息
        Assert.Equal("实际开船 / 离港", departure.EventTypeText);
        Assert.Equal("实际到港 / 抵达", arrival.EventTypeText);
        Assert.Equal("查验", inspection.EventTypeText);
        Assert.Equal("放行", release.EventTypeText);
        Assert.Equal(reference.Id, departure.ContainerShipmentReferenceId);
        Assert.Equal("订柜信息", departure.ParentSourceTypeText);
        Assert.Equal("DG-058-A", departure.ParentSourceNo);
        // ParentStatusText 是父**出运引用**的状态（已登记 / 已作废），不是源记录的装柜单据状态
        Assert.Equal("已登记", departure.ParentStatusText);
        Assert.True(departure.ParentAvailable);

        // 用户录入的事件时间 / 来源说明 / 备注 / 记录人原样保留；登记时间由服务端写入
        Assert.Equal(new DateTime(2026, 9, 12, 10, 30, 0), departure.EventAt);
        Assert.Equal("船公司网站截图", departure.SourceDescription);
        Assert.Equal("预配船名：COSCO A", departure.Notes);
        Assert.Equal("张三", departure.RecordedBy);
        Assert.True(departure.RecordedAt >= before && departure.RecordedAt <= DateTime.Now);
        Assert.Equal(ContainerShipmentMilestoneRules.StatusRecorded, departure.Status);
        Assert.Equal("已登记", departure.StatusText);
        Assert.True(departure.IsRecorded);
        Assert.False(departure.IsVoided);
        Assert.Null(departure.VoidedAt);
        Assert.Equal(string.Empty, departure.VoidReason);
        Assert.Contains("不是承运人确认", departure.EvidenceCategoryText);
        Assert.Contains("不是海关决定", inspection.EvidenceCategoryText);
        Assert.Equal(ContainerShipmentMilestoneRules.BoundaryText, departure.BoundaryText);

        // 四条事件都落在本登记册，且没有新增任何出运引用
        Assert.Equal(4, await db.ContainerShipmentMilestones.CountAsync());
        Assert.Equal(1, await db.ContainerShipmentReferences.CountAsync());

        // 装柜链路记录与出运引用行完全不变（登记里程碑绝不推进工作流 / 不改既有列）
        var storedBooking = await db.ContainerBookings.AsNoTracking().FirstAsync(o => o.Id == booking.Id);
        Assert.Equal("DG-058-A", storedBooking.BookingNo);
        Assert.Equal(DocumentStatus.Pending, storedBooking.Status);
        Assert.Null(storedBooking.Atd);
        Assert.Null(storedBooking.Ata);
        Assert.Null(storedBooking.InspectionDate);
        Assert.Null(storedBooking.CustomsReleaseDate);
        Assert.Equal(new DateTime(2026, 9, 10, 8, 0, 0), storedBooking.Etd);

        var storedPreLoading = await db.ContainerPreLoadings.AsNoTracking().FirstAsync(o => o.Id == preLoading.Id);
        Assert.Equal(DocumentStatus.Pending, storedPreLoading.Status);
        Assert.Equal("CONT-A", storedPreLoading.ContainerNo);

        var storedLoadingList = await db.ContainerLoadingLists.AsNoTracking().FirstAsync(o => o.Id == loadingList.Id);
        Assert.Equal(DocumentStatus.Pending, storedLoadingList.Status);
        Assert.Equal("CONT-A", storedLoadingList.ContainerNo);

        var storedReference = await db.ContainerShipmentReferences.AsNoTracking()
            .FirstAsync(o => o.Id == reference.Id);
        Assert.Equal(ContainerShipmentReferenceRules.StatusRecorded, storedReference.Status);
        Assert.Equal(1, storedReference.RevisionNo);
        Assert.Equal(reference.PlannedDepartureAt, storedReference.PlannedDepartureAt);
        Assert.Equal(reference.UpdatedAt, storedReference.UpdatedAt);
    }

    // ==================== 2. 事件时间必填 + 可选证据「未知不推断」 ====================

    [Fact]
    public async Task 事件时间必填且有界_可选证据留空保持未知且绝不按计划时间推断()
    {
        using var db = TestDbFactory.Create();
        var booking = SeedBooking(db, "DG-058-B");
        var reference = SeedReference(db, booking.Id);
        var controller = BuildController(db);

        // 事件时间必填：null 直接拒绝（不按父记录的计划 ETD / ETA 或单据状态推断）
        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => controller.Create(
            new ContainerShipmentMilestoneSaveDto
            {
                ContainerShipmentReferenceId = reference.Id,
                EventType = ContainerShipmentMilestoneRules.EventTypeActualDeparture,
                EventAt = null
            }));
        // 越界事件时间拒绝
        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => controller.Create(Dto(
            reference.Id, ContainerShipmentMilestoneRules.EventTypeActualDeparture, new DateTime(1999, 12, 31))));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => controller.Create(Dto(
            reference.Id, ContainerShipmentMilestoneRules.EventTypeActualDeparture, new DateTime(2100, 1, 1))));
        Assert.Equal(0, await db.ContainerShipmentMilestones.CountAsync());   // 校验失败不落任何数据

        // 可选证据留空：保持空串 = 未知（绝不从父记录的承运人 / 港口 / 备注 / 计划时间推断）
        var inspection = await RecordAsync(controller, Dto(
            reference.Id, ContainerShipmentMilestoneRules.EventTypeInspection,
            new DateTime(2026, 9, 14, 9, 0, 0)));
        Assert.Equal(string.Empty, inspection.SourceDescription);
        Assert.Equal(string.Empty, inspection.Notes);
        Assert.Equal(string.Empty, inspection.RecordedBy);
        Assert.NotEqual(reference.PlannedDepartureAt, inspection.EventAt);
        Assert.NotEqual(reference.PlannedArrivalAt, inspection.EventAt);
        Assert.DoesNotContain(reference.CarrierName, inspection.SourceDescription);
        Assert.DoesNotContain(reference.ShippingOrderNo, inspection.Notes);
        // 空值保持空串（不写入「无」「待定」这类占位值，也不写成「未知」）
        Assert.DoesNotContain("未知",
            inspection.SourceDescription + inspection.Notes + inspection.RecordedBy);

        // 证据性质文案：查验 / 放行不等于海关结论
        Assert.Equal(ContainerShipmentMilestoneRules.InspectionAndReleaseEvidenceText, inspection.EvidenceCategoryText);
        Assert.Contains("不代表允许出运", inspection.EvidenceCategoryText);
        Assert.Contains("不是海关决定", inspection.EvidenceCategoryText);

        // 只读文本边界：超长来源说明 / 备注 / 记录人一律拒绝（不静默截断）
        var tooLong = new string('源', ContainerShipmentMilestoneRules.MaxSourceDescriptionLength + 1);
        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => controller.Create(Dto(
            reference.Id, ContainerShipmentMilestoneRules.EventTypeActualArrival, DefaultEventAt, tooLong)));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => controller.Create(Dto(
            reference.Id, ContainerShipmentMilestoneRules.EventTypeActualArrival, DefaultEventAt,
            "来源", new string('备', ContainerShipmentMilestoneRules.MaxNotesLength + 1))));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => controller.Create(Dto(
            reference.Id, ContainerShipmentMilestoneRules.EventTypeActualArrival, DefaultEventAt,
            "来源", "备注", new string('人', ContainerShipmentMilestoneRules.MaxRecordedByLength + 1))));
        Assert.Equal(1, await db.ContainerShipmentMilestones.CountAsync());
    }

    // ==================== 3. 父记录与事件类型校验（allowlist，拒绝不存在 / 已删除 / 已作废） ====================

    [Fact]
    public async Task 父记录与事件类型校验_不存在已删除或已作废父记录与非法事件类型被拒绝且不落库()
    {
        using var db = TestDbFactory.Create();
        var booking = SeedBooking(db, "DG-058-C");
        var active = SeedReference(db, booking.Id, sourceNo: "DG-058-C1");
        var deleted = SeedReference(db, booking.Id + 1, sourceNo: "DG-058-C2", deleted: true);
        var voided = SeedReference(db, booking.Id + 2, sourceNo: "DG-058-C3",
            status: ContainerShipmentReferenceRules.StatusVoided);
        var controller = BuildController(db);

        // 未选择父记录
        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => controller.Create(Dto(0)));
        // 父记录 Id 不存在（也绝不按柜号 / 单号兜底匹配）
        await AssertBusinessAsync(ErrorCodes.NotFound, () => controller.Create(Dto(999_999)));
        // 父记录已删除 / 已作废
        await AssertBusinessAsync(ErrorCodes.RuleConflict, () => controller.Create(Dto(deleted.Id)));
        await AssertBusinessAsync(ErrorCodes.RuleConflict, () => controller.Create(Dto(voided.Id)));

        // 事件类型 allowlist：空 / 自由文本 / 大小写以外取值一律拒绝
        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => RecordAsync(controller, Dto(active.Id, "  ")));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => RecordAsync(controller, Dto(active.Id, "customs-hold")));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => RecordAsync(controller, Dto(active.Id, "实际开船")));
        Assert.Equal(0, await db.ContainerShipmentMilestones.CountAsync());

        // 合法取值（忽略大小写与首尾空白）可登记
        var recorded = await RecordAsync(controller, Dto(active.Id, " ACTUAL-DEPARTURE "));
        Assert.Equal(ContainerShipmentMilestoneRules.EventTypeActualDeparture, recorded.EventType);
        Assert.Equal(1, await db.ContainerShipmentMilestones.CountAsync());
    }

    // ==================== 4. 重复有效证据拒绝（同父 + 同类型 + 同时间） ====================

    [Fact]
    public async Task 同一父记录同类型同时间重复登记被拒绝_其他组合允许且不静默合并()
    {
        using var db = TestDbFactory.Create();
        var bookingA = SeedBooking(db, "DG-058-D");
        var bookingB = SeedBooking(db, "DG-058-E");
        var referenceA = SeedReference(db, bookingA.Id, sourceNo: "DG-058-D");
        var referenceB = SeedReference(db, bookingB.Id, sourceNo: "DG-058-E");
        var controller = BuildController(db);

        var first = await RecordAsync(controller, Dto(
            referenceA.Id, ContainerShipmentMilestoneRules.EventTypeActualDeparture,
            DefaultEventAt, "船公司网站截图", "首条证据", "张三"));

        // 同一父记录 + 同类型 + 同时间：重复直接拒绝（提示先核对 / 显式作废），绝不静默合并
        var ex = await AssertBusinessAsync(ErrorCodes.Duplicate, () => controller.Create(Dto(
            referenceA.Id, ContainerShipmentMilestoneRules.EventTypeActualDeparture,
            DefaultEventAt, "重复来源", "重复备注", "李四")));
        Assert.Contains("不会静默合并", ex.Message);
        Assert.Equal(1, await db.ContainerShipmentMilestones.CountAsync());

        // 既有证据完全不被覆盖
        var stored = await db.ContainerShipmentMilestones.AsNoTracking().FirstAsync();
        Assert.Equal("船公司网站截图", stored.SourceDescription);
        Assert.Equal("首条证据", stored.Notes);
        Assert.Equal("张三", stored.RecordedBy);
        Assert.Equal(ContainerShipmentMilestoneRules.StatusRecorded, stored.Status);

        // 同类型不同时间 / 同时间不同类型 / 同时间同类型但不同父记录：都允许（不是「文本相同」就去重）
        await RecordAsync(controller, Dto(referenceA.Id,
            ContainerShipmentMilestoneRules.EventTypeActualDeparture, DefaultEventAt.AddHours(1)));
        await RecordAsync(controller, Dto(referenceA.Id,
            ContainerShipmentMilestoneRules.EventTypeActualArrival, DefaultEventAt));
        await RecordAsync(controller, Dto(referenceB.Id,
            ContainerShipmentMilestoneRules.EventTypeActualDeparture, DefaultEventAt));
        Assert.Equal(4, await db.ContainerShipmentMilestones.CountAsync());

        // 作废后同一父 + 类型 + 时间可重新登记（已作废行不占用额度，新旧并存可查）
        await controller.Void(first.Id, new ContainerShipmentMilestoneVoidRequest { Reason = "时间录错，重新登记" });
        var reRecorded = await RecordAsync(controller, Dto(
            referenceA.Id, ContainerShipmentMilestoneRules.EventTypeActualDeparture,
            DefaultEventAt, "更正后的来源"));
        Assert.True(reRecorded.Id > first.Id);
        Assert.Equal(5, await db.ContainerShipmentMilestones.CountAsync());

        // 新旧并存：默认台账同时可见已作废历史与新的有效证据
        var page = await QueryAsync(controller, new ContainerShipmentMilestoneQuery
        {
            ContainerShipmentReferenceId = referenceA.Id
        });
        Assert.Equal(4, page.Items.Count);
        Assert.Contains(page.Items, r => r.Id == first.Id && r.IsVoided);
        Assert.Contains(page.Items, r => r.Id == reRecorded.Id && r.IsRecorded);
    }

    // ==================== 5. 作废：保留原始类型 / 时间 / 来源 / 原因，无硬删除与改写 ====================

    [Fact]
    public async Task 作废保留原始类型时间来源与原因_重复作废被拒绝且不提供硬删除与改写()
    {
        using var db = TestDbFactory.Create();
        var booking = SeedBooking(db, "DG-058-F");
        var reference = SeedReference(db, booking.Id);
        var controller = BuildController(db);

        var recorded = await RecordAsync(controller, Dto(
            reference.Id, ContainerShipmentMilestoneRules.EventTypeInspection,
            new DateTime(2026, 9, 14, 9, 0, 0), "报关行通知", "抽查 3 箱", "张三"));

        // 作废必须填写原因
        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () =>
            controller.Void(recorded.Id, new ContainerShipmentMilestoneVoidRequest { Reason = "   " }));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () =>
            controller.Void(recorded.Id, null));

        var voided = AssertOk<ContainerShipmentMilestoneDto>(await controller.Void(
            recorded.Id, new ContainerShipmentMilestoneVoidRequest { Reason = "查验日期录错，重新登记" }));

        Assert.Equal(ContainerShipmentMilestoneRules.StatusVoided, voided.Status);
        Assert.Equal("已作废", voided.StatusText);
        Assert.True(voided.IsVoided);
        Assert.False(voided.IsRecorded);
        Assert.NotNull(voided.VoidedAt);
        Assert.Equal("查验日期录错，重新登记", voided.VoidReason);

        // 原始证据逐字段保留：作废不是删除，也不是改写
        Assert.Equal(recorded.EventType, voided.EventType);
        Assert.Equal(recorded.EventTypeText, voided.EventTypeText);
        Assert.Equal(recorded.EventAt, voided.EventAt);
        Assert.Equal(recorded.SourceDescription, voided.SourceDescription);
        Assert.Equal(recorded.Notes, voided.Notes);
        Assert.Equal(recorded.RecordedBy, voided.RecordedBy);
        Assert.Equal(recorded.RecordedAt, voided.RecordedAt);
        Assert.Equal(recorded.ContainerShipmentReferenceId, voided.ContainerShipmentReferenceId);
        Assert.Equal(recorded.EvidenceCategoryText, voided.EvidenceCategoryText);

        // 重复作废被拒绝；已作废证据仍然可读（没有被物理删除）
        await AssertBusinessAsync(ErrorCodes.RuleConflict, () => controller.Void(
            recorded.Id, new ContainerShipmentMilestoneVoidRequest { Reason = "重复作废" }));
        Assert.Equal(1, await db.ContainerShipmentMilestones.CountAsync());
        var storedRow = await db.ContainerShipmentMilestones.AsNoTracking().FirstAsync();
        Assert.False(storedRow.IsDeleted);
        Assert.Equal(ContainerShipmentMilestoneRules.StatusVoided, storedRow.Status);
        Assert.Equal("报关行通知", storedRow.SourceDescription);

        // 控制器只暴露 GET / POST（没有 PUT / PATCH / DELETE），服务里也没有删除或改写入口
        var actions = typeof(ContainerShipmentMilestoneController).GetMethods(
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        Assert.DoesNotContain(actions, m => m.GetCustomAttributes<HttpPutAttribute>().Any());
        Assert.DoesNotContain(actions, m => m.GetCustomAttributes<HttpPatchAttribute>().Any());
        Assert.DoesNotContain(actions, m => m.GetCustomAttributes<HttpDeleteAttribute>().Any());
        Assert.Contains(actions, m => m.GetCustomAttributes<HttpPostAttribute>().Any());

        var service = File.ReadAllText(RepoFile(
            "src", "ERP.Application", "Services", "ContainerShipmentMilestoneService.cs"));
        Assert.DoesNotContain("Remove(", service);
        Assert.DoesNotContain("RemoveRange", service);

        // 已作废证据只能按状态筛选读取（历史视图），不能被「修好」成有效证据
        var history = await QueryAsync(controller, new ContainerShipmentMilestoneQuery
        {
            Status = ContainerShipmentMilestoneRules.StatusVoided
        });
        Assert.Equal(voided.Id, Assert.Single(history.Items).Id);
        var activeOnly = await QueryAsync(controller, new ContainerShipmentMilestoneQuery
        {
            Status = ContainerShipmentMilestoneRules.StatusRecorded
        });
        Assert.Empty(activeOnly.Items);
    }

    // ==================== 6. 父记录删除 / 缺失 / 作废：历史可读且绝不改派 ====================

    [Fact]
    public async Task 父记录删除或缺失后历史里程碑仍可读并显式标注不可用_绝不改派()
    {
        using var db = TestDbFactory.Create();
        var booking = SeedBooking(db, "DG-058-G");
        var reference = SeedReference(db, booking.Id, sourceNo: "DG-058-G");
        var controller = BuildController(db);

        var recorded = await RecordAsync(controller, Dto(
            reference.Id, ContainerShipmentMilestoneRules.EventTypeActualArrival,
            DefaultEventAt, "货代邮件"));

        // 父出运引用被软删除：历史里程碑照常可读，只是显式标注不可用
        reference.IsDeleted = true;
        reference.UpdatedAt = DateTime.Now;
        await db.SaveChangesAsync();

        var row = Assert.Single((await QueryAsync(controller)).Items);
        Assert.Equal(recorded.Id, row.Id);
        Assert.False(row.ParentAvailable);
        Assert.Contains("已删除或不存在", row.ParentAvailabilityText);
        Assert.Equal(string.Empty, row.ParentSourceNo);         // 不回填、不伪造快照
        Assert.Equal(string.Empty, row.ParentSourceType);
        Assert.Equal(recorded.ContainerShipmentReferenceId, row.ContainerShipmentReferenceId);

        var detail = AssertOk<ContainerShipmentMilestoneDetailDto>(await controller.GetById(recorded.Id));
        Assert.False(detail.Milestone.ParentAvailable);
        Assert.Contains("历史里程碑证据仍可读", detail.Milestone.ParentAvailabilityText);
        Assert.Contains("不会按柜号", detail.InfoText);

        // 不能再对已删除的父记录新增里程碑（也不会静默改派到别的出运引用）
        await AssertBusinessAsync(ErrorCodes.RuleConflict, () => controller.Create(Dto(reference.Id)));
        Assert.Equal(1, await db.ContainerShipmentMilestones.CountAsync());

        // 父记录被物理删除（历史异常）同样照实可读
        db.ContainerShipmentReferences.Remove(await db.ContainerShipmentReferences.FirstAsync());
        await db.SaveChangesAsync();
        Assert.False(Assert.Single((await QueryAsync(controller)).Items).ParentAvailable);

        // 父出运引用已作废：里程碑仍可读，并显式说明父已作废、不能新增
        var voidedParent = SeedReference(db, booking.Id + 1, sourceNo: "DG-058-G2",
            status: ContainerShipmentReferenceRules.StatusVoided);
        db.ContainerShipmentMilestones.Add(new ContainerShipmentMilestone
        {
            ContainerShipmentReferenceId = voidedParent.Id,
            EventType = ContainerShipmentMilestoneRules.EventTypeCustomsRelease,
            EventAt = DefaultEventAt.AddDays(1),
            SourceDescription = "报关行通知",
            Status = ContainerShipmentMilestoneRules.StatusRecorded,
            CreatedAt = DateTime.Now
        });
        await db.SaveChangesAsync();

        var voidedParentRow = (await QueryAsync(controller)).Items
            .Single(r => r.ContainerShipmentReferenceId == voidedParent.Id);
        Assert.True(voidedParentRow.ParentAvailable);
        Assert.Equal("已作废", voidedParentRow.ParentStatusText);
        Assert.Contains("父出运引用已作废", voidedParentRow.ParentAvailabilityText);
        await AssertBusinessAsync(ErrorCodes.RuleConflict, () => controller.Create(Dto(voidedParent.Id)));
    }

    // ==================== 7. 历史异常事件类型 / 状态：照实可读，不静默修正 ====================

    [Fact]
    public async Task 历史异常事件类型与状态照实可读_不静默修正也不被筛选项误用()
    {
        using var db = TestDbFactory.Create();
        var booking = SeedBooking(db, "DG-058-H");
        var reference = SeedReference(db, booking.Id);
        var controller = BuildController(db);

        // 历史遗留行：事件类型与状态都不在当前 allowlist / 状态机内（例如人工直接写库或历史口径变更）
        db.ContainerShipmentMilestones.Add(new ContainerShipmentMilestone
        {
            ContainerShipmentReferenceId = reference.Id,
            EventType = "customs-hold",
            EventAt = new DateTime(2026, 9, 13, 11, 0, 0),
            SourceDescription = "历史遗留来源",
            Status = 9,
            CreatedAt = DateTime.Now
        });
        await db.SaveChangesAsync();

        var row = Assert.Single((await QueryAsync(controller)).Items);
        Assert.Equal("customs-hold", row.EventType);
        Assert.Contains("未知", row.EventTypeText);              // 未知取值照实回显，不假定为实际开船
        Assert.Contains("未知", row.StatusText);
        Assert.Equal(9, row.Status);
        Assert.False(row.IsRecorded);
        Assert.False(row.IsVoided);
        Assert.Equal(ContainerShipmentMilestoneRules.MovementEvidenceText, row.EvidenceCategoryText);

        // 详情同样可读（不抛异常、不被静默改写成「已登记」）
        var detail = AssertOk<ContainerShipmentMilestoneDetailDto>(await controller.GetById(row.Id));
        Assert.Contains("未知", detail.Milestone.EventTypeText);

        // 但筛选条件仍然严格：未知事件类型 / 未知状态一律拒绝，不静默忽略
        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => controller.GetPaged(
            new ContainerShipmentMilestoneQuery { EventType = "customs-hold" }));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => controller.GetPaged(
            new ContainerShipmentMilestoneQuery { Status = 9 }));

        // 异常行既不占用「同父 + 类型 + 时间」的有效额度，也不被静默删除或改写
        await RecordAsync(controller, Dto(reference.Id, ContainerShipmentMilestoneRules.EventTypeActualDeparture,
            new DateTime(2026, 9, 13, 11, 0, 0)));
        Assert.Equal(2, await db.ContainerShipmentMilestones.CountAsync());
        Assert.Equal("customs-hold",
            (await db.ContainerShipmentMilestones.AsNoTracking().OrderBy(m => m.Id).FirstAsync()).EventType);
    }

    // ==================== 8. 台账过滤 / 分页有界 / 未知筛选取值拒绝 ====================

    [Fact]
    public async Task 台账按父记录类型状态日期与关键字过滤_分页有界且未知筛选取值拒绝()
    {
        using var db = TestDbFactory.Create();
        var bookingA = SeedBooking(db, "DG-058-I");
        var bookingB = SeedBooking(db, "DG-058-J");
        var referenceA = SeedReference(db, bookingA.Id, sourceNo: "DG-058-I");
        var referenceB = SeedReference(db, bookingB.Id, sourceNo: "DG-058-J");
        var controller = BuildController(db);

        await RecordAsync(controller, Dto(referenceA.Id, ContainerShipmentMilestoneRules.EventTypeActualDeparture,
            new DateTime(2026, 9, 12, 8, 0, 0), "船公司网站截图", "第一条", "张三"));
        var departure = (await QueryAsync(controller, new ContainerShipmentMilestoneQuery
        {
            ContainerShipmentReferenceId = referenceA.Id,
            EventType = ContainerShipmentMilestoneRules.EventTypeActualDeparture
        })).Items[0];
        var inspection = await RecordAsync(controller, Dto(referenceA.Id,
            ContainerShipmentMilestoneRules.EventTypeInspection,
            new DateTime(2026, 9, 14, 8, 0, 0), "报关行通知", "第二条", "李四"));
        await RecordAsync(controller, Dto(referenceB.Id, ContainerShipmentMilestoneRules.EventTypeActualArrival,
            new DateTime(2026, 10, 2, 8, 0, 0), "货代邮件", "第三条", "王五"));
        await controller.Void(inspection.Id, new ContainerShipmentMilestoneVoidRequest { Reason = "日期录错" });

        // 按父出运引用过滤（默认包含已作废历史）
        var byParent = await QueryAsync(controller, new ContainerShipmentMilestoneQuery
        {
            ContainerShipmentReferenceId = referenceA.Id
        });
        Assert.Equal(2, byParent.Items.Count);
        Assert.DoesNotContain(byParent.Items, r => r.ContainerShipmentReferenceId == referenceB.Id);

        // 按事件类型过滤（忽略大小写）
        var byType = await QueryAsync(controller, new ContainerShipmentMilestoneQuery { EventType = " INSPECTION " });
        Assert.Equal(inspection.Id, Assert.Single(byType.Items).Id);

        // 状态过滤
        Assert.Equal(3, (await QueryAsync(controller)).Total);
        Assert.Equal(2, (await QueryAsync(controller,
            new ContainerShipmentMilestoneQuery { Status = ContainerShipmentMilestoneRules.StatusRecorded })).Items.Count);
        Assert.Equal(inspection.Id, Assert.Single((await QueryAsync(controller,
            new ContainerShipmentMilestoneQuery { Status = ContainerShipmentMilestoneRules.StatusVoided })).Items).Id);

        // 事件时间区间（含当天边界）
        var inRange = await QueryAsync(controller, new ContainerShipmentMilestoneQuery
        {
            EventDateFrom = new DateTime(2026, 9, 13),
            EventDateTo = new DateTime(2026, 9, 14)
        });
        Assert.Equal(inspection.Id, Assert.Single(inRange.Items).Id);

        // 关键字只匹配来源说明 / 备注 / 记录人（不匹配父记录文本）
        Assert.Single((await QueryAsync(controller, new ContainerShipmentMilestoneQuery { Keyword = "报关行" })).Items);
        Assert.Single((await QueryAsync(controller, new ContainerShipmentMilestoneQuery { Keyword = "第三条" })).Items);
        Assert.Single((await QueryAsync(controller, new ContainerShipmentMilestoneQuery { Keyword = "李四" })).Items);
        Assert.Empty((await QueryAsync(controller, new ContainerShipmentMilestoneQuery { Keyword = "DG-058-I" })).Items);

        // 排序：事件时间倒序（时间线一眼可读）
        var ordered = await QueryAsync(controller);
        Assert.Equal(
            new[] { new DateTime(2026, 10, 2, 8, 0, 0), new DateTime(2026, 9, 14, 8, 0, 0), new DateTime(2026, 9, 12, 8, 0, 0) },
            ordered.Items.Select(r => r.EventAt).ToArray());
        Assert.True(ordered.Items[0].IsRecorded);

        // 分页有界：每页上限截断、页码 / 每页参数被修正、翻页位移正确
        var huge = await QueryAsync(controller, new ContainerShipmentMilestoneQuery { PageSize = 100_000 });
        Assert.Equal(ContainerShipmentMilestoneQuery.MaxPageSize, huge.PageSize);
        Assert.Equal(3, huge.Items.Count);
        var secondPage = await QueryAsync(controller, new ContainerShipmentMilestoneQuery { Page = 2, PageSize = 2 });
        Assert.Equal(2, secondPage.Page);
        Assert.Single(secondPage.Items);
        Assert.Equal(departure.Id, secondPage.Items[0].Id);
        Assert.Equal(referenceA.Id, secondPage.Items[0].ContainerShipmentReferenceId);
        var clamped = await QueryAsync(controller, new ContainerShipmentMilestoneQuery { Page = 0, PageSize = 0 });
        Assert.Equal(1, clamped.Page);
        Assert.Equal(ContainerShipmentMilestoneQuery.DefaultPageSize, clamped.PageSize);

        // 未知筛选取值 / 超长关键字一律拒绝，不静默忽略（也不会返回全量）
        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => controller.GetPaged(
            new ContainerShipmentMilestoneQuery { Status = 3 }));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => controller.GetPaged(
            new ContainerShipmentMilestoneQuery { EventType = "customs" }));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => controller.GetPaged(
            new ContainerShipmentMilestoneQuery
            {
                Keyword = new string('k', ContainerShipmentMilestoneRules.MaxKeywordLength + 1)
            }));
    }

    // ==================== 9. 有界数据集访问（无逐行查库）与只读不写库 ====================

    [Fact]
    public async Task 台账读取是固定数量的数据集访问_分页行数变化不改变访问次数且只读不写库()
    {
        using var db = TestDbFactory.Create();
        var booking = SeedBooking(db, "DG-058-K");
        var reference = SeedReference(db, booking.Id);
        db.ContainerShipmentMilestones.Add(new ContainerShipmentMilestone
        {
            ContainerShipmentReferenceId = reference.Id,
            EventType = ContainerShipmentMilestoneRules.EventTypeActualDeparture,
            EventAt = DefaultEventAt,
            Status = ContainerShipmentMilestoneRules.StatusRecorded,
            CreatedAt = DateTime.Now
        });
        await db.SaveChangesAsync();

        var counting = CountingDbContext.Wrap(db);
        var single = await ContainerShipmentMilestoneService.ListAsync(
            counting.Proxy, new ContainerShipmentMilestoneQuery { PageSize = 1 });
        var singleReads = counting.DatasetReads;

        Assert.Equal(1, single.Total);
        // 常数级访问：里程碑数据集（计数 + 本页）与父记录数据集（批量标注）各一次，无逐行查库
        Assert.Equal(2, singleReads);
        Assert.Equal(
            new[]
            {
                nameof(IErpDbContext.ContainerShipmentMilestones),
                nameof(IErpDbContext.ContainerShipmentReferences)
            },
            counting.ReadProperties.Distinct().ToArray());

        // 再补 300 条（跨多页）：访问次数必须保持不变（无逐行查库）
        for (var i = 0; i < 300; i++)
        {
            db.ContainerShipmentMilestones.Add(new ContainerShipmentMilestone
            {
                ContainerShipmentReferenceId = reference.Id,
                EventType = ContainerShipmentMilestoneRules.EventTypeActualDeparture,
                EventAt = DefaultEventAt.AddMinutes(i + 1),
                Status = ContainerShipmentMilestoneRules.StatusRecorded,
                CreatedAt = DateTime.Now
            });
        }
        await db.SaveChangesAsync();

        var large = await ContainerShipmentMilestoneService.ListAsync(
            counting.Proxy, new ContainerShipmentMilestoneQuery { PageSize = 200 });
        var largeReads = counting.DatasetReads - singleReads;

        Assert.Equal(301, large.Total);
        Assert.Equal(ContainerShipmentMilestoneQuery.MaxPageSize, large.Items.Count);   // 单页有界（上限 200）
        Assert.Equal(singleReads, largeReads);                                          // 行数 / 页大小变化不改变访问次数
        Assert.Equal(0, counting.WriteCalls);                                           // 只读：不落库
        Assert.All(large.Items, r => Assert.Equal("订柜信息", r.ParentSourceTypeText));
    }

    // ==================== 10. 父记录候选：有界、显式选择、批量统计 ====================

    [Fact]
    public async Task 父记录候选有界并批量统计里程碑数_无里程碑的出运引用无需回填()
    {
        using var db = TestDbFactory.Create();
        var bookingA = SeedBooking(db, "DG-058-L");
        var bookingB = SeedBooking(db, "DG-058-M");
        var bookingC = SeedBooking(db, "DG-058-N");
        var withMilestones = SeedReference(db, bookingA.Id, sourceNo: "DG-058-L", containerNo: "CONT-L");
        var empty = SeedReference(db, bookingB.Id, sourceNo: "DG-058-M", containerNo: "CONT-M");
        var voided = SeedReference(db, bookingC.Id, sourceNo: "DG-058-N",
            status: ContainerShipmentReferenceRules.StatusVoided);
        var controller = BuildController(db);

        var first = await RecordAsync(controller, Dto(
            withMilestones.Id, ContainerShipmentMilestoneRules.EventTypeActualDeparture, DefaultEventAt));
        await RecordAsync(controller, Dto(
            withMilestones.Id, ContainerShipmentMilestoneRules.EventTypeInspection, DefaultEventAt.AddHours(2)));
        await controller.Void(first.Id, new ContainerShipmentMilestoneVoidRequest { Reason = "重复登记" });

        var candidates = AssertOk<List<ContainerShipmentMilestoneParentCandidateDto>>(
            await controller.ParentCandidates(null, ContainerShipmentMilestoneRules.MaxParentCandidates));

        // 只列出未删除、未作废的出运引用（已作废的父记录不再出现在候选中）
        Assert.Equal(2, candidates.Count);
        Assert.DoesNotContain(candidates, c => c.ContainerShipmentReferenceId == voided.Id);

        var hit = candidates.Single(c => c.ContainerShipmentReferenceId == withMilestones.Id);
        Assert.Equal(2, hit.MilestoneCount);              // 含已作废历史
        Assert.Equal(1, hit.ActiveMilestoneCount);        // 有效证据只有 1 条
        Assert.Contains("已有 2 条里程碑", hit.EligibilityText);
        Assert.Equal("订柜信息", hit.SourceTypeText);
        Assert.Equal("待提交", hit.SourceStatusText);

        // 没有任何里程碑的出运引用照常出现在候选中（历史记录不需要任何回填）
        var without = candidates.Single(c => c.ContainerShipmentReferenceId == empty.Id);
        Assert.Equal(0, without.MilestoneCount);
        Assert.Equal(0, without.ActiveMilestoneCount);
        Assert.Contains("当前没有任何里程碑", without.EligibilityText);

        // 关键字只按父记录的显式字段过滤（源单号 / 柜号 / B/L / S/O / 承运人）
        var byKeyword = AssertOk<List<ContainerShipmentMilestoneParentCandidateDto>>(
            await controller.ParentCandidates("CONT-M", ContainerShipmentMilestoneRules.MaxParentCandidates));
        Assert.Equal(empty.Id, Assert.Single(byKeyword).ContainerShipmentReferenceId);
        var byOrderNo = AssertOk<List<ContainerShipmentMilestoneParentCandidateDto>>(
            await controller.ParentCandidates("DG-058-L", ContainerShipmentMilestoneRules.MaxParentCandidates));
        Assert.Equal(withMilestones.Id, Assert.Single(byOrderNo).ContainerShipmentReferenceId);

        // 候选有界：take 收敛到上限（不报错），且总数不超过上限
        Assert.Single(AssertOk<List<ContainerShipmentMilestoneParentCandidateDto>>(
            await controller.ParentCandidates(null, 1)));
        Assert.Equal(candidates.Count, AssertOk<List<ContainerShipmentMilestoneParentCandidateDto>>(
            await controller.ParentCandidates(null, 100_000)).Count);
        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => controller.ParentCandidates(
            new string('k', ContainerShipmentMilestoneRules.MaxKeywordLength + 1), 200));
    }

    // ==================== 11. 登记 / 作废均不改写相邻业务记录 ====================

    [Fact]
    public async Task 登记与作废均不改写相邻业务记录()
    {
        using var db = TestDbFactory.Create();
        var customer = new BaseCustomer
        {
            CustomerCode = "C-058",
            CustomerName = "里程碑测试客户",
            Status = 1,
            CreditStatus = "正常",
            CreditLimit = 50000m
        };
        var order = new SalesOrder
        {
            OrderNo = "SO-058",
            OrderDate = new DateTime(2026, 9, 1),
            CustomerId = 1,
            Currency = Currency.USD,
            TotalAmount = 1000m,
            Status = DocumentStatus.Approved
        };
        var receipt = new FinanceReceipt
        {
            ReceiptNo = "SK-058",
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

        var booking = SeedBooking(db, "DG-058-O");
        var preLoading = SeedPreLoading(db, "YZ-058-O", "CONT-O", booking.Id);
        var loadingList = SeedLoadingList(db, "ZJ-058-O", "CONT-O", preLoading.Id);
        var reference = SeedReference(db, booking.Id, sourceNo: "SO-058");
        var controller = BuildController(db);

        var recorded = await RecordAsync(controller, Dto(
            reference.Id, ContainerShipmentMilestoneRules.EventTypeCustomsRelease,
            DefaultEventAt, "报关行通知", "放行凭证由报关行口头通知", "张三"));
        await controller.Void(recorded.Id, new ContainerShipmentMilestoneVoidRequest
        {
            Reason = "相邻记录非变更测试"
        });

        // 客户 / 销售订单 / 收款单 / 库存与成本：完全不变
        var storedCustomer = await db.BaseCustomers.AsNoTracking().FirstAsync(o => o.Id == customer.Id);
        Assert.Equal("C-058", storedCustomer.CustomerCode);
        Assert.Equal(1, storedCustomer.Status);
        Assert.Equal(50000m, storedCustomer.CreditLimit);
        Assert.Equal("正常", storedCustomer.CreditStatus);

        var storedOrder = await db.SalesOrders.AsNoTracking().FirstAsync(o => o.Id == order.Id);
        Assert.Equal(1000m, storedOrder.TotalAmount);
        Assert.Equal(DocumentStatus.Approved, storedOrder.Status);
        Assert.Equal(Currency.USD, storedOrder.Currency);

        var storedReceipt = await db.FinanceReceipts.AsNoTracking().FirstAsync(o => o.Id == receipt.Id);
        Assert.Equal(1000m, storedReceipt.Amount);
        Assert.Equal(DocumentStatus.Approved, storedReceipt.Status);
        Assert.Equal(PaymentMethod.BankTransfer, storedReceipt.PaymentMethod);

        var storedStock = await db.Stocks.AsNoTracking().FirstAsync(o => o.Id == stock.Id);
        Assert.Equal(120m, storedStock.Quantity);
        Assert.Equal(120m, storedStock.AvailableQuantity);
        Assert.Equal(10.5m, storedStock.AverageCost);

        // 订柜信息 / 预装柜单 / 装柜清单与出运引用行同样不变
        var storedBooking = await db.ContainerBookings.AsNoTracking().FirstAsync(o => o.Id == booking.Id);
        Assert.Equal("DG-058-O", storedBooking.BookingNo);
        Assert.Equal(DocumentStatus.Pending, storedBooking.Status);
        Assert.Null(storedBooking.Atd);
        Assert.Null(storedBooking.Ata);
        Assert.Null(storedBooking.CustomsReleaseDate);
        Assert.Equal(booking.UpdatedAt, storedBooking.UpdatedAt);

        Assert.Equal(DocumentStatus.Pending,
            (await db.ContainerPreLoadings.AsNoTracking().FirstAsync(o => o.Id == preLoading.Id)).Status);
        Assert.Equal(DocumentStatus.Pending,
            (await db.ContainerLoadingLists.AsNoTracking().FirstAsync(o => o.Id == loadingList.Id)).Status);

        var storedReference = await db.ContainerShipmentReferences.AsNoTracking()
            .FirstAsync(o => o.Id == reference.Id);
        Assert.Equal(ContainerShipmentReferenceRules.StatusRecorded, storedReference.Status);
        Assert.Equal(1, storedReference.RevisionNo);
        Assert.Null(storedReference.LastRevisedAt);

        // 本模块只写自己的一张表（1 条里程碑，作废只改状态）
        Assert.Equal(1, await db.ContainerShipmentMilestones.CountAsync());
        // 里程碑不会产生出运引用修订留痕（ERP-057 修订表保持为空）
        Assert.Equal(0, await db.ContainerShipmentReferenceRevisions.CountAsync());
    }

    // ==================== 12. 纯规则：事件类型 / 状态 / 时间 / 边界文案 ====================

    [Fact]
    public void 纯规则_事件类型状态时间与边界文案()
    {
        // 事件类型 allowlist（忽略大小写与首尾空白；自由文本一律拒绝）
        Assert.Equal(ContainerShipmentMilestoneRules.EventTypeActualDeparture,
            ContainerShipmentMilestoneRules.NormalizeEventType(" ACTUAL-DEPARTURE "));
        Assert.Equal(ContainerShipmentMilestoneRules.EventTypeCustomsRelease,
            ContainerShipmentMilestoneRules.NormalizeEventType("Customs-Release"));
        Assert.True(ContainerShipmentMilestoneRules.IsSupportedEventType("INSPECTION"));
        Assert.False(ContainerShipmentMilestoneRules.IsSupportedEventType("customs-hold"));
        Assert.Equal(4, ContainerShipmentMilestoneRules.SupportedEventTypes.Count);
        Assert.Equal(ErrorCodes.InvalidParameter,
            Assert.Throws<BusinessException>(() => ContainerShipmentMilestoneRules.NormalizeEventType("  ")).Code);
        Assert.Equal("实际开船 / 离港", ContainerShipmentMilestoneRules.EventTypeText("actual-departure"));
        Assert.Equal("实际到港 / 抵达", ContainerShipmentMilestoneRules.EventTypeText("ACTUAL-ARRIVAL"));
        Assert.Contains("未知", ContainerShipmentMilestoneRules.EventTypeText("customs-hold"));

        // 状态机与状态过滤（未知状态照实说明，不抛异常、不按「已登记」兜底）
        Assert.Equal("已登记", ContainerShipmentMilestoneRules.StatusText(ContainerShipmentMilestoneRules.StatusRecorded));
        Assert.Equal("已作废", ContainerShipmentMilestoneRules.StatusText(ContainerShipmentMilestoneRules.StatusVoided));
        Assert.Contains("未知", ContainerShipmentMilestoneRules.StatusText(9));
        Assert.Null(ContainerShipmentMilestoneRules.NormalizeStatusFilter(null));
        Assert.Equal(ContainerShipmentMilestoneRules.StatusVoided,
            ContainerShipmentMilestoneRules.NormalizeStatusFilter(ContainerShipmentMilestoneRules.StatusVoided));
        Assert.Equal(ErrorCodes.InvalidParameter, Assert.Throws<BusinessException>(
            () => ContainerShipmentMilestoneRules.NormalizeStatusFilter(3)).Code);
        Assert.Null(ContainerShipmentMilestoneRules.NormalizeEventTypeFilter("  "));
        Assert.Equal(ErrorCodes.InvalidParameter, Assert.Throws<BusinessException>(
            () => ContainerShipmentMilestoneRules.NormalizeEventTypeFilter("customs")).Code);

        // 事件时间：必填 + 有界（绝不推断）
        Assert.Equal(DefaultEventAt, ContainerShipmentMilestoneRules.NormalizeEventAt(DefaultEventAt));
        Assert.Equal(ErrorCodes.InvalidParameter, Assert.Throws<BusinessException>(
            () => ContainerShipmentMilestoneRules.NormalizeEventAt(null)).Code);
        Assert.Equal(ErrorCodes.InvalidParameter, Assert.Throws<BusinessException>(
            () => ContainerShipmentMilestoneRules.NormalizeEventAt(new DateTime(1999, 12, 31))).Code);
        Assert.Equal(ErrorCodes.InvalidParameter, Assert.Throws<BusinessException>(
            () => ContainerShipmentMilestoneRules.NormalizeEventAt(new DateTime(2100, 1, 1))).Code);

        // 文本规范化与有界（超长拒绝，不静默截断）
        Assert.Equal("来源", ContainerShipmentMilestoneRules.NormalizeSourceDescription("  来源  "));
        Assert.Equal("记录人", ContainerShipmentMilestoneRules.NormalizeRecordedBy(" 记录人 "));
        Assert.Equal(string.Empty, ContainerShipmentMilestoneRules.NormalizeNotes(null));
        Assert.Contains("来源说明", Assert.Throws<BusinessException>(
            () => ContainerShipmentMilestoneRules.NormalizeSourceDescription(
                new string('源', ContainerShipmentMilestoneRules.MaxSourceDescriptionLength + 1))).Message);
        Assert.Equal(ErrorCodes.InvalidParameter, Assert.Throws<BusinessException>(
            () => ContainerShipmentMilestoneRules.NormalizeVoidReason(" ")).Code);
        Assert.Equal(ErrorCodes.InvalidParameter, Assert.Throws<BusinessException>(
            () => ContainerShipmentMilestoneRules.NormalizeVoidReason(
                new string('因', ContainerShipmentMilestoneRules.MaxVoidReasonLength + 1))).Code);

        // 证据性质与父记录资格 / 可用性文案
        Assert.True(ContainerShipmentMilestoneRules.IsInspectionOrRelease("inspection"));
        Assert.True(ContainerShipmentMilestoneRules.IsInspectionOrRelease("CUSTOMS-RELEASE"));
        Assert.False(ContainerShipmentMilestoneRules.IsInspectionOrRelease("actual-arrival"));
        Assert.Contains("不是海关决定", ContainerShipmentMilestoneRules.EvidenceCategoryText("customs-release"));
        Assert.Contains("不是承运人确认", ContainerShipmentMilestoneRules.EvidenceCategoryText("actual-departure"));
        Assert.False(ContainerShipmentMilestoneRules.EvaluateParentEligibility(false, false, 1).Eligible);
        Assert.False(ContainerShipmentMilestoneRules.EvaluateParentEligibility(true, true, 1).Eligible);
        Assert.False(ContainerShipmentMilestoneRules.EvaluateParentEligibility(
            true, false, ContainerShipmentReferenceRules.StatusVoided).Eligible);
        Assert.True(ContainerShipmentMilestoneRules.EvaluateParentEligibility(true, false, 1).Eligible);
        Assert.Contains("已作废", ContainerShipmentMilestoneRules.ParentAvailabilityText(true, true));
        Assert.Contains("已删除或不存在", ContainerShipmentMilestoneRules.ParentAvailabilityText(false, false));
        Assert.Equal("未知（7）", ContainerShipmentMilestoneRules.ParentStatusText(7));
        Assert.Equal(ErrorCodes.RuleConflict, Assert.Throws<BusinessException>(
            () => ContainerShipmentMilestoneRules.EnsureRecordedForVoid(
                ContainerShipmentMilestoneRules.StatusVoided)).Code);
        ContainerShipmentMilestoneRules.EnsureRecordedForVoid(ContainerShipmentMilestoneRules.StatusRecorded);

        // 文案与 ERP-040 / ERP-057 同一口径（未知 = 未知，不回落；边界声明同源）
        Assert.Equal(ContainerShipmentTrackingRules.UnknownText, ContainerShipmentMilestoneRules.UnknownText);
        Assert.Contains("不会按", ContainerShipmentMilestoneRules.ParentLinkText);
        Assert.Contains("不是承运人", ContainerShipmentMilestoneRules.EvidenceText);
        Assert.Contains("出运许可", ContainerShipmentMilestoneRules.EvidenceText);
        Assert.Contains("不轮询", ContainerShipmentMilestoneRules.BoundaryText);
        Assert.Contains("不提供硬删除", ContainerShipmentMilestoneRules.RuleText);
    }

    // ==================== 13. ERP-058 审计：里程碑只挂在 ERP-057 出运引用之下 ====================

    [Fact]
    public void ERP058审计_里程碑只挂在ERP057出运引用之下_不改写任何既有记录()
    {
        // 1) 里程碑只有一套模型：挂 ERP-057 出运引用之下（不新建出运 / 跟踪主数据）
        var milestoneSets = typeof(IErpDbContext).GetProperties()
            .Where(p => p.Name.Contains("Milestone", StringComparison.Ordinal)).ToList();
        Assert.Single(milestoneSets);
        Assert.Equal("ContainerShipmentMilestones", milestoneSets[0].Name);
        Assert.Equal(typeof(DbSet<ContainerShipmentMilestone>), milestoneSets[0].PropertyType);

        var parentSets = typeof(IErpDbContext).GetProperties()
            .Where(p => p.Name.Contains("ShipmentReference", StringComparison.Ordinal)
                        && !p.Name.Contains("Revision", StringComparison.Ordinal)
                        && !p.Name.Contains("Milestone", StringComparison.Ordinal)).ToList();
        Assert.Single(parentSets);      // ERP-057 的出运引用登记册仍然是唯一父模型

        // 2) 里程碑只按显式父 Id 关联：没有柜号 / 单号 / 客户等自由文本列，也没有外键与导航属性
        var milestoneProperties = typeof(ContainerShipmentMilestone).GetProperties().Select(p => p.Name).ToList();
        Assert.Contains(nameof(ContainerShipmentMilestone.ContainerShipmentReferenceId), milestoneProperties);
        foreach (var forbidden in new[]
                 {
                     "ContainerNo", "BookingNo", "SourceNo", "BillOfLadingNo", "ShippingOrderNo",
                     "CustomerId", "ReferenceId", "Atd", "Ata", "PlannedDepartureAt", "PlannedArrivalAt"
                 })
            Assert.DoesNotContain(forbidden, milestoneProperties);

        using (var db = TestDbFactory.Create())
        {
            var entityType = db.Model.FindEntityType(typeof(ContainerShipmentMilestone));
            Assert.NotNull(entityType);
            Assert.Empty(entityType!.GetForeignKeys());
            Assert.Empty(entityType.GetNavigations());
        }

        // 3) 装柜三单与出运引用都没有新增任何「里程碑」列（本模块只在新建表里留痕）
        foreach (var type in new[]
                 {
                     typeof(ContainerBooking), typeof(ContainerPreLoading),
                     typeof(ContainerLoadingList), typeof(ContainerShipmentReference)
                 })
        {
            var names = type.GetProperties().Select(p => p.Name).ToList();
            Assert.DoesNotContain("EventType", names);
            Assert.DoesNotContain("EventAt", names);
            Assert.DoesNotContain("MilestoneId", names);
            Assert.DoesNotContain("MilestoneCount", names);
            Assert.DoesNotContain(names, n => n.Contains("Milestone", StringComparison.Ordinal));
        }

        // 4) 服务只按 AsNoTracking 读取父出运引用：既不碰装柜三单，也不回写 ERP-040 的跟踪列
        var service = File.ReadAllText(RepoFile(
            "src", "ERP.Application", "Services", "ContainerShipmentMilestoneService.cs"));
        Assert.Contains("db.ContainerShipmentReferences.AsNoTracking()", service);
        foreach (var forbidden in new[] { "ContainerBookings", "ContainerPreLoadings", "ContainerLoadingLists" })
            Assert.DoesNotContain(forbidden, service);
        Assert.DoesNotContain("db.ContainerShipmentReferences.Add", service);
        Assert.DoesNotContain("db.ContainerShipmentReferences.Update", service);
        Assert.DoesNotContain("db.ContainerShipmentReferences.Remove", service);
        Assert.DoesNotContain("InspectionDate", service);          // 不复制 / 不回写订柜查验与放行列
        Assert.DoesNotContain("CustomsReleaseDate", service);
        Assert.DoesNotContain("Atd", service);
        Assert.DoesNotContain("Ata", service);
        Assert.DoesNotContain("HttpClient", service);              // 不轮询任何外部系统

        // 5) 控制器只编排里程碑服务：没有 DDL、没有外部系统调用
        var controller = File.ReadAllText(RepoFile(
            "src", "ERP.Api", "Controllers", "ContainerShipmentMilestoneController.cs"));
        Assert.Contains("ContainerShipmentMilestoneService", controller);
        Assert.DoesNotContain("SqlRaw", controller);
        Assert.DoesNotContain("HttpClient", controller);

        // 6) 界面文案与服务端同源：明确「不是承运人 / 海关 / 货代确认」且不推断
        var js = File.ReadAllText(RepoFile(
            "src", "ERP.Api", "wwwroot", "js", "container-shipment-milestones.js"));
        Assert.Contains("不是承运人 / 海关 / 货代的确认或回执", js);
        Assert.Contains("不是海关决定", js);
        Assert.Contains("不会自动推进装柜", js);
        Assert.Contains("不按计划时间", js);
        Assert.Contains("本登记册只按显式的", js);
    }

    // ==================== 14. 模型配置契约：长度 / 过滤唯一索引 / 刻意不建外键 ====================

    [Fact]
    public void 模型配置契约_长度过滤唯一索引与刻意不建外键()
    {
        using var db = TestDbFactory.Create();

        var entityType = db.Model.FindEntityType(typeof(ContainerShipmentMilestone));
        Assert.NotNull(entityType);

        Assert.Equal(20, entityType!.FindProperty(nameof(ContainerShipmentMilestone.EventType))!.GetMaxLength());
        Assert.Equal(200,
            entityType.FindProperty(nameof(ContainerShipmentMilestone.SourceDescription))!.GetMaxLength());
        Assert.Equal(500, entityType.FindProperty(nameof(ContainerShipmentMilestone.Notes))!.GetMaxLength());
        Assert.Equal(100, entityType.FindProperty(nameof(ContainerShipmentMilestone.RecordedBy))!.GetMaxLength());
        Assert.Equal(500, entityType.FindProperty(nameof(ContainerShipmentMilestone.VoidReason))!.GetMaxLength());

        // 同一父记录 + 事件类型 + 事件时间最多一条有效证据（与幂等建表脚本同名同过滤条件）
        var uniqueIndex = Assert.Single(entityType.GetIndexes(), i => i.IsUnique);
        Assert.Equal("UX_ContainerShipmentMilestones_ActiveIdentity", uniqueIndex.GetDatabaseName());
        Assert.Equal("IsDeleted = 0 AND Status <> 2", uniqueIndex.GetFilter());

        var indexNames = entityType.GetIndexes().Select(i => i.GetDatabaseName()).ToList();
        Assert.Contains("IX_ContainerShipmentMilestones_Parent_Status_EventAt", indexNames);
        Assert.Contains("IX_ContainerShipmentMilestones_EventType_EventAt", indexNames);
        Assert.Contains("IX_ContainerShipmentMilestones_Status_RecordedAt", indexNames);

        // 刻意不建任何外键与导航属性（父出运引用可能被软删除 / 作废，历史里程碑必须始终可读）
        Assert.Empty(entityType.GetForeignKeys());
        Assert.Empty(entityType.GetNavigations());

        // 读取侧标注一律 [NotMapped]（不落库）
        foreach (var name in new[]
                 {
                     nameof(ContainerShipmentMilestone.EventTypeText),
                     nameof(ContainerShipmentMilestone.StatusText),
                     nameof(ContainerShipmentMilestone.ParentAvailable),
                     nameof(ContainerShipmentMilestone.ParentAvailabilityText),
                     nameof(ContainerShipmentMilestone.ParentSourceType),
                     nameof(ContainerShipmentMilestone.ParentSourceTypeText),
                     nameof(ContainerShipmentMilestone.ParentSourceNo),
                     nameof(ContainerShipmentMilestone.ParentContainerNo),
                     nameof(ContainerShipmentMilestone.ParentStatusText),
                     nameof(ContainerShipmentMilestone.EvidenceCategoryText),
                     nameof(ContainerShipmentMilestone.BoundaryText)
                 })
            Assert.Null(entityType.FindProperty(name));
    }

    // ==================== 15. 幂等结构升级：只建表建索引，不含任何回填或写语句 ====================

    [Fact]
    public void Schema_upgrade_幂等建表建索引且不含任何回填或写语句()
    {
        var script = File.ReadAllText(
            RepoFile("src", "ERP.Infrastructure", "Data", "SchemaUpgrader.cs"));

        Assert.Contains("IF OBJECT_ID('db_owner.ContainerShipmentMilestones') IS NULL", script);
        Assert.Contains("CREATE TABLE db_owner.ContainerShipmentMilestones", script);
        Assert.Contains("ContainerShipmentReferenceId BIGINT NOT NULL", script);
        Assert.Contains("EventType NVARCHAR(20) NOT NULL DEFAULT N''", script);
        Assert.Contains("EventAt DATETIME2 NOT NULL", script);
        Assert.Contains("SourceDescription NVARCHAR(200) NOT NULL DEFAULT N''", script);
        Assert.Contains("Notes NVARCHAR(500) NOT NULL DEFAULT N''", script);
        Assert.Contains("RecordedBy NVARCHAR(100) NOT NULL DEFAULT N''", script);
        Assert.Contains("Status INT NOT NULL DEFAULT 1", script);
        Assert.Contains("VoidedAt DATETIME2 NULL", script);
        Assert.Contains("VoidReason NVARCHAR(500) NOT NULL DEFAULT N''", script);

        Assert.Contains("CREATE UNIQUE INDEX UX_ContainerShipmentMilestones_ActiveIdentity", script);
        Assert.Contains("WHERE IsDeleted = 0 AND Status <> 2;", script);
        Assert.Contains("CREATE INDEX IX_ContainerShipmentMilestones_Parent_Status_EventAt", script);
        Assert.Contains("CREATE INDEX IX_ContainerShipmentMilestones_EventType_EventAt", script);
        Assert.Contains("CREATE INDEX IX_ContainerShipmentMilestones_Status_RecordedAt", script);

        // 不建外键
        Assert.DoesNotContain("FK_ContainerShipmentMilestone", script);

        // 本模块段落只建表 + 过滤索引：不做任何回填，也不修改任何既有表
        // （父出运引用与装柜三单只由 ERP-040 / ERP-041 / ERP-057 等既有段落维护）
        var start = script.IndexOf("// 39. 装柜出运里程碑证据", StringComparison.Ordinal);
        Assert.True(start > 0);
        var segment = script[start..];
        Assert.DoesNotContain("ALTER TABLE", segment);
        Assert.DoesNotContain("UPDATE db_owner", segment);
        Assert.DoesNotContain("INSERT INTO db_owner", segment);
        Assert.DoesNotContain("DELETE FROM db_owner", segment);
        Assert.DoesNotContain("ContainerShipmentReferences", segment);
        Assert.DoesNotContain("ContainerBookings", segment);
        Assert.DoesNotContain("ContainerPreLoadings", segment);
        Assert.DoesNotContain("ContainerLoadingLists", segment);
    }

    // ==================== 16. 前端与路由接线契约 ====================

    [Fact]
    public void 前端与路由接线契约()
    {
        var index = File.ReadAllText(RepoFile("src", "ERP.Api", "wwwroot", "index.html"));
        Assert.Contains("/js/container-shipment-milestones.js", index);

        // ERP-057 出运引用台账提供两个入口：行操作「里程碑」（带父 Id）与工具栏「里程碑登记册」
        var referencesJs = File.ReadAllText(
            RepoFile("src", "ERP.Api", "wwwroot", "js", "container-shipment-references.js"));
        Assert.Contains("openContainerShipmentMilestones(${row.id})", referencesJs);
        Assert.Contains("openContainerShipmentMilestones()", referencesJs);
        Assert.Contains("🛣 里程碑登记册", referencesJs);

        var js = File.ReadAllText(
            RepoFile("src", "ERP.Api", "wwwroot", "js", "container-shipment-milestones.js"));
        Assert.Contains("async function openContainerShipmentMilestones(referenceId)", js);
        Assert.Contains("const CSM_EVENT_TYPES", js);
        Assert.Contains("{ value: 'actual-departure'", js);
        Assert.Contains("{ value: 'actual-arrival'", js);
        Assert.Contains("{ value: 'inspection'", js);
        Assert.Contains("{ value: 'customs-release'", js);
        Assert.Contains("'/api/container/shipment-milestones'", js);
        Assert.Contains("'/api/container/shipment-milestones?'", js);
        Assert.Contains("/parent-candidates?", js);
        Assert.Contains("'/void'", js);
        Assert.Contains("containerShipmentReferenceId", js);
        Assert.Contains("csmSaveForm", js);
        Assert.Contains("csmConfirmVoid", js);
        Assert.Contains("csmReturnToReferences", js);
        Assert.Contains("不提供修改接口", js);
        Assert.Contains("不静默合并", js);
        Assert.Contains("不是海关决定", js);
        Assert.Contains("父记录不可用", js);

        var controller = File.ReadAllText(RepoFile(
            "src", "ERP.Api", "Controllers", "ContainerShipmentMilestoneController.cs"));
        Assert.Contains("[Route(\"api/container/shipment-milestones\")]", controller);
        Assert.Contains("parent-candidates", controller);
        Assert.Contains("{id:long}/void", controller);
        Assert.Contains("[HttpGet]", controller);
        Assert.Contains("[HttpPost]", controller);
    }

    /// <summary>
    /// 只读计数上下文代理（<see cref="DispatchProxy"/>）：记录访问的数据集（<c>DbSet</c> 属性）名称与写入次数，
    /// 用于断言「分页 / 有界查询」「无逐行查库」与「只读不写库」；不改动生产代码。
    /// </summary>
    public class CountingDbContext : DispatchProxy
    {
        private IErpDbContext _inner = null!;

        /// <summary>包装后的上下文（服务 / 控制器按 <see cref="IErpDbContext"/> 使用）</summary>
        public IErpDbContext Proxy { get; private set; } = null!;

        /// <summary>数据集（<c>DbSet</c> 属性）访问次数：即本次查询实际发起的数据集访问次数</summary>
        public int DatasetReads => ReadProperties.Count;

        /// <summary>被访问的数据集属性名（本模块预期只有里程碑表与父出运引用表）</summary>
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
