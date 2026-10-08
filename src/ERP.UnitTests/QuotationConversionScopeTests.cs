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
/// 报价成交率报表（/api/reports/quotation-conversion，ERP-204）数据范围与授权单元测试：
/// 每次请求重新校验当前登录身份与「报价单」菜单授权，按业务员数据范围（ERP-097 唯一权威口径）
/// 在查询源头过滤 Quotations（特权账号不过滤、受限制业务员仅其被分配客户、空客户对受限制账号不可见），
/// 日期校验先于读取（含首尾最多 366 天）、范围内最多物化 2000 张（多读 1 张检测超限）、
/// PI / 销售订单转换链接分批查询，且保留既有换算口径。
/// <para>全部使用内存数据库（TestDbFactory），不连接 SQL Server、不启动 API、不执行任何 SQL / seed、不运行浏览器验收。</para>
/// </summary>
public class QuotationConversionScopeTests
{
    private const string QuotationMenuCode = "quotation";

    private static readonly DateTime Start = new(2026, 9, 1);
    private static readonly DateTime End = new(2026, 9, 30);

    /// <summary>成交率口径 / 超限 / 分批测试使用的特权数据范围（不过滤客户）。</summary>
    private static readonly SalespersonDataScope PrivilegedScope = new() { IsPrivileged = true, AllowedCustomerIds = null };

    // ==================== 脚手架 ====================

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

    private static BaseCustomer SeedCustomer(ErpDbContext db, string code, string name, long? empId = null)
    {
        var customer = new BaseCustomer
        {
            CustomerCode = code,
            CustomerName = name,
            Status = 1,
            CreditStatus = "正常",
            EmpId = empId
        };
        db.BaseCustomers.Add(customer);
        db.SaveChanges();
        return customer;
    }

    private static Quotation SeedQuotation(
        ErpDbContext db, string no, long? customerId, string salesmanName,
        DateTime? quotationDate = null, decimal totalAmount = 1000m,
        DocumentStatus status = DocumentStatus.Approved)
    {
        var quotation = new Quotation
        {
            QuotationNo = no,
            QuotationDate = quotationDate ?? Start,
            CustomerId = customerId,
            CustomerName = no,
            SalesmanName = salesmanName,
            TotalAmount = totalAmount,
            Status = status
        };
        db.Quotations.Add(quotation);
        db.SaveChanges();
        return quotation;
    }

    /// <summary>创建拥有「报价单」菜单授权的用户（不含业务员映射，由各用例按需补齐）</summary>
    private static SysUser SeedAuthorizedUser(ErpDbContext db, string userName, string roleCode, bool isSystemRole = false)
    {
        var role = SeedRole(db, roleCode, isSystemRole);
        var user = SeedUser(db, userName);
        SeedUserRole(db, user.Id, role.Id);
        SeedRoleMenu(db, role.Id, SeedMenu(db, QuotationMenuCode).Id);
        return user;
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

    private static List<ReportDtos.QuotationConversionItem> OkList(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<ApiResponse<List<ReportDtos.QuotationConversionItem>>>(ok.Value);
        return resp.Data ?? new List<ReportDtos.QuotationConversionItem>();
    }

    // ==================== 1. 身份与菜单授权 ====================

    [Fact]
    public async Task 无身份_未认证拒绝()
    {
        using var db = TestDbFactory.Create();
        var ctl = BuildController(db, null);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.QuotationConversion(Start, End));
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

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.QuotationConversion(Start, End));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    [Fact]
    public async Task 授权被回收_下一次请求立即拒绝()
    {
        using var db = TestDbFactory.Create();
        var role = SeedRole(db, "Revoke-Role");
        var user = SeedUser(db, "revoke-user");
        SeedUserRole(db, user.Id, role.Id);
        var menu = SeedMenu(db, QuotationMenuCode);
        var roleMenu = new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id };
        db.SysRoleMenus.Add(roleMenu);
        db.SaveChanges();

        var ctl = BuildController(db, user.Id);
        Assert.IsType<OkObjectResult>(await ctl.QuotationConversion(Start, End));

        roleMenu.IsDeleted = true;
        db.SaveChanges();

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.QuotationConversion(Start, End));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    // ==================== 2. 业务员数据范围 ====================

    [Fact]
    public async Task 受限制业务员_只看到被分配客户_他人与空客户不可见()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "alice", "Sales");
        var employee = SeedEmployee(db, "alice");
        var mine = SeedCustomer(db, "C001", "我的客户", employee.Id);
        var other = SeedCustomer(db, "C002", "别人的客户", employee.Id + 1000);

        SeedQuotation(db, "QT-MINE", mine.Id, "我的业务员");
        SeedQuotation(db, "QT-OTHER", other.Id, "他人业务员");
        SeedQuotation(db, "QT-NULL", null, "匿名业务员");

        var ctl = BuildController(db, user.Id);
        var items = OkList(await ctl.QuotationConversion(Start, End));

        var row = Assert.Single(items);
        Assert.Equal("我的业务员", row.SalesmanName);
        Assert.Equal(1, row.QuotationCount);
    }

    [Fact]
    public async Task 未映射业务员_看不到任何客户()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "bob", "Sales");
        var customer = SeedCustomer(db, "C001", "有客户");
        SeedQuotation(db, "QT-1", customer.Id, "业务员");

        var ctl = BuildController(db, user.Id);
        var items = OkList(await ctl.QuotationConversion(Start, End));

        Assert.Empty(items);
    }

    [Fact]
    public async Task 特权账号_保留全部可见_包括空客户()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "priv-user", "Priv", isSystemRole: true);

        var c1 = SeedCustomer(db, "C001", "客户一");
        var c2 = SeedCustomer(db, "C002", "客户二");
        SeedQuotation(db, "QT-1", c1.Id, "业务员甲");
        SeedQuotation(db, "QT-2", c2.Id, "业务员乙");
        SeedQuotation(db, "QT-NULL", null, "匿名业务员");

        var ctl = BuildController(db, user.Id);
        var items = OkList(await ctl.QuotationConversion(Start, End));

        Assert.Equal(3, items.Count);
        Assert.Contains(items, i => i.SalesmanName == "匿名业务员");
    }

    // ==================== 3. 日期校验（先于读取） ====================

    [Fact]
    public async Task 日期范围结束早于开始_拒绝()
    {
        using var db = TestDbFactory.Create();
        var service = new ReportService(db);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            service.GetQuotationConversionAsync(new DateTime(2026, 9, 30), new DateTime(2026, 9, 1), PrivilegedScope));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public async Task 日期范围超过366天_拒绝()
    {
        using var db = TestDbFactory.Create();
        var service = new ReportService(db);

        // 2026-01-01 ~ 2027-01-02：含首尾 367 天
        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            service.GetQuotationConversionAsync(new DateTime(2026, 1, 1), new DateTime(2027, 1, 2), PrivilegedScope));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public async Task 日期范围366天边界_允许()
    {
        using var db = TestDbFactory.Create();
        var service = new ReportService(db);

        // 2026-01-01 ~ 2027-01-01：含首尾正好 366 天
        var rows = await service.GetQuotationConversionAsync(
            new DateTime(2026, 1, 1), new DateTime(2027, 1, 1), PrivilegedScope);

        Assert.Empty(rows);
    }

    // ==================== 4. 物化上限与转换链接分批 ====================

    [Fact]
    public async Task 报价单超过2000_超限_要求缩小日期范围()
    {
        using var db = TestDbFactory.Create();
        var list = new List<Quotation>();
        for (var i = 0; i < 2001; i++)
        {
            list.Add(new Quotation
            {
                QuotationNo = $"QT-{i:D4}",
                QuotationDate = new DateTime(2026, 9, 1).AddDays(i % 30),
                CustomerId = 1L,
                CustomerName = "客户",
                SalesmanName = "业务员",
                TotalAmount = 1m,
                Status = DocumentStatus.Approved
            });
        }
        db.Quotations.AddRange(list);
        await db.SaveChangesAsync();

        var service = new ReportService(db);
        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            service.GetQuotationConversionAsync(Start, End, PrivilegedScope));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Contains("缩小日期范围", ex.Message);
    }

    [Fact]
    public async Task 报价单恰好2000_不超限_正常统计()
    {
        using var db = TestDbFactory.Create();
        var list = new List<Quotation>();
        for (var i = 0; i < 2000; i++)
        {
            list.Add(new Quotation
            {
                QuotationNo = $"QT-{i:D4}",
                QuotationDate = new DateTime(2026, 9, 1).AddDays(i % 30),
                CustomerId = 1L,
                CustomerName = "客户",
                SalesmanName = "业务员",
                TotalAmount = 1m,
                Status = DocumentStatus.Approved
            });
        }
        db.Quotations.AddRange(list);
        await db.SaveChangesAsync();

        var rows = await new ReportService(db).GetQuotationConversionAsync(Start, End, PrivilegedScope);

        var row = Assert.Single(rows);
        Assert.Equal(2000, row.QuotationCount);
        Assert.Equal(2000m, row.TotalAmount);
    }

    [Fact]
    public async Task 转换链接分批查询_超过一批仍全部计入()
    {
        using var db = TestDbFactory.Create();
        var quotations = new List<Quotation>();
        for (var i = 0; i < 1001; i++)
        {
            quotations.Add(new Quotation
            {
                QuotationNo = $"QT-{i:D4}",
                QuotationDate = new DateTime(2026, 9, 1).AddDays(i % 30),
                CustomerId = 1L,
                CustomerName = "客户",
                SalesmanName = "业务员",
                TotalAmount = 100m,
                Status = DocumentStatus.Approved
            });
        }
        db.Quotations.AddRange(quotations);
        await db.SaveChangesAsync();

        var pis = quotations.Select(q => new ProformaInvoice
        {
            PiNo = $"PI-{q.QuotationNo}",
            PiDate = q.QuotationDate,
            QuotationId = q.Id,
            QuotationNo = q.QuotationNo,
            CustomerName = q.CustomerName,
            Currency = Currency.USD,
            ExchangeRate = 7.2m
        }).ToList();
        db.ProformaInvoices.AddRange(pis);
        await db.SaveChangesAsync();

        var rows = await new ReportService(db).GetQuotationConversionAsync(Start, End, PrivilegedScope);

        // 1001 张报价单 → 两个批次（1000 + 1）都必须被查询到，全部计入已转出
        var row = Assert.Single(rows);
        Assert.Equal(1001, row.QuotationCount);
        Assert.Equal(1001, row.ConvertedCount);
        Assert.Equal(100m, row.ConversionRate);
    }

    // ==================== 5. 换算口径不变 ====================

    [Fact]
    public async Task 换算口径不变_作废不计分母_完成无来源外键计入分子()
    {
        using var db = TestDbFactory.Create();
        SeedQuotation(db, "QT-DONE", 1L, "业务员", quotationDate: Start.AddDays(1), status: DocumentStatus.Completed);
        SeedQuotation(db, "QT-CANCEL", 1L, "业务员", quotationDate: Start.AddDays(2), status: DocumentStatus.Cancelled);
        SeedQuotation(db, "QT-OPEN", 1L, "业务员", quotationDate: Start.AddDays(3), status: DocumentStatus.Approved);

        var rows = await new ReportService(db).GetQuotationConversionAsync(Start, End, PrivilegedScope);

        var row = Assert.Single(rows);
        Assert.Equal(2, row.QuotationCount);        // completed + open；cancelled 不计入分母
        Assert.Equal(1, row.ConvertedCount);        // 仅 completed（无来源外键也计入分子）
        Assert.Equal(1, row.CancelledCount);
        Assert.Equal(50m, row.ConversionRate);      // 1 ÷ 2 × 100
    }

    // ==================== 6. 报价单授权 / 客户范围谓词（ERP-400） ====================

    [Fact]
    public void 报价单范围谓词_特权与进程内恒真_受限账号仅本人客户且无主不可见()
    {
        Assert.NotNull(QuotationAuthorizationRules.ScopeFilter(PrivilegedScope));
        Assert.NotNull(QuotationAuthorizationRules.ScopeFilter(null));

        var restricted = new SalespersonDataScope
        {
            IsPrivileged = false, SalesmanId = 5, AllowedCustomerIds = new HashSet<long> { 1, 2 }
        };
        var quotations = new List<Quotation>
        {
            new() { CustomerId = 1 }, new() { CustomerId = 2 },
            new() { CustomerId = 3 }, new() { CustomerId = null }
        }.AsQueryable();

        var visible = QuotationAuthorizationRules.ApplyScope(quotations, restricted)
            .Select(q => q.CustomerId).ToList();
        Assert.Equal(new long?[] { 1, 2 }, visible);            // 越界 3 与无主 null 一律排除

        var unmapped = new SalespersonDataScope
        {
            IsPrivileged = false, SalesmanId = null, AllowedCustomerIds = new HashSet<long>()
        };
        Assert.Empty(QuotationAuthorizationRules.ApplyScope(quotations, unmapped).ToList());
    }

    [Fact]
    public void 报价单归属复核_受限账号缺失归属或越界一律fail_closed()
    {
        var restricted = new SalespersonDataScope
        {
            IsPrivileged = false, SalesmanId = 5, AllowedCustomerIds = new HashSet<long> { 1 }
        };

        QuotationAuthorizationRules.EnsureStoredCustomerInScope(restricted, 1);
        Assert.Equal(ErrorCodes.Forbidden,
            Assert.Throws<BusinessException>(() =>
                QuotationAuthorizationRules.EnsureStoredCustomerInScope(restricted, 2)).Code);
        Assert.Equal(ErrorCodes.Forbidden,
            Assert.Throws<BusinessException>(() =>
                QuotationAuthorizationRules.EnsureStoredCustomerInScope(restricted, null)).Code);
        Assert.Equal(ErrorCodes.Forbidden,
            Assert.Throws<BusinessException>(() =>
                QuotationAuthorizationRules.EnsureProposedCustomerInScope(restricted, 0)).Code);
        Assert.Equal(ErrorCodes.Forbidden,
            Assert.Throws<BusinessException>(() =>
                QuotationAuthorizationRules.EnsureSourceCustomerInScope(restricted, 9)).Code);
        Assert.Equal(ErrorCodes.Forbidden,
            Assert.Throws<BusinessException>(() =>
                QuotationAuthorizationRules.EnsureTargetCustomerInScope(restricted, 9)).Code);
        Assert.Equal(ErrorCodes.Forbidden,
            Assert.Throws<BusinessException>(() =>
                QuotationAuthorizationRules.EnsureChainCustomerInScope(restricted, new long?[] { 1, 2 })).Code);

        // 进程内调用（null）与特权账号保持既有口径：绝不把空身份当作匿名或管理员而额外放行。
        QuotationAuthorizationRules.EnsureStoredCustomerInScope(null, null);
        QuotationAuthorizationRules.EnsureStoredCustomerInScope(PrivilegedScope, null);
        QuotationAuthorizationRules.EnsureChainCustomerInScope(restricted, new long?[] { 1, 1 });
    }
}
