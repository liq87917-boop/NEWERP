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
/// ERP-177 动态客户订单与收款核对报表「当前页金额汇总 PDF 导出」单元测试。
/// 覆盖：none 保留证据分区与选定计数分组分区、customerCurrency 追加「订单金额汇总」与「未关联收款金额汇总」两个独立分区、
/// 多币种隔离、null 金额显式「未知」（绝不回落 0）、pending / historical 收款证据状态显式拆分、长标签、分页、空页 / 截断、
/// 无效金额汇总模式源读取前拒绝、无身份 / 无菜单授权拒绝、字体缺失显式失败、以及只读不写库。
/// <para>全部使用内存数据库（TestDbFactory），不连接 SQL Server、不启动 API、不执行任何 SQL / seed。</para>
/// </summary>
public class DynamicReceiptReconciliationSummaryPdfTests
{
    private static readonly DateTime AsOf = new(2026, 9, 25);
    private const long CustomerA = 968001L;
    private const long CustomerB = 968002L;
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
        Currency currency, DocumentStatus status, long customerId)
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

    private static FileContentResult PdfOk(IActionResult result)
    {
        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal("application/pdf", file.ContentType);
        Assert.EndsWith(".pdf", file.FileDownloadName);
        Assert.StartsWith("%PDF-", Encoding.ASCII.GetString(file.FileContents));
        return file;
    }

    private static DynamicReceiptReconciliationReportPageDto PreviewOk(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<ApiResponse<DynamicReceiptReconciliationReportPageDto>>(ok.Value);
        Assert.NotNull(resp.Data);
        return resp.Data!;
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


    // ==================== 1. none 模式保留证据分区 ====================

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
            SummaryMode = "none",
            PageSize = 200,
        }));

        // 原有默认结构：订单证据 + 未关联收款证据两个独立分区，绝不追加金额汇总分区。
        using var pdf = OpenPdf(file.FileContents);
        Assert.Equal(2, pdf.Pages.Count);
    }

    [Fact]
    public async Task ExportPdf_none_with_grouping_retains_count_group_sections()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "甲客户");
        SeedOrder(db, "SO-GROUP", CustomerA, Currency.USD, 100m);
        SeedReceipt(db, "SK-GROUP", CustomerA, 30m, Currency.USD, DocumentStatus.Approved);
        await db.SaveChangesAsync();

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var file = PdfOk(await ctl.ExportPdf(new DynamicReceiptReconciliationReportRequest
        {
            Fields = new() { "orderNo" },
            ReceiptFields = new() { "receiptNo" },
            GroupBy = "customer",
            SummaryMode = "none",
            PageSize = 200,
        }));

        // none 金额汇总模式保留证据两分区 + 选定计数分组两个分区（共 4 页），绝不追加金额汇总分区。
        using var pdf = OpenPdf(file.FileContents);
        Assert.Equal(4, pdf.Pages.Count);
    }

    // ==================== 2. customerCurrency 追加两个金额汇总分区 ====================

    [Fact]
    public async Task ExportPdf_customerCurrency_appends_two_summary_sections()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "甲客户");
        SeedOrder(db, "SO-SUM", CustomerA, Currency.USD, 100m);
        SeedReceipt(db, "SK-SUM", CustomerA, 30m, Currency.USD, DocumentStatus.Approved);
        await db.SaveChangesAsync();

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var file = PdfOk(await ctl.ExportPdf(new DynamicReceiptReconciliationReportRequest
        {
            Fields = new() { "orderNo" },
            ReceiptFields = new() { "receiptNo" },
            SummaryMode = "customerCurrency",
            PageSize = 200,
        }));

        // 订单证据 + 未关联收款证据（2 页）+ 订单金额汇总（10 列 → 2 个列页）+ 未关联收款金额汇总（6 列 → 1 页）= 5 页。
        using var pdf = OpenPdf(file.FileContents);
        Assert.Equal(5, pdf.Pages.Count);
    }

    // ==================== 3. 无效金额汇总模式源读取前拒绝 ====================

    [Fact]
    public async Task ExportPdf_invalid_summary_mode_rejected_before_source_reads()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        await AssertBusinessAsync(ErrorCodes.InvalidParameter,
            () => ctl.ExportPdf(new DynamicReceiptReconciliationReportRequest
            {
                SummaryMode = "bogus",
                PageSize = 200,
            }));
    }

    // ==================== 4. 授权拒绝（fail closed） ====================

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
                SummaryMode = "customerCurrency",
                PageSize = 200,
            }));
    }

    [Fact]
    public async Task ExportPdf_denied_without_identity()
    {
        using var db = TestDbFactory.Create();
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, null);

        await AssertBusinessAsync(ErrorCodes.Unauthorized,
            () => ctl.ExportPdf(new DynamicReceiptReconciliationReportRequest
            {
                SummaryMode = "customerCurrency",
                PageSize = 200,
            }));
    }


    // ==================== 5. 字体缺失显式失败 ====================

    [Fact]
    public void ExportPdf_summary_missing_font_fails_explicitly()
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
            SummaryMode: "customerCurrency",
            OrderSummaries: new List<DynamicReceiptReconciliationReportOrderSummaryDto> { new(1, "甲客户", "USD", 1, 100m, 0, 1, null, 0, 1, null) },
            ReceiptSummaries: new List<DynamicReceiptReconciliationReportReceiptSummaryDto> { new(1, "甲客户", "USD", "active", 1, 30m, false) });

        var ex = Assert.Throws<BusinessException>(() =>
            DynamicReceiptReconciliationPdfExporter.Export(page, @"Z:\__missing__\simhei.ttf"));
        Assert.Equal(ErrorCodes.InternalError, ex.Code);
        Assert.Contains("SimHei", ex.Message);
    }

    // ==================== 6. 金额汇总单元格语义（null → 「未知」，绝不回落 0） ====================

    [Fact]
    public void FormatSummaryAmount_null_returns_unknown()
    {
        Assert.Equal("未知", DynamicReceiptReconciliationPdfExporter.FormatSummaryAmount(null));
        Assert.Equal("12.5", DynamicReceiptReconciliationPdfExporter.FormatSummaryAmount(12.5m));
    }

    [Fact]
    public void BuildOrderSummaryCells_null_amounts_render_unknown()
    {
        var cells = DynamicReceiptReconciliationPdfExporter.BuildOrderSummaryCells(
            new DynamicReceiptReconciliationReportOrderSummaryDto(
                1, "甲客户", "USD", 2, 100m, 0, 2, null, 0, 2, null));

        Assert.Equal("甲客户", cells[0]);
        Assert.Equal("USD", cells[1]);
        Assert.Equal("2", cells[2]);
        Assert.Equal("100", cells[3]);
        Assert.Equal("0", cells[4]);
        Assert.Equal("2", cells[5]);
        Assert.Equal("未知", cells[6]);
        Assert.Equal("0", cells[7]);
        Assert.Equal("2", cells[8]);
        Assert.Equal("未知", cells[9]);
    }

    [Fact]
    public void BuildOrderSummaryCells_known_amounts_render_numbers()
    {
        var cells = DynamicReceiptReconciliationPdfExporter.BuildOrderSummaryCells(
            new DynamicReceiptReconciliationReportOrderSummaryDto(
                1, "甲客户", "USD", 1, 100m, 1, 0, 30m, 1, 0, 70m));

        Assert.Equal("30", cells[6]);
        Assert.Equal("70", cells[9]);
    }

    [Fact]
    public void BuildReceiptSummaryCells_preserves_evidence_status_and_truncation()
    {
        var cells = DynamicReceiptReconciliationPdfExporter.BuildReceiptSummaryCells(
            new DynamicReceiptReconciliationReportReceiptSummaryDto(
                1, "甲客户", "USD", "pending", 3, 20.5m, true));

        Assert.Equal("甲客户", cells[0]);
        Assert.Equal("USD", cells[1]);
        Assert.Equal("pending", cells[2]);
        Assert.Equal("3", cells[3]);
        Assert.Equal("20.5", cells[4]);
        Assert.Equal("是", cells[5]);
    }


    // ==================== 7. null 金额全链路（不崩溃 + 显式未知） ====================

    [Fact]
    public async Task ExportPdf_null_amounts_render_unknown()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "甲客户");
        SeedOrder(db, "SO-NULL", CustomerA, Currency.USD, 100m);
        await db.SaveChangesAsync();

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var page = PreviewOk(await ctl.Preview(new DynamicReceiptReconciliationReportRequest
        {
            SummaryMode = "customerCurrency",
        }));

        var summary = Assert.Single(page.OrderSummaries!);
        Assert.Null(summary.LinkedReceiptAmount);
        Assert.Null(summary.UncoveredAmount);
        Assert.Equal("未知", DynamicReceiptReconciliationPdfExporter.FormatSummaryAmount(summary.LinkedReceiptAmount));

        var file = PdfOk(await ctl.ExportPdf(new DynamicReceiptReconciliationReportRequest
        {
            Fields = new() { "orderNo" },
            ReceiptFields = new() { "receiptNo" },
            SummaryMode = "customerCurrency",
            PageSize = 200,
        }));

        using var pdf = OpenPdf(file.FileContents);
        Assert.True(pdf.Pages.Count >= 4);
    }

    // ==================== 8. pending / historical 收款证据状态显式保留 ====================

    [Fact]
    public async Task ExportPdf_pending_historical_receipts_kept_separate()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "甲客户");
        SeedOrder(db, "SO-EV", CustomerA, Currency.USD, 100m);
        SeedReceipt(db, "SK-ACT", CustomerA, 10m, Currency.USD, DocumentStatus.Approved);
        SeedReceipt(db, "SK-PEND", CustomerA, 20m, Currency.USD, DocumentStatus.Pending);
        SeedReceipt(db, "SK-HIST", CustomerA, 30m, Currency.USD, DocumentStatus.Cancelled);
        await db.SaveChangesAsync();

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var page = PreviewOk(await ctl.Preview(new DynamicReceiptReconciliationReportRequest
        {
            SummaryMode = "customerCurrency",
            ReceiptStatus = "all",
        }));

        Assert.Equal(3, page.ReceiptSummaries!.Count);
        Assert.Contains(page.ReceiptSummaries, s => s.EvidenceStatus == "active" && s.Amount == 10m);
        Assert.Contains(page.ReceiptSummaries, s => s.EvidenceStatus == "pending" && s.Amount == 20m);
        Assert.Contains(page.ReceiptSummaries, s => s.EvidenceStatus == "historical" && s.Amount == 30m);

        var file = PdfOk(await ctl.ExportPdf(new DynamicReceiptReconciliationReportRequest
        {
            Fields = new() { "orderNo" },
            ReceiptFields = new() { "receiptNo" },
            SummaryMode = "customerCurrency",
            ReceiptStatus = "all",
            PageSize = 200,
        }));

        using var pdf = OpenPdf(file.FileContents);
        Assert.True(pdf.Pages.Count >= 4);
    }

    // ==================== 9. 多币种隔离 ====================

    [Fact]
    public async Task ExportPdf_multiple_currencies_kept_separate()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "甲客户");
        SeedCustomer(db, CustomerB, "乙客户");
        SeedOrder(db, "SO-A", CustomerA, Currency.USD, 100m);
        SeedOrder(db, "SO-B", CustomerB, Currency.CNY, 200m);
        SeedReceipt(db, "SK-A", CustomerA, 30m, Currency.USD, DocumentStatus.Approved);
        SeedReceipt(db, "SK-B", CustomerB, 40m, Currency.CNY, DocumentStatus.Approved);
        await db.SaveChangesAsync();

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var page = PreviewOk(await ctl.Preview(new DynamicReceiptReconciliationReportRequest
        {
            SummaryMode = "customerCurrency",
            PageSize = 200,
        }));

        Assert.Equal(2, page.OrderSummaries!.Count);
        Assert.Contains(page.OrderSummaries, s => s.Currency == "USD");
        Assert.Contains(page.OrderSummaries, s => s.Currency == "CNY");

        var file = PdfOk(await ctl.ExportPdf(new DynamicReceiptReconciliationReportRequest
        {
            Fields = new() { "orderNo" },
            ReceiptFields = new() { "receiptNo" },
            SummaryMode = "customerCurrency",
            PageSize = 200,
        }));

        using var pdf = OpenPdf(file.FileContents);
        Assert.True(pdf.Pages.Count >= 4);
    }


    // ==================== 10. 长标签 / 多组分页 / 空页 / 截断 ====================

    [Fact]
    public async Task ExportPdf_long_label_renders_without_failure()
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
            SummaryMode = "customerCurrency",
            PageSize = 200,
        }));

        using var pdf = OpenPdf(file.FileContents);
        Assert.True(pdf.Pages.Count >= 4);
    }

    [Fact]
    public async Task ExportPdf_many_summaries_paginate()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        for (var i = 0; i < 120; i++)
        {
            var id = 968100L + i;
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
            SummaryMode = "customerCurrency",
            PageSize = 200,
        }));

        // 证据两分区 + 订单金额汇总（120 行）都会纵向分页，总页数应明显超过证据两页。
        using var pdf = OpenPdf(file.FileContents);
        Assert.True(pdf.Pages.Count > 4);
    }

    [Fact]
    public async Task ExportPdf_empty_page_renders_visible_empty_notes()
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
            SummaryMode = "customerCurrency",
            PageSize = 200,
        }));

        using var pdf = OpenPdf(file.FileContents);
        Assert.True(pdf.Pages.Count >= 4);
    }

    [Fact]
    public async Task ExportPdf_truncated_receipt_summary_renders_without_failure()
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
            SummaryMode = "customerCurrency",
            PageSize = 200,
        }));

        using var pdf = OpenPdf(file.FileContents);
        Assert.True(pdf.Pages.Count >= 4);
    }


    // ==================== 11. 只读不写库 ====================

    [Fact]
    public async Task ExportPdf_summary_does_not_write_to_database()
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
            SummaryMode = "customerCurrency",
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

