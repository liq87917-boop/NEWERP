using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using ERP.Infrastructure.Export;
using ERP.Infrastructure.Storage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using System.Security.Claims;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-467 主数据（客户 / 供应商 / 员工 / 仓库 / 商品）<b>入口授权</b>单元测试：证明实时身份 / 账号状态 /
/// 既有「客户资料」/「供应商资料」/「员工资料」/「仓库资料」/「商品资料」菜单判定在<b>每一条入口</b>
/// （分页 / 全部 / 按主键 / 新增 / 修改 / 删除 / 批量删除，以及指定货代下拉 / 业务员下拉 / 商品导出 /
/// 单张与批量图片上传）都无条件执行，且与请求路径是否赋值、请求形状、以及控制器是否绑定 <c>HttpContext</c>
/// <b>完全无关</b>。
/// <list type="number">
/// <item><b>路径无关</b>：空路径 / 已赋值路径 / 完全未绑定 <c>HttpContext</c> 三种形状对同一身份给出完全一致的判定；</item>
/// <item><b>fail closed</b>：缺失 / 零 / 未知 / 已删除身份按未认证（2000），禁用 / 缺菜单 / 被撤销菜单按权限不足（2002），
/// 且发生在任何主数据读取 / 写入 / 导出 / 上传产物<b>之前</b>；</item>
/// <item><b>零写入</b>：被拒绝的调用不新增 / 不改写任何主数据行，也不产出任何导出或图片上传产物；</item>
/// <item><b>既有契约不变</b>：授权身份下既有读 / 写 / 软删除 / 批量删除生命周期逐条保持。</item>
/// </list>
/// 全部使用隔离的内存库（<see cref="TestDbFactory"/>），授权只播种在测试数据里；
/// 不新增任何生产菜单 / 权限 / 用户授权，也没有任何测试专用放行开关。
/// </summary>
public class MasterDataEntryAuthorizationTests
{
    // ==================== 0. 测试脚手架 ====================

    private const string PathModeEmpty = "empty";
    private const string PathModePopulated = "populated";
    private const string PathModeNoContext = "no-context";

    private const string CustomerPath = "/api/base/customers";
    private const string SupplierPath = "/api/base/suppliers";
    private const string EmployeePath = "/api/base/employees";
    private const string WarehousePath = "/api/base/warehouses";
    private const string ProductPath = "/api/base/products";

    private static ControllerContext? ContextFor(long? userId, string pathMode, string populatedPath)
    {
        if (pathMode == PathModeNoContext) return null;
        var claims = userId.HasValue
            ? new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }
            : Array.Empty<Claim>();
        var http = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) };
        if (pathMode == PathModePopulated) http.Request.Path = populatedPath;
        return new ControllerContext { HttpContext = http };
    }

    private static CustomerController Customer(ErpDbContext db, long? userId, string pathMode)
    {
        var controller = new CustomerController(new GenericService<BaseCustomer>(db), db);
        var context = ContextFor(userId, pathMode, CustomerPath);
        if (context is not null) controller.ControllerContext = context;
        return controller;
    }

    private static SupplierController Supplier(ErpDbContext db, long? userId, string pathMode)
    {
        var controller = new SupplierController(new GenericService<BaseSupplier>(db), db);
        var context = ContextFor(userId, pathMode, SupplierPath);
        if (context is not null) controller.ControllerContext = context;
        return controller;
    }

    private static EmployeeController Employee(ErpDbContext db, long? userId, string pathMode)
    {
        var controller = new EmployeeController(new GenericService<BaseEmployee>(db), db);
        var context = ContextFor(userId, pathMode, EmployeePath);
        if (context is not null) controller.ControllerContext = context;
        return controller;
    }

    private static WarehouseController Warehouse(ErpDbContext db, long? userId, string pathMode)
    {
        var controller = new WarehouseController(new GenericService<BaseWarehouse>(db), db);
        var context = ContextFor(userId, pathMode, WarehousePath);
        if (context is not null) controller.ControllerContext = context;
        return controller;
    }

    private static ProductController Product(ErpDbContext db, long? userId, string pathMode)
    {
        var controller = new ProductController(
            new GenericService<BaseProduct>(db), new OssStorageService(BuildOssConfiguration()),
            new ProductExcelExporter(db), new FakeWebHostEnvironment(), db);
        var context = ContextFor(userId, pathMode, ProductPath);
        if (context is not null) controller.ControllerContext = context;
        return controller;
    }

    /// <summary>播种一个独立授权身份（可选状态 / 删除 / 是否具备既有菜单 / 是否系统内置特权角色），返回用户 Id。</summary>
    private static long SeedActor(ErpDbContext db, string menuCode, string menuText,
        UserStatus status = UserStatus.Enabled, bool deleted = false, bool withMenu = true, bool privileged = true)
    {
        var user = new SysUser
        {
            UserName = $"md-entry-{Guid.NewGuid():N}",
            DisplayName = "主数据入口授权测试账号",
            PasswordHash = "hash",
            PasswordSalt = "salt",
            Status = status,
            IsDeleted = deleted
        };
        db.SysUsers.Add(user);
        db.SaveChanges();

        var role = new SysRole
        {
            RoleCode = $"MdEntry-{Guid.NewGuid():N}",
            RoleName = "主数据入口授权测试角色",
            IsSystem = privileged
        };
        db.SysRoles.Add(role);
        db.SaveChanges();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        db.SaveChanges();

        if (withMenu) GrantMenu(db, role.Id, menuCode, menuText);
        return user.Id;
    }

    /// <summary>按既有菜单编码授予角色访问权限（幂等；菜单缺失时按既有种子口径补建一条功能菜单）。</summary>
    private static void GrantMenu(ErpDbContext db, long roleId, string menuCode, string menuText)
    {
        var menu = db.SysMenus.FirstOrDefault(m => m.MenuCode == menuCode && !m.IsDeleted);
        if (menu is null)
        {
            menu = new SysMenu { MenuCode = menuCode, MenuName = menuText, MenuType = MenuType.Menu };
            db.SysMenus.Add(menu);
            db.SaveChanges();
        }
        if (!db.SysRoleMenus.Any(rm => rm.RoleId == roleId && rm.MenuId == menu.Id && !rm.IsDeleted))
        {
            db.SysRoleMenus.Add(new SysRoleMenu { RoleId = roleId, MenuId = menu.Id });
            db.SaveChanges();
        }
    }

    /// <summary>撤销指定账号所持既有菜单（模拟请求之间撤销权限，不影响其它账号）。</summary>
    private static void RevokeMenu(ErpDbContext db, long userId, string menuCode)
    {
        var roleIds = db.SysUserRoles.AsNoTracking()
            .Where(ur => ur.UserId == userId && !ur.IsDeleted).Select(ur => ur.RoleId).ToList();
        var menuIds = db.SysMenus.AsNoTracking()
            .Where(m => m.MenuCode == menuCode).Select(m => m.Id).ToList();
        foreach (var grant in db.SysRoleMenus
            .Where(rm => roleIds.Contains(rm.RoleId) && menuIds.Contains(rm.MenuId)).ToList())
            grant.IsDeleted = true;
        db.SaveChanges();
    }

    private static async Task AssertCode(int expected, Func<Task<IActionResult>> action)
    {
        var ex = await Assert.ThrowsAsync<BusinessException>(action);
        Assert.Equal(expected, ex.Code);
    }

    private static T Data<T>(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<ApiResponse<T>>(ok.Value);
        return response.Data!;
    }

    private static IFormFile Image()
        => new FakeFormFile("p.png", "image/png",
            new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x01 });

    private static IConfiguration BuildOssConfiguration() =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Oss:AccessKeyId"] = "test-key-id",
            ["Oss:AccessKeySecret"] = "test-secret",
            ["Oss:Bucket"] = "test-bucket",
            ["Oss:Endpoint"] = "oss-cn-hangzhou.aliyuncs.com"
        }).Build();


    // ==================== 1. 五类控制器的拒绝矩阵（逐入口） ====================

    /// <summary>
    /// 逐条入口断言拒绝：覆盖五类控制器全部分页 / 全部 / 按主键 / 新增 / 修改 / 删除 / 批量删除，
    /// 以及指定货代下拉 / 业务员下拉 / 商品导出 / 单张与批量图片上传。
    /// </summary>
    private static async Task AssertAllDeniedAsync(ErpDbContext db, long? userId, string pathMode, int expectedCode)
    {
        var customer = Customer(db, userId, pathMode);
        await AssertCode(expectedCode, () => customer.GetPaged(new PageQuery()));
        await AssertCode(expectedCode, () => customer.GetAll());
        await AssertCode(expectedCode, () => customer.GetById(1));
        await AssertCode(expectedCode, () => customer.GetForwarderOptions());
        await AssertCode(expectedCode, () => customer.Create(NewCustomer()));
        await AssertCode(expectedCode, () => customer.Update(1, NewCustomer()));
        await AssertCode(expectedCode, () => customer.Delete(1));
        await AssertCode(expectedCode, () => customer.BatchDelete(new List<long> { 1 }));

        var supplier = Supplier(db, userId, pathMode);
        await AssertCode(expectedCode, () => supplier.GetPaged(new PageQuery()));
        await AssertCode(expectedCode, () => supplier.GetAll());
        await AssertCode(expectedCode, () => supplier.GetById(1));
        await AssertCode(expectedCode, () => supplier.Create(NewSupplier()));
        await AssertCode(expectedCode, () => supplier.Update(1, NewSupplier()));
        await AssertCode(expectedCode, () => supplier.Delete(1));
        await AssertCode(expectedCode, () => supplier.BatchDelete(new List<long> { 1 }));

        var employee = Employee(db, userId, pathMode);
        await AssertCode(expectedCode, () => employee.GetPaged(new PageQuery()));
        await AssertCode(expectedCode, () => employee.GetAll());
        await AssertCode(expectedCode, () => employee.GetById(1));
        await AssertCode(expectedCode, () => employee.GetSalesmen());
        await AssertCode(expectedCode, () => employee.Create(NewEmployee()));
        await AssertCode(expectedCode, () => employee.Update(1, NewEmployee()));
        await AssertCode(expectedCode, () => employee.Delete(1));
        await AssertCode(expectedCode, () => employee.BatchDelete(new List<long> { 1 }));

        var warehouse = Warehouse(db, userId, pathMode);
        await AssertCode(expectedCode, () => warehouse.GetPaged(new PageQuery()));
        await AssertCode(expectedCode, () => warehouse.GetAll());
        await AssertCode(expectedCode, () => warehouse.GetById(1));
        await AssertCode(expectedCode, () => warehouse.Create(NewWarehouse()));
        await AssertCode(expectedCode, () => warehouse.Update(1, NewWarehouse()));
        await AssertCode(expectedCode, () => warehouse.Delete(1));
        await AssertCode(expectedCode, () => warehouse.BatchDelete(new List<long> { 1 }));

        var product = Product(db, userId, pathMode);
        await AssertCode(expectedCode, () => product.GetPaged(new PageQuery()));
        await AssertCode(expectedCode, () => product.GetAll());
        await AssertCode(expectedCode, () => product.GetById(1));
        await AssertCode(expectedCode, () => product.Create(NewProduct()));
        await AssertCode(expectedCode, () => product.Update(1, NewProduct()));
        await AssertCode(expectedCode, () => product.Delete(1));
        await AssertCode(expectedCode, () => product.BatchDelete(new List<long> { 1 }));
        await AssertCode(expectedCode, () => product.Export());
        await AssertCode(expectedCode, () => product.Upload(Image()));
        await AssertCode(expectedCode, () => product.UploadBatch(new List<IFormFile> { Image() }));
    }


    private static BaseCustomer NewCustomer() => new()
    { CustomerCode = "C-ENTRY", CustomerName = "入口测试客户", Status = 1 };

    private static BaseSupplier NewSupplier() => new()
    { SupplierCode = "S-ENTRY", SupplierName = "入口测试供应商", Status = 1 };

    private static BaseEmployee NewEmployee() => new()
    { EmployeeCode = "E-ENTRY", EmployeeName = "入口测试员工", Status = 1 };

    private static BaseWarehouse NewWarehouse() => new()
    { WarehouseCode = "W-ENTRY", WarehouseName = "入口测试仓库", Status = 1 };

    private static BaseProduct NewProduct() => new()
    { ProductCode = "P-ENTRY", ProductName = "入口测试商品", Status = 1 };

    /// <summary>五类主表逐行快照（拒绝调用后必须逐字节不变）。</summary>
    private static string Snapshot(ErpDbContext db) => string.Join("||",
        db.BaseCustomers.AsNoTracking().OrderBy(x => x.Id)
            .Select(x => $"C{x.Id}:{x.Status}:{x.IsDeleted}:{x.CustomerCode}:{x.CustomerName}").ToList()) + "##" +
        string.Join("||", db.BaseSuppliers.AsNoTracking().OrderBy(x => x.Id)
            .Select(x => $"S{x.Id}:{x.Status}:{x.IsDeleted}:{x.SupplierCode}:{x.SupplierName}").ToList()) + "##" +
        string.Join("||", db.BaseEmployees.AsNoTracking().OrderBy(x => x.Id)
            .Select(x => $"E{x.Id}:{x.Status}:{x.IsDeleted}:{x.EmployeeCode}:{x.EmployeeName}").ToList()) + "##" +
        string.Join("||", db.BaseWarehouses.AsNoTracking().OrderBy(x => x.Id)
            .Select(x => $"W{x.Id}:{x.Status}:{x.IsDeleted}:{x.WarehouseCode}:{x.WarehouseName}").ToList()) + "##" +
        string.Join("||", db.BaseProducts.AsNoTracking().OrderBy(x => x.Id)
            .Select(x => $"P{x.Id}:{x.Status}:{x.IsDeleted}:{x.ProductCode}:{x.ProductName}").ToList());

    /// <summary>播种五类主表各一行历史数据（拒绝调用后快照必须逐字节不变）。</summary>
    private static void SeedRows(ErpDbContext db)
    {
        db.BaseCustomers.Add(new BaseCustomer { CustomerCode = "C-EXIST", CustomerName = "既有客户", Status = 1 });
        db.BaseSuppliers.Add(new BaseSupplier { SupplierCode = "S-EXIST", SupplierName = "既有供应商", Status = 1 });
        db.BaseEmployees.Add(new BaseEmployee { EmployeeCode = "E-EXIST", EmployeeName = "既有员工", Status = 1 });
        db.BaseWarehouses.Add(new BaseWarehouse { WarehouseCode = "W-EXIST", WarehouseName = "既有仓库", Status = 1 });
        db.BaseProducts.Add(new BaseProduct { ProductCode = "P-EXIST", ProductName = "既有商品", Status = 1 });
        db.SaveChanges();
    }

    /// <summary>五类既有菜单（与各 AuthorizationRules 同源，不新增菜单）。</summary>
    private static (string Code, string Text) CustomerMenu
        => (CustomerAuthorizationRules.RequiredMenuCode, CustomerAuthorizationRules.RequiredMenuText);
    private static (string Code, string Text) SupplierMenu
        => (SupplierAuthorizationRules.RequiredMenuCode, SupplierAuthorizationRules.RequiredMenuText);
    private static (string Code, string Text) EmployeeMenu
        => (EmployeeAuthorizationRules.RequiredMenuCode, EmployeeAuthorizationRules.RequiredMenuText);
    private static (string Code, string Text) WarehouseMenu
        => (WarehouseAuthorizationRules.RequiredMenuCode, WarehouseAuthorizationRules.RequiredMenuText);
    private static (string Code, string Text) ProductMenu
        => (ProductAuthorizationRules.RequiredMenuCode, ProductAuthorizationRules.RequiredMenuText);

    /// <summary>为五类控制器各播种一个具备既有菜单的授权身份，返回（customer, supplier, employee, warehouse, product）用户 Id。</summary>
    private static (long Customer, long Supplier, long Employee, long Warehouse, long Product) SeedAllAuthorized(ErpDbContext db)
    {
        var c = CustomerMenu; var s = SupplierMenu; var e = EmployeeMenu; var w = WarehouseMenu; var p = ProductMenu;
        return (SeedActor(db, c.Code, c.Text), SeedActor(db, s.Code, s.Text), SeedActor(db, e.Code, e.Text),
            SeedActor(db, w.Code, w.Text), SeedActor(db, p.Code, p.Text));
    }

    /// <summary>为五类控制器各播种一个<b>无既有菜单</b>的启用身份。</summary>
    private static (long Customer, long Supplier, long Employee, long Warehouse, long Product) SeedAllNoMenu(ErpDbContext db)
    {
        var c = CustomerMenu; var s = SupplierMenu; var e = EmployeeMenu; var w = WarehouseMenu; var p = ProductMenu;
        return (SeedActor(db, c.Code, c.Text, withMenu: false), SeedActor(db, s.Code, s.Text, withMenu: false),
            SeedActor(db, e.Code, e.Text, withMenu: false), SeedActor(db, w.Code, w.Text, withMenu: false),
            SeedActor(db, p.Code, p.Text, withMenu: false));
    }

    /// <summary>为五类控制器各播种一个指定状态 / 删除形态的身份（均携带既有菜单）。</summary>
    private static (long Customer, long Supplier, long Employee, long Warehouse, long Product) SeedAllWithState(
        ErpDbContext db, UserStatus status, bool deleted)
    {
        var c = CustomerMenu; var s = SupplierMenu; var e = EmployeeMenu; var w = WarehouseMenu; var p = ProductMenu;
        return (SeedActor(db, c.Code, c.Text, status, deleted), SeedActor(db, s.Code, s.Text, status, deleted),
            SeedActor(db, e.Code, e.Text, status, deleted), SeedActor(db, w.Code, w.Text, status, deleted),
            SeedActor(db, p.Code, p.Text, status, deleted));
    }

    /// <summary>逐类按各自账号断言拒绝（五类控制器身份独立，避免串扰）。</summary>
    private static async Task AssertAllDeniedPerControllerAsync(ErpDbContext db,
        (long Customer, long Supplier, long Employee, long Warehouse, long Product) actors,
        string pathMode, int expectedCode)
    {
        await AssertCode(expectedCode, () => Customer(db, actors.Customer, pathMode).GetPaged(new PageQuery()));
        await AssertCode(expectedCode, () => Supplier(db, actors.Supplier, pathMode).GetPaged(new PageQuery()));
        await AssertCode(expectedCode, () => Employee(db, actors.Employee, pathMode).GetPaged(new PageQuery()));
        await AssertCode(expectedCode, () => Warehouse(db, actors.Warehouse, pathMode).GetPaged(new PageQuery()));
        await AssertCode(expectedCode, () => Product(db, actors.Product, pathMode).GetPaged(new PageQuery()));
    }


    // ==================== 2. 路径 / 上下文无关的拒绝矩阵 ====================

    /// <summary>空路径与已赋值路径：缺少身份一律未认证（逐入口，五类控制器）。</summary>
    [Theory]
    [InlineData(PathModeEmpty)]
    [InlineData(PathModePopulated)]
    public async Task Entry_空路径与已赋值路径_缺少身份一律未认证(string pathMode)
    {
        using var db = TestDbFactory.Create();
        await AssertAllDeniedAsync(db, null, pathMode, ErrorCodes.Unauthorized);
    }

    /// <summary>完全未绑定 <c>HttpContext</c>（纯进程内直调）：同样执行实时授权，缺少身份一律未认证。</summary>
    [Fact]
    public async Task Entry_完全未绑定HttpContext_一律未认证()
    {
        using var db = TestDbFactory.Create();
        await AssertAllDeniedAsync(db, null, PathModeNoContext, ErrorCodes.Unauthorized);
    }

    /// <summary>零身份（<c>0</c>）在空路径 / 已赋值路径上一致未认证。</summary>
    [Theory]
    [InlineData(PathModeEmpty)]
    [InlineData(PathModePopulated)]
    public async Task Entry_零身份_一律未认证(string pathMode)
    {
        using var db = TestDbFactory.Create();
        await AssertAllDeniedAsync(db, 0L, pathMode, ErrorCodes.Unauthorized);
    }

    /// <summary>未知身份（账号不存在）在空路径 / 已赋值路径上一致未认证。</summary>
    [Theory]
    [InlineData(PathModeEmpty)]
    [InlineData(PathModePopulated)]
    public async Task Entry_未知身份_一律未认证(string pathMode)
    {
        using var db = TestDbFactory.Create();
        await AssertAllDeniedAsync(db, 987_654_321L, pathMode, ErrorCodes.Unauthorized);
    }

    /// <summary>已删除身份（软删除）在空路径 / 已赋值路径上一致未认证。</summary>
    [Theory]
    [InlineData(PathModeEmpty)]
    [InlineData(PathModePopulated)]
    public async Task Entry_已删除身份_一律未认证(string pathMode)
    {
        using var db = TestDbFactory.Create();
        var actors = SeedAllWithState(db, UserStatus.Enabled, deleted: true);
        await AssertAllDeniedPerControllerAsync(db, actors, pathMode, ErrorCodes.Unauthorized);
    }

    /// <summary>禁用账号在空路径 / 已赋值路径上一致权限不足。</summary>
    [Theory]
    [InlineData(PathModeEmpty)]
    [InlineData(PathModePopulated)]
    public async Task Entry_禁用账号_一律权限不足(string pathMode)
    {
        using var db = TestDbFactory.Create();
        var actors = SeedAllWithState(db, UserStatus.Disabled, deleted: false);
        await AssertAllDeniedPerControllerAsync(db, actors, pathMode, ErrorCodes.Forbidden);
    }

    /// <summary>缺少既有菜单在空路径 / 已赋值路径上一致权限不足。</summary>
    [Theory]
    [InlineData(PathModeEmpty)]
    [InlineData(PathModePopulated)]
    public async Task Entry_缺少既有菜单_一律权限不足(string pathMode)
    {
        using var db = TestDbFactory.Create();
        var actors = SeedAllNoMenu(db);
        await AssertAllDeniedPerControllerAsync(db, actors, pathMode, ErrorCodes.Forbidden);
    }

    /// <summary>被拒绝（未认证 / 权限不足）的调用对五类主表零写入。</summary>
    [Fact]
    public async Task Entry_被拒绝调用_五类主表零写入()
    {
        using var db = TestDbFactory.Create();
        SeedRows(db);
        var before = Snapshot(db);

        await AssertAllDeniedAsync(db, null, PathModeEmpty, ErrorCodes.Unauthorized);
        await AssertAllDeniedAsync(db, null, PathModeNoContext, ErrorCodes.Unauthorized);
        var actors = SeedAllNoMenu(db);
        await AssertAllDeniedPerControllerAsync(db, actors, PathModePopulated, ErrorCodes.Forbidden);

        Assert.Equal(before, Snapshot(db));
    }


    // ==================== 3. 请求之间撤销菜单立即收敛 ====================

    /// <summary>请求之间撤销既有菜单：空路径与已赋值路径的下一次请求立即权限不足，五类主表零写入。</summary>
    [Fact]
    public async Task Entry_请求之间撤销菜单_两种路径形状下一次请求立即拒绝()
    {
        using var db = TestDbFactory.Create();
        SeedRows(db);
        var actors = SeedAllAuthorized(db);

        foreach (var pathMode in new[] { PathModeEmpty, PathModePopulated })
        {
            Data<PagedResult<BaseCustomer>>(await Customer(db, actors.Customer, pathMode).GetPaged(new PageQuery()));
            Data<PagedResult<BaseSupplier>>(await Supplier(db, actors.Supplier, pathMode).GetPaged(new PageQuery()));
            Data<PagedResult<BaseEmployee>>(await Employee(db, actors.Employee, pathMode).GetPaged(new PageQuery()));
            Data<PagedResult<BaseWarehouse>>(await Warehouse(db, actors.Warehouse, pathMode).GetPaged(new PageQuery()));
            Data<PagedResult<BaseProduct>>(await Product(db, actors.Product, pathMode).GetPaged(new PageQuery()));
        }

        RevokeMenu(db, actors.Customer, CustomerMenu.Code);
        RevokeMenu(db, actors.Supplier, SupplierMenu.Code);
        RevokeMenu(db, actors.Employee, EmployeeMenu.Code);
        RevokeMenu(db, actors.Warehouse, WarehouseMenu.Code);
        RevokeMenu(db, actors.Product, ProductMenu.Code);

        var before = Snapshot(db);
        foreach (var pathMode in new[] { PathModeEmpty, PathModePopulated })
            await AssertAllDeniedPerControllerAsync(db, actors, pathMode, ErrorCodes.Forbidden);
        Assert.Equal(before, Snapshot(db));
    }

    // ==================== 4. 授权身份下既有生命周期保持不变 ====================

    /// <summary>
    /// 具备既有菜单的真实启用身份（特权数据范围）：在<b>空路径</b>与<b>已赋值路径</b>上完成
    /// 新增 → 分页 → 按主键 → 修改 → 删除 / 批量删除的既有生命周期，两种请求形状判定完全一致。
    /// </summary>
    [Fact]
    public async Task Entry_授权身份_空路径与已赋值路径完成既有生命周期()
    {
        using var db = TestDbFactory.Create();
        var actors = SeedAllAuthorized(db);

        foreach (var pathMode in new[] { PathModeEmpty, PathModePopulated })
        {
            var customer = Customer(db, actors.Customer, pathMode);
            var createdCustomer = Data<BaseCustomer>(await customer.Create(new BaseCustomer
            { CustomerCode = $"C-{pathMode}", CustomerName = "入口客户", Status = 1 }));
            Assert.Contains(Data<PagedResult<BaseCustomer>>(await customer.GetPaged(new PageQuery())).Items,
                x => x.Id == createdCustomer.Id);
            Assert.Equal(createdCustomer.Id, Data<BaseCustomer>(await customer.GetById(createdCustomer.Id)).Id);
            await customer.Delete(createdCustomer.Id);

            var supplier = Supplier(db, actors.Supplier, pathMode);
            var createdSupplier = Data<BaseSupplier>(await supplier.Create(new BaseSupplier
            { SupplierCode = $"S-{pathMode}", SupplierName = "入口供应商", Status = 1 }));
            Assert.Contains(Data<PagedResult<BaseSupplier>>(await supplier.GetPaged(new PageQuery())).Items,
                x => x.Id == createdSupplier.Id);
            await supplier.Update(createdSupplier.Id, new BaseSupplier
            { Id = createdSupplier.Id, SupplierCode = $"S-{pathMode}", SupplierName = "入口供应商改", Status = 1 });
            await supplier.BatchDelete(new List<long> { createdSupplier.Id });

            var employee = Employee(db, actors.Employee, pathMode);
            var createdEmployee = Data<BaseEmployee>(await employee.Create(new BaseEmployee
            { EmployeeCode = $"E-{pathMode}", EmployeeName = "入口员工", IsSalesman = true, Status = 1 }));
            Assert.Contains(Data<List<BaseEmployee>>(await employee.GetSalesmen()), x => x.Id == createdEmployee.Id);
            await employee.Delete(createdEmployee.Id);

            var warehouse = Warehouse(db, actors.Warehouse, pathMode);
            var createdWarehouse = Data<BaseWarehouse>(await warehouse.Create(new BaseWarehouse
            { WarehouseCode = $"W-{pathMode}", WarehouseName = "入口仓库", Status = 1 }));
            Assert.Contains(Data<List<BaseWarehouse>>(await warehouse.GetAll()), x => x.Id == createdWarehouse.Id);
            await warehouse.Delete(createdWarehouse.Id);

            var product = Product(db, actors.Product, pathMode);
            var createdProduct = Data<BaseProduct>(await product.Create(new BaseProduct
            { ProductCode = $"P-{pathMode}", ProductName = "入口商品", Status = 1 }));
            Assert.Contains(Data<List<BaseProduct>>(await product.GetAll()), x => x.Id == createdProduct.Id);

            // 授权通过后图片上传进入既有文件校验（非空非法扩展名 → 参数错误，不触达 OSS）。
            await AssertCode(ErrorCodes.InvalidParameter,
                () => product.Upload(new FakeFormFile("note.txt", "text/plain", new byte[] { 1, 2, 3 })));

            await product.Delete(createdProduct.Id);
    }
}
    // ==================== 5. 授权口径与源码契约 ====================

    /// <summary>授权口径只复用既有五类菜单，且契约明确声明与请求路径 / 上下文无关。</summary>
    [Fact]
    public void Entry_授权口径复用既有五类菜单且声明路径无关()
    {
        Assert.Equal("customer", CustomerAuthorizationRules.RequiredMenuCode);
        Assert.Equal("supplier", SupplierAuthorizationRules.RequiredMenuCode);
        Assert.Equal("employee", EmployeeAuthorizationRules.RequiredMenuCode);
        Assert.Equal("warehouse", WarehouseAuthorizationRules.RequiredMenuCode);
        Assert.Equal("product", ProductAuthorizationRules.RequiredMenuCode);

        foreach (var text in new[]
                 {
                     CustomerAuthorizationRules.PathIndependenceText, SupplierAuthorizationRules.PathIndependenceText,
                     EmployeeAuthorizationRules.PathIndependenceText, WarehouseAuthorizationRules.PathIndependenceText,
                     ProductAuthorizationRules.PathIndependenceText
                 })
        {
            Assert.Contains("与请求路径", text);
            Assert.Contains("未绑定任何请求上下文", text);
            Assert.Contains("未认证", text);
            Assert.Contains("权限不足", text);
        }
    }

    /// <summary>控制器源码契约：五类控制器全部无条件先授权再读写，且不含请求路径 / 环境 / 测试专用开关。</summary>
    [Fact]
    public void Entry_控制器源码契约_与请求形状无关且无兜底()
    {
        var source = File.ReadAllText(RepoFile("src", "ERP.Api", "Controllers", "BaseDataControllers.cs"));

        Assert.DoesNotContain("Request.Path", source);
        Assert.DoesNotContain("RequiresLiveAuthorization", source);
        Assert.DoesNotContain("AllowAnonymous", source);
        Assert.DoesNotContain("[Authorize(Roles", source);
        Assert.DoesNotContain("Environment.GetEnvironmentVariable", source);
        Assert.DoesNotContain("IsInMemory", source);

        Assert.Equal(8, System.Text.RegularExpressions.Regex.Matches(
            source, @"await EnsureCustomerAuthorizedAsync\(\);").Count);
        Assert.Equal(7, System.Text.RegularExpressions.Regex.Matches(
            source, @"await EnsureAuthorizedAsync\(\);").Count);
        Assert.Equal(8, System.Text.RegularExpressions.Regex.Matches(
            source, @"await EnsureEmployeeAuthorizedAsync\(\);").Count);
        Assert.Equal(7, System.Text.RegularExpressions.Regex.Matches(
            source, @"await EnsureWarehouseAuthorizedAsync\(\);").Count);
        Assert.Equal(10, System.Text.RegularExpressions.Regex.Matches(
            source, @"await EnsureProductAuthorizedAsync\(\);").Count);
    }

    /// <summary>按仓库根目录拼接文件的绝对路径（与其它契约测试口径一致）。</summary>
    private static string RepoFile(params string[] segments)
        => Path.GetFullPath(Path.Combine(
            new[] { AppContext.BaseDirectory, "..", "..", "..", "..", ".." }.Concat(segments).ToArray()));

    /// <summary>测试用宿主环境：内容根指向仓库目录，仅用于构造 ProductController（不会读写文件）。</summary>
    private sealed class FakeWebHostEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "ERP.UnitTests";
        public string EnvironmentName { get; set; } = "Development";
        public string ContentRootPath { get; set; } = Directory.GetCurrentDirectory();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = Directory.GetCurrentDirectory();
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }


    /// <summary>测试用内存 <see cref="IFormFile"/>（授权通过后仅用于触发既有文件校验，不触达 OSS）。</summary>
    private sealed class FakeFormFile : IFormFile
    {
        private readonly byte[] _content;

        public FakeFormFile(string fileName, string contentType, byte[] content)
        {
            FileName = fileName;
            ContentType = contentType;
            _content = content;
        }

        public string ContentType { get; }
        public string ContentDisposition => $"form-data; name=\"file\"; filename=\"{FileName}\"";
        public IHeaderDictionary Headers { get; set; } = new HeaderDictionary();
        public long Length => _content.Length;
        public string Name => "file";
        public string FileName { get; }
        public Stream OpenReadStream() => new MemoryStream(_content, writable: false);
        public void CopyTo(Stream target) => target.Write(_content, 0, _content.Length);
        public Task CopyToAsync(Stream target, CancellationToken cancellationToken = default)
            => target.WriteAsync(_content, 0, _content.Length, cancellationToken);
    }
}
