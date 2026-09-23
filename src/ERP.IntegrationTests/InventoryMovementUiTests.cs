using OpenQA.Selenium;
using OpenQA.Selenium.Support.UI;
using System.Text.Json;
using Xunit;

namespace ERP.IntegrationTests;

/// <summary>
/// ERP-009 真实浏览器验收（Collection=UiTests，Microsoft Edge）：
/// 1) 库存盘点调整：页面新建盘点单（账面 0 → 实盘 50，成本单价 10）→ 提交 → 审核，
///    核对库存数量 / 金额 / 加权平均成本与库存流水（含移动后结存快照）；
/// 2) 仓库调拨：页面新建调拨单（调出仓 A → 调入仓 B，数量 30）→ 审核，
///    核对调出仓减少、调入仓增加、两仓数量与金额合计守恒，流水出现「调拨出库 + 调拨入库」两笔；
/// 3) 销审：已审核单据在页面销审（确认框）→ 库存与流水冲销（追加红字流水）、
///    重复销审被服务端拒绝且不产生重复效果，随后可重新提交审核。
/// 说明：测试数据（仓库 / 商品 / 单据）全部经应用自身接口与页面以登录态创建，用后软删除；
///       不直连数据库、不使用生产数据；截图证据由 UiTestFixture.CaptureEvidence 输出。
/// </summary>
[Collection("UiTests")]
[Trait("Collection", "UiTests")]
public class InventoryMovementUiTests
{
    private const string AdminUser = "admin";
    private const string AdminPassword = "Admin@123";
    private readonly UiTestFixture _fx;

    public InventoryMovementUiTests(UiTestFixture fx) => _fx = fx;

    // ==================== 场景 1：库存盘点调整 → 审核 → 库存与流水 ====================

    [Fact]
    public void 库存盘点调整_页面新建并审核_库存与流水一致()
    {
        var tag = "UIPD" + DateTime.Now.ToString("HHmmss");
        LoginAsAdmin();
        InstallApiIssueRecorder();

        var warehouse = CreateWarehouse(tag + "A", tag);
        var product = CreateProduct(tag);

        OpenModule("stock-adjustment");
        Assert.Contains("盘点单号", TableHeaders());
        Assert.Contains("差异数量", TableHeaders());
        CaptureEvidence("stock-adjustment-list");

        ClickNewButton();
        WaitPanelFieldVisible("f_adjustmentDate");
        SetRefField("warehouseId", warehouse.Id, warehouse.Name);
        SetSelectValue("f_adjustType", "盘点调整");
        SetFieldValue("f_remark", tag);
        AddAdjustmentDetail(0, product.Id, tag + "-商品", book: "0", actual: "50", unitCost: "10");

        // 前端差异计算：差异 = 实盘 - 账面，差异金额 = 差异 × 成本单价
        Assert.Equal(50m, decimal.Parse(FormValue("d_0_diffQuantity")));
        Assert.Equal(500m, decimal.Parse(FormValue("d_0_diffAmount")));

        ClearToast();
        SavePanel();
        WaitToastContains("保存成功");
        CaptureEvidence("stock-adjustment-saved");
        ClosePanel();

        var doc = FindAdjustment(tag);
        Assert.StartsWith("PD", doc.No);
        Assert.Equal("Pending", doc.Status);
        Assert.Equal(1, doc.DetailCount);

        // 提交 → 审核（列表行「更多」菜单）
        ApproveViaList(doc.Id, submitFirst: true, expectToast: "盘点单已审核");
        Assert.Equal("Approved", GetAdjustment(doc.Id).Status);
        CaptureEvidence("stock-adjustment-approved");

        // 服务端事实：库存数量 50、库存金额 500、加权平均成本 10
        var stock = StockOf(warehouse.Id, product.Id);
        Assert.Equal(50m, stock.Quantity);
        Assert.Equal(500m, stock.TotalCost);
        Assert.Equal(10m, stock.AverageCost);

        // 库存流水：一笔入库 50、成本 10、移动后结存 50 / 500
        var movements = MovementsOf($"/api/inventory/stock-adjustments/{doc.Id}/movements");
        Assert.Single(movements);
        Assert.True(movements[0].GetProperty("direction").GetInt32() == 1);
        Assert.Equal(50m, movements[0].GetProperty("quantity").GetDecimal());
        Assert.Equal(10m, movements[0].GetProperty("unitCost").GetDecimal());
        Assert.Equal(500m, movements[0].GetProperty("amount").GetDecimal());
        Assert.Equal(50m, movements[0].GetProperty("balanceQuantity").GetDecimal());
        Assert.Equal(500m, movements[0].GetProperty("balanceAmount").GetDecimal());

        // 库存流水页面：按单据号可查到该笔变动
        OpenModule("stock-movement");
        SearchList(doc.No);
        var history = TableText();
        Assert.Contains(doc.No, history);
        Assert.Contains("盘点调整", history);
        Assert.Contains("入库", history);
        CaptureEvidence("stock-movement-history-adjustment");

        // 清理测试数据（销审 + 软删除），保证验收库不被测试单据长期占用
        CleanupAdjustment(doc.Id);
        AssertEmptyApiIssues();
    }

    // ==================== 场景 2：仓库调拨 → 审核 → 两仓守恒 ====================

    [Fact]
    public void 仓库调拨_页面新建并审核_两仓数量与金额守恒()
    {
        var tag = "UIDB" + DateTime.Now.ToString("HHmmss");
        LoginAsAdmin();
        InstallApiIssueRecorder();

        var source = CreateWarehouse(tag + "A", tag);
        var target = CreateWarehouse(tag + "B", tag);
        var product = CreateProduct(tag);
        // 预置调出仓库存：用盘点调整（账面 0 → 实盘 100，成本单价 10）经接口直接落账
        var seedAdjustmentId = SeedStockViaAdjustment(source.Id, product.Id, tag + "-商品", 100m, 10m);
        Assert.Equal(100m, StockOf(source.Id, product.Id).Quantity);

        OpenModule("stock-transfer");
        Assert.Contains("调出仓", TableHeaders());
        Assert.Contains("调入仓", TableHeaders());
        CaptureEvidence("stock-transfer-list");

        ClickNewButton();
        WaitPanelFieldVisible("f_transferDate");
        SetRefField("fromWarehouseId", source.Id, source.Name);
        SetRefField("toWarehouseId", target.Id, target.Name);
        SetFieldValue("f_remark", tag);
        // 成本单价留 0：后端按调出仓当前加权平均成本计价（10）
        AddTransferDetail(0, product.Id, tag + "-商品", quantity: "30", unitCost: "0");

        ClearToast();
        SavePanel();
        WaitToastContains("保存成功");
        ClosePanel();

        var doc = FindTransfer(tag);
        Assert.StartsWith("DB", doc.No);
        Assert.Equal("Pending", doc.Status);

        ApproveViaList(doc.Id, submitFirst: true, expectToast: "调拨单已审核");
        Assert.Equal("Approved", GetTransfer(doc.Id).Status);
        CaptureEvidence("stock-transfer-approved");

        // 两仓守恒：调出 70 + 调入 30 = 100；金额 700 + 300 = 1000
        var fromStock = StockOf(source.Id, product.Id);
        var toStock = StockOf(target.Id, product.Id);
        Assert.Equal(70m, fromStock.Quantity);
        Assert.Equal(30m, toStock.Quantity);
        Assert.Equal(100m, fromStock.Quantity + toStock.Quantity);
        Assert.Equal(700m, fromStock.TotalCost);
        Assert.Equal(300m, toStock.TotalCost);
        Assert.Equal(1000m, fromStock.TotalCost + toStock.TotalCost);

        var detail = ApiData("GET", $"/api/inventory/stock-transfers/{doc.Id}");
        Assert.Equal(30m, detail.GetProperty("totalQuantity").GetDecimal());
        Assert.Equal(300m, detail.GetProperty("totalAmount").GetDecimal());
        Assert.Equal(10m, detail.GetProperty("details")[0].GetProperty("unitCost").GetDecimal());

        // 流水：调拨出库 -30 / 调拨入库 +30，两侧成本单价一致
        var movements = MovementsOf($"/api/inventory/stock-transfers/{doc.Id}/movements");
        Assert.Equal(2, movements.Count);
        Assert.Equal(-1, movements[0].GetProperty("direction").GetInt32());
        Assert.Equal(1, movements[1].GetProperty("direction").GetInt32());
        Assert.Equal(movements[0].GetProperty("unitCost").GetDecimal(), movements[1].GetProperty("unitCost").GetDecimal());
        Assert.Equal(-300m, movements[0].GetProperty("amount").GetDecimal());
        Assert.Equal(300m, movements[1].GetProperty("amount").GetDecimal());

        OpenModule("stock-movement");
        SearchList(doc.No);
        var history = TableText();
        Assert.Contains("调拨出库", history);
        Assert.Contains("调拨入库", history);
        CaptureEvidence("stock-movement-history-transfer");

        // 清理测试数据（销审 + 软删除）：先销审调拨单，再销审预置库存的盘点单，最后软删除
        OpenModule("stock-transfer");
        SearchList(doc.No);
        UnauditViaPage(doc.Id);
        var seedNo = GetAdjustment(seedAdjustmentId).No;
        OpenModule("stock-adjustment");
        SearchList(seedNo);
        UnauditViaPage(seedAdjustmentId);
        Assert.Equal(0m, StockOf(source.Id, product.Id).Quantity);
        DeleteDoc($"/api/inventory/stock-transfers/{doc.Id}");
        DeleteDoc($"/api/inventory/stock-adjustments/{seedAdjustmentId}");
        AssertEmptyApiIssues();
    }


    // ==================== 场景 3：销审冲销与重复销审保护 ====================

    [Fact]
    public void 销审_页面销审_库存与流水冲销且重复销审被拒绝()
    {
        var tag = "UIOUT" + DateTime.Now.ToString("HHmmss");
        LoginAsAdmin();
        InstallApiIssueRecorder();

        var warehouse = CreateWarehouse(tag + "A", tag);
        var product = CreateProduct(tag);
        var docId = SeedStockViaAdjustment(warehouse.Id, product.Id, tag + "-商品", 20m, 5m);
        Assert.Equal(20m, StockOf(warehouse.Id, product.Id).Quantity);
        var docNo = GetAdjustment(docId).No;

        // 页面销审（行操作「销审」+ 确认框）
        OpenModule("stock-adjustment");
        SearchList(docNo);
        Assert.Contains("已审核", TableText());
        CaptureEvidence("stock-adjustment-approved-before-unaudit");
        UnauditViaPage(docId);

        // 库存还原 + 红字流水（原流水标记已冲销，冲销流水方向相反、金额取负）
        var stock = StockOf(warehouse.Id, product.Id);
        Assert.Equal(0m, stock.Quantity);
        Assert.Equal(0m, stock.TotalCost);
        var movements = MovementsOf($"/api/inventory/stock-adjustments/{docId}/movements");
        Assert.Equal(2, movements.Count);
        Assert.True(movements[0].GetProperty("isReversed").GetBoolean());
        Assert.True(movements[1].GetProperty("isReversal").GetBoolean());
        Assert.Equal(-1, movements[1].GetProperty("direction").GetInt32());
        Assert.Equal(-100m, movements[1].GetProperty("amount").GetDecimal());
        CaptureEvidence("stock-adjustment-unaudited-reversed");

        // 重复销审：服务端拒绝（单据已是待提交），且不再产生任何冲销流水
        var repeat = Api("POST", $"/api/inventory/stock-adjustments/{docId}/unaudit");
        using (var repeatDoc = JsonDocument.Parse(repeat))
        {
            Assert.NotEqual(0, repeatDoc.RootElement.GetProperty("code").GetInt32());
            Assert.Contains("仅已审核", repeatDoc.RootElement.GetProperty("message").GetString() ?? string.Empty);
        }
        Assert.Equal(2, MovementsOf($"/api/inventory/stock-adjustments/{docId}/movements").Count);
        Assert.Equal(0m, StockOf(warehouse.Id, product.Id).Quantity);

        // 冲销后可再次提交审核（单据仍可使用，库存/流水继续按新流水记账）
        ApproveViaList(docId, submitFirst: true, expectToast: "盘点单已审核");
        Assert.Equal(20m, StockOf(warehouse.Id, product.Id).Quantity);
        Assert.Equal(3, MovementsOf($"/api/inventory/stock-adjustments/{docId}/movements").Count);

        // 该用例有意触发 1 次业务拒绝（重复销审），其余请求不允许失败
        var issues = ApiIssues();
        Assert.Single(issues);
        Assert.Contains("仅已审核", issues[0]);

        // 清理测试数据（销审 + 软删除）：库存回到 0，单据不再出现在列表中
        CleanupAdjustment(docId);
        Assert.Equal(0m, StockOf(warehouse.Id, product.Id).Quantity);
    }


    // ==================== 登录 / 接口失败记录 ====================

    private void LoginAsAdmin()
    {
        _fx.ResetBrowserSession();
        _fx.Driver.FindElement(By.Id("login-username")).Clear();
        _fx.Driver.FindElement(By.Id("login-username")).SendKeys(AdminUser);
        _fx.Driver.FindElement(By.Id("login-password")).Clear();
        _fx.Driver.FindElement(By.Id("login-password")).SendKeys(AdminPassword);
        _fx.Driver.FindElement(By.Id("login-btn")).Click();
        Wait(20).Until(d => d.FindElement(By.Id("app-page")).Displayed);
    }

    /// <summary>包装 window.fetch：记录 HTTP 非 2xx 或业务码 != 0 的请求，用于「无失败请求」验收</summary>
    private void InstallApiIssueRecorder() => ExecuteScript(
        "window.__apiIssues = [];" +
        "const __origFetch = window.fetch;" +
        "window.fetch = async (...args) => {" +
        "  const res = await __origFetch(...args);" +
        "  try {" +
        "    const text = await res.clone().text();" +
        "    try {" +
        "      const j = JSON.parse(text);" +
        "      if (!res.ok || (j && typeof j.code === 'number' && j.code !== 0))" +
        "        window.__apiIssues.push(String(args[0] || '') + ' => ' + res.status + ' ' + (j.message || ''));" +
        "    } catch (e) { if (!res.ok) window.__apiIssues.push(String(args[0] || '') + ' => ' + res.status); }" +
        "  } catch (e) { /* 忽略读取失败 */ }" +
        "  return res;" +
        "}; return 'ok';");

    private string[] ApiIssues()
    {
        var json = ExecuteScript("return JSON.stringify(window.__apiIssues || []);");
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.EnumerateArray().Select(e => e.GetString() ?? string.Empty).ToArray();
    }

    private void AssertEmptyApiIssues()
    {
        var issues = ApiIssues();
        Assert.True(issues.Length == 0, "验收过程中不应出现失败请求：" + string.Join(" | ", issues));
    }

    private string ExecuteScript(string script)
        => ((IJavaScriptExecutor)_fx.Driver).ExecuteScript(script)?.ToString() ?? string.Empty;

    private string Api(string method, string path, string? bodyJson = null)
    {
        _fx.Driver.Manage().Timeouts().AsynchronousJavaScript = TimeSpan.FromSeconds(30);
        var script = $$"""
            const done = arguments[arguments.length - 1];
            (async () => {
              try {
                const res = await fetch({{JsonSerializer.Serialize(path)}}, {
                  method: {{JsonSerializer.Serialize(method)}},
                  headers: { 'Content-Type': 'application/json', 'Authorization': 'Bearer ' + (localStorage.getItem('erp_token') || '') },
                  body: {{JsonSerializer.Serialize(bodyJson)}}
                });
                done(await res.text());
              } catch (e) { done(JSON.stringify({ code: -1, message: String(e) })); }
            })();
            """;
        return (string)((IJavaScriptExecutor)_fx.Driver).ExecuteAsyncScript(script) ?? string.Empty;
    }

    /// <summary>调用接口并断言业务码为 0，返回 data 节点</summary>
    private JsonElement ApiData(string method, string path, string? bodyJson = null)
    {
        using var doc = JsonDocument.Parse(Api(method, path, bodyJson));
        var root = doc.RootElement;
        Assert.Equal(0, root.GetProperty("code").GetInt32());
        return root.GetProperty("data").Clone();
    }

    private void DeleteDoc(string path)
    {
        using var doc = JsonDocument.Parse(Api("DELETE", path));
        // 仅待提交状态的单据可删（清理阶段单据均已销审，软删除成功；失败时给出明确提示）
        Assert.Equal(0, doc.RootElement.GetProperty("code").GetInt32());
    }

    // ==================== 测试数据（经接口创建，每次运行唯一标记） ====================

    private sealed record WarehouseSeed(long Id, string Name);
    private sealed record ProductSeed(long Id, string Name);
    private sealed record DocSeed(long Id, string No, string Status, int DetailCount);

    private WarehouseSeed CreateWarehouse(string code, string name)
    {
        var body = JsonSerializer.Serialize(new { warehouseCode = "WH-" + code, warehouseName = name + "仓" });
        var data = ApiData("POST", "/api/base/warehouses", body);
        return new WarehouseSeed(data.GetProperty("id").GetInt64(), data.GetProperty("warehouseName").GetString()!);
    }

    private ProductSeed CreateProduct(string tag)
    {
        var body = JsonSerializer.Serialize(new
        {
            productCode = "P-" + tag,
            productName = tag + "-商品",
            spec = "大",
            unit = "PCS"
        });
        var data = ApiData("POST", "/api/base/products", body);
        return new ProductSeed(data.GetProperty("id").GetInt64(), data.GetProperty("productName").GetString()!);
    }


    /// <summary>用「盘点调整」接口把库存从 0 预置到指定数量（验收期间的真实业务链路，不直连数据库）</summary>
    private long SeedStockViaAdjustment(long warehouseId, long productId, string productName, decimal quantity, decimal unitCost)
    {
        var create = JsonSerializer.Serialize(new
        {
            adjustmentDate = DateTime.Today.ToString("yyyy-MM-dd"),
            warehouseId,
            adjustType = "盘点调整",
            remark = "INV_UI_SEED",
            details = new object[]
            {
                new { productId, productName, spec = "大", unit = "PCS", bookQuantity = 0m, actualQuantity = quantity, unitCost }
            }
        });
        var data = ApiData("POST", "/api/inventory/stock-adjustments", create);
        var id = data.GetProperty("id").GetInt64();
        ApiData("POST", $"/api/inventory/stock-adjustments/{id}/submit");
        ApiData("POST", $"/api/inventory/stock-adjustments/{id}/approve");
        return id;
    }

    private DocSeed FindAdjustment(string keyword)
    {
        var row = FirstItem($"/api/inventory/stock-adjustments?page=1&pageSize=10&keyword={Uri.EscapeDataString(keyword)}",
            $"未找到关键字为 {keyword} 的盘点单");
        var id = row.GetProperty("id").GetInt64();
        return new DocSeed(id, Str(row, "adjustmentNo"), Str(row, "status"), DetailCount(ApiData("GET", $"/api/inventory/stock-adjustments/{id}")));
    }

    private DocSeed FindTransfer(string keyword)
    {
        var row = FirstItem($"/api/inventory/stock-transfers?page=1&pageSize=10&keyword={Uri.EscapeDataString(keyword)}",
            $"未找到关键字为 {keyword} 的调拨单");
        var id = row.GetProperty("id").GetInt64();
        return new DocSeed(id, Str(row, "transferNo"), Str(row, "status"), DetailCount(ApiData("GET", $"/api/inventory/stock-transfers/{id}")));
    }

    private DocSeed GetAdjustment(long id)
    {
        var data = ApiData("GET", $"/api/inventory/stock-adjustments/{id}");
        return new DocSeed(id, Str(data, "adjustmentNo"), Str(data, "status"), DetailCount(data));
    }

    private DocSeed GetTransfer(long id)
    {
        var data = ApiData("GET", $"/api/inventory/stock-transfers/{id}");
        return new DocSeed(id, Str(data, "transferNo"), Str(data, "status"), DetailCount(data));
    }

    private JsonElement FirstItem(string listPath, string message)
    {
        var data = ApiData("GET", listPath);
        var items = data.GetProperty("items");
        Assert.True(items.GetArrayLength() > 0, message);
        return items[0].Clone();
    }

    private static string Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;

    private static int DetailCount(JsonElement e)
        => e.TryGetProperty("details", out var v) && v.ValueKind == JsonValueKind.Array ? v.GetArrayLength() : 0;

    /// <summary>仓库 + 商品的库存快照（取不到时返回零值，便于断言「库存不存在 = 0」）</summary>
    private (decimal Quantity, decimal TotalCost, decimal AverageCost) StockOf(long warehouseId, long productId)
    {
        var data = ApiData("GET", $"/api/stocks?page=1&pageSize=100&warehouseId={warehouseId}");
        foreach (var item in data.GetProperty("items").EnumerateArray())
        {
            if (item.GetProperty("productId").GetInt64() != productId)
            {
                // 该仓库存在其它商品的库存行（同一仓库被复用），继续找目标商品
                continue;
            }
            return (item.GetProperty("quantity").GetDecimal(),
                item.GetProperty("totalCost").GetDecimal(),
                item.GetProperty("averageCost").GetDecimal());
        }
        return (0m, 0m, 0m);
    }

    private List<JsonElement> MovementsOf(string path)
    {
        var data = ApiData("GET", path);
        Assert.Equal(JsonValueKind.Array, data.ValueKind);
        return data.EnumerateArray().Select(e => e.Clone()).ToList();
    }

    /// <summary>清理盘点单：先销审（若已审核）再软删除</summary>
    private void CleanupAdjustment(long id)
    {
        var status = GetAdjustment(id).Status;
        if (status == "Approved")
        {
            using var unaudit = JsonDocument.Parse(Api("POST", $"/api/inventory/stock-adjustments/{id}/unaudit"));
            Assert.Equal(0, unaudit.RootElement.GetProperty("code").GetInt32());
        }
        DeleteDoc($"/api/inventory/stock-adjustments/{id}");
    }


    // ==================== UI 交互助手（真实浏览器操作） ====================

    private WebDriverWait Wait(int seconds = 20) => new(_fx.Driver, TimeSpan.FromSeconds(seconds));

    private void CaptureEvidence(string scenario) => _fx.CaptureEvidence(scenario);

    /// <summary>点击侧边栏菜单（自动展开所属一级分组），等待模块页面渲染完成</summary>
    private void OpenModule(string menuCode)
    {
        var child = Wait().Until(d =>
        {
            var items = d.FindElements(By.CssSelector($"#sidebar-nav .nav-item.nav-child[data-code='{menuCode}']"));
            return items.Count > 0 ? items[0] : null;
        });
        Assert.NotNull(child);
        if (!child!.Displayed)
        {
            var parent = child.FindElement(By.XPath("ancestor::div[contains(@class,'nav-group')][1]/div[contains(@class,'nav-parent')]"));
            SafeClick(parent);
        }
        Wait().Until(_ => child.Displayed);
        SafeClick(child);
        Wait().Until(d => d.FindElements(By.Id("search-input")).Count > 0);
        Wait().Until(_ => ExecuteScript("return String(typeof CURRENT_MODULE_CODE !== 'undefined' ? CURRENT_MODULE_CODE : '');") == menuCode);
    }

    private void ClickNewButton()
    {
        var button = Wait().Until(d =>
            d.FindElements(By.CssSelector("#content .toolbar-actions button"))
                .FirstOrDefault(b => (b.GetDomAttribute("onclick") ?? string.Empty).StartsWith("openForm(", StringComparison.Ordinal)));
        Assert.NotNull(button);
        SafeClick(button!);
        Wait().Until(d => d.FindElement(By.Id("side-panel")).GetDomAttribute("class")!.Contains("active"));
    }

    private void WaitPanelFieldVisible(string elementId)
        => Wait().Until(d => d.FindElements(By.Id(elementId)).Count > 0);

    private void SetFieldValue(string elementId, string value) => ExecuteScript(
        $"const el = document.getElementById({JsonSerializer.Serialize(elementId)}); el.focus(); " +
        $"el.value = {JsonSerializer.Serialize(value)}; el.dispatchEvent(new Event('change', {{ bubbles: true }})); return 'ok';");

    private string FormValue(string elementId) => ExecuteScript(
        $"const el = document.getElementById({JsonSerializer.Serialize(elementId)}); return el ? String(el.value ?? '') : '';");

    /// <summary>下拉选择：仅在选项存在时设置（避免写出非法值）</summary>
    private void SetSelectValue(string elementId, string value)
    {
        var exists = ExecuteScript(
            $"const el = document.getElementById({JsonSerializer.Serialize(elementId)});" +
            $"return String(!!el && Array.from(el.options).some(o => o.value === {JsonSerializer.Serialize(value)}));");
        Assert.Equal("True", exists);
        SetFieldValue(elementId, value);
    }

    /// <summary>引用字段（仓库 / 商品）：直接给隐藏主键赋值，并回填显示文本</summary>
    private void SetRefField(string key, long id, string displayText) => ExecuteScript(
        $"const hid = document.getElementById('f_{key}'); if (hid) hid.value = '{id}'; " +
        $"const s = document.getElementById('f_{key}_search'); if (s) s.value = {JsonSerializer.Serialize(displayText)}; return 'ok';");


    /// <summary>盘点明细：商品 + 账面数量 + 实盘数量 + 成本单价（差异与差异金额由页面计算）</summary>
    private void AddAdjustmentDetail(int index, long productId, string productName, string book, string actual, string unitCost)
    {
        AddDetailRowCore(index);
        SetDetailCell(index, "productId", productId.ToString());
        SetDetailCell(index, "productName", productName);
        SetDetailCell(index, "spec", "大");
        SetDetailCell(index, "unit", "PCS");
        SetDetailCell(index, "bookQuantity", book);
        SetDetailCell(index, "actualQuantity", actual);
        SetDetailCell(index, "unitCost", unitCost);
    }

    /// <summary>调拨明细：商品 + 调拨数量 + 成本单价（留 0 时后端取调出仓加权平均成本）</summary>
    private void AddTransferDetail(int index, long productId, string productName, string quantity, string unitCost)
    {
        AddDetailRowCore(index);
        SetDetailCell(index, "productId", productId.ToString());
        SetDetailCell(index, "productName", productName);
        SetDetailCell(index, "spec", "大");
        SetDetailCell(index, "unit", "PCS");
        SetDetailCell(index, "quantity", quantity);
        SetDetailCell(index, "unitCost", unitCost);
    }

    private void AddDetailRowCore(int index)
    {
        var addButton = Wait().Until(d =>
            d.FindElements(By.XPath("//button[starts-with(@onclick,'detailAddRow()')]")).FirstOrDefault());
        Assert.NotNull(addButton);
        SafeClick(addButton!);
        Wait().Until(d => d.FindElements(By.Id($"d_{index}_productName")).Count > 0);
    }

    /// <summary>明细单元格：同步 DOM 值与数据模型（金额 / 差异由 detailSet 触发重算）</summary>
    private void SetDetailCell(int index, string key, string value) => ExecuteScript(
        $"const el = document.getElementById('d_{index}_{key}'); if (el) el.value = {JsonSerializer.Serialize(value)}; " +
        $"detailSet({index}, '{key}', {JsonSerializer.Serialize(value)}); return 'ok';");

    private void SavePanel()
    {
        var saveBtn = Wait().Until(d => d.FindElements(By.Id("sp-save-btn")).FirstOrDefault());
        Assert.NotNull(saveBtn);
        SafeClick(saveBtn!);
    }

    private void ClosePanel()
    {
        var close = _fx.Driver.FindElements(By.CssSelector("#side-panel .side-panel-close")).FirstOrDefault();
        if (close != null) SafeClick(close);
        try { Wait(5).Until(d => !d.FindElement(By.Id("side-panel")).GetDomAttribute("class")!.Contains("active")); }
        catch (WebDriverTimeoutException) { ExecuteScript("closePanel(); return 'ok';"); }
    }

    private void SafeClick(IWebElement element)
    {
        try { element.Click(); }
        catch (WebDriverException) { ((IJavaScriptExecutor)_fx.Driver).ExecuteScript("arguments[0].click();", element); }
    }

    private void ClearToast() => ExecuteScript(
        "const t = document.getElementById('toast'); t.textContent = ''; t.style.display = 'none'; t.className = 'toast'; return 'ok';");

    private void WaitToastContains(string text) => Wait().Until(d =>
    {
        var toast = d.FindElement(By.Id("toast"));
        return toast.Text.Contains(text) ? toast : null;
    });

    /// <summary>列表页按关键字搜索，等待出现包含该关键字的行</summary>
    private void SearchList(string keyword)
    {
        var input = Wait().Until(d => d.FindElements(By.Id("search-input")).FirstOrDefault());
        Assert.NotNull(input);
        input!.Clear();
        input.SendKeys(keyword);
        input.SendKeys(Keys.Enter);
        Wait().Until(d =>
        {
            try { return d.FindElements(By.CssSelector("#table-wrap tbody tr")).Any(tr => tr.Text.Contains(keyword)); }
            catch (StaleElementReferenceException) { return false; }
        });
    }

    private string[] TableHeaders() => Wait().Until(d =>
    {
        try
        {
            var ths = d.FindElements(By.CssSelector("#table-wrap thead th")).Select(t => t.Text).ToArray();
            return ths.Length > 0 ? ths : null;
        }
        catch (StaleElementReferenceException) { return null; }
    }) ?? Array.Empty<string>();

    private string TableText() => ExecuteScript("const w = document.getElementById('table-wrap'); return w ? w.innerText : '';");


    /// <summary>
    /// 通过列表行「更多」菜单执行业务动作：先定位目标行（按该行的编辑按钮），
    /// 展开菜单后点击 onclick 完全匹配的菜单项；列表异步重绘导致的 stale 异常在轮询内吞掉重试。
    /// </summary>
    private void ClickRowMenuAction(long id, string expectedOnclick)
    {
        var itemXPath = $"//button[@onclick='{expectedOnclick}']";
        var rowXPath = $"//tr[.//button[starts-with(@onclick,'openForm({id})')]]";
        var deadline = DateTime.UtcNow.AddSeconds(20);
        var lastError = "尚未尝试";
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var item = _fx.Driver.FindElements(By.XPath(itemXPath)).FirstOrDefault(e => e.Displayed);
                if (item == null)
                {
                    var row = _fx.Driver.FindElements(By.XPath(rowXPath)).FirstOrDefault();
                    if (row == null) { Thread.Sleep(200); continue; }
                    var more = row.FindElements(By.CssSelector("button.row-more")).FirstOrDefault();
                    if (more == null) { Thread.Sleep(200); continue; }
                    SafeClick(more);
                    Thread.Sleep(150);
                    item = _fx.Driver.FindElements(By.XPath(itemXPath)).FirstOrDefault(e => e.Displayed);
                }
                if (item != null) { SafeClick(item); return; }
            }
            catch (StaleElementReferenceException) { /* 列表重绘：重新定位 */ }
            catch (WebDriverException ex) { lastError = ex.Message; }
            Thread.Sleep(200);
        }
        throw new WebDriverTimeoutException($"未能在 20 秒内点击行操作 {expectedOnclick}；最后错误：{lastError}");
    }

    /// <summary>提交 → 审核：走列表行「更多」菜单的真实业务动作</summary>
    private void ApproveViaList(long id, bool submitFirst, string expectToast)
    {
        if (submitFirst)
        {
            ClearToast();
            ClickRowMenuAction(id, $"changeStatus({id},'submit')");
            WaitToastContains("提交成功");
        }
        ClearToast();
        ClickRowMenuAction(id, $"changeStatus({id},'approve')");
        WaitToastContains(expectToast);
    }

    /// <summary>页面销审：行「更多」菜单的「销审」+ 业务确认框</summary>
    private void UnauditViaPage(long id)
    {
        ClearToast();
        ClickRowMenuAction(id, $"unauditRow({id})");
        AcceptAlert();
        WaitToastContains("已销审");
    }

    /// <summary>轮询等待业务确认框（window.confirm）并确认</summary>
    private void AcceptAlert(int seconds = 10)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            try { _fx.Driver.SwitchTo().Alert().Accept(); return; }
            catch (NoAlertPresentException) { Thread.Sleep(200); }
        }
        throw new WebDriverTimeoutException("业务确认框未出现");
    }
}

