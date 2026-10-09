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
/// ERP-427 规范采购订单「实时主数据引用」护栏的**真实 SQL Server** 集成测试
/// （GUID 独占 <c>NEWERP_AUTOTEST</c> 目标）。
/// <list type="number">
/// <item><b>真实控制器 + 真实既有授权</b>：以既有系统内置角色（特权）与既有「采购订单」功能菜单 + 业务员客户数据范围
/// 驱动真实 <see cref="PurchaseOrderController"/>；不新增 / 不修改任何菜单 / 角色 / 用户授权，无匿名 / 管理员降级。</item>
/// <item><b>受控错误与零写入</b>：缺失 / 已删除 / 已停用供应商，缺失 / 已删除 / 已停用商品，非法可选采购员 / 起运港，
/// 以及不支持单位，均在单号预约与任何赋值之前被受控业务错误拒绝；<c>PurchaseOrders</c> / <c>PurchaseOrderDetails</c> /
/// <c>Stocks</c> / <c>StockMovements</c> / <c>SysOperationLogs</c> 只读快照全部不变。</item>
/// <item><b>支持单位放行</b>：基础单位与合法装箱单位照常放行（绝不臆造换算，也绝不假设等于默认基础单位）。</item>
/// <item><b>提交 / 审核重查</b>：供应商 / 商品在提交 / 审核前被删除 / 停用时原子拒绝，状态与原始证据不变。</item>
/// <item><b>历史读取 / 打印只读</b>：引用失效主数据的历史订单仍可读、不被回填。</item>
/// <item><b>专用目标护栏</b>：任何数据库访问之前精确命中 <c>(localdb)\NEWERP_AutoAcceptance</c> +
/// <c>NEWERP_AUTOTEST</c> 前缀 + <c>Integrated Security</c>；每次运行只创建一个全新 GUID 库，发现同名库已存在立即拒绝，
/// 绝不 drop / reset / 复用任何库，也绝不读取 appsettings / .env / 生产凭据。</item>
/// </list>
/// <para>保留既有库存来源单据审计与原始失败日志：本测试只读取计数与只读快照，不删除 / 不清理任何既有行。
/// 构建完成不等于阶段验收：只有本文件在受控 localdb 上真实执行通过才算验收证据。</para>
/// </summary>
public sealed class PurchaseOrderMasterReferenceSqlServerTests
    : IClassFixture<PurchaseOrderMasterReferenceSqlServerFixture>
{
    private readonly PurchaseOrderMasterReferenceSqlServerFixture _fixture;

    public PurchaseOrderMasterReferenceSqlServerTests(PurchaseOrderMasterReferenceSqlServerFixture fixture)
        => _fixture = fixture;

    private static readonly string[] SnapshotTables =
    {
        "PurchaseOrders", "PurchaseOrderDetails", "Stocks", "StockMovements", "SysOperationLogs",
    };

    /// <summary>专用目标护栏（任何数据库访问之前）。</summary>
    private void Guard() => PurchaseOrderMasterReferenceSqlServerFixture
        .AssertDedicatedTarget(_fixture.ConnectionString);

    private static PurchaseOrderController NewController(ErpDbContext db, long userId)
    {
        var http = new DefaultHttpContext();
        http.Request.Path = "/api/purchase-orders";
        http.User = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.NameIdentifier, userId.ToString()) }, "Test"));

        return new PurchaseOrderController(db, new DocumentNumberService(db))
        {
            ControllerContext = new ControllerContext { HttpContext = http }
        };
    }

    private async Task<Dictionary<string, int>> SnapshotAsync()
    {
        await using var db = _fixture.CreateDbContext();
        return new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["PurchaseOrders"] = await db.PurchaseOrders.CountAsync(),
            ["PurchaseOrderDetails"] = await db.PurchaseOrderDetails.CountAsync(),
            ["Stocks"] = await db.Stocks.CountAsync(),
            ["StockMovements"] = await db.StockMovements.CountAsync(),
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
    /// ERP-427：为本用例播种**专用**的既有合法供应商 / 商品（避免失效模拟污染共享夹具的其他用例）。
    /// </summary>
    private async Task<(long SupplierId, long ProductId)> SeedTransientMastersAsync()
    {
        await using var db = _fixture.CreateDbContext();
        var supplier = new BaseSupplier
        {
            SupplierCode = $"S-T-{Guid.NewGuid():N}", SupplierName = "ERP427 临时供应商", Status = 1
        };
        var product = new BaseProduct
        {
            ProductCode = $"P-T-{Guid.NewGuid():N}", ProductName = "ERP427 临时商品",
            Unit = "PCS", Status = 1
        };
        db.BaseSuppliers.Add(supplier);
        db.BaseProducts.Add(product);
        await db.SaveChangesAsync();
        return (supplier.Id, product.Id);
    }

    private static PurchaseOrder NewOrderBody(long supplierId, long productId, string unit,
        long? buyerId = null, long? portId = null)
        => new()
        {
            OrderDate = DateTime.Today,
            SupplierId = supplierId,
            BuyerId = buyerId,
            PortId = portId,
            Currency = Currency.CNY,
            ExchangeRate = 1m,
            TaxRate = 0m,
            Details = new List<PurchaseOrderDetail>
            {
                new()
                {
                    ProductId = productId, ProductName = "ERP427 商品", Spec = "规格A",
                    Unit = unit, Quantity = 2m, UnitPrice = 5m
                }
            }
        };

    // ==================== 1. 新增：必填供应商 ====================

    [Fact]
    public async Task 新增_供应商缺失或已删除_真实SQL受控拒绝且零写入()
    {
        Guard();
        var before = await SnapshotAsync();
        await using var db = _fixture.CreateDbContext();

        var missing = await Assert.ThrowsAsync<BusinessException>(() =>
            NewController(db, _fixture.PrivilegedUserId)
                .Create(NewOrderBody(9_427_999L, _fixture.ProductId, "PCS")));
        Assert.Equal(ErrorCodes.NotFound, missing.Code);
        Assert.Contains("供应商", missing.Message);

        var deleted = await Assert.ThrowsAsync<BusinessException>(() =>
            NewController(db, _fixture.PrivilegedUserId)
                .Create(NewOrderBody(_fixture.DeletedSupplierId, _fixture.ProductId, "PCS")));
        Assert.Equal(ErrorCodes.NotFound, deleted.Code);

        AssertSnapshotUnchanged(before, await SnapshotAsync());
    }

    [Fact]
    public async Task 新增_供应商已停用_真实SQL受控拒绝且零写入()
    {
        Guard();
        var before = await SnapshotAsync();
        await using var db = _fixture.CreateDbContext();

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            NewController(db, _fixture.PrivilegedUserId)
                .Create(NewOrderBody(_fixture.DisabledSupplierId, _fixture.ProductId, "PCS")));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("已停用", ex.Message);
        AssertSnapshotUnchanged(before, await SnapshotAsync());
    }

    [Fact]
    public async Task 新增_越界归属客户_受限账号_受控权限错误且零写入()
    {
        Guard();
        var before = await SnapshotAsync();
        await using var db = _fixture.CreateDbContext();

        var body = NewOrderBody(_fixture.SupplierId, _fixture.ProductId, "PCS");
        body.OwningCustomerId = _fixture.OutOfScopeCustomerId;
        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            NewController(db, _fixture.RestrictedUserId).Create(body));

        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
        Assert.Contains("数据范围", ex.Message);
        AssertSnapshotUnchanged(before, await SnapshotAsync());
    }

    // ==================== 2. 新增：必填商品 / 可选引用 / 单位 ====================

    [Fact]
    public async Task 新增_商品缺失或已删除_真实SQL受控拒绝且零写入()
    {
        Guard();
        var before = await SnapshotAsync();
        await using var db = _fixture.CreateDbContext();

        var missing = await Assert.ThrowsAsync<BusinessException>(() =>
            NewController(db, _fixture.PrivilegedUserId)
                .Create(NewOrderBody(_fixture.SupplierId, 9_427_888L, "PCS")));
        Assert.Equal(ErrorCodes.NotFound, missing.Code);
        Assert.Contains("商品", missing.Message);

        var deleted = await Assert.ThrowsAsync<BusinessException>(() =>
            NewController(db, _fixture.PrivilegedUserId)
                .Create(NewOrderBody(_fixture.SupplierId, _fixture.DeletedProductId, "PCS")));
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
                .Create(NewOrderBody(_fixture.SupplierId, _fixture.DisabledProductId, "PCS")));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("已停用", ex.Message);
        AssertSnapshotUnchanged(before, await SnapshotAsync());
    }

    [Fact]
    public async Task 新增_可选采购员或起运港失效_真实SQL受控拒绝且零写入()
    {
        Guard();
        var before = await SnapshotAsync();
        await using var db = _fixture.CreateDbContext();

        var buyer = await Assert.ThrowsAsync<BusinessException>(() =>
            NewController(db, _fixture.PrivilegedUserId)
                .Create(NewOrderBody(_fixture.SupplierId, _fixture.ProductId, "PCS", _fixture.DisabledBuyerId)));
        Assert.Equal(ErrorCodes.RuleConflict, buyer.Code);

        var port = await Assert.ThrowsAsync<BusinessException>(() =>
            NewController(db, _fixture.PrivilegedUserId)
                .Create(NewOrderBody(_fixture.SupplierId, _fixture.ProductId, "PCS", portId: _fixture.DisabledPortId)));
        Assert.Equal(ErrorCodes.RuleConflict, port.Code);

        var missingPort = await Assert.ThrowsAsync<BusinessException>(() =>
            NewController(db, _fixture.PrivilegedUserId)
                .Create(NewOrderBody(_fixture.SupplierId, _fixture.ProductId, "PCS", portId: 9_427_777L)));
        Assert.Equal(ErrorCodes.NotFound, missingPort.Code);

        AssertSnapshotUnchanged(before, await SnapshotAsync());
    }

    [Fact]
    public async Task 新增_不支持单位_受控拒绝_合法装箱单位放行()
    {
        Guard();
        var before = await SnapshotAsync();
        await using var db = _fixture.CreateDbContext();

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            NewController(db, _fixture.PrivilegedUserId)
                .Create(NewOrderBody(_fixture.SupplierId, _fixture.ProductId, "KG")));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("不在既有有效单位口径内", ex.Message);
        AssertSnapshotUnchanged(before, await SnapshotAsync());

        // 既有合法装箱单位（PackageUnit=BOX 且装箱数 > 0）照常放行。
        await using var write = _fixture.CreateDbContext();
        Assert.IsType<OkObjectResult>(await NewController(write, _fixture.PrivilegedUserId)
            .Create(NewOrderBody(_fixture.SupplierId, _fixture.ProductId, "BOX")));
        await using var verify = _fixture.CreateDbContext();
        var stored = await verify.PurchaseOrders.AsNoTracking()
            .Include(o => o.Details).SingleAsync(o => o.Details.Any(d => d.Unit == "BOX"));
        Assert.Equal(_fixture.SupplierId, stored.SupplierId);
    }

    // ==================== 3. 生命周期与提交 / 审核重查 ====================

    [Fact]
    public async Task 合法手工采购_真实SQL完整生命周期_新增修改提交审核()
    {
        Guard();
        var (supplierId, productId) = await SeedTransientMastersAsync();
        var buyerId = await _fixture.SeedBuyerAsync();
        var portId = await _fixture.SeedPortAsync();

        await using var db = _fixture.CreateDbContext();
        var ctl = NewController(db, _fixture.PrivilegedUserId);
        Assert.IsType<OkObjectResult>(await ctl.Create(
            NewOrderBody(supplierId, productId, "PCS", buyerId, portId)));
        var orderId = await LatestOrderIdAsync();

        var update = NewOrderBody(supplierId, productId, "PCS", buyerId, portId);
        update.Remark = "ERP427 真实 SQL 改后";
        Assert.IsType<OkObjectResult>(await ctl.Update(orderId, update));
        Assert.IsType<OkObjectResult>(await ctl.Submit(orderId));
        Assert.IsType<OkObjectResult>(await ctl.Approve(orderId));

        await using var final = _fixture.CreateDbContext();
        var persisted = await final.PurchaseOrders.AsNoTracking().SingleAsync(o => o.Id == orderId);
        Assert.Equal(DocumentStatus.Approved, persisted.Status);
        Assert.Equal(supplierId, persisted.SupplierId);
        Assert.Equal(buyerId, persisted.BuyerId);
        Assert.Equal(portId, persisted.PortId);
        Assert.Equal("ERP427 真实 SQL 改后", persisted.Remark);
    }

    [Fact]
    public async Task 提交_供应商提交前停用_真实SQL原子拒绝且状态不变()
    {
        Guard();
        var (supplierId, productId) = await SeedTransientMastersAsync();
        var order = await _fixture.SeedOrderAsync(supplierId, productId, DocumentStatus.Pending);
        await using (var db = _fixture.CreateDbContext())
        {
            db.BaseSuppliers.Single(s => s.Id == supplierId).Status = 0;
            await db.SaveChangesAsync();
        }

        await using var ctlDb = _fixture.CreateDbContext();
        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            NewController(ctlDb, _fixture.PrivilegedUserId).Submit(order.Id));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        await using var verify = _fixture.CreateDbContext();
        Assert.Equal(DocumentStatus.Pending,
            (await verify.PurchaseOrders.AsNoTracking().SingleAsync(o => o.Id == order.Id)).Status);
    }

    [Fact]
    public async Task 审核_商品审核前删除_真实SQL原子拒绝且状态不变()
    {
        Guard();
        var (supplierId, productId) = await SeedTransientMastersAsync();
        var order = await _fixture.SeedOrderAsync(supplierId, productId, DocumentStatus.Submitted);
        await using (var db = _fixture.CreateDbContext())
        {
            db.BaseProducts.Single(p => p.Id == productId).IsDeleted = true;
            await db.SaveChangesAsync();
        }

        await using var ctlDb = _fixture.CreateDbContext();
        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            NewController(ctlDb, _fixture.PrivilegedUserId).Approve(order.Id));

        Assert.Equal(ErrorCodes.NotFound, ex.Code);
        await using var verify = _fixture.CreateDbContext();
        Assert.Equal(DocumentStatus.Submitted,
            (await verify.PurchaseOrders.AsNoTracking().SingleAsync(o => o.Id == order.Id)).Status);
    }

    // ==================== 4. 历史读取 / 打印只读 ====================

    [Fact]
    public async Task 历史订单_引用失效主数据_真实SQL读取打印仍可读且不被回填()
    {
        Guard();
        var (supplierId, productId) = await SeedTransientMastersAsync();
        var order = await _fixture.SeedOrderAsync(supplierId, productId, DocumentStatus.Approved);
        await using (var db = _fixture.CreateDbContext())
        {
            db.BaseSuppliers.Single(s => s.Id == supplierId).IsDeleted = true;
            db.BaseProducts.Single(p => p.Id == productId).IsDeleted = true;
            await db.SaveChangesAsync();
        }

        await using var ctlDb = _fixture.CreateDbContext();
        var ctl = NewController(ctlDb, _fixture.PrivilegedUserId);
        var read = Assert.IsType<ApiResponse<PurchaseOrder>>(
            Assert.IsType<OkObjectResult>(await ctl.GetById(order.Id)).Value).Data!;
        Assert.Equal(supplierId, read.SupplierId);
        Assert.Equal(productId, Assert.Single(read.Details).ProductId);

        var print = Assert.IsType<ApiResponse<PurchaseOrder>>(
            Assert.IsType<OkObjectResult>(await ctl.GetPrint(order.Id)).Value).Data!;
        Assert.Equal(productId, Assert.Single(print.Details).ProductId);

        await using var verify = _fixture.CreateDbContext();
        var persisted = await verify.PurchaseOrders.AsNoTracking().SingleAsync(o => o.Id == order.Id);
        Assert.Equal(supplierId, persisted.SupplierId);
    }

    private async Task<long> LatestOrderIdAsync()
    {
        await using var db = _fixture.CreateDbContext();
        return await db.PurchaseOrders.AsNoTracking()
            .OrderByDescending(o => o.Id).Select(o => o.Id).FirstAsync();
    }
}

/// <summary>
/// ERP-427 专用 localdb 夹具：目标必须是专用实例 <c>(localdb)\NEWERP_AutoAcceptance</c>、库名前缀
/// <c>NEWERP_AUTOTEST</c> 且 <c>Integrated Security=true</c>；每次运行只创建一个<strong>全新 GUID 后缀库</strong>，
/// 发现同名库已存在立即拒绝，绝不 drop / reset / 复用任何数据库，也绝不读取 appsettings / .env / 生产凭据。
/// </summary>
public sealed class PurchaseOrderMasterReferenceSqlServerFixture : IAsyncLifetime
{
    /// <summary>专用实例（精确匹配）。</summary>
    public const string InstanceTarget = @"(localdb)\NEWERP_AutoAcceptance";

    /// <summary>库名前缀（必须为 NEWERP_AUTOTEST）。</summary>
    public const string DatabasePrefix = "NEWERP_AUTOTEST";

    private const string DefaultDatabaseName = DatabasePrefix + "_POMASTERREF";

    public string ConnectionString { get; private set; } = string.Empty;

    public long PrivilegedUserId { get; private set; }
    public long RestrictedUserId { get; private set; }
    public long SupplierId { get; private set; }
    public long DeletedSupplierId { get; private set; }
    public long DisabledSupplierId { get; private set; }
    public long ProductId { get; private set; }
    public long DeletedProductId { get; private set; }
    public long DisabledProductId { get; private set; }
    public long DisabledBuyerId { get; private set; }
    public long DisabledPortId { get; private set; }
    public long VisibleCustomerId { get; private set; }
    public long OutOfScopeCustomerId { get; private set; }

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = BuildDefaultConnectionString();

        AssertDedicatedTarget(connectionString);
        ConnectionString = connectionString;

        Console.WriteLine($"[ERP-427] 目标库护栏放行（实例 {InstanceTarget}，库名前缀 {DatabasePrefix}）。");
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

        Console.WriteLine("[ERP-427] 集成场景就绪：全新 GUID 库 + 完整 NEWERP 结构 + 种子数据（不清理既有行）。");
    }

    // ==================== 既有授权（不新增权限模型）+ 主数据夹具 ====================

    private async Task SeedAsync()
    {
        await using var db = CreateDbContext();

        // 1) 特权账号（系统内置角色 + 既有「采购订单」菜单）。
        var privilegedRole = new SysRole
        {
            RoleName = "ERP427 特权角色", RoleCode = $"ERP427-P-{Guid.NewGuid():N}", IsSystem = true
        };
        db.SysRoles.Add(privilegedRole);
        await db.SaveChangesAsync();
        var privileged = NewUser(UserStatus.Enabled);
        db.SysUsers.Add(privileged);
        await db.SaveChangesAsync();
        db.SysUserRoles.Add(new SysUserRole { UserId = privileged.Id, RoleId = privilegedRole.Id });
        var menu = await EnsurePurchaseOrderMenuAsync(db);
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = privilegedRole.Id, MenuId = menu.Id });
        await db.SaveChangesAsync();
        PrivilegedUserId = privileged.Id;

        // 2) 受限业务员：既有「采购订单」菜单 + 客户数据范围（登录账号 == 员工编码，ERP-097 权威映射）。
        var restricted = NewUser(UserStatus.Enabled);
        db.SysUsers.Add(restricted);
        await db.SaveChangesAsync();
        var restrictedRole = new SysRole
        {
            RoleName = $"ERP427-{Guid.NewGuid():N}", RoleCode = $"ERP427-{Guid.NewGuid():N}", IsSystem = false
        };
        db.SysRoles.Add(restrictedRole);
        await db.SaveChangesAsync();
        db.SysUserRoles.Add(new SysUserRole { UserId = restricted.Id, RoleId = restrictedRole.Id });
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = restrictedRole.Id, MenuId = menu.Id });
        await db.SaveChangesAsync();
        RestrictedUserId = restricted.Id;

        var restrictedEmployee = new BaseEmployee
        {
            EmployeeCode = restricted.UserName, EmployeeName = "ERP427 受限采购业务员",
            IsSalesman = true, Status = 1
        };
        db.BaseEmployees.Add(restrictedEmployee);
        await db.SaveChangesAsync();

        // 3) 客户：受限可见 / 越界。
        var visibleCustomer = new BaseCustomer
        {
            CustomerCode = $"C-427-{Guid.NewGuid():N}", CustomerName = "ERP427 可见客户",
            EmpId = restrictedEmployee.Id, Status = 1, CreditStatus = "正常"
        };
        var outOfScopeCustomer = new BaseCustomer
        {
            CustomerCode = $"C-427X-{Guid.NewGuid():N}", CustomerName = "ERP427 越界客户",
            Status = 1, CreditStatus = "正常"
        };
        db.BaseCustomers.AddRange(visibleCustomer, outOfScopeCustomer);
        await db.SaveChangesAsync();
        VisibleCustomerId = visibleCustomer.Id;
        OutOfScopeCustomerId = outOfScopeCustomer.Id;

        // 4) 供应商：启用 / 已删除 / 已停用。
        var supplier = new BaseSupplier
        {
            SupplierCode = $"S-427-{Guid.NewGuid():N}", SupplierName = "ERP427 供应商", Status = 1
        };
        var deletedSupplier = new BaseSupplier
        {
            SupplierCode = $"S-427D-{Guid.NewGuid():N}", SupplierName = "ERP427 已删除供应商",
            Status = 1, IsDeleted = true
        };
        var disabledSupplier = new BaseSupplier
        {
            SupplierCode = $"S-427S-{Guid.NewGuid():N}", SupplierName = "ERP427 已停用供应商", Status = 0
        };
        db.BaseSuppliers.AddRange(supplier, deletedSupplier, disabledSupplier);
        await db.SaveChangesAsync();
        SupplierId = supplier.Id;
        DeletedSupplierId = deletedSupplier.Id;
        DisabledSupplierId = disabledSupplier.Id;

        // 5) 采购员：已停用（可选用引用失效场景）。
        var disabledBuyer = new BaseEmployee
        {
            EmployeeCode = $"E-427S-{Guid.NewGuid():N}", EmployeeName = "ERP427 离职采购员", Status = 0
        };
        db.BaseEmployees.Add(disabledBuyer);
        await db.SaveChangesAsync();
        DisabledBuyerId = disabledBuyer.Id;

        // 6) 商品：启用（基础单位 PCS + 合法装箱单位 BOX）/ 已删除 / 已停用。
        var product = new BaseProduct
        {
            ProductCode = $"P-427-{Guid.NewGuid():N}", ProductName = "ERP427 商品",
            Unit = "PCS", PackageUnit = "BOX", UnitsPerPackage = 12, Status = 1
        };
        var deletedProduct = new BaseProduct
        {
            ProductCode = $"P-427D-{Guid.NewGuid():N}", ProductName = "ERP427 已删除商品",
            Unit = "PCS", Status = 1, IsDeleted = true
        };
        var disabledProduct = new BaseProduct
        {
            ProductCode = $"P-427S-{Guid.NewGuid():N}", ProductName = "ERP427 已停用商品",
            Unit = "PCS", Status = 0
        };
        db.BaseProducts.AddRange(product, deletedProduct, disabledProduct);
        await db.SaveChangesAsync();
        ProductId = product.Id;
        DeletedProductId = deletedProduct.Id;
        DisabledProductId = disabledProduct.Id;

        // 7) 起运港字典项：已停用（可选用引用失效场景）。
        var disabledPort = new BaseOtherInfo
        {
            InfoType = PurchaseOrderMasterReferenceRules.PortInfoType,
            InfoCode = $"PORT-S-{Guid.NewGuid():N}", InfoName = "ERP427 已停用起运港", Status = 0
        };
        db.BaseOtherInfos.Add(disabledPort);
        await db.SaveChangesAsync();
        DisabledPortId = disabledPort.Id;

        Console.WriteLine("[ERP-427] 既有授权 + 主数据夹具就绪（受控只读，不新增权限模型）。");
    }

    private static async Task<SysMenu> EnsurePurchaseOrderMenuAsync(ErpDbContext db)
    {
        var menu = await db.SysMenus.FirstOrDefaultAsync(m =>
            m.MenuCode == PurchaseOrderAuthorizationRules.RequiredMenuCode && !m.IsDeleted);
        if (menu is not null) return menu;

        menu = new SysMenu
        {
            MenuName = PurchaseOrderAuthorizationRules.RequiredMenuText,
            MenuCode = PurchaseOrderAuthorizationRules.RequiredMenuCode,
            MenuType = MenuType.Menu
        };
        db.SysMenus.Add(menu);
        await db.SaveChangesAsync();
        return menu;
    }

    /// <summary>为本用例播种一条**专用**的启用采购员，返回其 Id。</summary>
    public async Task<long> SeedBuyerAsync()
    {
        await using var db = CreateDbContext();
        var buyer = new BaseEmployee
        {
            EmployeeCode = $"E-T-{Guid.NewGuid():N}", EmployeeName = "ERP427 临时采购员", Status = 1
        };
        db.BaseEmployees.Add(buyer);
        await db.SaveChangesAsync();
        return buyer.Id;
    }

    /// <summary>为本用例播种一条**专用**的启用起运港字典项，返回其 Id。</summary>
    public async Task<long> SeedPortAsync()
    {
        await using var db = CreateDbContext();
        var port = new BaseOtherInfo
        {
            InfoType = PurchaseOrderMasterReferenceRules.PortInfoType,
            InfoCode = $"PORT-T-{Guid.NewGuid():N}", InfoName = "ERP427 临时起运港", Status = 1
        };
        db.BaseOtherInfos.Add(port);
        await db.SaveChangesAsync();
        return port.Id;
    }

    /// <summary>播种一条规范采购订单（供应商 / 商品为既有合法主数据），供提交 / 审核与历史读取场景使用。</summary>
    public async Task<PurchaseOrder> SeedOrderAsync(long supplierId, long productId, DocumentStatus status)
    {
        await using var db = CreateDbContext();
        var order = new PurchaseOrder
        {
            OrderNo = $"PO-427-{Guid.NewGuid():N}",
            OrderDate = DateTime.Today,
            SupplierId = supplierId,
            Currency = Currency.CNY,
            ExchangeRate = 1m,
            Status = status,
            TotalAmount = 10m,
            Details = new List<PurchaseOrderDetail>
            {
                new()
                {
                    ProductId = productId, ProductName = "ERP427 商品", Spec = "规格A",
                    Unit = "PCS", Quantity = 2m, UnitPrice = 5m, Amount = 10m
                }
            }
        };
        db.PurchaseOrders.Add(order);
        await db.SaveChangesAsync();
        return order;
    }

    private static SysUser NewUser(UserStatus status) => new()
    {
        UserName = $"erp427-{Guid.NewGuid():N}", PasswordHash = "hash", PasswordSalt = "salt",
        DisplayName = "ERP427 隔离账号", Status = status
    };
}

/// <summary>专用目标护栏的 fail-closed 覆盖：错误实例 / 错误库名 / 非集成安全必须在访问数据库之前被拒绝。</summary>
public sealed class PurchaseOrderMasterReferenceTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;User Id=sa;Password=secret")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(
            () => PurchaseOrderMasterReferenceSqlServerFixture.AssertDedicatedTarget(connection));
}
