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
using System.Security.Claims;
using Xunit;

namespace ERP.IntegrationTests;

/// <summary>
/// ERP-526 库存盘点单位完整性的真实 SQL Server 集成测试（NEWERP_AUTOTEST 护栏）：
/// 用与运行时完全相同的调用路径（真实 <see cref="StockAdjustmentController"/> + 真实 HTTP 身份 + 真实 SQL Server 上下文）验证：
/// <list type="number">
/// <item>创建 / 修改对「显式非空但不是商品当前基础单位」的明细单位（装箱单位 / 任意单位 / 大小写与空白变体）
/// 一律拒绝，且零单据、零明细、零库存流水、零单号消耗；</item>
/// <item>空 / 空白单位保持既有回退，基础单位（大小写、首尾空白）受理且账面 / 实盘数量与成本不折算；</item>
/// <item>商品未维护基础单位时，显式非空单位受控拒绝（绝不臆造新单位）；</item>
/// <item>提交 / 审核在既有单据 / 库存锁内用当前商品快照复核已存单位：主数据基础单位被改写即整体拒绝，
/// 状态 / 明细 / 库存余额 / 流水 / 单号逐项不变；直接落库的历史越界单位草稿同样被审核拒绝且零记账、仍可读；</item>
/// <item>合法基础单位 / 空单位审核恰好一次记账（差异按基础单位调整、单位归一），重复审核被拒绝，销审仍可冲销还原。</item>
/// </list>
/// <para>安全口径：目标必须为专用 localdb 实例 <c>NEWERP_AutoAcceptance</c> 且库名前缀 <c>NEWERP_AUTOTEST</c>；
/// 连接串只来自进程环境变量或专用 localdb 默认值，绝不读取 appsettings / .env / 生产凭据，也不执行生产库。</para>
/// </summary>
public sealed class StockAdjustmentUnitIntegritySqlServerTests
    : IClassFixture<StockAdjustmentUnitIntegritySqlServerFixture>
{
    private readonly StockAdjustmentUnitIntegritySqlServerFixture _fixture;

    public StockAdjustmentUnitIntegritySqlServerTests(StockAdjustmentUnitIntegritySqlServerFixture fixture)
        => _fixture = fixture;

    /// <summary>访问数据库前复核专用目标护栏（实例名 / 库名前缀 / 集成安全）。</summary>
    private void Guard()
    {
        var target = new SqlConnectionStringBuilder(_fixture.ConnectionString);
        Assert.Equal("(localdb)\\NEWERP_AutoAcceptance", target.DataSource, ignoreCase: true);
        Assert.StartsWith(StockAdjustmentUnitIntegritySqlServerFixture.DatabasePrefix, target.InitialCatalog,
            StringComparison.OrdinalIgnoreCase);
        Assert.True(target.IntegratedSecurity);
    }

    private static string Tag() => Guid.NewGuid().ToString("N")[..10];

    // ==================== 1. 创建：非基础单位一律拒绝（零写入 / 零单号） ====================

    [Theory]
    [InlineData("CTN")]
    [InlineData(" ctn ")]
    [InlineData("KG")]
    [InlineData("SET")]
    [InlineData("工程量")]
    public async Task Create_rejects_non_base_units_before_any_write(string unit)
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var warehouseId = await SeedWarehouseAsync(db, "盘点仓");
        var productId = await SeedProductAsync(db, "箱装商品", unit: "PCS", packageUnit: "CTN", factor: 12);
        var controller = CreateController(db, await SeedUserAsync(db));
        var before = await SnapshotAsync(db);

        var error = await Assert.ThrowsAsync<BusinessException>(() => controller.Create(
            Draft(warehouseId, (productId, "箱装商品", unit, 10m, 7m, 5m))));

        Assert.Equal(ErrorCodes.RuleConflict, error.Code);
        Assert.Contains("不是当前基础单位", error.Message);

        await AssertNoDocumentOrNumberChangeAsync(db, before);
        Assert.Equal(0, await db.StockAdjustmentDetails.CountAsync(d => d.ProductId == productId));
    }

    [Fact]
    public async Task Create_rejects_nonempty_unit_when_base_unit_is_missing()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var warehouseId = await SeedWarehouseAsync(db, "盘点仓");
        var productId = await SeedProductAsync(db, "缺基础单位商品", unit: "", packageUnit: "CTN", factor: 12);
        var controller = CreateController(db, await SeedUserAsync(db));
        var before = await SnapshotAsync(db);

        var error = await Assert.ThrowsAsync<BusinessException>(() => controller.Create(
            Draft(warehouseId, (productId, "缺基础单位商品", "PCS", 10m, 7m, 5m))));

        Assert.Equal(ErrorCodes.RuleConflict, error.Code);
        Assert.Contains("未设置", error.Message);
        await AssertNoDocumentOrNumberChangeAsync(db, before);
    }

    [Theory]
    [InlineData("PCS")]
    [InlineData(" pcs ")]
    public async Task Create_accepts_base_unit_case_and_whitespace_without_conversion(string unit)
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var warehouseId = await SeedWarehouseAsync(db, "盘点仓");
        var productId = await SeedProductAsync(db, "箱装商品", unit: "PCS", packageUnit: "CTN", factor: 12);
        var controller = CreateController(db, await SeedUserAsync(db));
        var before = await SnapshotAsync(db);

        var id = CreatedId(await controller.Create(
            Draft(warehouseId, (productId, "箱装商品", unit, 10m, 7m, 2.5m))));

        var doc = await db.StockAdjustments.AsNoTracking().SingleAsync(a => a.Id == id);
        Assert.Equal(DocumentStatus.Pending, doc.Status);
        Assert.Equal(-3m, doc.TotalDiffQuantity);
        Assert.Equal(-7.5m, doc.TotalDiffAmount);

        var detail = await db.StockAdjustmentDetails.AsNoTracking().SingleAsync(d => d.StockAdjustmentId == id);
        Assert.Equal(unit, detail.Unit);                 // 不折算、不臆造单位
        Assert.Equal(10m, detail.BookQuantity);
        Assert.Equal(7m, detail.ActualQuantity);
        Assert.Equal(2.5m, detail.UnitCost);

        Assert.Equal((before.RuleSequence ?? 0L) + 1L, await CurrentAdjustmentSequenceAsync(db)); // 受理恰好消耗一个单号
        Assert.Equal(before.Movements, await db.StockMovements.CountAsync());                     // 创建不产生任何库存流水
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Create_accepts_blank_unit_fallback(string unit)
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var warehouseId = await SeedWarehouseAsync(db, "盘点仓");
        var productId = await SeedProductAsync(db, "散装商品", unit: "KG", packageUnit: "CTN", factor: 12);
        var controller = CreateController(db, await SeedUserAsync(db));

        var id = CreatedId(await controller.Create(
            Draft(warehouseId, (productId, "散装商品", unit, 7m, 9m, 3m))));

        var detail = await db.StockAdjustmentDetails.AsNoTracking().SingleAsync(d => d.StockAdjustmentId == id);
        Assert.True(string.IsNullOrWhiteSpace(detail.Unit));   // 空单位保持既有回退
        Assert.Equal(7m, detail.BookQuantity);
        Assert.Equal(9m, detail.ActualQuantity);
        Assert.Equal(2m, detail.DiffQuantity);
        Assert.Equal(6m, detail.DiffAmount);
    }

    // ==================== 2. 修改：越界单位在明细整体替换之前收敛 ====================

    [Fact]
    public async Task Update_rejects_non_base_unit_and_keeps_existing_rows()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var warehouseId = await SeedWarehouseAsync(db, "盘点仓");
        var productId = await SeedProductAsync(db, "箱装商品", unit: "PCS", packageUnit: "CTN", factor: 12);
        var controller = CreateController(db, await SeedUserAsync(db));

        var id = CreatedId(await controller.Create(
            Draft(warehouseId, (productId, "箱装商品", "PCS", 10m, 7m, 2m))));
        var adjustmentNo = await db.StockAdjustments.Where(a => a.Id == id).Select(a => a.AdjustmentNo)
            .SingleAsync();
        var before = await SnapshotAsync(db);

        var error = await Assert.ThrowsAsync<BusinessException>(() => controller.Update(id,
            Draft(warehouseId, (productId, "箱装商品", "CTN", 9m, 4m, 2m))));

        Assert.Equal(ErrorCodes.RuleConflict, error.Code);
        Assert.Contains("不是当前基础单位", error.Message);

        var after = await db.StockAdjustments.AsNoTracking().SingleAsync(a => a.Id == id);
        Assert.Equal(adjustmentNo, after.AdjustmentNo);
        Assert.Equal(DocumentStatus.Pending, after.Status);
        Assert.Equal(-3m, after.TotalDiffQuantity);
        Assert.Equal(-6m, after.TotalDiffAmount);

        var detail = await db.StockAdjustmentDetails.AsNoTracking().SingleAsync(d => d.StockAdjustmentId == id);
        Assert.Equal("PCS", detail.Unit);                      // 明细未被整体替换
        Assert.Equal(10m, detail.BookQuantity);
        Assert.Equal(7m, detail.ActualQuantity);
        Assert.Equal(2m, detail.UnitCost);

        Assert.Equal(before.NumberRules, await db.SysDocumentNumberRules.CountAsync());
        Assert.Equal(before.RuleSequence, await CurrentAdjustmentSequenceAsync(db));
        Assert.Equal(before.Movements, await db.StockMovements.CountAsync());
    }

    // ==================== 3. 提交 / 审核：锁内复核已存单位 ====================

    [Fact]
    public async Task Submit_recheck_rejects_after_live_base_unit_edit()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var warehouseId = await SeedWarehouseAsync(db, "盘点仓");
        var productId = await SeedProductAsync(db, "箱装商品", unit: "PCS", packageUnit: "CTN", factor: 12);
        var controller = CreateController(db, await SeedUserAsync(db));
        var id = CreatedId(await controller.Create(
            Draft(warehouseId, (productId, "箱装商品", "PCS", 6m, 4m, 2m))));

        await SetProductUnitsAsync(db, productId, unit: "BOX", packageUnit: "CTN", factor: 12);
        var before = await SnapshotAsync(db);

        var error = await Assert.ThrowsAsync<BusinessException>(() => controller.Submit(id));

        Assert.Equal(ErrorCodes.RuleConflict, error.Code);
        Assert.Contains("不是当前基础单位", error.Message);
        Assert.Equal(DocumentStatus.Pending,
            await db.StockAdjustments.Where(a => a.Id == id).Select(a => a.Status).SingleAsync());
        Assert.Equal("PCS",
            await db.StockAdjustmentDetails.Where(d => d.StockAdjustmentId == id).Select(d => d.Unit).SingleAsync());
        await AssertNoDocumentOrNumberChangeAsync(db, before);
    }

    [Fact]
    public async Task Approve_recheck_rejects_after_live_base_unit_edit_and_changes_nothing()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var warehouseId = await SeedWarehouseAsync(db, "盘点仓");
        var productId = await SeedProductAsync(db, "箱装商品", unit: "PCS", packageUnit: "CTN", factor: 12);
        var controller = CreateController(db, await SeedUserAsync(db));
        await SeedStockAsync(db, warehouseId, productId, quantity: 100m, totalCost: 1000m);

        var id = CreatedId(await controller.Create(
            Draft(warehouseId, (productId, "箱装商品", "PCS", 100m, 70m, 10m))));
        await controller.Submit(id);
        await SetProductUnitsAsync(db, productId, unit: "BOX", packageUnit: "CTN", factor: 12);
        var before = await SnapshotAsync(db);

        var error = await Assert.ThrowsAsync<BusinessException>(() => controller.Approve(id));

        Assert.Equal(ErrorCodes.RuleConflict, error.Code);
        Assert.Equal(DocumentStatus.Submitted,
            await db.StockAdjustments.Where(a => a.Id == id).Select(a => a.Status).SingleAsync());

        var stock = await db.Stocks.AsNoTracking()
            .SingleAsync(s => s.WarehouseId == warehouseId && s.ProductId == productId);
        Assert.Equal(100m, stock.Quantity);
        Assert.Equal(1000m, stock.TotalCost);
        await AssertNoDocumentOrNumberChangeAsync(db, before);

        var detail = await db.StockAdjustmentDetails.AsNoTracking().SingleAsync(d => d.StockAdjustmentId == id);
        Assert.Equal("PCS", detail.Unit);                     // 未被 product.Unit 静默覆盖
        Assert.Equal(100m, detail.BookQuantity);
        Assert.Equal(70m, detail.ActualQuantity);
        Assert.Equal(10m, detail.UnitCost);
    }

    [Fact]
    public async Task Legacy_submitted_draft_with_foreign_unit_is_rejected_and_stays_readable()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var warehouseId = await SeedWarehouseAsync(db, "盘点仓");
        var productId = await SeedProductAsync(db, "箱装商品", unit: "PCS", packageUnit: "CTN", factor: 12);
        var controller = CreateController(db, await SeedUserAsync(db));
        await SeedStockAsync(db, warehouseId, productId, quantity: 100m, totalCost: 1000m);
        var id = await SeedSubmittedAdjustmentAsync(db, warehouseId, productId, unit: "CTN", bookQuantity: 100m,
            actualQuantity: 88m, unitCost: 5m);
        var before = await SnapshotAsync(db);

        var error = await Assert.ThrowsAsync<BusinessException>(() => controller.Approve(id));

        Assert.Equal(ErrorCodes.RuleConflict, error.Code);
        Assert.Contains("不是当前基础单位", error.Message);
        Assert.Equal(DocumentStatus.Submitted,
            await db.StockAdjustments.Where(a => a.Id == id).Select(a => a.Status).SingleAsync());
        Assert.Equal(100m, await db.Stocks.Where(s => s.WarehouseId == warehouseId && s.ProductId == productId)
            .Select(s => s.Quantity).SingleAsync());
        await AssertNoDocumentOrNumberChangeAsync(db, before);

        // 历史读取不新增资格闸门：已存单位原样可读。
        var read = await controller.GetById(id);
        var payload = Assert.IsType<ApiResponse<StockAdjustment>>(
            Assert.IsType<OkObjectResult>(read).Value).Data!;
        Assert.Equal("CTN", payload.Details.Single().Unit);
    }

    // ==================== 4. 合法审核恰好一次 + 销审回归 ====================

    [Theory]
    [InlineData("PCS")]
    [InlineData(" pcs ")]
    [InlineData("")]
    public async Task Approve_posts_difference_once_and_preserves_base_meaning(string unit)
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var warehouseId = await SeedWarehouseAsync(db, "盘点仓");
        var productId = await SeedProductAsync(db, "箱装商品", unit: "PCS", packageUnit: "CTN", factor: 12);
        var controller = CreateController(db, await SeedUserAsync(db));
        await SeedStockAsync(db, warehouseId, productId, quantity: 100m, totalCost: 1000m);

        var id = CreatedId(await controller.Create(
            Draft(warehouseId, (productId, "箱装商品", unit, 100m, 70m, 10m))));
        await controller.Submit(id);

        await controller.Approve(id);

        var stock = await db.Stocks.AsNoTracking()
            .SingleAsync(s => s.WarehouseId == warehouseId && s.ProductId == productId);
        Assert.Equal(70m, stock.Quantity);
        Assert.Equal(700m, stock.TotalCost);
        Assert.Equal(DocumentStatus.Approved,
            await db.StockAdjustments.Where(a => a.Id == id).Select(a => a.Status).SingleAsync());
        Assert.Equal(1, await db.StockMovements.CountAsync(m => m.SourceDocId == id && !m.IsDeleted));

        var detail = await db.StockAdjustmentDetails.AsNoTracking().SingleAsync(d => d.StockAdjustmentId == id);
        Assert.True(StockAdjustmentPostingRules.IsBaseUnitEligible(detail.Unit, "PCS"));
        if (string.IsNullOrWhiteSpace(unit))
            Assert.Equal("PCS", detail.Unit);                     // 空单位按商品基础单位兜底
        else
            Assert.Equal(unit, detail.Unit);                      // 已校验的基础单位保持原样
        Assert.Equal(100m, detail.BookQuantity);                  // 账面 / 实盘数量与成本绝不被改写
        Assert.Equal(70m, detail.ActualQuantity);
        Assert.Equal(10m, detail.UnitCost);
        Assert.Equal(-30m, detail.DiffQuantity);

        var again = await Assert.ThrowsAsync<BusinessException>(() => controller.Approve(id));
        Assert.Equal(ErrorCodes.RuleConflict, again.Code);
        Assert.Equal(1, await db.StockMovements.CountAsync(m => m.SourceDocId == id && !m.IsDeleted));
    }

    [Theory]
    [InlineData(100, 60, 600)]   // 盘亏：100 → 60
    [InlineData(0, 5, 50)]       // 盘盈：0 → 5
    [InlineData(0, 0, 0)]        // 零差异：不记账
    public async Task Approve_keeps_signed_difference_and_zero_difference_behavior(decimal current,
        decimal actual, decimal expectedCost)
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var warehouseId = await SeedWarehouseAsync(db, "盘点仓");
        var productId = await SeedProductAsync(db, "箱装商品", unit: "PCS", packageUnit: "CTN", factor: 12);
        var controller = CreateController(db, await SeedUserAsync(db));
        await SeedStockAsync(db, warehouseId, productId, quantity: current, totalCost: current * 10m);

        var id = CreatedId(await controller.Create(
            Draft(warehouseId, (productId, "箱装商品", "PCS", current, actual, 10m))));
        await controller.Submit(id);

        await controller.Approve(id);

        var stock = await db.Stocks.AsNoTracking()
            .SingleAsync(s => s.WarehouseId == warehouseId && s.ProductId == productId);
        Assert.Equal(actual, stock.Quantity);
        Assert.Equal(expectedCost, stock.TotalCost);

        var detail = await db.StockAdjustmentDetails.AsNoTracking().SingleAsync(d => d.StockAdjustmentId == id);
        Assert.Equal(actual - current, detail.DiffQuantity);
        Assert.Equal(actual == current ? 0 : 1,
            await db.StockMovements.CountAsync(m => m.SourceDocId == id && !m.IsDeleted));
    }

    [Fact]
    public async Task Unaudit_after_base_unit_edit_still_reverses()
    {
        Guard();
        await using var db = _fixture.CreateDbContext();
        var warehouseId = await SeedWarehouseAsync(db, "盘点仓");
        var productId = await SeedProductAsync(db, "箱装商品", unit: "PCS", packageUnit: "CTN", factor: 12);
        var controller = CreateController(db, await SeedUserAsync(db));
        await SeedStockAsync(db, warehouseId, productId, quantity: 100m, totalCost: 1000m);

        var id = CreatedId(await controller.Create(
            Draft(warehouseId, (productId, "箱装商品", "PCS", 100m, 60m, 10m))));
        await controller.Submit(id);
        await controller.Approve(id);
        await SetProductUnitsAsync(db, productId, unit: "BOX", packageUnit: "CTN", factor: 12);

        await controller.Unaudit(id);

        var stock = await db.Stocks.AsNoTracking()
            .SingleAsync(s => s.WarehouseId == warehouseId && s.ProductId == productId);
        Assert.Equal(100m, stock.Quantity);
        Assert.Equal(1000m, stock.TotalCost);
        Assert.Equal(DocumentStatus.Pending,
            await db.StockAdjustments.Where(a => a.Id == id).Select(a => a.Status).SingleAsync());
        Assert.Equal(1, await db.StockMovements.CountAsync(m => m.SourceDocId == id && m.IsReversal && !m.IsDeleted));
        Assert.Equal(1, await db.StockMovements.CountAsync(m => m.SourceDocId == id && !m.IsReversal && !m.IsDeleted));
    }

    // ==================== 测试辅助 ====================

    private static StockAdjustmentController CreateController(ErpDbContext db, long? userId)
        => new(db, new DocumentNumberService(db), new InventoryService(db))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(userId.HasValue
                        ? new[] { new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()) }
                        : Array.Empty<Claim>(), "Test"))
                }
            }
        };

    private static StockAdjustment Draft(long warehouseId,
        params (long ProductId, string Name, string Unit, decimal Book, decimal Actual, decimal UnitCost)[] lines)
        => new()
        {
            AdjustmentDate = DateTime.Today,
            WarehouseId = warehouseId,
            AdjustType = "盘点调整",
            Remark = "ERP-526_SQL_TEST",
            Details = lines.Select(l => new StockAdjustmentDetail
            {
                ProductId = l.ProductId,
                ProductName = l.Name,
                Spec = "规格A",
                Unit = l.Unit,
                BookQuantity = l.Book,
                ActualQuantity = l.Actual,
                UnitCost = l.UnitCost
            }).ToList()
        };

    private static long CreatedId(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var data = Assert.IsType<ApiResponse<object>>(ok.Value).Data!;
        return (long)data.GetType().GetProperty("Id")!.GetValue(data)!;
    }

    /// <summary>调用前基线快照（同一夹具库被多个用例复用，因此拒绝断言一律相对快照比较）。</summary>
    private static async Task<DraftSnapshot> SnapshotAsync(ErpDbContext db)
        => new(
            await db.StockAdjustments.CountAsync(),
            await db.StockAdjustmentDetails.CountAsync(),
            await db.StockMovements.CountAsync(),
            await db.SysDocumentNumberRules.CountAsync(),
            await CurrentAdjustmentSequenceAsync(db));

    /// <summary>拒绝场景的统一断言：单据 / 明细 / 流水 / 单号规则与盘点流水号相对快照逐项不变。</summary>
    private static async Task AssertNoDocumentOrNumberChangeAsync(ErpDbContext db, DraftSnapshot before)
    {
        Assert.Equal(before.Adjustments, await db.StockAdjustments.CountAsync());
        Assert.Equal(before.Details, await db.StockAdjustmentDetails.CountAsync());
        Assert.Equal(before.Movements, await db.StockMovements.CountAsync());
        Assert.Equal(before.NumberRules, await db.SysDocumentNumberRules.CountAsync());
        Assert.Equal(before.RuleSequence, await CurrentAdjustmentSequenceAsync(db));   // 拒绝方绝不消耗单据号
    }

    private sealed record DraftSnapshot(int Adjustments, int Details, int Movements, int NumberRules,
        long? RuleSequence);

    private static async Task<long?> CurrentAdjustmentSequenceAsync(ErpDbContext db)
        => await db.SysDocumentNumberRules.AsNoTracking()
            .Where(r => r.DocumentType == DocumentType.StockAdjustment)
            .Select(r => (long?)r.CurrentSequence)
            .FirstOrDefaultAsync();

    // ==================== 真实 SQL 夹具种子（一律使用 SQL Server 自增主键，不显式指定 Id） ====================

    private static async Task<long> SeedWarehouseAsync(ErpDbContext db, string name)
    {
        var warehouse = new BaseWarehouse { WarehouseCode = $"SADW-{Tag()}", WarehouseName = name, Status = 1 };
        db.BaseWarehouses.Add(warehouse);
        await db.SaveChangesAsync();
        return warehouse.Id;
    }

    private static async Task<long> SeedProductAsync(ErpDbContext db, string name, string unit,
        string packageUnit, int factor)
    {
        var product = new BaseProduct
        {
            ProductCode = $"SADP-{Tag()}",
            ProductName = name,
            Spec = "规格A",
            Unit = unit,
            PackageUnit = packageUnit,
            UnitsPerPackage = factor,
            Status = 1
        };
        db.BaseProducts.Add(product);
        await db.SaveChangesAsync();
        return product.Id;
    }

    private static async Task SetProductUnitsAsync(ErpDbContext db, long productId, string unit,
        string packageUnit, int factor)
    {
        var product = await db.BaseProducts.SingleAsync(p => p.Id == productId);
        product.Unit = unit;
        product.PackageUnit = packageUnit;
        product.UnitsPerPackage = factor;
        await db.SaveChangesAsync();
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

    /// <summary>直接落库一张「已提交」的历史盘点单（绕过控制器），用于验证审核期复核与历史读取。</summary>
    private static async Task<long> SeedSubmittedAdjustmentAsync(ErpDbContext db, long warehouseId,
        long productId, string unit, decimal bookQuantity, decimal actualQuantity, decimal unitCost)
    {
        var diff = actualQuantity - bookQuantity;
        var adjustment = new StockAdjustment
        {
            AdjustmentNo = $"PD-U-{Tag()}",
            AdjustmentDate = DateTime.Today,
            WarehouseId = warehouseId,
            AdjustType = "盘点调整",
            Status = DocumentStatus.Submitted,
            TotalDiffQuantity = diff,
            TotalDiffAmount = diff * unitCost
        };
        db.StockAdjustments.Add(adjustment);
        await db.SaveChangesAsync();

        db.StockAdjustmentDetails.Add(new StockAdjustmentDetail
        {
            StockAdjustmentId = adjustment.Id,
            AdjustmentNo = adjustment.AdjustmentNo,
            SortNo = 1,
            ProductId = productId,
            ProductName = "历史商品",
            Spec = "规格A",
            Unit = unit,
            BookQuantity = bookQuantity,
            ActualQuantity = actualQuantity,
            DiffQuantity = diff,
            UnitCost = unitCost,
            DiffAmount = diff * unitCost
        });
        await db.SaveChangesAsync();
        return adjustment.Id;
    }

    /// <summary>播种真实授权账号：既有 stock-query 菜单（种子数据已含）+ 角色授权 + 启用账号。</summary>
    private static async Task<long> SeedUserAsync(ErpDbContext db)
    {
        var role = new SysRole { RoleCode = $"SAD-UNIT-{Tag()}", RoleName = "盘点单位集成操作员" };
        db.SysRoles.Add(role);
        await db.SaveChangesAsync();

        var user = new SysUser
        {
            UserName = $"sad-unit-{Tag()}",
            PasswordHash = "hash",
            PasswordSalt = "salt",
            DisplayName = "盘点单位集成操作员",
            Status = UserStatus.Enabled
        };
        db.SysUsers.Add(user);
        await db.SaveChangesAsync();

        db.SysUserRoles.Add(new SysUserRole { UserId = user.Id, RoleId = role.Id });
        await db.SaveChangesAsync();

        var menu = await db.SysMenus.FirstAsync(m =>
            m.MenuCode == StockAdjustmentPostingRules.RequiredMenuCode && !m.IsDeleted);
        db.SysRoleMenus.Add(new SysRoleMenu { RoleId = role.Id, MenuId = menu.Id });
        await db.SaveChangesAsync();
        return user.Id;
    }
}

/// <summary>
/// 专用 localdb 目标 Fixture（ERP-526）：把目标库重置为完整 NEWERP 结构 + 种子数据，供盘点单位完整性集成测试复用。
/// <para>每次运行只创建一个全新 GUID 后缀库；发现同名库已存在立即拒绝；绝不 drop / reset / 复用任何数据库。</para>
/// </summary>
public sealed class StockAdjustmentUnitIntegritySqlServerFixture : IAsyncLifetime
{
    public const string InstanceMarker = "NEWERP_AutoAcceptance";
    public const string DatabasePrefix = "NEWERP_AUTOTEST";
    public const string DefaultDatabaseName = "NEWERP_AUTOTEST_STOCKADJUSTMENTUNIT_20261014";

    public string ConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("ERP_ConnectionStrings__Default");
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = BuildDefaultConnectionString();

        AssertDedicatedTarget(connectionString);
        ConnectionString = connectionString;

        Console.WriteLine($"[ERP-526] 目标库护栏放行（实例含 {InstanceMarker}，库名前缀 {DatabasePrefix}）。");

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

        // 破坏性重置前再次护栏：绝不使用生产回退。
        AssertDedicatedTarget(ConnectionString);

        var master = new SqlConnectionStringBuilder(ConnectionString) { InitialCatalog = "master" };
        await using (var conn = new SqlConnection(master.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            // Never destroy a pre-existing fixture or another caller's database.
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

        Console.WriteLine("[ERP-526] 集成场景就绪：完整 NEWERP 结构 + 种子数据。");
    }
}

/// <summary>目标库护栏单元级校验：非专用目标必须在访问数据库之前被拒绝（ERP-526）。</summary>
public sealed class StockAdjustmentUnitIntegrityTargetGuardTests
{
    [Theory]
    [InlineData("Server=production_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\\\OTHER_NEWERP_AutoAcceptance;Initial Catalog=NEWERP_AUTOTEST_BAD;Integrated Security=true")]
    [InlineData("Server=(localdb)\\\\NEWERP_AutoAcceptance;Initial Catalog=Production;Integrated Security=true")]
    public void Rejects_non_dedicated_targets_before_database_access(string connection)
        => Assert.ThrowsAny<Exception>(() =>
            StockAdjustmentUnitIntegritySqlServerFixture.AssertDedicatedTarget(connection));
}
