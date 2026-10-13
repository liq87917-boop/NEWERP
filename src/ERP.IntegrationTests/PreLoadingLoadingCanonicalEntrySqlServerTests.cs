using ERP.Application.Common;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace ERP.IntegrationTests;

/// <summary>
/// ERP-485 规范预装柜单（<c>api/container/pre-loadings</c>）与规范装柜清单（<c>api/container/loading-lists</c>）
/// 的<b>真实 HTTP + 真实隔离 SQL</b> 验收（真实 ERP.Api 进程 + 全新 GUID <c>NEWERP_AUTOTEST</c> 库）：
/// <list type="number">
/// <item><b>真实 HTTP</b>：请求经真实 Kestrel 管线（MVC 绑定 + <c>[ApiController]</c> 模型校验 + JWT 认证 + 既有授权），
/// 因此「省略单号」由<b>绑定层</b>证明（修复前正是绑定层因 <c>[Required]</c> 返回 HTTP 400
/// <c>{"errors":{"PreLoadingNo"/"LoadingListNo":[...]}}</c>）；夹具为真实 API 进程注入
/// <b>进程内临时 JWT 签名密钥</b>（随机生成、只存活在该进程、绝不落盘 / 绝不继承空白签名配置）。</item>
/// <item><b>主子表绑定</b>：省略单号 + 主表字段 + <c>details</c> 明细数组一次提交，服务端权威编号 / 规范 Id /
/// 待提交草稿状态；回读（详情）与修改都按规范 <c>Id</c> 往返，明细行身份由服务端拥有。</item>
/// <item><b>显式证据保留</b>：预装柜明细的显式需求来源（<c>sourceSalesOrderDetailId</c>）保存后可由既有
/// 「需求来源」工作台按规范 Id 读取；主表单独更新不得静默清空该证据。</item>
/// <item><b>零副作用拒绝</b>：匿名 / 缺菜单 / 无效来源链接一律受控拒绝，且预装柜 / 装柜清单行、单据号序列与
/// 库存 / 库存流水 / 财务表零变化。</item>
/// <item><b>专用目标护栏</b>：必须在任何数据库访问之前精确命中 <c>(localdb)\NEWERP_AutoAcceptance</c> + 库名前缀
/// <c>NEWERP_AUTOTEST</c> + <c>Integrated Security=true</c>；每次运行只创建一个全新 GUID 后缀库，发现同名库已存在
/// 立即拒绝，绝不 drop / reset / 复用任何数据库，也绝不读取 appsettings / .env / 生产凭据。</item>
/// </list>
/// <para>构建完成不等于阶段验收；真实浏览器 UI 工作流未执行（本环境无浏览器执行器）。</para>
/// </summary>
public sealed class PreLoadingLoadingCanonicalEntrySqlServerTests
    : IClassFixture<PreLoadingLoadingCanonicalEntrySqlServerFixture>
{
    private readonly PreLoadingLoadingCanonicalEntrySqlServerFixture _fx;

    public PreLoadingLoadingCanonicalEntrySqlServerTests(PreLoadingLoadingCanonicalEntrySqlServerFixture fixture)
        => _fx = fixture;

    /// <summary>访问数据库前复核专用目标护栏（实例名 / 库名前缀 / 集成安全）。</summary>
    private void Guard()
    {
        var target = new SqlConnectionStringBuilder(_fx.ConnectionString);
        Assert.Equal($"(localdb)\\{PreLoadingLoadingCanonicalEntrySqlServerFixture.InstanceMarker}",
            target.DataSource, ignoreCase: true);
        Assert.StartsWith(PreLoadingLoadingCanonicalEntrySqlServerFixture.DatabasePrefix, target.InitialCatalog,
            StringComparison.OrdinalIgnoreCase);
        Assert.True(target.IntegratedSecurity);
    }

    private static string Tag() => Guid.NewGuid().ToString("N")[..8];

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

    /// <summary>预装柜单正文：与前端 crud.js 提交同形（**省略 preLoadingNo**；含主子表明细）。</summary>
    private static Dictionary<string, object?> PreLoadingBody(
        long? bookingId, long productId, long? sourceSalesOrderDetailId = null)
        => new()
        {
            ["loadingDate"] = DateTime.Today,
            ["bookingId"] = bookingId,
            ["containerNo"] = "CTN-485-" + productId,
            ["sealNo"] = "SEAL-485",
            ["remark"] = "HTTP485",
            ["details"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["productId"] = productId,
                    ["productName"] = "商品" + productId,
                    ["quantity"] = 4m,
                    ["cartons"] = 1m,
                    ["weight"] = 10m,
                    ["volume"] = 0.5m,
                    ["sourceSalesOrderDetailId"] = sourceSalesOrderDetailId
                }
            }
        };

    /// <summary>装柜清单正文：与前端 crud.js 提交同形（**省略 loadingListNo**；含主子表明细）。</summary>
    private static Dictionary<string, object?> LoadingListBody(long customerId, long productId)
        => new()
        {
            ["loadingDate"] = DateTime.Today,
            ["customerId"] = customerId,
            ["preLoadingId"] = null,
            ["containerNo"] = "CTN-485-L",
            ["shippingMark"] = "唛头-485",
            ["remark"] = "HTTP485",
            ["details"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["productId"] = productId,
                    ["productName"] = "商品" + productId,
                    ["quantity"] = 4m,
                    ["cartons"] = 1m,
                    ["weight"] = 10m,
                    ["volume"] = 0.5m
                }
            }
        };

    // ==================== 1. 省略单号：真实 HTTP 主子表新增 / 回读 / 修改 ====================

    [Fact]
    public async Task 省略单号_真实HTTP主子表新增回读修改_服务端权威单号与草稿状态()
    {
        Guard();
        var tag = Tag();
        using var admin = await _fx.LoginAsAdminAsync();
        var customerId = await _fx.SeedCustomerAsync("PL485-C-" + tag);
        var productId = await _fx.SeedProductAsync("PL485-P-" + tag);

        var (preStatus, preRoot, preRaw) = await SendAsync(admin, HttpMethod.Post,
            "/api/container/pre-loadings", PreLoadingBody(null, productId));
        Assert.Equal(HttpStatusCode.OK, preStatus);
        Assert.Equal(0, preRoot.GetProperty("code").GetInt32());
        Assert.DoesNotContain("is required", preRaw, StringComparison.OrdinalIgnoreCase);
        var preId = preRoot.GetProperty("data").GetProperty("id").GetInt64();
        var preNo = preRoot.GetProperty("data").GetProperty("preLoadingNo").GetString()!;
        Assert.True(preId > 0);
        Assert.StartsWith("YZ", preNo, StringComparison.Ordinal);
        Assert.Equal("Pending", preRoot.GetProperty("data").GetProperty("status").GetString());

        var stored = await _fx.ReadPreLoadingAsync(preId);
        Assert.Equal(1, stored.Count);
        Assert.Equal(preNo, stored.PreLoadingNo);
        Assert.Equal((int)DocumentStatus.Pending, stored.Status);
        Assert.Equal(1L, stored.DetailCount);
        Assert.Equal(4m, stored.DetailQuantity);
        Assert.Equal(1m, stored.TotalCartons);
        Assert.Equal(0.5m, stored.TotalVolume);

        // 修改（携带明细）：权威单号不变，明细被替换但行身份由服务端拥有
        var (updStatus, updRoot, _) = await SendAsync(admin, HttpMethod.Put,
            $"/api/container/pre-loadings/{preId}", new Dictionary<string, object?>
            {
                ["loadingDate"] = DateTime.Today,
                ["bookingId"] = null,
                ["containerNo"] = "CTN-485-UPD",
                ["sealNo"] = "SEAL-UPD",
                ["remark"] = "HTTP485-UPD",
                ["details"] = new[]
                {
                    new Dictionary<string, object?>
                    {
                        ["id"] = 987654, ["isDeleted"] = true,
                        ["productId"] = productId, ["productName"] = "商品", ["quantity"] = 6m,
                        ["cartons"] = 2m, ["weight"] = 20m, ["volume"] = 1m
                    }
                }
            });
        Assert.Equal(HttpStatusCode.OK, updStatus);
        Assert.Equal(0, updRoot.GetProperty("code").GetInt32());
        var updated = await _fx.ReadPreLoadingAsync(preId);
        Assert.Equal(preNo, updated.PreLoadingNo);
        Assert.Equal("CTN-485-UPD", updated.ContainerNo);
        Assert.Equal(6m, updated.DetailQuantity);
        Assert.NotEqual(987654, updated.DetailId);      // 伪造明细主键被服务端覆盖
        Assert.False(updated.DetailDeleted);

        // 装柜清单：省略单号主子表新增
        var (listStatus, listRoot, listRaw) = await SendAsync(admin, HttpMethod.Post,
            "/api/container/loading-lists", LoadingListBody(customerId, productId));
        Assert.Equal(HttpStatusCode.OK, listStatus);
        Assert.Equal(0, listRoot.GetProperty("code").GetInt32());
        Assert.DoesNotContain("is required", listRaw, StringComparison.OrdinalIgnoreCase);
        var listId = listRoot.GetProperty("data").GetProperty("id").GetInt64();
        var listNo = listRoot.GetProperty("data").GetProperty("loadingListNo").GetString()!;
        Assert.True(listId > 0);
        Assert.StartsWith("ZQ", listNo, StringComparison.Ordinal);
        Assert.Equal("Pending", listRoot.GetProperty("data").GetProperty("status").GetString());

        var storedList = await _fx.ReadLoadingListAsync(listId);
        Assert.Equal(1, storedList.Count);
        Assert.Equal(listNo, storedList.LoadingListNo);
        Assert.Equal(customerId, storedList.CustomerId);
        Assert.Equal(1L, storedList.DetailCount);
        Assert.Equal(4m, storedList.DetailQuantity);
    }

    // ==================== 2. 显式证据保留 + 授权状态流转 ====================

    [Fact]
    public async Task 显式需求来源证据_真实HTTP保存保留与授权状态流转_零下游副作用()
    {
        Guard();
        var tag = Tag();
        using var admin = await _fx.LoginAsAdminAsync();
        var customerId = await _fx.SeedCustomerAsync("PL485-EV-" + tag);
        var productId = await _fx.SeedProductAsync("PL485-EVP-" + tag);
        var bookingId = await _fx.SeedBookingAsync("DG485-" + tag, customerId);
        var orderId = await _fx.SeedSalesOrderAsync("SO485-" + tag, customerId);
        var sourceDetailId = await _fx.SeedSalesOrderDetailAsync(orderId, productId, 10m);
        var before = await _fx.ReadSnapshotAsync();

        var (createStatus, createRoot, _) = await SendAsync(admin, HttpMethod.Post,
            "/api/container/pre-loadings", PreLoadingBody(bookingId, productId, sourceDetailId));
        Assert.Equal(HttpStatusCode.OK, createStatus);
        Assert.Equal(0, createRoot.GetProperty("code").GetInt32());
        var id = createRoot.GetProperty("data").GetProperty("id").GetInt64();
        var number = createRoot.GetProperty("data").GetProperty("preLoadingNo").GetString()!;
        var stored = await _fx.ReadPreLoadingAsync(id);
        Assert.Equal(sourceDetailId, stored.SourceSalesOrderDetailId);

        // 保存后的规范 Id 直接可达既有「需求来源」证据工作台，证据仍有效
        var (linksStatus, linksRoot, _) = await SendAsync(admin, HttpMethod.Get,
            $"/api/container/pre-loadings/{id}/sales-order-links");
        Assert.Equal(HttpStatusCode.OK, linksStatus);
        var link = linksRoot.GetProperty("data")[0];
        Assert.Equal(sourceDetailId, link.GetProperty("sourceSalesOrderDetailId").GetInt64());
        Assert.True(link.GetProperty("sourceAvailable").GetBoolean());

        // 主表单独更新（未携带明细）：绝不静默清空需求来源证据
        var headerOnly = await SendAsync(admin, HttpMethod.Put, $"/api/container/pre-loadings/{id}",
            new Dictionary<string, object?>
            {
                ["loadingDate"] = DateTime.Today,
                ["bookingId"] = bookingId,
                ["containerNo"] = stored.ContainerNo,
                ["sealNo"] = "SEAL-EV",
                ["remark"] = "仅更新主表"
            });
        Assert.Equal(0, headerOnly.Root.GetProperty("code").GetInt32());
        Assert.Equal(sourceDetailId, (await _fx.ReadPreLoadingAsync(id)).SourceSalesOrderDetailId);

        // 授权状态流转：提交 → 审核 → 取消
        var submit = await SendAsync(admin, HttpMethod.Post, $"/api/container/pre-loadings/{id}/submit");
        Assert.Equal(0, submit.Root.GetProperty("code").GetInt32());
        Assert.Equal((int)DocumentStatus.Submitted, (await _fx.ReadPreLoadingAsync(id)).Status);
        var approve = await SendAsync(admin, HttpMethod.Post, $"/api/container/pre-loadings/{id}/approve");
        Assert.Equal(0, approve.Root.GetProperty("code").GetInt32());
        Assert.Equal((int)DocumentStatus.Approved, (await _fx.ReadPreLoadingAsync(id)).Status);
        var cancel = await SendAsync(admin, HttpMethod.Post, $"/api/container/pre-loadings/{id}/cancel");
        Assert.Equal(0, cancel.Root.GetProperty("code").GetInt32());
        var finalRow = await _fx.ReadPreLoadingAsync(id);
        Assert.Equal((int)DocumentStatus.Cancelled, finalRow.Status);
        Assert.Equal(number, finalRow.PreLoadingNo);
        Assert.Equal(sourceDetailId, finalRow.SourceSalesOrderDetailId);   // 状态流转不清链接

        var after = await _fx.ReadSnapshotAsync();
        Assert.Equal(before.PreLoadingCount + 1, after.PreLoadingCount);
        Assert.Equal(before.LoadingListCount, after.LoadingListCount);
        // 装柜单据只是物流证据：状态流转绝不产生库存 / 库存流水 / 财务 / 单证记录
        Assert.Equal(before.StockInCount, after.StockInCount);
        Assert.Equal(before.StockOutCount, after.StockOutCount);
        Assert.Equal(before.StockMovementCount, after.StockMovementCount);
        Assert.Equal(before.StockCount, after.StockCount);
        Assert.Equal(before.ReceiptCount, after.ReceiptCount);
        Assert.Equal(before.ExpenseCount, after.ExpenseCount);
        Assert.Equal(before.TradeDocumentCount, after.TradeDocumentCount);
    }

    // ==================== 3. 拒绝：受控错误且零写入零占号 ====================

    [Fact]
    public async Task 匿名缺菜单与无效来源_真实HTTP受控拒绝且零写入零占号()
    {
        Guard();
        var tag = Tag();
        using var admin = await _fx.LoginAsAdminAsync();
        var customerId = await _fx.SeedCustomerAsync("PL485-R-" + tag);
        var productId = await _fx.SeedProductAsync("PL485-RP-" + tag);
        var before = await _fx.ReadSnapshotAsync();

        // 匿名 → 未认证（真实 JWT 认证链路，不是缺失密钥回落）
        using var anonymous = _fx.CreateAnonymousClient();
        var anonymousPre = await SendAsync(anonymous, HttpMethod.Post, "/api/container/pre-loadings",
            PreLoadingBody(null, productId));
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousPre.Status);
        Assert.Equal(ErrorCodes.Unauthorized, anonymousPre.Root.GetProperty("code").GetInt32());

        // 已认证但无既有「预装柜单」/「装柜清单」菜单 → 模块授权拒绝
        var user = "pl485-nomenu-" + tag;
        var password = "Test@485" + tag;
        await _fx.CreateUserWithoutRolesAsync(admin, user, password);
        using var temporary = await _fx.LoginAsync(user, password);
        var noMenuPre = await SendAsync(temporary, HttpMethod.Post, "/api/container/pre-loadings",
            PreLoadingBody(null, productId));
        Assert.Equal(HttpStatusCode.OK, noMenuPre.Status);
        Assert.Equal(ErrorCodes.Forbidden, noMenuPre.Root.GetProperty("code").GetInt32());
        var noMenuList = await SendAsync(temporary, HttpMethod.Post, "/api/container/loading-lists",
            LoadingListBody(customerId, productId));
        Assert.Equal(HttpStatusCode.OK, noMenuList.Status);
        Assert.Equal(ErrorCodes.Forbidden, noMenuList.Root.GetProperty("code").GetInt32());

        // 无效订柜来源链接 → 受控拒绝（绝不带失效来源落库）
        var badBooking = await SendAsync(admin, HttpMethod.Post, "/api/container/pre-loadings",
            PreLoadingBody(99999999L, productId));
        Assert.Equal(HttpStatusCode.OK, badBooking.Status);
        Assert.NotEqual(0, badBooking.Root.GetProperty("code").GetInt32());

        // 非正预装柜来源 Id → 显式非法引用，受控拒绝
        var badListBody = LoadingListBody(customerId, productId);
        badListBody["preLoadingId"] = 0L;
        var badList = await SendAsync(admin, HttpMethod.Post, "/api/container/loading-lists", badListBody);
        Assert.Equal(HttpStatusCode.OK, badList.Status);
        Assert.Equal(ErrorCodes.InvalidParameter, badList.Root.GetProperty("code").GetInt32());

        var after = await _fx.ReadSnapshotAsync();
        Assert.Equal(before.PreLoadingCount, after.PreLoadingCount);
        Assert.Equal(before.LoadingListCount, after.LoadingListCount);
        Assert.Equal(before.PreLoadingSequence, after.PreLoadingSequence);   // 被拒请求绝不占号
        Assert.Equal(before.LoadingListSequence, after.LoadingListSequence);
        Assert.Equal(before.StockCount, after.StockCount);
        Assert.Equal(before.StockMovementCount, after.StockMovementCount);
    }
}

/// <summary>专用目标护栏的 fail-closed 覆盖：错误实例 / 错误库名 / 非集成安全必须在访问数据库之前被拒绝。</summary>
public sealed class PreLoadingLoadingCanonicalEntryTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;User Id=sa;Password=secret")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(
            () => PreLoadingLoadingCanonicalEntrySqlServerFixture.AssertDedicatedTarget(connection));

    [Fact]
    public void Accepts_the_dedicated_localdb_target_with_integrated_security()
        => PreLoadingLoadingCanonicalEntrySqlServerFixture.AssertDedicatedTarget(
            "Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_X;Integrated Security=true");
}

/// <summary>
/// ERP-485 真实 HTTP 验收夹具：在任何数据库访问之前精确命中 <c>(localdb)\NEWERP_AutoAcceptance</c> +
/// 库名前缀 <c>NEWERP_AUTOTEST</c> 与 <c>Integrated Security=true</c>；只使用<strong>全新 GUID 后缀库</strong>，
/// 发现同名库已存在立即拒绝（绝不 drop / reset / 复用）；连接串只来自进程环境变量或专用 localdb 默认值。
/// <para>启动真实 ERP.Api 进程（真实 Kestrel / JWT 认证 / 既有授权），并为该进程注入<b>进程内临时 JWT 签名密钥</b>
/// （随机生成、只存在于该进程的环境变量、绝不落盘、绝不复用仓库内空白签名配置）；测试账号仍是既有种子账号
/// （不新增 / 不持久化凭据）。</para>
/// </summary>
public sealed class PreLoadingLoadingCanonicalEntrySqlServerFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";
    public const string AdminUser = "admin";
    public const string AdminPassword = "Admin@123";

    private const string DefaultDatabaseName = DatabasePrefix + "_PRELOADINGLOADING";
    private const string PortVariable = "ERP_485_HTTP_PORT";
    private const string DefaultPort = "5402";

    private readonly StringBuilder _apiConsole = new();
    private readonly object _apiConsoleLock = new();
    private Process? _apiProcess;

    public string ConnectionString { get; private set; } = string.Empty;
    public string BaseUrl { get; private set; } = string.Empty;

    /// <summary>按规范 Id 直接读取的预装柜单行（单号 / 状态 / 归属 / 汇总 / 明细身份与显式需求来源证据）。</summary>
    public readonly record struct PreLoadingRow(int Count, long Id, string? PreLoadingNo, int Status, bool IsDeleted,
        long? BookingId, string? ContainerNo, string? SealNo,
        decimal TotalCartons, decimal TotalWeight, decimal TotalVolume,
        long DetailCount, long DetailId, decimal DetailQuantity, bool DetailDeleted,
        long? SourceSalesOrderDetailId);

    /// <summary>按规范 Id 直接读取的装柜清单行（单号 / 状态 / 客户 / 归属 / 明细身份与显式出运证据）。</summary>
    public readonly record struct LoadingListRow(int Count, long Id, string? LoadingListNo, int Status, bool IsDeleted,
        long CustomerId, long? PreLoadingId, string? ContainerNo, string? ShippingMark,
        long DetailCount, decimal DetailQuantity, long? SourceStockOutDetailId);

    /// <summary>零副作用拒绝 / 有效状态变更所需的整表计数与单据号序列快照。</summary>
    public readonly record struct WriteSnapshot(long PreLoadingCount, long LoadingListCount,
        long PreLoadingSequence, long LoadingListSequence,
        long StockInCount, long StockOutCount, long StockMovementCount, long StockCount,
        long ReceiptCount, long ExpenseCount, long TradeDocumentCount);

    public async Task InitializeAsync()
    {
        var configured = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        var connectionString = string.IsNullOrWhiteSpace(configured)
            ? $"Server=(localdb)\\{InstanceMarker};Initial Catalog={DefaultDatabaseName}_{Guid.NewGuid():N};"
              + "Integrated Security=true;TrustServerCertificate=true;"
            : configured;

        // 访问任何数据库之前：实例 / 库名前缀 / 集成安全 + 显式测试上下文（不满足立 fail-closed）。
        AssertDedicatedTarget(connectionString);
        TestDatabaseSafetyGuard.EnsureApprovedTarget(connectionString, "预装柜 / 装柜清单规范入口集成验收");
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

    public ErpDbContext CreateDbContext() => new(
        new DbContextOptionsBuilder<ErpDbContext>().UseSqlServer(ConnectionString).Options);

    // ==================== HTTP 入口（真实登录 + 匿名） ====================

    public HttpClient CreateAnonymousClient()
        => new() { BaseAddress = new Uri(BaseUrl), Timeout = TimeSpan.FromSeconds(60) };

    public Task<HttpClient> LoginAsAdminAsync() => LoginAsync(AdminUser, AdminPassword);

    /// <summary>用既有登录接口取得真值 JWT（令牌只存活在内存，绝不写入日志 / 证据）。</summary>
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

    // ==================== 夹具数据（既有主数据表，只写夹具行） ====================

    /// <summary>播种一张既有客户（夹具数据；不新增表 / 列 / 权限）。</summary>
    public async Task<long> SeedCustomerAsync(string code, int status = 1)
    {
        AssertDedicatedTarget(ConnectionString);
        await using var db = CreateDbContext();
        var customer = new BaseCustomer
        {
            CustomerCode = code[..Math.Min(code.Length, 30)],
            CustomerName = code,
            Status = status
        };
        db.BaseCustomers.Add(customer);
        await db.SaveChangesAsync();
        return customer.Id;
    }

    /// <summary>播种一张既有商品（夹具数据；装柜明细的商品基础单位一致性由此证明）。</summary>
    public async Task<long> SeedProductAsync(string code, string unit = "PCS")
    {
        AssertDedicatedTarget(ConnectionString);
        await using var db = CreateDbContext();
        var product = new BaseProduct
        {
            ProductCode = code[..Math.Min(code.Length, 30)],
            ProductName = code,
            Spec = "规格A",
            Unit = unit
        };
        db.BaseProducts.Add(product);
        await db.SaveChangesAsync();
        return product.Id;
    }

    /// <summary>播种一张既有已审核订柜信息（夹具数据；预装柜单的权威上游来源）。</summary>
    public async Task<long> SeedBookingAsync(string no, long customerId)
    {
        AssertDedicatedTarget(ConnectionString);
        await using var db = CreateDbContext();
        var booking = new ContainerBooking
        {
            BookingNo = no, BookingDate = DateTime.Today, CustomerId = customerId,
            Status = DocumentStatus.Approved
        };
        db.ContainerBookings.Add(booking);
        await db.SaveChangesAsync();
        return booking.Id;
    }

    /// <summary>播种一张既有已审核销售订单（夹具数据；显式需求来源证据）。</summary>
    public async Task<long> SeedSalesOrderAsync(string no, long customerId)
    {
        AssertDedicatedTarget(ConnectionString);
        await using var db = CreateDbContext();
        var order = new SalesOrder
        {
            OrderNo = no, OrderDate = DateTime.Today, CustomerId = customerId,
            Status = DocumentStatus.Approved
        };
        db.SalesOrders.Add(order);
        await db.SaveChangesAsync();
        return order.Id;
    }

    /// <summary>播种一条既有销售订单明细（夹具数据；显式需求来源证据行）。</summary>
    public async Task<long> SeedSalesOrderDetailAsync(long salesOrderId, long productId, decimal quantity,
        string unit = "PCS")
    {
        AssertDedicatedTarget(ConnectionString);
        await using var db = CreateDbContext();
        var detail = new SalesOrderDetail
        {
            SalesOrderId = salesOrderId, ProductId = productId, ProductName = "商品" + productId,
            Unit = unit, Quantity = quantity, UnitPrice = 10m, Amount = quantity * 10m
        };
        db.SalesOrderDetails.Add(detail);
        await db.SaveChangesAsync();
        return detail.Id;
    }

    // ==================== 真实 SQL 复核（同一全新 GUID 库，只读） ====================

    /// <summary>按规范 Id 读取预装柜单头 + 明细（真实 SQL Server，独立连接）。</summary>
    public async Task<PreLoadingRow> ReadPreLoadingAsync(long id)
    {
        AssertDedicatedTarget(ConnectionString);
        await using var db = CreateDbContext();
        var header = await db.ContainerPreLoadings.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id);
        if (header is null)
            return new PreLoadingRow(0, 0, null, -1, false, null, null, null, 0m, 0m, 0m, 0, 0, 0m, false, null);

        var details = await db.ContainerPreLoadingDetails.AsNoTracking()
            .Where(d => d.PreLoadingId == id).ToListAsync();
        var active = details.Where(d => !d.IsDeleted).OrderBy(d => d.Id).ToList();
        var first = active.FirstOrDefault();
        return new PreLoadingRow(1, header.Id, header.PreLoadingNo, (int)header.Status, header.IsDeleted,
            header.BookingId, header.ContainerNo, header.SealNo,
            header.TotalCartons, header.TotalWeight, header.TotalVolume,
            active.Count, first?.Id ?? 0, first?.Quantity ?? 0m, first?.IsDeleted ?? false,
            first?.SourceSalesOrderDetailId);
    }

    /// <summary>按规范 Id 读取装柜清单头 + 明细（真实 SQL Server，独立连接）。</summary>
    public async Task<LoadingListRow> ReadLoadingListAsync(long id)
    {
        AssertDedicatedTarget(ConnectionString);
        await using var db = CreateDbContext();
        var header = await db.ContainerLoadingLists.AsNoTracking().FirstOrDefaultAsync(l => l.Id == id);
        if (header is null)
            return new LoadingListRow(0, 0, null, -1, false, 0, null, null, null, 0, 0m, null);

        var details = await db.ContainerLoadingDetails.AsNoTracking()
            .Where(d => d.LoadingListId == id).ToListAsync();
        var active = details.Where(d => !d.IsDeleted).OrderBy(d => d.Id).ToList();
        var first = active.FirstOrDefault();
        return new LoadingListRow(1, header.Id, header.LoadingListNo, (int)header.Status, header.IsDeleted,
            header.CustomerId, header.PreLoadingId, header.ContainerNo, header.ShippingMark,
            active.Count, first?.Quantity ?? 0m, first?.SourceStockOutDetailId);
    }

    /// <summary>零副作用拒绝 / 有效状态变更所需的整表计数与单据号序列快照。</summary>
    public async Task<WriteSnapshot> ReadSnapshotAsync()
    {
        AssertDedicatedTarget(ConnectionString);
        await using var db = CreateDbContext();
        var preSequence = await db.SysDocumentNumberRules.AsNoTracking()
            .Where(r => r.DocumentType == DocumentType.PreLoading && !r.IsDeleted)
            .Select(r => r.CurrentSequence).FirstOrDefaultAsync();
        var listSequence = await db.SysDocumentNumberRules.AsNoTracking()
            .Where(r => r.DocumentType == DocumentType.LoadingList && !r.IsDeleted)
            .Select(r => r.CurrentSequence).FirstOrDefaultAsync();
        return new WriteSnapshot(
            await db.ContainerPreLoadings.CountAsync(),
            await db.ContainerLoadingLists.CountAsync(),
            preSequence,
            listSequence,
            await db.StockIns.CountAsync(),
            await db.StockOuts.CountAsync(),
            await db.StockMovements.CountAsync(),
            await db.Stocks.CountAsync(),
            await db.FinanceReceipts.CountAsync(),
            await db.FinanceExpenses.CountAsync(),
            await db.TradeDocuments.CountAsync());
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

    /// <summary>进程内临时 JWT 签名密钥（随机生成：只注入被测 API 进程的环境变量，绝不落盘、绝不写入日志 / 证据）。</summary>
    private static string NewEphemeralJwtKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));

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
        // 真实认证链路（JWT 仍是唯一登录凭据）：只注入进程内临时签名密钥，不继承仓库里的空白 Jwt:Key，
        // 也不复用任何真实 / 生产密钥；密钥只存活于该进程环境变量，绝不落盘、绝不写入证据。
        psi.EnvironmentVariables["Jwt__Key"] = NewEphemeralJwtKey();
        psi.EnvironmentVariables["Jwt__Issuer"] = "ERP.Api";
        psi.EnvironmentVariables["Jwt__Audience"] = "ERP.Client";

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

    /// <summary>把 API 控制台输出保留到证据目录（若提供），便于门禁失败时定位原始失败原因（绝不写入任何凭据）。</summary>
    private void DumpApiConsole()
    {
        var evidenceDirectory = Environment.GetEnvironmentVariable("ERP_AI_EVIDENCE_DIR");
        if (string.IsNullOrWhiteSpace(evidenceDirectory)) return;
        try
        {
            Directory.CreateDirectory(evidenceDirectory);
            File.WriteAllText(Path.Combine(evidenceDirectory, "preloading-loading-canonical-entry-api-console.log"),
                ApiConsoleTail(int.MaxValue));
        }
        catch (IOException) { /* 证据写入失败不影响断言结果 */ }
    }
}
