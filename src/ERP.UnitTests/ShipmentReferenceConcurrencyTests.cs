using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.DTOs;
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
/// 装柜出运引用登记（ERP-362）的串行化与并发一致性单元测试。覆盖：
/// <list type="number">
/// <item>两个**独立上下文**（同一内存库）的并发登记只能产生一条有效记录引用，后到者被明确拒绝为重复；</item>
/// <item>作废后可重新登记且旧证据（原始值 / 作废原因 / 修订留痕）完全不变，重复作废明确失败；</item>
/// <item>修订与作废按同一引用行串行化：作废后修订被拒绝且不改写已作废证据；连续修订保持单调修订号与原值链；</item>
/// <item>修订 / 登记任一步校验失败都不落库（证据与修订留痕都不产生半行）；</item>
/// <item>已取消 / 已删除 / 不存在的源记录不得**新登记**引用（历史证据仍可读）；</item>
/// <item>受限账号的既有源模块菜单 + 权威客户数据范围作用于台账 / 详情 / 候选 / 写入（fail closed）；</item>
/// <item>控制器「源记录行锁 + 引用行锁 + 可序列化事务」与授权接线的源码契约。</item>
/// </list>
/// <para>全部使用内存库（TestDbFactory 口径），不连接 SQL Server、不执行任何 SQL / 部署脚本；
/// 真正的「两条独立连接竞争同一行」由 <c>ShipmentReferenceConcurrencySqlServerTests</c> 在
/// 专用 localdb（<c>(localdb)\NEWERP_AutoAcceptance</c>，全新 GUID 库）上覆盖。</para>
/// </summary>
public class ShipmentReferenceConcurrencyTests
{
    // ==================== 0. 测试脚手架 ====================

    /// <summary>两个「独立连接」：同一内存库名 + 各自独立的 DbContext（等价两条独立连接）。</summary>
    private static ErpDbContext OpenShared(string databaseName)
        => new(new DbContextOptionsBuilder<ErpDbContext>()
            .UseInMemoryDatabase(databaseName)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    /// <summary>既有特权账号控制器（系统内置角色，不新增任何菜单授权）。</summary>
    private static ContainerShipmentReferenceController PrivilegedController(ErpDbContext db)
    {
        var controller = new ContainerShipmentReferenceController(db);
        TestAuth.SetUser(controller, TestAuth.SeedPrivilegedUser(db));
        return controller;
    }

    private static ContainerShipmentReferenceController ControllerFor(ErpDbContext db, long? userId)
    {
        var controller = new ContainerShipmentReferenceController(db);
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

    private static ContainerShipmentReferenceSaveDto Dto(string sourceType, long sourceId, string mode = "FCL")
        => new() { SourceType = sourceType, SourceId = sourceId, ShipmentMode = mode };

    private static ContainerBooking SeedBooking(
        ErpDbContext db, string bookingNo, long customerId, DocumentStatus status = DocumentStatus.Pending)
    {
        var booking = new ContainerBooking
        {
            BookingNo = bookingNo,
            BookingDate = new DateTime(2026, 9, 1),
            CustomerId = customerId,
            ContainerType = ContainerType.GP40,
            Status = status
        };
        db.ContainerBookings.Add(booking);
        db.SaveChanges();
        return booking;
    }

    private static ContainerPreLoading SeedPreLoading(
        ErpDbContext db, string no, long? bookingId, DocumentStatus status = DocumentStatus.Pending)
    {
        var preLoading = new ContainerPreLoading
        {
            PreLoadingNo = no,
            LoadingDate = new DateTime(2026, 9, 3),
            BookingId = bookingId,
            ContainerNo = "CONT-" + no,
            Status = status
        };
        db.ContainerPreLoadings.Add(preLoading);
        db.SaveChanges();
        return preLoading;
    }

    private static void SeedCustomer(ErpDbContext db, long id, string name, long? empId = null)
    {
        db.BaseCustomers.Add(new BaseCustomer
        {
            Id = id, CustomerCode = $"CSR-C-{id}", CustomerName = name, EmpId = empId, Status = 1
        });
        db.SaveChanges();
    }

    /// <summary>受限业务员账号：员工映射（客户数据范围）+ 显式授予的既有源模块菜单。</summary>
    private static (long UserId, long EmployeeId) SeedRestrictedOperator(
        ErpDbContext db, params string[] menuCodes)
    {
        var code = $"csr-op-{Guid.NewGuid():N}";
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
            RoleName = "出运引用操作员", RoleCode = $"CsrOp-{Guid.NewGuid():N}", IsSystem = false
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

    // ==================== 1. 两个独立连接：登记并发一致性 ====================

    [Fact]
    public async Task 两个独立连接登记同一源记录_只产生一条有效引用_后到者明确重复()
    {
        var databaseName = $"csr-race-{Guid.NewGuid():N}";
        long bookingId;
        long userId;

        await using (var seeding = OpenShared(databaseName))
        {
            SeedCustomer(seeding, 900001, "本人客户");
            bookingId = SeedBooking(seeding, "DG-RACE-1", 900001).Id;
            userId = TestAuth.SeedPrivilegedUser(seeding);
        }

        // 两个独立上下文（同一内存库）＝ 两条独立连接。
        await using var connectionA = OpenShared(databaseName);
        await using var connectionB = OpenShared(databaseName);

        var created = AssertOk<ContainerShipmentReferenceDto>(await ControllerFor(connectionA, userId).Create(
            Dto(ContainerShipmentReferenceRules.SourceTypeBooking, bookingId, "FCL")));

        // 后到者（另一条连接）看到已登记的有效引用 → 明确重复，绝不产生第二条有效引用
        var duplicate = await AssertBusinessAsync(ErrorCodes.Duplicate, () => ControllerFor(connectionB, userId)
            .Create(Dto(ContainerShipmentReferenceRules.SourceTypeBooking, bookingId, "LCL")));
        Assert.Contains("已有有效出运引用", duplicate.Message);

        await using var verify = OpenShared(databaseName);
        Assert.Equal(1, await verify.ContainerShipmentReferences.CountAsync());
        Assert.Equal(created.Id, (await verify.ContainerShipmentReferences.AsNoTracking().SingleAsync()).Id);
        Assert.Equal("FCL", (await verify.ContainerShipmentReferences.AsNoTracking().SingleAsync()).ShipmentMode);
        Assert.Equal(0, await verify.ContainerShipmentReferenceRevisions.CountAsync());
    }

    [Fact]
    public async Task 作废后可重新登记_旧证据与修订留痕完全不变且重复作废明确失败()
    {
        var databaseName = $"csr-void-{Guid.NewGuid():N}";
        await using var db = OpenShared(databaseName);
        SeedCustomer(db, 900002, "本人客户");
        var booking = SeedBooking(db, "DG-VOID-1", 900002);
        var controller = PrivilegedController(db);

        var first = AssertOk<ContainerShipmentReferenceDto>(await controller.Create(
            Dto(ContainerShipmentReferenceRules.SourceTypeBooking, booking.Id, "FCL")));
        AssertOk<ContainerShipmentReferenceDto>(await controller.Update(first.Id,
            new ContainerShipmentReferenceUpdateDto
            {
                SourceType = ContainerShipmentReferenceRules.SourceTypeBooking,
                SourceId = booking.Id,
                Reason = "首次修订",
                ShipmentMode = "LCL",
                BillOfLadingNo = "BL-V1"
            }));
        var voided = AssertOk<ContainerShipmentReferenceDto>(await controller.Void(
            first.Id, new ContainerShipmentReferenceVoidRequest { Reason = "出运方式录错" }));
        Assert.Equal(ContainerShipmentReferenceRules.StatusVoided, voided.Status);
        Assert.Equal("LCL", voided.ShipmentMode);           // 原始值保留
        Assert.Equal("BL-V1", voided.BillOfLadingNo);

        // 重复作废：明确失败，且不新增任何留痕 / 不改写作废证据
        var duplicateVoid = await AssertBusinessAsync(ErrorCodes.RuleConflict, () => controller.Void(
            first.Id, new ContainerShipmentReferenceVoidRequest { Reason = "再次作废" }));
        Assert.Contains("已作废", duplicateVoid.Message);

        // 已作废证据只读：修订被 fail closed 拒绝
        await AssertBusinessAsync(ErrorCodes.RuleConflict, () => controller.Update(first.Id,
            new ContainerShipmentReferenceUpdateDto
            {
                SourceType = ContainerShipmentReferenceRules.SourceTypeBooking,
                SourceId = booking.Id,
                Reason = "作废后尝试改写",
                ShipmentMode = "FCL"
            }));

        var storedAfterVoid = await db.ContainerShipmentReferences.AsNoTracking().SingleAsync(o => o.Id == first.Id);
        Assert.Equal(ContainerShipmentReferenceRules.StatusVoided, storedAfterVoid.Status);
        Assert.Equal("出运方式录错", storedAfterVoid.VoidReason);
        Assert.Equal("BL-V1", storedAfterVoid.BillOfLadingNo);
        Assert.Equal(2, storedAfterVoid.RevisionNo);

        // 作废后允许重新登记一条**新的**有效引用（旧证据保持原样、新旧并存可查）
        var second = AssertOk<ContainerShipmentReferenceDto>(await controller.Create(
            Dto(ContainerShipmentReferenceRules.SourceTypeBooking, booking.Id, "FCL")));
        Assert.NotEqual(first.Id, second.Id);

        var all = await db.ContainerShipmentReferences.AsNoTracking().OrderBy(o => o.Id).ToListAsync();
        Assert.Equal(2, all.Count);
        Assert.Equal(ContainerShipmentReferenceRules.StatusVoided, all[0].Status);
        Assert.Equal("出运方式录错", all[0].VoidReason);
        Assert.Equal(ContainerShipmentReferenceRules.StatusRecorded, all[1].Status);
        Assert.Equal(1, await db.ContainerShipmentReferenceRevisions.AsNoTracking()
            .CountAsync(r => r.ContainerShipmentReferenceId == first.Id));
    }

    // ==================== 2. 修订 / 作废串行化与单调修订号 ====================

    [Fact]
    public async Task 连续修订保持单调修订号与逐版原值链()
    {
        await using var db = TestDbFactory.Create();
        SeedCustomer(db, 900003, "本人客户");
        var booking = SeedBooking(db, "DG-REV-1", 900003);
        var controller = PrivilegedController(db);

        var created = AssertOk<ContainerShipmentReferenceDto>(await controller.Create(
            Dto(ContainerShipmentReferenceRules.SourceTypeBooking, booking.Id, "LCL")));
        Assert.Equal(1, created.RevisionNo);

        var v2 = AssertOk<ContainerShipmentReferenceDto>(await controller.Update(created.Id,
            new ContainerShipmentReferenceUpdateDto
            {
                SourceType = ContainerShipmentReferenceRules.SourceTypeBooking,
                SourceId = booking.Id,
                Reason = "第一次修订",
                ShipmentMode = "FCL",
                CarrierName = "CARRIER-V2",
                BillOfLadingNo = "BL-V2"
            }));
        Assert.Equal(2, v2.RevisionNo);
        Assert.Equal(1, v2.RevisionCount);

        var v3 = AssertOk<ContainerShipmentReferenceDto>(await controller.Update(created.Id,
            new ContainerShipmentReferenceUpdateDto
            {
                SourceType = ContainerShipmentReferenceRules.SourceTypeBooking,
                SourceId = booking.Id,
                Reason = "第二次修订",
                ShipmentMode = "FCL",
                CarrierName = "CARRIER-V3",
                BillOfLadingNo = "BL-V3"
            }));
        Assert.Equal(3, v3.RevisionNo);
        Assert.Equal(2, v3.RevisionCount);

        // 留痕：V1（修订前原值）与 V2（第二版原值）逐列保留，修订号单调且不重复
        var revisions = AssertOk<List<ContainerShipmentReferenceRevisionDto>>(
            await controller.Revisions(created.Id, 50));
        Assert.Equal(2, revisions.Count);
        Assert.Equal(new[] { 2, 1 }, revisions.Select(r => r.RevisionNo).ToArray());
        Assert.Equal("LCL", revisions[1].ShipmentMode);            // V1 原值
        Assert.Equal(string.Empty, revisions[1].CarrierName);
        Assert.Equal("FCL", revisions[0].ShipmentMode);            // V2 原值
        Assert.Equal("CARRIER-V2", revisions[0].CarrierName);
        Assert.Equal("BL-V2", revisions[0].BillOfLadingNo);
        Assert.Equal("第一次修订", revisions[1].Reason);
        Assert.Equal("第二次修订", revisions[0].Reason);
        Assert.Equal(revisions.Count, revisions.Select(r => r.RevisionNo).Distinct().Count());
    }

    [Fact]
    public async Task 修订校验失败时证据与修订留痕都不落库()
    {
        var databaseName = $"csr-rev-fail-{Guid.NewGuid():N}";
        await using var db = OpenShared(databaseName);
        SeedCustomer(db, 900004, "本人客户");
        var booking = SeedBooking(db, "DG-REV-2", 900004);
        var controller = PrivilegedController(db);

        var created = AssertOk<ContainerShipmentReferenceDto>(await controller.Create(
            Dto(ContainerShipmentReferenceRules.SourceTypeBooking, booking.Id, "FCL")));

        // 计划时间先后不一致 → 校验失败（在写入任何一行之前失败）
        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => controller.Update(created.Id,
            new ContainerShipmentReferenceUpdateDto
            {
                SourceType = ContainerShipmentReferenceRules.SourceTypeBooking,
                SourceId = booking.Id,
                Reason = "非法计划时间",
                ShipmentMode = "FCL",
                PlannedDepartureAt = new DateTime(2026, 11, 20),
                PlannedArrivalAt = new DateTime(2026, 11, 1)
            }));

        // 文本超长 → 校验失败
        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => controller.Update(created.Id,
            new ContainerShipmentReferenceUpdateDto
            {
                SourceType = ContainerShipmentReferenceRules.SourceTypeBooking,
                SourceId = booking.Id,
                Reason = "超长承运人",
                ShipmentMode = "FCL",
                CarrierName = new string('C', ContainerShipmentReferenceRules.CarrierNameMaxLength + 1)
            }));

        // 用独立连接复核：证据仍是 V1，且没有产生任何修订留痕
        await using var verify = OpenShared(databaseName);
        var stored = await verify.ContainerShipmentReferences.AsNoTracking().SingleAsync(o => o.Id == created.Id);
        Assert.Equal(1, stored.RevisionNo);
        Assert.Equal("FCL", stored.ShipmentMode);
        Assert.Equal(ContainerShipmentReferenceRules.StatusRecorded, stored.Status);
        Assert.Equal(0, await verify.ContainerShipmentReferenceRevisions.CountAsync());
    }

    [Fact]
    public async Task 源记录已取消_已删除_不存在时拒绝新登记且不落库()
    {
        var databaseName = $"csr-source-{Guid.NewGuid():N}";
        await using var db = OpenShared(databaseName);
        SeedCustomer(db, 900005, "本人客户");
        var cancelled = SeedBooking(db, "DG-CANCELLED", 900005, DocumentStatus.Cancelled);
        var deleted = SeedBooking(db, "DG-DELETED", 900005);
        deleted.IsDeleted = true;
        db.SaveChanges();
        var active = SeedBooking(db, "DG-ACTIVE", 900005);

        var controller = PrivilegedController(db);

        var cancelledEx = await AssertBusinessAsync(ErrorCodes.RuleConflict, () => controller.Create(
            Dto(ContainerShipmentReferenceRules.SourceTypeBooking, cancelled.Id, "FCL")));
        Assert.Contains("已取消", cancelledEx.Message);

        var deletedEx = await AssertBusinessAsync(ErrorCodes.RuleConflict, () => controller.Create(
            Dto(ContainerShipmentReferenceRules.SourceTypeBooking, deleted.Id, "FCL")));
        Assert.Contains("已删除", deletedEx.Message);

        await AssertBusinessAsync(ErrorCodes.NotFound, () => controller.Create(
            Dto(ContainerShipmentReferenceRules.SourceTypeBooking, 999999L, "FCL")));

        await using var verify = OpenShared(databaseName);
        Assert.Equal(0, await verify.ContainerShipmentReferences.CountAsync());
        Assert.Equal(0, await verify.ContainerShipmentReferenceRevisions.CountAsync());

        // 已取消 / 已删除源记录不出现在候选（不能承载新的出运引用证据）
        var candidates = AssertOk<List<ContainerShipmentReferenceSourceCandidateDto>>(
            await controller.SourceCandidates(
                ContainerShipmentReferenceRules.SourceTypeBooking, null, 200));
        var candidate = Assert.Single(candidates);
        Assert.Equal(active.Id, candidate.SourceId);

        // 有效源记录仍可正常登记（历史证据不受影响）
        AssertOk<ContainerShipmentReferenceDto>(await controller.Create(
            Dto(ContainerShipmentReferenceRules.SourceTypeBooking, active.Id, "FCL")));
    }

    [Fact]
    public async Task 修订不允许改派源记录且不会改写原证据()
    {
        await using var db = TestDbFactory.Create();
        SeedCustomer(db, 900006, "本人客户");
        var bookingA = SeedBooking(db, "DG-A1", 900006);
        var bookingB = SeedBooking(db, "DG-B1", 900006);
        var controller = PrivilegedController(db);

        var row = AssertOk<ContainerShipmentReferenceDto>(await controller.Create(
            Dto(ContainerShipmentReferenceRules.SourceTypeBooking, bookingA.Id, "FCL")));

        await AssertBusinessAsync(ErrorCodes.InvalidParameter, () => controller.Update(row.Id,
            new ContainerShipmentReferenceUpdateDto
            {
                SourceType = ContainerShipmentReferenceRules.SourceTypeBooking,
                SourceId = bookingB.Id,
                Reason = "尝试改派",
                ShipmentMode = "LCL"
            }));

        var stored = await db.ContainerShipmentReferences.AsNoTracking().SingleAsync(o => o.Id == row.Id);
        Assert.Equal(bookingA.Id, stored.SourceId);
        Assert.Equal("FCL", stored.ShipmentMode);
        Assert.Equal(1, stored.RevisionNo);
        Assert.Equal(0, await db.ContainerShipmentReferenceRevisions.CountAsync());
    }

    // ==================== 3. 既有源模块菜单 + 权威客户数据范围 ====================

    [Fact]
    public async Task 受限账号只能读写本人客户的源记录与证据_台账详情候选写入均按范围收敛()
    {
        var databaseName = $"csr-scope-{Guid.NewGuid():N}";
        await using var db = OpenShared(databaseName);
        var (userId, employeeId) = SeedRestrictedOperator(
            db,
            ShipmentReferenceAuthorizationRules.BookingMenuCode,
            ShipmentReferenceAuthorizationRules.PreLoadingMenuCode);

        SeedCustomer(db, 901001, "本人客户", employeeId);
        SeedCustomer(db, 901002, "他人客户");
        var ownBooking = SeedBooking(db, "DG-OWN", 901001);
        var foreignBooking = SeedBooking(db, "DG-FOREIGN", 901002);
        var ownPreLoading = SeedPreLoading(db, "YZ-OWN", ownBooking.Id);
        var orphanPreLoading = SeedPreLoading(db, "YZ-ORPHAN", null);

        // 特权连接先登记两条证据（本人客户 / 他人客户）
        var seeder = PrivilegedController(db);
        var ownReference = AssertOk<ContainerShipmentReferenceDto>(await seeder.Create(
            Dto(ContainerShipmentReferenceRules.SourceTypeBooking, ownBooking.Id, "FCL")));
        var foreignReference = AssertOk<ContainerShipmentReferenceDto>(await seeder.Create(
            Dto(ContainerShipmentReferenceRules.SourceTypeBooking, foreignBooking.Id, "FCL")));

        var controller = ControllerFor(db, userId);

        // 台账：范围在 Count / 分页之前下推（受限账号只统计 / 只返回本人客户）
        var page = AssertOk<PagedResult<ContainerShipmentReferenceDto>>(
            await controller.GetPaged(new ContainerShipmentReferenceQuery { PageSize = 50 }));
        var listed = Assert.Single(page.Items);
        Assert.Equal(ownReference.Id, listed.Id);
        Assert.Equal(1, page.Total);

        // 详情 / 留痕：范围外 fail closed，不泄露任何证据
        var detail = AssertOk<ContainerShipmentReferenceDetailDto>(await controller.GetById(ownReference.Id));
        Assert.Equal(ownReference.Id, detail.Reference.Id);
        await AssertBusinessAsync(ErrorCodes.Forbidden, () => controller.GetById(foreignReference.Id));
        await AssertBusinessAsync(ErrorCodes.Forbidden, () => controller.Revisions(foreignReference.Id, 50));

        // 候选：只列本人客户的源记录；无权威客户归属的历史预装柜单对受限账号不可见
        var bookingCandidates = AssertOk<List<ContainerShipmentReferenceSourceCandidateDto>>(
            await controller.SourceCandidates(
                ContainerShipmentReferenceRules.SourceTypeBooking, null, 200));
        Assert.Equal(ownBooking.Id, Assert.Single(bookingCandidates).SourceId);

        var preLoadingCandidates = AssertOk<List<ContainerShipmentReferenceSourceCandidateDto>>(
            await controller.SourceCandidates(
                ContainerShipmentReferenceRules.SourceTypePreLoading, null, 200));
        Assert.Equal(ownPreLoading.Id, Assert.Single(preLoadingCandidates).SourceId);

        // 写入：本人客户的源记录可登记；范围外 / 无归属一律 fail closed
        var ownBooking2 = SeedBooking(db, "DG-OWN-2", 901001);
        AssertOk<ContainerShipmentReferenceDto>(await controller.Create(
            Dto(ContainerShipmentReferenceRules.SourceTypeBooking, ownBooking2.Id, "LCL")));
        await AssertBusinessAsync(ErrorCodes.Forbidden, () => controller.Create(
            Dto(ContainerShipmentReferenceRules.SourceTypeBooking, foreignBooking.Id, "LCL")));
        await AssertBusinessAsync(ErrorCodes.Forbidden, () => controller.Create(
            Dto(ContainerShipmentReferenceRules.SourceTypePreLoading, orphanPreLoading.Id, "LCL")));

        // 范围外的修订 / 作废同样 fail closed，且不改写已有证据
        await AssertBusinessAsync(ErrorCodes.Forbidden, () => controller.Update(foreignReference.Id,
            new ContainerShipmentReferenceUpdateDto
            {
                SourceType = ContainerShipmentReferenceRules.SourceTypeBooking,
                SourceId = foreignBooking.Id,
                Reason = "越范围修订",
                ShipmentMode = "LCL"
            }));
        await AssertBusinessAsync(ErrorCodes.Forbidden, () => controller.Void(
            foreignReference.Id, new ContainerShipmentReferenceVoidRequest { Reason = "越范围作废" }));

        await using var verify = OpenShared(databaseName);
        var foreignStored = await verify.ContainerShipmentReferences.AsNoTracking()
            .SingleAsync(o => o.Id == foreignReference.Id);
        Assert.Equal(ContainerShipmentReferenceRules.StatusRecorded, foreignStored.Status);
        Assert.Equal("FCL", foreignStored.ShipmentMode);
        Assert.Equal(1, foreignStored.RevisionNo);
    }

    [Fact]
    public async Task 受限账号缺少源模块菜单或身份异常时全部路由fail_closed()
    {
        var databaseName = $"csr-menu-{Guid.NewGuid():N}";
        await using var db = OpenShared(databaseName);
        SeedCustomer(db, 902001, "客户");

        // 只有「装柜清单」菜单，没有「订柜信息」菜单
        var (loadingListOnlyUser, _) = SeedRestrictedOperator(
            db, ShipmentReferenceAuthorizationRules.LoadingListMenuCode);
        // 完全没有既有源模块菜单
        var (noMenuUser, _) = SeedRestrictedOperator(db);

        var booking = SeedBooking(db, "DG-MENU", 902001);
        var reference = AssertOk<ContainerShipmentReferenceDto>(await PrivilegedController(db).Create(
            Dto(ContainerShipmentReferenceRules.SourceTypeBooking, booking.Id, "FCL")));

        // 无身份 / 非法身份 → 未认证（绝不猜身份、绝不降级为管理员）
        await AssertBusinessAsync(ErrorCodes.Unauthorized, () => ControllerFor(db, null)
            .GetPaged(new ContainerShipmentReferenceQuery()));
        await AssertBusinessAsync(ErrorCodes.Unauthorized, () => ControllerFor(db, 0)
            .GetPaged(new ContainerShipmentReferenceQuery()));
        await AssertBusinessAsync(ErrorCodes.Unauthorized, () => ControllerFor(db, 987654321L)
            .GetById(reference.Id));

        // 完全没有任何既有源模块菜单 → 权限不足
        await AssertBusinessAsync(ErrorCodes.Forbidden, () => ControllerFor(db, noMenuUser)
            .GetPaged(new ContainerShipmentReferenceQuery()));
        await AssertBusinessAsync(ErrorCodes.Forbidden, () => ControllerFor(db, noMenuUser)
            .CustomsBrokerOptions());

        // 只有装柜清单菜单：订柜信息相关路由（含读）fail closed，装柜清单路由可用
        var loadingListOnly = ControllerFor(db, loadingListOnlyUser);
        await AssertBusinessAsync(ErrorCodes.Forbidden, () => loadingListOnly.Create(
            Dto(ContainerShipmentReferenceRules.SourceTypeBooking, booking.Id, "LCL")));
        await AssertBusinessAsync(ErrorCodes.Forbidden, () => loadingListOnly.SourceCandidates(
            ContainerShipmentReferenceRules.SourceTypeBooking, null, 200));
        await AssertBusinessAsync(ErrorCodes.Forbidden, () => loadingListOnly.GetById(reference.Id));
        Assert.Empty(AssertOk<List<ContainerShipmentReferenceSourceCandidateDto>>(
            await loadingListOnly.SourceCandidates(
                ContainerShipmentReferenceRules.SourceTypeLoadingList, null, 200)));
    }

    [Fact]
    public async Task 禁用账号按权限不足_已删除账号按未认证()
    {
        var databaseName = $"csr-status-{Guid.NewGuid():N}";
        await using var db = OpenShared(databaseName);
        var (disabledUserId, _) = SeedRestrictedOperator(db, ShipmentReferenceAuthorizationRules.BookingMenuCode);
        var disabledUser = await db.SysUsers.FirstAsync(u => u.Id == disabledUserId);
        disabledUser.Status = UserStatus.Disabled;
        await db.SaveChangesAsync();

        var deletedUser = new SysUser
        {
            UserName = $"csr-del-{Guid.NewGuid():N}", PasswordHash = "hash", PasswordSalt = "salt",
            DisplayName = "已删除账号", Status = UserStatus.Enabled, IsDeleted = true
        };
        db.SysUsers.Add(deletedUser);
        db.SaveChanges();

        await AssertBusinessAsync(ErrorCodes.Forbidden, () => ControllerFor(db, disabledUserId)
            .GetPaged(new ContainerShipmentReferenceQuery()));
        await AssertBusinessAsync(ErrorCodes.Unauthorized, () => ControllerFor(db, deletedUser.Id)
            .GetPaged(new ContainerShipmentReferenceQuery()));
    }

    // ==================== 4. 串行化与授权接线契约 ====================

    [Fact]
    public void 控制器在可串行化事务内对源记录行与本引用行加锁()
    {
        var controller = File.ReadAllText(RepoFile(
            "src", "ERP.Api", "Controllers", "ContainerShipmentReferenceController.cs"));

        // 登记：源记录行锁 + 可序列化事务；修订 / 作废：本引用行锁 + 可序列化事务
        Assert.Contains("IsolationLevel.Serializable", controller);
        Assert.Contains("SELECT Id FROM db_owner.ContainerBookings WITH (UPDLOCK, HOLDLOCK) WHERE Id = {0}", controller);
        Assert.Contains("SELECT Id FROM db_owner.ContainerPreLoadings WITH (UPDLOCK, HOLDLOCK) WHERE Id = {0}", controller);
        Assert.Contains("SELECT Id FROM db_owner.ContainerLoadingLists WITH (UPDLOCK, HOLDLOCK) WHERE Id = {0}", controller);
        Assert.Contains("SELECT Id FROM db_owner.ContainerShipmentReferences WITH (UPDLOCK, HOLDLOCK) WHERE Id = {0}", controller);
        Assert.Contains("await transaction.CommitAsync();", controller);
        Assert.Contains("await transaction.RollbackAsync();", controller);
        Assert.Contains("IsRelational()", controller);

        // 每个路由都先授权
        Assert.Contains("ShipmentReferenceAuthorizationRules.EnsureAuthorizedAsync", controller);
        Assert.Contains("ShipmentReferenceAuthorizationRules.RequireSourceType", controller);

        // 只读写本模块两张表：加锁只 SELECT Id，不改写源记录任何列
        Assert.DoesNotContain("db_owner.ContainerBookings SET", controller);
        Assert.DoesNotContain("db_owner.ContainerPreLoadings SET", controller);
        Assert.DoesNotContain("db_owner.ContainerLoadingLists SET", controller);
    }

    [Fact]
    public void 授权规则复用既有权限模型_不新增授权不放宽特权()
    {
        var rules = File.ReadAllText(RepoFile(
            "src", "ERP.Application", "Services", "ShipmentReferenceAuthorizationRules.cs"));

        // 既有源模块菜单编码与既有模块同源，不自造权限
        Assert.Contains("PreLoadingBookingLinkRules.BookingRequiredMenuCode", rules);
        Assert.Contains("PreLoadingBookingLinkRules.RequiredMenuCode", rules);
        Assert.Contains("ContainerLoadingFulfillmentRules.RequiredMenuCode", rules);
        Assert.Contains("SalespersonDataScopeService.ResolveAsync", rules);
        Assert.Contains("CustomerReceivableReconciliationService", rules);

        // 不写库、不新增用户授权 / 权限模型、不调用外部系统
        Assert.DoesNotContain("SysUserRoles.Add", rules);
        Assert.DoesNotContain("SysRoleMenus.Add", rules);
        Assert.DoesNotContain("SysMenus.Add", rules);
        Assert.DoesNotContain("SaveChangesAsync", rules);
        Assert.DoesNotContain("HttpClient", rules);

        // 口径文案与错误码
        Assert.Contains("fail closed", rules);
        Assert.Contains("未映射为业务员", ShipmentReferenceAuthorizationRules.UnmappedOperatorText);
        Assert.Equal("booking", ShipmentReferenceAuthorizationRules.BookingMenuCode);
        Assert.Equal("pre-loading", ShipmentReferenceAuthorizationRules.PreLoadingMenuCode);
        Assert.Equal("loading-list", ShipmentReferenceAuthorizationRules.LoadingListMenuCode);
        Assert.Contains("绝不降级为全局 / 管理员可见", ShipmentReferenceAuthorizationRules.RuleText);
        Assert.Contains("不新增用户授权", ShipmentReferenceAuthorizationRules.BoundaryText);
        Assert.Equal(string.Empty, ShipmentReferenceAuthorizationRules.SourceMenuCode("customs"));
    }

    [Fact]
    public void 文档记录授权并发口径与专用测试目标()
    {
        var doc = File.ReadAllText(RepoFile("docs", "shipment-reference-concurrency.md"));
        Assert.Contains("UPDLOCK, HOLDLOCK", doc);
        Assert.Contains("Serializable", doc);
        Assert.Contains("(localdb)\\NEWERP_AutoAcceptance", doc);
        Assert.Contains("NEWERP_AUTOTEST", doc);
        Assert.Contains("绝不", doc);
        Assert.Contains("create / create", doc);
        Assert.Contains("update / void", doc);
        Assert.Contains("修订", doc);
    }
}
