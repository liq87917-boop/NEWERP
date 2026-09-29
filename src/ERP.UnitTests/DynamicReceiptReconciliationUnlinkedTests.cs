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
/// ERP-165 动态客户订单与收款核对报表（ERP-164 追加）——独立的未关联收款证据字段目录与投影单元测试。
/// 覆盖：独立有限收款字段目录、未知收款列在读取 ERP-046 源数据之前拒绝、收款字段顺序与去重、
/// 受限制业务员范围（仅本页被分配客户）与被拒绝客户不泄露、active / pending / historical 证据状态筛选、
/// 未关联收款不并入订单金额、币种隔离、截断标记、空页、以及只读不写库。
/// <para>全部使用内存数据库（TestDbFactory），不连接 SQL Server、不启动 API、不执行任何 SQL / seed。</para>
/// </summary>
public class DynamicReceiptReconciliationUnlinkedTests
{
    private static readonly DateTime AsOf = new(2026, 9, 25);
    private const long CustomerA = 966001L;
    private const long CustomerB = 966002L;

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

    /// <summary>播种一个「系统内置角色 + 销售订单菜单授权」用户（特权账号，仍通过菜单授权）</summary>
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


    // ==================== 1. 独立有限收款字段目录 ====================

    [Fact]
    public async Task Catalog_exposes_separate_finite_receipt_field_catalog()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var catalog = CatalogOk(await ctl.Catalog());

        Assert.NotEmpty(catalog.ReceiptFields);
        var receiptKeys = catalog.ReceiptFields.Select(f => f.Key).ToArray();

        // 独立于订单字段目录：订单侧合计字段绝不混入收款证据字段目录
        Assert.DoesNotContain("orderNo", receiptKeys);
        Assert.DoesNotContain("orderAmount", receiptKeys);
        Assert.DoesNotContain("linkedReceiptAmount", receiptKeys);

        // 有限目录包含关键收款证据字段
        var set = receiptKeys.ToHashSet(StringComparer.Ordinal);
        Assert.Contains("receiptNo", set);
        Assert.Contains("receiptDate", set);
        Assert.Contains("customerId", set);
        Assert.Contains("currency", set);
        Assert.Contains("amount", set);
        Assert.Contains("evidenceStatus", set);
        Assert.Contains("receiptLinkageStatus", set);
        Assert.Contains("referenceField", set);
        Assert.Contains("note", set);

        // 目录顺序与规则一致（有限、有序）
        Assert.Equal(
            DynamicReceiptReconciliationReportRules.GetReceiptCatalog().Select(f => f.Key).ToArray(),
            receiptKeys);
    }

    [Fact]
    public void NormalizeReceiptFields_rejects_unknown_receipt_column()
    {
        // 订单侧字段不可作为收款证据字段：两个目录相互独立，绝不把订单合计当成收款证据
        Assert.Throws<BusinessException>(() =>
            DynamicReceiptReconciliationReportRules.NormalizeReceiptFields(new[] { "receiptNo", "linkedReceiptAmount" }));
        Assert.Throws<BusinessException>(() =>
            DynamicReceiptReconciliationReportRules.NormalizeReceiptFields(new[] { "not-a-receipt-field" }));
    }

    [Fact]
    public void NormalizeReceiptFields_dedupes_and_keeps_request_order()
    {
        var keys = DynamicReceiptReconciliationReportRules.NormalizeReceiptFields(
            new[] { "amount", "receiptNo", "amount", "currency" });
        Assert.Equal(new[] { "amount", "receiptNo", "currency" }, keys);
    }

    [Fact]
    public void NormalizeReceiptFields_returns_all_when_empty()
    {
        var keys = DynamicReceiptReconciliationReportRules.NormalizeReceiptFields(null);
        Assert.Equal(
            DynamicReceiptReconciliationReportRules.GetReceiptCatalog().Select(f => f.Key).ToArray(),
            keys);
    }


    // ==================== 2. 未知收款列在读取源数据之前拒绝（fail closed） ====================

    [Fact]
    public async Task Preview_rejects_unknown_receipt_column()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => ctl.Preview(new DynamicReceiptReconciliationReportRequest
            {
                ReceiptFields = new() { "receiptNo", "not-a-receipt-field" },
            }));
    }


    // ==================== 3. 收款字段顺序 ====================

    [Fact]
    public async Task Preview_returns_receipt_fields_in_requested_order()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "甲客户");
        SeedOrder(db, "SO-1", CustomerA, Currency.USD, 100m);
        SeedReceipt(db, "SK-1", CustomerA, 30m, Currency.USD, DocumentStatus.Approved);
        await db.SaveChangesAsync();

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var requested = new[] { "currency", "receiptNo", "amount", "evidenceStatus" };
        var page = PreviewOk(await ctl.Preview(new DynamicReceiptReconciliationReportRequest
        {
            ReceiptFields = requested.ToList(),
        }));

        Assert.Equal(requested, page.ReceiptColumns.Select(c => c.Key).ToArray());
        var receiptRow = Assert.Single(page.ReceiptRows);
        Assert.Equal(requested, receiptRow.Keys.ToArray());
        Assert.Equal("USD", receiptRow["currency"]);
        Assert.Equal("SK-1", receiptRow["receiptNo"]);
        Assert.Equal(30m, receiptRow["amount"]);
        Assert.Equal("active", receiptRow["evidenceStatus"]);
    }


    // ==================== 4. 受限制业务员范围：仅本页被分配客户，被拒绝客户不泄露 ====================

    [Fact]
    public async Task Preview_scopes_unlinked_receipts_to_assigned_customers_only()
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

        var page = PreviewOk(await ctl.Preview(new DynamicReceiptReconciliationReportRequest
        {
            ReceiptFields = new() { "receiptNo", "customerId" },
        }));

        // 只看到自己被分配客户的收款单；被拒绝客户（CustomerB）的收款单绝不出现
        var receiptRow = Assert.Single(page.ReceiptRows);
        Assert.Equal("SK-MINE", receiptRow["receiptNo"]);
        Assert.Equal(mine.Id, (long)receiptRow["customerId"]!);
        Assert.DoesNotContain(page.ReceiptRows, r => (string)r["receiptNo"]! == "SK-OTHER");
    }



    // ==================== 5. active / pending / historical 证据状态筛选 ====================

    [Fact]
    public async Task Preview_applies_active_pending_historical_filters_to_unlinked_receipts()
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

        // 默认只看有效（已审核）证据
        var byDefault = PreviewOk(await ctl.Preview(new DynamicReceiptReconciliationReportRequest
        {
            ReceiptFields = new() { "receiptNo", "evidenceStatus" },
        }));
        var defaultReceipt = Assert.Single(byDefault.ReceiptRows);
        Assert.Equal("SK-ACT", defaultReceipt["receiptNo"]);
        Assert.Equal("active", defaultReceipt["evidenceStatus"]);

        // pending：仅未审核证据
        var pending = PreviewOk(await ctl.Preview(new DynamicReceiptReconciliationReportRequest
        {
            ReceiptFields = new() { "receiptNo", "evidenceStatus" },
            ReceiptStatus = "pending",
        }));
        var pendingReceipt = Assert.Single(pending.ReceiptRows);
        Assert.Equal("SK-PEND", pendingReceipt["receiptNo"]);
        Assert.Equal("pending", pendingReceipt["evidenceStatus"]);

        // historical：仅历史证据
        var historical = PreviewOk(await ctl.Preview(new DynamicReceiptReconciliationReportRequest
        {
            ReceiptFields = new() { "receiptNo", "evidenceStatus" },
            ReceiptStatus = "historical",
        }));
        var historicalReceipt = Assert.Single(historical.ReceiptRows);
        Assert.Equal("SK-HIST", historicalReceipt["receiptNo"]);
        Assert.Equal("historical", historicalReceipt["evidenceStatus"]);

        // all：三类各自单列，绝不合并
        var all = PreviewOk(await ctl.Preview(new DynamicReceiptReconciliationReportRequest
        {
            ReceiptFields = new() { "receiptNo" },
            ReceiptStatus = "all",
        }));
        Assert.Equal(3, all.ReceiptRows.Count);
    }


    // ==================== 6. 未关联收款证据独立：绝不并入订单金额、绝不匹配订单 ====================

    [Fact]
    public async Task Preview_keeps_unlinked_receipts_separate_from_order_amounts()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "甲客户");
        SeedOrder(db, "SO-1", CustomerA, Currency.USD, 100m);
        SeedReceipt(db, "SK-1", CustomerA, 30m, Currency.USD, DocumentStatus.Approved);
        await db.SaveChangesAsync();

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var page = PreviewOk(await ctl.Preview(new DynamicReceiptReconciliationReportRequest
        {
            Fields = new() { "orderNo", "orderAmount" },
            ReceiptFields = new() { "receiptNo", "amount" },
        }));

        // 订单金额不因收款单而改变
        var orderRow = Assert.Single(page.Rows);
        Assert.Equal(100m, orderRow["orderAmount"]);

        // 收款金额独立列出，绝不并入订单金额，也绝不匹配到任何订单
        var receiptRow = Assert.Single(page.ReceiptRows);
        Assert.Equal("SK-1", receiptRow["receiptNo"]);
        Assert.Equal(30m, receiptRow["amount"]);
    }


    // ==================== 7. 币种隔离 ====================

    [Fact]
    public async Task Preview_keeps_receipt_currencies_isolated()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "甲客户");
        SeedOrder(db, "SO-1", CustomerA, Currency.USD, 100m);
        SeedReceipt(db, "SK-USD", CustomerA, 30m, Currency.USD, DocumentStatus.Approved);
        SeedReceipt(db, "SK-CNY", CustomerA, 60m, Currency.CNY, DocumentStatus.Approved);
        await db.SaveChangesAsync();

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var page = PreviewOk(await ctl.Preview(new DynamicReceiptReconciliationReportRequest
        {
            ReceiptFields = new() { "receiptNo", "currency", "amount" },
        }));

        Assert.Equal(2, page.ReceiptRows.Count);
        var usd = page.ReceiptRows.Single(r => (string)r["receiptNo"]! == "SK-USD");
        var cny = page.ReceiptRows.Single(r => (string)r["receiptNo"]! == "SK-CNY");
        Assert.Equal("USD", usd["currency"]);
        Assert.Equal(30m, usd["amount"]);
        Assert.Equal("CNY", cny["currency"]);
        Assert.Equal(60m, cny["amount"]);
    }



    // ==================== 8. 截断标记（命中上限时显式标注，绝不静默截断） ====================

    [Fact]
    public async Task Preview_marks_unlinked_receipt_truncation_when_limit_hit()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "甲客户");
        SeedOrder(db, "SO-1", CustomerA, Currency.USD, 100m);

        for (var i = 0; i < SalesOrderReceiptReconciliation.UnlinkedReceiptLimit; i++)
        {
            db.FinanceReceipts.Add(new FinanceReceipt
            {
                ReceiptNo = $"SK-CAP-{i}",
                ReceiptDate = AsOf.AddDays(-1),
                CustomerId = CustomerA,
                Amount = 1m,
                Currency = Currency.USD,
                Status = DocumentStatus.Approved,
            });
        }
        await db.SaveChangesAsync();

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var page = PreviewOk(await ctl.Preview(new DynamicReceiptReconciliationReportRequest
        {
            ReceiptFields = new() { "receiptNo" },
        }));

        Assert.True(page.UnlinkedReceiptTruncated);
        Assert.Equal(SalesOrderReceiptReconciliation.UnlinkedReceiptLimit, page.ReceiptRows.Count);
    }


    // ==================== 9. 空页 ====================

    [Fact]
    public async Task Preview_returns_empty_receipts_for_empty_page()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var requested = new[] { "receiptNo", "amount" };
        var page = PreviewOk(await ctl.Preview(new DynamicReceiptReconciliationReportRequest
        {
            ReceiptFields = requested.ToList(),
        }));

        Assert.Empty(page.Rows);
        Assert.Empty(page.ReceiptRows);
        Assert.Empty(page.UnlinkedReceipts);
        Assert.False(page.UnlinkedReceiptTruncated);
        Assert.Equal(requested, page.ReceiptColumns.Select(c => c.Key).ToArray());
    }


    // ==================== 10. 只读不写库 ====================

    [Fact]
    public async Task Preview_unlinked_projection_does_not_write()
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
        await ctl.Preview(new DynamicReceiptReconciliationReportRequest
        {
            ReceiptFields = new() { "receiptNo", "amount", "currency", "evidenceStatus" },
        });

        Assert.Equal(receiptsBefore, db.FinanceReceipts.Count());
        Assert.Equal(ordersBefore, db.SalesOrders.Count());
        Assert.DoesNotContain(db.ChangeTracker.Entries(), e => e.State != EntityState.Unchanged);
    }
}

