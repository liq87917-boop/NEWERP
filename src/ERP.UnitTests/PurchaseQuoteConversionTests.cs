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
/// 供应商比价选中行 → 采购订单（带入预填 + 直接生成，ERP-020）单元测试：
/// 资格守卫（未选中 / 已放弃 / 未维护供应商）、字段与明细映射、服务端金额复核、来源留痕、
/// 重复生成守卫（RefOrderNo 链接 + 备注来源标记）、前端接线（行操作函数 + 接口路由）与报文契约。
/// </summary>
public class PurchaseQuoteConversionTests
{
    // ==================== 选中比价行 → 采购订单（直接生成） ====================

    [Fact]
    public async Task 选中比价行转采购订单_字段与明细映射完整并留痕来源()
    {
        using var db = TestDbFactory.Create();
        var quote = SeedSelectedQuote(db, "PQ-20260924-001");

        var ok = Assert.IsType<OkObjectResult>(await NewController(db).ToPurchaseOrder(quote.Id));
        var result = Assert.IsType<ApiResponse<PurchaseOrderConversionResult>>(ok.Value).Data!;
        Assert.True(result.Id > 0);
        Assert.StartsWith("PO", result.OrderNo);
        Assert.Equal("PQ-20260924-001", result.SourceNo);

        var order = db.PurchaseOrders.Single();
        Assert.Equal(DocumentStatus.Pending, order.Status);
        Assert.Equal(DateTime.Today, order.OrderDate);
        Assert.Equal(88L, order.SupplierId);
        Assert.Null(order.BuyerId);                             // 比价表无采购员列
        Assert.Equal(Currency.USD, order.Currency);             // 币种文本 USD → 枚举
        Assert.Equal(1m, order.ExchangeRate);                   // 比价表无汇率列，按 1
        Assert.True(order.TaxIncluded);
        Assert.Equal(0m, order.TaxRate);                        // 比价表无税率列，不臆造
        Assert.Equal("T/T 30% deposit", order.PaymentTerms);
        Assert.Equal(DateTime.Today.AddDays(25), order.DeliveryDate);          // 交期(天) → 交货日期
        Assert.Equal(DateTime.Today.AddDays(25), order.SupplierConfirmedDate); // 比价交期即供应商承诺
        Assert.Equal(7L, order.OwningCustomerId);                              // 为客户询价 → 归属客户
        Assert.Equal("义乌客户（比价测试）", order.OwningCustomerName);
        Assert.False(order.AdvanceOnBehalf);                    // 代垫由采购员决定，不自动置位
        Assert.Equal(string.Empty, order.ContractNo);

        // 来源留痕：备注保留比价备注 + 来源标记
        Assert.Contains("QUOTE_CONVERSION_TEST", order.Remark);
        Assert.Contains("来源比价 PQ-20260924-001（比价行 #" + quote.Id + "）", order.Remark);

        // 合计由服务端按采购订单口径重算：报价总额故意写成 1，重算为 5000 × 2.6
        Assert.Equal(13000m, order.TotalAmount);

        var line = db.PurchaseOrderDetails.Single(d => d.PurchaseOrderId == order.Id);
        Assert.Equal(310L, line.ProductId);
        Assert.Equal("PU 商品", line.ProductName);
        Assert.Equal("大号", line.Spec);
        Assert.Equal("PCS", line.Unit);
        Assert.Equal(5000m, line.Quantity);
        Assert.Equal(2.6m, line.UnitPrice);
        Assert.Equal(13000m, line.Amount);
        Assert.Equal(order.DeliveryDate, line.DeliveryDate);

        // 比价行留痕：状态置「已转采购订单」，RefOrderNo 写回采购单号
        var saved = db.PurchaseQuotes.Single();
        Assert.Equal(PurchaseQuoteConversion.ConvertedStatus, saved.Status);
        Assert.Equal(order.OrderNo, saved.RefOrderNo);
        Assert.NotNull(saved.UpdatedAt);
    }

    [Fact]
    public async Task 选中比价行转采购订单_关联销售订单号解析为归属销售订单()
    {
        using var db = TestDbFactory.Create();
        db.SalesOrders.Add(new SalesOrder { OrderNo = "SO-OWN-1", OrderDate = DateTime.Today, Status = DocumentStatus.Approved });
        db.SaveChanges();
        var salesOrderId = db.SalesOrders.Single().Id;
        var quote = SeedSelectedQuote(db, "PQ-OWNING-1", refOrderNo: "SO-OWN-1");

        await NewController(db).ToPurchaseOrder(quote.Id);

        var order = db.PurchaseOrders.Single();
        Assert.Equal(salesOrderId, order.OwningSalesOrderId);
        Assert.Equal("SO-OWN-1", order.OwningSalesOrderNo);
    }

    [Fact]
    public async Task 选中比价行转采购订单_关联销售订单号匹配不到_归属字段留空()
    {
        using var db = TestDbFactory.Create();
        var quote = SeedSelectedQuote(db, "PQ-OWNING-2", refOrderNo: "SO-NOT-EXIST");

        await NewController(db).ToPurchaseOrder(quote.Id);

        var order = db.PurchaseOrders.Single();
        Assert.Null(order.OwningSalesOrderId);
        Assert.Equal(string.Empty, order.OwningSalesOrderNo);
    }

    [Fact]
    public async Task 选中比价行转采购订单_交期为零_交货日期留空()
    {
        using var db = TestDbFactory.Create();
        var quote = SeedSelectedQuote(db, "PQ-NO-LEAD", deliveryDays: 0);

        await NewController(db).ToPurchaseOrder(quote.Id);

        var order = db.PurchaseOrders.Single();
        Assert.Null(order.DeliveryDate);
        Assert.Null(order.SupplierConfirmedDate);
        Assert.Null(db.PurchaseOrderDetails.Single().DeliveryDate);
    }

    [Fact]
    public async Task 选中比价行转采购订单_币种无法识别_回退人民币()
    {
        using var db = TestDbFactory.Create();
        var quote = SeedSelectedQuote(db, "PQ-CUR-1");
        quote.Currency = "RUB";
        db.SaveChanges();

        await NewController(db).ToPurchaseOrder(quote.Id);

        Assert.Equal(Currency.CNY, db.PurchaseOrders.Single().Currency);
    }

    [Fact]
    public async Task 选中比价行转采购订单_商品未引用档案_ProductId按零占位且保留名称()
    {
        using var db = TestDbFactory.Create();
        var quote = SeedSelectedQuote(db, "PQ-NO-PRODUCT");
        quote.ProductId = null;
        quote.ProductName = "档口现货（无档案）";
        db.SaveChanges();

        await NewController(db).ToPurchaseOrder(quote.Id);

        var line = db.PurchaseOrderDetails.Single();
        Assert.Equal(0L, line.ProductId);
        Assert.Equal("档口现货（无档案）", line.ProductName);
    }

    // ==================== 资格守卫 ====================

    [Fact]
    public async Task 未选中比价行_转采购订单_抛RuleConflict_且不落库()
    {
        using var db = TestDbFactory.Create();
        var quote = SeedSelectedQuote(db, "PQ-UNSELECTED");
        quote.IsSelected = false;
        quote.Status = PurchaseQuoteConversion.SelectedStatus;
        db.SaveChanges();

        var ex = await Assert.ThrowsAsync<BusinessException>(() => NewController(db).ToPurchaseOrder(quote.Id));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("未选中供应商", ex.Message);
        Assert.Empty(db.PurchaseOrders);
        Assert.Equal(PurchaseQuoteConversion.SelectedStatus, db.PurchaseQuotes.Single().Status);
    }

    [Fact]
    public async Task 已放弃比价行_即使误勾选中_转采购订单_抛RuleConflict()
    {
        using var db = TestDbFactory.Create();
        var quote = SeedSelectedQuote(db, "PQ-DISCARDED");
        quote.Status = PurchaseQuoteConversion.DiscardedStatus;
        db.SaveChanges();

        var ex = await Assert.ThrowsAsync<BusinessException>(() => NewController(db).ToPurchaseOrder(quote.Id));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("已放弃", ex.Message);
        Assert.Empty(db.PurchaseOrders);
    }

    [Fact]
    public async Task 比价行未维护供应商_转采购订单_抛RuleConflict()
    {
        using var db = TestDbFactory.Create();
        var quote = SeedSelectedQuote(db, "PQ-NO-SUPPLIER");
        quote.SupplierId = null;
        quote.SupplierName = string.Empty;
        db.SaveChanges();

        var ex = await Assert.ThrowsAsync<BusinessException>(() => NewController(db).ToPurchaseOrder(quote.Id));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("未维护供应商", ex.Message);
        Assert.Empty(db.PurchaseOrders);
    }

    [Fact]
    public async Task 比价行数量为零_转采购订单_抛InvalidParameter()
    {
        using var db = TestDbFactory.Create();
        var quote = SeedSelectedQuote(db, "PQ-QTY-0");
        quote.Quantity = 0m;
        db.SaveChanges();

        var ex = await Assert.ThrowsAsync<BusinessException>(() => NewController(db).ToPurchaseOrder(quote.Id));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Contains("数量必须大于 0", ex.Message);
        Assert.Empty(db.PurchaseOrders);
    }

    [Fact]
    public async Task 比价行单价为负_转采购订单_抛InvalidParameter()
    {
        using var db = TestDbFactory.Create();
        var quote = SeedSelectedQuote(db, "PQ-PRICE-NEG");
        quote.QuotePrice = -1m;
        db.SaveChanges();

        var ex = await Assert.ThrowsAsync<BusinessException>(() => NewController(db).ToPurchaseOrder(quote.Id));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Contains("单价不能为负数", ex.Message);
        Assert.Empty(db.PurchaseOrders);
    }

    [Fact]
    public async Task 比价行不存在_预填与生成_均抛NotFound()
    {
        using var db = TestDbFactory.Create();
        var ctl = NewController(db);

        await Assert.ThrowsAsync<BusinessException>(() => ctl.OrderPrefill(999));
        await Assert.ThrowsAsync<BusinessException>(() => ctl.ToPurchaseOrder(999));
    }

    // ==================== 重复生成守卫 ====================

    [Fact]
    public async Task 重复转换_第二次抛RuleConflict_且只生成一张采购订单()
    {
        using var db = TestDbFactory.Create();
        var quote = SeedSelectedQuote(db, "PQ-DUP-1");
        var ctl = NewController(db);

        var first = Assert.IsType<ApiResponse<PurchaseOrderConversionResult>>(
            Assert.IsType<OkObjectResult>(await ctl.ToPurchaseOrder(quote.Id)).Value).Data!;

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.ToPurchaseOrder(quote.Id));
        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains($"已生成采购订单：{first.OrderNo}", ex.Message);
        Assert.Single(db.PurchaseOrders);
    }

    [Fact]
    public async Task 人工把比价行状态改回已选中_RefOrderNo链接兜底拦截重复生成()
    {
        using var db = TestDbFactory.Create();
        var quote = SeedSelectedQuote(db, "PQ-DUP-2");
        var ctl = NewController(db);
        await ctl.ToPurchaseOrder(quote.Id);

        // 模拟人工回退：状态改回「已选中」，但 RefOrderNo 仍指向已生成的采购订单
        var saved = db.PurchaseQuotes.Single();
        saved.Status = PurchaseQuoteConversion.SelectedStatus;
        db.SaveChanges();

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.ToPurchaseOrder(quote.Id));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("已生成采购订单", ex.Message);
        Assert.Single(db.PurchaseOrders);
    }

    [Fact]
    public async Task 人工清空RefOrderNo与转换状态_备注来源标记兜底拦截重复生成()
    {
        using var db = TestDbFactory.Create();
        var quote = SeedSelectedQuote(db, "PQ-DUP-3");
        var ctl = NewController(db);
        await ctl.ToPurchaseOrder(quote.Id);

        var saved = db.PurchaseQuotes.Single();
        saved.Status = PurchaseQuoteConversion.SelectedStatus;
        saved.RefOrderNo = string.Empty;
        db.SaveChanges();

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.ToPurchaseOrder(quote.Id));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains("已生成采购订单", ex.Message);
        Assert.Single(db.PurchaseOrders);
    }

    [Fact]
    public async Task 生成的采购订单被软删除后_允许重新生成()
    {
        using var db = TestDbFactory.Create();
        var quote = SeedSelectedQuote(db, "PQ-DUP-4");
        var ctl = NewController(db);
        await ctl.ToPurchaseOrder(quote.Id);

        var first = db.PurchaseOrders.Single();
        first.IsDeleted = true;
        var saved = db.PurchaseQuotes.Single();
        saved.Status = PurchaseQuoteConversion.SelectedStatus;
        saved.RefOrderNo = string.Empty;
        db.SaveChanges();

        await ctl.ToPurchaseOrder(quote.Id);

        Assert.Equal(2, db.PurchaseOrders.Count());
        Assert.Single(db.PurchaseOrders.Where(o => !o.IsDeleted));
    }

    // ==================== 带入预填（不落库） ====================

    [Fact]
    public async Task 带入预填_返回未落库草稿_不占用单据号且不改来源状态()
    {
        using var db = TestDbFactory.Create();
        var quote = SeedSelectedQuote(db, "PQ-PREFILL-1", refOrderNo: "SO-OWN-9");
        var seedUpdatedAt = db.PurchaseQuotes.Single().UpdatedAt;      // 种子数据在插入时已写入审计时间

        var ok = Assert.IsType<OkObjectResult>(await NewController(db).OrderPrefill(quote.Id));
        var prefill = Assert.IsType<ApiResponse<PurchaseOrderPrefillResult>>(ok.Value).Data!;

        Assert.Equal(PurchaseQuoteConversion.PurchaseQuoteSourceType, prefill.SourceType);
        Assert.Equal(quote.Id, prefill.SourceId);
        Assert.Equal("PQ-PREFILL-1", prefill.SourceNo);
        Assert.Equal(string.Empty, prefill.Order.OrderNo);            // 预填不占用单据号
        Assert.Equal(88L, prefill.Order.SupplierId);
        Assert.Equal(Currency.USD, prefill.Order.Currency);
        Assert.Equal(13000m, prefill.Order.TotalAmount);              // 服务端重算合计
        Assert.Single(prefill.Order.Details);
        Assert.Equal(13000m, prefill.Order.Details[0].Amount);

        Assert.Empty(db.PurchaseOrders);                              // 预填不写库
        var saved = db.PurchaseQuotes.Single();
        Assert.Equal(PurchaseQuoteConversion.SelectedStatus, saved.Status);
        Assert.Equal("SO-OWN-9", saved.RefOrderNo);                   // 预填不改比价行
        Assert.Equal(seedUpdatedAt, saved.UpdatedAt);                 // 预填只读：审计时间未被刷新
    }

    [Fact]
    public async Task 带入预填_未选中比价行_同样被守卫拒绝()
    {
        using var db = TestDbFactory.Create();
        var quote = SeedSelectedQuote(db, "PQ-PREFILL-2");
        quote.IsSelected = false;
        db.SaveChanges();

        var ex = await Assert.ThrowsAsync<BusinessException>(() => NewController(db).OrderPrefill(quote.Id));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Empty(db.PurchaseOrders);
    }

    // ==================== 接口路由与前端接线 ====================

    [Fact]
    public void 转换接口路由_与前端调用路径一致()
    {
        AssertRoute(typeof(PurchaseQuoteController), nameof(PurchaseQuoteController.OrderPrefill), "order-prefill");
        AssertRoute(typeof(PurchaseQuoteController), nameof(PurchaseQuoteController.ToPurchaseOrder), "to-order");
    }

    [Fact]
    public void 前端接线_行操作函数_接口路径与脚本加载()
    {
        var modules = File.ReadAllText(Path.Combine(JsDirectory(), "modules.js"));
        var js = File.ReadAllText(Path.Combine(JsDirectory(), "purchase-order-conversion.js"));
        var indexHtml = File.ReadAllText(Path.Combine(JsDirectory(), "..", "index.html"));

        // 比价模块的行操作 → 全局函数名（必须成对存在）
        foreach (var function in new[] { "purchaseQuoteToOrder", "purchaseQuotePrefillOrder" })
        {
            Assert.Contains($"onclick: '{function}'", modules);
            Assert.Contains($"function {function}(", js);
        }
        // 只在「已选中」行显示转换入口；转换后状态必须出现在状态下拉里（否则回显为空）
        Assert.Contains("statuses: ['已选中']", modules);
        Assert.Contains("'已转采购订单'", modules);

        // 函数 → 后端接口路径（拼写漂移会让「更多」里的按钮点了没反应）
        Assert.Contains("const PURCHASE_ORDER_SOURCE = {", js);
        Assert.Contains("api: '/api/purchase/quotes'", js);
        Assert.Contains("`${cfg.api}/${id}/to-order`", js);
        Assert.Contains("`${cfg.api}/${id}/order-prefill`", js);
        // 带入预填复用采购订单模块与既有表单渲染
        Assert.Contains("gotoModulePage('purchase-order'", js);
        Assert.Contains("fillPurchaseOrderForm(", js);

        // index.html 必须加载该脚本，否则行操作点击后函数未定义
        Assert.Contains("/js/purchase-order-conversion.js", indexHtml);
    }

    [Fact]
    public void 带入预填报文_JSON字段名与枚举口径符合前端约定()
    {
        // 与 Program.cs 的 JSON 配置一致：camelCase + 枚举名（前端按 data.order[f.key] 取值）
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());

        var payload = new PurchaseOrderPrefillResult
        {
            SourceType = PurchaseQuoteConversion.PurchaseQuoteSourceType,
            SourceId = 12L,
            SourceNo = "PQ-20260924-001",
            Order = new PurchaseOrder
            {
                OrderDate = new DateTime(2026, 9, 24),
                SupplierId = 88L,
                Currency = Currency.USD,
                ExchangeRate = 1m,
                TaxIncluded = true,
                DeliveryDate = new DateTime(2026, 10, 19),
                OwningCustomerId = 7L,
                OwningCustomerName = "义乌客户",
                Details = new List<PurchaseOrderDetail>
                {
                    new() { ProductId = 310, ProductName = "PU 商品", Quantity = 5000m, UnitPrice = 2.6m, Amount = 13000m }
                }
            }
        };

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(payload, options));
        var root = doc.RootElement;
        Assert.Equal("PurchaseQuote", root.GetProperty("sourceType").GetString());
        Assert.Equal(12L, root.GetProperty("sourceId").GetInt64());
        Assert.Equal("PQ-20260924-001", root.GetProperty("sourceNo").GetString());

        var order = root.GetProperty("order");
        Assert.Equal("USD", order.GetProperty("currency").GetString());
        Assert.Equal(1m, order.GetProperty("exchangeRate").GetDecimal());
        Assert.True(order.GetProperty("taxIncluded").GetBoolean());
        Assert.Equal("2026-10-19", order.GetProperty("deliveryDate").GetString()![..10]);
        Assert.Equal("义乌客户", order.GetProperty("owningCustomerName").GetString());

        var detail = order.GetProperty("details")[0];
        Assert.Equal("PU 商品", detail.GetProperty("productName").GetString());
        Assert.Equal(2.6m, detail.GetProperty("unitPrice").GetDecimal());
        Assert.Equal(13000m, detail.GetProperty("amount").GetDecimal());
    }

    [Theory]
    [InlineData("USD", Currency.USD)]
    [InlineData("usd", Currency.USD)]
    [InlineData(" CNY ", Currency.CNY)]
    [InlineData("RUB", Currency.CNY)]
    [InlineData("", Currency.CNY)]
    [InlineData(null, Currency.CNY)]
    public void 币种文本解析_大小写不敏感_无法识别回退人民币(string? text, Currency expected)
        => Assert.Equal(expected, PurchaseQuoteConversion.ParseCurrency(text));

    // ==================== 工厂与种子数据 ====================

    private static PurchaseQuoteController NewController(ErpDbContext db)
        => new(new GenericService<PurchaseQuote>(db), db, new DocumentNumberService(db));

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

    /// <summary>
    /// 已选中的比价行：报价总额故意写成 1（验证服务端按采购订单口径重算），交期 25 天、含税、
    /// 报价币种 USD、为客户询价（归属客户 7），可选关联销售订单号（转换前语义）。
    /// </summary>
    private static PurchaseQuote SeedSelectedQuote(ErpDbContext db, string quoteNo, int deliveryDays = 25,
        string refOrderNo = "")
    {
        var quote = new PurchaseQuote
        {
            QuoteNo = quoteNo,
            QuoteDate = DateTime.Today,
            ProductId = 310L,
            ProductName = "PU 商品",
            Spec = "大号",
            Unit = "PCS",
            Quantity = 5000m,
            SupplierId = 88L,
            SupplierName = "义乌档口 A",
            SupplierType = "档口",
            QuotePrice = 2.6m,
            TotalAmount = 1m,
            Currency = "USD",
            TaxIncluded = true,
            DeliveryDays = deliveryDays,
            MinOrderQty = 1000,
            PaymentTerms = "T/T 30% deposit",
            IsSelected = true,
            Status = PurchaseQuoteConversion.SelectedStatus,
            CustomerId = 7L,
            CustomerName = "义乌客户（比价测试）",
            RefOrderNo = refOrderNo,
            Remark = "QUOTE_CONVERSION_TEST"
        };
        db.PurchaseQuotes.Add(quote);
        db.SaveChanges();
        return quote;
    }
}
