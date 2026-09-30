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
/// ERP-186 动态代理服务费月度汇总报表「当前页按对账月份 / 客户分组原币金额汇总」单元测试。
/// <para>语义：<see cref="DynamicAgencyServiceFeeMonthlyReportRules.BuildGroupCounts"/> 在 ERP-184 分组计数基础上，
/// 从同一「当前授权预览页」的 ERP-180 月度汇总行分别汇总已登记 / 草稿 / 已作废原币金额（绝不混入彼此、绝不跨币种、绝不跨页），
/// 金额按原币保留精度并给出币种精度文案；金额只是证据数字，不代表收入 / 应收 / 已收款，服务期间跨月不按期间分摊。</para>
/// <para>覆盖：精确小数、混合币种（JPY 0 位小数）、状态金额分离、隐藏金额字段（独立于选定列）、空页、截断页、
/// 业务员数据范围（撤销范围）、无身份 / 无菜单授权 fail closed、无效分组键 / 分页越界 fail closed、只读不写库。
/// 全部使用内存数据库（TestDbFactory），不连接 SQL Server、不执行任何 SQL。</para>
/// </summary>
public class DynamicAgencyServiceFeeMonthlyGroupAmountTests
{
    // ==================== 0. 测试脚手架 ====================

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
    private static long SeedAuthorizedUser(ErpDbContext db, string userName = "dyn-amt-user")
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

    /// <summary>构造一条 ERP-180 月度汇总行（纯内存，不写库），供分组金额纯规则测试直接使用。</summary>
    private static AgencyServiceFeeMonthlySummaryRow Row(
        int year, int month, long customerId, string currency,
        decimal registered, decimal draft, decimal voided,
        string code = "C001", string name = "客户一")
    {
        return new AgencyServiceFeeMonthlySummaryRow
        {
            StatementYear = year,
            StatementMonth = month,
            StatementMonthText = AgencyServiceFeeMonthlySummaryRules.MonthText(year, month),
            CustomerId = customerId,
            CustomerCode = code,
            CustomerName = name,
            Currency = currency,
            AmountDecimals = CurrencyAmountRules.PrecisionOf(currency),
            RegisteredCount = 1,
            RegisteredTotalAmount = registered,
            RegisteredTotalAmountText = AgencyServiceFeeStatementRules.AmountText(registered, currency),
            DraftCount = 1,
            DraftTotalAmount = draft,
            DraftTotalAmountText = AgencyServiceFeeStatementRules.AmountText(draft, currency),
            VoidedCount = 1,
            VoidedTotalAmount = voided,
            VoidedTotalAmountText = AgencyServiceFeeStatementRules.AmountText(voided, currency),
            StatementCount = 3,
        };
    }

    // ==================== 1. 精确小数 + 状态金额分离（纯规则） ====================

    [Fact]
    public void 纯规则_精确小数_状态金额分开汇总()
    {
        var rows = new[]
        {
            Row(2026, 9, 1, "USD", 100.10m, 50.05m, 20.20m, "C001", "客户一"),
            Row(2026, 9, 2, "USD", 200.20m, 30.03m, 10.10m, "C002", "客户二"),
        };

        var groups = DynamicAgencyServiceFeeMonthlyReportRules.BuildGroupCounts(
            rows, DynamicAgencyServiceFeeMonthlyReportRules.GroupByMonth);

        var g = Assert.Single(groups);
        Assert.Equal(DynamicAgencyServiceFeeMonthlyReportRules.GroupByMonth, g.GroupBy);
        Assert.Equal("USD", g.Currency);

        // 已登记 / 草稿 / 已作废金额分别精确求和，绝不混入彼此
        Assert.Equal(300.30m, g.RegisteredTotalAmount);
        Assert.Equal(80.08m, g.DraftTotalAmount);
        Assert.Equal(30.30m, g.VoidedTotalAmount);

        // 原币精度文案（USD 两位小数，含原币）
        Assert.Equal("300.30 USD", g.RegisteredTotalAmountText);
        Assert.Equal("80.08 USD", g.DraftTotalAmountText);
        Assert.Equal("30.30 USD", g.VoidedTotalAmountText);

        // 张数仍与 ERP-184 口径一致
        Assert.Equal(2, g.RegisteredCount);
        Assert.Equal(2, g.DraftCount);
        Assert.Equal(2, g.VoidedCount);
        Assert.Equal(6, g.StatementCount);
    }

    [Fact]
    public void 纯规则_浮点小数_精确到分_不产生舍入误差()
    {
        var rows = new[]
        {
            Row(2026, 9, 1, "USD", 0.10m, 0m, 0m, "C001", "客户一"),
            Row(2026, 9, 2, "USD", 0.20m, 0m, 0m, "C002", "客户二"),
        };

        var g = Assert.Single(DynamicAgencyServiceFeeMonthlyReportRules.BuildGroupCounts(
            rows, DynamicAgencyServiceFeeMonthlyReportRules.GroupByMonth));

        Assert.Equal(0.30m, g.RegisteredTotalAmount);
        Assert.Equal("0.30 USD", g.RegisteredTotalAmountText);
    }

    // ==================== 2. 混合币种：原币精度 + 绝不跨币种 ====================

    [Fact]
    public void 纯规则_混合币种_保留各自精度_绝不跨币种合计()
    {
        var rows = new[]
        {
            Row(2026, 9, 1, "USD", 0.10m, 0m, 0m, "C001", "客户一"),
            Row(2026, 9, 2, "USD", 0.20m, 0m, 0m, "C002", "客户二"),
            Row(2026, 9, 3, "JPY", 100m, 0m, 0m, "C003", "客户三"),
            Row(2026, 9, 4, "JPY", 23m, 0m, 0m, "C004", "客户四"),
        };

        var groups = DynamicAgencyServiceFeeMonthlyReportRules.BuildGroupCounts(
            rows, DynamicAgencyServiceFeeMonthlyReportRules.GroupByMonth);

        Assert.Equal(2, groups.Count);
        var usd = Assert.Single(groups, x => x.Currency == "USD");
        var jpy = Assert.Single(groups, x => x.Currency == "JPY");

        // 各币种按自身精度：USD 两位、JPY 0 位；绝无跨币种合计
        Assert.Equal(0.30m, usd.RegisteredTotalAmount);
        Assert.Equal("0.30 USD", usd.RegisteredTotalAmountText);
        Assert.Equal(123m, jpy.RegisteredTotalAmount);
        Assert.Equal("123 JPY", jpy.RegisteredTotalAmountText);

        // 不存在任何把两种币种合并成一个分组的结果
        Assert.DoesNotContain(groups, x => x.Currency is "USD,JPY" or "USD, JPY");
    }

    // ==================== 3. 隐藏金额字段：分组金额独立于选定列 ====================

    [Fact]
    public async Task 预览_隐藏金额字段_分组金额仍来自当前页行()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedAuthorizedUser(db);
        var c1 = SeedCustomer(db, "C001", "客户一");
        var c2 = SeedCustomer(db, "C002", "客户二");
        SeedStatement(db, "ASF-1", c1.Id, 100.10m, customerCode: "C001", customerName: "客户一");
        SeedStatement(db, "ASF-2", c2.Id, 200.20m, customerCode: "C002", customerName: "客户二");

        var ctl = BuildController(db, uid);
        // 只选定币种列（隐藏金额列），分组金额仍应来自同一当前页 ERP-180 行，而不是选定列
        var page = PreviewOk(await ctl.Preview(new DynamicAgencyServiceFeeMonthlyReportRequest
        {
            Fields = new() { "currency" },
            GroupBy = DynamicAgencyServiceFeeMonthlyReportRules.GroupByMonth,
            PageSize = 100,
        }));

        var g = Assert.Single(page.GroupCounts);
        Assert.Equal(300.30m, g.RegisteredTotalAmount);
        Assert.Equal("300.30 USD", g.RegisteredTotalAmountText);
    }

    // ==================== 4. 空页 / 截断页 ====================

    [Fact]
    public async Task 空页_分组金额为空()
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
    public async Task 截断页_分组金额只统计当前页()
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
            Fields = new() { "registeredTotalAmount", "currency" },
            GroupBy = DynamicAgencyServiceFeeMonthlyReportRules.GroupByCustomer,
            PageSize = 2,
        }));

        Assert.Equal(4, page.Total);
        Assert.True(page.Truncated);
        Assert.Equal(2, page.Rows.Count);
        // 金额也只统计当前页行，绝不扩大到整份报表
        Assert.Equal(page.Rows.Count, page.GroupCounts.Sum(g => g.RowCount));
        Assert.Equal(
            page.Rows.Sum(r => (decimal)r["registeredTotalAmount"]!),
            page.GroupCounts.Sum(g => g.RegisteredTotalAmount));
        Assert.False(string.IsNullOrWhiteSpace(page.GroupCountScopeText));
    }

    // ==================== 5. 数据范围 / 授权 / fail closed / 只读 ====================

    [Fact]
    public async Task 受限制业务员_分组金额只含被分配客户()
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
            Fields = new() { "currency" },
            GroupBy = DynamicAgencyServiceFeeMonthlyReportRules.GroupByCustomer,
            PageSize = 100,
        }));

        Assert.Equal(1, page.Total);
        var g = Assert.Single(page.GroupCounts);
        Assert.Equal(mine.Id, g.CustomerId);
        Assert.Equal(100m, g.RegisteredTotalAmount);
        Assert.DoesNotContain(page.GroupCounts, x => x.CustomerId == other.Id);
    }

    [Fact]
    public async Task 无效分组键_读取源数据前拒绝()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedAuthorizedUser(db);
        var ctl = BuildController(db, uid);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Preview(
            new DynamicAgencyServiceFeeMonthlyReportRequest { GroupBy = "quarter" }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public async Task 分页越界_读取源数据前拒绝()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedAuthorizedUser(db);
        var ctl = BuildController(db, uid);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Preview(
            new DynamicAgencyServiceFeeMonthlyReportRequest { PageSize = 201 }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public async Task 无菜单授权_分组金额预览拒绝()
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
    public async Task 分组金额预览_只读不写库()
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
