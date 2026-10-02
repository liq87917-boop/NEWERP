using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-208 动态报价成交率报表「客户 Id / 业务员关键字 / 原币币种」筛选的聚焦单元测试。
/// <para>全部使用内存数据库（TestDbFactory），不连接 SQL Server、不启动 API、不执行任何 SQL / seed、不运行浏览器验收。</para>
/// </summary>
public class QuotationConversionFilterTests
{
    private const string QuotationMenuCode = "quotation";

    private static readonly DateTime Start = new(2026, 9, 1);
    private static readonly DateTime End = new(2026, 9, 30);

    private static readonly SalespersonDataScope PrivilegedScope = new() { IsPrivileged = true, AllowedCustomerIds = null };

    // ==================== 脚手架 ====================

    private static Quotation SeedQuotation(
        ErpDbContext db, string no, long? customerId, string salesmanName,
        Currency currency, decimal totalAmount = 1000m, DocumentStatus status = DocumentStatus.Approved)
    {
        var quotation = new Quotation
        {
            QuotationNo = no,
            QuotationDate = Start.AddDays(1),
            CustomerId = customerId,
            CustomerName = no,
            SalesmanName = salesmanName,
            Currency = currency,
            TotalAmount = totalAmount,
            Status = status
        };
        db.Quotations.Add(quotation);
        db.SaveChanges();
        return quotation;
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

    private static SysUser SeedAuthorizedUser(ErpDbContext db, string userName)
    {
        var role = SeedRole(db, $"Priv-{userName}", isSystem: true);
        var user = SeedUser(db, userName);
        SeedUserRole(db, user.Id, role.Id);
        SeedRoleMenu(db, role.Id, SeedMenu(db, QuotationMenuCode).Id);
        return user;
    }

    private static DynamicQuotationConversionReportController BuildController(ErpDbContext db, long? userId)
    {
        var ctl = new DynamicQuotationConversionReportController(db, new ReportService(db));
        TestAuth.SetUser(ctl, userId);
        return ctl;
    }

    private static DynamicQuotationConversionReportPageDto OkPage(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<ApiResponse<DynamicQuotationConversionReportPageDto>>(ok.Value);
        return resp.Data!;
    }

    // ==================== 1. 筛选规范化（纯规则） ====================

    [Fact]
    public void NormalizeFilter_空筛选返回null()
    {
        Assert.Null(DynamicQuotationConversionReportRules.NormalizeFilter(null));
        Assert.Null(DynamicQuotationConversionReportRules.NormalizeFilter(new QuotationConversionFilterDto()));
    }

    [Fact]
    public void NormalizeFilter_客户Id非正拒绝()
    {
        Assert.Throws<BusinessException>(() => DynamicQuotationConversionReportRules.NormalizeFilter(
            new QuotationConversionFilterDto { CustomerId = 0 }));
        Assert.Throws<BusinessException>(() => DynamicQuotationConversionReportRules.NormalizeFilter(
            new QuotationConversionFilterDto { CustomerId = -1 }));
    }

    [Fact]
    public void NormalizeFilter_业务员关键字去首尾空白()
    {
        var normalized = DynamicQuotationConversionReportRules.NormalizeFilter(
            new QuotationConversionFilterDto { SalespersonName = "  张三丰  " });

        Assert.NotNull(normalized);
        Assert.Equal("张三丰", normalized.SalespersonName);
    }

    [Fact]
    public void NormalizeFilter_业务员关键字超过80字符拒绝()
    {
        var ex = Assert.Throws<BusinessException>(() => DynamicQuotationConversionReportRules.NormalizeFilter(
            new QuotationConversionFilterDto { SalespersonName = new string('张', 81) }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public void NormalizeFilter_币种已知枚举码_大小写不敏感归一化()
    {
        var normalized = DynamicQuotationConversionReportRules.NormalizeFilter(
            new QuotationConversionFilterDto { Currency = "usd" });

        Assert.NotNull(normalized);
        Assert.Equal("USD", normalized.Currency);
    }

    [Fact]
    public void NormalizeFilter_币种显式未知桶_绝不回退CNY()
    {
        var viaToken = DynamicQuotationConversionReportRules.NormalizeFilter(
            new QuotationConversionFilterDto { Currency = "unknown" });
        var viaText = DynamicQuotationConversionReportRules.NormalizeFilter(
            new QuotationConversionFilterDto { Currency = "未知币种" });

        Assert.Equal(DynamicQuotationConversionReportRules.UnknownCurrencyFilterToken, viaToken!.Currency);
        Assert.Equal(DynamicQuotationConversionReportRules.UnknownCurrencyFilterToken, viaText!.Currency);
        Assert.NotEqual("CNY", viaToken.Currency);
    }

    [Fact]
    public void NormalizeFilter_币种非法token拒绝()
    {
        var ex = Assert.Throws<BusinessException>(() => DynamicQuotationConversionReportRules.NormalizeFilter(
            new QuotationConversionFilterDto { Currency = "ABC" }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }


    // ==================== 2. 服务端过滤（数据库端、范围后、聚合前） ====================

    [Fact]
    public async Task 服务_客户Id筛选_只返回该客户()
    {
        using var db = TestDbFactory.Create();
        SeedQuotation(db, "QT-1", 1L, "张三", Currency.USD);
        SeedQuotation(db, "QT-2", 2L, "李四", Currency.USD);

        var rows = await new ReportService(db).GetQuotationConversionAsync(Start, End, PrivilegedScope,
            new QuotationConversionFilterDto { CustomerId = 1L });

        var row = Assert.Single(rows);
        Assert.Equal("张三", row.SalesmanName);
        Assert.Equal(1, row.QuotationCount);
    }

    [Fact]
    public async Task 服务_业务员关键字筛选()
    {
        using var db = TestDbFactory.Create();
        SeedQuotation(db, "QT-1", 1L, "张三丰", Currency.USD);
        SeedQuotation(db, "QT-2", 2L, "李四", Currency.USD);

        var rows = await new ReportService(db).GetQuotationConversionAsync(Start, End, PrivilegedScope,
            new QuotationConversionFilterDto { SalespersonName = "张三" });

        var row = Assert.Single(rows);
        Assert.Equal("张三丰", row.SalesmanName);
    }

    [Fact]
    public async Task 服务_币种筛选_已知枚举码()
    {
        using var db = TestDbFactory.Create();
        SeedQuotation(db, "QT-USD", 1L, "张三", Currency.USD);
        SeedQuotation(db, "QT-EUR", 1L, "张三", Currency.EUR);

        var rows = await new ReportService(db).GetQuotationConversionAsync(Start, End, PrivilegedScope,
            new QuotationConversionFilterDto { Currency = "USD" });

        var row = Assert.Single(rows);
        Assert.Equal("USD", row.Currency);
    }

    [Fact]
    public async Task 服务_未知币种筛选_匹配未定义枚举值_且不含CNY()
    {
        using var db = TestDbFactory.Create();
        SeedQuotation(db, "QT-CNY", 1L, "张三", Currency.CNY);
        SeedQuotation(db, "QT-UNDEF", 1L, "张三", (Currency)0);

        var rows = await new ReportService(db).GetQuotationConversionAsync(Start, End, PrivilegedScope,
            new QuotationConversionFilterDto { Currency = "unknown" });

        var row = Assert.Single(rows);
        Assert.Equal(ReportService.UnknownCurrencyGroup, row.Currency);
        Assert.Equal(1, row.QuotationCount);
    }

    [Fact]
    public async Task 服务_筛选在来源上限之前_过滤后不超限()
    {
        using var db = TestDbFactory.Create();
        var list = new List<Quotation>();
        for (var i = 0; i < 2001; i++)
        {
            list.Add(new Quotation
            {
                QuotationNo = $"QT-{i:D4}",
                QuotationDate = new DateTime(2026, 9, 1).AddDays(i % 30),
                CustomerId = i == 0 ? 1L : 2L,
                CustomerName = "客户",
                SalesmanName = "业务员",
                Currency = Currency.USD,
                TotalAmount = 1m,
                Status = DocumentStatus.Approved
            });
        }
        db.Quotations.AddRange(list);
        await db.SaveChangesAsync();

        var rows = await new ReportService(db).GetQuotationConversionAsync(Start, End, PrivilegedScope,
            new QuotationConversionFilterDto { CustomerId = 1L });

        var row = Assert.Single(rows);
        Assert.Equal(1, row.QuotationCount);
    }

    [Fact]
    public async Task 服务_不带筛选参数_保持既有行为兼容()
    {
        using var db = TestDbFactory.Create();
        SeedQuotation(db, "QT-USD", 1L, "张三", Currency.USD);
        SeedQuotation(db, "QT-EUR", 1L, "张三", Currency.EUR);

        var rows = await new ReportService(db).GetQuotationConversionAsync(Start, End, PrivilegedScope);

        Assert.Equal(2, rows.Count);
    }

    [Fact]
    public async Task 服务_受限制业务员范围与筛选叠加_不泄露范围外数据()
    {
        using var db = TestDbFactory.Create();
        SeedQuotation(db, "QT-MINE", 1L, "我的业务员", Currency.USD);
        SeedQuotation(db, "QT-OTHER", 2L, "他人业务员", Currency.USD);

        var restricted = new SalespersonDataScope { IsPrivileged = false, AllowedCustomerIds = new HashSet<long> { 1L } };

        var none = await new ReportService(db).GetQuotationConversionAsync(Start, End, restricted,
            new QuotationConversionFilterDto { CustomerId = 2L });
        Assert.Empty(none);

        var mine = await new ReportService(db).GetQuotationConversionAsync(Start, End, restricted,
            new QuotationConversionFilterDto { CustomerId = 1L });
        var row = Assert.Single(mine);
        Assert.Equal("我的业务员", row.SalesmanName);
    }


    [Fact]
    public async Task 服务_组合筛选_客户与关键字与币种同时生效()
    {
        using var db = TestDbFactory.Create();
        SeedQuotation(db, "QT-1", 1L, "张三丰", Currency.USD);
        SeedQuotation(db, "QT-2", 1L, "张三丰", Currency.EUR);
        SeedQuotation(db, "QT-3", 2L, "张三丰", Currency.USD);
        SeedQuotation(db, "QT-4", 1L, "李四", Currency.USD);

        var rows = await new ReportService(db).GetQuotationConversionAsync(Start, End, PrivilegedScope,
            new QuotationConversionFilterDto
            {
                CustomerId = 1L,
                SalespersonName = "张三",
                Currency = "USD"
            });

        var row = Assert.Single(rows);
        Assert.Equal("张三丰", row.SalesmanName);
        Assert.Equal("USD", row.Currency);
        Assert.Equal(1, row.QuotationCount);
    }

    // ==================== 3. 控制器（校验先于读取 + 导出上下文） ====================

    [Fact]
    public async Task 预览_非法筛选token_校验先于读取并拒绝()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "qcf-invalid");
        var ctl = BuildController(db, user.Id);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Preview(
            new DynamicQuotationConversionReportRequest
            {
                Start = Start,
                End = End,
                Filter = new QuotationConversionFilterDto { Currency = "ABC" }
            }));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public async Task 预览_应用筛选_结果与筛选上下文一致()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "qcf-preview");
        SeedQuotation(db, "QT-1", 1L, "张三", Currency.USD);
        SeedQuotation(db, "QT-2", 2L, "李四", Currency.USD);

        var ctl = BuildController(db, user.Id);
        var page = OkPage(await ctl.Preview(new DynamicQuotationConversionReportRequest
        {
            Fields = new List<string> { "salesmanName" },
            Start = Start,
            End = End,
            Filter = new QuotationConversionFilterDto { CustomerId = 1L }
        }));

        var row = Assert.Single(page.Rows);
        Assert.Equal("张三", row["salesmanName"]);
        Assert.Contains("客户 Id 1", page.FilterText);
    }

}
