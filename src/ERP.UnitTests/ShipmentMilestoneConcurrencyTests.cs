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
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// 装柜出运里程碑证据（ERP-058 / ERP-365）的实时授权与串行化一致性单元测试。覆盖：
/// <list type="number">
/// <item>两个**独立上下文**（同一内存库 = 两条独立连接）登记同一父记录 + 类型 + 时间，
/// 只产生一条有效证据，后到者被明确拒绝为重复；作废后可重新登记且旧证据完全不变；</item>
/// <item>父出运引用被作废 / 删除后拒绝新增证据，历史里程碑保留可读并显式标注不可用；</item>
/// <item>父出运引用的**源记录**被取消 / 删除 / 不存在时拒绝新增证据（与 ERP-057 同一资格口径），
/// 历史里程碑照常可读；</item>
/// <item>受限账号的既有源模块菜单 + 权威客户数据范围作用于台账 / 详情 / 父记录候选 / 登记 / 作废
/// （fail closed，不泄露范围外证据）；撤销菜单、未映射业务员、匿名 / 禁用 / 已删除账号全部 fail closed；</item>
/// <item>失败的登记不落任何证据、也不改写上游源记录与相邻业务 / 库存数据；</item>
/// <item>控制器「父引用行锁 + 源记录行锁 + 里程碑行锁 + 可序列化事务」与「每个路由先授权」的源码契约。</item>
/// </list>
/// <para>全部使用内存库（TestDbFactory 口径），不连接 SQL Server、不执行任何 SQL / 部署脚本；
/// 真正的「两条独立连接竞争同一行」由 <c>ShipmentMilestoneConcurrencySqlServerTests</c> 在专用 localdb
/// （<c>(localdb)\NEWERP_AutoAcceptance</c>，全新 GUID 库）上覆盖。</para>
/// </summary>
public class ShipmentMilestoneConcurrencyTests
{
    // ==================== 0. 测试脚手架 ====================

    private static readonly DateTime DefaultEventAt = new(2026, 9, 12, 10, 30, 0);

    /// <summary>两个「独立连接」：同一内存库名 + 各自独立的 DbContext（等价两条独立连接）。</summary>
    private static ErpDbContext OpenShared(string databaseName)
        => new(new DbContextOptionsBuilder<ErpDbContext>()
            .UseInMemoryDatabase(databaseName)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    /// <summary>既有特权账号控制器（系统内置角色，不新增任何菜单授权）。</summary>
    private static ContainerShipmentMilestoneController PrivilegedController(ErpDbContext db)
    {
        var controller = new ContainerShipmentMilestoneController(db);
        TestAuth.SetUser(controller, TestAuth.SeedPrivilegedUser(db));
        return controller;
    }

    private static ContainerShipmentMilestoneController ControllerFor(ErpDbContext db, long? userId)
    {
        var controller = new ContainerShipmentMilestoneController(db);
        TestAuth.SetUser(controller, userId);
        return controller;
    }

    private static T AssertOk<T>(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<ApiResponse<T>>(ok.Value);
        Assert.Equal(ErrorCodes.Success, resp.Code);
        Assert.NotNull(resp.Data);
        return resp.Data!;
    }

    private static async Task<BusinessException> AssertBusinessAsync(int expectedCode, Func<Task> action)
    {
        var ex = await Assert.ThrowsAsync<BusinessException>(action);
        Assert.Equal(expectedCode, ex.Code);
        return ex;
    }

    private static string RepoFile(params string[] segments)
        => Path.GetFullPath(Path.Combine(
            new[] { AppContext.BaseDirectory, "..", "..", "..", "..", ".." }.Concat(segments).ToArray()));

    private static ContainerShipmentMilestoneSaveDto Dto(
        long referenceId, string eventType = ContainerShipmentMilestoneRules.EventTypeActualDeparture,
        DateTime? eventAt = null, string sourceDescription = "船公司网站截图", string notes = "",
        string recordedBy = "张三")
        => new()
        {
            ContainerShipmentReferenceId = referenceId,
            EventType = eventType,
            EventAt = eventAt ?? DefaultEventAt,
            SourceDescription = sourceDescription,
            Notes = notes,
            RecordedBy = recordedBy
        };

    private static ContainerBooking SeedBooking(
        ErpDbContext db, string bookingNo, long customerId, DocumentStatus status = DocumentStatus.Pending,
        bool deleted = false)
    {
        var booking = new ContainerBooking
        {
            BookingNo = bookingNo,
            BookingDate = new DateTime(2026, 9, 1),
            CustomerId = customerId,
            ContainerType = ContainerType.GP40,
            Status = status,
            IsDeleted = deleted
        };
        db.ContainerBookings.Add(booking);
        db.SaveChanges();
        return booking;
    }

    private static ContainerPreLoading SeedPreLoading(ErpDbContext db, string no, long? bookingId)
    {
        var preLoading = new ContainerPreLoading
        {
            PreLoadingNo = no,
            LoadingDate = new DateTime(2026, 9, 3),
            BookingId = bookingId,
            ContainerNo = "CONT-" + no,
            Status = DocumentStatus.Pending
        };
        db.ContainerPreLoadings.Add(preLoading);
        db.SaveChanges();
        return preLoading;
    }

    private static ContainerShipmentReference SeedReference(
        ErpDbContext db, string sourceType, long sourceId, string sourceNo,
        int status = ContainerShipmentReferenceRules.StatusRecorded, bool deleted = false)
    {
        var reference = new ContainerShipmentReference
        {
            SourceType = sourceType,
            SourceId = sourceId,
            SourceNo = sourceNo,
            SourceDate = new DateTime(2026, 9, 1),
            SourceStatus = (int)DocumentStatus.Pending,
            SourceStatusText = ContainerShipmentReferenceRules.DocumentStatusText((int)DocumentStatus.Pending),
            ContainerNo = "CONT-" + sourceNo,
            Status = status,
            RecordedAt = new DateTime(2026, 9, 2),
            RevisionNo = 1,
            IsDeleted = deleted,
            CreatedAt = DateTime.Now
        };
        db.ContainerShipmentReferences.Add(reference);
        db.SaveChanges();
        return reference;
    }

    private static void SeedCustomer(ErpDbContext db, long id, string name, long? empId = null)
    {
        db.BaseCustomers.Add(new BaseCustomer
        {
            Id = id, CustomerCode = $"CSM-C-{id}", CustomerName = name, EmpId = empId, Status = 1
        });
        db.SaveChanges();
    }

    /// <summary>受限业务员账号：员工映射（客户数据范围）+ 显式授予的既有源模块菜单（不新增任何权限）。</summary>
    private static (long UserId, long EmployeeId) SeedRestrictedOperator(
        ErpDbContext db, params string[] menuCodes)
    {
        var code = $"csm-op-{Guid.NewGuid():N}";
        var employee = new BaseEmployee
        {
            EmployeeCode = code, EmployeeName = code, IsSalesman = true, Status = 1
        };
        db.BaseEmployees.Add(employee);
        db.SaveChanges();

        var user = new SysUser
        {
            UserName = code, PasswordHash = "hash", PasswordSalt = "salt",
            DisplayName = code, Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        db.SaveChanges();

        var role = new SysRole
        {
            RoleName = "里程碑操作员", RoleCode = $"CsmOp-{Guid.NewGuid():N}", IsSystem = false
        };
        db.SysRoles.Add(role);
        db.SaveChanges();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        db.SaveChanges();

        foreach (var menuCode in menuCodes)
        {
            var menu = new SysMenu { MenuCode = menuCode, MenuName = menuCode, MenuType = MenuType.Menu };
            db.SysMenus.Add(menu);
            db.SaveChanges();
            db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
            db.SaveChanges();
        }

        return (user.Id, employee.Id);
    }

    /// <summary>取控制器上的登录身份（用于把同一既有账号交给另一个控制器）。</summary>
    private static long? ControllerUser(ControllerBase controller)
        => long.TryParse(controller.ControllerContext.HttpContext.User
            .FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value, out var id) ? id : null;

    private static async Task<int> QueryTotalAsync(ContainerShipmentMilestoneController controller)
        => AssertOk<PagedResult<ContainerShipmentMilestoneDto>>(
            await controller.GetPaged(new ContainerShipmentMilestoneQuery())).Total;

    // ==================== 1. 两个独立连接：登记一致性 ====================

    [Fact]
    public async Task 两个独立连接登记同一父记录同类型同时间_只产生一条有效证据_后到者明确重复()
    {
        var databaseName = $"csm-race-{Guid.NewGuid():N}";
        long referenceId;
        long userId;

        await using (var seeding = OpenShared(databaseName))
        {
            SeedCustomer(seeding, 910001, "本人客户");
            var booking = SeedBooking(seeding, "DG-CSM-RACE-1", 910001);
            referenceId = SeedReference(seeding,
                ContainerShipmentReferenceRules.SourceTypeBooking, booking.Id, "DG-CSM-RACE-1").Id;
            userId = TestAuth.SeedPrivilegedUser(seeding);
        }

        // 两个独立上下文（同一内存库）＝ 两条独立连接。
        await using var connectionA = OpenShared(databaseName);
        await using var connectionB = OpenShared(databaseName);

        var created = AssertOk<ContainerShipmentMilestoneDto>(await ControllerFor(connectionA, userId)
            .Create(Dto(referenceId)));

        // 后到者（另一条连接）看到已登记的有效证据 → 明确重复，绝不产生第二条有效证据
        var duplicate = await AssertBusinessAsync(ErrorCodes.Duplicate, () => ControllerFor(connectionB, userId)
            .Create(Dto(referenceId, notes: "重复登记")));
        Assert.Contains("不会静默合并", duplicate.Message);

        await using var verify = OpenShared(databaseName);
        var stored = Assert.Single(await verify.ContainerShipmentMilestones.AsNoTracking().ToListAsync());
        Assert.Equal(created.Id, stored.Id);
        Assert.Equal("船公司网站截图", stored.SourceDescription);   // 既有证据完全不被覆盖
        Assert.Equal(ContainerShipmentMilestoneRules.StatusRecorded, stored.Status);
        Assert.Equal(userId, stored.CreatedBy);                     // 审计：真实登录用户
        Assert.Equal(userId, stored.UpdatedBy);

        // 同一父记录但不同时间 / 不同类型仍可登记（不是「文本相同」就去重）
        AssertOk<ContainerShipmentMilestoneDto>(await ControllerFor(connectionB, userId)
            .Create(Dto(referenceId, ContainerShipmentMilestoneRules.EventTypeInspection,
                DefaultEventAt.AddHours(1))));
        Assert.Equal(2, await verify.ContainerShipmentMilestones.CountAsync());
    }

    [Fact]
    public async Task 作废后可重新登记_旧证据与作废留痕完全不变且重复作废明确失败()
    {
        await using var db = TestDbFactory.Create();
        SeedCustomer(db, 910002, "本人客户");
        var booking = SeedBooking(db, "DG-CSM-VOID", 910002);
        var reference = SeedReference(db,
            ContainerShipmentReferenceRules.SourceTypeBooking, booking.Id, "DG-CSM-VOID");
        var controller = PrivilegedController(db);

        var recorded = AssertOk<ContainerShipmentMilestoneDto>(await controller.Create(Dto(reference.Id)));

        // 作废必须填写原因
        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => controller.Void(
            recorded.Id, new ContainerShipmentMilestoneVoidRequest { Reason = "   " }));

        var voided = AssertOk<ContainerShipmentMilestoneDto>(await controller.Void(
            recorded.Id, new ContainerShipmentMilestoneVoidRequest { Reason = "实际开船时间录错" }));
        Assert.Equal(ContainerShipmentMilestoneRules.StatusVoided, voided.Status);
        Assert.Equal("实际开船时间录错", voided.VoidReason);

        // 原始事件 / 时间 / 来源 / 记录人 / 登记时间逐字段保留（作废不是删除，也不是改写）
        Assert.Equal(recorded.EventType, voided.EventType);
        Assert.Equal(recorded.EventAt, voided.EventAt);
        Assert.Equal(recorded.SourceDescription, voided.SourceDescription);
        Assert.Equal(recorded.RecordedBy, voided.RecordedBy);
        Assert.Equal(recorded.RecordedAt, voided.RecordedAt);
        Assert.Equal(recorded.ContainerShipmentReferenceId, voided.ContainerShipmentReferenceId);

        // 重复作废被拒绝；已作废证据仍可读
        var repeat = await AssertBusinessAsync(ErrorCodes.RuleConflict, () => controller.Void(
            recorded.Id, new ContainerShipmentMilestoneVoidRequest { Reason = "重复作废" }));
        Assert.Contains("已作废状态", repeat.Message);
        var stored = await db.ContainerShipmentMilestones.AsNoTracking().SingleAsync();
        Assert.Equal("实际开船时间录错", stored.VoidReason);
        Assert.Equal("船公司网站截图", stored.SourceDescription);

        // 作废后同一父 + 类型 + 时间可重新登记一条新的有效证据（新旧并存可查）
        var reRecorded = AssertOk<ContainerShipmentMilestoneDto>(await controller.Create(Dto(reference.Id)));
        Assert.NotEqual(recorded.Id, reRecorded.Id);
        Assert.Equal(2, await QueryTotalAsync(controller));
    }

    // ==================== 2. 父出运引用失效后的读保留与写拒绝 ====================

    [Fact]
    public async Task 父记录作废或删除后登记被拒绝_历史里程碑保留可读并显式标注不可用()
    {
        await using var db = TestDbFactory.Create();
        SeedCustomer(db, 910003, "本人客户");
        var booking = SeedBooking(db, "DG-CSM-PARENT", 910003);
        var reference = SeedReference(db,
            ContainerShipmentReferenceRules.SourceTypeBooking, booking.Id, "DG-CSM-PARENT");
        var controller = PrivilegedController(db);

        var recorded = AssertOk<ContainerShipmentMilestoneDto>(await controller.Create(Dto(reference.Id)));

        // 父出运引用作废（经 ERP-362 真实控制器，既有特权账号）：历史里程碑可读、显式标注，不得新增证据
        var referenceController = new ContainerShipmentReferenceController(db);
        TestAuth.SetUser(referenceController, ControllerUser(controller));
        AssertOk<ContainerShipmentReferenceDto>(await referenceController.Void(
            reference.Id, new ContainerShipmentReferenceVoidRequest { Reason = "父引用录错作废" }));

        var voidedRow = Assert.Single(AssertOk<PagedResult<ContainerShipmentMilestoneDto>>(
            await controller.GetPaged(new ContainerShipmentMilestoneQuery())).Items);
        Assert.Equal(recorded.Id, voidedRow.Id);
        Assert.Contains("父出运引用已作废", voidedRow.ParentAvailabilityText);
        Assert.Contains("不能新增里程碑", voidedRow.ParentAvailabilityText);

        await AssertBusinessAsync(ErrorCodes.RuleConflict, () => controller.Create(Dto(reference.Id,
            eventAt: DefaultEventAt.AddHours(3))));
        Assert.Equal(1, await QueryTotalAsync(controller));

        // 父出运引用软删除：历史里程碑可读、显式标注不可用，且不能新增 / 不能改派
        reference.IsDeleted = true;
        reference.UpdatedAt = DateTime.Now;
        await db.SaveChangesAsync();
        var deletedRow = Assert.Single(AssertOk<PagedResult<ContainerShipmentMilestoneDto>>(
            await controller.GetPaged(new ContainerShipmentMilestoneQuery())).Items);
        Assert.False(deletedRow.ParentAvailable);
        Assert.Contains("已删除或不存在", deletedRow.ParentAvailabilityText);
        await AssertBusinessAsync(ErrorCodes.RuleConflict, () => controller.Create(Dto(reference.Id,
            eventAt: DefaultEventAt.AddHours(4))));
        Assert.Equal(1, await QueryTotalAsync(controller));
    }

    // ==================== 3. 父引用源记录资格：取消 / 删除 / 不存在 ====================

    [Fact]
    public async Task 父出运引用的源记录被取消或删除或不存在时拒绝新增证据_历史里程碑照常可读()
    {
        await using var db = TestDbFactory.Create();
        SeedCustomer(db, 910004, "本人客户");
        var activeBooking = SeedBooking(db, "DG-CSM-SRC-ACTIVE", 910004);
        var active = SeedReference(db,
            ContainerShipmentReferenceRules.SourceTypeBooking, activeBooking.Id, "DG-CSM-SRC-ACTIVE");
        var cancelledBooking = SeedBooking(db, "DG-CSM-SRC-CANCEL", 910004);
        var cancelled = SeedReference(db,
            ContainerShipmentReferenceRules.SourceTypeBooking, cancelledBooking.Id, "DG-CSM-SRC-CANCEL");
        var deletedBooking = SeedBooking(db, "DG-CSM-SRC-DEL", 910004);
        var deleted = SeedReference(db,
            ContainerShipmentReferenceRules.SourceTypeBooking, deletedBooking.Id, "DG-CSM-SRC-DEL");
        var missing = SeedReference(db,
            ContainerShipmentReferenceRules.SourceTypeBooking, 987_654_321L, "DG-CSM-SRC-MISSING");
        var controller = PrivilegedController(db);

        // 先登记一条历史证据，再把源记录取消 / 删除：历史照常可读，但不得新增证据
        var recorded = AssertOk<ContainerShipmentMilestoneDto>(await controller.Create(Dto(cancelled.Id)));
        cancelledBooking.Status = DocumentStatus.Cancelled;
        deletedBooking.IsDeleted = true;
        await db.SaveChangesAsync();

        var cancelEx = await AssertBusinessAsync(ErrorCodes.RuleConflict, () => controller.Create(Dto(cancelled.Id,
            eventAt: DefaultEventAt.AddHours(1))));
        Assert.Contains("源记录已取消", cancelEx.Message);
        Assert.Contains("历史里程碑仍可读", cancelEx.Message);

        var deleteEx = await AssertBusinessAsync(ErrorCodes.RuleConflict, () => controller.Create(Dto(deleted.Id)));
        Assert.Contains("源记录已删除", deleteEx.Message);

        var missingEx = await AssertBusinessAsync(ErrorCodes.NotFound, () => controller.Create(Dto(missing.Id)));
        Assert.Contains("源记录不存在", missingEx.Message);

        // 有效源记录仍可正常登记（历史证据不受影响）
        AssertOk<ContainerShipmentMilestoneDto>(await controller.Create(Dto(active.Id, notes: "源记录有效")));

        // 历史证据照常可读且逐字段不变
        var rows = AssertOk<PagedResult<ContainerShipmentMilestoneDto>>(
            await controller.GetPaged(new ContainerShipmentMilestoneQuery { PageSize = 50 })).Items;
        Assert.Equal(2, rows.Count);
        var history = rows.Single(r => r.Id == recorded.Id);
        Assert.Equal("船公司网站截图", history.SourceDescription);
        Assert.Equal(DefaultEventAt, history.EventAt);

        // 源记录状态绝不因里程碑登记 / 拒绝而被改写
        Assert.Equal(DocumentStatus.Cancelled,
            (await db.ContainerBookings.AsNoTracking().SingleAsync(b => b.Id == cancelledBooking.Id)).Status);
        Assert.True((await db.ContainerBookings.AsNoTracking().SingleAsync(b => b.Id == deletedBooking.Id)).IsDeleted);
        Assert.Equal(DocumentStatus.Pending,
            (await db.ContainerBookings.AsNoTracking().SingleAsync(b => b.Id == activeBooking.Id)).Status);
    }

    // ==================== 4. 失败登记不落证据、不改上游与相邻业务数据 ====================

    [Fact]
    public async Task 失败的登记不落任何证据也不改写上游源记录与相邻业务数据()
    {
        await using var db = TestDbFactory.Create();
        SeedCustomer(db, 910005, "本人客户");
        var booking = SeedBooking(db, "DG-CSM-ROLLBACK", 910005);
        var preLoading = SeedPreLoading(db, "YZ-CSM-ROLLBACK", booking.Id);
        var loadingList = new ContainerLoadingList
        {
            LoadingListNo = "ZJ-CSM-ROLLBACK",
            PreLoadingId = preLoading.Id,
            LoadingDate = new DateTime(2026, 9, 5),
            ContainerNo = "CONT-CSM-ROLLBACK",
            CustomerId = 910005,
            TotalCartons = 100,
            Status = DocumentStatus.Submitted
        };
        var stock = new Stock
        {
            WarehouseId = 1, ProductId = 1, Quantity = 120m, AvailableQuantity = 120m, AverageCost = 10.5m
        };
        var order = new SalesOrder
        {
            OrderNo = "SO-CSM-ROLLBACK",
            OrderDate = new DateTime(2026, 9, 1),
            CustomerId = 910005,
            Currency = Currency.USD,
            TotalAmount = 1000m,
            Status = DocumentStatus.Approved
        };
        db.ContainerLoadingLists.Add(loadingList);
        db.Stocks.Add(stock);
        db.SalesOrders.Add(order);
        await db.SaveChangesAsync();

        var reference = SeedReference(db,
            ContainerShipmentReferenceRules.SourceTypeBooking, booking.Id, "DG-CSM-ROLLBACK");
        var controller = PrivilegedController(db);

        // 1) 事件类型不在 allowlist → 拒绝，不落任何行
        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => controller.Create(
            Dto(reference.Id, "customs-hold")));
        // 2) 事件时间缺失（绝不按计划时间推断）→ 拒绝
        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => controller.Create(
            new ContainerShipmentMilestoneSaveDto
            {
                ContainerShipmentReferenceId = reference.Id,
                EventType = ContainerShipmentMilestoneRules.EventTypeActualArrival,
                EventAt = null
            }));
        // 3) 父引用不存在 → 数据不存在
        await AssertBusinessAsync(ErrorCodes.NotFound, () => controller.Create(Dto(999_999)));
        Assert.Equal(0, await db.ContainerShipmentMilestones.CountAsync());

        // 4) 先成功登记一条，再重复登记（失败）：既有证据不变、不新增行
        var ok = AssertOk<ContainerShipmentMilestoneDto>(await controller.Create(Dto(reference.Id)));
        await AssertBusinessAsync(ErrorCodes.Duplicate, () => controller.Create(Dto(reference.Id)));
        Assert.Equal(1, await db.ContainerShipmentMilestones.CountAsync());

        // 上游源记录 / 相邻业务与库存数据完全不变
        var storedBooking = await db.ContainerBookings.AsNoTracking().SingleAsync(b => b.Id == booking.Id);
        Assert.Equal(DocumentStatus.Pending, storedBooking.Status);
        Assert.Null(storedBooking.Atd);
        Assert.Null(storedBooking.Ata);
        Assert.Equal(DocumentStatus.Pending,
            (await db.ContainerPreLoadings.AsNoTracking().SingleAsync(p => p.Id == preLoading.Id)).Status);
        Assert.Equal(DocumentStatus.Submitted,
            (await db.ContainerLoadingLists.AsNoTracking().SingleAsync(l => l.Id == loadingList.Id)).Status);
        Assert.Equal(120m, (await db.Stocks.AsNoTracking().SingleAsync(s => s.Id == stock.Id)).Quantity);
        Assert.Equal(1000m, (await db.SalesOrders.AsNoTracking().SingleAsync(o => o.Id == order.Id)).TotalAmount);

        // 父出运引用本身不被里程碑模块改写（不新增修订 / 作废留痕）
        var storedReference = await db.ContainerShipmentReferences.AsNoTracking()
            .SingleAsync(r => r.Id == reference.Id);
        Assert.Equal(ContainerShipmentReferenceRules.StatusRecorded, storedReference.Status);
        Assert.Equal(1, storedReference.RevisionNo);
        Assert.Equal(0, await db.ContainerShipmentReferenceRevisions.CountAsync());
        Assert.Equal(ok.Id, (await db.ContainerShipmentMilestones.AsNoTracking().SingleAsync()).Id);
    }

    // ==================== 5. 既有源模块菜单 + 权威客户数据范围（读 / 写同口径） ====================

    [Fact]
    public async Task 受限账号只能读写本人客户源记录的里程碑_台账详情候选与写入按范围收敛()
    {
        var databaseName = $"csm-scope-{Guid.NewGuid():N}";
        await using var db = OpenShared(databaseName);
        var (userId, employeeId) = SeedRestrictedOperator(
            db,
            ShipmentReferenceAuthorizationRules.BookingMenuCode,
            ShipmentReferenceAuthorizationRules.PreLoadingMenuCode);

        SeedCustomer(db, 911001, "本人客户", employeeId);
        SeedCustomer(db, 911002, "他人客户");
        var ownBooking = SeedBooking(db, "DG-CSM-OWN", 911001);
        var foreignBooking = SeedBooking(db, "DG-CSM-FOREIGN", 911002);
        var ownPreLoading = SeedPreLoading(db, "YZ-CSM-OWN", ownBooking.Id);
        var orphanPreLoading = SeedPreLoading(db, "YZ-CSM-ORPHAN", null);
        var own = SeedReference(db,
            ContainerShipmentReferenceRules.SourceTypeBooking, ownBooking.Id, "DG-CSM-OWN");
        var foreign = SeedReference(db,
            ContainerShipmentReferenceRules.SourceTypeBooking, foreignBooking.Id, "DG-CSM-FOREIGN");
        var ownPre = SeedReference(db,
            ContainerShipmentReferenceRules.SourceTypePreLoading, ownPreLoading.Id, "YZ-CSM-OWN");
        var orphan = SeedReference(db,
            ContainerShipmentReferenceRules.SourceTypePreLoading, orphanPreLoading.Id, "YZ-CSM-ORPHAN");

        // 特权连接先登记四条证据（本人客户 / 他人客户 / 本人预装柜 / 无归属孤儿预装柜）
        var seeder = PrivilegedController(db);
        var ownRow = AssertOk<ContainerShipmentMilestoneDto>(await seeder.Create(Dto(own.Id)));
        var foreignRow = AssertOk<ContainerShipmentMilestoneDto>(await seeder.Create(Dto(foreign.Id)));
        var ownPreRow = AssertOk<ContainerShipmentMilestoneDto>(await seeder.Create(Dto(ownPre.Id)));
        var orphanRow = AssertOk<ContainerShipmentMilestoneDto>(await seeder.Create(Dto(orphan.Id)));

        var controller = ControllerFor(db, userId);

        // 台账：客户范围在 Count / 分页之前下推（受限账号只统计 / 只返回本人客户源记录的里程碑）
        var page = AssertOk<PagedResult<ContainerShipmentMilestoneDto>>(
            await controller.GetPaged(new ContainerShipmentMilestoneQuery { PageSize = 50 }));
        Assert.Equal(2, page.Total);
        Assert.Equal(2, page.Items.Count);
        Assert.Contains(page.Items, r => r.Id == ownRow.Id);
        Assert.Contains(page.Items, r => r.Id == ownPreRow.Id);
        Assert.DoesNotContain(page.Items, r => r.Id == foreignRow.Id);
        Assert.DoesNotContain(page.Items, r => r.Id == orphanRow.Id);

        // 详情：范围外 / 无权威归属一律 fail closed，不泄露证据
        AssertOk<ContainerShipmentMilestoneDetailDto>(await controller.GetById(ownRow.Id));
        await AssertBusinessAsync(ErrorCodes.Forbidden, () => controller.GetById(foreignRow.Id));
        await AssertBusinessAsync(ErrorCodes.Forbidden, () => controller.GetById(orphanRow.Id));

        // 父记录候选：只列本人客户的源记录（无权威客户归属的孤儿预装柜单对受限账号不可见）
        var candidates = AssertOk<List<ContainerShipmentMilestoneParentCandidateDto>>(
            await controller.ParentCandidates(null, 200));
        Assert.Equal(2, candidates.Count);
        Assert.Contains(candidates, c => c.ContainerShipmentReferenceId == own.Id);
        Assert.Contains(candidates, c => c.ContainerShipmentReferenceId == ownPre.Id);

        // 写入：本人客户源记录可登记；范围外 / 无权威归属一律 fail closed
        AssertOk<ContainerShipmentMilestoneDto>(await controller.Create(Dto(own.Id,
            ContainerShipmentMilestoneRules.EventTypeInspection, DefaultEventAt.AddHours(5))));
        await AssertBusinessAsync(ErrorCodes.Forbidden, () => controller.Create(Dto(foreign.Id,
            ContainerShipmentMilestoneRules.EventTypeInspection, DefaultEventAt.AddHours(6))));
        await AssertBusinessAsync(ErrorCodes.Forbidden, () => controller.Create(Dto(orphan.Id,
            ContainerShipmentMilestoneRules.EventTypeInspection, DefaultEventAt.AddHours(7))));

        // 范围外作废 fail closed 且证据不被改写；本人范围内作废成功
        await AssertBusinessAsync(ErrorCodes.Forbidden, () => controller.Void(
            foreignRow.Id, new ContainerShipmentMilestoneVoidRequest { Reason = "越范围作废" }));
        AssertOk<ContainerShipmentMilestoneDto>(await controller.Void(
            ownRow.Id, new ContainerShipmentMilestoneVoidRequest { Reason = "本人范围更正" }));

        var foreignStored = await db.ContainerShipmentMilestones.AsNoTracking()
            .SingleAsync(m => m.Id == foreignRow.Id);
        Assert.Equal(ContainerShipmentMilestoneRules.StatusRecorded, foreignStored.Status);
        Assert.Equal(string.Empty, foreignStored.VoidReason);
        var ownStored = await db.ContainerShipmentMilestones.AsNoTracking().SingleAsync(m => m.Id == ownRow.Id);
        Assert.Equal(ContainerShipmentMilestoneRules.StatusVoided, ownStored.Status);
        Assert.Equal("船公司网站截图", ownStored.SourceDescription);      // 原始证据保留
    }

    // ==================== 6. 撤销菜单 / 身份异常一律 fail closed（不新增授权，不匿名回退） ====================

    [Fact]
    public async Task 撤销既有源模块菜单后下一个请求立即收敛_身份异常与未映射业务员一律fail_closed()
    {
        await using var db = TestDbFactory.Create();
        SeedCustomer(db, 912001, "客户");
        var booking = SeedBooking(db, "DG-CSM-MENU", 912001);
        var reference = SeedReference(db,
            ContainerShipmentReferenceRules.SourceTypeBooking, booking.Id, "DG-CSM-MENU");
        var row = AssertOk<ContainerShipmentMilestoneDto>(await PrivilegedController(db).Create(Dto(reference.Id)));

        // 授予既有「订柜信息」菜单的受限账号（并映射到已分配本人客户的业务员）：先可用（只读 + 登记都通过）
        var (userId, employeeId) = SeedRestrictedOperator(db, ShipmentReferenceAuthorizationRules.BookingMenuCode);
        db.BaseCustomers.First(c => c.Id == 912001).EmpId = employeeId;
        await db.SaveChangesAsync();
        var controller = ControllerFor(db, userId);
        Assert.Equal(1, await QueryTotalAsync(controller));
        AssertOk<ContainerShipmentMilestoneDto>(await controller.Create(Dto(reference.Id,
            ContainerShipmentMilestoneRules.EventTypeInspection, DefaultEventAt.AddHours(1))));

        // 撤销菜单（删除既有角色菜单授权，不新增任何权限）：下一个请求立即收敛为权限不足
        db.SysRoleMenus.RemoveRange(await db.SysRoleMenus.ToListAsync());
        await db.SaveChangesAsync();

        await AssertBusinessAsync(ErrorCodes.Forbidden,
            () => controller.GetPaged(new ContainerShipmentMilestoneQuery()));
        await AssertBusinessAsync(ErrorCodes.Forbidden, () => controller.GetById(row.Id));
        await AssertBusinessAsync(ErrorCodes.Forbidden, () => controller.ParentCandidates(null, 200));
        await AssertBusinessAsync(ErrorCodes.Forbidden, () => controller.Create(Dto(reference.Id,
            ContainerShipmentMilestoneRules.EventTypeActualArrival, DefaultEventAt.AddHours(2))));
        await AssertBusinessAsync(ErrorCodes.Forbidden, () => controller.Void(
            row.Id, new ContainerShipmentMilestoneVoidRequest { Reason = "撤销菜单后作废" }));

        // 未映射业务员的受限账号（有既有菜单但登录名未映射员工）→ fail closed，不降级为全局可见
        var unmappedUser = new SysUser
        {
            UserName = $"csm-unmapped-{Guid.NewGuid():N}", PasswordHash = "hash", PasswordSalt = "salt",
            DisplayName = "未映射业务员", Status = UserStatus.Enabled
        };
        db.SysUsers.Add(unmappedUser);
        await db.SaveChangesAsync();
        var unmappedRole = new SysRole
        {
            RoleName = "未映射操作员", RoleCode = $"CsmUnmapped-{Guid.NewGuid():N}", IsSystem = false
        };
        db.SysRoles.Add(unmappedRole);
        await db.SaveChangesAsync();
        db.SysUserRoles.Add(new SysUserRole { UserId = unmappedUser.Id, RoleId = unmappedRole.Id });
        var unmappedMenu = new SysMenu
        {
            MenuCode = ShipmentReferenceAuthorizationRules.BookingMenuCode,
            MenuName = ShipmentReferenceAuthorizationRules.BookingMenuCode,
            MenuType = MenuType.Menu
        };
        db.SysMenus.Add(unmappedMenu);
        await db.SaveChangesAsync();
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = unmappedRole.Id, MenuId = unmappedMenu.Id });
        await db.SaveChangesAsync();

        await AssertBusinessAsync(ErrorCodes.Forbidden, () => ControllerFor(db, unmappedUser.Id)
            .GetPaged(new ContainerShipmentMilestoneQuery()));

        // 身份缺失 / 非法 / 不存在 / 已删除 → 未认证；禁用 → 权限不足
        await AssertBusinessAsync(ErrorCodes.Unauthorized, () => ControllerFor(db, null)
            .GetPaged(new ContainerShipmentMilestoneQuery()));
        await AssertBusinessAsync(ErrorCodes.Unauthorized, () => ControllerFor(db, 0)
            .GetPaged(new ContainerShipmentMilestoneQuery()));
        await AssertBusinessAsync(ErrorCodes.Unauthorized, () => ControllerFor(db, 987_654_321L)
            .GetById(row.Id));

        var (disabledUserId, _) = SeedRestrictedOperator(db, ShipmentReferenceAuthorizationRules.BookingMenuCode);
        (await db.SysUsers.FirstAsync(u => u.Id == disabledUserId)).Status = UserStatus.Disabled;
        await db.SaveChangesAsync();
        await AssertBusinessAsync(ErrorCodes.Forbidden, () => ControllerFor(db, disabledUserId)
            .GetPaged(new ContainerShipmentMilestoneQuery()));

        var deletedUser = new SysUser
        {
            UserName = $"csm-del-{Guid.NewGuid():N}", PasswordHash = "hash", PasswordSalt = "salt",
            DisplayName = "已删除账号", Status = UserStatus.Enabled, IsDeleted = true
        };
        db.SysUsers.Add(deletedUser);
        await db.SaveChangesAsync();
        await AssertBusinessAsync(ErrorCodes.Unauthorized, () => ControllerFor(db, deletedUser.Id)
            .GetPaged(new ContainerShipmentMilestoneQuery()));

        // 撤销菜单 / 越权尝试绝不改写任何既有证据
        var stored = await db.ContainerShipmentMilestones.AsNoTracking().SingleAsync(m => m.Id == row.Id);
        Assert.Equal(ContainerShipmentMilestoneRules.StatusRecorded, stored.Status);
        Assert.Equal(string.Empty, stored.VoidReason);
        Assert.Equal(2, await db.ContainerShipmentMilestones.CountAsync());   // 只有登记成功的那一条
    }

    // ==================== 7. 串行化与授权接线契约 ====================

    [Fact]
    public void 控制器在可序列化事务内对父引用源记录与里程碑行加锁_且每个路由先授权()
    {
        var controller = File.ReadAllText(RepoFile(
            "src", "ERP.Api", "Controllers", "ContainerShipmentMilestoneController.cs"));

        // 登记：父引用行锁（与 ERP-362 同一把）+ 源记录行锁 + 可序列化事务；作废：里程碑行锁 + 可序列化事务
        Assert.Contains("IsolationLevel.Serializable", controller);
        Assert.Contains(
            "SELECT Id FROM db_owner.ContainerShipmentReferences WITH (UPDLOCK, HOLDLOCK) WHERE Id = {0}",
            controller);
        Assert.Contains(
            "SELECT Id FROM db_owner.ContainerBookings WITH (UPDLOCK, HOLDLOCK) WHERE Id = {0}", controller);
        Assert.Contains(
            "SELECT Id FROM db_owner.ContainerPreLoadings WITH (UPDLOCK, HOLDLOCK) WHERE Id = {0}", controller);
        Assert.Contains(
            "SELECT Id FROM db_owner.ContainerLoadingLists WITH (UPDLOCK, HOLDLOCK) WHERE Id = {0}", controller);
        Assert.Contains(
            "SELECT Id FROM db_owner.ContainerShipmentMilestones WITH (UPDLOCK, HOLDLOCK) WHERE Id = {0}",
            controller);
        Assert.Contains("await transaction.CommitAsync();", controller);
        Assert.Contains("await transaction.RollbackAsync();", controller);
        Assert.Contains("IsRelational()", controller);

        // 5 个路由（台账 / 父记录候选 / 详情 / 登记 / 作废）全部先实时授权
        Assert.Equal(5, CountOccurrences(
            controller, "ShipmentReferenceAuthorizationRules.EnsureAuthorizedAsync"));

        // 加锁只 SELECT Id：绝不改写父出运引用 / 源记录任何列，也不调用外部系统
        Assert.DoesNotContain("db_owner.ContainerBookings SET", controller);
        Assert.DoesNotContain("db_owner.ContainerPreLoadings SET", controller);
        Assert.DoesNotContain("db_owner.ContainerLoadingLists SET", controller);
        Assert.DoesNotContain("db_owner.ContainerShipmentReferences SET", controller);
        Assert.DoesNotContain("db_owner.ContainerShipmentMilestones SET", controller);
        Assert.DoesNotContain("HttpClient", controller);

        // 服务侧：授权先于任何证据读取的接线契约
        var service = File.ReadAllText(RepoFile(
            "src", "ERP.Application", "Services", "ContainerShipmentMilestoneService.cs"));
        Assert.Contains("ShipmentReferenceAuthorizationRules.RequireSourceType", service);
        Assert.Contains("ShipmentReferenceAuthorizationRules.EnsureScopeAllowsSourceAsync", service);
        Assert.Contains("ShipmentReferenceAuthorizationRules.EnsureScopeAllowsParentReferenceAsync", service);
        Assert.Contains("ShipmentReferenceAuthorizationRules.ApplyScopeToMilestones", service);
        Assert.Contains("ContainerShipmentReferenceService.ResolveSourceStateAsync", service);
        Assert.True(service.IndexOf("EnsureScopeAllowsSourceAsync", StringComparison.Ordinal)
                    < service.IndexOf("var duplicated", StringComparison.Ordinal));
        Assert.True(service.IndexOf("ApplyScopeToMilestones", StringComparison.Ordinal)
                    < service.IndexOf("var total = await source.CountAsync()", StringComparison.Ordinal));
        Assert.DoesNotContain("db.ContainerShipmentReferences.Add", service);
        Assert.DoesNotContain("db.ContainerShipmentReferences.Update", service);
        Assert.DoesNotContain("db.ContainerShipmentReferences.Remove", service);
        Assert.DoesNotContain("db.ContainerShipmentMilestones.Remove", service);
        Assert.DoesNotContain("HttpClient", service);
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = text.IndexOf(value, StringComparison.Ordinal);
        while (index >= 0)
        {
            count++;
            index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal);
        }
        return count;
    }

    [Fact]
    public void 授权规则复用既有权限模型_不新增授权也不放宽特权()
    {
        var rules = File.ReadAllText(RepoFile(
            "src", "ERP.Application", "Services", "ShipmentReferenceAuthorizationRules.cs"));

        // 与 ERP-362 同源：既有菜单编码 + 既有权威数据范围
        Assert.Contains("PreLoadingBookingLinkRules.BookingRequiredMenuCode", rules);
        Assert.Contains("PreLoadingBookingLinkRules.RequiredMenuCode", rules);
        Assert.Contains("ContainerLoadingFulfillmentRules.RequiredMenuCode", rules);
        Assert.Contains("SalespersonDataScopeService.ResolveAsync", rules);
        Assert.Contains("CustomerReceivableReconciliationService", rules);
        Assert.Contains("ApplyScopeToMilestones", rules);
        Assert.Contains("EnsureScopeAllowsParentReferenceAsync", rules);

        // 不写库、不新增用户授权 / 权限模型、不调用外部系统
        Assert.DoesNotContain("SysUserRoles.Add", rules);
        Assert.DoesNotContain("SysRoleMenus.Add", rules);
        Assert.DoesNotContain("SysMenus.Add", rules);
        Assert.DoesNotContain("SaveChangesAsync", rules);
        Assert.DoesNotContain("HttpClient", rules);
        Assert.Contains("fail closed", rules);
        Assert.Contains("绝不降级为全局 / 管理员可见", ShipmentReferenceAuthorizationRules.RuleText);

        // 源记录资格与可用性文案（存在 / 未删除 / 未取消；新增前复核）
        Assert.False(ContainerShipmentMilestoneRules
            .EvaluateParentSourceEligibility(false, false, false, "订柜信息").Eligible);
        Assert.False(ContainerShipmentMilestoneRules
            .EvaluateParentSourceEligibility(true, true, false, "订柜信息").Eligible);
        Assert.False(ContainerShipmentMilestoneRules
            .EvaluateParentSourceEligibility(true, false, true, "订柜信息").Eligible);
        Assert.True(ContainerShipmentMilestoneRules
            .EvaluateParentSourceEligibility(true, false, false, "订柜信息").Eligible);
        Assert.Contains("源记录已取消", ContainerShipmentMilestoneRules
            .EvaluateParentSourceEligibility(true, false, true, "订柜信息").Text);
        Assert.Contains("复核该订柜信息未删除 / 未取消", ContainerShipmentMilestoneRules
            .ParentAvailabilityText(true, false, "订柜信息"));
        Assert.Contains("父出运引用已作废", ContainerShipmentMilestoneRules
            .ParentAvailabilityText(true, true, "订柜信息"));
        Assert.Contains("已删除或不存在", ContainerShipmentMilestoneRules
            .ParentAvailabilityText(false, false, "订柜信息"));
    }

    [Fact]
    public void 文档记录授权并发口径与专用测试目标()
    {
        var doc = File.ReadAllText(RepoFile("docs", "shipment-milestone-concurrency.md"));
        Assert.Contains("UPDLOCK, HOLDLOCK", doc);
        Assert.Contains("Serializable", doc);
        Assert.Contains("(localdb)\\NEWERP_AutoAcceptance", doc);
        Assert.Contains("NEWERP_AUTOTEST", doc);
        Assert.Contains("绝不", doc);
        Assert.Contains("父引用行锁", doc);
        Assert.Contains("源记录行锁", doc);
        Assert.Contains("里程碑行锁", doc);
        Assert.Contains("构建通过不等于阶段验收", doc);
    }
}
