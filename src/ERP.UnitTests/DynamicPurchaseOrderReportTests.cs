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
/// ERP-125 动态采购订单报表预览（只读、有界）单元测试。
/// 覆盖：字段白名单目录、选定列与顺序、供应商 / 日期 / 状态 / 币种筛选、稳定分页与 100 上限、
/// 软删除过滤、无身份（未认证）、无采购订单菜单授权（权限不足）、
/// 无效字段 / 无效日期区间 / 无效状态与币种 / 页大小超限、以及只读不写库。
/// <para>全部使用内存数据库（TestDbFactory），不连接 SQL Server、不启动 API、不执行任何 SQL / seed。</para>
/// </summary>
public class DynamicPurchaseOrderReportTests
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
        DateTime? orderDate = null, DocumentStatus status = DocumentStatus.Pending, Currency currency = Currency.CNY,
        decimal totalAmount = 100, bool isDeleted = false)
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

    // ==================== 1. 字段目录 ====================

    [Fact]
    public async Task Catalog_返回有限白名单字段目录()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var ok = Assert.IsType<OkObjectResult>(await ctl.Catalog());
        var resp = Assert.IsType<ApiResponse<DynamicPurchaseOrderReportCatalogDto>>(ok.Value);
        var catalog = resp.Data!;

        Assert.Equal(DynamicPurchaseOrderReportRules.RequiredMenuCode, catalog.RequiredMenuCode);
        Assert.Equal(100, catalog.MaxPageSize);
        Assert.Equal(24, catalog.Fields.Count);

        var keys = catalog.Fields.Select(f => f.Key).ToHashSet();
        Assert.Contains("orderNo", keys);
        Assert.Contains("orderDate", keys);
        Assert.Contains("supplierId", keys);
        Assert.Contains("currency", keys);
        Assert.Contains("status", keys);
        Assert.DoesNotContain("settlementProgress", keys); // 财务证据边界：不含结算进度
        Assert.All(catalog.Fields, f => Assert.False(string.IsNullOrWhiteSpace(f.Key)));
    }

    [Fact]
    public async Task Catalog_无采购订单菜单授权_拒绝()
    {
        using var db = TestDbFactory.Create();
        var user = SeedUser(db, "nomenu");
        var role = SeedRole(db, "Role-nomenu", isSystem: false);
        SeedUserRole(db, user.Id, role.Id);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Catalog());
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    [Fact]
    public async Task Catalog_无身份_未认证拒绝()
    {
        using var db = TestDbFactory.Create();
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, null);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Catalog());
        Assert.Equal(ErrorCodes.Unauthorized, ex.Code);
    }

    // ==================== 2. 选定列 ====================

    [Fact]
    public async Task Preview_选中列_只返回选定字段并保持请求顺序()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedOrder(db, "PO-1", 1L);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var page = PreviewOk(await ctl.Preview(new DynamicPurchaseOrderReportRequest
        {
            Fields = new() { "totalAmount", "orderNo", "status" }
        }));

        Assert.Equal(new[] { "totalAmount", "orderNo", "status" }, page.Columns.Select(c => c.Key));
        Assert.Single(page.Rows);
        Assert.Equal(new[] { "totalAmount", "orderNo", "status" }, page.Rows[0].Keys.ToArray());
        Assert.Equal("PO-1", page.Rows[0]["orderNo"]);
        Assert.Equal("Pending", page.Rows[0]["status"]);
    }

    [Fact]
    public async Task Preview_未指定字段_默认返回全部白名单字段()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedOrder(db, "PO-1", 1L);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var page = PreviewOk(await ctl.Preview(new DynamicPurchaseOrderReportRequest()));

        Assert.Equal(24, page.Columns.Count);
        Assert.Single(page.Rows);
        Assert.Equal(24, page.Rows[0].Count);
    }

    // ==================== 3. 筛选 ====================

    [Fact]
    public async Task Preview_按供应商_日期_状态_币种过滤()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedOrder(db, "A", 10L, new DateTime(2026, 1, 10), DocumentStatus.Approved, Currency.USD);
        SeedOrder(db, "B", 20L, new DateTime(2026, 2, 10), DocumentStatus.Pending, Currency.CNY);
        SeedOrder(db, "C", 10L, new DateTime(2026, 3, 10), DocumentStatus.Approved, Currency.EUR);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var page = PreviewOk(await ctl.Preview(new DynamicPurchaseOrderReportRequest
        {
            Fields = new() { "orderNo" },
            SupplierId = 10L,
            StartDate = new DateTime(2026, 1, 1),
            EndDate = new DateTime(2026, 3, 31),
            Status = "approved",
            Currency = "usd"
        }));

        Assert.Equal(1, page.Total);
        Assert.Single(page.Rows);
        Assert.Equal("A", page.Rows[0]["orderNo"]);
    }

    [Fact]
    public async Task Preview_软删除订单_被过滤()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedOrder(db, "PO-KEEP", 1L);
        SeedOrder(db, "PO-DELETED", 1L, isDeleted: true);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var page = PreviewOk(await ctl.Preview(new DynamicPurchaseOrderReportRequest
        {
            Fields = new() { "orderNo" }, PageSize = 100
        }));

        Assert.Equal(1, page.Total);
        Assert.Single(page.Rows);
        Assert.Equal("PO-KEEP", page.Rows[0]["orderNo"]);
    }

    // ==================== 4. 分页 ====================

    [Fact]
    public async Task Preview_分页稳定且单页上限100()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        for (var i = 1; i <= 130; i++)
            SeedOrder(db, $"PO-{i}", 1L, new DateTime(2026, 1, 1).AddDays(i % 30));
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var p1 = PreviewOk(await ctl.Preview(new DynamicPurchaseOrderReportRequest
        {
            Fields = new() { "orderNo" }, Page = 1, PageSize = 100
        }));
        Assert.Equal(130, p1.Total);
        Assert.Equal(100, p1.Rows.Count);
        Assert.Equal(2, p1.TotalPages);

        var p2 = PreviewOk(await ctl.Preview(new DynamicPurchaseOrderReportRequest
        {
            Fields = new() { "orderNo" }, Page = 2, PageSize = 100
        }));
        Assert.Equal(30, p2.Rows.Count);
    }

    // ==================== 5. 无效输入（查询前拒绝） ====================

    [Fact]
    public async Task Preview_页大小超限或非法_拒绝()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Preview(
            new DynamicPurchaseOrderReportRequest { PageSize = 101 }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);

        var ex2 = await Assert.ThrowsAsync<BusinessException>(() => ctl.Preview(
            new DynamicPurchaseOrderReportRequest { PageSize = 0 }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex2.Code);
    }

    [Fact]
    public async Task Preview_未知字段_拒绝()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Preview(
            new DynamicPurchaseOrderReportRequest { Fields = new() { "orderNo", "bogus" } }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public async Task Preview_日期区间无效_拒绝()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Preview(
            new DynamicPurchaseOrderReportRequest
            {
                StartDate = new DateTime(2026, 2, 1),
                EndDate = new DateTime(2026, 1, 1)
            }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public async Task Preview_无效状态或币种_拒绝()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var ex1 = await Assert.ThrowsAsync<BusinessException>(() => ctl.Preview(
            new DynamicPurchaseOrderReportRequest { Status = "Bogus" }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex1.Code);

        var ex2 = await Assert.ThrowsAsync<BusinessException>(() => ctl.Preview(
            new DynamicPurchaseOrderReportRequest { Currency = "BOGUS" }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex2.Code);
    }

    // ==================== 6. 未授权 ====================

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
            new DynamicPurchaseOrderReportRequest()));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    [Fact]
    public async Task Preview_无身份_未认证拒绝()
    {
        using var db = TestDbFactory.Create();
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, null);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Preview(
            new DynamicPurchaseOrderReportRequest()));
        Assert.Equal(ErrorCodes.Unauthorized, ex.Code);
    }

    // ==================== 7. 只读不写库 ====================

    [Fact]
    public async Task Preview_只读不写库()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedOrder(db, "PO-R", 1L);

        var counting = CountingDbContext.Wrap(db);
        var ctl = new DynamicPurchaseOrderReportController(new DynamicPurchaseOrderReportQuery(counting.Proxy));
        TestAuth.SetUser(ctl, uid);

        var page = PreviewOk(await ctl.Preview(new DynamicPurchaseOrderReportRequest
        {
            Fields = new() { "orderNo" }, PageSize = 10
        }));
        Assert.Equal(1, page.Total);
        Assert.Equal(0, counting.WriteCalls);
    }

    // ==================== 8. 只读计数上下文（断言不写库） ====================

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
