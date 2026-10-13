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
/// ERP-477 采购入库规范主子表入口的<b>真实 HTTP + 真实隔离 SQL</b> 验收（真实 ERP.Api 进程 + 全新 GUID <c>NEWERP_AUTOTEST</c> 库）：
/// <list type="number">
/// <item><b>真实 HTTP</b>：请求经真实 Kestrel 管线（MVC 绑定 + <c>[ApiController]</c> 模型校验 + JWT 认证 + 既有授权），
/// 因此「省略入库单号」由<b>绑定层</b>证明（修复前正是绑定层因 <c>[Required]</c> 返回 HTTP 400
/// <c>{"errors":{"StockInNo":[...]}}</c>），直接实例化控制器的测试无法证明这一点；夹具为真实 API 进程注入
/// <b>进程内临时 JWT 签名密钥</b>（随机生成、只存活在该进程、绝不落盘 / 绝不继承空白签名配置），认证链路保持真实。</item>
/// <item><b>权威单号与规范 Id</b>：省略 / 伪造入库单号一律持久化服务端生成单号，返回规范 <c>Id</c>；明细主键由服务端生成。</item>
/// <item><b>主子表保存 / 回填 / 修改</b>：真实 HTTP 新增带明细的入库单，详情接口回填明细，修改后再次回读一致（数量与合计重算）。</item>
/// <item><b>手工未链接与显式来源</b>：未链接入库（<c>purchaseOrderId</c> 省略 / null）审核按既有口径过账；显式来源经
/// <c>ERP-375</c> 只读候选 / 详情接口按权威剩余可收数量回填后保存 → 审核过账恰好一次（库存 + 流水）。</item>
/// <item><b>零副作用拒绝</b>：容量超限 / 非法来源 / 供应商不存在 / 未审核来源 / 无身份 / 无「采购入库」菜单一律受控拒绝，
/// 且入库单、明细、库存、流水与单号序列零变化；既有取消冲销守卫保持（红字流水把库存还原）。</item>
/// <item><b>专用目标护栏</b>：必须在任何数据库访问之前精确命中 <c>(localdb)\NEWERP_AutoAcceptance</c> + 库名前缀
/// <c>NEWERP_AUTOTEST</c> + <c>Integrated Security=true</c>；每次运行只创建一个全新 GUID 后缀库，发现同名库已存在立即拒绝，
/// 绝不 drop / reset / 复用任何数据库，也绝不读取 appsettings / .env / 生产凭据。</item>
/// </list>
/// <para>构建完成不等于阶段验收：只有下列场景在专用 localdb 上真实执行通过才算验收证据。</para>
/// </summary>
public sealed class StockInCanonicalEntrySqlServerTests : IClassFixture<StockInCanonicalEntrySqlServerFixture>
{
    private readonly StockInCanonicalEntrySqlServerFixture _fx;

    public StockInCanonicalEntrySqlServerTests(StockInCanonicalEntrySqlServerFixture fixture) => _fx = fixture;

    /// <summary>访问数据库前复核专用目标护栏（实例名 / 库名前缀 / 集成安全）。</summary>
    private void Guard()
    {
        var target = new SqlConnectionStringBuilder(_fx.ConnectionString);
        Assert.Equal($"(localdb)\\{StockInCanonicalEntrySqlServerFixture.InstanceMarker}",
            target.DataSource, ignoreCase: true);
        Assert.StartsWith(StockInCanonicalEntrySqlServerFixture.DatabasePrefix, target.InitialCatalog,
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

    /// <summary>采购入库正文：与前端 crud.js 提交同形（可省略入库单号；来源可省略 / 显式 null = 未链接）。</summary>
    private static Dictionary<string, object?> StockInBody(long supplierId, long warehouseId, long productId,
        decimal quantity, long? purchaseOrderId = null, bool includeSourceKey = true, string? stockInNo = null)
    {
        var body = new Dictionary<string, object?>
        {
            ["stockInDate"] = DateTime.Today.ToString("yyyy-MM-dd"),
            ["supplierId"] = supplierId,
            ["warehouseId"] = warehouseId,
            ["remark"] = "HTTP477",
            ["details"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["productId"] = productId, ["productName"] = "HTTP477 商品", ["spec"] = "标准",
                    ["unit"] = "PCS", ["quantity"] = quantity
                }
            }
        };
        if (includeSourceKey) body["purchaseOrderId"] = purchaseOrderId;
        if (stockInNo is not null) body["stockInNo"] = stockInNo;
        return body;
    }

    private static async Task<long> CreateSupplierAsync(HttpClient client, string tag)
    {
        var payload = JsonSerializer.Serialize(new { supplierCode = "S-HTTP477-" + tag, supplierName = "HTTP477 供应商 " + tag });
        using var response = await client.PostAsync("/api/base/suppliers",
            new StringContent(payload, Encoding.UTF8, "application/json"));
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(0, doc.RootElement.GetProperty("code").GetInt32());
        return doc.RootElement.GetProperty("data").GetProperty("id").GetInt64();
    }

    private static async Task<long> CreateWarehouseAsync(HttpClient client, string tag)
    {
        var payload = JsonSerializer.Serialize(new { warehouseCode = "W-HTTP477-" + tag, warehouseName = "HTTP477 仓库 " + tag });
        using var response = await client.PostAsync("/api/base/warehouses",
            new StringContent(payload, Encoding.UTF8, "application/json"));
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(0, doc.RootElement.GetProperty("code").GetInt32());
        return doc.RootElement.GetProperty("data").GetProperty("id").GetInt64();
    }

    private static async Task<long> CreateProductAsync(HttpClient client, string tag)
    {
        var payload = JsonSerializer.Serialize(new { productCode = "P-HTTP477-" + tag, productName = "HTTP477 商品 " + tag, unit = "PCS" });
        using var response = await client.PostAsync("/api/base/products",
            new StringContent(payload, Encoding.UTF8, "application/json"));
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(0, doc.RootElement.GetProperty("code").GetInt32());
        return doc.RootElement.GetProperty("data").GetProperty("id").GetInt64();
    }

    /// <summary>经既有采购订单接口产生<b>真实来源单据</b>（新建 → 可选提交 / 审核）；不新增任何接口或授权。</summary>
    private static async Task<(long Id, string No)> CreateOrderAsync(HttpClient client, string tag,
        long supplierId, long productId, decimal quantity, bool approve)
    {
        var payload = JsonSerializer.Serialize(new
        {
            orderDate = DateTime.Today.ToString("yyyy-MM-dd"),
            supplierId,
            currency = "CNY",
            exchangeRate = 1m,
            taxRate = 0m,
            contractNo = "PC-HTTP477-" + tag,
            paymentTerms = "月结 30 天",
            details = new object[]
            {
                new { productId, productName = "HTTP477 采购商品", spec = "标准", unit = "PCS", quantity, unitPrice = 5m }
            }
        });
        using var create = await client.PostAsync("/api/purchase-orders",
            new StringContent(payload, Encoding.UTF8, "application/json"));
        using var created = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
        Assert.Equal(0, created.RootElement.GetProperty("code").GetInt32());
        var data = created.RootElement.GetProperty("data");
        var id = data.GetProperty("id").GetInt64();
        var no = data.GetProperty("orderNo").GetString()!;

        if (approve)
        {
            var (submitStatus, submitRoot, _) = await SendAsync(client, HttpMethod.Post, $"/api/purchase-orders/{id}/submit");
            Assert.Equal(HttpStatusCode.OK, submitStatus);
            Assert.Equal(0, submitRoot.GetProperty("code").GetInt32());
            var (approveStatus, approveRoot, _) = await SendAsync(client, HttpMethod.Post, $"/api/purchase-orders/{id}/approve");
            Assert.Equal(HttpStatusCode.OK, approveStatus);
            Assert.Equal(0, approveRoot.GetProperty("code").GetInt32());
        }
        return (id, no);
    }

    private static async Task<(long Id, string No)> CreateStockInAsync(HttpClient client, object body)
    {
        var (status, root, _) = await SendAsync(client, HttpMethod.Post, "/api/stock-ins", body);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(0, root.GetProperty("code").GetInt32());
        var data = root.GetProperty("data");
        return (data.GetProperty("id").GetInt64(), data.GetProperty("stockInNo").GetString()!);
    }

    // ==================== 1. 省略 / 伪造入库单号（真实 HTTP，绑定层证明） ====================

    [Fact]
    public async Task 省略入库单号_真实HTTP新增成功_服务端权威单号与规范Id落库()
    {
        Guard();
        var tag = Tag();
        using var admin = await _fx.LoginAsAdminAsync();
        var supplierId = await CreateSupplierAsync(admin, tag);
        var warehouseId = await CreateWarehouseAsync(admin, tag);
        var productId = await CreateProductAsync(admin, tag);

        var (status, root, raw) = await SendAsync(admin, HttpMethod.Post, "/api/stock-ins",
            StockInBody(supplierId, warehouseId, productId, 5m));

        // 修复前：绑定层因 StockInNo 的显式 [Required] 返回 HTTP 400；现在必须成功。
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(0, root.GetProperty("code").GetInt32());
        Assert.DoesNotContain("StockInNo field is required", raw, StringComparison.OrdinalIgnoreCase);

        var data = root.GetProperty("data");
        var id = data.GetProperty("id").GetInt64();
        var number = data.GetProperty("stockInNo").GetString()!;
        Assert.True(id > 0);                                     // 规范 Id（EF 自增主键）
        Assert.StartsWith("RK", number, StringComparison.Ordinal);

        var persisted = await _fx.ReadByNumberAsync(number);
        Assert.Equal(1, persisted.Count);
        Assert.Equal(id, persisted.Id);
        Assert.Equal(0, persisted.Status);                       // Pending
        Assert.False(persisted.IsDeleted);
        Assert.Null(persisted.PurchaseOrderId);                  // 未选择来源 = 未链接
        Assert.Equal(5m, persisted.TotalQuantity);
        Assert.Equal(1, await _fx.CountDetailsAsync(id));

        // 列表：规范集合端点按权威单号可检索到刚写入的入库单（列表 / 表单同一规范 Id 口径）。
        var (listed, listRoot, _) = await SendAsync(admin, HttpMethod.Get,
            "/api/stock-ins?page=1&pageSize=5&keyword=" + Uri.EscapeDataString(number));
        Assert.Equal(HttpStatusCode.OK, listed);
        Assert.Equal(0, listRoot.GetProperty("code").GetInt32());
        Assert.True(listRoot.GetProperty("data").GetProperty("total").GetInt64() >= 1);
        Assert.Equal(id, listRoot.GetProperty("data").GetProperty("items")[0].GetProperty("id").GetInt64());
    }

    [Fact]
    public async Task 伪造入库单号_真实HTTP_持久化号码由服务端权威生成()
    {
        Guard();
        var tag = Tag();
        using var admin = await _fx.LoginAsAdminAsync();
        var supplierId = await CreateSupplierAsync(admin, tag);
        var warehouseId = await CreateWarehouseAsync(admin, tag);
        var productId = await CreateProductAsync(admin, tag);
        var forged = "HACK-HTTP477-" + tag;

        var (id, number) = await CreateStockInAsync(admin,
            StockInBody(supplierId, warehouseId, productId, 2m, stockInNo: forged));

        Assert.StartsWith("RK", number, StringComparison.Ordinal);
        Assert.NotEqual(forged, number);
        var persisted = await _fx.ReadByIdAsync(id);
        Assert.Equal(number, persisted.No);
    }

    // ==================== 2. 主子表保存 / 回填 / 修改（真实 HTTP，规范 Id） ====================

    [Fact]
    public async Task 主子表_新增回填修改_按规范Id一致()
    {
        Guard();
        var tag = Tag();
        using var admin = await _fx.LoginAsAdminAsync();
        var supplierId = await CreateSupplierAsync(admin, tag);
        var warehouseId = await CreateWarehouseAsync(admin, tag);
        var productId = await CreateProductAsync(admin, tag);

        var (id, _) = await CreateStockInAsync(admin, StockInBody(supplierId, warehouseId, productId, 5m));

        var (loaded, loadedRoot, _) = await SendAsync(admin, HttpMethod.Get, $"/api/stock-ins/{id}");
        Assert.Equal(HttpStatusCode.OK, loaded);
        var loadedData = loadedRoot.GetProperty("data");
        Assert.Equal(id, loadedData.GetProperty("id").GetInt64());
        var loadedDetails = loadedData.GetProperty("details");
        Assert.Equal(1, loadedDetails.GetArrayLength());
        Assert.Equal(productId, loadedDetails[0].GetProperty("productId").GetInt64());

        // 修改：明细数量改为 3 后回读一致，合计由服务端重算。
        var edit = StockInBody(supplierId, warehouseId, productId, 3m);
        edit["remark"] = "HTTP477 修改后";
        var (updated, updatedRoot, _) = await SendAsync(admin, HttpMethod.Put, $"/api/stock-ins/{id}", edit);
        Assert.Equal(HttpStatusCode.OK, updated);
        Assert.Equal(0, updatedRoot.GetProperty("code").GetInt32());

        var (reloaded, reloadedRoot, _) = await SendAsync(admin, HttpMethod.Get, $"/api/stock-ins/{id}");
        Assert.Equal(HttpStatusCode.OK, reloaded);
        var reloadedData = reloadedRoot.GetProperty("data");
        Assert.Equal("HTTP477 修改后", reloadedData.GetProperty("remark").GetString());
        Assert.Equal(3m, reloadedData.GetProperty("totalQuantity").GetDecimal());
        Assert.Equal(3m, reloadedData.GetProperty("details")[0].GetProperty("quantity").GetDecimal());

        var persisted = await _fx.ReadByIdAsync(id);
        Assert.Equal(3m, persisted.TotalQuantity);
        Assert.Equal(1, await _fx.CountDetailsAsync(id));
    }

    // ==================== 3. 手工未链接入库：审核过账恰好一次 + 取消红字冲销 ====================

    [Fact]
    public async Task 未链接入库_审核过账恰好一次_取消红字冲销()
    {
        Guard();
        var tag = Tag();
        using var admin = await _fx.LoginAsAdminAsync();
        var supplierId = await CreateSupplierAsync(admin, tag);
        var warehouseId = await CreateWarehouseAsync(admin, tag);
        var productId = await CreateProductAsync(admin, tag);

        var (id, _) = await CreateStockInAsync(admin, StockInBody(supplierId, warehouseId, productId, 4m));
        await ApproveAsync(admin, id);

        Assert.Equal(2, (await _fx.ReadByIdAsync(id)).Status);            // Approved
        var movements = await _fx.ReadMovementsAsync(id);
        Assert.Equal(1, movements.Count);                                 // 恰好一次过账
        Assert.Equal(1, movements.Direction);
        Assert.Equal(4m, movements.Quantity);
        Assert.Equal(4m, await _fx.ReadStockQuantityAsync(warehouseId, productId));

        // 重复审核：受控拒绝，且不重复加库存 / 不产生第二条流水。
        var (_, again, _) = await SendAsync(admin, HttpMethod.Post, $"/api/stock-ins/{id}/approve");
        Assert.Equal(1004, again.GetProperty("code").GetInt32());         // ErrorCodes.RuleConflict
        Assert.Equal(1, (await _fx.ReadMovementsAsync(id)).Count);
        Assert.Equal(4m, await _fx.ReadStockQuantityAsync(warehouseId, productId));

        // 取消：既有红字冲销守卫把库存还原（Σ 流水金额 = 0）。
        var (cancelled, _, _) = await SendAsync(admin, HttpMethod.Post, $"/api/stock-ins/{id}/cancel");
        Assert.Equal(HttpStatusCode.OK, cancelled);
        Assert.Equal(5, (await _fx.ReadByIdAsync(id)).Status);            // Cancelled
        Assert.Equal(0m, await _fx.ReadStockQuantityAsync(warehouseId, productId));
        var afterCancel = await _fx.ReadMovementsAsync(id);
        Assert.Equal(2, afterCancel.Count);
        Assert.Equal(0m, afterCancel.SumAmount);
    }

    // ==================== 4. 显式来源：剩余可收 / 审核 / 超收拒绝 / 释放额度 ====================

    [Fact]
    public async Task 显式来源_按剩余可收保存审核_超收拒绝且取消释放额度()
    {
        Guard();
        var tag = Tag();
        using var admin = await _fx.LoginAsAdminAsync();
        var supplierId = await CreateSupplierAsync(admin, tag);
        var warehouseId = await CreateWarehouseAsync(admin, tag);
        var productId = await CreateProductAsync(admin, tag);
        var (orderId, orderNo) = await CreateOrderAsync(admin, tag, supplierId, productId, 10m, approve: true);

        // ERP-375 只读候选 / 详情：权威剩余可收数量（未审核入库不占用名额）。
        var (candidateStatus, candidateRoot, _) = await SendAsync(admin, HttpMethod.Get,
            $"/api/stock-ins/source-candidates?supplierId={supplierId}&take=10");
        Assert.Equal(HttpStatusCode.OK, candidateStatus);
        var candidate = candidateRoot.GetProperty("data").EnumerateArray()
            .Single(c => c.GetProperty("purchaseOrderId").GetInt64() == orderId);
        Assert.True(candidate.GetProperty("available").GetBoolean());
        Assert.Equal(10m, candidate.GetProperty("remainingBaseQuantity").GetDecimal());

        var (detailStatus, detailRoot, _) = await SendAsync(admin, HttpMethod.Get,
            $"/api/stock-ins/source-candidates/{orderId}");
        Assert.Equal(HttpStatusCode.OK, detailStatus);
        Assert.Equal(orderNo, detailRoot.GetProperty("data").GetProperty("orderNo").GetString());
        var line = detailRoot.GetProperty("data").GetProperty("lines")[0];
        Assert.Equal(productId, line.GetProperty("productId").GetInt64());
        Assert.Equal("PCS", line.GetProperty("baseUnit").GetString());

        // 显式选择来源 → 保存（部分数量 4）→ 审核过账（成本按来源订单单价 5、汇率 1 → 金额 20）。
        var (firstId, _) = await CreateStockInAsync(admin,
            StockInBody(supplierId, warehouseId, productId, 4m, purchaseOrderId: orderId));
        Assert.Equal(orderId, (await _fx.ReadByIdAsync(firstId)).PurchaseOrderId);
        await ApproveAsync(admin, firstId);
        Assert.Equal(4m, await _fx.ReadStockQuantityAsync(warehouseId, productId));
        var firstMovements = await _fx.ReadMovementsAsync(firstId);
        Assert.Equal(1, firstMovements.Count);
        Assert.Equal(20m, firstMovements.SumAmount);

        // 超收（剩余 6 却报 7）：受控拒绝且零库存 / 零流水，状态保持已提交。
        var (secondId, _) = await CreateStockInAsync(admin,
            StockInBody(supplierId, warehouseId, productId, 7m, purchaseOrderId: orderId));
        var (submitted, _, _) = await SendAsync(admin, HttpMethod.Post, $"/api/stock-ins/{secondId}/submit");
        Assert.Equal(HttpStatusCode.OK, submitted);
        var (_, over, _) = await SendAsync(admin, HttpMethod.Post, $"/api/stock-ins/{secondId}/approve");
        Assert.Equal(1004, over.GetProperty("code").GetInt32());           // ErrorCodes.RuleConflict
        Assert.Equal(1, (await _fx.ReadByIdAsync(secondId)).Status);      // 仍为 Submitted
        Assert.Equal(0, (await _fx.ReadMovementsAsync(secondId)).Count);
        Assert.Equal(4m, await _fx.ReadStockQuantityAsync(warehouseId, productId));

        // 取消第一张释放额度：第二张（已提交）可审核过账（既有「取消释放额度」守卫保持）。
        var (cancelled, _, _) = await SendAsync(admin, HttpMethod.Post, $"/api/stock-ins/{firstId}/cancel");
        Assert.Equal(HttpStatusCode.OK, cancelled);
        Assert.Equal(0m, await _fx.ReadStockQuantityAsync(warehouseId, productId));
        var (approved2, approved2Root, _) = await SendAsync(admin, HttpMethod.Post, $"/api/stock-ins/{secondId}/approve");
        Assert.Equal(HttpStatusCode.OK, approved2);
        Assert.Equal(0, approved2Root.GetProperty("code").GetInt32());
        Assert.Equal(7m, await _fx.ReadStockQuantityAsync(warehouseId, productId));
    }

    // ==================== 5. 零副作用拒绝（真实 HTTP + 真实 SQL 计数） ====================

    [Theory]
    [InlineData("negativeSource")]
    [InlineData("missingSupplier")]
    [InlineData("unapprovedSource")]
    public async Task 非法来源_真实HTTP_受控拒绝且零写入零占号(string scenario)
    {
        Guard();
        var tag = Tag();
        using var admin = await _fx.LoginAsAdminAsync();
        var supplierId = await CreateSupplierAsync(admin, tag);
        var warehouseId = await CreateWarehouseAsync(admin, tag);
        var productId = await CreateProductAsync(admin, tag);

        var body = StockInBody(supplierId, warehouseId, productId, 2m);
        switch (scenario)
        {
            case "negativeSource":
                body["purchaseOrderId"] = -1L;
                break;
            case "missingSupplier":
                body["supplierId"] = 999999999L;
                break;
            default:
                var (pendingOrderId, _) = await CreateOrderAsync(admin, tag, supplierId, productId, 5m, approve: false);
                body["purchaseOrderId"] = pendingOrderId;
                break;
        }

        var before = await _fx.ReadSnapshotAsync();
        var (status, root, _) = await SendAsync(admin, HttpMethod.Post, "/api/stock-ins", body);
        var after = await _fx.ReadSnapshotAsync();

        Assert.Equal(HttpStatusCode.OK, status);
        // 负数来源 = 参数错误（1001）；供应商不存在 = 未找到（1002）；未审核来源 = 规则冲突（1004）。
        Assert.Contains(root.GetProperty("code").GetInt32(), new[] { 1001, 1002, 1004 });
        Assert.Equal(before.StockInCount, after.StockInCount);
        Assert.Equal(before.DetailCount, after.DetailCount);
        Assert.Equal(before.MovementCount, after.MovementCount);
        Assert.Equal(before.Sequence, after.Sequence);              // 被拒绝的请求绝不消耗单号
    }

    [Fact]
    public async Task 无身份_真实HTTP_按未认证拒绝且零写入()
    {
        Guard();
        var tag = Tag();
        using var admin = await _fx.LoginAsAdminAsync();
        var supplierId = await CreateSupplierAsync(admin, tag);
        var warehouseId = await CreateWarehouseAsync(admin, tag);
        var productId = await CreateProductAsync(admin, tag);
        var before = await _fx.ReadSnapshotAsync();

        using var anonymous = _fx.CreateAnonymousClient();
        var (status, _, _) = await SendAsync(anonymous, HttpMethod.Post, "/api/stock-ins",
            StockInBody(supplierId, warehouseId, productId, 2m));

        Assert.Equal(HttpStatusCode.Unauthorized, status);
        Assert.Equal(before.StockInCount, (await _fx.ReadSnapshotAsync()).StockInCount);
    }

    [Fact]
    public async Task 受限账号_缺采购入库菜单_按权限不足拒绝且零写入()
    {
        Guard();
        var tag = Tag();
        using var admin = await _fx.LoginAsAdminAsync();
        var supplierId = await CreateSupplierAsync(admin, tag);
        var warehouseId = await CreateWarehouseAsync(admin, tag);
        var productId = await CreateProductAsync(admin, tag);
        var before = await _fx.ReadSnapshotAsync();

        // 经既有用户管理接口新建账号且不分配任何角色：没有既有「采购入库」菜单授权（不新增任何授权 / 菜单）。
        var userName = "http477-" + tag;
        var password = "Http477@" + tag;
        await _fx.CreateUserWithoutRolesAsync(admin, userName, password);

        using var restricted = await _fx.LoginAsync(userName, password);
        var (status, root, _) = await SendAsync(restricted, HttpMethod.Post, "/api/stock-ins",
            StockInBody(supplierId, warehouseId, productId, 2m));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(2002, root.GetProperty("code").GetInt32());     // ErrorCodes.Forbidden
        Assert.Equal(before.StockInCount, (await _fx.ReadSnapshotAsync()).StockInCount);
    }

    // ==================== 6. 与旧存储过程族分离（旧写 fail closed） ====================

    [Fact]
    public async Task 旧族分离_旧写路径fail_closed_规范入口不受影响()
    {
        Guard();
        var tag = Tag();
        using var admin = await _fx.LoginAsAdminAsync();
        var supplierId = await CreateSupplierAsync(admin, tag);
        var warehouseId = await CreateWarehouseAsync(admin, tag);
        var productId = await CreateProductAsync(admin, tag);

        // 旧写路径（ERP-404 门禁）：即使具备既有功能菜单也整体 fail closed（绝不调用 sp_Biz_StockIn）。
        var legacy = new Dictionary<string, object?>
        {
            ["Action"] = "add",
            ["StockInNo"] = "LEGACY-HTTP477-" + tag,
            ["SupplierId"] = supplierId,
            ["WarehouseId"] = warehouseId
        };
        var (legacyStatus, legacyRoot, _) = await SendAsync(admin, HttpMethod.Post,
            "/api/v2/bills/stock-in/save", legacy);
        Assert.Equal(HttpStatusCode.OK, legacyStatus);
        Assert.NotEqual(0, legacyRoot.GetProperty("code").GetInt32());

        // 规范入口（api/stock-ins，规范 Id 口径）在同一次运行中仍可正常写入：旧 Oid 写路径被停用不影响规范 Id 入口。
        var (id, number) = await CreateStockInAsync(admin, StockInBody(supplierId, warehouseId, productId, 2m));
        Assert.True(id > 0);
        Assert.StartsWith("RK", number, StringComparison.Ordinal);
        Assert.Equal(1, (await _fx.ReadByIdAsync(id)).Count);
    }

    private static async Task ApproveAsync(HttpClient client, long id)
    {
        var (submitted, submittedRoot, _) = await SendAsync(client, HttpMethod.Post, $"/api/stock-ins/{id}/submit");
        Assert.Equal(HttpStatusCode.OK, submitted);
        Assert.Equal(0, submittedRoot.GetProperty("code").GetInt32());
        var (approved, approvedRoot, _) = await SendAsync(client, HttpMethod.Post, $"/api/stock-ins/{id}/approve");
        Assert.Equal(HttpStatusCode.OK, approved);
        Assert.Equal(0, approvedRoot.GetProperty("code").GetInt32());
    }
}

/// <summary>
/// ERP-477 真实 HTTP + 真实隔离 SQL 夹具：目标必须是专用实例 <c>(localdb)\NEWERP_AutoAcceptance</c>、
/// 库名前缀 <c>NEWERP_AUTOTEST</c> 且 <c>Integrated Security=true</c>；只使用<strong>全新 GUID 后缀库</strong>，
/// 发现同名库已存在立即拒绝（绝不 drop / reset / 复用）；连接串只来自进程环境变量或专用 localdb 默认值。
/// <para>启动真实 ERP.Api 进程（真实 Kestrel / JWT 认证 / 既有授权），并为该进程注入<b>进程内临时 JWT 签名密钥</b>：
/// 随机生成、只存在于该进程的环境变量、绝不落盘、绝不复用空白签名配置；测试账号仍是既有种子账号（不新增 / 不持久化凭据）。</para>
/// </summary>
public sealed class StockInCanonicalEntrySqlServerFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";
    public const string AdminUser = "admin";
    public const string AdminPassword = "Admin@123";

    private const string DefaultDatabaseName = DatabasePrefix + "_STOCKINCANONICAL";
    private const string PortVariable = "ERP_477_HTTP_PORT";
    private const string DefaultPort = "5297";

    private readonly StringBuilder _apiConsole = new();
    private readonly object _apiConsoleLock = new();
    private Process? _apiProcess;

    public string ConnectionString { get; private set; } = string.Empty;
    public string BaseUrl { get; private set; } = string.Empty;

    /// <summary>按规范 Id 直接读取的入库单行（单号 / 状态 / 删除态 / 来源 / 合计）。</summary>
    public readonly record struct StockInRow(int Count, long Id, string? No, int Status, bool IsDeleted,
        long? PurchaseOrderId, decimal TotalQuantity);

    /// <summary>该入库单的库存流水聚合（行数 / 首行方向 / 首行数量 / 金额合计）。</summary>
    public readonly record struct MovementRow(int Count, int Direction, decimal Quantity, decimal SumAmount);

    /// <summary>零副作用拒绝所需的整库计数快照（入库单 / 明细 / 流水 / 单号序列）。</summary>
    public readonly record struct WriteSnapshot(long StockInCount, long DetailCount, long MovementCount, long Sequence);

    public async Task InitializeAsync()
    {
        var configured = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        var connectionString = string.IsNullOrWhiteSpace(configured)
            ? $"Server=(localdb)\\{InstanceMarker};Initial Catalog={DefaultDatabaseName}_{Guid.NewGuid():N};"
              + "Integrated Security=true;TrustServerCertificate=true;"
            : configured;

        // 访问任何数据库之前：实例 / 库名前缀 / 集成安全 + 显式测试上下文（不满足立即 fail-closed）。
        AssertDedicatedTarget(connectionString);
        TestDatabaseSafetyGuard.EnsureApprovedTarget(connectionString, "采购入库规范入口集成验收");
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

    /// <summary>按规范 Id 直接读取入库单（单号 / 状态 / 删除态 / 来源 / 合计）。</summary>
    public Task<StockInRow> ReadByIdAsync(long id) => ReadStockInAsync("Id = @key", id);

    /// <summary>按权威单号直接读取入库单（证明服务端生成单号确实落库）。</summary>
    public Task<StockInRow> ReadByNumberAsync(string stockInNo) => ReadStockInAsync("StockInNo = @key", stockInNo);

    private async Task<StockInRow> ReadStockInAsync(string predicate, object value)
    {
        AssertDedicatedTarget(ConnectionString);
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT TOP 1 Id, StockInNo, Status, IsDeleted, PurchaseOrderId, TotalQuantity "
                          + "FROM db_owner.StockIns WHERE " + predicate;
        cmd.Parameters.AddWithValue("@key", value);
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return new StockInRow(0, 0, null, -1, false, null, 0m);

        return new StockInRow(1, reader.GetInt64(0), reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.GetInt32(2), reader.GetBoolean(3), reader.IsDBNull(4) ? null : reader.GetInt64(4),
            reader.GetDecimal(5));
    }

    /// <summary>按规范 Id 直接读取明细行数（含软删除行，用于证明零写入）。</summary>
    public async Task<int> CountDetailsAsync(long stockInId)
    {
        AssertDedicatedTarget(ConnectionString);
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(1) FROM db_owner.StockInDetails WHERE StockInId = @id";
        cmd.Parameters.AddWithValue("@id", stockInId);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    /// <summary>该入库单的库存流水聚合（行数 / 首行方向 / 首行数量 / 金额合计；含红字冲销行）。</summary>
    public async Task<MovementRow> ReadMovementsAsync(long stockInId)
    {
        AssertDedicatedTarget(ConnectionString);
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Direction, Quantity, Amount FROM db_owner.StockMovements "
                          + "WHERE SourceDocType = 'StockIn' AND SourceDocId = @id ORDER BY Id";
        cmd.Parameters.AddWithValue("@id", stockInId);
        await using var reader = await cmd.ExecuteReaderAsync();
        var count = 0;
        var direction = 0;
        var quantity = 0m;
        var amount = 0m;
        while (await reader.ReadAsync())
        {
            if (count == 0)
            {
                direction = reader.GetInt32(0);
                quantity = reader.GetDecimal(1);
            }
            amount += reader.GetDecimal(2);
            count++;
        }
        return new MovementRow(count, direction, quantity, amount);
    }

    /// <summary>直接读取仓库 × 商品的库存数量（隔离库内没有其它写入方，可直接断言）。</summary>
    public async Task<decimal> ReadStockQuantityAsync(long warehouseId, long productId)
    {
        AssertDedicatedTarget(ConnectionString);
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT ISNULL(SUM(Quantity), 0) FROM db_owner.Stocks "
                          + "WHERE WarehouseId = @warehouseId AND ProductId = @productId AND IsDeleted = 0";
        cmd.Parameters.AddWithValue("@warehouseId", warehouseId);
        cmd.Parameters.AddWithValue("@productId", productId);
        return Convert.ToDecimal(await cmd.ExecuteScalarAsync());
    }

    /// <summary>零副作用拒绝所需的整库计数快照（入库单 / 明细 / 流水 / 采购入库单号序列）。</summary>
    public async Task<WriteSnapshot> ReadSnapshotAsync()
    {
        AssertDedicatedTarget(ConnectionString);
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        // DocumentType.StockIn = 4（与 ERP.Domain.Enums.DocumentType 同源；不新增任何映射）。
        cmd.CommandText = "SELECT (SELECT COUNT(1) FROM db_owner.StockIns), "
                          + "(SELECT COUNT(1) FROM db_owner.StockInDetails), "
                          + "(SELECT COUNT(1) FROM db_owner.StockMovements), "
                          + "ISNULL((SELECT TOP 1 CurrentSequence FROM db_owner.SysDocumentNumberRules "
                          + "WHERE DocumentType = 4 AND IsDeleted = 0), 0)";
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
            File.WriteAllText(Path.Combine(evidenceDirectory, "stock-in-canonical-entry-api-console.log"),
                ApiConsoleTail(int.MaxValue));
        }
        catch (IOException) { /* 证据写入失败不影响断言结果 */ }
    }
}

/// <summary>专用目标护栏的 fail-closed 覆盖：错误实例 / 错误库名 / 非集成安全必须在访问数据库之前被拒绝。</summary>
public sealed class StockInCanonicalEntryTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;User Id=sa;Password=secret")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(
            () => StockInCanonicalEntrySqlServerFixture.AssertDedicatedTarget(connection));
}
