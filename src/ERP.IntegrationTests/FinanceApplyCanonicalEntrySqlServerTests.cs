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
/// ERP-480 定金申请单 / 货款申请单规范入口的<b>真实 HTTP + 真实隔离 SQL</b> 验收（真实 ERP.Api 进程 + 全新 GUID
/// <c>NEWERP_AUTOTEST</c> 库）：
/// <list type="number">
/// <item><b>真实 HTTP</b>：请求经真实 Kestrel 管线（MVC 绑定 + <c>[ApiController]</c> 模型校验 + JWT 认证 + 既有授权），
/// 因此「省略申请单号」由<b>绑定层</b>证明（修复前正是绑定层因 <c>[Required]</c> 返回 HTTP 400
/// <c>{"errors":{"ApplyNo":[...]}}</c>），直接实例化控制器的测试无法证明这一点；夹具为真实 API 进程注入
/// <b>进程内临时 JWT 签名密钥</b>（随机生成、只存活在该进程、绝不落盘 / 绝不继承空白签名配置），认证链路保持真实。</item>
/// <item><b>权威单号与规范 Id</b>：省略 / 伪造单据号一律持久化服务端生成单号（<c>DJ</c> / <c>HK</c> 前缀），
/// 返回规范 <c>Id</c>；回读（详情 / 列表）与修改都按规范 <c>Id</c> 往返。</item>
/// <item><b>显式来源销售订单</b>：可显式关联既有销售订单（可空引用）并显式清空；既有来源候选接口按规范参数可达。</item>
/// <item><b>有效生命周期</b>：提交 → 审核 → 取消按既有授权执行；非法流转（待提交直接审核 / 重复取消）零状态变化。</item>
/// <item><b>零副作用拒绝</b>：无身份 / 缺菜单 / 非法币种与金额 / 客户不可用 / 来源悬空 · 已取消 · 跨客户 · 币种不一致
/// 一律受控拒绝，且申请单行与各自单号序列零变化。</item>
/// <item><b>专用目标护栏</b>：必须在任何数据库访问之前精确命中 <c>(localdb)\NEWERP_AutoAcceptance</c> + 库名前缀
/// <c>NEWERP_AUTOTEST</c> + <c>Integrated Security=true</c>；每次运行只创建一个全新 GUID 后缀库，发现同名库已存在立即拒绝，
/// 绝不 drop / reset / 复用任何数据库，也绝不读取 appsettings / .env / 生产凭据。</item>
/// </list>
/// <para>来源销售订单为<b>夹具数据</b>（EF 直接播种到本次全新隔离库的既有 <c>SalesOrders</c> 表），
/// 被测写入（申请单创建 / 修改 / 提交 / 审核 / 取消）全部经真实 HTTP；构建完成不等于阶段验收。</para>
/// </summary>
public sealed class FinanceApplyCanonicalEntrySqlServerTests
    : IClassFixture<FinanceApplyCanonicalEntrySqlServerFixture>
{
    private readonly FinanceApplyCanonicalEntrySqlServerFixture _fx;

    public FinanceApplyCanonicalEntrySqlServerTests(FinanceApplyCanonicalEntrySqlServerFixture fixture) => _fx = fixture;

    /// <summary>访问数据库前复核专用目标护栏（实例名 / 库名前缀 / 集成安全）。</summary>
    private void Guard()
    {
        var target = new SqlConnectionStringBuilder(_fx.ConnectionString);
        Assert.Equal($"(localdb)\\{FinanceApplyCanonicalEntrySqlServerFixture.InstanceMarker}",
            target.DataSource, ignoreCase: true);
        Assert.StartsWith(FinanceApplyCanonicalEntrySqlServerFixture.DatabasePrefix, target.InitialCatalog,
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

    /// <summary>定金申请单正文：与前端 crud.js 提交同形（可省略申请单号；来源字段可空）。</summary>
    private static Dictionary<string, object?> DepositApplyBody(
        long customerId, decimal amount = 1000m, string currency = "USD", string? applyNo = null,
        long? salesOrderId = null, decimal exchangeRate = 1m)
    {
        var body = new Dictionary<string, object?>
        {
            ["applyDate"] = DateTime.Today.ToString("yyyy-MM-dd"),
            ["customerId"] = customerId,
            ["amount"] = amount,
            ["currency"] = currency,
            ["exchangeRate"] = exchangeRate,
            ["bankAccount"] = "BANK-HTTP480",
            ["payee"] = "HTTP480 收款方",
            ["reason"] = "HTTP480 定金申请",
            ["remark"] = "HTTP480"
        };
        if (applyNo is not null) body["applyNo"] = applyNo;
        if (salesOrderId is not null) body["salesOrderId"] = salesOrderId;
        return body;
    }

    /// <summary>货款申请单正文：与前端 crud.js 提交同形（可省略申请单号；来源字段可空）。</summary>
    private static Dictionary<string, object?> PaymentApplyBody(
        long customerId, decimal amount = 1000m, string currency = "USD", string? applyNo = null,
        long? salesOrderId = null, decimal exchangeRate = 1m)
    {
        var body = new Dictionary<string, object?>
        {
            ["applyDate"] = DateTime.Today.ToString("yyyy-MM-dd"),
            ["customerId"] = customerId,
            ["amount"] = amount,
            ["currency"] = currency,
            ["exchangeRate"] = exchangeRate,
            ["bankAccount"] = "BANK-HTTP480",
            ["payee"] = "HTTP480 收款方",
            ["reason"] = "HTTP480 货款申请",
            ["remark"] = "HTTP480"
        };
        if (applyNo is not null) body["applyNo"] = applyNo;
        if (salesOrderId is not null) body["salesOrderId"] = salesOrderId;
        return body;
    }

    private static async Task<long> CreateCustomerAsync(HttpClient client, string tag)
    {
        var payload = JsonSerializer.Serialize(new
        {
            customerCode = "C-HTTP480-" + tag, customerName = "HTTP480 客户 " + tag, currency = "USD"
        });
        using var response = await client.PostAsync("/api/base/customers",
            new StringContent(payload, Encoding.UTF8, "application/json"));
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(0, doc.RootElement.GetProperty("code").GetInt32());
        return doc.RootElement.GetProperty("data").GetProperty("id").GetInt64();
    }

    private static async Task<(long Id, string No)> CreateDepositApplyAsync(HttpClient client, object body)
    {
        var (status, root, raw) = await SendAsync(client, HttpMethod.Post, "/api/finance/deposit-applies", body);
        Assert.True(status == HttpStatusCode.OK && root.GetProperty("code").GetInt32() == 0,
            "定金申请单创建失败（HTTP " + (int)status + "）：" + raw);
        var data = root.GetProperty("data");
        return (data.GetProperty("id").GetInt64(), data.GetProperty("applyNo").GetString()!);
    }

    private static async Task<(long Id, string No)> CreatePaymentApplyAsync(HttpClient client, object body)
    {
        var (status, root, raw) = await SendAsync(client, HttpMethod.Post, "/api/finance/payment-applies", body);
        Assert.True(status == HttpStatusCode.OK && root.GetProperty("code").GetInt32() == 0,
            "货款申请单创建失败（HTTP " + (int)status + "）：" + raw);
        var data = root.GetProperty("data");
        return (data.GetProperty("id").GetInt64(), data.GetProperty("applyNo").GetString()!);
    }

    // ==================== 1. 省略单据号 + 规范 Id 往返（真实 HTTP，绑定层证明） ====================

    [Fact]
    public async Task 省略定金申请单号_真实HTTP新增回读修改_服务端权威单号与默认状态()
    {
        Guard();
        var tag = Tag();
        using var admin = await _fx.LoginAsAdminAsync();
        var customerId = await CreateCustomerAsync(admin, tag);

        // 修复前：绑定层因 ApplyNo 的显式 [Required] 返回 HTTP 400；现在必须成功。
        var (createdStatus, createdRoot, raw) = await SendAsync(admin, HttpMethod.Post,
            "/api/finance/deposit-applies", DepositApplyBody(customerId, 1200.5m));
        Assert.Equal(HttpStatusCode.OK, createdStatus);
        Assert.Equal(0, createdRoot.GetProperty("code").GetInt32());
        Assert.DoesNotContain("ApplyNo field is required", raw, StringComparison.OrdinalIgnoreCase);

        var id = createdRoot.GetProperty("data").GetProperty("id").GetInt64();
        var number = createdRoot.GetProperty("data").GetProperty("applyNo").GetString()!;
        Assert.True(id > 0);                                     // 规范 Id（EF 自增主键）
        Assert.StartsWith("DJ", number, StringComparison.Ordinal);

        var persisted = await _fx.ReadDepositApplyByIdAsync(id);
        Assert.Equal(1, persisted.Count);
        Assert.Equal(number, persisted.No);
        Assert.Equal(0, persisted.Status);                       // Pending
        Assert.False(persisted.IsDeleted);
        Assert.Equal(1200.5m, persisted.Amount);
        Assert.Equal(customerId, persisted.CustomerId);
        Assert.Null(persisted.SalesOrderId);                     // 未关联来源 = 历史未链接语义

        // 详情 / 列表按规范 Id 回读一致（表单重开口径）。
        var (detailStatus, detailRoot, _) = await SendAsync(admin, HttpMethod.Get, $"/api/finance/deposit-applies/{id}");
        Assert.Equal(HttpStatusCode.OK, detailStatus);
        Assert.Equal(number, detailRoot.GetProperty("data").GetProperty("applyNo").GetString());
        var (listStatus, listRoot, _) = await SendAsync(admin, HttpMethod.Get,
            "/api/finance/deposit-applies?page=1&pageSize=5&keyword=" + Uri.EscapeDataString(number));
        Assert.Equal(HttpStatusCode.OK, listStatus);
        Assert.Equal(id, listRoot.GetProperty("data").GetProperty("items")[0].GetProperty("id").GetInt64());

        // 修改（伪造申请单号 / 状态 / 删除态 / 时间戳）：单号 / 状态 / 删除态保持服务端值。
        var editBody = DepositApplyBody(customerId, 900m, applyNo: "HACK-DJ-480");
        editBody["status"] = 5;
        editBody["isDeleted"] = true;
        editBody["createdAt"] = new DateTime(2000, 1, 1);
        var (editStatus, editRoot, _) = await SendAsync(admin, HttpMethod.Put, $"/api/finance/deposit-applies/{id}", editBody);
        Assert.Equal(HttpStatusCode.OK, editStatus);
        Assert.Equal(0, editRoot.GetProperty("code").GetInt32());

        var reloaded = await _fx.ReadDepositApplyByIdAsync(id);
        Assert.Equal(number, reloaded.No);
        Assert.Equal(0, reloaded.Status);
        Assert.False(reloaded.IsDeleted);
        Assert.Equal(900m, reloaded.Amount);
    }

    [Fact]
    public async Task 省略货款申请单号_真实HTTP新增回读修改_服务端权威单号与默认状态()
    {
        Guard();
        var tag = Tag();
        using var admin = await _fx.LoginAsAdminAsync();
        var customerId = await CreateCustomerAsync(admin, tag);

        var (createdStatus, createdRoot, raw) = await SendAsync(admin, HttpMethod.Post,
            "/api/finance/payment-applies", PaymentApplyBody(customerId, 1500m));
        Assert.Equal(HttpStatusCode.OK, createdStatus);
        Assert.Equal(0, createdRoot.GetProperty("code").GetInt32());
        Assert.DoesNotContain("ApplyNo field is required", raw, StringComparison.OrdinalIgnoreCase);

        var id = createdRoot.GetProperty("data").GetProperty("id").GetInt64();
        var number = createdRoot.GetProperty("data").GetProperty("applyNo").GetString()!;
        Assert.True(id > 0);
        Assert.StartsWith("HK", number, StringComparison.Ordinal);

        var persisted = await _fx.ReadPaymentApplyByIdAsync(id);
        Assert.Equal(1, persisted.Count);
        Assert.Equal(number, persisted.No);
        Assert.Equal(0, persisted.Status);
        Assert.False(persisted.IsDeleted);
        Assert.Equal(1500m, persisted.Amount);
        Assert.Equal(customerId, persisted.CustomerId);
        Assert.Null(persisted.SalesOrderId);

        var (detailStatus, detailRoot, _) = await SendAsync(admin, HttpMethod.Get, $"/api/finance/payment-applies/{id}");
        Assert.Equal(HttpStatusCode.OK, detailStatus);
        Assert.Equal(number, detailRoot.GetProperty("data").GetProperty("applyNo").GetString());

        var (editStatus, editRoot, _) = await SendAsync(admin, HttpMethod.Put, $"/api/finance/payment-applies/{id}",
            PaymentApplyBody(customerId, 1200m, applyNo: "HACK-HK-480"));
        Assert.Equal(HttpStatusCode.OK, editStatus);
        Assert.Equal(0, editRoot.GetProperty("code").GetInt32());

        var reloaded = await _fx.ReadPaymentApplyByIdAsync(id);
        Assert.Equal(number, reloaded.No);
        Assert.Equal(0, reloaded.Status);
        Assert.False(reloaded.IsDeleted);
        Assert.Equal(1200m, reloaded.Amount);
    }

    [Fact]
    public async Task 伪造申请单号_真实HTTP_持久化号码由服务端权威生成()
    {
        Guard();
        var tag = Tag();
        using var admin = await _fx.LoginAsAdminAsync();
        var customerId = await CreateCustomerAsync(admin, tag);

        var (depositId, depositNo) = await CreateDepositApplyAsync(admin,
            DepositApplyBody(customerId, 700m, applyNo: "HACK-DJ-HTTP480-" + tag));
        Assert.StartsWith("DJ", depositNo, StringComparison.Ordinal);
        Assert.DoesNotContain("HACK", depositNo, StringComparison.Ordinal);
        Assert.Equal(depositNo, (await _fx.ReadDepositApplyByIdAsync(depositId)).No);

        var (paymentId, paymentNo) = await CreatePaymentApplyAsync(admin,
            PaymentApplyBody(customerId, 700m, applyNo: "HACK-HK-HTTP480-" + tag));
        Assert.StartsWith("HK", paymentNo, StringComparison.Ordinal);
        Assert.DoesNotContain("HACK", paymentNo, StringComparison.Ordinal);
        Assert.Equal(paymentNo, (await _fx.ReadPaymentApplyByIdAsync(paymentId)).No);
    }

    // ==================== 2. 显式来源销售订单：关联 / 候选 / 显式清空 ====================

    [Fact]
    public async Task 显式来源销售订单_真实HTTP关联候选与显式清空_保留未链接语义()
    {
        Guard();
        var tag = Tag();
        using var admin = await _fx.LoginAsAdminAsync();
        var customerId = await CreateCustomerAsync(admin, tag);
        var otherCustomerId = await CreateCustomerAsync(admin, tag + "b");
        var orderId = await _fx.SeedSalesOrderAsync("SO-HTTP480-" + tag, customerId, "USD");
        await _fx.SeedSalesOrderAsync("SO-HTTP480-OTHER-" + tag, otherCustomerId, "USD");

        // 显式关联既有来源销售订单（可空引用，按 Id 精确解析）
        var (depositId, _) = await CreateDepositApplyAsync(admin,
            DepositApplyBody(customerId, 900m, salesOrderId: orderId));
        var linked = await _fx.ReadDepositApplyByIdAsync(depositId);
        Assert.Equal(orderId, linked.SalesOrderId);

        // 既有来源候选接口：按精确客户 + 归一化币种返回，范围外客户订单绝不出现
        var (candidateStatus, candidateRoot, _) = await SendAsync(admin, HttpMethod.Get,
            "/api/finance/deposit-applies/sales-order-candidates?customerId=" + customerId
            + "&currency=USD&page=1&pageSize=20");
        Assert.Equal(HttpStatusCode.OK, candidateStatus);
        Assert.Equal(0, candidateRoot.GetProperty("code").GetInt32());
        var candidate = candidateRoot.GetProperty("data").GetProperty("items").EnumerateArray()
            .Single(c => c.GetProperty("salesOrderId").GetInt64() == orderId);
        Assert.True(candidate.GetProperty("eligible").GetBoolean());
        Assert.DoesNotContain(candidateRoot.GetProperty("data").GetProperty("items").EnumerateArray(),
            c => c.GetProperty("customerId").GetInt64() == otherCustomerId);

        // 已存储来源只读展示按规范 Id 可达
        var (sourceStatus, sourceRoot, _) = await SendAsync(admin, HttpMethod.Get,
            $"/api/finance/deposit-applies/{depositId}/sales-order-source");
        Assert.Equal(HttpStatusCode.OK, sourceStatus);
        Assert.Equal(orderId, sourceRoot.GetProperty("data").GetProperty("salesOrderId").GetInt64());

        // 显式清空来源（提交 null = 声明式可空引用留空）：单号 / 状态不变，来源归一为未关联
        var clearedBody = DepositApplyBody(customerId, 900m);
        clearedBody["salesOrderId"] = null;
        var (clearStatus, clearRoot, _) = await SendAsync(admin, HttpMethod.Put,
            $"/api/finance/deposit-applies/{depositId}", clearedBody);
        Assert.Equal(HttpStatusCode.OK, clearStatus);
        Assert.Equal(0, clearRoot.GetProperty("code").GetInt32());
        var cleared = await _fx.ReadDepositApplyByIdAsync(depositId);
        Assert.Null(cleared.SalesOrderId);
        Assert.Equal(linked.No, cleared.No);
        Assert.Equal(0, cleared.Status);

        // 手工输入的哨兵值 0 同样归一为未关联（历史口径）
        await SendAsync(admin, HttpMethod.Put, $"/api/finance/deposit-applies/{depositId}",
            DepositApplyBody(customerId, 900m, salesOrderId: 0L));
        Assert.Null((await _fx.ReadDepositApplyByIdAsync(depositId)).SalesOrderId);

        // 货款申请单同样支持显式关联与清空
        var (paymentId, paymentNo) = await CreatePaymentApplyAsync(admin,
            PaymentApplyBody(customerId, 900m, salesOrderId: orderId));
        Assert.Equal(orderId, (await _fx.ReadPaymentApplyByIdAsync(paymentId)).SalesOrderId);
        var paymentClearBody = PaymentApplyBody(customerId, 900m);
        paymentClearBody["salesOrderId"] = null;
        await SendAsync(admin, HttpMethod.Put, $"/api/finance/payment-applies/{paymentId}", paymentClearBody);
        var clearedPayment = await _fx.ReadPaymentApplyByIdAsync(paymentId);
        Assert.Null(clearedPayment.SalesOrderId);
        Assert.Equal(paymentNo, clearedPayment.No);
    }

    // ==================== 3. 有效生命周期（提交 / 审核 / 取消）与非法流转 ====================

    [Fact]
    public async Task 有效生命周期_真实HTTP_提交审核取消与非法流转零状态变化()
    {
        Guard();
        var tag = Tag();
        using var admin = await _fx.LoginAsAdminAsync();
        var customerId = await CreateCustomerAsync(admin, tag);

        var (depositId, depositNo) = await CreateDepositApplyAsync(admin, DepositApplyBody(customerId, 800m));

        // 待提交直接审核 = 非法流转（零状态变化）
        var (earlyApproveStatus, earlyApproveRoot, _) = await SendAsync(admin, HttpMethod.Post,
            $"/api/finance/deposit-applies/{depositId}/approve");
        Assert.Equal(HttpStatusCode.OK, earlyApproveStatus);
        Assert.NotEqual(0, earlyApproveRoot.GetProperty("code").GetInt32());
        Assert.Equal(0, (await _fx.ReadDepositApplyByIdAsync(depositId)).Status);

        // 提交 → 审核 → 取消（既有状态流转）
        var (submitStatus, submitRoot, _) = await SendAsync(admin, HttpMethod.Post,
            $"/api/finance/deposit-applies/{depositId}/submit");
        Assert.Equal(HttpStatusCode.OK, submitStatus);
        Assert.Equal(0, submitRoot.GetProperty("code").GetInt32());
        Assert.Equal(1, (await _fx.ReadDepositApplyByIdAsync(depositId)).Status);

        var (approveStatus, approveRoot, _) = await SendAsync(admin, HttpMethod.Post,
            $"/api/finance/deposit-applies/{depositId}/approve");
        Assert.Equal(HttpStatusCode.OK, approveStatus);
        Assert.Equal(0, approveRoot.GetProperty("code").GetInt32());
        Assert.Equal(2, (await _fx.ReadDepositApplyByIdAsync(depositId)).Status);

        var (cancelStatus, cancelRoot, _) = await SendAsync(admin, HttpMethod.Post,
            $"/api/finance/deposit-applies/{depositId}/cancel");
        Assert.Equal(HttpStatusCode.OK, cancelStatus);
        Assert.Equal(0, cancelRoot.GetProperty("code").GetInt32());
        var cancelled = await _fx.ReadDepositApplyByIdAsync(depositId);
        Assert.Equal(5, cancelled.Status);                        // Cancelled
        Assert.Equal(depositNo, cancelled.No);
        Assert.Equal(800m, cancelled.Amount);
        Assert.False(cancelled.IsDeleted);

        // 重复取消拒绝且零状态变化
        var (repeatStatus, repeatRoot, _) = await SendAsync(admin, HttpMethod.Post,
            $"/api/finance/deposit-applies/{depositId}/cancel");
        Assert.Equal(HttpStatusCode.OK, repeatStatus);
        Assert.NotEqual(0, repeatRoot.GetProperty("code").GetInt32());
        Assert.Equal(5, (await _fx.ReadDepositApplyByIdAsync(depositId)).Status);

        // 货款申请单同样走既有生命周期（提交 → 审核）
        var (paymentId, paymentNo) = await CreatePaymentApplyAsync(admin, PaymentApplyBody(customerId, 600m));
        await SendAsync(admin, HttpMethod.Post, $"/api/finance/payment-applies/{paymentId}/submit");
        Assert.Equal(1, (await _fx.ReadPaymentApplyByIdAsync(paymentId)).Status);
        var (paymentApproveStatus, paymentApproveRoot, _) = await SendAsync(admin, HttpMethod.Post,
            $"/api/finance/payment-applies/{paymentId}/approve");
        Assert.Equal(HttpStatusCode.OK, paymentApproveStatus);
        Assert.Equal(0, paymentApproveRoot.GetProperty("code").GetInt32());
        var approved = await _fx.ReadPaymentApplyByIdAsync(paymentId);
        Assert.Equal(2, approved.Status);
        Assert.Equal(paymentNo, approved.No);
    }

    // ==================== 4. 零副作用拒绝（真实 HTTP + 真实 SQL 计数 / 占号） ====================

    [Theory]
    [InlineData("noIdentity")]
    [InlineData("missingMenu")]
    [InlineData("unknownCustomer")]
    [InlineData("badCurrency")]
    [InlineData("nonPositiveAmount")]
    [InlineData("danglingSource")]
    [InlineData("cancelledSource")]
    [InlineData("sourceCustomerMismatch")]
    [InlineData("currencyMismatch")]
    public async Task 定金申请非法请求_真实HTTP_受控拒绝且零写入零占号(string scenario)
    {
        Guard();
        var tag = Tag();
        using var admin = await _fx.LoginAsAdminAsync();
        var customerId = await CreateCustomerAsync(admin, tag);
        var otherCustomerId = await CreateCustomerAsync(admin, tag + "x");
        var cancelledOrderId = await _fx.SeedSalesOrderAsync(
            "SO-HTTP480-CANCEL-" + tag, customerId, "USD", DocumentStatus.Cancelled);
        var foreignOrderId = await _fx.SeedSalesOrderAsync("SO-HTTP480-FOREIGN-" + tag, otherCustomerId, "USD");
        var cnyOrderId = await _fx.SeedSalesOrderAsync("SO-HTTP480-CNY-" + tag, customerId, "CNY");
        var before = await _fx.ReadSnapshotAsync();

        HttpClient? temporary = null;
        var client = admin;
        var body = DepositApplyBody(customerId, 1000m);
        var expectUnauthorized = false;
        switch (scenario)
        {
            case "noIdentity":
                client = _fx.CreateAnonymousClient();
                expectUnauthorized = true;
                break;
            case "missingMenu":
                var userName = "http480-deposit-" + tag;
                var password = "Http480@" + tag;
                await _fx.CreateUserWithoutRolesAsync(admin, userName, password);
                temporary = await _fx.LoginAsync(userName, password);
                client = temporary;
                break;
            case "unknownCustomer":
                body = DepositApplyBody(987654321L, 1000m);
                break;
            case "badCurrency":
                body["currency"] = 999;                          // 越界枚举值（非受支持币种）
                break;
            case "nonPositiveAmount":
                body["amount"] = 0m;
                break;
            case "danglingSource":
                body = DepositApplyBody(customerId, 1000m, salesOrderId: 987654321L);
                break;
            case "cancelledSource":
                body = DepositApplyBody(customerId, 1000m, salesOrderId: cancelledOrderId);
                break;
            case "sourceCustomerMismatch":
                body = DepositApplyBody(customerId, 1000m, salesOrderId: foreignOrderId);
                break;
            default:
                body = DepositApplyBody(customerId, 1000m, salesOrderId: cnyOrderId);
                break;
        }

        try
        {
            var (status, root, _) = await SendAsync(client, HttpMethod.Post, "/api/finance/deposit-applies", body);
            var after = await _fx.ReadSnapshotAsync();
            if (expectUnauthorized)
            {
                Assert.Equal(HttpStatusCode.Unauthorized, status);
            }
            else
            {
                Assert.Equal(HttpStatusCode.OK, status);
                var expected = scenario switch
                {
                    "missingMenu" => 2002,                       // ErrorCodes.Forbidden
                    "unknownCustomer" => 1002,                   // ErrorCodes.NotFound
                    "danglingSource" => 1002,                    // ErrorCodes.NotFound
                    "cancelledSource" => 1004,                   // ErrorCodes.RuleConflict
                    "sourceCustomerMismatch" => 1004,            // ErrorCodes.RuleConflict
                    "currencyMismatch" => 1004,                  // ErrorCodes.RuleConflict
                    _ => 1001                                    // ErrorCodes.InvalidParameter
                };
                Assert.Equal(expected, root.GetProperty("code").GetInt32());
            }

            Assert.Equal(before.DepositCount, after.DepositCount);
            Assert.Equal(before.PaymentCount, after.PaymentCount);
            Assert.Equal(before.DepositSequence, after.DepositSequence);   // 被拒绝的请求绝不消耗申请单号
            Assert.Equal(before.PaymentSequence, after.PaymentSequence);
        }
        finally
        {
            temporary?.Dispose();
        }
    }

    [Theory]
    [InlineData("noIdentity")]
    [InlineData("missingMenu")]
    [InlineData("unknownCustomer")]
    [InlineData("badCurrency")]
    [InlineData("nonPositiveAmount")]
    [InlineData("danglingSource")]
    [InlineData("cancelledSource")]
    [InlineData("currencyMismatch")]
    public async Task 货款申请非法请求_真实HTTP_受控拒绝且零写入零占号(string scenario)
    {
        Guard();
        var tag = Tag();
        using var admin = await _fx.LoginAsAdminAsync();
        var customerId = await CreateCustomerAsync(admin, tag);
        var cancelledOrderId = await _fx.SeedSalesOrderAsync(
            "SO-HTTP480-PCANCEL-" + tag, customerId, "USD", DocumentStatus.Cancelled);
        var cnyOrderId = await _fx.SeedSalesOrderAsync("SO-HTTP480-PCNY-" + tag, customerId, "CNY");
        var before = await _fx.ReadSnapshotAsync();

        HttpClient? temporary = null;
        var client = admin;
        var body = PaymentApplyBody(customerId, 1000m);
        var expectUnauthorized = false;
        switch (scenario)
        {
            case "noIdentity":
                client = _fx.CreateAnonymousClient();
                expectUnauthorized = true;
                break;
            case "missingMenu":
                var userName = "http480-payment-" + tag;
                var password = "Http480@" + tag;
                await _fx.CreateUserWithoutRolesAsync(admin, userName, password);
                temporary = await _fx.LoginAsync(userName, password);
                client = temporary;
                break;
            case "unknownCustomer":
                body = PaymentApplyBody(987654321L, 1000m);
                break;
            case "badCurrency":
                body["currency"] = 999;
                break;
            case "nonPositiveAmount":
                body["amount"] = 0m;
                break;
            case "danglingSource":
                body = PaymentApplyBody(customerId, 1000m, salesOrderId: 987654321L);
                break;
            case "cancelledSource":
                body = PaymentApplyBody(customerId, 1000m, salesOrderId: cancelledOrderId);
                break;
            default:
                body = PaymentApplyBody(customerId, 1000m, salesOrderId: cnyOrderId);
                break;
        }

        try
        {
            var (status, root, _) = await SendAsync(client, HttpMethod.Post, "/api/finance/payment-applies", body);
            var after = await _fx.ReadSnapshotAsync();
            if (expectUnauthorized)
            {
                Assert.Equal(HttpStatusCode.Unauthorized, status);
            }
            else
            {
                Assert.Equal(HttpStatusCode.OK, status);
                var expected = scenario switch
                {
                    "missingMenu" => 2002,
                    "unknownCustomer" => 1002,
                    "danglingSource" => 1002,
                    "cancelledSource" => 1004,
                    "currencyMismatch" => 1004,
                    _ => 1001
                };
                Assert.Equal(expected, root.GetProperty("code").GetInt32());
            }

            Assert.Equal(before.DepositCount, after.DepositCount);
            Assert.Equal(before.PaymentCount, after.PaymentCount);
            Assert.Equal(before.DepositSequence, after.DepositSequence);
            Assert.Equal(before.PaymentSequence, after.PaymentSequence);   // 被拒绝的请求绝不消耗申请单号
        }
        finally
        {
            temporary?.Dispose();
        }
    }

    // ==================== 5. 与旧存储过程族分离（旧写 fail closed） ====================

    [Fact]
    public async Task 旧族分离_旧写路径fail_closed_规范入口不受影响()
    {
        Guard();
        var tag = Tag();
        using var admin = await _fx.LoginAsAdminAsync();
        var customerId = await CreateCustomerAsync(admin, tag);

        // 旧写路径（ERP-404 门禁）：即使具备既有功能菜单也整体 fail closed（绝不调用旧申请单存储过程）。
        var legacyDeposit = new Dictionary<string, object?>
        {
            ["Action"] = "add",
            ["BillNo"] = "LEGACY-HTTP480-DJ-" + tag,
            ["CustomerId"] = customerId,
            ["Amount"] = 100m
        };
        var (legacyDepositStatus, legacyDepositRoot, _) = await SendAsync(admin, HttpMethod.Post,
            "/api/v2/bills/deposit-apply/save", legacyDeposit);
        Assert.Equal(HttpStatusCode.OK, legacyDepositStatus);
        Assert.NotEqual(0, legacyDepositRoot.GetProperty("code").GetInt32());

        var legacyPayment = new Dictionary<string, object?>
        {
            ["Action"] = "add",
            ["BillNo"] = "LEGACY-HTTP480-HK-" + tag,
            ["CustomerId"] = customerId,
            ["Amount"] = 100m
        };
        var (legacyPaymentStatus, legacyPaymentRoot, _) = await SendAsync(admin, HttpMethod.Post,
            "/api/v2/bills/payment-apply/save", legacyPayment);
        Assert.Equal(HttpStatusCode.OK, legacyPaymentStatus);
        Assert.NotEqual(0, legacyPaymentRoot.GetProperty("code").GetInt32());

        // 规范入口（api/finance/deposit-applies、api/finance/payment-applies，规范 Id 口径）在同一次运行中仍可正常写入：
        // 旧 Oid 写路径被停用不影响规范 Id 入口。
        var (depositId, depositNo) = await CreateDepositApplyAsync(admin, DepositApplyBody(customerId, 200m));
        var (paymentId, paymentNo) = await CreatePaymentApplyAsync(admin, PaymentApplyBody(customerId, 200m));
        Assert.True(depositId > 0 && paymentId > 0);
        Assert.StartsWith("DJ", depositNo, StringComparison.Ordinal);
        Assert.StartsWith("HK", paymentNo, StringComparison.Ordinal);
        Assert.Equal(1, (await _fx.ReadDepositApplyByIdAsync(depositId)).Count);
        Assert.Equal(1, (await _fx.ReadPaymentApplyByIdAsync(paymentId)).Count);
    }
}

/// <summary>
/// ERP-480 真实 HTTP + 真实隔离 SQL 夹具：目标必须是专用实例 <c>(localdb)\NEWERP_AutoAcceptance</c>、
/// 库名前缀 <c>NEWERP_AUTOTEST</c> 且 <c>Integrated Security=true</c>；只使用<strong>全新 GUID 后缀库</strong>，
/// 发现同名库已存在立即拒绝（绝不 drop / reset / 复用）；连接串只来自进程环境变量或专用 localdb 默认值。
/// <para>启动真实 ERP.Api 进程（真实 Kestrel / JWT 认证 / 既有授权），并为该进程注入<b>进程内临时 JWT 签名密钥</b>：
/// 随机生成、只存在于该进程的环境变量、绝不落盘、绝不复用空白签名配置；测试账号仍是既有种子账号（不新增 / 不持久化凭据）。</para>
/// </summary>
public sealed class FinanceApplyCanonicalEntrySqlServerFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";
    public const string AdminUser = "admin";
    public const string AdminPassword = "Admin@123";

    private const string DefaultDatabaseName = DatabasePrefix + "_FINANCEAPPLYCANONICAL";
    private const string PortVariable = "ERP_480_HTTP_PORT";
    private const string DefaultPort = "5397";

    private readonly StringBuilder _apiConsole = new();
    private readonly object _apiConsoleLock = new();
    private Process? _apiProcess;

    public string ConnectionString { get; private set; } = string.Empty;
    public string BaseUrl { get; private set; } = string.Empty;

    /// <summary>按规范 Id 直接读取的申请单行（单号 / 状态 / 删除态 / 金额 / 客户 / 可空来源销售订单）。</summary>
    public readonly record struct ApplyRow(
        int Count, long Id, string? No, int Status, bool IsDeleted, decimal Amount, long CustomerId, long? SalesOrderId);

    /// <summary>零副作用拒绝所需的整库计数快照（定金 / 货款申请单 + 两类单号序列）。</summary>
    public readonly record struct WriteSnapshot(
        long DepositCount, long PaymentCount, long DepositSequence, long PaymentSequence);

    public async Task InitializeAsync()
    {
        var configured = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        var connectionString = string.IsNullOrWhiteSpace(configured)
            ? $"Server=(localdb)\\{InstanceMarker};Initial Catalog={DefaultDatabaseName}_{Guid.NewGuid():N};"
              + "Integrated Security=true;TrustServerCertificate=true;"
            : configured;

        // 访问任何数据库之前：实例 / 库名前缀 / 集成安全 + 显式测试上下文（不满足立即 fail-closed）。
        AssertDedicatedTarget(connectionString);
        TestDatabaseSafetyGuard.EnsureApprovedTarget(connectionString, "定金 / 货款申请单规范入口集成验收");
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

    /// <summary>
    /// 在本次全新隔离库内播种一张既有销售订单（申请单来源为<b>夹具数据</b>）：
    /// 只写既有 <c>SalesOrders</c> 表，不新增表 / 列 / 权限，也不触碰任何申请单。
    /// </summary>
    public async Task<long> SeedSalesOrderAsync(
        string orderNo, long customerId, string currency, DocumentStatus status = DocumentStatus.Pending)
    {
        AssertDedicatedTarget(ConnectionString);
        var options = new DbContextOptionsBuilder<ErpDbContext>().UseSqlServer(ConnectionString).Options;
        await using var db = new ErpDbContext(options);
        var order = new SalesOrder
        {
            OrderNo = orderNo,
            OrderDate = DateTime.Today,
            CustomerId = customerId,
            Currency = Enum.Parse<Currency>(currency),
            ExchangeRate = 1m,
            TotalAmount = 10000m,
            Status = status
        };
        db.SalesOrders.Add(order);
        await db.SaveChangesAsync();
        return order.Id;
    }

    // ==================== 直接 SQL 复核（同一全新 GUID 库，只读） ====================

    /// <summary>按规范 Id 直接读取定金申请单行（单号 / 状态 / 删除态 / 金额 / 客户 / 来源）。</summary>
    public Task<ApplyRow> ReadDepositApplyByIdAsync(long id) => ReadApplyByIdAsync("FinanceDepositApplies", id);

    /// <summary>按规范 Id 直接读取货款申请单行（单号 / 状态 / 删除态 / 金额 / 客户 / 来源）。</summary>
    public Task<ApplyRow> ReadPaymentApplyByIdAsync(long id) => ReadApplyByIdAsync("FinancePaymentApplies", id);

    private async Task<ApplyRow> ReadApplyByIdAsync(string table, long id)
    {
        AssertDedicatedTarget(ConnectionString);
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT TOP 1 Id, ApplyNo, Status, IsDeleted, Amount, CustomerId, SalesOrderId "
                          + $"FROM db_owner.{table} WHERE Id = @key";
        cmd.Parameters.AddWithValue("@key", id);
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return new ApplyRow(0, 0, null, -1, false, 0m, 0L, null);

        return new ApplyRow(1, reader.GetInt64(0), reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.GetInt32(2), reader.GetBoolean(3), reader.GetDecimal(4), reader.GetInt64(5),
            reader.IsDBNull(6) ? null : reader.GetInt64(6));
    }

    /// <summary>零副作用拒绝所需的整库计数快照（定金 / 货款申请单 + 定金 / 货款申请单号序列）。</summary>
    public async Task<WriteSnapshot> ReadSnapshotAsync()
    {
        AssertDedicatedTarget(ConnectionString);
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        // DocumentType.DepositApply = 10、DocumentType.PaymentApply = 11（与 ERP.Domain.Enums.DocumentType 同源；不新增任何映射）。
        cmd.CommandText = "SELECT (SELECT COUNT(1) FROM db_owner.FinanceDepositApplies), "
                          + "(SELECT COUNT(1) FROM db_owner.FinancePaymentApplies), "
                          + "ISNULL((SELECT TOP 1 CurrentSequence FROM db_owner.SysDocumentNumberRules "
                          + "WHERE DocumentType = 10 AND IsDeleted = 0), 0), "
                          + "ISNULL((SELECT TOP 1 CurrentSequence FROM db_owner.SysDocumentNumberRules "
                          + "WHERE DocumentType = 11 AND IsDeleted = 0), 0)";
        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return new WriteSnapshot(Convert.ToInt64(reader.GetValue(0)), Convert.ToInt64(reader.GetValue(1)),
            Convert.ToInt64(reader.GetValue(2)), Convert.ToInt64(reader.GetValue(3)));
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
            File.WriteAllText(Path.Combine(evidenceDirectory, "finance-apply-canonical-entry-api-console.log"),
                ApiConsoleTail(int.MaxValue));
        }
        catch (IOException) { /* 证据写入失败不影响断言结果 */ }
    }
}

/// <summary>专用目标护栏的 fail-closed 覆盖：错误实例 / 错误库名 / 非集成安全必须在访问数据库之前被拒绝。</summary>
public sealed class FinanceApplyCanonicalEntryTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;User Id=sa;Password=secret")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(
            () => FinanceApplyCanonicalEntrySqlServerFixture.AssertDedicatedTarget(connection));
}
