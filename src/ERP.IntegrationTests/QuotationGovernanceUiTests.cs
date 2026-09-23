using OpenQA.Selenium;
using OpenQA.Selenium.Support.UI;
using System.Text.Json;
using Xunit;

namespace ERP.IntegrationTests;

/// <summary>
/// ERP-018 真实浏览器验收（Collection=UiTests，Microsoft Edge）：
/// 1) 报价单行操作「打印预览」→ 弹窗渲染单据版式（单据号 / 明细商品 / 合计），且「直接打印」「打印设计」入口可达；
/// 2) 打印设计 → 跳到样式设计并预选「报价单」（注册进共享 BILL_CONFIG 后归入「业务单据」分组）；
/// 3) 有效期治理 → 列表「有效期状态」徽标可见 + 工具栏「⏰ 有效期提醒」按紧急度列出待跟进报价单；
/// 4) 报表入口「📈 成交率报表」→ 渲染「报价成交率分析」并包含本次验收业务员的数据。
/// 说明：按 <c>completion_policy.defer_browser_during_development</c>，本文件在开发阶段记
/// <c>browser_deferred</c>，留待 <c>FINAL-UI-ACCEPTANCE</c> 阶段统一执行（真实 Edge + TRX + 截图 + SHA-256 清单）。
/// 测试数据全部经应用自身接口以登录态创建（不直连数据库、不使用生产数据、不执行 SQL）。
/// </summary>
[Collection("UiTests")]
[Trait("Collection", "UiTests")]
public class QuotationGovernanceUiTests
{
    private const string AdminUser = "admin";
    private const string AdminPassword = "Admin@123";

    private readonly UiTestFixture _fx;

    public QuotationGovernanceUiTests(UiTestFixture fx) => _fx = fx;

    // ==================== 场景 1：打印预览 + 直接打印 / 打印设计入口 ====================

    [Fact]
    public void 报价单打印_预览渲染单据版式_且打印三件套入口可达()
    {
        var tag = "UIQG" + DateTime.Now.ToString("HHmmss");
        LoginAsAdmin();
        InstallApiIssueRecorder();

        var quotation = SeedQuotation(tag, DateTime.Today.AddDays(30), "验收业务员" + tag);
        OpenModule("quotation");
        SearchList(quotation.No);

        Assert.True(FunctionExists("previewSalesDocPrint"),
            "页面未加载 /js/sales-pi.js（前端脚本过期）：请先重新构建并启动 ERP.Api 再运行浏览器验收");
        Assert.True(FunctionExists("printSalesDocDirect"), "缺少 ERP-018 的「直接打印」入口函数 printSalesDocDirect");
        Assert.True(FunctionExists("designSalesDocPrint"), "缺少 ERP-018 的「打印设计」入口函数 designSalesDocPrint");
        CaptureEvidence("quotation-list-with-print-actions");

        // 打印预览：弹窗渲染单据版式（单据号 / 明细商品 / 合计）
        ClickRowMenuAction(quotation.Id, "previewSalesDocPrint");
        WaitModalContains("打印预览");
        Assert.True(HasPrintPage(), "打印预览应渲染 .print-page 单据版式");
        var preview = ModalText();
        Assert.Contains(quotation.No, preview);
        Assert.Contains("UI 商品 A", preview);
        Assert.Contains("合计", preview);
        CaptureEvidence("quotation-print-preview");
        CloseModal();

        // 打印设计：跳到样式设计并预选「报价单」
        ClickRowMenuAction(quotation.Id, "designSalesDocPrint");
        Wait().Until(d => d.FindElement(By.Id("pc-doc-name")).Text.Contains("报价单"));
        Wait().Until(d => d.FindElements(By.CssSelector("#pc-doc-list .pd-doc-item")).Count > 0);
        var docList = ExecuteScript("const el = document.getElementById('pc-doc-list'); return el ? el.textContent : '';");
        Assert.Contains("报价单", docList);
        CaptureEvidence("quotation-print-design");

        AssertEmptyApiIssues();
    }

    // ==================== 场景 2：有效期治理（列表徽标 + 提醒弹窗） ====================

    [Fact]
    public void 报价单有效期治理_列表徽标与提醒弹窗可见()
    {
        var tag = "UIQV" + DateTime.Now.ToString("HHmmss");
        LoginAsAdmin();
        InstallApiIssueRecorder();

        var expired = SeedQuotation(tag + "EXP", DateTime.Today.AddDays(-3), "验收业务员" + tag);
        var soon = SeedQuotation(tag + "SOON", DateTime.Today.AddDays(3), "验收业务员" + tag);
        OpenModule("quotation");
        SearchList(tag);

        Assert.True(FunctionExists("quotationValidityBadge"), "缺少 ERP-018 的有效期徽标渲染函数 quotationValidityBadge");
        Assert.True(FunctionExists("openQuotationValidityReminder"), "缺少 ERP-018 的「有效期提醒」入口函数");
        CaptureEvidence("quotation-list-validity-column");

        // 列表「有效期状态」列：已过期 / 即将到期 徽标按行渲染
        Assert.Contains("已过期", RowText(expired.No));
        Assert.Contains("即将到期", RowText(soon.No));
        CaptureEvidence("quotation-list-validity-badge");

        // 工具栏「⏰ 有效期提醒」：按紧急度列出待跟进报价单（已过期的排最前）
        ClickToolbarExtraAction("openQuotationValidityReminder");
        WaitModalContains("报价有效期提醒");
        var modal = ModalText();
        Assert.Contains(expired.No, modal);
        Assert.Contains(soon.No, modal);
        Assert.Contains("已过期", modal);
        CaptureEvidence("quotation-validity-reminder-modal");
        CloseModal();

        AssertEmptyApiIssues();
    }

    // ==================== 场景 3：报价成交率报表 ====================

    [Fact]
    public void 报价成交率报表_从报价单页进入并渲染数据()
    {
        var tag = "UIQC" + DateTime.Now.ToString("HHmmss");
        var salesman = "验收业务员" + tag;
        LoginAsAdmin();
        InstallApiIssueRecorder();

        SeedQuotation(tag, DateTime.Today.AddDays(30), salesman);
        OpenModule("quotation");
        Assert.True(FunctionExists("openQuotationConversionReport"), "缺少 ERP-018 的「成交率报表」入口函数");
        CaptureEvidence("quotation-list-report-entry");

        ClickToolbarExtraAction("openQuotationConversionReport");
        Wait().Until(d => d.FindElement(By.Id("header-title")).Text.Contains("报价成交率分析"));
        Wait().Until(d => d.FindElements(By.CssSelector("#report-table tbody tr")).Count > 0);
        var table = ExecuteScript("const el = document.getElementById('report-table'); return el ? el.textContent : '';");
        Assert.Contains(salesman, table);
        Assert.Contains("成交率", ExecuteScript("return document.body.textContent || '';"));
        CaptureEvidence("quotation-conversion-report");

        AssertEmptyApiIssues();
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

    private void AssertEmptyApiIssues()
    {
        var issues = ApiIssues();
        Assert.True(issues.Length == 0, "验收过程中不应出现失败请求：" + string.Join(" | ", issues));
    }

    private string ExecuteScript(string script)
        => (string)((IJavaScriptExecutor)_fx.Driver).ExecuteScript(script);

    private WebDriverWait Wait(int seconds = 15) => new(_fx.Driver, TimeSpan.FromSeconds(seconds));

    private void SafeClick(IWebElement element)
    {
        try { element.Click(); }
        catch (WebDriverException) { ((IJavaScriptExecutor)_fx.Driver).ExecuteScript("arguments[0].click();", element); }
    }

    private void CaptureEvidence(string scenario) => _fx.CaptureEvidence(scenario);

    // ==================== 接口助手（以当前登录态调用应用自身 API） ====================

    private string Api(string method, string path, string? bodyJson = null)
    {
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

    private sealed record QuotationSeed(long Id, string No);

    /// <summary>经应用接口种一张报价单（客户名 = 检索标记，明细 100 × 2.5 共 250）</summary>
    private QuotationSeed SeedQuotation(string tag, DateTime validUntil, string salesmanName)
    {
        var body = JsonSerializer.Serialize(new
        {
            quotationDate = DateTime.Today.ToString("yyyy-MM-dd"),
            validUntil = validUntil.ToString("yyyy-MM-dd"),
            customerName = tag,
            contactPerson = "Mr. UI",
            contactEmail = "ui-acceptance@example.com",
            tradeTerms = "FOB",
            portOfLoading = "NINGBO",
            portOfDestination = "HAMBURG",
            paymentTerms = "T/T 30% deposit",
            leadTime = "30 days after deposit",
            currency = 2,                             // USD
            exchangeRate = 7.2m,
            salesmanName,
            remark = "QUOTATION_GOVERNANCE_UI",
            details = new object[]
            {
                new { productCode = "P-UI-1", productName = "UI 商品 A", spec = "大", unit = "PCS", quantity = 100m, unitPrice = 2.5m }
            }
        });
        var data = ApiData("POST", "/api/sales/quotations", body);
        return new QuotationSeed(data.GetProperty("id").GetInt64(), data.GetProperty("quotationNo").GetString()!);
    }

    // ==================== 页面操作助手 ====================

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

    /// <summary>点击工具栏扩展按钮（extraActions 渲染的 onclick 无参按钮）</summary>
    private void ClickToolbarExtraAction(string functionName)
    {
        var button = Wait().Until(d =>
        {
            var items = d.FindElements(By.XPath($"//*[@id='content']//button[@onclick='{functionName}']"));
            return items.Count > 0 ? items[0] : null;
        });
        Assert.NotNull(button);
        SafeClick(button!);
    }

    /// <summary>
    /// 点击列表中指定单据的「更多」→ 目标操作（按行内 onclick 精确匹配）；
    /// 列表异步重绘导致元素失效时每轮重新定位、必要时重新展开菜单，超时才带诊断失败。
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
            $"接口失败记录 = " + (ApiIssues() is { Length: > 0 } issues ? string.Join(" | ", issues) : "无"));
    }

    private string ModalText() => ExecuteScript(
        "const m = document.getElementById('modal'); return m ? m.textContent : '';");

    private void WaitModalContains(string text) => Wait().Until(_ => ModalText().Contains(text));

    private bool HasPrintPage() => ExecuteScript("return document.querySelectorAll('.print-page').length > 0;") == "True";

    private void CloseModal() => ExecuteScript("closeModal(); return 'ok';");

    private void ClearToast() => ExecuteScript(
        "const t = document.getElementById('toast'); t.textContent = ''; t.style.display = 'none'; t.className = 'toast'; return 'ok';");

    /// <summary>页面是否已定义指定全局函数（用于快速识别前端脚本未更新的情况）</summary>
    private bool FunctionExists(string functionName)
        => ExecuteScript($"return typeof window.{functionName} === 'function';") == "True";
}
