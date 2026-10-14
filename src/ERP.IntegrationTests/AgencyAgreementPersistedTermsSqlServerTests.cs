using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.DTOs;
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
/// ERP-516 代理服务费协议「显式固定金额存储边界 + 登记前已持久化费用条款复核」真实 SQL Server 集成测试
/// （GUID 独占 <c>NEWERP_AUTOTEST_ERP516_*</c> 目标）：
/// <list type="number">
/// <item>实际列精度：真实 <c>INFORMATION_SCHEMA</c> 为 <c>decimal(18,2)</c>（固定金额）/ <c>decimal(9,4)</c>（费率），
/// 与规则常量同源；恰好等于上限的固定金额在真实列上原样往返；</item>
/// <item>创建 / 修改：受权账号提交「取整进位越过上限 / <c>1e16</c> / <c>decimal.MaxValue</c>」一律被受控拒绝
/// （零协议写入），同一控制器随后合法创建成功；旁路守卫的原始 EF 写入被真实列 / 驱动拒绝；</item>
/// <item>零小数币种（<c>JPY</c>）取整进位越过上限被拒绝、整数上限原样落库；</item>
/// <item>登记：真实 SQL 存量非法条款（零金额 / 负金额 / 口径互斥 / 未知计费方式 / 费率越界）在冻结状态与审计字段
/// 之前被拒绝且状态 / 登记人 / 时间戳零变更；合法协议照常「创建 → 登记 → 读取 → 作废」。</item>
/// </list>
/// <para>安全口径：目标必须为专用 localdb 实例 <c>NEWERP_AutoAcceptance</c>、库名前缀 <c>NEWERP_AUTOTEST</c> 且
/// <c>Integrated Security</c>；每次运行只创建一个全新 GUID 后缀库，发现同名库已存在立即拒绝，绝不 drop / reset /
/// 复用任何数据库；连接串只来自进程环境变量或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据。</para>
/// <para>构建完成不等于阶段验收：本文件只有在受控 localdb 上真实执行通过才算验收证据。</para>
/// </summary>
public sealed class AgencyAgreementPersistedTermsSqlServerTests
    : IClassFixture<AgencyAgreementPersistedTermsSqlServerFixture>
{
    private const decimal MaxAmount = FinancePersistedAmountRules.MaxDecimal18Scale2;
    private const decimal HalfUpToMax = 9_999_999_999_999_999.985m;      // USD 半进位后恰好等于上限
    private const decimal JustOver = 9_999_999_999_999_999.999m;         // USD 取整进位越过上限
    private const decimal RoundUpCrossing = 9_999_999_999_999_999.995m;  // USD 取整进位越过上限
    private const decimal OneE16 = 10_000_000_000_000_000m;               // 1e16（超出上限）
    private const decimal JpyRoundUpCrossing = 9_999_999_999_999_999.6m;  // JPY 取整进位越过上限
    private const decimal JpyMaxInteger = 9_999_999_999_999_999m;         // JPY 整数上限

    private readonly AgencyAgreementPersistedTermsSqlServerFixture _fixture;

    public AgencyAgreementPersistedTermsSqlServerTests(AgencyAgreementPersistedTermsSqlServerFixture fixture)
        => _fixture = fixture;

    /// <summary>访问数据库前复核专用目标护栏（实例名 / 库名前缀 / 集成安全）。</summary>
    private void Guard()
    {
        var target = new SqlConnectionStringBuilder(_fixture.ConnectionString);
        Assert.Equal("(localdb)\\NEWERP_AutoAcceptance", target.DataSource, ignoreCase: true);
        Assert.StartsWith(AgencyAgreementPersistedTermsSqlServerFixture.DatabasePrefix, target.InitialCatalog,
            StringComparison.OrdinalIgnoreCase);
        Assert.True(target.IntegratedSecurity);
    }

    private static string Tag() => Guid.NewGuid().ToString("N")[..10];

    // ==================== 1. 实际列精度 + 上限原样往返 + 真实列取整行为 ====================

    [Fact]
    public async Task Actual_column_precision_and_boundary_values_round_trip()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();

        Assert.Equal(18, FinancePersistedAmountRules.DecimalPrecision);
        Assert.Equal(2, FinancePersistedAmountRules.DecimalScale);

        var entity = db.Model.FindEntityType(typeof(AgencyServiceFeeAgreement))!;
        Assert.Equal("decimal(18,2)",
            entity.FindProperty(nameof(AgencyServiceFeeAgreement.FixedAmount))!.GetColumnType());
        Assert.Equal("decimal(9,4)",
            entity.FindProperty(nameof(AgencyServiceFeeAgreement.RatePercent))!.GetColumnType());

        Assert.Equal("decimal(18,2)", await SqlColumnTypeAsync(db, "AgencyServiceFeeAgreements", "FixedAmount"));
        Assert.Equal("decimal(9,4)", await SqlColumnTypeAsync(db, "AgencyServiceFeeAgreements", "RatePercent"));

        // 真实列自身对多余小数位取整：应用层必须提前拒绝，而不是依赖 SQL 静默取整。
        Assert.Equal(1.01m, await SqlScalarDecimalAsync(db, "SELECT CAST(1.005 AS decimal(18,2))"));

        var customerId = await SeedCustomerAsync(db, "ERP-516 精度边界客户");
        var userId = await SeedOperatorAsync(db);
        var controller = NewController(db, userId);

        var exact = CreatedId(await controller.Create(FixedDraft(customerId, MaxAmount, "USD")));
        Assert.Equal(MaxAmount, await FixedAmountAsync(db, exact));

        var halfUp = CreatedId(await controller.Create(FixedDraft(customerId, HalfUpToMax, "USD")));
        Assert.Equal(MaxAmount, await FixedAmountAsync(db, halfUp));

        var jpy = CreatedId(await controller.Create(FixedDraft(customerId, JpyMaxInteger, "JPY")));
        Assert.Equal(JpyMaxInteger, await FixedAmountAsync(db, jpy));
    }

    // ==================== 2. 创建 / 修改：越界受控拒绝（零写入） + 原始 EF 写入被真实列拒绝 ====================

    [Theory]
    [InlineData("round-up-cross")]
    [InlineData("just-over")]
    [InlineData("1e16")]
    [InlineData("max-decimal")]
    [InlineData("jpy-round-up-cross")]
    public async Task Create_rejects_unpersistable_fixed_amount_without_write(string scenario)
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var customerId = await SeedCustomerAsync(db, "ERP-516 创建越界客户");
        var userId = await SeedOperatorAsync(db);
        var controller = NewController(db, userId);
        var (amount, currency) = OverLimit(scenario);

        var before = await db.AgencyServiceFeeAgreements.AsNoTracking().CountAsync();
        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => controller.Create(FixedDraft(customerId, amount, currency)));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Contains("DECIMAL(18,2)", ex.Message);
        Assert.Equal(before, await db.AgencyServiceFeeAgreements.AsNoTracking().CountAsync());

        // 同一受权控制器随后合法创建成功：证明拒绝原因是金额而非授权 / 客户，且零身份消耗。
        var id = CreatedId(await controller.Create(FixedDraft(customerId, 100m, currency)));
        Assert.True(id > 0);
        Assert.Equal(before + 1, await db.AgencyServiceFeeAgreements.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task Raw_ef_write_of_unpersistable_fixed_amount_is_rejected_by_real_column()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var customerId = await SeedCustomerAsync(db, "ERP-516 旁路越界客户");
        var before = await db.AgencyServiceFeeAgreements.AsNoTracking().CountAsync();

        db.AgencyServiceFeeAgreements.Add(Stored(
            customerId, $"ASF516-RAW-{Tag()}", AgencyServiceFeeAgreementRules.FeeMethodFixed, 0m, OneE16, "USD"));

        // 旁路应用层守卫的原始 EF 写入被真实 decimal(18,2) 列 / 驱动拒绝（绝不静默截断）。
        await Assert.ThrowsAnyAsync<DbUpdateException>(() => db.SaveChangesAsync());

        await using var verify = _fixture.CreateDbContext();
        Assert.Equal(before, await verify.AgencyServiceFeeAgreements.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task Update_rejects_unpersistable_fixed_amount_and_leaves_stored_terms_unchanged()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var customerId = await SeedCustomerAsync(db, "ERP-516 修改越界客户");
        var userId = await SeedOperatorAsync(db);
        var controller = NewController(db, userId);
        var id = CreatedId(await controller.Create(FixedDraft(customerId, 800m, "USD")));
        var before = await db.AgencyServiceFeeAgreements.AsNoTracking().SingleAsync(a => a.Id == id);

        var request = FixedDraft(customerId, OneE16, "USD");
        request.Remark = "SHOULD_NOT_BE_SAVED";
        var ex = await Assert.ThrowsAsync<BusinessException>(() => controller.Update(id, request));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        var after = await db.AgencyServiceFeeAgreements.AsNoTracking().SingleAsync(a => a.Id == id);
        Assert.Equal(before.FixedAmount, after.FixedAmount);
        Assert.Equal(before.Currency, after.Currency);
        Assert.Equal(before.FeeMethod, after.FeeMethod);
        Assert.Equal(before.Remark, after.Remark);
        Assert.Equal(before.Status, after.Status);
        Assert.Equal(before.UpdatedAt, after.UpdatedAt);
    }

    // ==================== 3. 登记：真实 SQL 存量非法条款在冻结之前被拒绝 ====================

    [Theory]
    [InlineData("fixed-zero")]
    [InlineData("fixed-negative")]
    [InlineData("rate-zero")]
    [InlineData("rate-over-limit")]
    [InlineData("unknown-method")]
    [InlineData("exclusive-fixed")]
    [InlineData("exclusive-rate")]
    public async Task Registration_refuses_malformed_stored_terms_with_zero_status_and_audit_change(string scenario)
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var customerId = await SeedCustomerAsync(db, "ERP-516 存量非法客户");
        var userId = await SeedOperatorAsync(db);
        var controller = NewController(db, userId);
        var (method, rate, fixedAmount, currency) = Malformed(scenario);
        var id = await SeedStoredAsync(db, customerId, method, rate, fixedAmount, currency);
        var before = await db.AgencyServiceFeeAgreements.AsNoTracking().SingleAsync(a => a.Id == id);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => controller.Record(id));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        var after = await db.AgencyServiceFeeAgreements.AsNoTracking().SingleAsync(a => a.Id == id);
        Assert.Equal(before.Status, after.Status);
        Assert.Equal(AgencyServiceFeeAgreementRules.StatusDraft, after.Status);
        Assert.Equal(before.RecordedAt, after.RecordedAt);
        Assert.Equal(before.RecordedBy, after.RecordedBy);
        Assert.Equal(before.VoidedAt, after.VoidedAt);
        Assert.Equal(before.RatePercent, after.RatePercent);
        Assert.Equal(before.FixedAmount, after.FixedAmount);
        Assert.Equal(before.FeeMethod, after.FeeMethod);
        Assert.Equal(before.UpdatedAt, after.UpdatedAt);
    }

    [Fact]
    public async Task Registration_accepts_stored_exact_bound_amount_and_freezes_status()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var customerId = await SeedCustomerAsync(db, "ERP-516 半进位客户");
        var userId = await SeedOperatorAsync(db);
        var controller = NewController(db, userId);

        // 真实列可存下的最大金额照常登记；列无法容纳的值连旁路写入都被拒绝（见 §2），
        // 因此登记闸门只需覆盖「列能放下但口径不可复现」的存量行。
        var id = await SeedStoredAsync(db, customerId, AgencyServiceFeeAgreementRules.FeeMethodFixed, 0m, MaxAmount, "USD");
        var recorded = AssertOk<AgencyServiceFeeAgreementDto>(await controller.Record(id));

        Assert.True(recorded.IsRecorded);
        Assert.Equal(MaxAmount, recorded.FixedAmount);
        Assert.NotNull(recorded.RecordedAt);
    }

    // ==================== 4. 合法生命周期：创建 → 登记 → 读取 → 作废 ====================

    [Fact]
    public async Task Valid_fixed_and_rate_agreements_record_read_and_void_persisting_exact_terms()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var customerId = await SeedCustomerAsync(db, "ERP-516 合法流转客户");
        var userId = await SeedOperatorAsync(db);
        var controller = NewController(db, userId);

        var fixedId = CreatedId(await controller.Create(FixedDraft(customerId, MaxAmount, "USD")));
        var recordedFixed = AssertOk<AgencyServiceFeeAgreementDto>(await controller.Record(fixedId));
        Assert.True(recordedFixed.IsRecorded);
        Assert.Equal(MaxAmount, recordedFixed.FixedAmount);
        Assert.Equal(0m, recordedFixed.RatePercent);

        var reloaded = AssertOk<AgencyServiceFeeAgreementDto>(await controller.GetById(fixedId));
        Assert.Equal(AgencyServiceFeeAgreementRules.StatusRecorded, reloaded.Status);
        Assert.Equal(MaxAmount, reloaded.FixedAmount);

        var voided = AssertOk<AgencyServiceFeeAgreementDto>(await controller.Void(
            fixedId, new AgencyServiceFeeAgreementVoidRequest { Reason = "协议终止" }));
        Assert.True(voided.IsVoided);
        Assert.Equal(MaxAmount, voided.FixedAmount);
        Assert.Equal(MaxAmount, await FixedAmountAsync(db, fixedId));

        // 费率路径口径未被本次改动影响：合法费率协议照常创建并登记。
        var rateId = CreatedId(await controller.Create(RateDraft(customerId, 1.5m, "USD")));
        var recordedRate = AssertOk<AgencyServiceFeeAgreementDto>(await controller.Record(rateId));
        Assert.True(recordedRate.IsRecorded);
        Assert.Equal(1.5m, recordedRate.RatePercent);
        Assert.Equal(0m, recordedRate.FixedAmount);
    }

    // ==================== 5. 脚手架 ====================

    private static AgencyServiceFeeAgreementController NewController(ErpDbContext db, long? userId)
    {
        var claims = userId.HasValue
            ? new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }
            : Array.Empty<Claim>();
        return new AgencyServiceFeeAgreementController(db)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) }
            }
        };
    }

    private static T AssertOk<T>(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<ApiResponse<T>>(ok.Value);
        Assert.Equal(ErrorCodes.Success, resp.Code);
        Assert.NotNull(resp.Data);
        return resp.Data!;
    }

    private static long CreatedId(IActionResult result)
        => AssertOk<AgencyServiceFeeAgreementDto>(result).Id;

    private static async Task<long> SeedCustomerAsync(ErpDbContext db, string name)
    {
        var customer = new BaseCustomer
        {
            CustomerCode = $"ASF516-C-{Tag()}", CustomerName = name, Status = 1, CreditStatus = "正常"
        };
        db.BaseCustomers.Add(customer);
        await db.SaveChangesAsync();
        return customer.Id;
    }

    /// <summary>播种真实授权账号：既有「客户资料」菜单授权 + 启用账号（系统内置角色 → 数据范围不受限）。</summary>
    private static async Task<long> SeedOperatorAsync(ErpDbContext db)
    {
        var user = new SysUser
        {
            UserName = $"asf516-op-{Guid.NewGuid():N}", DisplayName = "ERP-516 固定金额操作员",
            PasswordHash = "hash", PasswordSalt = "salt", Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        await db.SaveChangesAsync();

        var role = new SysRole
        {
            RoleName = "ERP-516 固定金额特权角色", RoleCode = $"Asf516Priv-{Guid.NewGuid():N}", IsSystem = true
        };
        db.SysRoles.Add(role);
        await db.SaveChangesAsync();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        await db.SaveChangesAsync();

        var menuId = await db.SysMenus.AsNoTracking()
            .Where(m => m.MenuCode == AgencyServiceFeeReconciliationRules.RequiredMenuCode && !m.IsDeleted)
            .Select(m => m.Id)
            .FirstAsync();
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menuId });
        await db.SaveChangesAsync();
        return user.Id;
    }

    private static AgencyServiceFeeAgreementSaveDto FixedDraft(long customerId, decimal amount, string currency)
        => new()
        {
            AgreementNo = $"ASF516-{Tag()}",
            CustomerId = customerId,
            EffectiveFrom = new DateTime(2026, 1, 1),
            Currency = currency,
            FeeMethod = AgencyServiceFeeAgreementRules.FeeMethodFixed,
            FixedAmount = amount,
            FeeBasis = "按月固定",
            Remark = "ERP-516_SQL_TEST"
        };

    private static AgencyServiceFeeAgreementSaveDto RateDraft(long customerId, decimal ratePercent, string currency)
        => new()
        {
            AgreementNo = $"ASF516-RATE-{Tag()}",
            CustomerId = customerId,
            EffectiveFrom = new DateTime(2026, 1, 1),
            Currency = currency,
            FeeMethod = AgencyServiceFeeAgreementRules.FeeMethodRate,
            RatePercent = ratePercent,
            FeeBasis = "按出口发票金额",
            Remark = "ERP-516_SQL_TEST"
        };

    private static AgencyServiceFeeAgreement Stored(
        long customerId, string agreementNo, string feeMethod, decimal ratePercent, decimal fixedAmount, string currency)
        => new()
        {
            AgreementNo = agreementNo,
            NormalizedAgreementNo = AgencyServiceFeeAgreementRules.NormalizeIdentityPart(agreementNo),
            CustomerId = customerId,
            CustomerCode = "ASF516-INT",
            CustomerName = "集成测试客户",
            EffectiveFrom = new DateTime(2026, 1, 1),
            Currency = currency,
            FeeMethod = feeMethod,
            RatePercent = ratePercent,
            FixedAmount = fixedAmount,
            FeeBasis = "按出口发票金额",
            Status = AgencyServiceFeeAgreementRules.StatusDraft
        };

    /// <summary>用真实 SQL 播种一条草稿协议（值直接落库，用于验证登记闸门）。</summary>
    private static async Task<long> SeedStoredAsync(
        ErpDbContext db, long customerId, string feeMethod, decimal ratePercent, decimal fixedAmount, string currency)
    {
        var agreement = Stored(customerId, $"ASF516-STORED-{Tag()}", feeMethod, ratePercent, fixedAmount, currency);
        db.AgencyServiceFeeAgreements.Add(agreement);
        await db.SaveChangesAsync();
        return agreement.Id;
    }

    private static Task<decimal> FixedAmountAsync(ErpDbContext db, long id)
        => db.AgencyServiceFeeAgreements.AsNoTracking().Where(a => a.Id == id).Select(a => a.FixedAmount).SingleAsync();

    private static async Task<string> SqlColumnTypeAsync(ErpDbContext db, string table, string column)
    {
        await using var conn = new SqlConnection(db.Database.GetConnectionString());
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT DATA_TYPE + '(' + CAST(NUMERIC_PRECISION AS varchar(10)) + ','"
            + " + CAST(NUMERIC_SCALE AS varchar(10)) + ')' FROM INFORMATION_SCHEMA.COLUMNS"
            + " WHERE TABLE_NAME = @table AND COLUMN_NAME = @column";
        cmd.Parameters.AddWithValue("@table", table);
        cmd.Parameters.AddWithValue("@column", column);
        return (string)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task<decimal> SqlScalarDecimalAsync(ErpDbContext db, string sql)
    {
        await using var conn = new SqlConnection(db.Database.GetConnectionString());
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToDecimal(await cmd.ExecuteScalarAsync());
    }

    /// <summary>超界场景 → (提交金额, 币种)，覆盖 USD 取整进位 / 1e16 / 极大十进制 / JPY 零小数进位。</summary>
    private static (decimal Amount, string Currency) OverLimit(string scenario) => scenario switch
    {
        "round-up-cross" => (RoundUpCrossing, "USD"),
        "just-over" => (JustOver, "USD"),
        "1e16" => (OneE16, "USD"),
        "max-decimal" => (decimal.MaxValue, "USD"),
        "jpy-round-up-cross" => (JpyRoundUpCrossing, "JPY"),
        _ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, "未知场景")
    };

    /// <summary>真实 SQL 可落库的存量非法条款场景 → (计费方式, 费率, 固定金额, 币种)。</summary>
    private static (string Method, decimal Rate, decimal FixedAmount, string Currency) Malformed(
        string scenario) => scenario switch
    {
        "fixed-zero" => (AgencyServiceFeeAgreementRules.FeeMethodFixed, 0m, 0m, "USD"),
        "fixed-negative" => (AgencyServiceFeeAgreementRules.FeeMethodFixed, 0m, -1m, "USD"),
        "rate-zero" => (AgencyServiceFeeAgreementRules.FeeMethodRate, 0m, 0m, "USD"),
        "rate-over-limit" => (AgencyServiceFeeAgreementRules.FeeMethodRate, 100.0001m, 0m, "USD"),
        "unknown-method" => ("阶梯费率", 1.5m, 0m, "USD"),
        "exclusive-fixed" => (AgencyServiceFeeAgreementRules.FeeMethodRate, 1.5m, 10m, "USD"),
        "exclusive-rate" => (AgencyServiceFeeAgreementRules.FeeMethodFixed, 1.5m, 100m, "USD"),
        _ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, "未知场景")
    };
}

/// <summary>
/// 专用 localdb 目标 Fixture（ERP-516）：把目标库初始化为完整 NEWERP 结构 + 种子数据，供真实 SQL Server
/// 固定金额存储边界集成测试复用。
/// <para>安全口径：库名一律带本次运行的 GUID 后缀（绝不复用 / 绝不 drop 既有库）；任何数据库访问之前先校验
/// 实例名、库名前缀与集成安全，连接串只来自进程环境变量或专用 localdb 默认值。</para>
/// </summary>
public sealed class AgencyAgreementPersistedTermsSqlServerFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST_ERP516_";
    public const string DefaultDatabaseName = "NEWERP_AUTOTEST_ERP516_AGREEMENTTERMS_20261014";

    public string ConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = BuildDefaultConnectionString();

        AssertDedicatedTarget(connectionString);
        ConnectionString = connectionString;

        Console.WriteLine($"[ERP-516] 目标库护栏放行（实例含 {InstanceMarker}，库名前缀 {DatabasePrefix}）。");

        await EnsureFreshDatabaseAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public ErpDbContext CreateDbContext() => new(BuildOptions());

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

    private async Task EnsureFreshDatabaseAsync()
    {
        var database = new SqlConnectionStringBuilder(ConnectionString).InitialCatalog;

        // 任何数据库访问之前再次护栏：绝不使用生产 / 非专用回退。
        AssertDedicatedTarget(ConnectionString);

        var master = new SqlConnectionStringBuilder(ConnectionString) { InitialCatalog = "master" };
        await using (var conn = new SqlConnection(master.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            // 绝不销毁已存在的 Fixture 库或其它调用方的数据库。
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

        Console.WriteLine("[ERP-516] 集成场景就绪：完整 NEWERP 结构 + 种子数据。");
    }
}

/// <summary>目标库护栏单元级校验：非专用目标必须在访问数据库之前被拒绝。</summary>
public sealed class AgencyAgreementPersistedTermsTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_ERP516_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_ERP516_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_ERP516_BAD;User Id=sa;Password=x")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(
            () => AgencyAgreementPersistedTermsSqlServerFixture.AssertDedicatedTarget(connection));

    [Fact]
    public void Accepts_the_dedicated_localdb_target_with_integrated_security()
        => AgencyAgreementPersistedTermsSqlServerFixture.AssertDedicatedTarget(
            "Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_ERP516_X;Integrated Security=true");
}
