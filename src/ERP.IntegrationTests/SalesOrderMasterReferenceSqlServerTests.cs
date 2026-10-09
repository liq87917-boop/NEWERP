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
/// ERP-423 规范销售订单「实时主数据引用」护栏的**真实 SQL Server** 集成测试
/// （GUID 独占 <c>NEWERP_AUTOTEST</c> 目标）。
/// <list type="number">
/// <item><b>真实控制器 + 真实既有授权</b>：以既有系统内置角色（特权）与既有「销售订单」功能菜单 + 业务员客户数据范围
/// 驱动真实 <see cref="SalesOrderController"/>；不新增 / 不修改任何菜单 / 角色 / 用户授权，无匿名 / 管理员降级。</item>
/// <item><b>受控错误与零写入</b>：缺失 / 已删除 / 已停用 / 越界客户，缺失 / 已删除 / 已停用商品，非法可选业务员 / 目的港，
/// 以及不支持单位，均在单号预约与任何赋值之前被受控业务错误拒绝；<c>SalesOrders</c> / <c>SalesOrderDetails</c> /
/// <c>StockOuts</c> / <c>StockMovements</c> / <c>ProformaInvoices</c> / <c>SysOperationLogs</c> 只读快照全部不变。</item>
/// <item><b>支持单位放行</b>：基础单位与合法装箱单位照常放行（绝不臆造换算，也绝不假设等于默认基础单位）。</item>
/// <item><b>提交 / 审核重查</b>：客户 / 商品在提交前被删除 / 停用时原子拒绝，状态与原始证据不变。</item>
/// <item><b>历史读取 / 打印只读</b>：引用失效主数据的历史订单仍可读、不被回填。</item>
/// <item><b>专用目标护栏</b>：任何数据库访问之前精确命中 <c>(localdb)\NEWERP_AutoAcceptance</c> +
/// <c>NEWERP_AUTOTEST</c> 前缀 + <c>Integrated Security</c>；每次运行只创建一个全新 GUID 库，发现同名库已存在立即拒绝，
/// 绝不 drop / reset / 复用任何库，也绝不读取 appsettings / .env / 生产凭据。</item>
/// </list>
/// <para>保留既有库存来源单据审计与原始失败日志：本测试只读取计数与只读快照，不删除 / 不清理任何既有行。
/// 构建完成不等于阶段验收：只有本文件在受控 localdb 上真实执行通过才算验收证据。</para>
/// </summary>
public sealed class SalesOrderMasterReferenceSqlServerTests
    : IClassFixture<SalesOrderMasterReferenceSqlServerFixture>
{
    private readonly SalesOrderMasterReferenceSqlServerFixture _fixture;

    public SalesOrderMasterReferenceSqlServerTests(SalesOrderMasterReferenceSqlServerFixture fixture)
        => _fixture = fixture;

    private static readonly string[] SnapshotTables =
    {
        "SalesOrders", "SalesOrderDetails", "StockOuts", "StockMovements",
        "ProformaInvoices", "SysOperationLogs",
    };

    /// <summary>专用目标护栏（任何数据库访问之前）。</summary>
    private void Guard() => SalesOrderMasterReferenceSqlServerFixture
        .AssertDedicatedTarget(_fixture.ConnectionString);

    private static SalesOrderController NewController(ErpDbContext db, long userId)
    {
        var http = new DefaultHttpContext();
        http.Request.Path = "/api/sales-orders";
        http.User = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.NameIdentifier, userId.ToString()) }, "Test"));

        return new SalesOrderController(db, new DocumentNumberService(db))
        {
            ControllerContext = new ControllerContext { HttpContext = http }
        };
    }

    private async Task<Dictionary<string, int>> SnapshotAsync()
    {
        await using var db = _fixture.CreateDbContext();
        return new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["SalesOrders"] = await db.SalesOrders.CountAsync(),
            ["SalesOrderDetails"] = await db.SalesOrderDetails.CountAsync(),
            ["StockOuts"] = await db.StockOuts.CountAsync(),
            ["StockMovements"] = await db.StockMovements.CountAsync(),
            ["ProformaInvoices"] = await db.ProformaInvoices.CountAsync(),
            ["SysOperationLogs"] = await db.SysOperationLogs.CountAsync(),
        };
    }

    private static void AssertSnapshotUnchanged(
        IReadOnlyDictionary<string, int> before, IReadOnlyDictionary<string, int> after)
    {
        foreach (var table in SnapshotTables)
            Assert.Equal(before[table], after[table]);
    }

    /// <summary>
    /// ERP-423：为本用例播种**专用**的既有合法客户 / 商品（避免失效模拟污染共享夹具的其他用例）。
    /// </summary>
    private async Task<(long CustomerId, long ProductId)> SeedTransientMastersAsync()
    {
        await using var db = _fixture.CreateDbContext();
        var customer = new BaseCustomer
        {
            CustomerCode = $"C-T-{Guid.NewGuid():N}", CustomerName = "ERP423 临时客户",
            Status = 1, CreditStatus = "正常", DepositRatio = 30m
        };
        var product = new BaseProduct
        {
            ProductCode = $"P-T-{Guid.NewGuid():N}", ProductName = "ERP423 临时商品",
            Unit = "PCS", Status = 1
        };
        db.BaseCustomers.Add(customer);
        db.BaseProducts.Add(product);
        await db.SaveChangesAsync();
        return (customer.Id, product.Id);
    }

    private static SalesOrder NewOrderBody(long customerId, params SalesOrderDetail[] details)
        => new()
        {
            OrderDate = DateTime.Today,
            CustomerId = customerId,
            Currency = Currency.USD,
            ExchangeRate = 7.2m,
            DepositRatio = 30m,
            Details = details.ToList()
        };

    private static SalesOrderDetail NewDetail(long productId, string unit)
        => new() { ProductId = productId, ProductName = "ERP423 商品", Unit = unit, Quantity = 2m, UnitPrice = 5m };

    // ==================== 1. 新增：客户引用 ====================

    [Fact]
    public async Task 新增_客户缺失或已删除_真实SQL受控拒绝且零写入()
    {
        Guard();
        var before = await SnapshotAsync();
        await using var db = _fixture.CreateDbContext();

        var missing = await Assert.ThrowsAsync<BusinessException>(() =>
            NewController(db, _fixture.PrivilegedUserId)
                .Create(NewOrderBody(9_423_999L, NewDetail(_fixture.ProductId, "PCS"))));
        Assert.Equal(ErrorCodes.NotFound, missing.Code);
        Assert.Contains("客户", missing.Message);

        var deleted = await Assert.ThrowsAsync<BusinessException>(() =>
            NewController(db, _fixture.PrivilegedUserId)
                .Create(NewOrderBody(_fixture.DeletedCustomerId, NewDetail(_fixture.ProductId, "PCS"))));
        Assert.Equal(ErrorCodes.NotFound, deleted.Code);

        AssertSnapshotUnchanged(before, await SnapshotAsync());
    }

    [Fact]
    public async Task 新增_客户已停用_真实SQL受控拒绝且零写入()
    {
        Guard();
        var before = await SnapshotAsync();
        await using var db = _fixture.CreateDbContext();

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            NewController(db, _fixture.PrivilegedUserId)
                .Create(NewOrderBody(_fixture.DisabledCustomerId, NewDetail(_fixture.ProductId, "PCS"))));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("已停用", ex.Message);
        AssertSnapshotUnchanged(before, await SnapshotAsync());
    }

    [Fact]
    public async Task 新增_越界客户_受限账号_受控权限错误且零写入()
    {
        Guard();
        var before = await SnapshotAsync();
        await using var db = _fixture.CreateDbContext();

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            NewController(db, _fixture.RestrictedUserId)
                .Create(NewOrderBody(_fixture.OutOfScopeCustomerId, NewDetail(_fixture.ProductId, "PCS"))));

        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
        Assert.Equal(SalesOrderMutationAuthorizationRules.ProposedCustomerDeniedText, ex.Message);
        AssertSnapshotUnchanged(before, await SnapshotAsync());
    }

    // ==================== 2. 新增：商品 / 可选引用 / 单位 ====================

    [Fact]
    public async Task 新增_商品缺失或已删除_真实SQL受控拒绝且零写入()
    {
        Guard();
        var before = await SnapshotAsync();
        await using var db = _fixture.CreateDbContext();

        var missing = await Assert.ThrowsAsync<BusinessException>(() =>
            NewController(db, _fixture.PrivilegedUserId)
                .Create(NewOrderBody(_fixture.CustomerId, NewDetail(9_423_888L, "PCS"))));
        Assert.Equal(ErrorCodes.NotFound, missing.Code);
        Assert.Contains("商品", missing.Message);

        var deleted = await Assert.ThrowsAsync<BusinessException>(() =>
            NewController(db, _fixture.PrivilegedUserId)
                .Create(NewOrderBody(_fixture.CustomerId, NewDetail(_fixture.DeletedProductId, "PCS"))));
        Assert.Equal(ErrorCodes.NotFound, deleted.Code);

        AssertSnapshotUnchanged(before, await SnapshotAsync());
    }

    [Fact]
    public async Task 新增_商品已停用_真实SQL受控拒绝且零写入()
    {
        Guard();
        var before = await SnapshotAsync();
        await using var db = _fixture.CreateDbContext();

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            NewController(db, _fixture.PrivilegedUserId)
                .Create(NewOrderBody(_fixture.CustomerId, NewDetail(_fixture.DisabledProductId, "PCS"))));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("已停用", ex.Message);
        AssertSnapshotUnchanged(before, await SnapshotAsync());
    }

    [Fact]
    public async Task 新增_可选业务员或目的港非法_真实SQL受控拒绝且零写入()
    {
        Guard();
        var before = await SnapshotAsync();
        await using var db = _fixture.CreateDbContext();

        var body = NewOrderBody(_fixture.CustomerId, NewDetail(_fixture.ProductId, "PCS"));
        body.SalesmanId = _fixture.DisabledEmployeeId;
        var salesman = await Assert.ThrowsAsync<BusinessException>(() =>
            NewController(db, _fixture.PrivilegedUserId).Create(body));
        Assert.Equal(ErrorCodes.RuleConflict, salesman.Code);
        Assert.Contains("业务员", salesman.Message);

        var portBody = NewOrderBody(_fixture.CustomerId, NewDetail(_fixture.ProductId, "PCS"));
        portBody.PortId = _fixture.DisabledPortId;
        var port = await Assert.ThrowsAsync<BusinessException>(() =>
            NewController(db, _fixture.PrivilegedUserId).Create(portBody));
        Assert.Equal(ErrorCodes.RuleConflict, port.Code);
        Assert.Contains("目的港", port.Message);

        AssertSnapshotUnchanged(before, await SnapshotAsync());
    }

    [Fact]
    public async Task 新增_支持单位_基础与装箱单位_真实SQL放行_不支持单位拒绝()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var ctl = NewController(db, _fixture.PrivilegedUserId);

        // 基础单位与合法装箱单位都被既有有效单位口径接受（绝不臆造换算）。
        Assert.IsType<OkObjectResult>(await ctl.Create(
            NewOrderBody(_fixture.CustomerId, NewDetail(_fixture.ProductId, "PCS"))));
        Assert.IsType<OkObjectResult>(await ctl.Create(
            NewOrderBody(_fixture.CustomerId, NewDetail(_fixture.ProductId, "BOX"))));

        var before = await SnapshotAsync();
        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            NewController(db, _fixture.PrivilegedUserId).Create(
                NewOrderBody(_fixture.CustomerId, NewDetail(_fixture.ProductId, "SET"))));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("单位", ex.Message);
        AssertSnapshotUnchanged(before, await SnapshotAsync());
    }

    // ==================== 3. 完整生命周期与提交 / 审核重查 ====================

    [Fact]
    public async Task 新增_手工订单_既有合法主数据_真实SQL完整生命周期可用()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var ctl = NewController(db, _fixture.PrivilegedUserId);

        var body = NewOrderBody(_fixture.CustomerId, NewDetail(_fixture.ProductId, "PCS"));
        body.SalesmanId = _fixture.SalesmanEmployeeId;
        body.PortId = _fixture.PortId;
        Assert.IsType<OkObjectResult>(await ctl.Create(body));

        await using var verify = _fixture.CreateDbContext();
        var created = await verify.SalesOrders.AsNoTracking()
            .Where(o => o.CustomerId == _fixture.CustomerId && o.SalesmanId == _fixture.SalesmanEmployeeId)
            .OrderByDescending(o => o.Id).FirstAsync();

        Assert.IsType<OkObjectResult>(await ctl.Submit(created.Id));
        Assert.IsType<OkObjectResult>(await ctl.Approve(created.Id));
        Assert.IsType<OkObjectResult>(await ctl.Cancel(created.Id));

        await using var final = _fixture.CreateDbContext();
        Assert.Equal(DocumentStatus.Cancelled,
            (await final.SalesOrders.AsNoTracking().SingleAsync(o => o.Id == created.Id)).Status);
    }

    [Fact]
    public async Task 提交_客户提交前被删除_真实SQL原子拒绝且状态不变()
    {
        Guard();
        var (customerId, productId) = await SeedTransientMastersAsync();
        var order = await _fixture.SeedOrderAsync(customerId, productId, DocumentStatus.Pending);
        await using (var db = _fixture.CreateDbContext())
        {
            db.BaseCustomers.Single(c => c.Id == customerId).IsDeleted = true;
            await db.SaveChangesAsync();
        }

        await using var ctlDb = _fixture.CreateDbContext();
        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            NewController(ctlDb, _fixture.PrivilegedUserId).Submit(order.Id));

        Assert.Equal(ErrorCodes.NotFound, ex.Code);
        await using var verify = _fixture.CreateDbContext();
        Assert.Equal(DocumentStatus.Pending,
            (await verify.SalesOrders.AsNoTracking().SingleAsync(o => o.Id == order.Id)).Status);
    }

    [Fact]
    public async Task 审核_商品提交前被停用_真实SQL原子拒绝且状态不变()
    {
        Guard();
        var (customerId, productId) = await SeedTransientMastersAsync();
        var order = await _fixture.SeedOrderAsync(customerId, productId, DocumentStatus.Submitted);
        await using (var db = _fixture.CreateDbContext())
        {
            db.BaseProducts.Single(p => p.Id == productId).Status = 0;
            await db.SaveChangesAsync();
        }

        await using var ctlDb = _fixture.CreateDbContext();
        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            NewController(ctlDb, _fixture.PrivilegedUserId).Approve(order.Id));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        await using var verify = _fixture.CreateDbContext();
        Assert.Equal(DocumentStatus.Submitted,
            (await verify.SalesOrders.AsNoTracking().SingleAsync(o => o.Id == order.Id)).Status);
    }

    // ==================== 4. 历史读取 / 打印只读 ====================

    [Fact]
    public async Task 历史订单_引用失效主数据_真实SQL读取打印仍可读且不被回填()
    {
        Guard();
        var (customerId, productId) = await SeedTransientMastersAsync();
        var order = await _fixture.SeedOrderAsync(customerId, productId, DocumentStatus.Approved);
        await using (var db = _fixture.CreateDbContext())
        {
            db.BaseCustomers.Single(c => c.Id == customerId).IsDeleted = true;
            db.BaseProducts.Single(p => p.Id == productId).IsDeleted = true;
            await db.SaveChangesAsync();
        }

        await using var ctlDb = _fixture.CreateDbContext();
        var ctl = NewController(ctlDb, _fixture.PrivilegedUserId);
        var read = Assert.IsType<ApiResponse<SalesOrder>>(
            Assert.IsType<OkObjectResult>(await ctl.GetById(order.Id)).Value).Data!;
        Assert.Equal(customerId, read.CustomerId);
        Assert.Equal(productId, Assert.Single(read.Details).ProductId);

        var print = Assert.IsType<ApiResponse<SalesOrder>>(
            Assert.IsType<OkObjectResult>(await ctl.GetPrint(order.Id)).Value).Data!;
        Assert.Equal(productId, Assert.Single(print.Details).ProductId);

        await using var verify = _fixture.CreateDbContext();
        var persisted = await verify.SalesOrders.AsNoTracking().SingleAsync(o => o.Id == order.Id);
        Assert.Equal(customerId, persisted.CustomerId);
    }
}

/// <summary>
/// ERP-423 专用 localdb 夹具：目标必须是专用实例 <c>(localdb)\NEWERP_AutoAcceptance</c>、库名前缀
/// <c>NEWERP_AUTOTEST</c> 且 <c>Integrated Security=true</c>；每次运行只创建一个<strong>全新 GUID 后缀库</strong>，
/// 发现同名库已存在立即拒绝，绝不 drop / reset / 复用任何数据库，也绝不读取 appsettings / .env / 生产凭据。
/// </summary>
public sealed class SalesOrderMasterReferenceSqlServerFixture : IAsyncLifetime
{
    /// <summary>专用实例（精确匹配）。</summary>
    public const string InstanceTarget = @"(localdb)\NEWERP_AutoAcceptance";

    /// <summary>库名前缀（必须为 NEWERP_AUTOTEST）。</summary>
    public const string DatabasePrefix = "NEWERP_AUTOTEST";

    private const string DefaultDatabaseName = DatabasePrefix + "_SOMASTERREF";

    public string ConnectionString { get; private set; } = string.Empty;

    public long PrivilegedUserId { get; private set; }
    public long RestrictedUserId { get; private set; }
    public long CustomerId { get; private set; }
    public long DeletedCustomerId { get; private set; }
    public long DisabledCustomerId { get; private set; }
    public long OutOfScopeCustomerId { get; private set; }
    public long SalesmanEmployeeId { get; private set; }
    public long DisabledEmployeeId { get; private set; }
    public long ProductId { get; private set; }
    public long DeletedProductId { get; private set; }
    public long DisabledProductId { get; private set; }
    public long PortId { get; private set; }
    public long DisabledPortId { get; private set; }

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = BuildDefaultConnectionString();

        AssertDedicatedTarget(connectionString);
        ConnectionString = connectionString;

        Console.WriteLine($"[ERP-423] 目标库护栏放行（实例 {InstanceTarget}，库名前缀 {DatabasePrefix}）。");
        await CreateFreshDatabaseAsync();
        await SeedAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public ErpDbContext CreateDbContext() => new(BuildOptions());

    private DbContextOptions<ErpDbContext> BuildOptions()
        => new DbContextOptionsBuilder<ErpDbContext>().UseSqlServer(ConnectionString).Options;

    private static string BuildDefaultConnectionString()
        => $"Server={InstanceTarget};Initial Catalog={DefaultDatabaseName}_{Guid.NewGuid():N};"
           + "Integrated Security=true;TrustServerCertificate=true;";

    /// <summary>专用目标护栏：错误实例 / 错误库名 / 非集成安全必须在访问数据库之前被拒绝。</summary>
    public static void AssertDedicatedTarget(string connectionString)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        Assert.Equal(InstanceTarget, builder.DataSource ?? string.Empty, ignoreCase: true);
        Assert.StartsWith(DatabasePrefix, builder.InitialCatalog ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.True(builder.IntegratedSecurity);
    }

    private async Task CreateFreshDatabaseAsync()
    {
        var database = new SqlConnectionStringBuilder(ConnectionString).InitialCatalog;

        // 任何库访问 / 建库之前再次护栏：绝不使用生产或非专用目标。
        AssertDedicatedTarget(ConnectionString);

        var master = new SqlConnectionStringBuilder(ConnectionString) { InitialCatalog = "master" };
        await using (var conn = new SqlConnection(master.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
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

        Console.WriteLine("[ERP-423] 集成场景就绪：全新 GUID 库 + 完整 NEWERP 结构 + 种子数据（不清理既有行）。");
    }

    // ==================== 既有授权（不新增权限模型）+ 主数据夹具 ====================

    private async Task SeedAsync()
    {
        await using var db = CreateDbContext();

        // 1) 特权账号（系统内置角色，沿用既有全部访问口径）。
        var privilegedRole = new SysRole
        {
            RoleName = "ERP423 特权角色", RoleCode = $"ERP423-P-{Guid.NewGuid():N}", IsSystem = true
        };
        db.SysRoles.Add(privilegedRole);
        await db.SaveChangesAsync();
        var privileged = NewUser(UserStatus.Enabled);
        db.SysUsers.Add(privileged);
        await db.SaveChangesAsync();
        db.SysUserRoles.Add(new SysUserRole { UserId = privileged.Id, RoleId = privilegedRole.Id });
        await db.SaveChangesAsync();
        PrivilegedUserId = privileged.Id;

        // 2) 受限业务员：既有「销售订单」功能菜单 + 客户数据范围（登录账号 == 员工编码，ERP-097 权威映射）。
        var restricted = NewUser(UserStatus.Enabled);
        db.SysUsers.Add(restricted);
        await db.SaveChangesAsync();
        var restrictedRole = new SysRole
        {
            RoleName = $"ERP423-{Guid.NewGuid():N}", RoleCode = $"ERP423-{Guid.NewGuid():N}", IsSystem = false
        };
        db.SysRoles.Add(restrictedRole);
        await db.SaveChangesAsync();
        db.SysUserRoles.Add(new SysUserRole { UserId = restricted.Id, RoleId = restrictedRole.Id });
        var menu = await db.SysMenus.FirstOrDefaultAsync(m =>
            m.MenuCode == SalesOrderMutationAuthorizationRules.RequiredMenuCode && !m.IsDeleted);
        if (menu is null)
        {
            menu = new SysMenu
            {
                MenuName = SalesOrderMutationAuthorizationRules.RequiredMenuText,
                MenuCode = SalesOrderMutationAuthorizationRules.RequiredMenuCode,
                MenuType = MenuType.Menu
            };
            db.SysMenus.Add(menu);
            await db.SaveChangesAsync();
        }
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = restrictedRole.Id, MenuId = menu.Id });
        await db.SaveChangesAsync();
        RestrictedUserId = restricted.Id;

        var restrictedEmployee = new BaseEmployee
        {
            EmployeeCode = restricted.UserName, EmployeeName = "ERP423 受限业务员", IsSalesman = true, Status = 1
        };
        db.BaseEmployees.Add(restrictedEmployee);
        await db.SaveChangesAsync();

        // 3) 客户：启用（受限可见）/ 越界 / 已删除 / 已停用。
        var customer = new BaseCustomer
        {
            CustomerCode = $"C-423-{Guid.NewGuid():N}", CustomerName = "ERP423 可见客户",
            EmpId = restrictedEmployee.Id, Status = 1, CreditStatus = "正常", DepositRatio = 30m
        };
        var outOfScope = new BaseCustomer
        {
            CustomerCode = $"C-423X-{Guid.NewGuid():N}", CustomerName = "ERP423 越界客户",
            Status = 1, CreditStatus = "正常", DepositRatio = 30m
        };
        var deletedCustomer = new BaseCustomer
        {
            CustomerCode = $"C-423D-{Guid.NewGuid():N}", CustomerName = "ERP423 已删除客户",
            Status = 1, CreditStatus = "正常", DepositRatio = 30m, IsDeleted = true
        };
        var disabledCustomer = new BaseCustomer
        {
            CustomerCode = $"C-423S-{Guid.NewGuid():N}", CustomerName = "ERP423 已停用客户",
            Status = 0, CreditStatus = "正常", DepositRatio = 30m
        };
        db.BaseCustomers.AddRange(customer, outOfScope, deletedCustomer, disabledCustomer);
        await db.SaveChangesAsync();
        CustomerId = customer.Id;
        OutOfScopeCustomerId = outOfScope.Id;
        DeletedCustomerId = deletedCustomer.Id;
        DisabledCustomerId = disabledCustomer.Id;

        // 4) 员工：在职 / 已停用。
        var salesman = new BaseEmployee
        {
            EmployeeCode = $"E-423-{Guid.NewGuid():N}", EmployeeName = "ERP423 在职业务员",
            IsSalesman = true, Status = 1
        };
        var disabledEmployee = new BaseEmployee
        {
            EmployeeCode = $"E-423S-{Guid.NewGuid():N}", EmployeeName = "ERP423 离职业务员",
            IsSalesman = true, Status = 0
        };
        db.BaseEmployees.AddRange(salesman, disabledEmployee);
        await db.SaveChangesAsync();
        SalesmanEmployeeId = salesman.Id;
        DisabledEmployeeId = disabledEmployee.Id;

        // 5) 商品：启用（基础单位 PCS + 合法装箱单位 BOX）/ 已删除 / 已停用。
        var product = new BaseProduct
        {
            ProductCode = $"P-423-{Guid.NewGuid():N}", ProductName = "ERP423 商品",
            Unit = "PCS", PackageUnit = "BOX", UnitsPerPackage = 12, Status = 1
        };
        var deletedProduct = new BaseProduct
        {
            ProductCode = $"P-423D-{Guid.NewGuid():N}", ProductName = "ERP423 已删除商品",
            Unit = "PCS", Status = 1, IsDeleted = true
        };
        var disabledProduct = new BaseProduct
        {
            ProductCode = $"P-423S-{Guid.NewGuid():N}", ProductName = "ERP423 已停用商品",
            Unit = "PCS", Status = 0
        };
        db.BaseProducts.AddRange(product, deletedProduct, disabledProduct);
        await db.SaveChangesAsync();
        ProductId = product.Id;
        DeletedProductId = deletedProduct.Id;
        DisabledProductId = disabledProduct.Id;

        // 6) 港口字典项：启用 / 已停用。
        var port = new BaseOtherInfo
        {
            InfoType = SalesOrderMasterReferenceRules.PortInfoType,
            InfoCode = $"PORT-{Guid.NewGuid():N}", InfoName = "ERP423 港口", Status = 1
        };
        var disabledPort = new BaseOtherInfo
        {
            InfoType = SalesOrderMasterReferenceRules.PortInfoType,
            InfoCode = $"PORT-S-{Guid.NewGuid():N}", InfoName = "ERP423 已停用港口", Status = 0
        };
        db.BaseOtherInfos.AddRange(port, disabledPort);
        await db.SaveChangesAsync();
        PortId = port.Id;
        DisabledPortId = disabledPort.Id;

        Console.WriteLine("[ERP-423] 既有授权 + 主数据夹具就绪（受控只读，不新增权限模型）。");
    }

    /// <summary>播种一条规范销售订单（客户 / 商品为既有合法主数据），供提交 / 审核与历史读取场景使用。</summary>
    public async Task<SalesOrder> SeedOrderAsync(long customerId, long productId, DocumentStatus status)
    {
        await using var db = CreateDbContext();
        var order = new SalesOrder
        {
            OrderNo = $"SO-423-{Guid.NewGuid():N}"[..18],
            OrderDate = DateTime.Today,
            CustomerId = customerId,
            Currency = Currency.USD,
            ExchangeRate = 7.2m,
            DepositRatio = 30m,
            Status = status,
            Details = new List<SalesOrderDetail>
            {
                new() { ProductId = productId, ProductName = "ERP423 商品", Unit = "PCS", Quantity = 10m, UnitPrice = 100m }
            }
        };
        SalesOrderAmountRules.ApplyDetailAmounts(order);
        SalesOrderAmountRules.Calculate(order);
        db.SalesOrders.Add(order);
        await db.SaveChangesAsync();
        return order;
    }

    private static SysUser NewUser(UserStatus status) => new()
    {
        UserName = $"erp423-{Guid.NewGuid():N}", PasswordHash = "hash", PasswordSalt = "salt",
        DisplayName = "ERP423 隔离账号", Status = status
    };
}

/// <summary>专用目标护栏的 fail-closed 覆盖：错误实例 / 错误库名 / 非集成安全必须在访问数据库之前被拒绝。</summary>
public sealed class SalesOrderMasterReferenceTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;User Id=sa;Password=secret")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(
            () => SalesOrderMasterReferenceSqlServerFixture.AssertDedicatedTarget(connection));
}
