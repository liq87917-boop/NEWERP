namespace ERP.Domain.Enums;

/// <summary>
/// 单据通用状态：待提交 -> 已提交 -> 已审核；已驳回 / 已取消 为终止状态
/// </summary>
public enum DocumentStatus
{
    /// <summary>待提交（草稿）</summary>
    Pending = 0,

    /// <summary>已提交（待审核）</summary>
    Submitted = 1,

    /// <summary>已审核</summary>
    Approved = 2,

    /// <summary>已驳回</summary>
    Rejected = 3,

    /// <summary>已完成</summary>
    Completed = 4,

    /// <summary>已取消</summary>
    Cancelled = 5
}

/// <summary>
/// 单据类型（用于单据号规则）
/// </summary>
public enum DocumentType
{
    /// <summary>询价单</summary>
    Inquiry = 1,

    /// <summary>销售订单</summary>
    SalesOrder = 2,

    /// <summary>采购订单</summary>
    PurchaseOrder = 3,

    /// <summary>采购入库单</summary>
    StockIn = 4,

    /// <summary>销售出库单</summary>
    StockOut = 5,

    /// <summary>收货计划</summary>
    ReceivingPlan = 6,

    /// <summary>订柜信息</summary>
    ContainerBooking = 7,

    /// <summary>预装柜单</summary>
    PreLoading = 8,

    /// <summary>装柜清单</summary>
    LoadingList = 9,

    /// <summary>定金申请单</summary>
    DepositApply = 10,

    /// <summary>货款申请单</summary>
    PaymentApply = 11,

    /// <summary>付款单</summary>
    Payment = 12,

    /// <summary>装柜结算单</summary>
    ContainerSettlement = 13,

    /// <summary>散货结算单</summary>
    BulkSettlement = 14,

    /// <summary>收款单</summary>
    Receipt = 15,

    /// <summary>客诉单</summary>
    Complaint = 16,

    /// <summary>报价单</summary>
    Quotation = 17,

    /// <summary>形式发票 PI</summary>
    ProformaInvoice = 18,

    /// <summary>库存盘点/调整单（ERP-009 新增）</summary>
    StockAdjustment = 19,

    /// <summary>仓库调拨单（ERP-009 新增）</summary>
    StockTransfer = 20,

    /// <summary>销售退货单（ERP-009 新增）</summary>
    SalesReturn = 21,

    /// <summary>采购退货单（ERP-009 新增）</summary>
    PurchaseReturn = 22,

    /// <summary>
    /// 销售订单变更申请（ERP-047 新增）：只登记「拟议变更」的编号字轨，**不是**一张可审核 / 可执行的业务单据 ——
    /// 本类型不进入单据存储过程目录，申请也不改写销售订单与任何下游数据。前缀 SOC。
    /// </summary>
    SalesOrderChangeRequest = 23

}

/// <summary>
/// 库存移动类型（ERP-009）：库存流水的业务来源，与「方向」配合还原每一次库存变动
/// </summary>
public enum InventoryMovementType
{
    /// <summary>采购入库</summary>
    PurchaseIn = 1,

    /// <summary>销售出库</summary>
    SalesOut = 2,

    /// <summary>库存盘点/调整</summary>
    Adjustment = 3,

    /// <summary>调拨出库（调出仓）</summary>
    TransferOut = 4,

    /// <summary>调拨入库（调入仓）</summary>
    TransferIn = 5,

    /// <summary>销售退货入库</summary>
    SalesReturn = 6,

    /// <summary>采购退货出库</summary>
    PurchaseReturn = 7
}

/// <summary>
/// 客户端限制类型
/// </summary>
public enum ClientLimitType
{
    /// <summary>按 IP 地址限制</summary>
    IP = 1,

    /// <summary>按机器码限制</summary>
    MachineCode = 2
}

/// <summary>
/// 费用科目方向（收入/支出）
/// </summary>
public enum ExpenseDirection
{
    /// <summary>收入</summary>
    Income = 1,

    /// <summary>支出</summary>
    Expense = 2
}

/// <summary>
/// 性别
/// </summary>
public enum Gender
{
    /// <summary>男</summary>
    Male = 1,

    /// <summary>女</summary>
    Female = 2
}

/// <summary>
/// 用户状态
/// </summary>
public enum UserStatus
{
    /// <summary>启用</summary>
    Enabled = 1,

    /// <summary>禁用</summary>
    Disabled = 0
}

/// <summary>
/// 菜单类型
/// </summary>
public enum MenuType
{
    /// <summary>目录（一级菜单）</summary>
    Directory = 1,

    /// <summary>菜单（二级菜单/页面）</summary>
    Menu = 2,

    /// <summary>按钮（操作权限）</summary>
    Button = 3
}

/// <summary>
/// 柜型
/// </summary>
public enum ContainerType
{
    /// <summary>20GP 小柜</summary>
    GP20 = 20,

    /// <summary>40GP 平柜</summary>
    GP40 = 40,

    /// <summary>40HQ 高柜</summary>
    HQ40 = 41,

    /// <summary>45HQ 高柜</summary>
    HQ45 = 45,

    /// <summary>散货（拼箱 LCL）</summary>
    LCL = 0
}

/// <summary>
/// 货币单位
/// </summary>
public enum Currency
{
    /// <summary>人民币</summary>
    CNY = 1,

    /// <summary>美元</summary>
    USD = 2,

    /// <summary>欧元</summary>
    EUR = 3,

    /// <summary>港币</summary>
    HKD = 4,

    /// <summary>英镑</summary>
    GBP = 5,

    /// <summary>日元</summary>
    JPY = 6
}

/// <summary>
/// 付款方式
/// </summary>
public enum PaymentMethod
{
    /// <summary>银行转账</summary>
    BankTransfer = 1,

    /// <summary>电汇</summary>
    TelegraphicTransfer = 2,

    /// <summary>信用证</summary>
    LetterOfCredit = 3,

    /// <summary>现金</summary>
    Cash = 4,

    /// <summary>支票</summary>
    Check = 5
}
