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
/// ERP-156 动态销售订单出货 / 财务进度报表（只读、有界）单元测试。
/// 覆盖：字段白名单目录、选定列与顺序、客户 / 币种 / 订单日期 / 出货状态 / 收款链接状态筛选、
/// 授权（无身份 / 无销售订单菜单 / 受限制业务员范围）、未知金额证据保留（null）、币种隔离、
/// 稳定分页与 200 上限、无效字段 / 无效筛选 / 页大小超限、以及只读不写库。
/// <para>全部使用内存数据库（TestDbFactory），不连接 SQL Server、不启动 API、不执行任何 SQL / seed。</para>
/// </summary>
public class DynamicShipmentFinanceReportTests
{
    private static readonly DateTime AsOf = new(2026, 9, 24);
    private const long CustomerA = 955001L;
    private const long CustomerB = 955002L;
    private const long ProductA = 955101L;

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

    private static void SeedStockOut(ErpDbContext db, string stockOutNo, long? salesOrderId,
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

    private static void SeedDepositApply(ErpDbContext db, string applyNo, long salesOrderId, decimal amount,
        Currency currency, DocumentStatus status)
    {
        db.FinanceDepositApplies.Add(new FinanceDepositApply
        {
            ApplyNo = applyNo,
            ApplyDate = AsOf.AddDays(-3),
            SalesOrderId = salesOrderId,
            CustomerId = CustomerA,
            Amount = amount,
            Currency = currency,
            Status = status,
        });
        db.SaveChanges();
    }

    private static DynamicShipmentFinanceReportController NewController(ErpDbContext db) => new(db);

    private static DynamicShipmentFinanceReportCatalogDto CatalogOk(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<ApiResponse<DynamicShipmentFinanceReportCatalogDto>>(ok.Value);
        Assert.NotNull(resp.Data);
        return resp.Data!;
    }

    private static DynamicShipmentFinanceReportPageDto PreviewOk(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<ApiResponse<DynamicShipmentFinanceReportPageDto>>(ok.Value);
        Assert.NotNull(resp.Data);
        return resp.Data!;
    }

    // ==================== 1. 字段白名单目录 ====================

    [Fact]
    public async Task Catalog_returns_finite_ERP032_allowlist_for_authorized_user()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var catalog = CatalogOk(await ctl.Catalog());

        Assert.Equal(DynamicShipmentFinanceReportRules.RequiredMenuCode, catalog.RequiredMenuCode);
        Assert.Equal(DynamicShipmentFinanceReportRules.RequiredMenuText, catalog.RequiredMenuText);
        Assert.Equal(DynamicShipmentFinanceReportRules.MaxPageSize, catalog.MaxPageSize);
        Assert.NotEmpty(catalog.Fields);

        var keys = catalog.Fields.Select(f => f.Key).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("orderNo", keys);
        Assert.Contains("customerId", keys);
        Assert.Contains("currency", keys);
        Assert.Contains("shipmentStatus", keys);
        Assert.Contains("financeLinkStatus", keys);
        Assert.Contains("linkedAmount", keys);
        Assert.Contains("shippedQuantity", keys);
        Assert.DoesNotContain("receivableBalance", keys);
        Assert.DoesNotContain("aging", keys);
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
            ctl.Preview(new DynamicShipmentFinanceReportRequest()));
        Assert.Equal(ErrorCodes.Forbidden, noMenuPreview.Code);
    }

    // ==================== 3. 字段 / 筛选 / 页大小校验（源读取之前拒绝） ====================

    [Fact]
    public async Task Preview_rejects_unknown_fields_invalid_filters_and_oversized_page()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var unknownField = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Preview(new DynamicShipmentFinanceReportRequest { Fields = new() { "unknownField" } }));
        Assert.Equal(ErrorCodes.InvalidParameter, unknownField.Code);

        var invalidCurrency = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Preview(new DynamicShipmentFinanceReportRequest { Currency = "XYZ" }));
        Assert.Equal(ErrorCodes.InvalidParameter, invalidCurrency.Code);

        var invalidShipment = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Preview(new DynamicShipmentFinanceReportRequest { ShipmentStatus = "bogus" }));
        Assert.Equal(ErrorCodes.InvalidParameter, invalidShipment.Code);

        var invalidFinance = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Preview(new DynamicShipmentFinanceReportRequest { FinanceLinkStatus = "bogus" }));
        Assert.Equal(ErrorCodes.InvalidParameter, invalidFinance.Code);

        var invalidCustomer = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Preview(new DynamicShipmentFinanceReportRequest { CustomerId = 0 }));
        Assert.Equal(ErrorCodes.InvalidParameter, invalidCustomer.Code);

        var invalidDate = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Preview(new DynamicShipmentFinanceReportRequest
            {
                OrderDateFrom = AsOf.AddDays(5),
                OrderDateTo = AsOf,
            }));
        Assert.Equal(ErrorCodes.InvalidParameter, invalidDate.Code);

        var oversizedPage = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Preview(new DynamicShipmentFinanceReportRequest { PageSize = 201 }));
        Assert.Equal(ErrorCodes.InvalidParameter, oversizedPage.Code);
    }

    // ==================== 4. 选定列与请求顺序 ====================

    [Fact]
    public async Task Preview_projects_selected_columns_in_requested_order()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "甲客户");
        SeedOrder(db, "SO-EXP-1", CustomerA, Currency.CNY, 100m);
        await db.SaveChangesAsync();

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var page = PreviewOk(await ctl.Preview(new DynamicShipmentFinanceReportRequest
        {
            Fields = new() { "currency", "orderNo", "orderAmount" },
        }));

        Assert.Equal(new[] { "currency", "orderNo", "orderAmount" }, page.Columns.Select(c => c.Key));
        var row = Assert.Single(page.Rows);
        Assert.Equal(new[] { "currency", "orderNo", "orderAmount" }, row.Keys);
        Assert.Equal("CNY", row["currency"]);
        Assert.Equal("SO-EXP-1", row["orderNo"]);
        Assert.Equal(100m, row["orderAmount"]);
    }


    // ==================== 5. 无权威引用：未知金额照实保留（null，绝不回落为 0） ====================

    [Fact]
    public async Task Preview_keeps_unlinked_finance_amounts_unknown()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "甲客户");
        var order = SeedOrder(db, "SO-EXP-2", CustomerA, Currency.USD, 1000m);
        SeedDetail(db, order.Id, ProductA, 10m);
        await db.SaveChangesAsync();

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var page = PreviewOk(await ctl.Preview(new DynamicShipmentFinanceReportRequest
        {
            Fields = new() { "orderNo", "financeLinkStatus", "linkedAmount", "uncoveredAmount", "submittedAmount" },
        }));

        var row = Assert.Single(page.Rows);
        Assert.Equal(DynamicShipmentFinanceReportRules.FinanceStatusUnlinked, row["financeLinkStatus"]);
        Assert.Null(row["linkedAmount"]);
        Assert.Null(row["uncoveredAmount"]);
        Assert.Null(row["submittedAmount"]);
    }

    // ==================== 6. 币种隔离：原币分别成行，绝不换算 ====================

    [Fact]
    public async Task Preview_preserves_currency_isolation_without_conversion()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "甲客户");
        SeedCustomer(db, CustomerB, "乙客户");
        SeedOrder(db, "SO-EXP-3", CustomerA, Currency.USD, 200m);
        SeedOrder(db, "SO-EXP-4", CustomerB, Currency.CNY, 300m);
        await db.SaveChangesAsync();

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var page = PreviewOk(await ctl.Preview(new DynamicShipmentFinanceReportRequest
        {
            Fields = new() { "orderNo", "currency", "orderAmount" },
        }));

        Assert.Equal(2, page.Rows.Count);
        var byCurrency = page.Rows.ToDictionary(r => (string)r["currency"]!);
        Assert.Equal(200m, byCurrency["USD"]["orderAmount"]);
        Assert.Equal(300m, byCurrency["CNY"]["orderAmount"]);
    }

    // ==================== 7. 受限制业务员：范围过滤 + 分页计数 ====================

    [Fact]
    public async Task Preview_scopes_restricted_salesperson_to_assigned_customers_with_paging()
    {
        using var db = TestDbFactory.Create();
        var user = SeedUser(db, "alice");
        var role = SeedRole(db, "Sales");
        SeedUserRole(db, user.Id, role.Id);
        SeedRoleMenu(db, role.Id, SeedMenu(db, DynamicShipmentFinanceReportRules.RequiredMenuCode).Id);
        var alice = SeedEmployee(db, "alice");
        var mine = SeedCustomer(db, CustomerA, "我的客户", alice.Id);
        SeedCustomer(db, CustomerB, "别人的客户", alice.Id + 1000);

        var first = SeedOrder(db, "SO-MINE-1", mine.Id, Currency.USD, 100m);
        SeedDetail(db, first.Id, ProductA, 10m);
        var second = SeedOrder(db, "SO-MINE-2", mine.Id, Currency.USD, 200m);
        SeedDetail(db, second.Id, ProductA, 20m);
        SeedOrder(db, "SO-OTHER", CustomerB, Currency.USD, 300m);
        await db.SaveChangesAsync();

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var page = PreviewOk(await ctl.Preview(new DynamicShipmentFinanceReportRequest
        {
            Fields = new() { "orderNo", "customerId" },
            PageSize = 2,
        }));

        Assert.Equal(2, page.Total);
        Assert.Equal(2, page.Rows.Count);
        Assert.All(page.Rows, r => Assert.Equal(mine.Id, (long)r["customerId"]!));
        Assert.DoesNotContain(page.Rows, r => (string)r["orderNo"]! == "SO-OTHER");

        var firstPage = PreviewOk(await ctl.Preview(new DynamicShipmentFinanceReportRequest
        {
            Fields = new() { "orderNo" },
            PageSize = 1,
            Page = 1,
        }));
        Assert.Equal(2, firstPage.Total);
        Assert.Single(firstPage.Rows);
        Assert.Equal(2, firstPage.TotalPages);
    }

    // ==================== 8. 只读不写库 ====================

    [Fact]
    public async Task Preview_does_not_write_to_database()
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
        await ctl.Preview(new DynamicShipmentFinanceReportRequest());

        Assert.Equal(salesOrdersBefore, db.SalesOrders.Count());
        Assert.Equal(detailsBefore, db.SalesOrderDetails.Count());
        Assert.Equal(stockOutsBefore, db.StockOuts.Count());
        Assert.DoesNotContain(db.ChangeTracker.Entries(), e => e.State != EntityState.Unchanged);
    }
}

