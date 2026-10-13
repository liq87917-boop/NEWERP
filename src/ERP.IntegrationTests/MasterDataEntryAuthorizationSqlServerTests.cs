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
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using System.Security.Claims;
using Xunit;

namespace ERP.IntegrationTests;

/// <summary>
/// ERP-467 主数据（客户 / 供应商 / 员工 / 仓库 / 商品）<b>入口授权</b>的真实 SQL Server 集成测试
/// （GUID 独占 <c>NEWERP_AUTOTEST</c> 目标，绝不 drop / reset / 复用）。
/// <list type="number">
/// <item><b>路径无关</b>：空路径 / 已赋值路径 / 完全未绑定 <c>HttpContext</c> 三种形状对同一身份给出完全一致的判定；</item>
/// <item><b>实时收敛</b>：缺失 / 零 / 已删除身份 → 未认证，禁用 / 撤销或缺少既有菜单 → 权限不足；</item>
/// <item><b>零写入</b>：被拒绝的调用不新增 / 不改写任何主数据行；</item>
/// <item><b>既有生命周期不变</b>：真实既有启用身份 + 既有菜单下，既有新增 / 读取 / 修改 / 软删除保持。</item>
/// </list>
/// <para>安全口径：目标必须为专用 localdb 实例 <c>NEWERP_AutoAcceptance</c>、库名前缀 <c>NEWERP_AUTOTEST</c>
/// 且集成安全；每次运行只创建全新 GUID 后缀库，发现同名库已存在立即拒绝；绝不 drop / reset / 复用任何数据库，
/// 连接串只来自进程环境变量或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据。</para>
/// </summary>
public sealed class MasterDataEntryAuthorizationSqlServerTests
    : IClassFixture<MasterDataEntryAuthorizationSqlServerFixture>
{
    private readonly MasterDataEntryAuthorizationSqlServerFixture _fixture;

    public MasterDataEntryAuthorizationSqlServerTests(MasterDataEntryAuthorizationSqlServerFixture fixture)
        => _fixture = fixture;

    private void Guard()
    {
        var target = new SqlConnectionStringBuilder(_fixture.ConnectionString);
        Assert.Equal("(localdb)\\NEWERP_AutoAcceptance", target.DataSource, ignoreCase: true);
        Assert.StartsWith(MasterDataEntryAuthorizationSqlServerFixture.DatabasePrefix,
            target.InitialCatalog, StringComparison.OrdinalIgnoreCase);
        Assert.True(target.IntegratedSecurity);
    }

    private static string Tag() => Guid.NewGuid().ToString("N")[..8];

    private static async Task AssertCodeAsync(int expected, Func<Task<IActionResult>> action)
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

    private static IConfiguration BuildOssConfiguration() =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Oss:AccessKeyId"] = "test-key-id",
            ["Oss:AccessKeySecret"] = "test-secret",
            ["Oss:Bucket"] = "test-bucket",
            ["Oss:Endpoint"] = "oss-cn-hangzhou.aliyuncs.com"
        }).Build();

    /// <summary>按请求形状绑定控制器：<c>empty</c> = 不设置 <c>Request.Path</c>；<c>populated</c> = 赋值真实路由；
    /// <c>no-context</c> = 完全不绑定 <c>HttpContext</c>（纯进程内直调）。三种形状必须给出完全一致的判定。</summary>
    private static ControllerContext? Context(long? userId, string pathMode, string populatedPath)
    {
        if (pathMode == "no-context") return null;
        var claims = userId.HasValue
            ? new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }
            : Array.Empty<Claim>();
        var http = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) };
        if (pathMode == "populated") http.Request.Path = populatedPath;
        return new ControllerContext { HttpContext = http };
    }

    private static CustomerController Customer(ErpDbContext db, long? userId, string pathMode)
    {
        var controller = new CustomerController(new GenericService<BaseCustomer>(db), db);
        var context = Context(userId, pathMode, "/api/base/customers");
        if (context is not null) controller.ControllerContext = context;
        return controller;
    }

    private static SupplierController Supplier(ErpDbContext db, long? userId, string pathMode)
    {
        var controller = new SupplierController(new GenericService<BaseSupplier>(db), db);
        var context = Context(userId, pathMode, "/api/base/suppliers");
        if (context is not null) controller.ControllerContext = context;
        return controller;
    }

    private static EmployeeController Employee(ErpDbContext db, long? userId, string pathMode)
    {
        var controller = new EmployeeController(new GenericService<BaseEmployee>(db), db);
        var context = Context(userId, pathMode, "/api/base/employees");
        if (context is not null) controller.ControllerContext = context;
        return controller;
    }

    private static WarehouseController Warehouse(ErpDbContext db, long? userId, string pathMode)
    {
        var controller = new WarehouseController(new GenericService<BaseWarehouse>(db), db);
        var context = Context(userId, pathMode, "/api/base/warehouses");
        if (context is not null) controller.ControllerContext = context;
        return controller;
    }

    private static ProductController Product(ErpDbContext db, long? userId, string pathMode)
    {
        var controller = new ProductController(
            new GenericService<BaseProduct>(db), new OssStorageService(BuildOssConfiguration()),
            new ProductExcelExporter(db), new FakeWebHostEnvironment(), db);
        var context = Context(userId, pathMode, "/api/base/products");
        if (context is not null) controller.ControllerContext = context;
        return controller;
    }

    private static ExpenseAccountController ExpenseAccount(ErpDbContext db, long? userId, string pathMode)
    {
        var controller = new ExpenseAccountController(new GenericService<BaseExpenseAccount>(db), db);
        var context = Context(userId, pathMode, "/api/base/expense-accounts");
        if (context is not null) controller.ControllerContext = context;
        return controller;
    }

    /// <summary>按控制器名返回其分页入口调用（用于身份矩阵）。</summary>
    private static Func<Task<IActionResult>> Paged(string controller, ErpDbContext db, long? userId, string pathMode)
        => controller switch
        {
            "customer" => () => Customer(db, userId, pathMode).GetPaged(new PageQuery { Page = 1, PageSize = 20 }),
            "supplier" => () => Supplier(db, userId, pathMode).GetPaged(new PageQuery { Page = 1, PageSize = 20 }),
            "employee" => () => Employee(db, userId, pathMode).GetPaged(new PageQuery { Page = 1, PageSize = 20 }),
            "warehouse" => () => Warehouse(db, userId, pathMode).GetPaged(new PageQuery { Page = 1, PageSize = 20 }),
            "product" => () => Product(db, userId, pathMode).GetPaged(new PageQuery { Page = 1, PageSize = 20 }),
            "expense-account" => () => ExpenseAccount(db, userId, pathMode).GetPaged(new PageQuery { Page = 1, PageSize = 20 }),
            _ => throw new ArgumentOutOfRangeException(nameof(controller), controller, null)
        };

    private static (string Code, string Text) Menu(string controller) => controller switch
    {
        "customer" => (CustomerAuthorizationRules.RequiredMenuCode, CustomerAuthorizationRules.RequiredMenuText),
        "supplier" => (SupplierAuthorizationRules.RequiredMenuCode, SupplierAuthorizationRules.RequiredMenuText),
        "employee" => (EmployeeAuthorizationRules.RequiredMenuCode, EmployeeAuthorizationRules.RequiredMenuText),
        "warehouse" => (WarehouseAuthorizationRules.RequiredMenuCode, WarehouseAuthorizationRules.RequiredMenuText),
        "product" => (ProductAuthorizationRules.RequiredMenuCode, ProductAuthorizationRules.RequiredMenuText),
        "expense-account" => (ExpenseAccountAuthorizationRules.RequiredMenuCode, ExpenseAccountAuthorizationRules.RequiredMenuText),
        _ => throw new ArgumentOutOfRangeException(nameof(controller), controller, null)
    };

    // ==================== 播种 / 快照辅助 ====================

    private static async Task<long> SeedActorAsync(ErpDbContext db, (string Code, string Text) menu,
        UserStatus status = UserStatus.Enabled, bool deleted = false, bool withMenu = true, bool revoked = false)
    {
        var user = new SysUser
        {
            UserName = $"mdea-{Tag()}",
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = "集成入口授权账号",
            Status = status,
            IsDeleted = deleted
        };
        db.SysUsers.Add(user);
        await db.SaveChangesAsync();

        var role = new SysRole { RoleCode = $"MDEA-{Tag()}", RoleName = "集成入口授权角色", IsSystem = true };
        db.SysRoles.Add(role);
        await db.SaveChangesAsync();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        await db.SaveChangesAsync();

        if (withMenu) await GrantMenuAsync(db, role.Id, menu.Code, menu.Text, revoked);
        return user.Id;
    }

    /// <summary>授予既有功能菜单（真实「角色 → 菜单」口径；菜单缺失时按既有种子口径补建，不新增生产菜单）。</summary>
    private static async Task GrantMenuAsync(ErpDbContext db, long roleId, string code, string text, bool revoked = false)
    {
        var menu = await db.SysMenus.FirstOrDefaultAsync(m => m.MenuCode == code && !m.IsDeleted);
        if (menu is null)
        {
            menu = new SysMenu { MenuCode = code, MenuName = text, MenuType = MenuType.Menu };
            db.SysMenus.Add(menu);
            await db.SaveChangesAsync();
        }
        if (!await db.SysRoleMenus.AnyAsync(rm => rm.RoleId == roleId && rm.MenuId == menu.Id && !rm.IsDeleted))
        {
            db.SysRoleMenus.Add(new SysRoleMenu { RoleId = roleId, MenuId = menu.Id, IsDeleted = revoked });
            await db.SaveChangesAsync();
        }
    }

    /// <summary>按身份场景播种对应账号；<c>missing</c> = 无身份，<c>zero</c> = 非法零身份。</summary>
    private static async Task<long?> SeedActorForAsync(ErpDbContext db, string controller, string identity)
    {
        var menu = Menu(controller);
        return identity switch
        {
            "missing" => null,
            "zero" => 0L,
            "deleted" => await SeedActorAsync(db, menu, UserStatus.Enabled, deleted: true),
            "disabled" => await SeedActorAsync(db, menu, UserStatus.Disabled, deleted: false),
            "no-menu" => await SeedActorAsync(db, menu, UserStatus.Enabled, deleted: false, withMenu: false),
            "revoked" => await SeedActorAsync(db, menu, UserStatus.Enabled, deleted: false, revoked: true),
            _ => await SeedActorAsync(db, menu, UserStatus.Enabled, deleted: false)
        };
    }

    /// <summary>六类主表逐行快照（拒绝调用后必须逐字节不变）。</summary>
    private static async Task<string> SnapshotAsync(ErpDbContext db)
    {
        var customers = await db.BaseCustomers.AsNoTracking().OrderBy(x => x.Id)
            .Select(x => $"C{x.Id}:{x.Status}:{x.IsDeleted}:{x.CustomerCode}:{x.CustomerName}").ToListAsync();
        var suppliers = await db.BaseSuppliers.AsNoTracking().OrderBy(x => x.Id)
            .Select(x => $"S{x.Id}:{x.Status}:{x.IsDeleted}:{x.SupplierCode}:{x.SupplierName}").ToListAsync();
        var employees = await db.BaseEmployees.AsNoTracking().OrderBy(x => x.Id)
            .Select(x => $"E{x.Id}:{x.Status}:{x.IsDeleted}:{x.EmployeeCode}:{x.EmployeeName}").ToListAsync();
        var warehouses = await db.BaseWarehouses.AsNoTracking().OrderBy(x => x.Id)
            .Select(x => $"W{x.Id}:{x.Status}:{x.IsDeleted}:{x.WarehouseCode}:{x.WarehouseName}").ToListAsync();
        var products = await db.BaseProducts.AsNoTracking().OrderBy(x => x.Id)
            .Select(x => $"P{x.Id}:{x.Status}:{x.IsDeleted}:{x.ProductCode}:{x.ProductName}").ToListAsync();
        var expenseAccounts = await db.BaseExpenseAccounts.AsNoTracking().OrderBy(x => x.Id)
            .Select(x => $"EA{x.Id}:{x.Status}:{x.IsDeleted}:{x.AccountCode}:{x.AccountName}").ToListAsync();
        return string.Join("||", customers) + "##" + string.Join("||", suppliers) + "##" + string.Join("||", employees)
            + "##" + string.Join("||", warehouses) + "##" + string.Join("||", products)
            + "##" + string.Join("||", expenseAccounts);
    }

    /// <summary>播种六类主表各一行历史数据（拒绝调用后快照必须逐字节不变）。</summary>
    private static async Task SeedRowsAsync(ErpDbContext db)
    {
        db.BaseCustomers.Add(new BaseCustomer { CustomerCode = $"C-EXIST-{Tag()}", CustomerName = "既有客户", Status = 1 });
        db.BaseSuppliers.Add(new BaseSupplier { SupplierCode = $"S-EXIST-{Tag()}", SupplierName = "既有供应商", Status = 1 });
        db.BaseEmployees.Add(new BaseEmployee { EmployeeCode = $"E-EXIST-{Tag()}", EmployeeName = "既有员工", Status = 1 });
        db.BaseWarehouses.Add(new BaseWarehouse { WarehouseCode = $"W-EXIST-{Tag()}", WarehouseName = "既有仓库", Status = 1 });
        db.BaseProducts.Add(new BaseProduct { ProductCode = $"P-EXIST-{Tag()}", ProductName = "既有商品", Status = 1 });
        db.BaseExpenseAccounts.Add(new BaseExpenseAccount
        { AccountCode = $"EA-EXIST-{Tag()}", AccountName = "既有费用科目", AccountType = 2, Status = 1 });
        await db.SaveChangesAsync();
    }

    // ==================== 1. 身份矩阵（空路径 / 已赋值路径完全一致） ====================

    public static IEnumerable<object[]> EntryMatrix()
    {
        foreach (var controller in new[] { "customer", "supplier", "employee", "warehouse", "product", "expense-account" })
            foreach (var pathMode in new[] { "empty", "populated" })
            {
                yield return new object[] { controller, pathMode, "missing", ErrorCodes.Unauthorized };
                yield return new object[] { controller, pathMode, "zero", ErrorCodes.Unauthorized };
                yield return new object[] { controller, pathMode, "deleted", ErrorCodes.Unauthorized };
                yield return new object[] { controller, pathMode, "disabled", ErrorCodes.Forbidden };
                yield return new object[] { controller, pathMode, "no-menu", ErrorCodes.Forbidden };
                yield return new object[] { controller, pathMode, "revoked", ErrorCodes.Forbidden };
                yield return new object[] { controller, pathMode, "genuine", 0 };
            }
    }

    /// <summary>五类控制器的分页入口在空路径 / 已赋值路径下对同一身份给出完全一致的判定。</summary>
    [Theory]
    [MemberData(nameof(EntryMatrix))]
    public async Task Read_entries_enforce_identical_authority_on_all_paths(
        string controller, string pathMode, string identity, int expectedCode)
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var userId = await SeedActorForAsync(db, controller, identity);
        var call = Paged(controller, db, userId, pathMode);

        if (expectedCode == 0)
            Assert.NotNull(Assert.IsType<OkObjectResult>(await call()).Value);
        else
            await AssertCodeAsync(expectedCode, call);
    }

    // ==================== 2. 完全未绑定 HttpContext：一律 fail closed ====================

    /// <summary>纯进程内直调（完全没有 <c>HttpContext</c>）：五类控制器一律未认证且五类主表零写入。</summary>
    [Fact]
    public async Task Unbound_context_is_unauthorized_and_writes_nothing()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        await SeedRowsAsync(db);
        var before = await SnapshotAsync(db);

        foreach (var controller in new[] { "customer", "supplier", "employee", "warehouse", "product", "expense-account" })
            await AssertDeniedAsync(controller, db, null, "no-context", ErrorCodes.Unauthorized);

        Assert.Equal(before, await SnapshotAsync(db));
    }

    // ==================== 3. 被拒绝调用零写入 ====================

    /// <summary>缺少既有菜单的身份在空路径 / 已赋值路径上被拒绝且五类主表零写入。</summary>
    [Theory]
    [InlineData("empty")]
    [InlineData("populated")]
    public async Task Denied_calls_persist_zero_mutations(string pathMode)
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        await SeedRowsAsync(db);
        var before = await SnapshotAsync(db);

        foreach (var controller in new[] { "customer", "supplier", "employee", "warehouse", "product", "expense-account" })
        {
            var noMenu = await SeedActorAsync(db, Menu(controller), UserStatus.Enabled, deleted: false, withMenu: false);
            await AssertDeniedAsync(controller, db, noMenu, pathMode, ErrorCodes.Forbidden);
        }

        Assert.Equal(before, await SnapshotAsync(db));
    }

    // ==================== 4. 授权身份下既有生命周期保持不变 ====================

    /// <summary>具备既有菜单的真实启用身份：空路径与已赋值路径上完成既有新增 / 读取 / 软删除生命周期。</summary>
    [Theory]
    [InlineData("empty")]
    [InlineData("populated")]
    public async Task Permitted_identity_completes_existing_lifecycle(string pathMode)
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        foreach (var controller in new[] { "customer", "supplier", "employee", "warehouse", "product", "expense-account" })
        {
            var userId = await SeedActorAsync(db, Menu(controller), UserStatus.Enabled, deleted: false);
            await RunLifecycleAsync(controller, db, userId, pathMode);
        }
    }

    private static IFormFile Image()
        => new FakeFormFile("p.png", "image/png",
            new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x01 });

    /// <summary>逐类断言其分页 / 新增 / 修改 / 删除 / 批量删除（商品另含导出与图片上传）一律被拒绝。</summary>
    private static async Task AssertDeniedAsync(
        string controller, ErpDbContext db, long? userId, string pathMode, int code)
    {
        switch (controller)
        {
            case "customer":
                {
                    var ctl = Customer(db, userId, pathMode);
                    await AssertCodeAsync(code, () => ctl.GetPaged(new PageQuery()));
                    await AssertCodeAsync(code, () => ctl.Create(
                        new BaseCustomer { CustomerCode = $"C-{Tag()}", CustomerName = "拒绝客户", Status = 1 }));
                    await AssertCodeAsync(code, () => ctl.Update(1,
                        new BaseCustomer { CustomerCode = $"C-{Tag()}", CustomerName = "拒绝客户", Status = 1 }));
                    await AssertCodeAsync(code, () => ctl.Delete(1));
                    await AssertCodeAsync(code, () => ctl.BatchDelete(new List<long> { 1 }));
                    break;
                }
            case "supplier":
                {
                    var ctl = Supplier(db, userId, pathMode);
                    await AssertCodeAsync(code, () => ctl.GetPaged(new PageQuery()));
                    await AssertCodeAsync(code, () => ctl.Create(
                        new BaseSupplier { SupplierCode = $"S-{Tag()}", SupplierName = "拒绝供应商", Status = 1 }));
                    await AssertCodeAsync(code, () => ctl.Update(1,
                        new BaseSupplier { SupplierCode = $"S-{Tag()}", SupplierName = "拒绝供应商", Status = 1 }));
                    await AssertCodeAsync(code, () => ctl.Delete(1));
                    await AssertCodeAsync(code, () => ctl.BatchDelete(new List<long> { 1 }));
                    break;
                }
            case "employee":
                {
                    var ctl = Employee(db, userId, pathMode);
                    await AssertCodeAsync(code, () => ctl.GetPaged(new PageQuery()));
                    await AssertCodeAsync(code, () => ctl.GetSalesmen());
                    await AssertCodeAsync(code, () => ctl.Create(
                        new BaseEmployee { EmployeeCode = $"E-{Tag()}", EmployeeName = "拒绝员工", Status = 1 }));
                    await AssertCodeAsync(code, () => ctl.Update(1,
                        new BaseEmployee { EmployeeCode = $"E-{Tag()}", EmployeeName = "拒绝员工", Status = 1 }));
                    await AssertCodeAsync(code, () => ctl.Delete(1));
                    await AssertCodeAsync(code, () => ctl.BatchDelete(new List<long> { 1 }));
                    break;
                }
            case "warehouse":
                {
                    var ctl = Warehouse(db, userId, pathMode);
                    await AssertCodeAsync(code, () => ctl.GetPaged(new PageQuery()));
                    await AssertCodeAsync(code, () => ctl.Create(
                        new BaseWarehouse { WarehouseCode = $"W-{Tag()}", WarehouseName = "拒绝仓库", Status = 1 }));
                    await AssertCodeAsync(code, () => ctl.Update(1,
                        new BaseWarehouse { WarehouseCode = $"W-{Tag()}", WarehouseName = "拒绝仓库", Status = 1 }));
                    await AssertCodeAsync(code, () => ctl.Delete(1));
                    await AssertCodeAsync(code, () => ctl.BatchDelete(new List<long> { 1 }));
                    break;
                }
            case "product":
                {
                    var ctl = Product(db, userId, pathMode);
                    await AssertCodeAsync(code, () => ctl.GetPaged(new PageQuery()));
                    await AssertCodeAsync(code, () => ctl.Create(
                        new BaseProduct { ProductCode = $"P-{Tag()}", ProductName = "拒绝商品", Status = 1 }));
                    await AssertCodeAsync(code, () => ctl.Update(1,
                        new BaseProduct { ProductCode = $"P-{Tag()}", ProductName = "拒绝商品", Status = 1 }));
                    await AssertCodeAsync(code, () => ctl.Delete(1));
                    await AssertCodeAsync(code, () => ctl.BatchDelete(new List<long> { 1 }));
                    await AssertCodeAsync(code, () => ctl.Export());
                    await AssertCodeAsync(code, () => ctl.Upload(Image()));
                    await AssertCodeAsync(code, () => ctl.UploadBatch(new List<IFormFile> { Image() }));
                    break;
                }
            case "expense-account":
                {
                    var ctl = ExpenseAccount(db, userId, pathMode);
                    await AssertCodeAsync(code, () => ctl.GetPaged(new PageQuery()));
                    await AssertCodeAsync(code, () => ctl.Create(new BaseExpenseAccount
                    { AccountCode = $"EA-{Tag()}", AccountName = "拒绝费用科目", AccountType = 2, Status = 1 }));
                    await AssertCodeAsync(code, () => ctl.Update(1, new BaseExpenseAccount
                    { AccountCode = $"EA-{Tag()}", AccountName = "拒绝费用科目", AccountType = 2, Status = 1 }));
                    await AssertCodeAsync(code, () => ctl.Delete(1));
                    await AssertCodeAsync(code, () => ctl.BatchDelete(new List<long> { 1 }));
                    break;
                }
        }
    }

    /// <summary>授权身份下的既有新增 / 读取 / 修改 / 软删除生命周期（真实 SQL）。</summary>
    private static async Task RunLifecycleAsync(string controller, ErpDbContext db, long userId, string pathMode)
    {
        switch (controller)
        {
            case "customer":
                {
                    var created = Data<BaseCustomer>(await Customer(db, userId, pathMode).Create(
                        new BaseCustomer { CustomerCode = $"C-{Tag()}", CustomerName = "集成客户", Status = 1 }));
                    Assert.Contains(Data<PagedResult<BaseCustomer>>(
                            await Customer(db, userId, pathMode).GetPaged(new PageQuery { Page = 1, PageSize = 50 })).Items,
                        x => x.Id == created.Id);
                    Assert.Equal(created.Id, Data<BaseCustomer>(
                        await Customer(db, userId, pathMode).GetById(created.Id)).Id);
                    await Customer(db, userId, pathMode).Delete(created.Id);
                    Assert.True((await db.BaseCustomers.AsNoTracking().SingleAsync(x => x.Id == created.Id)).IsDeleted);
                    break;
                }
            case "supplier":
                {
                    var created = Data<BaseSupplier>(await Supplier(db, userId, pathMode).Create(
                        new BaseSupplier { SupplierCode = $"S-{Tag()}", SupplierName = "集成供应商", Status = 1 }));
                    Assert.Contains(Data<PagedResult<BaseSupplier>>(
                            await Supplier(db, userId, pathMode).GetPaged(new PageQuery { Page = 1, PageSize = 50 })).Items,
                        x => x.Id == created.Id);
                    await Supplier(db, userId, pathMode).BatchDelete(new List<long> { created.Id });
                    Assert.True((await db.BaseSuppliers.AsNoTracking().SingleAsync(x => x.Id == created.Id)).IsDeleted);
                    break;
                }
            case "employee":
                {
                    var created = Data<BaseEmployee>(await Employee(db, userId, pathMode).Create(
                        new BaseEmployee { EmployeeCode = $"E-{Tag()}", EmployeeName = "集成员工", IsSalesman = true, Status = 1 }));
                    Assert.Contains(Data<List<BaseEmployee>>(await Employee(db, userId, pathMode).GetSalesmen()),
                        x => x.Id == created.Id);
                    Assert.Equal(created.Id, Data<BaseEmployee>(
                        await Employee(db, userId, pathMode).GetById(created.Id)).Id);
                    await Employee(db, userId, pathMode).Delete(created.Id);
                    Assert.True((await db.BaseEmployees.AsNoTracking().SingleAsync(x => x.Id == created.Id)).IsDeleted);
                    break;
                }
            case "warehouse":
                {
                    var created = Data<BaseWarehouse>(await Warehouse(db, userId, pathMode).Create(
                        new BaseWarehouse { WarehouseCode = $"W-{Tag()}", WarehouseName = "集成仓库", Status = 1 }));
                    Assert.Contains(Data<List<BaseWarehouse>>(await Warehouse(db, userId, pathMode).GetAll()),
                        x => x.Id == created.Id);
                    await Warehouse(db, userId, pathMode).Delete(created.Id);
                    Assert.True((await db.BaseWarehouses.AsNoTracking().SingleAsync(x => x.Id == created.Id)).IsDeleted);
                    break;
                }
            case "product":
                {
                    var created = Data<BaseProduct>(await Product(db, userId, pathMode).Create(
                        new BaseProduct { ProductCode = $"P-{Tag()}", ProductName = "集成商品", Status = 1 }));
                    Assert.Contains(Data<List<BaseProduct>>(await Product(db, userId, pathMode).GetAll()),
                        x => x.Id == created.Id);
                    await Product(db, userId, pathMode).Delete(created.Id);
                    Assert.True((await db.BaseProducts.AsNoTracking().SingleAsync(x => x.Id == created.Id)).IsDeleted);
                    break;
                }
            case "expense-account":
                {
                    var created = Data<BaseExpenseAccount>(await ExpenseAccount(db, userId, pathMode).Create(
                        new BaseExpenseAccount
                        { AccountCode = $"EA-{Tag()}", AccountName = "集成费用科目", AccountType = 2, Status = 1 }));
                    Assert.Contains(Data<PagedResult<BaseExpenseAccount>>(
                            await ExpenseAccount(db, userId, pathMode).GetPaged(new PageQuery { Page = 1, PageSize = 50 })).Items,
                        x => x.Id == created.Id);
                    Assert.Contains(Data<List<BaseExpenseAccount>>(await ExpenseAccount(db, userId, pathMode).GetAll()),
                        x => x.Id == created.Id);
                    Assert.Equal(created.Id, Data<BaseExpenseAccount>(
                        await ExpenseAccount(db, userId, pathMode).GetById(created.Id)).Id);
                    await ExpenseAccount(db, userId, pathMode).Delete(created.Id);
                    Assert.True((await db.BaseExpenseAccounts.AsNoTracking()
                        .SingleAsync(x => x.Id == created.Id)).IsDeleted);
                    break;
                }
        }
    }

    /// <summary>测试用宿主环境：内容根指向仓库目录，仅用于构造 ProductController（不会读写文件）。</summary>
    private sealed class FakeWebHostEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "ERP.IntegrationTests";
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
/// <summary>
/// ERP-467 集成测试夹具：只创建一次性 GUID 独占的 <c>NEWERP_AUTOTEST</c> 库，绝不 drop / reset / 复用；
/// 访问数据库之前先复核目标必须为 <c>(localdb)\NEWERP_AutoAcceptance</c>、库名前缀 <c>NEWERP_AUTOTEST</c>
/// 且使用集成安全；发现同名库已存在立即拒绝，绝不读取 appsettings / .env / 生产凭据或生产数据。
/// </summary>
public sealed class MasterDataEntryAuthorizationSqlServerFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";

    /// <summary>本次运行新建的 GUID 独占库名（每次运行唯一，绝不复用既有库）。</summary>
    public static string DefaultDatabaseName { get; } =
        $"{DatabasePrefix}_MASTERDATAENTRYAUTH_{Guid.NewGuid():N}";

    public string ConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = BuildDefaultConnectionString();

        // 访问数据库之前先复核目标护栏（错误目标 fail closed）。
        AssertDedicatedTarget(connectionString);
        ConnectionString = connectionString;

        Console.WriteLine($"[ERP-467] 目标库护栏放行（实例 {InstanceMarker}，库名前缀 {DatabasePrefix}，集成安全）。");
        await InitialiseFreshDatabaseAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public ErpDbContext CreateDbContext() => new(BuildOptions());

    private DbContextOptions<ErpDbContext> BuildOptions()
        => new DbContextOptionsBuilder<ErpDbContext>().UseSqlServer(ConnectionString).Options;

    private static string BuildDefaultConnectionString()
        => $"Server=(localdb)\\{InstanceMarker};Initial Catalog={DefaultDatabaseName};" +
           "Integrated Security=true;TrustServerCertificate=true;";

    internal static void AssertDedicatedTarget(string connectionString)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        var server = builder.DataSource ?? string.Empty;
        var database = builder.InitialCatalog ?? string.Empty;

        Assert.Equal($"(localdb)\\{InstanceMarker}", server, ignoreCase: true);
        Assert.StartsWith(DatabasePrefix, database, StringComparison.OrdinalIgnoreCase);
        Assert.True(builder.IntegratedSecurity);
    }

    private async Task InitialiseFreshDatabaseAsync()
    {
        var builder = new SqlConnectionStringBuilder(ConnectionString);
        var database = builder.InitialCatalog;

        // 破坏性初始化前再次护栏：绝不使用生产回退。
        AssertDedicatedTarget(ConnectionString);

        var master = new SqlConnectionStringBuilder(ConnectionString) { InitialCatalog = "master" };
        await using (var conn = new SqlConnection(master.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            // 绝不销毁已存在的夹具库或其它调用方的数据库。
            cmd.CommandText = "SELECT DB_ID(@database)";
            cmd.Parameters.AddWithValue("@database", database);
            var existing = await cmd.ExecuteScalarAsync();
            if (existing is not null && existing != DBNull.Value)
                throw new InvalidOperationException(
                    "The isolated fixture database already exists; choose a fresh NEWERP_AUTOTEST database.");
        }

        await using (var db = CreateDbContext())
        {
            await db.Database.EnsureCreatedAsync();
            await SchemaUpgrader.EnsureUpgradedAsync(db);
            await SeedData.InitializeAsync(db);
            await SchemaUpgrader.EnsureUpgradedAsync(db);
        }

        Console.WriteLine("[ERP-467] 集成场景就绪：完整 NEWERP 结构 + 种子数据（含既有五类主数据菜单）。");
    }
}
/// <summary>目标库护栏单元级校验：非专用目标必须在访问数据库之前被拒绝。</summary>
public sealed class MasterDataEntryAuthorizationTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;User Id=sa;Password=secret")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(
            () => MasterDataEntryAuthorizationSqlServerFixture.AssertDedicatedTarget(connection));
}
