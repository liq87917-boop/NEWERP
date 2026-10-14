using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using ERP.Infrastructure.Export;
using ERP.Infrastructure.Storage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using System.Data;
using System.Data.Common;
using System.Security.Claims;
using Xunit;

namespace ERP.IntegrationTests;

/// <summary>
/// ERP-530 商品资料（<c>api/base/products</c>）价格 / 尺寸 / 重量 / 体积 / 退税率 / 安全库存等**全部**持久化
/// decimal 列「实际存储精度」（<c>DECIMAL(18,4)</c>）的真实 SQL Server 集成测试
/// （专用 localdb <c>(localdb)\NEWERP_AutoAcceptance</c> + 全新 GUID 库）：
/// <list type="number">
/// <item>实际列精度与规则常量同源：EF 模型与真实 <c>INFORMATION_SCHEMA</c> 双向确认 16 个 decimal 列均为
/// <c>decimal(18,4)</c>；</item>
/// <item>合法的 4 位小数（含恰好上限）经真实受权控制器创建后**原样**往返（以 <c>CONVERT(varchar, ...)</c> 逐位
/// 断言，绝不收敛为 2 位）；</item>
/// <item>第 5 位小数 / 量级溢出在写入之前受控拒绝，零 <c>BaseProducts</c> 行；</item>
/// <item>「半进位取整后恰好落到列上限」的过小数位值仍被拒绝（绝不采信取整结果），而恰好上限本身放行；</item>
/// <item>被拒的修改不改变权威行的任何 decimal 字段与审计时间戳；</item>
/// <item>绕过应用层护栏的原始 EF 写入会被真实列静默取整，超容量值被 SQL Server 以算术溢出（8115）拒绝 ——
/// 这正是护栏必须提前受控拒绝的原因。</item>
/// </list>
/// <para>安全口径：目标必须为专用 localdb 实例 <c>NEWERP_AutoAcceptance</c>、库名前缀 <c>NEWERP_AUTOTEST</c> 且
/// <c>Integrated Security</c>；每次运行只创建一个全新 GUID 后缀库，发现同名库已存在立即拒绝，绝不 drop / reset /
/// 复用任何数据库；连接串只来自进程环境变量或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据。</para>
/// </summary>
public sealed class ProductMasterPersistedNumericBoundsSqlServerTests
    : IClassFixture<ProductMasterPersistedNumericBoundsSqlServerFixture>
{
    /// <summary><c>DECIMAL(18,4)</c> 的最大可表示值（与规则常量同源）。</summary>
    private static readonly decimal Max4 = ProductAuthorizationRules.MaxDecimalMagnitude;

    /// <summary>商品全部持久化 decimal 列（与实体 / 建表脚本逐一对应）。</summary>
    private static readonly string[] DecimalColumns =
    {
        nameof(BaseProduct.PurchasePrice),
        nameof(BaseProduct.SalePrice),
        nameof(BaseProduct.CostPrice),
        nameof(BaseProduct.Weight),
        nameof(BaseProduct.Volume),
        nameof(BaseProduct.Length),
        nameof(BaseProduct.Width),
        nameof(BaseProduct.Height),
        nameof(BaseProduct.OuterLength),
        nameof(BaseProduct.OuterWidth),
        nameof(BaseProduct.OuterHeight),
        nameof(BaseProduct.OuterWeight),
        nameof(BaseProduct.VolumeWeight),
        nameof(BaseProduct.RefundRate),
        nameof(BaseProduct.MinStock),
        nameof(BaseProduct.MaxStock),
    };

    private readonly ProductMasterPersistedNumericBoundsSqlServerFixture _fixture;

    public ProductMasterPersistedNumericBoundsSqlServerTests(
        ProductMasterPersistedNumericBoundsSqlServerFixture fixture)
        => _fixture = fixture;

    /// <summary>访问数据库前复核专用目标护栏（实例名 / 库名前缀 / 集成安全）。</summary>
    private void Guard()
        => ProductMasterPersistedNumericBoundsSqlServerFixture.AssertDedicatedTarget(_fixture.ConnectionString);

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

    /// <summary>商品工厂（全部 decimal 列留 0，由用例按需赋值）。</summary>
    private static BaseProduct Product(string code) => new()
    {
        ProductCode = code,
        ProductName = "商品精度集成测试商品",
        Status = ProductAuthorizationRules.EnabledStatus
    };

    // ==================== 1. 实际列精度（EF 模型 + 真实 INFORMATION_SCHEMA） ====================

    [Fact]
    public async Task Actual_column_precision_is_18_4_for_every_product_decimal_column()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();

        var entityType = db.Model.FindEntityType(typeof(BaseProduct));
        Assert.NotNull(entityType);

        Assert.Equal(4, ProductAuthorizationRules.PersistedDecimalScale);
        Assert.Equal(99_999_999_999_999.9999m, Max4);

        foreach (var column in DecimalColumns)
        {
            var mapped = entityType!.FindProperty(column);
            Assert.NotNull(mapped);
            Assert.Equal(18, mapped!.GetPrecision());
            Assert.Equal(ProductAuthorizationRules.PersistedDecimalScale, mapped.GetScale());
            Assert.Equal("decimal(18,4)", mapped.GetColumnType());

            Assert.Equal("decimal(18,4)", await SqlColumnTypeAsync(db, "BaseProducts", column));
        }
    }

    // ==================== 2. 合法 4 位小数（含恰好上限）原样往返（真实列） ====================

    [Fact]
    public async Task Four_decimal_values_round_trip_verbatim_through_every_real_column()
    {
        Guard();
        var code = $"PM530-4DP-{Tag()}";

        await using (var db = _fixture.CreateDbContext())
        {
            var payload = Product(code);
            SetAllDecimals(payload, 1234.5678m);
            var created = Data<BaseProduct>(await _fixture.NewController(db).Create(payload));
            Assert.True(created.Id > 0);
            AssertAllDecimals(created, 1234.5678m);
        }

        await using (var db = _fixture.CreateDbContext())
        {
            var stored = await db.BaseProducts.AsNoTracking().SingleAsync(p => p.ProductCode == code);
            AssertAllDecimals(stored, 1234.5678m);

            var text = await SqlRowTextAsync(db, code);
            Assert.Equal(DecimalColumns.Length, text.Length);
            Assert.All(text, value => Assert.Equal("1234.5678", value));
        }
    }

    [Fact]
    public async Task Exact_four_decimal_maxima_round_trip_verbatim_through_every_real_column()
    {
        Guard();
        var code = $"PM530-MAX-{Tag()}";

        await using (var db = _fixture.CreateDbContext())
        {
            var payload = Product(code);
            SetAllDecimals(payload, Max4);
            Data<BaseProduct>(await _fixture.NewController(db).Create(payload));
        }

        await using (var db = _fixture.CreateDbContext())
        {
            var text = await SqlRowTextAsync(db, code);
            Assert.All(text, value => Assert.Equal("99999999999999.9999", value));
        }
    }

    // ==================== 3. 过小数位 / 量级溢出 / 半进位到上限：写入之前受控拒绝（零行） ====================

    [Fact]
    public async Task Over_scale_and_overflowing_values_are_rejected_with_zero_writes()
    {
        Guard();
        var tag = Tag();
        var before = await CountAsync(_fixture, "BaseProducts");

        await using (var db = _fixture.CreateDbContext())
        {
            var controller = _fixture.NewController(db);

            var fifth = Product($"PM530-5DP-{tag}");
            fifth.Volume = 0.01255m;                       // 第 5 位小数
            var fifthEx = await Assert.ThrowsAsync<BusinessException>(() => controller.Create(fifth));
            Assert.Equal(ErrorCodes.InvalidParameter, fifthEx.Code);
            Assert.Contains(nameof(BaseProduct.Volume), fifthEx.Message);
            Assert.Contains("DECIMAL(18,4)", fifthEx.Message);

            var overflow = Product($"PM530-OVF-{tag}");
            overflow.SalePrice = 100_000_000_000_000m;     // 量级越界（1e14）
            var overflowEx = await Assert.ThrowsAsync<BusinessException>(() => controller.Create(overflow));
            Assert.Equal(ErrorCodes.InvalidParameter, overflowEx.Code);
            Assert.Contains(nameof(BaseProduct.SalePrice), overflowEx.Message);
        }

        Assert.Equal(before, await CountAsync(_fixture, "BaseProducts"));

        // 同一受权控制器随后合法创建成功（拒绝来自护栏而不是授权），且只新增一行。
        await using (var db = _fixture.CreateDbContext())
        {
            var ok = Product($"PM530-OK-{tag}");
            ok.Volume = 0.0125m;
            ok.RefundRate = 13.0555m;
            Data<BaseProduct>(await _fixture.NewController(db).Create(ok));
        }

        Assert.Equal(before + 1, await CountAsync(_fixture, "BaseProducts"));
    }

    [Fact]
    public async Task Half_up_rounding_onto_the_limit_is_rejected_while_the_exact_limit_is_accepted()
    {
        Guard();
        var tag = Tag();
        var before = await CountAsync(_fixture, "BaseProducts");
        var code = $"PM530-LIMT-{tag}";

        await using (var db = _fixture.CreateDbContext())
        {
            var controller = _fixture.NewController(db);

            // 半进位取整后恰好落到 DECIMAL(18,4) 上限（…99985 → …9999）仍被拒绝：
            // 护栏判定的是原值，绝不采信「取整之后才可存储」。
            var ontoLimit = Product($"PM530-HALF1-{tag}");
            ontoLimit.Weight = 99_999_999_999_999.99985m;
            var ontoEx = await Assert.ThrowsAsync<BusinessException>(() => controller.Create(ontoLimit));
            Assert.Equal(ErrorCodes.InvalidParameter, ontoEx.Code);
            Assert.Contains(nameof(BaseProduct.Weight), ontoEx.Message);

            // 半进位取整后越过上限（…99995 → 100_000_000_000_000.0000）同样拒绝。
            var pastLimit = Product($"PM530-HALF2-{tag}");
            pastLimit.Weight = 99_999_999_999_999.99995m;
            var pastEx = await Assert.ThrowsAsync<BusinessException>(() => controller.Create(pastLimit));
            Assert.Equal(ErrorCodes.InvalidParameter, pastEx.Code);

            Assert.Equal(before, await CountAsync(_fixture, "BaseProducts"));

            // 恰好上限本身放行。
            var atLimit = Product(code);
            atLimit.Weight = Max4;
            Data<BaseProduct>(await controller.Create(atLimit));
        }

        await using (var db = _fixture.CreateDbContext())
        {
            var stored = await db.BaseProducts.AsNoTracking().SingleAsync(p => p.ProductCode == code);
            Assert.Equal(Max4, stored.Weight);
            var text = await SqlRowTextAsync(db, code);
            Assert.Equal("99999999999999.9999", text[Array.IndexOf(DecimalColumns, nameof(BaseProduct.Weight))]);
        }

        Assert.Equal(before + 1, await CountAsync(_fixture, "BaseProducts"));
    }

    // ==================== 4. 被拒修改不改变权威行（decimal 字段 / 审计时间戳） ====================

    [Fact]
    public async Task Rejected_update_leaves_the_authoritative_row_unchanged()
    {
        Guard();
        var tag = Tag();
        var code = $"PM530-UPD-{tag}";
        long productId;

        await using (var seed = _fixture.CreateDbContext())
        {
            var payload = Product(code);
            payload.Volume = 0.0125m;
            payload.RefundRate = 13.0555m;
            productId = Data<BaseProduct>(await _fixture.NewController(seed).Create(payload)).Id;
        }

        await using (var db = _fixture.CreateDbContext())
        {
            var before = await db.BaseProducts.AsNoTracking().SingleAsync(p => p.Id == productId);
            var controller = _fixture.NewController(db);

            var overScale = await db.BaseProducts.AsNoTracking().SingleAsync(p => p.Id == productId);
            overScale.Volume = 0.01255m;                     // 第 5 位小数
            var rejected = await Assert.ThrowsAsync<BusinessException>(
                () => controller.Update(productId, overScale));
            Assert.Equal(ErrorCodes.InvalidParameter, rejected.Code);
            Assert.Contains(nameof(BaseProduct.Volume), rejected.Message);

            var overflow = await db.BaseProducts.AsNoTracking().SingleAsync(p => p.Id == productId);
            overflow.SalePrice = Max4 + 0.0001m;             // 量级越界
            var rejectedOverflow = await Assert.ThrowsAsync<BusinessException>(
                () => controller.Update(productId, overflow));
            Assert.Equal(ErrorCodes.InvalidParameter, rejectedOverflow.Code);
            Assert.Contains(nameof(BaseProduct.SalePrice), rejectedOverflow.Message);

            var after = await db.BaseProducts.AsNoTracking().SingleAsync(p => p.Id == productId);
            Assert.Equal(before.Volume, after.Volume);
            Assert.Equal(before.RefundRate, after.RefundRate);
            Assert.Equal(before.SalePrice, after.SalePrice);
            Assert.Equal(before.ProductName, after.ProductName);
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
        var roundedCode = $"PM530-RAW-{Tag()}";
        await using (var db = _fixture.CreateDbContext())
        {
            var raw = Product(roundedCode);
            raw.Volume = 0.01255m;
            db.BaseProducts.Add(raw);
            await db.SaveChangesAsync();
        }

        await using (var db = _fixture.CreateDbContext())
        {
            var stored = await db.BaseProducts.AsNoTracking().SingleAsync(p => p.ProductCode == roundedCode);
            Assert.NotEqual(0.01255m, stored.Volume);
            Assert.Equal(0.0126m, stored.Volume);          // 0.01255 → 0.0126：原值并未原样存储
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

    private static void SetAllDecimals(BaseProduct product, decimal value)
    {
        product.PurchasePrice = value;
        product.SalePrice = value;
        product.CostPrice = value;
        product.Weight = value;
        product.Volume = value;
        product.Length = value;
        product.Width = value;
        product.Height = value;
        product.OuterLength = value;
        product.OuterWidth = value;
        product.OuterHeight = value;
        product.OuterWeight = value;
        product.VolumeWeight = value;
        product.RefundRate = value;
        product.MinStock = value;
        product.MaxStock = value;
    }

    private static decimal[] GetAllDecimals(BaseProduct product) => new[]
    {
        product.PurchasePrice,
        product.SalePrice,
        product.CostPrice,
        product.Weight,
        product.Volume,
        product.Length,
        product.Width,
        product.Height,
        product.OuterLength,
        product.OuterWidth,
        product.OuterHeight,
        product.OuterWeight,
        product.VolumeWeight,
        product.RefundRate,
        product.MinStock,
        product.MaxStock,
    };

    private static void AssertAllDecimals(BaseProduct product, decimal expected)
        => Assert.All(GetAllDecimals(product), value => Assert.Equal(expected, value));

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

    /// <summary>按真实列顺序读取一行全部 decimal 列的 <c>CONVERT(varchar, ...)</c> 文本（逐位原样证据）。</summary>
    private static async Task<string[]> SqlRowTextAsync(ErpDbContext db, string productCode)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open) await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        var select = string.Join(", ", DecimalColumns.Select(c => $"CONVERT(varchar(50), [{c}])"));
        command.CommandText = $"SELECT {select} FROM db_owner.BaseProducts WHERE ProductCode = @code";
        AddParameter(command, "@code", productCode);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        var result = new string[DecimalColumns.Length];
        for (var i = 0; i < result.Length; i++) result[i] = reader.GetString(i);
        return result;
    }

    private static async Task<long> CountAsync(
        ProductMasterPersistedNumericBoundsSqlServerFixture fixture, string table)
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
/// ERP-530 专用 localdb 目标 Fixture：每次运行创建一个全新 GUID 后缀库并初始化为完整 NEWERP 结构 + 种子数据，
/// 再播种一个具备既有「商品资料」菜单授权的真实启用身份，供真实 <see cref="ProductController"/> 使用。
/// <para>任何库访问 / 建库之前先过专用目标护栏（实例 <c>NEWERP_AutoAcceptance</c> / 库名前缀 <c>NEWERP_AUTOTEST</c> /
/// 集成安全），发现同名库已存在立即拒绝，绝不 drop / reset / 复用任何数据库。</para>
/// </summary>
public sealed class ProductMasterPersistedNumericBoundsSqlServerFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";

    /// <summary>本次运行新建的 GUID 独占库名（每次运行唯一，绝不复用既有库）。</summary>
    public static string DefaultDatabaseName { get; } =
        $"{DatabasePrefix}_PRODUCTMASTERPREC_{Guid.NewGuid():N}";

    private long _authorizedUserId;

    public string ConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = BuildDefaultConnectionString();

        // 访问数据库之前先复核目标护栏（错误目标 fail closed）。
        AssertDedicatedTarget(connectionString);
        ConnectionString = connectionString;

        Console.WriteLine($"[ERP-530] 目标库护栏放行（实例 {InstanceMarker}，库名前缀 {DatabasePrefix}，集成安全）。");

        await InitialiseFreshDatabaseAsync();
        await SeedAuthorizedUserAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public ErpDbContext CreateDbContext() => new(BuildOptions());

    /// <summary>绑定已播种真实启用身份（具备既有 product 菜单）的商品控制器。</summary>
    public ProductController NewController(ErpDbContext db)
    {
        var http = new DefaultHttpContext();
        http.User = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.NameIdentifier, _authorizedUserId.ToString()) }, "Test"));
        http.Request.Path = "/api/base/products";
        var controller = new ProductController(
            new GenericService<BaseProduct>(db), new OssStorageService(BuildOssConfiguration()),
            new ProductExcelExporter(db), new FakeWebHostEnvironment(), db);
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

        Console.WriteLine("[ERP-530] 集成场景就绪：全新 GUID 库 + 完整 NEWERP 结构 + 种子数据（不清理既有行）。");
    }

    /// <summary>播种具备既有「商品资料」菜单授权的真实启用身份（不新增任何权限 / 菜单模型）。</summary>
    private async Task SeedAuthorizedUserAsync()
    {
        await using var db = CreateDbContext();
        var code = $"pm530-{Guid.NewGuid():N}";
        var user = new SysUser
        {
            UserName = code,
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = "ERP530 商品精度测试账号",
            Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        await db.SaveChangesAsync();

        var role = new SysRole { RoleName = code, RoleCode = code, IsSystem = false };
        db.SysRoles.Add(role);
        await db.SaveChangesAsync();

        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        await db.SaveChangesAsync();

        var menu = await db.SysMenus.FirstOrDefaultAsync(
            m => m.MenuCode == ProductAuthorizationRules.RequiredMenuCode && !m.IsDeleted);
        if (menu is null)
        {
            menu = new SysMenu
            {
                MenuCode = ProductAuthorizationRules.RequiredMenuCode,
                MenuName = ProductAuthorizationRules.RequiredMenuText,
                MenuType = MenuType.Menu
            };
            db.SysMenus.Add(menu);
            await db.SaveChangesAsync();
        }
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
        await db.SaveChangesAsync();

        _authorizedUserId = user.Id;
    }

    private static IConfiguration BuildOssConfiguration() =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Oss:AccessKeyId"] = "test-key-id",
            ["Oss:AccessKeySecret"] = "test-secret",
            ["Oss:Bucket"] = "test-bucket",
            ["Oss:Endpoint"] = "oss-cn-hangzhou.aliyuncs.com"
        }).Build();

    /// <summary>测试用宿主环境：内容根指向当前目录（导出用例不触达真实文件 / OSS）。</summary>
    private sealed class FakeWebHostEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "ERP.IntegrationTests";
        public string EnvironmentName { get; set; } = "Development";
        public string ContentRootPath { get; set; } = Directory.GetCurrentDirectory();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = Directory.GetCurrentDirectory();
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }
}

/// <summary>目标库护栏 fail-closed 覆盖：非专用目标必须在访问数据库之前被拒绝。</summary>
public sealed class ProductMasterPersistedNumericBoundsTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;User Id=sa;Password=secret")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(
            () => ProductMasterPersistedNumericBoundsSqlServerFixture.AssertDedicatedTarget(connection));
}






