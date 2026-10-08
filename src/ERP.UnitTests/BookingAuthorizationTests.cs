using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-360 订柜信息实时授权 / 客户数据范围 / 主数据可用性护栏单元测试。
/// <para>覆盖：列表 / 详情 / 出运时间线 / 报关行选项 / 新增 / 修改 / 提交 / 审核 / 取消 / 删除<b>每一个</b>路由的
/// 身份（缺失 / 已删除 / 禁用）与既有「订柜信息」菜单授权 fail closed；未映射业务员的受限账号不降级为全局可见；
/// 受限制业务员只能读写本人客户、列表在计数前按数据库侧范围过滤；新增 / 修改 / 状态变更前客户（必填）与供应商
/// （可选）必须真实可用、修改不能把订柜信息移入 / 移出当前账号的客户范围；历史读取在主数据停用 / 删除时照常可读
/// 并给出显式不可用证据；规则为纯只读判定、控制器全部路由先授权。</para>
/// <para>全部使用内存数据库（TestDbFactory），不连接 SQL Server、不启动 API。</para>
/// </summary>
public class BookingAuthorizationTests
{
    private const long CustomerOwn = 963001L;
    private const long CustomerOther = 963002L;
    private const long SupplierOk = 963101L;
    private const long SupplierOff = 963102L;
    private const long SupplierDeleted = 963103L;

    // ==================== 1. 身份 / 账号状态 / 菜单 fail closed ====================

    [Fact]
    public async Task 每个路由_无身份_一律未认证拒绝且不落任何变更()
    {
        using var db = TestDbFactory.Create();
        SeedCustomer(db, CustomerOwn, "本人客户");
        var booking = SeedBooking(db, "BA-ANON");
        var ctl = NewController(db, userId: null);

        await AssertCode(ErrorCodes.Unauthorized, () => ctl.GetPaged(new PageQuery { Page = 1, PageSize = 10 }, null));
        await AssertCode(ErrorCodes.Unauthorized, () => ctl.GetById(booking.Id));
        await AssertCode(ErrorCodes.Unauthorized, () => ctl.GetShipmentTimeline(booking.Id));
        await AssertCode(ErrorCodes.Unauthorized, () => ctl.GetCustomsBrokerOptions());
        await AssertCode(ErrorCodes.Unauthorized, () => ctl.Create(NewBooking()));
        await AssertCode(ErrorCodes.Unauthorized, () => ctl.Update(booking.Id, NewBooking()));
        await AssertCode(ErrorCodes.Unauthorized, () => ctl.Submit(booking.Id));
        await AssertCode(ErrorCodes.Unauthorized, () => ctl.Approve(booking.Id));
        await AssertCode(ErrorCodes.Unauthorized, () => ctl.Cancel(booking.Id));
        await AssertCode(ErrorCodes.Unauthorized, () => ctl.Delete(booking.Id));

        Assert.Single(db.ContainerBookings);
        var stored = db.ContainerBookings.Single();
        Assert.Equal(DocumentStatus.Pending, stored.Status);
        Assert.False(stored.IsDeleted);
    }

    [Fact]
    public async Task 禁用账号_权限不足_已删除账号_按未认证拒绝()
    {
        using var db = TestDbFactory.Create();
        SeedCustomer(db, CustomerOwn, "本人客户");
        var booking = SeedBooking(db, "BA-STATUS");
        var disabled = SeedUser(db, UserStatus.Disabled);
        var deleted = SeedUser(db, UserStatus.Enabled, deleted: true);

        await AssertCode(ErrorCodes.Forbidden, () => NewController(db, disabled).GetPaged(new PageQuery(), null));
        await AssertCode(ErrorCodes.Forbidden, () => NewController(db, disabled).Delete(booking.Id));
        await AssertCode(ErrorCodes.Unauthorized, () => NewController(db, deleted).GetPaged(new PageQuery(), null));
        await AssertCode(ErrorCodes.Unauthorized, () => NewController(db, deleted).Delete(booking.Id));

        Assert.False(db.ContainerBookings.Single().IsDeleted);
    }

    [Fact]
    public async Task 非特权_无既有订柜信息菜单_拒绝且文案指出模块授权()
    {
        using var db = TestDbFactory.Create();
        SeedCustomer(db, CustomerOwn, "本人客户");
        var booking = SeedBooking(db, "BA-NOMENU");
        var noMenuUser = SeedMenuUser(db);   // 普通角色 + 未映射员工 + 未授予 booking 菜单

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => NewController(db, noMenuUser).GetPaged(new PageQuery(), null));

        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
        Assert.Contains("模块授权", ex.Message);
        Assert.False(db.ContainerBookings.Single(b => b.Id == booking.Id).IsDeleted);
    }

    [Fact]
    public async Task 非特权_有菜单但未映射业务员_fail_closed不降级为全局可见()
    {
        using var db = TestDbFactory.Create();
        SeedCustomer(db, CustomerOwn, "本人客户");
        SeedBooking(db, "BA-UNMAPPED");
        var user = SeedMenuUser(db, grantBookingMenu: true);

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => NewController(db, user).GetPaged(new PageQuery(), null));

        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
        Assert.Contains("未映射为业务员", ex.Message);
    }

    // ==================== 2. 数据范围：读写与数据库侧过滤 ====================

    [Fact]
    public async Task 受限制业务员_列表只返回本人客户且计数在范围过滤之后()
    {
        using var db = TestDbFactory.Create();
        var (userId, employeeId) = SeedRestrictedOperator(db, grantBookingMenu: true);
        SeedCustomer(db, CustomerOwn, "本人客户", empId: employeeId);
        SeedCustomer(db, CustomerOther, "他人客户");
        SeedBooking(db, "BA-OWN", CustomerOwn);
        SeedBooking(db, "BA-OTHER", CustomerOther);

        var page = AssertOk<PagedResult<ContainerBooking>>(
            await NewController(db, userId).GetPaged(new PageQuery { Page = 1, PageSize = 10 }, null));

        Assert.Equal(1, page.Total);
        Assert.Equal("BA-OWN", Assert.Single(page.Items).BookingNo);
    }

    [Fact]
    public async Task 受限制业务员_范围外详情与时间线一律拒绝()
    {
        using var db = TestDbFactory.Create();
        var (userId, employeeId) = SeedRestrictedOperator(db, grantBookingMenu: true);
        SeedCustomer(db, CustomerOwn, "本人客户", empId: employeeId);
        SeedCustomer(db, CustomerOther, "他人客户");
        var own = SeedBooking(db, "BA-OWN-2", CustomerOwn);
        var foreign = SeedBooking(db, "BA-FOREIGN", CustomerOther);
        var ctl = NewController(db, userId);

        AssertOk<ContainerBooking>(await ctl.GetById(own.Id));
        await AssertCode(ErrorCodes.Forbidden, () => ctl.GetById(foreign.Id));
        await AssertCode(ErrorCodes.Forbidden, () => ctl.GetShipmentTimeline(foreign.Id));
    }

    [Fact]
    public async Task 受限制业务员_范围外写路由一律拒绝且状态不变()
    {
        using var db = TestDbFactory.Create();
        var (userId, employeeId) = SeedRestrictedOperator(db, grantBookingMenu: true);
        SeedCustomer(db, CustomerOwn, "本人客户", empId: employeeId);
        SeedCustomer(db, CustomerOther, "他人客户");
        var pending = SeedBooking(db, "BA-FOR-P", CustomerOther, DocumentStatus.Pending);
        var submitted = SeedBooking(db, "BA-FOR-S", CustomerOther, DocumentStatus.Submitted);
        var ctl = NewController(db, userId);

        await AssertCode(ErrorCodes.Forbidden, () => ctl.Update(pending.Id, NewBooking(CustomerOther)));
        await AssertCode(ErrorCodes.Forbidden, () => ctl.Submit(pending.Id));
        await AssertCode(ErrorCodes.Forbidden, () => ctl.Approve(submitted.Id));
        await AssertCode(ErrorCodes.Forbidden, () => ctl.Cancel(pending.Id));
        await AssertCode(ErrorCodes.Forbidden, () => ctl.Delete(pending.Id));

        db.ChangeTracker.Clear();
        Assert.Equal(DocumentStatus.Pending, db.ContainerBookings.Single(b => b.Id == pending.Id).Status);
        Assert.Equal(DocumentStatus.Submitted, db.ContainerBookings.Single(b => b.Id == submitted.Id).Status);
        Assert.False(db.ContainerBookings.Single(b => b.Id == pending.Id).IsDeleted);
    }

    // ==================== 3. 新增 / 修改：客户与供应商实时主数据 ====================

    [Fact]
    public async Task 新增_客户必须真实可用_否则拒绝且不占单据号不落库()
    {
        using var db = TestDbFactory.Create();
        var privileged = TestAuth.SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerOther, "停用客户", status: 0);
        SeedCustomer(db, 963003L, "已删除客户", deleted: true);
        var ctl = NewController(db, privileged);

        await AssertCode(ErrorCodes.InvalidParameter, () => ctl.Create(NewBooking(customerId: 0)));
        await AssertCode(ErrorCodes.NotFound, () => ctl.Create(NewBooking(customerId: 999999L)));
        await AssertCode(ErrorCodes.NotFound, () => ctl.Create(NewBooking(customerId: 963003L)));
        await AssertCode(ErrorCodes.RuleConflict, () => ctl.Create(NewBooking(customerId: CustomerOther)));

        Assert.Empty(db.ContainerBookings);
    }

    [Fact]
    public async Task 新增_可选供应商_填写时必须真实可用_留空放行()
    {
        using var db = TestDbFactory.Create();
        var privileged = TestAuth.SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerOwn, "本人客户");
        SeedSupplier(db, SupplierOff, "停用供应商", status: 0);
        SeedSupplier(db, SupplierDeleted, "已删除供应商", deleted: true);
        SeedSupplier(db, SupplierOk, "可用供应商");
        var ctl = NewController(db, privileged);

        await AssertCode(ErrorCodes.RuleConflict, () => ctl.Create(NewBooking(supplierId: SupplierOff)));
        await AssertCode(ErrorCodes.NotFound, () => ctl.Create(NewBooking(supplierId: SupplierDeleted)));
        Assert.Empty(db.ContainerBookings);

        AssertOkObject(await ctl.Create(NewBooking(supplierId: SupplierOk)));
        AssertOkObject(await ctl.Create(NewBooking(supplierId: null)));
        var stored = db.ContainerBookings.OrderBy(b => b.Id).ToList();
        Assert.Equal(2, stored.Count);
        Assert.Equal(SupplierOk, stored[0].SupplierId);
        Assert.Null(stored[1].SupplierId);
    }

    [Fact]
    public async Task 修改不能把订柜信息移入范围外的客户_拒绝且原单不变()
    {
        using var db = TestDbFactory.Create();
        var (userId, employeeId) = SeedRestrictedOperator(db, grantBookingMenu: true);
        SeedCustomer(db, CustomerOwn, "本人客户", empId: employeeId);
        SeedCustomer(db, CustomerOther, "他人客户");
        var booking = SeedBooking(db, "BA-MOVE", CustomerOwn);
        var ctl = NewController(db, userId);

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => ctl.Update(booking.Id, NewBooking(CustomerOther)));

        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
        db.ChangeTracker.Clear();
        var stored = db.ContainerBookings.Single();
        Assert.Equal(CustomerOwn, stored.CustomerId);
        Assert.Equal(DocumentStatus.Pending, stored.Status);
    }

    [Fact]
    public async Task 状态变更_客户或供应商已停用_拒绝且状态不变()
    {
        using var db = TestDbFactory.Create();
        var privileged = TestAuth.SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerOwn, "本人客户");
        SeedCustomer(db, CustomerOther, "停用客户", status: 0);
        SeedSupplier(db, SupplierOff, "停用供应商", status: 0);
        var pending = SeedBooking(db, "BA-OFF-P", CustomerOther, DocumentStatus.Pending);
        var submitted = SeedBooking(db, "BA-OFF-S", CustomerOwn, DocumentStatus.Submitted, SupplierOff);
        var cancelled = SeedBooking(db, "BA-OFF-C", CustomerOther, DocumentStatus.Approved);
        var ctl = NewController(db, privileged);

        await AssertCode(ErrorCodes.RuleConflict, () => ctl.Submit(pending.Id));
        await AssertCode(ErrorCodes.RuleConflict, () => ctl.Approve(submitted.Id));
        await AssertCode(ErrorCodes.RuleConflict, () => ctl.Cancel(cancelled.Id));
        await AssertCode(ErrorCodes.RuleConflict, () => ctl.Delete(pending.Id));

        db.ChangeTracker.Clear();
        Assert.Equal(DocumentStatus.Pending, db.ContainerBookings.Single(b => b.Id == pending.Id).Status);
        Assert.Equal(DocumentStatus.Submitted, db.ContainerBookings.Single(b => b.Id == submitted.Id).Status);
        Assert.Equal(DocumentStatus.Approved, db.ContainerBookings.Single(b => b.Id == cancelled.Id).Status);
        Assert.All(db.ContainerBookings.ToList(), b => Assert.False(b.IsDeleted));
    }

    // ==================== 4. 历史读取证据 / 选项 / 源码契约 ====================

    [Fact]
    public async Task 历史读取_客户停用供应商删除_仍可读并给出显式不可用证据()
    {
        using var db = TestDbFactory.Create();
        var privileged = TestAuth.SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerOwn, "停用客户", status: 0);
        SeedSupplier(db, SupplierDeleted, "已删除供应商", deleted: true);
        var booking = SeedBooking(db, "BA-HIST", CustomerOwn, DocumentStatus.Approved, SupplierDeleted);
        var ctl = NewController(db, privileged);

        var detail = AssertOkResponse<ContainerBooking>(await ctl.GetById(booking.Id));
        Assert.Equal(booking.Id, detail.Data!.Id);
        Assert.Contains(BookingAuthorizationRules.UnavailableEvidencePrefix, detail.Message);
        Assert.Contains("已停用", detail.Message);
        Assert.Contains("已删除", detail.Message);

        var page = AssertOkResponse<PagedResult<ContainerBooking>>(await ctl.GetPaged(new PageQuery(), null));
        Assert.Equal("BA-HIST", Assert.Single(page.Data!.Items).BookingNo);
        Assert.Contains(BookingAuthorizationRules.UnavailableEvidencePrefix, page.Message);

        var timeline = AssertOkResponse<ContainerShipmentTimelineDetailDto>(await ctl.GetShipmentTimeline(booking.Id));
        Assert.Contains(BookingAuthorizationRules.UnavailableEvidencePrefix, timeline.Message);

        // 只读：历史单据的客户 / 状态 / 软删除标记一律保持原样，绝不回填或改写
        db.ChangeTracker.Clear();
        var stored = db.ContainerBookings.Single();
        Assert.Equal(CustomerOwn, stored.CustomerId);
        Assert.Equal(DocumentStatus.Approved, stored.Status);
        Assert.False(stored.IsDeleted);
    }

    [Fact]
    public async Task 报关行选项_与订柜信息同一身份与菜单门槛()
    {
        using var db = TestDbFactory.Create();
        var privileged = TestAuth.SeedPrivilegedUser(db);
        var (operatorId, _) = SeedRestrictedOperator(db, grantBookingMenu: true);
        var noMenuUser = SeedMenuUser(db);

        AssertOk<List<OtherInfoOptionDto>>(await NewController(db, privileged).GetCustomsBrokerOptions());
        AssertOk<List<OtherInfoOptionDto>>(await NewController(db, operatorId).GetCustomsBrokerOptions());
        await AssertCode(ErrorCodes.Forbidden, () => NewController(db, noMenuUser).GetCustomsBrokerOptions());
        await AssertCode(ErrorCodes.Unauthorized, () => NewController(db, null).GetCustomsBrokerOptions());
    }

    [Fact]
    public void 规则_纯只读判定_不落库且不新增权限()
    {
        var rules = ReadSource("ERP.Application/Services/BookingAuthorizationRules.cs");

        Assert.Contains("AsNoTracking", rules);
        Assert.DoesNotContain("SaveChanges", rules);          // 纯判定，不落库
        Assert.DoesNotContain("ContainerBookings.Add", rules); // 不新增订柜信息
        Assert.DoesNotContain(".Remove(", rules);
        Assert.DoesNotContain(".Update(", rules);
        Assert.DoesNotContain("HttpClient", rules);           // 不调用外部系统
        Assert.DoesNotContain("SysRoleMenus.Add", rules);     // 不新增用户授权 / 权限模型

        Assert.Equal("booking", BookingAuthorizationRules.RequiredMenuCode);
        Assert.Equal(PreLoadingBookingLinkRules.BookingRequiredMenuCode, BookingAuthorizationRules.RequiredMenuCode);
        Assert.Contains("绝不降级为全局 / 管理员可见", BookingAuthorizationRules.RuleText);
        Assert.Contains("未映射业务员", BookingAuthorizationRules.RuleText);
        Assert.Contains("不新增任何表 / 列 / 菜单 / 权限", BookingAuthorizationRules.BoundaryText);
    }

    [Fact]
    public void 控制器_全部路由先授权_列表先范围后计数_状态变更共用订柜行锁()
    {
        var controller = ReadSource("ERP.Api/Controllers/ContainerControllers.cs");

        Assert.True(Count(controller, "BookingAuthorizationRules.EnsureAuthorizedAsync") >= 8);
        Assert.Contains("BookingAuthorizationRules.ApplyScope", controller);
        Assert.Contains("BookingAuthorizationRules.EnsureWriteAuthorizedAsync", controller);
        Assert.True(Count(controller, "BookingAuthorizationRules.EnsureWriteAllowedAsync") >= 4);
        Assert.Contains("EnsureWriteAllowedAsync(Db, scope, entity, existing.CustomerId)", controller);
        Assert.Contains("ContainerBookings WITH (UPDLOCK, HOLDLOCK)", controller);
        Assert.Contains("PreLoadingBookingLinkRules.ValidateBookingCancellationAsync", controller);
        Assert.Contains("[HttpPost(\"{id:long}/submit\")]", controller);
        Assert.Contains("[HttpPost(\"{id:long}/approve\")]", controller);
        Assert.Contains("[HttpPost(\"{id:long}/cancel\")]", controller);
        Assert.Contains("[HttpDelete(\"{id:long}\")]", controller);

        // 列表：授权（含范围解析）严格先于计数 / 分页（从授权位置向后查找计数）
        var auth = controller.IndexOf("BookingAuthorizationRules.EnsureAuthorizedAsync", StringComparison.Ordinal);
        Assert.True(auth >= 0);
        var count = controller.IndexOf("var total = await source.CountAsync();", auth, StringComparison.Ordinal);
        Assert.True(count > auth);

        // 新增：授权 + 主数据校验严格先于单据号生成
        var writeAuth = controller.IndexOf("BookingAuthorizationRules.EnsureWriteAuthorizedAsync", StringComparison.Ordinal);
        var generate = controller.IndexOf("GenerateAsync(DocumentType.ContainerBooking)", StringComparison.Ordinal);
        Assert.True(writeAuth >= 0 && generate > writeAuth);

        // 授权仅继承基类 [Authorize]，不在控制器上新增权限特性
        var type = typeof(ContainerBookingController);
        Assert.NotNull(type.BaseType!.GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
            .Cast<AuthorizeAttribute>().FirstOrDefault());
        Assert.Empty(type.GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false));
    }

    // ==================== 脚手架与种子数据 ====================

    private static ContainerBookingController NewController(ErpDbContext db, long? userId)
    {
        var ctl = new ContainerBookingController(db, new DocumentNumberService(db));
        TestAuth.SetUser(ctl, userId);
        return ctl;
    }

    private static ContainerBooking NewBooking(long customerId = CustomerOwn, long? supplierId = null)
        => new()
        {
            BookingDate = DateTime.Today,
            CustomerId = customerId,
            SupplierId = supplierId,
            Remark = "ERP-360_TEST"
        };

    private static void SeedCustomer(ErpDbContext db, long id, string name,
        long? empId = null, int status = 1, bool deleted = false)
    {
        db.BaseCustomers.Add(new BaseCustomer
        {
            Id = id, CustomerCode = $"BA-C-{id}", CustomerName = name,
            EmpId = empId, Status = status, IsDeleted = deleted
        });
        db.SaveChanges();
    }

    private static void SeedSupplier(ErpDbContext db, long id, string name, int status = 1, bool deleted = false)
    {
        db.BaseSuppliers.Add(new BaseSupplier
        {
            Id = id, SupplierCode = $"BA-S-{id}", SupplierName = name,
            Status = status, IsDeleted = deleted
        });
        db.SaveChanges();
    }

    private static ContainerBooking SeedBooking(ErpDbContext db, string no, long customerId = CustomerOwn,
        DocumentStatus status = DocumentStatus.Pending, long? supplierId = null)
    {
        var booking = new ContainerBooking
        {
            BookingNo = no, BookingDate = DateTime.Today, CustomerId = customerId,
            SupplierId = supplierId, Status = status
        };
        db.ContainerBookings.Add(booking);
        db.SaveChanges();
        return booking;
    }

    private static long SeedUser(ErpDbContext db, UserStatus status, bool deleted = false)
    {
        var user = new SysUser
        {
            UserName = $"ba-{Guid.NewGuid():N}", PasswordHash = "hash", PasswordSalt = "salt",
            DisplayName = "测试账号", Status = status, IsDeleted = deleted
        };
        db.SysUsers.Add(user);
        db.SaveChanges();
        return user.Id;
    }

    /// <summary>普通角色账号（未映射业务员）：可选授予既有「订柜信息」菜单。</summary>
    private static long SeedMenuUser(ErpDbContext db, bool grantBookingMenu = false)
    {
        var user = new SysUser
        {
            UserName = $"ba-menu-{Guid.NewGuid():N}", PasswordHash = "hash", PasswordSalt = "salt",
            DisplayName = "菜单账号", Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        db.SaveChanges();

        if (!grantBookingMenu) return user.Id;

        var role = new SysRole { RoleName = "菜单角色", RoleCode = $"BaMenu-{Guid.NewGuid():N}", IsSystem = false };
        db.SysRoles.Add(role);
        db.SaveChanges();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        db.SaveChanges();
        GrantBookingMenu(db, role.Id);
        return user.Id;
    }

    /// <summary>受限制业务员账号：员工映射（本人客户数据范围）+ 可选既有「订柜信息」菜单。</summary>
    private static (long UserId, long EmployeeId) SeedRestrictedOperator(ErpDbContext db, bool grantBookingMenu)
    {
        var code = $"ba-op-{Guid.NewGuid():N}";
        var employee = new BaseEmployee { EmployeeCode = code, EmployeeName = code, IsSalesman = true, Status = 1 };
        db.BaseEmployees.Add(employee);
        db.SaveChanges();

        var user = new SysUser
        {
            UserName = code, PasswordHash = "hash", PasswordSalt = "salt",
            DisplayName = code, Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        db.SaveChanges();

        var role = new SysRole { RoleName = "订柜操作员", RoleCode = $"BaOp-{Guid.NewGuid():N}", IsSystem = false };
        db.SysRoles.Add(role);
        db.SaveChanges();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        db.SaveChanges();

        if (grantBookingMenu) GrantBookingMenu(db, role.Id);
        return (user.Id, employee.Id);
    }

    private static void GrantBookingMenu(ErpDbContext db, long roleId)
    {
        var menu = new SysMenu
        {
            MenuCode = BookingAuthorizationRules.RequiredMenuCode,
            MenuName = BookingAuthorizationRules.RequiredMenuText,
            MenuType = MenuType.Menu
        };
        db.SysMenus.Add(menu);
        db.SaveChanges();
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = roleId, MenuId = menu.Id });
        db.SaveChanges();
    }

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

    private static void AssertOkObject(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<ApiResponse<object>>(ok.Value);
        Assert.Equal(ErrorCodes.Success, resp.Code);
    }

    private static string RepoRoot()
        => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    private static string ReadSource(string relativePath)
        => File.ReadAllText(Path.Combine(RepoRoot(), "src", relativePath.Replace('/', Path.DirectorySeparatorChar)));

    private static int Count(string text, string token)
    {
        var count = 0;
        var index = text.IndexOf(token, StringComparison.Ordinal);
        while (index >= 0)
        {
            count++;
            index = text.IndexOf(token, index + token.Length, StringComparison.Ordinal);
        }
        return count;
    }
}
