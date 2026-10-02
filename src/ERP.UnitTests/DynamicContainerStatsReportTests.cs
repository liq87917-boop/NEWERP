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
/// 动态柜量与装柜利用率证据报表（ERP-252）聚焦单元测试：字段目录 / 字段校验、日期与分页边界、
/// 可选应用筛选（客户 / 柜号关键字）、业务员数据范围、稳定分页、服务端上下文派生、只读操作与授权撤销 / 无效请求拒绝。
/// <para>全部使用内存数据库（TestDbFactory），不连接 SQL Server、不启动 API、不执行任何 SQL / seed。</para>
/// </summary>
public class DynamicContainerStatsReportTests
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

    private static BaseEmployee SeedEmployee(ErpDbContext db, string code)
    {
        var employee = new BaseEmployee
        {
            EmployeeCode = code,
            EmployeeName = code,
            IsSalesman = true,
            Status = 1
        };
        db.BaseEmployees.Add(employee);
        db.SaveChanges();
        return employee;
    }

    private static ContainerLoadingList SeedList(
        ErpDbContext db, string loadingListNo, long customerId, string containerNo,
        DateTime? loadingDate = null, DocumentStatus status = DocumentStatus.Approved, bool deleted = false)
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

    private static SysUser SeedAuthorizedUser(ErpDbContext db, string userName, string roleCode, bool isSystemRole = false)
    {
        var role = SeedRole(db, roleCode, isSystemRole);
        var user = SeedUser(db, userName);
        SeedUserRole(db, user.Id, role.Id);
        SeedRoleMenu(db, role.Id, SeedMenu(db, MenuCode).Id);
        return user;
    }

    private static DynamicContainerStatsReportController BuildController(ErpDbContext db, long? userId)
        => new(db, new ReportService(db));

    private static DynamicContainerStatsReportPageDto OkPage(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<ApiResponse<DynamicContainerStatsReportPageDto>>(ok.Value);
        return resp.Data!;
    }

    private static DynamicContainerStatsReportCatalogDto OkCatalog(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<ApiResponse<DynamicContainerStatsReportCatalogDto>>(ok.Value);
        return resp.Data!;
    }

    private static DynamicContainerStatsReportRequest Request(
        List<string>? fields = null, int page = 1, int pageSize = 20,
        DynamicContainerStatsReportFilterDto? filter = null)
        => new()
        {
            Fields = fields,
            Start = Start,
            End = End,
            Page = page,
            PageSize = pageSize,
            Filter = filter,
        };

    // ==================== 1. 目录 / 字段 ====================

    [Fact]
    public void 目录_有限白名单_含九个证据字段_菜单与额度()
    {
        var catalog = DynamicContainerStatsReportRules.GetCatalogDto();

        Assert.Equal("container-stats", catalog.RequiredMenuCode);
        Assert.Equal("柜量与装柜利用率统计", catalog.RequiredMenuText);
        Assert.Equal(200, catalog.MaxPageSize);
        Assert.Equal(20, catalog.DefaultPageSize);

        var keys = catalog.Fields.Select(f => f.Key).ToList();
        Assert.Equal(
            new[] { "loadingDate", "containerNo", "loadingListCount", "authorizedCustomerCount", "totalCartons", "totalWeight", "totalVolume", "utilizationType", "reasons" },
            keys);
    }

    // ==================== 2. 目录授权（身份 / 菜单 / 回收） ====================

    [Fact]
    public async Task 目录_无身份_未认证拒绝()
    {
        using var db = TestDbFactory.Create();
        var ctl = BuildController(db, null);
        TestAuth.SetUser(ctl, null);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Catalog());
        Assert.Equal(ErrorCodes.Unauthorized, ex.Code);
    }

    [Fact]
    public async Task 目录_无菜单授权_权限不足拒绝()
    {
        using var db = TestDbFactory.Create();
        var role = SeedRole(db, "NoMenu");
        var user = SeedUser(db, "nommenu-user");
        SeedUserRole(db, user.Id, role.Id);
        var ctl = BuildController(db, user.Id);
        TestAuth.SetUser(ctl, user.Id);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Catalog());
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    [Fact]
    public async Task 目录_授权被回收_下一次请求立即拒绝()
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
        TestAuth.SetUser(ctl, user.Id);
        Assert.IsType<OkObjectResult>(await ctl.Catalog());

        roleMenu.IsDeleted = true;
        db.SaveChanges();

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Catalog());
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    // ==================== 3. 预览授权与字段投影 ====================

    [Fact]
    public async Task 预览_授权用户_返回选定字段顺序_只投影选定列()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv-user", "Priv", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "客户");
        SeedList(db, "LL-1", customer.Id, "TCLU-001");

        var ctl = BuildController(db, user.Id);
        TestAuth.SetUser(ctl, user.Id);

        var page = OkPage(await ctl.Preview(Request(fields: new List<string> { "totalVolume", "loadingDate", "containerNo" })));

        Assert.Equal(new[] { "totalVolume", "loadingDate", "containerNo" }, page.Columns.Select(c => c.Key).ToArray());
        var row = Assert.Single(page.Rows);
        Assert.Equal(3m, row["totalVolume"]);
        Assert.Equal(new DateTime(2026, 9, 10), row["loadingDate"]);
        Assert.Equal("TCLU-001", row["containerNo"]);
    }

    [Fact]
    public async Task 预览_无效字段_拒绝()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv-user", "Priv", isSystemRole: true);
        var ctl = BuildController(db, user.Id);
        TestAuth.SetUser(ctl, user.Id);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Preview(Request(fields: new List<string> { "nope" })));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public async Task 预览_无身份_未认证拒绝()
    {
        using var db = TestDbFactory.Create();
        var ctl = BuildController(db, null);
        TestAuth.SetUser(ctl, null);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Preview(Request()));
        Assert.Equal(ErrorCodes.Unauthorized, ex.Code);
    }

    // ==================== 4. 稳定分页 / 页面覆盖 ====================

    [Fact]
    public async Task 预览_稳定分页_总数总页数截断与仅当前页()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv-user", "Priv", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "客户");
        SeedList(db, "LL-1", customer.Id, "AAA", new DateTime(2026, 9, 10));
        SeedList(db, "LL-2", customer.Id, "BBB", new DateTime(2026, 9, 11));
        SeedList(db, "LL-3", customer.Id, "CCC", new DateTime(2026, 9, 12));

        var ctl = BuildController(db, user.Id);
        TestAuth.SetUser(ctl, user.Id);

        var page1 = OkPage(await ctl.Preview(Request(
            fields: new List<string> { "containerNo" }, page: 1, pageSize: 2)));

        Assert.Equal(3, page1.Total);
        Assert.Equal(2, page1.TotalPages);
        Assert.Equal(2, page1.Rows.Count);
        Assert.True(page1.Truncated);
        Assert.True(page1.PageOnly);
        Assert.Equal(new[] { "AAA", "BBB" }, page1.Rows.Select(r => r["containerNo"]).ToArray());

        var page2 = OkPage(await ctl.Preview(Request(
            fields: new List<string> { "containerNo" }, page: 2, pageSize: 2)));
        Assert.Equal(3, page2.Total);
        Assert.Equal(2, page2.TotalPages);
        var row = Assert.Single(page2.Rows);
        Assert.Equal("CCC", row["containerNo"]);
        Assert.False(page2.Truncated);
        Assert.True(page2.PageOnly);
    }

    // ==================== 5. 上下文始终呈现（即使列被隐藏） ====================

    [Fact]
    public async Task 预览_上下文始终呈现_即使证据列被隐藏()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv-user", "Priv", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "客户");
        SeedList(db, "LL-1", customer.Id, "TCLU-001");

        var ctl = BuildController(db, user.Id);
        TestAuth.SetUser(ctl, user.Id);

        // 仅选柜号列（隐藏日期 / 箱数 / 毛重 / 体积 / 未知装载率柜型 / 未知原因）
        var page = OkPage(await ctl.Preview(Request(fields: new List<string> { "containerNo" })));

        Assert.Single(page.Columns);
        Assert.Equal("containerNo", page.Columns[0].Key);

        // 规范化日期 / 来源 / 来源上限 / 数量单位 / 未知实际容积 / 柜型 / 出运上下文始终呈现
        Assert.Equal(new DateTime(2026, 9, 1), page.Start);
        Assert.Equal(new DateTime(2026, 9, 30), page.End);
        Assert.False(string.IsNullOrEmpty(page.SourceContextText));
        Assert.False(string.IsNullOrEmpty(page.SourceLimitText));
        Assert.False(string.IsNullOrEmpty(page.UnitContextText));
        Assert.False(string.IsNullOrEmpty(page.UnknownCapacityContextText));
        Assert.False(string.IsNullOrEmpty(page.TypeContextText));
        Assert.False(string.IsNullOrEmpty(page.ShippingContextText));

        Assert.Equal("装柜日历日 × 原始非空白柜号证据桶", page.Context.Label);
        Assert.Equal(1, page.Context.BucketCount);
        Assert.Equal(1, page.Context.ApprovedLists);
    }

    // ==================== 6. 来源超限 / 范围 ====================

    [Fact]
    public async Task 预览_来源超限_拒绝且不返回数据()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv-user", "Priv", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "客户");

        for (var i = 1; i <= 501; i++)
        {
            db.ContainerLoadingLists.Add(new ContainerLoadingList
            {
                LoadingListNo = $"LL-{i:0000}",
                LoadingDate = new DateTime(2026, 9, 10),
                ContainerNo = $"TCLU-{i:0000}",
                CustomerId = customer.Id,
                Status = DocumentStatus.Approved
            });
        }
        db.SaveChanges();

        var ctl = BuildController(db, user.Id);
        TestAuth.SetUser(ctl, user.Id);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Preview(Request()));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
    }

    [Fact]
    public async Task 预览_受限制业务员_只看到被分配客户()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "alice", "Sales");
        var employee = SeedEmployee(db, "alice");
        var mine = SeedCustomer(db, "C001", "我的客户", employee.Id);
        var other = SeedCustomer(db, "C002", "别人的客户", employee.Id + 1000);
        SeedList(db, "LL-MINE", mine.Id, "TCLU-001");
        SeedList(db, "LL-OTHER", other.Id, "TCLU-002");

        var ctl = BuildController(db, user.Id);
        TestAuth.SetUser(ctl, user.Id);

        var page = OkPage(await ctl.Preview(Request(fields: new List<string> { "containerNo" })));

        var row = Assert.Single(page.Rows);
        Assert.Equal("TCLU-001", row["containerNo"]);
    }
}


