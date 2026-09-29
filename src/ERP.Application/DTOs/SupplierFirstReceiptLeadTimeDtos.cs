using ERP.Application.Common;

namespace ERP.Application.DTOs;

/// <summary>
/// 供应商首收交期查询条件（ERP-109，全部为只读筛选参数）。
/// 只筛选「未删除且已审核」的采购订单，并按订单日期区间 / 供应商 / 关键字过滤。
/// </summary>
public sealed class SupplierFirstReceiptLeadTimeQuery
{
    /// <summary>默认每页条数</summary>
    public const int DefaultPageSize = 50;

    /// <summary>每页条数上限（有界：单次请求最多返回这么多订单）</summary>
    public const int MaxPageSize = 200;

    /// <summary>供应商筛选（留空 = 全部供应商）</summary>
    public long? SupplierId { get; set; }

    /// <summary>订单日期开始（含当天；留空 = 不限）</summary>
    public DateTime? OrderDateFrom { get; set; }

    /// <summary>订单日期结束（含当天；留空 = 不限）</summary>
    public DateTime? OrderDateTo { get; set; }

    /// <summary>关键字（匹配采购单号 / 采购合同号 / 归属销售订单号；留空 = 不过滤）</summary>
    public string? Keyword { get; set; }

    /// <summary>页码（从 1 开始）</summary>
    public int Page { get; set; } = 1;

    /// <summary>每页条数（1 ~ MaxPageSize，超出按上限截断）</summary>
    public int PageSize { get; set; } = DefaultPageSize;

    /// <summary>
    /// 归一化并校验：分页参数钳制到有界范围、关键字去除首尾空白、订单日期归一到当天；
    /// 订单日期区间倒置时直接抛业务异常（参数错误），不静默忽略筛选条件。
    /// </summary>
    public void Normalize()
    {
        if (Page < 1) Page = 1;
        if (PageSize < 1) PageSize = DefaultPageSize;
        if (PageSize > MaxPageSize) PageSize = MaxPageSize;
        Keyword = string.IsNullOrWhiteSpace(Keyword) ? null : Keyword.Trim();
        if (OrderDateFrom.HasValue) OrderDateFrom = OrderDateFrom.Value.Date;
        if (OrderDateTo.HasValue) OrderDateTo = OrderDateTo.Value.Date;
        if (OrderDateFrom.HasValue && OrderDateTo.HasValue && OrderDateFrom.Value > OrderDateTo.Value)
            throw BusinessException.InvalidParameter("订单日期区间不得倒置：开始日期不能晚于结束日期");
    }
}

/// <summary>单张采购订单的首收交期行（只读派生，不落库）</summary>
public sealed class SupplierFirstReceiptLeadTimeItem
{
    public long OrderId { get; init; }
    public string OrderNo { get; init; } = string.Empty;
    public DateTime OrderDate { get; init; }
    public long SupplierId { get; init; }
    public string SupplierName { get; init; } = string.Empty;
    public string Currency { get; init; } = string.Empty;

    /// <summary>单据状态（采购订单本身的审核状态）</summary>
    public string Status { get; init; } = string.Empty;

    /// <summary>首张有效已审核入库单号（null = 未知）</summary>
    public string? FirstReceiptNo { get; init; }

    /// <summary>首张有效已审核入库日期（null = 未知）</summary>
    public DateTime? FirstReceiptDate { get; init; }

    /// <summary>首收间隔天数 = 首收日期 − 订单日期；null = 未知；可为负（负间隔，异常，不钳制为 0）</summary>
    public int? ElapsedDays { get; init; }

    /// <summary>首收状态：received / negative_interval / unavailable</summary>
    public string LeadTimeStatus { get; init; } = string.Empty;

    /// <summary>是否存在异常证据（未审核 / 已删除 / 供应商不一致 / 负间隔等），仅标注、不影响已派生首收日期</summary>
    public bool IsAnomalous { get; init; }

    /// <summary>异常证据逐条说明（未审核 / 已删除 / 供应商不一致 / 负间隔等）</summary>
    public List<string> Anomalies { get; init; } = new();

    /// <summary>状态判定说明（未知原因 / 负间隔详情等）</summary>
    public string Note { get; init; } = string.Empty;
}

/// <summary>本页首收交期计数（合计与计数只统计本次返回页的订单）</summary>
public sealed class SupplierFirstReceiptLeadTimeCounts
{
    public int Total { get; init; }
    public int Received { get; init; }
    public int NegativeInterval { get; init; }
    public int Unavailable { get; init; }
    public int Anomalous { get; init; }
}

/// <summary>供应商首收交期报表（只读派生）</summary>
public sealed class SupplierFirstReceiptLeadTimeReport
{
    public long? SupplierId { get; init; }
    public DateTime? OrderDateFrom { get; init; }
    public DateTime? OrderDateTo { get; init; }
    public int Total { get; init; }
    public int Page { get; init; }
    public int PageSize { get; init; }
    public int TotalPages { get; init; }

    /// <summary>口径说明（界面与文档同源，由服务端在装配时填充）</summary>
    public string Rule { get; init; } = string.Empty;

    /// <summary>范围说明（界面与文档同源，由服务端在装配时填充）</summary>
    public string ScopeNote { get; init; } = string.Empty;

    /// <summary>边界说明（界面与文档同源，由服务端在装配时填充）</summary>
    public string Boundary { get; init; } = string.Empty;

    public SupplierFirstReceiptLeadTimeCounts Counts { get; init; } = new();
    public List<SupplierFirstReceiptLeadTimeItem> Items { get; init; } = new();
}
