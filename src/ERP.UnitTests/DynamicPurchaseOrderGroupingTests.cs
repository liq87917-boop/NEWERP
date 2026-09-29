using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using ERP.Infrastructure.Reports;
using Microsoft.AspNetCore.Mvc;
using System.Reflection;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-128 动态采购订单报表「货币安全分组与页面小计」单元测试。
/// <para>页面小计语义：<see cref="DynamicPurchaseOrderReportRules.BuildGroupSubtotals"/> 只对「当前预览页」的已授权行聚合，
/// 分组内按币种分开统计条数与金额（金额只对同币种求和、绝不跨币种相加），并非全量合计。</para>
/// 覆盖：混合币种不合并、月份边界、选定字段自动补齐、空页、无效分组键（fail closed）与只读不写库。
/// <para>全部使用内存数据库（TestDbFactory），不连接 SQL Server、不启动 API、不执行任何 SQL / seed。</para>
/// </summary>
public class DynamicPurchaseOrderGroupingTests
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

    private static PurchaseOrder SeedOrder(ErpDbContext db, string orderNo, long supplierId,
        DateTime? orderDate = null, DocumentStatus status = DocumentStatus.Pending,
        Currency currency = Currency.CNY, decimal totalAmount = 100m, bool isDeleted = false)
    {
        var order = new PurchaseOrder
        {
            OrderNo = orderNo,
            SupplierId = supplierId,
            OrderDate = orderDate ?? new DateTime(2026, 9, 1),
            Status = status,
            Currency = currency,
            TotalAmount = totalAmount,
            IsDeleted = isDeleted
        };
        db.PurchaseOrders.Add(order);
        db.SaveChanges();
        return order;
    }

    /// <summary>播种一个「特权 + 采购订单菜单授权」用户（系统内置角色）</summary>
    private static long SeedPrivilegedUser(ErpDbContext db, string userName = "priv")
    {
        var user = SeedUser(db, userName);
        var role = SeedRole(db, $"Role-{userName}", isSystem: true);
        SeedUserRole(db, user.Id, role.Id);
        SeedRoleMenu(db, role.Id, SeedMenu(db, "purchase-order").Id);
        return user.Id;
    }

    private static DynamicPurchaseOrderReportController NewController(IErpDbContext db)
        => new(new DynamicPurchaseOrderReportQuery(db));

    private static DynamicPurchaseOrderReportPageDto PreviewOk(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<ApiResponse<DynamicPurchaseOrderReportPageDto>>(ok.Value);
        Assert.NotNull(resp.Data);
        return resp.Data!;
    }

    // ==================== 1. 分组键规范化（fail closed） ====================

    [Theory]
    [InlineData(null, "none")]
    [InlineData("", "none")]
    [InlineData("  ", "none")]
    [InlineData("none", "none")]
    [InlineData("NONE", "none")]
    [InlineData("supplier", "supplier")]
    [InlineData("SUPPLIER", "supplier")]
    [InlineData("month", "month")]
    [InlineData("Month", "month")]
    public void NormalizeGroupBy_仅接受none_supplier_month(string? input, string expected)
    {
        Assert.Equal(expected, DynamicPurchaseOrderReportRules.NormalizeGroupBy(input));
    }

    [Fact]
    public void NormalizeGroupBy_无效分组键_拒绝()
    {
        var ex = Assert.Throws<BusinessException>(() => DynamicPurchaseOrderReportRules.NormalizeGroupBy("bogus"));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    // ==================== 2. 按供应商分组：混合币种绝不合并 ====================

    [Fact]
    public async Task GroupBy_按供应商分组_混合币种_币种分开不合并()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);

        SeedOrder(db, "PO-1", 1L, new DateTime(2026, 9, 1), DocumentStatus.Pending, Currency.CNY, 100m);
        SeedOrder(db, "PO-2", 1L, new DateTime(2026, 9, 2), DocumentStatus.Pending, Currency.USD, 200m);
        SeedOrder(db, "PO-3", 2L, new DateTime(2026, 9, 3), DocumentStatus.Pending, Currency.CNY, 300m);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var page = PreviewOk(await ctl.Preview(new DynamicPurchaseOrderReportRequest
        {
            GroupBy = "supplier",
            PageSize = 100
        }));

        Assert.Equal(3, page.Total);
        Assert.Equal("supplier", page.GroupBy);
        Assert.Equal(2, page.Groups!.Count);

        Assert.Equal("supplier:1", page.Groups[0].Key);
        Assert.Equal("供应商 #1", page.Groups[0].Label);
        Assert.Equal(2, page.Groups[0].Subtotals.Count);
        Assert.Equal("CNY", page.Groups[0].Subtotals[0].Currency);
        Assert.Equal(1, page.Groups[0].Subtotals[0].Count);
        Assert.Equal(100m, page.Groups[0].Subtotals[0].Amount);
        Assert.Equal("USD", page.Groups[0].Subtotals[1].Currency);
        Assert.Equal(1, page.Groups[0].Subtotals[1].Count);
        Assert.Equal(200m, page.Groups[0].Subtotals[1].Amount);

        var g2 = Assert.Single(page.Groups[1].Subtotals);
        Assert.Equal("CNY", g2.Currency);
        Assert.Equal(1, g2.Count);
        Assert.Equal(300m, g2.Amount);
    }

    // ==================== 3. 按月份分组：月份边界 ====================

    [Fact]
    public async Task GroupBy_按月份分组_月份边界_分开统计()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);

        SeedOrder(db, "PO-1", 1L, new DateTime(2026, 1, 31), DocumentStatus.Pending, Currency.CNY, 20m);
        SeedOrder(db, "PO-2", 1L, new DateTime(2026, 1, 15), DocumentStatus.Pending, Currency.USD, 10m);
        SeedOrder(db, "PO-3", 1L, new DateTime(2026, 2, 1), DocumentStatus.Pending, Currency.USD, 30m);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var page = PreviewOk(await ctl.Preview(new DynamicPurchaseOrderReportRequest
        {
            GroupBy = "month",
            PageSize = 100
        }));

        Assert.Equal("month", page.GroupBy);
        Assert.Equal(2, page.Groups!.Count);

        Assert.Equal("month:2026-01", page.Groups[0].Key);
        Assert.Equal("2026年1月", page.Groups[0].Label);
        Assert.Equal("month:2026-02", page.Groups[1].Key);
        Assert.Equal("2026年2月", page.Groups[1].Label);

        Assert.Equal(2, page.Groups[0].Subtotals.Count);
        Assert.Equal("CNY", page.Groups[0].Subtotals[0].Currency);
        Assert.Equal(1, page.Groups[0].Subtotals[0].Count);
        Assert.Equal(20m, page.Groups[0].Subtotals[0].Amount);
        Assert.Equal("USD", page.Groups[0].Subtotals[1].Currency);
        Assert.Equal(1, page.Groups[0].Subtotals[1].Count);
        Assert.Equal(10m, page.Groups[0].Subtotals[1].Amount);

        Assert.Single(page.Groups[1].Subtotals);
        Assert.Equal("USD", page.Groups[1].Subtotals[0].Currency);
        Assert.Equal(1, page.Groups[1].Subtotals[0].Count);
        Assert.Equal(30m, page.Groups[1].Subtotals[0].Amount);
    }

    // ==================== 4. 页面小计：仅当前页、随分页变化 ====================

    [Fact]
    public async Task GroupBy_页面小计_仅统计当前页_随分页变化()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);

        SeedOrder(db, "PO-1", 1L, new DateTime(2026, 9, 1), DocumentStatus.Pending, Currency.USD, 100m);
        SeedOrder(db, "PO-2", 1L, new DateTime(2026, 9, 2), DocumentStatus.Pending, Currency.USD, 200m);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var p1 = PreviewOk(await ctl.Preview(new DynamicPurchaseOrderReportRequest
        {
            GroupBy = "supplier",
            Page = 1,
            PageSize = 1
        }));
        var g1 = Assert.Single(p1.Groups!);
        var s1 = Assert.Single(g1.Subtotals);
        Assert.Equal("USD", s1.Currency);
        Assert.Equal(1, s1.Count);
        Assert.Equal(100m, s1.Amount);

        var p2 = PreviewOk(await ctl.Preview(new DynamicPurchaseOrderReportRequest
        {
            GroupBy = "supplier",
            Page = 2,
            PageSize = 1
        }));
        var g2 = Assert.Single(p2.Groups!);
        var s2 = Assert.Single(g2.Subtotals);
        Assert.Equal(1, s2.Count);
        Assert.Equal(200m, s2.Amount);
    }

    // ==================== 5. 选定字段：分组字段自动补齐且保持请求顺序 ====================

    [Fact]
    public async Task GroupBy_按供应商分组_仅选orderNo_自动补齐计算字段()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);

        SeedOrder(db, "PO-1", 7L, new DateTime(2026, 9, 1), DocumentStatus.Pending, Currency.USD, 123.45m);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var page = PreviewOk(await ctl.Preview(new DynamicPurchaseOrderReportRequest
        {
            Fields = new() { "orderNo" },
            GroupBy = "supplier",
            PageSize = 10
        }));

        Assert.Equal(new[] { "orderNo", "currency", "totalAmount", "supplierId" },
            page.Columns.Select(c => c.Key).ToArray());
        var row = Assert.Single(page.Rows);
        Assert.Equal("PO-1", row["orderNo"]);
        Assert.Equal("USD", row["currency"]);
        Assert.Equal(123.45m, row["totalAmount"]);
        Assert.Equal(7L, row["supplierId"]);

        var g = Assert.Single(page.Groups!);
        var s = Assert.Single(g.Subtotals);
        Assert.Equal("USD", s.Currency);
        Assert.Equal(1, s.Count);
        Assert.Equal(123.45m, s.Amount);
    }

    [Fact]
    public async Task GroupBy_按月份分组_仅选id_自动补齐计算字段()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);

        SeedOrder(db, "PO-1", 1L, new DateTime(2026, 6, 30), DocumentStatus.Pending, Currency.EUR, 50m);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var page = PreviewOk(await ctl.Preview(new DynamicPurchaseOrderReportRequest
        {
            Fields = new() { "id" },
            GroupBy = "month",
            PageSize = 10
        }));

        Assert.Equal(new[] { "id", "currency", "totalAmount", "orderDate" },
            page.Columns.Select(c => c.Key).ToArray());

        var g = Assert.Single(page.Groups!);
        Assert.Equal("month:2026-06", g.Key);
        var s = Assert.Single(g.Subtotals);
        Assert.Equal("EUR", s.Currency);
        Assert.Equal(1, s.Count);
        Assert.Equal(50m, s.Amount);
    }

    // ==================== 6. 空页 ====================

    [Fact]
    public async Task GroupBy_空页_无分组小计()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var page = PreviewOk(await ctl.Preview(new DynamicPurchaseOrderReportRequest
        {
            GroupBy = "supplier",
            PageSize = 100
        }));

        Assert.Equal(0, page.Total);
        Assert.Empty(page.Rows);
        Assert.Equal("supplier", page.GroupBy);
        Assert.NotNull(page.Groups);
        Assert.Empty(page.Groups);
    }

    // ==================== 7. 无效分组键（控制器入口 fail closed） ====================

    [Fact]
    public async Task Preview_无效分组键_拒绝()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Preview(
            new DynamicPurchaseOrderReportRequest { GroupBy = "quarter" }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    // ==================== 8. 无采购订单菜单授权 ====================

    [Fact]
    public async Task Preview_无采购订单菜单授权_拒绝()
    {
        using var db = TestDbFactory.Create();
        var user = SeedUser(db, "nomenu");
        var role = SeedRole(db, "Role-nomenu", isSystem: false);
        SeedUserRole(db, user.Id, role.Id);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Preview(
            new DynamicPurchaseOrderReportRequest { GroupBy = "supplier" }));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    // ==================== 9. 只读不写库 ====================

    [Fact]
    public async Task GroupBy_只读不写库()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedOrder(db, "PO-R", 1L, new DateTime(2026, 9, 1), DocumentStatus.Pending, Currency.USD, 100m);

        var counting = CountingDbContext.Wrap(db);
        var ctl = new DynamicPurchaseOrderReportController(new DynamicPurchaseOrderReportQuery(counting.Proxy));
        TestAuth.SetUser(ctl, uid);

        var page = PreviewOk(await ctl.Preview(new DynamicPurchaseOrderReportRequest
        {
            GroupBy = "supplier",
            PageSize = 10
        }));

        Assert.Equal(1, page.Total);
        Assert.NotEmpty(page.Groups!);
        Assert.Equal(0, counting.WriteCalls);
    }

    // ==================== 10. 只读计数上下文（断言不写库） ====================

    public class CountingDbContext : DispatchProxy
    {
        private IErpDbContext _inner = null!;
        public IErpDbContext Proxy { get; private set; } = null!;
        public int WriteCalls { get; private set; }

        public static CountingDbContext Wrap(IErpDbContext inner)
        {
            var proxy = DispatchProxy.Create<IErpDbContext, CountingDbContext>();
            var counting = (CountingDbContext)(object)proxy;
            counting._inner = inner;
            counting.Proxy = proxy;
            return counting;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod is null) return null;
            if (targetMethod.Name == nameof(IErpDbContext.SaveChangesAsync))
            {
                WriteCalls++;
                return _inner.SaveChangesAsync(args is { Length: > 0 } ? (CancellationToken)args[0]! : default);
            }
            return targetMethod.Invoke(_inner, args);
        }
    }
}
