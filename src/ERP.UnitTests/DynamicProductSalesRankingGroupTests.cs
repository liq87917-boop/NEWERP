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
/// ERP-216 动态商品销量排名报表「按单位分组汇总」单元测试：仅接受 none / unit 分组键（fail closed），
/// 从新鲜授权、未投影的 Top 排名项派生出「精确单位 → 排名桶数 + 签名数量小计」的只读汇总。
/// <para>覆盖：分组键规范化（合法 / 非法）、隐藏字段不影响分组、精确单位独立成组（大小写 / 空白不合并）、
/// 空白 / 未知单位独立桶（数量 null）、负数 / 零数量保留符号、Top 截断只统计当前 Top 结果、授权撤销拒绝、只读不写库。</para>
/// <para>全部使用内存数据库（TestDbFactory），不连接 SQL Server、不启动 API、不执行任何 SQL / seed。</para>
/// </summary>
public class DynamicProductSalesRankingGroupTests
{
    private const string MenuCode = "product-sales-ranking";

    private static readonly DateTime Start = new(2026, 9, 1);
    private static readonly DateTime End = new(2026, 9, 30);

    // ==================== 0. 测试脚手架 ====================

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

    private static StockOut SeedStockOut(ErpDbContext db, string no, long customerId, DocumentStatus status)
    {
        var stockOut = new StockOut
        {
            StockOutNo = no,
            StockOutDate = new DateTime(2026, 9, 10),
            CustomerId = customerId,
            Status = status,
            IsDeleted = false
        };
        db.StockOuts.Add(stockOut);
        db.SaveChanges();
        return stockOut;
    }

    private static StockOutDetail SeedDetail(
        ErpDbContext db, long stockOutId, long productId, string productName, string unit, decimal quantity)
    {
        var detail = new StockOutDetail
        {
            StockOutId = stockOutId,
            ProductId = productId,
            ProductName = productName,
            Spec = "大",
            Unit = unit,
            Quantity = quantity,
            IsDeleted = false
        };
        db.StockOutDetails.Add(detail);
        db.SaveChanges();
        return detail;
    }

    /// <summary>创建拥有「商品销量排名榜」菜单授权的用户（不含业务员映射，由各用例按需补齐）</summary>
    private static SysUser SeedAuthorizedUser(ErpDbContext db, string userName, string roleCode, bool isSystemRole = false)
    {
        var role = SeedRole(db, roleCode, isSystemRole);
        var user = SeedUser(db, userName);
        SeedUserRole(db, user.Id, role.Id);
        SeedRoleMenu(db, role.Id, SeedMenu(db, MenuCode).Id);
        return user;
    }

    private static DynamicProductSalesRankingReportController BuildController(ErpDbContext db, long? userId)
    {
        var ctl = new DynamicProductSalesRankingReportController(db, new ReportService(db));
        var claims = userId.HasValue
            ? new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }
            : Array.Empty<Claim>();
        ctl.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"))
            }
        };
        return ctl;
    }

    private static DynamicProductSalesRankingReportPageDto OkPage(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<ApiResponse<DynamicProductSalesRankingReportPageDto>>(ok.Value);
        return resp.Data!;
    }

    private static DynamicProductSalesRankingReportRequest Request(
        List<string>? fields = null, DateTime? start = null, DateTime? end = null, int top = 10,
        string? groupBy = null, ProductSalesRankingFilterDto? filter = null)
        => new() { Fields = fields, Start = start, End = end, Top = top, GroupBy = groupBy, Filter = filter };

    // ==================== 1. 分组键规范化 ====================

    [Theory]
    [InlineData(null, "none")]
    [InlineData("", "none")]
    [InlineData("  ", "none")]
    [InlineData("none", "none")]
    [InlineData("NONE", "none")]
    [InlineData("unit", "unit")]
    [InlineData("Unit", "unit")]
    [InlineData("UNIT", "unit")]
    public void NormalizeGroupBy_合法取值_规范化(string? input, string expected)
    {
        Assert.Equal(expected, DynamicProductSalesRankingReportRules.NormalizeGroupBy(input));
    }

    [Theory]
    [InlineData("customer")]
    [InlineData("product")]
    [InlineData("units")]
    [InlineData("单位")]
    [InlineData("money")]
    public void NormalizeGroupBy_无效取值_拒绝(string input)
    {
        var ex = Assert.Throws<BusinessException>(() => DynamicProductSalesRankingReportRules.NormalizeGroupBy(input));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    // ==================== 2. 纯规则：按单位分组汇总 ====================

    [Fact]
    public void BuildGroups_按单位_分别成组_签名数量小计()
    {
        var items = new List<ReportDtos.ProductSalesRankItem>
        {
            new() { Rank = 1, ProductId = 1, ProductName = "A", Unit = "PCS", TotalQuantity = 100m },
            new() { Rank = 2, ProductId = 2, ProductName = "B", Unit = "PCS", TotalQuantity = 30m },
            new() { Rank = 3, ProductId = 3, ProductName = "C", Unit = "KG", TotalQuantity = -20m },
            new() { Rank = 4, ProductId = 4, ProductName = "D", Unit = "KG", TotalQuantity = 50m },
        };

        var groups = DynamicProductSalesRankingReportRules.BuildGroups(items, "unit");

        Assert.Equal(2, groups.Count);

        var pcs = Assert.Single(groups, g => g.Unit == "PCS");
        Assert.Equal("PCS", pcs.Label);
        Assert.Equal(2, pcs.RankingBucketCount);
        Assert.Equal(130m, pcs.TotalQuantity);
        Assert.False(pcs.IsUnknown);

        var kg = Assert.Single(groups, g => g.Unit == "KG");
        Assert.Equal(2, kg.RankingBucketCount);
        Assert.Equal(30m, kg.TotalQuantity);
        Assert.False(kg.IsUnknown);
    }

    [Fact]
    public void BuildGroups_大小写与空白单位_不合并()
    {
        var items = new List<ReportDtos.ProductSalesRankItem>
        {
            new() { Rank = 1, ProductId = 1, ProductName = "A", Unit = "PCS", TotalQuantity = 10m },
            new() { Rank = 2, ProductId = 2, ProductName = "B", Unit = "pcs", TotalQuantity = 20m },
            new() { Rank = 3, ProductId = 3, ProductName = "C", Unit = "PCS ", TotalQuantity = 30m },
        };

        var groups = DynamicProductSalesRankingReportRules.BuildGroups(items, "unit");

        Assert.Equal(3, groups.Count);
        Assert.Contains(groups, g => g.Unit == "PCS" && g.RankingBucketCount == 1 && g.TotalQuantity == 10m);
        Assert.Contains(groups, g => g.Unit == "pcs" && g.RankingBucketCount == 1 && g.TotalQuantity == 20m);
        Assert.Contains(groups, g => g.Unit == "PCS " && g.RankingBucketCount == 1 && g.TotalQuantity == 30m);
    }

    [Fact]
    public void BuildGroups_空白与未知单位_独立未知桶_null数量()
    {
        var items = new List<ReportDtos.ProductSalesRankItem>
        {
            new() { Rank = 1, ProductId = 1, ProductName = "A", Unit = "PCS", TotalQuantity = 10m },
            new() { Rank = 2, ProductId = 2, ProductName = "B", Unit = "", TotalQuantity = 5m },
            new() { Rank = 3, ProductId = 3, ProductName = "C", Unit = "  ", TotalQuantity = 7m },
            new() { Rank = 4, ProductId = 4, ProductName = "D", Unit = null!, TotalQuantity = 3m },
        };

        var groups = DynamicProductSalesRankingReportRules.BuildGroups(items, "unit");

        Assert.Equal(2, groups.Count);

        var known = Assert.Single(groups, g => !g.IsUnknown);
        Assert.Equal("PCS", known.Unit);
        Assert.Equal(1, known.RankingBucketCount);
        Assert.Equal(10m, known.TotalQuantity);

        var unknown = Assert.Single(groups, g => g.IsUnknown);
        Assert.Equal(string.Empty, unknown.Unit);
        Assert.Equal(DynamicProductSalesRankingReportRules.UnknownUnitLabel, unknown.Label);
        Assert.Equal(3, unknown.RankingBucketCount);
        Assert.Null(unknown.TotalQuantity);
    }

    [Fact]
    public void BuildGroups_零与负数量_保留符号且数量非空()
    {
        var items = new List<ReportDtos.ProductSalesRankItem>
        {
            new() { Rank = 1, ProductId = 1, ProductName = "A", Unit = "PCS", TotalQuantity = 0m },
            new() { Rank = 2, ProductId = 2, ProductName = "B", Unit = "PCS", TotalQuantity = -5m },
            new() { Rank = 3, ProductId = 3, ProductName = "C", Unit = "BOX", TotalQuantity = 0m },
        };

        var groups = DynamicProductSalesRankingReportRules.BuildGroups(items, "unit");

        var pcs = Assert.Single(groups, g => g.Unit == "PCS");
        Assert.Equal(2, pcs.RankingBucketCount);
        Assert.Equal(-5m, pcs.TotalQuantity);

        var box = Assert.Single(groups, g => g.Unit == "BOX");
        Assert.Equal(1, box.RankingBucketCount);
        Assert.Equal(0m, box.TotalQuantity);
        Assert.False(box.IsUnknown);
    }

    [Theory]
    [InlineData("none")]
    [InlineData(null)]
    public void BuildGroups_none或空_返回空列表(string? groupBy)
    {
        var items = new List<ReportDtos.ProductSalesRankItem>
        {
            new() { Rank = 1, ProductId = 1, ProductName = "A", Unit = "PCS", TotalQuantity = 10m },
        };

        Assert.Empty(DynamicProductSalesRankingReportRules.BuildGroups(items, groupBy!));
    }


    // ==================== 3. 控制器集成：授权 / 隐藏字段 / Top 截断 / 只读 ====================

    [Fact]
    public async Task 预览_按单位分组_隐藏单位与数量字段_分组仍正确()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "group-user", "Sales", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "客户");
        var out1 = SeedStockOut(db, "OUT-1", customer.Id, DocumentStatus.Approved);
        SeedDetail(db, out1.Id, 1, "商品A", "PCS", 100m);
        SeedDetail(db, out1.Id, 2, "商品B", "PCS", 30m);
        SeedDetail(db, out1.Id, 3, "商品C", "KG", 20m);

        var ctl = BuildController(db, user.Id);
        var page = OkPage(await ctl.Preview(Request(
            fields: new List<string> { "rank", "productName" },
            start: Start, end: End, groupBy: "unit")));

        Assert.Equal("unit", page.GroupBy);
        Assert.NotNull(page.Groups);
        Assert.Equal(2, page.Groups.Count);
        Assert.NotEmpty(page.GroupContextText);

        var pcs = Assert.Single(page.Groups, g => g.Unit == "PCS");
        Assert.Equal(2, pcs.RankingBucketCount);
        Assert.Equal(130m, pcs.TotalQuantity);

        var kg = Assert.Single(page.Groups, g => g.Unit == "KG");
        Assert.Equal(1, kg.RankingBucketCount);
        Assert.Equal(20m, kg.TotalQuantity);

        // 隐藏单位 / 数量字段不会影响分组派生
        Assert.All(page.Rows, r =>
        {
            Assert.False(r.ContainsKey("unit"));
            Assert.False(r.ContainsKey("totalQuantity"));
        });
    }

    [Fact]
    public async Task 预览_Top截断_分组仅统计当前Top结果()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "top-user", "Sales", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "客户");
        var out1 = SeedStockOut(db, "OUT-1", customer.Id, DocumentStatus.Approved);
        SeedDetail(db, out1.Id, 1, "PCS商品", "PCS", 100m);
        SeedDetail(db, out1.Id, 2, "BOX商品", "BOX", 50m);
        SeedDetail(db, out1.Id, 3, "KG商品", "KG", 30m);

        var ctl = BuildController(db, user.Id);
        var page = OkPage(await ctl.Preview(Request(start: Start, end: End, top: 2, groupBy: "unit")));

        Assert.Equal(2, page.Total);
        Assert.True(page.TopLimited);
        Assert.NotNull(page.Groups);
        Assert.Equal(2, page.Groups.Count);
        Assert.DoesNotContain(page.Groups, g => g.Unit == "KG");
    }

    [Fact]
    public async Task 预览_none分组_返回空分组与空分组上下文()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "none-user", "Sales", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "客户");
        var out1 = SeedStockOut(db, "OUT-1", customer.Id, DocumentStatus.Approved);
        SeedDetail(db, out1.Id, 1, "商品A", "PCS", 100m);

        var ctl = BuildController(db, user.Id);
        var page = OkPage(await ctl.Preview(Request(start: Start, end: End)));

        Assert.Equal("none", page.GroupBy);
        Assert.NotNull(page.Groups);
        Assert.Empty(page.Groups);
        Assert.Equal(string.Empty, page.GroupContextText);
    }

    [Fact]
    public async Task 预览_无效分组键_拒绝且不读取数据()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "bad-group-user", "Sales", isSystemRole: true);
        var ctl = BuildController(db, user.Id);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Preview(Request(groupBy: "money")));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public async Task 预览_按单位分组_授权撤销_拒绝()
    {
        using var db = TestDbFactory.Create();
        var role = SeedRole(db, "Revoke-Group-Role");
        var user = SeedUser(db, "revoke-group-user");
        SeedUserRole(db, user.Id, role.Id);
        var menu = SeedMenu(db, MenuCode);
        var roleMenu = new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id };
        db.SysRoleMenus.Add(roleMenu);
        db.SaveChanges();

        var customer = SeedCustomer(db, "C001", "客户");
        var out1 = SeedStockOut(db, "OUT-1", customer.Id, DocumentStatus.Approved);
        SeedDetail(db, out1.Id, 1, "商品A", "PCS", 100m);

        var ctl = BuildController(db, user.Id);
        Assert.IsType<OkObjectResult>(await ctl.Preview(Request(groupBy: "unit")));

        roleMenu.IsDeleted = true;
        db.SaveChanges();

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Preview(Request(groupBy: "unit")));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    [Fact]
    public async Task 预览_按单位分组_只读_不写库()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "readonly-group-user", "Sales", isSystemRole: true);
        var customer = SeedCustomer(db, "C001", "客户");
        var out1 = SeedStockOut(db, "OUT-1", customer.Id, DocumentStatus.Approved);
        SeedDetail(db, out1.Id, 1, "商品A", "PCS", 100m);

        var before = db.StockOuts.Count();
        var ctl = BuildController(db, user.Id);
        _ = OkPage(await ctl.Preview(Request(groupBy: "unit")));

        Assert.Equal(before, db.StockOuts.Count());
        Assert.False(db.ChangeTracker.HasChanges());
    }
}

