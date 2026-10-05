using System.Collections.Generic;

namespace ERP.Application.DTOs;

/// <summary>
/// 旧单据导出的单列白名单（有限、只读）：列键 / 中文标题与 <c>BillProcController.Export</c> 的
/// <c>ExportColumns</c> 完全一致；<see cref="Type"/> 仅取 <see cref="ReportConfigurationConstants"/> 的有限字段类型。
/// </summary>
public sealed record LegacyBillExportColumn(string Key, string Title, string Type);

/// <summary>
/// 旧单据导出族（一个 BillProc Bills 条目，共 16 族）：受控数据集的稳定元数据（表名 / 日期字段 / 菜单授权 / 有序列）。
/// <para>全部为服务端编译期常量，绝不来自客户端；表名 / 列名 / 日期字段只读且仅限本清单。</para>
/// </summary>
public sealed record LegacyBillExportFamilyDefinition(
    string FamilyKey,
    string DatasetKey,
    string TableName,
    string DateField,
    string Title,
    IReadOnlyList<string> RequiredMenuCodes,
    string RequiredMenuText,
    IReadOnlyList<LegacyBillExportColumn> Columns);

/// <summary>
/// 旧单据导出只读查询（参数化值 + 受控标识符）：绝不承载 SQL / 表名 / 列名 / 联接。
/// <para>列名只能来自对应族白名单；日期 / 状态 / 关键字均为有界参数。</para>
/// </summary>
public sealed class LegacyBillExportQuery
{
    /// <summary>旧单据导出族键（必须命中受控族目录，否则在打开查询前拒绝）。</summary>
    public string FamilyKey { get; set; } = string.Empty;

    /// <summary>请求输出的列键（有序、去重；必须全部命中族白名单；空 = 族默认全列）。</summary>
    public IReadOnlyList<string> Fields { get; set; } = new List<string>();

    /// <summary>单据号关键字（映射为 BillNo LIKE '%关键字%'）。</summary>
    public string? Keyword { get; set; }

    /// <summary>状态筛选（整数，精确匹配）。</summary>
    public int? Status { get; set; }

    /// <summary>日期范围下界（含）。</summary>
    public DateTime? StartDate { get; set; }

    /// <summary>日期范围上界（含）。</summary>
    public DateTime? EndDate { get; set; }

    /// <summary>页码（从 1 开始）。</summary>
    public int Page { get; set; } = 1;

    /// <summary>每页条数（1 ~ 200）。</summary>
    public int PageSize { get; set; } = 20;
}

/// <summary>旧单据导出只读分页结果（列顺序 + 原值行 + 命中总数）。</summary>
public sealed class LegacyBillExportPage
{
    /// <summary>本页列（与请求顺序一致，族白名单顺序）。</summary>
    public IReadOnlyList<LegacyBillExportColumn> Columns { get; set; } = new List<LegacyBillExportColumn>();

    /// <summary>本页原值行（仅含请求列；null / 原币 / 原单位原样保留）。</summary>
    public List<Dictionary<string, object?>> Rows { get; set; } = new();

    /// <summary>分页前命中总数。</summary>
    public int Total { get; set; }

    /// <summary>当前页码。</summary>
    public int Page { get; set; }

    /// <summary>每页条数。</summary>
    public int PageSize { get; set; }

    /// <summary>总页数。</summary>
    public int TotalPages { get; set; }
}
