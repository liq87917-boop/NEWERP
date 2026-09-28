namespace ERP.Application.DTOs;

/// <summary>
/// 只读出口字段完整度工作台查询参数（ERP-107，全部为只读筛选）：
/// 按商品编码 / 名称关键字与完整度分组筛选启用中的商品，结果按稳定商品 Id 分页有界，
/// 不读取图片、不改写商品 / 单证 / 报关单、不做任何外部查询。
/// </summary>
public sealed class ProductExportFieldCompletenessQuery
{
    /// <summary>商品编码 / 名称关键字（可选，模糊匹配）</summary>
    public string? Keyword { get; set; }

    /// <summary>完整度分组（可选：all / complete / incomplete / declaration / packing / dimensions / refund-rate）</summary>
    public string? Group { get; set; } = "all";

    /// <summary>页码（从 1 开始）</summary>
    public int Page { get; set; } = 1;

    /// <summary>每页条数（默认 20，单次上限 200）</summary>
    public int PageSize { get; set; } = 20;

    /// <summary>校验并修正分页参数（与 <see cref="ERP.Application.Common.PageQuery"/> 同口径 + 本模块上限）</summary>
    public void Normalize()
    {
        if (Page < 1) Page = 1;
        if (PageSize < 1) PageSize = 20;
        if (PageSize > 200) PageSize = 200;
        Keyword = string.IsNullOrWhiteSpace(Keyword) ? null : Keyword.Trim();
    }
}

/// <summary>
/// 单个出口 / 装箱字段的填写状态（ERP-107，只读）：只报告「已填写 / 空白 / 为 0 / 无效值」，
/// 绝不生成「可以报关 / 可以退税」之类的合规或税务结论。
/// </summary>
public sealed record ProductExportFieldCompletenessFieldDto(
    string Key,
    string Label,
    string State,
    string Text,
    bool Present);

/// <summary>
/// 只读出口字段完整度工作台行（ERP-107，只读）：商品身份 + 每个字段的填写 / 缺口状态。
/// </summary>
public sealed record ProductExportFieldCompletenessRowDto(
    long ProductId,
    string ProductCode,
    string ProductName,
    string Spec,
    string Unit,
    string Completeness,
    int GapCount,
    int FieldCount,
    List<ProductExportFieldCompletenessFieldDto> Fields);

/// <summary>
/// 只读出口字段完整度工作台页（ERP-107，只读）：分页行 + 只读 / 边界 / 免责文案（与服务端规则同源）。
/// </summary>
public sealed record ProductExportFieldCompletenessDto(
    List<ProductExportFieldCompletenessRowDto> Items,
    int Total,
    int Page,
    int PageSize,
    string ReadOnlyText,
    string BoundaryText,
    string DisclaimerText);
