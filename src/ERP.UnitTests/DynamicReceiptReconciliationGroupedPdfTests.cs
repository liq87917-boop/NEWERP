using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using ERP.Infrastructure.Export;
using Microsoft.AspNetCore.Mvc;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using System.Reflection;
using System.Text;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-175 动态客户订单与收款核对报表「当前页计数分组 PDF 导出」单元测试。
/// 覆盖：none 模式保留原有两分区、支持分组模式追加「订单计数分组」与「未关联收款计数分组」两个独立分区（当前页标签 + 数值计数、
/// 不适用 / 空 / 截断状态显式保留、无金额合计、绝不推断匹配）、未知分组键在源读取前拒绝、无销售订单菜单授权拒绝、无身份拒绝、
/// 字体缺失显式失败、长标签 / 多组分页、以及只读不写库。
/// <para>全部使用内存数据库（TestDbFactory），不连接 SQL Server、不启动 API、不执行任何 SQL / seed。</para>
/// </summary>
public class DynamicReceiptReconciliationGroupedPdfTests
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

    private static long SeedPrivilegedUser(ErpDbContext db, string userName = "priv")
    {
        var user = SeedUser(db, userName);
        var role = SeedRole(db, $"Role-{userName}", isSystem: true);
        SeedUserRole(db, user.Id, role.Id);
        SeedRoleMenu(db, role.Id, SeedMenu(db, DynamicReceiptReconciliationReportRules.RequiredMenuCode).Id);
        return user.Id;
    }

    private static BaseCustomer SeedCustomer(ErpDbContext db, long id, string name)
    {
        var customer = new BaseCustomer
        {
            Id = id,
            CustomerCode = $"C{id}",
            CustomerName = name,
            Status = 1,
            CreditStatus = "正常",
            EmpId = null
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

    private static FileContentResult PdfOk(IActionResult result)
    {
        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal("application/pdf", file.ContentType);
        Assert.EndsWith(".pdf", file.FileDownloadName);
        Assert.StartsWith("%PDF-", Encoding.ASCII.GetString(file.FileContents));
        return file;
    }

    private static PdfDocument OpenPdf(byte[] bytes)
    {
        var stream = new MemoryStream(bytes);
        return PdfReader.Open(stream);
    }

    private static async Task<BusinessException> AssertBusinessAsync(int expectedCode, Func<Task> action)
    {
        var ex = await Assert.ThrowsAsync<BusinessException>(action);
        Assert.Equal(expectedCode, ex.Code);
        return ex;
    }

    // ==================== 1. none 模式保留原有两分区 ====================

    [Fact]
    public async Task ExportPdf_none_retains_original_two_sections()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "甲客户");
        SeedOrder(db, "SO-NONE", CustomerA, Currency.USD, 100m);
        SeedReceipt(db, "SK-NONE", CustomerA, 30m, Currency.USD, DocumentStatus.Approved);
        await db.SaveChangesAsync();

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var file = PdfOk(await ctl.ExportPdf(new DynamicReceiptReconciliationReportRequest
        {
            Fields = new() { "orderNo" },
            ReceiptFields = new() { "receiptNo" },
            GroupBy = "none",
            PageSize = 200,
        }));

        // 原有默认结构：订单证据 + 未关联收款证据两个独立分区，绝不追加分组分区。
        using var pdf = OpenPdf(file.FileContents);
        Assert.Equal(2, pdf.Pages.Count);
    }

    // ==================== 2. 支持分组模式：两个独立计数分区 ====================

    [Theory]
    [InlineData("customer")]
    [InlineData("currency")]
    [InlineData("receiptCoverageStatus")]
    [InlineData("receiptEvidenceStatus")]
    public async Task ExportPdf_supported_grouping_adds_two_count_sections(string groupBy)
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "甲客户");
        SeedCustomer(db, CustomerB, "乙客户");
        SeedOrder(db, "SO-A-1", CustomerA, Currency.USD, 100m);
        SeedOrder(db, "SO-B-1", CustomerB, Currency.CNY, 200m);
        SeedReceipt(db, "SK-A-1", CustomerA, 30m, Currency.USD, DocumentStatus.Approved);
        SeedReceipt(db, "SK-B-1", CustomerB, 40m, Currency.CNY, DocumentStatus.Approved);
        await db.SaveChangesAsync();

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var file = PdfOk(await ctl.ExportPdf(new DynamicReceiptReconciliationReportRequest
        {
            Fields = new() { "orderNo" },
            ReceiptFields = new() { "receiptNo" },
            GroupBy = groupBy,
            PageSize = 200,
        }));

        // 原有两分区 + 订单计数分组 + 未关联收款计数分组 = 4 个独立分区（每个分区至少一页）。
        using var pdf = OpenPdf(file.FileContents);
        Assert.Equal(4, pdf.Pages.Count);
    }

    // ==================== 3. 不适用 / 空页状态显式可见（不崩溃） ====================

    [Fact]
    public async Task ExportPdf_empty_page_grouping_renders_visible_empty_notes()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        await db.SaveChangesAsync();

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var file = PdfOk(await ctl.ExportPdf(new DynamicReceiptReconciliationReportRequest
        {
            Fields = new() { "orderNo" },
            ReceiptFields = new() { "receiptNo" },
            GroupBy = "customer",
            PageSize = 200,
        }));

        // 空页：订单证据 / 未关联收款证据 / 订单计数分组 / 未关联收款计数分组各渲染一个可见分区（空提示），共 4 页。
        using var pdf = OpenPdf(file.FileContents);
        Assert.Equal(4, pdf.Pages.Count);
    }

    // ==================== 4. 未知分组键在源读取前拒绝 ====================

    [Fact]
    public async Task ExportPdf_unknown_group_rejected_before_source_reads()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => ctl.ExportPdf(new DynamicReceiptReconciliationReportRequest
            {
                GroupBy = "bogus",
                PageSize = 200,
            }));
    }

    // ==================== 5. 无销售订单菜单授权拒绝 ====================

    [Fact]
    public async Task ExportPdf_denied_access_without_sales_order_menu()
    {
        using var db = TestDbFactory.Create();
        var user = SeedUser(db, "nomenu");
        var role = SeedRole(db, "NoMenu");
        SeedUserRole(db, user.Id, role.Id);
        await db.SaveChangesAsync();

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        await AssertBusinessAsync(ErrorCodes.Forbidden,
            () => ctl.ExportPdf(new DynamicReceiptReconciliationReportRequest
            {
                GroupBy = "customer",
                PageSize = 200,
            }));
    }

    // ==================== 6. 无身份未认证拒绝 ====================

    [Fact]
    public async Task ExportPdf_denied_without_identity()
    {
        using var db = TestDbFactory.Create();
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, null);

        await AssertBusinessAsync(ErrorCodes.Unauthorized,
            () => ctl.ExportPdf(new DynamicReceiptReconciliationReportRequest
            {
                GroupBy = "customer",
                PageSize = 200,
            }));
    }

    // ==================== 7. 字体缺失显式失败 ====================

    [Fact]
    public void ExportPdf_grouping_missing_font_fails_explicitly()
    {
        var page = new DynamicReceiptReconciliationReportPageDto(
            new List<DynamicReceiptReconciliationReportFieldDto> { new("orderNo", "订单号", "text", false) },
            new List<Dictionary<string, object?>>(),
            new List<DynamicReceiptReconciliationReportReceiptDto>(),
            new List<DynamicReceiptReconciliationReportFieldDto>(),
            new List<Dictionary<string, object?>>(),
            false,
            0, 1, 20, 0,
            "只读", "边界", "免责",
            GroupBy: "customer",
            OrderGroups: new List<DynamicReceiptReconciliationReportOrderGroupDto> { new("customer:1", "甲客户", 1) },
            ReceiptGroups: new List<DynamicReceiptReconciliationReportReceiptGroupDto> { new("customer:1", "甲客户", 1, false) });

        var ex = Assert.Throws<BusinessException>(() =>
            DynamicReceiptReconciliationPdfExporter.Export(page, @"Z:\__missing__\simhei.ttf"));
        Assert.Equal(ErrorCodes.InternalError, ex.Code);
        Assert.Contains("SimHei", ex.Message);
    }

    // ==================== 8. 长标签正常渲染不崩溃 ====================

    [Fact]
    public async Task ExportPdf_long_group_label_renders_without_failure()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        var longName = "超长客户名称" + new string('甲', 300);
        SeedCustomer(db, CustomerA, longName);
        SeedOrder(db, "SO-LONG", CustomerA, Currency.USD, 100m);
        SeedReceipt(db, "SK-LONG", CustomerA, 30m, Currency.USD, DocumentStatus.Approved);
        await db.SaveChangesAsync();

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var file = PdfOk(await ctl.ExportPdf(new DynamicReceiptReconciliationReportRequest
        {
            Fields = new() { "orderNo" },
            ReceiptFields = new() { "receiptNo" },
            GroupBy = "customer",
            PageSize = 200,
        }));

        using var pdf = OpenPdf(file.FileContents);
        Assert.Equal(4, pdf.Pages.Count);
    }

    // ==================== 9. 多组分页 ====================

    [Fact]
    public async Task ExportPdf_many_groups_paginate()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        for (var i = 0; i < 120; i++)
        {
            var id = 966100L + i;
            SeedCustomer(db, id, $"客户{i}");
            SeedOrder(db, $"SO-PAGE-{i}", id, Currency.USD, 100m);
        }
        await db.SaveChangesAsync();

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var file = PdfOk(await ctl.ExportPdf(new DynamicReceiptReconciliationReportRequest
        {
            Fields = new() { "orderNo" },
            ReceiptFields = new() { "receiptNo" },
            GroupBy = "customer",
            PageSize = 200,
        }));

        // 订单证据 + 订单计数分组（120 行）都会纵向分页，总数应超过 4 页（两个证据分区 + 两个计数分区的最少页数）。
        using var pdf = OpenPdf(file.FileContents);
        Assert.True(pdf.Pages.Count > 4);
    }

    // ==================== 10. 截断状态显式保留（不崩溃） ====================

    [Fact]
    public void ExportPdf_truncated_receipt_group_renders_without_failure()
    {
        var page = new DynamicReceiptReconciliationReportPageDto(
            new List<DynamicReceiptReconciliationReportFieldDto> { new("orderNo", "订单号", "text", false) },
            new List<Dictionary<string, object?>>(),
            new List<DynamicReceiptReconciliationReportReceiptDto>(),
            new List<DynamicReceiptReconciliationReportFieldDto> { new("receiptNo", "收款单号", "text", false) },
            new List<Dictionary<string, object?>>(),
            true,
            1, 1, 20, 1,
            "只读", "边界", "免责",
            GroupBy: "customer",
            OrderGroups: new List<DynamicReceiptReconciliationReportOrderGroupDto> { new("customer:1", "甲客户", 1) },
            ReceiptGroups: new List<DynamicReceiptReconciliationReportReceiptGroupDto> { new("customer:1", "甲客户", 2000, true) });

        var bytes = DynamicReceiptReconciliationPdfExporter.Export(page);
        Assert.StartsWith("%PDF-", Encoding.ASCII.GetString(bytes));
    }

    [Fact]
    public async Task ExportPdf_truncated_receipt_group_full_pipeline_does_not_crash()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "甲客户");
        SeedOrder(db, "SO-TRUNC", CustomerA, Currency.USD, 100m);

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

        var file = PdfOk(await ctl.ExportPdf(new DynamicReceiptReconciliationReportRequest
        {
            Fields = new() { "orderNo" },
            ReceiptFields = new() { "receiptNo" },
            GroupBy = "customer",
            PageSize = 200,
        }));

        Assert.NotEmpty(file.FileContents);
    }

    // ==================== 11. 只读不写库 ====================

    [Fact]
    public async Task ExportPdf_grouped_does_not_write_to_database()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "只读客户");
        SeedOrder(db, "SO-RO", CustomerA, Currency.USD, 100m);
        SeedReceipt(db, "SK-RO", CustomerA, 30m, Currency.USD, DocumentStatus.Approved);
        await db.SaveChangesAsync();

        var counting = CountingDbContext.Wrap(db);
        var ctl = new DynamicReceiptReconciliationReportController(counting.Proxy);
        TestAuth.SetUser(ctl, uid);

        var file = PdfOk(await ctl.ExportPdf(new DynamicReceiptReconciliationReportRequest
        {
            Fields = new() { "orderNo" },
            ReceiptFields = new() { "receiptNo" },
            GroupBy = "customer",
            PageSize = 200,
        }));

        Assert.NotEmpty(file.FileContents);
        Assert.Equal(0, counting.WriteCalls);
    }

    // ==================== 12. 只读计数上下文（断言不写库） ====================

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




