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
/// ERP-179 动态销售订单出货 / 财务进度报表「当前页金额汇总 PDF 导出」单元测试。
/// 覆盖：none 保留既有选定列 PDF（原 ERP-159 行为）、非 none 金额汇总模式追加独立「金额汇总（当前页）」分区、
/// 出货状态 / 收款链接状态拆分、多币种隔离、null 金额证据显式「未知」与已知 / 未知行数、空页 / 分页、长标签、
/// 无效金额汇总模式源读取前拒绝、无身份 / 无销售订单菜单授权拒绝、受限制业务员范围过滤、字体缺失显式失败，以及只读不写库。
/// <para>全部使用内存数据库（TestDbFactory），不连接 SQL Server、不启动 API、不执行任何 SQL / seed。</para>
/// </summary>
public class DynamicShipmentFinanceSummaryPdfTests
{
    private static readonly DateTime AsOf = new(2026, 9, 24);
    private const long CustomerA = 959001L;
    private const long CustomerB = 959002L;
    private const long ProductA = 959101L;

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
        SeedRoleMenu(db, role.Id, SeedMenu(db, DynamicShipmentFinanceReportRules.RequiredMenuCode).Id);
        return user.Id;
    }

    private static BaseEmployee SeedEmployee(ErpDbContext db, string code)
    {
        var employee = new BaseEmployee { EmployeeCode = code, EmployeeName = code, IsSalesman = true, Status = 1 };
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

    private static void SeedStockOut(ErpDbContext db, string stockOutNo, long? salesOrderId, long customerId,
        DocumentStatus status, params (long ProductId, decimal Quantity)[] lines)
    {
        var stockOut = new StockOut
        {
            StockOutNo = stockOutNo,
            StockOutDate = AsOf.AddDays(-5),
            SalesOrderId = salesOrderId,
            CustomerId = customerId,
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

    private static void SeedDepositApply(ErpDbContext db, string applyNo, long salesOrderId, long customerId,
        decimal amount, Currency currency, DocumentStatus status)
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

    private static (long UserId, long MineCustomerId, long OtherCustomerId) SeedRestrictedUser(
        ErpDbContext db, string userName)
    {
        var user = SeedUser(db, userName);
        var role = SeedRole(db, $"Role-{userName}", isSystem: false);
        SeedUserRole(db, user.Id, role.Id);
        SeedRoleMenu(db, role.Id, SeedMenu(db, DynamicShipmentFinanceReportRules.RequiredMenuCode).Id);
        var emp = SeedEmployee(db, userName);
        var mine = SeedCustomer(db, CustomerA, "我的客户", emp.Id);
        var other = SeedCustomer(db, CustomerB, "别人的客户", emp.Id + 1000);
        return (user.Id, mine.Id, other.Id);
    }


    private static DynamicShipmentFinanceReportController NewController(ErpDbContext db) => new(db);

    private static FileContentResult PdfOk(IActionResult result)
    {
        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal("application/pdf", file.ContentType);
        Assert.EndsWith(".pdf", file.FileDownloadName);
        Assert.StartsWith("%PDF-", Encoding.ASCII.GetString(file.FileContents));
        return file;
    }

    private static DynamicShipmentFinanceReportPageDto PreviewOk(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<ApiResponse<DynamicShipmentFinanceReportPageDto>>(ok.Value);
        Assert.NotNull(resp.Data);
        return resp.Data!;
    }

    private static PdfDocument OpenPdf(byte[] bytes)
    {
        var stream = new MemoryStream(bytes);
        return PdfReader.Open(stream);
    }


    // ==================== 1. none 保留既有选定列 PDF（不追加金额汇总分区） ====================

    [Fact]
    public async Task ExportPdf_none_retains_original_selected_column_document()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "甲客户");
        SeedOrder(db, "SO-NONE", CustomerA, Currency.USD, 100m);
        await db.SaveChangesAsync();

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var file = PdfOk(await ctl.ExportPdf(new DynamicShipmentFinanceReportRequest
        {
            Fields = new() { "orderNo" },
            SummaryMode = "none",
            PageSize = 200,
        }));

        // none：仅既有选定列数据分区（单页），无金额汇总分区。
        using var pdf = OpenPdf(file.FileContents);
        Assert.Equal(1, pdf.Pages.Count);
    }

    // ==================== 2. customerCurrency 追加独立金额汇总分区 ====================

    [Fact]
    public async Task ExportPdf_customerCurrency_appends_summary_section()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "甲客户");
        var order = SeedOrder(db, "SO-SUM", CustomerA, Currency.USD, 1000m);
        SeedDepositApply(db, "DEP-SUM", order.Id, CustomerA, 300m, Currency.USD, DocumentStatus.Approved);
        await db.SaveChangesAsync();

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var page = PreviewOk(await ctl.Preview(new DynamicShipmentFinanceReportRequest
        { SummaryMode = "customerCurrency", PageSize = 200 }));
        var summary = Assert.Single(page.Summaries!);
        Assert.Equal(1, summary.OrderCount);
        Assert.Equal(1000m, summary.OrderAmount);
        Assert.Equal(300m, summary.LinkedAmount);

        var file = PdfOk(await ctl.ExportPdf(new DynamicShipmentFinanceReportRequest
        {
            Fields = new() { "orderNo" },
            SummaryMode = "customerCurrency",
            PageSize = 200,
        }));

        // 数据分区（1 页）+ 金额汇总分区（宽列集拆多个列页）> 1 页。
        using var pdf = OpenPdf(file.FileContents);
        Assert.True(pdf.Pages.Count > 1);
        foreach (var p in pdf.Pages)
        {
            Assert.InRange(p.Width.Point, 594, 596);
            Assert.InRange(p.Height.Point, 841, 843);
        }
    }

    // ==================== 3. 出货状态拆分（customerCurrencyShipment） ====================

    [Fact]
    public async Task ExportPdf_customerCurrencyShipment_splits_by_shipment_state()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "甲客户");

        var unshipped = SeedOrder(db, "SO-SH-1", CustomerA, Currency.USD, 1000m);
        SeedDepositApply(db, "DEP-SH-1", unshipped.Id, CustomerA, 100m, Currency.USD, DocumentStatus.Approved);

        var shipped = SeedOrder(db, "SO-SH-2", CustomerA, Currency.USD, 500m);
        SeedDepositApply(db, "DEP-SH-2", shipped.Id, CustomerA, 50m, Currency.USD, DocumentStatus.Approved);
        SeedDetail(db, shipped.Id, ProductA, 5m);
        SeedStockOut(db, "CK-SH-1", shipped.Id, CustomerA, DocumentStatus.Approved, (ProductA, 5m));
        await db.SaveChangesAsync();

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var page = PreviewOk(await ctl.Preview(new DynamicShipmentFinanceReportRequest
        { SummaryMode = "customerCurrencyShipment", PageSize = 200 }));
        Assert.Equal(2, page.Summaries!.Count);
        var byStatus = page.Summaries.ToDictionary(s => s.ShipmentStatus!);
        Assert.Equal(1000m, byStatus["none"].OrderAmount);
        Assert.Equal(500m, byStatus["complete"].OrderAmount);
        Assert.All(page.Summaries, s => Assert.Null(s.FinanceLinkStatus));

        var file = PdfOk(await ctl.ExportPdf(new DynamicShipmentFinanceReportRequest
        {
            Fields = new() { "orderNo" },
            SummaryMode = "customerCurrencyShipment",
            PageSize = 200,
        }));
        using var pdf = OpenPdf(file.FileContents);
        Assert.True(pdf.Pages.Count > 1);
    }

    // ==================== 4. 收款链接状态拆分（customerCurrencyFinance） ====================

    [Fact]
    public async Task ExportPdf_customerCurrencyFinance_splits_by_finance_link_state()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "甲客户");

        var linked = SeedOrder(db, "SO-FN-1", CustomerA, Currency.USD, 1000m);
        SeedDepositApply(db, "DEP-FN-1", linked.Id, CustomerA, 300m, Currency.USD, DocumentStatus.Approved);
        SeedOrder(db, "SO-FN-2", CustomerA, Currency.USD, 500m); // 未链接
        await db.SaveChangesAsync();

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var page = PreviewOk(await ctl.Preview(new DynamicShipmentFinanceReportRequest
        { SummaryMode = "customerCurrencyFinance", PageSize = 200 }));
        Assert.Equal(2, page.Summaries!.Count);
        var byStatus = page.Summaries.ToDictionary(s => s.FinanceLinkStatus!);
        Assert.Equal(300m, byStatus["linked"].LinkedAmount);
        Assert.Null(byStatus["unlinked"].LinkedAmount);
        Assert.All(page.Summaries, s => Assert.Null(s.ShipmentStatus));

        var file = PdfOk(await ctl.ExportPdf(new DynamicShipmentFinanceReportRequest
        {
            Fields = new() { "orderNo" },
            SummaryMode = "customerCurrencyFinance",
            PageSize = 200,
        }));
        using var pdf = OpenPdf(file.FileContents);
        Assert.True(pdf.Pages.Count > 1);
    }


    // ==================== 5. 多币种隔离（绝不跨币种合并 / 换算，无应收 / 合计） ====================

    [Fact]
    public async Task ExportPdf_customerCurrency_keeps_multiple_currencies_separate()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "甲客户");
        SeedOrder(db, "SO-USD", CustomerA, Currency.USD, 100m);
        SeedOrder(db, "SO-CNY", CustomerA, Currency.CNY, 300m);
        await db.SaveChangesAsync();

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var page = PreviewOk(await ctl.Preview(new DynamicShipmentFinanceReportRequest
        { SummaryMode = "customerCurrency", PageSize = 200 }));
        Assert.Equal(2, page.Summaries!.Count);
        Assert.Contains(page.Summaries, s => s.Currency == "USD" && s.OrderAmount == 100m);
        Assert.Contains(page.Summaries, s => s.Currency == "CNY" && s.OrderAmount == 300m);

        var file = PdfOk(await ctl.ExportPdf(new DynamicShipmentFinanceReportRequest
        {
            Fields = new() { "orderNo" },
            SummaryMode = "customerCurrency",
            PageSize = 200,
        }));
        using var pdf = OpenPdf(file.FileContents);
        Assert.True(pdf.Pages.Count > 1);
    }

    // ==================== 6. null 金额证据显式「未知」（绝不回落 0） ====================

    [Fact]
    public void BuildRowCells_summary_null_amounts_render_unknown_with_counts()
    {
        var columns = new List<DynamicShipmentFinanceReportFieldDto>
        {
            new("customerName", "客户", "text", false),
            new("currency", "币种", "text", false),
            new("orderCount", "订单张数", "number", false),
            new("orderAmount", "订单金额", "number", false),
            new("knownLinkedAmountRows", "已关联金额已知行数", "number", false),
            new("unknownLinkedAmountRows", "已关联金额未知行数", "number", false),
            new("linkedAmount", "已关联金额", "number", false),
            new("uncoveredAmount", "未覆盖金额", "number", false),
            new("submittedAmount", "已提交金额", "number", false),
        };
        var row = new Dictionary<string, object?>
        {
            ["customerName"] = "甲客户",
            ["currency"] = "USD",
            ["orderCount"] = 1,
            ["orderAmount"] = 1000m,
            ["knownLinkedAmountRows"] = 0,
            ["unknownLinkedAmountRows"] = 1,
            ["linkedAmount"] = null,
            ["uncoveredAmount"] = null,
            ["submittedAmount"] = null,
        };

        var cells = DynamicShipmentFinancePdfExporter.BuildRowCells(columns, row);

        Assert.Equal(new[] { "甲客户", "USD", "1", "1000", "0", "1", "未知", "未知", "未知" }, cells);
    }

    [Fact]
    public async Task ExportPdf_unlinked_orders_keep_amounts_unknown()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "甲客户");
        SeedOrder(db, "SO-NULL", CustomerA, Currency.USD, 1000m); // 未链接：金额未知
        await db.SaveChangesAsync();

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var page = PreviewOk(await ctl.Preview(new DynamicShipmentFinanceReportRequest
        { SummaryMode = "customerCurrency", PageSize = 200 }));
        var summary = Assert.Single(page.Summaries!);
        Assert.Equal(1000m, summary.OrderAmount);
        Assert.Null(summary.LinkedAmount);
        Assert.Null(summary.UncoveredAmount);
        Assert.Null(summary.SubmittedAmount);
        Assert.Equal(0, summary.KnownLinkedAmountRows);
        Assert.Equal(1, summary.UnknownLinkedAmountRows);

        var file = PdfOk(await ctl.ExportPdf(new DynamicShipmentFinanceReportRequest
        {
            Fields = new() { "orderNo" },
            SummaryMode = "customerCurrency",
            PageSize = 200,
        }));
        using var pdf = OpenPdf(file.FileContents);
        Assert.True(pdf.Pages.Count > 1);
    }


    // ==================== 7. 空页显式提示 ====================

    [Fact]
    public async Task ExportPdf_empty_page_renders_visible_empty_note_section()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        await db.SaveChangesAsync();

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var file = PdfOk(await ctl.ExportPdf(new DynamicShipmentFinanceReportRequest
        {
            Fields = new() { "orderNo" },
            SummaryMode = "customerCurrency",
            PageSize = 200,
        }));

        // 数据分区（空态 1 页）+ 金额汇总分区（空态提示 1 页）≥ 2 页。
        using var pdf = OpenPdf(file.FileContents);
        Assert.True(pdf.Pages.Count >= 2);
    }

    // ==================== 8. 分页只汇总当前页 ====================

    [Fact]
    public async Task ExportPdf_paged_results_only_count_current_page()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "甲客户");
        for (var i = 1; i <= 3; i++)
        {
            var order = SeedOrder(db, $"SO-PG-{i}", CustomerA, Currency.USD, 100m);
            SeedDepositApply(db, $"DEP-PG-{i}", order.Id, CustomerA, 100m, Currency.USD, DocumentStatus.Approved);
        }

        await db.SaveChangesAsync();

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var page1 = PreviewOk(await ctl.Preview(new DynamicShipmentFinanceReportRequest
        { SummaryMode = "customerCurrency", Page = 1, PageSize = 2 }));
        var page2 = PreviewOk(await ctl.Preview(new DynamicShipmentFinanceReportRequest
        { SummaryMode = "customerCurrency", Page = 2, PageSize = 2 }));

        var s1 = Assert.Single(page1.Summaries!);
        var s2 = Assert.Single(page2.Summaries!);
        Assert.Equal(2, s1.OrderCount);
        Assert.Equal(200m, s1.OrderAmount);
        Assert.Equal(1, s2.OrderCount);
        Assert.Equal(100m, s2.OrderAmount);
        Assert.Equal(3, page1.Total);

        var file = PdfOk(await ctl.ExportPdf(new DynamicShipmentFinanceReportRequest
        {
            Fields = new() { "orderNo" },
            SummaryMode = "customerCurrency",
            Page = 1,
            PageSize = 2,
        }));
        using var pdf = OpenPdf(file.FileContents);
        Assert.True(pdf.Pages.Count > 1);
    }


    // ==================== 9. 长标签 / 多组分页 ====================

    [Fact]
    public async Task ExportPdf_long_label_renders_without_failure()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        var longName = "超长客户名称" + new string('甲', 300);
        SeedCustomer(db, CustomerA, longName);
        SeedOrder(db, "SO-LONG", CustomerA, Currency.USD, 100m);
        await db.SaveChangesAsync();

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var file = PdfOk(await ctl.ExportPdf(new DynamicShipmentFinanceReportRequest
        {
            Fields = new() { "orderNo" },
            SummaryMode = "customerCurrency",
            PageSize = 200,
        }));

        using var pdf = OpenPdf(file.FileContents);
        Assert.True(pdf.Pages.Count > 1);
    }

    [Fact]
    public async Task ExportPdf_many_summaries_paginate()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        for (var i = 0; i < 120; i++)
        {
            var id = 959100L + i;
            SeedCustomer(db, id, $"客户{i}");
            SeedOrder(db, $"SO-PAGE-{i}", id, Currency.USD, 100m);
        }

        await db.SaveChangesAsync();

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var file = PdfOk(await ctl.ExportPdf(new DynamicShipmentFinanceReportRequest
        {
            Fields = new() { "orderNo" },
            SummaryMode = "customerCurrency",
            PageSize = 200,
        }));

        // 数据分区 + 金额汇总分区（120 行会纵向分页）总页数明显超过单页。
        using var pdf = OpenPdf(file.FileContents);
        Assert.True(pdf.Pages.Count > 4);
    }

    // ==================== 10. 无效金额汇总模式（源读取之前拒绝） ====================

    [Fact]
    public async Task ExportPdf_rejects_invalid_summary_mode_before_source_reads()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.ExportPdf(
            new DynamicShipmentFinanceReportRequest { SummaryMode = "grandTotal" }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    // ==================== 11. 授权（fail closed） ====================

    [Fact]
    public async Task ExportPdf_summary_denied_without_menu()
    {
        using var db = TestDbFactory.Create();
        var user = SeedUser(db, "no-menu");
        var role = SeedRole(db, "NoMenu", isSystem: false);
        SeedUserRole(db, user.Id, role.Id);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.ExportPdf(
            new DynamicShipmentFinanceReportRequest { SummaryMode = "customerCurrency" }));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    [Fact]
    public async Task ExportPdf_summary_denied_without_identity()
    {
        using var db = TestDbFactory.Create();
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, null);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.ExportPdf(
            new DynamicShipmentFinanceReportRequest { SummaryMode = "customerCurrency" }));
        Assert.Equal(ErrorCodes.Unauthorized, ex.Code);
    }


    // ==================== 12. 字体缺失显式失败 ====================

    [Fact]
    public void ExportPdf_summary_font_missing_fails_explicitly()
    {
        var page = new DynamicShipmentFinanceReportPageDto(
            new List<DynamicShipmentFinanceReportFieldDto> { new("orderNo", "订单号", "text", false) },
            new List<Dictionary<string, object?>>(),
            0, 1, 20, 0,
            "只读", "边界", "免责",
            "none", null,
            "customerCurrency",
            new List<DynamicShipmentFinanceReportSummaryDto>());

        var ex = Assert.Throws<BusinessException>(() =>
            DynamicShipmentFinancePdfExporter.Export(page, @"Z:\__missing__\simhei.ttf"));
        Assert.Equal(ErrorCodes.InternalError, ex.Code);
        Assert.Contains("SimHei", ex.Message);
    }

    // ==================== 13. 受限制业务员范围过滤 ====================

    [Fact]
    public async Task ExportPdf_scopes_restricted_salesperson_to_assigned_customers()
    {
        using var db = TestDbFactory.Create();
        var (userId, mine, _) = SeedRestrictedUser(db, "alice");

        var mineOrder = SeedOrder(db, "SO-SC-1", mine, Currency.USD, 100m);
        SeedDepositApply(db, "DEP-SC-1", mineOrder.Id, mine, 100m, Currency.USD, DocumentStatus.Approved);
        SeedOrder(db, "SO-SC-2", CustomerB, Currency.USD, 300m);
        await db.SaveChangesAsync();

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, userId);

        var page = PreviewOk(await ctl.Preview(new DynamicShipmentFinanceReportRequest
        { SummaryMode = "customerCurrency", PageSize = 200 }));
        var summary = Assert.Single(page.Summaries!);
        Assert.Equal(mine, summary.CustomerId);
        Assert.Equal(100m, summary.OrderAmount);

        var file = PdfOk(await ctl.ExportPdf(new DynamicShipmentFinanceReportRequest
        {
            Fields = new() { "orderNo" },
            SummaryMode = "customerCurrency",
            PageSize = 200,
        }));
        using var pdf = OpenPdf(file.FileContents);
        Assert.True(pdf.Pages.Count > 1);
    }

    // ==================== 14. 只读不写库 ====================

    [Fact]
    public async Task ExportPdf_summary_does_not_write_to_database()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "只读客户");
        var order = SeedOrder(db, "SO-RO", CustomerA, Currency.USD, 100m);
        SeedDepositApply(db, "DEP-RO", order.Id, CustomerA, 30m, Currency.USD, DocumentStatus.Approved);
        await db.SaveChangesAsync();

        var counting = CountingDbContext.Wrap(db);
        var ctl = new DynamicShipmentFinanceReportController(counting.Proxy);
        TestAuth.SetUser(ctl, uid);

        var file = PdfOk(await ctl.ExportPdf(new DynamicShipmentFinanceReportRequest
        {
            Fields = new() { "orderNo" },
            SummaryMode = "customerCurrency",
            PageSize = 200,
        }));

        Assert.NotEmpty(file.FileContents);
        Assert.Equal(0, counting.WriteCalls);
    }

    // ==================== 15. 只读计数上下文（断言不写库） ====================

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

