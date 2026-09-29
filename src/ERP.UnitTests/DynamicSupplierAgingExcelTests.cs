using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using NPOI.SS.UserModel;
using NPOI.XSSF.UserModel;
using System.Reflection;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-142 动态供应商对账与账龄报表 Excel 导出（只读、有界、作用域化）单元测试。
/// 覆盖：选定列顺序与行值、仅导出当前页（页大小上限）、未知到期日保留为空单元格（绝不回落为 0）、
/// 不同币种分别成行（不合并 / 不换算）、公式前导文本转义、空页仅表头、无菜单授权 / 无身份 fail closed、
/// 页大小超限拒绝、以及只读不写库。
/// <para>全部使用内存数据库（TestDbFactory），不连接 SQL Server、不启动 API、不执行任何 SQL / seed。</para>
/// </summary>
public class DynamicSupplierAgingExcelTests
{
    private static readonly DateTime AsOf = new(2026, 9, 24);
    private const long SupplierA = 968001L;
    private const long SupplierB = 968002L;

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

    private static FileContentResult ExportOk(IActionResult result)
    {
        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", file.ContentType);
        Assert.EndsWith(".xlsx", file.FileDownloadName);
        return file;
    }

    private static XSSFWorkbook OpenWorkbook(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        return new XSSFWorkbook(ms);
    }

    [Fact]
    public async Task Export_当前页_列顺序与行值符合选定字段()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedSupplier(db, SupplierA, "甲供应商");
        SeedSupplier(db, SupplierB, "乙供应商");
        SeedInvoice(db, 968101L, SupplierA, "CNY", "A001", 100m, 0m, 100m,
            PurchaseInvoiceRules.StatusRecorded, AsOf.AddDays(-40), dueDate: AsOf.AddDays(5));
        SeedInvoice(db, 968102L, SupplierB, "USD", "A002", 200m, 0m, 200m,
            PurchaseInvoiceRules.StatusRecorded, AsOf.AddDays(-40), dueDate: AsOf.AddDays(6));
        await db.SaveChangesAsync();
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var file = ExportOk(await ctl.Export(new DynamicSupplierAgingReportRequest
        {
            Fields = new() { "invoiceNumber", "currency", "grossAmount" },
            Page = 1,
            PageSize = 10
        }));

        var sheet = OpenWorkbook(file.FileContents).GetSheetAt(0);
        Assert.Equal("发票号码", sheet.GetRow(0).GetCell(0).StringCellValue);
        Assert.Equal("币种", sheet.GetRow(0).GetCell(1).StringCellValue);
        Assert.Equal("含税总额", sheet.GetRow(0).GetCell(2).StringCellValue);
        Assert.Equal(2, sheet.LastRowNum);

        var rows = new Dictionary<string, IRow>();
        for (var r = 1; r <= sheet.LastRowNum; r++)
            rows[sheet.GetRow(r).GetCell(0).StringCellValue] = sheet.GetRow(r);

        Assert.Equal("CNY", rows["A001"].GetCell(1).StringCellValue);
        Assert.Equal(100d, rows["A001"].GetCell(2).NumericCellValue);
        Assert.Equal("USD", rows["A002"].GetCell(1).StringCellValue);
        Assert.Equal(200d, rows["A002"].GetCell(2).NumericCellValue);
    }

    [Fact]
    public async Task Export_仅导出当前页_受页大小上限约束()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedSupplier(db, SupplierA, "甲供应商");
        SeedInvoice(db, 968201L, SupplierA, "CNY", "B001", 100m, 0m, 100m,
            PurchaseInvoiceRules.StatusRecorded, AsOf.AddDays(-40), dueDate: AsOf.AddDays(5));
        SeedInvoice(db, 968202L, SupplierA, "CNY", "B002", 200m, 0m, 200m,
            PurchaseInvoiceRules.StatusRecorded, AsOf.AddDays(-40), dueDate: AsOf.AddDays(6));
        SeedInvoice(db, 968203L, SupplierA, "CNY", "B003", 300m, 0m, 300m,
            PurchaseInvoiceRules.StatusRecorded, AsOf.AddDays(-40), dueDate: AsOf.AddDays(7));
        await db.SaveChangesAsync();
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var file = ExportOk(await ctl.Export(new DynamicSupplierAgingReportRequest
        {
            Fields = new() { "invoiceNumber" },
            Page = 1,
            PageSize = 2
        }));

        var sheet = OpenWorkbook(file.FileContents).GetSheetAt(0);
        Assert.Equal(2, sheet.LastRowNum);
    }

    [Fact]
    public async Task Export_未知到期日保留为空单元格()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedSupplier(db, SupplierA, "甲供应商");
        SeedInvoice(db, 968301L, SupplierA, "CNY", "C001", 100m, 0m, 100m,
            PurchaseInvoiceRules.StatusRecorded, AsOf.AddDays(-40));
        await db.SaveChangesAsync();
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var file = ExportOk(await ctl.Export(new DynamicSupplierAgingReportRequest
        {
            Fields = new() { "invoiceNumber", "dueDate", "dueDateKnown", "agingBucket" },
            PageSize = 10
        }));

        var sheet = OpenWorkbook(file.FileContents).GetSheetAt(0);
        var row = sheet.GetRow(1);
        Assert.Equal("C001", row.GetCell(0).StringCellValue);
        Assert.Equal(string.Empty, row.GetCell(1).StringCellValue);
        Assert.Equal("否", row.GetCell(2).StringCellValue);
        Assert.Equal(string.Empty, row.GetCell(3).StringCellValue);
    }

    [Fact]
    public async Task Export_公式前导文本_转义为字面文本()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedSupplier(db, SupplierA, "甲供应商");
        SeedInvoice(db, 968401L, SupplierA, "CNY", "=1+1", 100m, 0m, 100m,
            PurchaseInvoiceRules.StatusRecorded, AsOf.AddDays(-40), dueDate: AsOf.AddDays(5));
        await db.SaveChangesAsync();
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var file = ExportOk(await ctl.Export(new DynamicSupplierAgingReportRequest
        {
            Fields = new() { "invoiceNumber" },
            PageSize = 10
        }));

        var sheet = OpenWorkbook(file.FileContents).GetSheetAt(0);
        var cell = sheet.GetRow(1).GetCell(0);
        Assert.NotEqual(CellType.Formula, cell.CellType);
        Assert.Equal(CellType.String, cell.CellType);
        Assert.Equal("'=1+1", cell.StringCellValue);
    }

    [Theory]
    [InlineData("=1+1", "'=1+1")]
    [InlineData("+cmd", "'+cmd")]
    [InlineData("-cmd", "'-cmd")]
    [InlineData("@cmd", "'@cmd")]
    [InlineData("abc", "abc")]
    [InlineData(null, null)]
    public void EscapeFormulaLeading_危险字符转义_普通值原样(string? input, string? expected)
    {
        Assert.Equal(expected, DynamicSupplierAgingReportRules.EscapeFormulaLeading(input));
    }

    [Theory]
    [InlineData("=1", true)]
    [InlineData("+1", true)]
    [InlineData("-1", true)]
    [InlineData("@x", true)]
    [InlineData("abc", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsFormulaLeading_识别公式首字符(string? value, bool expected)
    {
        Assert.Equal(expected, DynamicSupplierAgingReportRules.IsFormulaLeading(value));
    }

    [Fact]
    public async Task Export_空页_返回仅表头工作簿()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var file = ExportOk(await ctl.Export(new DynamicSupplierAgingReportRequest
        {
            Fields = new() { "invoiceNumber" },
            PageSize = 10
        }));

        var sheet = OpenWorkbook(file.FileContents).GetSheetAt(0);
        Assert.Equal("发票号码", sheet.GetRow(0).GetCell(0).StringCellValue);
        Assert.Equal(0, sheet.LastRowNum);
    }

    [Fact]
    public async Task Export_无菜单授权_拒绝()
    {
        using var db = TestDbFactory.Create();
        var user = SeedUser(db, "nomenu");
        var role = SeedRole(db, "Role-nomenu", isSystem: false);
        SeedUserRole(db, user.Id, role.Id);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, user.Id);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Export(new DynamicSupplierAgingReportRequest()));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    [Fact]
    public async Task Export_无身份_拒绝()
    {
        using var db = TestDbFactory.Create();
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, null);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Export(new DynamicSupplierAgingReportRequest()));
        Assert.Equal(ErrorCodes.Unauthorized, ex.Code);
    }

    [Fact]
    public async Task Export_页大小超限_拒绝()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        var ctl = NewController(db);
        TestAuth.SetUser(ctl, uid);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Export(new DynamicSupplierAgingReportRequest { PageSize = 201 }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public async Task Export_只读不写库()
    {
        using var db = TestDbFactory.Create();
        var uid = SeedPrivilegedUser(db);
        SeedSupplier(db, SupplierA, "甲供应商");
        SeedInvoice(db, 968501L, SupplierA, "CNY", "D001", 100m, 0m, 100m,
            PurchaseInvoiceRules.StatusRecorded, AsOf.AddDays(-40), dueDate: AsOf.AddDays(5));
        await db.SaveChangesAsync();

        var counting = CountingDbContext.Wrap(db);
        var ctl = new DynamicSupplierAgingReportController(counting.Proxy);
        TestAuth.SetUser(ctl, uid);

        var file = ExportOk(await ctl.Export(new DynamicSupplierAgingReportRequest
        {
            Fields = new() { "invoiceNumber" },
            PageSize = 10
        }));

        Assert.NotEmpty(file.FileContents);
        Assert.Equal(0, counting.WriteCalls);
    }

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
