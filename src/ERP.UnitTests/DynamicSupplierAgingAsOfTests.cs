using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-168 动态供应商对账与账龄报表「有效 as-of 日期回传」单元测试。
/// <para>语义：每次预览由 ERP-068 只读派生解析出<b>唯一</b>有效账龄基准日（省略 as-of 时默认当天），
/// 并原样回传到结果页；分组计数与金额汇总使用的账龄基准与该回传值完全一致，绝不重复解析出第二个日期。</para>
/// <para>覆盖：显式固定 as-of 日期（固定分桶期望，确定性）、省略 as-of（默认当天）、无采购订单菜单授权（权限不足）、
/// 无身份（未认证）与只读不写库。</para>
/// <para>全部使用内存数据库（TestDbFactory），不连接 SQL Server、不启动 API、不执行任何 SQL / seed。</para>
/// </summary>
public class DynamicSupplierAgingAsOfTests
{
    private static readonly DateTime AsOf = new(2026, 9, 24);
    private const long SupplierA = 968001L;

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

    // ==================== 1. 显式固定 as-of 日期 ====================

    [Fact]
    public async Task Preview_explicit_asof_returns_effective_date_and_fixed_bucket()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedSupplier(db, SupplierA, "甲供应商");
        // 固定边界：到期日 = as-of − 35 天 → 逾期 31 ~ 60 天（确定性分桶，与运行当天无关）
        SeedInvoice(db, 968101L, SupplierA, "CNY", "A001", 100m, 0m, 100m,
            PurchaseInvoiceRules.StatusRecorded, AsOf.AddDays(-70), dueDate: AsOf.AddDays(-35));
        await db.SaveChangesAsync();
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var page = PreviewOk(await ctl.Preview(new DynamicSupplierAgingReportRequest
        {
            AsOfDate = AsOf,
            Fields = new() { "invoiceNumber", "agingBucket" }
        }));

        Assert.Equal(AsOf, page.AsOfDate);
        Assert.Equal($"账龄基准日（as-of）：{AsOf:yyyy-MM-dd}", page.AsOfDateText);
        var row = Assert.Single(page.Rows);
        Assert.Equal(SupplierReconciliationAgingSemantics.BucketOverdue31To60, row["agingBucket"]);
    }

    // ==================== 2. 省略 as-of（默认当天） ====================

    [Fact]
    public async Task Preview_omitted_asof_defaults_to_today()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedSupplier(db, SupplierA, "甲供应商");
        SeedInvoice(db, 968102L, SupplierA, "CNY", "A002", 100m, 0m, 100m,
            PurchaseInvoiceRules.StatusRecorded, DateTime.Today.AddDays(-40), dueDate: DateTime.Today.AddDays(5));
        await db.SaveChangesAsync();
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var page = PreviewOk(await ctl.Preview(new DynamicSupplierAgingReportRequest
        {
            Fields = new() { "invoiceNumber" }
        }));

        Assert.Equal(DateTime.Today, page.AsOfDate);
        Assert.Contains(DateTime.Today.ToString("yyyy-MM-dd"), page.AsOfDateText);
    }

    // ==================== 3. 拒绝（fail closed） ====================

    [Fact]
    public async Task Preview_denied_without_menu_forbidden()
    {
        using var db = TestDbFactory.Create();
        var user = SeedUser(db, "nomenu");
        var role = SeedRole(db, "Role-nomenu");
        SeedUserRole(db, user.Id, role.Id);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Preview(
            new DynamicSupplierAgingReportRequest { AsOfDate = AsOf }));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    [Fact]
    public async Task Preview_denied_without_identity_unauthorized()
    {
        using var db = TestDbFactory.Create();
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, null);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Preview(
            new DynamicSupplierAgingReportRequest { AsOfDate = AsOf }));
        Assert.Equal(ErrorCodes.Unauthorized, ex.Code);
    }

    // ==================== 4. 只读不写库 ====================

    [Fact]
    public async Task Preview_returns_asof_without_writes()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedSupplier(db, SupplierA, "甲供应商");
        SeedInvoice(db, 968103L, SupplierA, "CNY", "A003", 100m, 0m, 100m,
            PurchaseInvoiceRules.StatusRecorded, AsOf.AddDays(-40), dueDate: AsOf.AddDays(5));
        await db.SaveChangesAsync();

        var counting = DynamicSupplierAgingReportTests.CountingDbContext.Wrap(db);
        var ctl = new DynamicSupplierAgingReportController(counting.Proxy);
        TestAuth.SetUser(ctl, uid);

        var page = PreviewOk(await ctl.Preview(new DynamicSupplierAgingReportRequest
        {
            AsOfDate = AsOf,
            Fields = new() { "invoiceNumber" },
            PageSize = 10
        }));

        Assert.Equal(AsOf, page.AsOfDate);
        Assert.Equal(1, page.Total);
        Assert.Equal(0, counting.WriteCalls);
    }
}
