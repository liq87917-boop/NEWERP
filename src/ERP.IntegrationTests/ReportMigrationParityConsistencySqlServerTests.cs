using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using ERP.Infrastructure.Export;
using ERP.Infrastructure.Reports;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace ERP.IntegrationTests;

/// <summary>
/// ERP-338 Stage 2 三方一致读取作用域 SQL Server 集成测试：
/// 在专用 localdb 目标上自包含播种非空夹具，验证旧来源 / 通用预览 / 实际旧产物共享同一个 Snapshot 一致只读事务，
/// 两次读取之间源数据变化仍输出一致（绝不混合），以及权限撤销 / 最终复核的 fail closed。
/// <para>安全口径：目标护栏强制专用 localdb 实例 <c>NEWERP_AutoAcceptance</c> 且库名前缀 <c>NEWERP_AUTOTEST</c>；
/// 连接串只来自进程环境变量或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据。</para>
/// </summary>
public sealed class ReportMigrationParityConsistencySqlServerTests
    : IClassFixture<ReportMigrationParityConsistencySqlServerFixture>
{
    private readonly ReportMigrationParityConsistencySqlServerFixture _fixture;

    public ReportMigrationParityConsistencySqlServerTests(
        ReportMigrationParityConsistencySqlServerFixture fixture)
        => _fixture = fixture;

    private void Guard()
    {
        var builder = new SqlConnectionStringBuilder(_fixture.ConnectionString);
        Assert.Equal($"(localdb)\\{ReportMigrationParityConsistencySqlServerFixture.InstanceMarker}",
            builder.DataSource, ignoreCase: true);
        Assert.StartsWith(ReportMigrationParityConsistencySqlServerFixture.DatabasePrefix,
            builder.InitialCatalog, StringComparison.OrdinalIgnoreCase);
        Assert.True(builder.IntegratedSecurity);
    }

    private static IReportConfigurationDatasetProvider[] BuildProviders(ErpDbContext db, ReportService reportService)
        => new IReportConfigurationDatasetProvider[]
        {
            new ProductSalesRankingReportConfigurationDatasetProvider(reportService, db),
        };

    private IReportMigrationParityEvidenceService BuildService(ErpDbContext db)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = _fixture.ConnectionString,
            })
            .Build();
        var billReader = new LegacyBillExportReadService(new StoredProcedureService(config));

        var reportService = new ReportService(db);
        var salesQuery = new DynamicSalesOrderReportQuery(db);
        var receivableQuery = new DynamicReceivableReportQuery(db);
        var purchaseQuery = new DynamicPurchaseOrderReportQuery(db);

        var legacySource = new LegacyReportSourceRegistry(
            db, reportService, salesQuery, receivableQuery, purchaseQuery, billReader);

        var providers = BuildProviders(db, reportService);
        var scopeFactory = new ReportMigrationParityReadScopeFactory(db, providers);

        return new ReportMigrationParityEvidenceService(
            legacySource,
            providers,
            new ReportMigrationParityComparator(),
            new ReportMigrationOutputSemanticsComparator(),
            new ReportConfigurationExecutionBudget(),
            new ReportMigrationParityEvidenceProvider(),
            artifactSource: null,
            readScopeFactory: scopeFactory);
    }

    [Fact]
    public async Task 三方一致读取_SQLServer_完整证据_parity_passed()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();

        var evidence = await BuildService(db).GetEvidenceAsync(
            "report:product-sales-ranking", _fixture.PrivilegedUserId);

        Assert.NotNull(evidence);
        Assert.True(evidence!.Complete);
    }

    [Fact]
    public async Task 三方一致读取_SQLServer_菜单撤销_fail_closed()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var userId = _fixture.PrivilegedUserId;

        var roleId = await db.SysUserRoles.Where(r => r.UserId == userId).Select(r => r.RoleId).FirstAsync();
        var menuId = await db.SysMenus.Where(m => m.MenuCode == "product-sales-ranking" && !m.IsDeleted)
            .Select(m => m.Id).FirstAsync();

        await using (var revokeDb = _fixture.CreateDbContext())
        {
            var link = await revokeDb.SysRoleMenus.FirstOrDefaultAsync(
                rm => rm.RoleId == roleId && rm.MenuId == menuId && !rm.IsDeleted);
            Assert.NotNull(link);
            revokeDb.SysRoleMenus.Remove(link!);
            await revokeDb.SaveChangesAsync();
        }

        try
        {
            Guard();
            var evidence = await BuildService(db).GetEvidenceAsync(
                "report:product-sales-ranking", userId);
            Assert.Null(evidence);
        }
        finally
        {
            await using var restoreDb = _fixture.CreateDbContext();
            if (!await restoreDb.SysRoleMenus.AnyAsync(rm => rm.RoleId == roleId && rm.MenuId == menuId && !rm.IsDeleted))
            {
                restoreDb.SysRoleMenus.Add(new SysRoleMenu { RoleId = roleId, MenuId = menuId });
                await restoreDb.SaveChangesAsync();
            }
        }
    }

    [Fact]
    public async Task 三方一致读取_SQLServer_快照隔离_源变更_一致而非混合()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var factory = new ReportMigrationParityReadScopeFactory(
            db, BuildProviders(db, new ReportService(db)));

        var product = await db.BaseProducts.SingleAsync(
            p => p.ProductCode == ReportMigrationParityConsistencySqlServerFixture.ProductCode && !p.IsDeleted);

        await using var scope = await factory.OpenAsync(_fixture.PrivilegedUserId, "corr-erp338-snapshot");

        var before = await db.BaseProducts.AsNoTracking()
            .Where(p => p.Id == product.Id).Select(p => p.SalePrice).SingleAsync();

        // 两次读取之间，用独立连接把同一来源记录改掉（商品当前售价）。
        await using (var writer = _fixture.CreateDbContext())
        {
            var target = await writer.BaseProducts.SingleAsync(p => p.Id == product.Id);
            target.SalePrice = before + 999m;
            await writer.SaveChangesAsync();
        }

        // 第二次读取仍应看到同一 Snapshot 内的原始值，绝不混合跨快照来源。
        var after = await db.BaseProducts.AsNoTracking()
            .Where(p => p.Id == product.Id).Select(p => p.SalePrice).SingleAsync();

        Assert.Equal(before, after);

        await scope.CompleteAsync();
    }

    [Fact]
    public async Task 三方一致读取_SQLServer_快照读取后权限撤销_最终复核拒绝()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var userId = _fixture.PrivilegedUserId;
        var factory = new ReportMigrationParityReadScopeFactory(
            db, BuildProviders(db, new ReportService(db)));

        await using var readScope = await factory.OpenAsync(userId, "erp338-mid-read-revocation");

        // 在 Snapshot 内完成一次读取。
        _ = await db.BaseProducts.AsNoTracking().CountAsync();

        var roleId = await db.SysUserRoles.Where(r => r.UserId == userId).Select(r => r.RoleId).FirstAsync();
        var menuId = await db.SysMenus.Where(m => m.MenuCode == "product-sales-ranking" && !m.IsDeleted)
            .Select(m => m.Id).FirstAsync();

        await using (var revokeDb = _fixture.CreateDbContext())
        {
            var link = await revokeDb.SysRoleMenus.FirstOrDefaultAsync(
                rm => rm.RoleId == roleId && rm.MenuId == menuId && !rm.IsDeleted);
            Assert.NotNull(link);
            revokeDb.SysRoleMenus.Remove(link!);
            await revokeDb.SaveChangesAsync();
        }

        try
        {
            Guard();
            var ex = await Assert.ThrowsAsync<BusinessException>(() =>
                readScope.RecheckAsync(userId, new ReportMigrationParityReadTarget(
                    "report:product-sales-ranking",
                    "product-sales-ranking",
                    new[] { "product-sales-ranking" })));
            Assert.Equal(ErrorCodes.Forbidden, ex.Code);
        }
        finally
        {
            await using var restoreDb = _fixture.CreateDbContext();
            if (!await restoreDb.SysRoleMenus.AnyAsync(rm => rm.RoleId == roleId && rm.MenuId == menuId && !rm.IsDeleted))
            {
                restoreDb.SysRoleMenus.Add(new SysRoleMenu { RoleId = roleId, MenuId = menuId });
                await restoreDb.SaveChangesAsync();
            }
        }
    }
}

/// <summary>
/// 专用 localdb 目标 Fixture：一次性启动序列（自动建表 + 结构升级 + 种子 + 再升级 + 启用 Snapshot 隔离）
/// + 幂等非空业务夹具（用户 / 角色 / 菜单 / 客户 / 业务员 / 商品 / 已审核销售订单 / 已审核销售出库）。
/// </summary>
public sealed class ReportMigrationParityConsistencySqlServerFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";
    public const string DefaultDatabaseName = "NEWERP_AUTOTEST_ERP338";

    public const string ProductCode = "ERP338-P1";
    private const string CustomerCode = "ERP338-C1";
    private const string UserName = "ERP338-ADMIN";
    private const string RoleCode = "ERP338-SYS";
    private const string EmployeeCode = "ERP338-E1";

    public string ConnectionString { get; private set; } = string.Empty;
    public long PrivilegedUserId { get; private set; }

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = BuildDefaultConnectionString();

        AssertDedicatedTarget(connectionString);
        ConnectionString = connectionString;

        await using (var db = CreateDbContext())
        {
            await db.Database.EnsureCreatedAsync();
            await SchemaUpgrader.EnsureUpgradedAsync(db);
            await SeedData.InitializeAsync(db);
            await SchemaUpgrader.EnsureUpgradedAsync(db);
            await EnableSnapshotIsolationAsync(db);
        }

        await SeedFixtureAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public ErpDbContext CreateDbContext() => new(BuildOptions());

    private async Task SeedFixtureAsync()
    {
        AssertDedicatedTarget(ConnectionString);
        await using var db = CreateDbContext();

        var menu = await EnsureMenuAsync(db, "product-sales-ranking");
        var user = await EnsureUserAsync(db);
        var role = await EnsureRoleAsync(db);

        if (!await db.SysUserRoles.AnyAsync(ur => ur.UserId == user.Id && ur.RoleId == role.Id))
        {
            db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
            await db.SaveChangesAsync();
        }

        if (!await db.SysRoleMenus.AnyAsync(rm => rm.RoleId == role.Id && rm.MenuId == menu.Id))
        {
            db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
            await db.SaveChangesAsync();
        }

        var customer = await EnsureCustomerAsync(db);
        var employee = await EnsureEmployeeAsync(db);
        var product = await EnsureProductAsync(db);

        if (!await db.SalesOrders.AnyAsync(o => o.OrderNo == "ERP338-SO-1" && !o.IsDeleted))
        {
            db.SalesOrders.Add(new SalesOrder
            {
                OrderNo = "ERP338-SO-1",
                OrderDate = DateTime.Today,
                CustomerId = customer.Id,
                SalesmanId = employee.Id,
                Currency = Currency.USD,
                TotalAmount = 1200m,
                Status = DocumentStatus.Approved,
            });
            await db.SaveChangesAsync();
        }

        if (!await db.StockOuts.AnyAsync(o => o.StockOutNo == "ERP338-OUT-1" && !o.IsDeleted))
        {
            var stockOut = new StockOut
            {
                StockOutNo = "ERP338-OUT-1",
                StockOutDate = DateTime.Today,
                CustomerId = customer.Id,
                Status = DocumentStatus.Approved,
            };
            db.StockOuts.Add(stockOut);
            await db.SaveChangesAsync();

            db.StockOutDetails.Add(new StockOutDetail
            {
                StockOutId = stockOut.Id,
                ProductId = product.Id,
                ProductName = "ERP338 商品",
                Spec = "标准",
                Unit = "PCS",
                Quantity = 30m,
            });
            await db.SaveChangesAsync();
        }

        PrivilegedUserId = user.Id;
    }

    private static async Task<SysMenu> EnsureMenuAsync(ErpDbContext db, string code)
    {
        var menu = await db.SysMenus.FirstOrDefaultAsync(m => m.MenuCode == code && !m.IsDeleted);
        if (menu is not null)
            return menu;

        menu = new SysMenu { MenuName = code, MenuCode = code, MenuType = MenuType.Menu };
        db.SysMenus.Add(menu);
        await db.SaveChangesAsync();
        return menu;
    }

    private static async Task<SysUser> EnsureUserAsync(ErpDbContext db)
    {
        var user = await db.SysUsers.FirstOrDefaultAsync(u => u.UserName == UserName && !u.IsDeleted);
        if (user is not null)
            return user;

        user = new SysUser
        {
            UserName = UserName,
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = "ERP338 隔离账号",
            Status = UserStatus.Enabled,
        };
        db.SysUsers.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    private static async Task<SysRole> EnsureRoleAsync(ErpDbContext db)
    {
        var role = await db.SysRoles.FirstOrDefaultAsync(r => r.RoleCode == RoleCode && !r.IsDeleted);
        if (role is not null)
            return role;

        role = new SysRole { RoleName = RoleCode, RoleCode = RoleCode, IsSystem = true };
        db.SysRoles.Add(role);
        await db.SaveChangesAsync();
        return role;
    }

    private static async Task<BaseCustomer> EnsureCustomerAsync(ErpDbContext db)
    {
        var customer = await db.BaseCustomers.FirstOrDefaultAsync(c => c.CustomerCode == CustomerCode && !c.IsDeleted);
        if (customer is not null)
            return customer;

        customer = new BaseCustomer { CustomerCode = CustomerCode, CustomerName = "ERP338 客户", Status = 1, CreditStatus = "正常" };
        db.BaseCustomers.Add(customer);
        await db.SaveChangesAsync();
        return customer;
    }

    private static async Task<BaseEmployee> EnsureEmployeeAsync(ErpDbContext db)
    {
        var employee = await db.BaseEmployees.FirstOrDefaultAsync(e => e.EmployeeCode == EmployeeCode && !e.IsDeleted);
        if (employee is not null)
            return employee;

        employee = new BaseEmployee { EmployeeCode = EmployeeCode, EmployeeName = "ERP338 业务员", IsSalesman = true, Status = 1 };
        db.BaseEmployees.Add(employee);
        await db.SaveChangesAsync();
        return employee;
    }

    private static async Task<BaseProduct> EnsureProductAsync(ErpDbContext db)
    {
        var product = await db.BaseProducts.FirstOrDefaultAsync(p => p.ProductCode == ProductCode && !p.IsDeleted);
        if (product is not null)
            return product;

        product = new BaseProduct
        {
            ProductCode = ProductCode,
            ProductName = "ERP338 商品",
            Spec = "标准",
            Unit = "PCS",
            SalePrice = 40m,
            Status = 1,
        };
        db.BaseProducts.Add(product);
        await db.SaveChangesAsync();
        return product;
    }

    private DbContextOptions<ErpDbContext> BuildOptions()
        => new DbContextOptionsBuilder<ErpDbContext>()
            .UseSqlServer(ConnectionString)
            .Options;

    private static string BuildDefaultConnectionString()
        => $"Server=(localdb)\\{InstanceMarker};Initial Catalog={DefaultDatabaseName};Integrated Security=true;TrustServerCertificate=true;";

    private static void AssertDedicatedTarget(string connectionString)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        Assert.Equal($"(localdb)\\{InstanceMarker}", builder.DataSource, ignoreCase: true);
        Assert.True(builder.IntegratedSecurity);
        Assert.StartsWith(DatabasePrefix, builder.InitialCatalog, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task EnableSnapshotIsolationAsync(ErpDbContext db)
    {
        // 目标库名来自已通过 AssertDedicatedTarget 校验的专用连接串，仅在受控夹具内使用。
        var databaseName = new SqlConnectionStringBuilder(db.Database.GetConnectionString()!).InitialCatalog;
        await db.Database.ExecuteSqlRawAsync(
            "ALTER DATABASE [" + databaseName + "] SET ALLOW_SNAPSHOT_ISOLATION ON;");
    }
}

