using OpenQA.Selenium;
using OpenQA.Selenium.Support.UI;
using System.Globalization;
using System.Text.Json;
using Xunit;

namespace ERP.IntegrationTests;

/// <summary>
/// ERP-020 真实浏览器验收（Collection=UiTests，Microsoft Edge）：
/// 1) 供应商比价页「生成采购订单」→ 由选中比价行直接生成采购订单，核对字段映射、明细、来源留痕与比价行状态；
/// 2) 「预填采购订单」→ 带入采购订单新增表单（不落库），重复转换被服务端守卫拒绝且不产生重复单据。
/// 说明：测试数据全部经应用自身接口以登录态创建（不直连数据库、不使用生产数据、不执行 SQL）；
///       截图证据通过 UiTestFixture.CaptureEvidence 输出到 ERP_AI_EVIDENCE_DIR。
/// 当前开发阶段浏览器验收延后执行（browser_deferred），本用例在 FINAL-UI-ACCEPTANCE 阶段运行。
/// </summary>
[Collection("UiTests")]
[Trait("Collection", "UiTests")]
public class PurchaseQuoteConversionUiTests
{
    private const string AdminUser = "admin";
    private const string AdminPassword = "Admin@123";
    private readonly UiTestFixture _fx;

    public PurchaseQuoteConversionUiTests(UiTestFixture fx) => _fx = fx;

    // ==================== 场景 1：选中比价行 → 采购订单（直接生成） ====================

    [Fact]
    public void 选中比价行转采购订单_页面操作后核对映射与来源留痕()
    {
        var tag = "UIPQ" + DateTime.Now.ToString("HHmmss");
        LoginAsAdmin();
        InstallApiIssueRecorder();

        var customer = CreateCustomer(tag);
        var supplier = CreateSupplier(tag);
        var quote = CreateSelectedQuote(tag, customer.Id, supplier.Id);

        OpenModule("purchase-quote");
        SearchList(quote.No);
        Assert.True(FunctionExists("purchaseQuoteToOrder"),
            "页面未加载 /js/purchase-order-conversion.js（前端脚本过期）：请先重新构建并启动 ERP.Api 再运行浏览器验收");
        Assert.Contains("已选中", RowText(quote.No));
        CaptureEvidence("purchase-quote-list-ready");

        ClickRowMenuAction(quote.Id, "purchaseQuoteToOrder");
        AcceptAlert();
        WaitToastContains("已生成采购订单");
        CaptureEvidence("purchase-quote-converted-to-purchase-order");

        // 服务端事实：按来源标记找回生成的采购订单（备注留痕）
        var order = FindOrderByQuote(quote.No, quote.Id);
        Assert.True(order.Id > 0, "应按选中比价行生成采购订单");
        Assert.StartsWith("PO", order.No);
        Assert.Equal(13000m, order.TotalAmount);                 // 报价总额写错为 1，服务端按 5000 × 2.6 重算
        Assert.Equal("T/T 30% deposit", order.PaymentTerms);
        Assert.Equal(1, order.DetailCount);
        Assert.Equal(2.6m, order.DetailUnitPrice);
        Assert.Equal(5000m, order.DetailQuantity);
        Assert.Contains("QUOTE_CONVERSION_UI", order.Remark);
        Assert.Contains($"来源比价 {quote.No}（比价行 #{quote.Id}）", order.Remark);

        // 比价行留痕：状态「已转采购订单」，RefOrderNo 写回采购单号
        var savedQuote = QuoteRow(quote.Id);
        Assert.Equal("已转采购订单", savedQuote.Status);
        Assert.Equal(order.No, savedQuote.RefOrderNo);

        // 页面事实：采购订单列表可见该单据，编辑页回显带入值
        OpenModule("purchase-order");
        SearchList(order.No);
        Assert.Contains(order.No, RowText(order.No));
        CaptureEvidence("purchase-order-list-with-generated-order");

        OpenPurchaseOrderForm(order.No);
        Assert.Equal(supplier.Id.ToString(CultureInfo.InvariantCulture), FormValue("f_supplierId"));
        Assert.Equal("USD", FormValue("f_currency"));
        Assert.Equal("T/T 30% deposit", FormValue("f_paymentTerms"));
        Assert.Equal("true", FormValue("f_taxIncluded"));
        Assert.Equal(DateTime.Today.AddDays(25).ToString("yyyy-MM-dd"), FormValue("f_deliveryDate"));
        Assert.Equal(tag + "-客户", FormValue("f_owningCustomerName"));
        Assert.Equal(customer.Id.ToString(CultureInfo.InvariantCulture), FormValue("f_owningCustomerId"));
        Assert.Equal(string.Empty, FormValue("f_owningSalesOrderNo"));
        Assert.Equal(1, DetailRowCount());
        Assert.Equal(13000m, decimal.Parse(DetailTotal(), CultureInfo.InvariantCulture));
        Assert.Contains(tag + "-商品", DetailText());
        CaptureEvidence("purchase-order-reopened-from-quote");
        ClosePanel();

        AssertEmptyApiIssues();
    }

    // ==================== 场景 2：带入预填 + 重复转换守卫 ====================

    [Fact]
    public void 带入预填不落库_重复转换被服务端守卫拒绝()
    {
        var tag = "UIPQD" + DateTime.Now.ToString("HHmmss");
        LoginAsAdmin();
        InstallApiIssueRecorder();

        var customer = CreateCustomer(tag);
        var supplier = CreateSupplier(tag);
        var quote = CreateSelectedQuote(tag, customer.Id, supplier.Id);

        // 1) 带入预填：切到采购订单页并打开已带入的新增表单（此时不得产生任何单据）
        OpenModule("purchase-quote");
        SearchList(quote.No);
        Assert.True(FunctionExists("purchaseQuotePrefillOrder"),
            "页面未加载 /js/purchase-order-conversion.js（前端脚本过期）：请先重新构建并启动 ERP.Api 再运行浏览器验收");
        ClickRowMenuAction(quote.Id, "purchaseQuotePrefillOrder");
        AcceptAlert();
        WaitToastContains("已按供应商比价");
        Assert.Contains("采购订单", _fx.Driver.FindElement(By.Id("header-title")).Text);
        Assert.True(_fx.Driver.FindElement(By.Id("side-panel")).GetDomAttribute("class")!.Contains("active"),
            "带入预填后应打开采购订单新增表单（侧边面板）");
        WaitFieldValue("f_paymentTerms", "T/T 30% deposit");

        Assert.Equal(supplier.Id.ToString(CultureInfo.InvariantCulture), FormValue("f_supplierId"));
        Assert.Equal("USD", FormValue("f_currency"));
        Assert.Equal("true", FormValue("f_taxIncluded"));
        Assert.Equal(DateTime.Today.AddDays(25).ToString("yyyy-MM-dd"), FormValue("f_deliveryDate"));
        Assert.Equal(tag + "-客户", FormValue("f_owningCustomerName"));
        Assert.Equal(1, DetailRowCount());
        Assert.Equal(13000m, decimal.Parse(DetailTotal(), CultureInfo.InvariantCulture));
        CaptureEvidence("purchase-order-prefilled-from-quote");
        ClosePanel();

        Assert.Empty(PurchaseOrdersByQuote(quote.No, quote.Id));      // 预填与「不保存」都不得生成采购订单
        Assert.Equal("已选中", QuoteRow(quote.Id).Status);             // 预填不改来源状态

        // 2) 重复转换守卫：第一次生成成功，重复调用被服务端拒绝且不新增单据
        ClickRowMenuAction(quote.Id, "purchaseQuoteToOrder");
        AcceptAlert();
        WaitToastContains("已生成采购订单");

        var first = FindOrderByQuote(quote.No, quote.Id);
        Assert.True(first.Id > 0);

        using var duplicate = JsonDocument.Parse(Api("POST", $"/api/purchase/quotes/{quote.Id}/to-order"));
        Assert.NotEqual(0, duplicate.RootElement.GetProperty("code").GetInt32());
        Assert.Contains("已生成采购订单", duplicate.RootElement.GetProperty("message").GetString() ?? string.Empty);
        Assert.Single(PurchaseOrdersByQuote(quote.No, quote.Id));

        // 该用例有意触发 1 次业务拒绝（重复转换），其余请求不允许失败
        var issues = ApiIssues();
        Assert.Single(issues);
        Assert.Contains("已生成采购订单", issues[0]);
    }

    // ==================== 场景 3：批次转采购订单（ERP-027：兼容行合并 / 不兼容行独立 / 不合格行跳过） ====================

    [Fact]
    public void 批次转采购订单_兼容行合并为一张_不兼容行独立成单_未选中行跳过()
    {
        var tag = "UIPQB" + DateTime.Now.ToString("HHmmss");
        LoginAsAdmin();
        InstallApiIssueRecorder();

        var customer = CreateCustomer(tag);
        var supplierA = CreateSupplier(tag + "A");
        var supplierB = CreateSupplier(tag + "B");
        var quoteNo = "PQ-" + tag;
        var line1 = CreateQuoteLine(quoteNo, tag + "-商品1", supplierA.Id, 5000m, 2.6m, customer.Id, tag);
        var line2 = CreateQuoteLine(quoteNo, tag + "-商品2", supplierA.Id, 1000m, 3.5m, customer.Id, tag);   // 同供应商 → 与 line1 合并
        var line3 = CreateQuoteLine(quoteNo, tag + "-商品3", supplierB.Id, 2000m, 1.2m, customer.Id, tag);   // 不同供应商 → 独立成单
        var line4 = CreateQuoteLine(quoteNo, tag + "-商品4", supplierB.Id, 100m, 1m, customer.Id, tag, selected: false);

        OpenModule("purchase-quote");
        SearchList(quoteNo);
        Assert.True(FunctionExists("purchaseQuoteBatchToOrder"),
            "页面未加载 /js/purchase-order-conversion.js（前端脚本过期）：请先重新构建并启动 ERP.Api 再运行浏览器验收");
        Assert.True(FunctionExists("purchaseQuoteBatchToOrderByNo"),
            "页面未加载批次工具栏入口（前端脚本过期）：请先重新构建并启动 ERP.Api 再运行浏览器验收");
        Assert.Contains("批次转采购订单", ExecuteScript(
            "const t = document.querySelector('.toolbar-actions'); return t ? t.textContent : '';"));
        CaptureEvidence("purchase-quote-batch-list-ready");

        ClickRowMenuAction(line1.Id, "purchaseQuoteBatchToOrder");
        AcceptAlert();                                        // 计划确认框（只读计划 → 用户确认后才落库）
        WaitToastContains("已生成 2 张采购订单");
        CaptureEvidence("purchase-quote-batch-converted");

        // 兼容组：同供应商 + 币种的两行合并为一张订单（服务端重算合计 + 逐行来源留痕）
        var consolidated = FindOrderByQuote(quoteNo, line1.Id);
        Assert.True(consolidated.Id > 0, "同供应商 + 币种的两行应合并为一张采购订单");
        Assert.Equal(2, consolidated.DetailCount);
        Assert.Equal(13000m + 3500m, consolidated.TotalAmount);          // 报价总额故意写错为 1，不采信
        Assert.Contains(SourceMarker(quoteNo, line2.Id), consolidated.Remark);

        // 不兼容组：不同供应商的行独立成单
        var separate = FindOrderByQuote(quoteNo, line3.Id);
        Assert.True(separate.Id > 0, "不同供应商的比价行应保持独立成单");
        Assert.Equal(1, separate.DetailCount);
        Assert.Equal(2400m, separate.TotalAmount);
        Assert.NotEqual(consolidated.No, separate.No);

        // 来源留痕：三行状态与采购单号回写；未选中行保持原状态且不生成订单
        Assert.Equal("已转采购订单", QuoteRow(line1.Id).Status);
        Assert.Equal(consolidated.No, QuoteRow(line1.Id).RefOrderNo);
        Assert.Equal(consolidated.No, QuoteRow(line2.Id).RefOrderNo);
        Assert.Equal(separate.No, QuoteRow(line3.Id).RefOrderNo);
        Assert.Equal("待比较", QuoteRow(line4.Id).Status);
        Assert.Equal(string.Empty, QuoteRow(line4.Id).RefOrderNo ?? string.Empty);
        Assert.Empty(PurchaseOrdersByQuote(quoteNo, line4.Id));

        AssertEmptyApiIssues();
    }

    // ==================== 场景 4：批次保护（重复转换拒绝 + 不合格行显式跳过） ====================

    [Fact]
    public void 批次重复转换_被服务端拒绝且不新增单据_未选中行显式跳过()
    {
        var tag = "UIPQD" + DateTime.Now.ToString("HHmmss");
        LoginAsAdmin();
        InstallApiIssueRecorder();

        var customer = CreateCustomer(tag);
        var supplier = CreateSupplier(tag);
        var quoteNo = "PQ-" + tag;
        var line1 = CreateQuoteLine(quoteNo, tag + "-商品1", supplier.Id, 5000m, 2.6m, customer.Id, tag);
        var line2 = CreateQuoteLine(quoteNo, tag + "-商品2", supplier.Id, 1000m, 3.5m, customer.Id, tag);
        var unselected = CreateQuoteLine(quoteNo, tag + "-商品3", supplier.Id, 100m, 1m, customer.Id, tag, selected: false);

        OpenModule("purchase-quote");
        SearchList(quoteNo);
        ClickRowMenuAction(line1.Id, "purchaseQuoteBatchToOrder");
        AcceptAlert();
        WaitToastContains("已生成 1 张采购订单");
        CaptureEvidence("purchase-quote-batch-first-conversion");

        var first = PurchaseOrdersByQuote(quoteNo, line1.Id);
        Assert.Single(first);
        Assert.Equal(16500m, first[0].TotalAmount);
        Assert.Equal(2, first[0].DetailCount);

        // 重复批次转换：服务端守卫拒绝（同一来源行不再生成订单）
        using var duplicate = JsonDocument.Parse(Api("POST", "/api/purchase/quotes/batch-to-order",
            JsonSerializer.Serialize(new { quoteNo })));
        Assert.NotEqual(0, duplicate.RootElement.GetProperty("code").GetInt32());
        Assert.Contains("没有可转换", duplicate.RootElement.GetProperty("message").GetString() ?? string.Empty);
        Assert.Single(PurchaseOrdersByQuote(quoteNo, line1.Id));
        Assert.Equal(2, PurchaseOrdersByQuote(quoteNo, line1.Id).Single().DetailCount);   // 明细未重复

        // 只读计划：批次内已无可转换行，未选中行被显式列出原因（不写库）
        var plan = ApiData("GET", $"/api/purchase/quotes/batch-order-plan?quoteNo={Uri.EscapeDataString(quoteNo)}");
        Assert.Equal(0, plan.GetProperty("groupCount").GetInt32());
        Assert.Equal(0, plan.GetProperty("eligibleLineCount").GetInt32());
        var skipped = plan.GetProperty("skipped").EnumerateArray()
            .Select(s => s.GetProperty("reason").GetString() ?? string.Empty).ToList();
        Assert.Contains(skipped, reason => reason.Contains("未选中供应商"));
        Assert.Contains(skipped, reason => reason.Contains("已生成采购订单"));
        Assert.Equal("待比较", QuoteRow(unselected.Id).Status);

        // 该用例有意触发 1 次业务拒绝（重复批次转换），其余请求不允许失败
        var issues = ApiIssues();
        Assert.Single(issues);
        Assert.Contains("没有可转换", issues[0]);
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

    // ==================== 测试数据（经接口创建，每次运行唯一标记） ====================

    private sealed record CustomerSeed(long Id);

    private sealed record SupplierSeed(long Id);

    private sealed record QuoteSeed(long Id, string No);

    private sealed record QuoteRowData(string Status, string? RefOrderNo);

    private sealed record OrderRow(long Id, string No, decimal TotalAmount, string? PaymentTerms, string? Remark,
        int DetailCount, decimal DetailUnitPrice, decimal DetailQuantity);

    /// <summary>采购订单备注里的来源标记（后端 PurchaseQuoteConversion.SourceMarker 的同一口径）</summary>
    private static string SourceMarker(string quoteNo, long quoteId)
        => $"来源比价 {quoteNo}（比价行 #{quoteId}）";

    /// <summary>客户档案（名称带运行标记，便于界面检索与断言）</summary>
    private CustomerSeed CreateCustomer(string tag)
    {
        var body = JsonSerializer.Serialize(new { customerCode = "C-" + tag, customerName = tag + "-客户" });
        var data = ApiData("POST", "/api/base/customers", body);
        return new CustomerSeed(data.GetProperty("id").GetInt64());
    }

    /// <summary>供应商档案（比价行与采购订单都引用它，避免带入时名称查询失败）</summary>
    private SupplierSeed CreateSupplier(string tag)
    {
        var body = JsonSerializer.Serialize(new
        {
            supplierCode = "S-" + tag,
            supplierName = tag + "-供应商",
            supplierType = "档口",
            paymentTerms = "T/T 30% deposit"
        });
        var data = ApiData("POST", "/api/base/suppliers", body);
        return new SupplierSeed(data.GetProperty("id").GetInt64());
    }

    /// <summary>已选中的比价行（单行报价 5000 × 2.6）：报价总额故意写成 1，用于验证服务端重算</summary>
    private QuoteSeed CreateSelectedQuote(string tag, long customerId, long supplierId)
    {
        var body = JsonSerializer.Serialize(new
        {
            quoteNo = "PQ-" + tag,
            quoteDate = DateTime.Today.ToString("yyyy-MM-dd"),
            productName = tag + "-商品",
            spec = "大号",
            unit = "PCS",
            quantity = 5000m,
            supplierId,
            supplierName = tag + "-供应商",
            supplierType = "档口",
            quotePrice = 2.6m,
            totalAmount = 1m,
            currency = "USD",
            taxIncluded = true,
            deliveryDays = 25,
            minOrderQty = 1000,
            paymentTerms = "T/T 30% deposit",
            isSelected = true,
            status = "已选中",
            customerId,
            customerName = tag + "-客户",
            remark = "QUOTE_CONVERSION_UI"
        });
        var data = ApiData("POST", "/api/purchase/quotes", body);
        return new QuoteSeed(data.GetProperty("id").GetInt64(), data.GetProperty("quoteNo").GetString()!);
    }

    /// <summary>
    /// 在同一比价批次下再新增一行报价（ERP-027 批次转换用）：可指定是否「选中」；
    /// 报价总额同样故意写成 1，用于验证采购订单合计由服务端重算。
    /// </summary>
    private QuoteSeed CreateQuoteLine(string quoteNo, string productName, long supplierId, decimal quantity, decimal quotePrice,
        long customerId, string tag, bool selected = true)
    {
        var body = JsonSerializer.Serialize(new
        {
            quoteNo,
            quoteDate = DateTime.Today.ToString("yyyy-MM-dd"),
            productName,
            spec = "大号",
            unit = "PCS",
            quantity,
            supplierId,
            supplierName = tag + "-供应商",
            supplierType = "档口",
            quotePrice,
            totalAmount = 1m,
            currency = "USD",
            taxIncluded = true,
            deliveryDays = 25,
            minOrderQty = 1000,
            paymentTerms = "T/T 30% deposit",
            isSelected = selected,
            status = selected ? "已选中" : "待比较",
            customerId,
            customerName = tag + "-客户",
            remark = "QUOTE_CONVERSION_UI"
        });
        var data = ApiData("POST", "/api/purchase/quotes", body);
        return new QuoteSeed(data.GetProperty("id").GetInt64(), data.GetProperty("quoteNo").GetString()!);
    }

    private QuoteRowData QuoteRow(long id)
    {
        var data = ApiData("GET", $"/api/purchase/quotes/{id}");
        return new QuoteRowData(data.GetProperty("status").GetString() ?? string.Empty, Str(data, "refOrderNo"));
    }

    /// <summary>按来源标记（采购订单备注）找回该比价行生成的采购订单；列表不返回明细，命中后再取详情补明细</summary>
    private List<OrderRow> PurchaseOrdersByQuote(string quoteNo, long quoteId)
    {
        var marker = SourceMarker(quoteNo, quoteId);
        var data = ApiData("GET", "/api/purchase-orders?page=1&pageSize=200");
        var rows = new List<OrderRow>();
        foreach (var item in data.GetProperty("items").EnumerateArray())
        {
            var remark = Str(item, "remark");
            if (remark is null || !remark.Contains(marker)) continue;

            var id = item.GetProperty("id").GetInt64();
            var detail = ApiData("GET", $"/api/purchase-orders/{id}");
            var lines = detail.GetProperty("details").EnumerateArray()
                .Where(d => !(d.TryGetProperty("isDeleted", out var del) && del.GetBoolean()))
                .ToList();
            rows.Add(new OrderRow(id,
                item.GetProperty("orderNo").GetString()!,
                item.GetProperty("totalAmount").GetDecimal(),
                Str(item, "paymentTerms"), remark, lines.Count,
                lines.Count > 0 ? lines[0].GetProperty("unitPrice").GetDecimal() : 0m,
                lines.Count > 0 ? lines[0].GetProperty("quantity").GetDecimal() : 0m));
        }
        return rows;
    }

    private OrderRow FindOrderByQuote(string quoteNo, long quoteId)
        => PurchaseOrdersByQuote(quoteNo, quoteId).FirstOrDefault()
           ?? new OrderRow(0, string.Empty, 0m, null, null, 0, 0m, 0m);

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

    /// <summary>打开采购订单编辑面板（等待归属销售订单字段与明细行渲染完成）</summary>
    private void OpenPurchaseOrderForm(string orderNo)
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
        Wait().Until(d => d.FindElements(By.Id("f_owningSalesOrderNo")).Count > 0);
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
