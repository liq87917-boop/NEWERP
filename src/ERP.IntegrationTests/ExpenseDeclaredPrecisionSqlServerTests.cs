using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using System.Data;
using System.Data.Common;
using System.Security.Claims;
using Xunit;

namespace ERP.IntegrationTests;

/// <summary>
/// ERP-538 费用单（<c>api/finance/expenses</c>）标量**声明列精度**的真实 SQL Server 集成测试
/// （NEWERP_AUTOTEST 护栏）：用与运行时完全相同的调用路径（真实控制器 + 真实 HTTP 身份 + 真实 SQL Server 上下文）验证：
/// <list type="number">
/// <item>真实列精度：<c>FinanceExpenses</c> 的金额 / 折人民币金额 / 税率 / 税额 / 分摊比例 / 分摊金额列为
/// <c>decimal(18,4)</c>、汇率列为 <c>decimal(18,6)</c>（EF 模型与真实 <c>INFORMATION_SCHEMA</c> 双向确认）；</item>
/// <item>逐位原样往返：4 位小数金额 / 折人民币金额 / 税率 / 税额 / 分摊比例 / 分摊金额与 6 位小数汇率
/// （含各自声明列上限）经真实控制器落库后以 <c>CONVERT(varchar, ...)</c> 逐位断言；</item>
/// <item>写入护栏：第 5 位小数（金额类）/ 第 7 位小数（汇率）、量级越界与负数在**任何行写入之前**以既有受控错误拒绝，
/// 且被拒请求零 <c>FinanceExpenses</c> 行（同一受权控制器随后合法创建成功）；</item>
/// <item>绕过护栏的原始 EF 写入：真实列无法承载第 5 位小数（被取整 / 截断为 4 位），超容量值被真实列 / 驱动拒绝 ——
/// 证明应用层守卫确有必要，且真实列确实是 18,4 / 18,6。</item>
/// </list>
/// <para>安全口径：目标必须为专用 localdb 实例 <c>NEWERP_AutoAcceptance</c>、库名前缀 <c>NEWERP_AUTOTEST</c> 且
/// <c>Integrated Security</c>；每次运行只创建一个全新 GUID 后缀库，发现同名库已存在立即拒绝，绝不 drop / reset /
/// 复用任何数据库；连接串只来自进程环境变量或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据。</para>
/// <para>构建完成不等于阶段验收：本文件只有在受控 localdb 上真实执行通过才算验收证据。</para>
/// </summary>
public sealed class ExpenseDeclaredPrecisionSqlServerTests
    : IClassFixture<ExpenseDeclaredPrecisionSqlServerFixture>
{
    private const decimal Max4 = 99_999_999_999_999.9999m;      // DECIMAL(18,4) 声明列上限
    private const decimal Max6 = 999_999_999_999.999999m;       // DECIMAL(18,6) 声明列上限
    private const decimal OneE16 = 10_000_000_000_000_000m;     // 1e16（超出 18,4 上限）

    private readonly ExpenseDeclaredPrecisionSqlServerFixture _fixture;

    public ExpenseDeclaredPrecisionSqlServerTests(ExpenseDeclaredPrecisionSqlServerFixture fixture)
        => _fixture = fixture;

    /// <summary>访问数据库前复核专用目标护栏（实例名 / 库名前缀 / 集成安全）。</summary>
    private void Guard()
    {
        var target = new SqlConnectionStringBuilder(_fixture.ConnectionString);
        Assert.Equal("(localdb)\\NEWERP_AutoAcceptance", target.DataSource, ignoreCase: true);
        Assert.StartsWith(ExpenseDeclaredPrecisionSqlServerFixture.DatabasePrefix, target.InitialCatalog,
            StringComparison.OrdinalIgnoreCase);
        Assert.True(target.IntegratedSecurity);
    }

    private static string Tag() => Guid.NewGuid().ToString("N")[..8];

    // ==================== 1. 真实列精度：EF 模型 + INFORMATION_SCHEMA 双向确认 ====================

    [Fact]
    public async Task Declared_precision_columns_match_ef_model_and_information_schema()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();

        Assert.Equal(18, FinancePersistedAmountRules.DecimalPrecision);
        Assert.Equal(4, FinancePersistedAmountRules.DecimalScale4);
        Assert.Equal(6, FinancePersistedAmountRules.DecimalScale6);
        Assert.Equal(4, ExpenseAuthorizationRules.PersistedAmountScale);
        Assert.Equal(6, ExpenseAuthorizationRules.PersistedExchangeRateScale);

        // EF 模型（运行时映射）与真实列（INFORMATION_SCHEMA）逐列同源。
        foreach (var column in new[]
        {
            nameof(FinanceExpense.Amount),
            nameof(FinanceExpense.AmountCny),
            nameof(FinanceExpense.TaxRate),
            nameof(FinanceExpense.TaxAmount),
            nameof(FinanceExpense.AllocationRatio),
            nameof(FinanceExpense.AllocatedAmount)
        })
        {
            Assert.Equal("decimal(18,4)", ModelColumnType(typeof(FinanceExpense), column));
            Assert.Equal("decimal(18,4)", await SqlColumnTypeAsync(db, "FinanceExpenses", column));
        }
        Assert.Equal("decimal(18,6)", ModelColumnType(typeof(FinanceExpense), nameof(FinanceExpense.ExchangeRate)));
        Assert.Equal("decimal(18,6)", await SqlColumnTypeAsync(db, "FinanceExpenses", "ExchangeRate"));
    }

    /// <summary>SQL Server 提供程序**仅构建模型**（不打开连接）解析出的列类型。</summary>
    private static string ModelColumnType(Type entityType, string propertyName)
    {
        var options = new DbContextOptionsBuilder<ErpDbContext>()
            .UseSqlServer(
                "Server=(localdb)\\NEWERP_AutoAcceptance;"
                + "Database=NEWERP_AUTOTEST_ERP538_MODEL_ONLY;Integrated Security=true;TrustServerCertificate=true")
            .Options;
        using var db = new ErpDbContext(options);
        return db.Model.FindEntityType(entityType)!.FindProperty(propertyName)!.GetColumnType()!;
    }

    /// <summary>真实 SQL Server 列类型（<c>INFORMATION_SCHEMA.COLUMNS</c>）。</summary>
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

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    // ==================== 2. 逐位原样往返（真实列） ====================

    [Fact]
    public async Task Declared_precision_scalars_round_trip_verbatim_through_real_controller()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var customer = await SeedCustomerAsync(db, "声明精度往返客户");
        var userId = await SeedUserAsync(db, ExpenseAuthorizationRules.RequiredMenuCode, "声明精度操作员");
        var controller = NewExpenseController(db, userId);

        var entity = NewExpense(customer.Id, $"RT-{Tag()}");
        entity.Amount = 1234.5678m;
        entity.Currency = "USD";
        entity.ExchangeRate = 7.123456m;
        entity.AmountCny = 8765.4321m;
        entity.TaxRate = 0.0001m;
        entity.TaxAmount = 13.0555m;
        entity.AllocationRatio = 33.3333m;
        entity.AllocatedAmount = 0.0125m;
        var created = AssertOk<FinanceExpense>(await controller.Create(entity));

        await using (var verify = _fixture.CreateDbContext())
        {
            // 真实列逐位原样（CONVERT(varchar) 精确断言，绝不用近似比较 / 容忍取整）。
            Assert.Equal("1234.5678", await AmountTextAsync(verify, "Amount", created.Id));
            Assert.Equal("8765.4321", await AmountTextAsync(verify, "AmountCny", created.Id));
            Assert.Equal("7.123456", await AmountTextAsync(verify, "ExchangeRate", created.Id));
            Assert.Equal("0.0001", await AmountTextAsync(verify, "TaxRate", created.Id));
            Assert.Equal("13.0555", await AmountTextAsync(verify, "TaxAmount", created.Id));
            Assert.Equal("33.3333", await AmountTextAsync(verify, "AllocationRatio", created.Id));
            Assert.Equal("0.0125", await AmountTextAsync(verify, "AllocatedAmount", created.Id));
        }

        // 声明列上限（金额类 18,4 / 汇率 18,6）同样逐位原样。
        var maxEntity = NewExpense(customer.Id, $"RTMAX-{Tag()}");
        maxEntity.Amount = Max4;
        maxEntity.AmountCny = Max4;
        maxEntity.TaxAmount = Max4;
        maxEntity.AllocationRatio = Max4;
        maxEntity.AllocatedAmount = Max4;
        maxEntity.Currency = "USD";
        maxEntity.ExchangeRate = Max6;
        var maxCreated = AssertOk<FinanceExpense>(await controller.Create(maxEntity));

        await using (var verifyMax = _fixture.CreateDbContext())
        {
            Assert.Equal("99999999999999.9999", await AmountTextAsync(verifyMax, "Amount", maxCreated.Id));
            Assert.Equal("99999999999999.9999", await AmountTextAsync(verifyMax, "AmountCny", maxCreated.Id));
            Assert.Equal("99999999999999.9999", await AmountTextAsync(verifyMax, "TaxAmount", maxCreated.Id));
            Assert.Equal("99999999999999.9999", await AmountTextAsync(verifyMax, "AllocationRatio", maxCreated.Id));
            Assert.Equal("99999999999999.9999", await AmountTextAsync(verifyMax, "AllocatedAmount", maxCreated.Id));
            Assert.Equal("999999999999.999999", await AmountTextAsync(verifyMax, "ExchangeRate", maxCreated.Id));
        }
    }

    /// <summary>按列名读取真实列上的十进制文本（列名只来自本文件内的字面量调用点）。</summary>
    private static Task<string> AmountTextAsync(ErpDbContext db, string column, long id) => column switch
    {
        "Amount" => ScalarTextAsync(db,
            "SELECT CONVERT(varchar(60), Amount) AS [Value] FROM db_owner.FinanceExpenses WHERE Id = {0}", id),
        "AmountCny" => ScalarTextAsync(db,
            "SELECT CONVERT(varchar(60), AmountCny) AS [Value] FROM db_owner.FinanceExpenses WHERE Id = {0}", id),
        "ExchangeRate" => ScalarTextAsync(db,
            "SELECT CONVERT(varchar(60), ExchangeRate) AS [Value] FROM db_owner.FinanceExpenses WHERE Id = {0}", id),
        "TaxRate" => ScalarTextAsync(db,
            "SELECT CONVERT(varchar(60), TaxRate) AS [Value] FROM db_owner.FinanceExpenses WHERE Id = {0}", id),
        "TaxAmount" => ScalarTextAsync(db,
            "SELECT CONVERT(varchar(60), TaxAmount) AS [Value] FROM db_owner.FinanceExpenses WHERE Id = {0}", id),
        "AllocationRatio" => ScalarTextAsync(db,
            "SELECT CONVERT(varchar(60), AllocationRatio) AS [Value] FROM db_owner.FinanceExpenses WHERE Id = {0}", id),
        "AllocatedAmount" => ScalarTextAsync(db,
            "SELECT CONVERT(varchar(60), AllocatedAmount) AS [Value] FROM db_owner.FinanceExpenses WHERE Id = {0}", id),
        _ => throw new ArgumentOutOfRangeException(nameof(column), column, "未知的费用单标量列")
    };

    private static async Task<string> ScalarTextAsync(ErpDbContext db, string sql, long id)
        => (await db.Database.SqlQueryRaw<string>(sql, id).ToListAsync()).Single();


    // ==================== 3. 写入护栏：过精度 / 量级越界 / 负数拒绝且零行 ====================

    [Theory]
    [InlineData("amount-over-scale")]
    [InlineData("amount-magnitude")]
    [InlineData("amount-negative")]
    [InlineData("rate-over-scale")]
    [InlineData("rate-magnitude")]
    [InlineData("allocationratio-over-scale")]
    [InlineData("taxamount-negative")]
    public async Task Create_rejects_unpersistable_scalars_without_any_row(string scenario)
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var customer = await SeedCustomerAsync(db, "声明精度拒绝客户");
        var userId = await SeedUserAsync(db, ExpenseAuthorizationRules.RequiredMenuCode, "声明精度拒绝操作员");
        var controller = NewExpenseController(db, userId);

        var entity = NewExpense(customer.Id, $"BAD-{Tag()}");
        switch (scenario)
        {
            case "amount-over-scale":
                entity.Amount = 1.00005m;               // 第 5 位小数
                break;
            case "amount-magnitude":
                entity.Amount = OneE16;
                break;
            case "amount-negative":
                entity.Amount = -1m;
                break;
            case "rate-over-scale":
                entity.ExchangeRate = 1.0000005m;       // 第 7 位小数
                break;
            case "rate-magnitude":
                entity.ExchangeRate = OneE16;
                break;
            case "allocationratio-over-scale":
                entity.AllocationRatio = 1.00005m;      // 第 5 位小数
                break;
            default:
                entity.TaxAmount = -0.0001m;
                break;
        }

        var ex = await Assert.ThrowsAsync<BusinessException>(() => controller.Create(entity));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);

        // 被拒请求零写入（真实库行数不变）。
        await using (var verify = _fixture.CreateDbContext())
        {
            Assert.Equal(0, await verify.FinanceExpenses.CountAsync(x => x.CustomerId == customer.Id));
        }

        // 同一受权控制器随后合法创建成功（证明拒绝原因是标量精度而不是授权）。
        var created = AssertOk<FinanceExpense>(await controller.Create(NewExpense(customer.Id, $"OK-{Tag()}")));
        Assert.True(created.Id > 0);
    }

    // ==================== 4. 绕过护栏的原始 EF 写入：真实列才是最后一道边界 ====================

    [Fact]
    public async Task Raw_ef_write_bypassing_guard_is_rounded_or_rejected_by_real_columns()
    {
        Guard();

        // 绕过应用层护栏的原始 EF 写入：真实列（18,4）无法承载第 5 位小数 —— 会被取整 / 截断为 4 位，
        // 这正是写入护栏（逐位原样判定）必须存在的理由。
        await using var db = _fixture.CreateDbContext();
        var customer = await SeedCustomerAsync(db, "绕过护栏客户");
        var raw = NewExpense(customer.Id, $"RAW-{Tag()}");
        raw.Amount = 1.00005m;
        raw.AmountCny = 1.00005m;
        raw.TaxAmount = 1.00005m;
        db.FinanceExpenses.Add(raw);
        await db.SaveChangesAsync();

        await using (var verify = _fixture.CreateDbContext())
        {
            var stored = await verify.FinanceExpenses.AsNoTracking().SingleAsync(x => x.Id == raw.Id);
            Assert.NotEqual(1.00005m, stored.Amount);
            Assert.Equal(decimal.Round(stored.Amount, 4), stored.Amount);
            Assert.NotEqual(1.00005m, stored.AmountCny);
            Assert.Equal(decimal.Round(stored.AmountCny, 4), stored.AmountCny);
            Assert.NotEqual(1.00005m, stored.TaxAmount);
            Assert.Equal(decimal.Round(stored.TaxAmount, 4), stored.TaxAmount);
        }

        // 超出声明列容量的原始写入被真实列 / 驱动拒绝（绝非静默写入）。
        await using (var over = _fixture.CreateDbContext())
        {
            var bad = NewExpense(customer.Id, $"RAW-BAD-{Tag()}");
            bad.Amount = OneE16;
            bad.AmountCny = OneE16;
            over.FinanceExpenses.Add(bad);
            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => over.SaveChangesAsync());
            AssertProviderRejected(ex);
        }

        // 6 位小数汇率由真实列原样保存（证明汇率列确为 DECIMAL(18,6)）。
        await using (var rate = _fixture.CreateDbContext())
        {
            var rateRow = NewExpense(customer.Id, $"RAW-RATE-{Tag()}");
            rateRow.Currency = "USD";
            rateRow.ExchangeRate = 1.0000005m;
            rate.FinanceExpenses.Add(rateRow);
            await rate.SaveChangesAsync();

            var storedRate = await AmountTextAsync(rate, "ExchangeRate", rateRow.Id);
            Assert.Equal("1.000001", storedRate);      // 真实列保留 6 位小数（第 7 位被收敛）
        }
    }

    /// <summary>
    /// 异常链中必须出现提供程序层面的拒绝：SQL Server 算术溢出（<see cref="SqlException"/>）或
    /// SqlClient 客户端参数范围校验（<see cref="ArgumentException"/>）；绝不能是应用层提前拦截。
    /// </summary>
    private static void AssertProviderRejected(Exception exception)
    {
        var chain = new List<string>();
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            chain.Add($"{current.GetType().Name}: {current.Message}");
            if (current is SqlException or ArgumentException) return;
        }
        Assert.Fail("expected the provider to reject the over-limit scalar amount: " + string.Join(" -> ", chain));
    }


    // ==================== 5. 真实 SQL 夹具种子与脚手架 ====================

    private static FinanceExpense NewExpense(long? customerId, string expenseNo)
        => new()
        {
            ExpenseNo = expenseNo,
            ExpenseDate = DateTime.Today,
            ExpenseType = "报关费",
            Amount = 100m,
            Currency = "CNY",
            ExchangeRate = 1m,
            AmountCny = 100m,
            TaxRate = 0m,
            TaxAmount = 0m,
            AllocationRatio = 0m,
            AllocatedAmount = 0m,
            CustomerId = customerId,
            RefType = "客户",
            RefNo = expenseNo,
            AllocationBase = "不分摊",
            PaymentStatus = "未付"
        };

    private static ExpenseBillController NewExpenseController(ErpDbContext db, long? userId)
    {
        var controller = new ExpenseBillController(new GenericService<FinanceExpense>(db), db);
        SetUser(controller, userId);
        return controller;
    }

    private static void SetUser(ControllerBase controller, long? userId)
    {
        var claims = userId.HasValue
            ? new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }
            : Array.Empty<Claim>();
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) },
            ActionDescriptor = new ControllerActionDescriptor()
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

    private static async Task<BaseCustomer> SeedCustomerAsync(ErpDbContext db, string name)
    {
        var customer = new BaseCustomer
        {
            CustomerCode = $"C-{Tag()}",
            CustomerName = name,
            Status = 1,
            CreditStatus = "正常",
            CreditLimit = 100000m,
            CreditDays = 30
        };
        db.BaseCustomers.Add(customer);
        await db.SaveChangesAsync();
        return customer;
    }

    /// <summary>播种真实授权账号：既有「费用单」（expense-bill）菜单授权 + 启用账号（系统内置角色 → 不受客户数据范围限制）。</summary>
    private static async Task<long> SeedUserAsync(ErpDbContext db, string menuCode, string displayName)
    {
        var role = new SysRole { RoleCode = $"EXPPREC-{Tag()}", RoleName = displayName, IsSystem = true };
        db.SysRoles.Add(role);
        await db.SaveChangesAsync();

        var user = new SysUser
        {
            UserName = $"expprec-{Tag()}",
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = displayName,
            Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        await db.SaveChangesAsync();

        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        await db.SaveChangesAsync();

        var menu = await db.SysMenus.FirstOrDefaultAsync(m => m.MenuCode == menuCode && !m.IsDeleted)
            ?? throw new InvalidOperationException($"seed menu {menuCode} is missing");
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
        await db.SaveChangesAsync();
        return user.Id;
    }

    // ==================== 6. 专用 localdb 目标安全护栏（fail-closed，无生产回退） ====================

    /// <summary>目标库护栏单元级校验：非专用目标必须在访问数据库之前被拒绝。</summary>
    public sealed class TargetGuardTests
    {
        [Theory]
        [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
        [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
        [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
        [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;User Id=sa")]
        public void Rejects_non_dedicated_targets_before_database_access(string connection)
            => Assert.ThrowsAny<Exception>(() =>
                ExpenseDeclaredPrecisionSqlServerFixture.AssertDedicatedTarget(connection));
    }
}

/// <summary>
/// 专用 localdb 目标 Fixture（ERP-538）：每次运行创建一个全新 GUID 后缀库并初始化为完整 NEWERP 结构 +
/// 种子数据，供真实 SQL Server 集成测试复用。
/// <para>每次运行只创建一个全新 GUID 后缀库；发现同名库已存在立即拒绝；绝不 drop / reset / 复用任何数据库。</para>
/// </summary>
public sealed class ExpenseDeclaredPrecisionSqlServerFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";
    public const string DefaultDatabaseName = "NEWERP_AUTOTEST_EXPENSEPRECISION_20261014";

    public string ConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = BuildDefaultConnectionString();

        AssertDedicatedTarget(connectionString);
        ConnectionString = connectionString;

        Console.WriteLine($"[ERP-538] 目标库护栏放行（实例含 {InstanceMarker}，库名前缀 {DatabasePrefix}）。");

        await CreateFreshDatabaseAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public ErpDbContext CreateDbContext() => new(BuildOptions());

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

        // 破坏性重置前再次护栏：绝不使用生产回退。
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

        Console.WriteLine("[ERP-538] 集成场景就绪：完整 NEWERP 结构 + 种子数据。");
    }
}

