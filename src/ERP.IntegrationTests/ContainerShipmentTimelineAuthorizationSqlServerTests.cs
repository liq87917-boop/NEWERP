using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using Xunit;

namespace ERP.IntegrationTests;

/// <summary>
/// ERP-435 装柜出运证据时间线（<c>api/container/shipment-timeline</c> 三个只读路由）的**真实 SQL Server**
/// 实时授权与权威客户范围集成测试（GUID 独占 <c>NEWERP_AUTOTEST</c> 目标）。
/// <list type="number">
/// <item>工作台：受限业务员只读本人客户的出运引用时间线，范围在 <c>Count</c> / 分页之前下推，柜号 / B/L / 计划时间 /
/// 里程碑事件都不会跨客户泄露；</item>
/// <item>按显式源记录：本人客户源记录可读，越范围 / 无权威归属源记录 fail closed，来源类型必须命中既有源模块菜单；</item>
/// <item>按显式出运引用：本人引用可读，越范围引用 fail closed，父引用持久化的源记录类型必须命中既有源模块菜单；</item>
/// <item>真实身份：撤销既有菜单立即收敛为拒绝、禁用账号按权限不足、已删除账号 / 缺失身份按未认证（不新增任何授权，
/// 无匿名 / 管理员回退）；被拒绝时库中不新增 / 不修改任何装柜三单 / 出运引用 / 里程碑行。</item>
/// </list>
/// <para>安全口径：目标必须为专用 localdb 实例 <c>NEWERP_AutoAcceptance</c>、库名前缀 <c>NEWERP_AUTOTEST</c> 且集成安全；
/// 每次运行只创建全新 GUID 后缀库，发现同名库已存在立即拒绝；绝不 drop / reset / 复用任何数据库，
/// 连接串只来自进程环境变量或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据。</para>
/// </summary>
public sealed class ContainerShipmentTimelineAuthorizationSqlServerTests
    : IClassFixture<ContainerShipmentTimelineAuthorizationSqlServerFixture>
{
    private readonly ContainerShipmentTimelineAuthorizationSqlServerFixture _fixture;

    public ContainerShipmentTimelineAuthorizationSqlServerTests(
        ContainerShipmentTimelineAuthorizationSqlServerFixture fixture)
        => _fixture = fixture;

    /// <summary>访问数据库前复核专用目标护栏（实例名 / 库名前缀 / 集成安全）。</summary>
    private void Guard()
    {
        var target = new SqlConnectionStringBuilder(_fixture.ConnectionString);
        Assert.Equal("(localdb)\\NEWERP_AutoAcceptance", target.DataSource, ignoreCase: true);
        Assert.StartsWith(ContainerShipmentTimelineAuthorizationSqlServerFixture.DatabasePrefix,
            target.InitialCatalog, StringComparison.OrdinalIgnoreCase);
        Assert.True(target.IntegratedSecurity);
    }

    // ==================== 1. 工作台：本人客户可读，范围先于计数与分页 ====================

    [Fact]
    public async Task Live_workspace_returns_only_own_customer_timeline_rows_and_counts_after_scope()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var (userId, employeeId) = await SeedRestrictedOperatorAsync(db, booking: true);
        var own = await SeedCustomerAsync(db, "时间线本人客户", employeeId);
        var foreign = await SeedCustomerAsync(db, "时间线他人客户");

        var keyword = Tag();
        var ownBooking = await SeedBookingAsync(db, $"DG-{keyword}-OWN", own);
        var foreignBooking = await SeedBookingAsync(db, $"DG-{keyword}-OTHER", foreign);
        var ownReference = await SeedReferenceAsync(db, ownBooking, $"DG-{keyword}-OWN", "CTN-435-OWN");
        var foreignReference = await SeedReferenceAsync(db, foreignBooking, $"DG-{keyword}-OTHER", "CTN-435-OTHER");
        await SeedMilestoneAsync(db, ownReference.Id, "CTN-435-OWN");
        await SeedMilestoneAsync(db, foreignReference.Id, "CTN-435-OTHER");

        var ctl = NewController(db, userId);

        var page = AssertOk<PagedResult<ContainerShipmentTimelineShipmentDto>>(
            await ctl.GetPaged(new ContainerShipmentTimelineQuery { PageSize = 50 }));

        var row = Assert.Single(page.Items);
        Assert.Equal(1, page.Total);                               // 计数发生在数据库侧范围过滤之后
        Assert.Equal(ownReference.Id, row.ReferenceId);
        Assert.Equal($"DG-{keyword}-OWN", row.SourceNo);

        // 直接按他人源记录 Id 过滤：范围内没有该行 → 空页、Total 0（不泄露任何柜号 / B/L / 计划 / 事件计数）
        var filtered = AssertOk<PagedResult<ContainerShipmentTimelineShipmentDto>>(
            await ctl.GetPaged(new ContainerShipmentTimelineQuery { SourceId = foreignBooking.Id }));
        Assert.Empty(filtered.Items);
        Assert.Equal(0, filtered.Total);
    }

    // ==================== 2. 按显式源记录：菜单 + 客户范围 + 未知类型 ====================

    [Fact]
    public async Task Live_source_route_enforces_identity_menu_scope_and_unknown_source_type()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var (userId, employeeId) = await SeedRestrictedOperatorAsync(db, booking: true);
        var own = await SeedCustomerAsync(db, "源记录本人客户", employeeId);
        var foreign = await SeedCustomerAsync(db, "源记录他人客户");

        var keyword = Tag();
        var ownBooking = await SeedBookingAsync(db, $"DG-{keyword}-SOWN", own);
        var foreignBooking = await SeedBookingAsync(db, $"DG-{keyword}-SOTHER", foreign);
        var ownerlessBooking = await SeedBookingAsync(db, $"DG-{keyword}-SNONE", 0L);
        await SeedReferenceAsync(db, ownBooking, $"DG-{keyword}-SOWN", "CTN-435-SOWN");
        await SeedReferenceAsync(db, foreignBooking, $"DG-{keyword}-SOTHER", "CTN-435-SOTHER");

        // 本人客户但来源类型为装柜清单：本账号未被授予 loading-list 菜单。
        var ownLoadingList = await SeedLoadingListAsync(db, $"ZQ-{keyword}-SMENU", own);

        // 已删除账号：按未认证拒绝（已删除 = 未认证，不降级）。
        var deletedUserId = await SeedDeletedUserAsync(db);
        var (disabledUserId, _) = await SeedRestrictedOperatorAsync(db, booking: true);
        await DisableUserAsync(db, disabledUserId);
        var (revokedUserId, _) = await SeedRestrictedOperatorAsync(db, booking: true);
        await RevokeMenusAsync(db, revokedUserId);

        var ctl = NewController(db, userId);

        // 本人客户 + 命中既有订柜信息菜单：可读，且计划 / 实际证据照实返回。
        var detail = AssertOk<ContainerShipmentTimelineDetailDto>(
            await ctl.GetForSource(ContainerShipmentReferenceRules.SourceTypeBooking, ownBooking.Id));
        Assert.True(detail.Shipment.Linked);
        Assert.Equal($"DG-{keyword}-SOWN", detail.Shipment.SourceNo);

        // 越范围 / 无权威归属 / 来源类型未授权：一律 fail closed，不返回任何证据。
        await AssertCode(ErrorCodes.Forbidden, () => ctl.GetForSource(
            ContainerShipmentReferenceRules.SourceTypeBooking, foreignBooking.Id));
        await AssertCode(ErrorCodes.Forbidden, () => ctl.GetForSource(
            ContainerShipmentReferenceRules.SourceTypeBooking, ownerlessBooking.Id));
        await AssertCode(ErrorCodes.Forbidden, () => ctl.GetForSource(
            ContainerShipmentReferenceRules.SourceTypeLoadingList, ownLoadingList.Id));

        // 未知源记录类型：按非法参数拒绝（不静默忽略），先于读取任何证据。
        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.GetForSource("supplier", ownBooking.Id));

        // 身份：已删除按未认证、禁用按权限不足、撤销菜单按权限不足、缺失身份按未认证。
        await AssertCode(ErrorCodes.Unauthorized, () => NewController(db, deletedUserId).GetForSource(
            ContainerShipmentReferenceRules.SourceTypeBooking, ownBooking.Id));
        await AssertCode(ErrorCodes.Forbidden, () => NewController(db, disabledUserId).GetForSource(
            ContainerShipmentReferenceRules.SourceTypeBooking, ownBooking.Id));
        await AssertCode(ErrorCodes.Forbidden, () => NewController(db, revokedUserId).GetForSource(
            ContainerShipmentReferenceRules.SourceTypeBooking, ownBooking.Id));
        await AssertCode(ErrorCodes.Unauthorized, () => NewController(db, null).GetForSource(
            ContainerShipmentReferenceRules.SourceTypeBooking, ownBooking.Id));
    }

    // ==================== 3. 按显式出运引用：菜单 + 客户范围 ====================

    [Fact]
    public async Task Live_reference_route_enforces_identity_menu_scope_and_missing_reference()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var (userId, employeeId) = await SeedRestrictedOperatorAsync(db, booking: true);
        var own = await SeedCustomerAsync(db, "引用本人客户", employeeId);
        var foreign = await SeedCustomerAsync(db, "引用他人客户");

        var keyword = Tag();
        var ownBooking = await SeedBookingAsync(db, $"DG-{keyword}-ROWN", own);
        var foreignBooking = await SeedBookingAsync(db, $"DG-{keyword}-ROTHER", foreign);
        var ownReference = await SeedReferenceAsync(db, ownBooking, $"DG-{keyword}-ROWN", "CTN-435-ROWN");
        var foreignReference = await SeedReferenceAsync(db, foreignBooking, $"DG-{keyword}-ROTHER", "CTN-435-ROTHER");
        await SeedMilestoneAsync(db, ownReference.Id, "CTN-435-ROWN");

        // 本人客户装柜清单引用：本账号未被授予 loading-list 菜单 → 按父引用持久化的源记录类型 fail closed。
        var ownLoadingList = await SeedLoadingListAsync(db, $"ZQ-{keyword}-RMENU", own);
        var ownLoadingListReference = await SeedReferenceAsync(db, ownLoadingList,
            ContainerShipmentReferenceRules.SourceTypeLoadingList, $"ZQ-{keyword}-RMENU", "CTN-435-RMENU");

        var (revokedUserId, _) = await SeedRestrictedOperatorAsync(db, booking: true);
        await RevokeMenusAsync(db, revokedUserId);

        var ctl = NewController(db, userId);

        // 本人引用：可读。
        var detail = AssertOk<ContainerShipmentTimelineDetailDto>(await ctl.GetForReference(ownReference.Id));
        Assert.True(detail.Shipment.Linked);
        Assert.Equal(ownReference.Id, detail.Shipment.ReferenceId);
        Assert.Equal(1, detail.ActiveEventCount);

        // 越范围引用 / 父引用源记录类型未授权：fail closed，不返回任何证据。
        await AssertCode(ErrorCodes.Forbidden, () => ctl.GetForReference(foreignReference.Id));
        await AssertCode(ErrorCodes.Forbidden, () => ctl.GetForReference(ownLoadingListReference.Id));

        // 不存在 / 已删除引用：受控未找到（不泄露任何证据）。
        var deletedReference = await SeedReferenceAsync(db, ownBooking,
            ContainerShipmentReferenceRules.SourceTypeBooking, $"DG-{keyword}-RDEL", "CTN-435-RDEL", deleted: true);
        await AssertCode(ErrorCodes.NotFound, () => ctl.GetForReference(deletedReference.Id));
        await AssertCode(ErrorCodes.NotFound, () => ctl.GetForReference(987_654_321L));

        // 撤销菜单 / 缺失身份：一律拒绝，无匿名 / 管理员回退。
        await AssertCode(ErrorCodes.Forbidden, () => NewController(db, revokedUserId).GetForReference(ownReference.Id));
        await AssertCode(ErrorCodes.Unauthorized, () => NewController(db, null).GetForReference(ownReference.Id));
    }

    // ==================== 4. 拒绝不返回任何行，也不改写任何记录 ====================

    [Fact]
    public async Task Live_denials_return_no_rows_and_mutate_nothing()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var (userId, employeeId) = await SeedRestrictedOperatorAsync(db, booking: true);
        var own = await SeedCustomerAsync(db, "拒绝本人客户", employeeId);
        var foreign = await SeedCustomerAsync(db, "拒绝他人客户");

        var keyword = Tag();
        var ownBooking = await SeedBookingAsync(db, $"DG-{keyword}-DNYOWN", own);
        var foreignBooking = await SeedBookingAsync(db, $"DG-{keyword}-DNYOTHER", foreign);
        var ownReference = await SeedReferenceAsync(db, ownBooking, $"DG-{keyword}-DNYOWN", "CTN-435-DNYOWN");
        var foreignReference = await SeedReferenceAsync(db, foreignBooking, $"DG-{keyword}-DNYOTHER", "CTN-435-DNYOTHER");
        await SeedMilestoneAsync(db, ownReference.Id, "CTN-435-DNYOWN");
        await SeedMilestoneAsync(db, foreignReference.Id, "CTN-435-DNYOTHER");
        var ownLoadingList = await SeedLoadingListAsync(db, $"ZQ-{keyword}-DNYMENU", own);

        var beforeBookings = await db.ContainerBookings.AsNoTracking().CountAsync();
        var beforePreLoadings = await db.ContainerPreLoadings.AsNoTracking().CountAsync();
        var beforeLoadingLists = await db.ContainerLoadingLists.AsNoTracking().CountAsync();
        var beforeReferences = await db.ContainerShipmentReferences.AsNoTracking().CountAsync();
        var beforeMilestones = await db.ContainerShipmentMilestones.AsNoTracking().CountAsync();

        var ctl = NewController(db, userId);

        await AssertCode(ErrorCodes.Forbidden, () => ctl.GetForSource(
            ContainerShipmentReferenceRules.SourceTypeBooking, foreignBooking.Id));
        await AssertCode(ErrorCodes.Forbidden, () => ctl.GetForSource(
            ContainerShipmentReferenceRules.SourceTypeLoadingList, ownLoadingList.Id));
        await AssertCode(ErrorCodes.Forbidden, () => ctl.GetForReference(foreignReference.Id));
        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.GetForSource("supplier", ownBooking.Id));
        await AssertCode(ErrorCodes.Unauthorized, () => NewController(db, null).GetForReference(ownReference.Id));

        await using var verify = _fixture.CreateDbContext();
        Assert.Equal(beforeBookings, await verify.ContainerBookings.AsNoTracking().CountAsync());
        Assert.Equal(beforePreLoadings, await verify.ContainerPreLoadings.AsNoTracking().CountAsync());
        Assert.Equal(beforeLoadingLists, await verify.ContainerLoadingLists.AsNoTracking().CountAsync());
        Assert.Equal(beforeReferences, await verify.ContainerShipmentReferences.AsNoTracking().CountAsync());
        Assert.Equal(beforeMilestones, await verify.ContainerShipmentMilestones.AsNoTracking().CountAsync());

        // 被拒绝的引用证据保持不变（只读路由绝不改写任何记录）。
        var stored = await verify.ContainerShipmentReferences.AsNoTracking()
            .SingleAsync(r => r.Id == foreignReference.Id);
        Assert.Equal(ContainerShipmentReferenceRules.StatusRecorded, stored.Status);
        Assert.False(stored.IsDeleted);
    }

    // ==================== 5. 预装柜单既有菜单：来源类型必须命中对应菜单 ====================

    [Fact]
    public async Task Live_preloading_source_and_reference_require_preloading_menu()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var (userId, employeeId) = await SeedRestrictedOperatorAsync(db, booking: true, preLoading: true);
        var own = await SeedCustomerAsync(db, "预装柜单本人客户", employeeId);

        var keyword = Tag();
        var booking = await SeedBookingAsync(db, $"DG-{keyword}-PL", own);
        var preLoading = await SeedPreLoadingAsync(db, $"YZ-{keyword}-PL", booking.Id);
        var preLoadingReference = await SeedReferenceAsync(db, preLoading,
            ContainerShipmentReferenceRules.SourceTypePreLoading, $"YZ-{keyword}-PL", "CTN-435-PL");
        await SeedMilestoneAsync(db, preLoadingReference.Id, "CTN-435-PL");

        var ctl = NewController(db, userId);

        // 既有预装柜单菜单命中：本人客户预装柜单可读（父引用持久化的源记录类型为 pre-loading）。
        var detail = AssertOk<ContainerShipmentTimelineDetailDto>(
            await ctl.GetForSource(ContainerShipmentReferenceRules.SourceTypePreLoading, preLoading.Id));
        Assert.Equal(ContainerShipmentReferenceRules.SourceTypePreLoading, detail.Shipment.SourceType);
        Assert.Equal(preLoadingReference.Id, detail.Shipment.ReferenceId);

        var byReference = AssertOk<ContainerShipmentTimelineDetailDto>(
            await ctl.GetForReference(preLoadingReference.Id));
        Assert.Equal(preLoadingReference.Id, byReference.Shipment.ReferenceId);

        // 同一账号未授予 loading-list 菜单：装柜清单来源类型 fail closed。
        var loadingList = await SeedLoadingListAsync(db, $"ZQ-{keyword}-PL", own);
        await AssertCode(ErrorCodes.Forbidden, () => ctl.GetForSource(
            ContainerShipmentReferenceRules.SourceTypeLoadingList, loadingList.Id));
    }

    // ==================== 控制器工厂 / 身份注入 ====================

    private static ContainerShipmentTimelineController NewController(ErpDbContext db, long? userId)
    {
        var ctl = new ContainerShipmentTimelineController(db);
        SetUser(ctl, userId);
        return ctl;
    }

    private static void SetUser(ControllerBase controller, long? userId)
    {
        var claims = userId.HasValue
            ? new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }
            : Array.Empty<Claim>();
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) }
        };
    }

    // ==================== 种子数据（SQL 自增主键，不显式指定 Id） ====================

    private static string Tag() => Guid.NewGuid().ToString("N")[..10];

    private static async Task<long> SeedCustomerAsync(ErpDbContext db, string name, long? empId = null)
    {
        var customer = new BaseCustomer
        {
            CustomerCode = $"CST-C-{Guid.NewGuid():N}"[..30], CustomerName = name, EmpId = empId, Status = 1
        };
        db.BaseCustomers.Add(customer);
        await db.SaveChangesAsync();
        return customer.Id;
    }

    private static async Task<ContainerBooking> SeedBookingAsync(
        ErpDbContext db, string bookingNo, long customerId)
    {
        var booking = new ContainerBooking
        {
            BookingNo = bookingNo.Length > 50 ? bookingNo[..50] : bookingNo,
            BookingDate = DateTime.Today,
            CustomerId = customerId,
            ContainerType = ContainerType.GP40,
            ShippingCompany = "COSCO",
            DeparturePort = "NINGBO",
            DestinationPort = "HAMBURG",
            ShipmentMode = "FCL",
            Status = DocumentStatus.Pending,
            Remark = "ERP-435_INT"
        };
        db.ContainerBookings.Add(booking);
        await db.SaveChangesAsync();
        return booking;
    }

    private static async Task<ContainerPreLoading> SeedPreLoadingAsync(
        ErpDbContext db, string no, long bookingId)
    {
        var preLoading = new ContainerPreLoading
        {
            PreLoadingNo = no.Length > 50 ? no[..50] : no,
            LoadingDate = DateTime.Today,
            BookingId = bookingId,
            ContainerNo = "CTN-435-PL",
            SealNo = "SEAL-435",
            TotalCartons = 100,
            Status = DocumentStatus.Pending,
            Remark = "ERP-435_INT"
        };
        db.ContainerPreLoadings.Add(preLoading);
        await db.SaveChangesAsync();
        return preLoading;
    }

    private static async Task<ContainerLoadingList> SeedLoadingListAsync(
        ErpDbContext db, string no, long customerId, long? preLoadingId = null)
    {
        var list = new ContainerLoadingList
        {
            LoadingListNo = no.Length > 50 ? no[..50] : no,
            PreLoadingId = preLoadingId,
            LoadingDate = DateTime.Today,
            ContainerNo = "CTN-435-LL",
            CustomerId = customerId,
            ShippingMark = "MARK-435",
            TotalCartons = 100,
            Status = DocumentStatus.Pending,
            Remark = "ERP-435_INT"
        };
        db.ContainerLoadingLists.Add(list);
        await db.SaveChangesAsync();
        return list;
    }

    // ==================== 断言脚手架 ====================

    private static async Task AssertCode(int expected, Func<Task<IActionResult>> action)
    {
        var ex = await Assert.ThrowsAsync<BusinessException>(async () => await action());
        Assert.Equal(expected, ex.Code);
    }

    private static T AssertOk<T>(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<ApiResponse<T>>(ok.Value);
        Assert.Equal(ErrorCodes.Success, resp.Code);
        Assert.NotNull(resp.Data);
        return resp.Data!;
    }

    // ==================== 出运引用 / 里程碑种子 ====================

    private static Task<ContainerShipmentReference> SeedReferenceAsync(
        ErpDbContext db, ContainerBooking booking, string sourceNo, string containerNo, bool deleted = false)
        => SeedReferenceCoreAsync(db, ContainerShipmentReferenceRules.SourceTypeBooking, booking.Id,
            sourceNo, containerNo, deleted);

    private static Task<ContainerShipmentReference> SeedReferenceAsync(
        ErpDbContext db, ContainerBooking booking, string sourceType, string sourceNo,
        string containerNo, bool deleted = false)
        => SeedReferenceCoreAsync(db, sourceType, booking.Id, sourceNo, containerNo, deleted);

    private static Task<ContainerShipmentReference> SeedReferenceAsync(
        ErpDbContext db, ContainerLoadingList loadingList, string sourceType, string sourceNo,
        string containerNo, bool deleted = false)
        => SeedReferenceCoreAsync(db, sourceType, loadingList.Id, sourceNo, containerNo, deleted);

    private static Task<ContainerShipmentReference> SeedReferenceAsync(
        ErpDbContext db, ContainerPreLoading preLoading, string sourceType, string sourceNo,
        string containerNo, bool deleted = false)
        => SeedReferenceCoreAsync(db, sourceType, preLoading.Id, sourceNo, containerNo, deleted);

    private static async Task<ContainerShipmentReference> SeedReferenceCoreAsync(
        ErpDbContext db, string sourceType, long sourceId, string sourceNo, string containerNo, bool deleted)
    {
        var reference = new ContainerShipmentReference
        {
            SourceType = sourceType,
            SourceId = sourceId,
            SourceNo = sourceNo,
            SourceDate = DateTime.Today,
            SourceStatus = (int)DocumentStatus.Pending,
            SourceStatusText = "待提交",
            ContainerNo = containerNo,
            ShipmentMode = "FCL",
            ShippingOrderNo = $"SO-{sourceNo}",
            BillOfLadingNo = $"BL-{sourceNo}",
            CarrierName = "COSCO",
            ForwarderName = "FORWARDER-435",
            DeparturePort = "NINGBO",
            TransitPort = "SINGAPORE",
            DestinationPort = "HAMBURG",
            PlannedDepartureAt = new DateTime(2026, 9, 10, 8, 0, 0),
            PlannedArrivalAt = new DateTime(2026, 9, 25, 8, 0, 0),
            TruckerName = "TRUCKER-435",
            Remark = "ERP-435_INT",
            Status = ContainerShipmentReferenceRules.StatusRecorded,
            VoidReason = string.Empty,
            RecordedAt = new DateTime(2026, 9, 2, 9, 0, 0),
            RevisionNo = 1,
            IsDeleted = deleted
        };
        db.ContainerShipmentReferences.Add(reference);
        await db.SaveChangesAsync();
        return reference;
    }

    private static async Task SeedMilestoneAsync(ErpDbContext db, long referenceId, string marker)
    {
        var milestone = new ContainerShipmentMilestone
        {
            ContainerShipmentReferenceId = referenceId,
            EventType = ContainerShipmentMilestoneRules.EventTypeActualDeparture,
            EventAt = new DateTime(2026, 9, 12, 10, 30, 0),
            SourceDescription = $"{marker} 船公司网站截图",
            Notes = "ERP-435_INT",
            RecordedBy = "集成测试",
            RecordedAt = new DateTime(2026, 9, 12, 11, 0, 0),
            Status = ContainerShipmentMilestoneRules.StatusRecorded,
            VoidReason = string.Empty,
            CreatedAt = new DateTime(2026, 9, 12, 11, 0, 0)
        };
        db.ContainerShipmentMilestones.Add(milestone);
        await db.SaveChangesAsync();
    }

    // ==================== 受限身份（既有角色 → 菜单 + ERP-097 业务员映射） ====================

    private static async Task<(long UserId, long EmployeeId)> SeedRestrictedOperatorAsync(
        ErpDbContext db, bool booking = false, bool preLoading = false, bool loadingList = false)
    {
        var code = $"cst-op-{Guid.NewGuid():N}";
        var employee = new BaseEmployee { EmployeeCode = code, EmployeeName = code, IsSalesman = true, Status = 1 };
        db.BaseEmployees.Add(employee);
        await db.SaveChangesAsync();

        var user = new SysUser
        {
            UserName = code, DisplayName = code, PasswordHash = "hash", PasswordSalt = "salt",
            Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        await db.SaveChangesAsync();

        var role = new SysRole
        {
            RoleName = "装柜出运时间线操作员", RoleCode = $"CstOp-{Guid.NewGuid():N}", IsSystem = false
        };
        db.SysRoles.Add(role);
        await db.SaveChangesAsync();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        await db.SaveChangesAsync();

        await GrantMenuIfAsync(db, role.Id, booking, ShipmentReferenceAuthorizationRules.BookingMenuCode);
        await GrantMenuIfAsync(db, role.Id, preLoading, ShipmentReferenceAuthorizationRules.PreLoadingMenuCode);
        await GrantMenuIfAsync(db, role.Id, loadingList, ShipmentReferenceAuthorizationRules.LoadingListMenuCode);

        return (user.Id, employee.Id);
    }

    private static async Task GrantMenuIfAsync(ErpDbContext db, long roleId, bool grant, string menuCode)
    {
        if (!grant) return;
        var menuId = await db.SysMenus.AsNoTracking()
            .Where(m => m.MenuCode == menuCode && !m.IsDeleted)
            .Select(m => m.Id)
            .FirstAsync();
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = roleId, MenuId = menuId });
        await db.SaveChangesAsync();
    }

    private static async Task RevokeMenusAsync(ErpDbContext db, long userId)
    {
        var roleIds = await db.SysUserRoles.AsNoTracking()
            .Where(ur => ur.UserId == userId && !ur.IsDeleted)
            .Select(ur => ur.RoleId)
            .ToListAsync();
        var grants = await db.SysRoleMenus
            .Where(rm => roleIds.Contains(rm.RoleId) && !rm.IsDeleted).ToListAsync();
        foreach (var grant in grants) grant.IsDeleted = true;
        await db.SaveChangesAsync();
    }

    private static async Task DisableUserAsync(ErpDbContext db, long userId)
    {
        var user = await db.SysUsers.SingleAsync(u => u.Id == userId);
        user.Status = UserStatus.Disabled;
        await db.SaveChangesAsync();
    }

    private static async Task<long> SeedDeletedUserAsync(ErpDbContext db)
    {
        var user = new SysUser
        {
            UserName = $"cst-deleted-{Guid.NewGuid():N}", DisplayName = "已删除账号",
            PasswordHash = "hash", PasswordSalt = "salt", Status = UserStatus.Enabled, IsDeleted = true
        };
        db.SysUsers.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }
}

/// <summary>
/// 专用 localdb 目标 Fixture（ERP-435）：把目标库初始化为完整 NEWERP 结构 + 种子数据，供真实 SQL Server 授权集成测试复用。
/// <para>安全口径：库名一律带本次运行的 GUID 后缀（绝不复用 / 绝不 drop 既有库）；任何数据库访问之前先校验
/// 实例名、库名前缀与集成安全，连接串只来自进程环境变量或专用 localdb 默认值。</para>
/// </summary>
public sealed class ContainerShipmentTimelineAuthorizationSqlServerFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";
    public const string DefaultDatabaseName = "NEWERP_AUTOTEST_TIMELINEAUTHORIZATION_20261009";

    public string ConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = BuildDefaultConnectionString();

        AssertDedicatedTarget(connectionString);
        ConnectionString = connectionString;

        Console.WriteLine($"[ERP-435] 目标库护栏放行（实例含 {InstanceMarker}，库名前缀 {DatabasePrefix}）。");

        await EnsureFreshDatabaseAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public ErpDbContext CreateDbContext() => new(BuildOptions());

    private DbContextOptions<ErpDbContext> BuildOptions()
        => new DbContextOptionsBuilder<ErpDbContext>().UseSqlServer(ConnectionString).Options;

    private static string BuildDefaultConnectionString()
        => $"Server=(localdb)\\{InstanceMarker};Initial Catalog={DefaultDatabaseName}_{Guid.NewGuid():N};"
           + "Integrated Security=true;TrustServerCertificate=true;";

    internal static void AssertDedicatedTarget(string connectionString)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        var server = builder.DataSource ?? string.Empty;
        var database = builder.InitialCatalog ?? string.Empty;

        Assert.Equal($"(localdb)\\{InstanceMarker}", server, ignoreCase: true);
        Assert.StartsWith(DatabasePrefix, database, StringComparison.OrdinalIgnoreCase);
        Assert.True(builder.IntegratedSecurity);
    }

    private async Task EnsureFreshDatabaseAsync()
    {
        var database = new SqlConnectionStringBuilder(ConnectionString).InitialCatalog;

        // 任何数据库访问之前再次护栏：绝不使用生产 / 非专用回退。
        AssertDedicatedTarget(ConnectionString);

        var master = new SqlConnectionStringBuilder(ConnectionString) { InitialCatalog = "master" };
        await using (var conn = new SqlConnection(master.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            // 绝不销毁已存在的 Fixture 库或其它调用方的数据库。
            cmd.CommandText = "SELECT DB_ID(@database)";
            cmd.Parameters.AddWithValue("@database", database);
            var existing = await cmd.ExecuteScalarAsync();
            if (existing is not null && existing != DBNull.Value)
                throw new InvalidOperationException(
                    "The isolated fixture database already exists; choose a fresh NEWERP_AUTOTEST database.");
        }

        await using (var db = CreateDbContext())
        {
            await db.Database.EnsureCreatedAsync();
            await SchemaUpgrader.EnsureUpgradedAsync(db);
            await SeedData.InitializeAsync(db);
            await SchemaUpgrader.EnsureUpgradedAsync(db);
        }

        Console.WriteLine("[ERP-435] 集成场景就绪：完整 NEWERP 结构 + 种子数据。");
    }
}

/// <summary>目标库护栏单元级校验：非专用目标必须在访问数据库之前被拒绝。</summary>
public sealed class ContainerShipmentTimelineAuthorizationTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;User Id=sa;Password=x")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(
            () => ContainerShipmentTimelineAuthorizationSqlServerFixture.AssertDedicatedTarget(connection));

    [Fact]
    public void Accepts_the_dedicated_localdb_target_with_integrated_security()
        => ContainerShipmentTimelineAuthorizationSqlServerFixture.AssertDedicatedTarget(
            "Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_X;Integrated Security=true");
}
