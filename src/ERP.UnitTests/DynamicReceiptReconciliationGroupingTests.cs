using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-170 动态客户订单与收款核对报表「当前页订单 / 未关联收款计数分组」单元测试。
/// 覆盖：有限分组键白名单（none / customer / currency / receiptCoverageStatus / receiptEvidenceStatus，大小写不敏感）、
/// 未知分组键在源读取前拒绝、无菜单授权拒绝、受限制业务员仅本人客户进入分组、空页分组为空、
/// active / pending / historical 与 linked / partial / unlinked / unknown 状态隔离、币种不合并、
/// 分组只统计当前页（页大小上限 200）、以及只读不写库。
/// <para>全部使用内存数据库（TestDbFactory），不连接 SQL Server、不启动 API、不执行任何 SQL / seed。</para>
/// </summary>
public class DynamicReceiptReconciliationGroupingTests
{
    private static readonly DateTime AsOf = new(2026, 9, 25);
    private const long CustomerA = 966001L;
    private const long CustomerB = 966002L;

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

    private static long SeedPrivilegedUser(ErpDbContext db, string userName = "priv")
    {
        var user = SeedUser(db, userName);
        var role = SeedRole(db, $"Role-{userName}", isSystem: true);
        SeedUserRole(db, user.Id, role.Id);
        SeedRoleMenu(db, role.Id, SeedMenu(db, DynamicReceiptReconciliationReportRules.RequiredMenuCode).Id);
        return user.Id;
    }

    private static BaseEmployee SeedEmployee(ErpDbContext db, string code, bool isSalesman = true)
    {
        var employee = new BaseEmployee { EmployeeCode = code, EmployeeName = code, IsSalesman = isSalesman, Status = 1 };
        db.BaseEmployees.Add(employee);
        db.SaveChanges();
        return employee;
    }

    private static BaseCustomer SeedCustomer(ErpDbContext db, long id, string name, long? empId = null)
    {
        var customer = new BaseCustomer
        {
            Id = id,
            CustomerCode = $"C{id}",
            CustomerName = name,
            Status = 1,
            CreditStatus = "正常",
            EmpId = empId
        };
        db.BaseCustomers.Add(customer);
        db.SaveChanges();
        return customer;
    }

    private static SalesOrder SeedOrder(ErpDbContext db, string orderNo, long customerId, Currency currency,
        decimal totalAmount, DocumentStatus status = DocumentStatus.Approved)
    {
        var order = new SalesOrder
        {
            OrderNo = orderNo,
            OrderDate = AsOf.AddDays(-10),
            CustomerId = customerId,
            Currency = currency,
            TotalAmount = totalAmount,
            Status = status,
            CreatedAt = new DateTime(2026, 9, 14, 8, 0, 0),
        };
        db.SalesOrders.Add(order);
        db.SaveChanges();
        return order;
    }

    private static FinanceReceipt SeedReceipt(ErpDbContext db, string receiptNo, long customerId, decimal amount,
        Currency currency, DocumentStatus status)
    {
        var receipt = new FinanceReceipt
        {
            ReceiptNo = receiptNo,
            ReceiptDate = AsOf.AddDays(-1),
            CustomerId = customerId,
            Amount = amount,
            Currency = currency,
            Status = status,
        };
        db.FinanceReceipts.Add(receipt);
        db.SaveChanges();
        return receipt;
    }

    private static DynamicReceiptReconciliationReportController NewController(ErpDbContext db) => new(db);

    private static DynamicReceiptReconciliationReportPageDto PreviewOk(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<ApiResponse<DynamicReceiptReconciliationReportPageDto>>(ok.Value);
        Assert.NotNull(resp.Data);
        return resp.Data!;
    }

    private static async Task<BusinessException> AssertBusinessAsync(int expectedCode, Func<Task> action)
    {
        var ex = await Assert.ThrowsAsync<BusinessException>(action);
        Assert.Equal(expectedCode, ex.Code);
        return ex;
    }

    // ==================== 1. 分组键白名单（fail closed） ====================

    [Theory]
    [InlineData(null, "none")]
    [InlineData("", "none")]
    [InlineData("   ", "none")]
    [InlineData("none", "none")]
    [InlineData("customer", "customer")]
    [InlineData("CUSTOMER", "customer")]
    [InlineData("currency", "currency")]
    [InlineData("Currency", "currency")]
    [InlineData("receiptCoverageStatus", "receiptCoverageStatus")]
    [InlineData("RECEIPTCOVERAGESTATUS", "receiptCoverageStatus")]
    [InlineData("receiptEvidenceStatus", "receiptEvidenceStatus")]
    [InlineData("ReceiptEvidenceStatus", "receiptEvidenceStatus")]
    public void NormalizeGroupBy_接受有限分组键(string? input, string expected)
        => Assert.Equal(expected, DynamicReceiptReconciliationReportRules.NormalizeGroupBy(input));

    [Theory]
    [InlineData("supplier")]
    [InlineData("month")]
    [InlineData("orderStatus")]
    [InlineData("receiptStatus")]
    [InlineData("coverage")]
    [InlineData("RECEIPTCOVERAGE")]
    public void NormalizeGroupBy_拒绝未知分组键(string input)
    {
        var ex = Assert.Throws<BusinessException>(() => DynamicReceiptReconciliationReportRules.NormalizeGroupBy(input));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    // ==================== 2. 状态隔离（纯规则：显式保留 unknown / pending / historical） ====================

    [Fact]
    public void BuildOrderGroups_收款覆盖状态_显式保留unknown()
    {
        var rows = new List<Dictionary<string, object?>>
        {
            new() { ["receiptCoverageStatus"] = "linked", ["receiptCoverageText"] = "完整" },
            new() { ["receiptCoverageStatus"] = "partial", ["receiptCoverageText"] = "部分" },
            new() { ["receiptCoverageStatus"] = "unlinked", ["receiptCoverageText"] = "未关联" },
            new() { ["receiptCoverageStatus"] = "unknown", ["receiptCoverageText"] = "未知" },
        };

        var groups = DynamicReceiptReconciliationReportRules.BuildOrderGroups(rows, "receiptCoverageStatus");
        Assert.Equal(4, groups.Count);
        var byKey = groups.ToDictionary(g => g.Key, StringComparer.Ordinal);
        Assert.Equal(1, byKey["coverage:linked"].OrderCount);
        Assert.Equal(1, byKey["coverage:partial"].OrderCount);
        Assert.Equal(1, byKey["coverage:unlinked"].OrderCount);
        Assert.Equal(1, byKey["coverage:unknown"].OrderCount);
    }

    [Fact]
    public void BuildReceiptGroups_收款证据状态_隔离active_pending_historical()
    {
        var rows = new List<Dictionary<string, object?>>
        {
            new() { ["evidenceStatus"] = "active", ["evidenceText"] = "有效" },
            new() { ["evidenceStatus"] = "pending", ["evidenceText"] = "未审核" },
            new() { ["evidenceStatus"] = "historical", ["evidenceText"] = "历史" },
        };

        var groups = DynamicReceiptReconciliationReportRules.BuildReceiptGroups(rows, "receiptEvidenceStatus", truncated: true);
        Assert.Equal(3, groups.Count);
        var byKey = groups.ToDictionary(g => g.Key, StringComparer.Ordinal);
        Assert.Equal(1, byKey["evidence:active"].ReceiptCount);
        Assert.Equal(1, byKey["evidence:pending"].ReceiptCount);
        Assert.Equal(1, byKey["evidence:historical"].ReceiptCount);
        Assert.All(groups, g => Assert.True(g.Truncated));
    }

    [Fact]
    public void BuildOrderGroups_按币种分组_绝不合并币种()
    {
        var rows = new List<Dictionary<string, object?>>
        {
            new() { ["currency"] = "USD" },
            new() { ["currency"] = "USD" },
            new() { ["currency"] = "CNY" },
        };

        var groups = DynamicReceiptReconciliationReportRules.BuildOrderGroups(rows, "currency");
        Assert.Equal(2, groups.Count);
        Assert.Equal(2, groups.Single(g => g.Key == "currency:USD").OrderCount);
        Assert.Equal(1, groups.Single(g => g.Key == "currency:CNY").OrderCount);
    }

    [Fact]
    public void BuildOrderGroups_收款证据状态_订单侧不适用_返回空()
    {
        var rows = new List<Dictionary<string, object?>> { new() { ["orderNo"] = "SO-1" } };
        Assert.Empty(DynamicReceiptReconciliationReportRules.BuildOrderGroups(rows, "receiptEvidenceStatus"));
    }

    [Fact]
    public void BuildReceiptGroups_收款覆盖状态_收款侧不适用_返回空()
    {
        var rows = new List<Dictionary<string, object?>> { new() { ["receiptNo"] = "SK-1" } };
        Assert.Empty(DynamicReceiptReconciliationReportRules.BuildReceiptGroups(rows, "receiptCoverageStatus", false));
    }

    // ==================== 3. 控制器集成：授权 / 范围 / 空页 ====================

    [Fact]
    public async Task Preview_无效分组键_在源读取前拒绝()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "甲客户");
        SeedOrder(db, "SO-1", CustomerA, Currency.USD, 100m);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => ctl.Preview(new DynamicReceiptReconciliationReportRequest { GroupBy = "supplier" }));
    }

    [Fact]
    public async Task Preview_无销售订单菜单授权_分组拒绝()
    {
        using var db = TestDbFactory.Create();
        var user = SeedUser(db, "denied");
        var role = SeedRole(db, "NoMenu");
        SeedUserRole(db, user.Id, role.Id);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        await AssertBusinessAsync(ErrorCodes.Forbidden,
            () => ctl.Preview(new DynamicReceiptReconciliationReportRequest { GroupBy = "customer" }));
    }

    [Fact]
    public async Task Preview_受限制业务员_仅本人客户进入分组()
    {
        using var db = TestDbFactory.Create();
        var user = SeedUser(db, "alice");
        var role = SeedRole(db, "Sales");
        SeedUserRole(db, user.Id, role.Id);
        SeedRoleMenu(db, role.Id, SeedMenu(db, DynamicReceiptReconciliationReportRules.RequiredMenuCode).Id);
        var alice = SeedEmployee(db, "alice");
        var mine = SeedCustomer(db, CustomerA, "我的客户", alice.Id);
        SeedCustomer(db, CustomerB, "别人的客户", alice.Id + 1000);

        SeedOrder(db, "SO-MINE", mine.Id, Currency.USD, 100m);
        SeedOrder(db, "SO-OTHER", CustomerB, Currency.USD, 300m);
        SeedReceipt(db, "SK-MINE", mine.Id, 40m, Currency.USD, DocumentStatus.Approved);
        SeedReceipt(db, "SK-OTHER", CustomerB, 50m, Currency.USD, DocumentStatus.Approved);
        await db.SaveChangesAsync();

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var page = PreviewOk(await ctl.Preview(new DynamicReceiptReconciliationReportRequest { GroupBy = "customer" }));

        Assert.Equal(1, page.Total);
        var orderGroup = Assert.Single(page.OrderGroups!);
        Assert.Equal($"customer:{mine.Id}", orderGroup.Key);
        Assert.Equal(1, orderGroup.OrderCount);
        var receiptGroup = Assert.Single(page.ReceiptGroups!);
        Assert.Equal($"customer:{mine.Id}", receiptGroup.Key);
        Assert.Equal(1, receiptGroup.ReceiptCount);
    }

    [Fact]
    public async Task Preview_空页_分组为空()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var page = PreviewOk(await ctl.Preview(new DynamicReceiptReconciliationReportRequest { GroupBy = "customer" }));

        Assert.Equal(0, page.Total);
        Assert.Empty(page.Rows);
        Assert.Empty(page.OrderGroups!);
        Assert.Empty(page.ReceiptGroups!);
    }

    // ==================== 4. 控制器集成：状态隔离 / 页上限 / 只读 ====================

    [Fact]
    public async Task Preview_收款证据状态分组_隔离active_pending_historical()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "甲客户");
        SeedOrder(db, "SO-1", CustomerA, Currency.USD, 100m);
        SeedReceipt(db, "SK-ACT", CustomerA, 10m, Currency.USD, DocumentStatus.Approved);
        SeedReceipt(db, "SK-PEND", CustomerA, 20m, Currency.USD, DocumentStatus.Pending);
        SeedReceipt(db, "SK-HIST", CustomerA, 30m, Currency.USD, DocumentStatus.Cancelled);
        await db.SaveChangesAsync();

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var page = PreviewOk(await ctl.Preview(new DynamicReceiptReconciliationReportRequest
        {
            ReceiptStatus = "all",
            GroupBy = "receiptEvidenceStatus",
        }));

        Assert.Equal("receiptEvidenceStatus", page.GroupBy);
        Assert.Empty(page.OrderGroups!);
        var byKey = page.ReceiptGroups!.ToDictionary(g => g.Key, StringComparer.Ordinal);
        Assert.Equal(3, byKey.Count);
        Assert.Equal(1, byKey["evidence:active"].ReceiptCount);
        Assert.Equal(1, byKey["evidence:pending"].ReceiptCount);
        Assert.Equal(1, byKey["evidence:historical"].ReceiptCount);
        Assert.All(page.ReceiptGroups!, g => Assert.False(g.Truncated));
    }

    [Fact]
    public async Task Preview_分组只统计当前页()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "甲客户");
        SeedOrder(db, "SO-1", CustomerA, Currency.USD, 100m);
        SeedOrder(db, "SO-2", CustomerA, Currency.USD, 100m);
        SeedOrder(db, "SO-3", CustomerA, Currency.USD, 100m);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var page = PreviewOk(await ctl.Preview(new DynamicReceiptReconciliationReportRequest
        {
            GroupBy = "customer",
            PageSize = 2,
        }));

        Assert.Equal(3, page.Total);
        Assert.Equal(2, page.Rows.Count);
        var group = Assert.Single(page.OrderGroups!);
        Assert.Equal(2, group.OrderCount);
    }

    [Fact]
    public async Task Preview_页大小超上限_拒绝()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => ctl.Preview(new DynamicReceiptReconciliationReportRequest { GroupBy = "customer", PageSize = 201 }));
    }

    [Fact]
    public async Task Preview_分组只读不写库()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "甲客户");
        SeedOrder(db, "SO-1", CustomerA, Currency.USD, 100m);
        SeedReceipt(db, "SK-1", CustomerA, 30m, Currency.USD, DocumentStatus.Approved);
        await db.SaveChangesAsync();

        var receiptsBefore = db.FinanceReceipts.Count();
        var ordersBefore = db.SalesOrders.Count();
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        await ctl.Preview(new DynamicReceiptReconciliationReportRequest { GroupBy = "currency" });

        Assert.Equal(receiptsBefore, db.FinanceReceipts.Count());
        Assert.Equal(ordersBefore, db.SalesOrders.Count());
        Assert.DoesNotContain(db.ChangeTracker.Entries(), e => e.State != EntityState.Unchanged);
    }
}
