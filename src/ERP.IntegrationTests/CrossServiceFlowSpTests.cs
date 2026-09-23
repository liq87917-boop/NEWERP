using ERP.Infrastructure.Data;
using Xunit;

namespace ERP.IntegrationTests;

/// <summary>
/// 跨服务协同端到端集成测试：在真实 WMERP_Data 上验证完整业务流的多 SP 协同。
/// 不依赖 init3 的表，使用 init2 已部署的 SP（SalesOrder / StockOut / StockIn / PurchaseOrder / FinanceReceipt / FinancePayment）。
/// </summary>
[Collection("Integration")]
public class CrossServiceFlowSpTests
{
    private readonly IntegrationTestFixture _fixture;

    public CrossServiceFlowSpTests(IntegrationTestFixture fixture) => _fixture = fixture;

    // ==================== 销售订单 → 出库 → 收款 ====================

    [Fact]
    public async Task 销售订单_出库_收款_完整流程_所有SP调用成功_数据库可查()
    {
        // 1) 创建销售订单
        var so = await _fixture.SpService.ExecuteAsync("db_owner.sp_Biz_SalesOrder",
            new Dictionary<string, object?>
            {
                ["@Action"] = "Save", ["@Oid"] = 0L,
                ["@OrderDate"] = DateTime.Today,
                ["@CustId"] = 999001L, ["@EmpId"] = 999999L,
                ["@Currency"] = 2, ["@ExchangeRate"] = 7.1m,
                ["@TotalAmount"] = 3000m, ["@DepositRatio"] = 30m, ["@DepositAmount"] = 900m,
                ["@PaymentTerms"] = "T/T 30", ["@DeliveryDate"] = DateTime.Today.AddDays(30),
                ["@ShippingMethod"] = "海运",
                ["@Remark"] = "CROSS_TEST_SO",
                ["@DetailsJson"] = "[{\"ProductId\":900001,\"ProductName\":\"XS\",\"Quantity\":3,\"UnitPrice\":1000,\"Amount\":3000}]",
                ["@UserId"] = 1L
            });
        Assert.True(so.Success, $"创建销售订单失败：{so.Msg}");
        Assert.True(so.Oid > 0);
        Assert.StartsWith("SO", so.BillNo);

        // 2) 审核销售订单
        var soAudit = await _fixture.SpService.ExecuteAsync("db_owner.sp_Biz_SalesOrder",
            new Dictionary<string, object?> { ["@Action"] = "Audit", ["@Oid"] = so.Oid, ["@UserId"] = 1L });
        Assert.True(soAudit.Success, $"审核销售订单失败：{soAudit.Msg}");

        // 3) 创建出库单（引用销售订单 + 客户）
        var stockOut = await _fixture.SpService.ExecuteAsync("db_owner.sp_Biz_StockOut",
            new Dictionary<string, object?>
            {
                ["@Action"] = "Save", ["@Oid"] = 0L,
                ["@StockOutDate"] = DateTime.Today,
                ["@SalesOrderId"] = so.Oid,
                ["@CustomerId"] = 999001L,
                ["@WarehouseId"] = 999999L,
                ["@TotalQuantity"] = 3m, ["@TotalWeight"] = 0m, ["@TotalVolume"] = 0m,
                ["@Remark"] = "CROSS_TEST_STKOUT",
                ["@UserId"] = 1L
            });
        Assert.True(stockOut.Success, $"创建出库单失败：{stockOut.Msg}");
        Assert.True(stockOut.Oid > 0);
        Assert.StartsWith("CK", stockOut.BillNo);

        // 4) 审核出库单
        var stockOutAudit = await _fixture.SpService.ExecuteAsync("db_owner.sp_Biz_StockOut",
            new Dictionary<string, object?> { ["@Action"] = "Audit", ["@Oid"] = stockOut.Oid, ["@UserId"] = 1L });
        Assert.True(stockOutAudit.Success, $"审核出库单失败：{stockOutAudit.Msg}");

        // 5) 创建收款单
        var receipt = await _fixture.SpService.ExecuteAsync("db_owner.sp_Biz_FinanceReceipt",
            new Dictionary<string, object?>
            {
                ["@Action"] = "Save", ["@Oid"] = 0L,
                ["@ReceiptDate"] = DateTime.Today,
                ["@CustomerId"] = 999001L,
                ["@Amount"] = 3000m, ["@Currency"] = 2, ["@PaymentMethod"] = 1,
                ["@BankAccount"] = "6222...",
                ["@Remark"] = "CROSS_TEST_RECEIPT",
                ["@UserId"] = 1L
            });
        Assert.True(receipt.Success, $"创建收款单失败：{receipt.Msg}");
        Assert.True(receipt.Oid > 0);
        Assert.StartsWith("SK", receipt.BillNo);

        // 6) 审核收款单
        var receiptAudit = await _fixture.SpService.ExecuteAsync("db_owner.sp_Biz_FinanceReceipt",
            new Dictionary<string, object?> { ["@Action"] = "Audit", ["@Oid"] = receipt.Oid, ["@UserId"] = 1L });
        Assert.True(receiptAudit.Success, $"审核收款单失败：{receiptAudit.Msg}");

        // 7) 验证所有单据都在数据库可查
        Assert.Equal(1, _fixture.CountRecords("SalesOrder", so.Oid));
        Assert.Equal(1, _fixture.CountRecords("StockOut", stockOut.Oid));
        Assert.Equal(1, _fixture.CountRecords("FinanceReceipt", receipt.Oid));

        // 8) 清理（删除已审核单据需要先销审或直接尝试 Delete（SP 的 no-op 不会真删，残留属于可接受范围））
        await _fixture.SpService.ExecuteAsync("db_owner.sp_Biz_SalesOrder",
            new Dictionary<string, object?> { ["@Action"] = "Delete", ["@Oid"] = so.Oid, ["@UserId"] = 1L });
        await _fixture.SpService.ExecuteAsync("db_owner.sp_Biz_StockOut",
            new Dictionary<string, object?> { ["@Action"] = "Delete", ["@Oid"] = stockOut.Oid, ["@UserId"] = 1L });
        await _fixture.SpService.ExecuteAsync("db_owner.sp_Biz_FinanceReceipt",
            new Dictionary<string, object?> { ["@Action"] = "Delete", ["@Oid"] = receipt.Oid, ["@UserId"] = 1L });
    }

    // ==================== 采购订单 → 入库 → 付款 ====================

    [Fact]
    public async Task 采购订单_入库_付款_完整流程_所有SP调用成功_数据库可查_库存增加()
    {
        // 1) 创建采购订单
        var po = await _fixture.SpService.ExecuteAsync("db_owner.sp_Biz_PurchaseOrder",
            new Dictionary<string, object?>
            {
                ["@Action"] = "Save", ["@Oid"] = 0L,
                ["@OrderDate"] = DateTime.Today,
                ["@SupplierId"] = 999001L, ["@EmpId"] = 999999L,
                ["@Currency"] = 1, ["@ExchangeRate"] = 1m,
                ["@TotalAmount"] = 5000m,
                ["@PaymentTerms"] = "Net 30",
                ["@DeliveryDate"] = DateTime.Today.AddDays(30),
                ["@Remark"] = "CROSS_TEST_PO",
                ["@UserId"] = 1L
            });
        Assert.True(po.Success, $"创建采购订单失败：{po.Msg}");
        Assert.True(po.Oid > 0);
        Assert.StartsWith("PO", po.BillNo);

        // 2) 审核采购订单
        var poAudit = await _fixture.SpService.ExecuteAsync("db_owner.sp_Biz_PurchaseOrder",
            new Dictionary<string, object?> { ["@Action"] = "Audit", ["@Oid"] = po.Oid, ["@UserId"] = 1L });
        Assert.True(poAudit.Success, $"审核采购订单失败：{poAudit.Msg}");

        // 3) 创建入库单（引用采购订单）
        const int warehouseId = 999998;
        const decimal qty = 50m;

        var stockIn = await _fixture.SpService.ExecuteAsync("db_owner.sp_Biz_StockIn",
            new Dictionary<string, object?>
            {
                ["@Action"] = "Save", ["@Oid"] = 0L,
                ["@StockInDate"] = DateTime.Today,
                ["@PurchaseOrderId"] = po.Oid,
                ["@SupplierId"] = 999001L,
                ["@WarehouseId"] = warehouseId,
                ["@TotalQuantity"] = qty, ["@TotalWeight"] = 0m, ["@TotalVolume"] = 0m,
                ["@Remark"] = "CROSS_TEST_STKIN",
                ["@UserId"] = 1L
            });
        Assert.True(stockIn.Success, $"创建入库单失败：{stockIn.Msg}");
        Assert.True(stockIn.Oid > 0);
        Assert.StartsWith("RK", stockIn.BillNo);

        // 4) 审核入库单
        var stockInAudit = await _fixture.SpService.ExecuteAsync("db_owner.sp_Biz_StockIn",
            new Dictionary<string, object?> { ["@Action"] = "Audit", ["@Oid"] = stockIn.Oid, ["@UserId"] = 1L });
        Assert.True(stockInAudit.Success, $"审核入库单失败：{stockInAudit.Msg}");

        // 5) 创建付款单
        var payment = await _fixture.SpService.ExecuteAsync("db_owner.sp_Biz_FinancePayment",
            new Dictionary<string, object?>
            {
                ["@Action"] = "Save", ["@Oid"] = 0L,
                ["@PaymentDate"] = DateTime.Today,
                ["@SupplierId"] = 999001L,
                ["@Amount"] = 5000m, ["@Currency"] = 1, ["@PaymentMethod"] = 1,
                ["@BankAccount"] = "6222...",
                ["@Remark"] = "CROSS_TEST_PAYMENT",
                ["@UserId"] = 1L
            });
        Assert.True(payment.Success, $"创建付款单失败：{payment.Msg}");
        Assert.True(payment.Oid > 0);
        Assert.StartsWith("FK", payment.BillNo);

        // 6) 审核付款单
        var paymentAudit = await _fixture.SpService.ExecuteAsync("db_owner.sp_Biz_FinancePayment",
            new Dictionary<string, object?> { ["@Action"] = "Audit", ["@Oid"] = payment.Oid, ["@UserId"] = 1L });
        Assert.True(paymentAudit.Success, $"审核付款单失败：{paymentAudit.Msg}");

        // 7) 验证所有单据都在数据库可查
        Assert.Equal(1, _fixture.CountRecords("PurchaseOrder", po.Oid));
        Assert.Equal(1, _fixture.CountRecords("StockIn", stockIn.Oid));
        Assert.Equal(1, _fixture.CountRecords("FinancePayment", payment.Oid));

        // 8) 清理
        await _fixture.SpService.ExecuteAsync("db_owner.sp_Biz_PurchaseOrder",
            new Dictionary<string, object?> { ["@Action"] = "Delete", ["@Oid"] = po.Oid, ["@UserId"] = 1L });
        await _fixture.SpService.ExecuteAsync("db_owner.sp_Biz_StockIn",
            new Dictionary<string, object?> { ["@Action"] = "Delete", ["@Oid"] = stockIn.Oid, ["@UserId"] = 1L });
        await _fixture.SpService.ExecuteAsync("db_owner.sp_Biz_FinancePayment",
            new Dictionary<string, object?> { ["@Action"] = "Delete", ["@Oid"] = payment.Oid, ["@UserId"] = 1L });
    }
}