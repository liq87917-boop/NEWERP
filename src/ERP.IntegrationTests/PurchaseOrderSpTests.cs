using ERP.Infrastructure.Data;
using Xunit;

namespace ERP.IntegrationTests;

/// <summary>
/// sp_Biz_PurchaseOrder 端到端集成测试：在真实 WMERP_Data 测试库上验证
/// Save → Audit → Void → Restore → Delete 完整状态机（采购订单 SP 无 UnAudit 分支）。
/// 注：依赖 init2.sql + init4.sql 已在测试库部署。
/// </summary>
[Collection("Integration")]
public class PurchaseOrderSpTests
{
    private readonly IntegrationTestFixture _fixture;

    public PurchaseOrderSpTests(IntegrationTestFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Save_新增_返回成功_Oid大于0_BillNo以PO开头()
    {
        var result = await _fixture.SpService.ExecuteAsync("db_owner.sp_Biz_PurchaseOrder",
            new Dictionary<string, object?>
            {
                ["@Action"] = "Save",
                ["@Oid"] = 0L,
                ["@OrderDate"] = DateTime.Today,
                ["@SupplierId"] = 999999L,
                ["@EmpId"] = 999999L,
                ["@Currency"] = 1,
                ["@ExchangeRate"] = 1m,
                ["@TotalAmount"] = 5000m,
                ["@PaymentTerms"] = "Net 30",
                ["@DeliveryDate"] = DateTime.Today.AddDays(45),
                ["@Remark"] = "INT_TEST_PO_新增",
                ["@UserId"] = 1L
            });

        Assert.True(result.Success, $"保存失败：{result.Msg}");
        Assert.True(result.Oid > 0);
        Assert.False(string.IsNullOrEmpty(result.BillNo));
        Assert.StartsWith("PO", result.BillNo);

        await CleanupAsync(result.Oid);
    }

    [Fact]
    public async Task Audit_审核新采购单_返回成功()
    {
        var create = await SaveNewAsync("INT_TEST_PO_Audit");
        Assert.True(create.Success);

        var audit = await _fixture.SpService.ExecuteAsync("db_owner.sp_Biz_PurchaseOrder",
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
    public async Task Void_作废已审核采购单_返回成功_Restore还原_返回成功()
    {
        var create = await SaveNewAsync("INT_TEST_PO_VoidRestore");
        Assert.True(create.Success);

        var audit = await _fixture.SpService.ExecuteAsync("db_owner.sp_Biz_PurchaseOrder",
            new Dictionary<string, object?> { ["@Action"] = "Audit", ["@Oid"] = create.Oid, ["@UserId"] = 1L });
        Assert.True(audit.Success);

        var void1 = await _fixture.SpService.ExecuteAsync("db_owner.sp_Biz_PurchaseOrder",
            new Dictionary<string, object?> { ["@Action"] = "Void", ["@Oid"] = create.Oid, ["@UserId"] = 1L });
        Assert.True(void1.Success, $"作废失败：{void1.Msg}");

        var restore = await _fixture.SpService.ExecuteAsync("db_owner.sp_Biz_PurchaseOrder",
            new Dictionary<string, object?> { ["@Action"] = "Restore", ["@Oid"] = create.Oid, ["@UserId"] = 1L });
        Assert.True(restore.Success, $"还原失败：{restore.Msg}");

        await CleanupAsync(create.Oid);
    }

    [Fact]
    public async Task Audit_审核不存在的Oid_返回Result非零()
    {
        var audit = await _fixture.SpService.ExecuteAsync("db_owner.sp_Biz_PurchaseOrder",
            new Dictionary<string, object?>
            {
                ["@Action"] = "Audit",
                ["@Oid"] = 99999999L,
                ["@UserId"] = 1L
            });
        Assert.False(audit.Success);
        Assert.NotEqual(0, audit.Result);
        Assert.False(string.IsNullOrEmpty(audit.Msg));
    }

    [Fact]
    public async Task Delete_删除非保存状态采购单_返回Result负一()
    {
        var create = await SaveNewAsync("INT_TEST_PO_已审核不可删");
        var audit = await _fixture.SpService.ExecuteAsync("db_owner.sp_Biz_PurchaseOrder",
            new Dictionary<string, object?> { ["@Action"] = "Audit", ["@Oid"] = create.Oid, ["@UserId"] = 1L });
        Assert.True(audit.Success);

        var delete = await _fixture.SpService.ExecuteAsync("db_owner.sp_Biz_PurchaseOrder",
            new Dictionary<string, object?> { ["@Action"] = "Delete", ["@Oid"] = create.Oid, ["@UserId"] = 1L });
        Assert.False(delete.Success);
        Assert.Equal(-1, delete.Result);
        Assert.Contains("仅保存", delete.Msg);

        await CleanupAsync(create.Oid);
    }

    // ============ 辅助 ============

    private async Task<ProcResult> SaveNewAsync(string remark)
    {
        return await _fixture.SpService.ExecuteAsync("db_owner.sp_Biz_PurchaseOrder",
            new Dictionary<string, object?>
            {
                ["@Action"] = "Save",
                ["@Oid"] = 0L,
                ["@OrderDate"] = DateTime.Today,
                ["@SupplierId"] = 999999L,
                ["@EmpId"] = 999999L,
                ["@Currency"] = 1,
                ["@ExchangeRate"] = 1m,
                ["@TotalAmount"] = 5000m,
                ["@PaymentTerms"] = "",
                ["@DeliveryDate"] = DateTime.Today.AddDays(45),
                ["@Remark"] = remark,
                ["@UserId"] = 1L
            });
    }

    private async Task CleanupAsync(long oid)
    {
        await _fixture.SpService.ExecuteAsync("db_owner.sp_Biz_PurchaseOrder",
            new Dictionary<string, object?> { ["@Action"] = "Delete", ["@Oid"] = oid, ["@UserId"] = 1L });
    }
}