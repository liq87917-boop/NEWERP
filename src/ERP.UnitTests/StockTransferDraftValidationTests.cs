using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-501 仓库调拨草稿写入校验单元测试（内存库 + 真实 HTTP 身份）：
/// 1) 创建与修改在单号预约 / 明细整体替换之前拒绝「仓库 Id 非正 / 调出调入相同 / 不存在 / 已删除 / 已停用」；
/// 2) 拒绝「数量非正或不可表示、成本为负或不可表示、行金额与合计不可表示 / 溢出」（与 EF 实际精度同源）；
/// 3) 拒绝「商品 Id 缺失 / 不存在 / 已删除 / 已停用」；
/// 4) 保留空草稿头（0 行有效明细）与重复商品行（累计数量语义），提交与审核要求非空有效明细；
/// 5) 每个拒绝场景都断言零单据、零明细、零库存流水、零单号消耗，且既有单据与明细逐字段不变。
/// 说明：全部使用内存数据库，不连接 SQL Server、不触碰任何业务库数据。
/// </summary>
public class StockTransferDraftValidationTests
{
    private const long WarehouseA = 930001L;
    private const long WarehouseB = 930002L;
    private const long Product1 = 830001L;
    private const long Product2 = 830002L;

    // ==================== 1. 创建：仓库非法（零写入 / 零单号） ====================

    [Theory]
    [InlineData("from-zero", ErrorCodes.InvalidParameter)]
    [InlineData("to-zero", ErrorCodes.InvalidParameter)]
    [InlineData("from-negative", ErrorCodes.InvalidParameter)]
    [InlineData("to-negative", ErrorCodes.InvalidParameter)]
    [InlineData("same", ErrorCodes.RuleConflict)]
    [InlineData("missing-from", ErrorCodes.InvalidParameter)]
    [InlineData("missing-to", ErrorCodes.InvalidParameter)]
    [InlineData("deleted-from", ErrorCodes.InvalidParameter)]
    [InlineData("deleted-to", ErrorCodes.InvalidParameter)]
    [InlineData("disabled-from", ErrorCodes.RuleConflict)]
    [InlineData("disabled-to", ErrorCodes.RuleConflict)]
    public async Task 创建_仓库非法_拒绝且零单据零明细零流水零单号(string scenario, int expectedCode)
    {
        using var db = TestDbFactory.Create();
        SeedMasterData(db);
        if (scenario == "deleted-from") SoftDeleteWarehouse(db, WarehouseA);
        if (scenario == "deleted-to") SoftDeleteWarehouse(db, WarehouseB);
        if (scenario == "disabled-from") DisableWarehouse(db, WarehouseA);
        if (scenario == "disabled-to") DisableWarehouse(db, WarehouseB);

        var (from, to) = scenario switch
        {
            "from-zero" => (0L, WarehouseB),
            "to-zero" => (WarehouseA, 0L),
            "from-negative" => (-1L, WarehouseB),
            "to-negative" => (WarehouseA, -5L),
            "same" => (WarehouseA, WarehouseA),
            "missing-from" => (999_999L, WarehouseB),
            "missing-to" => (WarehouseA, 999_999L),
            _ => (WarehouseA, WarehouseB)
        };

        var ctl = Authorized(db);
        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(Draft(from, to, (Product1, "P1", 1m, 1m))));

        Assert.Equal(expectedCode, ex.Code);
        AssertRejectedWithNoSideEffects(db);
    }

    // ==================== 2. 创建：数量 / 成本 / 金额非法 ====================

    [Theory]
    [InlineData(0, 1)]                       // 数量为 0
    [InlineData(-1, 1)]                      // 数量为负
    [InlineData(1.00001, 1)]                 // 数量 5 位小数：DECIMAL(18,4) 不可表示
    [InlineData(100000000000000, 1)]         // 数量超出 DECIMAL(18,4) 上界
    [InlineData(1, -0.5)]                    // 成本为负
    [InlineData(1, -100000000000000)]        // 成本为负且超出上界
    [InlineData(1, 0.0000001)]               // 成本 7 位小数：DECIMAL(18,6) 不可表示
    [InlineData(1, 1000000000000)]           // 成本超出 DECIMAL(18,6) 上界
    public async Task 创建_数量或成本不可表示_拒绝且零写入零单号(decimal quantity, decimal unitCost)
    {
        using var db = TestDbFactory.Create();
        SeedMasterData(db);
        var ctl = Authorized(db);

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(Draft(WarehouseA, WarehouseB, (Product1, "P1", quantity, unitCost))));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        AssertRejectedWithNoSideEffects(db);
    }

    [Fact]
    public async Task 创建_行金额超出存储精度_拒绝()
    {
        // 数量与成本各自都在精度内，但「数量 × 成本」超出 DECIMAL(18,4)：
        // 必须拒绝，绝不静默取整出与明细合计不一致的金额。
        using var db = TestDbFactory.Create();
        SeedMasterData(db);
        var ctl = Authorized(db);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Create(
            Draft(WarehouseA, WarehouseB,
                (Product1, "P1", StockTransferPostingRules.MaxDecimal18Scale4,
                    StockTransferPostingRules.MaxDecimal18Scale6))));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Contains("行金额", ex.Message);
        AssertRejectedWithNoSideEffects(db);
    }

    [Fact]
    public async Task 创建_合计超出存储精度_拒绝()
    {
        // 单行数量 / 金额都在精度内，两行累计后超出 DECIMAL(18,4)
        using var db = TestDbFactory.Create();
        SeedMasterData(db);
        var ctl = Authorized(db);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Create(
            Draft(WarehouseA, WarehouseB,
                (Product1, "P1", 90_000_000_000_000m, 1m),
                (Product2, "P2", 90_000_000_000_000m, 1m))));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        AssertRejectedWithNoSideEffects(db);
    }

    // ==================== 3. 创建：商品非法 ====================

    [Theory]
    [InlineData("missing-id")]
    [InlineData("zero-id")]
    [InlineData("not-exists")]
    [InlineData("deleted")]
    [InlineData("disabled")]
    public async Task 创建_商品非法_拒绝且零写入零单号(string scenario)
    {
        using var db = TestDbFactory.Create();
        SeedMasterData(db);
        if (scenario == "deleted") SoftDeleteProduct(db, Product1);
        if (scenario == "disabled") DisableProduct(db, Product1);

        var productId = scenario switch
        {
            "missing-id" => (long?)null,
            "zero-id" => 0L,
            "not-exists" => 999_999L,
            _ => Product1
        };

        var ctl = Authorized(db);
        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(Draft(WarehouseA, WarehouseB, (productId, "P1", 1m, 1m))));

        Assert.Equal(scenario == "disabled" ? ErrorCodes.RuleConflict : ErrorCodes.InvalidParameter, ex.Code);
        AssertRejectedWithNoSideEffects(db);
    }

    // ==================== 4. 空草稿头与重复商品行（既有支持语义保留） ====================

    [Fact]
    public async Task 空草稿头_创建允许_提交与审核要求非空有效明细()
    {
        using var db = TestDbFactory.Create();
        SeedMasterData(db);
        var ctl = Authorized(db);

        var id = CreatedId(await ctl.Create(Draft(WarehouseA, WarehouseB)));      // 0 行明细的合法草稿头
        var doc = db.StockTransfers.Single(t => t.Id == id);
        Assert.StartsWith("DB", doc.TransferNo);
        Assert.Equal(DocumentStatus.Pending, doc.Status);
        Assert.Equal(0m, doc.TotalQuantity);
        Assert.Equal(0m, doc.TotalAmount);
        Assert.Empty(db.StockTransferDetails);

        // 提交：要求至少一行有效明细（空草稿头可保存，但不得提交）
        var submitEx = await Assert.ThrowsAsync<BusinessException>(() => ctl.Submit(id));
        Assert.Equal(ErrorCodes.RuleConflict, submitEx.Code);
        Assert.Equal(DocumentStatus.Pending, db.StockTransfers.Single(t => t.Id == id).Status);

        // 审核：无明细同样拒绝（保持既有审核语义）
        db.StockTransfers.Single(t => t.Id == id).Status = DocumentStatus.Submitted;
        db.SaveChanges();
        var approveEx = await Assert.ThrowsAsync<BusinessException>(() => ctl.Approve(id));
        Assert.Equal(ErrorCodes.RuleConflict, approveEx.Code);
        Assert.Equal(DocumentStatus.Submitted, db.StockTransfers.Single(t => t.Id == id).Status);
        Assert.Empty(db.StockMovements);
    }

    [Fact]
    public async Task 重复商品行_允许_累计数量语义与过账守恒()
    {
        using var db = TestDbFactory.Create();
        SeedMasterData(db);
        SeedStock(db, WarehouseA, Product1, quantity: 100m, totalCost: 1000m);     // 均价 10
        var ctl = Authorized(db);

        var id = CreatedId(await ctl.Create(Draft(WarehouseA, WarehouseB,
            (Product1, "P1", 3m, 2m),
            (Product1, "P1", 2m, 2m))));

        var doc = db.StockTransfers.Single(t => t.Id == id);
        Assert.Equal(5m, doc.TotalQuantity);                                       // 累计口径：3 + 2
        Assert.Equal(10m, doc.TotalAmount);

        var details = db.StockTransferDetails.Where(d => d.StockTransferId == id)
            .OrderBy(d => d.SortNo).ToList();
        Assert.Equal(2, details.Count);
        Assert.Equal(new[] { 1, 2 }, details.Select(d => d.SortNo).ToArray());
        Assert.All(details, d => Assert.Equal(Product1, d.ProductId));
        Assert.Equal(new[] { 6m, 4m }, details.Select(d => d.Amount).ToArray());

        await ctl.Submit(id);
        await ctl.Approve(id);

        var from = db.Stocks.Single(s => s.WarehouseId == WarehouseA && s.ProductId == Product1);
        var to = db.Stocks.Single(s => s.WarehouseId == WarehouseB && s.ProductId == Product1);
        Assert.Equal(95m, from.Quantity);
        Assert.Equal(5m, to.Quantity);
        Assert.Equal(100m, from.Quantity + to.Quantity);                           // 数量守恒
        Assert.Equal(4, db.StockMovements.Count());                                // 每行「调出 + 调入」各一笔
        Assert.Equal(DocumentStatus.Approved, db.StockTransfers.Single(t => t.Id == id).Status);
    }

    // ==================== 5. 修改：同一套护栏（先于明细整体替换） ====================

    [Theory]
    [InlineData("from-zero", ErrorCodes.InvalidParameter)]
    [InlineData("to-zero", ErrorCodes.InvalidParameter)]
    [InlineData("same", ErrorCodes.RuleConflict)]
    [InlineData("missing-from", ErrorCodes.InvalidParameter)]
    [InlineData("missing-to", ErrorCodes.InvalidParameter)]
    [InlineData("deleted-to", ErrorCodes.InvalidParameter)]
    [InlineData("disabled-from", ErrorCodes.RuleConflict)]
    [InlineData("zero-quantity", ErrorCodes.InvalidParameter)]
    [InlineData("unrepresentable-quantity", ErrorCodes.InvalidParameter)]
    [InlineData("negative-cost", ErrorCodes.InvalidParameter)]
    [InlineData("unrepresentable-cost", ErrorCodes.InvalidParameter)]
    [InlineData("missing-product", ErrorCodes.InvalidParameter)]
    [InlineData("deleted-product", ErrorCodes.InvalidParameter)]
    [InlineData("disabled-product", ErrorCodes.RuleConflict)]
    public async Task 修改_非法草稿_拒绝且既有单据明细与流水不变(string scenario, int expectedCode)
    {
        using var db = TestDbFactory.Create();
        SeedMasterData(db);
        var ctl = Authorized(db);
        var id = CreatedId(await ctl.Create(Draft(WarehouseA, WarehouseB, (Product1, "P1", 2m, 3m))));
        var numberRows = db.SysDocumentNumberRules.Count();
        var transferNo = db.StockTransfers.Single(t => t.Id == id).TransferNo;

        // 场景性主数据变更发生在合法草稿创建之后：修改路径必须在同一套护栏下收敛
        if (scenario == "deleted-to") SoftDeleteWarehouse(db, WarehouseB);
        if (scenario == "disabled-from") DisableWarehouse(db, WarehouseA);
        if (scenario == "deleted-product") SoftDeleteProduct(db, Product1);
        if (scenario == "disabled-product") DisableProduct(db, Product1);

        var (from, to) = scenario switch
        {
            "from-zero" => (0L, WarehouseB),
            "to-zero" => (WarehouseA, 0L),
            "same" => (WarehouseA, WarehouseA),
            "missing-from" => (999_999L, WarehouseB),
            "missing-to" => (WarehouseA, 999_999L),
            _ => (WarehouseA, WarehouseB)
        };
        var (productId, quantity, unitCost) = scenario switch
        {
            "zero-quantity" => ((long?)Product1, 0m, 1m),
            "unrepresentable-quantity" => ((long?)Product1, 1.00001m, 1m),
            "negative-cost" => ((long?)Product1, 1m, -1m),
            "unrepresentable-cost" => ((long?)Product1, 1m, 0.0000001m),
            "missing-product" => ((long?)999_999L, 1m, 1m),
            _ => ((long?)Product1, 1m, 1m)
        };

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Update(id,
            Draft(from, to, (productId, "P1", quantity, unitCost))));

        Assert.Equal(expectedCode, ex.Code);

        // 既有单据 / 明细逐字段不变，且未消耗新单号、未产生流水
        var after = db.StockTransfers.Single(t => t.Id == id);
        Assert.Equal(transferNo, after.TransferNo);
        Assert.Equal(DocumentStatus.Pending, after.Status);
        Assert.Equal(WarehouseA, after.FromWarehouseId);
        Assert.Equal(WarehouseB, after.ToWarehouseId);
        Assert.Equal(2m, after.TotalQuantity);
        Assert.Equal(6m, after.TotalAmount);
        var detail = db.StockTransferDetails.Single(d => d.StockTransferId == id);
        Assert.Equal(Product1, detail.ProductId);
        Assert.Equal(2m, detail.Quantity);
        Assert.Equal(3m, detail.UnitCost);
        Assert.Equal(6m, detail.Amount);
        Assert.Equal(numberRows, db.SysDocumentNumberRules.Count());
        Assert.Empty(db.StockMovements);
    }

    [Fact]
    public async Task 修改_合法草稿_合计重算且单号不变()
    {
        using var db = TestDbFactory.Create();
        SeedMasterData(db);
        var ctl = Authorized(db);
        var id = CreatedId(await ctl.Create(Draft(WarehouseA, WarehouseB, (Product1, "P1", 1m, 1m))));
        var transferNo = db.StockTransfers.Single(t => t.Id == id).TransferNo;
        var numberRows = db.SysDocumentNumberRules.Count();

        var result = await ctl.Update(id, Draft(WarehouseA, WarehouseB,
            (Product2, "P2", 4m, 5m),
            (Product1, "P1", 3m, 2m)));
        Assert.IsType<OkObjectResult>(result);

        var doc = db.StockTransfers.Single(t => t.Id == id);
        Assert.Equal(transferNo, doc.TransferNo);                      // 修改不重发单号
        Assert.Equal(7m, doc.TotalQuantity);                           // 4 + 3
        Assert.Equal(26m, doc.TotalAmount);                            // 4×5 + 3×2
        var details = db.StockTransferDetails.Where(d => d.StockTransferId == id)
            .OrderBy(d => d.SortNo).ToList();
        Assert.Equal(2, details.Count);
        Assert.Equal(new[] { 1, 2 }, details.Select(d => d.SortNo).ToArray());
        Assert.Equal(new[] { 20m, 6m }, details.Select(d => d.Amount).ToArray());
        Assert.Equal(numberRows, db.SysDocumentNumberRules.Count());   // 未消耗新单号
        Assert.Empty(db.StockMovements);
    }

    // ==================== 6. 实际 EF 精度与源码契约 ====================

    [Fact]
    public void EF实际精度_调拨数量金额成本列_与护栏常量同源()
    {
        var options = new DbContextOptionsBuilder<ErpDbContext>()
            .UseSqlServer(
                "Server=(localdb)\\NEWERP_AutoAcceptance;Database=NEWERP_AUTOTEST_MODEL_ONLY;Integrated Security=true")
            .Options;
        using var db = new ErpDbContext(options);
        var model = db.Model;

        Assert.Equal("decimal(18,4)",
            ColumnType(model, typeof(StockTransferDetail), nameof(StockTransferDetail.Quantity)));
        Assert.Equal("decimal(18,6)",
            ColumnType(model, typeof(StockTransferDetail), nameof(StockTransferDetail.UnitCost)));
        Assert.Equal("decimal(18,4)",
            ColumnType(model, typeof(StockTransferDetail), nameof(StockTransferDetail.Amount)));
        Assert.Equal("decimal(18,4)",
            ColumnType(model, typeof(StockTransfer), nameof(StockTransfer.TotalQuantity)));
        Assert.Equal("decimal(18,4)",
            ColumnType(model, typeof(StockTransfer), nameof(StockTransfer.TotalAmount)));

        Assert.Equal(18, StockTransferPostingRules.QuantityPrecision);
        Assert.Equal(4, StockTransferPostingRules.QuantityScale);
        Assert.Equal(18, StockTransferPostingRules.CostPrecision);
        Assert.Equal(6, StockTransferPostingRules.CostScale);
        Assert.Equal(99_999_999_999_999.9999m, StockTransferPostingRules.MaxDecimal18Scale4);
        Assert.Equal(999_999_999_999.999999m, StockTransferPostingRules.MaxDecimal18Scale6);
        Assert.True(StockTransferPostingRules.IsQuantityRepresentable(99_999_999_999_999.9999m));
        Assert.False(StockTransferPostingRules.IsQuantityRepresentable(100_000_000_000_000m));
        Assert.True(StockTransferPostingRules.IsCostRepresentable(999_999_999_999.999999m));
        Assert.False(StockTransferPostingRules.IsCostRepresentable(1_000_000_000_000m));
    }

    [Fact]
    public void 草稿护栏源码契约_先校验再消耗单号与替换明细()
    {
        var controller = ReadSource("ERP.Api/Controllers/StockTransferController.cs");
        Assert.Contains("StockTransferPostingRules.EnsureLineShape", controller);
        Assert.Contains("StockTransferPostingRules.EnsureLiveMasterDataAsync", controller);

        var createStart = controller.IndexOf("public async Task<IActionResult> Create", StringComparison.Ordinal);
        var updateStart = controller.IndexOf("public async Task<IActionResult> Update", StringComparison.Ordinal);
        var approveStart = controller.IndexOf("public override async Task<IActionResult> Approve", StringComparison.Ordinal);
        Assert.True(createStart >= 0 && updateStart > createStart && approveStart > updateStart);

        var create = controller[createStart..updateStart];
        Assert.True(create.IndexOf("EnsureLineShape", StringComparison.Ordinal)
                    < create.IndexOf("_noService.GenerateAsync", StringComparison.Ordinal));   // 拒绝方不消耗单号

        var update = controller[updateStart..approveStart];
        Assert.True(update.IndexOf("EnsureLineShape", StringComparison.Ordinal)
                    < update.IndexOf("Db.StockTransferDetails.RemoveRange", StringComparison.Ordinal));   // 校验先于明细替换

        var rules = ReadSource("ERP.Application/Services/StockTransferPostingRules.cs");
        Assert.Contains("EnsureLineShape", rules);
        Assert.Contains("EnsureLiveMasterDataAsync", rules);
        Assert.DoesNotContain("SaveChanges", rules);                 // 纯判定 + 只读查询，不落库
        Assert.Contains("AsNoTracking", rules);
        Assert.DoesNotContain("请合并为一行", rules);                 // 不引入任意唯一性规则：重复商品行受支持
        Assert.Equal("stock-query", StockTransferPostingRules.RequiredMenuCode);
    }

    // ==================== 测试辅助 ====================

    private static StockTransferController Authorized(ErpDbContext db)
        => StockTransferTestAuthorization.CreateAuthorized(db);

    /// <summary>拒绝场景的统一断言：零单据、零明细、零库存流水、零单号消耗。</summary>
    private static void AssertRejectedWithNoSideEffects(ErpDbContext db)
    {
        Assert.Empty(db.StockTransfers);
        Assert.Empty(db.StockTransferDetails);
        Assert.Empty(db.StockMovements);
        Assert.Empty(db.SysDocumentNumberRules);
    }

    private static void SeedMasterData(ErpDbContext db)
        => StockTransferTestAuthorization.SeedMasterData(db, WarehouseA, WarehouseB, Product1, Product2);

    private static void SoftDeleteWarehouse(ErpDbContext db, long id)
    {
        var warehouse = db.BaseWarehouses.Single(w => w.Id == id);
        warehouse.IsDeleted = true;
        db.SaveChanges();
    }

    private static void DisableWarehouse(ErpDbContext db, long id)
    {
        var warehouse = db.BaseWarehouses.Single(w => w.Id == id);
        warehouse.Status = 0;
        db.SaveChanges();
    }

    private static void SoftDeleteProduct(ErpDbContext db, long id)
    {
        var product = db.BaseProducts.Single(p => p.Id == id);
        product.IsDeleted = true;
        db.SaveChanges();
    }

    private static void DisableProduct(ErpDbContext db, long id)
    {
        var product = db.BaseProducts.Single(p => p.Id == id);
        product.Status = 0;
        db.SaveChanges();
    }

    private static StockTransfer Draft(long fromWarehouseId, long toWarehouseId,
        params (long? ProductId, string Name, decimal Quantity, decimal UnitCost)[] lines)
        => new()
        {
            TransferDate = DateTime.Today,
            FromWarehouseId = fromWarehouseId,
            ToWarehouseId = toWarehouseId,
            Remark = "ERP-501_TEST",
            Details = lines.Select(l => new StockTransferDetail
            {
                ProductId = l.ProductId,
                ProductName = l.Name,
                Spec = "规格A",
                Unit = "PCS",
                Quantity = l.Quantity,
                UnitCost = l.UnitCost
            }).ToList()
        };

    private static long CreatedId(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var data = Assert.IsType<ApiResponse<object>>(ok.Value).Data!;
        return (long)data.GetType().GetProperty("Id")!.GetValue(data)!;
    }

    private static void SeedStock(ErpDbContext db, long warehouseId, long productId, decimal quantity,
        decimal totalCost)
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
        db.SaveChanges();
    }

    private static string ColumnType(IModel model, Type entityType, string propertyName)
        => model.FindEntityType(entityType)!.FindProperty(propertyName)!.GetColumnType()!;

    private static string RepoRoot()
        => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    private static string ReadSource(string relativePath)
        => File.ReadAllText(Path.Combine(RepoRoot(), "src",
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
}
