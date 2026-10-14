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
using System.Data;
using System.Data.Common;
using System.Security.Claims;
using Xunit;

namespace ERP.IntegrationTests;

/// <summary>
/// ERP-528 供应商比价数量 / 单价 / 服务端重算总额「持久化精度」的真实 SQL Server 集成测试
/// （专用 localdb <c>(localdb)\NEWERP_AutoAcceptance</c> + 全新 GUID 库）：
/// <list type="number">
/// <item>实际列精度与规则常量同源：EF 模型与真实 <c>INFORMATION_SCHEMA</c> 双向确认
/// <c>PurchaseQuotes.Quantity</c> / <c>QuotePrice</c> / <c>TotalAmount</c> 均为 <c>decimal(18,4)</c>；</item>
/// <item>合法的 4 位小数报价（含服务端重算总额）经真实控制器创建后**原样**往返
/// （以 <c>CONVERT(varchar, ...)</c> 断言 4 位小数逐位保留，绝不收敛为 2 位）；</item>
/// <item>第 5 位小数 / 量级越界 / 重算溢出一律在写入之前受控拒绝，零比价行、零审批决定、零采购单；</item>
/// <item>被拒的修改不改变权威行的任何商业字段与审计时间戳。</item>
/// </list>
/// <para>安全口径：目标必须为专用 localdb 实例 <c>NEWERP_AutoAcceptance</c> 且库名前缀 <c>NEWERP_AUTOTEST</c>；
/// 每次运行创建全新 GUID 库，发现同名库已存在立即拒绝，绝不 drop / reset / 复用；连接串只来自进程环境变量或
/// 专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据。</para>
/// </summary>
public sealed class PurchaseQuotePersistedNumericBoundsSqlServerTests
    : IClassFixture<PurchaseQuotePersistedNumericBoundsSqlServerFixture>
{
    private readonly PurchaseQuotePersistedNumericBoundsSqlServerFixture _fixture;

    public PurchaseQuotePersistedNumericBoundsSqlServerTests(
        PurchaseQuotePersistedNumericBoundsSqlServerFixture fixture)
        => _fixture = fixture;

    private void Guard()
        => PurchaseQuotePersistedNumericBoundsSqlServerFixture.AssertDedicatedTarget(_fixture.ConnectionString);

    /// <summary><c>DECIMAL(18,4)</c> 的最大可表示绝对值（与规则常量同源）。</summary>
    private static readonly decimal Max4 = PurchaseQuoteMutationRules.MaxDecimal18Scale4;

    private static string Tag() => Guid.NewGuid().ToString("N")[..8];

    // ==================== 1. 实际列精度（EF 模型 + 真实 INFORMATION_SCHEMA） ====================

    [Fact]
    public async Task Actual_column_precision_is_decimal_18_4()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();

        Assert.Equal("decimal(18,4)", ColumnType(db, typeof(PurchaseQuote), nameof(PurchaseQuote.Quantity)));
        Assert.Equal("decimal(18,4)", ColumnType(db, typeof(PurchaseQuote), nameof(PurchaseQuote.QuotePrice)));
        Assert.Equal("decimal(18,4)", ColumnType(db, typeof(PurchaseQuote), nameof(PurchaseQuote.TotalAmount)));

        Assert.Equal("decimal(18,4)", await SqlColumnTypeAsync(db, "PurchaseQuotes", "Quantity"));
        Assert.Equal("decimal(18,4)", await SqlColumnTypeAsync(db, "PurchaseQuotes", "QuotePrice"));
        Assert.Equal("decimal(18,4)", await SqlColumnTypeAsync(db, "PurchaseQuotes", "TotalAmount"));

        Assert.Equal(18, PurchaseQuoteMutationRules.DecimalPrecision);
        Assert.Equal(4, PurchaseQuoteMutationRules.DecimalScale);
        Assert.Equal(99_999_999_999_999.9999m, Max4);
    }

    // ==================== 2. 合法 4 位小数原样往返（真实列） ====================

    [Fact]
    public async Task Four_decimal_values_round_trip_verbatim_through_real_columns()
    {
        Guard();
        var quoteNo = $"PQ-4DP-{Tag()}";

        await using (var db = _fixture.CreateDbContext())
        {
            var controller = _fixture.NewQuoteController(db);
            var created = AssertOk<PurchaseQuote>(await controller.Create(Draft(quoteNo, 4.0000m, 1.2345m, total: 999m)));
            Assert.Equal(4.0000m, created.Quantity);
            Assert.Equal(1.2345m, created.QuotePrice);
            Assert.Equal(4.9380m, created.TotalAmount);          // 服务端重算覆盖客户端伪造总额
        }

        await using (var db = _fixture.CreateDbContext())
        {
            var stored = await db.PurchaseQuotes.AsNoTracking().SingleAsync(q => q.QuoteNo == quoteNo);
            Assert.Equal(4.0000m, stored.Quantity);
            Assert.Equal(1.2345m, stored.QuotePrice);
            Assert.Equal(4.9380m, stored.TotalAmount);

            // 真实列以 4 位小数原样保存（绝不收敛为 decimal(18,2) 的两位）。
            var (quantity, price, total) = await SqlTextAsync(db, quoteNo);
            Assert.Equal("4.0000", quantity);
            Assert.Equal("1.2345", price);
            Assert.Equal("4.9380", total);
        }
    }

    // ==================== 3. 越界 / 溢出一律在写入之前受控拒绝（零行） ====================

    [Fact]
    public async Task Over_scale_and_overflow_are_rejected_with_zero_rows()
    {
        Guard();
        var beforeQuotes = await CountAsync(_fixture, "PurchaseQuotes");
        var beforeDecisions = await CountAsync(_fixture, "PurchaseQuoteDecisions");
        var beforeOrders = await CountAsync(_fixture, "PurchaseOrders");

        await using var db = _fixture.CreateDbContext();
        var controller = _fixture.NewQuoteController(db);

        var fifthDecimal = await Assert.ThrowsAsync<BusinessException>(() =>
            controller.Create(Draft($"PQ-5DP-{Tag()}", 1.00005m, 1.2345m, total: 1m)));
        Assert.Equal(ErrorCodes.InvalidParameter, fifthDecimal.Code);
        Assert.Equal(PurchaseQuoteMutationRules.QuantityText, fifthDecimal.Message);

        var magnitude = await Assert.ThrowsAsync<BusinessException>(() =>
            controller.Create(Draft($"PQ-MAX-{Tag()}", 100_000_000_000_000m, 1.0000m, total: 1m)));
        Assert.Equal(ErrorCodes.InvalidParameter, magnitude.Code);
        Assert.Equal(PurchaseQuoteMutationRules.QuantityText, magnitude.Message);

        var recompute = await Assert.ThrowsAsync<BusinessException>(() =>
            controller.Create(Draft($"PQ-RECOMP-{Tag()}", Max4, Max4, total: 1m)));
        Assert.Equal(ErrorCodes.InvalidParameter, recompute.Code);
        Assert.Equal(PurchaseQuoteMutationRules.TotalText, recompute.Message);

        Assert.Equal(beforeQuotes, await CountAsync(_fixture, "PurchaseQuotes"));
        Assert.Equal(beforeDecisions, await CountAsync(_fixture, "PurchaseQuoteDecisions"));
        Assert.Equal(beforeOrders, await CountAsync(_fixture, "PurchaseOrders"));
    }

    // ==================== 4. 被拒修改不改变权威行（含审计时间戳） ====================

    [Fact]
    public async Task Rejected_update_leaves_authoritative_row_unchanged()
    {
        Guard();
        var quoteNo = $"PQ-UPD-{Tag()}";
        long quoteId;
        await using (var seed = _fixture.CreateDbContext())
        {
            var controller = _fixture.NewQuoteController(seed);
            quoteId = AssertOk<PurchaseQuote>(await controller.Create(Draft(quoteNo, 10.0000m, 2.0000m, total: 0m))).Id;
        }

        await using var db = _fixture.CreateDbContext();
        var controller2 = _fixture.NewQuoteController(db);
        var before = await db.PurchaseQuotes.AsNoTracking().SingleAsync(q => q.Id == quoteId);

        var proposed = await db.PurchaseQuotes.AsNoTracking().SingleAsync(q => q.Id == quoteId);
        proposed.Quantity = 1.00005m;                            // 第 5 位小数
        var rejected = await Assert.ThrowsAsync<BusinessException>(() => controller2.Update(quoteId, proposed));
        Assert.Equal(PurchaseQuoteMutationRules.QuantityText, rejected.Message);

        var overflow = await db.PurchaseQuotes.AsNoTracking().SingleAsync(q => q.Id == quoteId);
        overflow.Quantity = Max4;
        overflow.QuotePrice = Max4;
        var overflowed = await Assert.ThrowsAsync<BusinessException>(() => controller2.Update(quoteId, overflow));
        Assert.Equal(PurchaseQuoteMutationRules.TotalText, overflowed.Message);

        var after = await db.PurchaseQuotes.AsNoTracking().SingleAsync(q => q.Id == quoteId);
        Assert.Equal(before.Quantity, after.Quantity);
        Assert.Equal(before.QuotePrice, after.QuotePrice);
        Assert.Equal(before.TotalAmount, after.TotalAmount);
        Assert.Equal(before.Status, after.Status);
        Assert.Equal(before.IsSelected, after.IsSelected);
        Assert.Equal(before.UpdatedAt, after.UpdatedAt);
        Assert.False(after.IsDeleted);
    }

    // ==================== 工厂与断言辅助 ====================

    private static PurchaseQuote Draft(string quoteNo, decimal quantity, decimal quotePrice, decimal total) => new()
    {
        QuoteNo = quoteNo,
        QuoteDate = new DateTime(2026, 10, 1),
        ProductName = "ERP528 SQL 商品",
        Spec = "大号",
        Unit = "PCS",
        Quantity = quantity,
        SupplierName = "ERP528 SQL 档口",
        SupplierType = "档口",
        QuotePrice = quotePrice,
        TotalAmount = total,
        Currency = "USD",
        DeliveryDays = 10,
        MinOrderQty = 1,
        PaymentTerms = "现结",
        IsSelected = false,
        Status = PurchaseQuoteMutationRules.PendingStatus,
        Remark = "ERP528_SQL_TEST"
    };

    private static T AssertOk<T>(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<ApiResponse<T>>(ok.Value);
        Assert.Equal(ErrorCodes.Success, resp.Code);
        Assert.NotNull(resp.Data);
        return resp.Data!;
    }

    private static string ColumnType(ErpDbContext db, Type entityType, string propertyName)
        => db.Model.FindEntityType(entityType)!.FindProperty(propertyName)!.GetColumnType()!;

    private static async Task<string> SqlColumnTypeAsync(ErpDbContext db, string table, string column)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open) await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT DISTINCT DATA_TYPE + '(' + CAST(NUMERIC_PRECISION AS varchar(10)) + ',' + " +
            "CAST(NUMERIC_SCALE AS varchar(10)) + ')' FROM INFORMATION_SCHEMA.COLUMNS " +
            "WHERE TABLE_NAME = @table AND COLUMN_NAME = @column AND DATA_TYPE = 'decimal'";
        AddParameter(command, "@table", table);
        AddParameter(command, "@column", column);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<(string Quantity, string Price, string Total)> SqlTextAsync(ErpDbContext db,
        string quoteNo)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open) await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT CONVERT(varchar(50), Quantity), CONVERT(varchar(50), QuotePrice), " +
            "CONVERT(varchar(50), TotalAmount) FROM db_owner.PurchaseQuotes WHERE QuoteNo = @no";
        AddParameter(command, "@no", quoteNo);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetString(0), reader.GetString(1), reader.GetString(2));
    }

    private static async Task<long> CountAsync(PurchaseQuotePersistedNumericBoundsSqlServerFixture fixture,
        string table)
    {
        await using var db = fixture.CreateDbContext();
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open) await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM db_owner.{table}";
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}

// ==================== 专用 localdb 夹具 + 目标护栏 ====================

/// <summary>
/// 专用 localdb 目标 Fixture（ERP-528）：每次运行创建一个全新 GUID 后缀库并初始化为完整 NEWERP 结构 +
/// 既有菜单授权的真实启用身份，供比价持久化精度集成测试复用。
/// <para>每次运行只创建一个全新 GUID 后缀库；发现同名库已存在立即拒绝；绝不 drop / reset / 复用任何数据库；
/// 连接串只来自进程环境变量 <c>ERP_ConnectionStrings__Default</c> 或专用 localdb 默认值，
/// 绝不读取 appsettings / .env / 生产凭据。</para>
/// </summary>
public sealed class PurchaseQuotePersistedNumericBoundsSqlServerFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";
    public const string DefaultDatabaseName = "NEWERP_AUTOTEST_PQNUMERIC_20261014";

    private long _authorizedUserId;

    public string ConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = BuildDefaultConnectionString();

        AssertDedicatedTarget(connectionString);
        ConnectionString = connectionString;

        Console.WriteLine($"[ERP-528] 目标库护栏放行（实例含 {InstanceMarker}，库名前缀 {DatabasePrefix}）。");

        await CreateFreshDatabaseAsync();
        await SeedAuthorizedUserAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public ErpDbContext CreateDbContext() => new(BuildOptions());

    /// <summary>绑定已播种的真实启用身份（既有菜单授权）的比价控制器；校验与写入都走真实授权口径。</summary>
    public PurchaseQuoteController NewQuoteController(ErpDbContext db)
    {
        var controller = new PurchaseQuoteController(new GenericService<PurchaseQuote>(db), db,
            new DocumentNumberService(db));
        var http = new DefaultHttpContext();
        http.User = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.NameIdentifier, _authorizedUserId.ToString()) }, "Test"));
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        return controller;
    }

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

    private async Task CreateFreshDatabaseAsync()
    {
        var database = new SqlConnectionStringBuilder(ConnectionString).InitialCatalog;

        // 任何建库 / 库访问之前再次护栏：绝不使用生产或非专用目标。
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

        Console.WriteLine("[ERP-528] 集成场景就绪：完整 NEWERP 结构 + 种子数据。");
    }

    /// <summary>播种真实启用身份 + 既有「供应商比价」/「采购订单」菜单授权（不新增权限模型）。</summary>
    private async Task SeedAuthorizedUserAsync()
    {
        await using var db = CreateDbContext();
        var code = $"erp528-a-{Guid.NewGuid():N}";
        var role = new SysRole { RoleName = code, RoleCode = code, IsSystem = true };
        db.SysRoles.Add(role);
        await db.SaveChangesAsync();

        var user = new SysUser
        {
            UserName = code, PasswordHash = "hash", PasswordSalt = "salt",
            DisplayName = "ERP528 特权测试账号", Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        await db.SaveChangesAsync();

        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        await db.SaveChangesAsync();

        foreach (var menuCode in new[]
                 {
                     PurchaseQuoteAuthorizationRules.RequiredMenuCode,
                     PurchaseQuoteAuthorizationRules.DestinationMenuCode
                 })
        {
            var menu = db.SysMenus.FirstOrDefault(m => m.MenuCode == menuCode && !m.IsDeleted);
            if (menu is null)
            {
                menu = new SysMenu { MenuName = menuCode, MenuCode = menuCode, MenuType = MenuType.Menu };
                db.SysMenus.Add(menu);
                await db.SaveChangesAsync();
            }

            db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
            await db.SaveChangesAsync();
        }

        _authorizedUserId = user.Id;
    }
}

/// <summary>目标库护栏单元级校验：非专用目标必须在访问数据库之前被拒绝。</summary>
public sealed class PurchaseQuotePersistedNumericBoundsTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;User Id=sa")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(() =>
            PurchaseQuotePersistedNumericBoundsSqlServerFixture.AssertDedicatedTarget(connection));
}
