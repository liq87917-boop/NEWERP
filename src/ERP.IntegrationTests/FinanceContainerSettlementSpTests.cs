using ERP.Infrastructure.Data;
using Xunit;

namespace ERP.IntegrationTests;

/// <summary>
/// sp_Biz_FinanceContainerSettlement 端到端集成测试：装柜结算单。
/// </summary>
[Collection("Integration")]
public class FinanceContainerSettlementSpTests
{
    private readonly IntegrationTestFixture _fixture;
    public FinanceContainerSettlementSpTests(IntegrationTestFixture fixture) => _fixture = fixture;

    [Fact(Skip = "测试库当前未部署 init3.sql（FinanceContainerSettlement 表缺失），待测试库补完 init3.sql 后启用")]
    public async Task Save_新增_返回成功_BillNo以ZGS开头()
    {
        var r = await SaveNewAsync("INT_TEST_FCS_新增");
        Assert.True(r.Success, r.Msg);
        Assert.True(r.Oid > 0);
        Assert.StartsWith("ZGS", r.BillNo);
        await CleanupAsync(r.Oid);
    }

    [Fact(Skip = "测试库当前未部署 init3.sql（FinanceContainerSettlement 表缺失），待测试库补完 init3.sql 后启用")]
    public async Task Audit_审核新装柜结算_返回成功()
    {
        var c = await SaveNewAsync("INT_TEST_FCS_Audit");
        var a = await _fixture.SpService.ExecuteAsync("db_owner.sp_Biz_FinanceContainerSettlement",
            new Dictionary<string, object?> { ["@Action"] = "Audit", ["@Oid"] = c.Oid, ["@UserId"] = 1L });
        Assert.True(a.Success, a.Msg);
        await CleanupAsync(c.Oid);
    }

    [Fact(Skip = "测试库当前未部署 init3.sql（FinanceContainerSettlement 表缺失），待测试库补完 init3.sql 后启用")]
    public async Task Audit_不存在的Oid_是no_op_Result保持0()
    {
        var a = await _fixture.SpService.ExecuteAsync("db_owner.sp_Biz_FinanceContainerSettlement",
            new Dictionary<string, object?> { ["@Action"] = "Audit", ["@Oid"] = 99999999L, ["@UserId"] = 1L });
        Assert.True(a.Success);
    }

    private async Task<ProcResult> SaveNewAsync(string remark)
        => await _fixture.SpService.ExecuteAsync("db_owner.sp_Biz_FinanceContainerSettlement", new Dictionary<string, object?>
        {
            ["@Action"] = "Save", ["@Oid"] = 0L,
            ["@SettlementDate"] = DateTime.Today,
            ["@LoadingListId"] = 999999L,
            ["@CustomerId"] = 999999L,
            ["@TotalAmount"] = 10000m, ["@FreightCost"] = 800m, ["@OtherCost"] = 200m,
            ["@Remark"] = remark, ["@UserId"] = 1L
        });

    private async Task CleanupAsync(long oid)
        => await _fixture.SpService.ExecuteAsync("db_owner.sp_Biz_FinanceContainerSettlement",
            new Dictionary<string, object?> { ["@Action"] = "Delete", ["@Oid"] = oid, ["@UserId"] = 1L });
}