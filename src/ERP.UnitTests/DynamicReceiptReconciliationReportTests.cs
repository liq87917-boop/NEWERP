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
/// ERP-164 动态客户订单与收款核对报表（只读、有界）单元测试。
/// 覆盖：字段白名单目录、选定列与顺序、客户 / 币种 / 订单日期 / 出货状态 / 收款链接状态 / 收款证据状态 / 订单状态 / 关键字 / 页大小校验、
/// 授权（无身份 / 无销售订单菜单 / 受限制业务员范围）、未知金额证据保留（null）、四类证据相互独立、币种隔离、稳定分页与 200 上限、以及只读不写库。
/// <para>全部使用内存数据库（TestDbFactory），不连接 SQL Server、不启动 API、不执行任何 SQL / seed。</para>
/// </summary>
public class DynamicReceiptReconciliationReportTests
{
    private static readonly DateTime AsOf = new(2026, 9, 25);
    private const long CustomerA = 966001L;
    private const long CustomerB = 966002L;
    private const long ProductA = 966101L;

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

    /// <summary>播种一个「系统内置角色 + 销售订单菜单授权」用户（特权账号）</summary>
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

    private static void SeedDepositApply(ErpDbContext db, string applyNo, long salesOrderId, decimal amount,
        Currency currency, DocumentStatus status, long customerId = CustomerA)
    {
        db.FinanceDepositApplies.Add(new FinanceDepositApply
        {
            ApplyNo = applyNo,
            ApplyDate = AsOf.AddDays(-3),
            SalesOrderId = salesOrderId,
            CustomerId = customerId,
            Amount = amount,
            Currency = currency,
            Status = status,
        });
        db.SaveChanges();
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

    private static DynamicReceiptReconciliationReportCatalogDto CatalogOk(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<ApiResponse<DynamicReceiptReconciliationReportCatalogDto>>(ok.Value);
        Assert.NotNull(resp.Data);
        return resp.Data!;
    }

    private static DynamicReceiptReconciliationReportPageDto PreviewOk(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<ApiResponse<DynamicReceiptReconciliationReportPageDto>>(ok.Value);
        Assert.NotNull(resp.Data);
        return resp.Data!;
    }

    /// <summary>断言业务异常的错误码（避免只断言消息文案）</summary>
    private static async Task<BusinessException> AssertBusinessAsync(int expectedCode, Func<Task> action)
    {
        var ex = await Assert.ThrowsAsync<BusinessException>(action);
        Assert.Equal(expectedCode, ex.Code);
        return ex;
    }


    // ==================== 1. 字段白名单目录 ====================

    [Fact]
    public async Task Catalog_returns_finite_ERP046_allowlist_for_authorized_user()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var catalog = CatalogOk(await ctl.Catalog());

        Assert.Equal(DynamicReceiptReconciliationReportRules.RequiredMenuCode, catalog.RequiredMenuCode);
        Assert.Equal(DynamicReceiptReconciliationReportRules.RequiredMenuText, catalog.RequiredMenuText);
        Assert.Equal(DynamicReceiptReconciliationReportRules.MaxPageSize, catalog.MaxPageSize);
        Assert.NotEmpty(catalog.Fields);

        var keys = catalog.Fields.Select(f => f.Key).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("orderNo", keys);
        Assert.Contains("customerId", keys);
        Assert.Contains("currency", keys);
        Assert.Contains("shipmentStatus", keys);
        Assert.Contains("linkedReceiptAmount", keys);
        Assert.Contains("receiptCoverageStatus", keys);
        Assert.Contains("receiptAllocationStatus", keys);
        Assert.Contains("invoiceEvidenceStatus", keys);
        Assert.Contains("note", keys);
    }

    // ==================== 2. 授权（fail closed） ====================

    [Fact]
    public async Task Catalog_rejects_missing_identity()
    {
        using var db = TestDbFactory.Create();
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, null);
        await AssertBusinessAsync(ErrorCodes.Unauthorized, () => ctl.Catalog());
    }

    [Fact]
    public async Task Catalog_rejects_user_without_sales_order_menu()
    {
        using var db = TestDbFactory.Create();
        var user = SeedUser(db, "nomenu");
        var role = SeedRole(db, "NoSalesOrderMenu");
        SeedUserRole(db, user.Id, role.Id);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        await AssertBusinessAsync(ErrorCodes.Forbidden, () => ctl.Catalog());
    }

    [Fact]
    public async Task Preview_rejects_missing_identity()
    {
        using var db = TestDbFactory.Create();
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, null);
        await AssertBusinessAsync(ErrorCodes.Unauthorized, () => ctl.Preview(new DynamicReceiptReconciliationReportRequest()));
    }

    [Fact]
    public async Task Preview_rejects_user_without_sales_order_menu()
    {
        using var db = TestDbFactory.Create();
        var user = SeedUser(db, "nomenu2");
        var role = SeedRole(db, "NoSalesOrderMenu2");
        SeedUserRole(db, user.Id, role.Id);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        await AssertBusinessAsync(ErrorCodes.Forbidden,
            () => ctl.Preview(new DynamicReceiptReconciliationReportRequest()));
    }


    // ==================== 3. 字段 / 筛选 / 页大小校验（全部在读取源数据之前拒绝） ====================

    [Fact]
    public async Task Preview_rejects_unknown_field()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => ctl.Preview(new DynamicReceiptReconciliationReportRequest
            {
                Fields = new() { "orderNo", "not-a-field" },
            }));
    }

    [Fact]
    public async Task Preview_rejects_invalid_customer()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => ctl.Preview(new DynamicReceiptReconciliationReportRequest { CustomerId = 0 }));
    }

    [Fact]
    public async Task Preview_rejects_invalid_currency()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => ctl.Preview(new DynamicReceiptReconciliationReportRequest { Currency = "XXX" }));
    }

    [Fact]
    public async Task Preview_rejects_invalid_date_range()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => ctl.Preview(new DynamicReceiptReconciliationReportRequest
            {
                OrderDateFrom = AsOf.AddDays(1),
                OrderDateTo = AsOf,
            }));
    }

    [Fact]
    public async Task Preview_rejects_invalid_shipment_status()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => ctl.Preview(new DynamicReceiptReconciliationReportRequest { ShipmentStatus = "shipped-out" }));
    }

    [Fact]
    public async Task Preview_rejects_invalid_receipt_link_status()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => ctl.Preview(new DynamicReceiptReconciliationReportRequest { ReceiptLinkStatus = "unknown" }));
    }

    [Fact]
    public async Task Preview_rejects_invalid_receipt_status()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => ctl.Preview(new DynamicReceiptReconciliationReportRequest { ReceiptStatus = "settled" }));
    }

    [Fact]
    public async Task Preview_rejects_invalid_order_status()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => ctl.Preview(new DynamicReceiptReconciliationReportRequest { OrderStatus = "deleted" }));
    }

    [Fact]
    public async Task Preview_rejects_long_keyword()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => ctl.Preview(new DynamicReceiptReconciliationReportRequest { Keyword = new string('K', 51) }));
    }

    [Fact]
    public async Task Preview_rejects_page_size_above_200()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => ctl.Preview(new DynamicReceiptReconciliationReportRequest { PageSize = 201 }));
    }


    // ==================== 4. 受限制业务员：范围过滤在计数与分页之前，未关联收款只来自本页客户 ====================

    [Fact]
    public async Task Preview_scopes_restricted_salesperson_to_assigned_customers_with_paging()
    {
        using var db = TestDbFactory.Create();
        var user = SeedUser(db, "alice");
        var role = SeedRole(db, "Sales");
        SeedUserRole(db, user.Id, role.Id);
        SeedRoleMenu(db, role.Id, SeedMenu(db, DynamicReceiptReconciliationReportRules.RequiredMenuCode).Id);
        var alice = SeedEmployee(db, "alice");
        var mine = SeedCustomer(db, CustomerA, "我的客户", alice.Id);
        SeedCustomer(db, CustomerB, "别人的客户", alice.Id + 1000);

        SeedOrder(db, "SO-MINE-1", mine.Id, Currency.USD, 100m);
        SeedOrder(db, "SO-MINE-2", mine.Id, Currency.USD, 200m);
        SeedOrder(db, "SO-OTHER", CustomerB, Currency.USD, 300m);
        SeedReceipt(db, "SK-MINE", mine.Id, 40m, Currency.USD, DocumentStatus.Approved);
        SeedReceipt(db, "SK-OTHER", CustomerB, 50m, Currency.USD, DocumentStatus.Approved);
        await db.SaveChangesAsync();

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var page = PreviewOk(await ctl.Preview(new DynamicReceiptReconciliationReportRequest
        {
            Fields = new() { "orderNo", "customerId" },
            PageSize = 2,
        }));

        Assert.Equal(2, page.Total);
        Assert.Equal(2, page.Rows.Count);
        Assert.All(page.Rows, r => Assert.Equal(mine.Id, (long)r["customerId"]!));
        Assert.DoesNotContain(page.Rows, r => (string)r["orderNo"]! == "SO-OTHER");

        // 未关联收款证据只由本页范围内客户派生：CustomerB 的收款单绝不出现
        Assert.Single(page.UnlinkedReceipts);
        Assert.Equal("SK-MINE", page.UnlinkedReceipts[0].ReceiptNo);

        var firstPage = PreviewOk(await ctl.Preview(new DynamicReceiptReconciliationReportRequest
        {
            Fields = new() { "orderNo" },
            PageSize = 1,
            Page = 1,
        }));
        Assert.Equal(2, firstPage.Total);
        Assert.Single(firstPage.Rows);
        Assert.Equal(2, firstPage.TotalPages);
    }

    // ==================== 5. 选定字段与顺序 ====================

    [Fact]
    public async Task Preview_returns_selected_fields_in_requested_order()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "甲客户");
        SeedOrder(db, "SO-FIELD-1", CustomerA, Currency.USD, 1000m);
        await db.SaveChangesAsync();

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var requested = new[] { "customerName", "orderNo", "currency", "orderAmount" };
        var page = PreviewOk(await ctl.Preview(new DynamicReceiptReconciliationReportRequest
        {
            Fields = requested.ToList(),
        }));

        Assert.Equal(requested, page.Columns.Select(c => c.Key).ToArray());
        var row = Assert.Single(page.Rows);
        Assert.Equal(requested, row.Keys.ToArray());
        Assert.Equal("甲客户", row["customerName"]);
        Assert.Equal("SO-FIELD-1", row["orderNo"]);
        Assert.Equal("USD", row["currency"]);
        Assert.Equal(1000m, row["orderAmount"]);
    }


    // ==================== 6. 四类证据相互独立 + 未知为 null + 币种隔离 ====================

    [Fact]
    public async Task Preview_keeps_evidence_distinct_unknown_null_and_currencies_separate()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "甲客户");
        SeedCustomer(db, CustomerB, "乙客户");
        var linkedOrder = SeedOrder(db, "SO-E1", CustomerA, Currency.USD, 1000m);
        SeedDetail(db, linkedOrder.Id, ProductA, 10m);
        SeedDepositApply(db, "DJ-E1", linkedOrder.Id, 250m, Currency.USD, DocumentStatus.Approved, CustomerA);
        SeedOrder(db, "SO-E2", CustomerB, Currency.CNY, 500m);
        SeedReceipt(db, "SK-E1", CustomerA, 30m, Currency.USD, DocumentStatus.Approved);
        await db.SaveChangesAsync();

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var page = PreviewOk(await ctl.Preview(new DynamicReceiptReconciliationReportRequest
        {
            Fields = new() { "orderNo", "currency", "receiptCoverageStatus", "linkedReceiptAmount", "uncoveredAmount" },
        }));

        Assert.Equal(2, page.Rows.Count);
        var linked = page.Rows.Single(r => (string)r["orderNo"]! == "SO-E1");
        Assert.Equal("USD", linked["currency"]);
        Assert.Equal("linked", linked["receiptCoverageStatus"]);
        Assert.Equal(250m, linked["linkedReceiptAmount"]);
        Assert.Equal(750m, linked["uncoveredAmount"]);

        var unlinkedOrder = page.Rows.Single(r => (string)r["orderNo"]! == "SO-E2");
        Assert.Equal("CNY", unlinkedOrder["currency"]);
        Assert.Equal("unlinked", unlinkedOrder["receiptCoverageStatus"]);
        Assert.Null(unlinkedOrder["linkedReceiptAmount"]);
        Assert.Null(unlinkedOrder["uncoveredAmount"]);

        // 收款申请链接证据与客户级未关联收款证据相互独立：收款单绝不并入任何订单金额
        var receipt = Assert.Single(page.UnlinkedReceipts);
        Assert.Equal("SK-E1", receipt.ReceiptNo);
        Assert.Equal("USD", receipt.Currency);
        Assert.Equal(30m, receipt.Amount);
        Assert.Equal("unlinked", receipt.ReceiptLinkageStatus);
    }

    // ==================== 7. 状态筛选与历史证据 ====================

    [Fact]
    public async Task Preview_applies_order_and_receipt_status_filters_and_historical_evidence()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "甲客户");
        SeedOrder(db, "SO-ACT", CustomerA, Currency.USD, 100m);
        SeedOrder(db, "SO-CANC", CustomerA, Currency.USD, 200m, status: DocumentStatus.Cancelled);
        SeedReceipt(db, "SK-ACT", CustomerA, 10m, Currency.USD, DocumentStatus.Approved);
        SeedReceipt(db, "SK-HIST", CustomerA, 20m, Currency.USD, DocumentStatus.Cancelled);
        await db.SaveChangesAsync();

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        // 默认：排除已取消订单，收款证据只看有效（已审核）
        var byDefault = PreviewOk(await ctl.Preview(new DynamicReceiptReconciliationReportRequest
        {
            Fields = new() { "orderNo" },
        }));
        Assert.Equal(1, byDefault.Total);
        Assert.Equal("SO-ACT", Assert.Single(byDefault.Rows)["orderNo"]);
        Assert.Equal("SK-ACT", Assert.Single(byDefault.UnlinkedReceipts).ReceiptNo);

        // 订单状态：仅已取消 → 历史订单可见
        var cancelled = PreviewOk(await ctl.Preview(new DynamicReceiptReconciliationReportRequest
        {
            Fields = new() { "orderNo", "status" },
            OrderStatus = "cancelled",
        }));
        Assert.Equal(1, cancelled.Total);
        Assert.Equal("SO-CANC", Assert.Single(cancelled.Rows)["orderNo"]);

        // 收款证据：历史 → 仅历史收款单可见，金额绝不并入有效合计
        var historical = PreviewOk(await ctl.Preview(new DynamicReceiptReconciliationReportRequest
        {
            Fields = new() { "orderNo" },
            ReceiptStatus = "historical",
        }));
        var receipt = Assert.Single(historical.UnlinkedReceipts);
        Assert.Equal("SK-HIST", receipt.ReceiptNo);
        Assert.Equal("historical", receipt.EvidenceStatus);
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
        SeedReceipt(db, "SK-NW-1", CustomerA, 30m, Currency.USD, DocumentStatus.Approved);
        await db.SaveChangesAsync();

        var salesOrdersBefore = db.SalesOrders.Count();
        var detailsBefore = db.SalesOrderDetails.Count();
        var receiptsBefore = db.FinanceReceipts.Count();

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);
        await ctl.Preview(new DynamicReceiptReconciliationReportRequest());

        Assert.Equal(salesOrdersBefore, db.SalesOrders.Count());
        Assert.Equal(detailsBefore, db.SalesOrderDetails.Count());
        Assert.Equal(receiptsBefore, db.FinanceReceipts.Count());
        Assert.DoesNotContain(db.ChangeTracker.Entries(), e => e.State != EntityState.Unchanged);
    }
}

