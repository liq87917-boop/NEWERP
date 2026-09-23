using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// 报价单 / 形式发票 PI → 销售订单（带入预填 + 直接生成，ERP-010）单元测试：
/// 两种来源的字段与明细映射、服务端金额复核、来源追溯留痕、重复生成守卫、非法来源状态、
/// 前端接线（行操作函数 + 接口路由）与报文契约。
/// </summary>
public class SalesOrderConversionTests
{
    // ==================== 报价单 → 销售订单（直接生成） ====================

    [Fact]
    public async Task 报价单转销售订单_已审核_字段与明细映射完整并留痕来源()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var quotation = SeedQuotation(db, "QT-ORDER-1", DocumentStatus.Approved, customer.Id);
        var ctl = NewQuotationController(db);

        var ok = Assert.IsType<OkObjectResult>(await ctl.ToSalesOrder(quotation.Id));
        var result = Assert.IsType<ApiResponse<SalesOrderConversionResult>>(ok.Value).Data!;
        Assert.True(result.Id > 0);
        Assert.StartsWith("SO", result.OrderNo);
        Assert.Equal("QT-ORDER-1", result.SourceNo);

        var order = db.SalesOrders.Single();
        Assert.Equal(DocumentStatus.Pending, order.Status);
        Assert.Equal(DateTime.Today, order.OrderDate);
        Assert.Equal(customer.Id, order.CustomerId);
        Assert.Equal(66L, order.SalesmanId);
        Assert.Equal(Currency.USD, order.Currency);
        Assert.Equal(7.2m, order.ExchangeRate);

        // 贸易 / 付款 / 目的港：来源报价单优先
        Assert.Equal("FOB", order.TradeTerms);
        Assert.Equal("HAMBURG", order.DestinationPort);
        Assert.Equal("T/T 30% deposit", order.PaymentTerms);
        // 收货人 / 通知人 / 唛头 / 定金 / 业务性质 / 佣金：客户档案默认值
        Assert.Equal("ACME IMPORT GMBH", order.Consignee);
        Assert.Equal("ACME LOGISTICS", order.NotifyParty);
        Assert.Contains("C/NO.1-120", order.ShippingMarks);
        Assert.Equal(40m, order.DepositRatio);
        Assert.Equal("自营出口", order.BusinessNature);
        Assert.Equal(3.5m, order.CommissionRatio);

        // 来源追溯（ERP-008 字段）
        Assert.Equal(quotation.Id, order.SourceQuotationId);
        Assert.Equal("QT-ORDER-1", order.SourceQuotationNo);
        Assert.Null(order.SourcePiId);
        Assert.Equal(string.Empty, order.SourcePiNo);

        // 交期没有对应列，合并进备注留存
        Assert.Equal("ORDER_CONVERSION_TEST ｜ 交期：35 days after deposit", order.Remark);

        // 合计与定金由服务端按销售订单口径重算：750 × 40% = 300
        Assert.Equal(750m, order.TotalAmount);
        Assert.Equal(300m, order.DepositAmount);

        var lines = db.SalesOrderDetails.Where(d => d.SalesOrderId == order.Id).OrderBy(d => d.Id).ToList();
        Assert.Equal(2, lines.Count);
        Assert.Equal(11L, lines[0].ProductId);
        Assert.Equal("商品 A", lines[0].ProductName);
        Assert.Equal("大", lines[0].Spec);
        Assert.Equal("PCS", lines[0].Unit);
        Assert.Equal(100m, lines[0].Quantity);
        Assert.Equal(2.5m, lines[0].UnitPrice);
        Assert.Equal(250m, lines[0].Amount);        // 来源金额被写成 1，服务端重算为 100 × 2.5
        Assert.Equal("红色", lines[0].Remark);
        Assert.Equal(500m, lines[1].Amount);

        // 报价单转为「已完成」（已转 PI 或已转销售订单）
        Assert.Equal(DocumentStatus.Completed, db.Quotations.Single().Status);
    }

    [Fact]
    public async Task 报价单转销售订单_来源条款为空_回退客户档案默认值()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var quotation = SeedQuotation(db, "QT-FALLBACK", DocumentStatus.Approved, customer.Id);
        quotation.TradeTerms = string.Empty;
        quotation.PortOfDestination = string.Empty;
        quotation.PaymentTerms = string.Empty;
        quotation.LeadTime = string.Empty;
        db.SaveChanges();

        await NewQuotationController(db).ToSalesOrder(quotation.Id);

        var order = db.SalesOrders.Single();
        Assert.Equal("CIF", order.TradeTerms);                       // ← 客户档案贸易条款
        Assert.Equal("ROTTERDAM", order.DestinationPort);            // ← 客户档案目的港
        Assert.Equal("T/T 30% deposit, balance against B/L copy", order.PaymentTerms);
        Assert.Equal("ORDER_CONVERSION_TEST", order.Remark);         // 无交期时不追加说明
    }

    [Fact]
    public async Task 报价单转销售订单_无客户档案_使用安全默认值()
    {
        using var db = TestDbFactory.Create();
        var quotation = SeedQuotation(db, "QT-NO-CUSTOMER", DocumentStatus.Approved, 0L);

        await NewQuotationController(db).ToSalesOrder(quotation.Id);

        var order = db.SalesOrders.Single();
        Assert.Equal(0L, order.CustomerId);
        Assert.Equal(string.Empty, order.Consignee);
        Assert.Equal(string.Empty, order.NotifyParty);
        Assert.Equal(string.Empty, order.ShippingMarks);
        Assert.Equal(string.Empty, order.BusinessNature);
        Assert.Equal(0m, order.CommissionRatio);
        Assert.Equal(30m, order.DepositRatio);          // 客户未维护时按外贸惯例 30%
        Assert.Equal(750m, order.TotalAmount);
        Assert.Equal(225m, order.DepositAmount);
    }

    // ==================== 报价单 → 销售订单（守卫与预填） ====================

    [Fact]
    public async Task 报价单转销售订单_未审核或已作废_拒绝且不落库()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var draft = SeedQuotation(db, "QT-DRAFT", DocumentStatus.Pending, customer.Id);
        var cancelled = SeedQuotation(db, "QT-CANCELLED", DocumentStatus.Cancelled, customer.Id);
        var ctl = NewQuotationController(db);

        var exDraft = await Assert.ThrowsAsync<BusinessException>(() => ctl.ToSalesOrder(draft.Id));
        Assert.Equal(ErrorCodes.RuleConflict, exDraft.Code);
        Assert.Contains("未审核", exDraft.Message);

        var exCancelled = await Assert.ThrowsAsync<BusinessException>(() => ctl.ToSalesOrder(cancelled.Id));
        Assert.Equal(ErrorCodes.RuleConflict, exCancelled.Code);
        Assert.Contains("已作废", exCancelled.Message);

        Assert.Empty(db.SalesOrders);
        Assert.Equal(DocumentStatus.Pending, db.Quotations.Single(q => q.Id == draft.Id).Status);
        Assert.Equal(DocumentStatus.Cancelled, db.Quotations.Single(q => q.Id == cancelled.Id).Status);
    }

    [Fact]
    public async Task 报价单转销售订单_无商品明细_拒绝()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var quotation = SeedQuotation(db, "QT-EMPTY", DocumentStatus.Approved, customer.Id);
        db.QuotationDetails.RemoveRange(db.QuotationDetails);
        db.SaveChanges();

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => NewQuotationController(db).ToSalesOrder(quotation.Id));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("无商品明细", ex.Message);
        Assert.Empty(db.SalesOrders);
    }

    [Fact]
    public async Task 报价单转销售订单_重复生成_被守卫拒绝且不新增单据()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var quotation = SeedQuotation(db, "QT-DUP", DocumentStatus.Approved, customer.Id);
        var ctl = NewQuotationController(db);

        await ctl.ToSalesOrder(quotation.Id);           // 首次生成

        // 重复点击（状态已变「已完成」）与「状态被人工改回」两种情形都必须被来源字段守卫拦住
        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.ToSalesOrder(quotation.Id));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("已生成销售订单", ex.Message);

        var entity = db.Quotations.Single();
        entity.Status = DocumentStatus.Approved;
        db.SaveChanges();
        var exAgain = await Assert.ThrowsAsync<BusinessException>(() => ctl.ToSalesOrder(quotation.Id));
        Assert.Contains("已生成销售订单", exAgain.Message);

        Assert.Single(db.SalesOrders);
        Assert.Equal(2, db.SalesOrderDetails.Count());   // 报价单两行明细已带入且不重复
    }

    [Fact]
    public async Task 报价单已转PI_不能直接转销售订单_提示从PI转()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var quotation = SeedQuotation(db, "QT-TO-PI", DocumentStatus.Approved, customer.Id);
        var ctl = NewQuotationController(db);

        await ctl.ToProformaInvoice(quotation.Id);      // 复用既有「报价单转 PI」

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.ToSalesOrder(quotation.Id));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("请从 PI 转销售订单", ex.Message);
        Assert.Empty(db.SalesOrders);

        var exPrefill = await Assert.ThrowsAsync<BusinessException>(() => ctl.OrderPrefill(quotation.Id));
        Assert.Contains("请从 PI 转销售订单", exPrefill.Message);
    }

    [Fact]
    public async Task 报价单带入预填_返回未落库草稿且不占用单号()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var quotation = SeedQuotation(db, "QT-PREFILL", DocumentStatus.Approved, customer.Id);

        var ok = Assert.IsType<OkObjectResult>(await NewQuotationController(db).OrderPrefill(quotation.Id));
        var prefill = Assert.IsType<ApiResponse<SalesOrderPrefillResult>>(ok.Value).Data!;

        Assert.Equal(SalesOrderConversion.QuotationSourceType, prefill.SourceType);
        Assert.Equal(quotation.Id, prefill.SourceId);
        Assert.Equal("QT-PREFILL", prefill.SourceNo);
        Assert.Equal(string.Empty, prefill.Order.OrderNo);          // 预填不占用单据号
        Assert.Equal(quotation.Id, prefill.Order.SourceQuotationId);
        Assert.Equal("QT-PREFILL", prefill.Order.SourceQuotationNo);
        Assert.Equal(750m, prefill.Order.TotalAmount);
        Assert.Equal(300m, prefill.Order.DepositAmount);
        Assert.Equal(2, prefill.Order.Details.Count);
        Assert.Equal(250m, prefill.Order.Details[0].Amount);

        Assert.Empty(db.SalesOrders);                                 // 预填不写库
        Assert.Empty(db.SysDocumentNumberRules);                      // 预填不消耗单据号规则
        Assert.Equal(DocumentStatus.Approved, db.Quotations.Single().Status);   // 来源状态不变
    }

    // ==================== 带入数据服务端复核 ====================

    [Fact]
    public async Task 报价单转销售订单_明细数量为零_服务端复核拒绝()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var quotation = SeedQuotation(db, "QT-ZERO-QTY", DocumentStatus.Approved, customer.Id);
        db.QuotationDetails.OrderBy(d => d.SortNo).First().Quantity = 0m;
        db.SaveChanges();

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => NewQuotationController(db).ToSalesOrder(quotation.Id));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Contains("数量必须大于 0", ex.Message);
        Assert.Empty(db.SalesOrders);
    }

    [Fact]
    public async Task 报价单转销售订单_客户定金比例超范围_服务端复核拒绝()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        customer.DepositRatio = 150m;                    // 客户档案被维护成非法比例
        db.SaveChanges();
        var quotation = SeedQuotation(db, "QT-BAD-RATIO", DocumentStatus.Approved, customer.Id);

        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => NewQuotationController(db).ToSalesOrder(quotation.Id));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Contains("定金比例", ex.Message);
        Assert.Empty(db.SalesOrders);
    }

    // ==================== 形式发票 PI → 销售订单 ====================

    [Fact]
    public async Task PI转销售订单_已审核_映射完整并保留PI与报价单双来源()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var pi = SeedPi(db, customer.Id);
        var ctl = NewProformaInvoiceController(db);

        var ok = Assert.IsType<OkObjectResult>(await ctl.ToSalesOrder(pi.Id));
        var result = Assert.IsType<ApiResponse<SalesOrderConversionResult>>(ok.Value).Data!;
        Assert.StartsWith("SO", result.OrderNo);
        Assert.Equal(pi.PiNo, result.SourceNo);

        var order = db.SalesOrders.Single();
        Assert.Equal(customer.Id, order.CustomerId);
        Assert.Equal(Currency.EUR, order.Currency);
        Assert.Equal(7.9m, order.ExchangeRate);
        Assert.Equal("CIF", order.TradeTerms);
        Assert.Equal("HAMBURG", order.DestinationPort);
        Assert.Equal("L/C at sight", order.PaymentTerms);
        // 收货人 / 通知人 / 唛头 / 运输方式：PI 值优先于客户档案默认值
        Assert.Equal("PI CONSIGNEE", order.Consignee);
        Assert.Equal("PI NOTIFY", order.NotifyParty);
        Assert.Equal("PI MARKS", order.ShippingMarks);
        Assert.Equal("By sea, FCL", order.ShippingMethod);
        Assert.Equal(30m, order.DepositRatio);
        Assert.Equal("自营出口", order.BusinessNature);
        Assert.Equal(3.5m, order.CommissionRatio);
        // 追溯链：来源 PI + PI 背后的来源报价单
        Assert.Equal(pi.Id, order.SourcePiId);
        Assert.Equal("PI-ORDER-1", order.SourcePiNo);
        Assert.Equal(77L, order.SourceQuotationId);
        Assert.Equal("QT-UPSTREAM-77", order.SourceQuotationNo);
        Assert.Equal("PI_CONVERSION_TEST ｜ 交期：30 days after deposit", order.Remark);
        Assert.Equal(1000m, order.TotalAmount);
        Assert.Equal(300m, order.DepositAmount);

        var line = db.SalesOrderDetails.Single();
        Assert.Equal(21L, line.ProductId);
        Assert.Equal("PI 商品", line.ProductName);
        Assert.Equal(100m, line.Quantity);
        Assert.Equal(10m, line.UnitPrice);
        Assert.Equal(1000m, line.Amount);                 // 来源金额被写成 9999，服务端重算
        Assert.Equal("PI 备注", line.Remark);

        Assert.Equal(DocumentStatus.Completed, db.ProformaInvoices.Single().Status);   // PI → 已转销售订单
    }

    [Fact]
    public async Task PI转销售订单_定金比例为空_按定金金额反算比例()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var pi = SeedPi(db, customer.Id);
        pi.DepositRatio = 0m;
        pi.DepositAmount = 250m;
        pi.TotalAmount = 1000m;
        db.SaveChanges();

        await NewProformaInvoiceController(db).ToSalesOrder(pi.Id);

        var order = db.SalesOrders.Single();
        Assert.Equal(25m, order.DepositRatio);            // 250 / 1000 = 25%
        Assert.Equal(250m, order.DepositAmount);
    }

    [Fact]
    public async Task PI转销售订单_未审核已作废无明细_拒绝()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var draft = SeedPi(db, customer.Id, no: "PI-DRAFT", status: DocumentStatus.Pending);
        var cancelled = SeedPi(db, customer.Id, no: "PI-CANCELLED", status: DocumentStatus.Cancelled);
        var empty = SeedPi(db, customer.Id, no: "PI-EMPTY", status: DocumentStatus.Approved, withDetails: false);
        var ctl = NewProformaInvoiceController(db);

        var exDraft = await Assert.ThrowsAsync<BusinessException>(() => ctl.ToSalesOrder(draft.Id));
        Assert.Equal(ErrorCodes.RuleConflict, exDraft.Code);
        Assert.Contains("未审核", exDraft.Message);

        var exCancelled = await Assert.ThrowsAsync<BusinessException>(() => ctl.ToSalesOrder(cancelled.Id));
        Assert.Contains("已作废", exCancelled.Message);

        var exEmpty = await Assert.ThrowsAsync<BusinessException>(() => ctl.ToSalesOrder(empty.Id));
        Assert.Contains("无商品明细", exEmpty.Message);

        Assert.Empty(db.SalesOrders);
    }

    [Fact]
    public async Task PI转销售订单_重复生成_被守卫拒绝且不新增单据()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var pi = SeedPi(db, customer.Id);
        var ctl = NewProformaInvoiceController(db);

        await ctl.ToSalesOrder(pi.Id);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.ToSalesOrder(pi.Id));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("已生成销售订单", ex.Message);

        var exPrefill = await Assert.ThrowsAsync<BusinessException>(() => ctl.OrderPrefill(pi.Id));
        Assert.Contains("已生成销售订单", exPrefill.Message);

        Assert.Single(db.SalesOrders);
        Assert.Single(db.SalesOrderDetails);
    }

    [Fact]
    public async Task PI带入预填_返回未落库草稿且不占用单号()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var pi = SeedPi(db, customer.Id);

        var ok = Assert.IsType<OkObjectResult>(await NewProformaInvoiceController(db).OrderPrefill(pi.Id));
        var prefill = Assert.IsType<ApiResponse<SalesOrderPrefillResult>>(ok.Value).Data!;

        Assert.Equal(SalesOrderConversion.ProformaInvoiceSourceType, prefill.SourceType);
        Assert.Equal(pi.Id, prefill.SourceId);
        Assert.Equal("PI-ORDER-1", prefill.SourceNo);
        Assert.Equal(string.Empty, prefill.Order.OrderNo);
        Assert.Equal(pi.Id, prefill.Order.SourcePiId);
        Assert.Equal(77L, prefill.Order.SourceQuotationId);
        Assert.Equal(1000m, prefill.Order.TotalAmount);
        Assert.Equal(300m, prefill.Order.DepositAmount);
        Assert.Single(prefill.Order.Details);

        Assert.Empty(db.SalesOrders);
        Assert.Equal(DocumentStatus.Approved, db.ProformaInvoices.Single().Status);
    }

    // ==================== 接口路由与前端接线（浏览器验收延后阶段的静态兜底） ====================

    [Fact]
    public void 转换接口路由_两种来源均提供to_order与order_prefill()
    {
        AssertRoute(typeof(QuotationController), nameof(QuotationController.ToSalesOrder), "to-order");
        AssertRoute(typeof(QuotationController), nameof(QuotationController.OrderPrefill), "order-prefill");
        AssertRoute(typeof(ProformaInvoiceController), nameof(ProformaInvoiceController.ToSalesOrder), "to-order");
        AssertRoute(typeof(ProformaInvoiceController), nameof(ProformaInvoiceController.OrderPrefill), "order-prefill");
    }

    [Fact]
    public void 前端行操作_与销售订单转换接口接线一致()
    {
        var directory = JsDirectory();
        Assert.True(Directory.Exists(directory), $"未找到前端脚本目录：{directory}");
        var modules = File.ReadAllText(Path.Combine(directory, "modules.js"));
        var salesPi = File.ReadAllText(Path.Combine(directory, "sales-pi.js"));

        // 报价单 / PI 的行操作 → 全局函数名（必须成对存在）
        foreach (var function in new[] { "quotationToOrder", "quotationPrefillOrder", "piToOrder", "piPrefillOrder" })
        {
            Assert.Contains($"onclick: '{function}'", modules);
            Assert.Contains($"function {function}(", salesPi);
        }

        // 函数 → 后端接口路径与来源配置（拼写漂移会让「更多」里的按钮点了没反应）
        Assert.Contains("const SALES_ORDER_SOURCE = {", salesPi);
        Assert.Contains("api: '/api/sales/quotations'", salesPi);
        Assert.Contains("api: '/api/sales/proforma-invoices'", salesPi);
        Assert.Contains("`${cfg.api}/${id}/to-order`", salesPi);
        Assert.Contains("`${cfg.api}/${id}/order-prefill`", salesPi);
        // 带入预填复用销售订单模块与既有表单渲染
        Assert.Contains("gotoModulePage('sales-order'", salesPi);
        Assert.Contains("fillSalesOrderForm(", salesPi);
    }

    [Fact]
    public void 带入预填报文_JSON字段名与枚举口径符合前端约定()
    {
        // 与 Program.cs 的 JSON 配置一致：camelCase + 枚举名（前端按 data.order[f.key] 取值）
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());

        var payload = new SalesOrderPrefillResult
        {
            SourceType = SalesOrderConversion.ProformaInvoiceSourceType,
            SourceId = 9L,
            SourceNo = "PI2609230001",
            Order = new SalesOrder
            {
                OrderDate = new DateTime(2026, 9, 23),
                CustomerId = 3L,
                Currency = Currency.USD,
                ExchangeRate = 7.2m,
                DepositRatio = 30m,
                SplitShipment = false,
                SourcePiNo = "PI2609230001",
                Details = new List<SalesOrderDetail>
                {
                    new() { ProductId = 1, ProductName = "P1", Quantity = 2m, UnitPrice = 3m, Amount = 6m }
                }
            }
        };

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(payload, options));
        var root = doc.RootElement;
        Assert.Equal("ProformaInvoice", root.GetProperty("sourceType").GetString());
        Assert.Equal(9L, root.GetProperty("sourceId").GetInt64());
        Assert.Equal("PI2609230001", root.GetProperty("sourceNo").GetString());

        var order = root.GetProperty("order");
        Assert.Equal("USD", order.GetProperty("currency").GetString());
        Assert.Equal(7.2m, order.GetProperty("exchangeRate").GetDecimal());
        Assert.Equal("PI2609230001", order.GetProperty("sourcePiNo").GetString());
        Assert.Equal(30m, order.GetProperty("depositRatio").GetDecimal());
        Assert.False(order.GetProperty("splitShipment").GetBoolean());

        var detail = order.GetProperty("details")[0];
        Assert.Equal("P1", detail.GetProperty("productName").GetString());
        Assert.Equal(3m, detail.GetProperty("unitPrice").GetDecimal());
    }

    // ==================== 工厂与种子数据 ====================

    private static QuotationController NewQuotationController(ErpDbContext db)
        => new(db, new DocumentNumberService(db));

    private static ProformaInvoiceController NewProformaInvoiceController(ErpDbContext db)
        => new(db, new DocumentNumberService(db));

    /// <summary>断言转换接口路由存在（前端调用的路径契约）</summary>
    private static void AssertRoute(Type controller, string methodName, string routeSuffix)
    {
        var method = controller.GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(method);
        var templates = method!.GetCustomAttributes<HttpMethodAttribute>(true)
            .Select(a => a.Template ?? string.Empty)
            .ToList();
        Assert.Contains(templates, t => t == "{id:long}/" + routeSuffix);
    }

    /// <summary>前端脚本目录（沿测试程序集输出目录上溯到仓库根，与 UiTestFixture 同一约定）</summary>
    private static string JsDirectory() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "..", "..", "..", "..", "..", "src", "ERP.Api", "wwwroot", "js"));

    /// <summary>客户档案：收货人 / 通知人 / 唛头 / 定金比例 / 业务性质 / 佣金比例 / 条款默认值齐备</summary>
    private static BaseCustomer SeedCustomer(ErpDbContext db)
    {
        var customer = new BaseCustomer
        {
            CustomerCode = "C-ORDER-1",
            CustomerName = "义乌客户（带入测试）",
            Consignee = "ACME IMPORT GMBH",
            NotifyParty = "ACME LOGISTICS",
            DefaultShippingMark = "ACME\r\nHAMBURG\r\nC/NO.1-120",
            DepositRatio = 40m,
            BusinessNature = "自营出口",
            CommissionRatio = 3.5m,
            TradeTerms = "CIF",
            DestinationPort = "ROTTERDAM",
            PaymentTerms = "T/T 30% deposit, balance against B/L copy"
        };
        db.BaseCustomers.Add(customer);
        db.SaveChanges();
        return customer;
    }

    /// <summary>报价单：两行明细共 750（第一行来源金额故意写成 1，用于验证服务端重算）</summary>
    private static Quotation SeedQuotation(ErpDbContext db, string no, DocumentStatus status, long customerId)
    {
        var quotation = new Quotation
        {
            QuotationNo = no,
            QuotationDate = DateTime.Today,
            ValidUntil = DateTime.Today.AddDays(30),
            CustomerId = customerId,
            CustomerName = "义乌客户（带入测试）",
            ContactPerson = "Mr. Smith",
            TradeTerms = "FOB",
            PortOfLoading = "NINGBO",
            PortOfDestination = "HAMBURG",
            PaymentTerms = "T/T 30% deposit",
            LeadTime = "35 days after deposit",
            Currency = Currency.USD,
            ExchangeRate = 7.2m,
            SalesmanId = 66L,
            SalesmanName = "业务员 A",
            Status = status,
            Remark = "ORDER_CONVERSION_TEST",
            Details = new List<QuotationDetail>
            {
                new()
                {
                    SortNo = 1, ProductId = 11, ProductCode = "P-1", ProductName = "商品 A", Spec = "大", Unit = "PCS",
                    Quantity = 100m, UnitPrice = 2.5m, Amount = 1m, Moq = "500 pcs", Remark = "红色"
                },
                new()
                {
                    SortNo = 2, ProductId = 12, ProductCode = "P-2", ProductName = "商品 B", Spec = "中", Unit = "SET",
                    Quantity = 40m, UnitPrice = 12.5m, Amount = 500m, Remark = "蓝色"
                }
            }
        };
        db.Quotations.Add(quotation);
        db.SaveChanges();
        return quotation;
    }

    /// <summary>形式发票 PI：含收货人 / 通知人 / 唛头 / 运输条款与来源报价单，明细一行 1000</summary>
    private static ProformaInvoice SeedPi(ErpDbContext db, long? customerId, string no = "PI-ORDER-1",
        DocumentStatus status = DocumentStatus.Approved, bool withDetails = true)
    {
        var pi = new ProformaInvoice
        {
            PiNo = no,
            PiDate = DateTime.Today,
            QuotationId = 77L,
            QuotationNo = "QT-UPSTREAM-77",
            CustomerId = customerId,
            CustomerName = "义乌客户（带入测试）",
            Consignee = "PI CONSIGNEE",
            NotifyParty = "PI NOTIFY",
            ShippingMarks = "PI MARKS",
            ShippingTerms = "By sea, FCL",
            TradeTerms = "CIF",
            PortOfDestination = "HAMBURG",
            PaymentTerms = "L/C at sight",
            LeadTime = "30 days after deposit",
            Currency = Currency.EUR,
            ExchangeRate = 7.9m,
            TotalAmount = 1000m,
            DepositRatio = 30m,
            SalesmanName = "业务员 A",
            Status = status,
            Remark = "PI_CONVERSION_TEST"
        };
        if (withDetails)
        {
            pi.Details.Add(new ProformaInvoiceDetail
            {
                SortNo = 1, ProductId = 21, ProductCode = "PX-1", ProductName = "PI 商品", Spec = "标准", Unit = "PCS",
                Quantity = 100m, UnitPrice = 10m, Amount = 9999m, Moq = "1000", Remark = "PI 备注"
            });
        }
        db.ProformaInvoices.Add(pi);
        db.SaveChanges();
        return pi;
    }
}
