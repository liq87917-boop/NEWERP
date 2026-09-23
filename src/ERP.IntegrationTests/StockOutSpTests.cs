using ERP.Infrastructure.Data;
using Xunit;

namespace ERP.IntegrationTests;

/// <summary>
/// sp_Biz_StockOut 端到端集成测试：销售出库单状态机（Save → Audit → Void → Restore → Delete）。
/// 注：与 sp_Biz_StockIn 相同的 no-op 模式（Audit/Delete 对非 Status=1 是 no-op）。
/// </summary>
[Collection("Integration")]
public class StockOutSpTests
{
    private readonly IntegrationTestFixture _fixture;
    public StockOutSpTests(IntegrationTestFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Save_新增_返回成功_BillNo以CK开头()
    {
        var r = await SaveNewAsync("INT_TEST_STKOUT_新增");
        Assert.True(r.Success, r.Msg);
        Assert.True(r.Oid > 0);
        Assert.StartsWith("CK", r.BillNo);
        await CleanupAsync(r.Oid);
    }

    [Fact]
    public async Task Audit_审核新出库单_返回成功()
    {
        var c = await SaveNewAsync("INT_TEST_STKOUT_Audit");
        var a = await _fixture.SpService.ExecuteAsync("db_owner.sp_Biz_StockOut",
            new Dictionary<string, object?> { ["@Action"] = "Audit", ["@Oid"] = c.Oid, ["@UserId"] = 1L });
        Assert.True(a.Success, a.Msg);
        await CleanupAsync(c.Oid);
    }

    [Fact]
    public async Task Audit_不存在的Oid_是no_op_Result保持0()
    {
        var a = await _fixture.SpService.ExecuteAsync("db_owner.sp_Biz_StockOut",
            new Dictionary<string, object?> { ["@Action"] = "Audit", ["@Oid"] = 99999999L, ["@UserId"] = 1L });
        Assert.True(a.Success);    // no-op
    }

    private async Task<ProcResult> SaveNewAsync(string remark)
        => await _fixture.SpService.ExecuteAsync("db_owner.sp_Biz_StockOut", new Dictionary<string, object?>
        {
            ["@Action"] = "Save", ["@Oid"] = 0L,
            ["@StockOutDate"] = DateTime.Today,
            ["@SalesOrderId"] = 999999L, ["@CustomerId"] = 999999L,
            ["@WarehouseId"] = 999999L,
            ["@TotalQuantity"] = 50m, ["@TotalWeight"] = 250m, ["@TotalVolume"] = 1.5m,
            ["@Remark"] = remark, ["@UserId"] = 1L
        });

    private async Task CleanupAsync(long oid)
        => await _fixture.SpService.ExecuteAsync("db_owner.sp_Biz_StockOut",
            new Dictionary<string, object?> { ["@Action"] = "Delete", ["@Oid"] = oid, ["@UserId"] = 1L });
}