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
/// ERP-481 装柜结算单 / 散货结算单规范入口的<b>真实 HTTP + 真实隔离 SQL</b> 验收（真实 ERP.Api 进程 + 全新 GUID
/// <c>NEWERP_AUTOTEST</c> 库）：
/// <list type="number">
/// <item><b>真实 HTTP</b>：请求经真实 Kestrel 管线（MVC 绑定 + <c>[ApiController]</c> 模型校验 + JWT 认证 + 既有授权），
/// 因此「省略结算单号」由<b>绑定层</b>证明（修复前正是绑定层因 <c>[Required]</c> 返回 HTTP 400
/// <c>{"errors":{"SettlementNo":[...]}}</c>），直接实例化控制器的测试无法证明这一点；夹具为真实 API 进程注入
/// <b>进程内临时 JWT 签名密钥</b>（随机生成、只存活在该进程、绝不落盘 / 绝不继承空白签名配置），认证链路保持真实。</item>
/// <item><b>权威单号与规范 Id</b>：省略 / 伪造结算单号一律持久化服务端生成单号（<c>ZJ</c> 装柜 / <c>SJ</c> 散货前缀），
/// 返回规范 <c>Id</c>；回读（详情 / 列表）与修改都按规范 <c>Id</c> 往返。</item>
/// <item><b>显式来源装柜清单</b>：可显式关联既有装柜清单（可空引用）并显式清空；既有来源候选 / 已存储来源接口按规范参数可达。</item>
/// <item><b>有效生命周期</b>：提交 → 审核 → 取消按既有授权执行；非法流转（待提交直接审核 / 重复取消）零状态变化。</item>
/// <item><b>零副作用拒绝</b>：无身份 / 缺菜单 / 客户不可用 / 非法金额 / 来源清单悬空 · 已取消 · 跨客户一律受控拒绝，
/// 且结算单行、来源清单状态与各自单号序列零变化。</item>
/// <item><b>专用目标护栏</b>：必须在任何数据库访问之前精确命中 <c>(localdb)\NEWERP_AutoAcceptance</c> + 库名前缀
/// <c>NEWERP_AUTOTEST</c> + <c>Integrated Security=true</c>；每次运行只创建一个全新 GUID 后缀库，发现同名库已存在立即拒绝，
/// 绝不 drop / reset / 复用任何数据库，也绝不读取 appsettings / .env / 生产凭据。</item>
/// </list>
/// <para>装柜清单 / 客户为<b>夹具数据</b>（EF 直接播种到本次全新隔离库的既有 <c>ContainerLoadingLists</c> /
/// <c>BaseCustomers</c> 表），被测写入（结算单创建 / 修改 / 提交 / 审核 / 取消）全部经真实 HTTP；构建完成不等于阶段验收。</para>
/// </summary>
public sealed class FinanceSettlementCanonicalEntrySqlServerTests
    : IClassFixture<FinanceSettlementCanonicalEntrySqlServerFixture>
{
    private readonly FinanceSettlementCanonicalEntrySqlServerFixture _fx;

    public FinanceSettlementCanonicalEntrySqlServerTests(FinanceSettlementCanonicalEntrySqlServerFixture fixture) => _fx = fixture;

    /// <summary>访问数据库前复核专用目标护栏（实例名 / 库名前缀 / 集成安全）。</summary>
    private void Guard()
    {
        var target = new SqlConnectionStringBuilder(_fx.ConnectionString);
        Assert.Equal($"(localdb)\\{FinanceSettlementCanonicalEntrySqlServerFixture.InstanceMarker}",
            target.DataSource, ignoreCase: true);
        Assert.StartsWith(FinanceSettlementCanonicalEntrySqlServerFixture.DatabasePrefix, target.InitialCatalog,
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

    /// <summary>装柜结算单正文：与前端 crud.js 提交同形（可省略结算单号；来源清单字段可空）。</summary>
    private static Dictionary<string, object?> ContainerSettlementBody(
        long customerId, long? loadingListId = null, decimal totalAmount = 1000m,
        decimal freightCost = 0m, decimal otherCost = 0m, string? settlementNo = null,
        string remark = "HTTP481 装柜结算")
    {
        var body = new Dictionary<string, object?>
        {
            ["settlementDate"] = DateTime.Today.ToString("yyyy-MM-dd"),
            ["customerId"] = customerId,
            ["loadingListId"] = loadingListId,
            ["totalAmount"] = totalAmount,
            ["freightCost"] = freightCost,
            ["otherCost"] = otherCost,
            ["remark"] = remark
        };
        if (settlementNo is not null) body["settlementNo"] = settlementNo;
        return body;
    }

    /// <summary>散货结算单正文：与前端 crud.js 提交同形（可省略结算单号；无来源单据字段）。</summary>
    private static Dictionary<string, object?> BulkSettlementBody(
        long customerId, decimal totalAmount = 1000m, decimal freightCost = 0m,
        string? settlementNo = null, string remark = "HTTP481 散货结算")
    {
        var body = new Dictionary<string, object?>
        {
            ["settlementDate"] = DateTime.Today.ToString("yyyy-MM-dd"),
            ["customerId"] = customerId,
            ["totalAmount"] = totalAmount,
            ["freightCost"] = freightCost,
            ["remark"] = remark
        };
        if (settlementNo is not null) body["settlementNo"] = settlementNo;
        return body;
    }

    private static async Task<long> CreateCustomerAsync(HttpClient client, string tag)
    {
        var payload = JsonSerializer.Serialize(new
        {
            customerCode = "C-HTTP481-" + tag, customerName = "HTTP481 客户 " + tag, currency = "USD"
        });
        using var response = await client.PostAsync("/api/base/customers",
            new StringContent(payload, Encoding.UTF8, "application/json"));
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(0, doc.RootElement.GetProperty("code").GetInt32());
        return doc.RootElement.GetProperty("data").GetProperty("id").GetInt64();
    }

    private static async Task<(long Id, string No)> CreateContainerSettlementAsync(HttpClient client, object body)
    {
        var (status, root, raw) = await SendAsync(client, HttpMethod.Post, "/api/finance/container-settlements", body);
        Assert.True(status == HttpStatusCode.OK && root.GetProperty("code").GetInt32() == 0,
            "装柜结算单创建失败（HTTP " + (int)status + "）：" + raw);
        var data = root.GetProperty("data");
        return (data.GetProperty("id").GetInt64(), data.GetProperty("settlementNo").GetString()!);
    }

    private static async Task<(long Id, string No)> CreateBulkSettlementAsync(HttpClient client, object body)
    {
        var (status, root, raw) = await SendAsync(client, HttpMethod.Post, "/api/finance/bulk-settlements", body);
        Assert.True(status == HttpStatusCode.OK && root.GetProperty("code").GetInt32() == 0,
            "散货结算单创建失败（HTTP " + (int)status + "）：" + raw);
        var data = root.GetProperty("data");
        return (data.GetProperty("id").GetInt64(), data.GetProperty("settlementNo").GetString()!);
    }

    // ==================== 1. 省略结算单号 + 规范 Id 往返（真实 HTTP，绑定层证明） ====================

    [Fact]
    public async Task 省略装柜结算单号_真实HTTP新增回读修改_服务端权威单号与默认状态()
    {
        Guard();
        var tag = Tag();
        using var admin = await _fx.LoginAsAdminAsync();
        var customerId = await CreateCustomerAsync(admin, tag);

        // 修复前：绑定层因 SettlementNo 的显式 [Required] 返回 HTTP 400；现在必须成功。
        var (createdStatus, createdRoot, raw) = await SendAsync(admin, HttpMethod.Post,
            "/api/finance/container-settlements", ContainerSettlementBody(customerId, totalAmount: 1200.5m));
        Assert.Equal(HttpStatusCode.OK, createdStatus);
        Assert.Equal(0, createdRoot.GetProperty("code").GetInt32());
        Assert.DoesNotContain("SettlementNo field is required", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("is required", raw, StringComparison.OrdinalIgnoreCase);

        var id = createdRoot.GetProperty("data").GetProperty("id").GetInt64();
        var number = createdRoot.GetProperty("data").GetProperty("settlementNo").GetString()!;
        Assert.True(id > 0);                                     // 规范 Id（EF 自增主键）
        Assert.StartsWith("ZJ", number, StringComparison.Ordinal);

        var persisted = await _fx.ReadContainerSettlementByIdAsync(id);
        Assert.Equal(1, persisted.Count);
        Assert.Equal(number, persisted.No);
        Assert.Equal(0, persisted.Status);                       // Pending
        Assert.False(persisted.IsDeleted);
        Assert.Equal(1200.5m, persisted.TotalAmount);
        Assert.Equal(customerId, persisted.CustomerId);
        Assert.Null(persisted.LoadingListId);                    // 未关联来源 = 历史未链接语义
        Assert.Equal("HTTP481 装柜结算", persisted.Remark);

        // 详情 / 列表按规范 Id 回读一致（表单重开口径）。
        var (detailStatus, detailRoot, _) = await SendAsync(admin, HttpMethod.Get,
            $"/api/finance/container-settlements/{id}");
        Assert.Equal(HttpStatusCode.OK, detailStatus);
        Assert.Equal(number, detailRoot.GetProperty("data").GetProperty("settlementNo").GetString());
        var (listStatus, listRoot, _) = await SendAsync(admin, HttpMethod.Get,
            "/api/finance/container-settlements?page=1&pageSize=5&keyword=" + Uri.EscapeDataString(number));
        Assert.Equal(HttpStatusCode.OK, listStatus);
        Assert.Equal(id, listRoot.GetProperty("data").GetProperty("items")[0].GetProperty("id").GetInt64());

        // 修改（伪造结算单号 / 状态 / 删除态 / 时间戳）：单号 / 状态 / 删除态保持服务端值，备注往返。
        var editBody = ContainerSettlementBody(customerId, totalAmount: 900m,
            settlementNo: "HACK-ZJ-481", remark: "更新备注");
        editBody["status"] = 5;
        editBody["isDeleted"] = true;
        editBody["createdAt"] = new DateTime(2000, 1, 1);
        var (editStatus, editRoot, _) = await SendAsync(admin, HttpMethod.Put,
            $"/api/finance/container-settlements/{id}", editBody);
        Assert.Equal(HttpStatusCode.OK, editStatus);
        Assert.Equal(0, editRoot.GetProperty("code").GetInt32());

        var reloaded = await _fx.ReadContainerSettlementByIdAsync(id);
        Assert.Equal(number, reloaded.No);
        Assert.Equal(0, reloaded.Status);
        Assert.False(reloaded.IsDeleted);
        Assert.Equal(900m, reloaded.TotalAmount);
        Assert.Equal("更新备注", reloaded.Remark);
    }

    [Fact]
    public async Task 省略散货结算单号_真实HTTP新增回读修改_服务端权威单号与默认状态()
    {
        Guard();
        var tag = Tag();
        using var admin = await _fx.LoginAsAdminAsync();
        var customerId = await CreateCustomerAsync(admin, tag);

        var (createdStatus, createdRoot, raw) = await SendAsync(admin, HttpMethod.Post,
            "/api/finance/bulk-settlements", BulkSettlementBody(customerId, totalAmount: 1500m));
        Assert.Equal(HttpStatusCode.OK, createdStatus);
        Assert.Equal(0, createdRoot.GetProperty("code").GetInt32());
        Assert.DoesNotContain("is required", raw, StringComparison.OrdinalIgnoreCase);

        var id = createdRoot.GetProperty("data").GetProperty("id").GetInt64();
        var number = createdRoot.GetProperty("data").GetProperty("settlementNo").GetString()!;
        Assert.True(id > 0);
        Assert.StartsWith("SJ", number, StringComparison.Ordinal);

        var persisted = await _fx.ReadBulkSettlementByIdAsync(id);
        Assert.Equal(1, persisted.Count);
        Assert.Equal(number, persisted.No);
        Assert.Equal(0, persisted.Status);
        Assert.False(persisted.IsDeleted);
        Assert.Equal(1500m, persisted.TotalAmount);
        Assert.Equal(customerId, persisted.CustomerId);
        Assert.Equal("HTTP481 散货结算", persisted.Remark);

        var (detailStatus, detailRoot, _) = await SendAsync(admin, HttpMethod.Get, $"/api/finance/bulk-settlements/{id}");
        Assert.Equal(HttpStatusCode.OK, detailStatus);
        Assert.Equal(number, detailRoot.GetProperty("data").GetProperty("settlementNo").GetString());

        var editBody = BulkSettlementBody(customerId, totalAmount: 1200m,
            settlementNo: "HACK-SJ-481", remark: "更新备注");
        editBody["status"] = 5;
        editBody["isDeleted"] = true;
        var (editStatus, editRoot, _) = await SendAsync(admin, HttpMethod.Put,
            $"/api/finance/bulk-settlements/{id}", editBody);
        Assert.Equal(HttpStatusCode.OK, editStatus);
        Assert.Equal(0, editRoot.GetProperty("code").GetInt32());

        var reloaded = await _fx.ReadBulkSettlementByIdAsync(id);
        Assert.Equal(number, reloaded.No);
        Assert.Equal(0, reloaded.Status);
        Assert.False(reloaded.IsDeleted);
        Assert.Equal(1200m, reloaded.TotalAmount);
        Assert.Equal("更新备注", reloaded.Remark);
    }

    // ==================== 2. 伪造结算单号：持久化号码由服务端权威生成 ====================

    [Fact]
    public async Task 伪造结算单号_真实HTTP_持久化号码由服务端权威生成()
    {
        Guard();
        var tag = Tag();
        using var admin = await _fx.LoginAsAdminAsync();
        var customerId = await CreateCustomerAsync(admin, tag);

        var (containerId, containerNo) = await CreateContainerSettlementAsync(admin,
            ContainerSettlementBody(customerId, totalAmount: 700m, settlementNo: "HACK-ZJ-HTTP481-" + tag));
        Assert.StartsWith("ZJ", containerNo, StringComparison.Ordinal);
        Assert.DoesNotContain("HACK", containerNo, StringComparison.Ordinal);
        Assert.Equal(containerNo, (await _fx.ReadContainerSettlementByIdAsync(containerId)).No);

        var (bulkId, bulkNo) = await CreateBulkSettlementAsync(admin,
            BulkSettlementBody(customerId, totalAmount: 700m, settlementNo: "HACK-SJ-HTTP481-" + tag));
        Assert.StartsWith("SJ", bulkNo, StringComparison.Ordinal);
        Assert.DoesNotContain("HACK", bulkNo, StringComparison.Ordinal);
        Assert.Equal(bulkNo, (await _fx.ReadBulkSettlementByIdAsync(bulkId)).No);
    }

    // ==================== 3. 显式来源装柜清单：关联 / 候选 / 显式清空 ====================

    [Fact]
    public async Task 显式来源装柜清单_真实HTTP关联候选与显式清空_保留未链接语义()
    {
        Guard();
        var tag = Tag();
        using var admin = await _fx.LoginAsAdminAsync();
        var customerId = await CreateCustomerAsync(admin, tag);
        var otherCustomerId = await CreateCustomerAsync(admin, tag + "b");
        var loadingListId = await _fx.SeedLoadingListAsync("LL-HTTP481-" + tag, "CONT-HTTP481-" + tag, customerId);
        await _fx.SeedLoadingListAsync("LL-HTTP481-OTHER-" + tag, "CONT-HTTP481-OTHER-" + tag, otherCustomerId);

        // 显式关联既有来源装柜清单（可空引用，按 Id 精确解析）。
        var (settlementId, number) = await CreateContainerSettlementAsync(admin,
            ContainerSettlementBody(customerId, loadingListId, totalAmount: 900m));
        var linked = await _fx.ReadContainerSettlementByIdAsync(settlementId);
        Assert.Equal(loadingListId, linked.LoadingListId);

        // 既有来源候选接口：按精确客户 + 数据范围返回，范围外客户清单绝不出现。
        var (candidateStatus, candidateRoot, _) = await SendAsync(admin, HttpMethod.Get,
            "/api/finance/container-settlements/loading-list-candidates?customerId=" + customerId
            + "&page=1&pageSize=20");
        Assert.Equal(HttpStatusCode.OK, candidateStatus);
        Assert.Equal(0, candidateRoot.GetProperty("code").GetInt32());
        var candidate = candidateRoot.GetProperty("data").GetProperty("items").EnumerateArray()
            .Single(c => c.GetProperty("loadingListId").GetInt64() == loadingListId);
        Assert.True(candidate.GetProperty("eligible").GetBoolean());
        Assert.DoesNotContain(candidateRoot.GetProperty("data").GetProperty("items").EnumerateArray(),
            c => c.GetProperty("customerId").GetInt64() == otherCustomerId);

        // 已存储来源只读展示按规范 Id 可达。
        var (sourceStatus, sourceRoot, _) = await SendAsync(admin, HttpMethod.Get,
            $"/api/finance/container-settlements/{settlementId}/loading-list-source");
        Assert.Equal(HttpStatusCode.OK, sourceStatus);
        Assert.True(sourceRoot.GetProperty("data").GetProperty("linked").GetBoolean());
        Assert.Equal(loadingListId, sourceRoot.GetProperty("data").GetProperty("loadingListId").GetInt64());

        // 显式清空来源（提交 null = 声明式可空引用留空）：单号 / 状态不变，来源归一为未关联。
        var clearedBody = ContainerSettlementBody(customerId, null, totalAmount: 900m, remark: "更新备注");
        var (clearStatus, clearRoot, _) = await SendAsync(admin, HttpMethod.Put,
            $"/api/finance/container-settlements/{settlementId}", clearedBody);
        Assert.Equal(HttpStatusCode.OK, clearStatus);
        Assert.Equal(0, clearRoot.GetProperty("code").GetInt32());
        var cleared = await _fx.ReadContainerSettlementByIdAsync(settlementId);
        Assert.Null(cleared.LoadingListId);
        Assert.Equal(number, cleared.No);
        Assert.Equal(0, cleared.Status);

        // 显式非正数来源 Id（0）按非法引用 fail closed 拒绝：零写入、已存来源与金额不被改写。
        var invalidBody = ContainerSettlementBody(customerId, 0L, totalAmount: 300m, remark: "不应写入");
        var (invalidStatus, invalidRoot, _) = await SendAsync(admin, HttpMethod.Put,
            $"/api/finance/container-settlements/{settlementId}", invalidBody);
        Assert.Equal(HttpStatusCode.OK, invalidStatus);
        Assert.Equal(1001, invalidRoot.GetProperty("code").GetInt32());
        var afterInvalid = await _fx.ReadContainerSettlementByIdAsync(settlementId);
        Assert.Null(afterInvalid.LoadingListId);
        Assert.Equal(900m, afterInvalid.TotalAmount);
        Assert.Equal("更新备注", afterInvalid.Remark);
    }

    // ==================== 4. 有效生命周期与非法流转（真实 HTTP，既有授权） ====================

    [Fact]
    public async Task 结算单_真实HTTP生命周期_提交审核取消与非法流转零状态变化()
    {
        Guard();
        var tag = Tag();
        using var admin = await _fx.LoginAsAdminAsync();
        var customerId = await CreateCustomerAsync(admin, tag);

        var (containerId, containerNo) = await CreateContainerSettlementAsync(admin,
            ContainerSettlementBody(customerId, totalAmount: 800m));

        // 待提交直接审核 → 受控拒绝（规则冲突），零状态变化。
        var earlyApprove = await SendAsync(admin, HttpMethod.Post,
            $"/api/finance/container-settlements/{containerId}/approve");
        Assert.Equal(1004, earlyApprove.Root.GetProperty("code").GetInt32());
        Assert.Equal(0, (await _fx.ReadContainerSettlementByIdAsync(containerId)).Status);

        var submit = await SendAsync(admin, HttpMethod.Post,
            $"/api/finance/container-settlements/{containerId}/submit");
        Assert.Equal(0, submit.Root.GetProperty("code").GetInt32());
        Assert.Equal(1, (await _fx.ReadContainerSettlementByIdAsync(containerId)).Status);

        var approve = await SendAsync(admin, HttpMethod.Post,
            $"/api/finance/container-settlements/{containerId}/approve");
        Assert.Equal(0, approve.Root.GetProperty("code").GetInt32());
        Assert.Equal(2, (await _fx.ReadContainerSettlementByIdAsync(containerId)).Status);

        var cancel = await SendAsync(admin, HttpMethod.Post,
            $"/api/finance/container-settlements/{containerId}/cancel");
        Assert.Equal(0, cancel.Root.GetProperty("code").GetInt32());
        Assert.Equal(5, (await _fx.ReadContainerSettlementByIdAsync(containerId)).Status);

        // 重复取消 → 受控拒绝，零状态变化；结算单号不变。
        var repeatCancel = await SendAsync(admin, HttpMethod.Post,
            $"/api/finance/container-settlements/{containerId}/cancel");
        Assert.Equal(1004, repeatCancel.Root.GetProperty("code").GetInt32());
        var storedContainer = await _fx.ReadContainerSettlementByIdAsync(containerId);
        Assert.Equal(5, storedContainer.Status);
        Assert.Equal(containerNo, storedContainer.No);
        Assert.False(storedContainer.IsDeleted);

        // 散货结算单同样按既有授权执行生命周期。
        var (bulkId, bulkNo) = await CreateBulkSettlementAsync(admin,
            BulkSettlementBody(customerId, totalAmount: 800m));

        Assert.Equal(0, (await SendAsync(admin, HttpMethod.Post,
            $"/api/finance/bulk-settlements/{bulkId}/submit")).Root.GetProperty("code").GetInt32());
        Assert.Equal(1, (await _fx.ReadBulkSettlementByIdAsync(bulkId)).Status);

        Assert.Equal(0, (await SendAsync(admin, HttpMethod.Post,
            $"/api/finance/bulk-settlements/{bulkId}/approve")).Root.GetProperty("code").GetInt32());
        Assert.Equal(2, (await _fx.ReadBulkSettlementByIdAsync(bulkId)).Status);

        Assert.Equal(0, (await SendAsync(admin, HttpMethod.Post,
            $"/api/finance/bulk-settlements/{bulkId}/cancel")).Root.GetProperty("code").GetInt32());
        var storedBulk = await _fx.ReadBulkSettlementByIdAsync(bulkId);
        Assert.Equal(5, storedBulk.Status);
        Assert.Equal(bulkNo, storedBulk.No);
        Assert.False(storedBulk.IsDeleted);
    }

    // ==================== 5. 非法请求：受控拒绝 + 零写入零占号（真实 HTTP） ====================

    [Theory]
    [InlineData("noIdentity")]
    [InlineData("missingMenu")]
    [InlineData("unknownCustomer")]
    [InlineData("nonPositiveAmount")]
    [InlineData("negativeOtherCost")]
    [InlineData("danglingSource")]
    [InlineData("cancelledSource")]
    [InlineData("crossCustomerSource")]
    public async Task 装柜结算单非法请求_真实HTTP_受控拒绝且零写入零占号(string scenario)
    {
        Guard();
        var tag = Tag();
        using var admin = await _fx.LoginAsAdminAsync();
        var customerId = await CreateCustomerAsync(admin, tag);
        var otherCustomerId = await CreateCustomerAsync(admin, tag + "b");
        var cancelledListId = await _fx.SeedLoadingListAsync(
            "LL-HTTP481-CAN-" + tag, "CONT-CAN-" + tag, customerId, DocumentStatus.Cancelled);
        var foreignListId = await _fx.SeedLoadingListAsync(
            "LL-HTTP481-FOR-" + tag, "CONT-FOR-" + tag, otherCustomerId);
        var before = await _fx.ReadSnapshotAsync();

        HttpClient? temporary = null;
        var client = admin;
        var body = ContainerSettlementBody(customerId, totalAmount: 1000m);
        var expected = 1001;
        var expectUnauthorized = false;
        switch (scenario)
        {
            case "noIdentity":
                client = _fx.CreateAnonymousClient();
                expectUnauthorized = true;
                break;
            case "missingMenu":
                var userName = "http481-container-" + tag;
                var password = "Http481@" + tag;
                await _fx.CreateUserWithoutRolesAsync(admin, userName, password);
                temporary = await _fx.LoginAsync(userName, password);
                client = temporary;
                expected = 2002;
                break;
            case "unknownCustomer":
                body = ContainerSettlementBody(987654321L, totalAmount: 1000m);
                expected = 1002;
                break;
            case "nonPositiveAmount":
                body["totalAmount"] = 0m;
                break;
            case "negativeOtherCost":
                body["otherCost"] = -1m;
                break;
            case "danglingSource":
                body = ContainerSettlementBody(customerId, 987654321L, totalAmount: 1000m);
                expected = 1002;
                break;
            case "cancelledSource":
                body = ContainerSettlementBody(customerId, cancelledListId, totalAmount: 1000m);
                expected = 1004;
                break;
            default:
                body = ContainerSettlementBody(customerId, foreignListId, totalAmount: 1000m);
                expected = 1004;
                break;
        }

        try
        {
            var (status, root, _) = await SendAsync(client, HttpMethod.Post,
                "/api/finance/container-settlements", body);
            var after = await _fx.ReadSnapshotAsync();

            if (expectUnauthorized)
            {
                Assert.Equal(HttpStatusCode.Unauthorized, status);
                Assert.Equal(2000, root.GetProperty("code").GetInt32());
            }
            else
            {
                Assert.Equal(HttpStatusCode.OK, status);
                Assert.Equal(expected, root.GetProperty("code").GetInt32());
            }

            // 零写入、零占号、来源清单状态 / 删除态不变。
            Assert.Equal(before.ContainerCount, after.ContainerCount);
            Assert.Equal(before.BulkCount, after.BulkCount);
            Assert.Equal(before.ContainerSequence, after.ContainerSequence);
            Assert.Equal(before.BulkSequence, after.BulkSequence);
            Assert.Equal(DocumentStatus.Pending, await _fx.ReadLoadingListStatusAsync(foreignListId));
            Assert.Equal(DocumentStatus.Cancelled, await _fx.ReadLoadingListStatusAsync(cancelledListId));
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
    [InlineData("nonPositiveAmount")]
    [InlineData("negativeFreight")]
    public async Task 散货结算单非法请求_真实HTTP_受控拒绝且零写入零占号(string scenario)
    {
        Guard();
        var tag = Tag();
        using var admin = await _fx.LoginAsAdminAsync();
        var customerId = await CreateCustomerAsync(admin, tag);
        var before = await _fx.ReadSnapshotAsync();

        HttpClient? temporary = null;
        var client = admin;
        var body = BulkSettlementBody(customerId, totalAmount: 1000m);
        var expected = 1001;
        var expectUnauthorized = false;
        switch (scenario)
        {
            case "noIdentity":
                client = _fx.CreateAnonymousClient();
                expectUnauthorized = true;
                break;
            case "missingMenu":
                var userName = "http481-bulk-" + tag;
                var password = "Http481@" + tag;
                await _fx.CreateUserWithoutRolesAsync(admin, userName, password);
                temporary = await _fx.LoginAsync(userName, password);
                client = temporary;
                expected = 2002;
                break;
            case "unknownCustomer":
                body = BulkSettlementBody(987654321L, totalAmount: 1000m);
                expected = 1002;
                break;
            case "nonPositiveAmount":
                body["totalAmount"] = 0m;
                break;
            default:
                body["freightCost"] = -1m;
                break;
        }

        try
        {
            var (status, root, _) = await SendAsync(client, HttpMethod.Post, "/api/finance/bulk-settlements", body);
            var after = await _fx.ReadSnapshotAsync();

            if (expectUnauthorized)
            {
                Assert.Equal(HttpStatusCode.Unauthorized, status);
                Assert.Equal(2000, root.GetProperty("code").GetInt32());
            }
            else
            {
                Assert.Equal(HttpStatusCode.OK, status);
                Assert.Equal(expected, root.GetProperty("code").GetInt32());
            }

            Assert.Equal(before.ContainerCount, after.ContainerCount);
            Assert.Equal(before.BulkCount, after.BulkCount);
            Assert.Equal(before.ContainerSequence, after.ContainerSequence);
            Assert.Equal(before.BulkSequence, after.BulkSequence);
        }
        finally
        {
            temporary?.Dispose();
        }
    }

    // ==================== 6. 旧族分离：旧写路径 fail closed，规范入口不受影响 ====================

    [Fact]
    public async Task 旧族分离_旧写路径fail_closed_规范入口不受影响()
    {
        Guard();
        var tag = Tag();
        using var admin = await _fx.LoginAsAdminAsync();
        var customerId = await CreateCustomerAsync(admin, tag);

        // 旧写路径（ERP-404 门禁）：即使具备既有功能菜单也整体 fail closed（绝不调用旧结算存储过程）。
        var legacyContainer = new Dictionary<string, object?>
        {
            ["Action"] = "add",
            ["BillNo"] = "LEGACY-HTTP481-ZJ-" + tag,
            ["CustomerId"] = customerId,
            ["TotalAmount"] = 100m
        };
        var (legacyContainerStatus, legacyContainerRoot, _) = await SendAsync(admin, HttpMethod.Post,
            "/api/v2/bills/container-settlement/save", legacyContainer);
        Assert.Equal(HttpStatusCode.OK, legacyContainerStatus);
        Assert.NotEqual(0, legacyContainerRoot.GetProperty("code").GetInt32());

        var legacyBulk = new Dictionary<string, object?>
        {
            ["Action"] = "add",
            ["BillNo"] = "LEGACY-HTTP481-SJ-" + tag,
            ["CustomerId"] = customerId,
            ["TotalAmount"] = 100m
        };
        var (legacyBulkStatus, legacyBulkRoot, _) = await SendAsync(admin, HttpMethod.Post,
            "/api/v2/bills/bulk-settlement/save", legacyBulk);
        Assert.Equal(HttpStatusCode.OK, legacyBulkStatus);
        Assert.NotEqual(0, legacyBulkRoot.GetProperty("code").GetInt32());

        // 规范入口（api/finance/container-settlements、api/finance/bulk-settlements，规范 Id 口径）在同一次运行中仍可正常写入：
        // 旧 Oid 写路径被停用不影响规范 Id 入口。
        var (containerId, containerNo) = await CreateContainerSettlementAsync(admin,
            ContainerSettlementBody(customerId, totalAmount: 200m));
        var (bulkId, bulkNo) = await CreateBulkSettlementAsync(admin,
            BulkSettlementBody(customerId, totalAmount: 200m));
        Assert.True(containerId > 0 && bulkId > 0);
        Assert.StartsWith("ZJ", containerNo, StringComparison.Ordinal);
        Assert.StartsWith("SJ", bulkNo, StringComparison.Ordinal);
        Assert.Equal(1, (await _fx.ReadContainerSettlementByIdAsync(containerId)).Count);
        Assert.Equal(1, (await _fx.ReadBulkSettlementByIdAsync(bulkId)).Count);
    }
}

/// <summary>
/// ERP-481 真实 HTTP + 真实隔离 SQL 夹具：目标必须是专用实例 <c>(localdb)\NEWERP_AutoAcceptance</c>、
/// 库名前缀 <c>NEWERP_AUTOTEST</c> 且 <c>Integrated Security=true</c>；只使用<strong>全新 GUID 后缀库</strong>，
/// 发现同名库已存在立即拒绝（绝不 drop / reset / 复用）；连接串只来自进程环境变量或专用 localdb 默认值。
/// <para>启动真实 ERP.Api 进程（真实 Kestrel / JWT 认证 / 既有授权），并为该进程注入<b>进程内临时 JWT 签名密钥</b>：
/// 随机生成、只存在于该进程的环境变量、绝不落盘、绝不复用空白签名配置；测试账号仍是既有种子账号（不新增 / 不持久化凭据）。</para>
/// </summary>
public sealed class FinanceSettlementCanonicalEntrySqlServerFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";
    public const string AdminUser = "admin";
    public const string AdminPassword = "Admin@123";

    private const string DefaultDatabaseName = DatabasePrefix + "_FINANCESETTLEMENTCANONICAL";
    private const string PortVariable = "ERP_481_HTTP_PORT";
    private const string DefaultPort = "5398";

    private readonly StringBuilder _apiConsole = new();
    private readonly object _apiConsoleLock = new();
    private Process? _apiProcess;

    public string ConnectionString { get; private set; } = string.Empty;
    public string BaseUrl { get; private set; } = string.Empty;

    /// <summary>按规范 Id 直接读取的结算单行（单号 / 状态 / 删除态 / 金额 / 客户 / 可空来源清单 / 备注）。</summary>
    public readonly record struct SettlementRow(
        int Count, long Id, string? No, int Status, bool IsDeleted,
        decimal TotalAmount, decimal FreightCost, decimal OtherCost,
        long CustomerId, long? LoadingListId, string? Remark);

    /// <summary>零副作用拒绝所需的整库计数快照（两类结算单 + 两类单号序列）。</summary>
    public readonly record struct WriteSnapshot(
        long ContainerCount, long BulkCount, long ContainerSequence, long BulkSequence);

    public async Task InitializeAsync()
    {
        var configured = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        var connectionString = string.IsNullOrWhiteSpace(configured)
            ? $"Server=(localdb)\\{InstanceMarker};Initial Catalog={DefaultDatabaseName}_{Guid.NewGuid():N};"
              + "Integrated Security=true;TrustServerCertificate=true;"
            : configured;

        // 访问任何数据库之前：实例 / 库名前缀 / 集成安全 + 显式测试上下文（不满足立即 fail-closed）。
        AssertDedicatedTarget(connectionString);
        TestDatabaseSafetyGuard.EnsureApprovedTarget(connectionString, "结算单规范入口集成验收");
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
    /// 在本次全新隔离库内播种一张既有装柜清单（结算单来源为<b>夹具数据</b>）：
    /// 只写既有 <c>ContainerLoadingLists</c> 表，不新增表 / 列 / 权限，也不触碰任何结算单。
    /// </summary>
    public async Task<long> SeedLoadingListAsync(
        string listNo, string containerNo, long customerId,
        DocumentStatus status = DocumentStatus.Pending)
    {
        AssertDedicatedTarget(ConnectionString);
        var options = new DbContextOptionsBuilder<ErpDbContext>().UseSqlServer(ConnectionString).Options;
        await using var db = new ErpDbContext(options);
        var list = new ContainerLoadingList
        {
            LoadingListNo = listNo,
            LoadingDate = DateTime.Today.AddDays(-4),
            ContainerNo = containerNo,
            CustomerId = customerId,
            Status = status
        };
        db.ContainerLoadingLists.Add(list);
        await db.SaveChangesAsync();
        return list.Id;
    }

    // ==================== 直接 SQL 复核（同一全新 GUID 库，只读） ====================

    /// <summary>按规范 Id 直接读取装柜结算单行。</summary>
    public Task<SettlementRow> ReadContainerSettlementByIdAsync(long id)
        => ReadSettlementByIdAsync(
            "SELECT TOP 1 Id, SettlementNo, Status, IsDeleted, TotalAmount, FreightCost, OtherCost, CustomerId, "
            + "LoadingListId, Remark FROM db_owner.FinanceContainerSettlements WHERE Id = @key",
            id, hasSourceColumns: true);

    /// <summary>按规范 Id 直接读取散货结算单行（散货结算单没有其他费用 / 来源单据字段）。</summary>
    public Task<SettlementRow> ReadBulkSettlementByIdAsync(long id)
        => ReadSettlementByIdAsync(
            "SELECT TOP 1 Id, SettlementNo, Status, IsDeleted, TotalAmount, FreightCost, CustomerId, Remark "
            + "FROM db_owner.FinanceBulkSettlements WHERE Id = @key",
            id, hasSourceColumns: false);

    private async Task<SettlementRow> ReadSettlementByIdAsync(string sql, long id, bool hasSourceColumns)
    {
        AssertDedicatedTarget(ConnectionString);
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@key", id);
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            return new SettlementRow(0, 0, null, -1, false, 0m, 0m, 0m, 0L, null, null);

        var otherCost = hasSourceColumns ? reader.GetDecimal(6) : 0m;
        var customerId = reader.GetInt64(hasSourceColumns ? 7 : 6);
        long? loadingListId = hasSourceColumns
            ? (reader.IsDBNull(8) ? (long?)null : reader.GetInt64(8))
            : null;
        var remarkOrdinal = hasSourceColumns ? 9 : 7;
        return new SettlementRow(1, reader.GetInt64(0), reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.GetInt32(2), reader.GetBoolean(3), reader.GetDecimal(4), reader.GetDecimal(5), otherCost,
            customerId, loadingListId,
            reader.IsDBNull(remarkOrdinal) ? null : reader.GetString(remarkOrdinal));
    }

    /// <summary>读取装柜清单当前状态（用于证明被拒绝的请求绝不改写来源状态）。</summary>
    public async Task<DocumentStatus> ReadLoadingListStatusAsync(long loadingListId)
    {
        AssertDedicatedTarget(ConnectionString);
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT TOP 1 Status FROM db_owner.ContainerLoadingLists WHERE Id = @key";
        cmd.Parameters.AddWithValue("@key", loadingListId);
        var value = await cmd.ExecuteScalarAsync();
        return value is null or DBNull ? DocumentStatus.Pending : (DocumentStatus)Convert.ToInt32(value);
    }

    /// <summary>零副作用拒绝所需的整库计数快照（两类结算单 + 装柜 / 散货结算单号序列）。</summary>
    public async Task<WriteSnapshot> ReadSnapshotAsync()
    {
        AssertDedicatedTarget(ConnectionString);
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        // DocumentType.ContainerSettlement = 13、DocumentType.BulkSettlement = 14（与 ERP.Domain.Enums 同源；不新增任何映射）。
        cmd.CommandText = "SELECT (SELECT COUNT(1) FROM db_owner.FinanceContainerSettlements), "
                          + "(SELECT COUNT(1) FROM db_owner.FinanceBulkSettlements), "
                          + "ISNULL((SELECT TOP 1 CurrentSequence FROM db_owner.SysDocumentNumberRules "
                          + "WHERE DocumentType = 13 AND IsDeleted = 0), 0), "
                          + "ISNULL((SELECT TOP 1 CurrentSequence FROM db_owner.SysDocumentNumberRules "
                          + "WHERE DocumentType = 14 AND IsDeleted = 0), 0)";
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
            File.WriteAllText(Path.Combine(evidenceDirectory, "finance-settlement-canonical-entry-api-console.log"),
                ApiConsoleTail(int.MaxValue));
        }
        catch (IOException) { /* 证据写入失败不影响断言结果 */ }
    }
}

/// <summary>专用目标护栏的 fail-closed 覆盖：错误实例 / 错误库名 / 非集成安全必须在访问数据库之前被拒绝。</summary>
public sealed class FinanceSettlementCanonicalEntryTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;User Id=sa;Password=secret")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(
            () => FinanceSettlementCanonicalEntrySqlServerFixture.AssertDedicatedTarget(connection));
}
