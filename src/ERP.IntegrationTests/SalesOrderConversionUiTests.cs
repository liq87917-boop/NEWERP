using OpenQA.Selenium;
using OpenQA.Selenium.Support.UI;
using System.Globalization;
using System.Text.Json;
using Xunit;

namespace ERP.IntegrationTests;

/// <summary>
/// ERP-010 真实浏览器验收（Collection=UiTests，Microsoft Edge）：
/// 1) 报价单页「转销售订单」→ 直接生成销售订单，核对字段映射、明细与来源报价单留痕；
/// 2) 报价单转 PI → PI 页「转销售订单」→ 核对 PI 与报价单双重来源留痕与 PI 值优先的映射；
/// 3) 报价单页「预填销售订单」→ 带入销售订单新增表单（不落库）→ 重复转换被服务端守卫拒绝，且不产生重复单据。
/// 说明：测试数据全部经应用自身接口以登录态创建（不直连数据库、不使用生产数据、不执行 SQL）；
///       截图证据通过 UiTestFixture.CaptureEvidence 输出到 ERP_AI_EVIDENCE_DIR。
/// </summary>
[Collection("UiTests")]
[Trait("Collection", "UiTests")]
public class SalesOrderConversionUiTests
{
    private const string AdminUser = "admin";
    private const string AdminPassword = "Admin@123";
    private readonly UiTestFixture _fx;

    public SalesOrderConversionUiTests(UiTestFixture fx) => _fx = fx;

    // ==================== 场景 1：报价单 → 销售订单（直接生成） ====================

    [Fact]
    public void 报价单转销售订单_页面操作后重新打开核对映射与来源留痕()
    {
        var tag = "UICV" + DateTime.Now.ToString("HHmmss");
        LoginAsAdmin();
        InstallApiIssueRecorder();

        var customer = CreateCustomer(tag);
        var quotation = CreateQuotation(tag, customer.Id, customerName: tag);
        ApproveQuotation(quotation.Id);

        OpenModule("quotation");
        SearchList(quotation.No);
        Assert.True(FunctionExists("quotationToOrder"),
            "页面未加载 /js/sales-pi.js（前端脚本过期）：请先重新构建并启动 ERP.Api 再运行浏览器验收");
        CaptureEvidence("quotation-list-ready");

        ClickRowMenuAction(quotation.Id, "quotationToOrder");
        AcceptAlert();
        WaitToastContains("已生成销售订单");
        CaptureEvidence("quotation-converted-to-sales-order");

        // 服务端事实：生成一张销售订单、来源留痕、报价单转为「已完成」
        var order = FindSalesOrderBySource(quotation.Id, piId: null);
        Assert.True(order.Id > 0, "应按报价单生成销售订单");
        Assert.StartsWith("SO", order.No);
        Assert.Equal(quotation.Id, order.SourceQuotationId);
        Assert.Equal(quotation.No, order.SourceQuotationNo);
        Assert.Equal("FOB", order.TradeTerms);
        Assert.Equal("HAMBURG", order.DestinationPort);
        Assert.Equal("ACME IMPORT GMBH", order.Consignee);
        Assert.Equal("ACME LOGISTICS", order.NotifyParty);
        Assert.Equal(40m, order.DepositRatio);
        Assert.Equal(250m, order.TotalAmount);          // 明细 100 × 2.5
        Assert.Equal(100m, order.DepositAmount);        // 250 × 40%
        Assert.Equal(1, order.DetailCount);
        Assert.Equal("Completed", OrderStatusOfQuotation(quotation.Id));

        // 页面事实：销售订单列表可见该单据，编辑页回显带入值
        OpenModule("sales-order");
        Assert.Contains("销售订单", _fx.Driver.FindElement(By.Id("header-title")).Text);
        SearchList(order.No);
        Assert.Contains(order.No, RowText(order.No));
        CaptureEvidence("sales-order-list-with-generated-order");

        OpenSalesOrderForm(order.No);
        Assert.Equal("FOB", FormValue("f_tradeTerms"));
        Assert.Equal("HAMBURG", FormValue("f_destinationPort"));
        Assert.Equal("ACME IMPORT GMBH", FormValue("f_consignee"));
        Assert.Equal("ACME LOGISTICS", FormValue("f_notifyParty"));
        Assert.Equal("USD", FormValue("f_currency"));
        Assert.Equal("40", FormValue("f_depositRatio"));
        Assert.Equal(quotation.No, FormValue("f_sourceQuotationNo"));
        Assert.Equal(quotation.Id.ToString(CultureInfo.InvariantCulture), FormValue("f_sourceQuotationId"));
        Assert.Equal(string.Empty, FormValue("f_sourcePiNo"));
        Assert.Equal(1, DetailRowCount());
        Assert.Contains(tag, DetailText());
        Assert.Equal(250m, decimal.Parse(DetailTotal(), CultureInfo.InvariantCulture));
        CaptureEvidence("sales-order-reopened-from-quotation");
        ClosePanel();

        AssertEmptyApiIssues();
    }

    // ==================== 场景 2：PI → 销售订单（双来源留痕） ====================

    [Fact]
    public void PI转销售订单_页面操作后核对PI与报价单双来源留痕()
    {
        var tag = "UIPI" + DateTime.Now.ToString("HHmmss");
        LoginAsAdmin();
        InstallApiIssueRecorder();

        var customer = CreateCustomer(tag);
        var quotation = CreateQuotation(tag, customer.Id, customerName: tag);
        ApproveQuotation(quotation.Id);
        var pi = CreatePiFromQuotation(quotation.Id);
        UpdatePi(tag, pi.Id, customer.Id);                 // PI 上覆盖收货人 / 唛头 / 运输条款（PI 值应优先）
        ApprovePi(pi.Id);

        OpenModule("proforma-invoice");
        SearchList(pi.No);
        Assert.True(FunctionExists("piToOrder"),
            "页面未加载 /js/sales-pi.js（前端脚本过期）：请先重新构建并启动 ERP.Api 再运行浏览器验收");
        CaptureEvidence("pi-list-ready");

        ClickRowMenuAction(pi.Id, "piToOrder");
        AcceptAlert();
        WaitToastContains("已生成销售订单");
        CaptureEvidence("pi-converted-to-sales-order");

        var order = FindSalesOrderBySource(quotation.Id, piId: pi.Id);
        Assert.True(order.Id > 0, "应按 PI 生成销售订单");
        Assert.Equal(pi.Id, order.SourcePiId);
        Assert.Equal(pi.No, order.SourcePiNo);
        Assert.Equal(quotation.Id, order.SourceQuotationId);       // PI 背后的报价单一并留痕
        Assert.Equal(quotation.No, order.SourceQuotationNo);
        Assert.Equal("PI CONSIGNEE " + tag, order.Consignee);       // PI 值优先于客户档案默认值
        Assert.Equal("PI NOTIFY " + tag, order.NotifyParty);
        Assert.Equal("PI MARKS " + tag, order.ShippingMarks);
        Assert.Equal("By sea, FCL", order.ShippingMethod);          // PI 运输条款 → 销售订单运输方式
        Assert.Equal(30m, order.DepositRatio);
        Assert.Equal(250m, order.TotalAmount);
        Assert.Equal(75m, order.DepositAmount);                     // 250 × 30%
        Assert.Equal("Completed", PiStatus(pi.Id));   // PI → 已转销售订单

        OpenModule("sales-order");
        SearchList(order.No);
        OpenSalesOrderForm(order.No);
        Assert.Equal("PI CONSIGNEE " + tag, FormValue("f_consignee"));
        Assert.Equal("PI NOTIFY " + tag, FormValue("f_notifyParty"));
        Assert.Equal("By sea, FCL", FormValue("f_shippingMethod"));
        Assert.Equal(pi.No, FormValue("f_sourcePiNo"));
        Assert.Equal(pi.Id.ToString(CultureInfo.InvariantCulture), FormValue("f_sourcePiId"));
        Assert.Equal(quotation.No, FormValue("f_sourceQuotationNo"));
        Assert.Equal(30m, decimal.Parse(FormValue("f_depositRatio"), CultureInfo.InvariantCulture));
        Assert.Equal(1, DetailRowCount());
        Assert.Equal(250m, decimal.Parse(DetailTotal(), CultureInfo.InvariantCulture));
        CaptureEvidence("sales-order-reopened-from-pi");
        ClosePanel();

        AssertEmptyApiIssues();
    }

    // ==================== 场景 3：带入预填 + 重复转换守卫 ====================

    [Fact]
    public void 带入预填不落库_重复转换被服务端守卫拒绝()
    {
        var tag = "UIDUP" + DateTime.Now.ToString("HHmmss");
        LoginAsAdmin();
        InstallApiIssueRecorder();

        var customer = CreateCustomer(tag);
        var quotation = CreateQuotation(tag, customer.Id, customerName: tag);
        ApproveQuotation(quotation.Id);

        // 1) 带入预填：切到销售订单页并打开已带入的新增表单（此时不得产生任何单据）
        OpenModule("quotation");
        SearchList(quotation.No);
        Assert.True(FunctionExists("quotationPrefillOrder"),
            "页面未加载 /js/sales-pi.js（前端脚本过期）：请先重新构建并启动 ERP.Api 再运行浏览器验收");
        ClickRowMenuAction(quotation.Id, "quotationPrefillOrder");
        AcceptAlert();
        WaitToastContains("已按报价单带入");
        Assert.Contains("销售订单", _fx.Driver.FindElement(By.Id("header-title")).Text);
        Assert.True(_fx.Driver.FindElement(By.Id("side-panel")).GetDomAttribute("class")!.Contains("active"),
            "带入预填后应打开销售订单新增表单（侧边面板）");
        WaitFieldValue("f_sourceQuotationNo", quotation.No);

        Assert.Equal("FOB", FormValue("f_tradeTerms"));
        Assert.Equal("HAMBURG", FormValue("f_destinationPort"));
        Assert.Equal("ACME IMPORT GMBH", FormValue("f_consignee"));
        Assert.Equal("USD", FormValue("f_currency"));
        Assert.Equal(quotation.No, FormValue("f_sourceQuotationNo"));
        Assert.Equal(1, DetailRowCount());
        Assert.Equal(250m, decimal.Parse(DetailTotal(), CultureInfo.InvariantCulture));
        CaptureEvidence("sales-order-prefilled-from-quotation");
        ClosePanel();

        Assert.False(SalesOrderExistsBySource(quotation.Id, piId: null), "带入预填与「不保存」都不得生成销售订单");
        Assert.Equal("Approved", OrderStatusOfQuotation(quotation.Id));   // 预填不改来源状态

        // 2) 重复转换守卫：第一次生成成功，重复点击被服务端拒绝且不新增单据
        OpenModule("quotation");
        SearchList(quotation.No);
        ClickRowMenuAction(quotation.Id, "quotationToOrder");
        AcceptAlert();
        WaitToastContains("已生成销售订单");

        var first = FindSalesOrderBySource(quotation.Id, piId: null);
        Assert.True(first.Id > 0);

        using var duplicate = JsonDocument.Parse(Api("POST", $"/api/sales/quotations/{quotation.Id}/to-order"));
        Assert.NotEqual(0, duplicate.RootElement.GetProperty("code").GetInt32());
        Assert.Contains("已生成销售订单", duplicate.RootElement.GetProperty("message").GetString() ?? string.Empty);
        Assert.Single(OrdersBySource(quotation.Id, piId: null));

        // 该用例有意触发 1 次业务拒绝（重复转换），其余请求不允许失败
        var issues = ApiIssues();
        Assert.Single(issues);
        Assert.Contains("已生成销售订单", issues[0]);
    }

    // ==================== 接口助手（以当前登录态调用应用自身 API） ====================

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

    private static string? Str(JsonElement e, string key)
        => e.TryGetProperty(key, out var v) && v.ValueKind != JsonValueKind.Null ? v.GetString() : null;

    private static long? Long(JsonElement e, string key)
        => e.TryGetProperty(key, out var v) && v.ValueKind != JsonValueKind.Null ? v.GetInt64() : null;

    // ==================== 测试数据（经接口创建，每次运行唯一标记） ====================

    private sealed record CustomerSeed(long Id);

    private sealed record QuotationSeed(long Id, string No);

    private sealed record PiSeed(long Id, string No);

    /// <summary>客户档案：带收货人 / 通知人 / 唛头 / 定金比例 / 业务性质 / 佣金比例默认值</summary>
    private CustomerSeed CreateCustomer(string tag)
    {
        var body = JsonSerializer.Serialize(new
        {
            customerCode = "C-" + tag,
            customerName = tag,
            consignee = "ACME IMPORT GMBH",
            notifyParty = "ACME LOGISTICS",
            defaultShippingMark = "ACME\r\nHAMBURG\r\nC/NO.1-120",
            depositRatio = 40m,
            businessNature = "自营出口",
            commissionRatio = 3.5m,
            tradeTerms = "CIF",
            destinationPort = "ROTTERDAM",
            paymentTerms = "T/T 30% deposit, balance against B/L copy"
        });
        var data = ApiData("POST", "/api/base/customers", body);
        return new CustomerSeed(data.GetProperty("id").GetInt64());
    }

    /// <summary>报价单：单行明细 100 × 2.5 = 250（客户名为运行标记，便于关键字检索）</summary>
    private QuotationSeed CreateQuotation(string tag, long customerId, string customerName)
    {
        var body = JsonSerializer.Serialize(new
        {
            quotationDate = DateTime.Today.ToString("yyyy-MM-dd"),
            validUntil = DateTime.Today.AddDays(30).ToString("yyyy-MM-dd"),
            customerId,
            customerName,
            contactPerson = "Mr. UI",
            tradeTerms = "FOB",
            portOfLoading = "NINGBO",
            portOfDestination = "HAMBURG",
            paymentTerms = "T/T 30% deposit",
            leadTime = "35 days after deposit",
            currency = "USD",
            exchangeRate = 7.2m,
            salesmanName = "自动化验收",
            remark = "ORDER_CONVERSION_UI",
            details = new object[]
            {
                new { productCode = "P-UI-1", productName = tag + "-商品A", spec = "大", unit = "PCS", quantity = 100m, unitPrice = 2.5m }
            }
        });
        var data = ApiData("POST", "/api/sales/quotations", body);
        return new QuotationSeed(data.GetProperty("id").GetInt64(), data.GetProperty("quotationNo").GetString()!);
    }

    private void ApproveQuotation(long id) => ApiData("POST", $"/api/sales/quotations/{id}/approve");

    private string OrderStatusOfQuotation(long id)
        => ApiData("GET", $"/api/sales/quotations/{id}").GetProperty("status").GetString()!;

    /// <summary>由已审核报价单生成 PI（走应用既有「报价单 → PI」接口，保证来源报价单真实存在）</summary>
    private PiSeed CreatePiFromQuotation(long quotationId)
    {
        var data = ApiData("POST", $"/api/sales/quotations/{quotationId}/to-pi");
        return new PiSeed(data.GetProperty("id").GetInt64(), data.GetProperty("piNo").GetString()!);
    }

    /// <summary>PI 上覆盖收货人 / 通知人 / 唛头 / 运输条款与定金比例（验证 PI 值优先于客户档案默认值）</summary>
    private void UpdatePi(string tag, long piId, long customerId)
    {
        var body = JsonSerializer.Serialize(new
        {
            piDate = DateTime.Today.ToString("yyyy-MM-dd"),
            customerId,
            customerName = tag,
            consignee = "PI CONSIGNEE " + tag,
            notifyParty = "PI NOTIFY " + tag,
            shippingMarks = "PI MARKS " + tag,
            shippingTerms = "By sea, FCL",
            tradeTerms = "CIF",
            portOfDestination = "HAMBURG",
            paymentTerms = "L/C at sight",
            leadTime = "30 days after deposit",
            currency = "USD",
            exchangeRate = 7.2m,
            depositRatio = 30m,
            remark = "PI_CONVERSION_UI",
            details = new object[]
            {
                new { productCode = "P-UI-1", productName = tag + "-商品A", spec = "大", unit = "PCS", quantity = 100m, unitPrice = 2.5m }
            }
        });
        ApiData("PUT", $"/api/sales/proforma-invoices/{piId}", body);
    }

    private void ApprovePi(long id) => ApiData("POST", $"/api/sales/proforma-invoices/{id}/approve");

    private string PiStatus(long id)
        => ApiData("GET", $"/api/sales/proforma-invoices/{id}").GetProperty("status").GetString()!;

    /// <summary>按来源（报价单 / PI）检索销售订单；列表接口不返回明细，命中后再取详情补明细行数</summary>
    private List<SalesOrderRow> OrdersBySource(long? quotationId, long? piId)
    {
        var data = ApiData("GET", "/api/sales-orders?page=1&pageSize=200");
        var rows = new List<SalesOrderRow>();
        foreach (var item in data.GetProperty("items").EnumerateArray())
        {
            var sourceQuotationId = Long(item, "sourceQuotationId");
            var sourcePiId = Long(item, "sourcePiId");
            var match = (quotationId.HasValue && sourceQuotationId == quotationId)
                        || (piId.HasValue && sourcePiId == piId);
            if (!match) continue;

            var id = item.GetProperty("id").GetInt64();
            var detail = ApiData("GET", $"/api/sales-orders/{id}");
            rows.Add(new SalesOrderRow(id,
                item.GetProperty("orderNo").GetString()!,
                sourceQuotationId, Str(item, "sourceQuotationNo"),
                sourcePiId, Str(item, "sourcePiNo"),
                item.GetProperty("totalAmount").GetDecimal(),
                item.GetProperty("depositRatio").GetDecimal(),
                item.GetProperty("depositAmount").GetDecimal(),
                Str(item, "tradeTerms"), Str(item, "destinationPort"), Str(item, "consignee"),
                Str(item, "notifyParty"), Str(item, "shippingMarks"), Str(item, "shippingMethod"),
                detail.GetProperty("details").GetArrayLength()));
        }
        return rows;
    }

    private SalesOrderRow FindSalesOrderBySource(long quotationId, long? piId)
    {
        var rows = OrdersBySource(quotationId, piId);
        Assert.True(rows.Count == 1, $"该来源应生成且仅生成一张销售订单，实际 {rows.Count} 张");
        return rows[0];
    }

    private bool SalesOrderExistsBySource(long quotationId, long? piId)
        => OrdersBySource(quotationId, piId).Count > 0;

    private sealed record SalesOrderRow(long Id, string No, long? SourceQuotationId, string? SourceQuotationNo,
        long? SourcePiId, string? SourcePiNo, decimal TotalAmount, decimal DepositRatio, decimal DepositAmount,
        string? TradeTerms, string? DestinationPort, string? Consignee, string? NotifyParty,
        string? ShippingMarks, string? ShippingMethod, int DetailCount);

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

    /// <summary>打开销售订单编辑面板（等待主表来源字段与明细行渲染完成）</summary>
    private void OpenSalesOrderForm(string orderNo)
    {
        var editButton = Wait().Until(d =>
        {
            try
            {
                return d.FindElements(By.CssSelector("#table-wrap tbody tr"))
                    .Where(tr => tr.Text.Contains(orderNo))
                    .Select(tr => tr.FindElements(By.CssSelector("button"))
                        .FirstOrDefault(b => (b.GetDomAttribute("onclick") ?? string.Empty)
                            .StartsWith("openForm(", StringComparison.Ordinal)))
                    .FirstOrDefault(b => b != null);
            }
            catch (StaleElementReferenceException) { return null; }
        });
        Assert.NotNull(editButton);
        SafeClick(editButton!);
        Wait().Until(d => d.FindElement(By.Id("side-panel")).GetDomAttribute("class")!.Contains("active"));
        Wait().Until(d => d.FindElements(By.Id("f_sourceQuotationNo")).Count > 0);
        Wait().Until(d => d.FindElements(By.CssSelector("#detail-body tr")).Count > 0);
    }

    private string FormValue(string elementId)
    {
        var el = _fx.Driver.FindElements(By.Id(elementId)).FirstOrDefault();
        return el?.GetDomProperty("value") ?? string.Empty;
    }

    /// <summary>等待表单字段值变为期望值（带入 / 回填是异步的）</summary>
    private void WaitFieldValue(string elementId, string expected)
        => Wait().Until(_ => FormValue(elementId) == expected);

    private int DetailRowCount() => _fx.Driver.FindElements(By.CssSelector("#detail-body tr")).Count;

    private string DetailText() => ExecuteScript(
        "return Array.from(document.querySelectorAll('#detail-body input, #detail-body select'))" +
        ".map(e => String(e.value ?? '')).join(' | ');");

    private string DetailTotal() => ExecuteScript(
        "const t = document.getElementById('detail-total'); return t ? t.textContent.trim() : '';");

    private void ClosePanel()
    {
        var close = _fx.Driver.FindElements(By.CssSelector("#side-panel .side-panel-close")).FirstOrDefault();
        if (close != null) SafeClick(close);
        try { Wait(5).Until(d => !d.FindElement(By.Id("side-panel")).GetDomAttribute("class")!.Contains("active")); }
        catch (WebDriverTimeoutException) { ExecuteScript("closePanel(); return 'ok';"); }
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
