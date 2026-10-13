using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.EntityFrameworkCore;
using System.Reflection;
using System.Security.Claims;
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
    public async Task 选中比价行转采购订单_币种无法识别_拒绝且不回退人民币()
    {
        using var db = TestDbFactory.Create();
        var quote = SeedSelectedQuote(db, "PQ-CUR-1");
        quote.Currency = "RUB";
        db.SaveChanges();

        // ERP-418：无法识别的币种一律拒绝转换（绝不回退 CNY，避免把原币金额静默改写成另一币种）。
        var ex = await Assert.ThrowsAsync<BusinessException>(() => NewController(db).ToPurchaseOrder(quote.Id));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Contains("币种无法识别", ex.Message);
        Assert.Empty(db.PurchaseOrders);
        Assert.Equal(PurchaseQuoteConversion.SelectedStatus, db.PurchaseQuotes.Single().Status);
    }

    [Fact]
    public async Task 比价行未引用档案商品_转采购订单_在发号与写入之前受控拒绝且绝不零占位()
    {
        using var db = TestDbFactory.Create();
        var rule = SeedPurchaseOrderNumberRule(db, 7);
        var quote = SeedSelectedQuote(db, "PQ-NO-PRODUCT");
        quote.ProductId = null;                       // 手工比价草稿：写入合法，但采购承诺必须显式引用商品
        quote.ProductName = "档口现货（无档案）";
        db.SaveChanges();

        // ERP-491：进程内直调同样执行承诺级实时主数据复核 —— 商品引用缺失（ProductId 非正整数）
        // 绝不静默零占位，一律在采购单号预约与任何写入之前受控拒绝，来源状态一行不改。
        var ex = await Assert.ThrowsAsync<BusinessException>(() => NewController(db).ToPurchaseOrder(quote.Id));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains(PurchaseQuoteMasterReferenceRules.CommitmentProductText, ex.Message);
        Assert.Empty(db.PurchaseOrders);
        Assert.Empty(db.PurchaseOrderDetails);
        Assert.Equal(7L, PurchaseOrderSequence(db, rule.Id));
        var source = db.PurchaseQuotes.AsNoTracking().Single();
        Assert.Equal(PurchaseQuoteConversion.SelectedStatus, source.Status);
        Assert.Equal(string.Empty, source.RefOrderNo ?? string.Empty);
    }

    // ==================== ERP-476：真实 HTTP 承诺请求的实时主数据引用 ====================

    [Fact]
    public async Task HTTP承诺_商品引用缺失_在发号与写入之前拒绝且绝不零占位()
    {
        using var db = TestDbFactory.Create();
        var supplier = SeedLiveSupplier(db);
        var rule = SeedPurchaseOrderNumberRule(db, 7);
        var quote = SeedSelectedQuote(db, "PQ-476-HTTP-NO-PRODUCT");
        quote.SupplierId = supplier.Id;
        quote.SupplierName = supplier.SupplierName;
        quote.ProductId = null;                     // 手工比价行：真实承诺绝不静默写 ProductId = 0
        db.SaveChanges();

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            HttpController(db).ToPurchaseOrder(quote.Id));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains(PurchaseQuoteMasterReferenceRules.CommitmentProductText, ex.Message);
        Assert.Empty(db.PurchaseOrders);
        Assert.Empty(db.PurchaseOrderDetails);
        Assert.Equal(7L, PurchaseOrderSequence(db, rule.Id));            // 被拒绝的承诺绝不消耗采购单号
        var source = db.PurchaseQuotes.AsNoTracking().Single();
        Assert.Equal(PurchaseQuoteConversion.SelectedStatus, source.Status);
        Assert.Equal(string.Empty, source.RefOrderNo ?? string.Empty);   // 来源状态与留痕不变
    }

    [Fact]
    public async Task HTTP承诺_主数据失效_受控拒绝且零写入零发号()
    {
        using var db = TestDbFactory.Create();
        var supplier = SeedLiveSupplier(db);
        var product = SeedLiveProduct(db);
        var customer = SeedLiveCustomer(db);
        var rule = SeedPurchaseOrderNumberRule(db, 7);
        var quote = SeedSelectedQuote(db, "PQ-476-HTTP-OFFLINE");
        quote.SupplierId = supplier.Id;
        quote.SupplierName = supplier.SupplierName;
        quote.ProductId = product.Id;
        quote.CustomerId = customer.Id;
        db.SaveChanges();

        supplier.Status = 0;                        // 审批 / 选中后供应商档案被停用
        db.SaveChanges();

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            HttpController(db).ToPurchaseOrder(quote.Id));

        Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
        Assert.Contains(PurchaseQuoteConversionMutationRules.SupplierMasterText, ex.Message);
        Assert.Empty(db.PurchaseOrders);
        Assert.Equal(7L, PurchaseOrderSequence(db, rule.Id));
    }

    [Fact]
    public async Task HTTP承诺_合法实时主数据_正向生成且明细引用实时商品()
    {
        using var db = TestDbFactory.Create();
        var supplier = SeedLiveSupplier(db);
        var product = SeedLiveProduct(db);
        var customer = SeedLiveCustomer(db);
        var rule = SeedPurchaseOrderNumberRule(db, 7);
        var quote = SeedSelectedQuote(db, "PQ-476-HTTP-LIVE");
        quote.SupplierId = supplier.Id;
        quote.SupplierName = supplier.SupplierName;
        quote.ProductId = product.Id;
        quote.CustomerId = customer.Id;
        // 既有一致性契约（ERP-417）：已批准决定的选中供应商必须与当前供应商一致。
        db.PurchaseQuoteDecisions.Single(d => d.QuoteId == quote.Id).SelectedSupplierId = supplier.Id;
        db.SaveChanges();

        var ok = Assert.IsType<OkObjectResult>(await HttpController(db).ToPurchaseOrder(quote.Id));

        Assert.IsType<ApiResponse<PurchaseOrderConversionResult>>(ok.Value);
        var order = db.PurchaseOrders.Single();
        Assert.Equal(supplier.Id, order.SupplierId);
        Assert.Equal(customer.Id, order.OwningCustomerId);
        Assert.Equal(product.Id, db.PurchaseOrderDetails.Single().ProductId);
        Assert.Equal(8L, PurchaseOrderSequence(db, rule.Id));
        Assert.Equal(order.OrderNo, db.PurchaseQuotes.AsNoTracking().Single().RefOrderNo);
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
    public async Task 比价行行金额超出存储精度_转采购订单_复用权威校验拒绝()
    {
        using var db = TestDbFactory.Create();
        var quote = SeedSelectedQuote(db, "PQ-AMOUNT-PRECISION");
        // 0.25 × 1.05 = 0.2625：旧的带入路径会 Math.Round 静默取整，现复用
        // PurchaseOrderAmountRules 唯一权威校验，绝不写入与明细合计不一致的金额。
        quote.Quantity = 0.25m;
        quote.QuotePrice = 1.05m;
        db.SaveChanges();

        var ex = await Assert.ThrowsAsync<BusinessException>(() => NewController(db).ToPurchaseOrder(quote.Id));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Contains("行金额", ex.Message);
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
    public void 币种文本解析_大小写不敏感_且仅接受受支持口径(string? text, Currency expected)
        => Assert.Equal(expected, PurchaseQuoteConversion.ParseCurrency(text));

    [Theory]
    [InlineData("RUB")]
    [InlineData("EUR")]
    [InlineData("0")]
    [InlineData("")]
    [InlineData(null)]
    public void 币种文本无法识别_拒绝而非回退人民币(string? text)
    {
        // ERP-418：无法识别一律拒绝（InvalidParameter），绝不回退人民币，也不接受纯数字枚举值。
        var ex = Assert.Throws<BusinessException>(() => PurchaseQuoteConversion.ParseCurrency(text));
        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Contains("币种无法识别", ex.Message);
        Assert.False(PurchaseQuoteConversion.TryParseCurrency(text, out _));
    }

    // ==================== 工厂与种子数据 ====================

    /// <summary>ERP-486：既有进程内夹具改为真实启用身份 + 既有「供应商比价」/「采购订单」菜单授权（刻意不设置 <c>Request.Path</c>）。</summary>
    private static PurchaseQuoteController NewController(ErpDbContext db)
        => PurchaseQuoteTestAuthority.NewQuoteController(db);

    // ==================== ERP-476：真实 HTTP 承诺请求形状的控制器与实时主数据 ====================

    /// <summary>
    /// ERP-476：真实 HTTP 承诺请求形状的控制器（<c>Request.Path</c> 已赋值 → 实时主数据引用复核执行；
    /// 特权账号 + 既有「供应商比价」/「采购订单」菜单 → 既有授权放行；不新增 / 不放宽任何授权）。
    /// </summary>
    private static PurchaseQuoteController HttpController(ErpDbContext db)
    {
        var role = new SysRole
        {
            RoleName = $"erp476-p-{Guid.NewGuid():N}", RoleCode = $"erp476-p-{Guid.NewGuid():N}",
            IsSystem = true
        };
        db.SysRoles.Add(role);
        db.SaveChanges();
        var user = new SysUser
        {
            UserName = $"erp476-p-{Guid.NewGuid():N}", PasswordHash = "hash", PasswordSalt = "salt",
            DisplayName = "ERP476 特权账号", Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        db.SaveChanges();
        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        db.SaveChanges();
        foreach (var menuCode in new[]
                 {
                     PurchaseQuoteAuthorizationRules.RequiredMenuCode,
                     PurchaseQuoteAuthorizationRules.DestinationMenuCode
                 })
        {
            var menu = db.SysMenus.FirstOrDefault(m => m.MenuCode == menuCode && !m.IsDeleted);
            if (menu is null)
            {
                menu = new SysMenu { MenuName = menuCode, MenuCode = menuCode, MenuType = MenuType.Menu };
                db.SysMenus.Add(menu);
                db.SaveChanges();
            }
            db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
            db.SaveChanges();
        }

        var controller = new PurchaseQuoteController(new GenericService<PurchaseQuote>(db), db,
            new DocumentNumberService(db));
        var http = new DefaultHttpContext();
        http.Request.Path = "/api/purchase/quotes";
        http.User = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()) }, "Test"));
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        return controller;
    }

    /// <summary>
    /// ERP-491：为既有转换夹具补齐与占位 Id 对齐的**真实启用**主数据行（供应商 88 / 商品 310 / 客户 7）。
    /// 幂等：已存在即复用，绝不新增第二行、绝不回填 / 改派既有引用，也绝不新增任何权限。
    /// </summary>
    private static void SeedLiveMasterRows(ErpDbContext db)
    {
        if (!db.BaseSuppliers.Any(s => s.Id == 88L))
            db.BaseSuppliers.Add(new BaseSupplier
            {
                Id = 88L, SupplierCode = "S-491-88", SupplierName = "义乌档口 A", Status = 1
            });
        if (!db.BaseProducts.Any(p => p.Id == 310L))
            db.BaseProducts.Add(new BaseProduct
            {
                Id = 310L, ProductCode = "P-491-310", ProductName = "PU 商品", Unit = "PCS", Status = 1
            });
        if (!db.BaseCustomers.Any(c => c.Id == 7L))
            db.BaseCustomers.Add(new BaseCustomer
            {
                Id = 7L, CustomerCode = "C-491-7", CustomerName = "义乌客户（比价测试）", Status = 1,
                CreditStatus = "正常", DepositRatio = 30m
            });
        db.SaveChanges();
    }

    private static BaseSupplier SeedLiveSupplier(ErpDbContext db)
    {
        var supplier = new BaseSupplier
        {
            SupplierCode = $"S-476-{Guid.NewGuid():N}", SupplierName = "ERP476 实时供应商", Status = 1
        };
        db.BaseSuppliers.Add(supplier);
        db.SaveChanges();
        return supplier;
    }

    private static BaseProduct SeedLiveProduct(ErpDbContext db)
    {
        var product = new BaseProduct
        {
            ProductCode = $"P-476-{Guid.NewGuid():N}", ProductName = "ERP476 实时商品", Unit = "PCS", Status = 1
        };
        db.BaseProducts.Add(product);
        db.SaveChanges();
        return product;
    }

    private static BaseCustomer SeedLiveCustomer(ErpDbContext db)
    {
        var customer = new BaseCustomer
        {
            CustomerCode = $"C-476-{Guid.NewGuid():N}", CustomerName = "ERP476 实时客户", Status = 1,
            CreditStatus = "正常", DepositRatio = 30m
        };
        db.BaseCustomers.Add(customer);
        db.SaveChanges();
        return customer;
    }

    private static SysDocumentNumberRule SeedPurchaseOrderNumberRule(ErpDbContext db, long current)
    {
        var rule = new SysDocumentNumberRule
        {
            DocumentType = DocumentType.PurchaseOrder, RuleCode = $"PO-{Guid.NewGuid():N}", RuleName = "采购订单单号",
            Prefix = "PO", DateFormat = "yyyyMMdd", SerialLength = 4, Separator = string.Empty,
            CurrentSequence = current, IsDeleted = false
        };
        db.SysDocumentNumberRules.Add(rule);
        db.SaveChanges();
        return rule;
    }

    private static long PurchaseOrderSequence(ErpDbContext db, long ruleId)
        => db.SysDocumentNumberRules.AsNoTracking().Single(r => r.Id == ruleId).CurrentSequence;

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
        // ERP-491：采购承诺（转采购订单 / 带入预填）夹具必须引用**真实存在且启用**的主数据行 ——
        // 复核与调用形状无关后，进程内直调不再跳过实时主数据门；占位 Id（88 / 310 / 7）与真实启用行对齐，
        // 绝不提供请求路径 / 环境 / 测试专用旁路。
        SeedLiveMasterRows(db);
        db.PurchaseQuotes.Add(quote);
        db.SaveChanges();

        // ERP-095：比价行需先「批准选中」才能转采购订单
        db.PurchaseQuoteDecisions.Add(new PurchaseQuoteDecision
        {
            QuoteId = quote.Id,
            QuoteNo = quote.QuoteNo,
            Decision = PurchaseQuoteApproval.Approved,
            SelectedSupplierId = quote.SupplierId,
            SelectedSupplierName = quote.SupplierName,
            DecisionBasis = "单价最优",
            DecidedBy = 1L,
            DecidedByName = "审批人",
            DecidedAt = DateTime.Now,
            DecisionRef = PurchaseQuoteApproval.DecisionRef(quote)
        });
        db.SaveChanges();
        return quote;
    }
}
