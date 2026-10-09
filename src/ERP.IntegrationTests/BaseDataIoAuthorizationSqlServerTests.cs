using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using ERP.Infrastructure.Export;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using Xunit;

namespace ERP.IntegrationTests;

/// <summary>
/// ERP-440 基础资料导入导出（<c>api/base/io</c>）实时身份 / 既有功能菜单 / 权威客户范围的真实
/// SQL Server 集成测试（GUID 独占 <c>NEWERP_AUTOTEST</c> 目标库）。
/// <list type="number">
/// <item>真实控制器：export / import-template / import 在读取 / 计数 / 写入任何行之前解析实时身份与既有功能菜单，
/// 缺失 / 已删除按未认证、禁用按权限不足、无菜单按权限不足，一律 fail closed 且不加载任何基础资料行；</item>
/// <item>真实身份 / 菜单：撤销既有功能菜单后下一次请求立即收敛为拒绝；</item>
/// <item>真实数据范围：受限制业务员客户导出只返回本人客户，导入越界客户行按失败行报告且不新增 /
/// 不改写任何客户；特权（种子管理员）保留既有全量导出并可按任意业务员新增客户；</item>
/// <item>拒绝路径断言客户 / 供应商 / 员工 / 商品行数均不变。</item>
/// </list>
/// <para>安全口径：目标必须为专用 localdb 实例 <c>NEWERP_AutoAcceptance</c>、库名前缀 <c>NEWERP_AUTOTEST</c>
/// 且集成安全；每次运行只创建全新 GUID 后缀库，发现同名库已存在立即拒绝；绝不 drop / reset / 复用任何数据库，
/// 连接串只来自进程环境变量或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据。</para>
/// </summary>
public sealed class BaseDataIoAuthorizationSqlServerTests
    : IClassFixture<BaseDataIoAuthorizationSqlServerFixture>
{
    private readonly BaseDataIoAuthorizationSqlServerFixture _fixture;

    public BaseDataIoAuthorizationSqlServerTests(BaseDataIoAuthorizationSqlServerFixture fixture)
        => _fixture = fixture;

    /// <summary>访问数据库前复核专用目标护栏（实例名 / 库名前缀 / 集成安全）。</summary>
    private void Guard()
    {
        var target = new SqlConnectionStringBuilder(_fixture.ConnectionString);
        Assert.Equal("(localdb)\\NEWERP_AutoAcceptance", target.DataSource, ignoreCase: true);
        Assert.StartsWith(BaseDataIoAuthorizationSqlServerFixture.DatabasePrefix, target.InitialCatalog,
            StringComparison.OrdinalIgnoreCase);
        Assert.True(target.IntegratedSecurity);
    }

    // ==================== 1. 身份 / 账号状态 / 菜单 fail closed ====================

    [Fact]
    public async Task Live_missing_identity_denies_every_route_without_loading_any_base_data_row()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        await SeedCustomerAsync(db, "无身份客户", empId: null);
        var before = await SnapshotAsync(db);
        var ctl = NewController(db, userId: null);

        await AssertCode(ErrorCodes.Unauthorized, () => ctl.Export("customers"));
        await AssertCode(ErrorCodes.Unauthorized, () => ctl.DownloadTemplate("customers"));
        await AssertCode(ErrorCodes.Unauthorized, () => ctl.Import("customers", CustomerFile(("IO-ANON", "无身份", null))));
        await AssertCode(ErrorCodes.Unauthorized, () => ctl.Export("suppliers"));

        await AssertUnchangedAsync(db, before);
    }

    [Fact]
    public async Task Live_disabled_is_forbidden_and_deleted_is_unauthenticated_with_no_row_loaded()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        await SeedCustomerAsync(db, "身份客户", empId: null);
        var disabled = await SeedUserAsync(db, UserStatus.Disabled, deleted: false);
        var deleted = await SeedUserAsync(db, UserStatus.Enabled, deleted: true);
        var before = await SnapshotAsync(db);

        await AssertCode(ErrorCodes.Forbidden, () => NewController(db, disabled).Export("customers"));
        await AssertCode(ErrorCodes.Forbidden, () => NewController(db, disabled).Import("customers", null));
        await AssertCode(ErrorCodes.Unauthorized, () => NewController(db, deleted).DownloadTemplate("customers"));
        await AssertCode(ErrorCodes.Unauthorized, () => NewController(db, deleted).Import("customers", null));

        await AssertUnchangedAsync(db, before);
    }

    [Fact]
    public async Task Live_missing_base_menu_is_forbidden_and_leaves_every_master_row_unchanged()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        await SeedCustomerAsync(db, "菜单客户", empId: null);
        await SeedSupplierAsync(db);
        await SeedProductAsync(db);
        var noMenu = await SeedRestrictedOperatorAsync(db, "customers", "suppliers");
        RevokeMenus(db, noMenu.UserId);
        await db.SaveChangesAsync();
        var before = await SnapshotAsync(db);

        var ctl = NewController(db, noMenu.UserId);
        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Export("customers"));
        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
        Assert.Contains("模块授权", ex.Message);
        await AssertCode(ErrorCodes.Forbidden, () => ctl.Import("customers", CustomerFile(("IO-NOMENU", "无菜单", noMenu.EmployeeId.ToString()))));

        await AssertUnchangedAsync(db, before);
    }

    [Fact]
    public async Task Live_revoked_menu_converges_to_denial_on_next_request()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var operatorInfo = await SeedRestrictedOperatorAsync(db, "customers");
        var ctl = NewController(db, operatorInfo.UserId);

        Assert.IsType<FileContentResult>(await ctl.Export("customers"));

        RevokeMenus(db, operatorInfo.UserId);
        await db.SaveChangesAsync();

        await AssertCode(ErrorCodes.Forbidden, () => ctl.Export("customers"));
    }

    // ==================== 2. 权威客户范围 ====================

    [Fact]
    public async Task Live_privileged_admin_exports_all_customers_and_can_import_for_any_salesman()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var adminId = await ResolveSeededAdminIdAsync(db);
        var otherEmployee = await SeedEmployeeAsync(db);
        await SeedCustomerAsync(db, "特权可见客户A", empId: otherEmployee);
        await SeedCustomerAsync(db, "特权可见客户B", empId: null);
        var expected = await db.BaseCustomers.CountAsync(c => !c.IsDeleted);

        var ctl = NewController(db, adminId);
        var exported = ReadExportedRows(Assert.IsType<FileContentResult>(await ctl.Export("customers")));
        Assert.Equal(expected, exported.Count);

        var resp = AssertOkObject(await ctl.Import("customers", CustomerFile(
            ($"IO-ADMIN-{Guid.NewGuid():N}"[..30], "特权新增客户", otherEmployee.ToString()))));
        Assert.Contains("成功 1 条", resp.Message);
    }

    [Fact]
    public async Task Live_restricted_operator_exports_only_own_customers()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var operatorInfo = await SeedRestrictedOperatorAsync(db, "customers");
        await SeedCustomerAsync(db, "范围本人客户", empId: operatorInfo.EmployeeId);
        await SeedCustomerAsync(db, "范围外客户", empId: null);

        var ctl = NewController(db, operatorInfo.UserId);
        var rows = ReadExportedRows(Assert.IsType<FileContentResult>(await ctl.Export("customers")));

        Assert.Single(rows);
        Assert.Equal("范围本人客户", rows[0]["客户名称"]);
    }

    [Fact]
    public async Task Live_restricted_operator_imports_only_in_scope_customer_rows()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var operatorInfo = await SeedRestrictedOperatorAsync(db, "customers");
        var foreignEmployee = await SeedEmployeeAsync(db);
        var before = await SnapshotAsync(db);
        var ctl = NewController(db, operatorInfo.UserId);

        var file = CustomerFile(
            ($"IO-OWN-{Guid.NewGuid():N}"[..30], "本人新增", operatorInfo.EmployeeId.ToString()),
            ($"IO-OTHER-{Guid.NewGuid():N}"[..30], "越界新增", foreignEmployee.ToString()),
            ($"IO-NONE-{Guid.NewGuid():N}"[..30], "无业务员新增", null));

        var resp = AssertOkObject(await ctl.Import("customers", file));
        Assert.Contains("成功 1 条", resp.Message);
        Assert.Contains("失败 2 条", resp.Message);

        var created = await db.BaseCustomers.AsNoTracking().SingleAsync(c => c.CustomerName == "本人新增");
        Assert.Equal(operatorInfo.EmployeeId, created.EmpId);
        Assert.False(await db.BaseCustomers.AsNoTracking().AnyAsync(c => c.CustomerName == "越界新增"));
        Assert.False(await db.BaseCustomers.AsNoTracking().AnyAsync(c => c.CustomerName == "无业务员新增"));
        Assert.Equal(before["customers"] + 1, await db.BaseCustomers.CountAsync());
        Assert.Equal(before["suppliers"], await db.BaseSuppliers.CountAsync());
        Assert.Equal(before["employees"], await db.BaseEmployees.CountAsync());
        Assert.Equal(before["products"], await db.BaseProducts.CountAsync());
    }

    // ==================== 3. 脚手架 ====================

    private static BaseDataIoController NewController(ErpDbContext db, long? userId)
    {
        var ctl = new BaseDataIoController(db);
        var claims = userId.HasValue
            ? new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }
            : Array.Empty<Claim>();
        ctl.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) }
        };
        return ctl;
    }

    private static async Task<Dictionary<string, int>> SnapshotAsync(ErpDbContext db)
        => new(StringComparer.Ordinal)
        {
            ["customers"] = await db.BaseCustomers.CountAsync(),
            ["suppliers"] = await db.BaseSuppliers.CountAsync(),
            ["employees"] = await db.BaseEmployees.CountAsync(),
            ["products"] = await db.BaseProducts.CountAsync(),
        };

    private static async Task AssertUnchangedAsync(ErpDbContext db, Dictionary<string, int> before)
        => Assert.Equal(before, await SnapshotAsync(db));

    private static async Task AssertCode(int expected, Func<Task<IActionResult>> action)
    {
        var ex = await Assert.ThrowsAsync<BusinessException>(action);
        Assert.Equal(expected, ex.Code);
    }

    private static ApiResponse<object> AssertOkObject(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<ApiResponse<object>>(ok.Value);
        Assert.Equal(ErrorCodes.Success, resp.Code);
        return resp;
    }

    private static List<Dictionary<string, string>> ReadExportedRows(FileContentResult file)
        => ExcelImporter.ReadRows(new MemoryStream(file.FileContents));

    /// <summary>用导出器构造一份可被导入器解析的客户 xlsx（表头与控制器客户列定义同源）。</summary>
    private static IFormFile CustomerFile(params (string Code, string Name, string? EmpId)[] rows)
    {
        var columns = new List<(string Key, string Title)>
        {
            ("CustomerCode", "客户编码"), ("CustomerName", "客户名称"), ("EmpId", "业务员Id"),
        };
        var data = rows.Select(r => new Dictionary<string, object?>
        {
            ["CustomerCode"] = r.Code,
            ["CustomerName"] = r.Name,
            ["EmpId"] = r.EmpId,
        }).ToList();
        var bytes = ExcelExporter.ExportRows("客户资料", data, columns);
        return new FormFile(new MemoryStream(bytes), 0, bytes.LongLength, "file", "customers.xlsx")
        {
            Headers = new HeaderDictionary(),
            ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"
        };
    }

    private static async Task<long> SeedCustomerAsync(ErpDbContext db, string name, long? empId)
    {
        var customer = new BaseCustomer
        {
            CustomerCode = $"IO-C-{Guid.NewGuid():N}"[..30], CustomerName = name, EmpId = empId, Status = 1
        };
        db.BaseCustomers.Add(customer);
        await db.SaveChangesAsync();
        return customer.Id;
    }

    private static async Task<long> SeedSupplierAsync(ErpDbContext db)
    {
        var supplier = new BaseSupplier
        {
            SupplierCode = $"IO-S-{Guid.NewGuid():N}"[..30], SupplierName = "集成供应商", Status = 1
        };
        db.BaseSuppliers.Add(supplier);
        await db.SaveChangesAsync();
        return supplier.Id;
    }

    private static async Task<long> SeedProductAsync(ErpDbContext db)
    {
        var product = new BaseProduct
        {
            ProductCode = $"IO-P-{Guid.NewGuid():N}"[..30], ProductName = "集成商品", Spec = "规格", Unit = "PCS"
        };
        db.BaseProducts.Add(product);
        await db.SaveChangesAsync();
        return product.Id;
    }

    private static async Task<long> SeedEmployeeAsync(ErpDbContext db)
    {
        var employee = new BaseEmployee
        {
            EmployeeCode = $"io-emp-{Guid.NewGuid():N}", EmployeeName = "集成业务员", IsSalesman = true, Status = 1
        };
        db.BaseEmployees.Add(employee);
        await db.SaveChangesAsync();
        return employee.Id;
    }

    private static async Task<long> SeedUserAsync(ErpDbContext db, UserStatus status, bool deleted)
    {
        var code = $"io-user-{Guid.NewGuid():N}";
        var employee = new BaseEmployee { EmployeeCode = code, EmployeeName = code, IsSalesman = true, Status = 1 };
        db.BaseEmployees.Add(employee);
        await db.SaveChangesAsync();

        var user = new SysUser
        {
            UserName = code, DisplayName = code, PasswordHash = "hash", PasswordSalt = "salt",
            Status = status, IsDeleted = deleted
        };
        db.SysUsers.Add(user);
        await db.SaveChangesAsync();

        var role = new SysRole { RoleName = "IO 身份", RoleCode = $"IoRole-{Guid.NewGuid():N}", IsSystem = false };
        db.SysRoles.Add(role);
        await db.SaveChangesAsync();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        await db.SaveChangesAsync();
        return user.Id;
    }

    /// <summary>受限制业务员：登录名 == 员工编码，仅授予传入的既有菜单（资源键与控制器同源）。</summary>
    private static async Task<(long UserId, long EmployeeId)> SeedRestrictedOperatorAsync(
        ErpDbContext db, params string[] resources)
    {
        var code = $"io-op-{Guid.NewGuid():N}";
        var employee = new BaseEmployee { EmployeeCode = code, EmployeeName = code, IsSalesman = true, Status = 1 };
        db.BaseEmployees.Add(employee);
        await db.SaveChangesAsync();

        var user = new SysUser
        {
            UserName = code, DisplayName = code, PasswordHash = "hash", PasswordSalt = "salt",
            Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        await db.SaveChangesAsync();

        var role = new SysRole { RoleName = "IO 业务员", RoleCode = $"IoOp-{Guid.NewGuid():N}", IsSystem = false };
        db.SysRoles.Add(role);
        await db.SaveChangesAsync();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        await db.SaveChangesAsync();

        foreach (var resource in resources)
        {
            if (!BaseDataIoAuthorizationRules.TryResolve(resource, out var authority)) continue;
            var menuId = await db.SysMenus.AsNoTracking()
                .Where(m => m.MenuCode == authority.MenuCode && !m.IsDeleted)
                .Select(m => m.Id)
                .FirstAsync();
            db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menuId });
            await db.SaveChangesAsync();
        }

        return (user.Id, employee.Id);
    }

    private static void RevokeMenus(ErpDbContext db, long userId)
    {
        var roleIds = db.SysUserRoles.Where(ur => ur.UserId == userId && !ur.IsDeleted)
            .Select(ur => ur.RoleId).ToList();
        foreach (var grant in db.SysRoleMenus.Where(rm => roleIds.Contains(rm.RoleId) && !rm.IsDeleted).ToList())
            grant.IsDeleted = true;
    }

    private static Task<long> ResolveSeededAdminIdAsync(ErpDbContext db)
        => db.SysUsers.AsNoTracking()
            .Where(u => u.UserName == SeedData.AdminUserName)
            .Select(u => u.Id)
            .FirstAsync();
}

/// <summary>
/// 专用 localdb 目标 Fixture（ERP-440）：把目标库初始化为完整 NEWERP 结构 + 种子数据，供基础资料导入导出授权集成测试复用。
/// <para>安全口径：库名一律带本次运行的 GUID 后缀（绝不复用 / 绝不 drop 既有库）；任何数据库访问之前先校验
/// 实例名、库名前缀与集成安全，连接串只来自进程环境变量或专用 localdb 默认值。</para>
/// </summary>
public sealed class BaseDataIoAuthorizationSqlServerFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";
    public const string DefaultDatabaseName = "NEWERP_AUTOTEST_BASEDATAIOAUTHORIZATION_20261009";

    public string ConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = BuildDefaultConnectionString();

        AssertDedicatedTarget(connectionString);
        ConnectionString = connectionString;

        Console.WriteLine($"[ERP-440] 目标库护栏放行（实例含 {InstanceMarker}，库名前缀 {DatabasePrefix}）。");

        await EnsureFreshDatabaseAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public ErpDbContext CreateDbContext() => new(BuildOptions());

    private DbContextOptions<ErpDbContext> BuildOptions()
        => new DbContextOptionsBuilder<ErpDbContext>().UseSqlServer(ConnectionString).Options;

    private static string BuildDefaultConnectionString()
        => $"Server=(localdb)\\{InstanceMarker};Initial Catalog={DefaultDatabaseName}_{Guid.NewGuid():N};" +
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

    private async Task EnsureFreshDatabaseAsync()
    {
        var database = new SqlConnectionStringBuilder(ConnectionString).InitialCatalog;

        // 任何数据库访问之前再次护栏：绝不使用生产 / 非专用回退。
        AssertDedicatedTarget(ConnectionString);

        var master = new SqlConnectionStringBuilder(ConnectionString) { InitialCatalog = "master" };
        await using (var conn = new SqlConnection(master.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            // Never destroy a pre-existing fixture or another caller's database.
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

        Console.WriteLine("[ERP-440] 集成场景就绪：完整 NEWERP 结构 + 种子数据。");
    }
}

/// <summary>目标库护栏单元级校验：非专用目标必须在访问数据库之前被拒绝。</summary>
public sealed class BaseDataIoAuthorizationTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;User Id=sa;Password=x")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(() => BaseDataIoAuthorizationSqlServerFixture.AssertDedicatedTarget(connection));

    [Fact]
    public void Accepts_the_dedicated_localdb_target_with_integrated_security()
        => BaseDataIoAuthorizationSqlServerFixture.AssertDedicatedTarget(
            "Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_X;Integrated Security=true");
}
