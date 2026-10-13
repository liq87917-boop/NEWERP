using Microsoft.Data.SqlClient;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Xunit;

namespace ERP.IntegrationTests;

/// <summary>
/// ERP-474 询价单规范主子表入口的<b>真实 HTTP + 真实隔离 SQL</b> 验收（真实 ERP.Api 进程 + 全新 GUID <c>NEWERP_AUTOTEST</c> 库）：
/// <list type="number">
/// <item><b>真实 HTTP</b>：请求经真实 Kestrel 管线（MVC 绑定 + <c>[ApiController]</c> 模型校验 + JWT 认证 + 既有授权），
/// 因此「省略询价单号」由<b>绑定层</b>证明（修复前正是绑定层因 <c>[Required]</c> 返回 HTTP 400
/// <c>{"errors":{"InquiryNo":[...]}}</c>），直接实例化控制器的测试无法证明这一点。</item>
/// <item><b>权威单号与规范 Id</b>：省略 / 伪造询价单号均持久化服务端生成单号，返回规范 <c>Id</c>；明细主键由服务端生成。</item>
/// <item><b>主子表保存 / 回填 / 修改</b>：真实 HTTP 新增带明细的询价单，详情接口回填明细，修改后再次回读一致。</item>
/// <item><b>授权状态流与转换</b>：提交 → 审核 → 转报价单在同一真实身份下可达（规范 Id 口径），来源状态按既有规则流转。</item>
/// <item><b>非法条款零写入零占号</b>：未定义币种 / 非正 / 超精度汇率 / 非正数量 / 负单价 / 越界有效期 / 软删除明细注入
/// 一律被受控业务错误拒绝，询价单 / 明细与单号流水全部零变化。</item>
/// <item><b>与旧族分离</b>：旧存储过程族写路径（ERP-404 门禁）保持 fail closed，旧读侧保持可用；两者不存在 Oid ↔ Id 映射。</item>
/// <item><b>专用目标护栏</b>：必须在任何数据库访问之前精确命中 <c>(localdb)\NEWERP_AutoAcceptance</c> + 库名前缀
/// <c>NEWERP_AUTOTEST</c> + <c>Integrated Security=true</c>；每次运行只创建一个全新 GUID 后缀库，发现同名库已存在立即拒绝，
/// 绝不 drop / reset / 复用任何数据库，也绝不读取 appsettings / .env / 生产凭据。</item>
/// </list>
/// <para>构建完成不等于阶段验收：只有下列场景在专用 localdb 上真实执行通过才算验收证据。</para>
/// </summary>
public sealed class InquiryCanonicalEntrySqlServerTests : IClassFixture<InquiryCanonicalEntrySqlServerFixture>
{
    private readonly InquiryCanonicalEntrySqlServerFixture _fx;

    public InquiryCanonicalEntrySqlServerTests(InquiryCanonicalEntrySqlServerFixture fixture) => _fx = fixture;

    /// <summary>访问数据库前复核专用目标护栏（实例名 / 库名前缀 / 集成安全）。</summary>
    private void Guard()
    {
        var target = new SqlConnectionStringBuilder(_fx.ConnectionString);
        Assert.Equal($"(localdb)\\{InquiryCanonicalEntrySqlServerFixture.InstanceMarker}",
            target.DataSource, ignoreCase: true);
        Assert.StartsWith(InquiryCanonicalEntrySqlServerFixture.DatabasePrefix, target.InitialCatalog,
            StringComparison.OrdinalIgnoreCase);
        Assert.True(target.IntegratedSecurity);
    }

    private static string Tag() => Guid.NewGuid().ToString("N")[..8];

    // ==================== HTTP 脚手架 ====================

    /// <summary>询价单正文：与前端 crud.js 提交同形（可省略询价单号；枚举一律按既有数字字符串口径）。</summary>
    private static Dictionary<string, object?> InquiryBody(long customerId, long productId,
        string? inquiryNo = null)
    {
        var body = new Dictionary<string, object?>
        {
            ["inquiryDate"] = DateTime.Today.ToString("yyyy-MM-dd"),
            ["customerId"] = customerId,
            ["contactPerson"] = "HTTP474 联系人",
            ["contactPhone"] = "13800000000",
            ["salesmanId"] = null,
            ["currency"] = "2",
            ["exchangeRate"] = 7.2m,
            ["validDays"] = 30,
            ["remark"] = "HTTP474",
            ["details"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["productId"] = productId, ["productName"] = "HTTP474 商品", ["spec"] = "标准",
                    ["unit"] = "PCS", ["quantity"] = 10m, ["unitPrice"] = 100m
                }
            }
        };
        if (inquiryNo is not null) body["inquiryNo"] = inquiryNo;
        return body;
    }

    private static async Task<(HttpStatusCode Status, JsonElement Root, string Raw)> SendAsync(
        HttpClient client, HttpMethod method, string url, object? body = null)
    {
        using var request = new HttpRequestMessage(method, url);
        if (body is not null)
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request);
        var raw = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(raw);
        return (response.StatusCode, doc.RootElement.Clone(), raw);
    }

    private static async Task<long> CreateCustomerAsync(HttpClient client, string tag)
    {
        var payload = JsonSerializer.Serialize(new
        {
            customerCode = "C-HTTP474-" + tag, customerName = "HTTP474 客户 " + tag, currency = "USD"
        });
        using var response = await client.PostAsync("/api/base/customers",
            new StringContent(payload, Encoding.UTF8, "application/json"));
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(0, doc.RootElement.GetProperty("code").GetInt32());
        return doc.RootElement.GetProperty("data").GetProperty("id").GetInt64();
    }

    private static async Task<long> CreateProductAsync(HttpClient client, string tag)
    {
        var payload = JsonSerializer.Serialize(new
        {
            productCode = "P-HTTP474-" + tag, productName = "HTTP474 商品 " + tag, unit = "PCS"
        });
        using var response = await client.PostAsync("/api/base/products",
            new StringContent(payload, Encoding.UTF8, "application/json"));
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(0, doc.RootElement.GetProperty("code").GetInt32());
        return doc.RootElement.GetProperty("data").GetProperty("id").GetInt64();
    }

    // ==================== 1. 省略 / 伪造询价单号（真实 HTTP） ====================

    [Fact]
    public async Task 省略询价单号_真实HTTP新增成功_服务端权威单号与规范Id落库()
    {
        Guard();
        var tag = Tag();
        using var admin = await _fx.LoginAsAdminAsync();
        var customerId = await CreateCustomerAsync(admin, tag);
        var productId = await CreateProductAsync(admin, tag);

        var (status, root, raw) = await SendAsync(admin, HttpMethod.Post, "/api/inquiries",
            InquiryBody(customerId, productId));

        // 修复前：绑定层因 [Required] 返回 HTTP 400；现在必须成功。
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(0, root.GetProperty("code").GetInt32());
        Assert.DoesNotContain("InquiryNo field is required", raw, StringComparison.OrdinalIgnoreCase);

        var data = root.GetProperty("data");
        var id = data.GetProperty("id").GetInt64();
        Assert.True(id > 0);                                        // 规范 Id（EF 自增主键）
        Assert.StartsWith("INQ", data.GetProperty("inquiryNo").GetString(), StringComparison.Ordinal);

        var persisted = await _fx.ReadByCustomerAsync(customerId);
        Assert.Equal(1, persisted.Count);
        Assert.Equal(id, persisted.Id);
        Assert.Equal(data.GetProperty("inquiryNo").GetString(), persisted.InquiryNo);
        Assert.Equal(0, persisted.Status);                          // Pending
        Assert.Equal(1, await _fx.CountDetailsAsync(id));

        // 列表：规范 ledger 端点按权威单号可检索到刚写入的询价单（列表 / 表单同一规范 Id 口径）。
        var number = data.GetProperty("inquiryNo").GetString()!;
        var (listed, listRoot, _) = await SendAsync(admin, HttpMethod.Get,
            "/api/inquiries?page=1&pageSize=5&keyword=" + Uri.EscapeDataString(number));
        Assert.Equal(HttpStatusCode.OK, listed);
        Assert.Equal(0, listRoot.GetProperty("code").GetInt32());
        Assert.True(listRoot.GetProperty("data").GetProperty("total").GetInt64() >= 1);
        Assert.Equal(id, listRoot.GetProperty("data").GetProperty("items")[0].GetProperty("id").GetInt64());
    }

    [Fact]
    public async Task 伪造询价单号_真实HTTP_持久化号码由服务端权威生成()
    {
        Guard();
        var tag = Tag();
        using var admin = await _fx.LoginAsAdminAsync();
        var customerId = await CreateCustomerAsync(admin, tag);
        var productId = await CreateProductAsync(admin, tag);
        var forged = "HACK-HTTP474-" + tag;

        var (status, root, _) = await SendAsync(admin, HttpMethod.Post, "/api/inquiries",
            InquiryBody(customerId, productId, inquiryNo: forged));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(0, root.GetProperty("code").GetInt32());

        var persisted = await _fx.ReadByCustomerAsync(customerId);
        Assert.Equal(1, persisted.Count);
        Assert.StartsWith("INQ", persisted.InquiryNo, StringComparison.Ordinal);
        Assert.NotEqual(forged, persisted.InquiryNo);
    }

    // ==================== 2. 主子表保存 / 回填 / 修改（真实 HTTP，规范 Id） ====================

    [Fact]
    public async Task 主子表_新增回填修改_按规范Id一致()
    {
        Guard();
        var tag = Tag();
        using var admin = await _fx.LoginAsAdminAsync();
        var customerId = await CreateCustomerAsync(admin, tag);
        var productId = await CreateProductAsync(admin, tag);

        var (created, createdRoot, _) = await SendAsync(admin, HttpMethod.Post, "/api/inquiries",
            InquiryBody(customerId, productId));
        Assert.Equal(HttpStatusCode.OK, created);
        var id = createdRoot.GetProperty("data").GetProperty("id").GetInt64();

        // 回填：详情接口返回规范 Id 与明细（含服务端派生金额）。
        var (loaded, loadedRoot, _) = await SendAsync(admin, HttpMethod.Get, $"/api/inquiries/{id}");
        Assert.Equal(HttpStatusCode.OK, loaded);
        var loadedData = loadedRoot.GetProperty("data");
        Assert.Equal(id, loadedData.GetProperty("id").GetInt64());
        var loadedDetails = loadedData.GetProperty("details");
        Assert.Equal(1, loadedDetails.GetArrayLength());
        Assert.Equal(1000m, loadedDetails[0].GetProperty("amount").GetDecimal());

        // 修改：编辑明细数量 / 单价后回读一致（金额由服务端重算）。
        var edit = InquiryBody(customerId, productId);
        edit["contactPerson"] = "HTTP474 修改后";
        edit["details"] = new object[]
        {
            new Dictionary<string, object?>
            {
                ["productId"] = productId, ["productName"] = "HTTP474 商品", ["spec"] = "标准",
                ["unit"] = "PCS", ["quantity"] = 3m, ["unitPrice"] = 12.5m
            }
        };
        var (updated, updatedRoot, _) = await SendAsync(admin, HttpMethod.Put, $"/api/inquiries/{id}", edit);
        Assert.Equal(HttpStatusCode.OK, updated);
        Assert.Equal(0, updatedRoot.GetProperty("code").GetInt32());

        var (reloaded, reloadedRoot, _) = await SendAsync(admin, HttpMethod.Get, $"/api/inquiries/{id}");
        Assert.Equal(HttpStatusCode.OK, reloaded);
        var reloadedData = reloadedRoot.GetProperty("data");
        Assert.Equal("HTTP474 修改后", reloadedData.GetProperty("contactPerson").GetString());
        Assert.Equal(37.5m, reloadedData.GetProperty("details")[0].GetProperty("amount").GetDecimal());
    }


    // ==================== 3. 非法条款：零写入、零占号（真实 HTTP） ====================

    [Theory]
    [InlineData("currency")]
    [InlineData("rate0")]
    [InlineData("rateOverprecision")]
    [InlineData("quantity0")]
    [InlineData("priceNegative")]
    [InlineData("validDays0")]
    [InlineData("injectedDeletedDetail")]
    public async Task 非法条款_真实HTTP_受控拒绝且零写入零占号(string scenario)
    {
        Guard();
        var tag = Tag();
        using var admin = await _fx.LoginAsAdminAsync();
        var customerId = await CreateCustomerAsync(admin, tag);
        var productId = await CreateProductAsync(admin, tag);
        var before = await _fx.ReadInquirySequenceAsync();

        var body = InquiryBody(customerId, productId);
        var line = (Dictionary<string, object?>)((object[])body["details"]!)[0];
        switch (scenario)
        {
            case "currency": body["currency"] = "999"; break;
            case "rate0": body["exchangeRate"] = 0m; break;
            case "rateOverprecision": body["exchangeRate"] = 7.123m; break;
            case "quantity0": line["quantity"] = 0m; break;
            case "priceNegative": line["unitPrice"] = -1m; break;
            case "validDays0": body["validDays"] = 0; break;
            default: line["isDeleted"] = true; break;
        }

        var (status, root, _) = await SendAsync(admin, HttpMethod.Post, "/api/inquiries", body);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(1001, root.GetProperty("code").GetInt32());     // ErrorCodes.InvalidParameter
        Assert.Equal(0, (await _fx.ReadByCustomerAsync(customerId)).Count);
        Assert.Equal(before, await _fx.ReadInquirySequenceAsync());  // 被拒绝的请求绝不消耗单号
    }

    // ==================== 4. 授权状态流与转报价（真实 HTTP，规范 Id） ====================

    [Fact]
    public async Task 授权状态流_提交审核与转报价可达()
    {
        Guard();
        var tag = Tag();
        using var admin = await _fx.LoginAsAdminAsync();
        var customerId = await CreateCustomerAsync(admin, tag);
        var productId = await CreateProductAsync(admin, tag);

        var (created, createdRoot, _) = await SendAsync(admin, HttpMethod.Post, "/api/inquiries",
            InquiryBody(customerId, productId));
        Assert.Equal(HttpStatusCode.OK, created);
        var id = createdRoot.GetProperty("data").GetProperty("id").GetInt64();

        var (submitted, _, _) = await SendAsync(admin, HttpMethod.Post, $"/api/inquiries/{id}/submit");
        Assert.Equal(HttpStatusCode.OK, submitted);
        Assert.Equal(1, (await _fx.ReadByCustomerAsync(customerId)).Status);   // Submitted

        var (approved, _, _) = await SendAsync(admin, HttpMethod.Post, $"/api/inquiries/{id}/approve");
        Assert.Equal(HttpStatusCode.OK, approved);
        Assert.Equal(2, (await _fx.ReadByCustomerAsync(customerId)).Status);   // Approved

        var (converted, convertedRoot, _) = await SendAsync(admin, HttpMethod.Post,
            $"/api/inquiries/{id}/to-quotation");
        Assert.Equal(HttpStatusCode.OK, converted);
        Assert.Equal(0, convertedRoot.GetProperty("code").GetInt32());
        Assert.StartsWith("QT", convertedRoot.GetProperty("data").GetProperty("quotationNo").GetString(),
            StringComparison.Ordinal);
        Assert.Equal(4, (await _fx.ReadByCustomerAsync(customerId)).Status);   // Completed
    }

    // ==================== 5. 权限拒绝（真实 HTTP，零写入） ====================

    [Fact]
    public async Task 无身份_真实HTTP_按未认证拒绝且零写入()
    {
        Guard();
        var tag = Tag();
        using var admin = await _fx.LoginAsAdminAsync();
        var customerId = await CreateCustomerAsync(admin, tag);
        var productId = await CreateProductAsync(admin, tag);

        using var anonymous = _fx.CreateAnonymousClient();
        var (status, _, _) = await SendAsync(anonymous, HttpMethod.Post, "/api/inquiries",
            InquiryBody(customerId, productId));

        Assert.Equal(HttpStatusCode.Unauthorized, status);
        Assert.Equal(0, (await _fx.ReadByCustomerAsync(customerId)).Count);
    }

    [Fact]
    public async Task 受限账号_缺询价单菜单_按权限不足拒绝且零写入()
    {
        Guard();
        var tag = Tag();
        using var admin = await _fx.LoginAsAdminAsync();
        var customerId = await CreateCustomerAsync(admin, tag);
        var productId = await CreateProductAsync(admin, tag);

        // 经既有用户管理接口新建账号且不分配任何角色：没有既有「询价单」菜单授权（不新增任何授权 / 菜单）。
        var userName = "http474-" + tag;
        var password = "Http474@" + tag;
        await _fx.CreateUserWithoutRolesAsync(admin, userName, password);

        using var restricted = await _fx.LoginAsync(userName, password);
        var (status, root, _) = await SendAsync(restricted, HttpMethod.Post, "/api/inquiries",
            InquiryBody(customerId, productId));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(2002, root.GetProperty("code").GetInt32());     // ErrorCodes.Forbidden
        Assert.Equal(0, (await _fx.ReadByCustomerAsync(customerId)).Count);
    }

    // ==================== 6. 与旧存储过程族分离（旧写 fail closed） ====================

    [Fact]
    public async Task 旧族分离_旧写路径fail_closed_规范入口不受影响()
    {
        Guard();
        var tag = Tag();
        using var admin = await _fx.LoginAsAdminAsync();
        var customerId = await CreateCustomerAsync(admin, tag);
        var productId = await CreateProductAsync(admin, tag);

        // 旧写路径（ERP-404 门禁）：即使具备既有功能菜单也整体 fail closed（绝不调用 sp_Biz_Inquiry）。
        var legacy = new Dictionary<string, object?>
        {
            ["Action"] = "add",
            ["InquiryNo"] = "LEGACY-HTTP474-" + tag,
            ["CustomerId"] = customerId,
            ["Currency"] = 2,
            ["ExchangeRate"] = 7.2m,
            ["ValidDays"] = 30
        };
        var (legacyStatus, legacyRoot, _) = await SendAsync(admin, HttpMethod.Post,
            "/api/v2/bills/inquiry/save", legacy);
        Assert.Equal(HttpStatusCode.OK, legacyStatus);
        Assert.NotEqual(0, legacyRoot.GetProperty("code").GetInt32());

        // 规范入口在同一次运行中仍可正常写入（旧 Oid 写路径被停用不影响规范 Id 入口）。
        var (canonical, canonicalRoot, _) = await SendAsync(admin, HttpMethod.Post, "/api/inquiries",
            InquiryBody(customerId, productId));
        Assert.Equal(HttpStatusCode.OK, canonical);
        Assert.Equal(0, canonicalRoot.GetProperty("code").GetInt32());
        Assert.Equal(1, (await _fx.ReadByCustomerAsync(customerId)).Count);
    }
}


/// <summary>
/// ERP-474 真实 HTTP + 真实隔离 SQL 夹具：目标必须是专用实例 <c>(localdb)\NEWERP_AutoAcceptance</c>、
/// 库名前缀 <c>NEWERP_AUTOTEST</c> 且 <c>Integrated Security=true</c>；只使用<strong>全新 GUID 后缀库</strong>，
/// 发现同名库已存在立即拒绝（绝不 drop / reset / 复用）；连接串只来自进程环境变量或专用 localdb 默认值。
/// 启动真实 ERP.Api 进程并在就绪后提供已登录 / 匿名 <see cref="HttpClient"/> 与直接 SQL 只读复核入口。
/// </summary>
public sealed class InquiryCanonicalEntrySqlServerFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";
    public const string AdminUser = "admin";
    public const string AdminPassword = "Admin@123";

    private const string DefaultDatabaseName = DatabasePrefix + "_INQCANONICAL";
    private const string PortVariable = "ERP_474_HTTP_PORT";
    private const string DefaultPort = "5288";

    private readonly StringBuilder _apiConsole = new();
    private readonly object _apiConsoleLock = new();
    private Process? _apiProcess;

    public string ConnectionString { get; private set; } = string.Empty;
    public string BaseUrl { get; private set; } = string.Empty;

    /// <summary>按客户直接读取的询价单行（规范 Id / 单号 / 状态；IsDeleted = 0）。</summary>
    public readonly record struct InquiryRow(int Count, long Id, string? InquiryNo, int Status);

    public async Task InitializeAsync()
    {
        var configured = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        var connectionString = string.IsNullOrWhiteSpace(configured)
            ? $"Server=(localdb)\\{InstanceMarker};Initial Catalog={DefaultDatabaseName}_{Guid.NewGuid():N};"
              + "Integrated Security=true;TrustServerCertificate=true;"
            : configured;

        // 访问任何数据库之前：实例 / 库名前缀 / 集成安全 + 显式测试上下文（不满足立即 fail-closed）。
        AssertDedicatedTarget(connectionString);
        TestDatabaseSafetyGuard.EnsureApprovedTarget(connectionString, "询价单规范入口集成验收");
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
                _apiProcess.Kill(entireProcessTree: true);
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

    // ==================== 直接 SQL 复核（同一全新 GUID 库，只读） ====================

    /// <summary>按客户直接读取持久化询价单（规范 Id / 单号 / 状态；IsDeleted = 0）。</summary>
    public async Task<InquiryRow> ReadByCustomerAsync(long customerId)
    {
        AssertDedicatedTarget(ConnectionString);
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id, InquiryNo, Status FROM db_owner.Inquiries "
                          + "WHERE CustomerId = @customerId AND IsDeleted = 0";
        cmd.Parameters.AddWithValue("@customerId", customerId);
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return new InquiryRow(0, 0, null, -1);

        var id = reader.GetInt64(0);
        var inquiryNo = reader.IsDBNull(1) ? null : reader.GetString(1);
        var status = reader.GetInt32(2);
        return new InquiryRow(1, id, inquiryNo, status);
    }

    /// <summary>按规范 Id 直接读取明细行数（IsDeleted = 0）。</summary>
    public async Task<int> CountDetailsAsync(long inquiryId)
    {
        AssertDedicatedTarget(ConnectionString);
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(1) FROM db_owner.InquiryDetails "
                          + "WHERE InquiryId = @id AND IsDeleted = 0";
        cmd.Parameters.AddWithValue("@id", inquiryId);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    /// <summary>直接读取询价单单号流水（用于验证「被拒绝的请求绝不消耗单号」；无规则时返回 null）。</summary>
    public async Task<long?> ReadInquirySequenceAsync()
    {
        AssertDedicatedTarget(ConnectionString);
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        // DocumentType.Inquiry = 1（与 ERP.Domain.Enums.DocumentType 同源；避免在本项目引用领域枚举常量以外的新映射）。
        cmd.CommandText = "SELECT CurrentSequence FROM db_owner.SysDocumentNumberRules "
                          + "WHERE DocumentType = 1 AND IsDeleted = 0";
        var value = await cmd.ExecuteScalarAsync();
        return value is null or DBNull ? null : Convert.ToInt64(value);
    }

    // ==================== 专用目标护栏与进程脚手架 ====================

    /// <summary>专用目标护栏：错误实例 / 错误库名 / 非集成安全必须在访问数据库之前被拒绝。</summary>
    public static void AssertDedicatedTarget(string connectionString)
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
            File.WriteAllText(Path.Combine(evidenceDirectory, "inquiry-canonical-entry-api-console.log"),
                ApiConsoleTail(int.MaxValue));
        }
        catch (IOException) { /* 证据写入失败不影响断言结果 */ }
    }
}

/// <summary>专用目标护栏的 fail-closed 覆盖：错误实例 / 错误库名 / 非集成安全必须在访问数据库之前被拒绝。</summary>
public sealed class InquiryCanonicalEntryTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;User Id=sa;Password=secret")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(
            () => InquiryCanonicalEntrySqlServerFixture.AssertDedicatedTarget(connection));
}

