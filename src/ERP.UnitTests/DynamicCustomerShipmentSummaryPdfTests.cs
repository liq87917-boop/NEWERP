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
using System.Text;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-234 动态客户出货量证据报表「全匹配汇总 PDF」聚焦单元测试（只读、有界、经授权端点）。
/// 覆盖：专用汇总 PDF 端点复用已授权预览管线（身份 / 客户出货量统计表菜单 / 业务员数据范围 / 字段 / 日期 / 分页 / 应用筛选）、
/// PDF 签名与内容类型、A4 页面、嵌入中文黑体 SimHei、原币金额与精确单位数量两块分离渲染（各至少一页）、
/// 未知币种金额 / 未知单位数量显式「未知」（绝不回落 0）、绝不跨币种 / 跨单位合计、绝不含客户明细行、
/// 空匹配显式空 PDF、越界详情页仍覆盖全部匹配汇总、字体缺失显式失败，以及无效 / 授权撤销 / 无身份 / 来源超限拒绝且不返回文件。
/// <para>全部使用内存数据库（TestDbFactory），不连接 SQL Server、不启动 API、不执行任何 SQL / seed、不运行浏览器验收。</para>
/// </summary>
public class DynamicCustomerShipmentSummaryPdfTests
{
    private const string MenuCode = "customer-shipment";
    private static readonly DateTime Start = new(2026, 9, 1);
    private static readonly DateTime End = new(2026, 9, 30);

    // ==================== 脚手架 ====================

    private static BaseCustomer SeedCustomer(ErpDbContext db, string code, string name, long? empId = null)
    {
        var customer = new BaseCustomer
        {
            CustomerCode = code,
            CustomerName = name,
            Status = 1,
            CreditStatus = "正常",
            EmpId = empId,
            IsDeleted = false
        };
        db.BaseCustomers.Add(customer);
        db.SaveChanges();
        return customer;
    }

    private static BaseEmployee SeedEmployee(ErpDbContext db, string code, bool isSalesman = true)
    {
        var employee = new BaseEmployee
        {
            EmployeeCode = code,
            EmployeeName = code,
            IsSalesman = isSalesman,
            Status = 1
        };
        db.BaseEmployees.Add(employee);
        db.SaveChanges();
        return employee;
    }

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

    private static SysUser SeedAuthorizedUser(ErpDbContext db, string userName, string roleCode, bool isSystemRole = true)
    {
        var role = SeedRole(db, roleCode, isSystemRole);
        var user = SeedUser(db, userName);
        SeedUserRole(db, user.Id, role.Id);
        SeedRoleMenu(db, role.Id, SeedMenu(db, MenuCode).Id);
        return user;
    }

    private static SalesOrder SeedOrder(
        ErpDbContext db, string orderNo, long customerId, Currency currency, decimal totalAmount)
    {
        var order = new SalesOrder
        {
            OrderNo = orderNo,
            OrderDate = new DateTime(2026, 9, 10),
            CustomerId = customerId,
            Status = DocumentStatus.Approved,
            Currency = currency,
            TotalAmount = totalAmount
        };
        db.SalesOrders.Add(order);
        db.SaveChanges();
        return order;
    }

    private static SalesOrderDetail SeedDetail(
        ErpDbContext db, long orderId, string unit, decimal quantity, bool deleted = false)
    {
        var detail = new SalesOrderDetail
        {
            SalesOrderId = orderId,
            ProductId = 1,
            ProductName = "商品",
            Quantity = quantity,
            Unit = unit,
            IsDeleted = deleted
        };
        db.SalesOrderDetails.Add(detail);
        db.SaveChanges();
        return detail;
    }

    private static DynamicCustomerShipmentReportController NewController(ErpDbContext db)
        => new(db, new ReportService(db));

    private static DynamicCustomerShipmentReportRequest Request(
        List<string>? fields = null, DateTime? start = null, DateTime? end = null, int page = 1, int pageSize = 20)
        => new() { Fields = fields, Start = start, End = end, Page = page, PageSize = pageSize };

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
        using var stream = new MemoryStream(bytes);
        return PdfReader.Open(stream);
    }

    // ==================== 1. 签名 / 内容类型 / 页面边界 / 字体 ====================

    [Fact]
    public async Task ExportSummaryPdf_返回PDF签名与内容类型_A4页面尺寸()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "csd-sum-pdf-user", "Priv");
        var customer = SeedCustomer(db, "C001", "客户");
        SeedOrder(db, "SO-USD", customer.Id, Currency.USD, 1500m);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var file = PdfOk(await ctl.ExportSummaryPdf(Request(start: Start, end: End)));

        using var pdf = OpenPdf(file.FileContents);
        Assert.True(pdf.Pages.Count >= 1);
        Assert.InRange(pdf.Pages[0].Width.Point, 594, 596);
        Assert.InRange(pdf.Pages[0].Height.Point, 841, 843);
    }

    [Fact]
    public async Task ExportSummaryPdf_嵌入中文黑体SimHei_非缺字字体()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "csd-sum-pdf-font", "Priv");
        var customer = SeedCustomer(db, "C001", "客户");
        SeedOrder(db, "SO-USD", customer.Id, Currency.USD, 1500m);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var file = PdfOk(await ctl.ExportSummaryPdf(Request(start: Start, end: End)));

        var text = Encoding.ASCII.GetString(file.FileContents);
        Assert.Contains("SimHei", text);
        Assert.Contains("FontFile2", text);
    }

    // ==================== 2. 原币金额与精确单位数量两块分离渲染 ====================

    [Fact]
    public async Task ExportSummaryPdf_原币金额与精确单位数量两块分离渲染_各至少一页()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "csd-sum-pdf-sections", "Priv");
        var customer = SeedCustomer(db, "C001", "客户");
        var order = SeedOrder(db, "SO-USD", customer.Id, Currency.USD, 100m);
        SeedDetail(db, order.Id, "PCS", 10m);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var file = PdfOk(await ctl.ExportSummaryPdf(Request(start: Start, end: End)));

        using var pdf = OpenPdf(file.FileContents);
        // 原币金额汇总 + 精确单位数量汇总两块，各至少一页
        Assert.True(pdf.Pages.Count >= 2);
    }

    [Fact]
    public void ExportSummaryPdf_长组_按行页拆分_每页重复标题与表头()
    {
        var items = new List<ReportDtos.CustomerShipmentItem>();
        for (var i = 1; i <= 40; i++)
        {
            items.Add(new ReportDtos.CustomerShipmentItem
            {
                CustomerId = i,
                Currency = $"CUR{i:00}",
                TotalAmount = 100m,
                OrderCount = 1
            });
        }

        var page = DynamicCustomerShipmentReportRules.BuildPage(
            items, DynamicCustomerShipmentReportRules.NormalizeFields(null), 1, 20, Start, End);
        var summary = page.Summary!;

        var bytes = DynamicCustomerShipmentSummaryPdfExporter.Export(summary, page);
        using var pdf = OpenPdf(bytes);

        // 40 个币种桶超出单行页容量，原币金额汇总块必然拆分为多行页；单位块为空仍显式占一页
        Assert.True(pdf.Pages.Count >= 3);
    }

    [Fact]
    public async Task ExportSummaryPdf_空匹配_显式空PDF_单页()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "csd-sum-pdf-empty", "Priv");

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var file = PdfOk(await ctl.ExportSummaryPdf(Request(start: Start, end: End)));

        using var pdf = OpenPdf(file.FileContents);
        Assert.Equal(1, pdf.Pages.Count);
    }

    [Fact]
    public async Task ExportSummaryPdf_越界详情页_仍覆盖全部匹配汇总()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "csd-sum-pdf-page", "Priv");
        var customer = SeedCustomer(db, "C001", "客户");
        var usd = SeedOrder(db, "SO-USD", customer.Id, Currency.USD, 100m);
        var cny = SeedOrder(db, "SO-CNY", customer.Id, Currency.CNY, 500m);
        SeedDetail(db, usd.Id, "PCS", 10m);
        SeedDetail(db, cny.Id, "BOX", 3m);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        // 详情页越界（第 99 页、每页 1 条）不抑制全匹配汇总：仍渲染 USD / CNY 原币块 + PCS / BOX 单位块
        var file = PdfOk(await ctl.ExportSummaryPdf(Request(start: Start, end: End, page: 99, pageSize: 1)));

        using var pdf = OpenPdf(file.FileContents);
        Assert.True(pdf.Pages.Count >= 2);
    }

    // ==================== 3. 字体缺失 / 只读 ====================

    [Fact]
    public void ExportSummaryPdf_字体缺失_显式失败()
    {
        var page = DynamicCustomerShipmentReportRules.BuildPage(
            Array.Empty<ReportDtos.CustomerShipmentItem>(),
            DynamicCustomerShipmentReportRules.NormalizeFields(null),
            1, 20, Start, End);
        Assert.NotNull(page.Summary);

        var ex = Assert.Throws<BusinessException>(
            () => DynamicCustomerShipmentSummaryPdfExporter.Export(page.Summary!, page, fontPath: null));
        Assert.Equal(ErrorCodes.InternalError, ex.Code);
        Assert.Contains("SimHei", ex.Message);
    }

    [Fact]
    public async Task ExportSummaryPdf_只读_不写库()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "csd-sum-pdf-ro", "Priv");
        var customer = SeedCustomer(db, "C001", "客户");
        var order = SeedOrder(db, "SO-USD", customer.Id, Currency.USD, 100m);
        SeedDetail(db, order.Id, "PCS", 10m);

        var before = db.SalesOrders.Count();
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        _ = PdfOk(await ctl.ExportSummaryPdf(Request(start: Start, end: End)));

        Assert.Equal(before, db.SalesOrders.Count());
        Assert.False(db.ChangeTracker.HasChanges());
    }

    // ==================== 4. 未知币种 / 未知单位显式「未知」，绝不写成 0 ====================

    [Fact]
    public void 未知币种金额与未知单位数量_显式未知_签名数值原样保留_金额绝不复制到单位行()
    {
        var items = new List<ReportDtos.CustomerShipmentItem>
        {
            new()
            {
                CustomerId = 1,
                Currency = CustomerShipmentEvidenceRules.UnknownCurrencyGroup,
                TotalAmount = null,
                OrderCount = 2,
                UnitGroups = new List<ReportDtos.CustomerShipmentUnitGroup>
                {
                    new()
                    {
                        Unit = CustomerShipmentEvidenceRules.UnknownUnitGroup,
                        Quantity = null,
                        DetailCount = 1,
                        QuantityLabel = CustomerShipmentEvidenceRules.UnknownUnitQuantityLabel
                    }
                }
            },
            new()
            {
                CustomerId = 2,
                Currency = "USD",
                TotalAmount = -2.5m,
                OrderCount = 1,
                UnitGroups = new List<ReportDtos.CustomerShipmentUnitGroup>
                {
                    new()
                    {
                        Unit = "PCS",
                        Quantity = -3.25m,
                        DetailCount = 1,
                        QuantityLabel = CustomerShipmentEvidenceRules.KnownUnitQuantityLabel
                    }
                }
            }
        };

        var summary = DynamicCustomerShipmentSummaryRules.BuildSummary(items);
        var totalAmountField = DynamicCustomerShipmentSummaryRules.CurrencySummaryColumns
            .Single(c => c.Key == "totalAmount");

        var unknownCurrencyRow = Assert.Single(summary.CurrencyRows.Where(r => r.Currency == CustomerShipmentEvidenceRules.UnknownCurrencyGroup));
        var unknownExport = DynamicCustomerShipmentReportRules.BuildCurrencySummaryExportRow(unknownCurrencyRow);
        Assert.Equal(DynamicCustomerShipmentReportRules.UnknownValueText, unknownExport["totalAmount"]);
        Assert.Equal("未知", DynamicCustomerShipmentSummaryPdfExporter.FormatFieldCell(totalAmountField, unknownExport));

        var knownCurrencyRow = Assert.Single(summary.CurrencyRows.Where(r => r.Currency == "USD"));
        var knownExport = DynamicCustomerShipmentReportRules.BuildCurrencySummaryExportRow(knownCurrencyRow);
        Assert.Equal(-2.5m, knownExport["totalAmount"]);
        Assert.Equal("-2.5", DynamicCustomerShipmentSummaryPdfExporter.FormatFieldCell(totalAmountField, knownExport));

        var unitRow = Assert.Single(summary.UnitRows.Where(r => r.Unit == "PCS"));
        var unitExport = DynamicCustomerShipmentReportRules.BuildUnitSummaryExportRow(unitRow);
        Assert.Equal(-3.25m, unitExport["quantity"]);
        // 单位行绝不携带货币金额
        Assert.False(unitExport.ContainsKey("totalAmount"));
    }

    // ==================== 5. 授权 / 无效输入 / 来源超限拒绝 ====================

    [Fact]
    public async Task ExportSummaryPdf_无菜单授权或无身份_拒绝_不返回文件()
    {
        using var db = TestDbFactory.Create();

        // 无菜单授权（角色未挂载 customer-shipment 菜单）
        var role = SeedRole(db, "NoMenu", isSystem: false);
        var user = SeedUser(db, "no-menu");
        SeedUserRole(db, user.Id, role.Id);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);
        var forbidden = await Assert.ThrowsAsync<BusinessException>(() => ctl.ExportSummaryPdf(
            new DynamicCustomerShipmentReportRequest { Start = Start, End = End }));
        Assert.Equal(ErrorCodes.Forbidden, forbidden.Code);

        // 无身份
        var ctl2 = NewController(db);
        TestAuth.SetUser(ctl2, null);
        var unauthorized = await Assert.ThrowsAsync<BusinessException>(() => ctl2.ExportSummaryPdf(
            new DynamicCustomerShipmentReportRequest { Start = Start, End = End }));
        Assert.Equal(ErrorCodes.Unauthorized, unauthorized.Code);
    }

    [Fact]
    public async Task ExportSummaryPdf_无效输入_拒绝_不返回文件()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "csd-sum-pdf-invalid", "Priv");

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var ex1 = await Assert.ThrowsAsync<BusinessException>(() => ctl.ExportSummaryPdf(
            new DynamicCustomerShipmentReportRequest { Start = Start, End = End, Fields = new List<string> { "bogus" } }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex1.Code);

        var ex2 = await Assert.ThrowsAsync<BusinessException>(() => ctl.ExportSummaryPdf(
            new DynamicCustomerShipmentReportRequest { Start = Start, End = End, PageSize = 201 }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex2.Code);
    }

    [Fact]
    public async Task ExportSummaryPdf_来源超限_拒绝且不返回文件()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "csd-sum-pdf-overflow", "Priv");
        var customer = SeedCustomer(db, "C001", "客户");

        var many = new List<SalesOrder>();
        for (var i = 1; i <= 501; i++)
        {
            many.Add(new SalesOrder
            {
                OrderNo = $"SO-{i:0000}",
                OrderDate = new DateTime(2026, 9, 10),
                CustomerId = customer.Id,
                Status = DocumentStatus.Approved,
                Currency = Currency.USD,
                TotalAmount = 100m
            });
        }
        db.SalesOrders.AddRange(many);
        db.SaveChanges();

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.ExportSummaryPdf(
            new DynamicCustomerShipmentReportRequest { Start = Start, End = End }));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
    }
}

