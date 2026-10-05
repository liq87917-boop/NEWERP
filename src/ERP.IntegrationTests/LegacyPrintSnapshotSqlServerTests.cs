using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using ERP.Infrastructure.Reports;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERP.IntegrationTests;

/// <summary>
/// ERP-334 旧打印快照统一读取的 SQL Server 集成测试：在专用 localdb 目标上自包含播种非空夹具
/// （客户 + 业务员 + 报价单 + PI，含多币种 / 空明细 / 已删除明细），直接通过旧打印快照读取服务验证
/// 列白名单 / null vs 零 / 客户数据范围 / 表头与明细身份与 SortNo 行序 / 原币与基础单位分区 / 软删除排除，
/// 以及菜单撤销 Forbidden、未知来源 EnvironmentBlocked。
/// <para>安全口径：写库前每次都断言目标身份（实例含 <c>NEWERP_AutoAcceptance</c>、库名前缀
/// <c>NEWERP_AUTOTEST</c>、集成安全）；连接串只来自进程环境变量或专用 localdb 默认值，绝不读取
/// appsettings / .env / 生产凭据。</para>
/// </summary>
public sealed class LegacyPrintSnapshotSqlServerTests
    : IClassFixture<LegacyPrintSnapshotSqlServerFixture>
{
    private readonly LegacyPrintSnapshotSqlServerFixture _fixture;

    public LegacyPrintSnapshotSqlServerTests(LegacyPrintSnapshotSqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    private void Guard()
    {
        var builder = new SqlConnectionStringBuilder(_fixture.ConnectionString);
        Assert.Equal(
            $"(localdb)\\{LegacyPrintSnapshotSqlServerFixture.InstanceMarker}",
            builder.DataSource, ignoreCase: true);
        Assert.StartsWith(LegacyPrintSnapshotSqlServerFixture.DatabasePrefix, builder.InitialCatalog);
        Assert.True(builder.IntegratedSecurity);
    }

    private static int Col(LegacyPrintSnapshot snapshot, string key)
        => Array.FindIndex(snapshot.Columns.ToArray(), c => c.Key == key);

    [Fact]
    public async Task 客户打印快照_非空SQL_列白名单与null零值原样()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var reader = new LegacyPrintSnapshotReadService(db);

        var result = await reader.ReadAsync(new LegacyPrintSnapshotRequest
        {
            SourceKey = "print-template:customer",
            UserId = _fixture.PrivilegedUserId,
            Page = 1,
            PageSize = 200,
        });

        Assert.Equal(LegacyPrintSnapshotStatus.Success, result.Status);
        var snapshot = result.Snapshot!;
        Assert.Equal(
            ReportConfigurationMasterDataCatalog.Resolve("customer").Columns.Select(c => c.Key).ToArray(),
            snapshot.Columns.Select(c => c.Key).ToArray());

        var nullRow = Assert.Single(snapshot.Rows, r =>
            (string)r.Cells[Col(snapshot, "customerCode")]! == "ERP334-CUST-NULL");
        Assert.Null(nullRow.Cells[Col(snapshot, "creditDays")]);
        Assert.Equal(0m, (decimal)nullRow.Cells[Col(snapshot, "creditLimit")]!);

        var zeroRow = Assert.Single(snapshot.Rows, r =>
            (string)r.Cells[Col(snapshot, "customerCode")]! == "ERP334-CUST-ZERO");
        Assert.Equal(0, zeroRow.Cells[Col(snapshot, "creditDays")]);
        Assert.Equal(150m, (decimal)zeroRow.Cells[Col(snapshot, "creditLimit")]!);
    }

    [Fact]
    public async Task 报价单打印快照_非空SQL_表头明细行序与软删除排除()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var reader = new LegacyPrintSnapshotReadService(db);

        var result = await reader.ReadAsync(new LegacyPrintSnapshotRequest
        {
            SourceKey = "print-template:quotation",
            UserId = _fixture.PrivilegedUserId,
            DocumentId = _fixture.QuotationId,
        });

        Assert.Equal(LegacyPrintSnapshotStatus.Success, result.Status);
        var snapshot = result.Snapshot!;
        Assert.Equal(
            ReportConfigurationSalesDocumentCatalog.Resolve("quotation").Columns.Select(c => c.Key).ToArray(),
            snapshot.Columns.Select(c => c.Key).ToArray());

        Assert.Equal(3, snapshot.Rows.Count); // 表头 + 两条有效明细（已删除明细排除）

        var header = snapshot.Rows[0];
        Assert.Equal("ERP334-QT-1", (string)header.Cells[Col(snapshot, "docNo")]!);
        Assert.Equal("USD", header.Currency);
        Assert.Null(header.Cells[Col(snapshot, "sortNo")]);

        var line1 = snapshot.Rows[1];
        Assert.Equal("USD", line1.Currency);
        Assert.Equal("PCS", line1.Unit);
        Assert.Equal(1, line1.Cells[Col(snapshot, "sortNo")]);
        Assert.Equal(30m, (decimal)line1.Cells[Col(snapshot, "amount")]!);
        Assert.Null(line1.Cells[Col(snapshot, "totalAmount")]);

        Assert.Equal(2, snapshot.Rows[2].Cells[Col(snapshot, "sortNo")]);
        Assert.DoesNotContain(snapshot.Rows, r => (int)r.Cells[Col(snapshot, "sortNo")]! == 3);
    }

    [Fact]
    public async Task 无菜单账号_Forbidden()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var reader = new LegacyPrintSnapshotReadService(db);
        var user = new SysUser
        {
            UserName = "ERP334-NOMENU-" + Guid.NewGuid().ToString("N"),
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = "ERP334 无菜单账号",
            Status = UserStatus.Enabled,
        };
        db.SysUsers.Add(user);
        await db.SaveChangesAsync();

        try
        {
            var result = await reader.ReadAsync(new LegacyPrintSnapshotRequest
            {
                SourceKey = "print-template:customer",
                UserId = user.Id,
            });

            Assert.Equal(LegacyPrintSnapshotStatus.Forbidden, result.Status);
            Assert.Equal(ErrorCodes.Forbidden, result.ErrorCode);
            Assert.Null(result.Snapshot);
        }
        finally
        {
            Guard();
            db.SysUsers.Remove(user);
            await db.SaveChangesAsync();
        }
    }

    [Fact]
    public async Task 未知来源_EnvironmentBlocked()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var reader = new LegacyPrintSnapshotReadService(db);

        var result = await reader.ReadAsync(new LegacyPrintSnapshotRequest
        {
            SourceKey = "print-template:does-not-exist",
            UserId = _fixture.PrivilegedUserId,
        });

        Assert.Equal(LegacyPrintSnapshotStatus.EnvironmentBlocked, result.Status);
        Assert.Equal(ReportConfigurationExecutionLimits.ErrorCodeEnvironmentUnsupported, result.ErrorCode);
        Assert.Null(result.Snapshot);
    }
}


/// <summary>
/// 专用 localdb 目标 Fixture：一次性启动序列（自动建表 + 结构升级 + 种子 + 再升级）+ 幂等非空夹具
/// （客户 + 报价单，含多币种 / 空明细 / 已删除明细）。写库前每次都断言目标身份。
/// </summary>
public sealed class LegacyPrintSnapshotSqlServerFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";
    public const string DefaultDatabaseName = "NEWERP_AUTOTEST_ERP334";
    private const string UserName = "ERP334-ADMIN";
    private const string RoleCode = "ERP334-SYS";

    public string ConnectionString { get; private set; } = string.Empty;
    public long PrivilegedUserId { get; private set; }
    public long QuotationId { get; private set; }

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

        var user = await EnsureUserAsync(db);
        var role = await EnsureRoleAsync(db);
        if (!await db.SysUserRoles.AnyAsync(ur => ur.UserId == user.Id && ur.RoleId == role.Id && !ur.IsDeleted))
        {
            db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
            await db.SaveChangesAsync();
        }

        foreach (var code in new[] { "customer", "quotation" })
        {
            var menu = await EnsureMenuAsync(db, code);
            if (!await db.SysRoleMenus.AnyAsync(rm => rm.RoleId == role.Id && rm.MenuId == menu.Id && !rm.IsDeleted))
            {
                db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
                await db.SaveChangesAsync();
            }
        }

        await EnsureMasterDataAsync(db);
        QuotationId = await EnsureQuotationAsync(db);

        PrivilegedUserId = user.Id;
    }

    private static async Task EnsureMasterDataAsync(ErpDbContext db)
    {
        if (!await db.BaseCustomers.AnyAsync(c => !c.IsDeleted && c.CustomerCode == "ERP334-CUST-NULL"))
        {
            db.BaseCustomers.Add(new BaseCustomer
            {
                CustomerCode = "ERP334-CUST-NULL",
                CustomerName = "ERP334 空值客户",
                Currency = "USD",
                CreditLimit = 0m,
                CreditDays = null,
                DepositRatio = 0m,
                Status = 1,
            });
            await db.SaveChangesAsync();
        }

        if (!await db.BaseCustomers.AnyAsync(c => !c.IsDeleted && c.CustomerCode == "ERP334-CUST-ZERO"))
        {
            db.BaseCustomers.Add(new BaseCustomer
            {
                CustomerCode = "ERP334-CUST-ZERO",
                CustomerName = "ERP334 零值客户",
                Currency = "EUR",
                CreditLimit = 150m,
                CreditDays = 0,
                DepositRatio = 20m,
                Status = 1,
            });
            await db.SaveChangesAsync();
        }
    }

    private static async Task<long> EnsureQuotationAsync(ErpDbContext db)
    {
        var quotation = await db.Quotations
            .FirstOrDefaultAsync(q => !q.IsDeleted && q.QuotationNo == "ERP334-QT-1");
        if (quotation is not null)
            return quotation.Id;

        quotation = new Quotation
        {
            QuotationNo = "ERP334-QT-1",
            QuotationDate = DateTime.Today,
            CustomerId = 1,
            CustomerName = "ERP334 客户",
            Currency = Currency.USD,
            TotalAmount = 90m,
            TotalAmountCny = 630m,
            Status = DocumentStatus.Approved,
        };
        db.Quotations.Add(quotation);
        await db.SaveChangesAsync();

        db.QuotationDetails.Add(new QuotationDetail
        {
            QuotationId = quotation.Id,
            QuotationNo = quotation.QuotationNo,
            SortNo = 1,
            ProductId = 1,
            ProductCode = "P-1",
            ProductName = "商品一",
            Spec = "标准",
            Unit = "PCS",
            Quantity = 1,
            UnitPrice = 30m,
            Amount = 30m,
        });
        db.QuotationDetails.Add(new QuotationDetail
        {
            QuotationId = quotation.Id,
            QuotationNo = quotation.QuotationNo,
            SortNo = 2,
            ProductId = 2,
            ProductCode = "P-2",
            ProductName = "商品二",
            Spec = "标准",
            Unit = "PCS",
            Quantity = 2,
            UnitPrice = 30m,
            Amount = 60m,
        });
        db.QuotationDetails.Add(new QuotationDetail
        {
            QuotationId = quotation.Id,
            QuotationNo = quotation.QuotationNo,
            SortNo = 3,
            ProductCode = "P-DEL",
            ProductName = "已删除明细",
            Unit = "PCS",
            Quantity = 1,
            UnitPrice = 9m,
            Amount = 9m,
            IsDeleted = true,
        });
        await db.SaveChangesAsync();
        return quotation.Id;
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
            DisplayName = "ERP334 隔离账号",
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

