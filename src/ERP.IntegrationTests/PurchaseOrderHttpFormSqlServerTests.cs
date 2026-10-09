using Microsoft.Data.SqlClient;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Xunit;

namespace ERP.IntegrationTests;

/// <summary>
/// ERP-429 手工 / 关联采购订单录入的<b>真实 HTTP 表单</b>回归（真实 ERP.Api 进程 + 全新 GUID <c>NEWERP_AUTOTEST</c> 库）：
/// <list type="number">
/// <item><b>真实 HTTP</b>：请求经真实 Kestrel 管线（MVC 绑定 + <c>[ApiController]</c> 模型校验 + JWT 认证 + 既有授权），
/// 因此「省略 / 伪造采购单号」「归属来源与起运港留空为 null」由<b>绑定层</b>证明；直接实例化控制器的测试无法证明这一点
/// （修复前正是绑定层返回 HTTP 400 <c>{"errors":{"OrderNo":["The OrderNo field is required."]}}</c>）。</item>
/// <item><b>权威单号</b>：省略采购单号保存成功后，经直接 SQL 复核持久化单号由服务端生成；伪造单号不进入持久化。</item>
/// <item><b>真实来源</b>：合法已审核销售来源经既有销售订单接口（新建 → 提交 → 审核）与<b>同一活商品</b>产生，
/// 关联采购保存后归属来源 / 单号 / 客户快照与原始执行结算字段经直接 SQL 复核。</item>
/// <item><b>既有护栏不变</b>：来源 0 / 负数、起运港 0 / 负数 / 不存在、供应商 / 商品不存在、数量 / 单价 / 税率非法
/// 仍受控拒绝且零写入；未审核来源仍拒绝；无身份仍 401、无菜单账号仍 403，且都零写入。</item>
/// </list>
/// <para>安全口径：目标必须为专用 localdb 实例 <c>(localdb)\NEWERP_AutoAcceptance</c>、库名前缀 <c>NEWERP_AUTOTEST</c>
/// 且集成安全；每次运行只使用<b>全新 GUID 后缀库</b>，发现同名库已存在立即拒绝；绝不 drop / reset / 复用任何数据库，
/// 连接串只来自进程环境变量或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据。
/// <b>构建完成不等于阶段验收</b>：只有下列场景在专用 localdb 上真实执行通过才算验收证据。</para>
/// </summary>
public sealed class PurchaseOrderHttpFormSqlServerTests : IClassFixture<PurchaseOrderHttpFormSqlServerFixture>
{
    private readonly PurchaseOrderHttpFormSqlServerFixture _fx;

    public PurchaseOrderHttpFormSqlServerTests(PurchaseOrderHttpFormSqlServerFixture fixture) => _fx = fixture;

    /// <summary>访问数据库前复核专用目标护栏（实例名 / 库名前缀 / 集成安全）。</summary>
    private void Guard()
    {
        var target = new SqlConnectionStringBuilder(_fx.ConnectionString);
        Assert.Equal($"(localdb)\\{PurchaseOrderHttpFormSqlServerFixture.InstanceMarker}", target.DataSource, ignoreCase: true);
        Assert.StartsWith(PurchaseOrderHttpFormSqlServerFixture.DatabasePrefix, target.InitialCatalog,
            StringComparison.OrdinalIgnoreCase);
        Assert.True(target.IntegratedSecurity);
    }

    private static string Tag() => Guid.NewGuid().ToString("N")[..8];

    // ==================== HTTP 脚手架 ====================

    private static async Task<(HttpStatusCode Status, JsonElement Root, string Raw)> PostOrderAsync(
        HttpClient client, object body)
    {
        var payload = JsonSerializer.Serialize(body);
        using var response = await client.PostAsync("/api/purchase-orders",
            new StringContent(payload, Encoding.UTF8, "application/json"));
        var raw = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(raw);
        return (response.StatusCode, doc.RootElement.Clone(), raw);
    }

    private static async Task<long> CreateSupplierAsync(HttpClient client, string tag)
    {
        var payload = JsonSerializer.Serialize(new { supplierCode = "S-HTTP429-" + tag, supplierName = tag });
        using var response = await client.PostAsync("/api/base/suppliers",
            new StringContent(payload, Encoding.UTF8, "application/json"));
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(0, doc.RootElement.GetProperty("code").GetInt32());
        return doc.RootElement.GetProperty("data").GetProperty("id").GetInt64();
    }

    private static async Task<long> CreateProductAsync(HttpClient client, string tag)
    {
        var payload = JsonSerializer.Serialize(new { productCode = "P-HTTP429-" + tag, productName = tag + "-商品A", unit = "PCS" });
        using var response = await client.PostAsync("/api/base/products",
            new StringContent(payload, Encoding.UTF8, "application/json"));
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(0, doc.RootElement.GetProperty("code").GetInt32());
        return doc.RootElement.GetProperty("data").GetProperty("id").GetInt64();
    }

    private static async Task<long> CreateCustomerAsync(HttpClient client, string tag)
    {
        var payload = JsonSerializer.Serialize(new { customerCode = "C-HTTP429-" + tag, customerName = tag, currency = "USD" });
        using var response = await client.PostAsync("/api/base/customers",
            new StringContent(payload, Encoding.UTF8, "application/json"));
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(0, doc.RootElement.GetProperty("code").GetInt32());
        return doc.RootElement.GetProperty("data").GetProperty("id").GetInt64();
    }

    /// <summary>
    /// 经既有销售订单接口产生<b>合法已审核来源</b>（新建 → 提交 → 审核），明细使用同一活商品；
    /// 不新增任何接口 / 授权 / 菜单，绝不伪造审核状态。
    /// </summary>
    private static async Task<(long Id, string No)> CreateSalesSourceAsync(
        HttpClient client, string tag, long customerId, long productId, bool approve = true)
    {
        var body = JsonSerializer.Serialize(new
        {
            orderDate = DateTime.Today.ToString("yyyy-MM-dd"),
            customerId,
            currency = "USD",
            exchangeRate = 7.2m,
            customerPoNo = "PO-HTTP429-" + tag,
            contractNo = "SC-HTTP429-" + tag,
            details = new object[]
            {
                new { productId, productName = "HTTP429 来源商品", spec = "中", unit = "PCS", quantity = 10m, unitPrice = 5m }
            }
        });
        using var create = await client.PostAsync("/api/sales-orders",
            new StringContent(body, Encoding.UTF8, "application/json"));
        using var created = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
        Assert.Equal(0, created.RootElement.GetProperty("code").GetInt32());
        var data = created.RootElement.GetProperty("data");
        var id = data.GetProperty("id").GetInt64();
        var no = data.GetProperty("orderNo").GetString()!;

        if (approve)
        {
            using var submit = await client.PostAsync($"/api/sales-orders/{id}/submit", null);
            using var submitDoc = JsonDocument.Parse(await submit.Content.ReadAsStringAsync());
            Assert.Equal(0, submitDoc.RootElement.GetProperty("code").GetInt32());
            using var approveResponse = await client.PostAsync($"/api/sales-orders/{id}/approve", null);
            using var approved = JsonDocument.Parse(await approveResponse.Content.ReadAsStringAsync());
            Assert.Equal(0, approved.RootElement.GetProperty("code").GetInt32());
        }

        return (id, no);
    }

    /// <summary>
    /// 手工采购正文：可省略单号、可省略 / 显式 null / 显式非法来源与起运港
    /// （与前端 crud.js 对声明可选数值字段留空时提交 null 的正文同形）。
    /// </summary>
    private static Dictionary<string, object?> OrderBody(long supplierId, long productId, string contractNo,
        string? orderNo = null, long? owningSalesOrderId = null, bool includeSourceKey = false,
        long? portId = null, bool includePortKey = false,
        decimal quantity = 10m, decimal unitPrice = 5m, decimal taxRate = 0m)
    {
        var body = new Dictionary<string, object?>
        {
            ["orderDate"] = DateTime.Today.ToString("yyyy-MM-dd"),
            ["supplierId"] = supplierId,
            ["currency"] = "CNY",
            ["exchangeRate"] = 1m,
            ["taxRate"] = taxRate,
            ["contractNo"] = contractNo,
            ["paymentTerms"] = "月结 30 天",
            ["details"] = new object[]
            {
                new { productId, productName = "HTTP 采购商品", spec = "中", unit = "PCS", quantity, unitPrice }
            }
        };
        if (orderNo is not null) body["orderNo"] = orderNo;
        if (includeSourceKey) body["owningSalesOrderId"] = owningSalesOrderId;
        if (includePortKey) body["portId"] = portId;
        return body;
    }

    // ==================== 1. 省略 / 伪造单号（真实 HTTP） ====================

    [Fact]
    public async Task 省略采购单号_真实HTTP保存成功_服务端权威单号落库()
    {
        Guard();
        var tag = Tag();
        using var admin = await _fx.LoginAsAdminAsync();
        var supplierId = await CreateSupplierAsync(admin, tag);
        var productId = await CreateProductAsync(admin, tag);
        var contractNo = "HTTP429-OMIT-" + tag;

        var (status, root, raw) = await PostOrderAsync(admin, OrderBody(supplierId, productId, contractNo));

        // 修复前：绑定层因 [Required] 返回 HTTP 400（OrderNo field is required）；现在必须成功。
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(0, root.GetProperty("code").GetInt32());
        Assert.DoesNotContain("OrderNo field is required", raw, StringComparison.OrdinalIgnoreCase);

        var persisted = await _fx.ReadPersistedAsync(contractNo);
        Assert.Equal(1, persisted.Count);
        Assert.StartsWith("PO", persisted.OrderNo, StringComparison.Ordinal);
        Assert.True(persisted.OrderNo!.Length is > 0 and <= 50);
        Assert.Equal(root.GetProperty("data").GetProperty("orderNo").GetString(), persisted.OrderNo);
        // 未关联手工采购：可选引用保持 NULL。
        Assert.Null(persisted.OwningSalesOrderId);
        Assert.Null(persisted.PortId);
        Assert.Equal(1, persisted.DetailCount);
        Assert.Equal(50m, persisted.TotalAmount);
    }

    [Fact]
    public async Task 伪造采购单号_真实HTTP_持久化号码由服务端权威生成()
    {
        Guard();
        var tag = Tag();
        using var admin = await _fx.LoginAsAdminAsync();
        var supplierId = await CreateSupplierAsync(admin, tag);
        var productId = await CreateProductAsync(admin, tag);
        var contractNo = "HTTP429-FORGE-" + tag;
        var forged = "HACK-HTTP429-" + tag;

        var (status, root, _) = await PostOrderAsync(admin,
            OrderBody(supplierId, productId, contractNo, orderNo: forged));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(0, root.GetProperty("code").GetInt32());

        var persisted = await _fx.ReadPersistedAsync(contractNo);
        Assert.Equal(1, persisted.Count);
        Assert.StartsWith("PO", persisted.OrderNo, StringComparison.Ordinal);
        Assert.NotEqual(forged, persisted.OrderNo);
    }

    // ==================== 2. 可选归属来源与起运港留空（真实 HTTP） ====================

    [Fact]
    public async Task 手工采购_省略或显式null的可选引用_真实HTTP持久化为NULL()
    {
        Guard();
        var tag = Tag();
        using var admin = await _fx.LoginAsAdminAsync();
        var supplierId = await CreateSupplierAsync(admin, tag);
        var productId = await CreateProductAsync(admin, tag);

        // (a) 真正省略两个字段。
        var omittedContract = "HTTP429-OMITNULL-" + tag;
        var (omittedStatus, omittedRoot, _) = await PostOrderAsync(admin,
            OrderBody(supplierId, productId, omittedContract));
        Assert.Equal(HttpStatusCode.OK, omittedStatus);
        Assert.Equal(0, omittedRoot.GetProperty("code").GetInt32());
        var omitted = await _fx.ReadPersistedAsync(omittedContract);
        Assert.Equal(1, omitted.Count);
        Assert.Null(omitted.OwningSalesOrderId);
        Assert.Null(omitted.PortId);

        // (b) 显式 null：与前端 crud.js 可选数值字段留空时提交 null 的正文一致。
        var nullContract = "HTTP429-EXPLNULL-" + tag;
        var (nullStatus, nullRoot, _) = await PostOrderAsync(admin,
            OrderBody(supplierId, productId, nullContract, includeSourceKey: true, includePortKey: true));
        Assert.Equal(HttpStatusCode.OK, nullStatus);
        Assert.Equal(0, nullRoot.GetProperty("code").GetInt32());
        var explicitNull = await _fx.ReadPersistedAsync(nullContract);
        Assert.Equal(1, explicitNull.Count);
        Assert.Null(explicitNull.OwningSalesOrderId);
        Assert.Null(explicitNull.PortId);
    }

    // ==================== 3. 关联真实已审核来源（真实 HTTP + 直接 SQL） ====================

    [Fact]
    public async Task 关联已审核来源_真实HTTP保存_权威来源快照与执行结算字段落库()
    {
        Guard();
        var tag = Tag();
        using var admin = await _fx.LoginAsAdminAsync();
        var customerId = await CreateCustomerAsync(admin, tag);
        var supplierId = await CreateSupplierAsync(admin, tag);
        var productId = await CreateProductAsync(admin, tag);
        var (salesOrderId, salesOrderNo) = await CreateSalesSourceAsync(admin, tag, customerId, productId);
        var contractNo = "HTTP429-LINK-" + tag;

        var (status, root, _) = await PostOrderAsync(admin,
            OrderBody(supplierId, productId, contractNo, owningSalesOrderId: salesOrderId, includeSourceKey: true));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(0, root.GetProperty("code").GetInt32());

        var persisted = await _fx.ReadPersistedAsync(contractNo);
        Assert.Equal(1, persisted.Count);
        Assert.StartsWith("PO", persisted.OrderNo, StringComparison.Ordinal);
        Assert.Equal(salesOrderId, persisted.OwningSalesOrderId);
        Assert.Equal(salesOrderNo, persisted.OwningSalesOrderNo);
        Assert.Equal(customerId, persisted.OwningCustomerId);
        Assert.Equal(1, persisted.DetailCount);
        Assert.Equal(50m, persisted.TotalAmount);
    }

    [Fact]
    public async Task 关联未审核来源_真实HTTP受控拒绝且零写入()
    {
        Guard();
        var tag = Tag();
        using var admin = await _fx.LoginAsAdminAsync();
        var customerId = await CreateCustomerAsync(admin, tag);
        var supplierId = await CreateSupplierAsync(admin, tag);
        var productId = await CreateProductAsync(admin, tag);
        var (pendingId, _) = await CreateSalesSourceAsync(admin, tag, customerId, productId, approve: false);
        var contractNo = "HTTP429-PENDING-" + tag;

        var (status, root, _) = await PostOrderAsync(admin,
            OrderBody(supplierId, productId, contractNo, owningSalesOrderId: pendingId, includeSourceKey: true));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(1004, root.GetProperty("code").GetInt32());   // ErrorCodes.RuleConflict
        Assert.Equal(0, (await _fx.ReadPersistedAsync(contractNo)).Count);
    }

    // ==================== 4. 可控非法引用（真实 HTTP，零写入） ====================

    [Theory]
    [InlineData(0L)]
    [InlineData(-3L)]
    public async Task 可控非法来源_零或负数_真实HTTP按参数错误拒绝且零写入(long sourceId)
    {
        Guard();
        var tag = Tag();
        using var admin = await _fx.LoginAsAdminAsync();
        var supplierId = await CreateSupplierAsync(admin, tag);
        var productId = await CreateProductAsync(admin, tag);
        var contractNo = "HTTP429-SRCBAD-" + tag;

        var (status, root, _) = await PostOrderAsync(admin,
            OrderBody(supplierId, productId, contractNo, owningSalesOrderId: sourceId, includeSourceKey: true));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(1001, root.GetProperty("code").GetInt32());   // ErrorCodes.InvalidParameter
        Assert.Equal(0, (await _fx.ReadPersistedAsync(contractNo)).Count);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-3L)]
    public async Task 可控非法起运港_零或负数_真实HTTP按参数错误拒绝且零写入(long portId)
    {
        Guard();
        var tag = Tag();
        using var admin = await _fx.LoginAsAdminAsync();
        var supplierId = await CreateSupplierAsync(admin, tag);
        var productId = await CreateProductAsync(admin, tag);
        var contractNo = "HTTP429-PORTBAD-" + tag;

        var (status, root, _) = await PostOrderAsync(admin,
            OrderBody(supplierId, productId, contractNo, portId: portId, includePortKey: true));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(1001, root.GetProperty("code").GetInt32());
        Assert.Equal(0, (await _fx.ReadPersistedAsync(contractNo)).Count);
    }

    [Fact]
    public async Task 可控非法引用_起运港不存在_真实HTTP按不存在拒绝且零写入()
    {
        Guard();
        var tag = Tag();
        using var admin = await _fx.LoginAsAdminAsync();
        var supplierId = await CreateSupplierAsync(admin, tag);
        var productId = await CreateProductAsync(admin, tag);
        var contractNo = "HTTP429-PORTMISSING-" + tag;

        var (status, root, _) = await PostOrderAsync(admin,
            OrderBody(supplierId, productId, contractNo, portId: 999_999_999L, includePortKey: true));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(1002, root.GetProperty("code").GetInt32());   // ErrorCodes.NotFound
        Assert.Equal(0, (await _fx.ReadPersistedAsync(contractNo)).Count);
    }

    [Fact]
    public async Task 可控非法引用_供应商不存在_真实HTTP按不存在拒绝且零写入()
    {
        Guard();
        var tag = Tag();
        using var admin = await _fx.LoginAsAdminAsync();
        var productId = await CreateProductAsync(admin, tag);
        var contractNo = "HTTP429-SUPMISSING-" + tag;

        var (status, root, _) = await PostOrderAsync(admin, OrderBody(999_999_999L, productId, contractNo));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(1002, root.GetProperty("code").GetInt32());
        Assert.Equal(0, (await _fx.ReadPersistedAsync(contractNo)).Count);
    }

    [Fact]
    public async Task 可控非法引用_商品不存在_真实HTTP按不存在拒绝且零写入()
    {
        Guard();
        var tag = Tag();
        using var admin = await _fx.LoginAsAdminAsync();
        var supplierId = await CreateSupplierAsync(admin, tag);
        var contractNo = "HTTP429-PRODMISSING-" + tag;

        var (status, root, _) = await PostOrderAsync(admin, OrderBody(supplierId, 999_999_999L, contractNo));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(1002, root.GetProperty("code").GetInt32());
        Assert.Equal(0, (await _fx.ReadPersistedAsync(contractNo)).Count);
    }

    // ==================== 5. 数量 / 单价 / 税率（真实 HTTP，零写入） ====================

    [Theory]
    [InlineData(0.0, 5.0, 0.0)]
    [InlineData(10.0, -1.0, 0.0)]
    [InlineData(10.0, 5.0, 200.0)]
    public async Task 非法数量单价或税率_真实HTTP按参数错误拒绝且零写入(double quantity, double unitPrice, double taxRate)
    {
        Guard();
        var tag = Tag();
        using var admin = await _fx.LoginAsAdminAsync();
        var supplierId = await CreateSupplierAsync(admin, tag);
        var productId = await CreateProductAsync(admin, tag);
        var contractNo = "HTTP429-TERMS-" + tag;

        var (status, root, _) = await PostOrderAsync(admin, OrderBody(supplierId, productId, contractNo,
            quantity: (decimal)quantity, unitPrice: (decimal)unitPrice, taxRate: (decimal)taxRate));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(1001, root.GetProperty("code").GetInt32());   // ErrorCodes.InvalidParameter
        Assert.Equal(0, (await _fx.ReadPersistedAsync(contractNo)).Count);
    }

    // ==================== 6. 权限拒绝（真实 HTTP，零写入） ====================

    [Fact]
    public async Task 无身份_真实HTTP_按未认证拒绝且零写入()
    {
        Guard();
        var tag = Tag();
        using var admin = await _fx.LoginAsAdminAsync();
        var supplierId = await CreateSupplierAsync(admin, tag);
        var productId = await CreateProductAsync(admin, tag);
        var contractNo = "HTTP429-ANON-" + tag;

        using var anonymous = _fx.CreateAnonymousClient();
        var (status, _, _) = await PostOrderAsync(anonymous, OrderBody(supplierId, productId, contractNo));

        Assert.Equal(HttpStatusCode.Unauthorized, status);
        Assert.Equal(0, (await _fx.ReadPersistedAsync(contractNo)).Count);
    }

    [Fact]
    public async Task 受限账号_缺采购订单菜单_真实HTTP_按权限不足拒绝且零写入()
    {
        Guard();
        var tag = Tag();
        using var admin = await _fx.LoginAsAdminAsync();
        var supplierId = await CreateSupplierAsync(admin, tag);
        var productId = await CreateProductAsync(admin, tag);
        var contractNo = "HTTP429-DENIED-" + tag;

        // 经既有用户管理接口新建账号且不分配任何角色：没有既有「采购订单」菜单授权（不新增任何授权 / 菜单）。
        var userName = "http429-" + tag;
        var password = "Http429@" + tag;
        await _fx.CreateUserWithoutRolesAsync(admin, userName, password);

        using var restricted = await _fx.LoginAsync(userName, password);
        var (status, root, _) = await PostOrderAsync(restricted, OrderBody(supplierId, productId, contractNo));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(2002, root.GetProperty("code").GetInt32());   // ErrorCodes.Forbidden
        Assert.Equal(0, (await _fx.ReadPersistedAsync(contractNo)).Count);
    }
}

/// <summary>
/// ERP-429 真实 HTTP + 真实隔离 SQL 夹具：目标必须是专用实例 <c>(localdb)\NEWERP_AutoAcceptance</c>、
/// 库名前缀 <c>NEWERP_AUTOTEST</c> 且 <c>Integrated Security=true</c>；只使用<strong>全新 GUID 后缀库</strong>，
/// 发现同名库已存在立即拒绝（绝不 drop / reset / 复用）；连接串只来自进程环境变量或专用 localdb 默认值。
/// 启动真实 ERP.Api 进程并在就绪后提供已登录 / 匿名 <see cref="HttpClient"/> 与直接 SQL 复核入口。
/// </summary>
public sealed class PurchaseOrderHttpFormSqlServerFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";
    public const string AdminUser = "admin";
    public const string AdminPassword = "Admin@123";

    private const string DefaultDatabaseName = DatabasePrefix + "_PURCHASEORDERHTTPFORM";
    private const string PortVariable = "ERP_429_HTTP_PORT";
    private const string DefaultPort = "5278";

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
        TestDatabaseSafetyGuard.EnsureApprovedTarget(connectionString, "采购订单 HTTP 表单集成验收");
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

    /// <summary>按采购合同号直接读取持久化采购订单（单号 / 归属来源 / 客户快照 / 起运港 / 总额 / 明细数）。</summary>
    public async Task<PersistedPurchase> ReadPersistedAsync(string contractNo)
    {
        AssertDedicatedTarget(ConnectionString);
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT o.OrderNo, o.OwningSalesOrderId, o.OwningSalesOrderNo, o.OwningCustomerId, o.OwningCustomerName,"
            + " o.PortId, o.TotalAmount,"
            + " (SELECT COUNT(*) FROM db_owner.PurchaseOrderDetails d"
            + "   WHERE d.PurchaseOrderId = o.Id AND d.IsDeleted = 0)"
            + " FROM db_owner.PurchaseOrders o WHERE o.ContractNo = @c AND o.IsDeleted = 0";
        cmd.Parameters.AddWithValue("@c", contractNo);
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return new PersistedPurchase(0, null, null, null, null, null, null, 0m, 0);

        return new PersistedPurchase(1,
            reader.IsDBNull(0) ? null : reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetInt64(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetInt64(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetInt64(5),
            reader.IsDBNull(6) ? 0m : reader.GetDecimal(6),
            reader.GetInt32(7));
    }

    /// <summary>直接 SQL 复核结果：单号（服务端权威）/ 归属来源与客户快照 / 起运港 / 总额 / 有效明细数。</summary>
    public sealed record PersistedPurchase(int Count, string? OrderNo, long? OwningSalesOrderId,
        string? OwningSalesOrderNo, long? OwningCustomerId, string? OwningCustomerName,
        long? PortId, decimal TotalAmount, int DetailCount);

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
            File.WriteAllText(Path.Combine(evidenceDirectory, "purchase-http-form-api-console.log"),
                ApiConsoleTail(int.MaxValue));
        }
        catch (IOException) { /* 证据写入失败不影响断言结果 */ }
    }
}

/// <summary>专用目标护栏的 fail-closed 覆盖：错误实例 / 错误库名 / 非集成安全必须在访问数据库之前被拒绝。</summary>
public sealed class PurchaseOrderHttpFormTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;User Id=sa;Password=secret")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(
            () => PurchaseOrderHttpFormSqlServerFixture.AssertDedicatedTarget(connection));
}
