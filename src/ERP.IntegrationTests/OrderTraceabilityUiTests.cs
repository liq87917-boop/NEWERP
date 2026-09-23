using OpenQA.Selenium;
using OpenQA.Selenium.Support.UI;
using System.Text.Json;
using Xunit;

namespace ERP.IntegrationTests;

/// <summary>
/// ERP-008 真实浏览器验收（Collection=UiTests，Microsoft Edge）：
/// 1) 「订单管理 → 销售订单」页面新建销售订单，填写外贸合同 / 运输 / 来源追溯字段与明细后保存，
///    重新打开核对回显，并从应用接口核对落库值；
/// 2) 「采购订单」页面新建采购订单，关联归属客户与归属销售订单，填写采购执行 / 结算字段后保存并重新打开核对；
/// 3) 订单列表与「销售订单导出」菜单渲染追溯列，Excel 导出接口可用，且全流程无 JS / 接口错误。
/// 说明：测试数据经应用自身接口以登录态创建（不直连数据库、不使用生产数据），
///       截图证据通过 UiTestFixture.CaptureEvidence 输出到 ERP_AI_EVIDENCE_DIR。
/// </summary>
[Collection("UiTests")]
[Trait("Collection", "UiTests")]
public class OrderTraceabilityUiTests
{
    private const string AdminUser = "admin";
    private const string AdminPassword = "Admin@123";
    private readonly UiTestFixture _fx;

    public OrderTraceabilityUiTests(UiTestFixture fx) => _fx = fx;

    // ==================== 场景 1：销售订单外贸合同与来源追溯 ====================

    [Fact]
    public void 销售订单_保存并重新打开_外贸合同与来源追溯字段回显一致()
    {
        var tag = "UISO" + DateTime.Now.ToString("HHmmss");
        LoginAsAdmin();
        InstallApiIssueRecorder();

        var customer = CreateCustomer(tag);
        var customerPo = "BUYERPO-" + tag;
        var contractNo = "SC-" + tag;

        OpenModule("sales-order");
        Assert.True(FunctionExists("previewSalesDocPrint"),
            "销售订单页面未加载 /js/sales-pi.js（前端脚本过期）：请先重新构建并启动 ERP.Api 再运行浏览器验收");
        ClickNewButton();
        WaitPanelFieldVisible("f_customerPoNo");

        SetRefField("customerId", customer.Id, tag);
        SetSelectValue("f_tradeTerms", "FOB");
        SetFieldValue("f_customerPoNo", customerPo);
        SetFieldValue("f_contractNo", contractNo);
        SetFieldValue("f_destinationPort", "HAMBURG");
        SetFieldValue("f_consignee", "ACME IMPORT GMBH");
        SetFieldValue("f_notifyParty", "ACME LOGISTICS");
        SetFieldValue("f_shippingMarks", "ACME\nHAMBURG\nC/NO.1-120");
        SetFieldValue("f_sourceQuotationId", "77");
        SetFieldValue("f_sourceQuotationNo", "QT-" + tag);
        SetFieldValue("f_sourcePiId", "88");
        SetFieldValue("f_sourcePiNo", "PI-" + tag);
        SetSelectValue("f_exportMode", "0110");
        SetSelectValue("f_businessNature", "自营出口");
        SetFieldValue("f_commissionRatio", "3.5");
        SetSelectValue("f_splitShipment", "true");
        SetFieldValue("f_inspectionRequirement", "客户验货 SGS-" + tag);
        SetFieldValue("f_packagingRequirement", "12 pcs/箱 彩盒");
        SetSelectValue("f_currency", "USD");
        SetFieldValue("f_exchangeRate", "7.2");
        SetFieldValue("f_depositRatio", "30");
        AddDetailRow(0, tag + "-商品A", "大", "PCS", "10", "100");

        ClearToast();
        SavePanel();
        WaitToastContains("保存成功");
        ClosePanel();

        var order = FindSalesOrder(customerPo);
        Assert.True(order.Id > 0, "销售订单应落库");
        Assert.StartsWith("SO", order.No);
        Assert.Equal(customerPo, order.CustomerPoNo);
        Assert.Equal(contractNo, order.ContractNo);
        Assert.Equal("FOB", order.TradeTerms);
        Assert.Equal("HAMBURG", order.DestinationPort);
        Assert.Equal("ACME IMPORT GMBH", order.Consignee);
        Assert.Equal("ACME LOGISTICS", order.NotifyParty);
        Assert.Contains("C/NO.1-120", order.ShippingMarks);
        Assert.Equal("QT-" + tag, order.SourceQuotationNo);
        Assert.Equal("PI-" + tag, order.SourcePiNo);
        Assert.Equal("0110", order.ExportMode);
        Assert.Equal("自营出口", order.BusinessNature);
        Assert.True(order.SplitShipment);
        Assert.Equal(3.5m, order.CommissionRatio);
        Assert.Equal(1, order.DetailCount);
        Assert.Equal(1000m, order.TotalAmount);
        CaptureEvidence("sales-order-saved-with-trade-fields");

        // 重新打开：页面回显必须与落库一致
        OpenModule("sales-order");
        SearchList(customerPo);
        OpenEditForm(customerPo, "f_contractNo", contractNo);
        Assert.Equal(contractNo, FormValue("f_contractNo"));
        Assert.Equal(customerPo, FormValue("f_customerPoNo"));
        Assert.Equal("HAMBURG", FormValue("f_destinationPort"));
        Assert.Equal("ACME IMPORT GMBH", FormValue("f_consignee"));
        Assert.Equal("QT-" + tag, FormValue("f_sourceQuotationNo"));
        Assert.Equal("PI-" + tag, FormValue("f_sourcePiNo"));
        Assert.Equal("0110", FormValue("f_exportMode"));
        Assert.Equal("自营出口", FormValue("f_businessNature"));
        Assert.Equal("true", FormValue("f_splitShipment"));
        Assert.Equal("3.5", FormValue("f_commissionRatio"));
        Assert.Contains("SGS-" + tag, FormValue("f_inspectionRequirement"));
        Assert.Equal(tag + "-商品A", FormValue("d_0_productName"));
        CaptureEvidence("sales-order-reopened-trade-fields");
        ClosePanel();

        // 打印预览：新增字段必须出现在单据上
        OpenModule("sales-order");
        SearchList(customerPo);
        ClickRowMenuAction(order.Id, "previewSalesDocPrint");
        WaitModalContains("打印预览");
        var preview = ModalPrintText();
        Assert.Contains(contractNo, preview);
        Assert.Contains(customerPo, preview);
        Assert.Contains("唛头", preview);
        Assert.True(HasPrintPage(), "打印预览应渲染 .print-page 单据版式");
        CaptureEvidence("sales-order-print-preview");
        CloseModal();

        // Excel 导出（含新增列）必须可用
        var export = ExportExcelViaPage("/api/sales-orders/export-excel", customerPo);
        Assert.Equal(200, export.Status);
        Assert.Contains("spreadsheetml", export.ContentType);
        Assert.True(export.Size > 1000, "导出文件不应为空");
        CaptureEvidence("sales-order-excel-export");

        // 清理测试单据与测试客户（软删除，仅待提交单据可删）
        ApiData("DELETE", $"/api/sales-orders/{order.Id}");
        ApiData("DELETE", $"/api/base/customers/{customer.Id}");
        Assert.Empty(ApiIssues());
    }

    // ==================== 场景 2：采购订单归属与采购执行 ====================

    [Fact]
    public void 采购订单_保存并重新打开_归属客户与采购执行字段回显一致()
    {
        var tag = "UIPO" + DateTime.Now.ToString("HHmmss");
        LoginAsAdmin();
        InstallApiIssueRecorder();

        var customer = CreateCustomer(tag);
        var supplier = CreateSupplier(tag);
        var salesOrder = CreateSalesOrderViaApi(tag, customer.Id);
        var contractNo = "PC-" + tag;

        OpenModule("purchase-order");
        ClickNewButton();
        WaitPanelFieldVisible("f_owningSalesOrderNo");

        SetRefField("supplierId", supplier.Id, tag);
        SetRefField("owningCustomerId", customer.Id, tag);
        SetFieldValue("f_owningCustomerName", tag);
        SetFieldValue("f_owningSalesOrderId", salesOrder.Id.ToString());
        SetFieldValue("f_owningSalesOrderNo", salesOrder.No);
        SetFieldValue("f_contractNo", contractNo);
        SetSelectValue("f_advanceOnBehalf", "true");
        SetSelectValue("f_taxIncluded", "true");
        SetFieldValue("f_taxRate", "13");
        SetFieldValue("f_supplierConfirmedDate", DateTime.Today.AddDays(10).ToString("yyyy-MM-dd"));
        SetSelectValue("f_arrivalProgress", "部分到货");
        SetSelectValue("f_qcStatus", "合格");
        SetSelectValue("f_settlementProgress", "未结算");
        SetFieldValue("f_paymentTerms", "月结 30 天");
        SetFieldValue("f_deliveryDate", DateTime.Today.AddDays(20).ToString("yyyy-MM-dd"));
        AddDetailRow(0, tag + "-采购商品A", "中", "PCS", "20", "50");

        ClearToast();
        SavePanel();
        WaitToastContains("保存成功");
        ClosePanel();

        var order = FindPurchaseOrder(contractNo);
        Assert.True(order.Id > 0, "采购订单应落库");
        Assert.StartsWith("PO", order.No);
        Assert.Equal(contractNo, order.ContractNo);
        Assert.Equal(salesOrder.Id, order.OwningSalesOrderId);
        Assert.Equal(salesOrder.No, order.OwningSalesOrderNo);
        Assert.Equal(tag, order.OwningCustomerName);
        Assert.True(order.AdvanceOnBehalf);
        Assert.True(order.TaxIncluded);
        Assert.Equal(13m, order.TaxRate);
        Assert.Equal("部分到货", order.ArrivalProgress);
        Assert.Equal("合格", order.QcStatus);
        Assert.Equal("未结算", order.SettlementProgress);
        Assert.Equal(1000m, order.TotalAmount);
        CaptureEvidence("purchase-order-saved-with-execution-fields");

        // 重新打开：页面回显必须与落库一致
        OpenModule("purchase-order");
        SearchList(contractNo);
        OpenEditForm(contractNo, "f_contractNo", contractNo);
        Assert.Equal(contractNo, FormValue("f_contractNo"));
        Assert.Equal(salesOrder.No, FormValue("f_owningSalesOrderNo"));
        Assert.Equal(tag, FormValue("f_owningCustomerName"));
        Assert.Equal("true", FormValue("f_advanceOnBehalf"));
        Assert.Equal("true", FormValue("f_taxIncluded"));
        Assert.Equal("13", FormValue("f_taxRate"));
        Assert.Equal("部分到货", FormValue("f_arrivalProgress"));
        Assert.Equal("合格", FormValue("f_qcStatus"));
        Assert.Equal("未结算", FormValue("f_settlementProgress"));
        Assert.Equal(tag + "-采购商品A", FormValue("d_0_productName"));
        CaptureEvidence("purchase-order-reopened-execution-fields");
        ClosePanel();

        // 打印预览：归属客户 / 执行进度必须出现在单据上
        OpenModule("purchase-order");
        SearchList(contractNo);
        ClickRowMenuAction(order.Id, "previewSalesDocPrint");
        WaitModalContains("打印预览");
        var preview = ModalPrintText();
        Assert.Contains(contractNo, preview);
        Assert.Contains(salesOrder.No, preview);
        Assert.True(HasPrintPage(), "打印预览应渲染 .print-page 单据版式");
        CaptureEvidence("purchase-order-print-preview");
        CloseModal();

        // 清理测试单据（软删除，仅待提交单据可删）
        ApiData("DELETE", $"/api/purchase-orders/{order.Id}");
        ApiData("DELETE", $"/api/sales-orders/{salesOrder.Id}");
        ApiData("DELETE", $"/api/base/customers/{customer.Id}");
        ApiData("DELETE", $"/api/base/suppliers/{supplier.Id}");
        Assert.Empty(ApiIssues());
    }

    // ==================== 场景 3：列表追溯列、导出菜单与接口可用性 ====================

    [Fact]
    public void 订单列表与导出菜单_渲染追溯列且导出接口可用()
    {
        var tag = "UILIST" + DateTime.Now.ToString("HHmmss");
        LoginAsAdmin();
        InstallApiIssueRecorder();

        var customer = CreateCustomer(tag);
        var supplier = CreateSupplier(tag);
        var salesOrder = CreateSalesOrderViaApi(tag, customer.Id);
        var purchaseOrder = CreatePurchaseOrderViaApi(tag, supplier.Id, salesOrder.Id, salesOrder.No);

        OpenModule("sales-order");
        var salesHeaders = TableHeaders();
        Assert.Contains("客户 PO 号", salesHeaders);
        Assert.Contains("合同号", salesHeaders);
        CaptureEvidence("sales-order-list-trace-columns");

        OpenModule("purchase-order");
        var purchaseHeaders = TableHeaders();
        Assert.Contains("归属客户", purchaseHeaders);
        Assert.Contains("到货进度", purchaseHeaders);
        CaptureEvidence("purchase-order-list-trace-columns");

        // 导出菜单：已切换为订单（EF）状态口径，导出接口可用（列含新增字段）
        OpenExportMenu("sales-order-export");
        Assert.Contains("待提交", SelectOptionTexts("ex-status"));
        var export = ExportExcelViaPage("/api/sales-orders/export-excel", tag);
        Assert.Equal(200, export.Status);
        Assert.True(export.Size > 1000, "导出文件不应为空");
        CaptureEvidence("sales-order-export-menu");

        ApiData("DELETE", $"/api/purchase-orders/{purchaseOrder.Id}");
        ApiData("DELETE", $"/api/sales-orders/{salesOrder.Id}");
        ApiData("DELETE", $"/api/base/customers/{customer.Id}");
        ApiData("DELETE", $"/api/base/suppliers/{supplier.Id}");
        Assert.Empty(ApiIssues());
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

    private sealed record PartySeed(long Id, string Code);

    private sealed record SalesOrderSeed(long Id, string No, string CustomerPoNo, string ContractNo, string TradeTerms,
        string DestinationPort, string Consignee, string NotifyParty, string ShippingMarks,
        string SourceQuotationNo, string SourcePiNo, string ExportMode, string BusinessNature,
        bool SplitShipment, decimal CommissionRatio, decimal TotalAmount, int DetailCount);

    private sealed record PurchaseOrderSeed(long Id, string No, string ContractNo, long? OwningSalesOrderId,
        string OwningSalesOrderNo, string OwningCustomerName, bool AdvanceOnBehalf, bool TaxIncluded, decimal TaxRate,
        string ArrivalProgress, string QcStatus, string SettlementProgress, decimal TotalAmount, int DetailCount);

    private PartySeed CreateCustomer(string tag)
    {
        var body = JsonSerializer.Serialize(new { customerCode = "C-" + tag, customerName = tag, currency = "USD" });
        var data = ApiData("POST", "/api/base/customers", body);
        return new PartySeed(data.GetProperty("id").GetInt64(), data.GetProperty("customerCode").GetString()!);
    }

    private PartySeed CreateSupplier(string tag)
    {
        var body = JsonSerializer.Serialize(new { supplierCode = "S-" + tag, supplierName = tag });
        var data = ApiData("POST", "/api/base/suppliers", body);
        return new PartySeed(data.GetProperty("id").GetInt64(), data.GetProperty("supplierCode").GetString()!);
    }

    private SalesOrderSeed CreateSalesOrderViaApi(string tag, long customerId)
    {
        var body = JsonSerializer.Serialize(new
        {
            orderDate = DateTime.Today.ToString("yyyy-MM-dd"),
            customerId,
            currency = "USD",
            exchangeRate = 7.2m,
            customerPoNo = "PO-API-" + tag,
            contractNo = "SC-API-" + tag,
            remark = "ORDER_TRACE_UI",
            details = new object[]
            {
                new { productId = 1, productName = "API 商品", spec = "大", unit = "PCS", quantity = 5m, unitPrice = 20m }
            }
        });
        var data = ApiData("POST", "/api/sales-orders", body);
        return new SalesOrderSeed(data.GetProperty("id").GetInt64(), data.GetProperty("orderNo").GetString()!,
            string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty,
            string.Empty, string.Empty, string.Empty, string.Empty, false, 0m, 0m, 0);
    }

    private PurchaseOrderSeed CreatePurchaseOrderViaApi(string tag, long supplierId, long owningSalesOrderId,
        string owningSalesOrderNo)
    {
        var body = JsonSerializer.Serialize(new
        {
            orderDate = DateTime.Today.ToString("yyyy-MM-dd"),
            supplierId,
            currency = "CNY",
            exchangeRate = 1m,
            contractNo = "PC-API-" + tag,
            owningCustomerName = tag,
            owningSalesOrderId,
            owningSalesOrderNo,
            remark = "ORDER_TRACE_UI",
            details = new object[]
            {
                new { productId = 1, productName = "API 采购商品", spec = "中", unit = "PCS", quantity = 5m, unitPrice = 20m }
            }
        });
        var data = ApiData("POST", "/api/purchase-orders", body);
        return new PurchaseOrderSeed(data.GetProperty("id").GetInt64(), data.GetProperty("orderNo").GetString()!,
            string.Empty, null, string.Empty, string.Empty, false, false, 0m, string.Empty, string.Empty, string.Empty, 0m, 0);
    }

    /// <summary>按关键字（客户 PO 号 / 合同号 / 订单号）检索销售订单并读取详情</summary>
    private SalesOrderSeed FindSalesOrder(string keyword)
    {
        var row = FirstItem($"/api/sales-orders?page=1&pageSize=10&keyword={Uri.EscapeDataString(keyword)}",
            $"未找到关键字为 {keyword} 的销售订单");
        var id = row.GetProperty("id").GetInt64();
        var detail = ApiData("GET", $"/api/sales-orders/{id}");
        return new SalesOrderSeed(id,
            Str(row, "orderNo"), Str(detail, "customerPoNo"), Str(detail, "contractNo"), Str(detail, "tradeTerms"),
            Str(detail, "destinationPort"), Str(detail, "consignee"), Str(detail, "notifyParty"),
            Str(detail, "shippingMarks"), Str(detail, "sourceQuotationNo"), Str(detail, "sourcePiNo"),
            Str(detail, "exportMode"), Str(detail, "businessNature"),
            Bool(detail, "splitShipment"), Dec(detail, "commissionRatio"), Dec(detail, "totalAmount"),
            DetailCount(detail));
    }

    /// <summary>按关键字（合同号 / 采购单号 / 归属销售订单号）检索采购订单并读取详情</summary>
    private PurchaseOrderSeed FindPurchaseOrder(string keyword)
    {
        var row = FirstItem($"/api/purchase-orders?page=1&pageSize=10&keyword={Uri.EscapeDataString(keyword)}",
            $"未找到关键字为 {keyword} 的采购订单");
        var id = row.GetProperty("id").GetInt64();
        var detail = ApiData("GET", $"/api/purchase-orders/{id}");
        return new PurchaseOrderSeed(id,
            Str(row, "orderNo"), Str(detail, "contractNo"),
            detail.TryGetProperty("owningSalesOrderId", out var osid) && osid.ValueKind != JsonValueKind.Null ? osid.GetInt64() : null,
            Str(detail, "owningSalesOrderNo"), Str(detail, "owningCustomerName"),
            Bool(detail, "advanceOnBehalf"), Bool(detail, "taxIncluded"), Dec(detail, "taxRate"),
            Str(detail, "arrivalProgress"), Str(detail, "qcStatus"), Str(detail, "settlementProgress"),
            Dec(detail, "totalAmount"), DetailCount(detail));
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

    private static bool Bool(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static decimal Dec(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDecimal() : 0m;

    private static int DetailCount(JsonElement e)
        => e.TryGetProperty("details", out var v) && v.ValueKind == JsonValueKind.Array ? v.GetArrayLength() : 0;

    // ==================== UI 交互助手（真实浏览器操作） ====================

    private WebDriverWait Wait(int seconds = 20) => new(_fx.Driver, TimeSpan.FromSeconds(seconds));

    /// <summary>截图证据（输出到 ERP_AI_EVIDENCE_DIR，由验收脚本汇总）</summary>
    private void CaptureEvidence(string scenario) => _fx.CaptureEvidence(scenario);

    /// <summary>点击侧边栏菜单（自动展开所属一级分组），等待该模块页面渲染完成（不要求列表非空）</summary>
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
        // 页面确实切到目标模块（MODULES 版页面会设置 CURRENT_MODULE_CODE）
        Wait().Until(_ => ExecuteScript("return String(typeof CURRENT_MODULE_CODE !== 'undefined' ? CURRENT_MODULE_CODE : '');") == menuCode);
    }

    /// <summary>打开导出菜单页（renderBillExport 渲染，不触发下载）</summary>
    private void OpenExportMenu(string menuCode)
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
        Wait().Until(d => d.FindElements(By.Id("ex-status")).Count > 0);
    }

    /// <summary>下拉框选项文案（用于验证导出菜单的订单状态口径）</summary>
    private string[] SelectOptionTexts(string selectId)
    {
        var json = ExecuteScript(
            $"const el = document.getElementById({JsonSerializer.Serialize(selectId)});" +
            " return el ? JSON.stringify(Array.from(el.options).map(o => o.textContent)) : '[]';");
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.EnumerateArray().Select(e => e.GetString() ?? string.Empty).ToArray();
    }

    /// <summary>列表表头文案（用于验证新的追溯列已渲染）</summary>
    private string[] TableHeaders() => Wait().Until(d =>
    {
        try
        {
            var ths = d.FindElements(By.CssSelector("#table-wrap thead th")).Select(t => t.Text).ToArray();
            return ths.Length > 0 ? ths : null;
        }
        catch (StaleElementReferenceException) { return null; }
    }) ?? Array.Empty<string>();

    private bool FunctionExists(string functionName)
        => ExecuteScript($"return String(typeof {functionName} === 'function');") == "True";

    private void ClickNewButton()
    {
        var button = Wait().Until(d =>
            d.FindElements(By.CssSelector("#content .toolbar-actions button"))
                .FirstOrDefault(b => (b.GetDomAttribute("onclick") ?? string.Empty).StartsWith("openForm(", StringComparison.Ordinal)));
        Assert.NotNull(button);
        SafeClick(button!);
        Wait().Until(d => d.FindElement(By.Id("side-panel")).GetDomAttribute("class")!.Contains("active"));
    }

    /// <summary>等待面板字段渲染完成（只要求存在：面板有滑入动画，避免误判可见性）</summary>
    private void WaitPanelFieldVisible(string elementId)
        => Wait().Until(d => d.FindElements(By.Id(elementId)).Count > 0);

    private void SetFieldValue(string elementId, string value) => ExecuteScript(
        $"const el = document.getElementById({JsonSerializer.Serialize(elementId)}); el.focus(); " +
        $"el.value = {JsonSerializer.Serialize(value)}; el.dispatchEvent(new Event('change', {{ bubbles: true }})); return 'ok';");

    /// <summary>下拉选择：仅在选项存在时设置（避免写出非法值）</summary>
    private void SetSelectValue(string elementId, string value)
    {
        var exists = ExecuteScript(
            $"const el = document.getElementById({JsonSerializer.Serialize(elementId)});" +
            $"return String(!!el && Array.from(el.options).some(o => o.value === {JsonSerializer.Serialize(value)}));");
        Assert.Equal("True", exists);
        SetFieldValue(elementId, value);
    }

    /// <summary>引用字段（客户 / 供应商）：直接给隐藏主键赋值，并回填显示文本</summary>
    private void SetRefField(string key, long id, string displayText) => ExecuteScript(
        $"const hid = document.getElementById('f_{key}'); if (hid) hid.value = '{id}'; " +
        $"const s = document.getElementById('f_{key}_search'); if (s) s.value = {JsonSerializer.Serialize(displayText)}; return 'ok';");

    private void AddDetailRow(int index, string productName, string spec, string unit, string qty, string price)
    {
        var addButton = Wait().Until(d =>
            d.FindElements(By.XPath("//button[starts-with(@onclick,'detailAddRow()')]")).FirstOrDefault());
        Assert.NotNull(addButton);
        SafeClick(addButton!);
        Wait().Until(d => d.FindElements(By.Id($"d_{index}_productName")).Count > 0);
        SetDetailCell(index, "productId", "1");
        SetDetailCell(index, "productName", productName);
        SetDetailCell(index, "spec", spec);
        SetDetailCell(index, "unit", unit);
        SetDetailCell(index, "quantity", qty);
        SetDetailCell(index, "unitPrice", price);
    }

    /// <summary>明细单元格：同步 DOM 值与该行数据模型（金额 = 数量 × 单价 由 detailSet 重算）</summary>
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

    private void CloseModal()
    {
        var close = _fx.Driver.FindElements(By.XPath("//div[@id='modal']//button[contains(normalize-space(.),'关闭')]")).FirstOrDefault();
        if (close != null) SafeClick(close);
        try { Wait(5).Until(d => !d.FindElement(By.Id("modal")).Displayed); }
        catch (WebDriverTimeoutException) { ExecuteScript("closeModal(); return 'ok';"); }
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
    /// <remarks>列表是异步重绘的：上一轮取到的行元素会在重绘后失效（stale），轮询里必须吞掉该异常继续等待。</remarks>
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

    /// <summary>打开指定单据的编辑面板，并等待回填完成（以某个字段达到期望值作为加载完成信号）</summary>
    private void OpenEditForm(string billNo, string waitFieldId, string expectedValue)
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
        Wait().Until(_ => FormValue(waitFieldId) == expectedValue);
    }

    /// <summary>
    /// 点击列表中指定单据的「更多」→ 目标行操作（按行内 onclick 精确匹配，避免误点其它单据）。
    /// 列表异步重绘会让已取得的元素失效（stale），因此每轮重新定位并吞掉 stale 异常继续重试。
    /// </summary>
    private void ClickRowMenuAction(long id, string functionName)
    {
        var itemXPath = $"//button[@onclick='{functionName}({id})']";
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
            catch (StaleElementReferenceException) { /* 列表异步重绘：重新定位后重试 */ }
            catch (WebDriverException ex) { lastError = ex.Message; }
            Thread.Sleep(200);
        }
        throw new WebDriverTimeoutException($"未能在 20 秒内点击行操作 {functionName}({id})；最后错误：{lastError}");
    }

    // ==================== 页面断言助手 ====================

    private string FormValue(string elementId) => ExecuteScript(
        $"const el = document.getElementById({JsonSerializer.Serialize(elementId)}); return el ? String(el.value ?? '') : '';");

    private string ModalPrintText() => ExecuteScript("const m = document.getElementById('modal'); return m ? m.innerText : '';");

    private bool HasPrintPage()
        => ExecuteScript("return String(document.querySelectorAll('#modal .print-page').length > 0);") == "True";

    private void WaitModalContains(string text) => Wait().Until(_ => ModalPrintText().Contains(text));

    private sealed record ExportResult(int Status, string ContentType, int Size);

    /// <summary>在页面内以登录态调用 Excel 导出接口，返回 HTTP 状态 / 类型 / 字节数（验证导出路径可用）</summary>
    private ExportResult ExportExcelViaPage(string apiPath, string? keyword)
    {
        _fx.Driver.Manage().Timeouts().AsynchronousJavaScript = TimeSpan.FromSeconds(30);
        var path = keyword is null ? apiPath : $"{apiPath}?keyword={Uri.EscapeDataString(keyword)}";
        var script = $$"""
            const done = arguments[arguments.length - 1];
            (async () => {
              try {
                const res = await fetch({{JsonSerializer.Serialize(path)}}, {
                  headers: { 'Authorization': 'Bearer ' + (localStorage.getItem('erp_token') || '') }
                });
                const buf = await res.arrayBuffer();
                done(JSON.stringify({ status: res.status, contentType: res.headers.get('content-type') || '', size: buf.byteLength }));
              } catch (e) { done(JSON.stringify({ status: -1, contentType: String(e), size: 0 })); }
            })();
            """;
        var json = (string)((IJavaScriptExecutor)_fx.Driver).ExecuteAsyncScript(script);
        using var doc = JsonDocument.Parse(json);
        return new ExportResult(doc.RootElement.GetProperty("status").GetInt32(),
            doc.RootElement.GetProperty("contentType").GetString() ?? string.Empty,
            doc.RootElement.GetProperty("size").GetInt32());
    }
}







