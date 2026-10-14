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
/// ERP-523 销售出库单位完整性单元测试（内存库 + 真实 HTTP 身份 + 真实控制器）：
/// <list type="number">
/// <item>手工未链接与显式链接草稿在<b>单号预约 / 明细整体替换之前</b>拒绝「显式非空且不在商品当前基础 / 装箱单位内」的单位；</item>
/// <item>保留空 / 空白单位回退与合法基础 / 装箱单位（大小写、首尾空白不敏感），装箱单位按当前正数装箱数<b>恰好折算一次</b>；</item>
/// <item>商品未维护可用基础 / 装箱单位（缺失 / 非法主数据）时受控拒绝，绝不臆造新单位；</item>
/// <item>提交 / 审核在既有承诺边界内复核仍然生效明细的单位资格，主数据被改写即整体拒绝；</item>
/// <item>链接来源按折算后的基础单位累计容量、不重复折算，第二张超发在审核前拒绝；</item>
/// <item>每个拒绝场景断言零单据 / 零明细 / 零库存流水 / 零单号消耗，来源订单与状态不变。</item>
/// </list>
/// </summary>
public class StockOutUnitIntegrityTests
{
    private const long CustomerA = 987001L;
    private const long WarehouseA = 987101L;
    private const long ProductA = 987201L;            // 基础 PCS / 装箱 CTN(12)
    private const long PlainProduct = 987202L;        // 仅基础 PCS
    private const long NoUnitProduct = 987203L;       // 历史商品：基础 / 装箱单位均未维护
    private const long NoBaseWithPackage = 987204L;   // 只维护装箱单位、缺基础单位
    private const long BadFactorProduct = 987205L;    // 装箱数非法（0）

    // ==================== 1. 纯判定：单位资格口径边界 ====================

    [Theory]
    [InlineData("", "PCS", "CTN", true)]          // 空单位：既有回退
    [InlineData("   ", "PCS", "CTN", true)]       // 空白单位：既有回退
    [InlineData("PCS", "PCS", "CTN", true)]       // 基础单位
    [InlineData("pcs", "PCS", "CTN", true)]       // 大小写不敏感
    [InlineData(" Pcs ", "PCS", "CTN", true)]     // 首尾空白 + 大小写
    [InlineData("CTN", "PCS", "CTN", true)]       // 装箱单位
    [InlineData("ctn", "PCS", "CTN", true)]
    [InlineData(" Ctn ", "PCS", "CTN", true)]
    [InlineData("KG", "PCS", "CTN", false)]       // 任意显式非空单位：拒绝
    [InlineData("SET", "PCS", "CTN", false)]
    [InlineData("工程量", "PCS", "CTN", false)]
    [InlineData("PCS", "", "", false)]            // 主数据未维护任何单位：拒绝，绝不臆造
    [InlineData("CTN", "", "CTN", true)]          // 仅维护装箱单位：装箱单位本身仍被识别（折算边界另行受控）
    [InlineData("PCS", "", "CTN", false)]
    public void 单位资格纯判定_口径边界(string? lineUnit, string? baseUnit, string? packageUnit, bool expected)
        => Assert.Equal(expected, StockUnitConversion.IsUnitEligible(lineUnit, baseUnit, packageUnit));

    [Fact]
    public void 销售出库单位资格文案_与既有共享口径同源()
    {
        Assert.Contains("销售出库", StockUnitConversion.StockOutUnitEligibilityRuleText);
        Assert.Contains("UnitsPerPackage", StockUnitConversion.StockOutUnitEligibilityRuleText);
        Assert.Contains("空单位", StockUnitConversion.StockOutUnitEligibilityRuleText);
        Assert.Contains("任意", StockUnitConversion.StockOutUnitEligibilityRuleText);
        // 采购入库口径保持 ERP-522 原样，不被改写。
        Assert.Contains("采购入库", StockUnitConversion.UnitEligibilityRuleText);
    }

    // ==================== 2. 显式越界单位：创建即拒绝（零副作用） ====================

    [Theory]
    [InlineData("KG")]
    [InlineData("kg")]
    [InlineData(" SET ")]
    [InlineData("工程量")]
    public async Task 创建_未链接草稿_显式越界单位_拒绝且零副作用(string unit)
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);

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
        var order = SeedApprovedOrder(db, "SO-523-LINK-DENY", 24m);

        var error = await Assert.ThrowsAsync<BusinessException>(
            () => Controller(db).Create(Draft(order.Id, Line(ProductA, 1m, "KG"))));

        Assert.Equal(ErrorCodes.RuleConflict, error.Code);
        Assert.Contains("不在当前有效单位口径内", error.Message);
        AssertRejectedWithNoSideEffects(db);

        var storedOrder = db.SalesOrders.AsNoTracking().Single(o => o.Id == order.Id);
        Assert.Equal(DocumentStatus.Approved, storedOrder.Status);
        Assert.Equal(24m, db.SalesOrderDetails.AsNoTracking().Single(d => d.SalesOrderId == order.Id).Quantity);
    }

    [Fact]
    public async Task 创建_合法行与越界行混合_整体拒绝且零副作用()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);

        var error = await Assert.ThrowsAsync<BusinessException>(() => Controller(db).Create(
            Draft(null, Line(ProductA, 2m, "PCS"), Line(PlainProduct, 1m, "KG"))));

        Assert.Equal(ErrorCodes.RuleConflict, error.Code);
        Assert.Contains("不在当前有效单位口径内", error.Message);
        AssertRejectedWithNoSideEffects(db);
    }

    // ==================== 3. 非法 / 缺失主数据：受控拒绝，绝不臆造单位 ====================

    [Fact]
    public async Task 创建_商品未维护任何单位_显式非空单位_受控拒绝不臆造单位()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);

        var error = await Assert.ThrowsAsync<BusinessException>(
            () => Controller(db).Create(Draft(null, Line(NoUnitProduct, 1m, "PCS"))));

        Assert.Equal(ErrorCodes.RuleConflict, error.Code);
        Assert.Contains("不在当前有效单位口径内", error.Message);
        Assert.Contains("未设置", error.Message);              // 缺失基础 / 装箱单位以「未设置」展示，绝不臆造新单位
        AssertRejectedWithNoSideEffects(db);
    }

    [Fact]
    public async Task 创建_只维护装箱单位缺基础单位_显式基础单位_受控拒绝()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);

        var error = await Assert.ThrowsAsync<BusinessException>(
            () => Controller(db).Create(Draft(null, Line(NoBaseWithPackage, 1m, "PCS"))));

        Assert.Equal(ErrorCodes.RuleConflict, error.Code);
        Assert.Contains("不在当前有效单位口径内", error.Message);
        AssertRejectedWithNoSideEffects(db);
    }

    [Fact]
    public async Task 创建_只维护装箱单位缺基础单位_显式装箱单位_受控拒绝不猜测换算()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);

        var error = await Assert.ThrowsAsync<BusinessException>(
            () => Controller(db).Create(Draft(null, Line(NoBaseWithPackage, 1m, "CTN"))));

        Assert.Equal(ErrorCodes.RuleConflict, error.Code);
        Assert.Contains("未设置基础单位", error.Message);
        AssertRejectedWithNoSideEffects(db);
    }

    [Fact]
    public async Task 创建_装箱数非法_显式装箱单位_受控拒绝()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);

        var error = await Assert.ThrowsAsync<BusinessException>(
            () => Controller(db).Create(Draft(null, Line(BadFactorProduct, 1m, "CTN"))));

        Assert.Equal(ErrorCodes.RuleConflict, error.Code);
        Assert.Contains("必须大于 0", error.Message);
        AssertRejectedWithNoSideEffects(db);
    }

    [Fact]
    public async Task 创建_装箱数非法_显式越界单位_仍按越界单位拒绝()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);

        var error = await Assert.ThrowsAsync<BusinessException>(
            () => Controller(db).Create(Draft(null, Line(BadFactorProduct, 1m, "KG"))));

        Assert.Equal(ErrorCodes.RuleConflict, error.Code);
        Assert.Contains("不在当前有效单位口径内", error.Message);
        AssertRejectedWithNoSideEffects(db);
    }

    // ==================== 4. 支持口径：空白回退 / 基础单位 / 装箱折算恰好一次 ====================

    [Theory]
    [InlineData("CTN", 24)]
    [InlineData("ctn", 24)]
    [InlineData(" Ctn ", 24)]
    public async Task 创建_装箱单位大小写与空白_折算一次为基础单位(string unit, decimal expectedQuantity)
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);

        var result = await Controller(db).Create(Draft(null, Line(ProductA, 2m, unit)));

        var saved = db.StockOuts.Include(o => o.Details).Single();
        Assert.IsType<OkObjectResult>(result);
        Assert.Equal("PCS", saved.Details.Single().Unit);              // 折算后改写为基础单位
        Assert.Equal(expectedQuantity, saved.Details.Single().Quantity); // 2 × 12 恰好一次
        Assert.Equal(expectedQuantity, saved.TotalQuantity);
    }

    [Theory]
    [InlineData("PCS", 2)]
    [InlineData("pcs", 2)]
    [InlineData(" Pcs ", 2)]
    [InlineData("", 3.5)]
    [InlineData("   ", 2)]
    public async Task 创建_基础单位与空白单位_保留既有回退不折算(string unit, decimal quantity)
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);

        await Controller(db).Create(Draft(null, Line(ProductA, quantity, unit)));

        var detail = db.StockOutDetails.AsNoTracking().Single();
        Assert.Equal(unit, detail.Unit);                 // 非装箱单位保持原样，不臆造改写到基础单位
        Assert.Equal(quantity, detail.Quantity);         // 不折算
        Assert.Equal(quantity, db.StockOuts.Single().TotalQuantity);
    }

    [Fact]
    public async Task 修改_显式越界单位_在明细整体替换前拒绝且既有单据逐字段不变()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        var ctl = Controller(db);
        var id = CreatedId(await ctl.Create(Draft(null, Line(ProductA, 2m, "PCS"))));
        var existing = db.StockOuts.Include(o => o.Details).Single();
        var originalDetailId = existing.Details.Single().Id;

        var error = await Assert.ThrowsAsync<BusinessException>(
            () => ctl.Update(id, Draft(null, Line(ProductA, 1m, "KG"))));

        Assert.Equal(ErrorCodes.RuleConflict, error.Code);
        Assert.Contains("不在当前有效单位口径内", error.Message);

        var reloaded = db.StockOuts.Include(o => o.Details).AsNoTracking().Single();
        Assert.Equal(2m, reloaded.TotalQuantity);
        Assert.Equal(2m, reloaded.Details.Single().Quantity);
        Assert.Equal("PCS", reloaded.Details.Single().Unit);
        Assert.Equal(originalDetailId, reloaded.Details.Single().Id);
        Assert.Equal(DocumentStatus.Pending, reloaded.Status);
        Assert.Empty(db.StockMovements);
    }


    [Fact]
    public async Task 创建_重复商品行_按行折算且累计语义保留()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);

        await Controller(db).Create(Draft(null,
            Line(ProductA, 2m, "CTN"),     // 24 PCS
            Line(ProductA, 3m, "PCS")));   // 3 PCS

        var saved = db.StockOuts.Include(o => o.Details).Single();
        Assert.Equal(2, saved.Details.Count);                       // 重复商品行保留，不合并 / 不丢弃
        Assert.Equal(24m, saved.Details.Single(d => d.Quantity == 24m).Quantity);
        Assert.Equal(3m, saved.Details.Single(d => d.Quantity == 3m).Quantity);
        Assert.Equal(27m, saved.TotalQuantity);                     // 累计语义保留
        Assert.All(saved.Details, d => Assert.Equal("PCS", d.Unit));
    }

    [Fact]
    public async Task 创建_装箱换算溢出_受控拒绝不静默截断()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);

        var error = await Assert.ThrowsAsync<BusinessException>(() => Controller(db).Create(
            Draft(null, Line(ProductA, 10_000_000_000_000_000_000_000_000_000m, "CTN"))));

        Assert.Equal(ErrorCodes.InvalidParameter, error.Code);
        Assert.Contains("超出可表示范围", error.Message);
        AssertRejectedWithNoSideEffects(db);
    }


    // ==================== 5. 提交 / 审核承诺边界：复核仍然生效的单位资格 ====================

    [Fact]
    public async Task 提交_已存显式越界单位_拒绝且状态与库存流水不变()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        var id = SeedDocument(db, "CK-523-SUB-KG", DocumentStatus.Pending, ProductA, "KG", 1m);
        var ctl = Controller(db);

        var error = await Assert.ThrowsAsync<BusinessException>(() => ctl.Submit(id));

        Assert.Equal(ErrorCodes.RuleConflict, error.Code);
        Assert.Contains("不在当前有效单位口径内", error.Message);
        Assert.Equal(DocumentStatus.Pending, db.StockOuts.Single(o => o.Id == id).Status);
        Assert.Empty(db.StockMovements);
    }

    [Fact]
    public async Task 提交_商品基础单位被改写_已存单位失效_受控拒绝()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        var ctl = Controller(db);
        var id = CreatedId(await ctl.Create(Draft(null, Line(ProductA, 2m, "PCS"))));

        var product = db.BaseProducts.Single(p => p.Id == ProductA);
        product.Unit = "BOX";                    // 基础单位被改写：已存 "PCS" 不再落在当前有效口径内
        db.SaveChanges();

        var error = await Assert.ThrowsAsync<BusinessException>(() => ctl.Submit(id));

        Assert.Equal(ErrorCodes.RuleConflict, error.Code);
        Assert.Contains("不在当前有效单位口径内", error.Message);
        Assert.Equal(DocumentStatus.Pending, db.StockOuts.Single(o => o.Id == id).Status);
        Assert.Empty(db.StockMovements);
    }

    [Fact]
    public async Task 审核_已存显式越界单位_锁内拒绝且零库存流水状态不变()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        SeedStock(db, ProductA, 50m);
        var id = SeedDocument(db, "CK-523-APP-KG", DocumentStatus.Submitted, ProductA, "KG", 1m);
        var ctl = Controller(db);

        var error = await Assert.ThrowsAsync<BusinessException>(() => ctl.Approve(id));

        Assert.Equal(ErrorCodes.RuleConflict, error.Code);
        Assert.Contains("不在当前有效单位口径内", error.Message);
        Assert.Equal(50m, db.Stocks.Single(s => s.ProductId == ProductA).Quantity);
        Assert.Empty(db.StockMovements);
        Assert.Equal(DocumentStatus.Submitted, db.StockOuts.Single(o => o.Id == id).Status);
    }

    // ==================== 6. 链接来源：按折算后的基础单位累计容量且不重复折算 ====================

    [Fact]
    public async Task 链接来源_装箱单位折算一次_按基础单位累计容量且第二张超发被拒()
    {
        using var db = TestDbFactory.Create();
        SeedMaster(db);
        var order = SeedApprovedOrder(db, "SO-523-CAP", 24m);
        SeedStock(db, ProductA, 100m);
        var ctl = Controller(db);

        var firstId = CreatedId(await ctl.Create(Draft(order.Id, Line(ProductA, 2m, "CTN"))));
        await ctl.Submit(firstId);
        await ctl.Approve(firstId);

        var firstDetail = db.StockOutDetails.AsNoTracking().Single(d => d.StockOutId == firstId);
        Assert.Equal(24m, firstDetail.Quantity);                       // 2 CTN → 24 PCS，恰好一次
        Assert.Equal("PCS", firstDetail.Unit);
        Assert.Equal(76m, db.Stocks.Single(s => s.ProductId == ProductA).Quantity);
        var movement = Assert.Single(db.StockMovements);
        Assert.Equal(24m, movement.Quantity);                          // 承诺边界不重复折算（不是 288）
        Assert.Equal(DocumentStatus.Approved, db.StockOuts.Single(o => o.Id == firstId).Status);

        // 第二张：订单授权 24 已被第一张占满，再出 1 PCS 在审核时按累计容量拒绝。
        var secondId = CreatedId(await ctl.Create(Draft(order.Id, Line(ProductA, 1m, "PCS"))));
        await ctl.Submit(secondId);

        var error = await Assert.ThrowsAsync<BusinessException>(() => ctl.Approve(secondId));

        Assert.Equal(ErrorCodes.RuleConflict, error.Code);
        Assert.Contains("超过来源销售订单授权数量", error.Message);
        Assert.Equal(76m, db.Stocks.Single(s => s.ProductId == ProductA).Quantity);
        Assert.Single(db.StockMovements);
        Assert.Equal(DocumentStatus.Submitted, db.StockOuts.Single(o => o.Id == secondId).Status);
    }

    // ==================== 7. 接线契约 ====================

    [Fact]
    public void 接线契约_销售出库草稿与承诺边界启用单位资格且不动入库策略()
    {
        var controller = ReadSource("src", "ERP.Api", "Controllers", "StockOutController.cs");
        Assert.Equal(2, Occurrences(controller, "NormalizeAsync(Db, entity.Details, ensureUnitEligible: true)"));
        Assert.Equal(2, Occurrences(controller,
            "EnsureUnitsEligibleAsync(Db, entity.Details.Where(d => !d.IsDeleted))"));

        var createStart = controller.IndexOf("public async Task<IActionResult> Create", StringComparison.Ordinal);
        var updateStart = controller.IndexOf("public async Task<IActionResult> Update", StringComparison.Ordinal);
        Assert.True(createStart >= 0 && updateStart > createStart);
        var create = controller[createStart..updateStart];
        Assert.True(create.IndexOf("NormalizeAsync(Db, entity.Details, ensureUnitEligible: true)", StringComparison.Ordinal)
            < create.IndexOf("EnsureDraftValidAsync", StringComparison.Ordinal));      // 资格判定 + 折算先于草稿护栏
        Assert.True(create.IndexOf("EnsureDraftValidAsync", StringComparison.Ordinal)
            < create.IndexOf("_noService.GenerateAsync", StringComparison.Ordinal));  // 再先于单号预约与写入

        var conversion = ReadSource("src", "ERP.Api", "Controllers", "StockUnitConversion.cs");
        Assert.Contains("StockOutUnitEligibilityRuleText", conversion);
        // 采购入库 / 销售退货 / 采购退货沿用既有「不启用」重载：数量保持 ERP-522 原样。
        Assert.Equal(4, Occurrences(conversion, "ensureUnitEligible: false"));
    }


    // ==================== 辅助 ====================

    private static StockOutController Controller(ErpDbContext db)
        => StockOutTestAuthorization.ForUser(db, StockOutTestAuthorization.SeedPrivilegedOutboundOperator(db));

    private static void SeedMaster(ErpDbContext db)
    {
        StockOutTestAuthorization.SeedWarehouse(db, WarehouseA, "出库测试仓");
        StockOutTestAuthorization.SeedCustomer(db, CustomerA, "客户A");
        StockOutTestAuthorization.SeedProduct(db, ProductA, "PCS", "CTN", 12, "箱装商品");
        StockOutTestAuthorization.SeedProduct(db, PlainProduct, "PCS", string.Empty, 0, "基础单位商品");
        StockOutTestAuthorization.SeedProduct(db, NoUnitProduct, string.Empty, string.Empty, 0, "无单位历史商品");
        StockOutTestAuthorization.SeedProduct(db, NoBaseWithPackage, string.Empty, "CTN", 12, "缺基础单位商品");
        StockOutTestAuthorization.SeedProduct(db, BadFactorProduct, "PCS", "CTN", 0, "非法装箱数商品");
        StockOutTestAuthorization.SeedStockOutNumberRule(db);
    }

    private static StockOut Draft(long? salesOrderId, params StockOutDetail[] lines)
        => new()
        {
            StockOutDate = DateTime.Today,
            SalesOrderId = salesOrderId,
            CustomerId = CustomerA,
            WarehouseId = WarehouseA,
            Remark = "ERP-523_TEST",
            Details = lines.ToList()
        };

    private static StockOutDetail Line(long productId, decimal quantity, string unit)
        => new() { ProductId = productId, ProductName = "商品", Spec = "规格A", Unit = unit, Quantity = quantity };

    private static SalesOrder SeedApprovedOrder(ErpDbContext db, string orderNo, decimal capacity)
    {
        var order = new SalesOrder
        {
            OrderNo = orderNo,
            OrderDate = DateTime.Today.AddDays(-2),
            CustomerId = CustomerA,
            Currency = Currency.USD,
            ExchangeRate = 1m,
            Status = DocumentStatus.Approved
        };
        db.SalesOrders.Add(order);
        db.SaveChanges();

        db.SalesOrderDetails.Add(new SalesOrderDetail
        {
            SalesOrderId = order.Id,
            ProductId = ProductA,
            ProductName = "商品",
            Spec = "规格A",
            Unit = "PCS",
            Quantity = capacity,
            UnitPrice = 1m,
            Amount = capacity
        });
        db.SaveChanges();
        return order;
    }

    /// <summary>直接播种一张既有出库单（绕过 Create），用于提交 / 审核承诺边界复核场景。</summary>
    private static long SeedDocument(ErpDbContext db, string no, DocumentStatus status, long productId,
        string unit, decimal quantity)
    {
        var document = new StockOut
        {
            StockOutNo = no,
            StockOutDate = DateTime.Today,
            CustomerId = CustomerA,
            WarehouseId = WarehouseA,
            TotalQuantity = quantity,
            Status = status
        };
        db.StockOuts.Add(document);
        db.SaveChanges();

        db.StockOutDetails.Add(new StockOutDetail
        {
            StockOutId = document.Id,
            ProductId = productId,
            ProductName = "商品",
            Spec = "规格A",
            Unit = unit,
            Quantity = quantity
        });
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

