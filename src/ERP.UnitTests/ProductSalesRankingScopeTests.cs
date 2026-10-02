using System.Security.Claims;
using ERP.Api.Controllers;
using ERP.Application.Common;
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
/// 商品销量排名（/api/reports/product-sales-ranking，ERP-212）数据范围与授权单元测试：
/// 每次请求重新校验当前登录身份与「商品销量排名榜」菜单授权，按业务员数据范围（ERP-097 唯一权威口径）
/// 在查询源头过滤销售出库头（特权账号不过滤、受限制业务员仅其被分配客户、空客户对受限制账号不可见），
/// 且只统计已审核、未删除出库头与未删除明细；签名数量（可为负）直接求和，不二次计入退货或库存流水。
/// <para>全部使用内存数据库（TestDbFactory），不连接 SQL Server、不启动 API、不执行任何 SQL / seed、不运行浏览器验收。</para>
/// </summary>
public class ProductSalesRankingScopeTests
{
    private const string MenuCode = "product-sales-ranking";

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

    /// <summary>创建拥有「商品销量排名榜」菜单授权的用户（不含业务员映射，由各用例按需补齐）</summary>
    private static SysUser SeedAuthorizedUser(ErpDbContext db, string userName, string roleCode, bool isSystemRole = false)
    {
        var role = SeedRole(db, roleCode, isSystemRole);
        var user = SeedUser(db, userName);
        SeedUserRole(db, user.Id, role.Id);
        SeedRoleMenu(db, role.Id, SeedMenu(db, MenuCode).Id);
        return user;
    }

    private static StockOut SeedStockOut(ErpDbContext db, string no, long customerId, DocumentStatus status, bool deleted = false)
    {
        var stockOut = new StockOut
        {
            StockOutNo = no,
            StockOutDate = new DateTime(2026, 9, 10),
            CustomerId = customerId,
            Status = status,
            IsDeleted = deleted
        };
        db.StockOuts.Add(stockOut);
        db.SaveChanges();
        return stockOut;
    }

    private static StockOutDetail SeedDetail(
        ErpDbContext db, long stockOutId, long productId, string productName, string unit, decimal quantity, bool deleted = false)
    {
        var detail = new StockOutDetail
        {
            StockOutId = stockOutId,
            ProductId = productId,
            ProductName = productName,
            Spec = "大",
            Unit = unit,
            Quantity = quantity,
            IsDeleted = deleted
        };
        db.StockOutDetails.Add(detail);
        db.SaveChanges();
        return detail;
    }

    private static ReportController BuildController(ErpDbContext db, long? userId)
    {
        var ctl = new ReportController(new ReportService(db), db);
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

    private static List<ReportDtos.ProductSalesRankItem> OkList(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<ApiResponse<List<ReportDtos.ProductSalesRankItem>>>(ok.Value);
        return resp.Data ?? new List<ReportDtos.ProductSalesRankItem>();
    }

    // ==================== 身份与菜单授权 ====================

    [Fact]
    public async Task 无身份_未认证拒绝()
    {
        using var db = TestDbFactory.Create();
        var ctl = BuildController(db, null);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.ProductSalesRanking(Start, End, 10));
        Assert.Equal(ErrorCodes.Unauthorized, ex.Code);
    }

    [Fact]
    public async Task 无菜单授权_权限不足拒绝()
    {
        using var db = TestDbFactory.Create();
        var role = SeedRole(db, "NoMenu");
        var user = SeedUser(db, "nommenu-user");
        SeedUserRole(db, user.Id, role.Id);
        var ctl = BuildController(db, user.Id);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.ProductSalesRanking(Start, End, 10));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    [Fact]
    public async Task 授权被回收_下一次请求立即拒绝()
    {
        using var db = TestDbFactory.Create();
        var role = SeedRole(db, "Revoke-Role");
        var user = SeedUser(db, "revoke-user");
        SeedUserRole(db, user.Id, role.Id);
        var menu = SeedMenu(db, MenuCode);
        var roleMenu = new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id };
        db.SysRoleMenus.Add(roleMenu);
        db.SaveChanges();

        var ctl = BuildController(db, user.Id);
        Assert.IsType<OkObjectResult>(await ctl.ProductSalesRanking(Start, End, 10));

        roleMenu.IsDeleted = true;
        db.SaveChanges();

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.ProductSalesRanking(Start, End, 10));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    // ==================== 业务员数据范围 ====================

    [Fact]
    public async Task 受限制业务员_只看到被分配客户()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "alice", "Sales");
        var employee = SeedEmployee(db, "alice");
        var mine = SeedCustomer(db, "C001", "我的客户", employee.Id);
        var other = SeedCustomer(db, "C002", "别人的客户", employee.Id + 1000);

        var mineOut = SeedStockOut(db, "OUT-MINE", mine.Id, DocumentStatus.Approved);
        var otherOut = SeedStockOut(db, "OUT-OTHER", other.Id, DocumentStatus.Approved);
        SeedDetail(db, mineOut.Id, 1, "热销商品", "PCS", 100m);
        SeedDetail(db, otherOut.Id, 2, "别家商品", "PCS", 999m);

        var ctl = BuildController(db, user.Id);
        var items = OkList(await ctl.ProductSalesRanking(Start, End, 10));

        var row = Assert.Single(items);
        Assert.Equal("热销商品", row.ProductName);
        Assert.Equal(100m, row.TotalQuantity);
    }

    [Fact]
    public async Task 未映射业务员_看不到任何数据()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "bob", "Sales");
        var customer = SeedCustomer(db, "C001", "有客户");
        var out1 = SeedStockOut(db, "OUT-1", customer.Id, DocumentStatus.Approved);
        SeedDetail(db, out1.Id, 1, "热销商品", "PCS", 100m);

        var ctl = BuildController(db, user.Id);
        var items = OkList(await ctl.ProductSalesRanking(Start, End, 10));

        Assert.Empty(items);
    }

    // ==================== 状态与软删除证据 ====================

    [Fact]
    public async Task 草稿已取消与软删除出库头_不计入()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "alice", "Sales");
        var employee = SeedEmployee(db, "alice");
        var customer = SeedCustomer(db, "C001", "我的客户", employee.Id);

        var draft = SeedStockOut(db, "OUT-DRAFT", customer.Id, DocumentStatus.Pending);
        var cancelled = SeedStockOut(db, "OUT-CANCEL", customer.Id, DocumentStatus.Cancelled);
        var deleted = SeedStockOut(db, "OUT-DEL", customer.Id, DocumentStatus.Approved, deleted: true);
        var approved = SeedStockOut(db, "OUT-OK", customer.Id, DocumentStatus.Approved);

        SeedDetail(db, draft.Id, 1, "热销商品", "PCS", 100m);
        SeedDetail(db, cancelled.Id, 1, "热销商品", "PCS", 200m);
        SeedDetail(db, deleted.Id, 1, "热销商品", "PCS", 300m);
        SeedDetail(db, approved.Id, 1, "热销商品", "PCS", 50m);

        var ctl = BuildController(db, user.Id);
        var items = OkList(await ctl.ProductSalesRanking(Start, End, 10));

        var row = Assert.Single(items);
        Assert.Equal(50m, row.TotalQuantity);
    }

    [Fact]
    public async Task 软删除明细_不计入()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "alice", "Sales");
        var employee = SeedEmployee(db, "alice");
        var customer = SeedCustomer(db, "C001", "我的客户", employee.Id);
        var out1 = SeedStockOut(db, "OUT-1", customer.Id, DocumentStatus.Approved);

        SeedDetail(db, out1.Id, 1, "热销商品", "PCS", 100m);
        SeedDetail(db, out1.Id, 1, "热销商品", "PCS", 50m, deleted: true);

        var ctl = BuildController(db, user.Id);
        var items = OkList(await ctl.ProductSalesRanking(Start, End, 10));

        var row = Assert.Single(items);
        Assert.Equal(100m, row.TotalQuantity);
    }

    [Fact]
    public async Task 签名数量保留_负数量直接计入_不二次调整()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "alice", "Sales");
        var employee = SeedEmployee(db, "alice");
        var customer = SeedCustomer(db, "C001", "我的客户", employee.Id);
        var out1 = SeedStockOut(db, "OUT-1", customer.Id, DocumentStatus.Approved);

        SeedDetail(db, out1.Id, 1, "热销商品", "PCS", 100m);
        SeedDetail(db, out1.Id, 1, "热销商品", "PCS", -20m);

        var ctl = BuildController(db, user.Id);
        var items = OkList(await ctl.ProductSalesRanking(Start, End, 10));

        var row = Assert.Single(items);
        Assert.Equal(80m, row.TotalQuantity);
    }

    // ==================== 日期 / Top 边界（先于源读取） ====================

    [Theory]
    [InlineData(0)]
    [InlineData(201)]
    public async Task Top越界_拒绝(int top)
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "alice", "Sales");
        var ctl = BuildController(db, user.Id);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.ProductSalesRanking(Start, End, top));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    [Fact]
    public async Task 日期范围超过366天_拒绝()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "alice", "Sales");
        var ctl = BuildController(db, user.Id);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.ProductSalesRanking(new DateTime(2026, 1, 1), new DateTime(2027, 1, 2), 10));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
    }

    // ==================== 只读 ====================

    [Fact]
    public async Task 只读_查询后无待保存变更且不新增记录()
    {
        using var db = TestDbFactory.Create();
        var user = SeedAuthorizedUser(db, "alice", "Sales");
        var employee = SeedEmployee(db, "alice");
        var customer = SeedCustomer(db, "C001", "我的客户", employee.Id);
        var out1 = SeedStockOut(db, "OUT-1", customer.Id, DocumentStatus.Approved);
        SeedDetail(db, out1.Id, 1, "热销商品", "PCS", 100m);

        var before = db.StockOuts.Count();
        var ctl = BuildController(db, user.Id);
        _ = OkList(await ctl.ProductSalesRanking(Start, End, 10));

        Assert.Equal(before, db.StockOuts.Count());
        Assert.False(db.ChangeTracker.HasChanges());
    }
}
