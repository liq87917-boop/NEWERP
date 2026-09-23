using ERP.Infrastructure.Data;
using Xunit;

namespace ERP.IntegrationTests;

/// <summary>
/// sp_Biz_FinanceDepositApply 端到端集成测试：定金申请单状态机。
/// </summary>
[Collection("Integration")]
public class FinanceDepositApplySpTests
{
    private readonly IntegrationTestFixture _fixture;
    public FinanceDepositApplySpTests(IntegrationTestFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Save_新增_返回成功_BillNo以DJ开头()
    {
        var r = await SaveNewAsync("INT_TEST_FD_新增");
        Assert.True(r.Success, r.Msg);
        Assert.True(r.Oid > 0);
        Assert.StartsWith("DJ", r.BillNo);
        await CleanupAsync(r.Oid);
    }

    [Fact]
    public async Task Audit_审核新定金申请单_返回成功()
    {
        var c = await SaveNewAsync("INT_TEST_FD_Audit");
        var a = await _fixture.SpService.ExecuteAsync("db_owner.sp_Biz_FinanceDepositApply",
            new Dictionary<string, object?> { ["@Action"] = "Audit", ["@Oid"] = c.Oid, ["@UserId"] = 1L });
        Assert.True(a.Success, a.Msg);
        await CleanupAsync(c.Oid);
    }

    [Fact]
    public async Task Audit_不存在的Oid_是no_op_Result保持0()
    {
        var a = await _fixture.SpService.ExecuteAsync("db_owner.sp_Biz_FinanceDepositApply",
            new Dictionary<string, object?> { ["@Action"] = "Audit", ["@Oid"] = 99999999L, ["@UserId"] = 1L });
        Assert.True(a.Success);
    }

    private async Task<ProcResult> SaveNewAsync(string remark)
        => await _fixture.SpService.ExecuteAsync("db_owner.sp_Biz_FinanceDepositApply", new Dictionary<string, object?>
        {
            ["@Action"] = "Save", ["@Oid"] = 0L,
            ["@ApplyDate"] = DateTime.Today,
            ["@CustomerId"] = 999999L,
            ["@SalesOrderId"] = 999999L,
            ["@Amount"] = 1500m,
            ["@Currency"] = 2,
            ["@Payee"] = "测试收款方",
            ["@Reason"] = "定金",
            ["@Remark"] = remark, ["@UserId"] = 1L
        });

    private async Task CleanupAsync(long oid)
        => await _fixture.SpService.ExecuteAsync("db_owner.sp_Biz_FinanceDepositApply",
            new Dictionary<string, object?> { ["@Action"] = "Delete", ["@Oid"] = oid, ["@UserId"] = 1L });
}