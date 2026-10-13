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
/// ERP-483 客诉单规范入口（<c>api/finance/complaints</c>）的<b>真实 HTTP + 真实隔离 SQL</b> 验收
/// （真实 ERP.Api 进程 + 全新 GUID <c>NEWERP_AUTOTEST</c> 库）：
/// <list type="number">
/// <item><b>真实 HTTP</b>：请求经真实 Kestrel 管线（MVC 绑定 + <c>[ApiController]</c> 模型校验 + JWT 认证 + 既有授权），
/// 因此「省略客诉单号」由<b>绑定层</b>证明（修复前正是绑定层因 <c>[Required]</c> 返回 HTTP 400
/// <c>{"errors":{"ComplaintNo":[...]}}</c>），直接实例化控制器的测试无法证明这一点；夹具为真实 API 进程注入
/// <b>进程内临时 JWT 签名密钥</b>（随机生成、只存活在该进程、绝不落盘 / 绝不继承空白签名配置），认证链路保持真实。</item>
/// <item><b>权威单号与规范 Id</b>：省略客诉单号一律持久化服务端生成单号（<c>KS</c> 前缀），返回规范 <c>Id</c>；
/// 回读（详情 / 列表）与修改都按规范 <c>Id</c> 往返。</item>
/// <item><b>可空来源与显式断开</b>：来源销售订单可显式关联 / 显式清空（<c>null</c> = 未关联历史语义），
/// 备注与文本字段保存 → 重开 → 修改原样往返；跨客户 / 已取消 / 悬空来源一律受控拒绝且零改写。</item>
/// <item><b>零副作用拒绝</b>：匿名 / 缺菜单 / 无效 · 已停用客户 / 超长文本一律受控拒绝，且客诉单行、单据号序列与
/// 销售订单 / 库存 / 收款 / 付款 / 费用分摊记录零变化；有效状态变更同样不产生退款 / 库存 / 财务记录。</item>
/// <item><b>专用目标护栏</b>：必须在任何数据库访问之前精确命中 <c>(localdb)\NEWERP_AutoAcceptance</c> + 库名前缀
/// <c>NEWERP_AUTOTEST</c> + <c>Integrated Security=true</c>；每次运行只创建一个全新 GUID 后缀库，发现同名库已存在
/// 立即拒绝，绝不 drop / reset / 复用任何数据库，也绝不读取 appsettings / .env / 生产凭据。</item>
/// </list>
/// <para>客户 / 销售订单为<b>夹具数据</b>（EF 直接播种到本次全新隔离库的既有 <c>BaseCustomers</c> /
/// <c>SalesOrders</c> 表），被测写入（客诉创建 / 修改 / 提交 / 审核 / 取消）全部经真实 HTTP；
/// 构建完成不等于阶段验收。</para>
/// </summary>
public sealed class ComplaintCanonicalEntrySqlServerTests
    : IClassFixture<ComplaintCanonicalEntrySqlServerFixture>
{
    private readonly ComplaintCanonicalEntrySqlServerFixture _fx;

    public ComplaintCanonicalEntrySqlServerTests(ComplaintCanonicalEntrySqlServerFixture fixture) => _fx = fixture;

    /// <summary>访问数据库前复核专用目标护栏（实例名 / 库名前缀 / 集成安全）。</summary>
    private void Guard()
    {
        var target = new SqlConnectionStringBuilder(_fx.ConnectionString);
        Assert.Equal($"(localdb)\\{ComplaintCanonicalEntrySqlServerFixture.InstanceMarker}",
            target.DataSource, ignoreCase: true);
        Assert.StartsWith(ComplaintCanonicalEntrySqlServerFixture.DatabasePrefix, target.InitialCatalog,
            StringComparison.OrdinalIgnoreCase);
        Assert.True(target.IntegratedSecurity);
    }

    private static string Tag() => Guid.NewGuid().ToString("N")[..8];

    // ==================== HTTP 脚手架 ====================

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

    /// <summary>客诉单正文：与前端 crud.js 提交同形（**省略 complaintNo**；来源销售订单字段可为 <c>null</c>）。</summary>
    private static Dictionary<string, object?> ComplaintBody(
        long customerId, long? salesOrderId = null, string complaintType = "质量",
        string description = "HTTP483", string responsibleDept = "质检部",
        string handleResult = "", string remark = "HTTP483", DateTime? complaintDate = null)
        => new()
        {
            ["complaintDate"] = (complaintDate ?? DateTime.Today).ToString("yyyy-MM-dd"),
            ["customerId"] = customerId,
            ["salesOrderId"] = salesOrderId,
            ["complaintType"] = complaintType,
            ["description"] = description,
            ["responsibleDept"] = responsibleDept,
            ["handleResult"] = handleResult,
            ["remark"] = remark
        };
    // ==================== 1. 省略客诉单号：真实 HTTP 新增 / 回读 / 修改 ====================

    [Fact]
    public async Task 省略客诉单号_真实HTTP新增回读修改_服务端权威单号与默认状态()
    {
        Guard();
        var tag = Tag();
        using var admin = await _fx.LoginAsAdminAsync();
        var (user, password) = await _fx.CreateComplaintOperatorAsync(admin, "cc483-op-" + tag, "Test@483" + tag);
        using var op = await _fx.LoginAsync(user, password);
        var customerId = await _fx.SeedCustomerAsync("CC483-C-" + tag);

        var (createdStatus, createdRoot, raw) = await SendAsync(op, HttpMethod.Post,
            "/api/finance/complaints", ComplaintBody(customerId));
        Assert.Equal(HttpStatusCode.OK, createdStatus);
        Assert.Equal(0, createdRoot.GetProperty("code").GetInt32());
        Assert.DoesNotContain("is required", raw, StringComparison.OrdinalIgnoreCase);

        var id = createdRoot.GetProperty("data").GetProperty("id").GetInt64();
        var number = createdRoot.GetProperty("data").GetProperty("complaintNo").GetString()!;
        Assert.True(id > 0);
        Assert.StartsWith("KS", number, StringComparison.Ordinal);

        var persisted = await _fx.ReadComplaintByIdAsync(id);
        Assert.Equal(1, persisted.Count);
        Assert.Equal(number, persisted.ComplaintNo);
        Assert.Equal(0, persisted.Status);                       // Pending
        Assert.False(persisted.IsDeleted);
        Assert.Equal(customerId, persisted.CustomerId);
        Assert.Null(persisted.SalesOrderId);
        Assert.Equal("质检部", persisted.ResponsibleDept);
        Assert.Equal("HTTP483", persisted.Remark);

        // 详情 / 列表均按规范 Id 回读（表单重开口径），并显式标注「未关联来源」
        var (detailStatus, detailRoot, detailRaw) = await SendAsync(op, HttpMethod.Get,
            $"/api/finance/complaints/{id}");
        Assert.Equal(HttpStatusCode.OK, detailStatus);
        Assert.Equal(0, detailRoot.GetProperty("code").GetInt32());
        Assert.Equal(id, detailRoot.GetProperty("data").GetProperty("id").GetInt64());
        Assert.Contains("未关联", detailRaw);

        var (listStatus, listRoot, _) = await SendAsync(op, HttpMethod.Get,
            "/api/finance/complaints?page=1&pageSize=20");
        Assert.Equal(HttpStatusCode.OK, listStatus);
        Assert.Contains(listRoot.GetProperty("data").GetProperty("items").EnumerateArray(),
            i => i.GetProperty("id").GetInt64() == id);

        // 修改按规范 Id：单号 / 状态保持，文本与备注原样往返
        var (updateStatus, updateRoot, _) = await SendAsync(op, HttpMethod.Put,
            $"/api/finance/complaints/{id}",
            ComplaintBody(customerId, complaintType: "交付", description: "UPDATED",
                responsibleDept: "客服部", handleResult: "已处理", remark: "UPDATED"));
        Assert.Equal(HttpStatusCode.OK, updateStatus);
        Assert.Equal(0, updateRoot.GetProperty("code").GetInt32());

        var updated = await _fx.ReadComplaintByIdAsync(id);
        Assert.Equal(number, updated.ComplaintNo);
        Assert.Equal(0, updated.Status);
        Assert.Equal("交付", updated.ComplaintType);
        Assert.Equal("UPDATED", updated.Description);
        Assert.Equal("客服部", updated.ResponsibleDept);
        Assert.Equal("已处理", updated.HandleResult);
        Assert.Equal("UPDATED", updated.Remark);
    }

    // ==================== 2. 来源销售订单：显式关联 / 显式断开 / 非法来源拒绝 ====================

    [Fact]
    public async Task 来源销售订单_真实HTTP关联与显式断开_跨客户与已取消来源拒绝()
    {
        Guard();
        var tag = Tag();
        using var admin = await _fx.LoginAsAdminAsync();
        var (user, password) = await _fx.CreateComplaintOperatorAsync(admin, "cc483-src-" + tag, "Test@483" + tag);
        using var op = await _fx.LoginAsync(user, password);
        var customerId = await _fx.SeedCustomerAsync("CC483-S-" + tag);
        var otherCustomerId = await _fx.SeedCustomerAsync("CC483-X-" + tag);
        var orderId = await _fx.SeedSalesOrderAsync(customerId, "SO-CC483-" + tag);
        var foreignOrderId = await _fx.SeedSalesOrderAsync(otherCustomerId, "SO-CC483-X-" + tag);
        var cancelledOrderId = await _fx.SeedSalesOrderAsync(customerId, "SO-CC483-C-" + tag,
            DocumentStatus.Cancelled);

        // 显式关联权威来源订单
        var (_, createdRoot, _) = await SendAsync(op, HttpMethod.Post, "/api/finance/complaints",
            ComplaintBody(customerId, orderId, remark: "LINKED"));
        var id = createdRoot.GetProperty("data").GetProperty("id").GetInt64();
        var number = createdRoot.GetProperty("data").GetProperty("complaintNo").GetString()!;
        var linked = await _fx.ReadComplaintByIdAsync(id);
        Assert.Equal(orderId, linked.SalesOrderId);
        Assert.Equal(number, linked.ComplaintNo);

        var (_, _, linkedRaw) = await SendAsync(op, HttpMethod.Get, $"/api/finance/complaints/{id}");
        Assert.Contains("已关联", linkedRaw);

        // 显式断开（null = 未关联历史语义）：单号 / 状态不变，来源订单本身不被改写
        var (clearStatus, clearRoot, _) = await SendAsync(op, HttpMethod.Put,
            $"/api/finance/complaints/{id}", ComplaintBody(customerId, null, remark: "UNLINKED"));
        Assert.Equal(HttpStatusCode.OK, clearStatus);
        Assert.Equal(0, clearRoot.GetProperty("code").GetInt32());
        var cleared = await _fx.ReadComplaintByIdAsync(id);
        Assert.Null(cleared.SalesOrderId);
        Assert.Equal(number, cleared.ComplaintNo);
        Assert.Equal(0, cleared.Status);
        Assert.Equal("UNLINKED", cleared.Remark);
        Assert.Equal(DocumentStatus.Approved, await _fx.ReadSalesOrderStatusAsync(orderId));

        // 跨客户来源 / 已取消来源 / 悬空来源 → 受控拒绝，且零改写
        var foreign = await SendAsync(op, HttpMethod.Put, $"/api/finance/complaints/{id}",
            ComplaintBody(customerId, foreignOrderId, remark: "FOREIGN"));
        Assert.Equal(ErrorCodes.RuleConflict, foreign.Root.GetProperty("code").GetInt32());
        var cancelled = await SendAsync(op, HttpMethod.Put, $"/api/finance/complaints/{id}",
            ComplaintBody(customerId, cancelledOrderId, remark: "CANCELLED"));
        Assert.Equal(ErrorCodes.RuleConflict, cancelled.Root.GetProperty("code").GetInt32());
        var dangling = await SendAsync(op, HttpMethod.Put, $"/api/finance/complaints/{id}",
            ComplaintBody(customerId, 987654321L, remark: "DANGLING"));
        Assert.Equal(ErrorCodes.NotFound, dangling.Root.GetProperty("code").GetInt32());

        var afterRejects = await _fx.ReadComplaintByIdAsync(id);
        Assert.Null(afterRejects.SalesOrderId);
        Assert.Equal("UNLINKED", afterRejects.Remark);
        Assert.Equal(number, afterRejects.ComplaintNo);
        Assert.Equal(0, afterRejects.Status);
        Assert.Equal(DocumentStatus.Cancelled, await _fx.ReadSalesOrderStatusAsync(cancelledOrderId));
    }

    // ==================== 3. 拒绝先于编号与持久化：真实 HTTP 零写入零占号 ====================

    [Fact]
    public async Task 拒绝先于编号与持久化_真实HTTP零写入零占号()
    {
        Guard();
        var tag = Tag();
        using var admin = await _fx.LoginAsAdminAsync();
        var (user, password) = await _fx.CreateComplaintOperatorAsync(admin, "cc483-rej-" + tag, "Test@483" + tag);
        using var op = await _fx.LoginAsync(user, password);
        var customerId = await _fx.SeedCustomerAsync("CC483-R-" + tag);
        var disabledCustomerId = await _fx.SeedCustomerAsync("CC483-RD-" + tag, status: 0);

        var before = await _fx.ReadSnapshotAsync();

        // 停用客户 → 规则冲突（受控拒绝）
        var disabled = await SendAsync(op, HttpMethod.Post, "/api/finance/complaints",
            ComplaintBody(disabledCustomerId));
        Assert.Equal(HttpStatusCode.OK, disabled.Status);
        Assert.Equal(ErrorCodes.RuleConflict, disabled.Root.GetProperty("code").GetInt32());

        // 超长文本 → 在绑定层被既有 [MaxLength] 拒绝（HTTP 400 校验错误，不进入控制器，更不占号）
        var tooLong = await SendAsync(op, HttpMethod.Post, "/api/finance/complaints",
            ComplaintBody(customerId, complaintType: new string('x', 101)));
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.Status);
        Assert.Contains("errors", tooLong.Raw, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ComplaintType", tooLong.Raw, StringComparison.Ordinal);

        // 匿名 → 未认证（真实 JWT 认证链路，不是缺失密钥回落）
        using var anonymous = _fx.CreateAnonymousClient();
        var anonymousResult = await SendAsync(anonymous, HttpMethod.Post, "/api/finance/complaints",
            ComplaintBody(customerId));
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousResult.Status);
        Assert.Equal(ErrorCodes.Unauthorized, anonymousResult.Root.GetProperty("code").GetInt32());

        // 已认证但无「客诉单」菜单（真实既有授权）→ 模块授权拒绝
        var noMenuUser = "cc483-nomenu-" + tag;
        var noMenuPassword = "Test@483" + tag;
        await _fx.CreateUserWithoutRolesAsync(admin, noMenuUser, noMenuPassword);
        using var temporary = await _fx.LoginAsync(noMenuUser, noMenuPassword);
        var noMenu = await SendAsync(temporary, HttpMethod.Post, "/api/finance/complaints",
            ComplaintBody(customerId));
        Assert.Equal(HttpStatusCode.OK, noMenu.Status);
        Assert.Equal(ErrorCodes.Forbidden, noMenu.Root.GetProperty("code").GetInt32());

        var after = await _fx.ReadSnapshotAsync();
        Assert.Equal(before.ComplaintCount, after.ComplaintCount);
        Assert.Equal(before.Sequence, after.Sequence);        // 被拒请求绝不消耗客诉单号
        Assert.Equal(before.SalesOrderCount, after.SalesOrderCount);
        Assert.Equal(before.StockMovementCount, after.StockMovementCount);
        Assert.Equal(before.ReceiptCount, after.ReceiptCount);
        Assert.Equal(before.PaymentCount, after.PaymentCount);
        Assert.Equal(before.ExpenseAllocationLineCount, after.ExpenseAllocationLineCount);
    }

    // ==================== 4. 有效生命周期：真实 HTTP 提交 / 审核 / 取消 ====================

    [Fact]
    public async Task 生命周期_真实HTTP提交审核取消_合法流转且零退款零库存()
    {
        Guard();
        var tag = Tag();
        using var admin = await _fx.LoginAsAdminAsync();
        var (user, password) = await _fx.CreateComplaintOperatorAsync(admin, "cc483-life-" + tag, "Test@483" + tag);
        using var op = await _fx.LoginAsync(user, password);
        var customerId = await _fx.SeedCustomerAsync("CC483-L-" + tag);
        var orderId = await _fx.SeedSalesOrderAsync(customerId, "SO-CC483-L-" + tag);
        var before = await _fx.ReadSnapshotAsync();

        var (_, createdRoot, _) = await SendAsync(op, HttpMethod.Post, "/api/finance/complaints",
            ComplaintBody(customerId, orderId));
        var id = createdRoot.GetProperty("data").GetProperty("id").GetInt64();
        var number = createdRoot.GetProperty("data").GetProperty("complaintNo").GetString()!;

        var (submitStatus, submitRoot, _) = await SendAsync(op, HttpMethod.Post,
            $"/api/finance/complaints/{id}/submit");
        Assert.Equal(HttpStatusCode.OK, submitStatus);
        Assert.Equal(0, submitRoot.GetProperty("code").GetInt32());
        Assert.Equal(1, (await _fx.ReadComplaintByIdAsync(id)).Status);   // Submitted

        var (approveStatus, approveRoot, _) = await SendAsync(op, HttpMethod.Post,
            $"/api/finance/complaints/{id}/approve");
        Assert.Equal(HttpStatusCode.OK, approveStatus);
        Assert.Equal(0, approveRoot.GetProperty("code").GetInt32());
        Assert.Equal(2, (await _fx.ReadComplaintByIdAsync(id)).Status);   // Approved

        // 禁止状态修改：受控拒绝且原字段不变
        var mutate = await SendAsync(op, HttpMethod.Put, $"/api/finance/complaints/{id}",
            ComplaintBody(customerId, orderId, remark: "MUTATE"));
        Assert.Equal(ErrorCodes.RuleConflict, mutate.Root.GetProperty("code").GetInt32());
        var afterReject = await _fx.ReadComplaintByIdAsync(id);
        Assert.Equal("HTTP483", afterReject.Remark);
        Assert.Equal(number, afterReject.ComplaintNo);

        var (cancelStatus, cancelRoot, _) = await SendAsync(op, HttpMethod.Post,
            $"/api/finance/complaints/{id}/cancel");
        Assert.Equal(HttpStatusCode.OK, cancelStatus);
        Assert.Equal(0, cancelRoot.GetProperty("code").GetInt32());
        Assert.Equal(5, (await _fx.ReadComplaintByIdAsync(id)).Status);   // Cancelled

        // 取消后再推进状态 → 受控拒绝，零状态变化
        var resubmit = await SendAsync(op, HttpMethod.Post, $"/api/finance/complaints/{id}/submit");
        Assert.Equal(ErrorCodes.RuleConflict, resubmit.Root.GetProperty("code").GetInt32());
        var stored = await _fx.ReadComplaintByIdAsync(id);
        Assert.Equal(5, stored.Status);
        Assert.Equal(number, stored.ComplaintNo);
        Assert.False(stored.IsDeleted);
        Assert.Equal(orderId, stored.SalesOrderId);

        var after = await _fx.ReadSnapshotAsync();
        Assert.Equal(before.ComplaintCount + 1, after.ComplaintCount);
        // 客诉只是追溯证据：状态推进绝不改写订单，也绝不创建退款 / 收款 / 付款 / 库存 / 费用分摊记录
        Assert.Equal(before.SalesOrderCount, after.SalesOrderCount);
        Assert.Equal(DocumentStatus.Approved, await _fx.ReadSalesOrderStatusAsync(orderId));
        Assert.Equal(before.StockInCount, after.StockInCount);
        Assert.Equal(before.StockOutCount, after.StockOutCount);
        Assert.Equal(before.StockMovementCount, after.StockMovementCount);
        Assert.Equal(before.StockCount, after.StockCount);
        Assert.Equal(before.ReceiptCount, after.ReceiptCount);
        Assert.Equal(before.PaymentCount, after.PaymentCount);
        Assert.Equal(before.ExpenseAllocationLineCount, after.ExpenseAllocationLineCount);
    }

    // ==================== 5. 旧族分离：旧写路径 fail closed，规范入口不受影响 ====================

    [Fact]
    public async Task 旧族写路径fail_closed_规范入口不受影响()
    {
        Guard();
        var tag = Tag();
        using var admin = await _fx.LoginAsAdminAsync();
        var (user, password) = await _fx.CreateComplaintOperatorAsync(admin, "cc483-legacy-" + tag, "Test@483" + tag);
        using var op = await _fx.LoginAsync(user, password);
        var customerId = await _fx.SeedCustomerAsync("CC483-LG-" + tag);

        // 旧写路径（ERP-404 门禁）：即使具备既有功能菜单也整体 fail closed（绝不调用旧存储过程）
        var legacy = new Dictionary<string, object?>
        {
            ["Action"] = "add",
            ["BillNo"] = "LEGACY-HTTP483-" + tag,
            ["CustomerId"] = customerId,
            ["ComplaintType"] = "质量"
        };
        var legacyResult = await SendAsync(op, HttpMethod.Post, "/api/v2/bills/complaint/save", legacy);
        Assert.Equal(HttpStatusCode.OK, legacyResult.Status);
        Assert.NotEqual(0, legacyResult.Root.GetProperty("code").GetInt32());

        // 规范入口（api/finance/complaints，规范 Id 口径）在同一次运行中仍可正常写入
        var (_, createdRoot, _) = await SendAsync(op, HttpMethod.Post, "/api/finance/complaints",
            ComplaintBody(customerId));
        Assert.Equal(0, createdRoot.GetProperty("code").GetInt32());
        var id = createdRoot.GetProperty("data").GetProperty("id").GetInt64();
        Assert.StartsWith("KS", createdRoot.GetProperty("data").GetProperty("complaintNo").GetString()!,
            StringComparison.Ordinal);
        Assert.Equal(1, (await _fx.ReadComplaintByIdAsync(id)).Count);
    }

}

/// <summary>
/// ERP-483 真实 HTTP + 真实隔离 SQL 夹具：目标必须是专用实例 <c>(localdb)\NEWERP_AutoAcceptance</c>、
/// 库名前缀 <c>NEWERP_AUTOTEST</c> 与 <c>Integrated Security=true</c>；只使用<strong>全新 GUID 后缀库</strong>，
/// 发现同名库已存在立即拒绝（绝不 drop / reset / 复用）；连接串只来自进程环境变量或专用 localdb 默认值。
/// <para>启动真实 ERP.Api 进程（真实 Kestrel / JWT 认证 / 既有授权），并为该进程注入<b>进程内临时 JWT 签名密钥</b>
/// （随机生成、只存在于该进程的环境变量、绝不落盘、绝不复用仓库内空白签名配置）；测试账号仍是既有种子账号
/// （不新增 / 不持久化凭据）。</para>
/// <para>真实登录账号：经既有 <c>/api/sys/users</c> 新建账号（既有用户管理接口，不新增权限模型），再由夹具在
/// 本次全新隔离库内授予既有「客诉单」/「销售订单」菜单（系统内置角色 → 全客户范围，客诉菜单仍按既有口径显式授权）。</para>
/// </summary>
public sealed class ComplaintCanonicalEntrySqlServerFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";
    public const string AdminUser = "admin";
    public const string AdminPassword = "Admin@123";

    private const string DefaultDatabaseName = DatabasePrefix + "_COMPLAINTCANONICAL";
    private const string PortVariable = "ERP_483_HTTP_PORT";
    private const string DefaultPort = "5398";

    private readonly StringBuilder _apiConsole = new();
    private readonly object _apiConsoleLock = new();
    private Process? _apiProcess;

    public string ConnectionString { get; private set; } = string.Empty;
    public string BaseUrl { get; private set; } = string.Empty;

    /// <summary>按规范 Id 直接读取的客诉单行（单号 / 状态 / 删除态 / 客户 / 可空来源 / 文本）。</summary>
    public readonly record struct ComplaintRow(int Count, long Id, string? ComplaintNo, int Status, bool IsDeleted,
        long CustomerId, long? SalesOrderId, DateTime ComplaintDate, string? ComplaintType, string? Description,
        string? ResponsibleDept, string? HandleResult, string? Remark);

    /// <summary>零副作用拒绝 / 有效状态变更所需的整表计数快照（客诉 + 单号序列 + 订单 + 库存 · 库存流水 · 资金）。</summary>
    public readonly record struct WriteSnapshot(long ComplaintCount, long Sequence, long SalesOrderCount,
        long StockInCount, long StockOutCount, long StockMovementCount, long StockCount,
        long ReceiptCount, long PaymentCount, long ExpenseAllocationLineCount);

    public async Task InitializeAsync()
    {
        var configured = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        var connectionString = string.IsNullOrWhiteSpace(configured)
            ? $"Server=(localdb)\\{InstanceMarker};Initial Catalog={DefaultDatabaseName}_{Guid.NewGuid():N};"
              + "Integrated Security=true;TrustServerCertificate=true;"
            : configured;

        // 访问任何数据库之前：实例 / 库名前缀 / 集成安全 + 显式测试上下文（不满足立即 fail-closed）。
        AssertDedicatedTarget(connectionString);
        TestDatabaseSafetyGuard.EnsureApprovedTarget(connectionString, "客诉单规范入口集成验收");
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

    /// <summary>
    /// 新建真实登录账号并经既有 <c>/api/sys/users</c> 之后，在本次全新隔离库内授予既有「客诉单」与
    /// 「销售订单」菜单（系统内置角色 → 全客户范围；客诉单菜单仍按既有口径显式授权）。
    /// 不新增权限模型、不新增菜单、不新增角色类型。
    /// </summary>
    public async Task<(string UserName, string Password)> CreateComplaintOperatorAsync(
        HttpClient admin, string userName, string password)
    {
        await CreateUserWithoutRolesAsync(admin, userName, password);
        AssertDedicatedTarget(ConnectionString);

        await using var db = CreateDbContext();
        var userId = await db.SysUsers.Where(u => u.UserName == userName).Select(u => u.Id).FirstAsync();

        var role = new SysRole
        {
            RoleName = "ERP-483 客诉规范入口夹具角色",
            RoleCode = $"ERP483Fixture-{Guid.NewGuid():N}",
            IsSystem = true
        };
        db.SysRoles.Add(role);
        await db.SaveChangesAsync();
        db.SysUserRoles.Add(new SysUserRole { UserId = userId, RoleId = role.Id });
        await db.SaveChangesAsync();

        foreach (var code in new[] { "complaint", "sales-order" })
        {
            var menuId = await db.SysMenus.Where(m => m.MenuCode == code && !m.IsDeleted)
                .Select(m => m.Id).FirstAsync();
            db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menuId });
        }
        await db.SaveChangesAsync();
        return (userName, password);
    }


    // ==================== 夹具数据（既有主数据表，只写夹具行） ====================

    /// <summary>播种一张既有客户（夹具数据；不新增表 / 列 / 权限）。</summary>
    public async Task<long> SeedCustomerAsync(string code, int status = 1, bool deleted = false)
    {
        AssertDedicatedTarget(ConnectionString);
        await using var db = CreateDbContext();
        var customer = new BaseCustomer
        {
            CustomerCode = code[..Math.Min(code.Length, 30)],
            CustomerName = code,
            CreditStatus = "正常",
            Status = status,
            IsDeleted = deleted
        };
        db.BaseCustomers.Add(customer);
        await db.SaveChangesAsync();
        return customer.Id;
    }

    /// <summary>播种一张既有销售订单（夹具数据；用于来源链接资格验证）。</summary>
    public async Task<long> SeedSalesOrderAsync(
        long customerId, string orderNo, DocumentStatus status = DocumentStatus.Approved)
    {
        AssertDedicatedTarget(ConnectionString);
        await using var db = CreateDbContext();
        var order = new SalesOrder
        {
            OrderNo = orderNo[..Math.Min(orderNo.Length, 50)],
            OrderDate = DateTime.Today,
            CustomerId = customerId,
            Currency = Currency.USD,
            TotalAmount = 1000m,
            Status = status
        };
        db.SalesOrders.Add(order);
        await db.SaveChangesAsync();
        return order.Id;
    }

    // ==================== 真实 SQL 复核（同一全新 GUID 库，只读） ====================

    /// <summary>按规范 Id 读取客诉单行（真实 SQL Server，独立连接）。</summary>
    public async Task<ComplaintRow> ReadComplaintByIdAsync(long id)
    {
        AssertDedicatedTarget(ConnectionString);
        await using var db = CreateDbContext();
        var row = await db.FinanceComplaints.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id);
        if (row is null)
            return new ComplaintRow(0, 0, null, -1, false, 0L, null, default, null, null, null, null, null);

        return new ComplaintRow(1, row.Id, row.ComplaintNo, (int)row.Status, row.IsDeleted, row.CustomerId,
            row.SalesOrderId, row.ComplaintDate, row.ComplaintType, row.Description, row.ResponsibleDept,
            row.HandleResult, row.Remark);
    }

    /// <summary>读取销售订单权威状态（证明客诉生命周期绝不取消 / 改写来源订单）。</summary>
    public async Task<DocumentStatus> ReadSalesOrderStatusAsync(long id)
    {
        AssertDedicatedTarget(ConnectionString);
        await using var db = CreateDbContext();
        return await db.SalesOrders.AsNoTracking().Where(o => o.Id == id)
            .Select(o => o.Status).FirstAsync();
    }

    /// <summary>零副作用拒绝 / 有效状态变更所需的整表计数快照（客诉 + 单号序列 + 订单 + 库存 · 资金）。</summary>
    public async Task<WriteSnapshot> ReadSnapshotAsync()
    {
        AssertDedicatedTarget(ConnectionString);
        await using var db = CreateDbContext();
        var sequence = await db.SysDocumentNumberRules.AsNoTracking()
            .Where(r => r.DocumentType == DocumentType.Complaint && !r.IsDeleted)
            .Select(r => r.CurrentSequence)
            .FirstOrDefaultAsync();
        return new WriteSnapshot(
            await db.FinanceComplaints.CountAsync(),
            sequence,
            await db.SalesOrders.CountAsync(),
            await db.StockIns.CountAsync(),
            await db.StockOuts.CountAsync(),
            await db.StockMovements.CountAsync(),
            await db.Stocks.CountAsync(),
            await db.FinanceReceipts.CountAsync(),
            await db.FinancePayments.CountAsync(),
            await db.FinanceExpenseAllocationLines.CountAsync());
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
            // 连接被拒 = 端口空闲，正常继续。
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
            File.WriteAllText(Path.Combine(evidenceDirectory, "complaint-canonical-entry-api-console.log"),
                ApiConsoleTail(int.MaxValue));
        }
        catch (IOException) { /* 证据写入失败不影响断言结果 */ }
    }
}

/// <summary>专用目标护栏的 fail-closed 覆盖：错误实例 / 错误库名 / 非集成安全必须在访问数据库之前被拒绝。</summary>
public sealed class ComplaintCanonicalEntryTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;User Id=sa;Password=secret")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(
            () => ComplaintCanonicalEntrySqlServerFixture.AssertDedicatedTarget(connection));

    [Fact]
    public void Accepts_the_dedicated_localdb_target_with_integrated_security()
        => ComplaintCanonicalEntrySqlServerFixture.AssertDedicatedTarget(
            "Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_X;Integrated Security=true");
}

