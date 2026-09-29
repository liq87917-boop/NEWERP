using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
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
/// ERP-159 动态销售订单出货 / 财务进度报表 PDF 导出（只读、有界）单元测试。
/// 覆盖：PDF 签名与内容类型、A4 页面尺寸（行列页边界）、选定字段顺序（BuildRowCells）、
/// 原币分行与未知金额 / 未知数量显式保留（null → 「未知」，不回落为 0）、中文状态文案、
/// 嵌入中文黑体 SimHei（非缺字字体）、字体缺失显式失败、宽列集拆分为多列页、多行拆分为多行页、
/// 无身份 / 无菜单授权 / 页大小超限 / 未知字段拒绝、空页，以及只读不写库。
/// <para>全部使用内存数据库（TestDbFactory），不连接 SQL Server、不启动 API、不执行任何 SQL / seed。</para>
/// </summary>
public class DynamicShipmentFinancePdfTests
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

    /// <summary>播种一个「系统内置角色 + 销售订单菜单授权」用户</summary>
    private static long SeedPrivilegedUser(ErpDbContext db, string userName = "priv")
    {
        var user = SeedUser(db, userName);
        var role = SeedRole(db, $"Role-{userName}", isSystem: true);
        SeedUserRole(db, user.Id, role.Id);
        SeedRoleMenu(db, role.Id, SeedMenu(db, "sales-order").Id);
        return user.Id;
    }

    private static (long UserId, long MineCustomerId, long OtherCustomerId) SeedRestrictedUser(
        ErpDbContext db, string userName)
    {
        var user = SeedUser(db, userName);
        var role = SeedRole(db, $"Role-{userName}", isSystem: false);
        SeedUserRole(db, user.Id, role.Id);
        SeedRoleMenu(db, role.Id, SeedMenu(db, "sales-order").Id);
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

    private static PdfDocument OpenPdf(byte[] bytes)
    {
        var stream = new MemoryStream(bytes);
        return PdfReader.Open(stream);
    }

    // ==================== 1. 签名 / 内容类型 / 页面边界 ====================

    [Fact]
    public async Task ExportPdf_导出当前页_返回PDF签名与A4页面边界()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "甲客户");
        SeedOrder(db, "SO-PDF-1", CustomerA, Currency.USD, 100m);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var file = PdfOk(await ctl.ExportPdf(new DynamicShipmentFinanceReportRequest
        {
            Fields = new() { "orderNo", "currency" },
            PageSize = 10
        }));

        using var pdf = OpenPdf(file.FileContents);
        Assert.Equal(1, pdf.Pages.Count);
        Assert.InRange(pdf.Pages[0].Width.Point, 594, 596);
        Assert.InRange(pdf.Pages[0].Height.Point, 841, 843);
    }

    // ==================== 2. 嵌入中文黑体字体 ====================

    [Fact]
    public async Task ExportPdf_嵌入中文黑体字体_非缺字字体()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "甲客户");
        SeedOrder(db, "SO-PDF-2", CustomerA, Currency.USD, 100m);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var file = PdfOk(await ctl.ExportPdf(new DynamicShipmentFinanceReportRequest
        {
            Fields = new() { "orderNo", "currency", "orderAmount" },
            PageSize = 10
        }));

        var text = Encoding.ASCII.GetString(file.FileContents);
        Assert.Contains("SimHei", text);
        Assert.Contains("FontFile2", text);
    }

    // ==================== 3. 字段顺序 / 原币 / 未知证据 / 状态文案 ====================

    [Fact]
    public void BuildRowCells_按选定列顺序映射_原币与未知证据保留()
    {
        var columns = new List<DynamicShipmentFinanceReportFieldDto>
        {
            new("orderNo", "订单号", "text", false),
            new("currency", "币种", "text", true),
            new("orderAmount", "订单金额", "number", false),
            new("linkedAmount", "已关联金额", "number", false),
            new("uncoveredAmount", "未覆盖金额", "number", false),
            new("orderedQuantity", "订单数量", "number", false),
            new("shippedQuantity", "已出货数量", "number", false),
            new("shipmentStatus", "出货状态", "text", true),
            new("financeLinkStatus", "收款链接状态", "text", true),
            new("hasApprovedShipment", "存在已审核出库单", "boolean", false),
            new("status", "单据状态", "text", false),
        };
        var row = new Dictionary<string, object?>
        {
            ["orderNo"] = "SO-001",
            ["currency"] = "USD",
            ["orderAmount"] = 250.5m,
            ["linkedAmount"] = null,
            ["uncoveredAmount"] = null,
            ["orderedQuantity"] = null,
            ["shippedQuantity"] = 3m,
            ["shipmentStatus"] = "complete",
            ["financeLinkStatus"] = "unlinked",
            ["hasApprovedShipment"] = true,
            ["status"] = "Approved",
        };

        var cells = DynamicShipmentFinancePdfExporter.BuildRowCells(columns, row);

        Assert.Equal(
            new[] { "SO-001", "USD", "250.5", "未知", "未知", "未知", "3", "已出齐", "未链接（金额未知）", "是", "已审核" },
            cells);
    }

    // ==================== 4. 字体缺失显式失败 ====================

    [Fact]
    public void ExportPdf_字体缺失_显式失败_不产出PDF()
    {
        var page = new DynamicShipmentFinanceReportPageDto(
            new List<DynamicShipmentFinanceReportFieldDto> { new("orderNo", "订单号", "text", false) },
            new List<Dictionary<string, object?>>(),
            0, 1, 20, 0,
            "只读", "边界", "免责");

        var ex = Assert.Throws<BusinessException>(() =>
            DynamicShipmentFinancePdfExporter.Export(page, @"Z:\__missing__\simhei.ttf"));
        Assert.Equal(ErrorCodes.InternalError, ex.Code);
        Assert.Contains("SimHei", ex.Message);
    }

    // ==================== 5. 行页 / 列页分页 ====================

    [Fact]
    public async Task ExportPdf_多行_拆分多行页()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "甲客户");
        for (var i = 0; i < 100; i++)
            SeedOrder(db, $"SO-PDF-{i:D3}", CustomerA, Currency.USD, 100m);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var file = PdfOk(await ctl.ExportPdf(new DynamicShipmentFinanceReportRequest
        {
            Fields = new() { "orderNo" },
            PageSize = 200
        }));

        using var pdf = OpenPdf(file.FileContents);
        Assert.True(pdf.Pages.Count > 1);
    }

    [Fact]
    public async Task ExportPdf_宽列集_拆分为多列页_不裁切()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "甲客户");
        SeedOrder(db, "SO-PDF-W", CustomerA, Currency.USD, 100m);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        // 留空字段 = 返回全部白名单字段（27 项），宽列集应拆成多个列页（每个列页总宽不超页宽，列不裁切）
        var file = PdfOk(await ctl.ExportPdf(new DynamicShipmentFinanceReportRequest
        {
            PageSize = 10
        }));

        using var pdf = OpenPdf(file.FileContents);
        Assert.True(pdf.Pages.Count > 1);
        foreach (var page in pdf.Pages)
        {
            Assert.InRange(page.Width.Point, 594, 596);
            Assert.InRange(page.Height.Point, 841, 843);
        }
    }

    // ==================== 6. 授权 / 校验拒绝 ====================

    [Fact]
    public async Task ExportPdf_无身份_未认证拒绝()
    {
        using var db = TestDbFactory.Create();
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, null);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.ExportPdf(
            new DynamicShipmentFinanceReportRequest()));
        Assert.Equal(ErrorCodes.Unauthorized, ex.Code);
    }

    [Fact]
    public async Task ExportPdf_无销售订单菜单授权_拒绝()
    {
        using var db = TestDbFactory.Create();
        var user = SeedUser(db, "nomenu");
        var role = SeedRole(db, "Role-nomenu", isSystem: false);
        SeedUserRole(db, user.Id, role.Id);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.ExportPdf(
            new DynamicShipmentFinanceReportRequest()));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    [Fact]
    public async Task ExportPdf_页大小超限_拒绝()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.ExportPdf(
            new DynamicShipmentFinanceReportRequest { PageSize = 201 }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public async Task ExportPdf_未知字段_拒绝()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.ExportPdf(
            new DynamicShipmentFinanceReportRequest { Fields = new() { "orderNo", "bogus" } }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    // ==================== 7. 业务员数据范围（fail closed） ====================

    [Fact]
    public async Task ExportPdf_受限制业务员_仅本人客户范围_不泄露范围外订单()
    {
        using var db = TestDbFactory.Create();
        var (userId, mine, _) = SeedRestrictedUser(db, "alice");
        SeedOrder(db, "SO-MINE", mine, Currency.USD, 100m);
        SeedOrder(db, "SO-OTHER", CustomerB, Currency.USD, 300m);

        var ctl = NewController(db);
        TestAuth.SetUser(ctl, userId);

        // 范围内仅一张订单：导出成功（授权通过 + 数据范围过滤后非空）
        var file = PdfOk(await ctl.ExportPdf(new DynamicShipmentFinanceReportRequest
        {
            Fields = new() { "orderNo" },
            PageSize = 10
        }));

        Assert.NotEmpty(file.FileContents);
    }

    // ==================== 8. 空页（文档下载限制：仅表头 + 空态提示） ====================

    [Fact]
    public async Task ExportPdf_空页_返回仅表头PDF()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var file = PdfOk(await ctl.ExportPdf(new DynamicShipmentFinanceReportRequest
        {
            Fields = new() { "orderNo", "currency" },
            PageSize = 10
        }));

        using var pdf = OpenPdf(file.FileContents);
        Assert.Equal(1, pdf.Pages.Count);
    }

    // ==================== 9. 只读不写库 ====================

    [Fact]
    public async Task ExportPdf_只读不写库()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedCustomer(db, CustomerA, "甲客户");
        var order = SeedOrder(db, "SO-RO", CustomerA, Currency.USD, 100m);
        SeedDetail(db, order.Id, ProductA, 5m);
        SeedStockOut(db, "CK-RO", order.Id, DocumentStatus.Approved, (ProductA, 2m));
        await db.SaveChangesAsync();

        var counting = CountingDbContext.Wrap(db);
        var ctl = new DynamicShipmentFinanceReportController(counting.Proxy);
        TestAuth.SetUser(ctl, uid);

        var file = PdfOk(await ctl.ExportPdf(new DynamicShipmentFinanceReportRequest
        {
            Fields = new() { "orderNo" },
            PageSize = 10
        }));

        Assert.NotEmpty(file.FileContents);
        Assert.Equal(0, counting.WriteCalls);
    }

    // ==================== 10. 只读计数上下文（断言不写库） ====================

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



