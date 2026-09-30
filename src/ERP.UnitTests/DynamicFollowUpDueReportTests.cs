using System.Security.Claims;
using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.DTOs;
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
/// 动态跟进提醒报表（ERP-193）聚焦单元测试：字段目录 / 字段校验（未知 / 重复 / 顺序）、
/// 到期状态与日期 / 分页边界校验、业务员数据范围（撤销授权 / 空客户）、空页、稳定排序、只读操作。
/// <para>全部使用内存数据库（TestDbFactory），不连接 SQL Server、不启动 API、不执行任何 SQL / seed、不运行浏览器验收。</para>
/// </summary>
public class DynamicFollowUpDueReportTests
{
    private const string FollowUpDueMenuCode = "follow-up-due";

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

    private static CustomerFollowUp SeedFollowUp(
        ErpDbContext db, string followNo, long? customerId, string customerName,
        DateTime? nextFollowDate, string subject = "跟进主题", string salesmanName = "张三")
    {
        var follow = new CustomerFollowUp
        {
            FollowNo = followNo,
            FollowDate = new DateTime(2026, 9, 1),
            CustomerId = customerId,
            CustomerName = customerName,
            Subject = subject,
            SalesmanName = salesmanName,
            Result = "待跟进",
            NextFollowDate = nextFollowDate,
            IsDeleted = false
        };
        db.CustomerFollowUps.Add(follow);
        db.SaveChanges();
        return follow;
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

    private static SysRoleMenu SeedRoleMenu(ErpDbContext db, long roleId, long menuId)
    {
        var roleMenu = new SysRoleMenu { RoleId = roleId, MenuId = menuId };
        db.SysRoleMenus.Add(roleMenu);
        db.SaveChanges();
        return roleMenu;
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

    /// <summary>创建拥有「跟进提醒」菜单授权的用户（不含业务员映射，由各用例按需补齐）</summary>
    private static SysUser SeedAuthorizedUser(ErpDbContext db, string userName, string roleCode, bool isSystemRole = false)
    {
        var role = SeedRole(db, roleCode, isSystemRole);
        var user = SeedUser(db, userName);
        SeedUserRole(db, user.Id, role.Id);
        SeedRoleMenu(db, role.Id, SeedMenu(db, FollowUpDueMenuCode).Id);
        return user;
    }

    private static DynamicFollowUpDueReportController BuildController(ErpDbContext db, long? userId)
    {
        var ctl = new DynamicFollowUpDueReportController(db, new ReportService(db));
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

    private static DynamicFollowUpDueReportPageDto OkPage(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<ApiResponse<DynamicFollowUpDueReportPageDto>>(ok.Value);
        return resp.Data!;
    }

    private static readonly DateTime AsOf = new(2026, 9, 15);

    // ==================== 字段目录与校验（纯规则） ====================

    [Fact]
    public void 目录_有限白名单且包含到期状态派生字段()
    {
        var catalog = DynamicFollowUpDueReportRules.GetCatalog();

        Assert.Equal(DynamicFollowUpDueReportRules.AllFieldKeys.Count, catalog.Count);
        Assert.Contains(catalog, f => f.Key == "dueDays");
        Assert.Contains(catalog, f => f.Key == "dueStatus");
        Assert.All(catalog, f => Assert.False(string.IsNullOrWhiteSpace(f.Key)));
    }

    [Fact]
    public void NormalizeFields_留空返回全部目录顺序()
    {
        var keys = DynamicFollowUpDueReportRules.NormalizeFields(null);
        Assert.Equal(DynamicFollowUpDueReportRules.AllFieldKeys, keys);
    }

    [Fact]
    public void NormalizeFields_保持请求顺序()
    {
        var keys = DynamicFollowUpDueReportRules.NormalizeFields(
            new[] { "customerName", "followNo", "dueStatus" });

        Assert.Equal(new[] { "customerName", "followNo", "dueStatus" }, keys);
    }

    [Fact]
    public void NormalizeFields_未知字段拒绝()
    {
        var ex = Assert.Throws<BusinessException>(
            () => DynamicFollowUpDueReportRules.NormalizeFields(new[] { "customerName", "notAField" }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public void NormalizeFields_重复字段拒绝_大小写不敏感()
    {
        var ex = Assert.Throws<BusinessException>(
            () => DynamicFollowUpDueReportRules.NormalizeFields(new[] { "customerName", "CUSTOMERNAME" }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Theory]
    [InlineData("overdue")]
    [InlineData("today")]
    [InlineData("upcoming")]
    [InlineData("OVERDUE")]
    [InlineData("Today")]
    public void NormalizeDueStatus_合法取值大小写不敏感(string value)
    {
        Assert.NotNull(DynamicFollowUpDueReportRules.NormalizeDueStatus(value));
    }

    [Fact]
    public void NormalizeDueStatus_空为不过滤()
    {
        Assert.Null(DynamicFollowUpDueReportRules.NormalizeDueStatus(null));
        Assert.Null(DynamicFollowUpDueReportRules.NormalizeDueStatus("  "));
    }

    [Fact]
    public void NormalizeDueStatus_未知取值拒绝()
    {
        var ex = Assert.Throws<BusinessException>(
            () => DynamicFollowUpDueReportRules.NormalizeDueStatus("tomorrow"));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(365)]
    public void ValidateAheadDays_边界0与365通过(int aheadDays)
        => DynamicFollowUpDueReportRules.ValidateAheadDays(aheadDays);

    [Theory]
    [InlineData(-1)]
    [InlineData(366)]
    public void ValidateAheadDays_越界拒绝(int aheadDays)
    {
        var ex = Assert.Throws<BusinessException>(
            () => DynamicFollowUpDueReportRules.ValidateAheadDays(aheadDays));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public void ValidatePageBounds_页码小于1拒绝()
    {
        var ex = Assert.Throws<BusinessException>(
            () => DynamicFollowUpDueReportRules.ValidatePageBounds(0, 20));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(201)]
    public void ValidatePageBounds_每页条数越界拒绝(int pageSize)
    {
        var ex = Assert.Throws<BusinessException>(
            () => DynamicFollowUpDueReportRules.ValidatePageBounds(1, pageSize));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(1L)]
    [InlineData(long.MaxValue)]
    public void ValidateCustomerId_留空或正整数通过(long? customerId)
        => Assert.Equal(customerId, DynamicFollowUpDueReportRules.ValidateCustomerId(customerId));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ValidateCustomerId_非正整数拒绝(long customerId)
    {
        var ex = Assert.Throws<BusinessException>(
            () => DynamicFollowUpDueReportRules.ValidateCustomerId(customerId));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public void NormalizeKeyword_留空为不过滤()
    {
        Assert.Null(DynamicFollowUpDueReportRules.NormalizeKeyword(null));
        Assert.Null(DynamicFollowUpDueReportRules.NormalizeKeyword(""));
        Assert.Null(DynamicFollowUpDueReportRules.NormalizeKeyword("   "));
    }

    [Fact]
    public void NormalizeKeyword_去首尾空白()
        => Assert.Equal("圣诞饰品", DynamicFollowUpDueReportRules.NormalizeKeyword("  圣诞饰品  "));

    [Fact]
    public void NormalizeKeyword_最多80字符通过()
        => Assert.Equal(new string('k', 80), DynamicFollowUpDueReportRules.NormalizeKeyword(new string('k', 80)));

    [Fact]
    public void NormalizeKeyword_超过80字符拒绝()
    {
        var ex = Assert.Throws<BusinessException>(
            () => DynamicFollowUpDueReportRules.NormalizeKeyword(new string('k', 81)));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public void 到期派生_逾期今日即将到期口径一致()
    {
        var item = new CustomerFollowUp();

        item.NextFollowDate = AsOf.AddDays(-2);
        Assert.Equal(2, DynamicFollowUpDueReportRules.DueDays(item, AsOf));
        Assert.Equal(DynamicFollowUpDueReportRules.DueOverdueText, DynamicFollowUpDueReportRules.DueStatusText(2));

        item.NextFollowDate = AsOf;
        Assert.Equal(0, DynamicFollowUpDueReportRules.DueDays(item, AsOf));
        Assert.Equal(DynamicFollowUpDueReportRules.DueTodayText, DynamicFollowUpDueReportRules.DueStatusText(0));

        item.NextFollowDate = AsOf.AddDays(3);
        Assert.Equal(-3, DynamicFollowUpDueReportRules.DueDays(item, AsOf));
        Assert.Equal(DynamicFollowUpDueReportRules.DueUpcomingText, DynamicFollowUpDueReportRules.DueStatusText(-3));
    }

    // ==================== 预览集成（内存库 + 授权 / 数据范围 / 分页 / 只读） ====================

    [Fact]
    public async Task 无身份_未认证拒绝()
    {
        using var db = TestDbFactory.Create();
        var ctl = BuildController(db, null);

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => ctl.Preview(new DynamicFollowUpDueReportRequest()));
        Assert.Equal(ErrorCodes.Unauthorized, ex.Code);
    }

    [Fact]
    public async Task 无菜单授权_权限不足拒绝()
    {
        using var db = TestDbFactory.Create();
        var role = SeedRole(db, "NoMenu");
        var user = SeedUser(db, "nommenu-user");
        SeedUserRole(db, user.Id, role.Id);

        var ctl = BuildController(db, user.Id);
        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => ctl.Preview(new DynamicFollowUpDueReportRequest()));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    [Fact]
    public async Task 预览_只投影选定字段且保持请求顺序()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv-user", "Priv", isSystemRole: true);
        var c = SeedCustomer(db, "C001", "客户");
        SeedFollowUp(db, "FU-1", c.Id, "客户", AsOf, subject: "s");

        var ctl = BuildController(db, user.Id);
        var page = OkPage(await ctl.Preview(new DynamicFollowUpDueReportRequest
        {
            Fields = new List<string> { "subject", "customerName", "dueStatus" },
            AsOfDate = AsOf,
            AheadDays = 7,
        }));

        Assert.Equal(new[] { "subject", "customerName", "dueStatus" }, page.Columns.Select(c => c.Key).ToArray());
        var row = Assert.Single(page.Rows);
        Assert.Equal(new[] { "subject", "customerName", "dueStatus" }, row.Keys.ToArray());
        Assert.Equal("s", (string)row["subject"]!);
        Assert.Equal(DynamicFollowUpDueReportRules.DueTodayText, (string)row["dueStatus"]!);
    }

    [Fact]
    public async Task 预览_稳定排序_逾期最久在前()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv-user", "Priv", isSystemRole: true);
        var c = SeedCustomer(db, "C001", "客户");
        SeedFollowUp(db, "FU-1", c.Id, "客户A", AsOf.AddDays(2), subject: "up2");
        SeedFollowUp(db, "FU-2", c.Id, "客户A", AsOf, subject: "today");
        SeedFollowUp(db, "FU-3", c.Id, "客户A", AsOf.AddDays(-5), subject: "od5");
        SeedFollowUp(db, "FU-4", c.Id, "客户B", AsOf.AddDays(-2), subject: "od2");

        var ctl = BuildController(db, user.Id);
        var page = OkPage(await ctl.Preview(new DynamicFollowUpDueReportRequest
        {
            Fields = new List<string> { "subject" },
            AsOfDate = AsOf,
            AheadDays = 7,
        }));

        Assert.Equal(new[] { "od5", "od2", "today", "up2" }, page.Rows.Select(r => (string)r["subject"]!).ToArray());
    }

    [Fact]
    public async Task 预览_到期状态筛选_只返回对应状态()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv-user", "Priv", isSystemRole: true);
        var c = SeedCustomer(db, "C001", "客户");
        SeedFollowUp(db, "FU-OD", c.Id, "客户", AsOf.AddDays(-1), subject: "od");
        SeedFollowUp(db, "FU-TD", c.Id, "客户", AsOf, subject: "td");
        SeedFollowUp(db, "FU-UP", c.Id, "客户", AsOf.AddDays(1), subject: "up");

        var ctl = BuildController(db, user.Id);

        var od = OkPage(await ctl.Preview(new DynamicFollowUpDueReportRequest
        {
            Fields = new List<string> { "subject" }, AsOfDate = AsOf, AheadDays = 7, DueStatus = "overdue"
        }));
        Assert.Equal("od", (string)Assert.Single(od.Rows)["subject"]!);

        var td = OkPage(await ctl.Preview(new DynamicFollowUpDueReportRequest
        {
            Fields = new List<string> { "subject" }, AsOfDate = AsOf, AheadDays = 7, DueStatus = "today"
        }));
        Assert.Equal("td", (string)Assert.Single(td.Rows)["subject"]!);

        var up = OkPage(await ctl.Preview(new DynamicFollowUpDueReportRequest
        {
            Fields = new List<string> { "subject" }, AsOfDate = AsOf, AheadDays = 7, DueStatus = "upcoming"
        }));
        Assert.Equal("up", (string)Assert.Single(up.Rows)["subject"]!);
    }

    [Fact]
    public async Task 预览_受限制业务员_只看到被分配客户_他人与空客户不可见()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "alice", "Sales");
        var employee = SeedEmployee(db, "alice");
        var mine = SeedCustomer(db, "C001", "我的客户", employee.Id);
        var other = SeedCustomer(db, "C002", "别人的客户", employee.Id + 1000);
        SeedFollowUp(db, "FU-MINE", mine.Id, "我的客户", AsOf, subject: "我的");
        SeedFollowUp(db, "FU-OTHER", other.Id, "别人的客户", AsOf, subject: "他人");
        SeedFollowUp(db, "FU-NULL", null, "匿名客户", AsOf, subject: "匿名");

        var ctl = BuildController(db, user.Id);
        var page = OkPage(await ctl.Preview(new DynamicFollowUpDueReportRequest
        {
            Fields = new List<string> { "customerName" }, AsOfDate = AsOf, AheadDays = 7
        }));

        var row = Assert.Single(page.Rows);
        Assert.Equal("我的客户", (string)row["customerName"]!);
        Assert.Equal(1, page.Total);
    }

    [Fact]
    public async Task 预览_重新分配客户_下一次请求立即收敛()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "alice", "Sales");
        var employee = SeedEmployee(db, "alice");
        var otherEmployee = SeedEmployee(db, "bob");
        var customer = SeedCustomer(db, "C001", "我的客户", employee.Id);
        SeedFollowUp(db, "FU-1", customer.Id, "我的客户", AsOf);

        var ctl = BuildController(db, user.Id);
        var first = OkPage(await ctl.Preview(new DynamicFollowUpDueReportRequest
        {
            Fields = new List<string> { "customerName" }, AsOfDate = AsOf, AheadDays = 7
        }));
        Assert.Equal(1, first.Total);

        customer.EmpId = otherEmployee.Id;   // 重新分配给其他业务员
        db.SaveChanges();

        var second = OkPage(await ctl.Preview(new DynamicFollowUpDueReportRequest
        {
            Fields = new List<string> { "customerName" }, AsOfDate = AsOf, AheadDays = 7
        }));
        Assert.Equal(0, second.Total);
        Assert.Empty(second.Rows);
    }

    [Fact]
    public async Task 预览_空页_显式EmptyText且Total为0()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv-user", "Priv", isSystemRole: true);

        var ctl = BuildController(db, user.Id);
        var page = OkPage(await ctl.Preview(new DynamicFollowUpDueReportRequest { AsOfDate = AsOf, AheadDays = 7 }));

        Assert.Equal(0, page.Total);
        Assert.Empty(page.Rows);
        Assert.False(page.Truncated);
        Assert.Equal(DynamicFollowUpDueReportRules.EmptyText, page.EmptyText);
    }

    [Fact]
    public async Task 预览_分页_total与truncated正确()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv-user", "Priv", isSystemRole: true);
        var c = SeedCustomer(db, "C001", "客户");
        for (var i = 0; i < 5; i++)
            SeedFollowUp(db, $"FU-{i}", c.Id, "客户", AsOf.AddDays(-i));

        var ctl = BuildController(db, user.Id);
        var page1 = OkPage(await ctl.Preview(new DynamicFollowUpDueReportRequest
        {
            Fields = new List<string> { "followNo" }, AsOfDate = AsOf, AheadDays = 7, Page = 1, PageSize = 2
        }));
        Assert.Equal(5, page1.Total);
        Assert.Equal(2, page1.Rows.Count);
        Assert.True(page1.Truncated);
        Assert.Equal(3, page1.TotalPages);

        var page3 = OkPage(await ctl.Preview(new DynamicFollowUpDueReportRequest
        {
            Fields = new List<string> { "followNo" }, AsOfDate = AsOf, AheadDays = 7, Page = 3, PageSize = 2
        }));
        Assert.Single(page3.Rows);
        Assert.False(page3.Truncated);
    }

    [Fact]
    public async Task 预览_只读_无待保存变更且不新增记录()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv-user", "Priv", isSystemRole: true);
        var c = SeedCustomer(db, "C001", "客户");
        SeedFollowUp(db, "FU-1", c.Id, "客户", AsOf);

        var before = db.CustomerFollowUps.Count();
        var ctl = BuildController(db, user.Id);
        _ = OkPage(await ctl.Preview(new DynamicFollowUpDueReportRequest { AsOfDate = AsOf, AheadDays = 7 }));

        Assert.Equal(before, db.CustomerFollowUps.Count());
        Assert.False(db.ChangeTracker.HasChanges());
    }

    // ==================== 页面分组计数（ERP-197） ====================

    [Theory]
    [InlineData("none", "none")]
    [InlineData("NONE", "none")]
    [InlineData("dueStatus", "dueStatus")]
    [InlineData("DueStatus", "dueStatus")]
    [InlineData("salesman", "salesman")]
    [InlineData("SALESMAN", "salesman")]
    public void NormalizeGroupBy_合法取值_大小写不敏感(string input, string expected)
    {
        Assert.Equal(expected, DynamicFollowUpDueReportRules.NormalizeGroupBy(input));
    }

    [Fact]
    public void NormalizeGroupBy_空为不分组()
    {
        Assert.Equal(DynamicFollowUpDueReportRules.GroupNone, DynamicFollowUpDueReportRules.NormalizeGroupBy(null));
        Assert.Equal(DynamicFollowUpDueReportRules.GroupNone, DynamicFollowUpDueReportRules.NormalizeGroupBy("  "));
    }

    [Theory]
    [InlineData("warehouse")]
    [InlineData("quarter")]
    [InlineData("due-status")]
    [InlineData("分组")]
    [InlineData("unknown")]
    public void NormalizeGroupBy_无效取值拒绝(string input)
    {
        var ex = Assert.Throws<BusinessException>(() => DynamicFollowUpDueReportRules.NormalizeGroupBy(input));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public async Task 预览_分组_到期状态_固定分类且保留空分类()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv-user", "Priv", isSystemRole: true);
        var c = SeedCustomer(db, "C001", "客户");
        SeedFollowUp(db, "FU-OD", c.Id, "客户", AsOf.AddDays(-1), subject: "逾期一条");

        var ctl = BuildController(db, user.Id);
        var page = OkPage(await ctl.Preview(new DynamicFollowUpDueReportRequest
        {
            Fields = new List<string> { "subject" }, AsOfDate = AsOf, AheadDays = 7, GroupBy = "dueStatus"
        }));

        Assert.Equal(DynamicFollowUpDueReportRules.GroupDueStatus, page.GroupBy);
        Assert.NotNull(page.Groups);
        Assert.Equal(new[] { "已逾期", "今日到期", "即将到期" }, page.Groups.Select(g => g.Label).ToArray());
        Assert.Equal(new[] { 1, 0, 0 }, page.Groups.Select(g => g.Count).ToArray());
    }

    [Fact]
    public async Task 预览_分组_业务员_未分配单独分桶且标签稳定()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv-user", "Priv", isSystemRole: true);
        var c = SeedCustomer(db, "C001", "客户");
        SeedFollowUp(db, "FU-A1", c.Id, "客户", AsOf, subject: "a1", salesmanName: "李四");
        SeedFollowUp(db, "FU-A2", c.Id, "客户", AsOf, subject: "a2", salesmanName: "李四");
        SeedFollowUp(db, "FU-B", c.Id, "客户", AsOf, subject: "b", salesmanName: "王五");
        SeedFollowUp(db, "FU-U", c.Id, "客户", AsOf, subject: "u", salesmanName: "");

        var ctl = BuildController(db, user.Id);
        var page = OkPage(await ctl.Preview(new DynamicFollowUpDueReportRequest
        {
            Fields = new List<string> { "subject" }, AsOfDate = AsOf, AheadDays = 7, GroupBy = "salesman"
        }));

        Assert.Equal(DynamicFollowUpDueReportRules.GroupSalesman, page.GroupBy);
        Assert.NotNull(page.Groups);
        Assert.Equal(3, page.Groups.Count);
        Assert.Contains(page.Groups, g => g.Label == "李四" && g.Count == 2);
        Assert.Contains(page.Groups, g => g.Label == "王五" && g.Count == 1);
        var unassigned = Assert.Single(page.Groups, g => g.Key.EndsWith(":unassigned"));
        Assert.Equal(DynamicFollowUpDueReportRules.UnassignedSalesmanText, unassigned.Label);
        Assert.Equal(1, unassigned.Count);
    }

    [Fact]
    public async Task 预览_分组_只统计当前页_绝不外推整表总数()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv-user", "Priv", isSystemRole: true);
        var c = SeedCustomer(db, "C001", "客户");
        for (var i = 0; i < 5; i++)
            SeedFollowUp(db, $"FU-{i}", c.Id, "客户", AsOf.AddDays(-(i + 1)), subject: $"逾期{i}");

        var ctl = BuildController(db, user.Id);

        var page1 = OkPage(await ctl.Preview(new DynamicFollowUpDueReportRequest
        {
            Fields = new List<string> { "subject" }, AsOfDate = AsOf, AheadDays = 7, GroupBy = "dueStatus", Page = 1, PageSize = 2
        }));
        Assert.Equal(5, page1.Total); // 整表口径总数仍为 5，分组计数只统计本页
        Assert.NotNull(page1.Groups);
        Assert.Equal(2, Assert.Single(page1.Groups, g => g.Label == "已逾期").Count);

        var page3 = OkPage(await ctl.Preview(new DynamicFollowUpDueReportRequest
        {
            Fields = new List<string> { "subject" }, AsOfDate = AsOf, AheadDays = 7, GroupBy = "dueStatus", Page = 3, PageSize = 2
        }));
        Assert.NotNull(page3.Groups);
        Assert.Equal(1, Assert.Single(page3.Groups, g => g.Label == "已逾期").Count);
    }

    [Fact]
    public async Task 预览_分组_none_不返回分组计数()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv-user", "Priv", isSystemRole: true);
        var c = SeedCustomer(db, "C001", "客户");
        SeedFollowUp(db, "FU-1", c.Id, "客户", AsOf);

        var ctl = BuildController(db, user.Id);
        var page = OkPage(await ctl.Preview(new DynamicFollowUpDueReportRequest
        {
            Fields = new List<string> { "subject" }, AsOfDate = AsOf, AheadDays = 7
        }));

        Assert.Equal(DynamicFollowUpDueReportRules.GroupNone, page.GroupBy);
        Assert.NotNull(page.Groups);
        Assert.Empty(page.Groups);
    }

    [Fact]
    public async Task 预览_分组_无效键_读取前拒绝且不写库()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv-user", "Priv", isSystemRole: true);
        var c = SeedCustomer(db, "C001", "客户");
        SeedFollowUp(db, "FU-1", c.Id, "客户", AsOf);

        var before = db.CustomerFollowUps.Count();
        var ctl = BuildController(db, user.Id);
        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => ctl.Preview(new DynamicFollowUpDueReportRequest
            {
                Fields = new List<string> { "subject" }, AsOfDate = AsOf, AheadDays = 7, GroupBy = "warehouse"
            }));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Equal(before, db.CustomerFollowUps.Count());
        Assert.False(db.ChangeTracker.HasChanges());
    }

    [Fact]
    public async Task 预览_分组_受限制业务员_范围外行不进入分组计数()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "alice", "Sales");
        var employee = SeedEmployee(db, "alice");
        var otherEmployee = SeedEmployee(db, "bob");
        var mine = SeedCustomer(db, "C001", "我的客户", employee.Id);
        var other = SeedCustomer(db, "C002", "别人的客户", otherEmployee.Id);
        SeedFollowUp(db, "FU-MINE", mine.Id, "我的客户", AsOf, subject: "我的", salesmanName: "张三");
        SeedFollowUp(db, "FU-OTHER", other.Id, "别人的客户", AsOf, subject: "他人", salesmanName: "李四");

        var ctl = BuildController(db, user.Id);
        var page = OkPage(await ctl.Preview(new DynamicFollowUpDueReportRequest
        {
            Fields = new List<string> { "customerName" }, AsOfDate = AsOf, AheadDays = 7, GroupBy = "salesman"
        }));

        var row = Assert.Single(page.Rows);
        Assert.Equal("我的客户", (string)row["customerName"]!);
        Assert.Equal(1, page.Total);
        Assert.NotNull(page.Groups);
        Assert.Contains(page.Groups, g => g.Label == "张三" && g.Count == 1);
        Assert.DoesNotContain(page.Groups, g => g.Label == "李四");
    }

    // ==================== 客户 Id / 关键字筛选（ERP-200） ====================

    [Fact]
    public async Task 预览_客户Id筛选_只返回该客户记录()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv-user", "Priv", isSystemRole: true);
        var c1 = SeedCustomer(db, "C001", "客户一");
        var c2 = SeedCustomer(db, "C002", "客户二");
        SeedFollowUp(db, "FU-1", c1.Id, "客户一", AsOf, subject: "主题A");
        SeedFollowUp(db, "FU-2", c2.Id, "客户二", AsOf, subject: "主题B");

        var ctl = BuildController(db, user.Id);
        var page = OkPage(await ctl.Preview(new DynamicFollowUpDueReportRequest
        {
            Fields = new List<string> { "customerName" }, AsOfDate = AsOf, AheadDays = 7, CustomerId = c2.Id
        }));

        var row = Assert.Single(page.Rows);
        Assert.Equal("客户二", (string)row["customerName"]!);
        Assert.Equal(1, page.Total);
    }

    [Fact]
    public async Task 预览_关键字筛选_匹配客户名称或跟进主题()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv-user", "Priv", isSystemRole: true);
        var c1 = SeedCustomer(db, "C001", "义乌A公司");
        var c2 = SeedCustomer(db, "C002", "广州B公司");
        var c3 = SeedCustomer(db, "C003", "义乌C公司");
        SeedFollowUp(db, "FU-1", c1.Id, "义乌A公司", AsOf, subject: "圣诞饰品");
        SeedFollowUp(db, "FU-2", c2.Id, "广州B公司", AsOf, subject: "义乌小商品");
        SeedFollowUp(db, "FU-3", c3.Id, "义乌C公司", AsOf, subject: "春交会");

        var ctl = BuildController(db, user.Id);
        var byName = OkPage(await ctl.Preview(new DynamicFollowUpDueReportRequest
        {
            Fields = new List<string> { "customerName" }, AsOfDate = AsOf, AheadDays = 7, Keyword = "义乌"
        }));
        Assert.Equal(3, byName.Total);

        var bySubject = OkPage(await ctl.Preview(new DynamicFollowUpDueReportRequest
        {
            Fields = new List<string> { "customerName" }, AsOfDate = AsOf, AheadDays = 7, Keyword = "圣诞"
        }));
        var row = Assert.Single(bySubject.Rows);
        Assert.Equal("义乌A公司", (string)row["customerName"]!);
    }

    [Fact]
    public async Task 预览_关键字筛选_受限制业务员不泄露未分配客户()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "alice", "Sales");
        var employee = SeedEmployee(db, "alice");
        var otherEmployee = SeedEmployee(db, "bob");
        var mine = SeedCustomer(db, "C001", "我的客户", employee.Id);
        var other = SeedCustomer(db, "C002", "别人的客户", otherEmployee.Id);
        SeedFollowUp(db, "FU-MINE", mine.Id, "我的客户", AsOf, subject: "机密报价");
        SeedFollowUp(db, "FU-OTHER", other.Id, "别人的客户", AsOf, subject: "机密报价");

        var ctl = BuildController(db, user.Id);
        var page = OkPage(await ctl.Preview(new DynamicFollowUpDueReportRequest
        {
            Fields = new List<string> { "customerName", "subject" }, AsOfDate = AsOf, AheadDays = 7, Keyword = "机密"
        }));

        var row = Assert.Single(page.Rows);
        Assert.Equal("我的客户", (string)row["customerName"]!);
        Assert.Equal(1, page.Total);
    }

    [Fact]
    public async Task 预览_客户Id非正整数_读取前拒绝且不写库()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv-user", "Priv", isSystemRole: true);
        var c = SeedCustomer(db, "C001", "客户");
        SeedFollowUp(db, "FU-1", c.Id, "客户", AsOf);

        var before = db.CustomerFollowUps.Count();
        var ctl = BuildController(db, user.Id);
        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => ctl.Preview(new DynamicFollowUpDueReportRequest
            {
                AsOfDate = AsOf, AheadDays = 7, CustomerId = 0
            }));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Equal(before, db.CustomerFollowUps.Count());
        Assert.False(db.ChangeTracker.HasChanges());
    }

    [Fact]
    public async Task 预览_关键字超过80字符_读取前拒绝且不写库()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv-user", "Priv", isSystemRole: true);
        var c = SeedCustomer(db, "C001", "客户");
        SeedFollowUp(db, "FU-1", c.Id, "客户", AsOf);

        var before = db.CustomerFollowUps.Count();
        var ctl = BuildController(db, user.Id);
        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => ctl.Preview(new DynamicFollowUpDueReportRequest
            {
                AsOfDate = AsOf, AheadDays = 7, Keyword = new string('k', 81)
            }));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Equal(before, db.CustomerFollowUps.Count());
        Assert.False(db.ChangeTracker.HasChanges());
    }
}
