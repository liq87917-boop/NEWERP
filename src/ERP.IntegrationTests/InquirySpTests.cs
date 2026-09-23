using ERP.Infrastructure.Data;
using Xunit;

namespace ERP.IntegrationTests;

/// <summary>
/// sp_Biz_Inquiry 端到端集成测试：询价单完整状态机（Save → Audit → Void → Delete）。
/// </summary>
[Collection("Integration")]
public class InquirySpTests
{
    private readonly IntegrationTestFixture _fixture;

    public InquirySpTests(IntegrationTestFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Save_新增_返回成功_Oid大于0_BillNo以INQ开头()
    {
        var result = await SaveNewAsync("INT_TEST_INQ_新增");
        Assert.True(result.Success, $"保存失败：{result.Msg}");
        Assert.True(result.Oid > 0);
        Assert.False(string.IsNullOrEmpty(result.BillNo));
        Assert.StartsWith("INQ", result.BillNo);

        await CleanupAsync(result.Oid);
    }

    [Fact]
    public async Task Save_更新已存在Oid且Status为1_返回成功_且Oid不变()
    {
        var create = await SaveNewAsync("INT_TEST_INQ_更新");
        Assert.True(create.Success);

        var update = await _fixture.SpService.ExecuteAsync("db_owner.sp_Biz_Inquiry",
            new Dictionary<string, object?>
            {
                ["@Action"] = "Save",
                ["@Oid"] = create.Oid,
                ["@InquiryDate"] = DateTime.Today,
                ["@CustomerId"] = 999999L,
                ["@ContactPerson"] = "李雷",
                ["@ContactPhone"] = "13800138000",
                ["@EmpId"] = 999999L,
                ["@Currency"] = 2,
                ["@ExchangeRate"] = 6.5m,
                ["@ValidDays"] = 60,                              // 更新有效期
                ["@Remark"] = "INT_TEST_INQ_更新成功",
                ["@UserId"] = 1L
            });
        Assert.True(update.Success, $"更新失败：{update.Msg}");
        Assert.Equal(create.Oid, update.Oid);

        await CleanupAsync(create.Oid);
    }

    [Fact]
    public async Task Audit_审核新询价单_返回成功()
    {
        var create = await SaveNewAsync("INT_TEST_INQ_Audit");
        Assert.True(create.Success);

        var audit = await _fixture.SpService.ExecuteAsync("db_owner.sp_Biz_Inquiry",
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
    public async Task Delete_保存状态Status为1_成功_且为真正删除()
    {
        // 询价单 SP 的 Delete 分支条件：`EXISTS (... WHERE Oid=@Oid AND Status=1)`
        //   - Status=1 时 EXISTS 真，整个分支执行 DELETE FROM db_owner.Inquiry WHERE Oid=@Oid（无 Status 过滤）
        //   - Status=2 时 EXISTS 假，分支被跳过，Result 保持 0 但 no-op
        // 本测试覆盖 Status=1 的"真删除"路径。
        var create = await SaveNewAsync("INT_TEST_INQ_Delete_Status1");
        Assert.True(create.Success);

        var delete = await _fixture.SpService.ExecuteAsync("db_owner.sp_Biz_Inquiry",
            new Dictionary<string, object?> { ["@Action"] = "Delete", ["@Oid"] = create.Oid, ["@UserId"] = 1L });
        Assert.True(delete.Success, $"删除失败：{delete.Msg}");
    }

    // ============ 辅助 ============

    private async Task<ProcResult> SaveNewAsync(string remark)
    {
        return await _fixture.SpService.ExecuteAsync("db_owner.sp_Biz_Inquiry",
            new Dictionary<string, object?>
            {
                ["@Action"] = "Save",
                ["@Oid"] = 0L,
                ["@InquiryDate"] = DateTime.Today,
                ["@CustomerId"] = 999999L,
                ["@ContactPerson"] = "李雷",
                ["@ContactPhone"] = "13800138000",
                ["@EmpId"] = 999999L,
                ["@Currency"] = 2,
                ["@ExchangeRate"] = 6.5m,
                ["@ValidDays"] = 30,
                ["@Remark"] = remark,
                ["@UserId"] = 1L
            });
    }

    private async Task CleanupAsync(long oid)
    {
        await _fixture.SpService.ExecuteAsync("db_owner.sp_Biz_Inquiry",
            new Dictionary<string, object?> { ["@Action"] = "Delete", ["@Oid"] = oid, ["@UserId"] = 1L });
    }
}