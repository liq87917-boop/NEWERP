using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.DTOs;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.EntityFrameworkCore;
using NPOI.SS.UserModel;
using NPOI.XSSF.UserModel;
using System.Reflection;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// 单证明细行快照的**生成 / 导出 / 打印**单元测试（ERP-052）。覆盖：
/// 销售订单 → 商业发票（订单明细权威值 + 服务端计算行金额 + 币种精度）、装箱单（箱数与重量留空）；
/// 装柜清单 → 装箱单（数量 / 箱数 / 毛重写入，**净重留空**、来源 0 与非整数箱数不写成 0）；
/// 来源明细非法 / 超限与缺少商品身份一律拒绝且不写半成品数据；生成后来源与商品资料变化不影响快照；
/// 重复生成守卫不追加明细行；老单证（无明细行）照常导出与打印；Excel 明细行布局的顺序 / 合计 / 空白语义；
/// 共享打印的类型适配列、币种分开合计与非变更边界；纯文本安全处理与有界查询；接口路由与前端接线契约。
/// <para>全部使用内存库（<see cref="TestDbFactory"/>），不连接 SQL Server、不执行任何 SQL / 部署脚本、
/// 不启动 API、不运行浏览器验收（浏览器验收按项目策略延后到 FINAL-UI-ACCEPTANCE）。</para>
/// </summary>
public class TradeDocumentLineSnapshotTests
{
    private const string 商业发票 = "商业发票";
    private const string 装箱单 = "装箱单";
    private const string 报关单 = "报关单";

    // ==================== 0. 测试脚手架 ====================

    private static SalesOrderController SalesOrderCtl(ErpDbContext db)
        => new(db, new DocumentNumberService(db));

    private static ContainerLoadingListController LoadingListCtl(ErpDbContext db)
        => new(db, new DocumentNumberService(db));

    private static TradeDocumentController TradeDocCtl(ErpDbContext db)
        => new(new GenericService<TradeDocument>(db), db);

    /// <summary>断言成功响应并取出数据（业务码必须为 0）</summary>
    private static T DataOf<T>(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var resp = Assert.IsType<ApiResponse<T>>(ok.Value);
        Assert.Equal(ErrorCodes.Success, resp.Code);
        Assert.NotNull(resp.Data);
        return resp.Data!;
    }

    /// <summary>断言业务异常的错误码并返回异常（避免只断言消息文案）</summary>
    private static async Task<BusinessException> AssertBusinessAsync(int expectedCode, Func<Task> action)
    {
        var ex = await Assert.ThrowsAsync<BusinessException>(action);
        Assert.Equal(expectedCode, ex.Code);
        return ex;
    }

    private static BaseCustomer SeedCustomer(ErpDbContext db, string currency = "EUR")
    {
        var customer = new BaseCustomer
        {
            CustomerCode = "C-052",
            CustomerName = "义乌客户（明细测试）",
            Currency = currency,
            DestinationPort = "HAMBURG",
        };
        db.BaseCustomers.Add(customer);
        db.SaveChanges();
        return customer;
    }

    private static BaseProduct SeedProduct(ErpDbContext db, string code, string nameCn,
        string nameEn = "Cotton Towel", string spec = "70x140cm", string unit = "箱", bool deleted = false)
    {
        var product = new BaseProduct
        {
            ProductCode = code,
            ProductName = nameCn,
            EnglishName = nameEn,
            Spec = spec,
            Unit = unit,
            Status = 1,
            IsDeleted = deleted,
        };
        db.BaseProducts.Add(product);
        db.SaveChanges();
        return product;
    }

    private static SalesOrder SeedSalesOrder(ErpDbContext db, string orderNo, long customerId,
        string currency = "USD",
        params (long ProductId, string Name, string Spec, decimal Quantity, string Unit, decimal UnitPrice)[] details)
    {
        var order = new SalesOrder
        {
            OrderNo = orderNo,
            OrderDate = new DateTime(2026, 9, 22),
            CustomerId = customerId,
            Currency = Enum.Parse<Currency>(currency),
            TotalAmount = details.Sum(d => d.Quantity * d.UnitPrice),
            DestinationPort = "HAMBURG",
            Status = DocumentStatus.Pending,
        };
        db.SalesOrders.Add(order);
        db.SaveChanges();

        foreach (var d in details)
            db.SalesOrderDetails.Add(new SalesOrderDetail
            {
                SalesOrderId = order.Id,
                ProductId = d.ProductId,
                ProductName = d.Name,
                Spec = d.Spec,
                Quantity = d.Quantity,
                Unit = d.Unit,
                UnitPrice = d.UnitPrice,
                Amount = d.Quantity * d.UnitPrice,
            });
        db.SaveChanges();
        return order;
    }

    private static ContainerLoadingList SeedLoadingList(ErpDbContext db, string loadingListNo, long customerId,
        string containerNo = "CTN-052",
        params (long ProductId, string Name, decimal Quantity, decimal Cartons, decimal Weight)[] details)
    {
        var list = new ContainerLoadingList
        {
            LoadingListNo = loadingListNo,
            LoadingDate = new DateTime(2026, 9, 24),
            ContainerNo = containerNo,
            CustomerId = customerId,
            Status = DocumentStatus.Pending,
            TotalCartons = details.Sum(d => d.Cartons),
            TotalWeight = details.Sum(d => d.Weight),
        };
        db.ContainerLoadingLists.Add(list);
        db.SaveChanges();

        foreach (var d in details)
            db.ContainerLoadingDetails.Add(new ContainerLoadingDetail
            {
                LoadingListId = list.Id,
                ProductId = d.ProductId,
                ProductName = d.Name,
                Quantity = d.Quantity,
                Cartons = d.Cartons,
                Weight = d.Weight,
            });
        db.SaveChanges();
        return list;
    }

    /// <summary>按行序读取某单证的明细行（只读）</summary>
    private static List<TradeDocumentItem> LinesOf(ErpDbContext db, long documentId)
        => db.TradeDocumentItems.AsNoTracking()
            .Where(i => !i.IsDeleted && i.TradeDocumentId == documentId)
            .OrderBy(i => i.LineNo).ThenBy(i => i.Id)
            .ToList();

    /// <summary>读取导出的 xlsx 第一个工作表</summary>
    private static ISheet ReadSheet(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        var workbook = new XSSFWorkbook(ms);
        return workbook.GetSheetAt(0);
    }

    /// <summary>读取单元格文本（数值单元格按 InvariantCulture 输出；空白单元格返回空串）</summary>
    private static string CellText(ISheet sheet, int row, int column)
    {
        var cell = sheet.GetRow(row)?.GetCell(column);
        if (cell is null) return string.Empty;
        return cell.CellType switch
        {
            CellType.String => cell.StringCellValue,
            CellType.Numeric => cell.NumericCellValue.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            CellType.Blank => string.Empty,
            _ => cell.ToString() ?? string.Empty,
        };
    }

    private static string JsDirectory() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "..", "..", "..", "..", "..", "src", "ERP.Api", "wwwroot", "js"));

    /// <summary>断言控制器方法上存在指定路由模板</summary>
    private static void AssertRouteTemplate(Type controller, string methodName, string expectedTemplate)
    {
        var method = controller.GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(method);
        var templates = method!.GetCustomAttributes<HttpMethodAttribute>(true)
            .Select(a => a.Template ?? string.Empty)
            .ToList();
        Assert.Contains(expectedTemplate, templates);
    }

    // ==================== 1. 销售订单 → 商业发票 / 装箱单 明细行 ====================

    [Fact]
    public async Task 销售订单生成商业发票与装箱单_明细行取订单明细_行金额服务端计算()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var towel = SeedProduct(db, "P-052-1", "毛巾（商品资料）", "Cotton Towel", "70x140（资料）", "箱");
        var order = SeedSalesOrder(db, "SO-052-1", customer.Id,
            details: new[]
            {
                (towel.Id, "毛巾（订单快照）", "70x140（订单）", 10m, "箱", 2.5m),
                (0L, "浴巾-B", "80x160", 3m, "打", 1.005m),
            });

        var result = DataOf<TradeDocGenerateResult>(await SalesOrderCtl(db).GenerateTradeDocuments(order.Id, null));

        Assert.Equal(2, result.Documents.Count);
        Assert.Equal(2, result.Documents.Single(d => d.DocType == 商业发票).LineCount);
        Assert.Equal(4, result.TotalLineCount);                       // 商业发票 2 行 + 装箱单 2 行
        Assert.Contains("已由销售订单带入 2 行", result.Documents[0].LineSummaryText);

        var ci = db.TradeDocuments.Single(d => d.DocType == 商业发票);
        var lines = LinesOf(db, ci.Id);
        Assert.Equal(new[] { 1, 2 }, lines.Select(l => l.LineNo).ToArray());

        var first = lines[0];
        Assert.Equal(towel.Id, first.ProductId);
        Assert.Equal("P-052-1", first.ProductCode);                    // 编码取商品资料权威快照
        Assert.Equal("毛巾（订单快照）", first.ProductNameCn);          // 名称 / 规格 / 单位以订单明细为准
        Assert.Equal("70x140（订单）", first.Spec);
        Assert.Equal("Cotton Towel", first.ProductNameEn);
        Assert.Equal("箱", first.Unit);
        Assert.Equal(10m, first.Quantity);
        Assert.Equal(2.5m, first.UnitPrice);
        Assert.Equal(25m, first.LineAmount);
        Assert.Equal("USD", first.Currency);                           // 币种与单证台账同口径
        Assert.Null(first.PackageCount);                               // 销售订单没有箱数 / 重量证据
        Assert.Null(first.NetWeight);
        Assert.Null(first.GrossWeight);

        var second = lines[1];
        Assert.Equal(0L, second.ProductId);
        Assert.Equal("浴巾-B", second.ProductNameCn);
        Assert.Equal(3.02m, second.LineAmount);                        // 3 × 1.005 按币种精度取整

        // 装箱单：同样带入行，但不含价格口径、箱数与重量留空
        var pl = db.TradeDocuments.Single(d => d.DocType == 装箱单);
        var plLines = LinesOf(db, pl.Id);
        Assert.Equal(2, plLines.Count);
        Assert.All(plLines, l => Assert.Equal(0m, l.UnitPrice));
        Assert.All(plLines, l => Assert.Equal(0m, l.LineAmount));
        Assert.All(plLines, l => Assert.Null(l.PackageCount));
        Assert.All(plLines, l => Assert.Null(l.NetWeight));
        Assert.All(plLines, l => Assert.Null(l.GrossWeight));
        Assert.Equal(10m, plLines[0].Quantity);

        // 非变更边界：来源订单明细与商品资料保持原样，不产生库存 / 财务记录
        var detail = await db.SalesOrderDetails.AsNoTracking().OrderBy(d => d.Id).FirstAsync();
        Assert.Equal(10m, detail.Quantity);
        Assert.Equal(2.5m, detail.UnitPrice);
        Assert.Equal("毛巾（订单快照）", detail.ProductName);
        Assert.False(detail.IsDeleted);
        var savedProduct = await db.BaseProducts.AsNoTracking().SingleAsync(p => p.Id == towel.Id);
        Assert.Equal("P-052-1", savedProduct.ProductCode);
        Assert.Equal("毛巾（商品资料）", savedProduct.ProductName);
        Assert.Empty(db.StockMovements);
        Assert.Empty(db.StockOuts);
        Assert.Empty(db.FinanceReceipts);
    }

    [Fact]
    public async Task 销售订单带入预填_返回明细行预览且不落库()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var towel = SeedProduct(db, "P-052-3", "毛巾", "Cotton Towel", "70x140", "箱");
        var order = SeedSalesOrder(db, "SO-052-3", customer.Id,
            details: new[]
            {
                (towel.Id, "毛巾（订单快照）", "70x140", 10m, "箱", 2.5m),
                (0L, "浴巾-B", "80x160", 3m, "打", 1m),
            });

        var prefill = DataOf<TradeDocPrefillResult>(await SalesOrderCtl(db).TradeDocumentPrefill(order.Id));

        Assert.Empty(db.TradeDocuments);                               // 预填不落库
        Assert.Empty(db.TradeDocumentItems);
        Assert.Equal(2, prefill.LineCounts[商业发票]);
        Assert.Equal(2, prefill.LineCounts[装箱单]);
        Assert.Equal(0, prefill.LineCounts[报关单]);

        var ciPreview = prefill.LinePreviews.Where(l => l.DocType == 商业发票).ToList();
        Assert.Equal(new[] { 1, 2 }, ciPreview.Select(l => l.LineNo).ToArray());
        Assert.Equal("2.50", ciPreview[0].UnitPriceText);
        Assert.Equal("25.00", ciPreview[0].LineAmountText);
        Assert.True(ciPreview[0].HasPricing);
        Assert.False(ciPreview[0].HasPackaging);
        Assert.Equal(string.Empty, ciPreview[0].PackageCountText);     // 未登记 → 空，不是 0

        var plPreview = prefill.LinePreviews.First(l => l.DocType == 装箱单);
        Assert.False(plPreview.HasPricing);
        Assert.True(plPreview.HasPackaging);
        Assert.Equal(string.Empty, plPreview.NetWeightText);
        Assert.Equal(string.Empty, plPreview.GrossWeightText);

        Assert.Contains("已由销售订单带入", prefill.LineSummaryText);
        Assert.Contains("不写成 0", prefill.LineEvidenceText);
        Assert.Equal(TradeDocumentPrintSemantics.DetailRuleText, prefill.LineRuleText);
    }

    // ==================== 2. 生成守卫：来源非法 / 超限 / 重复生成 ====================

    [Fact]
    public async Task 销售订单明细数量非法_拒绝生成且不写任何单证与明细行()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var product = SeedProduct(db, "P-052-4", "毛巾");
        var order = SeedSalesOrder(db, "SO-052-4", customer.Id,
            details: new[] { (product.Id, "毛巾", "70x140", 10m, "箱", 2.5m) });

        var detail = await db.SalesOrderDetails.SingleAsync();
        detail.Quantity = 0m;                                          // 来源明细数量非法
        await db.SaveChangesAsync();

        var error = await AssertBusinessAsync(ErrorCodes.RuleConflict,
            () => SalesOrderCtl(db).GenerateTradeDocuments(order.Id, null));

        Assert.Contains("数量", error.Message);
        Assert.Contains("不静默丢弃", error.Message);
        Assert.Empty(db.TradeDocuments);                                // 不产生半成品单证
        Assert.Empty(db.TradeDocumentItems);
    }

    [Fact]
    public async Task 销售订单明细缺少商品身份_拒绝生成且不写任何单证()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var order = SeedSalesOrder(db, "SO-052-5", customer.Id,
            details: new[] { (0L, string.Empty, string.Empty, 5m, string.Empty, 1m) });

        var error = await AssertBusinessAsync(ErrorCodes.RuleConflict,
            () => SalesOrderCtl(db).GenerateTradeDocuments(order.Id, null));

        Assert.Contains("商品", error.Message);
        Assert.Empty(db.TradeDocuments);
        Assert.Empty(db.TradeDocumentItems);
    }

    [Fact]
    public async Task 销售订单明细超过有界行数_拒绝生成且不截断写入()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var product = SeedProduct(db, "P-052-6", "毛巾");
        var order = SeedSalesOrder(db, "SO-052-6", customer.Id,
            details: new[] { (product.Id, "毛巾", "70x140", 1m, "箱", 1m) });

        for (var index = 1; index <= TradeDocumentItemRules.MaxLinesPerDocument; index++)
            db.SalesOrderDetails.Add(new SalesOrderDetail
            {
                SalesOrderId = order.Id,
                ProductId = product.Id,
                ProductName = $"毛巾-{index}",
                Quantity = 1m,
                Unit = "箱",
                UnitPrice = 1m,
            });
        await db.SaveChangesAsync();

        var error = await AssertBusinessAsync(ErrorCodes.RuleConflict,
            () => SalesOrderCtl(db).GenerateTradeDocuments(order.Id, null));

        Assert.Contains($"{TradeDocumentItemRules.MaxLinesPerDocument}", error.Message);
        Assert.Empty(db.TradeDocuments);
        Assert.Empty(db.TradeDocumentItems);

        // 预填同样拒绝（让用户在落库前就看到问题），且不落库
        await AssertBusinessAsync(ErrorCodes.RuleConflict,
            () => SalesOrderCtl(db).TradeDocumentPrefill(order.Id));
        Assert.Empty(db.TradeDocuments);
    }

    [Fact]
    public async Task 重复生成_守卫拒绝且不追加明细行()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var product = SeedProduct(db, "P-052-7", "毛巾");
        var order = SeedSalesOrder(db, "SO-052-7", customer.Id,
            details: new[] { (product.Id, "毛巾", "70x140", 2m, "箱", 5m) });

        await SalesOrderCtl(db).GenerateTradeDocuments(order.Id, null);
        var documents = db.TradeDocuments.Count();
        var lines = db.TradeDocumentItems.Count();
        Assert.Equal(2, documents);
        Assert.Equal(2, lines);

        var error = await AssertBusinessAsync(ErrorCodes.RuleConflict,
            () => SalesOrderCtl(db).GenerateTradeDocuments(order.Id, null));

        Assert.Contains("不能重复生成", error.Message);
        Assert.Equal(documents, db.TradeDocuments.Count());             // 不产生重复单证
        Assert.Equal(lines, db.TradeDocumentItems.Count());             // 也不追加明细行
    }

    [Fact]
    public async Task 生成后修改订单明细与商品资料_明细行快照保持生成当时的值()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var product = SeedProduct(db, "P-052-8", "毛巾（原）", "Towel", "70x140", "箱");
        var order = SeedSalesOrder(db, "SO-052-8", customer.Id,
            details: new[] { (product.Id, "毛巾（订单）", "70x140（订单）", 4m, "箱", 6m) });

        await SalesOrderCtl(db).GenerateTradeDocuments(order.Id, null);
        var ci = db.TradeDocuments.Single(d => d.DocType == 商业发票);
        var before = LinesOf(db, ci.Id).Single();

        var detail = await db.SalesOrderDetails.SingleAsync();
        detail.Quantity = 99m;
        detail.UnitPrice = 9.9m;
        detail.ProductName = "改名后的订单毛巾";
        var master = await db.BaseProducts.SingleAsync();
        master.ProductName = "改名后的商品资料";
        master.ProductCode = "P-CHANGED";
        master.Spec = "改后规格";
        await db.SaveChangesAsync();

        var after = LinesOf(db, ci.Id).Single();
        Assert.Equal(before.ProductNameCn, after.ProductNameCn);
        Assert.Equal(before.ProductCode, after.ProductCode);
        Assert.Equal(before.Spec, after.Spec);
        Assert.Equal(before.Unit, after.Unit);
        Assert.Equal(before.Quantity, after.Quantity);
        Assert.Equal(before.UnitPrice, after.UnitPrice);
        Assert.Equal(before.LineAmount, after.LineAmount);
        Assert.Equal(before.Currency, after.Currency);
        Assert.Equal(before.CreatedAt, after.CreatedAt);
    }

    [Fact]
    public async Task 商业发票行金额_按币种精度_日元零位小数()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var product = SeedProduct(db, "P-052-9", "毛巾");
        var order = SeedSalesOrder(db, "SO-052-9", customer.Id, currency: "JPY",
            details: new[] { (product.Id, "毛巾", "70x140", 3m, "箱", 100.5m) });

        var result = DataOf<TradeDocGenerateResult>(await SalesOrderCtl(db).GenerateTradeDocuments(order.Id,
            new TradeDocGenerateRequest { DocTypes = new List<string> { 商业发票 } }));

        Assert.Single(result.Documents);
        var ci = db.TradeDocuments.Single();
        Assert.Equal("JPY", ci.Currency);
        var line = LinesOf(db, ci.Id).Single();
        Assert.Equal(3m, line.Quantity);
        Assert.Equal(100.5m, line.UnitPrice);
        Assert.Equal(302m, line.LineAmount);                            // 301.5 → JPY 0 位，0.5 进位
    }

    [Fact]
    public async Task 非明细行类型的单证_不带入明细行且照常生成()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var product = SeedProduct(db, "P-052-10", "毛巾");
        var order = SeedSalesOrder(db, "SO-052-10", customer.Id,
            details: new[] { (product.Id, "毛巾", "70x140", 2m, "箱", 5m) });

        var result = DataOf<TradeDocGenerateResult>(await SalesOrderCtl(db).GenerateTradeDocuments(order.Id,
            new TradeDocGenerateRequest { DocTypes = new List<string> { 报关单 } }));

        var item = result.Documents.Single();
        Assert.Equal(报关单, item.DocType);
        Assert.Equal(0, item.LineCount);
        Assert.Equal(0, result.TotalLineCount);
        Assert.Empty(db.TradeDocumentItems);                            // 报关单不接受明细行
        Assert.Single(db.TradeDocuments);
    }

    // ==================== 3. 装柜清单 → 装箱单 明细行 ====================

    [Fact]
    public async Task 装柜清单生成装箱单_数量箱数毛重写入_净重留空()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var towel = SeedProduct(db, "P-052-11", "毛巾（商品资料）", "Cotton Towel", "70x140", "箱");
        var list = SeedLoadingList(db, "ZQ-052-1", customer.Id,
            details: new[] { (towel.Id, "毛巾（清单）", 10m, 5m, 12.5m) });

        var result = DataOf<TradeDocGenerateResult>(await LoadingListCtl(db).GenerateTradeDocuments(list.Id, null));

        var item = result.Documents.Single();
        Assert.Equal(装箱单, item.DocType);
        Assert.Equal(1, item.LineCount);

        var pl = db.TradeDocuments.Single();
        var line = LinesOf(db, pl.Id).Single();
        Assert.Equal(1, line.LineNo);
        Assert.Equal(towel.Id, line.ProductId);
        Assert.Equal("P-052-11", line.ProductCode);
        Assert.Equal("毛巾（清单）", line.ProductNameCn);                 // 清单明细自身的名称优先
        Assert.Equal("70x140", line.Spec);                               // 清单没有规格列 → 商品资料权威快照
        Assert.Equal("箱", line.Unit);                                   // 清单没有单位列 → 商品资料权威快照
        Assert.Equal(10m, line.Quantity);
        Assert.Equal(5, line.PackageCount);
        Assert.Equal(12.5m, line.GrossWeight);
        Assert.Null(line.NetWeight);                                     // 清单没有净重列 → 留空，不推断
        Assert.Equal(0m, line.UnitPrice);                                // 装箱单不含价格口径
        Assert.Equal(0m, line.LineAmount);
        Assert.Equal("EUR", line.Currency);                              // 币种与单证台账一致
        Assert.Contains("净重未登记 1 行", item.LineEvidenceText);
        Assert.Contains("不写成 0", item.LineEvidenceText);

        // 非变更边界：装柜清单明细与库存记录不受影响
        var detail = await db.ContainerLoadingDetails.AsNoTracking().SingleAsync();
        Assert.Equal(10m, detail.Quantity);
        Assert.Equal(5m, detail.Cartons);
        Assert.Equal(12.5m, detail.Weight);
        Assert.False(detail.IsDeleted);
        Assert.Empty(db.StockMovements);
        Assert.Empty(db.StockInDetails);
    }

    [Fact]
    public async Task 装柜清单箱数非整数或为零_箱数留空不四舍五入也不写成零()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var towel = SeedProduct(db, "P-052-12", "毛巾");
        var list = SeedLoadingList(db, "ZQ-052-2", customer.Id,
            details: new[]
            {
                (towel.Id, "毛巾-A", 2m, 3.5m, 0m),      // 非整数箱数 + 毛重未登记
                (towel.Id, "毛巾-B", 1m, 0m, 8m),        // 箱数未登记（来源 0） + 毛重已登记
            });

        var result = DataOf<TradeDocGenerateResult>(await LoadingListCtl(db).GenerateTradeDocuments(list.Id, null));

        var pl = db.TradeDocuments.Single();
        var lines = LinesOf(db, pl.Id);
        Assert.Equal(2, lines.Count);

        Assert.Null(lines[0].PackageCount);                              // 3.5 箱：不四舍五入
        Assert.Null(lines[0].GrossWeight);                               // 来源 0 = 未登记
        Assert.Null(lines[0].NetWeight);

        Assert.Null(lines[1].PackageCount);                              // 来源 0 = 未登记
        Assert.Equal(8m, lines[1].GrossWeight);
        Assert.Null(lines[1].NetWeight);

        Assert.Single(result.Documents);
        Assert.Equal(2, result.Documents.Single().LineCount);
    }

    [Fact]
    public async Task 装柜清单预填_返回明细行预览且不落库_明细超限则拒绝()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var towel = SeedProduct(db, "P-052-13", "毛巾");
        var list = SeedLoadingList(db, "ZQ-052-3", customer.Id,
            details: new[] { (towel.Id, "毛巾", 10m, 5m, 12.5m) });

        var prefill = DataOf<TradeDocPrefillResult>(await LoadingListCtl(db).TradeDocumentPrefill(list.Id));

        Assert.Empty(db.TradeDocuments);                                 // 预填不落库
        Assert.Empty(db.TradeDocumentItems);
        Assert.Equal(1, prefill.LineCounts[装箱单]);
        var preview = prefill.LinePreviews.Single();
        Assert.Equal(装箱单, preview.DocType);
        Assert.Equal("5", preview.PackageCountText);
        Assert.Equal("12.5", preview.GrossWeightText);
        Assert.Equal(string.Empty, preview.NetWeightText);               // 未登记 → 空，不是 0
        Assert.Equal(string.Empty, preview.UnitPriceText);               // 装箱单不含价格口径

        // 清单明细超过有界上限：生成直接拒绝（不截断写入）
        for (var index = 1; index <= TradeDocumentItemRules.MaxLinesPerDocument; index++)
            db.ContainerLoadingDetails.Add(new ContainerLoadingDetail
            {
                LoadingListId = list.Id,
                ProductId = towel.Id,
                ProductName = $"毛巾-{index}",
                Quantity = 1m,
                Cartons = 1m,
                Weight = 1m,
            });
        await db.SaveChangesAsync();

        var error = await AssertBusinessAsync(ErrorCodes.RuleConflict,
            () => LoadingListCtl(db).GenerateTradeDocuments(list.Id, null));
        Assert.Contains("装柜清单", error.Message);
        Assert.Empty(db.TradeDocuments);
        Assert.Empty(db.TradeDocumentItems);
    }

    // ==================== 4. 共享打印：明细行与合计 ====================

    [Fact]
    public async Task 打印接口_商业发票_返回有序明细行与币种分开的行金额合计()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var product = SeedProduct(db, "P-052-14", "毛巾", "Cotton Towel", "70x140", "箱");
        var order = SeedSalesOrder(db, "SO-052-14", customer.Id,
            details: new[]
            {
                (product.Id, "毛巾-A", "70x140", 10m, "箱", 2.5m),
                (product.Id, "毛巾-B", "80x160", 2m, "箱", 12.25m),
            });
        await SalesOrderCtl(db).GenerateTradeDocuments(order.Id,
            new TradeDocGenerateRequest { DocTypes = new List<string> { 商业发票 } });
        var ci = db.TradeDocuments.Single();

        var model = DataOf<TradeDocumentPrintModel>(await TradeDocCtl(db).GetPrint(ci.Id));

        Assert.True(model.HasDetailLines);
        Assert.False(model.DetailLinesTruncated);
        Assert.Equal(TradeDocumentPrintSemantics.CommercialInvoiceLineColumns.Select(c => c.Key).ToList(),
            model.DetailColumns.Select(c => c.Key).ToList());             // 类型适配：商业发票列
        Assert.Equal(new[] { 1, 2 }, model.DetailLines.Select(l => l.LineNo).ToArray());
        Assert.Equal("25.00", model.DetailLines[0].LineAmountText);
        Assert.Equal("2.50", model.DetailLines[0].UnitPriceText);
        Assert.Equal("毛巾-A", model.DetailLines[0].ProductNameCn);
        Assert.Equal(string.Empty, model.DetailLines[0].PackageCountText);  // 商业发票行不含箱数
        Assert.Equal("24.50", model.DetailLines[1].LineAmountText);

        Assert.NotNull(model.DetailTotals);
        var total = model.DetailTotals!.AmountByCurrency.Single();
        Assert.Equal("USD", total.Currency);
        Assert.Equal(49.50m, total.Amount);
        Assert.Equal(2, total.LineCount);
        Assert.Equal("49.50", total.AmountText);
        Assert.False(model.DetailTotals.MixedCurrency);
        Assert.Null(model.DetailTotals.PackageCountTotal);              // 商业发票行不参与箱数合计
        Assert.Equal(2, model.DetailTotals.LineCount);
        Assert.Equal(TradeDocumentPrintSemantics.DetailRuleText, model.DetailTotals.RuleText);

        // 只读：打印不改动明细行与单证表头
        var stored = LinesOf(db, ci.Id);
        Assert.Equal(2, stored.Count);
        Assert.Equal(25m, stored[0].LineAmount);
        Assert.Equal(12.25m, stored[1].UnitPrice);
        Assert.Equal(49.50m, db.TradeDocuments.AsNoTracking().Single(d => d.Id == ci.Id).Amount);
    }

    [Fact]
    public async Task 打印接口_装箱单_缺箱数与重量为空文本而不是零()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var product = SeedProduct(db, "P-052-15", "毛巾");
        var list = SeedLoadingList(db, "ZQ-052-4", customer.Id,
            details: new[]
            {
                (product.Id, "毛巾-A", 4m, 3m, 9m),
                (product.Id, "毛巾-B", 1m, 0m, 0m),        // 箱数与毛重均未登记
            });
        await LoadingListCtl(db).GenerateTradeDocuments(list.Id, null);
        var pl = db.TradeDocuments.Single();

        var model = DataOf<TradeDocumentPrintModel>(await TradeDocCtl(db).GetPrint(pl.Id));

        Assert.True(model.HasDetailLines);
        Assert.Equal(TradeDocumentPrintSemantics.PackingListLineColumns.Select(c => c.Key).ToList(),
            model.DetailColumns.Select(c => c.Key).ToList());             // 类型适配：装箱单列（无价格列）
        Assert.DoesNotContain(model.DetailColumns, c => c.Key == TradeDocumentPrintSemantics.UnitPrice);

        Assert.Equal("3", model.DetailLines[0].PackageCountText);
        Assert.Equal("9", model.DetailLines[0].GrossWeightText);
        Assert.Equal(string.Empty, model.DetailLines[0].NetWeightText);   // 净重来源未提供
        Assert.Equal(string.Empty, model.DetailLines[1].PackageCountText);
        Assert.Equal(string.Empty, model.DetailLines[1].GrossWeightText);
        Assert.Equal(string.Empty, model.DetailLines[1].UnitPriceText);   // 装箱单不含价格口径

        var totals = model.DetailTotals!;
        Assert.Equal(1, totals.PackageCountRecordedLines);
        Assert.Equal(3, totals.PackageCountTotal);
        Assert.Equal(1, totals.WeightRecordedLines);
        Assert.Null(totals.NetWeightTotal);                              // 无行登记 → null，不是 0
        Assert.Equal(9m, totals.GrossWeightTotal);
        Assert.Empty(totals.AmountByCurrency);                           // 装箱单不参与金额合计
        Assert.Contains("净重未登记 2 行", totals.MissingEvidenceText);
    }

    [Fact]
    public async Task 打印接口_老单证无明细行_照常返回表头且不输出明细()
    {
        using var db = TestDbFactory.Create();
        var legacy = new TradeDocument
        {
            DocNo = "CI-LEGACY-1", DocType = 商业发票, Status = "已使用", Amount = 100m, Currency = "USD",
            IssueDate = new DateTime(2026, 9, 1), Copies = 1,
        };
        db.TradeDocuments.Add(legacy);
        await db.SaveChangesAsync();

        var model = DataOf<TradeDocumentPrintModel>(await TradeDocCtl(db).GetPrint(legacy.Id));

        Assert.False(model.HasDetailLines);                              // 老单证照常打印表头
        Assert.Empty(model.DetailLines);
        Assert.Null(model.DetailTotals);
        Assert.Equal(TradeDocumentPrintSemantics.CommercialInvoiceLineColumns.Count, model.DetailColumns.Count);
    }

    [Fact]
    public async Task 打印接口_明细行超过有界上限_标注截断且合计只覆盖已读取行()
    {
        using var db = TestDbFactory.Create();
        var ci = new TradeDocument
        {
            DocNo = "CI-MANY-1", DocType = 商业发票, Status = "待制作", Currency = "USD", Copies = 1,
        };
        db.TradeDocuments.Add(ci);
        await db.SaveChangesAsync();
        for (var index = 1; index <= TradeDocumentItemRules.MaxLinesPerDocument + 5; index++)
            db.TradeDocumentItems.Add(new TradeDocumentItem
            {
                TradeDocumentId = ci.Id,
                LineNo = index,
                ProductCode = $"P-{index}",
                ProductNameCn = $"毛巾-{index}",
                Quantity = 1m,
                UnitPrice = 1m,
                LineAmount = 1m,
                Currency = "USD",
            });
        await db.SaveChangesAsync();

        var model = DataOf<TradeDocumentPrintModel>(await TradeDocCtl(db).GetPrint(ci.Id));

        Assert.True(model.HasDetailLines);
        Assert.True(model.DetailLinesTruncated);
        Assert.Equal(TradeDocumentItemRules.MaxLinesPerDocument, model.DetailLines.Count);
        Assert.Equal(TradeDocumentItemRules.MaxLinesPerDocument, model.DetailTotals!.LineCount);
        Assert.True(model.DetailTotals.Truncated);
        Assert.Contains("部分合计", model.DetailTotals.MissingEvidenceText);
    }

    [Fact]
    public async Task 打印接口_多币种明细行_行金额按币种分开合计()
    {
        using var db = TestDbFactory.Create();
        var ci = new TradeDocument
        {
            DocNo = "CI-MIX-1", DocType = 商业发票, Status = "待制作", Currency = "USD", Copies = 1,
        };
        db.TradeDocuments.Add(ci);
        await db.SaveChangesAsync();
        db.TradeDocumentItems.AddRange(
            new TradeDocumentItem
            {
                TradeDocumentId = ci.Id, LineNo = 1, ProductCode = "P-A", ProductNameCn = "毛巾-A",
                Quantity = 2m, UnitPrice = 10m, LineAmount = 20m, Currency = "USD",
            },
            new TradeDocumentItem
            {
                TradeDocumentId = ci.Id, LineNo = 2, ProductCode = "P-B", ProductNameCn = "毛巾-B",
                Quantity = 1m, UnitPrice = 5m, LineAmount = 5m, Currency = "EUR",
            });
        await db.SaveChangesAsync();

        var model = DataOf<TradeDocumentPrintModel>(await TradeDocCtl(db).GetPrint(ci.Id));

        var totals = model.DetailTotals!;
        Assert.True(totals.MixedCurrency);                               // 不合并、不换算
        Assert.Equal(new[] { "EUR", "USD" }, totals.AmountByCurrency.Select(t => t.Currency).ToArray());
        Assert.Equal(5m, totals.AmountByCurrency[0].Amount);
        Assert.Equal(20m, totals.AmountByCurrency[1].Amount);
        Assert.Contains("按币种分开", totals.MissingEvidenceText);
    }

    [Fact]
    public void 打印模型_无明细行时不出明细表但保留类型口径文案()
    {
        var model = TradeDocumentPrintModel.From(new TradeDocument
        {
            Id = 21, DocNo = "PL-EMPTY-1", DocType = 装箱单, Status = "待制作", Currency = "USD",
        });

        Assert.False(model.HasDetailLines);                             // 无明细行 → 不出明细表
        Assert.Null(model.DetailTotals);                                // 也不做任何合计
        Assert.Equal(TradeDocumentPrintSemantics.DetailRuleText, model.DetailRuleText);  // 类型口径文案照常
        Assert.Equal(TradeDocumentPrintSemantics.PackingListLineColumns.Count, model.DetailColumns.Count);
        Assert.Equal(TradeDocumentPrintSemantics.FieldDefinitions.Count, model.Fields.Count);
    }

    [Fact]
    public void 打印模型_非明细行类型_无明细列与明细口径()
    {
        var model = TradeDocumentPrintModel.From(new TradeDocument
        {
            Id = 22, DocNo = "CD-1", DocType = 报关单, Status = "待制作", Currency = "USD",
        });

        Assert.Empty(model.DetailColumns);
        Assert.Empty(model.DetailLines);
        Assert.False(model.HasDetailLines);
        Assert.Equal(string.Empty, model.DetailRuleText);
    }

    // ==================== 5. Excel 导出：明细行布局 ====================

    [Fact]
    public async Task 明细行导出_按单证与行序有序_商业发票含币种行合计()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var product = SeedProduct(db, "P-052-16", "毛巾", "Cotton Towel", "70x140", "箱");
        var order = SeedSalesOrder(db, "SO-052-EX", customer.Id,
            details: new[]
            {
                (product.Id, "毛巾-A", "70x140", 10m, "箱", 2.5m),
                (product.Id, "毛巾-B", "80x160", 2m, "箱", 12.25m),
            });
        await SalesOrderCtl(db).GenerateTradeDocuments(order.Id, null);

        var file = Assert.IsType<FileContentResult>(await TradeDocCtl(db)
            .ExportExcel(null, "SO-052-EX", null, null, null, null, TradeDocumentController.LinesLayout));

        Assert.StartsWith("TradeDocumentLines_", file.FileDownloadName);
        var sheet = ReadSheet(file.FileContents);

        Assert.Equal("行类型", sheet.GetRow(0).GetCell(0).StringCellValue);
        Assert.Equal("商品编码", sheet.GetRow(0).GetCell(9).StringCellValue);

        // 单证按 Id 倒序（装箱单后生成、Id 更大 → 排在商业发票之前），明细行各自按行序
        Assert.Equal(
            new[] { "PL-SO-052-EX", "PL-SO-052-EX", "CI-SO-052-EX", "CI-SO-052-EX", "CI-SO-052-EX" },
            Enumerable.Range(1, 5).Select(row => CellText(sheet, row, 1)).ToArray());
        Assert.Equal("明细行", CellText(sheet, 1, 0));
        Assert.Equal(string.Empty, CellText(sheet, 1, 15));              // 装箱单不含单价 → 空白
        Assert.Equal(string.Empty, CellText(sheet, 1, 17));              // 装箱单不含行金额 → 空白

        // 商业发票明细行（第 3 / 4 行）：行序、数量、单价、币种、服务端计算的行金额
        Assert.Equal("1", CellText(sheet, 3, 8));
        Assert.Equal("毛巾-A", CellText(sheet, 3, 10));
        Assert.Equal("10", CellText(sheet, 3, 13));
        Assert.Equal("2.5", CellText(sheet, 3, 15));                     // 数值单元格
        Assert.Equal("USD", CellText(sheet, 3, 16));
        Assert.Equal("25", CellText(sheet, 3, 17));
        Assert.Equal(string.Empty, CellText(sheet, 3, 18));              // 商业发票行不含箱数（留空）
        Assert.Equal(string.Empty, CellText(sheet, 3, 19));
        Assert.Equal(string.Empty, CellText(sheet, 3, 20));
        Assert.Equal("2", CellText(sheet, 4, 8));
        Assert.Equal("24.5", CellText(sheet, 4, 17));

        // 商业发票合计行：币种分开合计（USD 25 + 24.5 = 49.5）
        Assert.Equal("行合计", CellText(sheet, 5, 0));
        Assert.Equal("USD", CellText(sheet, 5, 16));
        Assert.Equal("49.5", CellText(sheet, 5, 17));
        Assert.Contains("不同币种分开", CellText(sheet, 5, 21));
    }

    [Fact]
    public async Task 明细行导出_装箱单_箱数与毛重写入_未登记为空白且合计标注登记行数()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var product = SeedProduct(db, "P-052-17", "毛巾", "Cotton Towel", "70x140", "箱");
        var list = SeedLoadingList(db, "ZQ-052-EX", customer.Id,
            details: new[]
            {
                (product.Id, "毛巾-A", 4m, 3m, 9m),
                (product.Id, "毛巾-B", 1m, 0m, 0m),      // 箱数与毛重均未登记
            });
        await LoadingListCtl(db).GenerateTradeDocuments(list.Id, null);

        var file = Assert.IsType<FileContentResult>(await TradeDocCtl(db)
            .ExportExcel(null, "ZQ-052-EX", null, null, null, null, "LINES"));

        var sheet = ReadSheet(file.FileContents);
        Assert.Equal("明细行", CellText(sheet, 1, 0));
        Assert.Equal("PL-ZQ-052-EX", CellText(sheet, 1, 1));
        Assert.Equal("3", CellText(sheet, 1, 18));                       // 箱数
        Assert.Equal("9", CellText(sheet, 1, 20));                       // 毛重
        Assert.Equal(string.Empty, CellText(sheet, 1, 19));              // 净重来源未提供 → 空白
        Assert.Equal(string.Empty, CellText(sheet, 1, 15));              // 装箱单不含单价 → 空白

        Assert.Equal(string.Empty, CellText(sheet, 2, 18));              // 未登记 → 空白（不是 0）
        Assert.Equal(string.Empty, CellText(sheet, 2, 20));

        // 箱数 / 重量合计行（只累加已登记行，并照实标注登记行数）
        Assert.Equal("箱数/重量合计", CellText(sheet, 3, 0));
        Assert.Equal("3", CellText(sheet, 3, 18));
        Assert.Equal(string.Empty, CellText(sheet, 3, 19));              // 无行登记净重 → 空白
        Assert.Equal("9", CellText(sheet, 3, 20));
        Assert.Contains("箱数登记 1 行", CellText(sheet, 3, 21));
        Assert.Equal(string.Empty, CellText(sheet, 3, 17));              // 装箱单不参与金额合计
    }

    [Fact]
    public async Task 明细行导出_老单证无明细行_照常导出一行并标明无明细行()
    {
        using var db = TestDbFactory.Create();
        db.TradeDocuments.Add(new TradeDocument
        {
            DocNo = "OTHER-LEGACY", DocType = "其他", Status = "已使用", CustomerName = "别的客户",
            IssueDate = new DateTime(2026, 9, 1),
        });
        await db.SaveChangesAsync();

        var file = Assert.IsType<FileContentResult>(await TradeDocCtl(db)
            .ExportExcel(null, "OTHER-LEGACY", null, null, null, null, "lines"));

        var sheet = ReadSheet(file.FileContents);
        Assert.Equal(1, sheet.LastRowNum);                               // 表头 + 1 行
        Assert.Equal("无明细行", CellText(sheet, 1, 0));
        Assert.Equal("OTHER-LEGACY", CellText(sheet, 1, 1));
        Assert.Equal(string.Empty, CellText(sheet, 1, 8));               // 无行序
        Assert.Contains("没有明细行", CellText(sheet, 1, 21));
    }

    [Fact]
    public async Task 明细行导出_安全文本_单元格为纯文本而不是公式()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var product = SeedProduct(db, "P-052-18", "毛巾");
        var order = SeedSalesOrder(db, "SO-052-INJ", customer.Id,
            details: new[] { (product.Id, "<b>毛巾</b>\t注入", "=1+1", 2m, "箱", 3m) });
        await SalesOrderCtl(db).GenerateTradeDocuments(order.Id,
            new TradeDocGenerateRequest { DocTypes = new List<string> { 商业发票 } });

        var file = Assert.IsType<FileContentResult>(await TradeDocCtl(db)
            .ExportExcel(null, "SO-052-INJ", null, null, null, null, "lines"));

        var sheet = ReadSheet(file.FileContents);
        var nameCell = sheet.GetRow(1).GetCell(10);
        Assert.Equal(CellType.String, nameCell.CellType);                // 纯文本单元格（不是公式）
        Assert.Equal("b毛巾/b 注入", nameCell.StringCellValue);           // 标记字符与控制字符已剔除
        Assert.DoesNotContain("<", nameCell.StringCellValue);
        Assert.DoesNotContain(">", nameCell.StringCellValue);

        var specCell = sheet.GetRow(1).GetCell(12);
        Assert.Equal(CellType.String, specCell.CellType);                // 以 = 开头的规格仍是字符串
        Assert.Equal("=1+1", specCell.StringCellValue);
    }

    [Fact]
    public async Task 默认导出_仍为表头布局_既有列与行数不变()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var product = SeedProduct(db, "P-052-19", "毛巾");
        var order = SeedSalesOrder(db, "SO-052-DEF", customer.Id,
            details: new[] { (product.Id, "毛巾", "70x140", 2m, "箱", 5m) });
        await SalesOrderCtl(db).GenerateTradeDocuments(order.Id, null);

        var file = Assert.IsType<FileContentResult>(await TradeDocCtl(db)
            .ExportExcel(null, "SO-052-DEF", null, null, null, null));

        Assert.StartsWith("TradeDocuments_", file.FileDownloadName);
        var sheet = ReadSheet(file.FileContents);
        Assert.Equal("单证编号", sheet.GetRow(0).GetCell(0).StringCellValue);
        Assert.Equal("备注", sheet.GetRow(0).GetCell(15).StringCellValue);   // 仍是 16 列的表头导出
        Assert.Equal(2, sheet.LastRowNum);
    }

    [Fact]
    public void 明细行导出布局判定_仅lines为明细行布局()
    {
        Assert.True(TradeDocumentController.IsLinesLayout("lines"));
        Assert.True(TradeDocumentController.IsLinesLayout(" LINES "));
        Assert.False(TradeDocumentController.IsLinesLayout(null));
        Assert.False(TradeDocumentController.IsLinesLayout(string.Empty));
        Assert.False(TradeDocumentController.IsLinesLayout("header"));
    }

    // ==================== 6. 纯文本安全处理与有界查询 ====================

    [Fact]
    public void 来源文本清洗_剔除标记与控制字符并按列长截断()
    {
        Assert.Equal("b毛巾/b 注入", TradeDocumentLineSnapshotRules.SanitizeText("<b>毛巾</b>\t注入", 200));
        Assert.Equal("第一行 第二行", TradeDocumentLineSnapshotRules.SanitizeText("第一行\r\n第二行", 200));
        Assert.Equal("毛巾", TradeDocumentLineSnapshotRules.SanitizeText("  毛巾  ", 200));
        Assert.Equal(new string('毛', 50), TradeDocumentLineSnapshotRules.SanitizeText(new string('毛', 80), 50));
        Assert.Equal(string.Empty, TradeDocumentLineSnapshotRules.SanitizeText(null, 50));
        Assert.Equal(string.Empty, TradeDocumentLineSnapshotRules.SanitizeText("\u0007", 50));  // 控制字符被剔除
        Assert.Equal(20, TradeDocumentLineSnapshotRules.SanitizeUnit(new string('箱', 30)).Length);
    }

    [Fact]
    public void 箱数与毛重映射_来源为零或非整数时留空()
    {
        Assert.Equal(5, TradeDocumentLineSnapshotRules.PackageCountOf(5m));
        Assert.Null(TradeDocumentLineSnapshotRules.PackageCountOf(0m));      // 来源 0 = 未登记
        Assert.Null(TradeDocumentLineSnapshotRules.PackageCountOf(-3m));
        Assert.Null(TradeDocumentLineSnapshotRules.PackageCountOf(3.5m));    // 非整数：不四舍五入
        Assert.True(TradeDocumentLineSnapshotRules.IsNonIntegralPackageCount(3.5m));
        Assert.False(TradeDocumentLineSnapshotRules.IsNonIntegralPackageCount(3m));

        Assert.Equal(12.5m, TradeDocumentLineSnapshotRules.GrossWeightOf(12.5m));
        Assert.Null(TradeDocumentLineSnapshotRules.GrossWeightOf(0m));       // 来源 0 = 未登记
        Assert.Equal(1.2345m, TradeDocumentLineSnapshotRules.GrossWeightOf(1.23449m));  // 仅精度收敛
    }

    [Fact]
    public async Task 生成单证_来源明细与商品资料各一次有界查询_不逐行查库()
    {
        using var db = TestDbFactory.Create();
        var customer = SeedCustomer(db);
        var product = SeedProduct(db, "P-052-20", "毛巾");
        var order = SeedSalesOrder(db, "SO-052-Q", customer.Id,
            details: new[]
            {
                (product.Id, "毛巾-A", "70x140", 1m, "箱", 1m),
                (product.Id, "毛巾-B", "70x140", 2m, "箱", 2m),
                (product.Id, "毛巾-C", "70x140", 3m, "箱", 3m),
            });

        var (proxy, counter) = CountingContext.Wrap(db);
        var details = await TradeDocumentLineSnapshotRules.LoadSalesOrderDetailsAsync(proxy, order.Id);
        var products = await TradeDocumentLineSnapshotRules.LoadProductsAsync(
            proxy, TradeDocumentLineSnapshotRules.ProductIdsOf(details));

        var result = await TradeDocumentGeneration.GenerateAsync(proxy,
            TradeDocumentGeneration.SalesOrderSourceType, order.Id, order.OrderNo,
            containerNo: null, salesOrderNo: order.OrderNo, loadingListNo: null,
            requestedDocTypes: new List<string> { 商业发票 },
            buildDraft: docType => TradeDocumentGeneration.BuildFromSalesOrder(order, null, docType),
            buildLines: docType => TradeDocumentLineSnapshotRules.BuildFromSalesOrder(
                order, details, products, docType, "USD"));

        Assert.Equal(3, result.TotalLineCount);
        // 来源明细与商品资料各**一次**有界查询（3 行明细不会触发 3 次查询）
        Assert.Equal(1, counter.ReadProperties.Count(p => p == nameof(IErpDbContext.SalesOrderDetails)));
        Assert.Equal(1, counter.ReadProperties.Count(p => p == nameof(IErpDbContext.BaseProducts)));
        // 明细行写入按行访问一次 DbSet 属性（是写入集合访问，不是逐行查库）；表头 + 明细行两次写入
        Assert.Equal(3, counter.ReadProperties.Count(p => p == nameof(IErpDbContext.TradeDocumentItems)));
        Assert.Equal(2, counter.WriteCalls);
        Assert.Equal(3, LinesOf(db, db.TradeDocuments.Single().Id).Count);
    }

    // ==================== 7. 接口路由与前端接线契约 ====================

    [Fact]
    public void 接口路由_生成预填导出与打印保持既有模板()
    {
        AssertRouteTemplate(typeof(SalesOrderController), nameof(SalesOrderController.TradeDocumentPrefill),
            "{id:long}/trade-documents/prefill");
        AssertRouteTemplate(typeof(SalesOrderController), nameof(SalesOrderController.GenerateTradeDocuments),
            "{id:long}/trade-documents");
        AssertRouteTemplate(typeof(ContainerLoadingListController),
            nameof(ContainerLoadingListController.TradeDocumentPrefill), "{id:long}/trade-documents/prefill");
        AssertRouteTemplate(typeof(ContainerLoadingListController),
            nameof(ContainerLoadingListController.GenerateTradeDocuments), "{id:long}/trade-documents");
        AssertRouteTemplate(typeof(TradeDocumentController), nameof(TradeDocumentController.ExportExcel),
            "export-excel");
        AssertRouteTemplate(typeof(TradeDocumentController), nameof(TradeDocumentController.GetPrint),
            "{id:long}/print");
    }

    [Fact]
    public void 前端接线_明细行预览与导出与打印渲染一致()
    {
        var directory = JsDirectory();
        Assert.True(Directory.Exists(directory), $"未找到前端脚本目录：{directory}");

        var genJs = File.ReadAllText(Path.Combine(directory, "trade-doc-gen.js"));
        var printJs = File.ReadAllText(Path.Combine(directory, "trade-doc-print.js"));
        var modulesJs = File.ReadAllText(Path.Combine(directory, "modules.js"));
        var indexHtml = File.ReadAllText(Path.Combine(directory, "..", "index.html"));

        // 生成对话框：明细行数与明细行快照预览（未登记值留空）
        Assert.Contains("lineCounts", genJs);
        Assert.Contains("function tradeDocLinePreviewHtml(", genJs);
        Assert.Contains("TRADE_DOC_LINE_PREVIEW_COLUMNS", genJs);
        Assert.Contains("totalLineCount", genJs);

        // 明细行 Excel 导出：行操作 + layout=lines 布局参数
        Assert.Contains("onclick: 'exportTradeDocumentLines'", modulesJs);
        Assert.Contains("function exportTradeDocumentLines(", genJs);
        Assert.Contains("layout=lines", genJs);
        Assert.Contains("qs.set('layout', 'lines')", genJs);

        // 共享打印：明细表按后端类型适配列渲染，未登记值留空而不是 0
        Assert.Contains("function tradeDocPrintDetailHtml(", printJs);
        Assert.Contains("detailColumns", printJs);
        Assert.Contains("detailLinesTruncated", printJs);
        Assert.Contains("print-details", printJs);
        Assert.Contains("/js/trade-doc-print.js", indexHtml);
        Assert.DoesNotContain("REF_APIS", printJs);
    }

    /// <summary>
    /// 只读计数上下文代理（<see cref="DispatchProxy"/>）：记录数据集（<c>DbSet</c> 属性）访问次数与写库次数，
    /// 用于断言「来源明细与商品资料各一次有界查询、不逐行查库」以及「表头 + 明细行两次写入」。
    /// </summary>
    private class CountingContext : DispatchProxy
    {
        private IErpDbContext _inner = null!;

        /// <summary>被访问的数据集属性名（如 SalesOrderDetails / BaseProducts）</summary>
        public List<string> ReadProperties { get; } = new();

        /// <summary><c>SaveChangesAsync</c> 调用次数（表头 1 次 + 明细行 1 次）</summary>
        public int WriteCalls { get; private set; }

        public static (IErpDbContext Proxy, CountingContext Counter) Wrap(IErpDbContext inner)
        {
            var proxy = DispatchProxy.Create<IErpDbContext, CountingContext>();
            var counter = (CountingContext)(object)proxy;
            counter._inner = inner;
            return (proxy, counter);
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod is null) return null;
            if (targetMethod.Name == nameof(IErpDbContext.SaveChangesAsync))
            {
                WriteCalls++;
                return _inner.SaveChangesAsync(args is { Length: > 0 } ? (CancellationToken)args[0]! : default);
            }

            if (targetMethod.Name.StartsWith("get_", StringComparison.Ordinal))
                ReadProperties.Add(targetMethod.Name[4..]);

            return targetMethod.Invoke(_inner, args);
        }
    }
}
