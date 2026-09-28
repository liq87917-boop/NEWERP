using ERP.Domain.Enums;

namespace ERP.Application.DTOs;

/// <summary>
/// 询价报价响应时间工作台查询参数（ERP-104，全部为可选过滤；只按显式持久化字段筛选，结果按稳定询价单 Id 分页有界）。
/// </summary>
public sealed class InquiryResponseQuery
{
    /// <summary>页码（从 1 开始）</summary>
    public int Page { get; set; } = 1;

    /// <summary>每页条数（默认 20，单次上限 200）</summary>
    public int PageSize { get; set; } = 20;

    /// <summary>询价日期区间起点（含，按日期比较）</summary>
    public DateTime? StartDate { get; set; }

    /// <summary>询价日期区间终点（含，按日期比较）</summary>
    public DateTime? EndDate { get; set; }

    /// <summary>客户 Id（可选；显式 Id 等值匹配，不按客户名文本合并记录）</summary>
    public long? CustomerId { get; set; }

    /// <summary>关键字（只命中询价单号）</summary>
    public string? Keyword { get; set; }

    /// <summary>校验并修正分页参数（与 <see cref="ERP.Application.Common.PageQuery"/> 同口径 + 本模块上限）</summary>
    public void Normalize()
    {
        if (Page < 1) Page = 1;
        if (PageSize < 1) PageSize = 20;
        if (PageSize > 200) PageSize = 200;
    }
}

/// <summary>
/// 询价报价响应时间工作台行（ERP-104，**只读**）：每张询价单 + 其首张有效报价日期 / 间隔日历天 / 响应状态。
/// <para>口径：只按报价单上持久化的 <see cref="ERP.Domain.Entities.Quotation.InquiryId"/> 显式链接（绝不按单号 / 文本 / 金额推断），
/// 只取版本链根单（<c>RootQuotationId == null</c> 的初始版本，版本不重复计入），软删除行一律排除；缺失链接 / 缺失日期 /
/// 报价日期早于询价日期分别作为独立证据标注，绝不推断为已报价。</para>
/// </summary>
public sealed record InquiryResponseRowDto(
    long InquiryId,
    string InquiryNo,
    DateTime InquiryDate,
    long CustomerId,
    string CustomerName,
    long? SalesmanId,
    DocumentStatus Status,
    string StatusText,
    bool Quoted,
    string ResponseState,
    string ResponseStateText,
    string ResponseStateLevel,
    DateTime? FirstQuotationDate,
    string QuotationNo,
    int? ElapsedDays,
    string EvidenceText);

/// <summary>
/// 询价报价响应时间工作台页（ERP-104，**只读**）：分页行 + 口径 / 边界 / 免责文案（与服务端规则同源）。
/// </summary>
public sealed record InquiryResponseWorkspaceDto(
    List<InquiryResponseRowDto> Items,
    int Total,
    int Page,
    int PageSize,
    string ReadOnlyText,
    string BoundaryText,
    string DisclaimerText);
