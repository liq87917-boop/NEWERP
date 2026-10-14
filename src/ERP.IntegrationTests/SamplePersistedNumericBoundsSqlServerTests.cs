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
/// ERP-532 样品管理（<c>api/crm/samples</c>）数量（<c>Quantity</c>）与样品费（<c>SampleFee</c>）
/// 「实际存储精度」（<c>DECIMAL(18,4)</c>）的真实 SQL Server 集成测试
/// （专用 localdb <c>(localdb)\NEWERP_AutoAcceptance</c> + 全新 GUID 库）：
/// <list type="number">
/// <item>实际列精度与规则常量同源：EF 模型与真实 <c>INFORMATION_SCHEMA</c> 双向确认两列均为 <c>decimal(18,4)</c>；</item>
/// <item>合法的 4 位小数（含恰好上限）经真实受权控制器创建后**原样**往返（以 <c>CONVERT(varchar, ...)</c>
/// 逐位断言，绝不收敛为 2 位）；</item>
/// <item>第 5 位小数 / 量级溢出 / 负数在写入之前受控拒绝，零 <c>Samples</c> 行；</item>
/// <item>「半进位取整后恰好落到列上限」的过小数位值仍被拒绝（绝不采信取整结果），而恰好上限本身放行；</item>
/// <item>被拒的修改不改变权威行的任何 decimal 字段与审计时间戳；</item>
/// <item>绕过应用层护栏的原始 EF 写入会被真实列静默取整，超容量值被 SQL Server 以算术溢出（8115）拒绝 ——
/// 这正是护栏必须提前受控拒绝的原因。</item>
/// </list>
/// <para>安全口径：目标必须为专用 localdb 实例 <c>NEWERP_AutoAcceptance</c>、库名前缀 <c>NEWERP_AUTOTEST</c> 且
/// <c>Integrated Security</c>；每次运行只创建一个全新 GUID 后缀库，发现同名库已存在立即拒绝，绝不 drop / reset /
/// 复用任何数据库；连接串只来自进程环境变量或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据。</para>
/// </summary>
public sealed class SamplePersistedNumericBoundsSqlServerTests
    : IClassFixture<SamplePersistedNumericBoundsSqlServerFixture>
{
    /// <summary><c>DECIMAL(18,4)</c> 的最大可表示值（与规则常量同源）。</summary>
    private static readonly decimal Max4 = SampleAuthorizationRules.MaxDecimalMagnitude;

    /// <summary>样品持久化 decimal 列（与实体 / 建表脚本逐一对应）。</summary>
    private static readonly string[] DecimalColumns =
    {
        nameof(Sample.Quantity),
        nameof(Sample.SampleFee),
    };

    private readonly SamplePersistedNumericBoundsSqlServerFixture _fixture;

    public SamplePersistedNumericBoundsSqlServerTests(SamplePersistedNumericBoundsSqlServerFixture fixture)
        => _fixture = fixture;

    /// <summary>访问数据库前复核专用目标护栏（实例名 / 库名前缀 / 集成安全）。</summary>
    private void Guard()
        => SamplePersistedNumericBoundsSqlServerFixture.AssertDedicatedTarget(_fixture.ConnectionString);

    private static string Tag() => Guid.NewGuid().ToString("N")[..8];

    private static T Data<T>(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<ApiResponse<T>>(ok.Value);
        return response.Data!;
    }

    private static async Task AssertCodeAsync(int expected, Func<Task<IActionResult>> action)
    {
        var ex = await Assert.ThrowsAsync<BusinessException>(action);
        Assert.Equal(expected, ex.Code);
    }

    /// <summary>样品工厂（两个 decimal 列留 0，由用例按需赋值）。</summary>
    private Sample NewSample(string sampleNo, decimal quantity = 0m, decimal sampleFee = 0m) => new()
    {
        SampleNo = sampleNo,
        SampleDate = new DateTime(2026, 9, 1),
        CustomerId = _fixture.AuthorizedCustomerId,
        CustomerName = "样品精度集成客户",
        SampleType = "打样",
        Quantity = quantity,
        Unit = "PCS",
        SampleFee = sampleFee,
        Currency = "CNY",
        Express = "顺丰",
        TrackingNo = "SF001",
        Result = "待反馈",
        Remark = ""
    };

    // ==================== 1. 实际列精度（EF 模型 + 真实 INFORMATION_SCHEMA） ====================

    [Fact]
    public async Task Actual_column_precision_is_18_4_for_both_sample_decimal_columns()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();

        var entityType = db.Model.FindEntityType(typeof(Sample));
        Assert.NotNull(entityType);

        Assert.Equal(4, SampleAuthorizationRules.PersistedDecimalScale);
        Assert.Equal(99_999_999_999_999.9999m, Max4);

        foreach (var column in DecimalColumns)
        {
            var mapped = entityType!.FindProperty(column);
            Assert.NotNull(mapped);
            Assert.Equal(18, mapped!.GetPrecision());
            Assert.Equal(SampleAuthorizationRules.PersistedDecimalScale, mapped.GetScale());
            Assert.Equal("decimal(18,4)", mapped.GetColumnType());

            Assert.Equal("decimal(18,4)", await SqlColumnTypeAsync(db, "Samples", column));
        }
    }

    // ==================== 2. 合法 4 位小数（含恰好上限）原样往返（真实列） ====================

    [Fact]
    public async Task Four_decimal_values_round_trip_verbatim_through_both_real_columns()
    {
        Guard();
        var no = $"SP532-4DP-{Tag()}";

        await using (var db = _fixture.CreateDbContext())
        {
            var created = Data<Sample>(await _fixture.NewController(db)
                .Create(NewSample(no, 1234.5678m, 0.0125m)));
            Assert.True(created.Id > 0);
            Assert.Equal(1234.5678m, created.Quantity);
            Assert.Equal(0.0125m, created.SampleFee);
        }

        await using (var db = _fixture.CreateDbContext())
        {
            var stored = await db.Samples.AsNoTracking().SingleAsync(s => s.SampleNo == no);
            Assert.Equal(1234.5678m, stored.Quantity);
            Assert.Equal(0.0125m, stored.SampleFee);

            var text = await SqlRowTextAsync(db, no);
            Assert.Equal("1234.5678", text[0]);
            Assert.Equal("0.0125", text[1]);
        }
    }

    [Fact]
    public async Task Exact_four_decimal_maxima_round_trip_verbatim()
    {
        Guard();
        var no = $"SP532-MAX-{Tag()}";

        await using (var db = _fixture.CreateDbContext())
        {
            Data<Sample>(await _fixture.NewController(db).Create(NewSample(no, Max4, Max4)));
        }

        await using (var db = _fixture.CreateDbContext())
        {
            var text = await SqlRowTextAsync(db, no);
            Assert.All(text, value => Assert.Equal("99999999999999.9999", value));
        }
    }

    // ==================== 3. 过小数位 / 量级溢出 / 负数：写入之前受控拒绝（零行） ====================

    [Fact]
    public async Task Over_scale_and_overflowing_values_are_rejected_with_zero_writes()
    {
        Guard();
        var tag = Tag();
        var atLimitNo = $"SP532-AT-{tag}";
        var before = await CountAsync(_fixture, "Samples");

        await using (var db = _fixture.CreateDbContext())
        {
            var controller = _fixture.NewController(db);

            var fifth = await Assert.ThrowsAsync<BusinessException>(
                () => controller.Create(NewSample($"SP532-5DP-{tag}", quantity: 0.01255m)));
            Assert.Equal(ErrorCodes.InvalidParameter, fifth.Code);
            Assert.Contains(nameof(Sample.Quantity), fifth.Message);

            var feeFifth = await Assert.ThrowsAsync<BusinessException>(
                () => controller.Create(NewSample($"SP532-5DP-F-{tag}", sampleFee: 1.00005m)));
            Assert.Equal(ErrorCodes.InvalidParameter, feeFifth.Code);
            Assert.Contains(nameof(Sample.SampleFee), feeFifth.Message);

            var negative = await Assert.ThrowsAsync<BusinessException>(
                () => controller.Create(NewSample($"SP532-NEG-{tag}", quantity: -0.0001m)));
            Assert.Equal(ErrorCodes.InvalidParameter, negative.Code);

            var overflow = await Assert.ThrowsAsync<BusinessException>(
                () => controller.Create(NewSample($"SP532-OVR-{tag}", sampleFee: Max4 + 1m)));
            Assert.Equal(ErrorCodes.InvalidParameter, overflow.Code);

            // 半进位取整后恰好落到上限（…99985 → …9999）仍被拒绝：护栏判定原值，绝不采信取整结果。
            var ontoLimit = await Assert.ThrowsAsync<BusinessException>(
                () => controller.Create(NewSample($"SP532-HALF-{tag}", quantity: 99999999999999.99985m)));
            Assert.Equal(ErrorCodes.InvalidParameter, ontoLimit.Code);
            Assert.Contains(nameof(Sample.Quantity), ontoLimit.Message);

            Assert.Equal(before, await CountAsync(_fixture, "Samples"));

            // 恰好上限本身放行并以真实列原样保存。
            Data<Sample>(await controller.Create(NewSample(atLimitNo, Max4, Max4)));
        }

        await using (var db = _fixture.CreateDbContext())
        {
            var text = await SqlRowTextAsync(db, atLimitNo);
            Assert.All(text, value => Assert.Equal("99999999999999.9999", value));
        }

        Assert.Equal(before + 1, await CountAsync(_fixture, "Samples"));
    }

    // ==================== 4. 被拒修改不改变权威行（decimal 字段 / 审计时间戳） ====================

    [Fact]
    public async Task Rejected_update_leaves_the_authoritative_row_unchanged()
    {
        Guard();
        var tag = Tag();
        var no = $"SP532-UPD-{tag}";
        long sampleId;

        await using (var seed = _fixture.CreateDbContext())
        {
            sampleId = Data<Sample>(await _fixture.NewController(seed)
                .Create(NewSample(no, 0.0125m, 13.0555m))).Id;
        }

        await using (var db = _fixture.CreateDbContext())
        {
            var before = await db.Samples.AsNoTracking().SingleAsync(s => s.Id == sampleId);
            var controller = _fixture.NewController(db);

            var overScale = await db.Samples.SingleAsync(s => s.Id == sampleId);
            overScale.Quantity = 0.01255m;                    // 第 5 位小数
            var rejectedQuantity = await Assert.ThrowsAsync<BusinessException>(
                () => controller.Update(sampleId, overScale));
            Assert.Equal(ErrorCodes.InvalidParameter, rejectedQuantity.Code);
            Assert.Contains(nameof(Sample.Quantity), rejectedQuantity.Message);
            db.ChangeTracker.Clear();

            var overflow = await db.Samples.AsNoTracking().SingleAsync(s => s.Id == sampleId);
            overflow.SampleFee = Max4 + 0.0001m;              // 量级越界
            var rejectedFee = await Assert.ThrowsAsync<BusinessException>(
                () => controller.Update(sampleId, overflow));
            Assert.Equal(ErrorCodes.InvalidParameter, rejectedFee.Code);
            Assert.Contains(nameof(Sample.SampleFee), rejectedFee.Message);

            var after = await db.Samples.AsNoTracking().SingleAsync(s => s.Id == sampleId);
            Assert.Equal(before.Quantity, after.Quantity);
            Assert.Equal(before.SampleFee, after.SampleFee);
            Assert.Equal(before.SampleNo, after.SampleNo);
            Assert.Equal(before.CustomerName, after.CustomerName);
            Assert.Equal(before.UpdatedAt, after.UpdatedAt);
            Assert.False(after.IsDeleted);
        }
    }


    // ==================== 5. 真实列行为：绕过护栏时静默取整 / 算术溢出（8115） ====================

    [Fact]
    public async Task Raw_writes_bypassing_the_guard_are_rounded_or_rejected_by_the_real_columns()
    {
        Guard();

        // 绕过应用层护栏的原始 EF 写入：真实四位小数列对第 5 位小数静默取整（护栏必须提前拒绝的原因）。
        var roundedNo = $"SP532-RAW-{Tag()}";
        await using (var db = _fixture.CreateDbContext())
        {
            db.Samples.Add(NewSample(roundedNo, 0.01255m, 1.00005m));
            await db.SaveChangesAsync();
        }

        await using (var db = _fixture.CreateDbContext())
        {
            var stored = await db.Samples.AsNoTracking().SingleAsync(s => s.SampleNo == roundedNo);
            Assert.NotEqual(0.01255m, stored.Quantity);
            Assert.Equal(0.0126m, stored.Quantity);       // 0.01255 → 0.0126：原值并未原样存储
            Assert.NotEqual(1.00005m, stored.SampleFee);
            Assert.Equal(1.0001m, stored.SampleFee);      // 1.00005 → 1.0001：原值并未原样存储
        }

        // 超出列容量的写入被 SQL Server 以算术溢出（8115）拒绝 —— 应用层护栏把它提前成受控错误。
        var overflow = await Assert.ThrowsAsync<SqlException>(() => ScalarAsync(_fixture.ConnectionString,
            "SELECT CAST(@v AS decimal(18,4))", 100_000_000_000_000m));
        Assert.Equal(8115, overflow.Number);

        // 恰好上限本身可被真实列原样保留（护栏放行的边界与列容量一致）。
        var limit = await ScalarAsync(_fixture.ConnectionString,
            "SELECT CONVERT(varchar(50), CAST(@v AS decimal(18,4)))", Max4);
        Assert.Equal("99999999999999.9999", limit);
    }

    // ==================== 工厂与断言辅助 ====================

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

    /// <summary>按真实列顺序读取一行样品两个 decimal 列的 <c>CONVERT(varchar, ...)</c> 文本（逐位原样证据）。</summary>
    private static async Task<string[]> SqlRowTextAsync(ErpDbContext db, string sampleNo)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open) await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        var select = string.Join(", ", DecimalColumns.Select(c => $"CONVERT(varchar(50), [{c}])"));
        command.CommandText = $"SELECT {select} FROM db_owner.Samples WHERE SampleNo = @no";
        AddParameter(command, "@no", sampleNo);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        var result = new string[DecimalColumns.Length];
        for (var i = 0; i < result.Length; i++) result[i] = reader.GetString(i);
        return result;
    }

    private static async Task<long> CountAsync(
        SamplePersistedNumericBoundsSqlServerFixture fixture, string table)
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
/// ERP-532 专用 localdb 目标 Fixture：每次运行创建一个全新 GUID 后缀库并初始化为完整 NEWERP 结构 + 种子数据，
/// 再播种一个具备既有「样品管理」菜单授权的真实启用身份与一个客户，供真实 <see cref="SampleController"/> 使用。
/// <para>任何库访问 / 建库之前先过专用目标护栏（实例 <c>NEWERP_AutoAcceptance</c> / 库名前缀 <c>NEWERP_AUTOTEST</c> /
/// 集成安全），发现同名库已存在立即拒绝，绝不 drop / reset / 复用任何数据库。</para>
/// </summary>
public sealed class SamplePersistedNumericBoundsSqlServerFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";

    /// <summary>本次运行新建的 GUID 独占库名（每次运行唯一，绝不复用既有库）。</summary>
    public static string DefaultDatabaseName { get; } =
        $"{DatabasePrefix}_SAMPLEBOUNDS_{Guid.NewGuid():N}";

    private long _authorizedUserId;

    /// <summary>已播种的授权客户 Id（样品写入的 <c>CustomerId</c>）。</summary>
    public long AuthorizedCustomerId { get; private set; }

    public string ConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = BuildDefaultConnectionString();

        // 访问数据库之前先复核目标护栏（错误目标 fail closed）。
        AssertDedicatedTarget(connectionString);
        ConnectionString = connectionString;

        Console.WriteLine($"[ERP-532] 目标库护栏放行（实例 {InstanceMarker}，库名前缀 {DatabasePrefix}，集成安全）。");

        await InitialiseFreshDatabaseAsync();
        await SeedAuthorizedUserAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public ErpDbContext CreateDbContext() => new(BuildOptions());

    /// <summary>绑定已播种真实启用身份（具备既有 sample 菜单）的样品控制器。</summary>
    public SampleController NewController(ErpDbContext db)
    {
        var http = new DefaultHttpContext();
        http.User = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.NameIdentifier, _authorizedUserId.ToString()) }, "Test"));
        http.Request.Path = "/api/crm/samples";
        var controller = new SampleController(new GenericService<Sample>(db), db);
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        return controller;
    }

    private DbContextOptions<ErpDbContext> BuildOptions()
        => new DbContextOptionsBuilder<ErpDbContext>().UseSqlServer(ConnectionString).Options;

    private static string BuildDefaultConnectionString()
        => $"Server=(localdb)\\{InstanceMarker};Initial Catalog={DefaultDatabaseName};" +
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

    private async Task InitialiseFreshDatabaseAsync()
    {
        var database = new SqlConnectionStringBuilder(ConnectionString).InitialCatalog;

        // 破坏性初始化前再次护栏：绝不使用生产回退。
        AssertDedicatedTarget(ConnectionString);

        var master = new SqlConnectionStringBuilder(ConnectionString) { InitialCatalog = "master" };
        await using (var conn = new SqlConnection(master.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            // 绝不销毁已存在的夹具库或其它调用方的数据库。
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

        Console.WriteLine("[ERP-532] 集成场景就绪：全新 GUID 库 + 完整 NEWERP 结构 + 种子数据（不清理既有行）。");
    }


    /// <summary>播种具备既有「样品管理」菜单授权的真实启用身份 + 一个已知未删除客户（不新增任何权限 / 菜单模型）。</summary>
    private async Task SeedAuthorizedUserAsync()
    {
        await using var db = CreateDbContext();
        var code = $"sb532-{Guid.NewGuid():N}";
        var user = new SysUser
        {
            UserName = code,
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = "ERP532 样品精度测试账号",
            Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        await db.SaveChangesAsync();

        // 系统内置角色 → ERP-097 数据范围不受限（特权），聚焦精度语义；不新增任何菜单 / 权限定义。
        var role = new SysRole { RoleName = code, RoleCode = code, IsSystem = true };
        db.SysRoles.Add(role);
        await db.SaveChangesAsync();

        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        await db.SaveChangesAsync();

        var menu = await db.SysMenus.FirstOrDefaultAsync(
            m => m.MenuCode == SampleAuthorizationRules.RequiredMenuCode && !m.IsDeleted);
        if (menu is null)
        {
            menu = new SysMenu
            {
                MenuCode = SampleAuthorizationRules.RequiredMenuCode,
                MenuName = SampleAuthorizationRules.RequiredMenuText,
                MenuType = MenuType.Menu
            };
            db.SysMenus.Add(menu);
            await db.SaveChangesAsync();
        }
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
        await db.SaveChangesAsync();

        var customer = new BaseCustomer
        {
            CustomerCode = $"SB532-C-{Guid.NewGuid():N}",
            CustomerName = "样品精度集成客户",
            Status = 1,
            CreditStatus = "正常",
            IsDeleted = false
        };
        db.BaseCustomers.Add(customer);
        await db.SaveChangesAsync();

        _authorizedUserId = user.Id;
        AuthorizedCustomerId = customer.Id;
    }
}

/// <summary>目标库护栏 fail-closed 覆盖：非专用目标必须在访问数据库之前被拒绝。</summary>
public sealed class SamplePersistedNumericBoundsTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    [InlineData("Server=(localdb)\\\\NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;User Id=sa;Password=secret")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(
            () => SamplePersistedNumericBoundsSqlServerFixture.AssertDedicatedTarget(connection));
}

