namespace ERP.Api.Controllers;

/// <summary>
/// 拼柜 / 整柜费用分摊请求
/// 设计目标：支持"整柜、拼柜、散货都有，分摊按体积或按重量不固定"的实际业务
///           —— 分摊基数由调用方指定，并允许完全自定义明细与权重。
/// </summary>
public class ExpenseAllocateRequest
{
    /// <summary>归属类型（整柜 / 拼柜 / 散货）</summary>
    public string RefType { get; set; } = "拼柜";

    /// <summary>归属单号（柜号 / 订舱号）</summary>
    public string RefNo { get; set; } = string.Empty;

    /// <summary>费用类型（报关费 / 拖车费 / THC …）</summary>
    public string ExpenseType { get; set; } = "报关费";

    /// <summary>待分摊的费用总额（人民币）</summary>
    public decimal TotalAmount { get; set; }

    /// <summary>币种</summary>
    public string Currency { get; set; } = "CNY";

    /// <summary>汇率</summary>
    public decimal ExchangeRate { get; set; } = 1;

    /// <summary>分摊基数：按体积 / 按重量 / 按金额 / 按箱数（手工分摊请直接逐行填分摊金额）</summary>
    public string AllocationBase { get; set; } = "按体积";

    /// <summary>费用日期（不填=今天）</summary>
    public DateTime? ExpenseDate { get; set; }

    /// <summary>收款方</summary>
    public string Payee { get; set; } = string.Empty;

    /// <summary>备注</summary>
    public string Remark { get; set; } = string.Empty;

    /// <summary>各客户明细</summary>
    public List<ExpenseAllocateDetail> Details { get; set; } = new();
}

/// <summary>分摊明细行（某客户在本柜的体积 / 重量 / 箱数 / 金额）</summary>
public class ExpenseAllocateDetail
{
    public long? CustomerId { get; set; }
    public string CustomerName { get; set; } = string.Empty;

    /// <summary>体积（m³）—— 按体积分摊时使用</summary>
    public decimal Volume { get; set; }

    /// <summary>重量（kg）—— 按重量分摊时使用</summary>
    public decimal Weight { get; set; }

    /// <summary>箱数 —— 按箱数分摊时使用</summary>
    public decimal Cartons { get; set; }

    /// <summary>金额 —— 按金额分摊时使用</summary>
    public decimal Amount { get; set; }
}

/// <summary>分摊计算结果行</summary>
public class ExpenseAllocateResultItem
{
    public long? CustomerId { get; set; }
    public string CustomerName { get; set; } = string.Empty;

    /// <summary>该行采用的权重（体积 / 重量 / 箱数 / 金额）</summary>
    public decimal WeightValue { get; set; }

    /// <summary>分摊比例（%）</summary>
    public decimal Ratio { get; set; }

    /// <summary>分摊金额（人民币）</summary>
    public decimal AllocatedAmount { get; set; }
}
