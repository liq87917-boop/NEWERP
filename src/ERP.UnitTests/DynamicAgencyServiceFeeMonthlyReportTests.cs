using System.Reflection;
using System.Security.Claims;
using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-181 动态代理服务费月度汇总报表（只读、有界）单元测试。
/// 覆盖：字段白名单目录与顺序、选定列投影、无效 / 重复字段、分页边界、混合币种、状态金额口径、
/// 业务员数据范围、无身份 / 无菜单授权 fail closed、只读不写库、接口路由与前端接线。
/// 全部使用内存数据库（TestDbFactory），不连接 SQL Server、不启动 API、不执行任何 SQL / seed。
/// </summary>
public class DynamicAgencyServiceFeeMonthlyReportTests
{
    private static string RepoFile(params string[] segments)
        => Path.GetFullPath(Path.Combine(
            new[] { AppContext.BaseDirectory, "..", "..", "..", "..", ".." }.Concat(segments).ToArray()));

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

    /// <summary>播种一个「客户资料菜单授权 + 系统内置角色（特权）」的登录用户并返回其用户 Id（特权 → 不过滤客户）</summary>
    private static long SeedAuthorizedUser(ErpDbContext db, string userName = "dyn-user")
    {
        var user = SeedUser(db, userName);
        var role = SeedRole(db, $"Role-{userName}", isSystem: true);
        SeedUserRole(db, user.Id, role.Id);
        SeedRoleMenu(db, role.Id, SeedMenu(db, DynamicAgencyServiceFeeMonthlyReportRules.RequiredMenuCode).Id);
        return user.Id;
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

    private static BaseCustomer SeedCustomer(ErpDbContext db, string code, string name, long? empId = null)
    {
        var customer = new BaseCustomer
        {
            CustomerCode = code,
            CustomerName = name,
            Status = 1,
            CreditStatus = "正常",
            CreditLimit = 100000m,
            CreditDays = 30,
            EmpId = empId,
            IsDeleted = false
        };
        db.BaseCustomers.Add(customer);
        db.SaveChanges();
        return customer;
    }

    private static AgencyServiceFeeStatement SeedStatement(
        ErpDbContext db, string statementNo, long customerId, decimal totalAmount,
        string currency = "USD", int status = AgencyServiceFeeStatementRules.StatusRecorded,
        DateTime? statementDate = null, bool deleted = false,
        string customerCode = "C001", string customerName = "义乌进出口")
    {
        var statement = new AgencyServiceFeeStatement
        {
            StatementNo = statementNo,
            NormalizedStatementNo = AgencyServiceFeeStatementRules.NormalizeIdentityPart(statementNo),
            CustomerId = customerId,
            CustomerCode = customerCode,
            CustomerName = customerName,
            Currency = currency,
            StatementDate = statementDate ?? new DateTime(2026, 9, 1),
            ServicePeriodFrom = new DateTime(2026, 8, 1),
            ServicePeriodTo = new DateTime(2026, 8, 31),
            TotalAmount = totalAmount,
            Status = status,
            RecordedAt = status == AgencyServiceFeeStatementRules.StatusRecorded
                ? new DateTime(2026, 9, 2)
                : null,
            RecordedBy = status == AgencyServiceFeeStatementRules.StatusRecorded ? "张三" : string.Empty,
            IsDeleted = deleted
        };
        db.AgencyServiceFeeStatements.Add(statement);
        db.SaveChanges();
        return statement;
    }

    private static DynamicAgencyServiceFeeMonthlyReportController BuildController(ErpDbContext db, long? userId)
    {
        var ctl = new DynamicAgencyServiceFeeMonthlyReportController(db);
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

    private static DynamicAgencyServiceFeeMonthlyReportPageDto PreviewOk(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<ApiResponse<DynamicAgencyServiceFeeMonthlyReportPageDto>>(ok.Value);
        Assert.NotNull(resp.Data);
        return resp.Data!;
    }

    private static DynamicAgencyServiceFeeMonthlyReportCatalogDto CatalogOk(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<ApiResponse<DynamicAgencyServiceFeeMonthlyReportCatalogDto>>(ok.Value);
        Assert.NotNull(resp.Data);
        return resp.Data!;
    }

    // ==================== 1. 目录 ====================

    [Fact]
    public async Task 目录_返回有限白名单_字段顺序与目录一致()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedAuthorizedUser(db);
        var ctl = BuildController(db, uid);

        var catalog = CatalogOk(await ctl.Catalog());

        Assert.Equal(DynamicAgencyServiceFeeMonthlyReportRules.AllFieldKeys, catalog.Fields.Select(f => f.Key));
        Assert.Equal(DynamicAgencyServiceFeeMonthlyReportRules.RequiredMenuCode, catalog.RequiredMenuCode);
        Assert.Equal(DynamicAgencyServiceFeeMonthlyReportRules.MaxPageSize, catalog.MaxPageSize);
        Assert.False(string.IsNullOrWhiteSpace(catalog.EvidenceOnlyText));
        Assert.False(string.IsNullOrWhiteSpace(catalog.ReadOnlyText));
    }

    [Fact]
    public async Task 目录_无身份_未认证拒绝()
    {
        using var db = TestDbFactory.Create();
        var ctl = BuildController(db, null);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Catalog());
        Assert.Equal(ErrorCodes.Unauthorized, ex.Code);
    }

    [Fact]
    public async Task 目录_无客户资料菜单授权_拒绝()
    {
        using var db = TestDbFactory.Create();
        var user = SeedUser(db, "nomenu");
        var role = SeedRole(db, "Role-nomenu");
        SeedUserRole(db, user.Id, role.Id);
        var ctl = BuildController(db, user.Id);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Catalog());
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    // ==================== 2. 字段投影 ====================

    [Fact]
    public async Task 预览_仅返回选定字段_保持请求顺序()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedAuthorizedUser(db);
        var c1 = SeedCustomer(db, "C001", "客户一");
        SeedStatement(db, "ASF-1", c1.Id, 100m, currency: "USD", customerCode: "C001", customerName: "客户一");

        var ctl = BuildController(db, uid);
        var page = PreviewOk(await ctl.Preview(new DynamicAgencyServiceFeeMonthlyReportRequest
        {
            Fields = new() { "statementMonthText", "customerName", "registeredTotalAmount" },
            PageSize = 10
        }));

        Assert.Equal(
            new[] { "statementMonthText", "customerName", "registeredTotalAmount" },
            page.Columns.Select(c => c.Key));
        var row = Assert.Single(page.Rows);
        Assert.Equal(3, row.Count);
        Assert.Equal("2026-09", (string)row["statementMonthText"]!);
        Assert.Equal("客户一", (string)row["customerName"]!);
        Assert.Equal(100m, (decimal)row["registeredTotalAmount"]!);
    }

    [Fact]
    public async Task 预览_未知字段_拒绝()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedAuthorizedUser(db);
        var ctl = BuildController(db, uid);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Preview(
            new DynamicAgencyServiceFeeMonthlyReportRequest { Fields = new() { "hackerColumn" } }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public async Task 预览_重复字段_拒绝()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedAuthorizedUser(db);
        var ctl = BuildController(db, uid);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Preview(
            new DynamicAgencyServiceFeeMonthlyReportRequest { Fields = new() { "currency", "currency" } }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    // ==================== 3. 分页边界 ====================

    [Fact]
    public async Task 预览_页码越界_拒绝()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedAuthorizedUser(db);
        var ctl = BuildController(db, uid);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Preview(
            new DynamicAgencyServiceFeeMonthlyReportRequest { Page = 0 }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public async Task 预览_每页条数越界_拒绝()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedAuthorizedUser(db);
        var ctl = BuildController(db, uid);

        var ex1 = await Assert.ThrowsAsync<BusinessException>(() => ctl.Preview(
            new DynamicAgencyServiceFeeMonthlyReportRequest { PageSize = 201 }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex1.Code);

        var ex2 = await Assert.ThrowsAsync<BusinessException>(() => ctl.Preview(
            new DynamicAgencyServiceFeeMonthlyReportRequest { PageSize = 0 }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex2.Code);
    }

    // ==================== 4. 币种隔离 ====================

    [Fact]
    public async Task 预览_混合币种_原币分别成组_无跨币种总额()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedAuthorizedUser(db);
        var c1 = SeedCustomer(db, "C001", "客户一");
        SeedStatement(db, "ASF-USD", c1.Id, 100m, currency: "USD");
        SeedStatement(db, "ASF-JPY", c1.Id, 1200m, currency: "JPY");

        var ctl = BuildController(db, uid);
        var page = PreviewOk(await ctl.Preview(new DynamicAgencyServiceFeeMonthlyReportRequest
        {
            Fields = new() { "currency", "registeredTotalAmount" },
            PageSize = 100
        }));

        Assert.Equal(2, page.Total);
        var usd = Assert.Single(page.Rows, r => (string)r["currency"]! == "USD");
        var jpy = Assert.Single(page.Rows, r => (string)r["currency"]! == "JPY");
        Assert.Equal(100m, (decimal)usd["registeredTotalAmount"]!);
        Assert.Equal(1200m, (decimal)jpy["registeredTotalAmount"]!);
    }

    // ==================== 5. 状态金额口径 ====================

    [Fact]
    public async Task 预览_状态金额口径_草稿与已作废不计入原币合计()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedAuthorizedUser(db);
        var c1 = SeedCustomer(db, "C001", "客户一");
        SeedStatement(db, "ASF-R", c1.Id, 100m, status: AgencyServiceFeeStatementRules.StatusRecorded);
        SeedStatement(db, "ASF-D", c1.Id, 30m, status: AgencyServiceFeeStatementRules.StatusDraft);
        SeedStatement(db, "ASF-V", c1.Id, 20m, status: AgencyServiceFeeStatementRules.StatusVoided);

        var ctl = BuildController(db, uid);
        var page = PreviewOk(await ctl.Preview(new DynamicAgencyServiceFeeMonthlyReportRequest
        {
            Fields = new()
            {
                "registeredTotalAmount", "draftTotalAmount", "voidedTotalAmount",
                "registeredCount", "draftCount", "voidedCount", "statementCount",
            },
            PageSize = 10
        }));

        var row = Assert.Single(page.Rows);
        Assert.Equal(100m, (decimal)row["registeredTotalAmount"]!);
        Assert.Equal(30m, (decimal)row["draftTotalAmount"]!);
        Assert.Equal(20m, (decimal)row["voidedTotalAmount"]!);
        Assert.Equal(1, (int)row["registeredCount"]!);
        Assert.Equal(1, (int)row["draftCount"]!);
        Assert.Equal(1, (int)row["voidedCount"]!);
        Assert.Equal(3, (int)row["statementCount"]!);
    }

    // ==================== 6. 业务员数据范围 ====================

    [Fact]
    public async Task 预览_受限制业务员_只看到其被分配客户()
    {
        using var db = TestDbFactory.Create();
        var user = SeedUser(db, "alice");
        var role = SeedRole(db, "Role-alice");
        SeedUserRole(db, user.Id, role.Id);
        SeedRoleMenu(db, role.Id, SeedMenu(db, DynamicAgencyServiceFeeMonthlyReportRules.RequiredMenuCode).Id);

        var employee = SeedEmployee(db, "alice");
        var mine = SeedCustomer(db, "C001", "我的客户", employee.Id);
        var other = SeedCustomer(db, "C002", "别人的客户", employee.Id + 1000);
        SeedStatement(db, "ASF-MY", mine.Id, 100m, customerCode: "C001", customerName: "我的客户");
        SeedStatement(db, "ASF-OTHER", other.Id, 999m, customerCode: "C002", customerName: "别人的客户");

        var ctl = BuildController(db, user.Id);
        var page = PreviewOk(await ctl.Preview(new DynamicAgencyServiceFeeMonthlyReportRequest
        {
            Fields = new() { "customerId" },
            PageSize = 100
        }));

        Assert.Equal(1, page.Total);
        Assert.Contains(page.Rows, r => (long)r["customerId"]! == mine.Id);
        Assert.DoesNotContain(page.Rows, r => (long)r["customerId"]! == other.Id);
    }

    // ==================== 7. 只读 ====================

    [Fact]
    public async Task 预览_只读不写库()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedAuthorizedUser(db);
        var c1 = SeedCustomer(db, "C001", "客户一");
        SeedStatement(db, "ASF-1", c1.Id, 100m);

        var ctl = BuildController(db, uid);
        var page = PreviewOk(await ctl.Preview(new DynamicAgencyServiceFeeMonthlyReportRequest
        {
            Fields = new() { "customerName" },
            PageSize = 10
        }));
        Assert.Equal(1, page.Total);

        Assert.DoesNotContain(db.ChangeTracker.Entries(), e => e.State != EntityState.Unchanged);
    }

    // ==================== 8. 接口路由与前端接线 ====================

    [Fact]
    public void 接口_路由契约()
    {
        var route = typeof(DynamicAgencyServiceFeeMonthlyReportController)
            .GetCustomAttribute<RouteAttribute>(true)!.Template;
        Assert.Equal("api/dynamic-agency-service-fee-monthly-report", route);

        var get = typeof(DynamicAgencyServiceFeeMonthlyReportController)
            .GetMethod(nameof(DynamicAgencyServiceFeeMonthlyReportController.Catalog))!
            .GetCustomAttributes<HttpGetAttribute>(true).ToList();
        Assert.Contains(get, a => a.Template == null);

        var post = typeof(DynamicAgencyServiceFeeMonthlyReportController)
            .GetMethod(nameof(DynamicAgencyServiceFeeMonthlyReportController.Preview))!
            .GetCustomAttributes<HttpPostAttribute>(true).ToList();
        Assert.Contains(post, a => a.Template == null);
    }

    [Fact]
    public void 前端接线_字段目录与预览接口路径齐备()
    {
        var js = File.ReadAllText(RepoFile("src", "ERP.Api", "wwwroot", "js", "agency-service-fee-monthly-summary.js"));
        Assert.Contains("/api/dynamic-agency-service-fee-monthly-report", js);
        Assert.Contains("function asfmsPreview(", js);
        Assert.Contains("asfms-dyn-field", js);
        Assert.Contains("registeredTotalAmountText", js);
    }
}
