using System.Collections.Generic;

namespace ERP.Application.DTOs;

/// <summary>
/// 迁移 parity 四维比对（ERP-329）的数据契约：有界旧路由结果快照 + 有界通用预览快照，
/// 以及由可复用比较器得出的四维证据与确定性不一致明细。
/// <para>纯只读比较：列键 / 类型 / 币种 / 单位口径、稳定行键、null 感知单元格值、币种单位分区与
/// 当前账号行归属 / 数据范围等价性；绝不触碰数据库、绝不扩权、绝不新增实体或 API。</para>
/// </summary>

/// <summary>
/// 有界比对列（有序）：稳定列键 + 声明类型 + 声明币种分区 + 声明单位分区（非货币 / 非计量列为 null）。
/// <para>列序即旧路由 / 通用平台的展示列序，比较按位置一一对应，顺序不一致即不匹配。</para>
/// </summary>
public sealed record ReportMigrationParityColumnDto(
    string Key,
    string Type,
    string? Currency,
    string? Unit);

/// <summary>
/// 有界比对行：稳定行键（有序，可复合）+ 币种分区 + 单位分区 + 与列平行、保持源顺序与 null 语义的单元格值。
/// <para><see cref="Currency"/> / <see cref="Unit"/> 为该行所属的币种 / 单位分区；null 表示该侧不分区（会跨币种 /
/// 单位合并），与另一侧出现具体分区即视为合并口径分歧（fail closed）。</para>
/// </summary>
public sealed record ReportMigrationParityRowDto(
    IReadOnlyList<string> RowKeys,
    string? Currency,
    string? Unit,
    IReadOnlyList<object?> Cells);

/// <summary>
/// 当前账号行归属 / 数据范围等价性证据（只读、有界）。
/// <para><see cref="OwnedRowIds"/> 为当前账号拥有的行身份键集合（比较按集合语义，与顺序无关）；
/// <see cref="DataScopeFingerprint"/> 为数据范围指纹（任何变更即整体拒绝）。</para>
/// </summary>
public sealed record ReportMigrationParityPermissionsDto(
    IReadOnlyList<long> OwnedRowIds,
    string DataScopeFingerprint);

/// <summary>有界结果快照：有序列 + 行 + 当前账号行归属 / 数据范围。</summary>
public sealed record ReportMigrationParitySnapshotDto(
    IReadOnlyList<ReportMigrationParityColumnDto> Columns,
    IReadOnlyList<ReportMigrationParityRowDto> Rows,
    ReportMigrationParityPermissionsDto Permissions);

/// <summary>四维比对的确定性不一致维度（有限、稳定）。</summary>
public enum ReportMigrationParityMismatchKind
{
    /// <summary>列键 / 列类型 / 列数不一致。</summary>
    Column,

    /// <summary>行数 / 稳定行键不一致。</summary>
    Row,

    /// <summary>单元格值（null 感知、按声明类型）不一致。</summary>
    Cell,

    /// <summary>币种分区（列声明或行分区）不一致。</summary>
    Currency,

    /// <summary>单位分区（列声明或行分区）不一致。</summary>
    Unit,

    /// <summary>当前账号行归属 / 数据范围等价性不一致。</summary>
    Scope,
}

/// <summary>
/// 确定性不一致明细：维度 + 列键 / 类型 / 行 / 币种 / 单位 / 范围 + 人类可读说明。
/// <para>未涉及的维度字段为 null；比较结果按「列 → 行 → 单元格 → 币种 → 单位 → 范围」稳定排序。</para>
/// </summary>
public sealed record ReportMigrationParityMismatchDto(
    ReportMigrationParityMismatchKind Kind,
    string? ColumnKey,
    string? ColumnType,
    int? RowIndex,
    string? RowKey,
    string? Currency,
    string? Unit,
    string? Scope,
    string Detail);

/// <summary>
/// 四维比对结果：<see cref="Evidence"/> 携带四维结论（<see cref="ReportMigrationParityEvidenceDto.OutputSemanticsMatched"/>
/// 由后续独立导出比对接缝提供，比较器仅透传），<see cref="Mismatches"/> 携带确定性不一致明细。
/// </summary>
public sealed record ReportMigrationParityComparisonResultDto(
    ReportMigrationParityEvidenceDto Evidence,
    IReadOnlyList<ReportMigrationParityMismatchDto> Mismatches);
