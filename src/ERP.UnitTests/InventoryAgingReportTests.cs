using ERP.Api.Controllers;
using ERP.Application.Common;
using ERP.Application.Interfaces;
using ERP.Application.Services;
using ERP.Domain.Entities;
using ERP.Domain.Enums;
using ERP.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Reflection;
using Xunit;

namespace ERP.UnitTests;

/// <summary>
/// ERP-034 库存库龄与成本估值报表（只读派生）单元测试：
/// 库龄全部分层边界（0 / 30 / 31 / 60 / 61 / 90 / 91 / 180 / 181 天）、FIFO 分层消耗、红字冲销权威配对
/// （入库冲销扣回原层、出库冲销按原出库日期回补）、无台账与部分无台账（库龄未知）、截止日期截断、
/// 已知成本与未知成本分离、币种口径（不读文本字典）、筛选 / 分页有界 / 参数校验 / 只读不写库 / 前端接线。
/// <para>说明：全部使用内存数据库，不连接 SQL Server、不启动 API、不执行任何 SQL 或 seed。</para>
/// </summary>
public class InventoryAgingReportTests
{
    private static readonly DateTime AsOf = new(2026, 9, 24);
    private const long WarehouseA = 910001L;
    private const long WarehouseB = 910002L;
    private const long Product1 = 710001L;
    private const long Product2 = 710002L;

    // ==================== 1. 库龄分层边界：0 / 30 / 31 / 60 / 61 / 90 / 91 / 180 / 181 天 ====================

    [Fact]
    public async Task Report_places_every_age_boundary_into_the_documented_bucket()
    {
        using var db = TestDbFactory.Create();
        SeedProduct(db, Product1, "P001", "商品一");
        SeedWarehouse(db, WarehouseA, "主仓");

        var ages = new[] { 0, 30, 31, 60, 61, 90, 91, 180, 181 };
        long movementId = 1;
        foreach (var age in ages)
        {
            SeedMovement(db, WarehouseA, Product1, AsOf.AddDays(-age), direction: 1, quantity: 1m, id: movementId++);
        }

        SeedStock(db, WarehouseA, Product1, ages.Length, averageCost: 10m, totalCost: 90m);
        await db.SaveChangesAsync();

        var report = await Service(db).GetInventoryAgingReportAsync(Query());
        var row = Assert.Single(report.Items);

        Assert.Equal(9m, row.CurrentQuantity);
        Assert.Equal(9m, row.KnownAgedQuantity);
        Assert.Equal(0m, row.UnknownAgeQuantity);
        Assert.Equal(InventoryAgingSemantics.EvidenceFull, row.EvidenceStatus);

        Assert.Equal(5, row.Buckets.Count);                                   // 固定 5 格，顺序与口径常量一致
        Assert.Equal(InventoryAgingSemantics.BucketKeys, row.Buckets.Select(b => b.Key));
        Assert.Equal(new[] { "0-30 天", "31-60 天", "61-90 天", "91-180 天", "180 天以上" },
            row.Buckets.Select(b => b.Label));
        Assert.Equal(new[] { 0, 31, 61, 91, 181 }, row.Buckets.Select(b => b.MinDays));
        Assert.Equal(new int?[] { 30, 60, 90, 180, null }, row.Buckets.Select(b => b.MaxDays));

        // 0 / 30 → 0-30；31 / 60 → 31-60；61 / 90 → 61-90；91 / 180 → 91-180；181 → 超出 180
        Assert.Equal(new[] { 2m, 2m, 2m, 2m, 1m }, row.Buckets.Select(b => b.Quantity));
        Assert.Equal(new[] { 20m, 20m, 20m, 20m, 10m }, row.Buckets.Select(b => b.Amount ?? -1m));

        // 分层金额合计恒等于库存行持久化的权威金额（1 × 10 × 9 = 90）
        Assert.Equal(row.AuthoritativeAmount, row.Buckets.Sum(b => b.Amount));
        Assert.Equal(90m, row.AgedAmount);
        Assert.Equal(InventoryAgingSemantics.CostKnown, row.CostStatus);
        Assert.Equal(9m, report.PageKnownAgedQuantity);
        Assert.Equal(90m, report.PageAuthoritativeAmount);
    }

    // ==================== 2. FIFO：出库消耗最早的分层 ====================

    [Fact]
    public async Task Report_consumes_the_oldest_layers_first_like_a_ledger_fifo()
    {
        using var db = TestDbFactory.Create();
        SeedProduct(db, Product1, "P001", "商品一");
        SeedWarehouse(db, WarehouseA, "主仓");
        SeedMovement(db, WarehouseA, Product1, AsOf.AddDays(-200), direction: 1, quantity: 10m, id: 1);
        SeedMovement(db, WarehouseA, Product1, AsOf.AddDays(-100), direction: 1, quantity: 10m, id: 2);
        SeedMovement(db, WarehouseA, Product1, AsOf.AddDays(-10), direction: 1, quantity: 10m, id: 3);
        SeedMovement(db, WarehouseA, Product1, AsOf.AddDays(-5), direction: -1, quantity: 10m, id: 4);
        SeedStock(db, WarehouseA, Product1, 20m, averageCost: 5m, totalCost: 100m);
        await db.SaveChangesAsync();

        var row = Assert.Single((await Service(db).GetInventoryAgingReportAsync(Query())).Items);

        Assert.Equal(20m, row.CurrentQuantity);
        Assert.Equal(20m, row.KnownAgedQuantity);
        Assert.Equal(0m, row.UnknownAgeQuantity);
        Assert.Equal(0m, row.Buckets[4].Quantity);                            // 200 天那层被最早消耗完
        Assert.Equal(10m, row.Buckets[3].Quantity);                           // 100 天 → 91-180
        Assert.Equal(10m, row.Buckets[0].Quantity);                           // 10 天 → 0-30
    }

    // ==================== 3. 无台账 / 部分无台账：库龄未知，不放进任何分层 ====================

    [Fact]
    public async Task Report_keeps_stock_without_ledger_evidence_out_of_every_bucket()
    {
        using var db = TestDbFactory.Create();
        SeedProduct(db, Product1, "P001", "商品一");
        SeedWarehouse(db, WarehouseA, "主仓");
        SeedStock(db, WarehouseA, Product1, 7m, averageCost: 0m, totalCost: 0m);   // 历史库存：无任何流水
        await db.SaveChangesAsync();

        var report = await Service(db).GetInventoryAgingReportAsync(Query());
        var row = Assert.Single(report.Items);

        Assert.Equal(7m, row.CurrentQuantity);
        Assert.Equal(0m, row.KnownAgedQuantity);
        Assert.Equal(7m, row.UnknownAgeQuantity);
        Assert.Equal(InventoryAgingSemantics.EvidenceNone, row.EvidenceStatus);
        Assert.All(row.Buckets, b => Assert.Equal(0m, b.Quantity));                // 不放进任何分层
        Assert.All(row.Buckets, b => Assert.Null(b.Amount));                       // 成本未知：金额为未知（null）
        Assert.Null(row.AuthoritativeAmount);
        Assert.Equal(7m, row.UnknownCostQuantity);
        Assert.Contains("库龄未知", row.Note);
        Assert.Contains("不臆造入库日期", row.Note);
        Assert.Contains("未建立成本依据", row.Note);

        Assert.Equal(7m, report.PageUnknownAgeQuantity);
        Assert.Null(report.PageAuthoritativeAmount);                               // 全部无成本依据：合计是未知，不是 0
        Assert.All(report.PageBuckets, b => Assert.Null(b.Amount));
        Assert.Equal(1, report.NoEvidenceCount);
        Assert.Equal(1, report.UnknownCostCount);
        Assert.Equal(0, report.KnownCostCount);
        Assert.Equal(InventoryAgingSemantics.CostCurrency, report.CostCurrency);
        Assert.Equal(InventoryAgingSemantics.RuleText, report.Rule);
        Assert.Equal(InventoryAgingSemantics.CostRuleText, report.CostRule);
        Assert.Equal(InventoryAgingSemantics.PageScopeText, report.ScopeNote);
    }

    [Fact]
    public async Task Report_splits_partially_evidenced_stock_into_known_buckets_and_unknown_age()
    {
        using var db = TestDbFactory.Create();
        SeedProduct(db, Product1, "P001", "商品一");
        SeedWarehouse(db, WarehouseA, "主仓");
        SeedMovement(db, WarehouseA, Product1, AsOf.AddDays(-45), direction: 1, quantity: 10m, id: 1);
        SeedStock(db, WarehouseA, Product1, 15m, averageCost: 4m, totalCost: 60m);   // 5 件是流水起点之前的历史库存
        await db.SaveChangesAsync();

        var row = Assert.Single((await Service(db).GetInventoryAgingReportAsync(Query())).Items);

        Assert.Equal(15m, row.CurrentQuantity);
        Assert.Equal(10m, row.KnownAgedQuantity);
        Assert.Equal(5m, row.UnknownAgeQuantity);
        Assert.Equal(InventoryAgingSemantics.EvidencePartial, row.EvidenceStatus);
        Assert.Equal(10m, row.Buckets[1].Quantity);                                  // 45 天 → 31-60
        Assert.Equal(40m, row.Buckets[1].Amount);                                    // 10 × 4（持久化均价）
        Assert.Equal(20m, row.UnknownAgeAmount);                                     // 未分层数量按同一套持久化均价分摊
        Assert.Equal(row.AuthoritativeAmount, row.Buckets.Sum(b => b.Amount) + row.UnknownAgeAmount);
        Assert.Contains("没有台账分层依据", row.Note);
    }

    // ==================== 4. 红字冲销：按 ReversalOfMovementId 权威配对，不二次扣减 ====================

    [Fact]
    public async Task Report_reverses_the_inbound_layer_it_refers_to_instead_of_consuming_fifo()
    {
        using var db = TestDbFactory.Create();
        SeedProduct(db, Product1, "P001", "商品一");
        SeedWarehouse(db, WarehouseA, "主仓");
        SeedMovement(db, WarehouseA, Product1, AsOf.AddDays(-200), direction: 1, quantity: 10m, id: 1);
        SeedMovement(db, WarehouseA, Product1, AsOf.AddDays(-10), direction: 1, quantity: 10m, id: 2);
        // 冲销「10 天前」那张入库（不是最早那层）：单纯 FIFO 消耗会误扣 200 天那层，权威配对必须扣回被冲销的层
        SeedMovement(db, WarehouseA, Product1, AsOf.AddDays(-1), direction: -1, quantity: 10m,
            isReversal: true, reversalOf: 2, id: 3);
        SeedStock(db, WarehouseA, Product1, 10m, averageCost: 2m, totalCost: 20m);
        await db.SaveChangesAsync();

        var row = Assert.Single((await Service(db).GetInventoryAgingReportAsync(Query())).Items);

        Assert.Equal(10m, row.CurrentQuantity);
        Assert.Equal(10m, row.Buckets[4].Quantity);                            // 200 天那层仍在（未被误扣）
        Assert.Equal(0m, row.Buckets[0].Quantity);                             // 被冲销的 10 天那层已扣回
        Assert.Equal(0m, row.UnknownAgeQuantity);
        Assert.Equal(1, row.ReversalCount);
        Assert.Equal(0m, row.UnpairedQuantity);
        Assert.Contains("红字冲销", row.Note);
    }

    [Fact]
    public async Task Report_restores_a_reversed_outbound_at_its_original_movement_date()
    {
        using var db = TestDbFactory.Create();
        SeedProduct(db, Product1, "P001", "商品一");
        SeedWarehouse(db, WarehouseA, "主仓");
        SeedMovement(db, WarehouseA, Product1, AsOf.AddDays(-100), direction: 1, quantity: 10m, id: 1);
        SeedMovement(db, WarehouseA, Product1, AsOf.AddDays(-50), direction: -1, quantity: 4m, id: 2);
        // 冲销那张出库：原出库并未发生，数量与库龄都回到「原出库日期」（50 天前 → 31-60），
        // 而不是按冲销日（1 天前）重新起算
        SeedMovement(db, WarehouseA, Product1, AsOf.AddDays(-1), direction: 1, quantity: 4m,
            isReversal: true, reversalOf: 2, id: 3);
        SeedStock(db, WarehouseA, Product1, 10m, averageCost: 3m, totalCost: 30m);
        await db.SaveChangesAsync();

        var row = Assert.Single((await Service(db).GetInventoryAgingReportAsync(Query())).Items);

        Assert.Equal(10m, row.CurrentQuantity);
        Assert.Equal(4m, row.Buckets[1].Quantity);                             // 回补的 4 件按原出库日期 → 31-60
        Assert.Equal(6m, row.Buckets[3].Quantity);                             // 原入库 10 件剩余 6 件 → 91-180
        Assert.Equal(0m, row.Buckets[0].Quantity);
        Assert.Equal(10m, row.KnownAgedQuantity);
        Assert.Equal(0m, row.UnknownAgeQuantity);
        Assert.Equal(1, row.ReversalCount);
    }

    [Fact]
    public async Task Report_falls_back_to_direction_when_the_reversed_original_is_not_visible()
    {
        using var db = TestDbFactory.Create();
        SeedProduct(db, Product1, "P001", "商品一");
        SeedProduct(db, Product2, "P002", "商品二");
        SeedWarehouse(db, WarehouseA, "主仓");

        // 场景 A：红字腿引用一条不可见（已删除 / 不在口径内）的入库 → 按自身方向做 FIFO 消耗，不臆造新层
        SeedMovement(db, WarehouseA, Product1, AsOf.AddDays(-20), direction: 1, quantity: 10m, id: 1);
        SeedMovement(db, WarehouseA, Product1, AsOf.AddDays(-5), direction: -1, quantity: 10m,
            isReversal: true, reversalOf: 999, id: 2);
        SeedStock(db, WarehouseA, Product1, 0m, averageCost: 0m, totalCost: 0m);

        // 场景 B：红字腿引用一条不可见的出库 → 按自身方向与日期形成一层（不猜测配对关系）
        SeedMovement(db, WarehouseA, Product2, AsOf.AddDays(-3), direction: 1, quantity: 6m,
            isReversal: true, reversalOf: 888, id: 3);
        SeedStock(db, WarehouseA, Product2, 6m, averageCost: 1m, totalCost: 6m);
        await db.SaveChangesAsync();

        var report = await Service(db).GetInventoryAgingReportAsync(Query(onlyPositiveQuantity: false));

        var noOriginal = report.Items.Single(i => i.ProductId == Product1);
        Assert.Equal(0m, noOriginal.KnownAgedQuantity);                        // 被 FIFO 消耗完，没有臆造新层
        Assert.Equal(0m, noOriginal.UnknownAgeQuantity);
        Assert.Equal(0, noOriginal.ReversalCount);                             // 原流水不可见：不配对、不二次扣减
        Assert.Equal(0m, noOriginal.UnpairedQuantity);

        var orphan = report.Items.Single(i => i.ProductId == Product2);
        Assert.Equal(6m, orphan.Buckets[0].Quantity);                          // 按红字腿自身日期入库，仍是台账事实
        Assert.Equal(0m, orphan.UnknownAgeQuantity);
    }

    // ==================== 5. 已知成本与未知成本严格分离 ====================

    [Fact]
    public async Task Report_separates_unknown_cost_from_the_authoritative_totals()
    {
        using var db = TestDbFactory.Create();
        SeedProduct(db, Product1, "P001", "商品一");
        SeedProduct(db, Product2, "P002", "商品二");
        SeedWarehouse(db, WarehouseA, "主仓");

        // 成本已知行：10 件 × 3 = 30（持久化均价与库存金额都来自库存行）
        SeedMovement(db, WarehouseA, Product1, AsOf.AddDays(-10), direction: 1, quantity: 10m, id: 1);
        SeedStock(db, WarehouseA, Product1, 10m, averageCost: 3m, totalCost: 30m);

        // 成本未知行：历史库存（首次入库成本为 0 的兜底口径）→ 数量单列、金额为未知
        SeedMovement(db, WarehouseA, Product2, AsOf.AddDays(-10), direction: 1, quantity: 5m, id: 2);
        SeedStock(db, WarehouseA, Product2, 5m, averageCost: 0m, totalCost: 0m);
        await db.SaveChangesAsync();

        var service = Service(db);
        var report = await service.GetInventoryAgingReportAsync(Query());

        var known = report.Items.Single(i => i.ProductId == Product1);
        Assert.Equal(InventoryAgingSemantics.CostKnown, known.CostStatus);
        Assert.Equal(3m, known.AverageCost);
        Assert.Equal(30m, known.AuthoritativeAmount);
        Assert.Equal(30m, known.AgedAmount);
        Assert.Equal(30m, known.Buckets[0].Amount);
        Assert.Equal(0m, known.UnknownCostQuantity);

        var unknown = report.Items.Single(i => i.ProductId == Product2);
        Assert.Equal(InventoryAgingSemantics.CostUnknown, unknown.CostStatus);
        Assert.Equal(5m, unknown.KnownAgedQuantity);                            // 数量有台账依据
        Assert.Null(unknown.AuthoritativeAmount);                               // 金额是未知（null），不是 0
        Assert.Null(unknown.AgedAmount);
        Assert.Null(unknown.UnknownAgeAmount);
        Assert.All(unknown.Buckets, b => Assert.Null(b.Amount));
        Assert.Equal(5m, unknown.UnknownCostQuantity);                          // 成本未知数量单列
        Assert.Contains("成本未知", unknown.Note);

        // 权威合计只包含成本已知的行；成本未知数量与权威合计严格分离
        Assert.Equal(30m, report.PageAuthoritativeAmount);
        Assert.Equal(5m, report.PageUnknownCostQuantity);
        Assert.Equal(1, report.KnownCostCount);
        Assert.Equal(1, report.UnknownCostCount);
        Assert.Equal(30m, report.PageBuckets[0].Amount);

        // 只筛成本未知行：权威合计与分层金额都回落为「未知」（null）而不是 0
        var onlyUnknown = await service.GetInventoryAgingReportAsync(Query(productId: Product2));
        Assert.Null(onlyUnknown.PageAuthoritativeAmount);
        Assert.All(onlyUnknown.PageBuckets, b => Assert.Null(b.Amount));
    }

    // ==================== 6. 分层金额按持久化均价分摊：恒等于行权威金额（确定性取整） ====================

    [Fact]
    public async Task Report_apportions_amounts_so_buckets_tie_to_the_persisted_stock_amount()
    {
        using var db = TestDbFactory.Create();
        SeedProduct(db, Product1, "P001", "商品一");
        SeedWarehouse(db, WarehouseA, "主仓");
        SeedMovement(db, WarehouseA, Product1, AsOf.AddDays(-10), direction: 1, quantity: 1m, id: 1);
        SeedMovement(db, WarehouseA, Product1, AsOf.AddDays(-45), direction: 1, quantity: 1m, id: 2);
        // 持久化均价 1.00005（6 位小数）→ 两格各 1 × 1.00005 = 1.00005，取整后合计 2.0002 与权威金额 2.0001 差 -0.0001
        SeedStock(db, WarehouseA, Product1, 2m, averageCost: 1.00005m, totalCost: 2.0001m);
        await db.SaveChangesAsync();

        var row = Assert.Single((await Service(db).GetInventoryAgingReportAsync(Query())).Items);

        Assert.NotNull(row.Buckets[0].Amount);
        Assert.NotNull(row.Buckets[1].Amount);
        Assert.Equal(1.0000m, row.Buckets[0].Amount!.Value);                    // 取整残差并入数量最多（并列为第一格）
        Assert.Equal(1.0001m, row.Buckets[1].Amount!.Value);
        Assert.Equal(row.AuthoritativeAmount, row.Buckets.Sum(b => b.Amount) + row.UnknownAgeAmount);
        Assert.Equal(2.0001m, row.AgedAmount);
    }

    // ==================== 7. 截止日期截断：之后的流水不进入口径（既不删除也不臆造） ====================

    [Fact]
    public async Task Report_truncates_the_ledger_at_the_as_of_date_and_reincludes_later_layers()
    {
        using var db = TestDbFactory.Create();
        SeedProduct(db, Product1, "P001", "商品一");
        SeedWarehouse(db, WarehouseA, "主仓");
        SeedMovement(db, WarehouseA, Product1, AsOf.AddHours(23), direction: 1, quantity: 2m, id: 1);   // 截止当天（含时间）
        SeedMovement(db, WarehouseA, Product1, AsOf.AddDays(3), direction: 1, quantity: 5m, id: 2);    // 截止之后
        SeedStock(db, WarehouseA, Product1, 7m, averageCost: 2m, totalCost: 14m);
        await db.SaveChangesAsync();

        var service = Service(db);
        var atAsOf = await service.GetInventoryAgingReportAsync(Query());
        var row = Assert.Single(atAsOf.Items);
        Assert.Equal(2m, row.KnownAgedQuantity);                                // 只算截止当天（含时间部分）
        Assert.Equal(5m, row.UnknownAgeQuantity);                               // 之后的层是「库龄未知」，不臆造
        Assert.Equal(InventoryAgingSemantics.EvidencePartial, row.EvidenceStatus);
        Assert.Equal(2m, row.Buckets[0].Quantity);

        var later = await service.GetInventoryAgingReportAsync(Query(asOf: AsOf.AddDays(3)));
        var laterRow = Assert.Single(later.Items);
        Assert.Equal(7m, laterRow.KnownAgedQuantity);                           // 时点后移后重新纳入
        Assert.Equal(7m, laterRow.Buckets[0].Quantity);                         // 3 天前的 5 件与当天的 2 件同属 0-30
        Assert.Equal(new[] { 7m, 0m, 0m, 0m, 0m }, laterRow.Buckets.Select(b => b.Quantity));
        Assert.Equal(0m, laterRow.UnknownAgeQuantity);
        Assert.Equal(InventoryAgingSemantics.EvidenceFull, laterRow.EvidenceStatus);
    }

    // ==================== 8. 缺证据的出库 / 台账多于现存量：留痕而不臆造 ====================

    [Fact]
    public async Task Report_records_outbound_without_any_layer_as_unpaired_evidence()
    {
        using var db = TestDbFactory.Create();
        SeedProduct(db, Product1, "P001", "商品一");
        SeedWarehouse(db, WarehouseA, "主仓");
        SeedMovement(db, WarehouseA, Product1, AsOf.AddDays(-40), direction: 1, quantity: 3m, id: 1);
        SeedMovement(db, WarehouseA, Product1, AsOf.AddDays(-10), direction: -1, quantity: 5m, id: 2);
        SeedStock(db, WarehouseA, Product1, 0m, averageCost: 0m, totalCost: 0m);
        await db.SaveChangesAsync();

        var row = Assert.Single((await Service(db)
            .GetInventoryAgingReportAsync(Query(onlyPositiveQuantity: false))).Items);

        Assert.All(row.Buckets, b => Assert.Equal(0m, b.Quantity));            // 没有臆造分层
        Assert.Equal(2m, row.UnpairedQuantity);                                // 3 件有依据、2 件没有
        Assert.Equal(0m, row.UnknownAgeQuantity);
        Assert.Contains("没有可扣减的台账分层", row.Note);
        Assert.Contains("现存量 <= 0", row.Note);
    }

    [Fact]
    public async Task Report_deducts_ledger_surplus_over_stock_from_the_oldest_layers()
    {
        using var db = TestDbFactory.Create();
        SeedProduct(db, Product1, "P001", "商品一");
        SeedWarehouse(db, WarehouseA, "主仓");
        SeedMovement(db, WarehouseA, Product1, AsOf.AddDays(-100), direction: 1, quantity: 10m, id: 1);
        SeedMovement(db, WarehouseA, Product1, AsOf.AddDays(-10), direction: 1, quantity: 10m, id: 2);
        SeedStock(db, WarehouseA, Product1, 12m, averageCost: 1m, totalCost: 12m);   // 台账 20 > 现存量 12
        await db.SaveChangesAsync();

        var row = Assert.Single((await Service(db).GetInventoryAgingReportAsync(Query())).Items);

        Assert.Equal(12m, row.KnownAgedQuantity);
        Assert.Equal(0m, row.UnknownAgeQuantity);
        Assert.Equal(8m, row.LedgerDeficitQuantity);                           // 差额按 FIFO 从最早分层扣减
        Assert.Equal(2m, row.Buckets[3].Quantity);                             // 100 天那层只剩 2
        Assert.Equal(10m, row.Buckets[0].Quantity);
        Assert.Contains("大于现存量", row.Note);
    }

    // ==================== 9. 筛选：仓库 / 商品 / 关键字 / 现存量 ====================

    [Fact]
    public async Task Report_filters_by_warehouse_product_keyword_and_positive_quantity()
    {
        using var db = TestDbFactory.Create();
        SeedProduct(db, Product1, "P001", "商品一");
        SeedProduct(db, Product2, "P002", "商品二");
        SeedWarehouse(db, WarehouseA, "主仓");
        SeedWarehouse(db, WarehouseB, "备用仓");
        SeedMovement(db, WarehouseA, Product1, AsOf.AddDays(-5), direction: 1, quantity: 5m, id: 1);
        SeedMovement(db, WarehouseB, Product1, AsOf.AddDays(-5), direction: 1, quantity: 7m, id: 2);
        SeedStock(db, WarehouseA, Product1, 5m, averageCost: 1m, totalCost: 5m);
        SeedStock(db, WarehouseB, Product1, 7m, averageCost: 1m, totalCost: 7m);
        SeedStock(db, WarehouseA, Product2, 0m, averageCost: 0m, totalCost: 0m);          // 零数量行
        await db.SaveChangesAsync();

        var service = Service(db);

        // 默认仅现存量 > 0：零数量行不出现
        var all = await service.GetInventoryAgingReportAsync(Query());
        Assert.Equal(2, all.Total);
        Assert.DoesNotContain(all.Items, i => i.WarehouseId == WarehouseA && i.ProductId == Product2);

        // 仓库筛选
        var byWarehouse = await service.GetInventoryAgingReportAsync(Query(warehouseId: WarehouseB));
        var warehouseRow = Assert.Single(byWarehouse.Items);
        Assert.Equal(WarehouseB, warehouseRow.WarehouseId);
        Assert.Equal("备用仓", warehouseRow.WarehouseName);
        Assert.Equal(7m, warehouseRow.CurrentQuantity);

        // 商品筛选 + 放开现存量限制：零数量行列出并标注
        var byProduct = await service.GetInventoryAgingReportAsync(
            Query(productId: Product2, onlyPositiveQuantity: false));
        var zeroRow = Assert.Single(byProduct.Items);
        Assert.Equal(0m, zeroRow.CurrentQuantity);
        Assert.Equal(0m, zeroRow.UnknownAgeQuantity);
        Assert.Contains("现存量 <= 0", zeroRow.Note);

        // 关键字筛选：匹配商品资料名称
        var byKeyword = await service.GetInventoryAgingReportAsync(
            Query(keyword: "商品二", onlyPositiveQuantity: false));
        Assert.Equal(Product2, Assert.Single(byKeyword.Items).ProductId);

        // 关键字无匹配：返回空集而不是全量
        var none = await service.GetInventoryAgingReportAsync(Query(keyword: "不存在的商品"));
        Assert.Equal(0, none.Total);
        Assert.Empty(none.Items);
        Assert.Null(none.PageAuthoritativeAmount);
    }

    // ==================== 10. 分页：有界且合计只统计本页 ====================

    [Fact]
    public async Task Report_pages_rows_and_scopes_totals_to_the_returned_page()
    {
        using var db = TestDbFactory.Create();
        SeedWarehouse(db, WarehouseA, "主仓");
        for (var i = 1; i <= 5; i++)
        {
            var productId = Product1 + i;
            SeedProduct(db, productId, $"P00{i}", $"商品{i}");
            SeedMovement(db, WarehouseA, productId, AsOf.AddDays(-1), direction: 1, quantity: i, id: i);
            SeedStock(db, WarehouseA, productId, i, averageCost: 1m, totalCost: i);
        }
        await db.SaveChangesAsync();

        var service = Service(db);
        var page1 = await service.GetInventoryAgingReportAsync(Query(page: 1, pageSize: 2));

        Assert.Equal(5, page1.Total);
        Assert.Equal(3, page1.TotalPages);
        Assert.Equal(2, page1.Items.Count);
        Assert.Equal(new[] { Product1 + 1L, Product1 + 2L }, page1.Items.Select(i => i.ProductId));
        Assert.Equal(3m, page1.PageCurrentQuantity);                           // 1 + 2：只统计本页
        Assert.Equal(3m, page1.PageKnownAgedQuantity);
        Assert.Equal(3m, page1.PageBuckets[0].Quantity);
        Assert.Equal(3m, page1.PageBuckets[0].Amount);
        Assert.Equal(3m, page1.PageAuthoritativeAmount);

        var page3 = await service.GetInventoryAgingReportAsync(Query(page: 3, pageSize: 2));
        Assert.Equal(5m, Assert.Single(page3.Items).CurrentQuantity);
        Assert.Equal(5m, page3.PageKnownAgedQuantity);

        // 超过上限的 pageSize 按上限截断（有界查询）
        var capped = await service.GetInventoryAgingReportAsync(Query(page: 1, pageSize: 100000));
        Assert.Equal(ReportDtos.InventoryAgingReportQuery.MaxPageSize, capped.PageSize);
        Assert.Equal(5, capped.Items.Count);
    }

    // ==================== 11. 参数校验与归一化 ====================

    [Fact]
    public void Query_normalization_defaults_the_date_caps_page_size_and_trims_keyword()
    {
        var query = new ReportDtos.InventoryAgingReportQuery { Page = 0, PageSize = 100000, Keyword = "  P001  " };
        query.Normalize();

        Assert.Equal(DateTime.Today, query.AsOfDate);
        Assert.Equal(1, query.Page);
        Assert.Equal(ReportDtos.InventoryAgingReportQuery.MaxPageSize, query.PageSize);
        Assert.Equal("P001", query.Keyword);

        var blank = new ReportDtos.InventoryAgingReportQuery { Keyword = "   " };
        blank.Normalize();
        Assert.Null(blank.Keyword);
    }

    // ==================== 12. 非变更：报表不改写库存与台账（数据前后一致 + 0 次 SaveChanges） ====================

    [Fact]
    public async Task Report_never_mutates_stock_or_ledger_rows()
    {
        using var db = TestDbFactory.Create();
        SeedProduct(db, Product1, "P001", "商品一");
        SeedWarehouse(db, WarehouseA, "主仓");
        SeedMovement(db, WarehouseA, Product1, AsOf.AddDays(-100), direction: 1, quantity: 10m, id: 1);
        SeedMovement(db, WarehouseA, Product1, AsOf.AddDays(-50), direction: -1, quantity: 4m, id: 2);
        SeedMovement(db, WarehouseA, Product1, AsOf.AddDays(-2), direction: 1, quantity: 4m,
            isReversal: true, reversalOf: 2, id: 3);
        SeedStock(db, WarehouseA, Product1, 10m, averageCost: 3m, totalCost: 30m);
        await db.SaveChangesAsync();

        var before = db.StockMovements.AsNoTracking().OrderBy(m => m.Id)
            .Select(m => new { m.Id, m.Direction, m.Quantity, m.IsReversal, m.IsReversed, m.ReversalOfMovementId })
            .ToList();

        var counting = CountingDbContext.Wrap(db);
        var report = await new ReportService(counting.Proxy).GetInventoryAgingReportAsync(Query());

        Assert.Single(report.Items);
        Assert.Equal(0, counting.WriteCalls);                                      // 只读报表：没有一次 SaveChanges
        Assert.DoesNotContain(counting.ReadProperties, n => n.Contains("BaseOtherInfo", StringComparison.Ordinal));
        Assert.DoesNotContain(counting.ReadProperties, n => n.Contains("SysParameter", StringComparison.Ordinal));

        var after = db.StockMovements.AsNoTracking().OrderBy(m => m.Id)
            .Select(m => new { m.Id, m.Direction, m.Quantity, m.IsReversal, m.IsReversed, m.ReversalOfMovementId })
            .ToList();
        Assert.Equal(before, after);                                               // 台账逐列未被改写（含红字标记）

        var stock = db.Stocks.AsNoTracking().Single();
        Assert.Equal(10m, stock.Quantity);
        Assert.Equal(3m, stock.AverageCost);
        Assert.Equal(30m, stock.TotalCost);
    }

    // ==================== 13. 有界查询：数据集访问次数与行数无关 ====================

    [Fact]
    public async Task Report_uses_a_bounded_number_of_dataset_reads()
    {
        using var db = TestDbFactory.Create();
        SeedWarehouse(db, WarehouseA, "主仓");
        SeedProduct(db, Product1, "P001", "商品一");
        SeedStock(db, WarehouseA, Product1, 5m, averageCost: 1m, totalCost: 5m);
        SeedMovement(db, WarehouseA, Product1, AsOf.AddDays(-1), direction: 1, quantity: 5m, id: 1);
        await db.SaveChangesAsync();

        var counting = CountingDbContext.Wrap(db);
        var service = new ReportService(counting.Proxy);

        var single = await service.GetInventoryAgingReportAsync(Query(pageSize: 1));
        var singleReads = counting.DatasetReads;
        Assert.Equal(1, single.Total);
        // 常数级访问：库存行 + 仓库名 + 商品信息 + 库龄台账（各一次整表级查询），台账数据集只读一次
        Assert.Equal(4, singleReads);
        Assert.Equal(1, counting.ReadProperties.Count(n => n == "StockMovements"));

        // 再补 300 个库存行 + 对应台账（跨多页）：数据集访问次数必须保持不变（无逐行查库）
        for (var i = 1; i <= 300; i++)
        {
            var productId = Product1 + i;
            SeedProduct(db, productId, $"P{i:0000}", $"商品{i}");
            SeedStock(db, WarehouseA, productId, i, averageCost: 1m, totalCost: i);
            SeedMovement(db, WarehouseA, productId, AsOf.AddDays(-2), direction: 1, quantity: i, id: i + 1);
        }
        await db.SaveChangesAsync();

        var large = await service.GetInventoryAgingReportAsync(Query(pageSize: 200));
        var largeReads = counting.DatasetReads - singleReads;
        Assert.Equal(301, large.Total);
        Assert.Equal(ReportDtos.InventoryAgingReportQuery.MaxPageSize, large.Items.Count);   // 单页有界（上限 200）
        Assert.Equal(singleReads, largeReads);                                               // 行数 / 页大小变化不改变访问次数

        // 关键字走 EXISTS 子查询（不逐行回表）：整页 200 行与单行请求的访问次数一致
        await service.GetInventoryAgingReportAsync(Query(keyword: "P", pageSize: 200));      // 预热查询管道
        var beforeFull = counting.DatasetReads;
        var keywordFull = await service.GetInventoryAgingReportAsync(Query(keyword: "P", pageSize: 200));
        var keywordFullReads = counting.DatasetReads - beforeFull;
        var beforeSingle = counting.DatasetReads;
        var keywordSingle = await service.GetInventoryAgingReportAsync(Query(keyword: "P", pageSize: 1));
        var keywordSingleReads = counting.DatasetReads - beforeSingle;

        Assert.Equal(301, keywordFull.Total);
        Assert.Equal(200, keywordFull.Items.Count);
        Assert.Equal(301, keywordSingle.Total);
        Assert.Single(keywordSingle.Items);
        Assert.Equal(keywordFullReads, keywordSingleReads);
        Assert.Equal(0, counting.WriteCalls);
    }

    // ==================== 14. 接口端点 ====================

    [Fact]
    public async Task Controller_endpoint_returns_the_aging_report_payload()
    {
        using var db = TestDbFactory.Create();
        SeedProduct(db, Product1, "P001", "商品一");
        SeedWarehouse(db, WarehouseA, "主仓");
        SeedMovement(db, WarehouseA, Product1, AsOf.AddDays(-10), direction: 1, quantity: 10m, id: 1);
        SeedStock(db, WarehouseA, Product1, 10m, averageCost: 2m, totalCost: 20m);
        await db.SaveChangesAsync();

        var controller = new ReportController(Service(db));
        var result = await controller.InventoryAging(Query());

        var ok = Assert.IsType<OkObjectResult>(result);
        var payload = Assert.IsType<ApiResponse<ReportDtos.InventoryAgingReport>>(ok.Value);
        Assert.Equal(ErrorCodes.Success, payload.Code);

        var row = Assert.Single(payload.Data!.Items);
        Assert.Equal(10m, row.CurrentQuantity);
        Assert.Equal(10m, row.Buckets[0].Quantity);
        Assert.Equal(20m, row.AuthoritativeAmount);
        Assert.Equal(InventoryAgingSemantics.RuleText, payload.Data.Rule);
        Assert.Equal(InventoryAgingSemantics.CostCurrency, payload.Data.CostCurrency);
    }

    // ==================== 15. 前端接线（离线校验，不启动浏览器） ====================

    [Fact]
    public void Frontend_entry_page_and_api_are_wired_without_browser()
    {
        var js = JsDirectory();
        var reportJs = File.ReadAllText(Path.Combine(js, "inventory-aging-report.js"));

        Assert.Contains("function openInventoryAgingReport()", reportJs);
        Assert.Contains("/api/reports/inventory-aging?", reportJs);
        Assert.Contains("function loadIarWarehouses()", reportJs);
        Assert.Contains("function loadIarProductOptions(keyword)", reportJs);
        Assert.Contains("/api/base/products?page=1&pageSize=50", reportJs);
        Assert.Contains("function iarRenderTable(data)", reportJs);
        Assert.Contains("function iarRenderPageBuckets(data)", reportJs);
        Assert.Contains("function exportIarCsv()", reportJs);
        Assert.Contains("'未知'", reportJs);                          // 未知渲染：不回落为 0
        Assert.Contains("IAR_COST_LABELS", reportJs);
        Assert.Contains("IAR_EVIDENCE_LABELS", reportJs);

        // 筛选控件齐备：仓库 / 商品 / 关键字 / 截止日期 / 仅现存量 > 0 / 每页
        foreach (var id in new[] { "iar-warehouse", "iar-product", "iar-keyword", "iar-asof", "iar-only-positive", "iar-pagesize" })
        {
            Assert.Contains(id, reportJs);
        }

        // 工具栏入口（库存查询页）与脚本注册
        var modulesDoc2 = File.ReadAllText(Path.Combine(js, "modules-doc2.js"));
        Assert.Contains("openInventoryAgingReport", modulesDoc2);
        var index = File.ReadAllText(Path.Combine(js, "..", "index.html"));
        Assert.Contains("/js/inventory-aging-report.js", index);
    }

    // ==================== 助手 ====================

    private static ReportService Service(ErpDbContext db) => new(db);

    private static string JsDirectory() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "..", "..", "..", "..", "..", "src", "ERP.Api", "wwwroot", "js"));

    /// <summary>默认查询：截止 AsOf、仅现存量 &gt; 0、第一页 50 条</summary>
    private static ReportDtos.InventoryAgingReportQuery Query(DateTime? asOf = null, long? warehouseId = null,
        long? productId = null, string? keyword = null, bool onlyPositiveQuantity = true, int page = 1, int pageSize = 50)
        => new()
        {
            AsOfDate = asOf ?? AsOf,
            WarehouseId = warehouseId,
            ProductId = productId,
            Keyword = keyword,
            OnlyPositiveQuantity = onlyPositiveQuantity,
            Page = page,
            PageSize = pageSize
        };

    private static void SeedProduct(ErpDbContext db, long id, string code, string name)
        => db.BaseProducts.Add(new BaseProduct
        {
            Id = id, ProductCode = code, ProductName = name, Spec = "标准", Unit = "PCS"
        });

    private static void SeedWarehouse(ErpDbContext db, long id, string name)
        => db.BaseWarehouses.Add(new BaseWarehouse
        {
            Id = id, WarehouseCode = $"WH{id}", WarehouseName = name
        });

    /// <summary>写入库存行（现存量为基础单位；成本口径取自 ERP-009 的持久化加权平均成本与库存金额）</summary>
    private static void SeedStock(ErpDbContext db, long warehouseId, long productId, decimal quantity,
        decimal averageCost, decimal totalCost)
        => db.Stocks.Add(new Stock
        {
            WarehouseId = warehouseId,
            ProductId = productId,
            Quantity = quantity,
            AvailableQuantity = quantity,
            AverageCost = averageCost,
            TotalCost = totalCost
        });

    /// <summary>写入一条库存流水（方向 1=入库 / -1=出库；红字行通过 isReversal + reversalOf 表达）</summary>
    private static StockMovement SeedMovement(ErpDbContext db, long warehouseId, long productId, DateTime movementDate,
        int direction, decimal quantity, long id = 0, bool isReversal = false, long? reversalOf = null)
    {
        var movement = new StockMovement
        {
            Id = id,
            MovementDate = movementDate,
            MovementType = direction > 0 ? InventoryMovementType.PurchaseIn : InventoryMovementType.SalesOut,
            SourceDocType = direction > 0 ? "StockIn" : "StockOut",
            SourceDocId = 1,
            SourceDocNo = direction > 0 ? "SI-0001" : "SO-0001",
            WarehouseId = warehouseId,
            WarehouseName = "主仓",
            ProductId = productId,
            ProductCode = "P001",
            ProductName = "商品一",
            Spec = "标准",
            Unit = "PCS",
            Direction = direction,
            Quantity = quantity,
            UnitCost = 0m,
            Amount = 0m,
            IsReversal = isReversal,
            IsReversed = false,
            ReversalOfMovementId = reversalOf
        };
        db.StockMovements.Add(movement);
        return movement;
    }

    /// <summary>
    /// 只读计数上下文代理（<see cref="DispatchProxy"/>）：记录报表访问的数据集（<c>DbSet</c> 属性）名称与写入次数，
    /// 用于断言「分页 / 有界查询」「无逐行查库」「只读不写库」以及「不从文本数据字典取汇率」；不改动生产代码。
    /// </summary>
    public class CountingDbContext : DispatchProxy
    {
        private IErpDbContext _inner = null!;

        /// <summary>包装后的上下文（报表服务按 <see cref="IErpDbContext"/> 使用）</summary>
        public IErpDbContext Proxy { get; private set; } = null!;

        /// <summary>数据集（<c>DbSet</c> 属性）访问次数：即本次报表实际发起的数据集查询次数</summary>
        public int DatasetReads => ReadProperties.Count;

        /// <summary>被访问的数据集属性名（如 Stocks / StockMovements / BaseProducts）</summary>
        public List<string> ReadProperties { get; } = new();

        /// <summary><c>SaveChangesAsync</c> 调用次数：只读报表恒为 0</summary>
        public int WriteCalls { get; private set; }

        /// <summary>包装一个真实上下文（计数从返回对象上读取）</summary>
        public static CountingDbContext Wrap(IErpDbContext inner)
        {
            var proxy = DispatchProxy.Create<IErpDbContext, CountingDbContext>();
            var counting = (CountingDbContext)(object)proxy;
            counting._inner = inner;
            counting.Proxy = proxy;
            return counting;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod is null) return null;
            if (targetMethod.Name == nameof(IErpDbContext.SaveChangesAsync))
            {
                WriteCalls++;
                return _inner.SaveChangesAsync(args is { Length: > 0 } ? (CancellationToken)args[0]! : default);
            }

            if (targetMethod.Name.StartsWith("get_", StringComparison.Ordinal))
            {
                ReadProperties.Add(targetMethod.Name[4..]);
            }

            return targetMethod.Invoke(_inner, args);
        }
    }
}
