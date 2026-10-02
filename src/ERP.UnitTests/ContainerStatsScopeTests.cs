using System.Security.Claims;
using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// 柜量与装柜利用率统计（/api/reports/container-stats，ERP-251）数据范围与授权单元测试：
/// 每次请求重新校验当前登录身份与「柜量与装柜利用率统计」菜单授权，按业务员数据范围（ERP-097 唯一权威口径）
/// 在查询源头过滤装柜清单头（特权账号不过滤、受限制业务员仅其被分配客户、空客户对受限制账号不可见），
/// 且只统计已审核、未删除清单头；服务层强制要求数据范围，不提供无范围绕过。
/// 本表口径为「已审核、未删除、授权范围装柜清单头证据」，非实际发货 / 实体柜数量。
/// <para>全部使用内存数据库（TestDbFactory），不连接 SQL Server、不启动 API、不执行任何 SQL / seed。</para>
/// </summary>
public class ContainerStatsScopeTests
{
    private const string MenuCode = "container-stats";

    private static readonly DateTime Start = new(2026, 9, 1);
    private static readonly DateTime End = new(2026, 9, 30);

    // ==================== 脚手架 ====================

    private static BaseCustomer SeedCustomer(ErpDbContext db, string code, string name, long? empId = null)
    {
        var customer = new BaseCustomer
        {
            CustomerCode = code,
            CustomerName = name,
            Status = 1,
            CreditStatus = "正常",
            EmpId = empId,
            IsDeleted = false
        };
        db.BaseCustomers.Add(customer);
        db.SaveChanges();
        return customer;
    }

    private static BaseEmployee SeedEmployee(ErpDbContext db, string code, bool isSalesman = true)
    {
        var employee = new BaseEmployee
        {
            EmployeeCode = code,
            EmployeeName = code,
            IsSalesman = isSalesman,
            Status = 1
        };
        db.BaseEmployees.Add(employee);
        db.SaveChanges();
        return employee;
    }

    private static SysUser SeedUser(ErpDbContext db, string userName)
    {
        var user = new SysUser
        {
            UserName = userName,
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = userName,
            Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        db.SaveChanges();
        return user;
    }

    private static SysRole SeedRole(ErpDbContext db, string code, bool isSystem = false)
    {
        var role = new SysRole { RoleName = code, RoleCode = code, IsSystem = isSystem };
        db.SysRoles.Add(role);
        db.SaveChanges();
        return role;
    }

    private static void SeedUserRole(ErpDbContext db, long userId, long roleId)
    {
        db.SysUserRoles.Add(new SysUserRole { UserId = userId, RoleId = roleId });
        db.SaveChanges();
    }

    private static SysMenu SeedMenu(ErpDbContext db, string code)
    {
        var menu = new SysMenu { MenuName = code, MenuCode = code, MenuType = MenuType.Menu };
        db.SysMenus.Add(menu);
        db.SaveChanges();
        return menu;
    }

    private static void SeedRoleMenu(ErpDbContext db, long roleId, long menuId)
    {
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = roleId, MenuId = menuId });
        db.SaveChanges();
    }

    /// <summary>创建拥有「柜量与装柜利用率统计」菜单授权的用户（不含业务员映射，由各用例按需补齐）</summary>
    private static SysUser SeedAuthorizedUser(ErpDbContext db, string userName, string roleCode, bool isSystemRole = false)
    {
        var role = SeedRole(db, roleCode, isSystemRole);
        var user = SeedUser(db, userName);
        SeedUserRole(db, user.Id, role.Id);
        SeedRoleMenu(db, role.Id, SeedMenu(db, MenuCode).Id);
        return user;
    }

    private static ContainerLoadingList SeedList(
        ErpDbContext db, string loadingListNo, long customerId, string containerNo,
        DocumentStatus status = DocumentStatus.Approved, bool deleted = false,
        DateTime? loadingDate = null)
    {
        var list = new ContainerLoadingList
        {
            LoadingListNo = loadingListNo,
            LoadingDate = loadingDate ?? new DateTime(2026, 9, 10),
            ContainerNo = containerNo,
            CustomerId = customerId,
            Status = status,
            IsDeleted = deleted,
            TotalCartons = 1m,
            TotalWeight = 2m,
            TotalVolume = 3m
        };
        db.ContainerLoadingLists.Add(list);
        db.SaveChanges();
        return list;
    }

    private static ReportController BuildController(ErpDbContext db, long? userId)
    {
        var ctl = new ReportController(new ReportService(db), db);
        var claims = userId.HasValue
            ? new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }
            : Array.Empty<Claim>();
        ctl.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"))
            }
        };
        return ctl;
    }

    private static List<ReportDtos.ContainerStatsItem> OkList(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<ApiResponse<List<ReportDtos.ContainerStatsItem>>>(ok.Value);
        return resp.Data ?? new List<ReportDtos.ContainerStatsItem>();
    }

    // ==================== 身份 / 数据库 / 菜单授权 ====================

    [Fact]
    public async Task 无身份_未认证拒绝()
    {
        using var db = TestDbFactory.Create();
        var ctl = BuildController(db, null);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.ContainerStats(Start, End));
        Assert.Equal(ErrorCodes.Unauthorized, ex.Code);
    }

    [Fact]
    public async Task 无数据库上下文_内部错误拒绝()
    {
        using var db = TestDbFactory.Create();
        var ctl = new ReportController(new ReportService(db), null);
        ctl.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    new[] { new Claim(ClaimTypes.NameIdentifier, "1") }, "Test"))
            }
        };

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.ContainerStats(Start, End));
        Assert.Equal(ErrorCodes.InternalError, ex.Code);
    }

    [Fact]
    public async Task 无菜单授权_权限不足拒绝()
    {
        using var db = TestDbFactory.Create();
        var role = SeedRole(db, "NoMenu");
        var user = SeedUser(db, "nommenu-user");
        SeedUserRole(db, user.Id, role.Id);
        var ctl = BuildController(db, user.Id);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.ContainerStats(Start, End));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    [Fact]
    public async Task 授权被回收_下一次请求立即拒绝()
    {
        using var db = TestDbFactory.Create();
        var role = SeedRole(db, "Revoke-Role");
        var user = SeedUser(db, "revoke-user");
        SeedUserRole(db, user.Id, role.Id);
        var menu = SeedMenu(db, MenuCode);
        var roleMenu = new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id };
        db.SysRoleMenus.Add(roleMenu);
        db.SaveChanges();

        var ctl = BuildController(db, user.Id);
        Assert.IsType<OkObjectResult>(await ctl.ContainerStats(Start, End));

        roleMenu.IsDeleted = true;
        db.SaveChanges();

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.ContainerStats(Start, End));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    // ==================== 业务员数据范围 ====================

    [Fact]
    public async Task 受限制业务员_只看到被分配客户()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "alice", "Sales");
        var employee = SeedEmployee(db, "alice");
        var mine = SeedCustomer(db, "C001", "我的客户", employee.Id);
        var other = SeedCustomer(db, "C002", "别人的客户", employee.Id + 1000);

        SeedList(db, "LL-MINE", mine.Id, "TCLU-001");
        SeedList(db, "LL-OTHER", other.Id, "TCLU-002");

        var ctl = BuildController(db, user.Id);
        var items = OkList(await ctl.ContainerStats(Start, End));

        var row = Assert.Single(items);
        Assert.Equal("TCLU-001", row.ContainerNo);
    }

    [Fact]
    public async Task 未映射业务员_看不到任何数据()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "bob", "Sales");
        var customer = SeedCustomer(db, "C001", "有客户");
        SeedList(db, "LL-1", customer.Id, "TCLU-001");

        var ctl = BuildController(db, user.Id);
        var items = OkList(await ctl.ContainerStats(Start, End));

        Assert.Empty(items);
    }

    [Fact]
    public async Task 服务层_受限范围_只返回范围内清单()
    {
        using var db = TestDbFactory.Create();
        var mine = SeedCustomer(db, "C001", "我的客户");
        var other = SeedCustomer(db, "C002", "别人的客户");
        SeedList(db, "LL-MINE", mine.Id, "TCLU-001");
        SeedList(db, "LL-OTHER", other.Id, "TCLU-002");

        var scope = new SalespersonDataScope
        {
            IsPrivileged = false,
            SalesmanId = 1,
            AllowedCustomerIds = new HashSet<long> { mine.Id }
        };
        var service = new ReportService(db);
        var result = await service.GetContainerStatsAsync(Start, End, scope);

        var row = Assert.Single(result);
        Assert.Equal("TCLU-001", row.ContainerNo);
    }

    [Fact]
    public async Task 服务层_空数据范围_拒绝_不提供无范围绕过()
    {
        using var db = TestDbFactory.Create();
        var service = new ReportService(db);

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            service.GetContainerStatsAsync(Start, End, null!));
    }

    // ==================== 状态与软删除证据 ====================

    [Fact]
    public async Task 草稿已取消与软删除清单_不计入()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv-user", "Priv", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "客户");

        SeedList(db, "LL-DRAFT", customer.Id, "TCLU-DRAFT", DocumentStatus.Pending);
        SeedList(db, "LL-CANCEL", customer.Id, "TCLU-CANCEL", DocumentStatus.Cancelled);
        SeedList(db, "LL-DEL", customer.Id, "TCLU-DEL", DocumentStatus.Approved, deleted: true);
        SeedList(db, "LL-OK", customer.Id, "TCLU-OK", DocumentStatus.Approved);

        var ctl = BuildController(db, user.Id);
        var items = OkList(await ctl.ContainerStats(Start, End));

        var row = Assert.Single(items);
        Assert.Equal("TCLU-OK", row.ContainerNo);
        Assert.Equal(1, row.LoadingListCount);
    }

    [Fact]
    public async Task 空白柜号_按装柜清单独立不合并()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv-user", "Priv", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "客户");

        SeedList(db, "LL-BLANK-1", customer.Id, "   ");
        SeedList(db, "LL-BLANK-2", customer.Id, "");

        var ctl = BuildController(db, user.Id);
        var items = OkList(await ctl.ContainerStats(Start, End));

        Assert.Equal(2, items.Count);
        Assert.All(items, i => Assert.True(i.ContainerNoBlank));
        Assert.All(items, i => Assert.Equal(1, i.LoadingListCount));
    }

}
