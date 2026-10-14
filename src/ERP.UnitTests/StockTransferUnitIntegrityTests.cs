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
/// ERP-525 仓库调拨单位完整性单元测试（内存库 + 真实 HTTP 身份 + 真实 <see cref="StockTransferController"/>）：
/// <list type="number">
/// <item>创建 / 修改在单号预约与明细整体替换<b>之前</b>拒绝显式提供的非空单位中不属于商品当前基础单位者
/// （<b>装箱单位也被拒绝</b> —— 本流程全程按基础单位计量、不实现任何包装折算，绝不改写数量 / 成本）；</item>
/// <item>保留空 / 空白单位回退、基础单位去首尾空白大小写不敏感受理与服务器侧身份 / 合计口径；</item>
/// <item>商品未维护基础单位时，显式非空单位受控拒绝（绝不臆造 PCS 之类新单位）；</item>
/// <item>提交 / 审核在既有单据 / 库存锁内用当前商品快照复核已存单位：主数据基础单位被改写即整体拒绝，
/// 状态 / 库存 / 流水 / 单号 / 明细逐字段不变；直接落库的历史越界单位草稿同样被审核拒绝且零记账；</item>
/// <item>销审 / 历史读取不新增资格闸门：基础单位在审核后被改写仍可正常冲销还原。</item>
/// </list>
/// <para>全部使用内存数据库，不连接 SQL Server、不触碰任何业务库数据。</para>
/// </summary>
public class StockTransferUnitIntegrityTests
{
    private const long WarehouseA = 950001L;
    private const long WarehouseB = 950002L;
    private const long BaseUnitProduct = 850001L;      // 基础 PCS + 装箱 CTN(12)：装箱单位也必须被拒绝
    private const long KilogramProduct = 850002L;      // 基础 KG：验证非 PCS 基础单位与大小写不敏感
    private const long NoBaseUnitProduct = 850003L;    // 只维护装箱单位、基础单位未维护
    private const long PlainProduct = 850004L;         // 仅基础 PCS（多行场景）

    // ==================== 1. 共享纯判定与文案 ====================

    [Theory]
    [InlineData("", "PCS", true)]              // 空单位：既有回退
    [InlineData("   ", "PCS", true)]           // 空白单位：既有回退
    [InlineData("PCS", "PCS", true)]           // 基础单位
    [InlineData("pcs", "PCS", true)]           // 大小写不敏感
    [InlineData(" pcs ", "PCS", true)]         // 首尾空白不敏感
    [InlineData("PCS", " pcs ", true)]         // 主数据侧空白同样收敛
    [InlineData("KG", "KG", true)]             // 非 PCS 基础单位
    [InlineData("CTN", "PCS", false)]          // 装箱单位：调拨不做包装折算 → 拒绝
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
        => Assert.Equal(expected, StockTransferPostingRules.IsBaseUnitEligible(lineUnit, baseUnit));

    [Fact]
    public void 调拨单位资格文案_与接口面板径同源_且明确不做包装折算()
    {
        Assert.Contains("基础单位", StockTransferPostingRules.BaseUnitEligibilityRuleText);
        Assert.Contains("装箱单位", StockTransferPostingRules.BaseUnitEligibilityRuleText);
        Assert.Contains("包装折算", StockTransferPostingRules.BaseUnitEligibilityRuleText);
        Assert.Contains("空单位", StockTransferPostingRules.BaseUnitEligibilityRuleText);
        Assert.Contains("受控拒绝", StockTransferPostingRules.BaseUnitEligibilityRuleText);

        // 总口径文案（RuleText）同步声明单位资格护栏，且不引入任意单位换算。
        Assert.Contains("基础单位", StockTransferPostingRules.RuleText);
        Assert.Contains("包装折算", StockTransferPostingRules.RuleText);
        Assert.Contains("已存明细单位", StockTransferPostingRules.RuleText);
    }

    [Fact]
    public void 批量复核_越界单位受控拒绝_缺失商品与无效商品Id跳过()
    {
        var products = new Dictionary<long, BaseProduct>
        {
            [BaseUnitProduct] = Product(BaseUnitProduct, "箱装商品", "PCS"),
            [NoBaseUnitProduct] = Product(NoBaseUnitProduct, "缺基础单位商品", "")
        };

        StockTransferPostingRules.EnsureBaseUnitsEligible(products, new[]
        {
            new StockTransferDetail { ProductId = BaseUnitProduct, Unit = " pcs ", Quantity = 1m },
            new StockTransferDetail { ProductId = BaseUnitProduct, Unit = "", Quantity = 1m },
            new StockTransferDetail { ProductId = 999_999L, Unit = "KG", Quantity = 1m },   // 商品缺失：既有护栏负责
            new StockTransferDetail { ProductId = 0L, Unit = "KG", Quantity = 1m }          // 无效 Id：跳过
        });

        var packageError = Assert.Throws<BusinessException>(() =>
            StockTransferPostingRules.EnsureBaseUnitsEligible(products, new[]
            {
                new StockTransferDetail { ProductId = BaseUnitProduct, Unit = "CTN", Quantity = 1m }
            }));
        Assert.Equal(ErrorCodes.RuleConflict, packageError.Code);
        Assert.Contains("不是当前基础单位", packageError.Message);
        Assert.Contains("[CTN]", packageError.Message);

        var missingBaseError = Assert.Throws<BusinessException>(() =>
            StockTransferPostingRules.EnsureBaseUnitsEligible(products, new[]
            {
                new StockTransferDetail { ProductId = NoBaseUnitProduct, Unit = "PCS", Quantity = 1m }
            }));
        Assert.Equal(ErrorCodes.RuleConflict, missingBaseError.Code);
        Assert.Contains("未设置", missingBaseError.Message);
    }

    // ==================== 2. 创建：显式越界单位（零写入 / 零单号） ====================

    [Theory]
    [InlineData("CTN")]         // 商品自身的装箱单位：调拨不做折算 → 不折算也不记账
    [InlineData(" ctn ")]
    [InlineData("KG")]
    [InlineData("SET")]
    [InlineData("工程量")]
    public async Task 创建_非基础单位_拒绝且零单据零明细零流水零单号(string unit)
    {
        using var db = TestDbFactory.Create();
        var (ctl, _) = NewController(db);

        var error = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(Draft(WarehouseA, WarehouseB, (BaseUnitProduct, "箱装商品", unit, 12m, 10m))));

        Assert.Equal(ErrorCodes.RuleConflict, error.Code);
        Assert.Contains("不是当前基础单位", error.Message);
        AssertRejectedWithoutSideEffects(db);
    }

    [Fact]
    public async Task 创建_商品未维护基础单位_显式非空单位拒绝且零副作用()
    {
        using var db = TestDbFactory.Create();
        var (ctl, _) = NewController(db);

        var error = await Assert.ThrowsAsync<BusinessException>(() =>
            ctl.Create(Draft(WarehouseA, WarehouseB, (NoBaseUnitProduct, "缺基础单位商品", "PCS", 1m, 1m))));

        Assert.Equal(ErrorCodes.RuleConflict, error.Code);
        Assert.Contains("未设置", error.Message);
        AssertRejectedWithoutSideEffects(db);
    }

    [Theory]
    [InlineData("PCS")]
    [InlineData("pcs")]
    [InlineData(" pcs ")]
    public async Task 创建_基础单位大小写与首尾空白_受理且数量成本原样(string unit)
    {
        using var db = TestDbFactory.Create();
        var (ctl, _) = NewController(db);

        var id = CreatedId(await ctl.Create(Draft(WarehouseA, WarehouseB,
            (BaseUnitProduct, "箱装商品", unit, 12m, 2.5m))));

        var doc = db.StockTransfers.Single(t => t.Id == id);
        Assert.Equal(DocumentStatus.Pending, doc.Status);
        Assert.Equal(12m, doc.TotalQuantity);
        Assert.Equal(30m, doc.TotalAmount);

        var detail = db.StockTransferDetails.Single(d => d.StockTransferId == id);
        Assert.Equal(unit, detail.Unit);                    // 服务器不臆造单位：保留调用方口径
        Assert.Equal(12m, detail.Quantity);                 // 不做任何装箱折算
        Assert.Equal(2.5m, detail.UnitCost);
        Assert.Empty(db.StockMovements);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task 创建_空单位_受理且保留既有回退(string unit)
    {
        using var db = TestDbFactory.Create();
        var (ctl, _) = NewController(db);

        var id = CreatedId(await ctl.Create(Draft(WarehouseA, WarehouseB,
            (KilogramProduct, "散装商品", unit, 7m, 3m))));

        var detail = db.StockTransferDetails.Single(d => d.StockTransferId == id);
        Assert.True(string.IsNullOrWhiteSpace(detail.Unit));   // 空单位保持既有回退，不被臆造为基础单位
        Assert.Equal(7m, detail.Quantity);
        Assert.Equal(3m, detail.UnitCost);
    }

    [Fact]
    public async Task 创建_多行中任一行越界_整单拒绝且零写入()
    {
        using var db = TestDbFactory.Create();
        var (ctl, _) = NewController(db);

        var error = await Assert.ThrowsAsync<BusinessException>(() => ctl.Create(Draft(WarehouseA, WarehouseB,
            (PlainProduct, "合法行", "PCS", 1m, 1m),
            (BaseUnitProduct, "越界行", "KG", 2m, 1m))));

        Assert.Equal(ErrorCodes.RuleConflict, error.Code);
        Assert.Contains("[KG]", error.Message);
        AssertRejectedWithoutSideEffects(db);
    }

    [Fact]
    public async Task 创建_商品基础单位为KG_等值单位受理_其它单位拒绝()
    {
        using var db = TestDbFactory.Create();
        var (ctl, _) = NewController(db);

        var accepted = CreatedId(await ctl.Create(Draft(WarehouseA, WarehouseB,
            (KilogramProduct, "散装商品", " kg ", 5m, 1m))));

        var acceptedDetail = db.StockTransferDetails.Single(d => d.StockTransferId == accepted);
        Assert.Equal(" kg ", acceptedDetail.Unit);
        Assert.Equal(5m, acceptedDetail.Quantity);

        var error = await Assert.ThrowsAsync<BusinessException>(() => ctl.Create(Draft(WarehouseA, WarehouseB,
            (KilogramProduct, "散装商品", "PCS", 5m, 1m))));
        Assert.Equal(ErrorCodes.RuleConflict, error.Code);
        Assert.Contains("不是当前基础单位", error.Message);

        Assert.Single(db.StockTransfers);                 // 被拒方不落任何新单据
        Assert.Equal(1, db.StockTransferDetails.Count());
    }

    // ==================== 3. 修改：越界单位在明细整体替换之前收敛 ====================

    [Fact]
    public async Task 修改_越界单位_拒绝且既有单据明细与单号不变()
    {
        using var db = TestDbFactory.Create();
        var (ctl, _) = NewController(db);
        var id = CreatedId(await ctl.Create(Draft(WarehouseA, WarehouseB,
            (BaseUnitProduct, "箱装商品", "PCS", 3m, 2m))));
        var transferNo = db.StockTransfers.Single(t => t.Id == id).TransferNo;
        var numberRows = db.SysDocumentNumberRules.Count();

        var error = await Assert.ThrowsAsync<BusinessException>(() => ctl.Update(id,
            Draft(WarehouseA, WarehouseB, (BaseUnitProduct, "箱装商品", "CTN", 9m, 2m))));

        Assert.Equal(ErrorCodes.RuleConflict, error.Code);
        Assert.Contains("不是当前基础单位", error.Message);

        var doc = db.StockTransfers.Single(t => t.Id == id);
        Assert.Equal(transferNo, doc.TransferNo);
        Assert.Equal(DocumentStatus.Pending, doc.Status);
        Assert.Equal(3m, doc.TotalQuantity);
        Assert.Equal(6m, doc.TotalAmount);

        var detail = db.StockTransferDetails.Single(d => d.StockTransferId == id);
        Assert.Equal("PCS", detail.Unit);                 // 明细未被整体替换
        Assert.Equal(3m, detail.Quantity);
        Assert.Equal(2m, detail.UnitCost);

        Assert.Equal(numberRows, db.SysDocumentNumberRules.Count());
        Assert.Empty(db.StockMovements);
    }

    [Fact]
    public async Task 修改_基础单位大小写_受理并整体替换合计重算()
    {
        using var db = TestDbFactory.Create();
        var (ctl, _) = NewController(db);
        var id = CreatedId(await ctl.Create(Draft(WarehouseA, WarehouseB,
            (BaseUnitProduct, "箱装商品", "PCS", 1m, 1m))));
        var transferNo = db.StockTransfers.Single(t => t.Id == id).TransferNo;

        var result = await ctl.Update(id, Draft(WarehouseA, WarehouseB,
            (BaseUnitProduct, "箱装商品", " pcs ", 4m, 5m)));

        Assert.IsType<OkObjectResult>(result);
        var doc = db.StockTransfers.Single(t => t.Id == id);
        Assert.Equal(transferNo, doc.TransferNo);
        Assert.Equal(4m, doc.TotalQuantity);
        Assert.Equal(20m, doc.TotalAmount);
        var detail = db.StockTransferDetails.Single(d => d.StockTransferId == id);
        Assert.Equal(" pcs ", detail.Unit);
        Assert.Equal(4m, detail.Quantity);
    }

    // ==================== 4. 提交：锁内复核已存单位 ====================

    [Fact]
    public async Task 提交_主数据基础单位被改写_拒绝且状态库存流水单号不变()
    {
        using var db = TestDbFactory.Create();
        var (ctl, _) = NewController(db);
        var id = CreatedId(await ctl.Create(Draft(WarehouseA, WarehouseB,
            (BaseUnitProduct, "箱装商品", "PCS", 6m, 2m))));
        var numberRows = db.SysDocumentNumberRules.Count();
        SetUnits(db, BaseUnitProduct, unit: "BOX");       // 草稿创建后主数据被改写

        var error = await Assert.ThrowsAsync<BusinessException>(() => ctl.Submit(id));

        Assert.Equal(ErrorCodes.RuleConflict, error.Code);
        Assert.Contains("不是当前基础单位", error.Message);
        Assert.Equal(DocumentStatus.Pending, db.StockTransfers.Single(t => t.Id == id).Status);
        Assert.Equal("PCS", db.StockTransferDetails.Single(d => d.StockTransferId == id).Unit);
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
        var (ctl, _) = NewController(db);
        var id = CreatedId(await ctl.Create(Draft(WarehouseA, WarehouseB,
            (KilogramProduct, "散装商品", unit, 6m, 2m))));

        await ctl.Submit(id);

        Assert.Equal(DocumentStatus.Submitted, db.StockTransfers.Single(t => t.Id == id).Status);
        Assert.Empty(db.StockMovements);
    }

    // ==================== 5. 审核：库存锁内复核 + 两仓守恒 ====================

    [Fact]
    public async Task 审核_主数据基础单位被改写_拒绝且两仓余额流水状态不变()
    {
        using var db = TestDbFactory.Create();
        SeedStock(db, WarehouseA, BaseUnitProduct, 100m, 1000m);
        var (ctl, _) = NewController(db);
        var id = CreatedId(await ctl.Create(Draft(WarehouseA, WarehouseB,
            (BaseUnitProduct, "箱装商品", "PCS", 30m, 0m))));
        await ctl.Submit(id);
        SetUnits(db, BaseUnitProduct, unit: "BOX");      // 提交后、审核前主数据被改写

        var error = await Assert.ThrowsAsync<BusinessException>(() => ctl.Approve(id));

        Assert.Equal(ErrorCodes.RuleConflict, error.Code);
        Assert.Contains("不是当前基础单位", error.Message);

        Assert.Equal(DocumentStatus.Submitted, db.StockTransfers.Single(t => t.Id == id).Status);
        var from = db.Stocks.Single(s => s.WarehouseId == WarehouseA && s.ProductId == BaseUnitProduct);
        Assert.Equal(100m, from.Quantity);
        Assert.Equal(1000m, from.TotalCost);
        Assert.DoesNotContain(db.Stocks, s => s.WarehouseId == WarehouseB);
        Assert.Empty(db.StockMovements);

        var detail = db.StockTransferDetails.Single(d => d.StockTransferId == id);
        Assert.Equal("PCS", detail.Unit);                        // 未被 product.Unit 静默覆盖
        Assert.Equal(30m, detail.Quantity);
        Assert.Equal(0m, detail.UnitCost);
    }

    [Fact]
    public async Task 审核_历史越界单位草稿_拒绝且零记账_历史读取保留()
    {
        using var db = TestDbFactory.Create();
        SeedStock(db, WarehouseA, BaseUnitProduct, 100m, 1000m);
        var (ctl, _) = NewController(db);
        var id = SeedLegacySubmittedTransfer(db, unit: "CTN", quantity: 5m, unitCost: 10m);

        var error = await Assert.ThrowsAsync<BusinessException>(() => ctl.Approve(id));

        Assert.Equal(ErrorCodes.RuleConflict, error.Code);
        Assert.Contains("不是当前基础单位", error.Message);
        Assert.Equal(DocumentStatus.Submitted, db.StockTransfers.Single(t => t.Id == id).Status);
        Assert.Equal(100m, db.Stocks.Single(s => s.WarehouseId == WarehouseA).Quantity);
        Assert.Empty(db.StockMovements);

        // 历史读取不新增资格闸门：已存单位原样可读，绝不被静默改写。
        var read = await ctl.GetById(id);
        var payload = Assert.IsType<ApiResponse<StockTransfer>>(
            Assert.IsType<OkObjectResult>(read).Value).Data!;
        Assert.Equal("CTN", payload.Details.Single().Unit);
    }

    [Theory]
    [InlineData("PCS")]
    [InlineData(" pcs ")]
    [InlineData("")]
    public async Task 审核_基础单位或空单位_恰好一次记账_两仓守恒_单位归一(string unit)
    {
        using var db = TestDbFactory.Create();
        SeedStock(db, WarehouseA, BaseUnitProduct, 100m, 1000m);
        var (ctl, _) = NewController(db);
        var id = CreatedId(await ctl.Create(Draft(WarehouseA, WarehouseB,
            (BaseUnitProduct, "箱装商品", unit, 30m, 0m))));
        await ctl.Submit(id);

        await ctl.Approve(id);

        var from = db.Stocks.Single(s => s.WarehouseId == WarehouseA && s.ProductId == BaseUnitProduct);
        var to = db.Stocks.Single(s => s.WarehouseId == WarehouseB && s.ProductId == BaseUnitProduct);
        Assert.Equal(70m, from.Quantity);
        Assert.Equal(700m, from.TotalCost);
        Assert.Equal(30m, to.Quantity);
        Assert.Equal(300m, to.TotalCost);
        Assert.Equal(100m, from.Quantity + to.Quantity);          // 数量守恒
        Assert.Equal(1000m, from.TotalCost + to.TotalCost);       // 金额守恒
        Assert.Equal(10m, to.AverageCost);                        // 两侧同一成本单价
        Assert.Equal(2, db.StockMovements.Count());
        Assert.Equal(DocumentStatus.Approved, db.StockTransfers.Single(t => t.Id == id).Status);

        var detail = db.StockTransferDetails.Single(d => d.StockTransferId == id);
        Assert.True(StockTransferPostingRules.IsBaseUnitEligible(detail.Unit, "PCS"));  // 落库单位仍是基础单位口径
        if (string.IsNullOrWhiteSpace(unit))
            Assert.Equal("PCS", detail.Unit);                 // 空单位按商品基础单位兜底（既有回退）
        else
            Assert.Equal(unit, detail.Unit);                  // 非空已校验单位保持原样，不被隐式改写
        Assert.Equal(30m, detail.Quantity);
        Assert.Equal(10m, detail.UnitCost);
    }

    [Fact]
    public async Task 审核后主数据基础单位被改写_销审仍可冲销还原_不新增资格闸门()
    {
        using var db = TestDbFactory.Create();
        SeedStock(db, WarehouseA, BaseUnitProduct, 100m, 1000m);
        var (ctl, _) = NewController(db);
        var id = CreatedId(await ctl.Create(Draft(WarehouseA, WarehouseB,
            (BaseUnitProduct, "箱装商品", "PCS", 40m, 10m))));
        await ctl.Submit(id);
        await ctl.Approve(id);
        SetUnits(db, BaseUnitProduct, unit: "BOX");

        await ctl.Unaudit(id);

        var from = db.Stocks.Single(s => s.WarehouseId == WarehouseA && s.ProductId == BaseUnitProduct);
        var to = db.Stocks.Single(s => s.WarehouseId == WarehouseB && s.ProductId == BaseUnitProduct);
        Assert.Equal(100m, from.Quantity);
        Assert.Equal(1000m, from.TotalCost);
        Assert.Equal(0m, to.Quantity);
        Assert.Equal(0m, to.TotalCost);
        Assert.Equal(4, db.StockMovements.Count());               // 2 条原流水 + 2 条红字冲销
        Assert.Equal(2, db.StockMovements.Count(m => m.IsReversal));
        Assert.Equal(DocumentStatus.Pending, db.StockTransfers.Single(t => t.Id == id).Status);
    }

    // ==================== 6. 源码契约：先校验，再编号 / 记账 ====================

    [Fact]
    public void 控制器与规则源码契约_单位资格先于编号与库存记账()
    {
        var controller = ReadSource("ERP.Api/Controllers/StockTransferController.cs");
        var rules = ReadSource("ERP.Application/Services/StockTransferPostingRules.cs");

        Assert.Contains("StockTransferPostingRules.EnsureLiveMasterDataAsync", controller);
        Assert.Contains("StockTransferPostingRules.EnsureStoredBaseUnitsEligibleAsync", controller);

        var createStart = controller.IndexOf("public async Task<IActionResult> Create", StringComparison.Ordinal);
        var updateStart = controller.IndexOf("public async Task<IActionResult> Update", StringComparison.Ordinal);
        var submitStart = controller.IndexOf("public override async Task<IActionResult> Submit", StringComparison.Ordinal);
        var approveStart = controller.IndexOf("public override async Task<IActionResult> Approve", StringComparison.Ordinal);
        Assert.True(createStart >= 0 && updateStart > createStart && approveStart > updateStart
                    && submitStart > approveStart);

        var create = controller[createStart..updateStart];
        Assert.True(create.IndexOf("EnsureLiveMasterDataAsync", StringComparison.Ordinal)
                    < create.IndexOf("_noService.GenerateAsync", StringComparison.Ordinal));   // 拒绝方不消耗单号

        var update = controller[updateStart..approveStart];
        Assert.True(update.IndexOf("EnsureLiveMasterDataAsync", StringComparison.Ordinal)
                    < update.IndexOf("Db.StockTransferDetails.RemoveRange", StringComparison.Ordinal));  // 校验先于明细替换

        var submit = controller[submitStart..];
        Assert.True(submit.IndexOf("EnsureStoredBaseUnitsEligibleAsync", StringComparison.Ordinal)
                    < submit.IndexOf("SetStatus(entity, DocumentStatus.Submitted)", StringComparison.Ordinal));

        var approve = controller[approveStart..submitStart];
        Assert.True(approve.IndexOf("LockTransferStocksAsync", StringComparison.Ordinal)
                    < approve.IndexOf("EnsureStoredBaseUnitsEligibleAsync", StringComparison.Ordinal));  // 库存锁内复核
        Assert.True(approve.IndexOf("EnsureStoredBaseUnitsEligibleAsync", StringComparison.Ordinal)
                    < approve.IndexOf("DecreaseAsync", StringComparison.Ordinal));                        // 先复核再记账

        Assert.Contains("IsBaseUnitEligible", rules);
        Assert.Contains("EnsureBaseUnitsEligible", rules);
        Assert.Contains("BaseUnitEligibilityRuleText", rules);
        Assert.DoesNotContain("SaveChanges", rules);          // 纯判定 + 有界只读查询，护栏自身不落库
        Assert.Contains("AsNoTracking", rules);
    }

    // ==================== 测试辅助 ====================

    /// <summary>播种在用主数据（两侧仓库 + 四个商品）并返回已授权控制器与授权用户。</summary>
    private static (StockTransferController Controller, long UserId) NewController(ErpDbContext db)
    {
        StockTransferTestAuthorization.SeedMasterData(db, WarehouseA, WarehouseB,
            BaseUnitProduct, KilogramProduct, NoBaseUnitProduct, PlainProduct);
        SetUnits(db, BaseUnitProduct, unit: "PCS", packageUnit: "CTN", unitsPerPackage: 12);
        SetUnits(db, KilogramProduct, unit: "KG");
        SetUnits(db, NoBaseUnitProduct, unit: "", packageUnit: "CTN", unitsPerPackage: 12);
        SetUnits(db, PlainProduct, unit: "PCS");

        var userId = StockTransferTestAuthorization.SeedAuthorizedOperator(db);
        return (StockTransferTestAuthorization.ForUser(db, userId), userId);
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

    private static StockTransfer Draft(long fromWarehouseId, long toWarehouseId,
        params (long? ProductId, string Name, string Unit, decimal Quantity, decimal UnitCost)[] lines)
        => new()
        {
            TransferDate = DateTime.Today,
            FromWarehouseId = fromWarehouseId,
            ToWarehouseId = toWarehouseId,
            Remark = "ERP-525_TEST",
            Details = lines.Select(l => new StockTransferDetail
            {
                ProductId = l.ProductId,
                ProductName = l.Name,
                Spec = "规格A",
                Unit = l.Unit,
                Quantity = l.Quantity,
                UnitCost = l.UnitCost
            }).ToList()
        };

    /// <summary>直接落库一张「已提交」的历史调拨单（绕过控制器），用于验证审核期复核与历史读取。</summary>
    private static long SeedLegacySubmittedTransfer(ErpDbContext db, string unit, decimal quantity,
        decimal unitCost)
    {
        var transfer = new StockTransfer
        {
            TransferNo = $"ST-LEGACY-{Guid.NewGuid():N}"[..20],
            TransferDate = DateTime.Today,
            FromWarehouseId = WarehouseA,
            ToWarehouseId = WarehouseB,
            Status = DocumentStatus.Submitted,
            TotalQuantity = quantity,
            TotalAmount = quantity * unitCost
        };
        db.StockTransfers.Add(transfer);
        db.SaveChanges();

        db.StockTransferDetails.Add(new StockTransferDetail
        {
            StockTransferId = transfer.Id,
            TransferNo = transfer.TransferNo,
            SortNo = 1,
            ProductId = BaseUnitProduct,
            ProductName = "历史商品",
            Spec = "规格A",
            Unit = unit,
            Quantity = quantity,
            UnitCost = unitCost,
            Amount = quantity * unitCost
        });
        db.SaveChanges();
        return transfer.Id;
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
        Assert.Empty(db.StockTransfers);
        Assert.Empty(db.StockTransferDetails);
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
