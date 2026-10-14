using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-522 采购入库单位完整性单元测试（内存库 + 真实 HTTP 身份 + 真实控制器）：
/// <list type="number">
/// <item>手工未链接与显式链接草稿在<b>单号预约 / 明细整体替换之前</b>拒绝「显式非空且不在商品当前基础 / 装箱单位内」的单位；</item>
/// <item>保留空 / 空白单位回退与合法基础 / 装箱单位（大小写、首尾空白不敏感），装箱单位按当前正数装箱数<b>恰好折算一次</b>；</item>
/// <item>商品未维护可用基础 / 装箱单位（缺失 / 非法主数据）时受控拒绝，绝不臆造新单位；</item>
/// <item>提交 / 审核在既有承诺边界内复核仍然生效明细的单位资格，主数据被改写即整体拒绝；</item>
/// <item>每个拒绝场景断言零单据 / 零明细 / 零库存 / 零流水 / 零单号消耗，来源容量与状态不变。</item>
/// </list>
/// </summary>
public class StockInUnitIntegrityTests
{
    private const long SupplierA = 986001L;
    private const long WarehouseA = 986101L;
    private const long ProductA = 986201L;            // 基础 PCS / 装箱 CTN(12)
    private const long PlainProduct = 986202L;        // 仅基础 PCS
    private const long NoUnitProduct = 986203L;       // 历史商品：基础 / 装箱单位均未维护
    private const long NoBaseWithPackage = 986204L;   // 只维护装箱单位、缺基础单位
    private const long BadFactorProduct = 986205L;    // 装箱数非法（0）

    // ==================== 1. 显式越界单位：创建即拒绝（零副作用） ====================

    [Theory]
    [InlineData("KG")]
    [InlineData("kg")]
    [InlineData(" SET ")]
    [InlineData("工程量")]
    public async Task 创建_未链接草稿_显式越界单位_拒绝且零副作用(string unit)
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        SeedNumberRule(db);

        var error = await Assert.ThrowsAsync<BusinessException>(
            () => Controller(db).Create(Draft(null, Line(ProductA, 1m, unit))));

        Assert.Equal(ErrorCodes.RuleConflict, error.Code);
        Assert.Contains("不在当前有效单位口径内", error.Message);
        AssertRejectedWithNoSideEffects(db);
    }

    [Fact]
    public async Task 创建_链接来源草稿_显式越界单位_在来源校验前拒绝且来源不变()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        SeedNumberRule(db);
        var order = SeedApprovedOrder(db, "PO-522-LINK-DENY", 24m);

        var error = await Assert.ThrowsAsync<BusinessException>(
            () => Controller(db).Create(Draft(order.Id, Line(ProductA, 1m, "KG"))));

        Assert.Equal(ErrorCodes.RuleConflict, error.Code);
        Assert.Contains("不在当前有效单位口径内", error.Message);
        AssertRejectedWithNoSideEffects(db);

        // 来源订单本身逐字段不变（未审核改状态、未改数量）。
        var storedOrder = db.PurchaseOrders.AsNoTracking().Single(o => o.Id == order.Id);
        Assert.Equal(DocumentStatus.Approved, storedOrder.Status);
        Assert.Equal(24m, db.PurchaseOrderDetails.AsNoTracking().Single(d => d.PurchaseOrderId == order.Id).Quantity);
    }

    [Fact]
    public async Task 创建_合法行与越界行混合_整体拒绝且零副作用()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        SeedNumberRule(db);

        var error = await Assert.ThrowsAsync<BusinessException>(() => Controller(db).Create(
            Draft(null, Line(ProductA, 2m, "PCS"), Line(PlainProduct, 1m, "KG"))));

        Assert.Equal(ErrorCodes.RuleConflict, error.Code);
        AssertRejectedWithNoSideEffects(db);
    }

    // ==================== 2. 非法 / 缺失主数据：受控拒绝，绝不臆造单位 ====================

    [Fact]
    public async Task 创建_商品未维护任何单位_显式非空单位_受控拒绝不臆造单位()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        SeedNumberRule(db);

        var error = await Assert.ThrowsAsync<BusinessException>(
            () => Controller(db).Create(Draft(null, Line(NoUnitProduct, 1m, "PCS"))));

        Assert.Equal(ErrorCodes.RuleConflict, error.Code);
        Assert.Contains("未设置", error.Message);
        AssertRejectedWithNoSideEffects(db);
    }

    [Fact]
    public async Task 创建_缺基础单位但仍提供装箱单位_显式越界单位_受控拒绝()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        SeedNumberRule(db);

        var error = await Assert.ThrowsAsync<BusinessException>(
            () => Controller(db).Create(Draft(null, Line(NoBaseWithPackage, 1m, "PCS"))));

        Assert.Equal(ErrorCodes.RuleConflict, error.Code);
        Assert.Contains("不在当前有效单位口径内", error.Message);
        AssertRejectedWithNoSideEffects(db);
    }

    [Fact]
    public async Task 创建_装箱单位但商品缺基础单位_装箱换算受控拒绝不臆造单位()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        SeedNumberRule(db);

        var error = await Assert.ThrowsAsync<BusinessException>(
            () => Controller(db).Create(Draft(null, Line(NoBaseWithPackage, 1m, "CTN"))));

        Assert.Equal(ErrorCodes.RuleConflict, error.Code);
        Assert.Contains("未设置基础单位", error.Message);
        AssertRejectedWithNoSideEffects(db);
    }

    [Fact]
    public async Task 创建_装箱数非法_装箱换算受控拒绝()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        SeedNumberRule(db);

        var error = await Assert.ThrowsAsync<BusinessException>(
            () => Controller(db).Create(Draft(null, Line(BadFactorProduct, 1m, "CTN"))));

        Assert.Equal(ErrorCodes.RuleConflict, error.Code);
        Assert.Contains("必须大于 0", error.Message);
        AssertRejectedWithNoSideEffects(db);
    }

    [Fact]
    public async Task 创建_装箱换算数量溢出_受控拒绝且不静默截断()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        SeedNumberRule(db);

        var error = await Assert.ThrowsAsync<BusinessException>(
            () => Controller(db).Create(Draft(null, Line(ProductA, decimal.MaxValue / 2m, "CTN"))));

        Assert.Equal(ErrorCodes.InvalidParameter, error.Code);
        Assert.Contains("超出可表示范围", error.Message);
        AssertRejectedWithNoSideEffects(db);
    }

    // ==================== 3. 既有支持口径：空白回退 / 基础单位 / 装箱折算恰好一次 ====================

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task 创建_空白单位_保持既有回退可保存且不折算(string unit)
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        SeedNumberRule(db);

        var result = await Controller(db).Create(Draft(null, Line(ProductA, 3.5m, unit)));

        Assert.IsType<OkObjectResult>(result);
        var saved = db.StockIns.Include(o => o.Details).Single();
        var detail = Assert.Single(saved.Details);
        Assert.Equal(3.5m, detail.Quantity);          // 空单位按既有回退：不折算
        Assert.Equal(3.5m, saved.TotalQuantity);
        Assert.Equal(1L, NumberSequence(db));         // 合法请求恰好消耗一个单号
    }

    [Theory]
    [InlineData("PCS")]
    [InlineData("pcs")]
    [InlineData(" Pcs ")]
    public async Task 创建_基础单位_大小写与首尾空白不敏感_放行且不折算(string unit)
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        SeedNumberRule(db);

        var result = await Controller(db).Create(Draft(null, Line(ProductA, 2m, unit)));

        Assert.IsType<OkObjectResult>(result);
        var saved = db.StockIns.Include(o => o.Details).Single();
        var detail = Assert.Single(saved.Details);
        Assert.Equal(2m, detail.Quantity);
        Assert.Equal(2m, saved.TotalQuantity);
    }

    [Theory]
    [InlineData("CTN")]
    [InlineData("ctn")]
    [InlineData(" cTn ")]
    public async Task 创建_装箱单位_恰好折算一次且审核取消按基础单位一致(string unit)
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        SeedNumberRule(db);
        var controller = Controller(db);

        var id = CreatedId(await controller.Create(Draft(null, Line(ProductA, 2m, unit))));
        var stored = db.StockInDetails.AsNoTracking().Single(d => d.StockInId == id);
        Assert.Equal("PCS", stored.Unit);
        Assert.Equal(24m, stored.Quantity);           // 2 CTN → 24 PCS，只折算一次

        await controller.Submit(id);
        await controller.Approve(id);
        Assert.Equal(24m, db.Stocks.Single().Quantity);
        Assert.Equal(24m, db.StockMovements.Single().Quantity);
        Assert.Equal("PCS", db.StockMovements.Single().Unit);
        Assert.Equal(24m, db.StockInDetails.AsNoTracking().Single(d => d.StockInId == id).Quantity);

        await controller.Cancel(id);
        Assert.Equal(0m, db.Stocks.Single().Quantity);
        Assert.Equal(DocumentStatus.Cancelled, db.StockIns.AsNoTracking().Single(o => o.Id == id).Status);
    }

    [Fact]
    public async Task 创建_同商品重复行_逐行折算后累计为基础数量()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        SeedNumberRule(db);

        var id = CreatedId(await Controller(db).Create(Draft(null,
            Line(ProductA, 1m, "CTN"), Line(ProductA, 2m, "CTN"))));

        var details = db.StockInDetails.AsNoTracking().Where(d => d.StockInId == id).OrderBy(d => d.Id).ToList();
        Assert.Equal(2, details.Count);
        Assert.All(details, d => Assert.Equal("PCS", d.Unit));
        Assert.Equal(new[] { 12m, 24m }, details.Select(d => d.Quantity).ToArray());
        Assert.Equal(36m, db.StockIns.AsNoTracking().Single(o => o.Id == id).TotalQuantity);
    }

    // ==================== 4. 提交 / 审核承诺边界复核（主数据被改写） ====================

    [Theory]
    [InlineData("units-cleared")]
    [InlineData("base-mutated")]
    public async Task 提交与审核_已存草稿主数据被改写_承诺边界拒绝且零副作用(string scenario)
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        SeedNumberRule(db);
        var controller = Controller(db);
        var id = CreatedId(await controller.Create(Draft(null, Line(ProductA, 2m, "PCS"))));

        var product = db.BaseProducts.Single(p => p.Id == ProductA);
        if (scenario == "units-cleared")
        {
            product.Unit = string.Empty;       // 主数据缺失：显式已存单位无法再判定 → 受控拒绝
            product.PackageUnit = string.Empty;
        }
        else
        {
            product.Unit = "BOX";              // 主数据改写：已存单位不再落在基础 / 装箱单位内 → 受控拒绝
        }
        db.SaveChanges();

        var submitError = await Assert.ThrowsAsync<BusinessException>(() => controller.Submit(id));
        Assert.Equal(ErrorCodes.RuleConflict, submitError.Code);
        Assert.Contains("不在当前有效单位口径内", submitError.Message);
        Assert.Equal(DocumentStatus.Pending, db.StockIns.AsNoTracking().Single(o => o.Id == id).Status);
        Assert.Empty(db.Stocks);
        Assert.Empty(db.StockMovements);

        // 历史形状：直接置为「已提交」后审核同样在既有锁内拒绝。
        var stored = db.StockIns.Single(o => o.Id == id);
        stored.Status = DocumentStatus.Submitted;
        db.SaveChanges();

        var approveError = await Assert.ThrowsAsync<BusinessException>(() => controller.Approve(id));
        Assert.Equal(ErrorCodes.RuleConflict, approveError.Code);
        Assert.Contains("不在当前有效单位口径内", approveError.Message);
        Assert.Equal(DocumentStatus.Submitted, db.StockIns.AsNoTracking().Single(o => o.Id == id).Status);
        Assert.Empty(db.Stocks);
        Assert.Empty(db.StockMovements);
    }

    [Fact]
    public async Task 审核_历史已提交单据带越界单位_拒绝且零库存零流水零状态变化()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        SeedNumberRule(db);
        var legacy = new StockIn
        {
            StockInNo = "RK-522-LEGACY",
            StockInDate = DateTime.Today,
            SupplierId = SupplierA,
            WarehouseId = WarehouseA,
            TotalQuantity = 5m,
            Status = DocumentStatus.Submitted
        };
        db.StockIns.Add(legacy);
        db.SaveChanges();
        db.StockInDetails.Add(new StockInDetail
        {
            StockInId = legacy.Id, ProductId = ProductA, ProductName = "商品A", Spec = "规格A",
            Unit = "KG", Quantity = 5m
        });
        db.SaveChanges();

        var error = await Assert.ThrowsAsync<BusinessException>(() => Controller(db).Approve(legacy.Id));

        Assert.Equal(ErrorCodes.RuleConflict, error.Code);
        Assert.Contains("不在当前有效单位口径内", error.Message);
        Assert.Equal(DocumentStatus.Submitted, db.StockIns.AsNoTracking().Single(o => o.Id == legacy.Id).Status);
        Assert.Empty(db.Stocks);
        Assert.Empty(db.StockMovements);
    }

    // ==================== 5. 链接来源：折算后按基础单位累计容量与取消释放 ====================

    [Fact]
    public async Task 审核_链接来源_折算后按基础单位累计容量且取消释放不重复折算()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        SeedNumberRule(db);
        var order = SeedApprovedOrder(db, "PO-522-CAP", 24m);
        var controller = Controller(db);

        var firstId = CreatedId(await controller.Create(Draft(order.Id, Line(ProductA, 2m, "CTN"))));
        await controller.Submit(firstId);
        await controller.Approve(firstId);
        Assert.Equal(24m, db.Stocks.Single().Quantity);
        Assert.Equal(24m, db.StockInDetails.AsNoTracking().Single(d => d.StockInId == firstId).Quantity);

        var secondId = CreatedId(await controller.Create(Draft(order.Id, Line(ProductA, 1m, "CTN"))));
        await controller.Submit(secondId);
        var over = await Assert.ThrowsAsync<BusinessException>(() => controller.Approve(secondId));
        Assert.Equal(ErrorCodes.RuleConflict, over.Code);
        Assert.Equal(24m, db.Stocks.Single().Quantity);            // 超收被拒：库存不变
        Assert.Equal(DocumentStatus.Submitted, db.StockIns.AsNoTracking().Single(o => o.Id == secondId).Status);

        await controller.Cancel(firstId);                          // 取消释放剩余容量
        Assert.Equal(0m, db.Stocks.Single().Quantity);
        await controller.Approve(secondId);
        Assert.Equal(12m, db.Stocks.Single().Quantity);            // 1 CTN → 12 PCS，仍只折算一次
    }

    // ==================== 6. 纯判定 / 批量复核 / 接线契约 ====================

    [Theory]
    [InlineData(null, "PCS", "CTN", true)]
    [InlineData("", "PCS", "CTN", true)]
    [InlineData("   ", "PCS", "CTN", true)]
    [InlineData("PCS", "PCS", "CTN", true)]
    [InlineData("pcs", "PCS", "CTN", true)]
    [InlineData(" Pcs ", "PCS", "CTN", true)]
    [InlineData("CTN", "PCS", "CTN", true)]
    [InlineData("ctn", "PCS", "CTN", true)]
    [InlineData("KG", "PCS", "CTN", false)]
    [InlineData("kg", "PCS", "CTN", false)]
    [InlineData("PCS", "", "", false)]
    [InlineData("KG", "", "CTN", false)]
    [InlineData("CTN", "", "CTN", true)]
    [InlineData("PCS", "PCS", "", true)]
    [InlineData("KG", "PCS", "", false)]
    [InlineData("", "", "", true)]
    public void 单位资格纯判定_口径边界(string? lineUnit, string? baseUnit, string? packageUnit, bool expected)
        => Assert.Equal(expected, StockUnitConversion.IsUnitEligible(lineUnit, baseUnit, packageUnit));

    [Fact]
    public void 批量复核_缺失商品行跳过_越界行抛受控错误()
    {
        var product = new BaseProduct
        {
            Id = ProductA, ProductCode = "P-522", ProductName = "商品A",
            Unit = "PCS", PackageUnit = "CTN", UnitsPerPackage = 12
        };
        var products = new Dictionary<long, BaseProduct> { [ProductA] = product };

        // 缺失商品行由既有在用商品护栏负责（此处跳过），合法装箱行放行。
        StockUnitConversion.EnsureUnitsEligible(products, new[]
        {
            new StockInDetail { ProductId = 999999L, Unit = "KG", Quantity = 1m },
            new StockInDetail { ProductId = ProductA, Unit = "CTN", Quantity = 1m }
        });

        var error = Assert.Throws<BusinessException>(() => StockUnitConversion.EnsureUnitsEligible(products,
            new[] { new StockInDetail { ProductId = ProductA, Unit = "KG", Quantity = 1m } }));
        Assert.Equal(ErrorCodes.RuleConflict, error.Code);
        Assert.Contains("不在当前有效单位口径内", error.Message);
    }

    [Fact]
    public void 接线契约_入库路径启用单位资格且其他单据族保持既有口径()
    {
        var stockIn = ReadSource("src", "ERP.Api", "Controllers", "StockInController.cs");
        Assert.Equal(2, Occurrences(stockIn, "NormalizeAsync(Db, entity.Details, ensureUnitEligible: true)"));
        Assert.Contains("EnsureUnitsEligibleAsync", stockIn);

        var conversion = ReadSource("src", "ERP.Api", "Controllers", "StockUnitConversion.cs");
        // 未启用单位资格的重载：入库 2 参旧重载 + 出库 + 销售退货 + 采购退货（其他族保持既有口径）。
        Assert.Equal(4, Occurrences(conversion, "ensureUnitEligible: false"));

        Assert.Contains("任意", StockUnitConversion.UnitEligibilityRuleText);
        Assert.Contains("UnitsPerPackage", StockUnitConversion.UnitEligibilityRuleText);
        Assert.Contains("空单位", StockUnitConversion.UnitEligibilityRuleText);
    }

    // ==================== 辅助 ====================

    private static StockInController Controller(ErpDbContext db)
        => StockInTestAuthorization.ForUser(db, StockInTestAuthorization.SeedPrivilegedInboundOperator(db));

    private static StockIn Draft(long? purchaseOrderId, params StockInDetail[] lines)
        => new()
        {
            StockInDate = DateTime.Today,
            PurchaseOrderId = purchaseOrderId,
            SupplierId = SupplierA,
            WarehouseId = WarehouseA,
            Remark = "ERP-522_TEST",
            Details = lines.ToList()
        };

    private static StockInDetail Line(long productId, decimal quantity, string unit)
        => new()
        {
            ProductId = productId,
            ProductName = $"商品{productId}",
            Spec = "规格A",
            Unit = unit,
            Quantity = quantity
        };

    private static void SeedMaster(ErpDbContext db)
    {
        StockInTestAuthorization.SeedSupplier(db, SupplierA, "单位完整性供应商");
        StockInTestAuthorization.SeedWarehouse(db, WarehouseA, "单位完整性仓");
        StockInTestAuthorization.SeedProduct(db, ProductA, "PCS", "CTN", 12, "箱装商品");
        StockInTestAuthorization.SeedProduct(db, PlainProduct, "PCS", string.Empty, 0, "基础单位商品");
        StockInTestAuthorization.SeedProduct(db, NoUnitProduct, string.Empty, string.Empty, 0, "无单位历史商品");
        StockInTestAuthorization.SeedProduct(db, NoBaseWithPackage, string.Empty, "CTN", 12, "缺基础单位商品");
        StockInTestAuthorization.SeedProduct(db, BadFactorProduct, "PCS", "CTN", 0, "非法装箱数商品");
    }

    private static void SeedNumberRule(ErpDbContext db)
    {
        db.SysDocumentNumberRules.Add(new SysDocumentNumberRule
        {
            DocumentType = DocumentType.StockIn,
            RuleCode = "RK-522",
            RuleName = "采购入库单号",
            Prefix = "RK",
            DateFormat = "yyyyMMdd",
            SerialLength = 4,
            CurrentSequence = 0
        });
        db.SaveChanges();
    }

    private static PurchaseOrder SeedApprovedOrder(ErpDbContext db, string orderNo, decimal capacity)
    {
        var order = new PurchaseOrder
        {
            OrderNo = orderNo,
            OrderDate = DateTime.Today.AddDays(-2),
            SupplierId = SupplierA,
            Currency = Currency.CNY,
            ExchangeRate = 1m,
            Status = DocumentStatus.Approved
        };
        db.PurchaseOrders.Add(order);
        db.SaveChanges();

        db.PurchaseOrderDetails.Add(new PurchaseOrderDetail
        {
            PurchaseOrderId = order.Id,
            ProductId = ProductA,
            ProductName = "商品A",
            Spec = "规格A",
            Unit = "PCS",
            Quantity = capacity,
            UnitPrice = 7.5m,
            Amount = capacity * 7.5m
        });
        db.SaveChanges();
        return order;
    }

    private static long NumberSequence(ErpDbContext db)
        => db.SysDocumentNumberRules.AsNoTracking()
            .Where(r => r.DocumentType == DocumentType.StockIn)
            .Select(r => r.CurrentSequence)
            .FirstOrDefault();

    /// <summary>拒绝场景的统一断言：零单据、零明细、零库存、零流水、零单号消耗。</summary>
    private static void AssertRejectedWithNoSideEffects(ErpDbContext db)
    {
        Assert.Empty(db.StockIns);
        Assert.Empty(db.StockInDetails);
        Assert.Empty(db.Stocks);
        Assert.Empty(db.StockMovements);
        Assert.Equal(0L, NumberSequence(db));
    }

    private static long CreatedId(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var payload = Assert.IsType<ApiResponse<object>>(ok.Value).Data!;
        return (long)payload.GetType().GetProperty("Id")!.GetValue(payload)!;
    }

    private static int Occurrences(string text, string value)
    {
        var count = 0;
        var index = text.IndexOf(value, StringComparison.Ordinal);
        while (index >= 0)
        {
            count++;
            index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal);
        }
        return count;
    }

    private static string ReadSource(params string[] segments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "NEWERP.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine(directory!.FullName,
            Path.Combine(segments).Replace('/', Path.DirectorySeparatorChar)));
    }
}
