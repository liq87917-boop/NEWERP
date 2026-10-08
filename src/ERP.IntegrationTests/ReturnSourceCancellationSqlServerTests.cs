using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using System.Data;
using System.Security.Claims;
using Xunit;

namespace ERP.IntegrationTests;

/// <summary>
/// ERP-359 来源出库单 / 来源入库单「取消前退货引用护栏」的真实 SQL Server 集成测试（GUID 独占 <c>NEWERP_AUTOTEST</c> 目标）。
/// <para>直接执行<b>真实业务代码</b>（<see cref="StockOutController"/> / <see cref="StockInController"/> /
/// <see cref="SalesReturnController"/> / <see cref="PurchaseReturnController"/> + <see cref="ReturnSourceCancellationRules"/> +
/// <see cref="InventoryService"/>），不复制测试专用实现：</para>
/// <list type="number">
/// <item>存在仍然生效的已审核退货单显式引用来源单据时，取消 fail closed 拒绝，且单据 / 明细 / 状态 / 库存 / 流水全部保持原样；</item>
/// <item>null 来源的无关退货与未完审核退货不阻断，绝不按来源单号文本推断引用；</item>
/// <item>销审（红字冲销）后即可按既有流程取消来源单据，红字冲销流水原样保留；</item>
/// <item>重复取消来源单据按既有规则幂等拒绝；</item>
/// <item><b>两条独立连接竞争</b>：并发「退货审核」与「来源取消」只允许一种一致结果（各 1 例销售 / 采购）。</item>
/// </list>
/// <para>安全口径：目标必须为专用 localdb 实例 <c>NEWERP_AutoAcceptance</c>、库名前缀 <c>NEWERP_AUTOTEST</c>
/// 且集成安全；每次运行只创建全新 GUID 后缀库，发现同名库已存在立即拒绝；绝不 drop / reset / 复用任何数据库，
/// 连接串只来自进程环境变量或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据。</para>
/// </summary>
public sealed class ReturnSourceCancellationSqlServerTests
    : IClassFixture<ReturnSourceCancellationSqlServerFixture>
{
    private readonly ReturnSourceCancellationSqlServerFixture _fixture;

    public ReturnSourceCancellationSqlServerTests(ReturnSourceCancellationSqlServerFixture fixture)
        => _fixture = fixture;

    /// <summary>访问数据库前复核专用目标护栏（实例名 / 库名前缀 / 集成安全）。</summary>
    private void Guard()
    {
        var target = new SqlConnectionStringBuilder(_fixture.ConnectionString);
        Assert.Equal("(localdb)\\NEWERP_AutoAcceptance", target.DataSource, ignoreCase: true);
        Assert.StartsWith(ReturnSourceCancellationSqlServerFixture.DatabasePrefix, target.InitialCatalog,
            StringComparison.OrdinalIgnoreCase);
        Assert.True(target.IntegratedSecurity);
    }

    private static string Tag() => Guid.NewGuid().ToString("N")[..10];

    private static ControllerContext Context(long? userId)
        => new()
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(userId.HasValue
                    ? new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }
                    : Array.Empty<Claim>(), "Test"))
            }
        };

    private static StockOutController NewStockOutController(ErpDbContext db, long? userId)
        => new(db, new DocumentNumberService(db), new InventoryService(db)) { ControllerContext = Context(userId) };

    private static StockInController NewStockInController(ErpDbContext db, long? userId)
        => new(db, new DocumentNumberService(db), new InventoryService(db)) { ControllerContext = Context(userId) };

    private static SalesReturnController NewSalesReturnController(ErpDbContext db, long? userId)
        => new(db, new DocumentNumberService(db), new InventoryService(db)) { ControllerContext = Context(userId) };

    private static PurchaseReturnController NewPurchaseReturnController(ErpDbContext db, long? userId)
        => new(db, new DocumentNumberService(db), new InventoryService(db)) { ControllerContext = Context(userId) };

    private static long CreatedId(IActionResult result)
    {
        var data = Assert.IsType<ApiResponse<object>>(Assert.IsType<OkObjectResult>(result).Value).Data!;
        return (long)data.GetType().GetProperty("Id")!.GetValue(data)!;
    }

    // ==================== 1. 来源销售出库单：存在已审核引用 → 拒绝且全部原始证据不变 ====================

    [Fact]
    public async Task Source_shipment_cancel_rejected_while_approved_sales_return_remains()
    {
        Guard();
        await using var seed = _fixture.CreateDbContext();
        var warehouseId = await SeedWarehouseAsync(seed, "来源取消仓");
        var productId = await SeedProductAsync(seed, "来源取消商品");
        var customerId = await SeedCustomerAsync(seed, "来源取消客户");
        var userId = await SeedSalesmanAsync(seed, customerId);
        var shipment = await SeedStockOutAsync(seed, customerId, warehouseId, DocumentStatus.Approved,
            (productId, "PCS", 5m));
        var ret = await SeedSalesReturnAsync(seed, shipment.Id, shipment.StockOutNo, customerId, warehouseId,
            DocumentStatus.Approved, (productId, "PCS", 5m));

        await using (var db = _fixture.CreateDbContext())
        {
            var ctl = NewStockOutController(db, userId);
            var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Cancel(shipment.Id));
            Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
            Assert.Contains("销审", ex.Message);              // 可执行的退货冲销要求
            Assert.Contains(ret.ReturnNo, ex.Message);        // 明确指出阻断的退货单
        }

        await using (var db = _fixture.CreateDbContext())
        {
            Assert.Equal(DocumentStatus.Approved, await db.StockOuts.Where(o => o.Id == shipment.Id)
                .Select(o => o.Status).SingleAsync());
            Assert.Equal(1, await db.StockOutDetails.CountAsync(d => d.StockOutId == shipment.Id && !d.IsDeleted));
            Assert.Equal(5m, await db.StockOutDetails.Where(d => d.StockOutId == shipment.Id).SumAsync(d => d.Quantity));
            Assert.Equal(DocumentStatus.Approved, await db.SalesReturns.Where(r => r.Id == ret.Id)
                .Select(r => r.Status).SingleAsync());
            Assert.Equal(shipment.Id, await db.SalesReturns.Where(r => r.Id == ret.Id)
                .Select(r => r.SourceStockOutId).SingleAsync());
            Assert.Equal(0, await db.StockMovements.CountAsync(m => m.WarehouseId == warehouseId && !m.IsDeleted));
            Assert.Equal(0m, await db.Stocks.Where(s => s.WarehouseId == warehouseId && s.ProductId == productId)
                .SumAsync(s => s.Quantity));
        }
    }

    // ==================== 2. 来源销售出库单：null 来源 / 未完审核退货不阻断 ====================

    [Fact]
    public async Task Null_source_or_unapproved_sales_return_does_not_block_source_cancel()
    {
        Guard();
        await using var seed = _fixture.CreateDbContext();
        var warehouseId = await SeedWarehouseAsync(seed, "来源取消仓");
        var productId = await SeedProductAsync(seed, "来源取消商品");
        var customerId = await SeedCustomerAsync(seed, "来源取消客户");
        var userId = await SeedSalesmanAsync(seed, customerId);
        await SeedStockAsync(seed, warehouseId, productId, quantity: 10m, totalCost: 100m);
        var shipment = await SeedStockOutAsync(seed, customerId, warehouseId, DocumentStatus.Approved,
            (productId, "PCS", 5m));
        // null 来源但来源单号文本完全相同：绝不据此推断引用
        var unrelated = await SeedSalesReturnAsync(seed, null, shipment.StockOutNo, customerId, warehouseId,
            DocumentStatus.Approved, (productId, "PCS", 99m));
        // 显式来源但尚未审核（已提交）：不构成生效引用
        var unapproved = await SeedSalesReturnAsync(seed, shipment.Id, shipment.StockOutNo, customerId,
            warehouseId, DocumentStatus.Submitted, (productId, "PCS", 3m));

        await using (var db = _fixture.CreateDbContext())
        {
            var ctl = NewStockOutController(db, userId);
            Assert.IsType<OkObjectResult>(await ctl.Cancel(shipment.Id));
        }

        await using (var db = _fixture.CreateDbContext())
        {
            Assert.Equal(DocumentStatus.Cancelled, await db.StockOuts.Where(o => o.Id == shipment.Id)
                .Select(o => o.Status).SingleAsync());
            // 无关 / 未完审核退货原样保留，未被改写
            Assert.Equal(DocumentStatus.Approved, await db.SalesReturns.Where(r => r.Id == unrelated.Id)
                .Select(r => r.Status).SingleAsync());
            Assert.Null(await db.SalesReturns.Where(r => r.Id == unrelated.Id)
                .Select(r => r.SourceStockOutId).SingleAsync());
            Assert.Equal(DocumentStatus.Submitted, await db.SalesReturns.Where(r => r.Id == unapproved.Id)
                .Select(r => r.Status).SingleAsync());
        }
    }

    // ==================== 3. 来源销售出库单：销审后退货不再阻断，红字冲销流水保留 ====================

    [Fact]
    public async Task Unaudited_linked_sales_return_allows_source_cancel_and_keeps_reversal_ledger()
    {
        Guard();
        await using var seed = _fixture.CreateDbContext();
        var warehouseId = await SeedWarehouseAsync(seed, "来源取消仓");
        var productId = await SeedProductAsync(seed, "来源取消商品");
        var customerId = await SeedCustomerAsync(seed, "来源取消客户");
        var userId = await SeedSalesmanAsync(seed, customerId);
        await SeedStockAsync(seed, warehouseId, productId, quantity: 10m, totalCost: 100m);

        long shipmentId;
        await using (var db = _fixture.CreateDbContext())
        {
            var ctl = NewStockOutController(db, userId);
            shipmentId = CreatedId(await ctl.Create(new StockOut
            {
                StockOutDate = DateTime.Today,
                CustomerId = customerId,
                WarehouseId = warehouseId,
                Details = new List<StockOutDetail>
                {
                    new() { ProductId = productId, ProductName = "来源取消商品", Unit = "PCS", Quantity = 5m }
                }
            }));
            await ctl.Submit(shipmentId);
            await ctl.Approve(shipmentId);
        }

        long returnId;
        await using (var db = _fixture.CreateDbContext())
        {
            var sourceNo = await db.StockOuts.Where(o => o.Id == shipmentId).Select(o => o.StockOutNo).SingleAsync();
            var ctl = NewSalesReturnController(db, userId);
            returnId = CreatedId(await ctl.Create(new SalesReturn
            {
                ReturnDate = DateTime.Today,
                CustomerId = customerId,
                CustomerName = "来源取消客户",
                WarehouseId = warehouseId,
                SourceStockOutId = shipmentId,
                SourceStockOutNo = sourceNo,
                ReturnReason = "质量",
                Details = new List<SalesReturnDetail>
                {
                    new() { ProductId = productId, ProductName = "来源取消商品", Unit = "PCS",
                        Quantity = 4m, UnitPrice = 10m, UnitCost = 8m }
                }
            }));
            await ctl.Submit(returnId);
            await ctl.Approve(returnId);
            await ctl.Unaudit(returnId);           // 销审：红字冲销，退货回到待提交
        }

        await using (var db = _fixture.CreateDbContext())
        {
            var ctl = NewStockOutController(db, userId);
            Assert.IsType<OkObjectResult>(await ctl.Cancel(shipmentId));
        }

        await using (var db = _fixture.CreateDbContext())
        {
            Assert.Equal(DocumentStatus.Cancelled, await db.StockOuts.Where(o => o.Id == shipmentId)
                .Select(o => o.Status).SingleAsync());
            Assert.Equal(DocumentStatus.Pending, await db.SalesReturns.Where(r => r.Id == returnId)
                .Select(r => r.Status).SingleAsync());
            Assert.Equal(10m, await db.Stocks.Where(s => s.WarehouseId == warehouseId && s.ProductId == productId)
                .Select(s => s.Quantity).SingleAsync());
            // 红字冲销轨迹（退货销审 + 出库取消）原样保留
            Assert.Equal(2, await db.StockMovements.CountAsync(m => m.WarehouseId == warehouseId && m.IsReversal));
        }
    }

    // ==================== 4. 来源销售出库单：重复取消幂等拒绝 ====================

    [Fact]
    public async Task Repeat_source_shipment_cancel_is_idempotent_under_existing_rules()
    {
        Guard();
        await using var seed = _fixture.CreateDbContext();
        var warehouseId = await SeedWarehouseAsync(seed, "来源取消仓");
        var productId = await SeedProductAsync(seed, "来源取消商品");
        var customerId = await SeedCustomerAsync(seed, "来源取消客户");
        var userId = await SeedSalesmanAsync(seed, customerId);
        await SeedStockAsync(seed, warehouseId, productId, quantity: 10m, totalCost: 100m);

        long shipmentId;
        await using (var db = _fixture.CreateDbContext())
        {
            var ctl = NewStockOutController(db, userId);
            shipmentId = CreatedId(await ctl.Create(new StockOut
            {
                StockOutDate = DateTime.Today,
                CustomerId = customerId,
                WarehouseId = warehouseId,
                Details = new List<StockOutDetail>
                {
                    new() { ProductId = productId, ProductName = "来源取消商品", Unit = "PCS", Quantity = 5m }
                }
            }));
            await ctl.Submit(shipmentId);
            await ctl.Approve(shipmentId);
            Assert.IsType<OkObjectResult>(await ctl.Cancel(shipmentId));
            var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Cancel(shipmentId));
            Assert.Contains("已取消", ex.Message);
        }

        await using (var db = _fixture.CreateDbContext())
        {
            Assert.Equal(10m, await db.Stocks.Where(s => s.WarehouseId == warehouseId && s.ProductId == productId)
                .Select(s => s.Quantity).SingleAsync());
            Assert.Equal(2, await db.StockMovements.CountAsync(m => m.WarehouseId == warehouseId && !m.IsDeleted));
        }
    }

    // ==================== 5. 来源采购入库单：存在已审核引用 → 拒绝且全部原始证据不变 ====================

    [Fact]
    public async Task Source_receipt_cancel_rejected_while_approved_purchase_return_remains()
    {
        Guard();
        await using var seed = _fixture.CreateDbContext();
        var warehouseId = await SeedWarehouseAsync(seed, "来源取消仓");
        var productId = await SeedProductAsync(seed, "来源取消商品");
        var supplierId = await SeedSupplierAsync(seed, "来源取消供应商");
        var userId = await SeedPrivilegedOperatorAsync(seed,
            PurchaseReturnSourceRules.RequiredMenuCode, StockInAuthorizationRules.RequiredMenuCode);
        var receipt = await SeedStockInAsync(seed, supplierId, warehouseId, DocumentStatus.Approved,
            (productId, "PCS", 10m));
        var ret = await SeedPurchaseReturnAsync(seed, receipt.Id, receipt.StockInNo, supplierId, warehouseId,
            DocumentStatus.Approved, (productId, "PCS", 10m));

        await using (var db = _fixture.CreateDbContext())
        {
            var ctl = NewStockInController(db, userId);
            var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Cancel(receipt.Id));
            Assert.Equal(ErrorCodes.RuleConflict, ex.Code);
            Assert.Contains("销审", ex.Message);
            Assert.Contains(ret.ReturnNo, ex.Message);
        }

        await using (var db = _fixture.CreateDbContext())
        {
            Assert.Equal(DocumentStatus.Approved, await db.StockIns.Where(o => o.Id == receipt.Id)
                .Select(o => o.Status).SingleAsync());
            Assert.Equal(1, await db.StockInDetails.CountAsync(d => d.StockInId == receipt.Id && !d.IsDeleted));
            Assert.Equal(10m, await db.StockInDetails.Where(d => d.StockInId == receipt.Id).SumAsync(d => d.Quantity));
            Assert.Equal(DocumentStatus.Approved, await db.PurchaseReturns.Where(r => r.Id == ret.Id)
                .Select(r => r.Status).SingleAsync());
            Assert.Equal(receipt.Id, await db.PurchaseReturns.Where(r => r.Id == ret.Id)
                .Select(r => r.SourceStockInId).SingleAsync());
            Assert.Equal(0, await db.StockMovements.CountAsync(m => m.WarehouseId == warehouseId && !m.IsDeleted));
        }
    }

    // ==================== 6. 来源采购入库单：null 来源 / 未完审核退货不阻断 ====================

    [Fact]
    public async Task Null_source_or_unapproved_purchase_return_does_not_block_source_cancel()
    {
        Guard();
        await using var seed = _fixture.CreateDbContext();
        var warehouseId = await SeedWarehouseAsync(seed, "来源取消仓");
        var productId = await SeedProductAsync(seed, "来源取消商品");
        var supplierId = await SeedSupplierAsync(seed, "来源取消供应商");
        var userId = await SeedPrivilegedOperatorAsync(seed,
            PurchaseReturnSourceRules.RequiredMenuCode, StockInAuthorizationRules.RequiredMenuCode);

        long receiptId;
        await using (var db = _fixture.CreateDbContext())
        {
            var ctl = NewStockInController(db, userId);
            receiptId = CreatedId(await ctl.Create(new StockIn
            {
                StockInDate = DateTime.Today,
                SupplierId = supplierId,
                WarehouseId = warehouseId,
                Details = new List<StockInDetail>
                {
                    new() { ProductId = productId, ProductName = "来源取消商品", Unit = "PCS", Quantity = 5m }
                }
            }));
            await ctl.Submit(receiptId);
            await ctl.Approve(receiptId);
        }

        long unrelatedId, unapprovedId;
        await using (var db = _fixture.CreateDbContext())
        {
            var sourceNo = await db.StockIns.Where(o => o.Id == receiptId).Select(o => o.StockInNo).SingleAsync();
            unrelatedId = (await SeedPurchaseReturnAsync(db, null, sourceNo, supplierId, warehouseId,
                DocumentStatus.Approved, (productId, "PCS", 99m))).Id;
            unapprovedId = (await SeedPurchaseReturnAsync(db, receiptId, sourceNo, supplierId, warehouseId,
                DocumentStatus.Submitted, (productId, "PCS", 3m))).Id;

            var ctl = NewStockInController(db, userId);
            Assert.IsType<OkObjectResult>(await ctl.Cancel(receiptId));
        }

        await using (var db = _fixture.CreateDbContext())
        {
            Assert.Equal(DocumentStatus.Cancelled, await db.StockIns.Where(o => o.Id == receiptId)
                .Select(o => o.Status).SingleAsync());
            Assert.Equal(0m, await db.Stocks.Where(s => s.WarehouseId == warehouseId && s.ProductId == productId)
                .SumAsync(s => s.Quantity));
            // 无关 / 未完审核退货原样保留，未被改写
            Assert.Equal(DocumentStatus.Approved, await db.PurchaseReturns.Where(r => r.Id == unrelatedId)
                .Select(r => r.Status).SingleAsync());
            Assert.Null(await db.PurchaseReturns.Where(r => r.Id == unrelatedId)
                .Select(r => r.SourceStockInId).SingleAsync());
            Assert.Equal(DocumentStatus.Submitted, await db.PurchaseReturns.Where(r => r.Id == unapprovedId)
                .Select(r => r.Status).SingleAsync());
        }
    }

    // ==================== 7. 两条独立连接竞争：销售退货审核 与 来源出库单取消 ====================

    [Fact]
    public async Task Two_connections_sales_return_approval_races_source_cancel_single_consistent_outcome()
    {
        Guard();
        await using var seed = _fixture.CreateDbContext();
        var warehouseId = await SeedWarehouseAsync(seed, "来源取消仓");
        var productId = await SeedProductAsync(seed, "来源取消商品");
        var customerId = await SeedCustomerAsync(seed, "来源取消客户");
        var userId = await SeedSalesmanAsync(seed, customerId);
        var shipment = await SeedStockOutAsync(seed, customerId, warehouseId, DocumentStatus.Approved,
            (productId, "PCS", 10m));
        var ret = await SeedSalesReturnAsync(seed, shipment.Id, shipment.StockOutNo, customerId, warehouseId,
            DocumentStatus.Submitted, (productId, "PCS", 4m));

        // 两条独立 DbContext / 连接：并发执行真实的退货审核与来源取消（含同一把来源行锁 + 可串行化事务）
        var results = await Task.WhenAll(
            TryApproveSalesReturnAsync(ret.Id, userId),
            TryCancelStockOutAsync(shipment.Id, userId));

        Assert.Equal(1, results.Count(r => r.Success));
        Assert.Equal(1, results.Count(r => !r.Success));

        await using (var db = _fixture.CreateDbContext())
        {
            var returnStatus = await db.SalesReturns.Where(r => r.Id == ret.Id).Select(r => r.Status).SingleAsync();
            var shipmentStatus = await db.StockOuts.Where(o => o.Id == shipment.Id).Select(o => o.Status).SingleAsync();
            var stock = await db.Stocks.Where(s => s.WarehouseId == warehouseId && s.ProductId == productId)
                .SumAsync(s => s.Quantity);
            var returnSource = await db.SalesReturns.Where(r => r.Id == ret.Id)
                .Select(r => r.SourceStockOutId).SingleAsync();

            // 只允许一种一致结果：退货已审核（来源保持已审核）異或 来源已取消（退货未审核）
            var returnApproved = returnStatus == DocumentStatus.Approved;
            var shipmentCancelled = shipmentStatus == DocumentStatus.Cancelled;
            Assert.True(returnApproved ^ shipmentCancelled);
            Assert.Equal(shipment.Id, returnSource);

            if (returnApproved)
            {
                Assert.Equal(DocumentStatus.Approved, shipmentStatus);
                Assert.Equal(4m, stock);
                Assert.Equal(1, await db.StockMovements.CountAsync(m => m.WarehouseId == warehouseId && !m.IsReversal));
            }
            else
            {
                Assert.Equal(DocumentStatus.Submitted, returnStatus);
                Assert.Equal(10m, stock);
            }
        }
    }

    // ==================== 8. 两条独立连接竞争：采购退货审核 与 来源入库单取消 ====================

    [Fact]
    public async Task Two_connections_purchase_return_approval_races_source_cancel_single_consistent_outcome()
    {
        Guard();
        await using var seed = _fixture.CreateDbContext();
        var warehouseId = await SeedWarehouseAsync(seed, "来源取消仓");
        var productId = await SeedProductAsync(seed, "来源取消商品");
        var supplierId = await SeedSupplierAsync(seed, "来源取消供应商");
        var userId = await SeedPrivilegedOperatorAsync(seed,
            PurchaseReturnSourceRules.RequiredMenuCode, StockInAuthorizationRules.RequiredMenuCode);
        await SeedStockAsync(seed, warehouseId, productId, quantity: 100m, totalCost: 1000m);
        var receipt = await SeedStockInAsync(seed, supplierId, warehouseId, DocumentStatus.Approved,
            (productId, "PCS", 10m));
        var ret = await SeedPurchaseReturnAsync(seed, receipt.Id, receipt.StockInNo, supplierId, warehouseId,
            DocumentStatus.Submitted, (productId, "PCS", 4m));

        var results = await Task.WhenAll(
            TryApprovePurchaseReturnAsync(ret.Id, userId),
            TryCancelStockInAsync(receipt.Id, userId));

        Assert.Equal(1, results.Count(r => r.Success));
        Assert.Equal(1, results.Count(r => !r.Success));

        await using (var db = _fixture.CreateDbContext())
        {
            var returnStatus = await db.PurchaseReturns.Where(r => r.Id == ret.Id).Select(r => r.Status).SingleAsync();
            var receiptStatus = await db.StockIns.Where(o => o.Id == receipt.Id).Select(o => o.Status).SingleAsync();
            var stock = await db.Stocks.Where(s => s.WarehouseId == warehouseId && s.ProductId == productId)
                .SumAsync(s => s.Quantity);
            var returnSource = await db.PurchaseReturns.Where(r => r.Id == ret.Id)
                .Select(r => r.SourceStockInId).SingleAsync();

            var returnApproved = returnStatus == DocumentStatus.Approved;
            var receiptCancelled = receiptStatus == DocumentStatus.Cancelled;
            Assert.True(returnApproved ^ receiptCancelled);
            Assert.Equal(receipt.Id, returnSource);

            if (returnApproved)
            {
                Assert.Equal(DocumentStatus.Approved, receiptStatus);
                Assert.Equal(96m, stock);
                Assert.Equal(1, await db.StockMovements.CountAsync(m => m.WarehouseId == warehouseId && !m.IsReversal));
            }
            else
            {
                Assert.Equal(DocumentStatus.Submitted, returnStatus);
                Assert.Equal(90m, stock);
            }
        }
    }

    // ==================== 独立连接执行真实业务操作（竞争用例） ====================

    private async Task<(bool Success, string Error)> TryApproveSalesReturnAsync(long returnId, long userId)
    {
        await using var db = _fixture.CreateDbContext();
        try
        {
            await NewSalesReturnController(db, userId).Approve(returnId);
            return (true, string.Empty);
        }
        catch (BusinessException ex)
        {
            return (false, ex.Message);
        }
    }

    private async Task<(bool Success, string Error)> TryCancelStockOutAsync(long stockOutId, long userId)
    {
        await using var db = _fixture.CreateDbContext();
        try
        {
            await NewStockOutController(db, userId).Cancel(stockOutId);
            return (true, string.Empty);
        }
        catch (BusinessException ex)
        {
            return (false, ex.Message);
        }
    }

    private async Task<(bool Success, string Error)> TryApprovePurchaseReturnAsync(long returnId, long userId)
    {
        await using var db = _fixture.CreateDbContext();
        try
        {
            await NewPurchaseReturnController(db, userId).Approve(returnId);
            return (true, string.Empty);
        }
        catch (BusinessException ex)
        {
            return (false, ex.Message);
        }
    }

    private async Task<(bool Success, string Error)> TryCancelStockInAsync(long stockInId, long userId)
    {
        await using var db = _fixture.CreateDbContext();
        try
        {
            await NewStockInController(db, userId).Cancel(stockInId);
            return (true, string.Empty);
        }
        catch (BusinessException ex)
        {
            return (false, ex.Message);
        }
    }

    // ==================== 播种助手（真实主数据 + 真实授权身份） ====================

    private static async Task<SysMenu> SeedMenuAsync(ErpDbContext db, string code, string name, string path)
    {
        var menu = await db.SysMenus.FirstOrDefaultAsync(m => m.MenuCode == code && !m.IsDeleted);
        if (menu is not null) return menu;

        menu = new SysMenu
        {
            ParentId = 0, MenuCode = code, MenuName = name, Path = path,
            Icon = "box", SortOrder = 20, MenuType = MenuType.Menu
        };
        db.SysMenus.Add(menu);
        await db.SaveChangesAsync();
        return menu;
    }

    /// <summary>播种真实业务员账号（非特权）：既有「销售退货」菜单 + 被分配客户（真实数据范围）。</summary>
    private static async Task<long> SeedSalesmanAsync(ErpDbContext db, long customerId)
    {
        var role = new SysRole { RoleCode = $"SRC-SR-{Tag()}", RoleName = "销售退货操作员" };
        db.SysRoles.Add(role);
        await db.SaveChangesAsync();

        var userName = $"src-sr-{Tag()}";
        var user = new SysUser
        {
            UserName = userName, PasswordHash = "hash", PasswordSalt = "salt",
            DisplayName = "销售退货操作员", Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        await db.SaveChangesAsync();

        var employee = new BaseEmployee
        {
            EmployeeCode = userName, EmployeeName = "销售退货操作员", IsSalesman = true, Status = 1
        };
        db.BaseEmployees.Add(employee);
        await db.SaveChangesAsync();

        var customer = await db.BaseCustomers.SingleAsync(c => c.Id == customerId);
        customer.EmpId = employee.Id;

        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        await db.SaveChangesAsync();

        var menu = await SeedMenuAsync(db, SalesReturnSourceRules.RequiredMenuCode,
            SalesReturnSourceRules.RequiredMenuText, "/logistics/sales-return");
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
        await db.SaveChangesAsync();
        return user.Id;
    }

    /// <summary>播种特权操作员账号（内置系统角色）：用于同时执行来源入库取消与采购退货审核。</summary>
    private static async Task<long> SeedPrivilegedOperatorAsync(ErpDbContext db, params string[] menuCodes)
    {
        var role = new SysRole { RoleCode = $"SRC-OP-{Tag()}", RoleName = "来源取消验证操作员", IsSystem = true };
        db.SysRoles.Add(role);
        await db.SaveChangesAsync();

        var userName = $"src-op-{Tag()}";
        var user = new SysUser
        {
            UserName = userName, PasswordHash = "hash", PasswordSalt = "salt",
            DisplayName = "来源取消验证操作员", Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        await db.SaveChangesAsync();

        var employee = new BaseEmployee
        {
            EmployeeCode = userName, EmployeeName = "来源取消验证操作员", IsSalesman = true, Status = 1
        };
        db.BaseEmployees.Add(employee);
        await db.SaveChangesAsync();

        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        await db.SaveChangesAsync();

        foreach (var code in menuCodes)
        {
            var menu = await SeedMenuAsync(db, code, code, $"/logistics/{code}");
            db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
        }
        await db.SaveChangesAsync();
        return user.Id;
    }

    private static async Task<long> SeedWarehouseAsync(ErpDbContext db, string name)
    {
        var warehouse = new BaseWarehouse { WarehouseCode = $"SRCW-{Tag()}", WarehouseName = name, Status = 1 };
        db.BaseWarehouses.Add(warehouse);
        await db.SaveChangesAsync();
        return warehouse.Id;
    }

    private static async Task<long> SeedProductAsync(ErpDbContext db, string name, string unit = "PCS")
    {
        var product = new BaseProduct
        {
            ProductCode = $"SRCP-{Tag()}", ProductName = name, Spec = "规格A", Unit = unit, Status = 1
        };
        db.BaseProducts.Add(product);
        await db.SaveChangesAsync();
        return product.Id;
    }

    private static async Task<long> SeedCustomerAsync(ErpDbContext db, string name)
    {
        var customer = new BaseCustomer { CustomerCode = $"SRCC-{Tag()}", CustomerName = name, Status = 1 };
        db.BaseCustomers.Add(customer);
        await db.SaveChangesAsync();
        return customer.Id;
    }

    private static async Task<long> SeedSupplierAsync(ErpDbContext db, string name)
    {
        var supplier = new BaseSupplier { SupplierCode = $"SRCS-{Tag()}", SupplierName = name, Status = 1 };
        db.BaseSuppliers.Add(supplier);
        await db.SaveChangesAsync();
        return supplier.Id;
    }

    private static async Task SeedStockAsync(ErpDbContext db, long warehouseId, long productId,
        decimal quantity, decimal totalCost)
    {
        db.Stocks.Add(new Stock
        {
            WarehouseId = warehouseId,
            ProductId = productId,
            Quantity = quantity,
            AvailableQuantity = quantity,
            TotalCost = totalCost,
            AverageCost = quantity > 0 ? Math.Round(totalCost / quantity, 6) : 0m
        });
        await db.SaveChangesAsync();
    }

    private static async Task<StockOut> SeedStockOutAsync(ErpDbContext db, long customerId, long warehouseId,
        DocumentStatus status, params (long ProductId, string Unit, decimal Quantity)[] lines)
    {
        var shipment = new StockOut
        {
            StockOutNo = $"CK-SRC-SQL-{Tag()}",
            StockOutDate = DateTime.Today,
            CustomerId = customerId,
            WarehouseId = warehouseId,
            Status = status,
            TotalQuantity = lines.Sum(l => l.Quantity),
            Details = lines.Select(l => new StockOutDetail
            {
                ProductId = l.ProductId,
                ProductName = $"商品{l.ProductId}",
                Unit = l.Unit,
                Quantity = l.Quantity
            }).ToList()
        };
        db.StockOuts.Add(shipment);
        await db.SaveChangesAsync();
        return shipment;
    }

    private static async Task<StockIn> SeedStockInAsync(ErpDbContext db, long supplierId, long warehouseId,
        DocumentStatus status, params (long ProductId, string Unit, decimal Quantity)[] lines)
    {
        var receipt = new StockIn
        {
            StockInNo = $"RK-SRC-SQL-{Tag()}",
            StockInDate = DateTime.Today,
            SupplierId = supplierId,
            WarehouseId = warehouseId,
            Status = status,
            TotalQuantity = lines.Sum(l => l.Quantity),
            Details = lines.Select(l => new StockInDetail
            {
                ProductId = l.ProductId,
                ProductName = $"商品{l.ProductId}",
                Unit = l.Unit,
                Quantity = l.Quantity
            }).ToList()
        };
        db.StockIns.Add(receipt);
        await db.SaveChangesAsync();
        return receipt;
    }

    private static async Task<SalesReturn> SeedSalesReturnAsync(ErpDbContext db, long? sourceStockOutId,
        string sourceNo, long customerId, long warehouseId, DocumentStatus status,
        params (long ProductId, string Unit, decimal Quantity)[] lines)
    {
        var ret = new SalesReturn
        {
            ReturnNo = $"XTH-SRC-SQL-{Tag()}",
            ReturnDate = DateTime.Today,
            CustomerId = customerId,
            CustomerName = "来源取消客户",
            WarehouseId = warehouseId,
            SourceStockOutId = sourceStockOutId,
            SourceStockOutNo = sourceNo,
            ReturnReason = "质量",
            Status = status,
            TotalQuantity = lines.Sum(l => l.Quantity),
            Details = lines.Select(l => new SalesReturnDetail
            {
                ProductId = l.ProductId,
                ProductName = $"商品{l.ProductId}",
                Spec = "规格A",
                Unit = l.Unit,
                Quantity = l.Quantity,
                UnitPrice = 10m,
                Amount = Math.Round(l.Quantity * 10m, 4),
                UnitCost = 8m
            }).ToList()
        };
        db.SalesReturns.Add(ret);
        await db.SaveChangesAsync();
        return ret;
    }

    private static async Task<PurchaseReturn> SeedPurchaseReturnAsync(ErpDbContext db, long? sourceStockInId,
        string sourceNo, long supplierId, long warehouseId, DocumentStatus status,
        params (long ProductId, string Unit, decimal Quantity)[] lines)
    {
        var ret = new PurchaseReturn
        {
            ReturnNo = $"CTH-SRC-SQL-{Tag()}",
            ReturnDate = DateTime.Today,
            SupplierId = supplierId,
            SupplierName = "来源取消供应商",
            WarehouseId = warehouseId,
            SourceStockInId = sourceStockInId,
            SourceStockInNo = sourceNo,
            ReturnReason = "规格不符",
            Status = status,
            TotalQuantity = lines.Sum(l => l.Quantity),
            Details = lines.Select(l => new PurchaseReturnDetail
            {
                ProductId = l.ProductId,
                ProductName = $"商品{l.ProductId}",
                Spec = "规格A",
                Unit = l.Unit,
                Quantity = l.Quantity,
                UnitPrice = 10m,
                Amount = Math.Round(l.Quantity * 10m, 4),
                UnitCost = 8m
            }).ToList()
        };
        db.PurchaseReturns.Add(ret);
        await db.SaveChangesAsync();
        return ret;
    }
}

/// <summary>
/// 专用 localdb 夹具：仅当目标为 <c>(localdb)\NEWERP_AutoAcceptance</c> 且库名前缀 <c>NEWERP_AUTOTEST</c>、
/// 集成安全时才建立完整 NEWERP 结构 + 种子数据；库名为全新 GUID 后缀，发现同名库已存在立即拒绝，
/// 绝不 drop / reset / 复用任何数据库，也绝不读取 appsettings / .env / 生产凭据。
/// </summary>
public sealed class ReturnSourceCancellationSqlServerFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";
    public const string DefaultDatabaseName = "NEWERP_AUTOTEST_RETURNSOURCECANCEL_20261008";

    public string ConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = BuildDefaultConnectionString();

        AssertDedicatedTarget(connectionString);
        ConnectionString = connectionString;

        Console.WriteLine($"[ERP-359] 目标库护栏放行（实例含 {InstanceMarker}，库名前缀 {DatabasePrefix}，集成安全）。");

        await ResetToFullDatabaseAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public ErpDbContext CreateDbContext() => new(BuildOptions());

    private DbContextOptions<ErpDbContext> BuildOptions()
        => new DbContextOptionsBuilder<ErpDbContext>().UseSqlServer(ConnectionString).Options;

    private static string BuildDefaultConnectionString()
        => $"Server=(localdb)\\{InstanceMarker};Initial Catalog={DefaultDatabaseName}_{Guid.NewGuid():N};" +
           "Integrated Security=true;TrustServerCertificate=true;";

    internal static void AssertDedicatedTarget(string connectionString)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        var server = builder.DataSource ?? string.Empty;
        var database = builder.InitialCatalog ?? string.Empty;

        Assert.Equal($"(localdb)\\{InstanceMarker}", server, ignoreCase: true);
        Assert.StartsWith(DatabasePrefix, database, StringComparison.OrdinalIgnoreCase);
        Assert.True(builder.IntegratedSecurity);
    }

    private async Task ResetToFullDatabaseAsync()
    {
        var builder = new SqlConnectionStringBuilder(ConnectionString);
        var database = builder.InitialCatalog;

        // 破坏性初始化前再次护栏：绝不使用生产回退。
        AssertDedicatedTarget(ConnectionString);

        var master = new SqlConnectionStringBuilder(ConnectionString) { InitialCatalog = "master" };
        await using (var conn = new SqlConnection(master.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            // 绝不销毁已存在的夹具库或其它调用方的数据库。
            cmd.CommandText = "SELECT DB_ID(@database)";
            cmd.Parameters.AddWithValue("@database", database);
            var existing = await cmd.ExecuteScalarAsync();
            if (existing is not null && existing != DBNull.Value)
                throw new InvalidOperationException(
                    "The isolated fixture database already exists; choose a fresh NEWERP_AUTOTEST database.");
        }

        await using (var db = CreateDbContext())
        {
            await db.Database.EnsureCreatedAsync();
            await SchemaUpgrader.EnsureUpgradedAsync(db);
            await SeedData.InitializeAsync(db);
            await SchemaUpgrader.EnsureUpgradedAsync(db);
        }

        Console.WriteLine("[ERP-359] 集成场景就绪：完整 NEWERP 结构 + 种子数据。");
    }
}

/// <summary>目标库护栏单元级校验：非专用目标必须在访问数据库之前被拒绝。</summary>
public sealed class ReturnSourceCancellationTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(
            () => ReturnSourceCancellationSqlServerFixture.AssertDedicatedTarget(connection));
}

