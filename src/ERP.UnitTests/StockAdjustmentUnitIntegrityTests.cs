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
/// ERP-526 库存盘点单位完整性单元测试（内存库 + 真实 HTTP 身份 + 真实 <see cref="StockAdjustmentController"/>）：
/// <list type="number">
/// <item>创建 / 修改在单号预约与明细整体替换<b>之前</b>拒绝显式提供的非空单位中不属于商品当前基础单位者
/// （<b>装箱单位也被拒绝</b> —— 盘点流程全程按基础单位计量、不实现任何包装折算，绝不改写账面 / 实盘数量或成本）；</item>
/// <item>保留空 / 空白单位回退、基础单位去首尾空白大小写不敏感受理与服务器侧数值 / 合计口径；</item>
/// <item>商品未维护基础单位时，显式非空单位受控拒绝（绝不臆造 PCS 之类新单位）；</item>
/// <item>提交 / 审核在既有单据 / 库存锁内用当前商品快照复核已存单位：主数据基础单位被改写即整体拒绝，
/// 状态 / 库存 / 流水 / 单号 / 明细逐字段不变；直接落库的历史越界单位草稿同样被审核拒绝且零记账；</item>
/// <item>销审 / 历史读取不新增资格闸门：基础单位在审核后被改写仍可正常冲销还原。</item>
/// </list>
/// <para>全部使用内存数据库，不连接 SQL Server、不触碰任何业务库数据。</para>
/// </summary>
public class StockAdjustmentUnitIntegrityTests
{
    private const long WarehouseA = 926001L;
    private const long BaseUnitProduct = 826001L;      // 基础 PCS + 装箱 CTN(12)：装箱单位也必须被拒绝
    private const long KilogramProduct = 826002L;      // 基础 KG：验证非 PCS 基础单位与大小写不敏感
    private const long NoBaseUnitProduct = 826003L;    // 只维护装箱单位、基础单位未维护
    private const long PlainProduct = 826004L;         // 仅基础 PCS（多行场景）

    // ==================== 1. 共享纯判定与文案 ====================

    [Theory]
    [InlineData("", "PCS", true)]              // 空单位：既有回退
    [InlineData("   ", "PCS", true)]           // 空白单位：既有回退
    [InlineData("PCS", "PCS", true)]           // 基础单位
    [InlineData("pcs", "PCS", true)]           // 大小写不敏感
    [InlineData(" pcs ", "PCS", true)]         // 首尾空白不敏感
    [InlineData("PCS", " pcs ", true)]         // 主数据侧空白同样收敛
    [InlineData("KG", "KG", true)]             // 非 PCS 基础单位
    [InlineData("CTN", "PCS", false)]          // 装箱单位：盘点不做包装折算 → 拒绝
    [InlineData(" ctn ", "PCS", false)]
    [InlineData("SET", "PCS", false)]          // 任意单位
    [InlineData("工程量", "PCS", false)]
    [InlineData("PCS", "", false)]             // 基础单位未维护：显式非空单位 fail closed
    [InlineData("PCS", "   ", false)]
    [InlineData("", "", true)]                 // 双方皆空：既有回退仍然放行
    [InlineData(null, null, true)]
    [InlineData("CTN", "CTN", true)]           // 基础单位恰为 CTN 时按基础单位处理
    public void 基础单位资格纯判定_显式非空单位必须等于商品基础单位(string? lineUnit, string? baseUnit,
        bool expected)
        => Assert.Equal(expected, StockAdjustmentPostingRules.IsBaseUnitEligible(lineUnit, baseUnit));

    [Fact]
    public void 盘点单位资格文案_与接口面板径同源_且明确不做包装折算()
    {
        Assert.Contains("基础单位", StockAdjustmentPostingRules.BaseUnitEligibilityRuleText);
        Assert.Contains("装箱单位", StockAdjustmentPostingRules.BaseUnitEligibilityRuleText);
        Assert.Contains("包装折算", StockAdjustmentPostingRules.BaseUnitEligibilityRuleText);
        Assert.Contains("空白单位", StockAdjustmentPostingRules.BaseUnitEligibilityRuleText);
        Assert.Contains("受控拒绝", StockAdjustmentPostingRules.BaseUnitEligibilityRuleText);

        // 总口径文案（RuleText）同步声明单位资格护栏，且不引入任意单位换算。
        Assert.Contains("基础单位", StockAdjustmentPostingRules.RuleText);
        Assert.Contains("包装折算", StockAdjustmentPostingRules.RuleText);
        Assert.Contains("已存明细单位", StockAdjustmentPostingRules.RuleText);
    }

    [Fact]
    public void 批量复核_越界单位受控拒绝_缺失商品与无效商品Id跳过()
    {
        var products = new Dictionary<long, BaseProduct>
        {
            [BaseUnitProduct] = Product(BaseUnitProduct, "箱装商品", "PCS"),
            [NoBaseUnitProduct] = Product(NoBaseUnitProduct, "缺基础单位商品", "")
        };

        var error = Assert.Throws<BusinessException>(() =>
            StockAdjustmentPostingRules.EnsureBaseUnitsEligible(products, new[]
            {
                new StockAdjustmentDetail { ProductId = BaseUnitProduct, ProductName = "箱装商品", Unit = "CTN" }
            }));
        Assert.Equal(ErrorCodes.RuleConflict, error.Code);
        Assert.Contains("不是当前基础单位", error.Message);

        var missingBaseError = Assert.Throws<BusinessException>(() =>
            StockAdjustmentPostingRules.EnsureBaseUnitsEligible(products, new[]
            {
                new StockAdjustmentDetail { ProductId = NoBaseUnitProduct, ProductName = "缺基础单位商品", Unit = "PCS" }
            }));
        Assert.Equal(ErrorCodes.RuleConflict, missingBaseError.Code);
        Assert.Contains("未设置", missingBaseError.Message);

        // 缺失商品 / 无效商品 Id / 已删除行一律跳过：由既有在用主数据护栏负责，本判定不越权。
        StockAdjustmentPostingRules.EnsureBaseUnitsEligible(products, new[]
        {
            new StockAdjustmentDetail { ProductId = null, Unit = "CTN" },
            new StockAdjustmentDetail { ProductId = 999_999L, Unit = "CTN" },
            new StockAdjustmentDetail { ProductId = BaseUnitProduct, Unit = "CTN", IsDeleted = true },
            new StockAdjustmentDetail { ProductId = BaseUnitProduct, Unit = "PCS" }
        });
    }

    // ==================== 2. 创建：显式越界单位（零写入 / 零单号） ====================

    [Theory]
    [InlineData("CTN")]         // 商品自身的装箱单位：盘点不做折算 → 不折算也不记账
    [InlineData(" ctn ")]
    [InlineData("KG")]
    [InlineData("SET")]
    [InlineData("工程量")]
    public async Task 创建_非基础单位_拒绝且零单据零明细零流水零单号(string unit)
    {
        using var db = TestDbFactory.Create();
        var ctl = NewController(db);

        var error = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(Draft((BaseUnitProduct, "箱装商品", unit, 10m, 7m, 2m))));

        Assert.Equal(ErrorCodes.RuleConflict, error.Code);
        Assert.Contains("不是当前基础单位", error.Message);
        AssertRejectedWithoutSideEffects(db);
    }

    [Fact]
    public async Task 创建_商品未维护基础单位_显式非空单位拒绝且零副作用()
    {
        using var db = TestDbFactory.Create();
        var ctl = NewController(db);

        var error = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(Draft((NoBaseUnitProduct, "缺基础单位商品", "PCS", 10m, 7m, 2m))));

        Assert.Equal(ErrorCodes.RuleConflict, error.Code);
        Assert.Contains("未设置", error.Message);
        AssertRejectedWithoutSideEffects(db);
    }

    [Theory]
    [InlineData("PCS")]
    [InlineData("pcs")]
    [InlineData(" pcs ")]
    public async Task 创建_基础单位大小写与首尾空白_受理且账面实盘成本原样(string unit)
    {
        using var db = TestDbFactory.Create();
        var ctl = NewController(db);

        var id = CreatedId(await ctl.Create(Draft((BaseUnitProduct, "箱装商品", unit, 10m, 7m, 2.5m))));

        var doc = db.StockAdjustments.Single(a => a.Id == id);
        Assert.Equal(DocumentStatus.Pending, doc.Status);
        Assert.Equal(-3m, doc.TotalDiffQuantity);
        Assert.Equal(-7.5m, doc.TotalDiffAmount);

        var detail = db.StockAdjustmentDetails.Single(d => d.StockAdjustmentId == id);
        Assert.Equal(unit, detail.Unit);                 // 服务器不臆造单位：保留调用方口径
        Assert.Equal(10m, detail.BookQuantity);          // 账面 / 实盘数量不做任何折算
        Assert.Equal(7m, detail.ActualQuantity);
        Assert.Equal(2.5m, detail.UnitCost);
        Assert.Equal(-3m, detail.DiffQuantity);          // 有符号差异口径不变
        Assert.Empty(db.StockMovements);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task 创建_空单位_受理且保留既有回退(string unit)
    {
        using var db = TestDbFactory.Create();
        var ctl = NewController(db);

        var id = CreatedId(await ctl.Create(Draft((KilogramProduct, "散装商品", unit, 7m, 9m, 3m))));

        var detail = db.StockAdjustmentDetails.Single(d => d.StockAdjustmentId == id);
        Assert.True(string.IsNullOrWhiteSpace(detail.Unit));   // 空单位保持既有回退，不被臆造为基础单位
        Assert.Equal(7m, detail.BookQuantity);
        Assert.Equal(9m, detail.ActualQuantity);
        Assert.Equal(3m, detail.UnitCost);
        Assert.Equal(2m, detail.DiffQuantity);
        Assert.Equal(6m, detail.DiffAmount);
    }

    [Fact]
    public async Task 创建_多行中任一行越界_整单拒绝且零写入()
    {
        using var db = TestDbFactory.Create();
        var ctl = NewController(db);

        var error = await Assert.ThrowsAsync<BusinessException>(() => ctl.Create(Draft(
            (PlainProduct, "合法行", "PCS", 1m, 2m, 1m),
            (BaseUnitProduct, "越界行", "KG", 2m, 3m, 1m))));

        Assert.Equal(ErrorCodes.RuleConflict, error.Code);
        Assert.Contains("[KG]", error.Message);
        AssertRejectedWithoutSideEffects(db);
    }

    [Fact]
    public async Task 创建_商品基础单位为KG_等值单位受理_其它单位拒绝()
    {
        using var db = TestDbFactory.Create();
        var ctl = NewController(db);

        var accepted = CreatedId(await ctl.Create(Draft((KilogramProduct, "散装商品", " kg ", 5m, 6m, 1m))));

        Assert.Equal(" kg ", db.StockAdjustmentDetails.Single(d => d.StockAdjustmentId == accepted).Unit);
        Assert.Equal(5m, db.StockAdjustmentDetails.Single(d => d.StockAdjustmentId == accepted).BookQuantity);

        var error = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(Draft((KilogramProduct, "散装商品", "PCS", 5m, 6m, 1m))));
        Assert.Equal(ErrorCodes.RuleConflict, error.Code);
        Assert.Contains("不是当前基础单位", error.Message);

        Assert.Single(db.StockAdjustments);               // 被拒方不落任何新单据
        Assert.Equal(1, db.StockAdjustmentDetails.Count());
    }

    // ==================== 3. 修改：越界单位在明细整体替换之前收敛 ====================

    [Fact]
    public async Task 修改_越界单位_拒绝且既有单据明细与单号不变()
    {
        using var db = TestDbFactory.Create();
        var ctl = NewController(db);
        var id = CreatedId(await ctl.Create(Draft((BaseUnitProduct, "箱装商品", "PCS", 3m, 5m, 2m))));
        var adjustmentNo = db.StockAdjustments.Single(a => a.Id == id).AdjustmentNo;
        var numberRows = db.SysDocumentNumberRules.Count();

        var error = await Assert.ThrowsAsync<BusinessException>(() => ctl.Update(id,
            Draft((BaseUnitProduct, "箱装商品", "CTN", 9m, 4m, 2m))));

        Assert.Equal(ErrorCodes.RuleConflict, error.Code);
        Assert.Contains("不是当前基础单位", error.Message);

        var doc = db.StockAdjustments.Single(a => a.Id == id);
        Assert.Equal(adjustmentNo, doc.AdjustmentNo);
        Assert.Equal(DocumentStatus.Pending, doc.Status);
        Assert.Equal(2m, doc.TotalDiffQuantity);
        Assert.Equal(4m, doc.TotalDiffAmount);

        var detail = db.StockAdjustmentDetails.Single(d => d.StockAdjustmentId == id);
        Assert.Equal("PCS", detail.Unit);                 // 明细未被整体替换
        Assert.Equal(3m, detail.BookQuantity);
        Assert.Equal(5m, detail.ActualQuantity);
        Assert.Equal(2m, detail.UnitCost);

        Assert.Equal(numberRows, db.SysDocumentNumberRules.Count());
        Assert.Empty(db.StockMovements);
    }

    [Fact]
    public async Task 修改_基础单位大小写_受理并整体替换合计重算()
    {
        using var db = TestDbFactory.Create();
        var ctl = NewController(db);
        var id = CreatedId(await ctl.Create(Draft((BaseUnitProduct, "箱装商品", "PCS", 1m, 2m, 1m))));
        var adjustmentNo = db.StockAdjustments.Single(a => a.Id == id).AdjustmentNo;

        var result = await ctl.Update(id, Draft((BaseUnitProduct, "箱装商品", " pcs ", 4m, 5m, 5m)));

        Assert.IsType<OkObjectResult>(result);
        var doc = db.StockAdjustments.Single(a => a.Id == id);
        Assert.Equal(adjustmentNo, doc.AdjustmentNo);
        Assert.Equal(1m, doc.TotalDiffQuantity);
        Assert.Equal(5m, doc.TotalDiffAmount);
        var detail = db.StockAdjustmentDetails.Single(d => d.StockAdjustmentId == id);
        Assert.Equal(" pcs ", detail.Unit);
        Assert.Equal(4m, detail.BookQuantity);
        Assert.Equal(5m, detail.ActualQuantity);
    }

    // ==================== 4. 提交：锁内复核已存单位 ====================

    [Fact]
    public async Task 提交_主数据基础单位被改写_拒绝且状态库存流水单号不变()
    {
        using var db = TestDbFactory.Create();
        var ctl = NewController(db);
        var id = CreatedId(await ctl.Create(Draft((BaseUnitProduct, "箱装商品", "PCS", 6m, 4m, 2m))));
        var numberRows = db.SysDocumentNumberRules.Count();
        SetUnits(db, BaseUnitProduct, unit: "BOX");       // 草稿创建后主数据被改写

        var error = await Assert.ThrowsAsync<BusinessException>(() => ctl.Submit(id));

        Assert.Equal(ErrorCodes.RuleConflict, error.Code);
        Assert.Contains("不是当前基础单位", error.Message);
        Assert.Equal(DocumentStatus.Pending, db.StockAdjustments.Single(a => a.Id == id).Status);
        Assert.Equal("PCS", db.StockAdjustmentDetails.Single(d => d.StockAdjustmentId == id).Unit);
        Assert.Equal(numberRows, db.SysDocumentNumberRules.Count());
        Assert.Empty(db.StockMovements);
        Assert.Empty(db.Stocks);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task 提交_空单位_受理并流转为已提交(string unit)
    {
        using var db = TestDbFactory.Create();
        var ctl = NewController(db);
        var id = CreatedId(await ctl.Create(Draft((KilogramProduct, "散装商品", unit, 6m, 4m, 2m))));

        await ctl.Submit(id);

        Assert.Equal(DocumentStatus.Submitted, db.StockAdjustments.Single(a => a.Id == id).Status);
        Assert.Empty(db.StockMovements);
    }

    // ==================== 5. 审核：库存锁内复核 + 差异恰好一次 ====================

    [Fact]
    public async Task 审核_主数据基础单位被改写_拒绝且库存流水状态不变()
    {
        using var db = TestDbFactory.Create();
        SeedStock(db, WarehouseA, BaseUnitProduct, 100m, 1000m);
        var ctl = NewController(db);
        var id = CreatedId(await ctl.Create(Draft((BaseUnitProduct, "箱装商品", "PCS", 100m, 70m, 10m))));
        await ctl.Submit(id);
        SetUnits(db, BaseUnitProduct, unit: "BOX");      // 提交后、审核前主数据被改写

        var error = await Assert.ThrowsAsync<BusinessException>(() => ctl.Approve(id));

        Assert.Equal(ErrorCodes.RuleConflict, error.Code);
        Assert.Contains("不是当前基础单位", error.Message);

        Assert.Equal(DocumentStatus.Submitted, db.StockAdjustments.Single(a => a.Id == id).Status);
        var stock = db.Stocks.Single(s => s.WarehouseId == WarehouseA && s.ProductId == BaseUnitProduct);
        Assert.Equal(100m, stock.Quantity);
        Assert.Equal(1000m, stock.TotalCost);
        Assert.Empty(db.StockMovements);

        var detail = db.StockAdjustmentDetails.Single(d => d.StockAdjustmentId == id);
        Assert.Equal("PCS", detail.Unit);                        // 未被 product.Unit 静默覆盖
        Assert.Equal(100m, detail.BookQuantity);
        Assert.Equal(70m, detail.ActualQuantity);
        Assert.Equal(10m, detail.UnitCost);
    }

    [Fact]
    public async Task 审核_历史越界单位草稿_拒绝且零记账_历史读取保留()
    {
        using var db = TestDbFactory.Create();
        SeedStock(db, WarehouseA, BaseUnitProduct, 100m, 1000m);
        var ctl = NewController(db);
        var id = SeedLegacySubmittedAdjustment(db, unit: "CTN", bookQuantity: 100m, actualQuantity: 88m,
            unitCost: 5m);

        var error = await Assert.ThrowsAsync<BusinessException>(() => ctl.Approve(id));

        Assert.Equal(ErrorCodes.RuleConflict, error.Code);
        Assert.Contains("不是当前基础单位", error.Message);
        Assert.Equal(DocumentStatus.Submitted, db.StockAdjustments.Single(a => a.Id == id).Status);
        Assert.Equal(100m, db.Stocks.Single(s => s.WarehouseId == WarehouseA).Quantity);
        Assert.Empty(db.StockMovements);

        // 历史读取不新增资格闸门：已存单位原样可读，绝不被静默改写。
        var read = await ctl.GetById(id);
        var payload = Assert.IsType<ApiResponse<StockAdjustment>>(
            Assert.IsType<OkObjectResult>(read).Value).Data!;
        Assert.Equal("CTN", payload.Details.Single().Unit);
    }

    [Theory]
    [InlineData("PCS")]
    [InlineData(" pcs ")]
    [InlineData("")]
    public async Task 审核_基础单位或空单位_恰好一次记账_差异与单位归一(string unit)
    {
        using var db = TestDbFactory.Create();
        SeedStock(db, WarehouseA, BaseUnitProduct, 100m, 1000m);
        var ctl = NewController(db);
        var id = CreatedId(await ctl.Create(Draft((BaseUnitProduct, "箱装商品", unit, 100m, 70m, 10m))));
        await ctl.Submit(id);

        await ctl.Approve(id);

        var stock = db.Stocks.Single(s => s.WarehouseId == WarehouseA && s.ProductId == BaseUnitProduct);
        Assert.Equal(70m, stock.Quantity);                        // 差异 -30 恰好一次
        Assert.Equal(700m, stock.TotalCost);
        Assert.Equal(DocumentStatus.Approved, db.StockAdjustments.Single(a => a.Id == id).Status);
        Assert.Equal(1, db.StockMovements.Count());

        var detail = db.StockAdjustmentDetails.Single(d => d.StockAdjustmentId == id);
        // 审核沿用既有 ResolveProductAsync 语义：空 / 空白单位按商品基础单位兜底，已校验的基础单位（含大小写 / 空白差异）原样保留。
        Assert.True(StockAdjustmentPostingRules.IsBaseUnitEligible(detail.Unit, "PCS"));
        if (string.IsNullOrWhiteSpace(unit))
            Assert.Equal("PCS", detail.Unit);
        else
            Assert.Equal(unit, detail.Unit);
        Assert.Equal(100m, detail.BookQuantity);                  // 账面 / 实盘数量与成本绝不被改写
        Assert.Equal(70m, detail.ActualQuantity);
        Assert.Equal(10m, detail.UnitCost);
        Assert.Equal(-30m, detail.DiffQuantity);

        var again = await Assert.ThrowsAsync<BusinessException>(() => ctl.Approve(id));
        Assert.Equal(ErrorCodes.RuleConflict, again.Code);
        Assert.Equal(1, db.StockMovements.Count());               // 不重复过账
    }

    [Theory]
    [InlineData(100, 60, -40, 600)]   // 盘亏
    [InlineData(0, 5, 5, 50)]         // 盘盈（账面为零）
    [InlineData(0, 0, 0, 0)]          // 零差异：不记账
    public async Task 审核_盘盈盘亏与零差异口径不变(decimal current, decimal actual, decimal expectedDiff,
        decimal expectedCost)
    {
        using var db = TestDbFactory.Create();
        SeedStock(db, WarehouseA, BaseUnitProduct, current, current * 10m);
        var ctl = NewController(db);
        var id = CreatedId(await ctl.Create(Draft((BaseUnitProduct, "箱装商品", "PCS", current, actual, 10m))));
        await ctl.Submit(id);

        await ctl.Approve(id);

        var stock = db.Stocks.Single(s => s.WarehouseId == WarehouseA && s.ProductId == BaseUnitProduct);
        Assert.Equal(current + expectedDiff, stock.Quantity);
        Assert.Equal(expectedCost, stock.TotalCost);
        Assert.Equal(DocumentStatus.Approved, db.StockAdjustments.Single(a => a.Id == id).Status);
        Assert.Equal(expectedDiff == 0m ? 0 : 1, db.StockMovements.Count());

        var detail = db.StockAdjustmentDetails.Single(d => d.StockAdjustmentId == id);
        Assert.Equal(expectedDiff, detail.DiffQuantity);          // 有符号差异口径不变
        Assert.Equal("PCS", detail.Unit);
    }

    [Fact]
    public async Task 审核后主数据基础单位被改写_销审仍可冲销还原_不新增资格闸门()
    {
        using var db = TestDbFactory.Create();
        SeedStock(db, WarehouseA, BaseUnitProduct, 100m, 1000m);
        var ctl = NewController(db);
        var id = CreatedId(await ctl.Create(Draft((BaseUnitProduct, "箱装商品", "PCS", 100m, 60m, 10m))));
        await ctl.Submit(id);
        await ctl.Approve(id);
        SetUnits(db, BaseUnitProduct, unit: "BOX");

        await ctl.Unaudit(id);

        var stock = db.Stocks.Single(s => s.WarehouseId == WarehouseA && s.ProductId == BaseUnitProduct);
        Assert.Equal(100m, stock.Quantity);
        Assert.Equal(1000m, stock.TotalCost);
        Assert.Equal(2, db.StockMovements.Count());               // 1 条原流水 + 1 条红字冲销
        Assert.Equal(1, db.StockMovements.Count(m => m.IsReversal));
        Assert.Equal(DocumentStatus.Pending, db.StockAdjustments.Single(a => a.Id == id).Status);
    }

    // ==================== 6. 源码契约：先校验，再编号 / 记账 ====================

    [Fact]
    public void 控制器与规则源码契约_单位资格先于编号与库存记账()
    {
        var controller = ReadSource("ERP.Api/Controllers/StockAdjustmentController.cs");
        var rules = ReadSource("ERP.Application/Services/StockAdjustmentPostingRules.cs");

        Assert.Contains("StockAdjustmentPostingRules.EnsureLiveMasterDataAsync", controller);
        Assert.Contains("StockAdjustmentPostingRules.EnsureStoredBaseUnitsEligibleAsync", controller);

        var createStart = controller.IndexOf("public async Task<IActionResult> Create", StringComparison.Ordinal);
        var updateStart = controller.IndexOf("public async Task<IActionResult> Update", StringComparison.Ordinal);
        var approveStart = controller.IndexOf("public override async Task<IActionResult> Approve",
            StringComparison.Ordinal);
        var submitStart = controller.IndexOf("public override async Task<IActionResult> Submit",
            StringComparison.Ordinal);
        Assert.True(createStart >= 0 && updateStart > createStart && approveStart > updateStart
                    && submitStart > approveStart);

        var create = controller[createStart..updateStart];
        Assert.True(create.IndexOf("EnsureLiveMasterDataAsync", StringComparison.Ordinal)
                    < create.IndexOf("_noService.GenerateAsync", StringComparison.Ordinal));   // 拒绝方不消耗单号

        var update = controller[updateStart..approveStart];
        Assert.True(update.IndexOf("EnsureLiveMasterDataAsync", StringComparison.Ordinal)
                    < update.IndexOf("Db.StockAdjustmentDetails.RemoveRange", StringComparison.Ordinal)); // 校验先于明细替换

        var approve = controller[approveStart..submitStart];
        Assert.True(approve.IndexOf("LockAdjustmentStocksAsync", StringComparison.Ordinal)
                    < approve.IndexOf("EnsureStoredBaseUnitsEligibleAsync", StringComparison.Ordinal));  // 库存锁内复核
        Assert.True(approve.IndexOf("EnsureStoredBaseUnitsEligibleAsync", StringComparison.Ordinal)
                    < approve.IndexOf("IncreaseAsync", StringComparison.Ordinal));                       // 先复核再记账

        var submit = controller[submitStart..];
        Assert.True(submit.IndexOf("EnsureStoredBaseUnitsEligibleAsync", StringComparison.Ordinal)
                    < submit.IndexOf("SetStatus(entity, DocumentStatus.Submitted)", StringComparison.Ordinal));

        Assert.Contains("IsBaseUnitEligible", rules);
        Assert.Contains("EnsureBaseUnitsEligible", rules);
        Assert.Contains("BaseUnitEligibilityRuleText", rules);
        Assert.DoesNotContain("SaveChanges", rules);          // 纯判定 + 有界只读查询，护栏自身不落库
        Assert.Contains("AsNoTracking", rules);
    }

    // ==================== 测试辅助 ====================

    /// <summary>播种在用主数据（仓库 + 四个商品）并返回已授权的盘点控制器。</summary>
    private static StockAdjustmentController NewController(ErpDbContext db)
    {
        StockAdjustmentTestAuthorization.SeedMasterData(db, WarehouseA,
            BaseUnitProduct, KilogramProduct, NoBaseUnitProduct, PlainProduct);
        SetUnits(db, BaseUnitProduct, unit: "PCS", packageUnit: "CTN", unitsPerPackage: 12);
        SetUnits(db, KilogramProduct, unit: "KG");
        SetUnits(db, NoBaseUnitProduct, unit: "", packageUnit: "CTN", unitsPerPackage: 12);
        SetUnits(db, PlainProduct, unit: "PCS");
        return StockAdjustmentTestAuthorization.CreateAuthorized(db);
    }

    private static void SetUnits(ErpDbContext db, long productId, string unit, string packageUnit = "",
        int unitsPerPackage = 0)
    {
        var product = db.BaseProducts.Single(p => p.Id == productId);
        product.Unit = unit;
        product.PackageUnit = packageUnit;
        product.UnitsPerPackage = unitsPerPackage;
        db.SaveChanges();
    }

    private static BaseProduct Product(long id, string name, string unit)
        => new() { Id = id, ProductName = name, ProductCode = $"P-{id}", Unit = unit };

    private static StockAdjustment Draft(
        params (long? ProductId, string Name, string Unit, decimal Book, decimal Actual, decimal UnitCost)[] lines)
        => new()
        {
            AdjustmentDate = DateTime.Today,
            WarehouseId = WarehouseA,
            AdjustType = "盘点调整",
            Remark = "ERP-526_TEST",
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

    /// <summary>直接落库一张「已提交」的历史盘点单（绕过控制器），用于验证审核期复核与历史读取。</summary>
    private static long SeedLegacySubmittedAdjustment(ErpDbContext db, string unit, decimal bookQuantity,
        decimal actualQuantity, decimal unitCost)
    {
        var diff = actualQuantity - bookQuantity;
        var adjustment = new StockAdjustment
        {
            AdjustmentNo = $"PD-LEGACY-{Guid.NewGuid():N}"[..20],
            AdjustmentDate = DateTime.Today,
            WarehouseId = WarehouseA,
            AdjustType = "盘点调整",
            Status = DocumentStatus.Submitted,
            TotalDiffQuantity = diff,
            TotalDiffAmount = diff * unitCost
        };
        db.StockAdjustments.Add(adjustment);
        db.SaveChanges();

        db.StockAdjustmentDetails.Add(new StockAdjustmentDetail
        {
            StockAdjustmentId = adjustment.Id,
            AdjustmentNo = adjustment.AdjustmentNo,
            SortNo = 1,
            ProductId = BaseUnitProduct,
            ProductName = "历史商品",
            Spec = "规格A",
            Unit = unit,
            BookQuantity = bookQuantity,
            ActualQuantity = actualQuantity,
            DiffQuantity = diff,
            UnitCost = unitCost,
            DiffAmount = diff * unitCost
        });
        db.SaveChanges();
        return adjustment.Id;
    }

    private static long CreatedId(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var data = Assert.IsType<ApiResponse<object>>(ok.Value).Data!;
        return (long)data.GetType().GetProperty("Id")!.GetValue(data)!;
    }

    /// <summary>拒绝场景的统一断言：零单据、零明细、零库存流水、零单号消耗。</summary>
    private static void AssertRejectedWithoutSideEffects(ErpDbContext db)
    {
        Assert.Empty(db.StockAdjustments);
        Assert.Empty(db.StockAdjustmentDetails);
        Assert.Empty(db.StockMovements);
        Assert.Empty(db.SysDocumentNumberRules);
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

    private static string RepoRoot()
        => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    private static string ReadSource(string relativePath)
        => File.ReadAllText(Path.Combine(RepoRoot(), "src",
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
}
