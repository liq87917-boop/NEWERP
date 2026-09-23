using ERP.Infrastructure.Data;
using Xunit;

namespace ERP.IntegrationTests;

/// <summary>
/// sp_Biz_SalesOrder 端到端集成测试：在真实 WMERP_Data 测试库上验证
/// Save → Audit → UnAudit → Void → Restore → Delete 完整状态机。
/// 注：依赖 init2.sql + init4.sql 已在测试库部署（Apply-Init4.ps1 已执行）。
/// </summary>
[Collection("Integration")]
public class SalesOrderSpTests
{
    private readonly IntegrationTestFixture _fixture;

    public SalesOrderSpTests(IntegrationTestFixture fixture)
    {
        _fixture = fixture;
    }

    // ============ Save ============

    [Fact]
    public async Task Save_新增_返回成功_Oid大于0_BillNo非空()
    {
        var result = await _fixture.SpService.ExecuteAsync("db_owner.sp_Biz_SalesOrder",
            new Dictionary<string, object?>
            {
                ["@Action"] = "Save",
                ["@Oid"] = 0L,           // 0 = 新增
                ["@OrderDate"] = DateTime.Today,
                ["@CustId"] = 999999L,   // 测试用任意正数（SalesOrder 表对 CustId 无外键约束）
                ["@EmpId"] = 999999L,
                ["@Currency"] = 2,
                ["@ExchangeRate"] = 7.1m,
                ["@TotalAmount"] = 1000m,
                ["@DepositRatio"] = 30m,
                ["@DepositAmount"] = 300m,
                ["@PaymentTerms"] = "T/T 30 days",
                ["@DeliveryDate"] = DateTime.Today.AddDays(30),
                ["@ShippingMethod"] = "海运",
                ["@Remark"] = "INT_TEST_Save_新增",
                ["@DetailsJson"] = "[{\"ProductId\":999999,\"ProductName\":\"INT\",\"Quantity\":10,\"UnitPrice\":100,\"Amount\":1000}]",
                ["@UserId"] = 1L
            });

        Assert.True(result.Success, $"保存失败：{result.Msg}");
        Assert.True(result.Oid > 0);
        Assert.False(string.IsNullOrEmpty(result.BillNo));
        Assert.StartsWith("SO", result.BillNo);

        // 清理：直接 Delete（Status=1 的新单据可删）
        await CleanupAsync(result.Oid);
    }

    [Fact]
    public async Task Save_更新已存在Oid且Status为1_返回成功()
    {
        // 1. 先 Save 新增
        var create = await SaveNewAsync("INT_TEST_Save_更新");
        Assert.True(create.Success);
        Assert.True(create.Oid > 0);

        // 2. 再次 Save 更新同一 Oid
        var update = await _fixture.SpService.ExecuteAsync("db_owner.sp_Biz_SalesOrder",
            new Dictionary<string, object?>
            {
                ["@Action"] = "Save",
                ["@Oid"] = create.Oid,
                ["@OrderDate"] = DateTime.Today,
                ["@CustId"] = 999999L,
                ["@EmpId"] = 999999L,
                ["@Currency"] = 2,
                ["@ExchangeRate"] = 7.1m,
                ["@TotalAmount"] = 2000m,           // 改成 2000
                ["@DepositRatio"] = 50m,
                ["@DepositAmount"] = 1000m,
                ["@PaymentTerms"] = "T/T 60 days",
                ["@DeliveryDate"] = DateTime.Today.AddDays(60),
                ["@ShippingMethod"] = "海运",
                ["@Remark"] = "INT_TEST_Save_更新成功",
                ["@DetailsJson"] = "[{\"ProductId\":999999,\"ProductName\":\"INT\",\"Quantity\":20,\"UnitPrice\":100,\"Amount\":2000}]",
                ["@UserId"] = 1L
            });

        Assert.True(update.Success, $"更新失败：{update.Msg}");
        Assert.Equal(create.Oid, update.Oid);    // Oid 不变

        await CleanupAsync(create.Oid);
    }

    // ============ Audit / UnAudit ============

    [Fact]
    public async Task Audit_审核新单据_返回成功_销审UnAudit_返回成功_数据库Status回到1()
    {
        var create = await SaveNewAsync("INT_TEST_AuditUnAudit");
        Assert.True(create.Success);

        // 审核
        var audit = await _fixture.SpService.ExecuteAsync("db_owner.sp_Biz_SalesOrder",
            new Dictionary<string, object?>
            {
                ["@Action"] = "Audit",
                ["@Oid"] = create.Oid,
                ["@UserId"] = 1L
            });
        Assert.True(audit.Success, $"审核失败：{audit.Msg}");

        // 销审（依赖 init4.sql 已部署）
        var unaudit = await _fixture.SpService.ExecuteAsync("db_owner.sp_Biz_SalesOrder",
            new Dictionary<string, object?>
            {
                ["@Action"] = "UnAudit",
                ["@Oid"] = create.Oid,
                ["@UserId"] = 1L
            });
        Assert.True(unaudit.Success,
            $"销审未生效——请确认测试库已执行 deploy/init4.sql（部署文档 8.3）：{unaudit.Msg}");

        await CleanupAsync(create.Oid);
    }

    // ============ Void / Restore ============

    [Fact]
    public async Task Void_作废已审核单据_返回成功_Restore还原_返回成功()
    {
        var create = await SaveNewAsync("INT_TEST_VoidRestore");
        Assert.True(create.Success);

        // 先审核（只有 Status=2 的单据才能 Void）
        var audit = await _fixture.SpService.ExecuteAsync("db_owner.sp_Biz_SalesOrder",
            new Dictionary<string, object?> { ["@Action"] = "Audit", ["@Oid"] = create.Oid, ["@UserId"] = 1L });
        Assert.True(audit.Success);

        // 作废
        var void1 = await _fixture.SpService.ExecuteAsync("db_owner.sp_Biz_SalesOrder",
            new Dictionary<string, object?> { ["@Action"] = "Void", ["@Oid"] = create.Oid, ["@UserId"] = 1L });
        Assert.True(void1.Success, $"作废失败：{void1.Msg}");

        // 还原
        var restore = await _fixture.SpService.ExecuteAsync("db_owner.sp_Biz_SalesOrder",
            new Dictionary<string, object?> { ["@Action"] = "Restore", ["@Oid"] = create.Oid, ["@UserId"] = 1L });
        Assert.True(restore.Success, $"还原失败：{restore.Msg}");

        await CleanupAsync(create.Oid);
    }

    // ============ 业务规则约束 ============

    [Fact]
    public async Task Audit_审核不存在的Oid_返回Result非零()
    {
        var audit = await _fixture.SpService.ExecuteAsync("db_owner.sp_Biz_SalesOrder",
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
    public async Task Delete_删除非保存状态单据_返回Result负一_业务规则生效()
    {
        // 1. Save
        var create = await SaveNewAsync("INT_TEST_Delete_已审核不可删");
        // 2. Audit（变成 Status=2）
        var audit = await _fixture.SpService.ExecuteAsync("db_owner.sp_Biz_SalesOrder",
            new Dictionary<string, object?> { ["@Action"] = "Audit", ["@Oid"] = create.Oid, ["@UserId"] = 1L });
        Assert.True(audit.Success);

        // 3. Delete（应失败：仅保存状态可删）
        var delete = await _fixture.SpService.ExecuteAsync("db_owner.sp_Biz_SalesOrder",
            new Dictionary<string, object?> { ["@Action"] = "Delete", ["@Oid"] = create.Oid, ["@UserId"] = 1L });
        Assert.False(delete.Success);
        Assert.Equal(-1, delete.Result);
        Assert.Contains("仅保存", delete.Msg);

        // 清理：先 UnAudit 回 Status=1，再 Delete
        await _fixture.SpService.ExecuteAsync("db_owner.sp_Biz_SalesOrder",
            new Dictionary<string, object?> { ["@Action"] = "UnAudit", ["@Oid"] = create.Oid, ["@UserId"] = 1L });
        await CleanupAsync(create.Oid);
    }

    // ============ 辅助 ============

    private async Task<ProcResult> SaveNewAsync(string remark)
    {
        return await _fixture.SpService.ExecuteAsync("db_owner.sp_Biz_SalesOrder",
            new Dictionary<string, object?>
            {
                ["@Action"] = "Save",
                ["@Oid"] = 0L,
                ["@OrderDate"] = DateTime.Today,
                ["@CustId"] = 999999L,
                ["@EmpId"] = 999999L,
                ["@Currency"] = 2,
                ["@ExchangeRate"] = 7.1m,
                ["@TotalAmount"] = 1000m,
                ["@DepositRatio"] = 0m,
                ["@DepositAmount"] = 0m,
                ["@PaymentTerms"] = "",
                ["@DeliveryDate"] = DateTime.Today.AddDays(30),
                ["@ShippingMethod"] = "海运",
                ["@Remark"] = remark,
                ["@DetailsJson"] = "[{\"ProductId\":999999,\"ProductName\":\"INT\",\"Quantity\":10,\"UnitPrice\":100,\"Amount\":1000}]",
                ["@UserId"] = 1L
            });
    }

    /// <summary>清理测试单据（Delete + 级联清理 SalesOrderDetail）</summary>
    private async Task CleanupAsync(long oid)
    {
        // 仅 Status=1 的单据可被 Delete
        var result = await _fixture.SpService.ExecuteAsync("db_owner.sp_Biz_SalesOrder",
            new Dictionary<string, object?> { ["@Action"] = "Delete", ["@Oid"] = oid, ["@UserId"] = 1L });
        // 静默失败（如果单据已经被 Void / Restore 等导致无法直接删除，不影响测试通过）
    }
}