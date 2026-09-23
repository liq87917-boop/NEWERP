using OpenQA.Selenium;
using OpenQA.Selenium.Support.UI;
using System.Globalization;
using System.Text.Json;
using Xunit;

namespace ERP.IntegrationTests;

/// <summary>
/// ERP-007 真实浏览器验收（Collection=UiTests，Microsoft Edge）：
/// 1) 登录后在「询报价 → 报价单」列表对已审核报价单执行「转 PI」，生成 PI 并落库；
/// 2) 打开生成的 PI，核对复制的客户 / 条款 / 币种 / 明细 / 合计 / 定金；
/// 3) 草稿 PI 修改保存 → 审核 → 审核后修改被拒 → 销审 → 修改恢复；作废校验；
/// 4) 打开 PI 打印预览，确认单据渲染且期间没有非预期 API 失败。
/// 说明：测试数据经应用自身接口以登录态创建（不直连数据库，不使用生产数据）；
///       截图证据通过 UiTestFixture.CaptureEvidence 输出到 ERP_AI_EVIDENCE_DIR。
/// </summary>
[Collection("UiTests")]
// [Collection] 只负责共享 Fixture，不会转成 VSTest 用例属性；
// 显式 Trait 才能被验收脚本 / CI 的 --filter "Collection=UiTests" 正确选中（否则 0 用例静默通过）。
[Trait("Collection", "UiTests")]
public class PiWorkflowUiTests
{
    private const string AdminUser = "admin";
    private const string AdminPassword = "Admin@123";
    private readonly UiTestFixture _fx;

    public PiWorkflowUiTests(UiTestFixture fx) => _fx = fx;

    // ==================== 场景 1 + 2：报价单 → 转 PI → 打开 PI 核对 ====================

    [Fact]
    public void 报价单转PI_并可在PI列表与编辑页核对复制内容()
    {
        var tag = "UI" + DateTime.Now.ToString("HHmmss");
        LoginAsAdmin();
        InstallApiIssueRecorder();

        var quotation = SeedQuotation(tag);
        ApproveQuotation(quotation.Id);

        OpenModule("quotation");
        SearchList(quotation.No);
        Assert.True(FunctionExists("quotationToPi"),
            "页面未加载 /js/sales-pi.js（前端脚本过期）：请先重新构建并启动 ERP.Api 再运行浏览器验收");
        CaptureEvidence("quotation-list-ready");

        ClickRowMenuAction(quotation.Id, "quotationToPi");
        AcceptAlert();
        WaitToastContains("已生成形式发票 PI");
        CaptureEvidence("quotation-converted-to-pi");

        // 服务端事实：已生成 PI、回填来源报价单、原报价单改为「已转 PI」
        var pi = FindPi(tag);
        Assert.True(pi.Id > 0, "应生成 PI 记录");
        Assert.StartsWith("PI", pi.No);
        Assert.Equal(quotation.Id, pi.QuotationId);
        Assert.Equal(quotation.No, pi.QuotationNo);
        Assert.Equal("Pending", pi.Status);
        Assert.Equal(750m, pi.TotalAmount);
        Assert.Equal(225m, pi.DepositAmount);
        var quotationData = ApiData("GET", $"/api/sales/quotations/{quotation.Id}");
        Assert.Equal("Completed", quotationData.GetProperty("status").GetString());   // 原报价单 → 已转 PI

        // 页面事实：PI 菜单可达、列表可见、编辑页字段与明细与报价单一致
        OpenModule("proforma-invoice");
        Assert.Contains("形式发票 PI", _fx.Driver.FindElement(By.Id("header-title")).Text);
        SearchList(pi.No);
        var rowText = RowText(pi.No);
        Assert.Contains(tag, rowText);
        Assert.Contains("750", rowText);                 // PI 总额
        Assert.Contains("225", rowText);                 // 定金金额
        CaptureEvidence("pi-list-with-generated-pi");

        OpenEditForm(pi.No);
        var form = ReadPiFormValues();
        Assert.Equal(pi.No, form.PiNo);
        Assert.Equal(quotation.No, form.QuotationNo);
        Assert.Equal(tag, form.CustomerName);
        Assert.Equal("USD", form.Currency);              // 币种随报价单复制（枚举名口径）
        Assert.Equal("FOB", form.TradeTerms);
        Assert.Equal("NINGBO", form.PortOfLoading);
        Assert.Equal("HAMBURG", form.PortOfDestination);
        Assert.Equal("T/T 30% deposit", form.PaymentTerms);
        Assert.Equal(30m, decimal.Parse(form.DepositRatio, CultureInfo.InvariantCulture));
        Assert.Equal(225m, decimal.Parse(form.DepositAmount, CultureInfo.InvariantCulture));   // 750 × 30%
        Assert.Equal(750m, decimal.Parse(form.DetailTotal, CultureInfo.InvariantCulture));
        Assert.Equal(2, form.DetailRows);
        Assert.Contains("P-UI-1", form.DetailText);
        Assert.Contains("250", form.DetailText);         // 100 × 2.5 = 250
        Assert.Contains("500", form.DetailText);         // 40 × 12.5 = 500
        CaptureEvidence("pi-edit-form-with-copied-lines");
        ClosePanel();

        Assert.Empty(ApiIssues());
    }

    // ==================== 场景 3 + 4：草稿修改 / 审核 / 销审 / 打印预览 / 作废 ====================

    [Fact]
    public void PI草稿修改_审核后禁改_销审恢复_并渲染打印预览()
    {
        var tag = "UIPI" + DateTime.Now.ToString("HHmmss");
        LoginAsAdmin();
        InstallApiIssueRecorder();

        var pi = CreatePiDirectly(tag);
        Assert.Equal("Pending", pi.Status);
        Assert.Equal(750m, pi.TotalAmount);
        Assert.Equal(225m, pi.DepositAmount);

        OpenModule("proforma-invoice");
        SearchList(pi.No);
        Assert.True(FunctionExists("previewSalesDocPrint"),
            "页面未加载 /js/sales-pi.js（前端脚本过期）：请先重新构建并启动 ERP.Api 再运行浏览器验收");
        CaptureEvidence("pi-list-draft");

        // 1) 草稿可修改：改唛头 + 定金比例 40% 后保存
        OpenEditForm(pi.No);
        SetFieldValue("f_shippingMarks", "UI-MARKS-" + tag);
        SetFieldValue("f_depositRatio", "40");
        ClearToast();
        SavePanel();
        WaitToastContains("保存成功");
        ClosePanel();

        var edited = GetPi(pi.Id);
        Assert.Equal("UI-MARKS-" + tag, edited.ShippingMarks);
        Assert.Equal(40m, edited.DepositRatio);
        Assert.Equal(300m, edited.DepositAmount);        // 750 × 40% 由后端重算
        CaptureEvidence("pi-edited-draft-saved");

        // 2) 审核：草稿 → 已审核
        ClickRowMenuAction(pi.Id, "piApprove");
        WaitToastContains("PI 已审核");
        Assert.Equal("Approved", GetPi(pi.Id).Status);

        // 3) 审核后不可修改：保存被业务规则拒绝，且数据未被改动
        OpenEditForm(pi.No);
        SetFieldValue("f_shippingMarks", "SHOULD-NOT-SAVE-" + tag);
        ClearToast();
        SavePanel();
        WaitToastContains("不可修改");
        ClosePanel();
        Assert.Equal("UI-MARKS-" + tag, GetPi(pi.Id).ShippingMarks);
        CaptureEvidence("pi-approved-edit-rejected");

        // 4) 销审后可继续修改
        ClickRowMenuAction(pi.Id, "piUnaudit");
        WaitToastContains("已销审");
        Assert.Equal("Pending", GetPi(pi.Id).Status);

        OpenEditForm(pi.No);
        SetFieldValue("f_shippingMarks", "UI-MARKS2-" + tag);
        ClearToast();
        SavePanel();
        WaitToastContains("保存成功");
        ClosePanel();
        Assert.Equal("UI-MARKS2-" + tag, GetPi(pi.Id).ShippingMarks);
        CaptureEvidence("pi-unaudited-edit-restored");

        // 5) 打印预览：单据渲染（主表 + 明细 + 定金），无错误提示
        ClickRowMenuAction(pi.Id, "previewSalesDocPrint");
        WaitModalContains("打印预览");
        var preview = ModalPrintText();
        Assert.Contains(pi.No, preview);
        Assert.Contains(tag, preview);
        Assert.Contains("定金", preview);
        Assert.Contains("P-UI-1", preview);
        Assert.True(HasPrintPage(), "打印预览应渲染 .print-page 单据版式");
        Assert.DoesNotContain("error", ToastClass());
        CaptureEvidence("pi-print-preview");
        CloseModal();

        // 6) 作废：草稿 PI 可作废，重复作废给出明确提示
        var voidPi = CreatePiDirectly(tag + "V");
        OpenModule("proforma-invoice");
        SearchList(voidPi.No);
        ClickRowMenuAction(voidPi.Id, "piVoid");
        AcceptAlert();
        WaitToastContains("PI 已作废");
        Assert.Equal("Cancelled", GetPi(voidPi.Id).Status);
        CaptureEvidence("pi-voided");

        // 全流程只允许出现 1 次业务拒绝（第 3 步「已审核不可修改」），不允许存在其它 API 失败
        var issues = ApiIssues();
        Assert.Single(issues);
        Assert.Contains("不可修改", issues[0]);
    }

    // ==================== 登录与接口失败记录 ====================

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

    // ==================== 测试数据（经接口创建，每次运行唯一标记） ====================

    private sealed record QuotationSeed(long Id, string No);

    private sealed record PiSeed(long Id, string No, long? QuotationId, string QuotationNo, string Status,
        decimal TotalAmount, decimal DepositRatio, decimal DepositAmount, string ShippingMarks);

    private QuotationSeed SeedQuotation(string tag)
    {
        var body = JsonSerializer.Serialize(new
        {
            quotationDate = DateTime.Today.ToString("yyyy-MM-dd"),
            validUntil = DateTime.Today.AddDays(30).ToString("yyyy-MM-dd"),
            customerName = tag,                       // 客户名即本次运行标记，便于关键字检索
            contactPerson = "Mr. UI",
            contactEmail = "ui-acceptance@example.com",
            tradeTerms = "FOB",
            portOfLoading = "NINGBO",
            portOfDestination = "HAMBURG",
            paymentTerms = "T/T 30% deposit",
            leadTime = "30 days after deposit",
            currency = 2,                             // USD
            exchangeRate = 7.2m,
            salesmanName = "自动化验收",
            remark = "PI_FLOW_UI",
            details = new object[]
            {
                new { productCode = "P-UI-1", productName = "UI 商品 A", spec = "大", unit = "PCS", quantity = 100m, unitPrice = 2.5m },
                new { productCode = "P-UI-2", productName = "UI 商品 B", spec = "中", unit = "PCS", quantity = 40m, unitPrice = 12.5m }
            }
        });
        var data = ApiData("POST", "/api/sales/quotations", body);
        return new QuotationSeed(data.GetProperty("id").GetInt64(), data.GetProperty("quotationNo").GetString()!);
    }

    private void ApproveQuotation(long id) => ApiData("POST", $"/api/sales/quotations/{id}/approve");

    private PiSeed CreatePiDirectly(string tag)
    {
        var body = JsonSerializer.Serialize(new
        {
            piDate = DateTime.Today.ToString("yyyy-MM-dd"),
            customerName = tag,
            contactPerson = "Mr. UI",
            tradeTerms = "FOB",
            portOfLoading = "NINGBO",
            portOfDestination = "HAMBURG",
            paymentTerms = "T/T 30% deposit",
            currency = 2,
            exchangeRate = 7.2m,
            depositRatio = 30m,
            salesmanName = "自动化验收",
            remark = "PI_FLOW_UI",
            details = new object[]
            {
                new { productCode = "P-UI-1", productName = "UI 商品 A", spec = "大", unit = "PCS", quantity = 100m, unitPrice = 2.5m },
                new { productCode = "P-UI-2", productName = "UI 商品 B", spec = "中", unit = "PCS", quantity = 40m, unitPrice = 12.5m }
            }
        });
        var created = ApiData("POST", "/api/sales/proforma-invoices", body);
        return GetPi(created.GetProperty("id").GetInt64());
    }

    private PiSeed GetPi(long id) => ReadPi(ApiData("GET", $"/api/sales/proforma-invoices/{id}"));

    private PiSeed FindPi(string tag)
    {
        var data = ApiData("GET", $"/api/sales/proforma-invoices?page=1&pageSize=5&keyword={Uri.EscapeDataString(tag)}");
        var items = data.GetProperty("items");
        Assert.True(items.GetArrayLength() > 0, $"未找到客户为 {tag} 的 PI");
        return ReadPi(items[0]);
    }

    private static PiSeed ReadPi(JsonElement e) => new(
        e.GetProperty("id").GetInt64(),
        e.GetProperty("piNo").GetString()!,
        e.TryGetProperty("quotationId", out var qid) && qid.ValueKind != JsonValueKind.Null ? qid.GetInt64() : null,
        e.TryGetProperty("quotationNo", out var qn) ? qn.GetString() ?? string.Empty : string.Empty,
        e.GetProperty("status").GetString() ?? string.Empty,
        e.GetProperty("totalAmount").GetDecimal(),
        e.GetProperty("depositRatio").GetDecimal(),
        e.GetProperty("depositAmount").GetDecimal(),
        e.TryGetProperty("shippingMarks", out var sm) ? sm.GetString() ?? string.Empty : string.Empty);

    // ==================== UI 交互助手（真实浏览器操作） ====================

    private WebDriverWait Wait(int seconds = 20) => new(_fx.Driver, TimeSpan.FromSeconds(seconds));

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
    /// <remarks>列表是异步重绘的：上一轮取到的行元素会在重绘后失效（stale），轮询里必须吞掉该异常继续等待，否则会随机失败。</remarks>
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

    /// <summary>
    /// 读取包含关键字的行文本。列表在「搜索 / 刷新」后是异步重绘的：关键字行尚未出现时必须继续轮询（返回 null），
    /// 否则 <c>Until</c> 会立即带着空串返回，后续断言因「读得太早」而随机失败。
    /// </summary>
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
    /// 点击列表中指定单据的「更多」→ 目标操作（按行内 onclick 精确匹配，避免误点其它单据）。
    /// </summary>
    /// <remarks>
    /// 列表是异步重绘的（搜索 / 刷新 / 操作后重新加载）：已取得的按钮可能在轮询途中失效（stale），
    /// 已展开的「更多」菜单也会随旧行一起被移除。因此这里不缓存任何元素引用：
    /// 每轮重新定位该行、必要时重新展开菜单，吞掉 stale 异常继续重试，超时才带诊断信息失败。
    /// 行的定位同时接受 openForm(id) 与 functionName(id)：菜单展开时菜单项被提升到 body 层，
    /// 此时只有行内的「编辑」按钮还能继续标识该行。
    /// </remarks>
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
                // 1) 菜单项已展开且可见（上一轮点击已生效）→ 直接使用
                var item = _fx.Driver.FindElements(By.XPath(itemXPath)).FirstOrDefault(b => b.Displayed);
                // 2) 尚未展开：重新定位该行的「更多」按钮并展开（行可能已被重绘）
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

    /// <summary>打开指定单据的编辑面板（Parallax Side Panel）</summary>
    private void OpenEditForm(string billNo)
    {
        var editButton = Wait().Until(d =>
        {
            try
            {
                return d.FindElements(By.CssSelector("#table-wrap tbody tr"))
                    .Where(tr => tr.Text.Contains(billNo))
                    .Select(tr => tr.FindElements(By.CssSelector("button"))
                        .FirstOrDefault(b => (b.GetDomAttribute("onclick") ?? string.Empty).StartsWith("openForm(", StringComparison.Ordinal)))
                    .FirstOrDefault(b => b != null);
            }
            catch (StaleElementReferenceException) { return null; }   // 列表异步重绘：本轮元素已失效，继续等待
        });
        Assert.NotNull(editButton);
        SafeClick(editButton!);
        Wait().Until(d => d.FindElement(By.Id("side-panel")).GetDomAttribute("class")!.Contains("active"));
        Wait().Until(d => d.FindElements(By.Id("f_piNo")).Count > 0);
        Wait().Until(d => d.FindElement(By.Id("f_piNo")).GetDomProperty("value")!.Length > 0);
        // 主子表：等待明细行渲染完成，保证后续读取明细与合计时数据已就位
        Wait().Until(d => d.FindElements(By.CssSelector("#detail-body tr")).Count > 0);
    }

    private void SetFieldValue(string elementId, string value) => ExecuteScript(
        $"const el = document.getElementById({JsonSerializer.Serialize(elementId)}); el.focus(); " +
        $"el.value = {JsonSerializer.Serialize(value)}; el.dispatchEvent(new Event('change', {{ bubbles: true }})); return 'ok';");

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

    private void CloseModal()
    {
        var close = _fx.Driver.FindElements(By.XPath("//div[@id='modal']//button[contains(normalize-space(.),'关闭')]")).FirstOrDefault();
        if (close != null) SafeClick(close);
        try { Wait(5).Until(d => !d.FindElement(By.Id("modal")).Displayed); }
        catch (WebDriverTimeoutException) { ExecuteScript("closeModal(); return 'ok';"); }
    }

    private void AcceptAlert(int seconds = 10)
    {
        // 轮询等待业务确认框（window.confirm）并确认；未出现确认框（如接口直接返回）时静默返回
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

    // ==================== 页面断言助手 ====================

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

    /// <summary>提示条运行时状态快照（文案 / 显示 / 样式类）。Selenium 的 Text 对隐藏元素返回空串，故必须看 textContent</summary>
    private string ToastSnapshot() => ExecuteScript(
        "const t = document.getElementById('toast');" +
        "return t ? JSON.stringify({ text: t.textContent, display: t.style.display, cls: t.className }) : 'no-toast-element';");

    private string ToastClass() => _fx.Driver.FindElement(By.Id("toast")).GetDomAttribute("class") ?? string.Empty;

    private void WaitModalContains(string text) => Wait().Until(d =>
        d.FindElement(By.Id("modal")).Displayed && d.FindElement(By.Id("modal")).Text.Contains(text));

    private string ModalPrintText() => ExecuteScript(
        "const m = document.getElementById('modal'); return m ? m.innerText.replace(/\\s+/g, ' ') : '';");

    private bool HasPrintPage() => ExecuteScript("return document.querySelector('#modal .print-page') !== null;") == "True";

    /// <summary>页面是否已定义指定全局函数（用于快速识别前端脚本未更新的情况）</summary>
    /// <remarks>
    /// 直接返回 JS 布尔值：Selenium 会把它还原成 .NET 的 bool（ToString() 为 "True"）。
    /// 切勿用 String(...) 包装后比较 "True" —— JS 的 String(true) 得到小写 "true"，条件将永远为假。
    /// </remarks>
    private bool FunctionExists(string functionName)
        => ExecuteScript($"return typeof window.{functionName} === 'function';") == "True";

    private void CaptureEvidence(string scenario) => _fx.CaptureEvidence(scenario);

    private sealed record PiFormValues(string PiNo, string QuotationNo, string CustomerName, string Currency,
        string TradeTerms, string PortOfLoading, string PortOfDestination, string PaymentTerms,
        string DepositRatio, string DepositAmount, string DetailTotal, string DetailText, int DetailRows);

    private PiFormValues ReadPiFormValues()
    {
        var json = ExecuteScript(@"
            const val = id => { const el = document.getElementById(id); return el ? String(el.value ?? '') : ''; };
            const total = document.getElementById('detail-total');
            const inputs = Array.from(document.querySelectorAll('#detail-body input, #detail-body select'))
                .map(e => String(e.value ?? '')).join(' | ');
            return JSON.stringify({
              piNo: val('f_piNo'), quotationNo: val('f_quotationNo'), customerName: val('f_customerName'),
              currency: val('f_currency'), tradeTerms: val('f_tradeTerms'), portOfLoading: val('f_portOfLoading'),
              portOfDestination: val('f_portOfDestination'), paymentTerms: val('f_paymentTerms'),
              depositRatio: val('f_depositRatio'), depositAmount: val('f_depositAmount'),
              detailTotal: total ? total.textContent.trim() : '', detailText: inputs,
              detailRows: document.querySelectorAll('#detail-body tr').length
            });");
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        string S(string key) => r.GetProperty(key).GetString() ?? string.Empty;
        return new PiFormValues(S("piNo"), S("quotationNo"), S("customerName"), S("currency"), S("tradeTerms"),
            S("portOfLoading"), S("portOfDestination"), S("paymentTerms"), S("depositRatio"), S("depositAmount"),
            S("detailTotal"), S("detailText"), r.GetProperty("detailRows").GetInt32());
    }
}
