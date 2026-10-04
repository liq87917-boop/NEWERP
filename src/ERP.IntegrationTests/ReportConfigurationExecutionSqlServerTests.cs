using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
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
/// ERP-287 Stage 1：通用销售订单报表执行与数据范围 parity 的 SQL Server 集成测试。
/// 在专用 localdb 目标上自包含播种非空业务夹具（启用客户、员工、业务员数据范围账号、未删除销售订单），
/// 通过既有执行链路（ReportConfigurationExecutionService -&gt; SalesOrderReportConfigurationDatasetProvider -&gt;
/// DynamicSalesOrderReportQuery）断言非空行、当前账号数据范围、日期/客户/状态/币种筛选、稳定排序与安全分页。
/// <para>安全口径：每次写库前先做目标护栏，强制目标为专用 localdb 实例 <c>NEWERP_AutoAcceptance</c>
/// 且库名前缀 <c>NEWERP_AUTOTEST</c>；连接串只来自进程环境变量（<c>ERP_ConnectionStrings__Default</c>）
/// 或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据，也不编辑 SeedData*.cs / SchemaUpgrader.cs。</para>
/// </summary>
public sealed class ReportConfigurationExecutionSqlServerTests : IClassFixture<ReportConfigurationExecutionSqlServerFixture>
{
    private readonly ReportConfigurationExecutionSqlServerFixture _fixture;

    public ReportConfigurationExecutionSqlServerTests(ReportConfigurationExecutionSqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    // ==================== 1. 非空数据执行 ====================

    [Fact]
    public async Task 预览_非空数据_返回非删除订单与列()
    {
        await using var db = _fixture.CreateDbContext();
        var config = await CreateConfigAsync(db, _fixture.PrivilegedUserId, "erp287-非空预览", SalesOrderDefinition());
        var execution = BuildExecution(db);

        var preview = await execution.PreviewAsync(_fixture.PrivilegedUserId,
            new ReportConfigurationPreviewRequest { ConfigurationId = config.Id, PageSize = 200 });

        var expectedTotal = await CountSeededOrdersAsync(db);
        Assert.Equal(expectedTotal, preview.Total);
        Assert.Equal(expectedTotal, preview.Rows.Count);
        Assert.Equal(ReportConfigurationConstants.DatasetSalesOrder, preview.DatasetKey);

        var orderNos = preview.Rows
            .Select(r => RowString(r, "orderNo"))
            .ToHashSet(StringComparer.Ordinal);
        foreach (var expected in ReportConfigurationExecutionSqlServerFixture.SeededOrderNos)
            Assert.Contains(expected, orderNos);
        Assert.DoesNotContain(ReportConfigurationExecutionSqlServerFixture.DeletedOrderNo, orderNos);

        Assert.Contains(preview.Columns, c => c.Key == "orderNo");
        Assert.Contains(preview.Columns, c => c.Key == "currency");
        Assert.Contains(preview.Columns, c => c.Key == "totalAmount");
    }

    // ==================== 2. 数据范围 parity ====================

    [Fact]
    public async Task 数据范围_销售员仅其分配客户_特权账号看到全部()
    {
        await using var db = _fixture.CreateDbContext();
        var execution = BuildExecution(db);

        var spConfig = await CreateConfigAsync(db, _fixture.SalespersonUserId, "erp287-业务员范围", SalesOrderDefinition());
        var spPreview = await execution.PreviewAsync(_fixture.SalespersonUserId,
            new ReportConfigurationPreviewRequest { ConfigurationId = spConfig.Id, PageSize = 200 });

        var spExpected = await db.SalesOrders.AsNoTracking()
            .CountAsync(o => !o.IsDeleted
                             && o.CustomerId == _fixture.CustomerAId
                             && o.OrderNo.StartsWith(ReportConfigurationExecutionSqlServerFixture.OrderNoPrefix));
        Assert.Equal(spExpected, spPreview.Total);
        Assert.All(spPreview.Rows, r => Assert.Equal(_fixture.CustomerAId, RowLong(r, "customerId")));
        Assert.DoesNotContain(spPreview.Rows, r => RowLong(r, "customerId") == _fixture.CustomerBId);

        var privilegedConfig = await CreateConfigAsync(db, _fixture.PrivilegedUserId, "erp287-特权范围", SalesOrderDefinition());
        var privilegedPreview = await execution.PreviewAsync(_fixture.PrivilegedUserId,
            new ReportConfigurationPreviewRequest { ConfigurationId = privilegedConfig.Id, PageSize = 200 });

        var allExpected = await CountSeededOrdersAsync(db);
        Assert.Equal(allExpected, privilegedPreview.Total);
        Assert.Contains(privilegedPreview.Rows, r => RowLong(r, "customerId") == _fixture.CustomerAId);
        Assert.Contains(privilegedPreview.Rows, r => RowLong(r, "customerId") == _fixture.CustomerBId);
    }

    // ==================== 3. 筛选 parity ====================

    [Fact]
    public async Task 筛选_日期区间_返回区间内订单()
    {
        await using var db = _fixture.CreateDbContext();
        var definition = SalesOrderDefinition(filters: new List<ReportConfigurationFilter>
        {
            new() { FieldKey = "orderDate", Operator = ReportConfigurationConstants.OperatorGte, Value = new DateTime(2026, 9, 1) },
            new() { FieldKey = "orderDate", Operator = ReportConfigurationConstants.OperatorLte, Value = new DateTime(2026, 9, 30) },
        });

        var preview = await PreviewAsync(db, _fixture.PrivilegedUserId, "erp287-日期区间", definition);

        var expected = await db.SalesOrders.AsNoTracking()
            .Where(o => !o.IsDeleted
                        && o.OrderNo.StartsWith(ReportConfigurationExecutionSqlServerFixture.OrderNoPrefix)
                        && o.OrderDate >= new DateTime(2026, 9, 1)
                        && o.OrderDate <= new DateTime(2026, 9, 30))
            .Select(o => o.OrderNo)
            .ToListAsync();
        Assert.Equal(expected.Count, preview.Total);
        Assert.Equal(expected.OrderBy(n => n), preview.Rows.Select(r => RowString(r, "orderNo")).OrderBy(n => n));
    }

    [Fact]
    public async Task 筛选_客户_返回指定客户订单()
    {
        await using var db = _fixture.CreateDbContext();
        var definition = SalesOrderDefinition(filters: new List<ReportConfigurationFilter>
        {
            new() { FieldKey = "customerId", Operator = ReportConfigurationConstants.OperatorEq, Value = _fixture.CustomerAId },
        });

        var preview = await PreviewAsync(db, _fixture.PrivilegedUserId, "erp287-客户筛选", definition);

        var expected = await db.SalesOrders.AsNoTracking()
            .Where(o => !o.IsDeleted
                        && o.OrderNo.StartsWith(ReportConfigurationExecutionSqlServerFixture.OrderNoPrefix)
                        && o.CustomerId == _fixture.CustomerAId)
            .Select(o => o.OrderNo)
            .ToListAsync();
        Assert.Equal(expected.Count, preview.Total);
        Assert.All(preview.Rows, r => Assert.Equal(_fixture.CustomerAId, RowLong(r, "customerId")));
    }

    [Fact]
    public async Task 筛选_状态_返回指定状态订单()
    {
        await using var db = _fixture.CreateDbContext();
        var definition = SalesOrderDefinition(filters: new List<ReportConfigurationFilter>
        {
            new() { FieldKey = "status", Operator = ReportConfigurationConstants.OperatorEq, Value = "Approved" },
        });

        var preview = await PreviewAsync(db, _fixture.PrivilegedUserId, "erp287-状态筛选", definition);

        var expected = await db.SalesOrders.AsNoTracking()
            .Where(o => !o.IsDeleted
                        && o.OrderNo.StartsWith(ReportConfigurationExecutionSqlServerFixture.OrderNoPrefix)
                        && o.Status == DocumentStatus.Approved)
            .Select(o => o.OrderNo)
            .ToListAsync();
        Assert.Equal(expected.Count, preview.Total);
        Assert.All(preview.Rows, r => Assert.Equal("Approved", RowString(r, "status")));
    }

    [Fact]
    public async Task 筛选_币种_返回指定币种订单()
    {
        await using var db = _fixture.CreateDbContext();
        var definition = SalesOrderDefinition(filters: new List<ReportConfigurationFilter>
        {
            new() { FieldKey = "currency", Operator = ReportConfigurationConstants.OperatorEq, Value = "USD" },
        });

        var preview = await PreviewAsync(db, _fixture.PrivilegedUserId, "erp287-币种筛选", definition);

        var expected = await db.SalesOrders.AsNoTracking()
            .Where(o => !o.IsDeleted
                        && o.OrderNo.StartsWith(ReportConfigurationExecutionSqlServerFixture.OrderNoPrefix)
                        && o.Currency == Currency.USD)
            .Select(o => o.OrderNo)
            .ToListAsync();
        Assert.Equal(expected.Count, preview.Total);
        Assert.All(preview.Rows, r => Assert.Equal("USD", RowString(r, "currency")));
    }

    // ==================== 4. 稳定排序 parity ====================

    [Fact]
    public async Task 排序_按订单日期升序_身份并列决断()
    {
        await using var db = _fixture.CreateDbContext();
        var definition = SalesOrderDefinition(
            presentation: new ReportConfigurationPresentation { SortFieldKey = "orderDate", SortDirection = "asc" });

        var preview = await PreviewAsync(db, _fixture.PrivilegedUserId, "erp287-日期升序", definition);

        var expected = await db.SalesOrders.AsNoTracking()
            .Where(o => !o.IsDeleted && o.OrderNo.StartsWith(ReportConfigurationExecutionSqlServerFixture.OrderNoPrefix))
            .OrderBy(o => o.OrderDate)
            .ThenBy(o => o.Id)
            .Select(o => o.OrderNo)
            .ToListAsync();
        Assert.Equal(expected, preview.Rows.Select(r => RowString(r, "orderNo")).ToList());
    }

    [Fact]
    public async Task 排序_按订单日期降序_身份并列决断()
    {
        await using var db = _fixture.CreateDbContext();
        var definition = SalesOrderDefinition(
            presentation: new ReportConfigurationPresentation { SortFieldKey = "orderDate", SortDirection = "desc" });

        var preview = await PreviewAsync(db, _fixture.PrivilegedUserId, "erp287-日期降序", definition);

        var expected = await db.SalesOrders.AsNoTracking()
            .Where(o => !o.IsDeleted && o.OrderNo.StartsWith(ReportConfigurationExecutionSqlServerFixture.OrderNoPrefix))
            .OrderByDescending(o => o.OrderDate)
            .ThenBy(o => o.Id)
            .Select(o => o.OrderNo)
            .ToListAsync();
        Assert.Equal(expected, preview.Rows.Select(r => RowString(r, "orderNo")).ToList());
    }

    // ==================== 5. 安全分页 parity ====================

    [Fact]
    public async Task 分页_安全分页_覆盖全部命中且不重叠()
    {
        await using var db = _fixture.CreateDbContext();
        var config = await CreateConfigAsync(db, _fixture.PrivilegedUserId, "erp287-分页",
            SalesOrderDefinition(presentation: new ReportConfigurationPresentation { Page = 1, PageSize = 2 }));
        var execution = BuildExecution(db);

        var page1 = await execution.PreviewAsync(_fixture.PrivilegedUserId,
            new ReportConfigurationPreviewRequest { ConfigurationId = config.Id, Page = 1, PageSize = 2 });
        var page2 = await execution.PreviewAsync(_fixture.PrivilegedUserId,
            new ReportConfigurationPreviewRequest { ConfigurationId = config.Id, Page = 2, PageSize = 2 });
        var page3 = await execution.PreviewAsync(_fixture.PrivilegedUserId,
            new ReportConfigurationPreviewRequest { ConfigurationId = config.Id, Page = 3, PageSize = 2 });

        var expected = await db.SalesOrders.AsNoTracking()
            .Where(o => !o.IsDeleted && o.OrderNo.StartsWith(ReportConfigurationExecutionSqlServerFixture.OrderNoPrefix))
            .OrderBy(o => o.Id)
            .Select(o => o.OrderNo)
            .ToListAsync();

        Assert.Equal(expected.Count, page1.Total);
        Assert.Equal(2, page1.Rows.Count);
        Assert.Equal(2, page2.Rows.Count);
        Assert.Single(page3.Rows);

        var actual = page1.Rows.Concat(page2.Rows).Concat(page3.Rows)
            .Select(r => RowString(r, "orderNo"))
            .ToList();
        Assert.Equal(expected, actual);
        Assert.Equal(expected.Count, actual.Distinct(StringComparer.Ordinal).Count());
    }

    // ==================== 6. 菜单授权 fail closed ====================

    [Fact]
    public async Task 预览_菜单授权撤销_数据集重新校验fail_closed()
    {
        await using var db = _fixture.CreateDbContext();
        var ownerId = await SeedAuthorizedOwnerAsync(db, "erp287-revoke");
        var config = await CreateConfigAsync(db, ownerId, "erp287-撤销授权", SalesOrderDefinition());

        var roleIds = await db.SysUserRoles.AsNoTracking()
            .Where(ur => ur.UserId == ownerId && !ur.IsDeleted)
            .Select(ur => ur.RoleId)
            .ToListAsync();
        var menuId = await db.SysMenus.AsNoTracking()
            .Where(m => m.MenuCode == DynamicSalesOrderReportRules.RequiredMenuCode && !m.IsDeleted)
            .Select(m => m.Id)
            .FirstAsync();
        var roleMenus = await db.SysRoleMenus
            .Where(rm => roleIds.Contains(rm.RoleId) && rm.MenuId == menuId && !rm.IsDeleted)
            .ToListAsync();
        foreach (var rm in roleMenus)
            rm.IsDeleted = true;
        await db.SaveChangesAsync();

        var execution = BuildExecution(db);
        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            execution.PreviewAsync(ownerId, new ReportConfigurationPreviewRequest { ConfigurationId = config.Id }));

        Assert.Equal(ErrorCodes.Forbidden, ex.Code);
    }

    // ==================== 7. 业务夹具幂等 ====================

    [Fact]
    public async Task 业务夹具_幂等重种_不产生重复数据()
    {
        await _fixture.SeedBusinessFixtureAsync();

        await using var db = _fixture.CreateDbContext();
        Assert.Equal(2, await db.BaseEmployees.AsNoTracking()
            .CountAsync(e => !e.IsDeleted && e.EmployeeCode.StartsWith("ERP287-SP")));
        Assert.Equal(2, await db.BaseCustomers.AsNoTracking()
            .CountAsync(c => !c.IsDeleted && c.CustomerCode.StartsWith("ERP287-CUST")));
        Assert.Equal(ReportConfigurationExecutionSqlServerFixture.SeededOrderNos.Length, await CountSeededOrdersAsync(db));
        Assert.Equal(1, await db.SysUsers.AsNoTracking()
            .CountAsync(u => !u.IsDeleted && u.UserName == ReportConfigurationExecutionSqlServerFixture.SalespersonUserName));
        Assert.Equal(1, await db.SysUsers.AsNoTracking()
            .CountAsync(u => !u.IsDeleted && u.UserName == ReportConfigurationExecutionSqlServerFixture.PrivilegedUserName));
    }

    // ==================== 脚手架 ====================

    private static IReadOnlyList<IReportConfigurationDatasetProvider> BuildProviders(ErpDbContext db)
        => new IReportConfigurationDatasetProvider[]
        {
            new SalesOrderReportConfigurationDatasetProvider(new DynamicSalesOrderReportQuery(db)),
            new ReceivableReportConfigurationDatasetProvider(new DynamicReceivableReportQuery(db)),
        };

    private static IReportConfigurationService BuildService(ErpDbContext db)
        => new ReportConfigurationService(db, new ReportConfigurationCatalog(BuildProviders(db)));

    private static IReportConfigurationExecutionService BuildExecution(ErpDbContext db)
        => new ReportConfigurationExecutionService(db, BuildProviders(db));

    private static ReportConfigurationSaveDto SaveDto(string name, ReportConfigurationDefinition definition)
        => new() { Name = name, Definition = definition };

    private static ReportConfigurationDefinition SalesOrderDefinition(
        string[]? fields = null,
        string groupBy = ReportConfigurationConstants.GroupNone,
        List<ReportConfigurationFilter>? filters = null,
        ReportConfigurationPresentation? presentation = null)
        => new()
        {
            SchemaVersion = ReportConfigurationRules.CurrentSchemaVersion,
            DatasetKey = ReportConfigurationConstants.DatasetSalesOrder,
            Fields = fields is { Length: > 0 }
                ? fields.ToList()
                : new List<string> { "orderNo", "orderDate", "customerId", "currency", "totalAmount", "status" },
            Filters = filters ?? new List<ReportConfigurationFilter>(),
            Grouping = new List<string> { groupBy },
            Aggregates = new List<ReportConfigurationAggregate>(),
            Capabilities = new List<string> { ReportConfigurationConstants.CapabilityPreview },
            Presentation = presentation,
        };

    private static async Task<ReportConfigurationDto> CreateConfigAsync(
        ErpDbContext db, long ownerId, string name, ReportConfigurationDefinition definition)
    {
        var service = BuildService(db);
        return await service.CreateAsync(ownerId, SaveDto(name, definition));
    }

    private static async Task<ReportConfigurationPreviewDto> PreviewAsync(
        ErpDbContext db, long ownerId, string name, ReportConfigurationDefinition definition)
    {
        var config = await CreateConfigAsync(db, ownerId, name, definition);
        var execution = BuildExecution(db);
        return await execution.PreviewAsync(ownerId,
            new ReportConfigurationPreviewRequest { ConfigurationId = config.Id, PageSize = 200 });
    }

    private static async Task<long> SeedAuthorizedOwnerAsync(ErpDbContext db, string tag)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var user = new SysUser
        {
            UserName = $"{tag}-{suffix}",
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = $"{tag}-{suffix}",
            Status = UserStatus.Enabled,
        };
        db.SysUsers.Add(user);
        await db.SaveChangesAsync();

        var role = new SysRole
        {
            RoleName = $"{tag}-role-{suffix}",
            RoleCode = $"{tag}-role-{suffix}",
            IsSystem = false,
        };
        db.SysRoles.Add(role);
        await db.SaveChangesAsync();

        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        await db.SaveChangesAsync();

        var menu = await db.SysMenus.FirstOrDefaultAsync(m => m.MenuCode == DynamicSalesOrderReportRules.RequiredMenuCode && !m.IsDeleted);
        if (menu is null)
        {
            menu = new SysMenu { MenuName = DynamicSalesOrderReportRules.RequiredMenuText, MenuCode = DynamicSalesOrderReportRules.RequiredMenuCode, MenuType = MenuType.Menu };
            db.SysMenus.Add(menu);
            await db.SaveChangesAsync();
        }

        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
        await db.SaveChangesAsync();

        return user.Id;
    }

    private static async Task<int> CountSeededOrdersAsync(ErpDbContext db)
        => await db.SalesOrders.AsNoTracking()
            .CountAsync(o => !o.IsDeleted && o.OrderNo.StartsWith(ReportConfigurationExecutionSqlServerFixture.OrderNoPrefix));

    private static string RowString(Dictionary<string, object?> row, string key)
        => Convert.ToString(row[key]) ?? string.Empty;

    private static long RowLong(Dictionary<string, object?> row, string key)
        => Convert.ToInt64(row[key]!);
}

/// <summary>
/// 专用 localdb 目标 Fixture：一次启动序列（自动建表 + 结构升级 + 种子 + 再升级）+ 幂等非空业务夹具。
/// 全部测试复用同一目标库；写库前每次断言目标身份（实例含 <c>NEWERP_AutoAcceptance</c>、库名前缀 <c>NEWERP_AUTOTEST</c>）。
/// </summary>
public sealed class ReportConfigurationExecutionSqlServerFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";
    public const string DefaultDatabaseName = "NEWERP_AUTOTEST_ERP287";

    public const string OrderNoPrefix = "ERP287-SO-";
    public const string DeletedOrderNo = "ERP287-SO-DELETED";
    public const string SalespersonEmployeeCode = "ERP287-SP1";
    public const string OtherEmployeeCode = "ERP287-SP2";
    public const string CustomerACode = "ERP287-CUST-A";
    public const string CustomerBCode = "ERP287-CUST-B";
    public const string SalespersonUserName = "ERP287-SP1";
    public const string PrivilegedUserName = "ERP287-ADMIN";
    public const string SalespersonRoleCode = "ERP287-SALES";
    public const string PrivilegedRoleCode = "ERP287-SYS";

    public static readonly string[] SeededOrderNos =
    {
        "ERP287-SO-A1",
        "ERP287-SO-A2",
        "ERP287-SO-A3",
        "ERP287-SO-B1",
        "ERP287-SO-B2",
    };

    public string ConnectionString { get; private set; } = null!;

    public long SalespersonEmployeeId { get; private set; }
    public long OtherEmployeeId { get; private set; }
    public long CustomerAId { get; private set; }
    public long CustomerBId { get; private set; }
    public long SalespersonUserId { get; private set; }
    public long PrivilegedUserId { get; private set; }

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = BuildDefaultConnectionString();

        AssertDedicatedTarget(connectionString);
        ConnectionString = connectionString;

        Console.WriteLine($"[ERP-287] 目标库护栏放行（实例含 {InstanceMarker}，库名前缀 {DatabasePrefix}）。");

        await BootstrapAsync();
        await SeedBusinessFixtureAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public ErpDbContext CreateDbContext() => new(BuildOptions());

    /// <summary>幂等播种非空业务夹具；重复调用不产生重复数据。写库前再次断言目标身份。</summary>
    public async Task SeedBusinessFixtureAsync()
    {
        AssertDedicatedTarget(ConnectionString);

        await using var db = CreateDbContext();

        var salesperson = await db.BaseEmployees.FirstOrDefaultAsync(e => e.EmployeeCode == SalespersonEmployeeCode && !e.IsDeleted);
        if (salesperson is null)
        {
            salesperson = new BaseEmployee { EmployeeCode = SalespersonEmployeeCode, EmployeeName = "ERP287 业务员甲", IsSalesman = true, Status = 1 };
            db.BaseEmployees.Add(salesperson);
            await db.SaveChangesAsync();
        }

        var other = await db.BaseEmployees.FirstOrDefaultAsync(e => e.EmployeeCode == OtherEmployeeCode && !e.IsDeleted);
        if (other is null)
        {
            other = new BaseEmployee { EmployeeCode = OtherEmployeeCode, EmployeeName = "ERP287 业务员乙", IsSalesman = true, Status = 1 };
            db.BaseEmployees.Add(other);
            await db.SaveChangesAsync();
        }

        var customerA = await db.BaseCustomers.FirstOrDefaultAsync(c => c.CustomerCode == CustomerACode && !c.IsDeleted);
        if (customerA is null)
        {
            customerA = new BaseCustomer { CustomerCode = CustomerACode, CustomerName = "ERP287 客户甲", Status = 1, CreditStatus = "正常", EmpId = salesperson.Id };
            db.BaseCustomers.Add(customerA);
            await db.SaveChangesAsync();
        }

        var customerB = await db.BaseCustomers.FirstOrDefaultAsync(c => c.CustomerCode == CustomerBCode && !c.IsDeleted);
        if (customerB is null)
        {
            customerB = new BaseCustomer { CustomerCode = CustomerBCode, CustomerName = "ERP287 客户乙", Status = 1, CreditStatus = "正常", EmpId = other.Id };
            db.BaseCustomers.Add(customerB);
            await db.SaveChangesAsync();
        }

        var menu = await db.SysMenus.FirstOrDefaultAsync(m => m.MenuCode == DynamicSalesOrderReportRules.RequiredMenuCode && !m.IsDeleted);
        if (menu is null)
        {
            menu = new SysMenu { MenuName = DynamicSalesOrderReportRules.RequiredMenuText, MenuCode = DynamicSalesOrderReportRules.RequiredMenuCode, MenuType = MenuType.Menu };
            db.SysMenus.Add(menu);
            await db.SaveChangesAsync();
        }

        var salespersonUser = await SeedUserAsync(db, SalespersonUserName, "ERP287 业务员账号");
        var salespersonRole = await SeedRoleAsync(db, SalespersonRoleCode, isSystem: false);
        await SeedUserRoleAsync(db, salespersonUser.Id, salespersonRole.Id);
        await SeedRoleMenuAsync(db, salespersonRole.Id, menu.Id);

        var privilegedUser = await SeedUserAsync(db, PrivilegedUserName, "ERP287 特权账号");
        var privilegedRole = await SeedRoleAsync(db, PrivilegedRoleCode, isSystem: true);
        await SeedUserRoleAsync(db, privilegedUser.Id, privilegedRole.Id);
        await SeedRoleMenuAsync(db, privilegedRole.Id, menu.Id);

        await SeedOrderAsync(db, SeededOrderNos[0], customerA.Id, new DateTime(2026, 9, 1), Currency.USD, 100m, DocumentStatus.Pending);
        await SeedOrderAsync(db, SeededOrderNos[1], customerA.Id, new DateTime(2026, 9, 15), Currency.CNY, 200m, DocumentStatus.Approved);
        await SeedOrderAsync(db, SeededOrderNos[2], customerA.Id, new DateTime(2026, 10, 1), Currency.USD, 300m, DocumentStatus.Completed);
        await SeedOrderAsync(db, SeededOrderNos[3], customerB.Id, new DateTime(2026, 9, 1), Currency.USD, 400m, DocumentStatus.Pending);
        await SeedOrderAsync(db, SeededOrderNos[4], customerB.Id, new DateTime(2026, 9, 20), Currency.EUR, 500m, DocumentStatus.Approved);
        await SeedOrderAsync(db, DeletedOrderNo, customerA.Id, new DateTime(2026, 9, 25), Currency.USD, 999m, DocumentStatus.Pending, deleted: true);

        SalespersonEmployeeId = salesperson.Id;
        OtherEmployeeId = other.Id;
        CustomerAId = customerA.Id;
        CustomerBId = customerB.Id;
        SalespersonUserId = salespersonUser.Id;
        PrivilegedUserId = privilegedUser.Id;
    }

    private static async Task<SysUser> SeedUserAsync(ErpDbContext db, string userName, string displayName)
    {
        var user = await db.SysUsers.FirstOrDefaultAsync(u => u.UserName == userName && !u.IsDeleted);
        if (user is not null)
            return user;

        user = new SysUser
        {
            UserName = userName,
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = displayName,
            Status = UserStatus.Enabled,
        };
        db.SysUsers.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    private static async Task<SysRole> SeedRoleAsync(ErpDbContext db, string roleCode, bool isSystem)
    {
        var role = await db.SysRoles.FirstOrDefaultAsync(r => r.RoleCode == roleCode && !r.IsDeleted);
        if (role is not null)
            return role;

        role = new SysRole { RoleName = roleCode, RoleCode = roleCode, IsSystem = isSystem };
        db.SysRoles.Add(role);
        await db.SaveChangesAsync();
        return role;
    }

    private static async Task SeedUserRoleAsync(ErpDbContext db, long userId, long roleId)
    {
        if (await db.SysUserRoles.AnyAsync(ur => ur.UserId == userId && ur.RoleId == roleId && !ur.IsDeleted))
            return;
        db.SysUserRoles.Add(new SysUserRole { UserId = userId, RoleId = roleId });
        await db.SaveChangesAsync();
    }

    private static async Task SeedRoleMenuAsync(ErpDbContext db, long roleId, long menuId)
    {
        if (await db.SysRoleMenus.AnyAsync(rm => rm.RoleId == roleId && rm.MenuId == menuId && !rm.IsDeleted))
            return;
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = roleId, MenuId = menuId });
        await db.SaveChangesAsync();
    }

    private static async Task SeedOrderAsync(
        ErpDbContext db, string orderNo, long customerId, DateTime orderDate,
        Currency currency, decimal totalAmount, DocumentStatus status, bool deleted = false)
    {
        if (await db.SalesOrders.AnyAsync(o => o.OrderNo == orderNo))
            return;
        db.SalesOrders.Add(new SalesOrder
        {
            OrderNo = orderNo,
            CustomerId = customerId,
            OrderDate = orderDate,
            Currency = currency,
            TotalAmount = totalAmount,
            Status = status,
            IsDeleted = deleted,
        });
        await db.SaveChangesAsync();
    }

    private async Task BootstrapAsync()
    {
        AssertDedicatedTarget(ConnectionString);

        var options = BuildOptions();
        await using var db = new ErpDbContext(options);

        // 与 ERP.Api Program.cs 完全一致的启动序列（全新库自动建表 + 幂等结构升级 + 种子数据 + 再次结构升级）
        await db.Database.EnsureCreatedAsync();
        await SchemaUpgrader.EnsureUpgradedAsync(db);
        await SeedData.InitializeAsync(db);
        await SchemaUpgrader.EnsureUpgradedAsync(db);

        Console.WriteLine("[ERP-287] 启动序列完成：EnsureCreated + EnsureUpgraded + SeedData + EnsureUpgraded（重复执行幂等）。");
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
        var server = builder.DataSource ?? string.Empty;
        var database = builder.InitialCatalog ?? string.Empty;

        Assert.Equal($"(localdb)\\{InstanceMarker}", server, ignoreCase: true);
        Assert.True(builder.IntegratedSecurity);
        Assert.StartsWith(DatabasePrefix, database, StringComparison.OrdinalIgnoreCase);
    }
}







