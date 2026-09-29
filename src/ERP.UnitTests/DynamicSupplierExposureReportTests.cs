using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using System.Reflection;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-148 动态供应商采购敞口预览（只读、有界）单元测试。
/// 覆盖：字段白名单目录、选定列与顺序、供应商 / 币种 / 订单日期 / 链接状态筛选、
/// 币种与未知结算金额证据保留、稳定分页与 200 上限、无身份（未认证）、无采购订单菜单授权（权限不足）、
/// 无效字段 / 无效筛选 / 页大小超限、以及只读不写库。
/// <para>全部使用内存数据库（TestDbFactory），不连接 SQL Server、不启动 API、不执行任何 SQL / seed。</para>
/// </summary>
public class DynamicSupplierExposureReportTests
{
    private static readonly DateTime AsOf = new(2026, 9, 24);
    private const long SupplierA = 968001L;
    private const long SupplierB = 968002L;
    private const long ProductA = 968101L;

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

    /// <summary>播种一个「系统内置角色 + 采购订单菜单授权」用户</summary>
    private static long SeedPrivilegedUser(ErpDbContext db, string userName = "priv")
    {
        var user = SeedUser(db, userName);
        var role = SeedRole(db, $"Role-{userName}", isSystem: true);
        SeedUserRole(db, user.Id, role.Id);
        SeedRoleMenu(db, role.Id, SeedMenu(db, "purchase-order").Id);
        return user.Id;
    }

    private static void SeedSupplier(ErpDbContext db, long id, string name)
        => db.BaseSuppliers.Add(new BaseSupplier { Id = id, SupplierCode = $"S{id}", SupplierName = name });

    private static SalesOrder SeedSalesOrder(ErpDbContext db, string orderNo)
    {
        var salesOrder = new SalesOrder
        {
            OrderNo = orderNo,
            OrderDate = AsOf.AddDays(-30),
            CustomerId = 1,
            Currency = Currency.CNY,
            Status = DocumentStatus.Approved,
            CreatedAt = new DateTime(2026, 8, 25, 8, 0, 0),
        };
        db.SalesOrders.Add(salesOrder);
        db.SaveChanges();
        return salesOrder;
    }

    /// <summary>写入一张采购订单（含 1 行明细；明细数量用于收货派生的冲抵口径）</summary>
    private static PurchaseOrder SeedOrder(ErpDbContext db, string orderNo, long supplierId, Currency currency,
        decimal totalAmount, long? owningSalesOrderId, (long ProductId, decimal Quantity) line,
        DateTime? orderDate = null)
    {
        var order = new PurchaseOrder
        {
            OrderNo = orderNo,
            OrderDate = orderDate ?? AsOf.AddDays(-10),
            SupplierId = supplierId,
            Currency = currency,
            TotalAmount = totalAmount,
            OwningSalesOrderId = owningSalesOrderId,
            OwningSalesOrderNo = owningSalesOrderId.HasValue ? $"SO#{owningSalesOrderId.Value}" : string.Empty,
            Status = DocumentStatus.Approved,
            CreatedAt = new DateTime(2026, 9, 14, 8, 0, 0),
        };
        db.PurchaseOrders.Add(order);
        db.SaveChanges();
        db.PurchaseOrderDetails.Add(new PurchaseOrderDetail
        {
            PurchaseOrderId = order.Id,
            ProductId = line.ProductId,
            ProductName = $"商品{line.ProductId}",
            Spec = "规格A",
            Unit = "PCS",
            Quantity = line.Quantity,
            UnitPrice = line.Quantity == 0 ? 0m : totalAmount / line.Quantity,
            Amount = totalAmount,
        });
        db.SaveChanges();
        return order;
    }

    private static DynamicSupplierExposureReportController NewController(ErpDbContext db)
        => new(db);

    private static DynamicSupplierExposureReportCatalogDto CatalogOk(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<ApiResponse<DynamicSupplierExposureReportCatalogDto>>(ok.Value);
        Assert.NotNull(resp.Data);
        return resp.Data!;
    }

    private static DynamicSupplierExposureReportPageDto PreviewOk(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<ApiResponse<DynamicSupplierExposureReportPageDto>>(ok.Value);
        Assert.NotNull(resp.Data);
        return resp.Data!;
    }

    // ==================== 1. 字段白名单目录 ====================

    [Fact]
    public async Task Catalog_returns_finite_field_allowlist_for_authorized_user()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var catalog = CatalogOk(await ctl.Catalog());

        Assert.Equal(DynamicSupplierExposureReportRules.RequiredMenuCode, catalog.RequiredMenuCode);
        Assert.Equal(DynamicSupplierExposureReportRules.RequiredMenuText, catalog.RequiredMenuText);
        Assert.Equal(DynamicSupplierExposureReportRules.MaxPageSize, catalog.MaxPageSize);
        Assert.NotEmpty(catalog.Fields);

        var keys = catalog.Fields.Select(f => f.Key).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("orderNo", keys);
        Assert.Contains("supplierId", keys);
        Assert.Contains("currency", keys);
        Assert.Contains("linkStatus", keys);
        Assert.Contains("settledAmount", keys);
        Assert.Contains("receivedQuantity", keys);
        Assert.DoesNotContain("unknownColumn", keys);
        Assert.All(catalog.Fields, f => Assert.False(string.IsNullOrWhiteSpace(f.Key)));
    }

    // ==================== 2. 授权（fail closed） ====================

    [Fact]
    public async Task Catalog_and_preview_fail_closed_without_identity_or_menu()
    {
        using var db = TestDbFactory.Create();
        var deniedUser = SeedUser(db, "no-menu");
        var ctl = NewController(db);

        TestAuth.SetUser(ctl, null);
        var noIdentity = await Assert.ThrowsAsync<BusinessException>(() => ctl.Catalog());
        Assert.Equal(ErrorCodes.Unauthorized, noIdentity.Code);

        TestAuth.SetUser(ctl, deniedUser.Id);
        var noMenu = await Assert.ThrowsAsync<BusinessException>(() => ctl.Catalog());
        Assert.Equal(ErrorCodes.Forbidden, noMenu.Code);

        var noMenuPreview = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Preview(new DynamicSupplierExposureReportRequest()));
        Assert.Equal(ErrorCodes.Forbidden, noMenuPreview.Code);
    }

    // ==================== 3. 字段 / 筛选 / 页大小校验（源读取之前拒绝） ====================

    [Fact]
    public async Task Preview_rejects_unknown_fields_and_bad_filters_before_source_reads()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        Assert.Equal(ErrorCodes.InvalidParameter, (await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Preview(new DynamicSupplierExposureReportRequest { Fields = new() { "orderNo", "bogus" } }))).Code);
        Assert.Equal(ErrorCodes.InvalidParameter, (await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Preview(new DynamicSupplierExposureReportRequest { SupplierId = -1 }))).Code);
        Assert.Equal(ErrorCodes.InvalidParameter, (await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Preview(new DynamicSupplierExposureReportRequest { Currency = "BOGUS" }))).Code);
        Assert.Equal(ErrorCodes.InvalidParameter, (await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Preview(new DynamicSupplierExposureReportRequest { LinkStatus = "bogus" }))).Code);
        Assert.Equal(ErrorCodes.InvalidParameter, (await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Preview(new DynamicSupplierExposureReportRequest
            {
                OrderDateFrom = new DateTime(2026, 2, 1),
                OrderDateTo = new DateTime(2026, 1, 1)
            }))).Code);
        Assert.Equal(ErrorCodes.InvalidParameter, (await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Preview(new DynamicSupplierExposureReportRequest { PageSize = 201 }))).Code);
        Assert.Equal(ErrorCodes.InvalidParameter, (await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Preview(new DynamicSupplierExposureReportRequest { PageSize = 0 }))).Code);
    }

    // ==================== 4. 选定列与顺序 ====================

    [Fact]
    public async Task Preview_projects_selected_fields_in_requested_order()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedSupplier(db, SupplierA, "甲供应商");
        SeedOrder(db, "PO-EXP-1", SupplierA, Currency.CNY, 100m, null, (ProductA, 1m));
        await db.SaveChangesAsync();

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var page = PreviewOk(await ctl.Preview(new DynamicSupplierExposureReportRequest
        {
            Fields = new() { "currency", "orderNo", "orderedAmount" },
        }));

        Assert.Equal(new[] { "currency", "orderNo", "orderedAmount" }, page.Columns.Select(c => c.Key));
        var row = Assert.Single(page.Rows);
        Assert.Equal(new[] { "currency", "orderNo", "orderedAmount" }, row.Keys);
        Assert.Equal("CNY", row["currency"]);
        Assert.Equal("PO-EXP-1", row["orderNo"]);
        Assert.Equal(100m, row["orderedAmount"]);
    }

    // ==================== 5. 链接不唯一 / 无可用引用：未知金额照实保留（null，绝不回落为 0） ====================

    [Fact]
    public async Task Preview_keeps_ambiguous_and_unavailable_settlement_unknown()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedSupplier(db, SupplierA, "甲供应商");
        var salesOrder = SeedSalesOrder(db, "SO-EXP-3");
        SeedOrder(db, "PO-EXP-6", SupplierA, Currency.CNY, 100m, salesOrder.Id, (ProductA, 1m));
        SeedOrder(db, "PO-EXP-7", SupplierA, Currency.CNY, 200m, salesOrder.Id, (ProductA, 1m)); // 同销售订单两张 → 不唯一
        SeedOrder(db, "PO-EXP-8", SupplierA, Currency.CNY, 50m, null, (ProductA, 1m));          // 无归属销售订单
        await db.SaveChangesAsync();

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var page = PreviewOk(await ctl.Preview(new DynamicSupplierExposureReportRequest
        {
            Fields = new() { "orderNo", "linkStatus", "settledAmount", "outstandingAmount", "submittedAmount" },
        }));

        Assert.Equal(3, page.Rows.Count);
        var byNo = page.Rows.ToDictionary(r => (string)r["orderNo"]!);

        Assert.Equal(DynamicSupplierExposureReportRules.LinkAmbiguous, byNo["PO-EXP-6"]["linkStatus"]);
        Assert.Null(byNo["PO-EXP-6"]["settledAmount"]);
        Assert.Null(byNo["PO-EXP-6"]["outstandingAmount"]);
        Assert.Null(byNo["PO-EXP-6"]["submittedAmount"]);

        Assert.Equal(DynamicSupplierExposureReportRules.LinkUnavailable, byNo["PO-EXP-8"]["linkStatus"]);
        Assert.Null(byNo["PO-EXP-8"]["settledAmount"]);
    }

    // ==================== 6. 币种隔离：原币分别成行，绝不换算 ====================

    [Fact]
    public async Task Preview_preserves_currency_isolation_without_conversion()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedSupplier(db, SupplierA, "甲供应商");
        SeedSupplier(db, SupplierB, "乙供应商");
        SeedOrder(db, "PO-EXP-3", SupplierA, Currency.USD, 200m, null, (ProductA, 1m));
        SeedOrder(db, "PO-EXP-4", SupplierB, Currency.CNY, 300m, null, (ProductA, 1m));
        await db.SaveChangesAsync();

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var page = PreviewOk(await ctl.Preview(new DynamicSupplierExposureReportRequest
        {
            Fields = new() { "orderNo", "currency", "orderedAmount" },
        }));

        Assert.Equal(2, page.Rows.Count);
        var byCurrency = page.Rows.ToDictionary(r => (string)r["currency"]!);
        Assert.Equal(200m, byCurrency["USD"]["orderedAmount"]);
        Assert.Equal(300m, byCurrency["CNY"]["orderedAmount"]);

        // 币种筛选只返回对应原币行
        var usdOnly = PreviewOk(await ctl.Preview(new DynamicSupplierExposureReportRequest
        {
            Fields = new() { "currency" },
            Currency = "USD",
        }));
        Assert.Single(usdOnly.Rows);
        Assert.Equal("USD", usdOnly.Rows[0]["currency"]);
    }

    // ==================== 7. 稳定分页与总数 ====================

    [Fact]
    public async Task Preview_pages_bounded_and_reports_totals()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedSupplier(db, SupplierA, "甲供应商");
        SeedOrder(db, "PO-EXP-1", SupplierA, Currency.CNY, 100m, null, (ProductA, 1m), AsOf.AddDays(-10));
        SeedOrder(db, "PO-EXP-2", SupplierA, Currency.CNY, 50m, null, (ProductA, 1m), AsOf.AddDays(-9));
        SeedOrder(db, "PO-EXP-3", SupplierA, Currency.CNY, 20m, null, (ProductA, 1m), AsOf.AddDays(-8));
        await db.SaveChangesAsync();

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var page1 = PreviewOk(await ctl.Preview(new DynamicSupplierExposureReportRequest
        {
            Fields = new() { "orderNo" },
            Page = 1,
            PageSize = 2,
        }));
        Assert.Equal(3, page1.Total);
        Assert.Equal(2, page1.PageSize);
        Assert.Equal(2, page1.Rows.Count);
        Assert.Equal(2, page1.TotalPages);

        var page2 = PreviewOk(await ctl.Preview(new DynamicSupplierExposureReportRequest
        {
            Fields = new() { "orderNo" },
            Page = 2,
            PageSize = 2,
        }));
        Assert.Single(page2.Rows);
    }

    // ==================== 8. 只读不写库 ====================

    [Fact]
    public async Task Preview_is_read_only_and_writes_nothing()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedSupplier(db, SupplierA, "甲供应商");
        SeedOrder(db, "PO-EXP-1", SupplierA, Currency.CNY, 100m, null, (ProductA, 1m));
        await db.SaveChangesAsync();

        var counting = CountingDbContext.Wrap(db);
        var ctl = new DynamicSupplierExposureReportController(counting.Proxy);
        TestAuth.SetUser(ctl, uid);

        var page = PreviewOk(await ctl.Preview(new DynamicSupplierExposureReportRequest
        {
            Fields = new() { "orderNo" },
            PageSize = 10,
        }));
        Assert.Equal(1, page.Total);
        Assert.Equal(0, counting.WriteCalls);
    }

    // ==================== 9. 只读计数上下文（断言不写库） ====================

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
