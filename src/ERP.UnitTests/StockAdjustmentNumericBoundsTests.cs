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
/// ERP-507 库存盘点单数值边界护栏单元测试（内存库 + 真实 HTTP 身份）：
/// <list type="number">
/// <item>创建 / 修改在单号预约与任何表头 / 明细写入之前拒绝「账面 / 实盘数量为负或超出 <c>DECIMAL(18,4)</c>、
/// 成本单价为负或超出 <c>DECIMAL(18,6)</c>、差异数量 × 成本单价的乘法溢出、差异合计溢出」（与实际 EF 精度同源）；</item>
/// <item>客户端 <c>IsDeleted=true</c> 不能隐藏坏行（服务端会把每一行都恢复为正常行并纳入校验），
/// 有效隐藏行照常持久化为正常行并计入合计；</item>
/// <item>保留零账面 / 零实盘 / 零成本、有符号盘盈盘亏与原有重复商品禁令；空草稿头仍受支持；</item>
/// <item>提交在锁内复核当前生效已存明细，拒绝且状态 / 库存 / 流水 / 单号序列全部不变；</item>
/// <item>合法盘点单可如实审核（盘盈 +、盘亏 −）并写入库存与流水；</item>
/// <item>实际 EF 列精度与规则常量同源，控制器源码契约保证「先校验再消耗单号 / 替换明细」。</item>
/// </list>
/// 说明：全部使用内存数据库；只有「实际 EF 精度」用例以 SQL Server 提供程序<b>仅构建模型</b>（不连接数据库）。
/// </summary>
public class StockAdjustmentNumericBoundsTests
{
    private const long WarehouseA = 940001L;
    private const long Product1 = 840001L;
    private const long Product2 = 840002L;

    // ==================== 1. 创建：不可表示 / 溢出数值（零写入 / 零单号） ====================

    [Theory]
    [InlineData("negative-book")]
    [InlineData("negative-actual")]
    [InlineData("negative-cost")]
    [InlineData("unrepresentable-book")]        // 5 位小数：DECIMAL(18,4) 不可表示
    [InlineData("unrepresentable-actual")]
    [InlineData("unrepresentable-cost")]        // 7 位小数：DECIMAL(18,6) 不可表示
    [InlineData("overflow-book")]               // 超出 DECIMAL(18,4) 上界
    [InlineData("overflow-actual")]
    [InlineData("overflow-cost")]               // 超出 DECIMAL(18,6) 上界
    [InlineData("overflow-product")]            // 差异数量 × 成本单价 超出 DECIMAL(18,4)
    public async Task 创建_数值不可表示_拒绝且零单据零明细零单号(string scenario)
    {
        using var db = TestDbFactory.Create();
        var ctl = Authorized(db);
        SeedNumberRule(db);

        var line = scenario switch
        {
            "negative-book" => Line(Product1, -1m, 5m, 1m),
            "negative-actual" => Line(Product1, 5m, -1m, 1m),
            "negative-cost" => Line(Product1, 5m, 5m, -1m),
            "unrepresentable-book" => Line(Product1, 1.00001m, 1m, 1m),
            "unrepresentable-actual" => Line(Product1, 1m, 1.00001m, 1m),
            "unrepresentable-cost" => Line(Product1, 1m, 1m, 0.0000001m),
            "overflow-book" => Line(Product1, 100_000_000_000_000m, 1m, 1m),
            "overflow-actual" => Line(Product1, 1m, 100_000_000_000_000m, 1m),
            "overflow-cost" => Line(Product1, 1m, 1m, 1_000_000_000_000m),
            _ => Line(Product1, 0m, 10_000_000_000m, 10_000_000_000m)   // 1e10 × 1e10 = 1e20 溢出
        };

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Create(Draft(line)));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        AssertRejectedWithNoSideEffects(db);
    }

    [Theory]
    [InlineData("total-quantity")]     // 两行各自可表示，合计数量溢出
    [InlineData("total-amount")]       // 两行各自可表示，合计金额溢出
    public async Task 创建_表头差异合计不可表示_拒绝且零副作用(string scenario)
    {
        using var db = TestDbFactory.Create();
        var ctl = Authorized(db);
        SeedNumberRule(db);

        var (first, second) = scenario switch
        {
            // 每行差异数量 = DECIMAL(18,4) 上界，合计 = 2 倍上界 → 不可表示
            "total-quantity" => (
                Line(Product1, 0m, StockAdjustmentPostingRules.MaxDecimal18Scale4, 0m),
                Line(Product2, 0m, StockAdjustmentPostingRules.MaxDecimal18Scale4, 0m)),
            // 每行差异金额 8e13 可表示，合计 1.6e14 超出 DECIMAL(18,4)
            _ => (
                Line(Product1, 0m, 40_000_000_000_000m, 2m),
                Line(Product2, 0m, 40_000_000_000_000m, 2m))
        };

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Create(Draft(first, second)));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        AssertRejectedWithNoSideEffects(db);
    }

    // ==================== 2. 创建：客户端删除态不能隐藏坏行 ====================

    [Theory]
    [InlineData("hidden-negative-book")]
    [InlineData("hidden-unrepresentable-actual")]
    [InlineData("hidden-overflow-cost")]
    [InlineData("hidden-missing-product")]
    [InlineData("hidden-duplicate-product")]
    public async Task 创建_客户端删除态隐藏坏行_仍然拒绝且零副作用(string scenario)
    {
        using var db = TestDbFactory.Create();
        var ctl = Authorized(db);
        SeedNumberRule(db);

        var visible = Line(Product1, 1m, 1m, 1m);
        var hidden = scenario switch
        {
            "hidden-negative-book" => Line(Product2, -5m, 1m, 1m, isDeleted: true),
            "hidden-unrepresentable-actual" => Line(Product2, 1m, 1.00001m, 1m, isDeleted: true),
            "hidden-overflow-cost" => Line(Product2, 1m, 1m, 1_000_000_000_000m, isDeleted: true),
            "hidden-missing-product" => Line(null, 1m, 1m, 1m, isDeleted: true),
            _ => Line(Product1, 2m, 3m, 1m, isDeleted: true)            // 与可见行同商品 → 重复
        };

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Create(Draft(visible, hidden)));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        AssertRejectedWithNoSideEffects(db);
    }

    [Fact]
    public async Task 创建_客户端删除态有效行_服务端恢复为正常行并计入合计()
    {
        using var db = TestDbFactory.Create();
        var ctl = Authorized(db);
        SeedNumberRule(db);

        var hidden = Line(Product2, 5m, 9m, 4m, isDeleted: true);
        hidden.Id = 424242L;
        hidden.StockAdjustmentId = 777777L;

        var result = await ctl.Create(Draft(Line(Product1, 10m, 8m, 12.5m), hidden));

        Assert.IsType<OkObjectResult>(result);
        var saved = db.StockAdjustments.Include(o => o.Details).Single();
        Assert.Equal(2m, saved.TotalDiffQuantity);                     // -2 + 4
        Assert.Equal(-9m, saved.TotalDiffAmount);                      // -2*12.5 + 4*4
        Assert.Equal(2, saved.Details.Count);
        Assert.All(saved.Details, d => Assert.False(d.IsDeleted));     // 删除态被服务端恢复
        Assert.All(saved.Details, d => Assert.NotEqual(424242L, d.Id));
        Assert.All(saved.Details, d => Assert.Equal(saved.Id, d.StockAdjustmentId));
        Assert.Equal(new[] { 1, 2 }, saved.Details.OrderBy(d => d.SortNo).Select(d => d.SortNo).ToArray());

        var details = saved.Details.OrderBy(d => d.SortNo).ToList();
        Assert.Equal(-2m, details[0].DiffQuantity);
        Assert.Equal(-25m, details[0].DiffAmount);
        Assert.Equal(4m, details[1].DiffQuantity);
        Assert.Equal(16m, details[1].DiffAmount);
        Assert.Equal(1L, CurrentSequence(db));                         // 创建恰好消耗一次单号
    }

    // ==================== 3. 创建：零数量 / 零成本 / 有符号差异 / 空草稿头 ====================

    [Fact]
    public async Task 创建_零账面零实盘零成本_允许且保留有符号差异()
    {
        using var db = TestDbFactory.Create();
        var ctl = Authorized(db);
        SeedNumberRule(db);

        var result = await ctl.Create(Draft(
            Line(Product1, 0m, 0m, 0m),        // 零差异、零成本
            Line(Product2, 0m, 5m, 0m)));      // 盘盈 5、零成本

        Assert.IsType<OkObjectResult>(result);
        var saved = db.StockAdjustments.Include(o => o.Details).Single();
        Assert.Equal(5m, saved.TotalDiffQuantity);
        Assert.Equal(0m, saved.TotalDiffAmount);

        var details = saved.Details.OrderBy(d => d.SortNo).ToList();
        Assert.Equal(0m, details[0].DiffQuantity);
        Assert.Equal(5m, details[1].DiffQuantity);
        Assert.Equal(0m, details[1].DiffAmount);
    }

    [Fact]
    public async Task 空草稿头_创建与提交受支持_审核仍要求有效明细()
    {
        using var db = TestDbFactory.Create();
        var ctl = Authorized(db);
        SeedNumberRule(db);

        var id = CreatedId(await ctl.Create(Draft()));                       // 0 行明细仍受支持

        var created = db.StockAdjustments.Include(o => o.Details).Single();
        Assert.Empty(created.Details);
        Assert.Equal(0m, created.TotalDiffQuantity);
        Assert.Equal(0m, created.TotalDiffAmount);

        await ctl.Submit(id);                                               // 提交不新增非空闸门
        Assert.Equal(DocumentStatus.Submitted, db.StockAdjustments.Single().Status);

        var approveEx = await Assert.ThrowsAsync<BusinessException>(() => ctl.Approve(id));
        Assert.Equal(ErrorCodes.RuleConflict, approveEx.Code);              // 既有审核承诺边界保持不变
        Assert.Empty(db.StockMovements);
    }

    // ==================== 4. 修改：非法输入零改动 ====================

    [Fact]
    public async Task 修改_不可表示数值_拒绝且既有单据明细与单号序列不变()
    {
        using var db = TestDbFactory.Create();
        var ctl = Authorized(db);
        SeedNumberRule(db);

        var id = CreatedId(await ctl.Create(Draft(Line(Product1, 10m, 8m, 12.5m))));
        var before = db.StockAdjustments.AsNoTracking().Include(o => o.Details).Single(a => a.Id == id);
        var beforeDetail = before.Details.Single();

        var bad = Draft(Line(Product1, 10m, 8m, 12.5m));
        bad.Remark = "SHOULD_NOT_BE_SAVED";
        bad.Details[0].ActualQuantity = 1.00001m;

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Update(id, bad));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);

        var after = db.StockAdjustments.AsNoTracking().Include(o => o.Details).Single(a => a.Id == id);
        Assert.Equal(before.AdjustmentNo, after.AdjustmentNo);
        Assert.Equal(before.Remark, after.Remark);                          // 表头未被改写
        Assert.Equal(before.TotalDiffQuantity, after.TotalDiffQuantity);
        Assert.Equal(before.TotalDiffAmount, after.TotalDiffAmount);
        var afterDetail = after.Details.Single();
        Assert.Equal(beforeDetail.Id, afterDetail.Id);
        Assert.Equal(beforeDetail.BookQuantity, afterDetail.BookQuantity);
        Assert.Equal(beforeDetail.ActualQuantity, afterDetail.ActualQuantity);
        Assert.Equal(beforeDetail.UnitCost, afterDetail.UnitCost);
        Assert.Equal(beforeDetail.DiffQuantity, afterDetail.DiffQuantity);
        Assert.Equal(beforeDetail.DiffAmount, afterDetail.DiffAmount);
        Assert.Equal(1L, CurrentSequence(db));                             // 修改不消耗新单号
    }

    [Fact]
    public async Task 修改_客户端删除态有效行_服务端恢复为正常行并重算合计()
    {
        using var db = TestDbFactory.Create();
        var ctl = Authorized(db);
        SeedNumberRule(db);

        var id = CreatedId(await ctl.Create(Draft(Line(Product1, 10m, 8m, 12.5m))));

        var hidden = Line(Product2, 5m, 9m, 4m, isDeleted: true);
        hidden.Id = 999999L;
        var result = await ctl.Update(id, Draft(Line(Product1, 10m, 8m, 12.5m), hidden));

        Assert.IsType<OkObjectResult>(result);
        var after = db.StockAdjustments.AsNoTracking().Include(o => o.Details).Single(a => a.Id == id);
        Assert.Equal(2, after.Details.Count);
        Assert.All(after.Details, d => Assert.False(d.IsDeleted));
        Assert.All(after.Details, d => Assert.Equal(after.Id, d.StockAdjustmentId));
        Assert.Equal(2m, after.TotalDiffQuantity);
        Assert.Equal(-9m, after.TotalDiffAmount);
    }

    // ==================== 5. 提交：复核当前生效已存明细 ====================

    [Fact]
    public async Task 提交_已存明细不可表示_拒绝且状态库存流水序列不变()
    {
        using var db = TestDbFactory.Create();
        var ctl = Authorized(db);
        SeedNumberRule(db);

        var id = CreatedId(await ctl.Create(Draft(Line(Product1, 10m, 8m, 12.5m))));

        // 模拟历史 / 被改写为非法数值的已存明细：直接落库 5 位小数
        var stored = db.StockAdjustmentDetails.Single(d => d.StockAdjustmentId == id);
        stored.ActualQuantity = 1.00001m;
        db.SaveChanges();

        var ex = await Assert.ThrowsAsync<BusinessException>(() => ctl.Submit(id));

        Assert.Equal(ErrorCodes.InvalidParameter, ex.Code);
        Assert.Equal(DocumentStatus.Pending,
            db.StockAdjustments.AsNoTracking().Single(a => a.Id == id).Status);
        Assert.Empty(db.StockMovements);
        Assert.Equal(1L, CurrentSequence(db));                             // 提交不消耗单号

        // 修正为可表示值后可提交
        var detail = db.StockAdjustmentDetails.Single(d => d.StockAdjustmentId == id);
        detail.ActualQuantity = 8m;
        db.SaveChanges();
        await ctl.Submit(id);
        Assert.Equal(DocumentStatus.Submitted,
            db.StockAdjustments.AsNoTracking().Single(a => a.Id == id).Status);
    }

    // ==================== 6. 合法盘点：有符号差异过账（盘盈 +、盘亏 −） ====================

    [Fact]
    public async Task 合法盘点_提交审核_按有符号差异调整库存并写流水()
    {
        using var db = TestDbFactory.Create();
        SeedStock(db, WarehouseA, Product1, quantity: 10m, totalCost: 100m);   // 均价 10
        SeedStock(db, WarehouseA, Product2, quantity: 5m, totalCost: 20m);     // 均价 4
        var ctl = Authorized(db);
        SeedNumberRule(db);

        var id = CreatedId(await ctl.Create(Draft(
            Line(Product1, 10m, 7m, 10m),      // 盘亏 -3 × 10 = -30
            Line(Product2, 5m, 9m, 4m))));     // 盘盈 +4 × 4  = +16

        var created = db.StockAdjustments.AsNoTracking().Single(a => a.Id == id);
        Assert.Equal(1m, created.TotalDiffQuantity);
        Assert.Equal(-14m, created.TotalDiffAmount);

        await ctl.Submit(id);
        await ctl.Approve(id);

        var first = db.Stocks.AsNoTracking().Single(s => s.WarehouseId == WarehouseA && s.ProductId == Product1);
        Assert.Equal(7m, first.Quantity);
        Assert.Equal(70m, first.TotalCost);

        var second = db.Stocks.AsNoTracking().Single(s => s.WarehouseId == WarehouseA && s.ProductId == Product2);
        Assert.Equal(9m, second.Quantity);
        Assert.Equal(36m, second.TotalCost);

        var movements = db.StockMovements.OrderBy(m => m.Id).ToList();
        Assert.Equal(2, movements.Count);
        Assert.Equal(-1, movements[0].Direction);                          // 盘亏：减少
        Assert.Equal(3m, movements[0].Quantity);
        Assert.Equal(-30m, movements[0].Amount);
        Assert.Equal(1, movements[1].Direction);                           // 盘盈：增加
        Assert.Equal(4m, movements[1].Quantity);
        Assert.Equal(16m, movements[1].Amount);
        Assert.Equal(DocumentStatus.Approved,
            db.StockAdjustments.AsNoTracking().Single(a => a.Id == id).Status);
    }

    // ==================== 7. 实际 EF 精度与规则常量同源 ====================

    [Fact]
    public void 实际EF列精度与规则常量同源()
    {
        var options = new DbContextOptionsBuilder<ErpDbContext>()
            .UseSqlServer(
                "Server=(localdb)\\NEWERP_AutoAcceptance;Database=NEWERP_AUTOTEST_ERP507_MODEL_ONLY;Integrated Security=true")
            .Options;
        using var db = new ErpDbContext(options);
        var model = db.Model;

        Assert.Equal("decimal(18,4)",
            ColumnType(model, typeof(StockAdjustmentDetail), nameof(StockAdjustmentDetail.BookQuantity)));
        Assert.Equal("decimal(18,4)",
            ColumnType(model, typeof(StockAdjustmentDetail), nameof(StockAdjustmentDetail.ActualQuantity)));
        Assert.Equal("decimal(18,4)",
            ColumnType(model, typeof(StockAdjustmentDetail), nameof(StockAdjustmentDetail.DiffQuantity)));
        Assert.Equal("decimal(18,6)",
            ColumnType(model, typeof(StockAdjustmentDetail), nameof(StockAdjustmentDetail.UnitCost)));
        Assert.Equal("decimal(18,4)",
            ColumnType(model, typeof(StockAdjustmentDetail), nameof(StockAdjustmentDetail.DiffAmount)));
        Assert.Equal("decimal(18,4)",
            ColumnType(model, typeof(StockAdjustment), nameof(StockAdjustment.TotalDiffQuantity)));
        Assert.Equal("decimal(18,4)",
            ColumnType(model, typeof(StockAdjustment), nameof(StockAdjustment.TotalDiffAmount)));

        Assert.Equal(18, StockAdjustmentPostingRules.QuantityPrecision);
        Assert.Equal(4, StockAdjustmentPostingRules.QuantityScale);
        Assert.Equal(18, StockAdjustmentPostingRules.CostPrecision);
        Assert.Equal(6, StockAdjustmentPostingRules.CostScale);
        Assert.Equal(99_999_999_999_999.9999m, StockAdjustmentPostingRules.MaxDecimal18Scale4);
        Assert.Equal(999_999_999_999.999999m, StockAdjustmentPostingRules.MaxDecimal18Scale6);

        Assert.True(StockAdjustmentPostingRules.IsQuantityRepresentable(99_999_999_999_999.9999m));
        Assert.True(StockAdjustmentPostingRules.IsQuantityRepresentable(-99_999_999_999_999.9999m));
        Assert.False(StockAdjustmentPostingRules.IsQuantityRepresentable(100_000_000_000_000m));
        Assert.False(StockAdjustmentPostingRules.IsQuantityRepresentable(1.00001m));
        Assert.True(StockAdjustmentPostingRules.IsCostRepresentable(999_999_999_999.999999m));
        Assert.False(StockAdjustmentPostingRules.IsCostRepresentable(1_000_000_000_000m));
        Assert.False(StockAdjustmentPostingRules.IsCostRepresentable(1.0000001m));

        // 有符号差异口径与控制器 Normalize 同源：正数 = 盘盈、负数 = 盘亏，金额按 4 位小数取整。
        var gain = StockAdjustmentPostingRules.ComputeSignedDifference(5m, 9m, 4m);
        Assert.Equal(4m, gain.DiffQuantity);
        Assert.Equal(16m, gain.DiffAmount);
        var loss = StockAdjustmentPostingRules.ComputeSignedDifference(10m, 7m, 10m);
        Assert.Equal(-3m, loss.DiffQuantity);
        Assert.Equal(-30m, loss.DiffAmount);
        var rounded = StockAdjustmentPostingRules.ComputeSignedDifference(0m, 1.00005m, 1m);
        Assert.Equal(1.0001m, rounded.DiffAmount);
    }

    // ==================== 8. 控制器 / 规则源代码契约 ====================

    [Fact]
    public void 控制器源码契约_数值护栏先于单号与明细替换()
    {
        var controller = ReadSource("ERP.Api/Controllers/StockAdjustmentController.cs");
        Assert.Contains("StockAdjustmentPostingRules.EnsureLineShape", controller);
        Assert.Contains("StockAdjustmentPostingRules.EnsureLiveMasterDataAsync", controller);
        Assert.Contains("ApplyServerOwnedDetailIdentity", controller);

        var createStart = controller.IndexOf("public async Task<IActionResult> Create", StringComparison.Ordinal);
        var updateStart = controller.IndexOf("public async Task<IActionResult> Update", StringComparison.Ordinal);
        var approveStart = controller.IndexOf("public override async Task<IActionResult> Approve", StringComparison.Ordinal);
        Assert.True(createStart >= 0 && updateStart > createStart && approveStart > updateStart);

        var create = controller[createStart..updateStart];
        Assert.True(create.IndexOf("EnsureLineShape", StringComparison.Ordinal)
                    < create.IndexOf("_noService.GenerateAsync", StringComparison.Ordinal));   // 拒绝方不消耗单号
        Assert.Contains("ApplyServerOwnedDetailIdentity", create);                                   // 删除态服务端权威

        var update = controller[updateStart..approveStart];
        Assert.True(update.IndexOf("EnsureLineShape", StringComparison.Ordinal)
                    < update.IndexOf("Db.StockAdjustmentDetails.RemoveRange", StringComparison.Ordinal)); // 校验先于明细替换
        Assert.Contains("ApplyServerOwnedDetailIdentity", update);

        var rules = ReadSource("ERP.Application/Services/StockAdjustmentPostingRules.cs");
        Assert.Contains("ComputeSignedDifference", rules);
        Assert.Contains("IsQuantityRepresentable", rules);
        Assert.Contains("IsCostRepresentable", rules);
        Assert.Contains("请合并为一行", rules);                       // 保留原重复商品禁令
        Assert.DoesNotContain("SaveChanges", rules);                  // 纯判定 + 只读查询，不落库
        Assert.Contains("AsNoTracking", rules);
        Assert.Equal("stock-query", StockAdjustmentPostingRules.RequiredMenuCode);
    }

    // ==================== 测试辅助 ====================

    private static StockAdjustmentController Authorized(ErpDbContext db)
    {
        StockAdjustmentTestAuthorization.SeedMasterData(db, WarehouseA, Product1, Product2);
        return StockAdjustmentTestAuthorization.CreateAuthorized(db);
    }

    /// <summary>播种盘点单号规则（让 Create 会真实消耗单号），用于断言拒绝方不推进序列。</summary>
    private static void SeedNumberRule(ErpDbContext db)
    {
        db.SysDocumentNumberRules.Add(new SysDocumentNumberRule
        {
            DocumentType = DocumentType.StockAdjustment,
            RuleCode = $"PD-507-{Guid.NewGuid():N}",
            RuleName = "盘点单号",
            Prefix = "PD",
            DateFormat = "yyyyMMdd",
            SerialLength = 4,
            Separator = string.Empty,
            CurrentSequence = 0
        });
        db.SaveChanges();
    }

    private static long CurrentSequence(ErpDbContext db)
        => db.SysDocumentNumberRules.AsNoTracking()
            .Where(r => r.DocumentType == DocumentType.StockAdjustment && !r.IsDeleted)
            .Select(r => r.CurrentSequence)
            .Single();

    /// <summary>拒绝场景的统一断言：零单据、零明细、零流水、单号序列未推进。</summary>
    private static void AssertRejectedWithNoSideEffects(ErpDbContext db)
    {
        Assert.Empty(db.StockAdjustments);
        Assert.Empty(db.StockAdjustmentDetails);
        Assert.Empty(db.StockMovements);
        Assert.Equal(0L, CurrentSequence(db));
    }

    private static StockAdjustment Draft(params StockAdjustmentDetail[] lines) => new()
    {
        AdjustmentDate = DateTime.Today,
        WarehouseId = WarehouseA,
        AdjustType = "盘点调整",
        Remark = "ERP-507_TEST",
        Details = lines.ToList()
    };

    private static StockAdjustmentDetail Line(long? productId, decimal book, decimal actual, decimal cost,
        bool isDeleted = false) => new()
        {
            ProductId = productId,
            ProductName = productId is null ? "无商品" : $"P{productId}",
            Spec = "规格A",
            Unit = "PCS",
            BookQuantity = book,
            ActualQuantity = actual,
            UnitCost = cost,
            IsDeleted = isDeleted
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
