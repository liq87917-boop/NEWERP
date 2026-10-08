using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-363 预装柜单实时授权 / 权威客户数据范围护栏单元测试。
/// <para>覆盖：列表 / 详情 / 出运跟踪 / 出运时间线 / 新增 / 修改 / 提交 / 审核 / 取消 / 删除<b>每一个</b>路由的
/// 身份（缺失 / 已删除 / 禁用）与既有「预装柜单」菜单授权 fail closed；未映射业务员的受限账号不降级为全局可见；
/// 受限业务员只按显式 BookingId 与订柜信息权威归属客户判定范围、列表在计数前按数据库侧范围过滤；未关联 / 来源
/// 已删除的无主单据 fail closed，而特权账号保留历史访问；修改先校验「已存储」与「拟议」来源范围、被拒绝的修改 /
/// 状态变更 / 删除不改动库中字段 / 明细 / 状态；规则为纯只读判定、控制器全部路由先授权并共用锁与事务。</para>
/// <para>全部使用内存数据库（TestDbFactory），不连接 SQL Server、不启动 API、不运行浏览器验收。</para>
/// </summary>
public class PreLoadingAuthorizationTests
{
    private const long CustomerOwn = 964001L;
    private const long CustomerOther = 964002L;
    private const long ProductA = 964101L;
    private const long ProductB = 964102L;

    // ==================== 脚手架与种子数据 ====================

    private static ContainerPreLoadingController NewController(ErpDbContext db, long? userId)
    {
        var ctl = new ContainerPreLoadingController(db, new DocumentNumberService(db));
        TestAuth.SetUser(ctl, userId);
        return ctl;
    }

    private static void SeedCustomer(ErpDbContext db, long id, string code, string name,
        long? empId = null, int status = 1)
    {
        db.BaseCustomers.Add(new BaseCustomer
        {
            Id = id, CustomerCode = code, CustomerName = name, EmpId = empId, Status = status
        });
        db.SaveChanges();
    }

    private static ContainerBooking SeedBooking(ErpDbContext db, string no, long customerId,
        DocumentStatus status = DocumentStatus.Approved, bool deleted = false)
    {
        var booking = new ContainerBooking
        {
            BookingNo = no, BookingDate = DateTime.Today, CustomerId = customerId,
            Status = status, IsDeleted = deleted, BillOfLadingNo = "BL-" + no
        };
        db.ContainerBookings.Add(booking);
        db.SaveChanges();
        return booking;
    }

    private static ContainerPreLoading SeedPreLoading(ErpDbContext db, string no, long? bookingId,
        DocumentStatus status, string containerNo = "",
        params (long ProductId, decimal Quantity)[] lines)
    {
        var pre = new ContainerPreLoading
        {
            PreLoadingNo = no, LoadingDate = DateTime.Today, BookingId = bookingId,
            ContainerNo = containerNo, SealNo = "SEAL-" + no, Status = status
        };
        db.ContainerPreLoadings.Add(pre);
        db.SaveChanges();
        foreach (var (productId, quantity) in lines)
        {
            db.ContainerPreLoadingDetails.Add(new ContainerPreLoadingDetail
            {
                PreLoadingId = pre.Id, ProductId = productId, ProductName = $"商品{productId}", Quantity = quantity
            });
        }
        db.SaveChanges();
        return pre;
    }

    private static ContainerPreLoading Reload(ErpDbContext db, long id)
    {
        db.ChangeTracker.Clear();
        return db.ContainerPreLoadings.AsNoTracking().Include(o => o.Details).Single(o => o.Id == id);
    }

    /// <summary>播种普通（非特权）账号并授予指定既有菜单；未映射为业务员时数据范围为空（fail closed）。</summary>
    private static SysUser SeedMenuUser(ErpDbContext db, params string[] menuCodes)
    {
        var role = new SysRole { RoleName = "测试角色", RoleCode = $"Role-{Guid.NewGuid():N}", IsSystem = false };
        db.SysRoles.Add(role);
        db.SaveChanges();

        var user = new SysUser
        {
            UserName = $"u-{Guid.NewGuid():N}", PasswordHash = "hash", PasswordSalt = "salt",
            DisplayName = "测试用户", Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        db.SaveChanges();

        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        db.SaveChanges();

        GrantMenus(db, role.Id, menuCodes);
        return user;
    }

    /// <summary>播种受限制的预装柜操作员（业务员映射 + 既有菜单授权），返回其用户 Id 与员工 Id。</summary>
    private static (long UserId, long EmployeeId) SeedRestrictedOperator(ErpDbContext db, params string[] menuCodes)
    {
        var code = $"container-op-{Guid.NewGuid():N}";
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

        var role = new SysRole { RoleName = "预装柜操作员", RoleCode = $"PreLoadingOp-{Guid.NewGuid():N}", IsSystem = false };
        db.SysRoles.Add(role);
        db.SaveChanges();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        db.SaveChanges();

        GrantMenus(db, role.Id, menuCodes);
        return (user.Id, employee.Id);
    }

    private static void GrantMenus(ErpDbContext db, long roleId, params string[] menuCodes)
    {
        foreach (var menuCode in menuCodes)
        {
            var menu = new SysMenu
            {
                MenuCode = menuCode,
                MenuName = menuCode == PreLoadingAuthorizationRules.RequiredMenuCode
                    ? PreLoadingAuthorizationRules.RequiredMenuText
                    : PreLoadingBookingLinkRules.BookingRequiredMenuText,
                MenuType = MenuType.Menu
            };
            db.SysMenus.Add(menu);
            db.SaveChanges();
            db.SysRoleMenus.Add(new SysRoleMenu { RoleId = roleId, MenuId = menu.Id });
            db.SaveChanges();
        }
    }

    private static ContainerPreLoading NewPreLoading(long? bookingId, string containerNo,
        params (long ProductId, decimal Quantity)[] lines)
    {
        var entity = new ContainerPreLoading
        {
            BookingId = bookingId, ContainerNo = containerNo, SealNo = "SEAL-NEW", LoadingDate = DateTime.Today
        };
        foreach (var (productId, quantity) in lines)
        {
            entity.Details.Add(new ContainerPreLoadingDetail
            {
                ProductId = productId, ProductName = $"商品{productId}", Quantity = quantity
            });
        }
        return entity;
    }

    private static string RepoRoot() =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    private static string ReadSource(string relativePath) =>
        File.ReadAllText(Path.Combine(RepoRoot(), "src", relativePath.Replace('/', Path.DirectorySeparatorChar)));

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

    private static T AssertOk<T>(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<ApiResponse<T>>(ok.Value);
        Assert.Equal(ErrorCodes.Success, response.Code);
        return response.Data!;
    }

    private static async Task AssertCode(int expected, Func<Task<IActionResult>> action)
    {
        var ex = await Assert.ThrowsAsync<BusinessException>(action);
        Assert.Equal(expected, ex.Code);
    }

    // ==================== 1. 身份 / 账号状态 / 菜单 fail closed ====================

    [Fact]
    public async Task 每个路由_无身份_一律未认证拒绝且不落任何变更()
    {
        using var db = TestDbFactory.Create();
        var booking = SeedBooking(db, "PRE-ANON", CustomerOwn);
        var pending = SeedPreLoading(db, "YZ-ANON", booking.Id, DocumentStatus.Pending, "CTN-ANON");
        var submitted = SeedPreLoading(db, "YZ-ANON-S", booking.Id, DocumentStatus.Submitted, "CTN-ANON-S");
        var ctl = NewController(db, userId: null);

        await AssertCode(ErrorCodes.Unauthorized, () => ctl.GetPaged(new PageQuery { Page = 1, PageSize = 10 }, null));
        await AssertCode(ErrorCodes.Unauthorized, () => ctl.GetById(pending.Id));
        await AssertCode(ErrorCodes.Unauthorized, () => ctl.GetShipmentTimeline(pending.Id));
        await AssertCode(ErrorCodes.Unauthorized, () => ctl.Create(NewPreLoading(booking.Id, "CTN-NEW", (ProductA, 1m))));
        await AssertCode(ErrorCodes.Unauthorized, () => ctl.Update(pending.Id, NewPreLoading(booking.Id, "CTN-UPD", (ProductA, 1m))));
        await AssertCode(ErrorCodes.Unauthorized, () => ctl.Submit(pending.Id));
        await AssertCode(ErrorCodes.Unauthorized, () => ctl.Approve(submitted.Id));
        await AssertCode(ErrorCodes.Unauthorized, () => ctl.Cancel(pending.Id));
        await AssertCode(ErrorCodes.Unauthorized, () => ctl.Delete(pending.Id));

        Assert.Empty(db.ContainerPreLoadingDetails);
        Assert.Equal(2, db.ContainerPreLoadings.Count());
        Assert.Equal(DocumentStatus.Pending, Reload(db, pending.Id).Status);
        Assert.Equal(DocumentStatus.Submitted, Reload(db, submitted.Id).Status);
        Assert.False(Reload(db, pending.Id).IsDeleted);
    }

    [Fact]
    public async Task 禁用账号_权限不足_已删除账号_按未认证拒绝()
    {
        using var db = TestDbFactory.Create();
        var booking = SeedBooking(db, "PRE-STATUS", CustomerOwn);
        var entity = SeedPreLoading(db, "YZ-STATUS", booking.Id, DocumentStatus.Pending, "CTN-STATUS");

        var disabled = SeedMenuUser(db, PreLoadingAuthorizationRules.RequiredMenuCode);
        disabled.Status = UserStatus.Disabled;
        db.SaveChanges();

        var deleted = SeedMenuUser(db, PreLoadingAuthorizationRules.RequiredMenuCode);
        deleted.IsDeleted = true;
        db.SaveChanges();

        await AssertCode(ErrorCodes.Forbidden, () => NewController(db, disabled.Id).GetPaged(new PageQuery(), null));
        await AssertCode(ErrorCodes.Forbidden, () => NewController(db, disabled.Id).Delete(entity.Id));
        await AssertCode(ErrorCodes.Unauthorized, () => NewController(db, deleted.Id).GetPaged(new PageQuery(), null));
        await AssertCode(ErrorCodes.Unauthorized, () => NewController(db, deleted.Id).Delete(entity.Id));

        Assert.False(Reload(db, entity.Id).IsDeleted);
        Assert.Equal(DocumentStatus.Pending, Reload(db, entity.Id).Status);
    }

    [Fact]
    public async Task 非特权_无既有预装柜菜单_拒绝且文案指出模块授权()
    {
        using var db = TestDbFactory.Create();
        var booking = SeedBooking(db, "PRE-NOMENU", CustomerOwn);
        var entity = SeedPreLoading(db, "YZ-NOMENU", booking.Id, DocumentStatus.Pending, "CTN-NOMENU");
        var noMenuUser = SeedMenuUser(db);

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => NewController(db, noMenuUser.Id).GetPaged(new PageQuery(), null));

        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
        Assert.Contains("模块授权", ex.Message);
        Assert.Equal(DocumentStatus.Pending, Reload(db, entity.Id).Status);
    }

    [Fact]
    public async Task 非特权_有菜单但未映射业务员_fail_closed不降级为全局可见()
    {
        using var db = TestDbFactory.Create();
        SeedBooking(db, "PRE-UNMAPPED", CustomerOwn);
        var user = SeedMenuUser(db, PreLoadingAuthorizationRules.RequiredMenuCode);

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => NewController(db, user.Id).GetPaged(new PageQuery(), null));

        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
        Assert.Contains("未映射为业务员", ex.Message);
    }

    // ==================== 2. 数据范围：受限业务员只认显式链接的权威归属 ====================

    [Fact]
    public async Task 受限业务员_列表只返回本人客户订柜下的预装柜且计数在范围过滤之后()
    {
        using var db = TestDbFactory.Create();
        var (userId, employeeId) = SeedRestrictedOperator(db, PreLoadingAuthorizationRules.RequiredMenuCode);
        SeedCustomer(db, CustomerOwn, "C-OWN", "本人客户", empId: employeeId);
        SeedCustomer(db, CustomerOther, "C-OTHER", "他人客户");

        var ownBooking = SeedBooking(db, "PRE-OWN", CustomerOwn);
        var otherBooking = SeedBooking(db, "PRE-OTHER", CustomerOther);
        SeedPreLoading(db, "YZ-OWN", ownBooking.Id, DocumentStatus.Pending, "CTN-OWN", (ProductA, 3m));
        SeedPreLoading(db, "YZ-OTHER", otherBooking.Id, DocumentStatus.Pending, "CTN-OTHER", (ProductA, 4m));
        SeedPreLoading(db, "YZ-UNLINKED", null, DocumentStatus.Pending, "CTN-UNLINKED", (ProductA, 5m));

        var page = AssertOk<PagedResult<ContainerPreLoading>>(
            await NewController(db, userId).GetPaged(new PageQuery { Page = 1, PageSize = 50 }, null));

        var row = Assert.Single(page.Items);
        Assert.Equal("YZ-OWN", row.PreLoadingNo);
        Assert.Equal(1, page.Total); // 他人与无主单据都不计数（范围在数据库侧过滤）
    }

    [Fact]
    public async Task 受限业务员_未关联或来源缺失_详情与时间线_fail_closed()
    {
        using var db = TestDbFactory.Create();
        var (userId, employeeId) = SeedRestrictedOperator(db, PreLoadingAuthorizationRules.RequiredMenuCode);
        SeedCustomer(db, CustomerOwn, "C-OWN2", "本人客户", empId: employeeId);

        var ownBooking = SeedBooking(db, "PRE-D-OWN", CustomerOwn);
        var deletedBooking = SeedBooking(db, "PRE-D-DEL", CustomerOwn, deleted: true);
        var own = SeedPreLoading(db, "YZ-D-OWN", ownBooking.Id, DocumentStatus.Pending, "CTN-D-OWN");
        var unlinked = SeedPreLoading(db, "YZ-D-UNLINKED", null, DocumentStatus.Pending, "CTN-D-UNLINKED");
        var missing = SeedPreLoading(db, "YZ-D-MISSING", 999999L, DocumentStatus.Pending, "CTN-D-MISSING");
        var deletedSource = SeedPreLoading(db, "YZ-D-DELSRC", deletedBooking.Id, DocumentStatus.Pending, "CTN-D-DELSRC");
        var ctl = NewController(db, userId);

        Assert.Equal(own.Id, AssertOk<ContainerPreLoading>(await ctl.GetById(own.Id)).Id);
        await AssertCode(ErrorCodes.Forbidden, () => ctl.GetById(unlinked.Id));
        await AssertCode(ErrorCodes.Forbidden, () => ctl.GetById(missing.Id));
        await AssertCode(ErrorCodes.Forbidden, () => ctl.GetById(deletedSource.Id));
        await AssertCode(ErrorCodes.Forbidden, () => ctl.GetShipmentTimeline(unlinked.Id));
    }

    [Fact]
    public async Task 受限业务员_无法编辑他人单据_拒绝且原单与明细不变()
    {
        using var db = TestDbFactory.Create();
        var (userId, employeeId) = SeedRestrictedOperator(db, PreLoadingAuthorizationRules.RequiredMenuCode);
        SeedCustomer(db, CustomerOwn, "C-OWN3", "本人客户", empId: employeeId);
        SeedCustomer(db, CustomerOther, "C-OTHER3", "他人客户");

        var otherBooking = SeedBooking(db, "PRE-OTHER3", CustomerOther);
        var foreign = SeedPreLoading(db, "YZ-FOREIGN", otherBooking.Id, DocumentStatus.Pending, "CTN-FOREIGN", (ProductA, 7m));
        var before = Reload(db, foreign.Id);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => NewController(db, userId)
            .Update(foreign.Id, NewPreLoading(otherBooking.Id, "CTN-HACK", (ProductB, 99m))));

        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
        var after = Reload(db, foreign.Id);
        Assert.Equal(before.BookingId, after.BookingId);
        Assert.Equal(before.ContainerNo, after.ContainerNo);
        Assert.Equal(before.SealNo, after.SealNo);
        Assert.Equal(before.Status, after.Status);
        Assert.Equal(before.Details.Single().ProductId, after.Details.Single().ProductId);
        Assert.Equal(before.Details.Single().Quantity, after.Details.Single().Quantity);
    }

    [Fact]
    public async Task 受限业务员_无法清空订柜链接或改派范围外_拒绝且原单不变()
    {
        using var db = TestDbFactory.Create();
        var (userId, employeeId) = SeedRestrictedOperator(db, PreLoadingAuthorizationRules.RequiredMenuCode);
        SeedCustomer(db, CustomerOwn, "C-OWN4", "本人客户", empId: employeeId);
        SeedCustomer(db, CustomerOther, "C-OTHER4", "他人客户");

        var ownBooking = SeedBooking(db, "PRE-OWN4", CustomerOwn);
        var foreignBooking = SeedBooking(db, "PRE-OTHER4", CustomerOther);
        var own = SeedPreLoading(db, "YZ-OWN4", ownBooking.Id, DocumentStatus.Pending, "CTN-OWN4", (ProductA, 2m));
        var before = Reload(db, own.Id);
        var ctl = NewController(db, userId);

        await AssertCode(ErrorCodes.Forbidden, () => ctl.Update(own.Id, NewPreLoading(null, "CTN-CLEAR", (ProductA, 2m))));
        await AssertCode(ErrorCodes.Forbidden, () => ctl.Update(own.Id, NewPreLoading(foreignBooking.Id, "CTN-MOVE", (ProductA, 2m))));

        var after = Reload(db, own.Id);
        Assert.Equal(before.BookingId, after.BookingId);
        Assert.Equal(before.ContainerNo, after.ContainerNo);
        Assert.Equal(before.SealNo, after.SealNo);
    }

    [Fact]
    public async Task 受限业务员_编辑本人客户单据_放行()
    {
        using var db = TestDbFactory.Create();
        var (userId, employeeId) = SeedRestrictedOperator(db, PreLoadingAuthorizationRules.RequiredMenuCode);
        SeedCustomer(db, CustomerOwn, "C-OWN4B", "本人客户", empId: employeeId);

        var ownBooking = SeedBooking(db, "PRE-OWN4B", CustomerOwn);
        var own = SeedPreLoading(db, "YZ-OWN4B", ownBooking.Id, DocumentStatus.Pending, "CTN-OWN4B", (ProductA, 2m));

        var result = await NewController(db, userId)
            .Update(own.Id, NewPreLoading(ownBooking.Id, "CTN-OWN4B-NEW", (ProductA, 8m)));

        Assert.IsType<OkObjectResult>(result);
        var after = Reload(db, own.Id);
        Assert.Equal("CTN-OWN4B-NEW", after.ContainerNo);
        Assert.Equal(8m, after.Details.Single().Quantity);
    }

    [Fact]
    public async Task 受限业务员_无主或范围外_状态变更与删除一律拒绝且状态不变()
    {
        using var db = TestDbFactory.Create();
        var (userId, employeeId) = SeedRestrictedOperator(db, PreLoadingAuthorizationRules.RequiredMenuCode);
        SeedCustomer(db, CustomerOther, "C-OTHER5", "他人客户");
        var foreignBooking = SeedBooking(db, "PRE-S-OTHER", CustomerOther);
        var foreign = SeedPreLoading(db, "YZ-S-OTHER", foreignBooking.Id, DocumentStatus.Submitted, "CTN-S-OTHER");
        var unlinked = SeedPreLoading(db, "YZ-S-UNLINKED", null, DocumentStatus.Pending, "CTN-S-UNLINKED");
        var ctl = NewController(db, userId);

        await AssertCode(ErrorCodes.Forbidden, () => ctl.Submit(unlinked.Id));
        await AssertCode(ErrorCodes.Forbidden, () => ctl.Approve(foreign.Id));
        await AssertCode(ErrorCodes.Forbidden, () => ctl.Cancel(unlinked.Id));
        await AssertCode(ErrorCodes.Forbidden, () => ctl.Delete(unlinked.Id));

        Assert.Equal(DocumentStatus.Pending, Reload(db, unlinked.Id).Status);
        Assert.Equal(DocumentStatus.Submitted, Reload(db, foreign.Id).Status);
        Assert.False(Reload(db, unlinked.Id).IsDeleted);
    }

    [Fact]
    public async Task 特权账号_未关联历史单据_保留历史访问与历史行为()
    {
        using var db = TestDbFactory.Create();
        var userId = TestAuth.SeedPrivilegedUser(db);
        var unlinked = SeedPreLoading(db, "YZ-HIST", null, DocumentStatus.Pending, "CTN-HIST", (ProductA, 4m));
        var ctl = NewController(db, userId);

        var page = AssertOk<PagedResult<ContainerPreLoading>>(await ctl.GetPaged(new PageQuery(), null));
        Assert.Contains(page.Items, p => p.Id == unlinked.Id);
        Assert.IsType<OkObjectResult>(await ctl.GetById(unlinked.Id));

        Assert.IsType<OkObjectResult>(await ctl.Update(unlinked.Id, NewPreLoading(null, "CTN-HIST-UPD", (ProductA, 5m))));
        Assert.IsType<OkObjectResult>(await ctl.Submit(unlinked.Id));
        Assert.IsType<OkObjectResult>(await ctl.Approve(unlinked.Id));

        var toDelete = SeedPreLoading(db, "YZ-HIST-DEL", null, DocumentStatus.Pending, "CTN-HIST-DEL");
        Assert.IsType<OkObjectResult>(await ctl.Delete(toDelete.Id));

        Assert.Equal(DocumentStatus.Approved, Reload(db, unlinked.Id).Status);
        Assert.True(Reload(db, toDelete.Id).IsDeleted);
    }

    // ==================== 3. 口径与接线契约 ====================

    [Fact]
    public void 规则_纯只读判定_不落库且不新增权限()
    {
        var rules = ReadSource("ERP.Application/Services/PreLoadingAuthorizationRules.cs");

        Assert.Contains("AsNoTracking", rules);
        Assert.DoesNotContain("SaveChanges", rules);           // 纯判定，不落库
        Assert.DoesNotContain(".Add(", rules);                 // 不新增单据 / 权限
        Assert.DoesNotContain(".Remove(", rules);
        Assert.DoesNotContain(".Update(", rules);
        Assert.DoesNotContain("HttpClient", rules);            // 不调用外部系统
        Assert.DoesNotContain("SysRoleMenus", rules);          // 不新增用户授权
        Assert.DoesNotContain("SysMenus", rules);              // 不新增菜单
        Assert.DoesNotContain("ContainerNo =", rules);         // 绝不推导 / 改写柜号
        Assert.DoesNotContain("SealNo =", rules);              // 绝不臆造封条号

        Assert.Equal("pre-loading", PreLoadingAuthorizationRules.RequiredMenuCode);
        Assert.Equal(PreLoadingBookingLinkRules.RequiredMenuCode, PreLoadingAuthorizationRules.RequiredMenuCode);
        Assert.Contains("绝不按单号等自由文本猜测归属", PreLoadingAuthorizationRules.RuleText);
        Assert.Contains("特权账号保留历史访问", PreLoadingAuthorizationRules.RuleText);
        Assert.Contains("不新增任何表 / 列 / 菜单 / 权限", PreLoadingAuthorizationRules.BoundaryText);
        Assert.Contains("ERP-353", PreLoadingAuthorizationRules.BoundaryText);
        Assert.Contains("ERP-348", PreLoadingAuthorizationRules.BoundaryText);
    }

    [Fact]
    public void 控制器_全部路由先授权_列表先范围后计数_状态变更共用锁与事务()
    {
        var controller = ReadSource("ERP.Api/Controllers/ContainerPreLoadingController.cs");

        Assert.True(Count(controller, "PreLoadingAuthorizationRules.EnsureAuthorizedAsync") >= 9);
        Assert.True(Count(controller, "PreLoadingAuthorizationRules.EnsureStoredScopeAllowedAsync") >= 6);
        Assert.True(Count(controller, "PreLoadingAuthorizationRules.EnsureProposedScopeAllowedAsync") >= 2);
        Assert.Contains("PreLoadingAuthorizationRules.ApplyScope", controller);
        Assert.Contains("IsolationLevel.Serializable", controller);
        Assert.True(Count(controller, "AcquirePreLoadingRowLockAsync") >= 5);
        Assert.Contains("ContainerBookings WITH (UPDLOCK, HOLDLOCK)", controller);
        Assert.Contains("ContainerPreLoadings WITH (UPDLOCK, HOLDLOCK)", controller);
        Assert.Contains("PreLoadingBookingLinkRules.ValidateLinkAsync", controller);
        Assert.Contains("PreLoadingBookingLinkRules.ValidateApprovalAsync", controller);
        Assert.Contains("ContainerLoadingFulfillmentRules.ValidateSourceCancellationAsync", controller);
        Assert.Contains("[HttpDelete(\"{id:long}\")]", controller);
        Assert.Contains("[HttpPost(\"{id:long}/submit\")]", controller);
        Assert.Contains("[HttpPost(\"{id:long}/approve\")]", controller);
        Assert.Contains("[HttpPost(\"{id:long}/cancel\")]", controller);

        // 列表：授权（含范围解析）严格先于计数 / 分页
        var auth = controller.IndexOf("PreLoadingAuthorizationRules.EnsureAuthorizedAsync", StringComparison.Ordinal);
        Assert.True(auth >= 0);
        var count = controller.IndexOf("var total = await source.CountAsync();", auth, StringComparison.Ordinal);
        Assert.True(count > auth);

        // 新增：授权 + 拟议范围校验严格先于单据号生成
        var proposed = controller.IndexOf("PreLoadingAuthorizationRules.EnsureProposedScopeAllowedAsync", StringComparison.Ordinal);
        var generate = controller.IndexOf("GenerateAsync(DocumentType.PreLoading)", StringComparison.Ordinal);
        Assert.True(proposed >= 0 && generate > proposed);
    }

    [Fact]
    public void Controller_授权仅继承基类_无权限扩展()
    {
        var type = typeof(ContainerPreLoadingController);
        Assert.NotNull(type.BaseType!.GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
            .Cast<AuthorizeAttribute>().FirstOrDefault());
        Assert.Empty(type.GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false));
    }
}
