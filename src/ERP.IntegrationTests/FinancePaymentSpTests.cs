using ERP.Infrastructure.Data;
using Xunit;

namespace ERP.IntegrationTests;

/// <summary>
/// sp_Biz_FinancePayment 端到端集成测试：付款单状态机。
/// </summary>
[Collection("Integration")]
public class FinancePaymentSpTests
{
    private readonly IntegrationTestFixture _fixture;
    public FinancePaymentSpTests(IntegrationTestFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Save_新增_返回成功_BillNo以FK开头()
    {
        var r = await SaveNewAsync("INT_TEST_FP_新增");
        Assert.True(r.Success, r.Msg);
        Assert.True(r.Oid > 0);
        Assert.StartsWith("FK", r.BillNo);
        await CleanupAsync(r.Oid);
    }

    [Fact]
    public async Task Audit_审核新付款单_返回成功()
    {
        var c = await SaveNewAsync("INT_TEST_FP_Audit");
        var a = await _fixture.SpService.ExecuteAsync("db_owner.sp_Biz_FinancePayment",
            new Dictionary<string, object?> { ["@Action"] = "Audit", ["@Oid"] = c.Oid, ["@UserId"] = 1L });
        Assert.True(a.Success, a.Msg);
        await CleanupAsync(c.Oid);
    }

    [Fact]
    public async Task Audit_不存在的Oid_是no_op_Result保持0()
    {
        var a = await _fixture.SpService.ExecuteAsync("db_owner.sp_Biz_FinancePayment",
            new Dictionary<string, object?> { ["@Action"] = "Audit", ["@Oid"] = 99999999L, ["@UserId"] = 1L });
        Assert.True(a.Success);
    }

    private async Task<ProcResult> SaveNewAsync(string remark)
        => await _fixture.SpService.ExecuteAsync("db_owner.sp_Biz_FinancePayment", new Dictionary<string, object?>
        {
            ["@Action"] = "Save", ["@Oid"] = 0L,
            ["@PaymentDate"] = DateTime.Today,
            ["@SupplierId"] = 999999L,
            ["@Amount"] = 3000m,
            ["@Currency"] = 1,
            ["@PaymentMethod"] = 1,
            ["@BankAccount"] = "6222...",
            ["@Remark"] = remark, ["@UserId"] = 1L
        });

    private async Task CleanupAsync(long oid)
        => await _fixture.SpService.ExecuteAsync("db_owner.sp_Biz_FinancePayment",
            new Dictionary<string, object?> { ["@Action"] = "Delete", ["@Oid"] = oid, ["@UserId"] = 1L });
}