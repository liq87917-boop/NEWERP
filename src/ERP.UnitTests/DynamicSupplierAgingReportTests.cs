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
/// ERP-140 动态供应商对账与账龄报表预览（只读、有界）单元测试。
/// 覆盖：字段白名单目录、选定列与顺序、供应商 / 币种 / 发票状态 / 分配状态 / 日期 / as-of 筛选、
/// 币种与未知到期日证据保留、稳定分页与 200 上限、无身份（未认证）、无采购订单菜单授权（权限不足）、
/// 无效字段 / 无效筛选 / 页大小超限、以及只读不写库。
/// <para>全部使用内存数据库（TestDbFactory），不连接 SQL Server、不启动 API、不执行任何 SQL / seed。</para>
/// </summary>
public class DynamicSupplierAgingReportTests
{
    private static readonly DateTime AsOf = new(2026, 9, 24);
    private const long SupplierA = 968001L;
    private const long SupplierB = 968002L;

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

    private static BaseSupplier SeedSupplier(ErpDbContext db, long id, string name, int status = 1, bool deleted = false)
    {
        var supplier = new BaseSupplier
        {
            Id = id,
            SupplierCode = $"S{id}",
            SupplierName = name,
            Status = status,
            IsDeleted = deleted
        };
        db.BaseSuppliers.Add(supplier);
        return supplier;
    }

    private static PurchaseInvoice SeedInvoice(
        ErpDbContext db, long id, long supplierId, string currency, string number,
        decimal net, decimal tax, decimal gross, int status, DateTime invoiceDate,
        DateTime? dueDate = null, string paymentTerms = "", bool deleted = false)
    {
        var supplier = db.BaseSuppliers.Local.FirstOrDefault(s => s.Id == supplierId);
        var invoice = new PurchaseInvoice
        {
            Id = id,
            InvoiceType = PurchaseInvoiceRules.InvoiceTypeOrdinary,
            InvoiceCode = string.Empty,
            InvoiceNumber = number,
            NormalizedInvoiceCode = string.Empty,
            NormalizedInvoiceNumber = PurchaseInvoiceRules.NormalizeIdentityPart(number),
            InvoiceDate = invoiceDate.Date,
            SupplierId = supplierId,
            SupplierCode = supplier?.SupplierCode ?? $"S{supplierId}",
            SupplierName = supplier?.SupplierName ?? $"供应商{supplierId}",
            Currency = currency,
            NetAmount = net,
            TaxAmount = tax,
            GrossAmount = gross,
            DueDate = dueDate?.Date,
            PaymentTerms = paymentTerms,
            Status = status,
            RecordedAt = status == PurchaseInvoiceRules.StatusDraft ? null : invoiceDate.Date.AddDays(1),
            VoidedAt = status == PurchaseInvoiceRules.StatusVoided ? invoiceDate.Date.AddDays(2) : null,
            VoidReason = status == PurchaseInvoiceRules.StatusVoided ? "作废测试" : string.Empty,
            IsDeleted = deleted
        };
        db.PurchaseInvoices.Add(invoice);
        return invoice;
    }

    private static DynamicSupplierAgingReportController NewController(IErpDbContext db)
        => new(db);

    private static DynamicSupplierAgingReportPageDto PreviewOk(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<ApiResponse<DynamicSupplierAgingReportPageDto>>(ok.Value);
        Assert.NotNull(resp.Data);
        return resp.Data!;
    }

    private static DynamicSupplierAgingReportCatalogDto CatalogOk(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<ApiResponse<DynamicSupplierAgingReportCatalogDto>>(ok.Value);
        Assert.NotNull(resp.Data);
        return resp.Data!;
    }

    // ==================== 1. 字段目录 ====================

    [Fact]
    public async Task Catalog_returns_finite_whitelist_fields()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var catalog = CatalogOk(await ctl.Catalog());

        Assert.Equal(DynamicSupplierAgingReportRules.RequiredMenuCode, catalog.RequiredMenuCode);
        Assert.Equal(200, catalog.MaxPageSize);
        Assert.Equal(DynamicSupplierAgingReportRules.AllFieldKeys.Count, catalog.Fields.Count);

        var keys = catalog.Fields.Select(f => f.Key).ToHashSet();
        Assert.Contains("currency", keys);
        Assert.Contains("grossAmount", keys);
        Assert.Contains("dueDateKnown", keys);
        Assert.Contains("agingBucket", keys);
        Assert.Contains("allocationState", keys);
        Assert.Contains("isDraft", keys);
        Assert.Contains("isVoided", keys);
        Assert.DoesNotContain("grandTotal", keys);      // 无跨币种总额字段
        Assert.DoesNotContain("payableBalance", keys);  // 无应付余额字段
        Assert.All(catalog.Fields, f => Assert.False(string.IsNullOrWhiteSpace(f.Key)));
    }

    // ==================== 2. 未授权（fail closed） ====================

    [Fact]
    public async Task Catalog_denied_without_menu()
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
    public async Task Catalog_denied_without_identity()
    {
        using var db = TestDbFactory.Create();
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, null);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Catalog());
        Assert.Equal(ErrorCodes.Unauthorized, ex.Code);
    }

    [Fact]
    public async Task Preview_denied_without_menu()
    {
        using var db = TestDbFactory.Create();
        var user = SeedUser(db, "nomenu");
        var role = SeedRole(db, "Role-nomenu", isSystem: false);
        SeedUserRole(db, user.Id, role.Id);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Preview(
            new DynamicSupplierAgingReportRequest()));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    [Fact]
    public async Task Preview_denied_without_identity()
    {
        using var db = TestDbFactory.Create();
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, null);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Preview(
            new DynamicSupplierAgingReportRequest()));
        Assert.Equal(ErrorCodes.Unauthorized, ex.Code);
    }

    // ==================== 3. 选定列与顺序 ====================

    [Fact]
    public async Task Preview_projects_only_selected_columns_in_request_order()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedSupplier(db, SupplierA, "甲供应商");
        SeedInvoice(db, 968101L, SupplierA, "CNY", "A001", 100m, 0m, 100m,
            PurchaseInvoiceRules.StatusRecorded, AsOf.AddDays(-40), dueDate: AsOf.AddDays(5));
        await db.SaveChangesAsync();
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var page = PreviewOk(await ctl.Preview(new DynamicSupplierAgingReportRequest
        {
            Fields = new() { "grossAmount", "invoiceNumber", "currency" }
        }));

        Assert.Equal(new[] { "grossAmount", "invoiceNumber", "currency" }, page.Columns.Select(c => c.Key));
        var row = Assert.Single(page.Rows);
        Assert.Equal(new[] { "grossAmount", "invoiceNumber", "currency" }, row.Keys.ToArray());
        Assert.Equal("A001", row["invoiceNumber"]);
        Assert.Equal("CNY", row["currency"]);
        Assert.Equal(100m, row["grossAmount"]);
    }

    [Fact]
    public async Task Preview_defaults_to_all_whitelist_fields()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedSupplier(db, SupplierA, "甲供应商");
        SeedInvoice(db, 968101L, SupplierA, "CNY", "A001", 100m, 0m, 100m,
            PurchaseInvoiceRules.StatusRecorded, AsOf.AddDays(-40), dueDate: AsOf.AddDays(5));
        await db.SaveChangesAsync();
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var page = PreviewOk(await ctl.Preview(new DynamicSupplierAgingReportRequest()));

        Assert.Equal(DynamicSupplierAgingReportRules.AllFieldKeys, page.Columns.Select(c => c.Key).ToArray());
        var row = Assert.Single(page.Rows);
        Assert.Equal(DynamicSupplierAgingReportRules.AllFieldKeys, row.Keys.ToArray());
    }

    // ==================== 4. 币种与未知证据保留 ====================

    [Fact]
    public async Task Preview_retains_separate_currencies_and_unknown_due_date_evidence()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedSupplier(db, SupplierA, "甲供应商");
        SeedSupplier(db, SupplierB, "乙供应商");
        SeedInvoice(db, 968201L, SupplierA, "CNY", "B001", 100m, 0m, 100m,
            PurchaseInvoiceRules.StatusRecorded, AsOf.AddDays(-40), dueDate: AsOf.AddDays(5));
        SeedInvoice(db, 968202L, SupplierB, "USD", "B002", 200m, 0m, 200m,
            PurchaseInvoiceRules.StatusRecorded, AsOf.AddDays(-40));
        await db.SaveChangesAsync();
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var page = PreviewOk(await ctl.Preview(new DynamicSupplierAgingReportRequest
        {
            Fields = new() { "invoiceNumber", "currency", "grossAmount", "dueDateKnown", "agingBucket", "allocationState", "remainingAmount", "activeAllocatedAmount" }
        }));

        Assert.Equal(2, page.Rows.Count);
        var cny = Assert.Single(page.Rows, r => (string)r["invoiceNumber"]! == "B001");
        var usd = Assert.Single(page.Rows, r => (string)r["invoiceNumber"]! == "B002");

        Assert.Equal("CNY", cny["currency"]);
        Assert.Equal(100m, cny["grossAmount"]);
        Assert.Equal(true, cny["dueDateKnown"]);
        Assert.Equal(SupplierReconciliationAgingSemantics.BucketNotDue, cny["agingBucket"]);
        Assert.Equal(SupplierReconciliationAgingSemantics.AllocationNone, cny["allocationState"]);
        Assert.Equal(100m, cny["remainingAmount"]);
        Assert.Equal(0m, cny["activeAllocatedAmount"]);

        Assert.Equal("USD", usd["currency"]);
        Assert.Equal(200m, usd["grossAmount"]);
        Assert.Equal(false, usd["dueDateKnown"]);
        Assert.Null(usd["agingBucket"]);
        Assert.Equal(SupplierReconciliationAgingSemantics.AllocationNone, usd["allocationState"]);
        Assert.Equal(200m, usd["remainingAmount"]);
        Assert.Equal(0m, usd["activeAllocatedAmount"]);
    }

    // ==================== 5. 稳定分页与 200 上限 ====================

    [Fact]
    public async Task Preview_pages_stably_with_200_cap()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedSupplier(db, SupplierA, "甲供应商");
        for (var i = 1; i <= 205; i++)
        {
            SeedInvoice(db, 968300L + i, SupplierA, "CNY", $"C{i:D3}", 100m, 0m, 100m,
                PurchaseInvoiceRules.StatusRecorded, AsOf.AddDays(-40), dueDate: AsOf.AddDays(5));
        }
        await db.SaveChangesAsync();
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var p1 = PreviewOk(await ctl.Preview(new DynamicSupplierAgingReportRequest
        {
            Fields = new() { "invoiceNumber" }, Page = 1, PageSize = 200
        }));
        Assert.Equal(205, p1.Total);
        Assert.Equal(200, p1.Rows.Count);
        Assert.Equal(2, p1.TotalPages);

        var p2 = PreviewOk(await ctl.Preview(new DynamicSupplierAgingReportRequest
        {
            Fields = new() { "invoiceNumber" }, Page = 2, PageSize = 200
        }));
        Assert.Equal(5, p2.Rows.Count);
    }

    // ==================== 6. 无效输入（源读取前拒绝） ====================

    [Fact]
    public async Task Preview_page_size_over_limit_or_invalid_rejected()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var ex1 = await Assert.ThrowsAsync<BusinessException>(() => ctl.Preview(
            new DynamicSupplierAgingReportRequest { PageSize = 201 }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex1.Code);

        var ex2 = await Assert.ThrowsAsync<BusinessException>(() => ctl.Preview(
            new DynamicSupplierAgingReportRequest { PageSize = 0 }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex2.Code);
    }

    [Fact]
    public async Task Preview_unknown_field_rejected()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Preview(
            new DynamicSupplierAgingReportRequest { Fields = new() { "invoiceNumber", "bogus" } }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public async Task Preview_invalid_filters_rejected()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        Assert.Equal(ErrorCodes.InvalidParameter, (await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Preview(new DynamicSupplierAgingReportRequest { SupplierId = -1 }))).Code);
        Assert.Equal(ErrorCodes.InvalidParameter, (await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Preview(new DynamicSupplierAgingReportRequest { Currency = "BOGUS" }))).Code);
        Assert.Equal(ErrorCodes.InvalidParameter, (await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Preview(new DynamicSupplierAgingReportRequest { InvoiceStatus = "bogus" }))).Code);
        Assert.Equal(ErrorCodes.InvalidParameter, (await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Preview(new DynamicSupplierAgingReportRequest { AllocationState = "bogus" }))).Code);
        Assert.Equal(ErrorCodes.InvalidParameter, (await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Preview(new DynamicSupplierAgingReportRequest
            {
                InvoiceDateFrom = new DateTime(2026, 2, 1),
                InvoiceDateTo = new DateTime(2026, 1, 1)
            }))).Code);
        Assert.Equal(ErrorCodes.InvalidParameter, (await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Preview(new DynamicSupplierAgingReportRequest { AsOfDate = new DateTime(1800, 1, 1) }))).Code);
    }

    // ==================== 7. 只读不写库 ====================

    [Fact]
    public async Task Preview_is_read_only_and_writes_nothing()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedSupplier(db, SupplierA, "甲供应商");
        SeedInvoice(db, 968401L, SupplierA, "CNY", "D001", 100m, 0m, 100m,
            PurchaseInvoiceRules.StatusRecorded, AsOf.AddDays(-40), dueDate: AsOf.AddDays(5));
        await db.SaveChangesAsync();

        var counting = CountingDbContext.Wrap(db);
        var ctl = new DynamicSupplierAgingReportController(counting.Proxy);
        TestAuth.SetUser(ctl, uid);

        var page = PreviewOk(await ctl.Preview(new DynamicSupplierAgingReportRequest
        {
            Fields = new() { "invoiceNumber" }, PageSize = 10
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




