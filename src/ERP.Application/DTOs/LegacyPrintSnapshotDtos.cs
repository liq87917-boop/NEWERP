namespace ERP.Application.DTOs;

/// <summary>
/// 旧打印快照统一读取（ERP-334 Stage 2）的结果状态（有限、稳定）。
/// </summary>
public enum LegacyPrintSnapshotStatus
{
    /// <summary>已解析并返回有界旧打印快照。</summary>
    Success = 0,

    /// <summary>当前账号菜单 / 数据范围授权不足（fail closed，绝不返回任何数据）。</summary>
    Forbidden = 1,

    /// <summary>来源在受控打印族目录中存在，但其既有读取服务在当前环境 / 阶段不可用（显式 environment-blocked）。</summary>
    EnvironmentBlocked = 2,
}

/// <summary>
/// 旧打印快照统一读取请求（ERP-334，全部为受控参数）：只承载打印快照来源键、当前账号身份与有界读取参数，
/// 绝不承载任意 SQL / 表名 / 列名 / 联接语义。
/// </summary>
public sealed record LegacyPrintSnapshotRequest
{
    /// <summary>打印快照来源键（<c>print-template:{familyKey}</c>；空白 / 畸形 / 未知在打开任何查询前拒绝）。</summary>
    public string? SourceKey { get; set; }

    /// <summary>当前账号 Id（未认证 / 非正整数直接拒绝）。</summary>
    public long? UserId { get; set; }

    /// <summary>页码（从 1 开始；基础资料按记录分页，销售单据按表头分页）。</summary>
    public int Page { get; set; } = 1;

    /// <summary>每页条数（有界）。</summary>
    public int PageSize { get; set; } = 200;

    /// <summary>单证 Id（销售单据单张打印快照，复用 <c>GetPrint</c> 语义）。</summary>
    public long? DocumentId { get; set; }

    /// <summary>日期区间起（含当日；仅销售单据生效）。</summary>
    public DateTime? Start { get; set; }

    /// <summary>日期区间止（含当日）。</summary>
    public DateTime? End { get; set; }

    /// <summary>关键字（销售单据：单号 / 客户 / 来源单号 / 业务员）。</summary>
    public string? Keyword { get; set; }
}

/// <summary>有界旧打印快照列（有序）：稳定列键 + 声明类型 + 币种分区 + 单位分区（非货币 / 非计量列为 null）。</summary>
public sealed record LegacyPrintSnapshotColumn(string Key, string Type, string? Currency, string? Unit);

/// <summary>有界旧打印快照行：稳定行键（可复合）+ 币种分区 + 单位分区 + 与列平行的单元格值（null 原样保留）。</summary>
public sealed record LegacyPrintSnapshotRow(
    IReadOnlyList<string> RowKeys,
    string? Currency,
    string? Unit,
    IReadOnlyList<object?> Cells);

/// <summary>有界旧打印快照：有序列 + 行 + 当前账号行归属 / 数据范围。</summary>
public sealed record LegacyPrintSnapshot(
    IReadOnlyList<LegacyPrintSnapshotColumn> Columns,
    IReadOnlyList<LegacyPrintSnapshotRow> Rows,
    ReportMigrationParityPermissionsDto Permissions);

/// <summary>旧打印快照统一读取结果：有界旧打印快照或显式失败状态。</summary>
public sealed record LegacyPrintSnapshotResult(
    string? SourceKey,
    string Category,
    LegacyPrintSnapshotStatus Status,
    int? ErrorCode,
    string? ErrorMessage,
    LegacyPrintSnapshot? Snapshot);
