using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-205 报价成交率按原币分桶的单元测试：同一业务员多币种分别成桶、币种规范化（去空白 / 大写 /
/// 未知取值归入「未知币种」）、零金额与空转换、确定性排序，以及经授权端点暴露币种字段。
/// <para>全部使用内存数据库（TestDbFactory），不连接 SQL Server、不启动 API、不执行任何 SQL / seed、不运行浏览器验收。</para>
/// </summary>
public class QuotationConversionCurrencyTests
{
    private static readonly DateTime Start = new(2026, 9, 1);
    private static readonly DateTime End = new(2026, 9, 30);

    /// <summary>成交率口径测试使用的特权数据范围（不过滤客户），只用于验证币种分桶口径本身。</summary>
    private static readonly SalespersonDataScope PrivilegedScope = new() { IsPrivileged = true, AllowedCustomerIds = null };

    // ==================== 服务层：币种分桶 ====================

    [Fact]
    public async Task 同一业务员多币种_分别成桶_金额按原币分列且不跨币种合计()
    {
        using var db = TestDbFactory.Create();
        var usdConverted = SeedQuotation(db, "QT-USD-1", "张三", Currency.USD, 1000m);
        SeedQuotation(db, "QT-USD-2", "张三", Currency.USD, 500m);
        SeedQuotation(db, "QT-EUR-1", "张三", Currency.EUR, 2000m);

        // 仅 USD 的一张已转 PI：同币种内计数 / 金额口径按桶独立计算
        db.ProformaInvoices.Add(new ProformaInvoice
        {
            PiNo = "PI-USD-1",
            PiDate = Start.AddDays(2),
            QuotationId = usdConverted.Id,
            QuotationNo = usdConverted.QuotationNo,
            CustomerName = usdConverted.CustomerName,
            Currency = Currency.USD,
            ExchangeRate = 7.2m
        });
        await db.SaveChangesAsync();

        var rows = await new ReportService(db).GetQuotationConversionAsync(Start, End, PrivilegedScope);

        Assert.Equal(2, rows.Count);

        var usd = rows.Single(r => r.Currency == "USD");
        Assert.Equal("张三", usd.SalesmanName);
        Assert.Equal(2, usd.QuotationCount);
        Assert.Equal(1, usd.ConvertedCount);
        Assert.Equal(1500m, usd.TotalAmount);
        Assert.Equal(1000m, usd.ConvertedAmount);
        Assert.Equal(1000m, usd.AvgConvertedAmount);

        var eur = rows.Single(r => r.Currency == "EUR");
        Assert.Equal("张三", eur.SalesmanName);
        Assert.Equal(1, eur.QuotationCount);
        Assert.Equal(0, eur.ConvertedCount);
        Assert.Equal(2000m, eur.TotalAmount);
        Assert.Equal(0m, eur.ConvertedAmount);
        Assert.Equal(0m, eur.AvgConvertedAmount);

        // 金额绝不跨币种合计：不存在把 USD + EUR 合并成 3500 的桶
        Assert.DoesNotContain(rows, r => r.TotalAmount == 3500m);
    }

    [Fact]
    public async Task 未知币种枚举值_归入单一未知币种桶_绝不默认币种()
    {
        using var db = TestDbFactory.Create();
        SeedQuotation(db, "QT-UNK-0", "王五", (Currency)0, 300m);
        SeedQuotation(db, "QT-UNK-99", "王五", (Currency)99, 700m);

        var rows = await new ReportService(db).GetQuotationConversionAsync(Start, End, PrivilegedScope);

        var row = Assert.Single(rows);
        Assert.Equal("王五", row.SalesmanName);
        Assert.Equal(ReportService.UnknownCurrencyGroup, row.Currency);
        Assert.Equal(2, row.QuotationCount);
        Assert.Equal(1000m, row.TotalAmount);
        Assert.NotEqual("USD", row.Currency);
        Assert.NotEqual("CNY", row.Currency);
    }

    [Fact]
    public async Task 零金额与空转换_金额按原币保留且为0()
    {
        using var db = TestDbFactory.Create();
        SeedQuotation(db, "QT-Z-USD", "李四", Currency.USD, 0m);
        SeedQuotation(db, "QT-Z-EUR", "李四", Currency.EUR, 0m);

        var rows = await new ReportService(db).GetQuotationConversionAsync(Start, End, PrivilegedScope);

        Assert.Equal(2, rows.Count);
        var usd = rows.Single(r => r.Currency == "USD");
        Assert.Equal(1, usd.QuotationCount);
        Assert.Equal(0, usd.ConvertedCount);
        Assert.Equal(0m, usd.TotalAmount);
        Assert.Equal(0m, usd.ConvertedAmount);
        Assert.Equal(0m, usd.AvgConvertedAmount);

        var eur = rows.Single(r => r.Currency == "EUR");
        Assert.Equal(1, eur.QuotationCount);
        Assert.Equal(0m, eur.TotalAmount);
        Assert.Equal(0m, eur.ConvertedAmount);
    }

    [Fact]
    public async Task 确定性排序_同业务员同指标按币种升序()
    {
        using var db = TestDbFactory.Create();
        SeedQuotation(db, "QT-TIE-USD", "赵六", Currency.USD, 500m);
        SeedQuotation(db, "QT-TIE-EUR", "赵六", Currency.EUR, 500m);

        var rows = await new ReportService(db).GetQuotationConversionAsync(Start, End, PrivilegedScope);

        Assert.Equal(2, rows.Count);
        Assert.Equal("EUR", rows[0].Currency);
        Assert.Equal("USD", rows[1].Currency);
    }

    // ==================== 源契约：币种规范化 ====================

    [Theory]
    [InlineData(" usd ", "USD")]
    [InlineData("eur", "EUR")]
    [InlineData("CNY", "CNY")]
    [InlineData(" hkd ", "HKD")]
    [InlineData("", "未知币种")]
    [InlineData("   ", "未知币种")]
    [InlineData(null, "未知币种")]
    [InlineData("BTC", "未知币种")]
    [InlineData("0", "未知币种")]
    [InlineData("99", "未知币种")]
    public void 币种规范化_去空白大写_空值与未知取值归入未知币种(string? raw, string expected)
    {
        Assert.Equal(expected, ReportService.NormalizeCurrencyCode(raw));
    }

    // ==================== 授权端点：暴露币种字段 ====================

    [Fact]
    public async Task 报表端点_授权后按业务员与币种分列并暴露币种字段()
    {
        using var db = TestDbFactory.Create();
        var role = new SysRole { RoleName = "报价单币种测试角色", RoleCode = "Quotation-Currency", IsSystem = true };
        db.SysRoles.Add(role);
        db.SaveChanges();
        var user = new SysUser
        {
            UserName = "qc-currency-user",
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = "币种测试用户",
            Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        db.SaveChanges();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        db.SaveChanges();
        var menu = new SysMenu { MenuName = "报价单", MenuCode = "quotation", MenuType = MenuType.Menu };
        db.SysMenus.Add(menu);
        db.SaveChanges();
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
        db.SaveChanges();

        SeedQuotation(db, "QT-C1", "孙七", Currency.USD, 1000m);
        SeedQuotation(db, "QT-C2", "孙七", Currency.EUR, 2000m);

        var controller = new ReportController(new ReportService(db), db);
        TestAuth.SetUser(controller, user.Id);

        var ok = Assert.IsType<OkObjectResult>(await controller.QuotationConversion(Start, End));
        var resp = Assert.IsType<ApiResponse<List<ReportDtos.QuotationConversionItem>>>(ok.Value);
        var rows = resp.Data ?? new List<ReportDtos.QuotationConversionItem>();

        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r.SalesmanName == "孙七" && r.Currency == "USD");
        Assert.Contains(rows, r => r.SalesmanName == "孙七" && r.Currency == "EUR");
    }

    // ==================== 脚手架 ====================

    private static Quotation SeedQuotation(
        ErpDbContext db, string no, string salesmanName, Currency currency,
        decimal totalAmount, DocumentStatus status = DocumentStatus.Approved)
    {
        var quotation = new Quotation
        {
            QuotationNo = no,
            QuotationDate = Start.AddDays(1),
            CustomerId = 1L,
            CustomerName = "币种测试客户",
            SalesmanName = salesmanName,
            Currency = currency,
            TotalAmount = totalAmount,
            Status = status
        };
        db.Quotations.Add(quotation);
        db.SaveChanges();
        return quotation;
    }
}
