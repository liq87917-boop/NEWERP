using System.Reflection;
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
using Microsoft.AspNetCore.Mvc.Routing;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// 代理服务费对账单月度汇总（ERP-110，只读派生）单元测试：月份边界（月初 / 月末 / 跨年）、
/// 币种（不同币种分组成行、无跨币种总额、JPY 0 位小数）、状态（仅已登记计入原币合计、草稿与已作废单独计数）、
/// 软删除（已删除不计入）、服务期间跨月（不按期间分摊、全额计入对账日期所属月份）、分页有界与截断、
/// 无写入语义、固定次数数据集访问（无逐行查库）、参数校验、接口路由与前端接线 / 规则文档同源。
/// 全部使用内存数据库（TestDbFactory），不连接 SQL Server、不启动 API、不执行任何 SQL 或 seed、不运行浏览器验收。
/// </summary>
public class AgencyServiceFeeMonthlySummaryTests
{
    // ==================== 0. 测试脚手架 ====================

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
        DateTime? statementDate = null, DateTime? servicePeriodFrom = null, DateTime? servicePeriodTo = null,
        bool deleted = false, string customerCode = "C001", string customerName = "义乌进出口")
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
            ServicePeriodFrom = servicePeriodFrom ?? new DateTime(2026, 8, 1),
            ServicePeriodTo = servicePeriodTo ?? new DateTime(2026, 8, 31),
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

    /// <summary>特权范围（不过滤，用于既有行为与无数据范围限制的测试）</summary>
    private static readonly SalespersonDataScope PrivilegedScope = new()
    {
        IsPrivileged = true,
        AllowedCustomerIds = null
    };

    private static async Task<AgencyServiceFeeMonthlySummaryView> QueryAsync(
        ErpDbContext db, AgencyServiceFeeMonthlySummaryQuery query,
        SalespersonDataScope? scope = null)
        => await AgencyServiceFeeMonthlySummaryService.ForQueryAsync(db, query, scope ?? PrivilegedScope);

    private static string RepoFile(params string[] segments)
        => Path.GetFullPath(Path.Combine(
            new[] { AppContext.BaseDirectory, "..", "..", "..", "..", ".." }.Concat(segments).ToArray()));

    // ==================== 1. 月份边界 ====================

    [Fact]
    public async Task 月份边界_月初月末与跨年正确分组()
    {
        using var db = TestDbFactory.Create();
        var c = SeedCustomer(db, "C001", "义乌进出口");
        SeedStatement(db, "ASF-08-31", c.Id, 100m, statementDate: new DateTime(2026, 8, 31));
        SeedStatement(db, "ASF-09-01", c.Id, 200m, statementDate: new DateTime(2026, 9, 1));
        SeedStatement(db, "ASF-12-31", c.Id, 50m, statementDate: new DateTime(2026, 12, 31));
        SeedStatement(db, "ASF-27-01", c.Id, 70m, statementDate: new DateTime(2027, 1, 1));

        var view = await QueryAsync(db, new AgencyServiceFeeMonthlySummaryQuery { PageSize = 100 });

        Assert.Equal(4, view.Total);
        Assert.Equal(
            new[] { "2026-08", "2026-09", "2026-12", "2027-01" },
            view.Rows.Select(r => r.StatementMonthText).ToArray());
        Assert.Equal(100m, view.Rows.Single(r => r.StatementMonthText == "2026-08").RegisteredTotalAmount);
        Assert.Equal(200m, view.Rows.Single(r => r.StatementMonthText == "2026-09").RegisteredTotalAmount);
        Assert.Equal(70m, view.Rows.Single(r => r.StatementMonthText == "2027-01").RegisteredTotalAmount);
    }

    // ==================== 2. 币种 ====================

    [Fact]
    public async Task 币种不同分组成行_无跨币种合并_日元零位小数()
    {
        using var db = TestDbFactory.Create();
        var c = SeedCustomer(db, "C001", "义乌进出口");
        var date = new DateTime(2026, 9, 1);
        SeedStatement(db, "ASF-USD", c.Id, 100m, currency: "USD", statementDate: date);
        SeedStatement(db, "ASF-CNY", c.Id, 300m, currency: "CNY", statementDate: date);
        SeedStatement(db, "ASF-JPY", c.Id, 1200m, currency: "JPY", statementDate: date);

        var view = await QueryAsync(db, new AgencyServiceFeeMonthlySummaryQuery { PageSize = 100 });

        Assert.Equal(3, view.Total);
        Assert.Equal(3, view.Rows.Count);
        Assert.Equal(100m, view.Rows.Single(r => r.Currency == "USD").RegisteredTotalAmount);
        Assert.Equal("100.00 USD", view.Rows.Single(r => r.Currency == "USD").RegisteredTotalAmountText);
        Assert.Equal(300m, view.Rows.Single(r => r.Currency == "CNY").RegisteredTotalAmount);
        var jpy = view.Rows.Single(r => r.Currency == "JPY");
        Assert.Equal(1200m, jpy.RegisteredTotalAmount);
        Assert.Equal(0, jpy.AmountDecimals);
        Assert.Equal("1200 JPY", jpy.RegisteredTotalAmountText);
    }

    // ==================== 3. 状态 ====================

    [Fact]
    public async Task 状态_仅已登记计入原币合计_草稿与已作废单独计数()
    {
        using var db = TestDbFactory.Create();
        var c = SeedCustomer(db, "C001", "义乌进出口");
        var date = new DateTime(2026, 9, 15);
        SeedStatement(db, "ASF-R1", c.Id, 100m, status: AgencyServiceFeeStatementRules.StatusRecorded, statementDate: date);
        SeedStatement(db, "ASF-R2", c.Id, 250m, status: AgencyServiceFeeStatementRules.StatusRecorded, statementDate: date);
        SeedStatement(db, "ASF-D1", c.Id, 999m, status: AgencyServiceFeeStatementRules.StatusDraft, statementDate: date);
        SeedStatement(db, "ASF-V1", c.Id, 888m, status: AgencyServiceFeeStatementRules.StatusVoided, statementDate: date);

        var view = await QueryAsync(db, new AgencyServiceFeeMonthlySummaryQuery { PageSize = 100 });

        var row = Assert.Single(view.Rows);
        Assert.Equal(2, row.RegisteredCount);
        Assert.Equal(350m, row.RegisteredTotalAmount);
        Assert.Equal(1, row.DraftCount);
        Assert.Equal(999m, row.DraftTotalAmount);
        Assert.Equal(1, row.VoidedCount);
        Assert.Equal(888m, row.VoidedTotalAmount);
        Assert.Equal(4, row.StatementCount);
    }

    // ==================== 4. 软删除 ====================

    [Fact]
    public async Task 软删除对账单完全不计入()
    {
        using var db = TestDbFactory.Create();
        var c = SeedCustomer(db, "C001", "义乌进出口");
        var date = new DateTime(2026, 9, 1);
        SeedStatement(db, "ASF-OK", c.Id, 100m, statementDate: date);
        SeedStatement(db, "ASF-DEL", c.Id, 500m, statementDate: date, deleted: true);

        var view = await QueryAsync(db, new AgencyServiceFeeMonthlySummaryQuery { PageSize = 100 });

        var row = Assert.Single(view.Rows);
        Assert.Equal(1, row.RegisteredCount);
        Assert.Equal(100m, row.RegisteredTotalAmount);
        Assert.Equal(1, row.StatementCount);
    }

    // ==================== 5. 服务期间跨月不按期间分摊 ====================

    [Fact]
    public async Task 服务期间跨月_不按期间分摊_全额计入对账日期所属月份()
    {
        using var db = TestDbFactory.Create();
        var c = SeedCustomer(db, "C001", "义乌进出口");
        SeedStatement(db, "ASF-SPAN", c.Id, 400m,
            statementDate: new DateTime(2026, 9, 10),
            servicePeriodFrom: new DateTime(2026, 7, 1),
            servicePeriodTo: new DateTime(2026, 10, 31));

        var view = await QueryAsync(db, new AgencyServiceFeeMonthlySummaryQuery { PageSize = 100 });

        var row = Assert.Single(view.Rows);
        Assert.Equal("2026-09", row.StatementMonthText);
        Assert.Equal(400m, row.RegisteredTotalAmount);
        Assert.Equal(1, row.RegisteredCount);
    }

    // ==================== 6. 分页有界与截断 ====================

    [Fact]
    public async Task 分页有界_按月稳定分页并标记截断()
    {
        using var db = TestDbFactory.Create();
        var c = SeedCustomer(db, "C001", "义乌进出口");
        for (var m = 1; m <= 5; m++)
            SeedStatement(db, $"ASF-2026-{m:00}", c.Id, 10m, statementDate: new DateTime(2026, m, 15));

        var first = await QueryAsync(db, new AgencyServiceFeeMonthlySummaryQuery { Page = 1, PageSize = 2 });
        Assert.Equal(5, first.Total);
        Assert.Equal(2, first.Rows.Count);
        Assert.True(first.Truncated);
        Assert.Equal(new[] { "2026-01", "2026-02" }, first.Rows.Select(r => r.StatementMonthText).ToArray());

        var last = await QueryAsync(db, new AgencyServiceFeeMonthlySummaryQuery { Page = 3, PageSize = 2 });
        var lastRow = Assert.Single(last.Rows);
        Assert.False(last.Truncated);
        Assert.Equal("2026-05", lastRow.StatementMonthText);
    }

    // ==================== 7. 只读不写库 ====================

    [Fact]
    public async Task 只读_查询后无待保存变更且不新增记录()
    {
        using var db = TestDbFactory.Create();
        var c = SeedCustomer(db, "C001", "义乌进出口");
        SeedStatement(db, "ASF-R", c.Id, 100m);

        var before = db.AgencyServiceFeeStatements.Count();
        await QueryAsync(db, new AgencyServiceFeeMonthlySummaryQuery { PageSize = 100 });

        Assert.Equal(before, db.AgencyServiceFeeStatements.Count());
        Assert.False(db.ChangeTracker.HasChanges());
    }

    // ==================== 8. 固定次数数据集访问（无逐行查库） ====================

    [Fact]
    public async Task 读取是固定次数数据集访问_行数变化不改变访问次数且只读不写库()
    {
        using var db = TestDbFactory.Create();
        var c = SeedCustomer(db, "C001", "义乌进出口");
        SeedStatement(db, "ASF-1", c.Id, 10m, statementDate: new DateTime(2026, 9, 1));

        var counting = AgencyServiceFeeStatementTests.StatementReadCounter.Wrap(db);
        var single = await AgencyServiceFeeMonthlySummaryService.ForQueryAsync(
            counting.Proxy, new AgencyServiceFeeMonthlySummaryQuery { PageSize = 100 }, PrivilegedScope);
        Assert.Equal(1, single.Total);
        Assert.Equal(
            new[] { nameof(IErpDbContext.AgencyServiceFeeStatements) },
            counting.ReadProperties.Distinct().ToArray());
        Assert.Equal(0, counting.WriteCalls);
        var baselineReads = counting.DatasetReads;

        // 再补 300 张（跨多页、多月）：数据集访问次数必须保持不变（无逐行查库）
        for (var i = 0; i < 300; i++)
        {
            db.AgencyServiceFeeStatements.Add(new AgencyServiceFeeStatement
            {
                StatementNo = $"ASF-BULK-{i:d4}",
                NormalizedStatementNo = $"ASFBULK{i:d4}",
                CustomerId = c.Id,
                CustomerCode = "C001",
                CustomerName = "义乌进出口",
                Currency = "USD",
                StatementDate = new DateTime(2026, 1, 1).AddMonths(i % 24),
                ServicePeriodFrom = new DateTime(2026, 8, 1),
                ServicePeriodTo = new DateTime(2026, 8, 31),
                TotalAmount = 10m,
                Status = AgencyServiceFeeStatementRules.StatusRecorded,
                CreatedAt = DateTime.Now
            });
        }
        await db.SaveChangesAsync();

        var counting2 = AgencyServiceFeeStatementTests.StatementReadCounter.Wrap(db);
        var large = await AgencyServiceFeeMonthlySummaryService.ForQueryAsync(
            counting2.Proxy, new AgencyServiceFeeMonthlySummaryQuery { PageSize = 5 }, PrivilegedScope);
        Assert.Equal(5, large.Rows.Count);
        Assert.Equal(baselineReads, counting2.DatasetReads);
        Assert.Equal(0, counting2.WriteCalls);
    }

    // ==================== 9. 参数校验 ====================

    [Fact]
    public async Task 参数校验_日期区间倒置与非法币种拒绝()
    {
        using var db = TestDbFactory.Create();
        var c = SeedCustomer(db, "C001", "义乌进出口");
        SeedStatement(db, "ASF-1", c.Id, 100m);

        var badDate = await Assert.ThrowsAsync<BusinessException>(() => QueryAsync(db,
            new AgencyServiceFeeMonthlySummaryQuery
            {
                StatementDateFrom = new DateTime(2026, 9, 10),
                StatementDateTo = new DateTime(2026, 9, 1)
            }));
        Assert.Equal(ErrorCodes.InvalidParameter, badDate.Code);

        var badCurrency = await Assert.ThrowsAsync<BusinessException>(() => QueryAsync(db,
            new AgencyServiceFeeMonthlySummaryQuery { Currency = "XXX" }));
        Assert.Equal(ErrorCodes.InvalidParameter, badCurrency.Code);
    }

    // ==================== 10. 接口路由契约 ====================

    [Fact]
    public void 接口路由_与前端调用路径一致()
    {
        var route = typeof(AgencyServiceFeeStatementController).GetCustomAttribute<RouteAttribute>(true)!.Template;
        Assert.Equal("api/agency-service-fee-statements", route);
        var method = typeof(AgencyServiceFeeStatementController).GetMethod(
            nameof(AgencyServiceFeeStatementController.MonthlySummary),
            BindingFlags.Public | BindingFlags.Instance)!;
        var templates = method.GetCustomAttributes<HttpMethodAttribute>(true)
            .Select(a => a.Template ?? string.Empty)
            .ToList();
        Assert.Contains("monthly-summary", templates);
    }

    // ==================== 11. 前端接线契约 ====================

    [Fact]
    public void 前端接线_脚本注册_入口按钮_接口路径齐备()
    {
        var index = File.ReadAllText(RepoFile("src", "ERP.Api", "wwwroot", "index.html"));
        Assert.Contains("/js/agency-service-fee-monthly-summary.js", index);

        var statementsJs = File.ReadAllText(RepoFile(
            "src", "ERP.Api", "wwwroot", "js", "agency-service-fee-statements.js"));
        Assert.Contains("openAgencyServiceFeeMonthlySummary()", statementsJs);

        var js = File.ReadAllText(RepoFile(
            "src", "ERP.Api", "wwwroot", "js", "agency-service-fee-monthly-summary.js"));
        Assert.Contains("function openAgencyServiceFeeMonthlySummary(", js);
        Assert.Contains("/api/agency-service-fee-statements/monthly-summary?", js);
        Assert.Contains("registeredTotalAmountText", js);
        Assert.Contains("draftCount", js);
        Assert.Contains("voidedCount", js);
        Assert.Contains("不按期间分摊", js);
        Assert.Contains("原币合计", js);
        Assert.Contains("不是", js);
    }

    // ==================== 12. 规则与文档同源 ====================

    [Fact]
    public void 规则与文档同源_口径文案非空且文档齐备()
    {
        Assert.Contains("未删除", AgencyServiceFeeMonthlySummaryRules.RuleText);
        Assert.Contains("不按期间分摊", AgencyServiceFeeMonthlySummaryRules.NoProrationText);
        Assert.Contains("绝不", AgencyServiceFeeMonthlySummaryRules.CurrencyIsolationText);
        Assert.Contains("只读", AgencyServiceFeeMonthlySummaryRules.ReadOnlyText);
        Assert.Contains("不是", AgencyServiceFeeMonthlySummaryRules.BoundaryText);

        var doc = File.ReadAllText(RepoFile("docs", "agency-service-fee-monthly-summary.md"));
        Assert.Contains("对账日期所属年月", doc);
        Assert.Contains("不按期间分摊", doc);
        Assert.Contains("无逐行查库", doc);
        Assert.Contains("/api/agency-service-fee-statements/monthly-summary", doc);
    }

    // ==================== 13. 数据范围与授权（ERP-180） ====================

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

    private static AgencyServiceFeeStatementController BuildController(ErpDbContext db, long? userId)
    {
        var ctl = new AgencyServiceFeeStatementController(db);
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

    [Fact]
    public async Task 范围_特权账号不过滤_看到全部客户()
    {
        using var db = TestDbFactory.Create();
        var c1 = SeedCustomer(db, "C001", "客户一");
        var c2 = SeedCustomer(db, "C002", "客户二");
        SeedStatement(db, "ASF-1", c1.Id, 100m);
        SeedStatement(db, "ASF-2", c2.Id, 200m);

        var view = await QueryAsync(db, new AgencyServiceFeeMonthlySummaryQuery { PageSize = 100 },
            new SalespersonDataScope { IsPrivileged = true, AllowedCustomerIds = null });

        Assert.Equal(2, view.Total);
        Assert.Equal(2, view.Rows.Count);
        Assert.Contains(view.Rows, r => r.CustomerId == c1.Id);
        Assert.Contains(view.Rows, r => r.CustomerId == c2.Id);
    }

    [Fact]
    public async Task 范围_受限制业务员_只看到其被分配客户_合计与金额不泄露他人()
    {
        using var db = TestDbFactory.Create();
        var mine = SeedCustomer(db, "C001", "我的客户");
        var other = SeedCustomer(db, "C002", "别人的客户");
        SeedStatement(db, "ASF-MY-1", mine.Id, 100m);
        SeedStatement(db, "ASF-MY-2", mine.Id, 150m);
        SeedStatement(db, "ASF-OTHER", other.Id, 999m);

        var view = await QueryAsync(db, new AgencyServiceFeeMonthlySummaryQuery { PageSize = 100 },
            new SalespersonDataScope { IsPrivileged = false, AllowedCustomerIds = new HashSet<long> { mine.Id } });

        var row = Assert.Single(view.Rows);
        Assert.Equal(1, view.Total);
        Assert.Equal(mine.Id, row.CustomerId);
        Assert.Equal(2, row.RegisteredCount);
        Assert.Equal(250m, row.RegisteredTotalAmount);
        Assert.DoesNotContain(view.Rows, r => r.CustomerId == other.Id);
    }

    [Fact]
    public async Task 范围_未授权客户筛选_无数据_不泄露任何分组()
    {
        using var db = TestDbFactory.Create();
        var mine = SeedCustomer(db, "C001", "我的客户");
        var other = SeedCustomer(db, "C002", "别人的客户");
        SeedStatement(db, "ASF-OTHER", other.Id, 999m);

        var view = await QueryAsync(db, new AgencyServiceFeeMonthlySummaryQuery
        {
            CustomerId = other.Id,
            PageSize = 100
        }, new SalespersonDataScope { IsPrivileged = false, AllowedCustomerIds = new HashSet<long> { mine.Id } });

        Assert.Equal(0, view.Total);
        Assert.Empty(view.Rows);
        Assert.False(string.IsNullOrEmpty(view.EmptyText));
    }

    [Fact]
    public async Task 范围_空范围_fail_closed_看不到任何客户()
    {
        using var db = TestDbFactory.Create();
        var c = SeedCustomer(db, "C001", "客户一");
        SeedStatement(db, "ASF-1", c.Id, 100m);

        var view = await QueryAsync(db, new AgencyServiceFeeMonthlySummaryQuery { PageSize = 100 },
            new SalespersonDataScope { IsPrivileged = false, AllowedCustomerIds = new HashSet<long>() });

        Assert.Equal(0, view.Total);
        Assert.Empty(view.Rows);
        Assert.Equal(0, view.GroupCount);
    }

    [Fact]
    public async Task 范围_混合币种_保留原币分离_绝不跨币种合并()
    {
        using var db = TestDbFactory.Create();
        var mine = SeedCustomer(db, "C001", "我的客户");
        var other = SeedCustomer(db, "C002", "别人的客户");
        var date = new DateTime(2026, 9, 1);
        SeedStatement(db, "ASF-USD", mine.Id, 100m, currency: "USD", statementDate: date);
        SeedStatement(db, "ASF-CNY", mine.Id, 300m, currency: "CNY", statementDate: date);
        SeedStatement(db, "ASF-OTHER", other.Id, 999m, currency: "USD", statementDate: date);

        var view = await QueryAsync(db, new AgencyServiceFeeMonthlySummaryQuery { PageSize = 100 },
            new SalespersonDataScope { IsPrivileged = false, AllowedCustomerIds = new HashSet<long> { mine.Id } });

        Assert.Equal(2, view.Total);
        Assert.Equal(2, view.Rows.Count);
        Assert.Equal(100m, view.Rows.Single(r => r.Currency == "USD").RegisteredTotalAmount);
        Assert.Equal(300m, view.Rows.Single(r => r.Currency == "CNY").RegisteredTotalAmount);
        Assert.All(view.Rows, r => Assert.Equal(mine.Id, r.CustomerId));
    }


    [Fact]
    public async Task 授权_无身份_未认证拒绝()
    {
        using var db = TestDbFactory.Create();
        var ctl = BuildController(db, null);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.MonthlySummary(
            new AgencyServiceFeeMonthlySummaryQuery()));
        Assert.Equal(ErrorCodes.Unauthorized, ex.Code);
    }

    [Fact]
    public async Task 授权_无客户菜单_权限不足拒绝()
    {
        using var db = TestDbFactory.Create();
        var role = SeedRole(db, "NoMenu");
        var user = SeedUser(db, "nommenu-user");
        SeedUserRole(db, user.Id, role.Id);
        var ctl = BuildController(db, user.Id);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.MonthlySummary(
            new AgencyServiceFeeMonthlySummaryQuery()));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    [Fact]
    public async Task 授权_被回收后_下一次请求立即拒绝()
    {
        using var db = TestDbFactory.Create();
        var role = SeedRole(db, "Revoke-Role");
        var user = SeedUser(db, "revoke-user");
        SeedUserRole(db, user.Id, role.Id);
        var menu = SeedMenu(db, AgencyServiceFeeReconciliationRules.RequiredMenuCode);
        var roleMenu = new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id };
        db.SysRoleMenus.Add(roleMenu);
        db.SaveChanges();

        var ctl = BuildController(db, user.Id);
        var ok = await ctl.MonthlySummary(new AgencyServiceFeeMonthlySummaryQuery { PageSize = 10 });
        Assert.IsType<OkObjectResult>(ok);

        roleMenu.IsDeleted = true;
        db.SaveChanges();

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.MonthlySummary(
            new AgencyServiceFeeMonthlySummaryQuery { PageSize = 10 }));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    [Fact]
    public async Task 授权_特权账号_正常返回全部客户()
    {
        using var db = TestDbFactory.Create();
        var role = SeedRole(db, "Priv", isSystem: true);
        var user = SeedUser(db, "priv-user");
        SeedUserRole(db, user.Id, role.Id);
        SeedRoleMenu(db, role.Id, SeedMenu(db, AgencyServiceFeeReconciliationRules.RequiredMenuCode).Id);

        var c1 = SeedCustomer(db, "C001", "客户一");
        var c2 = SeedCustomer(db, "C002", "客户二");
        SeedStatement(db, "ASF-1", c1.Id, 100m);
        SeedStatement(db, "ASF-2", c2.Id, 200m);

        var ctl = BuildController(db, user.Id);
        var ok = Assert.IsType<OkObjectResult>(await ctl.MonthlySummary(
            new AgencyServiceFeeMonthlySummaryQuery { PageSize = 100 }));
        var resp = Assert.IsType<ApiResponse<AgencyServiceFeeMonthlySummaryView>>(ok.Value);

        Assert.NotNull(resp.Data);
        Assert.Equal(2, resp.Data.Total);
        Assert.Contains(resp.Data.Rows, r => r.CustomerId == c1.Id);
        Assert.Contains(resp.Data.Rows, r => r.CustomerId == c2.Id);
    }

    [Fact]
    public async Task 授权_受限制业务员_通过接口只看到其被分配客户()
    {
        using var db = TestDbFactory.Create();
        var role = SeedRole(db, "Sales");
        var user = SeedUser(db, "alice");
        SeedUserRole(db, user.Id, role.Id);
        SeedRoleMenu(db, role.Id, SeedMenu(db, AgencyServiceFeeReconciliationRules.RequiredMenuCode).Id);

        var employee = SeedEmployee(db, "alice");
        var mine = SeedCustomer(db, "C001", "我的客户", employee.Id);
        var other = SeedCustomer(db, "C002", "别人的客户", employee.Id + 1000);

        SeedStatement(db, "ASF-MY", mine.Id, 100m);
        SeedStatement(db, "ASF-OTHER", other.Id, 999m);

        var ctl = BuildController(db, user.Id);
        var ok = Assert.IsType<OkObjectResult>(await ctl.MonthlySummary(
            new AgencyServiceFeeMonthlySummaryQuery { PageSize = 100 }));
        var resp = Assert.IsType<ApiResponse<AgencyServiceFeeMonthlySummaryView>>(ok.Value);

        Assert.NotNull(resp.Data);
        Assert.Equal(1, resp.Data.Total);
        var row = Assert.Single(resp.Data.Rows);
        Assert.Equal(mine.Id, row.CustomerId);
        Assert.Equal(100m, row.RegisteredTotalAmount);
    }

}



