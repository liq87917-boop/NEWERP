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
/// ERP-160 动态销售订单出货 / 财务进度报表「授权页面按客户 / 币种 / 出货状态 / 收款链接状态的销售订单张数分布」单元测试。
/// <para>页面张数语义：<see cref="DynamicShipmentFinanceReportRules.BuildGroupCounts"/> 只对「当前授权预览页」的销售订单出货 / 财务进度证据行计数，
/// 绝不求和任何金额或数量、绝不跨币种合并或换算；customer / currency 为动态分组（只出现本页存在的取值），
/// shipmentStatus / financeLinkStatus 为固定证据分类，unknown 出货 / 收款链接类别始终保留（计数可为 0）。</para>
/// 覆盖：分组键规范化（全键 + 大小写不敏感 + 默认 none）、未知分组键（fail closed，源读取之前拒绝）、
/// 客户 / 币种 / 出货状态 / 收款链接状态分组、未知证据保留、空页 / 分页（只统计当前页）、
/// 无销售订单菜单授权（权限不足）、无身份（未认证）与只读不写库。
/// <para>全部使用内存数据库（TestDbFactory），不连接 SQL Server、不启动 API、不执行任何 SQL / seed。</para>
/// </summary>
public class DynamicShipmentFinanceGroupingTests
{
    private static readonly DateTime AsOf = new(2026, 9, 24);
    private const long CustomerA = 956001L;
    private const long CustomerB = 956002L;
    private const long ProductA = 956101L;

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

    /// <summary>播种一个「系统内置角色 + 销售订单菜单授权」用户</summary>
    private static long SeedPrivilegedUser(ErpDbContext db, string userName = "priv")
    {
        var user = SeedUser(db, userName);
        var role = SeedRole(db, $"Role-{userName}", isSystem: true);
        SeedUserRole(db, user.Id, role.Id);
        SeedRoleMenu(db, role.Id, SeedMenu(db, DynamicShipmentFinanceReportRules.RequiredMenuCode).Id);
        return user.Id;
    }

    private static BaseCustomer SeedCustomer(ErpDbContext db, long id, string name)
    {
        var customer = new BaseCustomer
        {
            Id = id,
            CustomerCode = $"C{id}",
            CustomerName = name,
            Status = 1,
            CreditStatus = "正常"
        };
        db.BaseCustomers.Add(customer);
        db.SaveChanges();
        return customer;
    }

    private static SalesOrder SeedOrder(ErpDbContext db, string orderNo, long customerId, Currency currency,
        decimal totalAmount, DateTime? orderDate = null, DocumentStatus status = DocumentStatus.Approved)
    {
        var order = new SalesOrder
        {
            OrderNo = orderNo,
            OrderDate = orderDate ?? AsOf.AddDays(-10),
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

    private static void SeedDetail(ErpDbContext db, long salesOrderId, long productId, decimal quantity)
    {
        db.SalesOrderDetails.Add(new SalesOrderDetail
        {
            SalesOrderId = salesOrderId,
            ProductId = productId,
            ProductName = $"商品{productId}",
            Spec = "规格A",
            Unit = "PCS",
            Quantity = quantity,
            UnitPrice = 10m,
            Amount = quantity * 10m,
        });
        db.SaveChanges();
    }

    private static void SeedStockOut(ErpDbContext db, string stockOutNo, long salesOrderId,
        DocumentStatus status, params (long ProductId, decimal Quantity)[] lines)
    {
        var stockOut = new StockOut
        {
            StockOutNo = stockOutNo,
            StockOutDate = AsOf.AddDays(-5),
            SalesOrderId = salesOrderId,
            CustomerId = CustomerA,
            WarehouseId = 1,
            Status = status,
        };
        db.StockOuts.Add(stockOut);
        db.SaveChanges();
        foreach (var (productId, quantity) in lines)
        {
            db.StockOutDetails.Add(new StockOutDetail
            {
                StockOutId = stockOut.Id,
                ProductId = productId,
                ProductName = $"商品{productId}",
                Unit = "PCS",
                Quantity = quantity,
            });
        }

        db.SaveChanges();
    }

    private static DynamicShipmentFinanceReportController NewController(ErpDbContext db) => new(db);

    private static DynamicShipmentFinanceReportPageDto PreviewOk(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<ApiResponse<DynamicShipmentFinanceReportPageDto>>(ok.Value);
        Assert.NotNull(resp.Data);
        return resp.Data!;
    }

    private static Dictionary<string, object?> Row(
        long customerId, string customerName, string currency, string shipmentStatus, string financeLinkStatus)
        => new(StringComparer.Ordinal)
        {
            ["customerId"] = customerId,
            ["customerName"] = customerName,
            ["currency"] = currency,
            ["shipmentStatus"] = shipmentStatus,
            ["financeLinkStatus"] = financeLinkStatus,
        };

    private static int CountOf(IEnumerable<DynamicShipmentFinanceReportGroupDto> groups, string key)
        => groups.Where(g => g.Key == key).Select(g => g.Count).FirstOrDefault();

    // ==================== 1. 分组键规范化（全键 + 大小写不敏感 + 默认 none） ====================

    [Theory]
    [InlineData(null, DynamicShipmentFinanceReportRules.GroupNone)]
    [InlineData("", DynamicShipmentFinanceReportRules.GroupNone)]
    [InlineData("  ", DynamicShipmentFinanceReportRules.GroupNone)]
    [InlineData("none", DynamicShipmentFinanceReportRules.GroupNone)]
    [InlineData("NONE", DynamicShipmentFinanceReportRules.GroupNone)]
    [InlineData("customer", DynamicShipmentFinanceReportRules.GroupCustomer)]
    [InlineData("Customer", DynamicShipmentFinanceReportRules.GroupCustomer)]
    [InlineData("currency", DynamicShipmentFinanceReportRules.GroupCurrency)]
    [InlineData("CURRENCY", DynamicShipmentFinanceReportRules.GroupCurrency)]
    [InlineData("shipmentStatus", DynamicShipmentFinanceReportRules.GroupShipmentStatus)]
    [InlineData("SHIPMENTSTATUS", DynamicShipmentFinanceReportRules.GroupShipmentStatus)]
    [InlineData("financeLinkStatus", DynamicShipmentFinanceReportRules.GroupFinanceLinkStatus)]
    [InlineData("FINANCELINKSTATUS", DynamicShipmentFinanceReportRules.GroupFinanceLinkStatus)]
    public void NormalizeGroupBy_accepts_only_known_keys(string? input, string expected)
    {
        Assert.Equal(expected, DynamicShipmentFinanceReportRules.NormalizeGroupBy(input));
    }

    [Fact]
    public void NormalizeGroupBy_rejects_unknown_key()
    {
        var ex = Assert.Throws<BusinessException>(() =>
            DynamicShipmentFinanceReportRules.NormalizeGroupBy("region"));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    // ==================== 2. 未知分组键在源读取之前拒绝（fail closed） ====================

    [Fact]
    public async Task Preview_rejects_unknown_group_by_before_source_read()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "甲客户");
        SeedOrder(db, "SO-GB-1", CustomerA, Currency.USD, 100m);
        await db.SaveChangesAsync();

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Preview(new DynamicShipmentFinanceReportRequest { GroupBy = "region" }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Contains("分组键", ex.Message);
    }

    // ==================== 3. 按客户分组（动态分组） ====================

    [Fact]
    public async Task Preview_group_by_customer_counts_orders_per_customer()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "甲客户");
        SeedCustomer(db, CustomerB, "乙客户");
        var a1 = SeedOrder(db, "SO-C-1", CustomerA, Currency.USD, 100m);
        SeedDetail(db, a1.Id, ProductA, 10m);
        var a2 = SeedOrder(db, "SO-C-2", CustomerA, Currency.USD, 200m);
        SeedDetail(db, a2.Id, ProductA, 20m);
        var b1 = SeedOrder(db, "SO-C-3", CustomerB, Currency.USD, 300m);
        SeedDetail(db, b1.Id, ProductA, 30m);
        await db.SaveChangesAsync();

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var page = PreviewOk(await ctl.Preview(new DynamicShipmentFinanceReportRequest
        {
            GroupBy = "customer",
            PageSize = 200,
        }));

        Assert.Equal(DynamicShipmentFinanceReportRules.GroupCustomer, page.GroupBy);
        Assert.NotNull(page.Groups);
        Assert.Equal(2, page.Groups!.Count);
        Assert.Equal(2, CountOf(page.Groups, $"customer:{CustomerA}"));
        Assert.Equal(1, CountOf(page.Groups, $"customer:{CustomerB}"));
        Assert.All(page.Groups, g => Assert.True(g.Count >= 0));
    }

    // ==================== 4. 按币种分组（原币分别成行，绝不合并） ====================

    [Fact]
    public async Task Preview_group_by_currency_keeps_currencies_separate()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "甲客户");
        var usd = SeedOrder(db, "SO-CY-USD", CustomerA, Currency.USD, 100m);
        SeedDetail(db, usd.Id, ProductA, 10m);
        var cny = SeedOrder(db, "SO-CY-CNY", CustomerA, Currency.CNY, 200m);
        SeedDetail(db, cny.Id, ProductA, 20m);
        await db.SaveChangesAsync();

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var page = PreviewOk(await ctl.Preview(new DynamicShipmentFinanceReportRequest
        {
            GroupBy = "currency",
            PageSize = 200,
        }));

        Assert.Equal(DynamicShipmentFinanceReportRules.GroupCurrency, page.GroupBy);
        Assert.NotNull(page.Groups);
        Assert.Equal(2, page.Groups!.Count);
        Assert.Equal(1, CountOf(page.Groups, "currency:CNY"));
        Assert.Equal(1, CountOf(page.Groups, "currency:USD"));
    }

    // ==================== 5. 按出货状态 / 收款链接状态分组（固定分类，unknown 保持可见） ====================

    [Fact]
    public void BuildGroupCounts_shipment_status_keeps_all_classes_including_unknown()
    {
        var rows = new List<IReadOnlyDictionary<string, object?>>
        {
            Row(CustomerA, "甲客户", "USD", "none", "unlinked"),
            Row(CustomerB, "乙客户", "USD", "complete", "linked"),
            Row(CustomerB, "乙客户", "USD", "unknown", "unknown"),
        };

        var groups = DynamicShipmentFinanceReportRules.BuildGroupCounts(rows, "shipmentStatus");

        Assert.Equal(5, groups.Count);
        Assert.Equal(1, CountOf(groups, "shipmentStatus:none"));
        Assert.Equal(0, CountOf(groups, "shipmentStatus:partial"));
        Assert.Equal(1, CountOf(groups, "shipmentStatus:complete"));
        Assert.Equal(0, CountOf(groups, "shipmentStatus:over_shipped"));
        Assert.Equal(1, CountOf(groups, "shipmentStatus:unknown"));
        Assert.Contains(groups, g => g.Key == "shipmentStatus:unknown" && g.Label == "未知（超出派生上限）");
    }

    [Fact]
    public void BuildGroupCounts_finance_link_status_keeps_all_classes_including_unknown()
    {
        var rows = new List<IReadOnlyDictionary<string, object?>>
        {
            Row(CustomerA, "甲客户", "USD", "none", "linked"),
            Row(CustomerA, "甲客户", "USD", "none", "partial"),
            Row(CustomerB, "乙客户", "USD", "none", "unlinked"),
            Row(CustomerB, "乙客户", "USD", "none", "unknown"),
        };

        var groups = DynamicShipmentFinanceReportRules.BuildGroupCounts(rows, "financeLinkStatus");

        Assert.Equal(4, groups.Count);
        Assert.Equal(1, CountOf(groups, "financeLinkStatus:linked"));
        Assert.Equal(1, CountOf(groups, "financeLinkStatus:partial"));
        Assert.Equal(1, CountOf(groups, "financeLinkStatus:unlinked"));
        Assert.Equal(1, CountOf(groups, "financeLinkStatus:unknown"));
        Assert.Contains(groups, g => g.Key == "financeLinkStatus:unknown" && g.Label == "未知（超出派生上限）");
    }

    // ==================== 6. 空页：动态分组空、固定分类全部为 0 ====================

    [Fact]
    public async Task Preview_empty_page_groups_are_empty_or_all_zero()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var customerPage = PreviewOk(await ctl.Preview(new DynamicShipmentFinanceReportRequest
        {
            GroupBy = "customer",
        }));
        Assert.Empty(customerPage.Groups!);

        var shipmentPage = PreviewOk(await ctl.Preview(new DynamicShipmentFinanceReportRequest
        {
            GroupBy = "shipmentStatus",
        }));
        Assert.Equal(5, shipmentPage.Groups!.Count);
        Assert.All(shipmentPage.Groups, g => Assert.Equal(0, g.Count));

        var financePage = PreviewOk(await ctl.Preview(new DynamicShipmentFinanceReportRequest
        {
            GroupBy = "financeLinkStatus",
        }));
        Assert.Equal(4, financePage.Groups!.Count);
        Assert.All(financePage.Groups, g => Assert.Equal(0, g.Count));
    }

    // ==================== 7. 分页：分组只统计当前页，绝不跨页合并 ====================

    [Fact]
    public async Task Preview_grouping_counts_only_current_page()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "甲客户");
        SeedCustomer(db, CustomerB, "乙客户");
        var a1 = SeedOrder(db, "SO-P-1", CustomerA, Currency.USD, 100m);
        SeedDetail(db, a1.Id, ProductA, 10m);
        var b1 = SeedOrder(db, "SO-P-2", CustomerB, Currency.USD, 200m);
        SeedDetail(db, b1.Id, ProductA, 20m);
        await db.SaveChangesAsync();

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var firstPage = PreviewOk(await ctl.Preview(new DynamicShipmentFinanceReportRequest
        {
            GroupBy = "customer",
            Page = 1,
            PageSize = 1,
        }));
        Assert.Equal(2, firstPage.Total);
        Assert.Single(firstPage.Groups!);
        Assert.Equal(1, firstPage.Groups![0].Count);
        Assert.Equal($"customer:{CustomerA}", firstPage.Groups[0].Key);

        var secondPage = PreviewOk(await ctl.Preview(new DynamicShipmentFinanceReportRequest
        {
            GroupBy = "customer",
            Page = 2,
            PageSize = 1,
        }));
        Assert.Single(secondPage.Groups!);
        Assert.Equal(1, secondPage.Groups![0].Count);
        Assert.Equal($"customer:{CustomerB}", secondPage.Groups[0].Key);
    }

    // ==================== 8. 授权（fail closed） ====================

    [Fact]
    public async Task Preview_grouping_denied_without_menu()
    {
        using var db = TestDbFactory.Create();
        var user = SeedUser(db, "no-menu");
        var role = SeedRole(db, "NoMenu", isSystem: false);
        SeedUserRole(db, user.Id, role.Id);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Preview(
            new DynamicShipmentFinanceReportRequest { GroupBy = "customer" }));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    [Fact]
    public async Task Preview_grouping_denied_without_identity()
    {
        using var db = TestDbFactory.Create();
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, null);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Preview(
            new DynamicShipmentFinanceReportRequest { GroupBy = "customer" }));
        Assert.Equal(ErrorCodes.Unauthorized, ex.Code);
    }

    // ==================== 9. 只读不写库 ====================

    [Fact]
    public async Task Preview_grouping_does_not_write_to_database()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "甲客户");
        var order = SeedOrder(db, "SO-NW-1", CustomerA, Currency.USD, 500m);
        SeedDetail(db, order.Id, ProductA, 5m);
        SeedStockOut(db, "CK-NW-1", order.Id, DocumentStatus.Approved, (ProductA, 2m));
        await db.SaveChangesAsync();

        var salesOrdersBefore = db.SalesOrders.Count();
        var detailsBefore = db.SalesOrderDetails.Count();
        var stockOutsBefore = db.StockOuts.Count();

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);
        await ctl.Preview(new DynamicShipmentFinanceReportRequest { GroupBy = "customer" });

        Assert.Equal(salesOrdersBefore, db.SalesOrders.Count());
        Assert.Equal(detailsBefore, db.SalesOrderDetails.Count());
        Assert.Equal(stockOutsBefore, db.StockOuts.Count());
        Assert.DoesNotContain(db.ChangeTracker.Entries(), e => e.State != EntityState.Unchanged);
    }

    // ==================== 10. 分组 DTO 只暴露键 / 文案 / 张数，绝不求和金额 / 数量 ====================

    [Fact]
    public void GroupDto_exposes_only_key_label_count_never_sums_quantities_or_currencies()
    {
        var names = typeof(DynamicShipmentFinanceReportGroupDto).GetProperties().Select(p => p.Name).ToList();
        Assert.Contains("Key", names);
        Assert.Contains("Label", names);
        Assert.Contains("Count", names);
        Assert.DoesNotContain(names, n => n.Contains("Amount", StringComparison.Ordinal));
        Assert.DoesNotContain(names, n => n.Contains("Quantity", StringComparison.Ordinal));
        Assert.DoesNotContain(names, n => n.Contains("Total", StringComparison.Ordinal));
    }
}




