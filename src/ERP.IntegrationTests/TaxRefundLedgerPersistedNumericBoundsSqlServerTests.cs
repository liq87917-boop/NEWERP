using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.Interfaces;
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
/// ERP-529 出口退税台账金额 / 退税率 / 汇率「实际存储精度」的真实 SQL Server 集成测试
/// （专用 localdb <c>(localdb)\NEWERP_AutoAcceptance</c> + 全新 GUID 库）：
/// <list type="number">
/// <item>实际列精度与规则常量同源：EF 模型与真实 <c>INFORMATION_SCHEMA</c> 双向确认
/// <c>BaseTaxRefunds.ExportAmount</c> / <c>RefundRate</c> / <c>RefundableAmount</c> / <c>RefundedAmount</c>
/// 为 <c>decimal(18,4)</c>、<c>ExchangeRate</c> 为 <c>decimal(18,6)</c>；</item>
/// <item>合法的 4 位小数金额与 6 位小数汇率（含恰好上限）经真实受权控制器创建后**原样**往返
/// （以 <c>CONVERT(varchar, ...)</c> 逐位断言，绝不收敛为 2 位）；</item>
/// <item>第 5 位小数金额 / 第 7 位小数汇率 / 零与负汇率 / 量级溢出在写入之前受控拒绝，零台账行；</item>
/// <item>「半进位取整后恰好落到列上限」的过小数位值仍被拒绝（绝不采信取整结果），而恰好上限本身放行；</item>
/// <item>被拒的修改不改变权威行的任何金额字段与审计时间戳；</item>
/// <item>绕过应用层护栏的原始 EF 写入会被真实列静默取整，超容量值被 SQL Server 以算术溢出（8115）拒绝 ——
/// 这正是护栏必须提前受控拒绝的原因。</item>
/// </list>
/// <para>安全口径：目标必须为专用 localdb 实例 <c>NEWERP_AutoAcceptance</c>、库名前缀 <c>NEWERP_AUTOTEST</c> 且
/// <c>Integrated Security</c>；每次运行只创建一个全新 GUID 后缀库，发现同名库已存在立即拒绝，绝不 drop / reset /
/// 复用任何数据库；连接串只来自进程环境变量或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据。</para>
/// </summary>
public sealed class TaxRefundLedgerPersistedNumericBoundsSqlServerTests
    : IClassFixture<TaxRefundLedgerPersistedNumericBoundsSqlServerFixture>
{
    /// <summary><c>DECIMAL(18,4)</c> 的最大可表示值（与规则常量同源）。</summary>
    private static readonly decimal Max4 = TaxRefundLedgerRules.MaxAmount;

    /// <summary><c>DECIMAL(18,6)</c> 的最大可表示值（与规则常量同源）。</summary>
    private static readonly decimal Max6 = TaxRefundLedgerRules.MaxExchangeRate;

    private readonly TaxRefundLedgerPersistedNumericBoundsSqlServerFixture _fixture;

    public TaxRefundLedgerPersistedNumericBoundsSqlServerTests(
        TaxRefundLedgerPersistedNumericBoundsSqlServerFixture fixture)
        => _fixture = fixture;

    /// <summary>访问数据库前复核专用目标护栏（实例名 / 库名前缀 / 集成安全）。</summary>
    private void Guard()
        => TaxRefundLedgerPersistedNumericBoundsSqlServerFixture.AssertDedicatedTarget(_fixture.ConnectionString);

    private static string Tag() => Guid.NewGuid().ToString("N")[..8];

    /// <summary>台账工厂（既有列口径：金额 / 退税率 4 位小数、汇率 6 位小数）。</summary>
    private static BaseTaxRefund Ledger(string refundNo, decimal exportAmount = 1000m, decimal exchangeRate = 1m,
        decimal refundRate = 13m, decimal refundableAmount = 130m, decimal refundedAmount = 0m)
        => new()
        {
            RefundNo = refundNo,
            RefundPeriod = "2026-08",
            DeclareDate = new DateTime(2026, 8, 5),
            ExportAmount = exportAmount,
            Currency = "USD",
            ExchangeRate = exchangeRate,
            RefundRate = refundRate,
            RefundableAmount = refundableAmount,
            RefundedAmount = refundedAmount,
            Status = "待申报"
        };

    private static T AssertOk<T>(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<ApiResponse<T>>(ok.Value);
        Assert.Equal(ErrorCodes.Success, resp.Code);
        Assert.NotNull(resp.Data);
        return resp.Data!;
    }

    // ==================== 1. 实际列精度（EF 模型 + 真实 INFORMATION_SCHEMA） ====================

    [Fact]
    public async Task Actual_column_precision_is_18_4_for_amounts_and_18_6_for_the_exchange_rate()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();

        Assert.Equal("decimal(18,4)", ColumnType(db, nameof(BaseTaxRefund.ExportAmount)));
        Assert.Equal("decimal(18,4)", ColumnType(db, nameof(BaseTaxRefund.RefundRate)));
        Assert.Equal("decimal(18,4)", ColumnType(db, nameof(BaseTaxRefund.RefundableAmount)));
        Assert.Equal("decimal(18,4)", ColumnType(db, nameof(BaseTaxRefund.RefundedAmount)));
        Assert.Equal("decimal(18,6)", ColumnType(db, nameof(BaseTaxRefund.ExchangeRate)));

        Assert.Equal("decimal(18,4)", await SqlColumnTypeAsync(db, "BaseTaxRefunds", "ExportAmount"));
        Assert.Equal("decimal(18,4)", await SqlColumnTypeAsync(db, "BaseTaxRefunds", "RefundRate"));
        Assert.Equal("decimal(18,4)", await SqlColumnTypeAsync(db, "BaseTaxRefunds", "RefundableAmount"));
        Assert.Equal("decimal(18,4)", await SqlColumnTypeAsync(db, "BaseTaxRefunds", "RefundedAmount"));
        Assert.Equal("decimal(18,6)", await SqlColumnTypeAsync(db, "BaseTaxRefunds", "ExchangeRate"));

        Assert.Equal(18, TaxRefundLedgerRules.DecimalPrecision);
        Assert.Equal(4, TaxRefundLedgerRules.AmountScale);
        Assert.Equal(6, TaxRefundLedgerRules.ExchangeRateScale);
        Assert.Equal(99_999_999_999_999.9999m, Max4);
        Assert.Equal(999_999_999_999.999999m, Max6);
    }

    // ==================== 2. 合法 4 位小数金额 / 6 位小数汇率原样往返（真实列） ====================

    [Fact]
    public async Task Four_decimal_amounts_and_six_decimal_rates_round_trip_verbatim_through_real_columns()
    {
        Guard();
        var refundNo = $"TR-529-4DP-{Tag()}";

        await using (var db = _fixture.CreateDbContext())
        {
            var created = AssertOk<BaseTaxRefund>(await _fixture.NewController(db).Create(
                Ledger(refundNo, exportAmount: 1234.5678m, exchangeRate: 7.123456m, refundRate: 13.1234m,
                    refundableAmount: 160.4321m, refundedAmount: 0.0001m)));
            Assert.Equal(1234.5678m, created.ExportAmount);
            Assert.Equal(7.123456m, created.ExchangeRate);
        }

        await using (var db = _fixture.CreateDbContext())
        {
            var stored = await db.BaseTaxRefunds.AsNoTracking().SingleAsync(r => r.RefundNo == refundNo);
            Assert.Equal(1234.5678m, stored.ExportAmount);
            Assert.Equal(7.123456m, stored.ExchangeRate);
            Assert.Equal(13.1234m, stored.RefundRate);
            Assert.Equal(160.4321m, stored.RefundableAmount);
            Assert.Equal(0.0001m, stored.RefundedAmount);

            // 真实列以四位 / 六位小数原样保存（绝不收敛为 decimal(18,2) 的两位）。
            var (amount, rate, refundRate, refundable, refunded) = await SqlTextAsync(db, refundNo);
            Assert.Equal("1234.5678", amount);
            Assert.Equal("7.123456", rate);
            Assert.Equal("13.1234", refundRate);
            Assert.Equal("160.4321", refundable);
            Assert.Equal("0.0001", refunded);
        }
    }

    [Fact]
    public async Task Exact_four_decimal_and_six_decimal_maxima_round_trip_verbatim_through_real_columns()
    {
        Guard();
        var refundNo = $"TR-529-MAX-{Tag()}";

        await using var db = _fixture.CreateDbContext();
        AssertOk<BaseTaxRefund>(await _fixture.NewController(db).Create(
            Ledger(refundNo, exportAmount: Max4, exchangeRate: Max6, refundRate: 100m,
                refundableAmount: Max4, refundedAmount: Max4)));

        var (amount, rate, refundRate, _, _) = await SqlTextAsync(db, refundNo);
        Assert.Equal("99999999999999.9999", amount);
        Assert.Equal("999999999999.999999", rate);
        Assert.Equal("100.0000", refundRate);
    }

    // ==================== 3. 过小数位 / 零负汇率 / 量级溢出：写入之前受控拒绝（零台账行） ====================

    [Fact]
    public async Task Over_scale_zero_negative_and_overflowing_values_are_rejected_with_zero_writes()
    {
        Guard();
        var tag = Tag();
        var before = await CountAsync(_fixture, "BaseTaxRefunds");

        await using (var db = _fixture.CreateDbContext())
        {
            var controller = _fixture.NewController(db);

            var fifth = await Assert.ThrowsAsync<BusinessException>(() => controller.Create(
                Ledger($"TR-529-5DP-{tag}", exportAmount: 1000.00005m)));
            Assert.Equal(ErrorCodes.InvalidParameter, fifth.Code);
            Assert.Contains("ExportAmount", fifth.Message);

            var seventh = await Assert.ThrowsAsync<BusinessException>(() => controller.Create(
                Ledger($"TR-529-7DP-{tag}", exchangeRate: 7.1234567m)));
            Assert.Equal(ErrorCodes.InvalidParameter, seventh.Code);
            Assert.Contains("ExchangeRate", seventh.Message);

            var zeroRate = await Assert.ThrowsAsync<BusinessException>(() => controller.Create(
                Ledger($"TR-529-ZERO-{tag}", exchangeRate: 0m)));
            Assert.Equal(ErrorCodes.InvalidParameter, zeroRate.Code);
            Assert.Contains("ExchangeRate", zeroRate.Message);

            var negativeRate = await Assert.ThrowsAsync<BusinessException>(() => controller.Create(
                Ledger($"TR-529-NEG-{tag}", exchangeRate: -6.5m)));
            Assert.Equal(ErrorCodes.InvalidParameter, negativeRate.Code);

            var overflowAmount = await Assert.ThrowsAsync<BusinessException>(() => controller.Create(
                Ledger($"TR-529-OVF-{tag}", exportAmount: 100_000_000_000_000m,
                    refundableAmount: 0m, refundedAmount: 0m)));
            Assert.Equal(ErrorCodes.InvalidParameter, overflowAmount.Code);
            Assert.Contains("ExportAmount", overflowAmount.Message);

            var overflowRate = await Assert.ThrowsAsync<BusinessException>(() => controller.Create(
                Ledger($"TR-529-OVR-{tag}", exchangeRate: 1_000_000_000_000m)));
            Assert.Equal(ErrorCodes.InvalidParameter, overflowRate.Code);
            Assert.Contains("ExchangeRate", overflowRate.Message);

            Assert.Equal(before, await CountAsync(_fixture, "BaseTaxRefunds"));
        }

        // 同一受权控制器随后合法创建成功（拒绝来自护栏而不是授权），且只新增一行。
        await using (var db = _fixture.CreateDbContext())
        {
            AssertOk<BaseTaxRefund>(await _fixture.NewController(db).Create(
                Ledger($"TR-529-OK-{tag}", exportAmount: 1000.1234m, exchangeRate: 7.123456m)));
        }

        Assert.Equal(before + 1, await CountAsync(_fixture, "BaseTaxRefunds"));
    }

    [Fact]
    public async Task Half_up_rounding_onto_the_limit_is_still_rejected_while_the_exact_limit_is_accepted()
    {
        Guard();
        var tag = Tag();
        var before = await CountAsync(_fixture, "BaseTaxRefunds");

        await using var db = _fixture.CreateDbContext();
        var controller = _fixture.NewController(db);

        // 半进位取整后恰好落到 DECIMAL(18,4) 上限（99_999_999_999_999.99985 → …9999）仍被拒绝：
        // 护栏判定的是原值，绝不采信「取整之后才可存储」。
        var ontoLimit = await Assert.ThrowsAsync<BusinessException>(() => controller.Create(
            Ledger($"TR-529-HALF1-{tag}", exportAmount: 99_999_999_999_999.99985m,
                refundableAmount: 0m, refundedAmount: 0m)));
        Assert.Equal(ErrorCodes.InvalidParameter, ontoLimit.Code);
        Assert.Contains("ExportAmount", ontoLimit.Message);

        // 半进位取整后越过上限（…99995 → 100_000_000_000_000.0000）同样拒绝。
        var pastLimit = await Assert.ThrowsAsync<BusinessException>(() => controller.Create(
            Ledger($"TR-529-HALF2-{tag}", exportAmount: 99_999_999_999_999.99995m,
                refundableAmount: 0m, refundedAmount: 0m)));
        Assert.Equal(ErrorCodes.InvalidParameter, pastLimit.Code);

        // 汇率列同理：半进位取整到六位上限的 7 位小数汇率仍拒绝。
        var rateOntoLimit = await Assert.ThrowsAsync<BusinessException>(() => controller.Create(
            Ledger($"TR-529-HALF3-{tag}", exchangeRate: 999_999_999_999.9999995m)));
        Assert.Equal(ErrorCodes.InvalidParameter, rateOntoLimit.Code);

        Assert.Equal(before, await CountAsync(_fixture, "BaseTaxRefunds"));
    }

    // ==================== 4. 被拒修改不改变权威行（金额 / 汇率 / 审计时间戳） ====================

    [Fact]
    public async Task Rejected_update_leaves_the_authoritative_ledger_row_unchanged()
    {
        Guard();
        var tag = Tag();
        var refundNo = $"TR-529-UPD-{tag}";
        long ledgerId;

        await using (var seed = _fixture.CreateDbContext())
        {
            ledgerId = AssertOk<BaseTaxRefund>(await _fixture.NewController(seed).Create(
                Ledger(refundNo, exportAmount: 700.1234m, exchangeRate: 6.54321m, refundRate: 9m))).Id;
        }

        await using (var db = _fixture.CreateDbContext())
        {
            var before = await db.BaseTaxRefunds.AsNoTracking().SingleAsync(r => r.Id == ledgerId);
            var controller = _fixture.NewController(db);

            var overScale = await db.BaseTaxRefunds.AsNoTracking().SingleAsync(r => r.Id == ledgerId);
            overScale.ExportAmount = 800.00005m;              // 第 5 位小数
            var rejected = await Assert.ThrowsAsync<BusinessException>(
                () => controller.Update(ledgerId, overScale));
            Assert.Equal(ErrorCodes.InvalidParameter, rejected.Code);
            Assert.Contains("ExportAmount", rejected.Message);

            var badRate = await db.BaseTaxRefunds.AsNoTracking().SingleAsync(r => r.Id == ledgerId);
            badRate.ExchangeRate = 1.0000005m;                // 第 7 位小数
            var rejectedRate = await Assert.ThrowsAsync<BusinessException>(
                () => controller.Update(ledgerId, badRate));
            Assert.Equal(ErrorCodes.InvalidParameter, rejectedRate.Code);
            Assert.Contains("ExchangeRate", rejectedRate.Message);

            var zeroRate = await db.BaseTaxRefunds.AsNoTracking().SingleAsync(r => r.Id == ledgerId);
            zeroRate.ExchangeRate = 0m;
            await Assert.ThrowsAsync<BusinessException>(() => controller.Update(ledgerId, zeroRate));

            var after = await db.BaseTaxRefunds.AsNoTracking().SingleAsync(r => r.Id == ledgerId);
            Assert.Equal(before.ExportAmount, after.ExportAmount);
            Assert.Equal(before.ExchangeRate, after.ExchangeRate);
            Assert.Equal(before.RefundRate, after.RefundRate);
            Assert.Equal(before.RefundableAmount, after.RefundableAmount);
            Assert.Equal(before.RefundedAmount, after.RefundedAmount);
            Assert.Equal(before.CustomerId, after.CustomerId);
            Assert.Equal(before.UpdatedAt, after.UpdatedAt);
            Assert.False(after.IsDeleted);
        }
    }

    // ==================== 5. 真实列行为：绕过护栏时静默取整 / 算术溢出（8115） ====================

    [Fact]
    public async Task Raw_writes_bypassing_the_guard_are_rounded_or_rejected_by_the_real_columns()
    {
        Guard();

        // 绕过应用层护栏的原始 EF 写入：真实四位小数列对第 5 位小数静默取整（这正是护栏必须提前拒绝的原因）。
        var roundedNo = $"TR-529-RAW-{Tag()}";
        await using (var db = _fixture.CreateDbContext())
        {
            db.BaseTaxRefunds.Add(Ledger(roundedNo, exportAmount: 1.00006m));
            await db.SaveChangesAsync();
        }

        await using (var db = _fixture.CreateDbContext())
        {
            var (amount, _, _, _, _) = await SqlTextAsync(db, roundedNo);
            Assert.Equal("1.0001", amount);      // 1.00006 → 1.0001：原值并未原样存储
        }

        // 超出列容量的原始写入被 SQL Server 以算术溢出（8115）拒绝 —— 应用层护栏把它提前成受控错误。
        var overflow = await Assert.ThrowsAsync<SqlException>(() => ScalarAsync(_fixture.ConnectionString,
            "SELECT CAST(@v AS decimal(18,4))", 100_000_000_000_000m));
        Assert.Equal(8115, overflow.Number);

        // 汇率列（DECIMAL(18,6)）对第 7 位小数同样静默取整。
        var converted = await ScalarAsync(_fixture.ConnectionString,
            "SELECT CONVERT(varchar(50), CAST(@v AS decimal(18,6)))", 1.0000006m);
        Assert.Equal("1.000001", converted);

        // 恰好上限本身可被真实列原样保留（护栏放行的边界与列容量一致）。
        var limit = await ScalarAsync(_fixture.ConnectionString,
            "SELECT CONVERT(varchar(50), CAST(@v AS decimal(18,4)))", Max4);
        Assert.Equal("99999999999999.9999", limit);
    }

    // ==================== 工厂与断言辅助 ====================

    private static string ColumnType(ErpDbContext db, string propertyName)
        => db.Model.FindEntityType(typeof(BaseTaxRefund))!.FindProperty(propertyName)!.GetColumnType()!;

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

    private static async Task<(string ExportAmount, string ExchangeRate, string RefundRate,
        string RefundableAmount, string RefundedAmount)> SqlTextAsync(ErpDbContext db, string refundNo)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open) await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT CONVERT(varchar(50), ExportAmount), CONVERT(varchar(50), ExchangeRate), " +
            "CONVERT(varchar(50), RefundRate), CONVERT(varchar(50), RefundableAmount), " +
            "CONVERT(varchar(50), RefundedAmount) FROM db_owner.BaseTaxRefunds WHERE RefundNo = @no";
        AddParameter(command, "@no", refundNo);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
            reader.GetString(4));
    }

    private static async Task<long> CountAsync(
        TaxRefundLedgerPersistedNumericBoundsSqlServerFixture fixture, string table)
    {
        await using var db = fixture.CreateDbContext();
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open) await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM db_owner.{table}";
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    /// <summary>只读标量探针：绝不写入任何行（用于真实 SQL Server 自身的取整 / 溢出证据）。</summary>
    private static async Task<string> ScalarAsync(string connectionString, string sql, decimal value)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        AddParameter(command, "@v", value);
        return (string)(await command.ExecuteScalarAsync())!;
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
/// ERP-529 专用 localdb 目标 Fixture：每次运行创建一个全新 GUID 后缀库并初始化为完整 NEWERP 结构 + 种子数据，
/// 再播种一个真实启用身份（系统内置角色 = ERP-097 特权口径），供真实 <see cref="TaxRefundController"/> 使用。
/// <para>任何库访问 / 建库之前先过专用目标护栏（实例 <c>NEWERP_AutoAcceptance</c> / 库名前缀 <c>NEWERP_AUTOTEST</c> /
/// 集成安全），发现同名库已存在立即拒绝，绝不 drop / reset / 复用任何数据库。</para>
/// </summary>
public sealed class TaxRefundLedgerPersistedNumericBoundsSqlServerFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";
    public const string DefaultDatabaseName = "NEWERP_AUTOTEST_ERP529";

    private long _authorizedUserId;

    public string ConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = BuildDefaultConnectionString();

        AssertDedicatedTarget(connectionString);
        ConnectionString = connectionString;

        Console.WriteLine($"[ERP-529] 目标库护栏放行（实例含 {InstanceMarker}，库名前缀 {DatabasePrefix}）。");

        await CreateFreshDatabaseAsync();
        await SeedAuthorizedUserAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public ErpDbContext CreateDbContext() => new(BuildOptions());

    /// <summary>绑定已播种的真实启用身份（系统内置角色 → ERP-097 特权数据范围）的退税台账控制器。</summary>
    public TaxRefundController NewController(ErpDbContext db)
    {
        var http = new DefaultHttpContext();
        http.User = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.NameIdentifier, _authorizedUserId.ToString()) }, "Test"));
        var controller = new TaxRefundController(new GenericService<BaseTaxRefund>(db), db);
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        return controller;
    }

    private DbContextOptions<ErpDbContext> BuildOptions()
        => new DbContextOptionsBuilder<ErpDbContext>().UseSqlServer(ConnectionString).Options;

    private static string BuildDefaultConnectionString()
        => $"Server=(localdb)\\{InstanceMarker};Initial Catalog={DefaultDatabaseName}_{Guid.NewGuid():N};"
           + "Integrated Security=true;TrustServerCertificate=true;";

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

        // 任何库访问 / 建库之前再次护栏：绝不使用生产或非专用目标。
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

        Console.WriteLine("[ERP-529] 集成场景就绪：全新 GUID 库 + 完整 NEWERP 结构 + 种子数据（不清理既有行）。");
    }

    /// <summary>播种真实启用身份（系统内置角色，与 ERP-442 同口径；不新增任何权限 / 菜单 / 授权模型）。</summary>
    private async Task SeedAuthorizedUserAsync()
    {
        await using var db = CreateDbContext();
        var code = $"erp529-adm-{Guid.NewGuid():N}";
        var user = new SysUser
        {
            UserName = code, PasswordHash = "hash", PasswordSalt = "salt",
            DisplayName = "ERP529 特权测试账号", Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        await db.SaveChangesAsync();

        var role = new SysRole { RoleName = code, RoleCode = code, IsSystem = true };
        db.SysRoles.Add(role);
        await db.SaveChangesAsync();

        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        await db.SaveChangesAsync();

        _authorizedUserId = user.Id;
    }
}

/// <summary>目标库护栏 fail-closed 覆盖：非专用目标必须在访问数据库之前被拒绝。</summary>
public sealed class TaxRefundLedgerPersistedNumericBoundsTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;User Id=sa;Password=secret")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(
            () => TaxRefundLedgerPersistedNumericBoundsSqlServerFixture.AssertDedicatedTarget(connection));
}




