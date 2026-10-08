using ERP.Application.DTOs;
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
/// ERP-337 旧报表实际产物适配器目录 SQL Server 集成测试：在专用 localdb 目标上自包含播种非空夹具，
/// 通过有界旧产物来源执行动态销售订单 / 单证台账导出的真实规范产物，并验证有限能力矩阵与未知键 fail closed。
/// <para>安全口径：写库前每次都断言目标身份（实例含 <c>NEWERP_AutoAcceptance</c>、库名前缀 <c>NEWERP_AUTOTEST</c>、
/// 集成安全）；连接串只来自进程环境变量或专用 localdb 默认值。</para>
/// </summary>
public sealed class LegacyReportArtifactCatalogSqlServerTests
    : IClassFixture<LegacyReportArtifactCatalogSqlServerFixture>
{
    private readonly LegacyReportArtifactCatalogSqlServerFixture _fixture;

    public LegacyReportArtifactCatalogSqlServerTests(LegacyReportArtifactCatalogSqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    private void Guard()
    {
        var builder = new SqlConnectionStringBuilder(_fixture.ConnectionString);
        Assert.Equal($"(localdb)\\{LegacyReportArtifactCatalogSqlServerFixture.InstanceMarker}", builder.DataSource, ignoreCase: true);
        Assert.StartsWith(LegacyReportArtifactCatalogSqlServerFixture.DatabasePrefix, builder.InitialCatalog);
        Assert.True(builder.IntegratedSecurity);
    }

    [Fact]
    public async Task 动态销售订单_真实规范产物_Excel与PDF()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var source = BuildSource(db);

        var artifacts = await source.ReadArtifactsAsync(new LegacyReportSourceRequest
        {
            LegacyKey = "dynamic:sales-order",
            UserId = _fixture.PrivilegedUserId,
        });

        Assert.NotNull(artifacts);
        Assert.NotNull(artifacts!.ExcelBytes);
        Assert.NotNull(artifacts.PdfBytes);
        Assert.StartsWith("PK", System.Text.Encoding.ASCII.GetString(artifacts.ExcelBytes));
        Assert.StartsWith("%PDF-", System.Text.Encoding.ASCII.GetString(artifacts.PdfBytes));
    }

    [Fact]
    public async Task 单证台账导出_真实规范产物_Excel()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();

        var document = await db.TradeDocuments.FirstOrDefaultAsync(d => !d.IsDeleted);
        Assert.NotNull(document);

        var source = BuildSource(db);
        var artifacts = await source.ReadArtifactsAsync(new LegacyReportSourceRequest
        {
            LegacyKey = "document:trade-document-export-excel",
            UserId = _fixture.PrivilegedUserId,
            DocumentId = document!.Id,
        });

        Assert.NotNull(artifacts);
        Assert.NotNull(artifacts!.ExcelBytes);
        Assert.Null(artifacts.PdfBytes);
        Assert.StartsWith("PK", System.Text.Encoding.ASCII.GetString(artifacts.ExcelBytes));
    }

    [Fact]
    public void 能力矩阵_受支持与阻塞族_精确一致()
    {
        Guard();

        AssertSupported("dynamic:sales-order", excel: true, pdf: true);
        AssertSupported("dynamic:receivable", excel: true, pdf: true);
        AssertSupported("dynamic:purchase-order", excel: true, pdf: true);
        AssertSupported("export:bill-proc:stock-in", excel: true, pdf: false);
        AssertSupported("packet:customer-report-packet", excel: true, pdf: true);
        AssertSupported("document:trade-document-export-excel", excel: true, pdf: false);

        AssertBlocked("document:trade-document-print");
        AssertBlocked("print-template:customer");
        AssertBlocked("print-template:quotation");
    }

    [Fact]
    public async Task 未知旧报表键_产物null()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var source = BuildSource(db);

        var artifacts = await source.ReadArtifactsAsync(new LegacyReportSourceRequest
        {
            LegacyKey = "unknown:legacy-key",
            UserId = _fixture.PrivilegedUserId,
        });

        Assert.Null(artifacts);
    }

    private static void AssertSupported(string key, bool excel, bool pdf)
    {
        Assert.True(LegacyReportArtifactCatalog.TryResolve(key, out var capability), $"缺少能力矩阵条目：{key}");
        Assert.Equal(LegacyReportArtifactCapabilityStatus.Supported, capability.Status);
        Assert.Equal(excel, capability.ExcelSupported);
        Assert.Equal(pdf, capability.PdfSupported);
    }

    private static void AssertBlocked(string key)
    {
        Assert.True(LegacyReportArtifactCatalog.TryResolve(key, out var capability), $"缺少能力矩阵条目：{key}");
        Assert.Equal(LegacyReportArtifactCapabilityStatus.Blocked, capability.Status);
        Assert.False(string.IsNullOrWhiteSpace(capability.BlockReason));
    }

    private LegacyReportArtifactSource BuildSource(ErpDbContext db)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = _fixture.ConnectionString,
            })
            .Build();

        var billReader = new LegacyBillExportReadService(new StoredProcedureService(config));

        return new LegacyReportArtifactSource(
            new DynamicSalesOrderReportQuery(db),
            new DynamicReceivableReportQuery(db),
            new DynamicPurchaseOrderReportQuery(db),
            billReader,
            db);
    }
}

/// <summary>
/// 专用 localdb 目标 Fixture：一次性启动序列（自动建表 + 结构升级 + 种子 + 再升级）+ 幂等非空业务夹具。
/// </summary>
public sealed class LegacyReportArtifactCatalogSqlServerFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";
    public const string DefaultDatabaseName = "NEWERP_AUTOTEST_ERP337";

    private const string CustomerCode = "ERP337-C1";
    private const string UserName = "ERP337-ADMIN";
    private const string RoleCode = "ERP337-SYS";
    private const string EmployeeCode = "ERP337-E1";
    private const string ProductCode = "ERP337-P1";

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
        }

        await SeedFixtureAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public ErpDbContext CreateDbContext() => new(BuildOptions());

    private async Task SeedFixtureAsync()
    {
        AssertDedicatedTarget(ConnectionString);
        await using var db = CreateDbContext();

        var menus = new[]
        {
            await EnsureMenuAsync(db, "sales-order"),
            await EnsureMenuAsync(db, "customer"),
            await EnsureMenuAsync(db, "doc-center"),
        };

        var user = await EnsureUserAsync(db);
        var role = await EnsureRoleAsync(db);

        if (!await db.SysUserRoles.AnyAsync(ur => ur.UserId == user.Id && ur.RoleId == role.Id))
        {
            db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
            await db.SaveChangesAsync();
        }

        foreach (var menu in menus)
        {
            if (!await db.SysRoleMenus.AnyAsync(rm => rm.RoleId == role.Id && rm.MenuId == menu.Id))
            {
                db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
                await db.SaveChangesAsync();
            }
        }

        var customer = await EnsureCustomerAsync(db);
        var employee = await EnsureEmployeeAsync(db);
        var product = await EnsureProductAsync(db);

        if (!await db.SalesOrders.AnyAsync(o => o.OrderNo == "ERP337-SO-1" && !o.IsDeleted))
        {
            db.SalesOrders.Add(new SalesOrder
            {
                OrderNo = "ERP337-SO-1",
                OrderDate = DateTime.Today,
                CustomerId = customer.Id,
                SalesmanId = employee.Id,
                Currency = Currency.USD,
                TotalAmount = 1200m,
                Status = DocumentStatus.Approved,
            });
            await db.SaveChangesAsync();
        }

        if (!await db.TradeDocuments.AnyAsync(d => d.DocNo == "ERP337-CI-1" && !d.IsDeleted))
        {
            db.TradeDocuments.Add(new TradeDocument
            {
                DocNo = "ERP337-CI-1",
                DocType = "商业发票",
                IssueDate = DateTime.Today,
                CustomerId = customer.Id,
                CustomerName = customer.CustomerName ?? "ERP337 客户",
                Currency = "USD",
                Amount = 1200m,
                Status = "待制作",
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
            DisplayName = "ERP337 隔离账号",
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

        customer = new BaseCustomer { CustomerCode = CustomerCode, CustomerName = "ERP337 客户", Status = 1, CreditStatus = "正常" };
        db.BaseCustomers.Add(customer);
        await db.SaveChangesAsync();
        return customer;
    }

    private static async Task<BaseEmployee> EnsureEmployeeAsync(ErpDbContext db)
    {
        var employee = await db.BaseEmployees.FirstOrDefaultAsync(e => e.EmployeeCode == EmployeeCode && !e.IsDeleted);
        if (employee is not null)
            return employee;

        employee = new BaseEmployee { EmployeeCode = EmployeeCode, EmployeeName = "ERP337 业务员", IsSalesman = true, Status = 1 };
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
            ProductName = "ERP337 商品",
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
        Assert.Equal($"(localdb)\\{InstanceMarker}", builder.DataSource ?? string.Empty, ignoreCase: true);
        Assert.True(builder.IntegratedSecurity);
        Assert.StartsWith(DatabasePrefix, builder.InitialCatalog ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }
}
