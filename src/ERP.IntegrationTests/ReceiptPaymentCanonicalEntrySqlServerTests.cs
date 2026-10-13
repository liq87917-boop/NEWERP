using Microsoft.Data.SqlClient;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace ERP.IntegrationTests;

/// <summary>
/// ERP-479 收款单 / 付款单规范入口的<b>真实 HTTP + 真实隔离 SQL</b> 验收（真实 ERP.Api 进程 + 全新 GUID
/// <c>NEWERP_AUTOTEST</c> 库）：
/// <list type="number">
/// <item><b>真实 HTTP</b>：请求经真实 Kestrel 管线（MVC 绑定 + <c>[ApiController]</c> 模型校验 + JWT 认证 + 既有授权），
/// 因此「省略收款单号 / 付款单号」由<b>绑定层</b>证明（修复前正是绑定层因 <c>[Required]</c> 返回 HTTP 400
/// <c>{"errors":{"ReceiptNo":[...]}}</c>），直接实例化控制器的测试无法证明这一点；夹具为真实 API 进程注入
/// <b>进程内临时 JWT 签名密钥</b>（随机生成、只存活在该进程、绝不落盘 / 绝不继承空白签名配置），认证链路保持真实。</item>
/// <item><b>权威单号与规范 Id</b>：省略 / 伪造单据号一律持久化服务端生成单号，返回规范 <c>Id</c>；
/// 回读（详情 / 列表）与修改都按规范 <c>Id</c> 往返。</item>
/// <item><b>来源与既有工作台</b>：付款单可显式关联既有货款申请单（可空引用）；刚保存的规范收款单 / 付款单 Id
/// 直接命中既有收款 / 付款引用工作台的只读汇总与候选（既有服务路径，不新增接口）。</item>
/// <item><b>零副作用拒绝</b>：无身份 / 缺菜单 / 非法币种与金额 / 客户 · 供应商不可用 / 来源申请单悬空 · 币种不一致 ·
/// 超申请金额一律受控拒绝，且收款单、付款单、申请单与各自单号序列零变化。</item>
/// <item><b>专用目标护栏</b>：必须在任何数据库访问之前精确命中 <c>(localdb)\NEWERP_AutoAcceptance</c> + 库名前缀
/// <c>NEWERP_AUTOTEST</c> + <c>Integrated Security=true</c>；每次运行只创建一个全新 GUID 后缀库，发现同名库已存在立即拒绝，
/// 绝不 drop / reset / 复用任何数据库，也绝不读取 appsettings / .env / 生产凭据。</item>
/// </list>
/// <para>构建完成不等于阶段验收：只有下列场景在专用 localdb 上真实执行通过才算验收证据。</para>
/// </summary>
public sealed class ReceiptPaymentCanonicalEntrySqlServerTests
    : IClassFixture<ReceiptPaymentCanonicalEntrySqlServerFixture>
{
    private readonly ReceiptPaymentCanonicalEntrySqlServerFixture _fx;

    public ReceiptPaymentCanonicalEntrySqlServerTests(ReceiptPaymentCanonicalEntrySqlServerFixture fixture) => _fx = fixture;

    /// <summary>访问数据库前复核专用目标护栏（实例名 / 库名前缀 / 集成安全）。</summary>
    private void Guard()
    {
        var target = new SqlConnectionStringBuilder(_fx.ConnectionString);
        Assert.Equal($"(localdb)\\{ReceiptPaymentCanonicalEntrySqlServerFixture.InstanceMarker}",
            target.DataSource, ignoreCase: true);
        Assert.StartsWith(ReceiptPaymentCanonicalEntrySqlServerFixture.DatabasePrefix, target.InitialCatalog,
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

    /// <summary>收款单正文：与前端 crud.js 提交同形（可省略收款单号）。</summary>
    private static Dictionary<string, object?> ReceiptBody(
        long customerId, decimal amount = 1000m, string currency = "USD", string? receiptNo = null)
    {
        var body = new Dictionary<string, object?>
        {
            ["receiptDate"] = DateTime.Today.ToString("yyyy-MM-dd"),
            ["customerId"] = customerId,
            ["amount"] = amount,
            ["currency"] = currency,
            ["paymentMethod"] = "BankTransfer",
            ["bankAccount"] = "BANK-HTTP479",
            ["remark"] = "HTTP479"
        };
        if (receiptNo is not null) body["receiptNo"] = receiptNo;
        return body;
    }

    /// <summary>
    /// 付款单正文：与前端 crud.js 提交同形（可省略付款单号）；来源货款申请单按 ERP-479 的声明式可空引用提交
    /// （显式 Id 或显式 null = 未关联来源）。
    /// </summary>
    private static Dictionary<string, object?> PaymentBody(
        long supplierId, decimal amount = 1000m, string currency = "CNY",
        string? paymentNo = null, long? paymentApplyId = null)
    {
        var body = new Dictionary<string, object?>
        {
            ["paymentDate"] = DateTime.Today.ToString("yyyy-MM-dd"),
            ["supplierId"] = supplierId,
            ["amount"] = amount,
            ["currency"] = currency,
            ["paymentMethod"] = "BankTransfer",
            ["bankAccount"] = "BANK-HTTP479",
            ["paymentApplyId"] = paymentApplyId,
            ["remark"] = "HTTP479"
        };
        if (paymentNo is not null) body["paymentNo"] = paymentNo;
        return body;
    }

    private static async Task<long> CreateCustomerAsync(HttpClient client, string tag)
    {
        var payload = JsonSerializer.Serialize(new
        {
            customerCode = "C-HTTP479-" + tag, customerName = "HTTP479 客户 " + tag, currency = "USD"
        });
        using var response = await client.PostAsync("/api/base/customers",
            new StringContent(payload, Encoding.UTF8, "application/json"));
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(0, doc.RootElement.GetProperty("code").GetInt32());
        return doc.RootElement.GetProperty("data").GetProperty("id").GetInt64();
    }

    private static async Task<long> CreateSupplierAsync(HttpClient client, string tag)
    {
        var payload = JsonSerializer.Serialize(new
        {
            supplierCode = "S-HTTP479-" + tag, supplierName = "HTTP479 供应商 " + tag
        });
        using var response = await client.PostAsync("/api/base/suppliers",
            new StringContent(payload, Encoding.UTF8, "application/json"));
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(0, doc.RootElement.GetProperty("code").GetInt32());
        return doc.RootElement.GetProperty("data").GetProperty("id").GetInt64();
    }

    /// <summary>
    /// 经既有货款申请单接口产生真实来源申请单（CNY；待提交即已满足付款单来源资格）。
    /// <para>请求显式携带 <c>applyNo</c>：货款申请单的「省略申请单号」绑定口径属该菜单自身任务，
    /// 本任务不扩大范围改动其实体契约；服务端仍按权威口径覆盖申请单号，测试只依赖其规范 <c>Id</c>。</para>
    /// </summary>
    private static async Task<(long Id, string No)> CreatePaymentApplyAsync(
        HttpClient client, string tag, long customerId, decimal amount)
    {
        var payload = JsonSerializer.Serialize(new
        {
            applyNo = "HK-HTTP479-" + tag,
            applyDate = DateTime.Today.ToString("yyyy-MM-dd"),
            customerId,
            amount,
            currency = "CNY",
            exchangeRate = 1m,
            bankAccount = "BANK-HTTP479",
            payee = "HTTP479 收款方",
            reason = "HTTP479 来源申请单 " + tag
        });
        using var create = await client.PostAsync("/api/finance/payment-applies",
            new StringContent(payload, Encoding.UTF8, "application/json"));
        var raw = await create.Content.ReadAsStringAsync();
        using var created = JsonDocument.Parse(raw);
        Assert.True(create.StatusCode == HttpStatusCode.OK
            && created.RootElement.TryGetProperty("code", out var code) && code.GetInt32() == 0,
            "货款申请单创建失败（HTTP " + (int)create.StatusCode + "）：" + raw);
        var data = created.RootElement.GetProperty("data");
        return (data.GetProperty("id").GetInt64(), data.GetProperty("applyNo").GetString()!);
    }

    private static async Task<(long Id, string No)> CreateReceiptAsync(HttpClient client, object body)
    {
        var (status, root, _) = await SendAsync(client, HttpMethod.Post, "/api/finance/receipts", body);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(0, root.GetProperty("code").GetInt32());
        var data = root.GetProperty("data");
        return (data.GetProperty("id").GetInt64(), data.GetProperty("receiptNo").GetString()!);
    }

    private static async Task<(long Id, string No)> CreatePaymentAsync(HttpClient client, object body)
    {
        var (status, root, _) = await SendAsync(client, HttpMethod.Post, "/api/finance/payments", body);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(0, root.GetProperty("code").GetInt32());
        var data = root.GetProperty("data");
        return (data.GetProperty("id").GetInt64(), data.GetProperty("paymentNo").GetString()!);
    }

    // ==================== 1. 省略单据号 + 规范 Id 往返（真实 HTTP，绑定层证明） ====================

    [Fact]
    public async Task 省略收款单号_真实HTTP新增回读修改_服务端权威单号与规范Id()
    {
        Guard();
        var tag = Tag();
        using var admin = await _fx.LoginAsAdminAsync();
        var customerId = await CreateCustomerAsync(admin, tag);

        // 修复前：绑定层因 ReceiptNo 的显式 [Required] 返回 HTTP 400；现在必须成功。
        var (createdStatus, createdRoot, raw) = await SendAsync(admin, HttpMethod.Post, "/api/finance/receipts",
            ReceiptBody(customerId, 1200.5m));
        Assert.Equal(HttpStatusCode.OK, createdStatus);
        Assert.Equal(0, createdRoot.GetProperty("code").GetInt32());
        Assert.DoesNotContain("ReceiptNo field is required", raw, StringComparison.OrdinalIgnoreCase);

        var id = createdRoot.GetProperty("data").GetProperty("id").GetInt64();
        var number = createdRoot.GetProperty("data").GetProperty("receiptNo").GetString()!;
        Assert.True(id > 0);                                     // 规范 Id（EF 自增主键）
        Assert.StartsWith("SK", number, StringComparison.Ordinal);

        var persisted = await _fx.ReadReceiptByIdAsync(id);
        Assert.Equal(1, persisted.Count);
        Assert.Equal(number, persisted.No);
        Assert.Equal(0, persisted.Status);                       // Pending
        Assert.False(persisted.IsDeleted);
        Assert.Equal(1200.5m, persisted.Amount);
        Assert.Equal(customerId, persisted.CustomerId);

        // 详情 / 列表按规范 Id 回读一致（表单重开口径）。
        var (detailStatus, detailRoot, _) = await SendAsync(admin, HttpMethod.Get, $"/api/finance/receipts/{id}");
        Assert.Equal(HttpStatusCode.OK, detailStatus);
        Assert.Equal(number, detailRoot.GetProperty("data").GetProperty("receiptNo").GetString());
        var (listStatus, listRoot, _) = await SendAsync(admin, HttpMethod.Get,
            "/api/finance/receipts?page=1&pageSize=5&keyword=" + Uri.EscapeDataString(number));
        Assert.Equal(HttpStatusCode.OK, listStatus);
        Assert.Equal(id, listRoot.GetProperty("data").GetProperty("items")[0].GetProperty("id").GetInt64());

        // 修改（伪造单号 / 状态 / 删除态）：单号 / 状态 / 删除态保持服务端值。
        var (editStatus, editRoot, _) = await SendAsync(admin, HttpMethod.Put, $"/api/finance/receipts/{id}",
            ReceiptBody(customerId, 900m, receiptNo: "HACK-SK-479"));
        Assert.Equal(HttpStatusCode.OK, editStatus);
        Assert.Equal(0, editRoot.GetProperty("code").GetInt32());

        var reloaded = await _fx.ReadReceiptByIdAsync(id);
        Assert.Equal(number, reloaded.No);
        Assert.Equal(0, reloaded.Status);
        Assert.False(reloaded.IsDeleted);
        Assert.Equal(900m, reloaded.Amount);
    }

    [Fact]
    public async Task 省略付款单号_真实HTTP新增回读修改_服务端权威单号与规范Id()
    {
        Guard();
        var tag = Tag();
        using var admin = await _fx.LoginAsAdminAsync();
        var supplierId = await CreateSupplierAsync(admin, tag);

        var (createdStatus, createdRoot, raw) = await SendAsync(admin, HttpMethod.Post, "/api/finance/payments",
            PaymentBody(supplierId, 1500m));
        Assert.Equal(HttpStatusCode.OK, createdStatus);
        Assert.Equal(0, createdRoot.GetProperty("code").GetInt32());
        Assert.DoesNotContain("PaymentNo field is required", raw, StringComparison.OrdinalIgnoreCase);

        var id = createdRoot.GetProperty("data").GetProperty("id").GetInt64();
        var number = createdRoot.GetProperty("data").GetProperty("paymentNo").GetString()!;
        Assert.True(id > 0);
        Assert.StartsWith("FK", number, StringComparison.Ordinal);

        var persisted = await _fx.ReadPaymentByIdAsync(id);
        Assert.Equal(1, persisted.Count);
        Assert.Equal(number, persisted.No);
        Assert.Equal(0, persisted.Status);
        Assert.False(persisted.IsDeleted);
        Assert.Equal(1500m, persisted.Amount);
        Assert.Equal(supplierId, persisted.SupplierId);
        Assert.Null(persisted.PaymentApplyId);                    // 未关联来源 = 历史未链接语义

        var (detailStatus, detailRoot, _) = await SendAsync(admin, HttpMethod.Get, $"/api/finance/payments/{id}");
        Assert.Equal(HttpStatusCode.OK, detailStatus);
        Assert.Equal(number, detailRoot.GetProperty("data").GetProperty("paymentNo").GetString());

        var (editStatus, editRoot, _) = await SendAsync(admin, HttpMethod.Put, $"/api/finance/payments/{id}",
            PaymentBody(supplierId, 1200m, paymentNo: "HACK-FK-479"));
        Assert.Equal(HttpStatusCode.OK, editStatus);
        Assert.Equal(0, editRoot.GetProperty("code").GetInt32());

        var reloaded = await _fx.ReadPaymentByIdAsync(id);
        Assert.Equal(number, reloaded.No);
        Assert.Equal(0, reloaded.Status);
        Assert.False(reloaded.IsDeleted);
        Assert.Equal(1200m, reloaded.Amount);
    }

    [Fact]
    public async Task 伪造单据号_真实HTTP_持久化号码由服务端权威生成()
    {
        Guard();
        var tag = Tag();
        using var admin = await _fx.LoginAsAdminAsync();
        var customerId = await CreateCustomerAsync(admin, tag);
        var supplierId = await CreateSupplierAsync(admin, tag);

        var (receiptId, receiptNo) = await CreateReceiptAsync(admin,
            ReceiptBody(customerId, 700m, receiptNo: "HACK-SK-HTTP479-" + tag));
        Assert.StartsWith("SK", receiptNo, StringComparison.Ordinal);
        Assert.DoesNotContain("HACK", receiptNo, StringComparison.Ordinal);
        Assert.Equal(receiptNo, (await _fx.ReadReceiptByIdAsync(receiptId)).No);

        var (paymentId, paymentNo) = await CreatePaymentAsync(admin,
            PaymentBody(supplierId, 700m, paymentNo: "HACK-FK-HTTP479-" + tag));
        Assert.StartsWith("FK", paymentNo, StringComparison.Ordinal);
        Assert.DoesNotContain("HACK", paymentNo, StringComparison.Ordinal);
        Assert.Equal(paymentNo, (await _fx.ReadPaymentByIdAsync(paymentId)).No);
    }

    // ==================== 2. 显式关联货款申请单与既有引用工作台（规范 Id 可达） ====================

    [Fact]
    public async Task 显式关联货款申请单与既有引用工作台_真实HTTP按规范Id可达()
    {
        Guard();
        var tag = Tag();
        using var admin = await _fx.LoginAsAdminAsync();
        var customerId = await CreateCustomerAsync(admin, tag);
        var supplierId = await CreateSupplierAsync(admin, tag);
        var before = await _fx.ReadSnapshotAsync();

        var (applyId, applyNo) = await CreatePaymentApplyAsync(admin, tag, customerId, 5000m);
        Assert.StartsWith("HK", applyNo, StringComparison.Ordinal);

        var (paymentId, paymentNo) = await CreatePaymentAsync(admin,
            PaymentBody(supplierId, 1000m, paymentApplyId: applyId));
        var persisted = await _fx.ReadPaymentByIdAsync(paymentId);
        Assert.Equal(1, persisted.Count);
        Assert.Equal(applyId, persisted.PaymentApplyId);          // 显式来源按 Id 保留（可空引用）
        Assert.Equal(paymentNo, persisted.No);

        // 既有付款引用工作台：付款单侧只读汇总与候选都按服务端规范 Id 可达（既有服务路径）。
        var (summaryStatus, summaryRoot, _) = await SendAsync(admin, HttpMethod.Get,
            $"/api/supplier-payment-allocations/payments/{paymentId}/summary");
        Assert.Equal(HttpStatusCode.OK, summaryStatus);
        Assert.Equal(0, summaryRoot.GetProperty("code").GetInt32());
        Assert.Equal(paymentId, summaryRoot.GetProperty("data").GetProperty("paymentId").GetInt64());
        Assert.Equal(paymentNo, summaryRoot.GetProperty("data").GetProperty("paymentNo").GetString());

        var (candidateStatus, candidateRoot, _) = await SendAsync(admin, HttpMethod.Get,
            "/api/supplier-payment-allocations/payments?supplierId=" + supplierId
            + "&keyword=" + Uri.EscapeDataString(paymentNo));
        Assert.Equal(HttpStatusCode.OK, candidateStatus);
        var candidate = candidateRoot.GetProperty("data").EnumerateArray()
            .Single(c => c.GetProperty("paymentId").GetInt64() == paymentId);
        Assert.Equal(paymentNo, candidate.GetProperty("paymentNo").GetString());

        // 既有收款引用工作台：收款单侧只读汇总与候选同样按规范 Id 可达。
        var (receiptId, receiptNo) = await CreateReceiptAsync(admin, ReceiptBody(customerId, 800m));
        var (receiptSummaryStatus, receiptSummaryRoot, _) = await SendAsync(admin, HttpMethod.Get,
            $"/api/customer-receipt-allocations/receipts/{receiptId}/summary");
        Assert.Equal(HttpStatusCode.OK, receiptSummaryStatus);
        Assert.Equal(0, receiptSummaryRoot.GetProperty("code").GetInt32());
        Assert.Equal(receiptId, receiptSummaryRoot.GetProperty("data").GetProperty("receiptId").GetInt64());
        Assert.Equal(receiptNo, receiptSummaryRoot.GetProperty("data").GetProperty("receiptNo").GetString());

        var (receiptCandidateStatus, receiptCandidateRoot, _) = await SendAsync(admin, HttpMethod.Get,
            "/api/customer-receipt-allocations/receipts?customerId=" + customerId
            + "&keyword=" + Uri.EscapeDataString(receiptNo));
        Assert.Equal(HttpStatusCode.OK, receiptCandidateStatus);
        Assert.Contains(receiptCandidateRoot.GetProperty("data").EnumerateArray(),
            c => c.GetProperty("receiptId").GetInt64() == receiptId);

        // 本次只新增 1 张申请单 + 1 张付款单 + 1 张收款单：无自动财务指派、无台账回填。
        var after = await _fx.ReadSnapshotAsync();
        Assert.Equal(before.ApplyCount + 1, after.ApplyCount);
        Assert.Equal(before.PaymentCount + 1, after.PaymentCount);
        Assert.Equal(before.ReceiptCount + 1, after.ReceiptCount);
    }

    // ==================== 3. 零副作用拒绝（真实 HTTP + 真实 SQL 计数 / 占号） ====================

    [Theory]
    [InlineData("noIdentity")]
    [InlineData("missingMenu")]
    [InlineData("unknownCustomer")]
    [InlineData("badCurrency")]
    [InlineData("nonPositiveAmount")]
    public async Task 收款单非法请求_真实HTTP_受控拒绝且零写入零占号(string scenario)
    {
        Guard();
        var tag = Tag();
        using var admin = await _fx.LoginAsAdminAsync();
        var customerId = await CreateCustomerAsync(admin, tag);
        var before = await _fx.ReadSnapshotAsync();

        HttpClient? temporary = null;
        var client = admin;
        var body = ReceiptBody(customerId, 1000m);
        switch (scenario)
        {
            case "noIdentity":
                temporary = _fx.CreateAnonymousClient();
                client = temporary;
                break;
            case "missingMenu":
                var userName = "http479-receipt-" + tag;
                var password = "Http479@" + tag;
                await _fx.CreateUserWithoutRolesAsync(admin, userName, password);
                temporary = await _fx.LoginAsync(userName, password);
                client = temporary;
                break;
            case "unknownCustomer":
                body = ReceiptBody(987654321L, 1000m);
                break;
            case "badCurrency":
                body["currency"] = 999;                          // 越界枚举值（非受支持币种）
                break;
            default:
                body["amount"] = 0m;                             // 非正金额
                break;
        }

        try
        {
            var (status, root, _) = await SendAsync(client, HttpMethod.Post, "/api/finance/receipts", body);
            var after = await _fx.ReadSnapshotAsync();
            if (scenario == "noIdentity")
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
                    _ => 1001                                    // ErrorCodes.InvalidParameter
                };
                Assert.Equal(expected, root.GetProperty("code").GetInt32());
            }

            Assert.Equal(before.ReceiptCount, after.ReceiptCount);
            Assert.Equal(before.PaymentCount, after.PaymentCount);
            Assert.Equal(before.ReceiptSequence, after.ReceiptSequence);   // 被拒绝的请求绝不消耗收款单号
            Assert.Equal(before.PaymentSequence, after.PaymentSequence);
        }
        finally
        {
            temporary?.Dispose();
        }
    }

    [Theory]
    [InlineData("missingMenu")]
    [InlineData("unknownSupplier")]
    [InlineData("danglingApply")]
    [InlineData("currencyMismatch")]
    [InlineData("exceedsApplyAmount")]
    public async Task 付款单非法请求_真实HTTP_受控拒绝且零写入零占号(string scenario)
    {
        Guard();
        var tag = Tag();
        using var admin = await _fx.LoginAsAdminAsync();
        var customerId = await CreateCustomerAsync(admin, tag);
        var supplierId = await CreateSupplierAsync(admin, tag);

        HttpClient? temporary = null;
        var client = admin;
        var body = PaymentBody(supplierId, 1000m);
        switch (scenario)
        {
            case "missingMenu":
                var userName = "http479-payment-" + tag;
                var password = "Http479@" + tag;
                await _fx.CreateUserWithoutRolesAsync(admin, userName, password);
                temporary = await _fx.LoginAsync(userName, password);
                client = temporary;
                break;
            case "unknownSupplier":
                body = PaymentBody(987654321L, 1000m);
                break;
            case "danglingApply":
                body = PaymentBody(supplierId, 1000m, paymentApplyId: 987654321L);
                break;
            case "currencyMismatch":
                var (usdApplyId, _) = await CreatePaymentApplyAsync(admin, tag + "usd", customerId, 5000m);
                body = PaymentBody(supplierId, 1000m, currency: "USD", paymentApplyId: usdApplyId);
                break;
            default:
                var (smallApplyId, _) = await CreatePaymentApplyAsync(admin, tag + "sm", customerId, 100m);
                body = PaymentBody(supplierId, 1000m, paymentApplyId: smallApplyId);
                break;
        }

        var before = await _fx.ReadSnapshotAsync();
        try
        {
            var (status, root, _) = await SendAsync(client, HttpMethod.Post, "/api/finance/payments", body);
            var after = await _fx.ReadSnapshotAsync();

            Assert.Equal(HttpStatusCode.OK, status);
            var expected = scenario switch
            {
                "missingMenu" => 2002,                           // ErrorCodes.Forbidden
                "unknownSupplier" => 1002,                       // ErrorCodes.NotFound
                "danglingApply" => 1002,                         // ErrorCodes.NotFound
                _ => 1004                                        // ErrorCodes.RuleConflict
            };
            Assert.Equal(expected, root.GetProperty("code").GetInt32());

            Assert.Equal(before.PaymentCount, after.PaymentCount);
            Assert.Equal(before.ApplyCount, after.ApplyCount);
            Assert.Equal(before.PaymentSequence, after.PaymentSequence);   // 被拒绝的请求绝不消耗付款单号
            Assert.Equal(before.ReceiptSequence, after.ReceiptSequence);
        }
        finally
        {
            temporary?.Dispose();
        }
    }

    // ==================== 4. 与旧存储过程族分离（旧写 fail closed） ====================

    [Fact]
    public async Task 旧族分离_旧写路径fail_closed_规范入口不受影响()
    {
        Guard();
        var tag = Tag();
        using var admin = await _fx.LoginAsAdminAsync();
        var customerId = await CreateCustomerAsync(admin, tag);
        var supplierId = await CreateSupplierAsync(admin, tag);

        // 旧写路径（ERP-404 门禁）：即使具备既有功能菜单也整体 fail closed（绝不调用旧收款 / 付款存储过程）。
        var legacyReceipt = new Dictionary<string, object?>
        {
            ["Action"] = "add",
            ["ReceiptNo"] = "LEGACY-HTTP479-" + tag,
            ["CustomerId"] = customerId,
            ["Amount"] = 100m
        };
        var (legacyReceiptStatus, legacyReceiptRoot, _) = await SendAsync(admin, HttpMethod.Post,
            "/api/v2/bills/receipt/save", legacyReceipt);
        Assert.Equal(HttpStatusCode.OK, legacyReceiptStatus);
        Assert.NotEqual(0, legacyReceiptRoot.GetProperty("code").GetInt32());

        var legacyPayment = new Dictionary<string, object?>
        {
            ["Action"] = "add",
            ["PaymentNo"] = "LEGACY-HTTP479-" + tag,
            ["SupplierId"] = supplierId,
            ["Amount"] = 100m
        };
        var (legacyPaymentStatus, legacyPaymentRoot, _) = await SendAsync(admin, HttpMethod.Post,
            "/api/v2/bills/payment/save", legacyPayment);
        Assert.Equal(HttpStatusCode.OK, legacyPaymentStatus);
        Assert.NotEqual(0, legacyPaymentRoot.GetProperty("code").GetInt32());

        // 规范入口（api/finance/receipts、api/finance/payments，规范 Id 口径）在同一次运行中仍可正常写入：
        // 旧 Oid 写路径被停用不影响规范 Id 入口。
        var (receiptId, receiptNo) = await CreateReceiptAsync(admin, ReceiptBody(customerId, 200m));
        var (paymentId, paymentNo) = await CreatePaymentAsync(admin, PaymentBody(supplierId, 200m));
        Assert.True(receiptId > 0 && paymentId > 0);
        Assert.StartsWith("SK", receiptNo, StringComparison.Ordinal);
        Assert.StartsWith("FK", paymentNo, StringComparison.Ordinal);
        Assert.Equal(1, (await _fx.ReadReceiptByIdAsync(receiptId)).Count);
        Assert.Equal(1, (await _fx.ReadPaymentByIdAsync(paymentId)).Count);
    }
}

/// <summary>
/// ERP-479 真实 HTTP + 真实隔离 SQL 夹具：目标必须是专用实例 <c>(localdb)\NEWERP_AutoAcceptance</c>、
/// 库名前缀 <c>NEWERP_AUTOTEST</c> 且 <c>Integrated Security=true</c>；只使用<strong>全新 GUID 后缀库</strong>，
/// 发现同名库已存在立即拒绝（绝不 drop / reset / 复用）；连接串只来自进程环境变量或专用 localdb 默认值。
/// <para>启动真实 ERP.Api 进程（真实 Kestrel / JWT 认证 / 既有授权），并为该进程注入<b>进程内临时 JWT 签名密钥</b>：
/// 随机生成、只存在于该进程的环境变量、绝不落盘、绝不复用空白签名配置；测试账号仍是既有种子账号（不新增 / 不持久化凭据）。</para>
/// </summary>
public sealed class ReceiptPaymentCanonicalEntrySqlServerFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";
    public const string AdminUser = "admin";
    public const string AdminPassword = "Admin@123";

    private const string DefaultDatabaseName = DatabasePrefix + "_RECEIPTPAYMENTCANONICAL";
    private const string PortVariable = "ERP_479_HTTP_PORT";
    private const string DefaultPort = "5299";

    private readonly StringBuilder _apiConsole = new();
    private readonly object _apiConsoleLock = new();
    private Process? _apiProcess;

    public string ConnectionString { get; private set; } = string.Empty;
    public string BaseUrl { get; private set; } = string.Empty;

    /// <summary>按规范 Id 直接读取的收款单行（单号 / 状态 / 删除态 / 金额 / 客户）。</summary>
    public readonly record struct ReceiptRow(
        int Count, long Id, string? No, int Status, bool IsDeleted, decimal Amount, long CustomerId);

    /// <summary>按规范 Id 直接读取的付款单行（单号 / 状态 / 删除态 / 金额 / 供应商 / 可空来源申请单）。</summary>
    public readonly record struct PaymentRow(
        int Count, long Id, string? No, int Status, bool IsDeleted, decimal Amount, long SupplierId, long? PaymentApplyId);

    /// <summary>零副作用拒绝所需的整库计数快照（收款单 / 付款单 / 货款申请单 + 两类单号序列）。</summary>
    public readonly record struct WriteSnapshot(
        long ReceiptCount, long PaymentCount, long ApplyCount, long ReceiptSequence, long PaymentSequence);

    public async Task InitializeAsync()
    {
        var configured = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        var connectionString = string.IsNullOrWhiteSpace(configured)
            ? $"Server=(localdb)\\{InstanceMarker};Initial Catalog={DefaultDatabaseName}_{Guid.NewGuid():N};"
              + "Integrated Security=true;TrustServerCertificate=true;"
            : configured;

        // 访问任何数据库之前：实例 / 库名前缀 / 集成安全 + 显式测试上下文（不满足立即 fail-closed）。
        AssertDedicatedTarget(connectionString);
        TestDatabaseSafetyGuard.EnsureApprovedTarget(connectionString, "收款单 / 付款单规范入口集成验收");
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

    /// <summary>按规范 Id 直接读取收款单（单号 / 状态 / 删除态 / 金额 / 客户）。</summary>
    public async Task<ReceiptRow> ReadReceiptByIdAsync(long id)
    {
        AssertDedicatedTarget(ConnectionString);
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT TOP 1 Id, ReceiptNo, Status, IsDeleted, Amount, CustomerId "
                          + "FROM db_owner.FinanceReceipts WHERE Id = @key";
        cmd.Parameters.AddWithValue("@key", id);
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return new ReceiptRow(0, 0, null, -1, false, 0m, 0L);

        return new ReceiptRow(1, reader.GetInt64(0), reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.GetInt32(2), reader.GetBoolean(3), reader.GetDecimal(4), reader.GetInt64(5));
    }

    /// <summary>按规范 Id 直接读取付款单（含可空来源货款申请单 Id）。</summary>
    public async Task<PaymentRow> ReadPaymentByIdAsync(long id)
    {
        AssertDedicatedTarget(ConnectionString);
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT TOP 1 Id, PaymentNo, Status, IsDeleted, Amount, SupplierId, PaymentApplyId "
                          + "FROM db_owner.FinancePayments WHERE Id = @key";
        cmd.Parameters.AddWithValue("@key", id);
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return new PaymentRow(0, 0, null, -1, false, 0m, 0L, null);

        return new PaymentRow(1, reader.GetInt64(0), reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.GetInt32(2), reader.GetBoolean(3), reader.GetDecimal(4), reader.GetInt64(5),
            reader.IsDBNull(6) ? null : reader.GetInt64(6));
    }

    /// <summary>零副作用拒绝所需的整库计数快照（收款单 / 付款单 / 货款申请单 + 收款单 / 付款单单号序列）。</summary>
    public async Task<WriteSnapshot> ReadSnapshotAsync()
    {
        AssertDedicatedTarget(ConnectionString);
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        // DocumentType.Receipt = 15、DocumentType.Payment = 12（与 ERP.Domain.Enums.DocumentType 同源；不新增任何映射）。
        cmd.CommandText = "SELECT (SELECT COUNT(1) FROM db_owner.FinanceReceipts), "
                          + "(SELECT COUNT(1) FROM db_owner.FinancePayments), "
                          + "(SELECT COUNT(1) FROM db_owner.FinancePaymentApplies), "
                          + "ISNULL((SELECT TOP 1 CurrentSequence FROM db_owner.SysDocumentNumberRules "
                          + "WHERE DocumentType = 15 AND IsDeleted = 0), 0), "
                          + "ISNULL((SELECT TOP 1 CurrentSequence FROM db_owner.SysDocumentNumberRules "
                          + "WHERE DocumentType = 12 AND IsDeleted = 0), 0)";
        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return new WriteSnapshot(Convert.ToInt64(reader.GetValue(0)), Convert.ToInt64(reader.GetValue(1)),
            Convert.ToInt64(reader.GetValue(2)), Convert.ToInt64(reader.GetValue(3)), Convert.ToInt64(reader.GetValue(4)));
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
            File.WriteAllText(Path.Combine(evidenceDirectory, "receipt-payment-canonical-entry-api-console.log"),
                ApiConsoleTail(int.MaxValue));
        }
        catch (IOException) { /* 证据写入失败不影响断言结果 */ }
    }
}

/// <summary>专用目标护栏的 fail-closed 覆盖：错误实例 / 错误库名 / 非集成安全必须在访问数据库之前被拒绝。</summary>
public sealed class ReceiptPaymentCanonicalEntryTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;User Id=sa;Password=secret")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(
            () => ReceiptPaymentCanonicalEntrySqlServerFixture.AssertDedicatedTarget(connection));
}
