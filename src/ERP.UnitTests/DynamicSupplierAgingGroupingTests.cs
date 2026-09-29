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
/// ERP-144 动态供应商对账与账龄报表「授权页面按供应商 / 币种 / 账龄分桶 / 分配状态的发票张数分布」单元测试。
/// <para>页面张数语义：<see cref="DynamicSupplierAgingReportRules.BuildGroupCounts"/> 只对「当前授权预览页」的供应商发票证据行计数，
/// 绝不求和任何金额、绝不跨币种合并或换算；未知到期日（<c>agingBucket</c> 为 null）与 over_allocated / unknown 分配证据类别始终保留（计数可为 0）；
/// supplier / currency 为动态分组（只出现本页存在的取值），agingBucket / allocationState 为固定证据分类。</para>
/// 覆盖：分组键规范化、供应商 / 币种 / 账龄分桶 / 分配状态分组、未知证据、空页 / 分页、无效分组键（fail closed）、
/// 无采购订单菜单授权（权限不足）、无身份（未认证）与只读不写库。
/// <para>全部使用内存数据库（TestDbFactory），不连接 SQL Server、不启动 API、不执行任何 SQL / seed。</para>
/// </summary>
public class DynamicSupplierAgingGroupingTests
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
        SeedRoleMenu(db, role.Id, SeedMenu(db, DynamicSupplierAgingReportRules.RequiredMenuCode).Id);
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

    // ==================== 1. 分组键规范化 ====================

    [Theory]
    [InlineData(null, "none")]
    [InlineData("", "none")]
    [InlineData("  ", "none")]
    [InlineData("none", "none")]
    [InlineData("NONE", "none")]
    [InlineData("supplier", "supplier")]
    [InlineData("Supplier", "supplier")]
    [InlineData("currency", "currency")]
    [InlineData("CURRENCY", "currency")]
    [InlineData("agingBucket", "agingBucket")]
    [InlineData("AGINGBUCKET", "agingBucket")]
    [InlineData("allocationState", "allocationState")]
    [InlineData("ALLOCATIONSTATE", "allocationState")]
    public void NormalizeGroupBy_合法取值_规范化(string? input, string expected)
    {
        Assert.Equal(expected, DynamicSupplierAgingReportRules.NormalizeGroupBy(input));
    }

    [Theory]
    [InlineData("customer")]
    [InlineData("quarter")]
    [InlineData("unknown")]
    [InlineData("suppliers")]
    [InlineData("aging_bucket")]
    [InlineData("币种")]
    public void NormalizeGroupBy_非法取值_拒绝(string? input)
    {
        var ex = Assert.Throws<BusinessException>(() => DynamicSupplierAgingReportRules.NormalizeGroupBy(input));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    // ==================== 2. 供应商 / 币种分组 ====================

    [Fact]
    public async Task Preview_供应商分组_按供应商计数()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedSupplier(db, SupplierA, "甲供应商");
        SeedSupplier(db, SupplierB, "乙供应商");
        SeedInvoice(db, 968101L, SupplierA, "CNY", "A001", 100m, 0m, 100m,
            PurchaseInvoiceRules.StatusRecorded, AsOf.AddDays(-40), dueDate: AsOf.AddDays(5));
        SeedInvoice(db, 968102L, SupplierA, "CNY", "A002", 100m, 0m, 100m,
            PurchaseInvoiceRules.StatusRecorded, AsOf.AddDays(-40), dueDate: AsOf.AddDays(5));
        SeedInvoice(db, 968103L, SupplierB, "CNY", "B001", 100m, 0m, 100m,
            PurchaseInvoiceRules.StatusRecorded, AsOf.AddDays(-40), dueDate: AsOf.AddDays(5));
        await db.SaveChangesAsync();
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var page = PreviewOk(await ctl.Preview(new DynamicSupplierAgingReportRequest
        { GroupBy = "supplier", AsOfDate = AsOf, PageSize = 50 }));

        Assert.Equal("supplier", page.GroupBy);
        Assert.Equal(2, page.Groups!.Count);
        Assert.Equal(2, page.Groups.Single(g => g.Key == $"supplier:{SupplierA}").Count);
        Assert.Equal(1, page.Groups.Single(g => g.Key == $"supplier:{SupplierB}").Count);
        Assert.Equal("甲供应商", page.Groups.Single(g => g.Key == $"supplier:{SupplierA}").Label);
    }

    [Fact]
    public async Task Preview_币种分组_只计数不求和()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedSupplier(db, SupplierA, "甲供应商");
        SeedInvoice(db, 968201L, SupplierA, "CNY", "C001", 100m, 0m, 100m,
            PurchaseInvoiceRules.StatusRecorded, AsOf.AddDays(-40), dueDate: AsOf.AddDays(5));
        SeedInvoice(db, 968202L, SupplierA, "USD", "C002", 200m, 0m, 200m,
            PurchaseInvoiceRules.StatusRecorded, AsOf.AddDays(-40), dueDate: AsOf.AddDays(5));
        SeedInvoice(db, 968203L, SupplierA, "CNY", "C003", 300m, 0m, 300m,
            PurchaseInvoiceRules.StatusRecorded, AsOf.AddDays(-40), dueDate: AsOf.AddDays(5));
        await db.SaveChangesAsync();
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var page = PreviewOk(await ctl.Preview(new DynamicSupplierAgingReportRequest
        { GroupBy = "currency", AsOfDate = AsOf, PageSize = 50 }));

        Assert.Equal("currency", page.GroupBy);
        Assert.Equal(2, page.Groups!.Count);
        Assert.Equal(2, page.Groups.Single(g => g.Key == "currency:CNY").Count);
        Assert.Equal(1, page.Groups.Single(g => g.Key == "currency:USD").Count);
        Assert.Equal("CNY", page.Groups.Single(g => g.Key == "currency:CNY").Label);
        // GroupDto 只有 key / label / count：结构上不存在任何金额字段，绝不跨币种求和金额
    }

    // ==================== 3. 未知证据（未知到期日 / 无效 / 未知分配证据） ====================

    [Fact]
    public async Task Preview_账龄分桶分组_未知到期日独立成组()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedSupplier(db, SupplierA, "甲供应商");
        SeedInvoice(db, 968301L, SupplierA, "CNY", "D001", 100m, 0m, 100m,
            PurchaseInvoiceRules.StatusRecorded, AsOf.AddDays(-40), dueDate: AsOf.AddDays(5));
        SeedInvoice(db, 968302L, SupplierA, "CNY", "D002", 100m, 0m, 100m,
            PurchaseInvoiceRules.StatusRecorded, AsOf.AddDays(-40));
        await db.SaveChangesAsync();
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var page = PreviewOk(await ctl.Preview(new DynamicSupplierAgingReportRequest
        { GroupBy = "agingBucket", AsOfDate = AsOf, PageSize = 50 }));

        Assert.Equal("agingBucket", page.GroupBy);
        Assert.Equal(6, page.Groups!.Count);
        Assert.Equal(1, page.Groups.Single(g => g.Key == "agingBucket:not_due").Count);
        Assert.Equal(1, page.Groups.Single(g => g.Key == "agingBucket:unknown_due_date").Count);
        Assert.All(
            page.Groups.Where(g => g.Key != "agingBucket:not_due" && g.Key != "agingBucket:unknown_due_date"),
            g => Assert.Equal(0, g.Count));
    }

    [Fact]
    public async Task Preview_分配状态分组_固定类别含无效与未知()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedSupplier(db, SupplierA, "甲供应商");
        SeedInvoice(db, 968401L, SupplierA, "CNY", "E001", 100m, 0m, 100m,
            PurchaseInvoiceRules.StatusRecorded, AsOf.AddDays(-40), dueDate: AsOf.AddDays(5));
        SeedInvoice(db, 968402L, SupplierA, "CNY", "E002", 100m, 0m, 100m,
            PurchaseInvoiceRules.StatusRecorded, AsOf.AddDays(-40), dueDate: AsOf.AddDays(5));
        await db.SaveChangesAsync();
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var page = PreviewOk(await ctl.Preview(new DynamicSupplierAgingReportRequest
        { GroupBy = "allocationState", AsOfDate = AsOf, PageSize = 50 }));

        Assert.Equal("allocationState", page.GroupBy);
        Assert.Equal(6, page.Groups!.Count);
        Assert.Equal(2, page.Groups.Single(g => g.Key == "allocationState:none").Count);
        Assert.Equal(0, page.Groups.Single(g => g.Key == "allocationState:over_allocated").Count);
        Assert.Equal(0, page.Groups.Single(g => g.Key == "allocationState:unknown").Count);
    }

    // ==================== 4. 不分组 / 分页 / 空页 ====================

    [Fact]
    public async Task Preview_none分组_返回空列表()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedSupplier(db, SupplierA, "甲供应商");
        SeedInvoice(db, 968501L, SupplierA, "CNY", "F001", 100m, 0m, 100m,
            PurchaseInvoiceRules.StatusRecorded, AsOf.AddDays(-40), dueDate: AsOf.AddDays(5));
        await db.SaveChangesAsync();
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var page = PreviewOk(await ctl.Preview(new DynamicSupplierAgingReportRequest
        { GroupBy = "none", AsOfDate = AsOf, PageSize = 50 }));

        Assert.Equal("none", page.GroupBy);
        Assert.NotNull(page.Groups);
        Assert.Empty(page.Groups);
    }

    [Fact]
    public async Task Preview_供应商分组_分页只统计当前页()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedSupplier(db, SupplierA, "甲供应商");
        for (var i = 1; i <= 3; i++)
        {
            SeedInvoice(db, 968600L + i, SupplierA, "CNY", $"G{i:D3}", 100m, 0m, 100m,
                PurchaseInvoiceRules.StatusRecorded, AsOf.AddDays(-40), dueDate: AsOf.AddDays(5));
        }
        await db.SaveChangesAsync();
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var page1 = PreviewOk(await ctl.Preview(new DynamicSupplierAgingReportRequest
        { GroupBy = "supplier", AsOfDate = AsOf, PageSize = 2, Page = 1 }));
        var page2 = PreviewOk(await ctl.Preview(new DynamicSupplierAgingReportRequest
        { GroupBy = "supplier", AsOfDate = AsOf, PageSize = 2, Page = 2 }));

        var g1 = Assert.Single(page1.Groups!);
        var g2 = Assert.Single(page2.Groups!);
        Assert.Equal(2, g1.Count);
        Assert.Equal(1, g2.Count);
        Assert.Equal(3, page1.Total);
    }

    [Fact]
    public async Task Preview_空页_动态分组为空_固定分类零计数()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var supplierPage = PreviewOk(await ctl.Preview(new DynamicSupplierAgingReportRequest
        { GroupBy = "supplier", AsOfDate = AsOf, PageSize = 50 }));
        Assert.Empty(supplierPage.Groups!);

        var currencyPage = PreviewOk(await ctl.Preview(new DynamicSupplierAgingReportRequest
        { GroupBy = "currency", AsOfDate = AsOf, PageSize = 50 }));
        Assert.Empty(currencyPage.Groups!);

        var bucketPage = PreviewOk(await ctl.Preview(new DynamicSupplierAgingReportRequest
        { GroupBy = "agingBucket", AsOfDate = AsOf, PageSize = 50 }));
        Assert.Equal(6, bucketPage.Groups!.Count);
        Assert.All(bucketPage.Groups, g => Assert.Equal(0, g.Count));

        var allocationPage = PreviewOk(await ctl.Preview(new DynamicSupplierAgingReportRequest
        { GroupBy = "allocationState", AsOfDate = AsOf, PageSize = 50 }));
        Assert.Equal(6, allocationPage.Groups!.Count);
        Assert.All(allocationPage.Groups, g => Assert.Equal(0, g.Count));
    }

    // ==================== 5. 无效分组键（fail closed） ====================

    [Fact]
    public async Task Preview_无效分组键_拒绝()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Preview(
            new DynamicSupplierAgingReportRequest { GroupBy = "quarter" }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    // ==================== 6. 权限与只读 ====================

    [Fact]
    public async Task GroupBy_无采购订单菜单授权_拒绝()
    {
        using var db = TestDbFactory.Create();
        var user = SeedUser(db, "nomenu");
        var role = SeedRole(db, "Role-nomenu");
        SeedUserRole(db, user.Id, role.Id);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Preview(
            new DynamicSupplierAgingReportRequest { GroupBy = "supplier" }));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    [Fact]
    public async Task GroupBy_无身份_未认证拒绝()
    {
        using var db = TestDbFactory.Create();
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, null);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Preview(
            new DynamicSupplierAgingReportRequest { GroupBy = "supplier" }));
        Assert.Equal(ErrorCodes.Unauthorized, ex.Code);
    }

    [Fact]
    public async Task GroupBy_只读不写库()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedSupplier(db, SupplierA, "甲供应商");
        SeedInvoice(db, 968701L, SupplierA, "CNY", "H001", 100m, 0m, 100m,
            PurchaseInvoiceRules.StatusRecorded, AsOf.AddDays(-40), dueDate: AsOf.AddDays(5));
        await db.SaveChangesAsync();

        var counting = CountingDbContext.Wrap(db);
        var ctl = new DynamicSupplierAgingReportController(counting.Proxy);
        TestAuth.SetUser(ctl, uid);

        var page = PreviewOk(await ctl.Preview(new DynamicSupplierAgingReportRequest
        { GroupBy = "supplier", AsOfDate = AsOf, PageSize = 10 }));

        Assert.Equal(1, page.Total);
        Assert.NotEmpty(page.Groups!);
        Assert.Equal(0, counting.WriteCalls);
    }

    // ==================== 7. 只读计数上下文（断言不写库） ====================

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
