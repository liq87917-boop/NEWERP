using System.Collections.Generic;

namespace ERP.Application.DTOs;

/// <summary>
/// 旧单据读侧（<c>api/v2/bills</c> 的查询 / 翻页导航 / 详情）行归属类型（ERP-405，显式、有限、服务端常量）。
/// </summary>
public enum LegacyBillOwnershipKind
{
    /// <summary>表头含<strong>权威客户归属列</strong>：受限账号按既有客户数据范围约束该列（列名必须已在授权表头白名单内）。</summary>
    Customer,

    /// <summary>
    /// <strong>无权威客户归属</strong>（供应商 / 业务员 / 无归属列，或旧结构未知）：受限账号一律 fail closed，
    /// 仅特权（全量数据范围）账号可读；绝不猜测 <c>旧 Oid = 规范 Id</c>、绝不按客户名匹配、绝不放开为不受限。
    /// </summary>
    None,
}

/// <summary>旧单据副表明细的有限读取定义（表名 / 外键列 / 授权列白名单）。</summary>
public sealed record LegacyBillDetailDefinition(
    string TableName,
    string ForeignKeyColumn,
    IReadOnlyList<string> Columns);

/// <summary>
/// 旧单据读侧族定义（有限、显式、服务端常量）：表名 / 既有功能菜单 / 行归属 / 授权表头列白名单 / 副表明细定义。
/// <para>表名与表头列<strong>派生自</strong> <c>LegacyBillExportCatalog</c>（ERP-308 有限受控目录），不新增第二套元数据发现；
/// <see cref="HeaderColumns"/> 以 <c>Oid</c> 打头且只含授权列，绝不 <c>SELECT *</c>。</para>
/// </summary>
public sealed record LegacyBillReadFamilyDefinition(
    string FamilyKey,
    string TableName,
    string Title,
    string ModuleMenuCode,
    string ModuleMenuText,
    LegacyBillOwnershipKind OwnershipKind,
    string? OwnershipColumn,
    IReadOnlyList<string> HeaderColumns,
    LegacyBillDetailDefinition? Detail);

/// <summary>
/// 旧单据读侧的数据范围子句（受控常量标识符 + 参数化值）：绝不承载用户 SQL / 表名 / 列名 / 联接。
/// </summary>
public sealed record LegacyBillScopeClause(string Sql, IReadOnlyList<(string Name, object? Value)> Parameters)
{
    /// <summary>特权全量数据范围：不加行约束。</summary>
    public static readonly LegacyBillScopeClause Privileged = new("1=1", Array.Empty<(string, object?)>());

    /// <summary>受限但可见客户集合为空：恒假（看不到任何行）。</summary>
    public static readonly LegacyBillScopeClause DenyAll = new("1=0", Array.Empty<(string, object?)>());
}

/// <summary>旧单据读侧查询（参数化值 + 受控标识符）：绝不承载 SQL / 表名 / 列名 / 联接。</summary>
public sealed class LegacyBillReadQuery
{
    /// <summary>单据号关键字（映射为 <c>BillNo LIKE '%关键字%'</c>，参数化）。</summary>
    public string? Keyword { get; set; }

    /// <summary>状态筛选（整数，精确匹配）。</summary>
    public int? Status { get; set; }

    /// <summary>页码（从 1 开始）。</summary>
    public int Page { get; set; } = 1;

    /// <summary>每页条数（有界 1 ~ 200）。</summary>
    public int PageSize { get; set; } = 20;
}

/// <summary>旧单据读侧分页结果（有限授权列 + 原值行 + 命中总数）；null / 原币 / 原单位原样保留。</summary>
public sealed class LegacyBillReadPage
{
    /// <summary>本页列（授权表头白名单顺序，<c>Oid</c> 打头）。</summary>
    public IReadOnlyList<string> Columns { get; set; } = new List<string>();

    /// <summary>本页原值行（仅含授权列）。</summary>
    public List<Dictionary<string, object?>> Items { get; set; } = new();

    /// <summary>范围内命中总数（分页之前）。</summary>
    public int Total { get; set; }

    /// <summary>当前页码。</summary>
    public int Page { get; set; }

    /// <summary>每页条数。</summary>
    public int PageSize { get; set; }

    /// <summary>总页数。</summary>
    public int TotalPages { get; set; }
}

/// <summary>旧单据详情（受控表头 + 关联副表明细）；仅在表头处于当前数据范围内时才会读取副表。</summary>
public sealed class LegacyBillReadDetail
{
    /// <summary>表头（仅授权列）。</summary>
    public Dictionary<string, object?> Main { get; set; } = new(StringComparer.Ordinal);

    /// <summary>副表明细（仅授权列；无登记副表的族为空集合）。</summary>
    public List<Dictionary<string, object?>> Details { get; set; } = new();
}

/// <summary>
/// 旧单据翻页导航结果：<see cref="Found"/> 为 <c>false</c> 表示当前数据范围内没有更多单据；
/// 不可访问（越权 / 不存在）的锚点与「范围内没有更多」返回同一结果，绝不泄露锚点存在性。
/// </summary>
public sealed class LegacyBillNavigateResult
{
    /// <summary>是否命中范围内单据。</summary>
    public bool Found { get; set; }

    /// <summary>命中的表头行（仅授权列）；未命中为 <c>null</c>。</summary>
    public Dictionary<string, object?>? Row { get; set; }
}
