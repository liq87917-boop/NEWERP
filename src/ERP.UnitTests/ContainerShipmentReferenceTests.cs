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
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// 装柜出运引用登记单元测试（ERP-057）。覆盖：三种源记录类型（订柜信息 / 预装柜单 / 装柜清单）的显式关联与
/// 服务端快照、出运方式 allowlist 与「未知不推断」、计划时间一致性与有界校验、文本 / 来源校验、
/// 不存在 / 已删除 / 重复源记录拒绝、源记录不允许改派、修订留痕（修订前原值 + 必填原因）、
/// 作废保留历史与重复作废拒绝、作废后重新登记、源记录删除后历史可读与可用性标注、报关行字典项快照与停用标注、
/// 台账过滤分页有界、源记录候选有界与「已有有效引用」标注、历史记录无回填与相邻记录非变更、
/// 以及模型 / 幂等结构 / 前端接线契约与 ERP-057 审计结论（仓库只有一套出运引用模型）。
/// 全部使用内存库（TestDbFactory），不连接 SQL Server、不执行任何 SQL / 部署脚本、不联系任何外部系统、
/// 不做任何浏览器 / UI 验收。
/// </summary>
public class ContainerShipmentReferenceTests
{
    // ==================== 0. 测试脚手架 ====================

    private static ContainerShipmentReferenceController BuildController(ErpDbContext db) => new(db);

    private static ContainerBooking SeedBooking(
        ErpDbContext db, string bookingNo, DateTime? bookingDate = null,
        DocumentStatus status = DocumentStatus.Pending, bool deleted = false)
    {
        var booking = new ContainerBooking
        {
            BookingNo = bookingNo,
            BookingDate = bookingDate ?? new DateTime(2026, 9, 1),
            CustomerId = 100,
            ContainerType = ContainerType.GP40,
            ShippingCompany = "COSCO",
            DeparturePort = "NINGBO",
            DestinationPort = "HAMBURG",
            ShipmentMode = "FCL",
            BillOfLadingNo = "BL-IN-BOOKING",
            Status = status,
            Remark = "订柜备注",
            IsDeleted = deleted
        };
        db.ContainerBookings.Add(booking);
        db.SaveChanges();
        return booking;
    }

    private static ContainerPreLoading SeedPreLoading(
        ErpDbContext db, string no, string containerNo = "CONT-1", long? bookingId = null,
        DocumentStatus status = DocumentStatus.Pending, bool deleted = false)
    {
        var preLoading = new ContainerPreLoading
        {
            PreLoadingNo = no,
            LoadingDate = new DateTime(2026, 9, 3),
            BookingId = bookingId,
            ContainerNo = containerNo,
            SealNo = "SEAL-1",
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
        ErpDbContext db, string no, string containerNo = "CONT-1", long? preLoadingId = null,
        DocumentStatus status = DocumentStatus.Pending, bool deleted = false)
    {
        var loadingList = new ContainerLoadingList
        {
            LoadingListNo = no,
            PreLoadingId = preLoadingId,
            LoadingDate = new DateTime(2026, 9, 5),
            ContainerNo = containerNo,
            CustomerId = 100,
            ShippingMark = "MARK",
            TotalCartons = 100,
            Status = status,
            Remark = "装柜清单备注",
            IsDeleted = deleted
        };
        db.ContainerLoadingLists.Add(loadingList);
        db.SaveChanges();
        return loadingList;
    }

    private static BaseOtherInfo SeedBroker(
        ErpDbContext db, string name, int status = 1, string infoType = "CustomsBroker", bool deleted = false)
    {
        var broker = new BaseOtherInfo
        {
            InfoType = infoType,
            InfoCode = "BG-" + name,
            InfoName = name,
            Status = status,
            SortOrder = 1,
            IsDeleted = deleted
        };
        db.BaseOtherInfos.Add(broker);
        db.SaveChanges();
        return broker;
    }

    private static ContainerShipmentReferenceSaveDto Dto(
        string sourceType, long sourceId, string mode = "", string shippingOrderNo = "",
        string billOfLadingNo = "", string carrier = "", string forwarder = "",
        string departurePort = "", string transitPort = "", string destinationPort = "",
        DateTime? plannedDeparture = null, DateTime? plannedArrival = null,
        string trucker = "", long? brokerId = null, string remark = "")
        => new()
        {
            SourceType = sourceType,
            SourceId = sourceId,
            ShipmentMode = mode,
            ShippingOrderNo = shippingOrderNo,
            BillOfLadingNo = billOfLadingNo,
            CarrierName = carrier,
            ForwarderName = forwarder,
            DeparturePort = departurePort,
            TransitPort = transitPort,
            DestinationPort = destinationPort,
            PlannedDepartureAt = plannedDeparture,
            PlannedArrivalAt = plannedArrival,
            TruckerName = trucker,
            CustomsBrokerId = brokerId,
            Remark = remark
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

    private static async Task<ContainerShipmentReferenceDto> CreateAsync(
        ContainerShipmentReferenceController controller, ContainerShipmentReferenceSaveDto dto)
        => AssertOk<ContainerShipmentReferenceDto>(await controller.Create(dto));

    // ==================== 1. 登记：三种源记录 + 服务端快照 + 不改写装柜链路 ====================

    [Fact]
    public async Task 登记三种源记录_保留服务端快照且不改写装柜链路记录()
    {
        using var db = TestDbFactory.Create();
        var booking = SeedBooking(db, "DG20260901");
        var preLoading = SeedPreLoading(db, "YZ20260903", "CONT-A", booking.Id);
        var loadingList = SeedLoadingList(db, "ZJ20260905", "CONT-A", preLoading.Id);
        var controller = BuildController(db);

        var fromBooking = await CreateAsync(controller, Dto(
            ContainerShipmentReferenceRules.SourceTypeBooking, booking.Id, "FCL", "SO-1", "BL-1",
            "COSCO", "FORWARDER-1", "NINGBO", "SINGAPORE", "HAMBURG",
            new DateTime(2026, 10, 1), new DateTime(2026, 10, 25), "TRUCK-1", null, "首票"));
        var fromPreLoading = await CreateAsync(controller, Dto(
            ContainerShipmentReferenceRules.SourceTypePreLoading, preLoading.Id, "LCL"));
        var fromLoadingList = await CreateAsync(controller, Dto(
            ContainerShipmentReferenceRules.SourceTypeLoadingList, loadingList.Id, "FCL"));

        // 源记录快照一律由服务端按源记录写入
        Assert.Equal("订柜信息", fromBooking.SourceTypeText);
        Assert.Equal("DG20260901", fromBooking.SourceNo);
        Assert.Equal(new DateTime(2026, 9, 1), fromBooking.SourceDate);
        Assert.Equal("待提交", fromBooking.SourceStatusText);
        Assert.Equal(string.Empty, fromBooking.ContainerNo);   // 订柜信息没有柜号 → 未知，不做推断
        Assert.Equal("FCL", fromBooking.ShipmentMode);
        Assert.Equal("整箱 FCL", fromBooking.ShipmentModeText);
        Assert.Equal("SO-1", fromBooking.ShippingOrderNo);
        Assert.Equal("BL-1", fromBooking.BillOfLadingNo);
        Assert.Equal("COSCO", fromBooking.CarrierName);
        Assert.Equal("FORWARDER-1", fromBooking.ForwarderName);
        Assert.Equal("NINGBO", fromBooking.DeparturePort);
        Assert.Equal("SINGAPORE", fromBooking.TransitPort);
        Assert.Equal("HAMBURG", fromBooking.DestinationPort);
        Assert.Equal(new DateTime(2026, 10, 1), fromBooking.PlannedDepartureAt);
        Assert.Equal(new DateTime(2026, 10, 25), fromBooking.PlannedArrivalAt);
        Assert.Equal("TRUCK-1", fromBooking.TruckerName);
        Assert.Equal("首票", fromBooking.Remark);

        Assert.Equal("预装柜单", fromPreLoading.SourceTypeText);
        Assert.Equal("YZ20260903", fromPreLoading.SourceNo);
        Assert.Equal("CONT-A", fromPreLoading.ContainerNo);
        Assert.Equal("拼箱 LCL", fromPreLoading.ShipmentModeText);

        Assert.Equal("装柜清单", fromLoadingList.SourceTypeText);
        Assert.Equal("ZJ20260905", fromLoadingList.SourceNo);
        Assert.Equal("CONT-A", fromLoadingList.ContainerNo);

        foreach (var row in new[] { fromBooking, fromPreLoading, fromLoadingList })
        {
            Assert.Equal(ContainerShipmentReferenceRules.StatusRecorded, row.Status);
            Assert.Equal("已登记", row.StatusText);
            Assert.True(row.IsRecorded);
            Assert.False(row.IsVoided);
            Assert.Equal(1, row.RevisionNo);
            Assert.Equal(0, row.RevisionCount);
            Assert.True(row.SourceAvailable);
            Assert.Contains("不改写", row.BoundaryText);
        }

        // 装柜三单完全不被改写（状态 / 柜号 / 备注 / ERP-040 跟踪值 / 更新时间都不变）
        var storedBooking = await db.ContainerBookings.AsNoTracking().FirstAsync(o => o.Id == booking.Id);
        Assert.Equal(DocumentStatus.Pending, storedBooking.Status);
        Assert.Equal("订柜备注", storedBooking.Remark);
        Assert.Equal("FCL", storedBooking.ShipmentMode);
        Assert.Equal("BL-IN-BOOKING", storedBooking.BillOfLadingNo);
        Assert.Equal(booking.UpdatedAt, storedBooking.UpdatedAt);

        var storedPreLoading = await db.ContainerPreLoadings.AsNoTracking().FirstAsync(o => o.Id == preLoading.Id);
        Assert.Equal("CONT-A", storedPreLoading.ContainerNo);
        Assert.Equal("预装柜备注", storedPreLoading.Remark);
        Assert.Equal(preLoading.UpdatedAt, storedPreLoading.UpdatedAt);

        var storedLoadingList = await db.ContainerLoadingLists.AsNoTracking().FirstAsync(o => o.Id == loadingList.Id);
        Assert.Equal("CONT-A", storedLoadingList.ContainerNo);
        Assert.Equal("装柜清单备注", storedLoadingList.Remark);
        Assert.Equal(loadingList.UpdatedAt, storedLoadingList.UpdatedAt);
    }

    [Fact]
    public async Task 出运方式只接受LCL或FCL_未填写保持未知且绝不推断()
    {
        using var db = TestDbFactory.Create();
        var booking = SeedBooking(db, "DG-1");   // 订柜本身是 FCL，但出运引用可以为未知
        var controller = BuildController(db);

        var lower = await CreateAsync(controller, Dto(
            ContainerShipmentReferenceRules.SourceTypeBooking, booking.Id, " lcl "));
        Assert.Equal("LCL", lower.ShipmentMode);
        Assert.Equal("拼箱 LCL", lower.ShipmentModeText);

        // 未填写 → 未知，绝不从订柜信息的出运方式 / 柜型 / 其它字段推断
        var preLoading = SeedPreLoading(db, "YZ-1", "CONT-B");
        var unknown = await CreateAsync(controller, Dto(
            ContainerShipmentReferenceRules.SourceTypePreLoading, preLoading.Id));
        Assert.Equal(string.Empty, unknown.ShipmentMode);
        Assert.Equal(ContainerShipmentReferenceRules.UnknownText, unknown.ShipmentModeText);
        Assert.Equal(string.Empty, unknown.ShippingOrderNo);
        Assert.Equal(string.Empty, unknown.BillOfLadingNo);
        Assert.Equal(string.Empty, unknown.CarrierName);
        Assert.Equal(string.Empty, unknown.ForwarderName);
        Assert.Equal(string.Empty, unknown.CustomsBrokerName);
        Assert.Null(unknown.PlannedDepartureAt);
        Assert.Null(unknown.PlannedArrivalAt);

        // 自由文本 / 其他取值一律拒绝
        var loadingList = SeedLoadingList(db, "ZJ-1");
        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => controller.Create(Dto(
            ContainerShipmentReferenceRules.SourceTypeLoadingList, loadingList.Id, "海运")));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => controller.Create(Dto(
            ContainerShipmentReferenceRules.SourceTypeLoadingList, loadingList.Id, "GP40")));
    }

    [Fact]
    public async Task 计划时间必须先后一致且有界_任一为空保持未知()
    {
        using var db = TestDbFactory.Create();
        var twoDays = new DateTime(2026, 10, 10);
        var controller = BuildController(db);

        // 计划开船晚于计划到港 → 拒绝（服务端不自动改写、不补全时间）
        var incoherent = SeedBooking(db, "DG-2");
        var ex = await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => controller.Create(Dto(
            ContainerShipmentReferenceRules.SourceTypeBooking, incoherent.Id, "FCL",
            plannedDeparture: twoDays, plannedArrival: twoDays.AddDays(-5))));
        Assert.Contains("不能晚于", ex.Message);

        // 越界时间（1999 / 2101）→ 拒绝
        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => controller.Create(Dto(
            ContainerShipmentReferenceRules.SourceTypeBooking, incoherent.Id, "FCL",
            plannedDeparture: new DateTime(1999, 12, 31))));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => controller.Create(Dto(
            ContainerShipmentReferenceRules.SourceTypeBooking, incoherent.Id, "FCL",
            plannedArrival: new DateTime(2101, 1, 1))));

        // 三次非法提交都不落库
        Assert.Equal(0, await db.ContainerShipmentReferences.CountAsync());

        // 同一时刻允许（只要求「不晚于」）
        var sameTime = await CreateAsync(controller, Dto(
            ContainerShipmentReferenceRules.SourceTypeBooking, incoherent.Id, "FCL",
            plannedDeparture: twoDays, plannedArrival: twoDays));
        Assert.Equal(twoDays, sameTime.PlannedDepartureAt);
        Assert.Equal(twoDays, sameTime.PlannedArrivalAt);

        // 只填一个时间保持「未知」，绝不补另一个
        var preLoading = SeedPreLoading(db, "YZ-2", "CONT-C");
        var onlyDeparture = await CreateAsync(controller, Dto(
            ContainerShipmentReferenceRules.SourceTypePreLoading, preLoading.Id, "LCL",
            plannedDeparture: twoDays));
        Assert.Equal(twoDays, onlyDeparture.PlannedDepartureAt);
        Assert.Null(onlyDeparture.PlannedArrivalAt);

        var loadingList = SeedLoadingList(db, "ZJ-2", "CONT-C", preLoading.Id);
        var onlyArrival = await CreateAsync(controller, Dto(
            ContainerShipmentReferenceRules.SourceTypeLoadingList, loadingList.Id, "LCL",
            plannedArrival: twoDays));
        Assert.Null(onlyArrival.PlannedDepartureAt);
        Assert.Equal(twoDays, onlyArrival.PlannedArrivalAt);
    }

    [Fact]
    public async Task 文本超长与源记录校验被拒绝且不落库()
    {
        using var db = TestDbFactory.Create();
        var booking = SeedBooking(db, "DG-3");
        var deletedBooking = SeedBooking(db, "DG-DEL", deleted: true);
        var deletedPreLoading = SeedPreLoading(
            db, "YZ-DEL", "CONT-D", null, DocumentStatus.Pending, deleted: true);
        var controller = BuildController(db);

        // 超长文本：一律拒绝，不静默截断
        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => controller.Create(Dto(
            ContainerShipmentReferenceRules.SourceTypeBooking, booking.Id, "FCL",
            billOfLadingNo: new string('B', 51))));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => controller.Create(Dto(
            ContainerShipmentReferenceRules.SourceTypeBooking, booking.Id, "FCL",
            shippingOrderNo: new string('S', 51))));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => controller.Create(Dto(
            ContainerShipmentReferenceRules.SourceTypeBooking, booking.Id, "FCL",
            carrier: new string('C', 201))));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => controller.Create(Dto(
            ContainerShipmentReferenceRules.SourceTypeBooking, booking.Id, "FCL",
            forwarder: new string('F', 201))));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => controller.Create(Dto(
            ContainerShipmentReferenceRules.SourceTypeBooking, booking.Id, "FCL",
            departurePort: new string('P', 101))));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => controller.Create(Dto(
            ContainerShipmentReferenceRules.SourceTypeBooking, booking.Id, "FCL",
            transitPort: new string('T', 101))));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => controller.Create(Dto(
            ContainerShipmentReferenceRules.SourceTypeBooking, booking.Id, "FCL",
            destinationPort: new string('D', 101))));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => controller.Create(Dto(
            ContainerShipmentReferenceRules.SourceTypeBooking, booking.Id, "FCL",
            trucker: new string('T', 201))));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => controller.Create(Dto(
            ContainerShipmentReferenceRules.SourceTypeBooking, booking.Id, "FCL",
            remark: new string('R', 501))));

        // 源记录类型 allowlist 与源记录 Id 必填
        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => controller.Create(Dto("orders", booking.Id)));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => controller.Create(Dto("", booking.Id)));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => controller.Create(Dto(
            ContainerShipmentReferenceRules.SourceTypeBooking, 0)));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => controller.Create(Dto("BOOKING", 0)));

        // 不存在 → 404；已软删除 → 400 规则冲突（历史证据仍可读，但不能新增引用）
        await AssertBusinessAsync(ErrorCodes.NotFound, () => controller.Create(Dto(
            ContainerShipmentReferenceRules.SourceTypeBooking, 999999)));
        await AssertBusinessAsync(ErrorCodes.NotFound, () => controller.Create(Dto(
            ContainerShipmentReferenceRules.SourceTypeLoadingList, 999999)));
        await AssertBusinessAsync(ErrorCodes.RuleConflict, () => controller.Create(Dto(
            ContainerShipmentReferenceRules.SourceTypeBooking, deletedBooking.Id)));
        await AssertBusinessAsync(ErrorCodes.RuleConflict, () => controller.Create(Dto(
            ContainerShipmentReferenceRules.SourceTypePreLoading, deletedPreLoading.Id)));

        // 全部失败都不落库
        Assert.Equal(0, await db.ContainerShipmentReferences.CountAsync());
    }

    [Fact]
    public async Task 同一源记录最多一条有效引用_作废后可重新登记且拒绝重复作废()
    {
        using var db = TestDbFactory.Create();
        var booking = SeedBooking(db, "DG-4");
        var controller = BuildController(db);

        var first = await CreateAsync(controller, Dto(
            ContainerShipmentReferenceRules.SourceTypeBooking, booking.Id, "FCL", "SO-1"));

        var duplicate = await AssertBusinessAsync(ErrorCodes.Duplicate, () => controller.Create(Dto(
            ContainerShipmentReferenceRules.SourceTypeBooking, booking.Id, "LCL", "SO-2")));
        Assert.Contains("已有有效出运引用", duplicate.Message);
        Assert.Equal(1, await db.ContainerShipmentReferences.CountAsync());

        // 作废原因必填且长度有界
        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => controller.Void(
            first.Id, new ContainerShipmentReferenceVoidRequest()));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => controller.Void(
            first.Id, new ContainerShipmentReferenceVoidRequest { Reason = new string('R', 501) }));

        var voided = AssertOk<ContainerShipmentReferenceDto>(await controller.Void(
            first.Id, new ContainerShipmentReferenceVoidRequest { Reason = "出运方式录错" }));
        Assert.Equal(ContainerShipmentReferenceRules.StatusVoided, voided.Status);
        Assert.Equal("已作废", voided.StatusText);
        Assert.False(voided.IsRecorded);
        Assert.True(voided.IsVoided);
        Assert.Equal("出运方式录错", voided.VoidReason);
        Assert.NotNull(voided.VoidedAt);
        Assert.Equal("FCL", voided.ShipmentMode);          // 原始值保留
        Assert.Equal("SO-1", voided.ShippingOrderNo);
        Assert.Equal(booking.Id, voided.SourceId);          // 源记录关联保留

        var duplicateVoid = await AssertBusinessAsync(ErrorCodes.RuleConflict, () => controller.Void(
            first.Id, new ContainerShipmentReferenceVoidRequest { Reason = "重复作废" }));
        Assert.Contains("已作废", duplicateVoid.Message);

        // 已作废行只读：不能修订
        await AssertBusinessAsync(ErrorCodes.RuleConflict, () => controller.Update(
            first.Id, new ContainerShipmentReferenceUpdateDto
            {
                SourceType = ContainerShipmentReferenceRules.SourceTypeBooking,
                SourceId = booking.Id,
                Reason = "作废后不可改",
                ShipmentMode = "LCL"
            }));

        // 作废后可重新登记（新旧并存可查）
        var second = await CreateAsync(controller, Dto(
            ContainerShipmentReferenceRules.SourceTypeBooking, booking.Id, "LCL", "SO-2"));
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(2, await db.ContainerShipmentReferences.CountAsync());

        var page = AssertOk<PagedResult<ContainerShipmentReferenceDto>>(await controller.GetPaged(
            new ContainerShipmentReferenceQuery
            {
                SourceType = ContainerShipmentReferenceRules.SourceTypeBooking,
                SourceId = booking.Id
            }));
        Assert.Equal(2, page.Total);
        Assert.Contains(page.Items, r => r.IsVoided && r.VoidReason == "出运方式录错");
        Assert.Contains(page.Items, r => r.IsRecorded && r.ShippingOrderNo == "SO-2");
    }

    [Fact]
    public async Task 修订保留修订前原值与必填原因且递增修订号()
    {
        using var db = TestDbFactory.Create();
        var preLoading = SeedPreLoading(db, "YZ-3", "CONT-E");
        var controller = BuildController(db);

        var created = await CreateAsync(controller, Dto(
            ContainerShipmentReferenceRules.SourceTypePreLoading, preLoading.Id, "LCL",
            "SO-OLD", "BL-OLD", "OLD-CARRIER", "OLD-FORWARDER", "SHANGHAI", "SINGAPORE", "HAMBURG",
            new DateTime(2026, 10, 1), new DateTime(2026, 10, 20), "OLD-TRUCKER", null, "旧备注"));

        // 修订原因必填 / 有界；校验失败时留痕与新值都不落库
        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => controller.Update(
            created.Id, new ContainerShipmentReferenceUpdateDto
            {
                SourceType = ContainerShipmentReferenceRules.SourceTypePreLoading,
                SourceId = preLoading.Id,
                Reason = "   ",
                ShipmentMode = "FCL"
            }));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => controller.Update(
            created.Id, new ContainerShipmentReferenceUpdateDto
            {
                SourceType = ContainerShipmentReferenceRules.SourceTypePreLoading,
                SourceId = preLoading.Id,
                Reason = new string('R', 201),
                ShipmentMode = "FCL"
            }));
        Assert.Equal(0, await db.ContainerShipmentReferenceRevisions.CountAsync());
        var stillOriginal = await db.ContainerShipmentReferences.AsNoTracking().FirstAsync(o => o.Id == created.Id);
        Assert.Equal("LCL", stillOriginal.ShipmentMode);
        Assert.Equal(1, stillOriginal.RevisionNo);

        // 合法修订：写回新值 + 递增修订号 + 留痕保存修订前原值
        var revised = AssertOk<ContainerShipmentReferenceDto>(await controller.Update(
            created.Id, new ContainerShipmentReferenceUpdateDto
            {
                SourceType = ContainerShipmentReferenceRules.SourceTypePreLoading,
                SourceId = preLoading.Id,
                Reason = "计划时间由货代更新",
                ShipmentMode = "FCL",
                ShippingOrderNo = "SO-NEW",
                BillOfLadingNo = "BL-NEW",
                CarrierName = "NEW-CARRIER",
                ForwarderName = "NEW-FORWARDER",
                DeparturePort = "NINGBO",
                TransitPort = "SINGAPORE",
                DestinationPort = "ROTTERDAM",
                PlannedDepartureAt = new DateTime(2026, 11, 1),
                PlannedArrivalAt = new DateTime(2026, 11, 25),
                TruckerName = "NEW-TRUCKER",
                Remark = "新备注"
            }));

        Assert.Equal("FCL", revised.ShipmentMode);
        Assert.Equal("SO-NEW", revised.ShippingOrderNo);
        Assert.Equal(2, revised.RevisionNo);
        Assert.Equal("计划时间由货代更新", revised.LastRevisionReason);
        Assert.NotNull(revised.LastRevisedAt);
        Assert.Equal(1, revised.RevisionCount);

        var revisions = AssertOk<List<ContainerShipmentReferenceRevisionDto>>(
            await controller.Revisions(created.Id, 50));
        var revision = Assert.Single(revisions);
        Assert.Equal(1, revision.RevisionNo);          // 被取代的那一版
        Assert.Equal("计划时间由货代更新", revision.Reason);
        Assert.Equal("LCL", revision.ShipmentMode);    // 修订前的原值
        Assert.Equal("SO-OLD", revision.ShippingOrderNo);
        Assert.Equal("BL-OLD", revision.BillOfLadingNo);
        Assert.Equal("OLD-CARRIER", revision.CarrierName);
        Assert.Equal("OLD-FORWARDER", revision.ForwarderName);
        Assert.Equal("SHANGHAI", revision.DeparturePort);
        Assert.Equal("SINGAPORE", revision.TransitPort);
        Assert.Equal("HAMBURG", revision.DestinationPort);
        Assert.Equal(new DateTime(2026, 10, 1), revision.PlannedDepartureAt);
        Assert.Equal(new DateTime(2026, 10, 20), revision.PlannedArrivalAt);
        Assert.Equal("OLD-TRUCKER", revision.TruckerName);
        Assert.Equal("旧备注", revision.Remark);
        Assert.Equal(preLoading.Id, revision.SourceId);

        // 第二次修订：留痕按修订号倒序返回，V2 保存第一次修订后的值
        var secondRevision = AssertOk<ContainerShipmentReferenceDto>(await controller.Update(
            created.Id, new ContainerShipmentReferenceUpdateDto
            {
                SourceType = ContainerShipmentReferenceRules.SourceTypePreLoading,
                SourceId = preLoading.Id,
                Reason = "船期再次顺延",
                ShipmentMode = "FCL",
                ShippingOrderNo = "SO-NEW",
                BillOfLadingNo = "BL-NEW",
                PlannedDepartureAt = new DateTime(2026, 12, 1),
                PlannedArrivalAt = new DateTime(2026, 12, 25)
            }));
        Assert.Equal(3, secondRevision.RevisionNo);
        Assert.Equal(2, secondRevision.RevisionCount);

        var history = AssertOk<List<ContainerShipmentReferenceRevisionDto>>(
            await controller.Revisions(created.Id, 50));
        Assert.Equal(2, history.Count);
        Assert.Equal(2, history[0].RevisionNo);            // 倒序：最新被取代的在前
        Assert.Equal("船期再次顺延", history[0].Reason);
        Assert.Equal("SO-NEW", history[0].ShippingOrderNo);
        Assert.Equal(new DateTime(2026, 11, 1), history[0].PlannedDepartureAt);
        Assert.Equal(1, history[1].RevisionNo);
        Assert.Equal("SO-OLD", history[1].ShippingOrderNo);

        // 详情包含当前值与有界留痕
        var detail = AssertOk<ContainerShipmentReferenceDetailDto>(await controller.GetById(created.Id));
        Assert.Equal(3, detail.Reference.RevisionNo);
        Assert.Equal(2, detail.RevisionCount);
        Assert.Equal(2, detail.Revisions.Count);
        Assert.False(detail.RevisionsTruncated);
        Assert.Contains("修订", detail.InfoText);
        Assert.Contains("不是承运人", detail.InfoText);
        Assert.Contains("不改写", detail.BoundaryText);
    }

    [Fact]
    public async Task 修订不允许改派源记录_源记录删除后历史可读并标注不可用()
    {
        using var db = TestDbFactory.Create();
        var bookingA = SeedBooking(db, "DG-A");
        var bookingB = SeedBooking(db, "DG-B");
        var preLoading = SeedPreLoading(db, "YZ-A", "CONT-F");
        var controller = BuildController(db);

        var row = await CreateAsync(controller, Dto(
            ContainerShipmentReferenceRules.SourceTypeBooking, bookingA.Id, "FCL", "SO-A"));

        // 改派到另一条源记录 / 另一种类型 → 一律拒绝（历史证据不被静默改派）
        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => controller.Update(
            row.Id, new ContainerShipmentReferenceUpdateDto
            {
                SourceType = ContainerShipmentReferenceRules.SourceTypeBooking,
                SourceId = bookingB.Id,
                Reason = "换柜",
                ShipmentMode = "FCL"
            }));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => controller.Update(
            row.Id, new ContainerShipmentReferenceUpdateDto
            {
                SourceType = ContainerShipmentReferenceRules.SourceTypePreLoading,
                SourceId = preLoading.Id,
                Reason = "换来源类型",
                ShipmentMode = "FCL"
            }));

        var stored = await db.ContainerShipmentReferences.AsNoTracking().FirstAsync(o => o.Id == row.Id);
        Assert.Equal(ContainerShipmentReferenceRules.SourceTypeBooking, stored.SourceType);
        Assert.Equal(bookingA.Id, stored.SourceId);
        Assert.Equal(1, stored.RevisionNo);

        // 只提交证据字段（不带源记录）→ 视为保持原样，允许修订
        var unchangedSource = AssertOk<ContainerShipmentReferenceDto>(await controller.Update(
            row.Id, new ContainerShipmentReferenceUpdateDto
            {
                Reason = "仅更新提单号",
                ShipmentMode = "FCL",
                BillOfLadingNo = "BL-UPDATED"
            }));
        Assert.Equal(bookingA.Id, unchangedSource.SourceId);
        Assert.Equal("BL-UPDATED", unchangedSource.BillOfLadingNo);
        Assert.Equal(2, unchangedSource.RevisionNo);

        // 源记录被软删除：历史证据照常可读，只是显式标注不可用（不做回填、不改写快照）
        bookingA.IsDeleted = true;
        db.SaveChanges();

        var detail = AssertOk<ContainerShipmentReferenceDetailDto>(await controller.GetById(row.Id));
        Assert.False(detail.Reference.SourceAvailable);
        Assert.Contains("已删除", detail.Reference.SourceAvailabilityText);
        Assert.Equal("DG-A", detail.Reference.SourceNo);          // 快照保留
        Assert.Equal("BL-UPDATED", detail.Reference.BillOfLadingNo);
        Assert.Equal(2, detail.Reference.RevisionNo);

        var page = AssertOk<PagedResult<ContainerShipmentReferenceDto>>(await controller.GetPaged(
            new ContainerShipmentReferenceQuery { SourceType = ContainerShipmentReferenceRules.SourceTypeBooking }));
        var listed = Assert.Single(page.Items);
        Assert.False(listed.SourceAvailable);
        Assert.Contains("已删除", listed.SourceAvailabilityText);

        // 已删除源记录不能再新增引用
        await AssertBusinessAsync(ErrorCodes.RuleConflict, () => controller.Create(Dto(
            ContainerShipmentReferenceRules.SourceTypeBooking, bookingA.Id, "LCL")));

        // 源记录候选不包含已删除记录（候选只列未删除的既有记录）
        var candidates = AssertOk<List<ContainerShipmentReferenceSourceCandidateDto>>(
            await controller.SourceCandidates(
                ContainerShipmentReferenceRules.SourceTypeBooking, null, 200));
        Assert.DoesNotContain(candidates, c => c.SourceId == bookingA.Id);
        Assert.Contains(candidates, c => c.SourceId == bookingB.Id);
    }

    [Fact]
    public async Task 报关行引用_新增必须可选用_未变更保留历史快照_停用后标注不可用()
    {
        using var db = TestDbFactory.Create();
        var booking = SeedBooking(db, "DG-BROKER");
        var broker = SeedBroker(db, "宁波报关行");
        var deletedBroker = SeedBroker(db, "已删除报关行", deleted: true);
        var stoppedBroker = SeedBroker(db, "已停用报关行", status: 0);
        var wrongTypeBroker = SeedBroker(db, "货代公司", infoType: "Forwarder");
        var controller = BuildController(db);

        // 不存在 / 已删除 / 已停用 / 类型不符 → 一律拒绝（自由文本报关行名不被采信：只提交 Id）
        await AssertBusinessAsync(ErrorCodes.NotFound, () => controller.Create(Dto(
            ContainerShipmentReferenceRules.SourceTypeBooking, booking.Id, "FCL", brokerId: 999999)));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => controller.Create(Dto(
            ContainerShipmentReferenceRules.SourceTypeBooking, booking.Id, "FCL", brokerId: deletedBroker.Id)));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => controller.Create(Dto(
            ContainerShipmentReferenceRules.SourceTypeBooking, booking.Id, "FCL", brokerId: stoppedBroker.Id)));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => controller.Create(Dto(
            ContainerShipmentReferenceRules.SourceTypeBooking, booking.Id, "FCL", brokerId: wrongTypeBroker.Id)));

        var created = await CreateAsync(controller, Dto(
            ContainerShipmentReferenceRules.SourceTypeBooking, booking.Id, "FCL", brokerId: broker.Id));
        Assert.Equal(broker.Id, created.CustomsBrokerId);
        Assert.Equal("宁波报关行", created.CustomsBrokerName);   // 名称快照由服务端按字典项写入
        Assert.True(created.CustomsBrokerAvailable);

        // 下拉选项只返回未删除、已启用、类型匹配的字典项
        var options = AssertOk<List<OtherInfoOptionDto>>(await controller.CustomsBrokerOptions());
        Assert.Contains(options, o => o.Id == broker.Id);
        Assert.DoesNotContain(options, o => o.Id == deletedBroker.Id
            || o.Id == stoppedBroker.Id || o.Id == wrongTypeBroker.Id);

        // 字典项改名后：引用未变更 → 快照刷新为字典项最新名称（与 ERP-040 同一口径）
        broker.InfoName = "宁波报关行（新）";
        db.SaveChanges();
        var afterRename = AssertOk<ContainerShipmentReferenceDto>(await controller.Update(
            created.Id, new ContainerShipmentReferenceUpdateDto
            {
                SourceType = ContainerShipmentReferenceRules.SourceTypeBooking,
                SourceId = booking.Id,
                Reason = "字典项改名后同步名称快照",
                ShipmentMode = "FCL",
                CustomsBrokerId = broker.Id
            }));
        Assert.Equal("宁波报关行（新）", afterRename.CustomsBrokerName);

        // 字典项停用后：引用未变更的修订保留库中名称快照并标注不可用（绝不接受客户端改写历史名称）
        broker.Status = 0;
        db.SaveChanges();
        var afterStop = AssertOk<ContainerShipmentReferenceDto>(await controller.Update(
            created.Id, new ContainerShipmentReferenceUpdateDto
            {
                SourceType = ContainerShipmentReferenceRules.SourceTypeBooking,
                SourceId = booking.Id,
                Reason = "字典项停用后仅更新备注",
                ShipmentMode = "FCL",
                CustomsBrokerId = broker.Id,
                Remark = "备注更新"
            }));
        Assert.Equal("宁波报关行（新）", afterStop.CustomsBrokerName);   // 历史名称快照保留
        Assert.False(afterStop.CustomsBrokerAvailable);
        Assert.Contains("已停用", afterStop.CustomsBrokerAvailabilityText);
        Assert.Equal("备注更新", afterStop.Remark);

        // 停用后不能再被新选中
        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => controller.Create(Dto(
            ContainerShipmentReferenceRules.SourceTypePreLoading,
            SeedPreLoading(db, "YZ-BROKER", "CONT-G").Id, "FCL", brokerId: broker.Id)));

        // 清空报关行（Id 传 null）→ 两字段一并清空，且不影响其他字段
        var cleared = AssertOk<ContainerShipmentReferenceDto>(await controller.Update(
            created.Id, new ContainerShipmentReferenceUpdateDto
            {
                SourceType = ContainerShipmentReferenceRules.SourceTypeBooking,
                SourceId = booking.Id,
                Reason = "报关行改为未指定",
                ShipmentMode = "FCL",
                Remark = "备注更新"
            }));
        Assert.Null(cleared.CustomsBrokerId);
        Assert.Equal(string.Empty, cleared.CustomsBrokerName);
        Assert.Equal("备注更新", cleared.Remark);
    }

    [Fact]
    public async Task 台账按类型状态方式与关键字过滤_分页有界且未知筛选取值拒绝()
    {
        using var db = TestDbFactory.Create();
        var booking = SeedBooking(db, "DG-KEY");
        var preLoading = SeedPreLoading(db, "YZ-KEY", "CONT-KEY");
        var loadingList = SeedLoadingList(db, "ZJ-KEY", "CONT-KEY", preLoading.Id);
        var controller = BuildController(db);

        await CreateAsync(controller, Dto(
            ContainerShipmentReferenceRules.SourceTypeBooking, booking.Id, "FCL", "SO-KEY", "BL-KEY",
            carrier: "COSCO"));
        await CreateAsync(controller, Dto(
            ContainerShipmentReferenceRules.SourceTypePreLoading, preLoading.Id, "LCL"));
        var toVoid = await CreateAsync(controller, Dto(
            ContainerShipmentReferenceRules.SourceTypeLoadingList, loadingList.Id));
        await controller.Void(toVoid.Id, new ContainerShipmentReferenceVoidRequest { Reason = "作废测试" });

        // 默认包含已作废历史
        var all = AssertOk<PagedResult<ContainerShipmentReferenceDto>>(
            await controller.GetPaged(new ContainerShipmentReferenceQuery()));
        Assert.Equal(3, all.Total);

        // 状态 / 类型 / 出运方式（大小写归一）/ 关键字
        var recorded = AssertOk<PagedResult<ContainerShipmentReferenceDto>>(await controller.GetPaged(
            new ContainerShipmentReferenceQuery { Status = ContainerShipmentReferenceRules.StatusRecorded }));
        Assert.Equal(2, recorded.Total);

        var voided = AssertOk<PagedResult<ContainerShipmentReferenceDto>>(await controller.GetPaged(
            new ContainerShipmentReferenceQuery { Status = ContainerShipmentReferenceRules.StatusVoided }));
        Assert.Equal(1, voided.Total);
        Assert.Equal("作废测试", voided.Items[0].VoidReason);

        var byType = AssertOk<PagedResult<ContainerShipmentReferenceDto>>(await controller.GetPaged(
            new ContainerShipmentReferenceQuery
            {
                SourceType = ContainerShipmentReferenceRules.SourceTypePreLoading,
                SourceId = preLoading.Id
            }));
        var single = Assert.Single(byType.Items);
        Assert.Equal("YZ-KEY", single.SourceNo);
        Assert.Equal("CONT-KEY", single.ContainerNo);

        var byMode = AssertOk<PagedResult<ContainerShipmentReferenceDto>>(await controller.GetPaged(
            new ContainerShipmentReferenceQuery { ShipmentMode = "fcl" }));
        Assert.Equal(1, byMode.Total);
        Assert.Equal("FCL", byMode.Items[0].ShipmentMode);

        var byKeyword = AssertOk<PagedResult<ContainerShipmentReferenceDto>>(await controller.GetPaged(
            new ContainerShipmentReferenceQuery { Keyword = "CONT-KEY" }));
        Assert.Equal(2, byKeyword.Total);

        var byBillNo = AssertOk<PagedResult<ContainerShipmentReferenceDto>>(await controller.GetPaged(
            new ContainerShipmentReferenceQuery { Keyword = "BL-KEY" }));
        Assert.Equal(1, byBillNo.Total);

        // 分页参数有界（超出上限按上限收敛；非法值回落默认值）
        var capped = new ContainerShipmentReferenceQuery { PageSize = 9999 };
        capped.Normalize();
        Assert.Equal(ContainerShipmentReferenceQuery.MaxPageSize, capped.PageSize);

        var fallback = new ContainerShipmentReferenceQuery { Page = 0, PageSize = -3 };
        fallback.Normalize();
        Assert.Equal(1, fallback.Page);
        Assert.Equal(ContainerShipmentReferenceQuery.DefaultPageSize, fallback.PageSize);

        var paged = AssertOk<PagedResult<ContainerShipmentReferenceDto>>(await controller.GetPaged(
            new ContainerShipmentReferenceQuery { PageSize = 1, Page = 2 }));
        Assert.Equal(3, paged.Total);
        Assert.Single(paged.Items);
        Assert.Equal(2, paged.Page);
        Assert.Equal(3, paged.TotalPages);

        // 未知筛选取值一律拒绝（不静默忽略筛选条件）
        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => controller.GetPaged(
            new ContainerShipmentReferenceQuery { Status = 9 }));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => controller.GetPaged(
            new ContainerShipmentReferenceQuery { SourceType = "shipments" }));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => controller.GetPaged(
            new ContainerShipmentReferenceQuery { ShipmentMode = "SEA" }));
        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => controller.GetPaged(
            new ContainerShipmentReferenceQuery { Keyword = new string('K', 101) }));

        // 详情 / 留痕：不存在的引用 → 404
        await AssertBusinessAsync(ErrorCodes.NotFound, () => controller.GetById(999999));
        await AssertBusinessAsync(ErrorCodes.NotFound, () => controller.Revisions(999999, 50));
    }

    [Fact]
    public async Task 源记录候选有界并标注已有有效引用_历史无引用记录无需回填()
    {
        using var db = TestDbFactory.Create();
        var historical = SeedBooking(db, "DG-OLD");   // 历史记录：没有任何出运引用
        var current = SeedBooking(db, "DG-NEW");
        var preLoading = SeedPreLoading(db, "YZ-CAND", "CONT-CAND", historical.Id);
        var controller = BuildController(db);

        // 候选：历史记录照常出现且可登记（不需要任何请求期 / 生产回填）
        var before = AssertOk<List<ContainerShipmentReferenceSourceCandidateDto>>(
            await controller.SourceCandidates(ContainerShipmentReferenceRules.SourceTypeBooking, null, 200));
        Assert.Equal(2, before.Count);
        Assert.All(before, c =>
        {
            Assert.False(c.AlreadyReferenced);
            Assert.Null(c.ExistingReferenceId);
            Assert.True(c.Eligible);
            Assert.Equal(ContainerShipmentReferenceRules.SourceTypeBooking, c.SourceType);
        });

        var historicalCandidate = Assert.Single(before, c => c.SourceId == historical.Id);
        Assert.Equal("DG-OLD", historicalCandidate.SourceNo);
        Assert.Equal("待提交", historicalCandidate.SourceStatusText);
        Assert.Equal(string.Empty, historicalCandidate.ContainerNo);   // 订柜信息无柜号 → 未知
        Assert.Contains("可登记", historicalCandidate.EligibilityText);

        // 台账为空：没有任何被预置或回填的引用行
        var empty = AssertOk<PagedResult<ContainerShipmentReferenceDto>>(
            await controller.GetPaged(new ContainerShipmentReferenceQuery()));
        Assert.Equal(0, empty.Total);
        Assert.Equal(0, await db.ContainerShipmentReferences.CountAsync());
        Assert.Equal(0, await db.ContainerShipmentReferenceRevisions.CountAsync());

        var created = await CreateAsync(controller, Dto(
            ContainerShipmentReferenceRules.SourceTypeBooking, current.Id, "FCL"));

        var after = AssertOk<List<ContainerShipmentReferenceSourceCandidateDto>>(
            await controller.SourceCandidates(ContainerShipmentReferenceRules.SourceTypeBooking, null, 200));
        var referenced = Assert.Single(after, c => c.SourceId == current.Id);
        Assert.True(referenced.AlreadyReferenced);
        Assert.Equal(created.Id, referenced.ExistingReferenceId);
        Assert.Contains("已有有效出运引用", referenced.EligibilityText);
        Assert.False(Assert.Single(after, c => c.SourceId == historical.Id).AlreadyReferenced);

        // 预装柜单候选带柜号；take 有界（超出上限按上限收敛）；关键字只做显式筛选
        var preLoadingCandidates = AssertOk<List<ContainerShipmentReferenceSourceCandidateDto>>(
            await controller.SourceCandidates(
                ContainerShipmentReferenceRules.SourceTypePreLoading, "YZ-CAND", 99999));
        var preLoadingCandidate = Assert.Single(preLoadingCandidates);
        Assert.Equal("CONT-CAND", preLoadingCandidate.ContainerNo);

        var byKeyword = AssertOk<List<ContainerShipmentReferenceSourceCandidateDto>>(
            await controller.SourceCandidates(ContainerShipmentReferenceRules.SourceTypeBooking, "DG-OLD", 200));
        Assert.Single(byKeyword);

        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => controller.SourceCandidates(
            "containers", null, 200));

        // 装柜链路记录不被候选读取改写
        var storedHistorical = await db.ContainerBookings.AsNoTracking().FirstAsync(o => o.Id == historical.Id);
        Assert.Equal("DG-OLD", storedHistorical.BookingNo);
        Assert.Equal(DocumentStatus.Pending, storedHistorical.Status);
        Assert.Equal(historical.UpdatedAt, storedHistorical.UpdatedAt);
    }

    [Fact]
    public async Task 登记修订作废均不改写相邻业务记录()
    {
        using var db = TestDbFactory.Create();
        var customer = new BaseCustomer
        {
            CustomerCode = "C-057",
            CustomerName = "出运测试客户",
            Status = 1,
            CreditStatus = "正常",
            CreditLimit = 50000m
        };
        var order = new SalesOrder
        {
            OrderNo = "SO-057",
            OrderDate = new DateTime(2026, 9, 1),
            CustomerId = 1,
            Currency = Currency.USD,
            TotalAmount = 1000m,
            Status = DocumentStatus.Approved
        };
        var receipt = new FinanceReceipt
        {
            ReceiptNo = "SK-057",
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
        db.SaveChanges();

        var booking = SeedBooking(db, "DG-ADJ");
        var controller = BuildController(db);

        var created = await CreateAsync(controller, Dto(
            ContainerShipmentReferenceRules.SourceTypeBooking, booking.Id, "FCL", "SO-057"));
        await controller.Update(created.Id, new ContainerShipmentReferenceUpdateDto
        {
            SourceType = ContainerShipmentReferenceRules.SourceTypeBooking,
            SourceId = booking.Id,
            Reason = "相邻记录非变更测试",
            ShipmentMode = "LCL"
        });
        await controller.Void(created.Id, new ContainerShipmentReferenceVoidRequest { Reason = "相邻记录非变更测试" });

        // 客户 / 销售订单 / 收款单 / 库存与成本：完全不变
        var storedCustomer = await db.BaseCustomers.AsNoTracking().FirstAsync(o => o.Id == customer.Id);
        Assert.Equal("C-057", storedCustomer.CustomerCode);
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

        // 装柜链路记录同样不变
        var storedBooking = await db.ContainerBookings.AsNoTracking().FirstAsync(o => o.Id == booking.Id);
        Assert.Equal("DG-ADJ", storedBooking.BookingNo);
        Assert.Equal(DocumentStatus.Pending, storedBooking.Status);
        Assert.Equal(booking.UpdatedAt, storedBooking.UpdatedAt);

        // 本模块只写自己的两张表（1 条引用 + 1 条修订留痕）
        Assert.Equal(1, await db.ContainerShipmentReferences.CountAsync());
        Assert.Equal(1, await db.ContainerShipmentReferenceRevisions.CountAsync());
    }

    // ==================== 7. 纯规则 ====================

    [Fact]
    public void 纯规则_源类型状态方式时间与文案()
    {
        // 源记录类型 allowlist 与归一（忽略大小写与首尾空白；自由文本一律拒绝）
        Assert.Equal(ContainerShipmentReferenceRules.SourceTypeBooking,
            ContainerShipmentReferenceRules.NormalizeSourceType(" BOOKING "));
        Assert.Equal(ContainerShipmentReferenceRules.SourceTypeLoadingList,
            ContainerShipmentReferenceRules.NormalizeSourceType("Loading-List"));
        Assert.True(ContainerShipmentReferenceRules.IsSupportedSourceType("PRE-LOADING"));
        Assert.False(ContainerShipmentReferenceRules.IsSupportedSourceType("orders"));
        Assert.Equal(ErrorCodes.InvalidParameter,
            Assert.Throws<BusinessException>(() => ContainerShipmentReferenceRules.NormalizeSourceType("  ")).Code);
        Assert.Equal("订柜信息", ContainerShipmentReferenceRules.SourceTypeText("booking"));
        Assert.Equal("预装柜单", ContainerShipmentReferenceRules.SourceTypeText("PRE-LOADING"));
        Assert.Equal("装柜清单", ContainerShipmentReferenceRules.SourceTypeText("loading-list"));
        Assert.Contains("未知", ContainerShipmentReferenceRules.SourceTypeText("customs"));

        // 状态机与状态过滤
        Assert.Equal("已登记", ContainerShipmentReferenceRules.StatusText(1));
        Assert.Equal("已作废", ContainerShipmentReferenceRules.StatusText(2));
        Assert.Throws<BusinessException>(() => ContainerShipmentReferenceRules.StatusText(3));
        Assert.Null(ContainerShipmentReferenceRules.NormalizeStatusFilter(null));
        Assert.Equal(ContainerShipmentReferenceRules.StatusVoided,
            ContainerShipmentReferenceRules.NormalizeStatusFilter(2));
        Assert.Throws<BusinessException>(() => ContainerShipmentReferenceRules.NormalizeStatusFilter(7));

        // 出运方式过滤（复用 ERP-040 出运方式域）
        Assert.Null(ContainerShipmentReferenceRules.NormalizeModeFilter(null));
        Assert.Null(ContainerShipmentReferenceRules.NormalizeModeFilter("  "));
        Assert.Equal("FCL", ContainerShipmentReferenceRules.NormalizeModeFilter(" fcl "));
        Assert.Throws<BusinessException>(() => ContainerShipmentReferenceRules.NormalizeModeFilter("SEA"));

        // 计划时间：有界 + 先后一致（任一为空保持未知）
        Assert.Null(ContainerShipmentReferenceRules.NormalizePlannedTimestamp(null, "计划开船时间（ETD）"));
        Assert.Equal(new DateTime(2026, 10, 1), ContainerShipmentReferenceRules.NormalizePlannedTimestamp(
            new DateTime(2026, 10, 1), "计划开船时间（ETD）"));
        Assert.Throws<BusinessException>(() => ContainerShipmentReferenceRules.NormalizePlannedTimestamp(
            new DateTime(1999, 12, 31), "计划开船时间（ETD）"));
        ContainerShipmentReferenceRules.EnsurePlannedTimestampsCoherent(new DateTime(2026, 10, 1), new DateTime(2026, 10, 1));
        ContainerShipmentReferenceRules.EnsurePlannedTimestampsCoherent(new DateTime(2026, 10, 1), null);
        Assert.Throws<BusinessException>(() => ContainerShipmentReferenceRules.EnsurePlannedTimestampsCoherent(
            new DateTime(2026, 10, 2), new DateTime(2026, 10, 1)));

        // 文本与原因
        Assert.Equal("备注", ContainerShipmentReferenceRules.NormalizeRemark("  备注  "));
        Assert.Throws<BusinessException>(() => ContainerShipmentReferenceRules.NormalizeRemark(new string('R', 501)));
        Assert.Throws<BusinessException>(() => ContainerShipmentReferenceRules.NormalizeVoidReason(" "));
        Assert.Throws<BusinessException>(() => ContainerShipmentReferenceRules.NormalizeRevisionReason(string.Empty));
        Assert.Equal("改单", ContainerShipmentReferenceRules.NormalizeRevisionReason(" 改单 "));
        Assert.Throws<BusinessException>(() => ContainerShipmentReferenceRules.NormalizeKeyword(new string('K', 101)));

        // 源记录资格 / 可用性与报关行文案（未知一律照实说明）
        Assert.True(ContainerShipmentReferenceRules.EvaluateSourceEligibility(true, false, "订柜信息").Eligible);
        Assert.False(ContainerShipmentReferenceRules.EvaluateSourceEligibility(false, false, "订柜信息").Eligible);
        Assert.False(ContainerShipmentReferenceRules.EvaluateSourceEligibility(true, true, "订柜信息").Eligible);
        Assert.Contains("已删除", ContainerShipmentReferenceRules.SourceAvailabilityText(false, "订柜信息"));
        Assert.Contains("未指定报关行", ContainerShipmentReferenceRules.CustomsBrokerAvailabilityText(true, " "));
        Assert.Contains("已停用", ContainerShipmentReferenceRules.CustomsBrokerAvailabilityText(false, "宁波报关行"));
        Assert.Equal("已审核", ContainerShipmentReferenceRules.DocumentStatusText((int)DocumentStatus.Approved));

        // 状态机与「源记录不允许改派」
        Assert.Throws<BusinessException>(() => ContainerShipmentReferenceRules.EnsureRecordedForChange(
            ContainerShipmentReferenceRules.StatusVoided, "订柜信息「DG-1」"));
        ContainerShipmentReferenceRules.EnsureRecordedForChange(
            ContainerShipmentReferenceRules.StatusRecorded, "订柜信息「DG-1」");
        ContainerShipmentReferenceRules.EnsureSourceUnchanged("booking", 5, null, 0);
        ContainerShipmentReferenceRules.EnsureSourceUnchanged("booking", 5, "booking", 5);
        Assert.Throws<BusinessException>(() => ContainerShipmentReferenceRules.EnsureSourceUnchanged(
            "booking", 5, "booking", 6));
        Assert.Throws<BusinessException>(() => ContainerShipmentReferenceRules.EnsureSourceUnchanged(
            "booking", 5, "pre-loading", 5));

        // 文案（接口 / 界面 / 文档同源）
        Assert.Contains("不是承运人", ContainerShipmentReferenceRules.EvidenceText);
        Assert.Contains("不改写", ContainerShipmentReferenceRules.BoundaryText);
        Assert.Contains("不会按", ContainerShipmentReferenceRules.SourceLinkText + ContainerShipmentReferenceRules.EvidenceText);
    }

    // ==================== 8. 审计结论与契约 ====================

    [Fact]
    public void ERP057审计_仓库只有一套出运引用模型_不改写装柜链路()
    {
        // 1) 装柜链路已有唯一的持久化引用关系（复用既有字段，不新建第二套引用结构）
        var container2 = File.ReadAllText(
            RepoFile("src", "ERP.Domain", "Entities", "Container2.cs"));
        Assert.Contains("public long? BookingId", container2);
        Assert.Contains("public long? PreLoadingId", container2);

        // 2) 订柜信息由 ERP-040 承载本套跟踪值的权威记录（不复制它的跟踪列）
        var container1 = File.ReadAllText(
            RepoFile("src", "ERP.Domain", "Entities", "Container1.cs"));
        Assert.Contains("ERP-040", container1);
        Assert.Contains("权威记录", container1);
        Assert.Contains("public string BillOfLadingNo", container1);

        // 3) 装柜三单上没有新增任何「出运引用」证据列
        foreach (var type in new[]
                 {
                     typeof(ContainerBooking), typeof(ContainerPreLoading), typeof(ContainerLoadingList)
                 })
        {
            var names = type.GetProperties().Select(p => p.Name).ToList();
            Assert.DoesNotContain("SourceType", names);
            Assert.DoesNotContain("SourceId", names);
            Assert.DoesNotContain("CarrierName", names);
            Assert.DoesNotContain("ForwarderName", names);
            Assert.DoesNotContain("PlannedDepartureAt", names);
            Assert.DoesNotContain("PlannedArrivalAt", names);
            Assert.DoesNotContain("ShipmentReferenceId", names);
            Assert.DoesNotContain("RevisionNo", names);
        }

        // 4) IErpDbContext 中只有一套「出运引用」模型（不存在第二套出运主数据 / 链接表）
        var shipmentSets = typeof(IErpDbContext).GetProperties()
            .Where(p => p.Name.Contains("Shipment", StringComparison.Ordinal))
            .ToList();
        Assert.Equal(2, shipmentSets.Count);
        Assert.Contains(shipmentSets, p => p.Name == "ContainerShipmentReferences");
        Assert.Contains(shipmentSets, p => p.Name == "ContainerShipmentReferenceRevisions");

        var referenceSets = typeof(IErpDbContext).GetProperties()
            .Where(p => p.Name.Contains("ShipmentReference", StringComparison.Ordinal)
                        && !p.Name.Contains("Revision", StringComparison.Ordinal))
            .ToList();
        Assert.Single(referenceSets);
        Assert.Equal(typeof(DbSet<ContainerShipmentReference>), referenceSets[0].PropertyType);

        // 5) 服务端只按 AsNoTracking 读取装柜三单，从不新增 / 更新 / 删除它们
        var service = File.ReadAllText(RepoFile(
            "src", "ERP.Application", "Services", "ContainerShipmentReferenceService.cs"));
        Assert.Contains("db.ContainerBookings.AsNoTracking()", service);
        Assert.Contains("db.ContainerPreLoadings.AsNoTracking()", service);
        Assert.Contains("db.ContainerLoadingLists.AsNoTracking()", service);
        Assert.DoesNotContain("db.ContainerBookings.Add", service);
        Assert.DoesNotContain("db.ContainerPreLoadings.Add", service);
        Assert.DoesNotContain("db.ContainerLoadingLists.Add", service);
        Assert.DoesNotContain("db.ContainerBookings.Update", service);
        Assert.DoesNotContain("db.ContainerPreLoadings.Update", service);
        Assert.DoesNotContain("db.ContainerLoadingLists.Update", service);
        Assert.DoesNotContain("db.ContainerBookings.Remove", service);
        Assert.DoesNotContain("db.ContainerLoadingLists.Remove", service);
    }

    [Fact]
    public void 模型配置契约_长度过滤唯一索引与刻意不建外键()
    {
        using var db = TestDbFactory.Create();

        var entityType = db.Model.FindEntityType(typeof(ContainerShipmentReference));
        Assert.NotNull(entityType);

        Assert.Equal(20, entityType!.FindProperty(nameof(ContainerShipmentReference.SourceType))!.GetMaxLength());
        Assert.Equal(50, entityType.FindProperty(nameof(ContainerShipmentReference.SourceNo))!.GetMaxLength());
        Assert.Equal(30, entityType.FindProperty(nameof(ContainerShipmentReference.SourceStatusText))!.GetMaxLength());
        Assert.Equal(10, entityType.FindProperty(nameof(ContainerShipmentReference.ShipmentMode))!.GetMaxLength());
        Assert.Equal(50, entityType.FindProperty(nameof(ContainerShipmentReference.BillOfLadingNo))!.GetMaxLength());
        Assert.Equal(50, entityType.FindProperty(nameof(ContainerShipmentReference.ShippingOrderNo))!.GetMaxLength());
        Assert.Equal(200, entityType.FindProperty(nameof(ContainerShipmentReference.CarrierName))!.GetMaxLength());
        Assert.Equal(200, entityType.FindProperty(nameof(ContainerShipmentReference.ForwarderName))!.GetMaxLength());
        Assert.Equal(100, entityType.FindProperty(nameof(ContainerShipmentReference.TransitPort))!.GetMaxLength());
        Assert.Equal(200, entityType.FindProperty(nameof(ContainerShipmentReference.TruckerName))!.GetMaxLength());
        Assert.Equal(100, entityType.FindProperty(nameof(ContainerShipmentReference.CustomsBrokerName))!.GetMaxLength());
        Assert.Equal(500, entityType.FindProperty(nameof(ContainerShipmentReference.Remark))!.GetMaxLength());
        Assert.Equal(200, entityType.FindProperty(nameof(ContainerShipmentReference.LastRevisionReason))!.GetMaxLength());
        Assert.Equal(500, entityType.FindProperty(nameof(ContainerShipmentReference.VoidReason))!.GetMaxLength());

        // 同一条源记录最多一条有效引用（过滤唯一索引与幂等建表脚本同名同过滤条件）
        var uniqueIndex = Assert.Single(entityType.GetIndexes(), i => i.IsUnique);
        Assert.Equal("UX_ContainerShipmentReferences_ActiveSource", uniqueIndex.GetDatabaseName());
        Assert.Equal("IsDeleted = 0 AND Status <> 2", uniqueIndex.GetFilter());
        Assert.Contains(
            entityType.GetIndexes().Select(i => i.GetDatabaseName()),
            name => string.Equals(name, "IX_ContainerShipmentReferences_Status_RecordedAt", StringComparison.Ordinal));

        var revisionType = db.Model.FindEntityType(typeof(ContainerShipmentReferenceRevision));
        Assert.NotNull(revisionType);
        Assert.Equal(200, revisionType!.FindProperty(nameof(ContainerShipmentReferenceRevision.Reason))!.GetMaxLength());
        Assert.Equal(10, revisionType.FindProperty(nameof(ContainerShipmentReferenceRevision.ShipmentMode))!.GetMaxLength());
        Assert.Equal(500, revisionType.FindProperty(nameof(ContainerShipmentReferenceRevision.Remark))!.GetMaxLength());
        var revisionUnique = Assert.Single(revisionType.GetIndexes(), i => i.IsUnique);
        Assert.Equal("UX_ContainerShipmentReferenceRevisions_Reference_Revision", revisionUnique.GetDatabaseName());

        // 刻意不建任何外键与导航属性（源记录 / 报关行字典项可能被软删除或停用，历史证据必须始终可读）
        Assert.Empty(entityType.GetForeignKeys());
        Assert.Empty(revisionType.GetForeignKeys());
        Assert.Empty(entityType.GetNavigations());
        Assert.Empty(revisionType.GetNavigations());

        // 读取侧标注一律 [NotMapped]（不落库）
        foreach (var name in new[]
                 {
                     nameof(ContainerShipmentReference.SourceAvailable),
                     nameof(ContainerShipmentReference.SourceAvailabilityText),
                     nameof(ContainerShipmentReference.CustomsBrokerAvailable),
                     nameof(ContainerShipmentReference.CustomsBrokerAvailabilityText),
                     nameof(ContainerShipmentReference.StatusText),
                     nameof(ContainerShipmentReference.ShipmentModeText),
                     nameof(ContainerShipmentReference.RevisionCount),
                     nameof(ContainerShipmentReference.BoundaryText)
                 })
            Assert.Null(entityType.FindProperty(name));
    }

    [Fact]
    public void Schema_upgrade_幂等建表建索引且不含任何回填或写语句()
    {
        var script = File.ReadAllText(
            RepoFile("src", "ERP.Infrastructure", "Data", "SchemaUpgrader.cs"));

        Assert.Contains("IF OBJECT_ID('db_owner.ContainerShipmentReferences') IS NULL", script);
        Assert.Contains("CREATE TABLE db_owner.ContainerShipmentReferences", script);
        Assert.Contains("IF OBJECT_ID('db_owner.ContainerShipmentReferenceRevisions') IS NULL", script);
        Assert.Contains("CREATE TABLE db_owner.ContainerShipmentReferenceRevisions", script);
        Assert.Contains("ShipmentMode NVARCHAR(10) NOT NULL DEFAULT N''", script);
        Assert.Contains("PlannedDepartureAt DATETIME2 NULL", script);
        Assert.Contains("Status INT NOT NULL DEFAULT 1", script);
        Assert.Contains("RevisionNo INT NOT NULL DEFAULT 1", script);
        Assert.Contains("CREATE UNIQUE INDEX UX_ContainerShipmentReferences_ActiveSource", script);
        Assert.Contains("WHERE IsDeleted = 0 AND Status <> 2;", script);
        Assert.Contains("CREATE INDEX IX_ContainerShipmentReferences_Source_Status", script);
        Assert.Contains("CREATE INDEX IX_ContainerShipmentReferences_Status_RecordedAt", script);
        Assert.Contains("CREATE INDEX IX_ContainerShipmentReferences_ShipmentMode", script);
        Assert.Contains(
            "CREATE UNIQUE INDEX UX_ContainerShipmentReferenceRevisions_Reference_Revision", script);

        // 不建外键；本模块段落不修改任何既有表结构（装柜三单只由 ERP-040 / ERP-041 等既有段落维护）
        Assert.DoesNotContain("FK_ContainerShipmentReference", script);

        // 本模块段落只建表 + 过滤索引：不做任何回填，也不写任何业务数据
        var start = script.IndexOf("// 38. 装柜出运引用登记", StringComparison.Ordinal);
        Assert.True(start > 0);
        var segment = script[start..];
        Assert.DoesNotContain("ALTER TABLE", segment);
        Assert.DoesNotContain("UPDATE db_owner", segment);
        Assert.DoesNotContain("INSERT INTO db_owner", segment);
        Assert.DoesNotContain("DELETE FROM db_owner", segment);
        Assert.DoesNotContain("ContainerBookings", segment);
        Assert.DoesNotContain("ContainerPreLoadings", segment);
        Assert.DoesNotContain("ContainerLoadingLists", segment);
    }

    [Fact]
    public void 前端与路由接线契约()
    {
        var index = File.ReadAllText(RepoFile("src", "ERP.Api", "wwwroot", "index.html"));
        Assert.Contains("/js/container-shipment-references.js", index);

        var modules = File.ReadAllText(RepoFile("src", "ERP.Api", "wwwroot", "js", "modules-doc2.js"));
        // 工具栏入口必须带括号（extraActions 直接注入 onclick 属性），行操作只写函数名（渲染时注入行 Id）
        Assert.Contains("onclick: 'openContainerShipmentReferences()'", modules);
        Assert.Contains("onclick: 'openContainerShipmentReferences'", modules);
        Assert.Contains("booking: {", modules);
        Assert.Contains("'pre-loading': {", modules);
        Assert.Contains("'loading-list': {", modules);

        var js = File.ReadAllText(
            RepoFile("src", "ERP.Api", "wwwroot", "js", "container-shipment-references.js"));
        Assert.Contains("async function openContainerShipmentReferences", js);
        Assert.Contains("const CSR_MODULE_SOURCE_TYPES", js);
        Assert.Contains("'pre-loading': 'pre-loading'", js);
        Assert.Contains("'loading-list': 'loading-list'", js);
        Assert.Contains("'/api/container/shipment-references'", js);
        Assert.Contains("'/api/container/shipment-references?'", js);
        Assert.Contains("/source-candidates?", js);
        Assert.Contains("/customs-broker-options", js);
        Assert.Contains("/revisions", js);
        Assert.Contains("/void", js);
        Assert.Contains("csrSaveForm", js);
        Assert.Contains("csrConfirmVoid", js);
        Assert.Contains("修订留痕", js);
        Assert.Contains("不推进装柜状态", js);
        Assert.Contains("系统不会按柜型", js);
        Assert.Contains("不允许改派", js);

        var controller = File.ReadAllText(
            RepoFile("src", "ERP.Api", "Controllers", "ContainerShipmentReferenceController.cs"));
        Assert.Contains("[Route(\"api/container/shipment-references\")]", controller);
        Assert.Contains("source-candidates", controller);
        Assert.Contains("customs-broker-options", controller);
        Assert.Contains("{id:long}/revisions", controller);
        Assert.Contains("{id:long}/void", controller);
    }
}
