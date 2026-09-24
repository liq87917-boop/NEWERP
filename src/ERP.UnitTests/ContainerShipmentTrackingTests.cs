using System.Text.Json;
using System.Text.Json.Serialization;
using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// 装柜外贸与物流跟踪单元测试（ERP-040）。覆盖：
/// 出运方式取值域与可选日期（服务端不推断）、查验要求三态（未知 / 不需要 / 需要互不混淆）、
/// 报关行字典项引用与历史名称快照、预装柜单 / 装柜清单按**持久化引用**的只读回显
/// （未关联即未知，不按柜号等自由文本匹配）、「不触达费用 / 单证 / 库存等其他单据」的边界，
/// 以及前端接线、菜单路由与幂等建列口径。
/// 全部使用内存库（TestDbFactory），不连接 SQL Server、不执行任何部署 / SQL 脚本。
/// </summary>
public class ContainerShipmentTrackingTests
{
    // ==================== 新增：出运方式与可选跟踪字段 ====================

    [Fact]
    public async Task Create_FCL与完整跟踪字段_落库并可在列表与详情读回()
    {
        using var db = TestDbFactory.Create();
        var broker = SeedOtherInfo(db, "CustomsBroker", "CB001", "宁波报关行");
        var ctl = BuildBookingController(db);

        var result = await ctl.Create(new ContainerBooking
        {
            BookingDate = new DateTime(2026, 9, 25),
            CustomerId = 7,
            ContainerType = ContainerType.GP40,
            ShippingCompany = "COSCO",
            VoyageNo = "V001",
            DeparturePort = "NINGBO",
            DestinationPort = "ROTTERDAM",
            ShipmentMode = "fcl",                        // 小写：服务端统一为大写
            BillOfLadingNo = " BL-001 ",                 // 带空白：服务端去首尾空白
            ShippingOrderNo = "SO-001",
            TransitPort = "SINGAPORE",
            Etd = new DateTime(2026, 9, 28),
            Eta = new DateTime(2026, 10, 20),
            TruckerName = "义乌拖车行",
            CustomsBrokerId = broker.Id,
            CustomsBrokerName = "客户端随手填的报关行",    // 自由文本不得被采信
            InspectionRequired = true,
            InspectionDate = new DateTime(2026, 9, 29),
            CustomsReleaseDate = new DateTime(2026, 9, 30)
        });

        AssertOkEmpty(result);                             // 新增接口返回 Id + 单号，实体从库中读回
        var created = db.ContainerBookings.Single();
        Assert.Equal("FCL", created.ShipmentMode);
        Assert.Equal("BL-001", created.BillOfLadingNo);
        Assert.Equal(broker.Id, created.CustomsBrokerId);
        Assert.Equal("宁波报关行", created.CustomsBrokerName);
        Assert.True(created.CustomsBrokerAvailable);

        var stored = created;
        Assert.Equal("FCL", stored.ShipmentMode);
        Assert.Equal("ROTTERDAM", stored.DestinationPort);
        Assert.Equal("SINGAPORE", stored.TransitPort);
        Assert.Equal(new DateTime(2026, 9, 28), stored.Etd);
        Assert.Equal(new DateTime(2026, 10, 20), stored.Eta);
        Assert.Null(stored.Atd);                          // 未填写 = 未知，绝不推断
        Assert.Null(stored.Ata);
        Assert.Equal("义乌拖车行", stored.TruckerName);
        Assert.True(stored.InspectionRequired);
        Assert.Equal(new DateTime(2026, 9, 29), stored.InspectionDate);
        Assert.Equal(new DateTime(2026, 9, 30), stored.CustomsReleaseDate);

        // 详情与列表：只回显持久化值，并带报关行可用性标注
        var detail = AssertOk<ContainerBooking>(await ctl.GetById(stored.Id));
        Assert.Equal("FCL", detail.ShipmentMode);
        Assert.Equal("SO-001", detail.ShippingOrderNo);
        Assert.Equal("宁波报关行", detail.CustomsBrokerName);
        Assert.True(detail.CustomsBrokerAvailable);

        var page = AssertOk<PagedResult<ContainerBooking>>(
            await ctl.GetPaged(new PageQuery { Page = 1, PageSize = 50 }, null));
        var row = Assert.Single(page.Items);
        Assert.Equal("FCL", row.ShipmentMode);
        Assert.Equal("SINGAPORE", row.TransitPort);
        Assert.Equal("宁波报关行", row.CustomsBrokerName);
    }

    [Fact]
    public async Task Create_LCL拼箱_可选日期与查验全部留空_落库为未知且不回落为不需要()
    {
        using var db = TestDbFactory.Create();
        var ctl = BuildBookingController(db);

        await ctl.Create(new ContainerBooking
        {
            BookingDate = new DateTime(2026, 9, 25),
            CustomerId = 7,
            ShipmentMode = "lcl"
        });

        var stored = db.ContainerBookings.Single();
        Assert.Equal("LCL", stored.ShipmentMode);
        Assert.Equal(string.Empty, stored.BillOfLadingNo);
        Assert.Equal(string.Empty, stored.ShippingOrderNo);
        Assert.Equal(string.Empty, stored.TransitPort);
        Assert.Equal(string.Empty, stored.TruckerName);
        Assert.Equal(string.Empty, stored.CustomsBrokerName);
        Assert.Null(stored.CustomsBrokerId);
        Assert.Null(stored.Etd);
        Assert.Null(stored.Eta);
        Assert.Null(stored.Atd);
        Assert.Null(stored.Ata);
        Assert.Null(stored.InspectionRequired);          // 未知：绝不回落为 false（不需要查验）
        Assert.Null(stored.InspectionDate);
        Assert.Null(stored.CustomsReleaseDate);
    }

    [Fact]
    public async Task Create_出运方式为自由文本_拒绝且不落库()
    {
        using var db = TestDbFactory.Create();
        var ctl = BuildBookingController(db);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Create(new ContainerBooking
        {
            BookingDate = new DateTime(2026, 9, 25),
            CustomerId = 7,
            ShipmentMode = "海运整柜"
        }));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Empty(db.ContainerBookings);
        Assert.Empty(db.ContainerPreLoadings);           // 被拒绝的新增不产生任何其他单据
    }

    [Fact]
    public async Task Create_跟踪文本超长_拒绝且不落库()
    {
        using var db = TestDbFactory.Create();
        var ctl = BuildBookingController(db);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Create(new ContainerBooking
        {
            BookingDate = new DateTime(2026, 9, 25),
            CustomerId = 7,
            ShipmentMode = "FCL",
            BillOfLadingNo = new string('B', ContainerShipmentTrackingRules.BillOfLadingNoMaxLength + 1)
        }));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Contains("提单号", ex.Message);
        Assert.Empty(db.ContainerBookings);
    }

    // ==================== 查验要求三态 ====================

    [Fact]
    public async Task Create_明确不需要查验但填写查验日期_拒绝且不落库()
    {
        using var db = TestDbFactory.Create();
        var ctl = BuildBookingController(db);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Create(new ContainerBooking
        {
            BookingDate = new DateTime(2026, 9, 25),
            CustomerId = 7,
            InspectionRequired = false,
            InspectionDate = new DateTime(2026, 9, 26)
        }));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Contains("不需要查验", ex.Message);
        Assert.Empty(db.ContainerBookings);
    }

    [Fact]
    public async Task Create_需要查验_查验日期仍然可选留空()
    {
        using var db = TestDbFactory.Create();
        var ctl = BuildBookingController(db);

        await ctl.Create(new ContainerBooking
        {
            BookingDate = new DateTime(2026, 9, 25),
            CustomerId = 7,
            InspectionRequired = true                          // 需要查验但尚未查验：日期留空是允许的
        });

        var stored = db.ContainerBookings.Single();
        Assert.True(stored.InspectionRequired);
        Assert.Null(stored.InspectionDate);                    // 不用 0 值日期代替未知
    }

    [Fact]
    public async Task Create_不需要查验与未知并存_三态互不混淆()
    {
        using var db = TestDbFactory.Create();
        var ctl = BuildBookingController(db);

        await ctl.Create(new ContainerBooking
        {
            BookingDate = new DateTime(2026, 9, 25), CustomerId = 7, InspectionRequired = false
        });
        await ctl.Create(new ContainerBooking
        {
            BookingDate = new DateTime(2026, 9, 25), CustomerId = 8, InspectionRequired = null
        });

        var rows = db.ContainerBookings.OrderBy(b => b.Id).ToList();
        Assert.False(rows[0].InspectionRequired);              // 不需要查验（false，显式落库）
        Assert.Null(rows[1].InspectionRequired);               // 未知（null，绝不回落为 false）
        Assert.Equal("不需要查验", ContainerShipmentTrackingRules.InspectionRequiredText(rows[0].InspectionRequired));
        Assert.Equal("未知", ContainerShipmentTrackingRules.InspectionRequiredText(rows[1].InspectionRequired));
        Assert.Equal("需要查验", ContainerShipmentTrackingRules.InspectionRequiredText(true));
    }

    // ==================== 报关行引用：新增校验 ====================

    [Fact]
    public async Task Create_报关行Id不存在_抛NotFound且不落库()
    {
        using var db = TestDbFactory.Create();
        var ctl = BuildBookingController(db);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Create(new ContainerBooking
        {
            BookingDate = new DateTime(2026, 9, 25), CustomerId = 7, CustomsBrokerId = 999999L
        }));

        Assert.Equal(ErrorCodes.NotFound, ex.Code);
        Assert.Empty(db.ContainerBookings);
    }

    [Fact]
    public async Task Create_报关行已被软删除_拒绝且不落库()
    {
        using var db = TestDbFactory.Create();
        var broker = SeedOtherInfo(db, "CustomsBroker", "CB001", "已删除报关行", deleted: true);
        var ctl = BuildBookingController(db);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Create(new ContainerBooking
        {
            BookingDate = new DateTime(2026, 9, 25), CustomerId = 7, CustomsBrokerId = broker.Id
        }));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Contains("已被删除", ex.Message);
        Assert.Empty(db.ContainerBookings);
    }

    [Fact]
    public async Task Create_报关行已停用_拒绝且不落库()
    {
        using var db = TestDbFactory.Create();
        var broker = SeedOtherInfo(db, "CustomsBroker", "CB001", "已停用报关行", status: 0);
        var ctl = BuildBookingController(db);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Create(new ContainerBooking
        {
            BookingDate = new DateTime(2026, 9, 25), CustomerId = 7, CustomsBrokerId = broker.Id
        }));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Contains("已停用", ex.Message);
        Assert.Empty(db.ContainerBookings);
    }

    [Fact]
    public async Task Create_字典项类型不是报关行_拒绝且不落库()
    {
        using var db = TestDbFactory.Create();
        var port = SeedOtherInfo(db, "Port", "PT001", "宁波港");
        var ctl = BuildBookingController(db);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Create(new ContainerBooking
        {
            BookingDate = new DateTime(2026, 9, 25), CustomerId = 7, CustomsBrokerId = port.Id
        }));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Contains("不是报关行", ex.Message);
        Assert.Empty(db.ContainerBookings);
    }

    [Fact]
    public async Task Create_只提交自由文本报关行名_不构成权威引用()
    {
        using var db = TestDbFactory.Create();
        var ctl = BuildBookingController(db);

        var result = await ctl.Create(new ContainerBooking
        {
            BookingDate = new DateTime(2026, 9, 25),
            CustomerId = 7,
            CustomsBrokerId = null,
            CustomsBrokerName = "只有名字、没有引用 Id 的报关行"
        });

        AssertOkEmpty(result);
        var created = db.ContainerBookings.Single();
        Assert.Null(created.CustomsBrokerId);
        Assert.Equal(string.Empty, created.CustomsBrokerName);
    }

    // ==================== 报关行引用：更新 / 清空 / 历史引用 ====================

    [Fact]
    public async Task Update_更换报关行_写入新名称快照并更新跟踪字段()
    {
        using var db = TestDbFactory.Create();
        var old = SeedOtherInfo(db, "CustomsBroker", "CB001", "原报关行");
        var fresh = SeedOtherInfo(db, "CustomsBroker", "CB002", "新报关行");
        var booking = SeedBooking(db, "DG202609250001", old.Id, "原报关行");
        var ctl = BuildBookingController(db);

        var result = await ctl.Update(booking.Id, new ContainerBooking
        {
            BookingDate = booking.BookingDate,
            CustomerId = 7,
            ShipmentMode = "FCL",
            BillOfLadingNo = "BL-002",
            CustomsBrokerId = fresh.Id,
            CustomsBrokerName = "企图覆盖的名称"
        });

        AssertOkEmpty(result);
        var stored = db.ContainerBookings.Single();
        Assert.Equal(fresh.Id, stored.CustomsBrokerId);
        Assert.Equal("新报关行", stored.CustomsBrokerName);
        Assert.True(stored.CustomsBrokerAvailable);
        Assert.Equal("FCL", stored.ShipmentMode);
        Assert.Equal("BL-002", stored.BillOfLadingNo);
    }

    [Fact]
    public async Task Update_更换为已停用报关行_拒绝且库中原引用不变()
    {
        using var db = TestDbFactory.Create();
        var active = SeedOtherInfo(db, "CustomsBroker", "CB001", "在用报关行");
        var inactive = SeedOtherInfo(db, "CustomsBroker", "CB002", "停用报关行", status: 0);
        var booking = SeedBooking(db, "DG202609250002", active.Id, "在用报关行");
        var ctl = BuildBookingController(db);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Update(booking.Id, new ContainerBooking
        {
            BookingDate = booking.BookingDate, CustomerId = 7, CustomsBrokerId = inactive.Id
        }));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Contains("已停用", ex.Message);

        var stored = db.ContainerBookings.Single();
        Assert.Equal(active.Id, stored.CustomsBrokerId);
        Assert.Equal("在用报关行", stored.CustomsBrokerName);
    }

    [Fact]
    public async Task Update_报关行Id传0_等同清空且不影响其他单据()
    {
        using var db = TestDbFactory.Create();
        var broker = SeedOtherInfo(db, "CustomsBroker", "CB001", "报关行A");
        var booking = SeedBooking(db, "DG202609250003", broker.Id, "报关行A");
        var ctl = BuildBookingController(db);

        AssertOkEmpty(await ctl.Update(booking.Id, new ContainerBooking
        {
            BookingDate = booking.BookingDate, CustomerId = 7, CustomsBrokerId = 0
        }));

        var stored = db.ContainerBookings.Single();
        Assert.Null(stored.CustomsBrokerId);
        Assert.Equal(string.Empty, stored.CustomsBrokerName);
        Assert.True(stored.CustomsBrokerAvailable);

        // 边界：跟踪字段只写订柜信息自身，不建 / 不改装柜、费用、单证与库存记录
        Assert.Empty(db.ContainerPreLoadings);
        Assert.Empty(db.ContainerLoadingLists);
        Assert.Empty(db.FinanceExpenses);
        Assert.Empty(db.TradeDocuments);
        Assert.Empty(db.StockMovements);
    }

    [Fact]
    public async Task Update_引用未变更但字典项已停用_保留历史快照并标注不可用()
    {
        using var db = TestDbFactory.Create();
        var broker = SeedOtherInfo(db, "CustomsBroker", "CB001", "历史报关行");
        var booking = SeedBooking(db, "DG202609250004", broker.Id, "历史报关行");

        // 字典项被停用：历史订柜记录不许被动清空、改写，也不许因此读不出来
        broker.Status = 0;
        db.SaveChanges();

        var ctl = BuildBookingController(db);
        AssertOkEmpty(await ctl.Update(booking.Id, new ContainerBooking
        {
            BookingDate = booking.BookingDate,
            CustomerId = 7,
            CustomsBrokerId = broker.Id,       // 引用未变更
            CustomsBrokerName = "客户端改写的历史名称"
        }));

        var stored = db.ContainerBookings.Single();
        Assert.Equal(broker.Id, stored.CustomsBrokerId);
        Assert.Equal("历史报关行", stored.CustomsBrokerName);   // 快照原样保留
        Assert.False(stored.CustomsBrokerAvailable);           // 显式标注不可用

        // 列表 / 详情读取同样保留快照并标注不可用
        var detail = AssertOk<ContainerBooking>(await ctl.GetById(booking.Id));
        Assert.Equal("历史报关行", detail.CustomsBrokerName);
        Assert.False(detail.CustomsBrokerAvailable);
    }

    // ==================== 报关行下拉选项（只读） ====================

    [Fact]
    public async Task 报关行选项_只返回启用未删除且类型匹配的字典项()
    {
        using var db = TestDbFactory.Create();
        var first = SeedOtherInfo(db, "CustomsBroker", "CB001", "上海报关行", sortOrder: 1);
        var second = SeedOtherInfo(db, "CustomsBroker", "CB002", "宁波报关行", sortOrder: 2);
        SeedOtherInfo(db, "CustomsBroker", "CB003", "已停用报关行", status: 0);
        SeedOtherInfo(db, "CustomsBroker", "CB004", "已删除报关行", deleted: true);
        SeedOtherInfo(db, "Port", "PT001", "宁波港");
        var caseVariant = SeedOtherInfo(db, "customsbroker", "CB005", "大小写差异报关行", sortOrder: 3);
        var ctl = BuildBookingController(db);

        var options = AssertOk<List<OtherInfoOptionDto>>(await ctl.GetCustomsBrokerOptions());

        Assert.Equal(3, options.Count);
        Assert.All(options, o => Assert.True(o.Selectable));
        Assert.Equal(
            new[] { "上海报关行", "宁波报关行", "大小写差异报关行" },
            options.Select(o => o.InfoName).ToArray());
        Assert.Equal(first.Id, options[0].Id);
        Assert.Equal(second.Id, options[1].Id);
        Assert.Equal(caseVariant.Id, options[2].Id);
    }

    // ==================== 只读回显：预装柜单 ====================

    [Fact]
    public async Task 预装柜单_有持久化订柜引用_只读回显权威跟踪值()
    {
        using var db = TestDbFactory.Create();
        var broker = SeedOtherInfo(db, "CustomsBroker", "CB001", "宁波报关行");
        var booking = SeedTrackingBooking(db, "DG001", broker.Id, "宁波报关行");
        var preLoading = SeedPreLoading(db, "YZ001", booking.Id, "CTN-1001");
        var before = SnapshotOf(booking);

        var tracking = AssertOk<ContainerShipmentTrackingDto>(
            await BuildPreLoadingController(db).GetShipmentTracking(preLoading.Id));

        Assert.True(tracking.Linked);
        Assert.Equal(booking.Id, tracking.BookingId);
        Assert.Equal("DG001", tracking.BookingNo);
        Assert.Equal("FCL", tracking.ShipmentMode);
        Assert.Equal("整箱 FCL", tracking.ShipmentModeText);
        Assert.Equal("BL-001", tracking.BillOfLadingNo);
        Assert.Equal("SO-001", tracking.ShippingOrderNo);
        Assert.Equal("ROTTERDAM", tracking.DestinationPort);
        Assert.Equal("SINGAPORE", tracking.TransitPort);
        Assert.Equal(new DateTime(2026, 9, 28), tracking.Etd);
        Assert.Null(tracking.Atd);
        Assert.Equal("义乌拖车行", tracking.TruckerName);
        Assert.Equal("宁波报关行", tracking.CustomsBrokerName);
        Assert.True(tracking.CustomsBrokerAvailable);
        Assert.True(tracking.InspectionRequired);
        Assert.Equal("需要查验", tracking.InspectionRequiredText);
        Assert.Equal(new DateTime(2026, 9, 29), tracking.InspectionDate);
        Assert.Equal(string.Empty, tracking.NotLinkedReason);

        // 只读：预装柜单与订柜信息都不被改写，也不产生其他单据
        Assert.Equal("CTN-1001", db.ContainerPreLoadings.Single().ContainerNo);
        Assert.Equal(before, SnapshotOf(db.ContainerBookings.Single()));
        Assert.Empty(db.ContainerLoadingLists);
        Assert.Empty(db.FinanceExpenses);
        Assert.Empty(db.TradeDocuments);
        Assert.Empty(db.StockMovements);
    }

    [Fact]
    public async Task 预装柜单_未关联_跟踪字段一律未知且不按柜号等文本匹配()
    {
        using var db = TestDbFactory.Create();
        // 存在一张「文本看起来相关」的订柜记录（单号 = 预装柜单的柜号），但没有持久化引用 → 不得被匹配
        SeedTrackingBooking(db, "CTN-1001", null, string.Empty);
        var preLoading = SeedPreLoading(db, "YZ002", bookingId: null, containerNo: "CTN-1001");

        var tracking = AssertOk<ContainerShipmentTrackingDto>(
            await BuildPreLoadingController(db).GetShipmentTracking(preLoading.Id));

        Assert.False(tracking.Linked);
        Assert.Null(tracking.BookingId);
        Assert.Equal(string.Empty, tracking.BookingNo);
        Assert.Equal(string.Empty, tracking.BillOfLadingNo);   // 不取「同柜号」订柜记录上的提单号
        Assert.Equal(string.Empty, tracking.DestinationPort);
        Assert.Equal("未知", tracking.ShipmentModeText);
        Assert.Equal("未知", tracking.InspectionRequiredText);
        Assert.Null(tracking.Etd);
        Assert.Null(tracking.CustomsBrokerId);
        Assert.Contains("未关联", tracking.NotLinkedReason);

        // 未关联的只读查询不新建 / 不改写任何订柜记录
        Assert.Equal("CTN-1001", db.ContainerBookings.Single().BookingNo);
    }

    // ==================== 只读回显：装柜清单（引用链） ====================

    [Fact]
    public async Task 装柜清单_按持久化引用链只读回显且不改写任何单据()
    {
        using var db = TestDbFactory.Create();
        var booking = SeedTrackingBooking(db, "DG002", null, string.Empty);
        var preLoading = SeedPreLoading(db, "YZ003", booking.Id, "CTN-2002");
        var list = SeedLoadingList(db, "ZQ001", preLoading.Id, "CTN-2002");
        var before = SnapshotOf(booking);

        var tracking = AssertOk<ContainerShipmentTrackingDto>(
            await BuildLoadingListController(db).GetShipmentTracking(list.Id));

        Assert.True(tracking.Linked);
        Assert.Equal(booking.Id, tracking.BookingId);
        Assert.Equal("DG002", tracking.BookingNo);
        Assert.Equal("BL-001", tracking.BillOfLadingNo);
        Assert.Equal(new DateTime(2026, 9, 28), tracking.Etd);
        Assert.Equal(before, SnapshotOf(db.ContainerBookings.Single()));
        Assert.Equal(preLoading.Id, db.ContainerLoadingLists.Single().PreLoadingId);
        Assert.Empty(db.TradeDocuments);
        Assert.Empty(db.FinanceExpenses);
        Assert.Empty(db.StockMovements);
    }

    [Fact]
    public async Task 引用链缺失或指向不可读记录_一律返回未关联()
    {
        using var db = TestDbFactory.Create();
        var deletedBooking = SeedTrackingBooking(db, "DG003", null, string.Empty);
        deletedBooking.IsDeleted = true;
        var orphanPreLoading = SeedPreLoading(db, "YZ004", bookingId: 999999L, containerNo: "CTN-3003");
        var danglingList = SeedLoadingList(db, "ZQ002", preLoadingId: 999999L, containerNo: "CTN-3003");
        var unlinkedList = SeedLoadingList(db, "ZQ003", preLoadingId: null, containerNo: "CTN-3003");
        var brokenChainList = SeedLoadingList(db, "ZQ004", preLoadingId: orphanPreLoading.Id, containerNo: "CTN-3003");
        var deletedBookingPreLoading = SeedPreLoading(db, "YZ005", deletedBooking.Id, "CTN-3003");
        db.SaveChanges();

        var preLoadingCtl = BuildPreLoadingController(db);
        var listCtl = BuildLoadingListController(db);

        // 预装柜单引用了不存在的订柜记录 → 未关联
        var byOrphanPreLoading = AssertOk<ContainerShipmentTrackingDto>(
            await preLoadingCtl.GetShipmentTracking(orphanPreLoading.Id));
        Assert.False(byOrphanPreLoading.Linked);
        Assert.Null(byOrphanPreLoading.BookingId);

        // 预装柜单引用了已软删除的订柜记录 → 未关联（已删除记录不是权威引用）
        var byDeletedBooking = AssertOk<ContainerShipmentTrackingDto>(
            await preLoadingCtl.GetShipmentTracking(deletedBookingPreLoading.Id));
        Assert.False(byDeletedBooking.Linked);

        // 装柜清单：引用不存在的预装柜单 / 未关联 / 链断裂 → 全部未关联
        foreach (var listId in new[] { danglingList.Id, unlinkedList.Id, brokenChainList.Id })
        {
            var tracking = AssertOk<ContainerShipmentTrackingDto>(await listCtl.GetShipmentTracking(listId));
            Assert.False(tracking.Linked);
            Assert.Null(tracking.BookingId);
            Assert.Equal(string.Empty, tracking.BookingNo);
            Assert.Equal("未知", tracking.ShipmentModeText);
            Assert.Equal("未知", tracking.InspectionRequiredText);
            Assert.Null(tracking.Etd);
        }

        // 未关联的只读查询不新建 / 不改写任何单据
        Assert.Equal(2, db.ContainerPreLoadings.Count());
        Assert.Equal(3, db.ContainerLoadingLists.Count());
    }

    // ==================== 纯规则 ====================

    [Fact]
    public void Rules_出运方式规范化_仅接受拼箱整箱或未指定()
    {
        Assert.Equal("FCL", ContainerShipmentTrackingRules.NormalizeShipmentMode("fcl"));
        Assert.Equal("LCL", ContainerShipmentTrackingRules.NormalizeShipmentMode(" lcl "));
        Assert.Equal(string.Empty, ContainerShipmentTrackingRules.NormalizeShipmentMode(null));
        Assert.Equal(string.Empty, ContainerShipmentTrackingRules.NormalizeShipmentMode("   "));

        var ex = Assert.Throws<BusinessException>(() => ContainerShipmentTrackingRules.NormalizeShipmentMode("整箱"));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Equal(2, ContainerShipmentTrackingRules.ShipmentModes.Count);
    }

    [Fact]
    public void Rules_出运方式文案_未指定显示未知()
    {
        Assert.Equal("拼箱 LCL", ContainerShipmentTrackingRules.ShipmentModeText("lcl"));
        Assert.Equal("整箱 FCL", ContainerShipmentTrackingRules.ShipmentModeText("FCL"));
        Assert.Equal("未知", ContainerShipmentTrackingRules.ShipmentModeText(null));
        Assert.Equal("未知", ContainerShipmentTrackingRules.ShipmentModeText("  "));
        Assert.Equal("未知", ContainerShipmentTrackingRules.UnknownText);
    }

    [Fact]
    public void Rules_资料类型比较忽略大小写与首尾空白()
    {
        Assert.True(ContainerShipmentTrackingRules.IsType(" customsbroker ",
            ContainerShipmentTrackingRules.CustomsBrokerInfoType));
        Assert.False(ContainerShipmentTrackingRules.IsType("Forwarder",
            ContainerShipmentTrackingRules.CustomsBrokerInfoType));
        Assert.False(ContainerShipmentTrackingRules.IsType(null,
            ContainerShipmentTrackingRules.CustomsBrokerInfoType));
    }

    [Fact]
    public void Rules_不可用标注与名称快照()
    {
        Assert.Equal("XX 报关行（已停用/不可用）", ContainerShipmentTrackingRules.MarkUnavailable("XX 报关行"));
        Assert.Equal(ContainerShipmentTrackingRules.UnavailableMark,
            ContainerShipmentTrackingRules.MarkUnavailable("   "));
        Assert.Equal(ContainerShipmentTrackingRules.UnavailableMark,
            ContainerShipmentTrackingRules.MarkUnavailable(null));

        Assert.Equal("宁波报关行", ContainerShipmentTrackingRules.SnapshotName(new BaseOtherInfo { InfoName = "  宁波报关行  " }));
        Assert.Equal(string.Empty, ContainerShipmentTrackingRules.SnapshotName(new BaseOtherInfo()));
    }

    [Fact]
    public void Rules_长度与查验一致性校验()
    {
        ContainerShipmentTrackingRules.EnsureLength("OK", 2, "提单号");     // 边界内不抛

        var tooLong = Assert.Throws<BusinessException>(() =>
            ContainerShipmentTrackingRules.EnsureLength("TOO-LONG", 3, "提单号"));
        Assert.Equal(ErrorCodes.InvalidParameter, tooLong.Code);
        Assert.Contains("提单号", tooLong.Message);

        var inconsistent = Assert.Throws<BusinessException>(() =>
            ContainerShipmentTrackingRules.EnsureInspectionConsistency(false, new DateTime(2026, 9, 26)));
        Assert.Equal(ErrorCodes.InvalidParameter, inconsistent.Code);

        // 未知 + 有日期、需要查验 + 无日期：都允许（不推断、不强制）
        ContainerShipmentTrackingRules.EnsureInspectionConsistency(null, new DateTime(2026, 9, 26));
        ContainerShipmentTrackingRules.EnsureInspectionConsistency(true, null);
        ContainerShipmentTrackingRules.EnsureInspectionConsistency(false, null);
    }

    // ==================== 前端接线与菜单路由 ====================

    [Fact]
    public void 前端接线_跟踪字段_报关行下拉与只读回显入口成对存在()
    {
        var modulesDoc2 = ReadScript("modules-doc2.js");
        var billEdit = ReadScript("bill-edit.js");
        var trackingJs = ReadScript("container-shipment-tracking.js");
        var crudJs = ReadScript("crud.js");
        var indexHtml = File.ReadAllText(Path.Combine(JsDirectory(), "..", "index.html"));

        // 表单字段：订柜信息（权威记录）必须能录入全部跟踪字段；关联列用于建立持久化引用
        foreach (var key in new[] { "shipmentMode", "billOfLadingNo", "shippingOrderNo", "transitPort", "etd", "eta",
            "atd", "ata", "truckerName", "customsBrokerId", "inspectionRequired", "inspectionDate",
            "customsReleaseDate", "bookingId", "preLoadingId" })
            Assert.Contains($"key: '{key}'", modulesDoc2);

        // 报关行下拉 / 历史回显：复用「其他资料」字典项口径（只读选项 + 名称快照 + 不可用标注）
        Assert.Contains("api: '/api/container/bookings/customs-broker-options'", billEdit);
        Assert.Contains("rowKey: 'customsBrokerName'", billEdit);
        Assert.Contains("availableKey: 'customsBrokerAvailable'", billEdit);

        // 出运方式与查验要求（三态）选项：与后端取值域同口径
        Assert.Contains("const SHIPMENT_MODE_OPTS", modulesDoc2);
        Assert.Contains("const INSPECTION_REQUIRED_OPTS", modulesDoc2);
        Assert.Contains("valueType: 'bool?'", modulesDoc2);
        Assert.Contains("f.valueType === 'bool?'", crudJs);

        // 行操作 → 全局函数名（必须成对存在，拼写漂移会让「更多」里的按钮点了没反应）
        Assert.Contains("onclick: 'showShipmentTracking'", modulesDoc2);
        Assert.Contains("function showShipmentTracking(", trackingJs);
        Assert.Contains("const SHIPMENT_TRACKING_SOURCES", trackingJs);
        Assert.Contains("'/api/container/pre-loadings'", trackingJs);
        Assert.Contains("'/api/container/loading-lists'", trackingJs);
        Assert.Contains("/shipment-tracking", trackingJs);
        // 未知口径：空值一律「未知」，绝不回落为空 / 0 / 今天
        Assert.Contains("const TRACKING_UNKNOWN = '未知'", trackingJs);
        Assert.Contains("function trackingDate(", trackingJs);
        Assert.Contains("function inspectionRequiredText(", trackingJs);

        // 前端脚本与后端接口路径成对存在
        var bookingCtl = ReadSource("ERP.Api/Controllers/ContainerControllers.cs");
        Assert.Contains("[HttpGet(\"customs-broker-options\")]", bookingCtl);
        Assert.Contains("ContainerShipmentTrackingService.ApplyAsync", bookingCtl);
        Assert.Contains("[HttpGet(\"{id:long}/shipment-tracking\")]",
            ReadSource("ERP.Api/Controllers/ContainerPreLoadingController.cs"));
        Assert.Contains("[HttpGet(\"{id:long}/shipment-tracking\")]",
            ReadSource("ERP.Api/Controllers/ContainerLoadingListController.cs"));

        // 脚本已注册进页面（否则函数不存在，浏览器验收会「点了没反应」）
        Assert.Contains("/js/container-shipment-tracking.js", indexHtml);
    }

    [Fact]
    public void 菜单路由_装柜三单改由EF页面承载_不再从菜单进入SP版单据页()
    {
        var billV2 = ReadScript("bill-v2.js");
        // 收货计划仍走 SP 版单据页（未受影响）
        Assert.Contains("'receiving-plan': 'receiving-plan'", billV2);
        // ERP-040：装柜三单改由 EF 主子表页面承载（SP 版无法保存新增跟踪列，也无法只读回显引用链）
        Assert.DoesNotContain("booking: 'booking'", billV2);
        Assert.DoesNotContain("'pre-loading': 'pre-loading'", billV2);
        Assert.DoesNotContain("'loading-list': 'loading-list'", billV2);

        // EF 模块必须仍然存在（否则菜单会落到「该功能开发中」）
        var modulesDoc2 = ReadScript("modules-doc2.js");
        Assert.Contains("api: '/api/container/bookings'", modulesDoc2);
        Assert.Contains("api: '/api/container/pre-loadings'", modulesDoc2);
        Assert.Contains("api: '/api/container/loading-lists'", modulesDoc2);
    }

    // ==================== 建列口径（幂等、不回填） ====================

    [Fact]
    public void SchemaUpgrader_订柜跟踪列幂等补齐且不回填历史数据()
    {
        var sql = ReadSource("ERP.Infrastructure/Data/SchemaUpgrader.cs");

        foreach (var column in new[] { "ShipmentMode", "BillOfLadingNo", "ShippingOrderNo", "TransitPort",
            "Etd", "Eta", "Atd", "Ata", "TruckerName", "CustomsBrokerId", "CustomsBrokerName",
            "InspectionRequired", "InspectionDate", "CustomsReleaseDate" })
        {
            Assert.Contains($"IF COL_LENGTH('db_owner.ContainerBooking', '{column}') IS NULL", sql);
            Assert.Contains($"ADD {column} ", sql);
        }

        // 文本列默认空串（未知）、日期与报关行 Id 可空、查验要求三态为 BIT NULL
        Assert.Contains("ADD ShipmentMode NVARCHAR(10) NOT NULL DEFAULT N'';", sql);
        Assert.Contains("ADD CustomsBrokerName NVARCHAR(100) NOT NULL DEFAULT N'';", sql);
        Assert.Contains("ADD Etd DATETIME2 NULL;", sql);
        Assert.Contains("ADD CustomsBrokerId BIGINT NULL;", sql);
        Assert.Contains("ADD InspectionRequired BIT NULL;", sql);

        // 不回填历史数据、不臆造任何日期，也不给预装柜单 / 装柜清单另加跟踪列
        Assert.DoesNotContain("UPDATE db_owner.ContainerBooking SET", sql);
        Assert.DoesNotContain("ALTER TABLE db_owner.ContainerPreLoading", sql);
        Assert.DoesNotContain("ALTER TABLE db_owner.ContainerLoadingList", sql);
    }

    // ==================== 只读投影的 JSON 口径 ====================

    [Fact]
    public void 跟踪投影_JSON字段名与未知口径符合前端约定()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());

        var booking = new ContainerBooking
        {
            Id = 3, BookingNo = "DG001", ShipmentMode = "LCL", BillOfLadingNo = "BL-001",
            CustomsBrokerId = 9, CustomsBrokerName = "宁波报关行",
            Etd = new DateTime(2026, 9, 28), InspectionRequired = false
        };
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(
            ContainerShipmentTrackingService.From(booking, customsBrokerAvailable: false), options));
        var root = doc.RootElement;

        Assert.True(root.GetProperty("linked").GetBoolean());
        Assert.Equal(3, root.GetProperty("bookingId").GetInt64());
        Assert.Equal("DG001", root.GetProperty("bookingNo").GetString());
        Assert.Equal("LCL", root.GetProperty("shipmentMode").GetString());
        Assert.Equal("拼箱 LCL", root.GetProperty("shipmentModeText").GetString());
        Assert.Equal("不需要查验", root.GetProperty("inspectionRequiredText").GetString());
        Assert.False(root.GetProperty("inspectionRequired").GetBoolean());
        Assert.False(root.GetProperty("customsBrokerAvailable").GetBoolean());
        Assert.Equal("2026-09-28", root.GetProperty("etd").GetString()!.Substring(0, 10));
        Assert.Equal(JsonValueKind.Null, root.GetProperty("atd").ValueKind);   // 未填写 = 未知（null）

        // 未关联投影：linked = false 且字段为空 / null（前端显示「未知」），不是 0 或「无」这类占位值
        using var notLinked = JsonDocument.Parse(JsonSerializer.Serialize(
            ContainerShipmentTrackingService.NotLinked(), options));
        var unlinked = notLinked.RootElement;
        Assert.False(unlinked.GetProperty("linked").GetBoolean());
        Assert.Equal(JsonValueKind.Null, unlinked.GetProperty("bookingId").ValueKind);
        Assert.Equal(string.Empty, unlinked.GetProperty("bookingNo").GetString());
        Assert.Equal(string.Empty, unlinked.GetProperty("billOfLadingNo").GetString());
        Assert.Equal(JsonValueKind.Null, unlinked.GetProperty("inspectionRequired").ValueKind);

        // 三态在 JSON 里同样互不混淆：未知 = null，不需要 = false
        using var unknownState = JsonDocument.Parse(JsonSerializer.Serialize(
            ContainerShipmentTrackingService.From(new ContainerBooking { Id = 4, BookingNo = "DG002" }, true),
            options));
        Assert.Equal(JsonValueKind.Null, unknownState.RootElement.GetProperty("inspectionRequired").ValueKind);
        Assert.Equal("未知", unknownState.RootElement.GetProperty("inspectionRequiredText").GetString());
    }

    // ==================== 工厂与种子数据 ====================

    /// <summary>订柜信息控制器（内存库 + 单据号服务）</summary>
    private static ContainerBookingController BuildBookingController(ErpDbContext db)
        => new(db, new DocumentNumberService(db));

    /// <summary>预装柜单控制器（内存库 + 单据号服务）</summary>
    private static ContainerPreLoadingController BuildPreLoadingController(ErpDbContext db)
        => new(db, new DocumentNumberService(db));

    /// <summary>装柜清单控制器（内存库 + 单据号服务）</summary>
    private static ContainerLoadingListController BuildLoadingListController(ErpDbContext db)
        => new(db, new DocumentNumberService(db));

    /// <summary>断言成功响应并取出数据（业务码必须为 0）</summary>
    private static T AssertOk<T>(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<ApiResponse<T>>(ok.Value);
        Assert.Equal(ErrorCodes.Success, resp.Code);
        Assert.NotNull(resp.Data);
        return resp.Data!;
    }

    /// <summary>断言成功响应（更新类接口的 Data 为 null，只校验业务码）</summary>
    private static void AssertOkEmpty(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<ApiResponse<object>>(ok.Value);
        Assert.Equal(ErrorCodes.Success, resp.Code);
    }

    /// <summary>造一条「其他资料」字典项</summary>
    private static BaseOtherInfo SeedOtherInfo(ErpDbContext db, string infoType, string infoCode, string infoName,
        int status = 1, bool deleted = false, int sortOrder = 0)
    {
        var entry = new BaseOtherInfo
        {
            InfoType = infoType,
            InfoCode = infoCode,
            InfoName = infoName,
            Status = status,
            IsDeleted = deleted,
            SortOrder = sortOrder
        };
        db.BaseOtherInfos.Add(entry);
        db.SaveChanges();
        return entry;
    }

    /// <summary>造一条只有基础字段的订柜记录（更新 / 清空 / 历史引用场景）</summary>
    private static ContainerBooking SeedBooking(ErpDbContext db, string bookingNo, long? brokerId, string brokerName)
    {
        var booking = new ContainerBooking
        {
            BookingNo = bookingNo,
            BookingDate = new DateTime(2026, 9, 20),
            CustomerId = 7,
            ContainerType = ContainerType.GP40,
            CustomsBrokerId = brokerId,
            CustomsBrokerName = brokerName
        };
        db.ContainerBookings.Add(booking);
        db.SaveChanges();
        return booking;
    }

    /// <summary>造一条带完整跟踪值的订柜记录（已维护跟踪信息的历史数据）</summary>
    private static ContainerBooking SeedTrackingBooking(ErpDbContext db, string bookingNo, long? brokerId, string brokerName)
    {
        var booking = SeedBooking(db, bookingNo, brokerId, brokerName);
        booking.ShippingCompany = "COSCO";
        booking.VoyageNo = "V001";
        booking.DeparturePort = "NINGBO";
        booking.DestinationPort = "ROTTERDAM";
        booking.ShipmentMode = "FCL";
        booking.BillOfLadingNo = "BL-001";
        booking.ShippingOrderNo = "SO-001";
        booking.TransitPort = "SINGAPORE";
        booking.Etd = new DateTime(2026, 9, 28);
        booking.Eta = new DateTime(2026, 10, 20);
        booking.TruckerName = "义乌拖车行";
        booking.InspectionRequired = true;
        booking.InspectionDate = new DateTime(2026, 9, 29);
        db.SaveChanges();
        return booking;
    }

    /// <summary>造一条预装柜单（bookingId 为 null = 未关联订柜信息）</summary>
    private static ContainerPreLoading SeedPreLoading(ErpDbContext db, string preLoadingNo, long? bookingId, string containerNo)
    {
        var entity = new ContainerPreLoading
        {
            PreLoadingNo = preLoadingNo,
            LoadingDate = new DateTime(2026, 9, 21),
            BookingId = bookingId,
            ContainerNo = containerNo,
            SealNo = "SEAL-1"
        };
        db.ContainerPreLoadings.Add(entity);
        db.SaveChanges();
        return entity;
    }

    /// <summary>造一条装柜清单（preLoadingId 为 null = 未关联预装柜单）</summary>
    private static ContainerLoadingList SeedLoadingList(ErpDbContext db, string loadingListNo, long? preLoadingId, string containerNo)
    {
        var entity = new ContainerLoadingList
        {
            LoadingListNo = loadingListNo,
            LoadingDate = new DateTime(2026, 9, 22),
            PreLoadingId = preLoadingId,
            ContainerNo = containerNo,
            CustomerId = 7
        };
        db.ContainerLoadingLists.Add(entity);
        db.SaveChanges();
        return entity;
    }

    /// <summary>订柜记录的可比较快照：用于验证只读查询不改写任何字段</summary>
    private static string SnapshotOf(ContainerBooking b) =>
        $"{b.Id}|{b.BookingNo}|{b.ShipmentMode}|{b.BillOfLadingNo}|{b.ShippingOrderNo}|{b.DeparturePort}|" +
        $"{b.DestinationPort}|{b.TransitPort}|{b.Etd}|{b.Eta}|{b.Atd}|{b.Ata}|{b.TruckerName}|" +
        $"{b.CustomsBrokerId}|{b.CustomsBrokerName}|{b.CustomsBrokerAvailable}|{b.InspectionRequired}|" +
        $"{b.InspectionDate}|{b.CustomsReleaseDate}|{b.IsDeleted}";

    /// <summary>仓库根目录（沿测试程序集输出目录上溯，与既有测试同一约定）</summary>
    private static string RepoRoot() =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    /// <summary>前端脚本目录</summary>
    private static string JsDirectory() => Path.Combine(RepoRoot(), "src", "ERP.Api", "wwwroot", "js");

    /// <summary>读取前端脚本</summary>
    private static string ReadScript(string fileName) => File.ReadAllText(Path.Combine(JsDirectory(), fileName));

    /// <summary>读取仓库 src 下的源码文件（斜杠分隔的相对路径，如 ERP.Api/Controllers/Xxx.cs）</summary>
    private static string ReadSource(string relativePath) =>
        File.ReadAllText(Path.Combine(RepoRoot(), "src", relativePath.Replace('/', Path.DirectorySeparatorChar)));
}
