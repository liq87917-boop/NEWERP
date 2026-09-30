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
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-184 动态代理服务费月度汇总报表「当前页按对账月份 / 客户分组计数」单元测试。
/// <para>语义：<see cref="DynamicAgencyServiceFeeMonthlyReportRules.BuildGroupCounts"/> 只对「当前授权预览页」的
/// ERP-180 月度汇总行计数（RowCount）并分项累计已登记 / 草稿 / 已作废 / 总计张数，原币严格隔离、
/// 绝不跨币种合并，也绝不声明整份报表或会计合计。分组键（none / month / customer）在读取任何源数据之前校验，
/// 未知取值 fail closed。</para>
/// <para>覆盖：按月份 / 按客户分组、混合币种、隐藏维度列、业务员数据范围（撤销范围）、无身份 / 无菜单授权 fail closed、
/// 无效分组键、空页、截断页与只读不写库。全部使用内存数据库（TestDbFactory），不连接 SQL Server、不执行任何 SQL。</para>
/// </summary>
public class DynamicAgencyServiceFeeMonthlyGroupCountTests
{
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

    // ==================== 1. 分组键规范化（纯规则） ====================

    [Theory]
    [InlineData("none", DynamicAgencyServiceFeeMonthlyReportRules.GroupByNone)]
    [InlineData("NONE", DynamicAgencyServiceFeeMonthlyReportRules.GroupByNone)]
    [InlineData(" month ", DynamicAgencyServiceFeeMonthlyReportRules.GroupByMonth)]
    [InlineData("Customer", DynamicAgencyServiceFeeMonthlyReportRules.GroupByCustomer)]
    [InlineData("", DynamicAgencyServiceFeeMonthlyReportRules.GroupByNone)]
    [InlineData(null, DynamicAgencyServiceFeeMonthlyReportRules.GroupByNone)]
    public void 分组键规范化_大小写与空白容错(string? input, string expected)
    {
        Assert.Equal(expected, DynamicAgencyServiceFeeMonthlyReportRules.NormalizeGroupBy(input));
    }

    // ==================== 2. 按月份分组 ====================

    [Fact]
    public async Task 按月份分组_隐藏维度列_汇总行数与各状态张数()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedAuthorizedUser(db);
        var c1 = SeedCustomer(db, "C001", "客户一");
        var c2 = SeedCustomer(db, "C002", "客户二");
        SeedStatement(db, "ASF-1", c1.Id, 100m, statementDate: new DateTime(2026, 8, 15));
        SeedStatement(db, "ASF-2", c1.Id, 30m, status: AgencyServiceFeeStatementRules.StatusDraft,
            statementDate: new DateTime(2026, 8, 15));
        SeedStatement(db, "ASF-3", c2.Id, 20m, status: AgencyServiceFeeStatementRules.StatusVoided,
            statementDate: new DateTime(2026, 9, 1));

        var ctl = BuildController(db, uid);
        var page = PreviewOk(await ctl.Preview(new DynamicAgencyServiceFeeMonthlyReportRequest
        {
            Fields = new() { "registeredTotalAmount" }, // 不选择月份 / 客户维度列
            GroupBy = DynamicAgencyServiceFeeMonthlyReportRules.GroupByMonth,
            PageSize = 100,
        }));

        Assert.Equal(DynamicAgencyServiceFeeMonthlyReportRules.GroupByMonth, page.GroupBy);
        Assert.Equal(2, page.GroupCounts.Count);

        var aug = Assert.Single(page.GroupCounts, g => g.StatementMonth == 8);
        Assert.Equal(DynamicAgencyServiceFeeMonthlyReportRules.GroupByMonth, aug.GroupBy);
        Assert.Equal(2026, aug.StatementYear);
        Assert.Equal("2026-08", aug.StatementMonthText);
        Assert.Equal("USD", aug.Currency);
        Assert.Null(aug.CustomerId);
        Assert.Equal(1, aug.RowCount);
        Assert.Equal(1, aug.RegisteredCount);
        Assert.Equal(1, aug.DraftCount);
        Assert.Equal(0, aug.VoidedCount);
        Assert.Equal(2, aug.StatementCount);

        var sep = Assert.Single(page.GroupCounts, g => g.StatementMonth == 9);
        Assert.Equal(1, sep.RowCount);
        Assert.Equal(0, sep.RegisteredCount);
        Assert.Equal(0, sep.DraftCount);
        Assert.Equal(1, sep.VoidedCount);
        Assert.Equal(1, sep.StatementCount);
    }

    // ==================== 3. 按客户分组 ====================

    [Fact]
    public async Task 按客户分组_隐藏维度列_仍可识别客户与原币()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedAuthorizedUser(db);
        var c1 = SeedCustomer(db, "C001", "客户一");
        var c2 = SeedCustomer(db, "C002", "客户二");
        SeedStatement(db, "ASF-1", c1.Id, 100m, customerCode: "C001", customerName: "客户一",
            statementDate: new DateTime(2026, 8, 15));
        SeedStatement(db, "ASF-2", c1.Id, 30m, status: AgencyServiceFeeStatementRules.StatusDraft,
            customerCode: "C001", customerName: "客户一", statementDate: new DateTime(2026, 9, 1));
        SeedStatement(db, "ASF-3", c2.Id, 20m, status: AgencyServiceFeeStatementRules.StatusVoided,
            customerCode: "C002", customerName: "客户二", statementDate: new DateTime(2026, 8, 15));

        var ctl = BuildController(db, uid);
        var page = PreviewOk(await ctl.Preview(new DynamicAgencyServiceFeeMonthlyReportRequest
        {
            Fields = new() { "statementMonthText" }, // 不选择客户维度列
            GroupBy = DynamicAgencyServiceFeeMonthlyReportRules.GroupByCustomer,
            PageSize = 100,
        }));

        Assert.Equal(DynamicAgencyServiceFeeMonthlyReportRules.GroupByCustomer, page.GroupBy);
        Assert.Equal(2, page.GroupCounts.Count);

        var c1g = Assert.Single(page.GroupCounts, g => g.CustomerId == c1.Id);
        Assert.Null(c1g.StatementMonth);
        Assert.Equal("C001", c1g.CustomerCode);
        Assert.Equal("客户一", c1g.CustomerName);
        Assert.Equal("USD", c1g.Currency);
        Assert.Equal(2, c1g.RowCount);
        Assert.Equal(1, c1g.RegisteredCount);
        Assert.Equal(1, c1g.DraftCount);
        Assert.Equal(0, c1g.VoidedCount);
        Assert.Equal(2, c1g.StatementCount);

        var c2g = Assert.Single(page.GroupCounts, g => g.CustomerId == c2.Id);
        Assert.Equal(1, c2g.RowCount);
        Assert.Equal(0, c2g.RegisteredCount);
        Assert.Equal(0, c2g.DraftCount);
        Assert.Equal(1, c2g.VoidedCount);
        Assert.Equal(1, c2g.StatementCount);
    }

    // ==================== 4. 币种隔离 ====================

    [Fact]
    public async Task 混合币种_分组绝不跨币种合并()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedAuthorizedUser(db);
        var c1 = SeedCustomer(db, "C001", "客户一");
        SeedStatement(db, "ASF-USD", c1.Id, 100m, currency: "USD", statementDate: new DateTime(2026, 8, 15));
        SeedStatement(db, "ASF-JPY", c1.Id, 1200m, currency: "JPY", statementDate: new DateTime(2026, 8, 15));

        var ctl = BuildController(db, uid);
        var page = PreviewOk(await ctl.Preview(new DynamicAgencyServiceFeeMonthlyReportRequest
        {
            Fields = new() { "currency" },
            GroupBy = DynamicAgencyServiceFeeMonthlyReportRules.GroupByMonth,
            PageSize = 100,
        }));

        Assert.Equal(2, page.GroupCounts.Count);
        Assert.Contains(page.GroupCounts, g => g.StatementMonth == 8 && g.Currency == "USD");
        Assert.Contains(page.GroupCounts, g => g.StatementMonth == 8 && g.Currency == "JPY");
        Assert.DoesNotContain(page.GroupCounts, g => g.Currency == "USD,JPY");
    }

    // ==================== 5. 不分组 / 无效 / 空页 / 截断 ====================

    [Fact]
    public async Task 不分组_分组计数为空()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedAuthorizedUser(db);
        var c1 = SeedCustomer(db, "C001", "客户一");
        SeedStatement(db, "ASF-1", c1.Id, 100m);

        var ctl = BuildController(db, uid);
        var page = PreviewOk(await ctl.Preview(new DynamicAgencyServiceFeeMonthlyReportRequest
        {
            Fields = new() { "currency" },
            PageSize = 100,
        }));

        Assert.Equal(DynamicAgencyServiceFeeMonthlyReportRules.GroupByNone, page.GroupBy);
        Assert.Empty(page.GroupCounts);
    }

    [Fact]
    public async Task 无效分组键_拒绝()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedAuthorizedUser(db);
        var ctl = BuildController(db, uid);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Preview(
            new DynamicAgencyServiceFeeMonthlyReportRequest { GroupBy = "quarter" }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public async Task 空页_分组计数为空()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedAuthorizedUser(db);
        var ctl = BuildController(db, uid);

        var page = PreviewOk(await ctl.Preview(new DynamicAgencyServiceFeeMonthlyReportRequest
        {
            Fields = new() { "currency" },
            GroupBy = DynamicAgencyServiceFeeMonthlyReportRules.GroupByMonth,
            PageSize = 100,
        }));

        Assert.Equal(0, page.Total);
        Assert.Empty(page.GroupCounts);
        Assert.False(string.IsNullOrWhiteSpace(page.EmptyText));
    }

    [Fact]
    public async Task 截断页_仅统计当前页_标注截断与证据口径()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedAuthorizedUser(db);
        var c1 = SeedCustomer(db, "C001", "客户一");
        var c2 = SeedCustomer(db, "C002", "客户二");
        SeedStatement(db, "ASF-1", c1.Id, 100m, statementDate: new DateTime(2026, 8, 15));
        SeedStatement(db, "ASF-2", c1.Id, 100m, statementDate: new DateTime(2026, 9, 1));
        SeedStatement(db, "ASF-3", c2.Id, 100m, statementDate: new DateTime(2026, 8, 15));
        SeedStatement(db, "ASF-4", c2.Id, 100m, statementDate: new DateTime(2026, 9, 1));

        var ctl = BuildController(db, uid);
        var page = PreviewOk(await ctl.Preview(new DynamicAgencyServiceFeeMonthlyReportRequest
        {
            Fields = new() { "currency" },
            GroupBy = DynamicAgencyServiceFeeMonthlyReportRules.GroupByCustomer,
            PageSize = 2,
        }));

        Assert.Equal(4, page.Total);
        Assert.True(page.Truncated);
        Assert.Equal(2, page.Rows.Count);
        // 分组计数只统计当前页行，绝不扩大到整份报表
        Assert.Equal(page.Rows.Count, page.GroupCounts.Sum(g => g.RowCount));
        Assert.False(string.IsNullOrWhiteSpace(page.GroupCountScopeText));
    }

    // ==================== 6. 数据范围 / 授权 / 只读 ====================

    [Fact]
    public async Task 受限制业务员_分组计数只含被分配客户()
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
            GroupBy = DynamicAgencyServiceFeeMonthlyReportRules.GroupByCustomer,
            PageSize = 100,
        }));

        Assert.Equal(1, page.Total);
        var g = Assert.Single(page.GroupCounts);
        Assert.Equal(mine.Id, g.CustomerId);
        Assert.DoesNotContain(page.GroupCounts, x => x.CustomerId == other.Id);
    }

    [Fact]
    public async Task 无菜单授权_分组预览拒绝()
    {
        using var db = TestDbFactory.Create();
        var user = SeedUser(db, "nomenu");
        var role = SeedRole(db, "Role-nomenu");
        SeedUserRole(db, user.Id, role.Id);
        var ctl = BuildController(db, user.Id);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Preview(
            new DynamicAgencyServiceFeeMonthlyReportRequest
            {
                GroupBy = DynamicAgencyServiceFeeMonthlyReportRules.GroupByCustomer
            }));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    [Fact]
    public async Task 无身份_分组预览拒绝()
    {
        using var db = TestDbFactory.Create();
        var ctl = BuildController(db, null);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Preview(
            new DynamicAgencyServiceFeeMonthlyReportRequest
            {
                GroupBy = DynamicAgencyServiceFeeMonthlyReportRules.GroupByMonth
            }));
        Assert.Equal(ErrorCodes.Unauthorized, ex.Code);
    }

    [Fact]
    public async Task 分组预览_只读不写库()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedAuthorizedUser(db);
        var c1 = SeedCustomer(db, "C001", "客户一");
        SeedStatement(db, "ASF-1", c1.Id, 100m);

        var ctl = BuildController(db, uid);
        var page = PreviewOk(await ctl.Preview(new DynamicAgencyServiceFeeMonthlyReportRequest
        {
            Fields = new() { "currency" },
            GroupBy = DynamicAgencyServiceFeeMonthlyReportRules.GroupByMonth,
            PageSize = 10,
        }));

        Assert.NotEmpty(page.GroupCounts);
        Assert.DoesNotContain(db.ChangeTracker.Entries(), e => e.State != EntityState.Unchanged);
    }
}




