namespace ERP.Application.DTOs;

/// <summary>
/// 动态报价成交率报表（ERP-208）的可选应用筛选 DTO：客户 Id、业务员姓名关键字与原币币种有限选择。
/// <para>本 DTO 只描述「如何在既有业务员数据范围 + 日期窗口之外再收窄报价单读取范围」，不含任何 SQL、连接串或写入语义；
/// 校验 / 规范化统一由 <see cref="Services.DynamicQuotationConversionReportRules.NormalizeFilter"/> 完成（fail closed）。</para>
/// </summary>
public sealed class QuotationConversionFilterDto
{
    /// <summary>客户 Id 筛选（可选：正整数；留空 = 不过滤；非法取值由服务端 fail closed 拒绝）</summary>
    public long? CustomerId { get; set; }

    /// <summary>业务员姓名关键字筛选（可选：去首尾空白后最多 80 字符；留空 = 不过滤；超出直接拒绝）</summary>
    public string? SalespersonName { get; set; }

    /// <summary>
    /// 原币币种筛选（可选：留空 = 全部；已知 <c>Currency</c> 枚举码或显式「未知币种」分桶；
    /// 非法取值直接拒绝，绝不回退为 CNY 或任何默认币种）。
    /// </summary>
    public string? Currency { get; set; }
}
