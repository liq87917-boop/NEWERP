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
/// ERP-363 预装柜单实时授权 / 权威客户数据范围的**真实 SQL Server** 集成测试（GUID 独占 <c>NEWERP_AUTOTEST</c> 目标）。
/// <list type="number">
/// <item>真实控制器：受限制业务员只能读本人客户订柜下的预装柜单；未关联 / 来源缺失的「无主」单据详情与时间线 fail closed 且库中不变；</item>
/// <item>真实身份：禁用账号按权限不足、撤销既有「pre-loading」菜单后立即收敛为拒绝（不新增任何用户授权）；</item>
/// <item>失败编辑不改变库中来源 / 柜号 / 封条号 / 状态 / 明细（先校验后写入）；</item>
/// <item><b>两条独立连接竞争</b>：并发编辑不留撕裂状态、并发审核恰好一方成功，均由既有订柜行 + 预装柜单行锁
/// （<c>UPDLOCK, HOLDLOCK</c>）与可串行化事务串行化。</item>
/// </list>
/// <para>安全口径：目标必须为专用 localdb 实例 <c>NEWERP_AutoAcceptance</c>、库名前缀 <c>NEWERP_AUTOTEST</c> 且集成安全；
/// 每次运行只创建全新 GUID 后缀库，发现同名库已存在立即拒绝；绝不 drop / reset / 复用任何数据库，
/// 连接串只来自进程环境变量或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据。</para>
/// </summary>
public sealed class PreLoadingAuthorizationSqlServerTests
    : IClassFixture<PreLoadingAuthorizationSqlServerFixture>
{
    private readonly PreLoadingAuthorizationSqlServerFixture _fixture;

    public PreLoadingAuthorizationSqlServerTests(PreLoadingAuthorizationSqlServerFixture fixture)
        => _fixture = fixture;

    /// <summary>访问数据库前复核专用目标护栏（实例名 / 库名前缀 / 集成安全）。</summary>
    private void Guard()
    {
        var target = new SqlConnectionStringBuilder(_fixture.ConnectionString);
        Assert.Equal("(localdb)\\NEWERP_AutoAcceptance", target.DataSource, ignoreCase: true);
        Assert.StartsWith(PreLoadingAuthorizationSqlServerFixture.DatabasePrefix, target.InitialCatalog,
            StringComparison.OrdinalIgnoreCase);
        Assert.True(target.IntegratedSecurity);
    }

    // ==================== 1. 真实控制器：受限业务员只读本人客户 ====================

    [Fact]
    public async Task Live_restricted_operator_reads_only_own_customer_preloading()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var (userId, employeeId) = await SeedRestrictedOperatorAsync(db, withPreLoadingMenu: true);
        var ownCustomer = await SeedCustomerAsync(db, "本人客户", employeeId);
        var foreignCustomer = await SeedCustomerAsync(db, "他人客户", empId: null);
        var ownBooking = await SeedBookingAsync(db, ownCustomer, DocumentStatus.Approved);
        var foreignBooking = await SeedBookingAsync(db, foreignCustomer, DocumentStatus.Approved);

        var keyword = Tag();
        var own = await SeedPreLoadingAsync(db, $"YZ-{keyword}-OWN", ownBooking.Id, DocumentStatus.Pending, "CTN-OWN");
        var foreign = await SeedPreLoadingAsync(db, $"YZ-{keyword}-OTHER", foreignBooking.Id, DocumentStatus.Pending, "CTN-OTHER");
        var unlinked = await SeedPreLoadingAsync(db, $"YZ-{keyword}-UNLINK", null, DocumentStatus.Pending, "CTN-UNLINK");
        var dangling = await SeedPreLoadingAsync(db, $"YZ-{keyword}-DANGLING", 999_999_999L, DocumentStatus.Pending, "CTN-DANGLING");

        var ctl = NewController(db, userId);

        var page = AssertOk<PagedResult<ContainerPreLoading>>(
            await ctl.GetPaged(new PageQuery { Page = 1, PageSize = 50, Keyword = keyword }, null));
        var row = Assert.Single(page.Items);
        Assert.Equal(own.Id, row.Id);
        Assert.Equal(1, page.Total); // 计数发生在数据库侧范围过滤之后

        Assert.Equal(own.Id, AssertOk<ContainerPreLoading>(await ctl.GetById(own.Id)).Id);
        await AssertCode(ErrorCodes.Forbidden, () => ctl.GetById(foreign.Id));
        await AssertCode(ErrorCodes.Forbidden, () => ctl.GetById(unlinked.Id));
        await AssertCode(ErrorCodes.Forbidden, () => ctl.GetById(dangling.Id));
        await AssertCode(ErrorCodes.Forbidden, () => ctl.GetShipmentTimeline(unlinked.Id));
    }

    // ==================== 1B. ERP-431：出运跟踪路由实时授权（与出运时间线同口径） ====================

    [Fact]
    public async Task Live_shipment_tracking_enforces_identity_menu_and_customer_scope()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var (userId, employeeId) = await SeedRestrictedOperatorAsync(db, withPreLoadingMenu: true);
        var ownCustomer = await SeedCustomerAsync(db, "本人客户", employeeId);
        var foreignCustomer = await SeedCustomerAsync(db, "他人客户", empId: null);
        var ownBooking = await SeedBookingAsync(db, ownCustomer, DocumentStatus.Approved);
        var foreignBooking = await SeedBookingAsync(db, foreignCustomer, DocumentStatus.Approved);

        var keyword = Tag();
        var own = await SeedPreLoadingAsync(db, $"YZ-{keyword}-TROWN", ownBooking.Id, DocumentStatus.Pending, "CTN-TROWN");
        var foreign = await SeedPreLoadingAsync(db, $"YZ-{keyword}-TROTHER", foreignBooking.Id, DocumentStatus.Pending, "CTN-TROTHER");
        var unlinked = await SeedPreLoadingAsync(db, $"YZ-{keyword}-TRUNLINK", null, DocumentStatus.Pending, "CTN-TRUNLINK");
        var deleted = await SeedPreLoadingAsync(db, $"YZ-{keyword}-TRDEL", ownBooking.Id, DocumentStatus.Pending, "CTN-TRDEL");
        deleted.IsDeleted = true;
        await db.SaveChangesAsync();

        var beforePreLoadings = await db.ContainerPreLoadings.AsNoTracking().CountAsync();
        var beforeBookings = await db.ContainerBookings.AsNoTracking().CountAsync();
        var beforeReferences = await db.ContainerShipmentReferences.AsNoTracking().CountAsync();
        var beforeMilestones = await db.ContainerShipmentMilestones.AsNoTracking().CountAsync();

        var ctl = NewController(db, userId);

        // 本人客户：按持久化订柜引用返回权威跟踪值（只读，DTO 语义不变）。
        var tracking = AssertOk<ContainerShipmentTrackingDto>(await ctl.GetShipmentTracking(own.Id));
        Assert.True(tracking.Linked);
        Assert.Equal(ownBooking.Id, tracking.BookingId);

        // 他人 / 无主单据与出运时间线同口径拒绝；已删除单据返回受控未找到。
        await AssertCode(ErrorCodes.Forbidden, () => ctl.GetShipmentTracking(foreign.Id));
        await AssertCode(ErrorCodes.Forbidden, () => ctl.GetShipmentTracking(unlinked.Id));
        await AssertCode(ErrorCodes.Forbidden, () => ctl.GetShipmentTimeline(foreign.Id));
        await AssertCode(ErrorCodes.NotFound, () => ctl.GetShipmentTracking(deleted.Id));

        var disabledUserId = await SeedDisabledUserAsync(db);
        await AssertCode(ErrorCodes.Forbidden, () => NewController(db, disabledUserId).GetShipmentTracking(own.Id));
        await AssertCode(ErrorCodes.Unauthorized, () => NewController(db, null).GetShipmentTracking(own.Id));

        // 拒绝 / 未找到不改变任何行：预装柜 / 订柜 / 出运引用 / 里程碑保持原样。
        await using var verify = _fixture.CreateDbContext();
        Assert.Equal(beforePreLoadings, await verify.ContainerPreLoadings.AsNoTracking().CountAsync());
        Assert.Equal(beforeBookings, await verify.ContainerBookings.AsNoTracking().CountAsync());
        Assert.Equal(beforeReferences, await verify.ContainerShipmentReferences.AsNoTracking().CountAsync());
        Assert.Equal(beforeMilestones, await verify.ContainerShipmentMilestones.AsNoTracking().CountAsync());

        var storedForeign = await verify.ContainerPreLoadings.AsNoTracking().SingleAsync(p => p.Id == foreign.Id);
        Assert.Equal("CTN-TROTHER", storedForeign.ContainerNo);
        Assert.Equal(foreignBooking.Id, storedForeign.BookingId);
        var storedUnlinked = await verify.ContainerPreLoadings.AsNoTracking().SingleAsync(p => p.Id == unlinked.Id);
        Assert.Null(storedUnlinked.BookingId);
        Assert.True((await verify.ContainerPreLoadings.AsNoTracking().SingleAsync(p => p.Id == deleted.Id)).IsDeleted);
    }

    // ==================== 2. 受限业务员不能写他人 / 无主单据 ====================

    [Fact]
    public async Task Live_restricted_operator_cannot_edit_or_transition_foreign_or_unlinked()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var (userId, employeeId) = await SeedRestrictedOperatorAsync(db, withPreLoadingMenu: true);
        await SeedCustomerAsync(db, "本人客户", employeeId);
        var foreignCustomer = await SeedCustomerAsync(db, "他人客户", empId: null);
        var foreignBooking = await SeedBookingAsync(db, foreignCustomer, DocumentStatus.Approved);

        var keyword = Tag();
        var foreign = await SeedPreLoadingAsync(db, $"YZ-{keyword}-F", foreignBooking.Id, DocumentStatus.Pending, "CTN-F");
        var unlinked = await SeedPreLoadingAsync(db, $"YZ-{keyword}-U", null, DocumentStatus.Submitted, "CTN-U");
        var ctl = NewController(db, userId);

        await AssertCode(ErrorCodes.Forbidden, () => ctl.Update(foreign.Id, NewPreLoading(foreignBooking.Id, "CTN-HACK")));
        await AssertCode(ErrorCodes.Forbidden, () => ctl.Submit(foreign.Id));
        await AssertCode(ErrorCodes.Forbidden, () => ctl.Approve(unlinked.Id));
        await AssertCode(ErrorCodes.Forbidden, () => ctl.Cancel(unlinked.Id));
        await AssertCode(ErrorCodes.Forbidden, () => ctl.Delete(foreign.Id));

        db.ChangeTracker.Clear();
        var storedForeign = await db.ContainerPreLoadings.AsNoTracking().SingleAsync(p => p.Id == foreign.Id);
        var storedUnlinked = await db.ContainerPreLoadings.AsNoTracking().SingleAsync(p => p.Id == unlinked.Id);
        Assert.Equal(DocumentStatus.Pending, storedForeign.Status);
        Assert.False(storedForeign.IsDeleted);
        Assert.Equal("CTN-F", storedForeign.ContainerNo);
        Assert.Equal(foreignBooking.Id, storedForeign.BookingId);
        Assert.Equal(DocumentStatus.Submitted, storedUnlinked.Status);
    }

    // ==================== 3. 禁用身份 / 撤销菜单 ====================

    [Fact]
    public async Task Live_disabled_user_and_revoked_preloading_menu_fail_closed()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var (userId, employeeId, roleId) = await SeedRestrictedOperatorWithRoleAsync(db);
        var customerId = await SeedCustomerAsync(db, "撤销菜单客户", employeeId);
        var booking = await SeedBookingAsync(db, customerId, DocumentStatus.Approved);
        var keyword = Tag();
        var entity = await SeedPreLoadingAsync(db, $"YZ-{keyword}-MENU", booking.Id, DocumentStatus.Pending, "CTN-MENU");

        var grants = await db.SysRoleMenus.Where(rm => rm.RoleId == roleId && !rm.IsDeleted).ToListAsync();
        Assert.NotEmpty(grants);
        foreach (var grant in grants) grant.IsDeleted = true;
        await db.SaveChangesAsync();

        await AssertCode(ErrorCodes.Forbidden, () => NewController(db, userId).GetPaged(new PageQuery(), null));
        await AssertCode(ErrorCodes.Forbidden, () => NewController(db, userId).Delete(entity.Id));
        await AssertCode(ErrorCodes.Forbidden, () => NewController(db, userId).GetShipmentTracking(entity.Id));

        var disabledUserId = await SeedDisabledUserAsync(db);
        await AssertCode(ErrorCodes.Forbidden, () => NewController(db, disabledUserId).GetPaged(new PageQuery(), null));
        await AssertCode(ErrorCodes.Forbidden, () => NewController(db, disabledUserId).Delete(entity.Id));
        await AssertCode(ErrorCodes.Forbidden, () => NewController(db, disabledUserId).GetShipmentTracking(entity.Id));

        db.ChangeTracker.Clear();
        var stored = await db.ContainerPreLoadings.AsNoTracking().SingleAsync(p => p.Id == entity.Id);
        Assert.False(stored.IsDeleted);
        Assert.Equal(DocumentStatus.Pending, stored.Status);
    }

    // ==================== 4. 失败编辑不改动库中来源 / 明细 / 状态 / 审计 ====================

    [Fact]
    public async Task Live_failed_edit_preserves_original_detail_status_and_audit()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var (userId, employeeId) = await SeedRestrictedOperatorAsync(db, withPreLoadingMenu: true);
        var ownCustomer = await SeedCustomerAsync(db, "本人客户", employeeId);
        var foreignCustomer = await SeedCustomerAsync(db, "他人客户", empId: null);
        var ownBooking = await SeedBookingAsync(db, ownCustomer, DocumentStatus.Approved);
        var foreignBooking = await SeedBookingAsync(db, foreignCustomer, DocumentStatus.Approved);
        var productId = await SeedProductAsync(db);

        var keyword = Tag();
        var entity = await SeedPreLoadingAsync(db, $"YZ-{keyword}-EDIT", ownBooking.Id, DocumentStatus.Pending,
            "CTN-EDIT", (productId, 6m));
        var before = await db.ContainerPreLoadings.AsNoTracking().Include(p => p.Details)
            .SingleAsync(p => p.Id == entity.Id);
        var ctl = NewController(db, userId);

        await AssertCode(ErrorCodes.Forbidden, () => ctl.Update(entity.Id,
            NewPreLoading(foreignBooking.Id, "CTN-MOVE", (productId, 99m))));
        await AssertCode(ErrorCodes.Forbidden, () => ctl.Update(entity.Id, NewPreLoading(null, "CTN-CLEAR", (productId, 99m))));

        db.ChangeTracker.Clear();
        var after = await db.ContainerPreLoadings.AsNoTracking().Include(p => p.Details)
            .SingleAsync(p => p.Id == entity.Id);
        Assert.Equal(before.BookingId, after.BookingId);
        Assert.Equal(before.ContainerNo, after.ContainerNo);
        Assert.Equal(before.SealNo, after.SealNo);
        Assert.Equal(before.Status, after.Status);
        Assert.Equal(before.UpdatedAt, after.UpdatedAt);              // 审计时间戳不被改写
        Assert.Equal(before.Details.Single().ProductId, after.Details.Single().ProductId);
        Assert.Equal(before.Details.Single().Quantity, after.Details.Single().Quantity);
    }

    // ==================== 5. 两条独立连接的并发编辑 / 审核 ====================

    [Fact]
    public async Task Two_connection_concurrent_edits_leave_no_torn_state()
    {
        Guard();
        long preLoadingId;
        long bookingId;
        long productId;
        long adminId;
        await using (var db = _fixture.CreateDbContext())
        {
            adminId = await ResolveSeededAdminIdAsync(db);
            var customerId = await SeedCustomerAsync(db, "并发编辑客户");
            bookingId = (await SeedBookingAsync(db, customerId, DocumentStatus.Approved)).Id;
            productId = await SeedProductAsync(db);
            var keyword = Tag();
            preLoadingId = (await SeedPreLoadingAsync(db, $"YZ-{keyword}-RACE", bookingId,
                DocumentStatus.Pending, "CTN-RACE")).Id;
        }

        var results = await RaceAsync(
            () => TryUpdateAsync(preLoadingId, bookingId, "CTN-A", productId, 5m, adminId),
            () => TryUpdateAsync(preLoadingId, bookingId, "CTN-B", productId, 9m, adminId));

        Assert.True(results.Count(r => r.Success) >= 1);

        await using var verify = _fixture.CreateDbContext();
        var stored = await verify.ContainerPreLoadings.AsNoTracking().Include(p => p.Details)
            .SingleAsync(p => p.Id == preLoadingId);
        // 行锁 + 提交后整单替换：最终柜号与明细必须来自同一次编辑，绝不出现 A 柜号配 B 明细的撕裂状态。
        if (stored.ContainerNo == "CTN-A")
        {
            Assert.Equal(5m, stored.Details.Single().Quantity);
        }
        else
        {
            Assert.Equal("CTN-B", stored.ContainerNo);
            Assert.Equal(9m, stored.Details.Single().Quantity);
        }
    }

    [Fact]
    public async Task Two_connection_double_approve_allows_exactly_one_winner()
    {
        Guard();
        long preLoadingId;
        long adminId;
        await using (var db = _fixture.CreateDbContext())
        {
            adminId = await ResolveSeededAdminIdAsync(db);
            var customerId = await SeedCustomerAsync(db, "并发审核客户");
            var bookingId = (await SeedBookingAsync(db, customerId, DocumentStatus.Approved)).Id;
            var keyword = Tag();
            preLoadingId = (await SeedPreLoadingAsync(db, $"YZ-{keyword}-APPROVE", bookingId,
                DocumentStatus.Submitted, "CTN-APPROVE")).Id;
        }

        var results = await RaceAsync(
            () => TryApproveAsync(preLoadingId, adminId),
            () => TryApproveAsync(preLoadingId, adminId));

        Assert.Equal(1, results.Count(r => r.Success));

        await using var verify = _fixture.CreateDbContext();
        var stored = await verify.ContainerPreLoadings.AsNoTracking().SingleAsync(p => p.Id == preLoadingId);
        Assert.Equal(DocumentStatus.Approved, stored.Status);
        Assert.False(stored.IsDeleted);
    }

    // ==================== 控制器工厂 / 并发脚手架 ====================

    private static ContainerPreLoadingController NewController(ErpDbContext db, long? userId)
    {
        var ctl = new ContainerPreLoadingController(db, new DocumentNumberService(db));
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

    /// <summary>两条独立连接在同一栅栏后同时发起编辑 / 审核（各自独立 DbContext / 连接 / 事务）。</summary>
    private static async Task<List<(bool Success, string Error)>> RaceAsync(
        Func<Task<(bool Success, string Error)>> first,
        Func<Task<(bool Success, string Error)>> second)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<(bool Success, string Error)> Run(Func<Task<(bool Success, string Error)>> action)
        {
            await gate.Task;
            return await action();
        }

        var left = Run(first);
        var right = Run(second);
        gate.SetResult();
        return (await Task.WhenAll(left, right)).ToList();
    }

    private async Task<(bool Success, string Error)> TryUpdateAsync(
        long id, long bookingId, string containerNo, long productId, decimal quantity, long userId)
        => await TryAsync(async ctl => await ctl.Update(id,
            NewPreLoading(bookingId, containerNo, (productId, quantity))), userId);

    private async Task<(bool Success, string Error)> TryApproveAsync(long id, long userId)
        => await TryAsync(async ctl => await ctl.Approve(id), userId);

    private async Task<(bool Success, string Error)> TryAsync(
        Func<ContainerPreLoadingController, Task<IActionResult>> action, long userId)
    {
        await using var db = _fixture.CreateDbContext();
        try
        {
            var result = await action(NewController(db, userId));
            return (result is OkObjectResult, string.Empty);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static ContainerPreLoading NewPreLoading(long? bookingId, string containerNo,
        params (long ProductId, decimal Quantity)[] lines)
    {
        var entity = new ContainerPreLoading
        {
            BookingId = bookingId, ContainerNo = containerNo, SealNo = "SEAL-RACE", LoadingDate = DateTime.Today
        };
        foreach (var (productId, quantity) in lines)
        {
            entity.Details.Add(new ContainerPreLoadingDetail
            {
                ProductId = productId, ProductName = "race", Quantity = quantity
            });
        }
        return entity;
    }

    // ==================== 种子数据（SQL 自增主键，不显式指定 Id） ====================

    private static string Tag() => Guid.NewGuid().ToString("N")[..10];

    private static async Task<long> SeedCustomerAsync(ErpDbContext db, string name,
        long? empId = null, int status = 1)
    {
        var customer = new BaseCustomer
        {
            CustomerCode = $"PL-C-{Guid.NewGuid():N}"[..30], CustomerName = name, EmpId = empId, Status = status
        };
        db.BaseCustomers.Add(customer);
        await db.SaveChangesAsync();
        return customer.Id;
    }

    private static async Task<ContainerBooking> SeedBookingAsync(ErpDbContext db, long customerId,
        DocumentStatus status)
    {
        var booking = new ContainerBooking
        {
            BookingNo = $"PL-B-{Guid.NewGuid():N}"[..30], BookingDate = DateTime.Today, CustomerId = customerId,
            Status = status, Remark = "ERP-363_INT"
        };
        db.ContainerBookings.Add(booking);
        await db.SaveChangesAsync();
        return booking;
    }

    private static async Task<ContainerPreLoading> SeedPreLoadingAsync(ErpDbContext db, string no, long? bookingId,
        DocumentStatus status, string containerNo, params (long ProductId, decimal Quantity)[] lines)
    {
        var pre = new ContainerPreLoading
        {
            PreLoadingNo = no.Length > 50 ? no[..50] : no, LoadingDate = DateTime.Today, BookingId = bookingId,
            ContainerNo = containerNo, SealNo = "SEAL-INT", Status = status
        };
        db.ContainerPreLoadings.Add(pre);
        await db.SaveChangesAsync();
        foreach (var (productId, quantity) in lines)
        {
            db.ContainerPreLoadingDetails.Add(new ContainerPreLoadingDetail
            {
                PreLoadingId = pre.Id, ProductId = productId, ProductName = "集成商品", Quantity = quantity
            });
        }
        await db.SaveChangesAsync();
        return pre;
    }

    private static async Task<long> SeedProductAsync(ErpDbContext db)
    {
        var product = new BaseProduct
        {
            ProductCode = $"PL-P-{Guid.NewGuid():N}"[..30], ProductName = "集成商品", Spec = "规格A", Unit = "PCS"
        };
        db.BaseProducts.Add(product);
        await db.SaveChangesAsync();
        return product.Id;
    }

    private static async Task<(long UserId, long EmployeeId)> SeedRestrictedOperatorAsync(
        ErpDbContext db, bool withPreLoadingMenu)
    {
        var (userId, employeeId, roleId) = await SeedRestrictedOperatorWithRoleAsync(db);
        if (!withPreLoadingMenu)
        {
            var grants = await db.SysRoleMenus.Where(rm => rm.RoleId == roleId && !rm.IsDeleted).ToListAsync();
            foreach (var grant in grants) grant.IsDeleted = true;
            await db.SaveChangesAsync();
        }
        return (userId, employeeId);
    }

    /// <summary>播种受限制的预装柜操作员：业务员映射 + 既有「pre-loading」菜单（复用 SeedData 菜单，不新增权限模型）。</summary>
    private static async Task<(long UserId, long EmployeeId, long RoleId)> SeedRestrictedOperatorWithRoleAsync(
        ErpDbContext db)
    {
        var code = $"pl-op-{Guid.NewGuid():N}";
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

        var role = new SysRole { RoleName = "预装柜操作员", RoleCode = $"PlOp-{Guid.NewGuid():N}", IsSystem = false };
        db.SysRoles.Add(role);
        await db.SaveChangesAsync();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        await db.SaveChangesAsync();

        var menuId = await db.SysMenus.AsNoTracking()
            .Where(m => m.MenuCode == PreLoadingAuthorizationRules.RequiredMenuCode && !m.IsDeleted)
            .Select(m => m.Id)
            .FirstAsync();
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menuId });
        await db.SaveChangesAsync();

        return (user.Id, employee.Id, role.Id);
    }

    private static async Task<long> SeedDisabledUserAsync(ErpDbContext db)
    {
        var user = new SysUser
        {
            UserName = $"pl-disabled-{Guid.NewGuid():N}", DisplayName = "禁用账号",
            PasswordHash = "hash", PasswordSalt = "salt", Status = UserStatus.Disabled
        };
        db.SysUsers.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    private static Task<long> ResolveSeededAdminIdAsync(ErpDbContext db)
        => db.SysUsers.AsNoTracking()
            .Where(u => u.UserName == SeedData.AdminUserName)
            .Select(u => u.Id)
            .FirstAsync();

    // ==================== 断言脚手架 ====================

    private static async Task AssertCode(int expected, Func<Task<IActionResult>> action)
    {
        var ex = await Assert.ThrowsAsync<BusinessException>(async () => await action());
        Assert.Equal(expected, ex.Code);
    }

    private static ApiResponse<T> AssertOkResponse<T>(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<ApiResponse<T>>(ok.Value);
        Assert.Equal(ErrorCodes.Success, resp.Code);
        return resp;
    }

    private static T AssertOk<T>(IActionResult result)
    {
        var resp = AssertOkResponse<T>(result);
        Assert.NotNull(resp.Data);
        return resp.Data!;
    }
}

/// <summary>
/// 专用 localdb 目标 Fixture（ERP-363）：把目标库初始化为完整 NEWERP 结构 + 种子数据，供真实 SQL Server 集成测试复用。
/// <para>安全口径：库名一律带本次运行的 GUID 后缀（绝不复用 / 绝不 drop 既有库）；任何数据库访问之前先校验
/// 实例名、库名前缀与集成安全，连接串只来自进程环境变量或专用 localdb 默认值。</para>
/// </summary>
public sealed class PreLoadingAuthorizationSqlServerFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";
    public const string DefaultDatabaseName = "NEWERP_AUTOTEST_PRELOADINGAUTHORIZATION_20261008";

    public string ConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = BuildDefaultConnectionString();

        AssertDedicatedTarget(connectionString);
        ConnectionString = connectionString;

        Console.WriteLine($"[ERP-363] 目标库护栏放行（实例含 {InstanceMarker}，库名前缀 {DatabasePrefix}）。");

        await EnsureFreshDatabaseAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public ErpDbContext CreateDbContext() => new(BuildOptions());

    private DbContextOptions<ErpDbContext> BuildOptions()
        => new DbContextOptionsBuilder<ErpDbContext>().UseSqlServer(ConnectionString).Options;

    private static string BuildDefaultConnectionString()
        => $"Server=(localdb)\\{InstanceMarker};Initial Catalog={DefaultDatabaseName}_{Guid.NewGuid():N};Integrated Security=true;TrustServerCertificate=true;";

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
            // Never destroy a pre-existing fixture or another caller's database.
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

        Console.WriteLine("[ERP-363] 集成场景就绪：完整 NEWERP 结构 + 种子数据。");
    }
}

/// <summary>目标库护栏单元级校验：非专用目标必须在访问数据库之前被拒绝。</summary>
public sealed class PreLoadingAuthorizationTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;User Id=sa;Password=x")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(() => PreLoadingAuthorizationSqlServerFixture.AssertDedicatedTarget(connection));

    [Fact]
    public void Accepts_the_dedicated_localdb_target_with_integrated_security()
        => PreLoadingAuthorizationSqlServerFixture.AssertDedicatedTarget(
            "Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_X;Integrated Security=true");
}
