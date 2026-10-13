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
/// ERP-502 销售出库草稿写入校验单元测试（内存库 + 真实 HTTP 身份）：
/// <list type="number">
/// <item>创建与修改在单号预约 / 明细整体替换之前拒绝「仓库 Id 非正 / 不存在 / 已删除 / 已停用」；</item>
/// <item>拒绝「数量非正或不可表示 / 溢出、毛重与体积为负或不可表示、表头合计不可表示 / 溢出」（与 EF 实际精度同源）；</item>
/// <item>拒绝「商品 Id 缺失 / 不存在 / 已删除 / 已停用」；</item>
/// <item>保留空草稿头（0 行有效明细）与重复商品行（累计数量语义），提交与审核要求至少一行有效明细；</item>
/// <item>单位折算先于护栏（包装单位行先折算为基础单位再校验）；</item>
/// <item>每个拒绝场景都断言零单据、零明细、零库存流水、零单号消耗，且既有单据与明细逐字段不变。</item>
/// </list>
/// 说明：全部使用内存数据库；只有「实际 EF 精度」用例以 SQL Server 提供程序<b>仅构建模型</b>（不连接数据库）。
/// </summary>
public class StockOutDraftValidationTests
{
    private const long WarehouseA = 984001L;
    private const long CustomerA = 784001L;
    private const long Product1 = 884001L;
    private const long Product2 = 884002L;
    private const long PackageProduct = 884003L;

    // ==================== 1. 创建：仓库非法（零写入 / 零单号） ====================

    [Theory]
    [InlineData("zero", ErrorCodes.InvalidParameter)]
    [InlineData("negative", ErrorCodes.InvalidParameter)]
    [InlineData("missing", ErrorCodes.InvalidParameter)]
    [InlineData("deleted", ErrorCodes.InvalidParameter)]
    [InlineData("disabled", ErrorCodes.RuleConflict)]
    [InlineData("zero-empty-details", ErrorCodes.InvalidParameter)]
    public async Task 创建_仓库非法_拒绝且零单据零明细零流水零单号(string scenario, int expectedCode)
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        if (scenario == "deleted") SoftDeleteWarehouse(db, WarehouseA);
        if (scenario == "disabled") DisableWarehouse(db, WarehouseA);

        var warehouseId = scenario switch
        {
            "zero" or "zero-empty-details" => 0L,
            "negative" => -9L,
            "missing" => 999_999_999L,
            _ => WarehouseA
        };
        var lines = scenario == "zero-empty-details"
            ? Array.Empty<StockOutDetail>()
            : new[] { Line(Product1, 1m) };

        var ctl = Authorized(db);
        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Create(Draft(warehouseId, lines)));

        Assert.Equal(expectedCode, ex.Code);
        AssertRejectedWithNoSideEffects(db);
    }

    // ==================== 2. 创建：数量 / 毛重 / 体积 / 合计非法 ====================

    [Theory]
    [InlineData(0, 0, 0, ErrorCodes.InvalidParameter)]                       // 数量为 0
    [InlineData(-1, 0, 0, ErrorCodes.InvalidParameter)]                      // 数量为负
    [InlineData(1.005, 0, 0, ErrorCodes.InvalidParameter)]                   // 数量 3 位小数：DECIMAL(18,2) 不可表示
    [InlineData(10_000_000_000_000_000, 0, 0, ErrorCodes.InvalidParameter)]  // 数量超出 DECIMAL(18,2) 上界
    [InlineData(1, -0.5, 0, ErrorCodes.InvalidParameter)]                    // 毛重为负
    [InlineData(1, 0.001, 0, ErrorCodes.InvalidParameter)]                   // 毛重不可表示
    [InlineData(1, 0, -0.5, ErrorCodes.InvalidParameter)]                    // 体积为负
    [InlineData(1, 0, 0.001, ErrorCodes.InvalidParameter)]                   // 体积不可表示
    public async Task 创建_数量毛重体积非法_拒绝且零副作用(
        decimal quantity, decimal weight, decimal volume, int expectedCode)
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);

        var ctl = Authorized(db);
        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(Draft(WarehouseA, Line(Product1, quantity, weight, volume))));

        Assert.Equal(expectedCode, ex.Code);
        AssertRejectedWithNoSideEffects(db);
    }

    [Fact]
    public async Task 创建_表头合计溢出_拒绝且零副作用()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);

        // 单行数量各自可表示（= DECIMAL(18,2) 上界），但两行合计溢出 —— 校验必须覆盖计算后的表头合计。
        var max = StockOutAuthorizationRules.MaxDecimal18Scale2;
        Assert.True(StockOutAuthorizationRules.IsQuantityRepresentable(max));

        var ctl = Authorized(db);
        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Create(Draft(WarehouseA,
            Line(Product1, max), Line(Product1, max))));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        AssertRejectedWithNoSideEffects(db);
    }

    // ==================== 3. 创建：商品非法 ====================

    [Theory]
    [InlineData("missing-id", ErrorCodes.InvalidParameter)]
    [InlineData("missing", ErrorCodes.InvalidParameter)]
    [InlineData("deleted", ErrorCodes.InvalidParameter)]
    [InlineData("disabled", ErrorCodes.RuleConflict)]
    public async Task 创建_商品非法_拒绝且零副作用(string scenario, int expectedCode)
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        if (scenario == "deleted") SoftDeleteProduct(db, Product1);
        if (scenario == "disabled") DisableProduct(db, Product1);

        var productId = scenario switch
        {
            "missing-id" => 0L,
            "missing" => 999_999_999L,
            _ => Product1
        };

        var ctl = Authorized(db);
        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Create(Draft(WarehouseA,
            Line(productId, 1m))));

        Assert.Equal(expectedCode, ex.Code);
        AssertRejectedWithNoSideEffects(db);
    }

    // ==================== 4. 空草稿头与重复商品行（既有语义保留） ====================

    [Fact]
    public async Task 创建_空明细草稿头_保留既有语义但不可提交与审核()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        var ctl = Authorized(db);

        var createdId = CreatedId(await ctl.Create(Draft(WarehouseA)));

        Assert.Equal(0m, db.StockOuts.Single(o => o.Id == createdId).TotalQuantity);
        Assert.Empty(db.StockOutDetails);
        Assert.Equal(1L, NumberSequence(db));                 // 空草稿头照常占用一次服务端单号

        var submitEx = await Assert.ThrowsAsync<BusinessException>(() => ctl.Submit(createdId));
        Assert.Equal(ErrorCodes.RuleConflict, submitEx.Code);
        Assert.Contains("无明细", submitEx.Message);
        Assert.Equal(DocumentStatus.Pending, db.StockOuts.Single(o => o.Id == createdId).Status);

        // 审核：直接播种「已提交但无明细」的直写单据（旧版遗留形状），审核期必须拒绝且零库存 / 流水。
        var legacyId = SeedSubmittedDraft(db, "CK-502-EMPTY");
        var approveEx = await Assert.ThrowsAsync<BusinessException>(() => ctl.Approve(legacyId));
        Assert.Equal(ErrorCodes.RuleConflict, approveEx.Code);
        Assert.Contains("无明细", approveEx.Message);
        Assert.Equal(DocumentStatus.Submitted, db.StockOuts.Single(o => o.Id == legacyId).Status);
        Assert.Empty(db.StockMovements);
    }

    [Fact]
    public async Task 创建_重复商品行_按累计数量保留且不引入唯一性规则()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        SeedStock(db, Product1, 100m);
        var ctl = Authorized(db);

        var id = CreatedId(await ctl.Create(Draft(WarehouseA, Line(Product1, 2m), Line(Product1, 3m))));

        Assert.Equal(5m, db.StockOuts.Single(o => o.Id == id).TotalQuantity);
        Assert.Equal(2, db.StockOutDetails.Count(d => d.StockOutId == id));

        await ctl.Submit(id);
        Assert.IsType<OkObjectResult>(await ctl.Approve(id));
        Assert.Equal(95m, db.Stocks.Single(s => s.ProductId == Product1).Quantity);
        Assert.Equal(2, db.StockMovements.Count());
    }

    // ==================== 5. 单位折算先于护栏 ====================

    [Fact]
    public async Task 创建_装箱单位行_先折算为基础单位再校验()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        var ctl = Authorized(db);

        var id = CreatedId(await ctl.Create(Draft(WarehouseA,
            new StockOutDetail { ProductId = PackageProduct, ProductName = "箱装商品", Unit = "BOX", Quantity = 2m })));

        var detail = db.StockOutDetails.Single(d => d.StockOutId == id);
        Assert.Equal("PCS", detail.Unit);                     // 折算发生在护栏之前
        Assert.Equal(24m, detail.Quantity);
        Assert.Equal(24m, db.StockOuts.Single(o => o.Id == id).TotalQuantity);
    }

    [Fact]
    public async Task 创建_装箱单位零数量行_折算后仍被拒绝且零副作用()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        var ctl = Authorized(db);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Create(Draft(WarehouseA,
            new StockOutDetail { ProductId = PackageProduct, ProductName = "箱装商品", Unit = "BOX", Quantity = 0m })));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        AssertRejectedWithNoSideEffects(db);
    }


    // ==================== 6. 修改：非法输入零改动，合法输入保留服务端身份 ====================

    [Theory]
    [InlineData("zero-warehouse", ErrorCodes.InvalidParameter)]
    [InlineData("missing-warehouse", ErrorCodes.InvalidParameter)]
    [InlineData("deleted-warehouse", ErrorCodes.InvalidParameter)]
    [InlineData("disabled-warehouse", ErrorCodes.RuleConflict)]
    [InlineData("zero-quantity", ErrorCodes.InvalidParameter)]
    [InlineData("negative-quantity", ErrorCodes.InvalidParameter)]
    [InlineData("unrepresentable-quantity", ErrorCodes.InvalidParameter)]
    [InlineData("missing-product-id", ErrorCodes.InvalidParameter)]
    [InlineData("missing-product", ErrorCodes.InvalidParameter)]
    [InlineData("deleted-product", ErrorCodes.InvalidParameter)]
    [InlineData("disabled-product", ErrorCodes.RuleConflict)]
    public async Task 修改_非法输入_拒绝且既有单据逐字段不变(string scenario, int expectedCode)
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        var ctl = Authorized(db);
        var id = CreatedId(await ctl.Create(Draft(WarehouseA, Line(Product1, 4m, 1m, 0.5m))));

        var createdDetail = db.StockOutDetails.AsNoTracking().Single(d => d.StockOutId == id);
        var created = db.StockOuts.AsNoTracking().Single(o => o.Id == id);
        var number = created.StockOutNo;
        var updatedAt = created.UpdatedAt;
        var sequence = NumberSequence(db);

        if (scenario == "deleted-warehouse") SoftDeleteWarehouse(db, WarehouseA);
        if (scenario == "disabled-warehouse") DisableWarehouse(db, WarehouseA);
        if (scenario == "deleted-product") SoftDeleteProduct(db, Product1);
        if (scenario == "disabled-product") DisableProduct(db, Product1);

        var warehouseId = scenario switch
        {
            "zero-warehouse" => 0L,
            "missing-warehouse" => 999_999_999L,
            _ => WarehouseA
        };
        var (productId, quantity) = scenario switch
        {
            "zero-quantity" => (Product1, 0m),
            "negative-quantity" => (Product1, -2m),
            "unrepresentable-quantity" => (Product1, 1.005m),
            "missing-product-id" => (0L, 3m),
            "missing-product" => (999_999_999L, 3m),
            _ => (Product1, 3m)
        };

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Update(id, Draft(warehouseId, Line(productId, quantity, 1m, 0.5m))));

        Assert.Equal(expectedCode, ex.Code);

        // 既有单据与明细逐字段不变：单号 / 状态 / 仓库 / 合计 / 明细身份与数量全部保持服务端值。
        var stored = db.StockOuts.AsNoTracking().Single(o => o.Id == id);
        Assert.Equal(number, stored.StockOutNo);
        Assert.Equal(DocumentStatus.Pending, stored.Status);
        Assert.False(stored.IsDeleted);
        Assert.Equal(WarehouseA, stored.WarehouseId);
        Assert.Equal(4m, stored.TotalQuantity);
        Assert.Equal(updatedAt, stored.UpdatedAt);            // 时间戳保持拒绝前的服务端值
        var storedDetail = db.StockOutDetails.AsNoTracking().Single(d => d.StockOutId == id);
        Assert.Equal(createdDetail.Id, storedDetail.Id);
        Assert.Equal(Product1, storedDetail.ProductId);
        Assert.Equal(4m, storedDetail.Quantity);
        Assert.Equal(sequence, NumberSequence(db));          // 不消耗新单号
        Assert.Empty(db.StockMovements);
    }

    [Fact]
    public async Task 修改_合法输入_保留服务端单号与状态并重算合计()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        var ctl = Authorized(db);
        var id = CreatedId(await ctl.Create(Draft(WarehouseA, Line(Product1, 4m, 1m, 0.5m))));

        var number = db.StockOuts.AsNoTracking().Single(o => o.Id == id).StockOutNo;
        var sequence = NumberSequence(db);

        var result = await ctl.Update(id, Draft(WarehouseA, Line(Product1, 2m, 0.25m, 0.12m), Line(Product2, 3m)));

        Assert.IsType<OkObjectResult>(result);
        var stored = db.StockOuts.AsNoTracking().Single(o => o.Id == id);
        Assert.Equal(number, stored.StockOutNo);              // 单号不被客户端改写
        Assert.Equal(DocumentStatus.Pending, stored.Status);  // 状态保持服务端值
        Assert.Equal(5m, stored.TotalQuantity);               // 2 + 3
        Assert.Equal(0.25m, stored.TotalWeight);
        Assert.Equal(0.12m, stored.TotalVolume);
        Assert.Equal(sequence, NumberSequence(db));           // 未消耗新单号
        Assert.Empty(db.StockMovements);
    }


    // ==================== 7. 实际 EF 精度与源码契约 ====================

    [Fact]
    public void EF实际精度_数量毛重体积与表头合计_与护栏常量同源()
    {
        var options = new DbContextOptionsBuilder<ErpDbContext>()
            .UseSqlServer(
                "Server=(localdb)\\NEWERP_AutoAcceptance;Database=NEWERP_AUTOTEST_MODEL_ONLY;Integrated Security=true")
            .Options;
        using var db = new ErpDbContext(options);
        var model = db.Model;

        Assert.Equal("decimal(18,2)",
            ColumnType(model, typeof(StockOutDetail), nameof(StockOutDetail.Quantity)));
        Assert.Equal("decimal(18,2)",
            ColumnType(model, typeof(StockOutDetail), nameof(StockOutDetail.Weight)));
        Assert.Equal("decimal(18,2)",
            ColumnType(model, typeof(StockOutDetail), nameof(StockOutDetail.Volume)));
        Assert.Equal("decimal(18,2)",
            ColumnType(model, typeof(StockOut), nameof(StockOut.TotalQuantity)));
        Assert.Equal("decimal(18,2)",
            ColumnType(model, typeof(StockOut), nameof(StockOut.TotalWeight)));
        Assert.Equal("decimal(18,2)",
            ColumnType(model, typeof(StockOut), nameof(StockOut.TotalVolume)));

        Assert.Equal(18, StockOutAuthorizationRules.QuantityPrecision);
        Assert.Equal(2, StockOutAuthorizationRules.QuantityScale);
        Assert.Equal(9_999_999_999_999_999.99m, StockOutAuthorizationRules.MaxDecimal18Scale2);
        Assert.True(StockOutAuthorizationRules.IsQuantityRepresentable(9_999_999_999_999_999.99m));
        Assert.True(StockOutAuthorizationRules.IsQuantityRepresentable(1.05m));
        Assert.False(StockOutAuthorizationRules.IsQuantityRepresentable(10_000_000_000_000_000m));
        Assert.False(StockOutAuthorizationRules.IsQuantityRepresentable(1.005m));
    }

    [Fact]
    public void 草稿护栏源码契约_先折算再校验且先于单号与写入()
    {
        var controller = ReadSource("src/ERP.Api/Controllers/StockOutController.cs");
        Assert.Contains("StockOutAuthorizationRules.EnsureDraftValidAsync", controller);
        Assert.Contains("StockOutAuthorizationRules.EnsureHasEffectiveLine(entity.Details, \"提交\")", controller);
        Assert.Contains("StockOutAuthorizationRules.EnsureHasEffectiveLine(entity.Details, \"审核\")", controller);

        var createStart = controller.IndexOf("public async Task<IActionResult> Create", StringComparison.Ordinal);
        var updateStart = controller.IndexOf("public async Task<IActionResult> Update", StringComparison.Ordinal);
        var approveStart = controller.IndexOf(
            "public override async Task<IActionResult> Approve", StringComparison.Ordinal);
        Assert.True(createStart >= 0 && updateStart > createStart && approveStart > updateStart);

        var create = controller[createStart..updateStart];
        Assert.True(create.IndexOf("StockUnitConversion.NormalizeAsync", StringComparison.Ordinal)
                    < create.IndexOf("EnsureDraftValidAsync", StringComparison.Ordinal));        // 折算先于护栏
        Assert.True(create.IndexOf("EnsureDraftValidAsync", StringComparison.Ordinal)
                    < create.IndexOf("_noService.GenerateAsync", StringComparison.Ordinal));     // 拒绝方不消耗单号

        var update = controller[updateStart..approveStart];
        Assert.True(update.IndexOf("StockUnitConversion.NormalizeAsync", StringComparison.Ordinal)
                    < update.IndexOf("EnsureDraftValidAsync", StringComparison.Ordinal));
        Assert.True(update.IndexOf("EnsureDraftValidAsync", StringComparison.Ordinal)
                    < update.IndexOf("Db.StockOutDetails.RemoveRange", StringComparison.Ordinal)); // 校验先于明细替换

        var rules = ReadSource("src/ERP.Application/Services/StockOutAuthorizationRules.cs");
        Assert.Contains("EnsureDraftValidAsync", rules);
        Assert.Contains("EnsureWarehouseAvailableAsync", rules);
        Assert.Contains("EnsureProductsAvailableAsync", rules);
        Assert.DoesNotContain("SaveChanges", rules);              // 纯判定 + 只读查询，不落库
        Assert.Contains("AsNoTracking", rules);
        Assert.Equal("stock-out", StockOutAuthorizationRules.RequiredMenuCode);   // 不新增菜单 / 权限
        Assert.Contains("DECIMAL(18,2)", StockOutAuthorizationRules.DraftGuardRuleText);
        Assert.Contains("绝不引入任意唯一性规则", StockOutAuthorizationRules.DraftGuardRuleText);
    }


    // ==================== 测试辅助 ====================

    private static StockOutController Authorized(ErpDbContext db)
    {
        StockOutTestAuthorization.SeedStockOutNumberRule(db);
        return StockOutTestAuthorization.Create(db);
    }

    /// <summary>播种真实在用的仓库 / 商品 / 客户主数据（存在、未删除、启用）。</summary>
    private static void SeedMaster(ErpDbContext db)
    {
        StockOutTestAuthorization.SeedWarehouse(db, WarehouseA, "出库仓A");
        StockOutTestAuthorization.SeedProduct(db, Product1, name: "商品1");
        StockOutTestAuthorization.SeedProduct(db, Product2, name: "商品2");
        StockOutTestAuthorization.SeedProduct(db, PackageProduct, packageUnit: "BOX",
            unitsPerPackage: 12, name: "箱装商品");
        StockOutTestAuthorization.SeedCustomer(db, CustomerA, "客户A");
    }

    private static StockOutDetail Line(long productId, decimal quantity, decimal weight = 0m, decimal volume = 0m)
        => new()
        {
            ProductId = productId,
            ProductName = $"商品{productId}",
            Spec = "规格A",
            Unit = "PCS",
            Quantity = quantity,
            Weight = weight,
            Volume = volume
        };

    private static StockOut Draft(long warehouseId, params StockOutDetail[] lines)
        => new()
        {
            StockOutDate = DateTime.Today,
            CustomerId = CustomerA,
            WarehouseId = warehouseId,
            Remark = "ERP-502_TEST",
            Details = lines.ToList()
        };

    /// <summary>直接播种「已提交」直写单据（旧版遗留形状，无明细），不经过 Create、不消耗单号。</summary>
    private static long SeedSubmittedDraft(ErpDbContext db, string no)
    {
        var document = new StockOut
        {
            StockOutNo = no,
            StockOutDate = DateTime.Today,
            CustomerId = CustomerA,
            WarehouseId = WarehouseA,
            Status = DocumentStatus.Submitted
        };
        db.StockOuts.Add(document);
        db.SaveChanges();
        return document.Id;
    }

    private static void SeedStock(ErpDbContext db, long productId, decimal quantity)
    {
        db.Stocks.Add(new Stock
        {
            WarehouseId = WarehouseA,
            ProductId = productId,
            Quantity = quantity,
            AvailableQuantity = quantity
        });
        db.SaveChanges();
    }

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

    private static long NumberSequence(ErpDbContext db)
        => db.SysDocumentNumberRules.AsNoTracking()
            .Where(r => r.DocumentType == DocumentType.StockOut)
            .Select(r => r.CurrentSequence)
            .FirstOrDefault();

    /// <summary>拒绝场景的统一断言：零单据、零明细、零库存流水、零单号消耗。</summary>
    private static void AssertRejectedWithNoSideEffects(ErpDbContext db)
    {
        Assert.Empty(db.StockOuts);
        Assert.Empty(db.StockOutDetails);
        Assert.Empty(db.StockMovements);
        Assert.Equal(0L, NumberSequence(db));
    }

    private static long CreatedId(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var payload = Assert.IsType<ApiResponse<object>>(ok.Value).Data!;
        return (long)payload.GetType().GetProperty("Id")!.GetValue(payload)!;
    }

    private static string ColumnType(IModel model, Type entityType, string propertyName)
        => model.FindEntityType(entityType)!.FindProperty(propertyName)!.GetColumnType()!;

    private static string ReadSource(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "NEWERP.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine(directory!.FullName,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
    }
}

