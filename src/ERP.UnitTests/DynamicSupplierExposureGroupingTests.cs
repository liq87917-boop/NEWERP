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
/// ERP-152 动态供应商采购敞口预览「授权页面按供应商 / 币种 / 链接状态 / 收货状态的采购订单张数分布」单元测试。
/// <para>页面张数语义：<see cref="DynamicSupplierExposureReportRules.BuildGroupCounts"/> 只对「当前授权预览页」的采购订单敞口证据行计数，
/// 绝不求和任何金额或数量、绝不跨币种合并或换算；supplier / currency 为动态分组（只出现本页存在的取值），
/// linkStatus / receiptStatus 为固定证据分类，ambiguous / unavailable 链接与 unknown 收货类别始终保留（计数可为 0）。</para>
/// 覆盖：分组键规范化、供应商 / 币种 / 链接状态 / 收货状态分组、未知证据、空页 / 分页、无效分组键（fail closed）、
/// 无采购订单菜单授权（权限不足）、无身份（未认证）与只读不写库。
/// <para>全部使用内存数据库（TestDbFactory），不连接 SQL Server、不启动 API、不执行任何 SQL / seed。</para>
/// </summary>
public class DynamicSupplierExposureGroupingTests
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
        SeedRoleMenu(db, role.Id, SeedMenu(db, DynamicSupplierExposureReportRules.RequiredMenuCode).Id);
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

    private static StockIn SeedStockIn(ErpDbContext db, string stockInNo, long? purchaseOrderId,
        DocumentStatus status, params (long ProductId, decimal Quantity)[] lines)
    {
        var stockIn = new StockIn
        {
            StockInNo = stockInNo,
            StockInDate = AsOf.AddDays(-5),
            PurchaseOrderId = purchaseOrderId,
            SupplierId = SupplierA,
            WarehouseId = 1,
            Status = status,
        };
        db.StockIns.Add(stockIn);
        db.SaveChanges();
        foreach (var (productId, quantity) in lines)
        {
            db.StockInDetails.Add(new StockInDetail
            {
                StockInId = stockIn.Id,
                ProductId = productId,
                ProductName = $"商品{productId}",
                Unit = "PCS",
                Quantity = quantity,
            });
        }

        db.SaveChanges();
        stockIn.TotalQuantity = db.StockInDetails.Where(d => d.StockInId == stockIn.Id).Sum(d => d.Quantity);
        db.SaveChanges();
        return stockIn;
    }

    private static DynamicSupplierExposureReportController NewController(ErpDbContext db)
        => new(db);

    private static DynamicSupplierExposureReportPageDto PreviewOk(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<ApiResponse<DynamicSupplierExposureReportPageDto>>(ok.Value);
        Assert.NotNull(resp.Data);
        return resp.Data!;
    }


    // ==================== 1. 分组键规范化（fail closed） ====================

    [Theory]
    [InlineData(null, "none")]
    [InlineData("", "none")]
    [InlineData("   ", "none")]
    [InlineData("none", "none")]
    [InlineData("NONE", "none")]
    [InlineData("supplier", "supplier")]
    [InlineData("SUPPLIER", "supplier")]
    [InlineData("currency", "currency")]
    [InlineData("CURRENCY", "currency")]
    [InlineData("linkStatus", "linkStatus")]
    [InlineData("LINKSTATUS", "linkStatus")]
    [InlineData("receiptStatus", "receiptStatus")]
    [InlineData("RECEIPTSTATUS", "receiptStatus")]
    public void NormalizeGroupBy_接受白名单分组键_大小写不敏感(string? input, string expected)
        => Assert.Equal(expected, DynamicSupplierExposureReportRules.NormalizeGroupBy(input));

    [Fact]
    public void NormalizeGroupBy_未知分组键_拒绝()
    {
        var ex = Assert.Throws<BusinessException>(() =>
            DynamicSupplierExposureReportRules.NormalizeGroupBy("quarter"));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public async Task Preview_无效分组键_在源读取前拒绝()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Preview(
            new DynamicSupplierExposureReportRequest { GroupBy = "quarter" }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    // ==================== 2. 供应商分组（动态，只出现本页存在的取值） ====================

    [Fact]
    public async Task Preview_group_by_supplier_counts_orders_per_supplier_on_page()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedSupplier(db, SupplierA, "甲供应商");
        SeedSupplier(db, SupplierB, "乙供应商");
        SeedOrder(db, "PO-G-1", SupplierA, Currency.CNY, 100m, null, (ProductA, 1m));
        SeedOrder(db, "PO-G-2", SupplierA, Currency.USD, 200m, null, (ProductA, 1m));
        SeedOrder(db, "PO-G-3", SupplierB, Currency.CNY, 300m, null, (ProductA, 1m));
        await db.SaveChangesAsync();

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var page = PreviewOk(await ctl.Preview(new DynamicSupplierExposureReportRequest
        {
            GroupBy = "supplier",
            PageSize = 10,
        }));

        Assert.Equal("supplier", page.GroupBy);
        Assert.Equal(2, page.Groups!.Count);
        var byKey = page.Groups.ToDictionary(g => g.Key, StringComparer.Ordinal);
        Assert.Equal(2, byKey[$"supplier:{SupplierA}"].Count);
        Assert.Equal("甲供应商", byKey[$"supplier:{SupplierA}"].Label);
        Assert.Equal(1, byKey[$"supplier:{SupplierB}"].Count);
        Assert.Equal("乙供应商", byKey[$"supplier:{SupplierB}"].Label);
    }

    // ==================== 3. 币种分组（不同币种分别成组，绝不合并） ====================

    [Fact]
    public async Task Preview_group_by_currency_keeps_distinct_currencies_separate()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedSupplier(db, SupplierA, "甲供应商");
        SeedOrder(db, "PO-C-1", SupplierA, Currency.CNY, 100m, null, (ProductA, 1m));
        SeedOrder(db, "PO-C-2", SupplierA, Currency.CNY, 200m, null, (ProductA, 1m));
        SeedOrder(db, "PO-C-3", SupplierA, Currency.USD, 300m, null, (ProductA, 1m));
        await db.SaveChangesAsync();

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var page = PreviewOk(await ctl.Preview(new DynamicSupplierExposureReportRequest
        {
            GroupBy = "currency",
            PageSize = 10,
        }));

        Assert.Equal("currency", page.GroupBy);
        Assert.Equal(2, page.Groups!.Count);
        var byKey = page.Groups.ToDictionary(g => g.Key, StringComparer.Ordinal);
        Assert.Equal(2, byKey["currency:CNY"].Count);
        Assert.Equal(1, byKey["currency:USD"].Count);
    }


    // ==================== 4. 链接状态分组（固定三类，ambiguous / unavailable 保持可见） ====================

    [Fact]
    public async Task Preview_group_by_linkStatus_keeps_all_link_classes_visible()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedSupplier(db, SupplierA, "甲供应商");
        var linkedSo = SeedSalesOrder(db, "SO-LINK");
        var ambiguousSo = SeedSalesOrder(db, "SO-AMB");
        SeedOrder(db, "PO-L-1", SupplierA, Currency.CNY, 10m, linkedSo.Id, (ProductA, 1m));    // linked（归属销售订单唯一）
        SeedOrder(db, "PO-A-1", SupplierA, Currency.CNY, 20m, ambiguousSo.Id, (ProductA, 1m)); // ambiguous（同销售订单两张）
        SeedOrder(db, "PO-A-2", SupplierA, Currency.CNY, 30m, ambiguousSo.Id, (ProductA, 1m)); // ambiguous
        SeedOrder(db, "PO-U-1", SupplierA, Currency.CNY, 40m, null, (ProductA, 1m));           // unavailable（无归属销售订单）
        await db.SaveChangesAsync();

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var page = PreviewOk(await ctl.Preview(new DynamicSupplierExposureReportRequest
        {
            GroupBy = "linkStatus",
            PageSize = 10,
        }));

        Assert.Equal("linkStatus", page.GroupBy);
        Assert.Equal(3, page.Groups!.Count);
        var byKey = page.Groups.ToDictionary(g => g.Key, StringComparer.Ordinal);
        Assert.Equal(1, byKey["linkStatus:linked"].Count);
        Assert.Equal("链接可用", byKey["linkStatus:linked"].Label);
        Assert.Equal(2, byKey["linkStatus:ambiguous"].Count);
        Assert.Contains("链接不唯一", byKey["linkStatus:ambiguous"].Label);
        Assert.Equal(1, byKey["linkStatus:unavailable"].Count);
        Assert.Contains("无可用链接", byKey["linkStatus:unavailable"].Label);
    }

    [Fact]
    public async Task Preview_group_by_linkStatus_keeps_ambiguous_and_unavailable_visible_when_zero()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedSupplier(db, SupplierA, "甲供应商");
        var linkedSo = SeedSalesOrder(db, "SO-ONLY");
        SeedOrder(db, "PO-LONLY-1", SupplierA, Currency.CNY, 10m, linkedSo.Id, (ProductA, 1m));
        await db.SaveChangesAsync();

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var page = PreviewOk(await ctl.Preview(new DynamicSupplierExposureReportRequest
        {
            GroupBy = "linkStatus",
            PageSize = 10,
        }));

        Assert.Equal(3, page.Groups!.Count);
        var byKey = page.Groups.ToDictionary(g => g.Key, StringComparer.Ordinal);
        Assert.Equal(1, byKey["linkStatus:linked"].Count);
        Assert.Equal(0, byKey["linkStatus:ambiguous"].Count);
        Assert.Equal(0, byKey["linkStatus:unavailable"].Count);
    }


    // ==================== 5. 收货状态分组（固定五类，unknown 保持可见） ====================

    [Fact]
    public async Task Preview_group_by_receiptStatus_keeps_all_receipt_classes_and_unknown_visible()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedSupplier(db, SupplierA, "甲供应商");

        var partialOrder = SeedOrder(db, "PO-R-PART", SupplierA, Currency.CNY, 20m, null, (ProductA, 10m));
        var completeOrder = SeedOrder(db, "PO-R-COMP", SupplierA, Currency.CNY, 30m, null, (ProductA, 10m));
        var overOrder = SeedOrder(db, "PO-R-OVER", SupplierA, Currency.CNY, 40m, null, (ProductA, 10m));
        SeedOrder(db, "PO-R-NONE", SupplierA, Currency.CNY, 10m, null, (ProductA, 10m)); // 无入库 → none

        SeedStockIn(db, "RK-PART", partialOrder.Id, DocumentStatus.Approved, (ProductA, 4m));   // 4 / 10 → partial
        SeedStockIn(db, "RK-COMP", completeOrder.Id, DocumentStatus.Approved, (ProductA, 10m)); // 10 / 10 → complete
        SeedStockIn(db, "RK-OVER", overOrder.Id, DocumentStatus.Approved, (ProductA, 12m));     // 12 / 10 → over_received
        await db.SaveChangesAsync();

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var page = PreviewOk(await ctl.Preview(new DynamicSupplierExposureReportRequest
        {
            GroupBy = "receiptStatus",
            PageSize = 10,
        }));

        Assert.Equal("receiptStatus", page.GroupBy);
        Assert.Equal(5, page.Groups!.Count);
        var byKey = page.Groups.ToDictionary(g => g.Key, StringComparer.Ordinal);
        Assert.Equal(1, byKey["receiptStatus:none"].Count);
        Assert.Equal(1, byKey["receiptStatus:partial"].Count);
        Assert.Equal(1, byKey["receiptStatus:complete"].Count);
        Assert.Equal(1, byKey["receiptStatus:over_received"].Count);
        Assert.Equal(0, byKey["receiptStatus:unknown"].Count); // 未知收货类别始终保留（本页无命中上限）
        Assert.Contains("未知", byKey["receiptStatus:unknown"].Label);
    }


    // ==================== 6. 分页：只统计当前授权预览页 ====================

    [Fact]
    public async Task Preview_grouping_counts_only_current_page_not_all_matches()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedSupplier(db, SupplierA, "甲供应商");
        SeedSupplier(db, SupplierB, "乙供应商");
        SeedOrder(db, "PO-P-1", SupplierA, Currency.CNY, 100m, null, (ProductA, 1m));
        SeedOrder(db, "PO-P-2", SupplierB, Currency.CNY, 200m, null, (ProductA, 1m));
        SeedOrder(db, "PO-P-3", SupplierB, Currency.CNY, 300m, null, (ProductA, 1m));
        await db.SaveChangesAsync();

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var page1 = PreviewOk(await ctl.Preview(new DynamicSupplierExposureReportRequest
        {
            GroupBy = "supplier",
            Page = 1,
            PageSize = 2,
        }));

        Assert.Equal(3, page1.Total);
        Assert.Equal(2, page1.Rows.Count);
        var byKey = page1.Groups!.ToDictionary(g => g.Key, StringComparer.Ordinal);
        // SupplierB 共有 2 张订单，但第 1 页只包含其中 1 张 → 分组计数只统计本页
        Assert.Equal(1, byKey[$"supplier:{SupplierA}"].Count);
        Assert.Equal(1, byKey[$"supplier:{SupplierB}"].Count);
    }

    // ==================== 7. 空页：动态为空、固定分类保留零计数 ====================

    [Fact]
    public async Task Preview_grouping_empty_page_returns_empty_for_dynamic_and_zero_for_fixed()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var none = PreviewOk(await ctl.Preview(new DynamicSupplierExposureReportRequest { GroupBy = null }));
        Assert.Equal("none", none.GroupBy);
        Assert.Empty(none.Groups!);

        var supplier = PreviewOk(await ctl.Preview(new DynamicSupplierExposureReportRequest { GroupBy = "supplier" }));
        Assert.Empty(supplier.Groups!);

        var currency = PreviewOk(await ctl.Preview(new DynamicSupplierExposureReportRequest { GroupBy = "currency" }));
        Assert.Empty(currency.Groups!);

        var link = PreviewOk(await ctl.Preview(new DynamicSupplierExposureReportRequest { GroupBy = "linkStatus" }));
        Assert.Equal(3, link.Groups!.Count);
        Assert.All(link.Groups, g => Assert.Equal(0, g.Count));

        var receipt = PreviewOk(await ctl.Preview(new DynamicSupplierExposureReportRequest { GroupBy = "receiptStatus" }));
        Assert.Equal(5, receipt.Groups!.Count);
        Assert.All(receipt.Groups, g => Assert.Equal(0, g.Count));
    }

    // ==================== 8. 权限（fail closed） ====================

    [Fact]
    public async Task Preview_grouping_no_menu_authorization_denied()
    {
        using var db = TestDbFactory.Create();
        var user = SeedUser(db, "nomenu");
        var role = SeedRole(db, "Role-nomenu");
        SeedUserRole(db, user.Id, role.Id);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Preview(
            new DynamicSupplierExposureReportRequest { GroupBy = "supplier" }));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    [Fact]
    public async Task Preview_grouping_no_identity_denied()
    {
        using var db = TestDbFactory.Create();
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, null);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Preview(
            new DynamicSupplierExposureReportRequest { GroupBy = "supplier" }));
        Assert.Equal(ErrorCodes.Unauthorized, ex.Code);
    }


    // ==================== 9. 只读不写库 ====================

    [Fact]
    public async Task Preview_grouping_is_read_only_and_writes_nothing()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedSupplier(db, SupplierA, "甲供应商");
        SeedOrder(db, "PO-RW-1", SupplierA, Currency.CNY, 100m, null, (ProductA, 1m));
        await db.SaveChangesAsync();

        var counting = CountingDbContext.Wrap(db);
        var ctl = new DynamicSupplierExposureReportController(counting.Proxy);
        TestAuth.SetUser(ctl, uid);

        var page = PreviewOk(await ctl.Preview(new DynamicSupplierExposureReportRequest
        {
            GroupBy = "supplier",
            PageSize = 10,
        }));

        Assert.Equal(1, page.Total);
        Assert.NotEmpty(page.Groups!);
        Assert.Equal(0, counting.WriteCalls);
    }

    // ==================== 10. 分组只统计张数，绝不求和金额 / 数量 ====================

    [Fact]
    public void GroupDto_exposes_only_key_label_count_never_sums_quantities_or_currencies()
    {
        var names = typeof(DynamicSupplierExposureReportGroupDto).GetProperties().Select(p => p.Name).ToList();
        Assert.Contains("Key", names);
        Assert.Contains("Label", names);
        Assert.Contains("Count", names);
        Assert.DoesNotContain(names, n => n.Contains("Amount", StringComparison.Ordinal));
        Assert.DoesNotContain(names, n => n.Contains("Quantity", StringComparison.Ordinal));
        Assert.DoesNotContain(names, n => n.Contains("Total", StringComparison.Ordinal));
    }

    // ==================== 11. 只读计数上下文（断言不写库） ====================

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

