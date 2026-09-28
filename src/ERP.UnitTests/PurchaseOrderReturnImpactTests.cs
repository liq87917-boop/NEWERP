using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Reflection;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-100 采购订单退货影响（只读派生）单元测试：
/// 毛收货 / 有效退货 / 净收货派生、链接状态、审核状态、删除、超退不钳制、供应商不一致、
/// 来源已删除 / 来源不属于本单 / 未关联来源的异常退货、只读不写库、订单不存在、接口端点、路由只读、前端接线、有界读取。
/// <para>说明：全部使用内存数据库，不连接 SQL Server、不启动 API、不执行任何 SQL 或 seed，不运行浏览器验收。</para>
/// </summary>
public class PurchaseOrderReturnImpactTests
{
    private static readonly DateTime Day = new(2026, 9, 20);
    private const long SupplierA = 990001L;
    private const long SupplierB = 990002L;
    private const long ProductA = 990101L;

    // ==================== 1. 毛收货 / 有效退货 / 净收货 派生 ====================

    [Fact]
    public async Task Derives_gross_returned_and_net_per_product()
    {
        using var db = TestDbFactory.Create();
        SeedSupplier(db, SupplierA, "甲供应商");
        var order = SeedOrder(db, "PO-100-1", SupplierA, (ProductA, 10m));
        var stockIn = SeedStockIn(db, "RK-1", order.Id, DocumentStatus.Approved, lines: (ProductA, 10m));
        SeedReturn(db, "TH-1", stockIn.Id, DocumentStatus.Approved, lines: (ProductA, 4m));

        var view = await PurchaseOrderReturnImpact.ForOrderAsync(db, order.Id);

        Assert.False(view.Truncated);
        Assert.Equal(10m, view.GrossReceivedTotal);
        Assert.Equal(4m, view.ValidReturnedTotal);
        Assert.Equal(6m, view.NetReceivedTotal);

        var line = Assert.Single(view.Products);
        Assert.Equal(ProductA, line.ProductId);
        Assert.Equal(10m, line.GrossReceived);
        Assert.Equal(4m, line.ValidReturned);
        Assert.Equal(6m, line.NetReceived);
        Assert.False(line.OverReturned);
        Assert.Equal(PurchaseOrderReturnImpactSemantics.AnomalyNone, line.Anomaly);

        var ret = Assert.Single(view.Returns);
        Assert.Equal(PurchaseOrderReturnImpactSemantics.ReturnValid, ret.Classification);
        Assert.True(ret.Applied);
    }

    // ==================== 2. 仅已审核未删除入库计入毛收货；仅已审核退货计入有效退货 ====================

    [Fact]
    public async Task Only_approved_nondeleted_stock_ins_and_approved_returns_count()
    {
        using var db = TestDbFactory.Create();
        SeedSupplier(db, SupplierA, "甲供应商");
        var order = SeedOrder(db, "PO-100-2", SupplierA, (ProductA, 10m));
        var approved = SeedStockIn(db, "RK-OK", order.Id, DocumentStatus.Approved, lines: (ProductA, 10m));
        SeedStockIn(db, "RK-SUB", order.Id, DocumentStatus.Submitted, lines: (ProductA, 5m));
        SeedStockIn(db, "RK-DEL", order.Id, DocumentStatus.Approved, deleted: true, lines: (ProductA, 3m));
        SeedReturn(db, "TH-V", approved.Id, DocumentStatus.Approved, lines: (ProductA, 4m));
        SeedReturn(db, "TH-SUB", approved.Id, DocumentStatus.Submitted, lines: (ProductA, 2m));

        var view = await PurchaseOrderReturnImpact.ForOrderAsync(db, order.Id);

        var line = Assert.Single(view.Products);
        Assert.Equal(10m, line.GrossReceived);   // 仅已审核未删除入库
        Assert.Equal(4m, line.ValidReturned);    // 仅已审核退货
        Assert.Equal(6m, line.NetReceived);

        Assert.Equal(2, view.Returns.Count);
        Assert.Contains(view.Returns, r => r.Classification == PurchaseOrderReturnImpactSemantics.ReturnValid);
        Assert.Contains(view.Returns, r => r.Classification == PurchaseOrderReturnImpactSemantics.ReturnNonApproved);
    }

    // ==================== 3. 超退不静默钳制（净额可为负） ====================

    [Fact]
    public async Task Over_return_is_not_clamped()
    {
        using var db = TestDbFactory.Create();
        SeedSupplier(db, SupplierA, "甲供应商");
        var order = SeedOrder(db, "PO-100-3", SupplierA, (ProductA, 5m));
        var stockIn = SeedStockIn(db, "RK-3", order.Id, DocumentStatus.Approved, lines: (ProductA, 5m));
        SeedReturn(db, "TH-3", stockIn.Id, DocumentStatus.Approved, lines: (ProductA, 8m));

        var view = await PurchaseOrderReturnImpact.ForOrderAsync(db, order.Id);

        var line = Assert.Single(view.Products);
        Assert.Equal(5m, line.GrossReceived);
        Assert.Equal(8m, line.ValidReturned);
        Assert.Equal(-3m, line.NetReceived);   // 超退：净额为负，不钳制为 0
        Assert.True(line.OverReturned);
        Assert.Equal(PurchaseOrderReturnImpactSemantics.AnomalyOverReturn, line.Anomaly);
        Assert.Equal(-3m, view.NetReceivedTotal);
    }

    // ==================== 4. 来源入库单已删除 → 悬空异常（不计入） ====================

    [Fact]
    public async Task Return_linked_to_deleted_stock_in_is_missing_source_exception()
    {
        using var db = TestDbFactory.Create();
        SeedSupplier(db, SupplierA, "甲供应商");
        var order = SeedOrder(db, "PO-100-4", SupplierA, (ProductA, 10m));
        var deletedStockIn = SeedStockIn(db, "RK-DELSRC", order.Id, DocumentStatus.Approved, deleted: true, lines: (ProductA, 10m));
        SeedReturn(db, "TH-4", deletedStockIn.Id, DocumentStatus.Approved, lines: (ProductA, 3m));

        var view = await PurchaseOrderReturnImpact.ForOrderAsync(db, order.Id);

        var line = Assert.Single(view.Products);
        Assert.Equal(0m, line.GrossReceived);   // 已删除入库不计入毛收货
        Assert.Equal(0m, line.ValidReturned);   // 来源已删除退货不计入
        Assert.Equal(0m, line.NetReceived);

        var ret = Assert.Single(view.Returns);
        Assert.Equal(PurchaseOrderReturnImpactSemantics.ReturnMissingSource, ret.Classification);
        Assert.False(ret.Applied);
    }

    // ==================== 5. 供应商不一致 → 异常（不计入） ====================

    [Fact]
    public async Task Return_with_wrong_supplier_is_exception_not_applied()
    {
        using var db = TestDbFactory.Create();
        SeedSupplier(db, SupplierA, "甲供应商");
        SeedSupplier(db, SupplierB, "乙供应商");
        var order = SeedOrder(db, "PO-100-5", SupplierA, (ProductA, 10m));
        var stockIn = SeedStockIn(db, "RK-5", order.Id, DocumentStatus.Approved, supplierId: SupplierA, lines: (ProductA, 10m));
        SeedReturn(db, "TH-5", stockIn.Id, DocumentStatus.Approved,
            supplierId: SupplierB, supplierName: "乙供应商", lines: (ProductA, 4m));

        var view = await PurchaseOrderReturnImpact.ForOrderAsync(db, order.Id);

        Assert.Equal(10m, view.GrossReceivedTotal);
        Assert.Equal(0m, view.ValidReturnedTotal);   // 供应商不一致不计入
        var ret = Assert.Single(view.Returns);
        Assert.Equal(PurchaseOrderReturnImpactSemantics.ReturnWrongSupplier, ret.Classification);
        Assert.False(ret.Applied);
    }

    // ==================== 6. 未关联 / 来源不属于本单 → 异常（不计入） ====================

    [Fact]
    public async Task Unlinked_and_unmatched_returns_are_exceptions_not_applied()
    {
        using var db = TestDbFactory.Create();
        SeedSupplier(db, SupplierA, "甲供应商");
        var order = SeedOrder(db, "PO-100-6", SupplierA, (ProductA, 10m));
        SeedStockIn(db, "RK-6", order.Id, DocumentStatus.Approved, lines: (ProductA, 10m));

        var otherOrder = SeedOrder(db, "PO-OTHER", SupplierA, (ProductA, 5m));
        var otherStockIn = SeedStockIn(db, "RK-OTHER", otherOrder.Id, DocumentStatus.Approved, lines: (ProductA, 5m));

        SeedReturn(db, "TH-UNLINKED", null, DocumentStatus.Approved, lines: (ProductA, 2m));
        SeedReturn(db, "TH-UNMATCHED", otherStockIn.Id, DocumentStatus.Approved, lines: (ProductA, 2m));

        var view = await PurchaseOrderReturnImpact.ForOrderAsync(db, order.Id);

        Assert.Equal(2, view.Exceptions.Count);
        Assert.Contains(view.Exceptions, e => e.Classification == PurchaseOrderReturnImpactSemantics.ReturnUnlinked);
        Assert.Contains(view.Exceptions, e => e.Classification == PurchaseOrderReturnImpactSemantics.ReturnUnmatched);
        Assert.DoesNotContain(view.Returns, r => r.Applied);
        Assert.Equal(0m, view.ValidReturnedTotal);
    }

    // ==================== 7. 只读不写库 ====================

    [Fact]
    public async Task View_is_read_only_and_writes_nothing()
    {
        using var db = TestDbFactory.Create();
        SeedSupplier(db, SupplierA, "甲供应商");
        var order = SeedOrder(db, "PO-100-7", SupplierA, (ProductA, 10m));
        var stockIn = SeedStockIn(db, "RK-7", order.Id, DocumentStatus.Approved, lines: (ProductA, 10m));
        SeedReturn(db, "TH-7", stockIn.Id, DocumentStatus.Approved, lines: (ProductA, 4m));
        await db.SaveChangesAsync();

        var ordersBefore = db.PurchaseOrders.Count();
        var stockInsBefore = db.StockIns.Count();
        var returnsBefore = db.PurchaseReturns.Count();

        _ = await PurchaseOrderReturnImpact.ForOrderAsync(db, order.Id);

        Assert.Equal(ordersBefore, db.PurchaseOrders.Count());
        Assert.Equal(stockInsBefore, db.StockIns.Count());
        Assert.Equal(returnsBefore, db.PurchaseReturns.Count());
        Assert.DoesNotContain(db.ChangeTracker.Entries(),
            e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted);
    }

    // ==================== 8. 订单不存在 → 明确业务错误 ====================

    [Fact]
    public async Task Missing_order_throws_not_found()
    {
        using var db = TestDbFactory.Create();
        var ex = await Assert.ThrowsAsync<BusinessException>(
            () => PurchaseOrderReturnImpact.ForOrderAsync(db, 987654L));
        Assert.Equal(ErrorCodes.NotFound, ex.Code);
    }


    // ==================== 9. 接口端点 ====================

    [Fact]
    public async Task Controller_endpoint_returns_return_impact_payload()
    {
        using var db = TestDbFactory.Create();
        SeedSupplier(db, SupplierA, "甲供应商");
        var order = SeedOrder(db, "PO-100-CTRL", SupplierA, (ProductA, 10m));
        var stockIn = SeedStockIn(db, "RK-CTRL", order.Id, DocumentStatus.Approved, lines: (ProductA, 10m));
        SeedReturn(db, "TH-CTRL", stockIn.Id, DocumentStatus.Approved, lines: (ProductA, 4m));
        await db.SaveChangesAsync();

        var controller = new PurchaseOrderController(db, new DocumentNumberService(db));
        var result = await controller.ReturnImpact(order.Id);

        var ok = Assert.IsType<OkObjectResult>(result);
        var payload = Assert.IsType<ApiResponse<PurchaseOrderReturnImpactView>>(ok.Value);
        Assert.Equal(ErrorCodes.Success, payload.Code);
        Assert.Equal(6m, payload.Data!.NetReceivedTotal);
    }

    // ==================== 10. 只读 GET 路由与口径边界 ====================

    [Fact]
    public void View_exposes_only_read_get_route_and_states_boundary()
    {
        var methods = typeof(PurchaseOrderController).GetMethods();
        var getRoutes = methods.SelectMany(m => m.GetCustomAttributes<HttpGetAttribute>())
            .Select(a => a.Template ?? string.Empty).ToList();
        Assert.Contains("{id:long}/return-impact", getRoutes);
        Assert.Single(getRoutes.Where(r => r == "{id:long}/return-impact"));
        Assert.DoesNotContain(methods.SelectMany(m => m.GetCustomAttributes<HttpPostAttribute>()),
            a => (a.Template ?? string.Empty).Contains("return-impact", StringComparison.Ordinal));
        Assert.DoesNotContain(methods.SelectMany(m => m.GetCustomAttributes<HttpPutAttribute>()),
            a => (a.Template ?? string.Empty).Contains("return-impact", StringComparison.Ordinal));
        Assert.DoesNotContain(methods.SelectMany(m => m.GetCustomAttributes<HttpDeleteAttribute>()),
            a => (a.Template ?? string.Empty).Contains("return-impact", StringComparison.Ordinal));

        Assert.Contains("只读派生", PurchaseOrderReturnImpactSemantics.RuleText);
        Assert.Contains("不改写", PurchaseOrderReturnImpactSemantics.RuleText);
        Assert.Contains("未知", PurchaseOrderReturnImpactSemantics.RuleText);
        Assert.Contains("超退", PurchaseOrderReturnImpactSemantics.RuleText);
    }

    // ==================== 11. 前端接线（离线校验，不启动浏览器） ====================

    [Fact]
    public void Frontend_entry_and_api_are_wired_without_browser()
    {
        var js = JsDirectory();
        var reportJs = File.ReadAllText(Path.Combine(js, "purchase-order-return-impact.js"));

        Assert.Contains("function showPurchaseOrderReturnImpact(id)", reportJs);
        Assert.Contains("/api/purchase-orders/${id}/return-impact", reportJs);
        Assert.Contains("PRI_CLASSIFICATION_LABELS", reportJs);
        Assert.Contains("PRI_ANOMALY_LABELS", reportJs);
        Assert.Contains("'未知'", reportJs);

        var modulesDoc = File.ReadAllText(Path.Combine(js, "modules-doc.js"));
        Assert.Contains("showPurchaseOrderReturnImpact", modulesDoc);
        var index = File.ReadAllText(Path.Combine(js, "..", "index.html"));
        Assert.Contains("/js/purchase-order-return-impact.js", index);
    }

    // ==================== 12. 有界读取：命中上限 → 未知（不回落为 0） ====================

    [Fact]
    public async Task Hitting_collection_ceiling_marks_quantities_unknown()
    {
        using var db = TestDbFactory.Create();
        SeedSupplier(db, SupplierA, "甲供应商");
        var order = SeedOrder(db, "PO-100-BOUND", SupplierA, (ProductA, 1m));
        for (var i = 0; i < 200; i++)
        {
            db.StockIns.Add(new StockIn
            {
                StockInNo = $"RK-B-{i}",
                StockInDate = Day,
                PurchaseOrderId = order.Id,
                SupplierId = SupplierA,
                WarehouseId = 1,
                Status = DocumentStatus.Approved,
            });
        }
        db.SaveChanges();

        var view = await PurchaseOrderReturnImpact.ForOrderAsync(db, order.Id);

        Assert.True(view.Truncated);
        Assert.Null(view.GrossReceivedTotal);
        Assert.Null(view.ValidReturnedTotal);
        Assert.Null(view.NetReceivedTotal);

        var line = Assert.Single(view.Products);
        Assert.Null(line.GrossReceived);
        Assert.Null(line.NetReceived);
        Assert.Equal(PurchaseOrderReturnImpactSemantics.AnomalyUnknown, line.Anomaly);
    }


    // ==================== 助手 ====================

    private static string JsDirectory() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "..", "..", "..", "..", "..", "src", "ERP.Api", "wwwroot", "js"));

    private static void SeedSupplier(ErpDbContext db, long id, string name)
        => db.BaseSuppliers.Add(new BaseSupplier { Id = id, SupplierCode = $"S{id}", SupplierName = name });

    private static PurchaseOrder SeedOrder(ErpDbContext db, string orderNo, long supplierId,
        params (long ProductId, decimal Quantity)[] lines)
    {
        var order = new PurchaseOrder
        {
            OrderNo = orderNo,
            OrderDate = Day,
            SupplierId = supplierId,
            Currency = Currency.CNY,
            TotalAmount = lines.Sum(l => l.Quantity),
            Status = DocumentStatus.Approved,
        };
        db.PurchaseOrders.Add(order);
        db.SaveChanges();
        foreach (var (productId, quantity) in lines)
        {
            db.PurchaseOrderDetails.Add(new PurchaseOrderDetail
            {
                PurchaseOrderId = order.Id,
                ProductId = productId,
                ProductName = $"商品{productId}",
                Unit = "PCS",
                Quantity = quantity,
                UnitPrice = 1m,
                Amount = quantity,
            });
        }
        db.SaveChanges();
        return order;
    }

    private static StockIn SeedStockIn(ErpDbContext db, string stockInNo, long? purchaseOrderId,
        DocumentStatus status, bool deleted = false, long supplierId = SupplierA,
        params (long ProductId, decimal Quantity)[] lines)
    {
        var stockIn = new StockIn
        {
            StockInNo = stockInNo,
            StockInDate = Day,
            PurchaseOrderId = purchaseOrderId,
            SupplierId = supplierId,
            WarehouseId = 1,
            Status = status,
            IsDeleted = deleted,
        };
        db.StockIns.Add(stockIn);
        db.SaveChanges();
        foreach (var (productId, quantity) in lines)
        {
            db.StockInDetails.Add(new StockInDetail
            {
                StockInId = stockIn.Id,
                ProductId = productId,
                ProductName = $"商品{productId}",
                Unit = "PCS",
                Quantity = quantity,
            });
        }
        db.SaveChanges();
        return stockIn;
    }

    private static PurchaseReturn SeedReturn(ErpDbContext db, string returnNo, long? sourceStockInId,
        DocumentStatus status, bool deleted = false, long? supplierId = SupplierA, string supplierName = "甲供应商",
        params (long ProductId, decimal Quantity)[] lines)
    {
        var ret = new PurchaseReturn
        {
            ReturnNo = returnNo,
            ReturnDate = Day,
            SupplierId = supplierId,
            SupplierName = supplierName,
            WarehouseId = 1,
            SourceStockInId = sourceStockInId,
            SourceStockInNo = sourceStockInId == null ? string.Empty : $"RK-{sourceStockInId}",
            Status = status,
            IsDeleted = deleted,
        };
        db.PurchaseReturns.Add(ret);
        db.SaveChanges();
        foreach (var (productId, quantity) in lines)
        {
            db.PurchaseReturnDetails.Add(new PurchaseReturnDetail
            {
                PurchaseReturnId = ret.Id,
                ReturnNo = returnNo,
                ProductId = productId,
                ProductName = $"商品{productId}",
                Unit = "PCS",
                Quantity = quantity,
                UnitPrice = 1m,
                Amount = quantity,
            });
        }
        db.SaveChanges();
        return ret;
    }
}

