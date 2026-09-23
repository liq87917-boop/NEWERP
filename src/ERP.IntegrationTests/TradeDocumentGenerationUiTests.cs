using OpenQA.Selenium;
using OpenQA.Selenium.Support.UI;
using System.Globalization;
using System.Text.Json;
using Xunit;

namespace ERP.IntegrationTests;

/// <summary>
/// ERP-019 真实浏览器验收（Collection=UiTests，Microsoft Edge）：
/// 1) 销售订单页「生成单证」→ 生成商业发票 + 装箱单，核对映射字段与来源订单留痕，并在单证中心可见；
/// 2) 装柜清单页「生成单证」→ 生成装箱单，核对柜号 / 港口 / 箱数毛重体积与来源清单留痕；
/// 3) 重复生成被服务端守卫拒绝（提示已生成），单证中心不出现重复记录（已生成类型在对话框置灰）；
/// 4) 单证中心 Excel 导出：对话框与导出接口可用，导出的 xlsx 可被解析（zip 文件头）。
/// 说明：测试数据全部经应用自身接口以登录态创建（不直连数据库、不使用生产数据、不执行 SQL）；
///       截图证据通过 UiTestFixture.CaptureEvidence 输出到 ERP_AI_EVIDENCE_DIR。
/// </summary>
[Collection("UiTests")]
[Trait("Collection", "UiTests")]
public class TradeDocumentGenerationUiTests
{
    private const string AdminUser = "admin";
    private const string AdminPassword = "Admin@123";
    private readonly UiTestFixture _fx;

    public TradeDocumentGenerationUiTests(UiTestFixture fx) => _fx = fx;

    // ==================== 场景 1：销售订单 → 单证中心 ====================

    [Fact]
    public void 销售订单生成单证_页面操作后单证中心可见且映射与来源留痕正确()
    {
        var tag = "UITD" + DateTime.Now.ToString("HHmmss");
        LoginAsAdmin();
        InstallApiIssueRecorder();

        var customer = CreateCustomer(tag);
        var order = CreateSalesOrder(tag, customer.Id);

        OpenModule("sales-order");
        SearchList(order.No);
        Assert.True(FunctionExists("generateTradeDocsFromSource"),
            "页面未加载 /js/trade-doc-gen.js（前端脚本过期）：请先重新构建并启动 ERP.Api 再运行浏览器验收");
        CaptureEvidence("sales-order-list-ready");

        // 行操作「生成单证」→ 对话框默认勾选商业发票 + 装箱单 → 生成（一次业务确认）
        ClickRowMenuAction(order.Id, "generateTradeDocsFromSource");
        Assert.True(Wait(20).Until(d => d.FindElements(By.CssSelector("#modal .td-kind")).Count > 0),
            "生成单证对话框未渲染单证类型列表");
        CaptureEvidence("trade-document-generate-dialog");
        ClickModalAction("generateSelectedTradeDocs()");
        AcceptAlert();
        WaitToastContains("已生成");
        CaptureEvidence("trade-documents-generated");

        // 服务端事实：两张单证、映射字段与来源留痕
        var docs = TradeDocsBySalesOrder(order.No);
        Assert.Equal(2, docs.Count);
        var ci = docs.Single(d => d.DocType == "商业发票");
        Assert.Equal($"CI-{order.No}", ci.DocNo);
        Assert.Equal(tag, ci.CustomerName);
        Assert.Equal(250m, ci.Amount);
        Assert.Equal("USD", ci.Currency);
        Assert.Equal("HAMBURG", ci.DestinationPort);
        Assert.Equal("待制作", ci.Status);
        Assert.Contains($"来源：销售订单 {order.No}", ci.Remark);
        var pl = docs.Single(d => d.DocType == "装箱单");
        Assert.Equal($"PL-{order.No}", pl.DocNo);
        Assert.Equal(250m, pl.Amount);

        // 页面事实：单证中心列表可见新生成的单证（生成后自动跳到单证中心）
        Assert.Contains("单证中心", _fx.Driver.FindElement(By.Id("header-title")).Text);
        SearchList(ci.DocNo);
        Assert.Contains(ci.DocNo, RowText(ci.DocNo));
        CaptureEvidence("doc-center-with-generated-documents");

        AssertEmptyApiIssues();
    }

    // ==================== 场景 2：装柜清单 → 单证中心 ====================

    [Fact]
    public void 装柜清单生成单证_页面操作后核对柜号港口与装柜数据()
    {
        var tag = "UITL" + DateTime.Now.ToString("HHmmss");
        LoginAsAdmin();
        InstallApiIssueRecorder();

        var customer = CreateCustomer(tag);
        var loadingList = CreateLoadingList(tag, customer.Id, containerNo: "CTN" + tag);

        OpenModule("loading-list");
        SearchList(loadingList.No);
        Assert.True(FunctionExists("generateTradeDocsFromSource"),
            "页面未加载 /js/trade-doc-gen.js（前端脚本过期）：请先重新构建并启动 ERP.Api 再运行浏览器验收");
        CaptureEvidence("loading-list-ready");

        ClickRowMenuAction(loadingList.Id, "generateTradeDocsFromSource");
        Assert.True(Wait(20).Until(d => d.FindElements(By.CssSelector("#modal .td-kind")).Count > 0),
            "生成单证对话框未渲染单证类型列表");
        // 默认只勾选装箱单；核对后直接生成
        ClickModalAction("generateSelectedTradeDocs()");
        AcceptAlert();
        WaitToastContains("已生成");
        CaptureEvidence("loading-list-trade-document-generated");

        var docs = TradeDocsByContainer(loadingList.ContainerNo);
        var pl = Assert.Single(docs);
        Assert.Equal("装箱单", pl.DocType);
        Assert.Equal($"PL-{loadingList.No}", pl.DocNo);
        Assert.Equal(loadingList.ContainerNo, pl.RefNo);           // 来源柜号留痕
        Assert.Equal(tag, pl.CustomerName);
        Assert.Equal("EUR", pl.Currency);                          // 客户档案币种
        Assert.Equal("ROTTERDAM", pl.DestinationPort);             // 无订柜信息时回退客户档案目的港
        Assert.Equal("待制作", pl.Status);
        Assert.Contains($"来源：装柜清单 {loadingList.No}", pl.Remark);
        Assert.Contains("箱数：120", pl.Remark);
        Assert.Contains("毛重：2100.5kg", pl.Remark);
        CaptureEvidence("doc-center-with-container-document");

        AssertEmptyApiIssues();
    }

    // ==================== 场景 3：重复生成守卫 ====================

    [Fact]
    public void 重复生成同类型_被守卫拒绝且单证中心无重复且对话框置灰已生成()
    {
        var tag = "UITD2" + DateTime.Now.ToString("HHmmss");
        LoginAsAdmin();
        InstallApiIssueRecorder();

        var customer = CreateCustomer(tag);
        var order = CreateSalesOrder(tag, customer.Id);

        // 第一次：页面行操作生成默认两件套
        OpenModule("sales-order");
        SearchList(order.No);
        ClickRowMenuAction(order.Id, "generateTradeDocsFromSource");
        ClickModalAction("generateSelectedTradeDocs()");
        AcceptAlert();
        WaitToastContains("已生成");
        Assert.Equal(2, TradeDocsBySalesOrder(order.No).Count);

        // 第二次：已生成类型在对话框中必须置灰且不可再勾选（前端防重）
        OpenModule("sales-order");
        SearchList(order.No);
        ClickRowMenuAction(order.Id, "generateTradeDocsFromSource");
        Wait(20).Until(d => d.FindElements(By.CssSelector("#modal .td-kind")).Count == 5);
        Assert.Equal(2L, ExecuteScriptLong(
            "return document.querySelectorAll('#modal .td-kind[disabled]').length;"));
        Assert.Equal(0L, ExecuteScriptLong(
            "return document.querySelectorAll('#modal .td-kind:checked').length;"));
        CaptureEvidence("duplicate-generation-dialog-disabled");

        // 绕过前端直接调用接口重复生成：服务端守卫必须拒绝且不新增单据
        using var duplicate = JsonDocument.Parse(Api("POST", $"/api/sales-orders/{order.Id}/trade-documents",
            JsonSerializer.Serialize(new { docTypes = new[] { "商业发票", "装箱单" } })));
        Assert.NotEqual(0, duplicate.RootElement.GetProperty("code").GetInt32());
        Assert.Contains("不能重复生成", duplicate.RootElement.GetProperty("message").GetString() ?? string.Empty);
        Assert.Equal(2, TradeDocsBySalesOrder(order.No).Count);

        // 该用例有意触发 1 次业务拒绝（重复生成），其余请求不允许失败
        var issues = ApiIssues();
        Assert.Single(issues);
        Assert.Contains("不能重复生成", issues[0]);
    }

    // ==================== 场景 4：单证中心 Excel 导出 ====================

    [Fact]
    public void 单证中心Excel导出_对话框可用且导出文件可解析()
    {
        var tag = "UITX" + DateTime.Now.ToString("HHmmss");
        LoginAsAdmin();
        InstallApiIssueRecorder();

        var customer = CreateCustomer(tag);
        var order = CreateSalesOrder(tag, customer.Id);
        ApiData("POST", $"/api/sales-orders/{order.Id}/trade-documents",
            JsonSerializer.Serialize(new { docTypes = new[] { "商业发票", "装箱单" } }));

        OpenModule("doc-center");
        SearchList(order.No);
        Assert.True(FunctionExists("openTradeDocExportDialog"),
            "页面未加载 /js/trade-doc-gen.js（前端脚本过期）：请先重新构建并启动 ERP.Api 再运行浏览器验收");
        CaptureEvidence("doc-center-list-ready");

        // 工具栏「导出 Excel」→ 导出对话框（单证类型 / 状态 / 出具日期区间 / 关键字）
        SafeClick(_fx.Driver.FindElement(By.XPath(
            "//button[contains(@onclick,'openTradeDocExportDialog()')]")));
        Wait(20).Until(d => d.FindElements(By.Id("td-ex-doctype")).Count > 0);
        CaptureEvidence("trade-document-export-dialog");

        // 导出接口（以页面登录态调用）：返回可解析的 xlsx（zip 文件头 PK，且内容非空）
        var escapedDocType = Uri.EscapeDataString("商业发票");
        var size = ExecuteScriptLong(
            "const done = arguments[arguments.length - 1];" +
            "(async () => {" +
            "  const res = await fetch('/api/trade/documents/export-excel?docType=" + escapedDocType + "'," +
            "    { headers: { Authorization: 'Bearer ' + (localStorage.getItem('erp_token') || '') } });" +
            "  const buf = new Uint8Array(await res.arrayBuffer());" +
            "  done(buf.length > 1000 && buf[0] === 0x50 && buf[1] === 0x4B ? buf.length : 0);" +
            "})();", async: true);
        Assert.True(size > 1000, "导出的 xlsx 文件不可解析（无 zip 文件头或内容为空）");

        AssertEmptyApiIssues();
    }

    // ==================== 测试数据（经接口创建，每次运行唯一标记） ====================

    private sealed record CustomerSeed(long Id);

    private sealed record SalesOrderSeed(long Id, string No);

    private sealed record LoadingListSeed(long Id, string No, string ContainerNo);

    private sealed record TradeDocRow(long Id, string DocNo, string DocType, string? SalesOrderNo, string? RefNo,
        string? CustomerName, decimal Amount, string? Currency, string? DeparturePort, string? DestinationPort,
        string Status, string? Remark);

    /// <summary>客户档案：币种 EUR / 目的港 ROTTERDAM（用于验证装柜清单来源的回退口径）</summary>
    private CustomerSeed CreateCustomer(string tag)
    {
        var body = JsonSerializer.Serialize(new
        {
            customerCode = "C-" + tag,
            customerName = tag,
            currency = "EUR",
            destinationPort = "ROTTERDAM",
            tradeTerms = "CIF",
            paymentTerms = "T/T 30% deposit"
        });
        var data = ApiData("POST", "/api/base/customers", body);
        return new CustomerSeed(data.GetProperty("id").GetInt64());
    }

    /// <summary>销售订单：目的港 HAMBURG、币种 USD、一行明细 100 × 2.5 = 250</summary>
    private SalesOrderSeed CreateSalesOrder(string tag, long customerId)
    {
        var body = JsonSerializer.Serialize(new
        {
            orderDate = DateTime.Today.ToString("yyyy-MM-dd") + "T00:00:00",
            customerId = customerId,
            currency = "USD",
            exchangeRate = 7.2m,
            depositRatio = 30m,
            tradeTerms = "FOB",
            destinationPort = "HAMBURG",
            paymentTerms = "T/T 30% deposit",
            remark = "TRADE_DOC_UI_TEST",
            details = new[]
            {
                new
                {
                    productId = 1, productName = tag + "-商品", spec = "标准", unit = "PCS",
                    quantity = 100m, unitPrice = 2.5m, remark = tag
                }
            }
        });
        var data = ApiData("POST", "/api/sales-orders", body);
        return new SalesOrderSeed(data.GetProperty("id").GetInt64(), data.GetProperty("orderNo").GetString()!);
    }

    /// <summary>装柜清单：120 箱 / 2100.5kg / 28.75m³（不挂预装柜单，港口回退客户档案）</summary>
    private LoadingListSeed CreateLoadingList(string tag, long customerId, string containerNo)
    {
        var body = JsonSerializer.Serialize(new
        {
            loadingDate = DateTime.Today.ToString("yyyy-MM-dd") + "T00:00:00",
            containerNo = containerNo,
            customerId = customerId,
            shippingMark = "ACME C/NO.1-120",
            remark = "TRADE_DOC_UI_TEST",
            details = new[]
            {
                new
                {
                    productId = 1, productName = tag + "-商品", quantity = 1200m,
                    cartons = 120m, weight = 2100.5m, volume = 28.75m
                }
            }
        });
        var data = ApiData("POST", "/api/container/loading-lists", body);
        return new LoadingListSeed(data.GetProperty("id").GetInt64(),
            data.GetProperty("loadingListNo").GetString()!, containerNo);
    }

    /// <summary>按来源销售订单号检索单证中心记录（单证台账为单表，列表接口已返回全部字段）</summary>
    private List<TradeDocRow> TradeDocsBySalesOrder(string orderNo)
        => TradeDocs().Where(d => d.SalesOrderNo == orderNo).ToList();

    /// <summary>按柜号检索单证中心记录</summary>
    private List<TradeDocRow> TradeDocsByContainer(string containerNo)
        => TradeDocs().Where(d => d.RefNo == containerNo).ToList();

    private List<TradeDocRow> TradeDocs()
    {
        var data = ApiData("GET", "/api/trade/documents?page=1&pageSize=500");
        var rows = new List<TradeDocRow>();
        foreach (var item in data.GetProperty("items").EnumerateArray())
        {
            rows.Add(new TradeDocRow(
                item.GetProperty("id").GetInt64(),
                Str(item, "docNo") ?? string.Empty,
                Str(item, "docType") ?? string.Empty,
                Str(item, "salesOrderNo"),
                Str(item, "refNo"),
                Str(item, "customerName"),
                item.GetProperty("amount").GetDecimal(),
                Str(item, "currency"),
                Str(item, "departurePort"),
                Str(item, "destinationPort"),
                Str(item, "status") ?? string.Empty,
                Str(item, "remark")));
        }
        return rows;
    }

    // ==================== 接口助手（以当前登录态调用应用自身 API） ====================

    private string ExecuteScript(string script)
        => ((IJavaScriptExecutor)_fx.Driver).ExecuteScript(script)?.ToString() ?? string.Empty;

    /// <summary>执行脚本并读取数值结果（async=true 时走 ExecuteAsyncScript，用于 fetch 等异步脚本）</summary>
    private long ExecuteScriptLong(string script, bool async = false)
    {
        var executor = (IJavaScriptExecutor)_fx.Driver;
        object? result;
        if (async)
        {
            _fx.Driver.Manage().Timeouts().AsynchronousJavaScript = TimeSpan.FromSeconds(30);
            result = executor.ExecuteAsyncScript(script);
        }
        else
        {
            result = executor.ExecuteScript(script);
        }
        return Convert.ToInt64(result ?? 0L, CultureInfo.InvariantCulture);
    }

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

    private static string? Str(JsonElement e, string key)
        => e.TryGetProperty(key, out var v) && v.ValueKind != JsonValueKind.Null ? v.GetString() : null;

    // ==================== 浏览器交互助手（与 PiWorkflowUiTests 同一套约定） ====================

    private WebDriverWait Wait(int seconds = 20) => new(_fx.Driver, TimeSpan.FromSeconds(seconds));

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

    /// <summary>点击侧边栏菜单（自动展开所属一级分组），等待列表渲染完成</summary>
    private void OpenModule(string menuCode)
    {
        var child = Wait().Until(d =>
        {
            var items = d.FindElements(By.CssSelector($"#sidebar-nav .nav-item.nav-child[data-code='{menuCode}']"));
            return items.Count > 0 ? items[0] : null;
        });
        if (!child!.Displayed)
        {
            var parent = child.FindElement(By.XPath("ancestor::div[contains(@class,'nav-group')][1]/div[contains(@class,'nav-parent')]"));
            SafeClick(parent);
        }
        Wait().Until(_ => child.Displayed);
        SafeClick(child);
        Wait().Until(d => d.FindElements(By.Id("search-input")).Count > 0);
        Wait().Until(d => d.FindElements(By.CssSelector("#table-wrap tbody tr")).Count > 0);
    }

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

    /// <summary>读取包含关键字的行文本（列表异步重绘：未出现时继续轮询）</summary>
    private string RowText(string keyword) => Wait().Until<string?>(d =>
    {
        try
        {
            var text = d.FindElements(By.CssSelector("#table-wrap tbody tr"))
                .FirstOrDefault(tr => tr.Text.Contains(keyword))?.Text;
            return string.IsNullOrEmpty(text) ? null : text;
        }
        catch (StaleElementReferenceException) { return null; }
    }) ?? string.Empty;

    /// <summary>
    /// 点击列表中指定单据的「更多」→ 目标操作（按行内 onclick 精确匹配）。
    /// 列表异步重绘导致元素失效（stale）时每轮重新定位该行、必要时重新展开菜单，超时才带诊断失败。
    /// </summary>
    private void ClickRowMenuAction(long id, string functionName)
    {
        var itemXPath = $"//button[@onclick='{functionName}({id})']";
        var rowMoreXPath = $"//*[@id='table-wrap']//tr[.//button[@onclick='openForm({id})'] or .//button[@onclick='{functionName}({id})']]" +
                           "//button[contains(@class,'row-more')]";
        var deadline = DateTime.UtcNow.AddSeconds(20);
        var polls = 0;
        var lastError = "尚未出现可见的菜单项";
        while (DateTime.UtcNow < deadline)
        {
            polls++;
            try
            {
                var item = _fx.Driver.FindElements(By.XPath(itemXPath)).FirstOrDefault(b => b.Displayed);
                if (item is null)
                {
                    var more = _fx.Driver.FindElements(By.XPath(rowMoreXPath)).FirstOrDefault();
                    if (more is null) { Thread.Sleep(200); continue; }
                    SafeClick(more);
                    Thread.Sleep(150);
                    item = _fx.Driver.FindElements(By.XPath(itemXPath)).FirstOrDefault(b => b.Displayed);
                }
                if (item is null) { Thread.Sleep(200); continue; }
                ClearToast();
                SafeClick(item);
                return;
            }
            catch (StaleElementReferenceException ex) { lastError = ex.Message; Thread.Sleep(200); }
            catch (InvalidOperationException ex) { lastError = ex.Message; Thread.Sleep(200); }
        }
        CaptureEvidence("row-menu-action-timeout");
        throw new WebDriverTimeoutException(
            $"点击行操作 {functionName}({id}) 超时（轮询 {polls} 次仍未命中）；最后错误：{lastError}；" +
            $"提示条快照 = {ToastSnapshot()}；接口失败记录 = " +
            (ApiIssues() is { Length: > 0 } issues ? string.Join(" | ", issues) : "无"));
    }

    /// <summary>点击模态框内指定 onclick 的按钮（生成单证 / 导出对话框共用）</summary>
    private void ClickModalAction(string onclick)
    {
        var button = Wait().Until(d =>
        {
            try { return d.FindElements(By.XPath($"//*[@id='modal']//button[@onclick='{onclick}']")).FirstOrDefault(); }
            catch (StaleElementReferenceException) { return null; }
        });
        Assert.NotNull(button);
        SafeClick(button!);
    }

    private void AcceptAlert(int seconds = 10)
    {
        // 轮询等待业务确认框（window.confirm）并确认；未出现确认框时静默返回
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            try { _fx.Driver.SwitchTo().Alert().Accept(); return; }
            catch (NoAlertPresentException) { Thread.Sleep(200); }
        }
    }

    private void SafeClick(IWebElement element)
    {
        try { element.Click(); }
        catch (WebDriverException) { ((IJavaScriptExecutor)_fx.Driver).ExecuteScript("arguments[0].click();", element); }
    }

    private void ClearToast() => ExecuteScript(
        "const t = document.getElementById('toast'); t.textContent = ''; t.style.display = 'none'; t.className = 'toast'; return 'ok';");

    /// <summary>等待提示条出现指定文案；超时时给出提示条快照与接口失败记录，便于定位门禁失败原因</summary>
    private void WaitToastContains(string text)
    {
        try
        {
            Wait().Until(d => d.FindElement(By.Id("toast")).Text.Contains(text));
        }
        catch (WebDriverTimeoutException)
        {
            CaptureEvidence("toast-timeout");
            throw new InvalidOperationException(
                $"等待提示「{text}」超时：提示条快照 = {ToastSnapshot()}；接口失败记录 = " +
                (ApiIssues() is { Length: > 0 } issues ? string.Join(" | ", issues) : "无"));
        }
    }

    /// <summary>提示条运行时状态快照（Selenium 的 Text 对隐藏元素返回空串，故必须看 textContent）</summary>
    private string ToastSnapshot() => ExecuteScript(
        "const t = document.getElementById('toast');" +
        "return t ? JSON.stringify({ text: t.textContent, display: t.style.display, cls: t.className }) : 'no-toast-element';");

    /// <summary>页面是否已定义指定全局函数（用于快速识别前端脚本未更新的情况）</summary>
    private bool FunctionExists(string functionName)
        => ExecuteScript($"return typeof window.{functionName} === 'function';") == "True";

    private void CaptureEvidence(string scenario) => _fx.CaptureEvidence(scenario);
}
