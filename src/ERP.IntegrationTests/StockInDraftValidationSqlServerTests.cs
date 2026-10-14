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
/// ERP-504 采购入库草稿写入护栏的真实 SQL Server 集成测试（GUID 独占 <c>NEWERP_AUTOTEST</c> 目标）：
/// 用与运行时完全相同的调用路径（真实 <see cref="StockInController"/> + 真实 HTTP 身份 + 真实 SQL Server 上下文）验证：
/// <list type="number">
/// <item>创建（手工未链接与显式链接来源同口径）对「数量非正或不可表示 / 溢出、毛重与体积为负或不可表示、
/// 商品 Id 缺失 / 不存在 / 已删除 / 已停用」一律拒绝，且零单据、零明细、零库存、零流水、零单号消耗；</item>
/// <item>客户端 <c>IsDeleted=true</c> 不能隐藏坏行（服务端会把每一行恢复为正常行）与表头合计溢出同样被拒绝；</item>
/// <item>修改在明细整体替换之前收敛同样的非法输入，既有单据与明细逐字段不变、不消耗新单号；</item>
/// <item>空草稿头可保存但提交 / 审核要求有效明细；重复商品行按累计数量语义保留；合法草稿仍可审核过账并取消冲销；</item>
/// <item>保存后商品失效 / 已存数值被改写时，提交与审核在锁内实时复核并拒绝，零库存、零流水、状态不变。</item>
/// </list>
/// <para>安全口径：目标必须为专用 localdb 实例 <c>NEWERP_AutoAcceptance</c>、库名前缀 <c>NEWERP_AUTOTEST</c>
/// 且集成安全；连接串只来自进程环境变量或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据，
/// 每次运行只创建全新 GUID 后缀库，绝不 drop / reset / 复用任何数据库。</para>
/// </summary>
public sealed class StockInDraftValidationSqlServerTests : IClassFixture<StockInDraftValidationSqlServerFixture>
{
    private readonly StockInDraftValidationSqlServerFixture _fixture;

    public StockInDraftValidationSqlServerTests(StockInDraftValidationSqlServerFixture fixture)
        => _fixture = fixture;

    /// <summary>访问数据库前复核专用目标护栏（实例名 / 库名前缀 / 集成安全）。</summary>
    private void Guard()
    {
        var target = new SqlConnectionStringBuilder(_fixture.ConnectionString);
        Assert.Equal("(localdb)\\NEWERP_AutoAcceptance", target.DataSource, ignoreCase: true);
        Assert.StartsWith(StockInDraftValidationSqlServerFixture.DatabasePrefix, target.InitialCatalog,
            StringComparison.OrdinalIgnoreCase);
        Assert.True(target.IntegratedSecurity);
    }

    private static string Tag() => Guid.NewGuid().ToString("N")[..10];

    // ==================== 1. 创建：非法明细（手工未链接与显式链接来源同口径） ====================

    [Theory]
    [InlineData("missing-id", false, ErrorCodes.InvalidParameter)]
    [InlineData("missing-product", false, ErrorCodes.InvalidParameter)]
    [InlineData("deleted-product", false, ErrorCodes.InvalidParameter)]
    [InlineData("disabled-product", false, ErrorCodes.RuleConflict)]
    [InlineData("zero-quantity", false, ErrorCodes.InvalidParameter)]
    [InlineData("negative-quantity", false, ErrorCodes.InvalidParameter)]
    [InlineData("unrepresentable-quantity", false, ErrorCodes.InvalidParameter)]
    [InlineData("overflow-quantity", false, ErrorCodes.InvalidParameter)]
    [InlineData("negative-weight", false, ErrorCodes.InvalidParameter)]
    [InlineData("unrepresentable-weight", false, ErrorCodes.InvalidParameter)]
    [InlineData("negative-volume", false, ErrorCodes.InvalidParameter)]
    [InlineData("unrepresentable-volume", false, ErrorCodes.InvalidParameter)]
    [InlineData("zero-quantity", true, ErrorCodes.InvalidParameter)]
    [InlineData("negative-quantity", true, ErrorCodes.InvalidParameter)]
    [InlineData("unrepresentable-quantity", true, ErrorCodes.InvalidParameter)]
    [InlineData("overflow-quantity", true, ErrorCodes.InvalidParameter)]
    [InlineData("negative-weight", true, ErrorCodes.InvalidParameter)]
    [InlineData("unrepresentable-volume", true, ErrorCodes.InvalidParameter)]
    [InlineData("disabled-product", true, ErrorCodes.RuleConflict)]
    public async Task Create_rejects_invalid_lines_before_any_write(string scenario, bool linked, int expectedCode)
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var supplierId = await SeedSupplierAsync(db);
        var warehouseId = await SeedWarehouseAsync(db);
        var productId = await SeedProductAsync(db);
        var userId = await SeedOperatorAsync(db);

        if (scenario == "deleted-product")
            await SetProductFlagsAsync(db, productId, isDeleted: true, status: null);
        if (scenario == "disabled-product")
            await SetProductFlagsAsync(db, productId, isDeleted: null, status: 0);

        long? orderId = linked ? await SeedApprovedOrderAsync(db, supplierId, productId, capacity: 100m) : null;

        var (lineProductId, quantity, weight, volume) = scenario switch
        {
            "missing-id" => (0L, 1m, 0m, 0m),
            "missing-product" => (999_999_999L, 1m, 0m, 0m),
            "zero-quantity" => (productId, 0m, 0m, 0m),
            "negative-quantity" => (productId, -1m, 0m, 0m),
            "unrepresentable-quantity" => (productId, 1.005m, 0m, 0m),
            "overflow-quantity" => (productId, 10_000_000_000_000_000m, 0m, 0m),
            "negative-weight" => (productId, 1m, -1m, 0m),
            "unrepresentable-weight" => (productId, 1m, 0.005m, 0m),
            "negative-volume" => (productId, 1m, 0m, -1m),
            "unrepresentable-volume" => (productId, 1m, 0m, 0.005m),
            _ => (productId, 1m, 0m, 0m)
        };

        var before = await SnapshotAsync(db);
        var controller = ControllerFor(db, userId);

        var error = await Assert.ThrowsAsync<BusinessException>(() =>
            controller.Create(Draft(supplierId, warehouseId, orderId, Line(lineProductId, quantity, weight, volume))));

        Assert.Equal(expectedCode, error.Code);
        await AssertNoWriteAsync(db, before);
    }

    [Fact]
    public async Task Create_rejects_overflowing_header_total_before_any_write()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var supplierId = await SeedSupplierAsync(db);
        var warehouseId = await SeedWarehouseAsync(db);
        var productA = await SeedProductAsync(db, "商品A");
        var productB = await SeedProductAsync(db, "商品B");
        var userId = await SeedOperatorAsync(db);

        // 单行数量各自可表示，但两行合计溢出 —— 必须校验 Calculate 之后的表头合计。
        var max = StockInAuthorizationRules.MaxDecimal18Scale2;
        var before = await SnapshotAsync(db);
        var controller = ControllerFor(db, userId);

        var error = await Assert.ThrowsAsync<BusinessException>(() => controller.Create(
            Draft(supplierId, warehouseId, null, Line(productA, max), Line(productB, max))));

        Assert.Equal(ErrorCodes.InvalidParameter, error.Code);
        await AssertNoWriteAsync(db, before);
    }

    [Fact]
    public async Task Create_rejects_client_deleted_bad_line_before_any_write()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var supplierId = await SeedSupplierAsync(db);
        var warehouseId = await SeedWarehouseAsync(db);
        var productId = await SeedProductAsync(db);
        var userId = await SeedOperatorAsync(db);

        var before = await SnapshotAsync(db);
        var controller = ControllerFor(db, userId);

        var error = await Assert.ThrowsAsync<BusinessException>(() => controller.Create(
            Draft(supplierId, warehouseId, null, Line(productId, 1m), Line(productId, 0m, isDeleted: true))));

        Assert.Equal(ErrorCodes.InvalidParameter, error.Code);
        await AssertNoWriteAsync(db, before);
    }

    // ==================== 2. 创建：空草稿头 / 重复商品行 / 合法过账与取消 ====================

    [Fact]
    public async Task Create_allows_empty_draft_header_but_submit_and_approve_require_lines()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var supplierId = await SeedSupplierAsync(db);
        var warehouseId = await SeedWarehouseAsync(db);
        var userId = await SeedOperatorAsync(db);

        var controller = ControllerFor(db, userId);
        var createdId = CreatedId(await controller.Create(Draft(supplierId, warehouseId, null)));

        Assert.Empty(await db.StockInDetails.Where(d => d.StockInId == createdId).ToListAsync());

        var submitError = await Assert.ThrowsAsync<BusinessException>(() => controller.Submit(createdId));
        Assert.Equal(ErrorCodes.RuleConflict, submitError.Code);
        Assert.Equal(DocumentStatus.Pending,
            (await db.StockIns.AsNoTracking().SingleAsync(o => o.Id == createdId)).Status);

        // 历史形状：直接置为「已提交」后审核同样拒绝（承诺边界要求至少一行有效明细）。
        await SetDocumentStatusAsync(db, createdId, DocumentStatus.Submitted);
        var approveError = await Assert.ThrowsAsync<BusinessException>(() => controller.Approve(createdId));
        Assert.Equal(ErrorCodes.RuleConflict, approveError.Code);
        Assert.Equal(DocumentStatus.Submitted,
            (await db.StockIns.AsNoTracking().SingleAsync(o => o.Id == createdId)).Status);
        Assert.Equal(0, await db.StockMovements.CountAsync(m => m.SourceDocId == createdId));
    }

    [Fact]
    public async Task Create_allows_repeated_product_rows_and_posts_cumulative_quantity()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var supplierId = await SeedSupplierAsync(db);
        var warehouseId = await SeedWarehouseAsync(db);
        var productId = await SeedProductAsync(db);
        var userId = await SeedOperatorAsync(db);
        var controller = ControllerFor(db, userId);

        var createdId = CreatedId(await controller.Create(
            Draft(supplierId, warehouseId, null, Line(productId, 4m), Line(productId, 2m))));

        Assert.Equal(6m, (await db.StockIns.AsNoTracking().SingleAsync(o => o.Id == createdId)).TotalQuantity);
        Assert.Equal(2, await db.StockInDetails.CountAsync(d => d.StockInId == createdId));

        await controller.Submit(createdId);
        Assert.IsType<OkObjectResult>(await controller.Approve(createdId));

        Assert.Equal(6m, await db.Stocks.AsNoTracking()
            .Where(s => s.WarehouseId == warehouseId && s.ProductId == productId).SumAsync(s => s.Quantity));
        Assert.Equal(2, await db.StockMovements.CountAsync(m => m.SourceDocId == createdId
            && m.MovementType == InventoryMovementType.PurchaseIn));
    }

    [Fact]
    public async Task Approve_of_linked_draft_posts_once_and_cancel_reverses()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var supplierId = await SeedSupplierAsync(db);
        var warehouseId = await SeedWarehouseAsync(db);
        var productId = await SeedProductAsync(db);
        var userId = await SeedOperatorAsync(db);
        var orderId = await SeedApprovedOrderAsync(db, supplierId, productId, capacity: 10m);

        var controller = ControllerFor(db, userId);
        var createdId = CreatedId(await controller.Create(
            Draft(supplierId, warehouseId, orderId, Line(productId, 6m))));

        await controller.Submit(createdId);
        Assert.IsType<OkObjectResult>(await controller.Approve(createdId));
        Assert.Equal(6m, await db.Stocks.AsNoTracking()
            .Where(s => s.WarehouseId == warehouseId && s.ProductId == productId).SumAsync(s => s.Quantity));

        // 超收（剩余 4 却报 5）：受控拒绝且零库存 / 零流水，状态保持已提交。
        var overId = CreatedId(await controller.Create(
            Draft(supplierId, warehouseId, orderId, Line(productId, 5m))));
        await controller.Submit(overId);
        var overError = await Assert.ThrowsAsync<BusinessException>(() => controller.Approve(overId));
        Assert.Equal(ErrorCodes.RuleConflict, overError.Code);
        Assert.Equal(DocumentStatus.Submitted,
            (await db.StockIns.AsNoTracking().SingleAsync(o => o.Id == overId)).Status);
        Assert.Equal(0, await db.StockMovements.CountAsync(m => m.SourceDocId == overId));

        // 取消释放额度：第二张可审核过账（既有 ERP-342 口径不变）。
        Assert.IsType<OkObjectResult>(await controller.Cancel(createdId));
        Assert.Equal(0m, await db.Stocks.AsNoTracking()
            .Where(s => s.WarehouseId == warehouseId && s.ProductId == productId).SumAsync(s => s.Quantity));
        Assert.IsType<OkObjectResult>(await controller.Approve(overId));
        Assert.Equal(5m, await db.Stocks.AsNoTracking()
            .Where(s => s.WarehouseId == warehouseId && s.ProductId == productId).SumAsync(s => s.Quantity));
    }

    // ==================== 3. 修改：非法输入零改动，合法输入保留服务端单号 ====================

    [Theory]
    [InlineData("zero-quantity", ErrorCodes.InvalidParameter)]
    [InlineData("negative-quantity", ErrorCodes.InvalidParameter)]
    [InlineData("missing-product", ErrorCodes.InvalidParameter)]
    [InlineData("deleted-product", ErrorCodes.InvalidParameter)]
    [InlineData("disabled-product", ErrorCodes.RuleConflict)]
    [InlineData("hidden-bad-row", ErrorCodes.InvalidParameter)]
    public async Task Update_rejects_invalid_draft_and_leaves_document_unchanged(string scenario, int expectedCode)
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var supplierId = await SeedSupplierAsync(db);
        var warehouseId = await SeedWarehouseAsync(db);
        var productId = await SeedProductAsync(db);
        var userId = await SeedOperatorAsync(db);
        var controller = ControllerFor(db, userId);

        var createdId = CreatedId(await controller.Create(
            Draft(supplierId, warehouseId, null, Line(productId, 4m, weight: 1m))));
        var created = await db.StockIns.AsNoTracking().SingleAsync(o => o.Id == createdId);
        var createdDetail = await db.StockInDetails.AsNoTracking().SingleAsync(d => d.StockInId == createdId);
        var before = await SnapshotAsync(db);

        if (scenario == "deleted-product")
            await SetProductFlagsAsync(db, productId, isDeleted: true, status: null);
        if (scenario == "disabled-product")
            await SetProductFlagsAsync(db, productId, isDeleted: null, status: 0);

        var edit = scenario switch
        {
            "zero-quantity" => Draft(supplierId, warehouseId, null, Line(productId, 0m)),
            "negative-quantity" => Draft(supplierId, warehouseId, null, Line(productId, -2m)),
            "missing-product" => Draft(supplierId, warehouseId, null, Line(999_999_999L, 3m)),
            "hidden-bad-row" => Draft(supplierId, warehouseId, null, Line(productId, 3m),
                Line(productId, 0m, isDeleted: true)),
            _ => Draft(supplierId, warehouseId, null, Line(productId, 3m))
        };

        var error = await Assert.ThrowsAsync<BusinessException>(() => controller.Update(createdId, edit));

        Assert.Equal(expectedCode, error.Code);
        await AssertNoWriteAsync(db, before);

        var stored = await db.StockIns.AsNoTracking().SingleAsync(o => o.Id == createdId);
        Assert.Equal(created.StockInNo, stored.StockInNo);
        Assert.Equal(DocumentStatus.Pending, stored.Status);
        Assert.False(stored.IsDeleted);
        Assert.Equal(4m, stored.TotalQuantity);
        Assert.Equal(1m, stored.TotalWeight);

        var storedDetail = await db.StockInDetails.AsNoTracking().SingleAsync(d => d.StockInId == createdId);
        Assert.Equal(createdDetail.Id, storedDetail.Id);
        Assert.Equal(productId, storedDetail.ProductId);
        Assert.Equal(4m, storedDetail.Quantity);
        Assert.Equal(0, await db.StockMovements.CountAsync(m => m.SourceDocId == createdId));
    }

    [Fact]
    public async Task Update_permits_valid_draft_and_keeps_server_number()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var supplierId = await SeedSupplierAsync(db);
        var warehouseId = await SeedWarehouseAsync(db);
        var productId = await SeedProductAsync(db);
        var userId = await SeedOperatorAsync(db);
        var controller = ControllerFor(db, userId);

        var createdId = CreatedId(await controller.Create(
            Draft(supplierId, warehouseId, null, Line(productId, 4m, weight: 1m))));
        var number = (await db.StockIns.AsNoTracking().SingleAsync(o => o.Id == createdId)).StockInNo;
        var before = await SnapshotAsync(db);

        var result = await controller.Update(createdId,
            Draft(supplierId, warehouseId, null, Line(productId, 7m, weight: 2.5m, volume: 1m)));

        Assert.IsType<OkObjectResult>(result);
        var stored = await db.StockIns.AsNoTracking().SingleAsync(o => o.Id == createdId);
        Assert.Equal(number, stored.StockInNo);
        Assert.Equal(DocumentStatus.Pending, stored.Status);
        Assert.Equal(7m, stored.TotalQuantity);
        Assert.Equal(2.5m, stored.TotalWeight);
        Assert.Equal(1m, stored.TotalVolume);
        Assert.Equal(before.Sequence, await NumberSequenceAsync(db));       // 不消耗新单号
        Assert.Equal(0, await db.StockMovements.CountAsync(m => m.SourceDocId == createdId));
    }

    // ==================== 4. 承诺边界：主数据 / 已存数值失效的实时复核 ====================

    [Theory]
    [InlineData("deleted-product", ErrorCodes.InvalidParameter)]
    [InlineData("disabled-product", ErrorCodes.RuleConflict)]
    public async Task Submit_revalidates_live_master_data(string scenario, int expectedCode)
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var supplierId = await SeedSupplierAsync(db);
        var warehouseId = await SeedWarehouseAsync(db);
        var productId = await SeedProductAsync(db);
        var userId = await SeedOperatorAsync(db);
        var controller = ControllerFor(db, userId);

        var createdId = CreatedId(await controller.Create(
            Draft(supplierId, warehouseId, null, Line(productId, 2m))));

        if (scenario == "deleted-product")
            await SetProductFlagsAsync(db, productId, isDeleted: true, status: null);
        if (scenario == "disabled-product")
            await SetProductFlagsAsync(db, productId, isDeleted: null, status: 0);

        var error = await Assert.ThrowsAsync<BusinessException>(() => controller.Submit(createdId));

        Assert.Equal(expectedCode, error.Code);
        Assert.Equal(DocumentStatus.Pending,
            (await db.StockIns.AsNoTracking().SingleAsync(o => o.Id == createdId)).Status);
        Assert.Equal(0, await db.StockMovements.CountAsync(m => m.SourceDocId == createdId));
    }

    [Theory]
    [InlineData("zero-quantity", ErrorCodes.InvalidParameter)]
    [InlineData("negative-quantity", ErrorCodes.InvalidParameter)]
    [InlineData("deleted-product", ErrorCodes.InvalidParameter)]
    [InlineData("disabled-product", ErrorCodes.RuleConflict)]
    public async Task Approve_revalidates_stored_values_and_live_master_data(string scenario, int expectedCode)
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var supplierId = await SeedSupplierAsync(db);
        var warehouseId = await SeedWarehouseAsync(db);
        var productId = await SeedProductAsync(db);
        var userId = await SeedOperatorAsync(db);
        var controller = ControllerFor(db, userId);

        var createdId = CreatedId(await controller.Create(
            Draft(supplierId, warehouseId, null, Line(productId, 2m))));
        await controller.Submit(createdId);

        if (scenario == "deleted-product")
            await SetProductFlagsAsync(db, productId, isDeleted: true, status: null);
        if (scenario == "disabled-product")
            await SetProductFlagsAsync(db, productId, isDeleted: null, status: 0);
        if (scenario is "zero-quantity" or "negative-quantity")
        {
            var stored = await db.StockInDetails.SingleAsync(d => d.StockInId == createdId);
            stored.Quantity = scenario == "zero-quantity" ? 0m : -2m;
            await db.SaveChangesAsync();
        }

        var error = await Assert.ThrowsAsync<BusinessException>(() => controller.Approve(createdId));

        Assert.Equal(expectedCode, error.Code);
        Assert.Equal(DocumentStatus.Submitted,
            (await db.StockIns.AsNoTracking().SingleAsync(o => o.Id == createdId)).Status);
        Assert.Equal(0, await db.StockMovements.CountAsync(m => m.SourceDocId == createdId));
        Assert.Equal(0, await db.Stocks.CountAsync(s => s.WarehouseId == warehouseId && s.ProductId == productId));
    }

    // ==================== 测试辅助 ====================

    private sealed record Snapshot(long Documents, long Details, long Movements, long Stocks, long? Sequence);

    private static StockInController ControllerFor(ErpDbContext db, long userId)
        => new(db, new DocumentNumberService(db), new InventoryService(db))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        new[] { new Claim(ClaimTypes.NameIdentifier, userId.ToString()) }, "Test"))
                }
            }
        };

    private static StockIn Draft(long supplierId, long warehouseId, long? purchaseOrderId,
        params StockInDetail[] lines)
        => new()
        {
            StockInDate = DateTime.Today,
            SupplierId = supplierId,
            WarehouseId = warehouseId,
            PurchaseOrderId = purchaseOrderId,
            Remark = "ERP-504_SQL",
            Details = lines.ToList()
        };

    private static StockInDetail Line(long productId, decimal quantity, decimal weight = 0m,
        decimal volume = 0m, bool isDeleted = false)
        => new()
        {
            ProductId = productId,
            ProductName = $"商品{productId}",
            Spec = "规格A",
            Unit = "PCS",
            Quantity = quantity,
            Weight = weight,
            Volume = volume,
            IsDeleted = isDeleted
        };

    private static long CreatedId(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var payload = Assert.IsType<ApiResponse<object>>(ok.Value).Data!;
        return (long)payload.GetType().GetProperty("Id")!.GetValue(payload)!;
    }

    private static async Task<Snapshot> SnapshotAsync(ErpDbContext db)
        => new(await db.StockIns.CountAsync(), await db.StockInDetails.CountAsync(),
            await db.StockMovements.CountAsync(), await db.Stocks.CountAsync(), await NumberSequenceAsync(db));

    /// <summary>拒绝场景的统一断言：零单据、零明细、零库存、零流水、零单号消耗。</summary>
    private static async Task AssertNoWriteAsync(ErpDbContext db, Snapshot before)
    {
        Assert.Equal(before.Documents, await db.StockIns.CountAsync());
        Assert.Equal(before.Details, await db.StockInDetails.CountAsync());
        Assert.Equal(before.Movements, await db.StockMovements.CountAsync());
        Assert.Equal(before.Stocks, await db.Stocks.CountAsync());
        Assert.Equal(before.Sequence, await NumberSequenceAsync(db));
    }

    private static async Task<long?> NumberSequenceAsync(ErpDbContext db)
        => await db.SysDocumentNumberRules.AsNoTracking()
            .Where(r => r.DocumentType == DocumentType.StockIn)
            .Select(r => (long?)r.CurrentSequence)
            .FirstOrDefaultAsync();

    // ==================== 真实 SQL 夹具种子（一律使用 SQL Server 自增主键） ====================

    private static async Task<long> SeedSupplierAsync(ErpDbContext db)
    {
        var supplier = new BaseSupplier
        {
            SupplierCode = $"SIDV-{Tag()}",
            SupplierName = "草稿校验供应商",
            Status = 1
        };
        db.BaseSuppliers.Add(supplier);
        await db.SaveChangesAsync();
        return supplier.Id;
    }

    private static async Task<long> SeedWarehouseAsync(ErpDbContext db)
    {
        var warehouse = new BaseWarehouse
        {
            WarehouseCode = $"SIDW-{Tag()}",
            WarehouseName = "草稿校验仓",
            Status = 1
        };
        db.BaseWarehouses.Add(warehouse);
        await db.SaveChangesAsync();
        return warehouse.Id;
    }

    private static async Task<long> SeedProductAsync(ErpDbContext db, string name = "草稿校验商品")
    {
        var product = new BaseProduct
        {
            ProductCode = $"SIDP-{Tag()}",
            ProductName = name,
            Spec = "规格A",
            Unit = "PCS",
            Status = 1
        };
        db.BaseProducts.Add(product);
        await db.SaveChangesAsync();
        return product.Id;
    }

    private static async Task<long> SeedApprovedOrderAsync(ErpDbContext db, long supplierId, long productId,
        decimal capacity)
    {
        var order = new PurchaseOrder
        {
            OrderNo = $"SID-PO-{Tag()}",
            OrderDate = DateTime.Today,
            SupplierId = supplierId,
            Currency = Currency.CNY,
            ExchangeRate = 1m,
            Status = DocumentStatus.Approved
        };
        db.PurchaseOrders.Add(order);
        await db.SaveChangesAsync();

        db.PurchaseOrderDetails.Add(new PurchaseOrderDetail
        {
            PurchaseOrderId = order.Id,
            ProductId = productId,
            ProductName = "草稿校验商品",
            Spec = "规格A",
            Unit = "PCS",
            Quantity = capacity,
            UnitPrice = 5m,
            Amount = capacity * 5m
        });
        await db.SaveChangesAsync();
        return order.Id;
    }

    /// <summary>复用既有「admin」种子账号（SuperAdmin 特权身份 + 既有全部菜单），不新增任何用户授权。</summary>
    private static async Task<long> SeedOperatorAsync(ErpDbContext db)
        => await db.SysUsers.AsNoTracking()
            .Where(u => u.UserName == SeedData.AdminUserName && !u.IsDeleted)
            .Select(u => u.Id)
            .FirstAsync();

    private static async Task SetProductFlagsAsync(ErpDbContext db, long id, bool? isDeleted, int? status)
    {
        var product = await db.BaseProducts.SingleAsync(p => p.Id == id);
        if (isDeleted.HasValue) product.IsDeleted = isDeleted.Value;
        if (status.HasValue) product.Status = status.Value;
        await db.SaveChangesAsync();
    }

    private static async Task SetDocumentStatusAsync(ErpDbContext db, long id, DocumentStatus status)
    {
        var document = await db.StockIns.SingleAsync(o => o.Id == id);
        document.Status = status;
        await db.SaveChangesAsync();
    }
}

/// <summary>
/// 专用 localdb 目标 Fixture（ERP-504）：全新 GUID 后缀库 → 完整 NEWERP 结构 + 种子数据。
/// <para>每次运行只创建一个全新 GUID 后缀库；发现同名库已存在立即拒绝；绝不 drop / reset / 复用任何数据库，
/// 也绝不读取 appsettings / .env / 生产凭据。</para>
/// </summary>
public sealed class StockInDraftValidationSqlServerFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";
    public const string DefaultDatabaseName = "NEWERP_AUTOTEST_STOCKINDRAFT_20261014";

    public string ConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = BuildDefaultConnectionString();

        AssertDedicatedTarget(connectionString);
        ConnectionString = connectionString;

        Console.WriteLine($"[ERP-504] 目标库护栏放行（实例含 {InstanceMarker}，库名前缀 {DatabasePrefix}）。");

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

        // 破坏性初始化前再次护栏：绝不使用生产回退，也绝不复用已存在的库。
        AssertDedicatedTarget(ConnectionString);

        var master = new SqlConnectionStringBuilder(ConnectionString) { InitialCatalog = "master" };
        await using (var conn = new SqlConnection(master.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
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

        Console.WriteLine("[ERP-504] 集成场景就绪：完整 NEWERP 结构 + 种子数据。");
    }
}

/// <summary>目标库护栏单元级校验：非专用目标必须在访问数据库之前被拒绝。</summary>
public sealed class StockInDraftValidationTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(() =>
            StockInDraftValidationSqlServerFixture.AssertDedicatedTarget(connection));
}
