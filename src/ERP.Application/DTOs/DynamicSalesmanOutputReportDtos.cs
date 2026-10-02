using ERP.Application.Interfaces;

namespace ERP.Application.DTOs;

/// <summary>
/// 动态业务员产值证据报表（ERP-237）的只读数据传输对象：有限字段白名单目录、有界预览请求与预览结果页。
/// <para>复用既有「业务员产值报表」（salesman-output）菜单授权与 <see cref="SalespersonDataScopeService"/>（ERP-097）业务员数据范围；
/// 行口径与既有 <see cref="ReportDtos.SalesmanOutputItem"/>（ERP-236）完全一致（业务员 Id × 原始原币证据行，
/// 已分配已审核订单数 / 已知签名原币金额 / 未知利润 / 显式未知币种与来源依据）；本 DTO 只描述
/// 「选择哪些业务员产值证据字段 + 用哪个有界日期窗口与分页预览哪些业务员 × 原币行」，不含任何 SQL、连接串或写入语义。</para>
/// </summary>

/// <summary>
/// 业务员产值证据字段目录项（ERP-237，只读）：来自既有 <see cref="ReportDtos.SalesmanOutputItem"/> 的有限白名单，
/// 仅保留业务员身份 / 原币币种 / 已分配已审核订单数 / 已知签名原币金额 / 未知利润 / 显式来源依据字段。
/// </summary>
public sealed record DynamicSalesmanOutputReportFieldDto(
    string Key,
    string Label,
    string DataType,
    bool Filterable);

/// <summary>
/// 业务员产值证据预览请求（ERP-237，全部为只读筛选）：选定字段（仅限白名单，保持请求顺序）、
/// 有界日期窗口（start / end，含首尾日历日最多 366 天），以及稳定分页（页码从 1 开始，单页上限 200）。
/// </summary>
public sealed class DynamicSalesmanOutputReportRequest
{
    /// <summary>选定字段键（仅限白名单；留空 = 返回全部白名单字段，保持目录顺序；未知 / 重复 / 空键由服务端 fail closed 拒绝）</summary>
    public List<string>? Fields { get; set; }

    /// <summary>开始日期（留空 = 今天；只取日期部分）</summary>
    public DateTime? Start { get; set; }

    /// <summary>结束日期（留空 = 今天；只取日期部分；含首尾，且不得早于开始日期）</summary>
    public DateTime? End { get; set; }

    /// <summary>可选应用筛选（客户 Id / 业务员 Id / 原币币种；留空 = 不过滤）</summary>
    public SalesmanOutputFilterDto? Filter { get; set; }

    /// <summary>页码（从 1 开始）</summary>
    public int Page { get; set; } = 1;

    /// <summary>每页条数（1 ~ 200，超出直接拒绝）</summary>
    public int PageSize { get; set; } = 20;
}

/// <summary>
/// 动态业务员产值证据报表（ERP-237）的可选应用筛选 DTO：客户 Id / 业务员 Id 与原币币种有限选择。
/// <para>本 DTO 只描述「如何在既有业务员数据范围 + 日期窗口之外再收窄销售订单读取范围」，不含任何 SQL、连接串或写入语义；
/// 校验 / 规范化统一由 <see cref="Services.DynamicSalesmanOutputReportRules.NormalizeFilter"/> 完成（fail closed）。</para>
/// <para>业务员 Id 仅是订单持久化属性（非权限边界）：绝不因业务员筛选而扩展数据范围，也不会在聚合后再筛选。</para>
/// </summary>
public sealed class SalesmanOutputFilterDto
{
    /// <summary>客户 Id 筛选（可选：正整数；留空 = 不过滤；非法取值由服务端 fail closed 拒绝）</summary>
    public long? CustomerId { get; set; }

    /// <summary>业务员 Id 筛选（可选：正整数；留空 = 不过滤；非法取值由服务端 fail closed 拒绝；仅订单属性，非权限边界）</summary>
    public long? SalesmanId { get; set; }

    /// <summary>
    /// 原币币种筛选（可选：留空 = 全部；仅接受已知 <c>Currency</c> 枚举码 CNY / USD / EUR / HKD / GBP / JPY，
    /// 非法 / 数字 / 未知取值直接拒绝，绝不回退为 CNY 或任何默认币种）。不提供「未知币种」选择器。
    /// </summary>
    public string? Currency { get; set; }
}

/// <summary>
/// 业务员产值证据字段目录（ERP-237，只读）：白名单字段 + 所需菜单授权与有界额度口径（与规则同源），
/// 以及仅含筛选能力说明（不含任何业务员 / 订单 / 金额数据）的支持筛选口径。
/// </summary>
public sealed record DynamicSalesmanOutputReportCatalogDto(
    List<DynamicSalesmanOutputReportFieldDto> Fields,
    string RequiredMenuCode,
    string RequiredMenuText,
    int MaxPageSize,
    int DefaultPageSize,
    string ReadOnlyText,
    string BoundaryText,
    string FilterText);

/// <summary>
/// 业务员产值证据范围上下文（ERP-237，只读、服务端派生）：明确区分「业务员 × 原币证据行」与
/// 「去重业务员数 / 已分配已审核订单数」，并显式声明证据依据为「已分配业务员·已审核·未删除·授权客户销售订单证据」，
/// 非总 ERP 订单 / 产值 / 实际收入 / 出货 / 收款。
/// </summary>
public sealed record DynamicSalesmanOutputReportContextDto(
    string Label,
    int SalesmanCurrencyRows,
    int UniqueSalesmen,
    int AssignedApprovedOrders,
    string EvidenceBasis);

/// <summary>
/// 业务员产值证据预览结果页（ERP-237，只读）：按请求顺序返回选定列与分页行；行内仅包含选定的白名单字段值，
/// 不泄露范围外 / 未分配业务员记录。<see cref="Total"/> 为业务员 × 原币证据行总数（分页前，服务端派生），
/// <see cref="TotalPages"/> 为总页数、<see cref="Truncated"/> 表示本页之外仍有更多行，
/// <see cref="PageOnly"/> 表示本页仅覆盖当前分页行（绝不声称一次性返回全部行）；<see cref="EmptyText"/> 在空页时显式说明；
/// <see cref="Start"/> / <see cref="End"/> 为已规范化的日期上下文（供 Excel 导出标注日期口径）。
/// <see cref="CurrencyContextText"/> / <see cref="UnknownContextText"/> / <see cref="ProfitContextText"/> /
/// <see cref="SourceContextText"/> 显式声明原币 / 未知 / 未知利润 / 来源口径（即使对应列被取消选择也始终呈现）；
/// <see cref="SourceLimitText"/> 显式声明来源上限（有界读取，超出 fail closed）；<see cref="Context"/> 携带
/// 去重业务员数与已分配已审核订单数（服务端派生，绝非总 ERP 订单）；<see cref="FilterText"/> 显式声明
/// 已规范化的应用筛选上下文（即使对应列被取消选择也始终呈现，供预览 / Excel 复用）。
/// </summary>
public sealed record DynamicSalesmanOutputReportPageDto(
    List<DynamicSalesmanOutputReportFieldDto> Columns,
    List<Dictionary<string, object?>> Rows,
    int Total,
    int Page,
    int PageSize,
    int TotalPages,
    bool Truncated,
    bool PageOnly,
    string PageOnlyText,
    string EmptyText,
    string ReadOnlyText,
    string BoundaryText,
    string DisclaimerText,
    DateTime Start,
    DateTime End,
    string CurrencyContextText,
    string UnknownContextText,
    string ProfitContextText,
    string SourceContextText,
    string SourceLimitText,
    string FilterText,
    DynamicSalesmanOutputReportContextDto Context);
