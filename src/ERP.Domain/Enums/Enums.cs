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
    ProformaInvoice = 18

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
