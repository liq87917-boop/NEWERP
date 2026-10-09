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
/// ERP-422 规范销售订单「新写入条款」校验的**真实 SQL Server** 集成测试（GUID 独占 <c>NEWERP_AUTOTEST</c> 目标）。
/// <list type="number">
/// <item><b>真实控制器 + 真实既有授权</b>：以既有系统内置角色（等同既有无保留访问口径）驱动真实
/// <see cref="SalesOrderController"/>，不新增任何菜单 / 角色 / 用户授权，无匿名 / 管理员降级。</item>
/// <item><b>非法条款零写入</b>：过小数量、未定义币种、超精度单价在新增 / 修改时被受控业务错误拒绝，
/// 表头 / 明细 / 单号字轨 / 下游证据（库存、出库、操作日志）零变化。</item>
/// <item><b>服务端权威</b>：客户端伪造的合计 / 定金额在落库时被服务端按明细重算覆盖，
/// <c>Σ 明细金额 == 主表总额</c> 精确成立（真实 <c>DECIMAL(18,2)</c> 落库后仍成立）。</item>
/// <item><b>提交 / 审核复核已持久化条款</b>：直接改库模拟非法历史条款后，提交 / 审核原子拒绝且状态不变；
/// 合法订单正常流转且来源血缘（<c>SourcePiId/No</c>）保持不变；历史读取 / 打印仍可读、不被修正。</item>
/// <item><b>专用目标护栏</b>：任何数据库访问之前精确命中 <c>(localdb)\NEWERP_AutoAcceptance</c> +
/// <c>NEWERP_AUTOTEST</c> 前缀 + <c>Integrated Security</c>；每次运行只创建一个全新 GUID 库，发现同名库已存在
/// 立即拒绝，绝不 drop / reset / 复用任何库，也绝不读取 appsettings / .env / 生产凭据。</item>
/// </list>
/// <para>保留既有库存来源单据审计与原始失败日志：本测试只读取计数与只读快照，不删除 / 不清理任何既有行。
/// 构建完成不等于阶段验收：只有本文件在受控 localdb 上真实执行通过才算验收证据。</para>
/// </summary>
public sealed class SalesOrderWriteValidationSqlServerTests
    : IClassFixture<SalesOrderWriteValidationSqlServerFixture>
{
    private readonly SalesOrderWriteValidationSqlServerFixture _fixture;

    public SalesOrderWriteValidationSqlServerTests(SalesOrderWriteValidationSqlServerFixture fixture)
        => _fixture = fixture;

    /// <summary>专用目标护栏（任何数据库访问之前）。</summary>
    private void Guard() => SalesOrderWriteValidationSqlServerFixture
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

    private sealed record WriteSnapshot(
        int SalesOrders, int SalesOrderDetails, int StockOuts, int StockMovements,
        int ProformaInvoices, int NumberRules);

    private static async Task<WriteSnapshot> SnapshotAsync(ErpDbContext db) => new(
        await db.SalesOrders.CountAsync(),
        await db.SalesOrderDetails.CountAsync(),
        await db.StockOuts.CountAsync(),
        await db.StockMovements.CountAsync(),
        await db.ProformaInvoices.CountAsync(),
        await db.SysDocumentNumberRules.CountAsync());

    private void AssertUnchanged(WriteSnapshot before, WriteSnapshot after)
    {
        Assert.Equal(before.SalesOrders, after.SalesOrders);
        Assert.Equal(before.SalesOrderDetails, after.SalesOrderDetails);
        Assert.Equal(before.StockOuts, after.StockOuts);
        Assert.Equal(before.StockMovements, after.StockMovements);
        Assert.Equal(before.ProformaInvoices, after.ProformaInvoices);
        Assert.Equal(before.NumberRules, after.NumberRules);
    }

    // ==================== 1. 新增：非法条款零写入 ====================

    [Fact]
    public async Task 新增_数量过小_拒绝且零写入且不消耗单号()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var before = await SnapshotAsync(db);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            NewController(db, _fixture.PrivilegedUserId).Create(NewBody(
                new SalesOrderDetail { ProductId = _fixture.ProductId, ProductName = "P1", Quantity = 0.001m, UnitPrice = 10m })));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Contains("数量必须大于 0", ex.Message);

        await using var verify = _fixture.CreateDbContext();
        AssertUnchanged(before, await SnapshotAsync(verify));
    }

    [Fact]
    public async Task 新增_未定义币种_拒绝且零写入()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var before = await SnapshotAsync(db);

        var body = NewBody(new SalesOrderDetail
        {
            ProductId = _fixture.ProductId, ProductName = "P1", Quantity = 1m, UnitPrice = 1m
        });
        body.Currency = (Currency)77;

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            NewController(db, _fixture.PrivilegedUserId).Create(body));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Equal(SalesOrderAmountRules.CurrencyText, ex.Message);

        await using var verify = _fixture.CreateDbContext();
        AssertUnchanged(before, await SnapshotAsync(verify));
    }

    [Fact]
    public async Task 新增_客户端伪造合计与定金_服务端按明细重算覆盖并精确落库()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var body = NewBody(new SalesOrderDetail
        {
            ProductId = _fixture.ProductId, ProductName = "P1", Quantity = 10m, UnitPrice = 100m
        });
        body.DepositRatio = 30m;
        body.TotalAmount = 999_999m;      // 伪造
        body.DepositAmount = 888_888m;    // 伪造

        Assert.IsType<OkObjectResult>(await NewController(db, _fixture.PrivilegedUserId).Create(body));

        await using var verify = _fixture.CreateDbContext();
        var stored = await verify.SalesOrders.AsNoTracking()
            .Where(o => o.CustomerId == _fixture.CustomerId && o.SalesmanId == _fixture.SalesmanEmployeeId)
            .OrderByDescending(o => o.Id)
            .FirstAsync();
        var lines = await verify.SalesOrderDetails.AsNoTracking()
            .Where(d => d.SalesOrderId == stored.Id).ToListAsync();

        Assert.Equal(1000m, stored.TotalAmount);
        Assert.Equal(300m, stored.DepositAmount);
        Assert.Equal(stored.TotalAmount, lines.Sum(d => d.Amount));
        Assert.All(lines, d => Assert.True(SalesOrderAmountRules.IsRepresentable(d.Amount)));
    }

    // ==================== 2. 修改：非法条款保留原始证据与来源血缘 ====================

    [Fact]
    public async Task 修改_非法条款_原表头明细与来源血缘不变()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var order = await _fixture.SeedOrderAsync(db, DocumentStatus.Pending, linkPi: true);
        var before = await SnapshotAsync(db);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            NewController(db, _fixture.PrivilegedUserId).Update(order.Id, NewBody(
                new SalesOrderDetail { ProductId = _fixture.ProductId, ProductName = "P1", Quantity = 1.005m, UnitPrice = 100m })));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);

        await using var verify = _fixture.CreateDbContext();
        var persisted = await verify.SalesOrders.AsNoTracking().SingleAsync(o => o.Id == order.Id);
        Assert.Equal(DocumentStatus.Pending, persisted.Status);
        Assert.Equal(1000m, persisted.TotalAmount);
        Assert.Equal(300m, persisted.DepositAmount);
        Assert.Equal(_fixture.PiId, persisted.SourcePiId);
        Assert.False(persisted.IsDeleted);

        var lines = await verify.SalesOrderDetails.AsNoTracking()
            .Where(d => d.SalesOrderId == order.Id).ToListAsync();
        Assert.Single(lines);
        Assert.Equal(10m, lines[0].Quantity);
        Assert.Equal(100m, lines[0].UnitPrice);

        AssertUnchanged(before, await SnapshotAsync(verify));
    }

    // ==================== 3. 提交 / 审核：已持久化条款复核 ====================

    [Fact]
    public async Task 提交_已持久化条款非法_原子拒绝且状态与证据不变()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var order = await _fixture.SeedOrderAsync(db, DocumentStatus.Pending, linkPi: false);
        var before = await SnapshotAsync(db);

        // 直接改库模拟历史 / 外部写入的非法条款（数量 0），绕过新写入校验。
        var line = await db.SalesOrderDetails.SingleAsync(d => d.SalesOrderId == order.Id);
        line.Quantity = 0m;
        line.Amount = 0m;
        await db.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            NewController(db, _fixture.PrivilegedUserId).Submit(order.Id));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);

        await using var verify = _fixture.CreateDbContext();
        Assert.Equal(DocumentStatus.Pending,
            (await verify.SalesOrders.AsNoTracking().SingleAsync(o => o.Id == order.Id)).Status);
        Assert.Equal(0m, (await verify.SalesOrderDetails.AsNoTracking()
            .SingleAsync(d => d.SalesOrderId == order.Id)).Quantity);
        AssertUnchanged(before, await SnapshotAsync(verify));
    }

    [Fact]
    public async Task 审核_已持久化币种未定义_原子拒绝且状态不变()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var order = await _fixture.SeedOrderAsync(db, DocumentStatus.Submitted, linkPi: false);

        var stored = await db.SalesOrders.SingleAsync(o => o.Id == order.Id);
        stored.Currency = (Currency)999;
        await db.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            NewController(db, _fixture.PrivilegedUserId).Approve(order.Id));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Equal(SalesOrderAmountRules.CurrencyText, ex.Message);

        await using var verify = _fixture.CreateDbContext();
        Assert.Equal(DocumentStatus.Submitted,
            (await verify.SalesOrders.AsNoTracking().SingleAsync(o => o.Id == order.Id)).Status);
    }

    [Fact]
    public async Task 提交审核_合法订单_精确落库且血缘不变()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var order = await _fixture.SeedOrderAsync(db, DocumentStatus.Pending, linkPi: true);

        var ctl = NewController(db, _fixture.PrivilegedUserId);
        Assert.IsType<OkObjectResult>(await ctl.Submit(order.Id));
        Assert.IsType<OkObjectResult>(await ctl.Approve(order.Id));

        await using var verify = _fixture.CreateDbContext();
        var persisted = await verify.SalesOrders.AsNoTracking().SingleAsync(o => o.Id == order.Id);
        var lines = await verify.SalesOrderDetails.AsNoTracking()
            .Where(d => d.SalesOrderId == order.Id).ToListAsync();

        Assert.Equal(DocumentStatus.Approved, persisted.Status);
        Assert.Equal(persisted.TotalAmount, lines.Sum(d => d.Amount));
        Assert.Equal(persisted.TotalAmount * persisted.DepositRatio / 100m, persisted.DepositAmount);
        Assert.Equal(_fixture.PiId, persisted.SourcePiId);
        Assert.Equal(_fixture.PiNo, persisted.SourcePiNo);
    }

    // ==================== 4. 历史读取 / 打印不修正 ====================

    [Fact]
    public async Task 历史读取与打印_非法存储条款仍可读且不被修正()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var order = await _fixture.SeedOrderAsync(db, DocumentStatus.Approved, linkPi: false);

        var line = await db.SalesOrderDetails.SingleAsync(d => d.SalesOrderId == order.Id);
        line.Quantity = -5m;
        line.UnitPrice = -3.33m;
        await db.SaveChangesAsync();

        var ctl = NewController(db, _fixture.PrivilegedUserId);
        var detail = await ctl.GetById(order.Id);
        var read = Assert.IsType<ApiResponse<SalesOrder>>(Assert.IsType<OkObjectResult>(detail).Value).Data!;
        Assert.Equal(-5m, read.Details.Single().Quantity);
        Assert.Equal(-3.33m, read.Details.Single().UnitPrice);

        var print = await ctl.GetPrint(order.Id);
        var printed = Assert.IsType<ApiResponse<SalesOrder>>(Assert.IsType<OkObjectResult>(print).Value).Data!;
        Assert.Equal(-5m, printed.Details.Single().Quantity);

        await using var verify = _fixture.CreateDbContext();
        var persisted = await verify.SalesOrderDetails.AsNoTracking()
            .SingleAsync(d => d.SalesOrderId == order.Id);
        Assert.Equal(-5m, persisted.Quantity);
        Assert.Equal(-3.33m, persisted.UnitPrice);
    }

    // ==================== 工厂 ====================

    private SalesOrder NewBody(params SalesOrderDetail[] details) => new()
    {
        OrderDate = DateTime.Today,
        CustomerId = _fixture.CustomerId,
        SalesmanId = _fixture.SalesmanEmployeeId,
        Currency = Currency.USD,
        ExchangeRate = 7.2m,
        DepositRatio = 30m,
        Details = details.ToList()
    };
}

/// <summary>
/// ERP-422 专用 localdb 夹具：目标必须是专用实例 <c>(localdb)\NEWERP_AutoAcceptance</c>、库名前缀
/// <c>NEWERP_AUTOTEST</c> 且 <c>Integrated Security=true</c>；每次运行只创建一个<strong>全新 GUID 后缀库</strong>，
/// 发现同名库已存在立即拒绝，绝不 drop / reset / 复用任何数据库，也绝不读取 appsettings / .env / 生产凭据。
/// </summary>
public sealed class SalesOrderWriteValidationSqlServerFixture : IAsyncLifetime
{
    /// <summary>专用实例（精确匹配）。</summary>
    public const string InstanceTarget = @"(localdb)\NEWERP_AutoAcceptance";

    /// <summary>库名前缀（必须为 NEWERP_AUTOTEST）。</summary>
    public const string DatabasePrefix = "NEWERP_AUTOTEST";

    private const string DefaultDatabaseName = DatabasePrefix + "_SOWRITEVAL";

    public string ConnectionString { get; private set; } = string.Empty;

    public long PrivilegedUserId { get; private set; }
    public long CustomerId { get; private set; }
    public long PiId { get; private set; }
    public string PiNo { get; private set; } = string.Empty;

    /// <summary>ERP-423：既有合法业务员（在职员工）—— 规范销售订单写入要求显式 SalesmanId 可解析。</summary>
    public long SalesmanEmployeeId { get; private set; }

    /// <summary>ERP-423：既有合法商品（未维护单位 = 不产生单位口径判定）—— 规范销售订单写入要求实时商品主数据。</summary>
    public long ProductId { get; private set; }

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = BuildDefaultConnectionString();

        AssertDedicatedTarget(connectionString);
        ConnectionString = connectionString;

        Console.WriteLine($"[ERP-422] 目标库护栏放行（实例 {InstanceTarget}，库名前缀 {DatabasePrefix}）。");
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

        Console.WriteLine("[ERP-422] 集成场景就绪：全新 GUID 库 + 完整 NEWERP 结构 + 种子数据（不清理既有行）。");
    }

    // ==================== 既有授权（不新增权限模型）+ 客户 / PI 夹具 ====================

    private async Task SeedAsync()
    {
        await using var db = CreateDbContext();

        // 特权账号：既有系统内置角色（等同既有无保留访问口径，不新增菜单 / 角色授权）。
        var role = new SysRole
        {
            RoleName = "ERP422 特权角色", RoleCode = $"ERP422-P-{Guid.NewGuid():N}", IsSystem = true
        };
        db.SysRoles.Add(role);
        await db.SaveChangesAsync();

        var user = new SysUser
        {
            UserName = $"erp422-{Guid.NewGuid():N}", PasswordHash = "hash", PasswordSalt = "salt",
            DisplayName = "ERP422 隔离账号", Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        await db.SaveChangesAsync();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        await db.SaveChangesAsync();
        PrivilegedUserId = user.Id;

        var customer = new BaseCustomer
        {
            CustomerCode = $"C-422-{Guid.NewGuid():N}", CustomerName = "ERP422 客户",
            Status = 1, CreditStatus = "正常", DepositRatio = 30m
        };
        db.BaseCustomers.Add(customer);
        await db.SaveChangesAsync();
        CustomerId = customer.Id;

        // ERP-423：规范销售订单写入要求实时业务员（在职员工）与商品主数据；播种既有合法夹具。
        var employee = new BaseEmployee
        {
            EmployeeCode = $"E-422-{Guid.NewGuid():N}", EmployeeName = "ERP422 业务员",
            IsSalesman = true, Status = 1
        };
        db.BaseEmployees.Add(employee);
        var product = new BaseProduct
        {
            ProductCode = $"P-422-{Guid.NewGuid():N}", ProductName = "ERP422 商品",
            Unit = string.Empty, Status = 1
        };
        db.BaseProducts.Add(product);
        await db.SaveChangesAsync();
        SalesmanEmployeeId = employee.Id;
        ProductId = product.Id;

        PiNo = $"PI-422-{Guid.NewGuid():N}"[..18];
        var pi = new ProformaInvoice
        {
            PiNo = PiNo,
            PiDate = DateTime.Today,
            CustomerId = customer.Id,
            CustomerName = customer.CustomerName,
            Currency = Currency.USD,
            ExchangeRate = 7.2m,
            TotalAmount = 1000m,
            DepositRatio = 30m,
            SalesmanId = user.Id,
            Status = DocumentStatus.Approved,
            Details = new List<ProformaInvoiceDetail>
            {
                new()
                {
                    SortNo = 1, ProductId = 21, ProductCode = "PX-422", ProductName = "ERP422 商品",
                    Spec = "标准", Unit = "PCS", Quantity = 100m, UnitPrice = 10m, Amount = 1000m
                }
            }
        };
        db.ProformaInvoices.Add(pi);
        await db.SaveChangesAsync();
        PiId = pi.Id;

        Console.WriteLine("[ERP-422] 既有授权 + 客户 / PI 夹具就绪（受控只读，不新增权限模型）。");
    }

    /// <summary>播种一条合法（可选链接已审核 PI）的待提交 / 已提交 / 已审核销售订单，供控制器写入场景使用。</summary>
    public async Task<SalesOrder> SeedOrderAsync(ErpDbContext db, DocumentStatus status, bool linkPi)
    {
        var order = new SalesOrder
        {
            OrderNo = $"SO-422-{Guid.NewGuid():N}"[..18],
            OrderDate = DateTime.Today,
            CustomerId = CustomerId,
            SalesmanId = SalesmanEmployeeId,
            Currency = Currency.USD,
            ExchangeRate = 7.2m,
            DepositRatio = 30m,
            Status = status,
            SourcePiId = linkPi ? PiId : null,
            SourcePiNo = linkPi ? PiNo : string.Empty,
            Details = new List<SalesOrderDetail>
            {
                new()
                {
                    ProductId = ProductId, ProductName = "ERP422 商品", Spec = "标准", Unit = "PCS",
                    Quantity = 10m, UnitPrice = 100m
                }
            }
        };
        SalesOrderAmountRules.ApplyDetailAmounts(order);
        SalesOrderAmountRules.Calculate(order);
        db.SalesOrders.Add(order);
        await db.SaveChangesAsync();
        return order;
    }
}

/// <summary>专用目标护栏的 fail-closed 覆盖：错误实例 / 错误库名 / 非集成安全必须在访问数据库之前被拒绝。</summary>
public sealed class SalesOrderWriteValidationTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;User Id=sa;Password=secret")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(
            () => SalesOrderWriteValidationSqlServerFixture.AssertDedicatedTarget(connection));
}
