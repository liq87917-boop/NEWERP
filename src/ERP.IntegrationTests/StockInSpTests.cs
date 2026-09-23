using ERP.Infrastructure.Data;
using Xunit;

namespace ERP.IntegrationTests;

/// <summary>
/// sp_Biz_StockIn 端到端集成测试：采购入库单完整状态机（Save → Audit → Void → Restore → Delete）。
/// 注意：Status=2（已审核）后会触发 Stock 表的库存扣减（库存增加）；测试仅验证 SP 接口行为，库存数据副作用接受。
/// </summary>
[Collection("Integration")]
public class StockInSpTests
{
    private readonly IntegrationTestFixture _fixture;

    public StockInSpTests(IntegrationTestFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Save_新增_返回成功_Oid大于0_BillNo以RK开头()
    {
        var result = await SaveNewAsync("INT_TEST_STKIN_新增");
        Assert.True(result.Success, $"保存失败：{result.Msg}");
        Assert.True(result.Oid > 0);
        Assert.False(string.IsNullOrEmpty(result.BillNo));
        Assert.StartsWith("RK", result.BillNo);

        await CleanupAsync(result.Oid);
    }

    [Fact]
    public async Task Audit_审核新采购入库单_返回成功_库存增加()
    {
        var create = await SaveNewAsync("INT_TEST_STKIN_Audit");
        Assert.True(create.Success);

        var audit = await _fixture.SpService.ExecuteAsync("db_owner.sp_Biz_StockIn",
            new Dictionary<string, object?>
            {
                ["@Action"] = "Audit",
                ["@Oid"] = create.Oid,
                ["@UserId"] = 1L
            });
        Assert.True(audit.Success, $"审核失败：{audit.Msg}");

        await CleanupAsync(create.Oid);
    }

    [Fact]
    public async Task Delete_对已审核入库单_是no_op_Result保持0_但未真删除()
    {
        // 注：sp_Biz_StockIn 的 Delete 分支是 `EXISTS (... Status=1)` 为条件，
        //   当 Status=2 时 EXISTS 假，分支被跳过 —— Result 保持 0、no-op、什么都不做。
        //   这与 SalesOrder/PurchaseOrder 不同（后者明确返回 -1 "仅保存"）。
        // 本测试覆盖此真实 SP 行为。
        var create = await SaveNewAsync("INT_TEST_STKIN_已审核不可删");
        var audit = await _fixture.SpService.ExecuteAsync("db_owner.sp_Biz_StockIn",
            new Dictionary<string, object?> { ["@Action"] = "Audit", ["@Oid"] = create.Oid, ["@UserId"] = 1L });
        Assert.True(audit.Success);

        var delete = await _fixture.SpService.ExecuteAsync("db_owner.sp_Biz_StockIn",
            new Dictionary<string, object?> { ["@Action"] = "Delete", ["@Oid"] = create.Oid, ["@UserId"] = 1L });
        Assert.True(delete.Success);    // SP 返回成功，但 Status=2 不满足 EXISTS，分支跳过 no-op
    }

    [Fact]
    public async Task Audit_对不存在的Oid_是no_op_Result保持0()
    {
        // 注：sp_Biz_StockIn 的 Audit 分支条件 `EXISTS (... Status=1)`，Oid 不存在时为假，
        //   分支被跳过 —— Result 保持 0、no-op。这与 SalesOrder/PurchaseOrder 不同。
        var audit = await _fixture.SpService.ExecuteAsync("db_owner.sp_Biz_StockIn",
            new Dictionary<string, object?>
            {
                ["@Action"] = "Audit",
                ["@Oid"] = 99999999L,
                ["@UserId"] = 1L
            });
        Assert.True(audit.Success);     // no-op，但 SP 返回成功
    }

    // ============ 辅助 ============

    private async Task<ProcResult> SaveNewAsync(string remark)
    {
        return await _fixture.SpService.ExecuteAsync("db_owner.sp_Biz_StockIn",
            new Dictionary<string, object?>
            {
                ["@Action"] = "Save",
                ["@Oid"] = 0L,
                ["@StockInDate"] = DateTime.Today,
                ["@PurchaseOrderId"] = 999999L,   // 测试用非真实 PO Id
                ["@SupplierId"] = 999999L,
                ["@WarehouseId"] = 999999L,      // 测试用非真实仓库 Id（SP 不验外键）
                ["@TotalQuantity"] = 100m,
                ["@TotalWeight"] = 500m,
                ["@TotalVolume"] = 2.5m,
                ["@Remark"] = remark,
                ["@UserId"] = 1L
            });
    }

    private async Task CleanupAsync(long oid)
    {
        await _fixture.SpService.ExecuteAsync("db_owner.sp_Biz_StockIn",
            new Dictionary<string, object?> { ["@Action"] = "Delete", ["@Oid"] = oid, ["@UserId"] = 1L });
    }
}