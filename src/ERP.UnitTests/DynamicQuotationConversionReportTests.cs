using System.Security.Claims;
using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// 动态报价成交率报表（ERP-206）聚焦单元测试：字段目录 / 字段校验（未知 / 重复 / 空键 / 顺序）、
/// 日期与分页边界校验、业务员数据范围（撤销授权 / 空客户）、分桶投影与分页、来源超限、空页。
/// <para>全部使用内存数据库（TestDbFactory），不连接 SQL Server、不启动 API、不执行任何 SQL / seed、不运行浏览器验收。</para>
/// </summary>
public class DynamicQuotationConversionReportTests
{
    private const string QuotationMenuCode = "quotation";
    private static readonly DateTime Start = new(2026, 9, 1);
    private static readonly DateTime End = new(2026, 9, 30);

    // ==================== 脚手架 ====================

    private static Quotation SeedQuotation(
        ErpDbContext db, string no, string salesmanName, Currency currency,
        decimal totalAmount, long? customerId = 1L, DocumentStatus status = DocumentStatus.Approved)
    {
        var quotation = new Quotation
        {
            QuotationNo = no,
            QuotationDate = Start.AddDays(1),
            CustomerId = customerId,
            CustomerName = "报价成交率测试客户",
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

    // ==================== 字段目录与校验（纯规则） ====================

    [Fact]
    public void 目录_有限白名单且包含币种与原币金额字段()
    {
        var catalog = DynamicQuotationConversionReportRules.GetCatalog();

        Assert.Equal(DynamicQuotationConversionReportRules.AllFieldKeys.Count, catalog.Count);
        Assert.Contains(catalog, f => f.Key == "currency");
        Assert.Contains(catalog, f => f.Key == "salesmanName");
        Assert.Contains(catalog, f => f.Key == "totalAmount");
        Assert.Contains(catalog, f => f.Key == "convertedAmount");
        Assert.All(catalog, f => Assert.False(string.IsNullOrWhiteSpace(f.Key)));
    }

    [Fact]
    public void NormalizeFields_留空返回全部目录顺序()
    {
        Assert.Equal(DynamicQuotationConversionReportRules.AllFieldKeys,
            DynamicQuotationConversionReportRules.NormalizeFields(null));
        Assert.Equal(DynamicQuotationConversionReportRules.AllFieldKeys,
            DynamicQuotationConversionReportRules.NormalizeFields(new List<string>()));
    }

    [Fact]
    public void NormalizeFields_保持请求顺序()
    {
        var keys = DynamicQuotationConversionReportRules.NormalizeFields(
            new[] { "currency", "salesmanName", "convertedAmount" });

        Assert.Equal(new[] { "currency", "salesmanName", "convertedAmount" }, keys);
    }

    [Fact]
    public void NormalizeFields_未知字段拒绝()
    {
        var ex = Assert.Throws<BusinessException>(
            () => DynamicQuotationConversionReportRules.NormalizeFields(new[] { "currency", "notAField" }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public void NormalizeFields_重复字段拒绝_大小写不敏感()
    {
        var ex = Assert.Throws<BusinessException>(
            () => DynamicQuotationConversionReportRules.NormalizeFields(new[] { "currency", "CURRENCY" }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public void NormalizeFields_空键拒绝()
    {
        var ex = Assert.Throws<BusinessException>(
            () => DynamicQuotationConversionReportRules.NormalizeFields(new[] { "currency", "  " }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public void ValidateDateRange_结束早于开始拒绝()
    {
        var ex = Assert.Throws<BusinessException>(
            () => DynamicQuotationConversionReportRules.ValidateDateRange(End, Start));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public void ValidateDateRange_超过366天拒绝()
    {
        var ex = Assert.Throws<BusinessException>(
            () => DynamicQuotationConversionReportRules.ValidateDateRange(
                new DateTime(2026, 1, 1), new DateTime(2027, 1, 2)));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public void ValidateDateRange_边界366天通过()
    {
        var (s, e) = DynamicQuotationConversionReportRules.ValidateDateRange(
            new DateTime(2026, 1, 1), new DateTime(2027, 1, 1));
        Assert.Equal(new DateTime(2026, 1, 1), s);
        Assert.Equal(new DateTime(2027, 1, 1), e);
    }

    [Theory]
    [InlineData(0, 20)]
    [InlineData(1, 0)]
    [InlineData(1, 201)]
    public void ValidatePageBounds_非法页码或页大小拒绝(int page, int pageSize)
    {
        var ex = Assert.Throws<BusinessException>(
            () => DynamicQuotationConversionReportRules.ValidatePageBounds(page, pageSize));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    // ==================== 预览：授权 + 范围 + 投影 + 分页 ====================

    private static SysUser SeedAuthorizedUser(ErpDbContext db, string userName, string roleCode, bool isSystemRole = true)
    {
        var role = SeedRole(db, roleCode, isSystemRole);
        var user = SeedUser(db, userName);
        SeedUserRole(db, user.Id, role.Id);
        var menu = SeedMenu(db, QuotationMenuCode);
        SeedRoleMenu(db, role.Id, menu.Id);
        return user;
    }

    [Fact]
    public async Task 预览_授权后按选定字段顺序投影并分页()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "qcd-priv", "Priv", isSystemRole: true);
        SeedQuotation(db, "QT-A", "张三", Currency.USD, 1000m);
        SeedQuotation(db, "QT-B", "李四", Currency.EUR, 2000m);
        SeedQuotation(db, "QT-C", "王五", Currency.USD, 500m);

        var ctl = BuildController(db, user.Id);
        var page = OkPage(await ctl.Preview(new DynamicQuotationConversionReportRequest
        {
            Fields = new List<string> { "salesmanName", "currency", "quotationCount" },
            Start = Start,
            End = End,
            Page = 1,
            PageSize = 2
        }));

        Assert.Equal(3, page.Total);
        Assert.Equal(2, page.Rows.Count);
        Assert.True(page.Truncated);
        Assert.Equal(2, page.TotalPages);
        Assert.Equal(new[] { "salesmanName", "currency", "quotationCount" },
            page.Columns.Select(c => c.Key).ToArray());
        Assert.All(page.Rows, r => Assert.Equal(new[] { "salesmanName", "currency", "quotationCount" }, r.Keys.ToArray()));
    }

    [Fact]
    public async Task 预览_无身份_拒绝()
    {
        using var db = TestDbFactory.Create();
        var ctl = BuildController(db, null);

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => ctl.Preview(new DynamicQuotationConversionReportRequest { Start = Start, End = End }));
        Assert.Equal(ErrorCodes.Unauthorized, ex.Code);
    }

    [Fact]
    public async Task 预览_无报价单菜单授权_拒绝()
    {
        using var db = TestDbFactory.Create();
        var role = SeedRole(db, "NoMenu", isSystem: true);
        var user = SeedUser(db, "qcd-nomenu");
        SeedUserRole(db, user.Id, role.Id);

        var ctl = BuildController(db, user.Id);
        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => ctl.Preview(new DynamicQuotationConversionReportRequest { Start = Start, End = End }));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    [Fact]
    public async Task 预览_受限制业务员仅见其被分配客户的报价单()
    {
        using var db = TestDbFactory.Create();
        var role = SeedRole(db, "Sales", isSystem: false);
        var user = SeedUser(db, "sales-1");
        SeedUserRole(db, user.Id, role.Id);
        var menu = SeedMenu(db, QuotationMenuCode);
        SeedRoleMenu(db, role.Id, menu.Id);

        var employee = SeedEmployee(db, "sales-1");
        var mine = SeedCustomer(db, "C-MINE", "我的客户", employee.Id);
        var other = SeedCustomer(db, "C-OTHER", "他人客户");

        SeedQuotation(db, "QT-MINE", "张三", Currency.USD, 1000m, mine.Id);
        SeedQuotation(db, "QT-OTHER", "李四", Currency.EUR, 2000m, other.Id);

        var ctl = BuildController(db, user.Id);
        var page = OkPage(await ctl.Preview(new DynamicQuotationConversionReportRequest
        {
            Fields = new List<string> { "salesmanName" },
            Start = Start,
            End = End
        }));

        var row = Assert.Single(page.Rows);
        Assert.Equal("张三", row["salesmanName"]);
    }

    [Fact]
    public async Task 预览_空结果_显式给出空页说明()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "qcd-empty", "Priv");
        var ctl = BuildController(db, user.Id);

        var page = OkPage(await ctl.Preview(new DynamicQuotationConversionReportRequest
        {
            Fields = new List<string> { "salesmanName" },
            Start = Start,
            End = End
        }));

        Assert.Equal(0, page.Total);
        Assert.Empty(page.Rows);
        Assert.Equal(DynamicQuotationConversionReportRules.EmptyText, page.EmptyText);
    }

    [Fact]
    public async Task 预览_来源超限_拒绝并提示缩小日期范围()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "qcd-over", "Priv");

        var list = new List<Quotation>();
        for (var i = 0; i < 2001; i++)
        {
            list.Add(new Quotation
            {
                QuotationNo = $"QT-{i:D4}",
                QuotationDate = new DateTime(2026, 9, 1).AddDays(i % 30),
                CustomerId = 1L,
                CustomerName = "客户",
                SalesmanName = "业务员",
                Currency = Currency.USD,
                TotalAmount = 1m,
                Status = DocumentStatus.Approved
            });
        }
        db.Quotations.AddRange(list);
        await db.SaveChangesAsync();

        var ctl = BuildController(db, user.Id);
        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => ctl.Preview(new DynamicQuotationConversionReportRequest { Start = Start, End = End }));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Contains("缩小日期范围", ex.Message);
    }
}


