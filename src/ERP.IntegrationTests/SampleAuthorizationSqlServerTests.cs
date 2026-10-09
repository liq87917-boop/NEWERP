using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using Xunit;

namespace ERP.IntegrationTests;

/// <summary>
/// ERP-459 样品管理（<c>api/crm/samples</c> 分页 / 全部 / 按主键读取 / 新增 / 修改 / 删除 / 批量删除）
/// 实时授权、ERP-097 业务员数据范围与有界字段校验的真实 SQL Server 集成测试（GUID 独占 <c>NEWERP_AUTOTEST</c> 目标）。
/// <list type="number">
/// <item><b>真实控制器 + 真实既有授权</b>：以既有「样品管理」（<c>sample</c>）菜单与既有
/// 「角色 → 菜单」口径驱动真实 <see cref="SampleController"/>（注入真实 HTTP 身份）。</item>
/// <item><b>拒绝矩阵</b>：缺失 / 禁用 / 已删除 / 无菜单的身份在<b>全部 7 条路由</b> fail closed，
/// 且 <c>Samples</c> 行逐字节不变（拒绝既不读取也不改写任何行）。</item>
/// <item><b>授权身份</b>：具备既有样品菜单时既有读 / 写契约放行；请求之间撤销菜单立即收敛为拒绝。</item>
/// <item><b>ERP-097 范围</b>：受限业务员只读到 / 只写入自己被分配客户的样品，越界读取按「不存在」、
/// 越界写入按权限不足拒绝且零写入。</item>
/// <item><b>有界字段校验</b>：样品编号空值 / 文本长度越界 / 数量与样品费非法 / 客户、商品与业务员引用非法
/// 在授权后仍 fail closed 且零写入。</item>
/// </list>
/// <para>安全口径：目标必须为专用 localdb 实例 <c>NEWERP_AutoAcceptance</c>、库名前缀 <c>NEWERP_AUTOTEST</c>
/// 且集成安全；每次运行只创建全新 GUID 后缀库，发现同名库已存在立即拒绝；绝不 drop / reset / 复用任何数据库，
/// 连接串只来自进程环境变量或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据。</para>
/// </summary>
public sealed class SampleAuthorizationSqlServerTests
    : IClassFixture<SampleAuthorizationSqlServerFixture>
{
    private readonly SampleAuthorizationSqlServerFixture _fixture;

    public SampleAuthorizationSqlServerTests(SampleAuthorizationSqlServerFixture fixture)
        => _fixture = fixture;

    /// <summary>访问数据库前复核专用目标护栏（实例名 / 库名前缀 / 集成安全）。</summary>
    private void Guard()
    {
        var target = new SqlConnectionStringBuilder(_fixture.ConnectionString);
        Assert.Equal("(localdb)\\NEWERP_AutoAcceptance", target.DataSource, ignoreCase: true);
        Assert.StartsWith(SampleAuthorizationSqlServerFixture.DatabasePrefix,
            target.InitialCatalog, StringComparison.OrdinalIgnoreCase);
        Assert.True(target.IntegratedSecurity);
    }

    private static SampleController Controller(ErpDbContext db, long? userId)
        => new(new GenericService<Sample>(db), db) { ControllerContext = ContextFor(userId) };

    /// <summary>
    /// 注入真实 HTTP 身份（可空 = 无 <c>NameIdentifier</c>）；<c>Request.Path</c> 已赋值以标记真实请求，
    /// 因此缺失身份也一律实时授权并 fail closed。
    /// </summary>
    private static ControllerContext ContextFor(long? userId)
    {
        var claims = userId.HasValue
            ? new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }
            : Array.Empty<Claim>();
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"))
        };
        http.Request.Path = "/api/crm/samples";
        return new ControllerContext { HttpContext = http };
    }

    private static string Tag() => Guid.NewGuid().ToString("N");

    private static Sample NewSample(
        string sampleNo, long? customerId, string customerName = "集成客户",
        long? productId = null, string productName = "样品", string spec = "规格",
        string sampleType = "打样", decimal quantity = 1m, string unit = "PCS",
        decimal sampleFee = 0m, string currency = "CNY", string express = "顺丰",
        string trackingNo = "SF001", string result = "待反馈", long? salesmanId = null,
        string salesmanName = "张三", string remark = "")
        => new()
        {
            SampleNo = sampleNo,
            SampleDate = new DateTime(2026, 9, 1),
            CustomerId = customerId,
            CustomerName = customerName,
            ProductId = productId,
            ProductName = productName,
            Spec = spec,
            SampleType = sampleType,
            Quantity = quantity,
            Unit = unit,
            SampleFee = sampleFee,
            Currency = currency,
            Express = express,
            TrackingNo = trackingNo,
            Result = result,
            SalesmanId = salesmanId,
            SalesmanName = salesmanName,
            Remark = remark
        };

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

    /// <summary>样品快照（授权 / 校验拒绝后必须逐字节不变）。</summary>
    private static async Task<string> SnapshotAsync(ErpDbContext db) => string.Join("|",
        await db.Samples.AsNoTracking().OrderBy(x => x.Id)
            .Select(x => $"{x.Id}:{x.IsDeleted}:{x.SampleNo}:{x.CustomerId}:{x.CustomerName}:{x.ProductId}:" +
                         $"{x.Quantity}:{x.SampleFee}:{x.Result}:{x.SalesmanId}:{x.Remark}")
            .ToListAsync());

    /// <summary>播种一个独立授权身份（可选状态 / 删除 / 样品菜单 / 特权角色），返回用户 Id。</summary>
    private static async Task<long> SeedUserAsync(ErpDbContext db, UserStatus status, bool deleted,
        bool sampleMenu, bool privileged = false)
    {
        var user = new SysUser
        {
            UserName = $"sa-{Tag()}",
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = "样品授权集成测试账号",
            Status = status,
            IsDeleted = deleted
        };
        db.SysUsers.Add(user);
        await db.SaveChangesAsync();

        var role = new SysRole
        {
            RoleName = "样品授权集成测试角色",
            RoleCode = $"SA-{Tag()}",
            IsSystem = privileged
        };
        db.SysRoles.Add(role);
        await db.SaveChangesAsync();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        await db.SaveChangesAsync();

        if (sampleMenu)
            await GrantMenuAsync(db, role.Id, SampleAuthorizationRules.RequiredMenuCode);
        return user.Id;
    }

    /// <summary>播种受限业务员（登录账号 == 员工编码，非特权角色），返回账号 / 员工。</summary>
    private static async Task<(long UserId, BaseEmployee Employee)> SeedRestrictedSalesmanAsync(
        ErpDbContext db, bool sampleMenu = true)
    {
        var employee = new BaseEmployee
        {
            EmployeeCode = $"sasales-{Tag()}",
            EmployeeName = "受限业务员",
            IsSalesman = true,
            Status = 1
        };
        db.BaseEmployees.Add(employee);
        await db.SaveChangesAsync();

        var user = new SysUser
        {
            UserName = employee.EmployeeCode,
            DisplayName = "受限业务员账号",
            PasswordHash = "hash",
            PasswordSalt = "salt",
            Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        await db.SaveChangesAsync();

        var role = new SysRole { RoleName = "Sales", RoleCode = $"SASales-{Tag()}", IsSystem = false };
        db.SysRoles.Add(role);
        await db.SaveChangesAsync();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        await db.SaveChangesAsync();

        if (sampleMenu)
            await GrantMenuAsync(db, role.Id, SampleAuthorizationRules.RequiredMenuCode);
        return (user.Id, employee);
    }

    /// <summary>授予既有功能菜单（菜单由种子数据提供；缺失时按既有种子口径补建）。</summary>
    private static async Task GrantMenuAsync(ErpDbContext db, long roleId, string menuCode)
    {
        var menu = await db.SysMenus.FirstOrDefaultAsync(m => m.MenuCode == menuCode && !m.IsDeleted);
        if (menu is null)
        {
            menu = new SysMenu { MenuCode = menuCode, MenuName = menuCode, MenuType = MenuType.Menu };
            db.SysMenus.Add(menu);
            await db.SaveChangesAsync();
        }
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = roleId, MenuId = menu.Id });
        await db.SaveChangesAsync();
    }

    /// <summary>回收指定账号的既有「样品管理」菜单授权（模拟请求之间撤销权限，不影响其它账号 / 种子管理员）。</summary>
    private static async Task RevokeMenuForUserAsync(ErpDbContext db, long userId)
    {
        var roleIds = await db.SysUserRoles.AsNoTracking()
            .Where(ur => ur.UserId == userId && !ur.IsDeleted)
            .Select(ur => ur.RoleId)
            .ToListAsync();
        var menuIds = await db.SysMenus.AsNoTracking()
            .Where(m => m.MenuCode == SampleAuthorizationRules.RequiredMenuCode)
            .Select(m => m.Id)
            .ToListAsync();
        var grants = await db.SysRoleMenus
            .Where(rm => roleIds.Contains(rm.RoleId) && menuIds.Contains(rm.MenuId))
            .ToListAsync();
        foreach (var grant in grants) grant.IsDeleted = true;
        await db.SaveChangesAsync();
    }

    private static async Task<BaseCustomer> SeedCustomerAsync(ErpDbContext db, long? empId = null)
    {
        var customer = new BaseCustomer
        {
            CustomerCode = $"SA-C-{Tag()}",
            CustomerName = "集成客户",
            Status = 1,
            CreditStatus = "正常",
            EmpId = empId,
            IsDeleted = false
        };
        db.BaseCustomers.Add(customer);
        await db.SaveChangesAsync();
        return customer;
    }

    private static async Task<BaseProduct> SeedProductAsync(ErpDbContext db, bool deleted = false)
    {
        var product = new BaseProduct
        {
            ProductCode = $"SA-P-{Tag()}",
            ProductName = "集成商品",
            Status = 1,
            IsDeleted = deleted
        };
        db.BaseProducts.Add(product);
        await db.SaveChangesAsync();
        return product;
    }

    private static async Task<Sample> SeedSampleAsync(
        ErpDbContext db, string sampleNo, long? customerId, string customerName = "集成客户")
    {
        var sample = new Sample
        {
            SampleNo = sampleNo,
            SampleDate = new DateTime(2026, 9, 1),
            CustomerId = customerId,
            CustomerName = customerName,
            SampleType = "打样",
            Quantity = 1m,
            SampleFee = 0m,
            Result = "待反馈",
            IsDeleted = false
        };
        db.Samples.Add(sample);
        await db.SaveChangesAsync();
        return sample;
    }

    /// <summary>解析既有种子管理员的账号 Id（用于确认既有种子账号仍具备既有样品菜单）。</summary>
    private static async Task<long> ResolveSeededAdminIdAsync(ErpDbContext db)
        => await db.SysUsers.AsNoTracking()
            .Where(u => u.UserName == SeedData.AdminUserName && !u.IsDeleted)
            .Select(u => u.Id)
            .FirstAsync();

    // ==================== 1. 身份 / 账号状态 / 菜单 fail closed ====================

    [Theory]
    [InlineData("missing")]
    [InlineData("disabled")]
    [InlineData("deleted")]
    [InlineData("no-menu")]
    public async Task Live_denies_every_route_without_reading_or_mutating_any_sample_row(string scenario)
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var customer = await SeedCustomerAsync(db);
        var sample = await SeedSampleAsync(db, $"SP-DENY-{Tag()}", customer.Id);

        long? userId = scenario switch
        {
            "missing" => null,
            "disabled" => await SeedUserAsync(db, UserStatus.Disabled, deleted: false, sampleMenu: true),
            "deleted" => await SeedUserAsync(db, UserStatus.Enabled, deleted: true, sampleMenu: true),
            _ => await SeedUserAsync(db, UserStatus.Enabled, deleted: false, sampleMenu: false)
        };
        var expectedCode = scenario is "missing" or "deleted"
            ? ErrorCodes.Unauthorized
            : ErrorCodes.Forbidden;

        var ctl = Controller(db, userId);
        var before = await SnapshotAsync(db);

        await AssertCodeAsync(expectedCode, () => ctl.GetPaged(new PageQuery { Page = 1, PageSize = 10 }));
        await AssertCodeAsync(expectedCode, () => ctl.GetAll());
        await AssertCodeAsync(expectedCode, () => ctl.GetById(sample.Id));
        await AssertCodeAsync(expectedCode, () => ctl.Create(NewSample($"SP-DENY-NEW-{Tag()}", customer.Id)));
        await AssertCodeAsync(expectedCode,
            () => ctl.Update(sample.Id, NewSample($"SP-DENY-UPD-{Tag()}", customer.Id, "被拒改名")));
        await AssertCodeAsync(expectedCode, () => ctl.Delete(sample.Id));
        await AssertCodeAsync(expectedCode, () => ctl.BatchDelete(new List<long> { sample.Id }));

        Assert.Equal(before, await SnapshotAsync(db));
        var stored = await db.Samples.AsNoTracking().SingleAsync(x => x.Id == sample.Id);
        Assert.Equal(sample.SampleNo, stored.SampleNo);
        Assert.False(stored.IsDeleted);
    }

    // ==================== 2. 授权身份：既有读 / 写契约放行 ====================

    [Fact]
    public async Task Authorized_identity_allows_existing_contracts()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var adminId = await ResolveSeededAdminIdAsync(db);
        var menuCodes = await CustomerReceivableReconciliationService.LoadAuthorizedMenuCodesAsync(db, adminId);
        Assert.Contains(SampleAuthorizationRules.RequiredMenuCode, menuCodes,
            StringComparer.OrdinalIgnoreCase);

        var employee = new BaseEmployee
        {
            EmployeeCode = $"saemp-{Tag()}",
            EmployeeName = "业务员",
            IsSalesman = true,
            Status = 1
        };
        db.BaseEmployees.Add(employee);
        await db.SaveChangesAsync();

        var customer = await SeedCustomerAsync(db, employee.Id);
        var product = await SeedProductAsync(db);
        var ctl = Controller(db, adminId);

        var createdNo = $"SP-OK-{Tag()}";
        var created = Data<Sample>(await ctl.Create(NewSample(
            createdNo, customer.Id, productId: product.Id, salesmanId: employee.Id)));
        Assert.True(created.Id > 0);

        var page = Data<PagedResult<Sample>>(await ctl.GetPaged(new PageQuery { Page = 1, PageSize = 100 }));
        Assert.Contains(page.Items, s => s.Id == created.Id && s.SampleNo == createdNo);
        Assert.Contains(Data<List<Sample>>(await ctl.GetAll()), s => s.Id == created.Id);
        Assert.Equal(createdNo, Data<Sample>(await ctl.GetById(created.Id)).SampleNo);

        var updated = Data<Sample>(
            await ctl.Update(created.Id, NewSample(createdNo, customer.Id, customerName: "改名客户")));
        Assert.Equal("改名客户", updated.CustomerName);

        var second = Data<Sample>(await ctl.Create(NewSample($"SP-OK2-{Tag()}", customer.Id)));
        await ctl.BatchDelete(new List<long> { second.Id });
        Assert.True((await db.Samples.AsNoTracking().SingleAsync(x => x.Id == second.Id)).IsDeleted);

        await ctl.Delete(created.Id);
        Assert.True((await db.Samples.AsNoTracking().SingleAsync(x => x.Id == created.Id)).IsDeleted);
    }

    // ==================== 3. ERP-097 受限业务员读取 / 写入范围 ====================

    [Fact]
    public async Task Restricted_salesman_sees_and_writes_only_own_customer_samples()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var (userId, employee) = await SeedRestrictedSalesmanAsync(db);
        var mine = await SeedCustomerAsync(db, employee.Id);
        var other = await SeedCustomerAsync(db, employee.Id + 100000);
        var mineSample = await SeedSampleAsync(db, $"SP-MINE-{Tag()}", mine.Id, "我的客户");
        var otherSample = await SeedSampleAsync(db, $"SP-OTHER-{Tag()}", other.Id, "别人的客户");
        await SeedSampleAsync(db, $"SP-NULL-{Tag()}", null, "匿名客户");

        var ctl = Controller(db, userId);

        var page = Data<PagedResult<Sample>>(await ctl.GetPaged(new PageQuery { Page = 1, PageSize = 100 }));
        Assert.Contains(page.Items, s => s.Id == mineSample.Id);
        Assert.DoesNotContain(page.Items, s => s.Id == otherSample.Id);
        Assert.DoesNotContain(page.Items, s => s.CustomerId == null);

        var all = Data<List<Sample>>(await ctl.GetAll());
        Assert.Contains(all, s => s.Id == mineSample.Id);
        Assert.DoesNotContain(all, s => s.Id == otherSample.Id);

        Assert.Equal(mineSample.Id, Data<Sample>(await ctl.GetById(mineSample.Id)).Id);
        await AssertCodeAsync(ErrorCodes.NotFound, () => ctl.GetById(otherSample.Id));

        var mineNo = $"SP-MINE-NEW-{Tag()}";
        var created = Data<Sample>(await ctl.Create(NewSample(mineNo, mine.Id, "我的客户")));
        Assert.True(created.Id > 0);
        await AssertCodeAsync(ErrorCodes.Forbidden,
            () => ctl.Create(NewSample($"SP-OTHER-NEW-{Tag()}", other.Id, "别人的客户")));
        await AssertCodeAsync(ErrorCodes.Forbidden,
            () => ctl.Update(mineSample.Id, NewSample(mineSample.SampleNo, other.Id, "别人的客户")));

        var snapshot = await SnapshotAsync(db);
        Assert.Contains(mineNo, snapshot);
        Assert.Equal(1, await db.Samples.AsNoTracking().CountAsync(s => s.CustomerId == other.Id));
    }

    // ==================== 4. 请求之间撤销菜单：立即收敛 ====================

    [Fact]
    public async Task Revoked_menu_converges_to_denial_with_zero_writes()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var customer = await SeedCustomerAsync(db);
        var sample = await SeedSampleAsync(db, $"SP-REVOKE-{Tag()}", customer.Id);
        var userId = await SeedUserAsync(db, UserStatus.Enabled, deleted: false, sampleMenu: true);
        var ctl = Controller(db, userId);

        Data<PagedResult<Sample>>(await ctl.GetPaged(new PageQuery { Page = 1, PageSize = 10 }));

        await RevokeMenuForUserAsync(db, userId);

        await AssertCodeAsync(ErrorCodes.Forbidden, () => ctl.GetPaged(new PageQuery { Page = 1, PageSize = 10 }));
        await AssertCodeAsync(ErrorCodes.Forbidden, () => ctl.GetAll());
        await AssertCodeAsync(ErrorCodes.Forbidden, () => ctl.Create(NewSample($"SP-REVOKED-{Tag()}", customer.Id)));
        await AssertCodeAsync(ErrorCodes.Forbidden, () => ctl.Delete(sample.Id));
        await AssertCodeAsync(ErrorCodes.Forbidden, () => ctl.BatchDelete(new List<long> { sample.Id }));

        Assert.False((await db.Samples.AsNoTracking().SingleAsync(x => x.Id == sample.Id)).IsDeleted);
    }

    // ==================== 5. 授权后非法载荷：拒绝且零写入 ====================

    [Theory]
    [InlineData("empty-sampleno")]
    [InlineData("sampleno-too-long")]
    [InlineData("sampletype-too-long")]
    [InlineData("remark-too-long")]
    [InlineData("quantity-negative")]
    [InlineData("samplefee-too-large")]
    [InlineData("customer-missing")]
    [InlineData("customer-unknown")]
    [InlineData("product-unknown")]
    [InlineData("salesman-unknown")]
    public async Task Authorized_invalid_payload_is_rejected_with_zero_writes(string scenario)
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var adminId = await ResolveSeededAdminIdAsync(db);
        var customer = await SeedCustomerAsync(db);
        var existing = await SeedSampleAsync(db, $"SP-EXIST-{Tag()}", customer.Id);
        var ctl = Controller(db, adminId);

        Sample Payload() => scenario switch
        {
            "empty-sampleno" => NewSample("", customer.Id),
            "sampleno-too-long" => NewSample(
                new string('N', SampleAuthorizationRules.MaxSampleNoLength + 1), customer.Id),
            "sampletype-too-long" => NewSample($"SP-{Tag()}", customer.Id,
                sampleType: new string('T', SampleAuthorizationRules.MaxSampleTypeLength + 1)),
            "remark-too-long" => NewSample($"SP-{Tag()}", customer.Id,
                remark: new string('M', SampleAuthorizationRules.MaxRemarkLength + 1)),
            "quantity-negative" => NewSample($"SP-{Tag()}", customer.Id, quantity: -1m),
            "samplefee-too-large" => NewSample($"SP-{Tag()}", customer.Id,
                sampleFee: SampleAuthorizationRules.MaxDecimalMagnitude + 1m),
            "customer-missing" => NewSample($"SP-{Tag()}", null),
            "customer-unknown" => NewSample($"SP-{Tag()}", 999999999),
            "product-unknown" => NewSample($"SP-{Tag()}", customer.Id, productId: 999999999),
            _ => NewSample($"SP-{Tag()}", customer.Id, salesmanId: 999999999)
        };

        var before = await SnapshotAsync(db);

        await AssertCodeAsync(ErrorCodes.InvalidParameter, () => ctl.Create(Payload()));
        Assert.Equal(before, await SnapshotAsync(db));
        Assert.Equal(1, await db.Samples.AsNoTracking().CountAsync(s => s.CustomerId == customer.Id));

        await AssertCodeAsync(ErrorCodes.InvalidParameter, () => ctl.Update(existing.Id, Payload()));
        Assert.Equal(before, await SnapshotAsync(db));
        var stored = await db.Samples.AsNoTracking().SingleAsync(x => x.Id == existing.Id);
        Assert.Equal(existing.SampleNo, stored.SampleNo);
    }

}

/// <summary>
/// ERP-459 专用 localdb 目标 Fixture：只创建一个全新 GUID 后缀库并初始化完整 NEWERP 结构 + 种子数据，
/// 供样品管理实时授权集成测试复用。
/// <para>安全口径：实例必须精确为 <c>(localdb)\NEWERP_AutoAcceptance</c>，库名前缀必须为 <c>NEWERP_AUTOTEST</c>
/// 且使用集成安全；发现同名库已存在立即拒绝；绝不 drop / reset / 复用任何数据库，绝不读取生产设置。</para>
/// </summary>
public sealed class SampleAuthorizationSqlServerFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";

    /// <summary>本次运行新建的 GUID 独占库名（每次运行唯一，绝不复用既有库）。</summary>
    public static string DefaultDatabaseName { get; } =
        $"{DatabasePrefix}_SAMPLEAUTH_{Guid.NewGuid():N}";

    public string ConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = BuildDefaultConnectionString();

        // 访问数据库之前先复核目标护栏（错误目标 fail closed）。
        AssertDedicatedTarget(connectionString);
        ConnectionString = connectionString;

        Console.WriteLine($"[ERP-459] 目标库护栏放行（实例 {InstanceMarker}，库名前缀 {DatabasePrefix}，集成安全）。");

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

        Console.WriteLine("[ERP-459] 集成场景就绪：完整 NEWERP 结构 + 种子数据（含既有 sample 菜单）。");
    }
}

/// <summary>目标库护栏单元级校验：非专用目标必须在访问数据库之前被拒绝。</summary>
public sealed class SampleAuthorizationTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    [InlineData("Server=(localdb)\\\\NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;User Id=sa;Password=secret")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(
            () => SampleAuthorizationSqlServerFixture.AssertDedicatedTarget(connection));
}

