using ERP.Infrastructure.Data;
using Xunit;

namespace ERP.IntegrationTests;

/// <summary>
/// sp_Biz_FinancePaymentApply 端到端集成测试：货款申请单状态机。
/// 参数同 FinanceDepositApply（Payee / Reason），业务区别：SalesOrderId 必传。
/// </summary>
[Collection("Integration")]
public class FinancePaymentApplySpTests
{
    private readonly IntegrationTestFixture _fixture;
    public FinancePaymentApplySpTests(IntegrationTestFixture fixture) => _fixture = fixture;

    [Fact(Skip = "测试库当前未部署 init3.sql（FinancePaymentApply 表缺失），待测试库补完 init3.sql 后启用")]
    public async Task Save_新增_返回成功_BillNo以DJ开头()
    {
        var r = await SaveNewAsync("INT_TEST_FPA_新增");
        Assert.True(r.Success, r.Msg);
        Assert.True(r.Oid > 0);
        Assert.StartsWith("DJ", r.BillNo);
        await CleanupAsync(r.Oid);
    }

    [Fact(Skip = "测试库当前未部署 init3.sql（FinancePaymentApply 表缺失），待测试库补完 init3.sql 后启用")]
    public async Task Audit_审核新货款申请单_返回成功()
    {
        var c = await SaveNewAsync("INT_TEST_FPA_Audit");
        var a = await _fixture.SpService.ExecuteAsync("db_owner.sp_Biz_FinancePaymentApply",
            new Dictionary<string, object?> { ["@Action"] = "Audit", ["@Oid"] = c.Oid, ["@UserId"] = 1L });
        Assert.True(a.Success, a.Msg);
        await CleanupAsync(c.Oid);
    }

    [Fact(Skip = "测试库当前未部署 init3.sql（FinancePaymentApply 表缺失），待测试库补完 init3.sql 后启用")]
    public async Task Audit_不存在的Oid_是no_op_Result保持0()
    {
        var a = await _fixture.SpService.ExecuteAsync("db_owner.sp_Biz_FinancePaymentApply",
            new Dictionary<string, object?> { ["@Action"] = "Audit", ["@Oid"] = 99999999L, ["@UserId"] = 1L });
        Assert.True(a.Success);
    }

    private async Task<ProcResult> SaveNewAsync(string remark)
        => await _fixture.SpService.ExecuteAsync("db_owner.sp_Biz_FinancePaymentApply", new Dictionary<string, object?>
        {
            ["@Action"] = "Save", ["@Oid"] = 0L,
            ["@ApplyDate"] = DateTime.Today,
            ["@SalesOrderId"] = 999999L,
            ["@CustomerId"] = 999999L,
            ["@Amount"] = 2500m, ["@Currency"] = 2, ["@ExchangeRate"] = 7.1m,
            ["@BankAccount"] = "6222...", ["@Payee"] = "测试收款方",
            ["@Reason"] = "货款",
            ["@Remark"] = remark, ["@UserId"] = 1L
        });

    private async Task CleanupAsync(long oid)
        => await _fixture.SpService.ExecuteAsync("db_owner.sp_Biz_FinancePaymentApply",
            new Dictionary<string, object?> { ["@Action"] = "Delete", ["@Oid"] = oid, ["@UserId"] = 1L });
}