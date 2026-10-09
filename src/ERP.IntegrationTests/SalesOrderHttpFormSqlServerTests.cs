using Microsoft.Data.SqlClient;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Xunit;

namespace ERP.IntegrationTests;

/// <summary>
/// ERP-428 手工销售订单录入的<b>真实 HTTP 表单</b>回归（真实 ERP.Api 进程 + 全新 GUID <c>NEWERP_AUTOTEST</c> 库）：
/// <list type="number">
/// <item><b>真实 HTTP</b>：请求经真实 Kestrel 管线（MVC 绑定 + <c>[ApiController]</c> 模型校验 + JWT 认证 + 既有授权），
/// 因此「省略 / 伪造订单号」「可选目的港留空为 null」由<b>绑定层</b>证明；直接实例化控制器的测试无法证明这一点
/// （修复前正是绑定层返回 HTTP 400 <c>{"errors":{"OrderNo":["The OrderNo field is required."]}}</c>）。</item>
/// <item><b>权威单号</b>：省略订单号保存成功后，经直接 SQL 复核持久化单号由服务端生成；伪造订单号不进入持久化。</item>
/// <item><b>既有护栏不变</b>：目的港 0 / 不存在引用仍被拒绝且零写入；无身份仍 401、无菜单账号仍 403，且都零写入。</item>
/// </list>
/// <para>安全口径：目标必须为专用 localdb 实例 <c>(localdb)\NEWERP_AutoAcceptance</c>、库名前缀 <c>NEWERP_AUTOTEST</c>
/// 且集成安全；每次运行只使用<b>全新 GUID 后缀库</b>，发现同名库已存在立即拒绝；绝不 drop / reset / 复用任何数据库，
/// 连接串只来自进程环境变量或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据。
/// <b>构建完成不等于阶段验收</b>：只有下列场景在专用 localdb 上真实执行通过才算验收证据。</para>
/// </summary>
public sealed class SalesOrderHttpFormSqlServerTests : IClassFixture<SalesOrderHttpFormSqlServerFixture>
{
    private readonly SalesOrderHttpFormSqlServerFixture _fx;

    public SalesOrderHttpFormSqlServerTests(SalesOrderHttpFormSqlServerFixture fixture) => _fx = fixture;

    /// <summary>访问数据库前复核专用目标护栏（实例名 / 库名前缀 / 集成安全）。</summary>
    private void Guard()
    {
        var target = new SqlConnectionStringBuilder(_fx.ConnectionString);
        Assert.Equal($"(localdb)\\{SalesOrderHttpFormSqlServerFixture.InstanceMarker}", target.DataSource, ignoreCase: true);
        Assert.StartsWith(SalesOrderHttpFormSqlServerFixture.DatabasePrefix, target.InitialCatalog,
            StringComparison.OrdinalIgnoreCase);
        Assert.True(target.IntegratedSecurity);
    }

    private static string Tag() => Guid.NewGuid().ToString("N")[..8];

    // ==================== HTTP 脚手架 ====================

    private static async Task<(HttpStatusCode Status, JsonElement Root, string Raw)> PostOrderAsync(
        HttpClient client, object body)
    {
        var payload = JsonSerializer.Serialize(body);
        using var response = await client.PostAsync("/api/sales-orders",
            new StringContent(payload, Encoding.UTF8, "application/json"));
        var raw = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(raw);
        return (response.StatusCode, doc.RootElement.Clone(), raw);
    }

    private static async Task<long> CreateCustomerAsync(HttpClient client, string tag)
    {
        var payload = JsonSerializer.Serialize(new { customerCode = "C-HTTP428-" + tag, customerName = tag, currency = "USD" });
        using var response = await client.PostAsync("/api/base/customers",
            new StringContent(payload, Encoding.UTF8, "application/json"));
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(0, doc.RootElement.GetProperty("code").GetInt32());
        return doc.RootElement.GetProperty("data").GetProperty("id").GetInt64();
    }

    private static async Task<long> CreateProductAsync(HttpClient client, string tag)
    {
        var payload = JsonSerializer.Serialize(new { productCode = "P-HTTP428-" + tag, productName = tag + "-商品A", unit = "PCS" });
        using var response = await client.PostAsync("/api/base/products",
            new StringContent(payload, Encoding.UTF8, "application/json"));
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(0, doc.RootElement.GetProperty("code").GetInt32());
        return doc.RootElement.GetProperty("data").GetProperty("id").GetInt64();
    }

    /// <summary>手工录入正文：与前端 crud.js 提交同形（可省略订单号、可选目的港留空 / null / 非法引用）。</summary>
    private static object OrderBody(long customerId, long productId, string customerPo,
        string? orderNo = null, long? portId = null, bool includePortId = false)
        => new
        {
            orderNo,
            orderDate = DateTime.Today.ToString("yyyy-MM-dd"),
            customerId,
            currency = "USD",
            exchangeRate = 7.2m,
            depositRatio = 30m,
            customerPoNo = customerPo,
            contractNo = "SC-HTTP428-" + customerPo,
            tradeTerms = "FOB",
            destinationPort = "HAMBURG",
            portId = includePortId ? portId : null,
            details = new object[]
            {
                new { productId, productName = "HTTP 商品A", spec = "大", unit = "PCS", quantity = 10m, unitPrice = 100m }
            }
        };

    // ==================== 1. 省略 / 伪造订单号（真实 HTTP） ====================

    [Fact]
    public async Task 省略订单号_真实HTTP保存成功_服务端权威单号落库()
    {
        Guard();
        var tag = Tag();
        using var admin = await _fx.LoginAsAdminAsync();
        var customerId = await CreateCustomerAsync(admin, tag);
        var productId = await CreateProductAsync(admin, tag);
        var customerPo = "HTTP428-OMIT-" + tag;

        var (status, root, raw) = await PostOrderAsync(admin, OrderBody(customerId, productId, customerPo));

        // 修复前：绑定层因 [Required] 返回 HTTP 400（OrderNo field is required）；现在必须成功。
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(0, root.GetProperty("code").GetInt32());
        Assert.DoesNotContain("OrderNo field is required", raw, StringComparison.OrdinalIgnoreCase);

        var persisted = await _fx.ReadPersistedAsync(customerPo);
        Assert.Equal(1, persisted.Count);
        Assert.StartsWith("SO", persisted.OrderNo, StringComparison.Ordinal);
        Assert.True(persisted.OrderNo!.Length is > 0 and <= 50);
        Assert.Equal(root.GetProperty("data").GetProperty("orderNo").GetString(), persisted.OrderNo);
    }

    [Fact]
    public async Task 伪造订单号_真实HTTP_持久化号码由服务端权威生成()
    {
        Guard();
        var tag = Tag();
        using var admin = await _fx.LoginAsAdminAsync();
        var customerId = await CreateCustomerAsync(admin, tag);
        var productId = await CreateProductAsync(admin, tag);
        var customerPo = "HTTP428-FORGE-" + tag;
        var forged = "HACK-HTTP428-" + tag;

        var (status, root, _) = await PostOrderAsync(admin,
            OrderBody(customerId, productId, customerPo, orderNo: forged));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(0, root.GetProperty("code").GetInt32());

        var persisted = await _fx.ReadPersistedAsync(customerPo);
        Assert.Equal(1, persisted.Count);
        Assert.StartsWith("SO", persisted.OrderNo, StringComparison.Ordinal);
        Assert.NotEqual(forged, persisted.OrderNo);
    }

    // ==================== 2. 可选目的港留空（真实 HTTP） ====================

    [Fact]
    public async Task 可选目的港_留空为null_真实HTTP保存并持久化为NULL()
    {
        Guard();
        var tag = Tag();
        using var admin = await _fx.LoginAsAdminAsync();
        var customerId = await CreateCustomerAsync(admin, tag);
        var productId = await CreateProductAsync(admin, tag);
        var customerPo = "HTTP428-PORTNULL-" + tag;

        // includePortId=true 且 portId=null：与前端「留空提交 null」完全一致的正文。
        var (status, root, _) = await PostOrderAsync(admin,
            OrderBody(customerId, productId, customerPo, portId: null, includePortId: true));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(0, root.GetProperty("code").GetInt32());

        var persisted = await _fx.ReadPersistedAsync(customerPo);
        Assert.Equal(1, persisted.Count);
        Assert.Null(persisted.PortId);
        Assert.StartsWith("SO", persisted.OrderNo, StringComparison.Ordinal);
    }

    // ==================== 3. 可控非法引用（真实 HTTP，零写入） ====================

    [Theory]
    [InlineData(0L)]
    [InlineData(-3L)]
    public async Task 可控非法引用_目的港零或负数_真实HTTP按参数错误拒绝且零写入(long portId)
    {
        Guard();
        var tag = Tag();
        using var admin = await _fx.LoginAsAdminAsync();
        var customerId = await CreateCustomerAsync(admin, tag);
        var productId = await CreateProductAsync(admin, tag);
        var customerPo = "HTTP428-PORTBAD-" + tag;

        var (status, root, _) = await PostOrderAsync(admin,
            OrderBody(customerId, productId, customerPo, portId: portId, includePortId: true));

        // 业务拒绝走既有受控信封（HTTP 200 + 非零业务码），绝不落库。
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(1001, root.GetProperty("code").GetInt32());   // ErrorCodes.InvalidParameter
        Assert.Equal(0, (await _fx.ReadPersistedAsync(customerPo)).Count);
    }

    [Fact]
    public async Task 可控非法引用_目的港不存在_真实HTTP按不存在拒绝且零写入()
    {
        Guard();
        var tag = Tag();
        using var admin = await _fx.LoginAsAdminAsync();
        var customerId = await CreateCustomerAsync(admin, tag);
        var productId = await CreateProductAsync(admin, tag);
        var customerPo = "HTTP428-PORTMISSING-" + tag;

        var (status, root, _) = await PostOrderAsync(admin,
            OrderBody(customerId, productId, customerPo, portId: 999_999_999L, includePortId: true));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(1002, root.GetProperty("code").GetInt32());   // ErrorCodes.NotFound
        Assert.Equal(0, (await _fx.ReadPersistedAsync(customerPo)).Count);
    }

    [Fact]
    public async Task 可控非法引用_商品不存在_真实HTTP按不存在拒绝且零写入()
    {
        Guard();
        var tag = Tag();
        using var admin = await _fx.LoginAsAdminAsync();
        var customerId = await CreateCustomerAsync(admin, tag);
        var customerPo = "HTTP428-PRODMISSING-" + tag;

        var (status, root, _) = await PostOrderAsync(admin,
            OrderBody(customerId, 999_999_999L, customerPo));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(1002, root.GetProperty("code").GetInt32());   // ErrorCodes.NotFound
        Assert.Equal(0, (await _fx.ReadPersistedAsync(customerPo)).Count);
    }

    // ==================== 4. 权限拒绝（真实 HTTP，零写入） ====================

    [Fact]
    public async Task 无身份_真实HTTP_按未认证拒绝且零写入()
    {
        Guard();
        var tag = Tag();
        using var admin = await _fx.LoginAsAdminAsync();
        var customerId = await CreateCustomerAsync(admin, tag);
        var productId = await CreateProductAsync(admin, tag);
        var customerPo = "HTTP428-ANON-" + tag;

        using var anonymous = _fx.CreateAnonymousClient();
        var (status, _, _) = await PostOrderAsync(anonymous, OrderBody(customerId, productId, customerPo));

        Assert.Equal(HttpStatusCode.Unauthorized, status);
        Assert.Equal(0, (await _fx.ReadPersistedAsync(customerPo)).Count);
    }

    [Fact]
    public async Task 受限账号_缺销售订单菜单_真实HTTP_按权限不足拒绝且零写入()
    {
        Guard();
        var tag = Tag();
        using var admin = await _fx.LoginAsAdminAsync();
        var customerId = await CreateCustomerAsync(admin, tag);
        var productId = await CreateProductAsync(admin, tag);
        var customerPo = "HTTP428-DENIED-" + tag;

        // 经既有用户管理接口新建账号且不分配任何角色：没有既有「销售订单」菜单授权（不新增任何授权 / 菜单）。
        var userName = "http428-" + tag;
        var password = "Http428@" + tag;
        await _fx.CreateUserWithoutRolesAsync(admin, userName, password);

        using var restricted = await _fx.LoginAsync(userName, password);
        var (status, root, _) = await PostOrderAsync(restricted, OrderBody(customerId, productId, customerPo));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(2002, root.GetProperty("code").GetInt32());   // ErrorCodes.Forbidden
        Assert.Equal(0, (await _fx.ReadPersistedAsync(customerPo)).Count);
    }
}

/// <summary>
/// ERP-428 真实 HTTP + 真实隔离 SQL 夹具：目标必须是专用实例 <c>(localdb)\NEWERP_AutoAcceptance</c>、
/// 库名前缀 <c>NEWERP_AUTOTEST</c> 且 <c>Integrated Security=true</c>；只使用<strong>全新 GUID 后缀库</strong>，
/// 发现同名库已存在立即拒绝（绝不 drop / reset / 复用）；连接串只来自进程环境变量或专用 localdb 默认值。
/// 启动真实 ERP.Api 进程并在就绪后提供已登录 / 匿名 <see cref="HttpClient"/> 与直接 SQL 复核入口。
/// </summary>
public sealed class SalesOrderHttpFormSqlServerFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";
    public const string AdminUser = "admin";
    public const string AdminPassword = "Admin@123";

    private const string DefaultDatabaseName = DatabasePrefix + "_SALESORDERHTTPFORM";
    private const string PortVariable = "ERP_428_HTTP_PORT";
    private const string DefaultPort = "5277";

    private readonly StringBuilder _apiConsole = new();
    private readonly object _apiConsoleLock = new();
    private Process? _apiProcess;

    public string ConnectionString { get; private set; } = string.Empty;
    public string BaseUrl { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        var configured = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        var connectionString = string.IsNullOrWhiteSpace(configured)
            ? $"Server=(localdb)\\{InstanceMarker};Initial Catalog={DefaultDatabaseName}_{Guid.NewGuid():N};"
              + "Integrated Security=true;TrustServerCertificate=true;"
            : configured;

        // 访问任何数据库之前：实例 / 库名前缀 / 集成安全 + 显式测试上下文（不满足立即 fail-closed）。
        AssertDedicatedTarget(connectionString);
        TestDatabaseSafetyGuard.EnsureApprovedTarget(connectionString, "销售订单 HTTP 表单集成验收");
        ConnectionString = connectionString;

        // 访问任何数据库之前：只允许全新 GUID 后缀库；同名库已存在立即拒绝。
        await AssertFreshDatabaseAsync(connectionString);

        BaseUrl = $"http://localhost:{Environment.GetEnvironmentVariable(PortVariable) ?? DefaultPort}";
        EnsurePortIsFree();
        _apiProcess = StartApi(connectionString);
        try
        {
            await WaitForApiReadyAsync();
        }
        catch
        {
            DumpApiConsole();
            throw;
        }
    }

    public Task DisposeAsync()
    {
        DumpApiConsole();
        try
        {
            if (_apiProcess is not null && !_apiProcess.HasExited)
            {
                _apiProcess.Kill(entireProcessTree: true);
            }
        }
        catch { /* 结束进程失败不影响断言结果 */ }
        try { _apiProcess?.Dispose(); } catch { /* 忽略 */ }
        return Task.CompletedTask;
    }

    // ==================== HTTP 入口（真实登录 + 匿名） ====================

    public HttpClient CreateAnonymousClient()
        => new() { BaseAddress = new Uri(BaseUrl), Timeout = TimeSpan.FromSeconds(60) };

    public Task<HttpClient> LoginAsAdminAsync() => LoginAsync(AdminUser, AdminPassword);

    /// <summary>用既有登录接口取得真实 JWT（令牌只存活在内存，绝不写入日志 / 证据）。</summary>
    public async Task<HttpClient> LoginAsync(string userName, string password)
    {
        using var anonymous = CreateAnonymousClient();
        var payload = JsonSerializer.Serialize(new { userName, password });
        using var response = await anonymous.PostAsync("/api/auth/login",
            new StringContent(payload, Encoding.UTF8, "application/json"));
        var raw = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(raw);
        Assert.Equal(0, doc.RootElement.GetProperty("code").GetInt32());
        var token = doc.RootElement.GetProperty("data").GetProperty("token").GetString();
        Assert.False(string.IsNullOrWhiteSpace(token));

        var client = CreateAnonymousClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    /// <summary>经既有用户管理接口新建账号且不分配任何角色（不新增任何授权 / 菜单 / 角色）。</summary>
    public async Task CreateUserWithoutRolesAsync(HttpClient admin, string userName, string password)
    {
        var payload = JsonSerializer.Serialize(new
        {
            userName, password, displayName = userName, email = string.Empty, phone = string.Empty,
            roleIds = Array.Empty<long>()
        });
        using var response = await admin.PostAsync("/api/sys/users",
            new StringContent(payload, Encoding.UTF8, "application/json"));
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(0, doc.RootElement.GetProperty("code").GetInt32());
    }

    // ==================== 直接 SQL 复核（同一全新 GUID 库） ====================

    /// <summary>按客户 PO 号直接读取持久化销售订单（单号 / 目的港；IsDeleted=0）。</summary>
    public async Task<(int Count, string? OrderNo, long? PortId)> ReadPersistedAsync(string customerPo)
    {
        AssertDedicatedTarget(ConnectionString);
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT OrderNo, PortId FROM db_owner.SalesOrders WHERE CustomerPoNo = @po AND IsDeleted = 0";
        cmd.Parameters.AddWithValue("@po", customerPo);
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return (0, null, null);

        var orderNo = reader.IsDBNull(0) ? null : reader.GetString(0);
        var portId = reader.IsDBNull(1) ? (long?)null : reader.GetInt64(1);
        return (1, orderNo, portId);
    }

    internal static void AssertDedicatedTarget(string connectionString)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        Assert.Equal($"(localdb)\\{InstanceMarker}", builder.DataSource ?? string.Empty, ignoreCase: true);
        Assert.StartsWith(DatabasePrefix, builder.InitialCatalog ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.True(builder.IntegratedSecurity);
    }

    /// <summary>任何数据库访问之前拒绝既有目标：只允许本次全新 GUID 后缀库。</summary>
    private static async Task AssertFreshDatabaseAsync(string connectionString)
    {
        AssertDedicatedTarget(connectionString);
        var database = new SqlConnectionStringBuilder(connectionString).InitialCatalog;
        var master = new SqlConnectionStringBuilder(connectionString) { InitialCatalog = "master" };
        await using var conn = new SqlConnection(master.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT DB_ID(@database)";
        cmd.Parameters.AddWithValue("@database", database);
        var existing = await cmd.ExecuteScalarAsync();
        if (existing is not null && existing != DBNull.Value)
            throw new InvalidOperationException(
                "The isolated fixture database already exists; choose a fresh NEWERP_AUTOTEST database.");
    }

    /// <summary>启动前确认端口空闲：避免静默连上残留 / 外部实例产出无效证据。</summary>
    private void EnsurePortIsFree()
    {
        var uri = new Uri(BaseUrl);
        try
        {
            using var probe = new System.Net.Sockets.TcpClient();
            if (probe.ConnectAsync(uri.Host, uri.Port).Wait(TimeSpan.FromMilliseconds(500)))
                throw new InvalidOperationException(
                    $"端口 {uri.Port} 已被其它进程监听（很可能是上一次验收残留的 ERP.Api）："
                    + "继续执行会让本次验收连上外部实例并产出无效证据，请先结束占用该端口的进程后重试。");
        }
        catch (AggregateException)
        {
            // 连接被拒绝 = 端口空闲，正常继续。
        }
    }

    private Process StartApi(string connectionString)
    {
        var binDir = ResolveApiBinDirectory();
        var dllPath = Path.Combine(binDir, "ERP.Api.dll");
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"\"{dllPath}\" --urls {BaseUrl} --contentRoot \"{binDir}\"",
            WorkingDirectory = binDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.EnvironmentVariables["ASPNETCORE_ENVIRONMENT"] = "Development";
        psi.EnvironmentVariables[TestDatabaseSafetyGuard.TestRunVariable] = TestDatabaseSafetyGuard.TestRunValue;
        psi.EnvironmentVariables["ERP_ConnectionStrings__Default"] = connectionString;

        var process = Process.Start(psi)!;
        process.OutputDataReceived += (_, e) => AppendApiConsole(e.Data);
        process.ErrorDataReceived += (_, e) => AppendApiConsole(e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    private static string ResolveApiBinDirectory()
    {
        var baseDir = new DirectoryInfo(AppContext.BaseDirectory);
        var configuration = baseDir.Parent?.Name ?? "Release";
        var root = baseDir;
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "NEWERP.sln"))) root = root.Parent;
        Assert.NotNull(root);

        foreach (var candidate in new[] { configuration, "Release", "Debug" })
        {
            var binDir = Path.Combine(root!.FullName, "src", "ERP.Api", "bin", candidate, "net8.0");
            if (File.Exists(Path.Combine(binDir, "ERP.Api.dll"))) return binDir;
        }
        throw new InvalidOperationException("未找到 ERP.Api 构建输出，请先构建解决方案后再运行真实 HTTP 集成验收。");
    }

    private async Task WaitForApiReadyAsync()
    {
        var deadline = DateTime.UtcNow.AddSeconds(180);
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        string lastError = "尚未收到响应";
        while (DateTime.UtcNow < deadline)
        {
            if (_apiProcess?.HasExited == true)
                throw new InvalidOperationException(
                    $"ERP.Api 启动过程中退出（ExitCode={_apiProcess.ExitCode}）。API 控制台尾部输出：\n{ApiConsoleTail()}");
            try
            {
                using var response = await client.GetAsync(BaseUrl + "/swagger/v1/swagger.json");
                if ((int)response.StatusCode == 200) return;
                lastError = $"HTTP {(int)response.StatusCode}";
            }
            catch (Exception ex) { lastError = ex.Message; }
            await Task.Delay(500);
        }
        throw new InvalidOperationException(
            $"ERP.Api 在 180 秒内未就绪（最后状态：{lastError}）。API 控制台尾部输出：\n{ApiConsoleTail()}");
    }

    private void AppendApiConsole(string? line)
    {
        if (line is null) return;
        lock (_apiConsoleLock)
        {
            if (_apiConsole.Length > 400_000) _apiConsole.Remove(0, 200_000);
            _apiConsole.Append(line).Append('\n');
        }
    }

    private string ApiConsoleTail(int maxChars = 4000)
    {
        lock (_apiConsoleLock)
        {
            var text = _apiConsole.ToString();
            return text.Length <= maxChars ? text : text[^maxChars..];
        }
    }

    /// <summary>把 API 控制台输出保留到证据目录（若提供），便于门禁失败时定位原始失败原因。</summary>
    private void DumpApiConsole()
    {
        var evidenceDirectory = Environment.GetEnvironmentVariable("ERP_AI_EVIDENCE_DIR");
        if (string.IsNullOrWhiteSpace(evidenceDirectory)) return;
        try
        {
            Directory.CreateDirectory(evidenceDirectory);
            File.WriteAllText(Path.Combine(evidenceDirectory, "sales-http-form-api-console.log"),
                ApiConsoleTail(int.MaxValue));
        }
        catch (IOException) { /* 证据写入失败不影响断言结果 */ }
    }
}

/// <summary>专用目标护栏的 fail-closed 覆盖：错误实例 / 错误库名 / 非集成安全必须在访问数据库之前被拒绝。</summary>
public sealed class SalesOrderHttpFormTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;User Id=sa;Password=secret")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(
            () => SalesOrderHttpFormSqlServerFixture.AssertDedicatedTarget(connection));
}
